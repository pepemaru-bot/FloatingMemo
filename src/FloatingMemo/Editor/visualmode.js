// Floating Memo — the visual (WYSIWYG) editing mode, Milkdown (ProseMirror), inside the editor
// page. Loaded only when the visual mode is first used.
//
// The note's text lives in the source editor (CodeMirror); this mode shows it and reports
// Milkdown's Markdown after each edit. The page then puts only the changed lines into the note
// (mdpatch.js), so switching modes never rewrites the note.
// No network, no eval, no innerHTML.

import { Editor, rootCtx, defaultValueCtx, editorViewCtx, parserCtx, remarkStringifyOptionsCtx } from './visual-vendor/milkdown-core/lib/index.js';
import { commonmark } from './visual-vendor/milkdown-preset-commonmark/lib/index.js';
import { gfm, insertTableCommand } from './visual-vendor/milkdown-preset-gfm/lib/index.js';
import { history } from './visual-vendor/milkdown-plugin-history/lib/index.js';
import { listener, listenerCtx } from './visual-vendor/milkdown-plugin-listener/lib/index.js';
import { clipboard } from './visual-vendor/milkdown-plugin-clipboard/lib/index.js';
import { getMarkdown, replaceAll, callCommand } from './visual-vendor/milkdown-utils/lib/index.js';
import { TextSelection } from './visual-vendor/milkdown-prose/lib/state.js';
import { sameContent } from './mdpatch.js';
import { attachListDrag } from './visualdrag.js';

const SAFE_URL = /^(https?:\/\/[^\s<>"]+|mailto:[^\s<>"]+)$/i;

/**
 * hooks.changed(markdown): Milkdown's Markdown after a user edit (debounced).
 * hooks.requestLink(text, url, existing) / hooks.openLink(url): the host's link dialog / confirmation.
 */
export async function createVisual(root, hooks) {
  let loading = false;
  let active = false;

  const editor = await Editor.make()
    .config((ctx) => {
      ctx.set(rootCtx, root);
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
        if (!loading && active) hooks.changed(md);
      });
    })
    .use(commonmark).use(gfm).use(history).use(listener).use(clipboard)
    .create();

  const view = () => editor.action((ctx) => ctx.get(editorViewCtx));
  const drag = attachListDrag(root, {
    view,
    active: () => active,
    markdown: () => editor.action(getMarkdown()),
    replace: (md) => editor.action(replaceAll(md, false)),   // one transaction: one Ctrl+Z
  });

  // The extent of the link around a position (null if there is none).
  function linkRange(state, pos) {
    const type = state.schema.marks.link;
    const $pos = state.doc.resolve(pos);
    const parent = $pos.parent;
    const start = $pos.start();
    let offset = 0;
    const pieces = [];
    parent.forEach((child) => { pieces.push({ from: start + offset, to: start + offset + child.nodeSize, mark: type.isInSet(child.marks) }); offset += child.nodeSize; });
    const i = pieces.findIndex((p) => p.from <= pos && pos <= p.to && p.mark);
    if (i < 0) return null;
    let a = i, b = i;
    while (a > 0 && pieces[a - 1].mark && pieces[a - 1].mark.attrs.href === pieces[i].mark.attrs.href) a--;
    while (b < pieces.length - 1 && pieces[b + 1].mark && pieces[b + 1].mark.attrs.href === pieces[i].mark.attrs.href) b++;
    return { from: pieces[a].from, to: pieces[b].to, href: pieces[i].mark.attrs.href };
  }

  let pendingLink = null;

  // Ctrl+K: the selected words (or the link at the caret) and the address go to the host's dialog.
  function requestLink() {
    const v = view();
    const { state } = v;
    const sel = state.selection;
    const around = linkRange(state, sel.from);
    const range = around && (sel.empty || (sel.from >= around.from && sel.to <= around.to)) ? around : { from: sel.from, to: sel.to, href: '' };
    const text = state.doc.textBetween(range.from, range.to, ' ');
    if (text.includes('\n')) return;
    pendingLink = { from: range.from, to: range.to };
    hooks.requestLink(text, range.href, Boolean(around && range === around));
  }

  // The answer: url '' removes the link; otherwise the words become (or stay) the link.
  function applyLink(text, url) {
    if (!pendingLink) return;
    const { from, to } = pendingLink;
    pendingLink = null;
    const v = view();
    const type = v.state.schema.marks.link;
    let tr = v.state.tr;
    if (url === '') {
      tr = tr.removeMark(from, to, type);
    } else {
      if (!SAFE_URL.test(url)) return;
      const words = text.trim() || url;
      const marks = from < to ? v.state.doc.resolve(from + 1).marks().filter((m) => m.type !== type) : [];
      tr = tr.replaceWith(from, to, v.state.schema.text(words, [...marks, type.create({ href: url })]));
      tr = tr.setSelection(TextSelection.create(tr.doc, from + words.length));
    }
    v.dispatch(tr.scrollIntoView());
    v.focus();
  }

  root.addEventListener('keydown', (e) => {
    if (!active || e.isComposing) return;
    if (e.ctrlKey && !e.altKey && !e.shiftKey && (e.code === 'KeyK' || (!e.code && e.keyCode === 75))) {
      e.preventDefault();
      e.stopPropagation();
      requestLink();
    }
  }, true);

  // A web address pasted over selected words links them (same as the source mode).
  root.addEventListener('paste', (e) => {
    if (!active) return;
    const url = (e.clipboardData && e.clipboardData.getData('text/plain') || '').trim();
    const v = view();
    const sel = v.state.selection;
    if (sel.empty || !SAFE_URL.test(url) || v.state.doc.textBetween(sel.from, sel.to, '\n').includes('\n')) return;
    e.preventDefault();
    e.stopPropagation();
    v.dispatch(v.state.tr.addMark(sel.from, sel.to, v.state.schema.marks.link.create({ href: url })));
  }, true);

  // Ctrl+click on a link asks the host (which shows the address first); a plain click edits.
  root.addEventListener('mousedown', (e) => {
    if (!active) return;
    const a = e.target.closest && e.target.closest('a');
    if (a && e.ctrlKey && e.button === 0) {
      e.preventDefault();
      const href = a.getAttribute('href') || '';
      if (SAFE_URL.test(href)) hooks.openLink(href);
      return;
    }
    // Clicking the box of a task item toggles "[ ]" / "[x]".
    const li = e.target.closest && e.target.closest('li[data-item-type="task"]');
    if (!li || e.offsetX > 0 || !view().editable) return;
    e.preventDefault();
    const v = view();
    const pos = v.posAtDOM(li, 0) - 1;
    const node = v.state.doc.nodeAt(pos);
    if (node && node.type.name === 'list_item') {
      v.dispatch(v.state.tr.setNodeMarkup(pos, undefined, { ...node.attrs, checked: !node.attrs.checked }));
    }
  }, true);

  return {
    /** Shows a note. Returns Milkdown's Markdown for it, or null if it would not mean the same. */
    open(md, readOnly) {
      loading = true;
      let out;
      try {
        editor.action(replaceAll(md, true));   // a new state: no undo history from another note
        out = editor.action(getMarkdown());
      } finally {
        loading = false;
      }
      if (!sameContent(out, md)) {
        active = false;
        return null;
      }
      view().setProps({ editable: () => !readOnly });
      active = true;
      return out;
    },
    close() { active = false; pendingLink = null; },
    /** Shown again with its state (caret, selection, undo history) when the note did not change meanwhile. */
    resume(readOnly) { view().setProps({ editable: () => !readOnly }); active = true; },
    /** Words just before the caret (in its paragraph or cell) and how often they occur before it. */
    caretText() {
      const { state } = view();
      const { $head } = state.selection;
      const before = $head.parent.textBetween(0, $head.parentOffset, ' ').slice(-16);
      if (before.trim() === '') return null;
      const all = state.doc.textBetween(0, $head.pos, '\n');
      return { needle: before, nth: all.split(before).length - 2 };
    },
    /** Puts the caret after the nth occurrence of the words (null: keep the start). */
    placeCaret(needle, nth) {
      const v = view();
      let seen = 0, found = null;
      v.state.doc.descendants((node, pos) => {
        if (found !== null || !node.isTextblock) return found === null;
        const text = node.textBetween(0, node.content.size, ' ');
        let at = text.indexOf(needle);
        while (at >= 0 && found === null) {
          if (seen === nth) found = pos + 1 + at + needle.length;
          seen++;
          at = text.indexOf(needle, at + 1);
        }
        return false;
      });
      if (found !== null && found <= v.state.doc.content.size) v.dispatch(v.state.tr.setSelection(TextSelection.create(v.state.doc, found)).scrollIntoView());
    },
    markdown: () => editor.action(getMarkdown()),
    focus: () => view().focus(),
    get composing() { return view().composing; },
    applyLink,
    requestLink,
    cancelLink() { pendingLink = null; },
    insertTable(rows, cols) {
      editor.action(callCommand(insertTableCommand.key, { row: rows, col: cols }));
      view().focus();
    },
    // A transcript: appended as its own blocks at the end; the caret stays where it is.
    append(block) {
      editor.action((ctx) => {
        const v = ctx.get(editorViewCtx);
        const parsed = ctx.get(parserCtx)(block);
        if (parsed) v.dispatch(v.state.tr.insert(v.state.doc.content.size, parsed.content));
      });
    },
    get scrollElement() { return root; },
    probe: () => drag.probe(),
    view,
  };
}
