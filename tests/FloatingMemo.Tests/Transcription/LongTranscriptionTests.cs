using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using FloatingMemo.Services.Transcription;
using NAudio.Wave;

namespace FloatingMemo.Tests.Transcription;

/// <summary>
/// Windowed transcription (memory stays small for hours of audio) and resuming after a failure,
/// with short windows so a two-minute synthetic recording crosses several boundaries.
/// Synthetic Windows voice only; no recorded human speech.
/// </summary>
public sealed class LongTranscriptionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", "longtx-" + Guid.NewGuid().ToString("N"));

    public LongTranscriptionTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private float[] Speak(string sentence)
    {
        string path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".wav");
        using (var synth = new SpeechSynthesizer())
        {
            synth.SelectVoice(RequiresWhisperModelFactAttribute.JapaneseVoice!);
            synth.SetOutputToWaveFile(path, new SpeechAudioFormatInfo(16_000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(sentence);
            synth.SetOutputToNull();
        }

        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var samples = new float[reader.SampleCount];
        int read = provider.Read(samples.AsSpan());
        return samples[..read];
    }

    /// <summary>Speech and silence laid out on a timeline; returns the WAV path.</summary>
    private string Compose(params (double AtSeconds, float[] Speech)[] parts)
    {
        const int rate = 16_000;
        int length = parts.Max(p => (int)(p.AtSeconds * rate) + p.Speech.Length) + rate;
        var all = new float[length];
        foreach (var (at, speech) in parts)
        {
            speech.CopyTo(all, (int)(at * rate));
        }

        string path = Path.Combine(_dir, "meeting.wav");
        using var writer = new WaveFileWriter(path, new WaveFormat(rate, 16, 1));
        writer.WriteSamples(all, 0, all.Length);
        return path;
    }

    [RequiresWhisperModelFact]
    public async Task LongAudio_IsTranscribedWindowByWindow_InOrder_WithoutSplittingSpeechAtABoundary()
    {
        var first = Speak("最初に、会場の予約について話しました。");
        var second = Speak("次に、参加費は五百円にすることに決まりました。");
        var third = Speak("最後に、資料は金曜日までに送ります。");
        // Windows are 30 s; the second sentence starts 1.5 s before the first boundary and crosses it.
        string wav = Compose((2, first), (30 - 1.5, second), (75, third));
        string checkpoint = Path.Combine(_dir, TranscriptionCheckpoint.FileName);
        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja") { WindowLength = TimeSpan.FromSeconds(30) };

        TranscriptionResult result;
        await using (var audio = File.OpenRead(wav))
        {
            result = await service.TranscribeAsync(audio, CancellationToken.None, null, checkpoint);
        }

        string text = result.Text;
        Assert.Contains("会場", text);
        Assert.Contains("五百円", text.Replace("500", "五百"));
        Assert.Contains("金曜日", text);
        Assert.True(text.IndexOf("会場", StringComparison.Ordinal) < text.IndexOf("金曜日", StringComparison.Ordinal)); // original order
        Assert.True(result.Segments.Zip(result.Segments.Skip(1)).All(p => p.First.Start <= p.Second.Start)); // positions kept
        var saved = TranscriptionCheckpoint.Load(checkpoint, Path.GetFileName(service.ModelPath), (long)(Compose_Length(wav) * 16_000));
        Assert.NotNull(saved);
        Assert.Equal(saved!.TotalSamples, saved.ProcessedSamples); // every window final
    }

    [RequiresWhisperModelFact]
    public async Task Retry_ContinuesAfterTheLastFinishedWindow_KeepingWhatWasDone()
    {
        var first = Speak("最初に、会場の予約について話しました。");
        var third = Speak("最後に、資料は金曜日までに送ります。");
        string wav = Compose((2, first), (45, third));
        string checkpointPath = Path.Combine(_dir, TranscriptionCheckpoint.FileName);
        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja") { WindowLength = TimeSpan.FromSeconds(30) };

        // As if an earlier attempt finished the first window and then failed.
        long total = (long)Math.Round(Compose_Length(wav) * 16_000);
        new TranscriptionCheckpoint
        {
            Model = Path.GetFileName(service.ModelPath),
            TotalSamples = total,
            ProcessedSamples = 30 * 16_000,
            Segments = [new CheckpointSegment { StartMs = 2000, EndMs = 5000, Text = "（前回の結果）" }],
        }.TrySave(checkpointPath);

        TranscriptionResult result;
        await using (var audio = File.OpenRead(wav))
        {
            result = await service.TranscribeAsync(audio, CancellationToken.None, null, checkpointPath);
        }

        Assert.StartsWith("（前回の結果）", result.Text); // kept, first, not transcribed again
        Assert.DoesNotContain("会場", result.Text);
        Assert.Contains("金曜日", result.Text);
    }

    [Fact]
    public void Checkpoint_IsIgnoredForOtherAudioOrAnotherModel()
    {
        string path = Path.Combine(_dir, TranscriptionCheckpoint.FileName);
        new TranscriptionCheckpoint { Model = "ggml-base.bin", TotalSamples = 1000, ProcessedSamples = 500 }.TrySave(path);

        Assert.NotNull(TranscriptionCheckpoint.Load(path, "ggml-base.bin", 1000));
        Assert.Null(TranscriptionCheckpoint.Load(path, "ggml-small.bin", 1000)); // another model: start again
        Assert.Null(TranscriptionCheckpoint.Load(path, "ggml-base.bin", 2000)); // another recording
        File.WriteAllText(path, "{ broken");
        Assert.Null(TranscriptionCheckpoint.Load(path, "ggml-base.bin", 1000)); // damaged: start again, no error
    }

    private static double Compose_Length(string wav)
    {
        using var reader = new WaveFileReader(wav);
        return reader.TotalTime.TotalSeconds;
    }

    /// <summary>
    /// FLOATINGMEMO_LONGPROBE=1 only: a 3-hour recording (mostly silence, a sentence every
    /// 10 minutes) to measure peak memory of the windowed transcription. Writes ~350 MB to %TEMP%.
    /// </summary>
    [RequiresWhisperModelFact]
    public async Task Probe_ThreeHours_PeakMemory()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_LONGPROBE") != "1")
        {
            return;
        }

        var sentence = Speak("この区間では、議事録の確認をしました。");
        string path = Path.Combine(_dir, "three-hours.wav");
        using (var writer = new WaveFileWriter(path, new WaveFormat(16_000, 16, 1)))
        {
            var silence = new float[16_000 * 60];
            for (int minute = 0; minute < 180; minute++)
            {
                if (minute % 10 == 5)
                {
                    writer.WriteSamples(sentence, 0, sentence.Length);
                }

                writer.WriteSamples(silence, 0, silence.Length);
            }
        }

        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja");
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        long peak = 0;
        using var timer = new Timer(_ => { process.Refresh(); peak = Math.Max(peak, process.WorkingSet64); }, null, 0, 200);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        TranscriptionResult result;
        await using (var audio = File.OpenRead(path))
        {
            result = await service.TranscribeAsync(audio, CancellationToken.None, null, Path.Combine(_dir, TranscriptionCheckpoint.FileName));
        }

        File.AppendAllText(Path.Combine(Path.GetTempPath(), "FloatingMemoTests", "long-probe.txt"),
            $"{DateTime.Now:s} 3h audio: {watch.Elapsed.TotalSeconds:0}s, peak working set {peak / 1_048_576} MB, segments {result.Segments.Count}" + Environment.NewLine);
        Assert.True(result.Segments.Count >= 15);
    }
}
