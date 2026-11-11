using FCG.Catalog.Domain.Common;

namespace FCG.Catalog.Domain.Catalog;

public sealed record PriceCalculation(
    decimal CurrentPrice,
    decimal DiscountPercentage);

public static class PricingService
{
    public static PriceCalculation Calculate(
        decimal basePrice,
        IEnumerable<Promotion> promotions,
        DateTime instantUtc)
    {
        ArgumentNullException.ThrowIfNull(promotions);
        DomainGuard.EnsureUtc(instantUtc, nameof(instantUtc));

        var highestActiveDiscount = promotions
            .Where(promotion => promotion.IsActiveAt(instantUtc))
            .Select(promotion => promotion.DiscountPercentage)
            .DefaultIfEmpty(0m)
            .Max();

        return Calculate(basePrice, highestActiveDiscount);
    }

    public static PriceCalculation Calculate(
        decimal basePrice,
        decimal discountPercentage)
    {
        if (basePrice < 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(basePrice),
                basePrice,
                "Base price cannot be negative.");
        }

        if (discountPercentage is < 0m or > Promotion.MaximumDiscountPercentage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discountPercentage),
                discountPercentage,
                $"Discount percentage must be between 0 and {Promotion.MaximumDiscountPercentage}.");
        }

        var currentPrice = decimal.Round(
            basePrice * (1m - discountPercentage / 100m),
            Game.BasePriceScale,
            MidpointRounding.AwayFromZero);

        return new PriceCalculation(currentPrice, discountPercentage);
    }
}
