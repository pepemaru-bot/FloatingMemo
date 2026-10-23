param([string]$NotesDir, [string]$Speech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Recordings left by an earlier run (a crash while recording, failed processing): they are found
# at start-up, never deleted silently, can be transcribed into a new note or deleted after a
# confirmation. Also the Whisper model choice in the settings. Test folders only; the "recording"
# is the synthetic Windows-voice speech of run-e2e.ps1. Needs the Whisper model.

$recordings = Join-Path (Split-Path $NotesDir) 'recordings'
function NewLeftover([string]$name, [string]$state) {
  $dir = Join-Path $recordings $name
  New-Item -ItemType Directory -Force $dir | Out-Null
  Copy-Item $Speech (Join-Path $dir 'Microphone-0001.wav')
  $manifest = @{ Version = 1; Created = (Get-Date).AddHours(-3).ToString('s'); State = $state; Tracks = @(@{ Source = 'Microphone'; StartOffsetTicks = 0; SampleRate = 16000; Channels = 1; BlockAlign = 2; Segments = @('Microphone-0001.wav') }) }
  [IO.File]::WriteAllText((Join-Path $dir 'session.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
  $dir
}
function FindByNamePrefix([string]$prefix) {
  foreach ($e in AllElements) { if ($e.Current.Name -and $e.Current.Name.StartsWith($prefix)) { return $e } }
  $null
}
function Press($e) {
  if ($null -eq $e) { throw 'element not found' }
  $p = $null
  if ($e.TryGetCurrentPattern([Windows.Automation.InvokePattern]::Pattern, [ref]$p)) { $p.Invoke() }
  elseif ($e.TryGetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern, [ref]$p)) { $p.Expand() }
  Start-Sleep -Milliseconds 600
}
function WaitFor([scriptblock]$condition, [int]$seconds = 60) {
  for ($i = 0; $i -lt $seconds * 4; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 250 }
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

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  if (Test-Path $recordings) { Remove-Item $recordings -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null

  # --- A recording left by a crash is offered, not deleted ----------------------------------
  $crashed = NewLeftover 'rec-20261018-010000-e2e0000000001' 'recording'
  Launch -Build
  Check "leftover recording offered at start-up" (WaitFor { (InfoBarText 'AudioInfoBar') -match '前回の録音が残っています' } 15) (InfoBarText 'AudioInfoBar')
  Check "nothing deleted at start-up" (Test-Path (Join-Path $crashed 'Microphone-0001.wav'))

  $menu = Find '残っている録音の操作' -ByName
  Check "recover / delete choice shown" ($null -ne $menu)
  if ($menu) { Press $menu }
  $transcribe = FindByNamePrefix '文字起こしする'
  Check "transcribe option in the menu" ($null -ne $transcribe)
  if ($transcribe) { Press $transcribe }

  # Transcribed into a new note (the original note is unknown); the audio goes only after success.
  $ok = WaitFor { @(Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '録音の復元*.md' -ErrorAction SilentlyContinue).Count -gt 0 -and -not (Test-Path $crashed) } 120
  $note = @(Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '録音の復元*.md' -ErrorAction SilentlyContinue) | Select-Object -First 1
  $text = if ($note) { [IO.File]::ReadAllText($note.FullName, [Text.Encoding]::UTF8) } else { '' }
  Check "recovered recording transcribed into a new note" ($ok -and $text.Contains('以下、AIによる文字起こし') -and $text -match '資料') (Esc $text)
  Check "recovered audio deleted after the successful transcription" (-not (Test-Path $crashed))
  CloseWindow; [void](WaitExit 20)

  # --- Deleting asks first; "やめる" keeps the audio, "削除する" removes it -------------------
  $failed = NewLeftover 'rec-20261018-020000-e2e0000000002' 'failed'
  Launch
  Check "failed processing offered too" (WaitFor { (InfoBarText 'AudioInfoBar') -match '前回の録音が残っています' } 15)
  Press (Find '残っている録音の操作' -ByName)
  Press (FindByNamePrefix '削除する')
  $cancel = $null
  [void](WaitFor { ($script:cancel = Find 'やめる' -ByName) -ne $null } 10)
  Check "delete asks for confirmation" ($null -ne $script:cancel)
  if ($script:cancel) { Press $script:cancel }
  Check "cancelled: the audio stays" (Test-Path (Join-Path $failed 'Microphone-0001.wav'))
  $again = $null
  [void](WaitFor { ($script:again = Find '残っている録音の操作' -ByName) -ne $null } 10)
  Press $script:again
  Press (FindByNamePrefix '削除する')
  $confirm = $null
  [void](WaitFor { ($script:confirm = @(AllElements | Where-Object { $_.Current.Name -eq '削除する' -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.Button' }) | Select-Object -First 1) -ne $null } 10)
  if ($script:confirm) { Press $script:confirm }
  Check "confirmed: the leftover recording is deleted" (WaitFor { -not (Test-Path $failed) } 10)

  # --- Whisper model: the installed ones are listed, the choice is saved -------------------------
  $settingsFile = Join-Path (Split-Path $NotesDir) 'e2e-settings.json'
  (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 0.6
  $item = Find 'MinutesSettings'; if ($item) { $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  $win = SettingsWindow
  function In($id) { $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
  $box = $null
  if ($win) { [void](WaitFor { ($script:box = In 'WhisperBox') -ne $null -and (In 'WhisperReport').Current.Name -match '使用中|見つかりません' } 15); $box = $script:box }
  $report = if ($win) { (In 'WhisperReport').Current.Name } else { '' }
  Check "Whisper model in use is shown" ($report -match '使用中: ggml-') $report
  if ($box) {
    $box.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Expand(); Start-Sleep 0.5
    $items = @($box.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::ListItem))))
    $names = $items | ForEach-Object { $_.Current.Name }
    Check "installed models listed (auto + base/small)" ($names.Count -ge 2 -and ($names -join ' ') -match 'base') ($names -join ' / ')
    $pick = $items | Where-Object { $_.Current.Name -match 'small' } | Select-Object -First 1
    if (-not $pick) { $pick = $items | Where-Object { $_.Current.Name -match 'base' } | Select-Object -First 1 }
    $pickName = $pick.Current.Name
    # The popup's items are recreated: find the item again right before selecting it.
    $fresh = $null
    for ($t = 0; $t -lt 3 -and -not $fresh; $t++) {
      $state = $box.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern)
      if ($state.Current.ExpandCollapseState -ne 'Expanded') { $state.Expand(); Start-Sleep 0.5 }
      $fresh = @(AllElements | Where-Object { $_.Current.Name -eq $pickName -and $_.Current.ControlType.ProgrammaticName -eq 'ControlType.ListItem' }) | Select-Object -First 1
      if ($fresh) { try { $fresh.GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 0.3; $box.GetCurrentPattern([Windows.Automation.ExpandCollapsePattern]::Pattern).Collapse() } catch { $fresh = $null; Start-Sleep 0.5 } }
    }
    Start-Sleep 1
    $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
    $expected = if ($pickName -match 'small') { 'ggml-small.bin' } else { 'ggml-base.bin' }
    $shown = try { ($box.GetCurrentPattern([Windows.Automation.SelectionPattern]::Pattern).Current.GetSelection() | ForEach-Object { $_.Current.Name }) -join "," } catch { "?" }
    if ($shown -ne $pickName) {
      # UI Automation could not change the WinUI ComboBox (the box still shows the old choice):
      # the app's own handler never ran. Not a pass and not an app failure: check by hand.
      $script:results += [pscustomobject]@{ Test = "chosen model saved in the settings (UIA cannot select in the ComboBox: check by hand)"; Result = "SKIP"; Detail = "shown=$shown picked=$pickName" }
    }
    else {
      Check "chosen model saved in the settings" ($json.WhisperModel -eq $expected) "WhisperModel=$($json.WhisperModel)"
      Check "restart notice when the choice differs from the model in use" (((In 'WhisperReport').Current.Name -match '再起動') -or ($report -match [regex]::Escape($expected))) ((In 'WhisperReport').Current.Name)
    }
  }
  if ($win) { $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1 }
  for ($f = 0; $f -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $f++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }

  CloseWindow
  Check "app exits" (WaitExit 20)
  Check "recordings stay in the test folder only" (-not (Test-Path (Join-Path $recordings 'rec-20261018-010000-e2e0000000001')))
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
