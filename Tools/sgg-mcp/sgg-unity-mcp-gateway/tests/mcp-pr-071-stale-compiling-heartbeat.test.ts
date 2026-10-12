import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");

describe("MCP-PR-071 stale compiling heartbeat regression", () => {
  it("publishes current editor state after generic compilation completion", () => {
    const consoleSource = readPackage("Editor/Commands/ConsoleLogBuffer.cs");
    const runtimeSource = readPackage("Editor/Bridge/UnityBridgeRuntime.cs");
    const callbackStart = consoleSource.indexOf("static void OnCompilationFinished(object context)");
    const callbackEnd = consoleSource.indexOf("static string ToTimestamp", callbackStart);
    const callback = consoleSource.slice(callbackStart, callbackEnd);
    const forceStart = runtimeSource.indexOf("public static bool ForceHeartbeat(string reason)");
    const forceEnd = runtimeSource.indexOf("public static bool RefreshCommandCatalog", forceStart);
    const forceHeartbeat = runtimeSource.slice(forceStart, forceEnd);

    expect(callback).toContain("CompilationLifecycle.Finish(DateTime.UtcNow)");
    expect(callback.indexOf("CompilationLifecycle.Finish(DateTime.UtcNow)")).toBeLessThan(callback.indexOf("UnityBridgeRuntime.PublishCompilationFinishedHeartbeat()"));
    expect(runtimeSource).toContain('ForceHeartbeat("compilation_pipeline_finished")');
    expect(runtimeSource).toContain('BridgeReloadTrace.Log(\n                "bridge.compilation.finished_heartbeat"');
    expect(forceHeartbeat).toContain("State.UpdateFromEditor()");
    expect(forceHeartbeat).toContain("if (s_lifecycleShutdown)");
    expect(forceHeartbeat).not.toContain('State.MarkReady');
    expect(forceHeartbeat).not.toContain('Status = "ready"');
  });

  it("correlates one assets.refresh with bounded compile and heartbeat evidence", () => {
    const commands = readPackage("Editor/Commands/BuiltInCommands.cs");
    const runtime = readPackage("Editor/Bridge/UnityBridgeRuntime.cs");
    const assetsStart = commands.indexOf("public static CommandExecutionResult AssetsRefresh");
    const assetsEnd = commands.indexOf("internal static CommandExecutionResult CompileRefreshAndRequestBarrier", assetsStart);
    const assetsBody = commands.slice(assetsStart, assetsEnd);

    expect((assetsBody.match(/AssetDatabase\.Refresh\(\)/g) ?? [])).toHaveLength(1);
    expect(assetsBody).toContain("UnityBridgeRuntime.BeginAssetRefresh()");
    expect(assetsBody).toContain("UnityBridgeRuntime.CompleteAssetRefresh(refreshObservation)");
    expect(assetsBody).toContain("refresh_evidence");
    expect(assetsBody).toContain("next_valid_actions");
    expect(assetsBody).toContain("compile.status");
    expect(assetsBody).toContain("compile.wait_ready");
    expect(assetsBody).not.toContain("ExplicitCompilationBarrier");
    expect(assetsBody).not.toContain("RequestScriptCompilation");

    expect(runtime).toContain("compilation_generation_before");
    expect(runtime).toContain("compile_completion_proven");
    expect(runtime).toContain("evidence");
    expect(runtime).toContain("write_replayed");
    expect(runtime).toContain('completionProven ? "finished_observed" : compilationAdvanced ? "active_or_transitioning" : "unproven"');
  });

  it("persists additive per-instance generations in heartbeat schema v1", () => {
    const record = readPackage("Editor/Bridge/HeartbeatRecord.cs");
    const writer = readPackage("Editor/Bridge/HeartbeatWriter.cs");
    const protocol = fs.readFileSync(path.join(root, "src", "bridge", "bridge-protocol.ts"), "utf8");

    for (const field of [
      "heartbeat_generation",
      "refresh_generation",
      "last_asset_refresh_utc",
      "compilation_generation",
      "completed_compilation_generation",
      "last_compilation_started_utc",
      "last_compilation_finished_utc",
    ]) {
      expect(record).toContain(field);
      expect(writer).toContain(field);
      expect(protocol).toContain(`${field}:`);
    }

    expect(record).toContain("public int schema_version = 1");
    expect(writer).toContain("m_writeGeneration = heartbeatGeneration");
    expect(writer.indexOf("WriteHeartbeatFile(")).toBeLessThan(writer.indexOf("m_writeGeneration = heartbeatGeneration"));
    expect(protocol).toContain("heartbeat_generation: z.number().int().min(1)");
    expect(protocol).toContain("completedGeneration > compilationGeneration");
  });

  it("keeps heartbeat age separate from compile completion proof", () => {
    const commands = readPackage("Editor/Commands/BuiltInCommands.cs");
    const runtime = readPackage("Editor/Bridge/UnityBridgeRuntime.cs");

    expect(commands).toContain("compilation_lifecycle");
    expect(commands).toContain("ConsoleLogBuffer.CompilationLifecycleJson()");
    expect(runtime).toContain("compilation.CompletedGeneration == compilation.Generation");
    expect(runtime).toContain("!State.IsCompiling");
    expect(runtime).not.toContain("heartbeat_age_ms");
  });
});

function readPackage(relativePath: string): string {
  return fs.readFileSync(path.join(packageRoot, relativePath), "utf8");
}
