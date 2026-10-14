using System.Text;

namespace FloatingMemo.Services.Notes;

/// <summary>
/// Turns user-typed note titles into safe Windows file names. The title (file name without
/// ".md") is also what an Obsidian-style [[wiki link]] will refer to.
/// </summary>
public static class NoteNames
{
    public const string Extension = ".md";
    public const int MaxTitleLength = 120;
    public const string Untitled = "無題";

    // Characters Windows forbids in file names, plus '#', '^', '[', ']' and '|' which
    // Obsidian treats specially inside [[links]].
    private static readonly char[] Forbidden = ['\\', '/', ':', '*', '?', '"', '<', '>', '|', '#', '^', '[', ']'];

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>Makes a title safe to use as a file name (never empty, never reserved).</summary>
    public static string Sanitize(string? title)
    {
        var sb = new StringBuilder();
        foreach (char c in (title ?? string.Empty).Trim())
        {
            sb.Append(char.IsControl(c) || Array.IndexOf(Forbidden, c) >= 0 ? '_' : c);
        }

        // Windows silently drops trailing dots/spaces, which would break round-tripping.
        string result = sb.ToString().Trim().TrimEnd('.', ' ');
        if (result.Length > MaxTitleLength)
        {
            result = result[..MaxTitleLength].TrimEnd('.', ' ');
        }

        if (result.Length == 0 || result.All(c => c == '_'))
        {
            result = Untitled;
        }

        return Reserved.Contains(result) ? result + "_" : result;
    }

    /// <summary>Default title for a new note, e.g. "2026-10-10 0930".</summary>
    public static string DefaultTitle(DateTime now) => now.ToString("yyyy-MM-dd HHmm");

    /// <summary>
    /// Returns a title that is free in <paramref name="directory"/> and not in
    /// <paramref name="taken"/>: "会議メモ", "会議メモ 2", "会議メモ 3", …
    /// </summary>
    public static string MakeUnique(string directory, string title, ISet<string>? taken = null)
    {
        string candidate = title;
        for (int n = 2; IsTaken(candidate); n++)
        {
            candidate = $"{title} {n}";
        }

        return candidate;

        bool IsTaken(string t) =>
            File.Exists(Path.Combine(directory, t + Extension)) || (taken?.Contains(t) ?? false);
    }
}
