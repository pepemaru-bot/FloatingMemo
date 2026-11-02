param([string]$NotesDir, [string]$Speech, [string]$LongSpeech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Recording apart from AI processing: a recording can start while earlier recordings are being
# transcribed, several recordings in a row each go to their own note, unfinished jobs continue
# after a restart, and minutes asked for during a transcription run after it. Made-up speech only.
# Speech recognition runs on the CPU here (--acceleration CpuOnly): slow enough that the next
# recording really starts while the previous one is being transcribed.

$recordings = Join-Path (Split-Path $NotesDir) 'recordings' # the test folder (--recordings-dir)
function Sessions { if (Test-Path $recordings) { @(Get-ChildItem $recordings -Directory -Filter 'rec-*') } else { @() } }
function NoteFiles { @(Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '*.md' -ErrorAction SilentlyContinue) }
function NoteWith([string]$marker) {
  foreach ($f in NoteFiles) { $t = [IO.File]::ReadAllText($f.FullName, [Text.Encoding]::UTF8); if ($t.Contains($marker)) { return $t } }
  $null
}
function Blocks([string]$text) { if ($text) { [regex]::Matches($text, '以下、AIによる文字起こし').Count } else { 0 } }
function WaitFor([scriptblock]$condition, $sec = 30) {
  for ($i = 0; $i -lt $sec * 10; $i++) { if (& $condition) { return $true }; Start-Sleep -Milliseconds 100 }
  $false
}
function StatusText { $e = Find "RecordingStatusText"; if ($e) { $e.Current.Name } else { "" } }
function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
# Ctrl+N, then the marker; refuses to go on if no new tab opened (the marker would land in another note).
function NewNote([string]$marker) {
  $before = (TabElements).Count
  SafeKeys 0x11, 0x4E
  if (-not (WaitFor { (TabElements).Count -gt $before } 5)) { throw "ABORT: Ctrl+N opened no new note" }
  Start-Sleep 0.5; SafeType $marker; Start-Sleep 1
}

try {
  if (Test-Path $recordings) { Remove-Item $recordings -Recurse -Force }
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }

  # --- 1. Three recordings in a row while the first is still being transcribed -------------
  Launch -Build -Extra @('--fake-audio', $LongSpeech, '--acceleration', 'CpuOnly') -NoDraft
  NewNote "面談A"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  Check "A: transcription job started" (WaitFor { (JobsState) -match 'running=Transcribe' } 20) (JobsState)

  NewNote "面談B"
  $startedWhileBusy = (JobsState) -match 'running=Transcribe'
  $clock = [Diagnostics.Stopwatch]::StartNew()
  Invoke-Button "録音開始"
  $recordingSoon = WaitFor { (StatusText) -match '^録音中' } 5
  $clock.Stop()
  Check "B: recording starts while A is transcribed" ($startedWhileBusy -and $recordingSoon) "$(JobsState) | $(StatusText)"
  Check "B: recording starts at once (< 1.5 s)" ($clock.ElapsedMilliseconds -lt 1500) "$($clock.ElapsedMilliseconds) ms"
  Start-Sleep 1.5
  Check "status shows the recording and the transcription" ((StatusText) -match '^録音中 .*・(文字起こし|音声モデル)') (StatusText)
  Invoke-Button "録音停止"

  NewNote "面談C"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  Check "three recordings queued or done (none refused)" (WaitFor { (JobsState) -match 'unfinished=[1-3] ' -or (Blocks (NoteWith '面談C')) -eq 1 } 10) (JobsState)

  Check "all transcriptions finish" (WaitJobs 300) (JobsState)
  Start-Sleep 2
  $a = NoteWith '面談A'; $b = NoteWith '面談B'; $c = NoteWith '面談C'
  Check "A got exactly its transcript" ((Blocks $a) -eq 1 -and $a -match '定例会議') (Esc $a)
  Check "B got exactly its transcript" ((Blocks $b) -eq 1 -and $b -match '定例会議') (Esc $b)
  Check "C got exactly its transcript" ((Blocks $c) -eq 1 -and $c -match '定例会議') (Esc $c)
  Check "nothing mixed: each note has only its own marker" (-not ($a -match '面談[BC]') -and -not ($b -match '面談[AC]') -and -not ($c -match '面談[AB]'))
  Check "recordings deleted after their successful transcription" ((Sessions).Count -eq 0) ((Sessions) -join ',')
  Check "no error shown" (-not ((InfoBarText "AudioInfoBar") -match 'できません'))
  CloseWindow; [void](WaitExit 20)

  # --- 2. Closing during a transcription: the job continues at the next start ---------------
  Launch -Extra @('--fake-audio', $LongSpeech, '--acceleration', 'CpuOnly') -NoDraft
  NewNote "面談D"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  Check "D: transcription running" (WaitFor { (JobsState) -match 'running=Transcribe' } 20) (JobsState)
  CloseWindow; Start-Sleep 1.5
  $dlg = FindDialog
  Check "closing with unfinished jobs asks (nothing is lost)" ($dlg -ne $null)
  if ($dlg) { ClickDialogButton $dlg "6" }  # IDYES
  Check "app exits" (WaitExit 20)
  $queue = Get-Content (Join-Path $recordings 'ai-jobs.json') -Raw -Encoding UTF8 | ConvertFrom-Json
  Check "D: job saved as interrupted with its recording" (@($queue).Count -eq 1 -and $queue[0].State -in @('Interrupted', 'Running') -and (Test-Path $queue[0].SessionDirectory)) ($queue | ConvertTo-Json -Compress)
  Check "D: note not touched yet" ((Blocks (NoteWith '面談D')) -eq 0)

  Launch -Extra @('--fake-audio', $LongSpeech, '--acceleration', 'CpuOnly') -NoDraft
  Check "D: resumed after the restart and finished" (WaitJobs 300) (JobsState)
  Start-Sleep 2
  $d = NoteWith '面談D'
  Check "D: transcript went into its note" ((Blocks $d) -eq 1 -and $d -match '定例会議') (Esc $d)
  Check "D: not offered as a leftover recording" (-not ((InfoBarText 'AudioInfoBar') -match '前回の録音が残っています'))
  Check "D: recording deleted after success" ((Sessions).Count -eq 0) ((Sessions) -join ',')
  CloseWindow; [void](WaitExit 20)

  # --- 2b. The app is killed during a transcription (crash, power loss): restored at the next start ---
  Launch -Extra @('--fake-audio', $LongSpeech, '--acceleration', 'CpuOnly') -NoDraft
  NewNote "面談K"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  Check "K: transcription running" (WaitFor { (JobsState) -match 'running=Transcribe' } 20) (JobsState)
  Stop-Process -Id $script:app.Id -Force; Start-Sleep 2
  Check "K: recording kept after the crash" ((Sessions).Count -eq 1) ((Sessions) -join ',')
  Launch -Extra @('--fake-audio', $LongSpeech, '--acceleration', 'CpuOnly') -NoDraft
  Check "K: resumed after the crash and finished" (WaitJobs 300) (JobsState)
  Start-Sleep 2
  $k = NoteWith '面談K'
  Check "K: transcript went into its note once" ((Blocks $k) -eq 1 -and $k -match '定例会議') (Esc $k)
  Check "K: recording deleted after success" ((Sessions).Count -eq 0) ((Sessions) -join ',')
  CloseWindow; [void](WaitExit 20)

  # --- 3. Minutes asked for while the note's transcription waits: they run after it -----------
  Launch -Extra @('--fake-audio', $Speech, '--fake-llm', '--acceleration', 'CpuOnly') -NoDraft
  NewNote "面談E"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  $create = $null; [void](WaitFor { ($script:create = Find 'MinutesCreate') -ne $null } 5)
  if ($script:create) { $script:create.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  Start-Sleep 0.5
  $msg = InfoBarText 'AudioInfoBar'
  Check "minutes accepted while the transcription is pending" ($msg -match '予約しました|作成中|下書きを作成しました') $msg
  Check "minutes and transcription finish" (WaitJobs 120) (JobsState)
  Start-Sleep 1
  $minutes = @(NoteFiles | Where-Object { $_.Name -match 'AI議事録' }) | Select-Object -First 1
  $mtext = if ($minutes) { [IO.File]::ReadAllText($minutes.FullName, [Text.Encoding]::UTF8) } else { '' }
  # The test model writes the first sentence of the transcript it was given.
  Check "minutes made from the note with its transcript" ($mtext -match '資料を準備') (Esc $mtext.Substring(0, [Math]::Min(200, $mtext.Length)))
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
