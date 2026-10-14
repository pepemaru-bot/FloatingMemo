using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using FloatingMemo.Services.Transcription;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Transcription;

/// <summary>
/// Skips the test when no Whisper model is installed (run scripts\setup-model.ps1 first).
/// Override the location with the FLOATINGMEMO_MODEL environment variable.
/// </summary>
public sealed class RequiresWhisperModelFactAttribute : FactAttribute
{
    public static string ModelPath =>
        Environment.GetEnvironmentVariable("FLOATINGMEMO_MODEL") is { Length: > 0 } custom
            ? custom
            : LocalWhisperTranscriptionService.DefaultModelPath;

    public RequiresWhisperModelFactAttribute()
    {
        if (!File.Exists(ModelPath))
        {
            Skip = $"Whisper model not found at {ModelPath}";
        }
        else if (JapaneseVoice is null)
        {
            Skip = "No Japanese SAPI voice installed";
        }
    }

    /// <summary>Name of an installed Japanese text-to-speech voice, if any.</summary>
    public static string? JapaneseVoice
    {
        get
        {
            using var synth = new SpeechSynthesizer();
            return synth.GetInstalledVoices()
                .FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name == "ja-JP")?.VoiceInfo.Name;
        }
    }
}

public sealed class TranscriptionTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    public TranscriptionTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>
    /// Synthesizes Japanese speech with the built-in Windows voice (offline SAPI) as a
    /// 16 kHz mono 16-bit WAV — no microphone or recorded human speech involved.
    /// </summary>
    private string SynthesizeJapanese(string sentence)
    {
        string path = Path.Combine(_dir, "speech.wav");
        using var synth = new SpeechSynthesizer();
        synth.SelectVoice(RequiresWhisperModelFactAttribute.JapaneseVoice!);
        synth.SetOutputToWaveFile(path, new SpeechAudioFormatInfo(16_000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
        synth.Speak(sentence);
        synth.SetOutputToNull();
        return path;
    }

    [RequiresWhisperModelFact]
    public async Task Transcribes_SynthesizedJapaneseSpeech_Offline()
    {
        string wav = SynthesizeJapanese("今日の会議では、来週までに資料を準備することになりました。");
        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja");
        Assert.False(service.IsModelLoaded, "model must load lazily, not in the constructor");

        var sw = Stopwatch.StartNew();
        await using var audio = File.OpenRead(wav);
        var result = await service.TranscribeAsync(audio);
        _output.WriteLine($"first run {sw.ElapsedMilliseconds} ms: {result.Text}");

        Assert.True(service.IsModelLoaded);
        Assert.False(result.IsEmpty);
        Assert.NotEmpty(result.Segments);
        Assert.Contains("会議", result.Text);
        Assert.Contains("資料", result.Text);
        Assert.All(result.Segments, s => Assert.True(s.End >= s.Start));
    }

    [RequiresWhisperModelFact]
    public async Task Progress_IsMeasured_RisesMonotonically_AndEndsAt100()
    {
        // Long enough (about a minute) to be split into several stretches.
        string wav = SynthesizeJapanese(string.Concat(Enumerable.Repeat("今日の会議では、来週までに資料を準備することになりました。次回は見積もりの結果を確認して、担当者を決めます。", 6)));
        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja");
        var reports = new List<double>();
        await using var audio = File.OpenRead(wav);
        await service.TranscribeAsync(audio, progress: new Services.Minutes.SynchronousProgress<double>(reports.Add));

        _output.WriteLine(string.Join(", ", reports.Select(r => r.ToString("0.00"))));
        Assert.Equal(0, reports[0]);
        Assert.Equal(1, reports[^1]);
        Assert.True(reports.Count >= 3, "intermediate progress expected");
        Assert.All(reports.Zip(reports.Skip(1)), pair => Assert.True(pair.Second >= pair.First));
    }

    [RequiresWhisperModelFact]
    public async Task SecondTranscription_ReusesLoadedModel()
    {
        string wav = SynthesizeJapanese("メモを保存します。");
        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja");
        await using (var first = File.OpenRead(wav))
        {
            await service.TranscribeAsync(first);
        }

        var sw = Stopwatch.StartNew();
        await using var second = File.OpenRead(wav);
        var result = await service.TranscribeAsync(second);
        _output.WriteLine($"second run {sw.ElapsedMilliseconds} ms: {result.Text}");

        Assert.Contains("メモ", result.Text);
    }

    [RequiresWhisperModelFact]
    public async Task CancelledToken_Throws()
    {
        string wav = SynthesizeJapanese("キャンセルのテストです。");
        using var service = new LocalWhisperTranscriptionService(RequiresWhisperModelFactAttribute.ModelPath, "ja");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await using var audio = File.OpenRead(wav);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TranscribeAsync(audio, cts.Token));
    }

    [Fact]
    public void DefaultModelPath_SearchesWhisperFolders_AppFolderFirst()
    {
        string app = Path.Combine(_dir, "app", "models");
        string user = Path.Combine(_dir, "user", "models");

        // Nothing anywhere, no models folder next to the app: the user is told to use their profile folder.
        Assert.Equal(Path.Combine(user, "whisper", "ggml-base.bin"), LocalWhisperTranscriptionService.ResolveDefaultModelPath(app, user));

        // The portable ZIP ships an (empty) models folder: that is where the model should go.
        Directory.CreateDirectory(app);
        Assert.Equal(Path.Combine(app, "whisper", "ggml-base.bin"), LocalWhisperTranscriptionService.ResolveDefaultModelPath(app, user));

        // The older flat layout in the user's folder is still found.
        Directory.CreateDirectory(user);
        File.WriteAllText(Path.Combine(user, "ggml-base.bin"), "x");
        Assert.Equal(Path.Combine(user, "ggml-base.bin"), LocalWhisperTranscriptionService.ResolveDefaultModelPath(app, user));

        // models\whisper next to the app wins.
        Directory.CreateDirectory(Path.Combine(app, "whisper"));
        File.WriteAllText(Path.Combine(app, "whisper", "ggml-base.bin"), "x");
        Assert.Equal(Path.Combine(app, "whisper", "ggml-base.bin"), LocalWhisperTranscriptionService.ResolveDefaultModelPath(app, user));
    }

    [Fact]
    public void DefaultModelPath_UsesSmallModel_WhenBaseIsAbsent()
    {
        string app = Path.Combine(_dir, "app", "models");
        string user = Path.Combine(_dir, "user", "models");
        Directory.CreateDirectory(Path.Combine(user, "whisper"));
        File.WriteAllText(Path.Combine(user, "whisper", "ggml-small.bin"), "x");
        Assert.Equal(Path.Combine(user, "whisper", "ggml-small.bin"), LocalWhisperTranscriptionService.ResolveDefaultModelPath(app, user));
    }

    [Fact]
    public void ModelLocations_SearchOrder()
    {
        var dirs = FloatingMemo.Services.ModelLocations.SearchDirectories("qwen", @"A:\app\models", @"U:\user\models");
        Assert.Equal([@"A:\app\models\qwen", @"A:\app\models", @"U:\user\models\qwen", @"U:\user\models"], dirs.Select(d => d.Directory));
        Assert.Equal([true, true, false, false], dirs.Select(d => d.InAppFolder));
    }

    [Fact]
    public async Task MissingModel_ThrowsModelMissingWithPath()
    {
        string missing = Path.Combine(_dir, "no-such-model.bin");
        using var service = new LocalWhisperTranscriptionService(missing);
        await using var audio = new MemoryStream();

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => service.TranscribeAsync(audio));

        Assert.True(ex.IsModelMissing);
        Assert.Contains(missing, ex.Message);
        Assert.Contains("README", ex.Message); // points to the manual download steps
        Assert.False(service.IsModelLoaded);
    }

    [Fact]
    public async Task InvalidModelFile_ThrowsTranscriptionException()
    {
        string bogus = Path.Combine(_dir, "bogus.bin");
        File.WriteAllText(bogus, "not a model");
        using var service = new LocalWhisperTranscriptionService(bogus);
        await using var audio = new MemoryStream();

        var ex = await Assert.ThrowsAsync<TranscriptionException>(() => service.TranscribeAsync(audio));

        Assert.False(ex.IsModelMissing);
        Assert.StartsWith("文字起こしに失敗しました", ex.Message);
    }

    [Theory]
    [InlineData("[BLANK_AUDIO]", "")]
    [InlineData(" (音楽) ", "")]
    [InlineData("（拍手）", "")]
    [InlineData("♪♪", "")]
    [InlineData("  こんにちは。 ", "こんにちは。")]
    [InlineData("[注] は残す", "[注] は残す")]
    public void CleanSegment_RemovesNonSpeechMarkersOnly(string input, string expected)
    {
        Assert.Equal(expected, LocalWhisperTranscriptionService.CleanSegment(input));
    }

    [Fact]
    public void JoinSegments_JapaneseWithoutSpaces_LatinWithSpace()
    {
        Assert.Equal("今日は晴れ。明日は雨。", LocalWhisperTranscriptionService.JoinSegments(["今日は晴れ。", "明日は雨。"]));
        Assert.Equal("Hello world. Bye.", LocalWhisperTranscriptionService.JoinSegments(["Hello world.", "Bye."]));
        Assert.Equal("会議はZoomで", LocalWhisperTranscriptionService.JoinSegments(["会議は", "Zoomで"]));
    }
}

public sealed class TranscriptInsertionTests
{
    [Theory]
    [InlineData("", "以下、AIによる文字起こし\n文字起こし")]                 // empty note: no blank line first
    [InlineData("メモ", "\n\n以下、AIによる文字起こし\n文字起こし")]         // one empty line between
    [InlineData("メモ\n", "\n以下、AIによる文字起こし\n文字起こし")]
    [InlineData("メモ\n\n", "以下、AIによる文字起こし\n文字起こし")]
    [InlineData("メモ\n\n\n", "以下、AIによる文字起こし\n文字起こし")]      // never removes the user's lines
    [InlineData("メモ\r\n", "\n以下、AIによる文字起こし\n文字起こし")]       // CRLF counts as one line break
    public void Append_AddsHeaderBlockAtTheEnd_WithOneEmptyLineBefore(string text, string expected)
    {
        Assert.Equal(expected, TranscriptInsertion.Append(text, "  文字起こし ", "\n"));
    }

    [Fact]
    public void Block_AlwaysStartsWithTheHeader()
    {
        Assert.StartsWith("以下、AIによる文字起こし", TranscriptInsertion.Block("x", "\r"));
        Assert.Equal("以下、AIによる文字起こし\rx", TranscriptInsertion.Block(" x ", "\r"));
    }
}
