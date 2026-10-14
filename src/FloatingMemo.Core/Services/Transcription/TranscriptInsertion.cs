namespace FloatingMemo.Services.Transcription;

/// <summary>
/// How a finished transcript is added to a note: always at the very end of the note, under the
/// header "以下、AIによる文字起こし". Nothing the user wrote is moved, replaced or merged.
/// </summary>
public static class TranscriptInsertion
{
    /// <summary>The first line of every transcript block (so AI-written text is always recognizable).</summary>
    public const string Header = "以下、AIによる文字起こし";

    /// <summary>The transcript as a block: the header line, then the recognized text.</summary>
    public static string Block(string transcript, string newline) => Header + newline + transcript.Trim();

    /// <summary>
    /// What to add at the very end of a note: the block, separated from the existing text by one
    /// empty line. The existing text, the caret and any selection are left untouched.
    /// </summary>
    public static string Append(string text, string transcript, string newline) => Separator(text, newline) + Block(transcript, newline);

    /// <summary>Line breaks needed before a block appended to <paramref name="text"/> (one empty line).</summary>
    public static string Separator(string text, string newline)
    {
        if (text.Length == 0)
        {
            return string.Empty;
        }

        int trailing = 0;
        for (int i = text.Length - 1; i >= 0 && IsLineBreak(text[i]); i--)
        {
            // "\r\n" counts as one line break.
            if (text[i] == '\n' && i > 0 && text[i - 1] == '\r')
            {
                i--;
            }

            trailing++;
        }

        return string.Concat(Enumerable.Repeat(newline, Math.Max(0, 2 - trailing)));
    }

    private static bool IsLineBreak(char c) => c is '\r' or '\n';
}
