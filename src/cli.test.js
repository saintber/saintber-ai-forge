import test from "node:test";
import assert from "node:assert/strict";
import {
  existsSync,
  mkdtempSync,
  mkdirSync,
  readFileSync,
  writeFileSync,
  statSync,
} from "fs";
import { join, resolve } from "path";
import { tmpdir } from "os";
import { run } from "./cli.js";

async function captureRun(args) {
  const logs = [];
  const errors = [];
  const warns = [];
  const originalLog = console.log;
  const originalError = console.error;
  const originalWarn = console.warn;
  const previousExitCode = process.exitCode;

  console.log = (...messages) => logs.push(messages.join(" "));
  console.error = (...messages) => errors.push(messages.join(" "));
  console.warn = (...messages) => warns.push(messages.join(" "));
  process.exitCode = 0;

  try {
    await run(["node", "saifg", ...args]);
    return {
      stdout: logs.join("\n"),
      stderr: errors.join("\n"),
      warnings: warns.join("\n"),
      exitCode: process.exitCode ?? 0,
    };
  } finally {
    console.log = originalLog;
    console.error = originalError;
    console.warn = originalWarn;
    process.exitCode = previousExitCode;
  }
}

function makeTargetDir() {
  return mkdtempSync(join(tmpdir(), "saifg-cli-"));
}

function readState(targetDir) {
  return JSON.parse(
    readFileSync(join(targetDir, ".copilot-library", "state.json"), "utf8")
  );
}

test("module list shows available modules with descriptions", async () => {
  const result = await captureRun(["module", "list"]);

  assert.equal(result.exitCode, 0);
  assert.match(result.stdout, /Available modules:/);
  assert.match(result.stdout, /copilot/);
  assert.match(result.stdout, /migration/);
  assert.match(result.stdout, /—/);
  assert.doesNotMatch(result.stdout, /migration\.dotnet-modernizer/);
});

test("module add installs files matching selector into deploy layer and tracks state", async () => {
  const targetDir = makeTargetDir();

  const result = await captureRun(["module", "add", "copilot", "--target", targetDir]);

  assert.equal(result.exitCode, 0);
  assert.equal(existsSync(join(targetDir, ".github", "copilot-instructions.md")), true);
  assert.equal(
    existsSync(join(targetDir, ".github", "instructions", "copilot-instructions.md")),
    false
  );

  const state = readState(targetDir);
  assert.ok(state.installedFiles.includes("copilot-instructions.md"));
  assert.ok(state.version);
  assert.ok(state.targetPath);
  assert.equal(state.namespaceVersion, 2);
});

test("module add stages copilot-instructions under .github/instructions when root file already exists", async () => {
  const targetDir = makeTargetDir();
  mkdirSync(join(targetDir, ".github"), { recursive: true });
  writeFileSync(join(targetDir, ".github", "copilot-instructions.md"), "# existing\nkeep\n");

  const result = await captureRun(["module", "add", "copilot", "--target", targetDir]);

  assert.equal(result.exitCode, 0);
  const rootContent = readFileSync(join(targetDir, ".github", "copilot-instructions.md"), "utf8");
  assert.match(rootContent, /^# existing\nkeep\n/);
  assert.equal(
    existsSync(join(targetDir, ".github", "instructions", "copilot-instructions.md")),
    true
  );

  const state = readState(targetDir);
  assert.ok(state.installedFiles.includes("instructions/copilot-instructions.md"));
});

test("module add reports an error and non-zero exit code when no file matches the selector", async () => {
  const targetDir = makeTargetDir();

  const result = await captureRun(["module", "add", "nonexistent-scope", "--target", targetDir]);

  assert.notEqual(result.exitCode, 0);
  assert.match(result.stderr, /Error: no files match selector\(s\): nonexistent-scope/);
});

test("module add requires at least one selector", async () => {
  const targetDir = makeTargetDir();
  const result = await captureRun(["module", "add", "--target", targetDir]);
  assert.notEqual(result.exitCode, 0);
  assert.match(result.stderr, /requires at least one selector/);
});

test("module remove deletes only tracked installed files and preserves user content", async () => {
  const targetDir = makeTargetDir();
  mkdirSync(join(targetDir, ".github"), { recursive: true });
  writeFileSync(join(targetDir, ".github", "user-note.md"), "keep me\n");

  const addResult = await captureRun(["module", "add", "kb,copilot", "--target", targetDir]);
  assert.equal(addResult.exitCode, 0);

  const stateAfterAdd = readState(targetDir);
  assert.ok(stateAfterAdd.installedFiles.some((f) => f.includes("kb.")));
  assert.ok(stateAfterAdd.installedFiles.some((f) => f.includes("copilot.")));

  const removeResult = await captureRun(["module", "remove", "kb", "--target", targetDir]);
  assert.equal(removeResult.exitCode, 0);
  assert.ok(existsSync(join(targetDir, ".github", "user-note.md")));

  const stateAfterRemove = readState(targetDir);
  assert.equal(stateAfterRemove.installedFiles.some((f) => f.includes("kb.")), false);
});

test("module remove all fully uninstalls tracked content but keeps user .github files", async () => {
  const targetDir = makeTargetDir();
  mkdirSync(join(targetDir, ".github"), { recursive: true });
  writeFileSync(join(targetDir, ".github", "user-note.md"), "keep me\n");

  const addResult = await captureRun(["module", "add", "copilot", "--target", targetDir]);
  assert.equal(addResult.exitCode, 0);
  assert.ok(existsSync(join(targetDir, ".copilot-library", "state.json")));

  const removeResult = await captureRun(["module", "remove", "all", "--target", targetDir]);

  assert.equal(removeResult.exitCode, 0);
  assert.ok(existsSync(join(targetDir, ".github", "user-note.md")));
  assert.equal(existsSync(join(targetDir, ".copilot-library")), false);
});

test("module update with no selector refreshes every tracked module", async () => {
  const targetDir = makeTargetDir();

  await captureRun(["module", "add", "kb", "--target", targetDir]);
  await captureRun(["module", "add", "copilot", "--target", targetDir]);

  const beforeState = readState(targetDir);
  const kbFile = join(targetDir, ".github", beforeState.installedFiles.find((f) => f.includes("kb.")));
  const beforeMtime = statSync(kbFile).mtimeMs;

  await new Promise((r) => setTimeout(r, 20));
  const result = await captureRun(["module", "update", "--target", targetDir]);
  assert.equal(result.exitCode, 0);

  const afterMtime = statSync(kbFile).mtimeMs;
  assert.ok(afterMtime >= beforeMtime);

  const afterState = readState(targetDir);
  assert.ok(afterState.installedFiles.some((f) => f.includes("kb.")));
  assert.ok(afterState.installedFiles.some((f) => f.includes("copilot.")));
});

test("top-level update is equivalent to module update with no selector", async () => {
  const targetDirA = makeTargetDir();
  const targetDirB = makeTargetDir();

  await captureRun(["module", "add", "kb", "--target", targetDirA]);
  await captureRun(["module", "add", "kb", "--target", targetDirB]);

  await captureRun(["update", "--target", targetDirA]);
  await captureRun(["module", "update", "--target", targetDirB]);

  const stateA = readState(targetDirA);
  const stateB = readState(targetDirB);
  assert.deepEqual(stateA.installedFiles, stateB.installedFiles);
});

test("module list groups installed assets by scope", async () => {
  const targetDir = makeTargetDir();
  mkdirSync(join(targetDir, ".github"), { recursive: true });
  writeFileSync(join(targetDir, ".github", "org.kb.custom.skill.md"), "# org kb\n");
  writeFileSync(join(targetDir, ".github", "prj.code.custom.skill.md"), "# prj code\n");
  writeFileSync(join(targetDir, ".github", "usr.docs.custom.skill.md"), "# usr docs\n");
  writeFileSync(join(targetDir, ".github", "kb.faq.skill.md"), "# shared kb\n");
  mkdirSync(join(targetDir, ".copilot-library"), { recursive: true });
  writeFileSync(
    join(targetDir, ".copilot-library", "state.json"),
    JSON.stringify({
      version: "0.0.0",
      targetPath: targetDir,
      installedAt: new Date().toISOString(),
      updatedAt: new Date().toISOString(),
      modules: ["org.kb", "prj.code", "usr.docs", "kb"],
      installedFiles: [
        "org.kb.custom.skill.md",
        "prj.code.custom.skill.md",
        "usr.docs.custom.skill.md",
        "kb.faq.skill.md",
      ],
    })
  );

  const result = await captureRun(["module", "list", "--target", targetDir]);
  assert.equal(result.exitCode, 0);

  const orgIndex = result.stdout.indexOf("Organization (org.)");
  const prjIndex = result.stdout.indexOf("Project (prj.)");
  const usrIndex = result.stdout.indexOf("User (usr.)");
  const sharedIndex = result.stdout.indexOf("Shared");
  assert.ok(orgIndex >= 0 && prjIndex > orgIndex && usrIndex > prjIndex && sharedIndex > usrIndex);
  assert.match(result.stdout, /org\.kb\.custom\.skill\.md/);
  assert.match(result.stdout, /kb\.faq\.skill\.md/);
});

test("init installs everything and applies policy to CLAUDE.md", async () => {
  const targetDir = makeTargetDir();
  writeFileSync(join(targetDir, "CLAUDE.md"), "# My Project\n");

  const result = await captureRun(["init", "--target", targetDir]);

  assert.equal(result.exitCode, 0);
  assert.equal(existsSync(join(targetDir, ".saifg", "policy.md")), true);
  const claudeContent = readFileSync(join(targetDir, "CLAUDE.md"), "utf8");
  assert.match(claudeContent, /<!-- SAIFG:START/);
});

async function withFakeHome(fn) {
  const fakeHome = mkdtempSync(join(tmpdir(), "saifg-cli-home-"));
  const previousUserProfile = process.env.USERPROFILE;
  const previousHome = process.env.HOME;
  process.env.USERPROFILE = fakeHome;
  process.env.HOME = fakeHome;
  try {
    return await fn(fakeHome);
  } finally {
    if (previousUserProfile === undefined) delete process.env.USERPROFILE;
    else process.env.USERPROFILE = previousUserProfile;
    if (previousHome === undefined) delete process.env.HOME;
    else process.env.HOME = previousHome;
  }
}

test("config get defaults to project layer, falls back to empty on global with no value", async () => {
  await withFakeHome(async () => {
    const targetDir = makeTargetDir();
    await captureRun(["config", "set", "storage.project.memory", "/data/memory", "--target", targetDir]);

    const projectResult = await captureRun(["config", "get", "storage.project.memory", "--target", targetDir]);
    assert.equal(projectResult.stdout, "/data/memory");

    const globalResult = await captureRun(["config", "get", "storage.project.memory", "-g", "--target", targetDir]);
    assert.equal(globalResult.stdout, "");
  });
});

test("config set writes to project layer by default, creating the file", async () => {
  await withFakeHome(async () => {
    const targetDir = makeTargetDir();
    const result = await captureRun([
      "config",
      "set",
      "storage.project.memory",
      "/data/memory",
      "--target",
      targetDir,
    ]);
    assert.equal(result.exitCode, 0);

    const configPath = join(targetDir, ".saifg", "config.yaml");
    assert.equal(existsSync(configPath), true);
    assert.match(readFileSync(configPath, "utf8"), /memory: \/data\/memory/);
  });
});

test("config path reports the resolved layer file location", async () => {
  await withFakeHome(async (fakeHome) => {
    const targetDir = makeTargetDir();

    const projectResult = await captureRun(["config", "path", "--target", targetDir]);
    assert.equal(projectResult.stdout, resolve(targetDir, ".saifg", "config.yaml"));

    const globalResult = await captureRun(["config", "path", "-g"]);
    assert.equal(globalResult.stdout, join(fakeHome, ".saifg", "config.yaml"));
  });
});

test("config get returns empty string with exit code 0 for an unset key", async () => {
  await withFakeHome(async () => {
    const targetDir = makeTargetDir();
    const result = await captureRun(["config", "get", "storage.org.keys", "--target", targetDir]);
    assert.equal(result.stdout, "");
    assert.equal(result.exitCode, 0);
  });
});

test("config list prints YAML parseable back into the same values", async () => {
  await withFakeHome(async () => {
    const targetDir = makeTargetDir();
    await captureRun(["config", "set", "storage.org.memory", "/org/memory", "--target", targetDir]);
    await captureRun(["config", "set", "storage.user.kb", "/user/kb", "--target", targetDir]);

    const result = await captureRun(["config", "list", "--target", targetDir]);
    assert.equal(result.exitCode, 0);
    assert.match(result.stdout, /storage:/);
    assert.match(result.stdout, /memory: \/org\/memory/);
    assert.match(result.stdout, /kb: \/user\/kb/);
  });
});
