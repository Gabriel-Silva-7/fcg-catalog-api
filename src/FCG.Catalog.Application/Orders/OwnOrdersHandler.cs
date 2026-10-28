using FCG.Contracts;

namespace FCG.Catalog.Application.Orders;

public interface IOrderReadRepository
{
    Task<PurchaseOrderResponse?> FindByUserAsync(Guid userId, Guid orderId, CancellationToken cancellationToken);
    Task<PurchaseOrdersPage> SearchByUserAsync(Guid userId, PurchaseOrdersQuery query, CancellationToken cancellationToken);
}

public sealed class OwnOrdersHandler(IOrderReadRepository repository)
{
    public Task<PurchaseOrderResponse?> FindAsync(Guid userId, Guid orderId, CancellationToken cancellationToken) =>
        repository.FindByUserAsync(userId, orderId, cancellationToken);
    public Task<PurchaseOrdersPage> ListAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken) =>
        repository.SearchByUserAsync(userId, new PurchaseOrdersQuery(page, pageSize), cancellationToken);
}
