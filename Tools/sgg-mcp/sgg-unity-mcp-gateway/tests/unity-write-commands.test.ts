import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const builtInCommandsPath = path.join(packageRoot, "Editor", "Commands", "BuiltInCommands.cs");
const assetSafeWriteCommandsPath = path.join(packageRoot, "Editor", "Commands", "AssetSafeWriteCommands.cs");
const pathGuardPath = path.join(packageRoot, "Editor", "Paths", "PathGuard.cs");
const canonicalPathServicePath = path.join(packageRoot, "Editor", "Paths", "CanonicalPathService.cs");

describe("Unity write command source-contract", () => {
  it("declares MVP write command IDs behind the command catalog", () => {
    const source = fs.readFileSync(builtInCommandsPath, "utf8");

    expect(source).toContain('"assets.refresh"');
    expect(source).toContain('"asset.import"');
    expect(source).toContain('"scene.open"');
    expect(source).toContain('"scene.save"');
  });

  it("guards refresh and scene open from Unity save prompts when scenes are dirty", () => {
    const source = fs.readFileSync(builtInCommandsPath, "utf8");

    expect(source).toContain("AssetsRefreshSchemaJson");
    expect(source).toContain('\\"allow_unsaved_scene_changes\\"');
    expect(source).toContain('RequireNoUnsavedSceneChanges(context.State, "assets.refresh", "assets.refresh")');
    expect(source).toContain('RequireNoUnsavedSceneChanges(context.State, "scene.open", "scene.open")');
    expect(source).toContain('"{\\"reason\\":\\"unsaved_scene_changes\\""');
    expect(source).toContain('editor.save_prompt.respond');
    expect(source).toContain('action');
    expect(source).toContain('cancel');
    expect(source).toContain('Do not use dont_save unless discarding changes is intended');
  });

  it("validates every path-taking MVP write command through PathGuard", () => {
    const source = fs.readFileSync(builtInCommandsPath, "utf8");
    const assetSafeWrites = fs.readFileSync(assetSafeWriteCommandsPath, "utf8");
    const pathGuard = fs.readFileSync(pathGuardPath, "utf8");

    expect(source).toContain("PathGuard.RequireUnityAssetPath(assetPath)");
    expect(source).toContain('validatedPath.StartsWith("Assets/", StringComparison.Ordinal)');
    expect(source).toContain("PathGuard.RequireUnityAssetPath(scenePath)");
    expect(assetSafeWrites).toContain("PathGuard.RequireUnityAssetPath(rawPath)");
    expect(pathGuard).toContain("absolute_path_rejected");
    expect(pathGuard).toContain("path_traversal_rejected");
    expect(pathGuard).toContain("path_not_allowed");
  });

  it("standardizes asset.import dry-run and mutation result metadata", () => {
    const source = fs.readFileSync(builtInCommandsPath, "utf8");
    const importSource = source.slice(source.indexOf('"asset.import"'), source.indexOf('[McpCommand(\n            "scene.open"'));
    const schemaLiteral = /const string AssetImportSchemaJson = "((?:\\.|[^"\\])*)";/.exec(source)?.[1];
    const schema = schemaLiteral ? JSON.parse(JSON.parse(`"${schemaLiteral}"`)) as { properties?: { asset_path?: { pattern?: string } } } : undefined;

    expect(source).toContain('const string AssetImportSchemaJson =');
    expect(source).toContain('\\"asset_path\\":{\\"type\\":\\"string\\",\\"minLength\\":1');
    expect(source).toContain('\\"pattern\\":\\"^Assets[');
    expect(source).toContain('Any asset file extension is accepted. Eligible Local or Embedded package C# files use package.local_script.import.');
    expect(source).toContain('\\"dry_run\\":{\\"type\\":\\"boolean\\",\\"default\\":false}');
    expect(schema?.properties?.asset_path?.pattern).toBe("^Assets[\\\\/].+");
    expect(new RegExp(schema?.properties?.asset_path?.pattern ?? "$").test("Assets/Foo.prefab")).toBe(true);
    expect(new RegExp(schema?.properties?.asset_path?.pattern ?? "$").test("Assets\\Foo.prefab")).toBe(true);
    expect(new RegExp(schema?.properties?.asset_path?.pattern ?? "$").test("Packages/com.example/Foo.cs")).toBe(false);
    expect(importSource).toContain('AssetImportPathScopeError(validatedPath)');
    expect(importSource).toContain('CommandExecutionResult.ValidationError("invalid_field_value", message, "asset_path", "unsupported_for_target")');
    expect(importSource).toContain('PathGuard.RequireUnityAssetPath(assetPath)');
    expect(importSource).toContain('asset_path must be under Assets/');
    expect(importSource).toContain('package.local_script.import');
    expect(importSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false)');
    expect(importSource).toContain('\\"would_import\\"');
    expect(importSource).toContain('AssetDatabase.ImportAsset(validatedPath, ImportAssetOptions.ForceUpdate)');
    expect(importSource).toContain('\\"guid_before\\"');
    expect(importSource).toContain('\\"guid_after\\"');
    expect(importSource).toContain('\\"changed_assets\\"');
    expect(importSource).toContain('\\"imported_assets\\"');
    expect(importSource).toContain('\\"next_valid_actions\\"');
  });

  it("declares conservative asset move and delete commands", () => {
    const assetSafeWrites = fs.readFileSync(assetSafeWriteCommandsPath, "utf8");

    expect(assetSafeWrites).toContain('\"asset.folder.create\"');
    expect(assetSafeWrites).toContain('\"asset.move\"');
    expect(assetSafeWrites).toContain('\"asset.delete\"');
    expect(assetSafeWrites).toContain('RequireAssetsPath(context.ArgsJson, "source_path"');
    expect(assetSafeWrites).toContain('RequireAssetsPath(context.ArgsJson, "destination_path"');
    expect(assetSafeWrites).toContain('RequireAssetsPath(context.ArgsJson, "asset_path"');
    expect(assetSafeWrites).toContain('precondition_guid');
    expect(assetSafeWrites).toContain('RequirePreconditionGuid(context.ArgsJson, "asset.move", sourcePath, currentSourceGuid)');
    expect(assetSafeWrites).toContain('RequirePreconditionGuid(context.ArgsJson, "asset.delete", assetPath, currentGuid)');
    expect(assetSafeWrites).toContain("destination_path already exists; overwrite is not supported");
    expect(assetSafeWrites).toContain("dry_run");
    expect(assetSafeWrites).toContain("moved_assets");
    expect(assetSafeWrites).toContain("deleted_assets");
    expect(assetSafeWrites).toContain("AssetDatabase.MoveAsset(sourcePath, destinationPath)");
    expect(assetSafeWrites).toContain("AssetDatabase.DeleteAsset(assetPath)");
  });

  it("defines optional advanced descriptor metadata fields", () => {
    const descriptor = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "CommandDescriptor.cs"), "utf8");
    const bridgeServer = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "BridgeServer.cs"), "utf8");

    expect(descriptor).toContain("Tags");
    expect(descriptor).toContain("Capability");
    expect(descriptor).toContain("FeatureFlag");
    expect(bridgeServer).toContain("feature_flag");
    expect(bridgeServer).toContain("supports_job");
  });

  it("keeps required CanonicalPath installable through setup-owned SGG facades", () => {
    const manifest = JSON.parse(fs.readFileSync(path.join(packageRoot, "package.json"), "utf8"));
    const asmdef = JSON.parse(fs.readFileSync(path.join(packageRoot, "Editor", "SGG.UnityMCP.Editor.asmdef"), "utf8"));
    const canonicalPathAsmdef = JSON.parse(fs.readFileSync(path.join(packageRoot, "Editor", "CanonicalPath", "SGG.UnityMCP.Editor.CanonicalPath.asmdef"), "utf8"));
    const canonicalPathRegistration = fs.readFileSync(path.join(packageRoot, "Editor", "CanonicalPath", "CanonicalPathBridgeRegistration.cs"), "utf8");
    const bridgeSource = fs.readFileSync(path.join(packageRoot, "Editor", "Paths", "CanonicalPathBridge.cs"), "utf8");
    const interfaceSource = fs.readFileSync(path.join(packageRoot, "Editor", "Paths", "ICanonicalPathService.cs"), "utf8");
    const pathGuard = fs.readFileSync(pathGuardPath, "utf8");
    const canonicalPathService = fs.readFileSync(canonicalPathServicePath, "utf8");

    expect(manifest.dependencies["com.romanilyin.canonicalpath"]).toBeUndefined();
    expect(manifest.dependencies["com.unity.modules.imageconversion"]).toBe("1.0.0");
    expect(manifest.dependencies["com.unity.modules.screencapture"]).toBe("1.0.0");
    expect(manifest.dependencies["com.unity.test-framework"]).toBe("1.1.33");
    expect(asmdef.references).not.toContain("CanonicalPath.UnityBridge");
    expect(asmdef.versionDefines).toEqual([]);
    expect(asmdef.defineConstraints).toEqual([]);
    expect(canonicalPathAsmdef.references).toContain("SGG.UnityMCP.Editor");
    expect(canonicalPathAsmdef.references).toContain("CanonicalPath.UnityBridge");
    expect(canonicalPathAsmdef.defineConstraints).toContain("SGG_UNITY_MCP_CANONICALPATH");
    expect(canonicalPathAsmdef.versionDefines).toContainEqual({
      name: "com.romanilyin.canonicalpath",
      expression: "2026.10.10-4",
      define: "SGG_UNITY_MCP_CANONICALPATH",
    });
    expect(canonicalPathRegistration).toContain("new global::CanonicalPath.BridgeCanonicalPathService()");
    expect(canonicalPathRegistration).toContain("global::CanonicalPath.PathGuard.NormalizeUnityPath");
    expect(canonicalPathRegistration).toContain("s_bridge.Normalize(MapDefaultWslDrive(input))");
    expect(canonicalPathRegistration).toContain('candidate.StartsWith("/mnt/", System.StringComparison.Ordinal)');
    expect(canonicalPathRegistration).toContain("s_bridge.MakeSafeFileName(input, maxLength)");
    expect(bridgeSource).not.toContain("System.Reflection");
    expect(interfaceSource).toContain("public interface ICanonicalPathService");
    expect(bridgeSource).toContain("CanonicalPathDependencyMissingException");
    expect(pathGuard).toContain("CanonicalPathBridge.TryNormalizeUnityPath(input, out normalized)");
    expect(pathGuard).toContain("throw new CanonicalPathDependencyMissingException()");
    expect(pathGuard).toContain("RejectUnsafeUnityPayload(input, true)");
    expect(pathGuard).toContain("RejectUnsafeUnityPayload(normalized, false)");
    expect(pathGuard).toContain("IsWindowsReservedName(part)");
    expect(pathGuard).toContain("part.Length > 255");
    expect(canonicalPathService).toContain("CanonicalPathBridge.TryNormalize(input, out normalized)");
    expect(canonicalPathService).toContain("CanonicalPathBridge.TryMakeSafeFileName(input, maxLength, out safeFileName)");
    expect(canonicalPathService).toContain("throw new CanonicalPathDependencyMissingException()");
    expect(canonicalPathService).toContain("RejectEmptyRoot(input)");
    expect(canonicalPathService).toContain("RejectUncPath(input.Replace('\\\\', '/'))");
    expect(canonicalPathService.indexOf("PathGuard.HasTraversal(candidate.Value.Replace('\\\\', '/'))"))
      .toBeLessThan(canonicalPathService.indexOf("Normalize(candidate.Value)"));
  });

  it("keeps CanonicalPath dependency, provider, corpus and setup pins synchronized", () => {
    const gateway = JSON.parse(fs.readFileSync(path.join(process.cwd(), "package.json"), "utf8"));
    const lock = JSON.parse(fs.readFileSync(path.join(process.cwd(), "package-lock.json"), "utf8"));
    const version = gateway.dependencies["@romanilyin/canonicalpath"];
    expect(version).toBe("2026.10.10-4");
    expect(lock.packages[""].dependencies["@romanilyin/canonicalpath"]).toBe(version);
    expect(lock.packages["node_modules/@romanilyin/canonicalpath"].version).toBe(version);
    for (const relative of [
      "Editor/CanonicalPath/SGG.UnityMCP.Editor.CanonicalPath.asmdef",
      "Tests/CanonicalPath/SGG.UnityMCP.Editor.CanonicalPath.Tests.asmdef",
    ]) {
      const definition = JSON.parse(fs.readFileSync(path.join(packageRoot, relative), "utf8"));
      expect(definition.defineConstraints).toContain("SGG_UNITY_MCP_CANONICALPATH");
      expect(definition.versionDefines).toContainEqual({
        name: "com.romanilyin.canonicalpath", expression: version, define: "SGG_UNITY_MCP_CANONICALPATH",
      });
    }
    expect(fs.readFileSync(path.join(packageRoot, "Editor/Paths/CanonicalPathBridge.cs"), "utf8"))
      .toContain(`public const string PackageVersion = "${version}";`);
    expect(fs.readFileSync(path.join(packageRoot, "Editor/UI/UnityMcpSetupWindow.cs"), "utf8"))
      .toContain(`const string CanonicalPathVersion = "${version}";`);
    for (const language of ["en", "ru"]) {
      expect(fs.readFileSync(path.join(process.cwd(), "docs", language, "installation.md"), "utf8"))
        .toContain(`"com.romanilyin.canonicalpath": "${version}"`);
    }
  });

  it("keeps C# CanonicalPath facades aligned with shared project alias vectors", () => {
    const interfaceSource = fs.readFileSync(path.join(packageRoot, "Editor", "Paths", "ICanonicalPathService.cs"), "utf8");
    const canonicalPathService = fs.readFileSync(canonicalPathServicePath, "utf8");
    const heartbeatRecord = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "HeartbeatRecord.cs"), "utf8");
    const heartbeatWriter = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "HeartbeatWriter.cs"), "utf8");

    expect(interfaceSource).toContain("public sealed class PathAlias");
    expect(interfaceSource).toContain("public PathAlias()");
    expect(interfaceSource).toContain("public string environment;");
    expect(interfaceSource).toContain("public string host_path;");
    expect(interfaceSource).toContain("public string canonical_path;");
    expect(interfaceSource).toContain("PathAlias[] ProjectRootAliases(string input);");
    expect(canonicalPathService).toContain("public PathAlias[] ProjectRootAliases(string input)");
    expect(canonicalPathService).toContain('AddAlias(aliases, "unity"');
    expect(canonicalPathService).toContain('AddAlias(aliases, "windows"');
    expect(canonicalPathService).toContain('AddAlias(aliases, "wsl"');
    expect(canonicalPathService).toContain('AddAlias(aliases, "posix"');
    expect(canonicalPathService).toContain("ToWindowsDrivePath(canonicalRoot)");
    expect(canonicalPathService).toContain("ToWslDrivePath(canonicalRoot)");
    expect(heartbeatRecord).toContain("public string canonical_project_root");
    expect(heartbeatRecord).toContain("public PathAlias[] aliases");
    expect(heartbeatRecord).toContain("public string package_version");
    expect(heartbeatWriter).toContain("canonical_project_root = canonicalProjectRoot");
    expect(heartbeatWriter).toContain("aliases = m_paths.ProjectRootAliases(projectRoot)");
    expect(heartbeatWriter).toContain("package_version = UnityBridgeRuntime.PackageVersion");
  });

  it("writes heartbeat records defensively under concurrent project discovery", () => {
    const heartbeatWriter = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "HeartbeatWriter.cs"), "utf8");
    const bridgeRuntime = fs.readFileSync(path.join(packageRoot, "Editor", "Bridge", "UnityBridgeRuntime.cs"), "utf8");

    expect(heartbeatWriter).toContain("public bool Write()");
    expect(heartbeatWriter).toContain("catch (IOException exception)");
    expect(heartbeatWriter).toContain("catch (UnauthorizedAccessException exception)");
    expect(heartbeatWriter).toContain("File.Replace(tempPath, targetPath, null)");
    expect(heartbeatWriter).toContain("File.Move(tempPath, targetPath)");
    expect(heartbeatWriter).toContain("FileShare.ReadWrite | FileShare.Delete");
    expect(heartbeatWriter).toContain('Directory.GetFiles(m_instancesDirectory, m_state.Project + "-*.json")');
    expect(heartbeatWriter).toContain("m_cleanupAttempted");
    expect(heartbeatWriter).toContain("m_nextWriteWarningUtc = now.AddSeconds(30)");
    expect(heartbeatWriter).toContain("UnityEngine.Debug.LogWarning");
    expect(bridgeRuntime).toContain("if (HeartbeatWriter.Write())");
  });
});
