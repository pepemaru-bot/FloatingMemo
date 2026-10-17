using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>What a correct draft of one made-up meeting must contain (and must not).</summary>
public sealed record ExpectedMinutes
{
    /// <summary>Topics: each entry is a set of alternative words; one of them must appear.</summary>
    public required string[][] Topics { get; init; }

    /// <summary>Decisions: words that must appear together in one decision item.</summary>
    public required string[][] Decisions { get; init; }

    /// <summary>Tasks: (what, assignee, deadline) — all in one task item.</summary>
    public required (string What, string Who, string Due)[] Tasks { get; init; }

    /// <summary>Words of open questions that must NOT be written as decisions.</summary>
    public required string[] NotDecided { get; init; }

    /// <summary>Numbers that must appear (arabic digits; kanji numerals are converted before comparing).</summary>
    public string[] Numbers { get; init; } = [];

    /// <summary>Numbers that must NOT appear (e.g. a figure that was corrected in the meeting).</summary>
    public string[] WrongNumbers { get; init; } = [];
}

/// <summary>One made-up meeting: notes, transcript, dictionary and the expected result.</summary>
public sealed record EvaluationCase(string Name, string Title, string HumanNotes, string Transcript, IReadOnlyList<TermEntry> Dictionary, ExpectedMinutes Expected)
{
    public string Note => HumanNotes.Trim() + "\n\n" + TranscriptInsertion.Header + "\n" + Transcript.Replace("\r\n", "\n").Trim();
}

/// <summary>
/// Made-up meetings for comparing minutes methods (no real people, organisations or meetings;
/// topics unrelated to any real business). Short, long, noisy and topic-switching.
/// </summary>
public static class EvaluationMeetings
{
    public static readonly EvaluationCase Short = new(
        "短い会議（校正あり）", "夏祭り準備（架空）",
        """
        # 夏祭り実行委員会（架空）
        - 参加: 高橋、佐藤、中村（すべて架空）
        - 自分用: 帰りに備品の数を数える
        """,
        """
        今日は夏祭りの断取りについて話しました。
        会場は加工市民ホールの前の広場に決まりました。
        雨の場合は加工市民ホールの中で行います。
        屋台は八店で合意しました。
        ポスターの作成は高橋さんが来週の金曜日までに行います。
        花火を上げるかどうかは、次回の会議までに検討しましょう。
        駐車場の台数はまだ決まっていません。
        """,
        [
            new TermEntry { Term = "架空市民ホール", Aliases = ["市民ホール"], Misrecognitions = ["加工市民ホール"], Category = "会場" },
            new TermEntry { Term = "段取り", Misrecognitions = ["断取り"], Category = "一般" },
        ],
        new ExpectedMinutes
        {
            Topics = [["会場", "広場"], ["屋台"], ["ポスター"], ["花火"], ["駐車場"]],
            Decisions = [["広場"], ["屋台", "8"]],
            Tasks = [("ポスター", "高橋", "金曜")],
            NotDecided = ["花火", "駐車場"],
            Numbers = ["8"],
        });

    public static readonly EvaluationCase Long = new(
        "長い会議（2 分割）", "アプリ開発定例（架空）",
        LongMeeting.HumanNotes, LongMeeting.Transcript, [],
        new ExpectedMinutes
        {
            Topics = [["ログイン画面"], ["リリース日"], ["テスト計画"], ["問い合わせフォーム"], ["予算"], ["サーバー"], ["アンケート"], ["研修"], ["広報"], ["次回"]],
            Decisions = [["青"], ["リリース", "11月20日"], ["研修", "12月"]],
            Tasks = [("テスト計画", "森", "水曜"), ("問い合わせフォーム", "石井", "今週")],
            NotDecided = ["予算", "サーバー"],
            Numbers = ["11月20日", "120", "12月", "11月8日"],
        });

    /// <summary>Fillers, false starts and a figure corrected in the middle of a sentence.</summary>
    public static readonly EvaluationCase Noisy = new(
        "ノイズの多い会議", "社内勉強会の準備（架空）",
        """
        # 勉強会の準備（架空）
        - 参加: 中村、小林
        """,
        """
        えーと、聞こえてますか。はい、聞こえてます。あ、ちょっと音が。大丈夫です。
        じゃあ、勉強会の、えー、勉強会の件ですけど。
        開催日は十二月三日に決まりました。あ、三日、三日で大丈夫です。
        参加者は二十人、あ、いや、二十五人です。参加者は二十五人の予定です。
        えっと、すみません、もう一度お願いします。はい。
        会場は第二会議室にします。
        資料の準備は中村さんが前日までに行います。
        あの、オンラインでも参加できるようにするかは、まだ決まっていません。
        えー、はい、そうですね、なるほど。
        お菓子とかは、まあ、いいかなと思います。はい。
        アンケートは終わったあとに小林さんが取ります。
        はい、じゃあそんな感じで。お疲れさまでした。
        """,
        [],
        new ExpectedMinutes
        {
            Topics = [["開催日", "12月3日"], ["参加者", "25"], ["会場", "第二会議室", "第2会議室"], ["資料"], ["オンライン"], ["アンケート"]],
            Decisions = [["12月3日"]],
            Tasks = [("資料", "中村", "前日"), ("アンケート", "小林", "")],
            NotDecided = ["オンライン"],
            Numbers = ["12月3日", "25"],
            WrongNumbers = ["20人"],
        });

    /// <summary>Topics come back later with new figures; the later statement is the result.</summary>
    public static readonly EvaluationCase Switching = new(
        "話題が何度も切り替わる会議", "事務所移転の打ち合わせ（架空）",
        """
        # 事務所移転（架空）
        - 参加: 加藤、山田
        """,
        """
        事務所の移転日について確認します。移転日は三月の中旬の予定です。
        次に予算です。予算は百五十万円で考えています。
        ネット回線の申し込みは加藤さんが今月中に行います。
        机の配置は、まだ決まっていません。図面を見てから決めましょう。
        移転日の話に戻りますが、移転日は三月二十日に決まりました。
        電話の番号はそのまま使えることを確認しました。
        予算の話に戻ります。見積もりが上がったので、予算は百八十万円に増やすことで承認されました。
        引っ越し業者は二社から見積もりを取ります。山田さんが来週までに取ります。
        机の配置は次回また話しましょう。
        """,
        [],
        new ExpectedMinutes
        {
            Topics = [["移転日"], ["予算"], ["回線"], ["机"], ["電話"], ["業者", "見積"]],
            Decisions = [["移転日", "3月20日"], ["予算", "180"]],
            Tasks = [("回線", "加藤", "今月"), ("見積", "山田", "来週")],
            NotDecided = ["机"],
            Numbers = ["3月20日", "180"],
        });

    public static readonly EvaluationCase[] All = [Short, Long, Noisy, Switching];
}
