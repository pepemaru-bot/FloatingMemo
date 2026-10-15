using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using FloatingMemo.Services.Acceleration;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;
using FloatingMemo.Tests.Minutes;
using FloatingMemo.Tests.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Settings;

/// <summary>
/// FLOATINGMEMO_LLM=1 only: the same made-up workload with FLOATINGMEMO_ACCEL = CpuOnly / Auto /
/// PreferGpu (one mode per test process: native libraries are chosen once per process).
/// Measures Whisper on synthesized speech and Qwen3 4B fact-first minutes on the long made-up
/// meeting, and prints which backend was used. The report names this PC's GPU, so it is written
/// to %TEMP% only and must not be committed.
/// </summary>
public sealed class AccelerationBenchmark(ITestOutputHelper output)
{
    [LlmFact]
    public async Task MeasureOneMode()
    {
        var mode = Enum.Parse<AccelerationMode>(Environment.GetEnvironmentVariable("FLOATINGMEMO_ACCEL") ?? "Auto");
        AccelerationState.Initialize(mode);
        var report = new StringBuilder($"# 処理方式の計測（{mode}、{DateTime.Now:yyyy-MM-dd HH:mm}）\n\n");

        // Whisper: about 60 s of synthesized Japanese speech.
        string wav = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", $"accel-speech-{Guid.NewGuid():N}.wav");
        Directory.CreateDirectory(Path.GetDirectoryName(wav)!);
        using (var synth = new SpeechSynthesizer())
        {
            synth.SelectVoice(RequiresWhisperModelFactAttribute.JapaneseVoice!);
            synth.SetOutputToWaveFile(wav, new SpeechAudioFormatInfo(16_000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(string.Concat(Enumerable.Repeat("本日の定例会議では、リリース日とテスト計画について話し合いました。資料の準備は金曜日までに行います。", 8)));
            synth.SetOutputToNull();
        }

        try
        {
            using var whisper = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja");
            var sw = Stopwatch.StartNew();
            await using (var audio = File.OpenRead(wav))
            {
                var first = await whisper.TranscribeAsync(audio);
                report.Append($"- Whisper 1 回目（読み込み込み）: {sw.Elapsed.TotalSeconds:0.0} 秒、{first.Text.Length} 文字\n");
            }

            sw.Restart();
            await using (var audio = File.OpenRead(wav))
            {
                await whisper.TranscribeAsync(audio);
                report.Append($"- Whisper 2 回目: {sw.Elapsed.TotalSeconds:0.0} 秒\n");
            }
        }
        finally
        {
            File.Delete(wav);
        }

        // Qwen3 4B, fact-first minutes of the long made-up meeting (no correction step).
        string? path = LlamaTextGenerator.AvailableModels().FirstOrDefault(m => m.FileName == "Qwen3-4B-Q4_K_M.gguf")?.Path;
        if (path is not null)
        {
            using var llm = new LlamaTextGenerator(path);
            var sw = Stopwatch.StartNew();
            var draft = await new MinutesGenerator(llm, MinutesStyle.Facts).GenerateAsync(LongMeeting.Note, "架空", DateTime.Now);
            report.Append($"- Qwen3 4B 議事録（事実抽出・長い架空会議）: {sw.Elapsed.TotalSeconds:0} 秒、呼び出し {draft.Run!.Calls.Count} 回\n");
        }

        report.Append("\n```\n").Append(AccelerationState.Report()).Append("\n```\n");
        string file = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", $"acceleration-{mode}-{DateTime.Now:yyyyMMdd-HHmm}.md");
        await File.WriteAllTextAsync(file, report.ToString());
        output.WriteLine(file);
        output.WriteLine(report.ToString());
    }
}
