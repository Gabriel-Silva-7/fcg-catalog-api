namespace FCG.Catalog.Domain.Orders;

public enum OrderStatus { Pending, Approved, Rejected }

public sealed class Order
{
    private Order() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid GameId { get; private set; }
    public decimal Price { get; private set; }
    public OrderStatus Status { get; private set; }
    public DateTimeOffset PlacedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public string? RejectionReason { get; private set; }
    public long Version { get; private set; }

    // Price and availability are selected by Catalog's placement application flow.
    public static Order Place(Guid userId, Guid gameId, decimal price, DateTimeOffset placedAtUtc)
    {
        if (userId == Guid.Empty || gameId == Guid.Empty) throw new ArgumentException("Order identities cannot be empty.");
        if (price < 0 || price > 9999999999999999.99m || decimal.Round(price, 2) != price)
            throw new ArgumentOutOfRangeException(nameof(price), "Price must fit exact non-negative BRL numeric(18,2).");
        RequireInstant(placedAtUtc);
        return new Order { Id = Guid.NewGuid(), UserId = userId, GameId = gameId, Price = price,
            PlacedAtUtc = placedAtUtc, Status = OrderStatus.Pending, Version = 1 };
    }

    public bool Complete(OrderStatus status, string? reason, DateTimeOffset processedAtUtc)
    {
        if (!(status == OrderStatus.Approved && reason is null || status == OrderStatus.Rejected && reason == "AmountAboveLimit"))
            throw new ArgumentException("Completion must be Approved without a reason or Rejected/AmountAboveLimit.");
        RequireInstant(processedAtUtc);
        if (Status != OrderStatus.Pending)
        {
            if (Status == status && RejectionReason == reason && CompletedAtUtc == processedAtUtc) return false;
            throw new InvalidOperationException("A terminal order cannot change its recorded result.");
        }
        Status = status; RejectionReason = reason; CompletedAtUtc = processedAtUtc; Version++;
        return true;
    }

    private static void RequireInstant(DateTimeOffset instant)
    {
        if (instant.Offset != TimeSpan.Zero || instant.Ticks % 10 != 0)
            throw new ArgumentException("Order instants must use UTC with microsecond precision.");
    }
}
