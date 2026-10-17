// Floating Memo — visual (WYSIWYG) Markdown editing with Milkdown (ProseMirror). Experimental;
// the source editor (editor.html, CodeMirror) stays the default.
//
// Safety rules for the Markdown files:
// - Opening a note never changes it: the text is reported to the host only after the user
//   edits; until then the host keeps the file exactly as it was.
// - A note whose Markdown would be changed by a visual round trip (beyond spacing, bullet
//   markers and table padding) is shown in a plain source text area instead, with a notice.
// - Same host protocol as editor.js (load / insert / flush / focus / theme / forget, and
//   change / caret / flushed / composition / command / insertRejected / ready back).
// No network, no eval, no innerHTML.

import { Editor, rootCtx, defaultValueCtx, editorViewCtx, parserCtx, remarkStringifyOptionsCtx } from './visual-vendor/milkdown-core/lib/index.js';
import { commonmark } from './visual-vendor/milkdown-preset-commonmark/lib/index.js';
import { gfm } from './visual-vendor/milkdown-preset-gfm/lib/index.js';
import { history } from './visual-vendor/milkdown-plugin-history/lib/index.js';
import { listener, listenerCtx } from './visual-vendor/milkdown-plugin-listener/lib/index.js';
import { clipboard } from './visual-vendor/milkdown-plugin-clipboard/lib/index.js';
import { getMarkdown, replaceAll } from './visual-vendor/milkdown-utils/lib/index.js';

const host = window.chrome && window.chrome.webview;
const sent = [];
const post = (message) => { if (host) host.postMessage(message); else sent.push(message); };

const visual = document.getElementById('visual');
const source = document.getElementById('source');
const notice = document.getElementById('notice');

let currentId = null;
let original = '';      // the note as it was opened
let text = '';          // what the host should save (original until the user edits)
let mode = 'visual';    // or 'source' for notes the visual editor would change
let loading = false;    // replaceAll() while opening a note is not an edit
let composing = false;
let pendingInsert = null;

const editor = await Editor.make()
  .config((ctx) => {
    ctx.set(rootCtx, visual);
    ctx.set(defaultValueCtx, '');
    // Written like the app writes Markdown: "-" bullets, tables not re-padded, [[links]] literal.
    ctx.update(remarkStringifyOptionsCtx, (prev) => ({
      ...prev,
      bullet: '-',
      tablePipeAlign: false,
      handlers: {
        ...prev.handlers,
        text: (node, parent, state, info) => state.safe(node.value, info).replace(/\\\[\\\[([^\]\n]+)\]\]/g, '[[$1]]'),
      },
    }));
    ctx.get(listenerCtx).markdownUpdated((_, md) => {
      if (loading || mode !== 'visual' || currentId === null) return;
      text = md;
      post({ type: 'change', id: currentId, text, caret: text.length });
    });
  })
  .use(commonmark).use(gfm).use(history).use(listener).use(clipboard)
  .create();

const view = () => editor.action((ctx) => ctx.get(editorViewCtx));

// ----- Round-trip check ---------------------------------------------------------------------

// Differences that do not change what the note says: spacing, bullet markers, table padding.
function canonical(md) {
  return md.replace(/\r\n/g, '\n')
    .split('\n')
    .map((line) => {
      let l = line.replace(/\s+$/, '');
      if (/^\s*\|.*\|\s*$/.test(l)) {
        l = l.split('|').map((cell) => cell.trim()).join('|');
        if (/^\|?(\s*:?-+:?\s*\|)+\s*:?-*:?\|?$/.test(l)) l = l.replace(/-+/g, '-');
      }
      return l.replace(/^(\s*)[*+] /, '$1- ');
    })
    .join('\n')
    .replace(/\n{3,}/g, '\n\n')
    .trim();
}

function roundTrip(md) {
  loading = true;
  try {
    editor.action(replaceAll(md, true));
    return editor.action(getMarkdown());
  } finally {
    loading = false;
  }
}

// ----- Notes ---------------------------------------------------------------------------------

function showSource(md, why) {
  mode = 'source';
  visual.hidden = true;
  source.hidden = false;
  source.value = md;
  notice.hidden = false;
  notice.textContent = why;
}

function showVisual() {
  mode = 'visual';
  source.hidden = true;
  visual.hidden = false;
  notice.hidden = true;
}

function load(id, md, isReadOnly) {
  if (pendingInsert !== null && currentId !== null) {
    const block = pendingInsert;
    pendingInsert = null;
    insertNow(block); // a transcript waiting for the IME belongs to the note being left
  }
  currentId = id;
  original = md;
  text = md;
  const out = roundTrip(md);
  if (canonical(out) !== canonical(md)) {
    showSource(md, 'このノートにはビジュアル編集で形が変わる記法があるため、Markdown のまま表示しています（内容は変わりません）。');
  } else {
    showVisual();
  }
  source.readOnly = isReadOnly;
  view().setProps({ editable: () => !isReadOnly });
}

// The transcript block goes to the end of the note after one empty line (like editor.js).
// In the visual editor it is appended to the document itself, so text typed a moment ago (not
// yet reported: change notifications are debounced) is kept and the caret stays where it was.
function insertNow(block) {
  if (mode === 'visual') {
    editor.action((ctx) => {
      const v = ctx.get(editorViewCtx);
      const parsed = ctx.get(parserCtx)(block);
      v.dispatch(v.state.tr.insert(v.state.doc.content.size, parsed.content));
    });
    text = editor.action(getMarkdown());
  } else {
    const base = source.value.replace(/\n*$/, '');
    text = (base.length === 0 ? '' : base + '\n\n') + block;
    source.value = text;
  }
  post({ type: 'change', id: currentId, text, caret: text.length });
}

function insert(block) {
  if (composing) {
    pendingInsert = pendingInsert === null ? block : pendingInsert + '\n\n' + block;
    return;
  }
  insertNow(block);
}

source.addEventListener('input', () => {
  if (currentId === null || mode !== 'source') return;
  text = source.value;
  post({ type: 'change', id: currentId, text, caret: source.selectionStart });
});

// ----- IME, keys, checkboxes, focus ------------------------------------------------------------

for (const el of [visual, source]) {
  el.addEventListener('compositionstart', () => { composing = true; post({ type: 'composition', active: true }); });
  el.addEventListener('compositionend', () => {
    composing = false;
    post({ type: 'composition', active: false });
    if (pendingInsert !== null) {
      const block = pendingInsert;
      pendingInsert = null;
      setTimeout(() => insertNow(block), 0);
    }
  });
}

// App shortcuts go to the host (never while composing), as in editor.js.
const commands = [
  ['Escape', false, false, 'hide'], ['n', true, false, 'new'], ['o', true, false, 'open'], ['e', true, true, 'export'],
  ['w', true, false, 'close'], ['s', true, false, 'save'], ['r', true, false, 'record'], ['Tab', true, false, 'next'],
  ['Tab', true, true, 'previous'], ['t', true, true, 'reopen'], ['F2', false, false, 'rename'], ['F6', false, false, 'focusOut'],
];
document.addEventListener('keydown', (e) => {
  if (composing || e.isComposing) return;
  const key = e.key.length === 1 ? e.key.toLowerCase() : e.key;
  const hit = commands.find(([k, ctrl, shift]) => k === key && ctrl === e.ctrlKey && shift === e.shiftKey && !e.altKey);
  if (hit) {
    e.preventDefault();
    e.stopPropagation();
    post({ type: 'command', name: hit[3] });
  }
}, true);

// Clicking the box of a task item toggles "[ ]" / "[x]".
visual.addEventListener('mousedown', (e) => {
  const li = e.target.closest && e.target.closest('li[data-item-type="task"]');
  if (!li || e.offsetX > 0) return; // only the box drawn left of the item
  e.preventDefault();
  const v = view();
  const pos = v.posAtDOM(li, 0) - 1;
  const node = v.state.doc.nodeAt(pos);
  if (node && node.type.name === 'list_item') {
    v.dispatch(v.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, checked: !node.attrs.checked }));
  }
});

window.addEventListener('focus', () => focusEditor());
// The host tracks whether the editor has keyboard focus (as with editor.js).
document.addEventListener('focusin', () => post({ type: 'focus', focused: true }));
document.addEventListener('focusout', (e) => { if (!e.relatedTarget) post({ type: 'focus', focused: false }); });
function focusEditor() { if (mode === 'visual') view().focus(); else source.focus(); }

document.addEventListener('click', (e) => { if (e.target.closest && e.target.closest('a')) e.preventDefault(); }, true);
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => { if (e.dataTransfer && e.dataTransfer.files.length) e.preventDefault(); });

// ----- Host messages ---------------------------------------------------------------------------

const isId = (v) => typeof v === 'string' && v.length > 0 && v.length <= 64;
const isText = (v) => typeof v === 'string' && v.length <= 20_000_000;

function handleHost(message) {
  if (!message || typeof message !== 'object') return;
  switch (message.type) {
    case 'load':
      if (isId(message.id) && isText(message.text)) load(message.id, message.text, message.readOnly === true);
      break;
    case 'insert':
      if (!isText(message.text)) break;
      if (message.id === currentId) insert(message.text);
      else post({ type: 'insertRejected', id: message.id, text: message.text });
      break;
    case 'focus':
      focusEditor();
      break;
    case 'theme':
      if (message.theme === 'light' || message.theme === 'dark') document.documentElement.dataset.theme = message.theme;
      else delete document.documentElement.dataset.theme;
      if (typeof message.paper === 'string' && /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(message.paper)) document.documentElement.style.setProperty('--paper', message.paper);
      else document.documentElement.style.removeProperty('--paper');
      post({ type: 'caretStyle', color: getComputedStyle(document.documentElement).getPropertyValue('--caret').trim() });
      break;
    case 'flush':
    {
      // Change notifications are debounced: read the document now. A note whose content did not
      // change keeps its text byte for byte (the original when it was never edited).
      if (mode === 'visual' && currentId !== null) {
        const now = editor.action(getMarkdown());
        if (canonical(now) !== canonical(text)) text = now;
      }
      post({ type: 'flushed', id: currentId, text, caret: text.length });
      break;
    }
    default:
      break; // 'forget' and unknown messages: nothing to keep per note here
  }
}

if (host) {
  host.addEventListener('message', (e) => handleHost(e.data));
} else {
  // Opened in a normal browser (development / tests): a handle to drive and inspect the page.
  window.floatingMemoVisual = { handleHost, sent, canonical, get text() { return text; }, get mode() { return mode; }, view };
}

post({ type: 'ready' });
