using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FCG.Catalog.Api.Contracts;

public sealed record CreatePromotionRequest
{
    /// <summary>
    /// Percentage discount greater than zero and at most 100.
    /// </summary>
    [Required]
    [Range(typeof(decimal), "0.01", "100", ParseLimitsInInvariantCulture = true)]
    [JsonConverter(typeof(ExactDiscountJsonConverter))]
    public decimal? DiscountPercentage { get; init; }

    [Required]
    public DateTime? StartsAt { get; init; }

    [Required]
    public DateTime? EndsAt { get; init; }
}
