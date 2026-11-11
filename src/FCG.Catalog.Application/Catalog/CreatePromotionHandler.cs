using FCG.Catalog.Application.Common;
using FCG.Catalog.Domain.Catalog;

namespace FCG.Catalog.Application.Catalog;

public interface IPromotionCreation
{
    Task<Promotion?> CreateForActiveGameAsync(CreatePromotionCommand command, CancellationToken cancellationToken);
}

public sealed class CreatePromotionHandler(IPromotionCreation repository, IClock clock)
{
    public async Task<PromotionSummary?> HandleAsync(CreatePromotionCommand command, CancellationToken cancellationToken)
    {
        Promotion? promotion;
        try { promotion = await repository.CreateForActiveGameAsync(command, cancellationToken); }
        catch (ArgumentException exception) when (exception.ParamName is "discountPercentage" or "startsAtUtc" or "endsAtUtc")
        {
            var field = exception.ParamName switch { "discountPercentage" => "DiscountPercentage", "startsAtUtc" => "StartsAt", _ => "EndsAt" };
            throw new ApplicationValidationException(new Dictionary<string, string[]> { [field] = [exception.Message] });
        }
        return promotion is null ? null : new(promotion.Id, promotion.GameId, promotion.DiscountPercentage,
            promotion.StartsAtUtc, promotion.EndsAtUtc, promotion.IsActiveAt(clock.UtcNow));
    }
}
