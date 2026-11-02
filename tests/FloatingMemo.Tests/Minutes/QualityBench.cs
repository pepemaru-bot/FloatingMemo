using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>One fact a correct draft must contain: about which subject, of which kind, in which words.</summary>
public sealed record BenchFact(string Subject, string Kind, string[] Keywords, bool Important = true, string Who = "", string Due = "");

/// <summary>
/// One made-up meeting with its expected facts (no real people, organisations or meetings).
/// Subjects are the things the meeting discusses one after another (shops, projects ...).
/// </summary>
public sealed record BenchCase(string Name, string Title, string Transcript, string[] Subjects, BenchFact[] Facts,
    string[] NotDecided, string[] BadHeadingWords)
{
    public string Note => "- 架空の会議の記録（評価用）\n\n" + TranscriptInsertion.Header + "\n" + Transcript;
}

/// <summary>Scores of one draft (see <see cref="QualityBench.Score"/>).</summary>
public sealed record BenchScore(string Case, int Facts, int Full, int Partial, int ImportantTotal, int ImportantFull,
    int WrongSubject, int Decisions, int DecisionsTotal, int Tasks, int TasksTotal, int UndecidedAsDecision,
    int InventedNumbers, int Duplicates, int Headings, int BadHeadings, int SplitSubjects, int Links, int WrongLinks,
    int NeedsReview, int Items, double Seconds, long PeakMb, int ImportantEvidenced = 0)
{
    public static string Header =>
        "| 会議 | 完全網羅 | 部分網羅 | 重要事項 | 根拠つき重要事項 | 対象の取り違え | 決定 | Next Action | 未決→決定 | 原文にない数値 | 重複 | 見出し | 不適切な見出し | 対象の見出し分裂 | 根拠リンク誤り | 要確認 | 項目 | 時間 | メモリ |\n"
        + "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|";

    public string Row => $"| {Case} | {Full}/{Facts} | {Partial} | {ImportantFull}/{ImportantTotal} | {ImportantEvidenced}/{ImportantTotal} | {WrongSubject} | {Decisions}/{DecisionsTotal} | {Tasks}/{TasksTotal} | {UndecidedAsDecision} | "
        + $"{InventedNumbers} | {Duplicates} | {Headings} | {BadHeadings} | {SplitSubjects} | {WrongLinks}/{Links} | {NeedsReview} | {Items} | {Seconds:F0}s | {PeakMb}MB |";
}

public static partial class QualityBench
{
    // ----- Made-up meetings ------------------------------------------------------------------------

    // Talk without facts, to make the meetings as long as real ones (several parts for the model).
    private static readonly string[] Chatter =
    [
        "現場の感覚としても、だいたいそういう印象を持っています。",
        "細かい数字は後で資料に載せておきますので、そちらも見てください。",
        "このあたりは前回も少し話に出ていたと思います。",
        "お客さんの声を聞いても、似たような反応が多いですね。",
        "スタッフからも同じような話を聞いています。",
        "ほかの店舗の様子も見ながら考えたいところです。",
        "時間帯によってかなり差があるのも気になっています。",
        "ここはもう少し様子を見てもいいかもしれません。",
        "去年の同じ時期と比べると、雰囲気はだいぶ違いますね。",
        "ちょっと補足すると、週末の動きはまた別です。",
    ];

    private static string Pad(int count, int seed) =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => Chatter[(seed + i * 3) % Chatter.Length]));

    /// <summary>Four shops reviewed one after another; later shops are mostly referred to as "ここ".</summary>
    public static readonly BenchCase Sequential = new(
        "対象を順に検討", "店舗別の月次レビュー（架空）",
        string.Join("\n", [
            "それでは月次の店舗レビューを始めます。今日は四店舗を順番に見ていきます。",
            "まず北町店です。", "北町店の売上は前年比で八パーセント減りました。", Pad(6, 0),
            "一番の課題はレジ待ちの列が長いことです。", Pad(5, 1),
            "議論の結果、北町店にはセルフレジを二台追加することに決まりました。",
            "山田さんが十一月七日までに見積もりを取ってください。", "はい、山田が担当します。",
            "次に南町店です。", "ここは来客数が十二パーセント増えています。", Pad(6, 2),
            "ただ駐車場が足りないという声が多いです。", Pad(5, 3),
            "隣の空き地を借りることで合意しました。",
            "契約書の確認は佐藤さんが十一月十四日までにお願いします。",
            "三つ目は東町店です。", "ここは在庫の廃棄が月に三十万円出ています。", Pad(6, 4),
            "原因は発注を担当者一人で回していることです。", Pad(5, 5),
            "発注は二人体制にすることに決まりました。",
            "鈴木さんが十一月二十一日までに研修の計画を作ってください。",
            "最後に西町店です。", "ここは十二月に改装を予定しています。", Pad(6, 6),
            "改装の業者は中央工務店に決まりました。",
            "休業の期間はまだ決まっていません。来週もう一度相談します。", Pad(4, 7),
            "お知らせの文案は高橋さんが十一月二十八日までに用意してください。",
            "以上で今日のレビューを終わります。",
        ]),
        ["北町店", "南町店", "東町店", "西町店"],
        [
            new("北町店", FactKind.Info, ["北町店", "8", "減"]),
            new("北町店", FactKind.Info, ["レジ", "待ち"]),
            new("北町店", FactKind.Decision, ["セルフレジ", "2台"]),
            new("北町店", FactKind.Task, ["見積"], Who: "山田", Due: "11月7日"),
            new("南町店", FactKind.Info, ["来客", "12"]),
            new("南町店", FactKind.Info, ["駐車場"]),
            new("南町店", FactKind.Decision, ["空き地", "借り"]),
            new("南町店", FactKind.Task, ["契約書"], Who: "佐藤", Due: "11月14日"),
            new("東町店", FactKind.Info, ["廃棄", "30万円"]),
            new("東町店", FactKind.Info, ["発注", "一人"]),
            new("東町店", FactKind.Decision, ["発注", "二人体制"]),
            new("東町店", FactKind.Task, ["研修"], Who: "鈴木", Due: "11月21日"),
            new("西町店", FactKind.Info, ["改装", "12月"]),
            new("西町店", FactKind.Decision, ["中央工務店"]),
            new("西町店", FactKind.Open, ["休業", "期間"]),
            new("西町店", FactKind.Task, ["文案"], Who: "高橋", Due: "11月28日"),
        ],
        ["休業"], []);

    /// <summary>One subject early and again late, another in between.</summary>
    public static readonly BenchCase Revisited = new(
        "前半と後半に再登場", "業務システム移行の打ち合わせ（架空）",
        string.Join("\n", [
            "まず新しい勤怠システムについてです。", "先週からログインできないという問い合わせが毎日五件ほど来ています。",
            "原因はパスワードの初期設定のようです。", Pad(8, 1),
            "次に社内研修の件です。", "研修は二回に分けて開催します。", "一回目は十二月三日、会場は第二会議室です。", Pad(8, 2),
            "研修の講師は外部の方にお願いすることになりました。", Pad(6, 3),
            "さて、勤怠システムの話に戻ります。", "ログインの問題は、パスワードの再設定手順を全員に配ることで対応すると決まりました。",
            "手順書は中村さんが十一月十日までに作ってください。", "問い合わせ窓口は総務に一本化します。",
        ]),
        ["勤怠システム", "研修"],
        [
            new("勤怠システム", FactKind.Info, ["ログイン", "5件"]),
            new("勤怠システム", FactKind.Info, ["パスワード", "初期設定"]),
            new("勤怠システム", FactKind.Decision, ["再設定", "手順"]),
            new("勤怠システム", FactKind.Task, ["手順書"], Who: "中村", Due: "11月10日"),
            new("勤怠システム", FactKind.Decision, ["窓口", "総務"], Important: false),
            new("研修", FactKind.Info, ["2回"]),
            new("研修", FactKind.Info, ["12月3日", "第二会議室"]),
            new("研修", FactKind.Decision, ["講師", "外部"]),
        ],
        [], []);

    /// <summary>Three proposals, discussion, then one final decision.</summary>
    public static readonly BenchCase Proposals = new(
        "提案と最終決定", "展示会の出展計画（架空）",
        string.Join("\n", [
            "展示会の出展時期について三つ案が出ています。", "案Aは十月の東京会場、案Bは十一月の大阪会場、案Cは十二月の名古屋会場です。",
            "案Aは準備期間が足りないと思います。", "案Cは年末で人手が厳しいという意見がありました。", Pad(8, 4),
            "ブースの大きさは九平方メートルか十八平方メートルかで迷っています。", "費用を考えると九平方メートルがいいという提案がありました。", Pad(6, 5),
            "最終的に、案Bの十一月の大阪会場で出展することに決まりました。",
            "ブースの大きさは来週までに決めます。",
            "申し込みは木村さんが十月十五日までに行ってください。",
        ]),
        ["展示会"],
        [
            new("展示会", FactKind.Decision, ["11月", "大阪"]),
            new("展示会", FactKind.Proposal, ["9平方メートル"], Important: false),
            new("展示会", FactKind.Open, ["ブース", "大きさ"]),
            new("展示会", FactKind.Task, ["申し込み"], Who: "木村", Due: "10月15日"),
        ],
        ["東京", "名古屋", "18平方メートル"], []);

    /// <summary>Many numbers, dates and names, one figure corrected.</summary>
    public static readonly BenchCase Numbers = new(
        "数字・日付・固有名詞", "地域交流イベントの準備会議（架空）",
        string.Join("\n", [
            "イベントの準備状況を確認します。", "会場はみなと会館で、日程は三月十四日の土曜日です。",
            "予算は全体で二百五十万円です。", "参加者は百人と言いましたが、百二十人の誤りでした。参加者は百二十人です。", Pad(6, 6),
            "協賛は三社から内諾をもらっています。", "協賛社への請求書は二月二十日が締め切りです。", Pad(6, 7),
            "当日のスタッフは十五人を予定しています。", "ポスターは千部印刷します。",
            "受付の名簿は小林さんが三月一日までにまとめてください。",
            "印刷会社への入稿は青木さんが二月十日までにお願いします。",
        ]),
        ["イベント"],
        [
            new("イベント", FactKind.Info, ["みなと会館"]),
            new("イベント", FactKind.Info, ["3月14日"]),
            new("イベント", FactKind.Info, ["250万円"]),
            new("イベント", FactKind.Info, ["120人"]),
            new("イベント", FactKind.Info, ["3社"]),
            new("イベント", FactKind.Info, ["2月20日"], Important: false),
            new("イベント", FactKind.Info, ["15人"], Important: false),
            new("イベント", FactKind.Info, ["1000部"], Important: false),
            new("イベント", FactKind.Task, ["名簿"], Who: "小林", Due: "3月1日"),
            new("イベント", FactKind.Task, ["入稿"], Who: "青木", Due: "2月10日"),
        ],
        [], []);

    /// <summary>Negation, corrections and things put on hold.</summary>
    public static readonly BenchCase Corrections = new(
        "否定・訂正・保留", "サポート体制の見直し会議（架空）",
        string.Join("\n", [
            "サポート業務の外部委託について検討しました。", "結論として、外部委託はしないことにしました。", Pad(6, 8),
            "前回、定例会を金曜に開くと決めましたが、それは取り消して、毎週水曜に開くことにしました。", Pad(5, 9),
            "問い合わせ対応の担当は井上さんと言いましたが、井上さんではなく森さんです。",
            "森さんは十一月五日までに対応手順をまとめてください。",
            "料金の改定は、次回まで保留にします。", "チャットでの受付は今回は見送ります。",
        ]),
        ["サポート"],
        [
            new("サポート", FactKind.Decision, ["外部委託", "しない"]),
            new("サポート", FactKind.Decision, ["水曜"]),
            new("サポート", FactKind.Task, ["手順"], Who: "森", Due: "11月5日"),
            new("サポート", FactKind.Open, ["料金", "保留"]),
            new("サポート", FactKind.Decision, ["チャット", "見送"], Important: false),
        ],
        ["金曜", "料金"], []);

    /// <summary>Fillers, repeats and misrecognised words in the transcript.</summary>
    public static readonly BenchCase Noisy = new(
        "雑音・誤認識", "倉庫の安全点検の打ち合わせ（架空）",
        string.Join("\n", [
            "えー、はい、聞こえますか。", "はい、聞こえてます。", "えーと、では、倉庫の安全点検の件です。",
            "あの、棚の固定が、えー、三か所外れていました。", "あ、すいません、三か所です、三か所。", Pad(5, 1),
            "うん、うん。", "それで、改選工事、じゃなくて配線工事を十二月にやることになりました。",
            "えー、あのー、消火器の期限切れが二本ありました。", "はい。", Pad(5, 2),
            "消火器の交換は藤田さんが十一月十八日までにお願いします。", "あ、はい、藤田やります。",
            "棚の固定は今週中に直します。", "えー、以上です。",
        ]),
        ["倉庫"],
        [
            new("倉庫", FactKind.Info, ["棚", "3か所"]),
            new("倉庫", FactKind.Decision, ["配線工事", "12月"]),
            new("倉庫", FactKind.Info, ["消火器", "2本"]),
            new("倉庫", FactKind.Task, ["消火器", "交換"], Who: "藤田", Due: "11月18日"),
            new("倉庫", FactKind.Decision, ["棚", "今週"], Important: false),
        ],
        [], ["えー", "あの", "うん", "改選", "聞こえ"]);

    public static readonly BenchCase[] All = [Sequential, Revisited, Proposals, Numbers, Corrections, Noisy];

    // ----- Scoring --------------------------------------------------------------------------------

    [GeneratedRegex(@"〔[^〕]*〕|\[\^\d+\]|（原文[:：][^）]*）|（メモより）|（メモ・発言）|⚠要確認（[^）]*）")]
    private static partial Regex Decorations();

    [GeneratedRegex(@"〔(?<ids>[AB]\d{3,}(・[AB]\d{3,})*)〕")]
    private static partial Regex Citations();

    [GeneratedRegex(@"^- (?<id>[AB]\d{3,}) (?<text>.*)$", RegexOptions.Multiline)]
    private static partial Regex Numbered();

    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Digits();

    // Numbers in one form: 三万二千円, 3万2千円, 3万2000円 and 32,000円 all become 32000円.
    // Digits mixed with 万/千 are read here first: Numerals.Arabic alone turns the 万 of 3万 into 0.
    // A spoken decimal (五十六点四 = 56点4) reads as the written one (56.4); Normalize drops the point.
    private static string N(string s) =>
        SpokenDecimal().Replace(Numerals.Arabic(MixedNumber().Replace(MinutesValidator.Normalize(s), m => MixedValue(m.Value).ToString())), "$1$2");

    [GeneratedRegex(@"(\d+)点(\d)(?!\d)")]
    private static partial Regex SpokenDecimal();

    private static long MixedValue(string text)
    {
        long total = 0, section = 0, number = 0;
        foreach (char c in text)
        {
            int d = c is >= '0' and <= '9' ? c - '0' : "〇一二三四五六七八九".IndexOf(c);
            if (d >= 0)
            {
                number = (number * 10) + d;
            }
            else if (c == '万')
            {
                total += (section + number) * 10000;
                section = number = 0;
            }
            else
            {
                section += (number == 0 ? 1 : number) * (c switch { '十' => 10, '百' => 100, _ => 1000 });
                number = 0;
            }
        }

        return total + section + number;
    }

    // A number written with digits and 十/百/千/万 together (3万2千, 4千500, 30万).
    [GeneratedRegex(@"(?=[〇一二三四五六七八九十百千万]*[0-9])(?=[0-9〇一二三四五六七八九]*[十百千万])[0-9〇一二三四五六七八九十百千万]+")]
    private static partial Regex MixedNumber();

    private static bool HasAll(string text, IEnumerable<string> words) => words.All(w => N(text).Contains(N(w), StringComparison.Ordinal));

    /// <summary>The source of the case contains the word, as the scorer compares them.</summary>
    internal static bool SourceHas(BenchCase c, string word) => N(c.Transcript).Contains(N(word), StringComparison.Ordinal);

    /// <summary>The written text contains the keyword, as the scorer compares them.</summary>
    internal static bool SameWords(string text, string keyword) => HasAll(text, [keyword]);

    /// <summary>Scores a draft (the generator's Markdown, with 〔B012〕 citations and the numbered source).</summary>
    public static BenchScore Score(BenchCase c, string markdown, double seconds = 0, long peakMb = 0)
    {
        int appendix = MinutesValidator.AppendixStart(markdown);
        string body = appendix >= 0 ? markdown[..appendix] : markdown;
        var units = Numbered().Matches(appendix >= 0 ? markdown[appendix..] : string.Empty)
            .ToDictionary(m => m.Groups["id"].Value, m => m.Groups["text"].Value);

        // Items with the heading they are under.
        var items = new List<(string Heading, string Raw, string Text)>();
        var headings = new List<string>();
        string heading = string.Empty;
        foreach (string line in body.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal) || line.StartsWith("### ", StringComparison.Ordinal))
            {
                heading = line.TrimStart('#', ' ');
                headings.Add(heading);
                continue;
            }

            if (MinutesValidator.IsListItem(line) && !MinutesValidator.IsNone(line))
            {
                items.Add((heading, line, Decorations().Replace(line, string.Empty)));
            }
        }

        bool IsDecision((string Heading, string Raw, string Text) i) => i.Text.Contains("【決定】", StringComparison.Ordinal) || i.Heading.StartsWith("決定", StringComparison.Ordinal);
        bool IsTask((string Heading, string Raw, string Text) i) => i.Text.TrimStart().StartsWith("- [", StringComparison.Ordinal);

        int full = 0, partial = 0, importantFull = 0, importantEvidenced = 0, wrongSubject = 0, decisions = 0, tasks = 0;
        // An item whose citations all carry it (and that has at least one).
        bool Evidenced(string raw)
        {
            var found = Citations().Matches(raw);
            return found.Count > 0 && found.All(m => FactEvidence.Check(Decorations().Replace(raw, string.Empty).Split(" — ")[0],
                string.Join("。", m.Groups["ids"].Value.Split('・').Select(id => units.GetValueOrDefault(id, string.Empty))),
                raw.Contains("【決定】", StringComparison.Ordinal) ? FactKind.Decision : FactKind.Info).Level != Support.Unsupported);
        }

        foreach (var f in c.Facts)
        {
            var words = f.Keywords.Concat(f.Kind == FactKind.Task ? new[] { f.Who, f.Due } : []).Where(w => w.Length > 0).ToArray();
            var hit = items.FirstOrDefault(i => HasAll(i.Text, words));
            if (hit.Text is not null)
            {
                full++;
                if (f.Important)
                {
                    importantFull++;
                    if (Evidenced(hit.Raw))
                    {
                        importantEvidenced++;
                    }
                }

                // The item (or its heading) names another subject and not its own: mixed up.
                string where = hit.Heading + " " + hit.Text;
                if (c.Subjects.Length > 1 && !where.Contains(f.Subject, StringComparison.Ordinal)
                    && c.Subjects.Any(s => s != f.Subject && where.Contains(s, StringComparison.Ordinal)))
                {
                    wrongSubject++;
                }

                if (f.Kind == FactKind.Decision && IsDecision(hit))
                {
                    decisions++;
                }

                if (f.Kind == FactKind.Task && IsTask(hit))
                {
                    tasks++;
                }
            }
            else if (items.Any(i => words.Count(w => N(i.Text).Contains(N(w), StringComparison.Ordinal)) * 2 >= words.Length))
            {
                partial++;
            }
        }

        int undecided = items.Count(i => IsDecision(i) && c.NotDecided.Any(w => N(i.Text).Contains(N(w), StringComparison.Ordinal)));
        string source = N(c.Note);
        var sourceNumbers = Digits().Matches(source).Select(m => m.Value).ToHashSet();
        int invented = items.SelectMany(i => Digits().Matches(N(i.Text)).Select(m => m.Value)).Count(n => !sourceNumbers.Contains(n));
        int duplicates = MinutesQuality.Analyze(markdown, c.Note).Issues.Count(i => i.Kind == QualityReport.Duplicate);
        int badHeadings = headings.Count(h => c.BadHeadingWords.Any(w => h.Contains(w, StringComparison.Ordinal)));
        int split = c.Subjects.Length > 1 ? c.Subjects.Sum(s => Math.Max(0, headings.Count(h => h.Contains(s, StringComparison.Ordinal)) - 1)) : 0;

        // Citations: the cited statements must carry the item (same check as for facts).
        int links = 0, wrongLinks = 0;
        foreach (var item in items)
        {
            foreach (Match m in Citations().Matches(item.Raw))
            {
                links++;
                var cited = m.Groups["ids"].Value.Split('・').Select(id => units.GetValueOrDefault(id, string.Empty));
                string kind = item.Text.Contains("【決定】", StringComparison.Ordinal) ? FactKind.Decision : FactKind.Info;
                var check = FactEvidence.Check(item.Text.Split(" — ")[0], string.Join("。", cited), kind);
                if (check.Level == Support.Unsupported)
                {
                    wrongLinks++;
                }
            }
        }

        int review = items.Count(i => i.Raw.Contains("要確認", StringComparison.Ordinal));
        return new BenchScore(c.Name, c.Facts.Length, full, partial, c.Facts.Count(f => f.Important), importantFull, wrongSubject,
            decisions, c.Facts.Count(f => f.Kind == FactKind.Decision), tasks, c.Facts.Count(f => f.Kind == FactKind.Task), undecided,
            invented, duplicates, headings.Count, badHeadings, split, links, wrongLinks, review, items.Count, seconds, peakMb, importantEvidenced);
    }
}

/// <summary>
/// FLOATINGMEMO_LLM=1: runs every made-up meeting through the app's pipeline with the local model
/// and writes the scores (and the drafts) to FLOATINGMEMO_BENCH_OUT (a folder). Label with
/// FLOATINGMEMO_LLM_LABEL; FLOATINGMEMO_ACCEL as elsewhere.
/// </summary>
public sealed class QualityBenchRun(ITestOutputHelper output)
{
    [LlmFact]
    public async Task RunAll()
    {
        string outDir = Environment.GetEnvironmentVariable("FLOATINGMEMO_BENCH_OUT") ?? Path.Combine(Path.GetTempPath(), "fm-bench");
        Directory.CreateDirectory(outDir);
        string label = Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_LABEL") ?? "current";
        var mode = Enum.Parse<Services.Acceleration.AccelerationMode>(Environment.GetEnvironmentVariable("FLOATINGMEMO_ACCEL") ?? "Auto");
        Services.Acceleration.AccelerationState.Initialize(mode);
        string model = Path.Combine(Services.ModelLocations.UserModelsDirectory, "Qwen3-4B-Q4_K_M.gguf");
        Assert.True(File.Exists(model), model);

        var sb = new StringBuilder($"# 議事録品質ベンチマーク（{label}、Qwen3-4B-Q4_K_M、{mode}、{DateTime.Now:yyyy-MM-dd HH:mm}）\n\n");
        // FLOATINGMEMO_BENCH_SET: main (the tuned set), holdout (never tuned on) or all.
        // FLOATINGMEMO_BENCH_SEEDS: comma-separated sampler seeds, one run each (spread).
        string set = Environment.GetEnvironmentVariable("FLOATINGMEMO_BENCH_SET") ?? "main";
        BenchCase[] cases = set switch { "holdout" => QualityHoldout.All, "all" => [.. QualityBench.All, .. QualityHoldout.All], _ => QualityBench.All };
        var seeds = (Environment.GetEnvironmentVariable("FLOATINGMEMO_BENCH_SEEDS") ?? "42").Split(',').Select(uint.Parse).ToList();
        sb.Append($"評価データ: {set}、シード: {string.Join(",", seeds)}\n\n");
        sb.Append(BenchScore.Header).Append('\n');
        using var llm = new LlamaTextGenerator(model);
        var scores = new List<BenchScore>();
        foreach (uint seed in seeds)
        foreach (var c in cases)
        {
            LlamaTextGenerator.SamplingSeed = seed;
            long peak = 0;
            using var sampler = new Timer(_ => { using var p = Process.GetCurrentProcess(); peak = Math.Max(peak, p.WorkingSet64); }, null, 0, 200);
            var watch = Stopwatch.StartNew();
            var draft = await new MinutesGenerator(llm, MinutesStyle.Facts) { Priority = MinutesPriority.Quality }
                .GenerateAsync(c.Note, c.Title, new DateTime(2026, 10, 28, 9, 0, 0));
            watch.Stop();
            var score = QualityBench.Score(c, draft.Markdown, watch.Elapsed.TotalSeconds, peak / 1024 / 1024) with { Case = seeds.Count > 1 ? $"{c.Name}（{seed}）" : c.Name };
            scores.Add(score);
            sb.Append(score.Row).Append('\n');
            File.WriteAllText(Path.Combine(outDir, $"{label}-{c.Name}-{seed}.md"), draft.Markdown);
            if (draft.Trace is { } trace)
            {
                File.WriteAllLines(Path.Combine(outDir, $"{label}-{c.Name}-{seed}.trace.tsv"),
                    trace.Items.Select(t => $"{t.Stage}	{t.Kind}	{t.Sources}	{t.Text}	{t.Note}"));
            }
            output.WriteLine(score.Row);
        }

        int Sum(Func<BenchScore, int> f) => scores.Sum(f);
        sb.Append($"| **合計** | {Sum(s => s.Full)}/{Sum(s => s.Facts)} | {Sum(s => s.Partial)} | {Sum(s => s.ImportantFull)}/{Sum(s => s.ImportantTotal)} | {Sum(s => s.ImportantEvidenced)}/{Sum(s => s.ImportantTotal)} | {Sum(s => s.WrongSubject)} | "
            + $"{Sum(s => s.Decisions)}/{Sum(s => s.DecisionsTotal)} | {Sum(s => s.Tasks)}/{Sum(s => s.TasksTotal)} | {Sum(s => s.UndecidedAsDecision)} | {Sum(s => s.InventedNumbers)} | "
            + $"{Sum(s => s.Duplicates)} | {Sum(s => s.Headings)} | {Sum(s => s.BadHeadings)} | {Sum(s => s.SplitSubjects)} | {Sum(s => s.WrongLinks)}/{Sum(s => s.Links)} | "
            + $"{Sum(s => s.NeedsReview)} | {Sum(s => s.Items)} | {scores.Sum(s => s.Seconds):F0}s | {scores.Max(s => s.PeakMb)}MB |\n");
        if (seeds.Count > 1)
        {
            // Spread over the seeds: the important-fact coverage of each run.
            var perRun = seeds.Select(s => scores.Where(x => x.Case.EndsWith($"（{s}）", StringComparison.Ordinal)).Sum(x => x.ImportantFull)).ToList();
            sb.Append($"\n重要事項の網羅（実行ごと）: {string.Join(" / ", perRun)}（最小 {perRun.Min()}、最大 {perRun.Max()}、平均 {perRun.Average():F1}）\n");
        }

        File.WriteAllText(Path.Combine(outDir, $"{label}-scores.md"), sb.ToString());
        output.WriteLine(sb.ToString());
    }
}

/// <summary>Merging topics never mixes up two subjects whose names only look alike.</summary>
public sealed class TopicMergeTests
{
    [Theory]
    [InlineData("北町店の課題", "南町店の課題", false)]
    [InlineData("佐藤さんの対応", "加藤さんの対応", false)]
    [InlineData("北町店", "北町店の課題", true)]
    [InlineData("アンケートの結果", "アンケート結果", true)]
    public void SimilarNames_OfDifferentSubjects_AreNotMerged(string a, string b, bool same) =>
        Assert.Equal(same, FactMinutes.SameTopicForTests(a, b));
}

/// <summary>Decisions stated as "〜します" are decisions; plans, proposals and "検討します" are not.</summary>
public sealed class DecisionWordingTests
{
    [Theory]
    [InlineData("月額は二千九百八十円で始めます。", FactKind.Info, FactKind.Decision)]
    [InlineData("年額プランは今回は作りません。", FactKind.Info, FactKind.Decision)]
    [InlineData("席替えは十一月の第三週に行います。", FactKind.Info, FactKind.Decision)]
    [InlineData("発売日は四月一日を予定しています。", FactKind.Info, FactKind.Info)]
    [InlineData("研修を五日に延ばしてはどうかという提案がありました。", FactKind.Proposal, FactKind.Proposal)]
    [InlineData("法人向けの割引は、次回までに検討します。", FactKind.Proposal, FactKind.Open)]
    [InlineData("期間については、人事と相談してから決めます。", FactKind.Info, FactKind.Open)]
    [InlineData("来週は全員で行いますかもしれません。", FactKind.Info, FactKind.Info)]
    public void WordingOfTheStatement_SetsTheKind(string statement, string modelKind, string expected)
    {
        var units = new Dictionary<string, SourceUnit> { ["B001"] = new SourceUnit("B001", statement, false) };
        var fact = new Fact(modelKind, "話題", statement, ["B001"], string.Empty, string.Empty);
        Assert.Equal(expected, FactMinutes.ByWording(fact, units).Kind);
    }
}

public sealed class SpokenOpeningTests
{
    [Theory]
    [InlineData("あ、はい、消火器の交換は藤田さんが行います", "はい、消火器の交換は藤田さんが行います")]
    [InlineData("議論の結果、北町店にはセルフレジを二台追加します", "北町店にはセルフレジを二台追加します")]
    [InlineData("えーと、では、契約書の確認をお願いします", "契約書の確認をお願いします")]
    [InlineData("あと三日で終わります", "あと三日で終わります")]
    public void OpeningWords_AreDropped(string spoken, string written) => Assert.Equal(written, FactMinutes.WithoutSpokenOpening(spoken));
}
