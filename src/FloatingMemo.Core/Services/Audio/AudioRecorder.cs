using System.Diagnostics;

namespace FloatingMemo.Services.Audio;

/// <summary>The result of a recording: one 16 kHz mono WAV ready for transcription.</summary>
public sealed class RecordedAudio
{
    internal RecordedAudio(string sessionDirectory, string? filePath, TimeSpan duration, AudioSources capturedSources)
    {
        SessionDirectory = sessionDirectory;
        FilePath = filePath;
        Duration = duration;
        CapturedSources = capturedSources;
    }

    public string SessionDirectory { get; }

    /// <summary>The prepared WAV file, or null if no audio was captured at all.</summary>
    public string? FilePath { get; }

    public TimeSpan Duration { get; }

    /// <summary>Sources that actually delivered audio (PC audio delivers nothing if nothing played).</summary>
    public AudioSources CapturedSources { get; }

    public bool HasAudio => FilePath is not null && Duration > TimeSpan.Zero;

    /// <summary>Deletes the audio. Raw audio is temporary by design.</summary>
    public void Delete() => AudioRecorder.TryDeleteDirectory(SessionDirectory);

    /// <summary>Marks the audio as kept for a later retry (offered again at the next start).</summary>
    public void Keep()
    {
        var manifest = RecordingManifest.Load(SessionDirectory) ?? new RecordingManifest { Created = DateTime.Now };
        manifest.State = RecordingStates.Kept;
        manifest.TrySave(SessionDirectory);
    }
}

/// <summary>
/// Stopping the recording worked, but the transcription file could not be made. Nothing was
/// deleted: the captured audio is in <see cref="SessionDirectory"/> and can be recovered.
/// </summary>
public sealed class RecordingSaveException(string sessionDirectory, Exception inner)
    : Exception($"録音の後処理に失敗しました（{inner.Message}）。録音された音声は一時フォルダに残しています。", inner)
{
    public string SessionDirectory { get; } = sessionDirectory;
}

/// <summary>
/// A recording whose capture has stopped: its files are closed and the recorder is free again.
/// <see cref="AudioRecorder.PrepareAsync"/> makes the transcription WAV from it.
/// </summary>
public sealed class StoppedRecording
{
    internal StoppedRecording(string sessionDirectory, AudioSources sources, IReadOnlyList<AudioTrack> inputs,
        IReadOnlyList<string> segmentFiles, RecordingManifest manifest)
    {
        SessionDirectory = sessionDirectory;
        Sources = sources;
        Inputs = inputs;
        SegmentFiles = segmentFiles;
        Manifest = manifest;
    }

    public string SessionDirectory { get; }

    /// <summary>Sources that actually delivered audio.</summary>
    public AudioSources Sources { get; }

    public bool HasAudio => Inputs.Count > 0;

    /// <summary>Removes the session (nothing was captured, so there is nothing to keep).</summary>
    public void Delete() => AudioRecorder.TryDeleteDirectory(SessionDirectory);

    internal IReadOnlyList<AudioTrack> Inputs { get; }

    internal IReadOnlyList<string> SegmentFiles { get; }

    internal RecordingManifest Manifest { get; }
}

/// <summary>A recording left from an earlier run (crash, failed processing, or kept for a retry).</summary>
public sealed record LeftoverRecording(string Directory, DateTime Created, string State, TimeSpan EstimatedDuration, long Bytes);

/// <summary>
/// Records one or more sources into temporary files and, on stop, prepares a single
/// transcription-ready WAV. Recording only ever starts from an explicit <see cref="Start"/> call.
/// </summary>
/// <remarks>
/// Audio is never deleted because something failed: only after a successful transcription
/// (by the caller), when the user discards it, or when the user deletes a leftover recording.
/// </remarks>
public sealed class AudioRecorder : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly IAudioCaptureFactory _factory;
    private readonly string _workDirectory;
    private readonly long _segmentBytes;
    private readonly Func<string, Stream>? _openFile;
    private readonly TimeSpan? _flushInterval;
    private readonly Stopwatch _stopwatch = new();
    private readonly List<SourceTrackWriter> _tracks = [];
    private readonly object _manifestLock = new();
    private RecordingManifest? _manifest;
    private string? _sessionDirectory;

    /// <summary>Tests: runs just before the transcription file is made (throwing simulates a failure there).</summary>
    internal Action? BeforePrepare { get; set; }

    /// <param name="segmentBytes">Size of each native-format segment (tests use small values).</param>
    /// <param name="openFile">Creates segment files (tests inject failures such as a full disk).</param>
    /// <param name="flushInterval">How often the open segment's header is updated (tests use zero).</param>
    public AudioRecorder(IAudioCaptureFactory factory, string workDirectory, long segmentBytes = SourceTrackWriter.DefaultSegmentBytes,
        Func<string, Stream>? openFile = null, TimeSpan? flushInterval = null)
    {
        _flushInterval = flushInterval;
        _factory = factory;
        _workDirectory = workDirectory;
        _segmentBytes = segmentBytes;
        _openFile = openFile;
    }

    /// <summary>%TEMP%\FloatingMemo\recordings — local and per user.</summary>
    public static string DefaultWorkDirectory => Path.Combine(Path.GetTempPath(), "FloatingMemo", "recordings");

    public bool IsRecording => _sessionDirectory is not null;

    /// <summary>Deletes a session folder (its transcript is saved, or the user chose to).</summary>
    public static void DeleteSession(string sessionDirectory) => TryDeleteDirectory(sessionDirectory);

    /// <summary>The folder the session folders are made in.</summary>
    public string WorkDirectory => _workDirectory;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Live loudness of each source while recording (numbers only, for the indicators).</summary>
    public AudioLevelMonitor Levels { get; } = new();

    /// <summary>
    /// A source failed while recording (e.g. device unplugged, disk full). Raised on a capture thread.
    /// What was captured before the failure is kept.
    /// </summary>
    public event EventHandler<AudioCaptureException>? SourceFailed;

    /// <param name="noteId">The note the recording is for (kept in the manifest for recovery).</param>
    public void Start(AudioSources sources, string? noteId = null)
    {
        if (IsRecording)
        {
            throw new InvalidOperationException("Already recording.");
        }

        if (sources == AudioSources.None)
        {
            throw new ArgumentException("録音する音声（マイクまたはPC音声）を選択してください。", nameof(sources));
        }

        string session = Path.Combine(_workDirectory, $"rec-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..36]);
        Directory.CreateDirectory(session);
        long startQpc = QpcClock.Now;
        _manifest = new RecordingManifest { Created = DateTime.Now, NoteId = noteId };

        foreach (var source in sources.Each())
        {
            IAudioCaptureStream? stream = null;
            try
            {
                stream = _factory.Create(source);
                Levels.Reset(source);
                var track = new SourceTrackWriter(stream, session, startQpc, Levels, _segmentBytes, _flushInterval, _openFile);
                stream = null; // owned by the track now
                track.Failed += (_, error) => SourceFailed?.Invoke(this, new AudioCaptureException(track.Source, error));
                track.LayoutChanged += (_, _) => SaveManifest(session);
                _tracks.Add(track);
                track.Start();
            }
            catch (Exception ex)
            {
                // Nothing was recorded yet: the empty session is removed.
                lock (_manifestLock)
                {
                    _manifest = null; // no capture thread writes session.json while the folder is removed
                }

                stream?.Dispose();
                DisposeTracks();
                TryDeleteDirectory(session);
                throw new AudioCaptureException(source, ex);
            }
        }

        _sessionDirectory = session;
        SaveManifest(session);
        _stopwatch.Restart();
    }

    /// <summary>
    /// Stops all sources and builds the transcription WAV on a background thread. Raw segments
    /// are deleted only once the prepared file exists. On failure nothing is deleted and a
    /// <see cref="RecordingSaveException"/> names the folder the audio is kept in.
    /// </summary>
    public async Task<RecordedAudio> StopAsync() => await PrepareAsync(await StopCaptureAsync().ConfigureAwait(false), BeforePrepare).ConfigureAwait(false);

    /// <summary>
    /// Stops all sources and closes their files. The recorder is free for the next recording as
    /// soon as this returns: the transcription WAV is made afterwards by <see cref="PrepareAsync"/>
    /// (which may run while the next recording is going on). On failure nothing is deleted.
    /// </summary>
    public async Task<StoppedRecording> StopCaptureAsync()
    {
        string session = _sessionDirectory ?? throw new InvalidOperationException("Not recording.");
        _stopwatch.Stop();

        try
        {
            await Task.WhenAll(_tracks.Select(t => t.StopAsync(StopTimeout))).ConfigureAwait(false);
            SetState(session, RecordingStates.Stopped);

            var captured = _tracks.Where(t => t.FramesWritten > 0).ToList();
            var sources = captured.Aggregate(AudioSources.None, (all, t) => all | t.Source);
            // Copies: the tracks are disposed below and the recorder may start again at once.
            var inputs = captured.Select(t => new AudioTrack(t.Segments.ToList(), t.StartOffset)).ToList();
            lock (_manifestLock)
            {
                return new StoppedRecording(session, sources, inputs, _tracks.SelectMany(t => t.Segments).ToList(), _manifest!);
            }
        }
        catch (Exception ex)
        {
            // The captured audio stays: the user can process it again or delete it.
            SetState(session, RecordingStates.Failed, error: $"{ex.GetType().Name}: {ex.Message}");
            throw new RecordingSaveException(session, ex);
        }
        finally
        {
            DisposeTracks();
            lock (_manifestLock)
            {
                _sessionDirectory = null;
                _manifest = null;
            }
        }
    }

    /// <summary>
    /// Makes the transcription WAV of a stopped recording on a background thread. Raw segments are
    /// deleted only once the prepared file exists. On failure nothing is deleted and a
    /// <see cref="RecordingSaveException"/> names the folder the audio is kept in.
    /// </summary>
    /// <param name="beforePrepare">Tests: runs just before the file is made (throwing simulates a failure there).</param>
    public static async Task<RecordedAudio> PrepareAsync(StoppedRecording stopped, Action? beforePrepare = null)
    {
        string session = stopped.SessionDirectory;
        var manifest = stopped.Manifest; // this session's own: the recorder no longer touches it
        if (stopped.Inputs.Count == 0)
        {
            return new RecordedAudio(session, null, TimeSpan.Zero, AudioSources.None);
        }

        try
        {
            var (output, duration) = await Task.Run(() =>
            {
                beforePrepare?.Invoke();
                return Prepare(session, stopped.Inputs);
            }).ConfigureAwait(false);

            foreach (var file in stopped.SegmentFiles)
            {
                TryDeleteFile(file);
            }

            manifest.State = RecordingStates.Prepared;
            manifest.Error = null;
            manifest.Prepared = Path.GetFileName(output);
            manifest.TrySave(session);
            return new RecordedAudio(session, output, duration, stopped.Sources);
        }
        catch (Exception ex)
        {
            manifest.State = RecordingStates.Failed;
            manifest.Error = $"{ex.GetType().Name}: {ex.Message}";
            manifest.TrySave(session);
            throw new RecordingSaveException(session, ex);
        }
    }

    /// <summary>Stops recording and deletes everything captured (the user chose to discard it).</summary>
    public void Cancel()
    {
        string? session = _sessionDirectory;
        if (session is null)
        {
            return;
        }

        _stopwatch.Stop();
        lock (_manifestLock)
        {
            _manifest = null; // no capture thread writes session.json while the folder is removed
        }

        foreach (var track in _tracks)
        {
            track.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }

        DisposeTracks();
        TryDeleteDirectory(session);
        _sessionDirectory = null;
    }

    /// <summary>
    /// Recordings left by an earlier run: a crash while recording, processing that failed, or
    /// audio kept for a retry. Nothing is deleted here; the user decides.
    /// </summary>
    public IReadOnlyList<LeftoverRecording> FindLeftovers()
    {
        if (!Directory.Exists(_workDirectory))
        {
            return [];
        }

        var list = new List<LeftoverRecording>();
        foreach (var dir in Directory.GetDirectories(_workDirectory, "rec-*"))
        {
            if (dir == _sessionDirectory)
            {
                continue;
            }

            var wavs = Directory.GetFiles(dir, "*.wav");
            if (wavs.Length == 0)
            {
                continue; // an empty folder (nothing was captured): not worth asking about
            }

            var manifest = RecordingManifest.Load(dir);
            long bytes = wavs.Sum(f => new FileInfo(f).Length);
            list.Add(new LeftoverRecording(dir, manifest?.Created ?? Directory.GetCreationTime(dir),
                manifest?.State ?? RecordingStates.Recording, EstimateDuration(dir, manifest), bytes));
        }

        return list.OrderBy(l => l.Created).ToList();
    }

    /// <summary>Deletes the given leftover recordings (the user chose to). Returns how many were removed.</summary>
    public int DeleteLeftovers(IEnumerable<LeftoverRecording> leftovers)
    {
        int removed = 0;
        foreach (var leftover in leftovers)
        {
            if (leftover.Directory != _sessionDirectory && Path.GetDirectoryName(leftover.Directory) == Path.GetFullPath(_workDirectory).TrimEnd('\\'))
            {
                TryDeleteDirectory(leftover.Directory);
                removed += Directory.Exists(leftover.Directory) ? 0 : 1;
            }
        }

        return removed;
    }

    /// <summary>
    /// Makes the transcription WAV of a leftover recording: the prepared file when it is complete,
    /// otherwise from the captured segments (also those of a crashed recording). Segments are kept.
    /// </summary>
    public static async Task<RecordedAudio> RecoverAsync(string sessionDirectory)
    {
        var manifest = RecordingManifest.Load(sessionDirectory);
        if (manifest?.Prepared is { } preparedName && Path.Combine(sessionDirectory, preparedName) is var prepared && File.Exists(prepared)
            && manifest.State is RecordingStates.Prepared or RecordingStates.Kept)
        {
            try
            {
                using var reader = new NAudio.Wave.WaveFileReader(prepared);
                return new RecordedAudio(sessionDirectory, prepared, reader.TotalTime, SourcesOf(manifest));
            }
            catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException)
            {
                // Damaged: made again from the segments below if they are still there.
            }
        }

        var tracks = TracksOf(sessionDirectory, manifest);
        foreach (var file in tracks.SelectMany(t => t.Track.Files))
        {
            try
            {
                // A crash leaves the header behind the data written after the last flush.
                WavRepair.ExtendToFileLength(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Read as it is (or replaced by silence of its length) below.
            }
        }

        if (tracks.Count == 0)
        {
            throw new InvalidDataException("この録音には読み取れる音声がありません。");
        }

        var (output, duration) = await Task.Run(() => Prepare(sessionDirectory, tracks.Select(t => t.Track).ToList())).ConfigureAwait(false);
        var updated = manifest ?? new RecordingManifest { Created = Directory.GetCreationTime(sessionDirectory) };
        updated.Prepared = Path.GetFileName(output);
        updated.State = RecordingStates.Kept;
        updated.TrySave(sessionDirectory);
        return new RecordedAudio(sessionDirectory, output, duration, tracks.Aggregate(AudioSources.None, (all, t) => all | t.Source));
    }

    /// <summary>The source tracks of a session folder: from the manifest, or by file name when it is missing.</summary>
    internal static List<(AudioSources Source, AudioTrack Track)> TracksOf(string sessionDirectory, RecordingManifest? manifest)
    {
        var result = new List<(AudioSources, AudioTrack)>();
        foreach (var source in new[] { AudioSources.Microphone, AudioSources.SystemAudio })
        {
            var info = manifest?.Tracks.FirstOrDefault(t => t.Source == source.ToString());
            var files = (info?.Segments.Select(s => Path.Combine(sessionDirectory, s)) ?? [])
                .Concat(Directory.GetFiles(sessionDirectory, $"{source}-*.wav").OrderBy(f => f, StringComparer.Ordinal))
                .Concat(Directory.GetFiles(sessionDirectory, $"{source}.wav")) // recordings made before segmenting
                .Where(File.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count > 0)
            {
                result.Add((source, new AudioTrack(files, TimeSpan.FromTicks(info?.StartOffsetTicks ?? 0))));
            }
        }

        return result;
    }

    private static AudioSources SourcesOf(RecordingManifest manifest) =>
        manifest.Tracks.Aggregate(AudioSources.None, (all, t) => Enum.TryParse<AudioSources>(t.Source, out var s) ? all | s : all);

    private static TimeSpan EstimateDuration(string dir, RecordingManifest? manifest)
    {
        try
        {
            if (manifest?.Prepared is { } prepared && File.Exists(Path.Combine(dir, prepared)))
            {
                return TimeSpan.FromSeconds(Math.Max(0, new FileInfo(Path.Combine(dir, prepared)).Length - 44) / (double)(AudioPreparation.TargetSampleRate * 2));
            }

            // The longest track: its bytes divided by its byte rate (from the manifest; 48 kHz stereo float otherwise).
            double longest = 0;
            foreach (var (source, track) in TracksOf(dir, manifest))
            {
                var info = manifest?.Tracks.FirstOrDefault(t => t.Source == source.ToString());
                double rate = info is { SampleRate: > 0, BlockAlign: > 0 } ? info.SampleRate * (double)info.BlockAlign : 384_000;
                longest = Math.Max(longest, track.Files.Sum(f => Math.Max(0, new FileInfo(f).Length - 44)) / rate);
            }

            return TimeSpan.FromSeconds(longest);
        }
        catch (IOException)
        {
            return TimeSpan.Zero;
        }
    }

    /// <summary>Writes recording.wav (or a new name if an earlier attempt left a file) and returns it with its duration.</summary>
    private static (string Output, TimeSpan Duration) Prepare(string session, IReadOnlyList<AudioTrack> tracks)
    {
        string output = Path.Combine(session, "recording.wav");
        for (int n = 2; File.Exists(output) || Directory.Exists(output); n++)
        {
            output = Path.Combine(session, $"recording-{n}.wav");
        }

        try
        {
            return (output, AudioPreparation.MixToTranscriptionWav(tracks, output));
        }
        catch
        {
            TryDeleteFile(output); // a half-written output only; the segments stay
            throw;
        }
    }

    private void SaveManifest(string session)
    {
        lock (_manifestLock)
        {
            if (_manifest is null)
            {
                return;
            }

            _manifest.Tracks = _tracks.Select(t => new RecordingTrackInfo
            {
                Source = t.Source.ToString(),
                StartOffsetTicks = t.StartOffset.Ticks,
                SampleRate = t.Format.SampleRate,
                Channels = t.Format.Channels,
                BlockAlign = t.Format.BlockAlign,
                Segments = t.Segments.Select(Path.GetFileName).ToList()!,
            }).ToList();
            _manifest.TrySave(session);
        }
    }

    private void SetState(string session, string state, string? error = null, string? prepared = null)
    {
        lock (_manifestLock)
        {
            var manifest = _manifest ?? RecordingManifest.Load(session) ?? new RecordingManifest { Created = DateTime.Now };
            _manifest = manifest;
        }

        SaveManifest(session);
        lock (_manifestLock)
        {
            _manifest!.State = state;
            _manifest.Error = error;
            _manifest.Prepared = prepared ?? _manifest.Prepared;
            _manifest.TrySave(session);
        }
    }

    private void DisposeTracks()
    {
        foreach (var track in _tracks)
        {
            track.Dispose();
            Levels.Clear(track.Source);
        }

        _tracks.Clear();
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public void Dispose() => Cancel();
}
