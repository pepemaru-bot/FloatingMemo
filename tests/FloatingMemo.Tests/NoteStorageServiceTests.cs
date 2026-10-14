using System.Text;
using FloatingMemo.Services;

namespace FloatingMemo.Tests;

public sealed class NoteStorageServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));

    private string FilePath => Path.Combine(_dir, "draft.md");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            foreach (var f in Directory.GetFiles(_dir))
            {
                File.SetAttributes(f, FileAttributes.Normal);
            }
            Directory.Delete(_dir, recursive: true);
        }
    }

    [Fact]
    public void Load_MissingFile_ReturnsEmptyAndCreatesNothing()
    {
        var storage = new NoteStorageService(_dir);

        Assert.Equal(string.Empty, storage.Load());
        Assert.False(Directory.Exists(_dir));
    }

    [Fact]
    public async Task SaveAsync_CreatesFolderAndWritesUtf8MarkdownWithoutBom()
    {
        var storage = new NoteStorageService(_dir);
        storage.Load();
        const string markdown = "# 見出し\n- [ ] タスク **太字** [link](https://example.com)\n\n```cs\nvar x = 1;\n```";

        await storage.SaveAsync(markdown);

        byte[] bytes = File.ReadAllBytes(FilePath);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "BOM must not be written");
        Assert.Equal(markdown, Encoding.UTF8.GetString(bytes));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task Save_ThenReload_RestoresSameText()
    {
        const string markdown = "## メモ\n- 項目1\n- 項目2\n> 引用";
        var first = new NoteStorageService(_dir);
        first.Load();
        await first.SaveAsync(markdown);

        var second = new NoteStorageService(_dir);

        Assert.Equal(markdown, second.Load());
    }

    [Fact]
    public async Task Save_NormalizesTextBoxLineBreaksToLf()
    {
        var storage = new NoteStorageService(_dir);
        storage.Load();

        await storage.SaveAsync("a\rb\r\nc");

        Assert.Equal("a\nb\nc", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task Save_EmptyStringAfterContent_WritesEmptyFile()
    {
        var storage = new NoteStorageService(_dir);
        storage.Load();
        await storage.SaveAsync("something");

        await storage.SaveAsync(string.Empty);

        Assert.True(File.Exists(FilePath));
        Assert.Equal(0, new FileInfo(FilePath).Length);
    }

    [Fact]
    public void Save_Synchronous_WritesFile()
    {
        var storage = new NoteStorageService(_dir);
        storage.Load();

        storage.Save("exit save");

        Assert.Equal("exit save", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task Save_Failure_ThrowsKeepsOldFileAndCanRetry()
    {
        var storage = new NoteStorageService(_dir);
        storage.Load();
        await storage.SaveAsync("old");
        File.SetAttributes(FilePath, FileAttributes.ReadOnly);

        await Assert.ThrowsAnyAsync<Exception>(() => storage.SaveAsync("new"));

        Assert.Equal("old", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));

        File.SetAttributes(FilePath, FileAttributes.Normal);
        await storage.SaveAsync("new");
        Assert.Equal("new", File.ReadAllText(FilePath));
    }

    [Fact]
    public async Task Load_InvalidUtf8_ThrowsAndBlocksOverwrite()
    {
        Directory.CreateDirectory(_dir);
        byte[] invalid = [0x23, 0x20, 0xFF, 0xFE, 0x41];
        File.WriteAllBytes(FilePath, invalid);
        var storage = new NoteStorageService(_dir);

        Assert.ThrowsAny<Exception>(() => storage.Load());
        await Assert.ThrowsAnyAsync<IOException>(() => storage.SaveAsync("would overwrite"));

        Assert.Equal(invalid, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public async Task Load_FileWithBom_ReturnsTextWithoutBom()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "# BOM付き", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var storage = new NoteStorageService(_dir);

        Assert.Equal("# BOM付き", storage.Load());

        await storage.SaveAsync("# BOM付き\n追記");
        Assert.NotEqual(0xEF, File.ReadAllBytes(FilePath)[0]);
    }

    [Fact]
    public async Task Load_IgnoresLeftoverTempFileFromCrash()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(FilePath, "committed");
        File.WriteAllText(FilePath + ".tmp", "half-writ");
        var storage = new NoteStorageService(_dir);

        Assert.Equal("committed", storage.Load());

        await storage.SaveAsync("next");
        Assert.Equal("next", File.ReadAllText(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
    }

    [Fact]
    public async Task ConcurrentSaves_LastRequestWins()
    {
        var storage = new NoteStorageService(_dir);
        storage.Load();

        var tasks = Enumerable.Range(1, 50).Select(i => storage.SaveAsync($"version {i}")).ToArray();
        await Task.WhenAll(tasks);

        Assert.Equal("version 50", File.ReadAllText(FilePath));
    }
}
