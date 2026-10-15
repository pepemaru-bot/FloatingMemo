// Floating Memo — Markdown editor (CodeMirror 6) with Obsidian-style live preview.
//
// The document is always the plain Markdown text: live preview only *decorates* the view
// (hides markup on lines the cursor is not on, draws checkboxes and bullets). Nothing is
// converted to HTML and back, so syntax this editor does not understand is never altered.
//
// Talks to the C# host through window.chrome.webview messages only (see handleHost()).
// No network, no eval, no innerHTML.

import { EditorState, EditorSelection, Compartment, Prec } from './vendor/codemirror-state/dist/index.js';
import { EditorView, keymap, Decoration, ViewPlugin, WidgetType, drawSelection, placeholder } from './vendor/codemirror-view/dist/index.js';
import { history, historyKeymap, defaultKeymap, indentMore, indentLess, insertTab } from './vendor/codemirror-commands/dist/index.js';
import { syntaxTree, indentUnit } from './vendor/codemirror-language/dist/index.js';
import { markdown, markdownLanguage, insertNewlineContinueMarkup } from './vendor/codemirror-lang-markdown/dist/index.js';

const host = window.chrome && window.chrome.webview;
const post = (message) => { if (host) host.postMessage(message); };

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
      EditorView.lineWrapping,
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
  if (update.focusChanged) post({ type: 'focus', focused: update.view.hasFocus });
}

const view = new EditorView({ parent: document.getElementById('editor'), state: createState('', 0, false) });

// Shows a note. Reuses the note's previous state (undo history) if its text is unchanged.
function load(id, text, caret, isReadOnly) {
  if (pendingInsert !== null && currentId !== null) {
    // A transcript was waiting for the IME: it belongs to the note being left, not the next one.
    const text0 = pendingInsert;
    pendingInsert = null;
    insertTranscriptNow(text0);
  }
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
  view.dispatch({ effects: EditorView.scrollIntoView(view.state.selection.main.head, { y: 'center' }) });
}

// Same rule as the C# TranscriptInsertion: the transcript becomes its own paragraph at the caret.
function insertTranscript(text) {
  if (view.composing) {
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
  const doc = view.state.doc;
  let trailing = 0;
  for (let i = doc.length - 1; i >= 0 && trailing < 2 && doc.sliceString(i, i + 1) === '\n'; i--) trailing++;
  const separator = doc.length === 0 ? '' : '\n'.repeat(2 - trailing);
  view.dispatch({
    changes: { from: doc.length, insert: separator + block },
    selection: view.state.selection,   // positions before the end are unaffected
    userEvent: 'input.transcript',
  });
}

view.contentDOM.addEventListener('compositionstart', () => post({ type: 'composition', active: true }));
view.contentDOM.addEventListener('compositionend', () => {
  compositionEndedAt = performance.now();
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
window.addEventListener('focus', () => { if (!view.hasFocus) view.focus(); });

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
      view.focus();
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
  window.floatingMemoDebug = { view, load, handleHost };
}
// Tells the host which cursor colour is in effect (only a colour value; used by tests).
function reportCaret() {
  post({ type: 'caretStyle', color: getComputedStyle(document.documentElement).getPropertyValue('--caret').trim() });
}
matchMedia('(prefers-color-scheme: dark)').addEventListener('change', reportCaret);

post({ type: 'ready' });
reportCaret();
