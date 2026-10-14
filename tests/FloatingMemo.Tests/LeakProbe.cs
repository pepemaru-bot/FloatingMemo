using System.Diagnostics;
using FloatingMemo.Services.Audio;
using FloatingMemo.Services.Transcription;
using FloatingMemo.Tests.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests;

/// <summary>Runs only when FLOATINGMEMO_LEAK=1: OS handle growth per transcription / recording.</summary>
public sealed class LeakProbeFactAttribute : FactAttribute
{
    public LeakProbeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_LEAK") != "1")
        {
            Skip = "Set FLOATINGMEMO_LEAK=1 to run the handle-leak probe.";
        }
    }
}

public sealed class LeakProbe(ITestOutputHelper output)
{
    private static int Handles()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        using var p = Process.GetCurrentProcess();
        p.Refresh();
        return p.HandleCount;
    }

    [LeakProbeFact]
    public async Task HandlesPerTranscription()
    {
        string wav = Path.Combine(Path.GetTempPath(), "FloatingMemoE2E", "speech-ja.wav");
        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja");
        for (int i = 0; i < 3; i++)
        {
            await using var warm = File.OpenRead(wav);
            await service.TranscribeAsync(warm);
        }

        int before = Handles();
        for (int i = 0; i < 30; i++)
        {
            await using var s = File.OpenRead(wav);
            await service.TranscribeAsync(s);
        }

        int after = Handles();
        output.WriteLine($"transcription: {before} -> {after} (+{after - before} over 30)");
    }

    [LeakProbeFact]
    public async Task HandlesPerRecording()
    {
        string wav = Path.Combine(Path.GetTempPath(), "FloatingMemoE2E", "speech-ja.wav");
        string dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));
        using var recorder = new AudioRecorder(new WavFileCaptureFactory(wav), dir);
        for (int i = 0; i < 3; i++)
        {
            recorder.Start(AudioSources.Microphone);
            await Task.Delay(200);
            (await recorder.StopAsync()).Delete();
        }

        int before = Handles();
        for (int i = 0; i < 30; i++)
        {
            recorder.Start(AudioSources.Microphone);
            await Task.Delay(200);
            (await recorder.StopAsync()).Delete();
        }

        int after = Handles();
        output.WriteLine($"recording: {before} -> {after} (+{after - before} over 30)");
        Directory.Delete(dir, true);
    }
}
