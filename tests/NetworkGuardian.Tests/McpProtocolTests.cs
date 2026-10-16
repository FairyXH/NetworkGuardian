using System.Text.Json.Nodes;
using NetworkGuardian.Infrastructure.Mcp;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class McpProtocolTests
{
    internal sealed class Backend : IMcpToolBackend
    {
        public int Calls;
        public Func<string, JsonObject, Task<JsonNode?>>? Handler;
        public Task<JsonNode?> InvokeAsync(string name, JsonObject args, CancellationToken token)
        {
            Calls++;
            return Handler?.Invoke(name, args) ?? Task.FromResult<JsonNode?>(new JsonObject { ["success"] = true });
        }
    }

    [Theory]
    [InlineData("2025-11-25", "2025-11-25")]
    [InlineData("2025-03-26", "2025-03-26")]
    [InlineData("unknown", "2025-11-25")]
    public async Task InitializationNegotiatesProtocolAndTools(string requested, string expected)
    {
        var dispatcher = new McpDispatcher(new Backend(), "test");
        var reply = await dispatcher.DispatchAsync($$$"""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"{{{requested}}}"}}""", default);
        Assert.Equal(expected, reply!["result"]!["protocolVersion"]!.GetValue<string>());
        Assert.NotNull(reply["result"]!["capabilities"]!["tools"]);
    }

    [Fact]
    public async Task ToolListHasUniqueNamesAndUsableSchemas()
    {
        var reply = await new McpDispatcher(new Backend(), "test").DispatchAsync("""{"jsonrpc":"2.0","id":"a","method":"tools/list"}""", default);
        Assert.Equal(McpTools.All.Count, reply!["result"]!["tools"]!.AsArray().Count);
        Assert.Equal(McpTools.All.Count, McpTools.All.Select(t => t.Name).Distinct().Count());
        foreach (var tool in McpTools.All)
        {
            var descriptor = tool.Descriptor();
            var properties = descriptor["inputSchema"]!["properties"]!.AsObject();
            Assert.All(tool.Required, key => Assert.True(properties.ContainsKey(key)));
        }
    }

    [Theory]
    [InlineData("{", -32700)]
    [InlineData("[]", -32600)]
    [InlineData("{}", -32600)]
    [InlineData("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"missing\"}", -32601)]
    public async Task InvalidMessagesHaveProtocolErrors(string json, int code)
    {
        var response = await new McpDispatcher(new Backend(), "test").DispatchAsync(json, default);
        Assert.Equal(code, response!["error"]!["code"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("{\"name\":\"guardian_pause\",\"arguments\":{\"paused\":\"yes\"}}")]
    [InlineData("{\"name\":\"guardian_pause\",\"arguments\":{}}")]
    [InlineData("{\"name\":\"debug_logs\",\"arguments\":{\"limit\":501}}")]
    [InlineData("{\"name\":\"guardian_status\",\"arguments\":{\"unknown\":true}}")]
    [InlineData("{\"name\":\"device_set_state\",\"arguments\":{\"deviceId\":\"x\",\"state\":\"oops\"}}")]
    public async Task InvalidArgumentsNeverReachBackend(string parameters)
    {
        var backend = new Backend();
        var response = await new McpDispatcher(backend, "test").DispatchAsync(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\",\"params\":" + parameters + "}", default);
        Assert.Equal(-32602, response!["error"]!["code"]!.GetValue<int>());
        Assert.Equal(0, backend.Calls);
    }

    [Fact]
    public async Task NotificationsDoNotExecuteTools()
    {
        var backend = new Backend();
        var reply = await new McpDispatcher(backend, "test").DispatchAsync(
            """{"jsonrpc":"2.0","method":"tools/call","params":{"name":"recovery_reset"}}""", default);
        Assert.Null(reply);
        Assert.Equal(0, backend.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ToolErrorsAndFailedOperationsAreNotReportedAsSuccess(bool throws)
    {
        var backend = new Backend { Handler = (_, _) => throws ? throw new InvalidOperationException("refused")
            : Task.FromResult<JsonNode?>(new JsonObject { ["success"] = false }) };
        var response = await new McpDispatcher(backend, "test").DispatchAsync(
            """{"jsonrpc":"2.0","id":7,"method":"tools/call","params":{"name":"guardian_status"}}""", default);
        Assert.True(response!["result"]!["isError"]!.GetValue<bool>());
        Assert.NotEmpty(response["result"]!["content"]!.AsArray());
    }
}
