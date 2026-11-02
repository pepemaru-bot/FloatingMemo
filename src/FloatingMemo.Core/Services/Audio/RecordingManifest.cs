using System.Text.Json;
using System.Text.Json.Serialization;

namespace FloatingMemo.Services.Audio;

/// <summary>Where a recording session is, written next to its audio so it can be recovered after a failure or crash.</summary>
public static class RecordingStates
{
    /// <summary>Capture is running (or the app ended while it was: a crash).</summary>
    public const string Recording = "recording";

    /// <summary>Capture stopped; the transcription file is being made.</summary>
    public const string Stopped = "stopped";

    /// <summary>Making the transcription file failed; the captured segments are kept.</summary>
    public const string Failed = "failed";

    /// <summary>The transcription file is ready (raw segments removed).</summary>
    public const string Prepared = "prepared";

    /// <summary>Kept for the user to retry (e.g. transcription failed and the app was closed).</summary>
    public const string Kept = "kept";

    /// <summary>The transcript is saved in its note; only deleting the audio was left.</summary>
    public const string Delivered = "delivered";
}

/// <summary>One source of a session: its segment files (names only) and where it starts on the timeline.</summary>
public sealed class RecordingTrackInfo
{
    public string Source { get; set; } = string.Empty;

    public long StartOffsetTicks { get; set; }

    public int SampleRate { get; set; }

    public int Channels { get; set; }

    public int BlockAlign { get; set; }

    public List<string> Segments { get; set; } = [];
}

/// <summary>
/// session.json in a recording folder: sizes, names and states only (never note text). Written
/// atomically (temporary file, then replace), so a crash leaves either the old or the new version.
/// </summary>
public sealed class RecordingManifest
{
    public const string FileName = "session.json";

    public int Version { get; set; } = 1;

    public DateTime Created { get; set; }

    public string State { get; set; } = RecordingStates.Recording;

    /// <summary>Why it failed (exception type and message), for the user's message and diagnostics.</summary>
    public string? Error { get; set; }

    /// <summary>File name of the 16 kHz transcription WAV once it exists.</summary>
    public string? Prepared { get; set; }

    /// <summary>The note the recording was made for (its id), so a recovered transcript can go back there.</summary>
    public string? NoteId { get; set; }

    public List<RecordingTrackInfo> Tracks { get; set; } = [];

    public static RecordingManifest? Load(string sessionDirectory)
    {
        try
        {
            string path = Path.Combine(sessionDirectory, FileName);
            return File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), RecordingManifestJsonContext.Default.RecordingManifest) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null; // damaged: the audio files are still found by name
        }
    }

    /// <summary>Marks a session whose transcript is saved in its note (before its audio is deleted).</summary>
    public static void MarkDelivered(string sessionDirectory)
    {
        var manifest = Load(sessionDirectory) ?? new RecordingManifest { Created = DateTime.Now };
        manifest.State = RecordingStates.Delivered;
        manifest.TrySave(sessionDirectory);
    }

    /// <summary>Saves next to the audio. Never throws: the manifest helps recovery but is not required for it.</summary>
    public bool TrySave(string sessionDirectory)
    {
        string path = Path.Combine(sessionDirectory, FileName);
        string temp = path + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(this, RecordingManifestJsonContext.Default.RecordingManifest));
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(RecordingManifest))]
internal sealed partial class RecordingManifestJsonContext : JsonSerializerContext
{
}
