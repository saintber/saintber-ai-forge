# CHANGELOG

本文件記錄 CopilotLibrary 的版本異動歷程。

---

## [未發布]

### Breaking Change

- **套件與 CLI 指令更名**：套件由 `@saintber/copilot-library` 改為 `@saintber/saifg`，CLI 指令由 `saintber-copilot` 改為 `saifg`；`bin/copilot-library.js` 移除，新增 `bin/saifg.js`。
- **指令介面改為子命令風格**：`init`/`update`/`remove` 的 `--module`/`--modules` flag 過濾方式移除，改為 `saifg module add|remove|update|list <selector...>` 子命令；`saifg update`（無子命令）保留為 `module update`（不帶 selector）的語法糖；原有頂層 `list`/`remove` 命令移除，改由 `module list`/`module remove` 取代。
- **資產命名空間新增 scope 維度**：檔名規則由 `<module>.<name>.<type>.md` 擴充為 `[org.|prj.|usr.]<module>.<name>.<type>.md`，省略 scope 前綴代表共用資產；既有 templates 內容不變（尚未套用 scope 前綴），僅解析邏輯支援新格式。
- **新增 policy 注入機制**：`saifg init`/`saifg update` 會自動偵測目標專案入口檔（`CLAUDE.md`/`AGENTS.md`/`.github/copilot-instructions.md`），寫入/更新 `<!-- SAIFG:START/END -->` 治理規則區塊，並安裝 `.saifg/policy.md`；治理規則說明助理自產、未經人工確認的內容一律歸類為 `usr.` scope，可被 git 追蹤但不進 npm 發行。
- **新增設定檔管理**：新增 `saifg config get|set|list|path` 指令，管理全域（`~/.saifg/config.yaml`）與專案層（`<target>/.saifg/config.yaml`）雙層設定，schema 為 `storage.{org,project,user}.{memory,keys,kb}`，專案層覆蓋全域層同一 key。
- 新增 `src/namespace.js`（scope/module/name/type 解析）、`src/config.js`（設定檔讀寫）、`src/policy.js`（policy 注入）；`src/cli.js` 大幅重構以支援上述新指令介面。
- `.gitignore` 新增 `.saifg/`、`.copilot-library/`，避免本機測試/安裝殘留檔案誤入版控。

### 變更
- **Herdr 網格 pane 工具重構並遷移至 `tools/herdr/`**：`scripts/herdr-grid.cjs` 改為 `tools/herdr/grid.cjs`（離線模擬 `grid-simulate.cjs`）。新增 `--name`（新 pane 命名）、`--no-reorder`、`--focus`；新增欄改為逐列長出並以暫存 tab 搬移，使每欄列高可獨立調整；版面相容時自動將 pane 排成逐列順序；移除 realign。規格見 `openspec/specs/herdr-grid/spec.md`，使用說明見 `docs/tools/herdr/grid.md`。
- **AI 工具生產流程改版（Draft/Deploy/Release 階段化）**：
  - `ai/README.md`：將流程改為 module 內雙語 Draft（`ai/<module>/en/[type]`、`ai/<module>/zh-TW/[type]`），移除 `composed` 作為流程主節點，並明確 Deploy/Release 階段邊界。
  - `ai/manifest.yaml`：模組映射由 `composed` 調整為 `draft`，並改用 module 內 `en` / `zh-TW` 路徑。
  - `ai/copilot/instructions/copilot.maintenance.instructions.md`：更新維護治理為 Draft-first；未宣告 Deploy 前不得更新 `.github/`。
  - `ai/copilot/agents/copilot.maintainer.agent.md`：工作流程更新為先寫雙語 Draft，僅在 `deploy=true` 時寫入 `.github/`。
  - `ai/copilot/prompts/copilot.maintain.prompt.md`：輸入參數改為 `draft_en_path`、`draft_zh_tw_path`、`deploy`，移除 composed 導向語意。
  - `ai/copilot/README.md`：同步更新 copilot 模組使用與路徑說明，強化「未宣告 Deploy 不寫 `.github/`」規則。
  - `ai/copilot/sources/requirements/copilot.requirement-history.md`：新增本次需求原文與影響範圍記錄。
- **Deploy 發布（copilot maintain 三件套）**：已將 Draft 版維護規則正式同步到 `.github/instructions/copilot.maintenance.instructions.md`、`.github/agents/copilot.maintainer.agent.md`、`.github/prompts/copilot.maintain.prompt.md`。
- **中文 Draft 補齊（新流程目錄）**：新增 `ai/copilot/zh-TW/instructions/copilot.maintenance.instructions.md`、`ai/copilot/zh-TW/agents/copilot.maintainer.agent.md`、`ai/copilot/zh-TW/prompts/copilot.maintain.prompt.md`，補齊新流程下的繁體中文 Draft 產物。
- **英文 Draft 補齊（新流程目錄）**：新增 `ai/copilot/en/instructions/copilot.maintenance.instructions.md`、`ai/copilot/en/agents/copilot.maintainer.agent.md`、`ai/copilot/en/prompts/copilot.maintain.prompt.md`，補齊新流程下的英文 Draft 產物。

---

## [0.3.0] - 2026-04-30

### 變更
- **通用後端遷移重構工具鏈（保留 DI/IOC 專用流程）**：
  - `.github/prompts/migration.adopt-backend-modernization.prompt.md`：新增通用後端遷移重構命令，支援 `migration_objective`、`scan_scope`、`modify_scope`、可選 `depth_mode`、CSV 盤點輸出、雙向抽樣複檢、分批導入與最終驗證。
  - `.github/skills/migration.backend-modernization.skill.md`：新增可重用 skill 契約，統一通用盤點欄位（`File`、`Line`、`ReferencedObject`、`ProcessingStatus`、`Code`）、複檢規則與分批導入要求。
  - `.github/instructions/code.migration-conventions.instructions.md`：新增「Mandatory Backend Modernization Quality Loop」，將盤點 -> 複檢 -> 分批導入 -> 驗證規範擴展到 DI/IOC 以外的後端重構遷移類型，並保留深度參數為可選。
  - `.github/agents/migration.dotnet-modernizer.agent.md`：新增可重用通用後端遷移重構流程段落，明確 `migration_objective` 與可選 `depth_mode` 契約，維持 script-first、AI bounded partition 的品質策略。
  - 既有 `/migration.adopt-di-ioc` 與 `migration.di-ioc-adoption` 保持獨立可用，確保 DI/IOC 任務仍能精準執行。
  - 同步更新 `ai/migration/README.md`、`ai/migration/sources/requirements/migration.requirement-history.md`，並完成 `ai/composed/en/` 與 `ai/composed/zh-TW/` 對應檔案同步。
- **Template 與 Scripts 整合至 AI 演化流程**：
  - `src/cli.js`：擴展 `ARTIFACT_DIRS` 新增 `scripts` 與 `docs` 類型，支援 `templates/[module]/scripts/` 與 `templates/[module]/docs/` 目錄結構，同時維持向後相容性（舊版 `[type]/` 根目錄路徑仍被支援）；更新 `resolveTemplateEntry` 與 `getModuleSelectorFromFile` 函數以支援新類型檔案（如 `*.template.ps1`、`*.template.md`）；新增完整註釋說明 artifact 類型與 extended types 的區分。
  - `/templates/migration/scripts/`：新增 DI/IOC 盤點指令稿 `di-ioc-inventory-script.template.ps1`（移從 `ai/migration/templates/`），支援 `direct-hit` 與 `recursive-search` 掃描深度、CSV 輸出與標準狀態欄位。
  - `/templates/migration/docs/`：新增 DI/IOC 導入工作流程指南 `DI-IOC-ADOPTION-GUIDE.md`（移從 `ai/migration/DI-IOC-ADOPTION-GUIDE.md`）、Pending Clarification 範本 `di-ioc-clarification-template.md`（移從 `ai/migration/templates/`），指向新的 templates 路徑。
  - `ai/migration/README.md`：更新資源連結，指向新的 `/templates/migration/scripts/` 與 `/templates/migration/docs/` 位置；移除已遷移的直接文件連結。
- **Migration 模組目錄結構優化**：遷移 DI/IOC 工具相關文件至 templates 發行層，使得 CLI `init/update` 命令可直接管理腳本與文件資源；scripts 與 docs 納入模組化安裝流程，確保使用者安裝時能同時獲得完整的工作指南與範本。
- **CLI 邊界測試**：驗證 `templates/[module]/scripts/` 與 `templates/[module]/docs/` 在 `init`、`update`、`remove`、`doctor` 命令中的完整覆蓋；確認模組選擇器 (`--module migration`) 能正確篩選新增的文件類型。
- **原有 DI/IOC 導入工具鏈持續支援**：`migration.di-ioc-adoption` skill、`/migration.adopt-di-ioc` prompt、`migration.dotnet-modernizer` agent 等 AI 流程保持穩定，與新增的 template 腳本與文件相輔相成。
- **migration DI/IOC 導入工具鏈**：`migration.di-ioc-adoption` skill 與 `/migration.adopt-di-ioc` prompt 建立 legacy 專案 DI/IOC 導入標準流程（盤點 -> 雙向抽樣複檢 -> 逐步導入 -> 最終驗證），支援 `scan_scope`、`modify_scope`、`depth_mode`（`direct-hit`/`recursive-search`）、分區掃描策略、CSV 輸出（`File`、`Line`、`ReferencedObject`、`ProcessingStatus`、`Code`），並新增 static class `new` 目標之 `Pending Clarification` 與問題釐清文件輸出要求；同步補強 `migration.dotnet-modernizer` agent 與 `code.migration-conventions.instructions.md` 的穩定規範。
- **CLI 代碼文件調整以符合 ai-toolchain-workflow 架構設計**：
  - `src/cli.js`：增強架構註釋，明確說明 authoring 層 (`ai/`)、deploy 層 (`.github/`) 與 release 層 (`templates/`) 的角色與流向；改進 `resolveTemplateEntry`、`collectTemplateEntries`、`resolveDestinationPath` 等核心函數的可讀性和文檔；確保完全支持新的模組優先 (`templates/[module]/[type]/`) 目錄結構與平坦部署層 (`.github/[type]/`) 映射；添加 `targetPath` 到狀態跟蹤（符合 ai-toolchain-workflow.md §10 Installer State Minimum Schema）。
  - `src/cli.test.js`：增強測試註釋與文檔，直接引用 ai-toolchain-workflow.md 各相關章節；確保所有測試驗證新架構的模組化安裝、平坦部署、state 追蹤與 copilot-instructions.md 雙軌部署邏輯；驗證用戶內容保護與追蹤移除行為完整性。
- **`.copilot` 內容遷移到 `ai/` 新架構**：執行非破壞性遷移，完成 `base -> ai/<module>/<type>/`、`composed -> ai/composed/zh-TW/<module>/<type>/`、`sources/requirements -> ai/<module>/sources/requirements/` 的批次搬移；共搬移 124 個檔案，5 個既有目標檔採 skip 保留（未覆蓋）；遷移明細輸出於 `migration-log-copilot-to-ai.txt`。
- **copilot maintain 三件套參數清理**：移除未被流程使用的參數 `existing_instruction`、`existing_agent`、`existing_prompt`、`existing_skill`（同步更新 `.github` 與 `ai/composed` 對應 prompt/agent），並將輸入參數 `new_requirement_zh_tw` 更名為 `new_requirement`，避免非必要語系後綴。
- **新 authoring 層 `ai/` 建立**：依 `docs/policy/ai-toolchain-workflow.md` 新設計，建立 `ai/` 目錄骨架（module-first, type-second）；包含六個模組（`copilot`、`migration`、`kb`、`docs`、`code`、`speckit`）、七種 artifact 類型目錄、`sources/requirements/`、以及雙語 composed 層（`ai/composed/en/`、`ai/composed/zh-TW/`）。
- **`ai/README.md`**：建立 authoring 層導覽說明，記錄模組清單、目錄結構與 authoring 流程。
- **`ai/manifest.yaml`**：建立 module → composed → publish path 對應表，供 CLI 與 deploy 工具解析。
- **`copilot.maintenance.instructions.md`**：authoring 層路徑從 `.copilot/` 全面改為 `ai/`；composed 層拆分為 `ai/composed/en/<module>/`（English deploy-ready）與 `ai/composed/zh-TW/<module>/`（繁中備份）；更新 `applyTo` 範圍為 `ai/**/*.md`；更新 `copilot-instructions.md` canonical 路徑；更新 module README 路徑為 `ai/<module>/README.md`；更新 Every `.github/` 同步規則。
- **`copilot.maintainer.agent.md`**：內嵌治理路徑全面改為 `ai/`；Inputs 新增 English deploy-ready 輸出路徑參數；步驟 8 同時寫入 `ai/composed/en/` 與 `ai/composed/zh-TW/`；Release 步驟從 `ai/composed/en/` 同步至 `templates/`。
- **`copilot.maintain.prompt.md`**：`requirement_storage_path` 預設改為 `ai/<module>/sources/requirements/`；`composed_path` 預設改為 `ai/composed/zh-TW/<module>/`。
- **`ai/copilot/README.md`**：建立 copilot module 的新 README（取代 `.copilot/copilot/README.md` 作為往後的正式路徑），更新 authoring 路徑說明與需求歷程路徑。
- **`ai/copilot/sources/requirements/copilot.requirement-history.md`**：建立需求歷程記錄檔，保存本次建構需求原文。
- **`ai/composed/en/copilot/`**：建立 English deploy-ready composed 輸出（instruction、agent、prompt）。
- **`ai/composed/zh-TW/copilot/`**：建立繁體中文備份 composed 輸出（instruction、agent、prompt）。
- **可重用需求記錄器（skill-first）**：新增 `.github/skills/copilot.requirement-recorder.skill.md` 與 `.copilot/copilot/composed/skills/copilot.requirement-recorder.skill.md`，建立可被多個 agent/prompt 共用的原始需求記錄器，支援 `chronological`、`versioned-basic`、`versioned-structured` 三模式，預設 `chronological` + `/docs/histories`。
  - `copilot.maintenance.instructions.md`：加入可重用需求記錄器的穩定規範，明確模式行為、路徑預設、發布 Draft 遷移規則，以及「外部指定優先」覆寫原則。
  - `copilot.maintainer.agent.md`、`copilot.maintain.prompt.md`：加入記錄器參數契約與流程要求，強化 skill-first 與低 usage（避免不必要 handoff）策略。
  - `.copilot/copilot/sources/requirements/copilot.requirement-history.md`：新增本次需求原文與影響範圍記錄。
- **`copilot-instructions.md` 雙軌部署策略**：`src/cli.js` 調整安裝策略為安全雙軌：若目標專案 `.github/copilot-instructions.md` 不存在，直接安裝到 `.github/` root；若已存在，改安裝到 `.github/instructions/copilot-instructions.md`，避免覆蓋既有根檔並保留後續合併空間。`copyFiles` 改為回傳實際落地路徑，`init/update` 以實際路徑寫入 state，確保 `doctor/remove` 對 staged 與 root 兩種路徑都能正確追蹤。`src/cli.test.js` 新增 2 個測試，驗證 `copilot-instructions.md` 在「目標根檔不存在」與「目標根檔已存在」兩種情境下的安裝與 state 追蹤行為。
- **`copilot.merge-copilot-instructions` Prompt**：新增 `.github/prompts/copilot.merge-copilot-instructions.prompt.md`（及 `.copilot/copilot/base|composed` 對應檔），提供去重/合併流程，將 `.github/instructions/copilot-instructions.md` 合併到 `.github/copilot-instructions.md`；若目標不存在則直接建立。
- `README.md`、`.copilot/copilot/README.md`、`templates/copilot/README.md`：補充新安裝策略與新 prompt 說明。

---

## [0.2.2] - 2026-04-09

### 變更
- 清理 non-canonical 路徑：移除 `.copilot/copilot-instructions/**`，避免與 canonical `.copilot/copilot/**` 並存造成維護流程誤判與雙軌更新風險。
- `copilot.maintenance.instructions.md`、`copilot.maintainer.agent.md`、`copilot.maintain.prompt.md`：新增 canonical 路徑治理，明確規範 `copilot-instructions.md` 只能維護於 `.copilot/copilot/base|composed/instructions/`；若偵測 `.copilot/copilot-instructions/**` 等 non-canonical 路徑，必須標記為 skipped 並禁止雙寫。
- `.copilot/copilot/README.md`：補充 `copilot-instructions.md` 的 canonical authoring 路徑說明，並將需求歷程預設路徑示例更新為 module 化結構（`.copilot/<module>/sources/requirements/`）。
- `copilot-instructions.md`：內容改為英文版，並強化語言規則：所有對話過程必須使用繁體中文（zh-TW）；除非使用者明確要求其他語言，文件輸出預設為繁體中文，專有名詞與關鍵字可保留英文。
- `src/cli.js` `list` 命令：輸出改為只顯示頂層模組清單（不再列出子選擇器），並自動讀取各模組 README 第一段描述文字右對齊顯示；已安裝模組同樣只顯示模組名稱與說明。
- `copilot.maintenance.instructions.md`：新增穩定規則——每個 module README 緊接 H1 標題後的第一段落為 CLI `list` 模組說明，模組用途/能力變更時需同步保持準確。
- `copilot.maintainer.agent.md`：工作流程第 11 步補充，明確要求在行為變更時保持 module README 描述段落準確性。

---

## [0.2.1] - 2026-04-07

### 新增

- Module README 使用手冊（`.copilot/<module>/README.md`、`templates/<module>/README.md`），取代原有 TOOLS.md 單體說明，涵蓋 `code`、`copilot`、`docs`、`kb`、`migration`、`speckit` 六個命名空間，提供 agents / prompts / instructions / skills 的用途與使用案例。
- `copilot.maintain-test.prompt.md`：臨時維護流程測試用 prompt（`.github/` 為英文版，`.copilot/composed/` 保留繁體中文版，驗證完成後應予移除）。

### 變更

**維護治理流程（copilot 模組）**

- `copilot.maintain` 主流程改為 module 化 authoring 模型：需求歷程預設路徑改為 `.copilot/<module>/sources/requirements/`；維護更新寫入 `.copilot/<module>/base/` 與 `.copilot/<module>/composed/`；release 時同步至 `templates/<module>/`。
- `copilot.maintenance.instructions.md`：`applyTo` 範圍收斂至自有維護產物，避免套件使用者自有規則或外部 vendor AI 誤觸維護流程。
- `copilot.maintainer.agent.md`：核心維護治理內嵌至 agent，確保無論 `applyTo` 是否命中，English normalization、CHANGELOG / composed 同步規則仍強制生效。
- `copilot.maintain.prompt.md`：新增委派契約規則，不得因 `applyTo` 未命中而略過 `copilot.maintainer` 完整維護治理。
- `src/cli.js`：新增 module 化 templates 解析能力，支援 `templates/<module>/{agents|instructions|prompts|skills}` 並維持舊版根目錄模板相容。
- `.copilot/README.md`、`README.md`：更新為 module 目錄結構與新維護流程說明。

**架構文件工具（docs 模組）**

- `docs.architecture-documenter.agent.md`、`docs.hybrid-reviewer.agent.md`、`docs.hybrid-review-executor.agent.md`：釐清三者職責差異，新增 `inventory_mode` / `change_mode` 控制參數。
- `docs.architecture-review.prompt.md`、`docs.document-architecture.prompt.md`：新增 `inventory_mode` 與 `include_dependencies` 控制，支援完整 repository 架構盤點而無需修改 source code。
- `docs.hybrid-review-execute.prompt.md`：新增 `change_mode`（`docs-only` | `docs-and-plan` | `apply-code`），明確區分僅產出報告、提出計畫與實際修正程式碼三種模式。
- `docs.hybrid-review-executor.agent.md`：新增 `terminal/runInTerminal` 工具授權，允許執行 PowerShell 自行驗證結果（build / test / 檔案檢查）；補充 Verification rules 段落，明確禁止執行破壞性或影響共用基礎設施的命令。

### 已移除

- `.github/TOOLS.md`、`.copilot/composed/TOOLS.md`、`templates/TOOLS.md`：廢棄移除；工具說明改由各 module README 承接。

---

## [0.2.0] - 2026-04-02

### 新增
- 新增 `copilot.speckit-customizer` 專用 agent，用於將 Speckit 客製意圖轉為精確 overlay 規格，並在上游 Speckit 升版後重複套用。
- CLI 新增 `list` 命令，可列出目前可安裝的 module selectors，並在目標專案已有 state 時顯示已追蹤的已安裝模組。
- CLI 新增 `remove --module <ns>` 命令，可依模組安全解除安裝已追蹤的 CopilotLibrary 檔案。
- CLI 支援 `remove --module all`，可完整移除所有已追蹤安裝內容並清除 `.copilot-library/` 狀態目錄，同時保留使用者原有的 `.github` 項目。

### 變更
- `/copilot.apply-speckit-customizations`：改為由 `copilot.speckit-customizer` 執行，並將原始 Speckit 客製需求改寫成精確 overlay 契約，而非直接內嵌需求原文。
- `copilot.maintenance.instructions.md`：移除 Speckit 專屬細節，回復為共通維護規範；改以「vendor 基線保留」與「領域邏輯放專用 agent/prompt」兩條通則表達。
- `copilot.maintainer.agent.md`：移除 Speckit 專屬目標與流程，維持為通用維護 agent。
- `copilot.speckit-customizer.agent.md` 與 `/copilot.apply-speckit-customizations`：簡化為使用者專案適用的輕量 overlay 流程；預設不再依賴完整 `/copilot.maintain`、複雜 changelog/release 或 requirement history 步驟，但仍保留 English normalization、語意合併與 `.copilot/composed/` 繁中同步。
- `copilot.speckit-customizer.agent.md`：新增明確規則，要求寫入 `.copilot/composed/` 的文件必須是對應 `.github/` 產物的全文繁體中文完整版本，不得只翻譯新增的客製化片段；並於 Guardrails 補充禁止產出中英混雜或部分翻譯版本。
- `copilot.apply-speckit-customizations.prompt.md`：強化輕量 Overlay 規則第 6 條，明確指出 composed 檔案必須是整份文件的全文繁體中文版本；同步補回「文件產物語言」既定 Overlay 目標（第 5 項）。
- `.copilot/composed/agents/copilot.speckit-customizer.agent.md`、`.copilot/composed/prompts/copilot.apply-speckit-customizations.prompt.md`：修正為全文繁體中文的完整版本，反映對應 `.github/` 產物目前最新內容。
- `.github/TOOLS.md`：新增 `copilot.speckit-customizer` agent，並更新 `/copilot.apply-speckit-customizations` 說明與對應 agent。
- `src/cli.js`：`state.json` 會額外記錄 `installedFiles`，`doctor` 會優先依追蹤清單檢查，避免模組化安裝時誤判缺漏。
- `copilot.maintain` release 規則：當使用者明確指定發布版號時，除 CHANGELOG 封版與 `.github/` → `/templates/` 同步外，也必須同步更新 `package.json` 的 `version` 欄位。

---

## [0.1.0] - 2026-04-01

初版正式發布，整合 Copilot 客製化函式庫完整基礎建置、CLI 工具、產物命名空間化、維護治理流程，以及 Speckit 外部廠商管理。

### 新增

#### 基礎建置
- `.copilot/` 製作層初始化：含 `sources/`、`base/`、`composed/`、`changelog/`、`manifest.yaml` 子目錄結構。
- `.github/instructions/`、`.github/agents/`、`.github/prompts/`、`.github/skills/` 初始發布目錄建立。
- `AGENTS.md`：Copilot Coding Agent 全域入口規則。
- `CHANGELOG.md`：建立版本異動記錄。

#### CLI 工具
- `cli.js`：完整實作 `init`、`update`、`doctor` 三個 CLI 命令；`init`/`update` 將 `/templates` 內容複製至 `target/.github`，並於 `.copilot-library/state.json` 記錄版本、安裝時間與已安裝模組；`doctor` 檢查目標目錄、state.json、已安裝版本與缺漏檔案。
- CLI 新增 `--modules`/`--module` 參數，支援以逗號分隔的多個 namespace（含 sub-namespace），適用於 `init`、`update`、`doctor` 命令。範例：`--modules kb,copilot`、`--modules migration.dotnet-modernizer`。
- CLI 參數預設值：`--target` 未提供時預設使用目前目錄（`.`）；`--module`/`--modules` 未提供時預設套用所有命名空間。
- `README.md`：以一般使用者視角撰寫安裝與使用說明，並指向 AI 使用手冊 `/.github/TOOLS.md`。

#### Copilot 產物庫（命名空間化）
- 命名空間版 instructions 發布：`code.tech-stack.instructions.md`、`code.migration-conventions.instructions.md`、`copilot.maintenance.instructions.md`、`docs.architecture.instructions.md`、`kb.governance.instructions.md`。
- 命名空間版 agents 發布：`migration.solution-architect.agent.md`、`migration.dotnet-modernizer.agent.md`、`migration.mvc-kendo-migrator.agent.md`、`migration.db-architect.agent.md`、`migration.platform-infra-architect.agent.md`、`migration.reviewer.agent.md`、`docs.architecture-documenter.agent.md`、`docs.hybrid-reviewer.agent.md`、`docs.hybrid-review-executor.agent.md`、`copilot.maintainer.agent.md` 及 `kb.*` 群組各 agent。
- 命名空間版 prompts 發布：`migration.*`、`docs.*`、`kb.*`、`copilot.maintain.prompt.md`。
- `.github/TOOLS.md`：建立完整命名空間工具說明，涵蓋 `copilot`、`docs`、`migration`、`kb`、`code` 各系列能力、使用方式與應用範例。
- `.copilot/composed/`：建立所有 `.github/` 產物的繁體中文對應副本（含 instructions / agents / prompts / skills）。
- 舊版非命名空間 artifacts 完成清理，轉為對應 namespace 指令與檔案。
- `.copilot/README.md`：重寫，加入目錄職責表與三步驟產製流程（Step 1 建立需求 → Step 2 確認來源 → Step 3 執行 `/copilot-maintain`）。

#### 維護治理流程
- Copilot 維護流程三件套建立：`copilot.maintenance.instructions.md`、`copilot.maintainer.agent.md`、`copilot.maintain.prompt.md`，含 release workflow、requirement history 正式模板（`Recorded At`、`Change Summary`、`Affected Artifacts`、`Original Requirement`）與 release 時 `[未發布]` 條目移轉規則。
- Requirement history 管理：以命名空間為單位，統一記錄於 `.copilot/sources/updates/<namespace>/<namespace>.requirement-history.md`，依版本段落反序累積。
- Release 同步範圍明確化：release 時須將 `.github/` 全部內容（含 `TOOLS.md`）一併同步至 `/templates/`。

#### 文件與知識庫工具
- Docs 架構審查能力：`docs.architecture.instructions.md` 新增 Hybrid Architecture & Specification Review Pipeline 規則；`docs.architecture-documenter.agent.md` 支援 review 與 authoring 雙模式。
- Hybrid review pipeline 產物：`docs.hybrid-reviewer.agent.md`、`docs.hybrid-review-executor.agent.md`、`docs.hybrid-review.prompt.md`、`docs.hybrid-review-execute.prompt.md`、`docs.document-architecture.prompt.md`、`docs.hybrid-review-pipeline.skill.md`。
- `docs/` SDD 文件初始化：`constitution/`、`context/`、`intent/`、`plan/`、`policy/` 各子目錄。

#### Speckit 外部廠商管理
- Speckit 產物自 `.github/` 移除：因 Speckit 改由微軟官方維運，將 speckit instruction / agents / prompts（共 19 個）移出 `.github/`。
- 繁體中文備份保留於 `.copilot/composed/speckit-backup/`，含 `README.zh-TW.md` 說明。
- `.copilot/composed/instructions/`、`agents/`、`prompts/` 下建立 speckit 繁中翻譯版本（description 與常見章節標題 zh-TW 化）。
- `copilot.maintenance.instructions.md`：新增廠商維運規則，Speckit 只保留 `.copilot/composed/speckit-backup/` 備份，不在 `.github/` 發布。

---

## 記錄規則

- 後續發版時，請自 `[未發布]` 切出版號，格式為 `[x.x.x] - YYYY-MM-DD`。
- 每版僅保留與公開行為相關的變更；詳細需求歷程請見 `.copilot/sources/updates/` 各命名空間歷程檔。
