#!/usr/bin/env node
'use strict';

/**
 * herdr-grid.js — 依照「橫向優先擴張、縱向次之、左先右後」規則，
 * 在指定 Herdr workspace/tab 內新增下一個 pane。
 *
 * 目標序列（欄:欄:欄，每個數字＝該欄的列數）：
 *   1 => 1:1 => 2:1 => 2:2 => 2:2:1 => 2:2:2 => 3:2:2 => 3:3:2 => 3:3:3 => ...
 *
 * 重要概念（Herdr 分割是二元樹狀分割，不是表格）：
 *   當「新增一欄」時（例如 2:2 -> 2:2:1），實際上只有「被切割的那一列」在新欄位
 *   產生對應 pane；同一來源欄位的其他列，其 pane 寬度並未改變，於是變成一個
 *   「橫跨新舊兩欄寬度」的寬 pane（例如 2:2:1 狀態下，欄2 第2列的 pane 其實還是
 *   佔滿欄2+欄3 的寬度，並非真的只在欄2）。若不處理這個寬 pane，光靠「數欄位/列數」
 *   來決定下一步會選錯目標、切錯方向（這正是先前版本的 bug 根源）。
 *
 * 演算法（每次只新增 1 個 pane，且每次都重新讀取真實版面，不維護內部狀態）：
 *   0. 優先偵測「寬 pane」：依所有 pane 的 x 座標推得欄位邊界(colStarts)，
 *      任何 pane 的右邊界超出其所屬欄位邊界的下一條線，代表它橫跨了多個欄位寬度。
 *      若存在寬 pane（依讀取順序取由上到下、由左到右第一個），優先把它往右切開，
 *      切割比例 = (下一欄邊界 - 該pane.x) / 該pane.width，使左半邊寬度精確對齊
 *      既有較窄欄位的寬度。這一步不算「新增列」也不算「新增欄」，純粹是修正對齊。
 *   1. 若無寬 pane（版面已對齊），才依 pane rect 的 x 座標分群成「欄」，
 *      每欄內再依 y 座標排序成「列」（由上到下），計算兩個候選：
 *        A（新增一欄）：在最右欄的「第一列」pane 上，往右切一刀。
 *        B（在現有欄新增一列）：找列數最少且最靠左的欄，
 *          在該欄「最下面」的 pane 上，往下切一刀。
 *   2. 兩個候選各自算出「新增後的欄數 與 最大列數」之差的絕對值，
 *      取差值較小者；差值相同時優先選 A（橫向優先擴張）。
 *
 * 用法（ID 或名稱皆可混用，見下方解析規則）：
 *   node herdr-grid.js layout   --tab w1:t1
 *   node herdr-grid.js layout   --tab Company --workspace Main
 *   node herdr-grid.js layout   --tab Main:Company
 *   node herdr-grid.js next     --tab w1:t1
 *   node herdr-grid.js split    --tab w1:t1 --cwd "C:\path\to\worktree" [--no-focus] [--dry-run] [--no-equalize]
 *   node herdr-grid.js equalize --tab w1:t1 [--dry-run]
 *
 * 平均分配（equalize）：split 完成新增/對齊後，預設會自動把「目前所有 pane」的寬高
 * 調整為精確平均分配（例如 2:2:1 新增後不會維持切割當下的固定比例，而是讓 3 欄寬度
 * 平均、各欄內的列高度也平均）。原理是 herdr pane resize 的 --amount 直接加/減在該
 * 切割線的 ratio 上，且是精確線性運算，所以可以一次算出每條切割線「目標 ratio - 目前
 * ratio」的差值並直接命中，不需要反覆試探。傳 --no-equalize 可跳過這個自動平均步驟，
 * 只做新增/對齊本身；也可以單獨呼叫 `equalize` 子指令，隨時把現有版面重新調整為平均。
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

// 偵測「寬 pane」：因 Herdr 分割是二元樹狀分割，新增一欄時只有被切的那一列對齊到新欄，
// 同欄其他列的 pane 寬度不變，於是會橫跨新舊兩欄的寬度（即其右邊界越過了某條欄位邊界線）。
// 若存在這種未對齊的寬 pane，必須優先把它往右切齊，否則後續「數欄位/列數」的判斷會失準。
// 依由上到下、由左到右的順序取第一個找到的寬 pane，確保結果穩定可重現。
function findMisalignedWidePane(layoutPanes) {
  const { xs, tolerance } = getColumnStarts(layoutPanes);
  const sorted = [...layoutPanes].sort((a, b) => a.rect.y - b.rect.y || a.rect.x - b.rect.x);
  for (const p of sorted) {
    const rightEdge = p.rect.x + p.rect.width;
    for (const x of xs) {
      if (x > p.rect.x + tolerance && x < rightEdge - tolerance) {
        return {
          targetPaneId: p.pane_id,
          direction: 'right',
          ratio: (x - p.rect.x) / p.rect.width,
        };
      }
    }
  }
  return null;
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

// 決定下一步動作：優先修正任何未對齊的寬 pane（realign），沒有才走新增欄/列邏輯。
function planNext(layoutPanes) {
  const columns = reconstructColumns(layoutPanes);
  const beforeDescription = describeGrid(columns);

  const misaligned = findMisalignedWidePane(layoutPanes);
  if (misaligned) {
    return {
      mode: 'realign',
      targetPaneId: misaligned.targetPaneId,
      direction: misaligned.direction,
      ratio: misaligned.ratio,
      beforeDescription,
      afterDescription: `${beforeDescription}(對齊修正中，非新增)`,
    };
  }

  const next = computeNext(columns);
  return { ...next, mode: next.mode === 'A' ? 'add-column' : 'add-row' };
}

function cmdLayout(args) {
  const layoutPanes = fetchLayoutPanes(args);
  const misaligned = findMisalignedWidePane(layoutPanes);
  const columns = reconstructColumns(layoutPanes);
  console.log('目前版面：', describeGrid(columns));
  columns.forEach((c, i) => console.log(`  欄${i + 1}:`, c.join(' -> ')));
  if (misaligned) {
    console.log(
      `  [警告] 偵測到未對齊的寬 pane ${misaligned.targetPaneId}，下一步 split 會先對齊它（往右切，ratio=${misaligned.ratio.toFixed(4)}），而非新增欄/列。`
    );
  }
}

function cmdNext(args) {
  const layoutPanes = fetchLayoutPanes(args);
  const next = planNext(layoutPanes);
  const modeLabel = { realign: '對齊修正(非新增)', 'add-column': 'add-column(橫向新增一欄)', 'add-row': 'add-row(縱向在現有欄新增一列)' }[next.mode];
  console.log(JSON.stringify(
    {
      before: next.beforeDescription,
      after: next.afterDescription,
      mode: modeLabel,
      target_pane_id: next.targetPaneId,
      direction: next.direction,
      ratio: Number(next.ratio.toFixed(6)),
    },
    null,
    2
  ));
}

function cmdSplit(args) {
  if (!args.cwd) throw new Error('split 需要 --cwd 指定新 pane 的工作目錄');
  const layoutPanes = fetchLayoutPanes(args);
  const next = planNext(layoutPanes);

  const splitArgs = [
    'pane', 'split',
    '--pane', next.targetPaneId,
    '--direction', next.direction,
    '--ratio', next.ratio.toFixed(6),
    '--cwd', args.cwd,
  ];
  if (args['no-focus'] !== false) splitArgs.push('--no-focus');

  const modeLabel = next.mode === 'realign' ? '對齊修正(非新增)' : (next.mode === 'add-column' ? '新增欄' : '新增列');
  console.log(
    `[herdr-grid] (${modeLabel}) ${next.beforeDescription} -> 切割 ${next.targetPaneId} 往${next.direction === 'right' ? '右' : '下'} (ratio=${next.ratio.toFixed(4)})`
  );

  if (args['dry-run']) {
    console.log('[herdr-grid] --dry-run，未實際執行 split。指令為：', 'herdr', splitArgs.join(' '));
    return;
  }

  const result = herdr(splitArgs);
  const newPaneId = result.result.pane.pane_id;

  // 新增/對齊之後，若使用者要求平均分配（預設開啟，--no-equalize 可關閉），
  // 把目前所有 pane 的寬高調整為精確平均，而非維持切割當下的固定比例。
  let equalizeResult = null;
  if (args['no-equalize'] !== true) {
    equalizeResult = equalizeLayout(args, false);
  }

  // 對齊修正只是中間步驟，實際「新增後版面」以重新讀取的真實版面為準，避免預測誤差累積。
  const afterColumns = fetchColumns(args);
  const afterDescription = describeGrid(afterColumns);
  const stillMisaligned = findMisalignedWidePane(fetchLayoutPanes(args));

  console.log(JSON.stringify(
    {
      new_pane_id: newPaneId,
      action: next.mode,
      layout_after: afterDescription,
      equalized_steps: equalizeResult ? equalizeResult.applied : undefined,
      note: next.mode === 'realign'
        ? (stillMisaligned ? '仍有未對齊的寬 pane，請再呼叫一次 split 繼續對齊' : '對齊完成，可再呼叫一次 split 以真正新增下一個工作 pane')
        : undefined,
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

module.exports = { computeNext, describeGrid, reconstructColumns, findMisalignedWidePane, planNext };

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
      console.error('用法: node herdr-grid.js <layout|next|split|equalize> --tab <workspace(id/名稱)>:<tab(id/名稱)> [--workspace <id/名稱>] [--cwd <path>] [--dry-run] [--no-equalize]');
      process.exit(2);
  }
}

if (require.main === module) {
  main();
}
