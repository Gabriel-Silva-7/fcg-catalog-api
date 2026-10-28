using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace FCG.Catalog.Infrastructure.Orders;

public sealed class PendingOrderCommitter(IServiceScopeFactory scopes) : IPendingOrderCommitter
{
    public async Task<PlacementResult> CommitAsync(Order order, CancellationToken cancellationToken)
    {
        // Every collision gets a fresh DbContext and scoped bus outbox. The selected snapshot stays unchanged.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<CatalogDbContext>();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"pair:" + order.UserId + ":" + order.GameId}, 0))", cancellationToken);
            if (await db.LibraryEntries.AsNoTracking().AnyAsync(x => x.UserId == order.UserId && x.GameId == order.GameId, cancellationToken))
                return new(PlacementStatus.AlreadyAcquired);
            var existing = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.UserId == order.UserId && x.GameId == order.GameId
                && (x.Status == OrderStatus.Pending || x.Status == OrderStatus.Approved), cancellationToken);
            if (existing is not null)
                return existing.Status == OrderStatus.Approved ? new(PlacementStatus.AlreadyAcquired)
                    : new(PlacementStatus.AlreadyPending, ExistingOrderId: existing.Id);
            db.Orders.Add(order);
            await scope.ServiceProvider.GetRequiredService<IOrderPlacedEvents>().StageAsync(
                new OrderPlacedEvent(order.Id, order.UserId, order.GameId, order.Price, order.PlacedAtUtc), cancellationToken);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(PlacementStatus.Accepted, new PurchaseAcceptedResponse(order.Id, order.GameId, order.Price));
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException
                { SqlState: PostgresErrorCodes.UniqueViolation, SchemaName: "public", TableName: "Orders", ConstraintName: "UX_Orders_OpenUserGame" })
            { await transaction.RollbackAsync(CancellationToken.None); }
        }
        // Repeated external contention must not be misreported as an observed Pending order.
        return new(PlacementStatus.Busy);
    }
}
