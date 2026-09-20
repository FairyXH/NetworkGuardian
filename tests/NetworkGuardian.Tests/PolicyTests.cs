using NetworkGuardian.Core.Configuration;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;
using Xunit;

namespace NetworkGuardian.Tests;

public sealed class FailureTrackerTests
{
    [Fact]
    public void Threshold_RequiresConsecutiveFailures()
    {
        var tracker = new FailureTracker("t", failureThreshold: 3, recoveryThreshold: 2);
        var now = TestData.Now;

        Assert.False(tracker.RecordFailure(now));
        Assert.False(tracker.RecordFailure(now.AddSeconds(5)));
        Assert.True(tracker.RecordFailure(now.AddSeconds(10)));
        Assert.True(tracker.IsFailing);

        // Further failures do not re-trigger the transition.
        Assert.False(tracker.RecordFailure(now.AddSeconds(15)));
        Assert.Equal(4, tracker.ConsecutiveFailures);
    }

    [Fact]
    public void Success_ResetsTheFailureCounter()
    {
        var tracker = new FailureTracker("t", 3, 2);
        var now = TestData.Now;

        tracker.RecordFailure(now);
        tracker.RecordFailure(now);
        tracker.RecordSuccess(now);
        Assert.Equal(0, tracker.ConsecutiveFailures);
        Assert.False(tracker.IsFailing);

        Assert.False(tracker.RecordFailure(now));
        Assert.False(tracker.RecordFailure(now));
    }

    [Fact]
    public void Recovery_NeedsHysteresis()
    {
        var tracker = new FailureTracker("t", 2, 3);
        var now = TestData.Now;

        tracker.RecordFailure(now);
        tracker.RecordFailure(now);
        Assert.True(tracker.IsFailing);

        Assert.False(tracker.RecordSuccess(now));
        Assert.False(tracker.RecordSuccess(now));
        Assert.True(tracker.RecordSuccess(now));
        Assert.False(tracker.IsFailing);
    }
}

public sealed class ExponentialBackoffTests
{
    [Fact]
    public void Backoff_GrowsAndIsCapped()
    {
        var backoff = new ExponentialBackoff("b", baseSeconds: 5, maxSeconds: 60, factor: 2, jitterRatio: 0, seed: 1);

        Assert.Equal(5, backoff.DelayFor(0).TotalSeconds);
        Assert.Equal(10, backoff.DelayFor(1).TotalSeconds);
        Assert.Equal(20, backoff.DelayFor(2).TotalSeconds);
        Assert.Equal(40, backoff.DelayFor(3).TotalSeconds);
        Assert.Equal(60, backoff.DelayFor(4).TotalSeconds);
        Assert.Equal(60, backoff.DelayFor(20).TotalSeconds);
    }

    [Fact]
    public void Backoff_JitterKeepsTheDelayPositiveAndBounded()
    {
        var backoff = new ExponentialBackoff("b", 4, 30, 2, 0.2, seed: 7);

        for (var i = 0; i < 25; i++)
        {
            var delay = backoff.DelayFor(i);
            Assert.InRange(delay.TotalSeconds, 1, 36);
        }
    }
}

public sealed class SlidingWindowRateLimiterTests
{
    [Fact]
    public void MinimumInterval_BlocksImmediateRepeats()
    {
        var limiter = new SlidingWindowRateLimiter("scan", maxRunsPerHour: 10,
            minInterval: TimeSpan.FromSeconds(25), maxConsecutiveRuns: 5);

        Assert.True(limiter.TryAcquire(TestData.Now, out _, out _));
        limiter.RecordRun(TestData.Now);

        Assert.False(limiter.TryAcquire(TestData.Now.AddSeconds(5), out var retryAfter, out var reason));
        Assert.True(retryAfter.TotalSeconds > 0);
        Assert.Contains("minimum interval", reason);

        Assert.True(limiter.TryAcquire(TestData.Now.AddSeconds(26), out _, out _));
    }

    [Fact]
    public void HourlyBudget_IsEnforced()
    {
        var limiter = new SlidingWindowRateLimiter("auth", maxRunsPerHour: 3,
            minInterval: TimeSpan.Zero, maxConsecutiveRuns: 100);

        for (var i = 0; i < 3; i++)
        {
            Assert.True(limiter.TryAcquire(TestData.Now.AddMinutes(i), out _, out _));
            limiter.RecordRun(TestData.Now.AddMinutes(i));
        }

        Assert.False(limiter.TryAcquire(TestData.Now.AddMinutes(4), out _, out var reason));
        Assert.Contains("hourly budget", reason);

        // An hour later the window has moved on.
        Assert.True(limiter.TryAcquire(TestData.Now.AddMinutes(65), out _, out _));
    }

    [Fact]
    public void ConsecutiveRuns_AreCappedUntilSuccessIsReported()
    {
        var limiter = new SlidingWindowRateLimiter("connect", 100, TimeSpan.Zero, maxConsecutiveRuns: 2);

        for (var i = 0; i < 2; i++)
        {
            Assert.True(limiter.TryAcquire(TestData.Now.AddSeconds(i * 30), out _, out _));
            limiter.RecordRun(TestData.Now.AddSeconds(i * 30));
        }

        Assert.False(limiter.TryAcquire(TestData.Now.AddSeconds(90), out _, out var reason));
        Assert.Contains("consecutive runs", reason);

        limiter.NotifySuccess();
        Assert.True(limiter.TryAcquire(TestData.Now.AddSeconds(90), out _, out _));
    }
}

public sealed class ConnectFailureBlacklistTests
{
    [Fact]
    public void FailedProfile_IsBannedForTheConfiguredWindow()
    {
        var blacklist = new ConnectFailureBlacklist(TimeSpan.FromSeconds(60));

        blacklist.RecordFailure(TestData.AdapterA, "CampusWiFi", TestData.Now, "auth failed");

        Assert.True(blacklist.IsBlacklisted(TestData.AdapterA, "CampusWiFi", TestData.Now.AddSeconds(30), out var remaining));
        Assert.True(remaining.TotalSeconds > 0);

        Assert.False(blacklist.IsBlacklisted(TestData.AdapterA, "CampusWiFi", TestData.Now.AddSeconds(61), out _));
    }

    [Fact]
    public void BanIsPerAdapter()
    {
        var blacklist = new ConnectFailureBlacklist(TimeSpan.FromSeconds(60));
        blacklist.RecordFailure(TestData.AdapterA, "CampusWiFi", TestData.Now, "auth failed");

        Assert.False(blacklist.IsBlacklisted(TestData.AdapterB, "CampusWiFi", TestData.Now.AddSeconds(10), out _));
    }
}

public sealed class CandidateSelectorTests
{
    private readonly CandidateSelector _selector = new(new ConnectFailureBlacklist(TimeSpan.FromSeconds(120)));

    private IReadOnlyList<WifiCandidate> Select(
        WifiSettings settings,
        AdapterScanSnapshot scan,
        IReadOnlyList<string> profiles,
        string? lastProfile = null)
    {
        return _selector.SelectCandidates(
            scan.InterfaceGuid, scan, profiles, settings, TestData.Now, out _, lastProfile);
    }

    [Fact]
    public void UnknownNetwork_IsNeverChosen()
    {
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 70),
            TestData.Network(TestData.AdapterA, "CoffeeShop", 99, hasProfile: false));

        var candidates = Select(new WifiSettings(), scan, new[] { "CampusWiFi" });

        var candidate = Assert.Single(candidates);
        Assert.Equal("CampusWiFi", candidate.Ssid);
    }

    [Fact]
    public void StrongestNetworkWins()
    {
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 40),
            TestData.Network(TestData.AdapterA, "DormWiFi", 88));

        var candidates = Select(new WifiSettings(), scan, new[] { "CampusWiFi", "DormWiFi" });

        Assert.Equal("DormWiFi", candidates[0].Ssid);
        Assert.Equal("CampusWiFi", candidates[1].Ssid);
    }

    [Fact]
    public void HighBandBonus_CanOutweighASlightlyStrongerTwoPointFourNetwork()
    {
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "TwoPointFour", 60, band: NetworkBand.Band2_4GHz),
            TestData.Network(TestData.AdapterA, "FiveG", 55, band: NetworkBand.Band5GHz));

        var withBonus = Select(new WifiSettings { PreferHighBand = true, HighBandBonus = 10 }, scan,
            new[] { "TwoPointFour", "FiveG" });
        var withoutBonus = Select(new WifiSettings { PreferHighBand = false }, scan,
            new[] { "TwoPointFour", "FiveG" });

        Assert.Equal("FiveG", withBonus[0].Ssid);
        Assert.Equal("TwoPointFour", withoutBonus[0].Ssid);
    }

    [Fact]
    public void AdHocNetworks_AreIgnored()
    {
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "AdHocNet", 95, bssType: WifiBssType.Independent),
            TestData.Network(TestData.AdapterA, "CampusWiFi", 30));

        var candidates = Select(new WifiSettings(), scan, new[] { "AdHocNet", "CampusWiFi" });

        Assert.DoesNotContain(candidates, c => c.Ssid == "AdHocNet");
        Assert.Single(candidates);
    }

    [Fact]
    public void DenyListAndAllowList_AreApplied()
    {
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 70),
            TestData.Network(TestData.AdapterA, "DormWiFi", 60));

        var denied = Select(new WifiSettings { SsidDenyList = new List<string> { "CampusWiFi" } }, scan,
            new[] { "CampusWiFi", "DormWiFi" });

        Assert.Equal("DormWiFi", Assert.Single(denied).Ssid);

        var allowed = Select(new WifiSettings { SsidAllowList = new List<string> { "CampusWiFi" } }, scan,
            new[] { "CampusWiFi", "DormWiFi" });

        Assert.Equal("CampusWiFi", Assert.Single(allowed).Ssid);
    }

    [Fact]
    public void CampusNetwork_IsSkippedDuringQuietPeriod()
    {
        var settings = new WifiSettings
        {
            CampusQuietPeriodEnabled = true,
            CampusQuietStartMinutes = 0,
            CampusQuietEndMinutes = 0,
            CampusNetworkSsids = new List<string> { "CampusWiFi" },
        };
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 90),
            TestData.Network(TestData.AdapterA, "DormWiFi", 50));

        var candidates = Select(settings, scan, new[] { "CampusWiFi", "DormWiFi" });

        Assert.Equal("DormWiFi", Assert.Single(candidates).Ssid);
    }

    [Fact]
    public void RecentlyBlacklistedProfile_IsSkipped()
    {
        var blacklist = new ConnectFailureBlacklist(TimeSpan.FromSeconds(300));
        var selector = new CandidateSelector(blacklist);

        blacklist.RecordFailure(TestData.AdapterA, "CampusWiFi", TestData.Now, "auth failed");

        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 90),
            TestData.Network(TestData.AdapterA, "DormWiFi", 40));

        var candidates = selector.SelectCandidates(
            TestData.AdapterA, scan, new[] { "CampusWiFi", "DormWiFi" }, new WifiSettings(),
            TestData.Now, out var rejections);

        Assert.Equal("DormWiFi", Assert.Single(candidates).Ssid);
        Assert.Contains(rejections, r => r.Contains("blacklisted"));
    }

    [Fact]
    public void WeakNetworks_AreDroppedOnlyWhenSomethingStrongerExists()
    {
        var settings = new WifiSettings { MinimumSignalQuality = 25 };

        var strongAndWeak = Select(settings, TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "Strong", 80),
            TestData.Network(TestData.AdapterA, "Weak", 10)), new[] { "Strong", "Weak" });

        Assert.Single(strongAndWeak);

        var onlyWeak = Select(settings, TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "Weak", 10)), new[] { "Weak" });

        // A weak network is still better than no connection at all.
        Assert.Single(onlyWeak);
    }

    [Fact]
    public void RecentlyUsedProfile_BreaksTies()
    {
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 60),
            TestData.Network(TestData.AdapterA, "DormWiFi", 60));

        var candidates = Select(
            new WifiSettings { PreferRecentProfiles = true, RecentProfileBonus = 5 },
            scan,
            new[] { "CampusWiFi", "DormWiFi" },
            lastProfile: "DormWiFi");

        Assert.Equal("DormWiFi", candidates[0].Ssid);
    }

    [Fact]
    public void NotConnectableNetworks_AreSkipped()
    {
        var scan = TestData.Scan(
            TestData.AdapterA,
            TestData.Network(TestData.AdapterA, "CampusWiFi", 90, connectable: false),
            TestData.Network(TestData.AdapterA, "DormWiFi", 40));

        var candidates = Select(new WifiSettings(), scan, new[] { "CampusWiFi", "DormWiFi" });

        Assert.Equal("DormWiFi", Assert.Single(candidates).Ssid);
    }
}

public sealed class AdapterAssignmentPlannerTests
{
    private static AdapterCandidateSet Set(Guid guid, params (string Ssid, int Quality)[] candidates) => new(
        guid,
        candidates.Select(c => new WifiCandidate
        {
            InterfaceGuid = guid,
            Ssid = c.Ssid,
            ProfileName = c.Ssid,
            SignalQuality = c.Quality,
            Score = c.Quality,
        }).ToList());

    [Fact]
    public void DuplicateSsid_IsAvoidedWhenConfigured()
    {
        var sets = new[]
        {
            Set(TestData.AdapterA, ("CampusWiFi", 90), ("DormWiFi", 40)),
            Set(TestData.AdapterB, ("CampusWiFi", 80), ("DormWiFi", 70)),
        };

        var plan = AdapterAssignmentPlanner.Plan(sets, allowSameSsidOnMultipleAdapters: false);

        Assert.Equal(2, plan.Count);
        Assert.Equal(2, plan.Select(p => p.Candidate.Ssid).Distinct().Count());
        Assert.Equal("CampusWiFi", plan.Single(p => p.InterfaceGuid == TestData.AdapterA).Candidate.Ssid);
        Assert.Equal("DormWiFi", plan.Single(p => p.InterfaceGuid == TestData.AdapterB).Candidate.Ssid);
    }

    [Fact]
    public void DuplicateSsid_IsAllowedWhenConfigured()
    {
        var sets = new[]
        {
            Set(TestData.AdapterA, ("CampusWiFi", 90)),
            Set(TestData.AdapterB, ("CampusWiFi", 80)),
        };

        var plan = AdapterAssignmentPlanner.Plan(sets, allowSameSsidOnMultipleAdapters: true);

        Assert.Equal(2, plan.Count);
        Assert.All(plan, p => Assert.Equal("CampusWiFi", p.Candidate.Ssid));
    }

    [Fact]
    public void AdapterWithoutCandidates_IsLeftUnassigned()
    {
        var sets = new[]
        {
            Set(TestData.AdapterA, ("CampusWiFi", 50)),
            Set(TestData.AdapterB),
        };

        var plan = AdapterAssignmentPlanner.Plan(sets, allowSameSsidOnMultipleAdapters: true);

        Assert.Single(plan);
        Assert.Equal(TestData.AdapterA, plan[0].InterfaceGuid);
    }
}

/// <summary>Campus authentication must never turn into a launch storm.</summary>
public sealed class CampusAuthRateLimitTests
{
    [Fact]
    public void CampusAuth_IsSuspendedDuringQuietPeriod()
    {
        var config = TestData.Config(c =>
        {
            c.CampusAuth.Enabled = true;
            c.CampusAuth.ExecutablePath = @"C:\tools\campus.exe";
            c.CampusAuth.TriggerAfterConsecutiveFailures = 1;
            c.Wifi.CampusQuietPeriodEnabled = true;
            c.Wifi.CampusQuietStartMinutes = 0;
            c.Wifi.CampusQuietEndMinutes = 0;
        });
        var engine = new GuardianDecisionEngine(config);

        RunCampusAuth(engine, config, new[] { TestData.EthernetInterface() }, TestData.Now, expectAuth: false);
    }

    [Fact]
    public void CampusAuth_RespectsMinimumIntervalAndHourlyBudget()
    {
        var config = TestData.Config(c =>
        {
            c.Ethernet.Enabled = true;
            c.Ethernet.AuthenticateWhenLinkUpButOffline = true;
            c.Ethernet.FailureThreshold = 1;
            c.Recovery.InternetFailureThreshold = 1;
            c.CampusAuth.Enabled = true;
            c.CampusAuth.ExecutablePath = @"C:\tools\campus.exe";
            c.CampusAuth.TriggerAfterConsecutiveFailures = 1;
            c.CampusAuth.MinIntervalSeconds = 300;
            c.CampusAuth.MaxRunsPerHour = 2;
            c.CampusAuth.MaxConsecutiveRuns = 2;
            c.CampusAuth.WaitAfterRunSeconds = 0;
        });

        var engine = new GuardianDecisionEngine(config);
        var interfaces = new[] { TestData.EthernetInterface(probe: TestData.OfflineProbe("10.10.10.20")) };

        RunCampusAuth(engine, config, interfaces, TestData.Now, expectAuth: true);

        // A second attempt inside the minimum interval must be suppressed.
        RunCampusAuth(engine, config, interfaces, TestData.Now.AddSeconds(30), expectAuth: false);

        // After the interval, but only up to the hourly budget.
        RunCampusAuth(engine, config, interfaces, TestData.Now.AddSeconds(320), expectAuth: true);
        RunCampusAuth(engine, config, interfaces, TestData.Now.AddSeconds(650), expectAuth: false);
    }

    [Fact]
    public void CampusAuth_IsNotStartedWithoutPhysicalEthernetLink()
    {
        var config = TestData.Config(c =>
        {
            c.Ethernet.AuthenticateWhenLinkUpButOffline = true;
            c.Recovery.InternetFailureThreshold = 1;
            c.CampusAuth.Enabled = true;
            c.CampusAuth.ExecutablePath = @"C:\tools\campus.exe";
            c.CampusAuth.TriggerAfterConsecutiveFailures = 1;
            c.CampusAuth.RequireEthernetLink = true;
        });

        var engine = new GuardianDecisionEngine(config);
        var interfaces = new[] { TestData.EthernetInterface(up: false, hasAddress: false, hasGateway: false) };

        var decision = engine.Evaluate(new GuardianInput
        {
            Now = TestData.Now,
            Config = config,
            GlobalProbe = TestData.OfflineProbe(),
            Interfaces = interfaces,
            WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
            Devices = Array.Empty<ManagedDevice>(),
            Radio = TestData.RadioOn,
        });

        Assert.DoesNotContain(decision.Actions, a => a is RunExternalCommandAction { IsCampusAuth: true });
    }

    [Fact]
    public void OfflineCommand_IsRateLimited()
    {
        var config = TestData.Config(c =>
        {
            c.Recovery.InternetFailureThreshold = 1;
            c.OfflineCommands.Add(new CommandDefinition
            {
                Id = "cmd-1",
                Name = "重新认证",
                Kind = CommandKind.Shell,
                ExecutablePath = "curl http://10.0.0.1/login",
                Enabled = true,
                MinIntervalSeconds = 600,
                MaxRunsPerHour = 3,
                MaxConsecutiveRuns = 2,
            });
        });

        var engine = new GuardianDecisionEngine(config);
        var interfaces = new[] { TestData.EthernetInterface(probe: TestData.OfflineProbe("10.10.10.20")) };

        var first = engine.Evaluate(new GuardianInput
        {
            Now = TestData.Now,
            Config = config,
            GlobalProbe = TestData.OfflineProbe(),
            Interfaces = interfaces,
            WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
            Devices = Array.Empty<ManagedDevice>(),
            Radio = TestData.RadioOn,
        });

        Assert.Contains(first.Actions, a => a is RunExternalCommandAction { CommandId: "cmd-1" });

        var second = engine.Evaluate(new GuardianInput
        {
            Now = TestData.Now.AddSeconds(60),
            Config = config,
            GlobalProbe = TestData.OfflineProbe(),
            Interfaces = interfaces,
            WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
            Devices = Array.Empty<ManagedDevice>(),
            Radio = TestData.RadioOn,
        });

        Assert.DoesNotContain(second.Actions, a => a is RunExternalCommandAction { CommandId: "cmd-1" });
    }

    [Fact]
    public void InterfaceMetrics_AreAlwaysReconciledForAutomaticFailover()
    {
        var mismatched = TestData.WifiInterface(TestData.AdapterA) with { InterfaceMetric = 1 };
        var enabled = TestData.Config(c => c.General.ManageInterfaceMetrics = true);
        var engine = new GuardianDecisionEngine(enabled);

        var decision = engine.Evaluate(new GuardianInput
        {
            Now = TestData.Now,
            Config = enabled,
            GlobalProbe = TestData.OnlineProbe(),
            Interfaces = new[] { mismatched },
            WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
            Devices = Array.Empty<ManagedDevice>(),
            Radio = TestData.RadioOn,
        });

        Assert.Contains(decision.Actions, action => action is ApplyInterfaceMetricsAction);

        enabled.General.ManageInterfaceMetrics = false; // legacy setting no longer disables safety policy
        decision = engine.Evaluate(new GuardianInput
        {
            Now = TestData.Now.AddSeconds(20),
            Config = enabled,
            GlobalProbe = TestData.OnlineProbe(),
            Interfaces = new[] { mismatched },
            WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
            Devices = Array.Empty<ManagedDevice>(),
            Radio = TestData.RadioOn,
        });

        var metrics = Assert.IsType<ApplyInterfaceMetricsAction>(
            Assert.Single(decision.Actions, action => action is ApplyInterfaceMetricsAction));
        Assert.Equal(50, metrics.EthernetMetric);
        Assert.Equal(10, metrics.WifiMetric);
    }

    private static void RunCampusAuth(
        GuardianDecisionEngine engine,
        GuardianConfig config,
        IReadOnlyList<InterfaceRuntimeState> interfaces,
        DateTimeOffset now,
        bool expectAuth)
    {
        var decision = engine.Evaluate(new GuardianInput
        {
            Now = now,
            Config = config,
            GlobalProbe = TestData.OfflineProbe(),
            Interfaces = interfaces,
            WifiAdapters = Array.Empty<WifiAdapterRuntimeState>(),
            Devices = Array.Empty<ManagedDevice>(),
            Radio = TestData.RadioOn,
        });

        var started = decision.Actions.Any(a => a is RunExternalCommandAction { IsCampusAuth: true });
        Assert.Equal(expectAuth, started);
    }
}
