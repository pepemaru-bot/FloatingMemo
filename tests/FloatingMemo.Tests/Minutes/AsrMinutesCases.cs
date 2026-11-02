using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// The expected minutes of the spoken made-up meetings of tests/asr/eval (h01, h02): the same
/// meetings are read by speech recognition and then summarised, so the minutes of each recognizer
/// can be scored against what was actually said. Transcript = the reference transcript (what the
/// voices say, written form). Held out: not used to tune the minutes pipeline or its prompts.
/// No real people, schools, organisations or meetings.
/// </summary>
public static class AsrMinutesCases
{
    public static readonly BenchCase Shinro = new(
        "h01-shinro", "進路会議（架空の学習塾）",
        Reference("h01"),
        ["佐倉", "早瀬", "三浦", "冬期講習"],
        [
            new("佐倉", FactKind.Info, ["数学", "89点"]),
            new("佐倉", FactKind.Info, ["偏差値", "62"]),
            new("佐倉", FactKind.Info, ["青嶺学院"]),
            new("佐倉", FactKind.Info, ["一般入試"]),
            new("佐倉", FactKind.Open, ["個別指導", "了解"]),
            new("佐倉", FactKind.Decision, ["面談", "12月14日"]),
            new("佐倉", FactKind.Task, ["面談シート"], Who: "高田", Due: "12月10日"),
            new("佐倉", FactKind.Task, ["過去問"], Who: "高田", Due: "来週の水曜"),
            new("佐倉", FactKind.Open, ["英語", "次回"], Important: false),
            new("早瀬", FactKind.Info, ["物理基礎", "54点"]),
            new("早瀬", FactKind.Info, ["朝凪高専"]),
            new("早瀬", FactKind.Proposal, ["補習", "土曜"], Important: false),
            new("早瀬", FactKind.Open, ["補習", "空き"]),
            new("早瀬", FactKind.Task, ["課題プリント"], Who: "西川", Due: "毎週月曜"),
            new("三浦", FactKind.Info, ["92点"]),
            new("三浦", FactKind.Info, ["欠席", "3回"]),
            new("三浦", FactKind.Open, ["来月"]),
            new("冬期講習", FactKind.Info, ["28人"]),
            new("冬期講習", FactKind.Decision, ["定員", "35人"]),
            new("冬期講習", FactKind.Decision, ["3万2千円", "据え置"]),
            new("冬期講習", FactKind.Task, ["講師", "募集"], Who: "森本", Due: "今週中"),
        ],
        ["週2回", "12月7日"], []);

    public static readonly BenchCase Unei = new(
        "h02-unei", "教室運営会議（架空の学習塾）",
        Reference("h02"),
        ["駅前教室", "体験授業", "保護者会", "全国模試", "チラシ", "シフト", "説明会"],
        [
            new("駅前教室", FactKind.Decision, ["開校", "2月8日"]),
            new("駅前教室", FactKind.Info, ["自習席", "24席"]),
            new("駅前教室", FactKind.Decision, ["教室長", "松井"]),
            new("駅前教室", FactKind.Info, ["内装工事", "1月20日"], Important: false),
            new("駅前教室", FactKind.Task, ["自習席"], Who: "松井", Due: "来週"),
            new("体験授業", FactKind.Info, ["41件"]),
            new("体験授業", FactKind.Info, ["15人"]),
            new("体験授業", FactKind.Decision, ["1月11日", "18日"]),
            new("体験授業", FactKind.Task, ["案内メール"], Who: "岡田", Due: "12月20日"),
            new("保護者会", FactKind.Info, ["保護者会", "1月25日"]),
            new("保護者会", FactKind.Info, ["52家庭"]),
            new("保護者会", FactKind.Decision, ["資料", "入試"]),
            new("保護者会", FactKind.Task, ["資料"], Who: "山根", Due: "1月15日"),
            new("全国模試", FactKind.Info, ["33人"]),
            new("全国模試", FactKind.Info, ["56.4"]),
            new("全国模試", FactKind.Info, ["1.8"], Important: false),
            new("全国模試", FactKind.Open, ["古文", "講師"]),
            new("チラシ", FactKind.Decision, ["4500部"]),
            new("チラシ", FactKind.Info, ["6万7千円"], Important: false),
            new("チラシ", FactKind.Decision, ["配布", "2月", "第2週"]),
            new("チラシ", FactKind.Task, ["チラシ", "修正"], Who: "岡田", Due: "1月10日"),
            new("シフト", FactKind.Info, ["2コマ"]),
            new("シフト", FactKind.Open, ["時給", "本部"]),
            new("シフト", FactKind.Task, ["本部"], Who: "久保", Due: "来週の金曜"),
            new("説明会", FactKind.Decision, ["説明会", "2月8日"]),
            new("説明会", FactKind.Info, ["20組"]),
            new("説明会", FactKind.Task, ["申し込みフォーム"], Who: "松井", Due: "1月末"),
        ],
        ["2月1日", "週1回", "第1週"], []);

    // Written before any recognizer's output of this meeting was looked at (held out, 2026-11-01).
    public static readonly BenchCase Kikaku = new(
        "h03-kikaku", "ツアー企画会議（架空の旅行会社）",
        Reference("h03"),
        ["汐見台", "琴乃浜", "葛城商事"],
        [
            new("汐見台", FactKind.Info, ["42名"]),
            new("汐見台", FactKind.Decision, ["定員", "45名"]),
            new("汐見台", FactKind.Info, ["残り", "3名"], Important: false),
            new("汐見台", FactKind.Info, ["早割"], Important: false),
            new("汐見台", FactKind.Decision, ["キャンセル待ち", "10名"]),
            new("汐見台", FactKind.Task, ["案内文"], Who: "大塚", Due: "今週中"),
            new("琴乃浜", FactKind.Info, ["灯籠流し"], Important: false),
            new("琴乃浜", FactKind.Info, ["3万8千円"]),
            new("琴乃浜", FactKind.Info, ["2千円"], Important: false),
            new("琴乃浜", FactKind.Info, ["宿", "来週の水曜"], Important: false),
            new("琴乃浜", FactKind.Open, ["料金", "宿"]),
            new("琴乃浜", FactKind.Open, ["バス", "人数"]),
            new("琴乃浜", FactKind.Decision, ["添乗員", "坂口"]),
            new("葛城商事", FactKind.Info, ["60名"]),
            new("葛城商事", FactKind.Info, ["2万5千円"]),
            new("葛城商事", FactKind.Info, ["温泉"], Important: false),
            new("葛城商事", FactKind.Decision, ["温泉地", "2か所"]),
            new("葛城商事", FactKind.Task, ["提案書"], Who: "真柴", Due: "来週の火曜"),
            new("葛城商事", FactKind.Decision, ["説明会", "6月10日"]),
            new("葛城商事", FactKind.Decision, ["会議室"], Important: false),
        ],
        ["6月3日", "2台", "50名"], []);

    public static readonly BenchCase[] All = [Shinro, Unei, Kikaku];

    // The reference transcript next to the scenario (tests/asr/eval/<case>-*.json), written form,
    // one statement per line; {表記|よみ} keeps the written form.
    private static string Reference(string prefix)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "asr-eval");
        string file = Directory.GetFiles(dir, prefix + "-*.json").Single();
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        return string.Join("\n", json["utterances"]!.AsArray().Select(u =>
            System.Text.RegularExpressions.Regex.Replace(u!["text"]!.GetValue<string>(), @"\{([^|{}]+)\|[^{}]+\}", "$1")));
    }
}

/// <summary>Guards the scoring of the spoken meetings: every expected word occurs in what was said.</summary>
public sealed class AsrMinutesCasesTests
{
    public static TheoryData<string> Cases => new(AsrMinutesCases.All.Select(c => c.Name));

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryKeyword_IsInTheReferenceTranscript(string name)
    {
        var c = AsrMinutesCases.All.Single(x => x.Name == name);
        var missing = c.Facts.SelectMany(f => f.Keywords.Concat(f.Kind == FactKind.Task ? new[] { f.Who, f.Due } : []))
            .Where(w => w.Length > 0 && !QualityBench.SourceHas(c, w)).ToList();
        Assert.Empty(missing);
        Assert.All(c.Subjects, s => Assert.True(QualityBench.SourceHas(c, s), s));
    }

    [Theory]
    [InlineData("三万二千円", "3万2千円")]
    [InlineData("32,000円", "3万2千円")]
    [InlineData("第二週", "第2週")]
    [InlineData("四千五百部", "4500部")]
    [InlineData("4,500部", "4500部")]
    [InlineData("六万七千円", "6万7千円")]
    [InlineData("十二月十四日", "12月14日")]
    [InlineData("平均偏差値は五十六点四", "56.4")]
    [InlineData("前回より一点八上がった", "1.8")]
    public void Keywords_MatchTheUsualWritings(string written, string keyword) =>
        Assert.True(QualityBench.SameWords(written, keyword), $"{written} / {keyword}");
}
