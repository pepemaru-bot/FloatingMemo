<#
.SYNOPSIS
    Speaks every utterance of a made-up meeting script (scenario JSON) with the Windows OneCore
    Japanese voices into separate WAV files (OutDir\utt-NNN.wav). assemble.py then builds the
    meeting audio (gaps, overlaps, volume, noise) and the reference transcript from them.
    {表記|よみ} in a text gives the reading (SSML sub), so the voice says the word as intended.
#>
param([Parameter(Mandatory)] [string]$Scenario, [Parameter(Mandatory)] [string]$OutDir)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $OutDir | Out-Null

Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Media.SpeechSynthesis.SpeechSynthesizer, Windows.Media.SpeechSynthesis, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.DataReader, Windows.Storage.Streams, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
function Await($op, [Type]$type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(); $t.Result }

$s = Get-Content $Scenario -Raw -Encoding UTF8 | ConvertFrom-Json
$synth = New-Object Windows.Media.SpeechSynthesis.SpeechSynthesizer
$voices = @{}
foreach ($v in [Windows.Media.SpeechSynthesis.SpeechSynthesizer]::AllVoices) { $voices[$v.DisplayName] = $v }

$i = 0
foreach ($u in $s.utterances) {
    $i++
    $voiceName = $s.speakers.($u.spk).voice
    $synth.Voice = $voices[$voiceName]
    $rate = if ($u.rate) { [double]$u.rate } else { 1.0 }
    $body = [Security.SecurityElement]::Escape($u.text)
    $body = [regex]::Replace($body, '\{([^|{}]+)\|([^{}]+)\}', '<sub alias="$2">$1</sub>')
    $ssml = "<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='ja-JP'><prosody rate='$rate'>$body</prosody></speak>"
    $stream = Await ($synth.SynthesizeSsmlToStreamAsync($ssml)) ([Windows.Media.SpeechSynthesis.SpeechSynthesisStream])
    $reader = New-Object Windows.Storage.Streams.DataReader($stream.GetInputStreamAt(0))
    $size = [uint32]$stream.Size
    $null = Await ($reader.LoadAsync($size)) ([uint32])
    $bytes = New-Object byte[] $size
    $reader.ReadBytes($bytes)
    [IO.File]::WriteAllBytes((Join-Path $OutDir ('utt-{0:D3}.wav' -f $i)), $bytes)
    $reader.Dispose(); $stream.Dispose()
}
"OK: $i utterances -> $OutDir"
