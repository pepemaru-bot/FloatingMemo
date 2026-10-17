using System.Text;
using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// Optional writing step of the fact-first pipeline: the model turns the checked facts of each
/// topic into a short readable paragraph, citing the fact ids it used. Its text is then checked
/// against the ledger again, sentence by sentence; anything the facts do not carry is removed,
/// and decisions, tasks and open questions are always listed mechanically as well.
/// </summary>
public static partial class FactWriting
{
    public const int MaxAnswerTokens = 1500;

    /// <summary>Facts of at most this many characters go into one writing call.</summary>
    public const int BatchCharacters = 1800;

    public static string System { get; } = """
        あなたは議事録の清書係です。与えられた「確認済みの事実」だけを使って、話題ごとに読みやすい文章を書いてください。
        規則:
        1. 事実に書かれていないこと（人名・数字・日付・理由・評価・結論）を足さない。推測しない。
        2. 決定・作業・未決・提案の区別を変えない。提案や未決を決定のように書かない。否定や条件を落とさない。
        3. 各文の末尾に、その文で使った事実の番号を [F001] の形で必ず付ける（複数なら [F001][F002]）。
        4. 話題ごとに「## 話題名」を書き、その下に 1〜4 文の段落を書く。会議に出ていない人が流れを分かるように、関係する事実をつなげて書く。
        5. 箇条書き・前置き・結びの文は書かない。
        """;

    public static string User(IReadOnlyList<(string Topic, List<Fact> Facts)> topics)
    {
        var sb = new StringBuilder("<確認済みの事実>\n");
        foreach (var (topic, facts) in topics)
        {
            sb.Append("## ").Append(topic).Append('\n');
            foreach (var f in facts)
            {
                sb.Append(f.Id).Append(" [").Append(f.Kind).Append("] ");
                if (f.Condition.Length > 0)
                {
                    sb.Append("（条件: ").Append(f.Condition).Append("）");
                }

                sb.Append(f.Text);
                if (f.Kind == FactKind.Task)
                {
                    sb.Append("（担当: ").Append(f.Who.Length > 0 ? f.Who : "未確認").Append("／期限: ").Append(f.Due.Length > 0 ? f.Due : "未確認").Append('）');
                }

                sb.Append('\n');
            }
        }

        return sb.Append("</確認済みの事実>\n/no_think").ToString();
    }

    /// <summary>Topics in batches whose fact text stays under <see cref="BatchCharacters"/>.</summary>
    public static List<List<(string Topic, List<Fact> Facts)>> Batches(IReadOnlyList<(string Topic, List<Fact> Facts)> topics)
    {
        var batches = new List<List<(string Topic, List<Fact> Facts)>>();
        int size = 0;
        foreach (var topic in topics)
        {
            int length = topic.Facts.Sum(f => f.Text.Length + 20);
            if (batches.Count == 0 || size + length > BatchCharacters)
            {
                batches.Add([]);
                size = 0;
            }

            batches[^1].Add(topic);
            size += length;
        }

        return batches;
    }

    [GeneratedRegex(@"\[(?<id>F\d{3,})\]")]
    private static partial Regex FactRef();

    [GeneratedRegex(@"(?<=[。！？])")]
    private static partial Regex SentenceEnd();

    /// <summary>What the check kept and removed (for warnings and the evaluation).</summary>
    public sealed record Checked(Dictionary<string, string> Paragraphs, int Kept, int Removed, IReadOnlyList<string> Reasons, HashSet<string> CoveredIds);

    /// <summary>
    /// Keeps a sentence only if it cites facts of its topic and those facts carry it (numbers,
    /// names, negation, conditions — the same evidence check as for the source). Returns one
    /// paragraph per topic, with fact ids replaced by the facts' source numbers.
    /// </summary>
    public static Checked Check(string answer, IReadOnlyList<(string Topic, List<Fact> Facts)> topics)
    {
        var paragraphs = new Dictionary<string, string>();
        var reasons = new List<string>();
        var covered = new HashSet<string>();
        int kept = 0, removed = 0;
        foreach (var (title, lines) in TopicMinutes.Parse(answer))
        {
            var topic = topics.FirstOrDefault(t => Same(t.Topic, title));
            if (topic.Facts is null)
            {
                removed += lines.Count;
                reasons.Add($"知らない話題「{title}」の文章を除去");
                continue;
            }

            var byId = topic.Facts.Where(f => f.Id.Length > 0).ToDictionary(f => f.Id);
            var sb = new StringBuilder();
            string text = string.Concat(lines.Select(l => Regex.Replace(l, @"^-\s*", string.Empty)));
            // "…決まった。[F001]" — the ids after the period belong to the sentence before it.
            text = Regex.Replace(text, @"([。！？])\s*((?:\[F\d{3,}\])+)", "$2$1");
            foreach (string sentence in SentenceEnd().Split(text).Select(s => s.Trim()).Where(s => s.Length > 0))
            {
                var ids = FactRef().Matches(sentence).Select(m => m.Groups["id"].Value).Distinct().ToList();
                var facts = ids.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
                string plain = FactRef().Replace(sentence, string.Empty).Trim();
                if (facts.Count == 0 || facts.Count < ids.Count)
                {
                    removed++;
                    reasons.Add(facts.Count == 0 ? $"根拠の事実番号のない文を除去（{title}）" : $"この話題にない事実番号を挙げた文を除去（{title}）");
                    continue;
                }

                string carried = string.Join("。", facts.Select(f => $"{f.Condition} {f.Text} {f.Who} {f.Due}"));
                string kind = facts.Any(f => f.Kind == FactKind.Open) ? FactKind.Open : facts[0].Kind;
                var evidence = FactEvidence.Check(plain, carried, kind);
                if (evidence.Level == Support.Unsupported || evidence.Problems.Any(p => !p.StartsWith("根拠の原文は内容の一部", StringComparison.Ordinal)))
                {
                    removed++;
                    reasons.Add($"事実にない内容を含む文を除去（{title}: {string.Join("／", evidence.Problems)}）");
                    continue;
                }

                string refs = string.Join("・", facts.SelectMany(f => f.Sources).Distinct());
                sb.Append(plain).Append(refs.Length > 0 ? $"〔{refs}〕" : string.Empty);
                covered.UnionWith(facts.Select(f => f.Id));
                kept++;
            }

            if (sb.Length > 0)
            {
                paragraphs[topic.Topic] = sb.ToString();
            }
        }

        return new Checked(paragraphs, kept, removed, reasons, covered);
    }

    private static bool Same(string a, string b)
    {
        string x = MinutesValidator.Normalize(a), y = MinutesValidator.Normalize(b);
        return x == y || (Math.Min(x.Length, y.Length) >= 2 && (x.Contains(y, StringComparison.Ordinal) || y.Contains(x, StringComparison.Ordinal)));
    }
}
