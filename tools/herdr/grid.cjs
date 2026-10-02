#!/usr/bin/env node
'use strict';

/**
 * tools/herdr/grid.cjs — 依照「橫向優先擴張、縱向次之、左先右後」規則，
 * 在指定 Herdr workspace/tab 內新增下一個 pane。
 *
 * 目標序列（欄:欄:欄，每個數字＝該欄的列數）：
 *   1 => 1:1 => 2:1 => 2:2 => 2:2:1 => 2:2:2 => 3:2:2 => 3:3:2 => 3:3:3 => ...
 *
 * 重要概念（Herdr 分割是二元樹狀分割，不是表格）：
 *   若新增一欄時直接讓新 pane 橫跨所有列，或同欄的 down-split 同時涵蓋新舊兩欄，
 *   調整某欄列高時鄰欄會被一起拉動（列高耦合）。本腳本讓「每次呼叫只新增一個 pane」，
 *   並逐列長出新欄（2:2 -> 2:2:1 -> 2:2:2），同時保證每欄的 down-split 只涵蓋自己那一欄：
 *     add-column  最後一欄第一列往右切，新欄只長在第一列；該欄其餘列暫時是橫跨新舊兩欄的寬 pane。
 *     fill        補齊下一列：把第一個寬 pane 搬到暫存 tab（herdr pane move 在同 tab 內無效，
 *                 reason=same_tab），再搬回同欄上一列下方（此時它只佔舊欄寬度），
 *                 最後在新欄最後一個 pane 往下切出新 pane，與剛搬回的 pane 同列。
 *     add-row     列數最少且最靠左的欄，最下面 pane 往下切。
 *   注意：只保證之後新增的欄不耦合，不會修復舊版本已建立的 tab。
 *
 * 重新排序：版面符合本腳本結構（欄列數等於 canonicalShape(pane 數)）時，新 pane 排在最後，
 *   既有 pane 維持目前閱讀順序（逐列、再逐欄），再用 pane swap 重新排成逐列順序
 *   （例如 5 個 pane：第一列 1,2,3、第二列 4,5）。版面不符則略過；--no-reorder 可關閉。
 *
 * 決策規則：新增後「欄數與最大列數」差值較小者勝，平手優先新增欄。
 *
 * 用法（ID 或名稱皆可混用，見下方解析規則）：
 *   node tools/herdr/grid.cjs layout   --tab w1:t1
 *   node tools/herdr/grid.cjs layout   --tab Company --workspace Main
 *   node tools/herdr/grid.cjs layout   --tab Main:Company
 *   node tools/herdr/grid.cjs next     --tab w1:t1
 *   node tools/herdr/grid.cjs split    --tab w1:t1 --cwd "C:\path\to\worktree" [--name <pane名稱>] [--focus] [--no-reorder] [--no-equalize] [--dry-run]
 *   node tools/herdr/grid.cjs equalize --tab w1:t1 [--dry-run]
 *
 * 平均分配（equalize）：split 完成新增後，預設會自動把「目前所有 pane」的寬高
 * 調整為精確平均分配（例如 2:2:1 新增後不會維持切割當下的固定比例，而是讓 3 欄寬度
 * 平均、各欄內的列高度也平均）。原理是 herdr pane resize 的 --amount 直接加/減在該
 * 切割線的 ratio 上，且是精確線性運算，所以可以一次算出每條切割線「目標 ratio - 目前
 * ratio」的差值並直接命中，不需要反覆試探。傳 --no-equalize 可跳過這個自動平均步驟，
 * 只做新增本身；也可以單獨呼叫 `equalize` 子指令，隨時把現有版面重新調整為平均。
 *
 * workspace/tab 解析規則（ID 與顯示名稱皆可，AI 建議用 ID 較精確，人類慣用名稱）：
 *   - --workspace 可為 workspace_id（如 w1）或其顯示名稱 label（如 Main），比對時 id 優先、
 *     找不到才退而比對 label（大小寫不拘）。
 *   - --tab 支援三種寫法：
 *       1. 完整 tab id："<workspace_id>:<tab_id>"（如 w1:t1）
 *       2. "<workspace 名稱或id>:<tab 名稱或id>"（如 Main:Company），冒號前後分別解析。
 *       3. 純 tab 名稱或編號（如 Company 或 1），此時必須另外提供 --workspace 才能定位。
 *     tab 端的比對順序：tab_id 完全相符 → "<workspace_id>:<token>" 相符（處理只給 t1 這種
 *     半形式）→ label 完全相符（大小寫不拘）→ tab 顯示編號 number 相符。
 *   - 只給 --tab：workspace 從 --tab 冒號前半部解析（若 --tab 沒有冒號，則此規則不適用，見下）。
 *   - 只給 --workspace（不給 --tab）：用該 workspace 目前 active_tab_id，
 *     若查無 active tab 則退而使用該 workspace 下第一個 tab。
 *   - 兩者都給：解析後的 workspace 必須一致，不符則報錯，不會靜默忽略。
 *   - 任何一段解析失敗時，錯誤訊息會明確指出是「workspace 不存在」或「tab 不存在」，並列出
 *     該範圍內現有的候選（id + 名稱），不會將 tab 找不到誤報成 pane 找不到。
 *
 * split 之後，仍需自行呼叫 `herdr agent start` / `herdr agent prompt` 啟動並派工，
 * 這支 script 只負責「版面新增」這一步。
 */

const { execFileSync } = require('child_process');

function herdr(args) {
  const out = execFileSync('herdr', args, { encoding: 'utf8', maxBuffer: 16 * 1024 * 1024 });
  return JSON.parse(out);
}

function parseArgs(argv) {
  const args = { _: [] };
  for (let i = 0; i < argv.length; i++) {
    const a = argv[i];
    if (a.startsWith('--')) {
      const key = a.slice(2);
      const next = argv[i + 1];
      if (next === undefined || next.startsWith('--')) {
        args[key] = true;
      } else {
        args[key] = next;
        i++;
      }
    } else {
      args._.push(a);
    }
  }
  return args;
}

// 依 id 或 label（大小寫不拘）解析出 workspace 物件。找不到時列出現有候選。
function resolveWorkspace(token) {
  const wsList = herdr(['workspace', 'list']).result.workspaces;
  let ws = wsList.find((w) => w.workspace_id === token);
  if (!ws) {
    ws = wsList.find((w) => (w.label ?? '').toLowerCase() === token.toLowerCase());
  }
  if (!ws) {
    const known = wsList.map((w) => `${w.workspace_id}(${w.label ?? '無名稱'})`).join(', ');
    throw new Error(`workspace 不存在：${token}（現有 workspace：${known || '無'}）`);
  }
  return ws;
}

// 在已知 workspaceId 底下，依 id／label／顯示編號解析出 tab 物件。找不到時列出現有候選。
// 比對順序：完整 tab_id 相符 -> "<workspaceId>:<token>" 相符（處理如 "t1" 這種半形式）
//           -> label 相符（大小寫不拘） -> 顯示編號 number 相符。
function resolveTabInWorkspace(workspaceId, token) {
  const tabs = herdr(['tab', 'list', '--workspace', workspaceId]).result.tabs;
  let tab = tabs.find((t) => t.tab_id === token);
  if (!tab) {
    tab = tabs.find((t) => t.tab_id === `${workspaceId}:${token}`);
  }
  if (!tab) {
    tab = tabs.find((t) => (t.label ?? '').toLowerCase() === token.toLowerCase());
  }
  if (!tab) {
    tab = tabs.find((t) => String(t.number) === String(token));
  }
  if (!tab) {
    const known = tabs.map((t) => `${t.tab_id}(${t.label ?? t.number})`).join(', ');
    throw new Error(`tab 不存在：${token}（workspace ${workspaceId} 底下現有 tab：${known || '無'}）`);
  }
  return tab;
}

// 解析出最終要用的 { workspaceId, tabId }。--workspace / --tab 皆可用 id 或名稱，並可混用：
//   - --tab 含冒號："<workspace token>:<tab token>"，兩段各自可為 id 或名稱。
//   - --tab 不含冒號：視為純 tab token，此時必須另外提供 --workspace 才能定位。
//   - 只給 --workspace：使用該 workspace 的 active_tab_id，查無則退而用該 workspace 第一個 tab。
//   - --tab 與 --workspace 都給：兩者解析出的 workspace 必須一致，不符則報錯。
function resolveWorkspaceAndTab(args) {
  if (args.tab) {
    const colonIdx = args.tab.indexOf(':');
    let wsToken, tabToken;
    if (colonIdx > 0) {
      wsToken = args.tab.slice(0, colonIdx);
      tabToken = args.tab.slice(colonIdx + 1);
    } else {
      if (!args.workspace) {
        throw new Error(
          `--tab "${args.tab}" 未包含 workspace 前綴（如 w1:${args.tab}），請另外提供 --workspace <id或名稱>`
        );
      }
      wsToken = args.workspace;
      tabToken = args.tab;
    }

    const ws = resolveWorkspace(wsToken);

    if (colonIdx > 0 && args.workspace) {
      const wsFromArg = resolveWorkspace(args.workspace);
      if (wsFromArg.workspace_id !== ws.workspace_id) {
        throw new Error(
          `--workspace ${args.workspace}(${wsFromArg.workspace_id}) 與 --tab ${args.tab} 所屬的 workspace ${ws.workspace_id} 不相符`
        );
      }
    }

    const tab = resolveTabInWorkspace(ws.workspace_id, tabToken);
    return { workspaceId: ws.workspace_id, tabId: tab.tab_id };
  }

  if (!args.workspace) {
    throw new Error('必須提供 --tab <workspace(id或名稱)>:<tab(id或名稱)>，或至少提供 --workspace <id或名稱>');
  }

  const ws = resolveWorkspace(args.workspace);
  if (ws.active_tab_id) {
    return { workspaceId: ws.workspace_id, tabId: ws.active_tab_id };
  }
  const tabs = herdr(['tab', 'list', '--workspace', ws.workspace_id]).result.tabs;
  if (tabs.length === 0) {
    throw new Error(`workspace ${ws.workspace_id}(${ws.label ?? '無名稱'}) 底下沒有任何 tab`);
  }
  return { workspaceId: ws.workspace_id, tabId: tabs[0].tab_id };
}

// 找出 tab 內任一 pane，用來查詢完整版面（layout API 需要一個 pane id 起點）。
// 呼叫時 tabId 已由 resolveWorkspaceAndTab 驗證存在，這裡只處理「tab 存在但查無 pane」的例外情況。
function anyPaneInTab(workspaceId, tabId) {
  const res = herdr(['pane', 'list', '--workspace', workspaceId]);
  const panes = res.result.panes.filter((p) => p.tab_id === tabId);
  if (panes.length === 0) {
    // 正常情況下每個存在的 tab 至少會有一個 root pane；若真的查不到，代表 pane_list 與 tab
    // 兩個 API 之間有不一致（例如 pane 剛好在切換中），屬於例外狀況而非「使用者打錯 tab」。
    throw new Error(`tab ${tabId} 已存在，但 pane 清單目前查不到任何 pane（請重試一次，可能是狀態尚未同步）`);
  }
  return panes;
}

// 依 rect 的 x 座標分群成欄，欄內依 y 排序成列（由上到下）。
function reconstructColumns(layoutPanes) {
  const sorted = [...layoutPanes].sort((a, b) => a.rect.x - b.rect.x);
  const totalWidth = Math.max(...layoutPanes.map((p) => p.rect.x + p.rect.width));
  const tolerance = Math.max(1, totalWidth * 0.01);

  const columns = [];
  for (const p of sorted) {
    let col = columns.find((c) => Math.abs(c.x - p.rect.x) <= tolerance);
    if (!col) {
      col = { x: p.rect.x, panes: [] };
      columns.push(col);
    }
    col.panes.push(p);
  }
  columns.sort((a, b) => a.x - b.x);
  for (const col of columns) {
    col.panes.sort((a, b) => a.rect.y - b.rect.y);
  }
  return columns.map((c) => c.panes.map((p) => p.pane_id));
}

function fetchLayoutPanes(args) {
  const { workspaceId, tabId } = resolveWorkspaceAndTab(args);
  const panesInTab = anyPaneInTab(workspaceId, tabId);
  const layout = herdr(['pane', 'layout', '--pane', panesInTab[0].pane_id]).result.layout;
  return layout.panes;
}

function fetchColumns(args) {
  return reconstructColumns(fetchLayoutPanes(args));
}

// 找出所有欄位的起始 x 座標（去除誤差重複），由小到大排序。
function getColumnStarts(layoutPanes) {
  const tolerance = Math.max(1, Math.max(...layoutPanes.map((p) => p.rect.x + p.rect.width)) * 0.01);
  const xs = [];
  for (const p of layoutPanes) {
    if (!xs.some((x) => Math.abs(x - p.rect.x) <= tolerance)) xs.push(p.rect.x);
  }
  xs.sort((a, b) => a - b);
  return { xs, tolerance };
}

// --- 以下用於精確選出 herdr pane resize 的目標 pane（實測驗證過的行為）---
//
// 實測確認 `herdr pane resize --pane P --direction D` 的完整行為：
//   從 P 開始沿著分割樹往上走（geometry 上就是往外一層層找包住 P 的切割矩形）。
//   每經過一層祖先節點 A，只有在「P 位於 A 的『D 所指方向的來源側』」時才會命中 A：
//     - direction=right 只認 A 的左半邊（把左半邊往右推，變寬）
//     - direction=left  只認 A 的右半邊（把右半邊往左推，讓左半邊變窄）
//     - direction=down  只認 A 的上半邊
//     - direction=up    只認 A 的下半邊
//   若 P 位於 A 的「另一側」（例如 direction=right 但 P 在 A 右半邊），A 對這次 resize
//   是透明的，會繼續往上找更外層的祖先，直到找到一個「P 位於正確側」的節點才真正命中並調整它。
//   （這與方向是否相同無關：即使 A 與 D 同為水平方向，只要 P 在錯誤側，A 一樣會被跳過。）
//
// 所以要精確命中某條切割線 S（方向 D），必須挑一個 pane P，使得：
//   1. P 位於 S 的正確側（direction=right/down 就要在 S 的左/上分區；left/up 則相反 —
//      呼叫端只需準備兩種方向其中一種能命中 S 即可，我們固定選「S.direction 對應的來源側」）。
//   2. 從 P 往上走到 S 的路徑上，途中不能有任何節點會「先以同一 direction 命中」——也就是
//      沿路每一層祖先 A'，若 P 相對 A' 也剛好在 A'.direction 的正確側，就會先命中 A' 而非 S。
// 做法：從 S 的「正確側」分區開始往下遞迴；若該分區本身又是一個切割節點 A'：
//   - 若 A'.direction === S.direction → A' 的兩個子分區中，「正確側」那個子分區會被
//     A' 攔截（往上會先命中 A'，不能用），但「錯誤側」那個子分區對 A' 是透明的，
//     可以繼續往下找（因為對 A' 而言方向不合，會跳過 A' 直接命中更外層的 S）。
//   - 若 A'.direction !== S.direction（垂直方向）→ 兩個子分區都對 A' 透明（A' 是另一
//     個方向的分割，不會攔截 D 方向的 resize），兩側都可以繼續往下找。
//   若分區沒有對應到任何切割節點 → 是葉節點（單一 pane），直接回傳該 pane。

function rectsEqual(a, b, tol) {
  return (
    Math.abs(a.x - b.x) <= tol &&
    Math.abs(a.y - b.y) <= tol &&
    Math.abs(a.width - b.width) <= tol &&
    Math.abs(a.height - b.height) <= tol
  );
}

function rectContains(outer, inner, tol) {
  return (
    inner.x >= outer.x - tol &&
    inner.y >= outer.y - tol &&
    inner.x + inner.width <= outer.x + outer.width + tol &&
    inner.y + inner.height <= outer.y + outer.height + tol
  );
}

function partitionRects(split) {
  const { x, y, width, height } = split.rect;
  if (split.direction === 'right') {
    const w1 = width * split.ratio;
    return [
      { x, y, width: w1, height },
      { x: x + w1, y, width: width - w1, height },
    ];
  }
  const h1 = height * split.ratio;
  return [
    { x, y, width, height: h1 },
    { x, y: y + h1, width, height: height - h1 },
  ];
}

// resize --direction D 的軸向：right/left 屬水平軸（對應 split.direction==='right'）、
// up/down 屬垂直軸（對應 split.direction==='down'）。
function axisOf(resizeDirection) {
  return resizeDirection === 'right' || resizeDirection === 'left' ? 'right' : 'down';
}

// 「來源側」規則：resize --direction D 只認得分割節點的哪一個分區（partitionRects 回傳
// [區0, 區1]，right/down 的區0=左/上，區1=右/下）。這是相對於「實際呼叫的方向 D」而定，
// 同一個分割節點若呼叫 right 認區0、呼叫 left 就認區1 —— 兩者是相反的。
//   right -> 區0（左半）  down -> 區0（上半）
//   left  -> 區1（右半）  up   -> 區1（下半）
function sourceRegionIndex(resizeDirection) {
  return resizeDirection === 'right' || resizeDirection === 'down' ? 0 : 1;
}

// 在指定矩形範圍內，遞迴找出一個 pane，使得「從它往上走、呼叫 resize(--direction
// resizeDirection)」會先命中我們期望的祖先分割，而不是被路徑上其他節點攔截。
//   rect: 目前檢視的矩形範圍
//   resizeDirection: 最終要送給 herdr pane resize 的實際方向（left/right/up/down 之一，
//                     全程固定不變，不是 split.direction）
// 規則（矩形對應到另一個分割節點 A' 時）：
//   - 若 A' 與 resizeDirection 同軸（A'.direction==='right' 對應 left/right；
//     'down' 對應 up/down）：A' 的『正確側(來源側)』分區會被 A' 自己攔截（此路不通），
//     只有『錯誤側』分區對 A' 透明，可以繼續往下找。
//   - 若 A' 與 resizeDirection 不同軸：A' 對這個方向完全透明，兩側分區都可以往下找。
//   若矩形沒有對應到任何分割節點，代表是葉節點（單一 pane），直接回傳它。
function findRefPaneInRect(rect, resizeDirection, splits, panes, tol) {
  const matchSplit = splits.find((s) => rectsEqual(s.rect, rect, tol));
  if (!matchSplit) {
    const p = panes.find((p) => rectContains(rect, p.rect, tol));
    return p ? p.pane_id : null;
  }

  const [r0, r1] = partitionRects(matchSplit);
  if (matchSplit.direction === axisOf(resizeDirection)) {
    // 同軸：正確側(來源側)會被 matchSplit 自己攔截，只有錯誤側對 matchSplit 透明可以往下走。
    const wrongRegion = sourceRegionIndex(resizeDirection) === 0 ? r1 : r0;
    return findRefPaneInRect(wrongRegion, resizeDirection, splits, panes, tol);
  }
  // 不同軸，matchSplit 對這個方向完全透明，兩側都可以嘗試。
  return (
    findRefPaneInRect(r0, resizeDirection, splits, panes, tol) ??
    findRefPaneInRect(r1, resizeDirection, splits, panes, tol)
  );
}

// 找出可用來精確調整切割節點 split 的參考 pane，並指定實際要呼叫的 resizeDirection
//（increase 時等於 split.direction 本身；decrease 時是其相反方向 left/up）。
// 必須從 split 的「正確側(來源側)」分區出發找，這個側別是依 resizeDirection 決定，
// 與 split.direction 本身可能相反（decrease 呼叫時用的是相反方向）。
function findReferencePaneForSplit(panes, splits, split, resizeDirection, tol) {
  const regions = partitionRects(split);
  const sourceRegion = regions[sourceRegionIndex(resizeDirection)];
  return findRefPaneInRect(sourceRegion, resizeDirection, splits, panes, tol);
}

// 計算「把目前所有 pane 平均分配寬高」需要的一組 resize 呼叫。
// 原理：herdr pane resize 的 --amount 是直接加/減在該切割線的 ratio 上（right/down 為加，
// left/up 為減），且是精確線性運算，所以只要算出「目標 ratio - 目前 ratio」的差值，
// 一次 resize 呼叫就能精確命中目標，不需要反覆試探。
// 每條 right 切割線的目標 ratio = 該切割線範圍內，左側應佔的欄數 / 該切割線範圍內的總欄數；
// 每條 down 切割線的目標 ratio 同理，改用「該切割線所轄範圍內的列數」計算。
// 這個目標值只取決於「左右各佔幾欄/上下各佔幾列」，與其他切割線的 ratio 或呼叫順序無關，
// 所以可以一次算完所有切割線的目標值，再依任意順序逐一送出 resize。
function computeEqualizePlan(layout) {
  const panes = layout.panes;
  const splits = layout.splits;
  const totalW = layout.area.width;
  const totalH = layout.area.height;
  const tol = Math.max(1, Math.max(totalW, totalH) * 0.01);

  const { xs } = getColumnStarts(panes);

  const plan = [];
  for (const s of splits) {
    const { x: sx, y: sy, width: sw, height: sh } = s.rect;
    let targetRatio;

    if (s.direction === 'right') {
      const colsInRange = xs.filter((x) => x >= sx - tol && x < sx + sw - tol);
      const Nc = colsInRange.length;
      if (Nc < 2) continue;
      const boundaryX = sx + sw * s.ratio;
      const leftCount = colsInRange.filter((x) => x < boundaryX - tol).length;
      targetRatio = leftCount / Nc;
    } else {
      const contained = panes.filter(
        (p) =>
          p.rect.x >= sx - tol &&
          p.rect.x < sx + sw - tol &&
          p.rect.y >= sy - tol &&
          p.rect.y < sy + sh - tol
      );
      const ys = [];
      for (const p of contained) {
        if (!ys.some((y) => Math.abs(y - p.rect.y) <= tol)) ys.push(p.rect.y);
      }
      ys.sort((a, b) => a - b);
      const Nr = ys.length;
      if (Nr < 2) continue;
      const boundaryY = sy + sh * s.ratio;
      const topCount = ys.filter((y) => y < boundaryY - tol).length;
      targetRatio = topCount / Nr;
    }

    const delta = targetRatio - s.ratio;
    if (Math.abs(delta) < 0.001) continue;

    const resizeDirection = delta >= 0 ? s.direction : s.direction === 'right' ? 'left' : 'up';
    const refPaneId = findReferencePaneForSplit(panes, splits, s, resizeDirection, tol);
    if (!refPaneId) continue;

    plan.push({
      splitId: s.id,
      paneId: refPaneId,
      resizeDirection,
      amount: Math.abs(delta),
      currentRatio: s.ratio,
      targetRatio,
    });
  }
  return plan;
}

function fetchLayout(args) {
  const { workspaceId, tabId } = resolveWorkspaceAndTab(args);
  const panesInTab = anyPaneInTab(workspaceId, tabId);
  return herdr(['pane', 'layout', '--pane', panesInTab[0].pane_id]).result.layout;
}

// 執行平均分配：讀取版面、算出 resize 計畫、逐一送出，然後重新讀取版面確認是否已收斂。
// 有些情況（例如巢狀分割的某層在中途新增）單一輪計畫套用後，其餘節點的相對位置會跟著
// 變化，可能需要再算一輪才會完全收斂，所以這裡會重複「重算計畫並套用」直到計畫為空
// （代表已完全平均）或達到安全上限（迴圈次數上限，避免異常狀況造成無窮迴圈）。
// 回傳「總共套用的 resize 步驟數」（0 代表一開始就已經平均，不需調整）與最後一輪的計畫。
function equalizeLayout(args, dryRun) {
  if (dryRun) {
    const layout = fetchLayout(args);
    const plan = computeEqualizePlan(layout);
    return { applied: 0, plan, dryRun: true };
  }

  let totalApplied = 0;
  let lastPlan = [];
  const MAX_ROUNDS = 8;
  for (let round = 0; round < MAX_ROUNDS; round++) {
    const layout = fetchLayout(args);
    const plan = computeEqualizePlan(layout);
    lastPlan = plan;
    if (plan.length === 0) break;
    for (const step of plan) {
      herdr(['pane', 'resize', '--pane', step.paneId, '--direction', step.resizeDirection, '--amount', step.amount.toFixed(6)]);
    }
    totalApplied += plan.length;
  }
  return { applied: totalApplied, plan: lastPlan };
}

function describeGrid(columns) {
  return columns.map((c) => c.length).join(':');
}

// 計算下一步應該怎麼切。回傳 { mode: 'A'|'B', targetPaneId, direction, ratio, resultDescription }
function computeNext(columns) {
  const numCols = columns.length;
  const maxRows = Math.max(...columns.map((c) => c.length));

  // 候選 A：新增一欄（在最右欄的第一列 pane 上往右切）
  const candA = {
    mode: 'A',
    numCols: numCols + 1,
    maxRows: Math.max(maxRows, 1),
    targetPaneId: columns[numCols - 1][0],
    direction: 'right',
    ratioNumerator: numCols, // target 維持 numCols/(numCols+1)
    ratioDenominator: numCols + 1,
  };
  candA.diff = Math.abs(candA.numCols - candA.maxRows);

  // 候選 B：找列數最少、最靠左的欄，在其最下面的 pane 上往下切
  let targetColIdx = 0;
  let minRows = columns[0].length;
  for (let i = 1; i < columns.length; i++) {
    if (columns[i].length < minRows) {
      minRows = columns[i].length;
      targetColIdx = i;
    }
  }
  const kRows = columns[targetColIdx].length;
  const newRowsForThatCol = kRows + 1;
  const candB = {
    mode: 'B',
    numCols: numCols,
    maxRows: Math.max(maxRows, newRowsForThatCol),
    targetPaneId: columns[targetColIdx][kRows - 1],
    direction: 'down',
    ratioNumerator: kRows, // target 維持 kRows/(kRows+1)
    ratioDenominator: kRows + 1,
    targetColIdx,
  };
  candB.diff = Math.abs(candB.numCols - candB.maxRows);

  // 差值小者勝；平手時優先 A（橫向優先擴張）
  const chosen = candA.diff <= candB.diff ? candA : candB;

  const resultColumns = columns.map((c) => [...c]);
  if (chosen.mode === 'A') {
    resultColumns.push(['(new)']);
  } else {
    resultColumns[chosen.targetColIdx] = [...resultColumns[chosen.targetColIdx], '(new)'];
  }

  return {
    mode: chosen.mode,
    targetPaneId: chosen.targetPaneId,
    direction: chosen.direction,
    ratio: chosen.ratioNumerator / chosen.ratioDenominator,
    beforeDescription: describeGrid(columns),
    afterDescription: describeGrid(resultColumns),
  };
}

// 寬 pane：右邊界越過了下一條欄位起點（新增一欄後、尚未補齊的列）。依由上到下、由左到右排序。
function findWidePanes(layoutPanes) {
  const { xs, tolerance } = getColumnStarts(layoutPanes);
  const wide = layoutPanes.filter((p) => {
    const right = p.rect.x + p.rect.width;
    return xs.some((x) => x > p.rect.x + tolerance && x < right - tolerance);
  });
  return wide.sort((a, b) => a.rect.y - b.rect.y || a.rect.x - b.rect.x);
}

// 逐列、再逐欄（列優先）展開：第一列由左到右，再第二列……
function rowMajor(columns) {
  const out = [];
  const maxRows = Math.max(...columns.map((c) => c.length));
  for (let r = 0; r < maxRows; r++) {
    for (const c of columns) if (r < c.length) out.push(c[r]);
  }
  return out;
}

// n 個 pane 時，本腳本序列應有的欄列數（例如 5 -> "2:2:1"）。用來判斷現有版面是否相容、可否重新排序。
function canonicalShape(n) {
  let cols = [['_0']];
  let seq = 1;
  for (let i = 1; i < n; i++) {
    const next = computeNext(cols);
    const id = `_${seq++}`;
    if (next.mode === 'A') {
      cols = [...cols, [id]];
    } else {
      const idx = cols.findIndex((c) => c[c.length - 1] === next.targetPaneId);
      cols = cols.map((c, k) => (k === idx ? [...c, id] : c));
    }
  }
  return describeGrid(cols);
}

// 決定下一步：
//   fill       補齊上一次新增欄時留下的寬 pane（搬出 → 搬回上一列下方 → 新欄最後一個 pane 往下切）
//   add-column 最後一欄第一列往右切（只在該列長出新欄，其餘列暫時維持寬 pane）
//   add-row    列數最少且最靠左的欄，最下面 pane 往下切
function planNext(layoutPanes) {
  const columns = reconstructColumns(layoutPanes);
  const beforeDescription = describeGrid(columns);
  const total = columns.flat().length;
  const compatible = beforeDescription === canonicalShape(total);

  const wide = findWidePanes(layoutPanes);
  if (wide.length > 0) {
    const w = wide[0];
    const colIdx = columns.findIndex((c) => c.includes(w.pane_id));
    const pos = columns[colIdx].indexOf(w.pane_id);
    const nextCol = columns[colIdx + 1];
    if (pos < 1 || !nextCol) {
      throw new Error(`版面結構不符預期，無法補齊寬 pane ${w.pane_id}（欄${colIdx + 1} 第${pos + 1}列）`);
    }
    const after = columns.map((c, i) => (i === colIdx + 1 ? [...c, '(new)'] : c));
    return {
      mode: 'fill',
      widePaneId: w.pane_id,
      anchorPaneId: columns[colIdx][pos - 1],
      targetPaneId: nextCol[nextCol.length - 1],
      direction: 'down',
      ratio: 0.5,
      columns,
      compatible,
      beforeDescription,
      afterDescription: describeGrid(after),
    };
  }

  const next = computeNext(columns);
  return { ...next, mode: next.mode === 'A' ? 'add-column' : 'add-row', columns, compatible };
}

// 目前順序 -> 期望順序所需的 swap 清單（selection sort，只交換位置不改分割樹）。
function planSwaps(slots, desired) {
  const cur = [...slots];
  const swaps = [];
  for (let i = 0; i < desired.length; i++) {
    if (cur[i] === desired[i]) continue;
    const j = cur.indexOf(desired[i]);
    swaps.push([cur[i], cur[j]]);
    [cur[i], cur[j]] = [cur[j], cur[i]];
  }
  return swaps;
}

function cmdLayout(args) {
  const layoutPanes = fetchLayoutPanes(args);
  const columns = reconstructColumns(layoutPanes);
  console.log('目前版面：', describeGrid(columns));
  columns.forEach((c, i) => console.log(`  欄${i + 1}:`, c.join(' -> ')));
  const total = columns.flat().length;
  console.log(`  排序相容：${describeGrid(columns) === canonicalShape(total) ? '是' : '否（split 時不會重新排序）'}`);
}

function cmdNext(args) {
  const next = planNext(fetchLayoutPanes(args));
  console.log(JSON.stringify(
    {
      before: next.beforeDescription,
      after: next.afterDescription,
      mode: next.mode,
      target_pane_id: next.targetPaneId,
      direction: next.direction,
      ratio: Number(next.ratio.toFixed(6)),
      move_wide_pane: next.mode === 'fill' ? next.widePaneId : undefined,
      move_below: next.mode === 'fill' ? next.anchorPaneId : undefined,
      will_reorder: next.compatible,
    },
    null,
    2
  ));
}

function splitPane(targetPaneId, direction, ratio, cwd, noFocus) {
  const a = ['pane', 'split', '--pane', targetPaneId, '--direction', direction, '--ratio', Number(ratio).toFixed(6), '--cwd', cwd];
  if (noFocus) a.push('--no-focus');
  return herdr(a).result.pane.pane_id;
}

// 補齊寬 pane 且避免列高耦合。herdr pane move 在同一 tab 內無效（same_tab），所以先搬到暫存 tab。
//   1. 寬 pane 搬到暫存 tab：其兄弟節點取代原位置，新欄 pane 變成跨多列的高 pane。
//   2. 搬回原 tab，掛在同欄上一列(anchor)下方：舊欄多出這一列，且只涵蓋舊欄寬度。
//   3. 新欄最後一個 pane 往下切，得到新 pane，剛好與剛搬回的 pane 同列。
function executeFill(workspaceId, tabId, plan, cwd, noFocus) {
  const out = herdr(['pane', 'move', plan.widePaneId, '--new-tab', '--workspace', workspaceId, '--label', 'grid-tmp', '--no-focus']).result.move_result;
  if (!out.changed) throw new Error(`暫存寬 pane ${plan.widePaneId} 失敗：${out.reason ?? '未知原因'}`);
  const tmpTabId = out.created_tab.tab_id;
  const wideId = out.pane.pane_id;

  try {
    const back = herdr(['pane', 'move', wideId, '--tab', tabId, '--split', 'down', '--target-pane', plan.anchorPaneId, '--ratio', '0.5', '--no-focus']).result.move_result;
    if (!back.changed) throw new Error(back.reason ?? '未知原因');
  } catch (e) {
    throw new Error(
      `pane ${wideId} 已搬到暫存 tab ${tmpTabId}，但搬回失敗：${e.message}。` +
      `請手動執行：herdr pane move ${wideId} --tab ${tabId} --split down --target-pane ${plan.anchorPaneId}`
    );
  }

  try {
    const left = herdr(['tab', 'list', '--workspace', workspaceId]).result.tabs.find((t) => t.tab_id === tmpTabId);
    if (left) herdr(['tab', 'close', tmpTabId]);
  } catch (_) { /* 暫存 tab 已自動消失 */ }

  return splitPane(plan.targetPaneId, 'down', 0.5, cwd, noFocus);
}

// 新 pane 一律排在最後，其餘維持原本的閱讀順序，然後用 swap 把所有 pane 排成列優先順序。
// 版面與腳本序列不相容時略過。
function reorderPanes(desired) {
  const layout = herdr(['pane', 'layout', '--pane', desired[0]]).result.layout;
  const cols = reconstructColumns(layout.panes);
  if (describeGrid(cols) !== canonicalShape(desired.length)) return { skipped: `版面 ${describeGrid(cols)} 與預期不符` };
  const slots = rowMajor(cols);
  if (slots.length !== desired.length || !desired.every((id) => slots.includes(id))) return { skipped: 'pane 清單不一致' };
  const swaps = planSwaps(slots, desired);
  for (const [a, b] of swaps) herdr(['pane', 'swap', '--source-pane', a, '--target-pane', b]);
  return { swaps: swaps.length };
}

function cmdSplit(args) {
  if (!args.cwd) throw new Error('split 需要 --cwd 指定新 pane 的工作目錄');
  const { workspaceId, tabId } = resolveWorkspaceAndTab(args);
  const layout = herdr(['pane', 'layout', '--pane', anyPaneInTab(workspaceId, tabId)[0].pane_id]).result.layout;
  const next = planNext(layout.panes);
  const noFocus = args.focus !== true;
  const modeLabel = { fill: '補齊寬 pane', 'add-column': '新增欄', 'add-row': '新增列' }[next.mode];
  const doReorder = next.compatible && args['no-reorder'] !== true;
  const oldOrder = rowMajor(next.columns);

  console.log(`[herdr-grid] (${modeLabel}) ${next.beforeDescription} -> ${next.afterDescription}，目標 ${next.targetPaneId}`);
  if (args['dry-run']) {
    console.log('[herdr-grid] --dry-run，未實際執行。');
    return;
  }

  const newPaneId = next.mode === 'fill'
    ? executeFill(workspaceId, tabId, next, args.cwd, noFocus)
    : splitPane(next.targetPaneId, next.direction, next.ratio, args.cwd, noFocus);

  if (typeof args.name === 'string' && args.name) herdr(['pane', 'rename', newPaneId, args.name]);

  const reorder = doReorder
    ? reorderPanes([...oldOrder, newPaneId])
    : { skipped: next.compatible ? '--no-reorder' : '既有版面不符本腳本結構' };
  const equalizeResult = args['no-equalize'] !== true ? equalizeLayout(args, false) : null;

  console.log(JSON.stringify(
    {
      new_pane_id: newPaneId,
      name: typeof args.name === 'string' ? args.name : undefined,
      action: next.mode,
      layout_after: describeGrid(fetchColumns(args)),
      reorder_swaps: reorder.swaps,
      reorder_skipped: reorder.skipped,
      equalized_steps: equalizeResult ? equalizeResult.applied : undefined,
    },
    null,
    2
  ));
}

function cmdEqualize(args) {
  const result = equalizeLayout(args, !!args['dry-run']);
  if (result.dryRun) {
    console.log('[herdr-grid] --dry-run，以下是會執行的 resize 計畫（尚未實際套用）：');
  } else {
    console.log(`[herdr-grid] 已套用 ${result.applied} 個 resize 步驟使版面平均分配。`);
  }
  console.log(JSON.stringify(result.plan, null, 2));
}

module.exports = { computeNext, describeGrid, reconstructColumns, planNext, findWidePanes, rowMajor, canonicalShape, planSwaps };

function main() {
  const [sub, ...rest] = process.argv.slice(2);
  const args = parseArgs(rest);
  if (!args.tab && !args.workspace) {
    console.error('必須提供 --tab <workspace(id或名稱)>:<tab(id或名稱)>，或至少提供 --workspace <id或名稱>');
    process.exit(2);
  }
  switch (sub) {
    case 'layout':
      return cmdLayout(args);
    case 'next':
      return cmdNext(args);
    case 'split':
      return cmdSplit(args);
    case 'equalize':
      return cmdEqualize(args);
    default:
      console.error('用法: node tools/herdr/grid.cjs <layout|next|split|equalize> --tab <workspace(id/名稱)>:<tab(id/名稱)> [--workspace <id/名稱>] [--cwd <path>] [--name <名稱>] [--focus] [--no-reorder] [--no-equalize] [--dry-run]');
      process.exit(2);
  }
}

if (require.main === module) {
  main();
}
