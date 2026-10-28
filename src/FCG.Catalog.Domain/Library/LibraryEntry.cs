using FCG.Catalog.Domain.Common;

namespace FCG.Catalog.Domain.Library;

public sealed class LibraryEntry
{
    public const int PriceScale = 2;

    private LibraryEntry(
        Guid userId,
        Guid gameId,
        DateTime acquiredAtUtc,
        decimal acquisitionPrice)
    {
        UserId = userId;
        GameId = gameId;
        AcquiredAtUtc = acquiredAtUtc;
        AcquisitionPrice = acquisitionPrice;
    }

    public Guid UserId { get; private set; }

    public Guid GameId { get; private set; }

    public DateTime AcquiredAtUtc { get; private set; }

    public decimal AcquisitionPrice { get; private set; }

    public Guid? OriginOrderId { get; private set; }

    public static LibraryEntry FromApprovedOrder(FCG.Catalog.Domain.Orders.Order order)
    {
        if (order.Status != FCG.Catalog.Domain.Orders.OrderStatus.Approved || order.CompletedAtUtc is null)
            throw new InvalidOperationException("Only an approved order can grant a game.");
        var entry = Create(order.UserId, order.GameId, order.CompletedAtUtc.Value.UtcDateTime, order.Price);
        entry.OriginOrderId = order.Id;
        return entry;
    }

    public static LibraryEntry Create(
        Guid userId,
        Guid gameId,
        DateTime acquiredAtUtc,
        decimal acquisitionPrice)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("User identifier cannot be empty.", nameof(userId));
        }

        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game identifier cannot be empty.", nameof(gameId));
        }

        if (acquisitionPrice < 0 || acquisitionPrice > 9999999999999999.99m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(acquisitionPrice),
                acquisitionPrice,
                "Acquisition price must fit non-negative numeric(18,2).");
        }

        if (decimal.Round(acquisitionPrice, PriceScale) != acquisitionPrice)
        {
            throw new ArgumentException(
                $"Acquisition price cannot have more than {PriceScale} decimal places.",
                nameof(acquisitionPrice));
        }

        DomainGuard.EnsureUtc(acquiredAtUtc, nameof(acquiredAtUtc));

        return new LibraryEntry(userId, gameId, acquiredAtUtc, acquisitionPrice);
    }
}
