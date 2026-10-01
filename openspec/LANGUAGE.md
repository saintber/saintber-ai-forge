# 詞彙表（Vocabulary）

本文件記錄本專案的規範詞彙，供 `/spectra-discuss`、`/spectra-propose` 等流程在撰寫 proposal/design/specs 時對齊用語，避免同一概念在不同 change 之間出現不一致的定義。

## auto.

- **definition**：目標專案（安裝了本套件產出的資產後）在執行期間，由 AI 助理自行產生、尚未經人工確認的內容。這類內容一律歸類為 `usr.` scope（見下），並依 `entry-policy-injection` 治理規則管理：可被 git 追蹤（跨機器/跨組織成員同步），但不進 npm 發行。
- **avoid**：「`auto.` 作為套件模板命名空間的獨立前綴/維度」——不要在檔名規則或 selector 語法中把 `auto.` 當成與 `org./prj./usr.` 平行的第二個維度。
- **why**：`openspec/config.yaml` 最初的專案 context 曾規劃 `auto.`（暫定）作為與功能模組平行的第二種前綴，且敘述「助理自製 skill 與其知識庫會被 .gitignore 排除，不會進 commit」。在 `redesign-cli-namespace-and-config` change 的 `/spectra-discuss` 討論中，確認這個概念描述的是「目標專案執行期間的產物治理」，不是本套件自身要分類/打包的模板資產，因此：
  1. 命名空間維度收斂為單一 scope 維度（`org./prj./usr./共用`），移除 `auto.` 作為獨立維度。
  2. 治理原則同時反轉：`auto` 產物改為**預設可被 git 追蹤**（而非排除），只在「是否進 npm 發行」這一點上被排除。
  3. 治理規則改由 `saifg init`/`saifg update` 注入到目標專案入口檔（`CLAUDE.md`/`AGENTS.md`/`.github/copilot-instructions.md`）的 policy 區塊描述，而非套件自身的檔名規則或發行設定。

## scope（org. / prj. / usr.）

- **definition**：資產檔名的擁有者層級前綴，格式為 `[org.|prj.|usr.]<module>.<name>.<type>.md`：
  - `org.`：組織層級專屬
  - `prj.`：專案層級專屬
  - `usr.`：個人層級專屬（含助理未經人工確認自產的內容，見上）
  - 省略前綴：共用資產，無擁有者限定
- **avoid**：不要把 `scope` 與 `module`（功能模組，如 `code`/`copilot`/`docs`/`kb`/`migration`/`speckit`）混用或並列表述為同一件事；兩者是正交的兩個維度。
- **why**：`redesign-cli-namespace-and-config` 之前，命名空間只有 module 一個維度，無法表達「這份資產屬於誰」。新增 scope 維度以支援組織/專案/個人三層級資產分類。

## Assistant

- **definition**：`assistant/` 下的 .NET 常駐服務（命名空間 `Saintber.Assistant.*`），負責接收各輸入端（LINE、Telegram、CLI）的訊息、路由到內部 Topic、呼叫 AI CLI 並回覆。收訊只是其中一環，完整設計見 `docs/intents/assistant-design-v0.2.md`。
- **avoid**：不要再稱為 gateway／AI CLI Gateway（v0.1 舊稱）；也不要與專案層級「AI 助理」概念混用——後者是 `openspec/config.yaml` 所述以 AI CLI 為核心的助理資產與配套工具，`Assistant` 專指此常駐服務。
- **why**：v0.1 稱 Gateway，但完整設計包含身分映射、Topic、記憶與 AI 執行，遠超過單純轉送，故在 `assistant-line-connector` 討論中改名。

## Connector

- **definition**：Assistant 中負責單一輸入平台的整合模組（例如 `Connectors.Line`）：驗證來源（簽章）、解析為 `InboundEnvelope`、正規化 External Key、發送訊息與活動指示（loading／typing）。
- **avoid**：不要讓 Connector 判定「兩個外部帳號是否同一個人」或選擇 Topic，這屬於核心 Mapping／Topic Router；也不要把它稱為 channel 或 endpoint（endpoint 專指 HTTP 進入點，屬 Host）。
- **why**：v0.1 只寫「驗證來源」，v0.2 明確為「驗簽在 Connector 內完成，Host 不理解簽章格式」，以維持平台細節不外洩到核心。
