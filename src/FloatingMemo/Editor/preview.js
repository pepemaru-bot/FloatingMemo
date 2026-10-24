// Floating Memo — read-only preview of a note (the "プレビュー" mode of the source editor).
//
// The Markdown is parsed (micromark + GFM, the same parser family as the visual mode) into a
// syntax tree and the page is built from it with createElement / textContent only:
// - no innerHTML, so nothing in a note can become markup or script; raw HTML in a note is
//   shown as text;
// - images are never loaded (their alt text is shown instead), nothing touches the network;
// - links are inert elements: a click asks the host, which shows the address before opening;
// - task-list checkboxes are disabled: the preview never changes the note.
// Rendering only reads the text; the note itself is never re-serialized.

import { fromMarkdown } from './visual-vendor/mdast-util-from-markdown/index.js';
import { gfm } from './visual-vendor/micromark-extension-gfm/index.js';
import { gfmFromMarkdown } from './visual-vendor/mdast-util-gfm/index.js';

const SAFE_URL = /^(https?:\/\/[^\s<>"]+|mailto:[^\s<>"]+)$/i;

function el(tag, className, text) {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text !== undefined) node.textContent = text;
  return node;
}

/** Builds the preview of `text` into `container` (its previous content is replaced). */
export function renderPreview(container, text) {
  const tree = fromMarkdown(text, { extensions: [gfm()], mdastExtensions: [gfmFromMarkdown()] });
  const definitions = new Map();
  const footnotes = [];
  for (const node of tree.children) {
    if (node.type === 'definition') definitions.set(node.identifier, node.url);
    if (node.type === 'footnoteDefinition') footnotes.push(node);
  }

  const link = (url, children, ctx) => {
    if (!url || !SAFE_URL.test(url)) {
      // Not an address the app would open (relative path, javascript:, file:...): text only.
      const span = el('span', 'pv-link-inert');
      children.forEach((c) => render(c, span, ctx));
      return span;
    }
    const a = el('a', 'pv-link');
    a.dataset.url = url;
    a.title = url;
    a.tabIndex = 0;
    a.setAttribute('role', 'link');
    children.forEach((c) => render(c, a, ctx));
    return a;
  };

  function renderAll(nodes, parent, ctx) {
    for (const n of nodes) render(n, parent, ctx);
  }

  function render(node, parent, ctx) {
    switch (node.type) {
      case 'paragraph': {
        if (ctx && ctx.tight) { renderAll(node.children, parent, ctx); break; }
        const p = el('p');
        renderAll(node.children, p, ctx);
        parent.appendChild(p);
        break;
      }
      case 'heading': {
        const h = el(`h${Math.min(6, Math.max(1, node.depth))}`);
        renderAll(node.children, h, ctx);
        parent.appendChild(h);
        break;
      }
      case 'thematicBreak':
        parent.appendChild(el('hr'));
        break;
      case 'blockquote': {
        const q = el('blockquote');
        renderAll(node.children, q, null);
        parent.appendChild(q);
        break;
      }
      case 'list': {
        const list = el(node.ordered ? 'ol' : 'ul');
        if (node.ordered && Number.isInteger(node.start) && node.start !== 1) list.start = node.start;
        if (node.children.some((i) => typeof i.checked === 'boolean')) list.className = 'pv-tasks';
        const tight = !node.spread && !node.children.some((i) => i.spread);
        for (const item of node.children) {
          const li = el('li');
          if (typeof item.checked === 'boolean') {
            li.className = 'pv-task';
            const box = el('input');
            box.type = 'checkbox';
            box.checked = item.checked;
            box.disabled = true;      // display only: the note is never changed from here
            box.tabIndex = -1;
            box.setAttribute('aria-label', item.checked ? '完了' : '未完了');
            li.appendChild(box);
          }
          // In a tight list the first paragraph flows inline; nested lists and other blocks follow.
          for (const child of item.children) {
            if (child.type === 'paragraph' && tight) {
              const span = el('span');
              renderAll(child.children, span, null);
              li.appendChild(span);
            } else {
              render(child, li, null);
            }
          }
          list.appendChild(li);
        }
        parent.appendChild(list);
        break;
      }
      case 'code': {
        const pre = el('pre', 'pv-code');
        const code = el('code', null, node.value);
        if (node.lang) pre.dataset.lang = node.lang.slice(0, 40);
        pre.appendChild(code);
        parent.appendChild(pre);
        break;
      }
      case 'html':
        // Raw HTML is shown as written, never interpreted.
        parent.appendChild(el(parent.tagName === 'DIV' || parent.tagName === 'BLOCKQUOTE' || parent.tagName === 'LI' ? 'pre' : 'code', 'pv-raw', node.value));
        break;
      case 'table': {
        const wrap = el('div', 'pv-table');   // only the table scrolls sideways
        const table = el('table');
        const align = node.align || [];
        node.children.forEach((row, r) => {
          const tr = el('tr');
          row.children.forEach((cell, c) => {
            const td = el(r === 0 ? 'th' : 'td');
            if (align[c]) td.style.textAlign = align[c];
            renderAll(cell.children, td, null);
            tr.appendChild(td);
          });
          if (r === 0) {
            const thead = el('thead');
            thead.appendChild(tr);
            table.appendChild(thead);
          } else {
            let tbody = table.tBodies[0];
            if (!tbody) { tbody = el('tbody'); table.appendChild(tbody); }
            tbody.appendChild(tr);
          }
        });
        wrap.appendChild(table);
        parent.appendChild(wrap);
        break;
      }
      case 'definition':
      case 'footnoteDefinition':
        break;    // shown where referenced / at the end
      case 'text':
        parent.appendChild(document.createTextNode(node.value));
        break;
      case 'strong':
      case 'emphasis':
      case 'delete': {
        const tag = { strong: 'strong', emphasis: 'em', delete: 'del' }[node.type];
        const e = el(tag);
        renderAll(node.children, e, ctx);
        parent.appendChild(e);
        break;
      }
      case 'inlineCode':
        parent.appendChild(el('code', 'pv-inline-code', node.value));
        break;
      case 'break':
        parent.appendChild(el('br'));
        break;
      case 'link':
        parent.appendChild(link(node.url, node.children, ctx));
        break;
      case 'linkReference': {
        const url = definitions.get(node.identifier);
        if (url !== undefined) parent.appendChild(link(url, node.children, ctx));
        else parent.appendChild(document.createTextNode(`[${node.label || node.identifier}]`));
        break;
      }
      case 'image':
      case 'imageReference':
        // Never loaded (no network, no local files): the description stands in for it.
        parent.appendChild(el('span', 'pv-image', `［画像${node.alt ? `: ${node.alt}` : ''}］`));
        break;
      case 'footnoteReference':
        parent.appendChild(el('sup', 'pv-footnote-ref', `[${node.label || node.identifier}]`));
        break;
      default:
        if (Array.isArray(node.children)) renderAll(node.children, parent, ctx);
        else if (typeof node.value === 'string') parent.appendChild(document.createTextNode(node.value));
        break;
    }
  }

  const root = el('div', 'pv-body');
  renderAll(tree.children, root, null);
  if (footnotes.length) {
    const section = el('section', 'pv-footnotes');
    section.appendChild(el('hr'));
    const ol = el('ol');
    for (const f of footnotes) {
      const li = el('li');
      li.appendChild(el('span', 'pv-footnote-label', `[${f.label || f.identifier}] `));
      renderAll(f.children, li, { tight: true });
      ol.appendChild(li);
    }
    section.appendChild(ol);
    root.appendChild(section);
  }
  container.replaceChildren(root);
}

export const isSafeUrl = (url) => SAFE_URL.test(url);
