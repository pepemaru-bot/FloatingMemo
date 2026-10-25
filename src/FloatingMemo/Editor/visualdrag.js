// Floating Memo — moving list items by drag and drop in the visual mode (Milkdown).
//
// The pointer side is the visual editor's own: a handle beside the hovered item, the drop line
// under the pointer, the level from how far the pointer moved sideways (as in the source mode:
// straight up or down keeps the level). The move itself is done on the visual editor's Markdown
// with the same rules as the source mode (listdrag.js: a parent moves with its children, levels
// that the list cannot have are refused) and given back to Milkdown as one change, so one Ctrl+Z
// undoes it.

import { listItems, allowedLevels, moveSpec } from './listdrag.js';
import { markdownState } from './mdpatch.js';

/** The move as Markdown: item `index` (document order) before item `target` or after its text. */
export function moveInMarkdown(md, index, target, after, level) {
  const state = markdownState(md);
  const items = listItems(state);
  const item = items[index], to = items[target];
  if (!item || !to) return null;
  let beforeLine = to.firstLine;
  if (target === index) {
    beforeLine = after ? item.lastLine + 1 : item.firstLine;   // same place: only the level can change
  } else if (after) {
    const next = items[target + 1];
    beforeLine = next && next.firstLine <= to.lastLine ? next.firstLine : to.lastLine + 1;
  }
  const { max } = allowedLevels(state, beforeLine, item);
  if (level > max) return null;               // the list cannot have this level here: refused
  const spec = moveSpec(state, item, beforeLine, level);
  return spec ? state.update(spec).state.doc.toString() : null;
}

export function itemCount(md) { return listItems(markdownState(md)).length; }

/**
 * Handles and drag-and-drop for the visual editor in `root` (the scrolling element).
 * api: view() (ProseMirror view), markdown(), replace(md) (one undoable change), active().
 */
export function attachListDrag(root, api) {
  const handle = document.createElement('div');
  handle.className = 'vd-handle';
  handle.textContent = '⠿';
  handle.setAttribute('aria-hidden', 'true');
  handle.hidden = true;
  root.appendChild(handle);
  let hovered = null;
  let drag = null;   // { index, li, startX, level, indicator, target, scroll }

  const items = () => [...root.querySelectorAll('.ProseMirror li')];
  const levelOf = (li) => { let n = 0; for (let p = li.parentElement; p && p !== root; p = p.parentElement) if (p.tagName === 'LI') n++; return n; };
  const firstBlock = (li) => [...li.children].find((c) => c.tagName !== 'UL' && c.tagName !== 'OL') || li;
  const indentPx = () => {
    const list = root.querySelector('.ProseMirror ul, .ProseMirror ol');
    return list ? parseFloat(getComputedStyle(list).paddingLeft) || 22 : 22;
  };
  const place = (el, rect) => {
    const box = root.getBoundingClientRect();
    return { top: rect.top - box.top + root.scrollTop, left: rect.left - box.left + root.scrollLeft };
  };

  root.addEventListener('mousemove', (e) => {
    if (drag || !api.active() || !api.view().editable) return;
    if (handle.contains(e.target)) return;   // on the handle: keep its item
    const li = e.target.closest && e.target.closest('.ProseMirror li');
    if (li === hovered && li) return;
    hovered = li;
    if (!li) { handle.hidden = true; return; }
    const r = firstBlock(li).getBoundingClientRect();
    const at = place(handle, { top: r.top, left: li.getBoundingClientRect().left });
    handle.style.top = `${at.top}px`;
    handle.style.left = `${Math.max(0, at.left - 34)}px`;
    handle.style.height = `${r.height}px`;
    handle.hidden = false;
  });
  root.addEventListener('mouseleave', () => { if (!drag) { handle.hidden = true; hovered = null; } });

  function targetAt(x, y) {
    const all = items().filter((li) => li === drag.li || !drag.li.contains(li));   // itself: a level change in place
    let best = null;
    for (const li of all) {
      const r = firstBlock(li).getBoundingClientRect();
      if (y >= r.top - 2 && y <= r.bottom + 2) { best = { li, r }; }
    }
    if (!best) return null;
    const after = y > best.r.top + best.r.height / 2;
    const steps = (x - drag.startX) / indentPx();
    const delta = Math.abs(steps) < 0.6 ? 0 : Math.round(steps);
    return { target: items().indexOf(best.li), li: best.li, after, rect: best.r, level: Math.max(0, drag.level + delta) };
  }

  function show(t) {
    const ind = drag.indicator;
    drag.target = null;
    if (!t) { ind.style.display = 'none'; return; }
    // Checked against the same rules as the drop, so what is shown is what happens.
    const md = api.markdown();
    if (!moveInMarkdown(md, drag.index, t.target, t.after, t.level)) { ind.style.display = 'none'; return; }
    const at = place(ind, { top: t.after ? t.rect.bottom : t.rect.top, left: t.li.getBoundingClientRect().left });
    ind.style.display = 'block';
    ind.style.top = `${at.top - 1}px`;
    ind.style.left = `${at.left + (t.level - levelOf(t.li)) * indentPx()}px`;
    drag.target = t;
  }

  let last = { x: 0, y: 0 };
  function onMove(e) { if (!drag) return; e.preventDefault(); last = { x: e.clientX, y: e.clientY }; show(targetAt(e.clientX, e.clientY)); }
  function end(drop, e) {
    if (!drag) return;
    clearInterval(drag.scroll);
    if (drop && e) show(targetAt(e.clientX, e.clientY));   // the release point decides
    const { index, target, indicator } = drag;
    indicator.remove();
    root.classList.remove('vd-dragging');
    drag = null;
    window.removeEventListener('mousemove', onMove, true);
    window.removeEventListener('mouseup', onUp, true);
    window.removeEventListener('keydown', onKey, true);
    if (drop && target) {
      const next = moveInMarkdown(api.markdown(), index, target.target, target.after, target.level);
      if (next !== null) api.replace(next);
    }
    api.view().focus();
  }
  function onUp(e) { if (drag) { e.preventDefault(); end(true, e); } }
  function onKey(e) { if (drag && e.key === 'Escape') { e.preventDefault(); e.stopPropagation(); end(false); } }

  handle.addEventListener('mousedown', (e) => {
    if (e.button !== 0 || !hovered || !api.active() || api.view().composing) return;
    const all = items();
    const index = all.indexOf(hovered);
    if (index < 0 || itemCount(api.markdown()) !== all.length) return;   // lists the page cannot match: no drag
    e.preventDefault();
    e.stopPropagation();
    const indicator = document.createElement('div');
    indicator.className = 'vd-drop-indicator';
    root.appendChild(indicator);
    drag = { index, li: hovered, startX: e.clientX, level: levelOf(hovered), indicator, target: null, scroll: 0 };
    root.classList.add('vd-dragging');
    last = { x: e.clientX, y: e.clientY };
    drag.scroll = setInterval(() => {
      if (!drag) return;
      const box = root.getBoundingClientRect();
      const step = last.y < box.top + 28 ? -14 : last.y > box.bottom - 28 ? 14 : 0;
      if (step) { root.scrollTop += step; show(targetAt(last.x, Math.max(box.top + 2, Math.min(box.bottom - 2, last.y)))); }
    }, 30);
    window.addEventListener('mousemove', onMove, true);
    window.addEventListener('mouseup', onUp, true);
    window.addEventListener('keydown', onKey, true);
  });

  return {
    // Tests: where the handles would be (item index and centre, page coordinates only).
    probe() {
      return items().map((li, i) => {
        const r = firstBlock(li).getBoundingClientRect();
        return { index: i, level: levelOf(li), x: Math.round(li.getBoundingClientRect().left - 26), y: Math.round(r.top + r.height / 2) };
      });
    },
  };
}
