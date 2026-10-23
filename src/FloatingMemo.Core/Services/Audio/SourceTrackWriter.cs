using NAudio.Wave;

namespace FloatingMemo.Services.Audio;

/// <summary>
/// Writes one capture stream to WAV files in the device's native format, split into segments.
/// </summary>
/// <remarks>
/// WASAPI loopback delivers no packets while nothing is playing, so a naive writer would
/// squeeze silent stretches out and drift out of sync with the microphone. Each packet's
/// QPC timestamp tells us where it belongs on the timeline, and any gap is filled with silence.
///
/// Long recordings: a device's native format (typically 48 kHz stereo float, 384 KB/s) reaches
/// the 4 GB limit of a plain WAV file after about 3 hours. Instead of one huge file the track is
/// written as consecutive segments (<see cref="Segments"/>), each closed — and therefore complete
/// and readable — as soon as the next one starts. Segments are cut on a frame boundary, so their
/// concatenation is exactly the track: nothing is lost or repeated at a boundary and the timeline
/// (and with it the sync with the other source) is unchanged. The open segment's header is
/// rewritten every few seconds, so after a crash at most that much audio is unreadable.
/// </remarks>
internal sealed class SourceTrackWriter : IDisposable
{
    // Gaps shorter than this are treated as normal timing jitter.
    private const int GapToleranceMs = 50;

    /// <summary>A new segment starts at this size: about 45 minutes of 48 kHz stereo float, far below 4 GB.</summary>
    public const long DefaultSegmentBytes = 1L << 30;

    /// <summary>How often the open segment's header is brought up to date (crash safety).</summary>
    public static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(5);

    // Guard against a bogus timestamp producing an enormous file.
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly IAudioCaptureStream _stream;
    private readonly long _sessionStartQpc;
    private readonly WaveFormat _format;
    private readonly AudioLevelMonitor? _levels;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _segmentBytes;
    private readonly TimeSpan _flushInterval;
    private readonly Func<string, Stream> _openFile;
    private readonly List<string> _segments = [];
    private WaveFileWriter? _writer;
    private Stream? _file; // the writer does not close a stream it was given
    private long _segmentWritten;
    private DateTime _lastFlush = DateTime.UtcNow;
    private long _firstPacketQpc = -1;
    private long _framesWritten;
    private bool _closed;

    /// <param name="directory">The session folder; segments are named "{source}-0001.wav", "-0002.wav", ….</param>
    /// <param name="segmentBytes">Audio bytes per segment (rounded down to whole frames). Tests use small values.</param>
    /// <param name="openFile">Creates a segment file (tests inject failing streams, e.g. a full disk).</param>
    public SourceTrackWriter(IAudioCaptureStream stream, string directory, long sessionStartQpc, AudioLevelMonitor? levels = null,
        long segmentBytes = DefaultSegmentBytes, TimeSpan? flushInterval = null, Func<string, Stream>? openFile = null)
    {
        _levels = levels;
        _stream = stream;
        _sessionStartQpc = sessionStartQpc;
        _format = stream.WaveFormat;
        _directory = directory;
        _baseName = stream.Source.ToString();
        _segmentBytes = Math.Max(_format.BlockAlign, segmentBytes - (segmentBytes % _format.BlockAlign));
        _flushInterval = flushInterval ?? DefaultFlushInterval;
        _openFile = openFile ?? (path => new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read));
        OpenSegment();
        _stream.PacketAvailable += OnPacket;
        _stream.Stopped += OnStopped;
    }

    public AudioSources Source => _stream.Source;

    public WaveFormat Format => _format;

    /// <summary>The segment files in order (complete except possibly the last one while recording).</summary>
    public IReadOnlyList<string> Segments
    {
        get
        {
            lock (_lock)
            {
                return [.. _segments];
            }
        }
    }

    /// <summary>Delay of the first packet relative to the start of the recording session.</summary>
    public TimeSpan StartOffset { get; private set; }

    public long FramesWritten
    {
        get
        {
            lock (_lock)
            {
                return _framesWritten;
            }
        }
    }

    /// <summary>Set when writing failed (e.g. the disk is full); everything written before is kept.</summary>
    public Exception? WriteError { get; private set; }

    /// <summary>Raised (on the capture thread) if the device stops because of an error, or writing fails.</summary>
    public event EventHandler<Exception>? Failed;

    /// <summary>Raised (on the capture thread) once the first packet has fixed <see cref="StartOffset"/>, and whenever a segment starts.</summary>
    public event EventHandler? LayoutChanged;

    public void Start() => _stream.Start();

    /// <summary>Stops capture, waits for the final packet, and closes the WAV file.</summary>
    public async Task StopAsync(TimeSpan timeout)
    {
        try
        {
            _stream.Stop();
        }
        catch
        {
            // A device that already failed may throw on stop; the file is still closed below.
        }

        await Task.WhenAny(_stopped.Task, Task.Delay(timeout)).ConfigureAwait(false);
        CloseWriter();
    }

    private void OnPacket(ReadOnlySpan<byte> data, bool silent, long qpc)
    {
        // Only a loudness number leaves this method (for the level indicator); outside the
        // file lock so the UI reading levels never waits for disk writes.
        _levels?.Report(Source, data, silent, _format);

        bool layoutChanged = false;
        Exception? failure = null;
        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            try
            {
                int frames = data.Length / _format.BlockAlign;
                if (_firstPacketQpc < 0)
                {
                    long first = qpc >= 0 ? qpc : QpcClock.Now;
                    _firstPacketQpc = first;
                    // TimeSpan ticks are 100 ns, the same unit as QPC positions.
                    StartOffset = TimeSpan.FromTicks(Math.Max(0, first - _sessionStartQpc));
                    layoutChanged = true;
                }
                else if (qpc >= 0)
                {
                    long expectedFrame = (qpc - _firstPacketQpc) * _format.SampleRate / 10_000_000;
                    long gap = expectedFrame - _framesWritten;
                    long tolerance = _format.SampleRate * GapToleranceMs / 1000;
                    long maxGap = (long)(_format.SampleRate * MaxGap.TotalSeconds);
                    if (gap > tolerance && gap < maxGap)
                    {
                        layoutChanged |= WriteSilence(gap);
                    }
                }

                layoutChanged |= silent ? WriteSilence(frames) : WriteAudio(data[..(frames * _format.BlockAlign)]);
                if (DateTime.UtcNow - _lastFlush >= _flushInterval)
                {
                    _writer?.Flush(); // rewrites the header: the file is readable up to here
                    _lastFlush = DateTime.UtcNow;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // Disk full, file removed, size limit: keep what is on disk, stop writing this track.
                failure = ex;
                WriteError = ex;
                CloseCurrentSegment();
            }
        }

        if (layoutChanged)
        {
            LayoutChanged?.Invoke(this, EventArgs.Empty);
        }

        if (failure is not null)
        {
            Failed?.Invoke(this, failure);
        }
    }

    /// <summary>Writes whole frames, starting a new segment at the size limit. True if a segment started.</summary>
    private bool WriteAudio(ReadOnlySpan<byte> bytes)
    {
        bool rolled = false;
        while (bytes.Length > 0)
        {
            rolled |= RollIfFull();
            int chunk = (int)Math.Min(bytes.Length, _segmentBytes - _segmentWritten);
            _writer!.Write(bytes[..chunk]);
            _segmentWritten += chunk;
            _framesWritten += chunk / _format.BlockAlign;
            bytes = bytes[chunk..];
        }

        return rolled;
    }

    private bool WriteSilence(long frames)
    {
        // All capture formats WASAPI hands out (16/24/32-bit PCM, float) use 0 for silence.
        Span<byte> zeros = stackalloc byte[4096 - (4096 % _format.BlockAlign)];
        zeros.Clear();
        long bytes = frames * _format.BlockAlign;
        bool rolled = false;
        while (bytes > 0)
        {
            int chunk = (int)Math.Min(bytes, zeros.Length);
            rolled |= WriteAudio(zeros[..chunk]);
            bytes -= chunk;
        }

        return rolled;
    }

    private bool RollIfFull()
    {
        if (_segmentWritten < _segmentBytes)
        {
            return false;
        }

        CloseCurrentSegment();
        OpenSegment();
        return true;
    }

    private void OpenSegment()
    {
        string path = Path.Combine(_directory, $"{_baseName}-{_segments.Count + 1:0000}.wav");
        var file = _openFile(path);
        try
        {
            _writer = new WaveFileWriter(file, _format);
            _file = file;
        }
        catch
        {
            file.Dispose();
            throw;
        }

        _segments.Add(path);
        _segmentWritten = 0;
        _lastFlush = DateTime.UtcNow;
    }

    private void CloseCurrentSegment()
    {
        try
        {
            _writer?.Dispose(); // writes the final header: the segment is complete
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The header could not be finalized (disk full); the data before the last flush stays readable.
        }

        try
        {
            _file?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        _writer = null;
        _file = null;
    }

    private void OnStopped(object? sender, Exception? error)
    {
        if (error is not null)
        {
            Failed?.Invoke(this, error);
        }

        _stopped.TrySetResult();
    }

    private void CloseWriter()
    {
        lock (_lock)
        {
            _closed = true;
            CloseCurrentSegment();
        }
    }

    /// <summary>True once the files are closed (stopped or disposed).</summary>
    public bool Closed
    {
        get
        {
            lock (_lock)
            {
                return _closed;
            }
        }
    }

    public void Dispose()
    {
        _stream.PacketAvailable -= OnPacket;
        _stream.Stopped -= OnStopped;
        CloseWriter();
        _stream.Dispose();
    }
}
