param([string]$NotesDir, [string]$Speech, [string]$LongSpeech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
$recordings = Join-Path $env:TEMP 'FloatingMemo\recordings'
function Sessions { if (Test-Path $recordings) { @(Get-ChildItem $recordings -Directory -Filter 'rec-*') } else { @() } }
function WaitIdle($sec = 120) {
  $deadline = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $deadline) { if ((Find "録音開始" -ByName) -ne $null) { return $true }; Start-Sleep -Milliseconds 250 }
  $false
}
function StatusText { $e = Find "RecordingStatusText"; if ($e) { $e.Current.Name } else { "" } }
try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }

  # --- 1. Record -> local Whisper -> appended at the end under the header -> autosaved ---
  Launch -Build -Extra @('--fake-audio', $Speech)
  SafeType "前の行"
  Invoke-Button "録音開始"; Start-Sleep 1
  Invoke-Button "録音停止"
  $seen = @(); $deadline = (Get-Date).AddSeconds(60)
  while ((Get-Date) -lt $deadline -and -not (Find "録音開始" -ByName)) { $st = StatusText; if ($st -and $seen -notcontains $st) { $seen += $st }; Start-Sleep -Milliseconds 100 }
  Check "shows transcription progress" (($seen -join ',') -match '文字起こし中|音声モデルを準備中') ($seen -join ', ')
  Check "back to idle after transcription" ((Find "録音開始" -ByName) -ne $null)
  $v = Value
  Check "transcript appended at the end under the header" ($v -match "^前の行`r`r以下、AIによる文字起こし`r.*会議.*資料") (Esc $v)
  Start-Sleep 1
  Check "transcript autosaved to Markdown" ((FileText) -match "^前の行`n`n以下、AIによる文字起こし`n.*会議.*資料") (Esc (FileText))
  Check "temporary audio deleted after success" ((Sessions).Count -eq 0) ((Sessions) -join ',')
  Check "no error shown" (-not ((InfoBarText "AudioInfoBar") -match '文字起こしできません'))

  # Second take with the caret at the very start: still appended at the end, caret stays.
  SafeKeys 0x11, 0x24; Start-Sleep -Milliseconds 300   # Ctrl+Home
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  Check "second transcription finishes" (WaitIdle 60)
  $v2 = Value
  Check "second transcript appended after the first, never at the caret" ($v2.StartsWith($v) -and ([regex]::Matches($v2, '以下、AIによる文字起こし')).Count -eq 2) (Esc $v2)
  Check "editor focus kept" (OurEditorFocused)
  SafeType "★"; Start-Sleep -Milliseconds 500
  Check "caret did not move (typing continues at the start)" ((Value).StartsWith("★前の行")) (Esc (Value))
  SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 500   # Ctrl+Z: the typed star
  SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 500   # Ctrl+Z: the transcript
  Check "Ctrl+Z undoes the last transcript insertion" ((Value) -eq $v) (Esc (Value))
  CloseWindow; [void](WaitExit)

  # --- 2. Cancel a long transcription -> memo untouched, retry works ---------------------
  Launch -Extra @('--fake-audio', $LongSpeech)
  $before = Value
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  $deadline = (Get-Date).AddSeconds(20)
  while ((Get-Date) -lt $deadline -and -not (Find "文字起こしを中止" -ByName)) { Start-Sleep -Milliseconds 100 }
  Check "cancel button available while transcribing" ((Find "文字起こしを中止" -ByName) -ne $null)
  Start-Sleep -Milliseconds 500
  Invoke-Button "文字起こしを中止"
  Check "cancel returns to idle" (WaitIdle 30)
  Check "cancel message with retry" (((InfoBarText "AudioInfoBar") -match '文字起こしを中止しました') -and (Find "文字起こしを再試行" -ByName)) (InfoBarText "AudioInfoBar")
  Check "memo untouched after cancel" ((Value) -eq $before)
  Check "audio kept for retry" ((Sessions).Count -eq 1)
  Invoke-Button "文字起こしを再試行"
  Check "retry finishes" (WaitIdle 180)
  $v3 = Value
  Check "retry inserts transcript" ($v3.Length -gt $before.Length + 50 -and $v3 -match '会議') ("+" + ($v3.Length - $before.Length) + " chars")
  Check "audio deleted after successful retry" ((Sessions).Count -eq 0)
  CloseWindow; [void](WaitExit)

  # --- 3. Missing model -> clear error, memo untouched, audio kept, exit cleans up -------
  $missing = Join-Path $NotesDir 'no-model.bin'
  Launch -Extra @('--fake-audio', $Speech, '--model', $missing)
  $before = Value
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  Check "missing model returns to idle" (WaitIdle 30)
  $info = InfoBarText "AudioInfoBar"
  Check "missing model error is short and points to the README steps" ($info -match 'Whisperモデル' -and $info -match 'README') $info
  $help = (Find "AudioInfoBar").Current.HelpText
  Check "details (model path) available as tooltip/help text" ($help -match [regex]::Escape($missing)) $help
  Check "memo untouched on failure" ((Value) -eq $before)
  Check "audio kept for retry on failure" ((Sessions).Count -eq 1)
  Check "retry button offered" ((Find "文字起こしを再試行" -ByName) -ne $null)
  CloseWindow
  Check "exit with pending audio closes normally" (WaitExit)
  Check "pending audio deleted on exit" ((Sessions).Count -eq 0)
}
catch { "ERROR: $_" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
