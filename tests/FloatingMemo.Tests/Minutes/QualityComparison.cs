using System.Diagnostics;
using System.Text;
using FloatingMemo.Services.Acceleration;
using FloatingMemo.Services.Minutes;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// FLOATINGMEMO_LLM=1 only: every layout on every made-up meeting, same model and settings,
/// repeated FLOATINGMEMO_LLM_RUNS times (default 2). One processing mode per test process:
/// FLOATINGMEMO_ACCEL = CpuOnly | Auto (native libraries are chosen once per process).
/// Filters: FLOATINGMEMO_LLM_MODELS, FLOATINGMEMO_LLM_STYLES, FLOATINGMEMO_LLM_CASES (names);
/// FLOATINGMEMO_LLM_PRIORITY = Auto | Speed | Quality (the recovery step).
/// Writes a Markdown table and a CSV to %TEMP%\FloatingMemoTests (the hardware is not named).
/// </summary>
public sealed class QualityComparison(ITestOutputHelper output)
{
    [LlmFact]
    public async Task CompareLayouts()
    {
        var mode = Enum.Parse<AccelerationMode>(Environment.GetEnvironmentVariable("FLOATINGMEMO_ACCEL") ?? "CpuOnly");
        AccelerationState.Initialize(mode);
        int runs = int.Parse(Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_RUNS") ?? "2");
        string[] models = (Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_MODELS") ?? "Qwen3-4B-Q4_K_M.gguf").Split(',');
        var styles = (Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_STYLES") ?? "FixedSections,Topics,Facts")
            .Split(',').Select(s => Enum.Parse<MinutesStyle>(s.Trim())).ToList();
        string? only = Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_CASES");
        var cases = EvaluationMeetings.All.Where(c => only is null || only.Split(',').Any(o => c.Name.Contains(o, StringComparison.Ordinal))).ToList();
        string label = Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_LABEL") ?? "current";
        var priority = Enum.Parse<MinutesPriority>(Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_PRIORITY") ?? "Auto");

        var table = new StringBuilder($"| 版 | モデル | 処理 | 会議 | 方式 | 回 | 時間 | 最大メモリ | {MinutesScoreCard.Header[2..]}\n");
        table.Append("|---|---|---|---|---|---|---|---|" + string.Concat(Enumerable.Repeat("---|", 14)) + "\n");
        var csv = new StringBuilder("label,model,mode,case,style,run,seconds,peak_mb,topics,decisions,tasks,numbers,wrong_numbers,invented_numbers,undecided_as_decision,duplicates,unsupported,dummies,misrecognitions,words_not_in_source,needs_review,items\n");
        var drafts = new StringBuilder();

        foreach (string name in models)
        {
            string? path = LlamaTextGenerator.AvailableModels().FirstOrDefault(m => m.FileName == name)?.Path;
            if (path is null)
            {
                output.WriteLine($"(skip {name}: not found)");
                continue;
            }

            foreach (var meeting in cases)
            {
                foreach (var style in styles)
                {
                    for (int run = 1; run <= runs; run++)
                    {
                        using var model = new LlamaTextGenerator(path);
                        using var peak = new PeakMemory();
                        var watch = Stopwatch.StartNew();
                        var draft = await new MinutesGenerator(model, style) { Priority = priority }.GenerateAsync(meeting.Note, meeting.Title, new DateTime(2026, 10, 17, 9, 0, 0),
                            dictionary: meeting.Dictionary.Count > 0 ? meeting.Dictionary : null);
                        watch.Stop();
                        var s = MinutesScore.Score(meeting, draft.Markdown);
                        string styleName = style switch
                        {
                            MinutesStyle.Topics => "話題ごと",
                            MinutesStyle.Facts => "事実抽出",
                            MinutesStyle.FactsProse => "事実抽出＋文章化",
                            _ => "固定 7 見出し",
                        };
                        string modelName = name.Replace(".gguf", string.Empty);
                        table.Append($"| {label} | {modelName} | {mode} | {meeting.Name} | {styleName} | {run} | {watch.Elapsed.TotalSeconds:0} 秒 | {peak.Megabytes} MB {s.Row}\n");
                        csv.Append($"{label},{modelName},{mode},{meeting.Name},{style},{run},{watch.Elapsed.TotalSeconds:0.0},{peak.Megabytes},"
                            + $"{s.Topics}/{s.TopicsTotal},{s.Decisions}/{s.DecisionsTotal},{s.Tasks}/{s.TasksTotal},{s.Numbers}/{s.NumbersTotal},{s.WrongNumbers},{s.InventedNumbers},"
                            + $"{s.UndecidedAsDecision},{s.Duplicates},{s.UnsupportedDecisions},{s.Dummies},{s.Misrecognitions},{s.WordsNotInSource},{s.NeedsReview},{s.Items}\n");
                        if (run == 1)
                        {
                            drafts.Append($"\n## {label} ／ {modelName} ／ {mode} ／ {meeting.Name} ／ {styleName}\n\n```markdown\n{draft.Markdown}\n```\n");
                            // Each model call (step, part, input/output characters, seconds): where the time went.
                            drafts.Append($"\n{draft.Run?.InputMode}: " + string.Join(", ", (draft.Run?.Calls ?? []).Select(c => $"{c.Step}#{c.Part} {c.InputCharacters}->{c.OutputCharacters} {c.Elapsed.TotalSeconds:0}s")) + "\n");
                        }

                        output.WriteLine($"{label} {modelName} {mode} {meeting.Name} {styleName} #{run}: {watch.Elapsed.TotalSeconds:0}s");
                    }
                }
            }
        }

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmm");
        string dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests");
        Directory.CreateDirectory(dir);
        string md = Path.Combine(dir, $"quality-{label}-{mode}-{stamp}.md");
        await File.WriteAllTextAsync(md, $"# 議事録の比較（{label}、{mode}、{DateTime.Now:yyyy-MM-dd HH:mm}）\n\n" + table + drafts);
        await File.WriteAllTextAsync(Path.Combine(dir, $"quality-{label}-{mode}-{stamp}.csv"), csv.ToString());
        output.WriteLine(md);
        output.WriteLine(table.ToString());
    }

    /// <summary>Highest working set of this process while it is alive (sampled every 200 ms).</summary>
    private sealed class PeakMemory : IDisposable
    {
        private readonly Timer _timer;
        private long _peak;

        public PeakMemory()
        {
            _timer = new Timer(_ => Sample(), null, 0, 200);
        }

        public long Megabytes
        {
            get
            {
                Sample();
                return Interlocked.Read(ref _peak) / (1024 * 1024);
            }
        }

        private void Sample()
        {
            using var p = Process.GetCurrentProcess();
            long now = p.WorkingSet64;
            long seen;
            while ((seen = Interlocked.Read(ref _peak)) < now && Interlocked.CompareExchange(ref _peak, now, seen) != seen)
            {
            }
        }

        public void Dispose() => _timer.Dispose();
    }
}
