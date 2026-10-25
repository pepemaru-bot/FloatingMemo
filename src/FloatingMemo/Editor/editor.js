// Floating Memo — Markdown editor (CodeMirror 6) with Obsidian-style live preview.
//
// The document is always the plain Markdown text: live preview only *decorates* the view
// (hides markup on lines the cursor is not on, draws checkboxes and bullets). Nothing is
// converted to HTML and back, so syntax this editor does not understand is never altered.
//
// Talks to the C# host through window.chrome.webview messages only (see handleHost()).
// No network, no eval, no innerHTML.

import { EditorState, EditorSelection, Compartment, Prec, StateField, StateEffect } from './vendor/codemirror-state/dist/index.js';
import { EditorView, keymap, Decoration, ViewPlugin, WidgetType, drawSelection, placeholder } from './vendor/codemirror-view/dist/index.js';
import { history, historyKeymap, defaultKeymap, indentMore, indentLess, insertTab, isolateHistory } from './vendor/codemirror-commands/dist/index.js';
import { syntaxTree, indentUnit } from './vendor/codemirror-language/dist/index.js';
import { markdown, markdownLanguage, insertNewlineContinueMarkup } from './vendor/codemirror-lang-markdown/dist/index.js';
import { listDrag, listItems, moveSpec } from './listdrag.js';
import { renderPreview } from './preview.js';
import { patch, sameContent } from './mdpatch.js';

const host = window.chrome && window.chrome.webview;
const sent = [];   // without the app (development / tests in a browser): what would have been sent
let lastCommand = null;   // tests: the last app command sent (its name only)
const post = (message) => { if (message.type === 'command' || message.type === 'mode') lastCommand = `${message.type}:${message.name || message.mode}${message.refused ? `!${message.refused}` : ''}`; if (host) host.postMessage(message); else if (sent.push(message) > 200) sent.shift(); };

// ----- Live preview ----------------------------------------------------------------------

class BulletWidget extends WidgetType {
  eq() { return true; }
  toDOM() {
    const span = document.createElement('span');
    span.className = 'cm-bullet';
    span.textContent = '•';
    span.setAttribute('aria-hidden', 'true');
    return span;
  }
}

class HrWidget extends WidgetType {
  eq() { return true; }
  toDOM() {
    const hr = document.createElement('span');
    hr.className = 'cm-hr';
    hr.setAttribute('role', 'separator');
    return hr;
  }
}

// A real checkbox for "- [ ]" / "- [x]". Clicking edits the Markdown ("[ ]" <-> "[x]"),
// so the state is stored in the .md file, and the edit is undoable like any other.
class CheckboxWidget extends WidgetType {
  constructor(checked, markerFrom) {
    super();
    this.checked = checked;
    this.markerFrom = markerFrom;
  }
  eq(other) { return other.checked === this.checked && other.markerFrom === this.markerFrom; }
  toDOM(view) {
    const box = document.createElement('input');
    box.type = 'checkbox';
    box.className = 'cm-task-checkbox';
    box.checked = this.checked;
    box.setAttribute('aria-label', this.checked ? '完了したタスク' : '未完了のタスク');
    box.addEventListener('mousedown', (e) => e.preventDefault()); // keep the caret where it is
    box.addEventListener('click', (e) => {
      e.preventDefault();
      const from = this.markerFrom + 1; // the character between "[" and "]"
      const current = view.state.sliceDoc(from, from + 1);
      view.dispatch({ changes: { from, to: from + 1, insert: current === ' ' ? 'x' : ' ' }, userEvent: 'input.task' });
    });
    return box;
  }
  ignoreEvent() { return false; }
}

const hide = Decoration.replace({});
const lineClass = (cls) => Decoration.line({ class: cls });
const markClass = (cls) => Decoration.mark({ class: cls });

function activeLines(view) {
  // Markup stays visible on lines with the cursor/selection so it can be edited normally.
  const lines = new Set();
  if (!view.hasFocus) return lines;
  for (const range of view.state.selection.ranges) {
    const first = view.state.doc.lineAt(range.from).number;
    const last = view.state.doc.lineAt(range.to).number;
    for (let n = first; n <= last; n++) lines.add(n);
  }
  return lines;
}

function buildDecorations(view) {
  const doc = view.state.doc;
  const active = activeLines(view);
  const isActive = (pos) => active.has(doc.lineAt(pos).number);
  const decos = [];
  const add = (from, to, deco) => { if (from <= to) decos.push(deco.range(from, to)); };
  const addLine = (pos, cls) => decos.push(lineClass(cls).range(doc.lineAt(pos).from));
  const forEachLine = (from, to, cls) => {
    for (let pos = from; pos <= to;) {
      const line = doc.lineAt(pos);
      decos.push(lineClass(cls).range(line.from));
      pos = line.to + 1;
    }
  };

  for (const { from, to } of view.visibleRanges) {
    syntaxTree(view.state).iterate({
      from, to,
      enter: (node) => {
        const name = node.name;
        const heading = /^(?:ATXHeading|SetextHeading)(\d)$/.exec(name);
        if (heading) {
          addLine(node.from, `cm-h${heading[1]}`);
          return;
        }

        switch (name) {
          case 'HeaderMark': {
            // "## " — hide the marks and the space after them.
            if (!isActive(node.from) && doc.lineAt(node.from).from === node.from) {
              const next = doc.sliceString(node.to, node.to + 1) === ' ' ? node.to + 1 : node.to;
              add(node.from, next, hide);
            }
            break;
          }
          case 'StrongEmphasis': add(node.from, node.to, markClass('cm-strong')); break;
          case 'Emphasis': add(node.from, node.to, markClass('cm-em')); break;
          case 'Strikethrough': add(node.from, node.to, markClass('cm-strike')); break;
          case 'EmphasisMark':
          case 'StrikethroughMark':
            if (!isActive(node.from)) add(node.from, node.to, hide);
            break;
          case 'InlineCode': add(node.from, node.to, markClass('cm-inline-code')); break;
          case 'CodeMark': {
            const parent = node.node.parent;
            if (parent && parent.name === 'InlineCode') {
              if (!isActive(node.from)) add(node.from, node.to, hide);
            } else {
              add(node.from, node.to, markClass('cm-code-fence'));
            }
            break;
          }
          case 'FencedCode':
          case 'CodeBlock':
            forEachLine(node.from, node.to, 'cm-codeblock');
            break;
          case 'CodeInfo': add(node.from, node.to, markClass('cm-code-fence')); break;
          case 'Blockquote': forEachLine(node.from, node.to, 'cm-quote'); break;
          case 'QuoteMark':
            if (!isActive(node.from)) {
              const next = doc.sliceString(node.to, node.to + 1) === ' ' ? node.to + 1 : node.to;
              add(node.from, next, hide);
            }
            break;
          case 'HorizontalRule':
            if (!isActive(node.from)) add(node.from, node.to, Decoration.replace({ widget: new HrWidget() }));
            break;
          case 'ListMark': {
            const item = node.node.parent;
            const list = item && item.parent;
            const hasTask = item && item.getChild('Task');
            if (list && list.name === 'OrderedList') {
              add(node.from, node.to, markClass('cm-list-number'));
            } else if (!isActive(node.from)) {
              // Tasks show only their checkbox; plain items get a bullet.
              add(node.from, node.to, hasTask ? hide : Decoration.replace({ widget: new BulletWidget() }));
            }
            break;
          }
          case 'TaskMarker': {
            const marker = doc.sliceString(node.from, node.to);
            const checked = /\[[xX]\]/.test(marker);
            const selection = view.state.selection.main;
            const caretInside = view.hasFocus && selection.from < node.to && selection.to > node.from;
            if (!caretInside) {
              add(node.from, node.to, Decoration.replace({ widget: new CheckboxWidget(checked, node.from) }));
            }
            if (checked) add(node.to, doc.lineAt(node.from).to, markClass('cm-task-done'));
            break;
          }
          case 'Link': {
            if (isActive(node.from)) {
              add(node.from, node.to, markClass('cm-link'));
              break;
            }
            // [label](url): show only the label, styled as a link (not clickable: no navigation).
            const marks = node.node.getChildren('LinkMark');
            const url = node.node.getChild('URL');
            if (marks.length >= 2) {
              add(marks[0].from, marks[0].to, hide);
              add(marks[0].to, marks[1].from, markClass('cm-link'));
              add(marks[1].from, node.to, hide);
            }
            else if (url) add(url.from, url.to, markClass('cm-url'));
            return false;
          }
          case 'URL': add(node.from, node.to, markClass('cm-url')); break;
          default: break;
        }
      },
    });
  }

  return Decoration.set(decos, true);
}

const livePreview = ViewPlugin.fromClass(class {
  constructor(view) { this.decorations = buildDecorations(view); }
  update(update) {
    if (update.docChanged || update.viewportChanged || update.selectionSet || update.focusChanged
        || syntaxTree(update.startState) !== syntaxTree(update.state)) {
      this.decorations = buildDecorations(update.view);
    }
  }
}, { decorations: (plugin) => plugin.decorations });

// ----- Editing commands ------------------------------------------------------------------

// Ctrl+B / Ctrl+I: wrap the selection in ** / *, or unwrap if it is already wrapped.
function toggleWrap(marker) {
  return (view) => {
    if (view.composing) return false;
    const n = marker.length;
    view.dispatch(view.state.changeByRange((range) => {
      const doc = view.state.doc;
      const before = doc.sliceString(range.from - n, range.from);
      const after = doc.sliceString(range.to, range.to + n);
      if (before === marker && after === marker) {
        return {
          changes: [{ from: range.from - n, to: range.from }, { from: range.to, to: range.to + n }],
          range: EditorSelection.range(range.from - n, range.to - n),
        };
      }
      return {
        changes: [{ from: range.from, insert: marker }, { from: range.to, insert: marker }],
        range: EditorSelection.range(range.from + n, range.to + n),
      };
    }), { userEvent: 'input.format' });
    return true;
  };
}

function inListItem(state) {
  const pos = state.selection.main.head;
  for (let node = syntaxTree(state).resolveInner(pos, -1); node; node = node.parent) {
    if (node.name === 'ListItem') return true;
  }
  return false;
}

// Tab indents list items and selections; elsewhere it inserts a tab character.
// (Keyboard users can leave the editor with F6 or Ctrl+M, see below.)
function smartTab(view) {
  if (view.composing) return false;
  const { state } = view;
  const multiLine = state.selection.ranges.some((r) => state.doc.lineAt(r.from).number !== state.doc.lineAt(r.to).number);
  if (multiLine || inListItem(state)) return indentMore(view);
  return insertTab(view);
}

function smartShiftTab(view) {
  if (view.composing) return false;
  return indentLess(view);
}

// Enter on an empty list item ends the list (nested items move out one level first),
// like Obsidian. Other Enter presses fall through to CodeMirror's list continuation.
const EMPTY_ITEM = /^(\s*)(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s*)?$/;
function endEmptyListItem(view) {
  if (view.composing) return false;
  const { state } = view;
  const range = state.selection.main;
  if (!range.empty) return false;
  const line = state.doc.lineAt(range.head);
  const match = EMPTY_ITEM.exec(line.text);
  if (!match || range.head !== line.to) return false;
  if (match[1].length > 0) return indentLess(view);
  view.dispatch({ changes: { from: line.from, to: line.to }, userEvent: 'delete.list' });
  return true;
}

// Enter in Markdown markup (lists, quotes) continues it on the next line. CodeMirror's own
// command also inserts an empty line first when the list is "loose" (items separated by blank
// lines), which looked like a random extra blank line. Here one Enter is always one line break.
function continueMarkup(view) {
  if (view.composing) return false;
  let proposed = null;
  if (!insertNewlineContinueMarkup({ state: view.state, dispatch: (tr) => { proposed = tr; } }) || !proposed) return false;
  const changes = [];
  let removedBlank = false;
  proposed.changes.iterChanges((fromA, toA, _fromB, _toB, inserted) => {
    let text = inserted.toString();
    const blank = /^\n[ \t>]*(?=\n)/.exec(text);   // line break + empty continuation line + line break…
    if (blank) {
      text = text.slice(blank[0].length);
      removedBlank = true;
    }
    changes.push({ from: fromA, to: toA, insert: text });
  });
  if (!removedBlank) {
    view.dispatch(proposed);
    return true;
  }
  const set = view.state.changes(changes);
  view.dispatch({
    changes: set,
    selection: EditorSelection.cursor(set.mapPos(view.state.selection.main.head, 1)),
    scrollIntoView: true,
    userEvent: 'input',
  });
  return true;
}

// Japanese IME: the Enter that confirms a conversion must not also start a new line. Browsers
// normally mark that key press as part of the composition, but some IME/Chromium combinations
// deliver it again as a plain Enter right after the composition ends. Nobody presses Enter
// twice within 60 ms, so an Enter that close to the end of a composition is swallowed.
let compositionEndedAt = -Infinity;
function justConfirmedIme() {
  return performance.now() - compositionEndedAt < 60;
}

// App-level shortcuts are forwarded to the host. Never while the IME is composing.
function command(name) {
  return (view) => {
    if (view.composing) return false;
    post({ type: 'command', name });
    return true;
  };
}

// ----- Brackets: typing an opening bracket adds its closing one -----------------------------
// Only pairs the editor inserted itself are treated as pairs: typing the closing bracket steps
// over it, Backspace between them deletes both. A closing bracket that was already in the note
// is never removed. Brackets confirmed with the Japanese IME get the same treatment once the
// composition has ended (never while composing, so nothing is inserted twice).
const PAIRS = { '(': ')', '[': ']', '{': '}', '「': '」', '『': '』', '（': '）', '【': '】', '〈': '〉', '《': '》' };
const CLOSERS = new Set(Object.values(PAIRS));
const addPair = StateEffect.define({ map: (pos, change) => change.mapPos(pos) });
const dropPair = StateEffect.define({ map: (pos, change) => change.mapPos(pos) });
// Positions of closing brackets the editor inserted (mapped through every edit).
const autoPairs = StateField.define({
  create: () => [],
  update(pairs, tr) {
    let next = tr.docChanged ? pairs.map((p) => tr.changes.mapPos(p, 1)) : pairs;
    for (const e of tr.effects) {
      if (e.is(addPair)) next = [...next, e.value];
      if (e.is(dropPair)) next = next.filter((p) => p !== e.value);
    }
    // A pair whose closing bracket was deleted or typed over is forgotten.
    return next.filter((p) => p < tr.state.doc.length && CLOSERS.has(tr.state.doc.sliceString(p, p + 1)));
  },
});

// Letters, digits, kana and kanji right after the cursor: no closing bracket (it would get in the way).
const WORD_AFTER = /[\p{L}\p{N}]/u;

function charAt(state, pos) { return pos >= 0 && pos < state.doc.length ? state.doc.sliceString(pos, pos + 1) : ''; }

const bracketInput = EditorView.inputHandler.of((view, from, to, text) => {
  if (view.composing || view.compositionStarted || view.state.readOnly) return false;
  const { state } = view;
  if (PAIRS[text] && state.selection.ranges.length === 1) {
    const close = PAIRS[text];
    if (from !== to) {
      // Selected text is wrapped; the text stays selected.
      view.dispatch({
        changes: [{ from, insert: text }, { from: to, insert: close }],
        selection: EditorSelection.range(from + 1, to + 1),
        userEvent: 'input.type',
      });
      return true;
    }
    if (WORD_AFTER.test(charAt(state, from))) return false;
    view.dispatch({
      changes: { from, insert: text + close },
      selection: EditorSelection.cursor(from + 1),
      effects: addPair.of(from + 1),
      userEvent: 'input.type',
    });
    return true;
  }
  if (CLOSERS.has(text) && from === to && charAt(state, from) === text && state.field(autoPairs).includes(from)) {
    // Typing the closing bracket the editor added: step over it instead of adding another.
    view.dispatch({ selection: EditorSelection.cursor(from + 1), effects: dropPair.of(from), userEvent: 'select' });
    return true;
  }
  return false;
});

function deleteAutoPair(view) {
  const { state } = view;
  const sel = state.selection.main;
  if (!sel.empty || view.composing) return false;
  const pos = sel.head;
  const open = charAt(state, pos - 1);
  if (PAIRS[open] && charAt(state, pos) === PAIRS[open] && state.field(autoPairs).includes(pos)) {
    view.dispatch({ changes: { from: pos - 1, to: pos + 1 }, userEvent: 'delete.backward' });
    return true;
  }
  return false;
}

// After the IME confirmed a single bracket: add the closing one, or step over an added one.
function afterImeBracket(committed) {
  if (typeof committed !== 'string' || committed.length !== 1 || view.state.readOnly) return;
  const { state } = view;
  const sel = state.selection.main;
  if (!sel.empty) return;
  const pos = sel.head;
  if (PAIRS[committed] && charAt(state, pos - 1) === committed && charAt(state, pos) !== PAIRS[committed] && !WORD_AFTER.test(charAt(state, pos))) {
    view.dispatch({ changes: { from: pos, insert: PAIRS[committed] }, selection: EditorSelection.cursor(pos), effects: addPair.of(pos), userEvent: 'input.type' });
  } else if (CLOSERS.has(committed) && charAt(state, pos - 1) === committed && charAt(state, pos) === committed && state.field(autoPairs).includes(pos)) {
    view.dispatch({ changes: { from: pos, to: pos + 1 }, userEvent: 'input.type' });
  }
}

// ----- Links: [text](url) ----------------------------------------------------------------
const SAFE_URL = /^(https?:\/\/[^\s<>"]+|mailto:[^\s<>"]+)$/i;
const LINK = /\[([^\]\n]*)\]\(([^)\s]*)\)/g;
let pendingLink = null;   // what Ctrl+K asked the host about: { id, from, to, existing }

// The Markdown link around a position, if any.
function linkAt(state, pos) {
  const line = state.doc.lineAt(pos);
  for (const m of line.text.matchAll(LINK)) {
    const from = line.from + m.index, to = from + m[0].length;
    if (pos >= from && pos <= to) return { from, to, text: m[1], url: m[2] };
  }
  return null;
}

function requestLink(view) {
  if (view.composing || view.state.readOnly || currentId === null) return false;
  const sel = view.state.selection.main;
  const existing = linkAt(view.state, sel.head);
  if (existing && (sel.empty || (sel.from >= existing.from && sel.to <= existing.to))) {
    pendingLink = { id: currentId, from: existing.from, to: existing.to, existing: true };
    post({ type: 'linkRequest', text: existing.text, url: existing.url, existing: true });
  } else {
    const text = view.state.sliceDoc(sel.from, sel.to);
    if (text.includes('\n')) return true; // one line only
    pendingLink = { id: currentId, from: sel.from, to: sel.to, existing: false };
    post({ type: 'linkRequest', text, url: '', existing: false });
  }
  return true;
}

// Brackets and spaces in a URL would end the Markdown link early.
const linkUrl = (url) => url.replace(/ /g, '%20').replace(/\(/g, '%28').replace(/\)/g, '%29');

function applyLink(text, url) {
  const p = pendingLink;
  pendingLink = null;
  if (!p || p.id !== currentId || p.to > view.state.doc.length) return;
  let insert;
  if (url === '') {
    if (!p.existing) return;
    insert = text; // link removed: the words stay
  } else {
    if (!SAFE_URL.test(url)) return;
    insert = `[${(text || url).replace(/[\[\]]/g, '')}](${linkUrl(url)})`;
  }
  view.dispatch({
    changes: { from: p.from, to: p.to, insert },
    selection: EditorSelection.cursor(p.from + insert.length),
    userEvent: 'input.link',
  });
  view.focus();
}

// Pasting a URL over selected words makes them a link (one step, undoable).
// Runs before the Markdown package's own paste-as-link, which also links text that is not a
// valid address (e.g. with spaces): such text is pasted as plain text instead.
const linkPaste = Prec.highest(EditorView.domEventHandlers({
  paste(event, view) {
    const url = (event.clipboardData && event.clipboardData.getData('text/plain') || '').trim();
    const sel = view.state.selection.main;
    if (view.composing || sel.empty) return false;
    if (!SAFE_URL.test(url)) {
      if (!/^(https?:|mailto:)/i.test(url)) return false;
      event.preventDefault();
      view.dispatch(view.state.replaceSelection(url), { userEvent: 'input.paste' });
      return true;
    }
    const text = view.state.sliceDoc(sel.from, sel.to);
    if (text.includes('\n') || /^\s*$/.test(text)) return false;
    event.preventDefault();
    const insert = `[${text.replace(/[\[\]]/g, '')}](${linkUrl(url)})`;
    view.dispatch({ changes: { from: sel.from, to: sel.to, insert }, selection: EditorSelection.cursor(sel.from + insert.length), userEvent: 'input.paste' });
    return true;
  },
  // Ctrl+click on a link asks the host to open it (after showing the address). A plain click
  // only moves the cursor, so editing never opens a browser by accident.
  mousedown(event, view) {
    if (!event.ctrlKey || event.button !== 0) return false;
    const pos = view.posAtCoords({ x: event.clientX, y: event.clientY });
    const link = pos === null ? null : linkAt(view.state, pos);
    if (!link || !SAFE_URL.test(link.url)) return false;
    event.preventDefault();
    post({ type: 'openLink', url: link.url });
    return true;
  },
}));

// ----- Line wrapping (display only: the note's text is never changed) ------------------------
const wrapping = new Compartment();
let wrapOn = true;
const wrapExtension = () => (wrapOn ? EditorView.lineWrapping : []);

const appKeymap = Prec.highest(keymap.of([
  { key: 'Escape', run: command('hide') },
  { key: 'Mod-n', run: command('new') },
  { key: 'Mod-o', run: command('open') },
  { key: 'Mod-Shift-e', run: command('export') },
  { key: 'Mod-w', run: command('close') },
  { key: 'Mod-s', run: command('save') },
  { key: 'Mod-r', run: command('record') },
  { key: 'Ctrl-Tab', run: command('next') },
  { key: 'Ctrl-Shift-Tab', run: command('previous') },
  { key: 'Mod-Shift-t', run: command('reopen') },
  { key: 'F2', run: command('rename') },
  { key: 'F6', run: command('focusOut') },
  { key: 'Mod-k', run: requestLink },
  { key: 'Mod-e', run: command('preview') },
  { key: 'Ctrl-Alt-p', run: command('preview') },
  { key: 'Backspace', run: deleteAutoPair },
  { key: 'Mod-b', run: toggleWrap('**') },
  { key: 'Mod-i', run: toggleWrap('*') },
  { key: 'Tab', run: smartTab, shift: smartShiftTab },
  { key: 'Enter', run: (view) => justConfirmedIme() || endEmptyListItem(view) || continueMarkup(view) },
]));

// ----- Editor state per note ---------------------------------------------------------------

const readOnly = new Compartment();
const states = new Map();      // note id -> EditorState (keeps each note's undo history and cursor)
let currentId = null;
let pendingInsert = null;      // transcript waiting for the IME to finish composing

function createState(text, caret, isReadOnly) {
  return EditorState.create({
    doc: text,
    selection: EditorSelection.cursor(Math.max(0, Math.min(caret, text.length))),
    extensions: [
      appKeymap,
      history(),
      drawSelection(),
      wrapping.of(wrapExtension()),
      autoPairs,
      bracketInput,
      listDrag,
      linkPaste,
      indentUnit.of('\t'),
      markdown({ base: markdownLanguage }),   // GFM: tasks, strikethrough, tables, autolinks
      keymap.of([...historyKeymap, ...defaultKeymap]),
      placeholder('メモを書く…'),
      livePreview,
      readOnly.of(isReadOnly ? [EditorState.readOnly.of(true), EditorView.editable.of(false)] : []),
      EditorView.contentAttributes.of({ spellcheck: 'false', autocorrect: 'off', autocapitalize: 'off', 'aria-label': 'メモ' }),
      EditorView.updateListener.of(onUpdate),
    ],
  });
}

function onUpdate(update) {
  if (currentId === null) return;
  if (update.docChanged) {
    post({ type: 'change', id: currentId, text: update.state.doc.toString(), caret: update.state.selection.main.head });
  } else if (update.selectionSet) {
    post({ type: 'caret', id: currentId, caret: update.state.selection.main.head });
  }
  // In the other modes the source editor is hidden: its late blur must not overrule their focus.
  if (update.focusChanged && (mode === 'source' || update.view.hasFocus)) post({ type: 'focus', focused: update.view.hasFocus });
  if (mode === 'preview' && update.docChanged) schedulePreview();
}

// Modes (see setMode); declared before the view exists, which reads them.
const editorEl = document.getElementById('editor');
const visualEl = document.getElementById('visual');
const previewEl = document.getElementById('preview');
let mode = 'source';          // 'source' | 'visual' | 'preview'
let lastEdit = 'source';      // the editing mode the preview goes back to
let editorScroll = null;
let previewTimer = 0;
const view = new EditorView({ parent: editorEl, state: createState('', 0, false) });

// Shows a note. Reuses the note's previous state (undo history) if its text is unchanged.
function load(id, text, caret, isReadOnly) {
  if (pendingInsert !== null && currentId !== null) {
    // A transcript was waiting for the IME: it belongs to the note being left, not the next one.
    const text0 = pendingInsert;
    pendingInsert = null;
    insertTranscriptNow(text0);
  }
  if (mode === 'visual') syncFromVisual();     // the note being left gets the last visual edit
  if (currentId !== null) states.set(currentId, view.state);
  const saved = states.get(id);
  let state;
  if (saved && saved.doc.toString() === text && saved.facet(EditorState.readOnly) === isReadOnly) {
    state = saved;
  } else {
    state = createState(text, caret, isReadOnly);
  }
  currentId = id;
  view.setState(state);
  view.dispatch({ effects: wrapping.reconfigure(wrapExtension()) }); // a kept state may have the other setting
  view.dispatch({ effects: EditorView.scrollIntoView(view.state.selection.main.head, { y: 'center' }) });
  editorScroll = null;   // another note: back in the source mode, show its caret
  if (mode === 'preview') {
    renderNow();
    previewEl.scrollTop = 0;
  } else if (mode === 'visual') {
    // The same mode for the next note when it can be shown without changing it.
    if (!openVisual()) showMode('source', 'visual-note');
    else visualEl.scrollTop = 0;
  }
}

// ----- Three modes: source (CodeMirror), visual (Milkdown), preview (read only) -------------
// The note's text is always the source editor's document: the visual mode shows it and puts
// only the lines an edit changed back into it (mdpatch.js); the preview renders it. Switching
// modes never changes the text, never saves and never adds undo steps.

function renderNow() {
  clearTimeout(previewTimer);
  previewTimer = 0;
  renderPreview(previewEl, view.state.doc.toString());
}

// A transcript arriving while the preview is shown: re-render, keeping the reading position.
function schedulePreview() {
  if (previewTimer) return;
  previewTimer = setTimeout(() => {
    const top = previewEl.scrollTop;
    renderNow();
    previewEl.scrollTop = top;
  }, 150);
}

let visual = null;            // the visual editor, loaded on first use
let visualLoading = null;
let visualBase = '';          // Milkdown's Markdown for the note's current text
let visualNotice = false;

function loadVisual() {
  if (!visualLoading) {
    visualLoading = import('./visualmode.js').then(({ createVisual }) => createVisual(visualEl, {
      changed: (md) => applyVisualEdit(md),
      requestLink: (text, url, existing) => post({ type: 'linkRequest', text, url, existing }),
      openLink: (url) => post({ type: 'openLink', url }),
    })).then((v) => { visual = v; return v; });
  }
  return visualLoading;
}

let visualNote = null;        // { id, text } the visual editor shows, to resume it unchanged

function openVisual() {
  const text = view.state.doc.toString();
  if (visualNote && visualNote.id === currentId && visualNote.text === text && sameContent(visual.markdown(), text)) {
    visual.resume(view.state.readOnly);   // nothing changed meanwhile: caret, selection, undo kept
    return true;
  }
  const base = visual.open(text, view.state.readOnly);
  if (base === null) { visualNote = null; return false; }
  visualBase = base;
  visualNote = { id: currentId, text };
  // The caret where it was in the source: after the same words.
  const words = sourceCaretText();
  if (words) visual.placeCaret(words.needle, words.nth);
  return true;
}

// The caret's position as text (markup left out), to find the same place in the other mode.
function plain(s) { return s.replace(/[*_`~\[\]()<>|#]/g, ''); }
function sourceCaretText() {
  const { state } = view;
  const head = state.selection.main.head;
  const line = state.doc.lineAt(head);
  const before = plain(line.text.slice(0, head - line.from).replace(/^\s*(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?|^\s*>\s?|^\s*#{1,6}\s+/, '')).slice(-16);
  if (before.trim() === '') return null;
  const all = plain(state.sliceDoc(0, head));
  return { needle: before, nth: all.split(before).length - 2 };
}
function placeSourceCaret(words) {
  if (!words) return;
  const text = view.state.doc.toString();
  // Search the text without markup, then map back to the document.
  const map = [];
  let flat = '';
  for (let i = 0; i < text.length; i++) { if (!/[*_`~\[\]()<>|#]/.test(text[i])) { map.push(i); flat += text[i]; } }
  let at = -1, from = 0;
  for (let k = 0; k <= words.nth; k++) { at = flat.indexOf(words.needle, from); if (at < 0) return; from = at + 1; }
  const pos = map[at + words.needle.length - 1] + 1;
  view.dispatch({ selection: EditorSelection.cursor(Math.min(pos, text.length)), scrollIntoView: true });
}

// An edit in the visual mode: only the changed lines go into the note (one source-editor change).
function applyVisualEdit(md) {
  if (mode !== 'visual' || md === visualBase) return;
  const original = view.state.doc.toString();
  // Safety: the visual editor must have been showing this very text. If the note changed some
  // other way meanwhile, its edit is not written over the note: it is shown again from the note.
  if (!visualNote || visualNote.id !== currentId || visualNote.text !== original) {
    post({ type: 'notice', name: 'visual-reloaded' });
    visualNote = null;
    if (!openVisual()) showMode('source', 'visual-note');
    return;
  }
  let next = patch(original, visualBase, md, (text) => sameContent(text, md));
  if (next === null) {
    next = md;   // could not be limited to the edited lines: the visual editor's text, never a lost edit
    if (!visualNotice) { visualNotice = true; post({ type: 'notice', name: 'visual-rewrote' }); }
  }
  visualBase = md;
  if (next === original) return;
  let from = 0;
  while (from < original.length && from < next.length && original[from] === next[from]) from++;
  let end = 0;
  while (end < original.length - from && end < next.length - from && original[original.length - 1 - end] === next[next.length - 1 - end]) end++;
  view.dispatch({ changes: { from, to: original.length - end, insert: next.slice(from, next.length - end) }, userEvent: 'input.visual' });
  visualNote = { id: currentId, text: next };
}

function syncFromVisual() {
  if (visual) applyVisualEdit(visual.markdown());
}

const scrollerOf = (m) => (m === 'source' ? view.scrollDOM : m === 'visual' ? visualEl : previewEl);
function ratioOf(el) { return el.scrollHeight > el.clientHeight ? el.scrollTop / (el.scrollHeight - el.clientHeight) : 0; }
function setRatio(el, r) { el.scrollTop = Math.round(r * Math.max(0, el.scrollHeight - el.clientHeight)); }
const composing = () => view.composing || (mode === 'visual' && visual !== null && visual.composing);

function report(refused) { post({ type: 'mode', mode, lastEdit, ...(refused ? { refused } : {}) }); }

// Changes what is shown; `refused` explains why the requested mode was not used.
function showMode(next, refused) {
  const from = mode;
  const ratio = ratioOf(scrollerOf(from));
  if (from === 'source') editorScroll = view.scrollDOM.scrollTop;
  let caretWords = null;
  if (from === 'visual' && visual) {
    syncFromVisual();
    caretWords = next === 'source' ? visual.caretText() : null;
    visual.close();
    visualNote = { id: currentId, text: view.state.doc.toString() };
  }
  if (from === 'preview') { clearTimeout(previewTimer); previewTimer = 0; previewEl.replaceChildren(); }
  mode = next;
  if (next !== 'preview') lastEdit = next;
  editorEl.hidden = next !== 'source';
  visualEl.hidden = next !== 'visual';
  previewEl.hidden = next !== 'preview';
  if (next === 'source') {
    view.requestMeasure();
    if (caretWords) placeSourceCaret(caretWords);
    if (from === 'preview' && lastEditWas === 'source' && editorScroll !== null) view.scrollDOM.scrollTop = editorScroll;
    else if (from !== 'source') setRatio(view.scrollDOM, ratio);
    view.focus();
  } else if (next === 'visual') {
    setRatio(visualEl, ratio);
    visual.focus();
  } else {
    lastEditWas = from;
    renderNow();
    setRatio(previewEl, ratio);
    previewEl.focus({ preventScroll: true });
  }
  if (next !== 'source' && document.hasFocus()) post({ type: 'focus', focused: true });
  report(refused);
}
let lastEditWas = 'source';

let switching = Promise.resolve();
function setMode(next) {
  switching = switching.then(async () => {
    if (next === 'previous') next = mode === 'preview' ? lastEdit : 'preview';
    if (!['source', 'visual', 'preview'].includes(next) || next === mode) { report(); return; }
    // Never in the middle of an IME composition: the unfinished text would be lost.
    if (composing()) { report('composing'); return; }
    if (next === 'visual') {
      try { await loadVisual(); } catch { report('visual-unavailable'); return; }
      if (!openVisual()) { report('visual-note'); return; }   // would change the note: stay
    }
    showMode(next);
  }).catch(() => report('error'));   // a failed switch never blocks the next one
  return switching;
}

// The letter key of a shortcut, by key position, or by key code when the position is not
// reported (keys sent by some tools): never by the typed character, which depends on the IME.
export function letter(e) {
  if (e.code && e.code.startsWith('Key')) return e.code.slice(3).toLowerCase();
  return e.keyCode >= 65 && e.keyCode <= 90 ? String.fromCharCode(e.keyCode + 32) : '';
}

// Keys outside the source editor: the app's shortcuts still work.
const appKeys = { n: 'new', o: 'open', w: 'close', s: 'save', r: 'record' };
function appKey(e) {
  if (e.isComposing || composing()) return null;
  if (e.key === 'Escape') return 'hide';
  if (e.key === 'F6') return 'focusOut';
  if (e.key === 'F2') return 'rename';
  if (e.ctrlKey && e.key === 'Tab') return e.shiftKey ? 'previous' : 'next';
  const k = letter(e);
  if (e.ctrlKey && e.altKey && !e.shiftKey && k === 'p') return 'preview';
  if (e.ctrlKey && !e.altKey && !e.shiftKey && k === 'e') return 'preview';
  if (e.ctrlKey && e.shiftKey && !e.altKey && k === 't') return 'reopen';
  if (e.ctrlKey && e.shiftKey && !e.altKey && k === 'e') return 'export';
  if (e.altKey && !e.ctrlKey && k === 'z') return 'wrap';
  if (e.ctrlKey && !e.shiftKey && !e.altKey && k) return appKeys[k] || null;
  return null;
}
for (const el of [visualEl, previewEl]) {
  el.addEventListener('keydown', (e) => {
    const name = appKey(e);
    if (name) {
      e.preventDefault();
      e.stopPropagation();
      post({ type: 'command', name });
    } else if (el === previewEl && e.key === 'Enter' && e.target.classList && e.target.classList.contains('pv-link')) {
      e.preventDefault();
      post({ type: 'openLink', url: e.target.dataset.url });
    }
  }, true);
}
// A click on a link in the preview asks the host, which shows the address before opening anything.
previewEl.addEventListener('click', (e) => {
  const a = e.target.closest && e.target.closest('.pv-link');
  if (a) post({ type: 'openLink', url: a.dataset.url });
});
visualEl.addEventListener('compositionstart', () => post({ type: 'composition', active: true }));
visualEl.addEventListener('compositionend', () => {
  post({ type: 'composition', active: false });
  if (pendingInsert !== null) {
    const text = pendingInsert;
    pendingInsert = null;
    setTimeout(() => insertTranscript(text), 0);
  }
});
for (const el of [visualEl, previewEl]) {
  el.addEventListener('focusin', () => post({ type: 'focus', focused: true }));
  // Only the shown mode reports losing focus (a hidden one blurs late, after the switch).
  el.addEventListener('focusout', (e) => { if (mode === (el === visualEl ? 'visual' : 'preview') && (!e.relatedTarget || !el.contains(e.relatedTarget))) post({ type: 'focus', focused: false }); });
}

// ----- Tables: inserted at the caret ------------------------------------------------------------

/** A GitHub table (a header row and rows - 1 empty rows), as its own block at the caret's line. */
function insertTableSource(rows, cols) {
  if (view.state.readOnly || view.composing) return;
  const header = '| ' + Array.from({ length: cols }, (_, i) => `列${i + 1}`).join(' | ') + ' |';
  const rule = '|' + '---|'.repeat(cols);
  const empty = '|' + '  |'.repeat(cols);
  const table = [header, rule, ...Array.from({ length: Math.max(1, rows - 1) }, () => empty)].join('\n');
  const { state } = view;
  const line = state.doc.lineAt(state.selection.main.head);
  const blank = line.text.trim() === '';
  const next = line.number < state.doc.lines ? state.doc.line(line.number + 1) : null;
  // An empty line becomes the table; after a line with text the table follows it. A blank line
  // separates it from text before and after, so neighbouring paragraphs are not joined.
  const before = blank ? (line.number > 1 && state.doc.line(line.number - 1).text.trim() !== '' ? '\n' : '') : '\n\n';
  const after = next && next.text.trim() !== '' ? '\n' : '';
  const from = blank ? line.from : line.to;
  const to = blank ? line.to : line.to;
  const insert = before + table + after;
  const cell = from + before.length + 2;   // the first header cell, selected so typing replaces it
  view.dispatch({
    changes: { from, to, insert },
    selection: EditorSelection.range(cell, cell + `列1`.length),
    userEvent: 'input.table',
    annotations: isolateHistory.of('full'),   // its own undo step, never merged with typing
    scrollIntoView: true,
  });
  view.focus();
}

function insertTable(rows, cols) {
  if (mode === 'source') insertTableSource(rows, cols);
  else if (mode === 'visual' && visual && !view.state.readOnly && !visual.composing) visual.insertTable(rows, cols);
}

// Same rule as the C# TranscriptInsertion: the transcript becomes its own paragraph at the caret.
function insertTranscript(text) {
  if (composing()) {
    // Several transcripts may arrive while composing: keep them all, in order.
    pendingInsert = pendingInsert === null ? text : pendingInsert + '\n\n' + text;
    return;
  }
  insertTranscriptNow(text);
}

// The host sends the finished block ("以下、AIによる文字起こし" + the text). It goes to the very
// end of the note, after one empty line; the caret, the selection and the scroll position stay
// as they are, so the user can keep writing where they were.
function insertTranscriptNow(block) {
  if (mode === 'visual') syncFromVisual();   // edits typed a moment ago come first
  const doc = view.state.doc;
  let trailing = 0;
  for (let i = doc.length - 1; i >= 0 && trailing < 2 && doc.sliceString(i, i + 1) === '\n'; i--) trailing++;
  const separator = doc.length === 0 ? '' : '\n'.repeat(2 - trailing);
  view.dispatch({
    changes: { from: doc.length, insert: separator + block },
    selection: view.state.selection,   // positions before the end are unaffected
    userEvent: 'input.transcript',
  });
  if (mode === 'visual' && visual) {
    // Shown at the end of the visual document too; the caret stays where it is.
    visual.append(block);
    visualBase = visual.markdown();
    visualNote = { id: currentId, text: view.state.doc.toString() };
  }
}

view.contentDOM.addEventListener('compositionstart', () => post({ type: 'composition', active: true }));
view.contentDOM.addEventListener('compositionend', (e) => {
  compositionEndedAt = performance.now();
  const committed = e.data;
  setTimeout(() => afterImeBracket(committed), 0);
  post({ type: 'composition', active: false });
  if (pendingInsert !== null) {
    const text = pendingInsert;
    pendingInsert = null;
    setTimeout(() => insertTranscript(text), 0);
  }
});

// Whenever the page receives focus (window shown, WebView focused by the host), put the caret
// back into the editor. A focus request from the host can arrive before the page actually
// has focus, in which case view.focus() alone would do nothing.
window.addEventListener('focus', () => focusMode());
function focusMode() {
  if (mode === 'preview') previewEl.focus({ preventScroll: true });
  else if (mode === 'visual' && visual) visual.focus();
  else if (!view.hasFocus) view.focus();
  // Focus already inside the visual editor or the preview fires no event: say so anyway.
  if (mode !== 'source' && document.hasFocus()) post({ type: 'focus', focused: true });
}

// Alt+Z (wrap on/off) by key position: with Alt held, the key value depends on the layout and IME.
document.addEventListener('keydown', (e) => {
  if (e.altKey && !e.ctrlKey && !e.metaKey && letter(e) === 'z' && !view.composing) {
    e.preventDefault();
    e.stopPropagation();
    post({ type: 'command', name: 'wrap' });
  }
}, true);

// Links are styled only: never navigate (the host blocks navigation as well).
document.addEventListener('click', (e) => { if (e.target.closest && e.target.closest('a')) e.preventDefault(); }, true);
document.addEventListener('dragover', (e) => e.preventDefault());
document.addEventListener('drop', (e) => { if (e.dataTransfer && e.dataTransfer.files.length) e.preventDefault(); });

// ----- Host messages (validated: unknown or malformed messages are ignored) ---------------

const isId = (v) => typeof v === 'string' && v.length > 0 && v.length <= 64;
const isText = (v) => typeof v === 'string' && v.length <= 20_000_000;
const isInt = (v) => Number.isInteger(v) && v >= 0;

function handleHost(message) {
  if (!message || typeof message !== 'object') return;
  switch (message.type) {
    case 'load':
      if (isId(message.id) && isText(message.text) && isInt(message.caret)) {
        load(message.id, message.text, message.caret, message.readOnly === true);
      }
      break;
    case 'insert':
      if (!isText(message.text)) break;
      // Never drop a transcript: if another note is shown by now, give it back to the host.
      if (message.id === currentId) insertTranscript(message.text);
      else post({ type: 'insertRejected', id: message.id, text: message.text });
      break;
    case 'forget':
      if (isId(message.id)) states.delete(message.id);
      break;
    case 'focus':
      focusMode();
      break;
    case 'mode':
      if (typeof message.mode === 'string') setMode(message.mode);
      break;
    case 'insertTable':
      if (Number.isInteger(message.rows) && Number.isInteger(message.cols) && message.rows >= 2 && message.rows <= 50 && message.cols >= 1 && message.cols <= 20) insertTable(message.rows, message.cols);
      break;
    case 'options':
      if (typeof message.wrap === 'boolean' && message.wrap !== wrapOn) {
        wrapOn = message.wrap;
        view.dispatch({ effects: wrapping.reconfigure(wrapExtension()) });
      }
      visualEl.classList.toggle('nowrap', !wrapOn);   // display only, as in the source mode
      break;
    case 'link':
      if (mode === 'visual' && visual) visual.requestLink();
      else if (mode === 'source') requestLink(view);
      break;
    case 'probe':
      // Tests only need where the list handles are (line numbers and coordinates, no text).
      post({ type: 'probeResult', data: JSON.stringify({ dpr: devicePixelRatio, down: lastDown, key: lastShortcut, mode, composing: Boolean(composing()), lastCommand, preview: previewProbe(), visual: mode === 'visual' && visual ? visual.probe() : null, handles: [...document.querySelectorAll('.cm-drag-handle')].map((h) => {
        const b = h.getBoundingClientRect();
        return { line: Number(h.dataset.line), x: Math.round(b.left + b.width / 2), y: Math.round(b.top + b.height / 2) };
      }) }) });
      break;
    case 'applyLink':
      if (typeof message.url === 'string' && message.url.length <= 2048 && typeof message.text === 'string' && message.text.length <= 1000) {
        if (mode === 'visual' && visual) visual.applyLink(message.text, message.url);
        else applyLink(message.text, message.url);
      } else {
        pendingLink = null;
        if (visual) visual.cancelLink();
      }
      break;
    case 'theme':
      if (message.theme === 'light' || message.theme === 'dark') document.documentElement.dataset.theme = message.theme;
      else delete document.documentElement.dataset.theme;
      // Paper colour: only a plain #rrggbb value is accepted.
      if (typeof message.paper === 'string' && /^#[0-9a-fA-F]{6}([0-9a-fA-F]{2})?$/.test(message.paper)) document.documentElement.style.setProperty('--paper', message.paper);
      else document.documentElement.style.removeProperty('--paper');
      reportCaret();
      break;
    case 'flush':
    {
      // Host asks for the exact current text (e.g. before exiting). While visible, answer a
      // moment later so keystrokes still queued in the browser are applied first. When hidden
      // (Esc already went through this editor, so nothing is pending) answer at once:
      // timers in hidden pages are throttled.
      const respond = () => post({ type: 'flushed', id: currentId, text: view.state.doc.toString(), caret: view.state.selection.main.head });
      if (message.settle === true && document.visibilityState === 'visible') setTimeout(respond, 60);
      else respond();
      break;
    }
    default:
      break;
  }
}

if (host) {
  host.addEventListener('message', (e) => handleHost(e.data));
} else {
  // Opened in a normal browser for development (no app host): expose a handle for testing.
  window.floatingMemoDebug = { view, load, handleHost, listItems, moveSpec, previewProbe, sent, get mode() { return mode; }, get visual() { return visual; }, setMode };
}
// Tells the host which cursor colour is in effect (only a colour value; used by tests).
function reportCaret() {
  post({ type: 'caretStyle', color: getComputedStyle(document.documentElement).getPropertyValue('--caret').trim() });
}
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', reportCaret);

post({ type: 'ready' });
reportCaret();

// Tests: what the preview shows, as element counts only (no note text).
function previewProbe() {
  if (mode !== 'preview') return null;
  const count = (sel) => previewEl.querySelectorAll(sel).length;
  return {
    h: [1, 2, 3, 4, 5, 6].map((d) => count(`h${d}`)), strong: count('strong'), em: count('em'), del: count('del'),
    ul: count('ul'), ol: count('ol'), nested: count('li ul, li ol'), tasks: count('input[type=checkbox]'),
    checked: count('input[type=checkbox]:checked'), enabledBoxes: count('input:not([disabled])'),
    quote: count('blockquote'), hr: count('hr'), code: count('pre'), inlineCode: count('.pv-inline-code'),
    links: count('.pv-link'), tables: count('.pv-table table'), th: count('th'), td: count('td'),
    cellStrong: count('td strong, th strong'), cellLinks: count('td .pv-link, th .pv-link'), br: count('br'),
    raw: count('.pv-raw'), images: count('img'), scripts: count('script, iframe, object, embed'),
    handlers: [...previewEl.querySelectorAll('*')].filter((n) => [...n.attributes].some((a) => a.name.startsWith('on'))).length,
    tableScrolls: [...previewEl.querySelectorAll('.pv-table')].some((w) => w.scrollWidth > w.clientWidth),
    pageScrollsSideways: previewEl.scrollWidth > previewEl.clientWidth,
    focused: document.activeElement === previewEl, scrollTop: Math.round(previewEl.scrollTop),
  };
}

// Tests: where the last mouse press landed (coordinates and element class only, no text).
let lastDown = null;
// Tests: the last shortcut (key position and modifiers only; plain typing is not recorded).
let lastShortcut = null;
addEventListener('keydown', (e) => { if (e.ctrlKey || e.altKey) lastShortcut = { code: e.code || `keyCode ${e.keyCode}`, ctrl: e.ctrlKey, alt: e.altKey, shift: e.shiftKey, mode }; }, true);
addEventListener('mousedown', (e) => { lastDown = { x: e.clientX, y: e.clientY, cls: String(e.target && e.target.className || '').slice(0, 40) }; }, true);
