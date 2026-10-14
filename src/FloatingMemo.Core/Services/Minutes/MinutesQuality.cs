using System.Text;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// A note split into what the person wrote and what speech recognition wrote. Transcripts are
/// appended as blocks that start with <see cref="TranscriptInsertion.Header"/>; a block runs
/// until the next empty line (the next block or the person's own text starts after one).
/// </summary>
public sealed record NoteSections(string HumanNotes, IReadOnlyList<string> Transcripts)
{
    public string AllTranscripts => string.Join("\n", Transcripts);

    public static NoteSections Split(string noteText)
    {
        var human = new StringBuilder();
        var transcripts = new List<string>();
        StringBuilder? current = null;
        foreach (string raw in noteText.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.Trim() == TranscriptInsertion.Header)
            {
                Close();
                current = new StringBuilder();
                continue;
            }

            if (current is not null)
            {
                if (line.Trim().Length == 0)
                {
                    Close();
                    human.Append('\n');
                    continue;
                }

                current.Append(line).Append('\n');
                continue;
            }

            human.Append(line).Append('\n');
        }

        Close();
        return new NoteSections(human.ToString().Trim(), transcripts);

        void Close()
        {
            if (current is { Length: > 0 })
            {
                transcripts.Add(current.ToString().Trim());
            }

            current = null;
        }
    }
}

/// <summary>One problem found in a minutes draft.</summary>
public sealed record QualityIssue(string Kind, string Section, string Item, string Detail);

/// <summary>Counts of each kind of problem (lower is better) and the list of findings.</summary>
public sealed record QualityReport(IReadOnlyList<QualityIssue> Issues, int Items)
{
    public int Count(string kind) => Issues.Count(i => i.Kind == kind);

    public const string Duplicate = "重複";
    public const string UndecidedAsDecision = "未決なのに決定";
    public const string UnsupportedDecision = "根拠のない決定";
    public const string Dummy = "ダミー・空欄";
    public const string Misrecognition = "誤認識語の残り";

    public static readonly string[] Kinds = [Duplicate, UndecidedAsDecision, UnsupportedDecision, Dummy, Misrecognition];

    public string Summary() => string.Join("、", Kinds.Select(k => $"{k} {Count(k)}")) + $"（項目 {Items}）";
}

/// <summary>
/// Mechanical quality checks of a minutes draft, used to compare models under the same
/// conditions. They look for the failure patterns seen with small models: repeated topics,
/// open questions written as decisions, decisions without a source, placeholder/empty items,
/// and misrecognized words that survived. Heuristics: they flag, a person judges.
/// </summary>
public static partial class MinutesQuality
{
    [GeneratedRegex(@"[（(]原文[:：]\s*「(?<q>[^」]*)」[）)]")]
    private static partial Regex Quote();

    [GeneratedRegex(@"\s*⚠要確認（[^）]*）")]
    private static partial Regex Warning();

    [GeneratedRegex(@"^\s*[-*]\s+(\[[ xX]\]\s*)?")]
    private static partial Regex Bullet();

    // Words that mark something as not (yet) decided.
    [GeneratedRegex("決まっていません|決まっていない|未定|保留|検討しましょう|検討する|検討したい|どうするか|はどうか|ではないか|しましょうか|したいと思います|意見がありました|提案|かもしれない|次回までに|今日は決めませんでした|判断する")]
    private static partial Regex Undecided();

    // Placeholders a model copies from the instructions instead of filling in.
    [GeneratedRegex(@"^(内容|項目|〇〇|○○|XXX|xxx|\.\.\.|…|TBD|未定義)?\s*(—|-|:|：)?\s*(担当[:：]\s*(未確認|名前|〇〇)?\s*/?\s*期限[:：]\s*(未確認|期限|〇〇)?)?\s*$")]
    private static partial Regex Placeholder();

    /// <summary>The format's own example words; an item made only of one of them is a placeholder.</summary>
    private static readonly HashSet<string> TemplateWords = ["内容", "項目", "〇〇", "○○", "XXX", "…", "...", "TBD", "タスク", "作業"];

    public static QualityReport Analyze(string minutesMarkdown, string transcript, IReadOnlyList<TermEntry>? dictionary = null)
    {
        var sections = MinutesValidator.Parse(ExportBody(minutesMarkdown));
        string source = MinutesValidator.Normalize(transcript);
        var issues = new List<QualityIssue>();
        var seen = new List<(string Section, string Item, string Key)>();
        int items = 0;

        foreach (var (section, lines) in sections)
        {
            foreach (string line in lines.Where(l => Bullet().IsMatch(l)))
            {
                string item = Warning().Replace(line, string.Empty).Trim();
                string content = Bullet().Replace(item, string.Empty).Trim();
                string withoutQuotes = Quote().Replace(content, string.Empty).Trim();
                if (content is "なし" or "特になし" or "該当なし")
                {
                    continue;
                }

                items++;

                // Placeholders and empty items.
                var quotes = Quote().Matches(content).Select(m => m.Groups["q"].Value.Trim()).ToList();
                string head = withoutQuotes.Split(['—', '／'], 2)[0].Trim().TrimEnd('-', ' ');
                if (Placeholder().IsMatch(withoutQuotes) || TemplateWords.Contains(head)
                    || quotes.Any(q => q.Length == 0 || q is "…" or "..." or "〇〇"))
                {
                    issues.Add(new QualityIssue(QualityReport.Dummy, section, item, "中身のない項目・ダミーの引用"));
                    continue;
                }

                // The same topic again (here or in another section of the same kind).
                string key = MinutesValidator.Normalize(withoutQuotes);
                if (key.Length >= 4 && seen.FirstOrDefault(s => Similarity(s.Key, key) >= 0.8) is { Item: not null } twin)
                {
                    issues.Add(new QualityIssue(QualityReport.Duplicate, section, item, $"「{twin.Section}」の「{Short(twin.Item)}」とほぼ同じ"));
                }

                seen.Add((section, item, key));

                if (section == "決定事項")
                {
                    string evidence = string.Join(" ", quotes);
                    if (Undecided().IsMatch(withoutQuotes) || Undecided().IsMatch(evidence))
                    {
                        issues.Add(new QualityIssue(QualityReport.UndecidedAsDecision, section, item, "提案・検討中・未定の言い回しが決定事項に入っている"));
                    }

                    if (quotes.Count == 0 || quotes.All(q => !source.Contains(MinutesValidator.Normalize(q), StringComparison.Ordinal)))
                    {
                        issues.Add(new QualityIssue(QualityReport.UnsupportedDecision, section, item, quotes.Count == 0 ? "原文の引用がない" : "引用が原文に見つからない"));
                    }
                }

                if (dictionary is not null)
                {
                    foreach (var entry in dictionary)
                    {
                        // Only outside quotes: a quote copies the transcript, misrecognitions included.
                        foreach (string wrong in entry.Misrecognitions.Where(m => m.Length >= 2 && withoutQuotes.Contains(m, StringComparison.Ordinal)))
                        {
                            issues.Add(new QualityIssue(QualityReport.Misrecognition, section, item, $"誤認識の「{wrong}」が残っている（正: {entry.Term}）"));
                        }
                    }
                }
            }
        }

        return new QualityReport(issues, items);
    }

    /// <summary>The minutes part of a note (without the correction appendix).</summary>
    private static string ExportBody(string markdown)
    {
        int appendix = markdown.IndexOf("\n---\n\n## 校正の記録", StringComparison.Ordinal);
        return appendix >= 0 ? markdown[..appendix] : markdown;
    }

    private static string Short(string text) => text.Length <= 24 ? text : text[..24] + "…";

    /// <summary>Character-bigram (Dice) similarity of two normalized texts, 0–1.</summary>
    public static double Similarity(string a, string b)
    {
        if (a == b)
        {
            return 1;
        }

        if (a.Length < 2 || b.Length < 2)
        {
            return 0;
        }

        var x = Enumerable.Range(0, a.Length - 1).Select(i => a.Substring(i, 2)).ToList();
        var y = Enumerable.Range(0, b.Length - 1).Select(i => b.Substring(i, 2)).ToList();
        var counts = y.GroupBy(s => s).ToDictionary(g => g.Key, g => g.Count());
        int common = 0;
        foreach (string bigram in x)
        {
            if (counts.TryGetValue(bigram, out int n) && n > 0)
            {
                common++;
                counts[bigram] = n - 1;
            }
        }

        return 2.0 * common / (x.Count + y.Count);
    }
}
