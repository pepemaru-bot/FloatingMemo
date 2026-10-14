using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// A made-up meeting used to compare models under identical conditions: the person's own notes
/// and a speech-recognition transcript with typical errors, plus what a correct minutes draft
/// must and must not contain. No real people, schools or meetings.
/// </summary>
public static class EvaluationMeeting
{
    public const string HumanNotes = """
        # 学習相談の打ち合わせ（架空）
        - 参加: 講師 山本、保護者 佐々木
        - 気になる点: 算数の割合、架空学園中学校の過去問
        """;

    // Contains misrecognitions the dictionary knows: 「加工学園」(架空学園), 「時方」(解き方).
    public const string Transcript = """
        今日は加工学園中学校の過去問の進め方について話しました。
        割合の問題は時方を確認してから解き直すことに決まりました。
        過去問は毎週土曜日に一年分ずつ進めることで合意しました。
        模試の申し込みは山本さんが今週金曜日までに行います。
        志望校を一つ増やすかどうかは、次回の面談までに検討しましょう。
        塾の宿題の量を減らすかはまだ決まっていません。
        """;

    public static string Note => HumanNotes + "\n\n以下、AIによる文字起こし\n" + Transcript.Replace("\r\n", "\n").Trim();

    public static IReadOnlyList<TermEntry> Dictionary { get; } =
    [
        new() { Term = "架空学園中学校", Aliases = ["架空学園"], Misrecognitions = ["加工学園"], Category = "学校名（東京都）" },
        new() { Term = "解き方", Misrecognitions = ["時方"], Category = "指導・学習管理" },
        new() { Term = "過去問", Category = "受験・志望校" },
        new() { Term = "模試", Category = "塾・教材・試験" },
    ];

    /// <summary>Things a correct draft states as decisions (each item: words that must appear together).</summary>
    public static readonly string[][] ExpectedDecisions = [["解き直"], ["土曜"]];

    /// <summary>Open questions that must NOT appear as decisions.</summary>
    public static readonly string[] MustNotBeDecisions = ["志望校", "宿題"];

    /// <summary>The one task with a stated owner and deadline.</summary>
    public static readonly string[] ExpectedTodo = ["模試", "山本", "金曜"];
}

public sealed class MinutesQualityTests
{
    [Fact]
    public void NoteSections_SeparatesHumanNotes_FromTranscriptBlocks()
    {
        string note = "自分のメモ\n- 要点\n\n以下、AIによる文字起こし\n一つ目の文字起こし。\n二行目。\n\n手で書いた追記\n\n以下、AIによる文字起こし\n二つ目。";
        var sections = NoteSections.Split(note);
        Assert.Equal("自分のメモ\n- 要点\n\n\n手で書いた追記", sections.HumanNotes);
        Assert.Equal(["一つ目の文字起こし。\n二行目。", "二つ目。"], sections.Transcripts);
        Assert.DoesNotContain("以下、AIによる文字起こし", sections.HumanNotes);

        var evaluation = NoteSections.Split(EvaluationMeeting.Note);
        Assert.Contains("気になる点", evaluation.HumanNotes);
        Assert.Contains("模試の申し込み", evaluation.AllTranscripts);
        Assert.DoesNotContain("気になる点", evaluation.AllTranscripts);
    }

    private const string GoodDraft = """
        # 議事録（AI下書き）
        ## 会議の概要
        - 架空学園中学校の過去問の進め方を話した。
        ## 主な議題
        - 過去問の進め方
        ## 議論の要点
        - なし
        ## 決定事項
        - 割合は解き方を確認してから解き直す（原文:「割合の問題は時方を確認してから解き直すことに決まりました」）
        - 過去問は毎週土曜日に一年分（原文:「過去問は毎週土曜日に一年分ずつ進めることで合意しました」）
        ## ToDo（担当者・期限）
        - [ ] 模試の申し込み — 担当: 山本 / 期限: 今週金曜日（原文:「模試の申し込みは山本さんが今週金曜日までに行います」）
        ## 未決事項
        - 志望校を一つ増やすか（原文:「志望校を一つ増やすかどうかは、次回の面談までに検討しましょう」）
        - 塾の宿題の量（原文:「塾の宿題の量を減らすかはまだ決まっていません」）
        ## 次回確認事項
        - なし
        """;

    [Fact]
    public void GoodDraft_HasNoIssues()
    {
        var report = MinutesQuality.Analyze(GoodDraft, EvaluationMeeting.Transcript, EvaluationMeeting.Dictionary);
        Assert.Empty(report.Issues);
        Assert.Equal(7, report.Items); // overview 1, topic 1, decisions 2, ToDo 1, open questions 2
    }

    [Fact]
    public void BadDraft_EveryKindOfProblemIsFound()
    {
        const string bad = """
            ## 主な議題
            - 過去問の進め方について
            - 過去問の進め方について。
            ## 決定事項
            - 志望校を一つ増やす（原文:「志望校を一つ増やすかどうかは、次回の面談までに検討しましょう」）
            - 予算を増やす
            - 加工学園中学校の過去問を進める（原文:「今日は加工学園中学校の過去問の進め方について話しました」）
            - 来月から週二回にする（原文:「来月から週二回にします」）
            ## ToDo（担当者・期限）
            - [ ] 内容 — 担当: 未確認 / 期限: 未確認
            - [ ]
            - [ ] 確認する（原文:「…」）
            - [ ] 内容 — 担当: 山本 / 期限: 未確認（AIの推測を除去）
            """;
        var report = MinutesQuality.Analyze(bad, EvaluationMeeting.Transcript, EvaluationMeeting.Dictionary);

        Assert.Equal(1, report.Count(QualityReport.Duplicate));
        Assert.Equal(1, report.Count(QualityReport.UndecidedAsDecision));      // 「検討しましょう」 as a decision
        Assert.Equal(2, report.Count(QualityReport.UnsupportedDecision));      // no quote / quote not in the transcript
        Assert.Equal(4, report.Count(QualityReport.Dummy));                    // template text (twice), empty item, "…" quote
        Assert.Equal(1, report.Count(QualityReport.Misrecognition));           // 加工学園 left as is
        Assert.Contains("重複 1", report.Summary());
    }

    [Fact]
    public void CorrectionAppendix_IsNotJudged()
    {
        string draft = GoodDraft + "\n---\n\n## 校正の記録\n- 「時方」→「解き方」：修正済み\n\n## 校正済み文字起こし\n\n加工学園〔要確認: 架空学園？〕\n";
        Assert.Empty(MinutesQuality.Analyze(draft, EvaluationMeeting.Transcript, EvaluationMeeting.Dictionary).Issues);
    }
}
