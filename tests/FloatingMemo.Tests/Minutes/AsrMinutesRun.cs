using System.Diagnostics;
using System.Text;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// FLOATINGMEMO_LLM=1 and FLOATINGMEMO_ASR_EVAL_DIR: minutes of the spoken made-up meetings
/// (AsrMinutesCases) from each speech recognizer's transcript (&lt;case&gt;/asr/*.json) and from the
/// reference transcript ("理想ASR"), with everything else fixed: Qwen3-4B-Q4_K_M, the app's
/// fact-first pipeline (quality), the sampler seeds, the scoring. Every transcript is joined as the
/// app joins Whisper's segments (JoinSegments) and inserted under the app's transcript header.
/// Scored against the reference meeting, so recognition errors count as they would for the reader.
/// FLOATINGMEMO_ASR_SOURCES: comma-separated result names to include (default: all), e.g.
/// "whisper-small-hint-clean,nemotron-iq4nl-clean"; FLOATINGMEMO_BENCH_SEEDS as for QualityBenchRun.
/// </summary>
public sealed class AsrMinutesRun(ITestOutputHelper output)
{
    [LlmFact]
    public async Task RunAll()
    {
        string root = Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_EVAL_DIR")!;
        string outDir = Path.Combine(root, "minutes");
        Directory.CreateDirectory(outDir);
        // FLOATINGMEMO_ACCELERATION=CpuOnly: the LLM without the GPU (small PCs).
        Services.Acceleration.AccelerationState.Initialize(Enum.TryParse<Services.Acceleration.AccelerationMode>(Environment.GetEnvironmentVariable("FLOATINGMEMO_ACCELERATION"), out var mode) ? mode : Services.Acceleration.AccelerationMode.Auto);
        string model = Path.Combine(Services.ModelLocations.UserModelsDirectory, "Qwen3-4B-Q4_K_M.gguf");
        Assert.True(File.Exists(model), model);
        var only = (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_SOURCES") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        var seeds = (Environment.GetEnvironmentVariable("FLOATINGMEMO_BENCH_SEEDS") ?? "42").Split(',').Select(uint.Parse).ToList();

        var sb = new StringBuilder($"# ASR別の議事録品質（Qwen3-4B-Q4_K_M、事実抽出・品質優先、{DateTime.Now:yyyy-MM-dd HH:mm}）\n\nシード: {string.Join(",", seeds)}\n\n");
        sb.Append("| 文字起こし | 会議 | シード | 完全網羅 | 重要事項 | 根拠つき重要事項 | 対象の取り違え | 決定 | Next Action | 未決→決定 | 正解にない数値 | 重複 | 見出し | 根拠リンク誤り | 要確認 | 項目 | 時間 |\n");
        sb.Append("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        using var llm = new LlamaTextGenerator(model);
        var all = new List<(string Source, BenchScore Score)>();
        // FLOATINGMEMO_ASR_MINUTES_CASES: only these meetings (comma-separated names; default: all).
        var meetings = (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_MINUTES_CASES") ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        foreach (var c in AsrMinutesCases.All.Where(c => meetings.Count == 0 || meetings.Contains(c.Name)))
        {
            // The reference transcript, then each recognizer's.
            var sources = new List<(string Name, string Text)> { ("reference", LocalWhisperTranscriptionService.JoinSegments(c.Transcript.Split('\n'))) };
            foreach (string file in Directory.GetFiles(Path.Combine(root, c.Name, "asr"), "*.json").Order())
            {
                if (!file.EndsWith(".raw.json", StringComparison.Ordinal))
                {
                    sources.Add((Path.GetFileNameWithoutExtension(file), Transcription.AsrResultFile.Parse(File.ReadAllText(file)).Transcript));
                }
            }

            foreach (var (name, text) in sources.Where(s => only.Count == 0 || only.Contains(s.Name)))
            foreach (uint seed in seeds)
            {
                LlamaTextGenerator.SamplingSeed = seed;
                var input = c with { Transcript = text };
                var watch = Stopwatch.StartNew();
                var draft = await new MinutesGenerator(llm, MinutesStyle.Facts) { Priority = MinutesPriority.Quality }
                    .GenerateAsync(input.Note, c.Title, new DateTime(2026, 10, 31, 9, 0, 0));
                watch.Stop();
                // Scored against the reference meeting (what was said), not the recognizer's text.
                var s = QualityBench.Score(c, draft.Markdown, watch.Elapsed.TotalSeconds);
                all.Add((name, s));
                File.WriteAllText(Path.Combine(outDir, $"{c.Name}-{name}-{seed}.md"), draft.Markdown);
                string row = $"| {name} | {c.Name} | {seed} | {s.Full}/{s.Facts} | {s.ImportantFull}/{s.ImportantTotal} | {s.ImportantEvidenced}/{s.ImportantTotal} | {s.WrongSubject} | "
                    + $"{s.Decisions}/{s.DecisionsTotal} | {s.Tasks}/{s.TasksTotal} | {s.UndecidedAsDecision} | {s.InventedNumbers} | {s.Duplicates} | {s.Headings} | {s.WrongLinks}/{s.Links} | {s.NeedsReview} | {s.Items} | {s.Seconds:F0}s |";
                sb.Append(row).Append('\n');
                output.WriteLine(row);
                File.WriteAllText(Path.Combine(outDir, "minutes-scores.md"), sb.ToString());
            }
        }

        // Totals per transcript source over both meetings and every seed.
        sb.Append("\n## 合計（2会議 × シード）\n\n| 文字起こし | 完全網羅 | 重要事項 | 根拠つき重要事項 | 対象の取り違え | 決定 | Next Action | 未決→決定 | 正解にない数値 | 重複 | 根拠リンク誤り | 要確認 | 項目 | 時間 |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var g in all.GroupBy(x => x.Source))
        {
            int S(Func<BenchScore, int> f) => g.Sum(x => f(x.Score));
            sb.Append($"| {g.Key} | {S(s => s.Full)}/{S(s => s.Facts)} | {S(s => s.ImportantFull)}/{S(s => s.ImportantTotal)} | {S(s => s.ImportantEvidenced)}/{S(s => s.ImportantTotal)} | {S(s => s.WrongSubject)} | "
                + $"{S(s => s.Decisions)}/{S(s => s.DecisionsTotal)} | {S(s => s.Tasks)}/{S(s => s.TasksTotal)} | {S(s => s.UndecidedAsDecision)} | {S(s => s.InventedNumbers)} | {S(s => s.Duplicates)} | "
                + $"{S(s => s.WrongLinks)}/{S(s => s.Links)} | {S(s => s.NeedsReview)} | {S(s => s.Items)} | {g.Sum(x => x.Score.Seconds):F0}s |\n");
        }

        File.WriteAllText(Path.Combine(outDir, "minutes-scores.md"), sb.ToString());
        output.WriteLine(sb.ToString());
    }

    /// <summary>
    /// FLOATINGMEMO_ASR_RESCORE=1 and FLOATINGMEMO_ASR_EVAL_DIR: scores every saved draft
    /// (minutes/&lt;case&gt;-&lt;source&gt;-&lt;seed&gt;.md) again with the current scorer, without generating
    /// anything, so runs made at different times are compared with one scorer. Time is not known here.
    /// </summary>
    [Fact]
    public void RescoreSavedDrafts()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_RESCORE") != "1")
        {
            return;
        }

        string outDir = Path.Combine(Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_EVAL_DIR")!, "minutes");
        var all = new List<(string Source, BenchScore Score)>();
        foreach (string file in Directory.GetFiles(outDir, "*.md").Where(f => !Path.GetFileName(f).StartsWith("minutes-", StringComparison.Ordinal)).Order())
        {
            string name = Path.GetFileNameWithoutExtension(file);
            var c = AsrMinutesCases.All.Single(x => name.StartsWith(x.Name + "-", StringComparison.Ordinal));
            string source = name[(c.Name.Length + 1)..name.LastIndexOf('-')];
            all.Add((source, QualityBench.Score(c, File.ReadAllText(file))));
        }

        var sb = new StringBuilder($"# ASR別の議事録品質（保存済みの下書きを現在の採点で再集計、{DateTime.Now:yyyy-MM-dd HH:mm}）\n\n");
        sb.Append("| 文字起こし | 会議 | 下書き数 | 完全網羅 | 重要事項 | 根拠つき重要事項 | 対象の取り違え | 決定 | Next Action | 未決→決定 | 正解にない数値 | 重複 | 根拠リンク誤り | 要確認 | 項目 |\n|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|\n");
        foreach (var g in all.GroupBy(x => (x.Source, x.Score.Case)).Concat(all.GroupBy(x => (x.Source, Case: "合計"))).OrderBy(g => g.Key.Source).ThenBy(g => g.Key.Case))
        {
            int S(Func<BenchScore, int> f) => g.Sum(x => f(x.Score));
            sb.Append($"| {g.Key.Source} | {g.Key.Case} | {g.Count()} | {S(s => s.Full)}/{S(s => s.Facts)} | {S(s => s.ImportantFull)}/{S(s => s.ImportantTotal)} | {S(s => s.ImportantEvidenced)}/{S(s => s.ImportantTotal)} | {S(s => s.WrongSubject)} | "
                + $"{S(s => s.Decisions)}/{S(s => s.DecisionsTotal)} | {S(s => s.Tasks)}/{S(s => s.TasksTotal)} | {S(s => s.UndecidedAsDecision)} | {S(s => s.InventedNumbers)} | {S(s => s.Duplicates)} | "
                + $"{S(s => s.WrongLinks)}/{S(s => s.Links)} | {S(s => s.NeedsReview)} | {S(s => s.Items)} |\n");
        }

        File.WriteAllText(Path.Combine(outDir, "minutes-rescored.md"), sb.ToString());
        output.WriteLine(sb.ToString());
    }

    /// <summary>
    /// FLOATINGMEMO_BENCH_RESCORE=&lt;label,label...&gt; and FLOATINGMEMO_BENCH_OUT: the saved drafts of earlier
    /// QualityBench runs (&lt;label&gt;-&lt;case&gt;-&lt;seed&gt;.md) scored again with the current scorer (totals per label).
    /// </summary>
    [Fact]
    public void RescoreBenchDrafts()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_BENCH_RESCORE") is not { Length: > 0 } labels)
        {
            return;
        }

        string dir = Environment.GetEnvironmentVariable("FLOATINGMEMO_BENCH_OUT")!;
        var cases = QualityBench.All.Concat(QualityHoldout.All).ToList();
        var sb = new StringBuilder("| ラベル | 下書き数 | 完全網羅 | 重要事項 | 根拠つき重要事項 | 決定 | Next Action | 未決→決定 | 原文にない数値 |\n|---|---|---|---|---|---|---|---|---|\n");
        foreach (string label in labels.Split(','))
        {
            var scores = Directory.GetFiles(dir, label + "-*.md").Where(f => !f.EndsWith("-scores.md", StringComparison.Ordinal)).Select(f =>
            {
                string rest = Path.GetFileNameWithoutExtension(f)[(label.Length + 1)..];
                var c = cases.Single(x => rest.StartsWith(x.Name + "-", StringComparison.Ordinal));
                return QualityBench.Score(c, File.ReadAllText(f));
            }).ToList();
            int S(Func<BenchScore, int> f) => scores.Sum(f);
            sb.Append($"| {label} | {scores.Count} | {S(s => s.Full)}/{S(s => s.Facts)} | {S(s => s.ImportantFull)}/{S(s => s.ImportantTotal)} | {S(s => s.ImportantEvidenced)}/{S(s => s.ImportantTotal)} | "
                + $"{S(s => s.Decisions)}/{S(s => s.DecisionsTotal)} | {S(s => s.Tasks)}/{S(s => s.TasksTotal)} | {S(s => s.UndecidedAsDecision)} | {S(s => s.InventedNumbers)} |\n");
        }

        File.WriteAllText(Path.Combine(dir, "rescored.md"), sb.ToString());
        output.WriteLine(sb.ToString());
    }
}
