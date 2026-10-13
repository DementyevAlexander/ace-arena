import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const semanticCommandsPath = path.join(packageRoot, "Editor", "Commands", "SemanticInspectionCommands.cs");
const jsonUtilPath = path.join(packageRoot, "Editor", "Commands", "JsonUtil.cs");

describe("Unity semantic inspection command source-contract", () => {
  it("declares the first scene and prefab inspection command IDs", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");

    expect(source).toContain('"scene.active"');
    expect(source).toContain('"scene.build_settings"');
    expect(source).toContain('"scene.loaded"');
    expect(source).toContain('"scene.hierarchy.active"');
    expect(source).toContain('"component.types.search"');
    expect(source).toContain('"component.type.inspect"');
    expect(source).toContain('"scene.visible_objects"');
    expect(source).toContain('"scene.hierarchy"');
    expect(source).toContain('"scene.search"');
    expect(source).toContain('"gameobject.builtin_assets"');
    expect(source).toContain('"gameobject.bounds"');
    expect(source).toContain('"scene.inspect_object"');
    expect(source).toContain('"scene.inspect_component"');
    expect(source).toContain('"prefab.hierarchy"');
    expect(source).toContain('"prefab.inspect_object"');
    expect(source).toContain('"prefab.inspect_component"');
    expect(source).toContain('"asset.components.list"');
  });

  it("keeps semantic inspection read-only and feature-gated", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "semantic_scene_inspection"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(17);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(14);
    expect(source).toContain('"scene.active"');
    expect(source).toContain('"scene.build_settings"');
    expect(source).toContain('"scene.loaded"');
    expect(source).toContain('OutputLimitJson = SceneBuildSettingsOutputLimitJson)]');
    expect(source).toContain('OutputLimitJson = SceneLoadedOutputLimitJson)]');
    expect(source).toContain('OutputLimitJson = TreeOutputLimitJson');
    expect(source).toContain('OutputLimitJson = SceneVisibleObjectsOutputLimitJson');
    expect(source).toContain('OutputLimitJson = BuiltinAssetsOutputLimitJson');
    expect(source).toContain('OutputLimitJson = GameObjectBoundsOutputLimitJson');
    expect(source).toContain('OutputLimitJson = AssetComponentsOutputLimitJson');
  });

  it("adds active-scene status, build-settings status, bounded loaded-scene and component-type discovery", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");

    expect(source).toContain('const string SceneActiveSchemaJson =');
    expect(source).toContain('const string SceneBuildSettingsSchemaJson =');
    expect(source).toContain('const string SceneBuildSettingsOutputLimitJson = "{\\"max_items\\":200}"');
    expect(source).toContain('const string SceneActiveHierarchySchemaJson =');
    expect(source).toContain("public static CommandExecutionResult SceneActive");
    expect(source).toContain("public static CommandExecutionResult SceneBuildSettings");
    expect(source).toContain("public static CommandExecutionResult SceneActiveHierarchy");
    expect(source).toContain("ActiveSceneJson()");
    expect(source).toContain("BuildSettingsScenesJson()");
    expect(source).toContain("ActiveHierarchyJson(scene, context)");
    expect(source).toContain("SceneManager.GetActiveScene()");
    expect(source).toContain("EditorBuildSettings.scenes");
    expect(source).toContain('\\"identity_status\\"');
    expect(source).toContain('\\"identity_unknown\\"');
    expect(source).toContain('\\"identity_confidence\\"');
    expect(source).toContain('unnamed_temp_identity_unknown');
    expect(source).toContain('IsActiveSceneIdentityUnknown');
    expect(source).toContain('string.IsNullOrEmpty(scene.path)');
    expect(source).toContain('string.IsNullOrEmpty(scene.name)');
    expect(source).toContain('scene.buildIndex < 0');
    expect(source).toContain('\\"compatible_with\\":\\"Unity.ManageScene GetActive\\"');
    expect(source).toContain('\\"compatible_with\\":\\"Unity.ManageScene GetBuildSettings\\"');
    expect(source).toContain('\\"compatible_with\\":\\"Unity.ManageScene GetHierarchy active scene\\"');
    expect(source).toContain('\\"has_active_scene\\"');
    expect(source).toContain('\\"is_valid\\"');
    expect(source).toContain('\\"scene_path\\"');
    expect(source).toContain('\\"scene_name\\"');
    expect(source).toContain('\\"is_dirty\\"');
    expect(source).toContain('\\"has_unsaved_changes\\"');
    expect(source).toContain('\\"requires_save\\"');
    expect(source).toContain('\\"root_count\\"');
    expect(source).toContain('\\"build_index\\"');
    expect(source).toContain('\\"build_settings_index\\"');
    expect(source).toContain('\\"enabled_build_index\\"');
    expect(source).toContain('const string SceneLoadedSchemaJson =');
    expect(source).toContain('\\"maximum\\":100');
    expect(source).toContain('const string ComponentTypesSearchSchemaJson =');
    expect(source).toContain('const string ComponentTypeInspectSchemaJson =');
    expect(source).toContain('\\"maximum\\":200');
    expect(source).toContain('\\"maximum\\":50');
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 100)');
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 200)');
    expect(source).toContain('\\"scenes\\"');
    expect(source).toContain('\\"types\\"');
    expect(source).toContain('\\"candidates\\"');
    expect(source).toContain('\\"truncated\\"');
    expect(source).toContain('\\"is_obsolete\\"');
    expect(source).toContain('typeof(Component).IsAssignableFrom(type)');
    expect(source).toContain('result.Sort((left, right) =>');
  });

  it("adds read-only component type inspection for safe property-write preparation", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"component.type.inspect"'), source.indexOf('[McpCommand(\n            "scene.visible_objects"'));
    const helperSource = source.slice(source.indexOf("static string ComponentTypeInspectJson"), source.indexOf("static string VisibleObjectsJson"));

    expect(commandSource).toContain('Risk = "read"');
    expect(commandSource).toContain('Idempotency = "safe"');
    expect(commandSource).toContain('SchemaJson = ComponentTypeInspectSchemaJson');
    expect(commandSource).toContain('OutputLimitJson = ComponentTypeInspectOutputLimitJson');
    expect(commandSource).toContain('FeatureFlag = FeatureFlag');
    expect(helperSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "type_name")');
    expect(helperSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "full_type_name")');
    expect(helperSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "assembly_name")');
    expect(helperSource).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 20), 1, 50)');
    expect(helperSource).toContain('typeof(MonoBehaviour).IsAssignableFrom(type)');
    expect(helperSource).toContain('typeof(Behaviour).IsAssignableFrom(type)');
    expect(helperSource).toContain('typeof(Renderer).IsAssignableFrom(type)');
    expect(helperSource).toContain('typeof(Collider).IsAssignableFrom(type)');
    expect(helperSource).toContain('typeof(Light).IsAssignableFrom(type)');
    expect(helperSource).toContain('typeof(Camera).IsAssignableFrom(type)');
    expect(helperSource).toContain('typeof(Rigidbody).IsAssignableFrom(type)');
    expect(helperSource).toContain('\\"property_schema_available\\"');
    expect(helperSource).toContain('!typeof(MonoBehaviour).IsAssignableFrom(type)');
    expect(helperSource).toContain('\\"ambiguous\\"');
    expect(helperSource).toContain('\\"requires_exact_selector\\"');
    expect(helperSource).not.toContain('SerializedObject');
    expect(helperSource).not.toContain('EditorSceneManager.OpenScene');
    expect(helperSource).not.toContain('EditorSceneManager.SaveScene');
    expect(helperSource).not.toContain('AssetDatabase');
    expect(helperSource).not.toContain('Undo.');
  });

  it("keeps active-scene status read-only and pathless", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const activeSource = source.slice(source.indexOf("public static CommandExecutionResult SceneActive"), source.indexOf('[McpCommand(\n            "scene.loaded"'));

    expect(activeSource).toContain("ActiveSceneJson()");
    expect(activeSource).not.toContain("EditorSceneManager.OpenScene");
    expect(activeSource).not.toContain("EditorSceneManager.SaveScene");
    expect(activeSource).not.toContain("AssetDatabase");
    expect(activeSource).not.toContain("PathGuard");
    expect(activeSource).not.toContain("FeatureFlag = FeatureFlag");
  });

  it("keeps build-settings scene status read-only and pathless", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const buildSettingsCommandSource = source.slice(source.indexOf("public static CommandExecutionResult SceneBuildSettings"), source.indexOf('[McpCommand(\n            "scene.loaded"'));
    const buildSettingsSource = source.slice(source.indexOf("static string BuildSettingsScenesJson"), source.indexOf("static string ActiveSceneJson"));

    expect(buildSettingsCommandSource).toContain("BuildSettingsScenesJson()");
    expect(buildSettingsSource).toContain("BuildSettingsScenesJson()");
    expect(buildSettingsSource).toContain("EditorBuildSettings.scenes");
    expect(buildSettingsSource).toContain("scene.guid.ToString()");
    expect(buildSettingsSource).not.toContain("EditorBuildSettings.scenes =");
    expect(buildSettingsSource).not.toContain("EditorSceneManager.OpenScene");
    expect(buildSettingsSource).not.toContain("EditorSceneManager.SaveScene");
    expect(buildSettingsSource).not.toContain("AssetDatabase");
    expect(buildSettingsSource).not.toContain("PathGuard");
    expect(buildSettingsSource).not.toContain("FeatureFlag = FeatureFlag");
  });

  it("adds bounded active-scene hierarchy without path input or scene mutation", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult SceneActiveHierarchy"), source.indexOf('[McpCommand(\n            "component.types.search"'));
    const helperSource = source.slice(source.indexOf("static string ActiveHierarchyJson"), source.indexOf("static string SearchJson"));

    expect(source).toContain('const string SceneActiveHierarchySchemaJson = "{\\"type\\":\\"object\\",\\"properties\\":{\\"max_depth\\"');
    expect(source).toContain('SceneActiveHierarchySchemaJson');
    expect(commandSource).toContain("SceneManager.GetActiveScene()");
    expect(commandSource).toContain("!scene.IsValid() || !scene.isLoaded");
    expect(helperSource).toContain("scene.GetRootGameObjects()");
    expect(helperSource).toContain("AppendHierarchy(root");
    expect(helperSource).toContain('\\"source\\":\\"active_scene\\"');
    expect(helperSource).toContain('string.IsNullOrEmpty(scene.path) ? "null" : JsonUtil.String(scene.path)');
    expect(helperSource).toContain('\\"compatible_with\\":\\"Unity.ManageScene GetHierarchy active scene\\"');
    expect(commandSource).not.toContain("EditorSceneManager.OpenScene");
    expect(helperSource).not.toContain("EditorSceneManager.OpenScene");
    expect(commandSource).not.toContain("EditorSceneManager.SaveScene");
    expect(helperSource).not.toContain("EditorSceneManager.SaveScene");
    expect(commandSource).not.toContain("AssetDatabase");
    expect(helperSource).not.toContain("AssetDatabase");
    expect(commandSource).not.toContain("PathGuard");
    expect(helperSource).not.toContain("PathGuard");
  });

  it("adds bounded Scene view visible objects parity without screenshots or writes", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult SceneVisibleObjects"), source.indexOf('[McpCommand(\n            "scene.hierarchy"'));
    const resultSource = source.slice(source.indexOf("static string VisibleObjectsJson"), source.indexOf("static List<Type> ComponentTypes"));
    const helperSource = source.slice(source.indexOf("static List<VisibleSceneObject> VisibleSceneObjects"), source.indexOf("static CommandExecutionResult InspectObjectResult"));

    expect(source).toContain('const string SceneVisibleObjectsSchemaJson =');
    expect(source).toContain('const string SceneVisibleObjectsOutputLimitJson = "{\\"max_results\\":200}"');
    expect(source).toContain('"scene.visible_objects"');
    expect(source).toContain('ResultShape = "bounded_list"');
    expect(source).toContain('Tags = new string[] { "scene", "camera", "semantic-inspection", "unity-ai-assistant" }');
    expect(commandSource).toContain("ActiveSceneView()");
    expect(commandSource).toContain("sceneView.camera");
    expect(resultSource).toContain('JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0)');
    expect(resultSource).toContain('JsonUtil.ExtractInt(context.ArgsJson, "max_results", 20)');
    expect(resultSource).toContain('\\"compatible_with\\":\\"Unity.Camera.GetVisibleObjects\\"');
    expect(helperSource).toContain("Resources.FindObjectsOfTypeAll<Renderer>()");
    expect(helperSource).toContain("GeometryUtility.CalculateFrustumPlanes(camera)");
    expect(helperSource).toContain("GeometryUtility.TestPlanesAABB(planes, bounds)");
    expect(helperSource).toContain("RuntimeObjectIdentity(gameObject)");
    expect(source).toContain("static string RuntimeObjectIdentity(UnityEngine.Object unityObject)");
    expect(source).toContain("#if UNITY_6000_4_OR_NEWER");
    expect(source).toContain("UnityEngine.EntityId.ToULong(unityObject.GetEntityId())");
    expect(helperSource).toContain("camera.WorldToViewportPoint");
    expect(helperSource).toContain("AppendRuntimeObjectIds");
    expect(helperSource).toContain("AppendComponents(builder, gameObject)");
    expect(commandSource).not.toContain("CaptureCamera");
    expect(resultSource).not.toContain("RenderTexture");
    expect(helperSource).not.toContain("Texture2D");
    expect(helperSource).not.toContain("File.");
    expect(helperSource).not.toContain("Directory.");
    expect(helperSource).not.toContain("EditorSceneManager.SaveScene");
  });

  it("extends scene.search toward Unity FindSceneObjects without writes", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult SceneSearch"), source.indexOf('[McpCommand(\n            "gameobject.builtin_assets"'));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult BuildSceneSearchOptions"), source.indexOf("static void AppendNode"));

    expect(source).toContain('const string SceneSearchSchemaJson =');
    expect(source).toContain('\\"scope\\"');
    expect(source).toContain('\\"scene_path\\",\\"active\\",\\"loaded\\"');
    expect(source).toContain('\\"component_type\\"');
    expect(source).toContain('\\"path_exact\\"');
    expect(source).toContain('\\"active_state\\"');
    expect(source).toContain('\\"start_index\\"');
    expect(commandSource).toContain('BuildSceneSearchOptions(context, out var options)');
    expect(commandSource).toContain('SceneManager.GetActiveScene()');
    expect(commandSource).toContain('LoadedScenes()');
    expect(commandSource).toContain('WithScene(scenePath');
    expect(helperSource).toContain('options.Query.StartsWith("t:", StringComparison.OrdinalIgnoreCase)');
    expect(helperSource).toContain('NormalizeSearchPath(JsonUtil.ExtractString(context.ArgsJson, "path_prefix"))');
    expect(helperSource).toContain('SceneSearchMatches(gameObject, objectPath, options)');
    expect(helperSource).toContain('HasComponentType(gameObject, options.ComponentType)');
    expect(helperSource).toContain('LayerMatches(gameObject, options.Layer)');
    expect(helperSource).toContain('AppendSearchNode');
    expect(helperSource).toContain('active_scene_identity_unknown');
    expect(helperSource).toContain('absence_provable');
    expect(helperSource).toContain('IsActiveSceneIdentityUnknown(scenes[0])');
    expect(helperSource).toContain('SearchWarnings(truncated, openedForInspection, scenes.Length == 0, returned == 0, activeSceneIdentityUnknown)');
    expect(helperSource).toContain('SearchActions(truncated, activeSceneIdentityUnknown)');
    expect(source).toContain('this no-match result is not proof of absence from the intended production scene');
    expect(source).toContain('new[] { "scene.active", "scene.loaded", "scene.build_settings", "scene.open", "scene.search" }');
    expect(helperSource).toContain('\\"compatible_with\\":\\"Unity.FindSceneObjects\\"');
    expect(helperSource).toContain('\\"next_start_index\\"');
    expect(helperSource).not.toContain('EditorSceneManager.SaveScene');
    expect(helperSource).not.toContain('AssetDatabase.ImportAsset');
  });

  it("hardens semantic scene read-first responses with workflow metadata", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");

    expect(source).toContain("SceneHierarchyJson(scenePath, scene, context, openedForInspection)");
    expect(source).toContain("HierarchySelectorJson");
    expect(source).toContain("SearchSelectorJson");
    expect(source).toContain("SceneMetadataJson");
    expect(source).toContain("SceneMetadataListJson");
    expect(source).toContain('\\"selector\\"');
    expect(source).toContain('\\"returned_node_count\\"');
    expect(source).toContain('\\"component_count\\"');
    expect(source).toContain('\\"opened_for_inspection\\"');
    expect(source).toContain('\\"warnings\\"');
    expect(source).toContain('\\"next_valid_actions\\"');
    expect(source).toContain("ObjectInspectWarnings");
    expect(source).toContain("ComponentInspectWarnings");
    expect(source).toContain("CountPathMatches");
    expect(source).toContain("CountComponentMatches");
    expect(source).toContain('\\"component_ambiguous\\"');
    expect(source).toContain('\\"requires_exact_selector\\"');
    expect(source).toContain('NormalizeSearchPath(JsonUtil.ExtractString(context.ArgsJson, "path_prefix"))');
    expect(source).toContain('builder.Append(\',\');\n            AppendGameObjectFields(builder, target, normalizedObjectPath, includeComponents);');
  });

  it("adds bounded prefab asset component listing without property dumps or writes", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult AssetComponentsList"), source.indexOf("static CommandExecutionResult WithScene"));
    const resultSource = source.slice(source.indexOf("static CommandExecutionResult AssetComponentsResult"), source.indexOf("static int FindComponentIndex"));
    const helperSource = source.slice(source.indexOf("static void AppendComponentObjectsRecursive"), source.indexOf("static GameObject FindByPath"));

    expect(source).toContain('const string AssetComponentsSchemaJson =');
    expect(source).toContain('const string AssetComponentsOutputLimitJson = "{\\"max_objects\\":200,\\"max_components\\":1000}"');
    expect(source).toContain('\\"asset_path\\"');
    expect(source).toContain('\\"object_path\\"');
    expect(source).toContain('\\"include_inactive\\"');
    expect(source).toContain('\\"max_objects\\"');
    expect(source).toContain('\\"max_components\\"');
    expect(commandSource).toContain('RequirePrefabAssetPath(context.ArgsJson, "asset_path", out var prefabPath)');
    expect(commandSource).toContain("WithPrefab(prefabPath");
    expect(resultSource).toContain("AppendComponentObjectsRecursive");
    expect(resultSource).toContain('\\"compatible_with\\":\\"Unity.ManageAsset GetComponents\\"');
    expect(helperSource).toContain("AppendComponentSummary");
    expect(helperSource).toContain("gameObject.GetComponents<Component>()");
    expect(helperSource).toContain('\\"is_missing_script\\"');
    expect(resultSource).not.toContain("SerializedObject");
    expect(helperSource).not.toContain("SerializedObject");
    expect(commandSource).not.toContain("Save");
    expect(helperSource).not.toContain("Save");
    expect(commandSource).not.toContain("AssetDatabase.ImportAsset");
    expect(helperSource).not.toContain("AssetDatabase.ImportAsset");
    expect(helperSource).not.toContain("EditorSceneManager.OpenScene");
  });

  it("adds read-only GameObject renderer bounds parity without instance-id lookup or writes", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult GameObjectBounds"), source.indexOf('[McpCommand(\n            "scene.inspect_object"'));
    const resultSource = source.slice(source.indexOf("static CommandExecutionResult GameObjectBoundsResult"), source.indexOf("static CommandExecutionResult InspectComponentResult"));

    expect(source).toContain('const string GameObjectBoundsSchemaJson =');
    expect(source).toContain('const string GameObjectBoundsOutputLimitJson = "{\\"max_objects\\":1}"');
    expect(source).toContain('"gameobject.bounds"');
    expect(source).toContain('ResultShape = "object_bounds"');
    expect(source).toContain('Tags = new string[] { "gameobject", "bounds", "semantic-inspection", "unity-ai-assistant" }');
    expect(commandSource).toContain('RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath)');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "object_path")');
    expect(commandSource).toContain('WithScene(scenePath');
    expect(resultSource).toContain("FindByPath(roots, normalizedObjectPath)");
    expect(resultSource).toContain("TryComputeRendererWorldBounds");
    expect(resultSource).toContain('target.GetComponentsInChildren<Renderer>(includeInactive)');
    expect(resultSource).toContain("bounds.Encapsulate(renderer.bounds)");
    expect(resultSource).toContain("RoundedFloat");
    expect(resultSource).toContain('\\"compatible_with\\":\\"Unity.GameObject.GetGameObjectBounds\\"');
    expect(resultSource).not.toContain("EditorUtility.InstanceIDToObject");
    expect(resultSource).not.toContain("EntityIdToObject");
    expect(commandSource).not.toContain("EditorSceneManager.SaveScene");
    expect(resultSource).not.toContain("EditorSceneManager.SaveScene");
    expect(commandSource).not.toContain("AssetDatabase.ImportAsset");
    expect(resultSource).not.toContain("AssetDatabase.ImportAsset");
  });

  it("adds read-only built-in GameObject asset lookup parity without temp object mutation", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"gameobject.builtin_assets"'), source.indexOf('[McpCommand(\n            "gameobject.bounds"'));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult BuiltinAssetsResult"), source.indexOf("static CommandExecutionResult GameObjectBoundsResult"));

    expect(source).toContain('const string BuiltinAssetsSchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('\\"PrimitiveMesh\\"');
    expect(source).toContain('\\"DefaultMaterial\\"');
    expect(source).toContain('const string BuiltinAssetsOutputLimitJson = "{\\"max_assets\\":6}"');
    expect(commandSource).toContain('Risk = "read"');
    expect(commandSource).toContain('Idempotency = "safe"');
    expect(commandSource).toContain('Tags = new string[] { "gameobject", "asset", "semantic-inspection", "unity-ai-assistant" }');
    expect(commandSource).toContain('ResultShape = "bounded_list"');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "asset_type")');
    expect(helperSource).toContain('Resources.GetBuiltinResource<Material>("Default-Material.mat")');
    expect(helperSource).toContain('Resources.GetBuiltinResource<Mesh>(primitiveName + ".fbx")');
    expect(helperSource).toContain("AppendRuntimeObjectIds(builder, asset)");
    expect(helperSource).toContain("Unity.GameObject.GetBuiltinAssets");
    expect(helperSource).not.toContain("GameObject.CreatePrimitive");
    expect(helperSource).not.toContain("DestroyImmediate");
    expect(helperSource).not.toContain("AssetDatabase");
    expect(helperSource).not.toContain("File.");
  });

  it("validates Unity-relative scene and prefab paths before Unity APIs run", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");

    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawPath)");
    expect(source).toContain('EndsWith(".unity", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain("EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive)");
    expect(source).toContain("PrefabUtility.LoadPrefabContents(prefabPath)");
    expect(source).toContain("PrefabUtility.UnloadPrefabContents(root)");
  });

  it("exposes bounded output controls in schemas and descriptors", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");

    expect(source).toContain('\\"maximum\\":16');
    expect(source).toContain('\\"maximum\\":1000');
    expect(source).toContain("Clamp(JsonUtil.ExtractInt(context.ArgsJson, \"max_depth\", 3), 0, 16)");
    expect(source).toContain("Clamp(JsonUtil.ExtractInt(context.ArgsJson, \"max_nodes\", 200), 1, 1000)");
    expect(source).toContain("OutputLimitJson = TreeOutputLimitJson");
    expect(source).toContain("OutputLimitJson = ListOutputLimitJson");
    expect(source).toContain("OutputLimitJson = SceneLoadedOutputLimitJson");
    expect(source).toContain("OutputLimitJson = ComponentTypesOutputLimitJson");
    expect(source).toContain("OutputLimitJson = BuiltinAssetsOutputLimitJson");
    expect(source).toContain("OutputLimitJson = ObjectOutputLimitJson");
    expect(source).toContain("OutputLimitJson = ComponentOutputLimitJson");
  });

  it("documents new priority commands in English and Russian command docs", () => {
    const docs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "reference", "commands.md"), "utf8");
    const ruDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "reference", "commands.md"), "utf8");

    for (const text of [docs, ruDocs]) {
      expect(text).toContain("`scene.active`");
      expect(text).toContain("`scene.build_settings`");
      expect(text).toContain("`scene.loaded`");
      expect(text).toContain("`scene.hierarchy.active`");
      expect(text).toContain("`scene.visible_objects`");
      expect(text).toContain("`gameobject.builtin_assets`");
      expect(text).toContain("`gameobject.bounds`");
      expect(text).toContain("`asset.components.list`");
      expect(text).toContain("`component.types.search`");
      expect(text).toContain("`component.type.inspect`");
      expect(text).toContain("max_results");
      expect(text).toContain("include_obsolete");
    }
  });

  it("supports focused component inspection after object drill-down", () => {
    const source = fs.readFileSync(semanticCommandsPath, "utf8");

    expect(source).toContain("FindComponentIndex");
    expect(source).toContain("component_type");
    expect(source).toContain("component_index");
    expect(source).toContain("SerializedObject(component)");
    expect(source).toContain("SerializedPropertyType.ObjectReference");
    expect(source).toContain("SerializedPropertyType.Vector2");
    expect(source).toContain("SerializedPropertyType.Vector3");
    expect(source).toContain("SerializedPropertyType.Vector4");
    expect(source).toContain("SerializedPropertyType.Quaternion");
    expect(source).toContain("property.quaternionValue");
    expect(source).toContain('value.ToString("R", CultureInfo.InvariantCulture)');
    expect(source).toContain("Append(SerializedPropertyValueJson(property))");
  });

  it("supports boolean command arguments used by include_components", () => {
    const source = fs.readFileSync(jsonUtilPath, "utf8");

    expect(source).toContain("ExtractBool");
    expect(source).toContain('json.IndexOf("true"');
    expect(source).toContain('json.IndexOf("false"');
  });
});
