param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Source | visual | preview in the app: switching with the buttons and Ctrl+Alt+P never changes
# the note; edits in the visual mode change only what was edited; tables, links and list moves
# work in both editing modes. Made-up notes only. The mouse is moved only inside the test window.

Add-Type -Namespace M -Name Mouse -MemberDefinition @'
[DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
[DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
public struct RECT { public int Left, Top, Right, Bottom; }
public struct POINT { public int X, Y; }
'@
function Text { (Value) -replace "`r", "`n" }
function WaitFor([scriptblock]$condition, [int]$seconds = 10) { for ($i = 0; $i -lt $seconds * 4; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }; $false }
function Probe { for ($i = 0; $i -lt 20; $i++) { $j = Mirror 'EditorProbe'; if ($j) { return ($j | ConvertFrom-Json) }; Start-Sleep -Milliseconds 200 }; $null }
# The mode the app shows (its mode switch reports what the page last confirmed).
function Mode {
  $status = (Find 'ModeSwitch').Current.ItemStatus
  switch ($status) { 'ソース編集' { 'source' } 'ビジュアル編集' { 'visual' } 'プレビュー' { 'preview' } default { $status } }
}
function ClickMode([string]$id) {
  $b = Find $id
  $b.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
}
# The page reports its mode through the probe mirror (a timer tick behind): wait for it.
function WaitMode([string]$mode, [int]$sec = 8) {
  $deadline = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $deadline) { if ((Mode) -eq $mode) { Start-Sleep -Milliseconds 250; if ((Mode) -eq $mode) { return $true } }; Start-Sleep -Milliseconds 150 }
  $false
}
function Saved { Start-Sleep 1.5; [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false)) -replace "`r", "" }
function Inside($x, $y) { $w = New-Object M.Mouse+RECT; [void][M.Mouse]::GetWindowRect($script:h, [ref]$w); $x -gt $w.Left -and $x -lt $w.Right -and $y -gt $w.Top -and $y -lt $w.Bottom }
function ToScreen($probe, $x, $y) { $r = (Find 'EditorWebView').Current.BoundingRectangle; @([int]($r.X + $x * $probe.dpr), [int]($r.Y + $y * $probe.dpr)) }
function MoveRel($x, $y) { $c = New-Object M.Mouse+POINT; [void][M.Mouse]::GetCursorPos([ref]$c); [M.Mouse]::mouse_event(0x1, $x - $c.X, $y - $c.Y, 0, [UIntPtr]::Zero) }
function Drag($from, $to, $hoverFirst) {
  foreach ($p in @($from, $to, $hoverFirst)) { if ($p -and -not (Inside $p[0] $p[1])) { throw "ABORT: drag outside the test window" } }
  if ($hoverFirst) { [void][M.Mouse]::SetCursorPos($hoverFirst[0], $hoverFirst[1]); MoveRel ($hoverFirst[0] + 2) $hoverFirst[1]; Start-Sleep -Milliseconds 300 }
  for ($i = 1; $i -le 5; $i++) { MoveRel ([int]($hoverFirst[0] + ($from[0] - $hoverFirst[0]) * $i / 5)) $from[1]; Start-Sleep -Milliseconds 40 }
  [void][M.Mouse]::SetCursorPos($from[0], $from[1]); MoveRel ($from[0] + 1) $from[1]; MoveRel $from[0] $from[1]; Start-Sleep -Milliseconds 200
  [M.Mouse]::mouse_event(0x2, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 100
  for ($i = 1; $i -le 10; $i++) { MoveRel ([int]($from[0] + ($to[0] - $from[0]) * $i / 10)) ([int]($from[1] + ($to[1] - $from[1]) * $i / 10)); Start-Sleep -Milliseconds 40 }
  [void][M.Mouse]::SetCursorPos($to[0], $to[1])
  [M.Mouse]::mouse_event(0x4, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 800
}

$note = @"
# 架空の定例会議

* 星の箇条書き
* 二つ目

- 親A
`t- 子A1
`t- 子A2
- 親B

段落の後ろに空白
次の行

| 項目 | 内容 |
|---|---|
| 資料 | [担当](https://example.com) |
"@ -replace "`r", ""
$note += "`n"

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null
  $path = Join-Path $NotesDir 'draft.md'
  [IO.File]::WriteAllText($path, $note, [Text.UTF8Encoding]::new($false))
  Launch -Build
  Check "starts in the source mode, mode switch shown" ((Mode) -eq 'source' -and (Find 'ModeSwitch')) (Mode)

  # --- Switching: the note never changes -----------------------------------------------------------
  ClickMode 'VisualModeButton'
  Check "visual mode with one click" (WaitMode 'visual') (Mode)
  Check "visual button shows as current" ((Find 'VisualModeButton').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On')
  ClickMode 'PreviewModeButton'
  Check "preview with one click" (WaitMode 'preview') (Mode)
  $ids = @('SourceModeButton', 'VisualModeButton', 'PreviewModeButton')
  $modes = @('source', 'visual', 'preview')
  for ($i = 0; $i -lt 99; $i++) { ClickMode $ids[$i % 3]; [void](WaitMode $modes[$i % 3] 5) }
  Check "after 101 switches the editor text is unchanged" ((Text) -eq $note) (Esc (Text))
  Check "and the file is byte for byte the same" ((Saved) -eq $note) (Esc (Saved))

  # --- Shortcut: preview and back to the last editing mode ---------------------------------------
  # Ctrl+E here: injected Alt combinations do not reach the app on this PC (as with Alt+Z), so
  # Ctrl+Alt+P (same command) is checked with real key presses in the browser and by hand.
  ClickMode 'VisualModeButton'; [void](WaitMode 'visual')
  SafeKeys 0x11, 0x45
  Check "Ctrl+E shows the preview" (WaitMode 'preview') "$(Mode) $((Probe) | Select-Object key, composing, lastCommand | ConvertTo-Json -Compress)"
  SafeKeys 0x11, 0x45
  Check "Ctrl+E again returns to the visual mode" ((WaitMode 'visual')) "$(Mode) $((Probe).key | ConvertTo-Json -Compress)"

  ClickMode 'SourceModeButton'; [void](WaitMode 'source')

  # --- Editing in the visual mode: only the edited line changes ------------------------------------
  ClickMode 'VisualModeButton'; [void](WaitMode 'visual')
  SafeKeys 0x11, 0x24        # Ctrl+Home: the heading
  SafeKeys 0x23; SafeType "（第2回）"
  $edited = $note.Replace("# 架空の定例会議", "# 架空の定例会議（第2回）")
  [void](WaitFor { (Text) -eq $edited } 5)
  Check "visual edit: only the edited line changed (tabs, * bullets, line break, table kept)" ((Saved) -eq $edited) (Esc (Saved))
  SafeKeys 0x11, 0x5A; [void](WaitFor { (Text) -eq $note } 5)
  Check "Ctrl+Z in the visual mode restores the note exactly" ((Saved) -eq $note) (Esc (Saved))
  SafeKeys 0x11, 0x59; [void](WaitFor { (Text) -eq $edited } 5)
  Check "Ctrl+Y redoes it" ((Text) -eq $edited) (Esc (Text))

  # --- Tables ---------------------------------------------------------------------------------------
  SafeKeys 0x11, 0x24; SafeKeys 0x23          # after the heading
  Invoke-Button '表を挿入'; Start-Sleep 0.8
  [void](WaitFor { ($script:preset = Find 'Table2x2') -ne $null } 5)
  Check "table size picker opens" ($null -ne $script:preset)
  if ($script:preset) { $script:preset.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  [void](WaitFor { (Text) -ne $edited } 5); $t = Saved
  $kept = -not ($edited.Split("`n") | Where-Object { $_ -ne '' -and -not $t.Contains($_) })
  Check "visual: 2 x 2 table inserted as a Markdown table, the rest kept" (([regex]::Matches($t, '(?m)^\|\s*:?-+')).Count -eq 2 -and $kept) (Esc $t)
  SafeKeys 0x11, 0x5A; [void](WaitFor { (Text) -eq $edited } 5)
  Check "Ctrl+Z removes the table" ((Saved) -eq $edited) (Esc (Saved))
  ClickMode 'SourceModeButton'; [void](WaitMode 'source')
  SafeKeys 0x11, 0x23
  # The picker opens a moment after the click; under load it may need a second press.
  Invoke-Button '表を挿入'
  if (-not (WaitFor { (Find 'TableColsBox') -ne $null } 10)) { Invoke-Button '表を挿入'; [void](WaitFor { (Find 'TableColsBox') -ne $null } 10) }
  (Find 'TableColsBox').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(2)
  (Find 'TableRowsBox').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(3)
  (Find 'TableInsertButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  $withTable = $edited + "`n| 列1 | 列2 |`n|---|---|`n|  |  |`n|  |  |"
  [void](WaitFor { (Text) -ne $edited } 5)
  Check "source: 2 columns x 3 rows inserted after the last line" ((Text) -eq $withTable) (Esc (Text))
  SafeType "番号"
  Check "the first header cell is selected: typing replaces it" ((Text) -eq $withTable.Replace('| 列1 |', '| 番号 |')) (Esc (Text))
  SafeKeys 0x11, 0x5A; SafeKeys 0x11, 0x5A; [void](WaitFor { (Text) -eq $edited } 5)
  Check "Ctrl+Z (twice: typing, table) restores the note" ((Text) -eq $edited) (Esc (Text))
  ClickMode 'PreviewModeButton'; [void](WaitMode 'preview')
  Check "table button disabled in the preview" (-not (Find 'TableButton').Current.IsEnabled)

  # --- Links in the visual mode ----------------------------------------------------------------------
  ClickMode 'VisualModeButton'; [void](WaitMode 'visual')
  SafeKeys 0x11, 0x24; SafeKeys 0x23           # caret at the end of the heading
  SafeKeys 0x11, 0x4B
  $url = $null
  [void](WaitFor { ($script:url = Find 'LinkUrlBox') -ne $null } 10)
  Check "Ctrl+K opens the link dialog in the visual mode" ($null -ne $script:url)
  if ($script:url) {
    (Find 'LinkTextBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('資料')
    $script:url.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue('https://example.com/minutes')
    (Find '設定' -ByName).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $linked = $edited.Replace("# 架空の定例会議（第2回）", "# 架空の定例会議（第2回）[資料](https://example.com/minutes)")
    [void](WaitFor { (Text) -eq $linked } 5)
    Check "visual: a link inserted at the caret, nothing else changed" ((Saved) -eq $linked) (Esc (Saved))
    SafeKeys 0x11, 0x5A; [void](WaitFor { (Text) -eq $edited } 5)
    Check "Ctrl+Z removes it" ((Text) -eq $edited) (Esc (Text))
  }

  # --- List move by mouse in the visual mode -----------------------------------------------------------
  $p = Probe
  $items = $p.visual
  Check "visual list handles reported" ($items -and $items.Count -ge 6) ($p | ConvertTo-Json -Compress -Depth 4)
  if ($items) {
    $a1 = $items | Where-Object { $_.level -eq 1 } | Select-Object -First 1
    $a2 = $items | Where-Object { $_.level -eq 1 } | Select-Object -Skip 1 -First 1
    Drag (ToScreen $p $a2.x $a2.y) (ToScreen $p $a2.x ($a1.y - 6)) (ToScreen $p ($a2.x + 60) $a2.y)
    $moved = $edited.Replace("`t- 子A1`n`t- 子A2", "`t- 子A2`n`t- 子A1")
    [void](WaitFor { (Text) -eq $moved } 5)
    Check "visual: dragging a child item up keeps its level (tab indentation kept)" ((Saved) -eq $moved) (Esc (Saved))
    SafeKeys 0x11, 0x5A; [void](WaitFor { (Text) -eq $edited } 5)
    Check "one Ctrl+Z undoes the move" ((Text) -eq $edited) (Esc (Text))
  }

  # --- A note the visual editor would rewrite stays in the source mode ---------------------------------
  ClickMode 'SourceModeButton'; [void](WaitMode 'source')
  SafeKeys 0x11, 0x4E; Start-Sleep 0.8
  SafeType "[参照][r]"; SafeKeys 0x0D; SafeKeys 0x0D; SafeType "[r]: https://example.com"
  Start-Sleep 1
  $ref = Text
  ClickMode 'VisualModeButton'; Start-Sleep 2
  Check "reference-style links: visual refused, stays in the source mode" ((Mode) -eq 'source') (Mode)
  Check "and the note is unchanged" ((Text) -eq $ref) (Esc (Text))

  CloseWindow
  Check "app exits" (WaitExit 20)
  Check "first note on disk as edited" (([IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false)) -replace "`r", "") -eq $edited)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
