using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Tests.Minutes;

/// <summary>
/// Everything in the note reaches the model: the person's notes and every transcript block, in
/// full, with every part of a long transcript processed and any loss reported (made-up data).
/// </summary>
public sealed class MinutesInputIntegrityTests
{
    /// <summary>Records every prompt; can pretend the answer hit the token budget.</summary>
    private sealed class Recorder(string answer, string finish = MinutesFinish.End) : ILocalTextGenerator
    {
        public List<string> Prompts { get; } = [];

        public string ModelName => "記録用モデル";

        public string LastFinish => finish;

        public Task<string> CompleteAsync(string system, string user, int maxTokens, CancellationToken cancellationToken, IProgress<int>? progress = null)
        {
            Prompts.Add(user);
            return Task.FromResult(answer);
        }
    }

    private const string Answer = "## 概要\n- 架空の会議";

    private static string Note(string notes, params string[] transcripts) =>
        notes + string.Concat(transcripts.Select(t => "\n\n" + TranscriptInsertion.Header + "\n" + t));

    private static (MinutesDraft Draft, Recorder Model) Run(string note, MinutesStyle style, string finish = MinutesFinish.End)
    {
        var model = new Recorder(Answer, finish);
        var draft = new MinutesGenerator(model, style).GenerateAsync(note, "定例", new DateTime(2026, 10, 16, 9, 0, 0), noteId: "note-1").Result;
        return (draft, model);
    }

    [Theory]
    [InlineData(MinutesStyle.FixedSections)]
    [InlineData(MinutesStyle.Topics)]
    public void NotesAndEveryTranscriptBlock_ReachTheModel(MinutesStyle style)
    {
        string note = Note("- 参加: 架空の三人\n- 予算の上限は事前資料で確認", "一つ目の録音です。リリース日は十一月一日です。", "二つ目の録音です。テスト計画は森さんが作ります。");
        var (draft, model) = Run(note, style);
        string all = string.Join("\n", model.Prompts);
        Assert.Contains("参加: 架空の三人", all);
        Assert.Contains("予算の上限は事前資料で確認", all);
        Assert.Contains("リリース日は十一月一日です", all);
        Assert.Contains("テスト計画は森さんが作ります", all);
        Assert.Empty(draft.Run!.Unprocessed);
        Assert.Equal(2, draft.Run.Input.TranscriptBlocks);
        Assert.True(draft.Run.Input.HasTranscript);
    }

    [Fact]
    public void LongTranscript_EveryPartIsProcessed_AndRangesCoverTheText()
    {
        string sentence = "会議では資料の確認を行いました。";
        string note = Note("- 自分のメモ", string.Concat(Enumerable.Repeat(sentence, 1200))); // ~19,000 characters
        var (draft, model) = Run(note, MinutesStyle.Topics);
        var run = draft.Run!;
        Assert.True(run.Ranges.Count >= 3);
        Assert.Equal(run.Ranges.Count, model.Prompts.Count);
        Assert.Empty(run.Unprocessed);
        Assert.All(run.Ranges, r => Assert.True(r.Start >= 0));
        Assert.All(model.Prompts, p => Assert.Contains("自分のメモ", p)); // notes go with every part
        Assert.Equal("notes-apart", run.InputMode);
    }

    [Fact]
    public void LongNotes_AreNeverCut_TheWholeNoteIsSummarized()
    {
        string longNotes = string.Concat(Enumerable.Repeat("手で書いたメモの行です。", 400)) + "最後の行は重要です。"; // > 3000 characters
        var (draft, model) = Run(Note(longNotes, "録音の内容です。"), MinutesStyle.Topics);
        string all = string.Join("\n", model.Prompts);
        Assert.Contains("最後の行は重要です", all);
        Assert.DoesNotContain("以下省略", all);
        Assert.Equal("whole-note(long-notes)", draft.Run!.InputMode);
        Assert.Empty(draft.Run.Unprocessed);
    }

    [Fact]
    public void NoteWithoutTranscript_IsDetected()
    {
        var snapshot = MinutesInputSnapshot.Of("n", "- 冒頭のメモ1\n- 冒頭のメモ2\n- 冒頭のメモ3\n- 冒頭のメモ4");
        Assert.False(snapshot.HasTranscript);
        Assert.Equal(0, snapshot.TranscriptCharacters);
        Assert.True(snapshot.HumanCharacters > 0);

        var empty = MinutesInputSnapshot.Of("n", "- メモ\n\n" + TranscriptInsertion.Header + "\n");
        Assert.False(empty.HasTranscript); // a header with nothing under it is no transcript
    }

    [Fact]
    public void AnswerCutOffByTheTokenBudget_IsReported()
    {
        var (draft, _) = Run(Note("- メモ", "録音の内容です。"), MinutesStyle.FixedSections, MinutesFinish.MaxTokens);
        Assert.True(draft.Run!.Truncated);
        Assert.Contains(draft.Warnings, w => w.Contains("長さの上限"));
    }

    [Fact]
    public void RecordParts_FindsWhatWasLeftOut()
    {
        var run = new MinutesRun { Input = MinutesInputSnapshot.Of("n", "x"), Model = "m", Style = MinutesStyle.Topics };
        run.RecordParts("前半の文。真ん中の文。後半の文。", ["前半の文。", "後半の文。"]);
        Assert.Equal([(5, 11)], run.Unprocessed);
    }

    [Fact]
    public void DiagnosticsLine_HasSizesAndHashes_NotTheText()
    {
        string secret = "架空株式会社の山田さんが予算を承認しました。";
        var (draft, _) = Run(Note("- メモ", secret), MinutesStyle.Topics);
        string line = draft.Run!.Format(new DateTime(2026, 10, 16, 9, 0, 0));
        Assert.Contains("note=note-1", line);
        Assert.Contains("blocks=1", line);
        Assert.Contains("unprocessed=none", line);
        Assert.DoesNotContain("山田", line);
        Assert.DoesNotContain("予算", line);
        Assert.DoesNotContain("メモ", line);
        Assert.Matches("input=[0-9a-f]{12}", line);
    }
}
