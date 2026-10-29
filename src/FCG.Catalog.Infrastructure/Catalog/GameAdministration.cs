using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Application.Common;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Catalog;

public sealed class GameAdministration(CatalogDbContext db, IClock clock) : IGameAdministration
{
    public async Task<GameReadModel?> UpdateAsync(UpdateGameCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var game = await db.Games.FromSqlInterpolated($"SELECT * FROM \"Games\" WHERE \"Id\"={command.Id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (game is null || !game.IsActive) return null;
        game.Update(command.Title!, command.Description, command.BasePrice);
        await db.SaveChangesAsync(cancellationToken);
        var result = await GameRepository.ProjectWithCurrentDiscount(db, db.Games.AsNoTracking().Where(x => x.Id == game.Id), clock.UtcNow)
            .SingleAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<bool> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var game = await db.Games.FromSqlInterpolated($"SELECT * FROM \"Games\" WHERE \"Id\"={id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (game is null) return false;
        game.Deactivate();
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }
}
