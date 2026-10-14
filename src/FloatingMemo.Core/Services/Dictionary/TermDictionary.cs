using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Encodings.Web;
using System.Text.Json.Serialization;
using System.Text.Unicode;

namespace FloatingMemo.Services.Dictionary;

/// <summary>
/// One term the user cares about (a school name, an exam word, …): how it is written, how it is
/// read, its abbreviations, and the ways speech recognition tends to get it wrong.
/// </summary>
public sealed class TermEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>The correct spelling (e.g. 「共通テスト」).</summary>
    public string Term { get; set; } = string.Empty;

    /// <summary>Reading in kana (e.g. 「きょうつうてすと」), optional.</summary>
    public string? Reading { get; set; }

    /// <summary>Abbreviations and other correct names (e.g. 「共テ」).</summary>
    public List<string> Aliases { get; set; } = [];

    /// <summary>Known misrecognitions (e.g. 「今日通テスト」). Used only as hints, never replaced blindly.</summary>
    public List<string> Misrecognitions { get; set; } = [];

    /// <summary>Group such as 学校名 / 教育・受験用語, used to switch whole groups on or off.</summary>
    public string Category { get; set; } = "その他";

    /// <summary>Off = kept but not used as a hint.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Also given to Whisper as a recognition hint (keep this to a few frequent words).</summary>
    public bool WhisperHint { get; set; }

    /// <summary>"user", or "import:&lt;file name&gt;".</summary>
    public string Source { get; set; } = "user";

    public DateTime Updated { get; set; } = DateTime.Now;

    /// <summary>Every spelling that points at this term (the term, aliases, misrecognitions).</summary>
    [JsonIgnore]
    public IEnumerable<string> AllForms => new[] { Term }.Concat(Aliases).Concat(Misrecognitions).Where(s => !string.IsNullOrWhiteSpace(s));
}

/// <summary>A word pair the user corrected (or the AI proposed), waiting for the user's approval.</summary>
public sealed class TermCandidate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>What the transcript said (a likely misrecognition).</summary>
    public string Heard { get; set; } = string.Empty;

    /// <summary>What it should have been.</summary>
    public string Correct { get; set; } = string.Empty;

    /// <summary>"ユーザーの修正" or "AI校正".</summary>
    public string Origin { get; set; } = "ユーザーの修正";

    /// <summary>Where it was found (a note title), for the user's reference.</summary>
    public string? Note { get; set; }

    public int Count { get; set; } = 1;

    public DateTime Seen { get; set; } = DateTime.Now;
}

public sealed class DictionaryData
{
    public int Version { get; set; } = 1;

    /// <summary>Master switch: use the dictionary for AI correction and hints at all.</summary>
    public bool UseForCorrection { get; set; } = true;

    public List<TermEntry> Entries { get; set; } = [];

    /// <summary>Categories switched off as a whole (entries keep their own switch too).</summary>
    public List<string> DisabledCategories { get; set; } = [];

    public List<TermCandidate> Candidates { get; set; } = [];

    /// <summary>Pairs the user rejected: never proposed again.</summary>
    public List<string> Rejected { get; set; } = [];
}

[JsonSourceGenerationOptions(WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DictionaryData))]
internal sealed partial class DictionaryJsonContext : JsonSerializerContext
{
}

/// <summary>Result of an import: what was added, merged or skipped.</summary>
public sealed record ImportResult(int Added, int Merged, int Skipped, IReadOnlyList<string> Categories);

/// <summary>
/// The user's own term dictionary, stored locally next to the notes
/// (<c>.floatingmemo\dictionary.json</c>). Nothing is bundled or applied without the user:
/// entries come from the user or from a file the user imports, and they are only hints.
/// </summary>
public sealed class TermDictionary
{
    private readonly object _lock = new();

    // Japanese is written as is (not as \uXXXX escapes), so the file stays readable and editable.
    private static readonly DictionaryJsonContext Json = new(new JsonSerializerOptions
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
    });

    public TermDictionary(string filePath)
    {
        FilePath = filePath;
        Data = Load(filePath);
    }

    public string FilePath { get; }

    public DictionaryData Data { get; private set; }

    public static string PathFor(string metaDirectory) => Path.Combine(metaDirectory, "dictionary.json");

    public IReadOnlyList<TermEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return Data.Entries.ToList();
            }
        }
    }

    /// <summary>Entries that may be used as hints (switched on, category on, master switch on).</summary>
    public IReadOnlyList<TermEntry> ActiveEntries
    {
        get
        {
            lock (_lock)
            {
                return Data.UseForCorrection
                    ? Data.Entries.Where(e => e.Enabled && !Data.DisabledCategories.Contains(e.Category)).ToList()
                    : [];
            }
        }
    }

    /// <summary>
    /// Whisper hint: only terms the user marked for it (at most 30, ~200 characters), or null.
    /// Whisper's prompt is short, and words in it can leak into silent parts, so it stays small.
    /// </summary>
    public string? WhisperHintText()
    {
        var terms = ActiveEntries.Where(e => e.WhisperHint).Select(e => e.Term).Take(30).ToList();
        if (terms.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder("用語: ");
        foreach (string term in terms)
        {
            if (sb.Length + term.Length > 200)
            {
                break;
            }

            sb.Append(term).Append('、');
        }

        sb.Length--;
        return sb.Append('。').ToString();
    }

    public void Upsert(TermEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Term))
        {
            throw new ArgumentException("用語を入力してください。");
        }

        entry.Term = entry.Term.Trim();
        entry.Aliases = Clean(entry.Aliases);
        entry.Misrecognitions = Clean(entry.Misrecognitions);
        entry.Updated = DateTime.Now;
        lock (_lock)
        {
            int index = Data.Entries.FindIndex(e => e.Id == entry.Id);
            if (index >= 0)
            {
                Data.Entries[index] = entry;
            }
            else
            {
                Data.Entries.Add(entry);
            }
        }

        Save();
    }

    public void Remove(string id)
    {
        lock (_lock)
        {
            Data.Entries.RemoveAll(e => e.Id == id);
        }

        Save();
    }

    public void SetCategoryEnabled(string category, bool enabled)
    {
        lock (_lock)
        {
            Data.DisabledCategories.Remove(category);
            if (!enabled)
            {
                Data.DisabledCategories.Add(category);
            }
        }

        Save();
    }

    public void SetUseForCorrection(bool use)
    {
        lock (_lock)
        {
            Data.UseForCorrection = use;
        }

        Save();
    }

    // ----- Candidates (learning from corrections, always with the user's approval) -----------

    /// <summary>Records a correction as a candidate. Known or rejected pairs are not re-proposed.</summary>
    public bool AddCandidate(string heard, string correct, string origin, string? note)
    {
        heard = heard.Trim();
        correct = correct.Trim();
        if (heard.Length == 0 || correct.Length == 0 || heard == correct || heard.Length > 40 || correct.Length > 40)
        {
            return false;
        }

        string key = heard + "→" + correct;
        lock (_lock)
        {
            if (Data.Rejected.Contains(key)
                || Data.Entries.Any(e => e.Term == correct && (e.Misrecognitions.Contains(heard) || e.Aliases.Contains(heard))))
            {
                return false;
            }

            if (Data.Candidates.FirstOrDefault(c => c.Heard == heard && c.Correct == correct) is { } known)
            {
                known.Count++;
                known.Seen = DateTime.Now;
            }
            else
            {
                Data.Candidates.Add(new TermCandidate { Heard = heard, Correct = correct, Origin = origin, Note = note });
            }
        }

        Save();
        return true;
    }

    /// <summary>The user approved: "heard" becomes a misrecognition of the term "correct".</summary>
    public TermEntry Approve(string candidateId, string? category = null)
    {
        TermEntry entry;
        lock (_lock)
        {
            var candidate = Data.Candidates.First(c => c.Id == candidateId);
            Data.Candidates.Remove(candidate);
            entry = Data.Entries.FirstOrDefault(e => e.Term == candidate.Correct)
                ?? new TermEntry { Term = candidate.Correct, Category = category ?? "ユーザー登録", Source = "user" };
            if (!entry.Misrecognitions.Contains(candidate.Heard))
            {
                entry.Misrecognitions.Add(candidate.Heard);
            }

            entry.Updated = DateTime.Now;
            if (!Data.Entries.Contains(entry))
            {
                Data.Entries.Add(entry);
            }
        }

        Save();
        return entry;
    }

    public void Reject(string candidateId)
    {
        lock (_lock)
        {
            if (Data.Candidates.FirstOrDefault(c => c.Id == candidateId) is { } candidate)
            {
                Data.Candidates.Remove(candidate);
                Data.Rejected.Add(candidate.Heard + "→" + candidate.Correct);
            }
        }

        Save();
    }

    // ----- Import ---------------------------------------------------------------------------

    /// <summary>
    /// Imports a JSON term list chosen by the user. Accepted shapes: an array of entries, or an
    /// object whose arrays are entries (array names become categories, e.g. {"学校名": [...]}).
    /// An entry is a string, or an object with a term field (term / word / name / 用語 / 名称 /
    /// 表記) and optional reading, aliases, misrecognitions and category fields. Existing terms
    /// are merged (spellings are added), never duplicated or deleted.
    /// </summary>
    public ImportResult Import(string json, string sourceName)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })
            ?? throw new JsonException("空のファイルです。");
        var found = new List<TermEntry>();
        // A file with an entry list ("entries", "terms", …) is read from that list only, so
        // descriptions, rules or region lists elsewhere in the file are never taken as terms.
        var lists = root is JsonObject top
            ? top.Where(p => WrapperKeys.Contains(p.Key) && p.Value is JsonArray or JsonObject).Select(p => p.Value!).ToList()
            : [];
        foreach (var node in lists.Count > 0 ? lists : [root])
        {
            Collect(node, null, found, $"import:{sourceName}");
        }

        int added = 0, merged = 0, skipped = 0;
        lock (_lock)
        {
            foreach (var entry in found)
            {
                if (string.IsNullOrWhiteSpace(entry.Term))
                {
                    skipped++;
                    continue;
                }

                if (Data.Entries.FirstOrDefault(e => e.Term == entry.Term) is { } existing)
                {
                    existing.Reading ??= entry.Reading;
                    existing.Aliases = Clean(existing.Aliases.Concat(entry.Aliases));
                    existing.Misrecognitions = Clean(existing.Misrecognitions.Concat(entry.Misrecognitions));
                    merged++;
                }
                else
                {
                    entry.Aliases = Clean(entry.Aliases);
                    entry.Misrecognitions = Clean(entry.Misrecognitions);
                    Data.Entries.Add(entry);
                    added++;
                }
            }
        }

        Save();
        return new ImportResult(added, merged, skipped, found.Select(e => e.Category).Distinct().ToList());
    }

    private static readonly string[] TermKeys = ["term", "word", "name", "surface", "text", "用語", "単語", "名称", "表記", "正式名称", "学校名"];
    private static readonly string[] ReadingKeys = ["reading", "yomi", "kana", "furigana", "pronunciation", "読み", "よみ", "読み仮名", "ふりがな"];
    private static readonly string[] AliasKeys = ["aliases", "alias", "abbreviations", "abbreviation", "short", "synonyms", "略称", "別名", "通称"];
    private static readonly string[] MisKeys = ["misrecognitions", "misrecognition", "recognition_candidates", "candidates", "errors", "mistakes", "variants", "誤認識", "誤認識候補", "誤変換", "誤り"];
    private static readonly string[] CategoryKeys = ["category", "type", "group", "分類", "カテゴリ", "カテゴリー", "種別"];
    private static readonly string[] RegionKeys = ["region", "prefecture", "area", "地域", "都道府県"];
    private static readonly string[] PolicyKeys = ["policy", "use", "利用"];
    private static readonly HashSet<string> WrapperKeys = ["entries", "terms", "words", "items", "data", "dictionary", "lexicon", "用語", "辞書"];

    /// <summary>English category codes some files use, shown in Japanese.</summary>
    private static readonly Dictionary<string, string> CategoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["school"] = "学校名",
        ["schools"] = "学校名",
        ["vocabulary"] = "用語",
        ["term"] = "用語",
        ["person"] = "人名",
        ["place"] = "地名",
    };

    /// <summary>"学校名（東京都）": regions become separate groups that can be switched off one by one.</summary>
    private static string CategoryOf(JsonObject obj, string? outer)
    {
        string category = Find(obj, CategoryKeys) ?? outer ?? "インポート";
        category = CategoryNames.GetValueOrDefault(category, category);
        return Find(obj, RegionKeys) is { } region ? $"{category}（{region}）" : category;
    }

    /// <summary>Entries marked "disabled"/"off" (or similar) are imported but switched off.</summary>
    private static bool EnabledByPolicy(JsonObject obj) =>
        Find(obj, PolicyKeys) is not { } policy
        || !(policy.Equals("disabled", StringComparison.OrdinalIgnoreCase) || policy.Equals("off", StringComparison.OrdinalIgnoreCase)
             || policy.Equals("ignore", StringComparison.OrdinalIgnoreCase) || policy is "無効" or "使わない");

    private static void Collect(JsonNode node, string? category, List<TermEntry> found, string source)
    {
        switch (node)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is JsonValue value && value.TryGetValue(out string? word))
                    {
                        found.Add(new TermEntry { Term = word.Trim(), Category = category ?? "インポート", Source = source });
                    }
                    else if (item is JsonObject obj && Find(obj, TermKeys) is { } term)
                    {
                        found.Add(new TermEntry
                        {
                            Term = term.Trim(),
                            Reading = Find(obj, ReadingKeys),
                            Aliases = Strings(obj, AliasKeys),
                            Misrecognitions = Strings(obj, MisKeys),
                            Category = CategoryOf(obj, category),
                            Enabled = EnabledByPolicy(obj),
                            Source = source,
                        });
                    }
                    else if (item is not null)
                    {
                        Collect(item, category, found, source);
                    }
                }

                break;

            case JsonObject obj:
                foreach (var (key, child) in obj)
                {
                    if (child is JsonArray or JsonObject)
                    {
                        // {"学校名": [...]} -> category; wrapper keys like "entries"/"terms" keep the outer one.
                        bool wrapper = WrapperKeys.Contains(key);
                        Collect(child, wrapper ? category : (CategoryKeys.Contains(key) ? category : key), found, source);
                    }
                }

                break;
        }
    }

    private static string? Find(JsonObject obj, string[] keys)
    {
        foreach (var (key, value) in obj)
        {
            if (keys.Contains(key, StringComparer.OrdinalIgnoreCase) && value is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrWhiteSpace(s))
            {
                return s.Trim();
            }
        }

        return null;
    }

    private static List<string> Strings(JsonObject obj, string[] keys)
    {
        var result = new List<string>();
        foreach (var (key, value) in obj)
        {
            if (!keys.Contains(key, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            if (value is JsonArray array)
            {
                result.AddRange(array.OfType<JsonValue>().Select(v => v.TryGetValue(out string? s) ? s : null).OfType<string>());
            }
            else if (value is JsonValue v && v.TryGetValue(out string? s))
            {
                result.AddRange(s.Split(['、', ',', '，', '/', '／', '|'], StringSplitOptions.RemoveEmptyEntries));
            }
        }

        return result;
    }

    private static List<string> Clean(IEnumerable<string> values) =>
        values.Select(v => v.Trim()).Where(v => v.Length > 0).Distinct().ToList();

    // ----- Storage --------------------------------------------------------------------------

    private static DictionaryData Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize(File.ReadAllText(path), Json.DictionaryData) ?? new DictionaryData();
            }
        }
        catch (JsonException)
        {
            // A damaged file is kept aside, never overwritten: the user's dictionary is user data.
            string backup = path + $".damaged-{DateTime.Now:yyyyMMdd-HHmmss}";
            File.Copy(path, backup, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return new DictionaryData();
    }

    /// <summary>Writes via a temp file and rename (a crash never leaves half a dictionary).</summary>
    public void Save()
    {
        string json;
        lock (_lock)
        {
            json = JsonSerializer.Serialize(Data, Json.DictionaryData);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, FilePath, overwrite: true);
    }
}

/// <summary>
/// Picks only the entries related to a given transcript (so the AI is not flooded with hundreds
/// of words, and unrelated terms cannot leak into the text).
/// </summary>
public static class TermMatcher
{
    public sealed record Match(TermEntry Entry, string FoundForm, double Score);

    /// <summary>Entries whose spellings (or similar-looking text) appear in the transcript, best first.</summary>
    public static IReadOnlyList<Match> Related(IEnumerable<TermEntry> entries, string transcript, int max = 40)
    {
        string text = Normalize(transcript);
        string[] textBigrams = Enumerable.Range(0, Math.Max(0, text.Length - 1)).Select(i => text.Substring(i, 2)).ToArray();
        var matches = new List<Match>();
        foreach (var entry in entries)
        {
            Match? best = null;
            foreach (string form in entry.AllForms.Append(entry.Reading ?? string.Empty).Where(f => f.Length >= 2))
            {
                string f = Normalize(form);
                double score = text.Contains(f, StringComparison.Ordinal) ? 1.0 : Fuzzy(textBigrams, f);
                if (score >= 0.67 && (best is null || score > best.Score))
                {
                    best = new Match(entry, form, score);
                }
            }

            if (best is not null)
            {
                matches.Add(best);
            }
        }

        return matches.OrderByDescending(m => m.Score).ThenByDescending(m => m.Entry.Term.Length).Take(max).ToList();
    }

    /// <summary>
    /// How closely some stretch of the text resembles <paramref name="form"/>: the share of the
    /// form's two-character pieces found in a window of the same length (a "Dice" score, 0–1).
    /// The window slides over the text once, so long transcripts stay fast.
    /// </summary>
    private static double Fuzzy(string[] textBigrams, string form)
    {
        int k = form.Length - 1;
        if (form.Length < 3 || textBigrams.Length < k)
        {
            return 0; // short words only match exactly (too many false hits otherwise)
        }

        var target = new HashSet<string>(Enumerable.Range(0, k).Select(i => form.Substring(i, 2)), StringComparer.Ordinal);
        int count = 0;
        for (int i = 0; i < k; i++)
        {
            count += target.Contains(textBigrams[i]) ? 1 : 0;
        }

        int best = count;
        for (int i = k; i < textBigrams.Length; i++)
        {
            count += (target.Contains(textBigrams[i]) ? 1 : 0) - (target.Contains(textBigrams[i - k]) ? 1 : 0);
            best = Math.Max(best, count);
        }

        return Math.Min(1.0, 2.0 * best / (k + target.Count));
    }

    /// <summary>Width/case-insensitive, katakana folded to hiragana, spaces removed.</summary>
    public static string Normalize(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char raw in text.Normalize(NormalizationForm.FormKC))
        {
            char c = raw is >= 'ァ' and <= 'ヶ' ? (char)(raw - 0x60) : char.ToLowerInvariant(raw);
            if (!char.IsWhiteSpace(c) && char.GetUnicodeCategory(c) != UnicodeCategory.OtherPunctuation)
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }
}
