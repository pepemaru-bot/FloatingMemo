# 音声認識モデルの比較（架空の会議音声）

音声認識（ASR）を替えたときに、文字起こしと**最終的な議事録**がどう変わるかを、同じ音声・同じ議事録処理で比べるための評価一式です。
音声・台本・人名・学校名はすべて架空です（実在の録音・会議・個人情報は使いません）。

## データ

| 台本 | 区分 | 内容 |
|---|---|---|
| `s01-single.json` | 動作確認用（dev） | 単独話者・明瞭（約38秒） |
| `h01-shinro.json` | ホールドアウト | 4人の進路会議（約3分）。訂正、決定の撤回、短い応答、小声、早口、重なり、物音 |
| `h02-unei.json` | ホールドアウト | 4人の教室運営会議（約6.5分）。話題の頻繁な切り替え、同じ対象の再登場、雑談、電話の音 |
| （既存）89秒の音声 | 動作確認用（dev） | 前回の Whisper 設定比較の音声（`tests/asr/make-asr-audio.ps1`） |

ホールドアウトの正解は、議事録処理やプロンプトの調整には使いません（評価のみ）。

`{表記|よみ}` は音声合成の読みの指定で、正解の文字起こしには表記が入ります。

## 手順

```bash
# 1. 音声合成（Windows の OneCore 日本語音声: Ichiro / Sayaka / Ayumi / Haruka）
powershell -File tests/asr/eval/synth-utterances.ps1 -Scenario tests/asr/eval/h01-shinro.json -OutDir <out>/h01-shinro/utt
# 2. 会議音声と正解（clean.wav / noisy.wav / truth.json / truth.txt）
python assemble.py h01-shinro.json <out>/h01-shinro
# 3. 現行 Whisper（アプリと同じパイプライン、ヒントあり・なし）
FLOATINGMEMO_ASR_EVAL=1 FLOATINGMEMO_ASR_EVAL_DIR=<out> dotnet test --filter AsrEvalExport
# 4. Nemotron 3.5 Transcribe（NeMo-Speech.cpp、ローカル GGUF、CPU）
python run_nemotron.py <nemo-speech.exe> <decoder.gguf> <out> plain|context|diarize clean noisy
# 4b. Haas-ja-v1.0（speech.cpp、ローカル Q8_0 GGUF、Vulkan）。設定: vad10 / vad28 / vad10-kw / vad10-kwterm / whole
python run_haas.py <speech.exe> <haas.gguf> <silero-vad.gguf> <out> tests/asr/eval vad10-kw
# 4c. 長い音声（会議をつないで繰り返す。文字起こしの指標のみ）
python make_long.py <out> <out>/l01-long 2 h01-shinro h02-unei d89-legacy s01-single
# 5. 文字起こしの採点（<out>/asr-scores.md）
python score_asr.py <out>        # python score_asr.py --selftest で正規化の自己テスト
# 6. 議事録の採点（各ASRの文字起こし → 同じ Qwen3-4B ハーネス、<out>/minutes/minutes-scores.md）
FLOATINGMEMO_LLM=1 FLOATINGMEMO_ASR_EVAL_DIR=<out> FLOATINGMEMO_BENCH_SEEDS=42,7,123 dotnet test --filter AsrMinutesRun
```

Python は隔離した仮想環境で実行します（numpy, soundfile, scipy, fugashi, unidic-lite, rapidfuzz, psutil）。

## 結果ファイルの共通形式

どの ASR も同じ形で書き出し、議事録処理には同じ方法（アプリの `JoinSegments`）で渡します。

```json
{ "model": "...", "setting": "...", "settings": "...", "audio": "clean|noisy",
  "seconds": 12.3, "peak_ram_mb": 400, "peak_vram_mb": 800, "speakers": false,
  "segments": [ { "start": 0.8, "end": 10.1, "speaker": null, "text": "..." } ] }
```

時刻や話者がないモデルは `null` にします（存在しない機能は指定しません）。

## 指標

- CER は「正規化前（空白のみ除去）」「正規化後（NFKC、句読点除去、漢数字→算用数字、II→2）」「フィラー除去後」の3種類です。
- WER は正規化後の文を MeCab（fugashi＋unidic-lite）で分割して数えます。
- 「欠落した発言」は、文字の半分以上が認識されなかった発言です。
- 「幻覚」は、原音にない6文字以上の挿入です。
