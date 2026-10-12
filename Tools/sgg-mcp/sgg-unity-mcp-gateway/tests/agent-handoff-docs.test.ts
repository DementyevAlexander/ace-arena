import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

describe("agent handoff documentation", () => {
  it("requires current-session MCP availability evidence before marking MCP unavailable", () => {
    const docs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "guides", "diagnostics.md"), "utf8");
    const ruDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "guides", "diagnostics.md"), "utf8");

    for (const text of [docs, ruDocs]) {
      expect(text).toContain("sgg_unity_projects");
      expect(text).toContain("compile.status");
      expect(text).toContain("mcp_availability_checked: current_session");
      expect(text).toContain("active_project_selector");
      expect(text).toContain("compile_status_result");
      expect(text).toContain("evidence_age: current, not historical");
      expect(text).toMatch(/Do not write `MCP skipped`|Не пиши `MCP skipped`/);
    }
  });
});
