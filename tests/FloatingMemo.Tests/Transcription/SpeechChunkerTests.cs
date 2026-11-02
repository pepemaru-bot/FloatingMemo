using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Transcription;

public sealed class SpeechChunkerTests
{
    private const int Rate = 16_000;

    private static SpeechRegion R(double from, double to) => new((int)(from * Rate), (int)(to * Rate));

    private static readonly SpeechChunkOptions App = new() { PadSeconds = 0.2, JoinGapSeconds = 0.5, MaxChunkSeconds = 20, ShortSeconds = 0.9 };

    private static IReadOnlyList<SpeechChunk> Plan(SpeechChunkOptions options, params SpeechRegion[] regions) =>
        SpeechChunker.Plan(regions, (long)(600 * Rate), Rate, (from, to) => (from + to) / 2, options);

    [Fact]
    public void PausesInsideASentence_AreJoined_LongerOnesStayApart()
    {
        var chunks = Plan(App, R(1, 3), R(3.3, 5), R(6, 8));
        Assert.Equal(2, chunks.Count);
        Assert.Equal(R(1, 5).StartSample - (int)(0.2 * Rate), chunks[0].StartSample);
    }

    [Fact]
    public void AShortReply_IsNeverJoined_SoItIsNotDroppedAtTheEndOfAPiece()
    {
        // "…お願いします。" 0.3 s "はい。" 0.3 s "では次に…"
        var chunks = Plan(App, R(1, 4), R(4.3, 4.8), R(5.1, 8));
        Assert.Equal(3, chunks.Count);
        Assert.InRange(chunks[1].Length / (double)Rate, 0.5, 0.9);
    }

    [Fact]
    public void Padding_NeverReachesIntoTheNeighborsSpeech()
    {
        var chunks = Plan(App with { JoinGapSeconds = 0 }, R(1, 2), R(2.1, 3));
        Assert.True(chunks[0].EndSample <= R(2.1, 3).StartSample);
        Assert.True(chunks[1].StartSample >= R(1, 2).EndSample);
        Assert.Equal(chunks[0].EndSample, chunks[1].StartSample);
    }

    [Fact]
    public void LongSpeech_IsCutAtItsQuietestMoment_IntoPiecesThatFit()
    {
        var chunks = SpeechChunker.Plan([R(0, 50)], 60 * Rate, Rate, (from, to) => to - Rate, App);
        Assert.All(chunks, c => Assert.True(c.Length <= 20 * Rate + 1));
        Assert.True(chunks[0].SplitAfter);
        Assert.False(chunks[^1].SplitAfter);
        Assert.Equal(chunks[0].EndSample, chunks[1].StartSample); // nothing lost between pieces
        Assert.Equal(0, chunks[0].StartSample);
    }

    [Fact]
    public void Join_EndsPiecesAtPausesAsSentences_ButNotCutsOrCommas()
    {
        var pieces = new List<(SpeechChunk, string)>
        {
            (new SpeechChunk(0, Rate, false), "会議を始めます"),
            (new SpeechChunk(Rate, 2 * Rate, false), "はい。"),
            (new SpeechChunk(2 * Rate, 3 * Rate, true), "全部で二十四ページで"),
            (new SpeechChunk(3 * Rate, 4 * Rate, false), "では、"),
            (new SpeechChunk(4 * Rate, 5 * Rate, false), "  "),
            (new SpeechChunk(5 * Rate, 6 * Rate, false), "どうですか？"),
        };
        var segments = SpeechChunker.Join(pieces, Rate);
        Assert.Equal(["会議を始めます。", "はい。", "全部で二十四ページで", "では、", "どうですか？"], segments.Select(s => s.Text));
        Assert.Equal(TimeSpan.FromSeconds(5), segments[^1].Start);
        Assert.Equal("会議を始めます", SpeechChunker.Join(pieces, Rate, addSentenceEnds: false)[0].Text);
    }

    [Fact]
    public void HaasKeywords_UseKanaReadings_AndSkipWhatTheFormatCannotHold()
    {
        string? keywords = TermDictionary.HaasKeywords(
        [
            new TermEntry { Term = "翠嶺社", Reading = "すいれいしゃ" },
            new TermEntry { Term = "PP加工", Reading = "ピーピーかこう" },
            new TermEntry { Term = "稟議書", Reading = "りんぎ書" },     // not kana: the written form only
            new TermEntry { Term = "A=B" },                                   // "=" separates the fields
            new TermEntry { Term = "白鷺ホール" },
        ]);
        Assert.Equal("翠嶺社=すいれいしゃ\nPP加工=ピーピーかこう\n稟議書\n白鷺ホール", keywords);
        Assert.Null(TermDictionary.HaasKeywords([]));
    }
}
