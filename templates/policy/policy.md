# saifg 資產治理規則

本檔案由 `saifg init`/`saifg update` 安裝到專案的 `.saifg/policy.md`，說明資產命名規則與助理自產內容的治理原則。支援 `@path` import 語法的入口檔（例如 `CLAUDE.md`）會直接引入本檔案；不支援 import 的入口檔（例如 `AGENTS.md`、`.github/copilot-instructions.md`）則會將以下核心規則完整內嵌在各自的 `<!-- SAIFG:START -->` 區塊內。

## 資產命名規則（saifg）

- 檔名格式：`[org.|prj.|usr.]<module>.<name>.<type>.md`，省略前綴代表共用資產。
- `org.`：組織層級專屬。`prj.`：專案層級專屬。`usr.`：個人層級專屬。
- `module` 為功能模組（例如 `code`/`copilot`/`docs`/`kb`/`migration`/`speckit`），`type` 為資產類型（`instructions`/`agent`/`prompt`/`skill`）。

範例：`org.kb.company-policy.skill.md`（組織、kb 模組、skill 類型）、`kb.faq.skill.md`（共用、kb 模組、skill 類型）。

## 助理自產內容治理

- 助理在執行期未經人工確認自行產生的內容，一律歸類為 `usr.` scope。
- 這類內容預設可被 git 追蹤（可跨機器、跨組織成員同步）。
- 這類內容不進 npm 發行（若此專案本身會發行 npm 套件）。

## 為什麼這樣設計

`usr.` scope 代表「個人層級、未經團隊審核」的內容。允許這類內容進 git，是為了讓同一個人在不同機器之間、或組織成員之間可以同步這些尚未正式確認的產出；但因為尚未經過人工審核，不應該隨套件對外發行，以避免未審核內容外流成為對外承諾的一部分。
