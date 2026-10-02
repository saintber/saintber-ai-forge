## Why

多個 AI 助理或工作同時在 Herdr 的同一個 tab 內開 pane 時，需要一支腳本維持整齊的網格版面（欄:列約 1:1），並讓使用者能獨立調整每個 pane 的列高。`tools/herdr/grid.cjs` 第一版（commit f12a4a9）只保證欄列數正確，實際使用發現三個問題：

- 新增一欄時，同欄其他列的 down-split 節點會同時涵蓋新舊兩欄，調整某欄列高時鄰欄會被一起拉動（列高耦合）。
- 新增的 pane 位置依分割順序而定，不是依開啟順序逐列排列。
- 無法替新 pane 命名。

本 change 記錄重新調整後、已由使用者人工驗證通過的行為，作為後續維護的規格依據。

## What Changes

- 新增 `--name <名稱>`（選填）：新 pane 建立後以 `herdr pane rename` 命名。
- 新增一欄改為「逐列長出」：先只在最後一欄第一列右側切出新欄，其餘列在後續呼叫中以「搬出暫存 tab → 搬回同欄上一列下方 → 新欄最後一個 pane 往下切」補齊，使每欄的 down-split 只涵蓋自己那一欄，列高可獨立調整。
- 新增自動重新排序：版面與本腳本序列相容時，新 pane 排最後、所有 pane 以 `herdr pane swap` 排成逐列順序（例如 5 個 pane：第一列 1,2,3、第二列 4,5）；不相容則略過；`--no-reorder` 可關閉。
- 移除舊的 realign（對齊修正）機制與 `findMisalignedWidePane`，改由上述補齊流程取代。
- 保留 `layout` / `next` / `equalize` 子指令與 workspace/tab 的 id／名稱解析規則，行為不變。
- 保留 `--no-equalize`：新增完成後預設平均分配寬高。

## Non-Goals

- 不修復舊版本已建立、已耦合的 tab（舊 tab 內 `split` 會略過排序，列高耦合維持現狀）。
- 不處理 `herdr pane move` 在同一 tab 內無效的限制，只以暫存 tab 繞過。
- 不啟動 agent、不派工；僅負責版面。
- 不納入 npm 發行內容（`tools/` 與 `docs/` 不在 `files` 內）。

## Capabilities

### New Capabilities

- `herdr-grid`: Herdr 網格版面腳本——pane 新增順序與目標形狀、逐列長欄與列高獨立、重新排序與相容判斷、pane 命名、平均分配、tab 解析與子指令。

### Modified Capabilities

（無；目前沒有既有 spec 可修改。）

## Impact

- 搬移並修改：`scripts/herdr-grid.cjs` → `tools/herdr/grid.cjs`、`scripts/herdr-grid-simulate.cjs` → `tools/herdr/grid-simulate.cjs`（目錄以 `herdr` 命名，供日後其他 Herdr 工具共用）。
- 新增：`tools/herdr/README.md`（Herdr 工具總覽）、`docs/tools/herdr/grid.md`（使用說明）。
- 不修改：`src/`、`templates/`、`package.json`（腳本不在 npm `files` 內，不隨套件發行）。
- 外部相依：Herdr CLI（`pane split/move/swap/resize/rename/layout`、`tab list/close`）；需在 Herdr 內執行（`HERDR_ENV=1`）。
- 運維影響：新增欄的補齊步驟會短暫建立並關閉標題為 `grid-tmp` 的暫存 tab；若搬回失敗，錯誤訊息會附上手動復原指令。
