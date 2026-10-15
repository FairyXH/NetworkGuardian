using System.Drawing;
using NetworkGuardian.Core.Models;

namespace NetworkGuardian.Portable.Ui.Pages;

internal sealed class PreferenceCard
{
    private bool _showHistory;

    public int Render(PageContext ctx, Rectangle area, int y)
    {
        var host = ctx.Host;
        var enabled = host.Config.Quality.LearningEnabled;
        var question = enabled ? host.PreferenceQuestion : null;
        var preferences = host.Preferences;
        var options = question?.Options ?? Array.Empty<PreferenceOption>();
        var records = _showHistory ? preferences.Feedback.TakeLast(5).Reverse().ToArray() : Array.Empty<PreferenceFeedback>();
        var explanation = !enabled ? "本地偏好学习已关闭。您仍可在无线页指定网卡。" :
            question is null ? "根据时段、网络质量和您的选择学习偏好。需要调整时可主动打开选择；自动询问每天最多一次，同类场景间隔七天。" :
            "这个时候您更想使用哪个网络、哪块网卡？“仅本次”影响一小时；“记住偏好”学习类似场景；“始终指定”保存为硬规则。";
        var innerWidth = area.Width - ctx.Scale(32);
        var textHeight = ctx.Canvas.MeasureWrappedHeight(explanation, innerWidth, TextStyle.Body);
        var height = ctx.Scale(112 + options.Count * 100 + records.Length * 38) + textHeight;
        var card = new Rectangle(area.Left, y, area.Width, height);
        ctx.Canvas.Card(card);
        var x = card.Left + ctx.Scale(16);
        var cy = y + ctx.Scale(12);
        ctx.Canvas.Text("偏好卡片", new Rectangle(x, cy, innerWidth, ctx.Scale(24)), Palette.TextPrimary, TextStyle.Section);
        cy += ctx.Scale(28);
        ctx.Canvas.Text(explanation, new Rectangle(x, cy, innerWidth, textHeight), Palette.TextSecondary, TextStyle.Body, wrap: TextWrap.Wrap);
        cy += textHeight + ctx.Scale(8);
        var mode = !host.Config.Quality.ApplyLearnedPreferences ? "仅建议" : preferences.ExplicitChoices < 10 ? "积累反馈，仅建议" : "已参与有限评分（最多 ±4 分）";
        ctx.Canvas.Text($"明确反馈 {preferences.ExplicitChoices} 次 · 已存记录 {preferences.Feedback.Count} 条 · {mode} · 数据仅保存在本机",
            new Rectangle(x, cy, innerWidth, ctx.Scale(20)), Palette.TextMuted, TextStyle.Caption);
        cy += ctx.Scale(26);
        const string operation = "preference-choice";
        foreach (var option in options)
        {
            ctx.Canvas.Text(option.Label, new Rectangle(x, cy, innerWidth, ctx.Scale(22)), Palette.TextPrimary);
            cy += ctx.Scale(22);
            var detail = $"质量 {option.Quality:F0}/100 · {(option.Confidence < .25 ? "采样不足" : $"置信度 {option.Confidence:P0}")} · " +
                $"TCP 失败 {option.FailureRate?.ToString("P0") ?? "—"} · 延迟 {option.LatencyMs?.ToString("F0") ?? "—"} ms · {(option.Connected ? "已连接" : "需要连接验证")}";
            ctx.Canvas.Text(detail, new Rectangle(x, cy, innerWidth, ctx.Scale(20)), Palette.TextSecondary, TextStyle.Caption);
            cy += ctx.Scale(24);
            var once = Widgets.Button(ctx, x, cy, "仅本次", () => ctx.Window.RunBackground(operation, "正在应用本次选择",
                () => host.ChoosePreferenceAsync(option, false, CancellationToken.None), "已使用所选网络，本次偏好保持一小时"), enabled: !ctx.Window.IsOperationRunning(operation));
            var remember = Widgets.Button(ctx, once.Right + ctx.Scale(8), cy, "记住此时偏好", () => ctx.Window.RunBackground(operation, "正在验证并学习偏好",
                () => host.ChoosePreferenceAsync(option, true, CancellationToken.None), "已记录偏好，将用于类似场景"), enabled: !ctx.Window.IsOperationRunning(operation));
            Widgets.Button(ctx, remember.Right + ctx.Scale(8), cy, "始终指定本卡", () => ctx.Window.RunBackground(operation, "正在保存 SSID 网卡指定", async () =>
            {
                await ctx.Window.App.UpdateConfigAsync(c => c.Wifi.CampusWifiAdapterAssignments[option.Candidate.Ssid] = option.Candidate.InterfaceGuid.ToString("D")).ConfigureAwait(false);
                await host.ChoosePreferenceAsync(option, false, CancellationToken.None).ConfigureAwait(false);
            }, "已保存指定网卡规则"), enabled: !ctx.Window.IsOperationRunning(operation));
            cy += ctx.Scale(54);
        }
        foreach (var record in records)
        {
            ctx.Canvas.Text($"{record.AtUtc.ToLocalTime():MM-dd HH:mm} · {(record.Source == "explicit" ? "明确偏好" : "手动选择")} · {record.Chosen.Label}",
                new Rectangle(x, cy, innerWidth - ctx.Scale(92), ctx.Scale(32)), Palette.TextSecondary, TextStyle.Caption);
            Widgets.Button(ctx, card.Right - ctx.Scale(100), cy, "删除记录", () => ctx.Window.RunBackground("preference-records", "正在删除记录并重新学习",
                () => host.ResetPreferencesAsync(record.AtUtc), "记录已删除，模型已重新学习"), enabled: !ctx.Window.IsOperationRunning("preference-records"));
            cy += ctx.Scale(38);
        }
        var adjust = Widgets.Button(ctx, x, cy, enabled ? "调整偏好" : "启用学习", () =>
        {
            if (!enabled)
                ctx.Window.RunBackground("preference-enable", "正在启用本地学习", () => ctx.Window.App.UpdateConfigAsync(c => c.Quality.LearningEnabled = true));
            else
            {
                host.RefreshPreferenceQuestion(force: true);
                if (host.PreferenceQuestion is null) ctx.Window.ShowToast("当前不足两个可用方案，连接或扫描后再调整");
            }
        });
        var skip = Widgets.Button(ctx, adjust.Right + ctx.Scale(8), cy, "暂不回答", () => host.DismissPreferenceQuestion(), enabled: question is not null);
        var history = Widgets.Button(ctx, skip.Right + ctx.Scale(8), cy, _showHistory ? "收起记录" : "查看记录", () => _showHistory = !_showHistory);
        var export = Widgets.Button(ctx, history.Right + ctx.Scale(8), cy, "导出记录", () => ctx.Window.RunBackground("preference-export", "正在导出到本机数据目录", () => host.ExportPreferencesAsync(), "已导出 preference-export.json，可从配置目录查看"));
        Widgets.Button(ctx, export.Right + ctx.Scale(8), cy, "重置学习", () => ctx.Window.RunBackground("preference-reset", "正在重置偏好模型", () => host.ResetPreferencesAsync(), "偏好记录与模型已重置"));
        return card.Bottom + ctx.Scale(12);
    }
}
