<#
.SYNOPSIS
    Checks Floating Memo package ZIPs against manifest.json (size and SHA-256) before use.
    Run it in the folder that holds manifest.json and the ZIPs. Reads only; changes nothing.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Verify-FloatingMemoPackage.ps1
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$manifest = Get-Content (Join-Path $here 'manifest.json') -Raw -Encoding UTF8 | ConvertFrom-Json
"Floating Memo $($manifest.version)  (作成 $($manifest.created)、commit $($manifest.commit))"
$ok = $true
foreach ($p in $manifest.packages) {
    $file = Join-Path $here $p.file
    if (-not (Test-Path $file)) { "  見つかりません: $($p.file)（$($p.kind)。モデルは前回のものを使う場合は不要）"; continue }
    $size = (Get-Item $file).Length
    $hash = (Get-FileHash $file -Algorithm SHA256).Hash.ToLower()
    if ($size -eq $p.bytes -and $hash -eq $p.sha256) { "  OK: $($p.file)" }
    else { "  一致しません: $($p.file)（コピーが壊れている可能性があります。使わずに取り直してください）"; $ok = $false }
}
if (-not $ok) { exit 1 }
