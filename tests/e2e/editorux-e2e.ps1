param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Editor usability: bracket completion, links (Ctrl+K), wrap on/off (Alt+Z), and tooltips that
# show only while the mouse is over their button. Test notes only. The mouse is moved and clicked
# only inside the test window. Not covered (needs a person): the Japanese IME itself.

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

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build
  Check "Markdown editor in use" ((EditorKind) -eq 'web') (EditorKind)

  # --- Brackets --------------------------------------------------------------------------------
  SafeType "(a)"; Start-Sleep -Milliseconds 400
  Check "( adds ), typing ) steps over it (no duplicate)" ((Text) -eq "(a)") (Esc (Text))
  SafeType " ["; SafeKeys 0x08; Start-Sleep -Milliseconds 400
  Check "Backspace removes an empty added pair" ((Text) -eq "(a) ") (Esc (Text))
  SafeType "「メモ"; Start-Sleep -Milliseconds 400
  Check "「 adds 」 (Japanese brackets)" ((Text) -eq "(a) 「メモ」") (Esc (Text))
  SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 400
  Check "Ctrl+Z works after bracket completion" ((Text) -ne "(a) 「メモ」") (Esc (Text))

  # --- Links (Ctrl+K) ------------------------------------------------------------------------
  NewNote
  SafeType "GitHub"; SafeKeys 0x11, 0x41; SafeKeys 0x11, 0x4B
  $url = $null
  [void](WaitFor { ($script:url = Find 'LinkUrlBox') -ne $null } 10)
  Check "Ctrl+K opens the link dialog" ($null -ne $script:url)
  if ($script:url) {
    $script:url.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('javascript:alert(1)')
    (Find '設定' -ByName).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 0.6
    Check "unsafe address refused (dialog stays open)" ((Find 'LinkUrlBox') -ne $null)
    (Find 'LinkUrlBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('https://github.com')
    (Find '設定' -ByName).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    [void](WaitFor { (Text) -eq '[GitHub](https://github.com)' } 5)
    Check "selected words become [text](url)" ((Text) -eq '[GitHub](https://github.com)') (Esc (Text))
    SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 400
    Check "Ctrl+Z removes the link again" ((Text) -eq 'GitHub') (Esc (Text))
  }

  # --- Wrap on/off: display only (settings toggle; Alt+Z is checked by hand, see DEVELOPMENT_LOG) ---
  $settingsFile = Join-Path (Split-Path $NotesDir) 'e2e-settings.json'
  $long = '長い行の表示を確認する文です。' * 30
  NewNote; SafeType $long; Start-Sleep 1
  $win = $null
  for ($try = 0; $try -lt 4 -and -not $win; $try++) {
    (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
    $item = Find 'MinutesSettings'; if ($item) { $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); $win = SettingsWindow }
  }
  function In($id) { $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
  Check "settings window opened" ($null -ne $win)
  $tab = $null
  if ($win) { [void](WaitFor { ($script:tab = In 'SettingsAppearance') -ne $null } 10) }
  if ($script:tab) { $script:tab.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 0.5 }
  $toggle = $null
  [void](WaitFor { ($script:toggle = In 'WrapToggle') -ne $null } 5)
  Check "wrap setting shown (on by default)" ($script:toggle -and $script:toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On')
  if ($script:toggle) { $script:toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1 }
  $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
  Check "wrapping off is saved" ($json.WrapLines -eq $false) "WrapLines=$($json.WrapLines)"
  if ($script:toggle) { $script:toggle.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 1 }
  $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
  Check "and back on" ($json.WrapLines -eq $true) "WrapLines=$($json.WrapLines)"
  $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
  for ($f = 0; $f -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $f++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
  Check "text unchanged by the wrap setting" ((Text) -eq $long)

  # --- Tooltips only while the mouse is over the button -------------------------------------------
  $record = Find '録音開始' -ByName
  $tip = '録音開始 (Ctrl+R)'
  MoveTo (EditorPoint); Start-Sleep 1
  Check "no tooltip while the pointer is on the memo" (-not (TipShown $tip))
  MoveTo (Center $record)
  Check "hovering the record button shows its shortcut" (WaitFor { (TipShown $tip) -or (Mirror 'HoverTipState') -eq $tip } 4) "app reports: $(Mirror 'HoverTipState')"
  MoveTo (EditorPoint)
  Check "moving into the memo hides it" (WaitFor { -not (TipShown $tip) } 3)
  MoveTo (Center $record); [void](WaitFor { TipShown $tip } 4)
  ClickAt (EditorPoint)
  Check "clicking the memo hides it" (WaitFor { -not (TipShown $tip) } 3)
  [void](WaitFor { OurEditorFocused } 5)
  SafeKeys 0x75; Start-Sleep 2   # F6: keyboard focus to the record button
  Check "keyboard focus on the button shows no tooltip" (-not (TipShown $tip))

  CloseWindow
  Check "app exits" (WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
