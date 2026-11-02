"""Scores every speech-recognition result (<case>/asr/*.json) against the reference (truth.json).

Metrics (written to <root>/asr-scores.md and asr-scores.json):
  CER raw     characters, only spaces removed (writing differences such as 89点/八十九点 count as errors)
  CER norm    after normalisation: NFKC, punctuation removed, kanji numerals to digits (八十九→89,
              三万二千→32000), roman numerals (II→2), lower case
  CER norm-f  as norm, and fillers (えー, えっと, あの, うーん, まあ ...) removed on both sides
  WER         words of the normalised text split by MeCab (fugashi + unidic-lite)
  terms       dictionary terms written right (count in reference vs in output, normalised)
  numbers     numbers of the reference found in the output (as a multiset, normalised)
  missing     reference statements of which fewer than half of the characters were recognised
  short       of those, the short answers (はい, いいえ, 分かりました ...)
  halluc      runs of 6 or more output characters that match nothing in the reference (inserted text)
  speakers    for outputs with speaker labels: statements attributed to the right speaker (best mapping)
Usage: python score_asr.py <root>
"""
import json
import re
import sys
import unicodedata
from collections import Counter
from itertools import permutations
from pathlib import Path

import fugashi
from rapidfuzz.distance import Levenshtein

FILLERS = ["えーと", "えっと", "えー", "あのー", "あの", "うーん", "まあ", "ええと"]
SHORT = {"はい", "いいえ", "わかりました", "分かりました", "どうぞ"}
DIGIT = {"〇": 0, "零": 0, "一": 1, "二": 2, "三": 3, "四": 4, "五": 5, "六": 6, "七": 7, "八": 8, "九": 9}
UNIT = {"十": 10, "百": 100, "千": 1000}
tagger = fugashi.Tagger()


def kanji_number(s: str) -> int:
    total, section, digit = 0, 0, None
    for ch in s:
        if ch in DIGIT or ch.isdigit():
            d = DIGIT[ch] if ch in DIGIT else int(ch)
            digit = d if digit is None else digit * 10 + d
        elif ch in UNIT:
            section += (digit if digit is not None else 1) * UNIT[ch]
            digit = None
        elif ch == "万":
            section += digit or 0
            total += (section or 1) * 10000
            section, digit = 0, None
    return total + section + (digit or 0)


def numerals(text: str) -> str:
    # Every run of numerals, kanji or mixed with digits (八十九, 3万2千, 4千500); both sides are
    # normalised the same way, so words such as 一般 (1般) still compare equal.
    text = text.replace(",", "")
    text = re.sub(r"[0-9〇零一二三四五六七八九十百千万]+",
                  lambda m: str(kanji_number(m.group())) if any(c in DIGIT or c in UNIT or c == "万" for c in m.group()) else m.group(), text)
    # 56点4 (spoken decimal) -> 56.4
    return re.sub(r"(\d+)点(\d+)", r"\1.\2", text)


def norm(text: str, fillers: bool = False) -> str:
    t = unicodedata.normalize("NFKC", text).lower()
    t = re.sub(r"\bii\b|ii(?=[^a-z]|$)", "2", t).replace("ⅱ", "2").replace("ⅰ", "1")
    t = re.sub(r"(?<![a-z])i(?=[^a-z]|$)", "1", t)
    t = numerals(t)
    t = re.sub(r"[\s、。,.!?！？「」『』・…―\-:：;；()（）]", lambda m: "." if m.group() == "." else "", t)
    t = re.sub(r"(?<!\d)\.|\.(?!\d)", "", t)  # keep decimal points only
    if fillers:
        for f in FILLERS:
            t = t.replace(f, "")
    return t


def raw(text: str) -> str:
    return re.sub(r"\s", "", text)


def sentence_ends(text: str) -> tuple[str, list[int]]:
    """The normalized text and where its sentences end (after 。？！?!), as positions in it."""
    out, ends = "", []
    for sentence in re.split(r"(?<=[。？！?!])", text):
        if sentence.strip():
            out += norm(sentence)
            if re.search(r"[。？！?!]\s*$", sentence):
                ends.append(len(out))
    return out, ends


def sentence_boundaries(ref_text: str, hyp_text: str, tolerance: int = 2) -> tuple[int, int, int]:
    """Sentence ends in the output that match one in the reference (within a couple of
    characters after aligning the normalized texts): (matched, output ends, reference ends).
    Precision = matched / output: ends put inside sentences (statements cut in two).
    Recall = matched / reference: statements run together."""
    ref, ref_ends = sentence_ends(ref_text)
    hyp, hyp_ends = sentence_ends(hyp_text)
    to_ref = [0] * (len(hyp) + 1)
    for op in Levenshtein.opcodes(ref, hyp):
        for k in range(op.dest_start, op.dest_end + 1):
            span = max(1, op.dest_end - op.dest_start)
            to_ref[k] = op.src_start + round((k - op.dest_start) * (op.src_end - op.src_start) / span)
    matched, used = 0, set()
    for e in hyp_ends:
        near = [r for r in ref_ends if abs(r - to_ref[e]) <= tolerance and r not in used]
        if near:
            used.add(near[0])
            matched += 1
    return matched, len(hyp_ends), len(ref_ends)


def cer(ref: str, hyp: str) -> float:
    return Levenshtein.distance(ref, hyp) / max(1, len(ref))


def words(text: str) -> list[str]:
    return [w.surface for w in tagger(text)]


def wer(ref: str, hyp: str) -> float:
    r, h = words(ref), words(hyp)
    return Levenshtein.distance(r, h) / max(1, len(r))


def per_statement(statements: list[str], hyp: str):
    """Aligns the whole reference to the output; returns, per statement, the share of its characters
    matched, and the inserted output runs (text in the output that matches nothing)."""
    ref = "".join(statements)
    owner = [i for i, s in enumerate(statements) for _ in s]
    matched = [0] * len(statements)
    inserted, run = [], ""
    for op in Levenshtein.opcodes(ref, hyp):
        if op.tag == "equal":
            for k in range(op.src_start, op.src_end):
                matched[owner[k]] += 1
        if op.tag == "insert" or (op.tag == "replace" and (op.dest_end - op.dest_start) > 2 * (op.src_end - op.src_start)):
            run += hyp[op.dest_start:op.dest_end]
        else:
            if len(run) >= 6:
                inserted.append(run)
            run = ""
    if len(run) >= 6:
        inserted.append(run)
    return [m / max(1, len(s)) for m, s in zip(matched, statements)], inserted


def speaker_votes_by_text(truth, segments):
    """Without times: each reference statement takes the speaker of the output characters aligned to it."""
    statements = [norm(u["text"], True) for u in truth]
    owner = [i for i, s in enumerate(statements) for _ in s]
    hyp, spk = "", []
    for s in segments:
        t = norm(s["text"], True)
        hyp += t
        spk += [s["speaker"]] * len(t)
    counts = [Counter() for _ in statements]
    for op in Levenshtein.opcodes("".join(statements), hyp):
        if op.tag == "equal":
            for k in range(op.src_end - op.src_start):
                counts[owner[op.src_start + k]][spk[op.dest_start + k]] += 1
    return [c.most_common(1)[0][0] if c else None for c in counts]


def speaker_accuracy(truth, segments):
    labelled = [s for s in segments if s.get("speaker") is not None]
    if not labelled:
        return None
    if any(s.get("start") is None for s in labelled):
        votes = speaker_votes_by_text(truth, labelled)
    else:
        votes = []
        for u in truth:
            c = Counter()
            for s in labelled:
                overlap = min(u["end"], s["end"]) - max(u["start"], s["start"])
                if overlap > 0:
                    c[s["speaker"]] += overlap
            votes.append(c.most_common(1)[0][0] if c else None)
    ref_spk = sorted({u["spk"] for u in truth})
    hyp_spk = sorted({v for v in votes if v is not None}, key=str)
    best = 0
    for perm in permutations(hyp_spk, min(len(hyp_spk), len(ref_spk))):
        mapping = dict(zip(perm, ref_spk))
        best = max(best, sum(1 for u, v in zip(truth, votes) if v is not None and mapping.get(v) == u["spk"]))
    return best, len(truth), len(hyp_spk)


def score(case_dir: Path, result_path: Path) -> dict:
    truth = json.loads((case_dir / "truth.json").read_text(encoding="utf-8"))
    result = json.loads(result_path.read_text(encoding="utf-8"))
    statements = [u["text"] for u in truth["utterances"]]
    hyp_text = "".join(s["text"] for s in result["segments"])
    ref_raw, hyp_raw = raw("".join(statements)), raw(hyp_text)
    ref_n, hyp_n = norm("".join(statements)), norm(hyp_text)
    ref_f, hyp_f = norm("".join(statements), True), norm(hyp_text, True)
    terms = [norm(t) for t in truth["terms"]]
    terms_total = sum(ref_n.count(t) for t in terms)
    terms_ok = sum(min(ref_n.count(t), hyp_n.count(t)) for t in terms)
    ref_numbers = Counter(re.findall(r"\d+(?:\.\d+)?", ref_n))
    hyp_numbers = Counter(re.findall(r"\d+(?:\.\d+)?", hyp_n))
    numbers_ok = sum(min(n, hyp_numbers[k]) for k, n in ref_numbers.items())
    shares, inserted = per_statement([norm(s, True) for s in statements], hyp_f)
    sent_ok, sent_hyp, sent_ref = sentence_boundaries("".join(statements), hyp_text)
    missing = [i for i, share in enumerate(shares) if share < 0.5]
    short_total = [i for i, s in enumerate(statements) if norm(s) in {norm(x) for x in SHORT} or len(norm(s)) <= 5]
    spk = speaker_accuracy(truth["utterances"], result["segments"]) if result.get("speakers") else None
    duration = truth["seconds"]
    return {
        "case": truth["name"], "set": truth["set"], "setting": result["setting"], "model": result["model"], "audio": result["audio"],
        "cer_raw": cer(ref_raw, hyp_raw), "cer_norm": cer(ref_n, hyp_n), "cer_norm_f": cer(ref_f, hyp_f), "wer": wer(ref_n, hyp_n),
        "terms": terms_ok, "terms_total": terms_total, "numbers": numbers_ok, "numbers_total": sum(ref_numbers.values()),
        "missing": len(missing), "statements": len(statements),
        "sent_ok": sent_ok, "sent_hyp": sent_hyp, "sent_ref": sent_ref,
        "short_missing": len([i for i in missing if i in short_total]), "short_total": len(short_total),
        "quiet_missing": len([i for i in missing if truth["utterances"][i]["quiet"]]), "quiet_total": sum(u["quiet"] for u in truth["utterances"]),
        "halluc_runs": len(inserted), "halluc_chars": sum(len(r) for r in inserted), "halluc_examples": inserted[:3],
        "missing_examples": [statements[i] for i in missing][:5],
        "speakers": f"{spk[0]}/{spk[1]}（推定話者 {spk[2]}）" if spk else "非対応",
        "seconds": result["seconds"], "rtf": result["seconds"] / duration, "audio_seconds": duration,
        "peak_ram_mb": result.get("peak_ram_mb"), "peak_vram_mb": result.get("peak_vram_mb"),
    }


def main(root: str) -> None:
    rows = []
    for case_dir in sorted(Path(root).iterdir()):
        if not (case_dir / "truth.json").exists():
            continue
        for result_path in sorted(p for p in (case_dir / "asr").glob("*.json") if not p.name.endswith(".raw.json")):
            rows.append(score(case_dir, result_path))
    Path(root, "asr-scores.json").write_text(json.dumps(rows, ensure_ascii=False, indent=1), encoding="utf-8")
    lines = ["| 会議 | 設定 | 音声 | CER（正規化前） | CER（正規化後） | CER（＋フィラー除去） | WER | 用語 | 数値 | 欠落した発言 | うち短い応答 | うち小声 | 幻覚（挿入） | 話者 | 処理時間 | RTF | RAM | VRAM |",
             "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|"]
    for r in rows:
        lines.append(f"| {r['case']} | {r['setting']} | {r['audio']} | {r['cer_raw']:.1%} | {r['cer_norm']:.1%} | {r['cer_norm_f']:.1%} | {r['wer']:.1%} | "
                     f"{r['terms']}/{r['terms_total']} | {r['numbers']}/{r['numbers_total']} | {r['missing']}/{r['statements']} | "
                     f"{r['short_missing']}/{r['short_total']} | {r['quiet_missing']}/{r['quiet_total']} | {r['halluc_runs']}件 {r['halluc_chars']}字 | {r['speakers']} | "
                     f"{r['seconds']:.1f}s | {r['rtf']:.3f} | {r['peak_ram_mb']}MB | {r['peak_vram_mb']}MB |")
    Path(root, "asr-scores.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print("\n".join(lines))


def selftest() -> None:
    """Writings that must compare equal (each pair once found the normalisation wrong)."""
    same = [("八十九点", "89点"), ("数学II", "数学2"), ("数学Ⅱ", "数学２"), ("三万二千円", "32,000円"), ("三万二千円", "3万2000円"),
            ("一人三万二千円", "1人3万2千円"), ("二十八人", "28人"), ("十二月十四日", "12月14日"), ("偏差値は56.4で", "偏差値は五十六点四で"),
            ("四千五百部", "4,500部"), ("八十九点、偏差値は六十二", "89点、偏差値は62")]
    for a, b in same:
        assert norm(a) == norm(b), (a, b, norm(a), norm(b))
    assert norm("えー、佐倉さんは", True) == norm("佐倉さんは", True)
    assert norm("英語コミュニケーション") == "英語コミュニケーション"
    shares, inserted = per_statement(["はい", "定員は30人です"], "はい定員は30人ですありがとうございました")
    assert shares == [1.0, 1.0] and inserted == ["ありがとうございました"], (shares, inserted)
    print("selftest OK")


if __name__ == "__main__":
    selftest() if sys.argv[1] == "--selftest" else main(sys.argv[1])
