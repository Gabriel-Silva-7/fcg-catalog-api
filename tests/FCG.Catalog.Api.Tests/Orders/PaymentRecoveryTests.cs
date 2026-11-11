using System.Data.Common;
using System.Text.Json;
using FCG.Catalog.Api.Tests.Catalog;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Contracts;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FCG.Catalog.Api.Tests.Orders.PaymentApplicationTests;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class PaymentRecoveryTests(CatalogPostgres postgres) : IClassFixture<CatalogPostgres>
{
    [Fact]
    public async Task Unknown_order_is_durable_then_local_replay_resolves_atomically_after_authorized_restore()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var message = await Unknown(app);
        Assert.Equal("OrderNotFound", (await Apply(app, message)).Reason); await Apply(app, message);
        var id = await app.ScalarAsync<Guid>("SELECT \"Id\" FROM \"MessageConflicts\"");
        var payload = await app.ScalarAsync<string>("SELECT \"Payload\" FROM \"MessageConflicts\"");
        var hash = await app.ScalarAsync<string>("SELECT \"PayloadHash\" FROM \"MessageConflicts\"");
        Assert.Equal(message, JsonSerializer.Deserialize<PaymentProcessedEvent>(payload));
        Assert.Equal("PaymentProcessedEvent", await app.ScalarAsync<string>("SELECT \"MessageType\" FROM \"MessageConflicts\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
        Assert.Equal("OrderNotFound", (await Replay(app, id)).Reason);
        Assert.False(await app.ScalarAsync<bool>("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
        await RestoreAcceptedOrder(app, message);
        Assert.Equal(PaymentApplicationOutcome.Approved, (await Replay(app, id)).Outcome);
        Assert.True(await app.ScalarAsync<bool>("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(PaymentApplicationOutcome.AlreadyResolved, (await Replay(app, id)).Outcome);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(3L, await app.ScalarAsync<long>("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
        Assert.Equal(payload, await app.ScalarAsync<string>("SELECT \"Payload\" FROM \"MessageConflicts\""));
        Assert.Equal(hash, await app.ScalarAsync<string>("SELECT \"PayloadHash\" FROM \"MessageConflicts\""));
    }

    [Fact]
    public async Task Replay_cannot_replace_conflicting_terminal_even_after_order_restore()
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var message = await Unknown(app); await Apply(app, message);
        var id = await app.ScalarAsync<Guid>("SELECT \"Id\" FROM \"MessageConflicts\""); await RestoreAcceptedOrder(app, message);
        await Apply(app, new PaymentProcessedEvent(message.OrderId, message.UserId, message.GameId, message.Price, "Rejected", "AmountAboveLimit", message.ProcessedAtUtc));
        Assert.Equal("DivergentPaymentResult", (await Replay(app, id)).Reason);
        Assert.Equal("Rejected", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.False(await app.ScalarAsync<bool>("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
    }

    [Fact]
    public async Task Replay_resolution_history_and_acquisition_roll_back_together_if_commit_fails()
    {
        var failure = new FailReplayCommitOnce();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(failure))));
        var message = await Unknown(app); await Apply(app, message);
        var id = await app.ScalarAsync<Guid>("SELECT \"Id\" FROM \"MessageConflicts\""); await RestoreAcceptedOrder(app, message); failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Replay(app, id)); Assert.True(failure.SawAtomicResolution);
        Assert.Equal("Pending", await app.ScalarAsync<string>("SELECT \"Status\" FROM \"Orders\""));
        Assert.False(await app.ScalarAsync<bool>("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"PaymentRecoveryAttempts\""));
        Assert.Equal(PaymentApplicationOutcome.Approved, (await Replay(app, id)).Outcome);
        Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
    }

    [Fact]
    public async Task Diagnostic_write_failure_cannot_ack_or_leave_an_in_memory_only_conflict()
    {
        var failure = new FailDiagnosticOnce();
        await using var app = await CatalogTestApplication.StartAsync(postgres, builder => builder.ConfigureTestServices(services =>
            services.AddDbContext<CatalogDbContext>(options => options.AddInterceptors(failure))));
        var message = await Unknown(app); failure.Armed = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Apply(app, message));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
        await Apply(app, message); Assert.Equal(1L, await app.ScalarAsync<long>("SELECT count(*) FROM \"MessageConflicts\""));
        Assert.DoesNotContain("private-diagnostic-failure", string.Join('\n', app.Logs.Entries));
    }

    [Theory]
    [InlineData("type", "UnsupportedMessageType")]
    [InlineData("payload", "InvalidStoredPayload")]
    public async Task Recovery_refuses_unknown_type_or_edited_payload_without_guessing(string change, string expected)
    {
        await using var app = await CatalogTestApplication.StartAsync(postgres); var message = await Unknown(app); await Apply(app, message);
        var id = await app.ScalarAsync<Guid>("SELECT \"Id\" FROM \"MessageConflicts\"");
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>(); var conflict = await db.MessageConflicts.SingleAsync();
            if (change == "type") conflict.MessageType = "UserCreatedEvent";
            else conflict.Payload = JsonSerializer.Serialize(new PaymentProcessedEvent(message.OrderId, message.UserId, message.GameId, 70m, "Approved", null, message.ProcessedAtUtc));
            await db.SaveChangesAsync();
        }
        Assert.Equal(expected, (await Replay(app, id)).Reason);
        Assert.False(await app.ScalarAsync<bool>("SELECT \"Resolved\" FROM \"MessageConflicts\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"Orders\""));
        Assert.Equal(0L, await app.ScalarAsync<long>("SELECT count(*) FROM \"LibraryEntries\""));
    }

    private static async Task<PaymentProcessedEvent> Unknown(CatalogTestApplication app)
    {
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        var game = NewGame(app.AdministratorId); db.Games.Add(game); await db.SaveChangesAsync();
        return new(Guid.NewGuid(), app.AdministratorId, game.Id, 49.90m, "Approved", null, Placed.AddSeconds(2));
    }
    private static async Task RestoreAcceptedOrder(CatalogTestApplication app, PaymentProcessedEvent message)
    {
        // Fixture of an owner-authorized restore; the consumer/replay operation never inserts an Order.
        using var scope = app.Services.CreateScope(); var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO \"Orders\" (\"Id\",\"UserId\",\"GameId\",\"Price\",\"Status\",\"PlacedAtUtc\",\"Version\") VALUES ({message.OrderId},{message.UserId},{message.GameId},{message.Price},'Pending',{message.ProcessedAtUtc.AddSeconds(-2)},1)");
    }
    private sealed class FailDiagnosticOnce : SaveChangesInterceptor
    {
        public bool Armed { get; set; }
        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Armed && eventData.Context!.ChangeTracker.Entries<MessageConflict>().Any(x => x.State == EntityState.Added))
            { Armed = false; throw new InvalidOperationException("private-diagnostic-failure"); }
            return ValueTask.FromResult(result);
        }
    }
    private sealed class FailReplayCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool SawAtomicResolution { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            await using var query = transaction.Connection!.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT (SELECT count(*) FROM \"LibraryEntries\")=1 AND (SELECT count(*) FROM \"MessageConflicts\" WHERE \"Resolved\")=1 AND (SELECT count(*) FROM \"PaymentRecoveryAttempts\")=1";
            if (!(bool)(await query.ExecuteScalarAsync(cancellationToken))!) return result;
            Armed = false; SawAtomicResolution = true; throw new InvalidOperationException("Synthetic replay commit failure.");
        }
    }
}
