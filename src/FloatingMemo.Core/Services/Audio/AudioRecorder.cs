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
}

/// <summary>
/// Records one or more sources into temporary files and, on stop, prepares a single
/// transcription-ready WAV. Recording only ever starts from an explicit <see cref="Start"/> call.
/// </summary>
public sealed class AudioRecorder : IDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    private readonly IAudioCaptureFactory _factory;
    private readonly string _workDirectory;
    private readonly Stopwatch _stopwatch = new();
    private readonly List<SourceTrackWriter> _tracks = [];
    private string? _sessionDirectory;

    public AudioRecorder(IAudioCaptureFactory factory, string workDirectory)
    {
        _factory = factory;
        _workDirectory = workDirectory;
    }

    /// <summary>%TEMP%\FloatingMemo\recordings — local and per user.</summary>
    public static string DefaultWorkDirectory => Path.Combine(Path.GetTempPath(), "FloatingMemo", "recordings");

    public bool IsRecording => _sessionDirectory is not null;

    public TimeSpan Elapsed => _stopwatch.Elapsed;

    /// <summary>Live loudness of each source while recording (numbers only, for the indicators).</summary>
    public AudioLevelMonitor Levels { get; } = new();

    /// <summary>
    /// A source failed while recording (e.g. device unplugged). Raised on a capture thread.
    /// What was captured before the failure is kept.
    /// </summary>
    public event EventHandler<AudioCaptureException>? SourceFailed;

    public void Start(AudioSources sources)
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

        foreach (var source in sources.Each())
        {
            IAudioCaptureStream? stream = null;
            try
            {
                stream = _factory.Create(source);
                Levels.Reset(source);
                var track = new SourceTrackWriter(stream, Path.Combine(session, $"{source}.wav"), startQpc, Levels);
                stream = null; // owned by the track now
                track.Failed += (_, error) => SourceFailed?.Invoke(this, new AudioCaptureException(track.Source, error));
                _tracks.Add(track);
                track.Start();
            }
            catch (Exception ex)
            {
                stream?.Dispose();
                DisposeTracks();
                TryDeleteDirectory(session);
                throw new AudioCaptureException(source, ex);
            }
        }

        _sessionDirectory = session;
        _stopwatch.Restart();
    }

    /// <summary>
    /// Stops all sources and builds the transcription WAV on a background thread.
    /// Raw per-source files are deleted afterwards; only the prepared file remains.
    /// </summary>
    public async Task<RecordedAudio> StopAsync()
    {
        string session = _sessionDirectory ?? throw new InvalidOperationException("Not recording.");
        _stopwatch.Stop();

        try
        {
            await Task.WhenAll(_tracks.Select(t => t.StopAsync(StopTimeout))).ConfigureAwait(false);

            var captured = _tracks.Where(t => t.FramesWritten > 0).ToList();
            if (captured.Count == 0)
            {
                return new RecordedAudio(session, null, TimeSpan.Zero, AudioSources.None);
            }

            var sources = captured.Aggregate(AudioSources.None, (all, t) => all | t.Source);
            var inputs = captured.Select(t => new AudioTrack(t.FilePath, t.StartOffset)).ToList();
            string output = Path.Combine(session, "recording.wav");
            TimeSpan duration = await Task.Run(() => AudioPreparation.MixToTranscriptionWav(inputs, output)).ConfigureAwait(false);

            foreach (var track in _tracks)
            {
                TryDeleteFile(track.FilePath);
            }

            return new RecordedAudio(session, output, duration, sources);
        }
        catch
        {
            TryDeleteDirectory(session);
            throw;
        }
        finally
        {
            DisposeTracks();
            _sessionDirectory = null;
        }
    }

    /// <summary>Stops recording and deletes everything captured (used when exiting).</summary>
    public void Cancel()
    {
        string? session = _sessionDirectory;
        if (session is null)
        {
            return;
        }

        _stopwatch.Stop();
        foreach (var track in _tracks)
        {
            track.StopAsync(TimeSpan.FromSeconds(1)).GetAwaiter().GetResult();
        }

        DisposeTracks();
        TryDeleteDirectory(session);
        _sessionDirectory = null;
    }

    /// <summary>Removes recordings left behind by a previous crash.</summary>
    public void DeleteLeftoverRecordings()
    {
        if (IsRecording || !Directory.Exists(_workDirectory))
        {
            return;
        }

        foreach (var dir in Directory.GetDirectories(_workDirectory, "rec-*"))
        {
            TryDeleteDirectory(dir);
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
