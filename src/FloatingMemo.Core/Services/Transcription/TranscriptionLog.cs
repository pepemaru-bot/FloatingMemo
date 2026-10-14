using System.Globalization;
using FloatingMemo.Services.Audio;

namespace FloatingMemo.Services.Transcription;

/// <summary>
/// One line per transcription in %TEMP%\FloatingMemo\transcription-diagnostics.log, for
/// diagnosing problems with real recordings (e.g. repeated phrases). Numbers only: never the
/// transcript, never audio (CLAUDE.md logging rules).
/// </summary>
public static class TranscriptionLog
{
    public static string DefaultPath => Path.Combine(Path.GetTempPath(), "FloatingMemo", "transcription-diagnostics.log");

    /// <summary>Longest run of identical consecutive segments (1 = no repetition).</summary>
    public static int MaxConsecutiveRepeat(IReadOnlyList<TranscriptSegment> segments)
    {
        int best = segments.Count > 0 ? 1 : 0, run = 1;
        for (int i = 1; i < segments.Count; i++)
        {
            run = segments[i].Text == segments[i - 1].Text ? run + 1 : 1;
            best = Math.Max(best, run);
        }

        return best;
    }

    public static string Format(DateTime when, AudioSources sources, TranscriptionDiagnostics d, TranscriptionResult result, string modelFile) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{when:yyyy-MM-ddTHH:mm:ss} sources={sources} model={modelFile} audio={d.AudioDuration.TotalSeconds:F1}s " +
            $"speech={d.SpeechDuration.TotalSeconds:F1}s regions={d.RegionsTranscribed} rawSegments={d.RawSegments} " +
            $"removedRepeats={d.RepeatsRemoved} segments={result.Segments.Count} maxRepeat={MaxConsecutiveRepeat(result.Segments)} " +
            $"chars={result.Text.Length} elapsed={d.Elapsed.TotalSeconds:F1}s");

    public static void Append(string line, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Diagnostics must never break transcription.
        }
    }
}
