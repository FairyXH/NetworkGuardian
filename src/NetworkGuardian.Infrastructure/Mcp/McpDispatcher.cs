using System.Text.Json;
using System.Text.Json.Nodes;

namespace NetworkGuardian.Infrastructure.Mcp;

public sealed class McpDispatcher(IMcpToolBackend backend, string version)
{
    public static readonly string[] Versions = { "2025-11-25", "2025-06-18", "2025-03-26" };

    public async Task<JsonObject?> DispatchAsync(string json, CancellationToken token)
    {
        JsonObject request;
        try { request = JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("Expected object"); }
        catch (JsonException) { return Error(null, -32700, "Parse error"); }
        catch (ArgumentException) { return Error(null, -32600, "Expected one JSON-RPC object"); }
        var id = request["id"]?.DeepClone();
        if (request["jsonrpc"]?.ToString() != "2.0" || request["method"] is not JsonValue methodValue ||
            !methodValue.TryGetValue<string>(out var method)) return Error(id, -32600, "Invalid request");
        if (!request.ContainsKey("id")) return null;
        try
        {
            var parameters = request["params"] is null ? new JsonObject() : request["params"] as JsonObject
                ?? throw new ArgumentException("params must be an object");
            JsonObject result;
            switch (method)
            {
                case "initialize":
                    var requested = parameters["protocolVersion"]?.ToString();
                    result = new JsonObject { ["protocolVersion"] = Versions.Contains(requested) ? requested : Versions[0],
                        ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
                        ["serverInfo"] = new JsonObject { ["name"] = "NetworkGuardian", ["version"] = version },
                        ["instructions"] = "本机网络管理。先查询设备、状态和配置再控制。写工具直接执行，无界面确认；不要自动重复失败的设备操作。保持禁用和 SSID 指定始终优先。" };
                    break;
                case "ping": result = new JsonObject(); break;
                case "tools/list":
                    if (parameters["cursor"] is not null) throw new ArgumentException("Pagination cursor is not supported");
                    result = new JsonObject { ["tools"] = new JsonArray(McpTools.All.Select(t => (JsonNode)t.Descriptor()).ToArray()) };
                    break;
                case "tools/call":
                    var name = parameters["name"]?.GetValue<string>() ?? throw new ArgumentException("Tool name is required");
                    var tool = McpTools.All.FirstOrDefault(t => t.Name == name) ?? throw new ArgumentException("Unknown tool");
                    var arguments = parameters["arguments"] is null ? new JsonObject() : parameters["arguments"] as JsonObject
                        ?? throw new ArgumentException("arguments must be an object");
                    Validate(tool, arguments);
                    try
                    {
                        var output = await backend.InvokeAsync(name, arguments, token).ConfigureAwait(false);
                        var data = output as JsonObject ?? new JsonObject { ["data"] = output };
                        var failed = data["success"] is JsonValue success && success.TryGetValue<bool>(out var ok) && !ok;
                        result = new JsonObject { ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = data.ToJsonString() }),
                            ["structuredContent"] = data, ["isError"] = failed };
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        result = new JsonObject { ["isError"] = true, ["content"] = new JsonArray(new JsonObject
                            { ["type"] = "text", ["text"] = ex.Message }) };
                    }
                    break;
                default: return Error(id, -32601, "Method not found");
            }
            return new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { return Error(id, -32602, ex.Message); }
    }

    private static void Validate(McpTool tool, JsonObject args)
    {
        var schema = JsonNode.Parse(tool.Properties)!.AsObject();
        foreach (var required in tool.Required)
            if (args[required] is null) throw new ArgumentException($"Missing argument: {required}");
        foreach (var (name, value) in args)
        {
            if (!schema.TryGetPropertyValue(name, out var rule)) throw new ArgumentException($"Unknown argument: {name}");
            var type = rule!["type"]!.GetValue<string>();
            var valid = type switch
            {
                "object" => value is JsonObject,
                "string" => value is JsonValue s && s.TryGetValue<string>(out _),
                "boolean" => value is JsonValue b && b.TryGetValue<bool>(out _),
                "integer" => value is JsonValue n && n.TryGetValue<int>(out _),
                _ => false,
            };
            if (!valid) throw new ArgumentException($"Invalid type for {name}; expected {type}");
            if (type == "string" && string.IsNullOrWhiteSpace(value!.GetValue<string>())) throw new ArgumentException($"Empty argument: {name}");
            if (rule["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value)))
                throw new ArgumentException($"Invalid value for {name}");
            if (type == "integer" && ((rule["minimum"] is { } min && value!.GetValue<int>() < min.GetValue<int>()) ||
                (rule["maximum"] is { } max && value!.GetValue<int>() > max.GetValue<int>())))
                throw new ArgumentException($"Argument out of range: {name}");
        }
    }

    public static JsonObject Error(JsonNode? id, int code, string message) => new()
    { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };
}
