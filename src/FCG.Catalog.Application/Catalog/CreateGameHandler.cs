using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Catalog;

namespace FCG.Catalog.Application.Catalog;

public sealed class CreateGameHandler(
    IGameRepository gameRepository,
    IClock clock)
{
    public async Task<CatalogGameSummary> HandleAsync(
        CreateGameCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.BasePrice > GamePriceLimits.MaximumSupportedBasePrice)
        {
            throw new ApplicationValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [nameof(command.BasePrice)] =
                    [
                        $"Base price cannot exceed {GamePriceLimits.MaximumSupportedBasePriceText}.",
                    ],
                });
        }

        Game game;

        try
        {
            game = Game.Create(
                command.Title!,
                command.Description,
                command.BasePrice,
                command.CreatedByUserId,
                clock.UtcNow);
        }
        catch (ArgumentException exception) when (TryGetPublicInputName(exception, out var inputName))
        {
            throw new ApplicationValidationException(
                new Dictionary<string, string[]>(StringComparer.Ordinal)
                {
                    [inputName] = [exception.Message],
                });
        }

        gameRepository.Add(game);
        await gameRepository.SaveChangesAsync(cancellationToken);

        return CatalogGameMapper.WithPricing(
            new GameReadModel(
                game.Id,
                game.Title,
                game.Description,
                game.BasePrice,
                game.IsActive,
                0m));
    }

    private static bool TryGetPublicInputName(ArgumentException exception, out string inputName)
    {
        inputName = exception.ParamName switch
        {
            "title" => nameof(CreateGameCommand.Title),
            "description" => nameof(CreateGameCommand.Description),
            "basePrice" => nameof(CreateGameCommand.BasePrice),
            _ => string.Empty,
        };

        return inputName.Length > 0;
    }
}
