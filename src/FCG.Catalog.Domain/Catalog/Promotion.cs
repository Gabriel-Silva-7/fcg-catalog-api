using FCG.Catalog.Domain.Common;

namespace FCG.Catalog.Domain.Catalog;

public sealed class Promotion
{
    public const int DiscountPercentageScale = 2;
    public const decimal MaximumDiscountPercentage = 100m;

    private Promotion(
        Guid id,
        Guid gameId,
        decimal discountPercentage,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        Guid createdByUserId)
    {
        Id = id;
        GameId = gameId;
        DiscountPercentage = discountPercentage;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; private set; }

    public Guid GameId { get; private set; }

    public decimal DiscountPercentage { get; private set; }

    public DateTime StartsAtUtc { get; private set; }

    public DateTime EndsAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public static Promotion Create(
        Guid gameId,
        decimal discountPercentage,
        DateTime startsAtUtc,
        DateTime endsAtUtc,
        Guid createdByUserId)
    {
        if (gameId == Guid.Empty)
        {
            throw new ArgumentException("Game identifier cannot be empty.", nameof(gameId));
        }

        if (discountPercentage is <= 0 or > MaximumDiscountPercentage)
        {
            throw new ArgumentOutOfRangeException(
                nameof(discountPercentage),
                discountPercentage,
                $"Discount percentage must be greater than 0 and at most {MaximumDiscountPercentage}.");
        }

        if (decimal.Round(discountPercentage, DiscountPercentageScale) != discountPercentage)
        {
            throw new ArgumentException(
                $"Discount percentage cannot have more than {DiscountPercentageScale} decimal places.",
                nameof(discountPercentage));
        }

        DomainGuard.EnsureUtc(startsAtUtc, nameof(startsAtUtc));
        DomainGuard.EnsureUtc(endsAtUtc, nameof(endsAtUtc));

        if (startsAtUtc.Ticks % 10 != 0 || endsAtUtc.Ticks % 10 != 0)
            throw new ArgumentException("Promotion dates must be representable as UTC microseconds.", startsAtUtc.Ticks % 10 != 0 ? nameof(startsAtUtc) : nameof(endsAtUtc));

        if (endsAtUtc <= startsAtUtc)
        {
            throw new ArgumentException("End date must be later than start date.", nameof(endsAtUtc));
        }

        if (createdByUserId == Guid.Empty)
        {
            throw new ArgumentException("Creator identifier cannot be empty.", nameof(createdByUserId));
        }

        return new Promotion(
            Guid.NewGuid(),
            gameId,
            discountPercentage,
            startsAtUtc,
            endsAtUtc,
            createdByUserId);
    }

    public bool IsActiveAt(DateTime instantUtc)
    {
        DomainGuard.EnsureUtc(instantUtc, nameof(instantUtc));

        return StartsAtUtc <= instantUtc && instantUtc < EndsAtUtc;
    }
}
