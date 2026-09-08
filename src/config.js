import { readFileSync, writeFileSync, mkdirSync, existsSync } from "fs";
import { join, dirname } from "path";
import { homedir } from "os";

const CONFIG_FILENAME = "config.yaml";

export function globalConfigPath() {
  return join(homedir(), ".saifg", CONFIG_FILENAME);
}

export function projectConfigPath(targetDir) {
  return join(targetDir, ".saifg", CONFIG_FILENAME);
}

// Minimal YAML reader/writer scoped to the flat `storage.<scope>.<field>: <string>`
// schema this config file uses. Not a general-purpose YAML implementation.
function parseYaml(text) {
  const root = {};
  const stack = [{ indent: -1, node: root }];

  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.replace(/#.*$/, "").trimEnd();
    if (!line.trim()) continue;

    const indent = line.length - line.trimStart().length;
    const [keyPart, ...rest] = line.trim().split(":");
    const key = keyPart.trim();
    const value = rest.join(":").trim();

    while (stack.length > 1 && indent <= stack[stack.length - 1].indent) {
      stack.pop();
    }
    const parent = stack[stack.length - 1].node;

    if (value === "") {
      const child = {};
      parent[key] = child;
      stack.push({ indent, node: child });
    } else {
      parent[key] = value;
    }
  }

  return root;
}

function serializeYaml(obj, indent = 0) {
  const pad = "  ".repeat(indent);
  const lines = [];
  for (const [key, value] of Object.entries(obj)) {
    if (value && typeof value === "object" && !Array.isArray(value)) {
      lines.push(`${pad}${key}:`);
      lines.push(serializeYaml(value, indent + 1));
    } else {
      lines.push(`${pad}${key}: ${value}`);
    }
  }
  return lines.filter((l) => l !== "").join("\n");
}

function readYamlFile(path) {
  if (!existsSync(path)) return {};
  try {
    return parseYaml(readFileSync(path, "utf8"));
  } catch {
    return {};
  }
}

function writeYamlFile(path, obj) {
  mkdirSync(dirname(path), { recursive: true });
  writeFileSync(path, serializeYaml(obj) + "\n");
}

function getByKeyPath(obj, keyPath) {
  const parts = keyPath.split(".");
  let current = obj;
  for (const part of parts) {
    if (current == null || typeof current !== "object") return undefined;
    current = current[part];
  }
  return typeof current === "string" ? current : undefined;
}

function setByKeyPath(obj, keyPath, value) {
  const parts = keyPath.split(".");
  let current = obj;
  for (let i = 0; i < parts.length - 1; i++) {
    const part = parts[i];
    if (typeof current[part] !== "object" || current[part] === null) {
      current[part] = {};
    }
    current = current[part];
  }
  current[parts[parts.length - 1]] = value;
}

/**
 * Merges global and project-level config with project taking precedence
 * per key path (shallow leaf-value merge, like `git config local > global`).
 */
export function readConfig(targetDir) {
  const global = readYamlFile(globalConfigPath());
  const project = readYamlFile(projectConfigPath(targetDir));

  function mergeInto(target, source) {
    for (const [key, value] of Object.entries(source)) {
      if (value && typeof value === "object" && !Array.isArray(value)) {
        if (typeof target[key] !== "object" || target[key] === null) {
          target[key] = {};
        }
        mergeInto(target[key], value);
      } else {
        target[key] = value;
      }
    }
  }

  const merged = {};
  mergeInto(merged, global);
  mergeInto(merged, project);
  return merged;
}

export function readConfigValue(targetDir, keyPath) {
  return getByKeyPath(readConfig(targetDir), keyPath) ?? "";
}

export function readValueFromLayer(layer, keyPath) {
  return getByKeyPath(layer, keyPath) ?? "";
}

export function readLayerConfig(targetDir, { global = false } = {}) {
  return global ? readYamlFile(globalConfigPath()) : readYamlFile(projectConfigPath(targetDir));
}

export function writeConfigValue(targetDir, keyPath, value, { global = false } = {}) {
  const path = global ? globalConfigPath() : projectConfigPath(targetDir);
  const existing = readYamlFile(path);
  setByKeyPath(existing, keyPath, value);
  writeYamlFile(path, existing);
  return path;
}

export function serializeConfigForDisplay(obj) {
  return serializeYaml(obj);
}
