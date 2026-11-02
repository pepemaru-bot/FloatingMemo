Floating Memo — AI モデルの置き場所
=====================================

この ZIP には AI モデルは入っていません。文字起こし・AI議事録を使う場合だけ、
公式の配布元から自分でダウンロードして、次のフォルダに置いてください。
モデルがなくても、メモの入力・自動保存・PDF / Word 書き出しは使えます。

  models\whisper\ggml-base.bin            … 文字起こし（必須。約 148 MB）
      配布元: https://huggingface.co/ggerganov/whisper.cpp/tree/main
      ファイル名は ggml-base.bin のまま置いてください（ggml-small.bin も可。精度は上がるが遅い）。

  models\qwen\Qwen3-1.7B-Q8_0.gguf        … AI議事録・軽量（約 1.8 GB、必要メモリ 約 2.5 GB）
      配布元: https://huggingface.co/Qwen/Qwen3-1.7B-GGUF/tree/main
  models\qwen\Qwen3-4B-Q4_K_M.gguf        … AI議事録・標準（約 2.5 GB、必要メモリ 約 3.5 GB）
      配布元: https://huggingface.co/Qwen/Qwen3-4B-GGUF/tree/main
      どちらか一方で使えます。両方置いた場合は、議事録ボタン →「AIモデル・専門用語辞書の設定…」で選べます。

  models\haas\  … 文字起こし・試験的な別エンジン Haas-ja（任意。Whisper はそのまま使えます）
      speech.exe と speech.dll（speech.cpp v0.10.0 の Windows x64 Vulkan 版、MIT License）
        配布元: https://github.com/nyosegawa/speech.cpp/releases/tag/v0.10.0
      Haas-616M-ja-v1.0-Q4_K.gguf または Haas-616M-ja-v1.0-Q8_0.gguf（Apache License 2.0）
        配布元: https://huggingface.co/sakasegawa/Haas-ja-v1.0-GGUF （リビジョン 6b965d5）
      silero-vad-309K-v6.2-F32.gguf（MIT License）
        配布元: https://huggingface.co/sakasegawa/silero-vad-GGUF
      4つとも同じフォルダに置き、議事録ボタン →「AIモデル・専門用語辞書の設定…」の
      「音声認識のエンジン」で Haas-ja を選ぶと、次の録音から使われます。
      speech.exe にはコード署名がありません。会社のPCでは、IT部門の規程に従ってください。

置いたあと、起動中のアプリを右上の × で終了し、もう一度起動してください。
アプリ以外のフォルダ（%USERPROFILE%\FloatingMemo\models\whisper など）に置くこともできます。
詳しい手順・確認方法・トラブル対処は README（GitHub のリポジトリ）を参照してください。

モデルのライセンスはそれぞれの配布元に従います
（Whisper: MIT License / Qwen3: Apache License 2.0）。
