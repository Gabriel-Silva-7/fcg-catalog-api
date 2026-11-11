namespace FCG.Catalog.Infrastructure.Persistence;

public sealed class PaymentRecoveryAttempt
{
    public Guid Id { get; set; }
    public Guid ConflictId { get; set; }
    public DateTimeOffset AttemptedAtUtc { get; set; }
    public string Outcome { get; set; } = "";
    public string? Reason { get; set; }
}
