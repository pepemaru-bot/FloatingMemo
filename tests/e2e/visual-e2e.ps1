param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Experimental visual editing (Milkdown) inside the real app.
# Not covered here (needs a person): the Japanese IME itself, and clicking checkboxes with the mouse.
# Transcript insertion while typing is checked in the browser (visual.js keeps unreported keystrokes).

$settingsFile = Join-Path (Split-Path $NotesDir) 'e2e-settings.json'
function Text { (Value) -replace "`r", "`n" }
function SetVisual([bool]$on) {
  $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
  $json | Add-Member -NotePropertyName VisualEditor -NotePropertyValue $on -Force
  [IO.File]::WriteAllText($settingsFile, ($json | ConvertTo-Json -Depth 10), [Text.UTF8Encoding]::new($false))
}
function Restart { CloseWindow; [void](WaitExit 20); Launch }

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null
  $note = "# 会議メモ`n`n- [ ] 資料を送る`n- [x] 会場を予約`n`n| 項目 | 担当 |`n| --- | --- |`n| 受付 | 山本 |`n`n[[関連ノート]] を参照`n"
  [IO.File]::WriteAllText($file, $note, [Text.UTF8Encoding]::new($false))

  # --- Default: the Markdown source editor ----------------------------------------------
  Launch -Build
  Check "source editor is the default" ((EditorKind) -eq 'web') (EditorKind)
  CloseWindow; [void](WaitExit 20)

  # --- Switched on: the visual page is used after a restart ---------------------------------
  SetVisual $true
  Launch
  Check "visual editor in use after enabling it" ((EditorKind) -eq 'visual') (EditorKind)
  Start-Sleep 2
  Check "opening a note does not change the file" ((FileText) -eq $note) (Esc (FileText))

  # --- Editing keeps tables, task boxes and [[links]] as Markdown ---------------------------
  SafeKeys 0x11, 0x23; SafeType "追記"; Start-Sleep 2
  $t = FileText
  Check "typed text autosaved" ($t -match '追記') (Esc $t)
  Check "task boxes kept" ($t -match '- \[ \] 資料を送る' -and $t -match '- \[x\] 会場を予約') (Esc $t)
  Check "table kept as a Markdown table" ($t -match '\|\s*受付\s*\|\s*山本\s*\|') (Esc $t)
  Check "[[link]] not escaped" ($t -match '\[\[関連ノート\]\]' -and $t -notmatch '\\\[') (Esc $t)
  Check "saved as plain Markdown (no HTML)" ($t -notmatch '<(div|span|p|br|table)\b') (Esc $t)

  # --- Undo / redo ------------------------------------------------------------------------------
  SafeKeys 0x11, 0x5A; Start-Sleep 2
  Check "Ctrl+Z undoes" ((FileText) -notmatch '追記') (Esc (FileText))
  SafeKeys 0x11, 0x59; Start-Sleep 2
  Check "Ctrl+Y redoes" ((FileText) -match '追記') (Esc (FileText))

  # --- Long input ---------------------------------------------------------------------------------
  $long = '長い入力の確認です。' * 40
  SafeKeys 0x0D; SafeType $long; Start-Sleep 2
  Check "long input saved completely" ((FileText).Contains($long)) ("length " + (FileText).Length)

  # --- New note and switching back --------------------------------------------------------------
  SafeKeys 0x11, 0x4E; Start-Sleep 1
  SafeType "別のノート"; Start-Sleep 1.5
  Check "new note edited in the visual editor" ((Text).Trim() -eq '別のノート') (Esc (Text))
  SafeKeys 0x11, 0x10, 0x09; Start-Sleep 1.5
  Check "switching back shows the first note intact" ((Text).Contains($long) -and (Text).Contains('追記')) (Esc ((Text).Substring(0, [Math]::Min(60, (Text).Length))))
  Check "switching notes did not change the first note's file" ((FileText).Contains($long)) ""

  # --- Exit keeps everything ------------------------------------------------------------------------
  $before = FileText
  CloseWindow
  Check "app exits" (WaitExit 20)
  Check "file unchanged by exiting" ((FileText) -eq $before) ""

  # --- Syntax the visual editor would change: shown as Markdown text, never rewritten ---------
  $unusual = "見出し`n===`n`n本文  `n次の行`n`n1) 括弧つき番号`n"
  [IO.File]::WriteAllText($file, $unusual, [Text.UTF8Encoding]::new($false))
  Launch
  Start-Sleep 2
  Check "unusual note opens unchanged" ((FileText) -eq $unusual) (Esc (FileText))
  SafeKeys 0x11, 0x23; SafeType "追記"; Start-Sleep 2
  Check "editing it as text keeps every byte" ((FileText) -eq ($unusual + "追記")) (Esc (FileText))

  # --- The setting can be switched off in the settings window --------------------------------------
  $win = $null
  for ($try = 0; $try -lt 4 -and -not $win; $try++) {
    (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
    $item = Find 'MinutesSettings'; if ($item) { $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    $desk = [Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    for ($i = 0; $i -lt 10 -and -not $win; $i++) { Start-Sleep -Milliseconds 300; $win = $desk.FindAll([Windows.Automation.TreeScope]::Children, $cond) | Where-Object { $_.Current.Name -eq 'Floating Memo の設定' } | Select-Object -First 1 }
  }
  function In($id) { $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
  $toggle = In 'VisualToggle'
  if (-not $toggle) {
    foreach ($section in @('SettingsAppearance')) {
      $s = In $section; if ($s) { $s.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 0.5 }
      $toggle = In 'VisualToggle'; if ($toggle) { break }
    }
  }
  Check "visual editing toggle shown and on" ($toggle -and $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On')
  if ($toggle) { $toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1 }
  $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
  Check "switching it off is saved" ($json.VisualEditor -eq $false) "VisualEditor=$($json.VisualEditor)"
  $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
  for ($f = 0; $f -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $f++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }

  Restart
  Check "source editor back after a restart" ((EditorKind) -eq 'web') (EditorKind)
  Check "note still intact" ((FileText) -eq ($unusual + "追記")) (Esc (FileText))
  CloseWindow
  Check "app exits at the end" (WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
