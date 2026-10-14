param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Window chrome and typing details found in real use: many tabs must not push "+"/the note list
# under the caption buttons; maximize/restore; one Enter = one line break.

function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function ActiveTabElement { TabElements | Where-Object { $_.Current.ItemStatus -eq '選択中' } | Select-Object -First 1 }
function Rect($e) { $e.Current.BoundingRectangle }
function WindowRect { $script:root.Current.BoundingRectangle }
function Reset { SafeKeys 0x11, 0x41; SafeKeys 0x2E; Start-Sleep -Milliseconds 300 }
function E { SafeKeys 0x0D }
function Typed([string]$name, [scriptblock]$steps, [string]$expected) {
  Reset; & $steps; Start-Sleep -Milliseconds 500
  $v = (Value) -replace "`r", "`n"
  Check $name ($v -eq $expected) (Esc $v)
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build

  # --- Enter never adds an extra blank line ------------------------------------------------
  Typed "Enter in text: one line break" { SafeType 'abc'; E; SafeType 'def' } "abc`ndef"
  Typed "Enter continues a list" { SafeType '- a'; E; SafeType 'b' } "- a`n- b"
  Typed "Enter on an empty item ends the list" { SafeType '- a'; E; E; SafeType 'x' } "- a`nx"
  Typed "loose list: no extra blank line" { SafeType '- a'; E; E; E; SafeType '- b'; E; SafeType 'c' } "- a`n`n- b`n- c"
  Typed "numbered list continues" { SafeType '1. a'; E; SafeType 'b' } "1. a`n2. b"
  Typed "quote continues" { SafeType '> q'; E; SafeType 'x' } "> q`n> x"
  Reset

  # --- Many tabs: only the tabs scroll ------------------------------------------------------
  for ($i = 0; $i -lt 9; $i++) { SafeKeys 0x11, 0x4E; Start-Sleep -Milliseconds 400; SafeType "タブ$i" }
  Start-Sleep 1
  $win = WindowRect; $list = Rect (Find 'NoteListButton'); $new = Rect (Find 'NewNoteButton')
  Check "note list button stays clear of the caption buttons" ($list.Right -le $win.Right - 130) "list.Right=$($list.Right) window.Right=$($win.Right)"
  Check "+ and note list visible next to the tabs" ($new.Width -gt 0 -and $list.Left -ge $new.Right - 1)
  $active = Rect (ActiveTabElement); $scroller = $null
  Check "active (newest) tab scrolled into view" ($active.Right -le $new.Left + 1 -and $active.Width -gt 20) "active=$active new=$new"
  SafeKeys 0x11, 0x10, 0x09; Start-Sleep 0.5; SafeKeys 0x11, 0x10, 0x09; Start-Sleep 0.5
  $active2 = Rect (ActiveTabElement)
  Check "switching tabs keeps the active one visible" ($active2.Left -ge $win.Left -and $active2.Right -le $new.Left + 1) "$active2"

  # --- Maximize / restore -------------------------------------------------------------------
  $pattern = $script:root.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern)
  Check "window can be maximized" ($pattern.Current.CanMaximize)
  Check "window can be minimized" ($pattern.Current.CanMinimize)
  $before = WindowRect
  $pattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Maximized); Start-Sleep 1
  $max = WindowRect
  Check "maximize enlarges the memo" ($max.Width -gt $before.Width + 100) "before=$before max=$max"
  $list = Rect (Find 'NoteListButton')
  Check "maximized: buttons still clear of the caption buttons" ($list.Right -le $max.Right - 130)
  $pattern.SetWindowVisualState([Windows.Automation.WindowVisualState]::Normal); Start-Sleep 1
  $after = WindowRect
  Check "restore returns to the previous size" ([Math]::Abs($after.Width - $before.Width) -lt 4 -and [Math]::Abs($after.Height - $before.Height) -lt 4) "after=$after"
  EnsureFocus; SafeType "!"; Start-Sleep 1
  Check "typing works after restore" ((Value) -match '!$') (Esc (Value))
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
