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
        progress?.Report(-1);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        for (int i = 1; i <= 4; i++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false); // long enough to test cancelling
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

        string first = transcript.Split('。')[0];
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
