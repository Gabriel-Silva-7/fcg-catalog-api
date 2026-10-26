using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace FCG.Catalog.Api;

public sealed class CatalogDatabaseHealthCheck(CatalogDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await db.Games.AsNoTracking().Take(1).ToArrayAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Catalog storage is unavailable.");
        }
    }
}
