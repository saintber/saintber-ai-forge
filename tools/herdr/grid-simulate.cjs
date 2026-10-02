#!/usr/bin/env node
'use strict';

// 離線模擬，驗證 computeNext 的欄:列序列：
//   1 => 1:1 => 2:1 => 2:2 => 2:2:1 => 2:2:2 => 3:2:2 => 3:3:2 => 3:3:3 => 3:3:3:1 => ...
// add-column 的實際 herdr 操作（搬出/切欄/搬回）結果等同「在最右側新增一欄，且舊欄列數不變」，
// 所以這裡只以欄/列結構模擬；列高獨立性需用真實 herdr 實測。
const { computeNext, describeGrid } = require('./grid.cjs');

const expected = ['1:1', '2:1', '2:2', '2:2:1', '2:2:2', '3:2:2', '3:3:2', '3:3:3', '3:3:3:1', '3:3:3:2', '3:3:3:3'];

let columns = [['p1']];
let seq = 2;
let failed = false;

for (const want of expected) {
  const next = computeNext(columns);
  const id = `p${seq++}`;
  if (next.mode === 'A') {
    columns = [...columns, [id]];
  } else {
    const idx = columns.findIndex((c) => c[c.length - 1] === next.targetPaneId);
    columns = columns.map((c, i) => (i === idx ? [...c, id] : c));
  }
  const got = describeGrid(columns);
  const ok = got === want;
  if (!ok) failed = true;
  console.log(`${ok ? 'OK  ' : 'FAIL'} 視窗${seq - 1}: ${next.mode === 'A' ? '新增欄' : '新增列'} -> ${got}${ok ? '' : `（預期 ${want}）`}`);
}

console.log(failed ? '\n整體結果：FAIL' : '\n整體結果：OK，序列吻合');
process.exitCode = failed ? 1 : 0;
