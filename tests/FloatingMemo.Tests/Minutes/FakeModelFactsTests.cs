using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>The E2E test model must work with every layout (it stands in for Qwen in UI tests).</summary>
public sealed class FakeModelFactsTests
{
    [Theory]
    [InlineData(MinutesStyle.Facts)]
    [InlineData(MinutesStyle.Topics)]
    [InlineData(MinutesStyle.FixedSections)]
    public async Task FakeModel_FinishesQuickly_ForALongTranscript(MinutesStyle style)
    {
        string speech = string.Concat(Enumerable.Repeat("本日の定例会議の議事録です。最初に先週の進捗を確認しました。次に来月のリリース計画について話し合いました。担当者はそれぞれの作業を金曜日までに完了させる予定です。", 6));
        string note = "- メモ\n\n" + TranscriptInsertion.Header + "\n" + speech;
        var reports = new List<MinutesProgress>();
        var task = new MinutesGenerator(new FakeMinutesModel(), style).GenerateAsync(note, "t", new DateTime(2026, 10, 16),
            new SynchronousProgress<MinutesProgress>(reports.Add));
        var finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(20)));
        Assert.Same(task, finished);
        Assert.Contains(style == MinutesStyle.FixedSections ? "## 会議の概要" : "## 概要", (await task).Markdown);
        Assert.Contains(reports, r => r.Step == "議事録を作成" && r.Characters > 0);
    }
}
