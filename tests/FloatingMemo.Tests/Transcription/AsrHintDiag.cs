using FloatingMemo.Services.Transcription;
using Whisper.net;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Transcription;

/// <summary>
/// FLOATINGMEMO_ASR_DIAG=&lt;wav&gt;;&lt;hint&gt; only: the app's speech regions of a made-up recording (shorter
/// than one window) and Whisper's raw segments for each, run one after another on one processor as
/// the app does, with and without the hint, and whether the hint-echo filter would drop each segment.
/// Written to %TEMP%\fm-asr-diag.txt (to find out why a stretch disappears).
/// </summary>
public sealed class AsrHintDiag(ITestOutputHelper output)
{
    [Fact]
    public async Task RawSegments()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_DIAG") is not { Length: > 0 } spec)
        {
            return;
        }

        var parts = spec.Split(';');
        float[] all;
        using (var stream = File.OpenRead(parts[0]))
        {
            all = AudioPreparationReader.ReadMono16k(stream);
        }

        const int rate = 16000;
        var regions = SpeechActivity.GroupIntoChunks(SpeechActivity.Detect(all, rate), rate).Where(r => r.Length >= rate / 4).ToList();
        string model = Path.Combine(Services.ModelLocations.UserModelsDirectory, "ggml-small.bin");
        using var factory = WhisperFactory.FromPath(model);
        var lines = new List<string>();
        foreach (string? hint in new[] { parts[1], null })
        {
            var builder = factory.CreateBuilder().WithLanguage("ja").WithNoContext();
            if (hint is not null)
            {
                builder = builder.WithPrompt(hint);
            }

            lines.Add(hint is null ? "== no hint" : "== hint");
            await using var processor = builder.Build();
            foreach (var region in regions)
            {
                lines.Add($"-- region {region.Start(rate).TotalSeconds:F1}-{region.End(rate).TotalSeconds:F1}");
                await foreach (var s in processor.ProcessAsync(all.AsMemory(region.StartSample, region.Length)))
                {
                    string text = LocalWhisperTranscriptionService.CleanSegment(s.Text);
                    lines.Add($"{s.Start.TotalSeconds:F1}-{s.End.TotalSeconds:F1} echo={LocalWhisperTranscriptionService.IsHintEcho(text, hint)} [{s.Text}]");
                }
            }
        }

        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "fm-asr-diag.txt"), lines);
        lines.ForEach(output.WriteLine);
    }
}
