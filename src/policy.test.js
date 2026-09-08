import test from "node:test";
import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  writeFileSync,
  readFileSync,
  existsSync,
  rmSync,
} from "fs";
import { join } from "path";
import { tmpdir } from "os";
import { detectEntryFiles, injectBlockIntoContent, applyPolicy } from "./policy.js";

function makeTmpDir() {
  return mkdtempSync(join(tmpdir(), "saifg-policy-"));
}

test("detectEntryFiles finds all three known entry files when present", () => {
  const dir = makeTmpDir();
  try {
    writeFileSync(join(dir, "CLAUDE.md"), "# claude\n");
    writeFileSync(join(dir, "AGENTS.md"), "# agents\n");
    mkdirSync(join(dir, ".github"), { recursive: true });
    writeFileSync(join(dir, ".github", "copilot-instructions.md"), "# copilot\n");

    const found = detectEntryFiles(dir).map((e) => e.relPath);
    assert.equal(found.length, 3);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("detectEntryFiles finds only the entry file that exists", () => {
  const dir = makeTmpDir();
  try {
    writeFileSync(join(dir, "CLAUDE.md"), "# claude\n");
    const found = detectEntryFiles(dir);
    assert.equal(found.length, 1);
    assert.equal(found[0].relPath, "CLAUDE.md");
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("detectEntryFiles returns empty array when no entry file exists", () => {
  const dir = makeTmpDir();
  try {
    assert.deepEqual(detectEntryFiles(dir), []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("injectBlockIntoContent appends a new block when none exists", () => {
  const result = injectBlockIntoContent("# My Project\n\nHello.\n", false);
  assert.match(result, /# My Project\n\nHello\.\n/);
  assert.match(result, /<!-- SAIFG:START v1 -->/);
  assert.match(result, /<!-- SAIFG:END -->/);
});

test("injectBlockIntoContent replaces only the marker block, preserving surrounding content", () => {
  const original =
    "# My Project\n\nKeep me.\n\n<!-- SAIFG:START v1 -->\nOLD CONTENT\n<!-- SAIFG:END -->\n";
  const result = injectBlockIntoContent(original, false);
  assert.match(result, /# My Project\n\nKeep me\.\n/);
  assert.doesNotMatch(result, /OLD CONTENT/);
  assert.match(result, /saifg 治理規則/);
});

test("CLAUDE.md block uses import syntax without duplicating full policy text", () => {
  const result = injectBlockIntoContent("# Claude\n", true);
  assert.match(result, /@\.saifg\/policy\.md/);
  assert.doesNotMatch(result, /助理在執行期未經人工確認/);
});

test("copilot-instructions.md block inlines full governance text", () => {
  const result = injectBlockIntoContent("# Copilot\n", false);
  assert.match(result, /助理在執行期未經人工確認/);
  assert.match(result, /usr\./);
  assert.match(result, /git/);
  assert.match(result, /npm/);
  assert.doesNotMatch(result, /@\.saifg\/policy\.md/);
});

test("applyPolicy installs .saifg/policy.md and injects into CLAUDE.md", () => {
  const dir = makeTmpDir();
  try {
    writeFileSync(join(dir, "CLAUDE.md"), "# Project\n");
    const result = applyPolicy(dir);
    assert.equal(result.installedPolicyFile, true);
    assert.equal(existsSync(join(dir, ".saifg", "policy.md")), true);
    const claudeContent = readFileSync(join(dir, "CLAUDE.md"), "utf8");
    assert.match(claudeContent, /<!-- SAIFG:START/);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});

test("applyPolicy skips injection when no entry file exists", () => {
  const dir = makeTmpDir();
  try {
    const result = applyPolicy(dir);
    assert.equal(result.installedPolicyFile, false);
    assert.deepEqual(result.injectedFiles, []);
  } finally {
    rmSync(dir, { recursive: true, force: true });
  }
});
