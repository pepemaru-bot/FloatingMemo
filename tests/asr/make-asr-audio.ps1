<#
.SYNOPSIS
    Makes the made-up meeting audio for the speech-recognition comparison (Windows speech
    synthesis, Japanese voice): meeting.wav (16 kHz mono) and truth.txt (what is said).
    Nothing real: the script below is invented. Output folder: first argument.
#>
param([Parameter(Mandatory)] [string]$OutDir)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Speech
New-Item -ItemType Directory -Force $OutDir | Out-Null

# Short statements with pauses (speech detection), proper nouns and numbers (dictionary hint),
# then one long stretch without pauses (more than 30 s: the hint must still work at its end).
$short = @(
    'それでは定例会議を始めます。'
    'はい。'
    'まず緑川物流の件です。'
    '見積もりは二百五十万円で、納期は三月十四日です。'
    'いいえ、その日は難しいです。'
    '会場は架空ホールに決まりました。'
    '稟議書は青葉商事の分も含めて来週までに回します。'
)
$long = @(
    '続いて、今月の作業の進め方について、少し長くなりますが順番に説明します。'
    '先週までに集めた意見を整理したところ、全体の流れについては大きな反対はありませんでした。'
    '一方で、細かい手順については部署ごとにやり方が違うため、共通の手順書を作る必要があります。'
    '手順書の案は来週の打ち合わせまでに用意し、その場で意見をもらう予定です。'
    'また、作業の担当は今の体制のまま進め、必要があれば途中で見直します。'
    '最後に、白石製作所の担当者は中央工務店と打ち合わせを行い、費用は八十万円、期限は十一月二十八日です。'
)

$synth = New-Object System.Speech.Synthesis.SpeechSynthesizer
$voice = $synth.GetInstalledVoices() | Where-Object { $_.VoiceInfo.Culture.Name -eq 'ja-JP' } | Select-Object -First 1
if (-not $voice) { throw 'No Japanese voice installed.' }
$synth.SelectVoice($voice.VoiceInfo.Name)
$format = New-Object System.Speech.AudioFormat.SpeechAudioFormatInfo(16000, [System.Speech.AudioFormat.AudioBitsPerSample]::Sixteen, [System.Speech.AudioFormat.AudioChannel]::Mono)
$wav = Join-Path $OutDir 'meeting.wav'
$synth.SetOutputToWaveFile($wav, $format)
$prompt = New-Object System.Speech.Synthesis.PromptBuilder
$prompt.AppendBreak([TimeSpan]::FromSeconds(1))
foreach ($line in $short) { $prompt.AppendText($line); $prompt.AppendBreak([TimeSpan]::FromSeconds(2)) }
$prompt.AppendBreak([TimeSpan]::FromSeconds(3))
$prompt.AppendText(($long -join ''))
$prompt.AppendBreak([TimeSpan]::FromSeconds(1))
$synth.Speak($prompt)
$synth.SetOutputToNull(); $synth.Dispose()
[IO.File]::WriteAllText((Join-Path $OutDir 'truth.txt'), (($short + $long) -join "`n"), [Text.UTF8Encoding]::new($false))
"OK: $wav"
