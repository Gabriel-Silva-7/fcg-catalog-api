using FCG.Catalog.Application.Common;

namespace FCG.Catalog.Application.Catalog;

public sealed record UpdateGameCommand(Guid Id, string? Title, string? Description, decimal BasePrice);

public interface IGameAdministration
{
    Task<GameReadModel?> UpdateAsync(UpdateGameCommand command, CancellationToken cancellationToken);
    Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class GameAdministrationHandler(IGameAdministration repository)
{
    public async Task<CatalogGameSummary?> UpdateAsync(UpdateGameCommand command, CancellationToken cancellationToken)
    {
        if (command.BasePrice > GamePriceLimits.MaximumSupportedBasePrice)
            throw new ApplicationValidationException(new Dictionary<string, string[]> { ["BasePrice"] = ["Base price exceeds supported numeric precision."] });
        try
        {
            var game = await repository.UpdateAsync(command, cancellationToken);
            return game is null ? null : CatalogGameMapper.WithPricing(game);
        }
        catch (ArgumentException exception) when (exception.ParamName is "title" or "description" or "basePrice")
        {
            var field = exception.ParamName switch { "title" => "Title", "description" => "Description", _ => "BasePrice" };
            throw new ApplicationValidationException(new Dictionary<string, string[]> { [field] = [exception.Message] });
        }
    }
    public Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken) => repository.DeactivateAsync(id, cancellationToken);
}
