using System.Text.Json.Nodes;

namespace NetworkGuardian.Core.Configuration;

/// <summary>Applies only edits made since a page's baseline to the latest configuration.</summary>
public static class ConfigMerge
{
    public static GuardianConfig Apply(GuardianConfig latest, GuardianConfig baseline, GuardianConfig edited)
    {
        var result = JsonNode.Parse(ConfigJson.Serialize(latest))!;
        Merge(result, JsonNode.Parse(ConfigJson.Serialize(baseline))!, JsonNode.Parse(ConfigJson.Serialize(edited))!);
        return ConfigJson.Deserialize(result.ToJsonString())!;
    }

    private static void Merge(JsonNode target, JsonNode baseline, JsonNode edited)
    {
        if (target is not JsonObject current || baseline is not JsonObject before || edited is not JsonObject after)
        {
            return;
        }

        foreach (var key in before.Select(p => p.Key).Union(after.Select(p => p.Key)).ToArray())
        {
            before.TryGetPropertyValue(key, out var oldValue);
            var exists = after.TryGetPropertyValue(key, out var newValue);
            if (JsonNode.DeepEquals(oldValue, newValue) && before.ContainsKey(key) == exists)
            {
                continue;
            }

            if (!exists)
            {
                current.Remove(key);
            }
            else if (oldValue is JsonObject && newValue is JsonObject && current[key] is JsonObject child)
            {
                Merge(child, oldValue, newValue);
            }
            else
            {
                current[key] = newValue?.DeepClone();
            }
        }
    }
}
