param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
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

  Choose 'MinutesCreate'
  Check "progress shown, says the transcript is not changed" ((InfoBarText 'AudioInfoBar') -match 'AI議事録を作成中' -and (InfoBarText 'AudioInfoBar') -match '原文は変更されません') (InfoBarText 'AudioInfoBar')
  $done = WaitInfo '下書きを作成しました'
  Check "minutes created" ($done -match '下書きを作成しました') $done
  Check "minutes open as a separate note" ((ActiveTab) -eq "$source（AI議事録）") (ActiveTab)
  $file = MinutesFiles | Select-Object -First 1
  $text = if ($file) { [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8) } else { '' }
  Check "minutes saved as Markdown with the AI-draft notice and a link to the source" ($text -match 'AI下書き' -and $text.Contains("[[$source]]")) (Esc $text.Substring(0, [Math]::Min(200, $text.Length)))
  Check "all seven sections present" (@('会議の概要', '主な議題', '議論の要点', '決定事項', 'ToDo（担当者・期限）', '未決事項', '次回確認事項' | Where-Object { $text.Contains("## $_") }).Count -eq 7)
  Check "invented assignee removed -> 未確認" (-not $text.Contains('架空太郎') -and $text.Contains('担当: 未確認（AIの推測を除去）'))
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
  Choose 'MinutesCreate'; Start-Sleep -Milliseconds 300
  Invoke-Button '中止'
  $msg = WaitInfo '中止しました' 10
  Check "cancel -> stopped, transcript kept" ($msg -match '中止しました' -and $msg -match '原文はそのまま') $msg
  Start-Sleep 2
  Check "cancel -> no extra minutes note" ((MinutesFiles).Count -eq $count)
  CloseWindow; [void](WaitExit 20)

  # No model installed: explained, nothing created.
  Launch -Extra @('--llm-model', (Join-Path $env:TEMP 'no-such-model.gguf'))
  Invoke-Button $source; Start-Sleep 1
  Choose 'MinutesCreate'; Start-Sleep 1
  $msg = InfoBarText 'AudioInfoBar'
  Check "no model -> explained where to put it" ($msg -match 'AIモデルがありません' -and $msg -match 'Qwen3') $msg
  Check "no model -> nothing created" ((MinutesFiles).Count -eq $count)
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
