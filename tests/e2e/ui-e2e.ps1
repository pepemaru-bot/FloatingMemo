param([string]$NotesDir, [string]$Speech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Namespace UiWin -Name U -MemberDefinition @'
[DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr h, int index);
[DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
[DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
[DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
public struct RECT { public int L, T, R, B; }
'@
[void][UiWin.U]::SetProcessDPIAware()
function Rect { $r = New-Object UiWin.U+RECT; [void][UiWin.U]::GetWindowRect($script:h, [ref]$r); $r }
$settingsFile = Join-Path (Split-Path $NotesDir) 'e2e-settings.json'
try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Remove-Item $settingsFile -ErrorAction SilentlyContinue

  Launch -Build -Extra @('--fake-audio', $Speech, '--model', (Join-Path $env:TEMP 'fm-no-model.bin'))
  $style = [UiWin.U]::GetWindowLong($script:h, -16)   # GWL_STYLE
  $exStyle = [UiWin.U]::GetWindowLong($script:h, -20) # GWL_EXSTYLE
  Check "always on top while visible" (($exStyle -band 0x8) -ne 0)                  # WS_EX_TOPMOST
  Check "maximize/restore button (Windows standard)" (($style -band 0x10000) -ne 0)                        # WS_MAXIMIZEBOX
  Check "minimize button (Windows standard)" (($style -band 0x20000) -ne 0)                        # WS_MINIMIZEBOX
  Check "resizable" (($style -band 0x40000) -ne 0)                                 # WS_THICKFRAME
  $r = Rect
  Check "compact default size" (($r.R - $r.L) -le 700 -and ($r.B - $r.T) -le 500) "$($r.R - $r.L) x $($r.B - $r.T)"
  # Windows may refuse a just-launched background process the foreground while the user works in
  # another app; bring it forward the way a user would (the global hotkey), then check focus.
  EnsureFocus
  Check "editor focused when the memo is in front" (OurEditorFocused)
  Check "mic on / PC audio off by default" ((Toggle-State "マイク") -eq 'On' -and (Toggle-State "PC音声") -eq 'Off')

  # Move/resize, change a toggle, hide -> state is remembered
  [void][UiWin.U]::SetWindowPos($script:h, [IntPtr]::Zero, 200, 150, 520, 380, 0x0014)  # NOZORDER|NOACTIVATE
  Start-Sleep -Milliseconds 500
  Set-Toggle "PC音声" 'On'
  SafeKeys 0x1B; Start-Sleep 1
  Check "settings file written on hide" (Test-Path $settingsFile)
  $saved = Get-Content $settingsFile -Raw | ConvertFrom-Json
  Check "position saved" ($saved.Window.X -eq 200 -and $saved.Window.Y -eq 150 -and $saved.Window.Width -eq 520) ($saved.Window | ConvertTo-Json -Compress)
  Check "toggle saved" ($saved.RecordSystemAudio -eq $true)
  Check "settings contain no memo text" (-not ((Get-Content $settingsFile -Raw) -match 'メモ|draft'))

  # Hotkey: fade-in and focus
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1
  Check "hotkey shows again with focus" ([K]::IsWindowVisible($script:h) -and (OurEditorFocused))
  CloseWindow; [void](WaitExit)

  # Restart -> same place, same toggles
  Launch -Extra @('--fake-audio', $Speech, '--model', (Join-Path $env:TEMP 'fm-no-model.bin'))
  $r = Rect
  Check "position restored after restart" ($r.L -eq 200 -and $r.T -eq 150 -and ($r.R - $r.L) -eq 520 -and ($r.B - $r.T) -eq 380) "$($r.L),$($r.T) $($r.R - $r.L)x$($r.B - $r.T)"
  Check "toggles restored after restart" ((Toggle-State "PC音声") -eq 'On')
  CloseWindow; [void](WaitExit)

  # Off-screen position (monitor removed) -> pulled back onto the screen
  $j = Get-Content $settingsFile -Raw | ConvertFrom-Json
  $j.Window.X = 50000; $j.Window.Y = 50000
  $j | ConvertTo-Json | Set-Content $settingsFile -Encoding UTF8
  Launch -Extra @('--fake-audio', $Speech, '--model', (Join-Path $env:TEMP 'fm-no-model.bin'))
  $r = Rect
  $screen = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
  Check "off-screen position recovered" ($r.L -ge $screen.Left -and $r.T -ge $screen.Top -and $r.R -le $screen.Right -and $r.B -le $screen.Bottom) "$($r.L),$($r.T)"

  # Corrupt settings -> app still starts with defaults
  CloseWindow; [void](WaitExit)
  Set-Content $settingsFile '{ broken' -Encoding UTF8
  Launch -Extra @('--fake-audio', $Speech, '--model', (Join-Path $env:TEMP 'fm-no-model.bin'))
  Check "corrupt settings -> starts with defaults" ((Toggle-State "マイク") -eq 'On' -and (OurEditorFocused))
  CloseWindow; [void](WaitExit)
}
catch { "ERROR: $_" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
