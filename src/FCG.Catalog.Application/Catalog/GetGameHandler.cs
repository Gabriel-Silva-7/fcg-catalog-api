namespace FCG.Catalog.Application.Catalog;

public sealed class GetGameHandler(IGameRepository gameRepository)
{
    public async Task<CatalogGameSummary?> HandleAsync(
        Guid gameId,
        CancellationToken cancellationToken)
    {
        var game = await gameRepository.FindActiveByIdAsync(gameId, cancellationToken);

        return game is null ? null : CatalogGameMapper.WithPricing(game);
    }
}
