using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Stands in for the LLM: returns scripted answers and records the prompts.</summary>
public sealed class ScriptedGenerator(params string[] answers) : ILocalTextGenerator
{
    private int _next;

    public List<string> Prompts { get; } = [];

    public string ModelName => "テスト用モデル";

    public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        Prompts.Add(user);
        return Task.FromResult(answers[Math.Min(_next++, answers.Length - 1)]);
    }
}

public sealed class MinutesTests
{
    // Synthetic meeting (no real people or meetings).
    private const string Transcript =
        "本日の定例会議を始めます。最初に先週の進捗を確認しました。リリース日は十一月一日に決まりました。" +
        "資料の準備は田中さんが金曜日までに行うことになりました。デザインの変更はどうするか、次回までに検討しましょう。";

    private const string Answer = """
        <think>考え中</think>
        はい、議事録です。
        ## 会議の概要
        - 定例会議で進捗とリリース日を確認した。
        ## 主な議題
        - 先週の進捗（原文:「先週の進捗を確認しました」）
        ## 議論の要点
        - なし
        ## 決定事項
        - リリース日は十一月一日（原文:「リリース日は十一月一日に決まりました」）
        - 予算は百万円（原文:「予算は百万円で承認されました」）
        ## ToDo（担当者・期限）
        - [ ] 資料の準備 — 担当: 田中 / 期限: 金曜日（原文:「資料の準備は田中さんが金曜日までに行う」）
        - [ ] デザイン案の作成 — 担当: 佐藤 / 期限: 来週月曜（原文:「デザインの変更はどうするか」）
        ## 未決事項
        - デザインの変更（原文:「デザインの変更はどうするか、次回までに検討しましょう」）
        """;

    private static (MinutesDraft Draft, ScriptedGenerator Model) Run(string transcript, params string[] answers)
    {
        var model = new ScriptedGenerator(answers);
        var draft = new MinutesGenerator(model).GenerateAsync(transcript, "定例会議", new DateTime(2026, 10, 11, 9, 0, 0)).Result;
        return (draft, model);
    }

    [Fact]
    public void Draft_HasAllSectionsInOrder_AndTheAiNotice()
    {
        var (draft, _) = Run(Transcript, Answer);
        int last = -1;
        foreach (string section in MinutesPrompt.Sections)
        {
            int at = draft.Markdown.IndexOf("## " + section, StringComparison.Ordinal);
            Assert.True(at > last, $"section missing or out of order: {section}");
            last = at;
        }

        Assert.Contains("AI下書き", draft.Markdown);
        Assert.Contains("[[定例会議]]", draft.Markdown);
        Assert.Contains("テスト用モデル", draft.Markdown);
        Assert.DoesNotContain("考え中", draft.Markdown);      // <think> removed
        Assert.DoesNotContain("はい、議事録です", draft.Markdown); // preamble removed
    }

    [Fact]
    public void MissingSection_IsWrittenAsNone()
    {
        var (draft, _) = Run(Transcript, Answer);
        Assert.Contains("## 次回確認事項\n- なし", draft.Markdown);
    }

    [Fact]
    public void InventedAssigneeAndDeadline_BecomeUnconfirmed()
    {
        var (draft, _) = Run(Transcript, Answer);
        Assert.Contains("担当: 田中", draft.Markdown);          // said in the meeting: kept
        Assert.Contains("期限: 金曜日", draft.Markdown);
        Assert.DoesNotContain("佐藤", draft.Markdown);           // never mentioned: removed
        Assert.DoesNotContain("来週月曜", draft.Markdown);
        Assert.Contains("担当: 未確認（AIの推測を除去）", draft.Markdown);
        Assert.Contains("期限: 未確認（AIの推測を除去）", draft.Markdown);
    }

    [Fact]
    public void QuoteNotInTranscript_IsFlagged_RealQuotesAreNot()
    {
        var (draft, _) = Run(Transcript, Answer);
        string budget = draft.Markdown.Split('\n').Single(l => l.Contains("予算"));
        Assert.EndsWith(MinutesValidator.QuoteMissingMark, budget);
        string release = draft.Markdown.Split('\n').Single(l => l.Contains("リリース日は十一月一日（"));
        Assert.DoesNotContain("⚠", release);
        Assert.Contains(draft.Warnings, w => w.Contains("予算"));
    }

    [Fact]
    public void DecisionWithoutQuote_IsFlagged()
    {
        var (draft, _) = Run(Transcript, "## 決定事項\n- 何かが決まった");
        Assert.Contains("- 何かが決まった" + MinutesValidator.NoQuoteMark, draft.Markdown);
    }

    [Fact]
    public void QuoteComparison_IgnoresSpacesAndPunctuation()
    {
        var (draft, _) = Run(Transcript, "## 決定事項\n- リリース日（原文:「リリース日は 十一月一日に、決まりました。」）");
        Assert.DoesNotContain("⚠", draft.Markdown.Split('\n').Single(l => l.Contains("リリース日（")));
    }

    [Fact]
    public void Prompt_ContainsTheRulesAndTheTranscript()
    {
        var (_, model) = Run(Transcript, Answer);
        Assert.Contains("未確認", MinutesPrompt.System);
        Assert.Contains("決定事項に入れない", MinutesPrompt.System);
        Assert.Contains(Transcript, model.Prompts.Single());
        Assert.EndsWith("/no_think", model.Prompts.Single()); // Qwen3: answer directly, no long reasoning
    }

    [Fact]
    public void LongTranscript_IsSplitAtSentenceEnds_AndPartsAreMerged()
    {
        string sentence = "会議では資料の確認を行いました。";
        string longTranscript = string.Concat(Enumerable.Repeat(sentence, 900)); // ~14,000 characters
        var chunks = MinutesPrompt.Split(longTranscript, MinutesGenerator.ChunkCharacters);
        Assert.True(chunks.Count >= 3);
        Assert.All(chunks, c => Assert.EndsWith("。", c));
        Assert.Equal(MinutesValidator.Normalize(longTranscript), MinutesValidator.Normalize(string.Concat(chunks)));

        var (draft, model) = Run(longTranscript,
            "## 主な議題\n- 資料の確認（原文:「資料の確認を行いました」）",
            "## 主な議題\n- なし",
            "## 主な議題\n- 資料の確認その2（原文:「資料の確認を行いました」）");
        Assert.Equal(chunks.Count, model.Prompts.Count);
        Assert.Contains("1/", model.Prompts[0]);
        Assert.Contains("資料の確認その2", draft.Markdown);
        Assert.DoesNotContain("- なし\n- 資料", draft.Markdown); // "なし" from one part is not mixed in
    }

    [Fact]
    public void WordsNotInTheTranscript_AreFlagged_AndDoubleBulletsFixed()
    {
        var (draft, _) = Run(Transcript,
            "## 主な議題\n- インフォームフォームの件\n- リリース日の確認\n## ToDo（担当者・期限）\n" +
            "- - [ ] 資料の準備 — 担当: 田中 / 期限: 金曜日（原文:「資料の準備は田中さんが金曜日までに行う」）");
        Assert.Contains("インフォームフォームの件 ⚠要確認（原文にない語: インフォームフォーム）", draft.Markdown);
        Assert.DoesNotContain("リリース日の確認 ⚠", draft.Markdown);
        Assert.Contains("\n- [ ] 資料の準備", draft.Markdown);
        Assert.DoesNotContain("- - [ ]", draft.Markdown);
    }

    [Fact]
    public void QuoteOnTheNextLine_OrWithHalfWidthParentheses_IsAccepted()
    {
        var (draft, _) = Run(Transcript,
            "## 決定事項\n- リリース日は十一月一日\n  (原文:「リリース日は十一月一日に決まりました」)");
        string line = draft.Markdown.Split('\n').Single(l => l.StartsWith("- リリース日"));
        Assert.Contains("原文", line);
        Assert.DoesNotContain("⚠", line);
    }

    [Fact]
    public void TheFormatsOwnWords_AreNotFlaggedAsInventions()
    {
        // Seen with Qwen3-1.7B: "未確認" (written by the checks themselves) was reported as a word
        // missing from the transcript.
        var (draft, _) = Run(Transcript, "## ToDo（担当者・期限）\n- [ ] 資料の準備 — 担当: 未確認 / 期限: 未確認（原文:「資料の準備は田中さんが金曜日までに行う」）");
        Assert.DoesNotContain("原文にない語", draft.Markdown);
    }

    [Fact]
    public void TranscriptIsNeverModified()
    {
        string transcript = Transcript;
        Run(transcript, Answer);
        Assert.Equal(Transcript, transcript);
    }
}
