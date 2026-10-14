param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Markdown editor behaviour inside the real app (WebView2 + CodeMirror).
# Not covered here (needs a person): the Japanese IME itself, and clicking checkboxes with the
# mouse (verified in the browser-based editor test; see DEVELOPMENT_LOG).

function Text { (Value) -replace "`r", "`n" }
function NewNote { SafeKeys 0x11, 0x4E; Start-Sleep -Milliseconds 800 }

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null

  # A note with syntax the editor does not render: it must survive editing byte for byte.
  $unusual = "---`ntags: [memo]`n---`n| a | b |`n|---|---|`n| 1 | 2 |`n`n脚注[^1] と <kbd>Ctrl</kbd> と [[ウィキリンク]] と ==ハイライト==`n`n[^1]: 脚注の本文`n`t- タブ字下げ`n"
  [IO.File]::WriteAllText((Join-Path $NotesDir 'draft.md'), $unusual, [Text.UTF8Encoding]::new($false))
  Launch -Build
  Check "Markdown editor is in use" ((EditorKind) -eq 'web') (EditorKind)

  # --- Unknown syntax is never rewritten ------------------------------------------------
  SafeKeys 0x11, 0x23; SafeType "追記"; Start-Sleep -Milliseconds 1500
  Check "unsupported syntax preserved exactly (only the typed text added)" ((FileText) -eq ($unusual + "追記")) (Esc (FileText))

  # --- Lists ----------------------------------------------------------------------------
  NewNote
  SafeType "- 一つ目"; SafeKeys 0x0D; SafeType "二つ目"
  Check "Enter continues a bullet list" ((Text) -eq "- 一つ目`n- 二つ目") (Esc (Text))
  SafeKeys 0x0D; SafeKeys 0x0D; SafeType "本文"
  Check "Enter on an empty item ends the list" ((Text) -eq "- 一つ目`n- 二つ目`n本文") (Esc (Text))

  NewNote
  SafeType "- [ ] 買い物"; SafeKeys 0x0D; SafeType "掃除"
  Check "Enter continues a task list with an empty box" ((Text) -eq "- [ ] 買い物`n- [ ] 掃除") (Esc (Text))

  NewNote
  SafeType "1. 最初"; SafeKeys 0x0D; SafeType "次"
  Check "Enter numbers an ordered list" ((Text) -eq "1. 最初`n2. 次") (Esc (Text))

  # --- Tab / Shift+Tab -------------------------------------------------------------------
  NewNote
  SafeType "- 親"; SafeKeys 0x0D; SafeKeys 0x09; SafeType "子"
  Check "Tab indents a list item (not focus change)" ((Text) -eq "- 親`n`t- 子" -and (OurEditorFocused)) (Esc (Text))
  SafeKeys 0x10, 0x09
  Check "Shift+Tab outdents it" ((Text) -eq "- 親`n- 子") (Esc (Text))

  # --- Formatting / undo -----------------------------------------------------------------
  NewNote
  SafeType "強調"; SafeKeys 0x11, 0x41   # Ctrl+A selects the text
  SafeKeys 0x11, 0x42                    # Ctrl+B
  Check "Ctrl+B wraps the selection in **" ((Text) -eq "**強調**") (Esc (Text))
  SafeKeys 0x11, 0x42
  Check "Ctrl+B again removes it" ((Text) -eq "強調") (Esc (Text))
  SafeKeys 0x11, 0x49                    # Ctrl+I
  Check "Ctrl+I wraps the selection in *" ((Text) -eq "*強調*") (Esc (Text))
  SafeKeys 0x11, 0x5A
  Check "Ctrl+Z undoes" ((Text) -eq "強調") (Esc (Text))
  SafeKeys 0x11, 0x59
  Check "Ctrl+Y redoes" ((Text) -eq "*強調*") (Esc (Text))

  # --- Saved as plain Markdown -------------------------------------------------------------
  Start-Sleep -Milliseconds 1500
  $files = Get-ChildItem (Join-Path $NotesDir 'notes') -Filter *.md | ForEach-Object { [IO.File]::ReadAllText($_.FullName) }
  Check "notes are stored as plain Markdown (no HTML)" (($files -join '') -notmatch '<(div|span|p|br)\b') (($files | Select-Object -First 3) -join ' / ')
  Check "task list saved with [ ] markers" (($files -join "`n") -match '- \[ \] 掃除')

  # --- F6 leaves the editor (Tab is indentation inside it) ---------------------------------
  SafeKeys 0x75; Start-Sleep -Milliseconds 500
  Check "F6 moves keyboard focus out of the editor" ((Mirror 'EditorFocusState') -eq 'blurred')

  CloseWindow
  Check "app exits" (WaitExit)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
