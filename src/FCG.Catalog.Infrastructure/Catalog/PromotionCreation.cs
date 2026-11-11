using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Domain.Catalog;
using FCG.Catalog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace FCG.Catalog.Infrastructure.Catalog;

public sealed class PromotionCreation(CatalogDbContext db) : IPromotionCreation
{
    public async Task<Promotion?> CreateForActiveGameAsync(CreatePromotionCommand command, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var game = await db.Games.FromSqlInterpolated($"SELECT * FROM \"Games\" WHERE \"Id\"={command.GameId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken);
        if (game is null || !game.IsActive) return null;
        var promotion = Promotion.Create(command.GameId, command.DiscountPercentage, command.StartsAtUtc, command.EndsAtUtc, command.CreatedByUserId);
        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return promotion;
    }
}
