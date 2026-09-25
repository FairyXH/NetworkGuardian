using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Windows.Connectivity;

/// <summary>
/// Multi-signal Internet probe. A single failing endpoint never declares the Internet down; the
/// verdict needs <c>RequiredSuccessCount</c> successful HTTP/HTTPS attempts against lightweight
/// connectivity pages and common sites. ICMP may cross a campus gateway before authentication and
/// therefore cannot prove usable Internet access.
/// </summary>
public sealed class ConnectivityProbe : IConnectivityProbe, IDisposable
{
    private const SocketOptionName IpUnicastInterface = (SocketOptionName)31;
    private readonly ILogger<ConnectivityProbe> _logger;
    private readonly NpcapProbeVerifier? _npcap;
    private readonly ConcurrentDictionary<string, HttpClient> _httpClients = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ownsClients = true;
    private bool _disposed;

    public ConnectivityProbe(
        ILogger<ConnectivityProbe>? logger = null,
        NpcapProbeVerifier? npcap = null)
    {
        _logger = logger ?? NullLogger<ConnectivityProbe>.Instance;
        _npcap = npcap;
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
            .Where(e => e.Enabled && e.Kind != ProbeKind.Icmp)
            .ToList();
        if (request.Settings.AllowIcmp)
        {
            endpoints.AddRange(request.Settings.PingTargets
                .Where(target => !string.IsNullOrWhiteSpace(target))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select((target, index) => new ProbeEndpointSettings
                {
                    Name = $"Ping-{index + 1}",
                    Kind = ProbeKind.Icmp,
                    Target = target.Trim(),
                    TimeoutMs = request.Settings.PingTimeoutMs,
                }));
        }

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

        var capture = _npcap?.TryStart(request.AdapterGuid, request.SourceAddress);

        var roundTimeout = TimeSpan.FromMilliseconds(Math.Max(500, request.Settings.RoundTimeoutMs));
        using var roundCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        roundCts.CancelAfter(roundTimeout);

        using var throttle = new SemaphoreSlim(Math.Max(1,
            request.MaxConcurrencyOverride ?? request.Settings.MaxConcurrency));
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

        // Consume completions as a race. Once enough strong Internet evidence or a definitive
        // captive-portal interception is observed, cancel slower probes instead of waiting for the
        // round timeout. The remaining tasks still unwind before local resources are disposed.
        var pending = tasks.ToList();
        while (pending.Count > 0 && !roundCts.IsCancellationRequested)
        {
            var completed = await Task.WhenAny(pending).ConfigureAwait(false);
            pending.Remove(completed);
            try
            {
                await completed.ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Individual failures are already captured as attempts.
            }

            var current = attempts.ToArray();
            var verified = current.Count(a => a.Evidence == ProbeEvidence.InternetVerified);
            if (verified >= Math.Max(1, request.Settings.RequiredSuccessCount))
            {
                await roundCts.CancelAsync().ConfigureAwait(false);
                break;
            }
        }

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

        var successCount = attemptList.Count(a => a.Evidence == ProbeEvidence.InternetVerified);
        var required = Math.Max(1, request.Settings.RequiredSuccessCount);
        var captiveAttempt = attemptList.FirstOrDefault(a => a.Outcome == ProbeOutcome.CaptivePortalRedirect);
        var captiveSuspected = captiveAttempt is not null;
        var hasTransport = attemptList.Any(a => a.Evidence == ProbeEvidence.InternetTransport);
        var hasLocal = attemptList.Any(a => a.Evidence == ProbeEvidence.LocalNetwork);
        var isOnline = successCount >= required;
        var reachability = isOnline
            ? InternetReachability.InternetVerified
            : captiveSuspected && request.Settings.TreatCaptivePortalAsOffline
                ? InternetReachability.CaptivePortal
                : hasTransport
                    ? InternetReachability.InternetLikely
                    : hasLocal
                        ? InternetReachability.LocalOnly
                        : InternetReachability.Unknown;

        var captureVerification = request.InterfaceId is null
            ? PacketCaptureVerification.NotRequested
            : _npcap?.Status.IsAvailable != true
                ? PacketCaptureVerification.Unavailable
                : capture is null
                    ? PacketCaptureVerification.CaptureFailed
                    : PacketCaptureVerification.NoTrafficOnTargetInterface;
        string? captureDetail = _npcap?.Status.Detail;
        string? captureNextHopMac = null;
        if (isOnline && request.InterfaceId is not null && _npcap?.Status.IsAvailable == true && capture is null)
        {
            isOnline = false;
            reachability = InternetReachability.InternetLikely;
            captureDetail = "Npcap 可用，但无法打开目标接口验证本轮流量";
        }
        else if (capture is not null && isOnline)
        {
            await Task.Delay(75, CancellationToken.None).ConfigureAwait(false);
            if (capture.IsVerified)
            {
                captureVerification = PacketCaptureVerification.VerifiedOnTargetInterface;
                captureDetail = "Npcap 在目标接口捕获到探测请求和回包";
                captureNextHopMac = capture.VerifiedNextHopMac;
            }
            else
            {
                isOnline = false;
                reachability = InternetReachability.InternetLikely;
                captureDetail = "应用层探测成功，但 Npcap 未在目标接口捕获到完整双向流量";
            }
        }

        if (capture is not null)
        {
            await capture.DisposeAsync().ConfigureAwait(false);
        }

        var report = new ConnectivityProbeReport
        {
            TimestampUtc = started,
            SourceAddress = request.SourceAddress,
            SourceInterfaceId = request.InterfaceId,
            IsOnline = isOnline,
            Reachability = reachability,
            CaptureVerification = captureVerification,
            CaptureVerificationDetail = captureDetail,
            CaptureNextHopMac = captureNextHopMac,
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

        var addresses = await ResolveAsync(host, request, timeout, cancellationToken).ConfigureAwait(false);
        if (addresses.Count == 0)
        {
            return Attempt(endpoint, request, ProbeOutcome.DnsFailure, stopwatch,
                detail: $"could not resolve {host}");
        }

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var socket = CreateBoundSocket(address, request.SourceAddress, request.InterfaceIndex, timeout);
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

        var client = GetClient(
            request.SourceAddress,
            request.InterfaceIndex,
            request.DnsServerAddresses,
            request.Settings);
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

        var addresses = await ResolveAsync(host, request, timeout, cancellationToken).ConfigureAwait(false);
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
        if (!string.IsNullOrWhiteSpace(request.SourceAddress))
        {
            return Attempt(endpoint, request, ProbeOutcome.NotAttempted, stopwatch,
                detail: "ICMP skipped: the managed Ping API cannot guarantee the selected interface");
        }

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

    private static async Task<List<IPAddress>> ResolveAsync(
        string host,
        ProbeRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return new List<IPAddress> { literal };
        }

        if (!string.IsNullOrWhiteSpace(request.SourceAddress))
        {
            return await ResolveBoundDnsAsync(
                    host,
                    request.SourceAddress,
                    request.InterfaceIndex,
                    request.DnsServerAddresses,
                    timeout,
                    cancellationToken)
                .ConfigureAwait(false);
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

    private static Socket? CreateBoundSocket(
        IPAddress target,
        string? sourceAddress,
        uint? interfaceIndex,
        TimeSpan timeout)
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
                PinSocketToInterface(socket, source, interfaceIndex);
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

    private HttpClient GetClient(
        string? sourceAddress,
        uint? interfaceIndex,
        IReadOnlyList<string> dnsServerAddresses,
        ProbeSettings settings)
    {
        var dnsKey = string.Join(",", dnsServerAddresses);
        var key = $"{sourceAddress ?? "any"}|{interfaceIndex?.ToString() ?? "any"}|{dnsKey}|{settings.DetectCaptivePortalRedirects}|{settings.UserAgent}";

        return _httpClients.GetOrAdd(key, _ =>
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = !settings.DetectCaptivePortalRedirects,
                ConnectTimeout = TimeSpan.FromMilliseconds(Math.Max(500, settings.TimeoutMs)),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                MaxConnectionsPerServer = 4,
                AutomaticDecompression = DecompressionMethods.None,
                // Connectivity probes must represent the selected adapter itself. A system proxy
                // (including a local traffic aggregator) would otherwise collapse every adapter
                // onto the proxy's route and make their results change together.
                UseProxy = false,
            };

            if (!string.IsNullOrWhiteSpace(sourceAddress) && IPAddress.TryParse(sourceAddress, out var source))
            {
                handler.ConnectCallback = async (context, token) =>
                {
                    var addresses = await ResolveBoundDnsAsync(
                            context.DnsEndPoint.Host,
                            sourceAddress,
                            interfaceIndex,
                            dnsServerAddresses,
                            TimeSpan.FromMilliseconds(Math.Max(500, settings.TimeoutMs)),
                            token)
                        .ConfigureAwait(false);
                    Exception? lastError = null;

                    foreach (var address in addresses.Where(address => address.AddressFamily == source.AddressFamily))
                    {
                        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                        try
                        {
                            PinSocketToInterface(socket, source, interfaceIndex);
                            socket.Bind(new IPEndPoint(source, 0));
                            await socket.ConnectAsync(
                                    new IPEndPoint(address, context.DnsEndPoint.Port), token)
                                .ConfigureAwait(false);
                            return new NetworkStream(socket, ownsSocket: true);
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            socket.Dispose();
                        }
                    }

                    throw new HttpRequestException(
                        $"No address for {context.DnsEndPoint.Host} was reachable on interface {interfaceIndex}",
                        lastError);
                };
            }

            return new HttpClient(handler, disposeHandler: true)
            {
                Timeout = Timeout.InfiniteTimeSpan, // cancellation is handled per request
            };
        });
    }

    private static async Task<List<IPAddress>> ResolveBoundDnsAsync(
        string host,
        string sourceAddress,
        uint? interfaceIndex,
        IReadOnlyList<string> dnsServerAddresses,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal))
        {
            return new List<IPAddress> { literal };
        }

        if (!IPAddress.TryParse(sourceAddress, out var source) || source.AddressFamily != AddressFamily.InterNetwork)
        {
            return new List<IPAddress>();
        }

        var queryId = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue + 1);
        var query = BuildDnsQuery(host, queryId);

        foreach (var serverText in dnsServerAddresses)
        {
            if (!IPAddress.TryParse(serverText, out var server) || server.AddressFamily != source.AddressFamily)
            {
                continue;
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);
            using var udp = new UdpClient(source.AddressFamily);
            try
            {
                PinSocketToInterface(udp.Client, source, interfaceIndex);
                udp.Client.Bind(new IPEndPoint(source, 0));
                udp.Connect(server, 53);
                await udp.SendAsync(query, timeoutCts.Token).ConfigureAwait(false);
                var response = await udp.ReceiveAsync(timeoutCts.Token).ConfigureAwait(false);
                var addresses = ParseDnsAddresses(response.Buffer, queryId);
                if (addresses.Count > 0)
                {
                    return addresses;
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Try the next DNS server assigned to this same interface.
            }
        }

        return new List<IPAddress>();
    }

    private static byte[] BuildDnsQuery(string host, ushort queryId)
    {
        using var stream = new MemoryStream();
        WriteUInt16(stream, queryId);
        WriteUInt16(stream, 0x0100); // recursion desired
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        foreach (var label in host.TrimEnd('.').Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            stream.WriteByte((byte)bytes.Length);
            stream.Write(bytes);
        }

        stream.WriteByte(0);
        WriteUInt16(stream, 1); // A
        WriteUInt16(stream, 1); // IN
        return stream.ToArray();
    }

    private static List<IPAddress> ParseDnsAddresses(byte[] response, ushort queryId)
    {
        var result = new List<IPAddress>();
        if (response.Length < 12 || ReadUInt16(response, 0) != queryId ||
            (ReadUInt16(response, 2) & 0x800F) != 0x8000)
        {
            return result;
        }

        var questionCount = ReadUInt16(response, 4);
        var answerCount = ReadUInt16(response, 6);
        var offset = 12;
        for (var index = 0; index < questionCount; index++)
        {
            offset = SkipDnsName(response, offset);
            offset += 4;
            if (offset > response.Length)
            {
                return result;
            }
        }

        for (var index = 0; index < answerCount && offset < response.Length; index++)
        {
            offset = SkipDnsName(response, offset);
            if (offset + 10 > response.Length)
            {
                break;
            }

            var type = ReadUInt16(response, offset);
            var dataLength = ReadUInt16(response, offset + 8);
            offset += 10;
            if (offset + dataLength > response.Length)
            {
                break;
            }

            if (type == 1 && dataLength == 4)
            {
                result.Add(new IPAddress(response.AsSpan(offset, 4)));
            }

            offset += dataLength;
        }

        return result;
    }

    private static int SkipDnsName(byte[] message, int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset++];
            if (length == 0)
            {
                return offset;
            }

            if ((length & 0xC0) == 0xC0)
            {
                return offset + 1;
            }

            offset += length;
        }

        return message.Length + 1;
    }

    private static ushort ReadUInt16(byte[] value, int offset) =>
        (ushort)((value[offset] << 8) | value[offset + 1]);

    private static void WriteUInt16(Stream stream, ushort value)
    {
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }

    private static void PinSocketToInterface(Socket socket, IPAddress source, uint? interfaceIndex)
    {
        if (interfaceIndex is not > 0 || source.AddressFamily != AddressFamily.InterNetwork)
        {
            return;
        }

        socket.SetSocketOption(
            SocketOptionLevel.IP,
            IpUnicastInterface,
            IPAddress.HostToNetworkOrder(unchecked((int)interfaceIndex.Value)));
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
        Evidence = ClassifyEvidence(endpoint, outcome),
    };

    private static ProbeEvidence ClassifyEvidence(ProbeEndpointSettings endpoint, ProbeOutcome outcome)
    {
        if (outcome == ProbeOutcome.CaptivePortalRedirect)
        {
            return ProbeEvidence.CaptivePortal;
        }

        if (outcome != ProbeOutcome.Success)
        {
            return ProbeEvidence.None;
        }

        return endpoint.Kind switch
        {
            ProbeKind.Https => ProbeEvidence.InternetVerified,
            ProbeKind.Http when !string.IsNullOrEmpty(endpoint.BodyMarker) => ProbeEvidence.InternetVerified,
            ProbeKind.Http when endpoint.ExpectedStatusMin == 204 && endpoint.ExpectedStatusMax == 204 =>
                ProbeEvidence.InternetVerified,
            ProbeKind.Http or ProbeKind.Tcp => ProbeEvidence.InternetTransport,
            ProbeKind.Dns or ProbeKind.Icmp => ProbeEvidence.LocalNetwork,
            _ => ProbeEvidence.None,
        };
    }

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
