using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace FCG.Catalog.Api.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record PurchaseBody
{
    [Required] public Guid? GameId { get; init; }
}
