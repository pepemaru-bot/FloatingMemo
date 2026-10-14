param([string]$NotesDir, [string]$FakeAudio, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Microphone device selection with a made-up device list (--fake-devices): no real audio
# device is listed, opened or changed.

$settingsFile = Join-Path (Split-Path $NotesDir) 'e2e-settings.json'
function SettingsJson { if (Test-Path $settingsFile) { Get-Content $settingsFile -Raw -Encoding UTF8 } else { '' } }
function DeviceLabel { (Find 'MicDeviceButton').Current.HelpText }

# Menu popups may be hosted outside the main window's UI Automation tree: also look at the
# app's other top-level windows (only those of the test process).
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
function OpenMenu {
  (Find 'MicDeviceButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 150; $m = @(MenuItems); if ($m.Count) { return $m } }
  @()
}
function CloseMenu { if (-not @(MenuItems).Count) { return }; if (-not [K]::ComboInto($script:h, @(0x1B))) { throw "ABORT: focus left the test window" }; Start-Sleep -Milliseconds 400 }  # Esc closes the open menu only
function Choose([string]$id) {
  $item = OpenMenu | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1
  if (-not $item) { CloseMenu; throw "menu item not found: $id" }
  # Radio menu items expose the toggle pattern (as a screen reader would use them).
  $item.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
  Start-Sleep -Milliseconds 300
  CloseMenu
}
function Checked([string]$id) {
  $item = OpenMenu | Where-Object { $_.Current.AutomationId -eq $id } | Select-Object -First 1
  $state = if ($item) { $item.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState.ToString() -eq 'On' } else { $null }
  CloseMenu
  $state
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  $noModel = Join-Path $env:TEMP 'fm-e2e-no-model.bin'
  Launch -Build -Extra @('--fake-audio', $FakeAudio, '--fake-devices', '--model', $noModel)

  Check "device button next to the mic" ((Find 'MicDeviceButton') -ne $null)
  Check "default mode shows the Windows default device name" ((DeviceLabel) -eq 'Windows の既定（ステレオ ミキサー（テスト））') (DeviceLabel)

  $items = @(OpenMenu)
  $ids = ($items | ForEach-Object { $_.Current.AutomationId }) -join '|'
  CloseMenu
  Check "menu lists default, communications and every device" (
    $ids -match 'MicDevice:default' -and $ids -match 'MicDevice:communications' -and
    $ids -match '\{fake-array\}' -and $ids -match '\{fake-usb\}' -and $ids -match '\{fake-mix\}') $ids
  Check "listing devices does not start recording" (-not (IsShown "RecordingStatusText"))

  # Choose a specific device: saved by ID and shown.
  Choose 'MicDevice:{fake-array}'
  Check "specific device shown" ((DeviceLabel) -eq 'テスト用マイク配列') (DeviceLabel)
  Check "specific device saved by ID" ((SettingsJson) -match '"Specific"' -and (SettingsJson) -match '\{fake-array\}') (SettingsJson)
  Check "chosen device is checked in the menu" ((Checked 'MicDevice:{fake-array}') -eq $true)

  # Communications default.
  Choose 'MicDevice:communications'
  Check "communications mode shows its device" ((DeviceLabel) -eq 'Windows の既定の通信デバイス（テスト用USBマイク）') (DeviceLabel)

  # The communications device is unplugged: the role moves, the label follows (Windows' choice).
  Invoke-Button 'TestToggleUsbDevice'; Start-Sleep 1
  Check "communications mode follows Windows' new communications device" ((DeviceLabel) -eq 'Windows の既定の通信デバイス（テスト用マイク配列）') (DeviceLabel)
  Invoke-Button 'TestToggleUsbDevice'; Start-Sleep 1

  # A specific device that is unplugged: warning, no silent switch, recording refused.
  Choose 'MicDevice:{fake-usb}'
  Invoke-Button 'TestToggleUsbDevice'; Start-Sleep 1
  $info = InfoBarText "AudioInfoBar"
  Check "unplugged chosen device -> warning" ($info -match 'マイクを使用できません' -and $info -match 'テスト用USBマイク') $info
  Check "still set to the unplugged device (no silent switch)" ((DeviceLabel) -eq 'テスト用USBマイク' -and (SettingsJson) -match '\{fake-usb\}') (DeviceLabel)
  $ids = (@(OpenMenu) | ForEach-Object { $_.Current.AutomationId }) -join '|'; CloseMenu
  Check "menu keeps showing the missing device" ($ids -match 'MicDevice:missing' -and $ids -notmatch '\{fake-usb\}') $ids
  Invoke-Button "録音開始"; Start-Sleep 1
  $info = InfoBarText "AudioInfoBar"
  Check "recording with the missing device is refused" (-not (IsShown "RecordingStatusText") -and $info -match '録音を開始できませんでした') $info

  # Mic off: a missing mic does not block PC-audio-only recording.
  Set-Toggle "マイク" 'Off'; Set-Toggle "PC音声" 'On'
  Invoke-Button "録音開始"; Start-Sleep 1
  Check "PC audio alone still records" (IsShown "RecordingStatusText")
  Check "device menu locked while recording" (-not (Find 'MicDeviceButton').Current.IsEnabled)
  Invoke-Button "録音停止"; Start-Sleep 3
  Set-Toggle "PC音声" 'Off'; Set-Toggle "マイク" 'On'

  # Plugged back in: warning goes away, the device is used again.
  Invoke-Button 'TestToggleUsbDevice'; Start-Sleep 1
  Check "replugged device -> usable again" ((DeviceLabel) -eq 'テスト用USBマイク')
  Invoke-Button "録音開始"; Start-Sleep 1.5
  Check "recording starts with the replugged device" (IsShown "RecordingStatusText")
  Invoke-Button "録音停止"; Start-Sleep 3

  # Restart: the choice is restored.
  CloseWindow; [void](WaitExit 20)
  Launch -Extra @('--fake-audio', $FakeAudio, '--fake-devices', '--model', $noModel)
  Check "choice restored after restart" ((DeviceLabel) -eq 'テスト用USBマイク') (DeviceLabel)
  Choose 'MicDevice:default'
  Check "back to the Windows default" ((DeviceLabel) -match '^Windows の既定（' -and (SettingsJson) -match '"SystemDefault"') (DeviceLabel)
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
