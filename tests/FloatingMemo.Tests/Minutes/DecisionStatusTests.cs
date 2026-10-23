using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// Decided vs not decided must never be reversed. Made-up meetings only. The first case is the
/// reported one: 「未決定」 heard as 「見決定」 and written as 【決定】.
/// </summary>
public sealed class DecisionStatusTests
{
    [Theory]
    [InlineData("公開日とVisperのモデル変更については見決定です。", DecisionState.Unclear)]
    [InlineData("公開日とWhisperのモデル変更は未決定です。", DecisionState.Open)]
    [InlineData("予算はまだ決まっていません。", DecisionState.Open)]
    [InlineData("決定は次回に持ち越しです。", DecisionState.Open)]
    [InlineData("担当者はまだ決めていません。", DecisionState.Open)]
    [InlineData("結論が出ませんでした。", DecisionState.Open)]
    [InlineData("リリース日は十一月二十日に決まりました。", DecisionState.Confirmed)]
    [InlineData("会場は市民ホールにすることになりました。", DecisionState.Confirmed)]
    [InlineData("予算は百八十万円に増やすことで承認されました。", DecisionState.Confirmed)]
    [InlineData("参加費は五百円で合意しました。", DecisionState.Confirmed)]
    [InlineData("今日の定例会議でリリース日を決めました。", DecisionState.Confirmed)]
    [InlineData("花火は上げないことに決まりました。", DecisionState.ConfirmedNegative)]
    [InlineData("今年の開催は見送ることになりました。", DecisionState.ConfirmedNegative)]
    [InlineData("次回の会議で決めましょう。", DecisionState.Future)]
    [InlineData("来週判断します。", DecisionState.Future)]
    [InlineData("ブログを出してはどうかという提案がありました。", DecisionState.Proposal)]
    [InlineData("仮決定ですが、日程は三日です。", DecisionState.Unclear)]
    [InlineData("決定事項を読み上げます。", DecisionState.None)]
    [InlineData("了解です。", DecisionState.None)]
    public void StateOfAStatement(string said, DecisionState expected) => Assert.Equal(expected, DecisionStatus.Of(said));

    [Theory]
    [InlineData("会場は市民ホール", DecisionState.Confirmed)]
    [InlineData("日程はまだ決まっていない", DecisionState.Open)]
    public void TwoSubjectsInOneSentence_EachGetsItsOwnState(string fact, DecisionState expected) =>
        Assert.Equal(expected, DecisionStatus.ForFact(fact, "会場は市民ホールに決まりましたが、日程はまだ決まっていません。"));

    // ----- Through the whole pipeline (scripted model) ---------------------------------------

    private sealed class Scripted(params string[] answers) : ILocalTextGenerator
    {
        private int _next;

        public string ModelName => "テスト用モデル";

        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null) =>
            Task.FromResult(_next < answers.Length ? answers[_next++] : string.Empty);
    }

    private static async Task<string> Minutes(string transcript, string answer)
    {
        string note = "- メモ\n\n" + TranscriptInsertion.Header + "\n" + transcript;
        var draft = await new MinutesGenerator(new Scripted(answer), MinutesStyle.Facts).GenerateAsync(note, "定例", new DateTime(2026, 10, 18, 9, 0, 0));
        int appendix = MinutesValidator.AppendixStart(draft.Markdown);
        return appendix >= 0 ? draft.Markdown[..appendix] : draft.Markdown;
    }

    [Fact]
    public async Task ReportedCase_GarbledUndecided_IsNeverADecision()
    {
        string minutes = await Minutes("公開日とVisperのモデル変更については見決定です。",
            """{"type":"決定","topic":"公開日","text":"公開日とモデル変更については見定まりました","src":["B001"]}""");

        Assert.DoesNotContain("【決定】", minutes);
        Assert.Contains("【要確認】", minutes);
        Assert.Contains("判断できない", minutes);
    }

    [Fact]
    public async Task Undecided_SaidCorrectly_IsOpen_EvenWhenTheModelSaysDecided()
    {
        string minutes = await Minutes("新バージョンの公開日とWhisperのモデル変更は未決定です。",
            """{"type":"決定","topic":"公開日","text":"新バージョンの公開日とWhisperのモデル変更は未決定","src":["B001"]}""");

        Assert.DoesNotContain("【決定】", minutes);
        Assert.Contains("【未決】新バージョンの公開日", minutes);
    }

    [Fact]
    public async Task ADecisionNotToDoSomething_MustKeepTheNegation()
    {
        string wrong = await Minutes("花火は上げないことに決まりました。",
            """{"type":"決定","topic":"花火","text":"花火を上げる","src":["B001"]}""");
        string right = await Minutes("花火は上げないことに決まりました。",
            """{"type":"決定","topic":"花火","text":"花火は上げない","src":["B001"]}""");

        Assert.DoesNotContain("【決定】", wrong);
        Assert.Contains("【決定】花火は上げない", right);
    }

    [Fact]
    public async Task ALaterStatement_ThatSaysUndecided_StopsTheDecision()
    {
        string minutes = await Minutes("会場は市民ホールに決まりました。参加費の話をしました。やはり会場はまだ決まっていません。",
            """{"type":"決定","topic":"会場","text":"会場は市民ホールに決まった","src":["B001"]}""");

        Assert.DoesNotContain("【決定】", minutes);
        Assert.Contains("後の発言 B003", minutes);
    }

    [Fact]
    public async Task AnEarlierUndecided_ThenDecided_StaysADecision()
    {
        string minutes = await Minutes("会場はまだ決まっていません。話し合いの結果、会場は市民ホールに決まりました。",
            """{"type":"決定","topic":"会場","text":"会場は市民ホールに決まった","src":["B002"]}""");

        Assert.Contains("【決定】会場は市民ホールに決まった", minutes);
    }

    [Theory]
    [InlineData("リリース日は十一月二十日に決まりました。", "リリース日は十一月二十日")]
    [InlineData("参加費は五百円で合意しました。", "参加費は五百円")]
    [InlineData("予算は百八十万円に増やすことで承認されました。", "予算は百八十万円に増やす")]
    public async Task ClearDecisions_AreNotHeldBack(string said, string text)
    {
        string minutes = await Minutes(said, $$"""{"type":"決定","topic":"t","text":"{{text}}","src":["B001"]}""");

        Assert.Contains("【決定】" + text, minutes);
    }

    [Fact]
    public async Task TwoSubjects_DecidedAndUndecided_AreNotMergedIntoOneDecision()
    {
        string minutes = await Minutes("会場は市民ホールに決まりましたが、日程はまだ決まっていません。",
            """
            {"type":"決定","topic":"会場","text":"会場は市民ホール","src":["B001"]}
            {"type":"決定","topic":"日程","text":"日程はまだ決まっていない","src":["B001"]}
            """);

        Assert.Contains("【決定】会場は市民ホール", minutes);
        Assert.Contains("【未決】日程はまだ決まっていない", minutes);
    }

    [Fact]
    public void OneStatement_ReadAsDecidedAndAsUndecided_BecomesUnclear()
    {
        var merged = FactMinutes.Merge(
        [
            new Fact(FactKind.Decision, "会場", "会場は市民ホール", ["B001"], "", "") { Id = "F001" },
            new Fact(FactKind.Open, "会場", "会場は市民ホール", ["B001"], "", "") { Id = "F002" },
        ]);

        var fact = merged.Single().Facts.Single();
        Assert.Equal(FactKind.Unclear, fact.Kind);
        Assert.Contains("決定か未決か判定が分かれた", fact.Issues);
    }

    [Fact]
    public void ACompletedTask_IsCheckedAgain()
    {
        var units = SourceUnits.From(NoteSections.Split("- メモ\n\n" + TranscriptInsertion.Header + "\n二社から見積もりを取ります。山田さんが来週までに取ります。"));
        var byId = units.ToDictionary(u => u.Id);
        var task = FactMinutes.Check(new Fact(FactKind.Task, "見積", "山田さんが来週までに取ります", ["B002"], "山田", "来週"), byId, new HashSet<string> { "B002" });
        var clarified = FactMinutes.Clarify(task, units);
        var again = FactMinutes.Check(clarified, byId, units.Select(u => u.Id).ToHashSet());

        Assert.Equal(["B001", "B002"], again.Sources); // the completed object is cited
        Assert.Equal(FactKind.Task, again.Kind);
        Assert.Equal(Support.Supported, again.Support);
    }
}
