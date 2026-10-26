namespace FCG.Catalog.Application.Catalog;

public static class GamePriceLimits
{
    // Capacidade técnica de numeric(18,2); não é um teto comercial do domínio.
    public const decimal MaximumSupportedBasePrice = 9_999_999_999_999_999.99m;
    public const string MinimumBasePriceText = "0";
    public const string MaximumSupportedBasePriceText = "9999999999999999.99";
}

public sealed record CreateGameCommand(
    Guid CreatedByUserId,
    string? Title,
    string? Description,
    decimal BasePrice);
