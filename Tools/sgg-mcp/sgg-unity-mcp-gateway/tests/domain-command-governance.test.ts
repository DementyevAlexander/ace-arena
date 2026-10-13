import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();

describe("domain command governance docs", () => {
  it("documents reusable admission and safety gates in EN/RU", () => {
    const en = read("docs/en/extensions/governance.md");
    const ru = read("docs/ru/extensions/governance.md");

    for (const text of [en, ru]) {
      expect(text).toContain("sgg_unity_describe");
      expect(text).toContain("sgg_unity_call");
      expect(text).toContain("top-level MCP tools");
      expect(text).toContain("diagnostics.problem.triage");
      expect(text).toContain("UI/localization/TMP continuation");
      expect(text).toContain("Addressables/project content delivery");
      expect(text).toContain("Dry-run plan/preflight");
      expect(text).toContain("PathGuard");
      expect(text).toContain("precondition hashes");
      expect(text).toContain("Output limits");
      expect(text).toContain("artifact_ref / artifact_refs");
      expect(text).toContain("Public docs");
      expect(text).not.toMatch(/problem_\d{8}/);
      expect(text).not.toMatch(/https?:\/\/(gitlab|jira|linear)\./i);
    }
  });

  it("makes governance discoverable from public entry points", () => {
    expect(read("docs/en/README.md")).toContain("./extensions/governance.md");
    expect(read("docs/ru/README.md")).toContain("./extensions/governance.md");
    expect(read("docs/en/extensions/quick-start.md")).toContain("./governance.md");
    expect(read("docs/ru/extensions/quick-start.md")).toContain("./governance.md");
    expect(read("docs/en/extensions/governance.md")).toContain("Domain Family Governance");
    expect(read("docs/ru/extensions/governance.md")).toContain("Domain Family Governance");
  });
});

function read(relativePath: string): string {
  return fs.readFileSync(path.join(root, relativePath), "utf8");
}
