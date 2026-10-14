namespace FloatingMemo.Services.Transcription;

/// <summary>A stretch of audio that contains (probably) speech.</summary>
public readonly record struct SpeechRegion(int StartSample, int EndSample)
{
    public int Length => EndSample - StartSample;

    public TimeSpan Start(int sampleRate) => TimeSpan.FromSeconds((double)StartSample / sampleRate);

    public TimeSpan End(int sampleRate) => TimeSpan.FromSeconds((double)EndSample / sampleRate);
}

/// <summary>
/// Simple, deterministic voice-activity detection by loudness (no extra model).
/// </summary>
/// <remarks>
/// Whisper tends to "hallucinate" — often the same short phrase over and over — on long
/// stretches without speech (silence after talking, the silent gaps filled in for PC audio).
/// Only the regions found here are sent to Whisper, so those stretches never reach it.
/// The threshold adapts to the recording's own noise floor, so steady background noise
/// (fans, hum) is treated as silence while quieter speech is still kept.
/// </remarks>
public static class SpeechActivity
{
    private const int FrameMs = 30;

    /// <summary>Frames this much louder than the noise floor count as speech.</summary>
    private const double SpeechAboveFloorDb = 12;

    /// <summary>Never treat anything quieter than this as speech (digital silence, hiss).</summary>
    private const double AbsoluteMinimumDb = -50;

    /// <summary>Gaps shorter than this inside speech (pauses between words) are kept.</summary>
    private const int MergeGapMs = 700;

    /// <summary>Context kept around speech so word starts/ends are not cut.</summary>
    private const int PaddingMs = 250;

    /// <summary>Very short blips (clicks, a cough) are ignored.</summary>
    private const int MinSpeechMs = 200;

    public static IReadOnlyList<SpeechRegion> Detect(ReadOnlySpan<float> samples, int sampleRate)
    {
        int frame = sampleRate * FrameMs / 1000;
        int frames = samples.Length / frame;
        if (frames == 0)
        {
            return [];
        }

        var db = new double[frames];
        for (int f = 0; f < frames; f++)
        {
            db[f] = Rms(samples.Slice(f * frame, frame));
        }

        // Noise floor: a low percentile of frame loudness (robust against speech-heavy audio).
        var sorted = (double[])db.Clone();
        Array.Sort(sorted);
        double floor = sorted[(int)(frames * 0.10)];
        double threshold = Math.Max(floor + SpeechAboveFloorDb, AbsoluteMinimumDb);

        var regions = new List<(int Start, int End)>();
        int? start = null;
        int gap = 0;
        int mergeFrames = MergeGapMs / FrameMs;
        for (int f = 0; f < frames; f++)
        {
            bool speech = db[f] >= threshold;
            if (speech)
            {
                start ??= f;
                gap = 0;
            }
            else if (start is not null && ++gap > mergeFrames)
            {
                regions.Add((start.Value, f - gap + 1));
                start = null;
                gap = 0;
            }
        }

        if (start is not null)
        {
            regions.Add((start.Value, frames - gap));
        }

        int pad = PaddingMs * sampleRate / 1000;
        int minSamples = MinSpeechMs * sampleRate / 1000;
        var result = new List<SpeechRegion>();
        foreach (var (s, e) in regions)
        {
            int from = Math.Max(0, (s * frame) - pad);
            int to = Math.Min(samples.Length, (e * frame) + pad);
            if ((e - s) * frame < minSamples)
            {
                continue;
            }

            // Padding may make neighbours overlap: merge them.
            if (result.Count > 0 && from <= result[^1].EndSample)
            {
                result[^1] = new SpeechRegion(result[^1].StartSample, to);
            }
            else
            {
                result.Add(new SpeechRegion(from, to));
            }
        }

        return result;
    }

    /// <summary>
    /// Joins speech regions separated by short pauses into chunks for Whisper. Each Whisper call
    /// costs a fixed ~1 s, so one call per sentence would be slow; long silences still split
    /// chunks (that is the point), and chunks stay within one 30 s Whisper window.
    /// </summary>
    public static IReadOnlyList<SpeechRegion> GroupIntoChunks(IReadOnlyList<SpeechRegion> speech, int sampleRate,
        double maxGapSeconds = 2.0, double maxChunkSeconds = 28.0)
    {
        var chunks = new List<SpeechRegion>();
        int maxGap = (int)(maxGapSeconds * sampleRate);
        int maxChunk = (int)(maxChunkSeconds * sampleRate);
        foreach (var region in speech)
        {
            if (chunks.Count > 0)
            {
                var last = chunks[^1];
                if (region.StartSample - last.EndSample <= maxGap && region.EndSample - last.StartSample <= maxChunk)
                {
                    chunks[^1] = new SpeechRegion(last.StartSample, region.EndSample);
                    continue;
                }
            }

            chunks.Add(region);
        }

        return chunks;
    }

    /// <summary>Loudness in dBFS of the given span (−120 for digital silence).</summary>
    public static double Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
        {
            return -120;
        }

        double sum = 0;
        foreach (float s in samples)
        {
            sum += s * s;
        }

        double rms = Math.Sqrt(sum / samples.Length);
        return rms <= 1e-6 ? -120 : 20 * Math.Log10(rms);
    }

    /// <summary>Share of <paramref name="region"/> (in seconds) covered by detected speech.</summary>
    public static double SpeechCoverage(IReadOnlyList<SpeechRegion> speech, TimeSpan from, TimeSpan to, int sampleRate)
    {
        long a = (long)(from.TotalSeconds * sampleRate);
        long b = (long)(to.TotalSeconds * sampleRate);
        if (b <= a)
        {
            return 0;
        }

        long covered = 0;
        foreach (var r in speech)
        {
            long s = Math.Max(a, r.StartSample);
            long e = Math.Min(b, r.EndSample);
            if (e > s)
            {
                covered += e - s;
            }
        }

        return (double)covered / (b - a);
    }
}
