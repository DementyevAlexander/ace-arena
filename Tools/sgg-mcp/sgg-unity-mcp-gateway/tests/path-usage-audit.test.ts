import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const srcRoot = path.join(root, "src");

describe("path identity usage audit", () => {
  it("keeps identity and security comparisons behind CanonicalPath adapters", () => {
    const sources = readSourceFiles(srcRoot);
    const forbiddenNormalize = sources.filter((file) => file.content.includes("path.normalize("));
    const forbiddenResolve = sources.filter((file) => file.content.includes("path.resolve(") && file.relative !== "cli.ts");

    expect(forbiddenNormalize.map((file) => file.relative)).toEqual([]);
    expect(forbiddenResolve.map((file) => file.relative)).toEqual([]);
  });

  it("keeps project registry and cache identity on canonical roots", () => {
    const registry = fs.readFileSync(path.join(srcRoot, "registry", "project-registry.ts"), "utf8");
    const protocol = fs.readFileSync(path.join(srcRoot, "bridge", "bridge-protocol.ts"), "utf8");
    const dispatcher = fs.readFileSync(path.join(srcRoot, "mcp", "dispatcher.ts"), "utf8");

    expect(registry).toContain("new CanonicalPathService()");
    expect(protocol).toContain("canonicalPaths.normalizeProjectRoot(record.project_root)");
    expect(protocol).toContain("canonicalPaths.projectRootAliases(record.project_root)");
    expect(registry).toContain("record.canonical_project_root === canonicalSelector");
    expect(dispatcher).toContain("createHash(\"sha256\").update(record.canonical_project_root)");
    expect(dispatcher).not.toContain("update(record.project_root)");
  });
});

function readSourceFiles(directory: string, prefix = ""): Array<{ relative: string; content: string }> {
  const files: Array<{ relative: string; content: string }> = [];
  for (const entry of fs.readdirSync(directory, { withFileTypes: true })) {
    const relative = path.join(prefix, entry.name);
    const fullPath = path.join(directory, entry.name);
    if (entry.isDirectory()) {
      files.push(...readSourceFiles(fullPath, relative));
    } else if (entry.isFile() && entry.name.endsWith(".ts")) {
      files.push({ relative, content: fs.readFileSync(fullPath, "utf8") });
    }
  }

  return files;
}
