using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatingMemo.Services.Notes;

/// <summary>
/// One note: a stable internal id plus the Markdown file it lives in. The id never changes,
/// so tabs, sessions (and later wiki links/backlinks) survive renames.
/// </summary>
public sealed class NoteInfo
{
    internal NoteInfo(string id, string filePath)
    {
        Id = id;
        FilePath = filePath;
    }

    public string Id { get; }

    public string FilePath { get; internal set; }

    /// <summary>File name without ".md" — the name a [[wiki link]] refers to.</summary>
    public string Title => Path.GetFileNameWithoutExtension(FilePath);

    public bool Exists => File.Exists(FilePath);
}

internal sealed record IndexEntry(string Id, string Path);

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(List<IndexEntry>))]
internal sealed partial class NoteIndexJsonContext : JsonSerializerContext
{
}

/// <summary>
/// The notes folder: every *.md directly in it (e.g. the original draft.md) and anything under
/// "notes\". Ids are kept in ".floatingmemo\notes.json" (a dot folder, which Obsidian ignores),
/// so the Markdown files themselves never need extra metadata.
/// </summary>
/// <remarks>
/// Existing files are never moved, renamed or modified here except by an explicit
/// <see cref="Rename"/>. A new note's file is created by its first save, not by this class.
/// </remarks>
public sealed class NoteLibrary
{
    private readonly Dictionary<string, NoteInfo> _byId = new(StringComparer.Ordinal);

    public NoteLibrary(string rootDirectory)
    {
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    public string RootDirectory { get; }

    /// <summary>Where new notes are created.</summary>
    public string NotesDirectory => Path.Combine(RootDirectory, "notes");

    /// <summary>App metadata (index, open tabs). Never contains note text.</summary>
    public string MetaDirectory => Path.Combine(RootDirectory, ".floatingmemo");

    private string IndexPath => Path.Combine(MetaDirectory, "notes.json");

    /// <summary>All known notes whose file exists, by title.</summary>
    public IReadOnlyList<NoteInfo> Notes =>
        _byId.Values.Where(n => n.Exists).OrderBy(n => n.Title, StringComparer.CurrentCulture).ToList();

    public NoteInfo? Find(string id) => _byId.GetValueOrDefault(id);

    public NoteInfo? FindByPath(string path) =>
        _byId.Values.FirstOrDefault(n => string.Equals(n.FilePath, Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Loads the index and matches it with the files actually on disk: new files get an id,
    /// entries whose file is gone are dropped. Safe to call repeatedly.
    /// </summary>
    public void Refresh()
    {
        foreach (var entry in ReadIndex())
        {
            string full = Path.GetFullPath(Path.Combine(RootDirectory, entry.Path));
            if (!_byId.ContainsKey(entry.Id) && File.Exists(full) && FindByPath(full) is null)
            {
                _byId[entry.Id] = new NoteInfo(entry.Id, full);
            }
        }

        foreach (string file in EnumerateMarkdownFiles())
        {
            if (FindByPath(file) is null)
            {
                var note = new NoteInfo(NewId(), file);
                _byId[note.Id] = note;
            }
        }

        foreach (var missing in _byId.Values.Where(n => !n.Exists && !_pending.Contains(n.Id)).ToList())
        {
            _byId.Remove(missing.Id);
        }

        WriteIndex();
    }

    // Notes created in this session whose file has not been written yet.
    private readonly HashSet<string> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// Reserves a new note with a unique title in <see cref="NotesDirectory"/>. No file is
    /// created until the note is first saved with some content.
    /// </summary>
    public NoteInfo CreateNote(string? title = null, DateTime? now = null)
    {
        string safe = NoteNames.Sanitize(title ?? NoteNames.DefaultTitle(now ?? DateTime.Now));
        string unique = NoteNames.MakeUnique(NotesDirectory, safe, TitlesIn(NotesDirectory));
        var note = new NoteInfo(NewId(), Path.Combine(NotesDirectory, unique + NoteNames.Extension));
        _byId[note.Id] = note;
        _pending.Add(note.Id);
        WriteIndex();
        return note;
    }

    /// <summary>
    /// Renames a note (and its file, if it exists) in the same folder. Never overwrites
    /// another file: if the title is taken, a number is appended.
    /// </summary>
    /// <returns>The title actually used.</returns>
    public string Rename(string id, string newTitle)
    {
        var note = Find(id) ?? throw new InvalidOperationException("Unknown note.");
        string directory = Path.GetDirectoryName(note.FilePath)!;
        string safe = NoteNames.Sanitize(newTitle);
        if (string.Equals(safe, note.Title, StringComparison.Ordinal))
        {
            return safe;
        }

        // Case-only renames ("memo" -> "Memo") must not count the note itself as a clash.
        var taken = TitlesIn(directory);
        taken.Remove(note.Title);
        string unique = string.Equals(safe, note.Title, StringComparison.OrdinalIgnoreCase)
            ? safe
            : NoteNames.MakeUnique(directory, safe, taken);
        string target = Path.Combine(directory, unique + NoteNames.Extension);

        if (note.Exists)
        {
            // overwrite: false — a rename can never destroy another file.
            File.Move(note.FilePath, target, overwrite: false);
        }

        note.FilePath = target;
        WriteIndex();
        return unique;
    }

    /// <summary>Called after a pending note's first save created its file.</summary>
    public void MarkSaved(string id)
    {
        if (_pending.Remove(id))
        {
            WriteIndex();
        }
    }

    /// <summary>Registers an existing file (e.g. a conflict copy) so it can be opened in a tab.</summary>
    public NoteInfo Register(string filePath)
    {
        if (FindByPath(filePath) is { } known)
        {
            return known;
        }

        var note = new NoteInfo(NewId(), Path.GetFullPath(filePath));
        _byId[note.Id] = note;
        WriteIndex();
        return note;
    }

    private HashSet<string> TitlesIn(string directory) =>
        _byId.Values
            .Where(n => string.Equals(Path.GetDirectoryName(n.FilePath), directory, StringComparison.OrdinalIgnoreCase))
            .Select(n => n.Title)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private IEnumerable<string> EnumerateMarkdownFiles()
    {
        if (Directory.Exists(RootDirectory))
        {
            foreach (string f in Directory.EnumerateFiles(RootDirectory, "*" + NoteNames.Extension))
            {
                yield return f;
            }
        }

        if (Directory.Exists(NotesDirectory))
        {
            foreach (string f in Directory.EnumerateFiles(NotesDirectory, "*" + NoteNames.Extension, SearchOption.AllDirectories))
            {
                yield return f;
            }
        }
    }

    private List<IndexEntry> ReadIndex()
    {
        try
        {
            return File.Exists(IndexPath)
                ? JsonSerializer.Deserialize(File.ReadAllText(IndexPath), NoteIndexJsonContext.Default.ListIndexEntry) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // A damaged index only loses id assignments; every note file is re-discovered.
            return [];
        }
    }

    private void WriteIndex()
    {
        try
        {
            var entries = _byId.Values
                .Select(n => new IndexEntry(n.Id, Path.GetRelativePath(RootDirectory, n.FilePath)))
                .OrderBy(e => e.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();
            Directory.CreateDirectory(MetaDirectory);
            string temp = IndexPath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(entries, NoteIndexJsonContext.Default.ListIndexEntry));
            File.Move(temp, IndexPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The index is a convenience; notes themselves are unaffected.
        }
    }

    private static string NewId() => Guid.NewGuid().ToString("N");
}
