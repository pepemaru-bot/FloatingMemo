using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// A made-up meeting used to compare models under identical conditions: the person's own notes
/// and a speech-recognition transcript with typical errors, plus what a correct minutes draft
/// must and must not contain. No real people, places or meetings.
/// </summary>
public static class EvaluationMeeting
{
    public const string HumanNotes = """
        # 地域清掃イベントの打ち合わせ（架空）
        - 参加: 実行委員 山本、自治会 佐々木
        - 気になる点: ゴミ袋の数、架空ホールの予約
        """;

    // Contains misrecognitions the dictionary knows: 「加工ホール」(架空ホール), 「美品」(備品).
    public const string Transcript = """
        今日は加工ホールでの清掃イベントの進め方について話しました。
        ゴミ袋は美品の在庫を確認してから追加で買うことに決まりました。
        集合は毎月第一土曜日の朝九時にすることで合意しました。
        ホールの予約は山本さんが今週金曜日までに行います。
        雨天時の会場を一つ増やすかどうかは、次回の打ち合わせまでに検討しましょう。
        参加賞の量を減らすかはまだ決まっていません。
        """;

    public static string Note => HumanNotes + "\n\n以下、AIによる文字起こし\n" + Transcript.Replace("\r\n", "\n").Trim();

    public static IReadOnlyList<TermEntry> Dictionary { get; } =
    [
        new() { Term = "架空ホール", Misrecognitions = ["加工ホール"], Category = "施設名" },
        new() { Term = "備品", Misrecognitions = ["美品"], Category = "一般用語" },
        new() { Term = "ゴミ袋", Category = "一般用語" },
        new() { Term = "参加賞", Category = "イベント" },
    ];

    /// <summary>Things a correct draft states as decisions (each item: words that must appear together).</summary>
    public static readonly string[][] ExpectedDecisions = [["追加"], ["土曜"]];

    /// <summary>Open questions that must NOT appear as decisions.</summary>
    public static readonly string[] MustNotBeDecisions = ["雨天", "参加賞"];

    /// <summary>The one task with a stated owner and deadline.</summary>
    public static readonly string[] ExpectedTodo = ["予約", "山本", "金曜"];
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
        Assert.Contains("ホールの予約", evaluation.AllTranscripts);
        Assert.DoesNotContain("気になる点", evaluation.AllTranscripts);
    }

    private const string GoodDraft = """
        # 議事録（AI下書き）
        ## 会議の概要
        - 架空ホールでの清掃イベントの進め方を話した。
        ## 主な議題
        - 清掃イベントの進め方
        ## 議論の要点
        - なし
        ## 決定事項
        - ゴミ袋は備品の在庫を確認してから追加で買う（原文:「ゴミ袋は美品の在庫を確認してから追加で買うことに決まりました」）
        - 集合は毎月第一土曜日の朝九時（原文:「集合は毎月第一土曜日の朝九時にすることで合意しました」）
        ## ToDo（担当者・期限）
        - [ ] ホールの予約 — 担当: 山本 / 期限: 今週金曜日（原文:「ホールの予約は山本さんが今週金曜日までに行います」）
        ## 未決事項
        - 雨天時の会場を一つ増やすか（原文:「雨天時の会場を一つ増やすかどうかは、次回の打ち合わせまでに検討しましょう」）
        - 参加賞の量（原文:「参加賞の量を減らすかはまだ決まっていません」）
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
            - 清掃イベントの進め方について
            - 清掃イベントの進め方について。
            ## 決定事項
            - 雨天時の会場を一つ増やす（原文:「雨天時の会場を一つ増やすかどうかは、次回の打ち合わせまでに検討しましょう」）
            - 予算を増やす
            - 加工ホールでの清掃イベントを進める（原文:「今日は加工ホールでの清掃イベントの進め方について話しました」）
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
        Assert.Equal(1, report.Count(QualityReport.Misrecognition));           // 加工ホール left as is
        Assert.Contains("重複 1", report.Summary());
    }

    [Fact]
    public void CorrectionAppendix_IsNotJudged()
    {
        string draft = GoodDraft + "\n---\n\n## 校正の記録\n- 「美品」→「備品」：修正済み\n\n## 校正済み文字起こし\n\n加工ホール〔要確認: 架空ホール？〕\n";
        Assert.Empty(MinutesQuality.Analyze(draft, EvaluationMeeting.Transcript, EvaluationMeeting.Dictionary).Issues);
    }
}
