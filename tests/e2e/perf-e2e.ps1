param([string]$NotesDir, [string]$Speech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
function WaitIdle($sec = 60) {
  $deadline = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $deadline) { if ((Find "録音開始" -ByName) -ne $null) { return $true }; Start-Sleep -Milliseconds 200 }
  $false
}
function Mem { $p = Get-Process -Id $script:app.Id; [pscustomobject]@{ MB = [math]::Round($p.PrivateMemorySize64 / 1MB); Handles = $p.HandleCount } }
try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build -Extra @('--fake-audio', $Speech)
  Start-Sleep 2

  # --- Hotkey -> visible + editor focused (target <= 500 ms) ---------------------------
  $times = @()
  for ($i = 0; $i -lt 10; $i++) {
    SafeKeys 0x1B; Start-Sleep -Milliseconds 700
    $sw = [Diagnostics.Stopwatch]::StartNew()
    [K]::Combo(0x11, 0x10, 0x4D)
    while ($sw.ElapsedMilliseconds -lt 3000 -and -not ([K]::IsWindowVisible($script:h) -and [K]::GetForegroundWindow() -eq $script:h)) { Start-Sleep -Milliseconds 5 }
    $visible = $sw.ElapsedMilliseconds
    while ($sw.ElapsedMilliseconds -lt 3000 -and -not (OurEditorFocused)) { Start-Sleep -Milliseconds 5 }
    $times += [pscustomobject]@{ Visible = $visible; Focused = $sw.ElapsedMilliseconds }
  }
  $maxFocus = ($times | Measure-Object Focused -Maximum).Maximum
  $avgFocus = [math]::Round(($times | Measure-Object Focused -Average).Average)
  Check "hotkey -> editor ready <= 500 ms (10 runs)" ($maxFocus -le 500) "avg $avgFocus ms, max $maxFocus ms (includes UI Automation polling overhead)"

  $idle = Mem
  Check "idle memory recorded (model not loaded)" ($true) "$($idle.MB) MB private, $($idle.Handles) handles"

  # --- Repeated record -> transcribe cycles: resources released ----------------------
  # Cycles are driven with Ctrl+R only. UI Automation queries create provider objects inside
  # the app, so polling the UI during the cycles would itself make handle counts grow;
  # UI Automation is used only between batches. A forced GC (test-only hook) removes
  # objects that are merely waiting for collection, so what remains would be a real leak.
  function Cycle {
    if (-not [K]::ComboInto($script:h, 0x11, 0x52)) { throw "focus left the test window" }
    Start-Sleep -Milliseconds 700
    if (-not [K]::ComboInto($script:h, 0x11, 0x52)) { throw "focus left the test window" }
    Start-Sleep -Milliseconds 3500
  }
  function MemAfterGC { Invoke-Button "TestForceGC"; Start-Sleep 2; Mem }
  EnsureFocus
  for ($i = 0; $i -lt 10; $i++) { Cycle }   # warm-up (model load, JIT, thread pool)
  $afterFirst = MemAfterGC
  Check "memory after warm-up (model loaded)" ($true) "$($afterFirst.MB) MB private, $($afterFirst.Handles) handles"
  EnsureFocus
  for ($i = 0; $i -lt 20; $i++) { Cycle }
  $afterTen = MemAfterGC
  $dHandles = $afterTen.Handles - $afterFirst.Handles
  $dMem = $afterTen.MB - $afterFirst.MB
  Check "no handle leak over 20 cycles (< 20 new)" ($dHandles -lt 20) "$dHandles handles"
  Check "no memory growth over 20 cycles (< 30 MB)" ($dMem -lt 30) "$dMem MB"
  $lines = ((Value) -split "`r").Count
  Check "all 30 transcripts inserted (10 warm-up + 20)" ($lines -ge 30) "$lines lines"
  $leftover = @(Get-ChildItem (Join-Path $env:TEMP 'FloatingMemo\recordings') -Directory -ErrorAction SilentlyContinue).Count
  Check "no temporary audio left behind" ($leftover -eq 0) "$leftover dirs"

  CloseWindow
  Check "exits cleanly" (WaitExit)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber): $($_.InvocationInfo.Line.Trim()))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
