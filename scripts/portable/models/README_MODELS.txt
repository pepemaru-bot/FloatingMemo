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

置いたあと、起動中のアプリを右上の × で終了し、もう一度起動してください。
アプリ以外のフォルダ（%USERPROFILE%\FloatingMemo\models\whisper など）に置くこともできます。
詳しい手順・確認方法・トラブル対処は README（GitHub のリポジトリ）を参照してください。

モデルのライセンスはそれぞれの配布元に従います
（Whisper: MIT License / Qwen3: Apache License 2.0）。
