import { spawnSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

const root = process.cwd();
const scriptPath = path.join(root, "scripts", "check-forbidden-identifiers.mjs");
const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("forbidden identifier check", () => {
  it("rejects blocked MCP/editor naming", () => {
    for (const value of blockedMcpNames()) {
      const dir = makeTempRepo();
      fs.writeFileSync(path.join(dir, "README.md"), `# Test\n\n${value}\n`);

      const result = runCheck(dir);

      expect(result.status).toBe(1);
      expect(result.stderr).toContain("contains forbidden private identifier");
      expect(result.stderr).toContain(JSON.stringify(value));
    }
  });

  it("allows Canonical path/fs/root names and repository references", () => {
    const dir = makeTempRepo();
    fs.mkdirSync(path.join(dir, "docs", "en", "reference"), { recursive: true });
    fs.writeFileSync(path.join(dir, "docs", "en", "reference", "security.md"), [
      "CanonicalPath provides deterministic lexical identity.",
      "CanonicalFS handles root-bound filesystem checks where used.",
      "CanonicalRoot identifies the project root.",
      "https://github.com/romanilyin/canonicalpath.git",
    ].join("\n"));

    const result = runCheck(dir);

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("[check-forbidden-identifiers] ok");
  });

  it("keeps the blocked naming list in the script source", () => {
    const source = fs.readFileSync(scriptPath, "utf8");

    for (const value of blockedMcpNames()) {
      expect(source).toContain(JSON.stringify(value));
    }
  });
});

function blockedMcpNames(): string[] {
  return [
    "Canonical" + "Mcp",
    "Canonical" + " MCP",
    "Canonical" + " MCP Tool",
    "Canonical" + " MCP Command",
  ];
}

function makeTempRepo(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-forbidden-identifiers-"));
  tempDirs.push(dir);
  return dir;
}

function runCheck(cwd: string): ReturnType<typeof spawnSync> {
  return spawnSync(process.execPath, [scriptPath], {
    cwd,
    encoding: "utf8",
  });
}
