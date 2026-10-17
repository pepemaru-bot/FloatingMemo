using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Splitting the source into units and parts (made-up text).</summary>
public sealed class FactChunkingTests
{
    [Fact]
    public void AVeryLongSentence_BecomesSeveralUnits_KeepingEveryCharacter()
    {
        // Speech recognition sometimes writes minutes of talk without a single "。".
        string run = string.Concat(Enumerable.Range(1, 40).Select(i => $"資料の確認について話しました、項目{i}を見直します "));
        var units = SourceUnits.From(NoteSections.Split(TranscriptInsertion.Header + "\n" + run));
        Assert.True(units.Count > 5);
        Assert.All(units, u => Assert.True(u.Text.Length <= SourceUnits.MaxUnitCharacters));
        Assert.Equal(MinutesValidator.Normalize(run), MinutesValidator.Normalize(string.Concat(units.Select(u => u.Text))));
    }

    [Fact]
    public void ManyUnits_GetFourDigitIds_AndTheChecksStillReadThem()
    {
        string many = string.Concat(Enumerable.Repeat("短い文です。", 1200));
        var units = SourceUnits.From(NoteSections.Split(TranscriptInsertion.Header + "\n" + many));
        Assert.Equal("B1200", units[^1].Id);

        const string draft = "## 概要\n- 会議\n## 話題\n- 【決定】短い文 〔B1200〕\n\n---\n\n## 原文（番号付き）\n- B1200 短い文です。\n";
        var report = MinutesQuality.Analyze(draft, many);
        Assert.Equal(0, report.Count(QualityReport.UnsupportedDecision)); // the 4-digit reference was read
    }

    [Fact]
    public void Parts_UseTheGivenTokenCounter_AndLeaveRoomForTheContextLines()
    {
        var units = Enumerable.Range(1, 60).Select(i => new SourceUnit($"B{i:000}", $"これは{i}番目の発言で、内容は資料の確認です。", false)).ToList();
        int calls = 0;
        int Count(string text) { calls++; return text.Length; } // 1 token per character: a strict counter

        var parts = FactMinutes.Group(units, Count, contextTokens: 8192, contentTokens: 300);
        Assert.True(calls > units.Count);
        Assert.Equal(units.Select(u => u.Id), parts.SelectMany(p => p).Select(u => u.Id)); // every unit once, in order
        for (int i = 0; i < parts.Count; i++)
        {
            int content = parts[i].Sum(u => Count(u.Id + " " + u.Text) + 1);
            int context = FactMinutes.ContextBefore(parts, i).Sum(u => Count("【文脈】" + u.Id + " " + u.Text) + 1);
            Assert.True(parts[i].Count == 1 || content + context <= 300, $"part {i}: {content} + {context}");
        }
    }

    [Fact]
    public void ContextLines_AreNotExtractable()
    {
        var units = Enumerable.Range(1, 60).Select(i => new SourceUnit($"B{i:000}", $"これは{i}番目の発言です。", false)).ToList();
        var parts = FactMinutes.Group(units, t => t.Length, contentTokens: 200);
        string prompt = FactPrompt.User(FactMinutes.ContextBefore(parts, 1), parts[1], 1, parts.Count);
        string firstContext = FactMinutes.ContextBefore(parts, 1)[0].Id;
        Assert.Contains("【文脈】" + firstContext, prompt);
        Assert.DoesNotContain(parts[1], u => u.Id == firstContext);
    }
}
