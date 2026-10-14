# CLAUDE.md — Floating Memo

## Project mission

Build a minimal Windows floating memo that appears only when invoked, accepts keyboard or local speech input, and stores plain Markdown locally.

The experience should be:

**Ctrl+Shift+M → write/talk → Esc**

Read `FLOATING_MEMO_SPEC.md` before implementation.

---

## Non-negotiable product rules

1. **Local-first.** No cloud transcription, telemetry, analytics, remote note storage, or background network calls in MVP.
2. **Fast.** Hotkey-to-editor readiness should target ≤500 ms.
3. **Minimal UI.** Do not turn the memo surface into a dashboard.
4. **Autosave.** Normal use must not require a visible Save button.
5. **Portable data.** Note bodies are UTF-8 Markdown files.
6. **Safe drafts.** Never discard memo text because audio/transcription/settings fail.
7. **Audio privacy.** Raw audio is temporary and deleted after transcription by default.
8. **Offline test required.** Local transcription must work with network disabled.

---

## Preferred stack

- C# / .NET 10 LTS
- WinUI 3 / current supported stable Windows App SDK
- NAudio 3.x using modern WASAPI recorder APIs
- Whisper.net using a local multilingual Whisper model
- System.Text.Json
- normal .NET File I/O

Use Win32 interop only where it provides required Windows-native behavior such as global hotkeys or tray integration.

Avoid Electron/browser-shell frameworks.

---

## UI direction

The visual target is a restrained glass-like floating surface inspired by modern liquid-glass UI.

Prefer native Mica/Acrylic system backdrop behavior. Keep:

- rounded corners;
- subtle border/highlight;
- subtle shadow;
- minimal icons;
- no traditional heavy title bar;
- strong text legibility;
- contextual controls only when needed.

Do not add decorative complexity that slows implementation or obscures the memo.

---

## Development order

Do not start with transcription.

### Phase 1

- project boots;
- global hotkey;
- floating show/hide;
- immediate focus;
- Markdown autosave;
- Esc hides;
- glass visual treatment.

### Phase 2

- microphone capture;
- PC audio loopback;
- independent toggles;
- recording state;
- local temp audio pipeline.

### Phase 3

- Whisper.net local transcription;
- local model path;
- lazy model loading;
- transcript insertion;
- temp-audio cleanup;
- offline verification.

### Phase 4

- tray menu;
- settings polish;
- startup launch;
- device/error edge cases;
- final UI polish.

---

## Code quality rules

- Keep UI thread unblocked.
- Use cancellation tokens for recording/transcription where appropriate.
- Dispose native/audio resources deterministically.
- Do not log note contents, transcripts, or audio.
- Avoid premature abstraction, except around audio capture, transcription, settings, and persistence boundaries.
- Prefer small services and explicit state transitions.
- Use async code where I/O or transcription can block.
- Add comments for Windows interop behavior that is non-obvious.
- Build frequently.

---

## Git workflow

Initialize Git immediately if `.git` is absent.

Make meaningful commits at working milestones, for example:

- `chore: initialize floating memo project`
- `feat: add hotkey floating markdown memo`
- `feat: add local microphone and system audio capture`
- `feat: add offline whisper transcription`
- `feat: polish floating memo experience`

Do not bundle unrelated experiments into one commit.

---

## First-session target

For the first development session, stop after a polished Phase 1 vertical slice if needed.

The first meaningful demo is:

1. app running;
2. user is in another app;
3. Ctrl+Shift+M opens the floating glass memo;
4. Japanese typing works immediately;
5. text autosaves to Markdown;
6. Esc hides the memo;
7. reopen restores the draft/state appropriately.

That experience matters more than partially wiring every later feature.

---

## When uncertain

Choose the option that best satisfies, in this order:

1. privacy;
2. responsiveness;
3. reliability;
4. simplicity;
5. visual polish;
6. extra features.

Do not add cloud functionality to solve a local implementation difficulty.
