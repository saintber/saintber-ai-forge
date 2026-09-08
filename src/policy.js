import { readFileSync, writeFileSync, existsSync, mkdirSync, copyFileSync } from "fs";
import { join, dirname } from "path";
import { fileURLToPath } from "url";

const __dirname = dirname(fileURLToPath(import.meta.url));
const ROOT = join(__dirname, "..");
const POLICY_TEMPLATE_SOURCE = join(ROOT, "templates", "policy", "policy.md");
const POLICY_VERSION = "1";

const ENTRY_FILES = [
  { relPath: "CLAUDE.md", supportsImport: true },
  { relPath: "AGENTS.md", supportsImport: false },
  { relPath: join(".github", "copilot-instructions.md"), supportsImport: false },
];

export const CORE_POLICY_TEXT = `## 資產命名規則（saifg）

- 檔名格式：\`[org.|prj.|usr.]<module>.<name>.<type>.md\`，省略前綴代表共用資產。
- \`org.\`：組織層級專屬。\`prj.\`：專案層級專屬。\`usr.\`：個人層級專屬。

## 助理自產內容治理

- 助理在執行期未經人工確認自行產生的內容，一律歸類為 \`usr.\` scope。
- 這類內容預設可被 git 追蹤（可跨機器、跨組織成員同步）。
- 這類內容不進 npm 發行（若此專案本身會發行 npm 套件）。`;

/**
 * Detects which supported entry files exist in the target directory.
 * Returns only the ones that exist, each annotated with whether the
 * provider supports file-import syntax.
 */
export function detectEntryFiles(targetDir) {
  return ENTRY_FILES.filter((entry) =>
    existsSync(join(targetDir, entry.relPath))
  );
}

function buildBlockBody(supportsImport) {
  if (supportsImport) {
    return `## saifg 治理規則\n\n完整規則請見 @.saifg/policy.md`;
  }
  return `## saifg 治理規則\n\n${CORE_POLICY_TEXT}`;
}

function blockMarkers() {
  return {
    start: `<!-- SAIFG:START v${POLICY_VERSION} -->`,
    end: `<!-- SAIFG:END -->`,
  };
}

/**
 * Replaces an existing SAIFG marker block in `content`, or appends a new
 * one if none exists. Content outside the markers is preserved untouched.
 */
export function injectBlockIntoContent(content, supportsImport) {
  const { start, end } = blockMarkers();
  const body = buildBlockBody(supportsImport);
  const block = `${start}\n\n${body}\n\n${end}`;
  const blockPattern = /<!-- SAIFG:START v\d+ -->[\s\S]*?<!-- SAIFG:END -->/;

  if (blockPattern.test(content)) {
    return content.replace(blockPattern, block);
  }

  const separator = content.endsWith("\n") ? "\n" : "\n\n";
  return `${content}${separator}${block}\n`;
}

/**
 * Installs `.saifg/policy.md` into the target project and injects the
 * SAIFG governance block into every detected entry file.
 */
export function applyPolicy(targetDir) {
  const entries = detectEntryFiles(targetDir);
  if (entries.length === 0) {
    console.log(
      "No entry file (CLAUDE.md/AGENTS.md/.github/copilot-instructions.md) found; skipping policy injection."
    );
    return { installedPolicyFile: false, injectedFiles: [] };
  }

  const policyDestDir = join(targetDir, ".saifg");
  const policyDestPath = join(policyDestDir, "policy.md");
  mkdirSync(policyDestDir, { recursive: true });
  copyFileSync(POLICY_TEMPLATE_SOURCE, policyDestPath);

  const injectedFiles = [];
  for (const entry of entries) {
    const filePath = join(targetDir, entry.relPath);
    const existing = readFileSync(filePath, "utf8");
    const updated = injectBlockIntoContent(existing, entry.supportsImport);
    writeFileSync(filePath, updated);
    injectedFiles.push(entry.relPath);
  }

  return { installedPolicyFile: true, injectedFiles };
}
