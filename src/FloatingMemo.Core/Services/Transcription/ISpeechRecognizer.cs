namespace FloatingMemo.Services.Transcription;

/// <summary>
/// A local speech recognizer the app can transcribe recordings with (Whisper, Haas-ja). Each
/// returns the same result: the joined text and its timed segments.
/// </summary>
public interface ISpeechRecognizer : IDisposable
{
    /// <summary>Short id stored with jobs and settings ("whisper", "haas-ja").</summary>
    string Name { get; }

    /// <summary>Shown to the user (e.g. "Whisper small", "Haas-ja（試験的）").</summary>
    string DisplayName { get; }

    /// <summary>False until the model is loaded (the status says "preparing" until then).</summary>
    bool IsModelLoaded { get; }

    /// <summary>Numbers only (never text) about the last transcription, for the diagnostics log.</summary>
    TranscriptionDiagnostics? LastDiagnostics { get; }

    /// <summary>Why it cannot run (a missing file), in words for the user; null when it can.</summary>
    string? MissingFiles();

    /// <param name="wavPath">16 kHz mono 16-bit PCM WAV (the recorder's transcription file).</param>
    /// <param name="progress">0–1 share of the audio processed so far.</param>
    /// <param name="checkpointPath">Where finished parts are saved, so a retry continues (null = none).</param>
    Task<TranscriptionResult> TranscribeFileAsync(string wavPath, CancellationToken cancellationToken, IProgress<double>? progress, string? checkpointPath);

    /// <summary>Gives the model's memory back (one AI model at a time).</summary>
    void ReleaseModel();
}
