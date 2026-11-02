using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.Wave;

namespace FloatingMemo.Services.Transcription;

/// <summary>
/// Haas-ja-v1.0 (Third Intelligence, Apache 2.0) through speech.cpp's command line (MIT), run as
/// a separate process on this PC. Experimental, chosen in the settings; Whisper stays the default.
/// </summary>
/// <remarks>
/// A separate process rather than speech.cpp's library in this one:
/// - a crash, a hang or a GPU driver failure ends only that process; the recorder and the notes
///   in the app are never affected, and the job fails with the audio kept for a retry;
/// - all of its memory (model, Vulkan buffers) is given back when it exits, before the next AI job;
/// - loading a Vulkan model in the app's own process moved Windows' keyboard focus off the
///   editor (measured with Whisper); a child process cannot do that;
/// - the exit code, the JSON on stdout and stderr are checked: every piece must come back.
/// The cost is starting the process and loading the 0.66 GB model per call (about 0.6 s).
///
/// Steps: Silero VAD (speech.cpp's own, on the CPU) finds the speech; <see cref="SpeechChunker"/>
/// plans the pieces (a long stretch is cut at its quietest moment; Haas needs memory growing
/// with the square of the input, a whole recording does not fit in 8 GB); each piece is written
/// as a small WAV next to the recording and all are recognized in a few calls; the texts are
/// joined with sentence ends. Nothing is downloaded: the files are paths on this PC.
/// </remarks>
public sealed class SpeechCppTranscriptionService : ISpeechRecognizer
{
    private const int Rate = Audio.AudioPreparation.TargetSampleRate;
    private const int FilesPerCall = 80;

    private readonly string _exe;
    private readonly string _model;
    private readonly string _vad;
    private readonly string _device;
    private readonly int _threads;

    /// <param name="device">"auto" (the first GPU, else the CPU), "gpu" or "cpu".</param>
    public SpeechCppTranscriptionService(string exePath, string modelPath, string vadModelPath, string device = "auto", int? threads = null)
    {
        _exe = exePath;
        _model = modelPath;
        _vad = vadModelPath;
        _device = device;
        // Leave half the cores free so the PC (and this app's UI) stays responsive.
        _threads = threads ?? Math.Max(1, Environment.ProcessorCount / 2);
    }

    public const string Id = "haas-ja";

    /// <summary>The files speech.cpp needs, all in &lt;models&gt;\haas (see README).</summary>
    public const string ExeName = "speech.exe";

    public static readonly string[] ModelNames = ["Haas-616M-ja-v1.0-Q8_0.gguf", "Haas-616M-ja-v1.0-Q6_K.gguf", "Haas-616M-ja-v1.0-Q4_K.gguf", "Haas-616M-ja-v1.0-F16.gguf"];

    public const string VadName = "silero-vad-309K-v6.2-F32.gguf";

    public string Name => Id;

    public string DisplayName => $"Haas-ja（試験的・{Path.GetFileNameWithoutExtension(_model).Replace("Haas-616M-ja-v1.0-", string.Empty)}）";

    public string ModelPath => _model;

    /// <summary>The pieces of the last transcription were recognized (the model loads per call).</summary>
    public bool IsModelLoaded { get; private set; }

    public TranscriptionDiagnostics? LastDiagnostics { get; private set; }

    public SpeechChunkOptions Chunking { get; init; } = new();

    /// <summary>Ends each piece that ended at a pause with "。" (see <see cref="SpeechChunker.Join"/>).</summary>
    public bool AddSentenceEnds { get; init; } = true;

    /// <summary>VAD: gaps shorter than this inside speech keep it one region (short replies after a pause stay apart).</summary>
    public int MinSilenceMs { get; init; } = 150;

    /// <summary>Haas-ja's custom vocabulary: one "表記=よみ" (or "表記") a line; null or empty = none.</summary>
    public Func<string?>? KeywordsProvider { get; set; }

    /// <summary>The files in one folder (tests and the portable layout); null when one is missing.</summary>
    public static SpeechCppTranscriptionService? FromFolder(string dir, string device = "auto")
    {
        string exe = Path.Combine(dir, ExeName);
        string vad = Path.Combine(dir, VadName);
        string? model = ModelNames.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        return File.Exists(exe) && File.Exists(vad) && model is not null ? new SpeechCppTranscriptionService(exe, model, vad, device) : null;
    }

    /// <summary>Finds speech.exe, a Haas-ja model and the VAD model in the models folders (null when one is missing).</summary>
    public static SpeechCppTranscriptionService? Find(string device = "auto", string? appModels = null, string? userModels = null)
    {
        foreach (var (dir, _) in ModelLocations.SearchDirectories(ModelLocations.HaasFolder, appModels ?? ModelLocations.AppModelsDirectory, userModels ?? ModelLocations.UserModelsDirectory))
        {
            if (FromFolder(dir, device) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    public string? MissingFiles()
    {
        var missing = new[] { _exe, _model, _vad }.Where(f => !File.Exists(f)).Select(Path.GetFileName).ToList();
        return missing.Count == 0 ? null : $"Haas-ja に必要なファイル（{string.Join("、", missing)}）がありません。README の手順で {Path.GetDirectoryName(_model)} に置いてください。";
    }

    public void ReleaseModel()
    {
        // Nothing stays loaded: the process exits after each call.
    }

    public void Dispose()
    {
    }

    public async Task<TranscriptionResult> TranscribeFileAsync(string wavPath, CancellationToken cancellationToken, IProgress<double>? progress, string? checkpointPath)
    {
        if (MissingFiles() is { } missing)
        {
            throw new TranscriptionException(missing) { IsModelMissing = true };
        }

        var started = Stopwatch.StartNew();
        IsModelLoaded = false;
        long totalSamples;
        using (var reader = new WaveFileReader(wavPath))
        {
            if (reader.WaveFormat.SampleRate != Rate || reader.WaveFormat.Channels != 1 || reader.WaveFormat.BitsPerSample != 16)
            {
                throw new TranscriptionException("対応していない音声形式です（16 kHz モノラルの WAV が必要です）。");
            }

            totalSamples = reader.Length / 2;
        }

        // Pieces already recognized by an earlier, interrupted run (same audio, model and settings).
        string settings = $"{Path.GetFileName(_model)}|{Chunking}|{MinSilenceMs}|{AddSentenceEnds}|{KeywordsProvider?.Invoke()}";
        var checkpoint = SpeechCppCheckpoint.Load(checkpointPath, settings, totalSamples);

        IReadOnlyList<SpeechChunk> chunks;
        if (checkpoint is not null)
        {
            chunks = checkpoint.Chunks.Select(c => new SpeechChunk(c.Start, c.End, c.Split)).ToList();
        }
        else
        {
            var regions = await DetectSpeechAsync(wavPath, cancellationToken).ConfigureAwait(false);
            chunks = SpeechChunker.Plan(regions, totalSamples, Rate, (from, to) => QuietestPoint(wavPath, from, to), Chunking);
            checkpoint = new SpeechCppCheckpoint
            {
                Settings = settings,
                TotalSamples = totalSamples,
                Chunks = chunks.Select(c => new SpeechCppCheckpoint.Piece { Start = c.StartSample, End = c.EndSample, Split = c.SplitAfter }).ToList(),
            };
        }

        double audioTotal = Math.Max(1, chunks.Sum(c => (double)c.Length));
        double done = checkpoint.Chunks.Where(c => c.Text is not null).Sum(c => (double)(c.End - c.Start));
        progress?.Report(done / audioTotal);

        // The pieces go next to the recording (or to a private temporary folder) and are removed afterwards.
        string work = Path.Combine(checkpointPath is not null ? Path.GetDirectoryName(checkpointPath)! : Path.GetTempPath(), $"haas-pieces-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var todo = Enumerable.Range(0, chunks.Count).Where(i => checkpoint.Chunks[i].Text is null).ToList();
            foreach (var batch in todo.Chunk(FilesPerCall))
            {
                var files = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (int i in batch)
                {
                    string file = Path.Combine(work, $"piece-{i:D5}.wav");
                    WritePiece(wavPath, chunks[i], file);
                    files[Path.GetFullPath(file)] = i;
                }

                var texts = await RecognizeAsync(files, chunks, cancellationToken, i =>
                {
                    done += chunks[i].Length;
                    IsModelLoaded = true;
                    progress?.Report(Math.Min(1, done / audioTotal));
                }).ConfigureAwait(false);
                foreach (var (i, text) in texts)
                {
                    checkpoint.Chunks[i].Text = text;
                }

                checkpoint.TrySave(checkpointPath);
                foreach (string file in files.Keys)
                {
                    TryDelete(file);
                }
            }
        }
        finally
        {
            try
            {
                Directory.Delete(work, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var pieces = chunks.Select((c, i) => (c, checkpoint.Chunks[i].Text ?? string.Empty)).ToList();
        var segments = SpeechChunker.Join(pieces, Rate, AddSentenceEnds);
        LastDiagnostics = new TranscriptionDiagnostics(TimeSpan.FromSeconds(totalSamples / (double)Rate),
            TimeSpan.FromSeconds(chunks.Sum(c => (double)c.Length) / Rate), chunks.Count, segments.Count, 0, started.Elapsed);
        progress?.Report(1);
        return new TranscriptionResult(LocalWhisperTranscriptionService.JoinSegments(segments.Select(s => s.Text)), segments);
    }

    /// <summary>Speech regions from speech.cpp's Silero VAD (no padding: the chunker pads).</summary>
    private async Task<IReadOnlyList<SpeechRegion>> DetectSpeechAsync(string wavPath, CancellationToken cancellationToken)
    {
        var (stdout, _) = await RunAsync(["vad", _vad, "--format", "json", "--threshold", "0.5", "--speech-pad-ms", "0",
            "--min-silence-duration-ms", MinSilenceMs.ToString(System.Globalization.CultureInfo.InvariantCulture), "--min-speech-duration-ms", "100", wavPath],
            TimeSpan.FromMinutes(10), cancellationToken, null).ConfigureAwait(false);
        var json = JsonNode.Parse(stdout.Trim().Split('\n').Last(l => l.TrimStart().StartsWith('{')))!;
        return json["regions"]!.AsArray().Select(r => new SpeechRegion(
            (int)Math.Round(r!["start"]!.GetValue<double>() * Rate), (int)Math.Round(r["end"]!.GetValue<double>() * Rate))).ToList();
    }

    /// <summary>Recognizes the piece files in one call; every piece must come back, or it fails.</summary>
    private async Task<Dictionary<int, string>> RecognizeAsync(Dictionary<string, int> files, IReadOnlyList<SpeechChunk> chunks,
        CancellationToken cancellationToken, Action<int> pieceDone)
    {
        var args = new List<string> { "asr", _model, "--device", _device, "--format", "json" };
        if (_device != "gpu")
        {
            args.AddRange(["--threads", _threads.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        if (KeywordsProvider?.Invoke() is { Length: > 0 } keywords)
        {
            args.AddRange(["--keywords", keywords]);
        }

        args.AddRange(files.Keys);
        double seconds = files.Values.Sum(i => (double)chunks[i].Length) / Rate;
        var texts = new Dictionary<int, string>();
        await RunAsync(args, TimeSpan.FromSeconds(60 + (seconds * 3)), cancellationToken, line =>
        {
            if (!line.TrimStart().StartsWith('{'))
            {
                return;
            }

            var json = JsonNode.Parse(line)!;
            string file = Path.GetFullPath(json["file"]!.GetValue<string>());
            if (files.TryGetValue(file, out int i))
            {
                texts[i] = json["text"]?.GetValue<string>() ?? string.Empty;
                pieceDone(i);
            }
        }).ConfigureAwait(false);

        if (texts.Count != files.Count)
        {
            throw new TranscriptionException($"音声認識の結果が一部欠けています（{files.Count} 件中 {texts.Count} 件）。音声は保持しています。");
        }

        return texts;
    }

    /// <summary>
    /// Runs speech.exe and waits for it. Cancel or timeout ends the process (it only reads files);
    /// a non-zero exit or a crash is an error with the end of stderr (file names and numbers, no text).
    /// </summary>
    private async Task<(string Stdout, string Stderr)> RunAsync(IReadOnlyList<string> args, TimeSpan timeout, CancellationToken cancellationToken, Action<string>? onLine)
    {
        var info = new ProcessStartInfo(_exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_exe)!,
        };
        foreach (string a in args)
        {
            info.ArgumentList.Add(a);
        }

        using var process = Process.Start(info) ?? throw new TranscriptionException("音声認識（speech.cpp）を起動できませんでした。");
        try
        {
            // Lower than the app: recording and typing come first.
            process.PriorityClass = ProcessPriorityClass.BelowNormal;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var readOut = Task.Run(async () =>
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                stdout.AppendLine(line);
                onLine?.Invoke(line);
            }
        });
        var readErr = Task.Run(async () =>
        {
            while (await process.StandardError.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (stderr)
                {
                    stderr.AppendLine(line);
                    if (stderr.Length > 8000)
                    {
                        stderr.Remove(0, stderr.Length - 4000);
                    }
                }
            }
        });

        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timer.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            await Task.WhenAll(readOut, readErr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            throw new TranscriptionException($"音声認識が時間内に終わりませんでした（{timeout.TotalSeconds:0} 秒）。音声は保持しています。");
        }

        string tail;
        lock (stderr)
        {
            tail = stderr.ToString().Trim();
        }

        // 3 = a piece reached the most the model writes (its text is still there).
        if (process.ExitCode is not (0 or 3))
        {
            throw new TranscriptionException($"音声認識（speech.cpp）が失敗しました（終了コード {process.ExitCode}）。{Last(tail, 300)}");
        }

        return (stdout.ToString(), tail);
    }

    private static string Last(string s, int n) => s.Length <= n ? s : s[^n..];

    /// <summary>The start of the quietest 100 ms between two sample positions (where a long stretch is cut).</summary>
    internal static int QuietestPoint(string wavPath, int from, int to)
    {
        using var reader = new WaveFileReader(wavPath);
        from = Math.Max(0, from);
        to = (int)Math.Min(to, reader.Length / 2);
        if (to - from < Rate / 10)
        {
            return to;
        }

        reader.Position = (long)from * 2;
        var bytes = new byte[(to - from) * 2];
        int read = reader.Read(bytes, 0, bytes.Length);
        const int frame = Rate / 10, hop = Rate / 50;
        int best = to;
        double bestEnergy = double.MaxValue;
        for (int s = 0; s + frame <= read / 2; s += hop)
        {
            double energy = 0;
            for (int k = s; k < s + frame; k++)
            {
                short v = BitConverter.ToInt16(bytes, k * 2);
                energy += (double)v * v;
            }

            if (energy < bestEnergy)
            {
                bestEnergy = energy;
                best = from + s + (frame / 2);
            }
        }

        return best;
    }

    private static void WritePiece(string wavPath, SpeechChunk chunk, string file)
    {
        using var reader = new WaveFileReader(wavPath);
        reader.Position = (long)chunk.StartSample * 2;
        var bytes = new byte[chunk.Length * 2];
        int read = reader.Read(bytes, 0, bytes.Length);
        using var writer = new WaveFileWriter(file, reader.WaveFormat);
        writer.Write(bytes, 0, read);
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>
/// haas-checkpoint.json next to the recording: the plan and the texts recognized so far, so an
/// interrupted transcription continues instead of starting over. Removed with the recording.
/// </summary>
internal sealed class SpeechCppCheckpoint
{
    public string Settings { get; set; } = string.Empty;

    public long TotalSamples { get; set; }

    public List<Piece> Chunks { get; set; } = [];

    public sealed class Piece
    {
        public int Start { get; set; }

        public int End { get; set; }

        public bool Split { get; set; }

        public string? Text { get; set; }
    }

    public static SpeechCppCheckpoint? Load(string? path, string settings, long totalSamples)
    {
        try
        {
            if (path is null || !File.Exists(path))
            {
                return null;
            }

            var c = JsonSerializer.Deserialize(File.ReadAllText(path), SpeechCppCheckpointJsonContext.Default.SpeechCppCheckpoint);
            return c is not null && c.Settings == settings && c.TotalSamples == totalSamples ? c : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    public void TrySave(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, SpeechCppCheckpointJsonContext.Default.SpeechCppCheckpoint));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(SpeechCppCheckpoint))]
internal sealed partial class SpeechCppCheckpointJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}
