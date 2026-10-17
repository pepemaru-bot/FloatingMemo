using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Minutes;

/// <summary>How well the cited source supports a fact.</summary>
public enum Support
{
    /// <summary>The cited words carry the fact: its content words, numbers, names, negation and conditions.</summary>
    Supported,

    /// <summary>Only part of the fact is in the cited words.</summary>
    Partial,

    /// <summary>The cited words do not say this.</summary>
    Unsupported,
}

/// <summary>The result of checking a fact against its cited source (what is wrong, in plain words).</summary>
public sealed record EvidenceCheck(Support Level, double Coverage, IReadOnlyList<string> Problems);

/// <summary>
/// Checks whether cited source text supports a fact. Mechanical and conservative: it never
/// replaces a citation; it says when the citation does not carry the fact, so the item is shown
/// as 要確認 (or re-extracted). "The number exists" and "the sentence says this" are separate
/// questions: existence is checked by the caller, support here.
/// </summary>
/// <remarks>
/// Support is judged on content, not on general similarity, so a similar sentence about
/// something else, by someone else, or with the opposite meaning does not count:
/// - coverage: the share of the fact's content bigrams (kanji / katakana / latin / digits;
///   particles and endings ignored) found in the cited text;
/// - every number, katakana or latin name in the fact must be in the cited text;
/// - negation must agree (a fact without a negative cited from a negative sentence, or the
///   reverse, is a contradiction);
/// - a condition in the cited text ("〜の場合", "〜なら") must not be dropped by the fact.
/// </remarks>
public static partial class FactEvidence
{
    public const double SupportedCoverage = 0.6;
    public const double PartialCoverage = 0.35;

    [GeneratedRegex(@"[\p{IsCJKUnifiedIdeographs}々〆ヵヶ\p{IsKatakana}ーA-Za-z0-9]{2,}")]
    private static partial Regex ContentRun();

    [GeneratedRegex(@"[\p{IsKatakana}ー]{3,}|[A-Za-z][A-Za-z0-9]{2,}")]
    private static partial Regex Name();

    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Digits();

    // People named with an honorific ("小川さん"): who did or said something must be in the source.
    [GeneratedRegex(@"(?<name>[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}ー]{1,6}?)(さん|様|氏|くん|君|部長|課長|先生)")]
    private static partial Regex Person();

    [GeneratedRegex("ない|ません|不要|未定|まだ決まって|しない|できない|やめ|中止|見送")]
    private static partial Regex Negation();

    [GeneratedRegex("場合|なら(?!ない)|たら|れば|次第|条件|ただし|もし|限り|以外")]
    private static partial Regex Condition();

    /// <summary>Words that say nothing about the content (they appear in many facts).</summary>
    private static readonly HashSet<string> Generic = ["こと", "これ", "それ", "会議", "話題", "内容", "決定", "決ま", "予定", "について"];

    /// <param name="topic">Words of the fact's topic name are not required in the cited sentence (they come from the context).</param>
    /// <param name="wholeSource">The whole meeting: a common word (katakana / latin) said elsewhere is context, not invented.
    /// People named with an honorific must still be in the cited sentence.</param>
    public static EvidenceCheck Check(string factText, string citedText, string kind = FactKind.Info, string topic = "", string wholeSource = "")
    {
        var problems = new List<string>();
        string fact = Numerals.Arabic(MinutesValidator.Normalize(factText));
        string cited = Numerals.Arabic(MinutesValidator.Normalize(citedText));
        if (cited.Length == 0)
        {
            return new EvidenceCheck(Support.Unsupported, 0, ["根拠の原文がない"]);
        }

        string topicNorm = Numerals.Arabic(MinutesValidator.Normalize(topic));
        string whole = Numerals.Arabic(MinutesValidator.Normalize(wholeSource));
        var bigrams = ContentRun().Matches(fact).Select(m => m.Value)
            .SelectMany(run => Enumerable.Range(0, run.Length - 1).Select(i => run.Substring(i, 2)))
            .Where(b => !Generic.Contains(b) && !topicNorm.Contains(b, StringComparison.Ordinal)).Distinct().ToList();
        double coverage = bigrams.Count == 0 ? 1 : bigrams.Count(b => cited.Contains(b, StringComparison.Ordinal)) / (double)bigrams.Count;

        var missingNumbers = Digits().Matches(fact).Select(m => m.Value).Distinct()
            .Where(n => !Digits().Matches(cited).Any(c => c.Value == n)).ToList();
        if (missingNumbers.Count > 0)
        {
            problems.Add($"数字「{string.Join("、", missingNumbers)}」が根拠の原文にない");
        }

        var missingNames = Name().Matches(fact).Select(m => m.Value)
            .Where(n => !topicNorm.Contains(n, StringComparison.Ordinal) && !(whole.Length > 0 && whole.Contains(n, StringComparison.Ordinal)))
            .Concat(Person().Matches(MinutesValidator.Normalize(factText)).Select(m => m.Groups["name"].Value))
            .Distinct().Where(n => !cited.Contains(n, StringComparison.Ordinal)).ToList();
        if (missingNames.Count > 0)
        {
            problems.Add($"「{string.Join("」「", missingNames)}」が根拠の原文にない");
        }

        // Undecided / open questions are allowed to be phrased negatively in both directions.
        bool factNegative = Negation().IsMatch(factText);
        bool citedNegative = Negation().IsMatch(citedText);
        if (kind is not FactKind.Question and not FactKind.Open && factNegative != citedNegative && coverage >= PartialCoverage)
        {
            problems.Add(citedNegative ? "根拠は否定・未定の発言だが、内容は肯定になっている" : "内容は否定だが、根拠は否定していない");
        }

        if (Condition().Match(citedText) is { Success: true } condition && !Condition().IsMatch(factText) && coverage >= PartialCoverage)
        {
            problems.Add($"根拠にある条件（「{condition.Value}」）が内容から抜けている");
        }

        var level = coverage >= SupportedCoverage && missingNumbers.Count == 0 && missingNames.Count == 0
            ? Support.Supported
            : coverage >= PartialCoverage ? Support.Partial : Support.Unsupported;
        if (level == Support.Partial)
        {
            problems.Add("根拠の原文は内容の一部しか示していない");
        }
        else if (level == Support.Unsupported)
        {
            problems.Add("根拠の原文は内容を示していない");
        }

        return new EvidenceCheck(level, coverage, problems);
    }

    /// <summary>
    /// The one unit of the part that would fully support the fact, if there is exactly one —
    /// offered to the reader as a suggestion ("候補"), never applied automatically.
    /// </summary>
    public static string? Candidate(string factText, string kind, IEnumerable<SourceUnit> part)
    {
        var full = part.Where(u => !u.IsNoise)
            .Select(u => (u.Id, Check: Check(factText, u.Text, kind)))
            .Where(x => x.Check.Level == Support.Supported && x.Check.Problems.Count == 0 && x.Check.Coverage >= 0.8)
            .ToList();
        return full.Count == 1 ? full[0].Id : null;
    }
}
