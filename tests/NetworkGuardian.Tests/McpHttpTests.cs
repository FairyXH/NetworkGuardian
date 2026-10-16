using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using NetworkGuardian.Infrastructure.Mcp;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class McpHttpTests
{
    private static int Port()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
    private static HttpClient Client(int port) => new() { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
    private static StringContent Body(string json = "{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"ping\"}") => new(json, Encoding.UTF8, "application/json");

    [Fact]
    public async Task LoopbackJsonRpcWorksWithoutAuthenticationAndGetDoesNotPretendToBeSse()
    {
        await using var server = new LoopbackMcpServer(new McpDispatcher(new McpProtocolTests.Backend(), "test"));
        var port = Port();
        await server.ConfigureAsync(true, port);
        using var client = Client(port);
        var reply = await client.PostAsync("/mcp", Body());
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.NotNull(JsonNode.Parse(await reply.Content.ReadAsStringAsync())!["result"]);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.GetAsync("/mcp")).StatusCode);
        var endpoint = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
            .Single(e => e.Port == port);
        Assert.Equal(IPAddress.Loopback, endpoint.Address);
    }

    [Fact]
    public async Task NotificationsReturn202WithNoBody()
    {
        await using var server = new LoopbackMcpServer(new McpDispatcher(new McpProtocolTests.Backend(), "test"));
        var port = Port(); await server.ConfigureAsync(true, port);
        using var client = Client(port);
        var reply = await client.PostAsync("/mcp", Body("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"));
        Assert.Equal(HttpStatusCode.Accepted, reply.StatusCode);
        Assert.Empty(await reply.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("Origin", "https://example.org", 403)]
    [InlineData("Host", "example.org", 403)]
    [InlineData("MCP-Protocol-Version", "invalid", 400)]
    public async Task InvalidOriginHostOrVersionIsRejected(string name, string value, int status)
    {
        await using var server = new LoopbackMcpServer(new McpDispatcher(new McpProtocolTests.Backend(), "test"));
        var port = Port(); await server.ConfigureAsync(true, port);
        using var client = Client(port);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = Body() };
        request.Headers.Add(name, value);
        Assert.Equal(status, (int)(await client.SendAsync(request)).StatusCode);
    }

    [Fact]
    public async Task ChunkedUtf8RequestIsSupported()
    {
        var backend = new McpProtocolTests.Backend();
        await using var server = new LoopbackMcpServer(new McpDispatcher(backend, "test"));
        var port = Port(); await server.ConfigureAsync(true, port);
        using var tcp = new TcpClient(); await tcp.ConnectAsync(IPAddress.Loopback, port);
        var json = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"debug_logs","arguments":{"search":"校园网"}}}""";
        var bytes = Encoding.UTF8.GetBytes(json);
        var stream = tcp.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"POST /mcp HTTP/1.1\r\nHost: 127.0.0.1:{port}\r\nContent-Type: application/json\r\nTransfer-Encoding: chunked\r\n\r\n{bytes.Length:X}\r\n"));
        await stream.WriteAsync(bytes);
        await stream.WriteAsync("\r\n0\r\n\r\n"u8.ToArray());
        using var reader = new StreamReader(stream);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var reply = await reader.ReadToEndAsync(timeout.Token);
        Assert.Contains("200 Response", reply);
        Assert.Contains("structuredContent", reply);
        Assert.Equal(1, backend.Calls);
    }

    [Fact]
    public async Task PortConflictDoesNotBindAnotherAddressOrChooseAnUnexpectedPort()
    {
        var occupied = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        occupied.Start();
        try
        {
            var port = ((IPEndPoint)occupied.LocalEndpoint).Port;
            await using var server = new LoopbackMcpServer(new McpDispatcher(new McpProtocolTests.Backend(), "test"));
            await server.ConfigureAsync(true, port);
            Assert.False(server.IsListening);
            Assert.NotNull(server.LastError);
        }
        finally { occupied.Stop(); }
    }

    [Fact]
    public async Task ToolCanDisableItsOwnListenerAndStillReturnItsResult()
    {
        var backend = new McpProtocolTests.Backend();
        await using var server = new LoopbackMcpServer(new McpDispatcher(backend, "test"));
        var port = Port(); await server.ConfigureAsync(true, port);
        backend.Handler = async (_, _) => { await server.ConfigureAsync(false, port); return new JsonObject { ["success"] = true }; };
        using var client = Client(port);
        var reply = await client.PostAsync("/mcp", Body("""{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"recovery_reset"}}"""));
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        Assert.Contains("structuredContent", await reply.Content.ReadAsStringAsync());
        Assert.False(server.IsListening);
        backend.Handler = null;
        var secondPort = Port(); await server.ConfigureAsync(true, secondPort);
        using var second = Client(secondPort);
        Assert.Equal(HttpStatusCode.OK, (await second.PostAsync("/mcp", Body())).StatusCode);
    }
}
