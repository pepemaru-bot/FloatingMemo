using FloatingMemo.Services.Notes;
using FloatingMemo.Services.Transcription;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace FloatingMemo.Editing;

/// <summary>
/// Plain-text editor on a WinUI TextBox: shown instantly at start-up while the Markdown editor
/// loads, and kept as the fallback if WebView2 is unavailable.
/// </summary>
internal sealed class TextBoxEditor : IMemoEditor
{
    private readonly TextBox _box;
    private string? _noteId;
    private bool _loading;

    public TextBoxEditor(TextBox box)
    {
        _box = box;
        _box.TextChanged += (_, _) => Report();
        _box.SelectionChanged += (_, _) => Report();
        _box.TextCompositionStarted += (_, _) =>
        {
            IsComposing = true;
            CompositionChanged?.Invoke(this, true);
        };
        _box.TextCompositionEnded += (_, _) =>
        {
            // Clear the flag only after the current key has finished dispatching, so the
            // Esc that cancels a conversion cannot also reach the window's Esc accelerator.
            _box.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                IsComposing = false;
                CompositionChanged?.Invoke(this, false);
            });
        };
    }

    public event EventHandler<EditorTextChange>? TextChanged;

    public event EventHandler<bool>? CompositionChanged;

#pragma warning disable CS0067 // Shortcuts reach the page through XAML accelerators instead.
    public event EventHandler<string>? CommandInvoked;
#pragma warning restore CS0067

    public bool IsComposing { get; private set; }

    public void Show(OpenNote note)
    {
        _loading = true;
        try
        {
            _noteId = note.Id;
            _box.Text = note.Text;
            _box.Select(Math.Min(note.Caret, _box.Text.Length), 0);
            // A note that could not be read is shown read-only so it cannot be overwritten.
            _box.IsReadOnly = note.LoadError is not null;
        }
        finally
        {
            _loading = false;
        }
    }

    public void InsertTranscript(string noteId, string transcript)
    {
        if (noteId != _noteId)
        {
            return;
        }

        // Appended at the end of the note; the caret and selection are restored afterwards.
        int start = _box.SelectionStart, length = _box.SelectionLength;
        string insertion = TranscriptInsertion.Append(_box.Text, transcript, "\r");
        _box.Select(_box.Text.Length, 0);
        _box.SelectedText = insertion; // keeps the edit in the TextBox undo history
        _box.Select(start, length);
    }

    public void Forget(string noteId)
    {
    }

    public void Focus() => _box.Focus(FocusState.Programmatic);

    public Task FlushAsync(bool settle = false) => Task.CompletedTask; // every change is reported synchronously

    private void Report()
    {
        if (!_loading && _noteId is { } id)
        {
            TextChanged?.Invoke(this, new EditorTextChange(id, _box.Text, _box.SelectionStart));
        }
    }
}
