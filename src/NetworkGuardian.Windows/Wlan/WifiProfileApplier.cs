using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetworkGuardian.Core.Abstractions;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Wlan;

namespace NetworkGuardian.Windows.Wlan;

/// <summary>Outcome of applying (or checking) a library entry against one adapter.</summary>
public sealed record WifiProfileApplyResult
{
    public required bool Success { get; init; }

    /// <summary>True when the profile document was written to the adapter.</summary>
    public bool ProfileWritten { get; init; }

    /// <summary>True when the account/password (EAP user data) was written as well.</summary>
    public bool UserDataWritten { get; init; }

    /// <summary>Why the profile was written (or not): missing, not an 802.1X profile, library changed.</summary>
    public ProfileUpdateReason UpdateReason { get; init; }

    public string? Failure { get; init; }

    /// <summary>
    /// Fingerprint of the generated profile XML; the caller stores it on the library entry so the next
    /// cycle knows the profile is current.
    /// </summary>
    public string? AppliedFingerprint { get; init; }

    public bool Changed => ProfileWritten || UserDataWritten;

    public static WifiProfileApplyResult Fail(string failure, ProfileUpdateReason reason = ProfileUpdateReason.UpToDate) =>
        new() { Success = false, Failure = failure, UpdateReason = reason };
}

/// <summary>Outcome of removing one profile from every adapter.</summary>
public sealed record WifiProfileRemoveResult
{
    public required bool Success { get; init; }

    /// <summary>Adapters that actually had the profile before the removal.</summary>
    public int Attempted { get; init; }

    public int Removed { get; init; }

    /// <summary>Adapters that still had it after the removal; must be 0.</summary>
    public int Remaining { get; init; }

    public IReadOnlyList<string> Failures { get; init; } = Array.Empty<string>();

    public string Describe() => Success
        ? $"已从 {Removed} 张网卡删除该配置"
        : $"删除不完整：处理 {Attempted} 张，仍剩余 {Remaining} 张" +
          (Failures.Count > 0 ? $"（{string.Join("；", Failures)}）" : string.Empty);
}

/// <summary>
/// Pushes a library entry into the Windows WLAN store: the connection profile plus, for
/// PEAP-MSCHAPv2, the EAP user credentials. This is what "the application maintains the 802.1X account
/// itself" means in practice - Windows never prompts, and the credentials come from our own library.
/// </summary>
/// <remarks>
/// The profile is only rewritten when it is missing, is not an 802.1X profile, or the entry changed
/// since it was applied. The user credentials are written whenever the profile was written or the
/// entry has never been applied, and after the write the profile is read back so a silent no-op cannot
/// be reported as success. EAP user data is intentionally written on every apply: it is stored separately
/// from the profile XML and another profile synchronizer can replace the profile without preserving the
/// current user's credentials.
/// </remarks>
public sealed class WifiProfileApplier
{
    private readonly NativeWifiManager _wifi;
    private readonly ILogger<WifiProfileApplier> _logger;

    public WifiProfileApplier(NativeWifiManager wifi, ILogger<WifiProfileApplier>? logger = null)
    {
        _wifi = wifi;
        _logger = logger ?? NullLogger<WifiProfileApplier>.Instance;
    }

    /// <summary>
    /// Ensures the adapter carries the profile (and credentials) of <paramref name="credential"/>.
    /// </summary>
    /// <param name="allowWrite">
    /// When false the entry is only inspected; an out-of-date profile is reported instead of written.
    /// </param>
    public WifiProfileApplyResult Apply(
        Guid interfaceGuid,
        WifiNetworkCredential credential,
        bool allowWrite = true)
    {
        ArgumentNullException.ThrowIfNull(credential);

        var profileName = credential.EffectiveProfileName;

        var profileBuild = EnterpriseProfileBuilder.BuildProfileXml(credential);
        if (!profileBuild.Success)
        {
            return WifiProfileApplyResult.Fail($"无法生成 802.1X 配置：{profileBuild.Failure}");
        }

        var profileXml = profileBuild.Xml!;
        var fingerprint = WifiProfileInspector.Fingerprint(profileXml);

        var userDataBuild = EnterpriseProfileBuilder.BuildEapUserDataXml(credential);
        if (credential.IsEnterprise && !userDataBuild.Success && credential.RequiresPassword)
        {
            // Without credentials an unattended handshake cannot succeed: report it instead of writing a
            // profile that would only make Windows prompt (or fail silently).
            return WifiProfileApplyResult.Fail($"无法生成 EAP 用户凭据：{userDataBuild.Failure}");
        }

        var current = _wifi.GetProfileXml(interfaceGuid, profileName);
        var reason = WifiProfileInspector.Evaluate(current, fingerprint, credential);

        if (reason != ProfileUpdateReason.UpToDate && !allowWrite)
        {
            return new WifiProfileApplyResult
            {
                Success = false,
                UpdateReason = reason,
                AppliedFingerprint = fingerprint,
                Failure = $"系统配置需要更新（{Describe(reason)}），但库设置中关闭了自动写入",
            };
        }

        var profileWritten = false;
        if (reason != ProfileUpdateReason.UpToDate)
        {
            var write = _wifi.SetProfileXml(interfaceGuid, profileName, profileXml, overwrite: true);

            // Measured: WlanSetProfile(overwrite: true) can still answer ERROR_ALREADY_EXISTS (183) when the
            // existing profile is in use - for example a leftover from a previous run whose adapter is still
            // authenticating with it. Deleting first and writing again is what Windows itself effectively
            // offers here, and without it a stale profile blocks the account from ever being applied.
            if (!write.Success && write.ErrorCode is 183 or 80)
            {
                _logger.LogWarning(
                    "Profile {Profile} already exists on {Adapter} and could not be overwritten; deleting and writing again",
                    profileName, interfaceGuid);

                var remove = _wifi.DeleteProfile(interfaceGuid, profileName);
                if (remove.Success)
                {
                    write = _wifi.SetProfileXml(interfaceGuid, profileName, profileXml, overwrite: false);
                }
            }

            if (!write.Success)
            {
                return new WifiProfileApplyResult
                {
                    Success = false,
                    UpdateReason = reason,
                    AppliedFingerprint = fingerprint,
                    Failure = $"写入 802.1X 配置失败：{write.Failure}",
                };
            }

            profileWritten = true;
            _logger.LogInformation(
                "Applied 802.1X profile {Profile} on {Adapter} ({Reason})",
                profileName, interfaceGuid, Describe(reason));
        }

        var userDataWritten = false;
        if (credential.IsEnterprise && userDataBuild.Success)
        {
            var write = _wifi.SetProfileEapUserData(interfaceGuid, profileName, userDataBuild.Xml!);
            if (!write.Success)
            {
                return new WifiProfileApplyResult
                {
                    Success = false,
                    ProfileWritten = profileWritten,
                    UpdateReason = reason,
                    AppliedFingerprint = fingerprint,
                    Failure = $"配置已写入，但写入账号密码失败：{write.Failure}",
                };
            }

            userDataWritten = true;
            _logger.LogInformation("Applied EAP credentials for {Profile} on {Adapter}", profileName, interfaceGuid);
        }

        // Read the profile back: a successful WlanSetProfile that produced nothing usable must not read
        // as success, because the connect would then fail with a confusing reason.
        var readBack = _wifi.GetProfileXml(interfaceGuid, profileName);
        if (string.IsNullOrWhiteSpace(readBack))
        {
            return new WifiProfileApplyResult
            {
                Success = false,
                ProfileWritten = profileWritten,
                UserDataWritten = userDataWritten,
                UpdateReason = reason,
                AppliedFingerprint = fingerprint,
                Failure = "配置写入后无法读回，网卡上仍不存在该配置",
            };
        }

        if (credential.IsEnterprise && !WifiProfileInspector.IsEnterprise(readBack))
        {
            return new WifiProfileApplyResult
            {
                Success = false,
                ProfileWritten = profileWritten,
                UserDataWritten = userDataWritten,
                UpdateReason = reason,
                AppliedFingerprint = fingerprint,
                Failure = "读回的配置不是 802.1X 配置，写入未生效",
            };
        }

        return new WifiProfileApplyResult
        {
            Success = true,
            ProfileWritten = profileWritten,
            UserDataWritten = userDataWritten,
            UpdateReason = reason,
            AppliedFingerprint = fingerprint,
        };
    }


    /// <summary>Removes a profile from one adapter.</summary>
    public WlanOperationResult Remove(Guid interfaceGuid, string profileName) =>
        _wifi.DeleteProfile(interfaceGuid, profileName);

    /// <summary>
    /// Removes a profile from every adapter and verifies that none still reports it.
    /// </summary>
    /// <remarks>
    /// Measured on hardware, and the reason this method walks every adapter:
    /// <list type="bullet">
    /// <item>immediately after <c>WlanSetProfile(..., WLAN_PROFILE_USER, ...)</c> only the target adapter
    /// reports the profile (WLAN API and <c>netsh ... interface=</c> agree);</item>
    /// <item>seconds later the other adapters report it too - the per-user profile store is machine wide and
    /// the per-interface views catch up asynchronously (a test that spent ~90 s connecting ended up with the
    /// profile on all three adapters).</item>
    /// </list>
    /// A single-adapter delete therefore leaves real leftovers behind (this happened here). Counts come from
    /// an API read-back, so a delete that silently did nothing cannot look like cleanup.
    /// </remarks>
    public WifiProfileRemoveResult RemoveEverywhere(string profileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileName);

        var attempted = 0;
        var removed = 0;
        var failures = new List<string>();
        var remaining = 0;

        // Bounded retry: deleting a profile the adapter is in the middle of connecting with can succeed and
        // then be reported again on the next read (measured while cleaning up a connect test), so a single
        // pass is not enough to claim success - but a permanent failure must not be retried forever either.
        for (var pass = 1; pass <= 3; pass++)
        {
            foreach (var adapter in _wifi.GetAdapters())
            {
                if (_wifi.GetProfileXml(adapter.InterfaceGuid, profileName) is null)
                {
                    continue;
                }

                attempted++;
                var result = _wifi.DeleteProfile(adapter.InterfaceGuid, profileName);
                if (result.Success)
                {
                    removed++;
                    _logger.LogInformation(
                        "Removed profile {Profile} from {Adapter} (pass {Pass})",
                        profileName, adapter.InterfaceGuid, pass);
                }
                else
                {
                    failures.Add($"{adapter.Description}: {result.Failure}");
                }
            }

            // Read back once more: a delete that reported success but left a copy must not look like cleanup.
            remaining = _wifi.GetAdapters()
                .Count(a => _wifi.GetProfileXml(a.InterfaceGuid, profileName) is not null);

            if (remaining == 0)
            {
                break;
            }

            _logger.LogDebug(
                "Profile {Profile} is still reported by {Remaining} adapter(s) after pass {Pass}",
                profileName, remaining, pass);
            Thread.Sleep(400);
        }

        return new WifiProfileRemoveResult
        {
            Success = remaining == 0,
            Attempted = attempted,
            Removed = removed,
            Remaining = remaining,
            Failures = failures,
        };
    }

    internal static string Describe(ProfileUpdateReason reason) => reason switch
    {
        ProfileUpdateReason.Missing => "网卡上没有该配置",
        ProfileUpdateReason.NotEnterprise => "现有配置不是 802.1X 配置",
        ProfileUpdateReason.LibraryChanged => "库中的账号或参数已更新",
        _ => "已是最新",
    };
}
