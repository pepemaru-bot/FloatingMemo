using System.Text;
using FloatingMemo.Services;
using FloatingMemo.Services.Notes;

namespace FloatingMemo.Tests.Notes;

public sealed class NoteWorkspaceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    public NoteWorkspaceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        foreach (var f in Directory.GetFiles(_root, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(f, FileAttributes.Normal);
        }

        Directory.Delete(_root, recursive: true);
    }

    private string Draft => Path.Combine(_root, "draft.md");

    private NoteWorkspace NewWorkspace()
    {
        var ws = new NoteWorkspace(new NoteLibrary(_root));
        ws.Restore();
        return ws;
    }

    private static string Read(string path) => File.ReadAllText(path, Encoding.UTF8);

    // ----- Migration from the single-memo version -----------------------------------

    [Fact]
    public void FirstRun_OpensExistingDraftInPlace_WithoutChangingIt()
    {
        byte[] original = Encoding.UTF8.GetBytes("# 既存のメモ\n- 大事な内容\n");
        File.WriteAllBytes(Draft, original);
        var before = File.GetLastWriteTimeUtc(Draft);

        var ws = NewWorkspace();
        ws.SaveAllForExit();

        Assert.Single(ws.Tabs);
        Assert.Equal("draft", ws.Active!.Title);
        Assert.Equal("# 既存のメモ\n- 大事な内容\n", ws.Active.Text);
        Assert.Equal(original, File.ReadAllBytes(Draft));
        Assert.Equal(before, File.GetLastWriteTimeUtc(Draft));
    }

    [Fact]
    public void FirstRun_WithoutDraft_StartsWithEmptyNoteAndCreatesNoFile()
    {
        var ws = NewWorkspace();
        ws.SaveAllForExit();

        Assert.Single(ws.Tabs);
        Assert.Equal(string.Empty, ws.Active!.Text);
        Assert.Empty(Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories));
    }

    // ----- New / save / close ----------------------------------------------------

    [Fact]
    public async Task NewNote_FileIsCreatedOnlyWhenItHasText()
    {
        var ws = NewWorkspace();
        var note = ws.NewNote("アイデア");

        Assert.False(File.Exists(note.Info.FilePath));
        ws.UpdateText(note.Id, "ひらめき", 4);
        await ws.SaveAsync(note);

        Assert.Equal(Path.Combine(_root, "notes", "アイデア.md"), note.Info.FilePath);
        Assert.Equal("ひらめき", Read(note.Info.FilePath));
    }

    [Fact]
    public async Task ClosingEmptyNewNote_LeavesNoFile_ClosingRealNote_KeepsFile()
    {
        File.WriteAllText(Draft, "draft");
        var ws = NewWorkspace();
        var empty = ws.NewNote("空");
        await ws.CloseAsync(empty.Id);
        Assert.False(File.Exists(empty.Info.FilePath));

        var real = ws.NewNote("残す");
        ws.UpdateText(real.Id, "消えない", 0);
        await ws.CloseAsync(real.Id);

        Assert.Equal("消えない", Read(real.Info.FilePath));
        Assert.DoesNotContain(ws.Tabs, t => t.Id == real.Id);
        Assert.Equal(real.Id, ws.ReopenLastClosed()!.Id);
    }

    [Fact]
    public async Task ClosingLastTab_LeavesNoTab_AndNeverCreatesOrDeletesNotes()
    {
        File.WriteAllText(Draft, "draft");
        var ws = NewWorkspace();
        int activeChanges = 0;
        ws.ActiveChanged += (_, _) => activeChanges++;
        var before = Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories).Order().ToList();

        await ws.CloseAsync(ws.Active!.Id);

        Assert.Empty(ws.Tabs);
        Assert.Null(ws.Active);
        Assert.Equal(1, activeChanges);
        Assert.Equal(before, Directory.GetFiles(_root, "*.md", SearchOption.AllDirectories).Order().ToList());
        Assert.Equal("draft", Read(Draft));
    }

    [Fact]
    public async Task AllTabsClosed_RestartsWithNoTab_AndNotesCanBeReopened()
    {
        File.WriteAllText(Draft, "draft");
        var first = NewWorkspace();
        string draftId = first.Active!.Id;
        await first.CloseAsync(draftId);

        var second = NewWorkspace();
        Assert.Empty(second.Tabs);
        Assert.Null(second.Active);
        Assert.Equal(draftId, second.Open(draftId)!.Id); // from the saved-notes list
        Assert.Single(second.Tabs);
        Assert.Equal("draft", second.Active!.Text);
        Assert.Equal(5, second.Active.Caret); // continue at the end
    }

    [Fact]
    public async Task ClosingDirtyTab_SavesFirst_AndOpeningAnOpenNote_DoesNotDuplicateTabs()
    {
        var ws = NewWorkspace();
        var note = ws.NewNote("会議");
        ws.UpdateText(note.Id, "未保存の変更", 6);
        var other = ws.NewNote("別");
        ws.UpdateText(other.Id, "x", 1);

        await ws.CloseAsync(note.Id);
        Assert.Equal("未保存の変更", Read(note.Info.FilePath));

        ws.Open(note.Id);
        ws.Open(note.Id);
        Assert.Single(ws.Tabs, t => t.Id == note.Id);
        Assert.Equal(note.Id, ws.Active!.Id);
    }

    [Fact]
    public void Catalog_SearchesTitleAndBody_SortsByUpdate_AndMarksOpenNotes()
    {
        var lib = new NoteLibrary(_root);
        Directory.CreateDirectory(lib.NotesDirectory);
        void Write(string title, string text, int minutesAgo)
        {
            string path = Path.Combine(lib.NotesDirectory, title + ".md");
            File.WriteAllText(path, text);
            File.SetLastWriteTime(path, DateTime.Now.AddMinutes(-minutesAgo));
        }

        Write("定例会議", "リリース日を決めた", 30);
        Write("買い物", "牛乳とＰＣケーブル", 10);
        Write("アイデア", "会議の進め方", 20);
        lib.Refresh();
        string open = lib.Notes.Single(n => n.Title == "買い物").Id;

        var all = NoteCatalog.Query(lib.Notes, [open], null, NoteSort.NewestFirst, NoteCatalog.ReadForSearch);
        Assert.Equal(["買い物", "アイデア", "定例会議"], all.Select(e => e.Note.Title));
        Assert.True(all[0].IsOpen);
        Assert.Equal(["定例会議", "アイデア", "買い物"],
            NoteCatalog.Query(lib.Notes, [], null, NoteSort.OldestFirst, NoteCatalog.ReadForSearch).Select(e => e.Note.Title));

        var meeting = NoteCatalog.Query(lib.Notes, [], "会議", NoteSort.NewestFirst, NoteCatalog.ReadForSearch);
        Assert.Equal(["アイデア", "定例会議"], meeting.Select(e => e.Note.Title));
        Assert.True(meeting[0].MatchedInBody);   // only in the text
        Assert.False(meeting[1].MatchedInBody);  // in the title

        Assert.Single(NoteCatalog.Query(lib.Notes, [], "pc", NoteSort.NewestFirst, NoteCatalog.ReadForSearch)); // width/case-insensitive
        Assert.Single(NoteCatalog.Query(lib.Notes, [], "牛乳　ケーブル", NoteSort.NewestFirst, NoteCatalog.ReadForSearch)); // every word
        Assert.Empty(NoteCatalog.Query(lib.Notes, [], "存在しない語", NoteSort.NewestFirst, NoteCatalog.ReadForSearch));
    }

    [Fact]
    public async Task SavesNeverMixNotes_EvenWhenTextChangesDuringSave()
    {
        var ws = NewWorkspace();
        var a = ws.NewNote("A");
        var b = ws.NewNote("B");
        ws.UpdateText(a.Id, "AAA", 3);
        ws.UpdateText(b.Id, "BBB", 3);

        // Start saving A, then keep editing A and switch tabs before the save finishes.
        var saveA = ws.SaveAsync(a);
        ws.UpdateText(a.Id, "AAA more", 8);
        ws.Activate(a.Id);
        var saveB = ws.SaveAsync(b);
        await Task.WhenAll(saveA, saveB);
        await ws.SaveAsync(a);

        Assert.Equal("AAA more", Read(a.Info.FilePath));
        Assert.Equal("BBB", Read(b.Info.FilePath));
    }

    [Fact]
    public async Task CloseWhenSaveFails_KeepsTabOpenAndText()
    {
        var ws = NewWorkspace();
        var note = ws.NewNote("ロック");
        ws.UpdateText(note.Id, "first", 5);
        await ws.SaveAsync(note);
        File.SetAttributes(note.Info.FilePath, FileAttributes.ReadOnly);
        ws.UpdateText(note.Id, "second", 6);

        var result = await ws.CloseAsync(note.Id);

        Assert.False(result!.Success);
        Assert.Contains(ws.Tabs, t => t.Id == note.Id);
        Assert.Equal("second", note.Text);
    }

    // ----- Switching / restore --------------------------------------------------

    [Fact]
    public async Task Restart_RestoresTabsActiveTabAndCarets()
    {
        File.WriteAllText(Draft, "draft text");
        var ws = NewWorkspace();
        var meeting = ws.NewNote("会議メモ");
        ws.UpdateText(meeting.Id, "議題1\n議題2", 4);
        await ws.SaveAsync(meeting);
        ws.UpdateText(ws.Tabs[0].Id, "draft text", 3);
        ws.Activate(ws.Tabs[0].Id);
        ws.SaveAllForExit();

        var again = NewWorkspace();

        Assert.Equal(["draft", "会議メモ"], again.Tabs.Select(t => t.Title));
        Assert.Equal("draft", again.Active!.Title);
        Assert.Equal(3, again.Tabs[0].Caret);
        Assert.Equal(4, again.Tabs[1].Caret);
        Assert.Equal(meeting.Id, again.Tabs[1].Id); // ids are stable across restarts
    }

    [Fact]
    public void ActivateRelative_Cycles()
    {
        var ws = NewWorkspace();
        var first = ws.Active!;
        var second = ws.NewNote("2");
        var third = ws.NewNote("3");

        ws.ActivateRelative(1);
        Assert.Same(first, ws.Active);
        ws.ActivateRelative(-1);
        Assert.Same(third, ws.Active);
        ws.ActivateRelative(-1);
        Assert.Same(second, ws.Active);
    }

    [Fact]
    public void DamagedSessionAndIndex_StillOpenNotes()
    {
        File.WriteAllText(Draft, "survives");
        Directory.CreateDirectory(Path.Combine(_root, ".floatingmemo"));
        File.WriteAllText(Path.Combine(_root, ".floatingmemo", "session.json"), "{ broken");
        File.WriteAllText(Path.Combine(_root, ".floatingmemo", "notes.json"), "[ broken");

        var ws = NewWorkspace();

        Assert.Equal("survives", ws.Active!.Text);
    }

    // ----- Rename ---------------------------------------------------------------

    [Fact]
    public async Task Rename_MovesFileWithPendingEditsAndKeepsId()
    {
        var ws = NewWorkspace();
        var note = ws.NewNote("旧名");
        ws.UpdateText(note.Id, "本文", 2);
        await ws.SaveAsync(note);
        string oldPath = note.Info.FilePath;
        ws.UpdateText(note.Id, "本文 追記", 5);

        string title = await ws.RenameAsync(note.Id, "新名");

        Assert.Equal("新名", title);
        Assert.False(File.Exists(oldPath));
        Assert.Equal("本文 追記", Read(note.Info.FilePath));
        ws.UpdateText(note.Id, "改名後も保存", 0);
        await ws.SaveAsync(note);
        Assert.Equal("改名後も保存", Read(Path.Combine(_root, "notes", "新名.md")));
    }

    [Fact]
    public async Task Rename_ToExistingName_NeverOverwritesOtherNote()
    {
        var ws = NewWorkspace();
        var a = ws.NewNote("会議");
        ws.UpdateText(a.Id, "A", 1);
        await ws.SaveAsync(a);
        var b = ws.NewNote("別");
        ws.UpdateText(b.Id, "B", 1);
        await ws.SaveAsync(b);

        string title = await ws.RenameAsync(b.Id, "会議");

        Assert.Equal("会議 2", title);
        Assert.Equal("A", Read(Path.Combine(_root, "notes", "会議.md")));
        Assert.Equal("B", Read(Path.Combine(_root, "notes", "会議 2.md")));
    }

    [Fact]
    public async Task Rename_UnsafeCharacters_AreReplaced()
    {
        var ws = NewWorkspace();
        var note = ws.NewNote();
        ws.UpdateText(note.Id, "x", 1);
        await ws.SaveAsync(note);

        string title = await ws.RenameAsync(note.Id, "a/b:c*?.  ");

        Assert.Equal("a_b_c__", title);
        Assert.True(File.Exists(Path.Combine(_root, "notes", "a_b_c__.md")));
    }

    // ----- External edits -------------------------------------------------------

    [Fact]
    public async Task ExternalChange_WithoutLocalEdits_IsReloaded()
    {
        var ws = NewWorkspace();
        var a = ws.NewNote("外部");
        ws.UpdateText(a.Id, "v1", 2);
        await ws.SaveAsync(a);
        ws.NewNote("other");
        File.WriteAllText(a.Info.FilePath, "edited in Obsidian");
        File.SetLastWriteTimeUtc(a.Info.FilePath, DateTime.UtcNow.AddSeconds(5));

        ws.Activate(a.Id);

        Assert.Equal("edited in Obsidian", a.Text);
    }

    [Fact]
    public async Task ExternalChange_WithLocalEdits_IsConflict_NothingOverwritten()
    {
        var ws = NewWorkspace();
        var a = ws.NewNote("競合");
        ws.UpdateText(a.Id, "v1", 2);
        await ws.SaveAsync(a);
        File.WriteAllText(a.Info.FilePath, "theirs");
        File.SetLastWriteTimeUtc(a.Info.FilePath, DateTime.UtcNow.AddSeconds(5));
        ws.UpdateText(a.Id, "mine", 4);

        var result = await ws.SaveAsync(a);

        Assert.True(result.IsConflict);
        Assert.True(a.HasConflict);
        Assert.Equal("theirs", Read(a.Info.FilePath));
        Assert.Equal("mine", a.Text);
    }

    [Fact]
    public async Task Conflict_KeepMine_SavesTheirsAsCopy()
    {
        var (ws, a) = await ConflictedNote();

        string copy = ws.ResolveConflictKeepMine(a)!;

        Assert.Equal("mine", Read(a.Info.FilePath));
        Assert.Equal("theirs", Read(copy));
        Assert.Contains("競合", Path.GetFileName(copy));
    }

    [Fact]
    public async Task Conflict_TakeTheirs_SavesMineAsCopy()
    {
        var (ws, a) = await ConflictedNote();

        string copy = ws.ResolveConflictTakeTheirs(a)!;

        Assert.Equal("theirs", a.Text);
        Assert.Equal("theirs", Read(a.Info.FilePath));
        Assert.Equal("mine", Read(copy));
    }

    private async Task<(NoteWorkspace, OpenNote)> ConflictedNote()
    {
        var ws = NewWorkspace();
        var a = ws.NewNote("競合");
        ws.UpdateText(a.Id, "v1", 2);
        await ws.SaveAsync(a);
        File.WriteAllText(a.Info.FilePath, "theirs");
        File.SetLastWriteTimeUtc(a.Info.FilePath, DateTime.UtcNow.AddSeconds(5));
        ws.UpdateText(a.Id, "mine", 4);
        await ws.SaveAsync(a);
        return (ws, a);
    }

    [Fact]
    public void UnreadableFile_OpensWithErrorAndIsNeverOverwritten()
    {
        byte[] invalid = [0x23, 0x20, 0xFF, 0xFE];
        File.WriteAllBytes(Draft, invalid);

        var ws = NewWorkspace();
        ws.UpdateText(ws.Active!.Id, "would destroy", 0);
        var failures = ws.SaveAllForExit();

        Assert.NotNull(ws.Active.LoadError);
        Assert.NotEmpty(failures);
        Assert.Equal(invalid, File.ReadAllBytes(Draft));
    }

    // ----- Transcript routing ---------------------------------------------------

    [Fact]
    public async Task Transcript_ForActiveTab_IsLeftToTheEditor()
    {
        var ws = NewWorkspace();

        var (delivery, tab) = await ws.DeliverTranscript(ws.Active!.Id, "文字起こし");

        Assert.Equal(TranscriptDelivery.InsertIntoActiveEditor, delivery);
        Assert.Same(ws.Active, tab);
    }

    [Fact]
    public async Task Transcript_RefusedByTheEditor_IsAppendedToTheActiveNote_AndSaved()
    {
        var ws = NewWorkspace();
        ws.UpdateText(ws.Active!.Id, "- 冒頭のメモ", 0);

        var (delivery, tab) = await ws.DeliverTranscript(ws.Active.Id, "届いた文字起こし", editorRefused: true);

        Assert.Equal(TranscriptDelivery.InsertedIntoOpenTab, delivery);
        Assert.Same(ws.Active, tab);
        Assert.Equal("- 冒頭のメモ\n\n以下、AIによる文字起こし\n届いた文字起こし", tab!.Text);
        Assert.False(tab.IsDirty); // saved right away
    }

    [Fact]
    public async Task Transcript_AfterSwitchingTabs_GoesIntoTheRecordedNote()
    {
        var ws = NewWorkspace();
        var recorded = ws.NewNote("録音先");
        ws.UpdateText(recorded.Id, "前\n後", 2);
        await ws.SaveAsync(recorded);
        var other = ws.NewNote("別のノート");
        ws.UpdateText(other.Id, "触らない", 4);

        var (delivery, _) = await ws.DeliverTranscript(recorded.Id, "文字起こし");

        Assert.Equal(TranscriptDelivery.InsertedIntoOpenTab, delivery);
        // Appended at the end under the header; the caret (after "前") does not move.
        Assert.Equal("前\n後\n\n以下、AIによる文字起こし\n文字起こし", recorded.Text);
        Assert.Equal(recorded.Text, Read(recorded.Info.FilePath));
        Assert.Equal(2, recorded.Caret);
        Assert.Equal("触らない", other.Text);
        Assert.Same(other, ws.Active);
    }

    [Fact]
    public async Task Transcript_ForClosedNote_IsAppendedToItsFile()
    {
        var ws = NewWorkspace();
        var target = ws.NewNote("閉じた");
        ws.UpdateText(target.Id, "本文", 2);
        await ws.CloseAsync(target.Id);

        var (delivery, _) = await ws.DeliverTranscript(target.Id, "文字起こし");

        Assert.Equal(TranscriptDelivery.AppendedToClosedNote, delivery);
        Assert.Equal("本文\n\n以下、AIによる文字起こし\n文字起こし", Read(target.Info.FilePath));
    }

    [Fact]
    public async Task Transcript_ForDeletedNote_IsSavedAsNewNote()
    {
        var ws = NewWorkspace();
        var target = ws.NewNote("消える");
        ws.UpdateText(target.Id, "x", 1);
        await ws.CloseAsync(target.Id);
        File.Delete(target.Info.FilePath);

        var (delivery, rescue) = await ws.DeliverTranscript(target.Id, "失わない");

        Assert.Equal(TranscriptDelivery.SavedAsNewNote, delivery);
        Assert.Equal("以下、AIによる文字起こし\n失わない", Read(rescue!.Info.FilePath));
        Assert.Same(rescue, ws.Active);
    }
}

public sealed class NoteNamesTests
{
    [Theory]
    [InlineData("会議メモ", "会議メモ")]
    [InlineData("  前後の空白  ", "前後の空白")]
    [InlineData("a/b\\c:d*e?f\"g<h>i|j", "a_b_c_d_e_f_g_h_i_j")]
    [InlineData("[[link]]#^", "__link____")]
    [InlineData("末尾のドット...", "末尾のドット")]
    [InlineData("", "無題")]
    [InlineData("///", "無題")]
    [InlineData("CON", "CON_")]
    [InlineData("nul", "nul_")]
    [InlineData("tab\there", "tab_here")]
    public void Sanitize(string input, string expected)
    {
        Assert.Equal(expected, NoteNames.Sanitize(input));
    }

    [Fact]
    public void Sanitize_LimitsLength()
    {
        Assert.Equal(NoteNames.MaxTitleLength, NoteNames.Sanitize(new string('あ', 300)).Length);
    }
}

public sealed class NoteLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    public NoteLibraryTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Ids_AreStableAcrossRefreshes_AndNewFilesAreDiscovered()
    {
        File.WriteAllText(Path.Combine(_root, "draft.md"), "d");
        var lib = new NoteLibrary(_root);
        lib.Refresh();
        string draftId = lib.Notes.Single().Id;

        Directory.CreateDirectory(Path.Combine(_root, "notes", "sub"));
        File.WriteAllText(Path.Combine(_root, "notes", "sub", "外部で作成.md"), "x");
        var again = new NoteLibrary(_root);
        again.Refresh();

        Assert.Equal(draftId, again.FindByPath(Path.Combine(_root, "draft.md"))!.Id);
        Assert.Equal(2, again.Notes.Count);
        Assert.Contains(again.Notes, n => n.Title == "外部で作成");
    }

    [Fact]
    public void MetadataFolder_IsNotTreatedAsNotes()
    {
        Directory.CreateDirectory(Path.Combine(_root, ".floatingmemo"));
        File.WriteAllText(Path.Combine(_root, ".floatingmemo", "x.md"), "meta");
        var lib = new NoteLibrary(_root);

        lib.Refresh();

        Assert.Empty(lib.Notes);
    }
}
