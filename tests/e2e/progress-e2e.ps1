param([string]$NotesDir, [string]$LongSpeech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Progress of long jobs in the control strip: Whisper shows a measured percentage; the AI
# minutes show step / elapsed time / characters (never a made-up percentage). Editing and
# switching tabs keep working meanwhile.

function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function Status([string]$id) { $e = Find $id; if ($e -and -not $e.Current.IsOffscreen) { $e.Current.Name } else { '' } }
function MinutesMenu([string]$id) {
  $m = $null
  for ($try = 0; $try -lt 3 -and -not $m; $try++) {
    (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 150; $m = Find $id; if ($m) { break } }
  }
  $m.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 300
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build -Extra @('--fake-audio', $LongSpeech, '--fake-llm')

  # --- Whisper: measured percentage + bar, then a clear outcome --------------------------------
  SafeType "メモ"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  $seen = @(); $bar = $false; $deadline = (Get-Date).AddSeconds(120)
  while ((Get-Date) -lt $deadline) {
    $t = Status 'RecordingStatusText'
    if ($t -and $seen -notcontains $t) { $seen += $t }
    if (IsShown 'TranscribeProgress') { $bar = $true }
    if ($t -match '文字起こし (完了|失敗|中止)') { break }
    Start-Sleep -Milliseconds 100
  }
  $percents = @($seen | ForEach-Object { if ($_ -match '文字起こし (\d+)%') { [int]$Matches[1] } })
  Check "transcription shows a percentage" ($percents.Count -ge 2) ($seen -join ' | ')
  Check "percentage never goes backwards" (@($percents | Sort-Object) -join ',' -eq ($percents -join ',')) ($percents -join ',')
  Check "progress bar shown while transcribing" $bar
  Check "outcome shown: 完了" (($seen -join '|') -match '文字起こし 完了') ($seen -join ' | ')

  # --- AI minutes: step / elapsed / characters, no percentage ---------------------------------
  $note = (Value)
  MinutesMenu 'MinutesCreate'
  $mseen = @(); $deadline = (Get-Date).AddSeconds(30); $typed = $false
  while ((Get-Date) -lt $deadline) {
    $t = Status 'MinutesStatusText'
    if ($t -and $mseen -notcontains $t) { $mseen += $t }
    if (-not $typed -and $t -match '工程') {
      # Editing and tab switching while the AI works.
      $typed = $true
      SafeKeys 0x11, 0x4E; Start-Sleep 0.5; SafeType "作業中に書く"; Start-Sleep 0.5
      SafeKeys 0x11, 0x09; Start-Sleep 0.5
    }
    if ($t -match 'AI議事録 (完了|失敗|中止)') { break }
    Start-Sleep -Milliseconds 100
  }
  $all = $mseen -join ' | '
  Check "minutes: step 1 (correction) shown" ($all -match '工程1/2：文字起こしを校正') $all
  Check "minutes: step 2 (minutes) shown" ($all -match '工程2/2：議事録を作成') $all
  Check "minutes: model loading shown as a step" ($all -match 'AIモデルを読み込み中') $all
  Check "minutes: characters written shown" ($all -match '\d+字') $all
  Check "minutes: elapsed time shown" ($all -match '\d\d:\d\d') $all
  Check "minutes: no made-up percentage" (-not ($all -match '%')) $all
  Check "minutes: outcome shown: 完了" ($all -match 'AI議事録 完了') $all
  Start-Sleep 1.5
  $written = @(Get-ChildItem (Join-Path $NotesDir 'notes') -Filter *.md | Where-Object { [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8) -eq '作業中に書く' })
  Check "typing in a new tab during the AI job was saved" ($typed -and $written.Count -eq 1)
  Check "Ctrl+Tab switched tabs during the AI job" ((TabElements | Where-Object { $_.Current.ItemStatus -eq '選択中' }).Current.Name -ne $written[0].BaseName)

  # --- Cancel -----------------------------------------------------------------------------------
  Invoke-Button ((TabElements)[0].Current.Name); Start-Sleep 0.5
  MinutesMenu 'MinutesCreate'; Start-Sleep 0.4
  Invoke-Button '中止'
  $t = ''; for ($i = 0; $i -lt 40 -and -not ($t -match '中止'); $i++) { $t = Status 'MinutesStatusText'; Start-Sleep -Milliseconds 100 }
  Check "cancel -> 'AI議事録 中止' shown" ($t -match 'AI議事録 中止') $t
  Start-Sleep 6
  Check "status clears after a few seconds" (-not (IsShown 'MinutesStatusText'))
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
