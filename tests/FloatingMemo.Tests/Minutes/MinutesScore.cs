using System.Text.RegularExpressions;
using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Scores of one draft against its made-up meeting's expectations (higher is better unless noted).</summary>
public sealed record MinutesScoreCard(
    int Topics, int TopicsTotal,
    int Decisions, int DecisionsTotal,
    int Tasks, int TasksTotal,
    int Numbers, int NumbersTotal,
    int WrongNumbers, int InventedNumbers, int UndecidedAsDecision,
    int Duplicates, int UnsupportedDecisions, int Dummies, int Misrecognitions, int WordsNotInSource,
    int NeedsReview, int Items)
{
    public static string Header =>
        "| 話題 | 決定 | ToDo（担当・期限） | 数値 | 誤った数値 | 原文にない数値 | 未決なのに決定 | 重複 | 根拠のない決定 | ダミー | 誤認識の残り | 原文にない語 | 要確認 | 項目 |";

    public string Row =>
        $"| {Topics}/{TopicsTotal} | {Decisions}/{DecisionsTotal} | {Tasks}/{TasksTotal} | {Numbers}/{NumbersTotal} | {WrongNumbers} | {InventedNumbers} | {UndecidedAsDecision} | "
        + $"{Duplicates} | {UnsupportedDecisions} | {Dummies} | {Misrecognitions} | {WordsNotInSource} | {NeedsReview} | {Items} |";
}

public static partial class MinutesScore
{
    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"〔[^〕]*〕|（原文[:：][^）]*）|\([^)]*原文[^)]*\)|[AB]\d{3,}|F\d{3,}")]
    private static partial Regex References();

    private static string Arabic(string text) => Numerals.Arabic(text);

    public static MinutesScoreCard Score(EvaluationCase meeting, string markdown)
    {
        int appendix = MinutesValidator.AppendixStart(markdown);
        string all = appendix >= 0 ? markdown[..appendix] : markdown;
        int start = all.IndexOf("\n## ", StringComparison.Ordinal);
        string body = Arabic(start >= 0 ? all[start..] : all);
        string bodyNoRefs = References().Replace(body, string.Empty);
        var e = meeting.Expected;

        var decisions = new List<string>();
        var tasks = new List<string>();
        string heading = string.Empty;
        foreach (string line in body.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                heading = line[3..];
                continue;
            }

            if (!MinutesValidator.IsListItem(line) || MinutesValidator.IsNone(line))
            {
                continue;
            }

            string item = References().Replace(line, string.Empty);
            if (heading.StartsWith("決定", StringComparison.Ordinal) || item.Contains("【決定】", StringComparison.Ordinal))
            {
                decisions.Add(item);
            }

            if (heading.StartsWith("ToDo", StringComparison.Ordinal) || item.TrimStart().StartsWith("- [", StringComparison.Ordinal))
            {
                tasks.Add(item);
            }
        }

        bool Has(string text, string word) => text.Contains(Arabic(word), StringComparison.Ordinal);
        int topics = e.Topics.Count(alts => alts.Any(a => Has(bodyNoRefs, a)));
        int decisionHits = e.Decisions.Count(words => decisions.Any(d => words.All(w => Has(d, w))));
        int taskHits = e.Tasks.Count(t => tasks.Any(l => Has(l, t.What) && Has(l, t.Who) && (t.Due.Length == 0 || Has(l, t.Due))));
        int numbers = e.Numbers.Count(n => Has(bodyNoRefs, n));
        int wrong = e.WrongNumbers.Count(n => Has(bodyNoRefs, n));
        int undecided = e.NotDecided.Count(w => decisions.Any(d => Has(d, w)));

        // Numbers in the minutes that the meeting never mentioned (notes or transcript).
        var said = Digits().Matches(Arabic(meeting.Note)).Select(m => m.Value).ToHashSet();
        // The app's own review notes (「⚠要確認（…内容の一部…）」) are not statements about the meeting.
        string withoutDates = Regex.Replace(bodyNoRefs, @"作成: [0-9\-: ]+|20[0-9]{2}-[0-9]{2}-[0-9]{2}|⚠[^\n]*", string.Empty);
        int invented = Digits().Matches(withoutDates).Select(m => m.Value).Distinct().Count(n => !said.Contains(n));

        var quality = MinutesQuality.Analyze(markdown, meeting.Note, meeting.Dictionary);
        int notInSource = all.Split('\n').Count(l => l.Contains("原文にない語", StringComparison.Ordinal));
        int review = all.Split('\n').Count(l => l.Contains('⚠'));

        return new MinutesScoreCard(topics, e.Topics.Length, decisionHits, e.Decisions.Length, taskHits, e.Tasks.Length,
            numbers, e.Numbers.Length, wrong, invented, undecided,
            quality.Count(QualityReport.Duplicate), quality.Count(QualityReport.UnsupportedDecision), quality.Count(QualityReport.Dummy),
            quality.Count(QualityReport.Misrecognition), notInSource, review, quality.Items);
    }
}
