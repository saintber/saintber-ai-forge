import test from "node:test";
import assert from "node:assert/strict";
import { mkdtempSync, rmSync } from "fs";
import { join } from "path";
import { tmpdir } from "os";
import {
  readConfig,
  readConfigValue,
  writeConfigValue,
  globalConfigPath,
  projectConfigPath,
} from "./config.js";

function withFakeHome(fn) {
  const fakeHome = mkdtempSync(join(tmpdir(), "saifg-home-"));
  const previousUserProfile = process.env.USERPROFILE;
  const previousHome = process.env.HOME;
  process.env.USERPROFILE = fakeHome;
  process.env.HOME = fakeHome;
  try {
    return fn(fakeHome);
  } finally {
    if (previousUserProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = previousUserProfile;
    if (previousHome === undefined) delete process.env.HOME;
    else process.env.HOME = previousHome;
    rmSync(fakeHome, { recursive: true, force: true });
  }
}

test("readConfigValue returns global value when only global layer is set", () => {
  withFakeHome((fakeHome) => {
    const projectDir = mkdtempSync(join(tmpdir(), "saifg-project-"));
    try {
      writeConfigValue(projectDir, "storage.user.kb", "/global/user-kb", {
        global: true,
      });
      assert.equal(
        readConfigValue(projectDir, "storage.user.kb"),
        "/global/user-kb"
      );
    } finally {
      rmSync(projectDir, { recursive: true, force: true });
    }
  });
});

test("readConfigValue returns project value when only project layer is set", () => {
  withFakeHome(() => {
    const projectDir = mkdtempSync(join(tmpdir(), "saifg-project-"));
    try {
      writeConfigValue(projectDir, "storage.project.keys", "/project/keys");
      assert.equal(
        readConfigValue(projectDir, "storage.project.keys"),
        "/project/keys"
      );
    } finally {
      rmSync(projectDir, { recursive: true, force: true });
    }
  });
});

test("readConfigValue prefers project value over global when both are set", () => {
  withFakeHome(() => {
    const projectDir = mkdtempSync(join(tmpdir(), "saifg-project-"));
    try {
      writeConfigValue(projectDir, "storage.org.memory", "/global/org-memory", {
        global: true,
      });
      writeConfigValue(projectDir, "storage.org.memory", "/project/org-memory");
      assert.equal(
        readConfigValue(projectDir, "storage.org.memory"),
        "/project/org-memory"
      );
    } finally {
      rmSync(projectDir, { recursive: true, force: true });
    }
  });
});

test("unset keys resolve to empty string", () => {
  withFakeHome(() => {
    const projectDir = mkdtempSync(join(tmpdir(), "saifg-project-"));
    try {
      assert.equal(readConfigValue(projectDir, "storage.org.keys"), "");
    } finally {
      rmSync(projectDir, { recursive: true, force: true });
    }
  });
});

test("globalConfigPath and projectConfigPath report expected locations", () => {
  withFakeHome((fakeHome) => {
    assert.equal(globalConfigPath(), join(fakeHome, ".saifg", "config.yaml"));
    assert.equal(
      projectConfigPath("/some/project"),
      join("/some/project", ".saifg", "config.yaml")
    );
  });
});

test("readConfig merges nested storage keys across layers", () => {
  withFakeHome(() => {
    const projectDir = mkdtempSync(join(tmpdir(), "saifg-project-"));
    try {
      writeConfigValue(projectDir, "storage.user.kb", "/global/user-kb", {
        global: true,
      });
      writeConfigValue(projectDir, "storage.project.keys", "/project/keys");
      const merged = readConfig(projectDir);
      assert.equal(merged.storage.user.kb, "/global/user-kb");
      assert.equal(merged.storage.project.keys, "/project/keys");
    } finally {
      rmSync(projectDir, { recursive: true, force: true });
    }
  });
});
