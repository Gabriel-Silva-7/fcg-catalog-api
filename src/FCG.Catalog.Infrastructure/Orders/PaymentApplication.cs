using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Library;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FCG.Catalog.Infrastructure.Orders;

public sealed class PaymentApplication(CatalogDbContext db, TimeProvider clock, ILogger<PaymentApplication> logger)
{
    public async Task<PaymentApplicationResult> HandleAsync(PaymentProcessedEvent incoming, Guid? messageId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(incoming); RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync("order:" + incoming.OrderId, cancellationToken);
        var result = await ApplyCoreAsync(incoming, cancellationToken);
        if (result.Outcome == PaymentApplicationOutcome.Quarantined)
            await RecordConflictAsync(incoming, messageId, result.Reason!, cancellationToken);
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // Local application operation; callers must not wrap it in a second transaction or edit the stored payload.
    public async Task<PaymentApplicationResult> ReplayAsync(Guid conflictId, CancellationToken cancellationToken)
    {
        RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await LockAsync("replay:" + conflictId, cancellationToken);
        var conflict = await db.MessageConflicts.SingleOrDefaultAsync(x => x.Id == conflictId, cancellationToken);
        if (conflict is null) return new(PaymentApplicationOutcome.NotFound);
        PaymentApplicationResult result;
        if (conflict.Kind != "Payment" || conflict.MessageType != nameof(PaymentProcessedEvent))
            result = new(PaymentApplicationOutcome.Quarantined, "UnsupportedMessageType");
        else if (conflict.Resolved) result = new(PaymentApplicationOutcome.AlreadyResolved);
        else
        {
            var incoming = ReadStoredPayment(conflict);
            if (incoming is null)
                result = new(PaymentApplicationOutcome.Quarantined, "InvalidStoredPayload");
            else
            {
                await LockAsync("order:" + incoming.OrderId, cancellationToken);
                result = await ApplyCoreAsync(incoming, cancellationToken);
                if (result.Outcome is PaymentApplicationOutcome.Approved or PaymentApplicationOutcome.Rejected or PaymentApplicationOutcome.Duplicate)
                { conflict.Resolved = true; conflict.ResolvedAtUtc = clock.GetUtcNow(); }
            }
        }
        db.PaymentRecoveryAttempts.Add(new PaymentRecoveryAttempt { Id = Guid.NewGuid(), ConflictId = conflictId,
            AttemptedAtUtc = clock.GetUtcNow(), Outcome = result.Outcome.ToString(), Reason = result.Reason });
        await db.SaveChangesAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
        return result;
    }

    // A point-in-time preview: no locks, counters, resolution, attempts or domain mutations.
    public async Task<PaymentApplicationResult> PreviewReplayAsync(Guid conflictId, CancellationToken cancellationToken)
    {
        RequireOwnTransaction(); db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var conflict = await db.MessageConflicts.AsNoTracking().SingleOrDefaultAsync(x => x.Id == conflictId, cancellationToken);
        if (conflict is null) return new(PaymentApplicationOutcome.NotFound);
        if (conflict.Kind != "Payment" || conflict.MessageType != nameof(PaymentProcessedEvent))
            return new(PaymentApplicationOutcome.Quarantined, "UnsupportedMessageType");
        if (conflict.Resolved) return new(PaymentApplicationOutcome.AlreadyResolved);
        var incoming = ReadStoredPayment(conflict);
        return incoming is null ? new(PaymentApplicationOutcome.Quarantined, "InvalidStoredPayload")
            : await ApplyCoreAsync(incoming, cancellationToken, apply: false);
    }

    private static PaymentProcessedEvent? ReadStoredPayment(MessageConflict conflict)
    {
        PaymentProcessedEvent? incoming;
        try { incoming = JsonSerializer.Deserialize<PaymentProcessedEvent>(conflict.Payload); }
        catch (Exception error) when (error is JsonException or ArgumentException or NotSupportedException) { return null; }
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(conflict.Payload)));
        return incoming is not null && incoming.OrderId == conflict.BusinessId && hash == conflict.PayloadHash ? incoming : null;
    }

    private async Task<PaymentApplicationResult> ApplyCoreAsync(PaymentProcessedEvent incoming, CancellationToken cancellationToken, bool apply = true)
    {
        var order = await db.Orders.SingleOrDefaultAsync(x => x.Id == incoming.OrderId, cancellationToken);
        var decision = PaymentApplicationPolicy.Decide(order, incoming);
        if (decision.Outcome == PaymentApplicationOutcome.Quarantined) return decision;
        if (decision.Outcome == PaymentApplicationOutcome.Duplicate)
        {
            if (order!.Status == OrderStatus.Approved)
            {
                var acquired = await db.LibraryEntries.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == order.UserId && x.GameId == order.GameId, cancellationToken);
                if (acquired is null || acquired.OriginOrderId != order.Id || acquired.AcquisitionPrice != order.Price
                    || acquired.AcquiredAtUtc != order.CompletedAtUtc!.Value.UtcDateTime)
                    return new(PaymentApplicationOutcome.Quarantined, "AcquisitionDoesNotMatchOrder");
            }
            return decision;
        }
        if (decision.Outcome == PaymentApplicationOutcome.Approved)
        {
            if (!await db.Games.AsNoTracking().AnyAsync(x => x.Id == order!.GameId, cancellationToken))
                return new(PaymentApplicationOutcome.Quarantined, "OrderGameNotFound");
            if (await db.LibraryEntries.AsNoTracking().AnyAsync(x => x.UserId == order!.UserId && x.GameId == order.GameId, cancellationToken))
                return new(PaymentApplicationOutcome.Quarantined, "ExistingAcquisitionConflict");
        }
        if (!apply) return decision;
        order!.Complete(decision.Outcome == PaymentApplicationOutcome.Approved ? OrderStatus.Approved : OrderStatus.Rejected, incoming.Reason, incoming.ProcessedAtUtc);
        if (decision.Outcome == PaymentApplicationOutcome.Approved) db.LibraryEntries.Add(LibraryEntry.FromApprovedOrder(order));
        return decision;
    }

    private async Task RecordConflictAsync(PaymentProcessedEvent incoming, Guid? messageId, string reason, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(incoming); var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var record = await db.MessageConflicts.SingleOrDefaultAsync(x => x.Kind == "Payment" && x.BusinessId == incoming.OrderId
            && x.EventId == Guid.Empty && x.MessageType == nameof(PaymentProcessedEvent) && x.PayloadHash == hash, cancellationToken);
        if (record is null)
        {
            record = new MessageConflict { Id = Guid.NewGuid(), Kind = "Payment", BusinessId = incoming.OrderId,
                MessageType = nameof(PaymentProcessedEvent), TransportMessageId = messageId, Payload = payload,
                PayloadHash = hash, Reason = reason, FirstSeenAtUtc = clock.GetUtcNow() };
            db.MessageConflicts.Add(record);
        }
        record.Observations++; record.LastSeenAtUtc = clock.GetUtcNow();
        logger.LogWarning("PaymentApplicationConflict {OrderId} {MessageId} {Reason}", incoming.OrderId, messageId, reason);
    }
    private Task LockAsync(string key, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
    private void RequireOwnTransaction()
    {
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Payment application owns its local transaction.");
    }
}
