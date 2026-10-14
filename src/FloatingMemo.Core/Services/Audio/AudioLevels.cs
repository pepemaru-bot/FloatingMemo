using System.Buffers.Binary;
using System.Diagnostics;
using NAudio.Wave;

namespace FloatingMemo.Services.Audio;

/// <summary>Loudness math for the level indicators (no audio is kept).</summary>
public static class AudioLevel
{
    /// <summary>Quietest level the indicator shows (below this it reads as empty).</summary>
    public const float FloorDb = -60f;

    /// <summary>Level that fills the indicator completely (normal speech peaks below this).</summary>
    public const float FullDb = -6f;

    // KSDATAFORMAT_SUBTYPE_IEEE_FLOAT: shared-mode WASAPI usually hands out 32-bit float this way.
    private static readonly Guid IeeeFloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    /// <summary>
    /// RMS ("root mean square", the average energy) of one buffer, as a linear value where
    /// 1.0 is a full-scale sine-ish signal. All channels are averaged together.
    /// </summary>
    public static float Rms(ReadOnlySpan<byte> data, WaveFormat format)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        if (bytesPerSample == 0 || data.Length < bytesPerSample)
        {
            return 0f;
        }

        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
            || (format is WaveFormatExtensible ext && ext.SubFormat == IeeeFloatSubFormat);
        int count = data.Length / bytesPerSample;
        double sum = 0;
        for (int i = 0; i < count; i++)
        {
            var bytes = data.Slice(i * bytesPerSample, bytesPerSample);
            double sample = (isFloat, bytesPerSample) switch
            {
                (true, 4) => BinaryPrimitives.ReadSingleLittleEndian(bytes),
                (true, 8) => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
                (false, 2) => BinaryPrimitives.ReadInt16LittleEndian(bytes) / 32768.0,
                (false, 3) => ((bytes[0] << 8 | bytes[1] << 16 | bytes[2] << 24) >> 8) / 8388608.0,
                (false, 4) => BinaryPrimitives.ReadInt32LittleEndian(bytes) / 2147483648.0,
                (false, 1) => (bytes[0] - 128) / 128.0, // 8-bit PCM is unsigned
                _ => 0,
            };
            sum += sample * sample;
        }

        return (float)Math.Sqrt(sum / count);
    }

    /// <summary>Decibels relative to full scale (dBFS): 0 = loudest possible, −60 ≈ very quiet.</summary>
    public static float ToDbfs(float rms) => rms <= 1e-6f ? -120f : 20f * MathF.Log10(rms);

    /// <summary>
    /// Maps a level to 0..1 for drawing. Hearing is logarithmic, so the scale is linear in dB
    /// (−60 dB → 0, −6 dB → 1) instead of linear in amplitude, where speech would barely move.
    /// </summary>
    public static float ToMeter(float rms) => Math.Clamp((ToDbfs(rms) - FloorDb) / (FullDb - FloorDb), 0f, 1f);
}

/// <summary>
/// Smooths the indicator: rises quickly when sound starts (attack), falls slowly (release),
/// so it reacts immediately but does not flicker.
/// </summary>
public sealed class LevelSmoother
{
    private readonly double _attackSeconds;
    private readonly double _releaseSeconds;

    public LevelSmoother(double attackSeconds = 0.03, double releaseSeconds = 0.25)
    {
        _attackSeconds = attackSeconds;
        _releaseSeconds = releaseSeconds;
    }

    public float Value { get; private set; }

    public float Update(float target, TimeSpan elapsed)
    {
        double tau = target > Value ? _attackSeconds : _releaseSeconds;
        double coefficient = 1 - Math.Exp(-Math.Max(0, elapsed.TotalSeconds) / tau);
        Value += (float)((target - Value) * coefficient);
        if (Value < 0.002f)
        {
            Value = 0; // settle at exactly empty
        }

        return Value;
    }

    public void Reset() => Value = 0;
}

/// <summary>What the indicator should show for one source.</summary>
public readonly record struct LevelReading(float Rms, bool Receiving);

/// <summary>
/// Latest loudness per source, written by capture threads and read by the UI timer. Only a
/// number is kept, never audio. The loudest buffer since the last read is reported, so short
/// sounds are not missed between UI frames.
/// </summary>
public sealed class AudioLevelMonitor
{
    /// <summary>No buffer for this long means the device delivers nothing (not just silence).</summary>
    public static readonly TimeSpan NoSignalAfter = TimeSpan.FromSeconds(1);

    private readonly object _lock = new();
    private readonly Dictionary<AudioSources, Slot> _slots = [];

    /// <summary>Called on the capture thread for each buffer (cheap: one pass over the samples).</summary>
    public void Report(AudioSources source, ReadOnlySpan<byte> data, bool silent, WaveFormat format)
    {
        float rms = silent ? 0f : AudioLevel.Rms(data, format);
        long now = Stopwatch.GetTimestamp();
        lock (_lock)
        {
            var slot = Get(source);
            slot.Peak = Math.Max(slot.Peak, rms);
            slot.LastPacket = now;
        }
    }

    /// <summary>Starts a source afresh (nothing received yet).</summary>
    public void Reset(AudioSources source)
    {
        lock (_lock)
        {
            var slot = Get(source);
            slot.Peak = 0;
            slot.LastPacket = Stopwatch.GetTimestamp();
            slot.Started = true;
        }
    }

    public void Clear(AudioSources source)
    {
        lock (_lock)
        {
            _slots.Remove(source);
        }
    }

    /// <summary>The loudest level since the previous read, and whether buffers are still arriving.</summary>
    public LevelReading Read(AudioSources source)
    {
        lock (_lock)
        {
            if (!_slots.TryGetValue(source, out var slot) || !slot.Started)
            {
                return new LevelReading(0, false);
            }

            float peak = slot.Peak;
            slot.Peak = 0;
            bool receiving = Stopwatch.GetElapsedTime(slot.LastPacket) < NoSignalAfter;
            return new LevelReading(peak, receiving);
        }
    }

    private Slot Get(AudioSources source)
    {
        if (!_slots.TryGetValue(source, out var slot))
        {
            slot = new Slot { Started = true, LastPacket = Stopwatch.GetTimestamp() };
            _slots[source] = slot;
        }

        return slot;
    }

    private sealed class Slot
    {
        public float Peak;
        public long LastPacket;
        public bool Started;
    }
}

/// <summary>
/// "Mic test": opens the chosen microphone only to show its level. Nothing is written to disk,
/// it starts only when the user asks, and the device is released on <see cref="Stop"/>.
/// </summary>
public sealed class MicrophoneTest : IDisposable
{
    private readonly IAudioCaptureFactory _factory;
    private readonly AudioLevelMonitor _levels;
    private IAudioCaptureStream? _stream;

    public MicrophoneTest(IAudioCaptureFactory factory, AudioLevelMonitor levels)
    {
        _factory = factory;
        _levels = levels;
    }

    public bool IsRunning => _stream is not null;

    /// <summary>The device stopped by itself (e.g. unplugged). Raised on a capture thread.</summary>
    public event EventHandler<AudioCaptureException>? Failed;

    public void Start()
    {
        if (_stream is not null)
        {
            return;
        }

        IAudioCaptureStream? stream = null;
        try
        {
            stream = _factory.Create(AudioSources.Microphone);
            var format = stream.WaveFormat;
            stream.PacketAvailable += (data, silent, _) => _levels.Report(AudioSources.Microphone, data, silent, format);
            stream.Stopped += (_, error) =>
            {
                if (error is not null)
                {
                    Failed?.Invoke(this, new AudioCaptureException(AudioSources.Microphone, error));
                }
            };
            _levels.Reset(AudioSources.Microphone);
            stream.Start();
            _stream = stream;
        }
        catch (Exception ex)
        {
            stream?.Dispose();
            _levels.Clear(AudioSources.Microphone);
            throw ex as AudioCaptureException ?? new AudioCaptureException(AudioSources.Microphone, ex);
        }
    }

    public void Stop()
    {
        var stream = _stream;
        _stream = null;
        if (stream is null)
        {
            return;
        }

        try
        {
            stream.Stop();
        }
        catch
        {
            // A device that already failed may throw on stop; it is released below anyway.
        }

        stream.Dispose();
        _levels.Clear(AudioSources.Microphone);
    }

    public void Dispose() => Stop();
}
