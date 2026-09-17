using System.Text.Json;
using NetworkGuardian.Core.Serialization;

namespace NetworkGuardian.Core.Configuration;

/// <summary>
/// Centralized JSON policy so every component reads/writes the same document shape.
/// </summary>
/// <remarks>
/// The serializer metadata is generated at compile time (see <see cref="NetworkGuardianJsonContext"/>),
/// which is what makes the configuration readable and writable from a Native AOT process.
/// </remarks>
public static class ConfigJson
{
    public static JsonSerializerOptions Options => NetworkGuardianJson.Options;

    public static string Serialize(GuardianConfig config) => NetworkGuardianJson.Serialize(config);

    public static GuardianConfig? Deserialize(string json) => NetworkGuardianJson.Deserialize<GuardianConfig>(json);
}
