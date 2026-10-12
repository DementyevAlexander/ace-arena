import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const editorStatusCommandsPath = path.join(packageRoot, "Editor", "Commands", "EditorStatusCommands.cs");

describe("Unity editor status command source-contract", () => {
  it("declares editor status and playmode commands through the command catalog", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");

    expect(source).toContain('"editor.state.get"');
    expect(source).toContain('"editor.status"');
    expect(source).toContain('"editor.playmode.enter"');
    expect(source).toContain('"editor.playmode.set"');
    expect(source).toContain('"editor.playmode.exit"');
    expect(source).toContain('"editor.quit"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source.match(/Risk = "write"/g)).toHaveLength(3);
    expect(source.match(/Idempotency = "idempotent"/g)).toHaveLength(3);
    expect(source).toContain('Risk = "destructive"');
    expect(source).toContain('Idempotency = "unsafe"');
  });

  it("gates editor status with the editor_csharp_execution feature flag", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "editor_csharp_execution"');
    expect(source).toContain('const string EditorUtilitiesFeatureFlag = "editor_utilities"');
    expect(source).toContain('Capability = "editor_csharp_execution"');
    expect(source).toContain("FeatureFlag = FeatureFlag");
  });

  it("adds a read-only Unity AI Assistant state wrapper under editor_utilities", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");
    const stateSource = source.slice(source.indexOf('"editor.state.get"'), source.indexOf('[McpCommand(\n            "editor.status"'));

    expect(stateSource).toContain('Risk = "read"');
    expect(stateSource).toContain('Idempotency = "safe"');
    expect(stateSource).toContain('Capability = EditorUtilitiesFeatureFlag');
    expect(stateSource).toContain('FeatureFlag = EditorUtilitiesFeatureFlag');
    expect(stateSource).toContain('Tags = new string[] { "editor", "status", "unity-ai-assistant" }');
    expect(stateSource).toContain('StatusJson(context.State, "Unity.ManageEditor GetState")');
    expect(source).toContain('\\"compatible_with\\"');
    expect(source).toContain('JsonUtil.String(compatibleWith)');
    expect(source).toContain('\\"has_unsaved_scene_changes\\"');
    expect(source).toContain('\\"dirty_scene_count\\"');
    expect(source).toContain('\\"dirty_scenes\\"');
    expect(source).toContain('BuiltInCommands.DirtyScenesJson(state)');
    expect(stateSource).not.toContain('isPlaying = true');
    expect(stateSource).not.toContain('isPlaying = false');
    expect(stateSource).not.toContain('applicationPath');
    expect(stateSource).not.toContain('dataPath');
  });

  it("reports the status states required before dynamic editor execution", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");

    expect(source).toContain('return "compiling"');
    expect(source).toContain('return "updating"');
    expect(source).toContain('return "playing_paused"');
    expect(source).toContain('return "playing"');
    expect(source).toContain('return "editing"');
    expect(source).toContain(String.raw`\"is_playing_actual\"`);
    expect(source).toContain(String.raw`\"playmode_target\"`);
    expect(source).toContain(String.raw`\"playmode_transition_requested\"`);
    expect(source).toContain('var transitionRequested = playing');
    expect(source).toContain('PlayModeTransitionTracker.Begin(');
    expect(source).toContain('? !EditorApplication.isPlayingOrWillChangePlaymode');
    expect(source).toContain('? EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode');
    expect(source).toContain('const string PlayModeStatusSchemaJson');
    expect(source).toContain(String.raw`"_playmode_transition_expectation_id"`);
    expect(source).toContain("EditorEditModeEligibility");
    expect(source).toContain("CaptureEditModeEligibility()");
    expect(source).toContain(String.raw`\"edit_mode_eligible\"`);
  });

  it("adds an explicit ask-gated playmode set command with a strict state schema", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");

    expect(source).toContain('const string SetPlayModeSchemaJson');
    expect(source).toContain(String.raw`\"enum\":[\"playing\",\"editing\"]`);
    expect(source).toContain(String.raw`\"required\":[\"state\"]`);
    expect(source).toContain('RequiresEditorStatus = new string[] { "editing", "playing", "playing_paused" }');
    expect(source).toContain('Argument state is required');
    expect(source).toContain('state must be playing or editing');
    expect(source).toContain('return RequestPlayMode(context, true);');
    expect(source).toContain('return RequestPlayMode(context, false);');
  });

  it("adds a safe Unity Editor quit command with dry-run and dirty-scene preflight", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");
    const quitSource = source.slice(source.indexOf('"editor.quit"'), source.indexOf("static CommandExecutionResult RequestPlayMode"));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult EditorQuitBlockedResult"), source.indexOf("static string StatusJson"));

    expect(source).toContain('const string EditorQuitSchemaJson');
    expect(source).toContain(String.raw`\"dry_run\":{\"type\":\"boolean\",\"default\":true}`);
    expect(source).toContain(String.raw`\"confirm_close\":{\"type\":\"boolean\",\"default\":false}`);
    expect(quitSource).toContain('Risk = "destructive"');
    expect(quitSource).toContain('Idempotency = "unsafe"');
    expect(quitSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(quitSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "confirm_close", false)');
    expect(quitSource).toContain('context.State?.UpdateFromEditor()');
    expect(quitSource).toContain('EditorQuitLifecycle.Begin(operationId, callerArgsHash, waitTimeoutMs)');
    expect(quitSource).toContain('EditorQuitLifecycle.ScheduleEditorExit()');
    expect(quitSource).toContain('editor.quit requires Gateway-reserved lifecycle correlation');
    expect(quitSource).toContain('"deferred"');
    expect(helperSource).toContain('Application.isBatchMode');
    expect(helperSource).toContain('EditorApplication.isCompiling');
    expect(helperSource).toContain('EditorApplication.isUpdating');
    expect(helperSource).toContain('EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode');
    expect(helperSource).toContain('BuiltInCommands.RequireNoUnsavedSceneChanges(context.State, "editor.quit", "editor.quit")');
    expect(helperSource).toContain('!dryRun && !confirmClose');
    expect(helperSource).toContain('"confirmation_required"');
    const lifecycleSource = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "EditorQuitLifecycle.cs"), "utf8");
    expect(lifecycleSource).toContain('EditorApplication.wantsToQuit += WantsToQuit');
    expect(lifecycleSource).toContain('EditorApplication.update -= RequestEditorExit');
    expect(lifecycleSource).toContain('EditorApplication.update += RequestEditorExit');
    expect(lifecycleSource).toContain('EditorApplication.Exit(0)');
    expect(lifecycleSource).toContain('EditorApplication.quitting += MarkAccepted');
    expect(lifecycleSource).toContain('EditorApplication.update += FinalizeReturnedExit');
    expect(lifecycleSource).toContain('ExitReturnGraceSeconds = 1.0');
    expect(lifecycleSource).toContain('SessionState.SetBool(ExitDispatchedKey, true)');
    expect(lifecycleSource).toContain('snapshot.State == "deferred"');
    expect(lifecycleSource).toContain('DateTime.UtcNow > acceptanceDeadlineUtc');
    expect(lifecycleSource).toContain('SetState("uncertain", true)');
    expect(lifecycleSource).toContain('SessionState.GetBool(WantsToQuitObservedKey, s_wantsToQuitObserved) ? "cancelled" : "ignored"');
    expect(lifecycleSource).toContain('SetState("accepted", false)');
    expect(lifecycleSource).toContain('if (string.IsNullOrEmpty(snapshot.OperationId)');
    expect(lifecycleSource).toContain('state == "deferred" || state == "requesting"');
    expect(lifecycleSource).toContain('SetState("uncertain", false)');
    expect(lifecycleSource).not.toContain('EditorApplication.Exit(0);\n                EditorApplication.Exit(0);');
  });

  it("uses Unity EditorApplication state and does not implement code execution", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");

    expect(source).toContain("EditorApplication.isCompiling");
    expect(source).toContain("EditorApplication.isPlaying");
    expect(source).toContain("EditorApplication.isPaused");
    expect(source).toContain("Application.unityVersion");
    expect(source).not.toContain("RoslynCompiler");
  });

  it("binds only this quit handler's preflight refusals before any lifecycle or exit scheduling", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");
    const quitSource = source.slice(source.indexOf("public static CommandExecutionResult EditorQuit("), source.indexOf("static CommandExecutionResult RequestPlayMode"));
    const preflightSource = source.slice(source.indexOf("static CommandExecutionResult EditorQuitBlockedResult"), source.indexOf("static string EditorQuitJson"));
    const proofSource = source.slice(source.indexOf("internal static string EditorQuitPreflightRefusalDetailsJson"), source.indexOf("static string StatusJson"));

    expect(quitSource.indexOf("EditorQuitBlockedResult(context, dryRun, confirmClose)")).toBeLessThan(quitSource.indexOf("EditorQuitLifecycle.Begin("));
    expect(quitSource.indexOf("return blocked;")).toBeLessThan(quitSource.indexOf("EditorQuitLifecycle.ScheduleEditorExit()"));
    expect(preflightSource).toContain("CommandExecutionResult.Error(unsavedResult.ErrorCode, unsavedResult.ErrorMessage, EditorQuitPreflightRefusalDetailsJson(context, unsavedResult.ErrorDetailsJson))");
    expect(preflightSource).not.toMatch(/EditorQuitLifecycle\.(Begin|ScheduleEditorExit)|EditorApplication\.Exit|isPlaying\s*=/);
    expect(source).toContain("return EditorQuitPreflightRefusalDetailsJson(context, details)");
    expect(proofSource).toContain("sgg.unity_mcp.editor_quit_preflight_refusal.v1");
    expect(proofSource).toContain('JsonUtil.ExtractString(argsJson, "_editor_quit_operation_id")');
    expect(proofSource).toContain('JsonUtil.ExtractString(argsJson, "_editor_quit_caller_args_hash")');
    for (const field of ["command", "operation_id", "caller_args_hash", "reason", "phase", "scope", "binding_complete", "owner", "project", "instance_id", "canonical_project_root", "unity_pid", "unity_process_start_utc"]) {
      expect(proofSource).toContain(`\\"${field}\\"`);
    }
    for (const field of ["close_requested", "quit_lifecycle_started", "exit_scheduled", "editor_exit_invoked", "automatic_replay_attempted", "automatic_process_termination_attempted", "retained_native_custody_observed", "retained_native_close_supported"]) {
      expect(proofSource).toContain(`\\"${field}\\":false`);
    }
    expect(proofSource).toContain('JsonUtil.ExtractString(detailsJson, "command") != "editor.quit"');
    expect(proofSource).toContain('reason != "unsaved_scene_changes" && reason != "confirm_close_required"');
    expect(proofSource).toContain('JsonUtil.HasProperty(detailsJson, "editor_quit_preflight_refusal")');
    expect(proofSource).toContain('JsonUtil.HasProperty(detailsJson, "close_requested")');
    expect(proofSource).toContain("operationId.Length != 29");
    expect(proofSource).toContain("callerArgsHash.Length != 71");
    expect(proofSource).toContain("startedUtc.Kind != DateTimeKind.Utc");
    expect(proofSource).toContain("processId > 0 && processStartUtc.Length > 0");
    expect(proofSource).not.toMatch(/GetCurrentProcess|ProjectPaths|CanonicalPathService|EditorQuitLifecycle|EditorApplication|SessionState/);
  });

  it("keeps quit refusal recovery observational and defers unobserved native custody to its owner", () => {
    const source = fs.readFileSync(editorStatusCommandsPath, "utf8");
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult EditorQuitBlockedResult"), source.indexOf("static string StatusJson"));

    expect(helperSource).toContain("This command does not observe retained native custody; consult the owning package or operator before any lifecycle transition.");
    expect(helperSource).toContain(String.raw`\"safe_recovery_commands\":[\"editor.status\",\"compile.status\",\"editor.save_prompt.get\"]`);
    expect(helperSource).toContain("do_not_exit_playmode_automatically");
    expect(helperSource).toContain("do_not_kill_unity_automatically");
    expect(helperSource).toContain("do_not_replay_editor.quit_automatically");
    expect(helperSource).not.toContain("editor.playmode.exit");
    expect(helperSource).not.toMatch(/\.Kill\(|\.Start\(|SaveScene\(|RequestScriptCompilation\(|AssetDatabase\.Refresh\(|SessionState\./);
    expect(helperSource).toContain("System.Diagnostics.Process.GetCurrentProcess()");
    expect(helperSource).toContain("process.StartTime.ToUniversalTime()");
    expect(helperSource).toContain("NormalizeProjectRoot(ProjectPaths.ProjectRoot).Value");
  });

  it("ships a pure Unity refusal fixture without invoking live quit or owner identity collection", () => {
    const testPath = path.join(packageRoot, "Tests", "Editor", "McpPr246EditorQuitPreflightRefusalTests.cs");
    const source = fs.readFileSync(testPath, "utf8");
    const meta = fs.readFileSync(`${testPath}.meta`, "utf8");

    expect(source).toContain("class McpPr246EditorQuitPreflightRefusalTests");
    expect(source).toContain("EditorStatusCommands.EditorQuitPreflightRefusalDetailsJson(");
    expect(source).toContain('"unsaved_scene_changes"');
    expect(source).toContain("IncompleteOwnerNeverClaimsABindableRefusal");
    expect(source).not.toMatch(/GetCurrentProcess|EditorApplication|SceneManager|SessionState|EditorQuitLifecycle|\.EditorQuit\(/);
    expect(meta).toMatch(/^fileFormatVersion: 2\nguid: [0-9a-f]{32}\n$/);
  });
});
