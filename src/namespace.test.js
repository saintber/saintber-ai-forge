import test from "node:test";
import assert from "node:assert/strict";
import { parseAssetFilename, matchesSelector } from "./namespace.js";

test("parses filename with explicit scope", () => {
  const result = parseAssetFilename("org.kb.company-policy.skill.md");
  assert.deepEqual(result, {
    scope: "org",
    module: "kb",
    name: "company-policy",
    type: "skill",
  });
});

test("parses filename without a scope segment as shared", () => {
  const result = parseAssetFilename("kb.faq.skill.md");
  assert.deepEqual(result, {
    scope: null,
    module: "kb",
    name: "faq",
    type: "skill",
  });
});

test("parses scope table examples", () => {
  assert.deepEqual(parseAssetFilename("prj.code.setup.agent.md"), {
    scope: "prj",
    module: "code",
    name: "setup",
    type: "agent",
  });
  assert.deepEqual(parseAssetFilename("usr.docs.notes.instructions.md"), {
    scope: "usr",
    module: "docs",
    name: "notes",
    type: "instructions",
  });
});

test("falls back to legacy selector logic for non-standard filenames", () => {
  const result = parseAssetFilename("di-ioc-inventory-script.template.ps1");
  assert.equal(result.module, "di-ioc-inventory-script");
  assert.equal(result.scope, null);
  assert.equal(result.type, undefined);
});

test("does not special-case an auto segment", () => {
  const result = parseAssetFilename("usr.auto.code.snippet.skill.md");
  assert.equal(result.scope, "usr");
  assert.equal(result.module, "auto");
});

test("matchesSelector supports bare module filter", () => {
  const parsed = { scope: "org", module: "kb", name: "x", type: "skill" };
  assert.equal(matchesSelector(parsed, "kb"), true);
  assert.equal(matchesSelector(parsed, "docs"), false);
});

test("matchesSelector supports scope-qualified module filter", () => {
  const parsed = { scope: "org", module: "kb", name: "x", type: "skill" };
  assert.equal(matchesSelector(parsed, "org.kb"), true);
  assert.equal(matchesSelector(parsed, "prj.kb"), false);
});

test("matchesSelector supports bare scope filter", () => {
  const parsedUsr = { scope: "usr", module: "code", name: "x", type: "skill" };
  const parsedOrg = { scope: "org", module: "code", name: "x", type: "skill" };
  assert.equal(matchesSelector(parsedUsr, "usr"), true);
  assert.equal(matchesSelector(parsedOrg, "usr"), false);
});
