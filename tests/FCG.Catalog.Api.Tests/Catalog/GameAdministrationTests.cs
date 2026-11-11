using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class GameAdministrationTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Administrator_updates_active_game_with_public_readable_result()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize();
        var create = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Before", basePrice = 59.90m });
        var id = (await create.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var response = await app.Client.PutAsJsonAsync("/api/v1/games/" + id, new { title = "After", description = "Changed", basePrice = 49.90m });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("After", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Delete_is_logical_repeatable_and_preserves_pending_order_and_library_history()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize();
        var order = await Orders.PaymentApplicationTests.Setup(app);
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync("/api/v1/games/" + order.GameId, new { title = "Updated history title", basePrice = 99m })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/v1/games/" + order.GameId)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/v1/games/" + order.GameId)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.GetAsync("/api/v1/games/" + order.GameId)).StatusCode);
        var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/games");
        Assert.Equal(0, list.GetProperty("totalCount").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PutAsJsonAsync("/api/v1/games/" + order.GameId, new { title = "Restore", basePrice = 1 })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = order.GameId })).StatusCode);
        await Orders.PaymentApplicationTests.Apply(app, Orders.PaymentApplicationTests.Payment(order));
        var library = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/library");
        Assert.Equal(1, library.GetProperty("totalCount").GetInt32());
        Assert.Equal(49.90m, library.GetProperty("items")[0].GetProperty("acquisitionPrice").GetDecimal());
        Assert.Equal("Updated history title", library.GetProperty("items")[0].GetProperty("title").GetString());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Games\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\" WHERE \"Status\"='Approved'"));
    }

    [Fact]
    public async Task Missing_game_returns_opaque_404_for_both_administrative_actions()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var id = Guid.NewGuid();
        foreach (var response in new[] { await app.Client.DeleteAsync("/api/v1/games/" + id),
            await app.Client.PutAsJsonAsync("/api/v1/games/" + id, new { title = "Missing", basePrice = 1 }) })
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Equal("resource_not_found", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(false, HttpStatusCode.Unauthorized)]
    [InlineData(true, HttpStatusCode.Forbidden)]
    public async Task Both_actions_enforce_administrative_access_before_mutation(bool user, HttpStatusCode expected)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Orders.PaymentApplicationTests.Setup(app);
        if (user) app.Authorize(app.Token("User"));
        Assert.Equal(expected, (await app.Client.PutAsJsonAsync("/api/v1/games/" + order.GameId, new { title = "Denied", basePrice = 1 })).StatusCode);
        Assert.Equal(expected, (await app.Client.DeleteAsync("/api/v1/games/" + order.GameId)).StatusCode);
        Assert.True(await app.ScalarAsync<bool>("SELECT \"IsActive\" FROM \"Games\""));
        Assert.Equal("Payment", await app.ScalarAsync<string>("SELECT \"Title\" FROM \"Games\""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"title\":\" \" ,\"basePrice\":1}")]
    [InlineData("{\"title\":\"Valid\" ,\"basePrice\":-1}")]
    [InlineData("{\"title\":\"Valid\" ,\"basePrice\":1.001}")]
    [InlineData("{\"title\":\"Valid\" ,\"basePrice\":10000000000000000}")]
    [InlineData("{\"title\":\"Valid\" ,\"basePrice\":null}")]
    public async Task Invalid_update_is_400_and_leaves_all_fields_unchanged(string body)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var order = await Orders.PaymentApplicationTests.Setup(app);
        using var content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        var response = await app.Client.PutAsync("/api/v1/games/" + order.GameId, content);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Payment", await app.ScalarAsync<string>("SELECT \"Title\" FROM \"Games\""));
        Assert.Equal(49.90m, await app.ScalarAsync<decimal>("SELECT \"BasePrice\" FROM \"Games\""));
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    public async Task Update_rejects_overlong_fields_before_write(string field)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var order = await Orders.PaymentApplicationTests.Setup(app);
        var response = await app.Client.PutAsJsonAsync("/api/v1/games/" + order.GameId,
            new { title = field == "title" ? new string('x', 201) : "Valid", description = field == "description" ? new string('x', 2001) : null, basePrice = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Payment", await app.ScalarAsync<string>("SELECT \"Title\" FROM \"Games\""));
    }

    [Fact]
    public void Domain_update_normalizes_preserves_creation_and_never_partially_mutates_or_reactivates()
    {
        var actor = Guid.NewGuid(); var created = DateTime.UtcNow; var game = Game.Create("Before", "Old", 59.90m, actor, created); var id = game.Id;
        game.Update(" After ", " Description ", 0m);
        Assert.Equal("After", game.Title); Assert.Equal("Description", game.Description); Assert.Equal(0m, game.BasePrice);
        Assert.Equal(id, game.Id); Assert.Equal(actor, game.CreatedByUserId); Assert.Equal(created, game.CreatedAtUtc);
        Assert.Throws<ArgumentException>(() => game.Update("Should not persist", "New", 0.001m));
        Assert.Equal("After", game.Title); Assert.Equal("Description", game.Description);
        game.Update("After", "   ", 1m); Assert.Null(game.Description);
        game.Deactivate(); game.Deactivate(); Assert.False(game.IsActive);
        Assert.Throws<InvalidOperationException>(() => game.Update("Restore", null, 2m)); Assert.False(game.IsActive);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Row_lock_serializes_put_delete_in_both_orders_without_reactivation(bool deleteFirst)
    {
        var gate = new HoldAdministrationCommit();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(gate))));
        app.Authorize(); var order = await Orders.PaymentApplicationTests.Setup(app); gate.Armed = true;
        Task<HttpResponseMessage> Update() => app.Client.PutAsJsonAsync("/api/v1/games/" + order.GameId, new { title = "Locked update", basePrice = 9m });
        Task<HttpResponseMessage> Delete() => app.Client.DeleteAsync("/api/v1/games/" + order.GameId);
        var first = deleteFirst ? Delete() : Update(); Task<HttpResponseMessage>? second = null;
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = deleteFirst ? Update() : Delete();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (await app.ScalarAsync<long>("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock'") == 0)
                await Task.Delay(25, deadline.Token);
            Assert.False(second.IsCompleted);
        }
        finally { gate.Release.TrySetResult(); }
        Assert.Equal(deleteFirst ? HttpStatusCode.NoContent : HttpStatusCode.OK, (await first).StatusCode);
        Assert.NotNull(second);
        Assert.Equal(deleteFirst ? HttpStatusCode.NotFound : HttpStatusCode.NoContent, (await second).StatusCode);
        Assert.False(await app.ScalarAsync<bool>("SELECT \"IsActive\" FROM \"Games\""));
        Assert.Equal(deleteFirst ? "Payment" : "Locked update", await app.ScalarAsync<string>("SELECT \"Title\" FROM \"Games\""));
    }

    [Fact]
    public async Task Swagger_describes_both_protected_actions_and_exact_price_for_update()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var swagger = await app.Client.GetFromJsonAsync<JsonElement>("/swagger/v1/swagger.json");
        var actions = swagger.GetProperty("paths").GetProperty("/api/v1/games/{id}");
        Assert.Equal(1, actions.GetProperty("put").GetProperty("security").GetArrayLength());
        Assert.Equal(1, actions.GetProperty("delete").GetProperty("security").GetArrayLength());
        Assert.True(actions.GetProperty("put").GetProperty("responses").TryGetProperty("404", out _));
        Assert.True(actions.GetProperty("delete").GetProperty("responses").TryGetProperty("204", out _));
        var price = swagger.GetProperty("components").GetProperty("schemas").GetProperty("UpdateGameRequest").GetProperty("properties").GetProperty("basePrice");
        Assert.Equal(0m, price.GetProperty("minimum").GetDecimal());
        Assert.Equal(9999999999999999.99m, price.GetProperty("maximum").GetDecimal());
    }

    [Fact]
    public async Task Failed_commit_returns_private_500_and_leaves_game_and_purchase_snapshot_unchanged()
    {
        var fault = new FailUpdateCommitOnce();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(fault))));
        app.Authorize(); var order = await Orders.PaymentApplicationTests.Setup(app); fault.Armed = true;
        var response = await app.Client.PutAsJsonAsync("/api/v1/games/" + order.GameId, new { title = "Commit failure", basePrice = 99m });
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode); Assert.True(fault.SawUpdate);
        Assert.DoesNotContain("SYNTHETIC-PRIVATE", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain("SYNTHETIC-PRIVATE", string.Join("\n", app.Logs.Entries));
        Assert.Equal("Payment", await app.ScalarAsync<string>("SELECT \"Title\" FROM \"Games\""));
        Assert.Equal(49.90m, await app.ScalarAsync<decimal>("SELECT \"BasePrice\" FROM \"Games\""));
        Assert.Equal(49.90m, await app.ScalarAsync<decimal>("SELECT \"Price\" FROM \"Orders\""));
        Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync("/api/v1/games/" + order.GameId, new { title = "Recovered", basePrice = 99m })).StatusCode);
    }

    private sealed class FailUpdateCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool SawUpdate { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"Games\" WHERE \"Title\"='Commit failure' AND \"BasePrice\"=99";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; SawUpdate = true; throw new InvalidOperationException("SYNTHETIC-PRIVATE update commit failure.");
        }
    }

    private sealed class HoldAdministrationCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"Games\" WHERE \"Title\"='Locked update' OR NOT \"IsActive\"";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; Entered.TrySetResult();
            await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return result;
        }
    }
}
