using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace FloatingMemo.Services.Transcription;

/// <summary>Counter-measures against Whisper hallucinations (see DEVELOPMENT_LOG, phase C).</summary>
/// <param name="SkipSilence">Send only detected speech to Whisper (no long silent stretches).</param>
/// <param name="NoContext">Do not condition each window on the previous window's text.</param>
/// <param name="GuardRepetitions">Drop repeated lines over silent audio / physically impossible repeats.</param>
/// <param name="SileroVadPath">Silero VAD model (ggml) for finding speech; null = the built-in energy detector (default).</param>
/// <param name="CarryInitialPrompt">Give the dictionary hint to every 30 s decode window, not only the first of a call.</param>
/// <param name="Temperature">Whisper's sampling temperature; null = Whisper.net's default.</param>
/// <param name="RecoverDroppedTail">
/// With the dictionary hint, Whisper sometimes stops after the first sentence of a region and
/// the rest (17 s, four statements, in a made-up meeting) never comes back. When a region's last
/// text ends 3 s or more before the region does and the detector still heard speech there, that
/// part is decoded again without the hint.
/// </param>
public sealed record WhisperPipelineOptions(bool SkipSilence, bool NoContext, bool GuardRepetitions,
    string? SileroVadPath = null, bool CarryInitialPrompt = false, float? Temperature = null, bool RecoverDroppedTail = false)
{
    public static WhisperPipelineOptions Default { get; } = new(true, true, true, RecoverDroppedTail: true);

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
