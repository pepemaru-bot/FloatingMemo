using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FloatingMemo.Services.Dictionary;

/// <summary>How imported entries meet the dictionary.</summary>
public enum ImportMode
{
    /// <summary>Only terms not in the dictionary are added; existing entries stay as they are.</summary>
    Add,

    /// <summary>New terms are added and existing ones take the imported values (spellings merged).</summary>
    Update,

    /// <summary>The dictionary becomes the imported list (a backup is written first).</summary>
    Replace,
}

/// <summary>What an import would do (shown before anything changes).</summary>
public sealed record ImportPreview(int Added, int Updated, int Unchanged, int Removed, IReadOnlyList<string> DuplicatesInFile, int Skipped);

/// <summary>
/// The user's dictionary as CSV or JSON files: written with every field, so exporting and
/// importing (Replace) gives the same dictionary back. Pure functions; only files the user picks.
/// </summary>
public static class DictionaryTransfer
{
    // CSV columns (also accepted with their Japanese names). Lists are joined with " | ".
    private static readonly string[] Columns = ["term", "reading", "aliases", "misrecognitions", "category", "enabled", "whisperHint", "source", "updated", "id"];
    private static readonly Dictionary<string, string> JapaneseColumns = new()
    {
        ["用語"] = "term", ["読み"] = "reading", ["別名"] = "aliases", ["誤認識"] = "misrecognitions", ["分類"] = "category",
        ["有効"] = "enabled", ["Whisperヒント"] = "whisperHint", ["出典"] = "source", ["更新日時"] = "updated", ["ID"] = "id",
    };
    private const string ListSeparator = " | ";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    // ----- Export ---------------------------------------------------------------------------------

    public static string ExportJson(IEnumerable<TermEntry> entries)
    {
        var list = new JsonArray();
        foreach (var e in entries)
        {
            list.Add(new JsonObject
            {
                ["term"] = e.Term,
                ["reading"] = e.Reading,
                ["aliases"] = new JsonArray(e.Aliases.Select(a => (JsonNode?)a).ToArray()),
                ["misrecognitions"] = new JsonArray(e.Misrecognitions.Select(a => (JsonNode?)a).ToArray()),
                ["category"] = e.Category,
                ["enabled"] = e.Enabled,
                ["whisperHint"] = e.WhisperHint,
                ["source"] = e.Source,
                ["updated"] = e.Updated.ToString("o", CultureInfo.InvariantCulture),
                ["id"] = e.Id,
            });
        }

        return new JsonObject { ["format"] = "floatingmemo-dictionary", ["version"] = 1, ["entries"] = list }.ToJsonString(JsonOptions);
    }

    /// <summary>UTF-8 CSV with a header row (Excel opens it when written with a BOM).</summary>
    public static string ExportCsv(IEnumerable<TermEntry> entries)
    {
        var sb = new StringBuilder(string.Join(",", Columns)).Append("\r\n");
        foreach (var e in entries)
        {
            string[] cells =
            [
                e.Term, e.Reading ?? string.Empty, string.Join(ListSeparator, e.Aliases), string.Join(ListSeparator, e.Misrecognitions), e.Category,
                e.Enabled ? "true" : "false", e.WhisperHint ? "true" : "false", e.Source, e.Updated.ToString("o", CultureInfo.InvariantCulture), e.Id,
            ];
            sb.Append(string.Join(",", cells.Select(Quote))).Append("\r\n");
        }

        return sb.ToString();
    }

    private static string Quote(string cell) =>
        cell.IndexOfAny([',', '"', '\r', '\n']) >= 0 || cell != cell.Trim() ? "\"" + cell.Replace("\"", "\"\"") + "\"" : cell;

    // ----- Parse ----------------------------------------------------------------------------------

    /// <summary>Entries of an exported JSON file (other JSON shapes go through TermDictionary.Import).</summary>
    public static List<TermEntry>? ParseExportedJson(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (root is not JsonObject top || top["format"]?.GetValue<string>() != "floatingmemo-dictionary" || top["entries"] is not JsonArray list)
        {
            return null;
        }

        var entries = new List<TermEntry>();
        foreach (var node in list.OfType<JsonObject>())
        {
            var entry = new TermEntry
            {
                Term = (node["term"]?.GetValue<string>() ?? string.Empty).Trim(),
                Reading = node["reading"]?.GetValue<string>(),
                Aliases = (node["aliases"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? string.Empty).Where(a => a.Length > 0).ToList() ?? [],
                Misrecognitions = (node["misrecognitions"] as JsonArray)?.Select(a => a?.GetValue<string>() ?? string.Empty).Where(a => a.Length > 0).ToList() ?? [],
                Category = node["category"]?.GetValue<string>() ?? "その他",
                Enabled = node["enabled"]?.GetValue<bool>() ?? true,
                WhisperHint = node["whisperHint"]?.GetValue<bool>() ?? false,
                Source = node["source"]?.GetValue<string>() ?? "import",
            };
            if (node["updated"]?.GetValue<string>() is { } updated && DateTime.TryParse(updated, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            {
                entry.Updated = when;
            }

            if (node["id"]?.GetValue<string>() is { Length: > 0 } id)
            {
                entry.Id = id;
            }

            entries.Add(entry);
        }

        return entries;
    }

    /// <summary>Entries of a CSV file with a header row (English or Japanese column names; quoted cells allowed).</summary>
    public static List<TermEntry> ParseCsv(string csv)
    {
        var rows = ReadRows(csv.TrimStart('﻿'));
        if (rows.Count == 0)
        {
            return [];
        }

        var header = rows[0].Select(h => JapaneseColumns.GetValueOrDefault(h.Trim(), h.Trim())).ToList();
        int Col(string name) => header.FindIndex(h => h.Equals(name, StringComparison.OrdinalIgnoreCase));
        int term = Col("term");
        if (term < 0)
        {
            throw new FormatException("CSV の 1 行目に term（または 用語）の列がありません。");
        }

        var entries = new List<TermEntry>();
        foreach (var row in rows.Skip(1))
        {
            string Cell(string name) => Col(name) is var i and >= 0 && i < row.Count ? row[i] : string.Empty;
            List<string> List(string name) => Cell(name).Split('|').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (row.All(c => c.Trim().Length == 0))
            {
                continue;
            }

            var entry = new TermEntry
            {
                Term = Cell("term").Trim(),
                Reading = Cell("reading") is { Length: > 0 } r ? r : null,
                Aliases = List("aliases"),
                Misrecognitions = List("misrecognitions"),
                Category = Cell("category") is { Length: > 0 } c ? c : "その他",
                Enabled = !Cell("enabled").Trim().Equals("false", StringComparison.OrdinalIgnoreCase),
                WhisperHint = Cell("whisperHint").Trim().Equals("true", StringComparison.OrdinalIgnoreCase),
                Source = Cell("source") is { Length: > 0 } s ? s : "import",
            };
            if (DateTime.TryParse(Cell("updated"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when))
            {
                entry.Updated = when;
            }

            if (Cell("id") is { Length: > 0 } id)
            {
                entry.Id = id;
            }

            entries.Add(entry);
        }

        return entries;
    }

    // RFC 4180: quoted cells may hold commas, quotes ("") and line breaks.
    private static List<List<string>> ReadRows(string csv)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < csv.Length; i++)
        {
            char c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { cell.Append(c); }
            }
            else if (c == '"') { quoted = true; }
            else if (c == ',') { row.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\r' || c == '\n')
            {
                if (c == '\r' && i + 1 < csv.Length && csv[i + 1] == '\n') { i++; }
                row.Add(cell.ToString()); cell.Clear();
                rows.Add(row); row = [];
            }
            else { cell.Append(c); }
        }

        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            rows.Add(row);
        }

        return rows;
    }

    // ----- Preview --------------------------------------------------------------------------------

    /// <summary>Same term: the same text after Unicode normalisation (full/half width, spaces).</summary>
    public static string Key(string term) => term.Normalize(NormalizationForm.FormKC).Trim();

    /// <summary>What importing <paramref name="incoming"/> with <paramref name="mode"/> would change.</summary>
    public static ImportPreview Preview(IReadOnlyList<TermEntry> existing, IReadOnlyList<TermEntry> incoming, ImportMode mode)
    {
        var (unique, duplicates, skipped) = Distinct(incoming);
        var current = existing.GroupBy(e => Key(e.Term)).ToDictionary(g => g.Key, g => g.First());
        int added = 0, updated = 0, unchanged = 0;
        foreach (var entry in unique)
        {
            if (!current.TryGetValue(Key(entry.Term), out var old))
            {
                added++;
            }
            else if (mode != ImportMode.Add && !SameContent(old, entry))
            {
                updated++;
            }
            else
            {
                unchanged++;
            }
        }

        int removed = mode == ImportMode.Replace ? current.Keys.Count(k => !unique.Any(u => Key(u.Term) == k)) : 0;
        return new ImportPreview(added, updated, unchanged, removed, duplicates, skipped);
    }

    /// <summary>One entry per term (the first one), the repeated terms, and rows without a term.</summary>
    public static (List<TermEntry> Unique, List<string> Duplicates, int Skipped) Distinct(IReadOnlyList<TermEntry> incoming)
    {
        var seen = new HashSet<string>();
        var unique = new List<TermEntry>();
        var duplicates = new List<string>();
        int skipped = 0;
        foreach (var entry in incoming)
        {
            if (string.IsNullOrWhiteSpace(entry.Term))
            {
                skipped++;
            }
            else if (seen.Add(Key(entry.Term)))
            {
                unique.Add(entry);
            }
            else
            {
                duplicates.Add(entry.Term);
            }
        }

        return (unique, duplicates.Distinct().ToList(), skipped);
    }

    public static bool SameContentForTests(TermEntry a, TermEntry b) => SameContent(a, b);

    internal static bool SameContent(TermEntry a, TermEntry b) =>
        a.Term == b.Term && a.Reading == b.Reading && a.Category == b.Category && a.Enabled == b.Enabled && a.WhisperHint == b.WhisperHint
        && a.Aliases.SequenceEqual(b.Aliases) && a.Misrecognitions.SequenceEqual(b.Misrecognitions);
}
