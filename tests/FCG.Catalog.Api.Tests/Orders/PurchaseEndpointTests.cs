using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Data.Common;
using FCG.Catalog.Api.Tests.Catalog;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Catalog.Infrastructure.Messaging;
using FCG.Contracts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MassTransit;
using Npgsql;
using RabbitMQ.Client;
using Xunit;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class PurchaseEndpointTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Theory]
    [InlineData("User")]
    [InlineData("Administrator")]
    public async Task Authenticated_purchase_returns_202_after_atomic_order_and_canonical_outbox_commit(string role)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(DisableDelivery));
        var game = await Game(app); var user = Guid.NewGuid(); app.Authorize(app.Token(role, subject: user.ToString()));
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var accepted = await response.Content.ReadFromJsonAsync<JsonElement>(); var id = accepted.GetProperty("orderId").GetGuid();
        Assert.Equal(new[] { "gameId", "orderId", "price", "status" }, accepted.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(game, accepted.GetProperty("gameId").GetGuid()); Assert.Equal(59.90m, accepted.GetProperty("price").GetDecimal());
        Assert.Equal("Pending", accepted.GetProperty("status").GetString()); Assert.Equal($"/api/v1/me/orders/{id}", response.Headers.Location!.ToString());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
        Assert.Equal(user, await app.ScalarAsync<Guid>("SELECT \"UserId\" FROM \"Orders\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"OutboxMessage\""));
        var body = await app.ScalarAsync<string>("SELECT \"Body\" FROM \"OutboxMessage\""); using var envelope = JsonDocument.Parse(body);
        var message = envelope.RootElement.GetProperty("message");
        Assert.Equal(new[] { "gameId", "orderId", "placedAtUtc", "price", "userId" }, message.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(id, message.GetProperty("orderId").GetGuid()); Assert.Equal(user, message.GetProperty("userId").GetGuid());
        Assert.Equal(game, message.GetProperty("gameId").GetGuid()); Assert.Equal(59.90m, message.GetProperty("price").GetDecimal());
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", message.GetProperty("placedAtUtc").GetString()!);
        Assert.Equal(id, envelope.RootElement.GetProperty("messageId").GetGuid()); Assert.Equal(id, envelope.RootElement.GetProperty("correlationId").GetGuid());
        Assert.Equal(id, await app.ScalarAsync<Guid>("SELECT \"MessageId\" FROM \"OutboxMessage\""));
        Assert.Contains("urn:message:FCG.Contracts:OrderPlacedEvent", body);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"gameId\":null}")]
    [InlineData("{\"gameId\":\"00000000-0000-0000-0000-000000000000\"}")]
    [InlineData("{\"gameId\":\"bad-guid\"}")]
    public async Task Invalid_request_is_400_without_order(string body)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); app.Authorize(app.Token("User"));
        using var response = await app.Client.PostAsync("/api/v1/me/library", new StringContent(body, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal("validation_error", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Theory]
    [InlineData("price")]
    [InlineData("userId")]
    public async Task Client_cannot_supply_price_or_owner(string field)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var game = await Game(app); app.Authorize(app.Token("User"));
        var body = new Dictionary<string, object> { ["gameId"] = game, [field] = field == "price" ? 0m : Guid.NewGuid() };
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Anonymous_purchase_requires_authentication()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Pending_duplicate_references_original_and_approval_changes_conflict_kind()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var game = await Game(app); app.Authorize(app.Token("User"));
        using var first = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        var id = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
        using var pending = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode); var problem = await pending.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("order_already_pending", problem.GetProperty("code").GetString()); Assert.Equal(id, problem.GetProperty("orderId").GetGuid());
        Assert.Equal(first.Headers.Location, pending.Headers.Location); Assert.Equal(pending.Headers.Location!.ToString(), problem.GetProperty("location").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString())); Assert.Equal("application/problem+json", pending.Content.Headers.ContentType!.MediaType);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>(); var order = await db.Orders.SingleAsync();
            order.Complete(OrderStatus.Approved, null, order.PlacedAtUtc.AddSeconds(2)); await db.SaveChangesAsync();
        }
        using var acquired = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Conflict, acquired.StatusCode); Assert.Equal("game_already_acquired", (await acquired.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Rejected_order_permits_new_order_with_new_id_and_current_server_price()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var game = await Game(app); app.Authorize(app.Token("User"));
        var original = Order.Place(app.AdministratorId, game, 600m, DateTimeOffset.Parse("2026-10-28T15:00:00Z"));
        original.Complete(OrderStatus.Rejected, "AmountAboveLimit", original.PlacedAtUtc.AddSeconds(2));
        using (var scope = app.Services.CreateScope()) { var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>(); db.Orders.Add(original); await db.SaveChangesAsync(); }
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(original.Id, accepted.GetProperty("orderId").GetGuid()); Assert.Equal(59.90m, accepted.GetProperty("price").GetDecimal());
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Missing_or_inactive_game_is_404_even_if_pair_was_acquired()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var game = await Game(app); app.Authorize(app.Token("User"));
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>(); var order = Order.Place(app.AdministratorId, game, 1m, DateTimeOffset.Parse("2026-10-28T15:00:00Z"));
            order.Complete(OrderStatus.Approved, null, order.PlacedAtUtc); db.Orders.Add(order); (await db.Games.SingleAsync()).Deactivate(); await db.SaveChangesAsync();
        }
        using var inactive = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        using var missing = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.NotFound, inactive.StatusCode); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Concurrent_http_requests_accept_one_order_and_reference_it_in_the_conflict()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(DisableDelivery));
        var game = await Game(app); app.Authorize(app.Token("User"));
        var requests = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game })));
        try
        {
            var accepted = Assert.Single(requests, x => x.StatusCode == HttpStatusCode.Accepted);
            var id = (await accepted.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid();
            foreach (var conflict in requests.Where(x => x != accepted))
            { Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode); Assert.Equal(id, (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("orderId").GetGuid()); }
            Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\"")); Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"OutboxMessage\""));
        }
        finally { foreach (var response in requests) response.Dispose(); }
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Approved)]
    public async Task Constraint_collision_is_reclassified_from_committed_winner_in_fresh_scope(OrderStatus status)
    {
        var winner = new ExternalWinner { Status = status };
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
        { DisableDelivery(services); services.AddSingleton(winner); services.AddScoped<IOrderPlacedEvents, WinningEvents>(); }));
        var game = await Game(app); app.Authorize(app.Token("User"));
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(status == OrderStatus.Pending ? "order_already_pending" : "game_already_acquired", problem.GetProperty("code").GetString());
        if (status == OrderStatus.Pending) Assert.Equal(winner.Id, problem.GetProperty("orderId").GetGuid());
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\"")); Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Price_and_availability_change_after_selection_does_not_rewrite_snapshot()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
        { DisableDelivery(services); services.AddScoped<IOrderPlacedEvents, ChangingGameEvents>(); }));
        var game = await Game(app); app.Authorize(app.Token("User"));
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); Assert.Equal(59.90m, (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("price").GetDecimal());
        Assert.Equal(99m, await app.ScalarAsync<decimal>("SELECT \"BasePrice\" FROM \"Games\"")); Assert.False(await app.ScalarAsync<bool>("SELECT \"IsActive\" FROM \"Games\""));
        Assert.Equal(59.90m, await app.ScalarAsync<decimal>("SELECT \"Price\" FROM \"Orders\""));
    }

    [Fact]
    public async Task Failure_after_writes_before_commit_rolls_back_order_and_event_then_new_request_works()
    {
        var failure = new FailCommitOnce();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
        { DisableDelivery(services); services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(failure)); }));
        var game = await Game(app); app.Authorize(app.Token("User")); failure.Armed = true;
        using var failed = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); Assert.True(failure.SawAtomicWrites);
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\"")); Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"OutboxMessage\""));
        using var accepted = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game }); Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
        Assert.DoesNotContain("private-commit-failure", string.Join('\n', app.Logs.Entries)); Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Publisher_delivers_real_canonical_order_to_durable_subscriber()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var game = await Game(app); app.Authorize(app.Token("User"));
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync(); var queue = await Queue(channel);
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game }); var accepted = await response.Content.ReadFromJsonAsync<JsonElement>();
        BasicGetResult? received = null; await Eventually(async () => (received = await channel.BasicGetAsync(queue, autoAck: false)) is not null);
        using var envelope = JsonDocument.Parse(received!.Body); var message = envelope.RootElement.GetProperty("message");
        Assert.Equal(accepted.GetProperty("orderId").GetGuid(), message.GetProperty("orderId").GetGuid()); Assert.Equal(game, message.GetProperty("gameId").GetGuid());
        Assert.Equal(59.90m, message.GetProperty("price").GetDecimal()); Assert.Equal(app.AdministratorId, message.GetProperty("userId").GetGuid());
        await channel.BasicAckAsync(received.DeliveryTag, multiple: false);
    }

    [Fact]
    public async Task Broker_outage_keeps_202_durable_outbox_and_delivery_recovers_without_another_post()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var game = await Game(app); app.Authorize(app.Token("User"));
        await using var connection = await Connect(); await using var channel = await connection.CreateChannelAsync(); var queue = await Queue(channel);
        await postgres.Broker.PauseAsync();
        try
        {
            using var accepted = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game }).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode); Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"OutboxMessage\""));
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await app.Client.GetAsync("/health/ready")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await app.Client.GetAsync("/health/live")).StatusCode);
        }
        finally { await postgres.Broker.UnpauseAsync(); }
        BasicGetResult? received = null; await Eventually(async () => (received = await channel.BasicGetAsync(queue, autoAck: false)) is not null);
        await channel.BasicAckAsync(received!.DeliveryTag, multiple: false);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Purchase_logs_only_ids_and_trace_and_openapi_describes_acceptance_and_conflicts()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var game = await Game(app); var token = app.Token("User"); app.Authorize(token);
        using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = game });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode); var logs = string.Join('\n', app.Logs.Entries);
        Assert.Contains("OrderPlaced", logs); Assert.DoesNotContain(token, logs); Assert.DoesNotContain("catalog-app-fixture", logs);
        using var swagger = JsonDocument.Parse(await app.Client.GetStringAsync("/swagger/v1/swagger.json"));
        var post = swagger.RootElement.GetProperty("paths").GetProperty("/api/v1/me/library").GetProperty("post");
        Assert.True(post.GetProperty("responses").TryGetProperty("202", out var accepted)); Assert.True(accepted.GetProperty("headers").TryGetProperty("Location", out _));
        Assert.Equal(2, post.GetProperty("responses").GetProperty("409").GetProperty("content").GetProperty("application/problem+json").GetProperty("schema").GetProperty("oneOf").GetArrayLength());
        Assert.True(post.TryGetProperty("security", out _));
    }

    private static async Task<Guid> Game(CatalogTestApplication app)
    {
        app.Authorize(); using var response = await app.Client.PostAsJsonAsync("/api/v1/games", new { title = "Purchase", basePrice = 59.90m });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode); return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
    private static void DisableDelivery(IServiceCollection services)
    {
        var delivery = Assert.Single(services, x => x.ServiceType == typeof(IHostedService)
            && x.ImplementationType?.Name.StartsWith("BusOutboxDeliveryService", StringComparison.Ordinal) == true);
        services.Remove(delivery);
    }
    private async Task<IConnection> Connect()
    {
        var settings = postgres.BrokerSettings;
        return await new ConnectionFactory { HostName = settings["RabbitMq:Host"], Port = int.Parse(settings["RabbitMq:Port"]), VirtualHost = "/fcg",
            UserName = settings["RabbitMq:Username"], Password = settings["RabbitMq:Password"] }.CreateConnectionAsync();
    }
    private static async Task<string> Queue(IChannel channel)
    {
        var queue = "orders-" + Guid.NewGuid().ToString("N"); await channel.ExchangeDeclareAsync("FCG.Contracts:OrderPlacedEvent", "fanout", durable: true);
        await channel.QueueDeclareAsync(queue, durable: true, exclusive: false, autoDelete: false); await channel.QueueBindAsync(queue, "FCG.Contracts:OrderPlacedEvent", ""); return queue;
    }
    private static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); while (!await condition()) await Task.Delay(100, timeout.Token);
    }
    private sealed class ExternalWinner { public OrderStatus Status { get; init; } public Guid Id { get; set; } public bool Written { get; set; } }
    private sealed class WinningEvents(IPublishEndpoint publisher, CatalogDbContext db, ExternalWinner state) : IOrderPlacedEvents
    {
        public async Task StageAsync(OrderPlacedEvent message, CancellationToken cancellationToken)
        {
            if (!state.Written)
            {
                state.Written = true;
                // Deliberately non-cooperating fixture forces the unique-index fallback without taking the application's advisory lock.
                await using var external = new CatalogDbContext(new DbContextOptionsBuilder<CatalogDbContext>().UseNpgsql(db.Database.GetConnectionString()!).Options);
                var winner = Order.Place(message.UserId, message.GameId, message.Price, message.PlacedAtUtc); state.Id = winner.Id;
                if (state.Status == OrderStatus.Approved) winner.Complete(OrderStatus.Approved, null, message.PlacedAtUtc.AddSeconds(2));
                external.Orders.Add(winner); await external.SaveChangesAsync(cancellationToken);
            }
            await new OrderPlacedEvents(publisher).StageAsync(message, cancellationToken);
        }
    }
    private sealed class ChangingGameEvents(IPublishEndpoint publisher, CatalogDbContext db) : IOrderPlacedEvents
    {
        public async Task StageAsync(OrderPlacedEvent message, CancellationToken cancellationToken)
        {
            await using var connection = new NpgsqlConnection(db.Database.GetConnectionString()!); await connection.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand("UPDATE \"Games\" SET \"BasePrice\"=99, \"IsActive\"=false WHERE \"Id\"=@id", connection);
            command.Parameters.AddWithValue("id", message.GameId); await command.ExecuteNonQueryAsync(cancellationToken);
            await new OrderPlacedEvents(publisher).StageAsync(message, cancellationToken);
        }
    }
    private sealed class FailCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool SawAtomicWrites { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT (SELECT count(*) FROM \"Orders\")=1 AND (SELECT count(*) FROM \"OutboxMessage\")=1";
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!) return result;
            Armed = false; SawAtomicWrites = true; throw new InvalidOperationException("private-commit-failure");
        }
    }
}
