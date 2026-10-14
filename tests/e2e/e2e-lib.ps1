# Shared helpers for Floating Memo end-to-end tests (Windows PowerShell 5.1).
#
# Safety rules these helpers enforce:
# - Keys are sent only after checking that the foreground window AND the focused element
#   belong to the test's own app process (SafeType / SafeKeys); otherwise the test aborts.
# - Only processes this script started ($script:launched) are ever stopped.
# - The app is started with --notes-dir / --settings-file in a scratch folder, so the user's
#   real draft.md and settings are never touched. Audio comes from --fake-audio WAV files,
#   never from a real microphone or loopback device.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class K {
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }
  [StructLayout(LayoutKind.Explicit, Size=40)] struct INPUT { [FieldOffset(0)] public uint type; [FieldOffset(8)] public KEYBDINPUT ki; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] i, int size);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  static void Send(ushort vk, ushort scan, uint flags) {
    var a = new INPUT[1]; a[0].type = 1; a[0].ki.wVk = vk; a[0].ki.wScan = scan; a[0].ki.dwFlags = flags;
    SendInput(1, a, Marshal.SizeOf(typeof(INPUT)));
  }
  public static void Combo(params ushort[] vks) {
    foreach (var v in vks) Send(v, 0, 0);
    for (int i = vks.Length - 1; i >= 0; i--) Send(vks[i], 0, 2);
  }
  // Re-checks the foreground window before EVERY key, so if focus moves to another app
  // (e.g. the user clicks elsewhere) typing stops instead of going into that app.
  public static bool TypeInto(IntPtr hwnd, string s) {
    foreach (char c in s) {
      if (GetForegroundWindow() != hwnd) return false;
      Send(0, c, 4); Send(0, c, 4 | 2); System.Threading.Thread.Sleep(5);
    }
    return true;
  }
  public static bool ComboInto(IntPtr hwnd, params ushort[] vks) {
    if (GetForegroundWindow() != hwnd) return false;
    Combo(vks);
    return true;
  }
}
"@
$script:results = @()
$script:launched = @()   # every PID this script started (dotnet + app); only these are ever stopped
function Check($name, $ok, $detail = "") { $script:results += [pscustomobject]@{ Test = $name; Result = $(if ($ok) { "PASS" } else { "FAIL" }); Detail = $detail } }
function Esc($s) { if ($null -eq $s) { return '<null>' }; ($s -replace "`r", "\r" -replace "`n", "\n") }
$file = Join-Path $NotesDir 'draft.md'
function FileText { if (Test-Path $file) { [IO.File]::ReadAllText($file, [Text.UTF8Encoding]::new($false)) } else { $null } }

function Launch([switch]$Build, [string[]]$Extra = @(), [switch]$NoDraft) {
  $log = Join-Path $env:TEMP ("fm-run-" + [guid]::NewGuid().ToString('N') + ".log")
  $a = @('run', '--project', $Project, '-p:Platform=x64')
  if (-not $Build) { $a += '--no-build' }
  # A suite's first launch (-Build) starts from default settings so suites don't affect each other.
  else { Remove-Item (Join-Path (Split-Path $NotesDir) 'e2e-settings.json') -ErrorAction SilentlyContinue }
  # Like an upgrade from the single-memo version: an (empty) draft.md opens as the first tab.
  if (-not $NoDraft -and -not (Test-Path (Join-Path $NotesDir 'draft.md'))) { New-Item -ItemType Directory -Force $NotesDir | Out-Null; [IO.File]::WriteAllText((Join-Path $NotesDir 'draft.md'), '') }
  $a += @('--', '--notes-dir', $NotesDir, '--settings-file', (Join-Path (Split-Path $NotesDir) 'e2e-settings.json')) + $Extra
  if ($env:FM_EXE) {
    # Test a published build (e.g. the portable folder) directly instead of `dotnet run`.
    $appArgs = $a[([array]::IndexOf($a, '--') + 1)..($a.Length - 1)]
    $appPid = (Start-Process $env:FM_EXE -ArgumentList $appArgs -WorkingDirectory $env:SystemRoot -PassThru).Id
  }
  else {
    $dn = Start-Process dotnet -ArgumentList $a -RedirectStandardOutput $log -PassThru -WindowStyle Hidden
    $script:launched += $dn.Id
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
      Start-Sleep 1
      $m = Select-String -Path $log -Pattern 'launched \(PID: (\d+)\)' -ErrorAction SilentlyContinue
      if ($m) { break }
    }
    if (-not $m) { throw "app did not launch: $(Get-Content $log -Raw)" }
    $appPid = [int]$m.Matches[0].Groups[1].Value
  }
  $script:launched += $appPid
  do { Start-Sleep 1; $p = Get-Process -Id $appPid } while ($p.MainWindowHandle -eq 0)
  Start-Sleep 2
  $script:app = $p; $script:h = $p.MainWindowHandle
  $root = [Windows.Automation.AutomationElement]::FromHandle($script:h)
  $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, "メモ")
  $script:edit = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $cond)
  $script:root = $root
  # Wait for the Markdown editor to replace the start-up text box (test builds report it).
  for ($i = 0; $i -lt 75 -and (EditorKind) -eq 'plain'; $i++) { Start-Sleep -Milliseconds 200 }
  Start-Sleep -Milliseconds 300
}
# The Markdown editor lives inside WebView2, so test builds (TEST_HOOKS) mirror its text and
# focus into hidden UI Automation elements. Without mirrors, the plain TextBox is read directly.
function Mirror([string]$id) {
  $e = Find $id
  if ($e) { $e.Current.Name } else { $null }
}
function Value {
  $m = Mirror 'EditorMirror'
  if ($null -ne $m) { return $m }
  $script:edit.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
}
function EditorKind { Mirror 'EditorKind' }
function OurEditorFocused {
  if ([K]::GetForegroundWindow() -ne $script:h) { return $false }
  $state = Mirror 'EditorFocusState'
  if ($null -ne $state) { return $state -eq 'focused' }
  $f = [Windows.Automation.AutomationElement]::FocusedElement
  $f -ne $null -and $f.Current.Name -eq "メモ" -and $f.Current.ProcessId -eq $script:app.Id
}
# Refuses to send keys unless the test app's editor has focus.
# Waits for the test editor to own focus; if another window holds the foreground,
# use the app's own global hotkey (only our app receives it) to bring it forward.
function EnsureFocus {
  for ($i = 0; $i -lt 20; $i++) { if (OurEditorFocused) { return }; Start-Sleep -Milliseconds 250 }
  [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1
  if (-not (OurEditorFocused)) { [K]::Combo(0x11, 0x10, 0x4D); Start-Sleep 1 }
}
function SafeType([string]$text) { EnsureFocus; if (-not (OurEditorFocused)) { throw "ABORT: test editor not focused; refusing to type" }; if (-not [K]::TypeInto($script:h, $text)) { throw "ABORT: focus left the test window while typing" } }
function SafeKeys([uint16[]]$vks) { EnsureFocus; if (-not (OurEditorFocused)) { throw "ABORT: test editor not focused; refusing to send keys" }; if (-not [K]::ComboInto($script:h, $vks)) { throw "ABORT: focus left the test window" } }
function CloseWindow { [void][K]::PostMessage($script:h, 0x0010, [IntPtr]::Zero, [IntPtr]::Zero) }  # WM_CLOSE = X button
function WaitExit($sec = 10) { try { $script:app.WaitForExit($sec * 1000) } catch { $true } }
function ErrorBarOpen {
  $bar = Find "SaveErrorBar"

  $bar -ne $null -and -not $bar.Current.IsOffscreen -and $bar.Current.BoundingRectangle.Height -gt 0
}


# UI Automation searches stop at the WebView2 element (its content belongs to the browser
# process), so elements after it would never be found. Walk the tree ourselves and skip it.
function AllElements {
  # Elements can disappear mid-walk (tabs are re-rendered): retry the whole walk a few times.
  for ($attempt = 0; $attempt -lt 3; $attempt++) {
    try { return (AllElementsOnce) } catch [System.Windows.Automation.ElementNotAvailableException] { Start-Sleep -Milliseconds 150 }
  }
  AllElementsOnce
}
function AllElementsOnce {
  $walker = [Windows.Automation.TreeWalker]::ControlViewWalker
  $result = New-Object System.Collections.Generic.List[object]
  $stack = New-Object System.Collections.Generic.Stack[object]
  $child = $walker.GetFirstChild($script:root)
  $siblings = @()
  while ($child) { $siblings += $child; $child = $walker.GetNextSibling($child) }
  for ($i = $siblings.Count - 1; $i -ge 0; $i--) { $stack.Push($siblings[$i]) }
  while ($stack.Count) {
    $e = $stack.Pop()
    $result.Add($e)
    if ($e.Current.AutomationId -eq 'EditorWebView') { continue }
    $kids = @(); $c = $walker.GetFirstChild($e)
    while ($c) { $kids += $c; $c = $walker.GetNextSibling($c) }
    for ($i = $kids.Count - 1; $i -ge 0; $i--) { $stack.Push($kids[$i]) }
  }
  $result
}
function Find([string]$automationIdOrName, [switch]$ByName) {
  foreach ($e in AllElements) {
    $v = if ($ByName) { $e.Current.Name } else { $e.Current.AutomationId }
    if ($v -eq $automationIdOrName) { return $e }
  }
  $null
}
function Invoke-Button([string]$name) {
  $b = Find $name -ByName
  if (-not $b) { throw "button not found: $name (status: $((Find 'RecordingStatusText').Current.Name))" }
  $b.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}
function Toggle-State([string]$name) { (Find $name -ByName).GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern).Current.ToggleState }
function Set-Toggle([string]$name, [string]$state) {
  $e = Find $name -ByName; $p = $e.GetCurrentPattern([Windows.Automation.TogglePattern]::Pattern)
  for ($i = 0; $i -lt 3 -and $p.Current.ToggleState.ToString() -ne $state; $i++) { $p.Toggle() }
}
function IsShown($id) { $e = Find $id; $e -ne $null -and -not $e.Current.IsOffscreen -and $e.Current.BoundingRectangle.Height -gt 0 }
function InfoBarText($id) {
  $e = Find $id; if (-not $e) { return "" }
  ($e.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { $_.Current.Name } | Where-Object { $_ }) -join " | "
}
function WindowTitle { $script:root.Current.Name }
Add-Type -Namespace W32 -Name Win -MemberDefinition '[DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);'
function WavInfo($path) {
  $b = [IO.File]::ReadAllBytes($path)
  [pscustomobject]@{ Channels = [BitConverter]::ToInt16($b, 22); Rate = [BitConverter]::ToInt32($b, 24); Bits = [BitConverter]::ToInt16($b, 34); Bytes = $b.Length }
}
function FindDialog { AllElements | Where-Object { $_.Current.ClassName -eq "#32770" } | Select-Object -First 1 }
function ClickDialogButton($dlg, [string]$id) {
  $b = $dlg.FindFirst([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.AndCondition(
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ClassNameProperty, "Button")),
    (New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $id)))))
  [void][K]::PostMessage([IntPtr]$b.Current.NativeWindowHandle, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero)
}