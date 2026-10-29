using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using FCG.Catalog.Application.Catalog;
using FCG.Catalog.Domain.Catalog;

namespace FCG.Catalog.Api.Contracts;

public sealed record UpdateGameRequest
{
    [Required]
    [StringLength(Game.MaxTitleLength, MinimumLength = 1)]
    public string? Title { get; init; }

    [StringLength(Game.MaxDescriptionLength)]
    public string? Description { get; init; }

    [Required]
    [Range(
        typeof(decimal),
        GamePriceLimits.MinimumBasePriceText,
        GamePriceLimits.MaximumSupportedBasePriceText,
        ParseLimitsInInvariantCulture = true,
        ErrorMessage = "Base price must be between 0 and 9999999999999999.99.")]
    [JsonConverter(typeof(ExactPriceJsonConverter))]
    public decimal? BasePrice { get; init; }
}
