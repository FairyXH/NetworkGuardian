using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Windows.Wlan;
using Xunit;
using Xunit.Abstractions;

namespace NetworkGuardian.Tests;

/// <summary>
/// Establishes, with measurements instead of assumptions, how a profile written for the current user is
/// seen per adapter: through the WLAN API (<c>WlanGetProfile</c>) and through
/// <c>netsh wlan show profiles interface=...</c>. It exists because a leftover test profile made the two
/// views look contradictory; the answer decides how cleanup has to be written and what the docs may claim.
/// </summary>
/// <remarks>Opt-in (<c>NETWORKGUARDIAN_WIFI_HARDWARE_TESTS=1</c>). Writes one throwaway profile and removes
/// it from every adapter at the end.</remarks>
[Collection(WifiHardwareCollection.Name)]
public sealed class WifiProfileVisibilityExploration
{
    private const string HardwareTestVariable = "NETWORKGUARDIAN_WIFI_HARDWARE_TESTS";

    private readonly ITestOutputHelper _output;

    public WifiProfileVisibilityExploration(ITestOutputHelper output) => _output = output;

    [Fact]
    public void WhereDoesAWrittenProfileShowUp()
    {
        if (Environment.GetEnvironmentVariable(HardwareTestVariable) is not ("1" or "true" or "TRUE"))
        {
            _output.WriteLine($"skipped: set {HardwareTestVariable}=1 to run against the real WLAN service");
            return;
        }

        var profileName = $"NG-VIS-{Guid.NewGuid():N}"[..20];
        using var wifi = new NativeWifiManager();
        var applier = new WifiProfileApplier(wifi, NullLogger<WifiProfileApplier>.Instance);
        var adapters = wifi.GetAdapters();
        _output.WriteLine($"probe profile: {profileName}, adapters: {adapters.Count}");

        var credential = new WifiNetworkCredential
        {
            Ssid = profileName,
            Identity = "visibility@example.invalid",
            Password = "not-a-real-password",
            ConnectAutomatically = false,
        };

        try
        {
            var apply = applier.Apply(adapters[0].InterfaceGuid, credential, allowWrite: true);
            _output.WriteLine($"applied to {adapters[0].Description}: success={apply.Success} failure={apply.Failure ?? "-"}");
            Assert.True(apply.Success, apply.Failure);

            var afterWrite = Report(profileName, "immediately after writing to adapter 0");

            // Immediately after the write only the target adapter reports the profile. This is why the
            // delayed view has to be measured as well: the per-user profile store is machine wide and the
            // other interfaces catch up asynchronously (the connect test, which spends ~90 s connected to
            // the service, ends up with the profile visible on all three).
            Assert.True(afterWrite[adapters[0].InterfaceGuid]);
            Assert.Single(afterWrite, pair => pair.Value);

            Thread.Sleep(TimeSpan.FromSeconds(5));
            Report(profileName, "5 seconds later (propagation through the user profile store)");
        }
        finally
        {
            var removed = applier.RemoveEverywhere(profileName);
            _output.WriteLine($"removed: {removed.Describe()}");
            var afterRemove = Report(profileName, "after RemoveEverywhere");
            Assert.DoesNotContain(afterRemove, pair => pair.Value);

            Assert.Empty(wifi.GetAdapters()
                .Where(a => wifi.GetProfileXml(a.InterfaceGuid, profileName) is not null)
                .Select(a => a.Description));
        }
    }

    /// <summary>Per adapter: is that adapter known to carry the profile (WLAN API and netsh agree)?</summary>
    private Dictionary<Guid, bool> Report(string profileName, string heading)
    {
        using var wifi = new NativeWifiManager();
        _output.WriteLine($"--- {heading}");
        var netshNames = NetshInterfaceNames();
        var seen = new Dictionary<Guid, bool>();

        foreach (var adapter in wifi.GetAdapters())
        {
            var viaApi = wifi.GetProfileXml(adapter.InterfaceGuid, profileName) is not null;
            var name = netshNames.TryGetValue(adapter.InterfaceGuid, out var found) ? found : null;
            var viaNetsh = name is not null && NetshSees(name, profileName);
            seen[adapter.InterfaceGuid] = viaApi || viaNetsh;
            _output.WriteLine($"    {adapter.Description,-45} ({name ?? "?"}) wlanapi={viaApi} netsh={viaNetsh}");
        }

        return seen;
    }

    /// <summary>Maps WLAN interface GUIDs to the connection names netsh uses ("WLAN 3").</summary>
    private static Dictionary<Guid, string> NetshInterfaceNames()
    {
        var start = new ProcessStartInfo("netsh", "wlan show interfaces")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start);
        var result = new Dictionary<Guid, string>();
        if (process is null)
        {
            return result;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);

        string? name = null;
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Contains("Name", StringComparison.Ordinal) && line.Contains(':'))
            {
                name = line[(line.IndexOf(':') + 1)..].Trim();
            }
            else if (line.Contains("GUID", StringComparison.OrdinalIgnoreCase) && line.Contains(':') && name is not null)
            {
                var guidText = line[(line.IndexOf(':') + 1)..].Trim().Trim('{', '}');
                if (Guid.TryParse(guidText, out var guid))
                {
                    result[guid] = name;
                }
            }
        }

        return result;
    }

    private static bool NetshSees(string interfaceName, string profileName)
    {
        var start = new ProcessStartInfo("netsh", $"wlan show profiles interface=\"{interfaceName}\"")
        {
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(10_000);
        return output.Contains(profileName, StringComparison.OrdinalIgnoreCase);
    }
}
