namespace FCG.Catalog.Application.Catalog;

public sealed record CreatePromotionCommand(
    Guid CreatedByUserId,
    Guid GameId,
    decimal DiscountPercentage,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc);

public sealed record PromotionSummary(
    Guid Id,
    Guid GameId,
    decimal DiscountPercentage,
    DateTime StartsAtUtc,
    DateTime EndsAtUtc,
    bool IsCurrentlyActive);
