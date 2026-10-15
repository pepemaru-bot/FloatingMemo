using System.Net;
using Markdig;
using FloatingMemo.Services.Minutes;

namespace FloatingMemo.Services.Export;

public enum ExportFormat
{
    Pdf,
    Docx,
}

public enum ExportScope
{
    /// <summary>Everything in the note.</summary>
    WholeNote,

    /// <summary>Only the minutes sections of an AI minutes note (no correction log / corrected transcript).</summary>
    MinutesOnly,
}

/// <summary>
/// What goes into an exported file. Exporting only reads the note: the Markdown is never changed.
/// </summary>
public static class ExportContent
{
    /// <summary>
    /// The Markdown for the chosen scope. For "minutes only", the appendix that follows the
    /// minutes (correction log and corrected transcript, after a "---" line) is left out.
    /// </summary>
    public static string Select(string markdown, ExportScope scope)
    {
        string text = markdown.Replace("\r\n", "\n");
        if (scope == ExportScope.WholeNote)
        {
            return text;
        }

        int appendix = FloatingMemo.Services.Minutes.MinutesValidator.AppendixStart(text);
        return (appendix >= 0 ? text[..appendix] : text).TrimEnd() + "\n";
    }

    /// <summary>A file name Windows accepts, based on the note title.</summary>
    public static string SuggestedFileName(string title, ExportScope scope)
    {
        string name = scope == ExportScope.MinutesOnly && !title.Contains(MinutesNotes.Suffix, StringComparison.Ordinal)
            ? title + MinutesNotes.Suffix
            : title;
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        return name.Trim().Length == 0 ? "メモ" : name.Trim();
    }

    /// <summary>The Markdown dialect used for export (GitHub-style tables, task lists, autolinks; no raw HTML).</summary>
    public static MarkdownPipeline Pipeline { get; } = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseAutoLinks()
        .UseEmphasisExtras()
        .DisableHtml() // raw HTML in a note is shown as text, never executed or loaded
        .Build();
}

/// <summary>
/// Markdown → a self-contained HTML page laid out for A4 printing (used for PDF). Nothing is
/// loaded from outside: the page carries its own styles and a strict content security policy.
/// </summary>
public static class HtmlExport
{
    public static string ToHtml(string markdown, string title)
    {
        string body = Markdown.ToHtml(markdown, ExportContent.Pipeline);
        return $$"""
            <!DOCTYPE html>
            <html lang="ja">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; img-src data:">
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
            @page { size: A4; } /* margins come from the print settings */
            html { -webkit-print-color-adjust: exact; print-color-adjust: exact; }
            body { font-family: "Yu Gothic UI", "Yu Gothic", "Meiryo", "Segoe UI", sans-serif; font-size: 10.5pt; line-height: 1.75; color: #1b1b1b; margin: 0; }
            h1 { font-size: 17pt; line-height: 1.35; margin: 0 0 10pt; padding-bottom: 5pt; border-bottom: 1.2pt solid #c8ced6; }
            h2 { font-size: 13.5pt; line-height: 1.4; margin: 16pt 0 6pt; padding-left: 7pt; border-left: 3pt solid #8aa4c4; }
            h3 { font-size: 11.5pt; margin: 12pt 0 4pt; }
            h4, h5, h6 { font-size: 10.5pt; margin: 10pt 0 3pt; }
            h1, h2, h3, h4 { break-after: avoid; }
            p { margin: 0 0 6pt; }
            ul, ol { margin: 0 0 6pt; padding-left: 18pt; }
            li { margin: 1pt 0; }
            li.task-list-item { list-style: none; margin-left: -16pt; }
            li.task-list-item input { margin: 0 5pt 0 0; vertical-align: -1pt; }
            blockquote { margin: 6pt 0; padding: 4pt 10pt; border-left: 3pt solid #d5dbe3; color: #4a4f57; background: #f6f8fa; }
            blockquote p { margin: 2pt 0; }
            code { font-family: "BIZ UDGothic", "Consolas", "MS Gothic", monospace; font-size: 9.5pt; background: #f1f3f5; padding: 0 2pt; border-radius: 2pt; }
            pre { background: #f6f8fa; border: 0.6pt solid #dde2e8; border-radius: 3pt; padding: 6pt 8pt; white-space: pre-wrap; word-break: break-all; }
            pre code { background: none; padding: 0; }
            table { border-collapse: collapse; width: 100%; margin: 6pt 0 10pt; break-inside: auto; }
            tr { break-inside: avoid; }
            th, td { border: 0.6pt solid #b9c1cb; padding: 3pt 6pt; vertical-align: top; text-align: left; }
            th { background: #eef2f6; font-weight: 600; }
            a { color: #0b57c2; text-decoration: underline; word-break: break-all; }
            hr { border: none; border-top: 0.8pt solid #c8ced6; margin: 12pt 0; }
            del { color: #6b7280; }
            </style>
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }
}

/// <summary>
/// Writes an exported file without ever leaving a half-written result: the content goes to a
/// temporary file next to the destination, which then replaces it in one step. If anything
/// fails, the temporary file is removed and the destination stays as it was. Notes are never
/// touched by an export.
/// </summary>
public static class SafeFileWriter
{
    public static void Write(string destination, Action<Stream> write)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        string temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            // Read-write: the .docx writer (a ZIP package) reads back what it wrote.
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite))
            {
                write(stream);
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    public static async Task WriteAsync(string destination, Func<string, Task> writeToPath)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(destination))!;
        string temp = Path.Combine(directory, $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await writeToPath(temp).ConfigureAwait(false);
            if (!File.Exists(temp) || new FileInfo(temp).Length == 0)
            {
                throw new IOException("書き出したファイルが空でした。");
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
