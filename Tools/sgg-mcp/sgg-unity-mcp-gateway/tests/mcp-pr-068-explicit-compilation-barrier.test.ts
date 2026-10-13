import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");
const readPackage = (relative: string) => fs.readFileSync(path.join(packageRoot, relative), "utf8");

describe("MCP-PR-068 explicit compilation barrier source contract", () => {
  it("uses one strict versioned SessionState blob without filesystem or package inventory", () => {
    const source = readPackage("Editor/Bridge/ExplicitCompilationBarrier.cs");

    expect(source).toContain('const string SessionStateKey = "SGG.UnityMCP.ExplicitCompilationBarrier.state"');
    expect(source.match(/SessionState\.SetString\(/g)).toHaveLength(1);
    expect(source).toContain('"v", "status", "operation_id"');
    expect(source).toContain("fields.Length != Keys.Length");
    expect(source).toContain('values[0] != "3"');
    expect(source).toContain('"terminal_compile_result", "terminal_compile_error_count"');
    expect(source).toContain('"reload_started_ticks", "reload_source_instance_id"');
    expect(source).toContain('Malformed("malformed_session_state")');
    expect(source).not.toMatch(/System\.IO|SHA256|PackageInfo|GetAllRegisteredPackages|Directory\.|File\./);
    expect(fs.existsSync(path.join(packageRoot, "Editor", "Bridge", "CompileInputFreshness.cs"))).toBe(false);
  });

  it("arms before synchronous refresh and requests compilation only when refresh did not start the correlated generation", () => {
    const barrier = readPackage("Editor/Bridge/ExplicitCompilationBarrier.cs");
    const commands = readPackage("Editor/Commands/BuiltInCommands.cs");
    const preflightIndex = commands.indexOf("CompileRefreshBusyPreflight(");
    const beginIndex = commands.indexOf("ExplicitCompilationBarrier.Begin(expectedOperationId)");
    const armIndex = commands.indexOf("ExplicitCompilationBarrier.MarkRequestIssued(operationId)", beginIndex);
    const refreshIndex = commands.indexOf("AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport)", armIndex);
    const refreshSnapshotIndex = commands.indexOf("ExplicitCompilationBarrier.Snapshot(operationId, true)", refreshIndex);
    const requestCall = "CompilationPipeline.RequestScriptCompilation()";
    const requestIndex = commands.indexOf(requestCall, refreshSnapshotIndex);

    expect(preflightIndex).toBeGreaterThanOrEqual(0);
    expect(preflightIndex).toBeLessThan(beginIndex);
    expect(beginIndex).toBeGreaterThanOrEqual(0);
    expect(armIndex).toBeGreaterThan(beginIndex);
    expect(refreshIndex).toBeGreaterThan(armIndex);
    expect(refreshSnapshotIndex).toBeGreaterThan(refreshIndex);
    expect(requestIndex).toBeGreaterThan(refreshSnapshotIndex);
    expect(commands).toContain("IsMatchingArmedCompileBarrier(barrierAfterRefresh, operationId)");
    expect(commands).toContain("ShouldRequestCompilationAfterRefresh(barrierAfterRefresh, operationId, EditorApplication.isCompiling)");
    expect(commands).toContain("refresh_started_compilation");
    expect(commands.split(requestCall)).toHaveLength(2);
    expect(commands).not.toContain("RequestScriptCompilationOptions.CleanBuildCache");
    expect(commands.match(/AssetDatabase\.Refresh\(ImportAssetOptions\.ForceSynchronousImport\)/g)).toHaveLength(1);
    expect(commands).toContain("EditorApplication.isCompiling");
    expect(commands).toContain("EditorApplication.isUpdating");
    expect(commands).toContain("EditorApplication.isPlayingOrWillChangePlaymode");
    expect(commands).toContain(',\\"refresh_started\\":false');
    expect(commands).toContain(',\\"compile_barrier_created\\":false');
    expect(commands).toContain(',\\"script_compilation_requested\\":false');
    expect(barrier).toContain("CompilationPipeline.compilationStarted += OnCompilationStarted");
    expect(barrier).toContain("CompilationPipeline.compilationFinished += OnCompilationFinished");
  });

  it("ignores pre-request compilation and rejects overlap or invalid callback order", () => {
    const source = readPackage("Editor/Bridge/ExplicitCompilationBarrier.cs");

    expect(source).toContain("if (!RequestIssued)");
    expect(source).toContain('Failed("finish_without_start")');
    expect(source).toContain('Failed("multiple_generation_overlap"');
    expect(source).toContain('prior.Status == "pending"');
    expect(source).toContain('"explicit_compilation_operation_pending"');
    expect(source).toContain("MaxOperationAgeTicks = TimeSpan.TicksPerHour");
    expect(source).toContain('"replaced_unarmed_pending"');
    expect(source).toContain('"abandoned_stale_armed"');
    expect(source).toContain('"reset_invalid_state"');
    expect(source).toContain("EditorApplication.isCompiling");
    expect(source).toContain('"active_compilation_blocks_stale_replacement"');
    expect(source).toContain("ExplicitCompilationBarrierCallbacks.HandleStarted");
    expect(source).toContain("ExplicitCompilationBarrierCallbacks.HandleFinished");
    expect(source).toContain("ReferenceEquals(epoch.StartedContext, context)");
    expect(source).not.toContain("!Equals(epoch.StartedContext, context)");
  });

  it("requires a matching verified barrier for PR065 and timeout recovery", () => {
    const dispatcher = fs.readFileSync(path.join(root, "src", "mcp", "dispatcher.ts"), "utf8");

    expect(dispatcher).toContain("_compile_barrier_expectation_id: randomUUID().replaceAll");
    expect(dispatcher).toContain("_require_explicit_compile_barrier: true");
    expect(dispatcher).toContain('value.state === "verified"');
    expect(dispatcher).toContain("value.request_issued === true");
    expect(dispatcher).toContain("value.operation_id === expectedBarrierOperationId");
    expect(dispatcher).toContain("command: COMPILE_WAIT_READY_COMMAND");
    expect(dispatcher).toContain("directCompileRefreshBarrierError(commandId, commandArgs, response, record.instance_id)");
    expect(dispatcher).toContain('"successful_response_without_matching_finished_barrier"');
    expect(dispatcher).toContain('"compilation_failed_without_matching_finished_barrier"');
    expect(dispatcher).toContain('write_replay_allowed: false');
    expect(dispatcher).toContain('"do_not_auto_repeat_validation.after_edit"');
    expect(dispatcher).toContain('"do_not_auto_repeat_compile.refresh_and_wait"');
    expect(dispatcher).not.toContain("safeCompileFreshness");
    expect(dispatcher).not.toContain("current_fingerprint");
  });

  it("leaves assets.refresh independent of the explicit compilation barrier", () => {
    const commands = readPackage("Editor/Commands/BuiltInCommands.cs");
    const assetsStart = commands.indexOf("public static CommandExecutionResult AssetsRefresh");
    const barrierStart = commands.indexOf("internal static CommandExecutionResult CompileRefreshAndRequestBarrier");
    const assetsBody = commands.slice(assetsStart, barrierStart);

    expect(assetsBody).toContain("AssetDatabase.Refresh()");
    expect(assetsBody).not.toContain("RequestScriptCompilation");
    expect(assetsBody).not.toContain("ExplicitCompilationBarrier");
  });

  it("keeps background snapshots free of SessionState and UnityEditor calls", () => {
    const source = readPackage("Editor/Bridge/ExplicitCompilationBarrier.cs");
    const snapshotStart = source.indexOf("public static ExplicitCompilationBarrierSnapshot Snapshot");
    const snapshotEnd = source.indexOf("public static string StatusJson", snapshotStart);
    const snapshotBody = source.slice(snapshotStart, snapshotEnd);

    expect(snapshotStart).toBeGreaterThanOrEqual(0);
    expect(snapshotBody).toContain("lock (Gate)");
    expect(snapshotBody).toContain("s_cachedState.Clone()");
    expect(snapshotBody).not.toContain("SessionState");
    expect(snapshotBody).not.toContain("EditorApplication");
    expect(snapshotBody).not.toContain("UnityEditor");
    expect(source).toContain("EnsureInitializedOnMainThread()");
    expect(source).toContain("PersistOnMainThread");
  });
});
