import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");
const readPackage = (relative: string) => fs.readFileSync(path.join(packageRoot, relative), "utf8");

describe("MCP-PR-152 compile barrier convergence", () => {
  it("persists exact terminal compilation and before/replacement reload evidence", () => {
    const barrier = readPackage("Editor/Bridge/ExplicitCompilationBarrier.cs");
    const runtime = readPackage("Editor/Bridge/UnityBridgeRuntime.cs");

    expect(barrier).toContain("const int SchemaVersion = 3");
    expect(barrier).toContain('"terminal_compile_result", "terminal_compile_error_count"');
    expect(barrier).toContain("ConsoleLogBuffer.CompileErrorCount()");
    expect(barrier).toContain("WithReloadStarted(string sourceInstanceId, long ticks)");
    expect(barrier).toContain("WithReplacementObserved(string replacementInstanceId, long ticks)");
    expect(barrier).toContain('failure: "runtime_replaced_before_compilation_started"');
    expect(barrier).toContain('failure: "runtime_replaced_without_correlated_reload_proof"');
    expect(barrier).toContain('State == "abandoned" && (Required || !Superseded)');
    expect(barrier).not.toContain("compilation.CompletedGeneration != state.FinishedGeneration");
    expect(runtime.indexOf("ExplicitCompilationBarrier.RecordReloadStarted(InstanceId)")).toBeLessThan(runtime.indexOf('WriteLifecycleHeartbeat("reloading", "assembly_reload")'));
    expect(runtime).toContain("ExplicitCompilationBarrier.RecordReplacementObserved(InstanceId)");
  });

  it("keeps clean recovery replacement-only and accepts current-instance failure only by exact marker", () => {
    const dispatcher = fs.readFileSync(path.join(root, "src", "mcp", "dispatcher.ts"), "utf8");
    const currentBranch = dispatcher.slice(
      dispatcher.indexOf('if (verificationRole === "current")'),
      dispatcher.indexOf("isCompilePostReloadReadyClean(", dispatcher.indexOf('if (verificationRole === "current")')),
    );

    expect(currentBranch).toContain("isExactTerminalCompileFailureWithoutReload");
    expect(currentBranch).not.toContain("ready_clean");
    expect(dispatcher).toMatch(/barrier(?:\?)?\.terminal_compile_result === "failed"/);
    expect(dispatcher).toContain("post_reload_required: !terminalFailureWithoutReload");
    expect(dispatcher).toContain("write_command_repeated: false");
    expect(dispatcher).toContain("write_replay_allowed: false");
    expect(dispatcher).toContain("command_replayed: false");
    expect(dispatcher).toContain("sourceInstanceId === expectedSourceInstanceId");
    expect(dispatcher).toContain("replacementInstanceId === expectedReplacementInstanceId");
    expect(dispatcher).toContain("MAX_COMPILE_RELOAD_EDGE_MS");
    expect(dispatcher).toMatch(/sourceStartedAt\.length > 0[\s\S]*replacementStartedAt\.length > 0[\s\S]*sourceStartedAt === replacementStartedAt/);
    expect(dispatcher).not.toContain("compileRefreshTimeoutVerificationResult");
  });
});
