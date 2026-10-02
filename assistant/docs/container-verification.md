# 容器驗證

前置條件：在儲存庫根目錄使用 PowerShell、.NET 10 SDK、Node.js，以及已啟動的 Podman machine 或 Docker Desktop。以下以 Podman 示範；Docker 環境將 `podman` 改為 `docker`。請使用隔離專案名稱和假憑證。

## 建置與檢查

將 `assistant/.env.example` 複製到已忽略的 `data/assistant.env`（不要覆蓋既有設定），把 secret/token 換成 `test-secret` / `test-token`，保留 LINE 啟用。以下健康與載入檢查不送外部訊息；完整離線 API 測試依 [安裝指南](install-container.md) 的專用網路流程。8080 必須空閒。

```powershell
podman build -f assistant/Containerfile -t assistant-host:local assistant/
podman compose --project-name assistant-verification -f assistant/compose.yaml up -d --build
Invoke-WebRequest http://localhost:8080/healthz
podman exec assistant-verification-assistant-1 id -u
podman exec assistant-verification-assistant-1 ls /app/connectors/line/Saintber.Assistant.Connectors.Line.dll
podman logs assistant-verification-assistant-1
```

預期：建置成功、healthz 為 200 且內容為 healthy、uid 非 0、DLL 存在。LINE 啟用時摘要含 `line enabled True` 與 `/webhook/line`，沒有載入錯誤。預設 Host 停止預算為 20 秒，小於 compose 的 30 秒。

## 假憑證是否進入映像

在 `data/assistant.env` 加入 `ASSISTANT_VERIFY_SENTINEL=SENTINEL-7F3A-NOT-A-SECRET`，另建立 `assistant/leak.env` 含同一字串。不覆寫既有正式憑證。

```powershell
podman build -f assistant/Containerfile -t assistant-host:local assistant/
podman build --target build -f assistant/Containerfile -t assistant-host:build-verification assistant/
podman save --format docker-archive -o final-image.tar assistant-host:local
podman save --format docker-archive -o build-image.tar assistant-host:build-verification
git status --short -- data/assistant.env
git check-ignore data/assistant.env
```

Docker 的 save 使用 `docker save -o ...`，省略 `--format`。docker-archive 內的 layer.tar 未壓縮；搜尋整個 archive 涵蓋所有層及映像設定。將下列內容存為暫存目錄中的 `scan-image.mjs`：

```javascript
import { createReadStream } from 'node:fs';
const needle = Buffer.from('SENTINEL-7F3A-NOT-A-SECRET');
let tail = Buffer.alloc(0), matches = 0;
for await (const chunk of createReadStream(process.argv[2])) {
  const data = Buffer.concat([tail, chunk]);
  for (let at = data.indexOf(needle); at >= 0; at = data.indexOf(needle, at + needle.length)) matches++;
  tail = data.subarray(Math.max(0, data.length - needle.length + 1));
}
console.log({ sentinel_matches: matches });
process.exitCode = matches ? 1 : 0;
```

執行 `node <暫存目錄>/scan-image.mjs final-image.tar` 與 build-image.tar 各一次。預期都是 0 筆；git status 不列出 data 檔，check-ignore 顯示其路徑。完成後移除自己建立的 leak.env、archive 與測試 sentinel。

## 真正的 compose stop

測試 Connector 在 `assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/StopConnector`，使用真正的 Core 停止機制，不納入正式映像。cooperative 模式在取消後花兩秒收尾；uncooperative 模式永久忽略取消。fixture 不驗證 webhook 簽章，只能用於隔離測試。

```powershell
$verifyDir = Join-Path $env:TEMP ('assistant-stop-' + [guid]::NewGuid())
New-Item -ItemType Directory -Path $verifyDir
dotnet publish assistant/tests/Saintber.Assistant.Host.Tests/Fixtures/StopConnector/StopConnector.csproj -o "$verifyDir/stop-published"
New-Item -ItemType Directory -Path "$verifyDir/config"
```

預期 publish 成功且 stop-published 含 StopFixture.dll。建立 config/cooperative.json：

```json
{
  "Assistant": {"ConnectorsPath":"/verification", "Shutdown":{"Margin":"00:00:01"}},
  "Connectors": {
    "line":{"Type":"line","Enabled":false,"Assembly":"line/Saintber.Assistant.Connectors.Line.dll"},
    "stop_fixture": {
      "Type":"stop_fixture","Enabled":true,"Assembly":"stop-fixture/StopFixture.dll",
      "Settings":{"Mode":"cooperative","Work":{"MaxConcurrency":1},"Stop":{"Grace":"00:00:01","JoinTimeout":"00:00:03"},"Timeouts":{"Event":"00:05:00"}}
    }
  }
}
```

複製成 uncooperative.json，僅改 Mode。先確認同名 volume/helper 不存在，存在時改用其他名稱；再複製檔案到專用 named volume：

```powershell
podman volume create assistant-stop-verification
podman run -d --name assistant-stop-files --user 0 --volume assistant-stop-verification:/verification --entrypoint sh mcr.microsoft.com/dotnet/sdk:10.0 -c 'sleep infinity'
podman exec assistant-stop-files mkdir -p /verification/stop-fixture /verification/config
podman cp "$verifyDir/stop-published/." assistant-stop-files:/verification/stop-fixture
podman cp "$verifyDir/config/." assistant-stop-files:/verification/config
podman rm -f assistant-stop-files
```

helper 只負責複製；Host 仍使用非 root 帳號，volume 檔案須可讀。建立暫存目錄中的 stop.yaml：

```yaml
services:
  assistant:
    environment:
      Assistant__ConfigFile: /verification/config/cooperative.json
      Connectors__line__Enabled: "false"
    volumes:
      - assistant-stop-verification:/verification:ro
volumes:
  assistant-stop-verification:
    external: true
```

先停止占用 8080 的其他測試專案。執行：

```powershell
podman compose --project-name assistant-stop-test -f assistant/compose.yaml -f "$verifyDir/stop.yaml" up -d --no-build
Invoke-WebRequest http://localhost:8080/healthz
Invoke-WebRequest http://localhost:8080/webhook/stop_fixture -Method Post -Body '{}'
podman logs assistant-stop-test-assistant-1
podman compose --project-name assistant-stop-test -f assistant/compose.yaml -f "$verifyDir/stop.yaml" stop
podman inspect assistant-stop-test-assistant-1 --format '{{json .State}}'
podman logs assistant-stop-test-assistant-1
```

預期 healthz、POST 為 200；日誌有 `Host shutdown budget 00:00:05` 和 `handler entered cooperative`。stop 後有 `cleanup completed`、ExitCode 0；約 1 秒寬限加 2 秒收尾，早於 30 秒容器期限。取消會被 Core 記為事件工作失敗，不改變停止驗證結果。

將 stop.yaml 的 ConfigFile 改為 uncooperative.json，重做 up、POST、logs、stop、inspect。預期有 `handler entered uncooperative`，約 1 秒寬限加 3 秒 join 後記錄 `join abandoned`；ExitCode 0，不能是 137。記錄實際耗時與日誌；compose provider 啟動時間不屬於 Host 預算。

```powershell
podman compose --project-name assistant-stop-test -f assistant/compose.yaml -f "$verifyDir/stop.yaml" down
podman volume rm assistant-stop-verification
```

預期只移除自己建立的專案容器、網路和測試 volume。確認暫存目錄是本次建立且位於 TEMP 後才刪除，不刪既有設定或其他容器。

## 本次證據

2026-10-02：Podman client 6.0.2 / server 5.8.2；Windows compose provider 5.4.0；SDK 映像 10.0.400。建置、healthz 200、uid 1654、LINE DLL 載入、最終及 build-stage archive 搜尋 0 筆、兩種 compose stop ExitCode 0 均通過。Docker 未安裝，相同步驟留待任務 7.1。本地成功不代表遠端 Actions 或真實 LINE 已驗收。
