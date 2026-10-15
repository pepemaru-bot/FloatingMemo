using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo.Services.Notes;

/// <summary>A note open in a tab: its latest text and caret, and the store that saves it.</summary>
public sealed class OpenNote
{
    internal OpenNote(NoteInfo info, NoteStorageService store, string text)
    {
        Info = info;
        Store = store;
        Text = text;
    }

    public NoteInfo Info { get; }

    public string Id => Info.Id;

    public string Title => Info.Title;

    internal NoteStorageService Store { get; }

    /// <summary>Latest editor text (LF line breaks).</summary>
    public string Text { get; internal set; }

    /// <summary>Caret offset in <see cref="Text"/>, restored when the tab is shown again.</summary>
    public int Caret { get; set; }

    /// <summary>Changed since the last successful save.</summary>
    public bool IsDirty { get; internal set; }

    /// <summary>Set when the file was changed by another program while this note had unsaved edits.</summary>
    public bool HasConflict { get; internal set; }

    /// <summary>The file existed but could not be read; saving is blocked to protect it.</summary>
    public string? LoadError { get; internal set; }
}

/// <summary>Where a transcript ended up (see <see cref="NoteWorkspace.DeliverTranscript"/>).</summary>
public enum TranscriptDelivery
{
    /// <summary>The target is the active tab: the editor must insert it at its caret.</summary>
    InsertIntoActiveEditor,

    /// <summary>The target is open in another tab: inserted at that tab's remembered caret.</summary>
    InsertedIntoOpenTab,

    /// <summary>The target tab was closed: appended to the end of its file and saved.</summary>
    AppendedToClosedNote,

    /// <summary>The target note no longer exists: saved as a new note and opened.</summary>
    SavedAsNewNote,
}

internal sealed class SessionState
{
    public List<string> Open { get; set; } = [];

    public string? Active { get; set; }

    public Dictionary<string, int> Carets { get; set; } = [];

    public List<string> RecentlyClosed { get; set; } = [];

    /// <summary>The user closed every tab: start with no tab instead of creating one.</summary>
    public bool AllClosed { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(SessionState))]
internal sealed partial class SessionJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The open tabs and everything that happens to them: create, open, switch, close, rename,
/// save, restore after restart, and routing a transcript to the note it was recorded for.
/// UI-independent so it can be unit tested; the page only renders it and feeds editor changes.
/// </summary>
/// <remarks>
/// Safety rules:
/// - Closing a tab never deletes a file. A new note that was never given any text is simply
///   forgotten (no empty file is ever created).
/// - Text is always saved per note: a save captures (note, text) together, so switching tabs
///   while a save runs cannot write one note's text into another note's file.
/// - The original draft.md is opened in place; it is not moved, renamed or rewritten unless
///   the user edits or renames it.
/// </remarks>
public sealed class NoteWorkspace
{
    private const int MaxRecentlyClosed = 20;

    private readonly NoteLibrary _library;
    private readonly List<OpenNote> _tabs = [];
    private readonly List<string> _recentlyClosed = [];

    public NoteWorkspace(NoteLibrary library)
    {
        _library = library;
    }

    public NoteLibrary Library => _library;

    public IReadOnlyList<OpenNote> Tabs => _tabs;

    /// <summary>The note in the editor, or null when every tab has been closed.</summary>
    public OpenNote? Active { get; private set; }

    /// <summary>Tabs were added, removed, renamed or reordered.</summary>
    public event EventHandler? TabsChanged;

    /// <summary>A different tab became active (the editor must show <see cref="Active"/>).</summary>
    public event EventHandler? ActiveChanged;

    private string SessionPath => Path.Combine(_library.MetaDirectory, "session.json");

    /// <summary>
    /// Re-opens the tabs of the last session. On first run (or if nothing can be restored) the
    /// existing draft.md is opened as the first tab; if there is none, a new empty note.
    /// </summary>
    public void Restore()
    {
        _library.Refresh();
        var session = ReadSession();
        foreach (string id in session.Open)
        {
            if (_library.Find(id) is { Exists: true } info && OpenInternal(info) is { } tab)
            {
                tab.Caret = Math.Clamp(session.Carets.GetValueOrDefault(id), 0, tab.Text.Length);
            }
        }

        _recentlyClosed.AddRange(session.RecentlyClosed.Where(id => _library.Find(id) is not null));

        if (_tabs.Count == 0 && !session.AllClosed)
        {
            // Migration from the single-memo version: draft.md becomes the first tab, untouched.
            var draft = _library.FindByPath(Path.Combine(_library.RootDirectory, "draft.md"));
            var first = draft is not null ? OpenInternal(draft) : null;
            if (first is not null)
            {
                first.Caret = first.Text.Length;
            }
            else
            {
                OpenInternal(_library.CreateNote());
            }
        }

        // No tab at all only if the user closed them all last time (notes are not touched).
        Active = _tabs.FirstOrDefault(t => t.Id == session.Active) ?? _tabs.FirstOrDefault();
        TabsChanged?.Invoke(this, EventArgs.Empty);
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Creates a new (empty, not yet saved) note in a new tab and activates it.</summary>
    public OpenNote NewNote(string? title = null)
    {
        var tab = OpenInternal(_library.CreateNote(title))!;
        Activate(tab.Id);
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    /// <summary>Creates a new note with the given text, saves it and shows it in a new tab.</summary>
    public async Task<(OpenNote Tab, SaveResult Result)> CreateNoteAsync(string title, string text)
    {
        var tab = NewNote(title);
        tab.Text = text;
        tab.Caret = 0;
        tab.IsDirty = true;
        var result = await SaveAsync(tab);
        ActiveChanged?.Invoke(this, EventArgs.Empty);
        return (tab, result);
    }

    /// <summary>Opens a note (or switches to its tab if already open).</summary>
    public OpenNote? Open(string id)
    {
        if (_tabs.FirstOrDefault(t => t.Id == id) is { } existing)
        {
            Activate(existing.Id);
            return existing;
        }

        if (_library.Find(id) is not { } info || OpenInternal(info) is not { } tab)
        {
            return null;
        }

        // Re-opened from the list: continue writing at the end, like the first draft.md tab.
        tab.Caret = tab.Text.Length;

        _recentlyClosed.Remove(id);
        Activate(tab.Id);
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return tab;
    }

    public void Activate(string id)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == id);
        if (tab is null || ReferenceEquals(tab, Active))
        {
            return;
        }

        Active = tab;
        ReloadIfChangedExternally(tab);
        SaveSession();
        ActiveChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: cycles through tabs.</summary>
    public void ActivateRelative(int step)
    {
        if (_tabs.Count < 2 || Active is null)
        {
            return;
        }

        int index = (_tabs.IndexOf(Active) + step + _tabs.Count) % _tabs.Count;
        Activate(_tabs[index].Id);
    }

    /// <summary>The editor's text/caret for a note changed (called on every edit).</summary>
    public void UpdateText(string id, string text, int caret)
    {
        if (_tabs.FirstOrDefault(t => t.Id == id) is not { } tab)
        {
            return;
        }

        string normalized = NoteStorageService.NormalizeNewlines(text);
        if (normalized != tab.Text)
        {
            tab.Text = normalized;
            tab.IsDirty = true;
        }

        tab.Caret = Math.Clamp(caret, 0, tab.Text.Length);
    }

    /// <summary>Saves one note in the background. Conflicts and errors are reported, not thrown.</summary>
    public async Task<SaveResult> SaveAsync(OpenNote tab)
    {
        if (tab.LoadError is not null)
        {
            return SaveResult.Failed(tab, new IOException(tab.LoadError));
        }

        // Capture the text now: later edits (or a tab switch) cannot change what this save writes.
        string text = tab.Text;
        bool hadFile = tab.Info.Exists;
        try
        {
            await tab.Store.SaveAsync(text).ConfigureAwait(true);
            if (tab.Text == text)
            {
                tab.IsDirty = false;
            }

            tab.HasConflict = false;
            if (!hadFile && tab.Info.Exists)
            {
                _library.MarkSaved(tab.Id);
            }

            return SaveResult.Ok(tab);
        }
        catch (NoteConflictException ex)
        {
            tab.HasConflict = true;
            return SaveResult.Failed(tab, ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return SaveResult.Failed(tab, ex);
        }
    }

    /// <summary>Saves every open note synchronously (app exit). Returns the failures.</summary>
    public List<SaveResult> SaveAllForExit()
    {
        var failures = new List<SaveResult>();
        foreach (var tab in _tabs.Where(t => t.IsDirty || !t.Info.Exists))
        {
            try
            {
                bool hadFile = tab.Info.Exists;
                tab.Store.Save(tab.Text);
                tab.IsDirty = false;
                if (!hadFile && tab.Info.Exists)
                {
                    _library.MarkSaved(tab.Id);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                tab.HasConflict = ex is NoteConflictException;
                failures.Add(SaveResult.Failed(tab, ex));
            }
        }

        SaveSession();
        return failures;
    }

    /// <summary>
    /// Closes a tab after saving it. The file is never deleted. If saving fails the tab stays
    /// open (returns the failure) so no text is lost.
    /// </summary>
    public async Task<SaveResult?> CloseAsync(string id)
    {
        if (_tabs.FirstOrDefault(t => t.Id == id) is not { } tab)
        {
            return null;
        }

        if (tab.IsDirty || (!tab.Info.Exists && tab.Text.Length > 0))
        {
            var result = await SaveAsync(tab);
            if (!result.Success)
            {
                return result;
            }
        }

        int index = _tabs.IndexOf(tab);
        _tabs.RemoveAt(index);
        if (tab.Info.Exists)
        {
            _recentlyClosed.Remove(tab.Id);
            _recentlyClosed.Insert(0, tab.Id);
            if (_recentlyClosed.Count > MaxRecentlyClosed)
            {
                _recentlyClosed.RemoveAt(_recentlyClosed.Count - 1);
            }
        }

        // Closing the last tab leaves no tab: a note is never created (or deleted) by closing.
        bool wasActive = ReferenceEquals(Active, tab);
        if (wasActive)
        {
            Active = _tabs.Count == 0 ? null : _tabs[Math.Min(index, _tabs.Count - 1)];
        }

        SaveSession();
        TabsChanged?.Invoke(this, EventArgs.Empty);
        if (wasActive)
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        return SaveResult.Ok(tab);
    }

    /// <summary>Ctrl+Shift+T: re-opens the most recently closed note.</summary>
    public OpenNote? ReopenLastClosed()
    {
        while (_recentlyClosed.Count > 0)
        {
            string id = _recentlyClosed[0];
            _recentlyClosed.RemoveAt(0);
            if (Open(id) is { } tab)
            {
                return tab;
            }
        }

        return null;
    }

    /// <summary>Notes that exist on disk but are not open (for the "open note" list).</summary>
    public IReadOnlyList<NoteInfo> ClosedNotes =>
        _library.Notes.Where(n => _tabs.All(t => t.Id != n.Id)).ToList();

    /// <summary>
    /// Renames the note's file (pending edits are saved first so they move with it).
    /// Returns the title actually used (a number is appended if the name is taken).
    /// </summary>
    public async Task<string> RenameAsync(string id, string newTitle)
    {
        var tab = _tabs.FirstOrDefault(t => t.Id == id) ?? throw new InvalidOperationException("Note is not open.");
        if (tab.IsDirty && tab.Info.Exists)
        {
            var result = await SaveAsync(tab);
            if (!result.Success)
            {
                throw result.Error!;
            }
        }

        string title = _library.Rename(id, newTitle);
        tab.Store.Relocate(tab.Info.FilePath);
        SaveSession();
        TabsChanged?.Invoke(this, EventArgs.Empty);
        return title;
    }

    /// <summary>
    /// Conflict resolution: keep the editor's text, after copying the other program's version
    /// to a conflict file. Returns that copy's path.
    /// </summary>
    public string? ResolveConflictKeepMine(OpenNote tab)
    {
        string? copy = tab.Store.OverwriteKeepingCopy(tab.Text);
        tab.HasConflict = false;
        tab.IsDirty = false;
        RegisterCopy(copy);
        return copy;
    }

    /// <summary>
    /// Conflict resolution: load the other program's version, after saving the editor's text
    /// to a conflict file. Returns that copy's path.
    /// </summary>
    public string? ResolveConflictTakeTheirs(OpenNote tab)
    {
        var (disk, copy) = tab.Store.ReloadKeepingCopy(tab.Text);
        tab.Text = disk;
        tab.Caret = Math.Min(tab.Caret, disk.Length);
        tab.HasConflict = false;
        tab.IsDirty = false;
        RegisterCopy(copy);
        if (ReferenceEquals(tab, Active))
        {
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }

        return copy;
    }

    /// <summary>
    /// Routes a finished transcript to the note that was active when recording started, even if
    /// the user has switched tabs or closed it since. Never drops the text.
    /// </summary>
    /// <param name="editorRefused">
    /// The editor could not insert it (it showed another note by then): append it to the note's
    /// text here instead, even if it is the active tab; the page then shows the note again.
    /// </param>
    public async Task<(TranscriptDelivery Delivery, OpenNote? Tab)> DeliverTranscript(string targetId, string transcript, bool editorRefused = false)
    {
        if (Active?.Id == targetId && !editorRefused)
        {
            return (TranscriptDelivery.InsertIntoActiveEditor, Active);
        }

        if (_tabs.FirstOrDefault(t => t.Id == targetId) is { } openTab)
        {
            // Appended at the end; the caret stays where the user left it.
            openTab.Text += TranscriptInsertion.Append(openTab.Text, transcript, "\n");
            openTab.IsDirty = true;
            await SaveAsync(openTab);
            return (TranscriptDelivery.InsertedIntoOpenTab, openTab);
        }

        if (_library.Find(targetId) is { Exists: true } closed)
        {
            var store = NoteStorageService.ForFile(closed.FilePath);
            try
            {
                string text = store.Load();
                await store.SaveAsync(text + TranscriptInsertion.Append(text, transcript, "\n"));
                return (TranscriptDelivery.AppendedToClosedNote, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Fall through: keep the transcript in a new note rather than lose it.
            }
        }

        var rescue = NewNote($"文字起こし {DateTime.Now:yyyy-MM-dd HHmm}");
        rescue.Text = TranscriptInsertion.Block(transcript, "\n");
        rescue.Caret = rescue.Text.Length;
        rescue.IsDirty = true;
        await SaveAsync(rescue);
        ActiveChanged?.Invoke(this, EventArgs.Empty);
        return (TranscriptDelivery.SavedAsNewNote, rescue);
    }

    /// <summary>Persists open tabs, the active tab and carets (no note text).</summary>
    public void SaveSession()
    {
        var state = new SessionState
        {
            // Unsaved empty notes are not remembered: they have no file to reopen.
            Open = _tabs.Where(t => t.Info.Exists).Select(t => t.Id).ToList(),
            Active = Active?.Info.Exists == true ? Active.Id : null,
            Carets = _tabs.Where(t => t.Info.Exists).ToDictionary(t => t.Id, t => t.Caret),
            RecentlyClosed = _recentlyClosed.ToList(),
            AllClosed = _tabs.Count == 0,
        };

        try
        {
            Directory.CreateDirectory(_library.MetaDirectory);
            string temp = SessionPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(state, SessionJsonContext.Default.SessionState));
            File.Move(temp, SessionPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the tab layout is acceptable; note files are unaffected.
        }
    }

    private SessionState ReadSession()
    {
        try
        {
            return File.Exists(SessionPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(SessionPath), SessionJsonContext.Default.SessionState) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return new();
        }
    }

    private OpenNote? OpenInternal(NoteInfo info)
    {
        var store = NoteStorageService.ForFile(info.FilePath);
        var tab = new OpenNote(info, store, string.Empty);
        try
        {
            tab.Text = store.Load();
        }
        catch (Exception ex)
        {
            // Keep the tab so the user sees the problem; saving stays blocked to protect the file.
            tab.LoadError = $"{info.Title} を読み込めませんでした（{ex.Message}）";
        }

        _tabs.Add(tab);
        return tab;
    }

    /// <summary>A note without local edits silently picks up changes made by another program.</summary>
    private static void ReloadIfChangedExternally(OpenNote tab)
    {
        if (!tab.IsDirty && tab.LoadError is null && tab.Info.Exists && tab.Store.HasExternalChanges())
        {
            try
            {
                tab.Text = tab.Store.Load();
                tab.Caret = Math.Min(tab.Caret, tab.Text.Length);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
            {
                tab.LoadError = ex.Message;
            }
        }
    }

    private void RegisterCopy(string? copy)
    {
        if (copy is not null)
        {
            _library.Register(copy);
            TabsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Checks the active tab for outside changes (e.g. when the window is shown again).</summary>
    public bool RefreshActiveFromDisk()
    {
        if (Active is not { } tab)
        {
            return false;
        }

        string before = tab.Text;
        ReloadIfChangedExternally(tab);
        return !ReferenceEquals(before, tab.Text) && before != tab.Text;
    }
}

/// <summary>Outcome of saving one note.</summary>
public sealed record SaveResult(OpenNote Tab, Exception? Error)
{
    public bool Success => Error is null;

    public bool IsConflict => Error is NoteConflictException;

    public static SaveResult Ok(OpenNote tab) => new(tab, null);

    public static SaveResult Failed(OpenNote tab, Exception error) => new(tab, error);
}
