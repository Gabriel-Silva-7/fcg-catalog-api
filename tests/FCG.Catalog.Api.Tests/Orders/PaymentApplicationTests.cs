using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FCG.Catalog.Api.Tests.Catalog;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Domain.Library;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Contracts;
using MassTransit;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Npgsql;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class PaymentApplicationTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Application_migrations_provision_owned_library_storage()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres);
        Assert.True(await app.ScalarAsync<bool>("SELECT to_regclass('\"LibraryEntries\"') IS NOT NULL"));
    }

    [Theory]
    [InlineData("Approved", 1L)]
    [InlineData("Rejected", 0L)]
    public async Task Result_finalizes_order_and_approval_alone_grants_library_with_original_snapshot(string status, long acquired)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app);
        var message = Payment(order, status); var result = await Apply(app, message);
        Assert.Equal(status, result.Outcome.ToString());
        Assert.Equal(status, await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT \"Version\" FROM \"Orders\""));
        Assert.Equal(acquired, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        if (acquired == 1)
        {
            Assert.Equal(order.Id, await app.ScalarAsync<Guid>("SELECT \"OriginOrderId\" FROM \"LibraryEntries\""));
            Assert.Equal(49.90m, await app.ScalarAsync<decimal>("SELECT \"AcquisitionPrice\" FROM \"LibraryEntries\""));
            Assert.Equal(message.ProcessedAtUtc.UtcDateTime, await app.ScalarAsync<DateTime>("SELECT \"AcquiredAtUtc\" FROM \"LibraryEntries\""));
        }
    }

    [Fact]
    public async Task Duplicate_results_from_new_contexts_keep_one_acquisition()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app); var payment = Payment(order);
        await Apply(app, payment);
        Assert.Equal(PaymentApplicationOutcome.Duplicate, (await Apply(app, payment)).Outcome);
        Assert.Equal(PaymentApplicationOutcome.Duplicate, (await Apply(app, payment)).Outcome);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(2L, await app.ScalarAsync<long>("SELECT \"Version\" FROM \"Orders\""));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("game")]
    [InlineData("price")]
    public async Task Divergent_purchase_snapshot_preserves_pending_order_without_grant(string field)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app); var original = Payment(order);
        var changed = new PaymentProcessedEvent(original.OrderId, field == "user" ? Guid.NewGuid() : original.UserId,
            field == "game" ? Guid.NewGuid() : original.GameId, field == "price" ? 70m : original.Price, "Approved", null, original.ProcessedAtUtc);
        Assert.Equal("PaymentDoesNotMatchOrder", (await Apply(app, changed)).Reason); await Apply(app, changed);
        Assert.Equal("Pending", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(2, await app.ScalarAsync<int>("SELECT \"Observations\" FROM \"MessageConflicts\""));
    }

    [Theory]
    [InlineData("Approved", "Rejected")]
    [InlineData("Rejected", "Approved")]
    public async Task Terminal_conflict_never_overwrites_first_result_or_compensates_acquisition(string first, string next)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app);
        await Apply(app, Payment(order, first));
        Assert.Equal("DivergentPaymentResult", (await Apply(app, Payment(order, next))).Reason);
        Assert.Equal(first, await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(first == "Approved" ? 1L : 0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
    }

    [Fact]
    public async Task Same_approved_with_another_processing_date_is_a_conflict()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app); var original = Payment(order); await Apply(app, original);
        var changed = new PaymentProcessedEvent(order.Id, order.UserId, order.GameId, order.Price, "Approved", null, original.ProcessedAtUtc.AddSeconds(1));
        Assert.Equal(PaymentApplicationOutcome.Quarantined, (await Apply(app, changed)).Outcome);
        Assert.Equal(original.ProcessedAtUtc.UtcDateTime, await app.ScalarAsync<DateTime>("SELECT \"AcquiredAtUtc\" FROM \"LibraryEntries\""));
    }

    [Fact]
    public async Task Concurrent_approved_and_rejected_leave_one_terminal_with_consistent_library()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<PaymentApplicationResult> Run(string status) { await gate.Task; return await Apply(app, Payment(order, status)); }
        var approved = Run("Approved"); var rejected = Run("Rejected"); gate.SetResult(); var results = await Task.WhenAll(approved, rejected);
        Assert.Single(results, x => x.Outcome == PaymentApplicationOutcome.Quarantined);
        var saved = await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\"");
        Assert.Contains(saved, new[] { "Approved", "Rejected" });
        Assert.Equal(saved == "Approved" ? 1L : 0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
    }

    [Fact]
    public async Task Accepted_purchase_finishes_after_game_deactivation_without_recalculating_price()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app);
        await app.ExecuteAsync("UPDATE \"Games\" SET \"BasePrice\"=99, \"IsActive\"=false");
        Assert.Equal(PaymentApplicationOutcome.Approved, (await Apply(app, Payment(order))).Outcome);
        Assert.Equal(49.90m, await app.ScalarAsync<decimal>("SELECT \"AcquisitionPrice\" FROM \"LibraryEntries\""));
    }

    [Fact]
    public async Task Failure_after_order_and_library_writes_rolls_back_both_then_redelivery_succeeds()
    {
        var failure = new FailCommitOnce();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(failure))));
        var order = await Setup(app); failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(app, Payment(order))); Assert.True(failure.SawBothWrites);
        Assert.Equal("Pending", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        await Apply(app, Payment(order)); Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
    }

    [Fact]
    public async Task Historical_acquisition_without_order_blocks_new_http_purchase()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); Guid id;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>(); var game = NewGame(app.AdministratorId); id = game.Id;
            db.Games.Add(game); db.LibraryEntries.Add(LibraryEntry.Create(app.AdministratorId, game.Id, Placed.UtcDateTime, 20m)); await db.SaveChangesAsync();
        }
        app.Authorize(app.Token("User")); using var response = await app.Client.PostAsJsonAsync("/api/v1/me/library", new { gameId = id });
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("game_already_acquired", (await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
    }

    [Fact]
    public async Task Consumer_applies_real_payment_transport_before_ack()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var order = await Setup(app);
        await app.Services.GetRequiredService<IBus>().Publish(Payment(order));
        await Eventually(async () => await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\"") == 1);
        Assert.Equal("Approved", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
    }

    [Fact]
    public async Task Actual_database_connection_loss_during_commit_rolls_back_and_logs_no_stack()
    {
        var failure = new TerminateCommitConnection(postgres.ConnectionString);
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(failure))));
        var order = await Setup(app); failure.Armed = true;
        await Assert.ThrowsAnyAsync<Exception>(() => Apply(app, Payment(order)));
        Assert.True(failure.Terminated);
        Assert.Equal("Pending", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
        await Apply(app, Payment(order));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
    }

    private sealed class TerminateCommitConnection(string adminConnection) : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool Terminated { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var query = transaction.Connection!.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT count(*) FROM \"LibraryEntries\"";
            if ((long)(await query.ExecuteScalarAsync(cancellationToken))! != 1L) return result;
            Armed = false;
            var processId = ((NpgsqlConnection)transaction.Connection).ProcessID;
            await using var admin = new NpgsqlConnection(adminConnection); await admin.OpenAsync(cancellationToken);
            await using var terminate = new NpgsqlCommand("SELECT pg_terminate_backend(@pid)", admin); terminate.Parameters.AddWithValue("pid", processId);
            Terminated = (bool)(await terminate.ExecuteScalarAsync(cancellationToken))!;
            return result;
        }
    }

    internal static readonly DateTimeOffset Placed = DateTimeOffset.Parse("2026-10-28T17:00:00.123456Z");
    internal static Game NewGame(Guid actor) => Game.Create("Payment", null, 49.90m, actor, Placed.UtcDateTime);
    internal static async Task<Order> Setup(CatalogTestApplication app)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>(); var game = NewGame(app.AdministratorId);
        var order = Order.Place(app.AdministratorId, game.Id, 49.90m, Placed); db.Games.Add(game); db.Orders.Add(order); await db.SaveChangesAsync(); return order;
    }
    internal static PaymentProcessedEvent Payment(Order order, string status = "Approved") =>
        new(order.Id, order.UserId, order.GameId, order.Price, status, status == "Rejected" ? "AmountAboveLimit" : null, order.PlacedAtUtc.AddSeconds(2));
    internal static async Task<PaymentApplicationResult> Apply(CatalogTestApplication app, PaymentProcessedEvent message)
    {
        using var scope = app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<PaymentApplication>().HandleAsync(message, Guid.NewGuid(), CancellationToken.None);
    }
    internal static async Task<PaymentApplicationResult> Replay(CatalogTestApplication app, Guid id)
    {
        using var scope = app.Services.CreateScope(); return await scope.ServiceProvider.GetRequiredService<PaymentApplication>().ReplayAsync(id, CancellationToken.None);
    }
    internal static async Task Eventually(Func<Task<bool>> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); while (!await condition()) await Task.Delay(100, timeout.Token);
    }
    private sealed class FailCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool SawBothWrites { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT (SELECT count(*) FROM \"Orders\" WHERE \"Status\"='Approved')=1 AND (SELECT count(*) FROM \"LibraryEntries\")=1";
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!) return result;
            Armed = false; SawBothWrites = true; throw new InvalidOperationException("Synthetic payment commit failure.");
        }
    }
}
