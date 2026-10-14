namespace FloatingMemo.Services.Transcription;

/// <summary>A piece of recognized speech with its position in the audio.</summary>
public sealed record TranscriptSegment(TimeSpan Start, TimeSpan End, string Text);

/// <summary>
/// The full result. Segments keep timestamps so later features (near-real-time display,
/// meeting minutes) can build on them without changing this contract.
/// </summary>
public sealed record TranscriptionResult(string Text, IReadOnlyList<TranscriptSegment> Segments)
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>Speech-to-text. The MVP implementation runs Whisper locally; there is no cloud implementation.</summary>
public interface ITranscriptionService
{
    /// <param name="audio">16 kHz mono 16-bit PCM WAV.</param>
    /// <param name="progress">0–1 share of the speech actually processed so far (not an estimate of time).</param>
    Task<TranscriptionResult> TranscribeAsync(Stream audio, CancellationToken cancellationToken = default, IProgress<double>? progress = null);
}

/// <summary>Transcription failed; <see cref="Exception.Message"/> is written for the user.</summary>
public sealed class TranscriptionException : Exception
{
    public TranscriptionException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    /// <summary>True when the Whisper model file is missing (the fix is to run the model setup).</summary>
    public bool IsModelMissing { get; init; }
}
