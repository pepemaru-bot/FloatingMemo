using System.Diagnostics;
using System.Text;
using FloatingMemo.Services.Minutes;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// Runs only when FLOATINGMEMO_LLM=1: Qwen3 1.7B and 4B on the same made-up meeting, with the
/// same input, dictionary, pipeline (correction + minutes), sampling settings (temperature 0.2,
/// fixed seed) and context size. Writes a Markdown report to %TEMP%\FloatingMemoTests and prints
/// it. Nothing is sent anywhere; no production prompt is changed by this test.
/// </summary>
public sealed class ModelComparison(ITestOutputHelper output)
{
    [LlmFact]
    public async Task CompareOneSevenAndFourB()
    {
        var report = new StringBuilder();
        report.Append("# Qwen3 議事録の比較（架空データ・同一条件）\n\n");
        report.Append($"- 実行日時: {DateTime.Now:yyyy-MM-dd HH:mm}\n- 入力: 人間のメモ＋AI文字起こし（現在のアプリと同じく、ノート全体をそのまま渡す）\n");
        report.Append("- 条件: 校正あり（架空の辞書 4 語）、温度 0.2、シード 42、コンテキスト 8192、CPU のみ\n\n");
        report.Append("| モデル | 時間 | 項目数 | 重複 | 未決なのに決定 | 根拠のない決定 | ダミー・空欄 | 誤認識語の残り | 期待した決定 | 未決を決定にしない | ToDo（担当・期限） |\n|---|---|---|---|---|---|---|---|---|---|---|\n");
        var drafts = new StringBuilder();

        foreach (string name in new[] { "Qwen3-1.7B-Q8_0.gguf", "Qwen3-4B-Q4_K_M.gguf" })
        {
            string path = LlamaTextGenerator.AvailableModels().FirstOrDefault(m => m.FileName == name)?.Path ?? name;
            if (!File.Exists(path))
            {
                output.WriteLine($"(skip {name}: not downloaded)");
                continue;
            }

            using var model = new LlamaTextGenerator(path);
            var watch = Stopwatch.StartNew();
            var draft = await new MinutesGenerator(model).GenerateAsync(EvaluationMeeting.Note, "学習相談（架空）", new DateTime(2026, 10, 13, 9, 0, 0),
                dictionary: EvaluationMeeting.Dictionary);
            watch.Stop();

            var quality = MinutesQuality.Analyze(draft.Markdown, EvaluationMeeting.Note, EvaluationMeeting.Dictionary);
            var minutes = MinutesValidator.Parse(draft.Markdown.Split("\n---\n\n## 校正の記録")[0]);
            string decisions = string.Join("\n", minutes["決定事項"]);
            string todos = string.Join("\n", minutes["ToDo（担当者・期限）"]);
            int expected = EvaluationMeeting.ExpectedDecisions.Count(words => words.All(w => decisions.Contains(w, StringComparison.Ordinal)));
            int wrong = EvaluationMeeting.MustNotBeDecisions.Count(w => decisions.Contains(w, StringComparison.Ordinal));
            bool todo = todos.Split('\n').Any(l => EvaluationMeeting.ExpectedTodo.All(w => l.Contains(w, StringComparison.Ordinal)));

            report.Append($"| {name} | {watch.Elapsed.TotalSeconds:0} 秒 | {quality.Items} | "
                + string.Join(" | ", QualityReport.Kinds.Select(k => quality.Count(k).ToString()))
                + $" | {expected}/{EvaluationMeeting.ExpectedDecisions.Length} | {(wrong == 0 ? "○" : $"× ({wrong})")} | {(todo ? "○" : "×")} |\n");

            drafts.Append($"\n## {name}\n\n指摘:\n");
            foreach (var issue in quality.Issues)
            {
                drafts.Append($"- [{issue.Kind}] {issue.Section}: {issue.Item} — {issue.Detail}\n");
            }

            drafts.Append("\n```markdown\n").Append(draft.Markdown).Append("\n```\n");
        }

        report.Append(drafts);
        string file = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", $"minutes-comparison-{DateTime.Now:yyyyMMdd-HHmm}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, report.ToString());
        output.WriteLine(file);
        output.WriteLine(report.ToString());
    }
}
