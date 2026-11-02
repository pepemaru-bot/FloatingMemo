"""Builds the meeting audio of a made-up scenario from its synthesized utterances (synth-utterances.ps1).

Output in <dir>: clean.wav and noisy.wav (16 kHz mono), truth.json (each utterance with speaker,
start, end and its written text) and truth.txt (one utterance per line). Gaps come from the scenario
("gap" seconds before an utterance; negative = it starts before the previous one ends, an overlap),
"gain" in dB makes a voice quieter, "event" mixes a sound (door, phone, clatter) under the gap.
noisy.wav adds room noise (pink noise and a 100 Hz hum) at 15 dB below the speech level.
Usage: python assemble.py <scenario.json> <dir>   (utterances in <dir>/utt)
"""
import json
import re
import sys
from pathlib import Path

import numpy as np
import soundfile as sf
from scipy.signal import resample_poly

RATE = 16000


def written(text: str) -> str:
    return re.sub(r"\{([^|{}]+)\|[^{}]+\}", r"\1", text)


def load(path: Path) -> np.ndarray:
    data, rate = sf.read(path, dtype="float32")
    if data.ndim > 1:
        data = data.mean(axis=1)
    if rate != RATE:
        g = np.gcd(rate, RATE)
        data = resample_poly(data, RATE // g, rate // g).astype(np.float32)
    # The voices leave a little silence at both ends: trim it so the gaps mean what they say.
    loud = np.flatnonzero(np.abs(data) > 0.01)
    return data[max(0, loud[0] - 400): loud[-1] + 400] if loud.size else data


def event(kind: str, rng: np.random.Generator) -> np.ndarray:
    t = np.arange(int(RATE * 1.2)) / RATE
    if kind == "phone":  # two-tone ring, twice
        tone = 0.25 * (np.sin(2 * np.pi * 440 * t) + np.sin(2 * np.pi * 480 * t)) * ((t % 0.6) < 0.4)
        return tone.astype(np.float32)
    if kind == "door":  # low thump
        n = int(RATE * 0.4)
        return (0.6 * np.exp(-np.arange(n) / (RATE * 0.05)) * np.sin(2 * np.pi * 70 * np.arange(n) / RATE)).astype(np.float32)
    # clatter: a few short clicks
    out = np.zeros(int(RATE * 0.8), np.float32)
    for start in rng.integers(0, len(out) - 800, 6):
        out[start:start + 800] += (0.4 * rng.standard_normal(800) * np.exp(-np.arange(800) / 120)).astype(np.float32)
    return out


def main(scenario_path: str, folder: str) -> None:
    scenario = json.loads(Path(scenario_path).read_text(encoding="utf-8"))
    out = Path(folder)
    rng = np.random.default_rng(7)
    pieces, truth = [], []
    cursor, prev_end = RATE * 1, RATE * 1
    for i, u in enumerate(scenario["utterances"], 1):
        voice = load(out / "utt" / f"utt-{i:03d}.wav") * (10 ** (u.get("gain", 0) / 20))
        gap = int(u.get("gap", 0.6) * RATE)
        start = max(0, prev_end + gap) if i > 1 else cursor
        if "event" in u:
            pieces.append((max(0, start - int(0.9 * RATE)), event(u["event"], rng)))
        pieces.append((start, voice))
        truth.append({"i": i, "spk": u["spk"], "name": scenario["speakers"][u["spk"]]["name"], "start": round(start / RATE, 2),
                      "end": round((start + len(voice)) / RATE, 2), "text": written(u["text"]),
                      "quiet": u.get("gain", 0) <= -10, "fast": u.get("rate", 1.0) > 1.1, "overlap": u.get("gap", 0.6) < 0})
        prev_end = max(prev_end, start + len(voice)) if u.get("gap", 0.6) < 0 else start + len(voice)
    length = max(s + len(p) for s, p in pieces) + RATE
    clean = np.zeros(length, np.float32)
    for s, p in pieces:
        clean[s:s + len(p)] += p
    clean = np.clip(clean, -1, 1)

    speech_rms = np.sqrt(np.mean(np.concatenate([p for _, p in pieces]) ** 2))
    white = rng.standard_normal(length)
    pink = np.fft.irfft(np.fft.rfft(white) / np.sqrt(np.maximum(1, np.arange(length // 2 + 1))), length)
    noise = pink / np.sqrt(np.mean(pink ** 2)) + 0.3 * np.sin(2 * np.pi * 100 * np.arange(length) / RATE)
    noise *= speech_rms * 10 ** (-15 / 20) / np.sqrt(np.mean(noise ** 2))
    noisy = np.clip(clean + noise.astype(np.float32), -1, 1)

    sf.write(out / "clean.wav", clean, RATE, subtype="PCM_16")
    sf.write(out / "noisy.wav", noisy, RATE, subtype="PCM_16")
    meta = {"name": scenario["name"], "set": scenario["set"], "title": scenario["title"], "terms": scenario["terms"],
            "seconds": round(length / RATE, 1), "utterances": truth}
    (out / "truth.json").write_text(json.dumps(meta, ensure_ascii=False, indent=1), encoding="utf-8")
    (out / "truth.txt").write_text("\n".join(t["text"] for t in truth), encoding="utf-8")
    print(f"OK {scenario['name']}: {length / RATE:.1f} s, {len(truth)} utterances")


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
