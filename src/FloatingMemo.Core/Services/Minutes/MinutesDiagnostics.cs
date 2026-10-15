using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace FloatingMemo.Services.Minutes;

/// <summary>
/// What the minutes pipeline was given, measured before anything is sent to the model. Only
/// sizes and a short hash: never the note text, names or anything said in the meeting.
/// </summary>
public sealed record MinutesInputSnapshot(
    string NoteId,
    string Hash,
    int TotalCharacters,
    int HumanCharacters,
    int TranscriptCharacters,
    int TranscriptBlocks)
{
    public bool HasTranscript => TranscriptBlocks > 0 && TranscriptCharacters > 0;

    public static MinutesInputSnapshot Of(string noteId, string text)
    {
        var sections = NoteSections.Split(text);
        return new MinutesInputSnapshot(noteId, ShortHash(text), text.Length, sections.HumanNotes.Length,
            sections.Transcripts.Sum(t => t.Length), sections.Transcripts.Count);
    }

    /// <summary>First 12 hex digits of SHA-256: tells two inputs apart without revealing them.</summary>
    public static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..12].ToLowerInvariant();
}

/// <summary>One model call: which part, how much went in and out, and why it stopped.</summary>
public sealed record MinutesCall(string Step, int Part, int InputCharacters, int OutputCharacters, string Finish, TimeSpan Elapsed);

/// <summary>
/// How a minutes draft was made, for finding lost input later: the parts the text was split
/// into (character ranges), whether every character of the input went into some part, each
/// model call, and the settings. Sizes, ranges and hashes only.
/// </summary>
public sealed class MinutesRun
{
    public required MinutesInputSnapshot Input { get; init; }

    public required string Model { get; init; }

    public required MinutesStyle Style { get; init; }

    public string Settings { get; init; } = string.Empty;

    /// <summary>How the input was laid out for the model (e.g. notes and transcript apart).</summary>
    public string InputMode { get; set; } = string.Empty;

    /// <summary>[start, end) of each part in the text that was split.</summary>
    public List<(int Start, int End)> Ranges { get; } = [];

    /// <summary>Characters of the split text that are in no part (should always be empty).</summary>
    public List<(int Start, int End)> Unprocessed { get; } = [];

    public List<MinutesCall> Calls { get; } = [];

    public bool Truncated => Calls.Any(c => c.Finish == MinutesFinish.MaxTokens);

    /// <summary>Finds each part in the split text in order and records what was left out.</summary>
    public void RecordParts(string splitText, IReadOnlyList<string> parts)
    {
        int at = 0;
        foreach (string part in parts)
        {
            int start = splitText.IndexOf(part, at, StringComparison.Ordinal);
            if (start < 0)
            {
                Ranges.Add((-1, -1)); // the part is not a verbatim piece (should not happen)
                continue;
            }

            if (HasContent(splitText, at, start))
            {
                Unprocessed.Add((at, start));
            }

            Ranges.Add((start, start + part.Length));
            at = start + part.Length;
        }

        if (HasContent(splitText, at, splitText.Length))
        {
            Unprocessed.Add((at, splitText.Length));
        }
    }

    private static bool HasContent(string text, int start, int end) =>
        end > start && text.AsSpan(start, end - start).Trim().Length > 0;

    public string Format(DateTime when) => string.Create(CultureInfo.InvariantCulture,
        $"{when:yyyy-MM-ddTHH:mm:ss} note={Input.NoteId} input={Input.Hash} chars={Input.TotalCharacters} " +
        $"human={Input.HumanCharacters} transcript={Input.TranscriptCharacters} blocks={Input.TranscriptBlocks} " +
        $"model={Model} style={Style} mode={InputMode} {Settings} parts={Ranges.Count} " +
        $"ranges={string.Join(",", Ranges.Select(r => $"{r.Start}-{r.End}"))} " +
        $"unprocessed={(Unprocessed.Count == 0 ? "none" : string.Join(",", Unprocessed.Select(r => $"{r.Start}-{r.End}")))} " +
        $"calls={string.Join(";", Calls.Select(c => $"{c.Step}#{c.Part}:in={c.InputCharacters},out={c.OutputCharacters},{c.Finish},{c.Elapsed.TotalSeconds:F1}s"))}");

    public static string DefaultPath => Path.Combine(Path.GetTempPath(), "FloatingMemo", "minutes-diagnostics.log");

    /// <summary>Appends one line to the diagnostics log (outside the repository; never fails the caller).</summary>
    public void Append(DateTime when, string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, Format(when) + Environment.NewLine);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Why a model stopped writing.</summary>
public static class MinutesFinish
{
    public const string End = "end";
    public const string MaxTokens = "max_tokens";
    public const string Unknown = "unknown";
}
