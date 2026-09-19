using System.Drawing;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>
/// 网络凭据库：程序自维护的 802.1X / EAP 账号库，独立于系统的无线配置与凭据。
/// </summary>
/// <remarks>
/// 这里保存的是账号密码的权威副本：连接企业级 Wi-Fi 时由程序按库中的参数生成系统配置并写入账号，
/// 不依赖 Windows 自己保存的凭据。密码只以 DPAPI 保护的密文落盘，界面上默认不显示明文。
/// </remarks>
internal sealed class CredentialsPage : IPage
{
    // Layout constants of one entry card, so the measured height and the drawing pass cannot drift apart.
    private const int TitleHeight = 30;
    private const int FieldRowHeight = 64;
    private const int XmlRowHeight = 80;
    private const int ButtonRowHeight = 44;
    private const int BasicFieldRows = 2;
    private const int AdvancedFieldRows = 6;

    private static readonly string[] AuthLabels = { "WPA2-Enterprise（AES）", "WPA-Enterprise（TKIP）", "WPA3-Enterprise（占位，见自定义 XML）" };
    private static readonly WifiEnterpriseAuth[] AuthValues =
    {
        WifiEnterpriseAuth.Wpa2Enterprise, WifiEnterpriseAuth.WpaEnterprise, WifiEnterpriseAuth.Wpa3Enterprise,
    };

    private static readonly string[] EapLabels = { "PEAP + MSCHAPv2（账号密码）", "EAP-TLS（客户端证书）", "自定义 XML（TTLS / 厂商方案）" };
    private static readonly WifiEapMethod[] EapValues =
    {
        WifiEapMethod.PeapMschapv2, WifiEapMethod.Tls, WifiEapMethod.CustomXml,
    };

    private List<WifiNetworkCredential>? _working;
    private readonly HashSet<string> _revealed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _advanced = new(StringComparer.Ordinal);
    private int _selectedSuggestion;
    private string _status = "尚未加载";

    public string Tag => "credentials";

    public string Label => "网络凭据库";

    public string Title => "网络凭据库（802.1X 账号）";

    public string Description =>
        "程序自维护的无线网络库：保存企业级（802.1X/EAP）网络的账号、密码与服务器校验参数。" +
        "密码以 DPAPI 加密保存，界面默认不显示明文；连接时用库里的参数生成系统配置并写入账号，Windows 不再需要自己保存密码。";

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var entries = Working(ctx);
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        // ---------- toolbar ----------
        Widgets.Mono(ctx, area.Left, y, area.Width,
            $"库文件：{ctx.Host.WifiLibraryPath}　密码保护：{ctx.Host.WifiLibraryProtection}　共 {entries.Count} 个网络");
        y += ctx.Scale(24);

        var save = Widgets.Button(ctx, area.Left, y, "保存并应用", () =>
        {
            var target = Clone(entries);
            _status = "正在保存…";
            ctx.Window.RunBackground(async () =>
            {
                await ctx.Host.SaveWifiLibraryAsync(target, CancellationToken.None).ConfigureAwait(false);
                _status = $"已保存 {DateTimeOffset.Now:HH:mm:ss}（已清除相应网络的 802.1X 失败计数）";
            });
        }, primary: true);

        Widgets.Button(ctx, save.Right + ctx.Scale(8), y, "自定义网络", () =>
        {
            var entry = new WifiNetworkCredential
            {
                Identity = string.Empty,
            };
            entries.Add(entry);
            _advanced.Add(entry.Id);
            _status = "已新增自定义网络；请在高级选项中填写 SSID 与认证参数";
        });

        Widgets.Button(ctx, save.Right + ctx.Scale(8) + Widgets.MeasureButtonWidth(ctx, "自定义网络") + ctx.Scale(8), y,
            "重新加载", () =>
            {
                _working = null;
                _status = "已从磁盘重新加载";
            });

        canvas.Text(
            _status,
            new Rectangle(save.Right + ctx.Scale(360), y, Math.Max(ctx.Scale(80), area.Right - save.Right - ctx.Scale(370)), ctx.Scale(32)),
            Palette.TextMuted,
            TextStyle.Caption);

        y += ctx.Scale(40);

        var suggestions = ctx.Host.GetWifiCredentialSuggestions();
        var suggestionLabels = suggestions
            .Select(s => $"{s.Ssid}　{s.Security}　{s.SignalQuality}%{(s.HasSavedProfile ? "　已保存" : string.Empty)}")
            .ToList();
        if (suggestionLabels.Count == 0)
        {
            suggestionLabels.Add("未发现企业级 Wi-Fi，请先重新扫描");
            _selectedSuggestion = 0;
        }
        else
        {
            _selectedSuggestion = Math.Clamp(_selectedSuggestion, 0, suggestions.Count - 1);
        }

        var pickerWidth = Math.Min(area.Width - ctx.Scale(280), ctx.Scale(620));
        Widgets.Dropdown(ctx, area.Left, y, pickerWidth, "从当前 Wi-Fi 选择（自动识别安全类型与 EAP）",
            suggestionLabels, _selectedSuggestion, value => _selectedSuggestion = value);
        Widgets.Button(ctx, area.Left + pickerWidth + ctx.Scale(8), y + ctx.Scale(20), "添加所选网络", () =>
        {
            if (suggestions.Count == 0)
            {
                _status = "当前扫描结果中没有企业级 Wi-Fi";
                return;
            }

            var selected = suggestions[_selectedSuggestion];
            if (entries.Any(e => string.Equals(e.Ssid, selected.Ssid, StringComparison.OrdinalIgnoreCase)))
            {
                _status = $"{selected.Ssid} 已在凭据库中";
                return;
            }

            var entry = new WifiNetworkCredential
            {
                Ssid = selected.Ssid,
                ProfileName = string.Equals(selected.ProfileName, selected.Ssid, StringComparison.OrdinalIgnoreCase)
                    ? null
                    : selected.ProfileName,
                Auth = selected.Auth,
                Eap = selected.Eap,
                ServerNames = selected.ServerNames.ToList(),
                TrustedRootCaThumbprints = selected.TrustedRootCaThumbprints.ToList(),
            };
            entries.Add(entry);
            if (selected.Eap != WifiEapMethod.PeapMschapv2)
            {
                _advanced.Add(entry.Id);
            }
            MarkDirty();
            _status = $"已添加 {selected.Ssid}；只需填写{(selected.Eap == WifiEapMethod.Tls ? "证书信息" : "账号和密码")}后保存";
        }, primary: suggestions.Count > 0);
        Widgets.Button(ctx, area.Left + pickerWidth + ctx.Scale(8) + Widgets.MeasureButtonWidth(ctx, "添加所选网络") + ctx.Scale(8),
            y + ctx.Scale(20), "刷新 Wi-Fi", () =>
            {
                _status = "正在重新扫描全部无线网卡…";
                ctx.Window.RunBackground(async () =>
                {
                    await ctx.Host.RescanAllAsync(CancellationToken.None).ConfigureAwait(false);
                    _status = "Wi-Fi 列表已刷新";
                });
            });
        y += ctx.Scale(FieldRowHeight + 8);

        var issues = ctx.Host.WifiLibraryIssues;
        if (issues.Count > 0)
        {
            var issueHeight = ctx.Scale(20) + (issues.Count * ctx.Scale(18));
            var issueRect = new Rectangle(area.Left, y, area.Width, issueHeight);
            canvas.Card(issueRect);
            var iy = issueRect.Top + ctx.Scale(8);
            canvas.Text("无线网络库提示", new Rectangle(issueRect.Left + ctx.Scale(16), iy, issueRect.Width - ctx.Scale(32), ctx.Scale(18)),
                Palette.Warn, TextStyle.Caption);
            iy += ctx.Scale(18);
            foreach (var issue in issues)
            {
                canvas.Text(issue, new Rectangle(issueRect.Left + ctx.Scale(16), iy, issueRect.Width - ctx.Scale(32), ctx.Scale(18)),
                    Palette.TextSecondary, TextStyle.Caption);
                iy += ctx.Scale(18);
            }

            y = issueRect.Bottom + ctx.Scale(12);
        }

        if (entries.Count == 0)
        {
            var emptyRect = new Rectangle(area.Left, y, area.Width, ctx.Scale(72));
            canvas.Card(emptyRect);
            canvas.Text(
                "库中还没有网络。请从上方当前 Wi-Fi 列表选择；程序会识别认证类型，通常只需填写账号和密码。特殊网络可使用“自定义网络”。",
                new Rectangle(emptyRect.Left + ctx.Scale(16), emptyRect.Top, emptyRect.Width - ctx.Scale(32), emptyRect.Height),
                Palette.TextSecondary,
                TextStyle.Body,
                wrap: TextWrap.Wrap);
            y = emptyRect.Bottom + ctx.Scale(12);
        }

        for (var i = 0; i < entries.Count; i++)
        {
            y = RenderEntry(ctx, area, entries, i, y);
        }

        return y - area.Top;
    }

    private int RenderEntry(PageContext ctx, Rectangle area, List<WifiNetworkCredential> entries, int index, int y)
    {
        var canvas = ctx.Canvas;
        var entry = entries[index];
        var showAdvanced = _advanced.Contains(entry.Id);
        var retry = ctx.Host.EapRetryStatus
            .FirstOrDefault(s => string.Equals(s.Ssid, entry.Ssid, StringComparison.OrdinalIgnoreCase));

        var cardHeight = ctx.Scale(16 + 16)
                         + ctx.Scale(TitleHeight)
                         + (BasicFieldRows * ctx.Scale(FieldRowHeight))
                         + (showAdvanced ? (AdvancedFieldRows * ctx.Scale(FieldRowHeight)) + ctx.Scale(XmlRowHeight) : 0)
                         + ctx.Scale(ButtonRowHeight);
        var card = new Rectangle(area.Left, y, area.Width, cardHeight);
        canvas.Card(card);

        var x = card.Left + ctx.Scale(16);
        var width = card.Width - ctx.Scale(32);
        var cy = card.Top + ctx.Scale(14);
        var column = (width - ctx.Scale(16)) / 2;

        var stateText = entry.Enabled ? "已启用" : "已停用";
        var appliedText = string.IsNullOrWhiteSpace(entry.AppliedFingerprint)
            ? "系统配置：待写入"
            : $"系统配置：已写入 {entry.LastAppliedUtc?.ToLocalTime():MM-dd HH:mm}";
        var retryText = retry is null
            ? "802.1X：本次运行无失败记录"
            : retry.Abandoned
                ? $"802.1X：已失败 {retry.Failures} 次，本次运行已临时放弃（重启后重试）"
                : $"802.1X：失败 {retry.Failures} 次";

        canvas.Text($"{entry.Ssid}（{stateText}）", new Rectangle(x, cy, width - ctx.Scale(320), ctx.Scale(24)),
            Palette.TextPrimary, TextStyle.Section);
        canvas.Text($"{appliedText}　{retryText}", new Rectangle(card.Right - ctx.Scale(16) - ctx.Scale(420), cy, ctx.Scale(420), ctx.Scale(24)),
            retry?.Abandoned == true ? Palette.Warn : Palette.TextMuted, TextStyle.Caption, TextAlign.Right);
        cy += ctx.Scale(TitleHeight);

        Widgets.Caption(ctx, x, cy, column, "网络（从扫描结果选择）");
        canvas.Text(string.IsNullOrWhiteSpace(entry.Ssid) ? "自定义网络：请展开高级选项填写 SSID" : entry.Ssid,
            new Rectangle(x + ctx.Scale(10), cy + ctx.Scale(20), column - ctx.Scale(20), ctx.Scale(30)),
            string.IsNullOrWhiteSpace(entry.Ssid) ? Palette.Warn : Palette.TextPrimary, TextStyle.Body);
        Widgets.Field(ctx, x + column + ctx.Scale(16), cy, column, "账号 / 身份", entry.Identity,
            v => { entry.Identity = v.Trim(); MarkDirty(); });
        cy += ctx.Scale(FieldRowHeight);

        var passwordColumn = x;
        var revealed = _revealed.Contains(entry.Id);
        var passwordDisplay = entry.PasswordDecryptionFailed
            ? "（已保存的密码无法解密，请重新输入）"
            : entry.Password is null
                ? string.Empty
                : revealed ? entry.Password : $"已保存 {entry.Password.Length} 位，点击输入新密码";

        Widgets.Field(ctx, passwordColumn, cy, column - ctx.Scale(200), "密码", passwordDisplay, v =>
        {
            // Only a typed value replaces the stored password; an empty commit must never wipe it, so
            // clearing is an explicit action (the button next to the field).
            if (!string.IsNullOrEmpty(v) && !v.StartsWith("已保存 ", StringComparison.Ordinal))
            {
                entry.Password = v;
                entry.PasswordDecryptionFailed = false;
                MarkDirty();
            }
        });

        var revealWidth = Widgets.MeasureButtonWidth(ctx, revealed ? "隐藏" : "显示");
        Widgets.ButtonAt(ctx, new Rectangle(passwordColumn + column - ctx.Scale(192), cy + ctx.Scale(20), revealWidth, ctx.Scale(30)),
            revealed ? "隐藏" : "显示",
            () =>
            {
                if (!_revealed.Add(entry.Id))
                {
                    _revealed.Remove(entry.Id);
                }
            });

        Widgets.ButtonAt(ctx, new Rectangle(passwordColumn + column - ctx.Scale(192) + revealWidth + ctx.Scale(6), cy + ctx.Scale(20),
                Widgets.MeasureButtonWidth(ctx, "清除"), ctx.Scale(30)),
            "清除",
            () =>
            {
                entry.Password = string.Empty;
                entry.PasswordDecryptionFailed = false;
                _revealed.Remove(entry.Id);
                MarkDirty();
            });
        Widgets.ButtonAt(ctx, new Rectangle(x + column + ctx.Scale(16), cy + ctx.Scale(20),
                Widgets.MeasureButtonWidth(ctx, showAdvanced ? "收起高级选项" : "高级选项"), ctx.Scale(30)),
            showAdvanced ? "收起高级选项" : "高级选项", () =>
            {
                if (!_advanced.Add(entry.Id))
                {
                    _advanced.Remove(entry.Id);
                }
            });
        cy += ctx.Scale(FieldRowHeight);

        if (showAdvanced)
        {
            Widgets.Field(ctx, x, cy, column, "SSID（自定义网络必填）", entry.Ssid, v =>
            {
                entry.Ssid = v.Trim();
                MarkDirty();
            });
            Widgets.Field(ctx, x + column + ctx.Scale(16), cy, column, "系统配置名称（留空 = SSID）", entry.ProfileName ?? string.Empty,
                v => entry.ProfileName = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
            cy += ctx.Scale(FieldRowHeight);

            Widgets.Dropdown(ctx, x, cy, column, "认证方式", AuthLabels, Math.Max(0, Array.IndexOf(AuthValues, entry.Auth)),
                v => { entry.Auth = AuthValues[v]; MarkDirty(); });
            Widgets.Dropdown(ctx, x + column + ctx.Scale(16), cy, column, "EAP 方法", EapLabels, Math.Max(0, Array.IndexOf(EapValues, entry.Eap)),
                v => { entry.Eap = EapValues[v]; MarkDirty(); });
            cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, column, "域（可空）", entry.Domain ?? string.Empty,
            v => entry.Domain = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        Widgets.Field(ctx, x + column + ctx.Scale(16), cy, column, "匿名身份 / 外部身份（可空）", entry.AnonymousIdentity ?? string.Empty,
            v => entry.AnonymousIdentity = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, width, "服务器名校验（可空，多个用分号分隔；留空表示不校验名称）",
            string.Join(";", entry.ServerNames), v => entry.ServerNames = SplitList(v));
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, width, "受信任根证书指纹（可空，SHA-1 40 位十六进制，多个用分号分隔）",
            string.Join(";", entry.TrustedRootCaThumbprints), v => entry.TrustedRootCaThumbprints = SplitList(v));
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, column, "客户端证书指纹（仅 EAP-TLS，可空）", entry.CertificateThumbprint ?? string.Empty,
            v => entry.CertificateThumbprint = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        Widgets.Field(ctx, x + column + ctx.Scale(16), cy, column, "备注（可空）", entry.Notes ?? string.Empty,
            v => entry.Notes = string.IsNullOrWhiteSpace(v) ? null : v);
        cy += ctx.Scale(FieldRowHeight);

        var toggleWidth = (width - (ctx.Scale(12) * 3)) / 4;
        Widgets.Toggle(ctx, x, cy + ctx.Scale(4), toggleWidth, "启用该网络", entry.Enabled, () =>
        {
            entry.Enabled = !entry.Enabled;
            MarkDirty();
        });
        Widgets.Toggle(ctx, x + toggleWidth + ctx.Scale(12), cy + ctx.Scale(4), toggleWidth, "自动连接", entry.ConnectAutomatically, () =>
        {
            entry.ConnectAutomatically = !entry.ConnectAutomatically;
            MarkDirty();
        });
        Widgets.Toggle(ctx, x + ((toggleWidth + ctx.Scale(12)) * 2), cy + ctx.Scale(4), toggleWidth, "隐藏网络", entry.Hidden, () =>
        {
            entry.Hidden = !entry.Hidden;
            MarkDirty();
        });
        Widgets.Toggle(ctx, x + ((toggleWidth + ctx.Scale(12)) * 3), cy + ctx.Scale(4), toggleWidth, "禁止服务器校验提示", entry.DisableUserPromptForServerValidation, () =>
        {
            entry.DisableUserPromptForServerValidation = !entry.DisableUserPromptForServerValidation;
            MarkDirty();
        });
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, width, "自定义配置 XML（EAP-TTLS、厂商方案、证书选择等生成器未覆盖的场景；留空则自动生成）",
            entry.ProfileXmlOverride ?? string.Empty, v => entry.ProfileXmlOverride = string.IsNullOrWhiteSpace(v) ? null : v, multiline: true);
        cy += ctx.Scale(XmlRowHeight);
        }

        var applyWidth = Widgets.MeasureButtonWidth(ctx, "写入到所有无线网卡");
        Widgets.ButtonAt(ctx, new Rectangle(x, cy, applyWidth, ctx.Scale(32)), "写入到所有无线网卡", () =>
        {
            _status = $"正在写入 {entry.Ssid}…";
            ctx.Window.RunBackground(async () =>
            {
                var results = await ctx.Host.ApplyWifiLibraryEntryAsync(entry.Id, null, CancellationToken.None)
                    .ConfigureAwait(false);
                _status = $"{entry.Ssid}：{string.Join("；", results)}";
            });
        }, enabled: !string.IsNullOrWhiteSpace(entry.Ssid));

        var removeWidth = Widgets.MeasureButtonWidth(ctx, "删除该网络");
        Widgets.ButtonAt(ctx, new Rectangle(x + applyWidth + ctx.Scale(8), cy, removeWidth, ctx.Scale(32)), "删除该网络", () =>
        {
            entries.Remove(entry);
            MarkDirty();
            _status = "已删除该网络，点击“保存并应用”生效（系统里已写入的配置不会自动删除）";
        });

        // Removing the entry from the library and removing the profile from Windows are two different
        // things: the profile was written per interface, so the cleanup button walks every adapter.
        var purgeWidth = Widgets.MeasureButtonWidth(ctx, "从系统删除配置");
        Widgets.ButtonAt(ctx, new Rectangle(x + applyWidth + removeWidth + ctx.Scale(16), cy, purgeWidth, ctx.Scale(32)),
            "从系统删除配置",
            () =>
            {
                var profileName = entry.EffectiveProfileName;
                _status = $"正在从所有网卡删除 {profileName}…";
                ctx.Window.RunBackground(async () =>
                {
                    var result = await Task.Run(() => ctx.Host.RemoveWifiProfileEverywhere(profileName))
                        .ConfigureAwait(false);
                    _status = $"{profileName}：{result.Describe()}";
                });
            },
            enabled: !string.IsNullOrWhiteSpace(entry.EffectiveProfileName));

        canvas.Text(
            entry.Eap == WifiEapMethod.PeapMschapv2
                ? "说明：账号密码通过 EAP 用户凭据写入 Windows；配置本身只声明服务器校验参数。"
                : entry.Eap == WifiEapMethod.Tls
                    ? "说明：EAP-TLS 用客户端证书认证；填写指纹可固定使用某张证书，留空则由 Windows 选择。"
                    : "说明：自定义 XML 会原样写入 Windows；EAP-TTLS 需要系统安装对应 EAP 方法。",
            new Rectangle(x + applyWidth + removeWidth + purgeWidth + ctx.Scale(24), cy,
                Math.Max(ctx.Scale(120), width - applyWidth - removeWidth - purgeWidth - ctx.Scale(24)), ctx.Scale(32)),
            Palette.TextMuted,
            TextStyle.Caption,
            wrap: TextWrap.Wrap);

        return card.Bottom + ctx.Scale(12);
    }

    private List<WifiNetworkCredential> Working(PageContext ctx)
    {
        _working ??= Clone(ctx.Host.WifiLibraryEntries);
        return _working;
    }

    private void MarkDirty() => _status = "有未保存的改动";

    private static List<string> SplitList(string value) => value
        .Split(new[] { ';', ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .ToList();

    /// <summary>Copies the entries so edits never touch the live objects (the vault compares on save).</summary>
    private static List<WifiNetworkCredential> Clone(IReadOnlyList<WifiNetworkCredential> source) => source
        .Select(e => new WifiNetworkCredential
        {
            Id = e.Id,
            Ssid = e.Ssid,
            ProfileName = e.ProfileName,
            Auth = e.Auth,
            Eap = e.Eap,
            Identity = e.Identity,
            AnonymousIdentity = e.AnonymousIdentity,
            Domain = e.Domain,
            Password = e.Password,
            PasswordProtected = e.PasswordProtected,
            PasswordDecryptionFailed = e.PasswordDecryptionFailed,
            UseWinLogonCredentials = e.UseWinLogonCredentials,
            ServerNames = e.ServerNames.ToList(),
            TrustedRootCaThumbprints = e.TrustedRootCaThumbprints.ToList(),
            CertificateThumbprint = e.CertificateThumbprint,
            DisableUserPromptForServerValidation = e.DisableUserPromptForServerValidation,
            ConnectAutomatically = e.ConnectAutomatically,
            Hidden = e.Hidden,
            Enabled = e.Enabled,
            ProfileXmlOverride = e.ProfileXmlOverride,
            AppliedFingerprint = e.AppliedFingerprint,
            LastAppliedUtc = e.LastAppliedUtc,
        })
        .ToList();
}
