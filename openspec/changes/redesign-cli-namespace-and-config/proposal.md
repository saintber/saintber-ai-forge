## Why

現行 `@saintber/copilot-library`（CLI 指令 `saintber-copilot`）以 `--module` flag 過濾安裝內容，命名空間只反映「功能模組」（code/copilot/docs/kb/migration/speckit），無法表達「這份資產屬於組織、專案、還是個人」。同時，套件本身沒有機制把治理規則（例如助理自產內容的命名與版控原則）注入到目標專案的入口檔，也沒有統一的設定檔管理個人／專案／組織層級的機敏資訊存放位置。隨著本專案要對齊 `claude`、`hermes` 等主流 CLI 的「動詞子命令」操作體驗，且未來要支援多 provider（Claude、Copilot 等）的治理規則注入，現行架構需要一次性重新設計。

## What Changes

- **BREAKING**：套件更名為 `@saintber/saifg`，CLI 指令由 `saintber-copilot` 改為 `saifg`
- **BREAKING**：指令介面由 `--module` flag 改為子命令風格：
  - `saifg init [--target <dir>]`
  - `saifg module add|remove|update|list <selector...> [--target <dir>]`
  - `saifg update [--target <dir>]`（語法糖，等同 `module update` 全部模組）
  - `saifg doctor [--target <dir>]`
  - `saifg config get|set|list|path <key> [<value>] [-g|--global] [--target <dir>]`
- **BREAKING**：命名空間規則改為單一 scope 維度：`[org.|prj.|usr.]<module>.<name>.<type>.md`，共用資產無前綴；`module` 沿用現有 `code/copilot/docs/kb/migration/speckit`
- 移除舊 `auto.` 命名空間前綴的概念（原本規劃為套件模板分類用的第二維度），改為由 `saifg init`/`update` 寫入目標專案入口檔的 policy 內容治理，不再是套件自身的命名/發行規則
- 新增 policy 注入機制：`saifg init`/`update` 會偵測目標專案已存在的入口檔（`CLAUDE.md`/`AGENTS.md`/`.github/copilot-instructions.md`），寫入 `<!-- SAIFG:START/END -->` 核心規則區塊；支援 import 語法的 provider（如 Claude）額外安裝 `.saifg/policy.md` 供 import 引用，不支援 import 的 provider（如 Copilot）核心規則需完整內嵌
- 新增設定檔管理：全域 `~/.saifg/config.yaml` 與專案層 `./.saifg/config.yaml` 並存，同 key 專案層覆蓋全域層；`-g/--global` 旗標操作全域層；schema 為 `storage.{org,project,user}.{memory,keys,kb}` 路徑設定

## Capabilities

### New Capabilities

- `asset-namespace`: 以 `[org.|prj.|usr.]<module>.<name>.<type>.md` 檔名規則解析資產歸屬 scope 與功能模組，取代現行單純以模組前綴比對的邏輯
- `cli-module-management`: `saifg module add|remove|update|list` 子命令介面，取代 `--module` flag 式的 init/update/remove 過濾方式
- `entry-policy-injection`: `saifg init`/`update` 偵測目標專案入口檔種類，寫入/合併 `<!-- SAIFG:START/END --> ` 治理規則區塊與（視 provider 支援情況）`.saifg/policy.md`
- `cli-config`: `saifg config get|set|list|path` 管理全域與專案層設定檔，提供 org/project/user 三層的 memory/keys/kb 路徑設定，專案層覆蓋全域層

### Modified Capabilities

(none)

## Impact

- Affected specs: `asset-namespace`、`cli-module-management`、`entry-policy-injection`、`cli-config`（皆為新增）
- Affected code:
  - Modified: package.json、bin/copilot-library.js、src/cli.js、src/cli.test.js、README.md、.gitignore
  - New: bin/saifg.js、src/config.js、src/namespace.js、src/policy.js、templates/policy/policy.md
  - Removed: bin/copilot-library.js（改名為 bin/saifg.js 後移除舊檔）
