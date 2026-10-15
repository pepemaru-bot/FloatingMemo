using System.Text;
using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Minutes;

/// <summary>How the minutes are laid out.</summary>
public enum MinutesStyle
{
    /// <summary>Seven fixed headings (概要・議題・要点・決定・ToDo・未決・次回).</summary>
    FixedSections,

    /// <summary>
    /// One heading per topic that was actually discussed, in the order of the meeting. Decisions,
    /// tasks and open questions are marked inside their topic instead of being repeated in
    /// separate sections. The person's own notes are passed apart from the transcript.
    /// </summary>
    Topics,

    /// <summary>
    /// Facts first: the source is numbered (notes A…, transcript sentences B…), the model lists
    /// facts with the numbers that back them, the checks verify them, and the minutes are
    /// written from the checked facts only (see <see cref="FactMinutes"/>).
    /// </summary>
    Facts,
}

/// <summary>Which layout each model gets (from the same-conditions comparison, docs/evaluation).</summary>
public static class MinutesStyles
{
    /// <summary>
    /// Facts first, except for Qwen3 1.7B. Same made-up meetings, same settings: with 4B the
    /// fact-first layout was as good or better than the topic layout on every check (10/10 topics,
    /// tasks 2/2, no duplicates or unsupported items) and about 20 % faster; 1.7B found only 3 of
    /// 10 topics of a long meeting that way (8 with topics, 10 with the fixed headings), so it
    /// keeps the fixed headings.
    /// </summary>
    public static MinutesStyle For(string modelName) =>
        modelName.Contains("1.7B", StringComparison.OrdinalIgnoreCase) ? MinutesStyle.FixedSections : MinutesStyle.Facts;
}

/// <summary>Instructions for the topic style (Japanese, because the meetings are Japanese).</summary>
public static class TopicPrompt
{
    public const int MaxAnswerTokens = 1500;

    /// <summary>
    /// Notes longer than this are not passed apart (they would be repeated with every part of a
    /// long transcript); the whole note is then summarized as one text. Notes are never cut.
    /// </summary>
    public const int MaxSeparateNotesCharacters = 3000;

    public const string Overview = "概要";
    public const string Decision = "【決定】";
    public const string Open = "【未決】";
    public const string Task = "【ToDo】";
    public const string FromNotes = "（メモより）";

    public static string System { get; } = $"""
        あなたは会議の文字起こしから議事録の下書きを作る書記です。次の規則を必ず守ってください。
        1. 文字起こしに書かれていることだけを使う。推測・補完・一般論・文字起こしにない固有名詞は書かない。
           言葉はできるだけ文字起こしのまま使い、言い換えたり新しい言葉を作ったりしない。日本語だけで書く。
        2. 最初に「## {Overview}」として、会議全体を1〜2行の箇条書きで書く。
        3. そのあと、会議で話された話題ごとに「## 見出し」を作り、話された順に並べる。見出しは文字起こしの言葉で短く書く。
           同じ話題は1つの見出しにまとめ、同じ内容を2回書かない。あいさつ・音声や画面の確認・相づちは書かない。
        4. 各見出しの下に、その話題で話された内容を箇条書きで書く。数字・日付・名前・理由は省略しない。
        5. 決まったこと（「〜に決まりました」「〜することになりました」「〜で合意」）だけ、行頭に{Decision}を付ける。
           提案・意見・検討中のことには付けない。
        6. 決まっていないこと（「まだ決まっていません」「次回までに検討」など）は行頭に{Open}を付ける。
        7. だれかが行うことになった作業（「〜さんが〜までに行います」「〜をお願いします」など）は、{Decision}ではなく
           「{Task}内容 — 担当: 名前または未確認 / 期限: 期限または未確認」と書く。
           担当者・期限は文字起こしで明言されている場合だけ書き、なければ「未確認」と書く。
        8. {Overview}以外の各項目の末尾に、根拠となる文字起こしの短い引用を（原文:「…」）の形で付ける。引用は文字起こしからそのまま写す。
        9. <メモ>は参加者が自分で書いたものです。話題や名前の理解に使ってよい。メモにしか書かれていない会議の情報（参加者など）を書く場合は
           末尾に{FromNotes}と付ける（引用は不要）。個人的な覚え書きは書かない。
        10. 前置きや結びの文は書かない。
        書き方の例（形だけの例です。内容は必ずこの会議の文字起こしから書くこと）:
        ## {Overview}
        - 町内会の打ち合わせで、夏祭りの日程と準備を話した。
        ## 夏祭りの日程
        - {Decision}夏祭りは八月三日（原文:「夏祭りは八月三日に決まりました」）
        - 雨の場合は公民館を使う案が出た（原文:「雨なら公民館という案もあります」）
        ## テントの準備
        - {Task}テントの手配 — 担当: 中村 / 期限: 来月（原文:「テントは中村さんが来月までに手配します」）
        ## 予算
        - {Open}予算の上限（原文:「予算はまだ決まっていません」）
        """;

    public static string User(string notes, string transcript, int part, int parts)
    {
        var sb = new StringBuilder();
        if (parts > 1)
        {
            sb.Append($"これは文字起こし全体の {part + 1}/{parts} 番目の部分です。この部分で話された話題だけを書いてください。\n");
        }

        string trimmedNotes = notes.Trim();
        if (trimmedNotes.Length > 0)
        {
            sb.Append("<メモ>\n").Append(trimmedNotes).Append("\n</メモ>\n");
        }

        sb.Append("<文字起こし>\n").Append(transcript.Trim()).Append("\n</文字起こし>\n/no_think");
        return sb.ToString();
    }
}

/// <summary>Parses, merges and checks topic-style answers (see <see cref="MinutesStyle.Topics"/>).</summary>
public static partial class TopicMinutes
{
    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    [GeneratedRegex(@"^\s*#{1,6}\s*(?<title>.+?)\s*#*\s*$")]
    private static partial Regex Heading();

    [GeneratedRegex(@"^(\s*)[-*]\s+(\[[ xX]\]\s*)?")]
    private static partial Regex Bullet();

    [GeneratedRegex(@"^\s*【\s*(?<m>決定|未決|ToDo|todo|TODO)\s*】")]
    private static partial Regex Mark();

    [GeneratedRegex(@"^- [（(]\s*(?:原文\s*[:：]\s*)?「[^」]*」\s*[）)]\s*$")]
    private static partial Regex QuoteOnly();

    [GeneratedRegex(@"^\s*([-*]\s+)?\[[ xX]\]\s*")]
    private static partial Regex Checkbox();

    /// <summary>Headings in the order they first appear, each with its lines.</summary>
    public static List<(string Title, List<string> Lines)> Parse(string answer)
    {
        var topics = new List<(string Title, List<string> Lines)>();
        List<string>? current = null;
        foreach (string raw in ThinkBlock().Replace(answer, string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (Heading().Match(line) is { Success: true } heading)
            {
                string title = heading.Groups["title"].Value.Trim().Trim('*').Trim();
                current = [];
                topics.Add((title, current));
                continue;
            }

            if (current is null || line.Trim().Length == 0)
            {
                continue; // preamble before the first heading is not part of the minutes
            }

            if (current.Count > 0 && char.IsWhiteSpace(line[0]) && !MinutesValidator.IsListItem(line))
            {
                current[^1] = current[^1] + " " + line.Trim(); // a quote on its own line
                continue;
            }

            // A quote written as its own item belongs to the item before it.
            string normalized = Normalize(line);
            if (current.Count > 0 && QuoteOnly().IsMatch(normalized))
            {
                current[^1] = current[^1] + " " + normalized[2..].Trim();
                continue;
            }

            current.Add(normalized);
        }

        return topics.Where(t => t.Lines.Any(l => !MinutesValidator.IsNone(l)) || t.Title == TopicPrompt.Overview).ToList();
    }

    /// <summary>One item per line: "- 内容", ToDo as a checkbox, double bullets removed.</summary>
    private static string Normalize(string line)
    {
        string content = Bullet().Replace(line.Trim(), string.Empty).Trim();
        while (content.StartsWith('-') || content.StartsWith('*'))
        {
            content = content[1..].Trim();
        }

        // A checkbox written by the model (with or without a bullet) is a task too.
        bool checkbox = Checkbox().IsMatch(line) || Checkbox().IsMatch(content);
        content = Checkbox().Replace(content, string.Empty).Trim();

        // "【 ToDo 】", "【todo】", "【決定 】" → the marks as instructed.
        content = Mark().Replace(content, m => "【" + (m.Groups["m"].Value.Equals("todo", StringComparison.OrdinalIgnoreCase) ? "ToDo" : m.Groups["m"].Value) + "】");

        if (content.StartsWith(TopicPrompt.Task, StringComparison.Ordinal))
        {
            return "- [ ] " + content[TopicPrompt.Task.Length..].Trim();
        }

        return checkbox ? "- [ ] " + content : "- " + content;
    }

    /// <summary>
    /// Combines the answers for the parts of a long transcript: the same topic (similar heading)
    /// becomes one section, and items repeated word for word are kept once.
    /// </summary>
    public static List<(string Title, List<string> Lines)> Merge(IEnumerable<string> answers)
    {
        var merged = new List<(string Title, List<string> Lines)>();
        foreach (string answer in answers)
        {
            foreach (var (title, lines) in Parse(answer))
            {
                var same = merged.FirstOrDefault(m => SameTopic(m.Title, title));
                if (same.Lines is null)
                {
                    same = (title, new List<string>());
                    if (title == TopicPrompt.Overview)
                    {
                        merged.Insert(0, same);
                    }
                    else
                    {
                        merged.Add(same);
                    }
                }

                foreach (string line in lines.Where(l => !MinutesValidator.IsNone(l)))
                {
                    string key = Key(line);
                    if (!merged.Any(m => m.Lines.Any(l => MinutesQuality.Similarity(Key(l), key) >= 0.9)))
                    {
                        same.Lines.Add(line);
                    }
                }
            }
        }

        return merged.Where(m => m.Lines.Count > 0).ToList();
    }

    private static string Key(string line) => MinutesValidator.Normalize(Bullet().Replace(line, string.Empty));

    private static bool SameTopic(string a, string b)
    {
        string x = MinutesValidator.Normalize(a), y = MinutesValidator.Normalize(b);
        return x == y || (Math.Min(x.Length, y.Length) >= 2 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)))
            || MinutesQuality.Similarity(x, y) >= 0.75;
    }

    /// <summary>
    /// Checks every item like the fixed style does (invented assignees and deadlines, quotes not
    /// in the transcript, words not in the transcript) and renders the sections as Markdown.
    /// </summary>
    public static string Check(List<(string Title, List<string> Lines)> topics, string said, string saidOrNoted, List<string> warnings)
    {
        string normalized = MinutesValidator.Normalize(said);
        string withNotes = MinutesValidator.Normalize(saidOrNoted);
        var sb = new StringBuilder();
        foreach (var (title, lines) in topics)
        {
            sb.Append("## ").Append(title).Append('\n');
            foreach (string line in lines)
            {
                string item = AsTask(line);
                bool fromNotes = item.Contains(TopicPrompt.FromNotes, StringComparison.Ordinal);
                sb.Append(MinutesValidator.CheckLine(item, KindOf(item, title), fromNotes ? withNotes : normalized, warnings)).Append('\n');
            }

            sb.Append('\n');
        }

        if (topics.Count == 0)
        {
            sb.Append("## ").Append(TopicPrompt.Overview).Append("\n- なし（AIの回答から話題を読み取れませんでした）\n");
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    /// <summary>
    /// A decision that names who does it and by when ("森さんが来週の水曜日までに作ることに
    /// なりました") is also a task: shown with a checkbox so it can be ticked off. Mechanical, so
    /// the model does not have to write the same thing twice.
    /// </summary>
    internal static string AsTask(string line)
    {
        string content = Bullet().Replace(line, string.Empty);
        string said = Quote().Replace(content, string.Empty);
        return line.TrimStart().StartsWith("- 【決定】", StringComparison.Ordinal) && Assignee().IsMatch(said) && Deadline().IsMatch(said)
            ? "- [ ] " + content
            : line;
    }

    [GeneratedRegex(@"\p{L}{1,8}(さん|氏|君|くん)(が|は)")]
    private static partial Regex Assignee();

    [GeneratedRegex(@"まで|今週中|今月中|今日中|中に|期限")]
    private static partial Regex Deadline();

    [GeneratedRegex(@"[（(]\s*(?:原文\s*[:：]\s*)?「[^」]*」\s*[）)]")]
    private static partial Regex Quote();

    /// <summary>Which fixed-style rules apply to an item (decisions and tasks need a quote, …).</summary>
    internal static string KindOf(string line, string title)
    {
        string content = Bullet().Replace(line, string.Empty);
        if (title == TopicPrompt.Overview || content.Contains(TopicPrompt.FromNotes, StringComparison.Ordinal))
        {
            return "会議の概要"; // no quote needed
        }

        if (line.TrimStart().StartsWith("- [", StringComparison.Ordinal))
        {
            return MinutesPrompt.Sections[4]; // ToDo（担当者・期限）
        }

        if (content.StartsWith(TopicPrompt.Decision, StringComparison.Ordinal))
        {
            return "決定事項";
        }

        return content.StartsWith(TopicPrompt.Open, StringComparison.Ordinal) ? "未決事項" : "議論の要点";
    }
}
