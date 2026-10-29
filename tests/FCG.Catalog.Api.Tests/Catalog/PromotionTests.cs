using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class PromotionTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Active_twenty_five_percent_promotion_returns_201_and_literal_44_93_price()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize();
        var response = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Promotion", basePrice = 59.90m });
        var id = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var promotion = await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", new { discountPercentage = 25m, startsAt = "2020-01-01T00:00:00Z", endsAt = "2090-01-01T00:00:00Z" });
        Assert.Equal(HttpStatusCode.Created, promotion.StatusCode);
        var game = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/games/" + id);
        Assert.Equal(44.93m, game.GetProperty("currentPrice").GetDecimal());
    }

    [Fact]
    public async Task Highest_overlap_and_boundaries_agree_in_list_detail_update_and_domain()
    {
        var start = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc); var clock = new FixedClock(start);
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IClock>(clock))));
        app.Authorize(); var id = await Game(app);
        foreach (var discount in new[] { 10m, 25m })
        {
            var response = await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", new { discountPercentage = discount, startsAt = start, endsAt = start.AddHours(1) });
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            Assert.Equal("/api/v1/games/" + id, response.Headers.Location?.AbsolutePath);
            Assert.True((await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("isCurrentlyActive").GetBoolean());
        }
        foreach (var (instant, discount, price) in new[] { (start.AddTicks(-10), 0m, 59.90m), (start, 25m, 44.93m),
            (start.AddHours(1).AddTicks(-10), 25m, 44.93m), (start.AddHours(1), 0m, 59.90m) })
        {
            clock.UtcNow = instant;
            var detail = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/games/" + id);
            var list = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/games");
            foreach (var game in new[] { detail, list.GetProperty("items")[0] })
            { Assert.Equal(discount, game.GetProperty("discountPercentage").GetDecimal()); Assert.Equal(price, game.GetProperty("currentPrice").GetDecimal()); }
        }
        clock.UtcNow = start;
        var updated = await app.Client.PutAsJsonAsync("/api/v1/games/" + id, new { title = "Updated promotion", basePrice = 59.90m });
        Assert.Equal(44.93m, (await updated.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("currentPrice").GetDecimal());
    }

    [Theory]
    [InlineData("0", "2030-01-01T00:00:00Z", "2030-01-02T00:00:00Z")]
    [InlineData("-1", "2030-01-01T00:00:00Z", "2030-01-02T00:00:00Z")]
    [InlineData("100.01", "2030-01-01T00:00:00Z", "2030-01-02T00:00:00Z")]
    [InlineData("25.001", "2030-01-01T00:00:00Z", "2030-01-02T00:00:00Z")]
    [InlineData("25.00000000000000000000000000001", "2030-01-01T00:00:00Z", "2030-01-02T00:00:00Z")]
    [InlineData("25", "2030-01-01T00:00:00Z", "2030-01-01T00:00:00Z")]
    [InlineData("25", "2030-01-02T00:00:00Z", "2030-01-01T00:00:00Z")]
    [InlineData("25", "2030-01-01T00:00:00", "2030-01-02T00:00:00Z")]
    [InlineData("25", "2030-01-01T00:00:00+03:00", "2030-01-02T00:00:00Z")]
    [InlineData("25", "2030-01-01T00:00:00.0000001Z", "2030-01-01T00:00:00.0000002Z")]
    public async Task Invalid_percentage_or_date_returns_400_without_inserting(string percentage, string start, string end)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var id = await Game(app);
        var body = "{\"discountPercentage\":" + percentage + ",\"startsAt\":\"" + start + "\",\"endsAt\":\"" + end + "\"}";
        var response = await app.Client.PostAsync($"/api/v1/games/{id}/promotions", new StringContent(body, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Promotions\""));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"discountPercentage\":25}")]
    [InlineData("{\"discountPercentage\":null,\"startsAt\":\"2030-01-01T00:00:00Z\",\"endsAt\":\"2030-01-02T00:00:00Z\"}")]
    public async Task Required_fields_are_not_defaulted(string body)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var id = await Game(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await app.Client.PostAsync($"/api/v1/games/{id}/promotions", new StringContent(body, System.Text.Encoding.UTF8, "application/json"))).StatusCode);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Promotions\""));
    }

    [Fact]
    public async Task Only_administrators_create_and_inactive_or_missing_games_are_unavailable()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(); var id = await Game(app);
        var body = new { discountPercentage = 25m, startsAt = "2020-01-01T00:00:00Z", endsAt = "2090-01-01T00:00:00Z" };
        app.Client.DefaultRequestHeaders.Authorization = null;
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", body)).StatusCode);
        app.Authorize(app.Token("User"));
        Assert.Equal(HttpStatusCode.Forbidden, (await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", body)).StatusCode);
        app.Authorize(); await app.Client.DeleteAsync("/api/v1/games/" + id);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", body)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Client.PostAsJsonAsync($"/api/v1/games/{Guid.NewGuid()}/promotions", body)).StatusCode);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Promotions\""));
    }

    [Theory]
    [InlineData("0.01", "59.89")]
    [InlineData("25", "44.93")]
    [InlineData("100", "0")]
    public void Domain_rounds_away_from_zero_and_allows_free_price(string percentage, string expected)
    {
        var actual = PricingService.Calculate(59.90m, decimal.Parse(percentage, System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), actual.CurrentPrice);
    }

    [Fact]
    public async Task Read_price_snapshot_survives_expiry_edit_and_deactivation_before_order_commit_and_payment()
    {
        var start = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc); var clock = new FixedClock(start); var gate = new PurchaseGate();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
        {
            services.Replace(ServiceDescriptor.Singleton<IClock>(clock));
            services.Replace(ServiceDescriptor.Scoped<IPendingOrderCommitter>(sp => new GatedCommitter(new PendingOrderCommitter(sp.GetRequiredService<IServiceScopeFactory>()), gate)));
        }));
        app.Authorize(); var id = await Game(app);
        Assert.Equal(HttpStatusCode.Created, (await app.Client.PostAsJsonAsync($"/api/v1/games/{id}/promotions", new { discountPercentage = 25m, startsAt = start, endsAt = start.AddHours(1) })).StatusCode);
        var request = app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = id });
        Order selected;
        try
        {
            selected = await gate.Selected.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(44.93m, selected.Price);
            clock.UtcNow = start.AddHours(1);
            Assert.Equal(HttpStatusCode.OK, (await app.Client.PutAsJsonAsync("/api/v1/games/" + id, new { title = "After snapshot", basePrice = 99m })).StatusCode);
            Assert.Equal(HttpStatusCode.NoContent, (await app.Client.DeleteAsync("/api/v1/games/" + id)).StatusCode);
        }
        finally { gate.Release.TrySetResult(); }
        var response = await request; Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(44.93m, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("price").GetDecimal());
        Assert.Equal(44.93m, await app.ScalarAsync<decimal>("SELECT \"Price\" FROM \"Orders\""));
        await Orders.PaymentApplicationTests.Apply(app, Orders.PaymentApplicationTests.Payment(selected));
        var library = await app.Client.GetFromJsonAsync<JsonElement>("/api/v1/me/library");
        Assert.Equal(44.93m, library.GetProperty("items")[0].GetProperty("acquisitionPrice").GetDecimal());
        Assert.Equal("After snapshot", library.GetProperty("items")[0].GetProperty("title").GetString());
    }

    private static async Task<Guid> Game(CatalogTestApplication app)
    {
        var response = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Promotion", basePrice = 59.90m });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private sealed class FixedClock(DateTime instant) : IClock { public DateTime UtcNow { get; set; } = instant; }
    private sealed class PurchaseGate
    {
        public TaskCompletionSource<Order> Selected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed class GatedCommitter(IPendingOrderCommitter inner, PurchaseGate gate) : IPendingOrderCommitter
    {
        public async Task<PlacementResult> CommitAsync(Order order, CancellationToken cancellationToken)
        {
            gate.Selected.TrySetResult(order); await gate.Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return await inner.CommitAsync(order, cancellationToken);
        }
    }
}
