using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCG.Catalog.Api.Contracts;

public sealed class ExactDiscountJsonConverter : JsonConverter<decimal>
{
    private readonly ExactPriceJsonConverter exact = new();
    public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        try { return exact.Read(ref reader, type, options); }
        catch (JsonException) { throw new JsonException("DiscountPercentage must be a nonnegative decimal with exact hundredths."); }
    }
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
