param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')

# Edit ⇄ preview with the toolbar button in the app. The preview is display only: the note's
# Markdown (file and editor) never changes, undo history survives, unsafe content stays inert.
# Made-up note; the page reports what it shows as element counts only.

function Probe {
  for ($i = 0; $i -lt 20; $i++) {
    $json = Mirror 'EditorProbe'
    if ($json) { return ($json | ConvertFrom-Json) }
    Start-Sleep -Milliseconds 200
  }
  $null
}
# The mode switch shows the mode the page confirmed (the probe mirror can be a tick behind).
function WaitMode([bool]$preview) {
  $name = if ($preview) { 'プレビュー' } else { 'ソース編集' }
  for ($i = 0; $i -lt 30 -and (Find 'ModeSwitch').Current.ItemStatus -ne $name; $i++) { Start-Sleep -Milliseconds 200 }
  for ($i = 0; $i -lt 15; $i++) {
    Start-Sleep -Milliseconds 200
    $p = Probe
    if ($p -and (($null -ne $p.preview) -eq $preview)) { return $p }
  }
  $p
}
function Text { (Value) -replace "`r", "`n" }

$note = @"
# 架空の定例会議

**太字** と *斜体* と ~~取り消し~~ と ``コード``。

- 親
`t- 子
`t`t- 孫
- [ ] 未完了の作業
- [x] 完了した作業

> 引用

| 項目 | **担当** | 期限 |
|:--|:-:|--:|
| 資料作成 | [担当A](https://example.com) | 10/30 |

<script>alert(1)</script>
<img src="https://example.invalid/x.png" onerror="alert(2)">
"@ -replace "`r", ""

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  New-Item -ItemType Directory -Force $NotesDir | Out-Null
  $path = Join-Path $NotesDir 'draft.md'
  [IO.File]::WriteAllText($path, $note, [Text.UTF8Encoding]::new($false))
  Launch -Build
  Check "note opened in the Markdown editor" ((Text) -eq $note) (Esc (Text))

  # An edit that is not saved yet must show in the preview too.
  SafeKeys 0x11, 0x23            # Ctrl+End
  SafeKeys 0x0D; SafeType "追記"
  $edited = $note + "`n追記"
  (Find 'PreviewModeButton').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
  $p = WaitMode $true
  Check "preview button shows the rendered note" ($p -and $p.preview) ($p | ConvertTo-Json -Compress)
  Check "the preview button shows as current" ((Find 'PreviewModeButton').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -eq 'On')
  if ($p.preview) {
    $v = $p.preview
    Check "table rendered with header and cells" ($v.tables -eq 1 -and $v.th -eq 3 -and $v.td -eq 3 -and $v.cellStrong -eq 1 -and $v.cellLinks -eq 1) ($v | ConvertTo-Json -Compress)
    Check "nested lists and checkboxes (display only)" ($v.nested -ge 2 -and $v.tasks -eq 2 -and $v.checked -eq 1 -and $v.enabledBoxes -eq 0)
    Check "headings, emphasis, quote, inline code" ($v.h[0] -eq 1 -and $v.strong -ge 2 -and $v.em -eq 1 -and $v.del -eq 1 -and $v.quote -eq 1 -and $v.inlineCode -eq 1)
    Check "raw HTML stays text: no script, no image, no handlers" ($v.scripts -eq 0 -and $v.images -eq 0 -and $v.handlers -eq 0 -and $v.raw -ge 1)
    Check "preview has keyboard focus" ($v.focused -eq $true)
  }
  Check "editor text unchanged by the preview" ((Text) -eq $edited) (Esc (Text))

  # Twenty-one more switches (ending in the editor): the Markdown stays byte for byte the same.
  for ($i = 0; $i -lt 21; $i++) {
    $id = if ($i % 2 -eq 0) { 'SourceModeButton' } else { 'PreviewModeButton' }
    (Find $id).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle()
    [void](WaitMode ($i % 2 -eq 1))
  }
  $p = WaitMode $false
  Check "back in the editor after the switches" ($p -and $null -eq $p.preview)
  Check "Markdown unchanged after 22 switches" ((Text) -eq $edited) (Esc (Text))

  # Editing works at once and the undo history is still there.
  SafeType "再開"; Start-Sleep -Milliseconds 600
  Check "typing works after returning" ((Text) -eq ($edited + "再開")) (Esc (Text))
  SafeKeys 0x11, 0x5A; Start-Sleep -Milliseconds 400
  Check "Ctrl+Z still undoes" ((Text) -eq $edited) (Esc (Text))

  Start-Sleep 2
  $saved = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false)) -replace "`r", ""
  Check "file holds exactly the edited Markdown (no re-serialization)" ($saved -eq $edited) (Esc $saved)
  CloseWindow
  Check "app exits" (WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
