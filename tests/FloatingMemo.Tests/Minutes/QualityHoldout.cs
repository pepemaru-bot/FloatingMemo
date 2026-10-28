using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// A second set of made-up meetings, written after the v0.10.0 fixes and never used to tune them:
/// other subjects, other wording, other filler talk. It shows whether the improvements carry over
/// to meetings the pipeline was not adjusted to. No real people, organisations or meetings.
/// </summary>
public static class QualityHoldout
{
    // Filler talk in other words than the first benchmark's.
    private static readonly string[] Talk =
    [
        "まあ、そのへんは人によって受け止め方が違うと思います。",
        "私も同じ意見で、特に付け足すことはありません。",
        "前に別の部署でも似たケースがあったと聞きました。",
        "ちょっと話がそれますけど、最近は問い合わせの質も変わってきました。",
        "それはそれで大事な観点ですね。",
        "現場からは特に大きな不満は出ていないようです。",
        "ここは皆さんの感触も聞いておきたいところです。",
        "うちのチームでも少し話題になっていました。",
        "具体的な話は、また資料を見ながらにしましょうか。",
        "全体としては悪くない流れだと思います。",
        "細かいところは担当者同士で詰めてもらえれば。",
        "去年の今ごろはもっとバタバタしていましたね。",
    ];

    private static string Talking(int count, int seed) =>
        string.Join("\n", Enumerable.Range(0, count).Select(i => Talk[(seed + i * 5) % Talk.Length]));

    /// <summary>Several people, three agenda items, decisions stated as "〜します".</summary>
    public static readonly BenchCase Team = new(
        "複数人の業務会議", "総務チームの定例（架空）",
        string.Join("\n", [
            "では定例を始めます。今日は三つです。", "一つ目、経費精算の新しいシステムの件です。",
            "田村です。いまの申請は紙なので、月末に処理が集中して残業が月四十時間を超えています。", Talking(5, 0),
            "システムは来年一月から切り替えます。", "操作マニュアルは田村さんが十二月十日までに作ってください。",
            "二つ目、新人研修です。", "今年の新人は六名です。", Talking(4, 1),
            "研修の期間を三日から五日に延ばしてはどうかという提案がありました。", "期間については、人事と相談してから決めます。",
            "三つ目、オフィスの席替えです。", "席替えは十一月の第三週に行います。", Talking(4, 2),
            "レイアウト案は川口さんが来週の月曜までに出してください。",
            "以上です。お疲れさまでした。",
        ]),
        ["経費精算", "研修", "席替え"],
        [
            new("経費精算", FactKind.Info, ["残業", "40時間"]),
            new("経費精算", FactKind.Decision, ["1月", "切り替え"]),
            new("経費精算", FactKind.Task, ["マニュアル"], Who: "田村", Due: "12月10日"),
            new("研修", FactKind.Info, ["6名"]),
            new("研修", FactKind.Proposal, ["5日"], Important: false),
            new("研修", FactKind.Open, ["期間", "人事"]),
            new("席替え", FactKind.Decision, ["11月", "第三週"]),
            new("席替え", FactKind.Task, ["レイアウト"], Who: "川口", Due: "来週の月曜"),
        ],
        ["5日"], []);

    /// <summary>Three customer cases one after another; the first comes back at the end.</summary>
    public static readonly BenchCase Customers = new(
        "複数の顧客案件", "営業案件の進捗会議（架空）",
        string.Join("\n", [
            "案件の進捗を順番に確認します。", "まず青葉商事です。", "提案書は先週提出済みで、先方の決裁待ちです。", Talking(5, 3),
            "決裁は今月末の予定と聞いています。",
            "次は緑川物流です。", "こちらは見積もりの金額が高いと言われています。", Talking(5, 4),
            "値引きは五パーセントまでなら対応することにしました。", "再見積もりは森本さんが金曜日までに送ってください。",
            "最後に白石製作所です。", "先方の担当者が十月で異動になりました。", Talking(4, 5),
            "新しい担当者へのあいさつは、来週の訪問で行います。",
            "あ、青葉商事について一点追加です。", "決裁が下りたら、納品は二月にします。",
        ]),
        ["青葉商事", "緑川物流", "白石製作所"],
        [
            new("青葉商事", FactKind.Info, ["提案書", "決裁"]),
            new("青葉商事", FactKind.Info, ["今月末"]),
            new("青葉商事", FactKind.Decision, ["納品", "2月"]),
            new("緑川物流", FactKind.Info, ["見積", "高い"]),
            new("緑川物流", FactKind.Decision, ["値引き", "5"]),
            new("緑川物流", FactKind.Task, ["再見積"], Who: "森本", Due: "金曜日"),
            new("白石製作所", FactKind.Info, ["異動"]),
            new("白石製作所", FactKind.Decision, ["あいさつ", "訪問"]),
        ],
        [], []);

    /// <summary>One subject early and late, another in between, with a correction.</summary>
    public static readonly BenchCase Hiring = new(
        "同一議題の再登場", "採用計画の打ち合わせ（架空）",
        string.Join("\n", [
            "採用計画から始めます。", "来年度は営業職を四名、技術職を三名採用する方向です。", "応募は今のところ二十件来ています。", Talking(6, 6),
            "続いて予算の話です。", "採用広告の予算は八十万円です。", Talking(5, 7),
            "広告は求人サイト二つに絞ります。",
            "採用計画に戻ります。", "さっき技術職三名と言いましたが、二名に訂正します。",
            "面接は一次をオンライン、二次を対面で行うことにしました。", "面接官の割り当ては小野さんが今週中に決めてください。",
        ]),
        ["採用", "予算"],
        [
            new("採用", FactKind.Info, ["営業職", "4名"]),
            new("採用", FactKind.Info, ["技術職", "2名"]),
            new("採用", FactKind.Info, ["20件"], Important: false),
            new("採用", FactKind.Decision, ["一次", "オンライン"]),
            new("採用", FactKind.Task, ["面接官"], Who: "小野", Due: "今週中"),
            new("予算", FactKind.Info, ["80万円"]),
            new("予算", FactKind.Decision, ["求人サイト", "2つ"]),
        ],
        [], []);

    /// <summary>Numbers, a negation, a correction, a proposal and decisions in one discussion.</summary>
    public static readonly BenchCase Pricing = new(
        "数値・否定・訂正・提案・決定", "新サービスの料金検討（架空）",
        string.Join("\n", [
            "新しいサービスの料金を決めます。", "月額は二千九百八十円で始めます。", Talking(4, 8),
            "年額プランを作ってはどうかという提案がありました。", "年額プランは今回は作りません。",
            "無料期間は十四日と言いましたが、三十日の誤りでした。無料期間は三十日です。", Talking(4, 9),
            "法人向けの割引は、次回までに検討します。",
            "発売日は四月一日を予定しています。",
            "料金表のページは原田さんが三月十五日までに作ってください。",
        ]),
        ["料金"],
        [
            new("料金", FactKind.Decision, ["2980円"]),
            new("料金", FactKind.Decision, ["年額", "作りません"]),
            new("料金", FactKind.Info, ["無料期間", "30日"]),
            new("料金", FactKind.Open, ["法人", "割引"]),
            new("料金", FactKind.Info, ["4月1日"]),
            new("料金", FactKind.Task, ["料金表"], Who: "原田", Due: "3月15日"),
        ],
        ["法人", "割引"], []);

    /// <summary>A long meeting that switches topics often and comes back to some of them.</summary>
    public static readonly BenchCase Switching = new(
        "話題が何度も切り替わる長い会議", "部門横断の連絡会（架空）",
        string.Join("\n", [
            "連絡会を始めます。話題が多いので順に行きます。",
            "まず防災訓練です。", "訓練は十一月二十日の午後に行います。", Talking(4, 10),
            "次に社内報です。", "社内報は今月から隔月発行にします。", Talking(4, 11),
            "それから会議室の予約システムです。", "予約の重複が先月十二件ありました。", Talking(4, 0),
            "防災訓練の補足ですが、避難経路の図は総務で更新します。",
            "次は健康診断です。", "健康診断の受診率は八十五パーセントでした。", Talking(4, 1),
            "未受診の人には個別に連絡します。",
            "会議室の件に戻ると、予約のルールは来月までに見直します。", "見直し案は松井さんが十一月末までにまとめてください。",
            "最後にノートパソコンの入れ替えです。", "入れ替えは三十台で、業者はまだ決まっていません。", Talking(4, 2),
            "社内報の件で一つ、次号の締め切りは十一月十日です。",
            "以上で連絡会を終わります。",
        ]),
        ["防災訓練", "社内報", "会議室", "健康診断", "ノートパソコン"],
        [
            new("防災訓練", FactKind.Decision, ["11月20日"]),
            new("防災訓練", FactKind.Decision, ["避難経路", "総務"], Important: false),
            new("社内報", FactKind.Decision, ["隔月"]),
            new("社内報", FactKind.Info, ["締め切り", "11月10日"]),
            new("会議室", FactKind.Info, ["重複", "12件"]),
            new("会議室", FactKind.Decision, ["ルール", "見直"]),
            new("会議室", FactKind.Task, ["見直し案"], Who: "松井", Due: "11月末"),
            new("健康診断", FactKind.Info, ["85"]),
            new("健康診断", FactKind.Decision, ["未受診", "連絡"], Important: false),
            new("ノートパソコン", FactKind.Info, ["30台"]),
            new("ノートパソコン", FactKind.Open, ["業者"]),
        ],
        ["業者"], []);

    public static readonly BenchCase[] All = [Team, Customers, Hiring, Pricing, Switching];
}
