using System.Net;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class CatalogStorageTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Migration_is_repeatable_with_own_role_and_preserves_catalog_without_users_tables()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        app.Authorize();
        using var created = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Persistent", basePrice = 7.15m });
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        for (var pass = 0; pass < 2; pass++)
        {
            await using var scope = app.Services.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.MigrateAsync();
            var persisted = await db.Games.AsNoTracking().SingleAsync(game => game.Id == id);
            Assert.Equal(7.15m, persisted.BasePrice);
        }
        Assert.Equal("Games,InboxState,Orders,OutboxMessage,OutboxState,__EFMigrationsHistory", await app.ScalarAsync<string>("SELECT string_agg(table_name, ',' ORDER BY table_name) FROM information_schema.tables WHERE table_schema='public' AND table_type='BASE TABLE'"));
        Assert.Equal(3L, await app.ScalarAsync<long>("SELECT count(*) FROM \"__EFMigrationsHistory\""));
        Assert.False(await app.ScalarAsync<bool>("SELECT rolsuper OR rolcreatedb OR rolcreaterole OR rolreplication OR rolbypassrls FROM pg_roles WHERE rolname=current_user"));
        Assert.True(await app.ScalarAsync<bool>("SELECT datdba=(SELECT oid FROM pg_roles WHERE rolname=current_user) FROM pg_database WHERE datname=current_database()"));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM information_schema.table_constraints WHERE table_schema='public' AND constraint_type='FOREIGN KEY' AND table_name NOT IN ('OutboxMessage')"));
        Assert.Equal("18,2", await app.ScalarAsync<string>("SELECT numeric_precision::text || ',' || numeric_scale::text FROM information_schema.columns WHERE table_name='Games' AND column_name='BasePrice'"));
        var error = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync($"UPDATE \"Games\" SET \"BasePrice\"=-0.01 WHERE \"Id\"='{id}'"));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal(7.15m, await app.ScalarAsync<decimal>($"SELECT \"BasePrice\" FROM \"Games\" WHERE \"Id\"='{id}'"));
    }

    [Fact]
    public async Task Readiness_detects_database_outage_and_recovers_while_liveness_stays_available()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        using var ready = await app.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        await postgres.PauseAsync();
        try
        {
            using var live = await app.Client.GetAsync("/health/live");
            using var failed = await app.Client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Equal("Unhealthy", await failed.Content.ReadAsStringAsync());
        }
        finally { await postgres.UnpauseAsync(); }
        Assert.Equal(new NpgsqlConnectionStringBuilder(app.ConnectionString).Port,
            new NpgsqlConnectionStringBuilder(postgres.ConnectionString).Port);
        // Pause preserves the published endpoint; stop/start can reassign an ephemeral
        // Docker host port. The image smoke separately proves restart via stable DNS.
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            using var recovered = await app.Client.GetAsync("/health/ready");
            if (recovered.StatusCode == HttpStatusCode.OK) break;
            Assert.True(deadline.Elapsed < TimeSpan.FromSeconds(15), "Catalog readiness did not recover after PostgreSQL resumed.");
            await Task.Delay(100);
        }
    }

    [Fact]
    public async Task Readiness_rejects_schema_missing_a_required_game_column()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        using var healthy = await app.Client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, healthy.StatusCode);
        await app.ExecuteAsync("ALTER TABLE \"Games\" RENAME COLUMN \"BasePrice\" TO \"UnavailablePrice\"");
        using var ready = await app.Client.GetAsync("/health/ready");
        using var live = await app.Client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }

    [Fact]
    public async Task Creation_logs_ids_and_trace_without_token_or_request_payload()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var token = app.Token(); app.Authorize(token);
        using var response = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "TITLE-THAT-MUST-NOT-BE-LOGGED", description = "DESCRIPTION-THAT-MUST-NOT-BE-LOGGED", basePrice = 1m });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var audit = Assert.Single(app.Logs.Entries, line => line.Contains("GameCreated", StringComparison.Ordinal));
        Assert.Contains(app.AdministratorId.ToString(), audit);
        Assert.Contains(id.ToString(), audit);
        Assert.Matches(@"00-[a-f0-9]{32}-[a-f0-9]{16}-[a-f0-9]{2}", audit);
        var logs = string.Join("\n", app.Logs.Entries);
        Assert.DoesNotContain(token, logs);
        Assert.DoesNotContain("TITLE-THAT-MUST-NOT-BE-LOGGED", logs);
        Assert.DoesNotContain("DESCRIPTION-THAT-MUST-NOT-BE-LOGGED", logs);
        Assert.DoesNotContain("catalog-app-fixture", logs);
    }

    [Fact]
    public async Task Unexpected_failure_returns_canonical_problem_without_private_exception_details()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<IGameRepository>();
            services.AddScoped<IGameRepository, FailingRepository>();
        }));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/games");
        request.Headers.Accept.ParseAdd("text/plain");
        using var response = await app.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var body = await response.Content.ReadAsStringAsync();
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("internal_error", json.GetProperty("code").GetString());
        var trace = json.GetProperty("traceId").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(trace));
        Assert.DoesNotContain(FailingRepository.PrivateMessage, body);
        var logs = string.Join("\n", app.Logs.Entries);
        Assert.DoesNotContain(FailingRepository.PrivateMessage, logs);
        Assert.Contains("UnhandledException " + trace, logs);
    }

    private sealed class FailingRepository : IGameRepository
    {
        internal const string PrivateMessage = "PRIVATE-PASSWORD-AND-CONNECTION-DETAILS";
        public void Add(Game game) => throw new InvalidOperationException(PrivateMessage);
        public Task SaveChangesAsync(CancellationToken ct) => throw new InvalidOperationException(PrivateMessage);
        public Task<GameReadModel?> FindActiveByIdAsync(Guid id, CancellationToken ct) => throw new InvalidOperationException(PrivateMessage);
        public Task<PagedResult<GameReadModel>> SearchActiveAsync(string? search, GameSortField sortBy, int page, int pageSize, CancellationToken ct) => throw new InvalidOperationException(PrivateMessage);
    }
}
