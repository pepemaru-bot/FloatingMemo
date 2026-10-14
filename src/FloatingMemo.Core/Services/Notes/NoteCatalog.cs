using System.Text;

namespace FloatingMemo.Services.Notes;

public enum NoteSort
{
    /// <summary>Most recently updated first (the default).</summary>
    NewestFirst,

    OldestFirst,

    ByTitle,
}

/// <summary>One row of the saved-notes list.</summary>
public sealed record NoteListEntry(NoteInfo Note, DateTime Modified, bool IsOpen, bool MatchedInBody);

/// <summary>
/// The saved-notes list (Ctrl+O): every Markdown note on disk, open in a tab or not, filtered by
/// a search and sorted. Reading the list never changes a note.
/// </summary>
public static class NoteCatalog
{
    /// <summary>Notes larger than this are searched by title only (keeps the list responsive).</summary>
    public const long MaxSearchBytes = 2 * 1024 * 1024;

    /// <param name="readText">Reads a note's text for the body search (null if unreadable).</param>
    public static IReadOnlyList<NoteListEntry> Query(
        IEnumerable<NoteInfo> notes, IReadOnlyCollection<string> openIds, string? query, NoteSort sort, Func<NoteInfo, string?> readText)
    {
        string[] terms = Normalize(query ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var entries = new List<NoteListEntry>();
        foreach (var note in notes)
        {
            if (!note.Exists)
            {
                continue;
            }

            bool matchedInBody = false;
            if (terms.Length > 0)
            {
                string title = Normalize(note.Title);
                if (!terms.All(t => title.Contains(t, StringComparison.Ordinal)))
                {
                    // Every word must appear in the title or the text (in any order).
                    string body = Normalize(readText(note) ?? string.Empty);
                    if (!terms.All(t => title.Contains(t, StringComparison.Ordinal) || body.Contains(t, StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    matchedInBody = true;
                }
            }

            entries.Add(new NoteListEntry(note, Modified(note), openIds.Contains(note.Id), matchedInBody));
        }

        return sort switch
        {
            NoteSort.OldestFirst => entries.OrderBy(e => e.Modified).ToList(),
            NoteSort.ByTitle => entries.OrderBy(e => e.Note.Title, StringComparer.CurrentCulture).ToList(),
            _ => entries.OrderByDescending(e => e.Modified).ToList(),
        };
    }

    /// <summary>Reads a note for searching; very large or unreadable files are skipped.</summary>
    public static string? ReadForSearch(NoteInfo note)
    {
        try
        {
            var file = new FileInfo(note.FilePath);
            return file.Exists && file.Length <= MaxSearchBytes ? File.ReadAllText(file.FullName, Encoding.UTF8) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static DateTime Modified(NoteInfo note)
    {
        try
        {
            return File.GetLastWriteTime(note.FilePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>Case- and width-insensitive (「ＡＢＣ」 finds "abc", 「ﾒﾓ」 finds 「メモ」).</summary>
    private static string Normalize(string text) =>
        text.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Replace('　', ' ');
}
