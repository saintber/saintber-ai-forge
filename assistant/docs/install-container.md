# 安裝容器工具並啟動 Assistant

前置條件：已取得 repository；終端機位於 repo 根目錄；可下載官方映像；8080 未占用。範例使用 PowerShell 7（Linux/macOS 可用 pwsh），執行 Linux containers。離線腳本另需 [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)。

Windows命令使用curl.exe；Linux／macOS將curl.exe改為curl，或用Invoke-WebRequest取得StatusCode。

## Docker

Windows 依 [官方安裝指南](https://docs.docker.com/desktop/setup/install/windows-install/) 準備 WSL 2／虛擬化，下載、安裝並啟動 Docker Desktop，選 Linux containers。macOS 依 [Mac 指南](https://docs.docker.com/desktop/setup/install/mac-install/)；Linux 依 [Engine 指南](https://docs.docker.com/engine/install/) 選對發行版，安裝 Engine 與 Compose plugin 並啟動服務。預期：daemon 可供目前使用者連線。

~~~powershell
docker version
docker compose version
docker run --rm hello-world
~~~

預期：version 有 Client、Server；Compose 顯示版本；hello-world 印 Hello from Docker! 並以 0 結束。只有 Client 或連線失敗時先啟動 daemon。

## Podman

依 [官方安裝頁](https://podman.io/docs/installation) 安裝 CLI／Podman Desktop。Windows/macOS 需要 [Podman machine](https://docs.podman.io/en/stable/markdown/podman-machine.1.html)；Windows 準備 WSL 2 或對應 VM backend。Linux 可直接用 rootless Podman。

~~~powershell
podman machine list
# 僅尚無 machine 時 init；已建立的 machine 不重建。
podman machine init
podman machine start
podman version
podman info
podman run --rm quay.io/podman/hello
podman compose version
~~~

預期：machine Running；version 有 Client、Server（版本可不同）；info 取得 engine 資訊；hello 印 Podman 歡迎訊息並 exit 0。Compose 可能先印 Executing external compose provider，再列 provider 版本。[podman compose](https://docs.podman.io/en/latest/markdown/podman-compose.1.html) 轉呼叫外部 docker-compose 或 podman-compose；Windows 可用 Podman Desktop 的 Compose 安裝功能取得 docker-compose.exe。provider 不在 PATH 可設定 PODMAN_COMPOSE_PROVIDER 為完整路徑；不需安裝 Docker daemon。沒有 provider 可走後面的不用 compose 流程。

## Compose（兩個 engine 共用）

前置條件：engine、Compose 檢查通過；真實 LINE 憑證依 [人工手冊](manual-test-line.md) 取得。離線測試使用下一節，不改既有 data/assistant.env。

~~~powershell
New-Item -ItemType Directory -Force data | Out-Null
if (-not (Test-Path data/assistant.env)) { Copy-Item assistant/.env.example data/assistant.env }
# 用編輯器填 ChannelSecret／ChannelAccessToken，不覆蓋已有檔案。
podman compose -p assistant -f assistant/compose.yaml up --build -d
curl.exe -i http://localhost:8080/healthz
podman compose -p assistant -f assistant/compose.yaml logs assistant
~~~

預期：建置成功且容器持續執行；healthz HTTP 200、固定 healthy 內容；日誌有 line、/webhook/line 與關閉預算，不含 Settings 值。Docker 命令把 podman 換成 docker。env_file 不存在或必要設定錯誤時先修正，不當成成功。設定變更後需重建容器，restart 不會更新 env_file 的值：

~~~powershell
podman compose -p assistant -f assistant/compose.yaml up -d --force-recreate
podman compose -p assistant -f assistant/compose.yaml stop
~~~

預期：新環境變數生效；stop 在預算內正常退出、沒有 SIGKILL（137）。清理此專案用同名 compose down。關閉與 sentinel 驗證見 [container-verification.md](container-verification.md)，預算見 [設定參考](settings-reference.md)。

## 不用 compose 的離線驗證

前置條件：Podman、.NET 10 SDK；18083、18084 可用；專用名稱尚不存在。全部為假憑證，不讀 data/assistant.env、不停其他專案。Docker 可將 podman 換成 docker；Windows Docker volume 需允許分享 scripts 目錄；SELinux 系統將專用 scripts 掛載的 :ro 改為 :ro,Z，權限說明見設定參考。

~~~powershell
podman build -t assistant-host:docs-check -f assistant/Containerfile assistant/
podman network create assistant-docs-check
$assistantScripts = (Resolve-Path assistant/scripts).Path
podman run -d --name assistant-docs-fake-line --network assistant-docs-check -p 127.0.0.1:18084:18081 --volume ($assistantScripts + ':/scripts:ro') mcr.microsoft.com/dotnet/sdk:10.0 dotnet run --file /scripts/fake-line-api.cs -- --urls http://0.0.0.0:18081
# 等待 /calls 回 200；初次 .NET file build 可能需數秒。
curl.exe -i http://localhost:18084/calls
podman run -d --name assistant-docs-host --network assistant-docs-check -p 127.0.0.1:18083:8080 --env Connectors__line__Enabled=true --env Connectors__line__Settings__ChannelSecret=test-secret --env Connectors__line__Settings__ChannelAccessToken=test-token --env Connectors__line__Settings__ApiBaseUrl=http://assistant-docs-fake-line:18081 assistant-host:docs-check
curl.exe -i http://localhost:18083/healthz
dotnet run --file assistant/scripts/fake-webhook.cs -- http://localhost:18083/webhook/line test-secret "offline test"
dotnet run --file assistant/scripts/fake-webhook.cs -- http://localhost:18083/webhook/line wrong-secret "offline test"
curl.exe http://localhost:18084/calls
podman exec assistant-docs-host id -u
podman logs assistant-docs-host
~~~

預期依序：build 成功、network 建立、API /calls 初始 []、healthz 200、腳本印 HTTP 200（exit 0）及 HTTP 401（exit 1）；不印 secret、body 或回應 body。等待 worker 完成後 /calls 有 loading/start 及 message/reply，reply 文字為 Echo: offline test；錯簽章不增加 API 呼叫。uid 非 0（.NET 映像預設 1654），啟動日誌顯示預設關閉預算 20 秒。假 API /calls 特意回傳請求內容，只用於離線假資料，不可搭配真實憑證。

~~~powershell
podman stop --time 30 assistant-docs-host assistant-docs-fake-line
podman inspect assistant-docs-host --format '{{.State.ExitCode}}'
podman rm assistant-docs-host assistant-docs-fake-line
podman network rm assistant-docs-check
podman rmi assistant-host:docs-check
~~~

預期：Host exit code 0，專用容器、network、映像移除，既有服務仍執行；不用全域 prune。30 秒是預設等待，覆寫 Stop:* 時先核對預算。

build context 僅 assistant/；data/ 在 context 外；.dockerignore 排除 bin、obj、.env*、*.env、TestResults。映像內非機密 appsettings.json，LINE publish 在 /app/connectors/line/，非 root 執行，不掛載 Docker socket 或整個 home。外部 JSON 只放非機密值、唯讀掛載；rootless 權限與 SELinux 見 [設定參考](settings-reference.md)。

2026-10-02 實測環境：Windows、Podman Client 6.0.2／Server 5.8.2、docker-compose.exe 5.4.0；數字可隨升級改變。Docker 仍需在有 daemon 的環境驗收。
