import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandsPath = path.join(packageRoot, "Editor", "Commands", "EditorScreenshotCommands.cs");
const contextPath = path.join(packageRoot, "Editor", "Commands", "GameViewCaptureEditorContext.cs");
const policyPath = path.join(packageRoot, "Editor", "Commands", "GameViewCapturePolicy.cs");

describe("MCP-PR-107 Game View resolution and focus recovery", () => {
  it.each([
    "problem_20260718T091236_7f3f39fe880c",
    "problem_20260718T135832_efb6c31165c2",
    "problem_20260722T160424_bcab32fa5560",
    "problem_20260722T182231_2ad43bad1f31",
    "problem_20260722T194701_8d3d31356f81",
    "problem_20260722T212145_119585762d9b",
    "problem_20260729T191014_5c80128ecdeb",
  ])("covers indexed report %s with the shared bounded contract", (reportId) => {
    expect(reportId).toMatch(/^problem_[0-9]{8}T[0-9]{6}_[0-9a-f]{12}$/);
    expect(read(commandsPath)).toContain("GameViewCapturePolicy.ResolutionChangeRequired");
  });

  it("retains bounded requested dimensions and uses the public Unity 6 resolution API", () => {
    const commands = read(commandsPath);
    const context = read(contextPath);

    expect(commands).toContain('\\"width\\":{\\"type\\":\\"integer\\",\\"minimum\\":64,\\"maximum\\":4096}');
    expect(commands).toContain('\\"height\\":{\\"type\\":\\"integer\\",\\"minimum\\":64,\\"maximum\\":4096}');
    expect(context).toContain("PlayModeWindow.SetCustomRenderingResolution((uint)width, (uint)height, TemporaryResolutionName)");
    expect(context).toContain("internal static bool TryGetRenderingResolution(out int width, out int height)");
    expect(context).toContain('TemporaryResolutionName = "SGG Unity MCP Capture"');
    expect(context).toContain("#if UNITY_6000_0_OR_NEWER");
    expect(context).toContain('failureReason = "custom_resolution_api_unavailable"');
  });

  it("waits for exact Game View and Screen convergence before beginning capture", () => {
    const commands = read(commandsPath);
    const apply = commands.slice(commands.indexOf("static void ApplyRequestedGameViewResolution"), commands.indexOf("static void OnGameViewCaptureProgress"));
    const poll = commands.slice(commands.indexOf("static void PollGameViewScreenCaptures"), commands.indexOf("static bool IsActiveGameViewCaptureStatus"));

    expect(commands).toContain('Status = dryRun ? "dry_run" : resolutionChangeRequired ? "resizing" : "scheduled"');
    expect(commands).toContain('return status == "resizing" || status == "scheduled" || status == "settling" || status == "submitted"');
    expect(apply).toContain("TryStartGameViewCaptureAfterResolutionSettled");
    expect(apply.indexOf("ResolutionMatches")).toBeLessThan(apply.indexOf('capture.Status = "scheduled"'));
    expect(apply.indexOf('capture.Status = "scheduled"')).toBeLessThan(apply.indexOf("BeginGameViewCapture(capture)"));
    expect(poll).toContain('capture.Status == "resizing"');
    expect(poll).toContain("ResolutionTransitionContextStillValid");
  });

  it("restores the original rendering dimensions before artifact commit and on every terminal path", () => {
    const commands = read(commandsPath);
    const completion = commands.slice(commands.indexOf("if (decision.ReportCompleted)"), commands.indexOf("static void EnsureGameViewScreenCapturePollHook"));
    const terminal = commands.slice(commands.indexOf("static void MarkGameViewCaptureTerminalLocked"), commands.indexOf("static void ApplyContextFailureLocked"));
    const recovery = commands.slice(commands.indexOf("static void RecoverPersistedGameViewCapture"), commands.indexOf("static void InterruptGameViewCaptureForReload"));
    const cleanup = commands.slice(commands.indexOf("static bool CleanupGameViewCaptureStaging"), commands.indexOf("static string Sha256Hex"));

    expect(completion.indexOf("TryRestoreGameViewResolutionLocked(capture)")).toBeLessThan(completion.indexOf("CommitPublication(publication"));
    expect(completion).toContain("RollbackPublication(publication");
    expect(terminal).toContain("TryRestoreGameViewResolutionLocked(capture)");
    expect(recovery).toContain("capture.ResolutionRestoreRequired && !capture.ResolutionRestoreVerified");
    expect(recovery).toContain("CleanupGameViewCaptureStaging(capture);");
    expect(commands).toContain('"capture_resolution_restore_failed"');
    expect(commands).toContain('\\"restore_verified\\":');
    expect(commands).toContain('\\"restore_screen_converged\\":');
    expect(terminal).toContain("TryGetRenderingResolution");
    expect(terminal.indexOf("TryInspect(out var selectedView)")).toBeLessThan(terminal.indexOf("TrySetCustomRenderingResolution"));
    expect(recovery).toContain("RestoreOrRetainDiscardedPersistedCaptureLocked");
    expect(recovery).toContain('capture.ErrorCode = "capture_resolution_restore_pending"');
    expect(cleanup).toContain("GameViewCapturePolicy.IsConfinedOutputPath(ProjectPaths.ProjectRoot, capture.FullPath)");
    expect(cleanup).toContain("GameViewCapturePolicy.IsConfinedOutputPath(ProjectPaths.ProjectRoot, capture.StagingPath)");
    expect(read(policyPath)).toContain("ResolutionRestored");
  });

  it("provides an explicit exact Game View focus command without native or silent focus", () => {
    const commands = read(commandsPath);
    const focus = commands.slice(commands.indexOf("public static CommandExecutionResult GameViewFocus"), commands.indexOf('[McpCommand(\n            "game.view.screenshot.status"'));
    const screenshot = commands.slice(commands.indexOf("public static CommandExecutionResult GameViewScreenshot"), commands.indexOf('[McpCommand(\n            "game.view.focus"'));

    expect(commands).toContain('"game.view.focus"');
    expect(commands).toContain('"windows_hash"');
    expect(commands).toContain('"confirm_focus"');
    expect(focus).toContain("EditorWindowSelector.Select");
    expect(focus).toContain('window.GetType().FullName, "UnityEditor.GameView"');
    expect(focus).toContain("window.Focus()");
    expect(focus).toContain("Application.isFocused");
    expect(focus).toContain("confirmation_required");
    expect(focus).toContain("native_focus_api_used\\\":false");
    expect(screenshot).not.toContain("window.Focus()");
    expect(commands).not.toContain("SetForegroundWindow");
  });

  it("publishes typed preflight, apply, restoration, and no-fallback evidence", () => {
    const commands = read(commandsPath);

    expect(commands).toContain('"capture_resolution_change_unsupported"');
    expect(commands).toContain('"capture_resolution_change_failed"');
    expect(commands).toContain('"capture_resolution_settle_interrupted"');
    expect(commands).toContain('\\"resolution_change\\":');
    expect(commands).toContain('\\"fallback_attempted\\":false');
    expect(commands).toContain('\\"automatic_focus_attempted\\":false');
    expect(commands).toContain('\\"next_valid_actions\\":[\\"editor.windows.list\\",\\"game.view.focus\\"');
  });
});

function read(filePath: string): string {
  return fs.readFileSync(filePath, "utf8");
}
