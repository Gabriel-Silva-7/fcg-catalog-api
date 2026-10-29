using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class PromotionPersistenceTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    private static readonly object Body = new { discountPercentage = 25m, startsAt = "2020-01-01T00:00:00Z", endsAt = "2090-01-01T00:00:00Z" };

    [Fact]
    public async Task Failed_commit_cannot_return_created_or_leave_partial_promotion()
    {
        var gate = new PromotionCommit { Throw = true };
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(gate))));
        app.Authorize(); var id = await Create(app); gate.Armed = true;
        var response = await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", Body);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); Assert.True(gate.Entered.Task.IsCompleted);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Promotions\""));
        Assert.DoesNotContain("PRIVATE-FAULT", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("PRIVATE-FAULT", string.Join('\n', app.Logs.Entries));
        Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", Body)).StatusCode);
    }

    [Fact]
    public async Task Creation_that_holds_game_lock_finishes_before_delete_and_preserves_promotion_history()
    {
        var gate = new PromotionCommit();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(gate))));
        app.Authorize(); var id = await Create(app); gate.Armed = true;
        var creation = app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", Body); Task<HttpResponseMessage>? deletion = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            deletion = app.Client.DeleteAsync("/api/v1/games/" + id); await AwaitLock(app); Assert.False(deletion.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(HttpStatusCode.Created, (await creation).StatusCode); Assert.NotNull(deletion);
        Assert.Equal(HttpStatusCode.NoContent, (await deletion).StatusCode);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Promotions\""));
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/v1/games/" + id)).StatusCode);
    }

    [Fact]
    public async Task Creator_waiting_for_deactivation_lock_observes_inactive_and_returns_404()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var id = await Create(app);
        await using var connection = new NpgsqlConnection(app.ConnectionString); await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand("UPDATE \"Games\" SET \"IsActive\"=false WHERE \"Id\"=@id", connection, transaction);
        command.Parameters.AddWithValue("id", id); await command.ExecuteNonQueryAsync();
        var request = app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", Body);
        try { await AwaitLock(app); Assert.False(request.IsCompleted); }
        finally { await transaction.CommitAsync(); }
        Assert.Equal(HttpStatusCode.NotFound, (await request).StatusCode);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Promotions\""));
    }

    [Fact]
    public async Task Storage_enforces_range_dates_own_game_fk_and_has_no_user_fk()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var id = await Create(app);
        Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", Body)).StatusCode);
        foreach (var change in new[] { "\"DiscountPercentage\"=0", "\"DiscountPercentage\"=101", "\"EndsAtUtc\"=\"StartsAtUtc\"", "\"CreatedByUserId\"='00000000-0000-0000-0000-000000000000'" })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync("UPDATE \"Promotions\" SET " + change));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        var foreign = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync($"UPDATE \"Promotions\" SET \"GameId\"='{Guid.NewGuid()}'"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, foreign.SqlState);
        Assert.Equal("Games", await app.ScalarAsync<string>("SELECT string_agg(p.relname, ',') FROM pg_constraint c JOIN pg_class p ON c.confrelid=p.oid WHERE c.conrelid='\"Promotions\"'::regclass AND c.contype='f'"));
        Assert.Equal(25m, await app.ScalarAsync<decimal>("SELECT \"DiscountPercentage\" FROM \"Promotions\""));
        Assert.Equal(6, await app.ScalarAsync<int>("SELECT datetime_precision FROM information_schema.columns WHERE table_name='Promotions' AND column_name='StartsAtUtc'"));
        var swagger = await app.Client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var post = swagger.GetProperty("paths").GetProperty("/api/v1/games/{gameId}/promotions").GetProperty("post");
        Assert.Equal(1, post.GetProperty("security").GetArrayLength());
        foreach (var status in new[] { "201", "400", "401", "403", "404" }) Assert.True(post.GetProperty("responses").TryGetProperty(status, out _));
    }

    [Fact]
    public async Task Catalog_count_rows_and_discount_share_read_only_snapshot_without_extra_game_queries()
    {
        var gate = new HoldCatalogCount();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(gate))));
        app.Authorize(); var id = await Create(app);
        await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", new { discountPercentage = 10m, startsAt = "2020-01-01T00:00:00Z", endsAt = "2090-01-01T00:00:00Z" });
        gate.Armed = true; app.Logs.Entries.Clear();
        var listing = app.Client.GetFromJsonAsync<JsonElement>("/api/v1/games");
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Create(app);
            Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", Body)).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        var snapshot = await listing;
        Assert.Equal(1, snapshot.GetProperty("totalCount").GetInt32());
        var item = Assert.Single(snapshot.GetProperty("items").EnumerateArray());
        Assert.Equal(53.91m, item.GetProperty("currentPrice").GetDecimal());
        Assert.Equal(10m, item.GetProperty("discountPercentage").GetDecimal());
        var commands = app.Logs.Entries.Where(x => x.Contains("Executed DbCommand", StringComparison.Ordinal) && x.Contains("max(", StringComparison.OrdinalIgnoreCase)
            && x.Contains("FROM \"Games\"", StringComparison.Ordinal)).ToArray();
        Assert.Single(commands);
        var current = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/games");
        Assert.Equal(2, current.GetProperty("totalCount").GetInt32());
        Assert.Contains(current.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == id && x.GetProperty("currentPrice").GetDecimal() == 44.93m);
    }

    private sealed class HoldCatalogCount : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("FROM \"Games\"", StringComparison.Ordinal))
            {
                Armed = false; Entered.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            }
            return result;
        }
    }

    private static async Task<Guid> Create(CatalogTestApplication app)
    {
        var response = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Persistent promotion", basePrice = 59.90m });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static async Task AwaitLock(CatalogTestApplication app)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await app.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock'") == 0)
            await Task.Delay(25, timeout.Token);
    }
    private sealed class PromotionCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool Throw { get; init; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"Promotions\"";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; Entered.TrySetResult();
            if (Throw) throw new InvalidOperationException("PRIVATE-FAULT promotion commit.");
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }
}
