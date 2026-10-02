## Context

Herdr 的 pane 版面是二元分割樹，`pane split` 只能把既有 pane 一分為二。列高調整（`pane resize`）命中的是「包住該 pane 的 down-split 節點」，因此若某個 down-split 節點的矩形涵蓋多欄，調整它會連動這些欄。

實測確認的 Herdr 行為（herdr 0.9.0-preview.2026-09-16）：

- `pane move --tab T --split D --target-pane P` 在來源與目標同一 tab 時永遠回傳 `changed:false, reason:"same_tab"`；來源在其他 tab 時才會生效。
- `pane move --new-tab` 可把 pane 搬到新 tab；最後一個 pane 搬走後該 tab 不一定自動消失，需主動關閉。
- `pane swap` 只交換兩個 pane 的位置，不改變分割樹形狀。
- `pane close` 會讓其父節點被兄弟節點取代（該層 split 消失）。

## Goals / Non-Goals

**Goals:**

- 欄:列約 1:1，序列 `1 → 1:1 → 2:1 → 2:2 → 2:2:1 → 2:2:2 → 3:2:2 → 3:3:2 → 3:3:3 → 3:3:3:1 …`（數字為各欄列數）。
- 每欄的每個 pane 列高可獨立調整，新增欄後也不例外。
- pane 依開啟順序逐列排列。

**Non-Goals:**

- 不修復舊 tab 的既有耦合；不支援非本腳本建立的任意版面。

## Decisions

### 逐列長欄，取代 swap + close 方案

原始構想是「在最後一欄第一列右切 → 與該欄下一列 swap → 關閉新開 pane → 再垂直分割」。推演後發現關閉 pane 會讓父節點塌縮，結果變成每欄列數減少（2:1:1），且總 pane 數不增反減，與目標形狀不符。

採用的做法：每次呼叫只新增一個 pane，新欄逐列長出：

1. `add-column`：在最後一欄第一列往右切。此時該欄其餘列的 pane 因寬度不變，變成橫跨新舊兩欄的「寬 pane」。
2. `fill`（下一次呼叫）：取第一個寬 pane W，其同欄上一列為 A。
   1. 把 W 搬到暫存 tab（同 tab 內 move 無效）。
   2. 把 W 搬回，掛在 A 下方。此時 W 只佔舊欄寬度，舊欄的 down-split 只涵蓋舊欄。
   3. 在新欄最後一個 pane 往下切，得到新 pane，與 W 同列。
   4. 關閉暫存 tab。

結果每欄的 down-split 節點只涵蓋單一欄，列高可獨立調整。代價是 `add-column` 後到 `fill` 之間的版面是暫態（有寬 pane），但這與「還沒開夠 pane」同義。

**替代方案**：新欄一次建完所有列——拒絕，一次呼叫會新增多個 pane，違反「每次呼叫一個 pane」。

### 下一步決策規則

`computeNext`：候選 A（新增欄）與 B（在列數最少且最靠左的欄新增一列）各自計算「新增後欄數與最大列數之差的絕對值」，差值小者勝，平手選 A。有寬 pane 時一律先 `fill`。

### 相容判斷與重新排序

不嘗試判斷 pane 是否由本腳本開啟。改用結構判斷：目前欄列數字串是否等於 `canonicalShape(pane 數)`（以 `computeNext` 從 1 個 pane 模擬到 N 個 pane 的結果）。相符才排序。

排序做法：把既有 pane 以目前列優先（逐列、再逐欄）順序視為既有開啟順序，新 pane 附加在最後，得到期望順序；再以 selection sort 產生 `pane swap` 清單套用。這個假設是「既有排序已正確」，因此每次呼叫只需處理新 pane 造成的位移（通常 1–2 次 swap）。

**替代方案**：以 pane id 或建立時間判斷開啟順序——拒絕，Herdr 的 pane id 在 move 後會變動（跨 tab 移動會取得新 id 的可能性），且無建立時間欄位。

### 暫存 tab 每次建立、用完關閉

不常駐隱藏 tab。`fill` 內建立標題 `grid-tmp` 的 tab，搬回後立即關閉；搬回失敗時不關閉，並在錯誤訊息附上手動復原指令，避免遺失 pane。

### 平均分配

沿用既有 `equalize`：算出每條切割線「目標 ratio − 目前 ratio」，以 `pane resize --amount` 一次命中，最多重複 8 輪。排序之後才平均，避免 swap 影響版面比例。

## Risks / Trade-offs

- [`fill` 中途失敗會留下暫存 tab 與孤立 pane] → 搬回失敗時保留暫存 tab 並輸出復原指令；搬出失敗則直接報錯、版面未變。
- [Herdr 版本更新改變 `pane move` 同 tab 行為] → 屆時暫存 tab 可省略，邏輯集中在 `executeFill`，可獨立替換。
- [舊 tab 已耦合] → 不處理；`layout` 會顯示「排序相容：否」協助辨識。
- [並行呼叫同一 tab 的 split 會讀到暫態版面] → 不處理，呼叫端應序列化。
