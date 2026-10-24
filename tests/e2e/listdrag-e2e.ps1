param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Moving list items with a real mouse drag in the app (WebView2 + CodeMirror). The test build
# reports where the handles are (line numbers and coordinates only). Made-up note; the mouse is
# moved only inside the test window.

Add-Type -Namespace M -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
function Text { (Value) -replace "`r", "`n" }
function Inside($x, $y) { $w = New-Object M.Mouse+RECT; [void][M.Mouse]::GetWindowRect($script:h, [ref]$w); $x -gt $w.Left -and $x -lt $w.Right -and $y -gt $w.Top -and $y -lt $w.Bottom }
function Handles {
  for ($i = 0; $i -lt 20; $i++) {
    $json = Mirror 'EditorProbe'
    if ($json) { $p = $json | ConvertFrom-Json; if ($p.handles.Count -gt 0) { return $p } }
    Start-Sleep -Milliseconds 300
  }
  $null
}
# Page coordinates (CSS px) -> screen pixels, from the WebView's place on screen.
function ToScreen($probe, $x, $y) {
  $r = (Find 'EditorWebView').Current.BoundingRectangle
  @([int]($r.X + $x * $probe.dpr), [int]($r.Y + $y * $probe.dpr))
}
function Drag($from, $to) {
  if (-not (Inside $from[0] $from[1]) -or -not (Inside $to[0] $to[1])) { throw "ABORT: drag outside the test window" }
  [void][M.Mouse]::SetCursorPos($from[0], $from[1]); Start-Sleep -Milliseconds 150
  [M.Mouse]::mouse_event(0x1, 1, 0, 0, [UIntPtr]::Zero); [M.Mouse]::mouse_event(0x1, -1, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 100   # hover: handle shows
  [M.Mouse]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 100   # press
  for ($i = 1; $i -le 10; $i++) {
    # Real relative moves (a cursor jump with a zero move is not reported to the page as a move).
    $x = [int]($from[0] + ($to[0] - $from[0]) * $i / 10); $y = [int]($from[1] + ($to[1] - $from[1]) * $i / 10)
    $c = New-Object M.Mouse+POINT; [void][M.Mouse]::GetCursorPos([ref]$c)
    [M.Mouse]::mouse_event(0x1, $x - $c.X, $y - $c.Y, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 40
  }
  [void][M.Mouse]::SetCursorPos($to[0], $to[1])   # exact end point whatever the pointer acceleration did
  [M.Mouse]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 600   # release
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build
  # - 親A / <tab>- 子A1 / <tab>- 子A2 / - 親B (typed: Enter continues the list, Tab / Shift+Tab indent)
  SafeType "- 親A"; SafeKeys 0x0D; SafeKeys 0x09; SafeType "子A1"; SafeKeys 0x0D; SafeType "子A2"; SafeKeys 0x0D; SafeKeys 0x10, 0x09; SafeType "親B"
  Start-Sleep 1
  $start = "- 親A`n`t- 子A1`n`t- 子A2`n- 親B"
  Check "nested list typed" ((Text) -eq $start) (Esc (Text))

  $p = Handles
  Check "handles reported for every item" ($p -and $p.handles.Count -eq 4) ($p | ConvertTo-Json -Compress)
  if ($p) {
    $h2 = $p.handles | Where-Object { $_.line -eq 2 }; $h3 = $p.handles | Where-Object { $_.line -eq 3 }
    # 1. Straight up: 子A2 above 子A1, same level.
    Drag (ToScreen $p $h3.x $h3.y) (ToScreen $p $h3.x ($h2.y - 6))
    Start-Sleep -Milliseconds 400; $after = Mirror 'EditorProbe'
    Check "dragging an indented item up keeps its indentation" ((Text) -eq "- 親A`n`t- 子A2`n`t- 子A1`n- 親B") ("$(Esc (Text)) | from=$(ToScreen $p $h3.x $h3.y) wv=$((Find 'EditorWebView').Current.BoundingRectangle) | $after")

    # 2. Sideways: 子A1 (now line 3) one indent to the left = top level, same place.
    $p = Handles; $h3 = $p.handles | Where-Object { $_.line -eq 3 }
    $indent = 28
    Drag (ToScreen $p $h3.x $h3.y) (ToScreen $p ($h3.x - $indent) ($h3.y - 4))
    Check "dragging left moves it up one level (children follow)" ((Text) -eq "- 親A`n`t- 子A2`n- 子A1`n- 親B") (Esc (Text))

    # 3. Undo restores the previous step in one go.
    SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 500
    Check "Ctrl+Z undoes the whole move" ((Text) -eq "- 親A`n`t- 子A2`n`t- 子A1`n- 親B") (Esc (Text))

    # 4. Three levels: a parent dragged below another parent takes its whole subtree along,
    #    relative indentation unchanged; one Ctrl+Z restores it.
    SafeKeys 0x11, 0x41; SafeKeys 0x2E; Start-Sleep -Milliseconds 300
    SafeType "- 甲"; SafeKeys 0x0D; SafeKeys 0x09; SafeType "甲1"; SafeKeys 0x0D; SafeKeys 0x09; SafeType "甲11"; SafeKeys 0x0D; SafeKeys 0x10, 0x09; SafeKeys 0x10, 0x09; SafeType "乙"
    Start-Sleep 1
    $three = "- 甲`n`t- 甲1`n`t`t- 甲11`n- 乙"
    Check "three-level list typed" ((Text) -eq $three) (Esc (Text))
    $p = Handles; $h1 = $p.handles | Where-Object { $_.line -eq 1 }; $h4 = $p.handles | Where-Object { $_.line -eq 4 }
    Drag (ToScreen $p $h1.x $h1.y) (ToScreen $p $h1.x ($h4.y + 8))
    Check "a parent moves with all its descendants, levels kept" ((Text) -eq "- 乙`n- 甲`n`t- 甲1`n`t`t- 甲11") (Esc (Text))
    SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 500
    Check "one Ctrl+Z restores the three-level list" ((Text) -eq $three) (Esc (Text))
  }

  Start-Sleep 1.5
  $file = Get-ChildItem $NotesDir -Recurse -Filter *.md | Sort-Object LastWriteTime | Select-Object -Last 1
  Check "moves are autosaved as plain Markdown" ([IO.File]::ReadAllText($file.FullName, [Text.Encoding]::UTF8).Contains("`t`t- 甲11")) $file.Name
  CloseWindow
  Check "app exits" (WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
