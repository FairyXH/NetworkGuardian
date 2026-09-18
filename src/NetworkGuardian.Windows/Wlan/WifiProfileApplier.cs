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

/// <summary>
/// Pushes a library entry into the Windows WLAN store: the connection profile plus, for
/// PEAP-MSCHAPv2, the EAP user credentials. This is what "the application maintains the 802.1X account
/// itself" means in practice - Windows never prompts, and the credentials come from our own library.
/// </summary>
/// <remarks>
/// The profile is only rewritten when it is missing, is not an 802.1X profile, or the entry changed
/// since it was applied. The user credentials are written whenever the profile was written or the
/// entry has never been applied, and after the write the profile is read back so a silent no-op cannot
/// be reported as success.
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
        if (!userDataBuild.Success && credential.RequiresPassword)
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
        if (userDataBuild.Success && (profileWritten || credential.LastAppliedUtc is null))
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

        if (!WifiProfileInspector.IsEnterprise(readBack))
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


    /// <summary>Removes a profile from one adapter (used when a library entry is deleted).</summary>
    public WlanOperationResult Remove(Guid interfaceGuid, string profileName) =>
        _wifi.DeleteProfile(interfaceGuid, profileName);

    internal static string Describe(ProfileUpdateReason reason) => reason switch
    {
        ProfileUpdateReason.Missing => "网卡上没有该配置",
        ProfileUpdateReason.NotEnterprise => "现有配置不是 802.1X 配置",
        ProfileUpdateReason.LibraryChanged => "库中的账号或参数已更新",
        _ => "已是最新",
    };
}
