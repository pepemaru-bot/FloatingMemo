using System.Text;

namespace FloatingMemo.Services;

/// <summary>
/// The file on disk was changed by someone else (e.g. Obsidian) since we last read or wrote it.
/// Nothing was written; the caller decides which version wins (the other is kept as a copy).
/// </summary>
public sealed class NoteConflictException : IOException
{
    public NoteConflictException(string filePath)
        : base($"{Path.GetFileName(filePath)} が外部で変更されています。上書きせずに保存を止めました。")
    {
        FilePath = filePath;
    }

    public string FilePath { get; }
}

/// <summary>
/// Persists one note as a UTF-8 Markdown file on the local disk. No UI dependencies,
/// so it can be unit tested and pointed at any folder (e.g. an Obsidian vault).
/// </summary>
/// <remarks>
/// - Writes go to a temp file in the same folder and are then moved over the target,
///   so a crash mid-write leaves either the old or the new file, never a truncated one.
/// - Saves are serialized; a save request older than the last written one is dropped.
/// - The file's size and timestamp are remembered after every read/write. If they differ at
///   the next save, another program changed the file and <see cref="NoteConflictException"/>
///   is thrown instead of silently overwriting it.
/// </remarks>
public sealed class NoteStorageService
{
    // UTF-8 without BOM is what Markdown tools (Obsidian, Git, editors) expect.
    // throwOnInvalidBytes makes a damaged/non-UTF-8 file fail to load instead of
    // being silently "repaired" and then overwritten.
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private long _requestedVersion;
    private long _writtenVersion;
    private string? _lastWrittenText;
    private bool _loadFailed;
    private FileStamp? _knownStamp;

    public NoteStorageService(string directory, string fileName = "draft.md")
    {
        FilePath = Path.GetFullPath(Path.Combine(directory, fileName));
    }

    /// <summary>A store for a note at an explicit file path.</summary>
    public static NoteStorageService ForFile(string filePath) =>
        new(Path.GetDirectoryName(Path.GetFullPath(filePath))!, Path.GetFileName(filePath));

    public string Directory => Path.GetDirectoryName(FilePath)!;

    public string FilePath { get; private set; }

    /// <summary>Default notes folder: %USERPROFILE%\FloatingMemo (outside OneDrive-redirected Documents).</summary>
    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FloatingMemo");

    /// <summary>
    /// Reads the memo. Returns an empty string if the file does not exist yet.
    /// If reading fails, saving is blocked so the unreadable file is never overwritten.
    /// </summary>
    public string Load()
    {
        try
        {
            string text = File.Exists(FilePath) ? NormalizeNewlines(File.ReadAllText(FilePath, Utf8)) : string.Empty;
            _lastWrittenText = text;
            _knownStamp = FileStamp.Of(FilePath);
            _loadFailed = false;
            return text;
        }
        catch
        {
            _loadFailed = true;
            throw;
        }
    }

    /// <summary>True if the file now differs from what this service last read or wrote.</summary>
    public bool HasExternalChanges() => FileStamp.Of(FilePath) != _knownStamp;

    /// <summary>Saves on a background thread so the UI thread is never blocked by disk I/O.</summary>
    public async Task SaveAsync(string text)
    {
        long version = Interlocked.Increment(ref _requestedVersion);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(() => WriteIfNewer(text, version, force: false)).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Synchronous save for app exit, where the process may end right after.</summary>
    public void Save(string text)
    {
        long version = Interlocked.Increment(ref _requestedVersion);
        _gate.Wait();
        try
        {
            WriteIfNewer(text, version, force: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Conflict resolution "keep mine": the version on disk is first copied to a conflict file
    /// next to the note (so it is never lost), then <paramref name="text"/> is written.
    /// </summary>
    /// <returns>The path of the copy of the other version, or null if there was nothing to keep.</returns>
    public string? OverwriteKeepingCopy(string text)
    {
        _gate.Wait();
        try
        {
            string? copy = File.Exists(FilePath) && HasExternalChanges() ? CopyToConflictFile(File.ReadAllText(FilePath, Utf8)) : null;
            WriteIfNewer(text, Interlocked.Increment(ref _requestedVersion), force: true);
            return copy;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Conflict resolution "take theirs": <paramref name="myText"/> is saved to a conflict file
    /// (so it is never lost) and the version on disk is loaded and returned.
    /// </summary>
    public (string DiskText, string? CopyPath) ReloadKeepingCopy(string myText)
    {
        _gate.Wait();
        try
        {
            string? copy = NormalizeNewlines(myText) == _lastWrittenText ? null : CopyToConflictFile(myText);
            return (Load(), copy);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Points the service at a renamed file (the caller has already moved it).</summary>
    public void Relocate(string newFilePath)
    {
        _gate.Wait();
        try
        {
            FilePath = Path.GetFullPath(newFilePath);
            _knownStamp = FileStamp.Of(FilePath);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void WriteIfNewer(string text, long version, bool force)
    {
        if (_loadFailed)
        {
            throw new IOException($"既存のメモ ({FilePath}) を読み込めなかったため、上書きを防ぐために保存を停止しています。");
        }

        if (version < _writtenVersion)
        {
            return; // A newer save already completed.
        }

        string normalized = NormalizeNewlines(text);
        bool changedOnDisk = FileStamp.Of(FilePath) is { } current && current != _knownStamp;
        if (changedOnDisk && !force)
        {
            throw new NoteConflictException(FilePath);
        }

        if (normalized == _lastWrittenText && !changedOnDisk && File.Exists(FilePath) == (_knownStamp is not null))
        {
            _writtenVersion = version;
            return; // Nothing changed since the last successful write.
        }

        if (normalized.Length == 0 && _knownStamp is null && !File.Exists(FilePath))
        {
            _writtenVersion = version;
            return; // Never create an empty file for a note that has no content yet.
        }

        WriteAtomically(normalized);
        _lastWrittenText = normalized;
        _knownStamp = FileStamp.Of(FilePath);
        _writtenVersion = version;
    }

    private string CopyToConflictFile(string text)
    {
        string name = Path.GetFileNameWithoutExtension(FilePath);
        string path = Path.Combine(Directory, $"{name} (競合 {DateTime.Now:yyyy-MM-dd HHmmss}).md");
        for (int i = 2; File.Exists(path); i++)
        {
            path = Path.Combine(Directory, $"{name} (競合 {DateTime.Now:yyyy-MM-dd HHmmss} {i}).md");
        }

        File.WriteAllText(path, NormalizeNewlines(text), Utf8);
        return path;
    }

    private void WriteAtomically(string text)
    {
        System.IO.Directory.CreateDirectory(Directory);

        string tempPath = FilePath + ".tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = Utf8.GetBytes(text);
                stream.Write(bytes, 0, bytes.Length);
                // Flush to disk (not just the OS cache) before the rename makes it visible.
                stream.Flush(flushToDisk: true);
            }

            // Same-volume rename with replace (MoveFileEx MOVEFILE_REPLACE_EXISTING).
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// WinUI's TextBox uses "\r" for line breaks; store LF as is standard for Markdown.
    /// </summary>
    public static string NormalizeNewlines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n');

    /// <summary>Size + last write time: cheap and enough to notice another program's save.</summary>
    private readonly record struct FileStamp(long Length, long LastWriteTicks)
    {
        public static FileStamp? Of(string path)
        {
            var info = new FileInfo(path);
            return info.Exists ? new FileStamp(info.Length, info.LastWriteTimeUtc.Ticks) : null;
        }
    }
}
