using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Catalog.Api.Tests.Catalog;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Domain.Library;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class OwnQueriesTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Accepted_location_returns_the_callers_order()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize();
        using var created = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Query", basePrice = 49.90m });
        var game = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid(); app.Authorize(app.Token("User"));
        using var accepted = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); using var order = await app.Client.GetAsync(accepted.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, order.StatusCode); var body = await order.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Pending", body.GetProperty("status").GetString()); Assert.Equal(JsonValueKind.Null, body.GetProperty("completedAtUtc").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("rejectionReason").ValueKind);
    }

    [Theory]
    [InlineData("/api/v1/me/orders")]
    [InlineData("/api/v1/me/orders/00000000-0000-0000-0000-000000000001")]
    [InlineData("/api/v1/me/library")]
    public async Task Own_queries_require_authentication(string path)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); using var response = await app.Client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Administrator")]
    public async Task Caller_scope_is_enforced_for_detail_and_both_lists_even_with_owner_query_override(string role)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var otherUser = Guid.NewGuid();
        var own = await Seed(app, app.AdministratorId, OrderStatus.Approved, "Own");
        var other = await Seed(app, otherUser, OrderStatus.Approved, "PRIVATE-OTHER-OWNER"); app.Authorize(app.Token(role));
        using var visible = await app.Client.GetAsync($"/api/v1/me/orders/{own.Id}"); Assert.Equal(HttpStatusCode.OK, visible.StatusCode);
        using var hidden = await app.Client.GetAsync($"/api/v1/me/orders/{other.Id}");
        using var absent = await app.Client.GetAsync($"/api/v1/me/orders/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode); Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        var hiddenBody = await hidden.Content.ReadFromJsonAsync<JsonElement>(); var missingBody = await absent.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var field in new[] { "type", "title", "status", "code" }) Assert.Equal(missingBody.GetProperty(field).GetRawText(), hiddenBody.GetProperty(field).GetRawText());
        foreach (var path in new[] { "/api/v1/me/orders", "/api/v1/me/library" })
        {
            using var response = await app.Client.GetAsync(path + "?userId=" + otherUser); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var body = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(1, body.GetProperty("totalCount").GetInt32());
            var item = Assert.Single(body.GetProperty("items").EnumerateArray()); Assert.Equal(own.GameId, item.GetProperty("gameId").GetGuid());
            Assert.DoesNotContain("PRIVATE-OTHER-OWNER", body.GetRawText()); Assert.DoesNotContain(otherUser.ToString(), body.GetRawText());
        }
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("page=1000001")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    [InlineData("page=bad")]
    [InlineData("pageSize=1.5")]
    public async Task Invalid_pagination_returns_canonical_400_for_both_lists(string query)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(app.Token("User"));
        foreach (var path in new[] { "/api/v1/me/orders", "/api/v1/me/library" })
        {
            using var response = await app.Client.GetAsync(path + "?" + query); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("validation_error", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Approved)]
    [InlineData(OrderStatus.Rejected)]
    public async Task First_read_can_observe_any_valid_state_and_has_only_contract_fields(OrderStatus status)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Seed(app, app.AdministratorId, status); app.Authorize(app.Token("User"));
        var body = await app.Client.GetFromJsonAsync<JsonElement>($"/api/v1/me/orders/{order.Id}");
        Assert.Equal(new[] { "completedAtUtc", "gameId", "orderId", "placedAtUtc", "price", "rejectionReason", "status" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(status.ToString(), body.GetProperty("status").GetString()); Assert.Equal("44.93", body.GetProperty("price").GetRawText());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", body.GetProperty("placedAtUtc").GetString()!);
        if (status == OrderStatus.Pending) Assert.Equal(JsonValueKind.Null, body.GetProperty("completedAtUtc").ValueKind);
        else Assert.Equal(order.CompletedAtUtc, body.GetProperty("completedAtUtc").GetDateTimeOffset());
        Assert.Equal(status == OrderStatus.Rejected ? "AmountAboveLimit" : null, body.GetProperty("rejectionReason").GetString());
    }

    [Fact]
    public async Task Orders_pagination_is_newest_first_with_stable_id_ties_and_empty_outside_page()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var oldest = await Seed(app, app.AdministratorId, OrderStatus.Pending, "Old", Instant.AddDays(-1));
        var first = await Seed(app, app.AdministratorId, OrderStatus.Pending, "Tie1", Instant);
        var second = await Seed(app, app.AdministratorId, OrderStatus.Pending, "Tie2", Instant);
        var newest = await Seed(app, app.AdministratorId, OrderStatus.Pending, "New", Instant.AddDays(1)); app.Authorize(app.Token("User"));
        var tie = new[] { first.Id, second.Id }.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        var page1 = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/orders?pageSize=2");
        var page2 = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/orders?page=2&pageSize=2");
        Assert.Equal(new[] { newest.Id, tie[0] }, page1.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("orderId").GetGuid()).ToArray());
        Assert.Equal(new[] { tie[1], oldest.Id }, page2.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("orderId").GetGuid()).ToArray());
        var outside = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/orders?page=1000000&pageSize=100");
        Assert.Equal(4, outside.GetProperty("totalCount").GetInt32()); Assert.Empty(outside.GetProperty("items").EnumerateArray());
        var defaults = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/orders"); Assert.Equal(1, defaults.GetProperty("page").GetInt32()); Assert.Equal(20, defaults.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task Library_preserves_historical_price_inactive_game_and_original_http_shape()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Seed(app, app.AdministratorId, OrderStatus.Approved, "History");
        await app.ExecuteAsync("UPDATE \"Games\" SET \"IsActive\"=false, \"BasePrice\"=999"); app.Authorize(app.Token("User"));
        var body = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/library"); var item = Assert.Single(body.GetProperty("items").EnumerateArray());
        Assert.Equal(new[] { "acquiredAt", "acquisitionPrice", "gameId", "title" }, item.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(order.GameId, item.GetProperty("gameId").GetGuid()); Assert.Equal("History", item.GetProperty("title").GetString());
        Assert.Equal(44.93m, item.GetProperty("acquisitionPrice").GetDecimal()); Assert.Equal(order.CompletedAtUtc!.Value.UtcDateTime, item.GetProperty("acquiredAt").GetDateTime());
        using var unavailable = await app.Client.GetAsync($"/api/v1/games/{order.GameId}"); Assert.Equal(HttpStatusCode.NotFound, unavailable.StatusCode);
    }

    [Fact]
    public async Task Library_pagination_is_acquisition_descending_then_game_id_ascending()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        var old = await Seed(app, app.AdministratorId, OrderStatus.Approved, "Old", Instant.AddDays(-1));
        var first = await Seed(app, app.AdministratorId, OrderStatus.Approved, "Tie1", Instant);
        var second = await Seed(app, app.AdministratorId, OrderStatus.Approved, "Tie2", Instant); app.Authorize(app.Token("User"));
        var tied = new[] { first.GameId, second.GameId }.OrderBy(id => id.ToString(), StringComparer.Ordinal).ToArray();
        var page1 = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/library?pageSize=2");
        var page2 = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/library?page=2&pageSize=2");
        Assert.Equal(tied, page1.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("gameId").GetGuid()).ToArray());
        Assert.Equal(old.GameId, Assert.Single(page2.GetProperty("items").EnumerateArray()).GetProperty("gameId").GetGuid());
        Assert.Equal(3, page2.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task Count_and_items_share_snapshot_when_order_is_inserted_between_the_queries()
    {
        var barrier = new CountBarrier();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(barrier))));
        var first = await Seed(app, app.AdministratorId, OrderStatus.Pending); app.Authorize(app.Token("User")); barrier.Armed = true;
        var reading = app.Client.GetAsync("/api/v1/me/orders"); await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try { await Seed(app, app.AdministratorId, OrderStatus.Pending, "Concurrent", Instant.AddDays(1)); }
        finally { barrier.Resume.TrySetResult(); }
        using var response = await reading; Assert.Equal(HttpStatusCode.OK, response.StatusCode); var page = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, page.GetProperty("totalCount").GetInt32()); Assert.Equal(first.Id, Assert.Single(page.GetProperty("items").EnumerateArray()).GetProperty("orderId").GetGuid());
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Openapi_exposes_bearer_protected_read_routes_and_correct_schema()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        using var document = JsonDocument.Parse(await app.Client.GetStringAsync("/swagger/v1/swagger.json")); var paths = document.RootElement.GetProperty("paths");
        foreach (var path in new[] { "/api/v1/me/orders", "/api/v1/me/orders/{id}", "/api/v1/me/library" })
        { var get = paths.GetProperty(path).GetProperty("get"); Assert.True(get.TryGetProperty("security", out _)); Assert.True(get.GetProperty("responses").TryGetProperty("200", out _)); }
    }

    private static readonly DateTimeOffset Instant = DateTimeOffset.Parse("2026-10-28T17:00:00.123456Z");
    private static async Task<Order> Seed(CatalogTestApplication app, Guid owner, OrderStatus status, string title = "Owned", DateTimeOffset? placed = null)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var game = Game.Create(title, null, 99m, Guid.NewGuid(), Instant.UtcDateTime); var order = Order.Place(owner, game.Id, 44.93m, placed ?? Instant);
        if (status != OrderStatus.Pending) order.Complete(status, status == OrderStatus.Rejected ? "AmountAboveLimit" : null, order.PlacedAtUtc.AddSeconds(2));
        db.Games.Add(game); db.Orders.Add(order); if (status == OrderStatus.Approved) db.LibraryEntries.Add(LibraryEntry.FromApprovedOrder(order));
        await db.SaveChangesAsync(); return order;
    }
    private sealed class CountBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains(nameof(CatalogDbContext.Orders), StringComparison.Ordinal))
            { Armed = false; Reached.SetResult(); await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken); }
            return result;
        }
    }
}
