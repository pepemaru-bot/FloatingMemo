using System.Text.RegularExpressions;
using FloatingMemo.Services.Notes;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// Links a transcript note and its AI minutes. The minutes are always a separate note
/// ("会議（AI議事録）.md") that names its source; the transcript is never overwritten.
/// </summary>
public static partial class MinutesNotes
{
    public const string Suffix = "（AI議事録）";

    [GeneratedRegex(@"原文ノート:\s*\[\[(?<title>[^\]\n]+)\]\]")]
    private static partial Regex SourceLink();

    public static string TitleFor(string sourceTitle) => sourceTitle + Suffix;

    public static bool IsMinutes(string title, string text) => title.Contains(Suffix, StringComparison.Ordinal) || SourceLink().IsMatch(text);

    /// <summary>The newest minutes made from this note ("会議（AI議事録）", "会議（AI議事録） 2", …).</summary>
    public static NoteInfo? FindMinutes(IEnumerable<NoteInfo> notes, string sourceTitle) => notes
        .Where(n => n.Exists && n.Title.StartsWith(TitleFor(sourceTitle), StringComparison.Ordinal))
        .OrderByDescending(n => File.GetLastWriteTimeUtc(n.FilePath))
        .FirstOrDefault();

    /// <summary>The transcript note these minutes were made from.</summary>
    public static NoteInfo? FindSource(IEnumerable<NoteInfo> notes, string minutesTitle, string minutesText)
    {
        string? title = SourceLink().Match(minutesText) is { Success: true } m
            ? m.Groups["title"].Value
            : minutesTitle.Contains(Suffix, StringComparison.Ordinal) ? minutesTitle[..minutesTitle.IndexOf(Suffix, StringComparison.Ordinal)] : null;
        return title is null ? null : notes.FirstOrDefault(n => n.Exists && n.Title == title);
    }
}

/// <summary>
/// Test stand-in for the LLM (test builds' "--fake-llm"): answers instantly in the expected
/// format, quoting the first sentence, and deliberately invents an assignee so the checks can
/// be seen working end to end.
/// </summary>
public sealed class FakeMinutesModel : ILocalTextGenerator
{
    public string ModelName => "テスト用の擬似モデル";

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        if (user.Contains("議事録にまだ入っていない", StringComparison.Ordinal))
        {
            return string.Empty; // the recovery pass: nothing more to find (and no waiting in UI tests)
        }

        progress?.Report(-1);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        for (int i = 1; i <= 4; i++)
        {
            await Task.Delay(500, cancellationToken).ConfigureAwait(false); // long enough to test cancelling and to see each step
            progress?.Report(i * 40);
        }

        string transcript = user.Split("<文字起こし>\n")[^1].Split("\n</文字起こし>")[0];
        if (system == TranscriptCorrector.System)
        {
            // Correction step: propose dictionary terms whose listed misrecognition occurs, and
            // (deliberately) a name change that the checks must not apply.
            var lines = new List<string>();
            foreach (string line in user.Split('\n').Where(l => l.StartsWith("- ", StringComparison.Ordinal) && l.Contains("誤認識例: ")))
            {
                string term = line[2..].Split('（', ' ')[0];
                foreach (string heard in line.Split("誤認識例: ")[1].Split('、'))
                {
                    if (transcript.Contains(heard.Trim(), StringComparison.Ordinal))
                    {
                        lines.Add($"- 原文「{heard.Trim()}」→ 修正「{term}」（理由: 辞書の誤認識例／確信: 高）");
                    }
                }
            }

            if (transcript.Contains("田中", StringComparison.Ordinal))
            {
                lines.Add("- 原文「田中」→ 修正「架空次郎」（理由: 推測／確信: 高）");
            }

            return lines.Count == 0 ? "なし" : string.Join("\n", lines);
        }

        if (system == TemplateFacts.AssignSystem)
        {
            // Heading assignment: every fact to the first heading offered.
            string heading = user.Split('\n').FirstOrDefault(l => l.Length > 2 && char.IsDigit(l[0]))?.Split('.')[0] ?? "0";
            return string.Join("\n", user.Split('\n').Where(l => l.StartsWith('F')).Select(l => $"{l.Split(' ')[0]}: {heading}"));
        }

        if (system.StartsWith("あなたは議事録の清書係です。「確認済みの事実」だけを使って、次の文章の下書き", StringComparison.Ordinal))
        {
            // Draft heading: one sentence made of the first fact, citing it.
            var fact = user.Split('\n').FirstOrDefault(l => l.StartsWith('F'));
            if (fact is null)
            {
                return string.Empty;
            }

            string id = fact.Split(' ')[0];
            string text = fact[(fact.IndexOf(']') + 1)..].Split('（')[0].Trim();
            return $"{text}。[{id}]";
        }

        if (system == FactPrompt.System)
        {
            // Fact extraction: the first numbered sentence as a decision, plus a task with an
            // invented assignee the checks must remove.
            string src = user.Split("<原文>\n")[^1].Split("\n</原文>")[0];
            var firstUnit = src.Split('\n').FirstOrDefault(l => l.StartsWith('B')) ?? src.Split('\n').FirstOrDefault(l => l.StartsWith('A')) ?? "B001 なし";
            string id = firstUnit.Split(' ')[0];
            string said = firstUnit[(id.Length + 1)..].TrimEnd('。');
            return $$"""
                {"type":"決定","topic":"{{said[..Math.Min(8, said.Length)]}}","text":"{{said}}","src":["{{id}}"],"who":"","due":""}
                {"type":"作業","topic":"{{said[..Math.Min(8, said.Length)]}}","text":"確認","src":["{{id}}"],"who":"架空太郎","due":""}
                """;
        }

        string first = transcript.Split('。')[0];
        if (system == TopicPrompt.System)
        {
            return $"""
                ## 概要
                - {first}
                ## {first[..Math.Min(8, first.Length)]}
                - 【決定】{first}（原文:「{first}」）
                - 【ToDo】確認 — 担当: 架空太郎 / 期限: 未確認（原文:「{first}」）
                """;
        }

        return $"""
            ## 会議の概要
            - {first}
            ## 決定事項
            - {first}（原文:「{first}」）
            ## ToDo（担当者・期限）
            - [ ] 確認 — 担当: 架空太郎 / 期限: 未確認（原文:「{first}」）
            """;
    }
}
