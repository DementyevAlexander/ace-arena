using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class SceneWiringCommands
    {
        const string MonoBehaviourFeatureFlag = "mono_behaviour_safe_writes";
        const string SerializedFeatureFlag = "serialized_safe_writes";
        const string UnityEventFeatureFlag = "unityevent_safe_writes";
        const string EditorSafeWriteFeatureFlag = "editor_safe_writes";
        const string PrefabCompositeFeatureFlag = "prefab_authoring_composite_safe_writes";
        const string MonoBehaviourAddSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"scene_path\",\"object_path\",\"full_type_name\",\"assembly_name\"],\"additionalProperties\":false}";
        const string SerializedPropertySetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"value_bool\":{\"type\":\"boolean\"},\"value_int\":{\"type\":\"integer\"},\"value_float\":{\"type\":\"number\"},\"value_string\":{\"type\":\"string\",\"maxLength\":4096},\"value_enum\":{\"type\":\"string\",\"maxLength\":256},\"value_vector2\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"value_vector3\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"value_vector4\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"value_color\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\",\"property_path\"],\"additionalProperties\":false}";
        const string SerializedObjectReferenceSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"source_object_path\":{\"type\":\"string\"},\"source_component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"source_component_type\":{\"type\":\"string\"},\"source_full_type_name\":{\"type\":\"string\"},\"source_assembly_name\":{\"type\":\"string\"},\"source_asset_path\":{\"type\":\"string\"},\"clear_reference\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\",\"property_path\"],\"additionalProperties\":false}";
        const string SerializedArrayEditSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"operation\":{\"type\":\"string\",\"enum\":[\"append\",\"insert\",\"remove_at\",\"clear\",\"move\"]},\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"to_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"value_bool\":{\"type\":\"boolean\"},\"value_int\":{\"type\":\"integer\"},\"value_float\":{\"type\":\"number\"},\"value_string\":{\"type\":\"string\",\"maxLength\":4096},\"value_enum\":{\"type\":\"string\",\"maxLength\":256},\"value_vector2\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"value_vector3\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"value_vector4\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"value_color\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"source_object_path\":{\"type\":\"string\"},\"source_component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"source_component_type\":{\"type\":\"string\"},\"source_full_type_name\":{\"type\":\"string\"},\"source_assembly_name\":{\"type\":\"string\"},\"source_asset_path\":{\"type\":\"string\"},\"clear_reference\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\",\"property_path\",\"operation\"],\"additionalProperties\":false}";
        const string UnityEventPersistentCallSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"event_name\":{\"type\":\"string\",\"maxLength\":128},\"listener_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1024},\"listener_state\":{\"type\":\"string\",\"enum\":[\"off\",\"runtime_only\",\"editor_and_runtime\"]},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\",\"event_name\",\"listener_index\",\"listener_state\"],\"additionalProperties\":false}";
        const string PrefabMonoBehaviourAddSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"prefab_path\",\"object_path\",\"full_type_name\",\"assembly_name\"],\"additionalProperties\":false}";
        const string PrefabSerializedPropertySetSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"value_bool\":{\"type\":\"boolean\"},\"value_int\":{\"type\":\"integer\"},\"value_float\":{\"type\":\"number\"},\"value_string\":{\"type\":\"string\",\"maxLength\":4096},\"value_enum\":{\"type\":\"string\",\"maxLength\":256},\"value_vector2\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"value_vector3\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"value_vector4\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"value_color\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"prefab_path\",\"object_path\",\"property_path\"],\"additionalProperties\":false}";
        const string PrefabSerializedObjectReferenceSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"source_object_path\":{\"type\":\"string\",\"maxLength\":512},\"source_component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"source_component_type\":{\"type\":\"string\"},\"source_full_type_name\":{\"type\":\"string\"},\"source_assembly_name\":{\"type\":\"string\"},\"source_asset_path\":{\"type\":\"string\"},\"clear_reference\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"prefab_path\",\"object_path\",\"property_path\"],\"additionalProperties\":false}";
        const string PrefabSerializedArrayEditSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"operation\":{\"type\":\"string\",\"enum\":[\"append\",\"insert\",\"remove_at\",\"clear\",\"move\"]},\"index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"to_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"value_bool\":{\"type\":\"boolean\"},\"value_int\":{\"type\":\"integer\"},\"value_float\":{\"type\":\"number\"},\"value_string\":{\"type\":\"string\",\"maxLength\":4096},\"value_enum\":{\"type\":\"string\",\"maxLength\":256},\"value_vector2\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"value_vector3\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"value_vector4\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"value_color\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"source_object_path\":{\"type\":\"string\",\"maxLength\":512},\"source_component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"source_component_type\":{\"type\":\"string\"},\"source_full_type_name\":{\"type\":\"string\"},\"source_assembly_name\":{\"type\":\"string\"},\"source_asset_path\":{\"type\":\"string\"},\"clear_reference\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"prefab_path\",\"object_path\",\"property_path\",\"operation\"],\"additionalProperties\":false}";
        const string PrefabAuthoringApplySpecSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"steps\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"enum\":[\"prefab.object.create\",\"prefab.component.add\",\"prefab.ugui.object.create\",\"prefab.ugui.component.add\",\"prefab.ugui.text.set\",\"prefab.mono_behaviour.add\",\"prefab.serialized.property.set\",\"prefab.serialized.object_reference.set\",\"prefab.serialized.array.edit\"]},\"args\":{\"type\":\"object\"}},\"required\":[\"command\",\"args\"],\"additionalProperties\":false}},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_apply\":{\"type\":\"boolean\",\"default\":false},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"prefab_path\",\"steps\"],\"additionalProperties\":false}";
        const string SceneWiringOutputLimitJson = "{\"max_objects\":1,\"max_components\":1,\"dry_run_default\":true,\"loaded_scenes_only\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":false}";
        const string PrefabWiringOutputLimitJson = "{\"max_objects\":1,\"max_components\":1,\"dry_run_default\":true,\"requires_precondition_hash\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":true}";
        const string PrefabAuthoringApplySpecOutputLimitJson = "{\"max_steps\":32,\"dry_run_default\":true,\"requires_confirm_apply\":true,\"requires_precondition_hash\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":true}";
        const int MaxSerializedArrayElements = 1000;
        const int MaxSerializedArrayReturnedElements = 64;
        const int MaxPrefabAuthoringSteps = 32;
        static readonly ConditionalWeakTable<UnityEngine.Object, LoadedObjectGeneration> LoadedObjectGenerations = new ConditionalWeakTable<UnityEngine.Object, LoadedObjectGeneration>();

        [McpCommand("mono_behaviour.add", Title = "Add project MonoBehaviour", Description = "Dry-run-first add of one exact project MonoBehaviour type to a loaded-scene object", Risk = "write", Idempotency = "unsafe", TimeoutMs = 10000, SchemaJson = MonoBehaviourAddSchemaJson, Tags = new[] { "monobehaviour", "scene", "safe-write" }, Capability = MonoBehaviourFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = SceneWiringOutputLimitJson, FeatureFlag = MonoBehaviourFeatureFlag)]
        public static CommandExecutionResult MonoBehaviourAdd(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var editResult = RequireEditing("mono_behaviour.add", dryRun);
            if (editResult != null) return editResult;
            var sceneResult = RequireLoadedScene(context.ArgsJson, out var scene, out var scenePath);
            if (sceneResult != null) return sceneResult;
            var objectResult = RequireSceneObject(context.ArgsJson, scene, "object_path", out var target, out var objectPath);
            if (objectResult != null) return objectResult;
            var typeResult = ResolveProjectMonoBehaviourType(context.ArgsJson, context.SettingsJson, out var type, out var allowReason);
            if (typeResult != null) return typeResult;
            if (dryRun) return CommandExecutionResult.Ok(MonoBehaviourAddJson(scene, scenePath, objectPath, type, allowReason, null, -1, true));
            var component = Undo.AddComponent(target, type);
            if (component == null) return CommandExecutionResult.Error("unity_exception", "MonoBehaviour add failed");
            EditorUtility.SetDirty(target);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(scene);
            return CommandExecutionResult.Ok(MonoBehaviourAddJson(scene, scenePath, objectPath, type, allowReason, component, ComponentIndex(target, component), false));
        }

        [McpCommand("serialized.property.set", Title = "Set serialized property", Description = "Dry-run-first bounded primitive serialized property write on a loaded-scene component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = SerializedPropertySetSchemaJson, Tags = new[] { "serialized", "scene", "safe-write" }, Capability = SerializedFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = SceneWiringOutputLimitJson, FeatureFlag = SerializedFeatureFlag)]
        public static CommandExecutionResult SerializedPropertySet(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var editResult = RequireEditing("serialized.property.set", dryRun);
            if (editResult != null) return editResult;
            var targetResult = RequireSerializedTarget(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex, out var property);
            if (targetResult != null) return targetResult;
            var valueResult = PreparePropertyValue(context.ArgsJson, property, out var requestedJson, out var apply);
            if (valueResult != null) return valueResult;
            var currentJson = SerializedPropertyValueJson(property);
            var changed = currentJson != requestedJson;
            var hash = PreconditionHash("serialized.property.set", RuntimeInstanceId(context), scenePath, objectPath, component, componentIndex, property.propertyPath, currentJson, requestedJson);
            if (!dryRun && changed)
            {
                var precondition = RequirePrecondition(context.ArgsJson, hash, "serialized.property.set");
                if (precondition != null) return precondition;
                var applyResult = ApplySerializedMutation("serialized.property.set", "Set Serialized Property", component, scene, property.serializedObject, apply, hash);
                if (applyResult != null) return applyResult;
            }
            return CommandExecutionResult.Ok(SerializedSetJson("serialized.property.set", scene, scenePath, objectPath, component, componentIndex, property.propertyPath, currentJson, requestedJson, hash, dryRun, changed));
        }

        [McpCommand("serialized.object_reference.set", Title = "Set serialized object reference", Description = "Dry-run-first object-reference assignment on a loaded-scene component from a scene object/component or Unity-relative asset", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = SerializedObjectReferenceSetSchemaJson, Tags = new[] { "serialized", "object-reference", "scene", "safe-write" }, Capability = SerializedFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = SceneWiringOutputLimitJson, FeatureFlag = SerializedFeatureFlag)]
        public static CommandExecutionResult SerializedObjectReferenceSet(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var editResult = RequireEditing("serialized.object_reference.set", dryRun);
            if (editResult != null) return editResult;
            var targetResult = RequireSerializedTarget(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex, out var property);
            if (targetResult != null) return targetResult;
            if (property.propertyType != SerializedPropertyType.ObjectReference) return CommandExecutionResult.Error("invalid_field_value", "property_path must reference a SerializedPropertyType.ObjectReference field");
            var sourceResult = ResolveReferenceSource(context.ArgsJson, scene, property, out var requestedObject, out var sourceLabel);
            if (sourceResult != null) return sourceResult;
            var currentJson = ObjectReferenceJson(property.objectReferenceValue);
            var requestedJson = ObjectReferenceJson(requestedObject);
            var changed = property.objectReferenceValue != requestedObject;
            var hash = PreconditionHash("serialized.object_reference.set", RuntimeInstanceId(context), scenePath, objectPath, component, componentIndex, property.propertyPath, currentJson, requestedJson);
            if (!dryRun && changed)
            {
                var precondition = RequirePrecondition(context.ArgsJson, hash, "serialized.object_reference.set");
                if (precondition != null) return precondition;
                var applyResult = ApplySerializedMutation("serialized.object_reference.set", "Set Serialized Object Reference", component, scene, property.serializedObject, () => property.objectReferenceValue = requestedObject, hash);
                if (applyResult != null) return applyResult;
            }
            var json = SerializedSetJson("serialized.object_reference.set", scene, scenePath, objectPath, component, componentIndex, property.propertyPath, currentJson, requestedJson, hash, dryRun, changed);
            return CommandExecutionResult.Ok(json.Substring(0, json.Length - 1) + ",\"source\":" + JsonUtil.String(sourceLabel) + "}");
        }

        [McpCommand("serialized.array.edit", Title = "Edit serialized array", Description = "Dry-run-first bounded array/list edit on a loaded-scene component", Risk = "write", Idempotency = "unsafe", TimeoutMs = 10000, SchemaJson = SerializedArrayEditSchemaJson, Tags = new[] { "serialized", "array", "scene", "safe-write" }, Capability = SerializedFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = SceneWiringOutputLimitJson, FeatureFlag = SerializedFeatureFlag)]
        public static CommandExecutionResult SerializedArrayEdit(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var editResult = RequireEditing("serialized.array.edit", dryRun);
            if (editResult != null) return editResult;
            var targetResult = RequireSerializedArrayTarget(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex, out var property);
            if (targetResult != null) return targetResult;
            var operationResult = PrepareArrayEditOperation(context.ArgsJson, scene, property, out var operation, out var requestedJson, out var changed, out var apply);
            if (operationResult != null) return operationResult;
            var currentHashJson = SerializedArrayValueJson(property, MaxSerializedArrayElements, out _);
            var currentPreviewJson = SerializedArrayValueJson(property, MaxSerializedArrayReturnedElements, out var currentTruncated);
            var hash = PreconditionHash("serialized.array.edit", RuntimeInstanceId(context), scenePath, objectPath, component, componentIndex, property.propertyPath, currentHashJson, requestedJson);
            if (!dryRun && changed)
            {
                var precondition = RequirePrecondition(context.ArgsJson, hash, "serialized.array.edit");
                if (precondition != null) return precondition;
                var applyResult = ApplySerializedMutation("serialized.array.edit", "Edit Serialized Array", component, scene, property.serializedObject, apply, hash);
                if (applyResult != null) return applyResult;
            }
            return CommandExecutionResult.Ok(SerializedArrayEditJson(scene, scenePath, objectPath, component, componentIndex, property.propertyPath, operation, currentPreviewJson, currentTruncated, requestedJson, hash, dryRun, changed));
        }

        [McpCommand("unityevent.persistent_call.set", Title = "Set UnityEvent persistent call state", Description = "Dry-run-first state edit for one existing persistent UnityEvent listener on a loaded-scene component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = UnityEventPersistentCallSetSchemaJson, Tags = new[] { "unityevent", "persistent-call", "scene", "safe-write" }, Capability = UnityEventFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = SceneWiringOutputLimitJson, FeatureFlag = UnityEventFeatureFlag)]
        public static CommandExecutionResult UnityEventPersistentCallSet(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var editResult = RequireEditing("unityevent.persistent_call.set", dryRun);
            if (editResult != null) return editResult;
            var targetResult = RequireUnityEventTarget(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex, out var eventName, out var eventPropertyPath, out var unityEvent, out var listenerIndex);
            if (targetResult != null) return targetResult;
            var stateResult = ParseListenerState(context.ArgsJson, out var requestedState, out var requestedStateJson);
            if (stateResult != null) return stateResult;
            var currentJson = PersistentCallJson(unityEvent, eventName, listenerIndex);
            var currentState = unityEvent.GetPersistentListenerState(listenerIndex);
            var changed = currentState != requestedState;
            var hash = PreconditionHash("unityevent.persistent_call.set", RuntimeInstanceId(context), scenePath, objectPath, component, componentIndex, eventName + "#" + listenerIndex.ToString(CultureInfo.InvariantCulture), currentJson, requestedStateJson);
            if (!dryRun && changed)
            {
                var precondition = RequirePrecondition(context.ArgsJson, hash, "unityevent.persistent_call.set");
                if (precondition != null) return precondition;
                var prepareResult = PreparePersistentListenerStateSerialized(component, eventPropertyPath, listenerIndex, requestedState, out var serializedObject, out var apply);
                if (prepareResult != null) return prepareResult;
                var applyResult = ApplySerializedMutation("unityevent.persistent_call.set", "Set UnityEvent Persistent Call", component, scene, serializedObject, apply, hash);
                if (applyResult != null) return applyResult;
            }
            return CommandExecutionResult.Ok(UnityEventPersistentCallSetJson(scene, scenePath, objectPath, component, componentIndex, eventName, listenerIndex, currentJson, requestedStateJson, hash, dryRun, changed));
        }

        [McpCommand("prefab.mono_behaviour.add", Title = "Add prefab project MonoBehaviour", Description = "Dry-run-first hash-gated add of one exact project MonoBehaviour type to a prefab asset object", Risk = "write", Idempotency = "unsafe", TimeoutMs = 30000, SchemaJson = PrefabMonoBehaviourAddSchemaJson, Tags = new[] { "prefab", "monobehaviour", "asset", "safe-write" }, Capability = MonoBehaviourFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = PrefabWiringOutputLimitJson, FeatureFlag = MonoBehaviourFeatureFlag)]
        public static CommandExecutionResult PrefabMonoBehaviourAdd(McpCommandContext context)
        {
            var featureResult = RequireFeatureEnabled(context.SettingsJson, EditorSafeWriteFeatureFlag, "prefab.mono_behaviour.add");
            if (featureResult != null) return featureResult;
            var editResult = RequireEditing("prefab.mono_behaviour.add");
            if (editResult != null) return editResult;
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            var typeResult = ResolveProjectMonoBehaviourType(context.ArgsJson, context.SettingsJson, out var type, out var allowReason);
            if (typeResult != null) return typeResult;
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var objectResult = RequirePrefabObject(context.ArgsJson, root, out var target, out var objectPath);
                if (objectResult != null) return objectResult;
                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                var operation = "prefab.mono_behaviour.add";
                var hash = PrefabWritePreconditionHash(prefabPath, operation, objectPath + "|" + (type.FullName ?? type.Name) + "|" + type.Assembly.GetName().Name);
                if (dryRun) return CommandExecutionResult.Ok(PrefabMonoBehaviourAddJson(prefabPath, root, objectPath, type, allowReason, null, -1, true, hash));
                var precondition = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, hash);
                if (precondition != null) return precondition;
                var component = target.AddComponent(type);
                if (component == null) return CommandExecutionResult.Error("unity_exception", "MonoBehaviour add failed");
                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(component);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null) return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                return CommandExecutionResult.Ok(PrefabMonoBehaviourAddJson(prefabPath, root, objectPath, type, allowReason, component, ComponentIndex(target, component), false, hash));
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [McpCommand("prefab.serialized.property.set", Title = "Set prefab serialized property", Description = "Dry-run-first bounded primitive serialized property write on a prefab asset component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 30000, SchemaJson = PrefabSerializedPropertySetSchemaJson, Tags = new[] { "prefab", "serialized", "asset", "safe-write" }, Capability = SerializedFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = PrefabWiringOutputLimitJson, FeatureFlag = SerializedFeatureFlag)]
        public static CommandExecutionResult PrefabSerializedPropertySet(McpCommandContext context)
        {
            var featureResult = RequireFeatureEnabled(context.SettingsJson, EditorSafeWriteFeatureFlag, "prefab.serialized.property.set");
            if (featureResult != null) return featureResult;
            var editResult = RequireEditing("prefab.serialized.property.set");
            if (editResult != null) return editResult;
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var targetResult = RequirePrefabSerializedTarget(context.ArgsJson, root, out var target, out var objectPath, out var component, out var componentIndex, out var property);
                if (targetResult != null) return targetResult;
                var valueResult = PreparePropertyValue(context.ArgsJson, property, out var requestedJson, out var apply);
                if (valueResult != null) return valueResult;
                var currentJson = SerializedPropertyValueJson(property);
                var changed = currentJson != requestedJson;
                var hash = PrefabWritePreconditionHash(prefabPath, "prefab.serialized.property.set", objectPath + "|" + ComponentSelectorKey(component, componentIndex) + "|" + property.propertyPath + "|" + currentJson + "|" + requestedJson);
                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                if (!dryRun && changed)
                {
                    var precondition = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, "prefab.serialized.property.set", hash);
                    if (precondition != null) return precondition;
                    apply();
                    property.serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(component);
                    var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    if (saved == null) return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }
                return CommandExecutionResult.Ok(PrefabSerializedSetJson("prefab.serialized.property.set", prefabPath, root, objectPath, component, componentIndex, property.propertyPath, currentJson, requestedJson, hash, dryRun, changed));
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [McpCommand("prefab.serialized.object_reference.set", Title = "Set prefab serialized object reference", Description = "Dry-run-first object-reference assignment on a prefab asset component from the same prefab contents or a Unity-relative asset", Risk = "write", Idempotency = "idempotent", TimeoutMs = 30000, SchemaJson = PrefabSerializedObjectReferenceSetSchemaJson, Tags = new[] { "prefab", "serialized", "object-reference", "asset", "safe-write" }, Capability = SerializedFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = PrefabWiringOutputLimitJson, FeatureFlag = SerializedFeatureFlag)]
        public static CommandExecutionResult PrefabSerializedObjectReferenceSet(McpCommandContext context)
        {
            var featureResult = RequireFeatureEnabled(context.SettingsJson, EditorSafeWriteFeatureFlag, "prefab.serialized.object_reference.set");
            if (featureResult != null) return featureResult;
            var editResult = RequireEditing("prefab.serialized.object_reference.set");
            if (editResult != null) return editResult;
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var targetResult = RequirePrefabSerializedTarget(context.ArgsJson, root, out var target, out var objectPath, out var component, out var componentIndex, out var property);
                if (targetResult != null) return targetResult;
                if (property.propertyType != SerializedPropertyType.ObjectReference) return CommandExecutionResult.Error("invalid_field_value", "property_path must reference a SerializedPropertyType.ObjectReference field");
                var sourceResult = ResolvePrefabReferenceSource(context.ArgsJson, root, property, out var requestedObject, out var sourceLabel);
                if (sourceResult != null) return sourceResult;
                var currentJson = ObjectReferenceJson(property.objectReferenceValue);
                var requestedJson = ObjectReferenceJson(requestedObject);
                var changed = property.objectReferenceValue != requestedObject;
                var hash = PrefabWritePreconditionHash(prefabPath, "prefab.serialized.object_reference.set", objectPath + "|" + ComponentSelectorKey(component, componentIndex) + "|" + property.propertyPath + "|" + currentJson + "|" + requestedJson);
                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                if (!dryRun && changed)
                {
                    var precondition = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, "prefab.serialized.object_reference.set", hash);
                    if (precondition != null) return precondition;
                    property.objectReferenceValue = requestedObject;
                    property.serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(component);
                    var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    if (saved == null) return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }
                var json = PrefabSerializedSetJson("prefab.serialized.object_reference.set", prefabPath, root, objectPath, component, componentIndex, property.propertyPath, currentJson, requestedJson, hash, dryRun, changed);
                return CommandExecutionResult.Ok(json.Substring(0, json.Length - 1) + ",\"source\":" + JsonUtil.String(sourceLabel) + "}");
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [McpCommand("prefab.serialized.array.edit", Title = "Edit prefab serialized array", Description = "Dry-run-first bounded serialized array/list edit on a prefab asset component", Risk = "write", Idempotency = "unsafe", TimeoutMs = 30000, SchemaJson = PrefabSerializedArrayEditSchemaJson, Tags = new[] { "prefab", "serialized", "array", "asset", "safe-write" }, Capability = SerializedFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = PrefabWiringOutputLimitJson, FeatureFlag = SerializedFeatureFlag)]
        public static CommandExecutionResult PrefabSerializedArrayEdit(McpCommandContext context)
        {
            var featureResult = RequireFeatureEnabled(context.SettingsJson, EditorSafeWriteFeatureFlag, "prefab.serialized.array.edit");
            if (featureResult != null) return featureResult;
            var editResult = RequireEditing("prefab.serialized.array.edit");
            if (editResult != null) return editResult;
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var targetResult = RequirePrefabSerializedArrayTarget(context.ArgsJson, root, out var target, out var objectPath, out var component, out var componentIndex, out var property);
                if (targetResult != null) return targetResult;
                var operationResult = PreparePrefabArrayEditOperation(context.ArgsJson, root, property, out var operation, out var requestedJson, out var changed, out var apply);
                if (operationResult != null) return operationResult;
                var currentHashJson = SerializedArrayValueJson(property, MaxSerializedArrayElements, out _);
                var currentPreviewJson = SerializedArrayValueJson(property, MaxSerializedArrayReturnedElements, out var currentTruncated);
                var hash = PrefabWritePreconditionHash(prefabPath, "prefab.serialized.array.edit", objectPath + "|" + ComponentSelectorKey(component, componentIndex) + "|" + property.propertyPath + "|" + currentHashJson + "|" + requestedJson);
                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                if (!dryRun && changed)
                {
                    var precondition = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, "prefab.serialized.array.edit", hash);
                    if (precondition != null) return precondition;
                    apply();
                    property.serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(component);
                    var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    if (saved == null) return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }
                return CommandExecutionResult.Ok(PrefabSerializedArrayEditJson(prefabPath, root, objectPath, component, componentIndex, property.propertyPath, operation, currentPreviewJson, currentTruncated, requestedJson, hash, dryRun, changed));
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [McpCommand("prefab.authoring.apply_spec", Title = "Prefab authoring apply spec", Description = "Dry-run-first prefab authoring spec composed from focused prefab safe-write commands", Risk = "write", Idempotency = "unsafe", TimeoutMs = 60000, SchemaJson = PrefabAuthoringApplySpecSchemaJson, Tags = new[] { "prefab", "authoring", "composite", "safe-write" }, Capability = PrefabCompositeFeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "status", OutputLimitJson = PrefabAuthoringApplySpecOutputLimitJson, FeatureFlag = PrefabCompositeFeatureFlag)]
        public static CommandExecutionResult PrefabAuthoringApplySpec(McpCommandContext context)
        {
            var editResult = RequireEditing("prefab.authoring.apply_spec");
            if (editResult != null) return editResult;
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            var stepsResult = ExtractPrefabAuthoringSteps(context.ArgsJson, out var steps);
            if (stepsResult != null) return stepsResult;
            var featureResult = RequirePrefabAuthoringFeatureFlags(context.SettingsJson, steps, out var requiredFeaturesJson);
            if (featureResult != null) return featureResult;
            var planResult = PlanPrefabAuthoringSpec(context, prefabPath, steps, requiredFeaturesJson, out var planJson, out var preconditionHash);
            if (planResult != null) return planResult;
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (dryRun) return CommandExecutionResult.Ok(PrefabAuthoringSpecResultJson(prefabPath, true, false, false, preconditionHash, requiredFeaturesJson, planJson, "[]", -1, string.Empty));
            if (!JsonUtil.ExtractBool(context.ArgsJson, "confirm_apply", false)) return CommandExecutionResult.Error("confirmation_required", "confirm_apply must be true when prefab.authoring.apply_spec runs with dry_run:false", "{\"precondition_hash\":" + JsonUtil.String(preconditionHash) + "}");
            var precondition = RequirePrecondition(context.ArgsJson, preconditionHash, "prefab.authoring.apply_spec");
            if (precondition != null) return precondition;
            var applyResult = ApplyPrefabAuthoringSteps(context, prefabPath, steps, out var appliedJson, out var failedIndex, out var failureJson);
            if (applyResult != null) return CommandExecutionResult.Ok(PrefabAuthoringSpecResultJson(prefabPath, false, false, true, preconditionHash, requiredFeaturesJson, planJson, appliedJson, failedIndex, failureJson));
            return CommandExecutionResult.Ok(PrefabAuthoringSpecResultJson(prefabPath, false, true, false, preconditionHash, requiredFeaturesJson, planJson, appliedJson, -1, string.Empty));
        }

        static CommandExecutionResult RequirePrefabSerializedTarget(string argsJson, GameObject root, out GameObject target, out string objectPath, out Component component, out int componentIndex, out SerializedProperty property)
        {
            var result = RequirePrefabSerializedTargetBase(argsJson, root, out target, out objectPath, out component, out componentIndex, out property);
            if (result != null) return result;
            if (property.isArray && property.propertyType != SerializedPropertyType.String) return CommandExecutionResult.Error("invalid_field_value", "arrays/lists are not supported by prefab.serialized.property.set; use prefab.serialized.array.edit");
            if (property.propertyType == SerializedPropertyType.ManagedReference) return CommandExecutionResult.Error("invalid_field_value", "managed references are not supported by prefab.serialized.property.set");
            return null;
        }

        static CommandExecutionResult RequirePrefabSerializedArrayTarget(string argsJson, GameObject root, out GameObject target, out string objectPath, out Component component, out int componentIndex, out SerializedProperty property)
        {
            var result = RequirePrefabSerializedTargetBase(argsJson, root, out target, out objectPath, out component, out componentIndex, out property);
            if (result != null) return result;
            if (!property.isArray || property.propertyType == SerializedPropertyType.String) return CommandExecutionResult.Error("invalid_field_value", "property_path must reference a serialized array/list field");
            if (property.arraySize > MaxSerializedArrayElements) return CommandExecutionResult.Error("invalid_field_value", "prefab.serialized.array.edit only supports arrays/lists up to " + MaxSerializedArrayElements.ToString(CultureInfo.InvariantCulture) + " elements");
            return null;
        }

        static CommandExecutionResult RequirePrefabSerializedTargetBase(string argsJson, GameObject root, out GameObject target, out string objectPath, out Component component, out int componentIndex, out SerializedProperty property)
        {
            component = null; componentIndex = -1; property = null;
            var objectResult = RequirePrefabObject(argsJson, root, out target, out objectPath);
            if (objectResult != null) return objectResult;
            var componentResult = RequireComponent(argsJson, target, string.Empty, out component, out componentIndex);
            if (componentResult != null) return componentResult;
            var propertyPath = JsonUtil.ExtractString(argsJson, "property_path").Trim();
            if (string.IsNullOrEmpty(propertyPath)) return CommandExecutionResult.Error("schema_validation_failed", "Argument property_path is required");
            if (PropertyPathLooksUnsafe(propertyPath)) return CommandExecutionResult.Error("invalid_field_value", "property_path must be a SerializedProperty path, not a URL or filesystem path");
            var serializedObject = new SerializedObject(component);
            property = serializedObject.FindProperty(propertyPath);
            return property == null ? CommandExecutionResult.Error("asset_not_found", "Serialized property was not found") : null;
        }

        static CommandExecutionResult ResolvePrefabReferenceSource(string argsJson, GameObject root, SerializedProperty property, out UnityEngine.Object source, out string label)
        {
            source = null; label = "null";
            var clear = JsonUtil.ExtractBool(argsJson, "clear_reference", false);
            var sourceObjectPath = JsonUtil.ExtractString(argsJson, "source_object_path");
            var sourceAssetPath = JsonUtil.ExtractString(argsJson, "source_asset_path");
            var selectorCount = (clear ? 1 : 0) + (!string.IsNullOrEmpty(sourceObjectPath) ? 1 : 0) + (!string.IsNullOrEmpty(sourceAssetPath) ? 1 : 0);
            if (selectorCount != 1) return CommandExecutionResult.Error("schema_validation_failed", "Provide exactly one of clear_reference, source_object_path, or source_asset_path");
            if (clear) return null;
            if (!string.IsNullOrEmpty(sourceAssetPath))
            {
                string path;
                try { path = PathGuard.RequireUnityAssetPath(sourceAssetPath); } catch (Exception ex) { return CommandExecutionResult.Error("invalid_field_value", "source_asset_path must be Unity-relative", JsonUtil.String(ex.Message)); }
                source = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (source == null) return CommandExecutionResult.Error("asset_not_found", "source_asset_path was not found");
                label = path;
            }
            else
            {
                var objectResult = RequirePrefabObject(argsJson, root, "source_object_path", out var sourceObject, out var normalizedPath);
                if (objectResult != null) return objectResult;
                var wantsComponent = HasJsonProperty(argsJson, "source_component_index") || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "source_component_type")) || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "source_full_type_name"));
                if (wantsComponent)
                {
                    var componentResult = RequireComponent(argsJson, sourceObject, "source_", out var sourceComponent, out var sourceComponentIndex);
                    if (componentResult != null) return componentResult;
                    source = sourceComponent;
                    label = normalizedPath + "#" + sourceComponentIndex.ToString(CultureInfo.InvariantCulture);
                }
                else
                {
                    source = sourceObject;
                    label = normalizedPath;
                }
            }
            var expected = ObjectReferenceExpectedType(property);
            if (expected != null && source != null && !expected.IsAssignableFrom(source.GetType())) return CommandExecutionResult.Error("invalid_field_value", "source reference is not assignable to the serialized field type", "{\"expected_type\":" + JsonUtil.String(expected.FullName ?? expected.Name) + ",\"source_type\":" + JsonUtil.String(source.GetType().FullName ?? source.GetType().Name) + "}");
            return null;
        }

        static CommandExecutionResult PreparePrefabArrayEditOperation(string argsJson, GameObject root, SerializedProperty property, out string operation, out string requestedJson, out bool changed, out Action apply)
        {
            operation = JsonUtil.ExtractString(argsJson, "operation").Trim().ToLowerInvariant(); requestedJson = "null"; changed = false; apply = null;
            if (string.IsNullOrEmpty(operation)) return CommandExecutionResult.Error("schema_validation_failed", "Argument operation is required");
            var supportedResult = RequireSupportedArrayElements(property);
            if (supportedResult != null) return supportedResult;
            switch (operation)
            {
                case "append":
                    if (HasJsonProperty(argsJson, "index") || HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "append does not accept index or to_index");
                    if (property.arraySize >= MaxSerializedArrayElements) return CommandExecutionResult.Error("invalid_field_value", "append would exceed the prefab.serialized.array.edit element limit");
                    return PreparePrefabArrayInsertLike(argsJson, root, property, property.arraySize, "append", out requestedJson, out changed, out apply);
                case "insert":
                    if (HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "insert does not accept to_index");
                    if (property.arraySize >= MaxSerializedArrayElements) return CommandExecutionResult.Error("invalid_field_value", "insert would exceed the prefab.serialized.array.edit element limit");
                    var insertIndexResult = RequireArrayIndex(argsJson, "index", 0, property.arraySize, out var insertIndex);
                    if (insertIndexResult != null) return insertIndexResult;
                    return PreparePrefabArrayInsertLike(argsJson, root, property, insertIndex, "insert", out requestedJson, out changed, out apply);
                case "remove_at":
                    if (HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "remove_at does not accept to_index");
                    var removePayload = RejectArrayEditPayload(argsJson, "remove_at");
                    if (removePayload != null) return removePayload;
                    var removeIndexResult = RequireArrayIndex(argsJson, "index", 0, property.arraySize - 1, out var removeIndex);
                    if (removeIndexResult != null) return removeIndexResult;
                    requestedJson = "{\"operation\":\"remove_at\",\"index\":" + removeIndex.ToString(CultureInfo.InvariantCulture) + "}";
                    changed = true;
                    apply = () => DeleteArrayElementAtIndexFully(property, removeIndex);
                    return null;
                case "clear":
                    if (HasJsonProperty(argsJson, "index") || HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "clear does not accept index or to_index");
                    var clearPayload = RejectArrayEditPayload(argsJson, "clear");
                    if (clearPayload != null) return clearPayload;
                    requestedJson = "{\"operation\":\"clear\",\"previous_array_size\":" + property.arraySize.ToString(CultureInfo.InvariantCulture) + "}";
                    changed = property.arraySize > 0;
                    apply = () => property.ClearArray();
                    return null;
                case "move":
                    var movePayload = RejectArrayEditPayload(argsJson, "move");
                    if (movePayload != null) return movePayload;
                    var fromResult = RequireArrayIndex(argsJson, "index", 0, property.arraySize - 1, out var fromIndex);
                    if (fromResult != null) return fromResult;
                    var toResult = RequireArrayIndex(argsJson, "to_index", 0, property.arraySize - 1, out var toIndex);
                    if (toResult != null) return toResult;
                    requestedJson = "{\"operation\":\"move\",\"index\":" + fromIndex.ToString(CultureInfo.InvariantCulture) + ",\"to_index\":" + toIndex.ToString(CultureInfo.InvariantCulture) + "}";
                    changed = fromIndex != toIndex;
                    apply = () => property.MoveArrayElement(fromIndex, toIndex);
                    return null;
                default:
                    return CommandExecutionResult.Error("invalid_field_value", "operation must be append, insert, remove_at, clear, or move");
            }
        }

        static CommandExecutionResult PreparePrefabArrayInsertLike(string argsJson, GameObject root, SerializedProperty property, int index, string operation, out string requestedJson, out bool changed, out Action apply)
        {
            requestedJson = "null"; changed = false; apply = null;
            var valueResult = PreparePrefabArrayElementValue(argsJson, root, property, out var valueJson, out var sourceLabel, out var setter);
            if (valueResult != null) return valueResult;
            requestedJson = "{\"operation\":" + JsonUtil.String(operation) + ",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"value\":" + valueJson + SourceJsonFragment(sourceLabel) + "}";
            changed = true;
            apply = () => { property.InsertArrayElementAtIndex(index); setter(property.GetArrayElementAtIndex(index)); };
            return null;
        }

        static CommandExecutionResult PreparePrefabArrayElementValue(string argsJson, GameObject root, SerializedProperty property, out string valueJson, out string sourceLabel, out Action<SerializedProperty> setter)
        {
            valueJson = "null"; sourceLabel = string.Empty; setter = null;
            if (property.arraySize == 0) return CommandExecutionResult.Error("invalid_field_value", "append/insert require an existing array element for type inference; empty arrays are not supported by this slice");
            var template = property.GetArrayElementAtIndex(property.arraySize - 1);
            var supportedResult = RequireSupportedArrayElement(template);
            if (supportedResult != null) return supportedResult;
            if (template.propertyType == SerializedPropertyType.ObjectReference)
            {
                if (CountValueFields(argsJson) != 0) return CommandExecutionResult.Error("schema_validation_failed", "object-reference array elements require source_object_path, source_asset_path, or clear_reference instead of value_* fields");
                var sourceResult = ResolvePrefabReferenceSource(argsJson, root, template, out var source, out sourceLabel);
                if (sourceResult != null) return sourceResult;
                valueJson = ObjectReferenceJson(source);
                setter = element => element.objectReferenceValue = source;
                return null;
            }
            if (HasReferenceSourceFields(argsJson)) return CommandExecutionResult.Error("schema_validation_failed", "primitive array elements require exactly one value_* field and do not accept reference source fields");
            return PreparePropertyValueSetter(argsJson, template, out valueJson, out setter);
        }

        static CommandExecutionResult PlanPrefabAuthoringSpec(McpCommandContext context, string prefabPath, List<SpecStep> steps, string requiredFeaturesJson, out string planJson, out string preconditionHash)
        {
            var plannedObjects = new HashSet<string>(StringComparer.Ordinal);
            var builder = new StringBuilder();
            for (var index = 0; index < steps.Count; index++)
            {
                if (index > 0) builder.Append(',');
                var step = steps[index];
                var targetObjectPath = JsonUtil.ExtractString(step.ArgsJson, "object_path");
                if (!string.IsNullOrEmpty(targetObjectPath) && plannedObjects.Contains(NormalizeObjectPath(targetObjectPath)))
                {
                    builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"status\":\"planned_after_create\",\"object_path\":").Append(JsonUtil.String(NormalizeObjectPath(targetObjectPath))).Append('}');
                    continue;
                }
                var nested = InvokePrefabAuthoringStep(context, step.Command, BuildPrefabNestedArgs(prefabPath, step.ArgsJson, true, string.Empty));
                if (nested.Status != "ok")
                {
                    planJson = "[]"; preconditionHash = string.Empty;
                    return CommandExecutionResult.Error(nested.ErrorCode, "prefab.authoring.apply_spec dry-run failed at step " + index + ": " + nested.ErrorMessage, "{\"step_index\":" + index + ",\"command\":" + JsonUtil.String(step.Command) + "}");
                }
                builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"result\":").Append(nested.DataJson).Append('}');
                if (step.Command == "prefab.object.create" || step.Command == "prefab.ugui.object.create")
                {
                    var planned = JsonUtil.ExtractString(nested.DataJson, "object_path");
                    if (!string.IsNullOrEmpty(planned)) plannedObjects.Add(NormalizeObjectPath(planned));
                }
            }
            planJson = "[" + builder + "]";
            preconditionHash = Sha256Prefixed("prefab.authoring.apply_spec\n" + prefabPath + "\n" + StepsStableJson(steps) + "\n" + requiredFeaturesJson + "\n" + planJson);
            return null;
        }

        static CommandExecutionResult ApplyPrefabAuthoringSteps(McpCommandContext context, string prefabPath, List<SpecStep> steps, out string appliedJson, out int failedIndex, out string failureJson)
        {
            var builder = new StringBuilder();
            failedIndex = -1; failureJson = string.Empty;
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var dryRun = InvokePrefabAuthoringStep(context, step.Command, BuildPrefabNestedArgs(prefabPath, step.ArgsJson, true, string.Empty));
                if (dryRun.Status != "ok") { failedIndex = index; failureJson = ErrorJson(dryRun); appliedJson = "[" + builder + "]"; return dryRun; }
                var perStepHash = JsonUtil.ExtractString(dryRun.DataJson, "precondition_hash");
                var result = InvokePrefabAuthoringStep(context, step.Command, BuildPrefabNestedArgs(prefabPath, step.ArgsJson, false, perStepHash));
                if (result.Status != "ok") { failedIndex = index; failureJson = ErrorJson(result); appliedJson = "[" + builder + "]"; return result; }
                if (builder.Length > 0) builder.Append(',');
                builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"result\":").Append(result.DataJson).Append('}');
            }
            appliedJson = "[" + builder + "]";
            return null;
        }

        static CommandExecutionResult InvokePrefabAuthoringStep(McpCommandContext context, string command, string argsJson)
        {
            var nested = new McpCommandContext(context.State, context.RequestId, context.Project, argsJson, context.SettingsJson);
            if (command == "prefab.object.create") return EditorSafeWriteCommands.PrefabObjectCreate(nested);
            if (command == "prefab.component.add") return EditorSafeWriteCommands.PrefabComponentAdd(nested);
            if (command == "prefab.ugui.object.create") return EditorSafeWriteCommands.PrefabUguiObjectCreate(nested);
            if (command == "prefab.ugui.component.add") return EditorSafeWriteCommands.PrefabUguiComponentAdd(nested);
            if (command == "prefab.ugui.text.set") return EditorSafeWriteCommands.PrefabUguiTextSet(nested);
            if (command == "prefab.mono_behaviour.add") return PrefabMonoBehaviourAdd(nested);
            if (command == "prefab.serialized.property.set") return PrefabSerializedPropertySet(nested);
            if (command == "prefab.serialized.object_reference.set") return PrefabSerializedObjectReferenceSet(nested);
            if (command == "prefab.serialized.array.edit") return PrefabSerializedArrayEdit(nested);
            return CommandExecutionResult.Error("invalid_field_value", "step command is not supported by prefab.authoring.apply_spec");
        }

        static CommandExecutionResult RequirePrefabAuthoringFeatureFlags(string settingsJson, List<SpecStep> steps, out string requiredFeaturesJson)
        {
            var featuresJson = JsonUtil.ExtractObject(settingsJson, "feature_flags");
            var required = new List<string>();
            AddRequiredFeature(required, PrefabCompositeFeatureFlag);
            AddRequiredFeature(required, EditorSafeWriteFeatureFlag);
            for (var index = 0; index < steps.Count; index++)
            {
                if (steps[index].Command.StartsWith("prefab.mono_behaviour.", StringComparison.Ordinal)) AddRequiredFeature(required, MonoBehaviourFeatureFlag);
                if (steps[index].Command.StartsWith("prefab.serialized.", StringComparison.Ordinal)) AddRequiredFeature(required, SerializedFeatureFlag);
            }
            for (var index = 0; index < required.Count; index++)
            {
                if (!FeatureEnabled(featuresJson, required[index])) { requiredFeaturesJson = StringArrayJson(required); return CommandExecutionResult.Error("feature_disabled", "prefab.authoring.apply_spec requires feature flag " + required[index], "{\"required_feature\":" + JsonUtil.String(required[index]) + "}"); }
            }
            requiredFeaturesJson = StringArrayJson(required);
            return null;
        }

        static CommandExecutionResult ExtractPrefabAuthoringSteps(string json, out List<SpecStep> steps)
        {
            steps = new List<SpecStep>();
            var arrayJson = ExtractArray(json, "steps");
            if (string.IsNullOrEmpty(arrayJson)) return CommandExecutionResult.Error("schema_validation_failed", "steps must be an array");
            var objects = new List<string>();
            if (!TryExtractObjectArrayItems(arrayJson, objects)) return CommandExecutionResult.Error("schema_validation_failed", "steps must contain objects");
            if (objects.Count == 0 || objects.Count > MaxPrefabAuthoringSteps) return CommandExecutionResult.Error("invalid_field_value", "steps must contain 1 to 32 items");
            for (var index = 0; index < objects.Count; index++)
            {
                var command = JsonUtil.ExtractString(objects[index], "command");
                if (!IsPrefabAuthoringStepCommand(command)) return CommandExecutionResult.Error("invalid_field_value", "step command is not supported by prefab.authoring.apply_spec");
                var args = JsonUtil.ExtractObject(objects[index], "args");
                if (string.IsNullOrEmpty(args) || args == "{}") return CommandExecutionResult.Error("schema_validation_failed", "each step requires args object");
                steps.Add(new SpecStep(command, args));
            }
            return null;
        }

        static bool IsPrefabAuthoringStepCommand(string command)
        {
            return command == "prefab.object.create" || command == "prefab.component.add" || command == "prefab.ugui.object.create" || command == "prefab.ugui.component.add" || command == "prefab.ugui.text.set" || command == "prefab.mono_behaviour.add" || command == "prefab.serialized.property.set" || command == "prefab.serialized.object_reference.set" || command == "prefab.serialized.array.edit";
        }

        static string PrefabMonoBehaviourAddJson(string prefabPath, GameObject root, string objectPath, Type type, string allowReason, Component component, int componentIndex, bool dryRun, string hash)
        {
            var componentJson = component == null ? ComponentTypeJson(type, componentIndex) : ComponentJson(component, componentIndex);
            return "{\"status\":\"ok\",\"command\":\"prefab.mono_behaviour.add\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"added\":" + JsonUtil.Bool(!dryRun) + ",\"would_add\":" + JsonUtil.Bool(dryRun) + ",\"prefab_path\":" + JsonUtil.String(prefabPath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"allowlist_decision\":{\"allowed\":true,\"reason\":" + JsonUtil.String(allowReason) + "},\"component\":" + componentJson + ",\"serialized_fields_configured\":false,\"unity_event_mutation\":false,\"changed_objects\":" + (dryRun ? "[]" : "[" + JsonUtil.String(objectPath) + "]") + ",\"changed_components\":" + (dryRun ? "[]" : "[" + componentJson + "]") + PrefabContractJson(prefabPath, root, dryRun, true, !dryRun, hash) + "}";
        }

        static string PrefabSerializedSetJson(string command, string prefabPath, GameObject root, string objectPath, Component component, int componentIndex, string propertyPath, string currentJson, string requestedJson, string hash, bool dryRun, bool changed)
        {
            return "{\"status\":\"ok\",\"command\":" + JsonUtil.String(command) + ",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed) + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed) + ",\"prefab_path\":" + JsonUtil.String(prefabPath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"component\":" + ComponentJson(component, componentIndex) + ",\"property_path\":" + JsonUtil.String(propertyPath) + ",\"current_value\":" + currentJson + ",\"requested_value\":" + requestedJson + ",\"changed_objects\":" + (!dryRun && changed ? "[" + JsonUtil.String(objectPath) + "]" : "[]") + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]") + PrefabContractJson(prefabPath, root, dryRun, changed, !dryRun && changed, hash) + "}";
        }

        static string PrefabSerializedArrayEditJson(string prefabPath, GameObject root, string objectPath, Component component, int componentIndex, string propertyPath, string operation, string currentJson, bool currentTruncated, string requestedJson, string hash, bool dryRun, bool changed)
        {
            return "{\"status\":\"ok\",\"command\":\"prefab.serialized.array.edit\",\"operation\":" + JsonUtil.String(operation) + ",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed) + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed) + ",\"prefab_path\":" + JsonUtil.String(prefabPath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"component\":" + ComponentJson(component, componentIndex) + ",\"property_path\":" + JsonUtil.String(propertyPath) + ",\"current_value\":" + currentJson + ",\"current_value_truncated\":" + JsonUtil.Bool(currentTruncated) + ",\"requested_value\":" + requestedJson + ",\"changed_objects\":" + (!dryRun && changed ? "[" + JsonUtil.String(objectPath) + "]" : "[]") + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]") + PrefabContractJson(prefabPath, root, dryRun, changed, !dryRun && changed, hash) + "}";
        }

        static string PrefabAuthoringSpecResultJson(string prefabPath, bool dryRun, bool applied, bool partialFailure, string preconditionHash, string requiredFeaturesJson, string planJson, string appliedJson, int failedIndex, string failureJson)
        {
            return "{\"status\":\"ok\",\"command\":\"prefab.authoring.apply_spec\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"applied\":" + JsonUtil.Bool(applied) + ",\"partial_failure\":" + JsonUtil.Bool(partialFailure) + ",\"prefab_path\":" + JsonUtil.String(prefabPath) + ",\"required_feature_flags\":" + requiredFeaturesJson + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash) + ",\"plan_steps\":" + planJson + ",\"applied_steps\":" + appliedJson + ",\"failed_step_index\":" + failedIndex.ToString(CultureInfo.InvariantCulture) + ",\"failure\":" + (string.IsNullOrEmpty(failureJson) ? "null" : failureJson) + ",\"scene_saved\":false,\"saved_prefab\":" + JsonUtil.Bool(applied || partialFailure) + ",\"opens_scenes\":false,\"writes_prefab_assets\":true,\"warnings\":[\"" + (dryRun ? "dry_run_no_prefab_mutation" : applied ? "prefab_saved" : partialFailure ? "partial_apply_prefab_may_be_saved" : "no_prefab_change_applied") + "\"],\"next_valid_actions\":[\"prefab.hierarchy\",\"prefab.inspect_object\",\"prefab.inspect_component\",\"asset.info\"]}";
        }

        static string PrefabContractJson(string prefabPath, GameObject root, bool dryRun, bool changed, bool savedPrefab, string hash)
        {
            var warnings = new List<string>();
            if (dryRun) warnings.Add("dry_run_no_prefab_mutation");
            else if (savedPrefab) warnings.Add("prefab_saved");
            else if (!changed) warnings.Add("no_prefab_change_applied");
            if (root != null && MissingScriptCount(root) > 0) warnings.Add("missing_scripts_detected");
            return ",\"scene_saved\":false,\"saved_prefab\":" + JsonUtil.Bool(savedPrefab) + ",\"opens_scenes\":false,\"writes_prefab_assets\":true,\"precondition_hash\":" + JsonUtil.String(hash) + ",\"warnings\":" + StringArrayJson(warnings) + ",\"next_valid_actions\":[\"prefab.inspect_object\",\"prefab.inspect_component\",\"prefab.hierarchy\",\"asset.info\"]";
        }

        static CommandExecutionResult RequireFeatureEnabled(string settingsJson, string feature, string command)
        {
            var featuresJson = JsonUtil.ExtractObject(settingsJson, "feature_flags");
            return FeatureEnabled(featuresJson, feature) ? null : CommandExecutionResult.Error("feature_disabled", command + " requires feature flag " + feature, "{\"required_feature\":" + JsonUtil.String(feature) + "}");
        }

        static bool FeatureEnabled(string featuresJson, string feature)
        {
            return !string.IsNullOrEmpty(featuresJson) && JsonUtil.ExtractBool(featuresJson, feature, false);
        }

        static void AddRequiredFeature(List<string> required, string feature)
        {
            if (!required.Contains(feature)) required.Add(feature);
        }

        static CommandExecutionResult RequirePrefabAssetPath(string argsJson, out string prefabPath)
        {
            prefabPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, "prefab_path");
            if (string.IsNullOrEmpty(rawPath)) return CommandExecutionResult.Error("schema_validation_failed", "Argument prefab_path is required");
            try { prefabPath = PathGuard.RequireUnityAssetPath(rawPath); } catch (Exception ex) { return CommandExecutionResult.Error("invalid_field_value", "prefab_path must be Unity-relative", JsonUtil.String(ex.Message)); }
            if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)) return CommandExecutionResult.Error("invalid_field_value", "prefab_path must end with .prefab");
            if (!prefabPath.StartsWith("Assets/", StringComparison.Ordinal)) return CommandExecutionResult.Error("invalid_field_value", "prefab_path must be under Assets/");
            return null;
        }

        static CommandExecutionResult RequirePrefabObject(string argsJson, GameObject root, out GameObject target, out string objectPath)
        {
            return RequirePrefabObject(argsJson, root, "object_path", out target, out objectPath);
        }

        static CommandExecutionResult RequirePrefabObject(string argsJson, GameObject root, string field, out GameObject target, out string objectPath)
        {
            target = null;
            objectPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(objectPath)) return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            if (objectPath.Length > 512 || objectPath.IndexOf('\0') >= 0 || objectPath.IndexOf("..", StringComparison.Ordinal) >= 0) return CommandExecutionResult.Error("invalid_field_value", field + " must be a safe prefab hierarchy path");
            objectPath = NormalizeObjectPath(objectPath);
            target = FindByPath(new[] { root }, objectPath);
            return target == null ? CommandExecutionResult.Error("asset_not_found", field + " was not found in the prefab asset") : null;
        }

        static string PrefabWritePreconditionHash(string prefabPath, string operation, string operationKey)
        {
            return Sha256Prefixed(operation + "\n" + PrefabAssetStateKey(prefabPath) + "\n" + (operationKey ?? string.Empty));
        }

        static string PrefabAssetStateKey(string prefabPath)
        {
            return prefabPath + "|" + AssetDatabase.AssetPathToGUID(prefabPath) + "|" + AssetDatabase.GetAssetDependencyHash(prefabPath).ToString();
        }

        static CommandExecutionResult RequirePrefabWritePrecondition(string argsJson, string prefabPath, string operation, string preconditionHash)
        {
            var actual = JsonUtil.ExtractString(argsJson, "precondition_hash").Trim();
            if (string.IsNullOrEmpty(actual)) return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when dry_run is false", PrefabPreconditionDetailsJson(prefabPath, operation, preconditionHash));
            return string.Equals(actual, preconditionHash, StringComparison.Ordinal) ? null : CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match current prefab asset state or requested operation", PrefabPreconditionDetailsJson(prefabPath, operation, preconditionHash));
        }

        static string PrefabPreconditionDetailsJson(string prefabPath, string operation, string preconditionHash)
        {
            return "{\"required_precondition\":\"precondition_hash\",\"prefab_path\":" + JsonUtil.String(prefabPath) + ",\"operation\":" + JsonUtil.String(operation) + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash) + ",\"next_valid_actions\":[\"prefab.write_risk_report\",\"checkpoint.create\",\"retry_with_precondition_hash\"]}";
        }

        static string ComponentSelectorKey(Component component, int componentIndex)
        {
            var type = component.GetType();
            return componentIndex.ToString(CultureInfo.InvariantCulture) + "|" + (type.FullName ?? type.Name) + "|" + type.Assembly.GetName().Name;
        }

        static int MissingScriptCount(GameObject root)
        {
            var total = 0;
            CountMissingScripts(root, ref total);
            return total;
        }

        static void CountMissingScripts(GameObject gameObject, ref int total)
        {
            if (gameObject == null) return;
            var components = gameObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++) if (components[index] == null) total++;
            for (var index = 0; index < gameObject.transform.childCount; index++) CountMissingScripts(gameObject.transform.GetChild(index).gameObject, ref total);
        }

        static string BuildPrefabNestedArgs(string prefabPath, string argsJson, bool dryRun, string preconditionHash)
        {
            var body = TrimObjectBody(argsJson);
            var prefix = "\"prefab_path\":" + JsonUtil.String(prefabPath) + ",\"dry_run\":" + JsonUtil.Bool(dryRun);
            if (!string.IsNullOrEmpty(preconditionHash)) prefix += ",\"precondition_hash\":" + JsonUtil.String(preconditionHash);
            return "{" + prefix + (string.IsNullOrEmpty(body) ? string.Empty : "," + body) + "}";
        }

        static string StepsStableJson(List<SpecStep> steps)
        {
            var builder = new StringBuilder("[");
            for (var index = 0; index < steps.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append("{\"command\":").Append(JsonUtil.String(steps[index].Command)).Append(",\"args\":").Append(steps[index].ArgsJson).Append('}');
            }
            return builder.Append(']').ToString();
        }

        static string ErrorJson(CommandExecutionResult result)
        {
            return "{\"status\":\"error\",\"code\":" + JsonUtil.String(result.ErrorCode) + ",\"message\":" + JsonUtil.String(result.ErrorMessage) + ",\"details\":" + (string.IsNullOrEmpty(result.ErrorDetailsJson) ? "null" : result.ErrorDetailsJson) + "}";
        }

        static string StringArrayJson(List<string> values)
        {
            var builder = new StringBuilder("[");
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append(JsonUtil.String(values[index]));
            }
            return builder.Append(']').ToString();
        }

        static string TrimObjectBody(string json)
        {
            var value = string.IsNullOrEmpty(json) ? "{}" : json.Trim();
            return value.Length >= 2 && value[0] == '{' && value[value.Length - 1] == '}' ? value.Substring(1, value.Length - 2).Trim() : string.Empty;
        }

        static CommandExecutionResult RequireSerializedTarget(string argsJson, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex, out SerializedProperty property)
        {
            component = null; componentIndex = -1; property = null;
            var sceneResult = RequireLoadedScene(argsJson, out scene, out scenePath);
            if (sceneResult != null) { target = null; objectPath = string.Empty; return sceneResult; }
            var objectResult = RequireSceneObject(argsJson, scene, "object_path", out target, out objectPath);
            if (objectResult != null) return objectResult;
            var componentResult = RequireComponent(argsJson, target, string.Empty, out component, out componentIndex);
            if (componentResult != null) return componentResult;
            var propertyPath = JsonUtil.ExtractString(argsJson, "property_path").Trim();
            if (string.IsNullOrEmpty(propertyPath)) return CommandExecutionResult.Error("schema_validation_failed", "Argument property_path is required");
            if (PropertyPathLooksUnsafe(propertyPath)) return CommandExecutionResult.Error("invalid_field_value", "property_path must be a SerializedProperty path, not a URL or filesystem path");
            var serializedObject = new SerializedObject(component);
            property = serializedObject.FindProperty(propertyPath);
            if (property == null) return CommandExecutionResult.Error("asset_not_found", "Serialized property was not found");
            if (property.isArray && property.propertyType != SerializedPropertyType.String) return CommandExecutionResult.Error("invalid_field_value", "arrays/lists are not supported by this first serialized write slice");
            if (property.propertyType == SerializedPropertyType.ManagedReference) return CommandExecutionResult.Error("invalid_field_value", "managed references are not supported by this first serialized write slice");
            return null;
        }

        static CommandExecutionResult RequireSerializedArrayTarget(string argsJson, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex, out SerializedProperty property)
        {
            var result = RequireSerializedTargetBase(argsJson, out scene, out scenePath, out target, out objectPath, out component, out componentIndex, out property);
            if (result != null) return result;
            if (!property.isArray || property.propertyType == SerializedPropertyType.String) return CommandExecutionResult.Error("invalid_field_value", "property_path must reference a serialized array/list field");
            if (property.arraySize > MaxSerializedArrayElements) return CommandExecutionResult.Error("invalid_field_value", "serialized.array.edit only supports arrays/lists up to " + MaxSerializedArrayElements.ToString(CultureInfo.InvariantCulture) + " elements");
            return null;
        }

        static CommandExecutionResult RequireSerializedTargetBase(string argsJson, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex, out SerializedProperty property)
        {
            component = null; componentIndex = -1; property = null;
            var sceneResult = RequireLoadedScene(argsJson, out scene, out scenePath);
            if (sceneResult != null) { target = null; objectPath = string.Empty; return sceneResult; }
            var objectResult = RequireSceneObject(argsJson, scene, "object_path", out target, out objectPath);
            if (objectResult != null) return objectResult;
            var componentResult = RequireComponent(argsJson, target, string.Empty, out component, out componentIndex);
            if (componentResult != null) return componentResult;
            var propertyPath = JsonUtil.ExtractString(argsJson, "property_path").Trim();
            if (string.IsNullOrEmpty(propertyPath)) return CommandExecutionResult.Error("schema_validation_failed", "Argument property_path is required");
            if (PropertyPathLooksUnsafe(propertyPath)) return CommandExecutionResult.Error("invalid_field_value", "property_path must be a SerializedProperty path, not a URL or filesystem path");
            var serializedObject = new SerializedObject(component);
            property = serializedObject.FindProperty(propertyPath);
            return property == null ? CommandExecutionResult.Error("asset_not_found", "Serialized property was not found") : null;
        }

        static CommandExecutionResult PreparePropertyValue(string json, SerializedProperty property, out string requestedJson, out Action apply)
        {
            requestedJson = "null"; apply = null;
            var result = PreparePropertyValueSetter(json, property, out requestedJson, out var setter);
            if (result != null) return result;
            apply = () => setter(property);
            return null;
        }

        static CommandExecutionResult PreparePropertyValueSetter(string json, SerializedProperty property, out string requestedJson, out Action<SerializedProperty> apply)
        {
            requestedJson = "null"; apply = null;
            var valueFields = CountValueFields(json);
            if (valueFields != 1) return CommandExecutionResult.Error("schema_validation_failed", "Provide exactly one value_* field");
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    if (!HasJsonProperty(json, "value_bool")) return TypeMismatch(property, "value_bool", out requestedJson, out apply);
                    var b = JsonUtil.ExtractBool(json, "value_bool", false); requestedJson = JsonUtil.Bool(b); apply = target => target.boolValue = b; return null;
                case SerializedPropertyType.Integer:
                    if (!HasJsonProperty(json, "value_int")) return TypeMismatch(property, "value_int", out requestedJson, out apply);
                    var i = JsonUtil.ExtractInt(json, "value_int", 0); requestedJson = i.ToString(CultureInfo.InvariantCulture); apply = target => target.intValue = i; return null;
                case SerializedPropertyType.Float:
                    if (!TryExtractFloatField(json, "value_float", out var f)) return TypeMismatch(property, "value_float", out requestedJson, out apply);
                    requestedJson = Number(f); apply = target => target.floatValue = f; return null;
                case SerializedPropertyType.String:
                    if (!HasJsonProperty(json, "value_string")) return TypeMismatch(property, "value_string", out requestedJson, out apply);
                    var s = JsonUtil.ExtractString(json, "value_string"); requestedJson = JsonUtil.String(s); apply = target => target.stringValue = s; return null;
                case SerializedPropertyType.Enum:
                    if (!HasJsonProperty(json, "value_enum")) return TypeMismatch(property, "value_enum", out requestedJson, out apply);
                    var e = JsonUtil.ExtractString(json, "value_enum"); var enumIndex = EnumIndex(property, e); if (enumIndex < 0) return CommandExecutionResult.Error("invalid_field_value", "value_enum must match an enum display name or numeric enum index"); requestedJson = EnumValueJson(property, enumIndex); apply = target => target.enumValueIndex = enumIndex; return null;
                case SerializedPropertyType.Vector2:
                    if (!TryExtractVector2(json, "value_vector2", out var v2)) return TypeMismatch(property, "value_vector2", out requestedJson, out apply);
                    requestedJson = Vector2Json(v2); apply = target => target.vector2Value = v2; return null;
                case SerializedPropertyType.Vector3:
                    if (!TryExtractVector3(json, "value_vector3", out var v3)) return TypeMismatch(property, "value_vector3", out requestedJson, out apply);
                    requestedJson = Vector3Json(v3); apply = target => target.vector3Value = v3; return null;
                case SerializedPropertyType.Vector4:
                    if (!TryExtractVector4(json, "value_vector4", out var v4)) return TypeMismatch(property, "value_vector4", out requestedJson, out apply);
                    requestedJson = Vector4Json(v4); apply = target => target.vector4Value = v4; return null;
                case SerializedPropertyType.Color:
                    if (!TryExtractVector4(json, "value_color", out var c)) return TypeMismatch(property, "value_color", out requestedJson, out apply);
                    requestedJson = Vector4Json(c); apply = target => target.colorValue = new Color(c.x, c.y, c.z, c.w); return null;
            }
            return CommandExecutionResult.Error("invalid_field_value", "property type is not supported by serialized.property.set");
        }

        static CommandExecutionResult ResolveReferenceSource(string argsJson, Scene scene, SerializedProperty property, out UnityEngine.Object source, out string label)
        {
            source = null; label = "null";
            var clear = JsonUtil.ExtractBool(argsJson, "clear_reference", false);
            var sourceObjectPath = JsonUtil.ExtractString(argsJson, "source_object_path");
            var sourceAssetPath = JsonUtil.ExtractString(argsJson, "source_asset_path");
            var selectorCount = (clear ? 1 : 0) + (!string.IsNullOrEmpty(sourceObjectPath) ? 1 : 0) + (!string.IsNullOrEmpty(sourceAssetPath) ? 1 : 0);
            if (selectorCount != 1) return CommandExecutionResult.Error("schema_validation_failed", "Provide exactly one of clear_reference, source_object_path, or source_asset_path");
            if (clear) return null;
            if (!string.IsNullOrEmpty(sourceAssetPath))
            {
                string path;
                try { path = PathGuard.RequireUnityAssetPath(sourceAssetPath); } catch (Exception ex) { return CommandExecutionResult.Error("invalid_field_value", "source_asset_path must be Unity-relative", JsonUtil.String(ex.Message)); }
                source = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (source == null) return CommandExecutionResult.Error("asset_not_found", "source_asset_path was not found");
                label = path;
            }
            else
            {
                var objectResult = RequireSceneObject(argsJson, scene, "source_object_path", out var sourceObject, out var normalizedPath);
                if (objectResult != null) return objectResult;
                var wantsComponent = HasJsonProperty(argsJson, "source_component_index") || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "source_component_type")) || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "source_full_type_name"));
                if (wantsComponent)
                {
                    var componentResult = RequireComponent(argsJson, sourceObject, "source_", out var sourceComponent, out var componentIndex);
                    if (componentResult != null) return componentResult;
                    source = sourceComponent;
                    label = normalizedPath + "#" + componentIndex;
                }
                else
                {
                    source = sourceObject;
                    label = normalizedPath;
                }
            }
            var expected = ObjectReferenceExpectedType(property);
            if (expected != null && source != null && !expected.IsAssignableFrom(source.GetType())) return CommandExecutionResult.Error("invalid_field_value", "source reference is not assignable to the serialized field type", "{\"expected_type\":" + JsonUtil.String(expected.FullName ?? expected.Name) + ",\"source_type\":" + JsonUtil.String(source.GetType().FullName ?? source.GetType().Name) + "}");
            return null;
        }

        static CommandExecutionResult PrepareArrayEditOperation(string argsJson, Scene scene, SerializedProperty property, out string operation, out string requestedJson, out bool changed, out Action apply)
        {
            operation = JsonUtil.ExtractString(argsJson, "operation").Trim().ToLowerInvariant(); requestedJson = "null"; changed = false; apply = null;
            if (string.IsNullOrEmpty(operation)) return CommandExecutionResult.Error("schema_validation_failed", "Argument operation is required");
            var supportedResult = RequireSupportedArrayElements(property);
            if (supportedResult != null) return supportedResult;
            switch (operation)
            {
                case "append":
                {
                    if (HasJsonProperty(argsJson, "index") || HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "append does not accept index or to_index");
                    if (property.arraySize >= MaxSerializedArrayElements) return CommandExecutionResult.Error("invalid_field_value", "append would exceed the serialized.array.edit element limit");
                    var valueResult = PrepareArrayElementValue(argsJson, scene, property, out var valueJson, out var sourceLabel, out var setter);
                    if (valueResult != null) return valueResult;
                    var index = property.arraySize;
                    requestedJson = "{\"operation\":\"append\",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"value\":" + valueJson + SourceJsonFragment(sourceLabel) + "}";
                    changed = true;
                    apply = () => { var targetIndex = property.arraySize; property.InsertArrayElementAtIndex(targetIndex); setter(property.GetArrayElementAtIndex(targetIndex)); };
                    return null;
                }
                case "insert":
                {
                    if (HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "insert does not accept to_index");
                    if (property.arraySize >= MaxSerializedArrayElements) return CommandExecutionResult.Error("invalid_field_value", "insert would exceed the serialized.array.edit element limit");
                    var indexResult = RequireArrayIndex(argsJson, "index", 0, property.arraySize, out var index);
                    if (indexResult != null) return indexResult;
                    var valueResult = PrepareArrayElementValue(argsJson, scene, property, out var valueJson, out var sourceLabel, out var setter);
                    if (valueResult != null) return valueResult;
                    requestedJson = "{\"operation\":\"insert\",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"value\":" + valueJson + SourceJsonFragment(sourceLabel) + "}";
                    changed = true;
                    apply = () => { property.InsertArrayElementAtIndex(index); setter(property.GetArrayElementAtIndex(index)); };
                    return null;
                }
                case "remove_at":
                {
                    if (HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "remove_at does not accept to_index");
                    var payloadResult = RejectArrayEditPayload(argsJson, "remove_at");
                    if (payloadResult != null) return payloadResult;
                    var indexResult = RequireArrayIndex(argsJson, "index", 0, property.arraySize - 1, out var index);
                    if (indexResult != null) return indexResult;
                    requestedJson = "{\"operation\":\"remove_at\",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + "}";
                    changed = true;
                    apply = () => DeleteArrayElementAtIndexFully(property, index);
                    return null;
                }
                case "clear":
                {
                    if (HasJsonProperty(argsJson, "index") || HasJsonProperty(argsJson, "to_index")) return CommandExecutionResult.Error("schema_validation_failed", "clear does not accept index or to_index");
                    var payloadResult = RejectArrayEditPayload(argsJson, "clear");
                    if (payloadResult != null) return payloadResult;
                    requestedJson = "{\"operation\":\"clear\",\"previous_array_size\":" + property.arraySize.ToString(CultureInfo.InvariantCulture) + "}";
                    changed = property.arraySize > 0;
                    apply = () => property.ClearArray();
                    return null;
                }
                case "move":
                {
                    var payloadResult = RejectArrayEditPayload(argsJson, "move");
                    if (payloadResult != null) return payloadResult;
                    var indexResult = RequireArrayIndex(argsJson, "index", 0, property.arraySize - 1, out var index);
                    if (indexResult != null) return indexResult;
                    var toIndexResult = RequireArrayIndex(argsJson, "to_index", 0, property.arraySize - 1, out var toIndex);
                    if (toIndexResult != null) return toIndexResult;
                    requestedJson = "{\"operation\":\"move\",\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"to_index\":" + toIndex.ToString(CultureInfo.InvariantCulture) + "}";
                    changed = index != toIndex;
                    apply = () => property.MoveArrayElement(index, toIndex);
                    return null;
                }
            }
            return CommandExecutionResult.Error("invalid_field_value", "operation must be append, insert, remove_at, clear, or move");
        }

        static CommandExecutionResult PrepareArrayElementValue(string argsJson, Scene scene, SerializedProperty property, out string valueJson, out string sourceLabel, out Action<SerializedProperty> setter)
        {
            valueJson = "null"; sourceLabel = string.Empty; setter = null;
            if (property.arraySize == 0) return CommandExecutionResult.Error("invalid_field_value", "append/insert require an existing array element for type inference; empty arrays are not supported by this slice");
            var template = property.GetArrayElementAtIndex(property.arraySize - 1);
            var supportedResult = RequireSupportedArrayElement(template);
            if (supportedResult != null) return supportedResult;
            if (template.propertyType == SerializedPropertyType.ObjectReference)
            {
                if (CountValueFields(argsJson) != 0) return CommandExecutionResult.Error("schema_validation_failed", "object-reference array elements require source_object_path, source_asset_path, or clear_reference instead of value_* fields");
                var sourceResult = ResolveReferenceSource(argsJson, scene, template, out var source, out sourceLabel);
                if (sourceResult != null) return sourceResult;
                valueJson = ObjectReferenceJson(source);
                setter = element => element.objectReferenceValue = source;
                return null;
            }
            if (HasReferenceSourceFields(argsJson)) return CommandExecutionResult.Error("schema_validation_failed", "primitive array elements require exactly one value_* field and do not accept reference source fields");
            return PreparePropertyValueSetter(argsJson, template, out valueJson, out setter);
        }

        static CommandExecutionResult RequireSupportedArrayElements(SerializedProperty property)
        {
            for (var index = 0; index < property.arraySize; index++)
            {
                var result = RequireSupportedArrayElement(property.GetArrayElementAtIndex(index));
                if (result != null) return result;
            }
            return null;
        }

        static CommandExecutionResult RequireSupportedArrayElement(SerializedProperty element)
        {
            if (element == null) return CommandExecutionResult.Error("invalid_field_value", "array element could not be inspected");
            if (element.propertyType == SerializedPropertyType.ManagedReference) return CommandExecutionResult.Error("invalid_field_value", "managed references are not supported by serialized.array.edit");
            if (element.isArray && element.propertyType != SerializedPropertyType.String) return CommandExecutionResult.Error("invalid_field_value", "nested arrays/lists are not supported by serialized.array.edit");
            return ArrayElementTypeSupported(element.propertyType) ? null : CommandExecutionResult.Error("invalid_field_value", "array element property type is not supported by serialized.array.edit");
        }

        static bool ArrayElementTypeSupported(SerializedPropertyType type)
        {
            switch (type)
            {
                case SerializedPropertyType.Boolean:
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.Float:
                case SerializedPropertyType.String:
                case SerializedPropertyType.Enum:
                case SerializedPropertyType.Vector2:
                case SerializedPropertyType.Vector3:
                case SerializedPropertyType.Vector4:
                case SerializedPropertyType.Color:
                case SerializedPropertyType.ObjectReference:
                    return true;
                default:
                    return false;
            }
        }

        static CommandExecutionResult RejectArrayEditPayload(string argsJson, string operation)
        {
            return CountValueFields(argsJson) != 0 || HasReferenceSourceFields(argsJson)
                ? CommandExecutionResult.Error("schema_validation_failed", operation + " does not accept value or reference source fields")
                : null;
        }

        static CommandExecutionResult RequireArrayIndex(string argsJson, string field, int minInclusive, int maxInclusive, out int index)
        {
            index = -1;
            if (!HasJsonProperty(argsJson, field)) return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required for this array operation");
            index = JsonUtil.ExtractInt(argsJson, field, -1);
            return index < minInclusive || index > maxInclusive
                ? CommandExecutionResult.Error("invalid_field_value", field + " is outside the current array bounds")
                : null;
        }

        static void DeleteArrayElementAtIndexFully(SerializedProperty property, int index)
        {
            var before = property.arraySize;
            property.DeleteArrayElementAtIndex(index);
            if (property.arraySize == before && index < property.arraySize) property.DeleteArrayElementAtIndex(index);
        }

        static string SourceJsonFragment(string sourceLabel)
        {
            return string.IsNullOrEmpty(sourceLabel) ? string.Empty : ",\"source\":" + JsonUtil.String(sourceLabel);
        }

        static CommandExecutionResult RequireUnityEventTarget(string argsJson, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex, out string eventName, out string eventPropertyPath, out UnityEventBase unityEvent, out int listenerIndex)
        {
            unityEvent = null; eventName = string.Empty; eventPropertyPath = string.Empty; listenerIndex = -1;
            var sceneResult = RequireLoadedScene(argsJson, out scene, out scenePath);
            if (sceneResult != null) { target = null; objectPath = string.Empty; component = null; componentIndex = -1; return sceneResult; }
            var objectResult = RequireSceneObject(argsJson, scene, "object_path", out target, out objectPath);
            if (objectResult != null) { component = null; componentIndex = -1; return objectResult; }
            var componentResult = RequireComponent(argsJson, target, string.Empty, out component, out componentIndex);
            if (componentResult != null) return componentResult;
            eventName = JsonUtil.ExtractString(argsJson, "event_name").Trim();
            if (EventNameLooksUnsafe(eventName)) return CommandExecutionResult.Error("invalid_field_value", "event_name must be a safe UnityEvent field or property name");
            var eventResult = ResolveUnityEvent(component, eventName, out unityEvent, out eventPropertyPath, out var eventTypeName);
            if (eventResult != null) return eventResult;
            if (!HasJsonProperty(argsJson, "listener_index")) return CommandExecutionResult.Error("schema_validation_failed", "Argument listener_index is required");
            listenerIndex = JsonUtil.ExtractInt(argsJson, "listener_index", -1);
            var count = unityEvent.GetPersistentEventCount();
            if (listenerIndex < 0 || listenerIndex >= count) return CommandExecutionResult.Error("asset_not_found", "listener_index was not found on the selected UnityEvent", "{\"event_name\":" + JsonUtil.String(eventName) + ",\"event_type\":" + JsonUtil.String(eventTypeName) + ",\"persistent_event_count\":" + count.ToString(CultureInfo.InvariantCulture) + "}");
            return null;
        }

        static CommandExecutionResult ResolveUnityEvent(Component component, string eventName, out UnityEventBase unityEvent, out string eventPropertyPath, out string eventTypeName)
        {
            unityEvent = null; eventPropertyPath = string.Empty; eventTypeName = string.Empty;
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var type = component.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                foreach (var field in type.GetFields(flags))
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(field.FieldType)) continue;
                    var normalizedName = NormalizeEventMemberName(field.Name);
                    if (!string.Equals(field.Name, eventName, StringComparison.Ordinal) && !string.Equals(normalizedName, eventName, StringComparison.Ordinal)) continue;
                    if (unityEvent != null) return CommandExecutionResult.Error("invalid_field_value", "event_name is ambiguous on this component");
                    unityEvent = field.GetValue(component) as UnityEventBase;
                    eventPropertyPath = field.Name;
                    eventTypeName = field.FieldType.FullName ?? field.FieldType.Name;
                }
                foreach (var property in type.GetProperties(flags))
                {
                    if (!property.CanRead || property.GetIndexParameters().Length != 0 || !typeof(UnityEventBase).IsAssignableFrom(property.PropertyType)) continue;
                    var normalizedName = NormalizeEventMemberName(property.Name);
                    if (!string.Equals(property.Name, eventName, StringComparison.Ordinal) && !string.Equals(normalizedName, eventName, StringComparison.Ordinal)) continue;
                    if (unityEvent != null) continue;
                    try { unityEvent = property.GetValue(component, null) as UnityEventBase; } catch (Exception ex) { return CommandExecutionResult.Error("unity_exception", "UnityEvent property getter threw while resolving event_name", JsonUtil.String(ex.Message)); }
                    eventPropertyPath = property.Name;
                    eventTypeName = property.PropertyType.FullName ?? property.PropertyType.Name;
                }
            }
            if (unityEvent == null) return CommandExecutionResult.Error("asset_not_found", "event_name must reference an existing UnityEvent field or property on the selected component");
            if (new SerializedObject(component).FindProperty(eventPropertyPath) == null) return CommandExecutionResult.Error("invalid_field_value", "event_name must resolve to a serialized UnityEvent field for safe persistent-call writes");
            return null;
        }

        static CommandExecutionResult PreparePersistentListenerStateSerialized(Component component, string eventPropertyPath, int listenerIndex, UnityEventCallState state, out SerializedObject serializedObject, out Action apply)
        {
            serializedObject = new SerializedObject(component);
            apply = null;
            var eventProperty = serializedObject.FindProperty(eventPropertyPath);
            var calls = eventProperty == null ? null : eventProperty.FindPropertyRelative("m_PersistentCalls.m_Calls");
            if (calls == null || !calls.isArray || listenerIndex < 0 || listenerIndex >= calls.arraySize) return CommandExecutionResult.Error("asset_not_found", "UnityEvent persistent call serialized state was not found");
            var call = calls.GetArrayElementAtIndex(listenerIndex);
            var callState = call == null ? null : call.FindPropertyRelative("m_CallState");
            if (callState == null || callState.propertyType != SerializedPropertyType.Enum) return CommandExecutionResult.Error("invalid_field_value", "UnityEvent persistent call state is not writable through the expected serialized layout");
            apply = () => callState.enumValueIndex = (int)state;
            return null;
        }

        static CommandExecutionResult ParseListenerState(string argsJson, out UnityEventCallState state, out string requestedJson)
        {
            requestedJson = "null"; state = UnityEventCallState.RuntimeOnly;
            var value = JsonUtil.ExtractString(argsJson, "listener_state").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(value)) return CommandExecutionResult.Error("schema_validation_failed", "Argument listener_state is required");
            switch (value)
            {
                case "off": state = UnityEventCallState.Off; break;
                case "runtime_only": state = UnityEventCallState.RuntimeOnly; break;
                case "editor_and_runtime": state = UnityEventCallState.EditorAndRuntime; break;
                default: return CommandExecutionResult.Error("invalid_field_value", "listener_state must be off, runtime_only, or editor_and_runtime");
            }
            requestedJson = "{\"listener_state\":" + JsonUtil.String(ListenerStateName(state)) + "}";
            return null;
        }

        static string PersistentCallJson(UnityEventBase unityEvent, string eventName, int listenerIndex)
        {
            var target = unityEvent.GetPersistentTarget(listenerIndex);
            return "{\"event_name\":" + JsonUtil.String(eventName) + ",\"event_type\":" + JsonUtil.String(unityEvent.GetType().FullName ?? unityEvent.GetType().Name) + ",\"listener_index\":" + listenerIndex.ToString(CultureInfo.InvariantCulture) + ",\"target\":" + ObjectReferenceJson(target) + ",\"method_name\":" + JsonUtil.String(unityEvent.GetPersistentMethodName(listenerIndex) ?? string.Empty) + ",\"listener_state\":" + JsonUtil.String(ListenerStateName(unityEvent.GetPersistentListenerState(listenerIndex))) + "}";
        }

        static string ListenerStateName(UnityEventCallState state)
        {
            switch (state)
            {
                case UnityEventCallState.Off: return "off";
                case UnityEventCallState.EditorAndRuntime: return "editor_and_runtime";
                default: return "runtime_only";
            }
        }

        static CommandExecutionResult RequireLoadedScene(string argsJson, out Scene scene, out string scenePath)
        {
            scene = default; scenePath = string.Empty; var raw = JsonUtil.ExtractString(argsJson, "scene_path");
            if (string.IsNullOrEmpty(raw)) return CommandExecutionResult.Error("schema_validation_failed", "Argument scene_path is required");
            try { scenePath = PathGuard.RequireUnityAssetPath(raw); } catch (Exception ex) { return CommandExecutionResult.Error("invalid_field_value", "scene_path must be Unity-relative", JsonUtil.String(ex.Message)); }
            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return CommandExecutionResult.Error("invalid_field_value", "scene_path must end with .unity");
            for (var index = 0; index < SceneManager.sceneCount; index++) { var candidate = SceneManager.GetSceneAt(index); if (string.Equals(candidate.path, scenePath, StringComparison.OrdinalIgnoreCase)) { scene = candidate; break; } }
            if (!scene.IsValid() || !scene.isLoaded) return CommandExecutionResult.Error("asset_not_found", "scene_path must reference a loaded scene");
            return null;
        }

        static CommandExecutionResult RequireSceneObject(string argsJson, Scene scene, string field, out GameObject target, out string objectPath)
        {
            target = null; objectPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(objectPath)) return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            if (objectPath.Length > 512 || objectPath.IndexOf('\0') >= 0 || objectPath.IndexOf("..", StringComparison.Ordinal) >= 0) return CommandExecutionResult.Error("invalid_field_value", field + " must be a safe scene hierarchy path");
            objectPath = NormalizeObjectPath(objectPath); target = FindByPath(scene.GetRootGameObjects(), objectPath);
            return target == null ? CommandExecutionResult.Error("asset_not_found", field + " was not found in the loaded scene") : null;
        }

        static CommandExecutionResult RequireComponent(string argsJson, GameObject target, string prefix, out Component component, out int componentIndex)
        {
            component = null; componentIndex = -1;
            var hasIndex = TryReadInt(argsJson, prefix + "component_index", out var requestedIndex);
            var requestedType = JsonUtil.ExtractString(argsJson, prefix + "component_type");
            var fullType = JsonUtil.ExtractString(argsJson, prefix + "full_type_name");
            var assembly = JsonUtil.ExtractString(argsJson, prefix + "assembly_name");
            if (!hasIndex && string.IsNullOrEmpty(requestedType) && string.IsNullOrEmpty(fullType)) return CommandExecutionResult.Error("schema_validation_failed", "Argument " + prefix + "component_type, " + prefix + "full_type_name, or " + prefix + "component_index is required");
            var components = target.GetComponents<Component>();
            if (hasIndex)
            {
                if (requestedIndex < 0 || requestedIndex >= components.Length) return CommandExecutionResult.Error("asset_not_found", "Component was not found");
                component = components[requestedIndex]; componentIndex = requestedIndex;
                if (component == null) return CommandExecutionResult.Error("invalid_field_value", "Missing script components are not supported");
                if ((!string.IsNullOrEmpty(requestedType) || !string.IsNullOrEmpty(fullType) || !string.IsNullOrEmpty(assembly)) && !MatchesComponentSelector(component.GetType(), requestedType, fullType, assembly)) return CommandExecutionResult.Error("invalid_field_value", "component_index does not match component selector");
                return null;
            }
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !MatchesComponentSelector(candidate.GetType(), requestedType, fullType, assembly)) continue;
                if (component != null) return CommandExecutionResult.Error("invalid_field_value", "component selector is ambiguous on this object; pass component_index");
                component = candidate; componentIndex = index;
            }
            return component == null ? CommandExecutionResult.Error("asset_not_found", "Component was not found") : null;
        }

        static CommandExecutionResult ResolveProjectMonoBehaviourType(string argsJson, string settingsJson, out Type type, out string allowReason)
        {
            type = null; allowReason = "project_assembly_heuristic_no_settings_allowlist";
            var fullName = JsonUtil.ExtractString(argsJson, "full_type_name").Trim();
            var assemblyName = JsonUtil.ExtractString(argsJson, "assembly_name").Trim();
            if (string.IsNullOrEmpty(fullName) || string.IsNullOrEmpty(assemblyName)) return CommandExecutionResult.Error("schema_validation_failed", "full_type_name and assembly_name are required");
            var allowlist = ExtractMonoBehaviourAllowlist(settingsJson);
            if (allowlist.Count > 0)
            {
                if (!MonoBehaviourAllowedBySettings(fullName, assemblyName, allowlist))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "mono_behaviour.add requires an exact scene_authoring.mono_behaviour_allowlist match when the settings allowlist is configured", "{\"assembly_name\":" + JsonUtil.String(assemblyName) + ",\"full_type_name\":" + JsonUtil.String(fullName) + ",\"allowlist_entries\":" + allowlist.Count + "}");
                }
                allowReason = "settings_allowlist_exact_selector";
            }
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                if (!string.Equals(assembly.GetName().Name, assemblyName, StringComparison.Ordinal)) continue;
                foreach (var candidate in SafeGetTypes(assembly)) if (candidate != null && string.Equals(candidate.FullName ?? candidate.Name, fullName, StringComparison.Ordinal)) type = candidate;
            }
            if (type == null) return CommandExecutionResult.Error("asset_not_found", "MonoBehaviour type was not found by exact full_type_name and assembly_name");
            if (type.IsAbstract || !typeof(MonoBehaviour).IsAssignableFrom(type)) return CommandExecutionResult.Error("invalid_field_value", "type must be a concrete MonoBehaviour");
            if (allowlist.Count == 0 && !IsProjectAssembly(assemblyName)) return CommandExecutionResult.Error("invalid_field_value", "mono_behaviour.add only allows exact project/source assemblies, not Unity/package/framework assemblies", "{\"assembly_name\":" + JsonUtil.String(assemblyName) + "}");
            return null;
        }

        static List<MonoBehaviourAllowlistEntry> ExtractMonoBehaviourAllowlist(string settingsJson)
        {
            var entries = new List<MonoBehaviourAllowlistEntry>();
            var arrayJson = ExtractArray(settingsJson, "mono_behaviour_allowlist");
            if (string.IsNullOrEmpty(arrayJson)) return entries;
            var objects = new List<string>();
            if (!TryExtractObjectArrayItems(arrayJson, objects)) return entries;
            for (var index = 0; index < objects.Count; index++)
            {
                var assemblyName = JsonUtil.ExtractString(objects[index], "assembly_name").Trim();
                var fullName = JsonUtil.ExtractString(objects[index], "full_type_name").Trim();
                if (!string.IsNullOrEmpty(assemblyName) && !string.IsNullOrEmpty(fullName)) entries.Add(new MonoBehaviourAllowlistEntry(assemblyName, fullName));
            }
            return entries;
        }

        static bool MonoBehaviourAllowedBySettings(string fullName, string assemblyName, List<MonoBehaviourAllowlistEntry> allowlist)
        {
            for (var index = 0; index < allowlist.Count; index++)
            {
                if (string.Equals(allowlist[index].AssemblyName, assemblyName, StringComparison.Ordinal) && string.Equals(allowlist[index].FullTypeName, fullName, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        static bool IsProjectAssembly(string assemblyName)
        {
            return !assemblyName.StartsWith("Unity", StringComparison.Ordinal)
                && !assemblyName.StartsWith("System", StringComparison.Ordinal)
                && !assemblyName.StartsWith("mscorlib", StringComparison.Ordinal)
                && !assemblyName.StartsWith("netstandard", StringComparison.Ordinal)
                && !assemblyName.StartsWith("TMPro", StringComparison.Ordinal)
                && assemblyName != "com.sungeargames.unity-mcp";
        }

        static string MonoBehaviourAddJson(Scene scene, string scenePath, string objectPath, Type type, string allowReason, Component component, int componentIndex, bool dryRun)
        {
            var componentJson = component == null ? ComponentTypeJson(type, componentIndex) : ComponentJson(component, componentIndex);
            return "{\"status\":\"ok\",\"command\":\"mono_behaviour.add\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"added\":" + JsonUtil.Bool(!dryRun) + ",\"would_add\":" + JsonUtil.Bool(dryRun) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"allowlist_decision\":{\"allowed\":true,\"reason\":" + JsonUtil.String(allowReason) + "},\"component\":" + componentJson + ",\"serialized_fields_configured\":false,\"unity_event_mutation\":false,\"changed_components\":" + (dryRun ? "[]" : "[" + componentJson + "]") + SceneContractJson(scene, dryRun, !dryRun) + "}";
        }

        static string SerializedSetJson(string command, Scene scene, string scenePath, string objectPath, Component component, int componentIndex, string propertyPath, string currentJson, string requestedJson, string hash, bool dryRun, bool changed)
        {
            return "{\"status\":\"ok\",\"command\":" + JsonUtil.String(command) + ",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed) + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"component\":" + ComponentJson(component, componentIndex) + ",\"property_path\":" + JsonUtil.String(propertyPath) + ",\"current_value\":" + currentJson + ",\"requested_value\":" + requestedJson + ",\"precondition_hash\":" + JsonUtil.String(hash) + SerializedPreconditionContractJson() + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]") + SceneContractJson(scene, dryRun, !dryRun && changed) + "}";
        }

        static string SerializedArrayEditJson(Scene scene, string scenePath, string objectPath, Component component, int componentIndex, string propertyPath, string operation, string currentJson, bool currentTruncated, string requestedJson, string hash, bool dryRun, bool changed)
        {
            return "{\"status\":\"ok\",\"command\":\"serialized.array.edit\",\"operation\":" + JsonUtil.String(operation) + ",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed) + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"component\":" + ComponentJson(component, componentIndex) + ",\"property_path\":" + JsonUtil.String(propertyPath) + ",\"current_value\":" + currentJson + ",\"current_value_truncated\":" + JsonUtil.Bool(currentTruncated) + ",\"requested_value\":" + requestedJson + ",\"precondition_hash\":" + JsonUtil.String(hash) + SerializedPreconditionContractJson() + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]") + SceneContractJson(scene, dryRun, !dryRun && changed) + "}";
        }

        static string UnityEventPersistentCallSetJson(Scene scene, string scenePath, string objectPath, Component component, int componentIndex, string eventName, int listenerIndex, string currentJson, string requestedJson, string hash, bool dryRun, bool changed)
        {
            return "{\"status\":\"ok\",\"command\":\"unityevent.persistent_call.set\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed) + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"component\":" + ComponentJson(component, componentIndex) + ",\"event_name\":" + JsonUtil.String(eventName) + ",\"listener_index\":" + listenerIndex.ToString(CultureInfo.InvariantCulture) + ",\"current_value\":" + currentJson + ",\"requested_value\":" + requestedJson + ",\"precondition_hash\":" + JsonUtil.String(hash) + SerializedPreconditionContractJson() + ",\"persistent_call_array_mutation\":false,\"target_or_method_mutation\":false,\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]") + SceneContractJson(scene, dryRun, !dryRun && changed) + "}";
        }

        static string SerializedPreconditionContractJson()
        {
            return ",\"precondition_contract\":\"sgg.unity_mcp.serialized_scene_write.v2\",\"precondition_is_authorization\":false,\"dry_run_authorizes_execution\":false";
        }

        static string SceneContractJson(Scene scene, bool dryRun, bool changed)
        {
            return ",\"scene_dirty\":" + JsonUtil.Bool(scene.IsValid() && scene.isDirty) + ",\"scene_saved\":false,\"saved_prefab\":false,\"opens_scenes\":false,\"writes_prefab_assets\":false,\"loaded_scenes_only\":true,\"warnings\":[\"" + (dryRun ? "dry_run_no_scene_mutation" : changed ? "scene_dirty_not_saved" : "no_scene_change_applied") + "\"],\"next_valid_actions\":[\"scene.inspect_component\",\"scene.inspect_object\",\"scene.save\"]";
        }

        static string ComponentJson(Component component, int index)
        {
            if (component == null) return "{\"component_index\":" + index + ",\"missing\":true}";
            return ComponentTypeJson(component.GetType(), index);
        }

        static string ComponentTypeJson(Type type, int index)
        {
            return "{\"component_index\":" + index + ",\"type\":" + JsonUtil.String(type.Name) + ",\"full_type_name\":" + JsonUtil.String(type.FullName ?? type.Name) + ",\"assembly_name\":" + JsonUtil.String(type.Assembly.GetName().Name) + "}";
        }

        static string SerializedPropertyValueJson(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return JsonUtil.Bool(property.boolValue);
                case SerializedPropertyType.Float: return Number(property.floatValue);
                case SerializedPropertyType.String: return JsonUtil.String(property.stringValue ?? string.Empty);
                case SerializedPropertyType.Enum: return EnumValueJson(property, property.enumValueIndex);
                case SerializedPropertyType.Vector2: return Vector2Json(property.vector2Value);
                case SerializedPropertyType.Vector3: return Vector3Json(property.vector3Value);
                case SerializedPropertyType.Vector4: return Vector4Json(property.vector4Value);
                case SerializedPropertyType.Color: var c = property.colorValue; return Vector4Json(new Vector4(c.r, c.g, c.b, c.a));
                case SerializedPropertyType.ObjectReference: return ObjectReferenceJson(property.objectReferenceValue);
                default: return "null";
            }
        }

        static string SerializedArrayValueJson(SerializedProperty property, int maxElements, out bool truncated)
        {
            truncated = property.arraySize > maxElements;
            var limit = Math.Min(property.arraySize, maxElements);
            var builder = new StringBuilder();
            builder.Append("{\"array_size\":").Append(property.arraySize.ToString(CultureInfo.InvariantCulture)).Append(",\"elements\":[");
            for (var index = 0; index < limit; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append(SerializedPropertyValueJson(property.GetArrayElementAtIndex(index)));
            }
            builder.Append("],\"truncated\":").Append(JsonUtil.Bool(truncated)).Append('}');
            return builder.ToString();
        }

        static string ObjectReferenceJson(UnityEngine.Object value)
        {
            if (value == null) return "null";
            var path = AssetDatabase.GetAssetPath(value);
            return "{\"name\":" + JsonUtil.String(value.name) + ",\"type\":" + JsonUtil.String(value.GetType().FullName ?? value.GetType().Name) + ",\"asset_path\":" + JsonUtil.String(IsUnityPath(path) ? path : string.Empty) + ",\"identity_hash\":" + JsonUtil.String(Sha256Prefixed(UnityObjectIdentity(value))) + "}";
        }

        internal static CommandExecutionResult RequirePrecondition(string argsJson, string hash, string command)
        {
            var actual = JsonUtil.ExtractString(argsJson, "precondition_hash").Trim();
            if (string.IsNullOrEmpty(actual)) return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when " + command + " runs with dry_run:false and would change the scene", SerializedPreconditionFailureDetailsJson(command, hash, "missing"));
            return string.Equals(actual, hash, StringComparison.Ordinal) ? null : CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match the current component, property, runtime instance, or values", SerializedPreconditionFailureDetailsJson(command, hash, "stale_or_mismatched"));
        }

        static string PreconditionHash(string command, string runtimeInstanceId, string scenePath, string objectPath, Component component, int componentIndex, string propertyPath, string currentJson, string requestedJson)
        {
            var type = component.GetType();
            return SerializedSceneWritePreconditionHash(command, runtimeInstanceId, UnityObjectIdentity(component), scenePath, objectPath, componentIndex, type.FullName ?? type.Name, type.Assembly.GetName().Name, propertyPath, currentJson, requestedJson);
        }

        internal static string SerializedSceneWritePreconditionHash(string command, string runtimeInstanceId, string componentIdentity, string scenePath, string objectPath, int componentIndex, string componentType, string componentAssembly, string propertyPath, string currentJson, string requestedJson)
        {
            var currentValueHash = Sha256Prefixed(currentJson ?? "null");
            var requestedValueHash = Sha256Prefixed(requestedJson ?? "null");
            return Sha256Prefixed("serialized_scene_write.v2\n"
                + JsonUtil.String(command ?? string.Empty) + "\n"
                + JsonUtil.String(runtimeInstanceId ?? string.Empty) + "\n"
                + JsonUtil.String(componentIdentity ?? string.Empty) + "\n"
                + JsonUtil.String(scenePath ?? string.Empty) + "\n"
                + JsonUtil.String(objectPath ?? string.Empty) + "\n"
                + componentIndex.ToString(CultureInfo.InvariantCulture) + "\n"
                + JsonUtil.String(componentType ?? string.Empty) + "\n"
                + JsonUtil.String(componentAssembly ?? string.Empty) + "\n"
                + JsonUtil.String(propertyPath ?? string.Empty) + "\n"
                + currentValueHash + "\n"
                + requestedValueHash);
        }

        static string RuntimeInstanceId(McpCommandContext context)
        {
            return context != null && context.State != null ? context.State.InstanceId : string.Empty;
        }

        static CommandExecutionResult RequireEditing(string command, bool dryRun = false)
        {
            if (EditorApplication.isCompiling) return CommandExecutionResult.Error("unity_busy", command + " requires editing status; Unity is compiling", EditorEligibilityFailureDetailsJson(command, dryRun, "compiling"));
            if (EditorApplication.isUpdating) return CommandExecutionResult.Error("unity_busy", command + " requires editing status; Unity is updating assets", EditorEligibilityFailureDetailsJson(command, dryRun, "updating"));
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return CommandExecutionResult.Error("unity_busy", command + " requires Edit Mode; Unity is in or entering Play Mode", EditorEligibilityFailureDetailsJson(command, dryRun, "unsupported_in_play_mode"));
            return null;
        }

        static string EditorEligibilityFailureDetailsJson(string command, bool dryRun, string reason)
        {
            return "{\"contract\":\"sgg.unity_mcp.scene_write_eligibility.v1\",\"command\":" + JsonUtil.String(command) + ",\"reason\":" + JsonUtil.String(reason) + ",\"required_editor_status\":\"editing\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"execution_eligible\":false,\"mutation_state\":\"not_applied\",\"dry_run_authorizes_execution\":false}";
        }

        static string SerializedPreconditionFailureDetailsJson(string command, string hash, string reason)
        {
            return "{\"contract\":\"sgg.unity_mcp.serialized_scene_write.v2\",\"command\":" + JsonUtil.String(command) + ",\"reason\":" + JsonUtil.String(reason) + ",\"precondition_hash\":" + JsonUtil.String(hash) + ",\"binding\":{\"runtime_instance\":true,\"component_instance\":true,\"component_property\":true,\"current_value_hash\":true,\"requested_value_hash\":true},\"mutation_state\":\"not_applied\",\"precondition_is_authorization\":false,\"dry_run_authorizes_execution\":false,\"value_data_included\":false}";
        }

        static CommandExecutionResult ApplySerializedMutation(string command, string undoLabel, Component component, Scene scene, SerializedObject serializedObject, Action apply, string preconditionHash)
        {
            var stage = "record_undo";
            var mutationState = "not_applied";
            try
            {
                Undo.RecordObject(component, undoLabel);
                stage = "set_serialized_value";
                apply();
                stage = "apply_modified_properties";
                mutationState = "application_uncertain";
                if (!serializedObject.ApplyModifiedProperties())
                {
                    return SerializedWriteFailure(command, stage, "apply_returned_false", string.Empty, "not_applied", preconditionHash);
                }
                mutationState = "applied";
                stage = "mark_dirty";
                EditorUtility.SetDirty(component);
                EditorSceneManager.MarkSceneDirty(scene);
                return null;
            }
            catch (Exception exception)
            {
                return SerializedWriteFailure(command, stage, "exception", exception.GetType().FullName ?? exception.GetType().Name, mutationState, preconditionHash);
            }
        }

        static CommandExecutionResult SerializedWriteFailure(string command, string stage, string failureKind, string exceptionType, string mutationState, string preconditionHash)
        {
            return CommandExecutionResult.Error("serialized_write_failed", command + " could not apply the prepared serialized write", SerializedWriteFailureDetailsJson(command, stage, failureKind, exceptionType, mutationState, preconditionHash));
        }

        internal static string SerializedWriteFailureDetailsJson(string command, string stage, string failureKind, string exceptionType, string mutationState, string preconditionHash)
        {
            return "{\"contract\":\"sgg.unity_mcp.serialized_write_failure.v1\",\"command\":" + JsonUtil.String(command) + ",\"failure_kind\":" + JsonUtil.String(failureKind) + ",\"failure_stage\":" + JsonUtil.String(stage) + ",\"exception_type\":" + JsonUtil.String(exceptionType) + ",\"mutation_state\":" + JsonUtil.String(mutationState) + ",\"scene_saved\":false,\"precondition_hash\":" + JsonUtil.String(preconditionHash) + ",\"value_data_included\":false,\"automatic_retry_safe\":false,\"write_replayed\":false}";
        }

        static string UnityObjectIdentity(UnityEngine.Object value)
        {
            if (value == null) return "null";
            var globalId = string.Empty;
            try { globalId = GlobalObjectId.GetGlobalObjectIdSlow(value).ToString(); } catch { globalId = "unavailable"; }
            // GetHashCode carries Unity's loaded-object identity on 2022.3 without relying on the Unity 6-only EntityId API.
            return (value.GetType().FullName ?? value.GetType().Name) + "|" + globalId + "|" + value.GetHashCode().ToString(CultureInfo.InvariantCulture) + "|" + LoadedObjectGenerations.GetValue(value, _ => new LoadedObjectGeneration()).Token;
        }

        static string EnumValueJson(SerializedProperty property, int index)
        {
            return JsonUtil.String(property.enumDisplayNames != null && index >= 0 && index < property.enumDisplayNames.Length ? property.enumDisplayNames[index] : index.ToString(CultureInfo.InvariantCulture));
        }

        sealed class LoadedObjectGeneration
        {
            internal readonly string Token = Guid.NewGuid().ToString("N");
        }

        static bool MatchesComponentSelector(Type type, string componentType, string fullTypeName, string assemblyName)
        {
            if (!string.IsNullOrEmpty(assemblyName) && !string.Equals(type.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(fullTypeName)) return string.Equals(type.FullName ?? type.Name, fullTypeName, StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrEmpty(componentType) && (string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName ?? type.Name, componentType, StringComparison.OrdinalIgnoreCase));
        }

        static int ComponentIndex(GameObject gameObject, Component component) { var components = gameObject.GetComponents<Component>(); for (var i = 0; i < components.Length; i++) if (components[i] == component) return i; return -1; }
        static bool TryReadInt(string json, string property, out int value) { value = JsonUtil.ExtractInt(json, property, int.MinValue); return HasJsonProperty(json, property); }
        static bool HasJsonProperty(string json, string property) { return !string.IsNullOrEmpty(json) && json.IndexOf("\"" + property + "\"", StringComparison.Ordinal) >= 0; }
        static bool HasReferenceSourceFields(string json) { var fields = new[] { "clear_reference", "source_object_path", "source_component_index", "source_component_type", "source_full_type_name", "source_assembly_name", "source_asset_path" }; foreach (var field in fields) if (HasJsonProperty(json, field)) return true; return false; }
        static bool IsUnityPath(string path) { return !string.IsNullOrEmpty(path) && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)); }
        static bool PropertyPathLooksUnsafe(string p) { return string.IsNullOrEmpty(p) || p.Length > 512 || p.IndexOf("://", StringComparison.Ordinal) >= 0 || p.IndexOf('\\') >= 0 || p.IndexOf('\0') >= 0 || p.IndexOf('"') >= 0 || p.IndexOf('{') >= 0 || p.IndexOf('}') >= 0; }
        static bool EventNameLooksUnsafe(string p) { return string.IsNullOrEmpty(p) || p.Length > 128 || p.IndexOf("://", StringComparison.Ordinal) >= 0 || p.IndexOf('/') >= 0 || p.IndexOf('\\') >= 0 || p.IndexOf('\0') >= 0 || p.IndexOf('"') >= 0 || p.IndexOf('{') >= 0 || p.IndexOf('}') >= 0; }
        static string NormalizeEventMemberName(string name) { if (name.StartsWith("m_", StringComparison.Ordinal) && name.Length > 2) { var trimmed = name.Substring(2); return char.ToLowerInvariant(trimmed[0]) + trimmed.Substring(1); } return name; }
        static int CountValueFields(string json) { var fields = new[] { "value_bool", "value_int", "value_float", "value_string", "value_enum", "value_vector2", "value_vector3", "value_vector4", "value_color" }; var count = 0; foreach (var f in fields) if (HasJsonProperty(json, f)) count++; return count; }
        static CommandExecutionResult TypeMismatch(SerializedProperty property, string field, out string requestedJson, out Action apply) { requestedJson = "null"; apply = null; return CommandExecutionResult.Error("invalid_field_value", field + " is required for property type " + property.propertyType); }
        static CommandExecutionResult TypeMismatch(SerializedProperty property, string field, out string requestedJson, out Action<SerializedProperty> apply) { requestedJson = "null"; apply = null; return CommandExecutionResult.Error("invalid_field_value", field + " is required for property type " + property.propertyType); }
        static int EnumIndex(SerializedProperty p, string value) { if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric) && numeric >= 0 && numeric < p.enumDisplayNames.Length) return numeric; for (var i = 0; i < p.enumDisplayNames.Length; i++) if (string.Equals(p.enumDisplayNames[i], value, StringComparison.OrdinalIgnoreCase) || string.Equals(p.enumNames[i], value, StringComparison.OrdinalIgnoreCase)) return i; return -1; }
        static Type ObjectReferenceExpectedType(SerializedProperty property) { var text = property.type; if (string.IsNullOrEmpty(text) || !text.StartsWith("PPtr<$", StringComparison.Ordinal)) return typeof(UnityEngine.Object); var end = text.IndexOf('>'); var name = end > 6 ? text.Substring(6, end - 6) : string.Empty; foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies()) foreach (var type in SafeGetTypes(assembly)) if (type != null && (type.Name == name || type.FullName == name) && typeof(UnityEngine.Object).IsAssignableFrom(type)) return type; return typeof(UnityEngine.Object); }
        static Type[] SafeGetTypes(Assembly assembly) { try { return assembly.GetTypes(); } catch (ReflectionTypeLoadException ex) { return ex.Types ?? new Type[0]; } catch { return new Type[0]; } }
        static GameObject FindByPath(GameObject[] roots, string path) { var parts = NormalizeObjectPath(path).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries); foreach (var root in roots) { if (parts.Length == 0 || root.name != parts[0]) continue; var current = root; for (var i = 1; i < parts.Length && current != null; i++) current = FindChild(current, parts[i]); if (current != null) return current; } return null; }
        static GameObject FindChild(GameObject parent, string name) { for (var i = 0; i < parent.transform.childCount; i++) { var child = parent.transform.GetChild(i).gameObject; if (child.name == name) return child; } return null; }
        static string NormalizeObjectPath(string path) { return string.IsNullOrEmpty(path) ? "/" : path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path; }
        static bool TryExtractFloatField(string json, string field, out float value) { value = 0f; var marker = "\"" + field + "\""; var at = json.IndexOf(marker, StringComparison.Ordinal); if (at < 0) return false; var colon = json.IndexOf(':', at + marker.Length); if (colon < 0) return false; var end = colon + 1; while (end < json.Length && char.IsWhiteSpace(json[end])) end++; var start = end; while (end < json.Length && "-+.0123456789eE".IndexOf(json[end]) >= 0) end++; return float.TryParse(json.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value); }
        static bool TryExtractVector2(string json, string field, out Vector2 v) { v = Vector2.zero; var n = ExtractFloatArray(json, field, 2); if (n.Count != 2) return false; v = new Vector2(n[0], n[1]); return true; }
        static bool TryExtractVector3(string json, string field, out Vector3 v) { v = Vector3.zero; var n = ExtractFloatArray(json, field, 3); if (n.Count != 3) return false; v = new Vector3(n[0], n[1], n[2]); return true; }
        static bool TryExtractVector4(string json, string field, out Vector4 v) { v = Vector4.zero; var n = ExtractFloatArray(json, field, 4); if (n.Count != 4) return false; v = new Vector4(n[0], n[1], n[2], n[3]); return true; }
        static List<float> ExtractFloatArray(string json, string field, int max) { var values = new List<float>(); var at = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal); if (at < 0) return values; var start = json.IndexOf('[', at); var end = start < 0 ? -1 : json.IndexOf(']', start); if (end < 0) return values; var token = new StringBuilder(); for (var i = start + 1; i < end && values.Count < max; i++) { var ch = json[i]; if ("-+.0123456789eE".IndexOf(ch) >= 0) token.Append(ch); else FlushFloat(token, values, max); } FlushFloat(token, values, max); return values; }
        static void FlushFloat(StringBuilder token, List<float> values, int max) { if (token.Length == 0 || values.Count >= max) { token.Length = 0; return; } if (float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) values.Add(f); token.Length = 0; }
        static string Vector2Json(Vector2 v) { return "[" + Number(v.x) + "," + Number(v.y) + "]"; }
        static string Vector3Json(Vector3 v) { return "[" + Number(v.x) + "," + Number(v.y) + "," + Number(v.z) + "]"; }
        static string Vector4Json(Vector4 v) { return "[" + Number(v.x) + "," + Number(v.y) + "," + Number(v.z) + "," + Number(v.w) + "]"; }
        static string Number(float value) { return value.ToString("G9", CultureInfo.InvariantCulture); }
        static string Sha256Prefixed(string value) { using (var sha = SHA256.Create()) return "sha256:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant(); }

        static string ExtractArray(string json, string property)
        {
            var colon = FindPropertyColon(json, property);
            if (colon < 0) return string.Empty;
            var start = IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0 || json[start] != '[') return string.Empty;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped) { escaped = false; continue; }
                if (character == '\\') { escaped = true; continue; }
                if (character == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (character == '[') depth++;
                else if (character == ']') { depth--; if (depth == 0) return json.Substring(start, index - start + 1); }
            }
            return string.Empty;
        }

        static bool TryExtractObjectArrayItems(string arrayJson, List<string> items)
        {
            var start = IndexOfNextNonWhitespace(arrayJson, 0);
            if (start < 0 || arrayJson[start] != '[') return false;
            var depth = 0;
            var objectStart = -1;
            var inString = false;
            var escaped = false;
            for (var index = start + 1; index < arrayJson.Length; index++)
            {
                var character = arrayJson[index];
                if (escaped) { escaped = false; continue; }
                if (character == '\\') { escaped = true; continue; }
                if (character == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (character == '{') { if (depth == 0) objectStart = index; depth++; }
                else if (character == '}') { depth--; if (depth < 0) return false; if (depth == 0 && objectStart >= 0) { items.Add(arrayJson.Substring(objectStart, index - objectStart + 1)); objectStart = -1; } }
                else if (character == ']' && depth == 0) return true;
            }
            return false;
        }

        static int FindPropertyColon(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property)) return -1;
            var pattern = "\"" + property + "\"";
            var propertyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            return propertyIndex < 0 ? -1 : json.IndexOf(':', propertyIndex + pattern.Length);
        }

        static int IndexOfNextNonWhitespace(string value, int start)
        {
            for (var index = start; index < value.Length; index++) if (!char.IsWhiteSpace(value[index])) return index;
            return -1;
        }

        sealed class MonoBehaviourAllowlistEntry
        {
            public MonoBehaviourAllowlistEntry(string assemblyName, string fullTypeName)
            {
                AssemblyName = assemblyName;
                FullTypeName = fullTypeName;
            }

            public string AssemblyName { get; private set; }
            public string FullTypeName { get; private set; }
        }

        sealed class SpecStep
        {
            public SpecStep(string command, string argsJson)
            {
                Command = command;
                ArgsJson = argsJson;
            }

            public string Command { get; private set; }
            public string ArgsJson { get; private set; }
        }
    }
}
