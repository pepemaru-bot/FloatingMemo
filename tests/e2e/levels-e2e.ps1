param([string]$NotesDir, [string]$FakeAudio, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Level indicators and the microphone test, fed by synthetic speech played at real-time speed
# (--fake-audio-realtime). No real microphone is opened.

# "mic=0.53;pc=0.00;nosignal=False;test=False" (test builds mirror the indicator state)
function Levels {
  $t = Mirror 'LevelMirror'; $r = @{}
  if ($t) { foreach ($kv in $t -split ';') { $k, $v = $kv -split '=', 2; $r[$k] = $v } }
  $r
}
function MaxLevel([string]$key, [double]$seconds) {
  $max = 0.0; $end = (Get-Date).AddSeconds($seconds)
  while ((Get-Date) -lt $end) { $l = Levels; if ($l[$key]) { $max = [Math]::Max($max, [double]$l[$key]) }; Start-Sleep -Milliseconds 100 }
  $max
}
function MenuItems {
  $items = @(AllElements | Where-Object { $_.Current.AutomationId -like 'MicDevice:*' })
  if ($items.Count) { return $items }
  $desk = [Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
  foreach ($w in $desk.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
    $items += @($w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
      Where-Object { $_.Current.AutomationId -like 'MicDevice:*' })
  }
  $items
}
function ToggleMicTest {
  (Find 'MicDeviceButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  $item = $null
  for ($i = 0; $i -lt 20 -and -not $item; $i++) { Start-Sleep -Milliseconds 150; $item = MenuItems | Where-Object { $_.Current.AutomationId -eq 'MicDevice:test' } | Select-Object -First 1 }
  if (-not $item) { throw "mic test menu item not found" }
  $item.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
  Start-Sleep -Milliseconds 300
  if (@(MenuItems).Count) { if (-not [K]::ComboInto($script:h, @(0x1B))) { throw "ABORT: focus left the test window" } }
  Start-Sleep -Milliseconds 400
}
function TestAudioFiles { @(Get-ChildItem (Join-Path $env:TEMP 'FloatingMemo\recordings') -Recurse -File -ErrorAction SilentlyContinue).Count }

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  $noModel = Join-Path $env:TEMP 'fm-e2e-no-model.bin'
  Launch -Build -Extra @('--fake-audio', $FakeAudio, '--fake-audio-realtime', '--model', $noModel)

  $l = Levels
  Check "idle: no gauge, mic not opened" ($l['mic'] -eq $null -or [double]$l['mic'] -eq 0) ((Mirror 'LevelMirror'))

  # Mic test: user-started, shows the level, saves nothing, releases the device at the end.
  $filesBefore = TestAudioFiles
  ToggleMicTest
  $info = InfoBarText "AudioInfoBar"
  Check "mic test announced (nothing saved)" ($info -match 'マイクテスト中' -and $info -match '保存されません') $info
  $peak = MaxLevel 'mic' 3
  Check "mic test: gauge rises with speech" ($peak -gt 0.3) $peak
  Check "mic test: speaker gauge stays empty" ([double](Levels)['pc'] -eq 0)
  Check "mic test writes no audio files" ((TestAudioFiles) -eq $filesBefore)
  Invoke-Button 'テストを終了'; Start-Sleep 1.5
  $l = Levels
  Check "mic test stopped: gauge drains to 0" ([double]$l['mic'] -eq 0 -and $l['test'] -eq 'False') ((Mirror 'LevelMirror'))
  Check "test info bar closed" (-not ((InfoBarText "AudioInfoBar") -match 'マイクテスト中'))

  # Esc (hide) ends a running test.
  ToggleMicTest
  Check "mic test running again" ((Levels)['test'] -eq 'True')
  SafeKeys 0x1B; Start-Sleep 1
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1.5
  Check "hiding the memo ends the mic test" ((Levels)['test'] -eq 'False') ((Mirror 'LevelMirror'))

  # Recording: independent green (mic) and blue (PC audio) gauges.
  Set-Toggle "PC音声" 'On'
  Invoke-Button "録音開始"; Start-Sleep 0.5
  $micPeak = MaxLevel 'mic' 3
  $pcPeak = [double](Levels)['pc']
  Check "recording: mic gauge moves" ($micPeak -gt 0.3) $micPeak
  Check "recording: PC audio gauge moves" ($pcPeak -gt 0 -or (MaxLevel 'pc' 1) -gt 0.3)
  Check "recording: receiving (not 'no signal')" ((Levels)['nosignal'] -eq 'False')
  Invoke-Button "録音停止"; Start-Sleep 3
  Check "after stop: both gauges empty" ([double](Levels)['mic'] -eq 0 -and [double](Levels)['pc'] -eq 0) ((Mirror 'LevelMirror'))

  # Mic off: no gauge for the mic even while recording PC audio.
  Set-Toggle "マイク" 'Off'
  Invoke-Button "録音開始"; Start-Sleep 0.5
  $micPeak = MaxLevel 'mic' 1.5
  Check "mic off: mic gauge stays empty" ($micPeak -eq 0) $micPeak
  Invoke-Button "録音停止"; Start-Sleep 3
  Set-Toggle "マイク" 'On'; Set-Toggle "PC音声" 'Off'

  CloseWindow; [void](WaitExit 20)

  # A device that delivers nothing ("no signal") is told apart from silence: the
  # non-real-time fake delivers its whole file at once and then nothing more.
  Launch -Extra @('--fake-audio', $FakeAudio, '--model', $noModel)
  Invoke-Button "録音開始"; Start-Sleep 2.5
  Check "no buffers arriving -> 'no signal' state" ((Levels)['nosignal'] -eq 'True') ((Mirror 'LevelMirror'))
  Invoke-Button "録音停止"; Start-Sleep 3
  Check "no-signal state cleared after stop" ((Levels)['nosignal'] -eq 'False') ((Mirror 'LevelMirror'))
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
