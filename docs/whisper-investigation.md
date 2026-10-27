# Whisper（文字起こし）の調査（2026-10-28）

現行の実装（LocalWhisperTranscriptionService、Whisper.net 1.9.1）を読んで確認した結果。実際の録音は使っていない。

## 現状

| 項目 | 現状 |
|---|---|
| モデル | ggml-base / ggml-small（設定で選択）。GPU（Vulkan）が使えれば GPU |
| 処理区間 | 録音を 10 分ずつ読み、その中の発話区間をまとめて Whisper に渡す。途中から再開できる（チェックポイント） |
| VAD | あり（エネルギー方式、SpeechActivity）。録音ごとの雑音の大きさに合わせて閾値を決める（雑音の床 +12 dB、下限 −50 dB）。発話の前後に 250 ms の余白、200 ms 未満の音は発話にしない、700 ms 以内の切れ目はつなぐ。ON/OFF（SkipSilence）を切り替えて比較できる構成 |
| 無音区間 | Whisper に渡さない（無音での幻聴を防ぐ）。録音の原本は変更しない |
| 繰り返し対策 | RepetitionGuard（無音上の繰り返し・物理的にありえない反復を除く） |
| 言語 | 設定どおり（日本語固定 "ja"、または自動判定） |
| デコード設定 | 温度・無音判定などは whisper.cpp の既定値のまま（明示的な変更なし） |
| 前区間の文脈 | 引き継がない（WithNoContext）。1 つの幻聴が区間ごとに続くのを防ぐため |
| 専門用語辞書 | 辞書で「Whisperヒント」にした語だけを初期プロンプト（"用語: …。"）に使う。最大 30 語・約 200 文字。ヒントの文だけが出力された区間（エコー）は捨てる |

## Whisper.net 1.9.1 で使える設定（確認済み）

WithTemperature / WithTemperatureInc、WithNoSpeechThreshold、WithEntropyThreshold、WithLogProbThreshold、
WithBeamSearchSamplingStrategy / WithGreedySamplingStrategy、WithCarryInitialPrompt（初期プロンプトを各区間に引き継ぐ）、
WithMaxLastTextTokens、WithSuppressRegex、WithNoContext、WithPrompt など。
Silero VAD（VadFactory / VadProcessor）も含まれるが、**VAD 用のモデルファイル（ggml 形式の Silero、約 1 MB）が別途必要**。

## 改善案と状況

1. **Silero VAD の比較**（未実施・承認待ち）: VAD モデルのダウンロードが必要。導入する場合は現行のエネルギー方式と切り替えられる形にし、
   短い発言（「はい」「いいえ」）が消えないか、発話の前後の余白（現行 250 ms）を同じ条件で比べる
2. **辞書ヒントを各区間に効かせる**（未実施）: whisper.cpp の初期プロンプトは、1 回の処理（発話区間のまとまり）の最初の 30 秒にしか効かない。
   長いまとまりの後半ではヒントが使われていない可能性が高い。
   WithCarryInitialPrompt で各区間に同じヒントを渡し、辞書あり／なしで、正しく書ける語の数と「発言していない語の出力（エコー）」を比較する
3. **デコード設定**（未実施）: 根拠のない一括変更はしない。架空の音声（音声合成で作った会議）を用意し、温度の段階、無音判定の閾値を 1 つずつ比べる

比較には、実際の録音ではなく、架空の台本から作った音声（Windows の音声合成など）を使う。
