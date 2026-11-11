using FCG.Catalog.Domain.Catalog;

namespace FCG.Catalog.Application.Catalog;

public enum GameSortField
{
    Title,
    BasePrice,
    CreatedAt,
}

public static class GameSortFields
{
    public const string Title = "title";
    public const string BasePrice = "basePrice";
    public const string CreatedAt = "createdAt";
    public const string Pattern = "^(title|basePrice|createdAt)$";

    public static bool TryParse(string? value, out GameSortField sortField)
    {
        switch (value)
        {
            case null:
            case Title:
                sortField = GameSortField.Title;
                return true;
            case BasePrice:
                sortField = GameSortField.BasePrice;
                return true;
            case CreatedAt:
                sortField = GameSortField.CreatedAt;
                return true;
            default:
                sortField = default;
                return false;
        }
    }
}

public sealed record GameReadModel(
    Guid Id,
    string Title,
    string? Description,
    decimal BasePrice,
    bool IsActive,
    decimal DiscountPercentage);

public sealed record CatalogGameSummary(
    Guid Id,
    string Title,
    string? Description,
    decimal BasePrice,
    decimal CurrentPrice,
    decimal DiscountPercentage,
    bool IsActive);

internal static class CatalogGameMapper
{
    public static CatalogGameSummary WithPricing(GameReadModel game)
    {
        var pricing = PricingService.Calculate(game.BasePrice, game.DiscountPercentage);

        return new CatalogGameSummary(
            game.Id,
            game.Title,
            game.Description,
            game.BasePrice,
            pricing.CurrentPrice,
            pricing.DiscountPercentage,
            game.IsActive);
    }
}
