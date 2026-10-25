# ローカル AI モデルの再選定（調査、2026-10-26）

AI議事録用のローカル LLM 候補を、公式のモデルカードと配布元で確認した結果。**まだどのモデルもダウンロードしていない**
（容量と保存先を示して承認を得てから行う）。評価は架空の会議データだけで行う。

## 実行環境（この PC で確認）

- Windows 11、WinUI 3、.NET 10、LLamaSharp 0.27.0（同梱 llama.cpp 3f7c29d、CPU と Vulkan）
- GPU: NVIDIA GeForce RTX 4060 Ti 8 GB（通常時の使用 約 1.7 GB）、RAM 48 GB、Intel Core i7-10700

## 互換性の確認方法

- 同梱の llama.cpp（CPU 版・Vulkan 版の llama.dll）に、各モデルのアーキテクチャ名が含まれるかを確認した。
  含まれていれば「読み込める見込み」、実際の読み込み・速度・品質は未確認
- 本体アプリは Qwen3 の ChatML 形式（思考なし）でプロンプトを組み立てている。別系統のモデルは
  チャットテンプレートの切り替え（モデル内蔵テンプレートの利用など）が必要。**本体へは組み込まず、独立した評価ハーネスで先に比べる**

## 候補

| モデル | 配布元（公式） | パラメータ | ライセンス | アーキテクチャ（同梱 llama.cpp） | GGUF（Q4 系） | 思考モード | 判定 |
|---|---|---|---|---|---|---|---|
| Qwen3-4B（現行） | Qwen | 4B | Apache-2.0 | qwen3（対応） | 導入済み 2.5 GB | 既定オン（アプリで空の think を入れて無効化） | 基準 |
| Qwen3.5-4B | Qwen/Qwen3.5-4B | 4.66B | Apache-2.0 | qwen35（対応） | unsloth Q4_K_M 2.74 GB（公式 GGUF なし） | 既定オン（enable_thinking） | 比較する |
| Qwen3.5-9B | Qwen/Qwen3.5-9B | 約 9B | Apache-2.0 | qwen35（対応） | unsloth Q4_K_M 5.68 GB | 既定オン | 比較する（高品質候補） |
| Gemma 4 E4B | google/gemma-4-E4B-it | 8.0B（実効 4B 級） | Apache-2.0 | gemma4（対応） | Google 公式 QAT Q4_0 5.15 GB | 既定オフ | 比較する（テンプレート切替が必要） |
| Nemotron Nano 9B v2 Japanese | nvidia/NVIDIA-Nemotron-Nano-9B-v2-Japanese | 8.9B | NVIDIA Open Model License | nemotron_h（Mamba 系ハイブリッド、対応） | mmnga-o Q4_K_M 6.53 GB（公式 GGUF なし） | 既定オン | 比較する（構造が異なるため単純差し替え不可。ライセンス条件の確認が必要） |
| CAT-Thinking-8B | cyberagent/CAT-Thinking-8B | 8.2B（Qwen3-8B 系） | Apache-2.0 | qwen3（対応） | mmnga-o Q4_K_M 5.03 GB | 既定オン | 比較する（推論特化。議事録向きかは要確認） |
| Phi-4-mini-instruct | microsoft/Phi-4-mini-instruct | 3.8B | MIT | phi3（対応） | unsloth Q4_K_M 2.49 GB | なし | 軽量の比較対象（日本語は対応言語の一つ） |
| Qwen3-14B | Qwen | 14B | Apache-2.0 | qwen3（対応） | 約 9 GB | 既定オン | 今回は対象外: 8 GB の GPU に収まらず、CPU 併用で大幅に遅くなる |
| CyberAgentLM3 | cyberagent | 22B | Apache-2.0 | llama 系 | 13 GB 前後 | なし | 今回は対象外: この PC では大きすぎる |

- 公式 GGUF がないモデルは第三者の変換版（unsloth、mmnga-o）を使う。ダウンロード後に配布元のハッシュと照合する
- Gemma 4・Qwen3.5 は画像入力用のファイル（mmproj）も配布されているが、議事録には不要なので取得しない

## ダウンロードの承認依頼（未実行）

保存先の案: `H:\dev\FloatingMemo-models-eval\`（普段のモデルフォルダとは別。アプリの動作には影響しない）

| 優先 | ファイル | サイズ |
|---|---|---|
| 1 | Qwen3.5-4B-Q4_K_M.gguf（unsloth） | 2.74 GB |
| 1 | Qwen3.5-9B-Q4_K_M.gguf（unsloth） | 5.68 GB |
| 2 | gemma-4-E4B_q4_0-it.gguf（Google 公式 QAT） | 5.15 GB |
| 2 | NVIDIA-Nemotron-Nano-9B-v2-Japanese-Q4_K_M.gguf（mmnga-o） | 6.53 GB |
| 3 | CAT-Thinking-8B-Q4_K_M.gguf（mmnga-o） | 5.03 GB |
| 3 | Phi-4-mini-instruct-Q4_K_M.gguf（unsloth） | 2.49 GB |

合計 約 27.6 GB（優先 1 だけなら 約 8.4 GB）。H: の空き 約 1.6 TB。

## ベンチマーク計画（承認後）

1. 独立した評価ハーネス（テストプロジェクト内、本体アプリとは別）で、モデルごとのチャットテンプレートで読み込み・生成できるかを確認
2. 共通条件（同じ架空の会議、同じプロンプト、思考なし、温度 0、同じコンテキスト長）でモデル単体を比較
3. Floating Memo のハーネス（事実抽出 → 検査 → 話題ごとの議事録）を通した結果を比較
4. 各モデルの推奨形式（思考あり、推奨パラメータ）に合わせた比較は別の表にする
5. 指標: 重要事項の網羅率、事実の正確さ、誤った断定・捏造の数、話題統合、重複、ToDo の正確さ、日本語の自然さ、
   処理時間、RAM・VRAM 使用量
