param([string]$NotesDir, [string]$Speech, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function TabTitles { (TabElements | ForEach-Object { $_.Current.Name }) -join '|' }
function ActiveTab { (TabElements | Where-Object { $_.Current.ItemStatus -eq '選択中' } | Select-Object -First 1).Current.Name }
function NoteFile([string]$title) { Join-Path $NotesDir "notes\$title.md" }
function ReadNote([string]$path) { if (Test-Path $path) { [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false)) } else { $null } }
function WaitIdle($sec = 60) {
  $deadline = (Get-Date).AddSeconds($sec)
  while ((Get-Date) -lt $deadline) { if ((Find "録音開始" -ByName) -ne $null) { return $true }; Start-Sleep -Milliseconds 200 }
  $false
}
function NewestNote { Get-ChildItem (Join-Path $NotesDir 'notes') -Filter *.md -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1 }

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null

  # --- Upgrade from the single-memo version: draft.md opens in place, untouched -----------
  $draftText = "# 既存のメモ`n- 消えてはいけない`n"
  [IO.File]::WriteAllText((Join-Path $NotesDir 'draft.md'), $draftText, [Text.UTF8Encoding]::new($false))
  $draftBytes = [IO.File]::ReadAllBytes((Join-Path $NotesDir 'draft.md'))
  $draftTime = (Get-Item (Join-Path $NotesDir 'draft.md')).LastWriteTimeUtc
  Launch -Build -Extra @('--fake-audio', $Speech)
  Check "existing draft.md is the first tab" ((TabTitles) -eq 'draft' -and (ActiveTab) -eq 'draft') (TabTitles)
  Check "draft content shown" (((Value) -replace "`r", "`n") -eq $draftText) (Esc (Value))

  # --- Ctrl+N: new note, file only after typing -------------------------------------------
  SafeKeys 0x11, 0x4E; Start-Sleep 1
  Check "Ctrl+N opens a second tab and activates it" ((TabElements).Count -eq 2 -and (ActiveTab) -ne 'draft') (TabTitles)
  $title1 = ActiveTab
  Check "new note has no file until it has text" (-not (Test-Path (NoteFile $title1)))
  Check "new note editor is empty and focused" ((Value) -eq "" -and (OurEditorFocused))
  SafeType "ノート1の本文"; Start-Sleep -Milliseconds 1500
  Check "new note saved to notes\<title>.md" ((ReadNote (NoteFile $title1)) -eq "ノート1の本文") (ReadNote (NoteFile $title1))

  # --- "+" button: third note -------------------------------------------------------------
  Invoke-Button "新規ノート"; Start-Sleep 1
  $title2 = ActiveTab
  Check "+ button opens a third tab" ((TabElements).Count -eq 3 -and $title2 -ne $title1) (TabTitles)
  SafeType "ノート2"; Start-Sleep -Milliseconds 300

  # --- Ctrl+Tab / Ctrl+Shift+Tab keep text and caret per note ------------------------------
  SafeKeys 0x11, 0x09; Start-Sleep 1   # Ctrl+Tab -> wraps to draft
  Check "Ctrl+Tab moves to the next tab (wraps)" ((ActiveTab) -eq 'draft') (ActiveTab)
  Check "draft text intact after switching" (((Value) -replace "`r", "`n") -eq $draftText)
  Check "unsaved text of the left tab was saved on switch" ((ReadNote (NoteFile $title2)) -eq "ノート2") (ReadNote (NoteFile $title2))
  SafeKeys 0x11, 0x10, 0x09; Start-Sleep 1   # Ctrl+Shift+Tab -> back to title2
  Check "Ctrl+Shift+Tab moves back" ((ActiveTab) -eq $title2) (ActiveTab)
  SafeType " 続き"; Start-Sleep -Milliseconds 1500
  Check "caret restored at the end: typing continues the same note" ((ReadNote (NoteFile $title2)) -eq "ノート2 続き") (ReadNote (NoteFile $title2))
  Check "other notes unchanged" ((ReadNote (NoteFile $title1)) -eq "ノート1の本文" -and [IO.File]::ReadAllBytes((Join-Path $NotesDir 'draft.md')).Length -eq $draftBytes.Length)

  # --- Undo never brings another note's text into this note -------------------------------
  Invoke-Button $title1; Start-Sleep 1
  SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 300; SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 300   # Ctrl+Z x2
  $afterUndo = Value
  Check "Ctrl+Z after switching does not show another note's text" (-not ($afterUndo -match 'ノート2|既存のメモ')) (Esc $afterUndo)
  Start-Sleep -Milliseconds 1500
  Check "Ctrl+Z after switching does not leak into the file" (-not ((ReadNote (NoteFile $title1)) -match 'ノート2|既存のメモ')) (ReadNote (NoteFile $title1))
  $note1Now = ReadNote (NoteFile $title1)

  # --- F2: rename ---------------------------------------------------------------------------
  SafeKeys 0x71; Start-Sleep 1   # F2
  $box = Find "RenameBox"
  Check "F2 opens the rename box" ($box -ne $null)
  if ($box) {
    $box.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue("会議/メモ")
    [K]::Combo(0x0D); Start-Sleep 1
  }
  Check "renamed tab shows sanitized title" ((ActiveTab) -eq '会議_メモ') (TabTitles)
  # Renaming saves first and then moves the file: allow for a slow disk / busy machine.
  $renamed = { -not (Test-Path (NoteFile $title1)) -and (ReadNote (NoteFile '会議_メモ')) -eq $note1Now }
  for ($i = 0; $i -lt 25 -and -not (& $renamed); $i++) { Start-Sleep -Milliseconds 200 }
  $files = (Get-ChildItem (Split-Path (NoteFile '会議_メモ')) -Filter *.md | ForEach-Object Name) -join '|'
  Check "file renamed, content kept" (& $renamed) "files=$files new=$(Esc (ReadNote (NoteFile '会議_メモ'))) expected=$(Esc $note1Now)"
  SafeType "!"
  for ($i = 0; $i -lt 25 -and (ReadNote (NoteFile '会議_メモ')) -ne "$note1Now!"; $i++) { Start-Sleep -Milliseconds 200 }
  Check "editing after rename saves to the new file" ((ReadNote (NoteFile '会議_メモ')) -eq "$note1Now!") (Esc (ReadNote (NoteFile '会議_メモ')))

  # --- Ctrl+W closes the tab, never the file; Ctrl+Shift+T reopens -------------------------
  SafeKeys 0x11, 0x57; Start-Sleep 1   # Ctrl+W
  Check "Ctrl+W closes the tab" (-not ((TabTitles) -match '会議_メモ')) (TabTitles)
  Check "closing keeps the file" ((ReadNote (NoteFile '会議_メモ')) -eq "$note1Now!")
  SafeKeys 0x11, 0x10, 0x54; Start-Sleep 1   # Ctrl+Shift+T
  Check "Ctrl+Shift+T reopens it" ((ActiveTab) -eq '会議_メモ') (TabTitles)

  # --- Transcript goes to the note that was recorded, even after switching tabs ------------
  Invoke-Button $title2; Start-Sleep 1
  $beforeTitle2 = ReadNote (NoteFile $title2)
  Invoke-Button "録音開始"; Start-Sleep 1
  Invoke-Button "draft"; Start-Sleep 1        # switch away while recording
  Check "recording continues while another tab is active" ((Find "録音停止" -ByName) -ne $null)
  $draftBeforeTranscript = Value
  Invoke-Button "録音停止"
  Check "transcription finishes" (WaitIdle 90)
  Start-Sleep 1
  $t2 = ReadNote (NoteFile $title2)
  Check "transcript inserted into the recorded note" ($t2 -match '会議' -and $t2.StartsWith($beforeTitle2)) (Esc $t2)
  Check "active tab (draft) untouched by the transcript" ((Value) -eq $draftBeforeTranscript -and [IO.File]::ReadAllBytes((Join-Path $NotesDir 'draft.md')).Length -eq $draftBytes.Length)
  Invoke-Button $title2; Start-Sleep 1
  Check "recorded tab shows the transcript" ((Value) -match '会議') (Esc (Value))

  # --- Recorded tab closed before the transcript arrives -> appended to its file ------------
  Invoke-Button "会議_メモ"; Start-Sleep 1
  $closedBefore = ReadNote (NoteFile '会議_メモ')
  Invoke-Button "録音開始"; Start-Sleep 1
  SafeKeys 0x11, 0x57; Start-Sleep 1        # close the recorded tab while recording
  Invoke-Button "録音停止"; [void](WaitIdle 90); Start-Sleep 1
  $closedAfter = ReadNote (NoteFile '会議_メモ')
  Check "transcript for a closed tab is appended to its file" ($closedAfter.StartsWith($closedBefore) -and $closedAfter -match '会議' -and $closedAfter.Length -gt $closedBefore.Length) (Esc $closedAfter)
  Check "user is told where it went" ((InfoBarText "AudioInfoBar") -match '末尾に追加') (InfoBarText "AudioInfoBar")

  # --- External edit + local edit -> conflict, nothing overwritten ---------------------------
  Invoke-Button $title2; Start-Sleep 1
  $path2 = NoteFile $title2
  # Local edit first (still unsaved: autosave waits 0.7 s), then another program saves the file.
  SafeType "ローカル編集"
  [IO.File]::WriteAllText($path2, "外部エディタの内容", [Text.UTF8Encoding]::new($false))
  (Get-Item $path2).LastWriteTimeUtc = (Get-Date).ToUniversalTime().AddSeconds(5)
  Start-Sleep -Milliseconds 1800
  Check "conflict detected, external content not overwritten" ((ReadNote $path2) -eq "外部エディタの内容" -and (InfoBarText "SaveErrorBar") -match '外部で変更') (InfoBarText "SaveErrorBar")
  Invoke-Button "この内容で上書き"; Start-Sleep 1
  $copy = Get-ChildItem (Join-Path $NotesDir 'notes') -Filter "*競合*.md" | Select-Object -First 1
  Check "keep mine: file has the editor text" ((ReadNote $path2) -match 'ローカル編集') (ReadNote $path2)
  Check "keep mine: external text kept as a conflict copy" ($copy -and (ReadNote $copy.FullName) -eq "外部エディタの内容") ($copy.Name)

  # --- Restart: tabs, active tab and text restored -----------------------------------------
  $tabsBefore = TabTitles; $activeBefore = ActiveTab
  CloseWindow
  Check "app exits" (WaitExit)
  Launch -Extra @('--fake-audio', $Speech)
  Check "tabs restored after restart" ((TabTitles) -eq $tabsBefore) "$(TabTitles) (before: $tabsBefore)"
  Check "active tab restored" ((ActiveTab) -eq $activeBefore) (ActiveTab)
  Check "draft.md still byte-identical (never edited)" ([Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $NotesDir 'draft.md'))) -eq [Convert]::ToBase64String($draftBytes) -and (Get-Item (Join-Path $NotesDir 'draft.md')).LastWriteTimeUtc -eq $draftTime)

  # --- Ctrl+S saves immediately -----------------------------------------------------------
  SafeType "即保存"; SafeKeys 0x11, 0x53; Start-Sleep -Milliseconds 400
  Check "Ctrl+S saves without waiting for the debounce" ((ReadNote (NoteFile (ActiveTab))) -match '即保存')
  CloseWindow; [void](WaitExit)
}
catch { "ERROR: $_" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
