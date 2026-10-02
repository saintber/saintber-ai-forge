## 1. 腳本重構（已完成，記錄追溯）

- [x] 1.1 移除 `findMisalignedWidePane` 與 realign 流程，新增 `findWidePanes`，`planNext` 回傳 `fill` / `add-column` / `add-row` 三種模式；驗證方式：`node tools/herdr/grid-simulate.cjs` 輸出「整體結果：OK」（對應設計「逐列長欄，取代 swap + close 方案」）
- [x] 1.2 實作 `executeFill`：寬 pane 搬到暫存 tab `grid-tmp` → 搬回上一列下方 → 新欄最後一個 pane 往下切 → 關閉暫存 tab；搬回失敗時保留暫存 tab 並在錯誤訊息附上復原指令；驗證方式：使用者在 Personal 新 tab 連續執行 6 次 `split`，調整第 2、4 個 pane 列高時第 5、6 個 pane 不受影響（對應「Row heights stay independent per column」「Fill uses a temporary tab」）
- [x] 1.3 實作 `canonicalShape`、`rowMajor`、`planSwaps`、`reorderPanes`，版面相容時重新排序、不相容時略過；驗證方式：離線測試 2:2:1 狀態的 swap 清單為 `[["e","c"],["e","d"]]`，且 `canonicalShape(1..12)` 序列與目標一致（對應「Panes are reordered in row-major order」）
- [x] 1.4 新增 `--name`、`--no-reorder`、`--focus` 選項，`split` 完成後依序執行命名 → 排序 → 平均分配；驗證方式：人工執行 `split --name test` 後 `herdr pane list` 可見該名稱（對應「Optional pane name」）
- [x] 1.5 更新 `tools/herdr/grid-simulate.cjs` 為純欄列結構模擬並涵蓋 12 個 pane；驗證方式：`node tools/herdr/grid-simulate.cjs` 結束碼為 0

## 2. 規格與文件

- [x] 2.1 撰寫 `openspec/changes/herdr-grid-layout/` 的 proposal、design、spec；驗證方式：`spectra validate herdr-grid-layout` 無錯誤
- [x] 2.2 撰寫使用說明 `docs/tools/herdr/grid.md`，涵蓋用法、選項、行為、疑難排解；驗證方式：文件中的每個選項都能在 `tools/herdr/grid.cjs` 檔頭用法列找到
- [x] 2.3 於 `CHANGELOG.md` 的「未發布」加入本次異動條目；驗證方式：`grep -n "herdr-grid" CHANGELOG.md` 有結果

## 3. 歸檔與目錄調整

- [x] 3.1 腳本遷移至 `tools/herdr/`（`grid.cjs`、`grid-simulate.cjs`），新增 `tools/herdr/README.md`；驗證方式：`node tools/herdr/grid-simulate.cjs` 結束碼為 0，`ls scripts` 不再有 herdr 檔案
- [x] 3.2 執行 `spectra archive herdr-grid-layout`，將 delta spec 併入 `openspec/specs/herdr-grid/spec.md`；驗證方式：`spectra list --specs` 出現 `herdr-grid`
