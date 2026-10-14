namespace FloatingMemo.Services;

/// <summary>
/// Where the app looks for the AI models the user downloads (nothing is bundled or fetched).
/// </summary>
/// <remarks>
/// Search order for each kind (first file found wins):
///   1. &lt;app folder&gt;\models\&lt;kind&gt;      portable ZIP: next to FloatingMemo.exe
///   2. &lt;app folder&gt;\models             older portable layout (flat)
///   3. %USERPROFILE%\FloatingMemo\models\&lt;kind&gt;
///   4. %USERPROFILE%\FloatingMemo\models  older layout (flat)
/// FLOATINGMEMO_MODELS_DIR, when set, replaces %USERPROFILE%\FloatingMemo\models in 3 and 4.
/// The user profile folder is not %LOCALAPPDATA%: Windows virtualizes a packaged (MSIX) app's
/// view of AppData, so a model placed there by hand would be invisible to the app.
/// </remarks>
public static class ModelLocations
{
    public const string WhisperFolder = "whisper";
    public const string QwenFolder = "qwen";

    /// <summary>&lt;app folder&gt;\models</summary>
    public static string AppModelsDirectory => Path.Combine(AppContext.BaseDirectory, "models");

    /// <summary>
    /// %USERPROFILE%\FloatingMemo\models, or the folder named by the FLOATINGMEMO_MODELS_DIR
    /// environment variable (e.g. models kept on another drive).
    /// </summary>
    public static string UserModelsDirectory =>
        Environment.GetEnvironmentVariable("FLOATINGMEMO_MODELS_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FloatingMemo", "models");

    /// <summary>The folders searched for one kind of model, in order; true = inside the app folder.</summary>
    public static IReadOnlyList<(string Directory, bool InAppFolder)> SearchDirectories(string kind) =>
        SearchDirectories(kind, AppModelsDirectory, UserModelsDirectory);

    internal static IReadOnlyList<(string Directory, bool InAppFolder)> SearchDirectories(string kind, string appModels, string userModels) =>
    [
        (Path.Combine(appModels, kind), true),
        (appModels, true),
        (Path.Combine(userModels, kind), false),
        (userModels, false),
    ];

    /// <summary>
    /// Where the user is told to put a model of this kind: the app's own models folder when the
    /// app was unzipped with one (portable), otherwise the per-user folder.
    /// </summary>
    public static string RecommendedDirectory(string kind) =>
        RecommendedDirectory(kind, AppModelsDirectory, UserModelsDirectory);

    internal static string RecommendedDirectory(string kind, string appModels, string userModels) =>
        Path.Combine(Directory.Exists(appModels) ? appModels : userModels, kind);
}
