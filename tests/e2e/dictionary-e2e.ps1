param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
# Term dictionary and the correction step, with the test stand-in model (--fake-llm). The
# fake model proposes dictionary fixes and, deliberately, a person-name change that must NOT
# be applied. Made-up words only.

function TabElements { @(AllElements | Where-Object { $_.Current.AutomationId -eq 'NoteTab' }) }
function ActiveTab { (TabElements | Where-Object { $_.Current.ItemStatus -eq '選択中' } | Select-Object -First 1).Current.Name }
function MinutesFile { Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '*AI議事録*.md' -ErrorAction SilentlyContinue | Select-Object -First 1 }
function SettingsWindow {
  $desk = [Windows.Automation.AutomationElement]::RootElement
  $cond = New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $script:app.Id)),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'Floating Memo の設定')))
  for ($i = 0; $i -lt 30; $i++) { $w = $desk.FindFirst([Windows.Automation.TreeScope]::Children, $cond); if ($w) { return $w }; Start-Sleep -Milliseconds 200 }
  $null
}
function In($win, [string]$id) {
  $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)
  for ($i = 0; $i -lt 15; $i++) { $e = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond); if ($e) { return $e }; Start-Sleep -Milliseconds 200 }
  throw "settings element not found: $id"
}
function SetText($win, [string]$id, [string]$text) { (In $win $id).GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).SetValue($text) }
function Press($win, [string]$id) { (In $win $id).GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 400 }
function MinutesMenu([string]$id) {
  $m = $null
  for ($try = 0; $try -lt 3 -and -not $m; $try++) {
    (Find 'MinutesButton').GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    for ($i = 0; $i -lt 20; $i++) { Start-Sleep -Milliseconds 150; $m = Find $id; if ($m) { break } }
  }
  if (-not $m) { throw "menu item not found: $id" }
  $m.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep -Milliseconds 500
}

try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }
  Launch -Build -Extra @('--fake-llm') -NoDraft
  $dictFile = Join-Path $NotesDir '.floatingmemo\dictionary.json'
  Check "no dictionary until the user adds one (nothing bundled or forced)" (-not (Test-Path $dictFile))

  $source = ActiveTab
  SafeType "加工ホールの説明会は三月五日です。担当は田中さんです。"; Start-Sleep 1.5
  $sourceFile = Join-Path $NotesDir "notes\$source.md"
  $before = [IO.File]::ReadAllBytes($sourceFile)

  # Settings -> dictionary -> add a term with a known misrecognition.
  MinutesMenu 'MinutesSettings'
  $win = SettingsWindow
  Check "settings window opens from the minutes menu" ($win -ne $null)
  (In $win 'SettingsDictionary').GetCurrentPattern([Windows.Automation.SelectionItemPattern]::Pattern).Select(); Start-Sleep 0.5
  Press $win 'NewEntry'
  SetText $win 'TermBox' '架空ホール'
  SetText $win 'ReadingBox' 'かくうほーる'
  SetText $win 'CategoryBox' '施設名'
  SetText $win 'MisBox' '加工ホール'
  Press $win 'SaveEntry'
  $json = if (Test-Path $dictFile) { [IO.File]::ReadAllText($dictFile, [Text.Encoding]::UTF8) } else { '' }
  Check "term saved to the local dictionary file" ($json.Contains('架空ホール') -and $json.Contains('加工ホール')) $dictFile
  $list = In $win 'EntryList'
  Check "entry listed" (($list.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) | Where-Object { $_.Current.Name -eq '架空ホール' }) -ne $null)
  SetText $win 'DictionarySearch' 'かくう'; Start-Sleep 0.5
  Check "search by reading finds it" (@($list.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition)).Count -ge 1)
  $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1

  # Minutes: correction step first, then minutes.
  for ($f = 0; $f -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $f++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
  MinutesMenu 'MinutesCreate'
  # The note has typed text only (no transcript block): confirm "メモだけで作成".
  for ($i = 0; $i -lt 20 -and -not ($anyway = Find 'MinutesWithoutTranscript'); $i++) { Start-Sleep -Milliseconds 150 }
  if ($anyway) { $anyway.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  for ($i = 0; $i -lt 80 -and -not ((InfoBarText 'AudioInfoBar') -match '下書きを作成しました'); $i++) { Start-Sleep -Milliseconds 250 }
  $info = InfoBarText 'AudioInfoBar'
  Check "minutes created" ($info -match '下書きを作成しました') $info
  $text = [IO.File]::ReadAllText((MinutesFile).FullName, [Text.Encoding]::UTF8)
  Check "dictionary-backed fix applied" ($text.Contains('「加工ホール」→「架空ホール」：修正済み'))
  Check "name change NOT applied, kept as 要確認" ($text.Contains('「田中」→「架空次郎」：要確認（原文のまま）') -and $text.Contains('田中〔要確認: 架空次郎？〕'))
  Check "corrected transcript included" ($text.Contains('## 校正済み文字起こし') -and $text.Contains('架空ホールの説明会は三月五日です'))
  Check "transcript note unchanged" ([Convert]::ToBase64String([IO.File]::ReadAllBytes($sourceFile)) -eq [Convert]::ToBase64String($before))
  Check "snapshot kept for learning" (Test-Path (Join-Path $NotesDir '.floatingmemo\corrections'))

  # Correction switched off -> minutes straight from the transcript.
  MinutesMenu 'MinutesSettings'
  $win = SettingsWindow
  (In $win 'CorrectToggle').GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Toggle(); Start-Sleep 0.5
  $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
  for ($f = 0; $f -lt 3 -and [K]::GetForegroundWindow() -ne $script:h; $f++) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
  MinutesMenu 'MinutesToggle'; Start-Sleep 0.5   # back to the transcript
  MinutesMenu 'MinutesCreate'
  for ($i = 0; $i -lt 20 -and -not ($anyway = Find 'MinutesWithoutTranscript'); $i++) { Start-Sleep -Milliseconds 150 }
  if ($anyway) { $anyway.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke() }
  for ($i = 0; $i -lt 80 -and -not ((InfoBarText 'AudioInfoBar') -match '下書きを作成しました'); $i++) { Start-Sleep -Milliseconds 250 }
  $second = Get-ChildItem (Join-Path $NotesDir 'notes') -Filter '*AI議事録*.md' | Sort-Object LastWriteTime | Select-Object -Last 1
  $text2 = [IO.File]::ReadAllText($second.FullName, [Text.Encoding]::UTF8)
  Check "correction off -> no correction section" (-not $text2.Contains('## 校正の記録')) $second.Name
  CloseWindow; [void](WaitExit 20)

  # --- Import a lexicon file (same format as the user's initial lexicon, made-up entries) ---------
  # The system file dialog cannot be automated reliably, so the test build's
  # "--import-dictionary" runs the import button's code with the file.
  $lexicon = Join-Path $PSScriptRoot 'fixtures\sample-lexicon.json'
  Launch -Extra @('--fake-llm', '--import-dictionary', $lexicon)
  MinutesMenu 'MinutesSettings'
  $win = SettingsWindow
  Start-Sleep 2
  $texts = ($win.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name }) -join ' / '
  Check "import reports added entries and categories" ($texts -match 'インポートしました' -and $texts -match '追加 4 件' -and $texts -match '学校名（東京都）') $texts
  $ok = $win.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, 'OK')))
  if ($ok) { $ok.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); Start-Sleep 1 }
  $json = [IO.File]::ReadAllText($dictFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
  $terms = @($json.Entries | ForEach-Object { $_.Term })
  Check "rules, scope and descriptions are not imported as terms" (-not ($terms -contains '東京都') -and -not ($terms | Where-Object { $_ -match '禁止' }))
  Check "lexicon entries merged into the existing dictionary" ($terms -contains '架空学園中学校' -and $terms -contains '架空ホール' -and $terms.Count -eq 5) ($terms -join ',')
  Check "recognition candidates imported as misrecognitions" ((@($json.Entries | Where-Object { $_.Term -eq '稟議書' })[0].Misrecognitions) -contains '林業書')
  Check "region becomes its own category" ((@($json.Entries | Where-Object { $_.Term -eq '未来ヶ丘中学校' })[0].Category) -eq '学校名（神奈川県）')
  Check "nothing marked as a Whisper hint by import" (@($json.Entries | Where-Object { $_.WhisperHint }).Count -eq 0)
  $category = In $win 'Category:学校名（東京都）'
  Check "category switch shown" ($category -ne $null)
  $win.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close(); Start-Sleep 1
  CloseWindow; [void](WaitExit 20)
}
catch { "ERROR: $_ (line $($_.InvocationInfo.ScriptLineNumber))" }
finally {
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
