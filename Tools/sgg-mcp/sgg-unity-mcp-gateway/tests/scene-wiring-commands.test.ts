import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const sceneWiringCommandsPath = path.join(packageRoot, "Editor", "Commands", "SceneWiringCommands.cs");
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");

describe("Unity scene wiring command source-contract", () => {
  it("declares loaded-scene wiring command IDs and feature flags", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");

    expect(source).toContain('"mono_behaviour.add"');
    expect(source).toContain('"serialized.property.set"');
    expect(source).toContain('"serialized.object_reference.set"');
    expect(source).toContain('"serialized.array.edit"');
    expect(source).toContain('"unityevent.persistent_call.set"');
    expect(source).toContain('"prefab.mono_behaviour.add"');
    expect(source).toContain('"prefab.serialized.property.set"');
    expect(source).toContain('"prefab.serialized.object_reference.set"');
    expect(source).toContain('"prefab.serialized.array.edit"');
    expect(source).toContain('"prefab.authoring.apply_spec"');
    expect(source).toContain('const string MonoBehaviourFeatureFlag = "mono_behaviour_safe_writes"');
    expect(source).toContain('const string SerializedFeatureFlag = "serialized_safe_writes"');
    expect(source).toContain('const string UnityEventFeatureFlag = "unityevent_safe_writes"');
    expect(source).toContain('const string EditorSafeWriteFeatureFlag = "editor_safe_writes"');
    expect(source).toContain('const string PrefabCompositeFeatureFlag = "prefab_authoring_composite_safe_writes"');
    expect(gatewayExample).toContain('"mono_behaviour_safe_writes": false');
    expect(gatewayExample).toContain('"serialized_safe_writes": false');
    expect(gatewayExample).toContain('"unityevent_safe_writes": false');
    expect(gatewayExample).toContain('"prefab_authoring_composite_safe_writes": false');
  });

  it("keeps scene wiring writes dry-run-first, loaded-scene-only, and ask-gated", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");

    expect(source.match(/Risk = "write"/g)).toHaveLength(10);
    expect(source.match(/Idempotency = "unsafe"/g)).toHaveLength(5);
    expect(source.match(/Idempotency = "idempotent"/g)).toHaveLength(5);
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(source.match(/RequireEditing\("(?:mono_behaviour\.add|serialized\.property\.set|serialized\.object_reference\.set|serialized\.array\.edit|unityevent\.persistent_call\.set)", dryRun\)/g)).toHaveLength(5);
    expect(source).toContain('\\"reason\\":');
    expect(source).toContain('\\"required_editor_status\\":\\"editing\\"');
    expect(source).toContain('\\"execution_eligible\\":false');
    expect(source).toContain('scene_path must reference a loaded scene');
    expect(source).toContain('EditorSceneManager.MarkSceneDirty(scene)');
    expect(source).toContain('\\"scene_saved\\":false');
    expect(source).toContain('\\"saved_prefab\\":false');
    expect(source).toContain('\\"opens_scenes\\":false');
    expect(source).toContain('\\"writes_prefab_assets\\":false');
    expect(source).toContain('\\"loaded_scenes_only\\":true');
    expect(cli).toContain('"mono_behaviour.": "mono_behaviour_safe_writes"');
    expect(cli).toContain('"serialized.": "serialized_safe_writes"');
    expect(cli).toContain('"unityevent.": "unityevent_safe_writes"');
    expect(cli).toContain('"prefab.authoring.": "prefab_authoring_composite_safe_writes"');
    expect(cli).toContain('"prefab.mono_behaviour.": "mono_behaviour_safe_writes"');
    expect(cli).toContain('"prefab.serialized.": "serialized_safe_writes"');
    expect(policyExample).toContain('"mono_behaviour.add"');
    expect(policyExample).toContain('"serialized.property.set"');
    expect(policyExample).toContain('"serialized.object_reference.set"');
    expect(policyExample).toContain('"serialized.array.edit"');
    expect(policyExample).toContain('"unityevent.persistent_call.set"');
    expect(policyExample).toContain('"prefab.mono_behaviour.add"');
    expect(policyExample).toContain('"prefab.serialized.property.set"');
    expect(policyExample).toContain('"prefab.serialized.object_reference.set"');
    expect(policyExample).toContain('"prefab.serialized.array.edit"');
    expect(policyExample).toContain('"prefab.authoring.apply_spec"');
    expect(policyProfiles).toContain('"serialized.array.edit"');
    expect(policyProfiles).toContain('"unityevent.persistent_call.set"');
    expect(policyProfiles).toContain('"prefab.serialized.array.edit"');
    expect(policyProfiles).toContain('"prefab.authoring.apply_spec"');
    expect(policyProfiles).toContain('"mono_behaviour_safe_writes"');
    expect(policyProfiles).toContain('"serialized_safe_writes"');
    expect(policyProfiles).toContain('"unityevent_safe_writes"');
    expect(policyProfiles).toContain('"prefab_authoring_composite_safe_writes"');
  });

  it("keeps prefab wiring writes dry-run-first, hash-gated, and prefab-asset-only", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult PrefabMonoBehaviourAdd");
    expect(source).toContain("public static CommandExecutionResult PrefabSerializedPropertySet");
    expect(source).toContain("public static CommandExecutionResult PrefabSerializedObjectReferenceSet");
    expect(source).toContain("public static CommandExecutionResult PrefabSerializedArrayEdit");
    expect(source).toContain("PrefabUtility.LoadPrefabContents(prefabPath)");
    expect(source).toContain("PrefabUtility.SaveAsPrefabAsset(root, prefabPath)");
    expect(source).toContain("PrefabUtility.UnloadPrefabContents(root)");
    expect(source).toContain("RequireFeatureEnabled(context.SettingsJson, EditorSafeWriteFeatureFlag");
    expect(source).toContain("PrefabWritePreconditionHash(prefabPath");
    expect(source).toContain("RequirePrefabWritePrecondition(context.ArgsJson, prefabPath");
    expect(source).toContain('PathGuard.RequireUnityAssetPath(rawPath)');
    expect(source).toContain('prefab_path must be under Assets/');
    expect(source).toContain('\\"writes_prefab_assets\\":true');
    expect(source).toContain('\\"opens_scenes\\":false');
    expect(source).toContain('\\"scene_saved\\":false');
    expect(source).not.toContain("EditorSceneManager.OpenScene");
    expect(source).not.toContain("EditorSceneManager.SaveScene");
  });

  it("composes prefab authoring steps through focused prefab commands", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult PrefabAuthoringApplySpec");
    expect(source).toContain("ExtractPrefabAuthoringSteps");
    expect(source).toContain("PlanPrefabAuthoringSpec");
    expect(source).toContain("ApplyPrefabAuthoringSteps");
    expect(source).toContain("InvokePrefabAuthoringStep");
    expect(source).toContain('AddRequiredFeature(required, PrefabCompositeFeatureFlag)');
    expect(source).toContain('AddRequiredFeature(required, EditorSafeWriteFeatureFlag)');
    expect(source).toContain('StartsWith("prefab.mono_behaviour."');
    expect(source).toContain('StartsWith("prefab.serialized."');
    expect(source).toContain('"prefab.ugui.object.create"');
    expect(source).toContain('"prefab.ugui.component.add"');
    expect(source).toContain('"prefab.ugui.text.set"');
    expect(source).toContain('"confirm_apply"');
    expect(source).toContain('confirm_apply must be true when prefab.authoring.apply_spec runs with dry_run:false');
    expect(source).toContain('Sha256Prefixed("prefab.authoring.apply_spec\\n"');
    expect(source).toContain('\\"partial_failure\\"');
    expect(source).toContain('\\"saved_prefab\\"');
  });

  it("adds project MonoBehaviours only by exact project assembly selector", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult MonoBehaviourAdd");
    expect(source).toContain("full_type_name and assembly_name are required");
    expect(source).toContain("type must be a concrete MonoBehaviour");
    expect(source).toContain("settings_allowlist_exact_selector");
    expect(source).toContain("project_assembly_heuristic_no_settings_allowlist");
    expect(source).toContain("mono_behaviour.add requires an exact scene_authoring.mono_behaviour_allowlist match");
    expect(source).toContain("ResolveProjectMonoBehaviourType(context.ArgsJson, context.SettingsJson");
    expect(source).toContain("mono_behaviour.add only allows exact project/source assemblies");
    expect(source).toContain("!assemblyName.StartsWith(\"Unity\"");
    expect(source).toContain("assemblyName != \"com.sungeargames.unity-mcp\"");
    expect(source).toContain("Undo.AddComponent(target, type)");
    expect(source).toContain('\\"serialized_fields_configured\\":false');
  });

  it("sets bounded primitive serialized properties with precondition hashes", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");
    const propertySet = source.slice(source.indexOf("public static CommandExecutionResult SerializedPropertySet"), source.indexOf('[McpCommand("serialized.object_reference.set"'));

    expect(source).toContain("public static CommandExecutionResult SerializedPropertySet");
    expect(source).toContain("Provide exactly one value_* field");
    expect(source).toContain("arrays/lists are not supported by this first serialized write slice");
    expect(source).toContain("managed references are not supported by this first serialized write slice");
    expect(source).toContain("SerializedPropertyType.Boolean");
    expect(source).toContain("SerializedPropertyType.Integer");
    expect(source).toContain("SerializedPropertyType.Float");
    expect(source).toContain("SerializedPropertyType.String");
    expect(source).toContain("SerializedPropertyType.Enum");
    expect(source).toContain("SerializedPropertyType.Vector2");
    expect(source).toContain("SerializedPropertyType.Vector3");
    expect(source).toContain("SerializedPropertyType.Vector4");
    expect(source).toContain("SerializedPropertyType.Color");
    expect(source).toContain('RequirePrecondition(context.ArgsJson, hash, "serialized.property.set")');
    expect(source).toContain("precondition_hash is required when " + "\" + command + \"");
    expect(propertySet.indexOf('RequireEditing("serialized.property.set", dryRun)')).toBeLessThan(propertySet.indexOf("RequireSerializedTarget"));
    expect(source).toContain('"serialized_scene_write.v2\\n"');
    expect(source).toContain("value.GetHashCode()");
    expect(source).toContain("RuntimeInstanceId(context)");
    expect(source).toContain("GlobalObjectId.GetGlobalObjectIdSlow(value)");
    expect(source).toContain("ConditionalWeakTable<UnityEngine.Object, LoadedObjectGeneration>");
    expect(source).toContain('\\"identity_hash\\":');
    expect(source).toContain("currentValueHash");
    expect(source).toContain("requestedValueHash");
    expect(source).toContain('CommandExecutionResult.Error("serialized_write_failed"');
    expect(source).toContain('\\"value_data_included\\":false');
    expect(source).toContain('\\"dry_run_authorizes_execution\\":false');
    expect(source).toContain('ApplySerializedMutation("serialized.property.set", "Set Serialized Property"');
  });

  it("sets object references from scene objects/components or Unity-relative assets", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult SerializedObjectReferenceSet");
    expect(source).toContain("SerializedPropertyType.ObjectReference");
    expect(source).toContain("Provide exactly one of clear_reference, source_object_path, or source_asset_path");
    expect(source).toContain("PathGuard.RequireUnityAssetPath(sourceAssetPath)");
    expect(source).toContain("AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path)");
    expect(source).toContain("source reference is not assignable to the serialized field type");
    expect(source).toContain('RequirePrecondition(context.ArgsJson, hash, "serialized.object_reference.set")');
    expect(source).toContain('ApplySerializedMutation("serialized.object_reference.set", "Set Serialized Object Reference"');
  });

  it("edits bounded serialized arrays with precondition hashes", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult SerializedArrayEdit");
    expect(source).toContain("SerializedArrayEditSchemaJson");
    expect(source).toContain("append");
    expect(source).toContain("insert");
    expect(source).toContain("remove_at");
    expect(source).toContain("clear");
    expect(source).toContain("move");
    expect(source).toContain("const int MaxSerializedArrayElements = 1000");
    expect(source).toContain("const int MaxSerializedArrayReturnedElements = 64");
    expect(source).toContain("property_path must reference a serialized array/list field");
    expect(source).toContain("managed references are not supported by serialized.array.edit");
    expect(source).toContain("nested arrays/lists are not supported by serialized.array.edit");
    expect(source).toContain("append/insert require an existing array element for type inference");
    expect(source).toContain("object-reference array elements require source_object_path, source_asset_path, or clear_reference");
    expect(source).toContain('RequirePrecondition(context.ArgsJson, hash, "serialized.array.edit")');
    expect(source).toContain('ApplySerializedMutation("serialized.array.edit", "Edit Serialized Array"');
    expect(source).toContain("InsertArrayElementAtIndex");
    expect(source).toContain("DeleteArrayElementAtIndex");
    expect(source).toContain("ClearArray");
    expect(source).toContain("MoveArrayElement");
  });

  it("sets UnityEvent persistent listener state without mutating calls", () => {
    const source = fs.readFileSync(sceneWiringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult UnityEventPersistentCallSet");
    expect(source).toContain("UnityEventPersistentCallSetSchemaJson");
    expect(source).toContain("UnityEngine.Events");
    expect(source).toContain('FindPropertyRelative("m_PersistentCalls.m_Calls")');
    expect(source).toContain('FindPropertyRelative("m_CallState")');
    expect(source).toContain("NormalizeEventMemberName");
    expect(source).toContain("GetPersistentEventCount");
    expect(source).toContain("GetPersistentTarget");
    expect(source).toContain("GetPersistentMethodName");
    expect(source).toContain("GetPersistentListenerState");
    expect(source).toContain('RequirePrecondition(context.ArgsJson, hash, "unityevent.persistent_call.set")');
    expect(source).toContain('ApplySerializedMutation("unityevent.persistent_call.set", "Set UnityEvent Persistent Call"');
    expect(source).toContain("PreparePersistentListenerStateSerialized");
    expect(source).toContain('\\"persistent_call_array_mutation\\":false');
    expect(source).toContain('\\"target_or_method_mutation\\":false');
    expect(source).not.toContain("AddPersistentListener");
    expect(source).not.toContain("RemovePersistentListener");
    expect(source).not.toContain("UnityEventTools.SetPersistentListenerState");
  });
});
