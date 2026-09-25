using System.Drawing;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui.Pages;

/// <summary>
/// 缃戠粶鍑嵁搴擄細绋嬪簭鑷淮鎶ょ殑 802.1X / EAP 璐﹀彿搴擄紝鐙珛浜庣郴缁熺殑鏃犵嚎閰嶇疆涓庡嚟鎹€?/// </summary>
/// <remarks>
/// 杩欓噷淇濆瓨鐨勬槸璐﹀彿瀵嗙爜鐨勬潈濞佸壇鏈細杩炴帴浼佷笟绾?Wi-Fi 鏃剁敱绋嬪簭鎸夊簱涓殑鍙傛暟鐢熸垚绯荤粺閰嶇疆骞跺啓鍏ヨ处鍙凤紝
/// 涓嶄緷璧?Windows 鑷繁淇濆瓨鐨勫嚟鎹€傚瘑鐮佸彧浠?DPAPI 淇濇姢鐨勫瘑鏂囪惤鐩橈紝鐣岄潰涓婇粯璁や笉鏄剧ず鏄庢枃銆?/// </remarks>
internal sealed class CredentialsPage : IPage
{
    // Layout constants of one entry card, so the measured height and the drawing pass cannot drift apart.
    private const int TitleHeight = 30;
    private const int FieldRowHeight = 64;
    private const int XmlRowHeight = 80;
    private const int ButtonRowHeight = 44;
    private const int FieldRows = 8;

    private static readonly string[] AuthLabels = { "WPA2-Enterprise锛圓ES锛?, "WPA-Enterprise锛圱KIP锛?, "WPA3-Enterprise锛堝崰浣嶏紝瑙佽嚜瀹氫箟 XML锛? };
    private static readonly WifiEnterpriseAuth[] AuthValues =
    {
        WifiEnterpriseAuth.Wpa2Enterprise, WifiEnterpriseAuth.WpaEnterprise, WifiEnterpriseAuth.Wpa3Enterprise,
    };

    private static readonly string[] EapLabels = { "PEAP + MSCHAPv2锛堣处鍙峰瘑鐮侊級", "EAP-TLS锛堝鎴风璇佷功锛?, "鑷畾涔?XML锛圱TLS / 鍘傚晢鏂规锛? };
    private static readonly WifiEapMethod[] EapValues =
    {
        WifiEapMethod.PeapMschapv2, WifiEapMethod.Tls, WifiEapMethod.CustomXml,
    };

    private List<WifiNetworkCredential>? _working;
    private readonly HashSet<string> _revealed = new(StringComparer.Ordinal);
    private string _status = "灏氭湭鍔犺浇";

    public string Tag => "credentials";

    public string Label => "缃戠粶鍑嵁搴?;

    public string Title => "缃戠粶鍑嵁搴擄紙802.1X 璐﹀彿锛?;

    public string Description =>
        "绋嬪簭鑷淮鎶ょ殑鏃犵嚎缃戠粶搴擄細淇濆瓨浼佷笟绾э紙802.1X/EAP锛夌綉缁滅殑璐﹀彿銆佸瘑鐮佷笌鏈嶅姟鍣ㄦ牎楠屽弬鏁般€? +
        "瀵嗙爜浠?DPAPI 鍔犲瘑淇濆瓨锛岀晫闈㈤粯璁や笉鏄剧ず鏄庢枃锛涜繛鎺ユ椂鐢ㄥ簱閲岀殑鍙傛暟鐢熸垚绯荤粺閰嶇疆骞跺啓鍏ヨ处鍙凤紝Windows 涓嶅啀闇€瑕佽嚜宸变繚瀛樺瘑鐮併€?;

    public int Render(PageContext ctx, Rectangle area)
    {
        var canvas = ctx.Canvas;
        var entries = Working(ctx);
        var y = area.Top + Widgets.Heading(ctx, area, Title, Description);

        // ---------- toolbar ----------
        Widgets.Mono(ctx, area.Left, y, area.Width,
            $"搴撴枃浠讹細{ctx.Host.WifiLibraryPath}銆€瀵嗙爜淇濇姢锛歿ctx.Host.WifiLibraryProtection}銆€鍏?{entries.Count} 涓綉缁?);
        y += ctx.Scale(24);

        var save = Widgets.Button(ctx, area.Left, y, "淇濆瓨骞跺簲鐢?, () =>
        {
            var target = Clone(entries);
            _status = "姝ｅ湪淇濆瓨鈥?;
            ctx.Window.RunBackground(async () =>
            {
                await ctx.Host.SaveWifiLibraryAsync(target, CancellationToken.None).ConfigureAwait(false);
                _status = $"宸蹭繚瀛?{DateTimeOffset.Now:HH:mm:ss}锛堝凡娓呴櫎鐩稿簲缃戠粶鐨?802.1X 澶辫触璁℃暟锛?;
            });
        }, primary: true);

        Widgets.Button(ctx, save.Right + ctx.Scale(8), y, "鏂板缃戠粶", () =>
        {
            entries.Add(new WifiNetworkCredential
            {
                Ssid = "鏂扮殑鏍″洯缃?SSID",
                Identity = "璐﹀彿@鍩熷悕",
            });
            _status = "宸叉柊澧炰竴涓綉缁滐紝璇峰～鍐?SSID 涓庤处鍙峰瘑鐮?;
        });

        Widgets.Button(ctx, save.Right + ctx.Scale(8) + Widgets.MeasureButtonWidth(ctx, "鏂板缃戠粶") + ctx.Scale(8), y,
            "閲嶆柊鍔犺浇", () =>
            {
                _working = null;
                _status = "宸蹭粠纾佺洏閲嶆柊鍔犺浇";
            });

        canvas.Text(
            _status,
            new Rectangle(save.Right + ctx.Scale(360), y, Math.Max(ctx.Scale(80), area.Right - save.Right - ctx.Scale(370)), ctx.Scale(32)),
            Palette.TextMuted,
            TextStyle.Caption);

        y += ctx.Scale(40);

        var issues = ctx.Host.WifiLibraryIssues;
        if (issues.Count > 0)
        {
            var issueHeight = ctx.Scale(20) + (issues.Count * ctx.Scale(18));
            var issueRect = new Rectangle(area.Left, y, area.Width, issueHeight);
            canvas.Card(issueRect);
            var iy = issueRect.Top + ctx.Scale(8);
            canvas.Text("鏃犵嚎缃戠粶搴撴彁绀?, new Rectangle(issueRect.Left + ctx.Scale(16), iy, issueRect.Width - ctx.Scale(32), ctx.Scale(18)),
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
                "搴撲腑杩樻病鏈夌綉缁溿€傜偣鍑烩€滄柊澧炵綉缁溾€濓紝濉叆浼佷笟绾?Wi-Fi 鐨?SSID銆佽处鍙蜂笌瀵嗙爜锛屼繚瀛樺悗绋嬪簭浼氬湪杩炴帴鏃惰嚜鍔ㄥ啓鍏ョ郴缁熼厤缃€?,
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
        var retry = ctx.Host.EapRetryStatus
            .FirstOrDefault(s => string.Equals(s.Ssid, entry.Ssid, StringComparison.OrdinalIgnoreCase));

        var cardHeight = ctx.Scale(16 + 16)
                         + ctx.Scale(TitleHeight)
                         + (FieldRows * ctx.Scale(FieldRowHeight))
                         + ctx.Scale(XmlRowHeight)
                         + ctx.Scale(ButtonRowHeight);
        var card = new Rectangle(area.Left, y, area.Width, cardHeight);
        canvas.Card(card);

        var x = card.Left + ctx.Scale(16);
        var width = card.Width - ctx.Scale(32);
        var cy = card.Top + ctx.Scale(14);
        var column = (width - ctx.Scale(16)) / 2;

        var stateText = entry.Enabled ? "宸插惎鐢? : "宸插仠鐢?;
        var appliedText = string.IsNullOrWhiteSpace(entry.AppliedFingerprint)
            ? "绯荤粺閰嶇疆锛氬緟鍐欏叆"
            : $"绯荤粺閰嶇疆锛氬凡鍐欏叆 {entry.LastAppliedUtc?.ToLocalTime():MM-dd HH:mm}";
        var retryText = retry is null
            ? "802.1X锛氭湰娆¤繍琛屾棤澶辫触璁板綍"
            : retry.Abandoned
                ? $"802.1X锛氬凡澶辫触 {retry.Failures} 娆★紝鏈杩愯宸蹭复鏃舵斁寮冿紙閲嶅惎鍚庨噸璇曪級"
                : $"802.1X锛氬け璐?{retry.Failures} 娆?;

        canvas.Text($"{entry.Ssid}锛坽stateText}锛?, new Rectangle(x, cy, width - ctx.Scale(320), ctx.Scale(24)),
            Palette.TextPrimary, TextStyle.Section);
        canvas.Text($"{appliedText}銆€{retryText}", new Rectangle(card.Right - ctx.Scale(16) - ctx.Scale(420), cy, ctx.Scale(420), ctx.Scale(24)),
            retry?.Abandoned == true ? Palette.Warn : Palette.TextMuted, TextStyle.Caption, TextAlign.Right);
        cy += ctx.Scale(TitleHeight);

        Widgets.Field(ctx, x, cy, column, "SSID锛堝繀濉級", entry.Ssid, v =>
        {
            entry.Ssid = v.Trim();
            MarkDirty();
        });
        Widgets.Field(ctx, x + column + ctx.Scale(16), cy, column, "绯荤粺閰嶇疆鍚嶇О锛堢暀绌?= SSID锛?, entry.ProfileName ?? string.Empty,
            v => entry.ProfileName = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Dropdown(ctx, x, cy, column, "璁よ瘉鏂瑰紡", AuthLabels, Math.Max(0, Array.IndexOf(AuthValues, entry.Auth)),
            v => entry.Auth = AuthValues[v]);
        Widgets.Dropdown(ctx, x + column + ctx.Scale(16), cy, column, "EAP 鏂规硶", EapLabels, Math.Max(0, Array.IndexOf(EapValues, entry.Eap)),
            v => entry.Eap = EapValues[v]);
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, column, "璐﹀彿 / 韬唤锛堝繀濉紝PEAP 鐢級", entry.Identity, v => entry.Identity = v.Trim());

        var passwordColumn = x + column + ctx.Scale(16);
        var revealed = _revealed.Contains(entry.Id);
        var passwordDisplay = entry.PasswordDecryptionFailed
            ? "锛堝凡淇濆瓨鐨勫瘑鐮佹棤娉曡В瀵嗭紝璇烽噸鏂拌緭鍏ワ級"
            : entry.Password is null
                ? string.Empty
                : revealed ? entry.Password : $"宸蹭繚瀛?{entry.Password.Length} 浣嶏紝鐐瑰嚮杈撳叆鏂板瘑鐮?;

        Widgets.Field(ctx, passwordColumn, cy, column - ctx.Scale(200), "瀵嗙爜", passwordDisplay, v =>
        {
            // Only a typed value replaces the stored password; an empty commit must never wipe it, so
            // clearing is an explicit action (the button next to the field).
            if (!string.IsNullOrEmpty(v) && !v.StartsWith("宸蹭繚瀛?", StringComparison.Ordinal))
            {
                entry.Password = v;
                entry.PasswordDecryptionFailed = false;
                MarkDirty();
            }
        });

        var revealWidth = Widgets.MeasureButtonWidth(ctx, revealed ? "闅愯棌" : "鏄剧ず");
        Widgets.ButtonAt(ctx, new Rectangle(passwordColumn + column - ctx.Scale(192), cy + ctx.Scale(20), revealWidth, ctx.Scale(30)),
            revealed ? "闅愯棌" : "鏄剧ず",
            () =>
            {
                if (!_revealed.Add(entry.Id))
                {
                    _revealed.Remove(entry.Id);
                }
            });

        Widgets.ButtonAt(ctx, new Rectangle(passwordColumn + column - ctx.Scale(192) + revealWidth + ctx.Scale(6), cy + ctx.Scale(20),
                Widgets.MeasureButtonWidth(ctx, "娓呴櫎"), ctx.Scale(30)),
            "娓呴櫎",
            () =>
            {
                entry.Password = string.Empty;
                entry.PasswordDecryptionFailed = false;
                _revealed.Remove(entry.Id);
                MarkDirty();
            });
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, column, "鍩燂紙鍙┖锛?, entry.Domain ?? string.Empty,
            v => entry.Domain = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        Widgets.Field(ctx, x + column + ctx.Scale(16), cy, column, "鍖垮悕韬唤 / 澶栭儴韬唤锛堝彲绌猴級", entry.AnonymousIdentity ?? string.Empty,
            v => entry.AnonymousIdentity = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, width, "鏈嶅姟鍣ㄥ悕鏍￠獙锛堝彲绌猴紝澶氫釜鐢ㄥ垎鍙峰垎闅旓紱鐣欑┖琛ㄧず涓嶆牎楠屽悕绉帮級",
            string.Join(";", entry.ServerNames), v => entry.ServerNames = SplitList(v));
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, width, "鍙椾俊浠绘牴璇佷功鎸囩汗锛堝彲绌猴紝SHA-1 40 浣嶅崄鍏繘鍒讹紝澶氫釜鐢ㄥ垎鍙峰垎闅旓級",
            string.Join(";", entry.TrustedRootCaThumbprints), v => entry.TrustedRootCaThumbprints = SplitList(v));
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, column, "瀹㈡埛绔瘉涔︽寚绾癸紙浠?EAP-TLS锛屽彲绌猴級", entry.CertificateThumbprint ?? string.Empty,
            v => entry.CertificateThumbprint = string.IsNullOrWhiteSpace(v) ? null : v.Trim());
        Widgets.Field(ctx, x + column + ctx.Scale(16), cy, column, "澶囨敞锛堝彲绌猴級", entry.Notes ?? string.Empty,
            v => entry.Notes = string.IsNullOrWhiteSpace(v) ? null : v);
        cy += ctx.Scale(FieldRowHeight);

        var toggleWidth = (width - (ctx.Scale(12) * 3)) / 4;
        Widgets.Toggle(ctx, x, cy + ctx.Scale(4), toggleWidth, "鍚敤璇ョ綉缁?, entry.Enabled, () =>
        {
            entry.Enabled = !entry.Enabled;
            MarkDirty();
        });
        Widgets.Toggle(ctx, x + toggleWidth + ctx.Scale(12), cy + ctx.Scale(4), toggleWidth, "鑷姩杩炴帴", entry.ConnectAutomatically, () =>
        {
            entry.ConnectAutomatically = !entry.ConnectAutomatically;
            MarkDirty();
        });
        Widgets.Toggle(ctx, x + ((toggleWidth + ctx.Scale(12)) * 2), cy + ctx.Scale(4), toggleWidth, "闅愯棌缃戠粶", entry.Hidden, () =>
        {
            entry.Hidden = !entry.Hidden;
            MarkDirty();
        });
        Widgets.Toggle(ctx, x + ((toggleWidth + ctx.Scale(12)) * 3), cy + ctx.Scale(4), toggleWidth, "绂佹鏈嶅姟鍣ㄦ牎楠屾彁绀?, entry.DisableUserPromptForServerValidation, () =>
        {
            entry.DisableUserPromptForServerValidation = !entry.DisableUserPromptForServerValidation;
            MarkDirty();
        });
        cy += ctx.Scale(FieldRowHeight);

        Widgets.Field(ctx, x, cy, width, "鑷畾涔夐厤缃?XML锛圗AP-TTLS銆佸巶鍟嗘柟妗堛€佽瘉涔﹂€夋嫨绛夌敓鎴愬櫒鏈鐩栫殑鍦烘櫙锛涚暀绌哄垯鑷姩鐢熸垚锛?,
            entry.ProfileXmlOverride ?? string.Empty, v => entry.ProfileXmlOverride = string.IsNullOrWhiteSpace(v) ? null : v, multiline: true);
        cy += ctx.Scale(XmlRowHeight);

        var applyWidth = Widgets.MeasureButtonWidth(ctx, "鍐欏叆鍒版墍鏈夋棤绾跨綉鍗?);
        Widgets.ButtonAt(ctx, new Rectangle(x, cy, applyWidth, ctx.Scale(32)), "鍐欏叆鍒版墍鏈夋棤绾跨綉鍗?, () =>
        {
            _status = $"姝ｅ湪鍐欏叆 {entry.Ssid}鈥?;
            ctx.Window.RunBackground(async () =>
            {
                var results = await ctx.Host.ApplyWifiLibraryEntryAsync(entry.Id, null, CancellationToken.None)
                    .ConfigureAwait(false);
                _status = $"{entry.Ssid}锛歿string.Join("锛?, results)}";
            });
        }, enabled: !string.IsNullOrWhiteSpace(entry.Ssid));

        var removeWidth = Widgets.MeasureButtonWidth(ctx, "鍒犻櫎璇ョ綉缁?);
        Widgets.ButtonAt(ctx, new Rectangle(x + applyWidth + ctx.Scale(8), cy, removeWidth, ctx.Scale(32)), "鍒犻櫎璇ョ綉缁?, () =>
        {
            entries.Remove(entry);
            MarkDirty();
            _status = "宸插垹闄よ缃戠粶锛岀偣鍑烩€滀繚瀛樺苟搴旂敤鈥濈敓鏁堬紙绯荤粺閲屽凡鍐欏叆鐨勯厤缃笉浼氳嚜鍔ㄥ垹闄わ級";
        });

        // Removing the entry from the library and removing the profile from Windows are two different
        // things: the profile was written per interface, so the cleanup button walks every adapter.
        var purgeWidth = Widgets.MeasureButtonWidth(ctx, "浠庣郴缁熷垹闄ら厤缃?);
        Widgets.ButtonAt(ctx, new Rectangle(x + applyWidth + removeWidth + ctx.Scale(16), cy, purgeWidth, ctx.Scale(32)),
            "浠庣郴缁熷垹闄ら厤缃?,
            () =>
            {
                var profileName = entry.EffectiveProfileName;
                _status = $"姝ｅ湪浠庢墍鏈夌綉鍗″垹闄?{profileName}鈥?;
                ctx.Window.RunBackground(async () =>
                {
                    var result = await Task.Run(() => ctx.Host.RemoveWifiProfileEverywhere(profileName))
                        .ConfigureAwait(false);
                    _status = $"{profileName}锛歿result.Describe()}";
                });
            },
            enabled: !string.IsNullOrWhiteSpace(entry.EffectiveProfileName));

        canvas.Text(
            entry.Eap == WifiEapMethod.PeapMschapv2
                ? "璇存槑锛氳处鍙峰瘑鐮侀€氳繃 EAP 鐢ㄦ埛鍑嵁鍐欏叆 Windows锛涢厤缃湰韬彧澹版槑鏈嶅姟鍣ㄦ牎楠屽弬鏁般€?
                : entry.Eap == WifiEapMethod.Tls
                    ? "璇存槑锛欵AP-TLS 鐢ㄥ鎴风璇佷功璁よ瘉锛涘～鍐欐寚绾瑰彲鍥哄畾浣跨敤鏌愬紶璇佷功锛岀暀绌哄垯鐢?Windows 閫夋嫨銆?
                    : "璇存槑锛氳嚜瀹氫箟 XML 浼氬師鏍峰啓鍏?Windows锛汦AP-TTLS 闇€瑕佺郴缁熷畨瑁呭搴?EAP 鏂规硶銆?,
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

    private void MarkDirty() => _status = "鏈夋湭淇濆瓨鐨勬敼鍔?;

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
