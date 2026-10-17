using System.Diagnostics;
using System.Text;
using FloatingMemo.Services.Acceleration;
using FloatingMemo.Services.Minutes;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>FLOATINGMEMO_LLM=1 only: the made-up sample templates on made-up meetings, to read the result.</summary>
public sealed class TemplateProbe(ITestOutputHelper output)
{
    [LlmFact]
    public async Task SampleTemplatesOnMadeUpMeetings()
    {
        AccelerationState.Initialize(Enum.Parse<AccelerationMode>(Environment.GetEnvironmentVariable("FLOATINGMEMO_ACCEL") ?? "Auto"));
        string path = LlamaTextGenerator.AvailableModels().First(m => m.FileName == "Qwen3-4B-Q4_K_M.gguf").Path;
        string samples = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "..", "docs", "templates");
        var templates = new[]
        {
            TemplateLibrary.ParseMarkdown(File.ReadAllText(Path.Combine(samples, "sample-project-meeting.md")), "project"),
            TemplateLibrary.ParseJson(File.ReadAllText(Path.Combine(samples, "sample-one-on-one.json"))),
        };
        var report = new StringBuilder("# テンプレート × 事実台帳（架空）\n");
        foreach (var (template, meeting) in new[] { (templates[0], EvaluationMeetings.Switching), (templates[0], EvaluationMeetings.Long), (templates[1], EvaluationMeetings.Noisy) })
        {
            using var model = new LlamaTextGenerator(path);
            var watch = Stopwatch.StartNew();
            var draft = await new MinutesGenerator(model, MinutesStyle.Facts, TemplateLibrary.Validate(template, "t-probe")).GenerateAsync(meeting.Note, meeting.Title, DateTime.Now);
            report.Append($"\n## {template.Name} × {meeting.Name}（{watch.Elapsed.TotalSeconds:0} 秒、{draft.Run!.InputMode}）\n\n```markdown\n{draft.Markdown[..MinutesValidator.AppendixStart(draft.Markdown)]}\n```\n");
        }

        string file = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", $"template-probe-{DateTime.Now:yyyyMMdd-HHmm}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await File.WriteAllTextAsync(file, report.ToString());
        output.WriteLine(file);
        output.WriteLine(report.ToString());
    }
}
