param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Minutes templates with the test stand-in model (--fake-llm): import the made-up sample
# (docs/templates), choose it in the minutes menu, and check that the minutes follow it. The
# test build's "--import-template <file>" runs the import button's code (the system file dialog
# cannot be automated reliably). Made-up content only.

$sample = (Resolve-Path (Join-Path $PSScriptRoot '..\..\docs\templates\sample-project-meeting.md')).Path
function MinutesFiles { @(Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '*AI議事録*.md' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime) }
function SettingsWindow {
  $desk = [Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Floating Memo の設定')))
  for ($i = 0; $i -lt 30; $i++) { $w = $desk.FindFirst([Windows.Automation.TreeScope]::Children, $cond); if ($w) { return $w }; Start-Sleep -Milliseconds 200 }
  $null
}
# Esc only while a menu is open (an extra Esc would hide the memo window).
function CloseMenus {
  for ($i = 0; $i -lt 3 -and ((Find 'MinutesCreate') -or (Find 'MinutesFormatAuto')); $i++) { [K]::Combo(0x1B); Start-Sleep -Milliseconds 300 }
}
function OpenMenu {
  (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 600
}
function MenuInvoke([string]$idOrName, [switch]$ByName) {
  for ($i = 0; $i -lt 20; $i++) { $m = if ($ByName) { Find $idOrName -ByName } else { Find $idOrName }; if ($m) { break }; Start-Sleep -Milliseconds 150 }
  if (-not $m) { throw "menu item not found: $idOrName" }
  # A submenu ("議事録の形式") offers no pattern: focus it and press → like a keyboard user.
  $p = $null
  if ($m.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() }
  elseif ($m.TryGetCurrentPattern([Windows.Automation.TogglePattern]::Pattern, [ref]$p)) { if ($p.Current.ToggleState -ne 'On') { $p.Toggle() } }
  elseif ($m.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand() }
  else {
    if ($m.Current.ProcessId -ne $script:app.Id) { throw "ABORT: menu item is not in the test app" }
    $m.SetFocus(); Start-Sleep -Milliseconds 200; [K]::Combo(0x27)
  }
  Start-Sleep -Milliseconds 500
}
function DismissDialogs($win) {
  # The "読み込みました" message in the settings window.
  $ok = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'OK')))
  if ($ok) { $ok.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300 }
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build -Extra @('--fake-llm', '--import-template', $sample) -NoDraft
  $store = Join-Path $NotesDir '.floatingmemo\templates'
  Check "no templates until the user imports one" (-not (Test-Path $store))

  SafeType "リリース日は十一月一日に決まりました。資料の準備は田中さんが金曜日までに行います。"; Start-Sleep 1.5

  # Settings opens and the test hook imports the sample (same code as the import button).
  OpenMenu; MenuInvoke 'MinutesSettings'
  $win = SettingsWindow
  Start-Sleep 2
  $files = @(Get-ChildItem $store -Filter *.json -ErrorAction SilentlyContinue)
  Check "imported into the notes' private folder as JSON" ($files.Count -eq 1) $store
  $json = if ($files.Count -eq 1) { [IO.File]::ReadAllText($files[0].FullName, [Text.Encoding]::UTF8) } else { '' }
  Check "markdown headings and rules kept (omit / draft)" ($json.Contains('"title": "進捗報告"') -and $json.Contains('"whenMissing": "省略"') -and $json.Contains('"kind": "draft"')) $files[0].Name
  if ($win) { DismissDialogs $win; $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1 }
  for ($f = 0; $f -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $f++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }

  # Choose the template in the minutes menu, then make minutes.
  OpenMenu; MenuInvoke 'MinutesFormat'; MenuInvoke 'プロジェクト定例（サンプル）' -ByName
  CloseMenus
  $prefs = [IO.File]::ReadAllText((Join-Path (Split-Path $NotesDir) 'e2e-settings.json'), [Text.Encoding]::UTF8)
  Check "chosen template saved" ($prefs -match '"MinutesTemplateId":\s*"t-') ($prefs -split "`n" | Select-String MinutesTemplateId)
  OpenMenu; MenuInvoke 'MinutesCreate'
  for ($i = 0; $i -lt 20 -and -not ($anyway = Find 'MinutesWithoutTranscript'); $i++) { Start-Sleep -Milliseconds 150 }
  if ($anyway) { $anyway.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  for ($i = 0; $i -lt 80 -and -not ((InfoBarText 'AudioInfoBar') -match '下書きを作成しました'); $i++) { Start-Sleep -Milliseconds 250 }
  $file = MinutesFiles | Select-Object -Last 1
  $text = if ($file) { [IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8) } else { '' }
  Check "minutes created with the template" ($text -match 'AI下書き') (InfoBarText 'AudioInfoBar')
  $order = @('## 概要', '## 進捗報告', '## 決定事項', '## ToDo', '## 次回予定', '## 共有用の短い報告文') | ForEach-Object { $text.IndexOf($_) }
  Check "the template's headings, in its order" (($order -notcontains -1) -and (($order | Sort-Object) -join ',') -eq ($order -join ',')) ($order -join ',')
  Check "empty section with 省略 left out" (-not $text.Contains('## 課題・リスク'))
  Check "draft section marked as a draft" ($text.Contains('送信前の下書きです'))
  Check "invented assignee still removed" (-not $text.Contains('架空太郎'))

  # Back to おまかせ: the model's own layout again.
  OpenMenu; MenuInvoke 'MinutesFormat'; MenuInvoke 'MinutesFormatAuto'; CloseMenus
  $prefs = [IO.File]::ReadAllText((Join-Path (Split-Path $NotesDir) 'e2e-settings.json'), [Text.Encoding]::UTF8)
  Check "おまかせ saved (no template id)" ($prefs -match '"MinutesTemplateId":\s*null') $prefs
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
