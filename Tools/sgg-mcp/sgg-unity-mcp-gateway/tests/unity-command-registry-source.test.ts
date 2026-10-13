import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const registryPath = path.join(packageRoot, "Editor", "Commands", "CommandRegistry.cs");
const scannerPath = path.join(packageRoot, "Editor", "Commands", "CommandManifestScanner.cs");
const attributePath = path.join(packageRoot, "Editor", "Commands", "McpCommandAttribute.cs");
const resultsPath = path.join(process.cwd(), "src", "mcp", "results.ts");

describe("Unity command registry source-contract", () => {
  it("keeps manifest commands discoverable when their handler is not loaded", () => {
    const source = fs.readFileSync(registryPath, "utf8");
    const results = fs.readFileSync(resultsPath, "utf8");

    expect(source).toContain("RegisterUnavailableManifestCommand(descriptor, handlerName)");
    expect(source).toContain('descriptor.ResultShape = "handler_unavailable"');
    expect(source).toContain('CommandExecutionResult.Error("command_handler_unavailable"');
    expect(source).not.toContain("manifest handler not found:");
    expect(results).toContain('"command_handler_unavailable"');
  });

  it("publishes package manifest paths as Unity-relative catalog metadata", () => {
    const source = fs.readFileSync(scannerPath, "utf8");

    expect(source).toContain('"Packages/" + package.name + "/Editor/Mcp/mcp.commands.json"');
    expect(source).toContain('"Packages/" + package.name + "/mcp.commands.json"');
    expect(source).toContain("ManifestPath = unityManifestPath");
    expect(source).not.toContain("ManifestPath = manifestPath");
  });

  it("reports manifest scan failures and project-local candidates without registering them", () => {
    const source = fs.readFileSync(scannerPath, "utf8");
    const bridgeServer = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "BridgeServer.cs"), "utf8");

    expect(source).toContain('registry.AddManifestDiagnostic("package_scan_failed"');
    expect(source).toContain('registry.AddManifestDiagnostic("manifest_read_failed"');
    expect(source).toContain('Path.Combine(Application.dataPath, "Scripts")');
    expect(source).toContain('registry.AddProjectManifestCandidate(ToAssetsRelativePath(files[index]), ProjectManifestCandidateReason)');
    expect(source).not.toContain("TryReadManifest(registry, string.Empty");
    expect(bridgeServer).toContain("manifest_diagnostics");
    expect(bridgeServer).toContain("project_manifest_candidates");
  });

  it("resolves trust from real package identity instead of self-declared metadata", () => {
    const registry = fs.readFileSync(registryPath, "utf8");
    const scanner = fs.readFileSync(scannerPath, "utf8");
    const attribute = fs.readFileSync(attributePath, "utf8");

    expect(attribute).toContain("public string Package { get; set; } = string.Empty");
    expect(registry).toContain("var builtIn = assembly == typeof(CommandRegistry).Assembly");
    expect(registry).toContain("var packageId = builtIn ? BuiltInPackageId : packageInfo?.name");
    expect(registry).toContain('packageId = "project:" + SafeAssemblyName(assembly)');
    expect(registry).toContain("var displayPackage = string.IsNullOrEmpty(attribute.Package) ? packageId : attribute.Package");
    expect(registry).toContain('Trust = builtIn ? "builtin" : "untrusted_package"');
    expect(scanner).toContain("var packageId = packageName");
    expect(scanner).toContain("var displayPackage = string.IsNullOrEmpty(manifest.package) ? packageName : manifest.package");
  });

  it("publishes wrapper waits through descriptor serialization and stale-binding hashes", () => {
    const registry = fs.readFileSync(registryPath, "utf8");
    const scanner = fs.readFileSync(scannerPath, "utf8");
    const attribute = fs.readFileSync(attributePath, "utf8");
    const descriptor = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "CommandDescriptor.cs"), "utf8");
    const bridgeServer = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "BridgeServer.cs"), "utf8");

    expect(attribute).toContain("public string WrapperWaitJson");
    expect(descriptor).toContain("public string WrapperWaitJson");
    expect(registry).toContain("WrapperWaitJson = attribute.WrapperWaitJson");
    expect(registry).toContain("AppendHashField(builder, descriptor.WrapperWaitJson)");
    expect(scanner).toContain('ExtractOptionalObject(rawCommandJson, "wrapper_wait")');
    expect(bridgeServer).toContain('AppendOptionalObject(builder, "wrapper_wait", descriptor.WrapperWaitJson)');
  });

  it("returns bounded timeout details for dynamic C# commands", () => {
    const registry = fs.readFileSync(registryPath, "utf8");
    const dispatcher = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "MainThreadDispatcher.cs"), "utf8");
    const progressDetector = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "CompileProgressWindowDetector.cs"), "utf8");

    expect(registry).toContain('CommandExecutionResult.Error("command_timeout", "Unity command timed out", CommandTimeoutDetailsJson(command.Descriptor, exception, m_state))');
    expect(registry).toContain('"dynamic_csharp_main_thread_execution"');
    expect(registry).toContain("timed_out_work_state");
    expect(registry).toContain("late_execution_possible");
    expect(registry).toContain("isEditorWindowsList");
    expect(registry).toContain("CompileProgressWindowDetector.StatusJson()");
    expect(registry).toContain("command_replayed");
    expect(dispatcher).toContain("workState.TryCancelBeforeStart()");
    expect(dispatcher).toContain('new MainThreadDispatchTimeoutException("cancelled_before_start", QueueDepth, false)');
    expect(dispatcher).toContain('new MainThreadDispatchTimeoutException("running", QueueDepth, true)');
    expect(dispatcher).toContain("completed.Set();\n                        workState.Complete();");
    expect(dispatcher).toContain("if (!completed.IsSet)");
    expect(dispatcher).toContain("static readonly LinkedList<Action> Pending");
    expect(dispatcher).toContain("Pending.Remove(pendingNode)");
    expect(progressDetector).toContain("const int ScanBudgetMs = 500");
    expect(progressDetector).toContain("const int MaxScannedWindows = 100");
    expect(progressDetector).toContain('catch (Exception exception)');
    expect(progressDetector).toContain('\\"scan_failed\\":true');
    expect(registry).toContain('\\"bounded_timeout_details\\"');
    expect(registry).toContain('\\"reduce_snippet_work\\"');
    expect(registry).toContain('\\"prefer_regular_catalog_command_or_job\\"');
  });
});
