# saifg

多 AI Provider 的資產安裝與治理 CLI 工具：安裝與更新 Instructions / Agents / Prompts / Skills，並自動將治理規則注入目標專案的入口檔（`CLAUDE.md`/`AGENTS.md`/`.github/copilot-instructions.md`）。

## 快速安裝（建議用 npx）

不需要先全域安裝，直接在目標專案目錄執行：

```bash
npx @saintber/saifg init
```

## 常用指令

```bash
npx @saintber/saifg init
npx @saintber/saifg module add org.kb,prj.code
npx @saintber/saifg module remove kb
npx @saintber/saifg module update
npx @saintber/saifg module list
npx @saintber/saifg update
npx @saintber/saifg doctor
npx @saintber/saifg config get storage.user.kb
npx @saintber/saifg config set storage.user.kb /path/to/kb
npx @saintber/saifg config list
npx @saintber/saifg config path
```

### 參數說明

- `--target <directory>`：目標目錄。
  - 未帶入時，預設為目前目錄（`.`）。
- `<selector...>`：`module add`/`module remove`/`module update` 的過濾條件，可逗號分隔或以多個參數帶入。
  - 支援 `<module>`（例如 `kb`）、`<scope>.<module>`（例如 `org.kb`）、`<scope>`（例如 `org`，比對所有屬於該 scope 的模組）三種形式。
  - 也相容既有的細粒度子命名空間選取（例如 `migration.dotnet-modernizer`）。
- `-g`／`--global`：`config` 指令專用，指定操作全域設定層（`~/.saifg/config.yaml`）；預設操作專案層（`<target>/.saifg/config.yaml`）。

## 資產命名規則

檔名格式：`[org.|prj.|usr.]<module>.<name>.<type>.md`，省略 scope 前綴代表共用資產。

- `org.`：組織層級專屬
- `prj.`：專案層級專屬
- `usr.`：個人層級專屬（含助理在執行期未經人工確認自行產生的內容）
- `module`：功能模組，例如 `code`/`copilot`/`docs`/`kb`/`migration`/`speckit`
- `type`：資產類型，例如 `instructions`/`agent`/`prompt`/`skill`

範例：`org.kb.company-policy.skill.md`、`kb.faq.skill.md`（共用）。

助理自產、未經人工確認的內容一律歸類為 `usr.` scope；這類內容預設可被 git 追蹤（跨機器/組織成員同步），但不進 npm 發行。詳見 `templates/policy/policy.md`（安裝後會落地到目標專案的 `.saifg/policy.md`）。

## 指令說明

- `init`：把套件內 templates 全部內容安裝到 `target/.github`，建立 `.copilot-library/state.json`，並執行 policy 注入（見下）。
- `module add <selector...>`：安裝符合 selector 的模板到 `target/.github`，更新已安裝狀態。
- `module remove <selector...>`：解除安裝符合 selector 的已追蹤資產；使用 `module remove all` 會完整移除所有已追蹤安裝內容並刪除 `.copilot-library/` 狀態目錄，但不會碰觸使用者原本未由本工具安裝的 `.github` 內容。
- `module update [<selector...>]`：更新已安裝資產；不帶 selector 時更新全部已追蹤模組。
- `module list`：列出目前可安裝的 module 清單；若目標專案已有 state，也會依 scope（org/prj/usr/共用）分組列出已安裝資產。
- `update`：`module update`（不帶 selector）的語法糖，等同更新全部已安裝模組。
- `doctor`：檢查目標目錄、state 檔案、安裝版本與目標檔案缺漏。
- `config get|set|list|path`：管理全域／專案層設定檔（`storage.{org,project,user}.{memory,keys,kb}` schema），專案層設定會覆蓋全域層的同一 key。
- `copilot-instructions` 特例：若 `target/.github/copilot-instructions.md` 不存在，會直接建立在根目錄；若已存在，則改安裝到 `target/.github/instructions/copilot-instructions.md` 供後續合併，避免覆蓋既有根檔內容。

## Policy 注入

`init`/`update` 會自動偵測目標專案已存在的入口檔（`CLAUDE.md`/`AGENTS.md`/`.github/copilot-instructions.md`），並在每個入口檔寫入/更新 `<!-- SAIFG:START -->...<!-- SAIFG:END -->` 治理規則區塊：

- `CLAUDE.md`（支援 `@path` import 語法）：區塊內以 `@.saifg/policy.md` 引入完整規則。
- `AGENTS.md`／`.github/copilot-instructions.md`（無 import 機制）：核心規則完整內嵌於區塊內。

## 設定檔

- 全域層：`~/.saifg/config.yaml`
- 專案層：`<target>/.saifg/config.yaml`（優先於全域層，同 key 時專案層覆蓋全域層）
- Schema：

  ```yaml
  storage:
    org:     { memory: <path>, keys: <path>, kb: <path> }
    project: { memory: <path>, keys: <path>, kb: <path> }
    user:    { memory: <path>, keys: <path>, kb: <path> }
  ```

專案層設定檔應加入該專案的 `.gitignore`（本套件本身已將 `.saifg/` 排除）。

## 目錄概要

- `bin/`：CLI 入口（`bin/saifg.js`）。
- `src/`：CLI 實作（`cli.js`／`namespace.js`／`config.js`／`policy.js`）。
- `templates/`：要部署到目標專案的 `.github` 內容（module 化）與 `templates/policy/policy.md`。

## AI 使用手冊

工具說明已改為 module README：
- `templates/code/README.md`
- `templates/copilot/README.md`
- `templates/docs/README.md`
- `templates/kb/README.md`
- `templates/migration/README.md`
- `templates/speckit/README.md`
