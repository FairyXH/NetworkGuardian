using System.Drawing;
using NetworkGuardian.Core.Models;
using NetworkGuardian.Core.Policies;

namespace NetworkGuardian.Portable.Ui.Pages;

internal sealed class PreferenceCard
{
    private bool _showHistory;
    public int Render(PageContext ctx, Rectangle area, int y)
    {
        var host = ctx.Host;
        var enabled = host.Config.Quality.LearningEnabled;
        var question = enabled ? host.SurveyQuestion : null;
        var answers = _showHistory ? host.Survey.Answers.TakeLast(5).Reverse().ToArray() : Array.Empty<SurveyAnswer>();
        var width = area.Width - ctx.Scale(32);
        var prompt = !enabled ? "本地学习已关闭，启用后可以回答网络选择题。" : question?.Prompt ?? "回答选择题，帮助网络管家理解您的环境与习惯。点击“换一道题”开始。";
        var promptHeight = ctx.Canvas.MeasureWrappedHeight(prompt, width, TextStyle.Body);
        var optionHeights = question?.Options.Select(o => Math.Max(ctx.Scale(34), ctx.Canvas.MeasureWrappedHeight(o.Label, width - ctx.Scale(40), TextStyle.Body) + ctx.Scale(12))).ToArray() ?? Array.Empty<int>();
        var historyLines = answers.Select(a => $"{a.AnswerContext.CapturedAt.ToLocalTime():MM-dd HH:mm} · {a.Question.Prompt} → {a.Question.Options.FirstOrDefault(o => o.Value == a.Value)?.Label} · 样本权重 {a.Reliability:P0}\n{a.Evidence}").ToArray();
        var historyHeights = historyLines.Select(t => ctx.Canvas.MeasureWrappedHeight(t, width - ctx.Scale(100), TextStyle.Caption) + ctx.Scale(12)).ToArray();
        var card = new Rectangle(area.Left, y, area.Width, ctx.Scale(148) + promptHeight + optionHeights.Sum() + optionHeights.Length * ctx.Scale(6) + historyHeights.Sum());
        ctx.Canvas.Card(card);
        var x = card.Left + ctx.Scale(16);
        var cy = y + ctx.Scale(12);
        ctx.Canvas.Text($"偏好选择题 · {NetworkQuestionBank.Templates.Count} 题", new Rectangle(x, cy, width, ctx.Scale(24)), Palette.TextPrimary, TextStyle.Section);
        cy += ctx.Scale(28);
        ctx.Canvas.Text(prompt, new Rectangle(x, cy, width, promptHeight), Palette.TextPrimary, TextStyle.Body, wrap: TextWrap.Wrap);
        cy += promptHeight + ctx.Scale(8);
        const string operation = "survey-answer";
        if (question is not null)
        for (var i = 0; i < question.Options.Count; i++)
        {
            var choice = question.Options[i];
            Widgets.ButtonAt(ctx, new Rectangle(x, cy, width, optionHeights[i]), $"{(char)('A' + i)}：{choice.Label}",
                () => ctx.Window.RunBackground(operation, "正在记录状态并学习回答", () => host.AnswerSurveyAsync(question.Id, choice.Value), "已按证据加权学习，已换下一题"),
                enabled: !ctx.Window.IsOperationRunning(operation));
            cy += optionHeights[i] + ctx.Scale(6);
        }
        ctx.Canvas.Text($"已答 {host.Survey.TotalAnswers} 题 · 神经网络个性化更新 {host.Brain.PersonalSteps} 次 · 回答不直接执行换网",
            new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextMuted, TextStyle.Caption);
        cy += ctx.Scale(24);
        ctx.Canvas.Text("保存答题时状态，结合探测降权；操作置信度见任务卡，置信度不代表成功概率。",
            new Rectangle(x, cy, width, ctx.Scale(20)), Palette.TextSecondary, TextStyle.Caption);
        cy += ctx.Scale(26);
        for (var i = 0; i < answers.Length; i++)
        {
            var answer = answers[i];
            ctx.Canvas.Text(historyLines[i], new Rectangle(x, cy, width - ctx.Scale(100), historyHeights[i]), Palette.TextSecondary, TextStyle.Caption, wrap: TextWrap.Wrap);
            Widgets.Button(ctx, card.Right - ctx.Scale(100), cy, "删除记录", () => ctx.Window.RunBackground("survey-delete", "正在删除并重新训练",
                () => host.RemoveSurveyAnswerAsync(answer.Question.Id), "记录已删除，模型已从保留数据重新训练"), enabled: !ctx.Window.IsOperationRunning("survey-delete"));
            cy += historyHeights[i];
        }
        var next = Widgets.Button(ctx, x, cy, enabled ? "跳过 / 换题" : "启用学习", () =>
        {
            if (enabled) host.NextSurveyQuestion();
            else ctx.Window.RunBackground("survey-enable", "正在启用本地学习", async () =>
            {
                await ctx.Window.App.UpdateConfigAsync(c => c.Quality.LearningEnabled = true).ConfigureAwait(false);
                host.NextSurveyQuestion();
            });
        }, enabled: !ctx.Window.IsOperationRunning(operation), confirm: !enabled);
        var history = Widgets.Button(ctx, next.Right + ctx.Scale(8), cy, _showHistory ? "收起记录" : "查看记录", () => _showHistory = !_showHistory, confirm: false);
        var export = Widgets.Button(ctx, history.Right + ctx.Scale(8), cy, "导出答题集", () => ctx.Window.RunBackground("survey-export", "正在导出本机答题集",
            () => host.ExportSurveyAsync(), "已导出到配置目录 network-survey-export.json"));
        Widgets.Button(ctx, export.Right + ctx.Scale(8), cy, "重置学习", () => ctx.Window.RunBackground("survey-reset", "正在重置答题集与模型",
            () => host.ResetPreferencesAsync(), "已恢复内置预训练模型"));
        return card.Bottom + ctx.Scale(12);
    }
}
