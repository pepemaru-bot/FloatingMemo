param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Starts another instance with the same notes folder and returns its PID (it is expected to exit).
function LaunchSecond {
  $log = Join-Path $env:TEMP ("fm-run-" + [guid]::NewGuid().ToString('N') + ".log")
  $a = @('run', '--project', $Project, '-p:Platform=x64', '--no-build', '--', '--notes-dir', $NotesDir,
         '--settings-file', (Join-Path (Split-Path $NotesDir) 'e2e-settings.json'))
  if ($env:FM_EXE) { $id = (Start-Process $env:FM_EXE -ArgumentList $a[($a.IndexOf('--') + 1)..($a.Count - 1)] -PassThru).Id; $script:launched += $id; return $id }
  $dn = Start-Process dotnet -ArgumentList $a -RedirectStandardOutput $log -PassThru -WindowStyle Hidden
  $script:launched += $dn.Id
  $deadline = (Get-Date).AddSeconds(90)
  while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 300
    $m = Select-String -Path $log -Pattern 'launched \(PID: (\d+)\)' -ErrorAction SilentlyContinue
    if ($m) { break }
  }
  if (-not $m) { throw "second instance did not launch: $(Get-Content $log -Raw)" }
  $id = [int]$m.Matches[0].Groups[1].Value
  $script:launched += $id
  $id
}
function AnyDialog {
  [Windows.Automation.AutomationElement]::RootElement.FindAll([Windows.Automation.TreeScope]::Children,
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, "#32770"))) |
    Where-Object { $_.Current.ProcessId -in $script:launched }
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build
  $first = $script:app.Id
  SafeType "最初のインスタンス"
  SafeKeys 0x1B; Start-Sleep 1
  Check "first instance hidden" (-not [K]::IsWindowVisible($script:h))

  # Second launch with the same notes folder
  $second = LaunchSecond
  $exited = $false
  for ($i = 0; $i -lt 40; $i++) { if (-not (Get-Process -Id $second -ErrorAction SilentlyContinue)) { $exited = $true; break }; Start-Sleep -Milliseconds 250 }
  Check "second instance exits by itself" $exited "pid $second"
  Start-Sleep 1
  Check "only one FloatingMemo window process for this folder" (-not (Get-Process -Id $second -ErrorAction SilentlyContinue) -and (Get-Process -Id $first -ErrorAction SilentlyContinue))
  Check "first instance's memo is shown again" ([K]::IsWindowVisible($script:h))
  Check "first instance comes to the foreground" ([K]::GetForegroundWindow() -eq $script:h)
  Check "editor focused in first instance" (OurEditorFocused)
  Check "text kept" ((Value) -eq "最初のインスタンス") (Value)
  Check "no hotkey-conflict dialog" (@(AnyDialog).Count -eq 0)

  # Hotkey still belongs to the first instance
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1
  Check "hotkey still toggles first instance" (-not [K]::IsWindowVisible($script:h))
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1
  CloseWindow
  Check "first instance exits normally" (WaitExit)
  Check "memo saved" ((FileText) -eq "最初のインスタンス")
}
catch { "ERROR: $_" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
