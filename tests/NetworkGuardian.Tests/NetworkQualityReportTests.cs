using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class NetworkQualityReportTests
{
    private static GuardianSnapshot Snapshot() => GuardianSnapshot.Initial(TestData.Config(), TestData.Now) with
    {
        GlobalProbe = TestData.OnlineProbe(),
        WifiAdapters = new[] { TestData.ConnectedAdapter(TestData.AdapterA, "Home", "Home") },
    };
    private static LinkQualityEstimate Quality() => new()
    {
        Adapter = TestData.AdapterA, Ssid = "Home", UpdatedAtUtc = TestData.Now,
        Samples = 20, Score = 90, GatewayLoss = 0, TcpFailure = 0, LatencyMs = 30,
    };
    private static NetworkQualityReport Report(GuardianSnapshot? snapshot = null, params LinkQualityEstimate[] quality) =>
        NetworkQualityReporter.Build(snapshot ?? Snapshot(), quality, TestData.Config(), NetworkBrain.Seed(), TestData.Now);

    [Fact]
    public void HealthyLink_IsExplainedWithoutClaimingBandwidth()
    {
        var report = Report(null, Quality());
        Assert.Contains("目前可以上网", report.Summary);
        Assert.Contains("质量优秀", report.Findings.Single());
        Assert.Contains("暂时不必换网", report.Advice);
        Assert.Contains("未测量下载带宽", report.Evidence);
    }

    [Fact]
    public void LocalLoss_DoesNotDiagnoseBrokenUsbHardware()
    {
        var report = Report(null, Quality() with { Score = 35, GatewayLoss = .3 });
        Assert.Contains("本地链路", report.Findings.Single());
        Assert.Contains("尚不能确定是硬件损坏", report.Findings.Single());
        Assert.Contains("保留当前可用出口", report.Advice);
    }

    [Fact]
    public void PublicPingLoss_WithHealthyTcp_IsNotReportedAsOffline()
    {
        var report = Report(null, Quality() with { InternetLoss = 1 });
        Assert.Contains("不据此判断断网", report.Findings.Single());
        Assert.Contains("目前可以上网", report.Summary);
    }

    [Theory]
    [InlineData(2, 0, "证据仍不足")]
    [InlineData(20, -6, "已过时")]
    [InlineData(20, 6, "已过时")]
    public void InsufficientOrStaleEvidence_IsNotGraded(int samples, int minutes, string message)
    {
        var report = Report(null, Quality() with { Samples = samples, UpdatedAtUtc = TestData.Now.AddMinutes(minutes) });
        Assert.Contains(message, report.Findings.Single());
        Assert.DoesNotContain("质量优秀", report.Findings.Single());
    }

    [Fact]
    public void UnknownLossMeasurements_AreNotGradedAsExcellent()
    {
        var report = Report(null, Quality() with { GatewayLoss = null, TcpFailure = null });
        Assert.Contains("证据仍不足", report.Findings.Single());
    }

    [Fact]
    public void StaleOnlineProbe_IsNotPresentedAsCurrentOnlineStatus()
    {
        var report = Report(Snapshot() with { GlobalProbe = TestData.OnlineProbe() with { TimestampUtc = TestData.Now.AddMinutes(-6) } });
        Assert.Contains("暂时还不能判断", report.Summary);
    }

    [Fact]
    public void PreviousSsidOrBand_IsNotUsedToGradeCurrentConnection()
    {
        var report = Report(null, Quality() with { Ssid = "Old" }, Quality() with { Band = NetworkBand.Band5GHz });
        Assert.Contains("还没有本次连接", report.Findings.Single());
    }

    [Fact]
    public void WorkingBackup_DoesNotHideFailureOfActualOutlet()
    {
        var report = Report(Snapshot() with { Interfaces = new[]
        {
            TestData.EthernetInterface(probe: TestData.OfflineProbe()) with { IsDefaultRoute = true },
        } });
        Assert.Contains("当前出口未通过外网验证", report.Summary);
        Assert.Contains("不代表当前出口已经恢复", report.Advice);
    }

    [Fact]
    public void WiredConnectivity_IsNotPresentedAsMeasuredQuality()
    {
        var report = Report(Snapshot() with { Interfaces = new[] { TestData.EthernetInterface() with { IsDefaultRoute = true } },
            WifiAdapters = Array.Empty<WifiAdapterRuntimeState>() });
        Assert.Contains(report.Findings, text => text.Contains("已连通不等于质量优秀", StringComparison.Ordinal));
    }

    [Fact]
    public void DisabledQuality_DoesNotReuseHistory()
    {
        var config = TestData.Config(c => c.Quality.Enabled = false);
        var report = NetworkQualityReporter.Build(Snapshot(), new[] { Quality() }, config, NetworkBrain.Seed(), TestData.Now);
        Assert.Contains("质量采样已关闭", report.Findings.Single());
    }
}
