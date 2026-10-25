// Floating Memo — keeping the note's own Markdown when it is edited in the visual mode.
//
// The visual editor (Milkdown) writes the whole document back in its own style (bullets,
// indentation, table padding, blank lines). Saving that would change lines the user never
// touched. Instead:
// 1. A note opens in the visual mode only if what Milkdown writes for it means exactly the same
//    (same syntax tree, see sameContent): nothing can be lost or reinterpreted.
// 2. After an edit, only the lines that changed in Milkdown's output are put into the note;
//    every other line keeps its bytes (patch).
// 3. The result is accepted only if it means exactly what the visual editor shows; otherwise
//    the caller falls back to Milkdown's full text (the user's edit is never lost).
// Pure functions: no DOM changes, no network.

import { fromMarkdown } from './visual-vendor/mdast-util-from-markdown/index.js';
import { gfm } from './visual-vendor/micromark-extension-gfm/index.js';
import { gfmFromMarkdown } from './visual-vendor/mdast-util-gfm/index.js';
import { EditorState } from './vendor/codemirror-state/dist/index.js';
import { ensureSyntaxTree } from './vendor/codemirror-language/dist/index.js';
import { markdown, markdownLanguage } from './vendor/codemirror-lang-markdown/dist/index.js';
import { listItems } from './listdrag.js';

/** A Markdown text with its full syntax tree (for list items and their levels). */
export function markdownState(md) {
  const state = EditorState.create({ doc: md, extensions: [markdown({ base: markdownLanguage })] });
  ensureSyntaxTree(state, state.doc.length, 5000);
  return state;
}

// What a document says, without where it says it: node types, text, attributes.
const KEEP = ['type', 'value', 'depth', 'ordered', 'start', 'checked', 'url', 'title', 'alt', 'lang', 'meta', 'align', 'identifier', 'label', 'referenceType'];

function strip(node) {
  const out = {};
  for (const k of KEEP) if (node[k] !== undefined && node[k] !== null) out[k] = node[k];
  if (Array.isArray(node.children)) out.children = node.children.map(strip);
  return out;
}

export function contentOf(md) {
  return JSON.stringify(strip(fromMarkdown(md, { extensions: [gfm()], mdastExtensions: [gfmFromMarkdown()] })));
}

export const sameContent = (a, b) => contentOf(a) === contentOf(b);

// Lines compared without the parts the visual editor rewrites (indentation, trailing spaces,
// bullet characters, table padding), to find which line of the note is which line of its output.
function key(line) {
  let l = line.replace(/\r$/, '').trim().replace(/\\$/, '').trimEnd();   // a hard break: "\" or two spaces
  if (/^\|.*\|$/.test(l)) {
    l = l.split('|').map((c) => c.trim()).join('|');
    if (/^\|?(\s*:?-+:?\s*\|)+\s*:?-*:?\|?$/.test(l)) l = l.replace(/-+/g, '-');
  }
  return l.replace(/^[*+] /, '- ').replace(/^(\d+)[.)] /, '$1. ');
}

// Myers' diff: index pairs of equal lines. Gives up (null) beyond `limit` differences.
function matches(a, b, limit = 4000) {
  const n = a.length, m = b.length, max = n + m, off = max + 1;
  const v = new Int32Array(2 * max + 3);
  const trace = [];
  for (let d = 0; d <= Math.min(max, limit); d++) {
    trace.push(v.slice(off - d - 1, off + d + 2));
    for (let k = -d; k <= d; k += 2) {
      let x = (k === -d || (k !== d && v[off + k - 1] < v[off + k + 1])) ? v[off + k + 1] : v[off + k - 1] + 1;
      let y = x - k;
      while (x < n && y < m && a[x] === b[y]) { x++; y++; }
      v[off + k] = x;
      if (x >= n && y >= m) return backtrack(trace, n, m);
    }
  }
  return null;
}

function backtrack(trace, n, m) {
  const pairs = [];
  let x = n, y = m;
  for (let d = trace.length - 1; d >= 0; d--) {
    const t = trace[d];                         // v[k] for k in [-d-1, d+1] stored at k + d + 1
    const at = (k) => t[k + d + 1];
    const k = x - y;
    const prevK = (k === -d || (k !== d && at(k - 1) < at(k + 1))) ? k + 1 : k - 1;
    const prevX = d === 0 ? 0 : at(prevK);
    const prevY = prevX - prevK;
    while (x > prevX && y > prevY) { x--; y--; pairs.push([x, y]); }
    if (d > 0) { x = prevX; y = prevY; }
  }
  return pairs.reverse();
}

// Leading part (indentation, list marker, task box) and trailing part (hard break, spaces).
const LEAD = /^\s*(?:(?:[-*+]|\d+[.)])(?:\s+\[[ xX]\])?(?:\s+|$))?/;
const TRAIL = /(?:\|\s+)$/;

/**
 * One changed line: the visual editor's new text with the note's own indentation, marker and
 * line end kept (only when the visual editor wrote those parts the same way before and after).
 */
function editLine(o, a, b) {
  const cr = o.endsWith('\r') ? '\r' : '';
  if (cr) o = o.slice(0, -1);
  const la = LEAD.exec(a)[0], lo = LEAD.exec(o)[0], lb = LEAD.exec(b)[0];
  let body = b;
  let lead = '';
  if (lb === la) { lead = lo; body = b.slice(la.length); }
  const ta = (TRAIL.exec(a.slice(la.length)) || [''])[0], to = (TRAIL.exec(o.slice(lo.length)) || [''])[0];
  if (ta !== '' ? body.endsWith(ta) : body !== '' && !TRAIL.test(body)) {
    body = body.slice(0, body.length - ta.length) + to;
  }
  return lead + body + cr;
}

/**
 * One changed line, edited in place: the characters the visual editor changed (between its old
 * and new line) are changed at the matching place of the note's line; everything else on the
 * line (indentation, marker, table padding, line end) keeps its bytes. Null if the place is unclear.
 */
function inline(o, a, b) {
  let p = 0;
  while (p < a.length && p < b.length && a[p] === b[p]) p++;
  let s = 0;
  while (s < a.length - p && s < b.length - p && a[a.length - 1 - s] === b[b.length - 1 - s]) s++;
  const pairs = matches([...a], [...o], 400);
  if (!pairs) return null;
  let end = a.length - s;
  // Only padding replaced (a table column got wider): the new text goes in, the padding stays.
  const padding = /^\s*\|/.test(a) && end > p && a.slice(p, end).trim() === '' && b.slice(p, b.length - s).trim() !== '';
  if (padding) end = p;
  let oStart = 0, oEnd = o.length;
  for (const [ai, oi] of pairs) { if (ai < p) oStart = oi + 1; }
  for (const [ai, oi] of pairs) { if (ai >= end) { oEnd = oi; break; } }
  if (oStart > oEnd) return null;
  const added = padding ? b.slice(p, b.length - s).trim() : b.slice(p, b.length - s);
  return o.slice(0, oStart) + added + o.slice(oEnd);
}

// Two lines of the visual editor's own output that differ only in formatting (padding, bullet
// character, line end). Indentation counts: in a list it is the level.
const same = (a, b) => /^\s*/.exec(a)[0] === /^\s*/.exec(b)[0] && key(a) === key(b);

/** Changed runs of lines between two texts: [a0, a1, b0, b1]. */
function hunks(A, B) {
  const pairs = matches(A, B);
  if (!pairs) return null;
  const out = [];
  let i = 0, j = 0;
  for (const [x, y] of [...pairs, [A.length, B.length]]) {
    if (x > i || y > j) out.push([i, x, j, y]);
    i = x + 1; j = y + 1;
  }
  return out;
}

/**
 * The note after an edit in the visual mode, changing only what the edit changed.
 * original: the note's text; before / after: Milkdown's output before and after the edit.
 * Lines the visual editor only re-formatted keep the note's bytes. Returns null when the lines
 * cannot be matched up safely.
 */
export function patch(original, before, after, check = () => true) {
  if (before === after) return original;
  // First the closest to the note's own writing; each candidate must mean what the visual editor shows.
  for (const tidy of [true, false]) {
    const text = build(original, before, after, tidy);
    if (text !== null && check(text)) return text;
  }
  return null;
}

// Indentation of one list level in the note: a tab unless the note indents with spaces.
function unitOf(lines) {
  const items = listItems(markdownState(lines.join('\n')));
  const deeper = items.find((i) => i.level > 0 && i.indent.length > 0);
  return deeper ? deeper.indent.slice(0, Math.max(1, Math.round(deeper.indent.length / deeper.level))) : null;
}

function build(original, before, after, tidy) {
  const O = original.split('\n'), A = before.split('\n'), B = after.split('\n');
  const changes = hunks(A, B);
  const pairs = matches(O.map(key), A.map(key));
  if (!changes || !pairs) return null;
  const toO = new Map(pairs.map(([i, j]) => [j, i]));
  const crlf = original.includes('\r\n');
  const result = O.slice();
  let levels = null;   // list item levels of the visual editor's new text, by line (computed when needed)
  const unit = tidy ? unitOf(O) : null;
  // From the bottom up, so earlier line numbers stay valid.
  for (const [a0, a1, b0, b1] of changes.reverse()) {
    const formatOnly = a1 - a0 === b1 - b0 && A.slice(a0, a1).every((l, k) => same(l, B[b0 + k]));
    if (formatOnly) continue;
    if (a1 - a0 === b1 - b0 && A.slice(a0, a1).every((_, k) => toO.has(a0 + k))) {
      // The same lines, edited: each keeps its own indentation, marker and line end.
      for (let k = 0; k < a1 - a0; k++) {
        if (same(A[a0 + k], B[b0 + k])) continue;   // only re-formatted
        const n = toO.get(a0 + k);
        const cr = O[n].endsWith('\r') ? '\r' : '';
        const own = inline(cr ? O[n].slice(0, -1) : O[n], A[a0 + k], B[b0 + k]);
        let line = own !== null ? own : editLine(O[n], A[a0 + k], B[b0 + k]).replace(/\r$/, '');
        if (/^\s*/.exec(A[a0 + k])[0] !== /^\s*/.exec(B[b0 + k])[0] && unit !== null) {
          // The item changed level: indented the note's way for its new level.
          if (!levels) levels = new Map(listItems(markdownState(after)).map((i) => [i.firstLine - 1, i.level]));
          if (levels.has(b0 + k)) line = unit.repeat(levels.get(b0 + k)) + line.trimStart();
        }
        result[n] = line + cr;
      }
      continue;
    }
    // Lines added or removed: replaced between the nearest lines both texts share.
    let p = a0 - 1;
    while (p >= 0 && !toO.has(p)) p--;
    let q = a1;
    while (q < A.length && !toO.has(q)) q++;
    const op = p >= 0 ? toO.get(p) : -1;
    const oq = q < A.length ? toO.get(q) : O.length;
    if (op + 1 > oq) return null;
    let added = B.slice(b0, b1);
    let context = [A.slice(p + 1, a0), A.slice(a1, q)];
    if (tidy) {
      // New list items indented the note's way (a tab per level, say), not the visual editor's.
      if (unit !== null) {
        if (!levels) levels = new Map(listItems(markdownState(after)).map((i) => [i.firstLine - 1, i.level]));
        added = added.map((l, k) => (levels.has(b0 + k) ? unit.repeat(levels.get(b0 + k)) + l.trimStart() : l));
      }
      // Blank lines the visual editor puts between list items are not the note's.
      context = context.map((c) => c.filter((l) => l.trim() !== ''));
      added = added.filter((l, k) => !(l.trim() === '' && levels && (levels.has(b0 + k - 1) || /^\s*([-*+]|\d+[.)])\s/.test(added[k - 1] || '')) && /^\s*([-*+]|\d+[.)])\s/.test(added[k + 1] || B[b1] || '')));
    }
    const fresh = [...context[0], ...added, ...context[1]].map((l) => (crlf && l !== '' ? `${l}\r` : l));
    result.splice(op + 1, oq - op - 1, ...fresh);
  }
  return result.join('\n');
}
