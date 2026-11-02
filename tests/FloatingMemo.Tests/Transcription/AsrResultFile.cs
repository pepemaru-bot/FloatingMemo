using System.Text.Json.Nodes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Transcription;

/// <summary>
/// One speech recognizer's result in the comparison's common shape (written by AsrEvalExport for
/// Whisper and by tests/asr/eval/run_nemotron.py for Nemotron): model, setting, segments with
/// start/end (seconds, null when unknown), speaker (null when the recognizer gives none) and text.
/// </summary>
public sealed record AsrResultFile(string Model, string Setting, IReadOnlyList<AsrResultFile.Segment> Segments)
{
    public sealed record Segment(double? Start, double? End, string? Speaker, string Text);

    public static AsrResultFile Parse(string json)
    {
        var root = JsonNode.Parse(json)!;
        var segments = root["segments"]!.AsArray().Select(s => new Segment(
            s!["start"]?.GetValue<double>(), s["end"]?.GetValue<double>(),
            s["speaker"] is { } spk ? spk.ToJsonString().Trim('"') : null,
            s["text"]?.GetValue<string>() ?? string.Empty)).ToList();
        return new AsrResultFile(root["model"]!.GetValue<string>(), root["setting"]!.GetValue<string>(), segments);
    }

    /// <summary>The transcript as the app inserts it into a note (Whisper's JoinSegments), whatever the recognizer.</summary>
    public string Transcript => LocalWhisperTranscriptionService.JoinSegments(
        Segments.Select(s => s.Text.Trim()).Where(t => t.Length > 0));

    /// <summary>Times go forward and every segment ends after it starts (where times are given).</summary>
    public bool TimesAreConsistent =>
        Segments.All(s => s.Start is null || s.End is null || s.End >= s.Start)
        && Segments.Where(s => s.Start is not null).Zip(Segments.Where(s => s.Start is not null).Skip(1)).All(p => p.Second.Start >= p.First.Start - 0.05);
}

public sealed class AsrResultFileTests
{
    [Fact]
    public void Transcript_IsJoinedAsTheAppJoinsWhisperSegments()
    {
        var file = AsrResultFile.Parse("""
            {"model":"m","setting":"s","segments":[
              {"start":0.5,"end":2.0,"speaker":null,"text":" それでは始めます。"},
              {"start":2.0,"end":2.4,"speaker":null,"text":""},
              {"start":2.5,"end":3.0,"speaker":null,"text":"はい。"}]}
            """);
        Assert.Equal("それでは始めます。はい。", file.Transcript);
        Assert.True(file.TimesAreConsistent);
    }

    [Fact]
    public void SpeakersAndMissingTimes_AreKept()
    {
        var file = AsrResultFile.Parse("""
            {"model":"m","setting":"s","segments":[
              {"start":null,"end":null,"speaker":null,"text":"時刻のない全文。"},
              {"start":1.0,"end":2.0,"speaker":1,"text":"話者つき。"}]}
            """);
        Assert.Null(file.Segments[0].Start);
        Assert.Equal("1", file.Segments[1].Speaker);
        Assert.True(file.TimesAreConsistent);
    }

    [Fact]
    public void BackwardTimes_AreReported()
    {
        var file = AsrResultFile.Parse("""
            {"model":"m","setting":"s","segments":[
              {"start":5.0,"end":6.0,"speaker":null,"text":"後"},
              {"start":1.0,"end":2.0,"speaker":null,"text":"前"}]}
            """);
        Assert.False(file.TimesAreConsistent);
    }

    /// <summary>FLOATINGMEMO_ASR_EVAL_DIR only: every exported result reads, has text and consistent times.</summary>
    [Fact]
    public void ExportedResults_AreWellFormed()
    {
        if (Environment.GetEnvironmentVariable("FLOATINGMEMO_ASR_EVAL_DIR") is not { Length: > 0 } root)
        {
            return;
        }

        foreach (string file in Directory.GetFiles(root, "*.json", SearchOption.AllDirectories).Where(f => f.Contains($"{Path.DirectorySeparatorChar}asr{Path.DirectorySeparatorChar}") && !f.EndsWith(".raw.json", StringComparison.Ordinal)))
        {
            var result = AsrResultFile.Parse(File.ReadAllText(file));
            Assert.True(result.Transcript.Length > 0, file);
            Assert.True(result.TimesAreConsistent, file);
        }
    }
}
