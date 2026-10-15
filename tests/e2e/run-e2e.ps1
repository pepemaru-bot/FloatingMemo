<#
.SYNOPSIS
    Runs Floating Memo's end-to-end tests against the real app (Debug build).

.DESCRIPTION
    - Generates Japanese test speech with the built-in Windows voice (offline SAPI); no
      microphone, no recorded human voice and no network are used.
    - Runs each suite in a scratch folder under %TEMP%\FloatingMemoE2E, never against the
      real notes/settings.
    - Refuses to run while Floating Memo is already running (the tests press the global
      hotkey, which would otherwise reach your real instance).

    The transcription and perf suites need the Whisper model (scripts\setup-model.ps1).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\e2e\run-e2e.ps1
    powershell -ExecutionPolicy Bypass -File tests\e2e\run-e2e.ps1 -Only ui
#>
param([string]$Only = '', [int]$TimeoutMinutes = 15)

$ErrorActionPreference = 'Stop'
$work = Join-Path $env:TEMP 'FloatingMemoE2E'
$notes = Join-Path $work 'notes'
New-Item -ItemType Directory -Force $work | Out-Null

if (Get-Process FloatingMemo -ErrorAction SilentlyContinue) {
    Write-Error "Floating Memo is running. Close it before running the E2E tests (they use the global hotkey)."
    exit 2
}

# --- Synthetic Japanese speech (offline Windows TTS) ----------------------------------
Add-Type -AssemblyName System.Speech
function New-Speech([string]$path, [string]$text) {
    if (Test-Path $path) { return }
    $synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
    try {
        $voice = $synth.GetInstalledVoices() | Where-Object { $_.Enabled -and $_.VoiceInfo.Culture.Name -eq 'ja-JP' } | Select-Object -First 1
        if (-not $voice) { throw "No Japanese voice (ja-JP) is installed for Windows text-to-speech." }
        $synth.SelectVoice($voice.VoiceInfo.Name)
        $format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000,
            [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
        $synth.SetOutputToWaveFile($path, $format)
        $synth.Speak($text)
        $synth.SetOutputToNull()
    }
    finally { $synth.Dispose() }
}
$speech = Join-Path $work 'speech-ja.wav'
$long = Join-Path $work 'speech-long.wav'
New-Speech $speech '今日の会議では、来週までに資料を準備することになりました。'
New-Speech $long ('本日の定例会議の議事録です。最初に先週の進捗を確認しました。次に来月のリリース計画について話し合いました。担当者はそれぞれの作業を金曜日までに完了させる予定です。' * 6)

# --- Suites ----------------------------------------------------------------------------
$suites = [ordered]@{
    'autosave'      = @()
    'recording'     = @('-FakeAudio', $speech)
    'devices'       = @('-FakeAudio', $speech)
    'levels'        = @('-FakeAudio', $speech)
    'transcription' = @('-Speech', $speech, '-LongSpeech', $long)
    'ui'            = @('-Speech', $speech)
    'instance'      = @()
    'perf'          = @('-Speech', $speech)
    'tabs'          = @('-Speech', $speech)
    'notelist'      = @()
    'chrome'        = @()
    'markdown'      = @()
    'appearance'    = @()
    'minutes'       = @('-Speech', $speech)
    'dictionary'    = @()
    'progress'      = @('-LongSpeech', $long)
    'export'        = @()
    'templates'     = @()
    'integration'   = @('-LongSpeech', $long)
}

$failed = 0
foreach ($name in $suites.Keys) {
    if ($Only -and $name -ne $Only) { continue }
    $out = Join-Path $work "$name.out"
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$(Join-Path $PSScriptRoot "$name-e2e.ps1")`"", '-NotesDir', "`"$notes`"") + ($suites[$name] | ForEach-Object { "`"$_`"" })
    # Each suite gets a time limit so a stuck UI Automation call can never block the run.
    $proc = Start-Process powershell -ArgumentList $argList -RedirectStandardOutput $out -NoNewWindow -PassThru
    if (-not $proc.WaitForExit($TimeoutMinutes * 60 * 1000)) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        # No Floating Memo was running before the tests (checked above), so these are test instances.
        Get-Process FloatingMemo -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
        Add-Content $out "ERROR: suite timed out after $TimeoutMinutes minutes"
    }
    $o = Get-Content $out
    $pass = ($o | Select-String -CaseSensitive ' PASS ').Count
    $fail = ($o | Select-String -CaseSensitive ' FAIL ').Count
    $err = ($o | Select-String -CaseSensitive '^ERROR').Count
    "{0,-14} PASS={1,3} FAIL={2} ERROR={3}" -f $name, $pass, $fail, $err
    $o | Select-String -CaseSensitive ' FAIL |^ERROR' | ForEach-Object { "    $($_.Line.Trim())" }
    $failed += $fail + $err
}
"Details: $work\<suite>.out"
exit ([int]($failed -gt 0))
