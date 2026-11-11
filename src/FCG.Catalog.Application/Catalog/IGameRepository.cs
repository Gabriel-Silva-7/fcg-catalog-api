using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Catalog;

namespace FCG.Catalog.Application.Catalog;

public interface IGameRepository
{
    void Add(Game game);

    Task<PagedResult<GameReadModel>> SearchActiveAsync(
        string? search,
        GameSortField sortBy,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<GameReadModel?> FindActiveByIdAsync(
        Guid gameId,
        CancellationToken cancellationToken);

    Task SaveChangesAsync(CancellationToken cancellationToken);
}
