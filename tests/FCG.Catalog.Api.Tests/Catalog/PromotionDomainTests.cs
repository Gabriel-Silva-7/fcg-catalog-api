using FCG.Catalog.Domain.Catalog;
using Xunit;

namespace FCG.Catalog.Api.Tests.Catalog;

public sealed class PromotionDomainTests
{
    private static readonly DateTime Start = new(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Domain_rejects_invalid_percentage(decimal percentage) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => Promotion.Create(Guid.NewGuid(), percentage, Start, Start.AddDays(1), Guid.NewGuid()));
    [Fact]
    public void Domain_rejects_fractional_percent_date_precision_non_utc_and_empty_identifiers()
    {
        Assert.Throws<ArgumentException>(() => Promotion.Create(Guid.NewGuid(), 25.001m, Start, Start.AddDays(1), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Promotion.Create(Guid.Empty, 25m, Start, Start.AddDays(1), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Promotion.Create(Guid.NewGuid(), 25m, Start, Start.AddDays(1), Guid.Empty));
        Assert.Throws<ArgumentException>(() => Promotion.Create(Guid.NewGuid(), 25m, Start, Start, Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Promotion.Create(Guid.NewGuid(), 25m, Start.AddTicks(1), Start.AddDays(1), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Promotion.Create(Guid.NewGuid(), 25m, Start, Start.AddTicks(1), Guid.NewGuid()));
        Assert.Throws<ArgumentException>(() => Promotion.Create(Guid.NewGuid(), 25m, DateTime.SpecifyKind(Start, DateTimeKind.Unspecified), Start.AddDays(1), Guid.NewGuid()));
    }
    [Fact]
    public void Domain_selects_greatest_active_overlap_with_inclusive_start_exclusive_end()
    {
        var game = Guid.NewGuid(); var actor = Guid.NewGuid();
        var promotions = new[] { Promotion.Create(game, 10m, Start, Start.AddHours(1), actor),
            Promotion.Create(game, 25m, Start, Start.AddHours(1), actor),
            Promotion.Create(game, 90m, Start.AddDays(1), Start.AddDays(2), actor) };
        Assert.Equal(59.90m, PricingService.Calculate(59.90m, promotions, Start.AddTicks(-10)).CurrentPrice);
        Assert.Equal(44.93m, PricingService.Calculate(59.90m, promotions, Start).CurrentPrice);
        Assert.Equal(44.93m, PricingService.Calculate(59.90m, promotions, Start.AddHours(1).AddTicks(-10)).CurrentPrice);
        Assert.Equal(59.90m, PricingService.Calculate(59.90m, promotions, Start.AddHours(1)).CurrentPrice);
    }
}
