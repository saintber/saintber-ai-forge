# herdr grid：Herdr 網格 pane 工具

在 Herdr 的同一個 tab 內，每呼叫一次就新增一個 pane，並維持欄:列約 1:1 的網格。腳本只負責版面，不啟動 agent、不派工。

- 腳本：`tools/herdr/grid.cjs`（離線序列模擬：`tools/herdr/grid-simulate.cjs`）
- 規格：`openspec/changes/herdr-grid-layout/`（歸檔後為 `openspec/specs/herdr-grid/spec.md`）
- 工具總覽：`tools/herdr/README.md`
- 需求：Node.js、Herdr CLI（在 Herdr 內執行，`HERDR_ENV=1`）

## 快速開始

```bash
node tools/herdr/grid.cjs split --workspace Personal --tab 2 --cwd "C:\path\to\worktree" --name reviewer
```

每執行一次新增一個 pane，版面依序成長：

```
1 → 1:1 → 2:1 → 2:2 → 2:2:1 → 2:2:2 → 3:2:2 → 3:3:2 → 3:3:3 → 3:3:3:1 → …
```

數字是各欄的列數，冒號分隔欄。例如 `2:2:1` 表示三欄，列數為 2、2、1。

## 子命令

| 子命令 | 說明 |
| --- | --- |
| `split` | 新增一個 pane（需要 `--cwd`） |
| `layout` | 顯示目前欄列結構、各欄 pane id、是否可自動排序 |
| `next` | 顯示下一步會怎麼做（不變更版面） |
| `equalize` | 把現有版面的寬高重新平均分配 |

## 選項

| 選項 | 適用 | 說明 |
| --- | --- | --- |
| `--workspace <id或名稱>` | 全部 | workspace，如 `w3` 或 `Personal` |
| `--tab <...>` | 全部 | tab，見下方「指定 tab」 |
| `--cwd <路徑>` | `split` | 新 pane 的工作目錄（必填） |
| `--name <名稱>` | `split` | 新 pane 名稱（選填，不給則維持預設） |
| `--focus` | `split` | 新 pane 取得焦點（預設不取得） |
| `--no-reorder` | `split` | 不重新排序既有 pane |
| `--no-equalize` | `split` | 不自動平均分配寬高 |
| `--dry-run` | `split` `equalize` | 只顯示計畫，不實際執行 |

### 指定 tab

- `--tab w3:t2`：完整 tab id。
- `--tab Personal:2`：`<workspace 名稱或 id>:<tab 名稱、id 或編號>`。
- `--workspace Personal --tab 2`：兩者並用，必須指向同一個 workspace。
- 只給 `--workspace`：使用該 workspace 目前的 active tab，沒有則用第一個 tab。
- 找不到時，錯誤訊息會說明是 workspace 還是 tab 不存在，並列出現有候選。

## 行為說明

### 新增欄是逐列長出來的

新增一欄時，一次呼叫只在最後一欄的第一列右側長出新欄；其餘列在接下來的呼叫中補齊。

```
2:2 → 呼叫 → 2:2:1 → 呼叫 → 2:2:2
┌─┬─┐       ┌─┬─┬─┐       ┌─┬─┬─┐
│1│2│       │1│2│5│       │1│2│5│
├─┼─┤   →   ├─┼─┴─┤   →   ├─┼─┼─┤
│3│4│       │3│ 4 │       │3│4│6│
└─┴─┘       └─┴───┘       └─┴─┴─┘
```

中間的 `2:2:1` 是暫態：第 4 個 pane 暫時橫跨兩欄，下一次呼叫會補齊。補齊後，每欄的列高可以各自獨立調整，不會牽動鄰欄。

### 補齊時會短暫出現暫存 tab

補齊需要把橫跨兩欄的 pane 搬到暫存 tab 再搬回（Herdr 的 `pane move` 在同一 tab 內無效）。你會短暫看到名為 `grid-tmp` 的 tab，完成後自動關閉。pane 內的程序不會中斷。

### 自動排序

版面符合本腳本的成長序列時，新 pane 排在最後，所有 pane 以逐列順序排列。第 5 個 pane 開出後：

```
第一列：1, 2, 3
第二列：4, 5
```

- 判斷方式：目前的欄列數是否等於該 pane 數應有的形狀（5 個 pane 應為 `2:2:1`）。
- 不符合時（例如手動調整過版面、或舊版腳本建立的 tab）會略過排序，輸出中 `reorder_skipped` 會說明原因。
- 用 `layout` 可以事先看到「排序相容：是／否」。

### 自動平均分配

新增並排序後，預設會把所有欄寬與每欄內的列高平均分配。想保留手動調整的比例，使用 `--no-equalize`。

## 輸出

`split` 結束時印出 JSON：

```json
{
  "new_pane_id": "w3:p9",
  "name": "reviewer",
  "action": "add-row",
  "layout_after": "3:2:2",
  "reorder_swaps": 1,
  "equalized_steps": 4
}
```

| 欄位 | 說明 |
| --- | --- |
| `new_pane_id` | 新 pane 的 id，後續可接 `herdr agent start --pane <id>` |
| `action` | `add-column`、`fill`、`add-row` |
| `layout_after` | 完成後的欄列數 |
| `reorder_swaps` / `reorder_skipped` | 排序的 swap 次數，或略過原因 |
| `equalized_steps` | 平均分配套用的 resize 次數 |

## 常見問題

**舊 tab 調整列高時鄰欄一起動？**
舊版腳本建立的 tab 已經耦合，新版不會修復。請開新 tab 重新建立。

**出現 `grid-tmp` tab 沒有消失？**
補齊過程中搬回失敗時會保留它，錯誤訊息會附上復原指令，照著執行即可：
`herdr pane move <pane> --tab <原 tab> --split down --target-pane <上一列 pane>`。

**`版面結構不符預期，無法補齊寬 pane`？**
tab 內有非本腳本建立或手動調整過的版面。用 `layout` 查看結構，必要時改用新 tab。

**想先確認會做什麼？**
`next` 或 `split --dry-run` 只顯示計畫，不會改變版面。

## 已知限制

- 不修復既有已耦合的 tab。
- 同一 tab 不要並行呼叫 `split`，補齊過程中的暫態版面會被另一個呼叫讀到。
- 依賴 Herdr 的 `pane move` / `pane swap` 行為（以 herdr 0.9.0-preview 實測）；Herdr 更新後若行為改變，補齊邏輯集中於 `executeFill`。
