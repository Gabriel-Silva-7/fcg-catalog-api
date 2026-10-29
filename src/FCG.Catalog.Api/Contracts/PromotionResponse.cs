namespace FCG.Catalog.Api.Contracts;

public sealed record PromotionResponse(
    Guid Id,
    Guid GameId,
    decimal DiscountPercentage,
    DateTime StartsAt,
    DateTime EndsAt,
    bool IsCurrentlyActive);
