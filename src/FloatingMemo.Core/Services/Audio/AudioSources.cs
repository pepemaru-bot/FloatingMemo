namespace FloatingMemo.Services.Audio;

/// <summary>Which inputs to record. Microphone and PC audio can be combined.</summary>
[Flags]
public enum AudioSources
{
    None = 0,

    /// <summary>The default recording device (your voice).</summary>
    Microphone = 1,

    /// <summary>What the default playback device is playing (WASAPI loopback).</summary>
    SystemAudio = 2,
}

public static class AudioSourcesExtensions
{
    public static IEnumerable<AudioSources> Each(this AudioSources sources)
    {
        if (sources.HasFlag(AudioSources.Microphone))
        {
            yield return AudioSources.Microphone;
        }

        if (sources.HasFlag(AudioSources.SystemAudio))
        {
            yield return AudioSources.SystemAudio;
        }
    }

    public static string DisplayName(this AudioSources source) => source switch
    {
        AudioSources.Microphone => "マイク",
        AudioSources.SystemAudio => "PC音声",
        _ => source.ToString(),
    };
}
