import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const editorUtilityCommandsPath = path.join(packageRoot, "Editor", "Commands", "EditorUtilityCommands.cs");
const gatewayExampleConfigPath = path.join(process.cwd(), "config", "gateway.example.json");

describe("Unity editor utility command source-contract", () => {
  it("declares editor utility commands behind the editor_utilities feature flag", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const config = JSON.parse(fs.readFileSync(gatewayExampleConfigPath, "utf8"));

    expect(source).toContain('const string FeatureFlag = "editor_utilities"');
    expect(source).toContain('"editor.selection.get"');
    expect(source).toContain('"editor.selection.set"');
    expect(source).toContain('"editor.prefab_stage.get"');
    expect(source).toContain('"editor.active_tool.get"');
    expect(source).toContain('"editor.active_tool.set"');
    expect(source).toContain('"editor.windows.list"');
    expect(source).toContain('"editor.window.inspect"');
    expect(source).toContain('"editor.window.position.set"');
    expect(source).toContain('"editor.menu.list"');
    expect(source).toContain('"editor.menu.exists"');
    expect(source).toContain('"editor.menu.execute"');
    expect(source).toContain('"editor.tags.list"');
    expect(source).toContain('"editor.tags.add"');
    expect(source).toContain('"editor.tags.remove"');
    expect(source).toContain('"editor.layers.list"');
    expect(source).toContain('"editor.layers.add"');
    expect(source).toContain('"editor.layers.remove"');
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(17);
    expect(source).toContain('Capability = FeatureFlag');
    expect(config.features.editor_utilities).toBe(false);
  });

  it("uses policy-compatible risk and idempotency metadata", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");

    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('Risk = "write"');
    expect(source).toContain('Idempotency = "idempotent"');
    expect(source).toContain('Risk = "execute"');
    expect(source).toContain('Idempotency = "unsafe"');
  });

  it("keeps schemas strict and utility results bounded", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");

    expect(source).toContain('additionalProperties\\":false');
    expect(source).toContain('oneOf');
    expect(source).toContain('MenuExecuteSchemaJson');
    expect(source).toContain('MenuExistsSchemaJson');
    expect(source).toContain('MenuListSchemaJson');
    expect(source).toContain('TagModifySchemaJson');
    expect(source).toContain('LayerModifySchemaJson');
    expect(source).toContain('EmptyObjectSchemaJson');
    expect(source).toContain(String.raw`\"required\":[\"menu_path\"]`);
    expect(source).toContain('DefaultSelectionLimit = 100');
    expect(source).toContain('MaxSelectionLimit = 100');
    expect(source).toContain('DefaultWindowLimit = 50');
    expect(source).toContain('MaxWindowLimit = 100');
    expect(source).toContain('WindowsListSchemaJson');
    expect(source).toContain('WindowInspectSchemaJson');
    expect(source).toContain('WindowPositionSetSchemaJson');
    expect(source).toContain('MaxMenuPathLength = 128');
    expect(source).toContain('OutputLimitJson = "{\\"max_objects\\":100}"');
    expect(source).toContain('OutputLimitJson = "{\\"max_objects\\":1}"');
    expect(source).toContain('OutputLimitJson = "{\\"max_objects\\":16}"');
    expect(source).toContain('OutputLimitJson = "{\\"max_items\\":200}"');
    expect(source).toContain('OutputLimitJson = "{\\"max_items\\":100}"');
    expect(source).toContain('OutputLimitJson = "{\\"max_items\\":32}"');
  });

  it("adds read-only prefab stage status for Unity AI Assistant parity", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const prefabStageSource = source.slice(source.indexOf("public static CommandExecutionResult EditorPrefabStageGet"), source.indexOf('[McpCommand(\n            "editor.menu.list"'));

    expect(source).toContain("using UnityEditor.SceneManagement;");
    expect(prefabStageSource).toContain("PrefabStageUtility.GetCurrentPrefabStage()");
    expect(prefabStageSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor GetPrefabStage\\"');
    expect(prefabStageSource).toContain('\\"is_open\\":false');
    expect(prefabStageSource).toContain('\\"asset_path\\":null');
    expect(prefabStageSource).toContain('\\"prefab_root_name\\":null');
    expect(prefabStageSource).toContain("stage.assetPath");
    expect(prefabStageSource).toContain("stage.prefabContentsRoot");
    expect(prefabStageSource).toContain("stage.mode.ToString()");
    expect(prefabStageSource).toContain("stage.scene.isDirty");
    expect(prefabStageSource).not.toContain("SaveScene");
    expect(prefabStageSource).not.toContain("SaveAsPrefabAsset");
    expect(prefabStageSource).not.toContain("OpenPrefab");
    expect(prefabStageSource).not.toContain("LoadPrefabContents");
    expect(prefabStageSource).not.toContain("AssetDatabase");
    expect(prefabStageSource).not.toContain("PathGuard");
  });

  it("adds read-only active tool status for Unity AI Assistant parity", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const activeToolSource = source.slice(source.indexOf("public static CommandExecutionResult EditorActiveToolGet"), source.indexOf('[McpCommand(\n            "editor.active_tool.set"'));

    expect(activeToolSource).toContain("Tools.current");
    expect(activeToolSource).toContain("ActiveToolName(currentTool)");
    expect(source).toContain('Type.GetType("UnityEditor.EditorTools.ToolManager, UnityEditor")');
    expect(source).toContain('GetProperty("activeToolType", BindingFlags.Public | BindingFlags.Static)');
    expect(source).not.toContain("EditorTools.GetActiveToolName()");
    expect(activeToolSource).toContain("Tools.pivotMode");
    expect(activeToolSource).toContain("Tools.pivotRotation");
    expect(activeToolSource).toContain("Tools.handleRotation.eulerAngles");
    expect(activeToolSource).toContain("Tools.handlePosition");
    expect(activeToolSource).toContain("Vector3ArrayJson");
    expect(activeToolSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor GetActiveTool\\"');
    expect(source).toContain("static string FloatJson(float value)");
    expect(activeToolSource).not.toContain("Tools.current =");
    expect(activeToolSource).not.toContain("SetActiveTool");
    expect(activeToolSource).not.toContain("EditorApplication.ExecuteMenuItem");
    expect(activeToolSource).not.toContain("AssetDatabase");
    expect(activeToolSource).not.toContain("PathGuard");
  });

  it("adds write-risk active tool set with dry-run preflight", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"editor.active_tool.set"'), source.indexOf("static string ActiveToolName"));
    const helperSource = source.slice(source.indexOf("static bool TryParseBuiltInTool"), source.indexOf('[McpCommand(\n            "editor.windows.list"'));

    expect(source).toContain('const string ActiveToolSetSchemaJson =');
    expect(source).toContain('\\"View\\",\\"Move\\",\\"Rotate\\",\\"Scale\\",\\"Rect\\",\\"Transform\\"');
    expect(commandSource).toContain('Risk = "write"');
    expect(commandSource).toContain('Idempotency = "idempotent"');
    expect(commandSource).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false)');
    expect(commandSource).toContain('Tools.current = tool');
    expect(commandSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor SetActiveTool\\"');
    expect(helperSource).toContain('Enum.TryParse(value, true, out tool)');
    expect(helperSource).not.toContain('EditorApplication.ExecuteMenuItem');
    expect(helperSource).not.toContain('AssetDatabase');
  });

  it("adds bounded read-only editor window metadata for Unity AI Assistant parity", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const windowsCommandSource = source.slice(source.indexOf("public static CommandExecutionResult EditorWindowsList"), source.indexOf("static CommandExecutionResult TryResolveObjectByRuntimeId"));
    const windowsHelperSource = source.slice(source.indexOf("static string EditorWindowsJson"), source.indexOf("static string FloatJson"));

    expect(windowsCommandSource).toContain("JsonUtil.ExtractInt(context.ArgsJson, \"max_results\", DefaultWindowLimit)");
    expect(windowsCommandSource).toContain("max_results must be between 1 and 100");
    expect(windowsHelperSource).toContain("UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>()");
    expect(windowsHelperSource).toContain("EditorWindow.focusedWindow");
    expect(windowsHelperSource).toContain("window.titleContent.text");
    expect(windowsHelperSource).toContain('"window_index\\":');
    expect(windowsHelperSource).toContain("window.GetType().FullName");
    expect(windowsHelperSource).toContain("AppendRuntimeObjectIds(builder, window)");
    expect(windowsHelperSource).toContain("RectJson(window.position)");
    expect(windowsHelperSource).toContain('"windows_hash\\":');
    expect(windowsHelperSource).toContain("EditorWindowsHash");
    expect(windowsHelperSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor GetWindows\\"');
    expect(windowsHelperSource).not.toContain("GetWindow<");
    expect(windowsHelperSource).not.toContain("Focus()");
    expect(windowsHelperSource).not.toContain("Show()");
    expect(windowsHelperSource).not.toContain("Close()");
    expect(windowsHelperSource).not.toContain("EditorApplication.ExecuteMenuItem");
    expect(windowsHelperSource).not.toContain("AssetDatabase");
    expect(windowsHelperSource).not.toContain("PathGuard");
  });

  it("adds read-only editor window inspect with shared selectors and stability metadata", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const selector = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "EditorWindowSelector.cs"), "utf8");
    const commandSource = source.slice(source.indexOf('"editor.window.inspect"'), source.indexOf('[McpCommand(\n            "editor.window.position.set"'));

    expect(commandSource).toContain('Risk = "read"');
    expect(commandSource).toContain('Idempotency = "safe"');
    expect(commandSource).toContain('Capability = FeatureFlag');
    expect(commandSource).toContain('ResultShape = "editor_window"');
    expect(commandSource).toContain('EditorWindowSelector.Select(context.ArgsJson');
    expect(commandSource).toContain('EditorWindowSelector.MetadataJson(window, selectedIndex)');
    expect(commandSource).toContain('\\"windows_hash\\"');
    expect(commandSource).toContain('\\"selector_contract\\"');
    expect(commandSource).toContain('\\"privacy_warning\\"');
    expect(commandSource).toContain('"next_actions\\":[\\"editor.windows.list\\",\\"editor.window.screenshot\\",\\"editor.window.position.set\\"]');
    expect(source).toContain("static string EditorWindowsHash");
    expect(source).toContain("static void UpdateHash");
    expect(selector).toContain('JsonUtil.ExtractString(argsJson, "title")');
    expect(selector).toContain('JsonUtil.ExtractInt(argsJson, "window_index", -1)');
    expect(selector).toContain('builder.Append(",\\"instance_id\\":")');
    expect(commandSource).not.toContain("Focus()");
    expect(commandSource).not.toContain("Show()");
    expect(commandSource).not.toContain("Close()");
    expect(commandSource).not.toContain("GetWindow<");
    expect(commandSource).not.toContain("EditorApplication.ExecuteMenuItem");
  });

  it("adds write-risk editor window position set with shared selectors and dry-run", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"editor.window.position.set"'), source.indexOf("static CommandExecutionResult TryResolveObjectByRuntimeId"));

    expect(source).toContain('const string WindowPositionSetSchemaJson =');
    expect(commandSource).toContain('Risk = "write"');
    expect(commandSource).toContain('Idempotency = "idempotent"');
    expect(commandSource).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(commandSource).toContain('EditorWindowSelector.Select(context.ArgsJson');
    expect(commandSource).toContain('JsonUtil.ExtractInt(context.ArgsJson, "x", int.MinValue)');
    expect(commandSource).toContain('JsonUtil.ExtractInt(context.ArgsJson, "width", 0)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false)');
    expect(commandSource).toContain('window.position = requestedPosition');
    expect(commandSource).toContain('Unity.ManageEditor SetWindowPosition');
    expect(commandSource).not.toContain('GetWindow<');
    expect(commandSource).not.toContain('Show()');
    expect(commandSource).not.toContain('Close()');
  });

  it("validates path selectors and supports versioned runtime object ids for setting selection", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult EditorSelectionSet"), source.indexOf('[McpCommand(\n            "editor.prefab_stage.get"'));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult TryValidateSceneAssetPath"), source.indexOf("static CommandExecutionResult RequireTagName"));

    expect(source).toContain("PathGuard.RequireUnityAssetPath(assetPath)");
    expect(source).toContain("TryValidateAssetPath(scenePath, out validatedPath)");
    expect(source).toContain("AssetDatabase.LoadMainAssetAtPath(validatedPath)");
    expect(source).toContain("EditorUtility.InstanceIDToObject(instanceId)");
    expect(source).toContain("EditorUtility.EntityIdToObject(UnityEngine.EntityId.FromULong(runtimeEntityId))");
    expect(source).toContain("UnityEngine.EntityId.ToULong(selectedObject.GetEntityId())");
    expect(source).toContain("Provide exactly one of asset_path, instance_id, entity_id, or scene_path with object_path");
    expect(source).toContain('required\\\":[\\\"scene_path\\\",\\\"object_path\\\"]');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "scene_path")');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "object_path")');
    expect(commandSource).toContain('scene_path and object_path must be provided together');
    expect(commandSource).toContain('FindLoadedScene(validatedPath)');
    expect(commandSource).toContain('scene_path must identify an already loaded scene');
    expect(commandSource).toContain('FindByPath(scene.GetRootGameObjects(), objectPath)');
    expect(commandSource).toContain('NormalizeObjectPath(objectPath)');
    expect(commandSource).toContain('AppendSelectedObject(builder, selectedObject, selectedAssetPath, selectedScenePath, selectedObjectPath)');
    expect(helperSource).toContain('validatedPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)');
    expect(helperSource).toContain('SceneManager.sceneCount');
    expect(helperSource).toContain('SceneManager.GetSceneAt(index)');
    expect(helperSource).toContain('normalized.Split(new[] { \'/\' }, StringSplitOptions.RemoveEmptyEntries)');
    expect(helperSource).not.toContain('EditorSceneManager.OpenScene');
  });

  it("keeps menu execution policy-gated without menu allowlist blocking", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");

    expect(source).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(source).toContain('"SGG/Unity MCP/MCP Window"');
    expect(source).toContain("IsKnownSafeMenuPath(menuPath)");
    expect(source).toContain("TryMenuCatalogContains(menuPath, out var existsInCatalog, out var catalogTruncated, out var catalogScanned, out var catalogSource, out var catalogRefreshAttempted, out var menuItemAttributeScanned, out var lookupTimedOut)");
    expect(source).toContain("ExecuteMenuItemObserved(menuPath)");
    expect(source).toContain("Application.logMessageReceived += callback");
    expect(source).toContain('CommandExecutionResult.Error(\n                    "unity_exception"');
    expect(source).toContain('"menu_path_not_found"');
    expect(source).toContain("return MenuPathNotFound(menuPath, catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, catalogScanned, catalogSource, catalogRefreshAttempted, menuItemAttributeScanned, lookupTimedOut)");
    expect(source).toContain('"suggested_recovery_actions\\":[\\"compile.wait_ready\\",\\"assets.refresh\\",\\"editor.menu.list\\",\\"editor.menu.exists\\",\\"diagnostics.unity_modal.status\\"]');
    expect(source).toContain('"feature_flag\\":\\"editor_utilities\\"');
    expect(source).toContain('"catalog_age_ms\\":0');
    expect(source).toContain('"catalog_refresh_attempted\\":');
    expect(source).toContain('"menu_item_attribute_scanned\\":');
    expect(source).toContain('"lookup_status\\":');
    expect(source).toContain('"lookup_timeout\\":');
    expect(source).toContain('"lookup_budget_ms\\":');
    expect(source).toContain('"modal_diagnostics_command\\":\\"diagnostics.unity_modal.status\\"');
    expect(source).toContain('"menu_lookup_timeout"');
    expect(source).toContain('"menu_item_attribute_scan"');
    expect(source).not.toContain('"menu_catalog_unavailable"');
    expect(source).toContain('"dry_run\\":true');
    expect(source).toContain('"would_attempt_execute\\":true');
    expect(source).toContain('"can_attempt_execute\\":true');
    expect(source).toContain('"known_safe_menu_paths\\":');
    expect(source).not.toContain('"allowed_menu_paths\\":');
    expect(source).toContain('"execute_policy_gated\\":true');
    expect(source).not.toContain('"execute_requires_approval\\":true');
    expect(source).not.toContain('"SGG/Unity MCP/Install Roslyn Compiler"');
    expect(source).not.toContain('"SGG/Unity MCP/Uninstall Roslyn Compiler"');
  });

  it("returns missing-path diagnostics only when menu absence is provable", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult EditorMenuExecute"), source.indexOf('[McpCommand(\n            "editor.tags.list"'));

    expect(commandSource).toContain("if (catalogAvailable && !catalogTruncated && !knownSafe && !existsInCatalog)");
    expect(commandSource).toContain("return MenuPathNotFound(menuPath, catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, catalogScanned, catalogSource, catalogRefreshAttempted, menuItemAttributeScanned, lookupTimedOut)");
    expect(commandSource).toContain("return MenuLookupTimeout(menuPath, true, false");
    expect(commandSource).toContain("return MenuLookupTimeout(menuPath, false, true");
    expect(commandSource).toContain("if (!execution.Returned)");
    expect(commandSource.indexOf("if (!execution.Returned)")).toBeLessThan(commandSource.lastIndexOf("return MenuPathNotFound(menuPath"));
    expect(commandSource.indexOf("if (!execution.Returned)")).toBeLessThan(commandSource.lastIndexOf("TryMenuCatalogContains(menuPath, out var existsInCatalog"));
    expect(commandSource).toContain('"would_attempt_execute\\":true');
    expect(commandSource).toContain('"catalog_source\\":');
    expect(commandSource).toContain('"catalog_refresh_attempted\\":');
    expect(commandSource).toContain('"menu_item_attribute_scanned\\":');
    expect(commandSource).toContain('"lookup_timeout\\":');
    expect(commandSource).toContain('"unity_lifecycle\\":');
    expect(commandSource).not.toContain('"allowed_menu_paths\\":');
    expect(commandSource).not.toContain('"menu_catalog_unavailable"');
  });

  it("adds read-only menu catalog and project metadata discovery without execution", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const menuListSource = source.slice(source.indexOf("public static CommandExecutionResult EditorMenuList"), source.indexOf('[McpCommand(\n            "editor.menu.exists"'));
    const menuExistsSource = source.slice(source.indexOf("public static CommandExecutionResult EditorMenuExists"), source.indexOf('[McpCommand(\n            "editor.menu.execute"'));
    const menuCatalogSource = source.slice(source.indexOf("static string[] MenuCatalogPaths"), source.indexOf("static bool IsKnownSafeMenuPath"));

    expect(menuListSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "query")');
    expect(menuListSource).toContain('JsonUtil.ExtractInt(context.ArgsJson, "max_results", DefaultMenuCatalogLimit)');
    expect(menuListSource).toContain('"source\\":" + JsonUtil.String(catalogAvailable ? "unity_menu_catalog" : "known_safe_fallback")');
    expect(menuListSource).toContain('"catalog_available\\":');
    expect(menuListSource).toContain('"known_safe_menu_paths\\":');
    expect(menuListSource).toContain('"execute_command\\":\\"editor.menu.execute\\"');
    expect(menuListSource).toContain('"execute_policy_gated\\":true');
    expect(menuListSource).not.toContain("ExecuteMenuItem");
    expect(menuExistsSource).toContain("TryMenuCatalogContains(menuPath, out var existsInCatalog, out var existsCatalogTruncated, out var existsCatalogScanned, out var existsCatalogSource, out var existsCatalogRefreshAttempted, out var existsMenuItemAttributeScanned, out var existsLookupTimedOut)");
    expect(menuExistsSource).toContain('"exists\\":');
    expect(menuExistsSource).toContain('"known_safe\\":');
    expect(menuExistsSource).toContain('"catalog_refresh_attempted\\":');
    expect(menuExistsSource).toContain('"menu_item_attribute_scanned\\":');
    expect(menuExistsSource).toContain('"lookup_complete\\":');
    expect(menuExistsSource).toContain('"absence_provable\\":');
    expect(menuExistsSource).toContain('"lookup_status\\":');
    expect(menuExistsSource).toContain('"lookup_timeout\\":');
    expect(menuExistsSource).toContain('"can_attempt_execute\\":true');
    expect(menuExistsSource).not.toContain("ExecuteMenuItem");
    expect(menuCatalogSource).toContain('typeof(EditorApplication).Assembly.GetType("UnityEditor.Unsupported")');
    expect(menuCatalogSource).toContain('GetMethod("GetSubmenus"');
    expect(menuCatalogSource).toContain("TypeCache.GetMethodsWithAttribute<MenuItem>()");
    expect(menuCatalogSource).toContain("Stopwatch.StartNew()");
    expect(menuCatalogSource).toContain("LookupBudgetExceeded(stopwatch)");
    expect(source).toContain("const int MenuLookupBudgetMs = 750");
    expect(menuCatalogSource).toContain("NormalizeMenuItemAttributePath(attribute.menuItem)");
    expect(menuCatalogSource).toContain("!EditorApplication.isCompiling && !EditorApplication.isUpdating");
    expect(menuCatalogSource).toContain("if (!exists && !lookupTimedOut && IsUnityReadyForMenuCatalogRefresh())");
    expect(menuCatalogSource).toContain("truncated = truncated || attributeTruncated");
    expect(menuCatalogSource).not.toContain("!exists && !truncated && IsUnityReadyForMenuCatalogRefresh()");
    expect(menuCatalogSource).toContain("KnownMenuRoots");
    expect(menuCatalogSource).toContain("MaxMenuCatalogScan");
    expect(source).toContain("UnityEditorInternal.InternalEditorUtility.tags");
    expect(source).toContain("UnityEditorInternal.InternalEditorUtility.layers");
    expect(source).toContain("StringArrayJson");
  });

  it("adds write-risk tag add/remove commands with dry-run preflight", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const addSource = source.slice(source.indexOf('"editor.tags.add"'), source.indexOf('[McpCommand(\n            "editor.tags.remove"'));
    const removeSource = source.slice(source.indexOf('"editor.tags.remove"'), source.indexOf('[McpCommand(\n            "editor.layers.list"'));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult RequireTagName"), source.indexOf("static CommandExecutionResult RequireLayerName"));

    expect(source).toContain('const int MaxTagLength = 64');
    expect(source).toContain('const string TagModifySchemaJson =');
    expect(addSource).toContain('Risk = "write"');
    expect(addSource).toContain('Idempotency = "idempotent"');
    expect(addSource).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(addSource).toContain('UnityEditorInternal.InternalEditorUtility.AddTag(tagName)');
    expect(addSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false)');
    expect(addSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor AddTag\\"');
    expect(removeSource).toContain('UnityEditorInternal.InternalEditorUtility.RemoveTag(tagName)');
    expect(removeSource).toContain('IsBuiltInTag(tagName)');
    expect(removeSource).toContain('"protected_tag"');
    expect(removeSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor RemoveTag\\"');
    expect(helperSource).toContain('tagName.IndexOf(\'/\') >= 0');
    expect(helperSource).toContain('string.Equals(tagName, "Untagged", StringComparison.Ordinal)');
    expect(helperSource).not.toContain('EditorApplication.ExecuteMenuItem');
    expect(helperSource).not.toContain('AssetDatabase');
  });

  it("adds write-risk layer add/remove commands with dry-run preflight", () => {
    const source = fs.readFileSync(editorUtilityCommandsPath, "utf8");
    const addSource = source.slice(source.indexOf('"editor.layers.add"'), source.indexOf('[McpCommand(\n            "editor.layers.remove"'));
    const removeSource = source.slice(source.indexOf('"editor.layers.remove"'), source.indexOf("static CommandExecutionResult TryValidateAssetPath"));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult RequireLayerName"), source.indexOf("static string[] MenuCatalogPaths"));

    expect(source).toContain('const int MaxLayerLength = 64');
    expect(source).toContain('const string LayerModifySchemaJson =');
    expect(addSource).toContain('Risk = "write"');
    expect(addSource).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(addSource).toContain('FindEmptyUserLayerIndex(layersProperty)');
    expect(addSource).toContain('layersProperty.GetArrayElementAtIndex(emptyIndex).stringValue = layerName');
    expect(addSource).toContain('AssetDatabase.SaveAssets()');
    expect(addSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor AddLayer\\"');
    expect(removeSource).toContain('FindLayerIndex(layersProperty, layerName, 0, 7)');
    expect(removeSource).toContain('"protected_layer"');
    expect(removeSource).toContain('layersProperty.GetArrayElementAtIndex(existingIndex).stringValue = string.Empty');
    expect(removeSource).toContain('\\"compatible_with\\":\\"Unity.ManageEditor RemoveLayer\\"');
    expect(helperSource).toContain('AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset")');
    expect(helperSource).toContain('tagManager.FindProperty("layers")');
    expect(helperSource).toContain('for (var index = 8; index < layersProperty.arraySize && index < 32; index++)');
    expect(helperSource).not.toContain('EditorApplication.ExecuteMenuItem');
  });

  it("keeps menu execution confirmation-gated in the example policy", () => {
    const policy = JSON.parse(fs.readFileSync(path.join(process.cwd(), "config", "policy.example.json"), "utf8"));

    expect(policy.projects.game.allow).toContain("editor.menu.list");
    expect(policy.projects.game.allow).toContain("editor.menu.exists");
    expect(policy.projects.game.allow).toContain("editor.state.get");
    expect(policy.projects.game.allow).toContain("editor.prefab_stage.get");
    expect(policy.projects.game.allow).toContain("editor.active_tool.get");
    expect(policy.projects.game.ask).toContain("editor.active_tool.set");
    expect(policy.projects.game.ask).toContain("editor.window.position.set");
    expect(policy.projects.game.allow).toContain("editor.windows.list");
    expect(policy.projects.game.allow).toContain("editor.window.inspect");
    expect(policy.projects.game.allow).toContain("editor.window.screenshot");
    expect(policy.projects.game.allow).toContain("desktop.region.screenshot");
    expect(policy.projects.game.allow).toContain("editor.tags.list");
    expect(policy.projects.game.ask).toContain("editor.tags.add");
    expect(policy.projects.game.ask).toContain("editor.tags.remove");
    expect(policy.projects.game.allow).toContain("editor.layers.list");
    expect(policy.projects.game.ask).toContain("editor.layers.add");
    expect(policy.projects.game.ask).toContain("editor.layers.remove");
    expect(policy.projects.game.ask).toContain("editor.menu.execute");
    expect(policy.projects.game.ask).toContain("editor.quit");
    expect(policy.projects.game.allow).not.toContain("editor.menu.execute");
    expect(policy.projects.game.allow).not.toContain("editor.quit");
  });
});
