## 1. 套件與指令更名

- [x] 1.1 將 package.json 的 `name` 改為 `@saintber/saifg`、`bin` 欄位改為 `{ "saifg": "./bin/saifg.js" }`，並更新 `description`/`keywords`；驗證方式：`node -e "console.log(require('./package.json').bin)"` 印出 `{ saifg: './bin/saifg.js' }`（對應設計「套件與指令更名為 @saintber/saifg / saifg」）
- [x] 1.2 新增 `bin/saifg.js`（沿用現行 `bin/copilot-library.js` 的入口邏輯，改為呼叫新的 `run()` 實作），移除 `bin/copilot-library.js`；驗證方式：`node bin/saifg.js` 可執行且印出 usage 訊息，`ls bin/copilot-library.js` 回報檔案不存在
- [x] 1.3 更新 `README.md` 所有指令範例為新的 `saifg` 子命令語法，移除舊 `--module` flag 範例；驗證方式：`grep -c "saintber-copilot" README.md` 回傳 0

## 2. 命名空間解析器（asset-namespace）

- [x] 2.1 新增 `src/namespace.js`，實作「命名空間簡化為單一 scope 維度」與「selector 解析邏輯重構」設計中的 `parseAssetFilename(filename)`，依「filename-based scope and module parsing」需求解析出 `{ scope, module, name, type }`，共用資產（無 scope 段）回傳 `scope: null`；驗證方式：新增 `src/namespace.test.js` 涵蓋 `org.kb.company-policy.skill.md` → `{scope:'org',module:'kb',name:'company-policy',type:'skill'}` 與 `kb.faq.skill.md` → `{scope:null,module:'kb',...}` 兩組斷言，`node --test src/namespace.test.js` 通過
- [x] 2.2 在 `parseAssetFilename` 中對「non-standard asset filenames bypass scope parsing」情境提供 fallback：無法比對已知 `type` 後綴時，沿用現行 `getModuleSelectorFromFile` 的邏輯回傳選擇器且不設定 `scope`/`type`；驗證方式：`src/namespace.test.js` 新增針對 `di-ioc-inventory-script.template.ps1` 的斷言，解析結果 module 為 `di-ioc-inventory-script` 且 `type` 為 `undefined`
- [x] 2.3 實作 `matchesSelector(parsed, selectorFilter)`，支援「selector filter supports scope and module combinations」的三種語法（`<module>`、`<scope>.<module>`、`<scope>`）；驗證方式：`src/namespace.test.js` 新增三組斷言分別驗證 bare module、scope-qualified module、bare scope 三種過濾語法的比對結果
- [x] 2.4 明確不在 `parseAssetFilename` 中特殊處理 `auto` 區段，落實「no dedicated namespace dimension for assistant-generated content」；驗證方式：`src/namespace.test.js` 新增斷言確認 `usr.auto.code.snippet.skill.md` 被解析為 `scope='usr'`、`module='auto'`（`auto` 視為一般 module 區段，不觸發任何特殊分支）

## 3. Module 子命令（cli-module-management）

- [x] 3.1 重構 `src/cli.js`，依「指令介面改為子命令風格」的設計，將現行 `switch(command)` 的 `init`/`update`/`remove` 邏輯拆分並新增 `module` 命令群組，實作「module subcommands replace flag-based filtering」中的 `saifg module add/remove/update <selector...>`；驗證方式：`src/cli.test.js` 新增測試，對暫存目錄執行 `run(['node','saifg','module','add','org.kb','--target', tmpDir])` 後，斷言 `tmpDir/.github` 下只出現符合 `org.kb` 選擇器的檔案，且 `tmpDir/.copilot-library/state.json` 的 `installedFiles` 包含這些路徑
- [x] 3.2 實作 `module add`/`add` 找不到符合檔案時的錯誤行為；驗證方式：`src/cli.test.js` 新增測試，執行 `module add nonexistent-scope` 後斷言 stderr 輸出包含 `Error: no files match selector(s): nonexistent-scope` 且 `process.exitCode` 為非 0
- [x] 3.3 實作 `module update` 不帶 selector 時更新全部已追蹤模組的行為；驗證方式：`src/cli.test.js` 新增測試，先 `module add` 兩個不同 scope 的模組，再執行不帶 selector 的 `module update`，斷言兩個模組的檔案都被重新複製（mtime 更新）
- [x] 3.4 實作頂層 `saifg update` 為 `module update`（無 selector）的語法糖，落實「`saifg update` is a shorthand for updating all modules」；驗證方式：`src/cli.test.js` 新增測試，比對 `run(['node','saifg','update','--target',tmpDir])` 與 `run(['node','saifg','module','update','--target',tmpDir])` 對同一份已安裝狀態產生相同的 `state.json` 內容
- [x] 3.5 改寫 `module list` 輸出，依「`saifg module list` groups output by scope」需求，將已安裝的 selector 依 `org`/`prj`/`usr`/共用分組列印；驗證方式：`src/cli.test.js` 新增測試，安裝跨三種 scope 與一個共用資產後執行 `module list`，斷言 stdout 依序出現四個分組標題且各自列出對應資產

## 4. 設定檔管理（cli-config）

- [x] 4.1 新增 `src/config.js`，依「config.js 的 global/local merge 邏輯」設計，實作 `readConfig(targetDir)` 合併全域 `~/.saifg/config.yaml` 與專案 `<targetDir>/.saifg/config.yaml`，落實「dual-layer configuration file」的專案覆蓋全域邏輯；驗證方式：新增 `src/config.test.js`，分別測試「僅全域有值」「僅專案有值」「兩者皆有值取專案」三種情境，`node --test src/config.test.js` 通過
- [x] 4.2 實作 `saifg config get <key> [-g] [--target <dir>]`，落實「`-g/--global` flag targets the global layer」的預設專案層行為；驗證方式：`src/cli.test.js` 新增測試，`config set` 到專案層後不帶 `-g` 執行 `config get` 回傳該值，帶 `-g` 執行 `config get` 回傳空字串（因全域層未設定）
- [x] 4.3 實作 `saifg config set <key> <value> [-g] [--target <dir>]`，寫入對應層並在目錄不存在時建立；驗證方式：`src/cli.test.js` 新增測試，對空的暫存目錄執行 `config set storage.project.memory /data/memory --target tmpDir`，斷言 `tmpDir/.saifg/config.yaml` 檔案存在且內容含 `storage.project.memory: /data/memory`
- [x] 4.4 實作 `saifg config path [-g]`，落實「`config path` reports the configuration file location」；驗證方式：`src/cli.test.js` 新增測試，斷言 `config path --target tmpDir` 印出 `resolve(tmpDir, '.saifg/config.yaml')`，`config path -g` 印出 `join(os.homedir(), '.saifg/config.yaml')`
- [x] 4.5 實作 `config get` 對未設定 key 回傳空字串且 exit code 0，落實「unset configuration keys resolve to empty」；驗證方式：`src/cli.test.js` 新增測試，對全新暫存目錄執行 `config get storage.org.keys --target tmpDir`，斷言 stdout 為空字串且 `process.exitCode` 未被設為非 0
- [x] 4.6 實作 `saifg config list [-g]`，以 YAML 格式印出指定層的完整內容；驗證方式：`src/cli.test.js` 新增測試，設定兩個 key 後執行 `config list --target tmpDir`，斷言 stdout 可被 YAML parser 解析回相同物件

## 5. Policy 注入機制（entry-policy-injection）

- [x] 5.1 新增 `src/policy.js`，實作「provider auto-detection for policy injection」：偵測 `CLAUDE.md`/`AGENTS.md`/`.github/copilot-instructions.md` 是否存在於 target 目錄，回傳需要注入的檔案清單；驗證方式：新增 `src/policy.test.js`，分別測試三種檔案都存在、只有一個存在、都不存在（回傳空陣列）三種情境
- [x] 5.2 依「entry 檔合併策略（policy 注入）」設計，實作「idempotent policy block injection」：以 `<!-- SAIFG:START v<version> -->`/`<!-- SAIFG:END -->` 標記寫入或取代區塊內容；驗證方式：`src/policy.test.js` 新增測試，對已含舊版本區塊的檔案內容執行注入函式，斷言區塊外文字不變、區塊內文字被新內容取代；對不含區塊的檔案執行注入函式，斷言新區塊被附加到檔案末尾
- [x] 5.3 實作「provider-specific policy content strategy」：`CLAUDE.md` 的注入區塊使用 `@.saifg/policy.md` import 語法、`.github/copilot-instructions.md` 與 `AGENTS.md` 的注入區塊完整內嵌核心規則；驗證方式：`src/policy.test.js` 新增測試，分別對兩種檔案呼叫注入函式，斷言 `CLAUDE.md` 產出內容包含字串 `@.saifg/policy.md` 且不含完整規則長文，`copilot-instructions.md` 產出內容包含完整規則文字且不含 `@.saifg/policy.md`
- [x] 5.4 撰寫套件內 `templates/policy/policy.md` 內容，落實「injected policy states assistant-generated content governance」，明確寫出「助理自產、未經人工確認的內容一律歸類為 `usr.` scope、可被 git 追蹤、不進 npm 發行」；驗證方式：手動檢閱 `templates/policy/policy.md` 內容包含上述三個治理陳述，並於 `src/policy.test.js` 斷言注入到無 import 機制檔案的區塊內容包含相同陳述的關鍵字（`usr.`、`git`、`npm`）
- [x] 5.5 在 `saifg init`/`saifg update` 流程中呼叫 `src/policy.js` 完成安裝 `.saifg/policy.md` 與 entry 檔注入；驗證方式：`src/cli.test.js` 新增整合測試，對含 `CLAUDE.md` 的暫存目錄執行 `saifg init`，斷言 `.saifg/policy.md` 檔案存在且 `CLAUDE.md` 出現 `<!-- SAIFG:START` 標記

## 6. 收尾與回歸驗證

- [x] 6.1 執行完整測試套件，確認所有新增與既有測試皆通過；驗證方式：`node --test` 全數通過且無失敗案例
- [x] 6.2 更新 `CHANGELOG.md` 記錄本次破壞性變更（套件更名、指令介面改版、命名空間規則、policy 注入、設定檔）；驗證方式：手動檢閱 `CHANGELOG.md` 新增條目涵蓋上述五項變更且標示為 Breaking Change
- [x] 6.3 更新目標為 `openspec/LANGUAGE.md`（若不存在則新建），記錄 `auto.` 語意由「套件模板分類前綴」變更為「目標專案執行期治理概念（由 policy 區塊描述）」；驗證方式：手動檢閱 `openspec/LANGUAGE.md` 內含 `auto.` 詞條並附上 definition/avoid/why 說明
