using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCMC.Contracts.Json;

/// <summary>
/// Deserializes a decimal from either a JSON number or a JSON string.
///
/// Required because the cloud API's TypeORM "decimal" columns (quantityKg,
/// fat, snf, temperature, minValue, maxValue, capacityKg) serialize as JSON
/// STRINGS (e.g. "45.50"), not JSON numbers - TypeORM's standard convention
/// for exact-precision columns, avoiding floating-point drift on the
/// Postgres/Node side. Verified during this project's Phase 0 inspection of
/// the entity source (see context.md "Cloud Responsibilities") - not a
/// guess: e.g. MilkReceptionTransaction.quantityKg is typed `string` in the
/// TypeORM entity, and the API's own seed.ts assigns string literals like
/// `capacityKg: "5000"` to these fields (only valid if the property type is
/// string).
///
/// Applied via [JsonConverter(...)] only to the specific DTO properties
/// known to come from such columns - deliberately NOT a blanket
/// JsonSerializerOptions.NumberHandling change, which would also loosen
/// every int/long property's parsing strictness across every DTO for no
/// reason (the narrowest fix for a specific, evidenced problem).
///
/// Always WRITES as a JSON number - outbound requests are unaffected. The
/// cloud's own class-validator DTOs (e.g. CreateReceptionDto's
/// `@IsNumber() quantityKg`) expect a JSON number, not a string, so this
/// converter must never be applied to outbound-only request DTOs in a way
/// that would change that (it doesn't - Write always emits a number).
/// </summary>
public sealed class FlexibleDecimalJsonConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.String:
                var text = reader.GetString();
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                throw new JsonException($"Could not parse \"{text}\" as a decimal value.");

            case JsonTokenType.Number:
                return reader.GetDecimal();

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} when reading a decimal value.");
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteNumberValue(value);
}

/// <summary>Nullable counterpart of FlexibleDecimalJsonConverter - see its doc comment. Used for optional decimal columns (e.g. Vehicle.capacityKg).</summary>
public sealed class FlexibleNullableDecimalJsonConverter : JsonConverter<decimal?>
{
    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            case JsonTokenType.String:
                var text = reader.GetString();
                if (string.IsNullOrEmpty(text)) return null;
                if (decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }
                throw new JsonException($"Could not parse \"{text}\" as a decimal value.");

            case JsonTokenType.Number:
                return reader.GetDecimal();

            default:
                throw new JsonException($"Unexpected token {reader.TokenType} when reading a nullable decimal value.");
        }
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
