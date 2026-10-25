// Browser tests for the editor's pure parts: mdpatch.js (visual edits keep the note's own
// Markdown) and the Markdown side of list moves (visualdrag.js). The "before"/"after" texts are
// what the visual editor (Milkdown) writes, captured from the app. Made-up notes only.

import { patch, sameContent } from '../../src/FloatingMemo/Editor/mdpatch.js';
import { moveInMarkdown } from '../../src/FloatingMemo/Editor/visualdrag.js';

const results = [];
function eq(name, actual, expected) {
  results.push({ name, ok: actual === expected, actual, expected });
}
const fix = (o, a, b) => patch(o, a, b, (t) => sameContent(t, b));

// --- Notes in their own style; Milkdown's output for them ("before") ---
const tabs = '# 架空の会議\n\n- 親A\n\t- 子A1\n\t\t- 孫\n\t- 子A2\n- 親B\n\n- [ ] 未完了\n- [x] 完了\n';
const tabsMd = '# 架空の会議\n\n- 親A\n  - 子A1\n    - 孫\n  - 子A2\n\n- 親B\n\n- [ ] 未完了\n\n- [x] 完了\n';
const stars = '* 星の箇条書き\n* 二つ目\n\n段落の後ろに空白  \n次の行\n\n| 項目 | 内容 |\n|---|---|\n| a | b |\n';
const starsMd = '- 星の箇条書き\n- 二つ目\n\n段落の後ろに空白\\\n次の行\n\n| 項目 | 内容 |\n| -- | -- |\n| a  | b  |\n';

eq('edit in a tab-indented list keeps every other byte',
  fix(tabs, tabsMd, tabsMd.replace('  - 子A2', '  - 子A2追記')),
  tabs.replace('\t- 子A2', '\t- 子A2追記'));
eq('edit on one line keeps * bullets and the two-space line break',
  fix(stars, starsMd, starsMd.replace('次の行', '次の行X')),
  stars.replace('次の行', '次の行X'));
eq('table cell edit keeps the note\'s padding',
  fix(stars, starsMd, starsMd.replace('| a  | b  |', '| a  | bZ |')),
  stars.replace('| a | b |', '| a | bZ |'));
eq('table header edit leaves the other rows alone',
  fix(stars, starsMd, starsMd.replace('| 項目 | 内容 |\n| -- | -- |\n| a  | b  |', '| 項目名 | 内容 |\n| --- | -- |\n| a   | b  |')),
  stars.replace('| 項目 |', '| 項目名 |'));
eq('a new nested item is indented with tabs like the note',
  fix(tabs, tabsMd, tabsMd.replace('  - 子A1\n    - 孫', '  - 子A1\n    - 新しい項目\n      - 孫')),
  tabs.replace('\t- 子A1\n\t\t- 孫', '\t- 子A1\n\t\t- 新しい項目\n\t\t\t- 孫'));
eq('a new sibling and a new task at the end',
  fix(tabs, tabsMd, tabsMd.replace('  - 子A2\n', '  - 子A2\n  - 子A3\n') + '\n- [ ] 新タスク\n'),
  tabs.replace('\t- 子A2\n', '\t- 子A2\n\t- 子A3\n') + '\n- [ ] 新タスク\n');
const flat = '- 親A\n\t- 子A1\n\t- 子A2\n- 親B\n';
const flatMd = '- 親A\n  - 子A1\n  - 子A2\n- 親B\n';
eq('an item moved one level up in place', fix(flat, flatMd, '- 親A\n  - 子A1\n- 子A2\n- 親B\n'), '- 親A\n\t- 子A1\n- 子A2\n- 親B\n');
eq('an item moved one level down in place', fix(flat, flatMd, '- 親A\n  - 子A1\n    - 子A2\n- 親B\n'), '- 親A\n\t- 子A1\n\t\t- 子A2\n- 親B\n');
eq('formatting-only differences leave the note as it is', fix(stars, starsMd, starsMd.replace('| a  | b  |', '| a | b |')), stars);
eq('nothing changed: the same text', fix(tabs, tabsMd, tabsMd), tabs);
const crlf = '* 一\r\n* 二\r\n\r\n本文\r\n';
eq('CRLF notes keep CRLF', fix(crlf, '- 一\n- 二\n\n本文\n', '- 一\n- 二\n\n本文です\n'), '* 一\r\n* 二\r\n\r\n本文です\r\n');
eq('task box toggled', fix(tabs, tabsMd, tabsMd.replace('- [ ] 未完了', '- [x] 未完了')), tabs.replace('- [ ] 未完了', '- [x] 未完了'));

eq('an edit on a "1)" item keeps its marker', fix('見出し\n===\n\n1) 括弧つき番号\n', '# 見出し\n\n1. 括弧つき番号\n', '# 見出し\n\n1. 括弧つき番号追記\n'), '見出し\n===\n\n1) 括弧つき番号追記\n');

// --- What counts as "the same content" ---
eq('same content: tabs vs spaces, bullets, padding', sameContent(tabs, tabsMd) && sameContent(stars, starsMd), true);
eq('different content: a reference link written inline', sameContent('[a][r]\n\n[r]: https://example.com\n', '[a](https://example.com)\n'), false);
eq('different content: a changed word', sameContent('- 一\n', '- 二\n'), false);

// --- List moves on Markdown (visual drag) ---
eq('vertical move keeps the level', moveInMarkdown(flatMd, 2, 1, false, 1), '- 親A\n  - 子A2\n  - 子A1\n- 親B\n');
eq('a parent moves with its children', moveInMarkdown('- 甲\n  - 甲1\n    - 甲11\n- 乙\n', 0, 3, true, 0), '- 乙\n- 甲\n  - 甲1\n    - 甲11\n');
eq('level change in place (left)', moveInMarkdown(flatMd, 2, 2, false, 0), '- 親A\n  - 子A1\n- 子A2\n- 親B\n');
eq('a level the list cannot have is refused', moveInMarkdown(flatMd, 3, 0, false, 2), null);

const out = document.getElementById('out');
const failed = results.filter((r) => !r.ok);
out.className = failed.length ? 'fail' : 'pass';
out.textContent = `${results.length - failed.length}/${results.length} passed\n\n` + results.map((r) => `${r.ok ? 'PASS' : 'FAIL'}  ${r.name}${r.ok ? '' : `\n    expected: ${JSON.stringify(r.expected)}\n    actual:   ${JSON.stringify(r.actual)}`}`).join('\n');
window.testResults = { passed: results.length - failed.length, failed: failed.length };
