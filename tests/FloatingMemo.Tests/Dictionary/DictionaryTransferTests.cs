using FloatingMemo.Services.Dictionary;

namespace FloatingMemo.Tests.Dictionary;

/// <summary>Dictionary export / import (CSV and JSON). Made-up terms only.</summary>
public sealed class DictionaryTransferTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fm-dict-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    private TermDictionary NewDictionary(string name = "dictionary.json") => new(Path.Combine(_dir, name));

    private static List<TermEntry> Sample() =>
    [
        new() { Term = "架空ホール", Reading = "かくうほーる", Aliases = ["ホール"], Misrecognitions = ["加工ホール"], Category = "施設名", WhisperHint = true, Source = "user", Updated = new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Local) },
        new() { Term = "定例報告会", Category = "用語", Enabled = false, Source = "user", Updated = new DateTime(2026, 10, 2, 9, 0, 0, DateTimeKind.Local) },
        // Commas, quotes, a line break and leading spaces in cells.
        new() { Term = "株式会社\"みなと\", 本社", Reading = " みなと ", Misrecognitions = ["港\n本社"], Category = "組織", Source = "import:x", Updated = new DateTime(2026, 10, 3, 9, 0, 0, DateTimeKind.Local) },
    ];

    private static void AssertSame(IReadOnlyList<TermEntry> expected, IReadOnlyList<TermEntry> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            var (a, b) = (expected[i], actual[i]);
            Assert.True(DictionaryTransfer.SameContentForTests(a, b), $"entry {i}: {a.Term}");
            Assert.Equal(a.Id, b.Id);
            Assert.Equal(a.Source, b.Source);
            Assert.Equal(a.Updated, b.Updated);
        }
    }

    [Fact]
    public void Json_RoundTrip_RestoresEveryField()
    {
        var entries = Sample();
        var back = DictionaryTransfer.ParseExportedJson(DictionaryTransfer.ExportJson(entries));
        Assert.NotNull(back);
        AssertSame(entries, back!);
    }

    [Fact]
    public void Csv_RoundTrip_RestoresEveryField_EvenWithCommasQuotesAndLineBreaks()
    {
        var entries = Sample();
        AssertSame(entries, DictionaryTransfer.ParseCsv(DictionaryTransfer.ExportCsv(entries)));
    }

    [Fact]
    public void Csv_WithJapaneseHeaders_AndABom_IsRead()
    {
        var entries = DictionaryTransfer.ParseCsv("﻿用語,読み,誤認識,分類\r\n架空ホール,かくうほーる,加工ホール | 架空保留,施設名\r\n");
        var e = Assert.Single(entries);
        Assert.Equal("架空ホール", e.Term);
        Assert.Equal(["加工ホール", "架空保留"], e.Misrecognitions);
        Assert.Throws<FormatException>(() => DictionaryTransfer.ParseCsv("name,x\r\na,b\r\n"));
    }

    [Fact]
    public void Preview_CountsChanges_AndDuplicatesInTheFile()
    {
        var existing = Sample();
        List<TermEntry> incoming =
        [
            new() { Term = "架空ホール", Category = "施設名", Aliases = ["ホール"], Misrecognitions = ["加工ホール"], Reading = "かくうほーる", WhisperHint = true }, // same
            new() { Term = "定例報告会", Category = "会議", Enabled = false },  // changed
            new() { Term = "新しい用語", Category = "用語" },
            new() { Term = "新しい用語", Category = "用語" },                 // twice in the file
            new() { Term = " " },                                            // no term
        ];
        var add = DictionaryTransfer.Preview(existing, incoming, ImportMode.Add);
        Assert.Equal((1, 0, 2, 0), (add.Added, add.Updated, add.Unchanged, add.Removed));
        Assert.Equal(["新しい用語"], add.DuplicatesInFile);
        Assert.Equal(1, add.Skipped);

        var update = DictionaryTransfer.Preview(existing, incoming, ImportMode.Update);
        Assert.Equal((1, 1, 1, 0), (update.Added, update.Updated, update.Unchanged, update.Removed));
        var replace = DictionaryTransfer.Preview(existing, incoming, ImportMode.Replace);
        Assert.Equal(1, replace.Removed);   // 株式会社… is not in the file
    }

    [Fact]
    public void Apply_Modes_AndABackupBeforeReplacing()
    {
        var dict = NewDictionary();
        dict.Apply(Sample(), ImportMode.Replace);
        string before = File.ReadAllText(dict.FilePath);

        // Add: existing entries untouched.
        dict.Apply([new TermEntry { Term = "定例報告会", Category = "会議" }, new TermEntry { Term = "追加分" }], ImportMode.Add);
        Assert.Equal("用語", dict.Entries.Single(e => e.Term == "定例報告会").Category);
        Assert.Contains(dict.Entries, e => e.Term == "追加分");

        // Update: the imported values win, spellings are merged.
        var (_, backup) = dict.Apply([new TermEntry { Term = "架空ホール", Category = "会場", Misrecognitions = ["架空保留"] }], ImportMode.Update);
        var hall = dict.Entries.Single(e => e.Term == "架空ホール");
        Assert.Equal("会場", hall.Category);
        Assert.Equal(["加工ホール", "架空保留"], hall.Misrecognitions);
        Assert.NotNull(backup);

        // Replace: exactly the file's entries; the previous dictionary is in the backup.
        var (result, replaceBackup) = dict.Apply([new TermEntry { Term = "だけ" }], ImportMode.Replace);
        Assert.Equal(["だけ"], dict.Entries.Select(e => e.Term));
        Assert.True(result.Removed >= 3);
        Assert.True(File.Exists(replaceBackup));
        Assert.Contains("追加分", File.ReadAllText(replaceBackup!));
        Assert.Contains("架空ホール", before);
    }

    [Fact]
    public void ExportThenReplace_GivesTheSameDictionaryBack()
    {
        var source = NewDictionary("a.json");
        source.Apply(Sample(), ImportMode.Replace);
        var target = NewDictionary("b.json");
        target.Apply([new TermEntry { Term = "消えるはずの語" }], ImportMode.Replace);

        target.Apply(DictionaryTransfer.ParseCsv(DictionaryTransfer.ExportCsv(source.Entries)), ImportMode.Replace);
        AssertSame(source.Entries, target.Entries);
        target.Apply(DictionaryTransfer.ParseExportedJson(DictionaryTransfer.ExportJson(source.Entries))!, ImportMode.Replace);
        AssertSame(source.Entries, target.Entries);
    }
}
