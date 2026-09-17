using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

/// <summary>
/// Virtual adapters are up with a default gateway on most machines (VMware, Hyper-V, WSL, Bluetooth
/// PAN), so a non-physical interface must never be mistaken for the campus Ethernet link: doing so
/// would run the authentication client against a link that cannot possibly authenticate.
/// </summary>
public sealed class EthernetEligibilityTests
{
    private static GuardianInput Input(Core.Configuration.GuardianConfig config, params InterfaceRuntimeState[] interfaces) => new()
    {
        Now = TestData.Now,
        Config = config,
        GlobalProbe = TestData.OfflineProbe(),
        Interfaces = interfaces,
        WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
        Devices = Array.Empty<ManagedDevice>(),
        Radio = TestData.RadioOn,
    };

    private static Core.Configuration.GuardianConfig CampusAuthConfig() => TestData.Config(c =>
    {
        c.Ethernet.Enabled = true;
        c.Ethernet.AuthenticateWhenLinkUpButOffline = true;
        c.Ethernet.FailureThreshold = 1;
        c.Recovery.InternetFailureThreshold = 1;
        c.CampusAuth.Enabled = true;
        c.CampusAuth.ExecutablePath = @"C:\tools\campus.exe";
        c.CampusAuth.TriggerAfterConsecutiveFailures = 1;
        c.CampusAuth.MinIntervalSeconds = 0;
        c.CampusAuth.RequireEthernetLink = true;
    });

    [Fact]
    public void VirtualAdapterUpWithGateway_IsNotEthernetEligible()
    {
        var config = CampusAuthConfig();
        var engine = new GuardianDecisionEngine(config);

        var virtualAdapter = TestData.EthernetInterface(
            probe: TestData.OfflineProbe("172.18.28.6"),
            isPhysical: false,
            id: "luid:7001");

        var decision = engine.Evaluate(Input(config, virtualAdapter));

        Assert.DoesNotContain(decision.Actions, a => a is RunExternalCommandAction { IsCampusAuth: true });
    }

    [Fact]
    public void PhysicalEthernetUpWithoutInternet_TriggersCampusAuth()
    {
        var config = CampusAuthConfig();
        var engine = new GuardianDecisionEngine(config);

        var physical = TestData.EthernetInterface(
            probe: TestData.OfflineProbe("10.10.10.20"),
            isPhysical: true);

        var decision = engine.Evaluate(Input(config, physical));

        Assert.Contains(decision.Actions, a => a is RunExternalCommandAction { IsCampusAuth: true });
    }

    [Fact]
    public void UnknownProvenance_IsStillConsideredSoRealLinksAreNotMissed()
    {
        var config = CampusAuthConfig();
        var engine = new GuardianDecisionEngine(config);

        // No PnP record could be correlated (null): the interface is treated as a real link rather
        // than silently ignored.
        var unknown = TestData.EthernetInterface(
            probe: TestData.OfflineProbe("10.10.10.20"),
            isPhysical: null);

        var decision = engine.Evaluate(Input(config, unknown));

        Assert.Contains(decision.Actions, a => a is RunExternalCommandAction { IsCampusAuth: true });
    }
}
