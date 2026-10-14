using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Transcription;

/// <summary>
/// Detects Whisper's "repetition loops" (e.g. 「読み10ですね」 printed dozens of times) without
/// deleting things that were really said more than once.
/// </summary>
/// <remarks>
/// The test is physical, not textual: repeating a phrase N times takes N times as long to say.
/// A run of identical lines is only shortened where the audio under it has no speech, and a
/// phrase repeated inside one segment is only collapsed when that segment's audio is far too
/// short to have contained all the repetitions (faster than anyone speaks).
/// </remarks>
public static partial class RepetitionGuard
{
    /// <summary>Generous upper bound for Japanese speech (typical: 6–10 characters/second).</summary>
    public const double MaxPlausibleCharsPerSecond = 16;

    /// <summary>A line repeated this many times in a row is examined.</summary>
    private const int SuspiciousRun = 3;

    /// <summary>Below this share of detected speech, a segment's audio counts as silent.</summary>
    private const double SilentCoverage = 0.3;

    /// <param name="segments">Segments in time order (after <see cref="LocalWhisperTranscriptionService.CleanSegment"/>).</param>
    /// <param name="speechCoverage">Share (0–1) of a time span that contains speech.</param>
    /// <param name="removed">How many segments were dropped or shortened (for diagnostics).</param>
    public static List<TranscriptSegment> Apply(
        IReadOnlyList<TranscriptSegment> segments,
        Func<TimeSpan, TimeSpan, double> speechCoverage,
        out int removed)
    {
        removed = 0;
        var result = new List<TranscriptSegment>(segments.Count);
        int i = 0;
        while (i < segments.Count)
        {
            // A run of consecutive segments with the same text.
            int j = i + 1;
            string key = Normalize(segments[i].Text);
            while (j < segments.Count && Normalize(segments[j].Text) == key)
            {
                j++;
            }

            int run = j - i;
            for (int k = i; k < j; k++)
            {
                var segment = segments[k];
                bool repeat = k > i;
                if (repeat && run >= SuspiciousRun && speechCoverage(segment.Start, segment.End) < SilentCoverage)
                {
                    removed++; // the same line again, over audio with no speech: a loop
                    continue;
                }

                var collapsed = CollapseImplausibleRepeats(segment);
                if (!ReferenceEquals(collapsed, segment))
                {
                    removed++;
                }

                result.Add(collapsed);
            }

            i = j;
        }

        return result;
    }

    /// <summary>
    /// "ABCABCABCABC…" inside one segment: collapsed to one "ABC" only if saying all of it would
    /// need more than <see cref="MaxPlausibleCharsPerSecond"/> for the segment's duration.
    /// </summary>
    internal static TranscriptSegment CollapseImplausibleRepeats(TranscriptSegment segment)
    {
        double seconds = Math.Max(0.5, (segment.End - segment.Start).TotalSeconds);
        if (segment.Text.Length / seconds <= MaxPlausibleCharsPerSecond)
        {
            return segment; // could have been said in that time: keep, even if repetitive
        }

        string collapsed = RepeatedUnit().Replace(segment.Text, "$1");
        return collapsed == segment.Text ? segment : segment with { Text = collapsed };
    }

    private static string Normalize(string text) => PunctuationAndSpace().Replace(text, string.Empty);

    // A unit of 2–30 characters repeated 4+ times in a row (with optional separators).
    [GeneratedRegex(@"(.{2,30}?)(?:[、。,.\s]*\1){3,}")]
    private static partial Regex RepeatedUnit();

    [GeneratedRegex(@"[\s、。,.!?！？]")]
    private static partial Regex PunctuationAndSpace();
}
