const KNOWN_TYPE_SUFFIXES = [
  ".instructions.md",
  ".agent.md",
  ".prompt.md",
  ".skill.md",
];

const KNOWN_SCOPES = new Set(["org", "prj", "usr"]);

function getFilenameFromRelativePath(filePath) {
  return filePath.split("/").pop() ?? filePath;
}

// Legacy fallback for non-standard asset filenames (scripts/docs) that do not
// use a known `.<type>.md` suffix. Mirrors the pre-existing selector derivation
// so scripts/docs modules keep resolving to the same selector as before.
function legacySelectorFromFilename(filename) {
  const nameWithoutExt = filename.replace(/\.[^.]+$/, "");
  if (filename.includes(".template.")) {
    return nameWithoutExt.split(".template")[0];
  }
  return nameWithoutExt;
}

/**
 * Parses a template asset filename into scope/module/name/type per the
 * `[<scope>.]<module>.<name>.<type>.md` namespace convention.
 * Non-standard filenames (no known type suffix) fall back to the legacy
 * selector logic with `scope: null` and `type: undefined`.
 */
export function parseAssetFilename(filePath) {
  const filename = getFilenameFromRelativePath(filePath);
  const suffix = KNOWN_TYPE_SUFFIXES.find((item) => filename.endsWith(item));

  if (!suffix) {
    return {
      scope: null,
      module: legacySelectorFromFilename(filename),
      name: null,
      type: undefined,
    };
  }

  const type = suffix.slice(1, -".md".length);
  const base = filename.slice(0, -suffix.length);
  const parts = base.split(".");

  if (parts.length > 0 && KNOWN_SCOPES.has(parts[0])) {
    return {
      scope: parts[0],
      module: parts[1] ?? "",
      name: parts.slice(2).join(".") || null,
      type,
    };
  }

  return {
    scope: null,
    module: parts[0] ?? "",
    name: parts.slice(1).join(".") || null,
    type,
  };
}

/**
 * Matches a parsed asset against a selector filter. Supports three forms:
 * - bare module (e.g. "kb")
 * - scope-qualified module (e.g. "org.kb")
 * - bare scope (e.g. "org", matching all modules under that scope)
 */
export function matchesSelector(parsed, selectorFilter) {
  if (!selectorFilter) return true;

  if (selectorFilter.includes(".")) {
    const [scope, ...moduleParts] = selectorFilter.split(".");
    const module = moduleParts.join(".");
    return parsed.scope === scope && parsed.module === module;
  }

  if (KNOWN_SCOPES.has(selectorFilter)) {
    return parsed.scope === selectorFilter;
  }

  return (
    parsed.module === selectorFilter ||
    parsed.module.startsWith(selectorFilter + "-")
  );
}
