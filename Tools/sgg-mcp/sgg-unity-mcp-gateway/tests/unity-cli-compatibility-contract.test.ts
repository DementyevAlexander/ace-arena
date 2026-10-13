import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const fixtureRoot = path.join(process.cwd(), "tests", "fixtures", "unity-cli-cold-one-shot");
const fixtureSource = fs.readFileSync(path.join(fixtureRoot, "Assets", "Editor", "SggColdOneShotCommands.cs"), "utf8");
const fixtureReadme = fs.readFileSync(path.join(fixtureRoot, "README.md"), "utf8");
const englishGuidance = fs.readFileSync(path.join(process.cwd(), "docs", "en", "connect", "windows-wsl.md"), "utf8");
const russianGuidance = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "connect", "windows-wsl.md"), "utf8");

describe("MCP-UX-007 CLI compatibility contract", () => {
  it("keeps distinct main-thread and background read-only cold one-shot controls", () => {
    expect(fixtureSource).toContain('"sgg_cold_main_thread_noop"');
    expect(fixtureSource).toContain('"sgg_cold_background_noop"');
    expect(fixtureSource).toContain("MainThreadRequired = true");
    expect(fixtureSource).toContain("MainThreadRequired = false");
    expect(fixtureSource).toContain('return "main-thread-ok";');
    expect(fixtureSource).toContain('return "background-ok";');
  });

  it("bounds settling retries and never treats automated mode as a fix", () => {
    for (const guidance of [fixtureReadme, englishGuidance, russianGuidance]) {
      expect(guidance).toContain("command_dispatched:false");
      expect(guidance).toContain("unity_cli_cold_main_thread_gate_stuck");
      expect(guidance).toContain("-automated");
    }
    expect(fixtureReadme).toContain("one fixed startup deadline");
    expect(fixtureReadme).toContain("automatically replay writes or uncertain deliveries");
  });

  it("records beta.6 test failures separately from infrastructure failures", () => {
    for (const guidance of [englishGuidance, russianGuidance]) {
      expect(guidance).toMatch(/exit (?:code )?`?8`?/i);
      expect(guidance).toMatch(/exit (?:code )?`?6`?/i);
      expect(guidance).toContain("TESTS_FAILED");
      expect(guidance).toContain("TEST_RUN_ERROR");
    }
  });
});
