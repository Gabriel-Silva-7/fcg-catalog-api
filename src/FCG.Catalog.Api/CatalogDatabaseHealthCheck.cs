using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using MassTransit.EntityFrameworkCoreIntegration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FCG.Catalog.Api;

public sealed class CatalogDatabaseHealthCheck(CatalogDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Set<OutboxMessage>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Set<OutboxState>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Set<InboxState>().AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.LibraryEntries.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.MessageConflicts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.PaymentRecoveryAttempts.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Orders.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.UserAuthorizationStates.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Promotions.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            await db.Games.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Catalog storage is unavailable.");
        }
    }
}
