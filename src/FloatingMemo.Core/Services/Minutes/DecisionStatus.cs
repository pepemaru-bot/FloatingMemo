using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Minutes;

/// <summary>What a statement says about whether something is decided.</summary>
public enum DecisionState
{
    /// <summary>The statement says nothing about a decision.</summary>
    None,

    /// <summary>Decided ("決まりました", "ことになりました", "合意", "承認").</summary>
    Confirmed,

    /// <summary>Decided not to do it ("花火は上げないことに決まりました", "見送ることになりました").</summary>
    ConfirmedNegative,

    /// <summary>Said not to be decided ("未決定", "まだ決まっていません", "保留", "持ち越し").</summary>
    Open,

    /// <summary>To be decided later ("次回決めましょう", "来週判断します", "検討します").</summary>
    Future,

    /// <summary>A proposal ("〜しませんか", "〜してはどうか").</summary>
    Proposal,

    /// <summary>
    /// The word that should carry the state is garbled or tentative ("見決定" — a likely
    /// misrecognition of 未決定 — "仮決定", "暫定"): never shown as a decision.
    /// </summary>
    Unclear,
}

/// <summary>
/// Reads the decision state of the sentence(s) a fact cites, separately from the kind the model
/// chose ("speech act" and "state" are not the same thing: "決定はまだです" contains 決定 but
/// says the opposite). Order matters: garbled and negated forms are checked before the plain
/// "決定" they contain, and a decision not to do something before plain negation.
/// </summary>
public static partial class DecisionStatus
{
    // Likely misrecognitions of 未決定 / 未定 (the state is not trustworthy), and tentative decisions.
    [GeneratedRegex("見決定|味決定|身決定|美決定|見定まり|未定まり|仮決定|仮に決め|暫定|とりあえず決め")]
    private static partial Regex UnclearCue();

    // A decision not to do something: still a decision, its content is negative.
    [GeneratedRegex("(ない|見送る|見送り|中止する|中止|取りやめ|取り止め|やめる|なし)(こと)?(に|と|で|が)(決まり|決定|なり|合意|承認|確定|し?まし)")]
    private static partial Regex NegativeDecision();

    // Said not to be decided (before the plain cues: 未決定 contains 決定).
    [GeneratedRegex("未決定|未確定|未決|未定|決定していない|決定してない|決定していません|決定ではない|決定ではありません|決定はまだ|決定前|決定待ち|決定に至っていない|決定に至らな|決まっていない|決まってない|決まっていません|決まらな|決まりませんでした|決めていない|決めてない|決めていません|まだ決ま|決めかね|保留|持ち越し|先送り|結論が出(ませんでした|なかった|ていない|ていません)")]
    private static partial Regex Undecided();

    [GeneratedRegex("決まりました|決まった|決まりです|決定しました|決定です|決定した|に決定|で決定|決定となり|ことになりました|ことになった|ことになりまし|合意しました|合意した|合意され|で合意|承認され|承認しました|承認した|確定しました|確定です|確定した|決めました|決めた|にします|にしました|でいきます|で行きます")]
    private static partial Regex Confirmed();

    // To be decided later.
    [GeneratedRegex("(次回|来週|来月|後日|あとで|後で|改めて|今度|週明け|月末)(に|まで|の会議|の定例)?(に)?.{0,8}(決め|判断|検討|確認|結論)|決めましょう|決めたい|検討しましょう|検討します|検討する|判断します|判断する|予定です|予定になりました")]
    private static partial Regex Future();

    [GeneratedRegex("しませんか|どうでしょう|どうか|提案|ましょうか|してはどうか|したらどうか|はいかが")]
    private static partial Regex ProposalCue();

    // Clause boundaries: "会場は決まりましたが、日程はまだです" is two statements.
    [GeneratedRegex("(?<=(ましたが|ですが|ますが|ませんが|でしたが|けど|けれど|けれども|ものの|。|！|？))[、,]?|(?<=が)[、,]")]
    private static partial Regex ClauseEnd();

    /// <summary>The state a piece of text states (the whole text, no clause selection).</summary>
    public static DecisionState Of(string text)
    {
        string t = MinutesValidator.Normalize(text);
        if (UnclearCue().IsMatch(t))
        {
            return DecisionState.Unclear;
        }

        if (NegativeDecision().IsMatch(t))
        {
            return DecisionState.ConfirmedNegative;
        }

        if (Undecided().IsMatch(t))
        {
            return DecisionState.Open;
        }

        if (Confirmed().IsMatch(t))
        {
            return DecisionState.Confirmed;
        }

        if (Future().IsMatch(t))
        {
            return DecisionState.Future;
        }

        return ProposalCue().IsMatch(t) ? DecisionState.Proposal : DecisionState.None;
    }

    /// <summary>
    /// The state of the part of <paramref name="cited"/> that the fact is about: the clause that
    /// shares the most words with the fact (so "会場は決まりましたが、日程はまだ" gives Confirmed
    /// for the venue and Open for the date). When no clause stands out, the strictest state of
    /// all clauses wins (Unclear, then Open, before Confirmed).
    /// </summary>
    public static DecisionState ForFact(string factText, string cited)
    {
        var clauses = Clauses(cited);
        if (clauses.Count <= 1)
        {
            return Of(cited);
        }

        var scored = clauses.Select(c => (Clause: c, Score: Overlap(factText, c), State: Of(c))).ToList();
        var stated = scored.Where(s => s.State != DecisionState.None).ToList();
        if (stated.Count == 0)
        {
            return DecisionState.None;
        }

        var best = stated.OrderByDescending(s => s.Score).ToList();
        if (best.Count == 1 || best[0].Score - best[1].Score >= 0.15)
        {
            return best[0].State;
        }

        // Ambiguous: the most cautious state of the candidates.
        return stated.Select(s => s.State).OrderBy(Caution).First();
    }

    /// <summary>Lower = more cautious (shown first when the clause is not clear).</summary>
    private static int Caution(DecisionState state) => state switch
    {
        DecisionState.Unclear => 0,
        DecisionState.Open => 1,
        DecisionState.Future => 2,
        DecisionState.Proposal => 3,
        DecisionState.ConfirmedNegative => 4,
        DecisionState.Confirmed => 5,
        _ => 6,
    };

    internal static List<string> Clauses(string text) =>
        ClauseEnd().Split(text).Select(c => c.Trim()).Where(c => c.Length > 0).ToList();

    private static double Overlap(string fact, string clause)
    {
        static HashSet<string> Bigrams(string s)
        {
            string n = MinutesValidator.Normalize(s);
            var set = new HashSet<string>();
            for (int i = 0; i + 1 < n.Length; i++)
            {
                set.Add(n.Substring(i, 2));
            }

            return set;
        }

        var f = Bigrams(fact);
        if (f.Count == 0)
        {
            return 0;
        }

        var c = Bigrams(clause);
        return f.Count(c.Contains) / (double)f.Count;
    }
}
