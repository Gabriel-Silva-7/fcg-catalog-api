using System.Data;
using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using FCG.Contracts;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Orders;

public sealed class OrderReadRepository(CatalogDbContext db) : IOrderReadRepository
{
    public async Task<PurchaseOrderResponse?> FindByUserAsync(Guid userId, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().SingleOrDefaultAsync(x => x.Id == orderId && x.UserId == userId, cancellationToken);
        return order is null ? null : Map(order);
    }
    public async Task<PurchaseOrdersPage> SearchByUserAsync(Guid userId, PurchaseOrdersQuery query, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        var owned = db.Orders.AsNoTracking().Where(x => x.UserId == userId);
        var count = await owned.CountAsync(cancellationToken);
        var rows = await owned.OrderByDescending(x => x.PlacedAtUtc).ThenBy(x => x.Id)
            .Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(rows.Select(Map).ToArray(), query.Page, query.PageSize, count);
    }
    private static PurchaseOrderResponse Map(Order order) => new(order.Id, order.GameId, order.Price, order.Status.ToString(),
        order.PlacedAtUtc, order.CompletedAtUtc, order.RejectionReason);
}
