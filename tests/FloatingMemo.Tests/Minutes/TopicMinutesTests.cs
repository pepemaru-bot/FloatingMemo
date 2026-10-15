using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

public sealed class TopicMinutesTests
{
    private const string Note = """
        # 定例（架空）
        - 参加: 田中、佐藤
        - 自分用: 帰りに資料を印刷

        以下、AIによる文字起こし
        本日の定例会議を始めます。リリース日は十一月一日に決まりました。資料の準備は田中さんが金曜日までに行うことになりました。デザインの変更はどうするか、次回までに検討しましょう。
        """;

    private const string Answer = """
        <think></think>
        はい、議事録です。
        ## 概要
        - 定例会議でリリース日と資料を確認した。
        - 参加: 田中、佐藤（メモより）
        ## リリース日
        - 【決定】リリース日は十一月一日（原文:「リリース日は十一月一日に決まりました」）
        ## 資料の準備
        - 【ToDo】資料の準備 — 担当: 田中 / 期限: 金曜日（原文:「資料の準備は田中さんが金曜日までに行う」）
        - 【ToDo】資料の確認 — 担当: 佐藤 / 期限: 来週月曜（原文:「資料の準備は田中さんが金曜日までに行う」）
        ## デザインの変更
        - 【未決】デザインの変更（原文:「デザインの変更はどうするか、次回までに検討しましょう」）
        - 【決定】予算は百万円（原文:「予算は百万円で承認されました」）
        """;

    private static (MinutesDraft Draft, ScriptedGenerator Model) Run(string note, params string[] answers)
    {
        var model = new ScriptedGenerator(answers);
        var draft = new MinutesGenerator(model, MinutesStyle.Topics).GenerateAsync(note, "定例", new DateTime(2026, 10, 15, 9, 0, 0)).Result;
        return (draft, model);
    }

    [Fact]
    public void NotesAreSentApartFromTheTranscript()
    {
        var (_, model) = Run(Note, Answer);
        string prompt = model.Prompts.Single();
        int notes = prompt.IndexOf("<メモ>", StringComparison.Ordinal);
        int transcript = prompt.IndexOf("<文字起こし>", StringComparison.Ordinal);
        Assert.True(notes >= 0 && transcript > notes);
        Assert.Contains("参加: 田中、佐藤", prompt[notes..transcript]);
        Assert.DoesNotContain("以下、AIによる文字起こし", prompt);           // the header is not content
        Assert.DoesNotContain("参加: 田中", prompt[transcript..]);          // notes are not "said"
        Assert.Contains("リリース日は十一月一日に決まりました", prompt[transcript..]);
    }

    [Fact]
    public void Headings_ComeFromTheAnswer_InOrder_WithoutTheFixedTemplate()
    {
        var (draft, _) = Run(Note, Answer);
        string md = draft.Markdown;
        int overview = md.IndexOf("## 概要", StringComparison.Ordinal);
        int release = md.IndexOf("## リリース日", StringComparison.Ordinal);
        int design = md.IndexOf("## デザインの変更", StringComparison.Ordinal);
        Assert.True(overview >= 0 && release > overview && design > release);
        Assert.DoesNotContain("## 主な議題", md);
        Assert.DoesNotContain("## 次回確認事項", md);
        Assert.DoesNotContain("はい、議事録です", md);
        Assert.Contains("AI下書き", md);
    }

    [Fact]
    public void Tasks_AreCheckboxes_AndInventedAssigneesAreRemoved()
    {
        var (draft, _) = Run(Note, Answer);
        Assert.Contains("- [ ] 資料の準備 — 担当: 田中 / 期限: 金曜日", draft.Markdown);
        Assert.DoesNotContain("佐藤 /", draft.Markdown);     // 佐藤 is only in the notes, never said
        Assert.DoesNotContain("来週月曜", draft.Markdown);
        Assert.Contains("担当: 未確認（AIの推測を除去）", draft.Markdown);
    }

    [Fact]
    public void DecisionWithAQuoteNotInTheTranscript_IsFlagged_NotesNeedNoQuote()
    {
        var (draft, _) = Run(Note, Answer);
        string budget = draft.Markdown.Split('\n').Single(l => l.Contains("予算"));
        Assert.EndsWith(MinutesValidator.QuoteMissingMark, budget);
        string participants = draft.Markdown.Split('\n').Single(l => l.Contains("（メモより）"));
        Assert.DoesNotContain("⚠", participants);
    }

    [Fact]
    public void Parts_SameTopicIsMerged_RepeatedItemsKeptOnce()
    {
        var merged = TopicMinutes.Merge(
        [
            "## 概要\n- 前半\n## リリース日\n- 【決定】リリース日は十一月一日（原文:「リリース日は十一月一日に決まりました」）",
            "## 概要\n- 後半\n## リリース日について\n- 【決定】リリース日は十一月一日（原文:「リリース日は十一月一日に決まりました」）\n- 案内は来週出す\n## 予算\n- なし",
        ]);
        Assert.Equal(["概要", "リリース日"], merged.Select(m => m.Title));
        Assert.Equal(["- 前半", "- 後半"], merged[0].Lines);
        Assert.Equal(2, merged[1].Lines.Count); // the decision once, plus the new item
    }

    [Fact]
    public void LooseAnswers_AreReadAsMeant()
    {
        // Seen with Qwen3: quote as a separate item, "(「…」)" without 原文:, spaces in the mark, a bare checkbox.
        var topics = TopicMinutes.Parse("""
            ## 研修
            - 【 決定 】研修は十二月
            - （原文:「研修は十二月に一回行うことに決まりました。」）
            - 【 todo】資料 — 担当: 森 / 期限: 水曜 (「森さんが水曜日までに作ります」)
            [ ] 会場の確認
            """);
        Assert.Equal(
        [
            "- 【決定】研修は十二月 （原文:「研修は十二月に一回行うことに決まりました。」）",
            "- [ ] 資料 — 担当: 森 / 期限: 水曜 (「森さんが水曜日までに作ります」)",
            "- [ ] 会場の確認",
        ], topics.Single().Lines);

        var warnings = new List<string>();
        string body = TopicMinutes.Check(topics, "研修は十二月に一回行うことに決まりました。森さんが水曜日までに作ります。", string.Empty, warnings);
        Assert.DoesNotContain("引用なし", body.Split('\n').Single(l => l.Contains("研修は十二月")));
        Assert.DoesNotContain("⚠", body.Split('\n').Single(l => l.Contains("資料")));
    }

    [Theory]
    [InlineData("- 【決定】テスト計画は森さんが来週の水曜日までに作る（原文:「森さんが来週の水曜日までに作る」）", true)]
    [InlineData("- 【決定】問い合わせフォームの修正は石井さんが今週中に行う", true)]
    [InlineData("- 【決定】リリース日は十一月二十日（原文:「田中さんが言うとおり十一月二十日までに」）", false)] // only the quote names someone
    [InlineData("- 【決定】研修は十二月に行う", false)]
    [InlineData("- 森さんが水曜日までに作る", false)] // not a decision
    public void DecisionWithAssigneeAndDeadline_IsAlsoATask(string line, bool task)
    {
        string result = TopicMinutes.AsTask(line);
        Assert.Equal(task, result.StartsWith("- [ ] 【決定】", StringComparison.Ordinal));
        Assert.Equal(line[2..], result[(task ? 6 : 2)..]);
    }

    [Fact]
    public void QualityAnalysis_ReadsTheMarks()
    {
        const string draft = """
            ## 概要
            - 定例会議
            ## デザイン
            - 【決定】デザインの変更はどうするか（原文:「デザインの変更はどうするか、次回までに検討しましょう」）
            - [ ] 資料 — 担当: 田中 / 期限: 金曜日（原文:「資料の準備は田中さんが金曜日までに行う」）
            """;
        var report = MinutesQuality.Analyze(draft, Note);
        Assert.Equal(1, report.Count(QualityReport.UndecidedAsDecision));
        Assert.Equal(3, report.Items);
    }

    [Fact]
    public void NoteWithoutATranscriptHeader_IsUsedAsTheTranscript()
    {
        var (_, model) = Run("リリース日は十一月一日に決まりました。資料の準備は田中さんが行います。", "## 概要\n- 定例");
        Assert.DoesNotContain("<メモ>", model.Prompts.Single());
        Assert.Contains("リリース日は十一月一日", model.Prompts.Single());
    }
}
