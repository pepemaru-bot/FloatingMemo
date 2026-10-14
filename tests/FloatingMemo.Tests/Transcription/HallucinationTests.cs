using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Transcription;

public sealed class SpeechActivityTests
{
    private const int Rate = 16_000;

    private static float[] Silence(double seconds) => new float[(int)(seconds * Rate)];

    private static float[] Tone(double seconds, float amplitude = 0.3f, double hz = 220)
    {
        var s = new float[(int)(seconds * Rate)];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / Rate));
        }

        return s;
    }

    private static float[] Noise(double seconds, float amplitude, int seed = 1)
    {
        var random = new Random(seed);
        var s = new float[(int)(seconds * Rate)];
        for (int i = 0; i < s.Length; i++)
        {
            s[i] = (float)((random.NextDouble() * 2 - 1) * amplitude);
        }

        return s;
    }

    private static float[] Concat(params float[][] parts) => parts.SelectMany(p => p).ToArray();

    private static float[] Add(float[] a, float[] b) => a.Zip(b, (x, y) => x + y).ToArray();

    [Fact]
    public void DigitalSilence_HasNoSpeech()
    {
        Assert.Empty(SpeechActivity.Detect(Silence(20), Rate));
    }

    [Fact]
    public void SteadyNoiseOnly_HasNoSpeech()
    {
        Assert.Empty(SpeechActivity.Detect(Noise(20, 0.05f), Rate));
    }

    [Fact]
    public void SpeechThenLongSilence_OnlyTheSpeechIsKept()
    {
        var audio = Concat(Silence(1), Tone(3), Silence(60));

        var regions = SpeechActivity.Detect(audio, Rate);

        var region = Assert.Single(regions);
        Assert.InRange(region.Start(Rate).TotalSeconds, 0.6, 1.0);
        Assert.InRange(region.End(Rate).TotalSeconds, 4.0, 4.4);
    }

    [Fact]
    public void ShortPausesBetweenWords_DoNotSplitSpeech()
    {
        var audio = Concat(Tone(1), Silence(0.4), Tone(1), Silence(0.4), Tone(1));

        Assert.Single(SpeechActivity.Detect(audio, Rate));
    }

    [Fact]
    public void SpeechOverBackgroundNoise_IsFound()
    {
        var noise = Noise(12, 0.02f);
        var speech = Concat(Silence(4), Tone(3, 0.25f), Silence(5));

        var regions = SpeechActivity.Detect(Add(noise, speech), Rate);

        var region = Assert.Single(regions);
        Assert.InRange(region.Start(Rate).TotalSeconds, 3.5, 4.1);
    }

    [Fact]
    public void Chunks_JoinShortPauses_SplitAtLongSilence_AndStayUnder28s()
    {
        var regions = new[]
        {
            new SpeechRegion(0, Rate * 3),
            new SpeechRegion(Rate * 4, Rate * 7),     // 1 s pause: joined
            new SpeechRegion(Rate * 20, Rate * 22),   // 13 s silence: new chunk
            new SpeechRegion(Rate * 23, Rate * 40),   // joined (chunk 20..40 = 20 s)
            new SpeechRegion(Rate * 41, Rate * 52),   // would make 32 s: new chunk
        };

        var chunks = SpeechActivity.GroupIntoChunks(regions, Rate);

        Assert.Equal(
            [new SpeechRegion(0, Rate * 7), new SpeechRegion(Rate * 20, Rate * 40), new SpeechRegion(Rate * 41, Rate * 52)],
            chunks);
    }

    [Fact]
    public void Coverage_ReflectsSpeechShare()
    {
        var regions = new[] { new SpeechRegion(0, Rate * 2) };

        Assert.Equal(1.0, SpeechActivity.SpeechCoverage(regions, TimeSpan.Zero, TimeSpan.FromSeconds(2), Rate), 3);
        Assert.Equal(0.5, SpeechActivity.SpeechCoverage(regions, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3), Rate), 3);
        Assert.Equal(0.0, SpeechActivity.SpeechCoverage(regions, TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(9), Rate), 3);
    }
}

public sealed class MixAndDiagnosticsTests
{
    [Theory]
    [InlineData(0.5f, 0.5f)]
    [InlineData(-0.9f, -0.9f)]
    [InlineData(0.0f, 0.0f)]
    public void SoftLimiter_LeavesNormalLevelsUntouched(float input, float expected)
    {
        Assert.Equal(expected, FloatingMemo.Services.Audio.AudioPreparation.SoftLimiterSampleProvider.Limit(input));
    }

    [Theory]
    [InlineData(1.0f)]
    [InlineData(1.6f)]
    [InlineData(-3.0f)]
    public void SoftLimiter_KeepsPeaksBelowFullScale_WithoutHardClipping(float input)
    {
        float output = FloatingMemo.Services.Audio.AudioPreparation.SoftLimiterSampleProvider.Limit(input);

        Assert.InRange(Math.Abs(output), 0.9f, 1.0f);
        Assert.Equal(Math.Sign(input), Math.Sign(output));
        Assert.True(FloatingMemo.Services.Audio.AudioPreparation.SoftLimiterSampleProvider.Limit(1.6f)
            > FloatingMemo.Services.Audio.AudioPreparation.SoftLimiterSampleProvider.Limit(1.0f), "louder stays louder (no flat clipping)");
    }

    [Fact]
    public void DiagnosticsLine_ContainsNumbersButNeverTheTranscript()
    {
        var segments = new List<TranscriptSegment>
        {
            new(TimeSpan.Zero, TimeSpan.FromSeconds(1), "秘密の会議内容"),
            new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), "秘密の会議内容"),
        };
        var result = new TranscriptionResult("秘密の会議内容秘密の会議内容", segments);
        var d = new TranscriptionDiagnostics(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(20), 3, 12, 10, TimeSpan.FromSeconds(4));

        string line = TranscriptionLog.Format(new DateTime(2026, 10, 10, 2, 0, 0), FloatingMemo.Services.Audio.AudioSources.Microphone, d, result, "ggml-base.bin");

        Assert.DoesNotContain("秘密", line);
        Assert.Contains("maxRepeat=2", line);
        Assert.Contains("removedRepeats=10", line);
        Assert.Contains("audio=60.0s", line);
    }
}

public sealed class RepetitionGuardTests
{
    private static TranscriptSegment Seg(double from, double to, string text) =>
        new(TimeSpan.FromSeconds(from), TimeSpan.FromSeconds(to), text);

    [Fact]
    public void RepeatedLinesOverSilence_AreDropped_FirstOneKept()
    {
        var segments = new List<TranscriptSegment> { Seg(0, 3, "本日の会議を始めます。") };
        for (int i = 0; i < 20; i++)
        {
            segments.Add(Seg(10 + i, 11 + i, "読み10ですね"));
        }

        // speech only in the first 3 seconds; the loop sits on silence
        var result = RepetitionGuard.Apply(segments, (a, b) => b.TotalSeconds <= 3.5 ? 1.0 : 0.0, out int removed);

        Assert.Equal(["本日の会議を始めます。", "読み10ですね"], result.Select(s => s.Text));
        Assert.Equal(19, removed);
    }

    [Fact]
    public void RepeatedLinesOverRealSpeech_AreKept()
    {
        // Someone really said "確認します" three times.
        var segments = new List<TranscriptSegment>
        {
            Seg(0, 1.5, "確認します。"),
            Seg(1.5, 3, "確認します。"),
            Seg(3, 4.5, "確認します。"),
        };

        var result = RepetitionGuard.Apply(segments, (_, _) => 0.9, out int removed);

        Assert.Equal(3, result.Count);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void TwoRepeats_AreNeverTouched()
    {
        var segments = new List<TranscriptSegment> { Seg(0, 1, "はい"), Seg(5, 6, "はい") };

        var result = RepetitionGuard.Apply(segments, (_, _) => 0.0, out int removed);

        Assert.Equal(2, result.Count);
        Assert.Equal(0, removed);
    }

    [Fact]
    public void ImpossiblyFastRepeatsInsideOneSegment_AreCollapsed()
    {
        // 30 repetitions (180+ characters) in 4 seconds: nobody speaks that fast.
        string loop = string.Concat(Enumerable.Repeat("読み4は、", 30));

        var result = RepetitionGuard.Apply([Seg(0, 4, loop)], (_, _) => 1.0, out int removed);

        Assert.Equal("読み4は、", result.Single().Text);
        Assert.Equal(1, removed);
    }

    [Fact]
    public void RealRepetitionAtSpeakingSpeed_IsKept()
    {
        // "はい、はい、はい、はい、はい" said over 3 seconds is plausible speech.
        var segment = Seg(0, 3, "はい、はい、はい、はい、はい");

        var result = RepetitionGuard.Apply([segment], (_, _) => 1.0, out int removed);

        Assert.Equal(segment.Text, result.Single().Text);
        Assert.Equal(0, removed);
    }
}
