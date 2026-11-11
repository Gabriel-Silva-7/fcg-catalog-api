using FCG.Catalog.Domain.Orders;

namespace FCG.Catalog.Application.Orders;

public interface IOrderRepository
{
    void Add(Order order);
    Task<Order?> FindAsync(Guid orderId, CancellationToken cancellationToken);
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
