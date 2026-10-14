<#
.SYNOPSIS
    Vendors CodeMirror 6 (the Markdown editor) into src\FloatingMemo\Editor\vendor\.

.DESCRIPTION
    Development-time only (uses the network); the app itself loads these files from disk.
    No Node.js/bundler needed:
      1. resolves the dependency tree from the npm registry, choosing for each package the
         newest stable version published at least -MinAgeDays ago (supply-chain caution);
      2. downloads each tarball and verifies the registry's sha512 integrity hash;
      3. copies the ES-module sources plus LICENSE;
      4. rewrites bare imports ("@codemirror/state") to relative paths so the browser can load
         them as plain ES modules with a strict Content-Security-Policy (no import map, no CDN).
    Writes vendor\versions.json (name, version, license, integrity) for the record.
#>
param([int]$MinAgeDays = 14)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$roots = @('@codemirror/state', '@codemirror/view', '@codemirror/commands', '@codemirror/language',
           '@codemirror/lang-markdown', '@lezer/markdown', '@lezer/highlight')
$repo = Split-Path $PSScriptRoot -Parent
$vendor = Join-Path $repo 'src\FloatingMemo\Editor\vendor'
$cutoff = (Get-Date).ToUniversalTime().AddDays(-$MinAgeDays)

function Parse-Version([string]$v) { $p = ($v -split '[-+]')[0].Split('.'); [version]::new([int]$p[0], [int]$p[1], [int]$p[2]) }
function Satisfies([string]$version, [string]$range) {
    $v = Parse-Version $version
    if ($range -match '^\^(\d+)\.(\d+)\.(\d+)$') {
        $min = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
        if ($min.Major -gt 0) { return $v -ge $min -and $v.Major -eq $min.Major }
        return $v -ge $min -and $v.Major -eq 0 -and $v.Minor -eq $min.Minor
    }
    throw "Unsupported version range '$range'"
}

# --- 1. Resolve ------------------------------------------------------------------------
$meta = @{}; $chosen = [ordered]@{}; $ranges = @{}
$queue = [System.Collections.Generic.Queue[string]]::new(); $roots | ForEach-Object { $queue.Enqueue($_) }
while ($queue.Count) {
    $name = $queue.Dequeue()
    if ($chosen.Contains($name)) { continue }
    $doc = Invoke-RestMethod "https://registry.npmjs.org/$($name -replace '/', '%2F')" -UseBasicParsing
    $meta[$name] = $doc
    $candidates = $doc.versions.PSObject.Properties.Name |
        Where-Object { $_ -notmatch '-' -and [datetime]$doc.time.$_ -le $cutoff } |
        Sort-Object { Parse-Version $_ } -Descending
    if (-not $candidates) { throw "No version of $name is older than $MinAgeDays days" }
    $version = $candidates[0]
    $chosen[$name] = $version
    $deps = $doc.versions.$version.dependencies
    if ($deps) {
        foreach ($d in $deps.PSObject.Properties) {
            $ranges[$d.Name] = @($ranges[$d.Name]) + "$($d.Value) (from $name)"
            $queue.Enqueue($d.Name)
        }
    }
}
foreach ($name in $ranges.Keys) {
    foreach ($r in $ranges[$name] | Where-Object { $_ }) {
        $range = ($r -split ' ')[0]
        if (-not (Satisfies $chosen[$name] $range)) { throw "$name@$($chosen[$name]) does not satisfy $r" }
    }
}

# --- 2-4. Download, verify, copy, rewrite -------------------------------------------------
$work = Join-Path ([IO.Path]::GetTempPath()) ("fm-vendor-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $work | Out-Null
if (Test-Path $vendor) { Remove-Item $vendor -Recurse -Force }
New-Item -ItemType Directory -Force $vendor | Out-Null
$entries = @{}; $record = @()
# Folder names without '@' or '/': MSBuild escapes '@' (as %40), which breaks MSIX packaging.
function Folder([string]$name) { $name.TrimStart('@').Replace('/', '-') }
try {
    foreach ($name in $chosen.Keys) {
        $version = $chosen[$name]
        $info = $meta[$name].versions.$version
        $tgz = Join-Path $work (($name -replace '[@/]', '_') + '.tgz')
        Invoke-WebRequest $info.dist.tarball -OutFile $tgz -UseBasicParsing
        $integrity = $info.dist.integrity
        if ($integrity -notmatch '^sha512-(.+)$') { throw "No sha512 integrity for $name" }
        $sha = [Convert]::ToBase64String([Security.Cryptography.SHA512]::Create().ComputeHash([IO.File]::ReadAllBytes($tgz)))
        if ($sha -ne $Matches[1]) { throw "Integrity check failed for $name@$version" }

        $x = Join-Path $work ($name -replace '[@/]', '_')
        New-Item -ItemType Directory $x | Out-Null
        tar -xzf $tgz -C $x
        $pkg = Join-Path $x 'package'
        $pj = Get-Content (Join-Path $pkg 'package.json') -Raw | ConvertFrom-Json

        # ES-module entry point: exports["."].import, then "module", then "main".
        $entry = $null
        if ($pj.exports) {
            $dot = if ($pj.exports.PSObject.Properties['.']) { $pj.exports.'.' } else { $pj.exports }
            if ($dot -is [string]) { $entry = $dot } elseif ($dot.import) { $entry = if ($dot.import -is [string]) { $dot.import } else { $dot.import.default } }
        }
        if (-not $entry) { $entry = if ($pj.module) { $pj.module } else { $pj.main } }
        $entry = $entry.TrimStart('.', '/')

        $dest = Join-Path $vendor (Folder $name)
        New-Item -ItemType Directory -Force $dest | Out-Null
        $entryDir = Split-Path $entry -Parent
        if ($entryDir) {
            Copy-Item (Join-Path $pkg $entryDir) (Join-Path $dest $entryDir) -Recurse
            Get-ChildItem (Join-Path $dest $entryDir) -Recurse -File | Where-Object { $_.Extension -notin '.js', '.mjs' } | Remove-Item
        }
        else {
            # Entry at the package root (crelt, w3c-keyname): copy only that file, not build configs.
            Copy-Item (Join-Path $pkg $entry) $dest
        }
        Get-ChildItem $pkg -File | Where-Object { $_.Name -match '^(LICEN[CS]E|COPYING)' } | Copy-Item -Destination $dest
        $entries[$name] = $entry
        $record += [ordered]@{ name = $name; version = $version; license = $pj.license; integrity = $integrity; published = $meta[$name].time.$version }
    }

    # Bare specifiers -> relative paths to the vendored entry files.
    foreach ($file in Get-ChildItem $vendor -Recurse -Include *.js, *.mjs) {
        $text = [IO.File]::ReadAllText($file.FullName)
        $rewritten = [regex]::Replace($text, '(\bfrom\s*|\bimport\s*\(?\s*)(["''])(@?[A-Za-z0-9_.\-]+(?:/[A-Za-z0-9_.\-]+)?)\2', {
            param($m)
            $spec = $m.Groups[3].Value
            if (-not $entries.ContainsKey($spec)) { return $m.Value }
            $target = Join-Path (Join-Path $vendor (Folder $spec)) $entries[$spec]
            # (Windows PowerShell 5.1 has no Path.GetRelativePath.)
            $from = [Uri]::new($file.DirectoryName.TrimEnd('\') + '\')
            $rel = [Uri]::UnescapeDataString($from.MakeRelativeUri([Uri]::new($target)).ToString())
            if (-not $rel.StartsWith('.')) { $rel = './' + $rel }
            $m.Groups[1].Value + $m.Groups[2].Value + $rel + $m.Groups[2].Value
        })
        # Only real import/export statements (line start), not mentions in comments.
        $unresolved = [regex]::Matches($rewritten, '(?m)^\s*(?:import|export)\b[^;]*?\bfrom\s*["''](@?[A-Za-z][^"''./][^"'']*)["'']') | ForEach-Object { $_.Groups[1].Value }
        if ($unresolved) { throw "Unresolved import(s) in $($file.FullName): $($unresolved -join ', ')" }
        [IO.File]::WriteAllText($file.FullName, $rewritten, (New-Object Text.UTF8Encoding $false))
    }

    $record | ConvertTo-Json | Set-Content (Join-Path $vendor 'versions.json') -Encoding UTF8
    $record | ForEach-Object { "{0,-30} {1,-9} {2}" -f $_.name, $_.version, $_.license }
    "Vendored $($record.Count) packages into $vendor"
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
