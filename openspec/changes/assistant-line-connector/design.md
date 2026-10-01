## Context

設計稿 `docs/intents/assistant-design-v0.2.md` 定義完整的 Assistant。本 change 只實作其中的 **1a：Connector 與 LINE**，重點是把「Host ↔ Connector」的契約、通用 Webhook 型 Connector 與 LINE 平台轉接層一次定義清楚，讓之後加 Telegram、CLI、AI 執行與發送佇列時，不需要改動已完成的部分。

現況：

- 本 repo 是 Node ESM 專案（`@saintber/saifg`），沒有 .NET 程式碼、容器檔、`.dockerignore` 或 CI 設定（`.github/` 只有 agents、instructions、prompts、skills）。
- 開發機有 .NET SDK 10.0.x 與 podman 6.0.2，沒有 docker。
- `openspec/specs/` 目前為空，四個 capability 皆為新增。
- 專案既有約定：外部溝通管道的 secret 一律放 `data/` 並被 `.gitignore` 排除（見 `openspec/config.yaml`）。

本版 artifacts 由三輪討論形成：第一輪規格審閱（去重鍵進不了平台無關契約、失敗語意、逾時、build context 憑證隔離、reply token 外洩、去重記憶體上限、Host 固化 LINE 規則、架構測試反例）；使用者提出「Host 只管收發、Connector 可獨立 dll、通用 Connector 底下有平台介面」；第二輪審閱（LINE webhook 須在 2 秒內回應、去重保證與淘汰矛盾、reply 快取缺 Chat 綁定、人工 push 驗收無可達路徑、事件編號仍經由 metadata 進入 Host）。再經一輪審閱（Running 狀態下不合作的 handler、lease 撤銷規則、活動資源所有權、`Accepted` 語意、early-ack 測試條件衝突、重送 token 策略），並由使用者新增「Connector 實例清單（通用欄位與自訂欄位）」需求。以下決策已由使用者確認。

## Goals / Non-Goals

**Goals:**

- Host 只依賴兩個介面：收（`IInboundMessageHandler`）與發（`IOutboundGateway`）；Connector 保證把訊息送入 Host，並接收 Host 的回應。
- 通用 Webhook 型 Connector 承擔共同流程，新平台只需實作 `IMessagingPlatform` 與 `IWebhookInbound`。
- Connector 先回 200 再於自身內部處理事件，滿足 LINE 的 webhook 回應期限，並讓 AI 階段不必重做此流程。
- LINE 作為第一個平台：驗簽、解析、reply、push、loading。
- Connector 為獨立 dll，啟動時依組態載入；LINE 放入映像作為預設。
- Connector 實例以組態中的清單管理：通用欄位（類型、實例 ID、是否啟用、dll、顯示名稱）與自訂欄位（平台專屬與通用層調校參數，由 factory 以描述宣告並統一驗證），平台參數可只改設定不改程式。
- 依賴方向、容器 build context、secret 與傳輸層資訊不外洩都有可自動驗證的規則。
- Docker 與 Podman 都能以同一份 `Containerfile` 與 `compose.yaml` 建置與啟動，並附文件。

**Non-Goals:**

- AI CLI、Context／Memory、Person／Space／Topic 映射、跨平台綁定。
- Host 與 Connector 之間的發送佇列與任何持久化。Connector 內部的記憶體事件處理不是發送佇列：它不跨程序、不持久化，也不由 Host 存取。
- Telegram、CLI Connector，以及輪詢型通用 Connector 的基底類別（只預留 `IConnector` 生命週期契約）。
- 執行中不重啟的熱載入與卸載、跨容器外部載入 dll；Connector 清單以資料庫（SQLite 等）儲存、執行期清單的 HTTP 端點與管理介面、組態熱載入（修改清單需重啟 Host）。
- 跨請求的訊息順序保證（屬於日後 Topic 路由的責任）。
- 非文字訊息、群組與聊天室的 loading（由 LINE 平台能力決定，不是 Host 的責任）。
- 映像發佈、CD、LINE SDK。
- 為單一平台預先建立共用的契約測試基底類別；待第二個平台出現時再抽出。

## Decisions

### 技術棧：.NET 10 LTS、ASP.NET Core Minimal API、xUnit

四個 src 專案、各有對應 xUnit 測試專案；`Directory.Build.props` 統一啟用 `Nullable`、`ImplicitUsings`、`TreatWarningsAsErrors`，`global.json` 鎖定 SDK 主版本 10。

替代方案：Node.js（與現有專案一致）。已與使用者討論後改用 .NET：容器化與 CI/CD 本來就需要獨立工具鏈，設計稿的介面與後續 PostgreSQL／pgvector 生態較貼近 .NET。代價是維護者需要懂兩種語言，以「兩個產物完全獨立」降低耦合。

### 依賴方向與責任邊界

| 專案 | 負責 | 不負責 | 允許的 Assistant 相依 |
| --- | --- | --- | --- |
| `Abstractions` | Host ↔ Connector 契約與共用型別 | 任何平台細節、I/O | 無（外部套件只允許 `Microsoft.Extensions.Logging.Abstractions`） |
| `Connectors.Core` | 通用 Webhook 型 Connector、平台轉接層介面 | 任何單一平台的 API 與格式 | `Abstractions` |
| `Connectors.Line` | LINE 的驗簽、解析、API 呼叫 | 去重、reply／push 選擇、loading 生命週期、事件處理預算 | `Abstractions`、`Core` |
| `Host` | HTTP 路由、Connector 載入與生命週期、發送閘道、echo handler、設定 | 任何平台欄位或平台規則；不引用 `Core` 與 `Line` | `Abstractions` |

規則以兩層測試強制：(1) 解析各 `.csproj` 的 `ProjectReference` 與 `PackageReference`；(2) 以 reflection 檢查輸出組件的 `GetReferencedAssemblies()`。反例使用**可編譯的 fixture**（例如讓 `Connectors.Line` 多引用一個獨立的假專案），不使用會造成 project reference 循環的反例。測試專案本身不受規則限制（Host 整合測試需要引用 LINE）。

替代方案：NetArchTest。目前只需要組件層級的檢查，多一個相依不划算；若之後需要型別層級規則再引入。

### Host 只認得收與發兩個介面

`Abstractions` 定義：

- `IInboundMessageHandler.HandleAsync(InboundEnvelope, CancellationToken)`：Host 實作，Connector 呼叫（「收」）。
- `IOutboundGateway.SendAsync(OutboundEnvelope, CancellationToken)` → `SendAcceptance`（`Accepted`、`UnknownConnector`、`ConnectorUnavailable`）：Host 提供，依 `ConnectorType` 與 `ConnectorInstanceId` 找到對應 Connector 並呼叫其 `DeliverAsync`（「發」）；語意見「發送介面：已接受語意，1a 直接呼叫」。
- `IConnector`：`ConnectorType`、`InstanceId`、`StopBudget`（最壞停止時間，由 Connector 自己回報，讓 Host 不必引用 Core 就能算出關閉預算）、`StartAsync(IInboundMessageHandler, ct)`、`StopAsync(ct)`、`DeliverAsync(OutboundEnvelope, ct)` → `DeliveryAcceptance`（`Accepted` 或 `Unavailable`）。輪詢型 Connector 之後以 `StartAsync` 啟動自己的迴圈，不需要改契約。
- `IWebhookReceiver.ReceiveAsync(WebhookRequest, ct)` → `WebhookResult(StatusCode)`：webhook 型 Connector 額外實作的可選介面；Host 只提供一條通用路由，不理解簽章。
- `IConnectorFactory`：`ConnectorType`、`Settings`（`IReadOnlyList<SettingDescriptor>`，宣告該類型的自訂欄位）與 `Create(ConnectorCreationContext)`（平台才有的額外設定規則在此驗證）；context 含該實例的組態、`ILoggerFactory`、`TimeProvider`、建立 `HttpClient` 的委派。

`InboundEnvelope` 與 `OutboundEnvelope` 不含任何平台憑證或傳輸層資訊（reply token、事件編號、重送資訊）。

### Connector 實例清單：通用欄位與自訂欄位

「目前可用的 Connector」由兩件事構成：**可載入的 dll**（見下一決策）與**實例清單**——組態中的 `Connectors`，每個實例有通用欄位與自訂欄位。清單讓平台參數可以只改設定、不改程式。

**清單形式：以實例 ID 為鍵的物件，不是陣列。** .NET 組態依索引與葉節點合併陣列，較短的陣列會留下尾端元素，重排索引會讓某個來源的憑證落到另一個實例上（已用 .NET 10 實測確認）。以穩定的實例 ID 為鍵，各來源依實例 ID 合併，不受順序與長度影響：

```json
{
  "Connectors": {
    "line": {
      "Type": "line",
      "Enabled": true,
      "Assembly": "line/Saintber.Assistant.Connectors.Line.dll",
      "DisplayName": "LINE 主帳號",
      "Settings": {
        "ChannelSecret": "（放環境變數）",
        "LoadingSeconds": 5,
        "Work": { "MaxConcurrency": 4 },
        "Timeouts": { "Event": "00:00:30" }
      }
    }
  }
}
```

等價的環境變數：`Connectors__line__Type=line`、`Connectors__line__Settings__ChannelSecret=...`、`Connectors__line__Settings__Work__MaxConcurrency=4`、`Connectors__line__Settings__Timeouts__Event=00:00:30`。

| 欄位類別 | 欄位 | 說明 |
| --- | --- | --- |
| 通用欄位（Host 理解） | 鍵（即 `InstanceId`） | 小寫字母開頭，只含小寫字母、數字與單一底線（不可連續底線、不可連字號），最長 32 字元；比對不分大小寫，一律正規化為小寫。webhook 路由為 `/webhook/{InstanceId}` |
| | `Type` | 對應 `IConnectorFactory.ConnectorType` |
| | `Enabled` | 預設 true；false 時不載入 dll、不建立路由（請求回 404） |
| | `Assembly` | 相對於 `Assistant:ConnectorsPath` 的 dll 路徑 |
| | `DisplayName` | 選填，只用於日誌與啟動清單 |
| 自訂欄位（`Settings`） | 由 Connector 類型定義 | 平台專屬參數（如 LINE 的 `ApiBaseUrl`、`LoadingSeconds`）、通用層調校參數（去重 TTL 與上限、逾時秒數、並行與等待上限等）、secret |

**分層只能新增與覆寫，不能移除**：要停用實例必須明確設 `Enabled=false`。只有 `Settings`（例如環境變數打錯實例 ID）而沒有 `Type` 的殘缺條目會使啟動失敗並指名實例 ID。**每個實例頂層只允許 `Type`、`Enabled`、`Assembly`、`DisplayName`、`Settings`**（不分大小寫）：未知欄位（如 `Enabeld`、`Asssembly`，否則 `Enabled` 會落回預設 true、`Assembly` 會落回 base 的舊 dll）與形狀錯誤（`Settings` 是純量、`Enabled` 不是布林）一律使啟動失敗，訊息只含實例 ID 與鍵名，不含值。這是固定的結構檢查，不擴充 `SettingDescriptor` 的驗證描述語言。

**自訂欄位的讀取規則**（.NET 的階層設定不能直接綁成字串字典：實測 `Settings` 的子節點中 `Work` 為 null，`Get<Dictionary<string,string>>` 會丟掉 `Work:MaxConcurrency`）：Host 遞迴走訪 `Settings` 下的所有葉節點，轉成相對的冒號鍵（如 `Work:MaxConcurrency`）再對照描述驗證。鍵不分大小寫，驗證後統一為描述的大小寫；同一個鍵既有值又有子節點（如 `Work=5` 與 `Work:MaxConcurrency=8`）時啟動失敗並指名鍵。

**時間長度格式**：必須是帶冒號的 `hh:mm:ss`（可含天數 `d.hh:mm:ss` 與小數秒），以 Invariant culture 解析；單獨的數字（例如 `60`）一律拒絕，因為 `TimeSpan.Parse("60")` 會被解讀為 60 天（已實測）；整數與小數以 Invariant culture 解析。

**自訂欄位以描述宣告**：factory 公開 `SettingDescriptor` 清單（鍵名、型別〔字串、整數、時間長度、布林〕、是否必要、預設值、是否 secret、最小與最大值、說明）。通用 Webhook Connector 的調校參數描述由 `Connectors.Core` 提供，LINE factory 合併自己的平台描述。**驗證分兩層**：Host 依描述統一驗證結構、型別與範圍——未知鍵、型別錯誤、超出範圍、缺必要鍵一律使啟動失敗，訊息只含實例 ID 與鍵名，不含任何值，secret 的值永不輸出；平台才有的額外規則（例如 LINE 的 `LoadingSeconds` 必須是 5 的倍數）由 factory 在 `Create` 時驗證，錯誤同樣只回鍵名。描述維持簡單，不建立完整的驗證描述語言。描述同時是文件（`assistant/docs/settings-reference.md`）與清單顯示的來源。

**儲存方式評估（已決定採用現有的 .NET 組態分層）**：

| 方案 | 優點 | 缺點 |
| --- | --- | --- |
| **組態分層：映像內 `appsettings.json` ＜ 可選的外部 JSON 檔 ＜ 環境變數（採用）** | 無新相依；與既有 `env_file`、secret 放 `data/` 的約定一致；容器內不需 volume 也能運作；測試簡單 | 變更需重啟 Host；外部 JSON 檔需自行維護 |
| 專用的 `connectors.json` 由程式自行讀寫 | 與 appsettings 分離 | 自行維護格式、覆寫與驗證，等於重造組態系統 |
| SQLite | 可由程式在執行期編輯 | 引入持久化（本 change 的 Non-Goal）、需要 volume 與 schema 管理、需要管理介面才有意義，secret 存入資料庫另有風險 |

採用分層的具體安排：映像內的 `appsettings.json` 提供範例與預設；可選的 `Assistant:ConfigFile` 指向外部 JSON 檔，只放非機密設定，容器以唯讀掛載提供（掛載時需留意 rootless Podman 的 volume 權限，文件說明）。**`Assistant:ConfigFile` 未設定才略過；明確指名但檔案不存在或讀不到時啟動失敗並只列設定鍵**，因為靜默略過會讓預期的 `Enabled=false` 或 `ApiBaseUrl` 覆寫完全失效。環境變數（`env_file`）優先級最高，放 secret 與覆寫。變更清單需重新啟動 Host；執行期熱載入與資料庫儲存待有持久層與管理介面的需求（設計稿的 PostgreSQL 階段）再評估，屆時只需換組態來源，`SettingDescriptor` 與 `ConnectorInstanceConfig` 不受影響。Host 於啟動時把清單摘要（類型、實例 ID、是否啟用、顯示名稱、webhook 路徑）寫入日誌，不含任何 `Settings` 值；本 change 不提供執行期清單的 HTTP 端點（避免暴露拓撲，也屬於管理介面）。

### Connector 以獨立 dll 啟動時依組態載入

組態中的 Connector 實例清單（見「Connector 實例清單：通用欄位與自訂欄位」）中，`Enabled` 為 true 的每個實例由 Host 在啟動時載入。Host 依**正規化後的完整 dll 路徑**建立 `AssemblyLoadContext`（「載入環境」，dll 的程式碼與 static 變數所在之處）：解析到相同路徑的實例共用同一個載入環境與同一份 factory，各實例仍各自呼叫 `Create`；不同路徑各自獨立。路徑正規化為完整路徑並解開符號連結，Windows 不分大小寫、Linux 分大小寫。以 `AssemblyDependencyResolver` 解析其相依（依 dll 旁的 `.deps.json`），並**固定由 Host 提供** `Saintber.Assistant.Abstractions` 與 `Microsoft.Extensions.Logging.Abstractions`（避免同一型別在兩個載入環境中被視為不同型別）；在 dll 內掃描 `IConnectorFactory` 實作，依 `Type` 對應。

- 只載入組態指定的檔案，路徑不得離開 `ConnectorsPath`。
- 共用載入環境的前提：Connector 的程式碼不得把實例自己的狀態放在 `static` 欄位；去重登記、reply 脈絡、worker 與 lease 都屬於各自的實例，因此同一個 dll 的兩個實例互不干擾，停止其中一個不影響另一個。由於本版不卸載，一個路徑對應一個載入環境，生命週期與程序相同。
- 載入前檢查該 dll 參考的 `Abstractions` 主版本與 Host 相同，不同則啟動失敗並指名檔案與雙方版本。
- 載入失敗、找不到 factory、`InstanceId` 重複、設定缺漏，一律使啟動失敗；錯誤訊息只含檔名、實例 ID、版本與缺少的設定鍵名，不含任何設定值。
- 一個 Connector 的載入單元是「dll＋`.deps.json`＋其自帶相依」的資料夾（以 `dotnet publish` 的輸出為準）；文件需列出此單元。載入測試必須針對乾淨的 publish 輸出（或容器內 `/app/connectors/line/`）進行，而不只依賴測試專案的引用，避免缺少相依被遮蔽。
- 本版不支援卸載與執行中新增；契約與載入器保留此擴充空間。LINE 的 dll 發佈到映像內的 `connectors/line/`，作為預設。

風險：載入 dll 等同執行任意程式，能讀到所有設定。以「只從固定資料夾載入」、容器內該資料夾唯讀為最低防線；是否加雜湊允許清單，待開放外部掛載時再決定。

### 通用 Webhook Connector 與 IMessagingPlatform

`Connectors.Core` 提供 `WebhookConnector`（實作 `IConnector` 與 `IWebhookReceiver`），由某個平台實作組成：

- `IWebhookInbound`：`Verify(WebhookRequest)`、`Parse(WebhookRequest)` → `IReadOnlyList<PlatformInboundEvent>`。`PlatformInboundEvent` 含 `EventId`（去重鍵）、`EventTime`（平台事件時間）、`ReplyToken?`、`InboundEnvelope`。驗簽失敗擲 `WebhookVerificationException`、payload 不合法擲 `ConnectorPayloadException`；平台不處理的事件（非文字、缺 userId、缺事件編號、standby）由 `Parse` 略過並記錄類型。
- `IMessagingPlatform`：`Capabilities`（`Reply`、`Push`、`Activity` 旗標，另含 `ReplyValidity`）、`ReplyAsync`、`PushAsync`、`StartActivityAsync(chat)`（不適用時回傳 null）。這是 LINE／Telegram／CLI 動作的**聯集**，各平台宣告自己支援哪些；不支援的動作由通用層略過或改路徑，不以例外表示。

命名說明：不採用 `Provider`，因為設計稿的 `Provider` 已用於 AI Provider；採用 `IMessagingPlatform` 指「訊息平台的 API 轉接層」。收端與送端拆成兩個介面，輪詢型之後只需要實作送端與自己的收端。

替代方案：只取三平台共有的最大公約數。Reply 為 LINE 獨有（一次性 token），取公約數會丟掉 LINE 最需要的能力，故改為聯集加能力宣告。

### 先回 200，再於 Connector 內處理事件

LINE 官方將「2 秒內沒有回應 webhook」列為 `request_timeout`（已對照官方的 webhook 錯誤統計文件確認）；reply token 的有效期不是 HTTP 回應的期限。因此將「回應」與「處理」分離：

```
POST /webhook/{instanceId}
  驗簽 → 解析 → 對每個事件（同一短閘門內）：檢查 Running 與重複 → TryWrite 排入有界 Channel
          → 成功才登記事件並建立 reply 脈絡（失敗＝過載丟棄，不登記）→ 回 200
                                        │
                  Connector 內部工作（固定 worker 從 Channel 取出，先取得同一閘門確認登記已提交）：
                  超齡則丟棄 → 啟動活動提示 → 呼叫 Host handler → handler 經 gateway 送出 → 結束活動提示
```

- 回應路徑不呼叫任何平台 API，也不等待 handler；它只做驗簽、解析、去重、排入。
- **接受（admission）資料流**：內部工作使用有界 `Channel`（`FullMode=Wait`、只呼叫 `TryWrite`、不 `await WriteAsync`，以免拖慢回應；`AllowSynchronousContinuations=false`，避免消費者在回應路徑內同步執行）加固定數量的 worker。`TryWrite` 成功就是真的寫入，不是容量預約，所以不能「先寫入再撤銷」。在同一個短閘門內依序：檢查狀態為 Running 與事件編號是否仍登記（重複直接丟棄）；對新事件只呼叫一次 `TryWrite`；失敗時不登記、不建立 reply 脈絡、不造成登記淘汰，只記錄過載；成功時完成事件登記與 reply 脈絡，再釋放閘門。worker 在開始處理每個工作前取得同一閘門，確認登記已提交且仍可啟動才標記為 running，因此不會在登記完成前啟動活動提示或 handler。通道寫入與登記之間不接受請求取消（改用不可取消的權杖）。
- **登記不變式**：只有「成功入列」的事件才有登記；過載丟棄不登記，之後重送在有容量時會被正常處理。已成功入列但之後因排隊超齡或停止而被丟棄的事件仍保留登記，登記代表「曾成功入列」，不代表「仍在等待佇列中」。
- 事件工作由 Connector 擁有，不隨 HTTP 請求中斷而取消，只在 Connector 停止或超過事件預算時取消；每個工作記錄 `EnqueuedAt`，處理預算從 worker 開始處理時起算。
- 這是 Connector 內的記憶體處理，不是 Host 與 Connector 之間的發送佇列：不持久化、不跨程序、Host 看不到它。

替代方案：同步處理並把整條路徑縮進 2 秒內（活動約 0.3 秒、reply 約 1 秒、整批約 1.5 秒，皆為估計）。LINE API 延遲稍高時會常態性沒有回覆，且 AI 階段無論如何必須先回 200，屆時要回頭改 Connector 流程與 spec，故不採用。

### External Key 的 canonical 格式

| 來源 | Kind | CanonicalValue | Properties |
| --- | --- | --- | --- |
| 使用者 | `user` | `line:user:<userId>` | `{"userId":"..."}` |
| 群組 | `group` | `line:group:<groupId>` | `{"groupId":"..."}` |
| 聊天室 | `room` | `line:room:<roomId>` | `{"roomId":"..."}` |
| 訊息 | `message` | `line:message:<messageId>` | `{"messageId":"..."}` |

1:1 聊天的 `Chat` 與 `Actor` 為同一個 user key；`Thread` 恆為 `null`。`ConnectorInstanceId` 不編入 canonical value，而是獨立欄位。事件編號（`webhookEventId`）只用於去重，不編成 External Key，也不進入 Host 契約。

`ExternalKey.Properties` 為獨立 `Clone()` 的 `JsonElement`（不再是需要 `Dispose` 的 `JsonDocument`），因此跨早回 200 與背景處理的生命週期不需要有人負責釋放。`CanonicalValue` 與 `Properties` 由平台的單一 factory 從同一來源（種類與 ID）同時產生，避免兩者不一致；LINE 在送出前驗證 envelope 的 Chat key 的 canonical 與 Properties 一致（例如 canonical 為 `line:user:U123` 時 Properties 不可是 `userId` 為 `U999`），不一致則記錄為無法送達、不送出，確保邏輯上的 Chat 與實際送出目的地相同。

### 重送去重搬入通用 Connector

鍵為 `PlatformInboundEvent.EventId`；每個 Connector 實例擁有自己的去重集合，因此作用域天然包含 `ConnectorInstanceId`。重送是傳輸層現象，由 Connector 吸收，Host 只會收到不重複的訊息。

- 檢查與登記為單一原子操作（處理同時到達的重複請求）。
- TTL 預設 10 分鐘（估計值，對應 LINE 的重送時間窗，需對照官方文件確認），`Dedup:Ttl` 設定值必須為正，否則啟動失敗。
- 記憶體上限 `Dedup:MaxEntries` 預設 10000（估計值，依一般訊息量保守取值，以實際流量確認），登記滿載時淘汰最舊登記並記錄警告。登記只在事件成功入列時建立（見上一決策）；工作佇列本身另有等待上限，過載時新事件不登記。
- 過期登記必須被清理：每次登記時清理已過期者，另有固定間隔的背景清理，確保沒有新事件時記憶體也會收斂。
- **保證的精確範圍**：只在「登記仍保留期間」內保證同一事件不被重複處理。容量淘汰會提前結束被淘汰事件的保護窗，使其在 TTL 內重送時被再次處理；這是為有界記憶體所接受的取捨（拒收新事件會丟掉真正的新訊息，比偶發重複 echo 更糟）。
- **登記儲存的結構**：以事件編號為鍵的登記，另有訊息 key 的索引指向共享的 reply 脈絡（見下一決策）；兩者是同一份登記儲存的兩個索引，不是兩套各自淘汰的快取。
- 其他已知限制：只保證同一程序內；多 replica 與重啟後失效。首次處理超過 TTL 時，同一事件重送可再處理一次。

### reply token 留在 Connector 內，以 InReplyTo 對應

reply token 是一次性、短效、平台專屬的憑證，不進入 Host 契約。通用 Connector 解析後，把 reply 脈絡 `Message key → (ReplyToken, 原始 Chat, 有效起點, 已用旗標)` 存在自己的記憶體中，作為**由事件登記共享的單一物件**：同一個訊息 key 的多筆事件登記在該脈絡保留期間重用同一個脈絡物件，不重設已用旗標與原有效期。脈絡的去留由所有仍保留、且引用它的事件登記共同決定：沒有任何保留登記引用時才移除，因此脈絡數量不超過登記數，仍然有界，也沒有獨立的墓碑。

- **有效起點**取 `min(收到時間, 平台事件時間)`，期限為平台宣告的 `ReplyValidity`（LINE 設為 50 秒，為估計值；LINE 官方說明為約 1 分鐘內有效且不保證，以實測 token 過期時間確認）。因此遲到的重送事件不會被視為新 token。這是**刻意保守的本地策略**，不是平台有效期的模型，50 秒與事件時間都不是 LINE 的保證。已知成本：LINE 官方文件指出重送事件中的 reply token 在特定情況外仍可使用，但本策略在重送事件的事件時間已早於有效期時，即使 token 尚未使用也會直接 push，可能多一筆 push 費用並錯失仍可用的 reply；此限制已接受。若要依平台規則調整（例如讓 LINE 平台依是否為重送自行提供 reply 截止時間，使重送資訊仍不進入 Host 與 Core），需先對照官方文件確認確切規則並由使用者決議。
- **Chat 為準**：`OutboundEnvelope.Chat` 是目的地，`InReplyTo` 只是提示。reply 只在原始 Chat 與 envelope 的 Chat 相同時使用；不同時直接 push 到 envelope 的 Chat，不消耗 token，並記錄警告。目的地不因 token 年齡而改變。
- **原子保留**：「有效、未用、Chat 相符」的檢查與「標為已用」必須是單一原子操作；已消耗的 token 在該脈絡保留期間保持已用，同一 message key 的後續登記（包含不同事件編號）重用同一個脈絡，不得把它重設為未用。
- **保證範圍**：「已用」只保證到脈絡被移除為止。若所有引用它的登記都被淘汰後同一訊息又被重新登記，會建立新脈絡並可能再次嘗試同一個 token；平台通常會拒絕，依下表記錄、不補 push，屬已接受的 best-effort 遺失（不加無上限的墓碑）。平台是否拒絕不是 Core 能保證的結果。

| 條件 | 動作 |
| --- | --- |
| `InReplyTo` 有值、脈絡存在且未過期未使用、Chat 相符、平台支援 Reply | reply，並原子標為已用 |
| `InReplyTo` 有值、脈絡存在且 Chat 不符 | push 到 envelope 的 Chat（不消耗 token，記錄警告） |
| `InReplyTo` 無值，或脈絡不存在、本地已過期、token 已用，且平台支援 Push | push |
| 以上皆不可用 | 記錄無法送達並結束 |
| reply 已嘗試但平台回錯（含平台拒絕 token） | 記錄錯誤，**不自動改用 push**（結果不明時重送可能重複回覆，也避免無預期的額度消耗） |

「本地快取到期改用 push」與「平台 API 拒絕 reply 不補 push」是兩個獨立行為，需分別測試與說明。

### 活動提示（loading／typing）由 Connector 管理

通用 Connector 在事件工作中、呼叫 Host handler 前，呼叫平台的 `StartActivityAsync(chat)`；回傳的 `IAsyncDisposable` 以被回覆訊息的 Message key 保存。當該訊息的回覆送出（`DeliverAsync` 的 `InReplyTo` 相符）、超過活動上限時間（`Activity:MaxDuration`，預設 2 分鐘，估計值）或 Connector 停止時 dispose，且每個 disposable 只 dispose 一次。Host 完全不知道活動提示存在。

**逾時後才回傳的結果（Running 狀態也適用）**：活動呼叫超過逾時（預設 2 秒）後 worker 即繼續呼叫 handler，晚到的結果一律**不登記**，在抵達時立即 dispose 恰好一次，並觀察其例外（不讓未處理的 task 例外外洩）。逾時之前回傳的結果才正常登記。因為 worker 先等活動呼叫再呼叫 handler，所以「結果在回覆之後才到、卻仍在逾時之前」不會發生，競態只出現在逾時路徑上。Stopped 之後才回傳的結果適用同一規則。Core 的承諾限於**framework 自己擁有的狀態**：不留登記、dispose 恰好一次、不再啟動本地計時器或重發。平台端已發生的效果依平台的期限收斂：LINE 的 loading 動畫只會在 `LoadingSeconds` 到期或下一則訊息到達時消失，沒有取消操作，所以 LINE 平台的 disposable 是 no-op；逾時後才啟動的 loading 可能在回覆之後還顯示數秒，此限制寫入人工測試文件。

平台自行決定適用性：LINE 對一對一聊天發出 loading，對群組與聊天室回傳 null；未來 Telegram 的「輸入中」只維持數秒，由其平台實作以計時器週期性重發，並在 dispose 時停止。活動提示失敗只記錄警告，不影響後續處理。

### 發送介面：已接受語意，1a 直接呼叫

`IOutboundGateway.SendAsync` 回傳 `SendAcceptance`，三種結果的語意互相獨立：

| 結果 | 意義 | 何時 |
| --- | --- | --- |
| `Accepted` | Connector 取得送出許可並已呼叫其送出路徑；之後的平台投遞失敗只記錄，仍回 `Accepted`（沒有可用路徑，例如脈絡已過期且平台不支援 Push，也只記錄） | 目標 Connector 存在且允許送出 |
| `UnknownConnector` | 找不到對應的 Connector 實例 | 類型或實例 ID 不存在，或實例為停用 |
| `ConnectorUnavailable` | 已知的 Connector 拒絕送出，不呼叫平台；Host 將來可據此決定是否重新排入 | Stopping 且無有效 lease、Stopped、攜帶已撤銷的 lease |

`IConnector.DeliverAsync` 回傳 `DeliveryAcceptance`（`Accepted` 或 `Unavailable`），由 gateway 對應。「已接受」只承諾 Connector 取得許可，不承諾已送達；把「拒絕」與「平台投遞失敗」混為一談會讓 Host 無法判斷該不該重新排入。1a 的實作在呼叫端執行緒中直接呼叫 Connector 的 `DeliverAsync`，將來換成佇列實作時，Host 不需要改動（屆時「已接受」是指排入佇列）。

替代方案：在程式內以 `Channel<T>` 建立記憶體佇列與背景送出。這只是同一個介面背後的另一種實作；1a 不採用。

### 失敗語意：best-effort、最多嘗試一次

驗簽通過後，webhook 一律回 200。單一事件在活動提示、handler 或送出任一步驟失敗，只記錄錯誤，不重試、不影響其他事件；因事件編號已登記，LINE 的重送會被丟棄，所以可能出現「使用者沒有收到回覆」。此外下列情況也會遺失：進程停止時尚未啟動或超過寬限期仍未完成的事件、程序崩潰、事件工作過載被丟棄（此類事件不登記，重送在有容量時可被處理；所有 worker 都 overrun 的 Degraded 期間同理）、入列後因排隊超齡被丟棄（保留登記）、去重滿載淘汰造成的重複。這是 1a 的明確取捨：echo 沒有副作用，且沒有持久層就無處存放可重試所需的狀態。可靠送達留待佇列與持久層的 change。

### 逾時、事件預算與停止

| 項目 | 預設 | 備註 |
| --- | --- | --- |
| 回應 200 | 不含任何平台呼叫 | 以假平台延遲驗證回應不被事件處理拖延；實測 LINE Webhook errors 沒有 `request_timeout` |
| 活動提示呼叫 `Timeouts:Activity` | 2 秒 | 估計值；逾時只記錄，不阻擋回覆 |
| reply／push 呼叫 `Timeouts:Send` | 10 秒 | 估計值；以實測延遲調整 |
| 單一事件處理預算 `Timeouts:Event` | 30 秒 | 估計值；從 worker 開始處理起算，經由取消期限傳給 handler |
| 預算取消後的寬限 `Work:OverrunGrace` | 5 秒 | 估計值；取消後 handler 仍未返回即視為 overrun |
| 並行處理上限 `Work:MaxConcurrency` | 4 | 估計值 |
| 等待處理上限 `Work:MaxPending` | 100 | 估計值；等於有界 Channel 的容量，`TryWrite` 失敗即為超過，丟棄新事件（不登記）、記錄警告與數量，仍回 200 |
| 排隊最長等待 `Work:MaxQueueAge` | 30 秒 | 估計值；以 `EnqueuedAt` 計算，worker 取出時若已超過則記錄並丟棄，不啟動活動提示與 handler，登記保留。以預設估計值（等待 100、worker 4、每件最長 30 秒）計算，尾端最多等約 750 秒，遠超 reply 的 50 秒有效期，故需要此上限；這是依估計值算出的上界，實測後再調整 |
| 停止寬限期 `Stop:Grace` | 10 秒 | 估計值；等待執行中工作完成，期限到後取消 |
| 取消後等待 `Stop:JoinTimeout` | 5 秒 | 估計值；取消後等待工作、清理計時器與活動提示 cleanup 退出，逾時則放棄等待並記錄 |

以上皆為 `Settings` 的自訂欄位，可只改設定覆寫（見「Connector 實例清單：通用欄位與自訂欄位」）；數值為估計，實作後以實測延遲與流量確認，結果出爐前不視為定論。handler 必須在事件預算內返回——1a 的 echo handler 在其中呼叫 `SendAsync`；未來的 AI handler 必須只做排入佇列就返回。

**Running 狀態的 handler 合作契約（保證範圍）**：取消是合作式的，不能強制終止忽略取消權杖的 handler。Connector 只保證**合作式 handler** 的「事件預算與其他事件不受影響」。預算取消後再等 `Work:OverrunGrace` 仍未返回的 handler，其 worker 標記為 overrun 並記錄一次錯誤；Connector **不替換 worker**，所以同時執行的 handler 永遠不超過 `Work:MaxConcurrency`，不會累積。**當所有 worker 都處於 overrun 時**，Connector 進入 Degraded：新事件一律過載丟棄（不登記）並記錄，直到有 handler 返回才自動恢復。**Degraded 是 Running 底下的接受狀態旗標，不是可回到 Running 的第四個生命週期狀態**：自動恢復只在生命週期仍為 Running 時有效；Stopping 或 Stopped 期間 handler 返回只更新 overrun 計數與日誌，不重新開放接受、不處理等待事件、不解除 503。Degraded 之前已排入的等待事件，在 Running 恢復時依 `Work:MaxQueueAge` 處理，Stop 時依原規則丟棄。overrun 的 handler 其 lease 已撤銷（見下），它之後的送出會被拒絕。每個 worker 的狀態（idle、running、overrun）與 Degraded 的進出都寫入日誌。替代方案：到期後放棄並啟動替換 worker，另設 `Work:MaxAbandoned` 上限；1a 的 handler 是 echo 且 AI 階段規定只排入佇列，不需要這套機制，待有無法保證合作的 handler 時再做。

**lease 與送出許可**：Core 在呼叫 Host handler 前建立 work lease（Core 私有、可撤銷的共享參照物件，放在 `AsyncLocal<WorkLease?>` 中，記錄所屬的 Connector 實例物件），`finally` 還原原值；handler 結束、事件預算取消、overrun 或寬限期到時撤銷。`DeliverAsync` 依呼叫者與狀態判定，三種狀態互相獨立：

| 呼叫者 | Running | Stopping | Stopped |
| --- | --- | --- | --- |
| 帶有效 lease | 允許 | 允許（寬限期內） | `Unavailable` |
| 攜帶**已撤銷**的 lease（遲到的子工作） | `Unavailable` | `Unavailable` | `Unavailable` |
| 完全沒有 lease（例如將來的 AI 完成事件主動 push） | 允許 | `Unavailable` | `Unavailable` |

「攜帶已撤銷 lease」與「沒有 lease」以 `AsyncLocal` 是否有值判別；有值但已撤銷一律拒絕，與狀態無關。**lease 只代表自家 Connector 的在途工作，不是跨 Connector 的通行證**：lease 有效的條件包含所屬實例就是接收送出的這個實例。從接收方 B 的角度，其他實例 A 的 lease（不論有效或已撤銷）視為「沒有 B 的 lease」，依「完全沒有 lease」那一列處理（Running 允許、Stopping 與 Stopped 拒絕）。同一個 dll 的兩個實例共用同一個 `AsyncLocal` 型別，以所屬實例區分；不同載入環境的兩個實例各有自己的 `AsyncLocal`，互不可見。Host 看不到 lease。不以 `InReplyTo` 或呼叫者提供的未取消權杖當許可。lease 驗證與在途送出登記經同一個狀態閘門排序。

**`StopAsync` 契約（不 drain）**：狀態為 Running → Stopping → Stopped，接受事件與進入 Stopping 由同一個狀態閘門序列化。

- **Stopping**：關閉入口並對新 webhook 回 503；尚未啟動的等待事件逐項記錄並丟棄（登記保留）；執行中的事件在寬限期內可繼續，包含它們自己的送出（依上表）。
- **到期後**：寬限期到後撤銷所有 lease、關閉送出許可並取消所有執行中與在途送出，再等待 worker、背景清理計時器與活動提示 cleanup 退出（上限 `Stop:JoinTimeout`）才標記 Stopped；每個尚未結束的活動提示 dispose 恰好一次。Stopped 後所有送出入口（含 Host 呼叫的 `DeliverAsync`）回 `Unavailable` 且不呼叫平台。
- **Host 取消權杖**：`StopAsync` 收到的取消權杖被取消（Host 的關閉逾時到了）時視為預算已用盡：立即撤銷所有 lease、取消在途工作與送出、不再等待寬限期與 join，記錄「被 Host 逾時中斷」後返回，並照下一點隔離遲到的延續。`StopBudget` 回報 `Stop:Grace` 加 `Stop:JoinTimeout`（覆寫後隨之改變，預設 15 秒）。
- **保證分級**：正常 join 完成的停止，所有 worker、計時器與活動提示 cleanup 皆已退出。若 `Stop:JoinTimeout` 到期，Connector 記錄被放棄的工作並隔離其延續：Stopped 後不啟動新的事件處理、活動提示或訊息 API 呼叫，不重新啟動計時器；遲到的延續只能觀察結果或例外與釋放資源，不得重新登記活動或經由 gateway 送出（例如 `StartActivityAsync` 在停止後才回傳的 disposable 必須恰好釋放一次且不加入活動登記）。不遵守取消的外部操作可能仍在執行，停止不承諾終止它們，也不承諾在放棄等待後 Core 自己的遲到延續完全不執行。

跨請求不保證處理順序（並行上限大於 1）；同一請求內的事件依原順序排入。訊息順序屬於日後 Topic 路由的責任。

### 關閉預算：並行停止與預算排序

Docker Compose 的預設停止等待時間是 10 秒，之後送 SIGKILL（官方文件）；.NET 的 `HostOptions.ShutdownTimeout` 預設為 30 秒。Connector 預設停止最久約 15 秒（`Stop:Grace` 10 秒加 `Stop:JoinTimeout` 5 秒），所以必須安排預算的先後，否則正常的 `compose stop` 會在清理完成前被殺掉；若 Host 逐一等待各實例，三個實例最壞 45 秒，超過 30 秒，後停止的實例還會繼續接受與送出。

- **並行停止**：Host 關閉開始時，對所有已啟動的實例同時呼叫 `StopAsync`（`Task.WhenAll`），每個實例恰好一次，失敗各自觀察與記錄，一個實例的失敗不影響其他實例。
- **預算排序**：Connector 停止預算（`StopBudget`，預設 15 秒）< Host 關閉預算（`HostOptions.ShutdownTimeout` ＝ 所有實例 `StopBudget` 的最大值加 `Assistant:Shutdown:Margin`，預設 5 秒，合計預設 20 秒）< 容器停止等待時間（`compose.yaml` 的 `stop_grace_period: 30s`）。餘裕預留給 HTTP 伺服器等收尾。三個數值（5 秒、20 秒、30 秒）皆為估計，以 7.1 的 `compose stop` 實測確認。
- **Host 不引用 Core**：Host 只讀 `IConnector.StopBudget`，不知道 `Stop:Grace` 的存在。
- **覆寫關係**：使用者改了 `Stop:Grace` 或 `Stop:JoinTimeout`，Host 的關閉預算自動跟著；容器的 `stop_grace_period` 要手動調高並維持「最大 `StopBudget` ＋ `Assistant:Shutdown:Margin` < `stop_grace_period`」；Host 在啟動日誌印出算出的關閉預算以便比對，文件（`settings-reference.md`）說明此關係。
- **Host 的取消權杖**：Host 的關閉逾時到了，`StopAsync` 收到已取消的權杖，Connector 立即中斷（見「逾時、事件預算與停止」）。

### LINE 平台以 HttpClient 自行維護（含 push）

不使用 SDK。`LineApiClient` 只實作 reply、push、loading 三個端點，使用 `ConnectorCreationContext` 提供的 `HttpClient`，基底網址由 `ApiBaseUrl` 設定（預設 `https://api.line.me`）。`LinePlatform` 同時實作 `IMessagingPlatform` 與 `IWebhookInbound`，能力宣告為 Reply、Push、Activity（僅一對一）。

- `PushAsync` 呼叫 `POST /v2/bot/message/push`，`to` 取自 Chat key 的 Properties（userId、groupId 或 roomId）；送出前驗證該 key 的 canonical 與 Properties 一致，不一致則記錄為無法送達、不送出。
- 驗簽以 BCL 實作 HMAC-SHA256、Base64，並以 `CryptographicOperations.FixedTimeEquals` 比較，針對**原始位元組**。簽章測試向量的預期 Base64 值以獨立工具（`openssl`）預先算出並固定寫入測試，避免測試與實作共用同一個錯誤演算法。
- 傳給 Host 的 `RawMetadata` 為原事件移除所有傳輸層欄位（`replyToken`、`webhookEventId`、`deliveryContext`）後以 `JsonElement` 複製（`Clone()`）而成，不受 parser 釋放影響；Host 看不到任何傳輸層資訊。
- 事件編號缺少、`mode` 不是 active（standby）、非文字、缺 userId 的事件一律不產生 envelope。事件時間取自 LINE 的 `timestamp`（毫秒）。

### 文字長度以 UTF-16 邊界截斷

LINE 單則文字上限 5000 個 UTF-16 code unit。超過時在 5000 以內截斷，若截斷點落在 surrogate pair 中間則退回前一個合法邊界，因此結果可短於 5000。此限制屬 LINE 平台層，echo handler 不處理長度。

### 設定、secret 與健康檢查

Host 層級設定：`Assistant:ConnectorsPath`（預設 `connectors`）、`Assistant:ConfigFile`（選填，外部 JSON 設定檔路徑；明確指名卻不存在則啟動失敗）、`Assistant:Echo:Mode`（`reply` 預設，或 `push`）、`Assistant:Shutdown:Margin`（Host 關閉預算在所有實例 `StopBudget` 最大值之外的餘裕，預設 5 秒，估計值）與 `Connectors`（以實例 ID 為鍵的實例清單，見前述決策）。Connector 的自訂欄位 `Settings` 由其 factory 的描述驗證；LINE 的設定鍵為 `ChannelSecret`、`ChannelAccessToken`（皆為 secret 且必要）、`ApiBaseUrl`（預設 `https://api.line.me`）、`LoadingSeconds`（預設 5，範圍 5～60，且必須為 5 的倍數，後者由 factory 驗證）；通用層調校參數（`Dedup:*`、`Timeouts:*`、`Work:*`、`Stop:*`、`Activity:MaxDuration`）也在 `Settings` 中，鍵名與預設值如「逾時、事件預算與停止」所列。環境變數形式如 `Connectors__line__Settings__ChannelSecret`、`Connectors__line__Settings__Work__MaxConcurrency`。缺少必要鍵、未知鍵、型別或範圍錯誤時啟動失敗，訊息只含實例 ID 與鍵名。`GET /healthz` 回 200 與固定內容，不檢查外部服務、不含設定值。Compose 以 `env_file` 讀取 repo 根目錄 `data/assistant.env`（該目錄被 `.gitignore` 排除）；`assistant/.env.example` 只含占位值。

`Assistant:Echo:Mode=push` 讓 echo handler 送出沒有 `InReplyTo` 的 envelope，使人工驗收可以走真實的 push API，而不需要新增任何 production endpoint；echo handler 本身就是驗證用的 handler。

### 容器：build context 收斂到 assistant/，Docker 與 Podman 共通

- build context 為 `assistant/`，`assistant/Containerfile` 以 `-f assistant/Containerfile assistant/` 指定；`assistant/.dockerignore`（Docker 與 Podman 都會讀取）排除 `bin/`、`obj/`、`.env*`、`*.env`、`TestResults/`。repo 根目錄的 `data/` 在 context 之外，因此不會進入 builder 或任何一層。Git 的 `.gitignore` 不等於 build context 的排除機制，所以另外以 sentinel 檢查。
- multi-stage：`mcr.microsoft.com/dotnet/sdk:10.0` 建置，Host 發佈到 `/app`，Line 發佈到 `/app/connectors/line/`；`mcr.microsoft.com/dotnet/aspnet:10.0` 執行，以非 root 使用者、監聽 8080。
- 不掛載 Docker socket、host home；LINE 內建於映像，故無需 volume（避開 rootless podman 的 volume 權限與 SELinux 標籤）。不在映像內放 `HEALTHCHECK`，改在文件說明用 `curl http://localhost:8080/healthz` 驗證。
- `compose.yaml` 位於 `assistant/`，`context: .`、`dockerfile: Containerfile`、`env_file: ../data/assistant.env`、`stop_grace_period: 30s`（估計值，見「關閉預算」），只使用 Docker Compose 與 podman compose 共通的欄位。

替代方案：chiseled／distroless 映像，體積更小但除錯困難且需確認 .NET 10 對應標籤；先以標準 aspnet 映像交付。替代方案二：repo 根目錄當 context，已因無法保證排除 secret 而放棄。

### CI：GitHub Actions，docker 與 podman 各建置一次

`.github/workflows/assistant-ci.yml`，路徑過濾 `assistant/**` 與 workflow 本身。Job 1：setup-dotnet 10.x → restore → build → test。Job 2（需 Job 1 成功，以矩陣跑 `docker` 與 `podman`）：以 `assistant/Containerfile`、context `assistant/` 建置映像（不推送）。

### 測試策略（TDD）

| 層級 | 範圍 | 手法 |
| --- | --- | --- |
| 單元（Core） | 去重、reply 脈絡與路徑選擇、先回 200、事件工作與預算、overrun 與 Degraded、lease 判定、活動生命週期（含逾時後的晚到結果）、停止清理、調校參數描述的驗證 | 假 `IMessagingPlatform`／`IWebhookInbound`、注入 `TimeProvider` |
| 單元（Line） | 驗簽、解析、External Key、metadata 清理、截斷、請求格式 | 純 xUnit；固定的獨立算出簽章向量；假 `HttpMessageHandler` |
| 整合（Host） | 路由、載入器、實例清單與設定驗證、gateway 三種結果、echo 全流程 | `WebApplicationFactory`＋載入實際的 LINE dll＋假 LINE API |
| 載入 | 乾淨 publish 輸出的 dll 載入與版本檢查 | 對 `dotnet publish` 輸出資料夾載入，不依賴測試專案引用 |
| 架構 | csproj 與組件引用方向 | 解析 csproj＋reflection，反例為可編譯 fixture |
| 容器 | build context 隔離 | sentinel 字串檢查各層與最終映像 |
| 人工 | 真實 LINE 帳號 | `assistant/docs/manual-test-line.md`；離線用 `assistant/scripts/` 的假 webhook 腳本；LINE 主控台確認無 `request_timeout` |

每個任務先寫失敗的測試（確認新測試在變更前失敗），再實作。

## Implementation Contract

**Behavior**

- `POST /webhook/{instanceId}`：body 超過 1 MiB → 413（不呼叫 Connector）；`instanceId` 不存在、已停用或不是 webhook 型 → 404；其餘由 Connector 回報狀態碼：簽章缺少或錯誤 → 401、payload 不合法 → 400、驗簽通過 → 200、Connector 停止中 → 503。200 的回應不等待任何活動提示、handler 或平台 API。
- 文字訊息事件：Connector 去重後排入內部工作（成功入列才登記），worker 取出時若超過 `Work:MaxQueueAge` 則丟棄，否則依序啟動活動提示、呼叫 Host 的 handler；echo handler 以 `OutboundEnvelope`（`InReplyTo` 為該訊息，`Chat` 為來源聊天）送出 `Echo: <原文>`，LINE 平台層在 5000 UTF-16 code unit 內截斷。`Assistant:Echo:Mode=push` 時 echo 不帶 `InReplyTo`，走 push。
- 送出時：Chat 相符且 reply token 本地仍有效且未用 → reply；Chat 不符、無脈絡、本地過期或 token 已用 → push 到 envelope 的 Chat；reply 已嘗試失敗 → 記錄、不補 push。重送事件的事件時間已早於有效期時即使 token 未用也 push（保守策略，已接受的成本）。
- `DeliverAsync` 依 lease 與狀態判定（見「逾時、事件預算與停止」的表）：不合法的呼叫回 `Unavailable`，gateway 對應為 `ConnectorUnavailable` 且不呼叫平台。
- 一對一聊天：事件工作開始時先發 loading；群組與聊天室不發。Host 從不呼叫活動提示。活動呼叫逾時後才回傳的結果不登記，立即 dispose 一次。
- 同一 Connector 實例內，同一事件編號在登記保留期間內第二次出現：不呼叫 handler、不呼叫任何 LINE API，仍回 200；被容量淘汰的登記會提前結束保護。
- 非文字、缺 userId、缺事件編號、standby 事件：不產生 envelope，只記錄類型。
- 啟動時依組態實例清單載入 `Enabled` 的 Connector；缺 secret、未知設定鍵、型別或範圍錯誤、載入失敗、版本不符、實例 ID 不合法、實例頂層有未知欄位或形狀錯誤、明確指名的 `Assistant:ConfigFile` 不存在、沒有 `Type` 的殘缺條目、TTL 無效時，程序以非零狀態結束，訊息只含檔名、實例 ID、版本與鍵名；啟動時把清單摘要（不含設定值）寫入日誌。
- Running 狀態下若所有 worker 都 overrun，Connector 進入 Degraded（Running 底下的旗標），新事件被丟棄（不登記），有 handler 返回且生命週期仍為 Running 時自動恢復；Stopping 或 Stopped 期間不恢復。
- 關閉時 Host 對所有已啟動的實例並行呼叫 `StopAsync`（各一次，失敗各自記錄、互不影響），Host 關閉預算為「所有實例 `StopBudget` 的最大值加 `Assistant:Shutdown:Margin`」；每個 Connector 進入 Stopping（新請求回 503、尚未啟動的等待事件丟棄）、執行中事件在寬限期內可繼續、取消後等待退出至 `Stop:JoinTimeout`、dispose 尚未結束的活動提示；Host 的取消權杖被取消時立即中斷；Stopped 後所有送出被拒絕。
- `GET /healthz`：200，內容不含設定值。

**Interface / data shape**

- `InboundEnvelope(ConnectorType, ConnectorInstanceId, Actor, Chat, Thread?, Message, Content, OccurredAt, RawMetadata)`；`RawMetadata` 為移除傳輸層欄位後複製的 `JsonElement`。
- `OutboundEnvelope(ConnectorType, ConnectorInstanceId, Chat, InReplyTo?, Content)`；`MessageContent` 本階段只有文字。
- `ExternalKey(Kind, CanonicalValue, Properties)`，`Properties` 為獨立 `Clone()` 的 `JsonElement`；以 `CanonicalValue` 判定相等，與 Properties 屬性順序無關。
- `IConnector`、`IWebhookReceiver`、`IConnectorFactory`（含 `Settings` 描述）、`IInboundMessageHandler`、`IOutboundGateway`、`SendAcceptance`（`Accepted`／`UnknownConnector`／`ConnectorUnavailable`）、`DeliveryAcceptance`（`Accepted`／`Unavailable`）、`SettingDescriptor`（鍵名、型別、是否必要、預設值、是否 secret、最小與最大值、說明）、`ConnectorInstanceConfig`（`InstanceId`〔取自鍵〕、`Type`、`Enabled`、`Assembly`、`DisplayName`、`Settings`）：定義於 `Abstractions`。`WebhookRequest(Headers, Body 位元組)`、`WebhookResult(StatusCode)`。
- `IWebhookInbound`、`IMessagingPlatform`、`PlatformCapabilities`、`PlatformInboundEvent(EventId, EventTime, ReplyToken?, Envelope)`、`WebhookVerificationException`、`ConnectorPayloadException`、通用層調校參數的 `SettingDescriptor` 清單：定義於 `Connectors.Core`。
- 組態形式：`Connectors` 是以實例 ID 為鍵的物件，每個元素含通用欄位（`Type`、`Enabled`、`Assembly`、`DisplayName`）與 `Settings`；`Settings` 的葉節點扁平化為相對冒號鍵；時間長度為 `hh:mm:ss`；環境變數如 `Connectors__line__Type`、`Connectors__line__Enabled`、`Connectors__line__Settings__ChannelSecret`。
- LINE reply：`POST {ApiBaseUrl}/v2/bot/message/reply`，標頭 `Authorization: Bearer <token>`，JSON `{ "replyToken": "...", "messages": [ { "type": "text", "text": "..." } ] }`。
- LINE push：`POST {ApiBaseUrl}/v2/bot/message/push`，JSON `{ "to": "<userId|groupId|roomId>", "messages": [ { "type": "text", "text": "..." } ] }`。
- LINE loading：`POST {ApiBaseUrl}/v2/bot/chat/loading/start`，JSON `{ "chatId": "<userId>", "loadingSeconds": 5 }`。

**Failure modes**

- 送出失敗（含 reply token 被平台拒絕、LINE 非成功狀態）：記錄錯誤（事件 ID 或訊息 key、HTTP 狀態；不含 token、body、簽章、訊息文字），不重試；gateway 仍回 `Accepted`。
- 送出被 Connector 拒絕（Stopping 無有效 lease、Stopped、攜帶已撤銷的 lease）：不呼叫平台，gateway 回 `ConnectorUnavailable`。
- 活動提示失敗或逾時：記錄警告並繼續；逾時後晚到的結果立即釋放並觀察例外。
- 事件工作過載：丟棄新事件、不登記並記錄；入列後排隊超齡：丟棄並記錄（登記保留）；事件預算用盡：取消該事件；handler 在預算取消後再過 `Work:OverrunGrace` 仍未返回：該 worker 標記 overrun 並記錄，全部 overrun 則進入 Degraded；Connector 停止：尚未啟動者丟棄，寬限期到後取消進行中事件；`Stop:JoinTimeout` 到期：記錄放棄的工作並隔離其延續。
- 事件登記滿載：淘汰最舊登記並記錄警告；reply 脈絡隨引用它的登記一併移除。
- 日誌不得包含 channel secret、access token、reply token、`X-Line-Signature` 值、完整 webhook body 或訊息文字；標為 secret 的設定值永不輸出。

**Acceptance criteria**

- `dotnet test assistant/Assistant.sln` 全數通過，涵蓋：固定的獨立簽章向量、竄改與缺漏簽章、先回 200（假平台延遲數秒且活動逾時覆寫為 10 秒時回應不受影響）、事件工作的並行與過載（容量 2、worker 全忙、同一請求 5 個新事件且解析後請求取消：前 2 個登記並排入、後 3 個不登記，之後重送被丟棄者在有容量時被處理；worker 被喚醒時不能在登記提交前處理）、排隊超齡（假時間）與預算起點、Running 狀態下 4 個忽略取消的 handler（預算加寬限後全部 overrun 並進入 Degraded、新事件被丟棄且不登記、其中一個返回後恢復、同時執行的 handler 始終不超過 4）、去重（平行、TTL 邊界、上限與清理、`MaxEntries=2` 淘汰後重送、無效 TTL）、reply 路徑選擇（有效、本地過期、Chat 不符、並行 token 消耗、遲到事件、重送事件時間早於有效期時即使 token 未用也 push、相同訊息 key 不同事件編號無淘汰時 reply 合計最多一次、其中一筆被淘汰另一筆仍在時已用不消失、消耗→淘汰→重新登記→送出會再嘗試一次且平台拒絕時記錄不 push）、平台拒絕不補 push、活動生命週期（含活動逾時後才回傳的 disposable 立即釋放一次、不登記、例外被觀察）、lease 判定矩陣（Running 下 handler 返回或預算到期後，攜帶已撤銷 lease 的子工作 reply 與 push 都被拒絕；無 lease 的 push 在 Running 允許、Stopping 與 Stopped 拒絕；有效 lease 在 Stopping 允許）、停止契約（running＋pending＋admission 競態、取消後延遲 200ms 收尾的合作式 handler 被 join、不遵守取消的 handler 在 join 逾時後被放棄並記錄、被放棄的 handler 在 Stop 返回後才恢復並嘗試送出不得呼叫任何平台 API、停止後才回傳的 `StartActivityAsync` disposable 恰好釋放一次、Stopped 後 `DeliverAsync` 不呼叫平台）、gateway 的 `Accepted`／`UnknownConnector`／`ConnectorUnavailable` 三種結果、ExternalKey 在解析器釋放後仍可讀與 canonical／Properties 矛盾的 key 被拒絕、emoji 邊界截斷、Host 路由 404／413／401／400／200／503、實例清單（通用欄位、`Enabled=false` 不載入且路由 404、未知設定鍵與型別與範圍錯誤使啟動失敗且訊息不含值、設定來源的優先序、啟動清單摘要不含設定值）、echo 的 reply 與 push 模式、載入器錯誤與版本不符、metadata 不含傳輸層欄位、csproj 與組件引用檢查（含可編譯的禁用依賴 fixture）。
- 第五輪審閱補充驗收：兩個實例指向同一 dll 路徑時載入環境與 factory 掃描只發生一次、兩實例的去重與 reply 脈絡互不影響、停止其中一個不影響另一個、不同路徑各自獨立載入；`Enabeld=false`、`Asssembly`、`Enabled=maybe`、純量 `Settings` 皆使啟動失敗且不含值；三個實例各需 8 秒停止時總時間約 8 秒而非 24 秒、其中一個 `StopAsync` 擲例外不影響其他、Host 取消權杖提前觸發時 Connector 立即中斷、`StopBudget` 等於 `Stop:Grace` 加 `Stop:JoinTimeout`；以 podman 做 `compose stop`：延遲但合作的 cleanup 在預算內完成且程序以正常結束碼結束、不合作的 handler 使 Host 在關閉逾時內放棄並記錄且程序不是被 SIGKILL（結束碼不是 137）。
- 第四輪審閱補充驗收：以真實的 JSON 檔與環境變數 provider 驗證 `Work:MaxConcurrency=8` 生效、拼錯的葉節點被拒絕、分支與葉衝突被拒絕、鍵不分大小寫、`Timeouts:Event=60` 被拒絕而 `00:01:00` 通過；清單以實例 ID 合併（base 有 A、B，外部檔只列 A，B 仍在；外部檔改 A 不影響 B；移除只能 `Enabled=false`；只有 Settings 無 Type 的條目被拒絕；環境變數的 secret 落到正確實例）；明確指名的 `Assistant:ConfigFile` 不存在使啟動失敗而未設定則略過；`LoadingSeconds=7` 通過 Host 的範圍檢查但在 factory 失敗、65 在 Host 的範圍檢查失敗；Degraded＋Stop＋恢復的可控競態（Stopping 期間 handler 返回不重新接受、Stopped 後遲到返回不恢復，Degraded 前已排入的事件在 Running 恢復時依 QueueAge 處理、Stop 時丟棄）；lease 所屬實例（同 dll 兩實例與不同載入環境兩實例：A 的 handler 送到 Stopping 中的 B 被拒、Running 下帶 A 的 lease 送到 B 視為無 lease 而允許）；LINE 活動 disposable 為 no-op 的晚到結果測試、Core 對有可停止計時器的 fake 在晚到結果時停止計時器。
- 對乾淨的 `dotnet publish` 輸出資料夾載入 LINE dll 成功。
- 以 podman（本機）建置並啟動，`/healthz` 回 200、程序 user id 非 0；sentinel 檢查確認 `assistant/` 內與 `data/` 中的假憑證不在任何層或最終映像中。
- 假 webhook 腳本對本機容器送出簽章正確的事件得 200，簽章錯誤得 401。
- 由使用者以 Docker 與 Podman 各執行一次 `compose up`，並依 `manual-test-line.md` 以真實 LINE 帳號完成：私聊 echo＋loading、群組 echo 無 loading、切換 `Echo:Mode=push` 後收到真實 push 訊息、同事件重送不重複回覆，並在 LINE 主控台確認 Webhook errors 沒有 `request_timeout`（此項需人工驗收）。
- CI workflow 在 PR 上跑 build、test，並以 docker 與 podman 各建置映像一次。

**Scope boundaries**

- In scope：`assistant/` 全部內容（含 `assistant/docs/settings-reference.md`）、根目錄 `.gitignore` 加入 `data/`、`.github/workflows/assistant-ci.yml`、`docs/intents/assistant-design-v0.2.md` 同步。
- Out of scope：Non-Goals 所列全部項目，以及 `src/`、`ai/`、`templates/`、`package.json` 的任何修改。

## Risks / Trade-offs

- [best-effort 可能讓使用者沒有收到 echo；停止、崩潰、過載也會遺失事件] → 已明確接受；echo 無副作用，可靠送達留待佇列與持久層。
- [去重滿載淘汰使保護窗提前結束，TTL 內可能重複 echo；reply 脈絡隨登記移除後的已用保證同樣只到保留期間] → 已明確接受並寫入規格；以估計的 10000 上限使其在一般流量下不易發生，實測流量後確認。
- [`Stop:JoinTimeout` 到期後放棄的延續仍可能在 Stopped 後執行，不遵守取消的外部操作也不會被終止] → 保證分級並寫入規格：只承諾隔離（不啟動新處理、不送出、不重啟計時器、遲到結果只可觀察與釋放資源）；不承諾強制終止。
- [排隊等待可能使新鮮訊息晚回覆並消耗 push 額度] → 以 `Work:MaxQueueAge` 限制；預設值為估計，實測回應延遲與流量後調整。
- [重啟後去重與 reply 脈絡遺失；多 replica 失效] → 已知限制，文件註明只支援單一程序。
- [跨請求不保證處理順序，連續兩則訊息的回覆可能互換] → 已知限制；順序屬於日後 Topic 路由。
- [本地 reply 有效期（50 秒）是啟發式，平台可能更早拒絕 token] → 平台拒絕不補 push 並明確記錄；以 `min(收到時間, 事件時間)` 降低遲到事件的誤判；實測確認有效期。
- [逾時、並行與等待上限、排隊最長等待、停止寬限期與取消後等待、去重 TTL 與上限、reply 有效期皆為估計值] → 標註為估計，實作後以實測延遲與流量、LINE 官方文件確認，結果出爐前不當成定論。
- [Connector dll 以任意程式碼執行，可讀取所有設定] → 只從固定資料夾載入；LINE 內建於映像；開放外部掛載前需另行決定簽章或雜湊允許清單。
- [Host 與 dll 的型別身分不一致、缺少相依被測試引用遮蔽] → 固定由 Host 提供 `Abstractions` 與 `Microsoft.Extensions.Logging.Abstractions`；載入測試針對乾淨 publish 輸出；載入前檢查 `Abstractions` 主版本。
- [LINE push 計入訊息額度] → 只在 reply 脈絡不可用時才用 push，且 reply 失敗不自動補 push；實際額度規則需對照官方文件與帳號方案。
- [此開發機無 docker] → CI 以 docker 建置，人工驗收步驟列入 tasks，由使用者驗收；Compose 僅用共通欄位。
- [Windows 上 `podman compose` 轉呼叫外部 compose provider（如 `docker-compose.exe`）] → 文件說明此行為與替代方式。
- [.NET 10 映像標籤與 setup-dotnet 的 10.x 支援] → 實作時先實際拉取確認，以實際可用標籤為準並更新文件。
- [Running 狀態下忽略取消的 handler 會佔用 worker；全部 worker 都 overrun 時 Connector 進入 Degraded 而不再處理新事件] → 保證範圍限定為合作式 handler 並寫入規格；Connector 不替換 worker，因此同時執行的 handler 永不超過上限；進出 Degraded 與每個 worker 的狀態都寫入日誌；1a 的 handler 是 echo，AI 階段規定只排入佇列。
- [`Accepted` 只代表取得送出許可，不代表已送達；`ConnectorUnavailable` 與平台投遞失敗是不同語意] → 已區分並寫入規格；將來換成發送佇列時「已接受」改指排入佇列。
- [重送事件的 reply token 在平台上可能仍可用，但本地保守策略會直接 push，多一筆費用] → 已接受並寫入規格；實測後若成本不可接受，再由使用者決議是否改為平台專屬規則，且需先對照 LINE 官方文件確認確切規則。
- [Connector 停止預算、Host 關閉預算與容器停止等待時間三者必須維持先後順序，使用者覆寫 `Stop:*` 後容器逾時可能不足] → Host 以 `StopBudget` 自動計算關閉預算並於日誌印出；文件說明關係，`stop_grace_period` 需手動調高；數值為估計，以 `compose stop` 實測確認。
- [共用載入環境使同一 dll 的 static 變數只有一份] → 契約禁止把實例狀態放在 static，並以兩實例隔離測試驗證。
- [Connector 清單依實例 ID 合併，分層只能新增與覆寫、不能移除，移除要明確設 `Enabled=false`；.NET 的階層設定不能直接綁成字串字典] → 以物件取代陣列並遞迴扁平化葉節點，已實測確認陣列合併與字典綁定的缺陷。
- [LINE 的 loading 動畫無取消操作，逾時後才啟動的 loading 可能在回覆之後仍顯示數秒] → Core 只承諾 framework 自己擁有的狀態；限制寫入人工測試文件。
- [Connector 清單以組態儲存，變更需重啟；外部 JSON 檔掛載在 rootless Podman 有 volume 權限問題] → 已接受；資料庫儲存與熱載入待有持久層與管理介面再評估；文件說明掛載的權限處理，且外部檔只放非機密設定。
- [只有一個平台時，「通用」介面可能偏向 LINE 的形狀] → 以聯集加能力宣告降低風險；Telegram 或 CLI 出現時回頭檢視並調整，輪詢型基底類別不預先建立。
