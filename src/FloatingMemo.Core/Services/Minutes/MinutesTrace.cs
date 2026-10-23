namespace FloatingMemo.Services.Minutes;

/// <summary>
/// Where each fact went through the fact-first pipeline: extracted, dropped (and why), checked,
/// recovered, merged, placed under a heading. Kept in memory with the draft for evaluation and
/// debugging; only the per-stage counts go to the diagnostics log (never the text).
/// </summary>
public sealed class MinutesTrace
{
    public sealed record Item(string Stage, string Id, string Kind, string Text, string Sources, string Note);

    public List<Item> Items { get; } = [];

    public void Add(string stage, Fact fact, string note = "") =>
        Items.Add(new Item(stage, fact.Id, fact.Kind, fact.Text, string.Join(",", fact.Sources),
            fact.RecitedFrom is null ? note : $"根拠番号を {fact.RecitedFrom} から補正" + (note.Length > 0 ? "／" + note : string.Empty)));

    public void Add(string stage, IEnumerable<Fact> facts)
    {
        foreach (var fact in facts)
        {
            Add(stage, fact, fact.Issues.Count == 0 ? string.Empty : string.Join("／", fact.Issues));
        }
    }

    /// <summary>"parsed=24 checked=22 drop-context=2 …": counts per stage, in the order the stages ran.</summary>
    public string Counts() => string.Join(" ", Items.GroupBy(i => i.Stage).Select(g => $"{g.Key}={g.Count()}"));
}
