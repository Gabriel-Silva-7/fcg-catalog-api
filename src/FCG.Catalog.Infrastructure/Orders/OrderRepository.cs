using FCG.Catalog.Application.Orders;
using FCG.Catalog.Domain.Orders;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Orders;

public sealed class OrderRepository(CatalogDbContext db) : IOrderRepository
{
    public void Add(Order order) => db.Orders.Add(order);
    public Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.SingleOrDefaultAsync(x => x.Id == orderId, cancellationToken);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
