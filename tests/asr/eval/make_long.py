"""Builds a long made-up recording from the assembled meetings, to look for statements lost by VAD,
chunking or long-input decoding: the meetings one after another, the sequence repeated, with a pause
between them. Output in <out>: clean.wav, noisy.wav and truth.json (utterance times shifted, the
union of the terms, set = holdout when any part is held out). Only for recognition metrics: the
minutes harness does not score it.
Usage: python make_long.py <root> <out> <repeat> <case> [case ...]
"""
import json
import sys
from pathlib import Path

import numpy as np
import soundfile as sf

RATE = 16000
PAUSE = 3.0


def main(root: str, out: str, repeat: int, cases: list[str]) -> None:
    target = Path(out)
    target.mkdir(parents=True, exist_ok=True)
    utterances, terms, sets = [], [], set()
    audio = {"clean": [], "noisy": []}
    offset = 0.0
    for _ in range(repeat):
        for case in cases:
            truth = json.loads((Path(root) / case / "truth.json").read_text(encoding="utf-8"))
            sets.add(truth["set"])
            terms += [t for t in truth["terms"] if t not in terms]
            length = None
            for kind in audio:
                data, rate = sf.read(Path(root) / case / f"{kind}.wav", dtype="float32")
                assert rate == RATE and data.ndim == 1
                audio[kind] += [data, np.zeros(int(RATE * PAUSE), np.float32)]
                length = len(data) / RATE
            for u in truth["utterances"]:
                utterances.append({**u, "i": len(utterances) + 1, "start": round(u["start"] + offset, 3), "end": round(u["end"] + offset, 3)})
            offset += length + PAUSE
    for kind, parts in audio.items():
        sf.write(target / f"{kind}.wav", np.concatenate(parts), RATE, subtype="PCM_16")
    (target / "truth.json").write_text(json.dumps({
        "name": target.name, "set": "holdout" if "holdout" in sets else "dev", "title": f"{'+'.join(cases)} ×{repeat}",
        "terms": terms, "seconds": round(offset, 1), "utterances": utterances,
    }, ensure_ascii=False, indent=1), encoding="utf-8")
    (target / "truth.txt").write_text("\n".join(u["text"] for u in utterances) + "\n", encoding="utf-8")
    (target / "asr").mkdir(exist_ok=True)
    print(f"{target.name}: {offset:.1f} s, {len(utterances)} utterances")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2], int(sys.argv[3]), sys.argv[4:])
