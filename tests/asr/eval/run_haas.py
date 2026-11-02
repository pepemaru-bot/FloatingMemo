"""Runs Haas-ja-v1.0 (speech.cpp CLI, local Q8_0 GGUF, Vulkan GPU) over the made-up meetings and
writes each result in the common shape (<case>/asr/haas-q8-<setting>-<audio>.json, see AsrEvalExport.cs):
model, settings, segments (start, end, speaker, text), seconds, peak RAM / VRAM.

Settings:
  whole      the whole file in one request (the model card advises regions above ~30 s; long-audio stress test)
  vad10      Silero VAD regions with the CLI's `asr --vad` defaults (threshold 0.5, pad 300 ms,
             min silence 500 ms, longest region 10 s), each region recognized alone
  vad28      the same with regions up to 28 s (the app's longest Whisper chunk)
  vad10-kw   vad10 + custom vocabulary: the meeting's dictionary terms with their kana readings
  vad10-kwterm  vad10 + custom vocabulary: the terms alone (a dictionary without readings)
The regions are cut by `speech vad --split` and the region files recognized in one `speech asr` call
(the same as `speech asr --vad`, which gives keywords no per-region text); time and memory cover both.
Usage: python run_haas.py <speech.exe> <haas.gguf> <silero.gguf> <root> <scenario-dir> <setting> [audio ...]
"""
import json
import re
import subprocess
import sys
import tempfile
import threading
import time
from pathlib import Path

import psutil

VAD = ["--threshold", "0.5", "--speech-pad-ms", "300", "--min-silence-duration-ms", "500"]
# Readings of the parts the scenarios do not mark (they mark only the made-up names), as a dictionary's
# owner would write a whole term's reading; d89-legacy has no scenario file.
READINGS = {"高校": "こうこう", "学院": "がくいん", "高専": "こうせん", "架空ホール": "かくうホール", "緑川物流": "みどりかわぶつりゅう",
            "白石製作所": "しろいしせいさくしょ", "稟議書": "りんぎしょ", "青葉商事": "あおばしょうじ", "中央工務店": "ちゅうおうこうむてん"}


def vram_mb() -> int:
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=10).stdout
        return int(out.strip().splitlines()[0])
    except Exception:
        return 0


def measured(args: list[str], peak: dict) -> str:
    """Runs one CLI call, keeping the peak RSS and the GPU memory above the level before it."""
    base = vram_mb()
    proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def sample():
        p = psutil.Process(proc.pid)
        while proc.poll() is None:
            try:
                peak["ram"] = max(peak["ram"], p.memory_info().rss)
            except psutil.Error:
                pass
            peak["vram"] = max(peak["vram"], vram_mb() - base)
            time.sleep(0.1)

    threading.Thread(target=sample, daemon=True).start()
    out, err = proc.communicate()
    if proc.returncode != 0:
        raise RuntimeError(f"exit {proc.returncode}: {err.decode('utf-8', 'replace')[-2000:]}")
    return out.decode("utf-8")


def keywords(terms: list[str], scenarios: Path, readings: bool) -> str:
    """One line per term: 表記=よみ when the scenarios' {表記|よみ} marks give the whole term a kana reading."""
    marks: dict[str, str] = dict(READINGS)
    for scenario in sorted(scenarios.glob("*.json")) if readings else []:
        for m in re.finditer(r"\{([^|{}]+)\|([^{}]+)\}", scenario.read_text(encoding="utf-8")):
            marks[m.group(1)] = m.group(2)
    lines = []
    for t in terms:
        r = t
        for written, reading in sorted(marks.items(), key=lambda kv: -len(kv[0])):
            r = r.replace(written, reading)
        lines.append(f"{t}={r}" if readings and r != t and re.fullmatch(r"[぀-ヿー]+", r) else t)
    return "\n".join(lines)


def run(exe: str, model: str, vad: str, wav: Path, setting: str, kw: str):
    peak = {"ram": 0, "vram": 0}
    start = time.time()
    if setting == "whole":
        out = measured([exe, "asr", model, "--device", "gpu", "--format", "json", "--timestamps", str(wav)], peak)
        result = json.loads(out.splitlines()[-1])
        segs = [{"start": s["start"], "end": s["end"], "speaker": None, "text": s["text"]} for s in result.get("segments", [])]
        if not segs:
            segs = [{"start": None, "end": None, "speaker": None, "text": result["text"]}]
        return segs, time.time() - start, peak, out
    longest = "28" if setting.startswith("vad28") else "10"
    with tempfile.TemporaryDirectory() as tmp:
        regions = json.loads(measured([exe, "vad", vad, "--format", "json", "--split", tmp, *VAD, "--max-speech-duration-s", longest, str(wav)], peak))["regions"]
        files = sorted(Path(tmp).glob("*.wav"))
        assert len(files) == len(regions), (len(files), len(regions))
        args = [exe, "asr", model, "--device", "gpu", "--format", "json"]
        if setting.endswith("-kw") or setting.endswith("-kwterm"):
            args += ["--keywords", kw]
        out = measured(args + [str(f) for f in files], peak)
    texts = [json.loads(line)["text"] for line in out.splitlines() if line.strip()]
    assert len(texts) == len(regions), (len(texts), len(regions))
    segs = [{"start": r["start"], "end": r["end"], "speaker": None, "text": t} for r, t in zip(regions, texts) if t.strip()]
    return segs, time.time() - start, peak, out


def main(exe: str, model: str, vad: str, root: str, scenarios: str, setting: str, audios: list[str]) -> None:
    for case_dir in sorted(Path(root).iterdir()):
        if not (case_dir / "truth.json").exists():
            continue
        truth = json.loads((case_dir / "truth.json").read_text(encoding="utf-8"))
        kw = keywords(truth["terms"], Path(scenarios), readings=setting.endswith("-kw"))
        for audio in audios or ["clean", "noisy"]:
            target = case_dir / "asr" / f"haas-q8-{setting}-{audio}.json"
            if target.exists():
                continue
            try:
                segs, seconds, peak, raw = run(exe, model, vad, case_dir / f"{audio}.wav", setting, kw)
            except Exception as e:  # e.g. out of GPU memory on a long single request: recorded, not hidden
                print(f"{case_dir.name} {setting} {audio}: FAILED {e}", flush=True)
                continue
            (case_dir / "asr" / f"haas-q8-{setting}-{audio}.raw.json").write_text(raw, encoding="utf-8")
            target.write_text(json.dumps({
                "model": "Haas-ja-v1.0 (Q8_0 GGUF rev 6b965d5, speech.cpp 0.10.0, Vulkan RTX 4060 Ti)",
                "setting": f"haas-q8-{setting}", "settings": f"setting={setting} vad={' '.join(VAD)}" + (f" keywords={kw!r}" if "-kw" in setting else ""),
                "audio": audio, "seconds": round(seconds, 2), "peak_ram_mb": peak["ram"] // 1024 // 1024, "peak_vram_mb": max(0, peak["vram"]),
                "speakers": False, "segments": segs,
            }, ensure_ascii=False, indent=1), encoding="utf-8")
            print(f"{case_dir.name} {setting} {audio}: {seconds:.1f}s, {len(segs)} segments, RAM {peak['ram'] / 1024 ** 2:.0f} MB, VRAM {peak['vram']} MB", flush=True)


if __name__ == "__main__":
    main(*sys.argv[1:7], sys.argv[7:])
