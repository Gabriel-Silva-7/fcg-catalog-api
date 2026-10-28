using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Orders;
using FCG.Contracts;

namespace FCG.Catalog.Application.Orders;

public enum PlacementStatus { Accepted, GameNotFound, AlreadyPending, AlreadyAcquired, Busy }
public sealed record PlacementResult(PlacementStatus Status, PurchaseAcceptedResponse? Accepted = null, Guid? ExistingOrderId = null);

public interface IPendingOrderCommitter
{
    Task<PlacementResult> CommitAsync(Order order, CancellationToken cancellationToken);
}
public interface IOrderPlacedEvents
{
    Task StageAsync(OrderPlacedEvent message, CancellationToken cancellationToken);
}

public sealed class PlaceOrderHandler(IGameRepository games, IPendingOrderCommitter committer, IClock clock)
{
    public async Task<PlacementResult> HandleAsync(Guid userId, Guid gameId, CancellationToken cancellationToken)
    {
        if (gameId == Guid.Empty)
            throw new ApplicationValidationException(new Dictionary<string, string[]> { ["GameId"] = ["GameId must not be empty."] });
        if (userId == Guid.Empty) throw new ArgumentException("Authenticated identity is required.", nameof(userId));
        var game = await games.FindActiveByIdAsync(gameId, cancellationToken);
        if (game is null) return new(PlacementStatus.GameNotFound);
        var price = CatalogGameMapper.WithPricing(game).CurrentPrice;
        var now = clock.UtcNow;
        if (now.Kind != DateTimeKind.Utc) throw new InvalidOperationException("The server clock must return UTC.");
        var placed = new DateTimeOffset(new DateTime(now.Ticks - now.Ticks % 10, DateTimeKind.Utc));
        var order = Order.Place(userId, game.Id, price, placed);
        return await committer.CommitAsync(order, cancellationToken);
    }
}
