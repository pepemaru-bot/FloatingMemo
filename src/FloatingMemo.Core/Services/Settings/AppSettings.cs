using System.Text.Json;
using System.Text.Json.Serialization;
using FloatingMemo.Services.Audio;

namespace FloatingMemo.Services.Settings;

/// <summary>Window position and size in physical screen pixels.</summary>
public sealed record WindowBounds(int X, int Y, int Width, int Height);

/// <summary>Small, non-sensitive app preferences. Never contains memo text or audio.</summary>
public sealed class AppSettings
{
    public WindowBounds? Window { get; set; }

    public bool RecordMicrophone { get; set; } = true;

    public bool RecordSystemAudio { get; set; }

    /// <summary>Which recording device the microphone toggle uses (ID, not name, identifies it).</summary>
    public AudioDeviceSelection Microphone { get; set; } = AudioDeviceSelection.SystemDefault;

    /// <summary>File name of the Qwen model for AI minutes (null = the best one available).</summary>
    public string? MinutesModel { get; set; }

    /// <summary>Experimental visual (WYSIWYG) editing instead of the Markdown source editor (applies after a restart).</summary>
    public bool VisualEditor { get; set; }

    /// <summary>Id of the user's minutes template (null = おまかせ: the layout chosen for the model).</summary>
    public string? MinutesTemplateId { get; set; }

    /// <summary>Correct the transcript with the dictionary and context before writing minutes.</summary>
    public bool CorrectTranscriptBeforeMinutes { get; set; } = true;

    /// <summary>Outer glass: 0 = frosted (more tint), 1 = clear (more of the background shows).</summary>
    public double GlassClarity { get; set; } = 0.6;

    /// <summary>Paper (memo body) color: 0 = white, 1 = milky blue-grey frosted-glass tone.</summary>
    public double PaperMilkiness { get; set; } = 0.6;

    /// <summary>GPU use for Whisper and Qwen (applies after a restart).</summary>
    public FloatingMemo.Services.Acceleration.AccelerationMode Acceleration { get; set; } = FloatingMemo.Services.Acceleration.AccelerationMode.Auto;

    /// <summary>AI minutes: Auto (speed on a CPU, quality on a GPU), Speed or Quality.</summary>
    public FloatingMemo.Services.Minutes.MinutesPriority MinutesPriority { get; set; } = FloatingMemo.Services.Minutes.MinutesPriority.Auto;
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}

/// <summary>
/// Loads and saves <see cref="AppSettings"/> as JSON. A missing or damaged file falls back to
/// defaults — settings are a convenience and must never stop the memo from opening.
/// </summary>
public sealed class SettingsService
{
    private readonly object _lock = new();

    public SettingsService(string filePath)
    {
        FilePath = filePath;
    }

    public string FilePath { get; }

    /// <summary>%LOCALAPPDATA%\FloatingMemo\settings.json (private to the packaged app).</summary>
    public static string DefaultFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FloatingMemo", "settings.json");

    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                // Source-generated serializer: no reflection, safe with trimming.
                return JsonSerializer.Deserialize(File.ReadAllText(FilePath), AppSettingsJsonContext.Default.AppSettings)
                    ?? new AppSettings();
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Fall through to defaults.
        }

        return new AppSettings();
    }

    /// <summary>Writes via a temp file and rename so a crash never leaves half a file.</summary>
    public void Save(AppSettings settings)
    {
        string json = JsonSerializer.Serialize(settings, AppSettingsJsonContext.Default.AppSettings);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string temp = FilePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, FilePath, overwrite: true);
        }
    }

    /// <summary>Best-effort save; failures are ignored because settings are not user data.</summary>
    public bool TrySave(AppSettings settings)
    {
        try
        {
            Save(settings);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
