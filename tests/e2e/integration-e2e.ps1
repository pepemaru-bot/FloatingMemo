param([string]$NotesDir, [string]$LongSpeech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Integration: autosave, tab switching and transcription running at the same time must never
# move text from one note into another; an upgrade from the single-memo version keeps draft.md.

function ActiveTab {
  (AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' -and $_.Current.ItemStatus -eq '選択中' } | Select-Object -First 1).Current.Name
}
function NotePath([string]$title) {
  $p = Join-Path $NotesDir "notes\$title.md"
  if (Test-Path $p) { $p } else { Join-Path $NotesDir "$title.md" }
}
function ReadNote([string]$title) { $p = NotePath $title; if (Test-Path $p) { [IO.File]::ReadAllText($p, [Text.UTF8Encoding]::new($false)) } else { '' } }
function WaitIdle($sec = 180) {
  $deadline = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $deadline) { if ((Find "録音開始" -ByName) -ne $null) { return $true }; Start-Sleep -Milliseconds 250 }
  $false
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null
  # Single-memo version's data: one draft.md with real-looking content.
  $legacy = "# 以前のメモ`n- 失ってはいけない内容`n"
  [IO.File]::WriteAllText((Join-Path $NotesDir 'draft.md'), $legacy, [Text.UTF8Encoding]::new($false))
  $legacyBytes = [IO.File]::ReadAllBytes((Join-Path $NotesDir 'draft.md'))

  Launch -Build -Extra @('--fake-audio', $LongSpeech)
  Check "upgrade: draft.md opens as the first tab" ((ActiveTab) -eq 'draft')

  # Three notes: draft + A + B
  SafeKeys 0x11, 0x4E; Start-Sleep -Milliseconds 800; $a = ActiveTab
  SafeKeys 0x11, 0x4E; Start-Sleep -Milliseconds 800; $b = ActiveTab
  $titles = @('draft', $a, $b)

  # --- 1. Type + switch immediately, 15 times (autosave is always pending at the switch) ----
  $expected = @{ 'draft' = $legacy; $a = ''; $b = '' }
  SafeKeys 0x11, 0x09; Start-Sleep -Milliseconds 400   # -> draft
  for ($i = 0; $i -lt 15; $i++) {
    $tab = ActiveTab
    $token = "[$tab#$i]"
    SafeKeys 0x11, 0x23   # end of note
    SafeType $token
    $expected[$tab] += $token
    SafeKeys 0x11, 0x09   # Ctrl+Tab right away
    Start-Sleep -Milliseconds 250
  }
  Start-Sleep 2
  foreach ($t in $titles) {
    $content = ReadNote $t
    Check "rapid switching: '$t' has exactly its own text" ($content -eq $expected[$t]) (Esc $content)
  }
  $leaks = foreach ($t in $titles) { $c = ReadNote $t; foreach ($o in $titles | Where-Object { $_ -ne $t }) { if ($c -match [regex]::Escape("[$o#")) { "$o->$t" } } }
  Check "no note contains another note's text" (-not $leaks) ($leaks -join ', ')

  # --- 2. Transcription finishes while the user types in another tab -------------------------
  Invoke-Button $a; Start-Sleep -Milliseconds 600
  $aBefore = ReadNote $a
  Invoke-Button "録音開始"; Start-Sleep 1
  Invoke-Button "録音停止"                       # ~95 s of speech: transcription takes a while
  Start-Sleep -Milliseconds 800
  Invoke-Button $b; Start-Sleep -Milliseconds 500
  $bTyped = ""
  for ($i = 0; $i -lt 6 -and -not (Find "録音開始" -ByName); $i++) {
    SafeKeys 0x11, 0x23; SafeType "<B$i>"; $bTyped += "<B$i>"; Start-Sleep -Milliseconds 700
  }
  Check "transcription finished" (WaitIdle 240)
  Start-Sleep 2
  $aAfter = ReadNote $a; $bAfter = ReadNote $b
  Check "typing during transcription happened in B" ($bTyped.Length -gt 0 -and $bAfter.EndsWith($bTyped)) "typed '$bTyped'"
  Check "transcript went into A (the recorded note)" ($aAfter.StartsWith($aBefore) -and $aAfter -match '会議') "+$($aAfter.Length - $aBefore.Length) chars"
  Check "B contains no transcript" (-not ($bAfter -match '会議|議事録'))
  Check "A contains none of B's typing" (-not ($aAfter -match '<B\d>'))
  Check "draft.md untouched by the transcript" (-not ((ReadNote 'draft') -match '会議'))

  # --- 3. Restart: everything restored, nothing lost ------------------------------------------
  $before = @{}; foreach ($t in $titles) { $before[$t] = ReadNote $t }
  CloseWindow
  Check "exit" (WaitExit 20)
  foreach ($t in $titles) { Check "after exit '$t' unchanged on disk" ((ReadNote $t) -eq $before[$t]) }
  Launch -Extra @('--fake-audio', $LongSpeech)
  $tabs = (AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' } | ForEach-Object { $_.Current.Name }) -join '|'
  Check "restart restores all three tabs" ($tabs -eq ($titles -join '|')) $tabs
  Check "legacy draft content still at the start of draft.md" ((ReadNote 'draft').StartsWith($legacy))
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
