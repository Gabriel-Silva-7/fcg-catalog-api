using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Catalog;

public sealed class GameRepository(CatalogDbContext db) : IGameRepository
{
    public void Add(Game game) => db.Games.Add(game);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
    public async Task<PagedResult<GameReadModel>> SearchActiveAsync(string? search, GameSortField sortBy, int page, int pageSize, CancellationToken cancellationToken)
    {
        var query = db.Games.AsNoTracking().Where(game => game.IsActive);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%";
            query = query.Where(game => EF.Functions.ILike(game.Title, pattern, "\\"));
        }
        var count = await query.CountAsync(cancellationToken);
        var ordered = sortBy switch
        {
            GameSortField.Title => query.OrderBy(game => game.Title).ThenBy(game => game.Id),
            GameSortField.BasePrice => query.OrderBy(game => game.BasePrice).ThenBy(game => game.Title).ThenBy(game => game.Id),
            GameSortField.CreatedAt => query.OrderBy(game => game.CreatedAtUtc).ThenBy(game => game.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(sortBy))
        };
        var items = await Project(ordered.Skip((page - 1) * pageSize).Take(pageSize)).ToArrayAsync(cancellationToken);
        return new(items, page, pageSize, count);
    }
    public Task<GameReadModel?> FindActiveByIdAsync(Guid gameId, CancellationToken cancellationToken) =>
        Project(db.Games.AsNoTracking().Where(game => game.Id == gameId && game.IsActive)).SingleOrDefaultAsync(cancellationToken);
    private static IQueryable<GameReadModel> Project(IQueryable<Game> query) => query.Select(game => new GameReadModel(
        game.Id, game.Title, game.Description, game.BasePrice, game.IsActive, 0m));
}
