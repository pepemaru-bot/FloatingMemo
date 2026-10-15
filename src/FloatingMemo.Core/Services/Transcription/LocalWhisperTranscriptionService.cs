using System.Text;
using System.Text.RegularExpressions;
using FloatingMemo.Services.Acceleration;
using Whisper.net;
using Whisper.net.LibraryLoader;

namespace FloatingMemo.Services.Transcription;

/// <summary>
/// Offline speech recognition with Whisper.net (whisper.cpp) and a local GGML model file.
/// </summary>
/// <remarks>
/// - Never downloads anything: the user puts the model on disk (see README, "AIモデルの手動セットアップ").
/// - The model is loaded lazily on first use, so app start-up and the hotkey stay fast,
///   and is then kept in memory for later recordings.
/// - Inference runs on a background thread; calls are serialized.
/// </remarks>
public sealed partial class LocalWhisperTranscriptionService : ITranscriptionService, IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _language;
    private readonly int _threads;
    private WhisperFactory? _factory;
    private bool _disposed;

    /// <param name="modelPath">Path to a GGML Whisper model, e.g. ggml-base.bin or ggml-small.bin.</param>
    /// <param name="language">Two-letter code ("ja") or "auto" for detection.</param>
    public LocalWhisperTranscriptionService(string modelPath, string language = "ja")
    {
        ModelPath = modelPath;
        _language = language;
        // Leave half the cores free so the PC (and this app's UI) stays responsive.
        _threads = Math.Max(1, Environment.ProcessorCount / 2);
    }

    public string ModelPath { get; }

    /// <summary>Whisper models the app picks up by itself, best first (the user downloads them).</summary>
    public static readonly string[] KnownModels = ["ggml-base.bin", "ggml-small.bin"];

    /// <summary>
    /// The model to use when none is specified: the first known model found in the folders of
    /// <see cref="ModelLocations"/>; if there is none, where ggml-base.bin is expected to be put.
    /// </summary>
    public static string DefaultModelPath => ResolveDefaultModelPath(ModelLocations.AppModelsDirectory, ModelLocations.UserModelsDirectory);

    internal static string ResolveDefaultModelPath(string appModels, string userModels)
    {
        foreach (string name in KnownModels)
        {
            foreach (var (dir, _) in ModelLocations.SearchDirectories(ModelLocations.WhisperFolder, appModels, userModels))
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }

        return Path.Combine(ModelLocations.RecommendedDirectory(ModelLocations.WhisperFolder, appModels, userModels), KnownModels[0]);
    }

    /// <summary>False until the first transcription has loaded the model.</summary>
    public bool IsModelLoaded => _factory is not null;

    /// <summary>
    /// Hallucination counter-measures (all on by default; switchable for the benchmark).
    /// </summary>
    public WhisperPipelineOptions Options { get; init; } = WhisperPipelineOptions.Default;

    /// <summary>
    /// Optional recognition hint (e.g. "用語: 共通テスト、総合型選抜。") from the user's dictionary:
    /// only the words the user marked as Whisper hints. Null or empty = no hint (the default).
    /// </summary>
    public Func<string?>? HintProvider { get; set; }

    /// <summary>Diagnostics of the last transcription (no text): regions, removed repeats, timing.</summary>
    public TranscriptionDiagnostics? LastDiagnostics { get; private set; }

    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken = default, IProgress<double>? progress = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WhisperFactory factory = await EnsureModelLoadedAsync(cancellationToken).ConfigureAwait(false);
            var started = System.Diagnostics.Stopwatch.StartNew();

            // 16 kHz mono float samples (the recorder already produces that format).
            float[] samples = await Task.Run(() => AudioPreparationReader.ReadMono16k(audio), cancellationToken).ConfigureAwait(false);
            const int rate = Audio.AudioPreparation.TargetSampleRate;

            // Only stretches with speech go to Whisper: long silence is where loops start.
            IReadOnlyList<SpeechRegion> speech = SpeechActivity.Detect(samples, rate);
            IReadOnlyList<SpeechRegion> regions = Options.SkipSilence
                ? SpeechActivity.GroupIntoChunks(speech, rate)
                : [new SpeechRegion(0, samples.Length)];

            var builder = factory.CreateBuilder().WithThreads(_threads);
            builder = _language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(_language);
            if (Options.NoContext)
            {
                // Do not feed the previous window's text back in as a prompt: one hallucinated
                // phrase would otherwise be "continued" window after window.
                builder = builder.WithNoContext();
            }

            // The hint is given as Whisper's "initial prompt" (text it believes came just before),
            // which makes those spellings more likely. Echoes of the hint itself are dropped below.
            string? hint = HintProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(hint))
            {
                builder = builder.WithPrompt(hint);
            }

            // Progress = speech already processed + whisper.cpp's own percentage for the current
            // stretch, over all the speech that will be processed. Silence is skipped, so it is
            // not counted.
            var work = regions.Where(r => r.Length >= rate / 4).ToList();
            double total = Math.Max(1, work.Sum(r => (double)r.Length));
            double done = 0, current = 0;
            int lastReported = -1;
            void Report(double fraction)
            {
                int percent = (int)(Math.Clamp(fraction, 0, 1) * 100);
                if (percent != lastReported)
                {
                    lastReported = percent;
                    progress?.Report(percent / 100.0);
                }
            }

            if (progress is not null)
            {
                builder = builder.WithProgressHandler(p => Report((done + (current * p / 100.0)) / total));
            }

            Report(0);
            var segments = new List<TranscriptSegment>();
            await using (var processor = builder.Build())
            {
                foreach (var region in work)
                {
                    current = region.Length;

                    var offset = region.Start(rate);
                    var chunk = new ReadOnlyMemory<float>(samples, region.StartSample, region.Length);
                    // ProcessAsync runs whisper.cpp on a worker thread and yields segments as they finish.
                    await foreach (var segment in processor.ProcessAsync(chunk, cancellationToken).ConfigureAwait(false))
                    {
                        string text = CleanSegment(segment.Text);
                        if (text.Length > 0 && !IsHintEcho(text, hint))
                        {
                            segments.Add(new TranscriptSegment(segment.Start + offset, segment.End + offset, text));
                        }
                    }

                    done += region.Length;
                    current = 0;
                    Report(done / total);
                }
            }

            Report(1);

            int rawCount = segments.Count;
            int removed = 0;
            if (Options.GuardRepetitions)
            {
                segments = RepetitionGuard.Apply(segments,
                    (from, to) => SpeechActivity.SpeechCoverage(speech, from, to, rate), out removed);
            }

            LastDiagnostics = new TranscriptionDiagnostics(
                TimeSpan.FromSeconds((double)samples.Length / rate),
                TimeSpan.FromSeconds(speech.Sum(r => (double)r.Length) / rate),
                regions.Count, rawCount, removed, started.Elapsed);
            return new TranscriptionResult(JoinSegments(segments.Select(s => s.Text)), segments);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TranscriptionException)
        {
            throw new TranscriptionException($"文字起こしに失敗しました（{ex.Message.Trim()}）", ex);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<WhisperFactory> EnsureModelLoadedAsync(CancellationToken cancellationToken)
    {
        if (_factory is not null)
        {
            return _factory;
        }

        if (!File.Exists(ModelPath))
        {
            throw new TranscriptionException(
                $"Whisperモデルが見つかりません: {ModelPath}\n公式配布元から {KnownModels[0]} をダウンロードして、このフォルダに置いてください（手順は README を参照）。")
            {
                IsModelMissing = true,
            };
        }

        // Loading reads ~150 MB (base) from disk; keep it off the UI thread.
        // GPU (Vulkan) when the policy says so; if that fails to start, the CPU — never an error.
        var choice = AccelerationPolicy.ForWhisper(AccelerationState.Mode, AccelerationState.Adapters);
        try
        {
            _factory = await Task.Run(() => WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions { UseGpu = choice.UseGpu }), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (choice.UseGpu && ex is not OperationCanceledException)
        {
            _factory = await Task.Run(() => WhisperFactory.FromPath(ModelPath, new WhisperFactoryOptions { UseGpu = false }), cancellationToken).ConfigureAwait(false);
            choice = choice with { UseGpu = false, Backend = "CPU", Reason = "GPU で開始できず CPU に切り替え" };
        }

        // Which native library was really loaded (Vulkan or CPU) is what the report shows.
        string loaded = RuntimeOptions.LoadedLibrary?.ToString() ?? "unknown";
        AccelerationState.Record(choice with { Backend = $"{(choice.UseGpu ? "GPU" : "CPU")} / {loaded}" });
        return _factory;
    }

    /// <summary>Removes Whisper's non-speech markers such as "[BLANK_AUDIO]" or "(音楽)".</summary>
    /// <summary>A segment that only repeats the hint (Whisper sometimes "hears" its prompt in noise).</summary>
    internal static bool IsHintEcho(string text, string? hint) =>
        !string.IsNullOrWhiteSpace(hint) && hint.Replace(" ", string.Empty).Contains(text.Replace(" ", string.Empty).TrimEnd('。', '、'), StringComparison.Ordinal);

    internal static string CleanSegment(string text)
    {
        string trimmed = text.Trim();
        return NonSpeechMarker().IsMatch(trimmed) ? string.Empty : trimmed;
    }

    /// <summary>
    /// Joins segments. Japanese/CJK text is joined without spaces; other scripts get one space.
    /// </summary>
    internal static string JoinSegments(IEnumerable<string> segments)
    {
        var sb = new StringBuilder();
        foreach (string segment in segments)
        {
            if (sb.Length > 0 && !(IsCjk(sb[^1]) || IsCjk(segment[0])))
            {
                sb.Append(' ');
            }

            sb.Append(segment);
        }

        return sb.ToString();
    }

    private static bool IsCjk(char c) =>
        c is >= '　' and <= 'ヿ'   // CJK punctuation, Hiragana, Katakana
            or >= '一' and <= '鿿' // CJK unified ideographs
            or >= '＀' and <= '￯'; // full-width forms

    [GeneratedRegex(@"^(\[[^\]]*\]|\([^\)]*\)|（[^）]*）|♪+)$")]
    private static partial Regex NonSpeechMarker();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Wait();
        try
        {
            _factory?.Dispose();
            _factory = null;
        }
        finally
        {
            _gate.Release();
        }
    }
}
