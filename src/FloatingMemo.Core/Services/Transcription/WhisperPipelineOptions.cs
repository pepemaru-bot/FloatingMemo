using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloatingMemo.Services.Transcription;

/// <summary>Counter-measures against Whisper hallucinations (see DEVELOPMENT_LOG, phase C).</summary>
/// <param name="SkipSilence">Send only detected speech to Whisper (no long silent stretches).</param>
/// <param name="NoContext">Do not condition each window on the previous window's text.</param>
/// <param name="GuardRepetitions">Drop repeated lines over silent audio / physically impossible repeats.</param>
public sealed record WhisperPipelineOptions(bool SkipSilence, bool NoContext, bool GuardRepetitions)
{
    public static WhisperPipelineOptions Default { get; } = new(true, true, true);

    /// <summary>Behaviour before the fix (for comparison in the benchmark).</summary>
    public static WhisperPipelineOptions Baseline { get; } = new(false, false, false);
}

/// <summary>What happened during one transcription (numbers only, never text).</summary>
public sealed record TranscriptionDiagnostics(
    TimeSpan AudioDuration,
    TimeSpan SpeechDuration,
    int RegionsTranscribed,
    int RawSegments,
    int RepeatsRemoved,
    TimeSpan Elapsed);

/// <summary>Reads a WAV stream as 16 kHz mono float samples (resampling/downmixing if needed).</summary>
internal static class AudioPreparationReader
{
    public static float[] ReadMono16k(Stream wav)
    {
        using var reader = new WaveFileReader(wav);
        ISampleProvider provider = reader.ToSampleProvider();
        if (provider.WaveFormat.Channels == 2)
        {
            provider = new StereoToMonoSampleProvider(provider);
        }
        else if (provider.WaveFormat.Channels != 1)
        {
            throw new TranscriptionException("対応していない音声形式です（モノラルまたはステレオのみ）。");
        }

        if (provider.WaveFormat.SampleRate != Audio.AudioPreparation.TargetSampleRate)
        {
            provider = new WdlResamplingSampleProvider(provider, Audio.AudioPreparation.TargetSampleRate);
        }

        var samples = new List<float>((int)(reader.TotalTime.TotalSeconds * Audio.AudioPreparation.TargetSampleRate) + 1024);
        var buffer = new float[16_000];
        int read;
        while ((read = provider.Read(buffer)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read));
        }

        return samples.ToArray();
    }
}
