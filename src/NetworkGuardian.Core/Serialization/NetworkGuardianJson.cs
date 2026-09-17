using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Helper;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Core.Serialization;

/// <summary>
/// JSON metadata for the whole application, generated at compile time.
/// </summary>
/// <remarks>
/// Native AOT has no reflection-based serializer, so every persisted type is registered here and every
/// call site goes through <see cref="NetworkGuardianJson.Options"/>, which uses this context as its
/// type info resolver. Adding a new persisted type means adding it to this list - a missing entry
/// throws at runtime instead of silently falling back to reflection.
/// </remarks>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(GuardianConfig))]
[JsonSerializable(typeof(HelperRequest))]
[JsonSerializable(typeof(HelperResponse))]
public sealed partial class NetworkGuardianJsonContext : JsonSerializerContext
{
}

/// <summary>Shared serializer settings; the single entry point for every JSON read and write.</summary>
public static class NetworkGuardianJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static string Serialize<TValue>(TValue value)
    {
        var info = (JsonTypeInfo<TValue>)Options.GetTypeInfo(typeof(TValue));
        return JsonSerializer.Serialize(value, info);
    }

    public static TValue? Deserialize<TValue>(string json)
    {
        var info = (JsonTypeInfo<TValue>)Options.GetTypeInfo(typeof(TValue));
        return JsonSerializer.Deserialize(json, info);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        // A copy of the generated defaults (which carry the source generated resolver) plus the
        // hand written enum converters: this keeps the documented camelCase enum spelling without
        // reintroducing reflection.
        var options = new JsonSerializerOptions(NetworkGuardianJsonContext.Default.Options);
        options.Converters.Add(new CamelCaseEnumConverter<GuardianLogLevel>());
        options.Converters.Add(new CamelCaseEnumConverter<ProbeKind>());
        options.Converters.Add(new CamelCaseEnumConverter<CommandKind>());
        options.Converters.Add(new CamelCaseEnumConverter<CommandWindowStyle>());
        return options;
    }
}
