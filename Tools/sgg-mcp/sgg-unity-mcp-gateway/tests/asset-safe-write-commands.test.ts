import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const sourcePath = path.join(packageRoot, "Editor", "Commands", "AssetSafeWriteCommands.cs");
const gatewayExampleConfigPath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExampleConfigPath = path.join(process.cwd(), "config", "policy.example.json");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");

describe("Unity asset safe-write command source-contract", () => {
  it("suggests only registered checkpoint command names in asset and scene write responses", () => {
    const commandSources = [
      "AssetSafeWriteCommands.cs",
      "AssetIndexCommands.cs",
      "EditorSafeWriteCommands.cs",
      "SceneWiringCommands.cs",
      "UguiInspectionCommands.cs",
    ].map((name) => fs.readFileSync(path.join(packageRoot, "Editor", "Commands", name), "utf8"));
    const checkpointCatalog = fs.readFileSync(path.join(process.cwd(), "src", "mcp", "checkpoints-commands.ts"), "utf8");

    expect(checkpointCatalog).toContain('id: "checkpoint.create"');
    expect(checkpointCatalog).toContain('id: "checkpoint.list"');
    expect(checkpointCatalog).toContain('feature_flag: FEATURE_FLAG');
    expect(checkpointCatalog).toContain('risk: "write"');
    expect(commandSources.join("\n")).toContain("checkpoint.create");
    expect(commandSources.join("\n")).toContain("checkpoint.list");
    for (const source of commandSources) {
      expect(source).not.toContain("checkpoint.create_before_write");
      expect(source).not.toContain("checkpoint.restore_if_needed");
    }
  });

  it("declares asset commands behind read-only and safe-write feature flags", () => {
    const source = fs.readFileSync(sourcePath, "utf8");
    const config = JSON.parse(fs.readFileSync(gatewayExampleConfigPath, "utf8"));

    expect(source.match(/\[McpCommand\(/g)).toHaveLength(10);
    expect(source).toContain('"asset.folder.create"');
    expect(source).toContain('"asset.move"');
    expect(source).toContain('"asset.delete"');
    expect(source).toContain('"script.read"');
    expect(source).toContain('"script.hash"');
    expect(source).toContain('"script.validate"');
    expect(source).toContain('"script.capabilities"');
    expect(source).toContain('"script.text_edits.spec"');
    expect(source).toContain('"script.text_edits.apply"');
    expect(source).toContain('"script.write"');
    expect(source).toContain('const string ReadonlyFeatureFlag = "asset_readonly"');
    expect(source).toContain('const string SafeWritesFeatureFlag = "asset_safe_writes"');
    expect(source).toContain("FeatureFlag = ReadonlyFeatureFlag");
    expect(source).toContain("FeatureFlag = SafeWritesFeatureFlag");
    expect(config.features.asset_readonly).toBe(false);
    expect(config.features.asset_safe_writes).toBe(false);
  });

  it("uses write/destructive metadata and requires editing status", () => {
    const source = fs.readFileSync(sourcePath, "utf8");

    expect(source).toContain('Risk = "write"');
    expect(source).toContain('Risk = "destructive"');
    expect(source).toContain('Idempotency = "idempotent"');
    expect(source).toContain('Idempotency = "unsafe"');
    expect(source).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(source).toContain('Capability = ReadonlyFeatureFlag');
    expect(source).toContain('Capability = SafeWritesFeatureFlag');
  });

  it("validates folder paths through PathGuard and restricts writes to Assets children", () => {
    const source = fs.readFileSync(sourcePath, "utf8");

    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawPath)");
    expect(source).toContain('StartsWith("Assets/", StringComparison.Ordinal)');
    expect(source).toContain('folderPath == "Assets"');
    expect(source).toContain('folderPath == "Assets/"');
    expect(source).toContain('folderPath.EndsWith("/", StringComparison.Ordinal)');
    expect(source).toContain("LastIndexOf('/')");
    expect(source).toContain("Parent folder must already exist");
  });

  it("uses AssetDatabase safe-write APIs without arbitrary directory creation", () => {
    const source = fs.readFileSync(sourcePath, "utf8");

    expect(source).toContain('precondition_guid');
    expect(source).toContain('RequirePreconditionGuid(context.ArgsJson, "asset.move", sourcePath, currentSourceGuid)');
    expect(source).toContain('RequirePreconditionGuid(context.ArgsJson, "asset.delete", assetPath, currentGuid)');
    expect(source).toContain('precondition_required');
    expect(source).toContain('precondition_failed');
    expect(source).toContain("AssetDatabase.IsValidFolder(folderPath)");
    expect(source).toContain("AssetDatabase.IsValidFolder(parentPath)");
    expect(source).toContain("AssetDatabase.CreateFolder(parentPath, folderName)");
    expect(source).toContain("AssetDatabase.AssetPathToGUID(folderPath)");
    expect(source).toContain("AssetDatabase.MoveAsset(sourcePath, destinationPath)");
    expect(source).toContain("AssetDatabase.DeleteAsset(assetPath)");
    expect(source).toContain("AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceUpdate)");
    expect(source).toContain("dry_run");
    expect(source).toContain('changed_assets');
    expect(source).toContain('moved_assets');
    expect(source).toContain('deleted_assets');
    expect(source).toContain('created_assets');
    expect(source).toContain('next_valid_actions');
    expect(source).not.toContain("Directory.CreateDirectory");
  });

  it("uses one fresh editor-state authority and revalidates state plus identity before mutation", () => {
    const source = fs.readFileSync(sourcePath, "utf8");
    const editorStatusSource = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "EditorStatusCommands.cs"), "utf8");

    expect(source).not.toContain("context.State.Status");
    expect(source).toContain("EditorStatusCommands.CaptureEditModeEligibility()");
    expect(editorStatusSource).toContain("EditorEditModeEligibility");
    expect(editorStatusSource).toContain("CaptureEditModeEligibility()");
    expect(editorStatusSource).toContain("editorState.NormalizedStateJson()");
    expect(source).toContain('"unsupported_in_play_mode"');
    expect(source).toContain('"editor_not_ready"');
    expect(source).toContain(String.raw`\"sgg.unity_mcp.asset_write_eligibility.v1\"`);
    expect(source).toContain(String.raw`\"mutation_state\":\"not_applied\"`);
    expect(source).toContain(String.raw`\"editor_state\":`);

    expect(source.match(/RequireEditing\("asset\.move"/g)).toHaveLength(2);
    expect(source.match(/RequireEditing\("asset\.delete"/g)).toHaveLength(2);
    expect(source.match(/RequireEditing\("script\.text_edits\.apply"/g)).toHaveLength(2);
    expect(source.match(/RequireEditing\("script\.write"/g)).toHaveLength(2);
    expect(source).toContain("var currentSourceGuid = AssetDatabase.AssetPathToGUID(sourcePath)");
    expect(source).toContain("var currentGuid = AssetDatabase.AssetPathToGUID(assetPath)");
    expect(source).toContain('RequirePreconditionGuid(context.ArgsJson, "asset.move", sourcePath, currentSourceGuid)');
    expect(source).toContain('RequirePreconditionGuid(context.ArgsJson, "asset.delete", assetPath, currentGuid)');
    expect(source).toContain("var preMutationSha256 = Sha256File(fullPath)");
    expect(source).toContain("var existsBeforeMutation = File.Exists(fullPath) || AssetExists(scriptPath)");

    const deleteSource = source.slice(source.indexOf("public static CommandExecutionResult AssetDelete"), source.indexOf('[McpCommand(\n            "script.read"'));
    const freshGuid = deleteSource.indexOf("var currentGuid = AssetDatabase.AssetPathToGUID(assetPath)");
    const guidGuard = deleteSource.indexOf('RequirePreconditionGuid(context.ArgsJson, "asset.delete", assetPath, currentGuid)');
    const finalStateGuard = deleteSource.lastIndexOf('RequireEditing("asset.delete", false, out editorState)');
    const mutation = deleteSource.indexOf("AssetDatabase.DeleteAsset(assetPath)");
    expect(freshGuid).toBeGreaterThan(-1);
    expect(guidGuard).toBeGreaterThan(freshGuid);
    expect(finalStateGuard).toBeGreaterThan(guidGuard);
    expect(mutation).toBeGreaterThan(finalStateGuard);
  });

  it("reads and writes only bounded C# scripts under Assets", () => {
    const source = fs.readFileSync(sourcePath, "utf8");

    expect(source).toContain("ScriptReadSchemaJson");
    expect(source).toContain("ScriptHashSchemaJson");
    expect(source).toContain("ScriptValidateSchemaJson");
    expect(source).toContain("ScriptCapabilitiesOutputLimitJson");
    expect(source).toContain("ScriptTextEditsSpecOutputLimitJson");
    expect(source).toContain("ScriptTextEditsApplySchemaJson");
    expect(source).toContain("ScriptWriteSchemaJson");
    expect(source).toContain('\\"precondition_sha256\\"');
    expect(source).toContain('\\"refresh\\"');
    expect(source).toContain("RequireScriptPath(context.ArgsJson, out var scriptPath)");
    expect(source).toContain('scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain("const int MaxScriptReadBytes = 65536");
    expect(source).toContain("const int MaxScriptValidateBytes = 262144");
    expect(source).toContain("const int MaxScriptTextEditBytes = 262144");
    expect(source).toContain("const int MaxScriptWriteChars = 131072");
    expect(source).toContain("ReadUtf8Prefix(fullPath, maxBytes, out var byteLength, out var truncated)");
    expect(source).toContain("File.ReadAllText(fullPath, new UTF8Encoding(false, true))");
    expect(source).toContain("File.WriteAllText(fullPath, content, new UTF8Encoding(false))");
    expect(source).toContain("script_path already exists; set overwrite:true to replace it");
    expect(source).toContain("precondition_sha256 is required when overwrite:true replaces an existing script");
    expect(source).toContain('PreconditionHashDetailsJson("script.write", scriptPath, originalSha256)');
    expect(source).toContain("script parent folder must already exist");
  });

  it("hashes scripts safely without returning content", () => {
    const source = fs.readFileSync(sourcePath, "utf8");
    const hashSource = source.slice(source.indexOf("public static CommandExecutionResult ScriptHash"), source.indexOf('[McpCommand(\n            "script.validate"'));

    expect(hashSource).toContain("RequireScriptPath(context.ArgsJson, out var scriptPath)");
    expect(hashSource).toContain("MaxScriptHashBytes");
    expect(hashSource).toContain("script_path exceeds maximum hashable script size");
    expect(hashSource).toContain("File.OpenRead(fullPath)");
    expect(hashSource).toContain("SHA256.Create()");
    expect(hashSource).toContain("hash.ComputeHash(stream)");
    expect(hashSource).toContain("AssetDatabase.AssetPathToGUID(scriptPath)");
    expect(hashSource).toContain("byte_length");
    expect(hashSource).toContain("last_write_time_utc");
    expect(hashSource).toContain("sha256");
    expect(hashSource).not.toContain("content");
  });

  it("validates scripts safely without writing or compiling", () => {
    const source = fs.readFileSync(sourcePath, "utf8");
    const validateSource = source.slice(source.indexOf("public static CommandExecutionResult ScriptValidate"), source.indexOf('[McpCommand(\n            "script.text_edits.spec"'));

    expect(validateSource).toContain("RequireScriptPath(context.ArgsJson, out var scriptPath)");
    expect(validateSource).toContain("MaxScriptValidateBytes");
    expect(validateSource).toContain("include_diagnostics");
    expect(validateSource).toContain("ValidateScriptStructure(content, diagnostics)");
    expect(validateSource).toContain("AddStandardScriptWarnings(content, diagnostics)");
    expect(validateSource).toContain("script_path must be valid UTF-8 text");
    expect(validateSource).not.toContain("File.WriteAllText");
    expect(validateSource).not.toContain("AssetDatabase.ImportAsset");
    expect(validateSource).not.toContain("RequestScriptCompilation");
  });

  it("exposes the Unity AI Assistant script-edits spec without writing", () => {
    const source = fs.readFileSync(sourcePath, "utf8");
    const specSource = source.slice(source.indexOf("public static CommandExecutionResult ScriptTextEditsSpec"), source.indexOf('[McpCommand(\n            "script.text_edits.apply"'));

    expect(specSource).toContain("ScriptTextEditsSpecJson()");
    expect(source).toContain("unity://spec/script-edits");
    expect(source).toContain("supported_local_parity");
    expect(source).toContain("bounded_line_column_replace");
    expect(source).toContain("unsupported_structured_ops");
    expect(specSource).not.toContain("RequireEditing");
    expect(specSource).not.toContain("File.WriteAllText");
    expect(specSource).not.toContain("AssetDatabase.ImportAsset");
  });

  it("exposes script capabilities without overpromising structured edit support", () => {
    const source = fs.readFileSync(sourcePath, "utf8");
    const capabilitiesSource = source.slice(source.indexOf("public static CommandExecutionResult ScriptCapabilities"), source.indexOf('[McpCommand(\n            "script.text_edits.spec"'));

    expect(capabilitiesSource).toContain("ScriptCapabilitiesJson()");
    expect(source).toContain('\\"compatible_with\\":\\"Unity.ManageScript_capabilities\\"');
    expect(source).toContain('\\"ops\\":[]');
    expect(source).toContain('\\"bounded_line_column_replace\\"');
    expect(source).toContain('\\"unsupported_structured_ops\\"');
    expect(source).toContain('\\"replace_method\\"');
    expect(source).toContain('\\"anchor_insert\\"');
    expect(source).toContain('\\"unsupported_text_ops\\"');
    expect(source).toContain('\\"regex_replace\\"');
    expect(source).toContain('\\"sha256_precondition_required\\":true');
    expect(source).toContain('\\"using_guard\\":false');
    expect(capabilitiesSource).not.toContain("RequireEditing");
    expect(capabilitiesSource).not.toContain("File.WriteAllText");
    expect(capabilitiesSource).not.toContain("AssetDatabase.ImportAsset");
    expect(capabilitiesSource).not.toContain("RequestScriptCompilation");
  });

  it("applies script text edits only with SHA preconditions and bounded writes", () => {
    const source = fs.readFileSync(sourcePath, "utf8");
    const editSource = source.slice(source.indexOf("public static CommandExecutionResult ScriptTextEditsApply"), source.indexOf('[McpCommand(\n            "script.write"'));

    expect(editSource).toContain('RequireEditing("script.text_edits.apply", dryRun, out var editorState)');
    expect(editSource).toContain("RequireScriptPath(context.ArgsJson, out var scriptPath)");
    expect(editSource).toContain("precondition_sha256");
    expect(editSource).toContain("IsSha256Hex(preconditionSha256)");
    expect(editSource).toContain("precondition_failed");
    expect(editSource).toContain("TryParseScriptTextEdits");
    expect(editSource).toContain("TryResolveScriptTextEdits");
    expect(source).toContain("edits must not overlap or share the same start location");
    expect(editSource).toContain("dry_run");
    expect(editSource).toContain("changed_assets");
    expect(editSource).toContain("imported_assets");
    expect(editSource).toContain("next_valid_actions");
    expect(editSource).toContain("File.WriteAllText(fullPath, updatedContent, new UTF8Encoding(false))");
    expect(editSource).toContain("AssetDatabase.ImportAsset(scriptPath, ImportAssetOptions.ForceUpdate)");
    expect(editSource).not.toContain("RequestScriptCompilation");
  });

  it("documents config and policy gating for asset safe-write commands", () => {
    const policy = JSON.parse(fs.readFileSync(policyExampleConfigPath, "utf8"));
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const ruDocs = fs.readFileSync(commandsRuDocsPath, "utf8");

    expect(policy.projects.game.ask).toContain("asset.folder.create");
    expect(policy.projects.game.ask).toContain("asset.move");
    expect(policy.projects.game.ask).toContain("asset.delete");
    expect(policy.projects.game.allow).toContain("script.read");
    expect(policy.projects.game.allow).toContain("script.hash");
    expect(policy.projects.game.allow).toContain("script.validate");
    expect(policy.projects.game.allow).toContain("script.capabilities");
    expect(policy.projects.game.allow).toContain("script.text_edits.spec");
    expect(policy.projects.game.ask).toContain("script.text_edits.apply");
    expect(policy.projects.game.ask).toContain("script.write");
    expect(docs).toContain("features.asset_readonly");
    expect(docs).toContain("features.asset_safe_writes");
    expect(docs).toContain("asset.folder.create");
    expect(docs).toContain("asset.move");
    expect(docs).toContain("asset.delete");
    expect(docs).toContain("script.read");
    expect(docs).toContain("script.hash");
    expect(docs).toContain("script.validate");
    expect(docs).toContain("script.capabilities");
    expect(docs).toContain("script.text_edits.spec");
    expect(docs).toContain("script.text_edits.apply");
    expect(docs).toContain("script.write");
    expect(docs).toContain("dry_run:true");
    expect(docs).toContain("precondition_guid");
    expect(docs).toContain("precondition_sha256");
    expect(docs).toContain("Safe asset relocation recipe");
    expect(docs).toContain("changed_assets");
    expect(ruDocs).toContain("features.asset_readonly");
    expect(ruDocs).toContain("features.asset_safe_writes");
    expect(ruDocs).toContain("asset.folder.create");
    expect(ruDocs).toContain("asset.move");
    expect(ruDocs).toContain("asset.delete");
    expect(ruDocs).toContain("script.read");
    expect(ruDocs).toContain("script.hash");
    expect(ruDocs).toContain("script.validate");
    expect(ruDocs).toContain("script.capabilities");
    expect(ruDocs).toContain("script.text_edits.spec");
    expect(ruDocs).toContain("script.text_edits.apply");
    expect(ruDocs).toContain("script.write");
    expect(ruDocs).toContain("dry_run:true");
    expect(ruDocs).toContain("precondition_guid");
    expect(ruDocs).toContain("precondition_sha256");
    expect(ruDocs).toContain("Safe asset relocation recipe");
    expect(ruDocs).toContain("changed_assets");
  });
});
