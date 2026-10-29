using FCG.Catalog.Domain.Common;

namespace FCG.Catalog.Domain.Catalog;

public sealed class Game
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 2_000;
    public const int BasePriceScale = 2;

    private Game(
        Guid id,
        string title,
        string? description,
        decimal basePrice,
        bool isActive,
        DateTime createdAtUtc,
        Guid createdByUserId)
    {
        Id = id;
        Title = title;
        Description = description;
        BasePrice = basePrice;
        IsActive = isActive;
        CreatedAtUtc = createdAtUtc;
        CreatedByUserId = createdByUserId;
    }

    public Guid Id { get; private set; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public decimal BasePrice { get; private set; }

    public bool IsActive { get; private set; }

    public DateTime CreatedAtUtc { get; private set; }

    public Guid CreatedByUserId { get; private set; }

    public static Game Create(
        string title,
        string? description,
        decimal basePrice,
        Guid createdByUserId,
        DateTime createdAtUtc)
    {
        var (normalizedTitle, normalizedDescription) = NormalizeDetails(title, description, basePrice);

        if (createdByUserId == Guid.Empty)
        {
            throw new ArgumentException("Creator identifier cannot be empty.", nameof(createdByUserId));
        }

        DomainGuard.EnsureUtc(createdAtUtc, nameof(createdAtUtc));

        return new Game(
            Guid.NewGuid(),
            normalizedTitle,
            normalizedDescription,
            basePrice,
            true,
            createdAtUtc,
            createdByUserId);
    }

    public void Update(string title, string? description, decimal basePrice)
    {
        if (!IsActive) throw new InvalidOperationException("An inactive game cannot be updated.");
        var (normalizedTitle, normalizedDescription) = NormalizeDetails(title, description, basePrice);
        Title = normalizedTitle; Description = normalizedDescription; BasePrice = basePrice;
    }

    private static (string Title, string? Description) NormalizeDetails(string title, string? description, decimal basePrice)
    {
        ArgumentNullException.ThrowIfNull(title);

        var normalizedTitle = title.Trim();

        if (normalizedTitle.Length is 0 or > MaxTitleLength)
        {
            throw new ArgumentException(
                $"Title must contain between 1 and {MaxTitleLength} characters.",
                nameof(title));
        }

        var normalizedDescription = string.IsNullOrWhiteSpace(description)
            ? null
            : description.Trim();

        if (normalizedDescription?.Length > MaxDescriptionLength)
        {
            throw new ArgumentException(
                $"Description cannot exceed {MaxDescriptionLength} characters.",
                nameof(description));
        }

        if (basePrice < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(basePrice),
                basePrice,
                "Base price cannot be negative.");
        }

        if (decimal.Round(basePrice, BasePriceScale) != basePrice)
        {
            throw new ArgumentException(
                $"Base price cannot have more than {BasePriceScale} decimal places.",
                nameof(basePrice));
        }

        return (normalizedTitle, normalizedDescription);
    }

    public void Deactivate() => IsActive = false;
}
