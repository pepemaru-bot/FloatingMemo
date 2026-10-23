using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloatingMemo.Services.Audio;

/// <summary>One recorded source (its segment files, in order) and where it starts on the session timeline.</summary>
public sealed record AudioTrack(IReadOnlyList<string> Files, TimeSpan StartOffset)
{
    public AudioTrack(string filePath, TimeSpan startOffset)
        : this([filePath], startOffset)
    {
    }
}

/// <summary>
/// Converts recorded tracks into the format Whisper expects: 16 kHz, mono, 16-bit PCM.
/// When microphone and PC audio were both recorded they are aligned by start time and mixed.
/// </summary>
public static class AudioPreparation
{
    public const int TargetSampleRate = 16_000;

    public static WaveFormat TargetFormat { get; } = new(TargetSampleRate, 16, 1);

    /// <summary>Writes the mixed 16 kHz mono file and returns its duration.</summary>
    public static TimeSpan MixToTranscriptionWav(IReadOnlyList<AudioTrack> tracks, string outputPath)
    {
        if (tracks.Count == 0)
        {
            throw new ArgumentException("No tracks to mix.", nameof(tracks));
        }

        var readers = new List<WaveFileReader>();
        try
        {
            var inputs = new List<ISampleProvider>();
            foreach (var track in tracks)
            {
                ISampleProvider? provider = Concatenate(track.Files, readers);
                if (provider is null)
                {
                    continue; // no readable segment in this track
                }

                provider = ToMono(provider);
                if (provider.WaveFormat.SampleRate != TargetSampleRate)
                {
                    // Managed (WDL) resampler: no Media Foundation dependency, deterministic.
                    provider = new WdlResamplingSampleProvider(provider, TargetSampleRate);
                }

                inputs.Add(new OffsetSampleProvider(provider) { DelayBy = track.StartOffset });
            }

            if (inputs.Count == 0)
            {
                throw new InvalidDataException("録音ファイルを読み取れませんでした。");
            }

            ISampleProvider mixed = inputs.Count == 1
                ? inputs[0]
                : new MixingSampleProvider(inputs) { ReadFully = false };

            // Mic + PC audio are summed; loud passages would be hard-clipped to ±1.0 and the
            // distortion confuses speech recognition. A soft limiter rounds off only the peaks.
            WaveFileWriter.CreateWaveFile16(outputPath, new SoftLimiterSampleProvider(mixed));
        }
        finally
        {
            foreach (var reader in readers)
            {
                reader.Dispose();
            }
        }

        using var result = new WaveFileReader(outputPath);
        return result.TotalTime;
    }

    /// <summary>
    /// The segments of one track played one after another. A segment that cannot be read (e.g.
    /// cut off by a crash before its header was ever written) becomes silence of the same length,
    /// estimated from its size, so the rest of the track keeps its place on the timeline.
    /// </summary>
    internal static ISampleProvider? Concatenate(IReadOnlyList<string> files, List<WaveFileReader> readers)
    {
        var parts = new List<ISampleProvider>();
        WaveFormat? format = null;
        int blockAlign = 0;
        var unreadable = new List<(int Index, long Bytes)>();
        foreach (string file in files)
        {
            try
            {
                var reader = new WaveFileReader(file);
                readers.Add(reader);
                var samples = reader.ToSampleProvider();
                format ??= samples.WaveFormat;
                blockAlign = blockAlign == 0 ? reader.WaveFormat.BlockAlign : blockAlign;
                if (!SameLayout(format, samples.WaveFormat))
                {
                    throw new InvalidDataException("録音の途中で音声の形式が変わりました。");
                }

                parts.Add(samples);
            }
            catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or ArgumentException or EndOfStreamException)
            {
                unreadable.Add((parts.Count, File.Exists(file) ? new FileInfo(file).Length : 0));
                parts.Add(null!); // placeholder, replaced below once the format is known
            }
        }

        if (format is null)
        {
            return null;
        }

        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i] is null)
            {
                long bytes = unreadable.First(u => u.Index == i).Bytes;
                // Frames from the file size (44 bytes = the basic WAV header).
                long frames = Math.Max(0, bytes - 44) / blockAlign;
                parts[i] = new SilenceProvider(format, frames);
            }
        }

        return parts.Count == 1 ? parts[0] : new ConcatenatingSampleProvider(parts);
    }

    private static bool SameLayout(WaveFormat a, WaveFormat b) => a.SampleRate == b.SampleRate && a.Channels == b.Channels;

    /// <summary>A fixed number of silent frames.</summary>
    private sealed class SilenceProvider(WaveFormat format, long frames) : ISampleProvider
    {
        private long _left = frames * format.Channels;

        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(format.SampleRate, format.Channels);

        public int Read(Span<float> buffer)
        {
            int n = (int)Math.Min(buffer.Length, _left);
            buffer[..n].Clear();
            _left -= n;
            return n;
        }
    }

    private static ISampleProvider ToMono(ISampleProvider source) =>
        source.WaveFormat.Channels == 1 ? source : new DownmixToMonoSampleProvider(source);

    /// <summary>
    /// Leaves samples below 0.9 untouched and compresses anything above smoothly towards 1.0
    /// instead of clipping it.
    /// </summary>
    internal sealed class SoftLimiterSampleProvider(ISampleProvider source) : ISampleProvider
    {
        private const float Knee = 0.9f;

        public WaveFormat WaveFormat => source.WaveFormat;

        public int Read(Span<float> buffer)
        {
            int read = source.Read(buffer);
            for (int i = 0; i < read; i++)
            {
                buffer[i] = Limit(buffer[i]);
            }

            return read;
        }

        internal static float Limit(float x)
        {
            float magnitude = Math.Abs(x);
            if (magnitude <= Knee)
            {
                return x;
            }

            float over = (magnitude - Knee) / (1 - Knee);
            return MathF.CopySign(Knee + ((1 - Knee) * MathF.Tanh(over)), x);
        }
    }

    /// <summary>Averages all channels (works for stereo and multi-channel devices).</summary>
    private sealed class DownmixToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _buffer = [];

        public DownmixToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(Span<float> buffer)
        {
            int needed = buffer.Length * _channels;
            if (_buffer.Length < needed)
            {
                _buffer = new float[needed];
            }

            int read = _source.Read(_buffer.AsSpan(0, needed));
            int frames = read / _channels;
            for (int frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                for (int ch = 0; ch < _channels; ch++)
                {
                    sum += _buffer[(frame * _channels) + ch];
                }

                buffer[frame] = sum / _channels;
            }

            return frames;
        }
    }
}
