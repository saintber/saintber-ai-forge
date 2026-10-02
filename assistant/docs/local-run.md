# 本機執行與離線測試

前置條件：repo 根目錄、.NET 10 SDK、PowerShell 7；8080、18081 未占用。dotnet --version 預期 10.0.x；SDK 遵守 assistant/global.json。此流程不自動讀 data/assistant.env，環境變數由目前 shell 提供。

## 建置與載入資料夾

~~~powershell
dotnet restore assistant/Assistant.sln
dotnet test assistant/Assistant.sln
$assistantRun = Join-Path ([System.IO.Path]::GetTempPath()) ('assistant-local-run-' + [Guid]::NewGuid().ToString('N'))
dotnet publish assistant/src/Saintber.Assistant.Host/Saintber.Assistant.Host.csproj -c Release -o $assistantRun
dotnet publish assistant/src/Saintber.Assistant.Connectors.Line/Saintber.Assistant.Connectors.Line.csproj -c Release -o (Join-Path $assistantRun 'connectors/line')
~~~

預期：每個命令 exit 0；Host 有 appsettings.json；connectors/line 有 LINE dll、.deps.json 與相依，不能只複製單一 dll。

## 全離線 echo

終端機 A：

~~~powershell
dotnet run --file assistant/scripts/fake-line-api.cs -- --urls http://127.0.0.1:18081
~~~

預期：Offline fake LINE API started，GET http://localhost:18081/calls 回 []。只記錄假請求，不發送真實訊息，保持綁定本機。

終端機 B（沿用 assistantRun）：

~~~powershell
$env:Connectors__line__Enabled = 'true'
$env:Connectors__line__Settings__ChannelSecret = 'test-secret'
$env:Connectors__line__Settings__ChannelAccessToken = 'test-token'
$env:Connectors__line__Settings__ApiBaseUrl = 'http://127.0.0.1:18081'
$env:Assistant__Echo__Mode = 'reply'
Push-Location $assistantRun
dotnet Saintber.Assistant.Host.dll
Pop-Location
~~~

預期：監聽 8080、line 摘要有 /webhook/line，healthz 200。Content root 是目前工作目錄，需在 publish 資料夾啟動；Ctrl+C 後才執行 Pop-Location。

終端機 C（repo 根目錄）：

~~~powershell
curl.exe -i http://localhost:8080/healthz
dotnet run --file assistant/scripts/fake-webhook.cs -- http://localhost:8080/webhook/line test-secret "你好 offline"
dotnet run --file assistant/scripts/fake-webhook.cs -- http://localhost:8080/webhook/line wrong-secret "你好 offline"
curl.exe http://localhost:18081/calls
~~~

預期：health 200，正確 secret HTTP 200／exit 0，錯誤 secret HTTP 401／exit 1。worker 完成後 calls 有 loading/start 及 message/reply，文字 Echo: 你好 offline；錯簽章無 API 呼叫。200 不保證外部 API 已完成。腳本只印狀態碼，不印 secret、簽章、body 或 API 回應。

驗證 push：終端機 B Ctrl+C，設 $env:Assistant__Echo__Mode='push' 再重啟，送新的 webhook；預期 calls 新增 message/push，to=Uoffline，文字 Echo: <原文>。

## 原始 payload 與重送

預設每次產生新的 event ID、message ID、reply token 與目前 timestamp。可建立只含假資料的 fake-event.json：文字事件含 type=message、mode=active、webhookEventId、timestamp 毫秒、replyToken、source.userId、message.id/type/text，重送相同檔案：

~~~powershell
dotnet run --file assistant/scripts/fake-webhook.cs -- http://localhost:8080/webhook/line test-secret --payload ./fake-event.json
~~~

預期：每次 HTTP 200，登記保留期間內只產生一組 API 呼叫。--payload 讀取原始 bytes 並簽章，不改寫中英文或跳脫；壞 JSON 回 400；過舊 timestamp 可使 reply 本地過期而走 push。

格式：URL、secret、選填 text（預設 offline test），或 --payload 檔案。缺／錯參數 exit 2、顯示 Usage；檔案／網路錯誤 exit 1、只顯示 Webhook request failed.。採 [dotnet run --file](https://learn.microsoft.com/dotnet/core/sdk/file-based-apps) 並用 -- 分開參數，避免目前目錄有 csproj 時跑錯專案。

## 工具檢查與收尾

~~~powershell
dotnet run --file assistant/scripts/fake-webhook-tests.cs -- assistant/scripts/fake-webhook.cs
dotnet run --file assistant/scripts/fake-line-api-tests.cs -- assistant/scripts/fake-line-api.cs
~~~

預期：Tool tests: 13 passed, 0 failed；Fake API tests 的 endpoints、records、invalid JSON、404 通過。純 .NET10、無 packages；使用隨機 localhost port 與固定獨立 HMAC 向量，網路故障案例可能等待工具的 10 秒 timeout。

終端機 A、B Ctrl+C，預期正常退出與 port 釋放；關閉專用 shell 移除暫存環境變數。設定變更需重啟，沒有熱載入；單一程序記憶體去重在重啟／多副本時失效。真實 LINE 見 [人工手冊](manual-test-line.md)，完整設定見 [設定參考](settings-reference.md)。
