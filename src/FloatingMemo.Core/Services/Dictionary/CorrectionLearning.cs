using System.Text;
using System.Text.RegularExpressions;

namespace FloatingMemo.Services.Dictionary;

/// <summary>
/// Learns from the user's own fixes. When minutes are made, the corrected transcript is kept as
/// a snapshot; later, words the user changed in that part of the minutes note become dictionary
/// candidates. Nothing is registered until the user approves it.
/// </summary>
public static partial class CorrectionLearning
{
    [GeneratedRegex(@"〔要確認: [^〕]*〕")]
    private static partial Regex ReviewMark();

    [GeneratedRegex(@"(?<=[。！？!?\n])")]
    private static partial Regex SentenceEnd();

    public static string SnapshotDirectory(string metaDirectory) => Path.Combine(metaDirectory, "corrections");

    /// <summary>Remembers the corrected transcript as the AI wrote it (local file, no audio).</summary>
    public static void SaveSnapshot(string metaDirectory, string noteId, string correctedText)
    {
        string dir = SnapshotDirectory(metaDirectory);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, noteId + ".txt"), correctedText, new UTF8Encoding(false));
    }

    /// <summary>
    /// Compares each snapshot with the corrected-transcript part of its minutes note now and
    /// records changed words as candidates. The snapshot is then updated, so a change is only
    /// counted once. Returns the number of new or repeated candidates.
    /// </summary>
    public static int Harvest(string metaDirectory, TermDictionary dictionary, Func<string, (string Title, string Text)?> readNote, string marker)
    {
        string dir = SnapshotDirectory(metaDirectory);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        int count = 0;
        foreach (string file in Directory.GetFiles(dir, "*.txt"))
        {
            string noteId = Path.GetFileNameWithoutExtension(file);
            if (readNote(noteId) is not { } note)
            {
                continue; // note deleted or unreadable: keep the snapshot, try again later
            }

            int at = note.Text.IndexOf(marker, StringComparison.Ordinal);
            if (at < 0)
            {
                continue; // the user removed the section: nothing to learn
            }

            string now = note.Text[(at + marker.Length)..].Trim();
            string before = File.ReadAllText(file);
            foreach (var (heard, correct) in ChangedWords(before, now))
            {
                if (dictionary.AddCandidate(heard, correct, "ユーザーの修正", note.Title))
                {
                    count++;
                }
            }

            File.WriteAllText(file, now, new UTF8Encoding(false));
        }

        return count;
    }

    /// <summary>
    /// Words replaced between two versions of a text, found sentence by sentence (review marks are
    /// ignored, so accepting a suggestion counts as a change). Only short replacements are kept.
    /// </summary>
    public static IReadOnlyList<(string Heard, string Correct)> ChangedWords(string before, string after)
    {
        var a = Sentences(ReviewMark().Replace(before, string.Empty));
        var b = Sentences(ReviewMark().Replace(after, string.Empty));

        // Longest common subsequence of whole sentences, then pair up the unmatched ones.
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
        {
            for (int j = b.Count - 1; j >= 0; j--)
            {
                lcs[i, j] = a[i] == b[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            }
        }

        var pairs = new List<(string, string)>();
        var gapA = new List<string>();
        var gapB = new List<string>();
        void Flush()
        {
            if (gapA.Count == gapB.Count)
            {
                for (int k = 0; k < gapA.Count; k++)
                {
                    if (Middle(gapA[k], gapB[k]) is { } pair)
                    {
                        pairs.Add(pair);
                    }
                }
            }

            gapA.Clear();
            gapB.Clear();
        }

        int x = 0, y = 0;
        while (x < a.Count || y < b.Count)
        {
            if (x < a.Count && y < b.Count && a[x] == b[y])
            {
                Flush();
                x++;
                y++;
            }
            else if (y < b.Count && (x == a.Count || lcs[x, y + 1] >= lcs[x + 1, y]))
            {
                gapB.Add(b[y++]);
            }
            else
            {
                gapA.Add(a[x++]);
            }
        }

        Flush();
        return pairs;
    }

    /// <summary>The differing middle of two sentences, once common beginnings and endings are removed.</summary>
    private static (string, string)? Middle(string a, string b)
    {
        int start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start])
        {
            start++;
        }

        int end = 0;
        while (end < a.Length - start && end < b.Length - start && a[a.Length - 1 - end] == b[b.Length - 1 - end])
        {
            end++;
        }

        // Widen to whole words: a fix of 「田」→「山」 inside 「田中」 is really 「田中」→「山中」.
        while (start > 0 && IsWordChar(a[start - 1]))
        {
            start--;
        }

        while (end > 0 && IsWordChar(a[a.Length - end]) && IsWordChar(b[b.Length - end]))
        {
            end--;
        }

        string heard = a.Substring(start, a.Length - start - end).Trim();
        string correct = b.Substring(start, b.Length - start - end).Trim();
        return heard.Length is > 0 and <= 20 && correct.Length is > 0 and <= 20 ? (heard, correct) : null;
    }

    /// <summary>Kanji, katakana and the long-vowel mark form "words" in Japanese text.</summary>
    private static bool IsWordChar(char c) => c is (>= '一' and <= '鿿') or (>= '゠' and <= 'ヿ') or '々';

    private static List<string> Sentences(string text) =>
        SentenceEnd().Split(text.Replace("\r\n", "\n")).Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
}
