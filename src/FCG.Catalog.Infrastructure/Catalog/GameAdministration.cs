using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Catalog;

public sealed class GameAdministration(CatalogDbContext db) : IGameAdministration
{
    public async Task<GameReadModel?> UpdateAsync(UpdateGameCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var game = await db.Games.FromSqlInterpolated($"SELECT * FROM \"Games\" WHERE \"Id\"={command.Id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (game is null || !game.IsActive) return null;
        game.Update(command.Title!, command.Description, command.BasePrice);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(game.Id, game.Title, game.Description, game.BasePrice, game.IsActive, 0m);
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
