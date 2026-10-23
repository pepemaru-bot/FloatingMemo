using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Transcription;

/// <summary>Choosing the Whisper model never touches model files; an unavailable choice falls back to the default.</summary>
public sealed class WhisperModelChoiceTests : IDisposable
{
    private readonly string _app = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", "wm-app-" + Guid.NewGuid().ToString("N"));
    private readonly string _user = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", "wm-user-" + Guid.NewGuid().ToString("N"));

    public WhisperModelChoiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_app, "whisper"));
        Directory.CreateDirectory(_user);
    }

    public void Dispose()
    {
        Directory.Delete(_app, recursive: true);
        Directory.Delete(_user, recursive: true);
    }

    [Fact]
    public void ChosenModel_IsUsedWhenInstalled_OtherwiseTheDefault()
    {
        File.WriteAllText(Path.Combine(_app, "whisper", "ggml-base.bin"), "base");
        File.WriteAllText(Path.Combine(_user, "ggml-small.bin"), "small");

        var installed = LocalWhisperTranscriptionService.InstalledModels(_app, _user);
        Assert.Equal(["ggml-base.bin", "ggml-small.bin"], installed.Select(m => m.Name));

        Assert.EndsWith("ggml-small.bin", LocalWhisperTranscriptionService.ResolveModelPath("ggml-small.bin", _app, _user));
        Assert.EndsWith("ggml-base.bin", LocalWhisperTranscriptionService.ResolveModelPath(null, _app, _user)); // auto: base first
        Assert.EndsWith("ggml-base.bin", LocalWhisperTranscriptionService.ResolveModelPath("ggml-large.bin", _app, _user)); // not installed
        Assert.Equal("base", File.ReadAllText(Path.Combine(_app, "whisper", "ggml-base.bin"))); // files untouched
        Assert.Equal("small", File.ReadAllText(Path.Combine(_user, "ggml-small.bin")));
    }
}
