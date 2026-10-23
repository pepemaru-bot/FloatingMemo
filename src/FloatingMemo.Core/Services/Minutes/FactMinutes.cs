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

    /// <summary>Moving the meeting along ("次に予算です"): context for topics, not a fact of its own.</summary>
    public bool IsProcedural => SourceUnits.IsProcedural(Text);
}

/// <summary>Splits a note into numbered units (notes: by line, transcript: by sentence).</summary>
public static partial class SourceUnits
{
    [GeneratedRegex(@"(?<=[。！？!?])")]
    private static partial Regex SentenceEnd();

    /// <summary>A sentence longer than this is split (at "、" or spaces, else by length) into several units.</summary>
    public const int MaxUnitCharacters = 160;

    /// <summary>Splits text that is too long for one unit; pieces keep their order and every character.</summary>
    public static IEnumerable<string> Pieces(string sentence)
    {
        string rest = sentence;
        while (rest.Length > MaxUnitCharacters)
        {
            int cut = rest.LastIndexOfAny(['、', ',', ' ', '　'], MaxUnitCharacters - 1, MaxUnitCharacters / 2);
            cut = cut < 0 ? MaxUnitCharacters : cut + 1;
            yield return rest[..cut].Trim();
            rest = rest[cut..];
        }

        if (rest.Trim().Length > 0)
        {
            yield return rest.Trim();
        }
    }

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
                // Speech recognition sometimes writes long runs without "。": very long sentences
                // become several units so no single unit fills a part.
                foreach (string text in SentenceEnd().Split(line).SelectMany(Pieces))
                {
                    units.Add(new SourceUnit($"B{++b:000}", text, false));
                }
            }
        }

        return units;
    }

    // Short utterances made only of fillers, greetings and audio/screen checks.
    [GeneratedRegex("^(えーと|えっと|あの|その|はい|ええ|うん|そうですね|なるほど|ありがとうございます|お疲れさまでした|よろしくお願いします|すみません|ちょっと待ってください|聞こえていますか|見えていますか|大丈夫です|画面の共有を止めますね|もう一度お願いします|始めます|始めましょう|終わります|以上です|以上で終わります|[、。,.!！?？\\s])+$")]
    private static partial Regex Filler();

    public static bool IsNoise(string text) => text.Length <= 40 && Filler().IsMatch(text);

    // "次に予算です" / "では、駐車場の件に移ります" / "続いて広報について": a short line that only names
    // the next item. Anything with a number, a decision, a task or a question is not procedural.
    [GeneratedRegex(@"^(では|それでは|じゃあ|次に|次は|続いて|まず|最後に|ここから|次の議題は)[、,\s]*[^、。,？?！!]{1,14}?(?:(?:について|の件|の話)(?:です)?|です|に移ります|に入ります|を話します|にいきます|に行きます|をお願いします)[。.]?$")]
    private static partial Regex Procedural();

    [GeneratedRegex(@"[0-9０-９〇一二三四五六七八九十百千万]|決ま|決定|合意|承認|さんが|まで|未定|検討|ない|ません")]
    private static partial Regex Substantive();

    public static bool IsProcedural(string text)
    {
        string t = text.Trim();
        return t.Length <= 24 && Procedural().IsMatch(t) && !Substantive().IsMatch(t);
    }
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

    /// <summary>
    /// Set by the checks, never by the model: the cited words do not show reliably whether it was
    /// decided (garbled "見決定", tentative "仮決定", weak evidence, conflicting statements).
    /// Shown as 【要確認】, never as 【決定】.
    /// </summary>
    public const string Unclear = "要確認";

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

    /// <summary>F001…: given in the order facts are found (for the ledger and the writing step).</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>The model's own source numbers when the check moved the citation to the sentence that carries the fact.</summary>
    public string? RecitedFrom { get; init; }

    /// <summary>A condition the fact holds under ("雨の場合" …), as the model reported it.</summary>
    public string Condition { get; init; } = string.Empty;

    /// <summary>How well the cited source supports the fact (null = not checked yet).</summary>
    public Support? Support { get; init; }

    /// <summary>Checked and nothing to review: the cited source carries the fact.</summary>
    public bool Verified => Support == Minutes.Support.Supported && Issues.Count == 0;
}

/// <summary>Instructions for the extraction step (Japanese, because the meetings are Japanese).</summary>
public static class FactPrompt
{
    public const int MaxAnswerTokens = 2000;

    public static string System { get; } = $$"""
        あなたは会議の記録係です。番号付きの原文から、議事録に必要な事実を漏れなく抜き出してください。
        規則:
        1. 1行に1件、JSON で書く: {"type":"種類","topic":"話題","text":"内容","src":["B001"],"who":"担当","due":"期限","cond":"条件"}
        2. type は次のどれか: {{string.Join("・", FactKind.All)}}
           決定 = 「決まりました」「ことになりました」「合意」など、決まったと明言されたことだけ。
           作業 = だれかが行うことになった作業。提案・意見・質問を決定にしない。決まっていないことは 未決。
        3. topic は話題の短い名前（原文の言葉で、10文字程度）。同じ話題には同じ名前を使う。
        4. text は原文の内容を自然な日本語で短く書く。話題名がなくても意味が分かるように、何についてかを含める（例: 「資料の準備は田中さんが行う」）。
           数字・日付・名前・理由・条件・否定は省略しない。原文にない情報を足さない。
           作業の text には何をするかを必ず書く。「それ」や省略で前の発言を指すときは、指している内容を書き、その発言の番号も src に入れる。
        5. src には根拠の原文番号を必ず書く（A は参加者のメモ、B は会議の発言）。番号は原文にあるものだけ。
        6. who と due は原文で明言されたときだけ書く。なければ空文字 "" にする。
           cond には「〜の場合」「〜なら」など、その事実が成り立つ条件が原文にあれば書く。なければ "" にする。
        7. 【文脈】の行は前後関係の参考で、そこから事実を抜き出さない。
        8. あいさつ・相づち・音声や画面の確認、「次に予算です」のような進行だけの発言は抜き出さない。
        9. JSON の行以外は何も書かない。
        """;

    /// <summary>The rest of a part after an answer that stopped at its length limit.</summary>
    public static string Continue(IReadOnlyList<SourceUnit> context, IReadOnlyList<SourceUnit> units)
    {
        var sb = new StringBuilder("前の回答は途中で終わりました。続きの発言から、事実を抜き出してください（前の発言の事実は書かない）。\n<原文>\n");
        foreach (var unit in context)
        {
            sb.Append("【文脈】").Append(unit.Id).Append(' ').Append(unit.Text).Append('\n');
        }

        foreach (var unit in units.Where(u => !u.IsNoise))
        {
            sb.Append(unit.Id).Append(' ').Append(unit.Text).Append('\n');
        }

        return sb.Append("</原文>\n/no_think").ToString();
    }

    /// <summary>Several recovery ranges in one request (see FactMinutes.RecoveryBatches).</summary>
    public static string Recovery(IReadOnlyList<(IReadOnlyList<SourceUnit> Context, IReadOnlyList<SourceUnit> Range)> ranges)
    {
        var sb = new StringBuilder("次の発言は、議事録にまだ入っていない重要そうな部分です。事実として必要なものを抜き出してください。\n");
        sb.Append("<原文>\n");
        for (int i = 0; i < ranges.Count; i++)
        {
            if (i > 0)
            {
                sb.Append("…\n");
            }

            foreach (var unit in ranges[i].Context)
            {
                sb.Append("【文脈】").Append(unit.Id).Append(' ').Append(unit.Text).Append('\n');
            }

            foreach (var unit in ranges[i].Range.Where(u => !u.IsNoise))
            {
                sb.Append(unit.Id).Append(' ').Append(unit.Text).Append('\n');
            }
        }

        sb.Append("</原文>\n/no_think");
        return sb.ToString();
    }

    public static string User(IReadOnlyList<SourceUnit> context, IReadOnlyList<SourceUnit> units, int part, int parts, bool recovery = false)
    {
        var sb = new StringBuilder();
        if (recovery)
        {
            sb.Append("次の発言は、議事録にまだ入っていない重要そうな部分です。事実として必要なものを抜き出してください。\n");
        }
        else if (parts > 1)
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

    public static List<List<SourceUnit>> Group(IReadOnlyList<SourceUnit> units, Func<string, int>? tokens = null,
        int contextTokens = 8192, int margin = 512, int contentTokens = ContentTokensPerPart)
    {
        tokens ??= EstimateTokens;
        // Fixed per call: the instructions, the chat wrapper (~30), the answer budget and a margin.
        int fixedCost = tokens(FactPrompt.System) + 30 + FactPrompt.MaxAnswerTokens + margin;
        int budget = Math.Min(contentTokens, Math.Max(200, contextTokens - fixedCost));
        var parts = new List<List<SourceUnit>>();
        var current = new List<SourceUnit>();
        int used = 0;
        foreach (var unit in units)
        {
            int cost = unit.IsNoise ? 0 : tokens(unit.Id + " " + unit.Text) + 1; // fillers are not sent
            // The context lines repeated from the previous part also take room in this part's prompt.
            int reserved = parts.Count == 0 ? 0 : parts[^1].Where(u => !u.FromNotes && !u.IsNoise).TakeLast(3).Sum(u => tokens("【文脈】" + u.Id + " " + u.Text) + 1);
            if (current.Count > 0 && used + cost + reserved > budget)
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

                facts.Add(new Fact(FactKind.Normalize(Str(root, "type")), Str(root, "topic"), text, sources, Str(root, "who"), Str(root, "due"))
                {
                    Condition = Str(root, "cond"),
                });
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

    [GeneratedRegex("決まり|決定|決めました|決めた|ことになり|ことにな|合意|承認|確定|にします|でいきます|でいく")]
    private static partial Regex DecisionCue();

    [GeneratedRegex("まだ決まって|決まっていません|決めていません|決めていない|未定|検討しましょう|検討します|次回までに|保留|今日は決め|てから決め|あとで決め|後で決め|決めましょう")]
    private static partial Regex OpenCue();

    [GeneratedRegex(@"[0-9]+")]
    private static partial Regex Digits();

    [GeneratedRegex("ましょう|しませんか|予定|検討")]
    private static partial Regex Proposal();

    [GeneratedRegex("と思います|と思う|かもしれ|気がします")]
    private static partial Regex Opinion();

    [GeneratedRegex("まだ決まって|決まっていません|決めていません|決めていない|未定")]
    private static partial Regex Undecided();

    [GeneratedRegex(@"(?<who>[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}ァ-ヶA-Za-z]{1,8}?)(さん|氏|くん|君)が")]
    private static partial Regex TaskOwner();

    [GeneratedRegex(@"(?<due>(今週|今月|今日|本日|年内|月内|週内)中|[^、。がはをも]{1,12}?(?=までに|まで))")]
    private static partial Regex TaskDue();

    /// <summary>
    /// Content checks against the cited units: references must exist (and not be context-only
    /// units of another part), assignees and deadlines must be in them, numbers must be in
    /// them, and a decision must be said to be decided.
    /// </summary>
    /// <summary>
    /// Small models often cite a sentence a few lines away from the one they used (the numbers
    /// drift in long parts). If the cited sentence does not carry the fact but exactly one
    /// sentence near it fully does, the fact is checked again against that sentence — all the
    /// checks, not just the words — and kept with it only if it now passes them all. The
    /// original numbers are kept in <see cref="Fact.RecitedFrom"/> (trace). Nothing else is ever
    /// re-cited: a citation that no nearby sentence explains stays, with its warning.
    /// </summary>
    public static Fact Check(Fact fact, IReadOnlyDictionary<string, SourceUnit> units, ISet<string> allowed)
    {
        var result = CheckCited(fact, units, allowed);
        if (result.Support is null or Minutes.Support.Supported || fact.Sources.Count == 0)
        {
            return result;
        }

        string claimed = fact.Condition.Length > 0 ? fact.Condition + " " + fact.Text : fact.Text;
        var order = units.Keys.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i);
        string? candidate = FactEvidence.Candidate(claimed, fact.Kind, allowed.Where(units.ContainsKey).Select(id => units[id]));
        if (candidate is null || fact.Sources.Contains(candidate) || !order.TryGetValue(candidate, out int at)
            || !fact.Sources.Any(s => order.TryGetValue(s, out int cited) && Math.Abs(cited - at) <= MaxCitationDrift))
        {
            return result;
        }

        var recited = CheckCited(fact with { Sources = [candidate] }, units, allowed);
        return recited.Support == Minutes.Support.Supported && recited.Issues.Count == 0
            ? recited with { RecitedFrom = string.Join(",", fact.Sources) }
            : result;
    }

    /// <summary>How far (in sentences) a citation may be from the sentence it really meant.</summary>
    public const int MaxCitationDrift = 12;

    private static Fact CheckCited(Fact fact, IReadOnlyDictionary<string, SourceUnit> units, ISet<string> allowed)
    {
        string whole = string.Concat(units.Values.Select(u => u.Text));
        // Citations are never changed here: a wrong number stays visible with a warning.
        var valid = fact.Sources.Where(s => allowed.Contains(s)).Distinct().ToList();
        var issues = new List<string>();
        if (valid.Count == 0)
        {
            issues.Add(fact.Sources.Count == 0 ? "根拠の番号なし"
                : fact.Sources.Any(units.ContainsKey) ? "根拠の番号がこの部分の原文ではない（参照用の文脈）" : "根拠の番号が原文にない");
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

        // The decision state is read from the cited words (the clause the fact is about), not
        // taken from the kind the model chose: "未決定です" contains 決定, "見決定" is garbled.
        string kind = fact.Kind;
        var state = valid.Count > 0 ? DecisionStatus.ForFact(fact.Text, cited) : DecisionState.None;
        if (kind == FactKind.Decision && valid.Count > 0)
        {
            kind = state switch
            {
                DecisionState.Confirmed or DecisionState.ConfirmedNegative => FactKind.Decision,
                DecisionState.Open or DecisionState.Future => FactKind.Open,
                DecisionState.Proposal => FactKind.Proposal,
                DecisionState.Unclear => FactKind.Unclear,
                // Not said to be decided ("…話しましょう", "…と思います"): not shown as a decision.
                _ => Proposal().IsMatch(cited) ? FactKind.Open : Opinion().IsMatch(cited) ? FactKind.Opinion : FactKind.Info,
            };
            if (state == DecisionState.Unclear)
            {
                issues.Add("決定したかどうか原文からは判断できない（聞き取りの誤りや暫定の可能性）");
            }
        }

        // A decision not to do something must say so: "花火は上げない" — not "花火を上げる".
        if (kind == FactKind.Decision && state == DecisionState.ConfirmedNegative && !Negation().IsMatch(fact.Text) && !NegativeContent().IsMatch(fact.Text))
        {
            kind = FactKind.Unclear;
            issues.Add("原文は「しないこと」の決定だが、内容に否定がない");
        }

        // Said to be undecided, whatever kind the model chose (提案 / 情報): an open question.
        if (kind is FactKind.Info or FactKind.Proposal or FactKind.Opinion && valid.Count > 0 && (state == DecisionState.Open || Undecided().IsMatch(cited)))
        {
            kind = FactKind.Open;
        }

        // Said to be decided ("次回の定例会は十一月十四日に決まりました") but given a weaker kind by
        // the model: a decision. Only from the clause the fact is about, and the evidence check
        // below still turns a weakly supported one into 【要確認】.
        if (kind is FactKind.Info or FactKind.Proposal && valid.Count > 0 && state is DecisionState.Confirmed or DecisionState.ConfirmedNegative)
        {
            kind = FactKind.Decision;
        }

        // "森さんが来週の水曜日までに作ることになりました" is a task, whatever kind the model chose:
        // assignee and deadline are taken from the cited words themselves. Not when the words say
        // it is undecided or unclear ("森さんが来週までに作るかは未定").
        if (kind is not FactKind.Task and not FactKind.Open and not FactKind.Question and not FactKind.Unclear && valid.Count > 0
            && state is not (DecisionState.Open or DecisionState.Future or DecisionState.Unclear)
            && TaskOwner().Match(cited) is { Success: true } owner && TaskDue().Match(cited) is { Success: true } deadline)
        {
            kind = FactKind.Task;
            who = who.Length > 0 ? who : owner.Groups["who"].Value;
            due = due.Length > 0 ? due : deadline.Groups["due"].Value.Trim();
            issues.RemoveAll(i => i.StartsWith("決定と明言", StringComparison.Ordinal));
        }

        // Does the cited text carry the fact (numbers, names, negation, conditions)? Separate from
        // "the number exists". If not, the item is shown as 要確認, with a suggestion when exactly one
        // sentence of the part would carry it — never applied automatically.
        Support? support = null;
        if (valid.Count > 0)
        {
            string claimed = fact.Condition.Length > 0 ? fact.Condition + " " + fact.Text : fact.Text;
            var evidence = FactEvidence.Check(claimed, cited, kind, fact.Topic, whole);
            support = evidence.Level;
            issues.AddRange(evidence.Problems);
            if (evidence.Level == Minutes.Support.Unsupported
                && FactEvidence.Candidate(claimed, kind, allowed.Where(units.ContainsKey).Select(id => units[id])) is { } candidate
                && !valid.Contains(candidate))
            {
                issues.Add($"候補: {candidate}");
            }
        }

        // A decision whose cited words do not fully carry it is not shown as 【決定】 with a
        // warning: it is shown as 【要確認】 (the reasons stay in the warning).
        if (kind == FactKind.Decision && support is Minutes.Support.Partial or Minutes.Support.Unsupported)
        {
            kind = FactKind.Unclear;
        }

        return fact with { Sources = valid, Kind = kind, Who = who, Due = due, Issues = issues.Distinct().ToList(), Support = support };
    }

    /// <summary>
    /// Compares a decision with everything else said about the same subject in the meeting: if a
    /// later statement about it says it is not decided (or is unclear), the decision is shown as
    /// 【要確認】 and names that statement. An earlier "まだ決まっていない" is normal (decided later).
    /// An open item that a later statement seems to decide keeps 【未決】 with a note.
    /// </summary>
    public static Fact CrossCheck(Fact fact, IReadOnlyList<SourceUnit> units)
    {
        if (fact.Kind is not (FactKind.Decision or FactKind.Open) || fact.Sources.Count == 0)
        {
            return fact;
        }

        string subject = Subject(fact.Topic);
        if (subject.Length < 2)
        {
            return fact;
        }

        int last = units.Select((u, i) => (u, i)).Where(x => fact.Sources.Contains(x.u.Id)).Select(x => x.i).DefaultIfEmpty(-1).Max();
        if (last < 0)
        {
            return fact;
        }

        foreach (var unit in units.Skip(last + 1).Where(u => !u.IsNoise && MentionsAlone(MinutesValidator.Normalize(u.Text), subject)))
        {
            var state = DecisionStatus.ForFact(fact.Text, unit.Text);
            if (fact.Kind == FactKind.Decision && state is DecisionState.Open or DecisionState.Unclear or DecisionState.Future)
            {
                return fact with { Kind = FactKind.Unclear, Issues = [.. fact.Issues, $"後の発言 {unit.Id} では「{subject}」は未決"] };
            }

            if (fact.Kind == FactKind.Open && state is DecisionState.Confirmed or DecisionState.ConfirmedNegative)
            {
                return fact with { Issues = [.. fact.Issues, $"後の発言 {unit.Id} で決まった可能性"] };
            }
        }

        return fact;
    }

    /// <summary>
    /// The subject as a word of its own: "会議" in "次回の会議は" but not in "定例会議" (another
    /// subject that happens to end with the same word).
    /// </summary>
    private static bool MentionsAlone(string text, string subject)
    {
        for (int at = text.IndexOf(subject, StringComparison.Ordinal); at >= 0; at = text.IndexOf(subject, at + 1, StringComparison.Ordinal))
        {
            char before = at > 0 ? text[at - 1] : ' ';
            bool compound = char.IsLetterOrDigit(before) && !(before is >= 'ぁ' and <= 'ゖ'); // kanji, katakana, latin before it
            if (!compound)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>"公開日の変更" → "公開日": what a topic is about (the part before "の", or the whole name).</summary>
    private static string Subject(string topic)
    {
        string t = MinutesValidator.Normalize(topic);
        int of = t.IndexOf('の');
        string subject = of >= 2 ? t[..of] : t;
        return subject is "その他" or "全体" or "概要" ? string.Empty : subject;
    }

    [GeneratedRegex("見送|中止|取りやめ|取り止め|やめ|なし|しない|行わない|不要|廃止")]
    private static partial Regex NegativeContent();

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
            // The same statement twice: once, with the more specific kind. Two kinds for the same
            // sentence (e.g. 未決 and 提案 for one source) are one statement too.
            int twin = list.FindIndex(f => (f.Kind == fact.Kind || f.Kind == FactKind.Info || fact.Kind == FactKind.Info
                    || (f.Sources.Count > 0 && f.Sources.ToHashSet().SetEquals(fact.Sources))) && SameStatement(f.Text, fact.Text));
            if (twin >= 0)
            {
                var kept = list[twin];
                // One statement read as decided and as undecided (or unclear): never resolved in
                // favour of 【決定】 — it is shown as 【要確認】 with both readings named.
                bool split = Conflicting(kept.Kind, fact.Kind);
                list[twin] = kept with
                {
                    Kind = split ? FactKind.Unclear : Specific(kept.Kind) >= Specific(fact.Kind) ? kept.Kind : fact.Kind,
                    Sources = kept.Sources.Concat(fact.Sources).Distinct().ToList(),
                    Issues = kept.Issues.Concat(fact.Issues).Concat(split ? ["決定か未決か判定が分かれた"] : []).Distinct().ToList(),
                };
                continue;
            }

            list.Add(fact with { Group = topics[at].Topic });
        }

        return FlagContradictions(topics);
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

    /// <summary>
    /// Two facts of one topic that say the same thing with other numbers, another person or the
    /// opposite meaning are both kept and both flagged — never resolved by picking one.
    /// </summary>
    private static List<(string Topic, List<Fact> Facts)> FlagContradictions(List<(string Topic, List<Fact> Facts)> topics)
    {
        foreach (var (_, facts) in topics)
        {
            for (int i = 0; i < facts.Count; i++)
            {
                for (int j = i + 1; j < facts.Count; j++)
                {
                    if (Disagree(facts[i], facts[j]))
                    {
                        facts[i] = facts[i] with { Issues = [.. facts[i].Issues, $"{Label(facts[j])}と食い違う"] };
                        facts[j] = facts[j] with { Issues = [.. facts[j].Issues, $"{Label(facts[i])}と食い違う"] };
                    }
                }
            }
        }

        return topics;

        static string Label(Fact f) => f.Id.Length > 0 ? f.Id : "別の項目";
    }

    private static bool Disagree(Fact a, Fact b)
    {
        string x = Numerals.Arabic(MinutesValidator.Normalize(a.Text)), y = Numerals.Arabic(MinutesValidator.Normalize(b.Text));
        string bareX = Digits().Replace(x, string.Empty), bareY = Digits().Replace(y, string.Empty);
        if (MinutesQuality.Similarity(bareX, bareY) < 0.7)
        {
            return false;
        }

        var nx = Digits().Matches(x).Select(m => m.Value).ToHashSet();
        var ny = Digits().Matches(y).Select(m => m.Value).ToHashSet();
        bool numbers = nx.Count > 0 && ny.Count > 0 && !nx.SetEquals(ny);
        bool negation = Negation().IsMatch(x) != Negation().IsMatch(y);
        bool people = a.Who.Length > 0 && b.Who.Length > 0 && a.Who != b.Who && a.Kind == FactKind.Task && b.Kind == FactKind.Task;
        return numbers || negation || people;
    }

    /// <summary>
    /// A fact that cites only context lines (sentences of another part) belongs to that part, which
    /// extracts it itself; it is dropped here rather than kept without a source. If that part missed
    /// it, the uncited-statement check and the recovery pass catch it.
    /// </summary>
    public static bool OnlyContext(Fact fact, ISet<string> allowed, IReadOnlyDictionary<string, SourceUnit> units) =>
        fact.Sources.Count > 0 && !fact.Sources.Any(allowed.Contains) && fact.Sources.All(units.ContainsKey);

    private static bool Conflicting(string a, string b) =>
        (a == FactKind.Decision && b is FactKind.Open or FactKind.Unclear) || (b == FactKind.Decision && a is FactKind.Open or FactKind.Unclear);

    /// <summary>Which kind wins when one statement was given two kinds.</summary>
    private static int Specific(string kind) => kind switch
    {
        FactKind.Unclear => 7,
        FactKind.Decision => 6,
        FactKind.Task => 5,
        FactKind.Open => 4,
        FactKind.Proposal => 3,
        FactKind.Question => 2,
        FactKind.Opinion => 1,
        _ => 0,
    };

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

    /// <summary>At most this many ranges are extracted again, …</summary>
    public const int MaxRecoveryRanges = 8;

    /// <summary>How many times one part is continued after an answer that stopped at its length limit.</summary>
    public const int MaxContinuations = 3;

    /// <summary>… and for at most this share of the first pass's time (at least a minute).</summary>
    public const double RecoveryTimeShare = 0.5;

    /// <summary>Speed priority: one recovery request; a second only within this share of the first pass.</summary>
    public const double SpeedRecoveryTimeShare = 0.25;

    /// <summary>A fact taken only from remarks that move the meeting along ("次に予算です").</summary>
    public static bool OnlyProcedural(Fact fact, IReadOnlyDictionary<string, SourceUnit> units) =>
        fact.Kind is not FactKind.Task and not FactKind.Decision
        && fact.Sources.Count > 0
        && fact.Sources.All(s => units.TryGetValue(s, out var u) && u.IsProcedural);

    [GeneratedRegex(@"[0-9０-９〇一二三四五六七八九十百千万]+(月|日|時|分|件|円|万|割|%|％|人|回|個|台|本|週|年)|決まり|決定|ことにな|合意|承認|お願い|担当|まで|締め切り|期限|予定|未定|まだ決まって|検討|保留|問題|課題|不具合|リスク|困って|遅い|できない|場合|なら|条件|ただし|要望|提案|次回|さんが|中に|確認しました|確認済|申し込|手配|依頼")]
    private static partial Regex ImportantCue();

    /// <summary>A statement minutes usually need (decision, task, date, number, condition, problem, open question).</summary>
    public static bool IsImportant(string text) => !SourceUnits.IsNoise(text) && !SourceUnits.IsProcedural(text) && ImportantCue().IsMatch(text);

    // What minutes must not lose: a task with an owner and a deadline, a decision, an amount or a
    // date, an explicit "not decided yet". Used alone when speed matters (CPU).
    [GeneratedRegex(@"[0-9０-９〇一二三四五六七八九十百千万]+(月|日|時|分|件|円|万|割|%|％|人|回|個|台|週|年)|決まり|決定|ことにな|合意|承認|まだ決まって|決まっていません|未定|保留|(さん|氏)が.{0,30}(まで|中に)|担当|締め切り|期限")]
    private static partial Regex StrongCue();

    [GeneratedRegex(@"決まり|決定|ことにな|合意|承認|まだ決まって|決まっていません|未定|保留|(さん|氏)が|担当|締め切り|期限|まで|お願い|検討|課題|問題|場合|なら")]
    private static partial Regex BeyondNumbers();

    /// <summary>Only numbers the minutes already state, and nothing else that would need recording.</summary>
    private static bool Repeats(string text, ISet<string> stated)
    {
        var numbers = Digits().Matches(Numerals.Arabic(text)).Select(m => m.Value).ToList();
        return numbers.Count > 0 && numbers.All(stated.Contains) && !BeyondNumbers().IsMatch(text);
    }

    /// <summary>
    /// Quality priority: any statement with content (not a filler, not "次に予算です", at least a
    /// short sentence) that no fact uses is read again — not only those with a decision or task cue.
    /// </summary>
    public static bool IsContent(string text) =>
        !SourceUnits.IsNoise(text) && !SourceUnits.IsProcedural(text) && MinutesValidator.Normalize(text).Length >= 10;

    /// <summary>
    /// Cited sentences with a clause (split at "が、", "けど" …) whose words no fact citing the
    /// sentence covers: information left in a sentence the model only partly used.
    /// </summary>
    public static IEnumerable<string> PartlyUsed(IReadOnlyList<SourceUnit> units, IReadOnlyList<Fact> facts, bool criticalOnly) =>
        UncoveredClauses(units, facts, criticalOnly).Keys;

    /// <summary>
    /// The clauses of partly used sentences that no fact covers, by unit id. Re-reading shows the
    /// model only these words (the unit keeps its number; checks still use the whole sentence),
    /// otherwise it sees the sentence as already in the minutes.
    /// </summary>
    public static Dictionary<string, string> UncoveredClauses(IReadOnlyList<SourceUnit> units, IReadOnlyList<Fact> facts, bool criticalOnly)
    {
        var result = new Dictionary<string, string>();
        var bySource = facts.SelectMany(f => f.Sources.Select(s => (s, f))).GroupBy(x => x.s).ToDictionary(g => g.Key, g => g.Select(x => x.f).ToList());
        foreach (var unit in units.Where(u => !u.IsNoise && bySource.ContainsKey(u.Id)))
        {
            var clauses = DecisionStatus.Clauses(unit.Text);
            if (clauses.Count < 2)
            {
                continue;
            }

            string covered = MinutesValidator.Normalize(string.Concat(bySource[unit.Id].Select(f => f.Text + f.Condition)));
            foreach (string clause in clauses)
            {
                string c = MinutesValidator.Normalize(clause);
                if (c.Length < 6 || (criticalOnly && !IsCritical(clause)))
                {
                    continue;
                }

                int bigrams = 0, hits = 0;
                for (int i = 0; i + 1 < c.Length; i++)
                {
                    bigrams++;
                    hits += covered.Contains(c.Substring(i, 2), StringComparison.Ordinal) ? 1 : 0;
                }

                if (bigrams > 0 && hits < bigrams * 0.4)
                {
                    result[unit.Id] = result.TryGetValue(unit.Id, out string? before) ? before + " " + clause : clause;
                }
            }
        }

        return result;
    }

    /// <summary>A statement that is very likely needed in the minutes (see StrongCue).</summary>
    public static bool IsCritical(string text) => IsImportant(text) && StrongCue().IsMatch(text);

    /// <summary>
    /// Important statements no fact cites, grouped into short ranges of neighbouring units
    /// (a gap of one unit joins them), each at most 8 units.
    /// </summary>
    public static List<List<SourceUnit>> RecoveryRanges(IReadOnlyList<SourceUnit> units, IEnumerable<Fact> facts, bool criticalOnly = false)
    {
        var factList = facts.ToList();
        var cited = factList.SelectMany(f => f.Sources).ToHashSet();
        // A cited sentence with a clause no fact covers ("会場は予約できましたが、駐車場はまだ探して
        //います" → only the 会場 half taken) is read again too.
        var partly = PartlyUsed(units, factList, criticalOnly).ToHashSet();
        // Numbers the minutes already state: "あ、三日、三日で大丈夫です" only repeats 「十二月三日に決まりました」.
        var stated = factList.SelectMany(f => Digits().Matches(Numerals.Arabic(f.Text)).Select(m => m.Value)).ToHashSet();
        var ranges = new List<List<SourceUnit>>();
        int last = -10;
        for (int i = 0; i < units.Count; i++)
        {
            bool candidate = partly.Contains(units[i].Id)
                || (!cited.Contains(units[i].Id) && (criticalOnly ? IsCritical(units[i].Text) : IsContent(units[i].Text)) && !Repeats(units[i].Text, stated));
            if (!candidate)
            {
                continue;
            }

            if (ranges.Count > 0 && i - last <= 2 && ranges[^1].Count < 8)
            {
                for (int j = last + 1; j <= i; j++)
                {
                    ranges[^1].Add(units[j]);
                }
            }
            else
            {
                ranges.Add([units[i]]);
            }

            last = i;
        }

        return ranges;
    }

    /// <summary>
    /// Recovery ranges sent together, each with the two statements before it as context, so that
    /// one request covers several ranges (the instructions are read once, which is most of the
    /// time on a CPU). A batch stays within <paramref name="contentTokens"/>.
    /// </summary>
    public static List<List<(IReadOnlyList<SourceUnit> Context, IReadOnlyList<SourceUnit> Range)>> RecoveryBatches(
        IReadOnlyList<SourceUnit> units, IReadOnlyList<List<SourceUnit>> ranges, Func<string, int> tokens, int contentTokens = 1500)
    {
        var batches = new List<List<(IReadOnlyList<SourceUnit>, IReadOnlyList<SourceUnit>)>>();
        int used = 0;
        foreach (var range in ranges)
        {
            int first = units.ToList().FindIndex(u => u.Id == range[0].Id);
            IReadOnlyList<SourceUnit> context = units.Take(first).Where(u => !u.IsNoise).TakeLast(2).ToList();
            int cost = context.Concat(range).Sum(u => tokens(u.Id + " " + u.Text) + 1);
            if (batches.Count == 0 || used + cost > contentTokens)
            {
                batches.Add([]);
                used = 0;
            }

            batches[^1].Add((context, range));
            used += cost;
        }

        return batches;
    }

    // A task that is only "who, by when, verb": "山田さんが来週までに取ります", "来週までに確認します".
    // Nothing else may stand before the verb (「資料は森さんが明日までに送ります」 says what).
    [GeneratedRegex(@"^(?:[\p{IsCJKUnifiedIdeographs}\p{IsKatakana}ァ-ヶA-Za-z]{1,8}(?:さん|氏|くん|君)?(?:が|は)、?)?(?:[^、。はがを]{1,12}?(?:までに|中に)、?)?(?<pronoun>それを|これを|それも|そちらを|あれを)?(?<stem>[\p{IsCJKUnifiedIdeographs}]{0,2})[ぁ-ゖ]{0,6}(?:ます|ました|る|ておきます)$")]
    private static partial Regex VagueTask();

    /// <summary>
    /// A task that says who and by when but not what ("山田さんが来週までに取ります"): the object
    /// is taken from a statement just before it that uses the same verb ("二社から見積もりを取ります"),
    /// and that statement is cited as well. When none does, the task is marked 要確認.
    /// </summary>
    public static Fact Clarify(Fact fact, IReadOnlyList<SourceUnit> units)
    {
        string text = fact.Text.Trim().TrimEnd('。', '.');
        if (fact.Kind != FactKind.Task || fact.Sources.Count == 0 || VagueTask().Match(text) is not { Success: true } vague)
        {
            return fact;
        }

        int at = units.Select((u, i) => (u, i)).Where(x => fact.Sources.Contains(x.u.Id)).Select(x => x.i).DefaultIfEmpty(-1).Min();
        string stem = vague.Groups["stem"].Value;
        if (at > 0 && stem.Length > 0)
        {
            var before = units.Take(at).Where(u => !u.IsNoise && u.FromNotes == units[at].FromNotes).TakeLast(3).Reverse();
            foreach (var unit in before)
            {
                var m = Regex.Match(unit.Text, "(?<obj>[^、。はがを]{1,20})を" + Regex.Escape(stem));
                if (m.Success)
                {
                    // 「それを取ります」 → 「二社から見積もりを取ります」: the pronoun is replaced.
                    int verbAt = vague.Groups["pronoun"].Success ? vague.Groups["pronoun"].Index : vague.Groups["stem"].Index;
                    string clear = text[..verbAt] + m.Groups["obj"].Value + "を" + text[vague.Groups["stem"].Index..];
                    return fact with { Text = clear, Sources = [unit.Id, .. fact.Sources] };
                }
            }
        }

        return fact with { Issues = [.. fact.Issues, "作業の内容が原文から分からない"] };
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
            bool missing = !cited.Contains(unit.Id) && !unit.IsNoise && !unit.IsProcedural;
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
        [FactKind.Unclear] = "【要確認】",
        [FactKind.Proposal] = "【提案】",
        [FactKind.Question] = "【質問】",
        [FactKind.Opinion] = "【意見】",
    };

    /// <summary>
    /// The minutes body, written from the facts only: one heading per topic in the order the
    /// topics came up, tasks as checkboxes, the source numbers after each item, and what the
    /// checks found. Nothing is added that is not a fact.
    /// </summary>
    /// <param name="paragraphs">Checked prose per topic (writing step); facts it covers and that need no
    /// action are not listed again. Decisions, tasks, open questions and items to review always are.</param>
    public static string Render(IReadOnlyList<(string Topic, List<Fact> Facts)> topics, IReadOnlyDictionary<string, SourceUnit> units,
        IReadOnlyList<string> overview, List<string> warnings, IReadOnlyDictionary<string, string>? paragraphs = null, ISet<string>? covered = null)
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
            if (paragraphs is not null && paragraphs.TryGetValue(topic, out string? paragraph))
            {
                sb.Append(paragraph).Append("\n\n");
            }

            foreach (var fact in facts)
            {
                if (covered is not null && covered.Contains(fact.Id) && fact.Issues.Count == 0
                    && fact.Kind is FactKind.Info or FactKind.Opinion)
                {
                    continue; // already said in the paragraph
                }

                sb.Append(Line(fact, units)).Append('\n');

                if (fact.Issues.Count > 0)
                {
                    warnings.Add($"{topic}: {string.Join("／", fact.Issues)}");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Under a template heading the topic is not visible any more: "二十人から二十五人に増えた"
    /// under 準備 does not say it was the number of sign-ups. The fact's own topic is shown
    /// first when its words are not already in the text (and it is not the heading itself).
    /// </summary>
    public static string LineWithTopic(Fact fact, IReadOnlyDictionary<string, SourceUnit> units, string heading)
    {
        string topic = fact.Topic.Trim();
        string text = MinutesValidator.Normalize(fact.Text);
        bool shown = topic.Length < 2 || topic == "その他" || text.Contains(MinutesValidator.Normalize(topic), StringComparison.Ordinal)
            || MinutesValidator.Normalize(heading).Contains(MinutesValidator.Normalize(topic), StringComparison.Ordinal);
        return shown ? Line(fact, units) : Line(fact with { Text = $"{topic}: {fact.Text}" }, units);
    }

    /// <summary>One fact as a Markdown line: mark or checkbox, text, source numbers, origin, what to review.</summary>
    public static string Line(Fact fact, IReadOnlyDictionary<string, SourceUnit> units)
    {
        string refs = fact.Sources.Count == 0 ? string.Empty : $"〔{string.Join("・", fact.Sources)}〕";
        string origin = fact.Origin(units) switch { "メモ" => "（メモより）", "両方" => "（メモ・発言）", _ => string.Empty };
        string warn = fact.Issues.Count == 0 ? string.Empty : $" ⚠要確認（{string.Join("／", fact.Issues)}）";
        string condition = fact.Condition.Length > 0 && !fact.Text.Contains(fact.Condition, StringComparison.Ordinal) ? $"（条件: {fact.Condition}）" : string.Empty;
        if (fact.Kind == FactKind.Task)
        {
            string who = fact.Who.Length > 0 ? fact.Who : MinutesValidator.Unconfirmed;
            string due = fact.Due.Length > 0 ? fact.Due : MinutesValidator.Unconfirmed;
            return $"- [ ] {fact.Text}{condition} — 担当: {who} / 期限: {due} {refs}{origin}{warn}";
        }

        return $"- {Marks.GetValueOrDefault(fact.Kind, string.Empty)}{fact.Text}{condition} {refs}{origin}{warn}";
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
