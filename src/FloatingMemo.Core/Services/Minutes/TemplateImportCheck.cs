using System.Text.Json;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// Reads a template file before it is saved: the template (shown as a preview) or an error that
/// says what is missing and, for JSON in another shape, where the headings seem to be and how the
/// supported format writes them. Nothing is converted automatically: a guess could put the
/// minutes under the wrong headings without anyone noticing.
/// </summary>
public sealed class TemplateImportCheck
{
    public MinutesTemplate? Template { get; private init; }

    public string? Error { get; private init; }

    public List<string> Hints { get; } = [];

    public const string Example = """
        {
          "schemaVersion": 1,
          "name": "テンプレートの名前",
          "sections": [
            { "title": "見出し", "instruction": "書き方", "whenMissing": "記載なし", "kind": "facts" }
          ]
        }
        """;

    private static readonly string[] TitleKeys = ["title", "name", "heading", "label", "見出し", "項目", "名前"];

    public static TemplateImportCheck Inspect(string text, string fileName)
    {
        bool json = Path.GetExtension(fileName).Equals(".json", StringComparison.OrdinalIgnoreCase);
        try
        {
            var template = json ? TemplateLibrary.ParseJson(text) : TemplateLibrary.ParseMarkdown(text, Path.GetFileNameWithoutExtension(fileName));
            return new TemplateImportCheck { Template = TemplateLibrary.Validate(template, "preview") };
        }
        catch (TemplateFormatException ex)
        {
            var check = new TemplateImportCheck { Error = ex.Message };
            if (json)
            {
                Explain(text, check.Hints);
            }

            return check;
        }
    }

    private static void Explain(string text, List<string> hints)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException)
        {
            hints.Add("JSON の書き方（カンマ・引用符・括弧の対応）を確認してください。");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                hints.Add("いちばん外側は { … }（オブジェクト）にしてください。");
                return;
            }

            if (!root.TryGetProperty("schemaVersion", out _))
            {
                hints.Add("必須の \"schemaVersion\": 1 がありません。");
            }

            if (!root.TryGetProperty("sections", out var sections) || sections.ValueKind != JsonValueKind.Array)
            {
                hints.Add("必須の \"sections\"（見出しの配列）がありません。");
                foreach (var property in root.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Array))
                {
                    var titles = property.Value.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.Object)
                        .Select(TitleOf).Where(t => t is not null).Take(6).ToList();
                    if (titles.Count > 0)
                    {
                        hints.Add($"\"{property.Name}\" に見出しらしい項目が {property.Value.GetArrayLength()} 個あります（{string.Join("・", titles)}…）。"
                            + "対応形式では \"sections\" の中に { \"title\": 見出し, \"instruction\": 書き方 } の形で並べます。");
                    }
                }

                return;
            }

            int index = 0;
            foreach (var section in sections.EnumerateArray())
            {
                index++;
                if (section.ValueKind != JsonValueKind.Object)
                {
                    hints.Add($"sections の {index} 番目が {{ … }} ではありません。");
                }
                else if (!section.TryGetProperty("title", out _))
                {
                    string? other = TitleOf(section);
                    hints.Add(other is null
                        ? $"sections の {index} 番目に \"title\"（見出し）がありません。"
                        : $"sections の {index} 番目は \"title\" ではなく別の名前で見出し（{other}）が書かれています。\"title\" にしてください。");
                }
            }
        }
    }

    private static string? TitleOf(JsonElement element)
    {
        foreach (string key in TitleKeys)
        {
            if (element.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } title)
            {
                return title.Length > 20 ? title[..20] : title;
            }
        }

        return null;
    }
}
