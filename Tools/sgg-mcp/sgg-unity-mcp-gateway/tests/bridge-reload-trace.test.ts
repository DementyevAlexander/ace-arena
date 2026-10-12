import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");
const bridgeRoot = path.join(packageRoot, "Editor", "Bridge");
const commandsRoot = path.join(packageRoot, "Editor", "Commands");
const unityPackageManifest = JSON.parse(fs.readFileSync(path.join(packageRoot, "package.json"), "utf8")) as { version: string };

describe("bridge reload trace diagnostics", () => {
  it("writes a bounded project-local reload trace", () => {
    const source = fs.readFileSync(path.join(bridgeRoot, "BridgeReloadTrace.cs"), "utf8");
    const meta = fs.readFileSync(path.join(bridgeRoot, "BridgeReloadTrace.cs.meta"), "utf8");

    expect(source).toContain("internal static class BridgeReloadTrace");
    expect(source).toContain('public static string RelativeTracePath => "Library/SGGUnityMcp/bridge-reload-trace.log"');
    expect(source).toContain("const long MaxTraceBytes = 262144");
    expect(source).toContain("TrimIfNeeded(path)");
    expect(source).toContain("Reload tracing must never affect Unity startup or command execution");
    expect(source).toContain("ReadRecentJson(int maxLines, int maxChars)");
    expect(source).toContain("FileShare.ReadWrite | FileShare.Delete");
    expect(source).toContain('AppendField(builder, "package_version", UnityBridgeRuntime.PackageVersion)');
    expect(source).toContain('AppendField(builder, "process_start_utc", ProcessStartUtc)');
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("instruments startup, command catalog, package enumeration, package changes, and reload lifecycle", () => {
    const runtime = fs.readFileSync(path.join(bridgeRoot, "UnityBridgeRuntime.cs"), "utf8");
    const observer = fs.readFileSync(path.join(bridgeRoot, "BridgeBootstrapLifecycleObserver.cs"), "utf8");
    const registry = fs.readFileSync(path.join(commandsRoot, "CommandRegistry.cs"), "utf8");
    const scanner = fs.readFileSync(path.join(commandsRoot, "CommandManifestScanner.cs"), "utf8");
    const monitor = fs.readFileSync(path.join(bridgeRoot, "PackageUpdateMonitor.cs"), "utf8");

    expect(runtime).toContain(`public const string PackageVersion = "${unityPackageManifest.version}"`);
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.bootstrap.start"');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.reload.after")');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.bootstrap.end")');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.startup.deferred"');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.startup.waiting_for_idle"');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.startup.resume"');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.startup.start"');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.registry.build.start")');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.heartbeat.write.start")');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.heartbeat.waiting_for_idle.write"');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.reload.before")');
    expect(runtime).toContain('BridgeReloadTrace.Log("bridge.server.dispose.start"');
    expect(runtime).toContain('BridgeReloadTrace.LogError("bridge.bootstrap.error", exception)');
    expect(runtime).toContain('BridgeReloadTrace.LogError("bridge.startup.error", exception)');
    expect(observer).toContain("[DidReloadScripts(-1000)]");
    expect(observer).toContain('"bridge.bootstrap.did_reload_scripts.runtime_initializer_not_observed"');
    expect(observer).toContain('"bridge.bootstrap.runtime_initializer_observed"');
    expect(observer).toContain('"bridge.bootstrap.editor_update_observed"');
    expect(observer).toContain('"bridge.bootstrap.idle_gate." + state');
    expect(observer).toContain('"bridge.bootstrap.first_heartbeat_failed."');
    expect(observer).not.toContain("exception.Message");
    expect(observer).not.toContain("exception.ToString");
    expect(registry).toContain('BridgeReloadTrace.Log("command_registry.rebuild.start")');
    expect(registry).toContain('BridgeReloadTrace.Log("command_registry.attrib_scan.start")');
    expect(registry).toContain("const int MaxAssemblyTimingRows = 40");
    expect(registry).toContain('BridgeReloadTrace.Log("command_registry.attrib_scan.assembly"');
    expect(registry).toContain('"assembly=" + timing.AssemblyName');
    expect(registry).toContain('";type_count=" + timing.TypeCount');
    expect(registry).toContain('";attributed_method_count=" + timing.AttributedMethodCount');
    expect(registry).toContain('";status=" + timing.Status');
    expect(registry).toContain("selection=commands_errors_then_slowest");
    expect(registry).toContain('BridgeReloadTrace.Log("command_registry.manifest_scan.start")');
    expect(scanner).toContain('BridgeReloadTrace.Log("command_manifest.get_all_registered_packages.start")');
    expect(scanner).toContain('BridgeReloadTrace.LogError("command_manifest.get_all_registered_packages.error", exception)');
    expect(monitor).toContain('BridgeReloadTrace.LogFingerprints("package_update_monitor.fingerprint.changed"');
    expect(monitor).toContain('BridgeReloadTrace.Log("package_update_monitor.update.end", status');
    expect(monitor).toContain('BridgeReloadTrace.Log("package_update_monitor.manual_refresh.observed"');
    expect(monitor).not.toContain("package_update_monitor.asset_database_refresh");
  });

  it("traces compile waits, validation, and test response writes without persisting bodies or raw arguments", () => {
    const server = fs.readFileSync(path.join(bridgeRoot, "BridgeServer.cs"), "utf8");

    expect(server).toContain('request.Command == "compile.wait_ready" || request.Command == "compile.refresh_and_wait" || request.Command == "validation.after_edit" || request.Command == "test.run"');
    expect(server).toContain('"bridge.invoke.response.write.start"');
    expect(server).toContain('"bridge.invoke.response.write.end"');
    expect(server).toContain('"bridge.invoke.response.write.error"');
    expect(server).toContain('return "command=" + command');
    expect(server).toContain('command == "compile.refresh_and_wait" ? string.IsNullOrEmpty(operationId) ? "unavailable" : "present" : "not_applicable"');
    expect(server).toContain('";response_bytes="');
    expect(server).not.toContain('";response_json="');
    expect(server).not.toContain('";args_json="');
    expect(server).not.toContain('";test_filter="');
    expect(server).not.toContain('";run_id="');
  });

  it("exposes a bounded read-only diagnostics command", () => {
    const builtIn = fs.readFileSync(path.join(commandsRoot, "BuiltInCommands.cs"), "utf8");
    const docs = fs.readFileSync(path.join(root, "docs", "en", "reference", "commands.md"), "utf8");
    const docsRu = fs.readFileSync(path.join(root, "docs", "ru", "reference", "commands.md"), "utf8");

    expect(builtIn).toContain('"diagnostics.bridge_reload_trace"');
    expect(builtIn).toContain('Risk = "read"');
    expect(builtIn).toContain('Idempotency = "safe"');
    expect(builtIn).toContain("BridgeReloadTrace.ReadRecentJson(maxLines, maxChars)");
    expect(builtIn).toContain('const string BridgeReloadTraceOutputLimitJson = "{\\"max_lines\\":500,\\"max_chars\\":262144}"');
    expect(docs).toContain("diagnostics.bridge_reload_trace args_json");
    expect(docs).toContain("Library/SGGUnityMcp/bridge-reload-trace.log");
    expect(docsRu).toContain("diagnostics.bridge_reload_trace args_json");
    expect(docsRu).toContain("Library/SGGUnityMcp/bridge-reload-trace.log");
  });
});
