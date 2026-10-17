"""Vendors Milkdown (and its dependency tree) as plain ES modules for the visual editor
(src/FloatingMemo/Editor/visual-vendor/, loaded by Editor/visual.html).

Development-time only (uses the network); the page loads the files from disk with a strict
CSP, no CDN, no bundler, no Node.js. Same safety rules as scripts/update-editor-vendor.ps1:
  - for each package, the newest stable version satisfying every range and published at least
    MIN_AGE_DAYS ago (supply-chain caution);
  - every tarball is checked against the registry's sha512 integrity before it is used;
  - only ES-module files (+ LICENSE) are kept; bare imports are rewritten to relative paths.
Writes vendor/versions.json (name, version, license, integrity, published).

Usage: python -I scripts/update-visual-editor-vendor.py
"""
import base64, datetime, hashlib, io, json, os, posixpath, re, shutil, sys, tarfile, urllib.request

ROOTS = [
    "@milkdown/core", "@milkdown/ctx", "@milkdown/prose", "@milkdown/utils", "@milkdown/transformer",
    "@milkdown/preset-commonmark", "@milkdown/preset-gfm", "@milkdown/plugin-history",
    "@milkdown/plugin-listener", "@milkdown/plugin-clipboard",
]
MIN_AGE_DAYS = 14
HERE = os.path.dirname(os.path.abspath(__file__))
VENDOR = os.path.join(HERE, "..", "src", "FloatingMemo", "Editor", "visual-vendor")
CONDITIONS = ["browser", "import", "module", "default"]  # never "node" / "development"
CUTOFF = datetime.datetime.now(datetime.timezone.utc) - datetime.timedelta(days=MIN_AGE_DAYS)


def get(url):
    with urllib.request.urlopen(url, timeout=60) as r:
        return r.read()


# ----- semver (enough for npm ranges used here) -------------------------------------------
def parse(v):
    m = re.match(r"^v?(\d+)(?:\.(\d+|x|\*))?(?:\.(\d+|x|\*))?(?:-([0-9A-Za-z.-]+))?", v.strip())
    if not m:
        return None
    nums = [None if p in (None, "x", "*") else int(p) for p in m.groups()[:3]]
    return nums, m.group(4)


def cmp(a, b):
    return (a > b) - (a < b)


def satisfies(version, rng):
    ver, pre = parse(version)
    if pre:
        return False
    rng = rng.strip()
    if rng in ("", "*", "latest", "x"):
        return True
    return any(all(comparator(ver, c) for c in re.split(r"\s+", alt.strip()) if c) for alt in rng.split("||"))


def comparator(ver, c):
    if c.startswith("^"):
        n, _ = parse(c[1:]); n = [x or 0 for x in n]
        if n[0] > 0:
            return ver[0] == n[0] and ver >= n
        if n[1] > 0:
            return ver[0] == 0 and ver[1] == n[1] and ver >= n
        return ver == n
    if c.startswith("~"):
        n, _ = parse(c[1:]); n = [x or 0 for x in n]
        return ver[0] == n[0] and ver[1] == n[1] and ver >= n
    for op in (">=", "<=", ">", "<", "="):
        if c.startswith(op):
            n, _ = parse(c[len(op):]); n = [x or 0 for x in n]
            return {">=": ver >= n, "<=": ver <= n, ">": ver > n, "<": ver < n, "=": ver == n}[op]
    n, _ = parse(c)
    if n is None:
        raise ValueError(f"unsupported range {c}")
    return all(x is None or x == ver[i] for i, x in enumerate(n))


# ----- resolve ----------------------------------------------------------------------------
meta, chosen, ranges = {}, {}, {}


def registry(name):
    if name not in meta:
        meta[name] = json.loads(get("https://registry.npmjs.org/" + name.replace("/", "%2F")))
    return meta[name]


def pick(name):
    doc = registry(name)
    want = ranges.get(name, ["*"])
    ok = [v for v in doc["versions"] if not parse(v)[1]
          and datetime.datetime.fromisoformat(doc["time"][v].replace("Z", "+00:00")) <= CUTOFF
          and all(satisfies(v, r) for r in want)]
    if not ok:
        raise SystemExit(f"no version of {name} satisfies {want} and is older than {MIN_AGE_DAYS} days")
    return max(ok, key=lambda v: parse(v)[0])


queue = list(ROOTS)
for r in ROOTS:
    ranges.setdefault(r, [])
changed = True
while changed:  # re-pick until every package satisfies all ranges seen so far
    changed = False
    queue = list(dict.fromkeys(list(chosen) + queue))
    while queue:
        name = queue.pop(0)
        v = pick(name)
        if chosen.get(name) != v:
            chosen[name] = v
            changed = True
        info = meta[name]["versions"][v]
        for dep, rng in (info.get("dependencies") or {}).items():
            if rng not in ranges.setdefault(dep, []):
                ranges[dep].append(rng)
                queue.append(dep)
                changed = True
        for dep, rng in (info.get("peerDependencies") or {}).items():
            if dep.startswith(("@milkdown/", "prosemirror", "@lezer", "@codemirror")) and rng not in ranges.setdefault(dep, []):
                ranges[dep].append(rng)
                queue.append(dep)

# ----- download, verify, extract ------------------------------------------------------------
os.makedirs(VENDOR, exist_ok=True)
for entry in os.listdir(VENDOR):  # the folder itself may be in use (a local test server)
    target = os.path.join(VENDOR, entry)
    shutil.rmtree(target) if os.path.isdir(target) else os.remove(target)


def folder(name):
    return name.lstrip("@").replace("/", "-")


pkgjson, record = {}, []
for name, v in sorted(chosen.items()):
    info = meta[name]["versions"][v]
    data = get(info["dist"]["tarball"])
    integrity = info["dist"].get("integrity", "")
    if not integrity.startswith("sha512-") or base64.b64encode(hashlib.sha512(data).digest()).decode() != integrity[7:]:
        raise SystemExit(f"integrity check failed for {name}@{v}")
    dest = os.path.join(VENDOR, folder(name))
    with tarfile.open(fileobj=io.BytesIO(data)) as tar:
        for m in tar.getmembers():
            if not m.isfile():
                continue
            rel = m.name.split("/", 1)[1] if "/" in m.name else m.name
            if ".." in rel.split("/"):
                raise SystemExit(f"unsafe path in {name}: {m.name}")
            keep = rel == "package.json" or re.match(r"^(LICEN[CS]E|COPYING)", posixpath.basename(rel), re.I) \
                or (rel.endswith((".js", ".mjs")) and not rel.endswith((".cjs", ".d.ts")) and "/test" not in rel and not rel.startswith(("src/", "test")))
            if keep:
                out = os.path.join(dest, *rel.split("/"))
                os.makedirs(os.path.dirname(out), exist_ok=True)
                with open(out, "wb") as f:
                    f.write(tar.extractfile(m).read())
    pkgjson[name] = json.load(open(os.path.join(dest, "package.json"), encoding="utf-8"))
    record.append({"name": name, "version": v, "license": pkgjson[name].get("license"), "integrity": integrity,
                   "published": meta[name]["time"][v]})


# ----- CommonJS leaves (e.g. "extend", used by unified) ----------------------------------------
# A package that is CommonJS and requires nothing is wrapped as an ES module (__esm.js) that
# runs the original file unchanged and default-exports module.exports.
for name, pj in pkgjson.items():
    if pj.get("type") == "module" or pj.get("module") or pj.get("exports"):
        continue
    main = (pj.get("main") or "index.js").lstrip("./")
    main = main if main.endswith(".js") else main + ".js"
    src = os.path.join(VENDOR, folder(name), *main.split("/"))
    if not os.path.exists(src):
        continue
    code = open(src, encoding="utf-8").read()
    if "require(" in code or "module.exports" not in code:
        continue
    with open(os.path.join(VENDOR, folder(name), "__esm.js"), "w", encoding="utf-8") as f:
        f.write("// ES-module wrapper written by vendor_milkdown.py; the original file is unchanged below.\n"
                "const module = { exports: {} };\nconst exports = module.exports;\n"
                + code + "\nexport default module.exports;\n")
    pj["module"] = "./__esm.js"

# ----- resolve specifiers ------------------------------------------------------------------
def condition(target):
    if isinstance(target, str):
        return target
    if isinstance(target, list):
        return next((t for t in (condition(x) for x in target) if t), None)
    if isinstance(target, dict):
        for key in CONDITIONS:
            if key in target:
                r = condition(target[key])
                if r:
                    return r
    return None


def export_target(pj, sub):
    exports = pj.get("exports")
    if exports is None:
        if sub == ".":
            return pj.get("module") or pj.get("main") or "index.js"
        return sub[2:] if sub.endswith((".js", ".mjs")) else sub[2:] + ".js"
    if isinstance(exports, (str, list)) or not any(k.startswith(".") for k in exports):
        exports = {".": exports}
    if sub in exports:
        return condition(exports[sub])
    for key, target in exports.items():  # "./*" patterns
        if "*" in key:
            pre, post = key.split("*", 1)
            if sub.startswith(pre) and sub.endswith(post):
                star = sub[len(pre):len(sub) - len(post) if post else None]
                t = condition(target)
                return t.replace("*", star) if t else None
    return None


def resolve(spec, from_pkg):
    if spec.startswith("#"):
        imports = pkgjson[from_pkg].get("imports", {})
        t = condition(imports.get(spec)) if spec in imports else None
        return (from_pkg, t) if t and t.startswith(".") else None
    parts = spec.split("/")
    name = "/".join(parts[:2]) if spec.startswith("@") else parts[0]
    sub = "./" + "/".join(parts[2 if spec.startswith("@") else 1:]) if spec != name else "."
    if name not in pkgjson:
        return None
    t = export_target(pkgjson[name], sub)
    return (name, t) if t else None


IMPORT = re.compile(r"""(\bfrom\s*|\bimport\s*\(?\s*|\bexport\s*\*\s*from\s*)(["'])([^"'.\s][^"']*)\2""")
missing = set()
for name in chosen:
    root = os.path.join(VENDOR, folder(name))
    for dirpath, _, files in os.walk(root):
        for fn in files:
            if not fn.endswith((".js", ".mjs")):
                continue
            path = os.path.join(dirpath, fn)
            text = open(path, encoding="utf-8").read()

            def repl(m):
                spec = m.group(3)
                line_start = text.rfind("\n", 0, m.start()) + 1
                if text[line_start:m.start()].lstrip().startswith(("*", "//", "/*")):
                    return m.group(0)  # a JSDoc type import, not code
                if spec.startswith(("http:", "https:", "data:")):
                    missing.add(spec)
                    return m.group(0)
                r = resolve(spec, name)
                if not r:
                    missing.add(f"{spec} (in {name})")
                    return m.group(0)
                pkg, target = r
                target_path = os.path.normpath(os.path.join(VENDOR, folder(pkg), *target.lstrip("./").split("/")))
                rel = os.path.relpath(target_path, os.path.dirname(path)).replace("\\", "/")
                return m.group(1) + m.group(2) + (rel if rel.startswith(".") else "./" + rel) + m.group(2)

            new = IMPORT.sub(repl, text)
            if new != text:
                open(path, "w", encoding="utf-8", newline="").write(new)

with open(os.path.join(VENDOR, "versions.json"), "w", encoding="utf-8") as f:
    json.dump(record, f, ensure_ascii=False, indent=1)
licenses = sorted({str(r["license"]) for r in record})
print(f"vendored {len(record)} packages; licenses: {', '.join(licenses)}")
if missing:
    # Node-only files (node:fs …) are copied but never loaded by the page; the browser test shows
    # whether anything that is actually loaded is missing.
    print("not rewritten (check in the browser):", *sorted(missing)[:40], sep="\n  ")
