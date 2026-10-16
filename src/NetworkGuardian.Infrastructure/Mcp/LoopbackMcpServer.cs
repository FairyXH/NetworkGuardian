using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace NetworkGuardian.Infrastructure.Mcp;

/// <summary>Bounded HTTP/1.1 transport, stateless JSON responses; GET SSE is intentionally unsupported.</summary>
public sealed class LoopbackMcpServer(McpDispatcher dispatcher) : IAsyncDisposable
{
    private const int MaxBody = 2 * 1024 * 1024;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly SemaphoreSlim _slots = new(16, 16);
    private readonly ConcurrentDictionary<long, Task> _clients = new();
    private CancellationTokenSource? _acceptStop;
    private TcpListener? _listener;
    private Task? _acceptTask;
    private long _nextId;
    private long _requests;
    public int? BoundPort { get; private set; }
    public string? LastError { get; private set; }
    public long Requests => Interlocked.Read(ref _requests);
    public bool IsListening => BoundPort.HasValue;

    public async Task ConfigureAsync(bool enabled, int port)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (enabled && port == BoundPort) return;
            _acceptStop?.Cancel();
            _listener?.Stop();
            if (_acceptTask is not null) await _acceptTask.ConfigureAwait(false);
            _acceptStop?.Dispose();
            _acceptStop = null;
            _acceptTask = null;
            _listener = null;
            BoundPort = null;
            LastError = null;
            if (!enabled) return;
            try
            {
                _listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                _listener.Start(32);
                BoundPort = port;
                _acceptStop = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                _acceptTask = AcceptAsync(_listener, port, _acceptStop.Token);
            }
            catch (SocketException ex)
            {
                _listener?.Stop();
                _listener = null;
                LastError = $"无法监听 127.0.0.1:{port}（{ex.SocketErrorCode}），请检查端口占用";
            }
        }
        finally { _lifecycle.Release(); }
    }

    private async Task AcceptAsync(TcpListener listener, int port, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await _slots.WaitAsync(token).ConfigureAwait(false);
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false); }
                catch { _slots.Release(); throw; }
                var id = Interlocked.Increment(ref _nextId);
                var task = Task.Run(async () =>
                {
                    try { await HandleAsync(client, port, _shutdown.Token).ConfigureAwait(false); }
                    finally { client.Dispose(); _slots.Release(); }
                }, CancellationToken.None);
                _clients[id] = task;
                _ = task.ContinueWith(_ => { _clients.TryRemove(id, out var ignored); }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task HandleAsync(TcpClient client, int port, CancellationToken shutdown)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var token = timeout.Token;
        await using var stream = client.GetStream();
        await using var input = new BufferedStream(stream, 8192);
        try
        {
            using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            readTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            var requestLine = await ReadLineAsync(input, readTimeout.Token).ConfigureAwait(false);
            var parts = requestLine.Split(' ');
            if (parts.Length != 3 || parts[2] != "HTTP/1.1") { await ReplyAsync(stream, 400, null, token); return; }
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var total = requestLine.Length;
            while (true)
            {
                var line = await ReadLineAsync(input, readTimeout.Token).ConfigureAwait(false);
                if (line.Length == 0) break;
                total += line.Length;
                if (total > 16384) throw new InvalidDataException("Headers too large");
                var colon = line.IndexOf(':');
                if (colon <= 0 || !headers.TryAdd(line[..colon].Trim(), line[(colon + 1)..].Trim()))
                    throw new InvalidDataException("Invalid or duplicate header");
            }
            if (!headers.TryGetValue("Host", out var host) || (host != $"127.0.0.1:{port}" && host != $"localhost:{port}" &&
                    !(port == 80 && host is "127.0.0.1" or "localhost")))
            { await ReplyAsync(stream, 403, null, token); return; }
            if (headers.TryGetValue("Origin", out var origin) &&
                (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme != "http" || uri.Port != port ||
                    (uri.Host != "127.0.0.1" && uri.Host != "localhost")))
            { await ReplyAsync(stream, 403, null, token); return; }
            if (parts[1] == "/health" && parts[0] == "GET")
            { await ReplyAsync(stream, 200, new JsonObject { ["listening"] = IsListening, ["port"] = BoundPort, ["requests"] = Requests }, token); return; }
            if (parts[1] is not "/mcp" and not "/mcp/") { await ReplyAsync(stream, 404, null, token); return; }
            if (parts[0] != "POST") { await ReplyAsync(stream, 405, null, token); return; }
            if (headers.TryGetValue("MCP-Protocol-Version", out var version) && !McpDispatcher.Versions.Contains(version))
            { await ReplyAsync(stream, 400, McpDispatcher.Error(null, -32600, "Unsupported protocol version"), token); return; }
            if (!headers.TryGetValue("Content-Type", out var contentType) || !contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase))
            { await ReplyAsync(stream, 415, null, token); return; }
            if (headers.TryGetValue("Expect", out var expect) && expect.Equals("100-continue", StringComparison.OrdinalIgnoreCase))
            { await stream.WriteAsync("HTTP/1.1 100 Continue\r\n\r\n"u8.ToArray(), token); await stream.FlushAsync(token); }
            var body = await ReadBodyAsync(input, headers, readTimeout.Token).ConfigureAwait(false);
            Interlocked.Increment(ref _requests);
            var response = await dispatcher.DispatchAsync(Encoding.UTF8.GetString(body), token).ConfigureAwait(false);
            await ReplyAsync(stream, response is null ? 202 : 200, response, token).ConfigureAwait(false);
        }
        catch (InvalidDataException ex)
        { try { await ReplyAsync(stream, 400, McpDispatcher.Error(null, -32600, ex.Message), token); } catch (IOException) { } }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
    }

    private static async Task<byte[]> ReadBodyAsync(Stream stream, Dictionary<string, string> headers, CancellationToken token)
    {
        if (headers.TryGetValue("Transfer-Encoding", out var encoding))
        {
            if (!encoding.Equals("chunked", StringComparison.OrdinalIgnoreCase) || headers.ContainsKey("Content-Length"))
                throw new InvalidDataException("Invalid body framing");
            using var body = new MemoryStream();
            while (true)
            {
                var line = await ReadLineAsync(stream, token);
                if (!int.TryParse(line.Split(';')[0], System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var length) || length < 0 || length > MaxBody - body.Length)
                    throw new InvalidDataException("Invalid or oversized chunk");
                if (length == 0)
                {
                    var trailerBytes = 0;
                    while ((line = await ReadLineAsync(stream, token)).Length > 0)
                        if ((trailerBytes += line.Length) > 16384) throw new InvalidDataException("Trailers too large");
                    return body.ToArray();
                }
                var chunk = new byte[length];
                await stream.ReadExactlyAsync(chunk, token);
                await body.WriteAsync(chunk, token);
                if ((await ReadLineAsync(stream, token)).Length != 0) throw new InvalidDataException("Invalid chunk terminator");
            }
        }
        if (!headers.TryGetValue("Content-Length", out var text) || !int.TryParse(text, out var count) || count < 0 || count > MaxBody)
            throw new InvalidDataException("Content-Length required, maximum 2 MiB");
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, token);
        return bytes;
    }

    private static async Task<string> ReadLineAsync(Stream stream, CancellationToken token)
    {
        using var line = new MemoryStream();
        var one = new byte[1];
        while (await stream.ReadAsync(one, token) != 0)
        {
            if (one[0] == '\n')
            {
                var bytes = line.ToArray();
                if (bytes.Length == 0 || bytes[^1] != '\r') throw new InvalidDataException("CRLF required");
                return Encoding.ASCII.GetString(bytes, 0, bytes.Length - 1);
            }
            line.WriteByte(one[0]);
            if (line.Length > 16384) throw new InvalidDataException("Header line too large");
        }
        throw new EndOfStreamException();
    }

    private static async Task ReplyAsync(Stream stream, int code, JsonNode? body, CancellationToken token)
    {
        var bytes = body is null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"HTTP/1.1 {code} Response\r\nContent-Type: application/json; charset=utf-8\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nAllow: POST\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, token);
        await stream.WriteAsync(bytes, token);
        await stream.FlushAsync(token);
    }

    public async ValueTask DisposeAsync()
    {
        await ConfigureAsync(false, BoundPort ?? 18765).ConfigureAwait(false);
        _shutdown.Cancel();
        try { await Task.WhenAll(_clients.Values).ConfigureAwait(false); } catch (OperationCanceledException) { }
        _shutdown.Dispose();
        _slots.Dispose();
        _lifecycle.Dispose();
    }
}
