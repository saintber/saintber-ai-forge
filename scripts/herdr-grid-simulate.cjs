#!/usr/bin/env node
'use strict';

// 離線模擬，驗證序列是否吻合：1 => 1:1 => 2:1 => 2:2 => 2:2:1 => 2:2:2 => 3:2:2 => 3:3:2 => 3:3:3
// 用真實幾何座標模擬 Herdr 的二元樹狀分割（而非只模擬欄/列計數），
// 因為先前版本只模擬計數邏輯，沒能驗出「未對齊寬 pane」造成的實際 bug。
const { reconstructColumns, describeGrid, findMisalignedWidePane, planNext } = require('./herdr-grid.cjs');

const TOTAL_W = 1000;
const TOTAL_H = 1000;

let panes = [{ pane_id: 'p1', rect: { x: 0, y: 0, width: TOTAL_W, height: TOTAL_H } }];
let seq = 2;

// 模擬 herdr pane split：對目標 pane 依方向與 ratio 切成兩塊。
function simulateSplit(panes, targetPaneId, direction, ratio) {
  const idx = panes.findIndex((p) => p.pane_id === targetPaneId);
  const target = panes[idx];
  const newId = `p${seq++}`;
  let updatedTarget, newPane;

  if (direction === 'right') {
    const leftW = target.rect.width * ratio;
    updatedTarget = { ...target, rect: { ...target.rect, width: leftW } };
    newPane = {
      pane_id: newId,
      rect: { x: target.rect.x + leftW, y: target.rect.y, width: target.rect.width - leftW, height: target.rect.height },
    };
  } else {
    const topH = target.rect.height * ratio;
    updatedTarget = { ...target, rect: { ...target.rect, height: topH } };
    newPane = {
      pane_id: newId,
      rect: { x: target.rect.x, y: target.rect.y + topH, width: target.rect.width, height: target.rect.height - topH },
    };
  }

  const result = [...panes];
  result[idx] = updatedTarget;
  result.push(newPane);
  return { panes: result, newPaneId: newId };
}

// 目標序列：每個都必須在某次操作後被「達成過」，操作次數不限（realign 不算數，只是修正過程）。
const expected = ['1:1', '2:1', '2:2', '2:2:1', '2:2:2', '3:2:2', '3:3:2', '3:3:3'];
let expectedIdx = 0;
let failed = false;
let stepGuard = 0;

while (expectedIdx < expected.length) {
  stepGuard++;
  if (stepGuard > 50) {
    console.log('[FAIL] 超過安全步數上限，可能陷入無窮迴圈');
    failed = true;
    break;
  }

  const next = planNext(panes);
  const sim = simulateSplit(panes, next.targetPaneId, next.direction, next.ratio);
  panes = sim.panes;

  const columns = reconstructColumns(panes);
  const got = describeGrid(columns);
  const misalignedAfter = findMisalignedWidePane(panes);

  const tag = next.mode === 'realign' ? '(對齊修正)' : next.mode === 'add-column' ? '(新增欄)' : '(新增列)';
  console.log(`${tag} target=${next.targetPaneId} dir=${next.direction} -> 目前版面=${got}` + (misalignedAfter ? ' [尚未對齊]' : ''));

  if (next.mode === 'realign' && misalignedAfter) {
    console.log(`  [FAIL] 對齊後仍偵測到未對齊寬 pane: ${misalignedAfter.targetPaneId}`);
    failed = true;
    break;
  }

  // 不論這步是 realign 還是 add，只要版面剛好等於目前排隊的 expected 目標就算達成一項。
  // 對齊步驟本身有時會直接補完成上一個 add 尚未達成的完整版面。
  while (expectedIdx < expected.length && got === expected[expectedIdx]) {
    console.log(`  [MATCH] 達成 expected[${expectedIdx}] = ${expected[expectedIdx]}`);
    expectedIdx++;
  }
}

if (expectedIdx < expected.length) {
  console.log(`[FAIL] 迴圈結束但仍有未達成的 expected 項目: ${expected.slice(expectedIdx).join(', ')}`);
  failed = true;
}

console.log(failed ? '\n整體結果：FAIL' : '\n整體結果：OK，全序列吻合且每步皆無殘留未對齊 pane');
process.exitCode = failed ? 1 : 0;
