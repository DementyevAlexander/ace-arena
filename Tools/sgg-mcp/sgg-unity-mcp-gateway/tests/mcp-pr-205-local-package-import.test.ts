import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const packageRoot = path.join(repositoryRoot, "unity", "Packages", "com.sungeargames.unity-mcp");

function readPackageFile(relativePath: string): string {
  return fs.readFileSync(path.join(packageRoot, ...relativePath.split("/")), "utf8");
}

describe("MCP-PR-205 local package script convergence", () => {
  it("keeps generic asset.import Assets-only and exposes a narrow package script command", () => {
    const builtIn = readPackageFile("Editor/Commands/BuiltInCommands.cs");
    const command = readPackageFile("Editor/Commands/LocalPackageScriptImportCommands.cs");

    expect(builtIn).toContain('asset_path must be under Assets/');
    expect(command).toContain('"package.local_script.import"');
    expect(command).toContain('packagePath.EndsWith(".cs"');
    expect(command).toContain("PackageSource.Local.ToString()");
    expect(command).toContain("PackageSource.Embedded.ToString()");
    expect(command).toContain("PathGuard.RequireUnityAssetPath");
    expect(command).toContain("HasReparsePoint(root, fullPath)");
    expect(command).toContain("MaxScriptBytes");
    expect(command).toContain('JsonUtil.ExtractString(context.ArgsJson, "precondition_sha256")');
    expect(command).toContain("ImportAssetOptions.ForceSynchronousImport | ImportAssetOptions.ForceUpdate");
    expect(command).toContain("CompilationPipeline.RequestScriptCompilation()");
    expect(command).toContain('UnityBridgeRuntime.MarkPackageRefreshObserved("package.local_script.import")');
    expect(command).toContain('"manifest_mutated\\\":false');
    expect(command).toContain('"packages_lock_mutated\\\":false');
    expect(command).not.toContain("Client.Add(");
    expect(command).not.toContain("Client.Resolve(");
    expect(command).not.toContain("File.WriteAll");
  });

  it("compares discovered source paths with compiler-included paths rather than counts alone", () => {
    const inventory = readPackageFile("Editor/Bridge/LocalPackageCodeInventoryInspector.cs");
    const monitor = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const state = readPackageFile("Editor/Bridge/BridgeRuntimeState.cs");
    const protocol = fs.readFileSync(path.join(repositoryRoot, "src", "bridge", "bridge-protocol.ts"), "utf8");
    const dispatcher = fs.readFileSync(path.join(repositoryRoot, "src", "mcp", "dispatcher.ts"), "utf8");
    const operationStore = fs.readFileSync(path.join(repositoryRoot, "src", "operations", "test-run-operation-store.ts"), "utf8");

    expect(inventory).toContain("CompilationPipeline.GetAssemblies()");
    expect(inventory).toContain("assemblies[assemblyIndex].sourceFiles");
    expect(inventory).toContain("CompilationPipeline.GetAssemblyNameFromScriptPath(assetPath)");
    expect(inventory).toContain("activeAssemblyNames.Contains(assemblyName)");
    expect(inventory).toContain("Difference(sortedExpectedCompilerPaths, compilerPaths, sourcePaths)");
    expect(inventory).toContain("Difference(sortedCompilerPaths, sourcePaths, null)");
    expect(inventory).toContain("missingCompilerPaths.Count");
    expect(inventory).toContain("unexpectedCompilerPaths.Count");
    expect(inventory).toContain("missingCompilerSourceFileCount > 0 || unexpectedCompilerSourceFileCount > 0");
    expect(monitor).toContain("TryCaptureCompilationSourcePaths(m_cachedLocalInputs");
    expect(monitor).toContain("request.CompilationSourcePaths");
    expect(monitor).toContain('local_package_compiler_sources:');
    expect(state).toContain('"missing_compiler_source_file_count\\\":"');
    expect(state).toContain('"compiler_source_fingerprint\\\":"');
    expect(state).toContain("LocalPackageCodeInventoryJson(compact, !bounded)");
    expect(state).toContain("LocalPackageCodeInventoryJson(true, false)");
    expect(protocol).toContain("missing_compiler_source_file_count:");
    expect(protocol).toContain('"compiler_source_read_exception"');
    expect(dispatcher).toContain("missing_compiler_source_file_count: inventory.missing_compiler_source_file_count");
    expect(operationStore).toContain('"missing_compiler_source_file_count"');
    expect(operationStore).toContain('"compiler_source_limit_exceeded"');
  });
});
