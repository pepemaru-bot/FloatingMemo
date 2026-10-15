param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'), [string]$PageImage = '')
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# PDF / Word export with made-up content. The test build's "--export-to <folder>" replaces the
# system "Save as" dialog; everything else is the real code path (dialog, flush, export, messages).

Add-Type -AssemblyName System.IO.Compression.FileSystem
function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function ActiveTab { (TabElements | Where-Object { $_.Current.ItemStatus -eq '選択中' } | Select-Object -First 1).Current.Name }
function Keys([uint16[]]$vks) { if (-not [K]::ComboInto($script:h, $vks)) { throw "ABORT: focus left the test window" } }
function ExportVia([int]$format, [int]$scope, [string]$expect) {
  EnsureFocus; SafeKeys 0x11, 0x10, 0x45   # Ctrl+Shift+E
  $f = $null; for ($i = 0; $i -lt 30 -and -not $f; $i++) { Start-Sleep -Milliseconds 200; $f = Find 'ExportFormat' }
  if (-not $f) { throw 'export dialog not shown' }
  $radio = { param($group, $index) @($group.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::RadioButton))))[$index] }
  (& $radio $f $format).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
  (& $radio (Find 'ExportScope') $scope).GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select()
  Start-Sleep -Milliseconds 300
  (Find '保存先を選んで書き出す' -ByName).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  for ($i = 0; $i -lt 80; $i++) { $t = InfoBarText 'AudioInfoBar'; if (($t -match '書き出しました' -and $t.Contains($expect)) -or $t -match '書き出せませんでした') { return $t + ' | ' + (Find 'AudioInfoBar').Current.HelpText }; Start-Sleep -Milliseconds 250 }
  InfoBarText 'AudioInfoBar'
}
function DocxXml($path) {
  $zip = [IO.Compression.ZipFile]::OpenRead($path)
  try { $r = New-Object IO.StreamReader($zip.GetEntry('word/document.xml').Open(), [Text.Encoding]::UTF8); $r.ReadToEnd() } finally { $zip.Dispose() }
}

$out = Join-Path $env:TEMP 'FloatingMemoE2E\export-out'
try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  if (Test-Path $out) { Remove-Item $out -Recurse -Force }
  New-Item -ItemType Directory -Force $out | Out-Null
  Launch -Build -Extra @('--fake-llm', '--export-to', $out) -NoDraft
  $title = ActiveTab
  SafeType "# 書き出しテスト（架空）"; SafeKeys 0x0D
  SafeType "本文に**太字**と[リンク](https://example.com/)があります。"; SafeKeys 0x0D
  SafeType "- 項目A"; SafeKeys 0x0D; SafeType "項目B"; SafeKeys 0x0D; SafeKeys 0x0D
  SafeType "| 担当 | 期限 |"; SafeKeys 0x0D; SafeType "|---|---|"; SafeKeys 0x0D; SafeType "| 山本 | 金曜日 |"
  Start-Sleep 1.5
  $noteFile = Join-Path $NotesDir "notes\$title.md"
  $before = [IO.File]::ReadAllBytes($noteFile)

  # --- Word -----------------------------------------------------------------------------------
  $msg = ExportVia 1 0 "$title.docx"
  $docx = Join-Path $out "$title.docx"
  Check "Word export reports success" ($msg -match '書き出しました') $msg
  Check "docx written with the note's name" (Test-Path $docx)
  if (Test-Path $docx) {
    $xml = DocxXml $docx
    Check "docx keeps Japanese text" ($xml.Contains('書き出しテスト（架空）') -and $xml.Contains('山本'))
    Check "docx has heading style, bold, list numbering, table, hyperlink" ($xml -match 'w:val="Heading1"' -and $xml -match '<w:b\s*/>' -and $xml -match '<w:numPr>' -and $xml -match '<w:tbl>' -and $xml -match '<w:hyperlink')
  }

  # --- PDF --------------------------------------------------------------------------------------
  $msg = ExportVia 0 0 "$title.pdf"
  $pdf = Join-Path $out "$title.pdf"
  Check "PDF export reports success" ($msg -match '書き出しました') $msg
  if (Test-Path $pdf) {
    $bytes = [IO.File]::ReadAllBytes($pdf)
    $head = [Text.Encoding]::ASCII.GetString($bytes, 0, 5)
    $ascii = [Text.Encoding]::ASCII.GetString($bytes)
    Check "file is a PDF" ($head -eq '%PDF-') $head
    Check "PDF embeds a Japanese font (no garbled text)" ($ascii -match 'FontFile2|FontFile3' -and $ascii -match 'Yu|Gothic|Meiryo') ''
    Check "PDF is A4 (595 x 842 pt)" ($ascii -match '/MediaBox\s*\[\s*0\s+0\s+59[45](\.\d+)?\s+84[12](\.\d+)?\s*\]')
    if ($PageImage) { Copy-Item $pdf $PageImage -Force }
  }
  else { Check "PDF written" $false }
  Check "no temporary files left next to the exports" (@(Get-ChildItem $out -Force | Where-Object { $_.Name -like '*.tmp' }).Count -eq 0)

  # --- AI minutes only --------------------------------------------------------------------------
  (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1
  (Find 'MinutesCreate').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
  # The note has typed text only (no transcript block): confirm "メモだけで作成".
  for ($i = 0; $i -lt 20 -and -not ($anyway = Find 'MinutesWithoutTranscript'); $i++) { Start-Sleep -Milliseconds 150 }
  if ($anyway) { $anyway.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  for ($i = 0; $i -lt 80 -and -not ((InfoBarText 'AudioInfoBar') -match '下書きを作成しました'); $i++) { Start-Sleep -Milliseconds 250 }
  $msg = ExportVia 1 1 "$title（AI議事録）.docx"
  $minutesDocx = Join-Path $out "$title（AI議事録）.docx"
  Check "minutes-only export written" (Test-Path $minutesDocx) $msg
  if (Test-Path $minutesDocx) {
    $xml = DocxXml $minutesDocx
    Check "minutes-only leaves out the correction appendix" ($xml.Contains('議事録（AI下書き）') -and -not $xml.Contains('校正済み文字起こし'))
  }

  # --- Failure: the note stays as it was --------------------------------------------------------
  CloseWindow; [void](WaitExit 20)
  Launch -Extra @('--fake-llm', '--export-to', (Join-Path $out 'no-such-folder')) -NoDraft
  Invoke-Button $title; Start-Sleep 1
  $msg = ExportVia 1 0 'never'
  Check "failure is reported, note protected" ($msg -match '書き出せませんでした' -and $msg -match '変更されていません') $msg
  Check "note file unchanged by all exports" ([Convert]::ToBase64String([IO.File]::ReadAllBytes($noteFile)) -eq [Convert]::ToBase64String($before))
  EnsureFocus; SafeType "!"; Start-Sleep 1.2
  Check "typing still works after export" ([IO.File]::ReadAllText($noteFile, [Text.Encoding]::UTF8).EndsWith('!'))
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
