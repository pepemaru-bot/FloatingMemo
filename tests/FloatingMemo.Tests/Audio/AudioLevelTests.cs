using FloatingMemo.Services.Audio;
using NAudio.Wave;

namespace FloatingMemo.Tests.Audio;

public sealed class AudioLevelTests
{
    private static byte[] Sine(WaveFormat format, float amplitude, int frames = 4800)
    {
        int bytesPerSample = format.BitsPerSample / 8;
        bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat || format is WaveFormatExtensible { BitsPerSample: 32 };
        var data = new byte[frames * format.Channels * bytesPerSample];
        for (int f = 0; f < frames; f++)
        {
            double sample = amplitude * Math.Sin(2 * Math.PI * 1000 * f / format.SampleRate);
            for (int ch = 0; ch < format.Channels; ch++)
            {
                var span = data.AsSpan(((f * format.Channels) + ch) * bytesPerSample, bytesPerSample);
                switch (isFloat, bytesPerSample)
                {
                    case (true, 4): BitConverter.TryWriteBytes(span, (float)sample); break;
                    case (false, 2): BitConverter.TryWriteBytes(span, (short)(sample * 32767)); break;
                    case (false, 3):
                        int v = (int)(sample * 8388607);
                        span[0] = (byte)v; span[1] = (byte)(v >> 8); span[2] = (byte)(v >> 16);
                        break;
                    case (false, 4): BitConverter.TryWriteBytes(span, (int)(sample * int.MaxValue)); break;
                }
            }
        }

        return data;
    }

    public static TheoryData<WaveFormat> AllFormats => new()
    {
        new WaveFormatExtensible(48_000, 32, 2),        // typical shared-mode device (float)
        WaveFormat.CreateIeeeFloatWaveFormat(44_100, 1),
        new WaveFormat(16_000, 16, 1),
        new WaveFormat(48_000, 24, 2),
        new WaveFormat(48_000, 32, 2),                   // 32-bit integer PCM
    };

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Rms_OfSine_IsAmplitudeOverRoot2_InEveryFormat(WaveFormat format)
    {
        float rms = AudioLevel.Rms(Sine(format, 0.5f), format);
        Assert.InRange(rms, 0.5f / MathF.Sqrt(2) * 0.98f, 0.5f / MathF.Sqrt(2) * 1.02f);
    }

    [Theory]
    [MemberData(nameof(AllFormats))]
    public void Rms_OfSilence_IsZero(WaveFormat format)
    {
        Assert.Equal(0f, AudioLevel.Rms(new byte[format.BlockAlign * 100], format));
        Assert.Equal(0f, AudioLevel.ToMeter(0f));
    }

    [Fact]
    public void Meter_IsLinearInDecibels()
    {
        Assert.Equal(-6.02f, AudioLevel.ToDbfs(0.5f), 0.01f);
        Assert.Equal(0f, AudioLevel.ToMeter(AudioLevelFromDb(-70)));           // below the floor
        Assert.Equal(0.5f, AudioLevel.ToMeter(AudioLevelFromDb(-33)), 0.01f);  // half way
        Assert.Equal(1f, AudioLevel.ToMeter(AudioLevelFromDb(-3)));            // loud: full
    }

    private static float AudioLevelFromDb(float db) => MathF.Pow(10, db / 20);

    [Fact]
    public void Smoother_RisesFast_FallsSlowly_AndSettlesAtZero()
    {
        var smoother = new LevelSmoother();
        float afterAttack = smoother.Update(1f, TimeSpan.FromMilliseconds(33));
        Assert.True(afterAttack > 0.6f, $"attack too slow: {afterAttack}");

        for (int i = 0; i < 10; i++)
        {
            smoother.Update(1f, TimeSpan.FromMilliseconds(33));
        }

        float afterRelease = smoother.Update(0f, TimeSpan.FromMilliseconds(33));
        Assert.True(afterRelease > 0.8f, $"release too fast: {afterRelease}");

        for (int i = 0; i < 60; i++)
        {
            smoother.Update(0f, TimeSpan.FromMilliseconds(33));
        }

        Assert.Equal(0f, smoother.Value);
    }

    [Fact]
    public void Monitor_ReportsLoudestSinceLastRead_AndThenResets()
    {
        var monitor = new AudioLevelMonitor();
        var format = new WaveFormat(16_000, 16, 1);
        monitor.Reset(AudioSources.Microphone);
        monitor.Report(AudioSources.Microphone, Sine(format, 0.8f), false, format);
        monitor.Report(AudioSources.Microphone, Sine(format, 0.1f), false, format);

        var first = monitor.Read(AudioSources.Microphone);
        Assert.True(first.Receiving);
        Assert.InRange(first.Rms, 0.5f, 0.6f);
        Assert.Equal(0f, monitor.Read(AudioSources.Microphone).Rms);
    }

    [Fact]
    public void Monitor_SilentFlaggedPackets_CountAsReceivingSilence()
    {
        var monitor = new AudioLevelMonitor();
        var format = new WaveFormat(16_000, 16, 1);
        monitor.Reset(AudioSources.Microphone);
        var garbage = Enumerable.Repeat((byte)0x55, 3200).ToArray(); // devices may leave junk in silent buffers
        monitor.Report(AudioSources.Microphone, garbage, true, format);

        var reading = monitor.Read(AudioSources.Microphone);
        Assert.True(reading.Receiving);
        Assert.Equal(0f, reading.Rms);
    }

    [Fact]
    public async Task Monitor_NoBuffers_IsReportedAsNotReceiving()
    {
        var monitor = new AudioLevelMonitor();
        Assert.False(monitor.Read(AudioSources.Microphone).Receiving); // never started

        monitor.Reset(AudioSources.Microphone);
        Assert.True(monitor.Read(AudioSources.Microphone).Receiving);  // just started: grace period
        await Task.Delay(AudioLevelMonitor.NoSignalAfter + TimeSpan.FromMilliseconds(200));
        Assert.False(monitor.Read(AudioSources.Microphone).Receiving); // device delivers nothing
    }

    [Fact]
    public async Task Recorder_ReportsLevelsPerSource_WhileRecording()
    {
        string dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));
        var micFormat = Formats.DeviceFloatStereo48k;
        SyntheticCaptureStream? mic = null;
        var factory = new SyntheticCaptureFactory()
            .With(AudioSources.Microphone, () => mic = new SyntheticCaptureStream(AudioSources.Microphone, micFormat,
                [new Segment(SegmentKind.Tone, TimeSpan.FromSeconds(0.5), Amplitude: 0.5f)]))
            .With(AudioSources.SystemAudio, () => new SyntheticCaptureStream(AudioSources.SystemAudio, micFormat,
                [new Segment(SegmentKind.Gap, TimeSpan.FromSeconds(0.5))]));
        using var recorder = new AudioRecorder(factory, dir);
        try
        {
            recorder.Start(AudioSources.Microphone | AudioSources.SystemAudio);
            mic!.TimelineDelivered.Wait(TimeSpan.FromSeconds(5));

            Assert.InRange(recorder.Levels.Read(AudioSources.Microphone).Rms, 0.3f, 0.4f);
            Assert.Equal(0f, recorder.Levels.Read(AudioSources.SystemAudio).Rms); // independent: nothing played

            (await recorder.StopAsync()).Delete();
            Assert.False(recorder.Levels.Read(AudioSources.Microphone).Receiving); // cleared after stop
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }

    [Fact]
    public void MicrophoneTest_ShowsLevels_WritesNothing_AndReleasesTheDevice()
    {
        var monitor = new AudioLevelMonitor();
        SyntheticCaptureStream? stream = null;
        var factory = new SyntheticCaptureFactory().With(AudioSources.Microphone, () => stream = new SyntheticCaptureStream(
            AudioSources.Microphone, Formats.Pcm16Mono44k, [new Segment(SegmentKind.Tone, TimeSpan.FromSeconds(0.3), Amplitude: 0.25f)]));
        using var test = new MicrophoneTest(factory, monitor);
        Assert.Empty(factory.Created); // nothing opened until the user starts the test

        test.Start();
        stream!.TimelineDelivered.Wait(TimeSpan.FromSeconds(5));
        Assert.True(test.IsRunning);
        Assert.InRange(monitor.Read(AudioSources.Microphone).Rms, 0.15f, 0.2f);

        test.Stop();
        Assert.False(test.IsRunning);
        Assert.True(stream.Disposed); // device released
        Assert.False(monitor.Read(AudioSources.Microphone).Receiving);
    }

    [Fact]
    public void MicrophoneTest_DeviceMissing_ThrowsNamedError()
    {
        var factory = new SyntheticCaptureFactory().With(AudioSources.Microphone,
            () => throw new AudioDeviceUnavailableException("「USB マイク」が見つかりません。"));
        using var test = new MicrophoneTest(factory, new AudioLevelMonitor());

        var ex = Assert.Throws<AudioCaptureException>(test.Start);
        Assert.Contains("USB マイク", ex.Message);
        Assert.False(test.IsRunning);
    }
}
