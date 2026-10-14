using FloatingMemo.Services.Audio;
using NAudio.Wave;

namespace FloatingMemo.Tests.Audio;

public sealed class AudioRecorderTests : IDisposable
{
    private static readonly TimeSpan S = TimeSpan.FromSeconds(1);
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static SyntheticCaptureStream Stream(AudioSources source, WaveFormat format, TimeSpan startDelay, params Segment[] timeline) =>
        new(source, format, timeline, startDelay);

    private static Segment Tone(double seconds, double hz = 440) => new(SegmentKind.Tone, TimeSpan.FromSeconds(seconds), hz);

    private static Segment Silent(double seconds) => new(SegmentKind.Silent, TimeSpan.FromSeconds(seconds));

    private static Segment Gap(double seconds) => new(SegmentKind.Gap, TimeSpan.FromSeconds(seconds));

    /// <summary>Starts, waits until every synthetic packet is delivered, then stops.</summary>
    private static async Task<RecordedAudio> RecordAsync(AudioRecorder recorder, SyntheticCaptureFactory factory, AudioSources sources)
    {
        recorder.Start(sources);
        foreach (var s in factory.Created.Cast<SyntheticCaptureStream>())
        {
            Assert.True(s.TimelineDelivered.Wait(TimeSpan.FromSeconds(10)));
        }

        return await recorder.StopAsync();
    }

    private static (WaveFormat Format, float[] Samples) ReadAll(string path)
    {
        using var reader = new WaveFileReader(path);
        var provider = reader.ToSampleProvider();
        var samples = new float[reader.SampleCount * reader.WaveFormat.Channels];
        int read = provider.Read(samples);
        return (reader.WaveFormat, samples[..read]);
    }

    private static double Rms(float[] samples, double fromSec, double toSec, int rate = 16_000)
    {
        int from = (int)(fromSec * rate);
        int to = Math.Min(samples.Length, (int)(toSec * rate));
        double sum = 0;
        for (int i = from; i < to; i++)
        {
            sum += samples[i] * samples[i];
        }

        return to > from ? Math.Sqrt(sum / (to - from)) : 0;
    }

    [Fact]
    public async Task Microphone_DeviceFormat_IsConvertedTo16kMono16Bit()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.DeviceFloatStereo48k, TimeSpan.Zero, Tone(1.0)));
        using var recorder = new AudioRecorder(factory, _dir);

        var audio = await RecordAsync(recorder, factory, AudioSources.Microphone);

        Assert.True(audio.HasAudio);
        Assert.Equal(AudioSources.Microphone, audio.CapturedSources);
        var (format, samples) = ReadAll(audio.FilePath!);
        Assert.Equal(16_000, format.SampleRate);
        Assert.Equal(1, format.Channels);
        Assert.Equal(16, format.BitsPerSample);
        Assert.Equal(WaveFormatEncoding.Pcm, format.Encoding);
        Assert.InRange(audio.Duration.TotalSeconds, 0.95, 1.05);
        Assert.InRange(Rms(samples, 0.1, 0.9), 0.3, 0.4); // sine amplitude 0.5 -> RMS ~0.354
    }

    [Fact]
    public async Task Microphone_RawTrackIsDeletedAndOnlyPreparedFileRemains()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.Pcm16Mono44k, TimeSpan.Zero, Tone(0.3)));
        using var recorder = new AudioRecorder(factory, _dir);

        var audio = await RecordAsync(recorder, factory, AudioSources.Microphone);

        Assert.Equal(["recording.wav"], Directory.GetFiles(audio.SessionDirectory).Select(Path.GetFileName));
        audio.Delete();
        Assert.False(Directory.Exists(audio.SessionDirectory));
    }

    [Fact]
    public async Task SystemAudio_GapWithoutPackets_IsFilledWithSilence()
    {
        // Loopback sends nothing while nothing plays: 0.3 s tone, 0.5 s no packets, 0.3 s tone.
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.SystemAudio, () => Stream(AudioSources.SystemAudio, Formats.DeviceFloatStereo48k, TimeSpan.Zero,
                Tone(0.3), Gap(0.5), Tone(0.3)));
        using var recorder = new AudioRecorder(factory, _dir);

        var audio = await RecordAsync(recorder, factory, AudioSources.SystemAudio);

        Assert.InRange(audio.Duration.TotalSeconds, 1.05, 1.15);
        var (_, samples) = ReadAll(audio.FilePath!);
        Assert.True(Rms(samples, 0.05, 0.25) > 0.3);
        Assert.True(Rms(samples, 0.4, 0.7) < 0.01, "gap must be silent");
        Assert.True(Rms(samples, 0.85, 1.05) > 0.3);
    }

    [Fact]
    public async Task SilentFlaggedPackets_AreWrittenAsZeros()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.Pcm16Mono44k, TimeSpan.Zero, Silent(0.5)));
        using var recorder = new AudioRecorder(factory, _dir);

        var audio = await RecordAsync(recorder, factory, AudioSources.Microphone);

        var (_, samples) = ReadAll(audio.FilePath!);
        Assert.Equal(0, Rms(samples, 0, 0.5));
    }

    [Fact]
    public async Task BothSources_AreAlignedByStartTimeAndMixed()
    {
        // Mic is silent for 1 s; PC audio first plays 0.5 s after the session starts.
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.DeviceFloatStereo48k, TimeSpan.Zero, Silent(1.0)))
            .With(AudioSources.SystemAudio, () => Stream(AudioSources.SystemAudio, Formats.Pcm16Mono44k, TimeSpan.FromSeconds(0.5), Tone(0.4, 880)));
        using var recorder = new AudioRecorder(factory, _dir);

        var audio = await RecordAsync(recorder, factory, AudioSources.Microphone | AudioSources.SystemAudio);

        Assert.Equal(AudioSources.Microphone | AudioSources.SystemAudio, audio.CapturedSources);
        var (_, samples) = ReadAll(audio.FilePath!);
        Assert.InRange(audio.Duration.TotalSeconds, 0.95, 1.05);
        Assert.True(Rms(samples, 0.0, 0.4) < 0.01, "PC audio must not start before its timestamp");
        Assert.True(Rms(samples, 0.6, 0.85) > 0.3, "PC audio must be mixed in after 0.5 s");
    }

    [Fact]
    public async Task NoPackets_ReturnsNoAudio()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.SystemAudio, () => Stream(AudioSources.SystemAudio, Formats.DeviceFloatStereo48k, TimeSpan.Zero, Gap(0.5)));
        using var recorder = new AudioRecorder(factory, _dir);

        var audio = await RecordAsync(recorder, factory, AudioSources.SystemAudio);

        Assert.False(audio.HasAudio);
        Assert.Null(audio.FilePath);
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public void Start_WithNoSources_Throws()
    {
        using var recorder = new AudioRecorder(new SyntheticCaptureFactory(), _dir);

        Assert.Throws<ArgumentException>(() => recorder.Start(AudioSources.None));
        Assert.False(recorder.IsRecording);
    }

    [Fact]
    public async Task Start_Twice_Throws()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.Pcm16Mono44k, TimeSpan.Zero, Tone(0.1)));
        using var recorder = new AudioRecorder(factory, _dir);
        recorder.Start(AudioSources.Microphone);

        Assert.Throws<InvalidOperationException>(() => recorder.Start(AudioSources.Microphone));
        (await recorder.StopAsync()).Delete();
    }

    [Fact]
    public void Start_WhenOneDeviceFails_ThrowsNamedErrorAndReleasesOtherDevice()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.Pcm16Mono44k, TimeSpan.Zero, Tone(0.1)))
            .With(AudioSources.SystemAudio, () => throw new InvalidOperationException("no render device"));
        using var recorder = new AudioRecorder(factory, _dir);

        var ex = Assert.Throws<AudioCaptureException>(() => recorder.Start(AudioSources.Microphone | AudioSources.SystemAudio));

        Assert.Equal(AudioSources.SystemAudio, ex.Source);
        Assert.StartsWith("PC音声を取得できませんでした", ex.Message);
        Assert.False(recorder.IsRecording);
        Assert.True(((SyntheticCaptureStream)factory.Created[0]).Disposed, "mic must be released");
        Assert.Empty(Directory.Exists(_dir) ? Directory.GetDirectories(_dir) : []);
    }

    [Theory]
    [InlineData(unchecked((int)0x80070005), AudioSources.Microphone, "プライバシー")]
    [InlineData(unchecked((int)0x80070490), AudioSources.Microphone, "マイクが見つかりません")]
    [InlineData(unchecked((int)0x80070490), AudioSources.SystemAudio, "再生デバイス")]
    [InlineData(unchecked((int)0x8889000A), AudioSources.Microphone, "他のアプリが占有")]
    [InlineData(unchecked((int)0x88890004), AudioSources.SystemAudio, "取り外され")]
    public void CaptureErrors_AreExplainedInJapanese(int hresult, AudioSources source, string expected)
    {
        var inner = new System.Runtime.InteropServices.COMException("raw", hresult);

        var ex = new AudioCaptureException(source, inner);

        Assert.Contains(expected, ex.Message);
        Assert.DoesNotContain("raw", ex.Message);
    }

    [Fact]
    public void CaptureError_UnknownCause_KeepsOriginalMessage()
    {
        var ex = new AudioCaptureException(AudioSources.Microphone, new InvalidOperationException("something odd"));

        Assert.Equal("マイクを開けませんでした（something odd）", ex.Message);
    }

    [Fact]
    public async Task DeviceFailureWhileRecording_RaisesEventAndKeepsCapturedAudio()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => new SyntheticCaptureStream(AudioSources.Microphone, Formats.Pcm16Mono44k,
                [Tone(0.5)], failAfterTimeline: new IOException("device unplugged")));
        using var recorder = new AudioRecorder(factory, _dir);
        AudioCaptureException? failure = null;
        recorder.SourceFailed += (_, e) => failure = e;

        var audio = await RecordAsync(recorder, factory, AudioSources.Microphone);

        Assert.NotNull(failure);
        Assert.Equal(AudioSources.Microphone, failure.Source);
        Assert.True(audio.HasAudio);
        Assert.InRange(audio.Duration.TotalSeconds, 0.45, 0.55);
    }

    [Fact]
    public void Cancel_StopsAndDeletesEverything()
    {
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => Stream(AudioSources.Microphone, Formats.Pcm16Mono44k, TimeSpan.Zero, Tone(0.2)));
        var recorder = new AudioRecorder(factory, _dir);
        recorder.Start(AudioSources.Microphone);

        recorder.Cancel();

        Assert.False(recorder.IsRecording);
        Assert.Empty(Directory.GetDirectories(_dir));
        Assert.True(((SyntheticCaptureStream)factory.Created[0]).Disposed);
    }

    [Fact]
    public void DeleteLeftoverRecordings_RemovesOldSessions()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "rec-old"));
        File.WriteAllText(Path.Combine(_dir, "rec-old", "Microphone.wav"), "x");
        using var recorder = new AudioRecorder(new SyntheticCaptureFactory(), _dir);

        recorder.DeleteLeftoverRecordings();

        Assert.Empty(Directory.GetDirectories(_dir));
    }
}
