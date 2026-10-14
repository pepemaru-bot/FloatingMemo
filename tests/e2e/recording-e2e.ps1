param([string]$NotesDir, [string]$FakeAudio, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  # Point at a missing model: transcription then fails and the recording is kept, so its WAV can be inspected.
  $noModel = Join-Path $env:TEMP 'fm-e2e-no-model.bin'
  Launch -Build -Extra @('--fake-audio', $FakeAudio, '--model', $noModel)

  Check "record button present (idle)" ((Find "録音開始" -ByName) -ne $null)
  Check "mic toggle on by default" ((Toggle-State "マイク") -eq 'On')
  Check "PC audio toggle off by default" ((Toggle-State "PC音声") -eq 'Off')
  Check "not recording at startup" (-not (IsShown "RecordingStatusText"))

  # No sources -> warning, nothing recorded
  Set-Toggle "マイク" 'Off'
  Invoke-Button "録音開始"; Start-Sleep -Milliseconds 500
  Check "no source selected -> warning" ((InfoBarText "AudioInfoBar") -match "録音する音声を選んでください") (InfoBarText "AudioInfoBar")
  Check "no source -> still idle" (-not (IsShown "RecordingStatusText"))
  Set-Toggle "マイク" 'On'; Set-Toggle "PC音声" 'On'

  # Start recording (mic + PC audio, both fed from the fake WAV)
  Invoke-Button "録音開始"; Start-Sleep -Milliseconds 2300
  Check "recording indicator shown" (IsShown "RecordingStatusText")
  $status = (Find "RecordingStatusText").Current.Name
  Check "elapsed time shown" ($status -match '^録音中 00:0[1-9]') $status
  Check "title says recording" ((WindowTitle) -match '録音中') (WindowTitle)
  Check "stop button shown" ((Find "録音停止" -ByName) -ne $null)
  Check "toggles locked while recording" (-not (Find "マイク" -ByName).Current.IsEnabled)

  # Esc while recording -> minimized (still in taskbar), not hidden
  SafeKeys 0x1B; Start-Sleep 1
  Check "Esc while recording minimizes instead of hiding" ([K]::IsWindowVisible($script:h) -and [W32.Win]::IsIconic($script:h))
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1
  Check "hotkey restores minimized window" (-not [W32.Win]::IsIconic($script:h) -and (OurEditorFocused))
  SafeType "録音中のメモ"; Start-Sleep -Milliseconds 300
  Check "typing works while recording" ((Value) -eq "録音中のメモ") (Value)

  # Ctrl+R stops
  SafeKeys 0x11, 0x52; Start-Sleep 3
  $info = InfoBarText "AudioInfoBar"
  Check "stop -> transcription attempted (no model in this test)" ($info -match 'Whisperモデル' -and $info -match 'がありません') $info
  $wav = (Get-ChildItem (Join-Path $env:TEMP 'FloatingMemo\recordings') -Recurse -Filter recording.wav -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1).FullName
  Check "recording.wav exists" ($wav -and (Test-Path $wav)) $wav
  if ($wav -and (Test-Path $wav)) {
    $wi = WavInfo $wav
    Check "wav is 16 kHz mono 16-bit" ($wi.Rate -eq 16000 -and $wi.Channels -eq 1 -and $wi.Bits -eq 16) ($wi | Out-String).Trim()
    Check "wav has audio data" ($wi.Bytes -gt 100000) $wi.Bytes
  }
  Check "back to idle after stop" (-not (IsShown "RecordingStatusText") -and (Find "録音開始" -ByName) -ne $null)
  Check "title restored" ((WindowTitle) -notmatch '録音中')
  Start-Sleep 1
  Check "memo untouched by failed transcription, typed text autosaved" ((FileText) -eq "録音中のメモ") (Esc (FileText))

  # Exit during recording -> confirmation; Yes discards and exits
  Invoke-Button "録音開始"; Start-Sleep 1
  Check "new recording supersedes audio kept for retry" (-not (Test-Path $wav))
  $sessionsBefore = if ($wav) { Get-ChildItem (Split-Path (Split-Path $wav)) -Directory | Measure-Object | % Count } else { 0 }
  CloseWindow; Start-Sleep 2
  $dlg = FindDialog
  Check "exit while recording asks confirmation" ($dlg -ne $null)
  if ($dlg) { ClickDialogButton $dlg "6" }  # IDYES
  Check "app exits after confirming" (WaitExit)
  if ($wav) {
    $sessionsAfter = Get-ChildItem (Split-Path (Split-Path $wav)) -Directory | Measure-Object | % Count
    Check "discarded recording deleted" ($sessionsAfter -eq $sessionsBefore - 1) "$sessionsBefore -> $sessionsAfter"
  }

  # Restart deletes recordings left behind by a crash
  $leftover = Join-Path $env:TEMP 'FloatingMemo\recordings\rec-e2e-leftover'
  New-Item -ItemType Directory -Force $leftover | Out-Null; Set-Content (Join-Path $leftover 'Microphone.wav') 'x'
  Launch -Extra @('--fake-audio', $FakeAudio, '--model', $noModel)
  Check "leftover recordings cleaned at startup" (-not (Test-Path $leftover))
  CloseWindow; [void](WaitExit)
}
catch { "ERROR: $_" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}