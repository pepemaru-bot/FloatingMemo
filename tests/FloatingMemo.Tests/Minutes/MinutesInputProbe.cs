using System.Diagnostics;
using System.Text;
using FloatingMemo.Services.Minutes;
using LLama;
using LLama.Common;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// Investigation (FLOATINGMEMO_LLM=1 only): how the current minutes pipeline handles its input.
/// Counts Qwen3 tokens of what is actually sent (system prompt, parts of the transcript, the
/// answer budget) against the 8192-token context, and runs the pipeline on the made-up long
/// meeting to see which topics survive. Reports go to %TEMP%\FloatingMemoTests; no prompt is changed.
/// </summary>
public sealed class MinutesInputProbe(ITestOutputHelper output)
{
    private const string ModelFile = "Qwen3-1.7B-Q8_0.gguf"; // same tokenizer as Qwen3-4B

    private static string? ModelPath(string name) =>
        LlamaTextGenerator.AvailableModels().FirstOrDefault(m => m.FileName == name)?.Path;

    [LlmFact]
    public void TokenBudget()
    {
        string path = ModelPath(ModelFile) ?? throw new InvalidOperationException("model missing");
        using var weights = LLamaWeights.LoadFromFile(new ModelParams(path) { ContextSize = 512, GpuLayerCount = 0 });
        int Tokens(string text) => weights.Tokenize(text, true, true, Encoding.UTF8).Length;

        var report = new StringBuilder("# 議事録の入力とトークン数（Qwen3 トークナイザー）\n\n");
        string sample = LongMeeting.Transcript;
        report.Append($"- 架空の長い会議: {sample.Length} 文字 = {Tokens(sample)} トークン（1 文字あたり {(double)Tokens(sample) / sample.Length:0.00}）\n");
        report.Append($"- 議事録の指示（system）: {Tokens(MinutesPrompt.System)} トークン、校正の指示: {Tokens(TranscriptCorrector.System)} トークン\n\n");
        report.Append("| 処理 | 部分 | 文字数 | 入力トークン（指示＋本文、ChatML 込み） | 回答の上限 | 合計 | コンテキスト 8192 に対して |\n|---|---|---|---|---|---|---|\n");

        var chunks = MinutesPrompt.Split(LongMeeting.Note, MinutesGenerator.ChunkCharacters);
        for (int i = 0; i < chunks.Count; i++)
        {
            int minutes = Tokens(LlamaTextGenerator.ChatPrompt(MinutesPrompt.System, MinutesPrompt.User(chunks[i], i, chunks.Count)));
            int correction = Tokens(LlamaTextGenerator.ChatPrompt(TranscriptCorrector.System, TranscriptCorrector.User(chunks[i], [])));
            report.Append($"| 議事録 | {i + 1}/{chunks.Count} | {chunks[i].Length} | {minutes} | {MinutesPrompt.MaxAnswerTokens} | {minutes + MinutesPrompt.MaxAnswerTokens} | {(minutes + MinutesPrompt.MaxAnswerTokens <= 8192 ? "収まる" : "超える")} |\n");
            report.Append($"| 校正 | {i + 1}/{chunks.Count} | {chunks[i].Length} | {correction} | {TranscriptCorrector.MaxAnswerTokens} | {correction + TranscriptCorrector.MaxAnswerTokens} | {(correction + TranscriptCorrector.MaxAnswerTokens <= 8192 ? "収まる" : "超える")} |\n");
        }

        // The worst case of a part: ChunkCharacters of dense speech without filler.
        string dense = string.Concat(Enumerable.Repeat("森さんが来週の水曜日までにテスト計画を作ることになりました。", 400))[..MinutesGenerator.ChunkCharacters];
        int worst = Tokens(LlamaTextGenerator.ChatPrompt(MinutesPrompt.System, MinutesPrompt.User(dense, 0, 2)));
        report.Append($"| 議事録（最悪の例: 6000 文字の密な文） | - | {dense.Length} | {worst} | {MinutesPrompt.MaxAnswerTokens} | {worst + MinutesPrompt.MaxAnswerTokens} | {(worst + MinutesPrompt.MaxAnswerTokens <= 8192 ? "収まる" : "超える")} |\n");
        Write("minutes-tokens", report.ToString());
    }

    /// <summary>The current pipeline on the long meeting: which topics, decisions and tasks survive.</summary>
    [LlmFact]
    public async Task CurrentPipelineOnTheLongMeeting()
    {
        string path = ModelPath(Environment.GetEnvironmentVariable("FLOATINGMEMO_LLM_MODEL") ?? "Qwen3-4B-Q4_K_M.gguf") ?? throw new InvalidOperationException("model missing");
        using var model = new LlamaTextGenerator(path);
        var answers = new RecordingGenerator(model);
        var watch = Stopwatch.StartNew();
        var draft = await new MinutesGenerator(answers).GenerateAsync(LongMeeting.Note, "アプリ開発定例（架空）", new DateTime(2026, 10, 15, 9, 0, 0));
        watch.Stop();

        var report = new StringBuilder($"# 現在の議事録処理と長い会議（{Path.GetFileName(path)}、{watch.Elapsed.TotalSeconds:0} 秒）\n\n");
        report.Append(Coverage.Describe(draft.Markdown));
        report.Append("\n## モデルの生の回答\n");
        foreach (var (prompt, answer, tokens) in answers.Calls)
        {
            report.Append($"\n### 呼び出し（回答 約 {tokens} 文字、上限到達の可能性: {(answer.TrimEnd().EndsWith('）') || answer.TrimEnd().EndsWith("なし") ? "低" : "要確認")}）\n\n```\n{answer}\n```\n");
        }

        report.Append("\n## 下書き\n\n```markdown\n").Append(draft.Markdown).Append("\n```\n");
        Write("minutes-current-long", report.ToString());
    }

    private void Write(string name, string text)
    {
        string file = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.md");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
        output.WriteLine(file);
        output.WriteLine(text);
    }
}

/// <summary>Passes calls through to a real model and keeps the raw answers for the report.</summary>
public sealed class RecordingGenerator(ILocalTextGenerator inner) : ILocalTextGenerator
{
    public List<(string Prompt, string Answer, int Characters)> Calls { get; } = [];

    public string ModelName => inner.ModelName;

    public async Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
    {
        string answer = await inner.CompleteAsync(system, user, maxTokens, cancellationToken, progress);
        Calls.Add((user, answer, answer.Length));
        return answer;
    }
}

/// <summary>What of <see cref="LongMeeting"/> a draft keeps (body only, not the correction appendix).</summary>
public static class Coverage
{
    public static string Body(string markdown)
    {
        int appendix = MinutesValidator.AppendixStart(markdown);
        string body = appendix >= 0 ? markdown[..appendix] : markdown;
        int start = body.IndexOf("\n## ", StringComparison.Ordinal);
        return start >= 0 ? body[start..] : body;
    }

    public static string Describe(string markdown)
    {
        string body = Body(markdown);
        var sb = new StringBuilder("| 議題 | 結果 | 触れている | 必要な語 |\n|---|---|---|---|\n");
        int covered = 0, complete = 0;
        foreach (var t in LongMeeting.Topics)
        {
            bool mentioned = t.Keywords.Any(k => body.Contains(k, StringComparison.Ordinal));
            bool details = t.MustAppear.All(w => w.Split('|').Any(alt => body.Contains(alt, StringComparison.Ordinal)));
            covered += mentioned ? 1 : 0;
            complete += mentioned && details ? 1 : 0;
            sb.Append($"| {t.Name} | {t.Outcome} | {(mentioned ? "○" : "×")} | {(t.MustAppear.Length == 0 ? "-" : details ? "○" : "×")} |\n");
        }

        sb.Append($"\n議題 {covered}/{LongMeeting.Topics.Length} に触れ、必要な語まで含むのは {complete}/{LongMeeting.Topics.Length}。");
        bool usesNotes = body.Contains("三百万", StringComparison.Ordinal);
        sb.Append($" 人のメモにだけある情報（予算の上限 三百万円）: {(usesNotes ? "使われた" : "使われていない")}。\n");
        return sb.ToString();
    }
}
