using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace FCG.Catalog.Infrastructure.Identity;

public enum IdentityProjectionOutcome { Applied, Ignored, Conflict }

public sealed class IdentityProjection(CatalogDbContext db, TimeProvider clock, ILogger<IdentityProjection> logger)
{
    public async Task<IdentityProjectionOutcome> HandleAsync(IdentitySnapshot incoming, Guid? messageId, CancellationToken cancellationToken)
    {
        IdentitySnapshotValidation.Validate(incoming);
        if (db.Database.CurrentTransaction is not null) throw new InvalidOperationException("Identity projection owns its transaction.");
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"identity:" + incoming.UserId}, 0))", cancellationToken);
        var current = await db.UserAuthorizationStates.SingleOrDefaultAsync(x => x.UserId == incoming.UserId, cancellationToken);
        var outcome = IdentityProjectionOutcome.Applied;
        string? conflict = null;
        if (current is null)
        {
            current = new UserAuthorizationState { UserId = incoming.UserId };
            db.UserAuthorizationStates.Add(current);
        }
        else if (incoming.EventVersion < current.EventVersion) outcome = IdentityProjectionOutcome.Ignored;
        else if (incoming.EventVersion == current.EventVersion)
        {
            if (incoming.Role == current.Role && incoming.State == current.State) outcome = IdentityProjectionOutcome.Ignored;
            else conflict = "SameVersionDifferentAuthorization";
        }
        else if (current.State == IdentityState.Deleted && incoming.State != IdentityState.Deleted) conflict = "DeletedStateIsTerminal";
        if (conflict is not null)
        {
            outcome = IdentityProjectionOutcome.Conflict;
            await RecordConflictAsync(incoming, messageId, conflict, cancellationToken);
        }
        else if (outcome == IdentityProjectionOutcome.Applied)
        {
            current.EventVersion = incoming.EventVersion; current.Role = incoming.Role; current.State = incoming.State;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        if (conflict is not null) logger.LogWarning("IdentityProjectionConflict {UserId} {EventVersion} {Reason}", incoming.UserId, incoming.EventVersion, conflict);
        return outcome;
    }

    private async Task RecordConflictAsync(IdentitySnapshot incoming, Guid? messageId, string reason, CancellationToken cancellationToken)
    {
        // Catalog compares only authorization semantics. Never retain contact in its projection or diagnostic payload.
        var payload = JsonSerializer.Serialize(new { userId = incoming.UserId, eventVersion = incoming.EventVersion, role = incoming.Role.ToString(), state = incoming.State.ToString() });
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
        var type = incoming.GetType().Name;
        var record = await db.MessageConflicts.SingleOrDefaultAsync(x => x.Kind == "Identity" && x.BusinessId == incoming.UserId
            && x.EventId == incoming.EventId && x.MessageType == type && x.PayloadHash == hash, cancellationToken);
        if (record is null)
        {
            record = new MessageConflict { Id = Guid.NewGuid(), Kind = "Identity", BusinessId = incoming.UserId, EventId = incoming.EventId,
                EventVersion = incoming.EventVersion, MessageType = type, TransportMessageId = messageId, Payload = payload,
                PayloadHash = hash, Reason = reason, FirstSeenAtUtc = clock.GetUtcNow() };
            db.MessageConflicts.Add(record);
        }
        record.Observations++; record.LastSeenAtUtc = clock.GetUtcNow();
    }
}
