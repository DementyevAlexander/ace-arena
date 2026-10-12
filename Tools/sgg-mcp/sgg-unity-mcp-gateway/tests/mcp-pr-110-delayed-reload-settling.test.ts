import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");
const readPackage = (relative: string) => fs.readFileSync(path.join(packageRoot, relative), "utf8");

describe("MCP-PR-110 delayed reload settling", () => {
  it("arms the exact compile generation before refresh and exposes unsettled readiness", () => {
    const commands = readPackage("Editor/Commands/BuiltInCommands.cs");
    const barrier = readPackage("Editor/Bridge/ExplicitCompilationBarrier.cs");
    const arm = commands.indexOf("ExplicitCompilationBarrier.MarkRequestIssued(operationId)");
    const refresh = commands.indexOf("AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport)", arm);
    const conditionalRequest = commands.indexOf("if (scriptCompilationRequested)", refresh);

    expect(arm).toBeGreaterThanOrEqual(0);
    expect(refresh).toBeGreaterThan(arm);
    expect(conditionalRequest).toBeGreaterThan(refresh);
    expect(barrier).toContain('public bool ReloadPending => Verified && Operation.TerminalCompileResult == "clean" && !PostReloadObserved;');
    expect(barrier).toContain('public bool Settling => State == "pending" || ReloadPending || (Verified && Operation.TerminalCompileResult == "unknown");');
    expect(commands).toContain('compileBarrier.Settling ? "settling"');
    expect(commands).toContain(String.raw`\"lifecycle_settled\"`);
  });

  it("arms Pipeline updater compilation before refresh and requests only when refresh did not start it", () => {
    const updater = readPackage("Editor/Setup/UnityPipelineUpdateOperation.cs");
    const begin = updater.indexOf("ExplicitCompilationBarrier.Begin(state.operationId)");
    const arm = updater.indexOf("ExplicitCompilationBarrier.MarkRequestIssued(state.compileBarrierOperationId)", begin);
    const refresh = updater.indexOf("AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport)", arm);
    const snapshot = updater.indexOf("ExplicitCompilationBarrier.Snapshot(state.compileBarrierOperationId, true)", refresh);
    const request = updater.indexOf("CompilationPipeline.RequestScriptCompilation()", snapshot);

    expect(begin).toBeGreaterThanOrEqual(0);
    expect(arm).toBeGreaterThan(begin);
    expect(refresh).toBeGreaterThan(arm);
    expect(snapshot).toBeGreaterThan(refresh);
    expect(request).toBeGreaterThan(snapshot);
    expect(updater.slice(snapshot, request)).toContain("BuiltInCommands.IsMatchingArmedCompileBarrier");
    expect(updater.slice(snapshot, request)).toContain("BuiltInCommands.ShouldRequestCompilationAfterRefresh");
    expect(updater.slice(snapshot, request)).toContain("EditorApplication.isCompiling");
    expect(updater.slice(snapshot, request)).toContain("pipeline_update_compile_barrier_invalid_after_refresh");
  });

  it("binds validation and Play Mode terminal success to post-reload evidence", () => {
    const server = readPackage("Editor/Bridge/BridgeServer.cs");
    const tracker = readPackage("Editor/Bridge/PlayModeTransitionTracker.cs");
    const dispatcher = fs.readFileSync(path.join(root, "src", "mcp", "dispatcher.ts"), "utf8");

    expect(server).toContain('out operationId[0], out _, "validation.after_edit"');
    expect(server).toContain("ValidationWaitReadyResult(timeoutMs, pollIntervalMs, operationId[0], true)");
    expect(tracker).toContain('return "v=2;operation_id="');
    expect(tracker).toContain('";reload_expected="');
    expect(tracker).toContain("EnterPlayModeOptions.DisableDomainReload");
    expect(tracker).toContain("TargetReachedForRestoration(s_state.Target, EditorApplication.isPlaying, EditorApplication.isPlayingOrWillChangePlaymode)");
    expect(tracker).toContain("SessionState.SetString(SessionStateKey, s_state.Serialize())");
    expect(tracker).toContain("EditorApplication.update += ReconcileRestoredTransition");
    expect(tracker).toContain("EditorApplication.update -= ReconcileRestoredTransition");
    expect(dispatcher).toContain("const PLAY_MODE_TRANSITION_STABILITY_OVERHEAD_MS = 30_000");
    expect(dispatcher).toContain("transition?.reload_expected !== false || role === \"replacement\"");
    expect(dispatcher).toContain('const verificationField = validationOperation ? "validation_after_edit_verification" : "compile_refresh_verification"');
  });

  it("returns an exact terminal compile failure without waiting for an impossible reload", () => {
    const server = readPackage("Editor/Bridge/BridgeServer.cs");
    const commands = readPackage("Editor/Commands/BuiltInCommands.cs");
    const barrier = readPackage("Editor/Bridge/ExplicitCompilationBarrier.cs");
    const terminalMatch = "var terminalCompileFailureWithoutReload = barrier.TerminalCompileFailureWithoutReload;";
    const validationWait = server.slice(server.indexOf("CommandExecutionResult ValidationWaitReadyResult"), server.indexOf("CommandExecutionResult CompileWaitReadyResult"));
    const compileWait = server.slice(server.indexOf("CommandExecutionResult CompileWaitReadyResult"), server.indexOf("static CommandExecutionResult LifecycleShutdownResult"));

    expect(server.split(terminalMatch)).toHaveLength(3);
    expect(validationWait.indexOf(terminalMatch)).toBeLessThan(validationWait.indexOf("if (lifecycle.IsReady && barrier.Stable)"));
    expect(compileWait.indexOf(terminalMatch)).toBeLessThan(compileWait.indexOf("if (lifecycle.IsReady && barrier.Stable)"));
    expect(validationWait.indexOf('if (priorityErrorCode == "compilation_failed")')).toBeLessThan(validationWait.indexOf('if (priorityErrorCode == "package_metadata_unavailable")'));
    expect(compileWait.indexOf('if (priorityErrorCode == "compilation_failed")')).toBeLessThan(compileWait.indexOf('if (priorityErrorCode == "package_metadata_unavailable")'));
    expect(server).toContain("if (terminalCompileFailureWithoutReload\n                || (barrierStable && hasCompileErrors && (isReady || requireExplicitBarrier)))");
    expect(barrier).toContain('TerminalCompileResult == "failed"');
    expect(barrier).toContain("TerminalCompileErrorCount > 0");
    expect(commands).toContain("var terminalCompileFailureWithoutReload = compileBarrier.TerminalCompileFailureWithoutReload;");
    expect(commands).toContain(': terminalCompileFailureWithoutReload && editorReady ? "compilation_failed"');
    expect(commands).toContain(String.raw`\"terminal_compile_failure_without_reload\"`);
    expect(barrier).toContain("ConsoleLogBuffer.CompileErrorCount()");
    expect(barrier).toContain("RecordReloadStarted(string sourceInstanceId)");
    expect(barrier).toContain("RecordReplacementObserved(string replacementInstanceId)");
  });

  it("refuses immediate test execution while the latest compile lifecycle is settling", () => {
    const tests = readPackage("Editor/Commands/BuildTestAutomationCommands.cs");

    const lifecycleGuard = "RequireSettledCompilationForTestRun(context.State)";
    const firstGuard = tests.indexOf(lifecycleGuard);
    const execute = tests.indexOf("InvokeReflected(api, \"Execute\", settings)");
    const finalGuard = tests.lastIndexOf(lifecycleGuard, execute);
    expect(firstGuard).toBeGreaterThanOrEqual(0);
    expect(execute).toBeGreaterThan(firstGuard);
    expect(finalGuard).toBeGreaterThan(firstGuard);
    expect(finalGuard).toBeLessThan(execute);
    expect(tests).toContain("TestRunCompilationBlockReason(barrier, isUpdating, packageRefreshRequired, packageRefreshWaitCanAdvance, backgroundCompilerProgress)");
    expect(tests).toContain('if (backgroundCompilerProgress.BlocksReadiness) return "background_compilation_pending";');
    expect(tests).toContain('if (isUpdating) return "asset_update_active";');
    expect(tests).toContain('if (packageRefreshRequired && !packageRefreshWaitCanAdvance) return "package_refresh_required";');
    expect(tests).toContain('reason == "package_refresh_required"');
    expect(tests).toContain(String.raw`\"command_dispatched\":false`);
    expect(tests).toContain(String.raw`\"test_runner_execute_invoked\":false`);
    expect(tests).toContain(String.raw`\"write_replay_allowed\":false`);
    expect(tests).toContain("EditorApplication.isUpdating");
    expect(tests).toContain("lifecycle != null && lifecycle.PackageRefreshRequired");
    expect(tests).toContain("lifecycle.PackageRefreshWaitCanAdvance");
    expect(tests).toContain('"asset_update_active"');
    expect(tests).toContain('"package_refresh_pending"');
  });
});
