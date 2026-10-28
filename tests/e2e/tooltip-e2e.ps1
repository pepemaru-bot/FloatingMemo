param([string]$NotesDir, [string]$Speech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# The record button's hint (Ctrl+R) must not stay over the note: pointer moves, keys,
# focus, deactivation and hiding while it is open.
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
function RecordTip { foreach ($t in @('録音開始 (Ctrl+R)', '録音停止 (Ctrl+R)')) { if (TipShown $t) { return $t } }; $null }
function TipGone([int]$sec = 3) { WaitFor { -not (RecordTip) } $sec }
function HoverRecord { MoveTo (CenterOf 'RecordButton'); WaitFor { (RecordTip) -ne $null } 4 }
Add-Type -Namespace W -Name U -MemberDefinition '[DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h); [DllImport("user32.dll")] public static extern IntPtr GetDesktopWindow(); [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();'

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build -Extra @('--fake-audio', $Speech)
  SafeType "ツールチップの確認"

  # 1. Pointer: from the record button into the note (WebView2), slowly and quickly.
  $shown = HoverRecord
  MoveTo (EditorPoint)
  Check "1a hover, then slowly into the note: hint gone" ($shown -and (TipGone)) "shown=$shown"
  [void](HoverRecord)
  $e = EditorPoint; [void][M.Mouse]::SetCursorPos($e[0], $e[1])     # a jump, no movement in between
  Check "1b hover, then a jump into the note: hint gone" (TipGone) "state=$(Mirror 'HoverTipState')"

  # 2. Keyboard while hovering: Ctrl+R starts recording (the hint's text changes), then the pointer leaves.
  [void](HoverRecord)
  [K]::Combo(0x11, 0x52); Start-Sleep 1.5
  MoveTo (EditorPoint)
  Check "2a Ctrl+R while hovering, then into the note: hint gone" (TipGone 4) "state=$(Mirror 'HoverTipState')"
  [K]::Combo(0x11, 0x52); Start-Sleep 3          # stop again

  # 3. Focus: F6 puts the keyboard on the record button (a keyboard focus must not show it).
  MoveTo (EditorPoint)
  SafeKeys 0x75; Start-Sleep 1.5
  Check "3a keyboard focus on the record button shows no hint (pointer on the note)" (-not (RecordTip))
  ClickAt (EditorPoint); Start-Sleep 0.5

  # 4. Window deactivated while the hint is open, pointer then moved into the note.
  [void](HoverRecord)
  [void][W.U]::SetForegroundWindow([W.U]::GetShellWindow()); Start-Sleep 1
  MoveTo (EditorPoint)
  Check "4a window deactivated while hovering, then into the note: hint gone" (TipGone 4) "state=$(Mirror 'HoverTipState')"
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1   # back (the app's hotkey)

  # 5. Hidden with Esc while the hint is open, shown again with the hotkey.
  [void](HoverRecord)
  [K]::Combo(0x1B); Start-Sleep 1
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1.5
  MoveTo (EditorPoint)
  Check "5a hidden with Esc while hovering, shown again: no hint over the note" (TipGone 4) "state=$(Mirror 'HoverTipState')"

  CloseWindow
  Check "app exits" (WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
