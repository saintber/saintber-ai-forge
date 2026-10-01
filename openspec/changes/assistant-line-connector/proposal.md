## Why

專案要提供常駐的 AI 助理，需要外部溝通管道作為傳輸層。設計稿 `docs/intents/assistant-design-v0.2.md` 規劃了完整的 Assistant（身分映射、Topic、記憶、AI 執行），但一次做完風險過高。本 change 的重點是**完成 Connector 的實作與測試，並以 LINE 作為第一個實作**：

- 讓 Host 只認得「收」與「發」兩個介面，平台細節（驗簽、reply／push、loading、重送）全部留在 Connector 內。
- 提供通用的 Webhook 型 Connector，讓新增平台（Telegram webhook 等）只需要寫平台自己的 API 呼叫。
- Connector 做成可由組態載入的獨立 dll，未來才能擴充而不改 Host。

## What Changes

- 新增 .NET 10 解決方案 `assistant/`（命名空間 `Saintber.Assistant.*`），與現有 `saifg` Node CLI 完全獨立，不納入 npm 發行內容。
- 新增 `Abstractions`：Host 與 Connector 之間的契約——收（`IInboundMessageHandler`）、發（`IOutboundGateway`）、`IConnector`、`IWebhookReceiver`、`IConnectorFactory`，以及 `InboundEnvelope`／`OutboundEnvelope`／`ExternalKey`。
- 新增 `Connectors.Core`：通用 Webhook 型 Connector，負責驗簽→解析→去重→先回 200 的共同流程、Connector 內部的事件處理（並行與等待上限、停止清理）、reply 憑證快取（綁定原始聊天室）與 reply／push 選擇、活動提示（loading／typing）生命週期、逾時與事件預算；並定義平台轉接層介面 `IMessagingPlatform`（Reply、Push、Activity 的聯集，附能力宣告）與 `IWebhookInbound`（Verify、Parse）。
- 新增 `Connectors.Line`：LINE 的平台轉接層，只實作 LINE 的驗簽、事件解析、reply／push／loading API 呼叫。
- 新增 `Host`：通用路由 `POST /webhook/{instanceId}`、啟動時依組態載入 Connector dll、發送閘道、echo handler、健康檢查。
- 新增 echo handler，用以證明收發路徑可用（Host 的預設 handler）；`Assistant:Echo:Mode=push` 可讓 echo 走真實的 push 路徑以供人工驗收。
- 新增容器交付：`Containerfile`（build context 為 `assistant/`）、`compose.yaml`、`.env.example`、`.dockerignore`，Docker 與 Podman 皆可執行；LINE 的 dll 預設放在映像內。
- 新增文件：容器安裝、本機執行、LINE 人工測試手冊、架構與責任邊界。
- 新增 xUnit 測試（單元、整合、架構邊界）與離線假 webhook 腳本。
- 新增 GitHub Actions：restore → build → test → 以 docker 與 podman 各建置容器映像（不發佈）。
- 根目錄 `.gitignore` 加入 `data/`（secret 與本機資料）。
- 同步更新 `docs/intents/assistant-design-v0.2.md`。

## Non-Goals

以下明確不在本 change 範圍，避免範圍蔓延：

- AI CLI 呼叫、Provider Adapter、Context／Memory。
- Person／Space／Topic 映射與路由、跨平台身分綁定。
- Host 與 Connector 之間的發送佇列（Queue）、Topic lock、任何持久化（PostgreSQL、Redis、SQLite）；發送介面只預留「已接受」語意，1a 直接呼叫。Connector 內部先回 200 再處理事件的記憶體工作不屬於發送佇列，不跨程序、不持久化。
- Telegram 與自製 CLI 的 Connector，以及輪詢型通用 Connector 的基底類別（僅預留 `IConnector` 生命週期契約）。
- 執行中不重啟的 Connector 熱載入與卸載；跨容器外部載入 dll 的情境（只做啟動時依組態載入）。
- 非文字訊息（圖片、貼圖等）的處理，僅忽略並記錄。
- 容器映像發佈到 registry、自動部署（CD）。
- 使用 LINE 官方或第三方 SDK。

## Capabilities

### New Capabilities

- `connector-framework`: 通用 Webhook 型 Connector——共同收訊流程、重送去重、reply 憑證與 reply／push 選擇、活動提示生命週期、逾時與批次預算、平台轉接層介面與能力宣告。
- `line-connector`: LINE 的平台轉接層——驗簽、事件解析為 `InboundEnvelope` 與 External Key、reply／push／loading API 呼叫、文字長度限制。
- `assistant-host`: webhook 路由、Connector 載入與生命週期、收發介面（handler 與發送閘道）、echo handler、設定、健康檢查、依賴方向。
- `assistant-container-delivery`: Docker／Podman 皆可執行的容器化、build context 隔離、交付文件與人工測試手冊、假 webhook 工具、CI 建置與測試。

### Modified Capabilities

（無；目前沒有既有 spec 可修改。）

## Impact

- 新增目錄：`assistant/`（`src/` 四個專案、`tests/`、`docs/`、`scripts/`、`Containerfile`、`compose.yaml`、`.env.example`、`.dockerignore`）。
- 新增：`.github/workflows/`（assistant CI）；`docs/intents/assistant-design-v0.2.md` 同步更新；`openspec/LANGUAGE.md` 已新增 Assistant、Connector 詞條。
- 修改：根目錄 `.gitignore`（加入 `data/`）。
- 不修改：`src/`、`ai/`、`templates/`、`package.json`。
- 新增外部相依：.NET 10 SDK／執行環境；容器引擎（Docker 或 Podman）；LINE Messaging API（reply、push、loading）；GitHub Actions。
- 運維影響：LINE 要求 webhook 在 2 秒內回應，因此 Connector 先回 200 再處理；去重、reply 脈絡與事件工作都在記憶體中，重啟會遺失，只支援單一程序。LINE push 訊息會計入官方帳號的訊息額度（reply 不計）；只有 reply 憑證過期或訊息非回覆時才會用 push。實際額度規則需對照 LINE 官方文件與帳號方案確認。
- 驗收限制：開發機目前只有 podman，沒有 docker，Docker 路徑的實測須由使用者在具備 docker 的環境驗收。
