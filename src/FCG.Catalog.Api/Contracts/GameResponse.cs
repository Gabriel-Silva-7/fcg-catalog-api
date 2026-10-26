namespace FCG.Catalog.Api.Contracts;

public sealed record GameResponse(
    Guid Id,
    string Title,
    string? Description,
    decimal BasePrice,
    decimal CurrentPrice,
    decimal DiscountPercentage,
    bool IsActive);
