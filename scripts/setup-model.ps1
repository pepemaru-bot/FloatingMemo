<#
.SYNOPSIS
    Downloads a Whisper GGML model for Floating Memo's offline transcription.

.DESCRIPTION
    Optional helper for developers; the same can be done by hand (README: AIモデルの手動セットアップ).
    The app itself never downloads anything; it reads the model from disk.

    Source : https://huggingface.co/ggerganov/whisper.cpp (official whisper.cpp model repository)
    License: MIT (OpenAI Whisper weights, converted to GGML by the whisper.cpp project)

    The file is downloaded to "<Destination>\ggml-<Model>.bin.partial", its SHA-1 is checked
    against the value published in whisper.cpp's model list, and only then renamed.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File scripts\setup-model.ps1
    powershell -ExecutionPolicy Bypass -File scripts\setup-model.ps1 -Model small
#>
param(
    [ValidateSet('base', 'small')]
    [string]$Model = 'base',
    # Not %LOCALAPPDATA%: the packaged app cannot see files placed there by other processes (MSIX AppData virtualization).
    [string]$Destination = (Join-Path $env:USERPROFILE 'FloatingMemo\models\whisper')
)

$ErrorActionPreference = 'Stop'

# SHA-1 values from whisper.cpp's models/README.md (multilingual models).
$knownSha1 = @{
    'base'  = '465707469ff3a37a2b9b8d8f89f2f99de7299dac'
    'small' = '55356645c2b361a969dfd0ef2c5a50d530afd8d5'
}

$fileName = "ggml-$Model.bin"
$target = Join-Path $Destination $fileName
$partial = "$target.partial"

if (Test-Path $target) {
    Write-Host "Already present: $target"
    exit 0
}

$ProgressPreference = 'SilentlyContinue'   # much faster Invoke-WebRequest on Windows PowerShell

# Pin the download to the repository's current commit so the exact version is recorded.
$revision = (Invoke-RestMethod 'https://huggingface.co/api/models/ggerganov/whisper.cpp' -UseBasicParsing).sha
$url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/$revision/$fileName"

New-Item -ItemType Directory -Force $Destination | Out-Null
Write-Host "Downloading $url"
Write-Host "        to $target"
Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing

$sha1 = (Get-FileHash $partial -Algorithm SHA1).Hash.ToLowerInvariant()
$sha256 = (Get-FileHash $partial -Algorithm SHA256).Hash.ToLowerInvariant()
if ($sha1 -ne $knownSha1[$Model]) {
    Remove-Item $partial -Force
    throw "SHA-1 mismatch for $fileName (got $sha1, expected $($knownSha1[$Model])). File removed."
}

Move-Item $partial $target -Force
$size = (Get-Item $target).Length

$info = [ordered]@{
    file       = $fileName
    source     = $url
    revision   = $revision
    sha1       = $sha1
    sha256     = $sha256
    bytes      = $size
    license    = 'MIT (OpenAI Whisper weights; GGML conversion by whisper.cpp)'
    downloaded = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssK')
}
$info | ConvertTo-Json | Set-Content (Join-Path $Destination "$fileName.json") -Encoding UTF8

Write-Host "OK: $target ($size bytes, sha1 $sha1, revision $revision)"
