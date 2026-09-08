import {
  readFileSync,
  writeFileSync,
  mkdirSync,
  copyFileSync,
  existsSync,
  readdirSync,
  rmSync,
} from "fs";
import { join, relative, resolve, dirname } from "path";
import { fileURLToPath } from "url";
import { homedir } from "os";
import { parseAssetFilename, matchesSelector } from "./namespace.js";
import {
  readConfigValue,
  readValueFromLayer,
  writeConfigValue,
  readLayerConfig,
  globalConfigPath,
  projectConfigPath,
  serializeConfigForDisplay,
} from "./config.js";
import { applyPolicy } from "./policy.js";

const __filename = fileURLToPath(import.meta.url);
const __dirname = dirname(__filename);
const ROOT = resolve(__dirname, "..");

// Directory structure per ai-toolchain-workflow.md:
// - templates/[module]/.github/[type]/: release layer (npm package artifacts, module-first)
// - .github/[type]/: deploy layer (Copilot runtime, flat merge across all modules)
const TEMPLATES_DIR = join(ROOT, "templates");
const STATE_REL = ".copilot-library/state.json";
const GITHUB_SUBDIR = ".github";

// Artifact types per ai-toolchain-workflow.md §3.1
// Extended types: scripts (PowerShell/bash), docs (Markdown/text resources)
const ARTIFACT_DIRS = ["agents", "instructions", "prompts", "skills", "scripts", "docs"];

// Modules per ai-toolchain-workflow.md §3.2
const MODULE_DIRS = ["code", "copilot", "docs", "kb", "migration", "speckit"];
// Special handling for copilot-instructions.md per ai-toolchain-workflow.md §5.2
// Deploy strategy: if .github/copilot-instructions.md does not exist, install to root;
// if it exists, stage to .github/instructions/ to preserve existing root file and enable merge.
const COPILOT_INSTRUCTIONS_FILENAME = "copilot-instructions.md";
const COPILOT_INSTRUCTIONS_STAGED_PATH = `instructions/${COPILOT_INSTRUCTIONS_FILENAME}`;

const BOOLEAN_FLAGS = new Set(["global"]);

function parseArgs(args) {
  const opts = {};
  const positional = [];
  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    if (arg === "-g") {
      opts.global = true;
      continue;
    }
    if (arg.startsWith("--")) {
      const key = arg.slice(2);
      if (BOOLEAN_FLAGS.has(key)) {
        opts[key] = true;
        continue;
      }
      const next = args[i + 1];
      opts[key] = next && !next.startsWith("--") ? (i++, next) : true;
      continue;
    }
    positional.push(arg);
  }
  return { opts, positional };
}

// Splits selector positional args on commas so `module add a,b` and
// `module add a b` both produce the same selector list.
function parseSelectors(positional) {
  return positional
    .flatMap((s) => s.split(","))
    .map((s) => s.trim())
    .filter(Boolean);
}

function getPackageVersion() {
  return JSON.parse(readFileSync(join(ROOT, "package.json"), "utf8")).version;
}

function readState(targetDir) {
  const p = join(targetDir, STATE_REL);
  if (!existsSync(p)) return null;
  try {
    return JSON.parse(readFileSync(p, "utf8"));
  } catch {
    return null;
  }
}

function writeState(targetDir, patch = {}) {
  const stateDir = join(targetDir, ".copilot-library");
  mkdirSync(stateDir, { recursive: true });
  const now = new Date().toISOString();
  const prev = readState(targetDir) ?? {};
  // State schema per ai-toolchain-workflow.md §10 (Installer State Minimum Schema)
  const state = {
    version: getPackageVersion(),
    targetPath: resolve(targetDir),
    installedAt: prev.installedAt ?? now,
    updatedAt: now,
    namespaceVersion: 2,
    ...patch,
  };
  writeFileSync(join(targetDir, STATE_REL), JSON.stringify(state, null, 2));
  return state;
}

// Matches an asset against the requested selectors using both the
// scope/module matcher (namespace.js) and legacy fine-grained name-level
// prefix matching (e.g. "migration.dotnet-modernizer" selects one file).
function matchesAnySelector(parsed, fullBase, selectors) {
  if (!selectors || selectors.length === 0) return true;
  return selectors.some(
    (sel) =>
      matchesSelector(parsed, sel) ||
      fullBase === sel ||
      fullBase.startsWith(sel + ".") ||
      fullBase.startsWith(sel + "-")
  );
}

function resolveTemplateEntry(relativePath) {
  const normalized = relativePath.replace(/\\/g, "/");
  const parts = normalized.split("/");
  const filename = parts[parts.length - 1];

  // Handle copilot-instructions.md: stage to .github/instructions by default,
  // but promote to .github root if root file does not exist (per ai-toolchain-workflow.md §5.2).
  if (filename === COPILOT_INSTRUCTIONS_FILENAME) {
    return {
      sourceRelativePath: normalized,
      destinationRelativePath: COPILOT_INSTRUCTIONS_STAGED_PATH,
      parsed: parseAssetFilename(filename),
      fullBase: filename.replace(/\.[^.]+$/, ""),
    };
  }

  // Module-first structure: templates/[module]/[type]/ per ai-toolchain-workflow.md §3.3
  // Maps to: .github/[type]/ (flat deployment layer)
  // Supports both standard artifact types (agents, instructions, prompts, skills)
  // and extended types (scripts, docs) for non-artifact resources.
  if (
    parts.length === 3 &&
    MODULE_DIRS.includes(parts[0]) &&
    ARTIFACT_DIRS.includes(parts[1])
  ) {
    const parsed = parseAssetFilename(filename);
    return {
      sourceRelativePath: normalized,
      destinationRelativePath: `${parts[1]}/${filename}`,
      parsed,
      fullBase: fullBaseFromFilename(filename),
    };
  }

  // Legacy flat structure support (backward compatibility): [type]/
  // This path is deprecated; all new modules should use templates/[module]/[type]/ structure.
  if (parts.length === 2 && ARTIFACT_DIRS.includes(parts[0])) {
    const parsed = parseAssetFilename(filename);
    return {
      sourceRelativePath: normalized,
      destinationRelativePath: normalized,
      parsed,
      fullBase: fullBaseFromFilename(filename),
    };
  }

  return null;
}

function fullBaseFromFilename(filename) {
  const knownSuffixes = [".instructions.md", ".agent.md", ".prompt.md", ".skill.md"];
  const suffix = knownSuffixes.find((s) => filename.endsWith(s));
  if (suffix) return filename.slice(0, -suffix.length);
  return filename.replace(/\.[^.]+$/, "");
}

function collectTemplateEntries(dir, baseDir, selectors) {
  const results = [];
  if (!existsSync(dir)) return results;

  for (const entry of readdirSync(dir, { withFileTypes: true })) {
    const full = join(dir, entry.name);
    if (entry.isDirectory()) {
      results.push(...collectTemplateEntries(full, baseDir, selectors));
      continue;
    }

    const rel = relative(baseDir, full).replace(/\\/g, "/");
    const resolved = resolveTemplateEntry(rel);
    if (!resolved) continue;
    if (!matchesAnySelector(resolved.parsed, resolved.fullBase, selectors)) continue;
    results.push(resolved);
  }

  // Deduplicate by destination path. Later entries win so module-scoped templates
  // (templates/[module]/[type]/) can override legacy root templates ([type]/),
  // enabling smooth migration per ai-toolchain-workflow.md §4.
  const dedup = new Map();
  for (const item of results) {
    dedup.set(item.destinationRelativePath, item);
  }

  return Array.from(dedup.values());
}

function resolveDestinationPath(entry, destBase) {
  // Special handling for copilot-instructions.md deployment per ai-toolchain-workflow.md §5.2
  // If root .github/copilot-instructions.md exists: stage to .github/instructions/
  // (preserve existing root file for user review and merge)
  // If root .github/copilot-instructions.md does not exist: install to root
  if (entry.destinationRelativePath !== COPILOT_INSTRUCTIONS_STAGED_PATH) {
    return entry.destinationRelativePath;
  }

  const rootInstructionsPath = join(destBase, COPILOT_INSTRUCTIONS_FILENAME);
  return existsSync(rootInstructionsPath)
    ? COPILOT_INSTRUCTIONS_STAGED_PATH
    : COPILOT_INSTRUCTIONS_FILENAME;
}

function copyFiles(entries, srcBase, destBase) {
  const copiedPaths = [];

  for (const entry of entries) {
    const src = join(srcBase, entry.sourceRelativePath);
    const destinationRelativePath = resolveDestinationPath(entry, destBase);
    const finalDest = join(destBase, destinationRelativePath);
    mkdirSync(dirname(finalDest), { recursive: true });
    copyFileSync(src, finalDest);
    copiedPaths.push(destinationRelativePath);
  }

  return copiedPaths;
}

function getFilenameFromRelativePath(filePath) {
  return filePath.split("/").pop() ?? filePath;
}

function readModuleDescription(module) {
  const readmePath = join(TEMPLATES_DIR, module, "README.md");
  if (!existsSync(readmePath)) return "";
  try {
    const content = readFileSync(readmePath, "utf8");
    const lines = content.split("\n");
    let foundTitle = false;
    for (const line of lines) {
      const trimmed = line.trim();
      if (!foundTitle) {
        if (trimmed.startsWith("# ")) foundTitle = true;
        continue;
      }
      if (trimmed && !trimmed.startsWith("#")) {
        // Strip a leading `<word>` (module name in backticks) to avoid duplication
        return trimmed.replace(/^`[^`]+`\s*/, "");
      }
    }
    return "";
  } catch {
    return "";
  }
}

function mergeInstalledFiles(previousFiles, nextFiles) {
  return Array.from(
    new Set([...(previousFiles ?? []), ...(nextFiles ?? [])])
  ).sort();
}

function mergeTrackedModules(previousModules, requestedSelectors) {
  if (!requestedSelectors || requestedSelectors.length === 0) {
    return ["all"];
  }

  const previous = Array.isArray(previousModules)
    ? previousModules.filter(Boolean)
    : [];

  if (previous.includes("all")) {
    return ["all"];
  }

  return Array.from(new Set([...previous, ...requestedSelectors])).sort();
}

function removeFiles(files, destBase) {
  const boundary = resolve(destBase);

  for (const f of files) {
    const dest = join(destBase, f);
    if (!existsSync(dest)) continue;

    rmSync(dest, { force: true });

    let current = resolve(dirname(dest));
    while (current.startsWith(boundary) && current !== boundary) {
      if (!existsSync(current) || readdirSync(current).length > 0) {
        break;
      }
      rmSync(current, { recursive: true, force: true });
      current = resolve(dirname(current));
    }
  }
}

function resolveExpectedFiles(state, selectors) {
  if (selectors && selectors.length > 0) {
    return collectTemplateEntries(TEMPLATES_DIR, TEMPLATES_DIR, selectors).map(
      (entry) => entry.destinationRelativePath
    );
  }

  if (Array.isArray(state?.installedFiles) && state.installedFiles.length > 0) {
    return state.installedFiles;
  }

  if (
    Array.isArray(state?.modules) &&
    state.modules.length > 0 &&
    !state.modules.includes("all")
  ) {
    return collectTemplateEntries(TEMPLATES_DIR, TEMPLATES_DIR, state.modules).map(
      (entry) => entry.destinationRelativePath
    );
  }

  return collectTemplateEntries(TEMPLATES_DIR, TEMPLATES_DIR, null).map(
    (entry) => entry.destinationRelativePath
  );
}

// Recomputes tracked selectors (module, or scope.module) from remaining
// installed files after a partial `module remove`.
function deriveTrackedSelectors(files) {
  const selectors = new Set();
  for (const file of files) {
    const filename = getFilenameFromRelativePath(file);
    const parsed = parseAssetFilename(filename);
    selectors.add(parsed.scope ? `${parsed.scope}.${parsed.module}` : parsed.module);
  }
  return Array.from(selectors).sort();
}

// Groups tracked installed file paths by scope (org/prj/usr/shared) for
// `module list` display, based on each file's own filename convention.
function groupInstalledFilesByScope(installedFiles) {
  const groups = { org: [], prj: [], usr: [], shared: [] };
  for (const file of installedFiles) {
    const filename = getFilenameFromRelativePath(file);
    const parsed = parseAssetFilename(filename);
    const bucket = parsed.scope ?? "shared";
    groups[bucket].push(file);
  }
  return groups;
}

function installOrUpdate(targetDir, selectors, { verbLabel }) {
  if (!existsSync(targetDir)) {
    console.error(`Error: target directory does not exist: ${targetDir}`);
    process.exitCode = 1;
    return;
  }

  const previousState = readState(targetDir) ?? {};
  const templateEntries = collectTemplateEntries(TEMPLATES_DIR, TEMPLATES_DIR, selectors);

  if (templateEntries.length === 0) {
    if (selectors && selectors.length > 0) {
      console.error(`Error: no files match selector(s): ${selectors.join(", ")}`);
    } else {
      console.error("Error: no files to install (templates may be empty)");
    }
    process.exitCode = 1;
    return;
  }

  const destGithub = join(targetDir, GITHUB_SUBDIR);
  const copiedFiles = copyFiles(templateEntries, TEMPLATES_DIR, destGithub);
  const installedFiles = mergeInstalledFiles(previousState.installedFiles, copiedFiles);
  writeState(targetDir, {
    modules: mergeTrackedModules(previousState.modules, selectors),
    installedFiles,
  });
  console.log(`✓ ${verbLabel} ${copiedFiles.length} file(s) in ${destGithub}`);
}

export async function run(argv) {
  const [, , command, ...rawArgs] = argv;
  const { opts, positional } = parseArgs(rawArgs);

  switch (command) {
    case "init": {
      const targetDir = resolve(process.cwd(), opts.target || ".");
      installOrUpdate(targetDir, null, { verbLabel: "Installed" });
      if (process.exitCode) return;
      applyPolicy(targetDir);
      break;
    }

    case "update": {
      const targetDir = resolve(process.cwd(), opts.target || ".");
      const state = readState(targetDir);
      if (!state) {
        console.warn("Warning: no state.json found. Run init first for a clean install.");
      }
      const trackedSelectors = Array.isArray(state?.modules) && !state.modules.includes("all")
        ? state.modules
        : null;
      installOrUpdate(targetDir, trackedSelectors, { verbLabel: "Updated" });
      if (process.exitCode) return;
      applyPolicy(targetDir);
      break;
    }

    case "module": {
      const [subcommand, ...subArgs] = positional;
      const selectors = parseSelectors(subArgs);
      const targetDir = resolve(process.cwd(), opts.target || ".");

      switch (subcommand) {
        case "add": {
          if (selectors.length === 0) {
            console.error("Error: module add requires at least one selector");
            process.exitCode = 1;
            return;
          }
          installOrUpdate(targetDir, selectors, { verbLabel: "Installed" });
          break;
        }

        case "update": {
          const state = readState(targetDir);
          const effectiveSelectors =
            selectors.length > 0
              ? selectors
              : Array.isArray(state?.modules) && !state.modules.includes("all")
              ? state.modules
              : null;
          if (!state) {
            console.warn("Warning: no state.json found. Run init first for a clean install.");
          }
          installOrUpdate(targetDir, effectiveSelectors, { verbLabel: "Updated" });
          break;
        }

        case "remove": {
          if (!existsSync(targetDir)) {
            console.error(`Error: target directory does not exist: ${targetDir}`);
            process.exitCode = 1;
            return;
          }
          if (selectors.length === 0) {
            console.error(
              "Error: module remove requires at least one selector (use 'all' to remove everything)"
            );
            process.exitCode = 1;
            return;
          }

          const state = readState(targetDir);
          if (!state) {
            console.error(
              "Error: state file not found (.copilot-library/state.json) — cannot safely remove tracked files"
            );
            process.exitCode = 1;
            return;
          }
          if (!Array.isArray(state.installedFiles)) {
            console.error(
              "Error: this installation does not track installed files yet. Run 'module update' once to refresh state.json before using 'module remove'."
            );
            process.exitCode = 1;
            return;
          }

          const isFullRemoval = selectors.includes("all");
          const filesToRemove = isFullRemoval
            ? state.installedFiles
            : state.installedFiles.filter((file) => {
                const filename = getFilenameFromRelativePath(file);
                const parsed = parseAssetFilename(filename);
                const fullBase = fullBaseFromFilename(filename);
                return matchesAnySelector(parsed, fullBase, selectors);
              });

          if (filesToRemove.length === 0 && !isFullRemoval) {
            console.log(`No tracked files matched selector(s): ${selectors.join(", ")}`);
            break;
          }

          const destGithub = join(targetDir, GITHUB_SUBDIR);
          removeFiles(filesToRemove, destGithub);

          const filesToRemoveSet = new Set(filesToRemove);
          const remainingFiles = isFullRemoval
            ? []
            : state.installedFiles.filter((file) => !filesToRemoveSet.has(file));

          if (remainingFiles.length === 0) {
            rmSync(join(targetDir, ".copilot-library"), { recursive: true, force: true });
            console.log(
              `✓ Removed ${filesToRemove.length} tracked file(s) for selector(s): ${selectors.join(", ")} and cleared .copilot-library`
            );
            break;
          }

          writeState(targetDir, {
            modules: deriveTrackedSelectors(remainingFiles),
            installedFiles: remainingFiles,
          });
          console.log(
            `✓ Removed ${filesToRemove.length} tracked file(s) for selector(s): ${selectors.join(", ")}`
          );
          break;
        }

        case "list": {
          const maxLen = Math.max(...MODULE_DIRS.map((m) => m.length));
          console.log("Available modules:");
          for (const module of MODULE_DIRS) {
            const description = readModuleDescription(module);
            const pad = module.padEnd(maxLen);
            const line = `  ${pad}  ${description ? `— ${description}` : ""}`;
            console.log(line.trimEnd());
          }

          const state = readState(targetDir);
          if (state?.installedFiles?.length) {
            const groups = groupInstalledFilesByScope(state.installedFiles);
            const order = [
              ["org", "Organization (org.)"],
              ["prj", "Project (prj.)"],
              ["usr", "User (usr.)"],
              ["shared", "Shared"],
            ];
            console.log("\nInstalled assets in target:");
            for (const [key, heading] of order) {
              if (groups[key].length === 0) continue;
              console.log(`  ${heading}:`);
              for (const file of groups[key]) {
                console.log(`    - ${file}`);
              }
            }
          }
          break;
        }

        default:
          console.error(
            "Usage: saifg module <add|remove|update|list> [<selector...>] [--target <dir>]"
          );
          process.exitCode = 1;
      }
      break;
    }

    case "doctor": {
      const targetDir = resolve(process.cwd(), opts.target || ".");
      let hasError = false;

      if (existsSync(targetDir)) {
        console.log(`✓ Target directory: ${targetDir}`);
      } else {
        console.error(`✗ Target directory not found: ${targetDir}`);
        hasError = true;
      }

      const state = readState(targetDir);
      if (state) {
        console.log(`✓ State file: .copilot-library/state.json`);
        console.log(`  Installed version : ${state.version}`);
        console.log(`  Installed at      : ${state.installedAt}`);
        console.log(`  Last updated      : ${state.updatedAt}`);
        if (state.modules && !state.modules.includes("all")) {
          console.log(`  Installed modules : ${state.modules.join(", ")}`);
        }
      } else {
        console.error(
          `✗ State file not found (.copilot-library/state.json) — run 'init' first`
        );
        hasError = true;
      }

      const expected = resolveExpectedFiles(state, null);
      const destGithub = join(targetDir, GITHUB_SUBDIR);
      const missing = expected.filter((f) => !existsSync(join(destGithub, f)));
      if (missing.length === 0) {
        console.log(`✓ All ${expected.length} expected file(s) are present`);
      } else {
        console.error(`✗ Missing ${missing.length} of ${expected.length} file(s):`);
        for (const f of missing) console.error(`  - .github/${f}`);
        hasError = true;
      }

      if (hasError) process.exitCode = 1;
      break;
    }

    case "config": {
      const [subcommand] = positional;
      const targetDir = resolve(process.cwd(), opts.target || ".");
      const isGlobal = Boolean(opts.global);

      switch (subcommand) {
        case "get": {
          const key = positional[1];
          if (!key) {
            console.error("Error: config get requires a <key>");
            process.exitCode = 1;
            return;
          }
          const value = isGlobal
            ? readValueFromLayer(readLayerConfig(targetDir, { global: true }), key)
            : readConfigValue(targetDir, key);
          console.log(value);
          break;
        }

        case "set": {
          const key = positional[1];
          const value = positional[2];
          if (!key || value === undefined) {
            console.error("Error: config set requires a <key> and <value>");
            process.exitCode = 1;
            return;
          }
          const path = writeConfigValue(targetDir, key, value, { global: isGlobal });
          console.log(`✓ Set ${key} in ${path}`);
          break;
        }

        case "list": {
          const layer = readLayerConfig(targetDir, { global: isGlobal });
          console.log(serializeConfigForDisplay(layer));
          break;
        }

        case "path": {
          console.log(isGlobal ? globalConfigPath() : projectConfigPath(targetDir));
          break;
        }

        default:
          console.error("Usage: saifg config <get|set|list|path> [<key>] [<value>] [-g|--global] [--target <dir>]");
          process.exitCode = 1;
      }
      break;
    }

    default:
      console.log("Usage:");
      console.log("  saifg init                                    [--target <dir>]");
      console.log("  saifg module add    <selector...>             [--target <dir>]");
      console.log("  saifg module remove <selector...>             [--target <dir>]");
      console.log("  saifg module update  [<selector...>]          [--target <dir>]");
      console.log("  saifg module list                             [--target <dir>]");
      console.log("  saifg update                                  [--target <dir>]");
      console.log("  saifg doctor                                  [--target <dir>]");
      console.log("  saifg config get   <key>                      [-g|--global] [--target <dir>]");
      console.log("  saifg config set   <key> <value>              [-g|--global] [--target <dir>]");
      console.log("  saifg config list                             [-g|--global] [--target <dir>]");
      console.log("  saifg config path                             [-g|--global]");
      console.log("");
      console.log("Options:");
      console.log("  --target   Target directory to operate on (default: current directory)");
      console.log("  -g, --global  Operate on the global config layer (~/.saifg/config.yaml)");
      console.log(
        "             Selector examples: kb  |  org.kb  |  org  |  migration.dotnet-modernizer"
      );
      process.exitCode = 1;
      break;
  }
}
