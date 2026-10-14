param([string]$NotesDir, [string]$Project = (Join-Path $PSScriptRoot '..\..\src\FloatingMemo\FloatingMemo.csproj'))
. (Join-Path $PSScriptRoot 'e2e-lib.ps1')
try {
  if (Test-Path $NotesDir) { Remove-Item $NotesDir -Recurse -Force }   # scratch-only test folder

  # --- 1. Autosave after typing --------------------------------------------------
  Launch -Build
  Check "fresh start: empty editor" ((Value) -eq "")
  SafeType "# テスト"; SafeKeys 0x0D; SafeType "- 項目 **太字** [link](x)"
  Start-Sleep -Milliseconds 300
  Check "no save while still within debounce" ((FileText) -eq "")
  Start-Sleep -Milliseconds 1200
  $expected = "# テスト`n- 項目 **太字** [link](x)"
  Check "autosave after pause (Markdown kept, LF)" ((FileText) -eq $expected) (Esc (FileText))
  Check "folder created automatically" (Test-Path $NotesDir)
  Check "no leftover .tmp" (-not (Test-Path "$file.tmp"))

  # --- 2. Save on Esc hide (before debounce fires) -------------------------------
  SafeType " Esc前"; SafeKeys 0x1B
  Start-Sleep -Milliseconds 300
  Check "Esc hides window" (-not [K]::IsWindowVisible($script:h))
  Check "Esc saves immediately" ((FileText) -eq "$expected Esc前") (Esc (FileText))

  # --- 3. Hotkey still works -----------------------------------------------------
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1
  Check "hotkey re-shows + focuses editor" ([K]::IsWindowVisible($script:h) -and (OurEditorFocused))

  # --- 4. Save on app exit (X / WM_CLOSE) right after typing ----------------------
  SafeType " 終了前"; CloseWindow
  Check "app exits on close" (WaitExit)
  Check "exit saves pending text" ((FileText) -eq "$expected Esc前 終了前") (Esc (FileText))

  # --- 5. Restore on restart -----------------------------------------------------
  Launch
  $saved = FileText
  Check "restart restores draft" (((Value) -replace "`r", "`n") -eq $saved) (Esc (Value))
  SafeType "!"; Start-Sleep -Milliseconds 1500
  Check "caret at end after restore" ((FileText) -eq "$saved!") (Esc (FileText))

  # --- 6. Empty string -----------------------------------------------------------
  SafeKeys 0x11, 0x41; SafeKeys 0x2E   # Ctrl+A, Delete
  Start-Sleep -Milliseconds 1500
  Check "clearing saves empty file" ((Test-Path $file) -and (Get-Item $file).Length -eq 0)
  CloseWindow; [void](WaitExit)

  # --- 7. Save failure (read-only file) ------------------------------------------
  [IO.File]::WriteAllText($file, "locked", [Text.UTF8Encoding]::new($false))
  Set-ItemProperty $file -Name IsReadOnly -Value $true
  Launch
  SafeKeys 0x11, 0x23   # Ctrl+End: the restored caret is where the user left it, not necessarily the end
  SafeType " abc"; Start-Sleep -Milliseconds 1500
  Check "failed save shows error bar" (ErrorBarOpen)
  Check "failed save keeps text in editor" ((Value) -eq "locked abc") (Esc (Value))
  Check "failed save leaves file intact" ((FileText) -eq "locked")
  Check "failed save leaves no .tmp" (-not (Test-Path "$file.tmp"))

  # Exit while saving fails -> confirmation dialog, choose No -> app stays
  CloseWindow; Start-Sleep 2
  $dlg = FindDialog
  Check "exit with failing save asks for confirmation" ($dlg -ne $null)
  if ($dlg) {
    $no = $dlg.FindFirst([Windows.Automation.TreeScope]::Descendants,
      (New-Object Windows.Automation.AndCondition(
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, "Button")),
        (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, "7")))))  # IDNO
    # Win32 dialog buttons expose no Invoke pattern here; BM_CLICK (0x00F5) presses it.
    [void][K]::PostMessage([IntPtr]$no.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero); Start-Sleep 1
  }
  Check "choosing No keeps app and text" ((-not $script:app.HasExited) -and (Value) -eq "locked abc")

  # Recover: make writable, edit again -> saved, error bar closes
  Set-ItemProperty $file -Name IsReadOnly -Value $false
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1
  if (-not (OurEditorFocused)) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
  SafeType "d"; Start-Sleep -Milliseconds 1500
  Check "save recovers after error" ((FileText) -eq "locked abcd") (Esc (FileText))
  Check "error bar closes after recovery" (-not (ErrorBarOpen))
  CloseWindow
  Check "normal exit after recovery" (WaitExit)
}
catch { "ERROR: $_" }
finally {
  if (Test-Path $file) { Set-ItemProperty $file -Name IsReadOnly -Value $false -ErrorAction SilentlyContinue }
  foreach ($id in $script:launched) { Stop-Process -Id $id -ErrorAction SilentlyContinue }
  $script:results | Format-Table -AutoSize -Wrap
}
