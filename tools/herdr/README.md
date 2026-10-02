# tools/herdr

在 Herdr（終端機多工器）內使用的輔助工具。需在 Herdr 內執行（`HERDR_ENV=1`），不隨 npm 套件發行。

| 工具 | 說明 | 使用說明 | 規格 |
| --- | --- | --- | --- |
| `grid.cjs` | 每次新增一個 pane，維持欄:列約 1:1 的網格，列高可獨立調整 | [docs/tools/herdr/grid.md](../../docs/tools/herdr/grid.md) | `openspec/specs/herdr-grid/spec.md` |
| `grid-simulate.cjs` | 離線驗證 `grid.cjs` 的欄列成長序列（不呼叫 Herdr） | 同上 | 同上 |

## 新增工具的慣例

- 檔名不帶 `herdr-` 前綴，目錄已表明範圍（例如 `tools/herdr/<name>.cjs`）。
- 使用說明放 `docs/tools/herdr/<name>.md`，並在上表登記。
- 有行為規格的工具，以 `spectra` 建立 change，capability 命名為 `herdr-<name>`。
