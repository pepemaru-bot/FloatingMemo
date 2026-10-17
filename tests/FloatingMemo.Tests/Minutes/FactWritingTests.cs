using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>The writing step: prose only from checked facts, checked again (made-up facts).</summary>
public sealed class FactWritingTests
{
    private static readonly List<(string Topic, List<Fact> Facts)> Topics =
    [
        ("リリース日", [
            new Fact(FactKind.Decision, "リリース日", "リリース日は11月20日", ["B010"], "", "") { Id = "F001" },
            new Fact(FactKind.Info, "リリース日", "テストの遅れで当初の11月13日から1週間ずらした", ["B008", "B009"], "", "") { Id = "F002" },
        ]),
        ("予算", [
            new Fact(FactKind.Open, "予算", "予算は見積もりがそろってから決める", ["B020"], "", "") { Id = "F003" },
        ]),
    ];

    [Fact]
    public void Prompt_ListsTheCheckedFacts_WithIdsAndKinds()
    {
        string user = FactWriting.User(Topics);
        Assert.Contains("F001 [決定] リリース日は11月20日", user);
        Assert.Contains("## 予算", user);
    }

    [Fact]
    public void Sentences_AreKeptOnlyWhenTheFactsCarryThem()
    {
        const string answer = """
            ## リリース日
            テストの遅れで当初の11月13日から1週間ずらし、リリース日は11月20日に決まった。[F002][F001]
            営業部長も了承した。[F001]
            お客さんへの案内は12月に出す。
            ## 予算
            予算は見積もりがそろってから決めることになった。[F003]
            予算は200万円に決まった。[F003]
            ## 雑談
            天気の話をした。[F009]
            """;
        var result = FactWriting.Check(answer, Topics);

        Assert.Contains("リリース日は11月20日に決まった。〔B008・B009・B010〕", result.Paragraphs["リリース日"]); // fact ids -> source numbers
        Assert.DoesNotContain("営業部長", result.Paragraphs["リリース日"]); // a person no fact names
        Assert.DoesNotContain("12月", result.Paragraphs["リリース日"]);    // no fact id at all
        Assert.DoesNotContain("200万円", result.Paragraphs["予算"]);       // a number and a decision no fact carries
        Assert.Contains("見積もりがそろってから決める", result.Paragraphs["予算"]);
        Assert.Equal(2, result.Kept);
        Assert.Equal(4, result.Removed);
        Assert.Equal(["F001", "F002", "F003"], result.CoveredIds.Order());
    }

    [Fact]
    public void Render_ListsDecisionsTasksAndOpenItems_EvenWhenTheParagraphCoversThem()
    {
        var units = new Dictionary<string, SourceUnit>
        {
            ["B008"] = new("B008", "当初は十一月十三日でした。", false),
            ["B009"] = new("B009", "テストが遅れているので一週間ずらします。", false),
            ["B010"] = new("B010", "リリース日は十一月二十日に決まりました。", false),
            ["B020"] = new("B020", "予算は見積もりがそろってから決めましょう。", false),
        };
        var paragraphs = new Dictionary<string, string> { ["リリース日"] = "段落。〔B010〕" };
        string md = FactMinutes.Render(Topics, units, [], [], paragraphs, new HashSet<string> { "F001", "F002" });
        Assert.Contains("段落。〔B010〕", md);
        Assert.Contains("【決定】リリース日は11月20日", md);              // decisions are always listed
        Assert.DoesNotContain("当初の11月13日から1週間ずらした 〔", md);  // info said in the paragraph is not repeated
        Assert.Contains("【未決】予算は見積もりがそろってから決める", md); // no paragraph for this topic: listed
    }
}
