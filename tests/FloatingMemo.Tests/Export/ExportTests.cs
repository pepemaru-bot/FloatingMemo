using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using DocumentFormat.OpenXml.Wordprocessing;
using FloatingMemo.Services.Export;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Export;

public sealed class ExportTests(ITestOutputHelper output) : IDisposable
{
    // Made-up content covering what notes and AI minutes use.
    private const string Sample = """
        # 定例会議（架空）

        本文の**太字**と*斜体*、~~取り消し~~、`コード`、[リンク](https://example.com/資料)。
        自動リンク https://example.org も。

        ## 決定事項
        - リリース日は十一月十四日（原文:「十一月十四日に決まりました」）
        - 担当: 山本さん
          - 下位の項目
        1. 一つ目
        2. 二つ目

        ## ToDo
        - [ ] 資料の準備 — 担当: 未確認
        - [x] 会場の予約

        > 引用された発言です。

        | 項目 | 担当 | 期限 |
        |---|---|---|
        | 資料 | 山本 | 金曜日 |
        | 会場 | **未確認** | 未確認 |

        ```
        コードブロック
        二行目
        ```

        ---

        ## 校正の記録
        - 「加工」→「架空」：修正済み
        """;

    private readonly string _dir = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void MinutesOnly_DropsTheCorrectionAppendix_WholeNoteKeepsIt()
    {
        Assert.Contains("校正の記録", ExportContent.Select(Sample, ExportScope.WholeNote));
        string minutes = ExportContent.Select(Sample, ExportScope.MinutesOnly);
        Assert.DoesNotContain("校正の記録", minutes);
        Assert.Contains("| 会場 | **未確認** | 未確認 |", minutes);
    }

    [Fact]
    public void FileName_IsSafe_AndMarksMinutes()
    {
        Assert.Equal("会議_メモ", ExportContent.SuggestedFileName("会議/メモ", ExportScope.WholeNote));
        Assert.Equal("会議（AI議事録）", ExportContent.SuggestedFileName("会議", ExportScope.MinutesOnly));
        Assert.Equal("会議（AI議事録）", ExportContent.SuggestedFileName("会議（AI議事録）", ExportScope.MinutesOnly));
    }

    [Fact]
    public void Html_IsSelfContained_A4_AndKeepsStructure()
    {
        string html = HtmlExport.ToHtml(Sample, "定例会議");
        Assert.Contains("size: A4", html);
        Assert.Contains("<meta charset=\"utf-8\">", html);
        Assert.Contains("default-src 'none'", html);           // nothing loaded from outside
        Assert.Contains("<h1", html);
        Assert.Contains("<strong>太字</strong>", html);
        Assert.Contains("<table>", html);
        Assert.Contains("href=\"https://example.com/", html);
        Assert.Contains("type=\"checkbox\"", html);
        Assert.Contains("リリース日は十一月十四日", html);       // Japanese kept as is
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Html_RawHtmlInANote_IsNotExecuted()
    {
        string html = HtmlExport.ToHtml("<script>alert(1)</script>\n<img src=\"https://example.com/x.png\">", "x");
        Assert.DoesNotContain("<script>alert", html);
        Assert.DoesNotContain("<img src=\"https://", html);
    }

    [Fact]
    public void Docx_IsValidOpenXml_AndKeepsHeadingsListsTablesBoldLinks()
    {
        string path = Path.Combine(_dir, "out.docx");
        using (var file = File.Create(path))
        {
            DocxExport.Write(Sample, "定例会議", file);
        }

        using var document = WordprocessingDocument.Open(path, false);
        var errors = new OpenXmlValidator().Validate(document).ToList();
        foreach (var error in errors.Take(10))
        {
            output.WriteLine($"{error.Path?.XPath}: {error.Description}");
        }

        Assert.Empty(errors);
        var body = document.MainDocumentPart!.Document!.Body!;
        string Style(Paragraph p) => p.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Normal";
        var paragraphs = body.Elements<Paragraph>().ToList();

        Assert.Contains(paragraphs, p => Style(p) == "Heading1" && p.InnerText == "定例会議（架空）");
        Assert.Contains(paragraphs, p => Style(p) == "Heading2" && p.InnerText == "決定事項");
        Assert.Contains(body.Descendants<Run>(), r => r.RunProperties?.Bold is not null && r.InnerText == "太字");
        Assert.Contains(body.Descendants<Run>(), r => r.RunProperties?.Italic is not null && r.InnerText == "斜体");
        Assert.Contains(body.Descendants<Run>(), r => r.RunProperties?.Strike is not null && r.InnerText == "取り消し");

        // Lists use Word numbering (editable bullets/numbers), with nesting.
        var listItems = paragraphs.Where(p => p.ParagraphProperties?.NumberingProperties is not null).ToList();
        Assert.True(listItems.Count >= 7, $"list paragraphs: {listItems.Count}");
        Assert.Contains(listItems, p => p.ParagraphProperties!.NumberingProperties!.NumberingLevelReference!.Val!.Value == 1 && p.InnerText == "下位の項目");
        Assert.Contains(paragraphs, p => p.InnerText == "☐ 資料の準備 — 担当: 未確認");
        Assert.Contains(paragraphs, p => p.InnerText == "☑ 会場の予約");

        // Table with header row and bold text kept.
        var table = body.Elements<DocumentFormat.OpenXml.Wordprocessing.Table>().Single();
        var rows = table.Elements<TableRow>().ToList();
        Assert.Equal(3, rows.Count);
        Assert.Equal("項目担当期限", rows[0].InnerText);
        Assert.Contains(rows[2].Descendants<Run>(), r => r.RunProperties?.Bold is not null && r.InnerText == "未確認");

        // Hyperlinks point at the original URLs.
        var links = document.MainDocumentPart.HyperlinkRelationships.Select(r => r.Uri.OriginalString).ToList();
        Assert.Contains(links, l => l.StartsWith("https://example.com/", StringComparison.Ordinal));
        Assert.Contains("https://example.org", links);
        Assert.Contains(body.Descendants<Hyperlink>(), h => h.InnerText == "リンク");

        // Japanese text and a Japanese East Asian font.
        Assert.Contains("リリース日は十一月十四日", body.InnerText);
        var fonts = document.MainDocumentPart.StyleDefinitionsPart!.Styles!.DocDefaults!.Descendants<RunFonts>().Single();
        Assert.Equal("Yu Gothic", fonts.EastAsia!.Value);
        Assert.Equal("定例会議", document.PackageProperties.Title);
    }

    [Fact]
    public void Docx_EmptyNote_IsStillAValidDocument()
    {
        using var stream = new MemoryStream();
        DocxExport.Write(string.Empty, "空", stream);
        stream.Position = 0;
        using var document = WordprocessingDocument.Open(stream, false);
        Assert.Empty(new OpenXmlValidator().Validate(document));
    }

    [Fact]
    public void SafeWriter_Failure_LeavesTheDestinationUntouched_AndNoTempFile()
    {
        string path = Path.Combine(_dir, "existing.docx");
        File.WriteAllText(path, "前のファイル");
        Assert.Throws<InvalidOperationException>(() => SafeFileWriter.Write(path, s =>
        {
            s.WriteByte(1);
            throw new InvalidOperationException("途中で失敗");
        }));

        Assert.Equal("前のファイル", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }

    [Fact]
    public void Docx_ThroughTheSafeWriter_IsComplete()
    {
        // The real export path (not a test stream): the writer must allow the package to read back.
        string path = Path.Combine(_dir, "real.docx");
        SafeFileWriter.Write(path, s => DocxExport.Write(Sample, "定例会議", s));
        using var document = WordprocessingDocument.Open(path, false);
        Assert.Contains("リリース日は十一月十四日", document.MainDocumentPart!.Document!.Body!.InnerText);
    }

    [Fact]
    public void SafeWriter_Success_ReplacesTheDestination()
    {
        string path = Path.Combine(_dir, "a.docx");
        SafeFileWriter.Write(path, s => s.Write("新"u8));
        Assert.Equal("新", File.ReadAllText(path));
        Assert.Single(Directory.GetFiles(_dir));
    }
}
