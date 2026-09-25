using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;

namespace NetworkGuardian.Windows.Connectivity;

public sealed record NpcapRuntimeStatus(bool IsAvailable, string Detail, string? Version = null);

public enum NpcapRawProbeVerdict
{
    Unavailable,
    Online,
    Offline,
}

public sealed record NpcapRawProbeResult(
    NpcapRawProbeVerdict Verdict,
    int TcpReplyCount,
    int IcmpReplyCount,
    string Detail);

/// <summary>Optional packet-level proof that a probe actually traversed its selected adapter.</summary>
public sealed class NpcapProbeVerifier : IDisposable
{
    private readonly ILogger _logger;
    private readonly NpcapApi? _api;
    private IReadOnlyDictionary<Guid, string> _devices = new Dictionary<Guid, string>();

    public NpcapProbeVerifier(ILogger<NpcapProbeVerifier> logger)
    {
        _logger = logger;
        try
        {
            _api = NpcapApi.TryLoad(out var failure);
            if (_api is null)
            {
                Status = new NpcapRuntimeStatus(false, failure ?? "未找到 Npcap 运行库");
                _devices = new Dictionary<Guid, string>();
                return;
            }

            RefreshDevices();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Npcap 初始化失败");
            Status = new NpcapRuntimeStatus(false, $"Npcap 初始化失败：{ex.Message}");
            _devices = new Dictionary<Guid, string>();
        }
    }

    public NpcapRuntimeStatus Status { get; private set; } =
        new(false, "Npcap 尚未初始化");

    /// <summary>Re-enumerates capture devices after a network adapter hot-plug event.</summary>
    public NpcapRuntimeStatus RefreshDevices()
    {
        if (_api is null)
        {
            return Status;
        }

        try
        {
            var devices = _api.EnumerateDevices();
            Volatile.Write(ref _devices, devices);
            Status = devices.Count == 0
                ? new NpcapRuntimeStatus(false, "Npcap 已加载，但没有可打开的网络接口", _api.Version)
                : new NpcapRuntimeStatus(true, $"Npcap 可用，可捕获 {devices.Count} 个接口", _api.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Npcap 接口刷新失败");
            Status = new NpcapRuntimeStatus(false, $"Npcap 接口刷新失败：{ex.Message}", _api.Version);
        }

        return Status;
    }

    public NpcapCaptureSession? TryStart(Guid? adapterGuid, string? sourceAddress)
    {
        if (_api is null || !Status.IsAvailable || adapterGuid is not { } guid ||
            string.IsNullOrWhiteSpace(sourceAddress) ||
            !Volatile.Read(ref _devices).TryGetValue(guid, out var device))
        {
            return null;
        }

        try
        {
            return _api.Open(device, sourceAddress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "无法在 Npcap 接口 {Guid} 上启动探测抓包", guid);
            return null;
        }
    }

    public Task<NpcapRawProbeResult> ProbeRawAsync(
        Guid? adapterGuid,
        string? sourceAddress,
        string? sourceMacAddress,
        string? gatewayAddress,
        IReadOnlyList<string> tcpTargets,
        IReadOnlyList<string> icmpTargets,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (_api is null || !Status.IsAvailable || adapterGuid is not { } guid ||
            string.IsNullOrWhiteSpace(sourceAddress) || string.IsNullOrWhiteSpace(sourceMacAddress) ||
            string.IsNullOrWhiteSpace(gatewayAddress) ||
            !Volatile.Read(ref _devices).TryGetValue(guid, out var device))
        {
            return Task.FromResult(new NpcapRawProbeResult(
                NpcapRawProbeVerdict.Unavailable, 0, 0, "缺少 Npcap 接口或二层地址"));
        }

        return Task.Run(
            () => _api.ProbeRaw(device, sourceAddress, sourceMacAddress, gatewayAddress,
                tcpTargets, icmpTargets, timeout, cancellationToken),
            cancellationToken);
    }

    public void Dispose() => _api?.Dispose();

    internal sealed class NpcapApi : IDisposable
    {
        private const int ErrorBufferSize = 256;
        private readonly IntPtr _library;
        private readonly PcapFindAllDevs _findAllDevs;
        private readonly PcapFreeAllDevs _freeAllDevs;
        private readonly PcapOpenLive _openLive;
        private readonly PcapClose _close;
        private readonly PcapNextEx _nextEx;
        private readonly PcapDatalink _datalink;
        private readonly PcapCompile _compile;
        private readonly PcapSetFilter _setFilter;
        private readonly PcapFreeCode _freeCode;
        private readonly PcapSendPacket _sendPacket;

        private NpcapApi(IntPtr library)
        {
            _library = library;
            _findAllDevs = Load<PcapFindAllDevs>("pcap_findalldevs");
            _freeAllDevs = Load<PcapFreeAllDevs>("pcap_freealldevs");
            _openLive = Load<PcapOpenLive>("pcap_open_live");
            _close = Load<PcapClose>("pcap_close");
            _nextEx = Load<PcapNextEx>("pcap_next_ex");
            _datalink = Load<PcapDatalink>("pcap_datalink");
            _compile = Load<PcapCompile>("pcap_compile");
            _setFilter = Load<PcapSetFilter>("pcap_setfilter");
            _freeCode = Load<PcapFreeCode>("pcap_freecode");
            _sendPacket = Load<PcapSendPacket>("pcap_sendpacket");
            var version = Load<PcapLibVersion>("pcap_lib_version")();
            Version = Marshal.PtrToStringAnsi(version);
        }

        public string? Version { get; }

        public static NpcapApi? TryLoad(out string? failure)
        {
            failure = null;
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Npcap", "wpcap.dll"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "wpcap.dll"),
            };

            foreach (var candidate in candidates)
            {
                if (NativeLibrary.TryLoad(candidate, out var library))
                {
                    return new NpcapApi(library);
                }
            }

            failure = "未检测到系统 Npcap。请从 npcap.com 手动安装后重启 NetworkGuardian。";
            return null;
        }

        public IReadOnlyDictionary<Guid, string> EnumerateDevices()
        {
            var error = Marshal.AllocHGlobal(ErrorBufferSize);
            try
            {
                Marshal.WriteByte(error, 0);
                if (_findAllDevs(out var head, error) != 0)
                {
                    throw new InvalidOperationException(Marshal.PtrToStringAnsi(error) ?? "pcap_findalldevs failed");
                }

                var result = new Dictionary<Guid, string>();
                try
                {
                    for (var current = head; current != IntPtr.Zero;)
                    {
                        var item = Marshal.PtrToStructure<PcapIf>(current);
                        var name = Marshal.PtrToStringAnsi(item.Name);
                        if (name is not null && TryReadGuid(name, out var guid))
                        {
                            result[guid] = name;
                        }

                        current = item.Next;
                    }
                }
                finally
                {
                    if (head != IntPtr.Zero)
                    {
                        _freeAllDevs(head);
                    }
                }

                return result;
            }
            finally
            {
                Marshal.FreeHGlobal(error);
            }
        }

        public NpcapCaptureSession Open(string device, string sourceAddress)
        {
            var error = Marshal.AllocHGlobal(ErrorBufferSize);
            try
            {
                Marshal.WriteByte(error, 0);
                var handle = _openLive(device, 96, 0, 50, error);
                if (handle == IntPtr.Zero)
                {
                    throw new InvalidOperationException(Marshal.PtrToStringAnsi(error) ?? "pcap_open_live failed");
                }

                if (_datalink(handle) != 1) // DLT_EN10MB
                {
                    _close(handle);
                    throw new NotSupportedException("Npcap interface does not expose Ethernet frames");
                }

                var filter = $"ip host {sourceAddress}";
                if (_compile(handle, out var program, filter, 1, 0xffffffff) != 0)
                {
                    _close(handle);
                    throw new InvalidOperationException("Npcap filter compilation failed");
                }

                try
                {
                    if (_setFilter(handle, ref program) != 0)
                    {
                        _close(handle);
                        throw new InvalidOperationException("Npcap filter activation failed");
                    }
                }
                finally
                {
                    _freeCode(ref program);
                }

                return new NpcapCaptureSession(handle, sourceAddress, _nextEx, _close);
            }
            finally
            {
                Marshal.FreeHGlobal(error);
            }
        }

        public NpcapRawProbeResult ProbeRaw(
            string device,
            string sourceAddress,
            string sourceMacAddress,
            string gatewayAddress,
            IReadOnlyList<string> tcpTargetTexts,
            IReadOnlyList<string> icmpTargetTexts,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            if (!IPAddress.TryParse(sourceAddress, out var sourceIp) ||
                !IPAddress.TryParse(gatewayAddress, out var gatewayIp) ||
                !TryParseMac(sourceMacAddress, out var sourceMac) ||
                sourceIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                gatewayIp.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            {
                return new NpcapRawProbeResult(
                    NpcapRawProbeVerdict.Unavailable, 0, 0, "源 IPv4、网关或 MAC 地址无效");
            }

            var icmpTargets = icmpTargetTexts
                .Select(text => IPAddress.TryParse(text, out var address) ? address : null)
                .Where(address => address?.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .Cast<IPAddress>()
                .Distinct()
                .Take(4)
                .ToList();
            if (icmpTargets.Count == 0)
            {
                icmpTargets.Add(IPAddress.Parse("223.5.5.5"));
                icmpTargets.Add(IPAddress.Parse("119.29.29.29"));
            }

            var tcpTargets = tcpTargetTexts
                .Select(TryParseTcpTarget)
                .Where(target => target is not null)
                .Cast<RawTcpTarget>()
                .Distinct()
                .Take(8)
                .ToList();
            if (tcpTargets.Count < 2)
            {
                return new NpcapRawProbeResult(
                    NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap 原始 TCP 目标少于两个");
            }

            var error = Marshal.AllocHGlobal(ErrorBufferSize);
            IntPtr handle = IntPtr.Zero;
            try
            {
                Marshal.WriteByte(error, 0);
                handle = _openLive(device, 128, 0, 40, error);
                if (handle == IntPtr.Zero)
                {
                    return new NpcapRawProbeResult(NpcapRawProbeVerdict.Unavailable, 0, 0,
                        Marshal.PtrToStringAnsi(error) ?? "pcap_open_live failed");
                }

                if (_datalink(handle) != 1)
                {
                    return new NpcapRawProbeResult(NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap 接口不是 Ethernet 帧格式");
                }

                if (_compile(handle, out var program, "arp or icmp or tcp", 1, 0xffffffff) != 0)
                {
                    return new NpcapRawProbeResult(NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap 原始探针过滤器编译失败");
                }

                try
                {
                    if (_setFilter(handle, ref program) != 0)
                    {
                        return new NpcapRawProbeResult(NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap 原始探针过滤器启用失败");
                    }
                }
                finally
                {
                    _freeCode(ref program);
                }

                var sourceBytes = sourceIp.GetAddressBytes();
                var gatewayBytes = gatewayIp.GetAddressBytes();
                if (_sendPacket(handle, BuildArpRequest(sourceMac, sourceBytes, gatewayBytes), 42) != 0)
                {
                    return new NpcapRawProbeResult(NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap ARP 请求发送失败");
                }

                var watch = Stopwatch.StartNew();
                byte[]? gatewayMac = null;
                var arpBudget = TimeSpan.FromMilliseconds(Math.Min(500, timeout.TotalMilliseconds / 2));
                var arpRetransmitted = false;
                while (watch.Elapsed < arpBudget && !cancellationToken.IsCancellationRequested)
                {
                    if (!arpRetransmitted && watch.Elapsed >= TimeSpan.FromTicks(arpBudget.Ticks / 2))
                    {
                        arpRetransmitted = true;
                        if (_sendPacket(handle, BuildArpRequest(sourceMac, sourceBytes, gatewayBytes), 42) != 0)
                        {
                            return new NpcapRawProbeResult(
                                NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap ARP 请求重传失败");
                        }
                    }
                    if (TryReadPacket(handle, out var packet) &&
                        TryReadArpReply(packet, gatewayBytes, sourceBytes, out gatewayMac))
                    {
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new NpcapRawProbeResult(
                        NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap 原始探针被取消");
                }

                if (gatewayMac is null)
                {
                    // An ARP timeout can also mean the adapter/driver does not expose injected
                    // frames correctly. Link state already handles a genuinely detached cable, so
                    // degrade instead of turning this ambiguous condition into a false WAN outage.
                    return new NpcapRawProbeResult(
                        NpcapRawProbeVerdict.Unavailable, 0, 0, "物理接口网关未响应原始 ARP；降级到常规探测");
                }

                var identifier = (ushort)RandomNumberGenerator.GetInt32(1, ushort.MaxValue + 1);
                var firstSourcePort = RandomNumberGenerator.GetInt32(49152, 65536 - tcpTargets.Count);
                var tcpProbes = tcpTargets.Select((target, index) => new RawTcpProbe(
                    target,
                    (ushort)(firstSourcePort + index),
                    (uint)RandomNumberGenerator.GetInt32(int.MaxValue),
                    index)).ToList();

                bool SendPublicProbes()
                {
                    for (var index = 0; index < icmpTargets.Count; index++)
                    {
                        var frame = BuildIcmpEcho(sourceMac, gatewayMac, sourceBytes,
                            icmpTargets[index].GetAddressBytes(), identifier, (ushort)(index + 1));
                        if (_sendPacket(handle, frame, frame.Length) != 0) return false;
                    }
                    foreach (var probe in tcpProbes)
                    {
                        var frame = BuildTcpSyn(sourceMac, gatewayMac, sourceBytes,
                            probe.Target.Address.GetAddressBytes(), probe.SourcePort,
                            probe.Target.Port, probe.Sequence);
                        if (_sendPacket(handle, frame, frame.Length) != 0) return false;
                    }
                    return true;
                }

                if (!SendPublicProbes())
                {
                    return new NpcapRawProbeResult(
                        NpcapRawProbeVerdict.Unavailable, 0, 0, "Npcap 原始公网探针发送失败");
                }

                var icmpReplies = new HashSet<string>(StringComparer.Ordinal);
                var tcpReplies = new HashSet<int>();
                var retransmitted = false;
                while (watch.Elapsed < timeout && !cancellationToken.IsCancellationRequested)
                {
                    if (!retransmitted && watch.Elapsed >= TimeSpan.FromTicks(timeout.Ticks * 2 / 3))
                    {
                        retransmitted = true;
                        if (!SendPublicProbes())
                        {
                            return new NpcapRawProbeResult(
                                NpcapRawProbeVerdict.Unavailable, tcpReplies.Count, icmpReplies.Count,
                                "Npcap 原始公网探针重传失败");
                        }
                    }
                    if (!TryReadPacket(handle, out var packet))
                    {
                        continue;
                    }

                    if (TryReadEchoReply(packet, sourceBytes, identifier, out var remote))
                    {
                        icmpReplies.Add(remote);
                    }
                    if (TryReadTcpReply(packet, sourceBytes, tcpProbes, out var probeIndex))
                    {
                        tcpReplies.Add(probeIndex);
                    }
                    if (tcpReplies.Count >= 2 || tcpReplies.Count >= 1 && icmpReplies.Count >= 2)
                    {
                        break;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    return new NpcapRawProbeResult(
                        NpcapRawProbeVerdict.Unavailable, tcpReplies.Count, icmpReplies.Count,
                        "Npcap 原始探针被取消");
                }

                var online = tcpReplies.Count >= 2 || tcpReplies.Count >= 1 && icmpReplies.Count >= 2;
                var tcpResponders = string.Join(",", tcpReplies.Order()
                    .Select(index => $"{tcpProbes[index].Target.Address}:{tcpProbes[index].Target.Port}"));
                return new NpcapRawProbeResult(
                    online ? NpcapRawProbeVerdict.Online : NpcapRawProbeVerdict.Offline,
                    tcpReplies.Count,
                    icmpReplies.Count,
                    online
                        ? $"Npcap 原始公网证据：TCP={tcpReplies.Count}/{tcpTargets.Count} [{tcpResponders}]，ICMP={icmpReplies.Count}/{icmpTargets.Count}"
                        : $"Npcap 原始公网证据不足：TCP={tcpReplies.Count}/2 [{tcpResponders}]，ICMP={icmpReplies.Count}/2");
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    _close(handle);
                }
                Marshal.FreeHGlobal(error);
            }

            bool TryReadPacket(IntPtr pcap, out byte[] packet)
            {
                packet = Array.Empty<byte>();
                var result = _nextEx(pcap, out var headerPointer, out var dataPointer);
                if (result <= 0 || headerPointer == IntPtr.Zero || dataPointer == IntPtr.Zero)
                {
                    return false;
                }
                var header = Marshal.PtrToStructure<PcapHeader>(headerPointer);
                packet = new byte[Math.Min(header.CapturedLength, 128)];
                Marshal.Copy(dataPointer, packet, 0, packet.Length);
                return true;
            }
        }

        internal static byte[] BuildArpRequest(byte[] sourceMac, byte[] sourceIp, byte[] gatewayIp)
        {
            var frame = new byte[42];
            frame.AsSpan(0, 6).Fill(0xff);
            sourceMac.CopyTo(frame, 6);
            WriteUInt16(frame, 12, 0x0806);
            WriteUInt16(frame, 14, 1);
            WriteUInt16(frame, 16, 0x0800);
            frame[18] = 6;
            frame[19] = 4;
            WriteUInt16(frame, 20, 1);
            sourceMac.CopyTo(frame, 22);
            sourceIp.CopyTo(frame, 28);
            gatewayIp.CopyTo(frame, 38);
            return frame;
        }

        internal static byte[] BuildIcmpEcho(byte[] sourceMac, byte[] gatewayMac, byte[] sourceIp,
            byte[] targetIp, ushort identifier, ushort sequence)
        {
            var frame = new byte[42];
            gatewayMac.CopyTo(frame, 0);
            sourceMac.CopyTo(frame, 6);
            WriteUInt16(frame, 12, 0x0800);
            frame[14] = 0x45;
            WriteUInt16(frame, 16, 28);
            WriteUInt16(frame, 18, (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
            WriteUInt16(frame, 20, 0x4000);
            frame[22] = 64;
            frame[23] = 1;
            sourceIp.CopyTo(frame, 26);
            targetIp.CopyTo(frame, 30);
            WriteUInt16(frame, 24, Checksum(frame.AsSpan(14, 20)));
            frame[34] = 8;
            WriteUInt16(frame, 38, identifier);
            WriteUInt16(frame, 40, sequence);
            WriteUInt16(frame, 36, Checksum(frame.AsSpan(34, 8)));
            return frame;
        }

        internal static byte[] BuildTcpSyn(byte[] sourceMac, byte[] gatewayMac, byte[] sourceIp,
            byte[] targetIp, ushort sourcePort, ushort targetPort, uint sequence)
        {
            var frame = new byte[54];
            gatewayMac.CopyTo(frame, 0);
            sourceMac.CopyTo(frame, 6);
            WriteUInt16(frame, 12, 0x0800);
            frame[14] = 0x45;
            WriteUInt16(frame, 16, 40);
            WriteUInt16(frame, 18, (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
            WriteUInt16(frame, 20, 0x4000);
            frame[22] = 64;
            frame[23] = 6;
            sourceIp.CopyTo(frame, 26);
            targetIp.CopyTo(frame, 30);
            WriteUInt16(frame, 24, Checksum(frame.AsSpan(14, 20)));
            WriteUInt16(frame, 34, sourcePort);
            WriteUInt16(frame, 36, targetPort);
            WriteUInt32(frame, 38, sequence);
            frame[46] = 0x50;
            frame[47] = 0x02;
            WriteUInt16(frame, 48, 64240);
            WriteUInt16(frame, 50, TcpChecksum(sourceIp, targetIp, frame.AsSpan(34, 20)));
            return frame;
        }

        private static bool TryReadArpReply(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> gatewayIp,
            ReadOnlySpan<byte> sourceIp, out byte[]? gatewayMac)
        {
            gatewayMac = null;
            if (packet.Length < 42 || ReadUInt16(packet, 12) != 0x0806 || ReadUInt16(packet, 20) != 2 ||
                !packet.Slice(28, 4).SequenceEqual(gatewayIp) ||
                !packet.Slice(38, 4).SequenceEqual(sourceIp))
            {
                return false;
            }
            gatewayMac = packet.Slice(22, 6).ToArray();
            return true;
        }

        private static bool TryReadEchoReply(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> sourceIp,
            ushort identifier, out string remote)
        {
            remote = string.Empty;
            if (packet.Length < 42 || ReadUInt16(packet, 12) != 0x0800)
            {
                return false;
            }
            var ipOffset = 14;
            var ipLength = (packet[ipOffset] & 0x0f) * 4;
            var icmpOffset = ipOffset + ipLength;
            if (ipLength < 20 || packet.Length < icmpOffset + 8 || packet[ipOffset + 9] != 1 ||
                !packet.Slice(ipOffset + 16, 4).SequenceEqual(sourceIp) || packet[icmpOffset] != 0 ||
                ReadUInt16(packet, icmpOffset + 4) != identifier)
            {
                return false;
            }
            remote = new IPAddress(packet.Slice(ipOffset + 12, 4)).ToString();
            return true;
        }

        private static bool TryReadTcpReply(ReadOnlySpan<byte> packet, ReadOnlySpan<byte> sourceIp,
            IReadOnlyList<RawTcpProbe> probes, out int probeIndex)
        {
            probeIndex = -1;
            if (packet.Length < 54 || ReadUInt16(packet, 12) != 0x0800)
            {
                return false;
            }
            const int ipOffset = 14;
            var ipLength = (packet[ipOffset] & 0x0f) * 4;
            var tcpOffset = ipOffset + ipLength;
            if (ipLength < 20 || packet.Length < tcpOffset + 20 || packet[ipOffset + 9] != 6 ||
                !packet.Slice(ipOffset + 16, 4).SequenceEqual(sourceIp))
            {
                return false;
            }
            var flags = packet[tcpOffset + 13];
            if ((flags & 0x04) == 0 && (flags & 0x12) != 0x12)
            {
                return false;
            }
            var remoteAddress = new IPAddress(packet.Slice(ipOffset + 12, 4));
            var remotePort = ReadUInt16(packet, tcpOffset);
            var localPort = ReadUInt16(packet, tcpOffset + 2);
            var match = probes.FirstOrDefault(probe =>
                probe.SourcePort == localPort && probe.Target.Port == remotePort &&
                probe.Target.Address.Equals(remoteAddress));
            if (match is null)
            {
                return false;
            }
            probeIndex = match.Index;
            return true;
        }

        private static RawTcpTarget? TryParseTcpTarget(string value)
        {
            var split = value.LastIndexOf(':');
            return split > 0 && IPAddress.TryParse(value[..split], out var address) &&
                   address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                   ushort.TryParse(value[(split + 1)..], out var port) && port > 0
                ? new RawTcpTarget(address, port)
                : null;
        }

        private static bool TryParseMac(string value, out byte[] bytes)
        {
            var parts = value.Split(':', '-');
            bytes = new byte[6];
            if (parts.Length != 6)
            {
                return false;
            }
            for (var index = 0; index < parts.Length; index++)
            {
                if (!byte.TryParse(parts[index], System.Globalization.NumberStyles.HexNumber, null, out bytes[index]))
                {
                    return false;
                }
            }
            return true;
        }

        internal static ushort Checksum(ReadOnlySpan<byte> data)
        {
            uint sum = 0;
            for (var index = 0; index < data.Length; index += 2)
            {
                sum += (uint)(data[index] << 8 | (index + 1 < data.Length ? data[index + 1] : 0));
            }
            while ((sum >> 16) != 0) sum = (sum & 0xffff) + (sum >> 16);
            return (ushort)~sum;
        }

        internal static ushort TcpChecksum(ReadOnlySpan<byte> sourceIp, ReadOnlySpan<byte> targetIp,
            ReadOnlySpan<byte> tcpSegment)
        {
            var pseudo = new byte[12 + tcpSegment.Length];
            sourceIp.CopyTo(pseudo);
            targetIp.CopyTo(pseudo.AsSpan(4));
            pseudo[9] = 6;
            WriteUInt16(pseudo, 10, (ushort)tcpSegment.Length);
            tcpSegment.CopyTo(pseudo.AsSpan(12));
            return Checksum(pseudo);
        }

        private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
            (ushort)(data[offset] << 8 | data[offset + 1]);

        private static void WriteUInt16(Span<byte> data, int offset, ushort value)
        {
            data[offset] = (byte)(value >> 8);
            data[offset + 1] = (byte)value;
        }

        private static void WriteUInt32(Span<byte> data, int offset, uint value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        private sealed record RawTcpTarget(IPAddress Address, ushort Port);

        private sealed record RawTcpProbe(RawTcpTarget Target, ushort SourcePort, uint Sequence, int Index);

        private T Load<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(_library, name));

        private static bool TryReadGuid(string name, out Guid guid)
        {
            guid = Guid.Empty;
            var open = name.LastIndexOf('{');
            var close = name.LastIndexOf('}');
            return open >= 0 && close > open && Guid.TryParse(name[(open + 1)..close], out guid);
        }

        public void Dispose()
        {
            if (_library != IntPtr.Zero)
            {
                NativeLibrary.Free(_library);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct PcapIf
        {
            public IntPtr Next;
            public IntPtr Name;
            public IntPtr Description;
            public IntPtr Addresses;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PcapHeader
        {
            public int Seconds;
            public int Microseconds;
            public uint CapturedLength;
            public uint OriginalLength;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BpfProgram
        {
            public uint Length;
            public IntPtr Instructions;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PcapFindAllDevs(out IntPtr devices, IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PcapFreeAllDevs(IntPtr devices);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PcapOpenLive(string device, int snapLength, int promiscuous, int timeoutMs, IntPtr error);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void PcapClose(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PcapNextEx(IntPtr handle, out IntPtr header, out IntPtr data);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PcapDatalink(IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PcapCompile(IntPtr handle, out BpfProgram program, string filter, int optimize, uint netmask);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PcapSetFilter(IntPtr handle, ref BpfProgram program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void PcapFreeCode(ref BpfProgram program);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PcapSendPacket(IntPtr handle, byte[] packet, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate IntPtr PcapLibVersion();
    }
}

public sealed class NpcapCaptureSession : IAsyncDisposable
{
    private readonly IntPtr _handle;
    private readonly byte[] _source;
    private readonly NpcapProbeVerifier.NpcapApi.PcapNextEx _next;
    private readonly NpcapProbeVerifier.NpcapApi.PcapClose _close;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _reader;
    private int _closed;

    internal NpcapCaptureSession(
        IntPtr handle,
        string sourceAddress,
        NpcapProbeVerifier.NpcapApi.PcapNextEx next,
        NpcapProbeVerifier.NpcapApi.PcapClose close)
    {
        _handle = handle;
        _source = System.Net.IPAddress.Parse(sourceAddress).GetAddressBytes();
        _next = next;
        _close = close;
        _reader = Task.Run(ReadLoop);
    }

    private volatile bool _sawOutbound;

    private volatile bool _sawInbound;

    private string? _outboundNextHopMac;

    private string? _inboundNextHopMac;

    private int _nonProbePackets;

    public bool SawOutbound => _sawOutbound;

    public bool SawInbound => _sawInbound;

    public bool IsVerified => SawOutbound && SawInbound;

    public bool HasActiveTraffic => Volatile.Read(ref _nonProbePackets) > 0;

    public string? VerifiedNextHopMac => IsVerified &&
        string.Equals(_outboundNextHopMac, _inboundNextHopMac, StringComparison.OrdinalIgnoreCase)
            ? _outboundNextHopMac
            : null;

    private void ReadLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            var result = _next(_handle, out var headerPointer, out var dataPointer);
            if (result <= 0 || headerPointer == IntPtr.Zero || dataPointer == IntPtr.Zero)
            {
                continue;
            }

            var header = Marshal.PtrToStructure<NpcapProbeVerifier.NpcapApi.PcapHeader>(headerPointer);
            var packet = new byte[Math.Min(header.CapturedLength, 96)];
            Marshal.Copy(dataPointer, packet, 0, packet.Length);
            var direction = ClassifyIpv4Direction(packet, _source);
            var isProbeTransport = IsProbeTransport(packet);
            if (direction != CapturedPacketDirection.None && !isProbeTransport)
            {
                Interlocked.Increment(ref _nonProbePackets);
            }

            if (isProbeTransport && direction.HasFlag(CapturedPacketDirection.Outbound))
            {
                _outboundNextHopMac ??= FormatMac(packet.AsSpan(0, 6));
                _sawOutbound = true;
            }

            if (isProbeTransport && direction.HasFlag(CapturedPacketDirection.Inbound))
            {
                _inboundNextHopMac ??= FormatMac(packet.AsSpan(6, 6));
                _sawInbound = true;
            }
        }
    }

    private static string FormatMac(ReadOnlySpan<byte> value) =>
        string.Join(":", value.ToArray().Select(item => item.ToString("X2")));

    internal static bool IsProbeTransport(ReadOnlySpan<byte> packet)
    {
        if (!TryGetIpv4Offset(packet, out var ipOffset))
        {
            return false;
        }

        var ipHeaderLength = (packet[ipOffset] & 0x0f) * 4;
        var transportOffset = ipOffset + ipHeaderLength;
        if (packet.Length < transportOffset + 4)
        {
            return false;
        }

        var protocol = packet[ipOffset + 9];
        var sourcePort = (packet[transportOffset] << 8) | packet[transportOffset + 1];
        var destinationPort = (packet[transportOffset + 2] << 8) | packet[transportOffset + 3];
        return protocol == 6 && (sourcePort is 80 or 443 || destinationPort is 80 or 443) ||
               protocol == 17 && (sourcePort == 53 || destinationPort == 53);
    }

    internal static CapturedPacketDirection ClassifyIpv4Direction(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> source)
    {
        if (source.Length != 4 || !TryGetIpv4Offset(packet, out var ipOffset))
        {
            return CapturedPacketDirection.None;
        }

        var ipHeaderLength = (packet[ipOffset] & 0x0f) * 4;
        if (ipHeaderLength < 20 || packet.Length < ipOffset + ipHeaderLength)
        {
            return CapturedPacketDirection.None;
        }

        var direction = CapturedPacketDirection.None;
        if (packet.Slice(ipOffset + 12, 4).SequenceEqual(source))
        {
            direction |= CapturedPacketDirection.Outbound;
        }

        if (packet.Slice(ipOffset + 16, 4).SequenceEqual(source))
        {
            direction |= CapturedPacketDirection.Inbound;
        }

        return direction;
    }

    private static bool TryGetIpv4Offset(ReadOnlySpan<byte> packet, out int ipOffset)
    {
        ipOffset = 14;
        if (packet.Length < ipOffset + 20)
        {
            return false;
        }

        var etherType = (packet[12] << 8) | packet[13];
        if (etherType is 0x8100 or 0x88a8)
        {
            ipOffset += 4;
            if (packet.Length < ipOffset + 20)
            {
                return false;
            }

            etherType = (packet[16] << 8) | packet[17];
        }

        return etherType == 0x0800;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        await Task.WhenAny(_reader, Task.Delay(250)).ConfigureAwait(false);
        _close(_handle);
        _cts.Dispose();
    }
}

[Flags]
internal enum CapturedPacketDirection
{
    None = 0,
    Outbound = 1,
    Inbound = 2,
}
