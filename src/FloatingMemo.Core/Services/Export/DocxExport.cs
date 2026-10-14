using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdTableRow = Markdig.Extensions.Tables.TableRow;
using MdTableCell = Markdig.Extensions.Tables.TableCell;
using WTable = DocumentFormat.OpenXml.Wordprocessing.Table;
using WRow = DocumentFormat.OpenXml.Wordprocessing.TableRow;
using WCell = DocumentFormat.OpenXml.Wordprocessing.TableCell;

namespace FloatingMemo.Services.Export;

/// <summary>
/// Markdown → an editable Word document (.docx, Office Open XML). Uses real Word features so the
/// result stays editable in Word and Google Docs: heading styles, numbered/bulleted lists,
/// tables, bold/italic/strike runs, hyperlinks, quotes and code. Japanese text uses a Japanese
/// font for East Asian characters, so it is not garbled.
/// </summary>
public static class DocxExport
{
    private const string LatinFont = "Segoe UI";
    private const string JapaneseFont = "Yu Gothic";
    private const string CodeFont = "Consolas";
    private const int BulletAbstractId = 1;
    private const int NumberAbstractId = 2;

    public static void Write(string markdown, string title, Stream output)
    {
        var document = Markdown.Parse(markdown, ExportContent.Pipeline);
        using var package = WordprocessingDocument.Create(output, WordprocessingDocumentType.Document);
        var main = package.AddMainDocumentPart();
        main.Document = new Document(new Body());
        AddStyles(main);
        var numbering = AddNumbering(main);
        package.PackageProperties.Title = title;
        package.PackageProperties.Creator = "Floating Memo";

        var writer = new Writer(main, numbering);
        foreach (var block in document)
        {
            writer.Block(block, 0);
        }

        var body = main.Document.Body!;
        if (!body.HasChildren)
        {
            body.Append(new Paragraph());
        }

        // A4 portrait, margins about 2 cm (Word measures in twentieths of a point).
        body.Append(new SectionProperties(
            new PageSize { Width = 11906U, Height = 16838U },
            new PageMargin { Top = 1134, Bottom = 1134, Left = 1077U, Right = 1077U, Header = 567U, Footer = 567U, Gutter = 0U }));
        main.Document.Save();
    }

    // ----- Document structure ----------------------------------------------------------------

    private sealed class Writer(MainDocumentPart main, Numbering numbering)
    {
        private readonly Body _body = main.Document!.Body!;
        private int _nextNumberId = 10;

        public void Block(Block block, int listLevel, int? numberId = null, bool firstInItem = false, bool? task = null)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    _body.Append(Paragraph($"Heading{Math.Clamp(heading.Level, 1, 4)}", heading.Inline));
                    break;

                case ParagraphBlock paragraph when numberId is not null && firstInItem:
                {
                    var p = Paragraph("ListParagraph", paragraph.Inline, taskMark: task);
                    p.ParagraphProperties!.Append(new NumberingProperties(
                        new NumberingLevelReference { Val = Math.Min(listLevel, 8) },
                        new NumberingId { Val = numberId.Value }));
                    _body.Append(p);
                    break;
                }

                case ParagraphBlock paragraph when listLevel > 0:
                {
                    // Follow-up paragraphs of a list item: indented, no new bullet.
                    var p = Paragraph("ListParagraph", paragraph.Inline);
                    p.ParagraphProperties!.Append(new Indentation { Left = (360 * (listLevel + 1)).ToString() });
                    _body.Append(p);
                    break;
                }

                case ParagraphBlock paragraph:
                    _body.Append(Paragraph("Normal", paragraph.Inline));
                    break;

                case ListBlock list:
                {
                    int id = NewList(list.IsOrdered, list.OrderedStart);
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        bool first = true;
                        bool? isTask = item.Descendants<TaskList>().FirstOrDefault() is { } t ? t.Checked : null;
                        foreach (var child in item)
                        {
                            Block(child, listLevel + (child is ListBlock ? 1 : 0), id, first && child is ParagraphBlock, first ? isTask : null);
                            if (child is ParagraphBlock)
                            {
                                first = false;
                            }
                        }

                        if (first)
                        {
                            // An empty item ("- ") still gets its bullet.
                            var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "ListParagraph" },
                                new NumberingProperties(new NumberingLevelReference { Val = Math.Min(listLevel, 8) }, new NumberingId { Val = id })));
                            _body.Append(p);
                        }
                    }

                    break;
                }

                case QuoteBlock quote:
                    foreach (var child in quote)
                    {
                        if (child is ParagraphBlock p)
                        {
                            _body.Append(Paragraph("Quote", p.Inline));
                        }
                        else
                        {
                            Block(child, listLevel);
                        }
                    }

                    break;

                case FencedCodeBlock or CodeBlock:
                {
                    var leaf = (LeafBlock)block;
                    var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "Code" }));
                    var lines = leaf.Lines.Lines.Take(leaf.Lines.Count).Select(l => l.Slice.ToString()).ToList();
                    for (int i = 0; i < lines.Count; i++)
                    {
                        if (i > 0)
                        {
                            p.Append(new Run(new Break()));
                        }

                        p.Append(new Run(new Text(lines[i]) { Space = SpaceProcessingModeValues.Preserve }));
                    }

                    _body.Append(p);
                    break;
                }

                case ThematicBreakBlock:
                    _body.Append(new Paragraph(new ParagraphProperties(
                        new ParagraphBorders(new BottomBorder { Val = BorderValues.Single, Size = 6U, Color = "C8CED6", Space = 1U }))));
                    break;

                case MdTable table:
                    _body.Append(Table(table));
                    _body.Append(new Paragraph()); // Word needs a paragraph between consecutive tables
                    break;

                case ContainerBlock container:
                    foreach (var child in container)
                    {
                        Block(child, listLevel);
                    }

                    break;

                case LeafBlock leaf when leaf.Inline is not null:
                    _body.Append(Paragraph("Normal", leaf.Inline));
                    break;
            }
        }

        private Paragraph Paragraph(string style, ContainerInline? inline, bool? taskMark = null)
        {
            var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = style }));
            if (taskMark is { } done)
            {
                p.Append(TextRun(done ? "☑ " : "☐ ", new Format()));
            }

            if (inline is not null)
            {
                Inlines(p, inline, new Format());
            }

            if (taskMark is not null && p.Descendants<Text>().Skip(1).FirstOrDefault() is { } first)
            {
                first.Text = first.Text.TrimStart(); // "☐ " already ends with a space
            }

            return p;
        }

        private record Format(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, bool Link = false);

        private void Inlines(OpenXmlElement target, ContainerInline container, Format format)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        target.Append(TextRun(literal.Content.ToString(), format));
                        break;
                    case EmphasisInline emphasis:
                    {
                        var f = emphasis.DelimiterChar is '~'
                            ? format with { Strike = true }
                            : emphasis.DelimiterCount >= 2 ? format with { Bold = true } : format with { Italic = true };
                        Inlines(target, emphasis, f);
                        break;
                    }

                    case CodeInline code:
                        target.Append(TextRun(code.Content, format with { Code = true }));
                        break;
                    case LineBreakInline lineBreak:
                        target.Append(lineBreak.IsHard ? new Run(new Break()) : TextRun(" ", format));
                        break;
                    case AutolinkInline auto:
                        target.Append(Link(auto.Url, auto.Url, format));
                        break;
                    case LinkInline { IsImage: true } image:
                        // Images are not embedded (nothing is fetched); their description stays.
                        target.Append(TextRun($"[画像: {Plain(image)}]", format with { Italic = true }));
                        break;
                    case LinkInline link:
                        if (link.Url is { } url && IsWebLink(url))
                        {
                            var hyperlink = Link(url, null, format);
                            Inlines(hyperlink, link, format with { Link = true });
                            target.Append(hyperlink);
                        }
                        else
                        {
                            Inlines(target, link, format);
                        }

                        break;
                    case HtmlEntityInline entity:
                        target.Append(TextRun(entity.Transcoded.ToString(), format));
                        break;
                    case HtmlInline html:
                        target.Append(TextRun(html.Tag, format));
                        break;
                    case TaskList:
                        break; // shown as ☐/☑ at the start of the item
                    case ContainerInline other:
                        Inlines(target, other, format);
                        break;
                }
            }
        }

        private static string Plain(ContainerInline container) =>
            string.Concat(container.Descendants<LiteralInline>().Select(l => l.Content.ToString()));

        private static bool IsWebLink(string url) =>
            url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase);

        private Hyperlink Link(string url, string? text, Format format)
        {
            var relation = main.AddHyperlinkRelationship(new Uri(url, UriKind.Absolute), true);
            var hyperlink = new Hyperlink { Id = relation.Id, History = OnOffValue.FromBoolean(true) };
            if (text is not null)
            {
                hyperlink.Append(TextRun(text, format with { Link = true }));
            }

            return hyperlink;
        }

        private static Run TextRun(string text, Format format)
        {
            var properties = new RunProperties();
            if (format.Link)
            {
                properties.Append(new RunStyle { Val = "Hyperlink" });
            }

            if (format.Code)
            {
                properties.Append(new RunFonts { Ascii = CodeFont, HighAnsi = CodeFont, EastAsia = JapaneseFont });
            }

            if (format.Bold)
            {
                properties.Append(new Bold());
            }

            if (format.Italic)
            {
                properties.Append(new Italic());
            }

            if (format.Strike)
            {
                properties.Append(new Strike());
            }

            if (format.Code)
            {
                properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "F1F3F5" });
            }

            var run = new Run();
            if (properties.HasChildren)
            {
                run.Append(properties);
            }

            run.Append(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            return run;
        }

        private WTable Table(MdTable table)
        {
            // Word requires this order: top, left, bottom, right, inside horizontal, inside vertical.
            var border = new TableBorders(
                new TopBorder { Val = BorderValues.Single, Size = 4U, Color = "B9C1CB" },
                new LeftBorder { Val = BorderValues.Single, Size = 4U, Color = "B9C1CB" },
                new BottomBorder { Val = BorderValues.Single, Size = 4U, Color = "B9C1CB" },
                new RightBorder { Val = BorderValues.Single, Size = 4U, Color = "B9C1CB" },
                new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4U, Color = "B9C1CB" },
                new InsideVerticalBorder { Val = BorderValues.Single, Size = 4U, Color = "B9C1CB" });
            var result = new WTable(new TableProperties(
                new TableStyle { Val = "TableGrid" },
                new TableWidth { Type = TableWidthUnitValues.Pct, Width = "5000" },
                border,
                new TableLayout { Type = TableLayoutValues.Autofit }));

            int columns = table.OfType<MdTableRow>().Select(r => r.Count).DefaultIfEmpty(1).Max();
            var grid = new TableGrid();
            for (int i = 0; i < columns; i++)
            {
                grid.Append(new GridColumn { Width = (9752 / columns).ToString() });
            }

            result.Append(grid);
            foreach (var row in table.OfType<MdTableRow>())
            {
                var wRow = new WRow();
                if (row.IsHeader)
                {
                    wRow.Append(new TableRowProperties(new TableHeader()));
                }

                for (int i = 0; i < columns; i++)
                {
                    var cell = new WCell(new TableCellProperties(new TableCellWidth { Type = TableWidthUnitValues.Auto }));
                    if (row.IsHeader)
                    {
                        cell.TableCellProperties!.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = "EEF2F6" });
                    }

                    var p = new Paragraph(new ParagraphProperties(new ParagraphStyleId { Val = "TableText" }));
                    if (i < row.Count && row[i] is MdTableCell mdCell)
                    {
                        foreach (var paragraph in mdCell.OfType<ParagraphBlock>())
                        {
                            if (paragraph.Inline is not null)
                            {
                                Inlines(p, paragraph.Inline, new Format(Bold: row.IsHeader));
                            }
                        }
                    }

                    cell.Append(p);
                    wRow.Append(cell);
                }

                result.Append(wRow);
            }

            return result;
        }

        /// <summary>A list instance of its own, so each numbered list starts at its own number.</summary>
        private int NewList(bool ordered, string? start)
        {
            int id = _nextNumberId++;
            var instance = new NumberingInstance(new AbstractNumId { Val = ordered ? NumberAbstractId : BulletAbstractId }) { NumberID = id };
            if (ordered)
            {
                instance.Append(new LevelOverride(new StartOverrideNumberingValue { Val = int.TryParse(start, out int s) ? s : 1 }) { LevelIndex = 0 });
            }

            numbering.Append(instance);
            return id;
        }
    }

    // ----- Styles and numbering ----------------------------------------------------------------

    private static void AddStyles(MainDocumentPart main)
    {
        var part = main.AddNewPart<StyleDefinitionsPart>();
        var styles = new Styles(new DocDefaults(
            new RunPropertiesDefault(new RunPropertiesBaseStyle(
                new RunFonts { Ascii = LatinFont, HighAnsi = LatinFont, EastAsia = JapaneseFont, ComplexScript = LatinFont },
                new FontSize { Val = "21" },
                new FontSizeComplexScript { Val = "21" },
                new Languages { Val = "en-US", EastAsia = "ja-JP" })),
            new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                new SpacingBetweenLines { After = "100", Line = "300", LineRule = LineSpacingRuleValues.Auto }))));

        styles.Append(ParagraphStyle("Normal", "Normal", null, isDefault: true));
        styles.Append(ParagraphStyle("Heading1", "heading 1", new StyleRunProperties(new Bold(), new Color { Val = "1B1B1B" }, new FontSize { Val = "34" }),
            spacingBefore: "120", spacingAfter: "160", outline: 0, bottomBorder: true));
        styles.Append(ParagraphStyle("Heading2", "heading 2", new StyleRunProperties(new Bold(), new Color { Val = "22344F" }, new FontSize { Val = "27" }),
            spacingBefore: "280", spacingAfter: "100", outline: 1));
        styles.Append(ParagraphStyle("Heading3", "heading 3", new StyleRunProperties(new Bold(), new FontSize { Val = "23" }),
            spacingBefore: "200", spacingAfter: "80", outline: 2));
        styles.Append(ParagraphStyle("Heading4", "heading 4", new StyleRunProperties(new Bold(), new FontSize { Val = "21" }),
            spacingBefore: "160", spacingAfter: "60", outline: 3));
        styles.Append(ParagraphStyle("ListParagraph", "List Paragraph", null, spacingAfter: "40"));
        styles.Append(ParagraphStyle("TableText", "Table Text", null, spacingAfter: "0"));
        styles.Append(ParagraphStyle("Quote", "Quote", new StyleRunProperties(new Color { Val = "4A4F57" }),
            spacingAfter: "60", indentLeft: "284", leftBorder: true));
        styles.Append(ParagraphStyle("Code", "Code", new StyleRunProperties(
            new RunFonts { Ascii = CodeFont, HighAnsi = CodeFont, EastAsia = JapaneseFont }, new FontSize { Val = "19" }),
            spacingAfter: "120", shading: "F6F8FA"));
        styles.Append(new Style(
            new StyleName { Val = "Hyperlink" },
            new UIPriority { Val = 99 },
            new StyleRunProperties(new Color { Val = "0B57C2" }, new Underline { Val = UnderlineValues.Single }))
        { Type = StyleValues.Character, StyleId = "Hyperlink" });
        styles.Append(new Style(
            new StyleName { Val = "Table Grid" },
            new StyleTableProperties(new TableCellMarginDefault(
                new TopMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                new TableCellLeftMargin { Width = 100, Type = TableWidthValues.Dxa },
                new BottomMargin { Width = "40", Type = TableWidthUnitValues.Dxa },
                new TableCellRightMargin { Width = 100, Type = TableWidthValues.Dxa })))
        { Type = StyleValues.Table, StyleId = "TableGrid" });
        part.Styles = styles;
    }

    private static Style ParagraphStyle(string id, string name, StyleRunProperties? run, bool isDefault = false,
        string? spacingBefore = null, string? spacingAfter = null, int? outline = null, bool bottomBorder = false,
        string? indentLeft = null, bool leftBorder = false, string? shading = null)
    {
        var properties = new StyleParagraphProperties();
        if (outline is not null)
        {
            properties.Append(new KeepNext());
        }

        if (bottomBorder || leftBorder)
        {
            properties.Append(new ParagraphBorders(bottomBorder
                ? new BottomBorder { Val = BorderValues.Single, Size = 8U, Color = "C8CED6", Space = 2U }
                : new LeftBorder { Val = BorderValues.Single, Size = 18U, Color = "D5DBE3", Space = 6U }));
        }

        if (shading is not null)
        {
            properties.Append(new Shading { Val = ShadingPatternValues.Clear, Fill = shading });
        }

        if (spacingBefore is not null || spacingAfter is not null)
        {
            var spacing = new SpacingBetweenLines();
            if (spacingBefore is not null)
            {
                spacing.Before = spacingBefore;
            }

            if (spacingAfter is not null)
            {
                spacing.After = spacingAfter;
            }

            properties.Append(spacing);
        }

        if (indentLeft is not null)
        {
            properties.Append(new Indentation { Left = indentLeft });
        }

        if (outline is not null)
        {
            properties.Append(new OutlineLevel { Val = outline });
        }

        // Child order matters in Word: name, basedOn, next, qFormat, pPr, rPr.
        var style = new Style(new StyleName { Val = name }) { Type = StyleValues.Paragraph, StyleId = id, Default = isDefault ? true : null };
        if (id != "Normal")
        {
            style.Append(new BasedOn { Val = "Normal" }, new NextParagraphStyle { Val = "Normal" });
        }

        style.Append(new PrimaryStyle());
        style.Append(properties);
        if (run is not null)
        {
            style.Append(run);
        }

        return style;
    }

    private static Numbering AddNumbering(MainDocumentPart main)
    {
        var part = main.AddNewPart<NumberingDefinitionsPart>();
        string[] bullets = ["•", "◦", "▪"];
        var bullet = new AbstractNum { AbstractNumberId = BulletAbstractId };
        var number = new AbstractNum { AbstractNumberId = NumberAbstractId };
        for (int level = 0; level < 9; level++)
        {
            int indent = 420 * (level + 1);
            bullet.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = NumberFormatValues.Bullet },
                new LevelText { Val = bullets[level % bullets.Length] },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = indent.ToString(), Hanging = "300" })) { LevelIndex = level });
            number.Append(new Level(
                new StartNumberingValue { Val = 1 },
                new NumberingFormat { Val = level % 3 == 1 ? NumberFormatValues.LowerLetter : level % 3 == 2 ? NumberFormatValues.LowerRoman : NumberFormatValues.Decimal },
                new LevelText { Val = $"%{level + 1}." },
                new LevelJustification { Val = LevelJustificationValues.Left },
                new PreviousParagraphProperties(new Indentation { Left = indent.ToString(), Hanging = "360" })) { LevelIndex = level });
        }

        part.Numbering = new Numbering(bullet, number);
        return part.Numbering;
    }
}
