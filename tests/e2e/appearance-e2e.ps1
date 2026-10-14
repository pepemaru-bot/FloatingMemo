param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Liquid-glass surface and the editor cursor (Phase C/D). The cursor colour is reported by
# the editor page itself (test builds mirror it); the window theme is forced with --theme.
try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  foreach ($theme in 'dark', 'light') {
    if ($theme -eq 'dark') { Launch -Build -Extra @('--theme', $theme) } else { Launch -Extra @('--theme', $theme) }
    Check "[$theme] Markdown editor active" ((EditorKind) -eq 'web') (EditorKind)
    for ($i = 0; $i -lt 20 -and -not (Mirror 'EditorCaret'); $i++) { Start-Sleep -Milliseconds 200 }
    Check "[$theme] white paper -> near-black cursor" ((Mirror 'EditorCaret') -eq '#1b1b1b') (Mirror 'EditorCaret')
    SafeType "テスト"; Start-Sleep -Milliseconds 800
    Check "[$theme] typing on the paper card works" ((Value) -match 'テスト') (Value)
    $tab = AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' } | Select-Object -First 1
    Check "[$theme] selected tab still announced" ($tab -and $tab.Current.ItemStatus -eq '選択中')
    CloseWindow; [void](WaitExit 20)
  }

  # --- Appearance settings: glass clarity and paper milkiness, applied live and saved ----------
  Launch -Extra @('--theme', 'light')
  $settingsFile = Join-Path (Split-Path $NotesDir) 'e2e-settings.json'
  $win = $null
  for ($try = 0; $try -lt 4 -and -not $win; $try++) {
    (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
    $item = Find 'MinutesSettings'; if ($item) { $item.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
    $desk = [Windows.Automation.AutomationElement]::RootElement
    $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)
    for ($i = 0; $i -lt 10 -and -not $win; $i++) { Start-Sleep -Milliseconds 300; $win = $desk.FindAll([Windows.Automation.TreeScope]::Children, $cond) | Where-Object { $_.Current.Name -eq 'Floating Memo の設定' } | Select-Object -First 1 }
  }
  function In($id) { $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id))) }
  (In 'SettingsAppearance').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 0.5
  Check "appearance sliders shown with defaults" ((In 'ClaritySlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current.Value -eq 60 -and (In 'PaperSlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).Current.Value -eq 60)
  (In 'ClaritySlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(100)
  (In 'PaperSlider').GetCurrentPattern([Windows.Automation.RangeValuePattern]::Pattern).SetValue(0)
  Start-Sleep 1
  $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
  Check "slider values saved" ($json.GlassClarity -eq 1 -and $json.PaperMilkiness -eq 0) "clarity=$($json.GlassClarity) milkiness=$($json.PaperMilkiness)"
  Check "cursor colour unchanged on white paper" ((Mirror 'EditorCaret') -eq '#1b1b1b') (Mirror 'EditorCaret')
  $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
  for ($f = 0; $f -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $f++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
  SafeKeys 0x11, 0x23; SafeType "設定後も入力できる"; Start-Sleep 1
  Check "typing works after changing the appearance" ((Value) -match '設定後も入力できる$') (Esc (Value))
  CloseWindow; [void](WaitExit 20)
  Launch -Extra @('--theme', 'light')
  $json = Get-Content $settingsFile -Raw -Encoding UTF8 | ConvertFrom-Json
  Check "appearance kept after restart" ($json.GlassClarity -eq 1 -and $json.PaperMilkiness -eq 0)
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
