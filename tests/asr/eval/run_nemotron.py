"""Runs Nemotron 3.5 Transcribe (NeMo-Speech.cpp CLI, local GGUF, CPU) over the made-up meetings and
writes each result in the common shape (<case>/asr/<setting>-<audio>.json, see AsrEvalExport.cs):
model, settings, segments (start, end, speaker, text), seconds, peak RAM / VRAM.

Settings: "plain" (the CLI's default transcription prompt, language ja), "context" (the model card's
context-term prompt with the meeting's dictionary terms), "diarize" (speaker-attributed instruction
with word timestamps, for the speaker metric).
Usage: python run_nemotron.py <exe> <decoder.gguf> <root> <setting> [audio ...]
"""
import json
import re
import subprocess
import sys
import threading
import time
from pathlib import Path

import psutil

# Small-machine settings (docs/salm/configuration.md: one request at a time, one hour of audio).
CONFIG = ["--device", "cpu", "--asr.salm.max_sequences", "1", "--asr.salm.n_ctx_per_sequence", "73728",
          "--asr.salm.mtp_draft", "0", "--asr.salm.n_batch", "1024", "--asr.salm.encoder_max_batch", "4"]
PUNCTUATED_SPEAKERS = ("Transcribe this audio with punctuation and capitalization. Include all spoken words and verbal fillers. "
                       "Write numbers in their standard written form. Put speaker tags <spk:0> <spk:1> <spk:2> <spk:3> on the transcription.")


def vram_mb() -> int:
    try:
        out = subprocess.run(["nvidia-smi", "--query-gpu=memory.used", "--format=csv,noheader,nounits"], capture_output=True, text=True, timeout=10).stdout
        return int(out.strip().splitlines()[0])
    except Exception:
        return 0


def run(exe: str, model: str, wav: Path, setting: str, terms: list[str]):
    args = [exe, "transcribe", str(wav), "--model", model, "-l", "ja", "--format", "json", *CONFIG]
    if setting == "context":
        args += ["--instruction", "Transcribe with punctuation and capitalization. Context terms: " + ", ".join(terms) + "."]
    elif setting == "diarize":
        args += ["--instruction", PUNCTUATED_SPEAKERS]
    elif setting == "diarizecli":
        args += ["--diarize"]  # the CLI's speaker prompt (no punctuation) and the encoder's speaker branch
    base_vram, peak = vram_mb(), {"ram": 0, "vram": 0}
    start = time.time()
    proc = subprocess.Popen(args, stdout=subprocess.PIPE, stderr=subprocess.PIPE)

    def sample():
        p = psutil.Process(proc.pid)
        while proc.poll() is None:
            try:
                peak["ram"] = max(peak["ram"], p.memory_info().rss)
            except psutil.Error:
                pass
            peak["vram"] = max(peak["vram"], vram_mb() - base_vram)
            time.sleep(0.5)

    threading.Thread(target=sample, daemon=True).start()
    out, err = proc.communicate()
    seconds = time.time() - start
    if proc.returncode != 0:
        raise RuntimeError(f"exit {proc.returncode}: {err.decode('utf-8', 'replace')[-2000:]}")
    return json.loads(out.decode("utf-8")), seconds, peak, err.decode("utf-8", "replace")


def segments_of(result: dict) -> list[dict]:
    """Segments from the CLI's JSON: by speaker tag (<spk:N>) when the text carries them (speaker
    instruction; no word times then), else by the words' times and speakers, else the whole text."""
    text = result.get("text", "")
    if re.search(r"<spk:\d+>", text):
        parts = re.split(r"\s*<spk:(\d+)>\s*", text)
        return [{"start": None, "end": None, "speaker": int(spk), "text": t.strip()}
                for spk, t in zip(parts[1::2], parts[2::2]) if t.strip()]
    words = result.get("words") or []
    if not words:
        return [{"start": None, "end": None, "speaker": None, "text": text}]
    segs, current = [], None
    for w in words:
        spk = w.get("speaker")
        if current is None or spk != current["speaker"] or current["text"].endswith(("。", "？", "?", "！")):
            current = {"start": w.get("start"), "end": w.get("end"), "speaker": spk, "text": ""}
            segs.append(current)
        current["text"] += w.get("word", w.get("text", ""))
        current["end"] = w.get("end")
    return segs


def write(target: Path, result: dict, setting: str, audio: str, seconds: float, ram_mb: int, vram_mb: int) -> None:
    segs = segments_of(result)
    speakers = any(s["speaker"] is not None for s in segs)
    if not speakers:
        # Without speakers the whole text is the transcript (its punctuation, as the model wrote it).
        segs = [{"start": segs[0]["start"], "end": segs[-1]["end"], "speaker": None, "text": result.get("text", "")}]
    target.write_text(json.dumps({
        "model": "Nemotron 3.5 Transcribe 30B-A4B (IQ4_NL GGUF, NeMo-Speech.cpp 0.3.0, CPU)",
        "setting": f"nemotron-iq4nl-{setting}", "settings": " ".join(CONFIG) + f" setting={setting}",
        "audio": audio, "seconds": round(seconds, 2), "peak_ram_mb": ram_mb, "peak_vram_mb": max(0, vram_mb),
        "speakers": speakers, "segments": segs,
    }, ensure_ascii=False, indent=1), encoding="utf-8")


def main(exe: str, model: str, root: str, setting: str, audios: list[str]) -> None:
    for case_dir in sorted(Path(root).iterdir()):
        if not (case_dir / "truth.json").exists():
            continue
        truth = json.loads((case_dir / "truth.json").read_text(encoding="utf-8"))
        for audio in audios or ["clean", "noisy"]:
            target = case_dir / "asr" / f"nemotron-iq4nl-{setting}-{audio}.json"
            if target.exists():
                continue
            result, seconds, peak, log = run(exe, model, case_dir / f"{audio}.wav", setting, truth["terms"])
            (case_dir / "asr" / f"nemotron-iq4nl-{setting}-{audio}.raw.json").write_text(json.dumps(result, ensure_ascii=False, indent=1), encoding="utf-8")
            write(target, result, setting, audio, seconds, peak["ram"] // 1024 // 1024, peak["vram"])
            print(f"{case_dir.name} {setting} {audio}: {seconds:.1f}s, RAM {peak['ram'] / 1024 ** 3:.1f} GiB", flush=True)


def reparse(root: str) -> None:
    """Rebuilds every result from its saved raw CLI output (keeps the measured time and memory)."""
    for raw in Path(root).glob("*/asr/nemotron-*.raw.json"):
        target = raw.with_name(raw.name.replace(".raw.json", ".json"))
        old = json.loads(target.read_text(encoding="utf-8"))
        setting = old["setting"].removeprefix("nemotron-iq4nl-")
        write(target, json.loads(raw.read_text(encoding="utf-8")), setting, old["audio"], old["seconds"], old["peak_ram_mb"], old["peak_vram_mb"])


if __name__ == "__main__":
    if sys.argv[1] == "--reparse":
        reparse(sys.argv[2])
    else:
        main(sys.argv[1], sys.argv[2], sys.argv[3], sys.argv[4], sys.argv[5:])
