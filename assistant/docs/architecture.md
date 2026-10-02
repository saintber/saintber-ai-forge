# Assistant 1a 架構與 Connector 擴充

前置條件：閱讀 [本機操作](local-run.md)、具備 .NET10 SDK；本文件描述 LINE echo 階段，尚無 AI、資料庫、持久佇列或跨平台身分合併。

## 責任與依賴

| 專案 | 負責 | 不可做 |
| --- | --- | --- |
| Abstractions | Host 與 Connector 契約、envelope、ExternalKey、設定描述與 acceptance enums | 平台 API、I/O、引用其他 Assistant 專案；除了 Logging.Abstractions 不引入套件 |
| Connectors.Core | WebhookConnector、驗簽／解析委派、去重、reply 脈絡與路由、worker、lease、活動／停止生命週期 | LINE 格式或 API、業務 Mapping／Topic／AI |
| Connectors.Line | HMAC 原始 bytes 驗簽、文字 event 解析、canonical keys、三個 HTTP API、UTF-16 截斷、factory | 去重、worker、reply/push 政策、跨平台人員判定、AI |
| Host | 通用 HTTP 路由、設定、dll loader、生命週期、gateway、echo handler、healthz | 平台簽章、reply token、事件 ID、loading；不得引用 Core／Line |

依賴：Core → Abstractions；Line → Core、Abstractions；Host → Abstractions。Host 在啟動時載入 Connector，沒有編譯期 Line 參考。csproj 與組件反射測試共同驗證此邊界。

## HTTP 與事件工作

1. Host 的 POST /webhook/{instanceId} 限制 body ≤1 MiB，路由至 IWebhookReceiver；不存在／停用／非 webhook 實例 404，超量 413。
2. Connector 驗簽、解析；錯簽章 401、壞 payload 400、Stopping 503。
3. 同一短閘門內檢查 Running／去重，TryWrite 成功後才登記並建立 reply 脈絡；worker 取得相同閘門確認登記已提交。過載／Degraded 丟棄不登記，回 200，之後重送仍有機會處理。
4. HTTP 200 路徑不等待 activity、handler 或平台 API；worker 檢查 QueueAge，從真正開始事件工作計算預算，先 activity 再 IInboundMessageHandler。
5. Host echo 透過 IOutboundGateway 送 OutboundEnvelope；reply 模式 InReplyTo=來源 Message，push 模式為 null。

InboundEnvelope 的 Actor／Chat／Thread／Message 為 ExternalKey；Content 本階段只有文字。ExternalKey.Properties 為 Clone 的 JsonElement，canonical 比較相等，不帶 instance ID 或 event ID。RawMetadata 移除 replyToken、webhookEventId、deliveryContext 後 Clone，Host 看不到傳輸資訊。Core 的 PlatformInboundEvent 才含 EventId／EventTime／ReplyToken。

送端：IMessagingPlatform.ReplyAsync(string replyToken, MessageContent, CancellationToken)、PushAsync(ExternalKey chat, MessageContent, CancellationToken)、StartActivityAsync(ExternalKey chat, CancellationToken)。能力以 PlatformCapabilities 宣告，未支援的能力不呼叫。

Chat 相符、本地未過期且未用才 reply；有效起點 min(收到時間, 事件時間)，LINE ReplyValidity 50 秒是保守估計。其他情況 push 到 outbound Chat；reply 嘗試失敗只記錄、不補 push、不重試。共享 reply 脈絡僅在仍有事件登記引用時保留；容量淘汰會提前失去去重／已用保護。舊重送即使平台 token 還有效也可能直接 push，額外費用是已接受限制。Gateway Accepted 僅代表取得送出許可，平台失敗仍 Accepted；另有 UnknownConnector、ConnectorUnavailable。

## 有界執行與停止

固定 workers，不替換不合作的 handler。事件預算取消後 OverrunGrace 到仍未返回，worker 標記 overrun；全部 overrun 時 Running 下進入 Degraded，新事件丟棄。有 handler 返回且仍 Running 才恢復。handler 必須配合取消，長 AI 工作應排到後續工作佇列再返回。

Core 私有 work lease 不在 Host 契約。有效自身 lease 於 Running／Stopping grace 允許送出；撤銷自身 lease 永遠拒絕；沒有／外來 lease 僅 Running 允許。handler 返回、預算取消與停止取消會撤銷。

IConnector.StopBudget=Stop:Grace+Stop:JoinTimeout。Host 並行 StopAsync 所有實例；Connector Running→Stopping→Stopped，不 drain，丟棄 pending 且保留登記，grace 後取消工作與 send，最多 join 指定時間。Host 取消立即中斷；不合作的外部操作只能隔離延續，不能強制終止。Stopped 不啟動 API、handler、活動或本地 timer。活動 timeout 的晚到結果不登記、立即 dispose 一次；LINE disposable 為 no-op，所以動畫可能晚於回覆才消失。

## 載入單元與新增 Connector

前置條件：Connector 使用和 Host 相容的 Abstractions，工廠為 public 無參數、可掃描 IConnectorFactory。loader 依正規化 dll 完整路徑共用 AssemblyLoadContext 和 factory；同一路徑多實例共享載入、不共享實例狀態；不同路徑獨立。禁止用 static 保存實例去重、reply 或 worker 狀態。

1. 新建 Connector 專案，只引用 Abstractions 與需要的 Core。實作平台或其他 IConnector／IWebhookReceiver、factory 與 SettingDescriptor。Create 必須回與 context 相符的 Type／InstanceId。預期：架構與契約測試通過。
2. dotnet publish 專案到 connectors/<type>/。部署整個乾淨 publish 資料夾：主 dll、.deps.json、Core 與其自帶相依，不能只拷貝 dll。Host 共用 Abstractions、Microsoft.Extensions.Logging.Abstractions，避免型別身分分裂；Abstractions assembly 主版本須與 Host 相同（目前 1），不相符於啟動時拒絕。預期：以乾淨 publish 載入，沒有測試專案引用掩蓋缺少相依。
3. 在 Connectors 清單以新 ID 設 Type、Enabled、Assembly（相對 ConnectorsPath，不能逃出根目錄）、DisplayName、Settings。預期：未知鍵／錯誤型別／範圍／factory 特殊規則啟動失敗且只顯示定位資訊。
4. 重啟 Host。預期：啟動摘要列新實例及 webhook 路徑；GET /healthz 200；webhook 依新 Connector 收端契約處理。只有啟動時載入，無執行期熱載入／卸載／清單 HTTP 端點。

驗證命令：dotnet test assistant/Assistant.sln，預期全部通過（含 csproj／assembly 邊界與乾淨 publish loader 測試）。容器掛載及配置優先序見 [設定參考](settings-reference.md)，LINE 驗收見 [人工手冊](manual-test-line.md)。
