# 設定參考

前置條件：了解 [容器啟動](install-container.md) 或 [本機操作](local-run.md)；修改前確認所選 Connector factory 的描述。設定在啟動時讀取、變更需重啟；env_file 變更用 compose up -d --force-recreate，不用 restart。所有預設調校值都需按實際延遲／流量驗證。

## 來源與形狀

優先序由低到高：內容根目錄 appsettings.json → 選填 Assistant:ConfigFile 外部 JSON → 環境變數。Host 不讀任意 appsettings.Environment.json、不提供命令列覆寫或執行期熱載入。Assistant:ConfigFile 路徑由 base＋環境變數選定，相對路徑以 Host content root 解析；未設定才略過，明確指名但不存在／無法讀取／壞 JSON 時非零結束，只報鍵名。

Connectors 是以實例 ID 為鍵的物件，不能用陣列。分層按 ID 新增與覆寫，外部檔只列 A 不會刪掉 base 的 B；移除要 Enabled=false。每個實例必須含 Type 與 Assembly，不能只提供無 Type 的殘缺 Settings。鍵名不分大小寫，Settings 的巢狀葉節點扁平化成相對冒號鍵；未知鍵、錯型別／範圍、分支與葉衝突、未知頂層欄位都使啟動失敗，錯誤不列設定值。

實例 ID 不分大小寫，啟動時轉為小寫；正規化後為1～32字元、a～z開頭，後續只允許小寫字母、數字、單一底線，不得連續底線（LINE_A接受並轉為line_a；Line-Main、_line、line__work不合法）。Type／InstanceId 必須與 factory 結果一致；停用仍需完整通用欄位，但不載入 dll／驗必要平台憑證，webhook 回404。

## 通用欄位與 Host 設定

| 鍵 | 型別 | 預設／必要 | 範圍／用途 | Secret | 估計值 |
| --- | --- | --- | --- | --- | --- |
| Connectors:<id>:Type | 字串 | 必要 | factory 的 ConnectorType，LINE為line | 否 | 否 |
| Connectors:<id>:Enabled | 布林 | true；內建line範例false | true／false；false不載入、不建立路由 | 否 | 否 |
| Connectors:<id>:Assembly | 字串 | 必要 | 相對 ConnectorsPath 的 dll 路徑，不能逃出根目錄；LINE為line/Saintber.Assistant.Connectors.Line.dll | 否 | 否 |
| Connectors:<id>:DisplayName | 字串 | 選填，無 | 啟動摘要顯示名稱，勿放秘密 | 否 | 否 |
| Connectors:<id>:Settings | 物件 | 空物件 | factory 描述的鍵，不能是純量／陣列 | 依葉鍵 | 否 |
| Assistant:ConnectorsPath | 字串 | connectors | 載入根目錄，相對 content root | 否 | 否 |
| Assistant:ConfigFile | 字串 | 選填，無 | 外部非機密 JSON；明確指名不存在則fail | 否 | 否 |
| Assistant:Echo:Mode | 字串 | reply | reply／push；push不帶InReplyTo | 否 | 否 |
| Assistant:Shutdown:Margin | 時間長度 | 00:00:05 | 正值；Host超過最大StopBudget的餘裕 | 否 | 是 |
| ASPNETCORE_URLS | 字串（環境變數） | http://0.0.0.0:8080 | Host的監聽位址；容器映射到8080 | 否 | 否 |

## LINE Settings（4鍵）

下表鍵皆位於 Connectors:<id>:Settings，描述由 LineConnectorFactory 提供。

| 鍵 | 型別 | 預設 | 範圍／用途 | Secret | 估計值 |
| --- | --- | --- | --- | --- | --- |
| ChannelSecret | 字串 | 無，必要 | 非空；原始webhook bytes的HMAC secret | 是 | 否 |
| ChannelAccessToken | 字串 | 無，必要 | 非空；三個API的Bearer token | 是 | 否 |
| ApiBaseUrl | 字串 | https://api.line.me | 絕對HTTP／HTTPS URI；路徑前綴保留；離線才改假API | 否 | 否 |
| LoadingSeconds | 整數 | 5 | 5～60，且為5的倍數；7於factory拒絕，65於Host範圍檢查拒絕 | 否 | 是 |

LINE ReplyValidity=50秒是平台能力的本地估計，不是可覆寫 Settings 鍵，不是 LINE token 保證；本地過期改push，reply平台拒絕不補push。文字最多5000 UTF-16單位且不割surrogate pair。Loading僅user可用，無取消API。

## 共用 Core Settings（12鍵）

這些鍵也位於 Connectors:<id>:Settings；LINE factory 合併 FrameworkSettings 的描述。時間與容量預設為估計值。

| 鍵 | 型別 | 預設 | 範圍／用途 | Secret | 估計值 |
| --- | --- | --- | --- | --- | --- |
| Dedup:Ttl | 時間長度 | 00:10:00 | 正值；登記保留TTL | 否 | 是 |
| Dedup:MaxEntries | 整數 | 10000 | ≥1；超量淘汰最舊，提前失去去重與reply保護 | 否 | 是 |
| Timeouts:Activity | 時間長度 | 00:00:02 | 正值；活動呼叫逾時後繼續handler | 否 | 是 |
| Timeouts:Send | 時間長度 | 00:00:10 | 正值；reply／push單次呼叫 | 否 | 是 |
| Timeouts:Event | 時間長度 | 00:00:30 | 正值；worker真正開始處理才起算，含activity＋handler＋送出 | 否 | 是 |
| Work:MaxConcurrency | 整數 | 4 | ≥1；固定worker數，不替換overrun worker | 否 | 是 |
| Work:MaxPending | 整數 | 100 | ≥1；尚未開始處理的佇列上限 | 否 | 是 |
| Work:MaxQueueAge | 時間長度 | 00:00:30 | 正值；排隊超齡丟棄但保留登記 | 否 | 是 |
| Work:OverrunGrace | 時間長度 | 00:00:05 | 正值；事件預算取消後仍未返回即標overrun | 否 | 是 |
| Stop:Grace | 時間長度 | 00:00:10 | 正值；Stopping的執行中事件寬限 | 否 | 是 |
| Stop:JoinTimeout | 時間長度 | 00:00:05 | 正值；取消後等待worker、timer、activity cleanup、inflight | 否 | 是 |
| Activity:MaxDuration | 時間長度 | 00:02:00 | 正值；本地活動登記保存上限 | 否 | 是 |

整數用十進位，不能有小數。時間長度使用帶冒號的 [d.]hh:mm:ss[.fffffff]，分鐘／秒00～59，正值，例00:01:00、00:00:00.200；單獨60、0、負值、ISO8601 PT1M皆拒絕。不要用極大時間值模擬無限等待。

## JSON 與環境變數的等價範例

以下是非機密外部 JSON，可合併內建line；修改成其他ID時也需完整Type／Assembly：

~~~json
{
  "Assistant": { "Echo": { "Mode": "push" } },
  "Connectors": {
    "line": {
      "Type": "line", "Enabled": true,
      "Assembly": "line/Saintber.Assistant.Connectors.Line.dll",
      "Settings": {
        "LoadingSeconds": 10,
        "Work": { "MaxConcurrency": 8 },
        "Timeouts": { "Event": "00:01:00" }
      }
    }
  }
}
~~~

等價的 env_file 葉鍵（雙底線取代冒號）：

~~~dotenv
Assistant__Echo__Mode=push
Connectors__line__Type=line
Connectors__line__Enabled=true
Connectors__line__Assembly=line/Saintber.Assistant.Connectors.Line.dll
Connectors__line__Settings__LoadingSeconds=10
Connectors__line__Settings__Work__MaxConcurrency=8
Connectors__line__Settings__Timeouts__Event=00:01:00
# 必要secret只放本機data/assistant.env（下列為占位值）：
Connectors__line__Settings__ChannelSecret=replace-with-channel-secret
Connectors__line__Settings__ChannelAccessToken=replace-with-channel-access-token
~~~

預期：JSON／env兩種方式產生相同非機密設定；env相同鍵勝過JSON；Work:MaxConcurrency=8、Event=1分鐘。真實秘密填到data/assistant.env，不提交、不在命令輸出展示。多實例以不同ID隔離Settings，不用陣列索引。

## 外部檔案與 rootless volume

前置條件：專用非機密JSON，例如 repo根 data/assistant.config.json，容器非root使用者需可讀此檔與父目錄。只掛載必要檔案或專用設定目錄，唯讀；不掛整個home或Docker socket。

~~~powershell
$assistantConfig = (Resolve-Path data/assistant.config.json).Path
# 附加至自己的 podman run（已有image、ports與env_file選項）：
# --volume ($assistantConfig + ':/config/assistant.json:ro') --env Assistant__ConfigFile=/config/assistant.json
~~~

Compose 可加 readonly volume source=專用檔案、target=/config/assistant.json，environment 的 Assistant__ConfigFile=/config/assistant.json。預期：容器讀到非機密覆寫且env secrets優先。更改內容仍需重啟。

Rootless Podman 的UID映射不等於host UID；檔案須可由容器UID1654讀取、父目錄可進入。Linux 可讓專用非機密檔0644／父目錄0755，避免為解決權限而放寬secret檔；SELinux環境依 [Podman volume文件](https://docs.podman.io/en/latest/markdown/podman-run.1.html) 對專用目錄選ro,Z（私有label）或ro,z（共用label），Windows／macOS則確認machine可存取該路徑。不要任意改整個home擁有者。讀不到／缺少指定檔案應非零退出、只報Assistant:ConfigFile；未指定則正常略過。

## 關閉預算

每實例 StopBudget=Stop:Grace+Stop:JoinTimeout；Host並行停止實例，關閉預算=max(各StopBudget)+Assistant:Shutdown:Margin。必須保持：

~~~text
max(Stop:Grace + Stop:JoinTimeout) + Assistant:Shutdown:Margin < compose stop_grace_period
預設：max(10s + 5s) + 5s = 20s < 30s
~~~

不是把所有實例時間相加，也不是單獨取不同實例的Grace／Join最大再相加。更改Stop:*時需手動調高compose.yaml的stop_grace_period；不用compose則相應提高podman stop --time／docker stop --time。Host啟動日誌印計算後的Shutdown budget，可與容器等待比對；修改後預期預算仍嚴格小於容器等待，stop正常退出而非137。可重現停止指南見 [container-verification.md](container-verification.md)。

驗證前先讀factory描述與本表；有效設定啟動有安全摘要與health200，拼錯鍵、LoadingSeconds=7／65、duration=60、缺secret或缺指定外部檔應啟動失敗。單一程序記憶體去重、容量淘汰、平台best-effort與晚到loading限制見 [架構](architecture.md) 和 [人工手冊](manual-test-line.md)。
