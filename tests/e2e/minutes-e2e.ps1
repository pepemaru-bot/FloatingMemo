param([string]$NotesDir, [string]$Speech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# AI minutes with the test stand-in model (--fake-llm, instant, deliberately invents an
# assignee). Checks: the transcript is never changed, minutes are a separate note, invented
# facts are removed, switching between the two, cancelling, and "no model" handling.

function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function ActiveTab { (TabElements | Where-Object { $_.Current.ItemStatus -eq '選択中' } | Select-Object -First 1).Current.Name }
function MinutesFiles { @(Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '*AI議事録*.md' -ErrorAction SilentlyContinue) }
function MenuItem([string]$id) {
  (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 150; $m = Find $id; if ($m) { return $m } }
  $null
}
function CloseMenu { if (Find 'MinutesCreate') { if (-not [K]::ComboInto($script:h, @(0x1B))) { throw "ABORT: focus left the test window" }; Start-Sleep -Milliseconds 300 } }
function Choose([string]$id) {
  $m = MenuItem $id; if (-not $m) { throw "menu item not found: $id" }
  $m.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400
}
function WaitIdle($sec = 120) {
  $deadline = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $deadline) { if ((Find "録音開始" -ByName) -ne $null) { return $true }; Start-Sleep -Milliseconds 250 }
  $false
}
# A note without a transcript asks first; "メモだけで作成" goes ahead.
function CreateAnyway {
  Choose 'MinutesCreate'; Start-Sleep -Milliseconds 500
  $anyway = Find 'MinutesWithoutTranscript'
  if ($anyway) { $anyway.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
}
function WaitInfo([string]$pattern, $sec = 20) {
  for ($i = 0; $i -lt $sec * 4; $i++) { $t = InfoBarText 'AudioInfoBar'; if ($t -match $pattern) { return $t }; Start-Sleep -Milliseconds 250 }
  InfoBarText 'AudioInfoBar'
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build -Extra @('--fake-llm') -NoDraft
  $source = ActiveTab
  SafeType "今日の定例会議でリリース日を決めました。担当者はまだ決めていません。"; Start-Sleep 1.5
  $sourceFile = Join-Path $NotesDir "notes\$source.md"
  $before = [IO.File]::ReadAllBytes($sourceFile)

  $toggle = MenuItem 'MinutesToggle'
  Check "no minutes yet -> switching is disabled" ($toggle -and -not $toggle.Current.IsEnabled)
  CloseMenu

  # Only typed notes, no transcript: the user is asked instead of quietly using the notes alone.
  Choose 'MinutesCreate'; Start-Sleep 1
  $msg = InfoBarText 'AudioInfoBar'
  Check "no transcript -> asked first, nothing created" ($msg -match '文字起こしがありません' -and (Find 'MinutesWithoutTranscript') -and (MinutesFiles).Count -eq 0) $msg
  $anyway = Find 'MinutesWithoutTranscript'
  if ($anyway) { $anyway.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  Start-Sleep -Milliseconds 300
  Check "progress shown, says the transcript is not changed" ((InfoBarText 'AudioInfoBar') -match 'AI議事録を作成中' -and (InfoBarText 'AudioInfoBar') -match '原文は変更されません') (InfoBarText 'AudioInfoBar')
  $done = WaitInfo '下書きを作成しました'
  Check "minutes created" ($done -match '下書きを作成しました') $done
  Check "minutes open as a separate note" ((ActiveTab) -eq "$source（AI議事録）") (ActiveTab)
  $file = MinutesFiles | Select-Object -First 1
  $text = if ($file) { [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8) } else { '' }
  Check "minutes saved as Markdown with the AI-draft notice and a link to the source" ($text -match 'AI下書き' -and $text.Contains("[[$source]]")) (Esc $text.Substring(0, [Math]::Min(200, $text.Length)))
  # The test model gets the fact-first layout (only Qwen3 1.7B keeps the seven fixed headings).
  Check "fact-first layout: overview, a topic, marks, source numbers; no fixed template" ($text.Contains('## 概要') -and $text.Contains('【決定】') -and $text.Contains('- [ ] 確認') -and $text -match '〔[AB]\d{3}〕' -and $text.Contains('## 原文（番号付き）') -and -not $text.Contains('## 次回確認事項'))
  Check "invented assignee removed -> 未確認" (-not $text.Contains('架空太郎') -and $text.Contains('担当: 未確認') -and $text.Contains('AIの推測'))
  Check "transcript note unchanged (byte for byte)" ([Convert]::ToBase64String([IO.File]::ReadAllBytes($sourceFile)) -eq [Convert]::ToBase64String($before))

  $create = MenuItem 'MinutesCreate'
  Check "minutes note cannot be summarized again" ($create -and -not $create.Current.IsEnabled)
  CloseMenu
  Choose 'MinutesToggle'
  Check "toggle -> transcript" ((ActiveTab) -eq $source) (ActiveTab)
  Choose 'MinutesToggle'
  Check "toggle -> minutes" ((ActiveTab) -eq "$source（AI議事録）") (ActiveTab)
  Choose 'MinutesToggle'

  # Cancel: nothing new is saved, the transcript stays.
  $count = (MinutesFiles).Count
  CreateAnyway
  Invoke-Button '中止'
  $msg = WaitInfo '中止しました' 10
  Check "cancel -> stopped, transcript kept" ($msg -match '中止しました' -and $msg -match '原文はそのまま') $msg
  Start-Sleep 2
  Check "cancel -> no extra minutes note" ((MinutesFiles).Count -eq $count)
  CloseWindow; [void](WaitExit 20)

  # No model installed: explained, nothing created.
  Launch -Extra @('--llm-model', (Join-Path $env:TEMP 'no-such-model.gguf'))
  Invoke-Button $source; Start-Sleep 1
  CreateAnyway; Start-Sleep 1
  $msg = InfoBarText 'AudioInfoBar'
  Check "no model -> explained where to put it" ($msg -match 'AIモデルがありません' -and $msg -match 'Qwen3') $msg
  Check "no model -> nothing created" ((MinutesFiles).Count -eq $count)
  CloseWindow; [void](WaitExit 20)

  # Recording, then minutes: refused while recording (the transcript is not in the note yet),
  # and after transcription the transcript is what the model gets (made-up speech).
  Launch -Extra @('--fake-llm', '--fake-audio', $Speech)
  SafeKeys 0x11, 0x4E; Start-Sleep 1   # Ctrl+N: a fresh note
  SafeType "- 自分のメモ: 架空の定例"; Start-Sleep 1
  $recorded = ActiveTab
  Invoke-Button "録音開始"; Start-Sleep 1
  Choose 'MinutesCreate'; Start-Sleep 1
  $msg = InfoBarText 'AudioInfoBar'
  $minutesBefore = (MinutesFiles).Count
  Check "while recording -> refused (transcript not added yet)" ($msg -match '録音と文字起こしが終わってから') $msg
  Invoke-Button "録音停止"
  Check "transcription finished" (WaitIdle 120)
  Start-Sleep 2
  Choose 'MinutesCreate'
  $done = WaitInfo '下書きを作成しました' 30
  Check "after transcription -> minutes created without asking" ($done -match '下書きを作成しました' -and -not (Find 'MinutesWithoutTranscript')) $done
  $file = MinutesFiles | Sort-Object LastWriteTime | Select-Object -Last 1
  $text = if ($file) { [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8) } else { '' }
  # The test model writes the first sentence of the transcript it was given.
  Check "the model got the transcript, not only the typed notes" ((MinutesFiles).Count -eq $minutesBefore + 1 -and $text -match '資料を準備') (Esc $text.Substring(0, [Math]::Min(300, $text.Length)))
  $log = Join-Path $env:TEMP 'FloatingMemo\minutes-diagnostics.log'
  $last = if (Test-Path $log) { (Get-Content $log -Encoding UTF8 | Select-Object -Last 1) } else { '' }
  Check "diagnostics: blocks=1, nothing unprocessed, no note text" ($last -match 'blocks=1' -and $last -match 'unprocessed=none' -and $last -notmatch '資料' -and $last -notmatch '自分のメモ') $last
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
