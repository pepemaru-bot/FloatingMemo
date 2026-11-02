namespace FloatingMemo.Services.Transcription;

/// <summary>
/// How speech regions become the pieces a recognizer gets (Haas-ja: one request each). The
/// defaults were chosen on the development meetings only (tests/asr/eval, d02 and the dev
/// recordings), never on the held-out ones: short replies kept, sentences kept whole.
/// </summary>
public sealed record SpeechChunkOptions
{
    /// <summary>Context kept around each piece of speech (never into the neighbor's speech).</summary>
    public double PadSeconds { get; init; } = 0.2;

    /// <summary>Regions closer than this are recognized together (0 = every region alone).</summary>
    public double JoinGapSeconds { get; init; } = 0.5;

    /// <summary>Longer speech is split, at its quietest moment, into pieces of at most this length.</summary>
    public double MaxChunkSeconds { get; init; } = 20;

    /// <summary>A split point is searched only after this much of the piece (no tiny pieces).</summary>
    public double MinSplitSeconds { get; init; } = 6;

    /// <summary>
    /// Regions shorter than this (a "はい", "いいえ") are never joined to their neighbors: at the
    /// end of a longer piece the recognizer tends to drop them.
    /// </summary>
    public double ShortSeconds { get; init; } = 0.9;
}

/// <summary>One piece of audio for the recognizer; <see cref="SplitAfter"/> = cut inside speech (not at a pause).</summary>
public readonly record struct SpeechChunk(int StartSample, int EndSample, bool SplitAfter)
{
    public int Length => EndSample - StartSample;
}

/// <summary>
/// Plans the pieces of a recording from its speech regions and joins the recognized texts.
/// </summary>
/// <remarks>
/// Haas-ja (global attention) needs memory growing with the square of the input, so long speech
/// is recognized in pieces. A fixed cut (Silero's 10 s limit) split words in two ("過去問は" →
/// "過去と岩"); here the cut goes to the quietest moment of the stretch. The recognizer often
/// leaves the end of a piece without "。", and the app joins pieces without separators, so
/// statements ran together ("始めます今日は…", even across speakers); <see cref="Join"/> ends
/// each piece that ended at a pause as a sentence. A piece cut inside speech gets nothing.
/// </remarks>
public static class SpeechChunker
{
    private static readonly char[] SentenceEnds = ['。', '．', '.', '！', '？', '!', '?', '」', '』', '）', ')', '…'];

    /// <param name="regions">Speech regions (unpadded, in order) from the voice activity detector.</param>
    /// <param name="totalSamples">Length of the recording.</param>
    /// <param name="quietestPoint">The quietest sample position between two positions (an energy search over the audio).</param>
    public static IReadOnlyList<SpeechChunk> Plan(IReadOnlyList<SpeechRegion> regions, long totalSamples, int sampleRate,
        Func<int, int, int> quietestPoint, SpeechChunkOptions options)
    {
        int pad = (int)(options.PadSeconds * sampleRate);
        int join = (int)(options.JoinGapSeconds * sampleRate);
        int max = (int)(options.MaxChunkSeconds * sampleRate);
        int minSplit = (int)(options.MinSplitSeconds * sampleRate);
        int shortRegion = (int)(options.ShortSeconds * sampleRate);
        var ordered = regions.Where(r => r.EndSample > r.StartSample).OrderBy(r => r.StartSample).ToList();

        // Group: regions closer than JoinGap become one stretch (as long as it fits), except
        // short ones (a reply), which stay alone.
        var stretches = new List<(int Start, int End)>();
        bool lastShort = false;
        foreach (var r in ordered)
        {
            bool isShort = r.Length < shortRegion;
            if (stretches.Count > 0 && !isShort && !lastShort && r.StartSample - stretches[^1].End < join && r.EndSample - stretches[^1].Start <= max)
            {
                stretches[^1] = (stretches[^1].Start, Math.Max(stretches[^1].End, r.EndSample));
            }
            else
            {
                stretches.Add((r.StartSample, r.EndSample));
            }

            lastShort = isShort;
        }

        var chunks = new List<SpeechChunk>();
        for (int i = 0; i < stretches.Count; i++)
        {
            // Padding, but never into the previous or next stretch (a short reply stays alone).
            int before = i > 0 ? (stretches[i - 1].End + stretches[i].Start) / 2 : 0;
            int after = i + 1 < stretches.Count ? (stretches[i].End + stretches[i + 1].Start) / 2 : (int)Math.Min(totalSamples, int.MaxValue);
            int start = Math.Max(before, stretches[i].Start - pad);
            int end = Math.Min(after, stretches[i].End + pad);

            // Long speech: cut at the quietest moment between MinSplit and Max from the piece's start.
            while (end - start > max)
            {
                int cut = quietestPoint(start + Math.Min(minSplit, max / 2), start + max);
                cut = Math.Clamp(cut, start + 1, start + max);
                chunks.Add(new SpeechChunk(start, cut, SplitAfter: true));
                start = cut;
            }

            chunks.Add(new SpeechChunk(start, end, SplitAfter: false));
        }

        return chunks;
    }

    /// <summary>
    /// The recognized pieces as timed segments: a piece that ended at a pause and has no
    /// sentence end gets "。" (a trailing "、" is kept: the speaker paused inside a sentence).
    /// </summary>
    public static List<TranscriptSegment> Join(IReadOnlyList<(SpeechChunk Chunk, string Text)> pieces, int sampleRate, bool addSentenceEnds = true)
    {
        var segments = new List<TranscriptSegment>();
        foreach (var (chunk, raw) in pieces)
        {
            string text = raw.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            if (addSentenceEnds && !chunk.SplitAfter && Array.IndexOf(SentenceEnds, text[^1]) < 0 && text[^1] is not ('、' or ','))
            {
                text += "。";
            }

            segments.Add(new TranscriptSegment(TimeSpan.FromSeconds((double)chunk.StartSample / sampleRate), TimeSpan.FromSeconds((double)chunk.EndSample / sampleRate), text));
        }

        return segments;
    }
}
