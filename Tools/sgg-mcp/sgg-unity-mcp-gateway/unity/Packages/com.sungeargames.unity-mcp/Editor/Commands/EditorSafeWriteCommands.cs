using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class EditorSafeWriteCommands
    {
        const string FeatureFlag = "editor_safe_writes";
        const string SceneCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"}},\"required\":[\"scene_path\"],\"additionalProperties\":false}";
        const string SceneUnloadSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"discard_unsaved\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\"],\"additionalProperties\":false}";
        const string GameObjectCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"parent_path\":{\"type\":\"string\"},\"active\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"name\"],\"additionalProperties\":false}";
        const string GameObjectPathSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string GameObjectWriteRiskReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.unity$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"planned_operation\":{\"type\":\"string\",\"enum\":[\"delete\",\"reparent\",\"component.remove\",\"material.assign\",\"component.property.set\",\"material.property.set\"]},\"max_children\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"max_components\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"max_materials\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":32}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabWriteRiskReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"planned_operation\":{\"type\":\"string\",\"enum\":[\"object.create\",\"object.delete\",\"component.add\",\"component.remove\",\"component.property.set\",\"variant.create\",\"apply_overrides\"]},\"max_children\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"max_components\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"max_references\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"max_materials\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":32},\"max_yaml_issues\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":64},\"max_yaml_scan_bytes\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":1048576}},\"required\":[\"prefab_path\"],\"additionalProperties\":false}";
        const string PrefabEditPreviewSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"operation\":{\"type\":\"string\",\"enum\":[\"object.create\",\"object.delete\",\"component.add\",\"component.remove\",\"component.property.set\",\"variant.create\",\"apply_overrides\"]},\"object_path\":{\"type\":\"string\",\"maxLength\":512},\"parent_path\":{\"type\":\"string\",\"maxLength\":512},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"property_name\":{\"type\":\"string\",\"maxLength\":64},\"bool_value\":{\"type\":\"boolean\"},\"number_value\":{\"type\":\"number\"},\"string_value\":{\"type\":\"string\",\"maxLength\":128},\"color_r\":{\"type\":\"number\"},\"color_g\":{\"type\":\"number\"},\"color_b\":{\"type\":\"number\"},\"color_a\":{\"type\":\"number\"},\"variant_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"max_yaml_issues\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":64},\"max_yaml_scan_bytes\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":1048576},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"operation\"],\"additionalProperties\":false}";
        const string PrefabComponentModifySchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"enabled\":{\"type\":\"boolean\"},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabVariantCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"variant_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"variant_path\"],\"additionalProperties\":false}";
        const string PrefabComponentPropertySetSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"property_name\":{\"type\":\"string\",\"maxLength\":64},\"bool_value\":{\"type\":\"boolean\"},\"number_value\":{\"type\":\"number\"},\"string_value\":{\"type\":\"string\",\"maxLength\":128},\"color_r\":{\"type\":\"number\"},\"color_g\":{\"type\":\"number\"},\"color_b\":{\"type\":\"number\"},\"color_a\":{\"type\":\"number\"},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"object_path\",\"property_name\"],\"additionalProperties\":false}";
        const string PrefabComponentAddSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"object_path\",\"component_type\"],\"additionalProperties\":false}";
        const string PrefabComponentRemoveSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabObjectCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"parent_path\":{\"type\":\"string\",\"maxLength\":512},\"active\":{\"type\":\"boolean\"},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"name\"],\"additionalProperties\":false}";
        const string PrefabUguiObjectCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"parent_path\":{\"type\":\"string\",\"maxLength\":512},\"active\":{\"type\":\"boolean\"},\"anchor_min\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchor_max\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"pivot\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchored_position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"size_delta\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"sibling_index\":{\"type\":\"integer\",\"minimum\":-1,\"maximum\":10000},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"prefab_path\",\"name\"],\"additionalProperties\":false}";
        const string PrefabUguiComponentAddSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"prefab_path\",\"object_path\",\"component_type\"],\"additionalProperties\":false}";
        const string PrefabUguiTextSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"text\":{\"type\":\"string\",\"maxLength\":4096},\"font_size\":{\"type\":\"number\",\"minimum\":0,\"maximum\":4096},\"color\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"raycast_target\":{\"type\":\"boolean\"},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"prefab_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabObjectDeleteSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabApplyOverridesSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.unity$\"},\"object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"prefab_path\":{\"type\":\"string\",\"pattern\":\"^Assets/.+\\\\.prefab$\"},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\",\"prefab_path\"],\"additionalProperties\":false}";
        const string GameObjectDuplicateSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"parent_path\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string TransformSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"position\":{\"$ref\":\"#/$defs/vector3\"},\"local_position\":{\"$ref\":\"#/$defs/vector3\"},\"rotation_euler\":{\"$ref\":\"#/$defs/vector3\"},\"local_rotation_euler\":{\"$ref\":\"#/$defs/vector3\"},\"local_scale\":{\"$ref\":\"#/$defs/vector3\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false,\"$defs\":{\"vector3\":{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"number\"},\"y\":{\"type\":\"number\"},\"z\":{\"type\":\"number\"}},\"required\":[\"x\",\"y\",\"z\"],\"additionalProperties\":false}}}";
        const string ParentSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"parent_path\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string ComponentPropertySchemaSchemaJson = "{\"type\":\"object\",\"properties\":{\"type_name\":{\"type\":\"string\",\"maxLength\":128},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":8}},\"additionalProperties\":false}";
        const string ComponentAddSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\",\"component_type\"],\"additionalProperties\":false}";
        const string ComponentSelectSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string ComponentModifySchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"enabled\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string ComponentPropertySetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\",\"maxLength\":256},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"property_name\":{\"type\":\"string\",\"maxLength\":64},\"bool_value\":{\"type\":\"boolean\"},\"number_value\":{\"type\":\"number\"},\"string_value\":{\"type\":\"string\",\"maxLength\":128},\"color_r\":{\"type\":\"number\"},\"color_g\":{\"type\":\"number\"},\"color_b\":{\"type\":\"number\"},\"color_a\":{\"type\":\"number\"},\"precondition_value_json\":{\"type\":\"string\",\"maxLength\":1024},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\",\"property_name\"],\"additionalProperties\":false}";
        const string MaterialPropertySchemaSchemaJson = "{\"type\":\"object\",\"properties\":{\"shader\":{\"type\":\"string\",\"maxLength\":256},\"material_path\":{\"type\":\"string\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":8}},\"additionalProperties\":false}";
        const string MaterialPropertiesInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"material_path\":{\"type\":\"string\",\"pattern\":\"^(Assets|Packages)/.+\\\\.mat$\"},\"max_properties\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":128}},\"required\":[\"material_path\"],\"additionalProperties\":false}";
        const string MaterialPropertySetSchemaJson = "{\"type\":\"object\",\"properties\":{\"material_path\":{\"type\":\"string\"},\"property_name\":{\"type\":\"string\",\"maxLength\":64},\"number_value\":{\"type\":\"number\"},\"color_r\":{\"type\":\"number\"},\"color_g\":{\"type\":\"number\"},\"color_b\":{\"type\":\"number\"},\"color_a\":{\"type\":\"number\"},\"vector_x\":{\"type\":\"number\"},\"vector_y\":{\"type\":\"number\"},\"vector_z\":{\"type\":\"number\"},\"vector_w\":{\"type\":\"number\"},\"texture_path\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"material_path\",\"property_name\"],\"additionalProperties\":false}";
        const string MaterialCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"material_path\":{\"type\":\"string\"},\"shader\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256}},\"required\":[\"material_path\",\"shader\"],\"additionalProperties\":false}";
        const string MaterialAssignSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"material_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"slot\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":32}},\"required\":[\"scene_path\",\"object_path\",\"material_path\"],\"additionalProperties\":false}";
        const string ObjectOutputLimitJson = "{\"max_objects\":1}";
        const string WriteRiskReportOutputLimitJson = "{\"max_children\":64,\"max_components\":64,\"max_materials\":32}";
        const string PrefabWriteRiskReportOutputLimitJson = "{\"max_children\":64,\"max_components\":64,\"max_references\":64,\"max_materials\":32,\"max_yaml_issues\":64,\"default_yaml_scan_bytes\":262144,\"max_yaml_scan_bytes\":1048576}";
        const string PrefabApplyOverridesOutputLimitJson = "{\"max_property_modifications\":32,\"max_structural_overrides\":32}";
        const string PrefabUguiAuthoringOutputLimitJson = "{\"max_objects\":1,\"max_components\":1,\"dry_run_default\":true,\"requires_precondition_hash\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":true}";
        const string PropertySchemaOutputLimitJson = "{\"max_types\":8,\"max_properties_per_type\":8}";
        const string ComponentOutputLimitJson = "{\"max_components\":1}";
        const string MaterialOutputLimitJson = "{\"max_materials\":1}";
        const string MaterialPropertyOutputLimitJson = "{\"max_materials\":1,\"max_properties\":8}";
        const string MaterialPropertiesInspectOutputLimitJson = "{\"max_materials\":1,\"max_properties\":128}";

        [McpCommand(
            "scene.create",
            Title = "Create scene",
            Description = "Create and save a new empty Unity scene asset",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = SceneCreateSchemaJson,
            Tags = new string[] { "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneCreate(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var fullPath = AbsolutePath(scenePath);
            if (File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "scene_path already exists");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
            if (!EditorSceneManager.SaveScene(scene, scenePath))
            {
                return CommandExecutionResult.Error("unity_exception", "Scene create failed");
            }

            AssetDatabase.ImportAsset(scenePath);
            return CommandExecutionResult.Ok("{\"created\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"scene_name\":" + JsonUtil.String(scene.name)
                + ",\"loaded\":" + JsonUtil.Bool(scene.isLoaded) + "}");
        }

        [McpCommand(
            "scene.unload",
            Title = "Unload scene",
            Description = "Unload one loaded Unity scene by Unity-relative path",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = SceneUnloadSchemaJson,
            Tags = new string[] { "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneUnload(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var scene = FindLoadedScene(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Ok("{\"unloaded\":false,\"scene_path\":" + JsonUtil.String(scenePath) + ",\"reason\":\"not_loaded\"}");
            }

            if (scene.isDirty && !JsonUtil.ExtractBool(context.ArgsJson, "discard_unsaved", false))
            {
                return CommandExecutionResult.Error("invalid_field_value", "scene has unsaved changes; save it or pass discard_unsaved=true");
            }

            if (SceneManager.sceneCount <= 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Cannot unload the only loaded scene");
            }

            if (SceneManager.GetActiveScene() == scene)
            {
                SetFirstOtherSceneActive(scene);
            }

            if (!EditorSceneManager.CloseScene(scene, true))
            {
                return CommandExecutionResult.Error("unity_exception", "Scene unload failed");
            }

            return CommandExecutionResult.Ok("{\"unloaded\":true,\"scene_path\":" + JsonUtil.String(scenePath) + "}");
        }

        [McpCommand(
            "gameobject.create",
            Title = "Create GameObject",
            Description = "Create an empty GameObject in a loaded scene",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = GameObjectCreateSchemaJson,
            Tags = new string[] { "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectCreate(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var sceneResult = RequireLoadedScene(context.ArgsJson, out var scene, out var scenePath);
            if (sceneResult != null)
            {
                return sceneResult;
            }

            var nameResult = RequireObjectName(context.ArgsJson, "name", out var name);
            if (nameResult != null)
            {
                return nameResult;
            }

            var parentResult = ResolveOptionalParent(context.ArgsJson, scene, out var parent, out var parentPath);
            if (parentResult != null)
            {
                return parentResult;
            }

            if (SiblingNameExists(scene, parent == null ? null : parent.transform, name, null))
            {
                return CommandExecutionResult.Error("invalid_field_value", "A sibling with this name already exists");
            }

            var active = JsonUtil.ExtractBool(context.ArgsJson, "active", true);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var plannedObjectPath = ChildSceneObjectPath(parent, name);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"created\":false,\"would_create\":true"
                    + ",\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"parent_path\":" + JsonUtil.String(parentPath)
                    + ",\"object_path\":" + JsonUtil.String(plannedObjectPath)
                    + ",\"name\":" + JsonUtil.String(name)
                    + ",\"active\":" + JsonUtil.Bool(active)
                    + ",\"changed_objects\":[]"
                    + SceneWriteContractJson(scene, true, false, new[] { "gameobject.create", "scene.hierarchy", "scene.save" }, parent)
                    + "}");
            }

            var gameObject = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(gameObject, "Create GameObject");
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            if (parent != null)
            {
                Undo.SetTransformParent(gameObject.transform, parent.transform, "Parent GameObject");
            }

            if (!active)
            {
                Undo.RecordObject(gameObject, "Set GameObject Active");
                gameObject.SetActive(false);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"created\":true,\"would_create\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"parent_path\":" + JsonUtil.String(parentPath)
                + ",\"object_path\":" + JsonUtil.String(ObjectPath(gameObject))
                + ",\"object\":" + GameObjectJson(gameObject)
                + ",\"changed_objects\":[" + JsonUtil.String(ObjectPath(gameObject)) + "]"
                + SceneWriteContractJson(scene, false, true, new[] { "scene.inspect_object", "scene.hierarchy", "scene.save" }, parent == null ? gameObject : parent)
                + "}");
        }

        [McpCommand(
            "gameobject.write_risk_report",
            Title = "GameObject write risk report",
            Description = "Read a bounded preflight risk report for a planned GameObject write",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = GameObjectWriteRiskReportSchemaJson,
            Tags = new string[] { "gameobject", "scene", "safe-write", "preflight" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = WriteRiskReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectWriteRiskReport(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (!scenePath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "scene_path must be under Assets/");
            }

            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            if (objectPath.Length > 512 || objectPath.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "object_path must be 512 characters or fewer and cannot contain NUL");
            }

            objectPath = NormalizeObjectPath(objectPath);
            var plannedOperation = JsonUtil.ExtractString(context.ArgsJson, "planned_operation");
            if (!string.IsNullOrEmpty(plannedOperation) && !IsSupportedPlannedOperation(plannedOperation))
            {
                return CommandExecutionResult.Error("invalid_field_value", "planned_operation is not supported");
            }

            var childrenResult = ReadOptionalInt(context.ArgsJson, "max_children", 0, 64, out var hasMaxChildren, out var maxChildren);
            if (childrenResult != null)
            {
                return childrenResult;
            }

            var componentsResult = ReadOptionalInt(context.ArgsJson, "max_components", 0, 64, out var hasMaxComponents, out var maxComponents);
            if (componentsResult != null)
            {
                return componentsResult;
            }

            var materialsResult = ReadOptionalInt(context.ArgsJson, "max_materials", 0, 32, out var hasMaxMaterials, out var maxMaterials);
            if (materialsResult != null)
            {
                return materialsResult;
            }

            maxChildren = hasMaxChildren ? maxChildren : 16;
            maxComponents = hasMaxComponents ? maxComponents : 16;
            maxMaterials = hasMaxMaterials ? maxMaterials : 16;

            var scene = FindLoadedScene(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Ok(WriteRiskReportJson(scenePath, objectPath, plannedOperation, false, false, false, false, false, "unknown", null, maxChildren, maxComponents, maxMaterials));
            }

            var target = FindByPath(scene.GetRootGameObjects(), objectPath);
            return CommandExecutionResult.Ok(WriteRiskReportJson(scenePath, objectPath, plannedOperation, true, scene.isDirty, target != null, scene.isDirty || IsDestructivePlannedOperation(plannedOperation), true, PrefabStatus(target), target, maxChildren, maxComponents, maxMaterials));
        }

        [McpCommand(
            "prefab.write_risk_report",
            Title = "Prefab write risk report",
            Description = "Read a bounded preflight risk report for a planned prefab asset write",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = PrefabWriteRiskReportSchemaJson,
            Tags = new string[] { "prefab", "asset", "safe-write", "preflight" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = PrefabWriteRiskReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabWriteRiskReport(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            if (!string.IsNullOrEmpty(objectPath))
            {
                if (objectPath.Length > 512 || objectPath.IndexOf('\0') >= 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "object_path must be 512 characters or fewer and cannot contain NUL");
                }

                objectPath = NormalizeObjectPath(objectPath);
            }

            var plannedOperation = JsonUtil.ExtractString(context.ArgsJson, "planned_operation");
            if (!string.IsNullOrEmpty(plannedOperation) && !IsSupportedPrefabOperation(plannedOperation))
            {
                return CommandExecutionResult.Error("invalid_field_value", "planned_operation is not supported");
            }

            var childrenResult = ReadOptionalInt(context.ArgsJson, "max_children", 0, 64, out var hasMaxChildren, out var maxChildren);
            if (childrenResult != null)
            {
                return childrenResult;
            }

            var componentsResult = ReadOptionalInt(context.ArgsJson, "max_components", 0, 64, out var hasMaxComponents, out var maxComponents);
            if (componentsResult != null)
            {
                return componentsResult;
            }

            var referencesResult = ReadOptionalInt(context.ArgsJson, "max_references", 0, 64, out var hasMaxReferences, out var maxReferences);
            if (referencesResult != null)
            {
                return referencesResult;
            }

            var materialsResult = ReadOptionalInt(context.ArgsJson, "max_materials", 0, 32, out var hasMaxMaterials, out var maxMaterials);
            if (materialsResult != null)
            {
                return materialsResult;
            }

            maxChildren = hasMaxChildren ? maxChildren : 16;
            maxComponents = hasMaxComponents ? maxComponents : 16;
            maxReferences = hasMaxReferences ? maxReferences : 16;
            maxMaterials = hasMaxMaterials ? maxMaterials : 16;

            var yamlIssuesResult = ReadOptionalInt(context.ArgsJson, "max_yaml_issues", 1, PrefabYamlIntegrity.MaxIssues, out var hasMaxYamlIssues, out var maxYamlIssues);
            if (yamlIssuesResult != null)
            {
                return yamlIssuesResult;
            }
            var yamlBytesResult = ReadOptionalInt(context.ArgsJson, "max_yaml_scan_bytes", 1024, PrefabYamlIntegrity.HardMaxScanBytes, out var hasMaxYamlScanBytes, out var maxYamlScanBytes);
            if (yamlBytesResult != null)
            {
                return yamlBytesResult;
            }
            maxYamlIssues = hasMaxYamlIssues ? maxYamlIssues : PrefabYamlIntegrity.MaxIssues;
            maxYamlScanBytes = hasMaxYamlScanBytes ? maxYamlScanBytes : PrefabYamlIntegrity.DefaultScanBytes;

            var yamlIntegrity = PrefabYamlIntegrity.AnalyzePrefabAsset(prefabPath, maxYamlIssues, maxYamlScanBytes);
            if (yamlIntegrity.ShouldSkipUnityLoad)
            {
                return CommandExecutionResult.Ok(PrefabWriteRiskReportJson(prefabPath, objectPath, plannedOperation, false, null, null, maxChildren, maxComponents, maxReferences, maxMaterials, yamlIntegrity, false, yamlIntegrity.ReadError));
            }

            bool exists;
            try
            {
                exists = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
            }
            catch (Exception)
            {
                return CommandExecutionResult.Ok(PrefabWriteRiskReportJson(prefabPath, objectPath, plannedOperation, false, null, null, maxChildren, maxComponents, maxReferences, maxMaterials, yamlIntegrity, false, "asset_import_failed"));
            }
            if (!exists)
            {
                return CommandExecutionResult.Ok(PrefabWriteRiskReportJson(prefabPath, objectPath, plannedOperation, false, null, null, maxChildren, maxComponents, maxReferences, maxMaterials, yamlIntegrity, false, "asset_import_failed"));
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var target = string.IsNullOrEmpty(objectPath) ? root : FindByPath(new[] { root }, objectPath);
                return CommandExecutionResult.Ok(PrefabWriteRiskReportJson(prefabPath, objectPath, plannedOperation, true, root, target, maxChildren, maxComponents, maxReferences, maxMaterials, yamlIntegrity, true, string.Empty));
            }
            catch (Exception)
            {
                return CommandExecutionResult.Ok(PrefabWriteRiskReportJson(prefabPath, objectPath, plannedOperation, true, null, null, maxChildren, maxComponents, maxReferences, maxMaterials, yamlIntegrity, false, "asset_load_failed"));
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.edit.preview",
            Title = "Preview prefab edit",
            Description = "Preview one future prefab edit without saving, modifying, importing, refreshing, or overwriting assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = PrefabEditPreviewSchemaJson,
            Tags = new string[] { "prefab", "asset", "safe-write", "preflight", "dry-run" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = PrefabWriteRiskReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabEditPreview(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var operation = JsonUtil.ExtractString(context.ArgsJson, "operation");
            if (string.IsNullOrEmpty(operation))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument operation is required");
            }

            if (!IsSupportedPrefabOperation(operation))
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation is not supported");
            }

            if (!JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true))
            {
                return CommandExecutionResult.Error("confirmation_required", "prefab.edit.preview is preview-only in this iteration; dry_run:false is unsupported and no prefab write was attempted");
            }

            var objectPathResult = ValidateOptionalHierarchyPath(context.ArgsJson, "object_path");
            if (objectPathResult != null)
            {
                return objectPathResult;
            }

            var parentPathResult = ValidateOptionalHierarchyPath(context.ArgsJson, "parent_path");
            if (parentPathResult != null)
            {
                return parentPathResult;
            }

            var variantPath = JsonUtil.ExtractString(context.ArgsJson, "variant_path");
            if (!string.IsNullOrEmpty(variantPath))
            {
                var variantPathResult = RequirePrefabAssetPath(context.ArgsJson, "variant_path", out variantPath);
                if (variantPathResult != null)
                {
                    return variantPathResult;
                }
            }

            var yamlIssuesResult = ReadOptionalInt(context.ArgsJson, "max_yaml_issues", 1, PrefabYamlIntegrity.MaxIssues, out var hasMaxYamlIssues, out var maxYamlIssues);
            if (yamlIssuesResult != null)
            {
                return yamlIssuesResult;
            }
            var yamlBytesResult = ReadOptionalInt(context.ArgsJson, "max_yaml_scan_bytes", 1024, PrefabYamlIntegrity.HardMaxScanBytes, out var hasMaxYamlScanBytes, out var maxYamlScanBytes);
            if (yamlBytesResult != null)
            {
                return yamlBytesResult;
            }
            maxYamlIssues = hasMaxYamlIssues ? maxYamlIssues : PrefabYamlIntegrity.MaxIssues;
            maxYamlScanBytes = hasMaxYamlScanBytes ? maxYamlScanBytes : PrefabYamlIntegrity.DefaultScanBytes;

            var yamlIntegrity = PrefabYamlIntegrity.AnalyzePrefabAsset(prefabPath, maxYamlIssues, maxYamlScanBytes);
            if (yamlIntegrity.ShouldSkipUnityLoad)
            {
                return CommandExecutionResult.Ok(PrefabEditPreviewJson(prefabPath, operation, false, null, null, variantPath, context.ArgsJson, yamlIntegrity, false, yamlIntegrity.ReadError));
            }

            bool exists;
            try
            {
                exists = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) != null;
            }
            catch (Exception)
            {
                return CommandExecutionResult.Ok(PrefabEditPreviewJson(prefabPath, operation, false, null, null, variantPath, context.ArgsJson, yamlIntegrity, false, "asset_import_failed"));
            }
            if (!exists)
            {
                return CommandExecutionResult.Ok(PrefabEditPreviewJson(prefabPath, operation, false, null, null, variantPath, context.ArgsJson, yamlIntegrity, false, "asset_import_failed"));
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
                if (!string.IsNullOrEmpty(objectPath))
                {
                    objectPath = NormalizeObjectPath(objectPath);
                }

                var target = string.IsNullOrEmpty(objectPath) ? null : FindByPath(new[] { root }, objectPath);
                return CommandExecutionResult.Ok(PrefabEditPreviewJson(prefabPath, operation, true, root, target, variantPath, context.ArgsJson, yamlIntegrity, true, string.Empty));
            }
            catch (Exception)
            {
                return CommandExecutionResult.Ok(PrefabEditPreviewJson(prefabPath, operation, true, null, null, variantPath, context.ArgsJson, yamlIntegrity, false, "asset_load_failed"));
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.component.modify",
            Title = "Modify prefab component",
            Description = "Modify one supported Component field on a prefab asset GameObject",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = PrefabComponentModifySchemaJson,
            Tags = new string[] { "prefab", "component", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabComponentModify(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var componentResult = RequirePrefabComponent(context.ArgsJson, root, out var target, out var objectPath, out var component, out var componentIndex);
                if (componentResult != null)
                {
                    return componentResult;
                }

                if (!HasJsonProperty(context.ArgsJson, "enabled"))
                {
                    return CommandExecutionResult.Error("schema_validation_failed", "Provide at least one supported component field");
                }

                var enabledProperty = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
                if (enabledProperty == null || enabledProperty.PropertyType != typeof(bool) || !enabledProperty.CanWrite)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "component does not expose a writable enabled field");
                }

                var enabled = JsonUtil.ExtractBool(context.ArgsJson, "enabled", false);
                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
                var previousEnabled = (bool)enabledProperty.GetValue(component, null);
                var changed = previousEnabled != enabled;
                var operation = "prefab.component.modify";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, objectPath + "|" + ComponentSelectorJson(component, componentIndex) + "|enabled=" + JsonUtil.Bool(enabled));
                if (!dryRun && changed)
                {
                    var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                    if (preconditionResult != null)
                    {
                        return preconditionResult;
                    }

                    enabledProperty.SetValue(component, enabled, null);
                    EditorUtility.SetDirty(component);
                    var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    if (saved == null)
                    {
                        return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                    }
                }

                var componentJson = ComponentJson(component, componentIndex);
                return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                    + ",\"updated\":" + JsonUtil.Bool(!dryRun && changed)
                    + ",\"would_update\":" + JsonUtil.Bool(dryRun && changed)
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"previous_enabled\":" + JsonUtil.Bool(previousEnabled)
                    + ",\"requested_enabled\":" + JsonUtil.Bool(enabled)
                    + ",\"component_selector\":" + ComponentSelectorJson(component, componentIndex)
                    + ",\"component\":" + componentJson
                    + ",\"changed_objects\":" + (!dryRun && changed ? "[" + JsonUtil.String(objectPath) + "]" : "[]")
                    + ",\"changed_components\":" + (!dryRun && changed ? "[" + componentJson + "]" : "[]")
                    + PrefabWriteContractJson(prefabPath, root, dryRun, changed, !dryRun && changed, operation, preconditionHash, new[] { "prefab.inspect_component", "prefab.inspect_object", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.variant.create",
            Title = "Create prefab variant",
            Description = "Create one new prefab variant asset under Assets without overwriting",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabVariantCreateSchemaJson,
            Tags = new string[] { "prefab", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "asset_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabVariantCreate(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var sourcePathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (sourcePathResult != null)
            {
                return sourcePathResult;
            }

            var variantPathResult = RequireNewPrefabAssetPath(context.ArgsJson, "variant_path", out var variantPath);
            if (variantPathResult != null)
            {
                return variantPathResult;
            }

            if (string.Equals(prefabPath, variantPath, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "variant_path must differ from prefab_path");
            }

            var sourcePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
            if (sourcePrefab == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var sourceGuid = AssetDatabase.AssetPathToGUID(prefabPath);
            var sourceAssetType = PrefabUtility.GetPrefabAssetType(sourcePrefab).ToString();
            var sourceBasePath = PrefabBaseAssetPath(sourcePrefab);
            var operation = "prefab.variant.create";
            var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, variantPath + "|" + sourceAssetType + "|" + sourceBasePath);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"created\":false,\"would_create\":true"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"variant_path\":" + JsonUtil.String(variantPath)
                    + ",\"source_guid\":" + JsonUtil.String(sourceGuid)
                    + ",\"source_asset_type\":" + JsonUtil.String(sourceAssetType)
                    + ",\"source_base_prefab_path\":" + JsonUtil.String(sourceBasePath)
                    + ",\"changed_objects\":[]"
                    + ",\"changed_components\":[]"
                    + ",\"checkpoint_recommended\":true"
                    + ",\"notes\":[\"dry_run:true preview only\",\"variant_path must not already exist\"]"
                    + PrefabWriteContractJson(prefabPath, sourcePrefab, true, true, false, operation, preconditionHash, new[] { "prefab.variant.create", "asset.info", "checkpoint.create" })
                    + "}");
            }

            var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
            if (preconditionResult != null)
            {
                return preconditionResult;
            }

            var previewScene = default(Scene);
            var previewSceneCreated = false;
            try
            {
                previewScene = EditorSceneManager.NewPreviewScene();
                previewSceneCreated = true;
                var instance = PrefabUtility.InstantiatePrefab(sourcePrefab, previewScene) as GameObject;
                if (instance == null)
                {
                    return CommandExecutionResult.Error("unity_exception", "Prefab variant instantiate failed");
                }

                var saved = PrefabUtility.SaveAsPrefabAsset(instance, variantPath);
                if (saved == null)
                {
                    return CommandExecutionResult.Error("unity_exception", "Prefab variant create failed");
                }

                return CommandExecutionResult.Ok("{\"dry_run\":false,\"created\":true,\"would_create\":false"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"variant_path\":" + JsonUtil.String(variantPath)
                    + ",\"source_guid\":" + JsonUtil.String(sourceGuid)
                    + ",\"variant_guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(variantPath))
                    + ",\"source_asset_type\":" + JsonUtil.String(sourceAssetType)
                    + ",\"variant_asset_type\":" + JsonUtil.String(PrefabUtility.GetPrefabAssetType(saved).ToString())
                    + ",\"source_base_prefab_path\":" + JsonUtil.String(sourceBasePath)
                    + ",\"variant_base_prefab_path\":" + JsonUtil.String(PrefabBaseAssetPath(saved))
                    + ",\"changed_objects\":[]"
                    + ",\"changed_components\":[]"
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, saved, false, true, true, operation, preconditionHash, new[] { "prefab.hierarchy", "prefab.inspect_object", "asset.info" })
                    + "}");
            }
            finally
            {
                if (previewSceneCreated)
                {
                    EditorSceneManager.ClosePreviewScene(previewScene);
                }
            }
        }

        [McpCommand(
            "prefab.component.property.set",
            Title = "Set prefab component property",
            Description = "Set one allowlisted built-in Component property on a prefab asset GameObject",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = PrefabComponentPropertySetSchemaJson,
            Tags = new string[] { "prefab", "component", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabComponentPropertySet(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var componentResult = RequirePrefabPropertyComponent(context.ArgsJson, root, out _, out var objectPath, out var component, out var componentIndex);
                if (componentResult != null)
                {
                    return componentResult;
                }

                var propertyName = JsonUtil.ExtractString(context.ArgsJson, "property_name");
                if (string.IsNullOrEmpty(propertyName))
                {
                    return CommandExecutionResult.Error("schema_validation_failed", "Argument property_name is required");
                }

                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                var setResult = PrepareComponentPropertySet(context.ArgsJson, component, propertyName, out var currentJson, out var requestedJson, out var changed, out var warnings, out var apply);
                if (setResult != null)
                {
                    return setResult;
                }

                var operation = "prefab.component.property.set";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, objectPath + "|" + ComponentSelectorJson(component, componentIndex) + "|" + propertyName + "|" + currentJson + "|" + requestedJson);
                if (!dryRun && changed)
                {
                    var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                    if (preconditionResult != null)
                    {
                        return preconditionResult;
                    }

                    apply();
                    EditorUtility.SetDirty(component);
                    var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    if (saved == null)
                    {
                        return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                    }
                }

                var componentJson = ComponentJson(component, componentIndex);
                return CommandExecutionResult.Ok("{\"status\":\"ok\",\"dry_run\":" + JsonUtil.Bool(dryRun)
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"component_selector\":" + ComponentSelectorJson(component, componentIndex)
                    + ",\"component\":" + componentJson
                    + ",\"property_name\":" + JsonUtil.String(propertyName)
                    + ",\"current_value\":" + currentJson
                    + ",\"requested_value\":" + requestedJson
                    + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed)
                    + ",\"changed_would_apply\":" + JsonUtil.Bool(dryRun && changed)
                    + ",\"changed_objects\":" + (!dryRun && changed ? "[" + JsonUtil.String(objectPath) + "]" : "[]")
                    + ",\"changed_components\":" + (!dryRun && changed ? "[" + componentJson + "]" : "[]")
                    + ",\"notes\":[\"Allowlisted built-in Component property only.\",\"Prefab asset is saved only when a real change applies.\"]"
                    + PrefabWriteContractJson(prefabPath, root, dryRun, changed, !dryRun && changed, operation, preconditionHash, new[] { "prefab.inspect_component", "prefab.hierarchy", "asset.info" }, warnings)
                    + "}");
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.component.add",
            Title = "Add prefab component",
            Description = "Add one non-MonoBehaviour Component to a prefab asset GameObject",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabComponentAddSchemaJson,
            Tags = new string[] { "prefab", "component", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabComponentAdd(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            var typeResult = ResolveComponentType(context.ArgsJson, out var componentType);
            if (typeResult != null)
            {
                return typeResult;
            }

            if (componentType == typeof(Transform))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Transform cannot be added as a regular component");
            }

            if (typeof(MonoBehaviour).IsAssignableFrom(componentType))
            {
                return CommandExecutionResult.Error("invalid_field_value", "MonoBehaviour components are not supported by safe-write component commands");
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var objectResult = RequirePrefabObject(context.ArgsJson, root, out var target, out var objectPath);
                if (objectResult != null)
                {
                    return objectResult;
                }

                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                var operation = "prefab.component.add";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, objectPath + "|" + (componentType.FullName ?? componentType.Name) + "|" + componentType.Assembly.GetName().Name);
                if (dryRun)
                {
                    return CommandExecutionResult.Ok("{\"dry_run\":true,\"added\":false,\"would_add\":true"
                        + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                        + ",\"object_path\":" + JsonUtil.String(objectPath)
                        + ",\"component_type\":" + JsonUtil.String(componentType.Name)
                        + ",\"full_type_name\":" + JsonUtil.String(componentType.FullName ?? componentType.Name)
                        + ",\"assembly_name\":" + JsonUtil.String(componentType.Assembly.GetName().Name)
                        + ",\"changed_objects\":[]"
                        + ",\"changed_components\":[]"
                        + ",\"checkpoint_recommended\":true"
                        + ",\"notes\":[\"dry_run:true preview only\",\"MonoBehaviour components are rejected\"]"
                        + PrefabWriteContractJson(prefabPath, root, true, true, false, operation, preconditionHash, new[] { "prefab.component.add", "prefab.inspect_object", "checkpoint.create" })
                        + "}");
                }

                var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }

                var component = target.AddComponent(componentType);
                if (component == null)
                {
                    return CommandExecutionResult.Error("unity_exception", "Component add failed");
                }

                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(component);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null)
                {
                    return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }

                var componentJson = ComponentJson(component, ComponentIndex(target, component));
                return CommandExecutionResult.Ok("{\"dry_run\":false,\"added\":true,\"would_add\":false"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"component_selector\":" + ComponentSelectorJson(component, ComponentIndex(target, component))
                    + ",\"component\":" + componentJson
                    + ",\"changed_objects\":[" + JsonUtil.String(objectPath) + "]"
                    + ",\"changed_components\":[" + componentJson + "]"
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, root, false, true, true, operation, preconditionHash, new[] { "prefab.inspect_component", "prefab.hierarchy", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.component.remove",
            Title = "Remove prefab component",
            Description = "Remove one non-MonoBehaviour Component from a prefab asset GameObject",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabComponentRemoveSchemaJson,
            Tags = new string[] { "prefab", "component", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabComponentRemove(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var componentResult = RequirePrefabComponent(context.ArgsJson, root, out var target, out var objectPath, out var component, out var componentIndex);
                if (componentResult != null)
                {
                    return componentResult;
                }

                var validateResult = ValidateSelectedComponent(component, false);
                if (validateResult != null)
                {
                    return validateResult;
                }

                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                var componentJson = ComponentJson(component, componentIndex);
                var componentSelectorJson = ComponentSelectorJson(component, componentIndex);
                var operation = "prefab.component.remove";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, objectPath + "|" + componentSelectorJson);
                if (dryRun)
                {
                    return CommandExecutionResult.Ok("{\"dry_run\":true,\"removed\":false,\"would_remove\":true"
                        + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                        + ",\"object_path\":" + JsonUtil.String(objectPath)
                        + ",\"component_selector\":" + componentSelectorJson
                        + ",\"component\":" + componentJson
                        + ",\"changed_objects\":[]"
                        + ",\"changed_components\":[]"
                        + ",\"checkpoint_recommended\":true"
                        + ",\"notes\":[\"dry_run:true preview only\",\"Transform and MonoBehaviour components are rejected\"]"
                        + PrefabWriteContractJson(prefabPath, root, true, true, false, operation, preconditionHash, new[] { "prefab.write_risk_report", "prefab.component.remove", "checkpoint.create" })
                        + "}");
                }

                var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }

                UnityEngine.Object.DestroyImmediate(component);
                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(root);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null)
                {
                    return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }

                return CommandExecutionResult.Ok("{\"dry_run\":false,\"removed\":true,\"would_remove\":false"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"component_selector\":" + componentSelectorJson
                    + ",\"component\":" + componentJson
                    + ",\"changed_objects\":[" + JsonUtil.String(objectPath) + "]"
                    + ",\"changed_components\":[" + componentJson + "]"
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, root, false, true, true, operation, preconditionHash, new[] { "prefab.inspect_object", "prefab.hierarchy", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.object.create",
            Title = "Create prefab object",
            Description = "Create one child GameObject inside a prefab asset without overwriting siblings",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabObjectCreateSchemaJson,
            Tags = new string[] { "prefab", "gameobject", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabObjectCreate(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            var nameResult = RequireObjectName(context.ArgsJson, "name", out var name);
            if (nameResult != null)
            {
                return nameResult;
            }

            var active = JsonUtil.ExtractBool(context.ArgsJson, "active", true);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var parentResult = ResolvePrefabParent(context.ArgsJson, root, out var parent, out var parentPath);
                if (parentResult != null)
                {
                    return parentResult;
                }

                if (PrefabSiblingNameExists(parent, name, null))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "A sibling with this name already exists");
                }

                var objectPath = ChildObjectPath(parent, name);
                var operation = "prefab.object.create";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, parentPath + "|" + name + "|active=" + JsonUtil.Bool(active));
                if (dryRun)
                {
                    return CommandExecutionResult.Ok("{\"dry_run\":true,\"created\":false,\"would_create\":true"
                        + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                        + ",\"parent_path\":" + JsonUtil.String(parentPath)
                        + ",\"object_path\":" + JsonUtil.String(objectPath)
                        + ",\"name\":" + JsonUtil.String(name)
                        + ",\"active\":" + JsonUtil.Bool(active)
                        + ",\"changed_objects\":[]"
                        + ",\"changed_components\":[]"
                        + ",\"checkpoint_recommended\":true"
                        + ",\"notes\":[\"dry_run:true preview only\",\"Sibling names must be unique under the selected parent\"]"
                        + PrefabWriteContractJson(prefabPath, root, true, true, false, operation, preconditionHash, new[] { "prefab.object.create", "prefab.hierarchy", "checkpoint.create" })
                        + "}");
                }

                var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }

                var gameObject = new GameObject(name);
                SceneManager.MoveGameObjectToScene(gameObject, root.scene);
                gameObject.SetActive(active);
                gameObject.transform.SetParent(parent, false);
                EditorUtility.SetDirty(gameObject);
                EditorUtility.SetDirty(root);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null)
                {
                    return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }

                return CommandExecutionResult.Ok("{\"dry_run\":false,\"created\":true,\"would_create\":false"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"parent_path\":" + JsonUtil.String(parentPath)
                    + ",\"object_path\":" + JsonUtil.String(ObjectPath(gameObject))
                    + ",\"object\":" + GameObjectJson(gameObject)
                    + ",\"changed_objects\":[" + JsonUtil.String(ObjectPath(gameObject)) + "]"
                    + ",\"changed_components\":[]"
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, root, false, true, true, operation, preconditionHash, new[] { "prefab.inspect_object", "prefab.hierarchy", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.ugui.object.create",
            Title = "Create prefab uGUI object",
            Description = "Create one RectTransform child inside a prefab asset without opening scenes",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabUguiObjectCreateSchemaJson,
            Tags = new string[] { "prefab", "ugui", "recttransform", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = PrefabUguiAuthoringOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabUguiObjectCreate(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null) return status;

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            var nameResult = RequireObjectName(context.ArgsJson, "name", out var name);
            if (nameResult != null) return nameResult;
            var requestResult = ExtractPrefabUguiRectRequest(context.ArgsJson, out var rectRequest);
            if (requestResult != null) return requestResult;

            var active = JsonUtil.ExtractBool(context.ArgsJson, "active", true);
            var siblingIndex = JsonUtil.ExtractInt(context.ArgsJson, "sibling_index", -1);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var parentResult = ResolvePrefabParent(context.ArgsJson, root, out var parent, out var parentPath);
                if (parentResult != null) return parentResult;
                if (!PrefabParentAcceptsUgui(parent)) return CommandExecutionResult.Error("invalid_field_value", "parent_path must reference a uGUI parent with RectTransform or Canvas");
                if (siblingIndex > parent.childCount) return CommandExecutionResult.Error("invalid_field_value", "sibling_index must be -1 or no greater than parent child count");
                if (PrefabSiblingNameExists(parent, name, null)) return CommandExecutionResult.Error("invalid_field_value", "A sibling with this name already exists");

                var objectPath = ChildObjectPath(parent, name);
                var operation = "prefab.ugui.object.create";
                var operationKey = parentPath + "|" + name + "|active=" + JsonUtil.Bool(active) + "|sibling_index=" + siblingIndex.ToString(CultureInfo.InvariantCulture) + "|" + PrefabUguiRectRequestJson(rectRequest);
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, operationKey);
                if (dryRun)
                {
                    return CommandExecutionResult.Ok("{\"dry_run\":true,\"created\":false,\"would_create\":true"
                        + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                        + ",\"parent_path\":" + JsonUtil.String(parentPath)
                        + ",\"object_path\":" + JsonUtil.String(objectPath)
                        + ",\"name\":" + JsonUtil.String(name)
                        + ",\"active\":" + JsonUtil.Bool(active)
                        + ",\"rect_transform\":" + PrefabUguiRectRequestJson(rectRequest)
                        + ",\"changed_objects\":[]"
                        + ",\"changed_components\":[]"
                        + ",\"checkpoint_recommended\":true"
                        + ",\"notes\":[\"dry_run:true preview only\",\"Creates a RectTransform and saves only the selected prefab asset on confirmed apply\"]"
                        + PrefabWriteContractJson(prefabPath, root, true, true, false, operation, preconditionHash, new[] { "prefab.ugui.object.create", "prefab.hierarchy", "checkpoint.create" })
                        + "}");
                }

                var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                if (preconditionResult != null) return preconditionResult;
                var gameObject = new GameObject(name, typeof(RectTransform));
                SceneManager.MoveGameObjectToScene(gameObject, root.scene);
                gameObject.SetActive(active);
                gameObject.transform.SetParent(parent, false);
                if (siblingIndex >= 0) gameObject.transform.SetSiblingIndex(siblingIndex);
                ApplyPrefabUguiRectRequest(gameObject.GetComponent<RectTransform>(), rectRequest);
                EditorUtility.SetDirty(gameObject);
                EditorUtility.SetDirty(root);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null) return CommandExecutionResult.Error("unity_exception", "Prefab save failed");

                return CommandExecutionResult.Ok("{\"dry_run\":false,\"created\":true,\"would_create\":false"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"parent_path\":" + JsonUtil.String(parentPath)
                    + ",\"object_path\":" + JsonUtil.String(ObjectPath(gameObject))
                    + ",\"object\":" + GameObjectJson(gameObject)
                    + ",\"rect_transform\":" + RectTransformJson(gameObject.GetComponent<RectTransform>())
                    + ",\"changed_objects\":[" + JsonUtil.String(ObjectPath(gameObject)) + "]"
                    + ",\"changed_components\":[]"
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, root, false, true, true, operation, preconditionHash, new[] { "prefab.inspect_object", "prefab.hierarchy", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [McpCommand(
            "prefab.ugui.component.add",
            Title = "Add prefab uGUI component",
            Description = "Add one allowlisted uGUI Component to a prefab asset RectTransform object",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabUguiComponentAddSchemaJson,
            Tags = new string[] { "prefab", "ugui", "component", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = PrefabUguiAuthoringOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabUguiComponentAdd(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null) return status;
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            var typeResult = ResolvePrefabUguiAuthoringComponentType(context.ArgsJson, out var componentType);
            if (typeResult != null) return typeResult;

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var objectResult = RequirePrefabObject(context.ArgsJson, root, out var target, out var objectPath);
                if (objectResult != null) return objectResult;
                if (target.GetComponent<RectTransform>() == null && target.GetComponent<Canvas>() == null) return CommandExecutionResult.Error("invalid_field_value", "object_path must reference a uGUI object with RectTransform or Canvas");
                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                var operation = "prefab.ugui.component.add";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, objectPath + "|" + (componentType.FullName ?? componentType.Name) + "|" + componentType.Assembly.GetName().Name);
                if (dryRun)
                {
                    return CommandExecutionResult.Ok("{\"dry_run\":true,\"added\":false,\"would_add\":true"
                        + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                        + ",\"object_path\":" + JsonUtil.String(objectPath)
                        + ",\"component_type\":" + JsonUtil.String(componentType.Name)
                        + ",\"full_type_name\":" + JsonUtil.String(componentType.FullName ?? componentType.Name)
                        + ",\"assembly_name\":" + JsonUtil.String(componentType.Assembly.GetName().Name)
                        + ",\"changed_objects\":[]"
                        + ",\"changed_components\":[]"
                        + ",\"checkpoint_recommended\":true"
                        + ",\"notes\":[\"dry_run:true preview only\",\"Only allowlisted uGUI components can be added\"]"
                        + PrefabWriteContractJson(prefabPath, root, true, true, false, operation, preconditionHash, new[] { "prefab.ugui.component.add", "prefab.inspect_object", "checkpoint.create" })
                        + "}");
                }

                var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                if (preconditionResult != null) return preconditionResult;
                var component = target.AddComponent(componentType);
                if (component == null) return CommandExecutionResult.Error("unity_exception", "Component add failed");
                EditorUtility.SetDirty(target);
                EditorUtility.SetDirty(component);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null) return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                var componentJson = ComponentJson(component, ComponentIndex(target, component));
                return CommandExecutionResult.Ok("{\"dry_run\":false,\"added\":true,\"would_add\":false"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"component_selector\":" + ComponentSelectorJson(component, ComponentIndex(target, component))
                    + ",\"component\":" + componentJson
                    + ",\"changed_objects\":[" + JsonUtil.String(objectPath) + "]"
                    + ",\"changed_components\":[" + componentJson + "]"
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, root, false, true, true, operation, preconditionHash, new[] { "prefab.inspect_component", "prefab.hierarchy", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [McpCommand(
            "prefab.ugui.text.set",
            Title = "Set prefab uGUI text",
            Description = "Set bounded text, font-size, color, and raycast fields on one prefab asset uGUI/TMP text component",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = PrefabUguiTextSetSchemaJson,
            Tags = new string[] { "prefab", "ugui", "text", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = PrefabUguiAuthoringOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabUguiTextSet(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null) return status;
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null) return pathResult;
            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null) return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            var requestResult = ExtractPrefabUguiTextRequest(context.ArgsJson, out var request);
            if (requestResult != null) return requestResult;

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var objectResult = RequirePrefabObject(context.ArgsJson, root, out var target, out var objectPath);
                if (objectResult != null) return objectResult;
                if (target.GetComponent<RectTransform>() == null && target.GetComponent<Canvas>() == null) return CommandExecutionResult.Error("invalid_field_value", "object_path must reference a uGUI object with RectTransform or Canvas");
                var componentResult = RequirePrefabUguiTextComponent(context.ArgsJson, target, out var component, out var componentIndex);
                if (componentResult != null) return componentResult;
                var current = CapturePrefabUguiTextState(component);
                var requested = RequestedPrefabUguiTextState(current, request);
                var changedFields = PrefabUguiTextChangedFields(current, requested, request);
                var changed = changedFields.Count > 0;
                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                var operation = "prefab.ugui.text.set";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, objectPath + "|" + ComponentSelectorJson(component, componentIndex) + "|" + PrefabUguiTextStateJson(current) + "|" + PrefabUguiTextStateJson(requested));
                if (!dryRun && changed)
                {
                    var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                    if (preconditionResult != null) return preconditionResult;
                    ApplyPrefabUguiTextState(component, changedFields, requested);
                    EditorUtility.SetDirty(component);
                    var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                    if (saved == null) return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }

                return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                    + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed)
                    + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed)
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"component_selector\":" + ComponentSelectorJson(component, componentIndex)
                    + ",\"component\":" + ComponentJson(component, componentIndex)
                    + ",\"changed_fields\":" + StringArrayJson(changedFields)
                    + ",\"current\":" + PrefabUguiTextStateJson(current)
                    + ",\"requested\":" + PrefabUguiTextStateJson(requested)
                    + ",\"changed_objects\":" + (!dryRun && changed ? "[" + JsonUtil.String(objectPath) + "]" : "[]")
                    + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]")
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, root, dryRun, changed, !dryRun && changed, operation, preconditionHash, new[] { "prefab.inspect_component", "prefab.hierarchy", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null) PrefabUtility.UnloadPrefabContents(root);
            }
        }

        [McpCommand(
            "prefab.object.delete",
            Title = "Delete prefab object",
            Description = "Delete one non-root GameObject from a prefab asset",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabObjectDeleteSchemaJson,
            Tags = new string[] { "prefab", "gameobject", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = WriteRiskReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabObjectDelete(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            GameObject root = null;
            try
            {
                root = PrefabUtility.LoadPrefabContents(prefabPath);
                var objectResult = RequirePrefabObject(context.ArgsJson, root, out var target, out var objectPath);
                if (objectResult != null)
                {
                    return objectResult;
                }

                if (target == root)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Prefab root cannot be deleted by this command");
                }

                var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
                var objectJson = GameObjectJson(target);
                var childrenJson = ChildrenRiskJson(target, 32);
                var componentsJson = ComponentsRiskJson(target, 32);
                var operation = "prefab.object.delete";
                var preconditionHash = PrefabWritePreconditionHash(prefabPath, operation, objectPath);
                if (dryRun)
                {
                    return CommandExecutionResult.Ok("{\"dry_run\":true,\"deleted\":false,\"would_delete\":true"
                        + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                        + ",\"object_path\":" + JsonUtil.String(objectPath)
                        + ",\"object\":" + objectJson
                        + ",\"children\":" + childrenJson
                        + ",\"components\":" + componentsJson
                        + ",\"changed_objects\":[]"
                        + ",\"changed_components\":[]"
                        + ",\"checkpoint_recommended\":true"
                        + ",\"notes\":[\"dry_run:true preview only\",\"Prefab root deletion is refused\"]"
                        + PrefabWriteContractJson(prefabPath, root, true, true, false, operation, preconditionHash, new[] { "prefab.write_risk_report", "prefab.object.delete", "checkpoint.create" })
                        + "}");
                }

                var preconditionResult = RequirePrefabWritePrecondition(context.ArgsJson, prefabPath, operation, preconditionHash);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }

                UnityEngine.Object.DestroyImmediate(target);
                EditorUtility.SetDirty(root);
                var saved = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (saved == null)
                {
                    return CommandExecutionResult.Error("unity_exception", "Prefab save failed");
                }

                return CommandExecutionResult.Ok("{\"dry_run\":false,\"deleted\":true,\"would_delete\":false"
                    + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"object\":" + objectJson
                    + ",\"children\":" + childrenJson
                    + ",\"components\":" + componentsJson
                    + ",\"changed_objects\":[" + JsonUtil.String(objectPath) + "]"
                    + ",\"changed_components\":[]"
                    + ",\"checkpoint_recommended\":true"
                    + PrefabWriteContractJson(prefabPath, root, false, true, true, operation, preconditionHash, new[] { "prefab.hierarchy", "asset.info" })
                    + "}");
            }
            finally
            {
                if (root != null)
                {
                    PrefabUtility.UnloadPrefabContents(root);
                }
            }
        }

        [McpCommand(
            "prefab.apply_overrides",
            Title = "Apply prefab overrides",
            Description = "Apply bounded non-structural prefab instance overrides to a prefab asset with a precondition hash",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = PrefabApplyOverridesSchemaJson,
            Tags = new string[] { "prefab", "scene", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = PrefabApplyOverridesOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabApplyOverrides(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "prefab_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath) == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "prefab_path was not found");
            }

            if (!PrefabUtility.IsPartOfPrefabInstance(target))
            {
                return CommandExecutionResult.Error("invalid_field_value", "object_path must target a prefab instance in the loaded scene");
            }

            var instanceRoot = PrefabUtility.GetOutermostPrefabInstanceRoot(target);
            if (instanceRoot == null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Prefab instance root was not found");
            }

            var instanceRootPath = ObjectPath(instanceRoot);
            if (target != instanceRoot)
            {
                return CommandExecutionResult.Error("invalid_field_value", "object_path must target the outermost prefab instance root: " + instanceRootPath);
            }

            var sourcePrefabPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instanceRoot);
            if (!string.Equals(sourcePrefabPath, prefabPath, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "prefab_path must match the source prefab of the selected instance");
            }

            var summary = BuildPrefabApplyOverridesSummary(scenePath, objectPath, prefabPath, instanceRoot, sourcePrefabPath);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (dryRun)
            {
                return CommandExecutionResult.Ok(PrefabApplyOverridesResultJson(true, false, summary.CanApply, scenePath, objectPath, prefabPath, instanceRootPath, sourcePrefabPath, scene.isDirty, summary));
            }

            var requestedHash = JsonUtil.ExtractString(context.ArgsJson, "precondition_hash");
            if (string.IsNullOrEmpty(requestedHash))
            {
                return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when dry_run is false", PrefabPreconditionDetailsJson(prefabPath, "prefab.apply_overrides", summary.PreconditionHash));
            }

            if (!string.Equals(requestedHash, summary.PreconditionHash, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match current prefab instance overrides", PrefabPreconditionDetailsJson(prefabPath, "prefab.apply_overrides", summary.PreconditionHash));
            }

            if (!summary.CanApply)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Current overrides are not supported by prefab.apply_overrides; run dry_run:true for warnings");
            }

            PrefabUtility.ApplyPrefabInstance(instanceRoot, InteractionMode.AutomatedAction);
            return CommandExecutionResult.Ok(PrefabApplyOverridesResultJson(false, summary.PropertyModificationCount > 0, false, scenePath, objectPath, prefabPath, instanceRootPath, sourcePrefabPath, scene.isDirty, summary));
        }

        [McpCommand(
            "component.property_schema",
            Title = "Component property schema",
            Description = "Read allowlisted Component property schemas for safe property writes",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ComponentPropertySchemaSchemaJson,
            Tags = new string[] { "component", "safe-write", "preflight" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "bounded_list",
            OutputLimitJson = PropertySchemaOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ComponentPropertySchema(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            return CommandExecutionResult.Ok(ComponentPropertySchemaJson(context));
        }

        [McpCommand(
            "gameobject.delete",
            Title = "Delete GameObject",
            Description = "Delete one GameObject from a loaded scene",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = GameObjectPathSchemaJson,
            Tags = new string[] { "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectDelete(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var objectJson = GameObjectJson(target);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"deleted\":false,\"would_delete\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"object\":" + objectJson
                    + ",\"changed_objects\":[]"
                    + SceneWriteContractJson(scene, true, false, new[] { "gameobject.write_risk_report", "gameobject.delete", "scene.save" }, target)
                    + "}");
            }

            Undo.DestroyObjectImmediate(target);
            EditorSceneManager.MarkSceneDirty(scene);
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"deleted\":true,\"would_delete\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"object\":" + objectJson
                + ",\"changed_objects\":[" + JsonUtil.String(objectPath) + "]"
                + SceneWriteContractJson(scene, false, true, new[] { "scene.hierarchy", "scene.save", "checkpoint.list" }, target)
                + "}");
        }

        [McpCommand(
            "gameobject.duplicate",
            Title = "Duplicate GameObject",
            Description = "Duplicate one GameObject in a loaded scene",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = GameObjectDuplicateSchemaJson,
            Tags = new string[] { "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectDuplicate(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var requestedName = JsonUtil.ExtractString(context.ArgsJson, "name");
            if (HasJsonProperty(context.ArgsJson, "name"))
            {
                var nameResult = ValidateObjectName(requestedName, "name");
                if (nameResult != null)
                {
                    return nameResult;
                }
            }

            GameObject parent;
            string parentPath;
            if (HasJsonProperty(context.ArgsJson, "parent_path"))
            {
                var parentResult = ResolveOptionalParent(context.ArgsJson, scene, out parent, out parentPath);
                if (parentResult != null)
                {
                    return parentResult;
                }
            }
            else
            {
                parent = target.transform.parent == null ? null : target.transform.parent.gameObject;
                parentPath = parent == null ? string.Empty : ObjectPath(parent);
            }

            var name = string.IsNullOrEmpty(requestedName) ? UniqueName(scene, parent == null ? null : parent.transform, target.name + " Copy") : requestedName;
            if (SiblingNameExists(scene, parent == null ? null : parent.transform, name, null))
            {
                return CommandExecutionResult.Error("invalid_field_value", "A sibling with this name already exists");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var plannedObjectPath = ChildSceneObjectPath(parent, name);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"duplicated\":false,\"would_duplicate\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"source_object_path\":" + JsonUtil.String(objectPath)
                    + ",\"parent_path\":" + JsonUtil.String(parentPath)
                    + ",\"object_path\":" + JsonUtil.String(plannedObjectPath)
                    + ",\"name\":" + JsonUtil.String(name)
                    + ",\"changed_objects\":[]"
                    + SceneWriteContractJson(scene, true, false, new[] { "gameobject.duplicate", "scene.hierarchy", "scene.save" }, target)
                    + "}");
            }

            var duplicate = UnityEngine.Object.Instantiate(target);
            duplicate.name = name;
            Undo.RegisterCreatedObjectUndo(duplicate, "Duplicate GameObject");
            SceneManager.MoveGameObjectToScene(duplicate, scene);
            if (parent != null)
            {
                Undo.SetTransformParent(duplicate.transform, parent.transform, "Parent GameObject");
            }
            else if (duplicate.transform.parent != null)
            {
                Undo.SetTransformParent(duplicate.transform, null, "Parent GameObject");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"duplicated\":true,\"would_duplicate\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"source_object_path\":" + JsonUtil.String(objectPath)
                + ",\"parent_path\":" + JsonUtil.String(parentPath)
                + ",\"object_path\":" + JsonUtil.String(ObjectPath(duplicate))
                + ",\"object\":" + GameObjectJson(duplicate)
                + ",\"changed_objects\":[" + JsonUtil.String(ObjectPath(duplicate)) + "]"
                + SceneWriteContractJson(scene, false, true, new[] { "scene.inspect_object", "scene.hierarchy", "scene.save" }, target)
                + "}");
        }

        [McpCommand(
            "gameobject.transform.set",
            Title = "Set GameObject transform",
            Description = "Set bounded transform fields on one loaded-scene GameObject",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = TransformSetSchemaJson,
            Tags = new string[] { "gameobject", "transform", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectTransformSet(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var readResult = ReadVector(context.ArgsJson, "position", out var hasPosition, out var position);
            if (readResult != null)
            {
                return readResult;
            }

            readResult = ReadVector(context.ArgsJson, "local_position", out var hasLocalPosition, out var localPosition);
            if (readResult != null)
            {
                return readResult;
            }

            readResult = ReadVector(context.ArgsJson, "rotation_euler", out var hasRotation, out var rotation);
            if (readResult != null)
            {
                return readResult;
            }

            readResult = ReadVector(context.ArgsJson, "local_rotation_euler", out var hasLocalRotation, out var localRotation);
            if (readResult != null)
            {
                return readResult;
            }

            readResult = ReadVector(context.ArgsJson, "local_scale", out var hasLocalScale, out var localScale);
            if (readResult != null)
            {
                return readResult;
            }

            if (!hasPosition && !hasLocalPosition && !hasRotation && !hasLocalRotation && !hasLocalScale)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide at least one transform field");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"updated\":false,\"would_update\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"object\":" + GameObjectJson(target)
                    + ",\"changed_objects\":[]"
                    + SceneWriteContractJson(scene, true, false, new[] { "gameobject.transform.set", "scene.inspect_object", "scene.save" }, target)
                    + "}");
            }

            Undo.RecordObject(target.transform, "Set GameObject Transform");
            if (hasPosition)
            {
                target.transform.position = position;
            }

            if (hasLocalPosition)
            {
                target.transform.localPosition = localPosition;
            }

            if (hasRotation)
            {
                target.transform.eulerAngles = rotation;
            }

            if (hasLocalRotation)
            {
                target.transform.localEulerAngles = localRotation;
            }

            if (hasLocalScale)
            {
                target.transform.localScale = localScale;
            }

            EditorSceneManager.MarkSceneDirty(scene);

            return CommandExecutionResult.Ok("{\"dry_run\":false,\"updated\":true,\"would_update\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"object\":" + GameObjectJson(target)
                + ",\"changed_objects\":[" + JsonUtil.String(objectPath) + "]"
                + SceneWriteContractJson(scene, false, true, new[] { "scene.inspect_object", "scene.hierarchy", "scene.save" }, target)
                + "}");
        }

        [McpCommand(
            "gameobject.parent.set",
            Title = "Set GameObject parent",
            Description = "Set or clear the parent of one loaded-scene GameObject",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = ParentSetSchemaJson,
            Tags = new string[] { "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectParentSet(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var parentResult = ResolveOptionalParent(context.ArgsJson, scene, out var parent, out var parentPath);
            if (parentResult != null)
            {
                return parentResult;
            }

            if (parent == target)
            {
                return CommandExecutionResult.Error("invalid_field_value", "parent_path cannot target the same object");
            }

            if (parent != null && IsDescendantOf(parent.transform, target.transform))
            {
                return CommandExecutionResult.Error("invalid_field_value", "parent_path cannot target a descendant");
            }

            if (SiblingNameExists(scene, parent == null ? null : parent.transform, target.name, target))
            {
                return CommandExecutionResult.Error("invalid_field_value", "A sibling with this name already exists");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var newObjectPath = ChildSceneObjectPath(parent, target.name);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"updated\":false,\"would_update\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"previous_object_path\":" + JsonUtil.String(objectPath)
                    + ",\"object_path\":" + JsonUtil.String(newObjectPath)
                    + ",\"parent_path\":" + JsonUtil.String(parentPath)
                    + ",\"object\":" + GameObjectJson(target)
                    + ",\"changed_objects\":[]"
                    + SceneWriteContractJson(scene, true, false, new[] { "gameobject.parent.set", "scene.inspect_object", "scene.save" }, target)
                    + "}");
            }

            Undo.SetTransformParent(target.transform, parent == null ? null : parent.transform, "Set GameObject Parent");
            EditorSceneManager.MarkSceneDirty(scene);
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"updated\":true,\"would_update\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"previous_object_path\":" + JsonUtil.String(objectPath)
                + ",\"object_path\":" + JsonUtil.String(ObjectPath(target))
                + ",\"parent_path\":" + JsonUtil.String(parentPath)
                + ",\"object\":" + GameObjectJson(target)
                + ",\"changed_objects\":[" + JsonUtil.String(ObjectPath(target)) + "]"
                + SceneWriteContractJson(scene, false, true, new[] { "scene.inspect_object", "scene.hierarchy", "scene.save" }, target)
                + "}");
        }

        [McpCommand(
            "component.add",
            Title = "Add component",
            Description = "Add one Component to a loaded-scene GameObject",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ComponentAddSchemaJson,
            Tags = new string[] { "component", "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ComponentAdd(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var typeResult = ResolveComponentType(context.ArgsJson, out var componentType);
            if (typeResult != null)
            {
                return typeResult;
            }

            if (componentType == typeof(Transform))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Transform cannot be added as a regular component");
            }

            if (typeof(MonoBehaviour).IsAssignableFrom(componentType))
            {
                return CommandExecutionResult.Error("invalid_field_value", "MonoBehaviour components are not supported by safe-write component commands");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"added\":false,\"would_add\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"component_type\":" + JsonUtil.String(componentType.Name)
                    + ",\"full_type_name\":" + JsonUtil.String(componentType.FullName ?? componentType.Name)
                    + ",\"assembly_name\":" + JsonUtil.String(componentType.Assembly.GetName().Name)
                    + ",\"changed_components\":[]"
                    + SceneWriteContractJson(scene, true, false, new[] { "component.add", "scene.inspect_component", "scene.save" }, target)
                    + "}");
            }

            var component = Undo.AddComponent(target, componentType);
            if (component == null)
            {
                return CommandExecutionResult.Error("unity_exception", "Component add failed");
            }

            EditorSceneManager.MarkSceneDirty(scene);
            var componentJson = ComponentJson(component, ComponentIndex(target, component));
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"added\":true,\"would_add\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"component\":" + componentJson
                + ",\"changed_components\":[" + componentJson + "]"
                + SceneWriteContractJson(scene, false, true, new[] { "scene.inspect_component", "scene.inspect_object", "scene.save" }, target)
                + "}");
        }

        [McpCommand(
            "component.remove",
            Title = "Remove component",
            Description = "Remove one Component from a loaded-scene GameObject",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ComponentSelectSchemaJson,
            Tags = new string[] { "component", "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ComponentRemove(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var componentResult = RequireSceneComponent(context.ArgsJson, false, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex);
            if (componentResult != null)
            {
                return componentResult;
            }

            var componentJson = ComponentJson(component, componentIndex);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"removed\":false,\"would_remove\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"component\":" + componentJson
                    + ",\"changed_components\":[]"
                    + SceneWriteContractJson(scene, true, false, new[] { "gameobject.write_risk_report", "component.remove", "scene.save" }, target)
                    + "}");
            }

            Undo.DestroyObjectImmediate(component);
            EditorSceneManager.MarkSceneDirty(scene);
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"removed\":true,\"would_remove\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"component\":" + componentJson
                + ",\"changed_components\":[" + componentJson + "]"
                + SceneWriteContractJson(scene, false, true, new[] { "scene.inspect_object", "scene.save", "checkpoint.list" }, target)
                + "}");
        }

        [McpCommand(
            "component.modify",
            Title = "Modify component",
            Description = "Modify one supported Component field on a loaded-scene GameObject",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = ComponentModifySchemaJson,
            Tags = new string[] { "component", "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ComponentModify(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var componentResult = RequireSceneComponent(context.ArgsJson, true, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex);
            if (componentResult != null)
            {
                return componentResult;
            }

            if (!HasJsonProperty(context.ArgsJson, "enabled"))
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "Provide at least one supported component field", "enabled", "relational_constraint_failed");
            }

            var enabledProperty = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (enabledProperty == null || enabledProperty.PropertyType != typeof(bool) || !enabledProperty.CanWrite)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "component does not expose a writable enabled field", "enabled", "unsupported_for_target");
            }

            var enabled = JsonUtil.ExtractBool(context.ArgsJson, "enabled", false);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var previousEnabled = (bool)enabledProperty.GetValue(component, null);
            var changed = previousEnabled != enabled;
            if (!dryRun && changed)
            {
                Undo.RecordObject(component, "Modify Component");
                enabledProperty.SetValue(component, enabled, null);
                EditorUtility.SetDirty(component);
                EditorSceneManager.MarkSceneDirty(scene);
            }

            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"updated\":" + JsonUtil.Bool(!dryRun && changed)
                + ",\"would_update\":" + JsonUtil.Bool(dryRun && changed)
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"previous_enabled\":" + JsonUtil.Bool(previousEnabled)
                + ",\"requested_enabled\":" + JsonUtil.Bool(enabled)
                + ",\"component\":" + ComponentJson(component, componentIndex)
                + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]")
                + SceneWriteContractJson(scene, dryRun, changed, new[] { "scene.inspect_component", "scene.inspect_object", "scene.save" }, target)
                + "}");
        }

        [McpCommand(
            "component.property.set",
            Title = "Set component property",
            Description = "Set one allowlisted built-in Component property on a loaded-scene GameObject",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = ComponentPropertySetSchemaJson,
            Tags = new string[] { "component", "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ComponentPropertySet(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var componentResult = RequireScenePropertyComponent(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex);
            if (componentResult != null)
            {
                return componentResult;
            }

            var propertyName = JsonUtil.ExtractString(context.ArgsJson, "property_name");
            if (string.IsNullOrEmpty(propertyName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument property_name is required");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var setResult = PrepareComponentPropertySet(context.ArgsJson, component, propertyName, out var currentJson, out var requestedJson, out var changed, out var warnings, out var apply);
            if (setResult != null)
            {
                return setResult;
            }

            var preconditionValueJson = JsonUtil.ExtractString(context.ArgsJson, "precondition_value_json");
            if (!dryRun && changed && string.IsNullOrEmpty(preconditionValueJson))
            {
                return CommandExecutionResult.Error("precondition_required", "precondition_value_json is required when dry_run is false and the component property would change", ComponentValuePreconditionDetailsJson(scenePath, objectPath, propertyName, component, componentIndex, currentJson));
            }

            if (!dryRun && changed && !string.Equals(preconditionValueJson, currentJson, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("precondition_failed", "precondition_value_json does not match the current component property value", "{\"property_name\":" + JsonUtil.String(propertyName) + ",\"current_value\":" + currentJson + ",\"precondition_value_json\":" + JsonUtil.String(preconditionValueJson) + "}");
            }

            if (!dryRun && changed)
            {
                Undo.RecordObject(component, "Set Component Property");
                apply();
                EditorUtility.SetDirty(component);
                EditorSceneManager.MarkSceneDirty(scene);
            }

            AddSceneWriteWarnings(warnings, scene, dryRun, changed, target);

            return CommandExecutionResult.Ok("{\"status\":\"ok\",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"component_selector\":" + ComponentSelectorJson(component, componentIndex)
                + ",\"property_name\":" + JsonUtil.String(propertyName)
                + ",\"current_value\":" + currentJson
                + ",\"requested_value\":" + requestedJson
                + ",\"precondition_value_json\":" + JsonUtil.String(currentJson)
                + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed)
                + ",\"changed_would_apply\":" + JsonUtil.Bool(dryRun && changed)
                + ",\"scene_dirty\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"scene_saved\":false"
                + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]")
                + ",\"warnings\":" + StringArrayJson(warnings)
                + ",\"notes\":[\"Allowlisted built-in UnityEngine component property only.\",\"Scenes are marked dirty but not saved.\"]"
                + ",\"next_valid_actions\":[\"scene.inspect_component\",\"scene.inspect_object\",\"scene.save\"]}");
        }

        [McpCommand(
            "material.property_schema",
            Title = "Material property schema",
            Description = "Read allowlisted Material property schemas for safe material property writes",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = MaterialPropertySchemaSchemaJson,
            Tags = new string[] { "material", "asset", "safe-write", "preflight" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "bounded_list",
            OutputLimitJson = MaterialPropertyOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MaterialPropertySchema(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var maxResultsResult = ReadOptionalInt(context.ArgsJson, "max_results", 1, 8, out var hasMaxResults, out var maxResults);
            if (maxResultsResult != null)
            {
                return maxResultsResult;
            }

            maxResults = hasMaxResults ? maxResults : 8;

            if (HasJsonProperty(context.ArgsJson, "material_path"))
            {
                var pathResult = RequireMaterialAssetPath(context.ArgsJson, "material_path", false, out var materialPath);
                if (pathResult != null)
                {
                    return pathResult;
                }

                var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
                if (material == null)
                {
                    return CommandExecutionResult.Error("asset_not_found", "material_path must reference a Material asset");
                }

                return CommandExecutionResult.Ok(MaterialPropertySchemaJson(JsonUtil.ExtractString(context.ArgsJson, "shader"), material.shader == null ? string.Empty : material.shader.name, materialPath, maxResults));
            }

            return CommandExecutionResult.Ok(MaterialPropertySchemaJson(JsonUtil.ExtractString(context.ArgsJson, "shader"), string.Empty, string.Empty, maxResults));
        }

        [McpCommand(
            "material.properties.inspect",
            Title = "Inspect material properties",
            Description = "Read bounded shader property metadata and current values for one Material asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = MaterialPropertiesInspectSchemaJson,
            Tags = new string[] { "material", "asset", "safe-write", "preflight" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "bounded_list",
            OutputLimitJson = MaterialPropertiesInspectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MaterialPropertiesInspect(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireMaterialAssetPath(context.ArgsJson, "material_path", false, out var materialPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var maxResult = ReadOptionalInt(context.ArgsJson, "max_properties", 1, 128, out var hasMaxProperties, out var maxProperties);
            if (maxResult != null)
            {
                return maxResult;
            }

            maxProperties = hasMaxProperties ? maxProperties : 64;
            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "material_path must reference a Material asset");
            }

            if (material.shader == null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "material shader is missing");
            }

            return CommandExecutionResult.Ok(MaterialPropertiesInspectJson(material, materialPath, maxProperties));
        }

        [McpCommand(
            "material.property.set",
            Title = "Set material property",
            Description = "Set one allowlisted Material float, color, vector, or texture property on an Assets .mat asset",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = MaterialPropertySetSchemaJson,
            Tags = new string[] { "material", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "asset_details",
            OutputLimitJson = MaterialPropertyOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MaterialPropertySet(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireMaterialAssetPath(context.ArgsJson, "material_path", true, out var materialPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "material_path must reference a Material asset");
            }

            if (material.shader == null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "material shader is missing");
            }

            var propertyName = JsonUtil.ExtractString(context.ArgsJson, "property_name");
            if (string.IsNullOrEmpty(propertyName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument property_name is required");
            }

            if (!material.HasProperty(propertyName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "material does not have allowlisted property_name");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var setResult = PrepareMaterialPropertySet(context.ArgsJson, material, propertyName, out var currentJson, out var requestedJson, out var changed, out var warnings, out var apply);
            if (setResult != null)
            {
                return setResult;
            }

            if (!dryRun && changed)
            {
                Undo.RecordObject(material, "Set Material Property");
                apply();
                EditorUtility.SetDirty(material);
            }

            return CommandExecutionResult.Ok("{\"status\":\"ok\",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"material_path\":" + JsonUtil.String(materialPath)
                + ",\"material\":" + MaterialJson(material, materialPath)
                + ",\"property_name\":" + JsonUtil.String(propertyName)
                + ",\"current_value\":" + currentJson
                + ",\"requested_value\":" + requestedJson
                + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed)
                + ",\"changed_would_apply\":" + JsonUtil.Bool(dryRun && changed)
                + ",\"warnings\":" + StringArrayJson(warnings)
                + ",\"notes\":[\"Allowlisted Material float/color property only.\",\"Assets are marked dirty but not saved.\"]"
                + ",\"next_actions\":[\"asset.info\",\"material.property_schema\"]}");
        }

        [McpCommand(
            "material.create",
            Title = "Create material",
            Description = "Create one new Material asset under Assets without overwriting",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = MaterialCreateSchemaJson,
            Tags = new string[] { "material", "asset", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "asset_details",
            OutputLimitJson = MaterialOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MaterialCreate(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var pathResult = RequireMaterialAssetPath(context.ArgsJson, "material_path", true, out var materialPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (File.Exists(AbsolutePath(materialPath)) || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(materialPath) != null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "material_path already exists");
            }

            var shaderName = JsonUtil.ExtractString(context.ArgsJson, "shader");
            if (string.IsNullOrEmpty(shaderName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument shader is required");
            }

            if (shaderName.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "shader cannot contain NUL");
            }

            var shader = Shader.Find(shaderName);
            if (shader == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "shader was not found");
            }

            var material = new Material(shader) { name = Path.GetFileNameWithoutExtension(materialPath) };
            AssetDatabase.CreateAsset(material, materialPath);
            var created = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (created == null)
            {
                return CommandExecutionResult.Error("unity_exception", "Material create failed");
            }

            return CommandExecutionResult.Ok("{\"created\":true,\"material\":" + MaterialJson(created, materialPath) + "}");
        }

        [McpCommand(
            "material.assign",
            Title = "Assign material",
            Description = "Assign one Material asset to one Renderer slot in a loaded scene",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = MaterialAssignSchemaJson,
            Tags = new string[] { "material", "gameobject", "scene", "safe-write" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = MaterialOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MaterialAssign(McpCommandContext context)
        {
            var status = RequireEditing();
            if (status != null)
            {
                return status;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var pathResult = RequireMaterialAssetPath(context.ArgsJson, "material_path", false, out var materialPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var material = AssetDatabase.LoadAssetAtPath<Material>(materialPath);
            if (material == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "material_path must reference a Material asset");
            }

            var rendererResult = ResolveRenderer(context.ArgsJson, target, out var renderer, out var rendererComponentIndex);
            if (rendererResult != null)
            {
                return rendererResult;
            }

            var slotResult = ReadOptionalInt(context.ArgsJson, "slot", 0, 32, out var hasSlot, out var slot);
            if (slotResult != null)
            {
                return slotResult;
            }

            if (!hasSlot)
            {
                slot = 0;
            }

            var materials = renderer.sharedMaterials;
            if (materials == null || slot >= materials.Length)
            {
                return CommandExecutionResult.Error("invalid_field_value", "slot is out of range for this Renderer");
            }

            if (materials[slot] == material)
            {
                return CommandExecutionResult.Ok("{\"assigned\":false,\"scene_path\":" + JsonUtil.String(scenePath)
                    + ",\"object_path\":" + JsonUtil.String(objectPath)
                    + ",\"slot\":" + slot
                    + ",\"renderer\":" + RendererJson(renderer, rendererComponentIndex)
                    + ",\"material\":" + MaterialJson(material, materialPath) + "}");
            }

            Undo.RecordObject(renderer, "Assign Material");
            materials[slot] = material;
            renderer.sharedMaterials = materials;
            EditorUtility.SetDirty(renderer);
            EditorSceneManager.MarkSceneDirty(scene);
            return CommandExecutionResult.Ok("{\"assigned\":true,\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"slot\":" + slot
                + ",\"renderer\":" + RendererJson(renderer, rendererComponentIndex)
                + ",\"material\":" + MaterialJson(material, materialPath) + "}");
        }

        sealed class MaterialPropertyDefinition
        {
            public readonly string Name;
            public readonly string ValueShape;
            public readonly float MinValue;
            public readonly float MaxValue;

            public MaterialPropertyDefinition(string name, string valueShape, float minValue, float maxValue)
            {
                Name = name;
                ValueShape = valueShape;
                MinValue = minValue;
                MaxValue = maxValue;
            }
        }

        sealed class PrefabApplyOverridesSummary
        {
            public int PropertyModificationCount;
            public int ReturnedPropertyModificationCount;
            public bool PropertyModificationsTruncated;
            public int AddedComponentCount;
            public int RemovedComponentCount;
            public int AddedGameObjectCount;
            public bool CanApply;
            public string PreconditionHash = string.Empty;
            public string PropertyModificationsJson = "[]";
            public string WarningsJson = "[]";
        }

        static string MaterialPropertySchemaJson(string requestedShader, string materialShader, string materialPath, int maxResults)
        {
            var shaderSelector = string.IsNullOrEmpty(materialShader) ? requestedShader : materialShader;
            var requestedSpecificShader = !string.IsNullOrEmpty(shaderSelector);
            var builder = new StringBuilder();
            var returned = 0;
            var total = 0;

            builder.Append("{\"selector\":{");
            builder.Append("\"shader\":").Append(JsonUtil.String(requestedShader));
            builder.Append(",\"material_path\":").Append(JsonUtil.String(materialPath));
            builder.Append("},\"material_shader\":").Append(JsonUtil.String(materialShader));
            builder.Append(",\"write_command_planned\":\"material.property.set\"");
            builder.Append(",\"schemas\":[");

            AppendMaterialSchemaIfMatched(builder, "Standard", shaderSelector, requestedSpecificShader, maxResults, ref total, ref returned);
            AppendMaterialSchemaIfMatched(builder, "Universal Render Pipeline/Lit", shaderSelector, requestedSpecificShader, maxResults, ref total, ref returned);
            AppendMaterialSchemaIfMatched(builder, "Universal Render Pipeline/Unlit", shaderSelector, requestedSpecificShader, maxResults, ref total, ref returned);
            AppendMaterialSchemaIfMatched(builder, "Unlit/Color", shaderSelector, requestedSpecificShader, maxResults, ref total, ref returned);

            builder.Append("],\"returned\":").Append(returned);
            builder.Append(",\"total_matches\":").Append(total);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(total > returned));
            builder.Append(",\"unsupported\":").Append(JsonUtil.Bool(requestedSpecificShader && total == 0));
            builder.Append(",\"notes\":[\"Hardcoded shader allowlist only.\",\"No dynamic shader or serialized property inspection is used.\"]}");
            return builder.ToString();
        }

        static string MaterialPropertiesInspectJson(Material material, string materialPath, int maxProperties)
        {
            var shader = material.shader;
            var total = shader.GetPropertyCount();
            var returned = Math.Min(total, maxProperties);
            var builder = new StringBuilder();
            builder.Append("{\"material_path\":").Append(JsonUtil.String(materialPath));
            builder.Append(",\"material\":").Append(MaterialJson(material, materialPath));
            builder.Append(",\"shader\":").Append(JsonUtil.String(shader.name));
            builder.Append(",\"property_count\":").Append(total);
            builder.Append(",\"returned\":").Append(returned);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(total > returned));
            builder.Append(",\"properties\":[");
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var propertyName = shader.GetPropertyName(index);
                var propertyType = shader.GetPropertyType(index);
                builder.Append("{\"index\":").Append(index);
                builder.Append(",\"name\":").Append(JsonUtil.String(propertyName));
                builder.Append(",\"description\":").Append(JsonUtil.String(shader.GetPropertyDescription(index)));
                builder.Append(",\"type\":").Append(JsonUtil.String(propertyType.ToString()));
                builder.Append(",\"flags\":").Append(JsonUtil.String(shader.GetPropertyFlags(index).ToString()));
                builder.Append(",\"has_current_value\":").Append(JsonUtil.Bool(material.HasProperty(propertyName)));
                builder.Append(",\"current_value\":").Append(MaterialPropertyCurrentValueJson(material, propertyName, propertyType));
                builder.Append(",\"allowlisted_for_set\":").Append(JsonUtil.Bool(FindMaterialPropertyDefinition(shader.name, propertyName) != null));
                builder.Append('}');
            }

            builder.Append("],\"notes\":[\"Read-only shader property inspection.\",\"Texture values are reported as asset references only.\",\"Use material.property_schema for the conservative write allowlist.\"]}");
            return builder.ToString();
        }

        static string MaterialPropertyCurrentValueJson(Material material, string propertyName, ShaderPropertyType propertyType)
        {
            if (!material.HasProperty(propertyName))
            {
                return "null";
            }

            if (propertyType == ShaderPropertyType.Color)
            {
                return ColorJson(material.GetColor(propertyName));
            }

            if (propertyType == ShaderPropertyType.Vector)
            {
                var value = material.GetVector(propertyName);
                return "{\"x\":" + FloatJson(value.x)
                    + ",\"y\":" + FloatJson(value.y)
                    + ",\"z\":" + FloatJson(value.z)
                    + ",\"w\":" + FloatJson(value.w) + "}";
            }

            if (propertyType == ShaderPropertyType.Float || propertyType == ShaderPropertyType.Range)
            {
                return FloatJson(material.GetFloat(propertyName));
            }

            if (propertyType == ShaderPropertyType.Texture)
            {
                var texture = material.GetTexture(propertyName);
                return TextureReferenceJson(texture);
            }

            return "null";
        }

        static void AppendMaterialSchemaIfMatched(StringBuilder builder, string shaderName, string selector, bool requestedSpecificShader, int maxResults, ref int total, ref int returned)
        {
            if (requestedSpecificShader && !string.Equals(shaderName, selector, StringComparison.Ordinal))
            {
                return;
            }

            total++;
            if (returned >= maxResults)
            {
                return;
            }

            if (returned > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"shader\":").Append(JsonUtil.String(shaderName));
            builder.Append(",\"properties\":[");
            var properties = MaterialPropertyDefinitions(shaderName);
            for (var index = 0; index < properties.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendMaterialProperty(builder, properties[index]);
            }

            builder.Append("]}");
            returned++;
        }

        static void AppendMaterialProperty(StringBuilder builder, MaterialPropertyDefinition property)
        {
            builder.Append("{\"name\":").Append(JsonUtil.String(property.Name));
            builder.Append(",\"value_shape\":").Append(JsonUtil.String(property.ValueShape));
            if (property.ValueShape == "number")
            {
                builder.Append(",\"minimum\":").Append(FloatJson(property.MinValue));
                builder.Append(",\"maximum\":").Append(FloatJson(property.MaxValue));
            }
            else if (property.ValueShape == "color")
            {
                builder.Append(",\"bounds\":\"rgba finite numbers clamped to 0..1\"");
            }
            else if (property.ValueShape == "vector")
            {
                builder.Append(",\"bounds\":\"xyzw finite numbers\"");
            }
            else if (property.ValueShape == "texture")
            {
                builder.Append(",\"bounds\":\"texture_path must reference an existing Unity Texture asset\"");
            }

            builder.Append('}');
        }

        static MaterialPropertyDefinition[] MaterialPropertyDefinitions(string shaderName)
        {
            if (shaderName == "Standard")
            {
                return new[]
                {
                    new MaterialPropertyDefinition("_Color", "color", 0f, 1f),
                    new MaterialPropertyDefinition("_MainTex", "texture", 0f, 1f),
                    new MaterialPropertyDefinition("_Metallic", "number", 0f, 1f),
                    new MaterialPropertyDefinition("_Glossiness", "number", 0f, 1f),
                    new MaterialPropertyDefinition("_BumpMap", "texture", 0f, 1f),
                    new MaterialPropertyDefinition("_EmissionColor", "color", 0f, 1f),
                    new MaterialPropertyDefinition("_EmissionMap", "texture", 0f, 1f),
                };
            }

            if (shaderName == "Universal Render Pipeline/Lit")
            {
                return new[]
                {
                    new MaterialPropertyDefinition("_BaseColor", "color", 0f, 1f),
                    new MaterialPropertyDefinition("_BaseMap", "texture", 0f, 1f),
                    new MaterialPropertyDefinition("_Metallic", "number", 0f, 1f),
                    new MaterialPropertyDefinition("_Smoothness", "number", 0f, 1f),
                    new MaterialPropertyDefinition("_BumpMap", "texture", 0f, 1f),
                    new MaterialPropertyDefinition("_EmissionColor", "color", 0f, 1f),
                    new MaterialPropertyDefinition("_EmissionMap", "texture", 0f, 1f),
                };
            }

            if (shaderName == "Universal Render Pipeline/Unlit")
            {
                return new[]
                {
                    new MaterialPropertyDefinition("_BaseColor", "color", 0f, 1f),
                    new MaterialPropertyDefinition("_BaseMap", "texture", 0f, 1f),
                };
            }

            if (shaderName == "Unlit/Color")
            {
                return new[] { new MaterialPropertyDefinition("_Color", "color", 0f, 1f) };
            }

            return Array.Empty<MaterialPropertyDefinition>();
        }

        static MaterialPropertyDefinition FindMaterialPropertyDefinition(string shaderName, string propertyName)
        {
            var properties = MaterialPropertyDefinitions(shaderName);
            for (var index = 0; index < properties.Length; index++)
            {
                if (properties[index].Name == propertyName)
                {
                    return properties[index];
                }
            }

            return null;
        }

        static CommandExecutionResult PrepareMaterialPropertySet(string argsJson, Material material, string propertyName, out string currentJson, out string requestedJson, out bool changed, out List<string> warnings, out Action apply)
        {
            currentJson = "null";
            requestedJson = "null";
            changed = false;
            warnings = new List<string>();
            apply = null;

            var shaderName = material.shader == null ? string.Empty : material.shader.name;
            var property = FindMaterialPropertyDefinition(shaderName, propertyName);
            if (property == null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "property_name is not allowlisted for this material shader");
            }

            var typeResult = RequireMaterialShaderPropertyType(material, propertyName, property.ValueShape);
            if (typeResult != null)
            {
                return typeResult;
            }

            if (property.ValueShape == "number")
            {
                return PrepareFloat(argsJson, material.GetFloat(propertyName), property.MinValue, property.MaxValue, value => material.SetFloat(propertyName, value), out currentJson, out requestedJson, out changed, out apply);
            }

            if (property.ValueShape == "color")
            {
                return PrepareColor(argsJson, material.GetColor(propertyName), value => material.SetColor(propertyName, value), out currentJson, out requestedJson, out changed, warnings, out apply);
            }

            if (property.ValueShape == "vector")
            {
                return PrepareVector(argsJson, material.GetVector(propertyName), value => material.SetVector(propertyName, value), out currentJson, out requestedJson, out changed, out apply);
            }

            if (property.ValueShape == "texture")
            {
                return PrepareTexture(argsJson, material.GetTexture(propertyName), texture => material.SetTexture(propertyName, texture), out currentJson, out requestedJson, out changed, out apply);
            }

            return CommandExecutionResult.Error("invalid_field_value", "material property value shape is not supported");
        }

        static CommandExecutionResult RequireMaterialShaderPropertyType(Material material, string propertyName, string valueShape)
        {
            var shader = material.shader;
            if (shader == null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "material shader is missing");
            }

            for (var index = 0; index < shader.GetPropertyCount(); index++)
            {
                if (shader.GetPropertyName(index) != propertyName)
                {
                    continue;
                }

                var propertyType = shader.GetPropertyType(index);
                if (MaterialPropertyTypeMatches(valueShape, propertyType))
                {
                    return null;
                }

                return CommandExecutionResult.Error("invalid_field_value", "allowlisted property_name has shader type " + propertyType + " but expected " + valueShape);
            }

            return CommandExecutionResult.Error("invalid_field_value", "property_name was not found in the material shader property list");
        }

        static bool MaterialPropertyTypeMatches(string valueShape, ShaderPropertyType propertyType)
        {
            if (valueShape == "number")
            {
                return propertyType == ShaderPropertyType.Float || propertyType == ShaderPropertyType.Range;
            }

            if (valueShape == "color")
            {
                return propertyType == ShaderPropertyType.Color;
            }

            if (valueShape == "vector")
            {
                return propertyType == ShaderPropertyType.Vector;
            }

            if (valueShape == "texture")
            {
                return propertyType == ShaderPropertyType.Texture;
            }

            return false;
        }

        static CommandExecutionResult RequireEditing()
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_busy", "Command requires editing status; Unity is compiling");
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "Command requires editing status; Unity is in Play Mode");
            }

            return null;
        }

        static CommandExecutionResult RequireLoadedScene(string argsJson, out Scene scene, out string scenePath)
        {
            scene = default;
            var pathResult = RequireSceneAssetPath(argsJson, "scene_path", out scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            scene = FindLoadedScene(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Error("asset_not_found", "scene_path must reference a loaded scene");
            }

            return null;
        }

        static CommandExecutionResult RequireSceneObject(string argsJson, out Scene scene, out string scenePath, out GameObject target, out string objectPath)
        {
            target = null;
            objectPath = string.Empty;
            var sceneResult = RequireLoadedScene(argsJson, out scene, out scenePath);
            if (sceneResult != null)
            {
                return sceneResult;
            }

            objectPath = JsonUtil.ExtractString(argsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            objectPath = NormalizeObjectPath(objectPath);
            target = FindByPath(scene.GetRootGameObjects(), objectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Object path was not found");
            }

            return null;
        }

        static CommandExecutionResult RequireSceneComponent(string argsJson, bool allowTransform, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var objectResult = RequireSceneObject(argsJson, out scene, out scenePath, out target, out objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            return RequireComponentOnObject(argsJson, target, allowTransform, out component, out componentIndex);
        }

        static CommandExecutionResult RequirePrefabComponent(string argsJson, GameObject root, out GameObject target, out string objectPath, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var objectResult = RequirePrefabObject(argsJson, root, out target, out objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            return RequireComponentOnObject(argsJson, target, true, out component, out componentIndex);
        }

        static CommandExecutionResult RequirePrefabObject(string argsJson, GameObject root, out GameObject target, out string objectPath)
        {
            target = null;
            objectPath = JsonUtil.ExtractString(argsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            if (objectPath.Length > 512 || objectPath.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "object_path must be 512 characters or fewer and cannot contain NUL");
            }

            objectPath = NormalizeObjectPath(objectPath);
            target = FindByPath(new[] { root }, objectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Object path was not found");
            }

            return null;
        }

        static CommandExecutionResult ResolvePrefabParent(string argsJson, GameObject root, out Transform parent, out string parentPath)
        {
            parent = root.transform;
            parentPath = ObjectPath(root);
            if (!HasJsonProperty(argsJson, "parent_path"))
            {
                return null;
            }

            parentPath = JsonUtil.ExtractString(argsJson, "parent_path");
            if (string.IsNullOrEmpty(parentPath))
            {
                parentPath = ObjectPath(root);
                return null;
            }

            if (parentPath.Length > 512 || parentPath.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "parent_path must be 512 characters or fewer and cannot contain NUL");
            }

            parentPath = NormalizeObjectPath(parentPath);
            var parentObject = FindByPath(new[] { root }, parentPath);
            if (parentObject == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "parent_path was not found");
            }

            parent = parentObject.transform;
            return null;
        }

        static bool PrefabSiblingNameExists(Transform parent, string name, GameObject ignore)
        {
            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index).gameObject;
                if (child != ignore && child.name == name)
                {
                    return true;
                }
            }

            return false;
        }

        static string ChildObjectPath(Transform parent, string childName)
        {
            return ObjectPath(parent.gameObject) + "/" + childName;
        }

        static string ChildSceneObjectPath(GameObject parent, string childName)
        {
            return parent == null ? "/" + childName : ObjectPath(parent) + "/" + childName;
        }

        static CommandExecutionResult RequirePrefabPropertyComponent(string argsJson, GameObject root, out GameObject target, out string objectPath, out Component component, out int componentIndex)
        {
            var componentResult = RequirePrefabComponent(argsJson, root, out target, out objectPath, out component, out componentIndex);
            if (componentResult != null)
            {
                return componentResult;
            }

            return ValidatePropertySetComponent(component);
        }

        static CommandExecutionResult RequireComponentOnObject(string argsJson, GameObject target, bool allowTransform, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;

            var indexResult = ReadOptionalInt(argsJson, "component_index", 0, 256, out var hasRequestedIndex, out var requestedIndex);
            if (indexResult != null)
            {
                return indexResult;
            }

            var requestedType = JsonUtil.ExtractString(argsJson, "component_type");
            var fullTypeName = JsonUtil.ExtractString(argsJson, "full_type_name");
            var assemblyName = JsonUtil.ExtractString(argsJson, "assembly_name");
            if (!hasRequestedIndex && string.IsNullOrEmpty(requestedType) && string.IsNullOrEmpty(fullTypeName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument component_type, full_type_name, or component_index is required");
            }

            var components = target.GetComponents<Component>();
            if (hasRequestedIndex)
            {
                if (requestedIndex >= components.Length)
                {
                    return CommandExecutionResult.Error("asset_not_found", "Component was not found");
                }

                component = components[requestedIndex];
                componentIndex = requestedIndex;
                if (component == null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Missing script components are not supported by this command");
                }

                if ((!string.IsNullOrEmpty(requestedType) || !string.IsNullOrEmpty(fullTypeName) || !string.IsNullOrEmpty(assemblyName)) && !MatchesComponentSelector(component.GetType(), requestedType, fullTypeName, assemblyName))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "component_index does not match component selector");
                }

                return ValidateSelectedComponent(component, allowTransform);
            }

            var skippedMonoBehaviourMatch = false;
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !MatchesComponentSelector(candidate.GetType(), requestedType, fullTypeName, assemblyName))
                {
                    continue;
                }

                if (candidate is MonoBehaviour)
                {
                    skippedMonoBehaviourMatch = true;
                    continue;
                }

                if (component != null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "component_type is ambiguous on this object; pass component_index. candidates=" + ComponentCandidateHintsJson(new List<Component> { component, candidate }, new List<int> { componentIndex, index }));
                }

                component = candidate;
                componentIndex = index;
            }

            if (component == null)
            {
                if (skippedMonoBehaviourMatch)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "MonoBehaviour components are not supported by safe-write component commands");
                }

                return CommandExecutionResult.Error("asset_not_found", "Component was not found");
            }

            return ValidateSelectedComponent(component, allowTransform);
        }

        static CommandExecutionResult RequireScenePropertyComponent(string argsJson, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var objectResult = RequireSceneObject(argsJson, out scene, out scenePath, out target, out objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var indexResult = ReadOptionalInt(argsJson, "component_index", 0, 256, out var hasRequestedIndex, out var requestedIndex);
            if (indexResult != null)
            {
                return indexResult;
            }

            var requestedType = JsonUtil.ExtractString(argsJson, "component_type");
            var fullTypeName = JsonUtil.ExtractString(argsJson, "full_type_name");
            var assemblyName = JsonUtil.ExtractString(argsJson, "assembly_name");
            if (!hasRequestedIndex && string.IsNullOrEmpty(requestedType) && string.IsNullOrEmpty(fullTypeName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument component_type, full_type_name, or component_index is required");
            }

            var components = target.GetComponents<Component>();
            if (hasRequestedIndex)
            {
                if (requestedIndex >= components.Length)
                {
                    return CommandExecutionResult.Error("asset_not_found", "Component was not found");
                }

                component = components[requestedIndex];
                componentIndex = requestedIndex;
                if (component == null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Missing script components are not supported by this command");
                }

                if ((!string.IsNullOrEmpty(requestedType) || !string.IsNullOrEmpty(fullTypeName) || !string.IsNullOrEmpty(assemblyName)) && !MatchesComponentSelector(component.GetType(), requestedType, fullTypeName, assemblyName))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "component_index does not match component selector");
                }

                return ValidatePropertySetComponent(component);
            }

            var matches = new List<Component>();
            var matchIndexes = new List<int>();
            var skippedMonoBehaviourMatch = false;
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !MatchesComponentSelector(candidate.GetType(), requestedType, fullTypeName, assemblyName))
                {
                    continue;
                }

                if (candidate is MonoBehaviour)
                {
                    skippedMonoBehaviourMatch = true;
                    continue;
                }

                matches.Add(candidate);
                matchIndexes.Add(index);
            }

            if (matches.Count == 0)
            {
                if (skippedMonoBehaviourMatch)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "MonoBehaviour components are not supported by component.property.set");
                }

                return CommandExecutionResult.Error("asset_not_found", "Component was not found");
            }

            if (matches.Count > 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", "component selector is ambiguous on this object; pass component_index. candidates=" + ComponentCandidateHintsJson(matches, matchIndexes));
            }

            component = matches[0];
            componentIndex = matchIndexes[0];
            return ValidatePropertySetComponent(component);
        }

        static CommandExecutionResult ValidatePropertySetComponent(Component component)
        {
            if (component is MonoBehaviour)
            {
                return CommandExecutionResult.Error("invalid_field_value", "MonoBehaviour components are not supported by component.property.set");
            }

            if (SchemaRefForComponentType(component.GetType()).Length == 0 || component is Behaviour && !(component is Light) && !(component is Camera))
            {
                return CommandExecutionResult.Error("invalid_field_value", "component type has no allowlisted component.property.set properties");
            }

            return null;
        }

        static bool MatchesComponentSelector(Type type, string componentType, string fullTypeName, string assemblyName)
        {
            if (!string.IsNullOrEmpty(assemblyName) && !string.Equals(type.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(fullTypeName))
            {
                return string.Equals(type.FullName ?? type.Name, fullTypeName, StringComparison.OrdinalIgnoreCase);
            }

            return !string.IsNullOrEmpty(componentType) && MatchesComponentType(type, componentType);
        }

        static string ComponentCandidateHintsJson(List<Component> components, List<int> indexes)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < components.Count && index < 8; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var type = components[index].GetType();
                builder.Append("{\"component_index\":").Append(indexes[index]);
                builder.Append(",\"type\":").Append(JsonUtil.String(type.Name));
                builder.Append(",\"full_type_name\":").Append(JsonUtil.String(type.FullName ?? type.Name));
                builder.Append(",\"assembly_name\":").Append(JsonUtil.String(type.Assembly.GetName().Name));
                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        static CommandExecutionResult ResolveRenderer(string argsJson, GameObject target, out Renderer renderer, out int componentIndex)
        {
            renderer = null;
            componentIndex = -1;
            var indexResult = ReadOptionalInt(argsJson, "component_index", 0, 256, out var hasRequestedIndex, out var requestedIndex);
            if (indexResult != null)
            {
                return indexResult;
            }

            var components = target.GetComponents<Component>();
            if (hasRequestedIndex)
            {
                if (requestedIndex >= components.Length)
                {
                    return CommandExecutionResult.Error("asset_not_found", "Component was not found");
                }

                if (components[requestedIndex] == null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Missing script components are not supported by this command");
                }

                renderer = components[requestedIndex] as Renderer;
                if (renderer == null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "component_index must reference a Renderer component");
                }

                componentIndex = requestedIndex;
                return null;
            }

            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index] as Renderer;
                if (candidate == null)
                {
                    continue;
                }

                if (renderer != null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Multiple Renderer components found; pass component_index");
                }

                renderer = candidate;
                componentIndex = index;
            }

            if (renderer == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Renderer component was not found");
            }

            return null;
        }

        static CommandExecutionResult ValidateSelectedComponent(Component component, bool allowTransform)
        {
            if (component is MonoBehaviour)
            {
                return CommandExecutionResult.Error("invalid_field_value", "MonoBehaviour components are not supported by safe-write component commands");
            }

            if (!allowTransform && component is Transform)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Transform cannot be removed");
            }

            return null;
        }

        static bool IsDestructivePlannedOperation(string plannedOperation)
        {
            return string.Equals(plannedOperation, "delete", StringComparison.Ordinal)
                || string.Equals(plannedOperation, "component.remove", StringComparison.Ordinal);
        }

        static bool IsSupportedPlannedOperation(string plannedOperation)
        {
            return IsDestructivePlannedOperation(plannedOperation)
                || string.Equals(plannedOperation, "reparent", StringComparison.Ordinal)
                || string.Equals(plannedOperation, "material.assign", StringComparison.Ordinal)
                || string.Equals(plannedOperation, "component.property.set", StringComparison.Ordinal)
                || string.Equals(plannedOperation, "material.property.set", StringComparison.Ordinal);
        }

        static bool IsSupportedPrefabOperation(string operation)
        {
            return string.Equals(operation, "object.create", StringComparison.Ordinal)
                || string.Equals(operation, "object.delete", StringComparison.Ordinal)
                || string.Equals(operation, "component.add", StringComparison.Ordinal)
                || string.Equals(operation, "component.remove", StringComparison.Ordinal)
                || string.Equals(operation, "component.property.set", StringComparison.Ordinal)
                || string.Equals(operation, "variant.create", StringComparison.Ordinal)
                || string.Equals(operation, "apply_overrides", StringComparison.Ordinal);
        }

        static bool IsDestructivePrefabOperation(string operation)
        {
            return string.Equals(operation, "object.delete", StringComparison.Ordinal)
                || string.Equals(operation, "component.remove", StringComparison.Ordinal)
                || string.Equals(operation, "apply_overrides", StringComparison.Ordinal);
        }

        static string ComponentPropertySchemaJson(McpCommandContext context)
        {
            var typeName = JsonUtil.ExtractString(context.ArgsJson, "type_name");
            var fullTypeName = JsonUtil.ExtractString(context.ArgsJson, "full_type_name");
            var assemblyName = JsonUtil.ExtractString(context.ArgsJson, "assembly_name");
            var maxResults = Math.Max(1, Math.Min(8, JsonUtil.ExtractInt(context.ArgsJson, "max_results", 8)));
            var requestedType = !string.IsNullOrEmpty(typeName) || !string.IsNullOrEmpty(fullTypeName) || !string.IsNullOrEmpty(assemblyName);
            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;

            builder.Append("{\"selector\":{");
            builder.Append("\"type_name\":").Append(JsonUtil.String(typeName));
            builder.Append(",\"full_type_name\":").Append(JsonUtil.String(fullTypeName));
            builder.Append(",\"assembly_name\":").Append(JsonUtil.String(assemblyName));
            builder.Append("},\"write_command_planned\":\"component.property.set\"");
            builder.Append(",\"arbitrary_mono_behaviour_writes\":false");
            builder.Append(",\"schemas\":[");

            if (requestedType)
            {
                var matches = ComponentPropertySchemaMatches(typeName, fullTypeName, assemblyName);
                foreach (var type in matches)
                {
                    if (count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    if (!AppendSchemaForComponentType(builder, type, count > 0))
                    {
                        continue;
                    }

                    count++;
                }

                builder.Append("],\"ambiguous\":").Append(JsonUtil.Bool(matches.Count > 1));
                builder.Append(",\"requires_exact_selector\":").Append(JsonUtil.Bool(matches.Count > 1));
                builder.Append(",\"unsupported_matches\":").Append(UnsupportedComponentPropertyMatchesJson(matches));
            }
            else
            {
                AppendBuiltInPropertySchemaSummary(builder, maxResults, out count, out truncated);
                builder.Append("],\"ambiguous\":false,\"requires_exact_selector\":false,\"unsupported_matches\":[]");
            }

            builder.Append(",\"returned\":").Append(count);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(truncated));
            builder.Append(",\"notes\":[\"Allowlist only; no arbitrary MonoBehaviour field writes.\",\"This command is read-only and does not mutate scenes, prefabs, assets, or serialized properties.\"]}");
            return builder.ToString();
        }

        static List<Type> ComponentPropertySchemaMatches(string typeName, string fullTypeName, string assemblyName)
        {
            var matches = new List<Type>();
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(assemblyName) && !string.Equals(type.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!string.IsNullOrEmpty(fullTypeName))
                    {
                        if (string.Equals(type.FullName ?? type.Name, fullTypeName, StringComparison.OrdinalIgnoreCase))
                        {
                            matches.Add(type);
                        }

                        continue;
                    }

                    if (string.IsNullOrEmpty(typeName) || string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName ?? type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(type);
                    }
                }
            }

            matches.Sort((left, right) => string.Compare(left.FullName ?? left.Name, right.FullName ?? right.Name, StringComparison.Ordinal));
            return matches;
        }

        static bool AppendSchemaForComponentType(StringBuilder builder, Type type, bool prependComma)
        {
            if (typeof(MonoBehaviour).IsAssignableFrom(type))
            {
                return false;
            }

            var schemaRef = SchemaRefForComponentType(type);
            if (string.IsNullOrEmpty(schemaRef))
            {
                return false;
            }

            if (prependComma)
            {
                builder.Append(',');
            }

            AppendPropertySchema(builder, schemaRef, type.FullName ?? type.Name, type.Assembly.GetName().Name);
            return true;
        }

        static string SchemaRefForComponentType(Type type)
        {
            if (typeof(Light).IsAssignableFrom(type)) return "UnityEngine.Light";
            if (typeof(Camera).IsAssignableFrom(type)) return "UnityEngine.Camera";
            if (typeof(Collider).IsAssignableFrom(type)) return "UnityEngine.Collider";
            if (typeof(Renderer).IsAssignableFrom(type)) return "UnityEngine.Renderer";
            if (typeof(Rigidbody).IsAssignableFrom(type)) return "UnityEngine.Rigidbody";
            return string.Empty;
        }

        static void AppendBuiltInPropertySchemaSummary(StringBuilder builder, int maxResults, out int count, out bool truncated)
        {
            var schemaRefs = new[] { "UnityEngine.Light", "UnityEngine.Camera", "UnityEngine.Collider", "UnityEngine.Renderer", "UnityEngine.Rigidbody" };
            count = 0;
            truncated = false;
            foreach (var schemaRef in schemaRefs)
            {
                if (count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                AppendPropertySchema(builder, schemaRef, schemaRef, "UnityEngine");
                count++;
            }
        }

        static string UnsupportedComponentPropertyMatchesJson(List<Type> matches)
        {
            var builder = new StringBuilder();
            var count = 0;
            builder.Append('[');
            foreach (var type in matches)
            {
                if (SchemaRefForComponentType(type).Length > 0 && !typeof(MonoBehaviour).IsAssignableFrom(type))
                {
                    continue;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"full_type_name\":").Append(JsonUtil.String(type.FullName ?? type.Name));
                builder.Append(",\"assembly_name\":").Append(JsonUtil.String(type.Assembly.GetName().Name));
                builder.Append(",\"reason\":").Append(JsonUtil.String(typeof(MonoBehaviour).IsAssignableFrom(type) ? "arbitrary MonoBehaviour field writes are not allowlisted" : "component type has no allowlisted safe-write properties"));
                builder.Append('}');
                count++;
                if (count >= 8)
                {
                    break;
                }
            }

            builder.Append(']');
            return builder.ToString();
        }

        static void AppendPropertySchema(StringBuilder builder, string schemaRef, string fullTypeName, string assemblyName)
        {
            builder.Append("{\"schema_ref\":").Append(JsonUtil.String(schemaRef));
            builder.Append(",\"full_type_name\":").Append(JsonUtil.String(fullTypeName));
            builder.Append(",\"assembly_name\":").Append(JsonUtil.String(assemblyName));
            builder.Append(",\"properties\":[");
            AppendSchemaProperties(builder, schemaRef);
            builder.Append("]}");
        }

        static void AppendSchemaProperties(StringBuilder builder, string schemaRef)
        {
            var wrote = false;
            if (schemaRef == "UnityEngine.Light" || schemaRef == "UnityEngine.Camera")
            {
                AppendProperty(builder, ref wrote, "enabled", "boolean", null, "Inherited Behaviour.enabled.");
            }

            if (schemaRef == "UnityEngine.Light")
            {
                AppendProperty(builder, ref wrote, "intensity", "number", "0..8 typical; finite number.", null);
                AppendProperty(builder, ref wrote, "color", "color", "{r,g,b,a} numbers clamped by future setter.", null);
                AppendProperty(builder, ref wrote, "range", "number", ">= 0 finite number.", null);
                AppendProperty(builder, ref wrote, "spotAngle", "number", "1..179 degrees for Spot lights.", null);
            }
            else if (schemaRef == "UnityEngine.Camera")
            {
                AppendProperty(builder, ref wrote, "fieldOfView", "number", "1..179 degrees.", null);
                AppendProperty(builder, ref wrote, "orthographic", "boolean", null, null);
                AppendProperty(builder, ref wrote, "orthographicSize", "number", "> 0 finite number.", null);
                AppendProperty(builder, ref wrote, "nearClipPlane", "number", "> 0 and less than farClipPlane.", null);
                AppendProperty(builder, ref wrote, "farClipPlane", "number", "> nearClipPlane.", null);
                AppendProperty(builder, ref wrote, "clearFlags", "enum_string", "UnityEngine.CameraClearFlags name.", null);
                AppendProperty(builder, ref wrote, "backgroundColor", "color", "{r,g,b,a} numbers clamped by future setter.", null);
            }
            else if (schemaRef == "UnityEngine.Collider")
            {
                AppendProperty(builder, ref wrote, "enabled", "boolean", null, null);
                AppendProperty(builder, ref wrote, "isTrigger", "boolean", null, null);
            }
            else if (schemaRef == "UnityEngine.Renderer")
            {
                AppendProperty(builder, ref wrote, "enabled", "boolean", null, null);
                AppendProperty(builder, ref wrote, "shadowCastingMode", "enum_string", "UnityEngine.Rendering.ShadowCastingMode name.", null);
                AppendProperty(builder, ref wrote, "receiveShadows", "boolean", null, null);
            }
            else if (schemaRef == "UnityEngine.Rigidbody")
            {
                AppendProperty(builder, ref wrote, "isKinematic", "boolean", null, null);
                AppendProperty(builder, ref wrote, "useGravity", "boolean", null, null);
                AppendProperty(builder, ref wrote, "mass", "number", "> 0 finite number; stable UnityEngine.Rigidbody API.", null);
                AppendProperty(builder, ref wrote, "linearDamping", "number", ">= 0 finite number.", "Maps to Rigidbody.drag on Unity versions before 6000.");
                AppendProperty(builder, ref wrote, "angularDamping", "number", ">= 0 finite number.", "Maps to Rigidbody.angularDrag on Unity versions before 6000.");
            }
        }

        static void AppendProperty(StringBuilder builder, ref bool wrote, string name, string valueShape, string bounds, string notes)
        {
            if (wrote)
            {
                builder.Append(',');
            }

            builder.Append("{\"name\":").Append(JsonUtil.String(name));
            builder.Append(",\"value_shape\":").Append(JsonUtil.String(valueShape));
            if (!string.IsNullOrEmpty(bounds))
            {
                builder.Append(",\"bounds\":").Append(JsonUtil.String(bounds));
            }

            if (!string.IsNullOrEmpty(notes))
            {
                builder.Append(",\"notes\":").Append(JsonUtil.String(notes));
            }

            builder.Append('}');
            wrote = true;
        }

        static CommandExecutionResult PrepareComponentPropertySet(string argsJson, Component component, string propertyName, out string currentJson, out string requestedJson, out bool changed, out List<string> warnings, out Action apply)
        {
            currentJson = "null";
            requestedJson = "null";
            changed = false;
            warnings = new List<string>();
            apply = null;

            if (component is Light light)
            {
                if (propertyName == "enabled") return PrepareBool(argsJson, light.enabled, value => light.enabled = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "intensity") return PrepareFloat(argsJson, light.intensity, 0f, 8f, value => light.intensity = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "range") return PrepareFloat(argsJson, light.range, 0f, 10000f, value => light.range = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "spotAngle") return PrepareFloat(argsJson, light.spotAngle, 1f, 179f, value => light.spotAngle = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "color") return PrepareColor(argsJson, light.color, value => light.color = value, out currentJson, out requestedJson, out changed, warnings, out apply);
            }
            else if (component is Camera camera)
            {
                if (propertyName == "enabled") return PrepareBool(argsJson, camera.enabled, value => camera.enabled = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "fieldOfView") return PrepareFloat(argsJson, camera.fieldOfView, 1f, 179f, value => camera.fieldOfView = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "orthographic") return PrepareBool(argsJson, camera.orthographic, value => camera.orthographic = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "orthographicSize") return PrepareFloat(argsJson, camera.orthographicSize, 0.0001f, 10000f, value => camera.orthographicSize = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "nearClipPlane") return PrepareFloat(argsJson, camera.nearClipPlane, 0.0001f, Math.Min(10000f, camera.farClipPlane - 0.0001f), value => camera.nearClipPlane = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "farClipPlane") return PrepareFloat(argsJson, camera.farClipPlane, camera.nearClipPlane + 0.0001f, 100000f, value => camera.farClipPlane = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "clearFlags") return PrepareEnum(argsJson, camera.clearFlags, value => camera.clearFlags = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "backgroundColor") return PrepareColor(argsJson, camera.backgroundColor, value => camera.backgroundColor = value, out currentJson, out requestedJson, out changed, warnings, out apply);
            }
            else if (component is Collider collider)
            {
                if (propertyName == "enabled") return PrepareBool(argsJson, collider.enabled, value => collider.enabled = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "isTrigger") return PrepareBool(argsJson, collider.isTrigger, value => collider.isTrigger = value, out currentJson, out requestedJson, out changed, out apply);
            }
            else if (component is Renderer renderer)
            {
                if (propertyName == "enabled") return PrepareBool(argsJson, renderer.enabled, value => renderer.enabled = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "shadowCastingMode") return PrepareEnum(argsJson, renderer.shadowCastingMode, value => renderer.shadowCastingMode = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "receiveShadows") return PrepareBool(argsJson, renderer.receiveShadows, value => renderer.receiveShadows = value, out currentJson, out requestedJson, out changed, out apply);
            }
            else if (component is Rigidbody rigidbody)
            {
                if (propertyName == "isKinematic") return PrepareBool(argsJson, rigidbody.isKinematic, value => rigidbody.isKinematic = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "useGravity") return PrepareBool(argsJson, rigidbody.useGravity, value => rigidbody.useGravity = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "mass") return PrepareFloat(argsJson, rigidbody.mass, 0.0001f, 100000f, value => rigidbody.mass = value, out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "linearDamping") return PrepareFloat(argsJson, RigidbodyLinearDamping(rigidbody), 0f, 100000f, value => SetRigidbodyLinearDamping(rigidbody, value), out currentJson, out requestedJson, out changed, out apply);
                if (propertyName == "angularDamping") return PrepareFloat(argsJson, RigidbodyAngularDamping(rigidbody), 0f, 100000f, value => SetRigidbodyAngularDamping(rigidbody, value), out currentJson, out requestedJson, out changed, out apply);
            }

            return CommandExecutionResult.Error("invalid_field_value", "property_name is not allowlisted for this component type");
        }

        static float RigidbodyLinearDamping(Rigidbody rigidbody)
        {
#if UNITY_6000_0_OR_NEWER
            return rigidbody.linearDamping;
#else
            return rigidbody.drag;
#endif
        }

        static void SetRigidbodyLinearDamping(Rigidbody rigidbody, float value)
        {
#if UNITY_6000_0_OR_NEWER
            rigidbody.linearDamping = value;
#else
            rigidbody.drag = value;
#endif
        }

        static float RigidbodyAngularDamping(Rigidbody rigidbody)
        {
#if UNITY_6000_0_OR_NEWER
            return rigidbody.angularDamping;
#else
            return rigidbody.angularDrag;
#endif
        }

        static void SetRigidbodyAngularDamping(Rigidbody rigidbody, float value)
        {
#if UNITY_6000_0_OR_NEWER
            rigidbody.angularDamping = value;
#else
            rigidbody.angularDrag = value;
#endif
        }

        static CommandExecutionResult PrepareBool(string argsJson, bool current, Action<bool> setter, out string currentJson, out string requestedJson, out bool changed, out Action apply)
        {
            currentJson = JsonUtil.Bool(current);
            if (!HasJsonProperty(argsJson, "bool_value"))
            {
                requestedJson = "null";
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("schema_validation_failed", "bool_value is required for this property");
            }

            var requested = JsonUtil.ExtractBool(argsJson, "bool_value", false);
            requestedJson = JsonUtil.Bool(requested);
            changed = current != requested;
            apply = () => setter(requested);
            return null;
        }

        static CommandExecutionResult PrepareFloat(string argsJson, float current, float minValue, float maxValue, Action<float> setter, out string currentJson, out string requestedJson, out bool changed, out Action apply)
        {
            currentJson = FloatJson(current);
            if (!TryReadNumber(argsJson, "number_value", out var requested))
            {
                requestedJson = "null";
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("schema_validation_failed", "number_value is required for this property");
            }

            if (float.IsNaN(requested) || float.IsInfinity(requested) || requested < minValue || requested > maxValue)
            {
                requestedJson = FloatJson(requested);
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("invalid_field_value", "number_value must be finite and between " + FloatJson(minValue) + " and " + FloatJson(maxValue));
            }

            requestedJson = FloatJson(requested);
            changed = Math.Abs(current - requested) > 0.000001f;
            apply = () => setter(requested);
            return null;
        }

        static CommandExecutionResult PrepareEnum<TEnum>(string argsJson, TEnum current, Action<TEnum> setter, out string currentJson, out string requestedJson, out bool changed, out Action apply) where TEnum : struct
        {
            currentJson = JsonUtil.String(current.ToString());
            var requestedName = JsonUtil.ExtractString(argsJson, "string_value");
            if (string.IsNullOrEmpty(requestedName))
            {
                requestedJson = "null";
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("schema_validation_failed", "string_value is required for this enum property");
            }

            if (!Enum.TryParse<TEnum>(requestedName, false, out var requested) || !Enum.IsDefined(typeof(TEnum), requested))
            {
                requestedJson = JsonUtil.String(requestedName);
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("invalid_field_value", "string_value must be a valid " + typeof(TEnum).FullName + " name");
            }

            requestedJson = JsonUtil.String(requested.ToString());
            changed = !EqualityComparer<TEnum>.Default.Equals(current, requested);
            apply = () => setter(requested);
            return null;
        }

        static CommandExecutionResult PrepareColor(string argsJson, Color current, Action<Color> setter, out string currentJson, out string requestedJson, out bool changed, List<string> warnings, out Action apply)
        {
            currentJson = ColorJson(current);
            if (!TryReadNumber(argsJson, "color_r", out var r) || !TryReadNumber(argsJson, "color_g", out var g) || !TryReadNumber(argsJson, "color_b", out var b))
            {
                requestedJson = "null";
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("schema_validation_failed", "color_r, color_g, and color_b are required for this property");
            }

            var a = current.a;
            if (HasJsonProperty(argsJson, "color_a") && !TryReadNumber(argsJson, "color_a", out a))
            {
                requestedJson = "null";
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("invalid_field_value", "color_a must be a finite number");
            }

            if (!FiniteColor(r, g, b, a))
            {
                requestedJson = ColorJson(new Color(r, g, b, a));
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("invalid_field_value", "color fields must be finite numbers");
            }

            var clamped = new Color(Clamp01(r, warnings), Clamp01(g, warnings), Clamp01(b, warnings), Clamp01(a, warnings));
            requestedJson = ColorJson(clamped);
            changed = Math.Abs(current.r - clamped.r) > 0.000001f || Math.Abs(current.g - clamped.g) > 0.000001f || Math.Abs(current.b - clamped.b) > 0.000001f || Math.Abs(current.a - clamped.a) > 0.000001f;
            apply = () => setter(clamped);
            return null;
        }

        static CommandExecutionResult PrepareVector(string argsJson, Vector4 current, Action<Vector4> setter, out string currentJson, out string requestedJson, out bool changed, out Action apply)
        {
            currentJson = Vector4Json(current);
            if (!TryReadNumber(argsJson, "vector_x", out var x) || !TryReadNumber(argsJson, "vector_y", out var y) || !TryReadNumber(argsJson, "vector_z", out var z) || !TryReadNumber(argsJson, "vector_w", out var w))
            {
                requestedJson = "null";
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("schema_validation_failed", "vector_x, vector_y, vector_z, and vector_w are required for this property");
            }

            var requested = new Vector4(x, y, z, w);
            if (!FiniteVector(requested))
            {
                requestedJson = Vector4Json(requested);
                changed = false;
                apply = null;
                return CommandExecutionResult.Error("invalid_field_value", "vector fields must be finite numbers");
            }

            requestedJson = Vector4Json(requested);
            changed = Math.Abs(current.x - requested.x) > 0.000001f || Math.Abs(current.y - requested.y) > 0.000001f || Math.Abs(current.z - requested.z) > 0.000001f || Math.Abs(current.w - requested.w) > 0.000001f;
            apply = () => setter(requested);
            return null;
        }

        static CommandExecutionResult PrepareTexture(string argsJson, Texture current, Action<Texture> setter, out string currentJson, out string requestedJson, out bool changed, out Action apply)
        {
            currentJson = TextureReferenceJson(current);
            requestedJson = "null";
            changed = false;
            apply = null;

            var rawTexturePath = JsonUtil.ExtractString(argsJson, "texture_path");
            if (string.IsNullOrEmpty(rawTexturePath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "texture_path is required for this property");
            }

            var texturePath = PathGuard.RequireUnityAssetPath(rawTexturePath);
            var texture = AssetDatabase.LoadAssetAtPath<Texture>(texturePath);
            if (texture == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "texture_path must reference a Texture asset");
            }

            requestedJson = TextureReferenceJson(texture);
            changed = current != texture;
            apply = () => setter(texture);
            return null;
        }

        static bool FiniteVector(Vector4 vector)
        {
            return !float.IsNaN(vector.x) && !float.IsInfinity(vector.x)
                && !float.IsNaN(vector.y) && !float.IsInfinity(vector.y)
                && !float.IsNaN(vector.z) && !float.IsInfinity(vector.z)
                && !float.IsNaN(vector.w) && !float.IsInfinity(vector.w);
        }

        static bool TryReadNumber(string argsJson, string property, out float value)
        {
            value = 0f;
            var colon = FindPropertyColon(argsJson, property);
            if (colon < 0)
            {
                return false;
            }

            var index = IndexOfNextNonWhitespace(argsJson, colon + 1);
            if (index < 0)
            {
                return false;
            }

            var start = index;
            if (argsJson[index] == '-')
            {
                index++;
            }

            var hasDigit = false;
            while (index < argsJson.Length && char.IsDigit(argsJson[index]))
            {
                hasDigit = true;
                index++;
            }

            if (index < argsJson.Length && argsJson[index] == '.')
            {
                index++;
                while (index < argsJson.Length && char.IsDigit(argsJson[index]))
                {
                    hasDigit = true;
                    index++;
                }
            }

            if (index < argsJson.Length && (argsJson[index] == 'e' || argsJson[index] == 'E'))
            {
                index++;
                if (index < argsJson.Length && (argsJson[index] == '-' || argsJson[index] == '+'))
                {
                    index++;
                }

                var exponentHasDigit = false;
                while (index < argsJson.Length && char.IsDigit(argsJson[index]))
                {
                    exponentHasDigit = true;
                    index++;
                }

                hasDigit = hasDigit && exponentHasDigit;
            }

            if (!hasDigit || !IsJsonNumberTerminator(argsJson, index))
            {
                return false;
            }

            return float.TryParse(argsJson.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value) && !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static bool FiniteColor(float r, float g, float b, float a)
        {
            return !(float.IsNaN(r) || float.IsInfinity(r) || float.IsNaN(g) || float.IsInfinity(g) || float.IsNaN(b) || float.IsInfinity(b) || float.IsNaN(a) || float.IsInfinity(a));
        }

        static float Clamp01(float value, List<string> warnings)
        {
            if (value < 0f)
            {
                if (!warnings.Contains("color_value_clamped")) warnings.Add("color_value_clamped");
                return 0f;
            }

            if (value > 1f)
            {
                if (!warnings.Contains("color_value_clamped")) warnings.Add("color_value_clamped");
                return 1f;
            }

            return value;
        }

        static CommandExecutionResult ResolveComponentType(string argsJson, out Type componentType)
        {
            componentType = null;
            var rawType = JsonUtil.ExtractString(argsJson, "component_type");
            if (string.IsNullOrEmpty(rawType))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument component_type is required");
            }

            var matches = new List<Type>();
            var unsupportedMatches = new List<Type>();
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (MatchesComponentType(type, rawType))
                    {
                        if (typeof(MonoBehaviour).IsAssignableFrom(type))
                        {
                            unsupportedMatches.Add(type);
                            continue;
                        }

                        matches.Add(type);
                    }
                }
            }

            if (matches.Count == 0)
            {
                if (unsupportedMatches.Count > 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "MonoBehaviour components are not supported by safe-write component commands");
                }

                return CommandExecutionResult.Error("asset_not_found", "component_type was not found");
            }

            if (matches.Count > 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", "component_type is ambiguous; use a full type name");
            }

            componentType = matches[0];
            return null;
        }

        static bool MatchesComponentType(Type type, string componentType)
        {
            return string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.FullName ?? type.Name, componentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(type.AssemblyQualifiedName ?? string.Empty, componentType, StringComparison.OrdinalIgnoreCase);
        }

        static Type[] SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types;
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        static CommandExecutionResult ResolveOptionalParent(string argsJson, Scene scene, out GameObject parent, out string parentPath)
        {
            parent = null;
            parentPath = string.Empty;
            if (!HasJsonProperty(argsJson, "parent_path"))
            {
                return null;
            }

            parentPath = JsonUtil.ExtractString(argsJson, "parent_path");
            if (string.IsNullOrEmpty(parentPath))
            {
                return null;
            }

            parentPath = NormalizeObjectPath(parentPath);
            parent = FindByPath(scene.GetRootGameObjects(), parentPath);
            if (parent == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "parent_path was not found");
            }

            return null;
        }

        static CommandExecutionResult RequireSceneAssetPath(string argsJson, string field, out string scenePath)
        {
            scenePath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            scenePath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must end with .unity");
            }

            return null;
        }

        static CommandExecutionResult RequireMaterialAssetPath(string argsJson, string field, bool requireAssets, out string materialPath)
        {
            materialPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            materialPath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!materialPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must end with .mat");
            }

            if (!requireAssets)
            {
                return null;
            }

            if (!materialPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be under Assets/");
            }

            var separator = materialPath.LastIndexOf('/');
            if (separator < "Assets".Length || separator >= materialPath.Length - 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must reference a .mat asset under Assets");
            }

            var parentPath = materialPath.Substring(0, separator);
            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Parent folder must already exist");
            }

            return null;
        }

        static CommandExecutionResult RequirePrefabAssetPath(string argsJson, string field, out string prefabPath)
        {
            prefabPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            prefabPath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!prefabPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must end with .prefab");
            }

            if (!prefabPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be under Assets/");
            }

            return null;
        }

        static CommandExecutionResult RequireNewPrefabAssetPath(string argsJson, string field, out string prefabPath)
        {
            var pathResult = RequirePrefabAssetPath(argsJson, field, out prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var separator = prefabPath.LastIndexOf('/');
            if (separator < "Assets".Length || separator >= prefabPath.Length - 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must reference a .prefab asset under Assets");
            }

            var parentPath = prefabPath.Substring(0, separator);
            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Parent folder must already exist");
            }

            if (File.Exists(AbsolutePath(prefabPath)) || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(prefabPath) != null)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " already exists");
            }

            return null;
        }

        static CommandExecutionResult RequireObjectName(string argsJson, string field, out string name)
        {
            name = JsonUtil.ExtractString(argsJson, field);
            return ValidateObjectName(name, field);
        }

        static CommandExecutionResult ValidateObjectName(string name, string field)
        {
            if (string.IsNullOrEmpty(name))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            if (name.Length > 128)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be 128 characters or fewer");
            }

            if (name.IndexOf('/') >= 0 || name.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " cannot contain / or NUL");
            }

            return null;
        }

        static CommandExecutionResult ValidateOptionalHierarchyPath(string argsJson, string field)
        {
            if (!HasJsonProperty(argsJson, field))
            {
                return null;
            }

            var path = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(path))
            {
                return null;
            }

            if (path.Length > 512 || path.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be 512 characters or fewer and cannot contain NUL");
            }

            return null;
        }

        static Scene FindLoadedScene(string scenePath)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (string.Equals(scene.path, scenePath, StringComparison.Ordinal))
                {
                    return scene;
                }
            }

            return default;
        }

        static void SetFirstOtherSceneActive(Scene current)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && scene != current)
                {
                    SceneManager.SetActiveScene(scene);
                    return;
                }
            }
        }

        static GameObject FindByPath(GameObject[] roots, string objectPath)
        {
            var normalized = NormalizeObjectPath(objectPath);
            var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            foreach (var root in roots)
            {
                if (root.name != parts[0])
                {
                    continue;
                }

                var current = root;
                for (var index = 1; index < parts.Length; index++)
                {
                    current = FindChild(current, parts[index]);
                    if (current == null)
                    {
                        break;
                    }
                }

                if (current != null)
                {
                    return current;
                }
            }

            return null;
        }

        static GameObject FindChild(GameObject parent, string name)
        {
            for (var index = 0; index < parent.transform.childCount; index++)
            {
                var child = parent.transform.GetChild(index).gameObject;
                if (child.name == name)
                {
                    return child;
                }
            }

            return null;
        }

        static string NormalizeObjectPath(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                return "/";
            }

            return objectPath.StartsWith("/", StringComparison.Ordinal) ? objectPath : "/" + objectPath;
        }

        static bool SiblingNameExists(Scene scene, Transform parent, string name, GameObject ignore)
        {
            if (parent == null)
            {
                var roots = scene.GetRootGameObjects();
                for (var index = 0; index < roots.Length; index++)
                {
                    if (roots[index] != ignore && roots[index].name == name)
                    {
                        return true;
                    }
                }

                return false;
            }

            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index).gameObject;
                if (child != ignore && child.name == name)
                {
                    return true;
                }
            }

            return false;
        }

        static string UniqueName(Scene scene, Transform parent, string baseName)
        {
            var candidate = baseName;
            var suffix = 2;
            while (SiblingNameExists(scene, parent, candidate, null))
            {
                candidate = baseName + " " + suffix;
                suffix++;
            }

            return candidate;
        }

        static bool IsDescendantOf(Transform candidate, Transform ancestor)
        {
            var current = candidate;
            while (current != null)
            {
                if (current == ancestor)
                {
                    return true;
                }

                current = current.parent;
            }

            return false;
        }

        static CommandExecutionResult ReadOptionalInt(string argsJson, string property, int minValue, int maxValue, out bool hasValue, out int value)
        {
            hasValue = false;
            value = 0;
            var colon = FindPropertyColon(argsJson, property);
            if (colon < 0)
            {
                return null;
            }

            var index = IndexOfNextNonWhitespace(argsJson, colon + 1);
            if (index < 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", property + " must be an integer");
            }

            var start = index;
            if (argsJson[index] == '-')
            {
                index++;
            }

            var hasDigit = false;
            while (index < argsJson.Length && char.IsDigit(argsJson[index]))
            {
                hasDigit = true;
                index++;
            }

            if (!hasDigit || !IsJsonNumberTerminator(argsJson, index) || !int.TryParse(argsJson.Substring(start, index - start), out value))
            {
                return CommandExecutionResult.Error("invalid_field_value", property + " must be an integer");
            }

            if (value < minValue || value > maxValue)
            {
                return CommandExecutionResult.Error("invalid_field_value", property + " must be between " + minValue + " and " + maxValue);
            }

            hasValue = true;
            return null;
        }

        static bool IsJsonNumberTerminator(string json, int index)
        {
            var next = IndexOfNextNonWhitespace(json, index);
            return next < 0 || json[next] == ',' || json[next] == '}';
        }

        static CommandExecutionResult ReadVector(string argsJson, string property, out bool hasValue, out Vector3 value)
        {
            hasValue = false;
            value = Vector3.zero;
            if (!HasJsonProperty(argsJson, property))
            {
                return null;
            }

            var objectJson = JsonUtil.ExtractObject(argsJson, property);
            if (!TryExtractFloat(objectJson, "x", out var x)
                || !TryExtractFloat(objectJson, "y", out var y)
                || !TryExtractFloat(objectJson, "z", out var z))
            {
                return CommandExecutionResult.Error("invalid_field_value", property + " must include numeric x, y and z");
            }

            hasValue = true;
            value = new Vector3(x, y, z);
            return null;
        }

        static bool TryExtractFloat(string json, string property, out float value)
        {
            value = 0f;
            var colon = FindPropertyColon(json, property);
            if (colon < 0)
            {
                return false;
            }

            var index = IndexOfNextNonWhitespace(json, colon + 1);
            if (index < 0)
            {
                return false;
            }

            var start = index;
            if (json[index] == '-')
            {
                index++;
            }

            var hasDigit = false;
            while (index < json.Length && char.IsDigit(json[index]))
            {
                hasDigit = true;
                index++;
            }

            if (index < json.Length && json[index] == '.')
            {
                index++;
                while (index < json.Length && char.IsDigit(json[index]))
                {
                    hasDigit = true;
                    index++;
                }
            }

            if (index < json.Length && (json[index] == 'e' || json[index] == 'E'))
            {
                index++;
                if (index < json.Length && (json[index] == '-' || json[index] == '+'))
                {
                    index++;
                }

                var exponentHasDigit = false;
                while (index < json.Length && char.IsDigit(json[index]))
                {
                    exponentHasDigit = true;
                    index++;
                }

                hasDigit = hasDigit && exponentHasDigit;
            }

            if (!hasDigit)
            {
                return false;
            }

            return float.TryParse(json.Substring(start, index - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static CommandExecutionResult ExtractPrefabUguiRectRequest(string json, out PrefabUguiRectRequest request)
        {
            request = new PrefabUguiRectRequest
            {
                AnchorMin = new Vector2(0.5f, 0.5f),
                AnchorMax = new Vector2(0.5f, 0.5f),
                Pivot = new Vector2(0.5f, 0.5f),
                AnchoredPosition = Vector2.zero,
                SizeDelta = new Vector2(100f, 100f)
            };
            var result = ReadPrefabUguiVector2(json, "anchor_min", out request.HasAnchorMin, out request.AnchorMin);
            if (result != null) return result;
            result = ReadPrefabUguiVector2(json, "anchor_max", out request.HasAnchorMax, out request.AnchorMax);
            if (result != null) return result;
            result = ReadPrefabUguiVector2(json, "pivot", out request.HasPivot, out request.Pivot);
            if (result != null) return result;
            result = ReadPrefabUguiVector2(json, "anchored_position", out request.HasAnchoredPosition, out request.AnchoredPosition);
            if (result != null) return result;
            return ReadPrefabUguiVector2(json, "size_delta", out request.HasSizeDelta, out request.SizeDelta);
        }

        static CommandExecutionResult ReadPrefabUguiVector2(string json, string field, out bool hasValue, out Vector2 value)
        {
            hasValue = false;
            value = Vector2.zero;
            if (!HasJsonProperty(json, field)) return null;
            var values = ExtractFloatArray(json, field, 2);
            if (values.Count != 2 || !IsFinite(values[0]) || !IsFinite(values[1])) return CommandExecutionResult.Error("invalid_field_value", field + " must be an array of two finite numbers");
            value = new Vector2(values[0], values[1]);
            hasValue = true;
            return null;
        }

        static List<float> ExtractFloatArray(string json, string field, int max)
        {
            var values = new List<float>();
            var at = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal);
            if (at < 0) return values;
            var start = json.IndexOf('[', at);
            var end = start < 0 ? -1 : json.IndexOf(']', start);
            if (end < 0) return values;
            var token = new StringBuilder();
            for (var index = start + 1; index < end && values.Count < max; index++)
            {
                var ch = json[index];
                if ("-+.0123456789eE".IndexOf(ch) >= 0) token.Append(ch);
                else FlushFloat(token, values, max);
            }

            FlushFloat(token, values, max);
            return values;
        }

        static void FlushFloat(StringBuilder token, List<float> values, int max)
        {
            if (token.Length == 0 || values.Count >= max) return;
            if (float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) values.Add(value);
            token.Length = 0;
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        static bool PrefabParentAcceptsUgui(Transform parent)
        {
            if (parent == null) return false;
            var gameObject = parent.gameObject;
            return gameObject.GetComponent<RectTransform>() != null || gameObject.GetComponent<Canvas>() != null;
        }

        static void ApplyPrefabUguiRectRequest(RectTransform rectTransform, PrefabUguiRectRequest request)
        {
            if (rectTransform == null) return;
            rectTransform.anchorMin = request.AnchorMin;
            rectTransform.anchorMax = request.AnchorMax;
            rectTransform.pivot = request.Pivot;
            rectTransform.anchoredPosition = request.AnchoredPosition;
            rectTransform.sizeDelta = request.SizeDelta;
        }

        static string PrefabUguiRectRequestJson(PrefabUguiRectRequest request)
        {
            return "{\"anchor_min\":" + Vector2Json(request.AnchorMin)
                + ",\"anchor_max\":" + Vector2Json(request.AnchorMax)
                + ",\"pivot\":" + Vector2Json(request.Pivot)
                + ",\"anchored_position\":" + Vector2Json(request.AnchoredPosition)
                + ",\"size_delta\":" + Vector2Json(request.SizeDelta) + "}";
        }

        static string RectTransformJson(RectTransform rectTransform)
        {
            if (rectTransform == null) return "null";
            return "{\"anchor_min\":" + Vector2Json(rectTransform.anchorMin)
                + ",\"anchor_max\":" + Vector2Json(rectTransform.anchorMax)
                + ",\"pivot\":" + Vector2Json(rectTransform.pivot)
                + ",\"anchored_position\":" + Vector2Json(rectTransform.anchoredPosition)
                + ",\"size_delta\":" + Vector2Json(rectTransform.sizeDelta) + "}";
        }

        static string Vector2Json(Vector2 value)
        {
            return "[" + FloatJson(value.x) + "," + FloatJson(value.y) + "]";
        }

        static CommandExecutionResult ResolvePrefabUguiAuthoringComponentType(string argsJson, out Type componentType)
        {
            componentType = null;
            var rawType = JsonUtil.ExtractString(argsJson, "component_type");
            if (string.IsNullOrEmpty(rawType)) return CommandExecutionResult.Error("schema_validation_failed", "Argument component_type is required");
            var matches = new List<Type>();
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || type.IsAbstract || !MatchesComponentType(type, rawType) || !IsPrefabUguiAuthoringComponentType(type)) continue;
                    matches.Add(type);
                }
            }

            if (matches.Count == 0) return CommandExecutionResult.Error("asset_not_found", "component_type was not found in the prefab uGUI authoring allowlist");
            if (matches.Count > 1) return CommandExecutionResult.Error("invalid_field_value", "component_type is ambiguous; use a full type name");
            componentType = matches[0];
            return null;
        }

        static bool IsPrefabUguiAuthoringComponentType(Type type)
        {
            if (type == null || !typeof(Component).IsAssignableFrom(type) || type == typeof(Transform) || type == typeof(RectTransform) || typeof(MonoBehaviour).IsAssignableFrom(type) && !IsAllowedUguiMonoBehaviour(type)) return false;
            return IsAllowedUguiMonoBehaviour(type) || type == typeof(CanvasGroup);
        }

        static bool IsAllowedUguiMonoBehaviour(Type type)
        {
            var name = type.FullName ?? type.Name;
            return name == "UnityEngine.UI.Image"
                || name == "UnityEngine.UI.RawImage"
                || name == "UnityEngine.UI.Button"
                || name == "UnityEngine.UI.Toggle"
                || name == "UnityEngine.UI.Slider"
                || name == "UnityEngine.UI.ScrollRect"
                || name == "UnityEngine.UI.HorizontalLayoutGroup"
                || name == "UnityEngine.UI.VerticalLayoutGroup"
                || name == "UnityEngine.UI.GridLayoutGroup"
                || name == "UnityEngine.UI.ContentSizeFitter"
                || name == "UnityEngine.UI.LayoutElement"
                || name == "UnityEngine.UI.AspectRatioFitter"
                || name == "UnityEngine.UI.Mask"
                || name == "UnityEngine.UI.RectMask2D"
                || name == "TMPro.TextMeshProUGUI";
        }

        static CommandExecutionResult ExtractPrefabUguiTextRequest(string json, out PrefabUguiTextRequest request)
        {
            request = new PrefabUguiTextRequest();
            if (HasJsonProperty(json, "text"))
            {
                request.HasText = true;
                request.Text = JsonUtil.ExtractString(json, "text");
                if (request.Text.Length > 4096 || request.Text.IndexOf('\0') >= 0) return CommandExecutionResult.Error("invalid_field_value", "text must be bounded text without NUL bytes");
            }

            if (HasJsonProperty(json, "font_size"))
            {
                request.HasFontSize = true;
                if (!TryExtractFloat(json, "font_size", out request.FontSize) || request.FontSize < 0f || request.FontSize > 4096f) return CommandExecutionResult.Error("invalid_field_value", "font_size must be a number between 0 and 4096");
            }

            if (HasJsonProperty(json, "color"))
            {
                request.HasColor = true;
                var color = ExtractFloatArray(json, "color", 4);
                if (color.Count != 4) return CommandExecutionResult.Error("invalid_field_value", "color must be an array of four numbers");
                request.Color = new Color(color[0], color[1], color[2], color[3]);
            }

            if (HasJsonProperty(json, "raycast_target"))
            {
                request.HasRaycastTarget = true;
                request.RaycastTarget = JsonUtil.ExtractBool(json, "raycast_target", false);
            }

            return request.HasAnyField ? null : CommandExecutionResult.Error("schema_validation_failed", "At least one allowlisted text field is required");
        }

        static CommandExecutionResult RequirePrefabUguiTextComponent(string argsJson, GameObject target, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var hasSelector = HasJsonProperty(argsJson, "component_index") || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "component_type")) || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "full_type_name"));
            if (hasSelector)
            {
                var selected = ResolvePrefabUguiTextComponentSelector(argsJson, target, out component, out componentIndex);
                if (selected != null) return selected;
                return IsPrefabUguiTextComponent(component) ? null : CommandExecutionResult.Error("invalid_field_value", "component selector must reference UnityEngine.UI.Text, TMPro.TMP_Text, or TMPro.TextMeshProUGUI");
            }

            var components = target.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (!IsPrefabUguiTextComponent(candidate)) continue;
                if (component != null) return CommandExecutionResult.Error("invalid_field_value", "text component selector is ambiguous on this object; pass component_index");
                component = candidate;
                componentIndex = index;
            }

            return component == null ? CommandExecutionResult.Error("asset_not_found", "Text component was not found") : null;
        }

        static CommandExecutionResult ResolvePrefabUguiTextComponentSelector(string argsJson, GameObject target, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var indexResult = ReadOptionalInt(argsJson, "component_index", 0, 256, out var hasRequestedIndex, out var requestedIndex);
            if (indexResult != null) return indexResult;
            var requestedType = JsonUtil.ExtractString(argsJson, "component_type");
            var fullTypeName = JsonUtil.ExtractString(argsJson, "full_type_name");
            var assemblyName = JsonUtil.ExtractString(argsJson, "assembly_name");
            var components = target.GetComponents<Component>();
            if (hasRequestedIndex)
            {
                if (requestedIndex >= components.Length) return CommandExecutionResult.Error("asset_not_found", "Component was not found");
                component = components[requestedIndex];
                componentIndex = requestedIndex;
                if (component == null) return CommandExecutionResult.Error("invalid_field_value", "Missing script components are not supported by this command");
                if ((!string.IsNullOrEmpty(requestedType) || !string.IsNullOrEmpty(fullTypeName) || !string.IsNullOrEmpty(assemblyName)) && !MatchesComponentSelector(component.GetType(), requestedType, fullTypeName, assemblyName)) return CommandExecutionResult.Error("invalid_field_value", "component_index does not match component selector");
                return null;
            }

            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !MatchesComponentSelector(candidate.GetType(), requestedType, fullTypeName, assemblyName) || !IsPrefabUguiTextComponent(candidate)) continue;
                if (component != null) return CommandExecutionResult.Error("invalid_field_value", "component_type is ambiguous on this object; pass component_index");
                component = candidate;
                componentIndex = index;
            }

            return component == null ? CommandExecutionResult.Error("asset_not_found", "Component was not found") : null;
        }

        static bool IsPrefabUguiTextComponent(Component component)
        {
            if (component == null) return false;
            var name = component.GetType().FullName ?? component.GetType().Name;
            return name == "UnityEngine.UI.Text" || name == "TMPro.TMP_Text" || name == "TMPro.TextMeshProUGUI";
        }

        static PrefabUguiTextState CapturePrefabUguiTextState(Component component)
        {
            return new PrefabUguiTextState
            {
                Text = GetPropertyValue(component, "text") as string ?? string.Empty,
                FontSize = ConvertToFloat(GetPropertyValue(component, "fontSize")),
                Color = GetPropertyValue(component, "color") is Color ? (Color)GetPropertyValue(component, "color") : Color.white,
                RaycastTarget = ConvertToBool(GetPropertyValue(component, "raycastTarget"))
            };
        }

        static PrefabUguiTextState RequestedPrefabUguiTextState(PrefabUguiTextState current, PrefabUguiTextRequest request)
        {
            var requested = current.Clone();
            if (request.HasText) requested.Text = request.Text;
            if (request.HasFontSize) requested.FontSize = request.FontSize;
            if (request.HasColor) requested.Color = request.Color;
            if (request.HasRaycastTarget) requested.RaycastTarget = request.RaycastTarget;
            return requested;
        }

        static List<string> PrefabUguiTextChangedFields(PrefabUguiTextState current, PrefabUguiTextState requested, PrefabUguiTextRequest request)
        {
            var fields = new List<string>();
            if (request.HasText && current.Text != requested.Text) fields.Add("text");
            if (request.HasFontSize && Math.Abs(current.FontSize - requested.FontSize) > 0.0001f) fields.Add("font_size");
            if (request.HasColor && ColorDistance(current.Color, requested.Color) > 0.0001f) fields.Add("color");
            if (request.HasRaycastTarget && current.RaycastTarget != requested.RaycastTarget) fields.Add("raycast_target");
            return fields;
        }

        static void ApplyPrefabUguiTextState(Component component, List<string> changedFields, PrefabUguiTextState requested)
        {
            for (var index = 0; index < changedFields.Count; index++)
            {
                var field = changedFields[index];
                if (field == "text") SetPropertyValue(component, "text", requested.Text);
                else if (field == "font_size") SetNumericPropertyValue(component, "fontSize", requested.FontSize);
                else if (field == "color") SetPropertyValue(component, "color", requested.Color);
                else if (field == "raycast_target") SetPropertyValue(component, "raycastTarget", requested.RaycastTarget);
            }
        }

        static string PrefabUguiTextStateJson(PrefabUguiTextState state)
        {
            return "{\"text\":" + JsonUtil.String(state.Text)
                + ",\"font_size\":" + FloatJson(state.FontSize)
                + ",\"color\":" + ColorJson(state.Color)
                + ",\"raycast_target\":" + JsonUtil.Bool(state.RaycastTarget) + "}";
        }

        static bool HasWritableProperty(object target, string name)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property != null && property.CanRead && property.CanWrite;
        }

        static object GetPropertyValue(object target, string name)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null || !property.CanRead ? null : property.GetValue(target, null);
        }

        static void SetPropertyValue(object target, string name, object value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property != null && property.CanWrite) property.SetValue(target, value, null);
        }

        static void SetNumericPropertyValue(object target, string name, float value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite) return;
            var type = property.PropertyType;
            if (type == typeof(float)) property.SetValue(target, value, null);
            else if (type == typeof(int)) property.SetValue(target, (int)Math.Round(value), null);
            else if (type == typeof(double)) property.SetValue(target, (double)value, null);
        }

        static float ConvertToFloat(object value)
        {
            if (value == null) return 0f;
            try { return Convert.ToSingle(value, CultureInfo.InvariantCulture); }
            catch { return 0f; }
        }

        static bool ConvertToBool(object value)
        {
            return value is bool && (bool)value;
        }

        static float ColorDistance(Color a, Color b)
        {
            return Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) + Math.Abs(a.a - b.a);
        }

        static string GameObjectJson(GameObject gameObject)
        {
            return "{\"path\":" + JsonUtil.String(ObjectPath(gameObject))
                + ",\"name\":" + JsonUtil.String(gameObject.name)
                + ",\"active\":" + JsonUtil.Bool(gameObject.activeSelf)
                + RuntimeObjectIdsJson(gameObject)
                + ",\"transform\":" + TransformJson(gameObject.transform) + "}";
        }

        static string WriteRiskReportJson(string scenePath, string objectPath, string plannedOperation, bool sceneLoaded, bool sceneDirty, bool found, bool checkpointRecommended, bool undoSupported, string prefabStatus, GameObject target, int maxChildren, int maxComponents, int maxMaterials)
        {
            return "{\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"planned_operation\":" + JsonUtil.String(string.IsNullOrEmpty(plannedOperation) ? "unspecified" : plannedOperation)
                + ",\"found\":" + JsonUtil.Bool(found)
                + ",\"scene_loaded\":" + JsonUtil.Bool(sceneLoaded)
                + ",\"scene_dirty\":" + JsonUtil.Bool(sceneDirty)
                + ",\"checkpoint_recommended\":" + JsonUtil.Bool(checkpointRecommended)
                + ",\"undo_supported\":" + JsonUtil.Bool(undoSupported)
                + ",\"prefab\":" + JsonUtil.String(prefabStatus)
                + ",\"children\":" + ChildrenRiskJson(target, maxChildren)
                + ",\"components\":" + ComponentsRiskJson(target, maxComponents)
                + ",\"materials\":" + MaterialsRiskJson(target, maxMaterials)
                + ",\"warnings\":" + WriteRiskWarningsJson(sceneLoaded, sceneDirty, found, plannedOperation, target)
                + ",\"suggested_verification_commands\":[\"scene.hierarchy\",\"scene.inspect_object\",\"scene.inspect_component\",\"asset.risk_report\"]}";
        }

        static string PrefabWriteRiskReportJson(string prefabPath, string objectPath, string plannedOperation, bool exists, GameObject root, GameObject target, int maxChildren, int maxComponents, int maxReferences, int maxMaterials, PrefabYamlIntegrityResult yamlIntegrity, bool assetLoadable, string assetLoadError)
        {
            var guid = AssetDatabase.AssetPathToGUID(prefabPath);
            var targetFound = root != null && (string.IsNullOrEmpty(objectPath) || target != null);
            return "{\"prefab_path\":" + JsonUtil.String(prefabPath)
                + ",\"asset_path\":" + JsonUtil.String(prefabPath)
                + ",\"path_guard\":{\"accepted\":true,\"scope\":\"Assets\",\"extension\":\".prefab\"}"
                + ",\"exists\":" + JsonUtil.Bool(exists)
                + ",\"guid\":" + JsonUtil.String(string.IsNullOrEmpty(guid) ? string.Empty : guid)
                + ",\"planned_operation\":" + JsonUtil.String(string.IsNullOrEmpty(plannedOperation) ? "unspecified" : plannedOperation)
                + ",\"root_name\":" + JsonUtil.String(root == null ? string.Empty : root.name)
                + ",\"object_path\":" + JsonUtil.String(string.IsNullOrEmpty(objectPath) ? (root == null ? string.Empty : ObjectPath(root)) : objectPath)
                + ",\"target_found\":" + JsonUtil.Bool(targetFound)
                + ",\"prefab_yaml_integrity\":" + yamlIntegrity.ToJson(assetLoadable, assetLoadError)
                + ",\"prefab\":" + PrefabAssetInfoJson(prefabPath, root)
                + ",\"children\":" + ChildrenRiskJson(target, maxChildren)
                + ",\"components\":" + ComponentsRiskJson(target, maxComponents)
                + ",\"materials\":" + MaterialsRiskJson(target, maxMaterials)
                + ",\"missing_script_count\":" + MissingScriptCount(root)
                + ",\"dependencies\":" + (assetLoadable ? PrefabDependencyHintsJson(prefabPath, maxReferences) : "{\"total\":0,\"returned\":0,\"truncated\":false,\"items\":[]}")
                + ",\"checkpoint_recommended\":" + JsonUtil.Bool(IsDestructivePrefabOperation(plannedOperation))
                + ",\"warnings\":" + PrefabWriteRiskWarningsJson(exists, targetFound, plannedOperation, root, yamlIntegrity, assetLoadable)
                + ",\"suggested_next_actions\":[\"prefab.hierarchy\",\"prefab.inspect_object\",\"prefab.inspect_component\",\"asset.risk_report\",\"prefab.edit.preview\"]}";
        }

        static string PrefabEditPreviewJson(string prefabPath, string operation, bool exists, GameObject root, GameObject target, string variantPath, string argsJson, PrefabYamlIntegrityResult yamlIntegrity, bool assetLoadable, string assetLoadError)
        {
            var objectPath = JsonUtil.ExtractString(argsJson, "object_path");
            if (!string.IsNullOrEmpty(objectPath))
            {
                objectPath = NormalizeObjectPath(objectPath);
            }

            var parentPath = JsonUtil.ExtractString(argsJson, "parent_path");
            if (!string.IsNullOrEmpty(parentPath))
            {
                parentPath = NormalizeObjectPath(parentPath);
            }

            var parent = root == null || string.IsNullOrEmpty(parentPath) ? null : FindByPath(new[] { root }, parentPath);
            var name = JsonUtil.ExtractString(argsJson, "name");
            var componentType = JsonUtil.ExtractString(argsJson, "component_type");
            var propertyName = JsonUtil.ExtractString(argsJson, "property_name");
            var hasComponentIndex = HasJsonProperty(argsJson, "component_index");
            var componentIndex = JsonUtil.ExtractInt(argsJson, "component_index", -1);
            var builder = new StringBuilder();
            builder.Append("{\"prefab_path\":").Append(JsonUtil.String(prefabPath));
            builder.Append(",\"operation\":").Append(JsonUtil.String(operation));
            builder.Append(",\"dry_run\":true,\"preview_only\":true,\"would_write\":false");
            builder.Append(",\"path_guard\":{\"accepted\":true,\"scope\":\"Assets\",\"extension\":\".prefab\"}");
            builder.Append(",\"prefab_yaml_integrity\":").Append(yamlIntegrity.ToJson(assetLoadable, assetLoadError));
            builder.Append(",\"target\":{\"prefab_exists\":").Append(JsonUtil.Bool(exists));
            builder.Append(",\"root_name\":").Append(JsonUtil.String(root == null ? string.Empty : root.name));
            builder.Append(",\"object_path\":").Append(JsonUtil.String(objectPath));
            builder.Append(",\"object_found\":").Append(JsonUtil.Bool(target != null));
            builder.Append(",\"parent_path\":").Append(JsonUtil.String(parentPath));
            builder.Append(",\"parent_found\":").Append(JsonUtil.Bool(parent != null || string.IsNullOrEmpty(parentPath))).Append('}');
            builder.Append(",\"planned_changes\":[").Append(PrefabPlannedChangeJson(operation, objectPath, parentPath, name, componentType, hasComponentIndex, componentIndex, propertyName, variantPath, argsJson)).Append(']');
            builder.Append(",\"required_future_approvals\":[\"policy approval for future prefab write command\",\"human approval for asset checkpoint or VCS diff review\"]");
            builder.Append(",\"checkpoint_recommended\":true");
            builder.Append(",\"conflict_warnings\":").Append(PrefabPreviewWarningsJson(operation, exists, root, target, parent, objectPath, parentPath, name, componentType, propertyName, variantPath, yamlIntegrity, assetLoadable));
            builder.Append(",\"next_actions\":[\"review planned_changes\",\"create checkpoint before future write\",\"inspect prefab target if target_found is false or ambiguous\"]}");
            return builder.ToString();
        }

        static PrefabApplyOverridesSummary BuildPrefabApplyOverridesSummary(string scenePath, string objectPath, string prefabPath, GameObject instanceRoot, string sourcePrefabPath)
        {
            var modifications = new List<PropertyModification>();
            var rawModifications = PrefabUtility.GetPropertyModifications(instanceRoot);
            if (rawModifications != null)
            {
                modifications.AddRange(rawModifications);
            }

            modifications.Sort((left, right) => string.Compare(PrefabPropertyModificationHashKey(left), PrefabPropertyModificationHashKey(right), StringComparison.Ordinal));

            var addedComponents = PrefabUtility.GetAddedComponents(instanceRoot);
            var removedComponents = PrefabUtility.GetRemovedComponents(instanceRoot);
            var addedGameObjects = PrefabUtility.GetAddedGameObjects(instanceRoot);
            var warnings = new List<string>();
            var unsupported = false;
            if (modifications.Count == 0)
            {
                warnings.Add("no_property_overrides_found");
            }

            if (modifications.Count > 64)
            {
                warnings.Add("too_many_property_overrides");
                unsupported = true;
            }

            if (addedComponents.Count > 0 || removedComponents.Count > 0 || addedGameObjects.Count > 0)
            {
                warnings.Add("structural_overrides_not_supported");
                unsupported = true;
            }

            for (var index = 0; index < modifications.Count; index++)
            {
                var modification = modifications[index];
                if (modification == null || modification.target == null)
                {
                    warnings.Add("missing_override_target");
                    unsupported = true;
                    continue;
                }

                if (modification.target is MonoBehaviour || string.Equals(modification.propertyPath, "m_Script", StringComparison.Ordinal))
                {
                    warnings.Add("monobehaviour_override_not_supported");
                    unsupported = true;
                }

                if (modification.objectReference != null && string.IsNullOrEmpty(AssetDatabase.GetAssetPath(modification.objectReference)))
                {
                    warnings.Add("scene_object_reference_override_not_supported");
                    unsupported = true;
                }
            }

            var hashInput = new StringBuilder();
            hashInput.Append(scenePath).Append('\n').Append(objectPath).Append('\n').Append(prefabPath).Append('\n').Append(sourcePrefabPath).Append('\n');
            hashInput.Append("added_components=").Append(addedComponents.Count).Append('\n');
            hashInput.Append("removed_components=").Append(removedComponents.Count).Append('\n');
            hashInput.Append("added_gameobjects=").Append(addedGameObjects.Count).Append('\n');
            for (var index = 0; index < modifications.Count; index++)
            {
                hashInput.Append(PrefabPropertyModificationHashKey(modifications[index])).Append('\n');
            }

            var summary = new PrefabApplyOverridesSummary
            {
                PropertyModificationCount = modifications.Count,
                AddedComponentCount = addedComponents.Count,
                RemovedComponentCount = removedComponents.Count,
                AddedGameObjectCount = addedGameObjects.Count,
                CanApply = !unsupported && modifications.Count > 0,
                PreconditionHash = Sha256Prefixed(hashInput.ToString()),
                WarningsJson = StringArrayJson(warnings)
            };
            summary.PropertyModificationsJson = PrefabPropertyModificationsJson(modifications, 32, out summary.ReturnedPropertyModificationCount, out summary.PropertyModificationsTruncated);
            return summary;
        }

        static string PrefabApplyOverridesResultJson(bool dryRun, bool applied, bool wouldApply, string scenePath, string objectPath, string prefabPath, string instanceRootPath, string sourcePrefabPath, bool sceneDirty, PrefabApplyOverridesSummary summary)
        {
            return "{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"applied\":" + JsonUtil.Bool(applied)
                + ",\"would_apply\":" + JsonUtil.Bool(wouldApply)
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"instance_root_path\":" + JsonUtil.String(instanceRootPath)
                + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                + ",\"source_prefab_path\":" + JsonUtil.String(sourcePrefabPath)
                + ",\"scene_dirty_after\":" + JsonUtil.Bool(sceneDirty)
                + ",\"scene_saved\":false"
                + ",\"saved_prefab\":" + JsonUtil.Bool(applied)
                + ",\"precondition_hash\":" + JsonUtil.String(summary.PreconditionHash)
                + ",\"override_counts\":{\"property_modifications\":" + summary.PropertyModificationCount
                + ",\"property_modifications_returned\":" + summary.ReturnedPropertyModificationCount
                + ",\"property_modifications_truncated\":" + JsonUtil.Bool(summary.PropertyModificationsTruncated)
                + ",\"added_components\":" + summary.AddedComponentCount
                + ",\"removed_components\":" + summary.RemovedComponentCount
                + ",\"added_gameobjects\":" + summary.AddedGameObjectCount + "}"
                + ",\"property_modifications\":" + summary.PropertyModificationsJson
                + ",\"changed_objects\":" + (applied ? "[" + JsonUtil.String(objectPath) + "]" : "[]")
                + ",\"changed_components\":[]"
                + ",\"unsupported\":" + JsonUtil.Bool(!summary.CanApply && summary.PropertyModificationCount > 0)
                + ",\"warnings\":" + summary.WarningsJson
                + ",\"checkpoint_recommended\":true"
                + ",\"notes\":[\"dry_run:true returns the precondition_hash required for a real apply\",\"Only non-structural non-MonoBehaviour property overrides are supported\"]"
                + ",\"next_valid_actions\":[\"review_property_modifications\",\"checkpoint.create\",\"retry_with_precondition_hash\"]}";
        }

        static string PrefabPropertyModificationsJson(List<PropertyModification> modifications, int maxResults, out int returned, out bool truncated)
        {
            returned = Math.Min(modifications.Count, maxResults);
            truncated = modifications.Count > returned;
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var modification = modifications[index];
                var target = modification == null ? null : modification.target;
                var objectReference = modification == null ? null : modification.objectReference;
                builder.Append("{\"target_type\":").Append(JsonUtil.String(target == null ? string.Empty : target.GetType().FullName));
                builder.Append(",\"target_name\":").Append(JsonUtil.String(target == null ? string.Empty : target.name));
                builder.Append(",\"property_path\":").Append(JsonUtil.String(modification == null ? string.Empty : modification.propertyPath));
                builder.Append(",\"value_preview\":").Append(JsonUtil.String(BoundedString(modification == null ? string.Empty : modification.value, 128)));
                builder.Append(",\"object_reference_path\":").Append(JsonUtil.String(objectReference == null ? string.Empty : AssetDatabase.GetAssetPath(objectReference)));
                builder.Append(",\"object_reference_type\":").Append(JsonUtil.String(objectReference == null ? string.Empty : objectReference.GetType().FullName));
                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string PrefabPropertyModificationHashKey(PropertyModification modification)
        {
            if (modification == null)
            {
                return "<null>";
            }

            var target = modification.target;
            var objectReference = modification.objectReference;
            return (target == null ? string.Empty : target.GetType().FullName) + "|"
                + (target == null ? string.Empty : target.name) + "|"
                + (modification.propertyPath ?? string.Empty) + "|"
                + (modification.value ?? string.Empty) + "|"
                + (objectReference == null ? string.Empty : objectReference.GetType().FullName) + "|"
                + (objectReference == null ? string.Empty : objectReference.name) + "|"
                + (objectReference == null ? string.Empty : AssetDatabase.GetAssetPath(objectReference));
        }

        static string PrefabAssetInfoJson(string prefabPath, GameObject root)
        {
            if (root == null)
            {
                return "{\"asset_type\":\"unknown\",\"base_asset_path\":\"\",\"base_guid\":\"\"}";
            }

            var basePath = string.Empty;
            var source = PrefabUtility.GetCorrespondingObjectFromSource(root);
            if (source != null)
            {
                basePath = AssetDatabase.GetAssetPath(source);
            }

            return "{\"asset_type\":" + JsonUtil.String(PrefabUtility.GetPrefabAssetType(root).ToString())
                + ",\"instance_status\":" + JsonUtil.String(PrefabStatus(root))
                + ",\"base_asset_path\":" + JsonUtil.String(basePath)
                + ",\"base_guid\":" + JsonUtil.String(string.IsNullOrEmpty(basePath) ? string.Empty : AssetDatabase.AssetPathToGUID(basePath))
                + "}";
        }

        static string PrefabDependencyHintsJson(string prefabPath, int maxReferences)
        {
            var dependencies = AssetDatabase.GetDependencies(prefabPath, true) ?? Array.Empty<string>();
            Array.Sort(dependencies, StringComparer.Ordinal);
            var returned = Math.Min(dependencies.Length, maxReferences);
            var builder = new StringBuilder();
            builder.Append("{\"total\":").Append(dependencies.Length).Append(",\"returned\":").Append(returned).Append(",\"truncated\":").Append(JsonUtil.Bool(dependencies.Length > returned)).Append(",\"items\":[");
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"path\":").Append(JsonUtil.String(dependencies[index]));
                builder.Append(",\"guid\":").Append(JsonUtil.String(AssetDatabase.AssetPathToGUID(dependencies[index]))).Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static string PrefabPlannedChangeJson(string operation, string objectPath, string parentPath, string name, string componentType, bool hasComponentIndex, int componentIndex, string propertyName, string variantPath, string argsJson)
        {
            var builder = new StringBuilder();
            builder.Append("{\"operation\":").Append(JsonUtil.String(operation));
            builder.Append(",\"object_path\":").Append(JsonUtil.String(objectPath));
            builder.Append(",\"parent_path\":").Append(JsonUtil.String(parentPath));
            builder.Append(",\"name\":").Append(JsonUtil.String(name));
            builder.Append(",\"component_type\":").Append(JsonUtil.String(componentType));
            if (hasComponentIndex)
            {
                builder.Append(",\"component_index\":").Append(componentIndex);
            }

            builder.Append(",\"property_name\":").Append(JsonUtil.String(propertyName));
            builder.Append(",\"value_carriers\":").Append(ValueCarriersJson(argsJson));
            builder.Append(",\"variant_path\":").Append(JsonUtil.String(variantPath));
            builder.Append(",\"note\":\"preview only; no prefab contents are saved or modified\"}");
            return builder.ToString();
        }

        static string ValueCarriersJson(string argsJson)
        {
            var carriers = new List<string>();
            if (HasJsonProperty(argsJson, "bool_value")) carriers.Add("bool_value");
            if (HasJsonProperty(argsJson, "number_value")) carriers.Add("number_value");
            if (HasJsonProperty(argsJson, "string_value")) carriers.Add("string_value");
            if (HasJsonProperty(argsJson, "color_r") || HasJsonProperty(argsJson, "color_g") || HasJsonProperty(argsJson, "color_b") || HasJsonProperty(argsJson, "color_a")) carriers.Add("color_rgba");
            return StringArrayJson(carriers);
        }

        static string PrefabWriteRiskWarningsJson(bool exists, bool targetFound, string plannedOperation, GameObject root, PrefabYamlIntegrityResult yamlIntegrity, bool assetLoadable)
        {
            var warnings = new List<string>();
            if (!exists && !yamlIntegrity.FileExists) warnings.Add("prefab_not_found");
            if (yamlIntegrity.HasIssues) warnings.Add("prefab_yaml_integrity_issues_detected");
            if (!yamlIntegrity.AnalysisComplete) warnings.Add("prefab_yaml_integrity_incomplete");
            if (yamlIntegrity.FileExists && !assetLoadable) warnings.Add("prefab_asset_not_loadable");
            if (exists && !targetFound) warnings.Add("object_not_found");
            if (IsDestructivePrefabOperation(plannedOperation)) warnings.Add("destructive_operation_planned");
            if (root != null && MissingScriptCount(root) > 0) warnings.Add("missing_scripts_detected");
            return StringArrayJson(warnings);
        }

        static string PrefabPreviewWarningsJson(string operation, bool exists, GameObject root, GameObject target, GameObject parent, string objectPath, string parentPath, string name, string componentType, string propertyName, string variantPath, PrefabYamlIntegrityResult yamlIntegrity, bool assetLoadable)
        {
            var warnings = new List<string>();
            if (!exists && !yamlIntegrity.FileExists) warnings.Add("prefab_not_found");
            if (yamlIntegrity.HasIssues) warnings.Add("prefab_yaml_integrity_issues_detected");
            if (!yamlIntegrity.AnalysisComplete) warnings.Add("prefab_yaml_integrity_incomplete");
            if (yamlIntegrity.FileExists && !assetLoadable) warnings.Add("prefab_asset_not_loadable");
            if (assetLoadable && !string.IsNullOrEmpty(objectPath) && target == null) warnings.Add("object_not_found");
            if (assetLoadable && !string.IsNullOrEmpty(parentPath) && parent == null) warnings.Add("parent_not_found");
            if (string.Equals(operation, "object.create", StringComparison.Ordinal) && string.IsNullOrEmpty(name)) warnings.Add("name_required_for_future_create");
            if ((string.Equals(operation, "component.add", StringComparison.Ordinal) || string.Equals(operation, "component.remove", StringComparison.Ordinal)) && string.IsNullOrEmpty(componentType)) warnings.Add("component_type_or_index_required_for_future_component_write");
            if (string.Equals(operation, "component.property.set", StringComparison.Ordinal) && string.IsNullOrEmpty(propertyName)) warnings.Add("property_name_required_for_future_property_write");
            if (string.Equals(operation, "variant.create", StringComparison.Ordinal) && string.IsNullOrEmpty(variantPath)) warnings.Add("variant_path_required_for_future_variant_create");
            if (root != null && MissingScriptCount(root) > 0) warnings.Add("missing_scripts_detected");
            return StringArrayJson(warnings);
        }

        static int MissingScriptCount(GameObject root)
        {
            if (root == null)
            {
                return 0;
            }

            var total = 0;
            CountMissingScripts(root, ref total);
            return total;
        }

        static void CountMissingScripts(GameObject gameObject, ref int total)
        {
            var components = gameObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                if (components[index] == null)
                {
                    total++;
                }
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                CountMissingScripts(gameObject.transform.GetChild(index).gameObject, ref total);
            }
        }

        static string ChildrenRiskJson(GameObject target, int maxChildren)
        {
            if (target == null)
            {
                return "{\"total\":0,\"returned\":0,\"truncated\":false,\"items\":[]}";
            }

            var total = target.transform.childCount;
            var returned = Math.Min(total, maxChildren);
            var builder = new StringBuilder();
            builder.Append("{\"total\":").Append(total).Append(",\"returned\":").Append(returned).Append(",\"truncated\":").Append(JsonUtil.Bool(total > returned)).Append(",\"items\":[");
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var child = target.transform.GetChild(index).gameObject;
                builder.Append("{\"name\":").Append(JsonUtil.String(child.name)).Append(",\"path\":").Append(JsonUtil.String(ObjectPath(child))).Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static string ComponentsRiskJson(GameObject target, int maxComponents)
        {
            if (target == null)
            {
                return "{\"total\":0,\"returned\":0,\"truncated\":false,\"items\":[]}";
            }

            var components = target.GetComponents<Component>();
            var returned = Math.Min(components.Length, maxComponents);
            var builder = new StringBuilder();
            builder.Append("{\"total\":").Append(components.Length).Append(",\"returned\":").Append(returned).Append(",\"truncated\":").Append(JsonUtil.Bool(components.Length > returned)).Append(",\"items\":[");
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(ComponentJson(components[index], index));
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static string MaterialsRiskJson(GameObject target, int maxMaterials)
        {
            if (target == null)
            {
                return "{\"total\":0,\"returned\":0,\"truncated\":false,\"items\":[]}";
            }

            var builder = new StringBuilder();
            var total = 0;
            var returned = 0;
            builder.Append("{\"items\":[");
            var components = target.GetComponents<Component>();
            for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                var renderer = components[componentIndex] as Renderer;
                if (renderer == null)
                {
                    continue;
                }

                var materials = renderer.sharedMaterials;
                if (materials == null)
                {
                    continue;
                }

                for (var slot = 0; slot < materials.Length; slot++)
                {
                    total++;
                    if (returned >= maxMaterials)
                    {
                        continue;
                    }

                    if (returned > 0)
                    {
                        builder.Append(',');
                    }

                    var material = materials[slot];
                    builder.Append("{\"component_index\":").Append(componentIndex)
                        .Append(",\"slot\":").Append(slot)
                        .Append(",\"material\":").Append(material == null ? "null" : MaterialJson(material, AssetDatabase.GetAssetPath(material)))
                        .Append('}');
                    returned++;
                }
            }

            builder.Append("],\"total\":").Append(total).Append(",\"returned\":").Append(returned).Append(",\"truncated\":").Append(JsonUtil.Bool(total > returned)).Append('}');
            return builder.ToString();
        }

        static string WriteRiskWarningsJson(bool sceneLoaded, bool sceneDirty, bool found, string plannedOperation, GameObject target)
        {
            var warnings = new List<string>();
            if (!sceneLoaded)
            {
                warnings.Add("scene_not_loaded");
            }
            else if (!found)
            {
                warnings.Add("object_not_found");
            }

            if (sceneDirty)
            {
                warnings.Add("scene_has_unsaved_changes");
            }

            if (IsDestructivePlannedOperation(plannedOperation))
            {
                warnings.Add("destructive_operation_planned");
            }

            if (target != null && target.transform.childCount > 0 && string.Equals(plannedOperation, "delete", StringComparison.Ordinal))
            {
                warnings.Add("delete_will_include_children");
            }

            return StringArrayJson(warnings);
        }

        static string PrefabStatus(GameObject target)
        {
            if (target == null)
            {
                return "unknown";
            }

            if (PrefabUtility.IsPartOfPrefabInstance(target))
            {
                return PrefabUtility.GetPrefabInstanceStatus(target).ToString();
            }

            return PrefabUtility.IsPartOfPrefabAsset(target) ? "asset" : "none";
        }

        static string StringArrayJson(List<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(values[index]));
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string StringArrayJson(string[] values)
        {
            return StringArrayJson(new List<string>(values ?? Array.Empty<string>()));
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
            if (string.IsNullOrEmpty(actual))
            {
                return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when dry_run is false", PrefabPreconditionDetailsJson(prefabPath, operation, preconditionHash));
            }

            if (!string.Equals(actual, preconditionHash, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match current prefab asset state or requested operation", PrefabPreconditionDetailsJson(prefabPath, operation, preconditionHash));
            }

            return null;
        }

        static string PrefabPreconditionDetailsJson(string prefabPath, string operation, string preconditionHash)
        {
            return "{\"required_precondition\":\"precondition_hash\""
                + ",\"prefab_path\":" + JsonUtil.String(prefabPath)
                + ",\"operation\":" + JsonUtil.String(operation)
                + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash)
                + ",\"next_valid_actions\":[\"prefab.edit.preview\",\"prefab.write_risk_report\",\"checkpoint.create\",\"retry_with_precondition_hash\"]}";
        }

        static string PrefabWriteContractJson(string prefabPath, GameObject root, bool dryRun, bool changed, bool savedPrefab, string operation, string preconditionHash, string[] nextValidActions, List<string> extraWarnings = null)
        {
            var warnings = extraWarnings == null ? new List<string>() : new List<string>(extraWarnings);
            AddPrefabWriteWarnings(warnings, root, dryRun, changed, savedPrefab);
            return ",\"saved_prefab\":" + JsonUtil.Bool(savedPrefab)
                + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash)
                + ",\"warnings\":" + StringArrayJson(warnings)
                + ",\"next_valid_actions\":" + StringArrayJson(nextValidActions);
        }

        static void AddPrefabWriteWarnings(List<string> warnings, GameObject root, bool dryRun, bool changed, bool savedPrefab)
        {
            if (dryRun)
            {
                warnings.Add("dry_run_no_prefab_mutation");
            }
            else if (savedPrefab)
            {
                warnings.Add("prefab_saved");
            }
            else if (!changed)
            {
                warnings.Add("no_prefab_change_applied");
            }

            if (root != null && MissingScriptCount(root) > 0)
            {
                warnings.Add("missing_scripts_detected");
            }
        }

        static string PrefabBaseAssetPath(GameObject prefabAsset)
        {
            if (prefabAsset == null)
            {
                return string.Empty;
            }

            var source = PrefabUtility.GetCorrespondingObjectFromSource(prefabAsset);
            return source == null ? string.Empty : AssetDatabase.GetAssetPath(source);
        }

        static string ComponentValuePreconditionDetailsJson(string scenePath, string objectPath, string propertyName, Component component, int componentIndex, string currentJson)
        {
            return "{\"required_precondition\":\"precondition_value_json\""
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"component_selector\":" + ComponentSelectorJson(component, componentIndex)
                + ",\"property_name\":" + JsonUtil.String(propertyName)
                + ",\"current_value\":" + currentJson
                + ",\"precondition_value_json\":" + JsonUtil.String(currentJson)
                + ",\"next_valid_actions\":[\"component.property.set_dry_run_true\",\"checkpoint.create\",\"retry_with_precondition_value_json\"]}";
        }

        static string SceneWriteContractJson(Scene scene, bool dryRun, bool changed, string[] nextValidActions, GameObject target = null)
        {
            var warnings = new List<string>();
            AddSceneWriteWarnings(warnings, scene, dryRun, changed, target);

            return ",\"scene_dirty\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"scene_saved\":false"
                + ",\"warnings\":" + StringArrayJson(warnings)
                + ",\"next_valid_actions\":" + StringArrayJson(nextValidActions);
        }

        static void AddSceneWriteWarnings(List<string> warnings, Scene scene, bool dryRun, bool changed, GameObject target = null)
        {
            if (dryRun)
            {
                warnings.Add("dry_run_no_scene_mutation");
            }
            else if (changed)
            {
                warnings.Add("scene_dirty_not_saved");
            }
            else
            {
                warnings.Add("no_scene_change_applied");
            }

            if (scene.IsValid() && scene.isDirty)
            {
                warnings.Add("scene_has_unsaved_changes");
            }

            if (target != null && PrefabUtility.IsPartOfPrefabInstance(target))
            {
                warnings.Add("target_is_prefab_instance");
            }
        }

        static string BoundedString(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, Math.Max(0, maxLength)) + "...";
        }

        static string Sha256Prefixed(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                return "sha256:" + HexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
            }
        }

        static string HexString(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        static string RuntimeObjectIdsJson(GameObject gameObject)
        {
#if UNITY_6000_4_OR_NEWER
            var entityId = UnityEngine.EntityId.ToULong(gameObject.GetEntityId());
            var legacyInstanceId = entityId <= int.MaxValue ? (int)entityId : 0;
            return ",\"instance_id\":" + legacyInstanceId
                + ",\"entity_id\":" + JsonUtil.String(entityId.ToString(CultureInfo.InvariantCulture));
#else
            return ",\"instance_id\":" + gameObject.GetInstanceID();
#endif
        }

        static string ComponentJson(Component component, int componentIndex)
        {
            if (component == null)
            {
                return "{\"index\":" + componentIndex + ",\"type\":\"MissingScript\",\"missing\":true}";
            }

            var type = component.GetType();
            var builder = new StringBuilder();
            builder.Append("{\"index\":").Append(componentIndex);
            builder.Append(",\"type\":").Append(JsonUtil.String(type.Name));
            builder.Append(",\"full_type\":").Append(JsonUtil.String(type.FullName ?? type.Name));
            builder.Append(",\"assembly\":").Append(JsonUtil.String(type.Assembly.GetName().Name));
            var enabledProperty = type.GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (enabledProperty != null && enabledProperty.PropertyType == typeof(bool) && enabledProperty.CanRead)
            {
                builder.Append(",\"enabled\":").Append(JsonUtil.Bool((bool)enabledProperty.GetValue(component, null)));
            }

            builder.Append('}');
            return builder.ToString();
        }

        static string ComponentSelectorJson(Component component, int componentIndex)
        {
            var type = component.GetType();
            return "{\"component_index\":" + componentIndex
                + ",\"type\":" + JsonUtil.String(type.Name)
                + ",\"full_type_name\":" + JsonUtil.String(type.FullName ?? type.Name)
                + ",\"assembly_name\":" + JsonUtil.String(type.Assembly.GetName().Name) + "}";
        }

        static int ComponentIndex(GameObject gameObject, Component component)
        {
            var components = gameObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                if (components[index] == component)
                {
                    return index;
                }
            }

            return -1;
        }

        static string RendererJson(Renderer renderer, int componentIndex)
        {
            var type = renderer.GetType();
            var materials = renderer.sharedMaterials;
            return "{\"component_index\":" + componentIndex
                + ",\"type\":" + JsonUtil.String(type.Name)
                + ",\"full_type\":" + JsonUtil.String(type.FullName ?? type.Name)
                + ",\"material_slots\":" + (materials == null ? 0 : materials.Length) + "}";
        }

        static string MaterialJson(Material material, string materialPath)
        {
            var path = string.IsNullOrEmpty(materialPath) ? AssetDatabase.GetAssetPath(material) : materialPath;
            var guid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
            var json = "{\"path\":" + JsonUtil.String(path)
                + ",\"name\":" + JsonUtil.String(material.name)
                + ",\"shader\":" + JsonUtil.String(material.shader == null ? string.Empty : material.shader.name);
            if (!string.IsNullOrEmpty(guid))
            {
                json += ",\"guid\":" + JsonUtil.String(guid);
            }

            return json + "}";
        }

        static string TransformJson(Transform transform)
        {
            return "{\"position\":" + VectorJson(transform.position)
                + ",\"local_position\":" + VectorJson(transform.localPosition)
                + ",\"rotation_euler\":" + VectorJson(transform.eulerAngles)
                + ",\"local_rotation_euler\":" + VectorJson(transform.localEulerAngles)
                + ",\"local_scale\":" + VectorJson(transform.localScale) + "}";
        }

        static string VectorJson(Vector3 vector)
        {
            return "{\"x\":" + FloatJson(vector.x)
                + ",\"y\":" + FloatJson(vector.y)
                + ",\"z\":" + FloatJson(vector.z) + "}";
        }

        static string Vector4Json(Vector4 vector)
        {
            return "{\"x\":" + FloatJson(vector.x)
                + ",\"y\":" + FloatJson(vector.y)
                + ",\"z\":" + FloatJson(vector.z)
                + ",\"w\":" + FloatJson(vector.w) + "}";
        }

        static string ColorJson(Color color)
        {
            return "{\"r\":" + FloatJson(color.r)
                + ",\"g\":" + FloatJson(color.g)
                + ",\"b\":" + FloatJson(color.b)
                + ",\"a\":" + FloatJson(color.a) + "}";
        }

        static string TextureReferenceJson(Texture texture)
        {
            return "{\"asset_path\":" + JsonUtil.String(texture == null ? string.Empty : AssetDatabase.GetAssetPath(texture))
                + ",\"name\":" + JsonUtil.String(texture == null ? string.Empty : texture.name)
                + ",\"type\":" + JsonUtil.String(texture == null ? string.Empty : texture.GetType().FullName) + "}";
        }

        static string ObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "/";
            }

            var builder = new StringBuilder();
            var current = gameObject.transform;
            while (current != null)
            {
                builder.Insert(0, "/" + current.name);
                current = current.parent;
            }

            return builder.Length == 0 ? "/" : builder.ToString();
        }

        static string AbsolutePath(string assetPath)
        {
            return Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
        }

        static bool HasJsonProperty(string json, string property)
        {
            return FindPropertyColon(json, property) >= 0;
        }

        static int FindPropertyColon(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property))
            {
                return -1;
            }

            var pattern = "\"" + property + "\"";
            var propertyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return -1;
            }

            return json.IndexOf(':', propertyIndex + pattern.Length);
        }

        static int IndexOfNextNonWhitespace(string value, int start)
        {
            for (var index = start; index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? JsonUtil.String(value.ToString(CultureInfo.InvariantCulture)) : value.ToString(CultureInfo.InvariantCulture);
        }

        struct PrefabUguiRectRequest
        {
            public bool HasAnchorMin;
            public bool HasAnchorMax;
            public bool HasPivot;
            public bool HasAnchoredPosition;
            public bool HasSizeDelta;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
        }

        struct PrefabUguiTextRequest
        {
            public bool HasText;
            public bool HasFontSize;
            public bool HasColor;
            public bool HasRaycastTarget;
            public string Text;
            public float FontSize;
            public Color Color;
            public bool RaycastTarget;
            public bool HasAnyField { get { return HasText || HasFontSize || HasColor || HasRaycastTarget; } }
        }

        sealed class PrefabUguiTextState
        {
            public string Text = string.Empty;
            public float FontSize;
            public Color Color = Color.white;
            public bool RaycastTarget;

            public PrefabUguiTextState Clone()
            {
                return new PrefabUguiTextState { Text = Text, FontSize = FontSize, Color = Color, RaycastTarget = RaycastTarget };
            }
        }
    }
}
