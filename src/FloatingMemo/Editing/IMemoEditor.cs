using FloatingMemo.Services.Notes;

namespace FloatingMemo.Editing;

/// <summary>The editor reports the current text of the note it shows.</summary>
public sealed record EditorTextChange(string NoteId, string Text, int Caret);

/// <summary>
/// The surface the memo is typed into. Two implementations: the Markdown live-preview editor
/// (WebView2 + CodeMirror) and a plain TextBox used until WebView2 is ready, or if it is not
/// available. The page only talks to this interface, so tabs, saving and transcripts work the
/// same with either.
/// </summary>
public interface IMemoEditor
{
    /// <summary>The note's text or caret changed (raised on the UI thread).</summary>
    event EventHandler<EditorTextChange>? TextChanged;

    /// <summary>The IME started/finished composing.</summary>
    event EventHandler<bool>? CompositionChanged;

    /// <summary>
    /// A transcript the editor could not insert (it was showing another note by then). The page
    /// must put it somewhere else; it is never dropped.
    /// </summary>
    event EventHandler<EditorTextChange>? InsertRejected;

    /// <summary>A shortcut pressed inside the editor: hide, new, close, save, next, previous, reopen, rename, record, focusOut.</summary>
    event EventHandler<string>? CommandInvoked;

    bool IsComposing { get; }

    /// <summary>Shows a note (its text and caret).</summary>
    void Show(OpenNote note);

    /// <summary>Inserts a transcript at the caret as its own paragraph (waits for the IME if composing).</summary>
    void InsertTranscript(string noteId, string transcript);

    /// <summary>The note's tab was closed: drop per-note editor state (undo history).</summary>
    void Forget(string noteId);

    void Focus();

    /// <summary>Makes sure every keystroke typed so far has been reported via <see cref="TextChanged"/>.</summary>
    /// <param name="settle">Also wait briefly for keystrokes still queued in the editor (used before exiting).</param>
    /// <returns>True when the editor confirmed the current text of the note it shows; false when it did not answer in time.</returns>
    Task<bool> FlushAsync(bool settle = false);
}
