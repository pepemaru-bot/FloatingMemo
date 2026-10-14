<#
.SYNOPSIS
    Smoke test for the shipped portable build (no test hooks): everything is checked through
    the saved .md files and window state, never by reading the editor through UI Automation.
    AI models: by default the per-user models folder is replaced (FLOATINGMEMO_MODELS_DIR) by an
    empty one, so only models inside the extracted folder count. Without -WithModels the test
    expects none and checks the "no model" guidance; with -WithModels it expects a Qwen model in
    <app>\models\qwen and checks that AI minutes are created.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tests\e2e\portable-smoke.ps1 -Exe C:\Temp\FloatingMemo-Portable\FloatingMemo.exe
#>
param([string]$Exe, [string]$NotesDir = (Join-Path $env:TEMP 'FloatingMemoE2E\portable-notes'), [switch]$WithModels)
$Project = ''
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
$env:FM_EXE = $Exe
$emptyModels = Join-Path $env:TEMP 'FloatingMemoE2E\no-user-models'
New-Item -ItemType Directory -Force $emptyModels | Out-Null
$env:FLOATINGMEMO_MODELS_DIR = $emptyModels

if (Get-Process FloatingMemo -ErrorAction SilentlyContinue) { Write-Error "Close Floating Memo first."; exit 2 }

function Keys([uint16[]]$vks) { if (-not [K]::ComboInto($script:h, $vks)) { throw "ABORT: focus left the test window" } }
function TypeText([string]$s) { if (-not [K]::TypeInto($script:h, $s)) { throw "ABORT: focus left the test window" } }
function Front {
  for ($i = 0; $i -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $i++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
  if ([K]::GetForegroundWindow() -ne $script:h) { throw "ABORT: cannot bring the test window to the front" }
  Start-Sleep -Milliseconds 500
}
function MinutesFiles { @(Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '*AI議事録*.md' -ErrorAction SilentlyContinue) }
function MinutesMenu([string]$id) {
  (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 150; $m = Find $id; if ($m) { $m.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); return $true } }
  $false
}
function NewestNote { Get-ChildItem (Join-Path $NotesDir 'notes') -Filter *.md -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 }

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null
  [IO.File]::WriteAllText((Join-Path $NotesDir 'draft.md'), "# 既存`n", [Text.UTF8Encoding]::new($false))

  $crashLog = Join-Path $env:TEMP 'FloatingMemo\crash.log'
  $crashBefore = if (Test-Path $crashLog) { (Get-Item $crashLog).LastWriteTimeUtc } else { $null }
  Launch
  Start-Sleep 4   # let the Markdown editor replace the start-up text box
  Check "window shown from the extracted folder" ([K]::IsWindowVisible($script:h))
  Front
  Keys 0x11, 0x23; Keys 0x0D
  TypeText "- 項目1"; Keys 0x0D; TypeText "項目2"
  Start-Sleep -Milliseconds 1500
  Check "typing saved; Enter continued the list (Markdown editor active)" ((FileText) -eq "# 既存`n`n- 項目1`n- 項目2") (Esc (FileText))

  Keys 0x11, 0x4E; Start-Sleep 1; TypeText "新しいノート"; Start-Sleep -Milliseconds 1500
  $note = NewestNote
  Check "Ctrl+N creates a separate note file" ($note -and [IO.File]::ReadAllText($note.FullName) -eq "新しいノート") ($note.Name)

  # Second launch: no second instance, the memo comes to the front.
  $before = @(Get-Process FloatingMemo).Count
  $second = (Start-Process $Exe -ArgumentList '--notes-dir', $NotesDir -PassThru).Id
  $script:launched += $second
  Start-Sleep 5
  Check "second launch exits (single instance)" (-not (Get-Process -Id $second -ErrorAction SilentlyContinue)) "processes before=$before"

  Front
  TypeText " 追記"; CloseWindow
  Check "exit" (WaitExit 20)
  Check "exit saved the last keystrokes" ([IO.File]::ReadAllText($note.FullName) -eq "新しいノート 追記") ([IO.File]::ReadAllText($note.FullName))

  Launch; Start-Sleep 4
  $tabs = (AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' } | ForEach-Object { $_.Current.Name }) -join '|'
  Check "restart restores both tabs" ($tabs -eq "draft|$($note.BaseName)") $tabs
  $crashAfter = if (Test-Path $crashLog) { (Get-Item $crashLog).LastWriteTimeUtc } else { $null }
  Check "no crash logged during the test" ($crashAfter -eq $crashBefore)

  # AI minutes: guidance without a model, a minutes note with one (fictional meeting text).
  Front
  Keys 0x11, 0x4E; Start-Sleep 1
  TypeText "架空の定例会議です。リリース日は十一月一日に決まりました。資料の準備は田中さんが金曜日までに行います。"
  Start-Sleep -Milliseconds 1500
  Check "minutes menu opens" (MinutesMenu 'MinutesCreate')
  if ($WithModels) {
    $done = $false
    for ($i = 0; $i -lt 600 -and -not $done; $i++) { Start-Sleep 1; $done = (MinutesFiles).Count -gt 0 }
    Check "with a model in models\qwen -> AI minutes note created" $done (InfoBarText 'AudioInfoBar')
  }
  else {
    Start-Sleep 1
    $msg = InfoBarText 'AudioInfoBar'
    Check "no model -> guidance names Qwen3 and the README" ($msg -match 'AIモデルがありません' -and $msg -match 'Qwen3' -and $msg -match 'README') $msg
    Check "no model -> nothing created" ((MinutesFiles).Count -eq 0)
  }
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  Remove-Item Env:FM_EXE -ErrorAction SilentlyContinue
  Remove-Item Env:FLOATINGMEMO_MODELS_DIR -ErrorAction SilentlyContinue
  $script:results | Format-Table -AutoSize -Wrap
}
