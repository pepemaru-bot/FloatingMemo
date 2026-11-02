using FloatingMemo.Services.Audio;
using FloatingMemo.Services.Dictionary;
using FloatingMemo.Services.Minutes;
using FloatingMemo.Services.Notes;
using FloatingMemo.Services.Settings;
using FloatingMemo.Services.Transcription;

namespace FloatingMemo;

/// <summary>The app-lifetime services the memo window and page work with.</summary>
public sealed record MemoServices(
    NoteWorkspace Workspace,
    AudioRecorder Recorder,
    LocalWhisperTranscriptionService Transcriber,
    SettingsService Settings,
    AppSettings Preferences,
    IAudioDeviceCatalog Devices,
    IAudioCaptureFactory CaptureFactory,
    Func<ILocalTextGenerator?> CreateMinutesModel,
    TermDictionary Dictionary)
{
    /// <summary>Haas-ja through speech.cpp (experimental); null when its files are not installed.</summary>
    public SpeechCppTranscriptionService? Haas { get; init; }

    /// <summary>The user's minutes templates (stored next to the notes, never in the app folder).</summary>
    public TemplateLibrary Templates { get; } = new(TemplateLibrary.PathFor(Workspace.Library.MetaDirectory));
}
