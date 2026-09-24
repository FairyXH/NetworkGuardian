using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace NetworkGuardian.Windows.Connectivity;

public sealed record NpcapRuntimeStatus(bool IsAvailable, string Detail, string? Version = null);

/// <summary>Optional packet-level proof that a probe actually traversed its selected adapter.</summary>
public sealed class NpcapProbeVerifier : IDisposable
{
    private readonly ILogger _logger;
    private readonly NpcapApi? _api;
    private readonly IReadOnlyDictionary<Guid, string> _devices;

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

            _devices = _api.EnumerateDevices();
            Status = _devices.Count == 0
                ? new NpcapRuntimeStatus(false, "Npcap 已加载，但没有可打开的网络接口", _api.Version)
                : new NpcapRuntimeStatus(true, $"Npcap 可用，可捕获 {_devices.Count} 个接口", _api.Version);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Npcap 初始化失败");
            Status = new NpcapRuntimeStatus(false, $"Npcap 初始化失败：{ex.Message}");
            _devices = new Dictionary<Guid, string>();
        }
    }

    public NpcapRuntimeStatus Status { get; }

    public NpcapCaptureSession? TryStart(Guid? adapterGuid, string? sourceAddress)
    {
        if (_api is null || !Status.IsAvailable || adapterGuid is not { } guid ||
            string.IsNullOrWhiteSpace(sourceAddress) || !_devices.TryGetValue(guid, out var device))
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
                "wpcap.dll",
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

                var filter = $"ip host {sourceAddress} and (tcp port 80 or tcp port 443 or udp port 53)";
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

    public bool SawOutbound => _sawOutbound;

    public bool SawInbound => _sawInbound;

    public bool IsVerified => SawOutbound && SawInbound;

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
            _sawOutbound |= direction.HasFlag(CapturedPacketDirection.Outbound);
            _sawInbound |= direction.HasFlag(CapturedPacketDirection.Inbound);
        }
    }

    internal static CapturedPacketDirection ClassifyIpv4Direction(
        ReadOnlySpan<byte> packet,
        ReadOnlySpan<byte> source)
    {
        const int ethernetHeaderLength = 14;
        if (source.Length != 4 || packet.Length < ethernetHeaderLength + 20 ||
            packet[12] != 0x08 || packet[13] != 0x00)
        {
            return CapturedPacketDirection.None;
        }

        var ipHeaderLength = (packet[ethernetHeaderLength] & 0x0f) * 4;
        if (ipHeaderLength < 20 || packet.Length < ethernetHeaderLength + ipHeaderLength)
        {
            return CapturedPacketDirection.None;
        }

        var direction = CapturedPacketDirection.None;
        if (packet.Slice(ethernetHeaderLength + 12, 4).SequenceEqual(source))
        {
            direction |= CapturedPacketDirection.Outbound;
        }

        if (packet.Slice(ethernetHeaderLength + 16, 4).SequenceEqual(source))
        {
            direction |= CapturedPacketDirection.Inbound;
        }

        return direction;
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
