using System.Text.Json;
using System.Text.Json.Serialization;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloatingMemo.Services.Transcription;

/// <summary>
/// Progress of a long transcription, saved after every finished window next to the recording
/// (in the temporary recording folder, deleted with it). A failed or cancelled transcription is
/// resumed from the first unfinished window instead of starting over. It is only used for the
/// same audio (same length) and the same model.
/// </summary>
public sealed class TranscriptionCheckpoint
{
    public const string FileName = "transcript.partial.json";

    public int Version { get; set; } = 1;

    public string Model { get; set; } = string.Empty;

    public long TotalSamples { get; set; }

    /// <summary>Samples (16 kHz) whose transcription is final.</summary>
    public long ProcessedSamples { get; set; }

    public List<CheckpointSegment> Segments { get; set; } = [];

    /// <summary>Speech regions found so far, as [start, end] sample pairs (for the repetition guard).</summary>
    public List<int[]> Speech { get; set; } = [];

    public static TranscriptionCheckpoint? Load(string? path, string model, long totalSamples)
    {
        if (path is null || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var loaded = JsonSerializer.Deserialize(File.ReadAllText(path), TranscriptionCheckpointJsonContext.Default.TranscriptionCheckpoint);
            return loaded is not null && loaded.Model == model && loaded.TotalSamples == totalSamples && loaded.ProcessedSamples <= totalSamples ? loaded : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // started again from the beginning: nothing is lost but time
        }
    }

    /// <summary>Saves atomically. Never throws: without a checkpoint a retry only starts from the beginning.</summary>
    public void TrySave(string? path)
    {
        if (path is null)
        {
            return;
        }

        try
        {
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, TranscriptionCheckpointJsonContext.Default.TranscriptionCheckpoint));
            File.Move(path + ".tmp", path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>One transcribed segment, positions in milliseconds.</summary>
public sealed class CheckpointSegment
{
    public long StartMs { get; set; }

    public long EndMs { get; set; }

    public string Text { get; set; } = string.Empty;
}

[JsonSerializable(typeof(TranscriptionCheckpoint))]
internal sealed partial class TranscriptionCheckpointJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Reads a WAV as 16 kHz mono float samples a piece at a time, so hours of audio never have to
/// be in memory at once (6 hours would be about 1.4 GB as one array).
/// </summary>
internal sealed class Mono16kReader : IDisposable
{
    private readonly WaveFileReader _reader;
    private readonly ISampleProvider _provider;

    public Mono16kReader(Stream wav)
    {
        _reader = new WaveFileReader(wav);
        ISampleProvider provider = _reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels != 1)
        {
            _reader.Dispose();
            throw new TranscriptionException("対応していない音声形式です（モノラルまたはステレオのみ）。");
        }

        if (provider.WaveFormat.SampleRate != Audio.AudioPreparation.TargetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, Audio.AudioPreparation.TargetSampleRate);
        }

        _provider = provider;
        TotalSamples = (long)Math.Round(_reader.TotalTime.TotalSeconds * Audio.AudioPreparation.TargetSampleRate);
    }

    /// <summary>Length in 16 kHz samples (from the header).</summary>
    public long TotalSamples { get; }

    /// <summary>Fills as much of <paramref name="buffer"/> as the file still has.</summary>
    public int Read(Span<float> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = _provider.Read(buffer[total..]);
            if (read <= 0)
            {
                break;
            }

            total += read;
        }

        return total;
    }

    public void Skip(long samples)
    {
        var scratch = new float[16_000];
        while (samples > 0)
        {
            int read = Read(scratch.AsSpan(0, (int)Math.Min(scratch.Length, samples)));
            if (read == 0)
            {
                return;
            }

            samples -= read;
        }
    }

    public void Dispose() => _reader.Dispose();
}
