using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Windows.Connectivity;

/// <summary>
/// Multi-signal Internet probe. A single failing endpoint never declares the Internet down; the
/// verdict needs <c>RequiredSuccessCount</c> successes, and an intercepted HTTP response is reported
/// as a captive portal instead of a plain failure.
/// </summary>
public sealed class ConnectivityProbe : IConnectivityProbe, IDisposable
{
    private readonly ILogger<ConnectivityProbe> _logger;
    private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ownsClients = true;
    private bool _disposed;

    public ConnectivityProbe(ILogger<ConnectivityProbe>? logger = null)
    {
        _logger = logger ?? NullLogger<ConnectivityProbe>.Instance;
    }

    public async Task<ConnectivityProbeReport> ProbeAsync(ProbeRequest request, CancellationToken cancellationToken)
    {
        var started = DateTimeOffset.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        if (!request.Settings.Enabled)
        {
            return new ConnectivityProbeReport
            {
                TimestampUtc = started,
                SourceAddress = request.SourceAddress,
                SourceInterfaceId = request.InterfaceId,
                IsOnline = false,
                RequiredSuccessCount = 1,
                AttemptCount = 0,
                CaptivePortalInterceptedBy = "probing-disabled",
            };
        }

        var endpoints = request.Endpoints
            .Where(e => e.Enabled)
            .Where(e => e.Kind != ProbeKind.Icmp || request.Settings.AllowIcmp)
            .ToList();

        if (endpoints.Count == 0)
        {
            return new ConnectivityProbeReport
            {
                TimestampUtc = started,
                SourceAddress = request.SourceAddress,
                SourceInterfaceId = request.InterfaceId,
                IsOnline = false,
                RequiredSuccessCount = 1,
                AttemptCount = 0,
                CaptivePortalInterceptedBy = "no-enabled-endpoints",
            };
        }

        var roundTimeout = TimeSpan.FromMilliseconds(Math.Max(500, request.Settings.RoundTimeoutMs));
        using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        roundCts.CancelAfter(roundTimeout);

        using var throttle = new SemaphoreSlim(Math.Max(1, request.Settings.MaxConcurrency));
        var attempts = new ConcurrentBag<ProbeAttemptResult>();

        var tasks = endpoints.Select(async endpoint =>
        {
            await throttle.WaitAsync(roundCts.Token).ConfigureAwait(false);
            try
            {
                var attempt = await RunAttemptAsync(endpoint, request, roundCts.Token).ConfigureAwait(false);
                attempts.Add(attempt);
            }
            catch (OperationCanceledException)
            {
                attempts.Add(new ProbeAttemptResult
                {
                    EndpointName = endpoint.Name,
                    Kind = endpoint.Kind,
                    Target = endpoint.Target,
                    SourceAddress = request.SourceAddress,
                    Outcome = ProbeOutcome.Timeout,
                    Detail = "probe round was cancelled or exceeded the round timeout",
                });
            }
            catch (Exception ex)
            {
                attempts.Add(new ProbeAttemptResult
                {
                    EndpointName = endpoint.Name,
                    Kind = endpoint.Kind,
                    Target = endpoint.Target,
                    SourceAddress = request.SourceAddress,
                    Outcome = ProbeOutcome.UnknownFailure,
                    Detail = DescribeFailure(ex),
                });
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Individual failures are already captured as attempts.
        }

        stopwatch.Stop();
        var attemptList = attempts
            .OrderBy(a => a.EndpointName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var successCount = attemptList.Count(a => a.IsSuccess);
        var required = Math.Max(1, request.Settings.RequiredSuccessCount);
        var captiveAttempt = attemptList.FirstOrDefault(a => a.Outcome == ProbeOutcome.CaptivePortalRedirect);
        var captiveSuspected = captiveAttempt is not null;

        var isOnline = successCount >= required;
        if (captiveSuspected && request.Settings.TreatCaptivePortalAsOffline)
        {
            isOnline = false;
        }

        var report = new ConnectivityProbeReport
        {
            TimestampUtc = started,
            SourceAddress = request.SourceAddress,
            SourceInterfaceId = request.InterfaceId,
            IsOnline = isOnline,
            CaptivePortalSuspected = captiveSuspected,
            CaptivePortalInterceptedBy = captiveAttempt is null
                ? null
                : $"{captiveAttempt.EndpointName} -> {captiveAttempt.RedirectLocation ?? "intercepted response"}",
            SuccessCount = successCount,
            AttemptCount = attemptList.Count,
            RequiredSuccessCount = required,
            Duration = stopwatch.Elapsed,
            Attempts = attemptList,
        };

        if (_logger.IsEnabled(LogLevel.Debug) || !isOnline)
        {
            _logger.Log(isOnline ? LogLevel.Debug : LogLevel.Information,
                "Connectivity probe on {Source}: {Summary} in {Elapsed:F0}ms",
                request.SourceAddress ?? "default",
                report.Summary,
                stopwatch.Elapsed.TotalMilliseconds);
        }

        return report;
    }

    private async Task<ProbeAttemptResult> RunAttemptAsync(
        ProbeEndpointSettings endpoint,
        ProbeRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var timeout = TimeSpan.FromMilliseconds(
            endpoint.TimeoutMs ?? request.Settings.TimeoutMs);

        try
        {
            switch (endpoint.Kind)
            {
                case ProbeKind.Tcp:
                    return await TcpProbeAsync(endpoint, request, timeout, stopwatch, cancellationToken)
                        .ConfigureAwait(false);
                case ProbeKind.Http:
                case ProbeKind.Https:
                    return await HttpProbeAsync(endpoint, request, timeout, stopwatch, cancellationToken)
                        .ConfigureAwait(false);
                case ProbeKind.Dns:
                    return await DnsProbeAsync(endpoint, request, timeout, stopwatch, cancellationToken)
                        .ConfigureAwait(false);
                case ProbeKind.Icmp:
                    return await IcmpProbeAsync(endpoint, request, timeout, stopwatch, cancellationToken)
                        .ConfigureAwait(false);
                default:
                    return Attempt(endpoint, request, ProbeOutcome.NotAttempted, stopwatch,
                        detail: "unsupported probe kind");
            }
        }
        catch (OperationCanceledException)
        {
            return Attempt(endpoint, request, ProbeOutcome.Timeout, stopwatch,
                detail: $"timed out after {timeout.TotalMilliseconds:F0}ms");
        }
        catch (SocketException ex)
        {
            var outcome = ex.SocketErrorCode switch
            {
                SocketError.TimedOut => ProbeOutcome.Timeout,
                SocketError.ConnectionRefused => ProbeOutcome.ConnectionRefused,
                SocketError.HostUnreachable or SocketError.NetworkUnreachable => ProbeOutcome.HostUnreachable,
                SocketError.AccessDenied => ProbeOutcome.AccessDenied,
                SocketError.HostNotFound => ProbeOutcome.DnsFailure,
                _ => ProbeOutcome.UnknownFailure,
            };

            return Attempt(endpoint, request, outcome, stopwatch,
                detail: $"SocketException {ex.SocketErrorCode}");
        }
    }

    private async Task<ProbeAttemptResult> TcpProbeAsync(
        ProbeEndpointSettings endpoint,
        ProbeRequest request,
        TimeSpan timeout,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var (host, port) = ParseTcpTarget(endpoint.Target);
        if (host is null || port <= 0)
        {
            return Attempt(endpoint, request, ProbeOutcome.NotAttempted, stopwatch,
                detail: $"target '{endpoint.Target}' is not host:port");
        }

        var addresses = await ResolveAsync(host, timeout, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            return Attempt(endpoint, request, ProbeOutcome.DnsFailure, stopwatch,
                detail: $"could not resolve {host}");
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var socket = CreateBoundSocket(address, request.SourceAddress, timeout);
            if (socket is null)
            {
                return Attempt(endpoint, request, ProbeOutcome.NotAttempted, stopwatch,
                    detail: $"source address {request.SourceAddress} is not usable for this address family");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, port), timeoutCts.Token).ConfigureAwait(false);
                stopwatch.Stop();
                return Attempt(endpoint, request, ProbeOutcome.Success, stopwatch,
                    detail: $"connected to {address}:{port}");
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        stopwatch.Stop();
        return Attempt(endpoint, request, ProbeOutcome.Timeout, stopwatch,
            detail: lastError is null ? "no address succeeded" : $"{lastError.GetType().Name}: {lastError.Message}");
    }

    private async Task<ProbeAttemptResult> HttpProbeAsync(
        ProbeEndpointSettings endpoint,
        ProbeRequest request,
        TimeSpan timeout,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(NormalizeUri(endpoint.Target, endpoint.Kind), UriKind.Absolute, out var uri))
        {
            return Attempt(endpoint, request, ProbeOutcome.NotAttempted, stopwatch,
                detail: $"target '{endpoint.Target}' is not an absolute URI");
        }

        var client = GetClient(request.SourceAddress, request.Settings);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        using var message = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrWhiteSpace(request.Settings.UserAgent))
        {
            message.Headers.TryAddWithoutValidation("User-Agent", request.Settings.UserAgent);
        }

        try
        {
            using var response = await client.SendAsync(
                message,
                HttpCompletionOption.ResponseContentRead,
                timeoutCts.Token).ConfigureAwait(false);

            var status = (int)response.StatusCode;

            if (status is >= 300 and < 400 && request.Settings.DetectCaptivePortalRedirects)
            {
                var location = response.Headers.Location?.ToString();
                var sameHost = response.Headers.Location is { } target &&
                               string.Equals(target.Host, uri.Host, StringComparison.OrdinalIgnoreCase);

                if (!sameHost)
                {
                    stopwatch.Stop();
                    return Attempt(endpoint, request, ProbeOutcome.CaptivePortalRedirect, stopwatch,
                        statusCode: status,
                        redirect: location,
                        detail: $"HTTP {status} redirected to {location}");
                }
            }

            if (status < endpoint.ExpectedStatusMin || status > endpoint.ExpectedStatusMax)
            {
                stopwatch.Stop();
                return Attempt(endpoint, request, ProbeOutcome.UnexpectedHttpStatus, stopwatch,
                    statusCode: status,
                    detail: $"HTTP {status} outside the expected {endpoint.ExpectedStatusMin}-{endpoint.ExpectedStatusMax} range");
            }

            if (!string.IsNullOrEmpty(endpoint.BodyMarker))
            {
                var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
                if (!body.Contains(endpoint.BodyMarker, StringComparison.OrdinalIgnoreCase))
                {
                    stopwatch.Stop();
                    return Attempt(endpoint, request, ProbeOutcome.CaptivePortalRedirect, stopwatch,
                        statusCode: status,
                        redirect: response.RequestMessage?.RequestUri?.ToString(),
                        detail: $"HTTP {status} answered but the expected marker was missing " +
                                "(a portal or proxy is intercepting the request)");
                }
            }

            stopwatch.Stop();
            return Attempt(endpoint, request, ProbeOutcome.Success, stopwatch,
                statusCode: status,
                detail: $"HTTP {status}");
        }
        catch (TaskCanceledException)
        {
            stopwatch.Stop();
            return Attempt(endpoint, request, ProbeOutcome.Timeout, stopwatch,
                detail: $"HTTP 请求超时（>{timeout.TotalMilliseconds:F0}ms）");
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var outcome = ex.InnerException is SocketException socket
                ? socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => ProbeOutcome.ConnectionRefused,
                    SocketError.HostUnreachable or SocketError.NetworkUnreachable => ProbeOutcome.HostUnreachable,
                    _ => ProbeOutcome.UnknownFailure,
                }
                : ProbeOutcome.UnknownFailure;

            return Attempt(endpoint, request, outcome, stopwatch, detail: DescribeFailure(ex));
        }
    }

    /// <summary>
    /// Turns a probe exception into a short, stable description for the UI. Raw exception text is
    /// English, version specific and often multi-line, so it stays in the log instead of the status
    /// card.
    /// </summary>
    private static string DescribeFailure(Exception exception) => exception switch
    {
        TaskCanceledException or TimeoutException or OperationCanceledException => "请求超时",
        HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "TLS 握手失败",
        HttpRequestException { InnerException: System.Net.Sockets.SocketException socket } =>
            $"连接失败（{socket.SocketErrorCode}）",
        HttpRequestException => "HTTP 请求失败",
        System.Net.Sockets.SocketException socket => $"套接字错误（{socket.SocketErrorCode}）",
        _ => "探测失败",
    };

    private async Task<ProbeAttemptResult> DnsProbeAsync(
        ProbeEndpointSettings endpoint,
        ProbeRequest request,
        TimeSpan timeout,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var host = endpoint.Target.Split(':', '/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(host))
        {
            return Attempt(endpoint, request, ProbeOutcome.NotAttempted, stopwatch, detail: "no host to resolve");
        }

        var addresses = await ResolveAsync(host, timeout, cancellationToken).ConfigureAwait(false);
        stopwatch.Stop();

        return addresses.Count == 0
            ? Attempt(endpoint, request, ProbeOutcome.DnsFailure, stopwatch, detail: $"no address for {host}")
            : Attempt(endpoint, request, ProbeOutcome.Success, stopwatch,
                detail: $"{host} -> {addresses[0]}");
    }

    private async Task<ProbeAttemptResult> IcmpProbeAsync(
        ProbeEndpointSettings endpoint,
        ProbeRequest request,
        TimeSpan timeout,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var target = string.IsNullOrWhiteSpace(endpoint.Target) ? request.GatewayAddress : endpoint.Target;
        if (string.IsNullOrWhiteSpace(target))
        {
            return Attempt(endpoint, request, ProbeOutcome.NotAttempted, stopwatch,
                detail: "no ICMP target and no gateway available");
        }

        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(target, (int)timeout.TotalMilliseconds).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            stopwatch.Stop();

            return reply.Status == IPStatus.Success
                ? Attempt(endpoint, request, ProbeOutcome.Success, stopwatch,
                    detail: $"{target} replied in {reply.RoundtripTime}ms")
                : Attempt(endpoint, request, ProbeOutcome.UnknownFailure, stopwatch,
                    detail: $"{target} replied with {reply.Status}");
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            return Attempt(endpoint, request, ProbeOutcome.UnknownFailure, stopwatch,
                detail: $"ICMP to {target} failed: {ex.Message}");
        }
    }

    private static async Task<List<IPAddress>> ResolveAsync(string host, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return new List<IPAddress> { literal };
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false);
            return addresses
                .OrderByDescending(a => a.AddressFamily == AddressFamily.InterNetwork)
                .ToList();
        }
        catch (Exception)
        {
            return new List<IPAddress>();
        }
    }

    private static Socket? CreateBoundSocket(IPAddress target, string? sourceAddress, TimeSpan timeout)
    {
        var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true,
        };

        if (!string.IsNullOrWhiteSpace(sourceAddress))
        {
            if (!IPAddress.TryParse(sourceAddress, out var source) || source.AddressFamily != target.AddressFamily)
            {
                socket.Dispose();
                return null;
            }

            try
            {
                socket.Bind(new IPEndPoint(source, 0));
            }
            catch (SocketException)
            {
                socket.Dispose();
                return null;
            }
        }

        _ = timeout;
        return socket;
    }

    private HttpClient GetClient(string? sourceAddress, ProbeSettings settings)
    {
        var key = $"{sourceAddress ?? "any"}|{settings.DetectCaptivePortalRedirects}|{settings.UserAgent}";

        return _httpClients.GetOrAdd(key, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = !settings.DetectCaptivePortalRedirects,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(500, settings.TimeoutMs)),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 4,
                AutomaticDecompression = DecompressionMethods.None,
            };

            if (!string.IsNullOrWhiteSpace(sourceAddress) && IPAddress.TryParse(sourceAddress, out var source))
            {
                handler.ConnectCallback = async (context, token) =>
                {
                    var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        socket.Bind(new IPEndPoint(source, 0));
                        await socket.ConnectAsync(context.DnsEndPoint, token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                };
            }

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // cancellation is handled per request
            };
        });
    }

    private static (string? Host, int Port) ParseTcpTarget(string target)
    {
        var value = target.Trim();
        if (value.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return (uri.Host, uri.Port > 0 ? uri.Port : 443);
        }

        var parts = value.Split(':', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out var port))
        {
            return (parts[0], port);
        }

        return parts.Length == 1 ? (parts[0], 80) : (null, 0);
    }

    private static string NormalizeUri(string target, ProbeKind kind)
    {
        if (target.Contains("://", StringComparison.Ordinal))
        {
            return target;
        }

        var scheme = kind == ProbeKind.Https ? "https" : "http";
        return $"{scheme}://{target}";
    }

    private static ProbeAttemptResult Attempt(
        ProbeEndpointSettings endpoint,
        ProbeRequest request,
        ProbeOutcome outcome,
        Stopwatch stopwatch,
        int? statusCode = null,
        string? redirect = null,
        string? detail = null) => new()
    {
        EndpointName = endpoint.Name,
        Kind = endpoint.Kind,
        Target = endpoint.Target,
        SourceAddress = request.SourceAddress,
        Outcome = outcome,
        HttpStatusCode = statusCode,
        RedirectLocation = redirect,
        Detail = detail,
        Duration = stopwatch.Elapsed,
    };

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_ownsClients)
        {
            foreach (var client in _httpClients.Values)
            {
                client.Dispose();
            }
        }

        _httpClients.Clear();
    }
}
