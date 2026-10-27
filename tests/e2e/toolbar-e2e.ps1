param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Toolbar: hover hints close when the pointer leaves; the table size boxes take mouse and keys.
# Real mouse moves and clicks, only inside the test window. Made-up note.
Add-Type -Namespace M -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
public struct RECT { public int Left, Top, Right, Bottom; }
'@
function Text { (Value) -replace "`r", "`n" }
function NewNote { SafeKeys 0x11, 0x4E; Start-Sleep -Milliseconds 800 }
function Center($element) { $r = $element.Current.BoundingRectangle; @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
# A point in the memo text area of the test window (below the tabs, above the control strip).
function EditorPoint { $w = New-Object M.Mouse+RECT; [void][M.Mouse]::GetWindowRect($script:h, [ref]$w); @([int](($w.Left + $w.Right) / 2), [int]($w.Top + ($w.Bottom - $w.Top) * 0.4)) }
function InsideTestWindow($x, $y) { $w = New-Object M.Mouse+RECT; [void][M.Mouse]::GetWindowRect($script:h, [ref]$w); $x -gt $w.Left -and $x -lt $w.Right -and $y -gt $w.Top -and $y -lt $w.Bottom }
function MoveTo($p) { if (-not (InsideTestWindow $p[0] $p[1])) { throw "ABORT: point outside the test window" }; [void][M.Mouse]::SetCursorPos($p[0] - 3, $p[1]); for ($i = 0; $i -lt 3; $i++) { Start-Sleep -Milliseconds 30; [M.Mouse]::mouse_event(0x1, 1, 0, 0, [UIntPtr]::Zero) } }  # real movement: XAML hears pointer events
function ClickAt($p) { MoveTo $p; Start-Sleep -Milliseconds 100; [M.Mouse]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); [M.Mouse]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero) }
# The hint's own text on screen (the button itself is named "録音開始"; its help text is not an element).
function TipShown([string]$text) {
  # WinUI shows a tooltip in its own popup window: look through every window of the test app.
  $desk = [Windows.Automation.AutomationElement]::RootElement
  $mine = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
  $named = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $text)
  foreach ($w in $desk.FindAll([Windows.Automation.TreeScope]::Children, $mine)) {
    foreach ($e in $w.FindAll([Windows.Automation.TreeScope]::Subtree, $named)) { if ($e.Current.ControlType.ProgrammaticName -ne 'ControlType.Button') { return $true } }
  }
  $false
}
function SettingsWindow {
  $desk = [Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Floating Memo の設定')))
  for ($i = 0; $i -lt 30; $i++) { $w = $desk.FindFirst([Windows.Automation.TreeScope]::Children, $cond); if ($w) { return $w }; Start-Sleep -Milliseconds 200 }
  $null
}
function WaitFor([scriptblock]$condition, [int]$seconds = 10) { for ($i = 0; $i -lt $seconds * 4; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }; $false }

function Bounds($id) { (Find $id).Current.BoundingRectangle }
function CenterOf($id) { $r = Bounds $id; @([int]($r.X + $r.Width / 2), [int]($r.Y + $r.Height / 2)) }
function Typed([string]$s) { if (-not [K]::TypeInto($script:h, $s)) { throw "ABORT: focus left the test window" } }

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build
  SafeType "表とツールチップの確認"

  # --- Hover hints of the toolbar: shown on hover, gone when the pointer leaves -----------------
  $tips = [ordered]@{
    'SourceModeButton' = 'ソース編集：Markdown を直接編集'
    'VisualModeButton' = 'ビジュアル編集：見たままの形で編集'
    'PreviewModeButton' = 'プレビュー：読み取り専用で表示 (Ctrl+Alt+P で切り替え)'
    'TableButton' = '表を挿入'
    'LinkButton' = 'リンクを挿入・編集 (Ctrl+K)'
  }
  foreach ($id in $tips.Keys) {
    $tip = $tips[$id]
    MoveTo (CenterOf $id)
    $shown = WaitFor { (TipShown $tip) -or (Mirror 'HoverTipState') -eq $tip } 4
    MoveTo (EditorPoint)
    $gone = WaitFor { -not (TipShown $tip) } 3
    Check "$id`: hint shown on hover, gone after leaving into the memo" ($shown -and $gone) "shown=$shown gone=$gone state=$(Mirror 'HoverTipState')"
    MoveTo (CenterOf $id); [void](WaitFor { TipShown $tip } 4)
    $r = Bounds 'ModeSwitch'
    MoveTo @([int]($r.X - 40), [int]($r.Y + $r.Height / 2))   # the bar beside the buttons
    $gone = WaitFor { -not (TipShown $tip) } 3
    Check "$id`: gone after leaving along the bar" $gone "state=$(Mirror 'HoverTipState')"
  }

  # --- Table size: the number boxes take mouse and keyboard ------------------------------------------
  ClickAt (CenterOf 'TableButton')
  [void](WaitFor { (Find 'TableColsBox') -ne $null } 5)
  Check "table picker opens with a click" ($null -ne (Find 'TableColsBox'))
  ClickAt (CenterOf 'TableColsBox'); Start-Sleep 0.5
  [K]::Combo(0x11, 0x41); Typed "5"; [K]::Combo(0x09); Start-Sleep 0.5
  $cols = (Find 'TableColsBox').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current.Value
  Check "columns typed with the keyboard (5)" ($cols -eq 5) "value=$cols"
  ClickAt (CenterOf 'TableRowsBox'); Start-Sleep 0.5
  [K]::Combo(0x26); [K]::Combo(0x26); Start-Sleep 0.5    # Up arrow twice: 3 -> 5
  $rows = (Find 'TableRowsBox').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current.Value
  Check "rows changed with the arrow keys (5)" ($rows -eq 5) "value=$rows"
  ClickAt (CenterOf 'TableInsertButton'); Start-Sleep 1
  $t = (Value) -replace "`r", "`n"
  Check "a 5 x 5 table inserted" (([regex]::Matches($t, '(?m)^\|')).Count -eq 6 -and $t -match '\| 列5 \|') (Esc $t)
  CloseWindow
  Check "app exits" (WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
