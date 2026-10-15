using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// One numbered piece of the source: a line of the person's notes (A001…) or a sentence of the
/// transcript (B001…). The numbers are given here, never by the model, so every fact can be
/// traced back and a made-up reference is detected.
/// </summary>
public sealed record SourceUnit(string Id, string Text, bool FromNotes)
{
    /// <summary>Greetings, audio checks, fillers: kept in the source, not expected in the minutes.</summary>
    public bool IsNoise => SourceUnits.IsNoise(Text);
}

/// <summary>Splits a note into numbered units (notes: by line, transcript: by sentence).</summary>
public static partial class SourceUnits
{
    [GeneratedRegex(@"(?<=[。！？!?])")]
    private static partial Regex SentenceEnd();

    public static List<SourceUnit> From(NoteSections note)
    {
        var units = new List<SourceUnit>();
        int a = 0, b = 0;
        foreach (string line in note.HumanNotes.Replace("\r\n", "\n").Split('\n'))
        {
            string text = line.Trim();
            if (text.Length > 0)
            {
                units.Add(new SourceUnit($"A{++a:000}", text, true));
            }
        }

        foreach (string block in note.Transcripts)
        {
            foreach (string line in block.Replace("\r\n", "\n").Split('\n'))
            {
                foreach (string sentence in SentenceEnd().Split(line))
                {
                    string text = sentence.Trim();
                    if (text.Length > 0)
                    {
                        units.Add(new SourceUnit($"B{++b:000}", text, false));
                    }
                }
            }
        }

        return units;
    }

    // Short utterances made only of fillers, greetings and audio/screen checks.
    [GeneratedRegex("^(えーと|えっと|あの|その|はい|ええ|うん|そうですね|なるほど|ありがとうございます|お疲れさまでした|よろしくお願いします|すみません|ちょっと待ってください|聞こえていますか|見えていますか|大丈夫です|画面の共有を止めますね|もう一度お願いします|始めます|始めましょう|終わります|以上です|以上で終わります|[、。,.!！?？\\s])+$")]
    private static partial Regex Filler();

    public static bool IsNoise(string text) => text.Length <= 40 && Filler().IsMatch(text);
}

/// <summary>What kind of statement a fact is.</summary>
public static class FactKind
{
    public const string Info = "情報";
    public const string Opinion = "意見";
    public const string Question = "質問";
    public const string Proposal = "提案";
    public const string Decision = "決定";
    public const string Task = "作業";
    public const string Open = "未決";

    public static readonly string[] All = [Info, Opinion, Question, Proposal, Decision, Task, Open];

    public static string Normalize(string? kind) => kind?.Trim() switch
    {
        null or "" => Info,
        var k when All.Contains(k) => k,
        "ToDo" or "TODO" or "todo" or "タスク" or "宿題" => Task,
        "決定事項" or "合意" => Decision,
        "未決事項" or "未定" or "保留" or "課題" => Open,
        "共有" or "事実" or "報告" => Info,
        // A mixed answer such as "情報・意見": the first known kind named in it.
        var k => All.Concat(["ToDo", "タスク"]).Select(name => (name, at: k.IndexOf(name, StringComparison.Ordinal)))
            .Where(x => x.at >= 0).OrderBy(x => x.at).Select(x => x.name is "ToDo" or "タスク" ? Task : x.name).FirstOrDefault() ?? Info,
    };
}

/// <summary>A fact taken from the source, with the units that back it.</summary>
public sealed record Fact(string Kind, string Topic, string Text, IReadOnlyList<string> Sources, string Who, string Due)
{
    /// <summary>Notes only, transcript only, or both (backed by what was written and what was said).</summary>
    public string Origin(IReadOnlyDictionary<string, SourceUnit> units)
    {
        bool notes = Sources.Any(s => units.TryGetValue(s, out var u) && u.FromNotes);
        bool said = Sources.Any(s => units.TryGetValue(s, out var u) && !u.FromNotes);
        return notes && said ? "両方" : notes ? "メモ" : said ? "発言" : "不明";
    }

    /// <summary>The topic (merged) this fact is listed under.</summary>
    public string Group { get; init; } = Topic;

    /// <summary>Problems the checks found (shown as ⚠ in the minutes).</summary>
    public IReadOnlyList<string> Issues { get; init; } = [];
}

/// <summary>Instructions for the extraction step (Japanese, because the meetings are Japanese).</summary>
public static class FactPrompt
{
    public const int MaxAnswerTokens = 2000;

    public static string System { get; } = $$"""
        あなたは会議の記録係です。番号付きの原文から、議事録に必要な事実を漏れなく抜き出してください。
        規則:
        1. 1行に1件、JSON で書く: {"type":"種類","topic":"話題","text":"内容","src":["B001"],"who":"担当","due":"期限"}
        2. type は次のどれか: {{string.Join("・", FactKind.All)}}
           決定 = 「決まりました」「ことになりました」「合意」など、決まったと明言されたことだけ。
           作業 = だれかが行うことになった作業。提案・意見・質問を決定にしない。決まっていないことは 未決。
        3. topic は話題の短い名前（原文の言葉で、10文字程度）。同じ話題には同じ名前を使う。
        4. text は原文の内容を自然な日本語で短く書く。話題名がなくても意味が分かるように、何についてかを含める（例: 「資料の準備は田中さんが行う」）。
           数字・日付・名前・理由・条件・否定は省略しない。原文にない情報を足さない。
        5. src には根拠の原文番号を必ず書く（A は参加者のメモ、B は会議の発言）。番号は原文にあるものだけ。
        6. who と due は原文で明言されたときだけ書く。なければ空文字 "" にする。
        7. 【文脈】の行は前後関係の参考で、そこから事実を抜き出さない。
        8. あいさつ・相づち・音声や画面の確認は抜き出さない。
        9. JSON の行以外は何も書かない。
        """;

    public static string User(IReadOnlyList<SourceUnit> context, IReadOnlyList<SourceUnit> units, int part, int parts)
    {
        var sb = new StringBuilder();
        if (parts > 1)
        {
            sb.Append($"これは原文全体の {part + 1}/{parts} 番目の部分です。\n");
        }

        sb.Append("<原文>\n");
        foreach (var unit in context)
        {
            sb.Append("【文脈】").Append(unit.Id).Append(' ').Append(unit.Text).Append('\n');
        }

        // Fillers keep their numbers but are not sent: shorter input, the model's attention on content.
        foreach (var unit in units.Where(u => !u.IsNoise))
        {
            sb.Append(unit.Id).Append(' ').Append(unit.Text).Append('\n');
        }

        sb.Append("</原文>\n/no_think");
        return sb.ToString();
    }
}

/// <summary>
/// The fact-first pipeline's mechanical parts: token-aware grouping of units, parsing and
/// checking the model's facts, merging them, and writing the minutes from them.
/// </summary>
public static partial class FactMinutes
{
    /// <summary>
    /// Qwen3 uses about 0.55 tokens per Japanese character (measured with its tokenizer on
    /// meeting text); 0.75 leaves room for latin text and numbers.
    /// </summary>
    public const double TokensPerCharacter = 0.75;

    public static int EstimateTokens(string text) => (int)Math.Ceiling(text.Length * TokensPerCharacter);

    /// <summary>
    /// Groups units into parts whose prompt fits: system + context + units + answer budget +
    /// safety margin ≤ context size. Notes (A) go with the first part. A unit is never split.
    /// </summary>
    /// <summary>
    /// Content per part. Smaller than the context allows: small models list facts sparsely from
    /// long input (measured: 2 of 10 topics missed with ~3,500-token parts).
    /// </summary>
    public const int ContentTokensPerPart = 1500;

    public static List<List<SourceUnit>> Group(IReadOnlyList<SourceUnit> units, int contextTokens = 8192, int margin = 512, int contentTokens = ContentTokensPerPart)
    {
        int fixedCost = EstimateTokens(FactPrompt.System) + FactPrompt.MaxAnswerTokens + margin + 100;
        int budget = Math.Min(contentTokens, Math.Max(400, contextTokens - fixedCost - 200)); // 200: room for context lines
        var parts = new List<List<SourceUnit>>();
        var current = new List<SourceUnit>();
        int used = 0;
        foreach (var unit in units)
        {
            int cost = unit.IsNoise ? 0 : EstimateTokens(unit.Text) + 3; // fillers are not sent
            if (current.Count > 0 && used + cost > budget)
            {
                parts.Add(current);
                current = [];
                used = 0;
            }

            current.Add(unit);
            used += cost;
        }

        if (current.Count > 0)
        {
            parts.Add(current);
        }

        return parts;
    }

    /// <summary>The last units of the previous part, shown as context only.</summary>
    public static List<SourceUnit> ContextBefore(List<List<SourceUnit>> parts, int part, int count = 3) =>
        part == 0 ? [] : parts[part - 1].Where(u => !u.FromNotes).TakeLast(count).ToList();

    /// <summary>
    /// Reads the model's lines. Format (JSON validity) and content (references, names, numbers)
    /// are checked separately: a malformed line is counted, not guessed at.
    /// </summary>
    public static List<Fact> Parse(string answer, out int malformed)
    {
        var facts = new List<Fact>();
        malformed = 0;
        foreach (string candidate in Objects(answer))
        {
            try
            {
                using var doc = JsonDocument.Parse(candidate);
                var root = doc.RootElement;
                string text = Str(root, "text");
                if (text.Length == 0)
                {
                    malformed++;
                    continue;
                }

                var sources = new List<string>();
                if (root.TryGetProperty("src", out var src))
                {
                    if (src.ValueKind == JsonValueKind.Array)
                    {
                        sources.AddRange(src.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!.Trim()));
                    }
                    else if (src.ValueKind == JsonValueKind.String)
                    {
                        sources.AddRange(src.GetString()!.Split([',', '、', ' '], StringSplitOptions.RemoveEmptyEntries));
                    }
                }

                facts.Add(new Fact(FactKind.Normalize(Str(root, "type")), Str(root, "topic"), text, sources, Str(root, "who"), Str(root, "due")));
            }
            catch (JsonException)
            {
                malformed++;
            }
        }

        return facts;
    }

    /// <summary>
    /// Every {...} in the answer, also when a model writes one object over several lines or
    /// inside a ```json block. A "{" that is never closed (an answer cut off) is returned as is,
    /// so it is counted as malformed instead of being silently ignored.
    /// </summary>
    private static IEnumerable<string> Objects(string answer)
    {
        int depth = 0, start = -1;
        bool inString = false, escaped = false;
        for (int i = 0; i < answer.Length; i++)
        {
            char c = answer[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"' && depth > 0)
            {
                inString = true;
            }
            else if (c == '{')
            {
                if (depth++ == 0)
                {
                    start = i;
                }
            }
            else if (c == '}' && depth > 0 && --depth == 0)
            {
                yield return answer[start..(i + 1)];
            }
        }

        if (depth > 0)
        {
            yield return answer[start..];
        }
    }

    private static string Str(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : string.Empty;

    [GeneratedRegex("決まり|決定|ことになり|ことにな|合意|承認|確定|にします|でいきます|でいく")]
    private static partial Regex DecisionCue();

    [GeneratedRegex("まだ決まって|決まっていません|未定|検討しましょう|検討します|次回までに|保留|今日は決め|てから決め|あとで決め|後で決め|決めましょう")]
    private static partial Regex OpenCue();

    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"(?<who>[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}ァ-ヶA-Za-z]{1,8}?)(さん|氏|くん|君)が")]
    private static partial Regex TaskOwner();

    [GeneratedRegex(@"(?<due>(今週|今月|今日|本日|年内|月内|週内)中|[^、。がはをも]{1,12}?(?=までに|まで))")]
    private static partial Regex TaskDue();

    /// <summary>
    /// Content checks against the cited units: references must exist (and not be context-only
    /// units of another part), assignees and deadlines must be in them, numbers must be in
    /// them, and a decision must be said to be decided.
    /// </summary>
    public static Fact Check(Fact fact, IReadOnlyDictionary<string, SourceUnit> units, ISet<string> allowed)
    {
        var valid = fact.Sources.Where(s => allowed.Contains(s)).Select(s => Recite(fact.Text, s, units, allowed)).Distinct().ToList();
        var issues = new List<string>();
        if (valid.Count == 0)
        {
            issues.Add(fact.Sources.Count == 0 ? "根拠の番号なし" : "根拠の番号が原文にない");
        }

        string cited = string.Concat(valid.Select(s => units[s].Text));
        string citedNorm = MinutesValidator.Normalize(cited);
        string who = fact.Who, due = fact.Due;
        if (who.Length > 0 && who != MinutesValidator.Unconfirmed && !citedNorm.Contains(MinutesValidator.Normalize(who.Replace("さん", string.Empty)), StringComparison.Ordinal))
        {
            issues.Add("担当はAIの推測のため除去（根拠の原文にない）"); // the guessed name is not repeated
            who = string.Empty;
        }

        if (due.Length > 0 && due != MinutesValidator.Unconfirmed && !citedNorm.Contains(MinutesValidator.Normalize(due), StringComparison.Ordinal)
            && !Numerals.Arabic(citedNorm).Contains(Numerals.Arabic(MinutesValidator.Normalize(due)), StringComparison.Ordinal))
        {
            issues.Add("期限はAIの推測のため除去（根拠の原文にない）");
            due = string.Empty;
        }

        // Numbers (dates, amounts, counts) must be in the cited units, in either way of writing.
        string citedNumbers = Numerals.Arabic(cited);
        var invented = Digits().Matches(Numerals.Arabic(fact.Text)).Select(m => m.Value)
            .Where(n => !Digits().Matches(citedNumbers).Any(c => c.Value == n)).Distinct().ToList();
        if (valid.Count > 0 && invented.Count > 0)
        {
            issues.Add($"数字「{string.Join("、", invented)}」が根拠の原文にない");
        }

        string kind = fact.Kind;
        if (kind == FactKind.Decision && valid.Count > 0)
        {
            if (OpenCue().IsMatch(cited) && !DecisionCue().IsMatch(cited))
            {
                kind = FactKind.Open; // said to be undecided: not a decision
            }
            else if (!DecisionCue().IsMatch(cited))
            {
                issues.Add("決定と明言された根拠がない");
            }
        }

        // "森さんが来週の水曜日までに作ることになりました" is a task, whatever kind the model chose:
        // assignee and deadline are taken from the cited words themselves.
        if (kind is not FactKind.Task and not FactKind.Open and not FactKind.Question && valid.Count > 0
            && TaskOwner().Match(cited) is { Success: true } owner && TaskDue().Match(cited) is { Success: true } deadline)
        {
            kind = FactKind.Task;
            who = who.Length > 0 ? who : owner.Groups["who"].Value;
            due = due.Length > 0 ? due : deadline.Groups["due"].Value.Trim();
            issues.RemoveAll(i => i.StartsWith("決定と明言", StringComparison.Ordinal));
        }

        return fact with { Sources = valid, Kind = kind, Who = who, Due = due, Issues = issues };
    }

    /// <summary>
    /// Merges the facts of all parts: topics with the same or a similar name become one; a fact
    /// that says the same thing as another of the same kind is kept once (with both sources).
    /// Facts that differ in numbers or negation are both kept.
    /// </summary>
    public static List<(string Topic, List<Fact> Facts)> Merge(IEnumerable<Fact> facts)
    {
        var topics = new List<(string Topic, List<Fact> Facts)>();
        foreach (var fact in facts)
        {
            string name = fact.Topic.Length > 0 ? fact.Topic : "その他";
            int at = topics.FindIndex(t => SameTopic(t.Topic, name));
            if (at < 0 && topics.FindIndex(t => SharedSubject(t.Topic, name) is not null) is var related and >= 0)
            {
                // "アンケートの結果" and "アンケートの満足度": one topic, named by what they share.
                at = related;
                string subject = SharedSubject(topics[at].Topic, name)!;
                topics[at] = (subject, topics[at].Facts);
            }

            if (at < 0)
            {
                topics.Add((name, []));
                at = topics.Count - 1;
            }

            var list = topics[at].Facts;
            // The same statement twice: once, with the more specific kind ("情報" gives way).
            int twin = list.FindIndex(f => (f.Kind == fact.Kind || f.Kind == FactKind.Info || fact.Kind == FactKind.Info) && SameStatement(f.Text, fact.Text));
            if (twin >= 0)
            {
                var kept = list[twin];
                list[twin] = kept with
                {
                    Kind = kept.Kind == FactKind.Info ? fact.Kind : kept.Kind,
                    Sources = kept.Sources.Concat(fact.Sources).Distinct().ToList(),
                    Issues = kept.Issues.Concat(fact.Issues).Distinct().ToList(),
                };
                continue;
            }

            list.Add(fact with { Group = topics[at].Topic });
        }

        return topics;
    }

    /// <summary>
    /// A cited number whose sentence is a filler or says something else (measured: the model
    /// sometimes cites a neighbouring number) is moved to the sentence of the same part that
    /// matches the fact's words best — only if that match is clear; otherwise the citation stays
    /// and the later checks flag it.
    /// </summary>
    internal static string Recite(string factText, string cited, IReadOnlyDictionary<string, SourceUnit> units, ISet<string> allowed)
    {
        string fact = Numerals.Arabic(MinutesValidator.Normalize(factText));
        double Score(string id) => MinutesQuality.Similarity(fact, Numerals.Arabic(MinutesValidator.Normalize(units[id].Text)));
        double current = units[cited].IsNoise ? 0 : Score(cited);
        if (current >= 0.4)
        {
            return cited;
        }

        var best = allowed.Where(id => units.ContainsKey(id) && !units[id].IsNoise).Select(id => (id, score: Score(id)))
            .OrderByDescending(x => x.score).FirstOrDefault();
        return best.id is not null && best.score >= 0.5 && best.score > current + 0.2 ? best.id : cited;
    }

    /// <summary>"社内研修の内容" and "社内研修の日程" → "社内研修" (what comes before "の", at least 2 characters).</summary>
    private static string? SharedSubject(string a, string b)
    {
        int x = a.IndexOf('の'), y = b.IndexOf('の');
        string first = x >= 2 ? a[..x] : a, second = y >= 2 ? b[..y] : b;
        return first.Length >= 2 && first == second && (x >= 2 || y >= 2) ? first : null;
    }

    private static bool SameTopic(string a, string b)
    {
        string x = MinutesValidator.Normalize(a), y = MinutesValidator.Normalize(b);
        return x == y || (Math.Min(x.Length, y.Length) >= 2 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)))
            || MinutesQuality.Similarity(x, y) >= 0.75;
    }

    [GeneratedRegex("ない|ません|ず|不要|しない")]
    private static partial Regex Negation();

    private static bool SameStatement(string a, string b)
    {
        // 十一月二十日 and 11月20日 are the same words.
        string x = Numerals.Arabic(MinutesValidator.Normalize(a)), y = Numerals.Arabic(MinutesValidator.Normalize(b));
        if (MinutesQuality.Similarity(x, y) < 0.85)
        {
            return false;
        }

        // Same words but other numbers or the opposite: two statements, keep both.
        var nx = Digits().Matches(Numerals.Arabic(x)).Select(m => m.Value).ToHashSet();
        var ny = Digits().Matches(Numerals.Arabic(y)).Select(m => m.Value).ToHashSet();
        return nx.SetEquals(ny) && Negation().IsMatch(x) == Negation().IsMatch(y);
    }

    /// <summary>
    /// Transcript units that no fact cites and that are not mere fillers: what the minutes may
    /// have left out. Consecutive ones are reported as ranges (B012–B015).
    /// </summary>
    public static List<string> Uncited(IReadOnlyList<SourceUnit> units, IEnumerable<Fact> facts)
    {
        var cited = facts.SelectMany(f => f.Sources).ToHashSet();
        var ranges = new List<string>();
        string? start = null, end = null;
        foreach (var unit in units.Where(u => !u.FromNotes))
        {
            bool missing = !cited.Contains(unit.Id) && !unit.IsNoise;
            if (missing)
            {
                start ??= unit.Id;
                end = unit.Id;
                continue;
            }

            Close();
        }

        Close();
        return ranges;

        void Close()
        {
            if (start is not null)
            {
                ranges.Add(start == end ? start : $"{start}–{end}");
            }

            start = end = null;
        }
    }

    private static readonly Dictionary<string, string> Marks = new()
    {
        [FactKind.Decision] = "【決定】",
        [FactKind.Open] = "【未決】",
        [FactKind.Proposal] = "【提案】",
        [FactKind.Question] = "【質問】",
        [FactKind.Opinion] = "【意見】",
    };

    /// <summary>
    /// The minutes body, written from the facts only: one heading per topic in the order the
    /// topics came up, tasks as checkboxes, the source numbers after each item, and what the
    /// checks found. Nothing is added that is not a fact.
    /// </summary>
    public static string Render(IReadOnlyList<(string Topic, List<Fact> Facts)> topics, IReadOnlyDictionary<string, SourceUnit> units,
        IReadOnlyList<string> overview, List<string> warnings)
    {
        var sb = new StringBuilder();
        sb.Append("## 概要\n");
        if (overview.Count == 0)
        {
            sb.Append("- 話題: ").Append(string.Join("、", topics.Select(t => t.Topic))).Append('\n');
        }

        foreach (string line in overview)
        {
            sb.Append("- ").Append(line).Append('\n');
        }

        foreach (var (topic, facts) in topics)
        {
            sb.Append("\n## ").Append(topic).Append('\n');
            foreach (var fact in facts)
            {
                string refs = fact.Sources.Count == 0 ? string.Empty : $"〔{string.Join("・", fact.Sources)}〕";
                string origin = fact.Origin(units) switch { "メモ" => "（メモより）", "両方" => "（メモ・発言）", _ => string.Empty };
                string warn = fact.Issues.Count == 0 ? string.Empty : $" ⚠要確認（{string.Join("／", fact.Issues)}）";
                if (fact.Kind == FactKind.Task)
                {
                    string who = fact.Who.Length > 0 ? fact.Who : MinutesValidator.Unconfirmed;
                    string due = fact.Due.Length > 0 ? fact.Due : MinutesValidator.Unconfirmed;
                    sb.Append($"- [ ] {fact.Text} — 担当: {who} / 期限: {due} {refs}{origin}{warn}\n");
                }
                else
                {
                    sb.Append($"- {Marks.GetValueOrDefault(fact.Kind, string.Empty)}{fact.Text} {refs}{origin}{warn}\n");
                }

                if (fact.Issues.Count > 0)
                {
                    warnings.Add($"{topic}: {string.Join("／", fact.Issues)}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>The numbered source, so every 〔B012〕 can be looked up (after the minutes).</summary>
    public static string SourceAppendix(IReadOnlyList<SourceUnit> units, IReadOnlyList<string> uncited)
    {
        var sb = new StringBuilder("\n---\n\n## 原文（番号付き）\n");
        if (uncited.Count > 0)
        {
            sb.Append("> 議事録のどの項目にも使われていない発言: ").Append(string.Join("、", uncited))
              .Append("（相づち・あいさつ等は除く）。抜けがないか確認してください。\n\n");
        }

        foreach (var unit in units)
        {
            sb.Append("- ").Append(unit.Id).Append(' ').Append(unit.Text).Append('\n');
        }

        return sb.ToString();
    }
}

/// <summary>Kanji numerals → digits (十一月二十日 → 11月20日), so both spellings compare equal.</summary>
public static partial class Numerals
{
    [GeneratedRegex("[〇零一二三四五六七八九十百千万]+")]
    private static partial Regex Kanji();

    public static string Arabic(string text) => Kanji().Replace(text, m => Value(m.Value)?.ToString() ?? m.Value);

    private static long? Value(string kanji)
    {
        const string digits = "〇一二三四五六七八九";
        long total = 0, section = 0, number = 0;
        bool any = false;
        foreach (char c in kanji)
        {
            int d = digits.IndexOf(c);
            if (c == '零')
            {
                d = 0;
            }

            if (d >= 0)
            {
                number = (number * 10) + d;
                any = true;
                continue;
            }

            long unit = c switch { '十' => 10, '百' => 100, '千' => 1000, '万' => 10000, _ => 0 };
            if (unit == 0)
            {
                return null;
            }

            if (unit == 10000)
            {
                total += (section + number) * unit;
                section = number = 0;
            }
            else
            {
                section += (number == 0 ? 1 : number) * unit;
                number = 0;
            }

            any = true;
        }

        return any ? total + section + number : null;
    }
}
