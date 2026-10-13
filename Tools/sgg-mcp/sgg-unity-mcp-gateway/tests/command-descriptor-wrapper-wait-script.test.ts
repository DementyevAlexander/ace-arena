import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { afterEach, describe, expect, it } from "vitest";

const roots: string[] = [];
const script = path.join(process.cwd(), "scripts/check-command-descriptors.mjs");
afterEach(() => { for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true }); });

describe("command descriptor wrapper-wait metadata check", () => {
  it.each([undefined, "sgg.unity_mcp.test_run_compile_readiness.v1", "future.v2", "x".repeat(128)])("accepts legacy or bounded additive contract %s", (contract) => {
    const terminal = { min_ms: 1, max_ms: 295000, deadline_scope: "start_and_terminal_poll", response_mode: "compact", dry_run: false,
      ...(contract === undefined ? {} : { compile_readiness_contract: contract }) };
    expect(check({ mode: "wait", wait_for_test_run_terminal_ms: terminal }).status).toBe(0);
  });

  it.each([null, true, 1, {}, "", "x".repeat(129)])("rejects malformed declared compile-readiness metadata %s", (contract) => {
    const result = check({ mode: "wait", wait_for_test_run_terminal_ms: { min_ms: 1, max_ms: 295000, deadline_scope: "start_and_terminal_poll", response_mode: "compact", dry_run: false, compile_readiness_contract: contract } });
    expect(result.status).toBe(1);
    expect(result.stderr).toContain("wait_for_test_run_terminal_ms contract is invalid");
  });

  it("retains the replacement-only field allowlist", () => {
    const result = check({ mode: "wait", wait_for_replacement_ms: { min_ms: 1, max_ms: 45000, deadline_scope: "pre_dispatch_only", compile_readiness_contract: "future.v2" } });
    expect(result.status).toBe(1);
    expect(result.stderr).toContain("wait_for_replacement_ms contract is invalid");
  });

  it("does not weaken the terminal range, mode or requirement gates", () => {
    for (const mutation of [{ min_ms: 0 }, { min_ms: 500000, max_ms: 1 }, { response_mode: "full" }, { dry_run: true }, { unknown: true }]) {
      const result = check({ mode: "wait", wait_for_test_run_terminal_ms: { min_ms: 1, max_ms: 295000, deadline_scope: "start_and_terminal_poll", response_mode: "compact", dry_run: false, compile_readiness_contract: "future.v2", ...mutation } });
      expect(result.status).toBe(1);
    }
  });
});

function check(wrapper: Record<string, unknown>) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-wrapper-descriptor-"));
  roots.push(root);
  const commandDir = path.join(root, "unity/Packages/com.sungeargames.unity-mcp/Editor/Commands");
  fs.mkdirSync(commandDir, { recursive: true });
  // Quoted JSON string literals, inspected as text only, never C# compilation.
  const literal = (value: unknown) => JSON.stringify(JSON.stringify(value));
  fs.writeFileSync(path.join(commandDir, "FixtureCommands.cs"), `const string Schema = ${literal({ type: "object" })};\nconst string Wrapper = ${literal(wrapper)};\n[McpCommand("test.run", Risk = "execute", Idempotency = "unsafe", TimeoutMs = 30000, SchemaJson = Schema, ResultShape = "status", WrapperWaitJson = Wrapper)]`);
  return spawnSync(process.execPath, [script], { cwd: root, encoding: "utf8", timeout: 10000 });
}
