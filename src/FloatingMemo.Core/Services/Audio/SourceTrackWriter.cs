using NAudio.Wave;

namespace FloatingMemo.Services.Audio;

/// <summary>
/// Writes one capture stream to its own WAV file in the device's native format.
/// </summary>
/// <remarks>
/// WASAPI loopback delivers no packets while nothing is playing, so a naive writer would
/// squeeze silent stretches out and drift out of sync with the microphone. Each packet's
/// QPC timestamp tells us where it belongs on the timeline, and any gap is filled with silence.
/// </remarks>
internal sealed class SourceTrackWriter : IDisposable
{
    // Gaps shorter than this are treated as normal timing jitter.
    private const int GapToleranceMs = 50;

    // Guard against a bogus timestamp producing an enormous file.
    private static readonly TimeSpan MaxGap = TimeSpan.FromMinutes(10);

    private readonly object _lock = new();
    private readonly IAudioCaptureStream _stream;
    private readonly long _sessionStartQpc;
    private readonly WaveFormat _format;
    private readonly AudioLevelMonitor? _levels;
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private WaveFileWriter? _writer;
    private long _firstPacketQpc = -1;
    private long _framesWritten;

    public SourceTrackWriter(IAudioCaptureStream stream, string filePath, long sessionStartQpc, AudioLevelMonitor? levels = null)
    {
        _levels = levels;
        _stream = stream;
        _sessionStartQpc = sessionStartQpc;
        _format = stream.WaveFormat;
        FilePath = filePath;
        _writer = new WaveFileWriter(filePath, _format);
        _stream.PacketAvailable += OnPacket;
        _stream.Stopped += OnStopped;
    }

    public AudioSources Source => _stream.Source;

    public string FilePath { get; }

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

    /// <summary>Raised (on the capture thread) if the device stops because of an error.</summary>
    public event EventHandler<Exception>? Failed;

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

        lock (_lock)
        {
            if (_writer is null)
            {
                return;
            }

            int frames = data.Length / _format.BlockAlign;
            if (_firstPacketQpc < 0)
            {
                long first = qpc >= 0 ? qpc : QpcClock.Now;
                _firstPacketQpc = first;
                // TimeSpan ticks are 100 ns, the same unit as QPC positions.
                StartOffset = TimeSpan.FromTicks(Math.Max(0, first - _sessionStartQpc));
            }
            else if (qpc >= 0)
            {
                long expectedFrame = (qpc - _firstPacketQpc) * _format.SampleRate / 10_000_000;
                long gap = expectedFrame - _framesWritten;
                long tolerance = _format.SampleRate * GapToleranceMs / 1000;
                long maxGap = (long)(_format.SampleRate * MaxGap.TotalSeconds);
                if (gap > tolerance && gap < maxGap)
                {
                    WriteSilence(gap);
                }
            }

            if (silent)
            {
                WriteSilence(frames);
            }
            else
            {
                _writer.Write(data[..(frames * _format.BlockAlign)]);
                _framesWritten += frames;
            }
        }
    }

    private void WriteSilence(long frames)
    {
        // All capture formats WASAPI hands out (16/24/32-bit PCM, float) use 0 for silence.
        Span<byte> zeros = stackalloc byte[4096 - (4096 % _format.BlockAlign)];
        zeros.Clear();
        long bytes = frames * _format.BlockAlign;
        while (bytes > 0)
        {
            int chunk = (int)Math.Min(bytes, zeros.Length);
            _writer!.Write(zeros[..chunk]);
            bytes -= chunk;
        }

        _framesWritten += frames;
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
            _writer?.Dispose();
            _writer = null;
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
