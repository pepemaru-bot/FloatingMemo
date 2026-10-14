using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using FloatingMemo.Services.Audio;
using FloatingMemo.Services.Transcription;
using NAudio.Wave;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Transcription;

/// <summary>Runs only when FLOATINGMEMO_BENCH=1 (takes minutes) and the Whisper model is installed.</summary>
public sealed class BenchmarkFactAttribute : FactAttribute
{
    public BenchmarkFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_BENCH") != "1")
        {
            Skip = "Set FLOATINGMEMO_BENCH=1 to run the Whisper hallucination benchmark.";
        }
        else if (!File.Exists(RequiresWhisperModelFactAttribute.ModelPath) || RequiresWhisperModelFactAttribute.JapaneseVoice is null)
        {
            Skip = "Needs the Whisper model and a Japanese SAPI voice.";
        }
    }
}

/// <summary>
/// Hallucination benchmark: synthetic Japanese speech (Windows TTS, no real recordings) in the
/// situations listed in the phase C brief, transcribed with the old pipeline and the new one.
/// Writes a Markdown table to %TEMP%\FloatingMemoBench\report.md.
/// </summary>
public sealed class WhisperBenchmark : IDisposable
{
    private const int Rate = AudioPreparation.TargetSampleRate;
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoBench");

    public WhisperBenchmark(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
    }

    private const string S1 = "本日の定例会議を始めます。";
    private const string S2 = "最初に先週の進捗を確認しました。";
    private const string S3 = "次に来月のリリース計画について話し合いました。";
    private const string S4 = "担当者は金曜日までに作業を完了させる予定です。";

    private float[] Speak(string text)
    {
        using var synth = new SpeechSynthesizer();
        synth.SelectVoice(RequiresWhisperModelFactAttribute.JapaneseVoice!);
        using var ms = new MemoryStream();
        synth.SetOutputToAudioStream(ms, new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(text);
        synth.SetOutputToNull();
        byte[] pcm = ms.ToArray();
        var samples = new float[pcm.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BitConverter.ToInt16(pcm, i * 2) / 32768f;
        }

        return samples;
    }

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static float[] Noise(double seconds, float amplitude)
    {
        var random = new Random(42);
        return Enumerable.Range(0, (int)(seconds * Rate)).Select(_ => (float)((random.NextDouble() * 2 - 1) * amplitude)).ToArray();
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    /// <summary>Sum of two signals, b delayed by <paramref name="delaySeconds"/> (like mic + PC audio).</summary>
    private static float[] Mix(float[] a, float[] b, double delaySeconds, float gainB = 1f)
    {
        int delay = (int)(delaySeconds * Rate);
        var result = new float[Math.Max(a.Length, b.Length + delay)];
        for (int i = 0; i < a.Length; i++)
        {
            result[i] += a[i];
        }

        for (int i = 0; i < b.Length; i++)
        {
            result[i + delay] += b[i] * gainB;
        }

        for (int i = 0; i < result.Length; i++)
        {
            result[i] = Math.Clamp(result[i], -1f, 1f);
        }

        return result;
    }

    private MemoryStream Wav(float[] samples)
    {
        var ms = new MemoryStream();
        using (var writer = new WaveFileWriter(new IgnoreDisposeStream(ms), new WaveFormat(Rate, 16, 1)))
        {
            writer.WriteSamples(samples, 0, samples.Length);
        }

        ms.Position = 0;
        return ms;
    }

    private sealed record Case(string Name, float[] Audio, string? Expected, string Note);

    [BenchmarkFact]
    public async Task CompareBaselineAndImprovedPipelines()
    {
        float[] s1 = Speak(S1), s2 = Speak(S2), s3 = Speak(S3), s4 = Speak(S4);
        float[] longSpeech = Concat(s1, Silence(0.6), s2, Silence(0.6), s3, Silence(0.6), s4);
        float[] repeated = Speak("確認します。確認します。確認します。はい、はい、はい。");
        float[] other = Speak("資料は共有フォルダに置いてあります。");

        var cases = new List<Case>
        {
            new("完全な無音 (60s)", Silence(60), "", "何も出ないのが正解"),
            new("短い日本語", s1, S1, ""),
            new("長い日本語 (4文)", longSpeech, S1 + S2 + S3 + S4, ""),
            new("発言後の長い無音 (90s)", Concat(s1, Silence(90)), S1, "無音部分のループが典型的な症状"),
            new("雑音のみ (60s)", Noise(60, 0.03f), "", ""),
            new("実際の繰り返し", repeated, "確認します。確認します。確認します。はい、はい、はい。", "繰り返しを消してはいけない"),
            new("同時録音 (別話者が0.5s後)", Mix(longSpeech, Concat(Silence(8), other), 0.5), null, "マイク+PC音声の混合"),
            new("時間ずれ (同じ音声が0.25s遅れて重複)", Mix(longSpeech, longSpeech, 0.25, 0.6f), S1 + S2 + S3 + S4, "スピーカー音をマイクが拾った状態"),
            new("音声の重複 (同じ内容を2回)", Concat(s1, Silence(1), s1), S1 + S1, "2回出るのが正解"),
            new("長い録音の後半が無音 (発言→3分無音→発言)", Concat(s1, Silence(180), s2), S1 + S2, "長時間+途中の無音"),
        };

        var report = new StringBuilder();
        report.AppendLine("| ケース | 方式 | CER | 最大連続反復 | 区間数(生) | 除去 | 処理時間 | 出力(先頭60字) |");
        report.AppendLine("|---|---|---|---|---|---|---|---|");

        foreach (var options in new[] { ("従来", WhisperPipelineOptions.Baseline), ("改善後", WhisperPipelineOptions.Default) })
        {
            using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja") { Options = options.Item2 };
            await service.TranscribeAsync(Wav(s1)); // load the model once, outside the timings

            foreach (var c in cases)
            {
                long memoryBefore = Process.GetCurrentProcess().PrivateMemorySize64;
                var result = await service.TranscribeAsync(Wav(c.Audio));
                var d = service.LastDiagnostics!;
                string cer = c.Expected is null ? "-" : $"{Metrics.Cer(c.Expected, result.Text):P0}";
                int repeat = Metrics.MaxConsecutiveRepeat(result.Segments.Select(s => s.Text).ToList());
                string head = result.Text.Length > 60 ? result.Text[..60] + "…" : result.Text;
                report.AppendLine($"| {c.Name} | {options.Item1} | {cer} | {repeat} | {d.RawSegments} | {d.RepeatsRemoved} | {d.Elapsed.TotalSeconds:F1}s | {head.Replace("|", "/")} |");
                _output.WriteLine($"{options.Item1} / {c.Name}: CER={cer} repeat={repeat} raw={d.RawSegments} removed={d.RepeatsRemoved} {d.Elapsed.TotalSeconds:F1}s mem+{(Process.GetCurrentProcess().PrivateMemorySize64 - memoryBefore) / 1_000_000}MB :: {head}");
            }
        }

        File.WriteAllText(Path.Combine(_dir, "report.md"), report.ToString(), Encoding.UTF8);
        _output.WriteLine(report.ToString());
    }
}

/// <summary>Evaluation metrics for transcripts.</summary>
public static class Metrics
{
    /// <summary>Character error rate (edit distance / reference length), ignoring spaces and punctuation.</summary>
    public static double Cer(string reference, string hypothesis)
    {
        string r = Clean(reference), h = Clean(hypothesis);
        if (r.Length == 0)
        {
            return h.Length == 0 ? 0 : 1; // anything on silence/noise counts as fully wrong
        }

        var previous = new int[h.Length + 1];
        var current = new int[h.Length + 1];
        for (int j = 0; j <= h.Length; j++)
        {
            previous[j] = j;
        }

        for (int i = 1; i <= r.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= h.Length; j++)
            {
                int cost = r[i - 1] == h[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return (double)previous[h.Length] / r.Length;
    }

    /// <summary>Longest run of identical consecutive segments (1 = no repetition).</summary>
    public static int MaxConsecutiveRepeat(IReadOnlyList<string> segments)
    {
        int best = segments.Count > 0 ? 1 : 0, run = 1;
        for (int i = 1; i < segments.Count; i++)
        {
            run = Clean(segments[i]) == Clean(segments[i - 1]) ? run + 1 : 1;
            best = Math.Max(best, run);
        }

        return best;
    }

    private static string Clean(string s) =>
        new(s.Where(c => !char.IsWhiteSpace(c) && !char.IsPunctuation(c) && c is not ('、' or '。')).ToArray());
}

/// <summary>Keeps the MemoryStream open when the WaveFileWriter is disposed.</summary>
internal sealed class IgnoreDisposeStream(Stream inner) : Stream
{
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
    protected override void Dispose(bool disposing)
    {
    }
}
