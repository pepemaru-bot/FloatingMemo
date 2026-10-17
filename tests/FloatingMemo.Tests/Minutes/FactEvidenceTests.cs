using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Support of a fact by its cited source (made-up sentences).</summary>
public sealed class FactEvidenceTests
{
    [Theory]
    // Supported: the cited sentence carries the fact, in other words or other numerals.
    [InlineData("リリース日は11月20日", "リリース日は十一月二十日に決まりました。", Support.Supported)]
    [InlineData("テスト計画は森さんが作る", "テスト計画は森さんが来週の水曜日までに作ることになりました。", Support.Supported)]
    // Similar, but about something else.
    [InlineData("リリース日は11月20日", "研修は十二月に一回行うことに決まりました。", Support.Unsupported)]
    // Another number.
    [InlineData("リリース日は11月21日", "リリース日は十一月二十日に決まりました。", Support.Partial)]
    public void Levels(string fact, string cited, Support expected) =>
        Assert.Equal(expected, FactEvidence.Check(fact, cited).Level);

    [Fact]
    public void WordsOfTheTopicName_AndCommonWordsSaidElsewhere_AreContext_PeopleAreNot()
    {
        const string cited = "では、ログイン画面は青の案でいくことに決まりました。";
        Assert.NotEqual(Support.Supported, FactEvidence.Check("ログイン画面のデザインは青の案", cited).Level);
        Assert.Equal(Support.Supported, FactEvidence.Check("ログイン画面のデザインは青の案", cited, topic: "ログイン画面のデザイン").Level);

        var said = FactEvidence.Check("ログイン画面は青の案でデザインする", cited, wholeSource: "デザインの担当が進めます。" + cited);
        Assert.DoesNotContain(said.Problems, p => p.Contains("デザイン"));

        var person = FactEvidence.Check("ログイン画面は小川さんの青の案", cited, wholeSource: "小川さんが出した案です。" + cited);
        Assert.Contains(person.Problems, p => p.Contains("小川")); // who said or did it must be in the cited sentence
    }

    [Fact]
    public void Negation_MustAgree()
    {
        var positive = FactEvidence.Check("サーバーの移行が決まった", "サーバーの移行はまだ決まっていません。", FactKind.Decision);
        Assert.Contains(positive.Problems, p => p.Contains("否定"));

        var open = FactEvidence.Check("サーバーの移行は未定", "サーバーの移行はまだ決まっていません。", FactKind.Open);
        Assert.DoesNotContain(open.Problems, p => p.Contains("否定"));
    }

    [Fact]
    public void ACondition_InTheSource_MustNotBeDropped()
    {
        var dropped = FactEvidence.Check("イベントは公民館で行う", "雨の場合はイベントを公民館で行います。");
        Assert.Contains(dropped.Problems, p => p.Contains("条件"));

        var kept = FactEvidence.Check("雨の場合、イベントは公民館で行う", "雨の場合はイベントを公民館で行います。");
        Assert.DoesNotContain(kept.Problems, p => p.Contains("条件"));
    }

    [Fact]
    public void ANameNotInTheSource_IsAProblem()
    {
        var check = FactEvidence.Check("アンケートはフォームズで集計する", "アンケートは来週集計します。");
        Assert.Contains(check.Problems, p => p.Contains("フォームズ"));
        Assert.NotEqual(Support.Supported, check.Level);
    }

    [Fact]
    public void AFactNeedingTwoSentences_IsSupportedByBothTogether()
    {
        const string first = "広報のブログを出すことに決まりました。";
        const string second = "担当は小川さんです。";
        const string fact = "広報のブログは小川さんが担当する";
        Assert.NotEqual(Support.Supported, FactEvidence.Check(fact, first).Level);
        Assert.Equal(Support.Supported, FactEvidence.Check(fact, first + second).Level);
    }

    [Fact]
    public void Candidate_OnlyWhenExactlyOneSentenceFullySupports()
    {
        SourceUnit[] part =
        [
            new("B001", "回答は百二十件ありました。", false),
            new("B002", "えーと、はい。", false),
            new("B003", "回答は百二十件で、満足が七割でした。", false),
        ];
        Assert.Null(FactEvidence.Candidate("回答は120件", FactKind.Info, part)); // two candidates: no suggestion
        Assert.Equal("B001", FactEvidence.Candidate("回答は百二十件ありました", FactKind.Info, part[..2]));
    }
}
