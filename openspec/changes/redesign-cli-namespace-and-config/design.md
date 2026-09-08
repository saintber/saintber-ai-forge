## Context

現行 `src/cli.js` 是一支單檔案 CLI，命令為 `init/update/doctor/list/remove`，全部透過 `--module`/`--modules` flag 過濾要處理的 selector；selector 只有單一維度（功能模組：code/copilot/docs/kb/migration/speckit），解析邏輯集中在 `getModuleSelectorFromFile()` 與 `resolveTemplateEntry()`。安裝狀態記錄在目標專案的 `.copilot-library/state.json`。套件本身沒有設定檔機制，也沒有把治理規則寫回目標專案入口檔的能力。

此設計文件源自 `/spectra-discuss`（本次 change 對應的討論）的結論：套件與 CLI 指令改名、指令介面改為子命令風格、命名空間簡化為單一 scope 維度、新增 policy 注入機制、新增全域／專案雙層設定檔。

## Goals / Non-Goals

**Goals:**

- 套件更名為 `@saintber/saifg`、CLI 指令改名為 `saifg`
- 指令介面由 flag 過濾改為子命令風格（`module add/remove/update/list`）
- 命名空間規則改為 `[org.|prj.|usr.]<module>.<name>.<type>.md`（單一 scope 維度 + 既有 module 維度）
- 提供 `saifg init`/`update` 自動偵測目標專案 provider、注入 policy 區塊與 `.saifg/policy.md`
- 提供 `saifg config get|set|list|path`，管理全域（`~/.saifg/config.yaml`）與專案層（`./.saifg/config.yaml`）設定，schema 為 `storage.{org,project,user}.{memory,keys,kb}`

**Non-Goals:**

- 不實作常駐 AI 助理啟動（line-webhook/telegram/排程常駐），留待後續獨立 change
- 不處理機敏內容本身的加密或存放邏輯，設定檔只存路徑指向
- 不實作 `usr.`/`org.`/`prj.` 資產內容的實際讀寫（記憶/金鑰/知識庫），本次只落地路徑設定的 CRUD
- 不遷移既有已安裝到其他專案的 `.copilot-library/state.json`；沿用相同檔案格式與位置命名為安裝狀態（是否改名為 `.saifg/state.json` 屬於本次實作範圍，但不處理既有安裝的自動遷移）

## Decisions

### 套件與指令更名為 @saintber/saifg / saifg

npm registry 只封鎖 package name 的重新註冊（尤其曾下架且下載量高的名稱），不管 `bin` 欄位宣告的指令名稱；`saifg` 目前在 npm 上完全無前例。套件名與指令名一致（`@saintber/saifg` → `saifg`），比照 `@angular/cli` → `ng` 的模式，兩者可以不同，但取一致命名更直覺好記。

**替代方案**：維持套件名 `@saintber/copilot-library`、只改 bin 為 `saifg` —— 拒絕，因為套件名稱應反映目前的角色定位（多 provider CLI 工具，不只是 Copilot 專屬），維持舊名會誤導。

### 指令介面改為子命令風格

`saifg module add|remove|update|list <selector...>` 取代 `saifg init/update/remove --module <ns1,ns2>`。`saifg init` 保留作為「首次於目標專案建立安裝狀態＋安裝所有預設模組」的獨立指令；`saifg update`（無子命令）作為語法糖，等同 `saifg module update`（不帶 selector＝全部已安裝模組）。

**替代方案**：保留 `--module` flag、只把動詞從 init/update/remove 改名 —— 拒絕，因為使用者要求對齊 `claude`/`hermes` 的動詞子命令體驗，且 `module add/remove/update/list` 的語意比 flag 過濾更明確表達「載入模組/卸載模組/更新已安裝模組」的操作意圖。

### 命名空間簡化為單一 scope 維度

檔名規則：`[<scope>.]<module>.<name>.<type>.md`，其中：
- `scope` ∈ `{org, prj, usr}`，省略即代表共用（無擁有者限定）
- `module` 沿用現有 `MODULE_DIRS`：`code/copilot/docs/kb/migration/speckit`
- `type` 沿用現有已知後綴：`instructions/agent/prompt/skill`

範例：`org.kb.company-policy.skill.md`（組織、kb 模組、company-policy、skill 類型）、`kb.faq.skill.md`（共用、kb 模組、faq、skill 類型）。

討論過程中曾考慮 `auto.`（助理自產、未經人工確認）作為與 scope 正交的第二維度，最終**拒絕**：`auto.` 描述的是目標專案執行期間的產物治理，不是套件本身要分類/打包的模板資產，因此移出套件的命名空間規則，改由 policy 內容治理（見下一項決策）。此語意變遷需記錄於 `openspec/LANGUAGE.md`。

### selector 解析邏輯重構

新增 `src/namespace.js`，取代現行 `src/cli.js` 內的 `getModuleSelectorFromFile()`／`resolveTemplateEntry()` 混合邏輯，職責分離：

- `parseAssetFilename(filename)` → `{ scope: 'org'|'prj'|'usr'|null, module: string, name: string, type: string } | null`
  - 依序嘗試比對已知 `type` 後綴（`.instructions.md`/`.agent.md`/`.prompt.md`/`.skill.md`）取出 base name
  - 將 base name 以 `.` 切割，第一段若屬於 `{org, prj, usr}` 則視為 scope，其餘（或全部，若無 scope 段）第一段視為 module
  - 非標準檔名（scripts/docs 類）沿用現行 `getFilenameFromRelativePath` 邏輯作為 fallback，不套用 scope 解析
- `matchesSelector(parsed, selectorFilter)` → boolean，取代現行 `matchesModules()`，selector filter 語法支援 `<module>`、`<scope>.<module>`、`<scope>`（僅比對 scope，忽略 module）三種形式

### config.js 的 global/local merge 邏輯

新增 `src/config.js`：

- `CONFIG_FILENAME = "config.yaml"`，全域路徑 `join(homedir(), ".saifg", CONFIG_FILENAME)`，專案路徑 `join(targetDir, ".saifg", CONFIG_FILENAME)`
- `readConfig(targetDir)` → 分別讀取全域與專案層 YAML（不存在則視為空物件），以**淺層 key path 合併**（`storage.org.memory` 這類巢狀 key，專案層若有設值即整個覆蓋對應的葉節點值，未設定的 key 才 fallback 到全域層），回傳合併後物件與 `{ key: 'global'|'project' }` 來源標記表（供 `config get --show-origin` 或除錯訊息使用，非本次必要功能但保留欄位）
- `writeConfigValue(targetDir, keyPath, value, { global })` → 寫入指定層（`global: true` 寫全域檔，否則寫專案檔），依 `.` 切割 `keyPath` 逐層寫入巢狀物件並保留該層其餘既有 key
- `readConfigValue(targetDir, keyPath)` → 呼叫 `readConfig` 取得合併結果後依 `keyPath` 取值

### entry 檔合併策略（policy 注入）

新增 `src/policy.js`，`saifg init`/`update` 執行時呼叫：

- Provider 自動偵測：依序檢查目標目錄是否存在 `CLAUDE.md`、`AGENTS.md`、`.github/copilot-instructions.md`；存在的每一個都視為要注入的 entry 檔（可能同時符合多個，逐一處理）
- 注入格式比照現行 Spectra 區塊寫法，使用 `<!-- SAIFG:START v<version> -->` / `<!-- SAIFG:END -->` 標記包住核心規則區塊；若檔案已存在同名標記區塊，替換區塊內容（保留區塊外的既有內容），否則附加到檔案末尾
- 核心規則區塊內容（精簡、每個 provider 相同）：說明 scope 命名規則（org/prj/usr/共用）、以及「助理自產內容一律歸類為 `usr.` scope、預設可被 git 追蹤（跨機器/組織成員同步）、不進 npm 發行」的治理原則
- `.saifg/policy.md`：由 `init` 一併安裝到目標專案（來源為套件內 `templates/policy/policy.md`），放置詳細說明與範例
- provider 差異化處理：
  - 若目標檔為 `CLAUDE.md`（Claude 支援 `@path` import 語法）：核心規則區塊內以 `@.saifg/policy.md` 語法引入完整內容，區塊本身可精簡
  - 若目標檔為 `AGENTS.md` 或 `.github/copilot-instructions.md`（無 import 機制）：核心規則需完整內嵌於區塊內，`.saifg/policy.md` 僅作為額外可讀參考，不依賴其被自動載入

## Implementation Contract

**行為（saifg module 子命令）**：
- `saifg module add <selector...> [--target <dir>]`：安裝符合 selector 的模板到 `<target>/.github`，更新 `.copilot-library/state.json`（沿用現行檔案位置與 schema，新增 `namespaceVersion: 2` 欄位標記已採用新命名空間解析器）。找不到符合 selector 的檔案時，回傳非 0 exit code 並印出 `Error: no files match selector(s): <selectors>`
- `saifg module remove <selector...> [--target <dir>]`：等同現行 `remove --module`，但參數改為位置參數而非 flag；找不到 state 檔時回傳非 0 exit code 並印出既有錯誤訊息
- `saifg module update [<selector...>] [--target <dir>]`：不帶 selector 時更新 state 中已追蹤的全部模組；帶 selector 時只更新指定範圍
- `saifg module list [--target <dir>]`：輸出格式沿用現行 `list` 指令（可用 module 清單 + 已安裝 module 清單），新增以 scope 分組顯示（若已安裝資產含 scope 前綴）
- `saifg update [--target <dir>]`：內部直接呼叫 `saifg module update`（無 selector）的實作函式，行為完全一致

**行為（saifg config 子命令）**：
- `saifg config get <key> [-g|--global] [--target <dir>]`：印出合併後（或指定層）的值；key 不存在時印出空字串並回傳 exit code 0（沿用一般 config 工具慣例，不視為錯誤）
- `saifg config set <key> <value> [-g|--global] [--target <dir>]`：寫入指定層（預設專案層），若對應層設定檔不存在則建立 `.saifg/config.yaml`（含目錄）
- `saifg config list [-g|--global] [--target <dir>]`：以 YAML 格式印出指定層（或合併後）的完整設定內容
- `saifg config path [-g|--global]`：印出指定層設定檔的絕對路徑（不驗證檔案是否存在）

**行為（policy 注入）**：
- `saifg init [--target <dir>]`：安裝完模板後，執行 provider 自動偵測與 policy 注入；若一個 provider 的 entry 檔都不存在，略過 policy 注入並印出提示訊息，不視為錯誤
- `saifg update [--target <dir>]`：同樣重新執行 policy 注入（確保區塊內容更新到最新版本），行為與 `init` 的注入邏輯共用同一函式

**資料格式**：
- `.saifg/config.yaml` schema：
  ```yaml
  storage:
    org:     { memory: <path>, keys: <path>, kb: <path> }
    project: { memory: <path>, keys: <path>, kb: <path> }
    user:    { memory: <path>, keys: <path>, kb: <path> }
  ```
  三個子層級皆為選填，缺漏的 key 在 `config get` 時視為未設定（回傳空字串）

**驗收條件**：
- `src/namespace.test.js`／`src/config.test.js`／`src/policy.test.js`（新增）涵蓋上述行為
- `node --test` 全數通過
- 手動執行 `saifg init --target <tmp>` 後，`<tmp>/.github` 內出現依 scope/module 分類的檔案，且 `<tmp>/CLAUDE.md`（若存在）出現 `<!-- SAIFG:START -->` 區塊

**範圍界線**：
- 本次僅新增 `saifg module/config` 子命令與 policy 注入機制，不變更 `templates/` 內既有模板檔案的實際內容（scope 前綴的實際套用是後續資產維護工作，非本 change 範圍）
- 不處理 `saifg` 全域安裝後與其他工具的 PATH 命令名稱衝突偵測

## Risks / Trade-offs

- [Risk] 破壞性變更：現有以 `--module` flag 呼叫本工具的使用方式（含 CI 腳本、README 範例）全部失效 → Mitigation：套件目前 0.3.0、非正式對外流通，可接受破壞性變更；README 與 CHANGELOG 需完整更新新舊指令對照
- [Risk] Provider 自動偵測誤判（例如目標專案同時有 `CLAUDE.md` 與 `.github/copilot-instructions.md`，但使用者只想用其中一種）→ Mitigation：兩者都存在時兩者都注入（各自符合各自 provider 的內嵌/import 規則），不強制二選一，因為兩份 entry 檔本來就是給不同 AI 工具讀取，互不影響
- [Risk] `.saifg/config.yaml` 專案層與全域層合併邏輯若實作錯誤，可能讓機敏路徑設定被錯誤層覆蓋 → Mitigation：`config.js` 的合併函式需有單元測試覆蓋「僅全域有值」「僅專案有值」「兩者皆有值取專案」三種情境
- [Risk] 現行 `.copilot-library/state.json` 命名與新套件名 `saifg` 不一致，可能造成使用者混淆 → Mitigation：本次不強制遷移狀態檔案位置/名稱，僅新增 `namespaceVersion` 欄位；是否改名狀態目錄留待下次獨立 change 評估，避免本次範圍擴大
