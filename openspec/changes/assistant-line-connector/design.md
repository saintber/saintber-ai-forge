## Context

設計稿 `docs/intents/assistant-design-v0.2.md` 定義完整的 Assistant。本 change 只實作其中的 **1a：Connector 與 LINE**，重點是把「Host ↔ Connector」的契約、通用 Webhook 型 Connector 與 LINE 平台轉接層一次定義清楚，讓之後加 Telegram、CLI、AI 執行與發送佇列時，不需要改動已完成的部分。

現況：

- 本 repo 是 Node ESM 專案（`@saintber/saifg`），沒有 .NET 程式碼、容器檔、`.dockerignore` 或 CI 設定（`.github/` 只有 agents、instructions、prompts、skills）。
- 開發機有 .NET SDK 10.0.x 與 podman 6.0.2，沒有 docker。
- `openspec/specs/` 目前為空，四個 capability 皆為新增。
- 專案既有約定：外部溝通管道的 secret 一律放 `data/` 並被 `.gitignore` 排除（見 `openspec/config.yaml`）。

本版 artifacts 由三輪討論形成：第一輪規格審閱（去重鍵進不了平台無關契約、失敗語意、逾時、build context 憑證隔離、reply token 外洩、去重記憶體上限、Host 固化 LINE 規則、架構測試反例）；使用者提出「Host 只管收發、Connector 可獨立 dll、通用 Connector 底下有平台介面」；第二輪審閱（LINE webhook 須在 2 秒內回應、去重保證與淘汰矛盾、reply 快取缺 Chat 綁定、人工 push 驗收無可達路徑、事件編號仍經由 metadata 進入 Host）。以下決策已由使用者確認。

## Goals / Non-Goals

**Goals:**

- Host 只依賴兩個介面：收（`IInboundMessageHandler`）與發（`IOutboundGateway`）；Connector 保證把訊息送入 Host，並接收 Host 的回應。
- 通用 Webhook 型 Connector 承擔共同流程，新平台只需實作 `IMessagingPlatform` 與 `IWebhookInbound`。
- Connector 先回 200 再於自身內部處理事件，滿足 LINE 的 webhook 回應期限，並讓 AI 階段不必重做此流程。
- LINE 作為第一個平台：驗簽、解析、reply、push、loading。
- Connector 為獨立 dll，啟動時依組態載入；LINE 放入映像作為預設。
- 依賴方向、容器 build context、secret 與傳輸層資訊不外洩都有可自動驗證的規則。
- Docker 與 Podman 都能以同一份 `Containerfile` 與 `compose.yaml` 建置與啟動，並附文件。

**Non-Goals:**

- AI CLI、Context／Memory、Person／Space／Topic 映射、跨平台綁定。
- Host 與 Connector 之間的發送佇列與任何持久化。Connector 內部的記憶體事件處理不是發送佇列：它不跨程序、不持久化，也不由 Host 存取。
- Telegram、CLI Connector，以及輪詢型通用 Connector 的基底類別（只預留 `IConnector` 生命週期契約）。
- 執行中不重啟的熱載入與卸載、跨容器外部載入 dll。
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
- `IOutboundGateway.SendAsync(OutboundEnvelope, CancellationToken)`：Host 提供，依 `ConnectorType` 與 `ConnectorInstanceId` 找到對應 Connector 並呼叫其 `DeliverAsync`（「發」）。
- `IConnector`：`ConnectorType`、`InstanceId`、`StartAsync(IInboundMessageHandler, ct)`、`StopAsync(ct)`、`DeliverAsync(OutboundEnvelope, ct)`。輪詢型 Connector 之後以 `StartAsync` 啟動自己的迴圈，不需要改契約。
- `IWebhookReceiver.ReceiveAsync(WebhookRequest, ct)` → `WebhookResult(StatusCode)`：webhook 型 Connector 額外實作的可選介面；Host 只提供一條通用路由，不理解簽章。
- `IConnectorFactory`：`ConnectorType` 與 `Create(ConnectorCreationContext)`；context 含該實例的組態、`ILoggerFactory`、`TimeProvider`、建立 `HttpClient` 的委派。

`InboundEnvelope` 與 `OutboundEnvelope` 不含任何平台憑證或傳輸層資訊（reply token、事件編號、重送資訊）。

### Connector 以獨立 dll 啟動時依組態載入

組態列出每個 Connector 實例：`Type`、`InstanceId`、`Assembly`（相對於 `Assistant:ConnectorsPath` 的 dll 路徑）、`Settings`（字串鍵值，含 secret）。Host 啟動時為每個 dll 建立獨立的 `AssemblyLoadContext`，以 `AssemblyDependencyResolver` 解析其相依（依 dll 旁的 `.deps.json`），並**固定由 Host 提供** `Saintber.Assistant.Abstractions` 與 `Microsoft.Extensions.Logging.Abstractions`（避免同一型別在兩個載入環境中被視為不同型別）；在 dll 內掃描 `IConnectorFactory` 實作，依 `Type` 對應。

- 只載入組態指定的檔案，路徑不得離開 `ConnectorsPath`。
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

- **有效起點**取 `min(收到時間, 平台事件時間)`，期限為平台宣告的 `ReplyValidity`（LINE 設為 50 秒，為估計值；LINE 官方說明為約 1 分鐘內有效且不保證，以實測 token 過期時間確認）。因此遲到的重送事件不會被視為新 token。這是本地的啟發式判斷，不是平台保證。
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

平台自行決定適用性：LINE 對一對一聊天發出 loading，對群組與聊天室回傳 null；未來 Telegram 的「輸入中」只維持數秒，由其平台實作以計時器週期性重發，並在 dispose 時停止。活動提示失敗只記錄警告，不影響後續處理。

### 發送介面：已接受語意，1a 直接呼叫

`IOutboundGateway.SendAsync` 只承諾「已接受」，不承諾「已送達」，回傳 `SendAcceptance`（`Accepted` 或 `UnknownConnector`）。1a 的實作在呼叫端執行緒中直接呼叫 Connector 的 `DeliverAsync`，平台層失敗只記錄、不擲回 Host。將來換成佇列實作時，Host 不需要改動。

替代方案：在程式內以 `Channel<T>` 建立記憶體佇列與背景送出。這只是同一個介面背後的另一種實作；1a 不採用。

### 失敗語意：best-effort、最多嘗試一次

驗簽通過後，webhook 一律回 200。單一事件在活動提示、handler 或送出任一步驟失敗，只記錄錯誤，不重試、不影響其他事件；因事件編號已登記，LINE 的重送會被丟棄，所以可能出現「使用者沒有收到回覆」。此外下列情況也會遺失：進程停止時尚未啟動或超過寬限期仍未完成的事件、程序崩潰、事件工作過載被丟棄（此類事件不登記，重送在有容量時可被處理）、入列後因排隊超齡被丟棄（保留登記）、去重滿載淘汰造成的重複。這是 1a 的明確取捨：echo 沒有副作用，且沒有持久層就無處存放可重試所需的狀態。可靠送達留待佇列與持久層的 change。

### 逾時、事件預算與停止

| 項目 | 預設 | 備註 |
| --- | --- | --- |
| 回應 200 | 不含任何平台呼叫 | 以假平台延遲驗證回應不被事件處理拖延；實測 LINE Webhook errors 沒有 `request_timeout` |
| 活動提示呼叫 | 2 秒 | 估計值；逾時只記錄，不阻擋回覆 |
| reply／push 呼叫 | 10 秒 | 估計值；以實測延遲調整 |
| 單一事件處理預算 | 30 秒 | 估計值；從 worker 開始處理起算，經由取消期限傳給 handler |
| 排隊最長等待 `Work:MaxQueueAge` | 30 秒 | 估計值；以 `EnqueuedAt` 計算，worker 取出時若已超過則記錄並丟棄，不啟動活動提示與 handler，登記保留。以預設估計值（等待 100、worker 4、每件最長 30 秒）計算，尾端最多等約 750 秒，遠超 reply 的 50 秒有效期，故需要此上限；這是依估計值算出的上界，實測後再調整 |
| 並行處理上限 `Work:MaxConcurrency` | 4 | 估計值 |
| 等待處理上限 `Work:MaxPending` | 100 | 估計值；等於有界 Channel 的容量，`TryWrite` 失敗即為超過，丟棄新事件（不登記）、記錄警告與數量，仍回 200 |
| 停止寬限期 `Stop:Grace` | 10 秒 | 估計值；等待執行中工作完成，期限到後取消 |
| 取消後等待 `Stop:JoinTimeout` | 5 秒 | 估計值；取消後等待工作、清理計時器與活動提示 cleanup 退出，逾時則放棄等待並記錄 |

以上皆可由 Connector 設定覆寫；數值為估計，實作後以實測延遲與流量確認，結果出爐前不視為定論。handler 必須在事件預算內返回——1a 的 echo handler 在其中呼叫 `SendAsync`；未來的 AI handler 必須只做排入佇列就返回。

`StopAsync` 契約（不 drain）：狀態為 Running → Stopping → Stopped，接受事件與進入 Stopping 由同一個狀態閘門序列化。

- **Stopping**：關閉入口並對新 webhook 回 503；尚未啟動的等待事件逐項記錄並丟棄（登記保留）；執行中的事件在寬限期內可繼續，包含它們自己的送出。
- **誰能在 Stopping 期間送出**：以 Core 私有、可撤銷的 work lease 判定，不用 `InReplyTo`（echo 的 push 模式沒有它），也不把呼叫者給的未取消權杖當許可。Core 在呼叫 Host handler 前建立 lease（共享參照物件，放在 `AsyncLocal<WorkLease?>` 中），結束後在 `finally` 還原原值；`DeliverAsync` 在 Stopping 期間只接受帶有效 lease 的呼叫（檢查所屬 Connector 實例、工作是否仍有效、寬限期與狀態）。handler 結束、事件預算取消或寬限期到時撤銷該 lease，因此子工作持有舊參照也不能送出；handler 內以正常 ExecutionContext 流動的 `Task.Run` 會繼承 lease，刻意抑制流動而沒有 lease 者在 Stopping 期間被拒絕並記錄。lease 驗證、在途送出登記與狀態切換都經同一閘門排序；取消只用來中止在途操作，不當許可。
- **到期後**：寬限期到後撤銷所有 lease、關閉送出許可並取消所有執行中與在途送出，再等待 worker、背景清理計時器與活動提示 cleanup 退出（上限 `Stop:JoinTimeout`）才標記 Stopped；每個尚未結束的活動提示 dispose 恰好一次。Stopped 後所有送出入口（含 Host 呼叫的 `DeliverAsync`）都拒絕且不呼叫平台。
- **保證分級**：取消是合作式的，不能強制終止不遵守取消權杖的程式碼。正常 join 完成的停止，所有 worker、計時器與活動提示 cleanup 皆已退出。若 `Stop:JoinTimeout` 到期，Connector 記錄被放棄的工作並隔離其延續：Stopped 後不啟動新的事件處理、活動提示或訊息 API 呼叫，不重新啟動計時器；遲到的延續只能觀察結果或例外與釋放資源，不得重新登記活動或經由 gateway 送出（例如 `StartActivityAsync` 在停止後才回傳的 disposable 必須恰好釋放一次且不加入活動登記）。不遵守取消的外部操作可能仍在執行，停止不承諾終止它們，也不承諾在放棄等待後 Core 自己的遲到延續完全不執行。

跨請求不保證處理順序（並行上限大於 1）；同一請求內的事件依原順序排入。訊息順序屬於日後 Topic 路由的責任。

### LINE 平台以 HttpClient 自行維護（含 push）

不使用 SDK。`LineApiClient` 只實作 reply、push、loading 三個端點，使用 `ConnectorCreationContext` 提供的 `HttpClient`，基底網址由 `ApiBaseUrl` 設定（預設 `https://api.line.me`）。`LinePlatform` 同時實作 `IMessagingPlatform` 與 `IWebhookInbound`，能力宣告為 Reply、Push、Activity（僅一對一）。

- `PushAsync` 呼叫 `POST /v2/bot/message/push`，`to` 取自 Chat key 的 Properties（userId、groupId 或 roomId）；送出前驗證該 key 的 canonical 與 Properties 一致，不一致則記錄為無法送達、不送出。
- 驗簽以 BCL 實作 HMAC-SHA256、Base64，並以 `CryptographicOperations.FixedTimeEquals` 比較，針對**原始位元組**。簽章測試向量的預期 Base64 值以獨立工具（`openssl`）預先算出並固定寫入測試，避免測試與實作共用同一個錯誤演算法。
- 傳給 Host 的 `RawMetadata` 為原事件移除所有傳輸層欄位（`replyToken`、`webhookEventId`、`deliveryContext`）後以 `JsonElement` 複製（`Clone()`）而成，不受 parser 釋放影響；Host 看不到任何傳輸層資訊。
- 事件編號缺少、`mode` 不是 active（standby）、非文字、缺 userId 的事件一律不產生 envelope。事件時間取自 LINE 的 `timestamp`（毫秒）。

### 文字長度以 UTF-16 邊界截斷

LINE 單則文字上限 5000 個 UTF-16 code unit。超過時在 5000 以內截斷，若截斷點落在 surrogate pair 中間則退回前一個合法邊界，因此結果可短於 5000。此限制屬 LINE 平台層，echo handler 不處理長度。

### 設定、secret 與健康檢查

Host 設定：`Assistant:ConnectorsPath`（預設 `connectors`）、`Assistant:Echo:Mode`（`reply` 預設，或 `push`）與 `Connectors` 陣列。每個元素的 `Settings` 由對應 factory 驗證；LINE 的設定鍵為 `ChannelSecret`、`ChannelAccessToken`、`ApiBaseUrl`（預設 `https://api.line.me`）、`LoadingSeconds`（預設 5，必須為 5 的倍數且介於 5～60）。通用層設定（去重、reply 脈絡、活動、逾時、`Work:MaxConcurrency`／`Work:MaxPending`／`Work:MaxQueueAge`、`Stop:Grace`／`Stop:JoinTimeout`）可在 Connector 的 `Settings` 以固定鍵名覆寫。環境變數形式如 `Connectors__0__Settings__ChannelSecret`。缺少必要鍵時啟動失敗，訊息只含鍵名。`GET /healthz` 回 200 與固定內容，不檢查外部服務、不含設定值。Compose 以 `env_file` 讀取 repo 根目錄 `data/assistant.env`（該目錄被 `.gitignore` 排除）；`assistant/.env.example` 只含占位值。

`Assistant:Echo:Mode=push` 讓 echo handler 送出沒有 `InReplyTo` 的 envelope，使人工驗收可以走真實的 push API，而不需要新增任何 production endpoint；echo handler 本身就是驗證用的 handler。

### 容器：build context 收斂到 assistant/，Docker 與 Podman 共通

- build context 為 `assistant/`，`assistant/Containerfile` 以 `-f assistant/Containerfile assistant/` 指定；`assistant/.dockerignore`（Docker 與 Podman 都會讀取）排除 `bin/`、`obj/`、`.env*`、`*.env`、`TestResults/`。repo 根目錄的 `data/` 在 context 之外，因此不會進入 builder 或任何一層。Git 的 `.gitignore` 不等於 build context 的排除機制，所以另外以 sentinel 檢查。
- multi-stage：`mcr.microsoft.com/dotnet/sdk:10.0` 建置，Host 發佈到 `/app`，Line 發佈到 `/app/connectors/line/`；`mcr.microsoft.com/dotnet/aspnet:10.0` 執行，以非 root 使用者、監聽 8080。
- 不掛載 Docker socket、host home；LINE 內建於映像，故無需 volume（避開 rootless podman 的 volume 權限與 SELinux 標籤）。不在映像內放 `HEALTHCHECK`，改在文件說明用 `curl http://localhost:8080/healthz` 驗證。
- `compose.yaml` 位於 `assistant/`，`context: .`、`dockerfile: Containerfile`、`env_file: ../data/assistant.env`，只使用 Docker Compose 與 podman compose 共通的欄位。

替代方案：chiseled／distroless 映像，體積更小但除錯困難且需確認 .NET 10 對應標籤；先以標準 aspnet 映像交付。替代方案二：repo 根目錄當 context，已因無法保證排除 secret 而放棄。

### CI：GitHub Actions，docker 與 podman 各建置一次

`.github/workflows/assistant-ci.yml`，路徑過濾 `assistant/**` 與 workflow 本身。Job 1：setup-dotnet 10.x → restore → build → test。Job 2（需 Job 1 成功，以矩陣跑 `docker` 與 `podman`）：以 `assistant/Containerfile`、context `assistant/` 建置映像（不推送）。

### 測試策略（TDD）

| 層級 | 範圍 | 手法 |
| --- | --- | --- |
| 單元（Core） | 去重、reply 脈絡與路徑選擇、先回 200、事件工作與預算、活動生命週期、停止清理 | 假 `IMessagingPlatform`／`IWebhookInbound`、注入 `TimeProvider` |
| 單元（Line） | 驗簽、解析、External Key、metadata 清理、截斷、請求格式 | 純 xUnit；固定的獨立算出簽章向量；假 `HttpMessageHandler` |
| 整合（Host） | 路由、載入器、echo 全流程 | `WebApplicationFactory`＋載入實際的 LINE dll＋假 LINE API |
| 載入 | 乾淨 publish 輸出的 dll 載入與版本檢查 | 對 `dotnet publish` 輸出資料夾載入，不依賴測試專案引用 |
| 架構 | csproj 與組件引用方向 | 解析 csproj＋reflection，反例為可編譯 fixture |
| 容器 | build context 隔離 | sentinel 字串檢查各層與最終映像 |
| 人工 | 真實 LINE 帳號 | `assistant/docs/manual-test-line.md`；離線用 `assistant/scripts/` 的假 webhook 腳本；LINE 主控台確認無 `request_timeout` |

每個任務先寫失敗的測試（確認新測試在變更前失敗），再實作。

## Implementation Contract

**Behavior**

- `POST /webhook/{instanceId}`：body 超過 1 MiB → 413（不呼叫 Connector）；`instanceId` 不存在或不是 webhook 型 → 404；其餘由 Connector 回報狀態碼：簽章缺少或錯誤 → 401、payload 不合法 → 400、驗簽通過 → 200、Connector 停止中 → 503。200 的回應不等待任何活動提示、handler 或平台 API。
- 文字訊息事件：Connector 去重後排入內部工作（成功入列才登記），worker 取出時若超過 `Work:MaxQueueAge` 則丟棄，否則依序啟動活動提示、呼叫 Host 的 handler；echo handler 以 `OutboundEnvelope`（`InReplyTo` 為該訊息，`Chat` 為來源聊天）送出 `Echo: <原文>`，LINE 平台層在 5000 UTF-16 code unit 內截斷。`Assistant:Echo:Mode=push` 時 echo 不帶 `InReplyTo`，走 push。
- 送出時：Chat 相符且 reply token 本地仍有效且未用 → reply；Chat 不符、無脈絡、本地過期或 token 已用 → push 到 envelope 的 Chat；reply 已嘗試失敗 → 記錄、不補 push。
- 一對一聊天：事件工作開始時先發 loading；群組與聊天室不發。Host 從不呼叫活動提示。
- 同一 Connector 實例內，同一事件編號在登記保留期間內第二次出現：不呼叫 handler、不呼叫任何 LINE API，仍回 200；被容量淘汰的登記會提前結束保護。
- 非文字、缺 userId、缺事件編號、standby 事件：不產生 envelope，只記錄類型。
- 啟動時依組態載入 Connector；缺 secret、載入失敗、版本不符、實例 ID 重複、TTL 無效時，程序以非零狀態結束，訊息只含檔名、實例 ID、版本與鍵名。
- 關閉時停止 Connector：進入 Stopping（新請求回 503、尚未啟動的等待事件丟棄）、執行中事件在寬限期內可繼續、取消後等待退出至 `Stop:JoinTimeout`、dispose 尚未結束的活動提示；Stopped 後所有送出被拒絕。
- `GET /healthz`：200，內容不含設定值。

**Interface / data shape**

- `InboundEnvelope(ConnectorType, ConnectorInstanceId, Actor, Chat, Thread?, Message, Content, OccurredAt, RawMetadata)`；`RawMetadata` 為移除傳輸層欄位後複製的 `JsonElement`。
- `OutboundEnvelope(ConnectorType, ConnectorInstanceId, Chat, InReplyTo?, Content)`；`MessageContent` 本階段只有文字。
- `ExternalKey(Kind, CanonicalValue, Properties)`，`Properties` 為獨立 `Clone()` 的 `JsonElement`；以 `CanonicalValue` 判定相等，與 Properties 屬性順序無關。
- `IConnector`、`IWebhookReceiver`、`IConnectorFactory`、`IInboundMessageHandler`、`IOutboundGateway`、`SendAcceptance`：定義於 `Abstractions`。`WebhookRequest(Headers, Body 位元組)`、`WebhookResult(StatusCode)`。
- `IWebhookInbound`、`IMessagingPlatform`、`PlatformCapabilities`、`PlatformInboundEvent(EventId, EventTime, ReplyToken?, Envelope)`、`WebhookVerificationException`、`ConnectorPayloadException`：定義於 `Connectors.Core`。
- LINE reply：`POST {ApiBaseUrl}/v2/bot/message/reply`，標頭 `Authorization: Bearer <token>`，JSON `{ "replyToken": "...", "messages": [ { "type": "text", "text": "..." } ] }`。
- LINE push：`POST {ApiBaseUrl}/v2/bot/message/push`，JSON `{ "to": "<userId|groupId|roomId>", "messages": [ { "type": "text", "text": "..." } ] }`。
- LINE loading：`POST {ApiBaseUrl}/v2/bot/chat/loading/start`，JSON `{ "chatId": "<userId>", "loadingSeconds": 5 }`。

**Failure modes**

- 送出失敗（含 reply token 被平台拒絕、LINE 非成功狀態）：記錄錯誤（事件 ID 或訊息 key、HTTP 狀態；不含 token、body、簽章、訊息文字），不重試。
- 活動提示失敗或逾時：記錄警告並繼續。
- 事件工作過載：丟棄新事件、不登記並記錄；入列後排隊超齡：丟棄並記錄（登記保留）；事件預算用盡：取消該事件；Connector 停止：尚未啟動者丟棄，寬限期到後取消進行中事件；`Stop:JoinTimeout` 到期：記錄放棄的工作並隔離其延續。
- 事件登記滿載：淘汰最舊登記並記錄警告；reply 脈絡隨引用它的登記一併移除。
- 日誌不得包含 channel secret、access token、reply token、`X-Line-Signature` 值、完整 webhook body 或訊息文字。

**Acceptance criteria**

- `dotnet test assistant/Assistant.sln` 全數通過，涵蓋：固定的獨立簽章向量、竄改與缺漏簽章、先回 200（假平台延遲數秒時回應不受影響）、事件工作的並行與過載（容量 2、worker 全忙、同一請求 5 個新事件且解析後請求取消：前 2 個登記並排入、後 3 個不登記，之後重送被丟棄者在有容量時被處理；worker 被喚醒時不能在登記提交前處理）、排隊超齡（假時間）與預算起點、去重（平行、TTL 邊界、上限與清理、`MaxEntries=2` 淘汰後重送、無效 TTL）、reply 路徑選擇（有效、本地過期、Chat 不符、並行 token 消耗、重新登記不重設已用、遲到事件、相同訊息 key 不同事件編號無淘汰時 reply 合計最多一次、其中一筆被淘汰另一筆仍在時已用不消失、消耗→淘汰→重新登記→送出會再嘗試一次且平台拒絕時記錄不 push）、平台拒絕不補 push、活動生命週期、停止契約（running＋pending＋admission 競態、取消後延遲 200ms 收尾的合作式 handler 被 join、不遵守取消的 handler 在 join 逾時後被放棄並記錄、被放棄的 handler 在 Stop 返回後才恢復並嘗試送出不得呼叫任何平台 API、`StartActivityAsync` 在停止後才回傳的 disposable 恰好釋放一次、Stopping 期間 Task.Run 子工作持有 lease 可送出而撤銷後不可、Stopped 後 `DeliverAsync` 不呼叫平台）、ExternalKey 在解析器釋放後仍可讀與 canonical／Properties 矛盾的 key 被拒絕、emoji 邊界截斷、Host 路由 404／413／401／400／200／503、echo 的 reply 與 push 模式、載入器錯誤與版本不符、metadata 不含傳輸層欄位、csproj 與組件引用檢查（含可編譯的禁用依賴 fixture）。
- 對乾淨的 `dotnet publish` 輸出資料夾載入 LINE dll 成功。
- 以 podman（本機）建置並啟動，`/healthz` 回 200、程序 user id 非 0；sentinel 檢查確認 `assistant/` 內與 `data/` 中的假憑證不在任何層或最終映像中。
- 假 webhook 腳本對本機容器送出簽章正確的事件得 200，簽章錯誤得 401。
- 由使用者以 Docker 與 Podman 各執行一次 `compose up`，並依 `manual-test-line.md` 以真實 LINE 帳號完成：私聊 echo＋loading、群組 echo 無 loading、切換 `Echo:Mode=push` 後收到真實 push 訊息、同事件重送不重複回覆，並在 LINE 主控台確認 Webhook errors 沒有 `request_timeout`（此項需人工驗收）。
- CI workflow 在 PR 上跑 build、test，並以 docker 與 podman 各建置映像一次。

**Scope boundaries**

- In scope：`assistant/` 全部內容、根目錄 `.gitignore` 加入 `data/`、`.github/workflows/assistant-ci.yml`、`docs/intents/assistant-design-v0.2.md` 同步。
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
- [只有一個平台時，「通用」介面可能偏向 LINE 的形狀] → 以聯集加能力宣告降低風險；Telegram 或 CLI 出現時回頭檢視並調整，輪詢型基底類別不預先建立。
