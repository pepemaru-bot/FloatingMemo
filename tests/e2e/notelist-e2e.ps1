param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Tabs are a workspace, Markdown files are the saved data: closing never deletes or creates
# notes, Ctrl+O lists every saved note (search, sort), and opening never duplicates a tab.

function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function TabTitles { (TabElements | ForEach-Object { $_.Current.Name }) -join '|' }
function ActiveTab { (TabElements | Where-Object { $_.Current.ItemStatus -eq '選択中' } | Select-Object -First 1).Current.Name }
function NoteFiles { (Get-ChildItem $NotesDir -Recurse -Filter *.md | ForEach-Object { $_.FullName.Substring($NotesDir.Length) } | Sort-Object) -join '|' }
function KeysToApp([uint16[]]$vks) { if (-not [K]::ComboInto($script:h, $vks)) { throw "ABORT: focus left the test window" } }
function ListTitles { (@(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteListItem' }) | ForEach-Object { $_.Current.Name }) -join '|' }
function OpenList {
  KeysToApp 0x11, 0x4F   # Ctrl+O
  for ($i = 0; $i -lt 20 -and -not (Find 'NoteSearchBox'); $i++) { Start-Sleep -Milliseconds 150 }
  Start-Sleep -Milliseconds 300
}
function SearchAndOpen([string]$text) {
  (Find 'NoteSearchBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text)
  Start-Sleep -Milliseconds 400
  KeysToApp 0x0D; Start-Sleep 1   # Enter opens the first match
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build
  SafeType "既存の下書き"; Start-Sleep 1.5
  SafeKeys 0x11, 0x4E; Start-Sleep 1; SafeType "Aの本文 りんご"; Start-Sleep 1.5
  $titleA = ActiveTab
  SafeKeys 0x11, 0x4E; Start-Sleep 1; SafeType "Bの本文 みかん"; Start-Sleep 1.5
  $titleB = ActiveTab
  $files = NoteFiles

  # x closes the tab only.
  Invoke-Button "$titleB を閉じる"; Start-Sleep 1
  Check "x closes the tab" (-not ((TabTitles) -match [regex]::Escape($titleB))) (TabTitles)
  Check "x never deletes the note file" ((NoteFiles) -eq $files) (NoteFiles)

  # Ctrl+O: every saved note, newest first, open notes included.
  OpenList
  Check "Ctrl+O opens the saved-notes list with search and sort" ((Find 'NoteSearchBox') -and (Find 'NoteSortBox'))
  Check "list shows all saved notes, newest first" ((ListTitles) -eq "$titleB|$titleA|draft") (ListTitles)
  SearchAndOpen "みかん"
  Check "search (in the text) + Enter opens the closed note as a tab" ((ActiveTab) -eq $titleB -and (TabTitles) -match [regex]::Escape($titleB)) (TabTitles)

  # An open note is switched to, never duplicated.
  OpenList
  SearchAndOpen "りんご"
  $countA = @(TabElements | Where-Object { $_.Current.Name -eq $titleA }).Count
  Check "opening an already open note switches to it (no duplicate tab)" ($countA -eq 1 -and (ActiveTab) -eq $titleA) (TabTitles)

  OpenList
  (Find 'NoteSearchBox').GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue("存在しない言葉")
  Start-Sleep -Milliseconds 400
  Check "no match -> empty list with a message" ((ListTitles) -eq '' -and (Find 'NoteListCount').Current.Name -match '一致するノートはありません')
  KeysToApp 0x1B; Start-Sleep 0.5
  Check "Esc closes only the list" ([K]::IsWindowVisible($script:h) -and -not (Find 'NoteSearchBox'))

  # Unsaved changes are saved before closing.
  EnsureFocus; SafeType " 追記"
  SafeKeys 0x11, 0x57; Start-Sleep 1   # Ctrl+W right after typing
  $fileA = Join-Path $NotesDir "notes\$titleA.md"
  $textA = [IO.File]::ReadAllText($fileA, [Text.Encoding]::UTF8)
  Check "closing saves unsaved changes first" ($textA -eq "Aの本文 りんご 追記") (Esc $textA)

  # Close every tab: no tab, nothing created or deleted.
  for ($i = 0; $i -lt 5 -and (TabElements).Count -gt 0; $i++) { EnsureFocus; if (OurEditorFocused) { SafeKeys 0x11, 0x57 } else { KeysToApp 0x11, 0x57 }; Start-Sleep 1 }
  Check "last tab closed -> no tabs" ((TabElements).Count -eq 0) (TabTitles)
  Check "empty state shown" (IsShown 'EmptyStateText')
  Check "no note created or deleted by closing tabs" ((NoteFiles) -eq $files) (NoteFiles)
  Invoke-Button "録音開始"; Start-Sleep 0.7
  Check "recording without a note -> explained, not started" ((InfoBarText "AudioInfoBar") -match 'ノートを開いてから録音' -and -not (IsShown "RecordingStatusText"))

  # Restart keeps the empty workspace; notes open from the list.
  CloseWindow; Check "exit with no tabs" (WaitExit 20)
  Launch
  Check "restart with no tabs (nothing auto-created)" ((TabElements).Count -eq 0 -and (IsShown 'EmptyStateText')) (TabTitles)
  Check "still no new files" ((NoteFiles) -eq $files) (NoteFiles)
  # Bring the test window to the front with its own hotkey (no editor to wait for here).
  for ($i = 0; $i -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $i++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
  OpenList
  SearchAndOpen "draft"
  Check "note opened from the list after restart" ((ActiveTab) -eq 'draft' -and -not (IsShown 'EmptyStateText')) (TabTitles)
  SafeType "!"; Start-Sleep 1.5
  $draft = [IO.File]::ReadAllText((Join-Path $NotesDir 'draft.md'), [Text.Encoding]::UTF8)
  Check "typing works again" ($draft -eq "既存の下書き!") "$(Esc $draft) kind=$(EditorKind) mirror=$(Esc (Value))"
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
