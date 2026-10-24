using System.Text;
using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// The minutes as the reader gets them. The pipeline cites numbered source units in each item
/// (〔B012・B013〕) so every statement can be checked; in the saved note those internal numbers
/// become ordinary Markdown footnotes that quote the source words instead. Only the citation
/// marks change: item text, warnings and the numbered source appendix stay as generated.
/// A citation whose numbers are not all in the appendix is left as it is (nothing is lost).
/// </summary>
public static partial class MinutesPresentation
{
    public const string AppendixHeading = "## 原文（番号付き）";
    private const int QuoteLength = 80;

    public static string ForReader(string markdown)
    {
        int appendix = markdown.IndexOf("\n" + AppendixHeading, StringComparison.Ordinal);
        if (appendix < 0)
        {
            return markdown;
        }

        var units = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in NumberedLine().Matches(markdown, appendix))
        {
            units.TryAdd(m.Groups["id"].Value, m.Groups["text"].Value.Trim());
        }

        // The separator line before the appendix belongs to it.
        int split = markdown.LastIndexOf("\n---\n", appendix, StringComparison.Ordinal) is int rule and >= 0 && appendix - rule <= 8 ? rule : appendix;
        string body = markdown[..split], rest = markdown[split..];

        var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
        var notes = new List<string>();
        body = Citation().Replace(body, m =>
        {
            var ids = m.Groups["ids"].Value.Split('・').Select(id => id.Trim()).ToList();
            if (!ids.All(units.ContainsKey))
            {
                return m.Value;
            }

            string key = string.Join("・", ids);
            if (!numbers.TryGetValue(key, out int n))
            {
                n = numbers[key] = notes.Count + 1;
                notes.Add($"[^{n}]: 原文: " + string.Join(" ／ ", ids.Select(id => $"「{Short(units[id])}」")));
            }

            return $"[^{n}]";
        });

        if (notes.Count == 0)
        {
            return markdown;
        }

        var sb = new StringBuilder(body.TrimEnd('\n')).Append("\n\n");
        foreach (string note in notes)
        {
            sb.Append(note).Append('\n');
        }

        return sb.Append(rest).ToString();
    }

    private static string Short(string text) =>
        (text.Length <= QuoteLength ? text : text[..QuoteLength] + "…").Replace("[", "［", StringComparison.Ordinal).Replace("]", "］", StringComparison.Ordinal);

    [GeneratedRegex(@" ?〔(?<ids>[AB]\d{3,}(・[AB]\d{3,})*)〕")]
    private static partial Regex Citation();

    [GeneratedRegex(@"^- (?<id>[AB]\d{3,}) (?<text>.*)$", RegexOptions.Multiline)]
    private static partial Regex NumberedLine();
}
