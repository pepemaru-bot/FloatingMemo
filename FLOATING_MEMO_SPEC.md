# Floating Memo for Windows — Product / Technical Specification

**Project codename:** Floating Memo  
**Target:** Windows 11 desktop  
**Status:** MVP specification v0.1  
**Date:** 2026-10-07  
**Primary development agent:** Claude Code  

---

## 0. One-line concept

> **必要な瞬間だけ現れ、書くか話すだけで、ローカルにMarkdownとして残る。**

A minimal, beautiful, local-first floating memo application for Windows.

The application normally stays out of the way. Press a global shortcut, a small glass-like memo surface appears, type or dictate, then dismiss it. Notes are saved automatically as plain Markdown so they can be used immediately in Obsidian or imported into AI tools later.

---

# 1. Product principles

Priority order:

1. **Fast** — thought → input should feel instantaneous.
2. **Invisible when unused** — the app must not occupy visual attention when not needed.
3. **Minimal** — the main UI only exposes actions needed at that moment.
4. **Beautiful** — modern translucent / glass-like Windows UI; restrained, not decorative.
5. **Local-first** — note content and audio stay on the device by default.
6. **Portable data** — notes are ordinary `.md` files, not trapped in a proprietary database.
7. **Recoverable** — drafts should survive crashes or accidental dismissal.

Do not trade principles 1–6 for feature count.

---

# 2. Privacy / local-first policy — NON-NEGOTIABLE

This application is designed for situations where confidential information may be present.

## 2.1 Runtime network policy

The finished application must operate fully without an Internet connection after local dependencies/models are installed.

By default, application runtime must NOT:

- call cloud speech-to-text APIs;
- upload audio;
- upload memo text;
- send analytics;
- send telemetry;
- send crash reports;
- synchronize notes to a cloud service;
- perform background network requests;
- load remote UI assets.

No API keys are required for MVP.

## 2.2 Local speech recognition

Speech-to-text must run locally using a Whisper-compatible local runtime/model.

Recommended initial implementation:

- `.NET` integration via **Whisper.net**;
- local multilingual Whisper model stored on disk;
- model path configurable;
- default MVP model: multilingual `base` for faster iteration;
- evaluate multilingual `small` later for improved Japanese accuracy.

Model download may be a separate manual setup step. Once the model exists locally, transcription must work offline.

## 2.3 Audio retention

Default behavior:

- raw captured audio is temporary;
- prefer in-memory buffering where practical;
- if a temporary WAV file is required, create it inside the app's local temp directory;
- delete it immediately after successful/failed transcription unless the user explicitly enables audio retention in a future version;
- do not retain recordings by default.

## 2.4 Local settings

Settings are stored locally, for example:

`%LOCALAPPDATA%/FloatingMemo/settings.json`

Do not put note content into settings or logs.

## 2.5 Logs

MVP logging must be minimal and local.

Logs may contain:

- timestamps;
- application state;
- exception type/message;
- device/runtime diagnostics.

Logs must NOT contain:

- memo body text;
- transcription text;
- raw audio;
- clipboard contents.

---

# 3. Core UX

## 3.1 Primary workflow

```text
Working in any Windows application
        ↓
Ctrl + Shift + M
        ↓
Floating memo appears and text box is focused
        ↓
Type OR start local transcription
        ↓
Auto-save
        ↓
Esc or Ctrl + Shift + M
        ↓
Window disappears, app remains resident
```

The user should never need to navigate through a dashboard to create a note.

## 3.2 Target interaction speed

- Hotkey → visible memo: target **≤ 500 ms**
- Visible memo → keyboard-ready: same frame / immediately after display
- No splash screen
- No modal dialog during ordinary note capture

---

# 4. Main window design

The memo is not meant to feel like a traditional application window.

## 4.1 Visual direction

Keywords:

- liquid glass inspired
- translucent
- subtle blur
- floating surface
- soft highlight
- restrained shadow
- rounded corners
- minimal chrome
- high legibility
- low visual noise

Use native Windows system materials where possible rather than manually faking glass.

Recommended UI stack:

- **WinUI 3**
- Windows App SDK stable channel
- `MicaBackdrop` / `DesktopAcrylicBackdrop` depending on final visual quality and performance

The goal is inspired by liquid-glass aesthetics, not pixel-for-pixel reproduction of another platform.

## 4.2 Default layout

Conceptual layout only:

```text
╭────────────────────────────────╮
│  メモを書く…                    │
│                                │
│                                │
│                                │
│                        ◉   ♪   │
╰────────────────────────────────╯
```

Visible by default:

- memo editor;
- subtle microphone control;
- subtle PC-audio control.

Everything else should be hidden until relevant.

## 4.3 Contextual controls

When recording:

```text
╭────────────────────────────────╮
│ 今日の会議では次回までに…       │
│                                │
│                                │
│ ● 00:37                 🎙  🔊 │
╰────────────────────────────────╯
```

Only while recording, show:

- recording state;
- elapsed time;
- active input sources;
- stop control.

## 4.4 No save button in normal flow

MVP should use autosave.

Expected user flow:

`Ctrl+Shift+M → write/talk → Esc`

A save command may exist internally / via `Ctrl+S`, but a persistent Save button is not required in the main UI.

---

# 5. Window behavior

## 5.1 Show / hide

Default global shortcut:

`Ctrl + Shift + M`

Behavior:

- if hidden → show and focus editor;
- if visible but unfocused → bring to foreground and focus editor;
- if visible and focused → hide;

Shortcut must be configurable later. Do not hard-couple architecture to this one shortcut.

Implementation can use Win32 `RegisterHotKey` interop.

## 5.2 Escape

`Esc` hides the memo window.

It does NOT terminate the application.

## 5.3 Always-on-top

While visible, default to topmost so the memo remains accessible over the current task.

Provide a future setting to disable topmost.

## 5.4 Position

MVP:

- remember last window position;
- remember last window size;
- guard against off-screen restoration after monitor configuration changes.

Future option:

- center;
- cursor-adjacent;
- fixed corner.

## 5.5 Window chrome

Avoid standard title bar if feasible.

Support:

- drag from empty area;
- rounded appearance;
- subtle resize behavior;
- no unnecessary maximize/minimize controls.

---

# 6. Text input

The editor must support:

- Japanese IME;
- normal keyboard input;
- line breaks;
- copy/paste;
- undo/redo;
- select all;
- direct Markdown syntax;
- automatic scrolling.

No rich-text editor is needed for MVP.

The note is plain Markdown source text.

---

# 7. Audio capture

Two independently toggleable audio inputs are required.

## 7.1 Microphone

Purpose:

- personal voice memo;
- own side of calls/meetings;
- spoken idea capture.

## 7.2 PC audio

Capture sound currently being rendered by the selected Windows output device.

Purpose:

- online meetings;
- video/audio playback;
- lectures;
- AI voice output.

Recommended library:

- **NAudio 3.x**
- prefer modern `WasapiRecorder` APIs for new code;
- system audio via WASAPI loopback capture.

Important MVP limitation:

> PC audio capture means the selected output device mix, not reliably a single application only.

Do not build per-process capture for v0.1.

## 7.3 Source states

Supported combinations:

```text
Mic ON / PC OFF
Mic OFF / PC ON
Mic ON / PC ON
```

Both OFF means transcription cannot start.

## 7.4 Both sources enabled

For MVP, capture microphone and system audio as independent streams where practical.

Normalize each to a transcription-friendly format such as:

- mono;
- 16 kHz;
- 16-bit PCM.

Then either:

A. mix down into one transcription stream, or
B. transcribe source streams separately and merge by timestamp.

**Preferred MVP approach: A** if substantially simpler.

Document known limitation:

- if speakers are used, microphone may pick up PC audio and create duplication;
- headphones reduce this problem.

Do not attempt acoustic echo cancellation in v0.1.

---

# 8. Transcription

## 8.1 MVP behavior

MVP transcription does NOT need true live streaming.

Initial flow:

```text
Start recording
↓
Capture audio locally
↓
Stop recording
↓
Local Whisper transcription
↓
Insert result at current editor cursor / append position
↓
Autosave
```

This dramatically reduces implementation complexity.

## 8.2 Future behavior

Later versions may implement chunked near-real-time transcription.

Do not architect the UI so tightly around post-recording transcription that streaming becomes impossible later.

## 8.3 Transcription interface

Create an abstraction:

```csharp
public interface ITranscriptionService
{
    Task<TranscriptionResult> TranscribeAsync(
        Stream audio,
        CancellationToken cancellationToken = default);
}
```

Concrete MVP implementation:

`LocalWhisperTranscriptionService`

No cloud implementation in MVP.

## 8.4 Model loading

Avoid loading a large model during app startup if it meaningfully hurts hotkey responsiveness.

Recommended behavior:

- app starts quickly;
- model lazy-loads on first transcription;
- after first load, keep the model/runtime alive while the application remains running unless memory pressure becomes problematic.

Show a subtle one-time state such as `音声モデルを準備中…` if first load takes noticeable time.

---

# 9. Notes / Markdown persistence

## 9.1 Storage format

Each note is an ordinary UTF-8 `.md` file.

No database is required for note bodies in MVP.

## 9.2 Default folder

Example:

`%USERPROFILE%/Documents/FloatingMemo/`

User must be able to change this folder.

Any local folder may be chosen, including an Obsidian Vault folder.

## 9.3 Obsidian

No Obsidian-specific API is required.

User can simply set output folder to:

`<Vault>/Inbox/`

The resulting Markdown appears naturally in Obsidian.

## 9.4 Filename

Default:

`yyyy-MM-dd_HHmmss.md`

Example:

`2026-10-07_214203.md`

Avoid illegal Windows filename characters.

## 9.5 File body

Recommended initial structure:

```md
---
created: 2026-10-07 21:42:03
source: floating-memo
---

# 2026-10-07 21:42

本文...
```

Do not automatically add AI-generated metadata in MVP.

## 9.6 Autosave

Autosave triggers:

- debounce after content changes (e.g. 500–1000 ms);
- after transcription insertion;
- before window hide;
- before app shutdown.

Write safely:

- write temp file;
- atomically replace/rename when practical;
- prevent accidental note loss on crash.

## 9.7 Empty notes

Do not create a permanent Markdown file if the note contains no meaningful text.

---

# 10. Draft lifecycle

Recommended MVP rule:

- if the current note has meaningful content and has not been finalized, reopening shows the same note;
- after the user explicitly starts a new note, create a fresh note;
- hiding the window alone does NOT necessarily finalize the note.

Because an explicit "new note" button adds visual noise, MVP may use:

- keyboard shortcut `Ctrl+N`;
- tray-menu action `New memo`.

If implementation complexity becomes high, simpler temporary rule is acceptable:

> one active note per application session; `Ctrl+N` starts a new note.

---

# 11. System tray / lifecycle

Application should remain resident when the memo is hidden.

Tray menu:

- Open memo
- New memo
- Settings
- Open notes folder
- Exit

Main window close action should hide, not terminate, unless invoked from Exit.

Do not require tray interaction during ordinary use.

---

# 12. Settings — MVP

Keep settings UI separate from the memo surface.

Required:

### General
- launch at Windows startup: optional / can be Phase 2;
- topmost toggle;
- global hotkey setting: may be Phase 2, architecture-ready in Phase 1.

### Storage
- Markdown output folder;
- reveal output folder.

### Audio
- microphone device;
- output/loopback device;
- local Whisper model path;
- transcription language: `auto` / `ja`.

### Privacy
Display a simple statement:

> 音声とメモは、このPC上で処理・保存されます。MVPではクラウド送信を行いません。

---

# 13. Recommended technical stack

Reference snapshot as of 2026-10-07. Prefer supported stable releases at project creation.

## Application

- C#
- .NET 10 LTS
- WinUI 3
- Windows App SDK — current Stable channel
- x64 first

## Audio

- NAudio 3.x
- modern `WasapiRecorder`
- WASAPI loopback for PC audio

## Local transcription

- Whisper.net
- local multilingual Whisper GGML model

## Serialization

- `System.Text.Json`

## Persistence

- standard .NET File I/O

## Native integration

Use direct Win32 interop only where needed:

- global hotkey;
- tray icon if no lightweight native option is appropriate;
- window activation/topmost behavior.

Avoid Electron / browser-shell frameworks for MVP because startup latency, memory footprint, and native Windows integration are contrary to the product goals.

---

# 14. Proposed project structure

```text
FloatingMemo/
├─ README.md
├─ SPEC.md
├─ CLAUDE.md
├─ FloatingMemo.sln
└─ src/
   └─ FloatingMemo.App/
      ├─ App.xaml
      ├─ App.xaml.cs
      ├─ Views/
      │  ├─ MemoWindow.xaml
      │  └─ SettingsWindow.xaml
      ├─ ViewModels/
      │  ├─ MemoViewModel.cs
      │  └─ SettingsViewModel.cs
      ├─ Services/
      │  ├─ Hotkeys/
      │  │  └─ GlobalHotkeyService.cs
      │  ├─ Audio/
      │  │  ├─ IAudioCaptureService.cs
      │  │  ├─ MicrophoneCaptureService.cs
      │  │  ├─ SystemAudioCaptureService.cs
      │  │  └─ AudioPreparationService.cs
      │  ├─ Transcription/
      │  │  ├─ ITranscriptionService.cs
      │  │  └─ LocalWhisperTranscriptionService.cs
      │  ├─ Notes/
      │  │  ├─ NoteService.cs
      │  │  └─ AutoSaveService.cs
      │  ├─ Settings/
      │  │  └─ SettingsService.cs
      │  └─ Tray/
      │     └─ TrayService.cs
      ├─ Models/
      │  ├─ Note.cs
      │  ├─ AppSettings.cs
      │  └─ TranscriptionResult.cs
      └─ Infrastructure/
         └─ LocalPaths.cs
```

Do not create abstractions with no foreseeable second implementation. The exceptions are audio capture/transcription/storage boundaries where future replacement is likely.

---

# 15. State model

Suggested memo window states:

```text
Hidden
  ↓ hotkey
Idle
  ├─ typing → Idle + autosave
  ├─ record → Recording
  └─ Esc/hotkey → Hidden

Recording
  ├─ stop → Transcribing
  └─ error → Idle + non-blocking error

Transcribing
  ├─ success → Idle + insert text + autosave
  └─ error → Idle + preserve draft/audio only as needed for retry
```

Recording and transcription must not freeze the UI thread.

---

# 16. Error handling UX

Errors should be concise and non-modal where possible.

Examples:

- `マイクを開けませんでした`
- `PC音声を取得できませんでした`
- `Whisperモデルが見つかりません`
- `保存先に書き込めませんでした`

Provide a small details affordance if debugging information is useful.

Never discard existing memo text because another subsystem failed.

---

# 17. Accessibility / input quality

Even with minimal UI:

- controls need tooltips;
- keyboard navigation must work;
- active recording state must not rely only on color;
- text contrast must remain readable over backdrop;
- respect Windows light/dark theme if feasible;
- reduce transparency if Windows accessibility/system policy requests it.

---

# 18. MVP scope

## REQUIRED for v0.1

- [ ] Windows application launches successfully
- [ ] app can remain running while memo is hidden
- [ ] global hotkey displays/hides memo
- [ ] editor receives immediate keyboard focus
- [ ] Japanese text input works
- [ ] glass-like minimal UI
- [ ] autosave to Markdown
- [ ] configurable local output folder
- [ ] selected output folder can be an Obsidian Vault folder
- [ ] microphone capture
- [ ] PC audio capture
- [ ] mic / PC audio toggles
- [ ] local speech transcription
- [ ] transcription inserted into note
- [ ] no runtime cloud/API dependency
- [ ] raw audio not retained by default

## NOT REQUIRED for v0.1

- AI summary
- AI rewriting
- cloud sync
- accounts/login
- mobile app
- semantic search
- speaker diarization
- per-application audio capture
- acoustic echo cancellation
- true live transcription
- note browser
- tagging system
- Markdown preview
- rich text
- calendar integration
- collaboration

---

# 19. Development phases

## Phase 0 — Project skeleton

Goal: bootable app and clean architecture.

- create project / solution;
- add README / SPEC / CLAUDE instructions;
- initialize Git;
- confirm Debug build;
- confirm packaged/unpackaged decision;
- no functionality beyond basic window.

Commit suggestion:

`chore: initialize floating memo project`

---

## Phase 1 — Core text memo

Goal: prove the product loop before touching speech.

Implement:

- hidden/resident app lifecycle;
- global hotkey;
- show/hide floating window;
- immediate editor focus;
- glass visual treatment;
- typing;
- local Markdown autosave;
- Esc to hide;
- note/output folder handling.

Acceptance test:

> Open another app → press Ctrl+Shift+M → type Japanese → Esc → confirm `.md` file contains the text.

Commit suggestion:

`feat: add hotkey floating markdown memo`

---

## Phase 2 — Audio capture

Goal: reliably capture both source types locally.

Implement:

- device enumeration;
- mic toggle;
- PC audio toggle;
- recording state;
- local temporary audio pipeline;
- stop behavior;
- format normalization.

Acceptance tests:

1. mic only produces valid audio;
2. PC audio only captures currently playing Windows output;
3. both enabled produces usable audio;
4. stopping repeatedly does not crash or leak devices.

Commit suggestion:

`feat: add local microphone and system audio capture`

---

## Phase 3 — Local transcription

Goal: captured audio becomes note text without leaving the PC.

Implement:

- Whisper.net integration;
- local model setting;
- lazy model loading;
- transcription service;
- insert transcript into editor;
- cleanup temporary audio;
- errors/cancellation.

Acceptance test:

> Disconnect network → record Japanese speech → stop → transcription appears → Markdown is updated.

Commit suggestion:

`feat: add offline whisper transcription`

---

## Phase 4 — Polish

Goal: make it feel like a product rather than a demo.

- animations kept subtle;
- window position persistence;
- dark/light behavior;
- recording timer;
- tray menu;
- settings screen;
- launch at startup;
- edge cases and resource cleanup.

Commit suggestion:

`feat: polish floating memo experience`

---

# 20. Tonight's recommended build target

Do NOT attempt the entire application tonight.

The best stopping point for the first session is a satisfying Phase 1 vertical slice.

## Tonight — Must achieve

1. Create repository and WinUI 3 project.
2. App launches.
3. Main memo window has the visual direction.
4. `Ctrl + Shift + M` toggles the window from anywhere.
5. Editor gets focus immediately.
6. Japanese typing works.
7. Text autosaves to a local `.md` file.
8. `Esc` hides the window without exiting.
9. Initialize Git and make clean commits.

## Tonight — Bonus

- tray icon;
- selectable output folder;
- mic capture spike;
- Mica vs Acrylic comparison.

## Explicitly defer if time begins to disappear

- Whisper integration;
- simultaneous mic + PC mixing;
- detailed settings UI;
- installer;
- AI features.

The product loop should feel good before audio complexity is added.

---

# 21. Definition of Done — v0.1

A normal user can:

1. start Windows app;
2. leave it in the background;
3. press `Ctrl+Shift+M` while using another application;
4. immediately type or start recording;
5. choose microphone, PC audio, or both;
6. stop recording;
7. receive locally generated Japanese transcript in the memo;
8. press `Esc`;
9. find the note as a Markdown file in the configured local folder / Obsidian Inbox;
10. perform all of the above with the computer disconnected from the Internet.

And:

- no memo/audio content leaves the device;
- the UI never feels like a dashboard;
- normal capture requires no Save button;
- closing/hiding never silently loses a draft.

---

# 22. Testing checklist

## Hotkey / window

- [ ] shortcut works while Chrome is active
- [ ] shortcut works while VS Code/IDE is active
- [ ] repeated show/hide does not create duplicate windows
- [ ] focus lands in editor
- [ ] multi-monitor position is valid
- [ ] Esc hides but process remains alive

## Notes

- [ ] Japanese filename/body saved as UTF-8
- [ ] rapid typing does not corrupt file
- [ ] crash/restart does not destroy last draft
- [ ] invalid/unwritable output path shows safe error
- [ ] empty note does not create clutter

## Audio

- [ ] mic permissions/device failures handled
- [ ] unplug/replug device behavior safe
- [ ] default output device loopback works
- [ ] recording can be cancelled
- [ ] audio resources are released after stop

## Offline privacy

- [ ] app core works with network disabled
- [ ] transcription works with network disabled
- [ ] no API key required
- [ ] no telemetry configured
- [ ] temporary audio removed after transcription

---

# 23. Later roadmap

Only consider after v0.1 feels excellent.

### v0.2 — Near-real-time transcription

- chunked transcription;
- partial text display;
- VAD;
- lower perceived delay.

### v0.3 — Local AI enrichment

Still local-first.

Possible features using a user-installed local LLM runtime:

- summarize;
- extract TODOs;
- clean transcript;
- generate title;
- classify note.

Do not make local LLM mandatory for basic memo capture.

### v0.4 — AI export helpers

- Copy as prompt;
- Copy current note;
- open file location;
- explicitly user-triggered integrations.

Any cloud-AI integration, if ever added, must be opt-in and visually explicit before content leaves the machine.

---

# 24. Technical notes / current references

Current implementation choices were selected because:

- .NET 10 is an active LTS release.
- WinUI 3 / Windows App SDK provides native Mica/Acrylic system backdrop APIs.
- modern NAudio 3 includes `WasapiRecorder` and loopback capture support; it recommends the newer recorder API for new WASAPI capture code.
- Whisper.net provides .NET bindings over whisper.cpp with local Windows runtimes and can run with pre-downloaded local models.

Useful references:

- Microsoft .NET support policy: https://dotnet.microsoft.com/platform/support/policy
- Windows system backdrops: https://learn.microsoft.com/windows/apps/develop/ui/system-backdrops
- Windows App SDK release channels: https://learn.microsoft.com/windows/apps/windows-app-sdk/stable-channel
- NAudio: https://github.com/naudio/NAudio
- Whisper.net: https://github.com/sandrohanea/whisper.net

---

# 25. Instruction to Claude Code

When implementing this project:

1. Read `SPEC.md` and `CLAUDE.md` completely before editing files.
2. Work phase-by-phase.
3. Do not implement future roadmap features unless requested.
4. Before each phase, inspect the existing repository and summarize the intended changes.
5. Prefer the smallest working implementation.
6. Build after meaningful changes.
7. Fix compilation/runtime errors before moving on.
8. Make clean Git commits at phase boundaries.
9. Do not introduce cloud dependencies, analytics, telemetry, or remote speech APIs.
10. Treat responsiveness and local privacy as product requirements, not optional optimizations.

