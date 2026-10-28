using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCG.Catalog.Infrastructure.Messaging;

// The application's event types own their canonical converters; never copy their wire rules here.
public sealed class PurchaseEventJsonConverter<T> : JsonConverter<T> where T : class
{
    private static readonly JsonSerializerOptions Canonical = new();
    public override bool HandleNull => true;
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        JsonSerializer.Deserialize<T>(ref reader, Canonical) ?? throw new JsonException("A purchase event is required.");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => JsonSerializer.Serialize(writer, value, Canonical);
}
