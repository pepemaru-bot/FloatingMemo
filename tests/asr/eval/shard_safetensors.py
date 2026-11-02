"""Splits one large .safetensors file into shards of at most MAX bytes, byte for byte (no weight changes).

Why: on Windows, safetensors' torch loader maps the whole file copy-on-write, which needs commit
charge equal to the file size (67 GB here > RAM + page file: os error 1455). Shards are opened one
after another by the converters, so each needs only its own size.
Every shard keeps the original metadata; tensors whose name starts with FIRST go into the first
shard (the CTC timestamp head: the converter reads it from the one shard whose metadata marks it).
Writes model-0000N-of-0000M.safetensors and model.safetensors.index.json, then re-reads every
shard and checks each tensor's SHA-256 against the source.
Usage: python shard_safetensors.py <source.safetensors> <out dir>
"""
import hashlib
import json
import struct
import sys
from pathlib import Path

MAX = 4 * 1024 ** 3
FIRST = ("ctc_timestamp.",)
CHUNK = 64 * 1024 ** 2


def read_header(path: Path):
    with open(path, "rb") as f:
        (size,) = struct.unpack("<Q", f.read(8))
        return json.loads(f.read(size)), 8 + size


def copy_hash(src, dst, length: int) -> str:
    digest = hashlib.sha256()
    while length > 0:
        block = src.read(min(CHUNK, length))
        digest.update(block)
        if dst is not None:
            dst.write(block)
        length -= len(block)
    return digest.hexdigest()


def main(source: str, out: str) -> None:
    src_path, out_dir = Path(source), Path(out)
    out_dir.mkdir(parents=True, exist_ok=True)
    header, base = read_header(src_path)
    metadata = header.pop("__metadata__", None)
    names = sorted(header, key=lambda n: header[n]["data_offsets"][0])
    names = [n for n in names if n.startswith(FIRST)] + [n for n in names if not n.startswith(FIRST)]
    shards, current, size = [], [], 0
    for n in names:
        b, e = header[n]["data_offsets"]
        if current and size + (e - b) > MAX:
            shards.append(current)
            current, size = [], 0
        current.append(n)
        size += e - b
    shards.append(current)

    weight_map, expected = {}, {}
    with open(src_path, "rb") as src:
        for i, shard in enumerate(shards, 1):
            name = f"model-{i:05d}-of-{len(shards):05d}.safetensors"
            new_header, offset = {}, 0
            for n in shard:
                b, e = header[n]["data_offsets"]
                new_header[n] = {"dtype": header[n]["dtype"], "shape": header[n]["shape"], "data_offsets": [offset, offset + e - b]}
                offset += e - b
            if metadata is not None:
                new_header["__metadata__"] = metadata
            raw = json.dumps(new_header, separators=(",", ":")).encode()
            raw += b" " * (-len(raw) % 8)
            with open(out_dir / name, "wb") as dst:
                dst.write(struct.pack("<Q", len(raw)))
                dst.write(raw)
                for n in shard:
                    b, e = header[n]["data_offsets"]
                    src.seek(base + b)
                    expected[n] = copy_hash(src, dst, e - b)
                    weight_map[n] = name
            print(f"{name}: {len(shard)} tensors, {offset / 1024 ** 3:.2f} GiB", flush=True)

    index = {"metadata": {"total_size": sum(header[n]["data_offsets"][1] - header[n]["data_offsets"][0] for n in names)}, "weight_map": weight_map}
    (out_dir / "model.safetensors.index.json").write_text(json.dumps(index, indent=1), encoding="utf-8")

    # Check: every tensor read back from its shard equals the source.
    bad = 0
    for shard_file in sorted(out_dir.glob("model-*.safetensors")):
        h, b0 = read_header(shard_file)
        h.pop("__metadata__", None)
        with open(shard_file, "rb") as f:
            for n, info in h.items():
                b, e = info["data_offsets"]
                f.seek(b0 + b)
                if copy_hash(f, None, e - b) != expected[n]:
                    bad += 1
    print(f"tensors {len(expected)}, mismatches {bad}")
    if bad:
        sys.exit(1)


if __name__ == "__main__":
    main(sys.argv[1], sys.argv[2])
