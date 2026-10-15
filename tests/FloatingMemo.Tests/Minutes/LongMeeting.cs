namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// A made-up, longer meeting (no real people, companies or meetings) used to see what the minutes
/// pipeline keeps and loses: ten topics spread over a transcript long enough to be processed in
/// two parts, filler talk in between, the person's own notes above the transcript, and for each
/// topic what a correct draft must contain.
/// </summary>
public static class LongMeeting
{
    public const string HumanNotes = """
        # アプリ開発定例（架空）
        - 参加: 森、石井、田中、小川（すべて架空）
        - 事前資料: 予算の上限は三百万円
        - 自分用: アンケートの自由記述を後で読む
        """;

    /// <summary>One topic: words that show it is covered, and how it ended in the meeting.</summary>
    public sealed record Topic(string Name, string[] Keywords, string Outcome, string[] MustAppear);

    public static readonly Topic[] Topics =
    [
        new("ログイン画面のデザイン", ["ログイン画面"], "決定", ["青"]),
        new("リリース日", ["リリース日"], "決定", ["十一月二十日|11月20日"]),
        new("テスト計画", ["テスト計画"], "ToDo", ["森", "水曜"]),
        new("問い合わせフォームの不具合", ["問い合わせフォーム"], "ToDo", ["石井", "今週"]),
        new("予算", ["予算"], "未決", []),
        new("サーバーの移行", ["サーバー"], "未決", []),
        new("アンケート結果", ["アンケート"], "議論", ["七割|7割"]),
        new("社内研修", ["研修"], "決定", ["十二月|12月"]),
        new("広報", ["広報"], "ToDo（担当未定）", []),
        new("次回の会議", ["次回"], "次回", ["十一月八日|11月8日"]),
    ];

    /// <summary>Topics that were not decided and must not be written as decisions.</summary>
    public static readonly string[] NotDecided = ["予算", "サーバー"];

    private static readonly string[] Blocks =
    [
        "それでは定例会議を始めます。今日は議題が多いので、少し早めに進めます。よろしくお願いします。",
        "まずログイン画面のデザインについてです。前回、色の案を三つ出してもらいました。" +
        "えーと、緑の案と青の案とオレンジの案ですね。社内で見てもらったところ、青の案が読みやすいという声が多かったです。" +
        "そうですね、私も青がいいと思います。ボタンの大きさも青の案のほうが押しやすいです。" +
        "では、ログイン画面は青の案でいくことに決まりました。細かい色の調整はデザインの担当のほうで進めてください。",
        "なるほど、ありがとうございます。ちょっと資料を共有しますね。見えていますか。はい、見えています。",
        "次にリリース日についてです。テストの進み具合を見ると、当初の十一月十三日は少し厳しいです。" +
        "一週間ずらすのはどうでしょうか。営業のほうにも確認しましたが、一週間なら問題ないそうです。" +
        "それではリリース日は十一月二十日に決まりました。お客さんへの案内は、日付が決まったので来週出します。",
        "えーと、そうですね、ここまでで何か質問はありますか。特にないようなので次に進みます。",
        "続いてテスト計画です。今回は画面が増えたので、テストの項目もかなり増えています。" +
        "手動のテストと自動のテストの分け方を整理したほうがいいと思います。" +
        "テスト計画は森さんが来週の水曜日までに作ることになりました。森さん、お願いします。はい、わかりました。",
        "次は問い合わせフォームの不具合です。送信ボタンを二回押すと、同じ内容が二通届くという報告がありました。" +
        "再現しましたか。はい、こちらでも再現しました。ボタンを押したあとに無効にすれば直ると思います。" +
        "問い合わせフォームの修正は石井さんが今週中に行います。修正したらテストの担当に連絡してください。",
        "ちょっと時間が押しているので、少し急ぎます。すみません、音声が途切れたかもしれません。聞こえていますか。はい、大丈夫です。",
        "予算についてです。来期の予算をどうするか、まだ見積もりがそろっていません。" +
        "外注する部分を増やすかどうかで金額がかなり変わります。予算は見積もりがそろってから決めましょう。今日は決めません。",
        "サーバーの移行についても少し話しておきます。今のサーバーは古くなってきたので、新しいサーバーに移したほうがいいという意見があります。" +
        "ただ、移行の時期はリリースのあとがいいのではないかという意見もありました。サーバーの移行はまだ決まっていません。次回までに検討しましょう。",
        "なるほど、そうですね。ほかに意見はありますか。大丈夫そうですね。",
        "次にアンケートの結果を共有します。回答は百二十件ありました。アプリに満足しているという回答が約七割でした。" +
        "不満の多かった点は、文字が小さいことと、読み込みが遅いことです。文字の大きさは設定で変えられるようにしたいという意見が出ました。" +
        "自由記述はまだ全部読めていないので、読んだら共有します。",
        "社内研修についてです。新しく入った人が増えたので、開発の進め方の研修をしたほうがいいという話が前からありました。" +
        "研修は十二月に一回行うことに決まりました。内容はこれから考えます。",
        "広報についてです。リリースに合わせて、ブログやお知らせを出したいです。" +
        "広報の文章を誰が書くかはまだ決まっていません。手が空いている人がいれば手伝ってください。",
        "えーと、ほかに何かありますか。ないようでしたら、最後に次回の予定です。",
        "次回の会議は十一月八日の午後二時からです。今日はありがとうございました。お疲れさまでした。",
    ];

    /// <summary>The transcript, padded with ordinary filler talk to about the length of a long meeting.</summary>
    public static string Transcript { get; } = Build();

    public static string Note => HumanNotes + "\n\n以下、AIによる文字起こし\n" + Transcript;

    private static string Build()
    {
        // Filler sentences between topics, as in real transcripts ("えーと", checking audio, …).
        string[] filler =
        [
            "えーと、ちょっと待ってください。",
            "はい、そうですね。",
            "今の点は大事なので、もう一度確認させてください。",
            "なるほど、それはそうかもしれません。",
            "画面の共有を止めますね。",
            "すみません、もう一度お願いします。",
        ];

        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < Blocks.Length; i++)
        {
            sb.Append(Blocks[i]);
            for (int j = 0; j < 30; j++)
            {
                sb.Append(filler[(i + j) % filler.Length]);
            }

            sb.Append('\n');
        }

        return sb.ToString().Trim();
    }
}
