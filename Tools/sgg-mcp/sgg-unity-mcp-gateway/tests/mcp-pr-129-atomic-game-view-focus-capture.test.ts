import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

import { isCommandFeatureEnabled } from "../src/mcp/feature-flags.js";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandsPath = path.join(packageRoot, "Editor", "Commands", "EditorScreenshotCommands.cs");
const policyManagerPath = path.join(packageRoot, "Editor", "Setup", "GatewayPolicyManager.cs");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const cliPath = path.join(process.cwd(), "src", "cli.ts");

describe("MCP-PR-129 atomic Game View focus/capture", () => {
  it("declares a separate disabled feature and ask-gated unsafe command", () => {
    const commands = read(commandsPath);
    const gateway = JSON.parse(read(gatewayExamplePath));
    const policy = JSON.parse(read(policyExamplePath));
    const policyManager = read(policyManagerPath);
    const cli = read(cliPath);

    expect(commands).toContain('const string GameViewFocusCaptureFeatureFlag = "game_view_focus_capture"');
    expect(commands).toContain('"game.view.focus_capture"');
    expect(commands).toContain('Risk = "write"');
    expect(commands).toContain('Idempotency = "unsafe"');
    expect(commands).toContain("FeatureFlag = GameViewFocusCaptureFeatureFlag");
    expect(gateway.features.game_view_focus_capture).toBe(false);
    expect(policy.projects.game.ask).toContain("game.view.focus_capture");
    expect(policyManager).toContain('"game_view_focus_capture"');
    expect(cli).toContain('"game.view.focus_capture": "game_view_focus_capture"');
  });

  it("binds a short-lived single-use dry-run plan to exact runtime and capture identity", () => {
    const commands = read(commandsPath);
    const command = focusCaptureCommand(commands);

    expect(commands).toContain("const int MaxGameViewFocusCapturePlans = 32");
    expect(commands).toContain("const int GameViewFocusCapturePlanExpirySeconds = 60");
    expect(commands).toContain('"plan_id"');
    expect(commands).toContain('"plan_hash"');
    expect(command).toContain("context.State.InstanceId");
    expect(command).toContain("context.State.CommandsHash");
    expect(command).toContain("context.State.CatalogGeneration");
    expect(command).toContain("EditorWindowSelector.RuntimeIdentity(window)");
    expect(command).toContain("CaptureArgsHash = captureArgsHash");
    expect(command).toContain("plan.PlanHash = GameViewFocusCapturePlanHash(plan)");
    expect(command).toContain('"single_use\\":true');
    expect(command).toContain('"precondition_not_authorization\\":true');
    expect(command).toContain('"gateway_approval_required\\":true');
    expect(command).toContain('"artifact_produced\\":false');
  });

  it("enables only the required discovery and capture lifecycle dependencies", () => {
    const features = { game_view_focus_capture: true };
    expect(isCommandFeatureEnabled({ id: "game.view.focus_capture", feature_flag: "game_view_focus_capture" }, features)).toBe(true);
    expect(isCommandFeatureEnabled({ id: "editor.windows.list", feature_flag: "editor_utilities" }, features)).toBe(true);
    expect(isCommandFeatureEnabled({ id: "game.view.screenshot.status", feature_flag: "editor_screenshots" }, features)).toBe(true);
    expect(isCommandFeatureEnabled({ id: "game.view.screenshot.cancel", feature_flag: "editor_screenshots" }, features)).toBe(true);
    expect(isCommandFeatureEnabled({ id: "editor.window.inspect", feature_flag: "editor_utilities" }, features)).toBe(false);
    expect(isCommandFeatureEnabled({ id: "game.view.screenshot", feature_flag: "editor_screenshots" }, features)).toBe(false);
  });

  it("consumes the exact plan before managed focus and schedules only after synchronous verification", () => {
    const commands = read(commandsPath);
    const command = focusCaptureCommand(commands);
    const consume = command.indexOf("s_gameViewFocusCapturePlans.Remove(planId)");
    const focus = command.indexOf("executionWindow.Focus()");
    const verify = command.indexOf("var focusVerified = Application.isFocused");
    const schedule = command.indexOf("ScheduleGameViewOverlayCapture(captureArgsJson");

    expect(consume).toBeGreaterThan(-1);
    expect(consume).toBeLessThan(focus);
    expect(focus).toBeLessThan(verify);
    expect(verify).toBeLessThan(schedule);
    expect(command).toContain("ReferenceEquals(EditorWindow.focusedWindow, executionWindow)");
    expect(command).toContain("consumedPlan.BridgeInstanceId");
    expect(command).toContain("consumedPlan.CommandsHash");
    expect(command).toContain("consumedPlan.CatalogGeneration");
    expect(command).toContain("consumedPlan.CaptureArgsHash");
    expect(command.match(/ScheduleGameViewOverlayCapture\(captureArgsJson/g)).toHaveLength(1);
    expect(command).toContain('"capture_precondition_failed"');
    expect(command).toContain('"replay_supported\\":false');
  });

  it("never opens or switches views and has no native or synthetic focus fallback", () => {
    const commands = read(commandsPath);
    const command = focusCaptureCommand(commands);
    const screenshot = commands.slice(
      commands.indexOf("public static CommandExecutionResult GameViewScreenshot"),
      commands.indexOf('[McpCommand(\n            "game.view.focus"'),
    );

    expect(command).toContain('window.GetType().FullName, "UnityEditor.GameView"');
    expect(command).toContain('"native_focus_api_used\\":false');
    expect(command).toContain('"synthetic_input_used\\":false');
    expect(command).toContain('"view_opened\\":false');
    expect(command).toContain('"view_switched\\":false');
    expect(command).toContain('"fallback_attempted\\":false');
    expect(command).not.toContain("SetForegroundWindow");
    expect(command).not.toContain("SendKeys");
    expect(command).not.toContain("GetWindow<");
    expect(command).not.toContain("SetViewType");
    expect(screenshot).not.toContain("Focus()");
  });

  it("retains focus-plan evidence in asynchronous capture status", () => {
    const commands = read(commandsPath);

    expect(commands).toContain("AutomaticFocusAttempted = focusExecution != null");
    expect(commands).toContain("FocusVerified = focusExecution != null");
    expect(commands).toContain('"focus_capture_plan_id\\":');
    expect(commands).toContain('"focus_capture_plan_hash\\":');
    expect(commands).toContain('"focus_window_runtime_identity\\":');
    expect(commands).toContain('"focus_windows_hash_before\\":');
    expect(commands).toContain('"focus_windows_hash_after\\":');
  });
});

function focusCaptureCommand(source: string): string {
  return source.slice(
    source.indexOf("public static CommandExecutionResult GameViewFocusCapture"),
    source.indexOf('[McpCommand(\n            "game.view.screenshot.status"'),
  );
}

function read(filePath: string): string {
  return fs.readFileSync(filePath, "utf8");
}
