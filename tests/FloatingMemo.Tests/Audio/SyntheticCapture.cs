using FloatingMemo.Services.Audio;
using NAudio.Wave;

namespace FloatingMemo.Tests.Audio;

/// <summary>A segment of the synthetic timeline.</summary>
/// <param name="Kind">Tone = sine wave, Silent = packets flagged silent, Gap = no packets at all (like idle loopback).</param>
public sealed record Segment(SegmentKind Kind, TimeSpan Duration, double Frequency = 440, float Amplitude = 0.5f);

public enum SegmentKind
{
    Tone,
    Silent,
    Gap,
}

/// <summary>
/// Fake capture device: emits a scripted timeline of packets with synthetic QPC timestamps,
/// as fast as possible on a background thread. No real audio hardware is touched.
/// </summary>
public sealed class SyntheticCaptureStream : IAudioCaptureStream
{
    private readonly IReadOnlyList<Segment> _timeline;
    private readonly TimeSpan _startDelay;
    private readonly Exception? _failAfterTimeline;
    private readonly ManualResetEventSlim _stopRequested = new();
    private Thread? _thread;

    public SyntheticCaptureStream(AudioSources source, WaveFormat format, IReadOnlyList<Segment> timeline,
        TimeSpan startDelay = default, Exception? failAfterTimeline = null)
    {
        Source = source;
        WaveFormat = format;
        _timeline = timeline;
        _startDelay = startDelay;
        _failAfterTimeline = failAfterTimeline;
    }

    public AudioSources Source { get; }

    public WaveFormat WaveFormat { get; }

    public bool Disposed { get; private set; }

    public event AudioPacketHandler? PacketAvailable;

    public event EventHandler<Exception?>? Stopped;

    /// <summary>Set once every scripted packet has been delivered.</summary>
    public ManualResetEventSlim TimelineDelivered { get; } = new();

    public void Start()
    {
        long baseQpc = QpcClock.Now + _startDelay.Ticks;
        _thread = new Thread(() => Run(baseQpc)) { IsBackground = true };
        _thread.Start();
    }

    public void Stop() => _stopRequested.Set();

    private void Run(long baseQpc)
    {
        const int packetMs = 10;
        int framesPerPacket = WaveFormat.SampleRate * packetMs / 1000;
        var packet = new byte[framesPerPacket * WaveFormat.BlockAlign];
        long frame = 0;

        foreach (var segment in _timeline)
        {
            long segmentFrames = (long)(segment.Duration.TotalSeconds * WaveFormat.SampleRate);
            for (long done = 0; done < segmentFrames; done += framesPerPacket)
            {
                long qpc = baseQpc + (frame * 10_000_000 / WaveFormat.SampleRate);
                if (segment.Kind != SegmentKind.Gap)
                {
                    if (segment.Kind == SegmentKind.Tone)
                    {
                        FillTone(packet, frame, framesPerPacket, segment);
                    }
                    else
                    {
                        // Real devices may leave garbage in silent packets; the writer must ignore it.
                        Array.Fill(packet, (byte)0x55);
                    }

                    PacketAvailable?.Invoke(packet, segment.Kind == SegmentKind.Silent, qpc);
                }

                frame += framesPerPacket;
            }
        }

        TimelineDelivered.Set();
        if (_failAfterTimeline is not null)
        {
            Stopped?.Invoke(this, _failAfterTimeline);
            return;
        }

        _stopRequested.Wait();
        Stopped?.Invoke(this, null);
    }

    private void FillTone(byte[] packet, long startFrame, int frames, Segment segment)
    {
        int channels = WaveFormat.Channels;
        for (int f = 0; f < frames; f++)
        {
            double t = (double)(startFrame + f) / WaveFormat.SampleRate;
            float sample = (float)(segment.Amplitude * Math.Sin(2 * Math.PI * segment.Frequency * t));
            for (int ch = 0; ch < channels; ch++)
            {
                int offset = ((f * channels) + ch) * (WaveFormat.BitsPerSample / 8);
                if (WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat || IsExtensibleFloat(WaveFormat))
                {
                    BitConverter.TryWriteBytes(packet.AsSpan(offset, 4), sample);
                }
                else
                {
                    BitConverter.TryWriteBytes(packet.AsSpan(offset, 2), (short)(sample * short.MaxValue));
                }
            }
        }
    }

    private static bool IsExtensibleFloat(WaveFormat format) =>
        format is WaveFormatExtensible ext && ext.SubFormat == KsDataFormatSubtypeIeeeFloat;

    private static readonly Guid KsDataFormatSubtypeIeeeFloat = new("00000003-0000-0010-8000-00aa00389b71");

    public void Dispose()
    {
        Disposed = true;
        _stopRequested.Set();
    }
}

/// <summary>Hands out pre-built synthetic streams, or throws to simulate a missing device.</summary>
public sealed class SyntheticCaptureFactory : IAudioCaptureFactory
{
    private readonly Dictionary<AudioSources, Func<IAudioCaptureStream>> _streams = [];

    public List<IAudioCaptureStream> Created { get; } = [];

    public SyntheticCaptureFactory With(AudioSources source, Func<IAudioCaptureStream> create)
    {
        _streams[source] = create;
        return this;
    }

    public IAudioCaptureStream Create(AudioSources source)
    {
        var stream = _streams[source]();
        Created.Add(stream);
        return stream;
    }
}

public static class Formats
{
    /// <summary>What a typical Windows device reports: 48 kHz stereo 32-bit float (extensible).</summary>
    public static WaveFormat DeviceFloatStereo48k => new WaveFormatExtensible(48_000, 32, 2);

    public static WaveFormat Pcm16Mono44k => new(44_100, 16, 1);
}
