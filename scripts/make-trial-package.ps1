<#
.SYNOPSIS
    Packs a built trial for copying to another PC: an app-only ZIP for every update, and a model
    pack that is needed only once (or when the models change). Nothing is uploaded anywhere.

.DESCRIPTION
    Input: an app folder made by build-portable.ps1 (no models inside).
    Output in dist\packages\<Version>\ (git-ignored):
      FloatingMemo-<Version>-app-x64.zip     the app (no models, no notes/settings/recordings)
      manifest.json                          version, date, commit, size and SHA-256 of each ZIP
      Verify-FloatingMemoPackage.ps1         checks the ZIPs against manifest.json on the other PC
    With -ModelsFrom <folder>: also FloatingMemo-models-<id>.zip (the GGUF/GGML files with their
    download records) and its hash in manifest.json. The model pack is only rebuilt when asked.

    On the other PC: unzip the app into a NEW folder (old versions stay as they are); put the
    models once into %USERPROFILE%\FloatingMemo\models (the app finds them there). Notes and
    settings live in %USERPROFILE%\FloatingMemo and are not touched by an update.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\make-trial-package.ps1 -AppFolder dist\FloatingMemo-Personal-v0.6.0-trial-x64 -Version v0.6.0
#>
param(
    [Parameter(Mandatory)] [string]$AppFolder,
    [Parameter(Mandatory)] [string]$Version,
    [string]$ModelsFrom = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root "dist\packages\$Version"
if (Test-Path $out) { throw "$out already exists; packages are never overwritten." }
$AppFolder = (Resolve-Path $AppFolder).Path

# Same guard as the build: no personal data, no models in the app package.
$bad = Get-ChildItem $AppFolder -Recurse -File -Include '*.wav', '*.log', 'settings.json', 'session.json', 'notes.json', 'dictionary.json', '*.pfx', '*.tmp', '*.gguf', 'ggml-*.bin', 'transcript.partial.json'
if ($bad) { throw "Not packed: personal data or models in $AppFolder ($($bad.FullName -join ', '))" }
$notes = Get-ChildItem $AppFolder -Recurse -File -Filter *.md | Where-Object { $_.FullName -notmatch '\\Editor\\visual-vendor\\[^\\]+\\license\.md$' }
if ($notes) { throw "Not packed: Markdown files (notes?) in $AppFolder ($($notes.FullName -join ', '))" }

New-Item -ItemType Directory -Force $out | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$appZip = Join-Path $out "FloatingMemo-$Version-app-x64.zip"
[IO.Compression.ZipFile]::CreateFromDirectory($AppFolder, $appZip, [IO.Compression.CompressionLevel]::Optimal, $true)

$packages = @([ordered]@{ kind = 'app'; file = (Split-Path $appZip -Leaf); bytes = (Get-Item $appZip).Length; sha256 = (Get-FileHash $appZip -Algorithm SHA256).Hash.ToLower() })

if ($ModelsFrom) {
    $models = Get-ChildItem $ModelsFrom -File | Where-Object { $_.Name -match '\.(gguf|bin)$' }
    if (-not $models) { throw "No model files in $ModelsFrom" }
    # Each model is checked against the SHA-256 recorded when it was downloaded.
    foreach ($m in $models) {
        $record = "$($m.FullName).json"
        if (-not (Test-Path $record)) { throw "No download record for $($m.Name)" }
        $expected = (Get-Content $record -Raw | ConvertFrom-Json).sha256
        if ((Get-FileHash $m.FullName -Algorithm SHA256).Hash.ToLower() -ne $expected) { throw "$($m.Name) does not match its download record" }
    }
    # A stable id from the file names and sizes: the same models give the same pack name.
    $key = [Text.Encoding]::UTF8.GetBytes((($models | Sort-Object Name | ForEach-Object { "$($_.Name):$($_.Length)" }) -join ';'))
    $id = -join ([Security.Cryptography.SHA256]::Create().ComputeHash($key)[0..3] | ForEach-Object { $_.ToString('x2') })
    $staging = Join-Path $env:TEMP "fm-models-$id"
    New-Item -ItemType Directory -Force $staging | Out-Null
    foreach ($m in $models) { Copy-Item $m.FullName $staging; Copy-Item "$($m.FullName).json" $staging }
    $modelZip = Join-Path $out "FloatingMemo-models-$id.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($staging, $modelZip, [IO.Compression.CompressionLevel]::NoCompression, $false)
    Remove-Item $staging -Recurse -Force
    $packages += [ordered]@{ kind = 'models'; file = (Split-Path $modelZip -Leaf); bytes = (Get-Item $modelZip).Length; sha256 = (Get-FileHash $modelZip -Algorithm SHA256).Hash.ToLower(); models = @($models.Name) }
}

$commit = (git -C $root rev-parse --short HEAD).Trim()
$manifest = [ordered]@{ version = $Version; created = (Get-Date).ToString('s'); commit = $commit; packages = $packages }
[IO.File]::WriteAllText((Join-Path $out 'manifest.json'), ($manifest | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
Copy-Item (Join-Path $PSScriptRoot 'portable\Verify-FloatingMemoPackage.ps1') $out
"OK: $out"
$packages | ForEach-Object { "  {0}  {1:N1} MB  {2}" -f $_.file, ($_.bytes / 1MB), $_.sha256 }
