using FCG.Catalog.Domain.Orders;
using Xunit;

namespace FCG.Catalog.Api.Tests.Orders;

public sealed class OrderTests
{
    private static readonly DateTimeOffset Placed = DateTimeOffset.Parse("2026-10-28T15:00:00.123456Z");
    [Theory]
    [InlineData("0")]
    [InlineData("44.93")]
    [InlineData("9999999999999999.99")]
    public void Placement_preserves_exact_snapshot_and_initial_state(string amount)
    {
        var user = Guid.NewGuid(); var game = Guid.NewGuid(); var price = decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture);
        var order = Order.Place(user, game, price, Placed);
        Assert.NotEqual(Guid.Empty, order.Id); Assert.Equal(user, order.UserId); Assert.Equal(game, order.GameId);
        Assert.Equal(price, order.Price); Assert.Equal(Placed, order.PlacedAtUtc); Assert.Equal(OrderStatus.Pending, order.Status);
        Assert.Null(order.CompletedAtUtc); Assert.Null(order.RejectionReason); Assert.Equal(1, order.Version);
    }
    [Theory]
    [InlineData("-0.01")]
    [InlineData("0.001")]
    [InlineData("10000000000000000")]
    public void Unrepresentable_prices_are_rejected_without_rounding(string amount) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Order.Place(Guid.NewGuid(), Guid.NewGuid(), decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Placed));
    [Fact]
    public void Empty_identities_and_noncanonical_instants_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Order.Place(Guid.Empty, Guid.NewGuid(), 1m, Placed));
        Assert.Throws<ArgumentException>(() => Order.Place(Guid.NewGuid(), Guid.Empty, 1m, Placed));
        Assert.Throws<ArgumentException>(() => Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1m, Placed.ToOffset(TimeSpan.FromHours(-3))));
        Assert.Throws<ArgumentException>(() => Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1m, Placed.AddTicks(1)));
    }
    [Theory]
    [InlineData(OrderStatus.Approved, null)]
    [InlineData(OrderStatus.Rejected, "AmountAboveLimit")]
    public void Completion_is_terminal_idempotent_and_keeps_original_snapshot(OrderStatus status, string? reason)
    {
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 44.93m, Placed); var time = Placed.AddSeconds(2);
        Assert.True(order.Complete(status, reason, time)); Assert.False(order.Complete(status, reason, time));
        Assert.Equal(status, order.Status); Assert.Equal(reason, order.RejectionReason); Assert.Equal(time, order.CompletedAtUtc);
        Assert.Equal(2, order.Version); Assert.Equal(44.93m, order.Price); Assert.Equal(Placed, order.PlacedAtUtc);
        Assert.Throws<InvalidOperationException>(() => order.Complete(status, reason, time.AddSeconds(1)));
        Assert.Throws<InvalidOperationException>(() => order.Complete(status == OrderStatus.Approved ? OrderStatus.Rejected : OrderStatus.Approved,
            status == OrderStatus.Approved ? "AmountAboveLimit" : null, time));
        Assert.Equal(status, order.Status); Assert.Equal(time, order.CompletedAtUtc);
    }
    [Theory]
    [InlineData(OrderStatus.Pending, null)]
    [InlineData(OrderStatus.Approved, "AmountAboveLimit")]
    [InlineData(OrderStatus.Rejected, null)]
    [InlineData(OrderStatus.Rejected, "Unknown")]
    [InlineData((OrderStatus)99, null)]
    public void Invalid_completion_does_not_change_pending_order(OrderStatus status, string? reason)
    {
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1m, Placed);
        Assert.Throws<ArgumentException>(() => order.Complete(status, reason, Placed.AddSeconds(2)));
        Assert.Equal(OrderStatus.Pending, order.Status); Assert.Null(order.CompletedAtUtc); Assert.Equal(1, order.Version);
    }
    [Fact]
    public void Completion_rejects_noncanonical_timestamp_even_for_duplicate()
    {
        var order = Order.Place(Guid.NewGuid(), Guid.NewGuid(), 1m, Placed);
        Assert.Throws<ArgumentException>(() => order.Complete(OrderStatus.Approved, null, Placed.AddTicks(1)));
        order.Complete(OrderStatus.Approved, null, Placed);
        Assert.Throws<ArgumentException>(() => order.Complete(OrderStatus.Approved, null, Placed.ToOffset(TimeSpan.FromHours(1))));
    }
}
