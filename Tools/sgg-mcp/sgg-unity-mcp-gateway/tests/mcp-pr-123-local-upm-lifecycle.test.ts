import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");

describe("MCP-PR-123 local UPM lifecycle continuity", () => {
  it("compares bounded local package code inventory without resolving or refreshing packages", () => {
    const inventory = readPackageFile("Editor/Bridge/LocalPackageCodeInventoryInspector.cs");
    const monitor = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const state = readPackageFile("Editor/Bridge/BridgeRuntimeState.cs");

    expect(inventory).toContain("LocalPackageCodeInventoryInspector");
    expect(inventory).toContain("AssetDatabase.GetAllAssetPaths");
    expect(inventory).toContain("MaxLocalPackages = 64");
    expect(inventory).toContain("MaxSourceFiles = 16384");
    expect(inventory).toContain("MaxScanMilliseconds = 30000");
    expect(inventory).toContain("HasReparsePointInPath");
    expect(inventory).not.toContain("Client.Resolve");
    expect(inventory).not.toContain("AssetDatabase.Refresh");
    expect(inventory).not.toContain("RequestScriptCompilation");
    expect(monitor).toContain("ApplyLocalInventoryStatus");
    expect(monitor).toContain("PackageInfo.GetAllRegisteredPackages");
    expect(monitor).toContain("InspectCapturedInputs");
    expect(monitor).toContain("Task.Run(() => ComputeFingerprintSafely(request))");
    expect(monitor).toContain("stale_local_package_code_inventory");
    expect(state).toContain("local_package_code_inventory");
    expect(state).toContain("LocalPackageCodeInventoryStale");
    expect(state).toContain("LocalPackageCodeInventoryUnavailable");
  });

  it("publishes a process-bound waiting heartbeat before stable-idle bridge startup", () => {
    const runtime = readPackageFile("Editor/Bridge/UnityBridgeRuntime.cs");
    const observer = readPackageFile("Editor/Bridge/BridgeBootstrapLifecycleObserver.cs");
    const bootstrapUpdateStart = runtime.indexOf("static void BootstrapUpdate()");
    const bootstrapRuntimeStart = runtime.indexOf("static void BootstrapRuntime(string reason)", bootstrapUpdateStart);
    const bootstrapUpdate = runtime.slice(bootstrapUpdateStart, bootstrapRuntimeStart);

    expect(bootstrapUpdate).toContain('WriteBootstrapWaitingHeartbeat("bootstrap_idle_gate")');
    expect(bootstrapUpdate.indexOf('WriteBootstrapWaitingHeartbeat("bootstrap_idle_gate")'))
      .toBeLessThan(bootstrapUpdate.indexOf("if (EditorApplication.isCompiling || EditorApplication.isUpdating)"));
    expect(runtime).toContain("CommandsHash = BootstrapPendingCommandsHash");
    expect(runtime).toContain("State.MarkWaitingForIdle()");
    expect(runtime).toContain("MaxBootstrapInitializationAttempts = 3");
    expect(runtime).toContain("MaxBridgeStartupAttempts = 3");
    expect(runtime).toContain('EnsureBootstrapScheduled("bootstrap_failure_retry")');
    expect(runtime).toContain("ResetFailedBridgeStartup");
    expect(runtime).toContain("s_bridgeStartupAttempts < MaxBridgeStartupAttempts");
    expect(observer).toContain('UnityBridgeRuntime.EnsureBootstrapScheduled("did_reload_scripts")');
    expect(observer).toContain("bridge.bootstrap.waiting_heartbeat_published");
    expect(observer).toContain("bridge.bootstrap.heartbeat_retry_published");
  });

  it("returns typed no-replay inventory failures and validates their health shape", () => {
    const server = readPackageFile("Editor/Bridge/BridgeServer.cs");
    const protocol = fs.readFileSync(path.join(root, "src", "bridge", "bridge-protocol.ts"), "utf8");
    const results = fs.readFileSync(path.join(root, "src", "mcp", "results.ts"), "utf8");

    expect(server).toContain('"local_package_inventory_stale"');
    expect(server).toContain('"local_package_inventory_unavailable"');
    expect(server).toContain('\\"write_replay_allowed\\":false');
    expect(protocol).toContain("local_package_code_inventory: z.object");
    expect(protocol).toContain('status: z.enum(["current", "stale", "unavailable"])');
    expect(results).toContain('"local_package_inventory_stale"');
    expect(results).toContain('"local_package_inventory_unavailable"');
  });

  it("keeps validation actions fail-closed while local package inventory is untrusted", () => {
    const commands = readPackageFile("Editor/Commands/BuiltInCommands.cs");

    expect(commands).toContain("localPackageInventoryBlocked");
    expect(commands).toContain('Add("inspect_local_package_code_inventory")');
    expect(commands).toContain("!localPackageInventoryBlocked && !hasCompileErrors");
  });
});

function readPackageFile(relativePath: string): string {
  return fs.readFileSync(path.join(packageRoot, relativePath), "utf8");
}
