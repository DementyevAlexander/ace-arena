import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "McpWindowPerfCommands.cs");
const windowPath = path.join(packageRoot, "Editor", "UI", "UnityMcpSetupWindow.cs");
const setupStatusPath = path.join(packageRoot, "Editor", "UI", "UnityMcpSetupWindow.SetupStatus.cs");
const permissionsCommandsPath = path.join(packageRoot, "Editor", "UI", "UnityMcpSetupWindow.PermissionsCommands.cs");
const permissionsCommandsRefreshPath = path.join(packageRoot, "Editor", "UI", "UnityMcpSetupWindow.PermissionsCommands.Refresh.cs");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");

describe("MCP Window perf commands", () => {
  it("declares bounded feature-gated command descriptors", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('"mcp_window.perf.status"');
    expect(source).toContain('"mcp_window.perf.capture"');
    expect(source).toContain('"mcp_window.scroll.benchmark"');
    expect(source).toContain('"mcp_window.perf.report"');
    expect(source).toContain('const string FeatureFlag = "sgg_mcp_diagnostics"');
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(4);
    expect(source.match(/Risk = "read"/g)).toHaveLength(2);
    expect(source.match(/Risk = "write"/g)).toHaveLength(2);
    expect(source.match(/Idempotency = "unsafe"/g)).toHaveLength(2);
    expect(source).toContain('"max_refresh_count\\":10');
    expect(source).toContain('"maximum\\":200');
    expect(source).toContain('"maximum\\":2000');
  });

  it("is dry-run-first and writes only project-local perf artifacts", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('"dry_run\\":{\\"type\\":\\"boolean\\",\\"default\\":true}');
    expect(source).toContain('"confirm_capture\\"');
    expect(source).toContain('"confirm_scroll\\"');
    expect(source).toContain('confirmation_required');
    expect(source).toContain('const string ArtifactRoot = "Library/SGGUnityMcp/job-artifacts/mcp-window-perf/"');
    expect(source).toContain('RequireProjectLocalFilePath(artifactPath, ArtifactRoot)');
    expect(source).toContain('HasReparsePointInExistingChain');
    expect(source).not.toContain("AppData");
    expect(source).not.toContain("problem-reports");
    expect(source).not.toContain("ScreenCapture");
    expect(source).not.toContain("Upload");
    expect(source).not.toContain("Process.Start");
  });

  it("uses MCP Window internal Stopwatch and synthetic scroll helpers", () => {
    const commandSource = fs.readFileSync(commandPath, "utf8");
    const windowSource = [windowPath, setupStatusPath, permissionsCommandsPath, permissionsCommandsRefreshPath].map((file) => fs.readFileSync(file, "utf8")).join("\n");

    expect(commandSource).toContain("mcp_window_internal_stopwatch");
    expect(commandSource).toContain("synthetic_scroll_offset_stopwatch");
    expect(commandSource).toContain("window.RefreshAllForCommand(probeHealth)");
    expect(commandSource).toContain("window.RefreshHealthForCommand()");
    expect(commandSource).toContain("window.RunScrollBenchmarkForCommand");
    expect(windowSource).toContain("ScrollView m_mainScrollView");
    expect(windowSource).toContain("internal static bool TryGetOpenWindow");
    expect(windowSource).toContain("internal string LatestTimingSummaryForCommand()");
    expect(windowSource).toContain("internal GatewayProcessManager.GatewayRuntimeStatus LastHealthProbeForCommand()");
    expect(commandSource).toContain('json.Append(",\\\"last_gateway_health_probed\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"last_gateway_health_ok\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"last_gateway_health_message\\\":")');
    expect(windowSource).toContain("internal bool HealthRefreshShortcutVisibleForCommand()");
    expect(windowSource).toContain("internal bool HeaderShortcutBarVisibleForCommand()");
    expect(commandSource).toContain('json.Append(",\\\"health_refresh_shortcut_visible\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"header_shortcut_bar_visible\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"ftue_tab_visible\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"ftue_complete\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"ftue_smoke_test_ready\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"ftue_client_config_ready\\\":")');
    expect(commandSource).toContain('json.Append(",\\\"ftue_permission_profile_ready\\\":")');
    expect(windowSource).toContain("internal McpWindowScrollBenchmarkResult RunScrollBenchmarkForCommand");
    expect(windowSource).toContain("SetMcpWindowTimingSummary(\"RefreshHealth\"");
    expect(windowSource).toContain("SetMcpWindowTimingSummary(\"ScrollBenchmark\"");
  });

  it("keeps example policy read commands allowed and UI-driving commands ask-gated", () => {
    const policy = JSON.parse(fs.readFileSync(policyExamplePath, "utf8"));
    const rules = policy.projects.game;

    expect(rules.allow).toContain("mcp_window.perf.status");
    expect(rules.allow).toContain("mcp_window.perf.report");
    expect(rules.ask).toContain("mcp_window.perf.capture");
    expect(rules.ask).toContain("mcp_window.scroll.benchmark");
  });
});
