namespace FloatingMemo.Tests.Minutes;

public sealed class MinutesScoreTests
{
    [Fact]
    public void ACorrectDraft_ScoresFull_AWrongOneDoesNot()
    {
        const string good = """
            # 議事録（AI下書き）
            ## 概要
            - 夏祭りの段取りを話した
            ## 会場
            - 【決定】会場は架空市民ホールの前の広場 〔B002〕
            - 雨の場合はホールの中 〔B003〕
            ## 屋台
            - 【決定】屋台は八店 〔B004〕
            ## ポスター
            - [ ] ポスターの作成 — 担当: 高橋 / 期限: 来週の金曜日 〔B005〕
            ## 花火
            - 【未決】花火を上げるか 〔B006〕
            ## 駐車場
            - 【未決】駐車場の台数 〔B007〕 ⚠要確認（根拠の原文は内容の一部しか示していない）
            """;
        var s = MinutesScore.Score(EvaluationMeetings.Short, good);
        Assert.Equal((5, 2, 1, 1, 0, 0), (s.Topics, s.Decisions, s.Tasks, s.Numbers, s.UndecidedAsDecision, s.InventedNumbers));

        const string bad = """
            ## 決定事項
            - 【決定】花火を上げる 〔B006〕
            - 屋台は十店
            """;
        var b = MinutesScore.Score(EvaluationMeetings.Short, bad);
        Assert.Equal(1, b.UndecidedAsDecision);
        Assert.Equal(1, b.InventedNumbers); // 10 was never said
        Assert.Equal(0, b.Tasks);
    }
}
