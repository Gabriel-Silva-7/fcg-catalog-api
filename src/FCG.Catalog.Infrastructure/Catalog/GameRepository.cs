using System.Data;
using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Catalog;

public sealed class GameRepository(CatalogDbContext db, IClock clock) : IGameRepository
{
    public void Add(Game game) => db.Games.Add(game);
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
    public async Task<PagedResult<GameReadModel>> SearchActiveAsync(string? search, GameSortField sortBy, int page, int pageSize, CancellationToken cancellationToken)
    {
        var instant = clock.UtcNow;
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
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
        var items = await ProjectWithCurrentDiscount(db, ordered.Skip((page - 1) * pageSize).Take(pageSize), instant).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(items, page, pageSize, count);
    }
    public Task<GameReadModel?> FindActiveByIdAsync(Guid gameId, CancellationToken cancellationToken) =>
        ProjectWithCurrentDiscount(db, db.Games.AsNoTracking().Where(game => game.Id == gameId && game.IsActive), clock.UtcNow).SingleOrDefaultAsync(cancellationToken);
    internal static IQueryable<GameReadModel> ProjectWithCurrentDiscount(CatalogDbContext db, IQueryable<Game> query, DateTime instant)
    {
        if (instant.Kind != DateTimeKind.Utc) throw new InvalidOperationException("Catalog clock must return UTC.");
        return query.Select(game => new GameReadModel(game.Id, game.Title, game.Description, game.BasePrice, game.IsActive,
            db.Promotions.Where(p => p.GameId == game.Id && p.StartsAtUtc <= instant && instant < p.EndsAtUtc)
                .Select(p => (decimal?)p.DiscountPercentage).Max() ?? 0m));
    }
}
