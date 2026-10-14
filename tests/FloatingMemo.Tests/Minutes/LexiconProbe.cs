using FloatingMemo.Services.Dictionary;
using Xunit.Abstractions;

namespace FloatingMemo.Tests.Minutes;

/// <summary>Runs only when FLOATINGMEMO_LEXICON points at a lexicon file: imports it into a temporary dictionary.</summary>
public sealed class LexiconProbeFactAttribute : FactAttribute
{
    public LexiconProbeFactAttribute()
    {
        if (!File.Exists(Environment.GetEnvironmentVariable("FLOATINGMEMO_LEXICON")))
        {
            Skip = "Set FLOATINGMEMO_LEXICON to a lexicon JSON file to check its import.";
        }
    }
}

public sealed class LexiconProbe(ITestOutputHelper output)
{
    [LexiconProbeFact]
    public void ImportsTheUsersLexicon()
    {
        string path = Environment.GetEnvironmentVariable("FLOATINGMEMO_LEXICON")!;
        string dir = Path.Combine(Path.GetTempPath(), "FloatingMemoTests", Guid.NewGuid().ToString("N"));
        try
        {
            var dictionary = new TermDictionary(Path.Combine(dir, "dictionary.json"));
            var result = dictionary.Import(File.ReadAllText(path), Path.GetFileName(path));
            output.WriteLine($"added {result.Added}, merged {result.Merged}, skipped {result.Skipped}");
            foreach (var group in dictionary.Entries.GroupBy(e => e.Category).OrderBy(g => g.Key))
            {
                output.WriteLine($"  {group.Key}: {group.Count()}");
            }

            output.WriteLine($"with aliases: {dictionary.Entries.Count(e => e.Aliases.Count > 0)}, with misrecognitions: {dictionary.Entries.Count(e => e.Misrecognitions.Count > 0)}, disabled: {dictionary.Entries.Count(e => !e.Enabled)}, whisper hints: {dictionary.Entries.Count(e => e.WhisperHint)}");
            Assert.Null(dictionary.WhisperHintText());

            // Second import: nothing duplicated.
            var again = dictionary.Import(File.ReadAllText(path), Path.GetFileName(path));
            output.WriteLine($"re-import: added {again.Added}, merged {again.Merged}");
            Assert.Equal(0, again.Added);

            // Only related terms are picked for a (made-up) sentence.
            const string sample = "渋渋の過去問と、時方の確認をしました。偏差値と志望校の話もしました。";
            var related = TermMatcher.Related(dictionary.ActiveEntries, sample);
            output.WriteLine("related to the sample: " + string.Join("、", related.Select(m => $"{m.Entry.Term}({m.FoundForm}:{m.Score:0.00})")));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            TermMatcher.Related(dictionary.ActiveEntries, string.Concat(Enumerable.Repeat(sample, 300)));
            output.WriteLine($"matching against a ~10,000-character transcript: {sw.ElapsedMilliseconds} ms");
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, true);
            }
        }
    }
}
