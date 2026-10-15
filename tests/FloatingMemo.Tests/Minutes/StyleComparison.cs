using System.Diagnostics;
using System.Text;
using FloatingMemo.Services.Minutes;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// FLOATINGMEMO_LLM=1 only: the fixed seven headings against the topic layout, with the same
/// model, the same made-up meetings, the same settings (temperature 0.2, seed 42, context 8192,
/// CPU) and the same checks. Writes a Markdown report with every draft to %TEMP%\FloatingMemoTests.
/// FLOATINGMEMO_LLM_MODELS can limit the models (comma separated file names).
/// </summary>
public sealed class StyleComparison(ITestOutputHelper output)
{
    private sealed record Meeting(string Name, string Note, string Title, bool Correct);

    private static readonly Meeting[] Meetings =
    [
        new("短い学習相談（校正あり）", EvaluationMeeting.Note, "学習相談（架空）", true),
        new("長いアプリ開発定例（2 分割）", LongMeeting.Note, "アプリ開発定例（架空）", false),
    ];

    [LlmFact]
    public async Task FixedAgainstTopics()
    {
        string[] models = (Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_MODELS") ?? "Qwen3-1.7B-Q8_0.gguf,Qwen3-4B-Q4_K_M.gguf").Split(',');
        var table = new StringBuilder("| モデル | 会議 | 形式 | 時間 | 項目 | 重複 | 未決なのに決定 | 根拠のない決定 | ダミー | 誤認識の残り | 原文にない語 | 期待した決定 | 未決を決定にしない | ToDo | 議題に触れた / 詳細まで | 個人メモの混入 |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        var drafts = new StringBuilder();

        foreach (string name in models)
        {
            string? path = LlamaTextGenerator.AvailableModels().FirstOrDefault(m => m.FileName == name)?.Path;
            if (path is null)
            {
                output.WriteLine($"(skip {name}: not found)");
                continue;
            }

            foreach (var meeting in Meetings)
            {
                foreach (var style in new[] { MinutesStyle.FixedSections, MinutesStyle.Topics })
                {
                    using var model = new LlamaTextGenerator(path);
                    var watch = Stopwatch.StartNew();
                    var draft = await new MinutesGenerator(model, style).GenerateAsync(meeting.Note, meeting.Title, new DateTime(2026, 10, 15, 9, 0, 0),
                        dictionary: meeting.Correct ? EvaluationMeeting.Dictionary : null);
                    watch.Stop();

                    string body = Coverage.Body(draft.Markdown);
                    var quality = MinutesQuality.Analyze(draft.Markdown, meeting.Note, EvaluationMeeting.Dictionary);
                    int invented = body.Split('\n').Count(l => l.Contains("原文にない語", StringComparison.Ordinal));
                    var (decisions, todos) = DecisionsAndTasks(body);

                    string expected = "-", notDecided = "-", todo = "-", topics = "-";
                    if (meeting.Note == EvaluationMeeting.Note)
                    {
                        expected = $"{EvaluationMeeting.ExpectedDecisions.Count(ws => decisions.Any(d => ws.All(w => d.Contains(w, StringComparison.Ordinal))))}/{EvaluationMeeting.ExpectedDecisions.Length}";
                        int wrong = EvaluationMeeting.MustNotBeDecisions.Count(w => decisions.Any(d => d.Contains(w, StringComparison.Ordinal)));
                        notDecided = wrong == 0 ? "○" : $"× ({wrong})";
                        todo = todos.Any(l => EvaluationMeeting.ExpectedTodo.All(w => l.Contains(w, StringComparison.Ordinal))) ? "○" : "×";
                    }
                    else
                    {
                        int wrong = LongMeeting.NotDecided.Count(w => decisions.Any(d => d.Contains(w, StringComparison.Ordinal)));
                        notDecided = wrong == 0 ? "○" : $"× ({wrong})";
                        int covered = LongMeeting.Topics.Count(t => t.Keywords.Any(k => body.Contains(k, StringComparison.Ordinal)));
                        int complete = LongMeeting.Topics.Count(t => t.Keywords.Any(k => body.Contains(k, StringComparison.Ordinal))
                            && t.MustAppear.All(w => w.Split('|').Any(a => body.Contains(a, StringComparison.Ordinal))));
                        topics = $"{covered}/{LongMeeting.Topics.Length} / {complete}/{LongMeeting.Topics.Length}";
                        todo = $"{todos.Count(l => (l.Contains("森") && l.Contains("水曜")) || (l.Contains("石井") && l.Contains("今週")))}/2";
                    }

                    bool leaked = body.Contains("自分用", StringComparison.Ordinal) || body.Contains("後で読む", StringComparison.Ordinal);
                    string label = style == MinutesStyle.Topics ? "話題ごと" : "固定 7 見出し";
                    table.Append($"| {name.Replace(".gguf", string.Empty)} | {meeting.Name} | {label} | {watch.Elapsed.TotalSeconds:0} 秒 | {quality.Items} | "
                        + string.Join(" | ", QualityReport.Kinds.Select(k => quality.Count(k).ToString()))
                        + $" | {invented} | {expected} | {notDecided} | {todo} | {topics} | {(leaked ? "あり" : "なし")} |\n");

                    drafts.Append($"\n## {name} ／ {meeting.Name} ／ {label}\n\n指摘:\n");
                    foreach (var issue in quality.Issues)
                    {
                        drafts.Append($"- [{issue.Kind}] {issue.Section}: {issue.Item} — {issue.Detail}\n");
                    }

                    drafts.Append("\n```markdown\n").Append(draft.Markdown).Append("\n```\n");
                    output.WriteLine($"{name} {meeting.Name} {label}: {watch.Elapsed.TotalSeconds:0}s");
                }
            }
        }

        var report = new StringBuilder("# 議事録の形式の比較（固定 7 見出し ／ 話題ごと、架空データ・同一条件）\n\n");
        report.Append($"- 実行日時: {DateTime.Now:yyyy-MM-dd HH:mm}\n- 条件: 温度 0.2、シード 42、コンテキスト 8192、CPU のみ。短い会議は校正あり（架空の辞書 4 語）、長い会議は校正なし\n");
        report.Append("- 「話題ごと」は人のメモを文字起こしと分けて渡す。「固定 7 見出し」は現在のアプリと同じくノート全体を渡す\n\n");
        report.Append(table).Append(drafts);
        string file = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", $"minutes-style-comparison-{DateTime.Now:yyyyMMdd-HHmm}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, report.ToString());
        output.WriteLine(file);
        output.WriteLine(table.ToString());
    }

    /// <summary>Decision and task lines in either layout.</summary>
    private static (List<string> Decisions, List<string> Tasks) DecisionsAndTasks(string body)
    {
        var decisions = new List<string>();
        var tasks = new List<string>();
        string heading = string.Empty;
        foreach (string line in body.Split('\n'))
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                heading = line[3..].Trim();
                continue;
            }

            if (!MinutesValidator.IsListItem(line) || MinutesValidator.IsNone(line))
            {
                continue;
            }

            if (heading == "決定事項" || line.Contains(TopicPrompt.Decision, StringComparison.Ordinal))
            {
                decisions.Add(line);
            }

            if (heading.StartsWith("ToDo", StringComparison.Ordinal) || line.TrimStart().StartsWith("- [", StringComparison.Ordinal))
            {
                tasks.Add(line);
            }
        }

        return (decisions, tasks);
    }
}
