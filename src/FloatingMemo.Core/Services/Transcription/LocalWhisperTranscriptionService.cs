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
/// - The model is loaded lazily on first use, so app start-up and the hotkey stay fast; the
///   app releases it after each transcription job (<see cref="ReleaseModel"/>) so it never
///   shares the memory with the minutes' LLM.
/// - Inference runs on a background thread; calls are serialized.
/// </remarks>
public sealed partial class LocalWhisperTranscriptionService : ITranscriptionService, ISpeechRecognizer
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

    public string Name => "whisper";

    public string DisplayName => $"Whisper（{Path.GetFileNameWithoutExtension(ModelPath).Replace("ggml-", string.Empty)}）";

    public string? MissingFiles() => File.Exists(ModelPath) ? null
        : $"Whisperモデル（{KnownModels[0]}）がありません。README の手順でダウンロードして {Path.GetDirectoryName(ModelPath)} に置いてください。";

    public async Task<TranscriptionResult> TranscribeFileAsync(string wavPath, CancellationToken cancellationToken, IProgress<double>? progress, string? checkpointPath)
    {
        await using var stream = File.OpenRead(wavPath);
        return await TranscribeAsync(stream, cancellationToken, progress, checkpointPath).ConfigureAwait(false);
    }

    /// <summary>Whisper models the app picks up by itself, best first (the user downloads them).</summary>
    public static readonly string[] KnownModels = ["ggml-base.bin", "ggml-small.bin"];

    /// <summary>
    /// The model to use when none is specified: the first known model found in the folders of
    /// <see cref="ModelLocations"/>; if there is none, where ggml-base.bin is expected to be put.
    /// </summary>
    public static string DefaultModelPath => ResolveDefaultModelPath(ModelLocations.AppModelsDirectory, ModelLocations.UserModelsDirectory);

    /// <summary>The known Whisper models present in the model folders (first location wins), smallest first.</summary>
    public static IReadOnlyList<(string Name, string Path)> InstalledModels(string? appModels = null, string? userModels = null)
    {
        var found = new List<(string, string)>();
        foreach (string name in KnownModels)
        {
            foreach (var (dir, _) in ModelLocations.SearchDirectories(ModelLocations.WhisperFolder, appModels ?? ModelLocations.AppModelsDirectory, userModels ?? ModelLocations.UserModelsDirectory))
            {
                string path = Path.Combine(dir, name);
                if (File.Exists(path))
                {
                    found.Add((name, path));
                    break;
                }
            }
        }

        return found;
    }

    /// <summary>The model the user chose (a file name such as ggml-small.bin) if it is installed, otherwise the default.</summary>
    public static string ResolveModelPath(string? preferred, string? appModels = null, string? userModels = null) =>
        InstalledModels(appModels, userModels).FirstOrDefault(m => string.Equals(m.Name, preferred, StringComparison.OrdinalIgnoreCase)).Path
            ?? ResolveDefaultModelPath(appModels ?? ModelLocations.AppModelsDirectory, userModels ?? ModelLocations.UserModelsDirectory);

    /// <summary>A short description for the settings (speed vs accuracy).</summary>
    public static string Describe(string name) => name switch
    {
        "ggml-base.bin" => "base（速い・標準）",
        "ggml-small.bin" => "small（誤認識が少ない・処理は約 3 倍の時間）",
        _ => name,
    };

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
    /// Optional recognition hint (e.g. "用語: 稟議書、定例報告会。") from the user's dictionary:
    /// only the words the user marked as Whisper hints. Null or empty = no hint (the default).
    /// </summary>
    public Func<string?>? HintProvider { get; set; }

    /// <summary>Diagnostics of the last transcription (no text): regions, removed repeats, timing.</summary>
    public TranscriptionDiagnostics? LastDiagnostics { get; private set; }

    /// <summary>Audio is read and transcribed in windows of this length (memory stays small for hours of audio).</summary>
    public TimeSpan WindowLength { get; init; } = TimeSpan.FromMinutes(10);

    public Task<TranscriptionResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken = default, IProgress<double>? progress = null) =>
        TranscribeAsync(audio, cancellationToken, progress, checkpointPath: null);

    /// <param name="checkpointPath">
    /// Where finished windows are saved (next to the recording). A later call with the same audio
    /// and model continues after the last finished window. Null = no checkpoint.
    /// </param>
    public async Task<TranscriptionResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken, IProgress<double>? progress, string? checkpointPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            WhisperFactory factory = await EnsureModelLoadedAsync(cancellationToken).ConfigureAwait(false);
            var started = System.Diagnostics.Stopwatch.StartNew();
            const int rate = Audio.AudioPreparation.TargetSampleRate;

            // 16 kHz mono float samples, read one window at a time (the recorder already produces that format).
            using var reader = new Mono16kReader(audio);
            string model = Path.GetFileName(ModelPath);
            var checkpoint = TranscriptionCheckpoint.Load(checkpointPath, model, reader.TotalSamples)
                ?? new TranscriptionCheckpoint { Model = model, TotalSamples = reader.TotalSamples };
            var segments = checkpoint.Segments.Select(c => new TranscriptSegment(TimeSpan.FromMilliseconds(c.StartMs), TimeSpan.FromMilliseconds(c.EndMs), c.Text)).ToList();
            var speech = checkpoint.Speech.Where(r => r.Length == 2).Select(r => new SpeechRegion(r[0], r[1])).ToList();
            long windowStart = checkpoint.ProcessedSamples;
            await Task.Run(() => reader.Skip(windowStart), cancellationToken).ConfigureAwait(false);

            // The hint is given as Whisper's "initial prompt" (text it believes came just before),
            // which makes those spellings more likely. Echoes of the hint itself are dropped below.
            string? hint = HintProvider?.Invoke();
            TailsRecovered = 0;
            var builder = CreateBuilder(factory, hint);

            // Speech detection: the energy detector, or Silero VAD when its model is given (comparison).
            using var silero = Options.SileroVadPath is { } vadPath && File.Exists(vadPath) ? WhisperVadFactory.FromPath(vadPath) : null;
            using var vad = silero?.CreateBuilder()
                .WithThreshold(0.5f)
                .WithMinSpeechDuration(TimeSpan.FromMilliseconds(200))
                .WithMinSilenceDuration(TimeSpan.FromMilliseconds(700))
                .WithSpeechPadding(TimeSpan.FromMilliseconds(250))
                .Build();

            // Progress = audio already finished + whisper.cpp's own percentage for the current
            // stretch, over the whole recording.
            double total = Math.Max(1, reader.TotalSamples);
            double position = windowStart, current = 0;
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
                builder = builder.WithProgressHandler(p => Report((position + (current * p / 100.0)) / total));
            }

            Report(windowStart / total);
            int windowSamples = (int)Math.Max(rate * 30, WindowLength.TotalSeconds * rate);
            var buffer = new float[windowSamples + (rate * 60)];
            int carried = 0;
            int regionCount = 0;
            // Built only when a dropped tail is found (see WhisperPipelineOptions.RecoverDroppedTail).
            WhisperProcessor? plain = null;
            await using (var processor = builder.Build())
            {
                while (true)
                {
                    int read = await Task.Run(() => reader.Read(buffer.AsSpan(carried, windowSamples)), cancellationToken).ConfigureAwait(false);
                    int length = carried + read;
                    if (length == 0)
                    {
                        break;
                    }

                    bool last = read < windowSamples;
                    var window = new ReadOnlyMemory<float>(buffer, 0, length);
                    var found = vad is null ? SpeechActivity.Detect(window.Span, rate)
                        : vad.DetectSpeech(window.Span).Select(s => new SpeechRegion(
                            (int)Math.Clamp(s.Start.TotalSeconds * rate, 0, length), (int)Math.Clamp(s.End.TotalSeconds * rate, 0, length)))
                            .Where(r => r.EndSample > r.StartSample).ToList();

                    // Speech that runs into the end of the window is not cut: it moves to the next
                    // window (at most a minute of it; longer speech is cut), so no word is split at a boundary.
                    int cut = length;
                    if (!last && found.Count > 0 && found[^1].EndSample >= length - (rate / 2) && found[^1].StartSample >= length - (rate * 60))
                    {
                        cut = found[^1].StartSample;
                    }

                    var done = found.Where(r => r.EndSample <= cut).ToList();
                    var regions = Options.SkipSilence ? SpeechActivity.GroupIntoChunks(done, rate) : [new SpeechRegion(0, cut)];
                    var offsetOfWindow = TimeSpan.FromSeconds(windowStart / (double)rate);
                    foreach (var region in regions.Where(r => r.Length >= rate / 4))
                    {
                        position = windowStart + region.StartSample;
                        current = region.Length;
                        var offset = offsetOfWindow + region.Start(rate);
                        // ProcessAsync runs whisper.cpp on a worker thread and yields segments as they finish.
                        var lastEnd = TimeSpan.Zero;
                        await foreach (var segment in processor.ProcessAsync(window.Slice(region.StartSample, region.Length), cancellationToken).ConfigureAwait(false))
                        {
                            string text = CleanSegment(segment.Text);
                            if (text.Length > 0 && !IsHintEcho(text, hint))
                            {
                                segments.Add(new TranscriptSegment(segment.Start + offset, segment.End + offset, text));
                                lastEnd = segment.End > lastEnd ? segment.End : lastEnd;
                            }
                        }

                        regionCount++;

                        // The hint can make Whisper stop early: speech after the last text is decoded again without it.
                        int tailStart = region.StartSample + (int)Math.Max(0, (lastEnd.TotalSeconds - 0.2) * rate);
                        if (Options.RecoverDroppedTail && !string.IsNullOrWhiteSpace(hint) && region.EndSample - tailStart >= rate * 3
                            && SpeechActivity.SpeechCoverage(done, TimeSpan.FromSeconds(tailStart / (double)rate), region.End(rate), rate) * (region.EndSample - tailStart) >= rate * 1.5)
                        {
                            plain ??= CreateBuilder(factory, withHint: null).Build();
                            var tailOffset = offsetOfWindow + TimeSpan.FromSeconds(tailStart / (double)rate);
                            await foreach (var segment in plain.ProcessAsync(window.Slice(tailStart, region.EndSample - tailStart), cancellationToken).ConfigureAwait(false))
                            {
                                string text = CleanSegment(segment.Text);
                                if (text.Length > 0)
                                {
                                    segments.Add(new TranscriptSegment(segment.Start + tailOffset, segment.End + tailOffset, text));
                                }
                            }

                            TailsRecovered++;
                        }
                    }

                    speech.AddRange(done.Select(r => new SpeechRegion((int)(r.StartSample + windowStart), (int)(r.EndSample + windowStart))));
                    windowStart += cut;
                    position = windowStart;
                    current = 0;

                    // This window is final: saved so a failure or cancel later does not repeat it.
                    checkpoint.ProcessedSamples = windowStart;
                    checkpoint.Segments = segments.Select(x => new CheckpointSegment { StartMs = (long)x.Start.TotalMilliseconds, EndMs = (long)x.End.TotalMilliseconds, Text = x.Text }).ToList();
                    checkpoint.Speech = speech.Select(r => new[] { r.StartSample, r.EndSample }).ToList();
                    checkpoint.TrySave(checkpointPath);
                    Report(windowStart / total);

                    // What was not processed (speech running into the boundary) starts the next window.
                    carried = length - cut;
                    Array.Copy(buffer, cut, buffer, 0, carried);
                    if (last && carried == 0)
                    {
                        break;
                    }
                }
            }

            if (plain is not null)
            {
                await plain.DisposeAsync().ConfigureAwait(false);
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
                TimeSpan.FromSeconds(reader.TotalSamples / (double)rate),
                TimeSpan.FromSeconds(speech.Sum(r => (double)r.Length) / rate),
                regionCount, rawCount, removed, started.Elapsed);
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

    /// <summary>Regions whose dropped tail was decoded again in the last transcription (diagnostics).</summary>
    public int TailsRecovered { get; private set; }

    private WhisperProcessorBuilder CreateBuilder(WhisperFactory factory, string? withHint)
    {
        var builder = factory.CreateBuilder().WithThreads(_threads);
        builder = _language == "auto" ? builder.WithLanguageDetection() : builder.WithLanguage(_language);
        if (Options.NoContext)
        {
            // Do not feed the previous window's text back in as a prompt: one hallucinated
            // phrase would otherwise be "continued" window after window.
            builder = builder.WithNoContext();
        }

        if (!string.IsNullOrWhiteSpace(withHint))
        {
            builder = builder.WithPrompt(withHint);
            if (Options.CarryInitialPrompt)
            {
                builder = builder.WithCarryInitialPrompt(true);
            }
        }

        if (Options.Temperature is { } temperature)
        {
            builder = builder.WithTemperature(temperature);
        }

        return builder;
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

    /// <summary>
    /// Gives the model's memory back (the next transcription loads it again, about a second).
    /// AI jobs run one model at a time, so Whisper is not kept while the minutes' LLM runs.
    /// </summary>
    public void ReleaseModel()
    {
        if (_disposed || !_gate.Wait(0))
        {
            return; // in use (or closing): it is released by the one using it
        }

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
