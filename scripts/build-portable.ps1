<#
.SYNOPSIS
    Builds the portable (unpackaged, self-contained) Floating Memo for Windows 11 x64.

.DESCRIPTION
    Output (git-ignored):
      dist\<Name>\                        the app folder (FloatingMemo.exe + runtime + Editor\ + models\ + licenses\)
      dist\<Name>.zip                     the folder zipped for copying to another PC
      dist\README_PORTABLE-<Name>.txt     how to run it
    <Name> defaults to FloatingMemo-Portable-yyyyMMdd-HHmm; existing builds are never overwritten.

    No AI model is packaged: the Whisper and Qwen models are downloaded by the user from their
    official sources and placed in models\whisper and models\qwen (see README.md). The package
    holds only build output, empty model folders with instructions, and license texts: no notes,
    recordings, settings, logs, models or credentials. The build fails if any of those appear.
#>
param(
    # A new name for every build: an existing, known-good ZIP is never overwritten.
    [string]$Name = ('FloatingMemo-Portable-' + (Get-Date -Format 'yyyyMMdd-HHmm'))
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'
$app = Join-Path $dist $Name
$zip = Join-Path $dist "$Name.zip"
$packages = Join-Path $env:USERPROFILE '.nuget\packages'

if ((Test-Path $zip) -or (Test-Path $app)) { throw "$zip or $app already exists; choose another -Name (existing builds are never overwritten)." }
New-Item -ItemType Directory -Force $dist | Out-Null

# Unpackaged + .NET self-contained + Windows App SDK self-contained (see Portable in the csproj).
# PublishProfile is cleared so local (git-ignored) .pubxml files cannot change the result.
dotnet publish (Join-Path $root 'src\FloatingMemo\FloatingMemo.csproj') -c Release -r win-x64 `
    -p:Platform=x64 -p:Portable=true -p:PublishProfile= -o $app
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# LLamaSharp / Whisper.net copy native libraries for every OS; only Windows x64 is needed here.
# runtimes\vulkan\win-x64 is the GPU (Vulkan) build of whisper.cpp: keep it.
Get-ChildItem (Join-Path $app 'runtimes') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -notin 'win-x64', 'vulkan' } | Remove-Item -Recurse -Force
Get-ChildItem (Join-Path $app 'runtimes\vulkan') -Directory -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -ne 'win-x64' } | Remove-Item -Recurse -Force

# Debug symbols embed build-machine paths (including the Windows user name); not shipped.
Get-ChildItem $app -Recurse -File -Filter *.pdb | Remove-Item -Force

# Empty model folders with instructions: the app finds models placed here (see ModelLocations.cs).
foreach ($kind in 'whisper', 'qwen') {
    New-Item -ItemType Directory -Force (Join-Path $app "models\$kind") | Out-Null
}
Copy-Item (Join-Path $PSScriptRoot 'portable\models\README_MODELS.txt') (Join-Path $app 'models\README_MODELS.txt')

$readme = Join-Path $PSScriptRoot 'portable\README_PORTABLE.txt'
Copy-Item $readme (Join-Path $app 'README_PORTABLE.txt')
Copy-Item $readme (Join-Path $dist "README_PORTABLE-$Name.txt")
Copy-Item (Join-Path $PSScriptRoot 'portable\THIRD_PARTY_NOTICES.txt') (Join-Path $app 'THIRD_PARTY_NOTICES.txt')

# Full license texts of the redistributed Microsoft components, taken from the NuGet packages
# the app was built with (the versions must match the csproj / package graph).
$licenses = Join-Path $app 'licenses'
New-Item -ItemType Directory -Force $licenses | Out-Null
$licenseFiles = [ordered]@{
    'WindowsAppSDK-license.txt'         = 'microsoft.windowsappsdk\2.5.1\license.txt'
    'WindowsAppSDK-NOTICE.txt'          = 'microsoft.windowsappsdk\2.5.1\NOTICE.txt'
    'WindowsML-license.txt'             = 'microsoft.windows.ai.machinelearning\2.1.74\license.txt'
    'WindowsML-ThirdPartyNotices.txt'   = 'microsoft.windows.ai.machinelearning\2.1.74\ThirdPartyNotices.txt'
    'WebView2-LICENSE.txt'              = 'microsoft.web.webview2\1.0.3719.77\LICENSE.txt'
    'WebView2-NOTICE.txt'               = 'microsoft.web.webview2\1.0.3719.77\NOTICE.txt'
    'CommunityToolkit-License.txt'      = 'communitytoolkit.highperformance\8.4.2\License.md'
    'CommunityToolkit-ThirdPartyNotices.txt' = 'communitytoolkit.highperformance\8.4.2\ThirdPartyNotices.txt'
}
foreach ($entry in $licenseFiles.GetEnumerator()) {
    $source = Join-Path $packages $entry.Value
    if (-not (Test-Path $source)) { throw "License file not found: $source (package version changed?)" }
    Copy-Item $source (Join-Path $licenses $entry.Key)
}

# Guards: nothing personal and no AI model may end up in the package.
$personal = Get-ChildItem $app -Recurse -File -Include '*.md', '*.wav', '*.log', 'settings.json', 'session.json', 'notes.json', 'dictionary.json', '*.pfx', '*.tmp'
if (Test-Path (Join-Path $app 'FloatingMemo.exe.WebView2')) { throw 'WebView2 user data found in the package' }
if ($personal) { throw "Unexpected personal/data files in package: $($personal.FullName -join ', ')" }
$models = Get-ChildItem $app -Recurse -File -Include '*.gguf', '*.ggml', 'ggml-*.bin', '*.onnx', '*.safetensors'
$models += Get-ChildItem (Join-Path $app 'models') -Recurse -File | Where-Object { $_.Name -ne 'README_MODELS.txt' }
if ($models) { throw "AI model files must not be packaged: $($models.FullName -join ', ')" }
$large = Get-ChildItem $app -Recurse -File | Where-Object { $_.Length -gt 100MB }
if ($large) { throw "Unexpectedly large files (a model?): $($large.FullName -join ', ')" }
$machine = @($env:USERNAME, $env:COMPUTERNAME) | Where-Object { $_ -and $_.Length -ge 3 }
foreach ($file in Get-ChildItem $app -Recurse -File) {
    $bytes = [IO.File]::ReadAllBytes($file.FullName)
    $text = [Text.Encoding]::ASCII.GetString($bytes) + [Text.Encoding]::Unicode.GetString($bytes)
    foreach ($word in $machine) {
        if ($text.IndexOf("\$word\", [StringComparison]::OrdinalIgnoreCase) -ge 0) { throw "Build-machine name '$word' found in $($file.FullName)" }
    }
}

# The folder itself is the top entry of the ZIP. Entries are written one by one with "/" as the
# separator: ZipFile.CreateFromDirectory on Windows PowerShell 5.1 (.NET Framework) writes "\",
# which other unzip tools treat as part of the file name. Empty folders (models\whisper,
# models\qwen) get their own "name/" entries.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$appFull = (Resolve-Path $app).Path.TrimEnd('\')
$stream = [IO.File]::Open($zip, [IO.FileMode]::CreateNew)
$archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in Get-ChildItem $appFull -Recurse -Force) {
        $relative = $Name + '/' + $item.FullName.Substring($appFull.Length + 1).Replace('\', '/')
        if ($item.PSIsContainer) {
            if (-not (Get-ChildItem $item.FullName -Force)) { [void]$archive.CreateEntry($relative + '/') }
            continue
        }
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $item.FullName, $relative, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}
"OK: $zip ({0:N1} MB)" -f ((Get-Item $zip).Length / 1MB)
