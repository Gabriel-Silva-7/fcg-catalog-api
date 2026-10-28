using FCG.Catalog.Domain.Orders;
using FCG.Contracts;

namespace FCG.Catalog.Application.Orders;

public enum PaymentApplicationOutcome { Approved, Rejected, Duplicate, Quarantined, AlreadyResolved, NotFound }
public sealed record PaymentApplicationResult(PaymentApplicationOutcome Outcome, string? Reason = null);

public static class PaymentApplicationPolicy
{
    public static PaymentApplicationResult Decide(Order? order, PaymentProcessedEvent incoming)
    {
        if (order is null) return new(PaymentApplicationOutcome.Quarantined, "OrderNotFound");
        if (order.UserId != incoming.UserId || order.GameId != incoming.GameId || order.Price != incoming.Price)
            return new(PaymentApplicationOutcome.Quarantined, "PaymentDoesNotMatchOrder");
        if (order.Status == OrderStatus.Pending)
            return new(incoming.Status == "Approved" ? PaymentApplicationOutcome.Approved : PaymentApplicationOutcome.Rejected);
        return order.Status.ToString() == incoming.Status && order.RejectionReason == incoming.Reason && order.CompletedAtUtc == incoming.ProcessedAtUtc
            ? new(PaymentApplicationOutcome.Duplicate) : new(PaymentApplicationOutcome.Quarantined, "DivergentPaymentResult");
    }
}
