using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FCG.Catalog.Api.Contracts;

/// <summary>Rejects fractions of a cent before decimal parsing could round a long JSON number.</summary>
public sealed class ExactPriceJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.Number) throw new JsonException("Price must be a JSON number.");
        using var document = JsonDocument.ParseValue(ref reader);
        var raw = document.RootElement.GetRawText();
        var exponentAt = raw.IndexOfAny(['e', 'E']);
        var mantissa = exponentAt < 0 ? raw : raw[..exponentAt];
        var pointAt = mantissa.IndexOf('.');
        var fractionDigits = pointAt < 0 ? 0 : mantissa.Length - pointAt - 1;
        var digits = mantissa.Replace(".", "", StringComparison.Ordinal).TrimStart('-').TrimStart('0');
        if (digits.Length == 0) return 0m;
        var significant = digits.TrimEnd('0');
        var trailingZeros = digits.Length - significant.Length;
        if (mantissa[0] == '-' || significant.Length > 18 || !int.TryParse(exponentAt < 0 ? "0" : raw[(exponentAt + 1)..],
                NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var exponent))
            throw new JsonException("Price must fit nonnegative numeric(18,2).");
        var power = 2L - fractionDigits + trailingZeros + exponent;
        if (power < 0 || power > 18 || significant.Length + power > 18)
            throw new JsonException("Price must contain exact cents within numeric(18,2).");
        var cents = decimal.Parse(significant, CultureInfo.InvariantCulture);
        for (var i = 0; i < power; i++) cents *= 10m;
        return cents / 100m;
    }
    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) => writer.WriteNumberValue(value);
}
