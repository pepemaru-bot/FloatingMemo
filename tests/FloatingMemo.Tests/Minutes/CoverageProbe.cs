using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// FLOATINGMEMO_LLM=1 and FLOATINGMEMO_EVAL_CASE=&lt;case.json&gt; only: one long made-up meeting
/// (kept outside the repository) with its template and the facts a correct draft must contain.
/// For every expected fact it reports the last pipeline stage that still had it (source →
/// extracted → kept → final → in the draft → under the right heading), plus invented content,
/// leaked template instructions, undecided-as-decided, time, peak RAM and GPU memory.
/// FLOATINGMEMO_ACCEL, FLOATINGMEMO_LLM_PRIORITY and FLOATINGMEMO_LLM_LABEL as in QualityComparison.
/// </summary>
public sealed partial class CoverageProbe(ITestOutputHelper output)
{
    private sealed record Expected(string Subject, string Section, string[] Keywords, bool Important);

    [GeneratedRegex(@"〔[^〕]*〕|（原文[:：][^）]*）|[AB]\d{3,}|F\d{3,}")]
    private static partial Regex Refs();

    private static string N(string s) => Numerals.Arabic(MinutesValidator.Normalize(s));

    private static bool Has(string text, string[] keywords) => keywords.All(k => N(text).Contains(N(k), StringComparison.Ordinal));

    [LlmFact]
    public async Task TraceOneMeeting()
    {
        string? casePath = Environment.GetEnvironmentVariable("FLOATINGMEMO_EVAL_CASE");
        if (casePath is null || !File.Exists(casePath))
        {
            output.WriteLine("FLOATINGMEMO_EVAL_CASE not set: nothing to do.");
            return;
        }

        using var doc = JsonDocument.Parse(File.ReadAllText(casePath));
        var root = doc.RootElement;
        string notes = root.GetProperty("notes").GetString()!;
        string transcript = root.GetProperty("transcript").GetString()!;
        string note = notes + "\n\n" + TranscriptInsertion.Header + "\n" + transcript;
        var expected = root.GetProperty("facts").EnumerateArray().Select(f => new Expected(
            f.GetProperty("subject").GetString()!, f.GetProperty("section").GetString()!,
            f.GetProperty("keywords").EnumerateArray().Select(k => k.GetString()!).ToArray(), f.GetProperty("important").GetBoolean())).ToList();
        string[] notDecided = root.GetProperty("notDecided").EnumerateArray().Select(x => x.GetString()!).ToArray();
        string[] instructions = root.GetProperty("instructionPhrases").EnumerateArray().Select(x => x.GetString()!).ToArray();
        string templateFile = Path.Combine(Path.GetDirectoryName(casePath)!, root.GetProperty("template").GetString()!);
        var template = TemplateLibrary.Validate(TemplateLibrary.ParseJson(File.ReadAllText(templateFile)), "eval-template");

        var mode = Enum.Parse<Services.Acceleration.AccelerationMode>(Environment.GetEnvironmentVariable("FLOATINGMEMO_ACCEL") ?? "Auto");
        Services.Acceleration.AccelerationState.Initialize(mode);
        var priority = Enum.Parse<MinutesPriority>(Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_PRIORITY") ?? "Auto");
        string label = Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_LABEL") ?? "current";
        string model = Path.Combine(Services.ModelLocations.UserModelsDirectory, "Qwen3-4B-Q4_K_M.gguf");
        Assert.True(File.Exists(model), model);

        var sb = new StringBuilder($"# 網羅性の追跡（{label}、{mode}、{priority}、{DateTime.Now:yyyy-MM-dd HH:mm}）\n\n");
        foreach (bool withTemplate in new[] { true, false })
        {
            using var llm = new LlamaTextGenerator(model);
            long peak = 0;
            int gpuBefore = GpuMemory(), gpuPeak = gpuBefore;
            using var sampler = new Timer(_ =>
            {
                using var p = Process.GetCurrentProcess();
                peak = Math.Max(peak, p.WorkingSet64);
            }, null, 0, 200);
            using var gpuSampler = new Timer(_ => gpuPeak = Math.Max(gpuPeak, GpuMemory()), null, 0, 1000);
            var watch = Stopwatch.StartNew();
            var draft = await new MinutesGenerator(llm, MinutesStyle.Facts, withTemplate ? template : null) { Priority = priority }
                .GenerateAsync(note, root.GetProperty("title").GetString()!, new DateTime(2026, 10, 23, 9, 0, 0));
            watch.Stop();

            int appendix = MinutesValidator.AppendixStart(draft.Markdown);
            string body = appendix >= 0 ? draft.Markdown[..appendix] : draft.Markdown;
            var lines = body.Split('\n').Where(l => l.TrimStart().StartsWith("- ", StringComparison.Ordinal)).ToList();
            string Section(string title)
            {
                int at = body.IndexOf("## " + title, StringComparison.Ordinal);
                if (at < 0)
                {
                    return string.Empty;
                }

                int next = body.IndexOf("\n## ", at + 3, StringComparison.Ordinal);
                return next < 0 ? body[at..] : body[at..next];
            }

            var trace = draft.Trace!;
            bool InStage(string stage, string[] k) => trace.Items.Any(i => i.Stage == stage && Has(i.Text, k));
            sb.Append($"## {(withTemplate ? "テンプレートあり" : "テンプレートなし（話題ごと）")}\n\n");
            sb.Append($"- 時間 {watch.Elapsed.TotalSeconds:0} 秒、最大メモリ {peak / 1_048_576} MB、GPU メモリ {gpuBefore} → 最大 {gpuPeak} MiB\n");
            sb.Append($"- 段階ごとの件数: {draft.Run?.Stages}\n- 呼び出し: {draft.Run?.InputMode} / ");
            sb.Append(string.Join(", ", (draft.Run?.Calls ?? []).Select(c => $"{c.Step}#{c.Part} {c.InputCharacters}->{c.OutputCharacters} {c.Elapsed.TotalSeconds:0}s"))).Append("\n\n");
            sb.Append("| 科目 | 事実（キーワード） | 原文 | 抽出 | 採用 | 最終 | 議事録 | 正しい見出し |\n|---|---|---|---|---|---|---|---|\n");
            int important = 0, recalled = 0, placed = 0;
            foreach (var e in expected)
            {
                bool source = Has(transcript, e.Keywords);
                bool parsed = InStage("extract-parsed", e.Keywords) || InStage("recover-parsed", e.Keywords);
                bool kept = InStage("extract-kept", e.Keywords) || InStage("recover-kept", e.Keywords);
                bool final = InStage("final", e.Keywords);
                bool inDraft = lines.Any(l => Has(Refs().Replace(l, string.Empty), e.Keywords));
                bool right = withTemplate ? Section(e.Section).Split('\n').Any(l => Has(Refs().Replace(l, string.Empty), e.Keywords)) : inDraft;
                if (e.Important)
                {
                    important++;
                    recalled += inDraft ? 1 : 0;
                    placed += right ? 1 : 0;
                }

                static string M(bool b) => b ? "○" : "×";
                sb.Append($"| {e.Subject} | {string.Join("・", e.Keywords)}{(e.Important ? string.Empty : "（参考）")} | {M(source)} | {M(parsed)} | {M(kept)} | {M(final)} | {M(inDraft)} | {M(right)} |\n");
            }

            var decisionLines = lines.Where(l => l.Contains("【決定】", StringComparison.Ordinal)).ToList();
            int reversals = decisionLines.Count(l => notDecided.Any(w => N(l).Contains(N(w), StringComparison.Ordinal)));
            int leaked = lines.Count(l => instructions.Any(p => l.Contains(p, StringComparison.Ordinal)) && !instructions.Any(p => transcript.Contains(p, StringComparison.Ordinal)));
            int unsupported = lines.Count(l => l.Contains("⚠", StringComparison.Ordinal));
            sb.Append($"\n- 重要事実の網羅率: {recalled}/{important}（{100.0 * recalled / important:0}%）、正しい見出し: {placed}/{important}\n");
            sb.Append($"- 未決なのに【決定】: {reversals}、テンプレートの説明文の混入: {leaked}、⚠付きの項目: {unsupported}、項目数: {lines.Count}\n\n");
            sb.Append("<details><summary>議事録</summary>\n\n```markdown\n").Append(body).Append("\n```\n</details>\n\n");
            sb.Append("<details><summary>追跡（落ちた・除いた事実）</summary>\n\n");
            foreach (var item in trace.Items.Where(i => i.Stage.EndsWith("dropped", StringComparison.Ordinal) || i.Stage == "assign" || i.Stage.StartsWith("recover", StringComparison.Ordinal)))
            {
                sb.Append($"- {item.Stage} {item.Id} [{item.Kind}] {item.Text} ({item.Sources}) → {item.Note}\n");
            }

            sb.Append("</details>\n\n");
            output.WriteLine($"{label} {(withTemplate ? "template" : "topics")}: recall {recalled}/{important}, placed {placed}, reversals {reversals}, leaked {leaked}, {watch.Elapsed.TotalSeconds:0}s");
        }

        string dir = Path.Combine(Path.GetDirectoryName(casePath)!, "results");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, $"coverage-{label}-{mode}-{DateTime.Now:yyyyMMdd-HHmm}.md"), sb.ToString());
    }

    /// <summary>GPU memory in use (MiB) from nvidia-smi; 0 when there is none.</summary>
    private static int GpuMemory()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.used --format=csv,noheader,nounits")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            string text = p!.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return int.TryParse(text.Trim().Split('\n')[0], out int mb) ? mb : 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return 0;
        }
    }
}
