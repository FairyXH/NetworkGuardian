using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkGuardian.Core.Serialization;

/// <summary>
/// Enum converter that reads and writes the camelCase spelling used by <c>config.json</c>
/// (<c>"information"</c>, <c>"shell"</c>, ...) without any reflection.
/// </summary>
/// <remarks>
/// The source generator cannot register <c>JsonStringEnumConverter</c> (the non-generic one is
/// reflection based and therefore not Native AOT safe) and its <c>UseStringEnumConverter</c> switch
/// does not preserve the documented camelCase contract. An explicit closed converter per enum keeps
/// both the wire format and the AOT analysis clean. Integers are still accepted on read so a
/// hand-edited file with <c>"kind": 1</c> keeps working.
/// </remarks>
public sealed class CamelCaseEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly string[] Names = Enum.GetNames<TEnum>();
    private static readonly string[] JsonNames = Names.Select(ToCamelCase).ToArray();

    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            var number = reader.GetInt64();
            return (TEnum)Enum.ToObject(typeof(TEnum), number);
        }

        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException(
                $"Expected a string or a number for {typeof(TEnum).Name}, found {reader.TokenType}.");
        }

        var text = reader.GetString();
        if (string.IsNullOrEmpty(text))
        {
            throw new JsonException($"Expected a value for {typeof(TEnum).Name}.");
        }

        for (var i = 0; i < JsonNames.Length; i++)
        {
            if (string.Equals(JsonNames[i], text, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(Names[i], text, StringComparison.OrdinalIgnoreCase))
            {
                return Enum.Parse<TEnum>(Names[i]);
            }
        }

        throw new JsonException($"Unknown {typeof(TEnum).Name} value '{text}'.");
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(ToCamelCase(value.ToString()));

    private static string ToCamelCase(string name) =>
        string.IsNullOrEmpty(name) ? name : char.ToLowerInvariant(name[0]) + name[1..];
}
