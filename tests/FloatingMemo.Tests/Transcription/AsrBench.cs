using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;
using NAudio.Wave;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Transcription;

/// <summary>
/// FLOATINGMEMO_ASR=1 only: the made-up meeting audio of tests/asr/make-asr-audio.ps1
/// (FLOATINGMEMO_ASR_AUDIO = its folder) through the app's Whisper pipeline with several settings:
/// speech detection (built-in / Silero VAD / none), the dictionary hint (off / on / for every
/// window), temperature, clean and with noise. Reports CER, dictionary terms and numbers written
/// right, repeats, missing statements and time. FLOATINGMEMO_SILERO = the Silero model file.
/// </summary>
public sealed partial class AsrBench(ITestOutputHelper output)
{
    private static readonly string[] Terms = ["架空ホール", "緑川物流", "白石製作所", "稟議書", "青葉商事", "中央工務店"];
    private static readonly string Hint = "用語: " + string.Join("、", Terms) + "。";

    [Fact]
    public async Task CompareSettings()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR") != "1")
        {
            return;
        }

        string dir = Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_AUDIO")!;
        string truth = File.ReadAllText(Path.Combine(dir, "truth.txt"));
        string silero = Environment.GetEnvironmentVariable("FLOATINGMEMO_SILERO") ?? string.Empty;
        string model = Path.Combine(Services.ModelLocations.UserModelsDirectory, Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_MODEL") ?? "ggml-small.bin");
        Services.Acceleration.AccelerationState.Initialize(Services.Acceleration.AccelerationMode.Auto);
        string clean = Path.Combine(dir, "meeting.wav");
        string noisy = Path.Combine(dir, "meeting-noise.wav");
        AddNoise(clean, noisy, 0.02f);

        (string Name, WhisperPipelineOptions Options, bool UseHint)[] settings =
        [
            ("現行の発話検出・ヒントなし", WhisperPipelineOptions.Default, false),
            ("現行の発話検出・ヒントあり（現行）", WhisperPipelineOptions.Default, true),
            ("現行の発話検出・ヒントを全区間に", WhisperPipelineOptions.Default with { CarryInitialPrompt = true }, true),
            ("Silero VAD・ヒントなし", WhisperPipelineOptions.Default with { SileroVadPath = silero }, false),
            ("Silero VAD・ヒントあり", WhisperPipelineOptions.Default with { SileroVadPath = silero }, true),
            ("発話検出なし・ヒントあり", WhisperPipelineOptions.Default with { SkipSilence = false }, true),
            ("発話検出なし・ヒントを全区間に", WhisperPipelineOptions.Default with { SkipSilence = false, CarryInitialPrompt = true }, true),
            ("現行・ヒントあり・温度0", WhisperPipelineOptions.Default with { Temperature = 0f }, true),
        ];

        var sb = new StringBuilder($"# 文字起こしの比較（{Path.GetFileName(model)}、架空の会議音声 {Duration(clean):F0} 秒、{DateTime.Now:yyyy-MM-dd HH:mm}）\n\n");
        sb.Append("| 設定 | 音声 | CER | 用語 | 数値 | 繰り返し | 欠落した発言 | 後半の用語 | 時間 |\n|---|---|---|---|---|---|---|---|---|\n");
        foreach (var (file, label) in new[] { (clean, "雑音なし"), (noisy, "雑音あり") })
        {
            foreach (var (name, options, useHint) in settings)
            {
                using var service = new LocalWhisperTranscriptionService(model, "ja") { Options = options };
                if (useHint)
                {
                    service.HintProvider = () => Hint;
                }

                await using var audio = File.OpenRead(file);
                var watch = Stopwatch.StartNew();
                var result = await service.TranscribeAsync(audio);
                watch.Stop();
                string text = result.Text;
                File.WriteAllText(Path.Combine(dir, $"out-{label}-{name}.txt"), text);
                var s = Score(truth, text);
                sb.Append($"| {name} | {label} | {s.Cer:P1} | {s.Terms}/{s.TermsTotal} | {s.Numbers}/{s.NumbersTotal} | {s.Repeats + (service.LastDiagnostics?.RepeatsRemoved ?? 0)} | "
                    + $"{s.Missing}/{s.Statements} | {s.LateTerms}/2 | {watch.Elapsed.TotalSeconds:F0}s |\n");
            }
        }

        File.WriteAllText(Path.Combine(dir, "scores.md"), sb.ToString());
        output.WriteLine(sb.ToString());
    }

    private static double Duration(string wav)
    {
        using var r = new WaveFileReader(wav);
        return r.TotalTime.TotalSeconds;
    }

    // White noise at a fixed level over the whole recording (a fan, a noisy room).
    private static void AddNoise(string from, string to, float level)
    {
        using var reader = new WaveFileReader(from);
        using var writer = new WaveFileWriter(to, reader.WaveFormat);
        var random = new Random(7);
        var buffer = new byte[reader.WaveFormat.AverageBytesPerSecond];
        int read;
        while ((read = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + 1 < read; i += 2)
            {
                float sample = BitConverter.ToInt16(buffer, i) / 32768f + (((float)random.NextDouble() * 2) - 1) * level;
                short value = (short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue);
                buffer[i] = (byte)(value & 0xFF);
                buffer[i + 1] = (byte)((value >> 8) & 0xFF);
            }

            writer.Write(buffer, 0, read);
        }
    }

    public sealed record AsrScore(double Cer, int Terms, int TermsTotal, int Numbers, int NumbersTotal, int Repeats, int Missing, int Statements, int LateTerms);

    [GeneratedRegex(@"[\s、。,.!?！？「」『』・]")]
    private static partial Regex Punctuation();

    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Digits();

    // "八十万円" and "80万円" are the same number: both become 800000.
    private static string Plain(string text) => Punctuation().Replace(
        Numerals.Arabic(TenThousands().Replace(text.Normalize(NormalizationForm.FormKC), m => (long.Parse(m.Groups[1].Value) * 10000).ToString())), string.Empty);

    [GeneratedRegex(@"([0-9]+)万")]
    private static partial Regex TenThousands();

    public static AsrScore Score(string truth, string text)
    {
        string t = Plain(truth), h = Plain(text);
        double cer = t.Length == 0 ? 0 : Distance(t, h) / (double)t.Length;
        int Count(string s, string w) => Regex.Matches(s, Regex.Escape(w)).Count;
        int termsTotal = Terms.Sum(w => Count(t, w));
        int terms = Terms.Sum(w => Math.Min(Count(t, w), Count(h, w)));
        var numbers = Digits().Matches(t).Select(m => m.Value).ToList();
        int numbersFound = numbers.Count(n => h.Contains(n, StringComparison.Ordinal));
        // Repeats: a sentence written twice in a row.
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        int repeats = lines.Zip(lines.Skip(1)).Count(p => p.First.Length > 4 && p.First == p.Second);
        // Missing: a statement of which fewer than half of the character pairs appear.
        var statements = truth.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Plain).ToList();
        int missing = statements.Count(s => Pairs(s).Count(p => h.Contains(p, StringComparison.Ordinal)) * 2 < Pairs(s).Count);
        // The terms at the end of the long stretch (more than 30 s into it).
        int late = new[] { "白石製作所", "中央工務店" }.Count(w => h.Contains(w, StringComparison.Ordinal));
        return new AsrScore(cer, terms, termsTotal, numbersFound, numbers.Count, repeats, missing, statements.Count, late);
    }

    private static List<string> Pairs(string s) => Enumerable.Range(0, Math.Max(0, s.Length - 1)).Select(i => s.Substring(i, 2)).ToList();

    private static int Distance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++)
        {
            prev[j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }

    [Fact]
    public void Score_CountsTermsNumbersAndMissingStatements()
    {
        var s = Score("会場は架空ホールです。\n費用は八十万円です。", "会場は加工ホールです。\n費用は80万円です。");
        Assert.Equal(0, s.Terms);
        Assert.Equal(1, s.Numbers);
        Assert.Equal(0, s.Missing);
        Assert.True(s.Cer is > 0 and < 0.3);
    }
}
