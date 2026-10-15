using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Dictionary;

namespace FloatingMemo.Services.Minutes;

/// <summary>A local text generator (a small LLM running on this PC). Never a cloud service.</summary>
public interface ILocalTextGenerator
{
    /// <summary>Shown in the minutes so the reader knows which model wrote the draft.</summary>
    string ModelName { get; }

    /// <param name="progress">
    /// Characters generated so far; -1 while the model file is being loaded. There is no
    /// percentage: how long an answer will be is not known in advance.
    /// </param>
    Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null);

    /// <summary>Why the last answer ended (see <see cref="MinutesFinish"/>); for diagnostics.</summary>
    string LastFinish => MinutesFinish.Unknown;
}

/// <summary>Where the minutes pipeline is (shown as text: step, part, characters; never a guessed %).</summary>
public sealed record MinutesProgress(string Step, int Part, int Parts, int Characters, bool LoadingModel);

/// <summary>The draft and what the checks changed or flagged.</summary>
public sealed record MinutesDraft(string Markdown, IReadOnlyList<string> Warnings)
{
    /// <summary>The correction step's result (null when correction was skipped).</summary>
    public CorrectedTranscript? Correction { get; init; }

    /// <summary>How the draft was made (sizes, parts, calls): for the diagnostics log.</summary>
    public MinutesRun? Run { get; init; }

    /// <summary>The checked facts the draft was written from (fact-first layout only).</summary>
    public IReadOnlyList<Fact>? Facts { get; init; }
}

/// <summary>
/// Turns a transcript into a minutes draft with a local model, then checks the draft against
/// the transcript (see <see cref="MinutesValidator"/>). The transcript itself is never changed.
/// </summary>
/// <param name="template">A user template: its headings and rules replace the layout's own (notes passed apart, like Topics).</param>
public sealed partial class MinutesGenerator(ILocalTextGenerator model, MinutesStyle style = MinutesStyle.FixedSections, MinutesTemplate? template = null)
{
    private readonly MinutesStyle _style = template is null ? style : MinutesStyle.Topics;

    /// <summary>Longer transcripts are processed in parts (small models lose track of long input).</summary>
    public const int ChunkCharacters = 6000;

    /// <summary>
    /// Whisper transcript → (1) correction with the dictionary and context → corrected transcript
    /// → (2) minutes. The two steps use separate prompts. <paramref name="dictionary"/> null =
    /// skip step 1 and summarize the transcript as it is.
    /// </summary>
    public async Task<MinutesDraft> GenerateAsync(string transcript, string sourceTitle, DateTime now,
        IProgress<MinutesProgress>? progress = null, CancellationToken cancellationToken = default,
        IReadOnlyList<TermEntry>? dictionary = null, string noteId = "")
    {
        var run = new MinutesRun
        {
            Input = MinutesInputSnapshot.Of(noteId, transcript),
            Model = model.ModelName,
            Style = _style,
            Settings = $"chunk={ChunkCharacters} correct={(dictionary is null ? "off" : "on")} template={(template is null ? "none" : template.Id)}",
        };
        var counted = new CountingGenerator(model, run);

        CorrectedTranscript? correction = null;
        string input = transcript;
        if (dictionary is not null)
        {
            correction = await new TranscriptCorrector(counted).CorrectAsync(transcript, dictionary, cancellationToken, progress).ConfigureAwait(false);
            input = correction.Clean;
        }

        if (_style == MinutesStyle.Facts)
        {
            return await GenerateFromFactsAsync(transcript, input, correction, sourceTitle, now, run, counted, progress, cancellationToken).ConfigureAwait(false);
        }

        // Topic style: the person's own notes are passed apart from the transcript (as context).
        // Very long notes are not (they would be repeated with every part): the whole note is
        // then summarized as one text. Nothing is cut either way.
        string notes = string.Empty;
        run.InputMode = "whole-note";
        if (_style == MinutesStyle.Topics && NoteSections.Split(input) is { Transcripts.Count: > 0 } note)
        {
            if (note.HumanNotes.Length <= TopicPrompt.MaxSeparateNotesCharacters)
            {
                notes = note.HumanNotes;
                input = note.AllTranscripts;
                run.InputMode = "notes-apart";
            }
            else
            {
                run.InputMode = "whole-note(long-notes)";
            }
        }

        var chunks = MinutesPrompt.Split(input, ChunkCharacters);
        run.RecordParts(input.Replace("\r\n", "\n").Trim(), chunks);
        var parts = new List<string>();
        for (int i = 0; i < chunks.Count; i++)
        {
            int part = i;
            progress?.Report(new MinutesProgress("議事録を作成", part + 1, chunks.Count, 0, false));
            var generated = progress is null ? null : new SynchronousProgress<int>(n =>
                progress.Report(new MinutesProgress("議事録を作成", part + 1, chunks.Count, Math.Max(0, n), n < 0)));
            string answer = template is not null
                ? await counted.CompleteAsync(TemplatePrompt.System(template), TopicPrompt.User(notes, chunks[i], i, chunks.Count),
                    TemplatePrompt.MaxAnswerTokens, cancellationToken, generated).ConfigureAwait(false)
                : _style == MinutesStyle.Topics
                ? await counted.CompleteAsync(TopicPrompt.System, TopicPrompt.User(notes, chunks[i], i, chunks.Count),
                    TopicPrompt.MaxAnswerTokens, cancellationToken, generated).ConfigureAwait(false)
                : await counted.CompleteAsync(MinutesPrompt.System, MinutesPrompt.User(chunks[i], i, chunks.Count),
                    MinutesPrompt.MaxAnswerTokens, cancellationToken, generated).ConfigureAwait(false);
            parts.Add(answer);
        }

        // Parts are merged mechanically (no second model pass that could invent connections).
        var warnings = new List<string>();
        if (run.Unprocessed.Count > 0)
        {
            warnings.Add($"原文の一部（{run.Unprocessed.Count} か所）がAIに渡されていません。原文を確認してください。");
        }

        if (run.Truncated)
        {
            warnings.Add("AIの回答が長さの上限で途中で終わった部分があります。議事録に載っていない内容があるかもしれません。");
        }

        // Quotes may come from the corrected text or the original; both count as "said".
        string source = correction is null ? transcript : transcript + "\n" + correction.Clean;
        // Topic style: only what was said backs quotes, assignees and deadlines; the notes back
        // only the items marked as taken from them.
        string said = NoteSections.Split(transcript) is { Transcripts.Count: > 0 } original
            ? original.AllTranscripts + "\n" + input
            : source;
        string body = template is not null
            ? TemplateMinutes.Check(template, parts, said, notes + "\n" + said, warnings)
            : _style == MinutesStyle.Topics
            ? TopicMinutes.Check(TopicMinutes.Merge(parts), said, notes + "\n" + said, warnings)
            : MinutesValidator.Check(MinutesValidator.Merge(parts), source, warnings);
        if (correction is not null)
        {
            warnings.AddRange(correction.Decisions.Where(d => d.Status == CorrectionStatus.NeedsReview)
                .Select(d => $"校正: 「{d.Proposal.From}」→「{d.Proposal.To}」は要確認（{d.Why}）"));
            body += MinutesValidator.CorrectionAppendix(correction);
        }

        return new MinutesDraft(MinutesValidator.Document(body, sourceTitle, model.ModelName, now, chunks.Count), warnings) { Correction = correction, Run = run };
    }
}

/// <summary>The fact-first pipeline (<see cref="MinutesStyle.Facts"/>).</summary>
public sealed partial class MinutesGenerator
{
    private static async Task<MinutesDraft> GenerateFromFactsAsync(string original, string input, CorrectedTranscript? correction,
        string sourceTitle, DateTime now, MinutesRun run, ILocalTextGenerator model, IProgress<MinutesProgress>? progress, CancellationToken cancellationToken)
    {
        // A note without a transcript block is all notes: its facts are marked as taken from them.
        var units = SourceUnits.From(NoteSections.Split(input));
        var byId = units.ToDictionary(u => u.Id);
        var parts = FactMinutes.Group(units);
        run.InputMode = "facts";
        for (int i = 0; i < parts.Count; i++)
        {
            run.Ranges.Add((units.IndexOf(parts[i][0]), units.IndexOf(parts[i][^1]) + 1));
        }

        if (parts.Sum(p => p.Count) != units.Count)
        {
            run.Unprocessed.Add((0, units.Count)); // cannot happen: every unit is in exactly one part
        }

        var facts = new List<Fact>();
        int malformed = 0;
        for (int i = 0; i < parts.Count; i++)
        {
            int part = i;
            progress?.Report(new MinutesProgress("議事録を作成", part + 1, parts.Count, 0, false));
            var generated = progress is null ? null : new SynchronousProgress<int>(n =>
                progress.Report(new MinutesProgress("議事録を作成", part + 1, parts.Count, Math.Max(0, n), n < 0)));
            string answer = await model.CompleteAsync(FactPrompt.System, FactPrompt.User(FactMinutes.ContextBefore(parts, i), parts[i], i, parts.Count),
                FactPrompt.MaxAnswerTokens, cancellationToken, generated).ConfigureAwait(false);
            var allowed = parts[i].Select(u => u.Id).ToHashSet();
            facts.AddRange(FactMinutes.Parse(answer, out int bad).Select(f => FactMinutes.Check(f, byId, allowed)));
            malformed += bad;
        }

        var warnings = new List<string>();
        if (malformed > 0)
        {
            warnings.Add($"AIの回答のうち {malformed} 行を読み取れませんでした。");
        }

        if (run.Truncated)
        {
            warnings.Add("AIの回答が長さの上限で途中で終わった部分があります。議事録に載っていない内容があるかもしれません。");
        }

        var topics = FactMinutes.Merge(facts);
        var uncited = FactMinutes.Uncited(units, facts);
        if (uncited.Count > 0)
        {
            warnings.Add($"議事録のどの項目にも使われていない発言があります（{string.Join("、", uncited)}）。");
        }

        string body = FactMinutes.Render(topics, byId, [], warnings) + FactMinutes.SourceAppendix(units, uncited);
        if (correction is not null)
        {
            body += MinutesValidator.CorrectionAppendix(correction);
        }

        return new MinutesDraft(MinutesValidator.Document(body, sourceTitle, model.ModelName, now, parts.Count), warnings)
        {
            Correction = correction,
            Run = run,
            Facts = facts,
        };
    }
}

/// <summary>Passes calls to the model and records each one (sizes, finish reason, time) in the run.</summary>
internal sealed class CountingGenerator(ILocalTextGenerator inner, MinutesRun run) : ILocalTextGenerator
{
    private readonly Dictionary<string, int> _parts = [];

    public string ModelName => inner.ModelName;

    public string LastFinish => inner.LastFinish;

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        string step = system == TranscriptCorrector.System ? "correct" : "minutes";
        int part = _parts[step] = _parts.GetValueOrDefault(step) + 1;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string answer = await inner.CompleteAsync(system, user, maxTokens, cancellationToken, progress).ConfigureAwait(false);
        run.Calls.Add(new MinutesCall(step, part, user.Length, answer.Length, inner.LastFinish, watch.Elapsed));
        return answer;
    }
}

/// <summary>Forwards reports immediately (the outer IProgress decides which thread shows them).</summary>
public sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}

/// <summary>Instructions for the model (Japanese, because the meetings are Japanese).</summary>
public static class MinutesPrompt
{
    public const int MaxAnswerTokens = 1200;

    public static readonly string[] Sections =
    [
        "会議の概要",
        "主な議題",
        "議論の要点",
        "決定事項",
        "ToDo（担当者・期限）",
        "未決事項",
        "次回確認事項",
    ];

    public static string System { get; } = $"""
        あなたは会議の文字起こしから議事録の下書きを作る書記です。次の規則を必ず守ってください。
        1. 文字起こしに書かれていることだけを使う。推測・補完・一般論・文字起こしにない固有名詞は書かない。
           項目は文字起こしの言葉をできるだけそのまま使って短く書く。言い換えたり、新しい言葉を作ったりしない。
        2. 担当者と期限は、文字起こしで明言されている場合だけ書く。明言がなければ「未確認」と書く。
        3. 提案・意見（「〜しましょうか」「〜はどうか」「〜したい」）は決定事項に入れない。担当者や期限がある作業は、決定事項に書いた場合も ToDo にも書く。決定（「〜に決まりました」「〜することになりました」「〜で合意」）だけを決定事項にする。決まっていないものは未決事項に入れる。
        4. 各項目の末尾に、根拠となる文字起こしの短い引用を（原文:「…」）の形で付ける。引用は文字起こしからそのまま写す。
        5. 該当する内容がない見出しには「なし」とだけ書く。
        6. 次の見出しをこの順番で使い、Markdown の箇条書きで簡潔に書く。前置きや結びの文は書かない。
        {string.Join("\n", Sections.Select(s => "## " + s))}
        ToDo は「- [ ] 内容 — 担当: 名前または未確認 / 期限: 期限または未確認（原文:「…」）」の形で書く。
        """;

    public static string User(string transcript, int part, int parts) =>
        (parts > 1 ? $"これは文字起こし全体の {part + 1}/{parts} 番目の部分です。この部分だけについて書いてください。\n" : string.Empty)
        + "以下が文字起こしです。\n<文字起こし>\n" + transcript.Trim() + "\n</文字起こし>\n/no_think";

    /// <summary>Splits at paragraph or sentence ends, never in the middle of a sentence.</summary>
    public static IReadOnlyList<string> Split(string transcript, int maxCharacters)
    {
        string text = transcript.Replace("\r\n", "\n").Trim();
        if (text.Length <= maxCharacters)
        {
            return [text];
        }

        var sentences = Regex.Split(text, @"(?<=[。！？!?\n])");
        var chunks = new List<string>();
        var current = new StringBuilder();
        foreach (string sentence in sentences)
        {
            if (current.Length > 0 && current.Length + sentence.Length > maxCharacters)
            {
                chunks.Add(current.ToString().Trim());
                current.Clear();
            }

            current.Append(sentence);
        }

        if (current.Length > 0)
        {
            chunks.Add(current.ToString().Trim());
        }

        return chunks.Where(c => c.Length > 0).ToList();
    }
}

/// <summary>
/// Mechanical checks of the model's answer against the transcript. They cannot prove the
/// draft right, but they remove the most common inventions and mark what needs a human look.
/// </summary>
public static partial class MinutesValidator
{
    public const string Unconfirmed = "未確認";
    public const string QuoteMissingMark = " ⚠要確認（引用が原文に見つかりません）";
    public const string NoQuoteMark = " ⚠要確認（根拠の引用なし）";

    [GeneratedRegex(@"[\p{IsKatakana}ー]{3,}|[A-Za-zＡ-Ｚａ-ｚ]{3,}")]
    private static partial Regex ForeignWord();

    /// <summary>Words the minutes format itself uses (never "invented" by the model).</summary>
    private static readonly string[] OwnWords = ["未確認", "要確認", "原文", "担当者", "期限", "議事録", "下書", "推測", "除去"];

    [GeneratedRegex(@"\p{IsCJKUnifiedIdeographs}{3,}")]
    private static partial Regex KanjiCompound();

    [GeneratedRegex(@"^(\s*)[-*]\s+[-*]\s+")]
    private static partial Regex DoubleBullet();

    [GeneratedRegex(@"<think>.*?</think>", RegexOptions.Singleline)]
    private static partial Regex ThinkBlock();

    // "（原文:「…」）"; small models sometimes drop "原文:" and write just "(「…」)".
    [GeneratedRegex(@"[（(]\s*(?:原文\s*[:：]\s*)?「(?<q>[^」]*)」\s*[）)]")]
    private static partial Regex Quote();

    [GeneratedRegex(@"(?<label>担当|期限)\s*[:：]\s*(?<value>[^/／（(\n]+)")]
    private static partial Regex Field();

    [GeneratedRegex(@"^\s*#{1,6}\s*(?<title>.+?)\s*$")]
    private static partial Regex Heading();

    /// <summary>Section title → lines, in the fixed order (unknown headings are dropped into 議論の要点).</summary>
    public static Dictionary<string, List<string>> Parse(string answer)
    {
        var sections = MinutesPrompt.Sections.ToDictionary(s => s, _ => new List<string>());
        string? current = null;
        foreach (string raw in ThinkBlock().Replace(answer, string.Empty).Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (Heading().Match(line) is { Success: true } heading)
            {
                current = Match(heading.Groups["title"].Value);
                continue;
            }

            if (current is null || line.Trim().Length == 0)
            {
                continue; // preamble before the first heading is not part of the minutes
            }

            // An indented line that is not a new item continues the previous one
            // (models sometimes put the quote on its own line).
            var lines = sections[current];
            if (lines.Count > 0 && char.IsWhiteSpace(line[0]) && !IsListItem(line))
            {
                lines[^1] = lines[^1] + " " + line.Trim();
                continue;
            }

            lines.Add(line);
        }

        return sections;
    }

    private static string Match(string title)
    {
        string t = title.Replace(" ", string.Empty);
        foreach (string s in MinutesPrompt.Sections)
        {
            string key = s.Split('（')[0];
            if (t.Contains(key, StringComparison.OrdinalIgnoreCase))
            {
                return s;
            }
        }

        return t.Contains("ToDo", StringComparison.OrdinalIgnoreCase) || t.Contains("タスク") ? MinutesPrompt.Sections[4] : MinutesPrompt.Sections[2];
    }

    /// <summary>Combines the answers for each part of a long transcript, section by section.</summary>
    public static Dictionary<string, List<string>> Merge(IEnumerable<string> answers)
    {
        var merged = MinutesPrompt.Sections.ToDictionary(s => s, _ => new List<string>());
        foreach (string answer in answers)
        {
            foreach (var (section, lines) in Parse(answer))
            {
                merged[section].AddRange(lines.Where(l => !IsNone(l)));
            }
        }

        return merged;
    }

    /// <summary>Checks every item against the transcript and renders the sections as Markdown.</summary>
    public static string Check(Dictionary<string, List<string>> sections, string transcript, List<string> warnings)
    {
        string source = Normalize(transcript);
        var sb = new StringBuilder();
        foreach (string section in MinutesPrompt.Sections)
        {
            sb.Append("## ").Append(section).Append('\n');
            var lines = sections.TryGetValue(section, out var l) ? l : [];
            if (lines.Count == 0)
            {
                sb.Append("- なし\n\n");
                continue;
            }

            foreach (string line in lines)
            {
                sb.Append(CheckLine(line, section, source, warnings)).Append('\n');
            }

            sb.Append('\n');
        }

        return sb.ToString().TrimEnd() + "\n";
    }

    internal static string CheckLine(string line, string section, string source, List<string> warnings)
    {
        string result = DoubleBullet().Replace(line, "$1- "); // "- - [ ]" -> "- [ ]"

        // Assignee / deadline that the transcript never mentions: the model guessed it.
        if (section == MinutesPrompt.Sections[4])
        {
            result = Field().Replace(result, m =>
            {
                string value = m.Groups["value"].Value.Trim().TrimEnd('—', '-', ' ');
                if (value.Length == 0 || value == Unconfirmed || source.Contains(Normalize(value), StringComparison.Ordinal))
                {
                    return m.Value;
                }

                warnings.Add($"{m.Groups["label"].Value}「{value}」は原文にないため「{Unconfirmed}」にしました。");
                return $"{m.Groups["label"].Value}: {Unconfirmed}（AIの推測を除去） ";
            });
        }

        // Katakana / Latin words the transcript never contains are typical misspellings or
        // inventions of small models ("インフォームフォーム" for 「問い合わせフォーム」).
        string outsideQuotes = Quote().Replace(result, string.Empty);
        var unknown = ForeignWord().Matches(outsideQuotes).Select(m => m.Value)
            .Where(w => w != "ToDo" && !source.Contains(Normalize(w), StringComparison.Ordinal))
            // Long kanji compounds with a two-character piece the transcript never uses
            // (「電話会議」 when only 「会議」 was said) are inventions too.
            .Concat(KanjiCompound().Matches(outsideQuotes).Select(m => m.Value)
                .Where(w => !OwnWords.Any(own => own.Contains(w, StringComparison.Ordinal) || w.Contains(own, StringComparison.Ordinal)))
                .Where(w => Enumerable.Range(0, w.Length - 1).Any(i => !source.Contains(w.Substring(i, 2), StringComparison.Ordinal))))
            .Distinct().ToList();
        if (unknown.Count > 0)
        {
            warnings.Add($"{section}: 原文にない語「{string.Join("」「", unknown)}」があります。");
            result += $" ⚠要確認（原文にない語: {string.Join("、", unknown)}）";
        }

        // Every quote must really be in the transcript.
        var quotes = Quote().Matches(result);
        bool needsQuote = section is not "会議の概要" && IsListItem(result) && !IsNone(result);
        if (quotes.Count == 0)
        {
            if (needsQuote && section is "決定事項" or "ToDo（担当者・期限）")
            {
                warnings.Add($"{section}: 根拠の引用がない項目があります。");
                return result + NoQuoteMark;
            }

            return result;
        }

        foreach (Match quote in quotes)
        {
            string q = Normalize(quote.Groups["q"].Value);
            if (q.Length > 0 && !source.Contains(q, StringComparison.Ordinal))
            {
                warnings.Add($"{section}: 引用「{quote.Groups["q"].Value}」が原文に見つかりません。");
                return result + QuoteMissingMark;
            }
        }

        return result;
    }

    internal static bool IsListItem(string line) => line.TrimStart().StartsWith('-') || line.TrimStart().StartsWith('*');

    internal static bool IsNone(string line) => line.Trim().TrimStart('-', '*', ' ') is "なし" or "特になし" or "該当なし";

    /// <summary>Ignores spacing, punctuation and full/half-width differences when comparing with the transcript.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char c in text.Normalize(NormalizationForm.FormKC))
        {
            var category = char.GetUnicodeCategory(c);
            if (char.IsWhiteSpace(c) || category is UnicodeCategory.OtherPunctuation or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation or UnicodeCategory.ClosePunctuation or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation)
            {
                continue;
            }

            sb.Append(char.ToLowerInvariant(c));
        }

        return sb.ToString();
    }

    /// <summary>
    /// After the minutes: what the correction step did (so every change can be checked), and the
    /// corrected transcript with 〔要確認〕 marks. The original note is not changed.
    /// </summary>
    public static string CorrectionAppendix(CorrectedTranscript correction)
    {
        var sb = new StringBuilder();
        sb.Append("\n---\n\n## 校正の記録\n");
        var shown = correction.Decisions.Where(d => d.Status != CorrectionStatus.Rejected).ToList();
        if (shown.Count == 0)
        {
            sb.Append("- 修正なし\n");
        }

        foreach (var d in shown)
        {
            string state = d.Status == CorrectionStatus.Applied ? "修正済み" : "要確認（原文のまま）";
            sb.Append($"- 「{d.Proposal.From}」→「{d.Proposal.To}」：{state}／{d.Why}");
            if (d.Proposal.Reason.Length > 0)
            {
                sb.Append($"／AIの理由: {d.Proposal.Reason}");
            }

            sb.Append('\n');
        }

        sb.Append("\n## 校正済み文字起こし\n\n");
        sb.Append(CorrectedTextMarker).Append('\n');
        sb.Append(correction.Annotated.Trim()).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Where the appendices after the minutes start (the numbered source, the correction log):
    /// they are for checking, not part of the minutes when exported or evaluated. -1 = none.
    /// </summary>
    public static int AppendixStart(string markdown)
    {
        string[] markers = ["\n---\n\n## 校正の記録", "\n---\n\n## 原文（番号付き）"];
        return markers.Select(m => markdown.IndexOf(m, StringComparison.Ordinal)).Where(i => i >= 0).DefaultIfEmpty(-1).Min();
    }

    /// <summary>Line that starts the corrected transcript (used to find it again for learning).</summary>
    public const string CorrectedTextMarker = "> 音声認識の誤りだけを辞書と文脈から直したものです。〔要確認: …〕は AI の候補で、原文のまま残しています。";

    /// <summary>The complete minutes note: a clear "AI draft" notice, the source, then the sections.</summary>
    public static string Document(string body, string sourceTitle, string modelName, DateTime now, int parts) =>
        $"""
        # 議事録（AI下書き）

        > この議事録は、ローカルAI（{modelName}）が文字起こし原文から自動で作成した**下書き**です。
        > 内容・担当者・期限は必ず原文と照らし合わせて確認・修正してください。「未確認」は原文で明言されていない項目です。
        > 原文ノート: [[{sourceTitle}]] ／ 作成: {now:yyyy-MM-dd HH:mm}{(parts > 1 ? $" ／ 原文を {parts} 部分に分けて処理" : string.Empty)}

        {body}
        """;
}
