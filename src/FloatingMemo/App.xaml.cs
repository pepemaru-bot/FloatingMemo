using System.Security.Cryptography;
using System.Text;
using FloatingMemo.Interop;
using FloatingMemo.Services;
using FloatingMemo.Services.Audio;
using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Notes;
using FloatingMemo.Services.Settings;
using FloatingMemo.Services.Transcription;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace FloatingMemo;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;
    
    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();

        UnhandledException += (_, e) => WriteCrashLog(e.Exception);
    }

    /// <summary>
    /// Local-only diagnostics: exception type, message and stack trace. Never memo text,
    /// transcripts or audio (CLAUDE.md logging rules).
    /// </summary>
    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            string dir = Path.Combine(Path.GetTempPath(), "FloatingMemo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "crash.log"), $"[{DateTime.Now:O}] {ex}\n\n");
        }
        catch
        {
            // Logging must never cause a second failure.
        }
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override async void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // "--notes-dir <folder>" overrides the notes folder (used by tests; later an Obsidian vault).
        string notesDirectory = GetArgument("--notes-dir") ?? NoteStorageService.DefaultDirectory;

        // Single instance per notes folder: two instances would overwrite each other's draft.md
        // (and the second would delete the first one's in-progress recording at start-up).
        // This must run before anything else touches files.
        var mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey(notesDirectory));
        if (!mainInstance.IsCurrent)
        {
            // We were just started by the user, so we may hand foreground rights to the
            // running instance; it then brings its memo to the front.
            NativeMethods.AllowSetForegroundWindow(mainInstance.ProcessId);
            await mainInstance.RedirectActivationToAsync(AppInstance.GetCurrent().GetActivatedEventArgs());
            Exit();
            return;
        }

        // Each note is its own .md file in the notes folder; the old draft.md is opened in place.
        var workspace = new NoteWorkspace(new NoteLibrary(notesDirectory));

        // Window position/size, audio toggles and the chosen microphone.
        // "--settings-file" isolates tests from real settings.
        var settings = new SettingsService(GetArgument("--settings-file") ?? SettingsService.DefaultFilePath);
        var preferences = settings.Load();

        // GPU or CPU for Whisper and Qwen: decided once, before either loads a native library.
        FloatingMemo.Services.Acceleration.AccelerationState.Initialize(
            GetArgument("--acceleration") is { } forced && Enum.TryParse<FloatingMemo.Services.Acceleration.AccelerationMode>(forced, true, out var m) ? m : preferences.Acceleration);

        // Nothing is recorded until the user presses the record button. Listing devices and
        // watching for changes does not open any device.
        var (devices, captureFactory) = CreateAudioDevices(() => preferences.Microphone);
        var recorder = new AudioRecorder(captureFactory, AudioRecorder.DefaultWorkDirectory);
        recorder.DeleteLeftoverRecordings();

        // The model is not loaded here: it loads on the first transcription so start-up stays fast.
        // "--model <file>" selects another GGML model (e.g. ggml-small.bin for better accuracy).
        var transcriber = new LocalWhisperTranscriptionService(
            GetArgument("--model") ?? LocalWhisperTranscriptionService.DefaultModelPath, language: "ja");

        // The user's own term dictionary (empty until the user imports or adds terms), stored
        // next to the notes. Whisper gets only the few words the user marked as hints.
        var dictionary = new TermDictionary(TermDictionary.PathFor(workspace.Library.MetaDirectory));
        transcriber.HintProvider = dictionary.WhisperHintText;

        var window = new MainWindow(new MemoServices(workspace, recorder, transcriber, settings, preferences, devices, captureFactory,
            () => CreateMinutesModel(preferences.MinutesModel), dictionary));
#if TEST_HOOKS
        // Debug builds only: "--theme light|dark" previews a theme without changing Windows settings.
        if (GetArgument("--theme") is { } theme && Enum.TryParse<ElementTheme>(theme, ignoreCase: true, out var requested)
            && window.Content is FrameworkElement root)
        {
            root.RequestedTheme = requested;
        }
#endif
        _window = window;
        window.Activate();
        window.RegisterToggleHotkey();

        // A second launch was redirected here: show the memo instead of opening another window.
        // Activated is raised on a background thread.
        var dispatcher = window.DispatcherQueue;
        mainInstance.Activated += (_, _) => dispatcher.TryEnqueue(window.ShowMemo);
    }

    /// <summary>Stable per-folder key (the path itself may be long or contain any characters).</summary>
    private static string InstanceKey(string notesDirectory)
    {
        string normalized = Path.GetFullPath(notesDirectory).TrimEnd('\\').ToUpperInvariant();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return "FloatingMemo-" + Convert.ToHexString(hash, 0, 8);
    }

    private static (IAudioDeviceCatalog Devices, IAudioCaptureFactory Factory) CreateAudioDevices(Func<AudioDeviceSelection> microphone)
    {
#if TEST_HOOKS
        // Debug builds only: "--fake-audio <file.wav>" replaces the microphone/PC audio with a
        // WAV file so automated tests never touch real audio devices; "--fake-devices" adds a
        // made-up device list for testing the device menu.
        if (GetArgument("--fake-audio") is { } wav)
        {
            IAudioDeviceCatalog fakeDevices = Environment.GetCommandLineArgs().Contains("--fake-devices")
                ? new StaticDeviceCatalog(
                    [
                        new AudioDeviceInfo("{fake-array}", "テスト用マイク配列", true),
                        new AudioDeviceInfo("{fake-usb}", "テスト用USBマイク", true),
                        new AudioDeviceInfo("{fake-mix}", "ステレオ ミキサー（テスト）", true),
                    ],
                    defaultId: "{fake-mix}",
                    communicationsId: "{fake-usb}")
                : new StaticDeviceCatalog([new AudioDeviceInfo("{fake-mic}", "テスト用マイク", true)], "{fake-mic}");
            // "--fake-audio-realtime": deliver the file at playback speed, repeating, like a
            // live device (for the level indicators and the mic test).
            bool realTime = Environment.GetCommandLineArgs().Contains("--fake-audio-realtime");
            return (fakeDevices, new WavFileCaptureFactory(wav, realTime));
        }
#endif
        var catalog = new WasapiDeviceCatalog();
        return (catalog, new WasapiCaptureFactory(catalog, microphone));
    }

    /// <summary>
    /// The local LLM for AI minutes, created only when the user asks for minutes (loading it
    /// takes memory). "--llm-model &lt;file.gguf&gt;" picks a model; otherwise the best one found
    /// in the models folders. Null when no model is installed.
    /// </summary>
    private static ILocalTextGenerator? CreateMinutesModel(string? chosenModel)
    {
#if TEST_HOOKS
        if (Environment.GetCommandLineArgs().Contains("--fake-llm"))
        {
            return new FakeMinutesModel();
        }
#endif
        string? path = GetArgument("--llm-model") ?? LlamaTextGenerator.FindModel(chosenModel);
        return path is not null && File.Exists(path) ? new LlamaTextGenerator(path) : null;
    }

    private static string? GetArgument(string name)
    {
        string[] commandLine = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(commandLine, name);
        return index >= 0 && index + 1 < commandLine.Length ? commandLine[index + 1] : null;
    }
}
