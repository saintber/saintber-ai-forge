# 真實 LINE 手動驗收

前置條件：LINE 帳號與可管理的 LINE Official Account；可使用 Messaging API；Docker／Podman 或 [.NET 本機流程](local-run.md) 已啟動；可提供公開 HTTPS tunnel；了解 push 會計入帳號訊息額度。本文件需真人帳號完成，離線假 API 不代表平台驗收。

## 建立 channel 與啟動

1. 依 [LINE 官方開始指南](https://developers.line.biz/en/docs/messaging-api/getting-started/) 建立 LINE Official Account，於 Official Account Manager 啟用 Messaging API、選 provider，再至 LINE Developers Console 確認 channel。預期：可開啟該 channel 的 Basic settings 與 Messaging API 頁面；現行流程不是直接在 Developers Console 建 Messaging API channel。
2. 依 [Build a bot](https://developers.line.biz/en/docs/messaging-api/building-bot/) 取得 Channel secret、簽發 Channel access token。在 repo 根目錄 data/assistant.env 填入 assistant/.env.example 的兩個必要秘密，ApiBaseUrl=https://api.line.me、Enabled=true、Echo Mode=reply。預期：資料只在本機 env_file，不寫到映像或外部非機密 JSON，不提交、不貼日誌。
3. 依 [容器指南](install-container.md) 啟動，GET http://localhost:8080/healthz。預期：HTTP 200；日誌 line 啟用、/webhook/line、關閉預算，沒有秘密值。
4. 用已安裝的 HTTPS tunnel 工具把公開位址轉到本機 8080，例如 ngrok http 8080。預期：取得 https://<公開主機> 且該位址 /healthz 回 200；依所選 tunnel 官方安裝／授權文件設定，保持程序執行。
5. Console 的 Messaging API／Webhook settings 設 https://<公開主機>/webhook/line，若實例為 line_work 則用 /webhook/line_work；啟用 Use webhook，按 Verify。預期：Verify 成功，空 events 可回 200。依 [webhook 接收指南](https://developers.line.biz/en/docs/messaging-api/receiving-messages/) 設定，可關閉 Official Account 自動回應與歡迎訊息以便辨認本程式 echo。

## 私聊與群組

1. 掃 channel 的 QR code 加 bot 好友，打開私聊畫面，傳「hello」。預期：Echo: hello，一對一 loading 有啟動；echo 很快時動畫可能短暫難以肉眼看到，可在 LINE 開啟聊天畫面觀察。LoadingSeconds 預設5，只適用 user；詳細限制見 [LINE loading API](https://developers.line.biz/en/reference/messaging-api/#display-a-loading-animation)。
2. 在 channel 設定允許 bot 加入群組，邀 bot 進測試群組，再傳「group hello」。預期：Echo: group hello，群組無 loading；非文字／standby／缺 userId 的事件只略過，不應期待 echo。聊天室同樣無 loading。
3. 傳中文、英文與 emoji。預期：Echo: <原文>；輸出最多5000 UTF-16 code units，跨界的 surrogate pair 整組略去，不產生半個 emoji。

## 驗證真實 push 與重送

1. 將 data/assistant.env 的 Assistant__Echo__Mode=push，使用 compose up -d --force-recreate（本機流程重啟 Host）。再於私聊與群組各送新文字。預期：收到 Echo: <原文>；handler 不帶 InReplyTo，Connector 明確走真實 push API。依 [LINE 訊息計費](https://developers.line.biz/en/docs/messaging-api/pricing/) 與帳號用量畫面核對訊息額度；不要只靠回覆外觀判定 reply／push。
2. 還原 reply 並重建。預期：新事件恢復 reply 選路由；同一事件重送在記憶體登記保留期間不產生第二次 echo。離線重送的可重現方法見 [local-run.md](local-run.md)。重新啟動、容量淘汰或多副本會失去此保護。
3. 本地 reply 起點 min(收到時間, 事件時間)，50 秒是保守估計，不是平台有效期保證。[Reply API](https://developers.line.biz/en/reference/messaging-api/#send-reply-message) 才定義平台 token 規則。本地過期／無脈絡／已用會 push；舊重送即使平台 token 還可用也可能被本地策略改成 push，可能多花額度。已嘗試 reply 被 LINE 拒絕時只記錄狀態，不補 push、不重試，可能收不到 echo。
4. Core 活動呼叫逾時（預設2秒）後繼續 handler，晚到結果不留本地活動登記並 dispose 一次。然而 LINE 沒有取消 loading API，逾時後才啟動的動畫可能在回覆之後仍顯示數秒，到 LoadingSeconds 或下一則訊息才結束。

## 確認 webhook 沒有 request_timeout

前置條件：channel 已開啟 webhook errors 統計，並已傳送以上訊息。依 [LINE 錯誤統計指南](https://developers.line.biz/en/docs/messaging-api/check-webhook-error-statistics/) 在 Messaging API 頁面的 Webhook errors／統計區選剛才測試的時間範圍。預期：沒有 request_timeout；LINE 將 webhook 2 秒未回應列為此錯誤。本程式先回200再做平台工作，不應等 echo 才完成 HTTP。若 Console 尚未顯示資料，等統計更新後再確認，不能把空白直接當作通過。

## 排錯

| 現象 | 檢查與預期 |
| --- | --- |
| Verify／webhook 401 | ChannelSecret 是否與此 channel 相符；proxy／tunnel 是否修改原始 body 或丟 X-Line-Signature；修正後 Verify 成功。不得把 secret、signature 或完整 body 印入日誌 |
| 404 | URL 實例 ID 是否正確、Enabled=true、Type 為 webhook 型；修正並重建後路徑存在 |
| 400／413 | 事件是否合法、body 是否超過1 MiB；真實 LINE 正常文字應200 |
| 啟動失敗 | 按日誌鍵名檢查缺 secret、未知鍵、duration 格式、LoadingSeconds 5倍數、dll publish 相依與主版本；修正後摘要／health正常，不記錄值 |
| health正常但沒 echo | Use webhook、好友／群組邀請、事件為active文字且含userId；確認LINE access token與ApiBaseUrl，平台401／400只記錄狀態；傳新事件，不沿用已去重事件 |
| 200但沒 echo | 200只表示接收完成；過載、QueueAge、Degraded、handler／send失敗可使沒有回覆；按事件ID／message key與step查日誌，不期待自動重試 |
| push沒收到 | 確認好友／群組權限及帳號額度，ApiBaseUrl為官方；只看日誌安全狀態碼，勿貼token或API完整回覆 |
| request_timeout | 確認 tunnel可達、HTTP接收延遲與負載；不能用調長reply有效期解決HTTP逾時 |

記錄各步是否通過、時間範圍、設定鍵與安全狀態碼；另實測50秒、TTL10分鐘、並行／等待／timeout／停止預設估計是否合適，必要時依 [設定參考](settings-reference.md) 調整並重新驗收。完成後停 tunnel、以預算內 compose stop 或 Ctrl+C 停 Host；預期正常退出、沒有137。
