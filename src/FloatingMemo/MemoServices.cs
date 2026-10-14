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
    TermDictionary Dictionary);
