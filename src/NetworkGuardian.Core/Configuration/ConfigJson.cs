using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetworkGuardian.Core.Configuration;

/// <summary>Centralized JSON policy so every component reads/writes the same document shape.</summary>
public static class ConfigJson
{
    public static readonly JsonSerializerOptions Options = Create();

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };

        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: true));
        return options;
    }

    public static string Serialize(GuardianConfig config) => JsonSerializer.Serialize(config, Options);

    public static GuardianConfig? Deserialize(string json) =>
        JsonSerializer.Deserialize<GuardianConfig>(json, Options);
}
