param([string]$NotesDir, [string]$Speech, [string]$LongSpeech, [string]$HaasDir = $env:FM_HAAS_DIR, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Haas-ja (experimental) through the app: chosen with --speech-recognizer, files from --haas-dir
# (speech.exe, speech.dll, a Haas-ja GGUF, the Silero VAD GGUF; FM_HAAS_DIR). Made-up speech only.
# Skipped (one PASS line saying so) when no Haas folder is given.

$recordings = Join-Path (Split-Path $NotesDir) 'recordings'
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
function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function NewNote([string]$marker) {
  $before = (TabElements).Count
  SafeKeys 0x11, 0x4E
  if (-not (WaitFor { (TabElements).Count -gt $before } 5)) { throw "ABORT: Ctrl+N opened no new note" }
  Start-Sleep 0.5; SafeType $marker; Start-Sleep 1
}
function StatusText { $e = Find "RecordingStatusText"; if ($e) { $e.Current.Name } else { "" } }

try {
  if (-not $HaasDir -or -not (Test-Path (Join-Path $HaasDir 'speech.exe'))) {
    Check "skipped: no Haas-ja folder (FM_HAAS_DIR)" $true
    return
  }
  if (Test-Path $recordings) { Remove-Item $recordings -Recurse -Force }
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }

  # --- 1. Haas-ja transcribes into the recorded note; a second recording starts meanwhile ------
  Launch -Build -Extra @('--fake-audio', $LongSpeech, '--haas-dir', $HaasDir, '--speech-recognizer', 'haas-ja') -NoDraft
  NewNote "面談H1"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  Check "H1: Haas-ja job started" (WaitFor { (JobsState) -match 'unfinished=1' } 10) (JobsState)
  NewNote "面談H2"
  Invoke-Button "録音開始"
  Check "H2: recording starts while H1 is transcribed or queued" (WaitFor { (StatusText) -match '^録音中' } 5) (StatusText)
  Start-Sleep 1; Invoke-Button "録音停止"
  Check "both transcriptions finish" (WaitJobs 180) (JobsState)
  Start-Sleep 2
  $h1 = NoteWith '面談H1'; $h2 = NoteWith '面談H2'
  Check "H1 got exactly its transcript (Haas-ja)" ((Blocks $h1) -eq 1 -and $h1 -match '定例会議') (Esc $h1)
  Check "H2 got exactly its transcript (Haas-ja)" ((Blocks $h2) -eq 1 -and $h2 -match '定例会議') (Esc $h2)
  Check "sentences end with 。 (pieces are not run together)" (([regex]::Matches($h1, '。')).Count -ge 10) (Esc $h1)
  Check "recordings deleted after success" ((Sessions).Count -eq 0) ((Sessions) -join ',')
  Check "no error shown" (-not ((InfoBarText "AudioInfoBar") -match 'できません'))
  $log = Join-Path $env:TEMP 'FloatingMemo\transcription-diagnostics.log'
  $last = if (Test-Path $log) { (Get-Content $log -Encoding UTF8 | Select-Object -Last 1) } else { '' }
  Check "diagnostics name Haas-ja and hold no transcript text" ($last -match 'Haas-ja' -and $last -notmatch '定例会議') $last
  CloseWindow; [void](WaitExit 20)

  # --- 2. Haas-ja chosen but its files are missing: clear error, audio kept --------------------
  $empty = Join-Path (Split-Path $NotesDir) 'no-haas'
  New-Item -ItemType Directory -Force $empty | Out-Null
  Launch -Extra @('--fake-audio', $Speech, '--haas-dir', $empty, '--speech-recognizer', 'haas-ja') -NoDraft
  NewNote "面談H3"
  Invoke-Button "録音開始"; Start-Sleep 1; Invoke-Button "録音停止"
  [void](WaitJobs 30)
  $info = ''; [void](WaitFor { ($script:info = InfoBarText 'AudioInfoBar') -match 'Haas-ja' } 10)
  Check "missing Haas-ja files -> error names them, points to the README" ($script:info -match 'Haas-ja' -and $script:info -match 'README') $script:info
  Check "audio kept for a retry" ((Sessions).Count -eq 1)
  Check "note untouched" ((Blocks (NoteWith '面談H3')) -eq 0)
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
