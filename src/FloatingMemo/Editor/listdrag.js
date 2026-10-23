// Floating Memo — moving Markdown list items by drag and drop (and Alt+Shift+Up/Down).
//
// Items come from the Markdown syntax tree: an item is its first line, its continuation lines
// and all its children, so a parent moves with everything under it. A drop is one change (one
// Undo step); the moved lines are re-indented for the new level, their text (checkboxes
// included) is not touched. A drop inside the item's own lines is refused.

import { EditorSelection } from './vendor/codemirror-state/dist/index.js';
import { EditorView, Decoration, ViewPlugin, WidgetType, keymap } from './vendor/codemirror-view/dist/index.js';
import { syntaxTree } from './vendor/codemirror-language/dist/index.js';

// ----- List items from the syntax tree ----------------------------------------------------------

/** Every list item: lines (1-based), level (0 = top), and its first line's indentation. */
export function listItems(state) {
  const items = [];
  syntaxTree(state).iterate({
    enter(node) {
      if (node.name !== 'ListItem') return;
      let level = 0;
      for (let p = node.node.parent; p; p = p.parent) if (p.name === 'ListItem') level++;
      const first = state.doc.lineAt(node.from);
      // The node can end at the start of the next line (after its newline): that line is not part of it.
      let endPos = node.to;
      if (endPos > first.from && state.doc.sliceString(endPos - 1, endPos) === '\n') endPos--;
      let last = state.doc.lineAt(endPos);
      // Trailing blank lines belong to the gap, not to the item.
      while (last.number > first.number && last.text.trim() === '') last = state.doc.line(last.number - 1);
      items.push({ from: first.from, to: last.to, firstLine: first.number, lastLine: last.number, level, indent: /^\s*/.exec(first.text)[0] });
    },
  });
  return items;
}

/** The item that starts on this line, if any. */
export function itemStartingAt(state, lineNumber) {
  return listItems(state).find((i) => i.firstLine === lineNumber) || null;
}

/** The indentation used for one level in this item ("\t" unless the note uses spaces). */
function unitOf(item, items) {
  if (item.level > 0 && item.indent.length > 0) return item.indent.slice(0, Math.max(1, Math.round(item.indent.length / item.level)));
  const deeper = items.find((i) => i.level > 0 && i.indent.length > 0);
  return deeper ? deeper.indent.slice(0, Math.max(1, Math.round(deeper.indent.length / deeper.level))) : '\t';
}

/** Levels allowed for a drop before `beforeLine` (or at the end): up to one deeper than the item above. */
export function allowedLevels(state, beforeLine, moving) {
  // The deepest item that owns the line just above the drop (skipping the moving item's lines).
  const items = listItems(state).filter((i) => !(i.firstLine >= moving.firstLine && i.lastLine <= moving.lastLine));
  let line = beforeLine - 1;
  while (line >= moving.firstLine && line <= moving.lastLine) line = moving.firstLine - 1;
  const above = items.filter((i) => i.firstLine <= line && i.lastLine >= line).sort((a, b) => b.level - a.level)[0];
  return { min: 0, max: above ? above.level + 1 : 0 };
}

/**
 * Moves `item` so it starts before line `beforeLine` (doc.lines + 1 = at the end) at `level`.
 * Returns a transaction spec, or null when the drop is not allowed (inside the item itself).
 */
export function moveSpec(state, item, beforeLine, level) {
  const doc = state.doc;
  if (beforeLine > item.firstLine && beforeLine <= item.lastLine) return null; // into its own lines
  const { max } = allowedLevels(state, beforeLine, item);
  level = Math.max(0, Math.min(level, max));
  const inPlace = beforeLine === item.firstLine || beforeLine === item.lastLine + 1;
  if (inPlace && level === item.level) return null; // nothing would change

  const items = listItems(state);
  const unit = unitOf(item, items);
  const oldIndent = item.indent;
  const newIndent = unit.repeat(level);
  const lines = [];
  for (let n = item.firstLine; n <= item.lastLine; n++) {
    const text = doc.line(n).text;
    lines.push(text.startsWith(oldIndent) ? newIndent + text.slice(oldIndent.length) : text.trim() === '' ? text : newIndent + text.trimStart());
  }
  const moved = lines.join('\n');

  // Both changes use the original positions, so they are one change (one Undo step).
  const last = item.lastLine === doc.lines;
  const removal = !last ? { from: item.from, to: doc.line(item.lastLine + 1).from }
    : item.firstLine > 1 ? { from: item.from - 1, to: item.to } : { from: item.from, to: item.to };
  const insert = beforeLine <= doc.lines
    ? { from: doc.line(beforeLine).from, insert: moved + '\n' }
    : { from: doc.length, insert: (doc.length > 0 ? '\n' : '') + moved };
  const changes = state.changes([removal, insert]);
  const start = changes.mapPos(insert.from, -1) + (insert.insert.startsWith('\n') ? 1 : 0) + newIndent.length;
  return { changes, selection: EditorSelection.cursor(Math.min(start, changes.newLength)), userEvent: 'move.list', scrollIntoView: true };
}

// ----- Keyboard: Alt+Shift+Up / Down among siblings ---------------------------------------------

function moveByKey(view, down) {
  if (view.composing) return false;
  const { state } = view;
  const line = state.doc.lineAt(state.selection.main.head).number;
  const items = listItems(state);
  // The innermost item containing the cursor line.
  const item = items.filter((i) => i.firstLine <= line && i.lastLine >= line).sort((a, b) => b.level - a.level)[0];
  if (!item) return false;
  const siblings = items.filter((i) => i.level === item.level && sameParent(items, i, item));
  const at = siblings.indexOf(item);
  const other = siblings[at + (down ? 1 : -1)];
  if (!other) return true; // already first / last: nothing to do, but the key is ours
  const spec = moveSpec(state, item, down ? other.lastLine + 1 : other.firstLine, item.level);
  if (spec) view.dispatch(spec);
  return true;
}

function parentOf(items, item) {
  return items.filter((p) => p.level === item.level - 1 && p.firstLine < item.firstLine && p.lastLine >= item.lastLine).pop() || null;
}

function sameParent(items, a, b) {
  const pa = parentOf(items, a), pb = parentOf(items, b);
  return pa === pb || (pa && pb && pa.firstLine === pb.firstLine);
}

export const listMoveKeymap = keymap.of([
  { key: 'Alt-Shift-ArrowUp', run: (v) => moveByKey(v, false) },
  { key: 'Alt-Shift-ArrowDown', run: (v) => moveByKey(v, true) },
]);

// ----- Drag handles and the drop preview ---------------------------------------------------------

class HandleWidget extends WidgetType {
  constructor(line) { super(); this.line = line; }
  eq(other) { return other.line === this.line; }
  toDOM() {
    const handle = document.createElement('span');
    handle.className = 'cm-drag-handle';
    handle.textContent = '⠿';
    handle.dataset.line = String(this.line);
    handle.setAttribute('aria-hidden', 'true'); // keyboard: Alt+Shift+Up/Down
    return handle;
  }
  ignoreEvent() { return false; }
}

const handles = ViewPlugin.fromClass(class {
  constructor(view) { this.decorations = this.build(view); }
  update(update) { if (update.docChanged || update.viewportChanged) this.decorations = this.build(update.view); }
  build(view) {
    const marks = [];
    for (const item of listItems(view.state)) {
      const line = view.state.doc.line(item.firstLine);
      marks.push(Decoration.widget({ widget: new HandleWidget(item.firstLine), side: -1 }).range(line.from));
      marks.push(Decoration.line({ class: 'cm-listItemLine' }).range(line.from));
    }
    return Decoration.set(marks, true);
  }
}, { decorations: (v) => v.decorations });

let drag = null; // { item, target: { beforeLine, level } | null, indicator, scroll }

function indentWidth(view) { return view.defaultCharacterWidth * 4; }

/** Where the text of a line starts on screen (left edge of the content plus its padding). */
function textLeft(view) {
  return view.contentDOM.getBoundingClientRect().left + (parseFloat(getComputedStyle(view.contentDOM).paddingLeft) || 0);
}

function targetAt(view, x, y) {
  const { state } = view;
  const content = view.contentDOM.getBoundingClientRect();
  const pos = view.posAtCoords({ x: content.left + 12, y }, false);
  const line = state.doc.lineAt(pos);
  const block = view.lineBlockAt(line.from);
  const top = view.documentTop + block.top;
  const beforeLine = y > top + block.height / 2 ? line.number + 1 : line.number;
  const { max } = allowedLevels(state, beforeLine, drag.item);
  // Half an indent to the right of a level's start already counts as that level.
  const level = Math.max(0, Math.min(max, Math.floor((x - textLeft(view)) / indentWidth(view) + 0.5)));
  return { beforeLine, level };
}

function showIndicator(view, target) {
  const spec = target && moveSpec(view.state, drag.item, target.beforeLine, target.level);
  const ind = drag.indicator;
  if (!target || (!spec && !(target.beforeLine === drag.item.firstLine && target.level === drag.item.level))) {
    ind.style.display = 'none';
    drag.target = null;
    return;
  }
  const doc = view.state.doc;
  const y = target.beforeLine <= doc.lines
    ? view.lineBlockAt(doc.line(target.beforeLine).from).top
    : view.lineBlockAt(doc.length).bottom;
  const left = textLeft(view) - view.scrollDOM.getBoundingClientRect().left + view.scrollDOM.scrollLeft;
  ind.style.display = 'block';
  ind.style.top = `${y + view.documentPadding.top - 1}px`;
  ind.style.left = `${left + target.level * indentWidth(view)}px`;
  drag.target = spec ? target : null;
}

function endDrag(view, drop) {
  if (!drag) return;
  clearInterval(drag.scroll);
  drag.indicator.remove();
  view.dom.classList.remove('cm-dragging-item');
  const { item, target } = drag;
  drag = null;
  window.removeEventListener('mousemove', onMove, true);
  window.removeEventListener('mouseup', onUp, true);
  window.removeEventListener('keydown', onKey, true);
  if (drop && target) {
    const spec = moveSpec(view.state, item, target.beforeLine, target.level);
    if (spec) view.dispatch(spec);
  }
  view.focus();
}

let activeView = null;
let lastPointer = { x: 0, y: 0 };
function onMove(e) {
  if (!drag) return;
  e.preventDefault();
  lastPointer = { x: e.clientX, y: e.clientY };
  showIndicator(activeView, targetAt(activeView, e.clientX, e.clientY));
}
function onUp(e) { if (drag) { e.preventDefault(); endDrag(activeView, true); } }
function onKey(e) { if (drag && e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); endDrag(activeView, false); } }

const dragStart = EditorView.domEventHandlers({
  mousedown(event, view) {
    const handle = event.target.closest && event.target.closest('.cm-drag-handle');
    if (!handle || event.button !== 0 || view.composing || view.state.readOnly) return false;
    const item = itemStartingAt(view.state, Number(handle.dataset.line));
    if (!item) return false;
    event.preventDefault(); // no text selection from the handle
    activeView = view;
    const indicator = document.createElement('div');
    indicator.className = 'cm-drop-indicator';
    view.scrollDOM.appendChild(indicator);
    drag = { item, target: null, indicator, scroll: 0 };
    view.dom.classList.add('cm-dragging-item');
    lastPointer = { x: event.clientX, y: event.clientY };
    // Scroll while the pointer is near the top or bottom edge.
    drag.scroll = setInterval(() => {
      if (!drag) return;
      const box = view.scrollDOM.getBoundingClientRect();
      const edge = 28;
      const step = lastPointer.y < box.top + edge ? -14 : lastPointer.y > box.bottom - edge ? 14 : 0;
      if (step !== 0) {
        view.scrollDOM.scrollTop += step;
        showIndicator(view, targetAt(view, lastPointer.x, Math.max(box.top + 2, Math.min(box.bottom - 2, lastPointer.y))));
      }
    }, 30);
    window.addEventListener('mousemove', onMove, true);
    window.addEventListener('mouseup', onUp, true);
    window.addEventListener('keydown', onKey, true);
    return true;
  },
});

/** Everything the editor needs: handles, drag and drop, the keyboard moves. */
export const listDrag = [handles, dragStart, listMoveKeymap];
