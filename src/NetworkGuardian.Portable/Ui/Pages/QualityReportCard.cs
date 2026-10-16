using System.Drawing;
using NetworkGuardian.Core.Policies;

namespace NetworkGuardian.Portable.Ui.Pages;

internal sealed class QualityReportCard
{
    public int Render(PageContext ctx, Rectangle area, int y)
    {
        var report = NetworkQualityReporter.Build(ctx.Snapshot, ctx.Host.LinkQuality, ctx.Config,
            ctx.Host.Brain, DateTimeOffset.UtcNow);
        var lines = new[] { report.Summary }.Concat(report.Findings).Append("建议：" + report.Advice).ToArray();
        var width = Math.Max(ctx.Scale(80), area.Width - ctx.Scale(32));
        var heights = lines.Select(line => ctx.Canvas.MeasureWrappedHeight(line, width, TextStyle.Body)).ToArray();
        var evidenceHeight = ctx.Canvas.MeasureWrappedHeight(report.Evidence, width, TextStyle.Caption);
        var card = new Rectangle(area.Left, y, area.Width,
            ctx.Scale(52) + heights.Sum() + ctx.Scale(8) * lines.Length + evidenceHeight);
        ctx.Canvas.Card(card);
        var x = card.Left + ctx.Scale(16);
        var cy = card.Top + ctx.Scale(12);
        ctx.Canvas.Text("网络质量评价报告", new Rectangle(x, cy, width, ctx.Scale(24)), Palette.TextPrimary, TextStyle.Section);
        cy += ctx.Scale(30);
        for (var i = 0; i < lines.Length; i++)
        {
            ctx.Canvas.Text(lines[i], new Rectangle(x, cy, width, heights[i]),
                i == 0 ? Palette.TextPrimary : Palette.TextSecondary, TextStyle.Body, wrap: TextWrap.Wrap);
            cy += heights[i] + ctx.Scale(8);
        }
        ctx.Canvas.Text(report.Evidence, new Rectangle(x, cy, width, evidenceHeight), Palette.TextMuted,
            TextStyle.Caption, wrap: TextWrap.Wrap);
        return card.Bottom + ctx.Scale(12);
    }
}
