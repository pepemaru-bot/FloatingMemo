using System.Text;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Dictionary;

namespace FloatingMemo.Services.Minutes;

/// <summary>One change the AI proposes: replace <see cref="From"/> (as written in the transcript) with <see cref="To"/>.</summary>
public sealed record CorrectionProposal(string From, string To, string Reason, bool Confident);

public enum CorrectionStatus
{
    /// <summary>Applied to the corrected transcript.</summary>
    Applied,

    /// <summary>Not applied; the original is kept and marked 〔要確認: …〕 for the user.</summary>
    NeedsReview,

    /// <summary>Discarded (the proposal did not match the transcript).</summary>
    Rejected,
}

public sealed record CorrectionDecision(CorrectionProposal Proposal, CorrectionStatus Status, string Why, TermEntry? Term);

/// <summary>
/// The transcript after correction, in two forms: <see cref="Clean"/> (only safe corrections,
/// fed to the minutes model) and <see cref="Annotated"/> (also marks what needs a human look).
/// </summary>
public sealed record CorrectedTranscript(string Original, string Clean, string Annotated, IReadOnlyList<CorrectionDecision> Decisions)
{
    public int Applied => Decisions.Count(d => d.Status == CorrectionStatus.Applied);

    public int NeedsReview => Decisions.Count(d => d.Status == CorrectionStatus.NeedsReview);
}

/// <summary>
/// Step 1 of the minutes pipeline: fix misrecognized words in the Whisper transcript using the
/// user's term dictionary and the context — without rewriting anything else.
/// </summary>
/// <remarks>
/// The model never rewrites the text. It only proposes "原文「X」→ 修正「Y」" pairs, and every
/// pair is checked here: X must be in the transcript, numbers may not change, and a change is
/// applied automatically only when the dictionary backs it (Y is a registered term related to
/// this transcript). Anything else stays as the original with a 〔要確認〕 mark.
/// </remarks>
public sealed partial class TranscriptCorrector(ILocalTextGenerator model)
{
    public const int MaxAnswerTokens = 700;
    public const string ReviewOpen = "〔要確認: ";

    [GeneratedRegex(@"原文\s*[「『](?<from>[^」』\n]{1,40})[」』]\s*(?:→|->|⇒)\s*修正\s*[「『](?<to>[^」』\n]{1,40})[」』](?<rest>[^\n]*)")]
    private static partial Regex ProposalLine();

    [GeneratedRegex(@"[0-9０-９〇零一二三四五六七八九十百千万億]+")]
    private static partial Regex Numbers();

    public static string System { get; } = """
        あなたは日本語の音声認識（文字起こし）の誤りを見つける校正者です。
        文字起こしを全体の文脈・前後の文章・読みや発音の近さ・用語リストと照らし合わせ、音声認識の聞き間違いと思われる語だけを指摘してください。
        規則:
        1. 文章を書き直さない。要約しない。言い回し・敬語・文体は直さない。
        2. 修正は「聞き間違いの語を正しい語に置き換える」ものだけ。発言・数字・人名・担当者・決定事項を新しく作らない。
        3. 「原文」には文字起こしにそのまま書かれている文字列を写す（1〜20文字程度）。
        4. 用語リストにある語は、読みや発音が近く文脈に合う場合だけ候補にする。似ているだけで文脈に合わなければ指摘しない。
        5. 確信が持てないものは「確信: 低」とする。
        6. 誤りがなければ「なし」とだけ書く。
        出力形式（1行に1件）:
        - 原文「…」→ 修正「…」（理由: …／確信: 高）
        """;

    public static string User(string transcript, IReadOnlyList<TermMatcher.Match> related)
    {
        var sb = new StringBuilder();
        if (related.Count > 0)
        {
            sb.Append("用語リスト（この文字起こしに関係しそうな語。正しい表記／読み／よくある誤認識）:\n");
            foreach (var m in related)
            {
                var e = m.Entry;
                sb.Append("- ").Append(e.Term);
                if (!string.IsNullOrEmpty(e.Reading))
                {
                    sb.Append("（").Append(e.Reading).Append("）");
                }

                if (e.Aliases.Count > 0)
                {
                    sb.Append(" 略称: ").Append(string.Join("、", e.Aliases));
                }

                if (e.Misrecognitions.Count > 0)
                {
                    sb.Append(" 誤認識例: ").Append(string.Join("、", e.Misrecognitions));
                }

                sb.Append('\n');
            }
        }

        sb.Append("文字起こし:\n<文字起こし>\n").Append(transcript.Trim()).Append("\n</文字起こし>\n/no_think");
        return sb.ToString();
    }

    /// <summary>Asks the model for proposals (in parts for long transcripts) and applies the safe ones.</summary>
    public async Task<CorrectedTranscript> CorrectAsync(string transcript, IReadOnlyList<TermEntry> dictionary,
        CancellationToken cancellationToken = default, IProgress<MinutesProgress>? progress = null)
    {
        var proposals = new List<CorrectionProposal>();
        var parts = MinutesPrompt.Split(transcript, MinutesGenerator.ChunkCharacters);
        for (int i = 0; i < parts.Count; i++)
        {
            int index = i;
            progress?.Report(new MinutesProgress("文字起こしを校正", index + 1, parts.Count, 0, false));
            var generated = progress is null ? null : new SynchronousProgress<int>(n =>
                progress.Report(new MinutesProgress("文字起こしを校正", index + 1, parts.Count, Math.Max(0, n), n < 0)));
            var related = TermMatcher.Related(dictionary, parts[i]);
            string answer = await model.CompleteAsync(System, User(parts[i], related), MaxAnswerTokens, cancellationToken, generated).ConfigureAwait(false);
            proposals.AddRange(Parse(answer));
        }

        return Apply(transcript, proposals, TermMatcher.Related(dictionary, transcript, max: 200).Select(m => m.Entry).ToList());
    }

    public static IReadOnlyList<CorrectionProposal> Parse(string answer)
    {
        var list = new List<CorrectionProposal>();
        foreach (Match m in ProposalLine().Matches(answer))
        {
            string rest = m.Groups["rest"].Value;
            string reason = Regex.Match(rest, @"理由\s*[:：]\s*(?<r>[^／/）)]*)").Groups["r"].Value.Trim();
            bool confident = !Regex.IsMatch(rest, @"確信\s*[:：]\s*(低|ひくい|中)");
            list.Add(new CorrectionProposal(m.Groups["from"].Value.Trim(), m.Groups["to"].Value.Trim(), reason, confident));
        }

        return list;
    }

    /// <summary>Checks each proposal against the transcript and the dictionary; never invents text.</summary>
    public static CorrectedTranscript Apply(string transcript, IReadOnlyList<CorrectionProposal> proposals, IReadOnlyList<TermEntry> related)
    {
        var decisions = new List<CorrectionDecision>();
        string clean = transcript;
        var reviews = new List<(string From, string To)>();
        var seen = new HashSet<string>();
        foreach (var p in proposals)
        {
            if (!seen.Add(p.From + "→" + p.To))
            {
                continue;
            }

            if (p.From.Length == 0 || p.To.Length == 0 || p.From == p.To)
            {
                continue;
            }

            if (!transcript.Contains(p.From, StringComparison.Ordinal))
            {
                decisions.Add(new CorrectionDecision(p, CorrectionStatus.Rejected, "原文にその文字列がない", null));
                continue;
            }

            if (Numbers().Matches(p.From).Select(m => m.Value).SequenceEqual(Numbers().Matches(p.To).Select(m => m.Value)) is false)
            {
                decisions.Add(new CorrectionDecision(p, CorrectionStatus.NeedsReview, "数字が変わる修正は自動で適用しない", null));
                reviews.Add((p.From, p.To));
                continue;
            }

            var term = Supporting(p, related);
            if (term is not null && p.Confident)
            {
                clean = clean.Replace(p.From, p.To, StringComparison.Ordinal);
                decisions.Add(new CorrectionDecision(p, CorrectionStatus.Applied, $"辞書の「{term.Term}」（{term.Category}）と一致", term));
            }
            else
            {
                string why = term is null ? "辞書で裏付けられない（人名・言い換えの可能性）" : "AIの確信が低い";
                decisions.Add(new CorrectionDecision(p, CorrectionStatus.NeedsReview, why, term));
                reviews.Add((p.From, p.To));
            }
        }

        // Marks go after the first occurrence of each reviewed word; the word itself stays.
        string annotated = clean;
        foreach (var (from, to) in reviews)
        {
            int at = annotated.IndexOf(from, StringComparison.Ordinal);
            if (at >= 0)
            {
                annotated = annotated.Insert(at + from.Length, $"{ReviewOpen}{to}？〕");
            }
        }

        return new CorrectedTranscript(transcript, clean, annotated, decisions);
    }

    /// <summary>
    /// The dictionary entry that backs a change: the new word is the entry's term (or an alias),
    /// and the old word is one of its known misrecognitions or resembles one of its spellings.
    /// </summary>
    private static TermEntry? Supporting(CorrectionProposal p, IReadOnlyList<TermEntry> related)
    {
        foreach (var e in related)
        {
            bool target = p.To == e.Term || e.Aliases.Contains(p.To) || p.To.Contains(e.Term, StringComparison.Ordinal);
            if (!target)
            {
                continue;
            }

            string from = TermMatcher.Normalize(p.From);
            if (e.Misrecognitions.Any(m => from.Contains(TermMatcher.Normalize(m), StringComparison.Ordinal))
                || (e.Reading is { Length: > 0 } reading && from.Contains(TermMatcher.Normalize(reading), StringComparison.Ordinal))
                || e.AllForms.Any(f => Similar(from, TermMatcher.Normalize(f))))
            {
                return e;
            }
        }

        return null;
    }

    /// <summary>
    /// Spellings that differ by a few characters (a dropped, added or swapped character): the
    /// longest common subsequence covers at least two thirds of the longer one.
    /// </summary>
    private static bool Similar(string a, string b)
    {
        if (a.Length < 2 || b.Length < 2 || Math.Abs(a.Length - b.Length) > 2 || Math.Max(a.Length, b.Length) > 40)
        {
            return false;
        }

        var row = new int[b.Length + 1];
        for (int i = 1; i <= a.Length; i++)
        {
            int diagonal = 0;
            for (int j = 1; j <= b.Length; j++)
            {
                int above = row[j];
                row[j] = a[i - 1] == b[j - 1] ? diagonal + 1 : Math.Max(row[j], row[j - 1]);
                diagonal = above;
            }
        }

        return row[b.Length] >= Math.Max(a.Length, b.Length) * 2 / 3.0;
    }
}
