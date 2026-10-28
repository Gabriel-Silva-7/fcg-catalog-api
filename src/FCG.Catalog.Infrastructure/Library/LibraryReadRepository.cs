using System.Data;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Application.Library;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Library;

public sealed class LibraryReadRepository(CatalogDbContext db) : ILibraryReadRepository
{
    public async Task<PagedResult<LibraryItem>> SearchByUserAsync(Guid userId, int page, int pageSize, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        // Keep inactive games: acquisition is historical, while title belongs to the current catalog record.
        var query = from entry in db.LibraryEntries.AsNoTracking()
            join game in db.Games.AsNoTracking() on entry.GameId equals game.Id
            where entry.UserId == userId
            select new { entry.GameId, game.Title, entry.AcquiredAtUtc, entry.AcquisitionPrice };
        var count = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.AcquiredAtUtc).ThenBy(x => x.GameId)
            .Skip((page - 1) * pageSize).Take(pageSize).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(rows.Select(x => new LibraryItem(x.GameId, x.Title, x.AcquiredAtUtc, x.AcquisitionPrice)).ToArray(), page, pageSize, count);
    }
}
