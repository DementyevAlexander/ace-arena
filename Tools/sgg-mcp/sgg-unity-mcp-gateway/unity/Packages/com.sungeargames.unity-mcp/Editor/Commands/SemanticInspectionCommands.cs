using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class SemanticInspectionCommands
    {
        const string FeatureFlag = "semantic_scene_inspection";
        const string SceneActiveSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string SceneBuildSettingsSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string SceneLoadedSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"additionalProperties\":false}";
        const string ComponentTypesSearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"include_obsolete\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string ComponentTypeInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"type_name\":{\"type\":\"string\",\"maxLength\":128},\"full_type_name\":{\"type\":\"string\",\"maxLength\":256},\"assembly_name\":{\"type\":\"string\",\"maxLength\":128},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50}},\"additionalProperties\":false}";
        const string SceneActiveHierarchySchemaJson = "{\"type\":\"object\",\"properties\":{\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"include_components\":{\"type\":\"boolean\"},\"path_prefix\":{\"type\":\"string\"}},\"additionalProperties\":false}";
        const string SceneHierarchySchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"include_components\":{\"type\":\"boolean\"},\"path_prefix\":{\"type\":\"string\"}},\"required\":[\"scene_path\"],\"additionalProperties\":false}";
        const string SceneVisibleObjectsSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"include_components\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string BuiltinAssetsSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_type\":{\"type\":\"string\",\"enum\":[\"PrimitiveMesh\",\"DefaultMaterial\",\"Cube\",\"Sphere\",\"Capsule\",\"Cylinder\",\"Plane\",\"Quad\"]}},\"required\":[\"asset_type\"],\"additionalProperties\":false}";
        const string GameObjectBoundsSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\",\"minLength\":1},\"object_path\":{\"type\":\"string\",\"minLength\":1},\"include_inactive\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabHierarchySchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\"},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"include_components\":{\"type\":\"boolean\"},\"path_prefix\":{\"type\":\"string\"}},\"required\":[\"prefab_path\"],\"additionalProperties\":false}";
        const string SceneSearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"scope\":{\"type\":\"string\",\"enum\":[\"scene_path\",\"active\",\"loaded\"]},\"query\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\",\"maxLength\":128},\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"path_exact\":{\"type\":\"string\",\"maxLength\":512},\"layer\":{\"type\":\"string\",\"maxLength\":64},\"active_state\":{\"type\":\"string\",\"enum\":[\"any\",\"active\",\"inactive\"]},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"include_components\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string SceneInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\",\"minLength\":1},\"object_path\":{\"type\":\"string\",\"minLength\":1},\"include_components\":{\"type\":\"boolean\"}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"include_components\":{\"type\":\"boolean\"}},\"required\":[\"prefab_path\",\"object_path\"],\"additionalProperties\":false}";
        const string SceneComponentSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"max_properties\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string PrefabComponentSchemaJson = "{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"max_properties\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"prefab_path\",\"object_path\"],\"additionalProperties\":false}";
        const string AssetComponentsSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"include_inactive\":{\"type\":\"boolean\"},\"max_objects\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_components\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string TreeOutputLimitJson = "{\"max_depth\":16,\"max_nodes\":1000}";
        const string ListOutputLimitJson = "{\"max_nodes\":1000}";
        const string SceneBuildSettingsOutputLimitJson = "{\"max_items\":200}";
        const string SceneLoadedOutputLimitJson = "{\"max_results\":100}";
        const string ComponentTypesOutputLimitJson = "{\"max_results\":200}";
        const string ComponentTypeInspectOutputLimitJson = "{\"max_results\":50}";
        const string SceneVisibleObjectsOutputLimitJson = "{\"max_results\":200}";
        const string BuiltinAssetsOutputLimitJson = "{\"max_assets\":6}";
        const string GameObjectBoundsOutputLimitJson = "{\"max_objects\":1}";
        const string ObjectOutputLimitJson = "{\"max_objects\":1}";
        const string ComponentOutputLimitJson = "{\"max_components\":1,\"max_properties\":200}";
        const string AssetComponentsOutputLimitJson = "{\"max_objects\":200,\"max_components\":1000}";

        [McpCommand(
            "scene.active",
            Title = "Active scene",
            Description = "Read active Unity scene status",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SceneActiveSchemaJson,
            Tags = new string[] { "scene", "status", "unity-ai-assistant" },
            Capability = "scene_status",
            ResultShape = "status")]
        public static CommandExecutionResult SceneActive(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(ActiveSceneJson());
        }

        [McpCommand(
            "scene.build_settings",
            Title = "Build settings scenes",
            Description = "Read Unity scenes listed in Build Settings",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SceneBuildSettingsSchemaJson,
            Tags = new string[] { "scene", "status", "unity-ai-assistant" },
            Capability = "scene_status",
            ResultShape = "bounded_list",
            OutputLimitJson = SceneBuildSettingsOutputLimitJson)]
        public static CommandExecutionResult SceneBuildSettings(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(BuildSettingsScenesJson());
        }

        [McpCommand(
            "scene.loaded",
            Title = "Loaded scenes",
            Description = "List currently loaded Unity scenes",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SceneLoadedSchemaJson,
            Tags = new string[] { "scene", "status" },
            Capability = "scene_status",
            ResultShape = "bounded_list",
            OutputLimitJson = SceneLoadedOutputLimitJson)]
        public static CommandExecutionResult SceneLoaded(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(LoadedScenesJson(context));
        }

        [McpCommand(
            "scene.hierarchy.active",
            Title = "Active scene hierarchy",
            Description = "Read a bounded semantic hierarchy for the active Unity scene",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SceneActiveHierarchySchemaJson,
            Tags = new string[] { "scene", "semantic-inspection", "unity-ai-assistant" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_tree",
            OutputLimitJson = TreeOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneActiveHierarchy(McpCommandContext context)
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Error("no_active_scene", "No valid loaded active scene is available");
            }

            return CommandExecutionResult.Ok(ActiveHierarchyJson(scene, context));
        }

        [McpCommand(
            "component.types.search",
            Title = "Search component types",
            Description = "Search loaded non-abstract Unity Component types",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ComponentTypesSearchSchemaJson,
            Tags = new string[] { "component", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = ComponentTypesOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ComponentTypesSearch(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(ComponentTypesSearchJson(context));
        }

        [McpCommand(
            "component.type.inspect",
            Title = "Inspect component type",
            Description = "Inspect loaded Unity Component type metadata for safe property workflows",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ComponentTypeInspectSchemaJson,
            Tags = new string[] { "component", "semantic-inspection", "safe-write-preflight" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = ComponentTypeInspectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ComponentTypeInspect(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(ComponentTypeInspectJson(context));
        }

        [McpCommand(
            "scene.visible_objects",
            Title = "Scene visible objects",
            Description = "Read bounded Scene view visible GameObjects from the active Scene view camera",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SceneVisibleObjectsSchemaJson,
            Tags = new string[] { "scene", "camera", "semantic-inspection", "unity-ai-assistant" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = SceneVisibleObjectsOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneVisibleObjects(McpCommandContext context)
        {
            var sceneView = ActiveSceneView();
            if (sceneView == null || sceneView.camera == null)
            {
                return CommandExecutionResult.Error("unity_busy", "No Scene view camera is available");
            }

            return CommandExecutionResult.Ok(VisibleObjectsJson(sceneView.camera, context));
        }

        [McpCommand(
            "scene.hierarchy",
            Title = "Scene hierarchy",
            Description = "Read a bounded semantic hierarchy for a Unity scene",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SceneHierarchySchemaJson,
            Tags = new string[] { "scene", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_tree",
            OutputLimitJson = TreeOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneHierarchy(McpCommandContext context)
        {
            var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            return WithScene(scenePath, (scene, openedForInspection) => CommandExecutionResult.Ok(SceneHierarchyJson(scenePath, scene, context, openedForInspection)));
        }

        [McpCommand(
            "scene.search",
            Title = "Scene search",
            Description = "Search GameObjects in a Unity scene by name/path substring",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SceneSearchSchemaJson,
            Tags = new string[] { "scene", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = ListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneSearch(McpCommandContext context)
        {
            var optionsResult = BuildSceneSearchOptions(context, out var options);
            if (optionsResult != null)
            {
                return optionsResult;
            }

            if (string.Equals(options.Scope, "scene_path", StringComparison.Ordinal))
            {
                var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
                if (pathResult != null)
                {
                    return pathResult;
                }

                options.ScenePath = scenePath;
                return WithScene(scenePath, (scene, openedForInspection) => CommandExecutionResult.Ok(SearchJson(options, new[] { scene }, openedForInspection)));
            }

            if (string.Equals(options.Scope, "active", StringComparison.Ordinal))
            {
                var scene = SceneManager.GetActiveScene();
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return CommandExecutionResult.Error("no_active_scene", "No valid loaded active scene is available");
                }

                return CommandExecutionResult.Ok(SearchJson(options, new[] { scene }, false));
            }

            return CommandExecutionResult.Ok(SearchJson(options, LoadedScenes(), false));
        }

        [McpCommand(
            "gameobject.builtin_assets",
            Title = "Built-in GameObject assets",
            Description = "Read Unity built-in primitive meshes or default material references",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = BuiltinAssetsSchemaJson,
            Tags = new string[] { "gameobject", "asset", "semantic-inspection", "unity-ai-assistant" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = BuiltinAssetsOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectBuiltinAssets(McpCommandContext context)
        {
            var assetType = JsonUtil.ExtractString(context.ArgsJson, "asset_type");
            if (string.IsNullOrEmpty(assetType))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument asset_type is required");
            }

            return BuiltinAssetsResult(assetType.Trim());
        }

        [McpCommand(
            "gameobject.bounds",
            Title = "GameObject bounds",
            Description = "Read renderer world-space bounds for one scene GameObject",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = GameObjectBoundsSchemaJson,
            Tags = new string[] { "gameobject", "bounds", "semantic-inspection", "unity-ai-assistant" },
            Capability = "semantic_scene_inspection",
            ResultShape = "object_bounds",
            OutputLimitJson = GameObjectBoundsOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameObjectBounds(McpCommandContext context)
        {
            var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "Argument object_path is required", "object_path", JsonUtil.HasProperty(context.ArgsJson, "object_path") ? "string_too_short" : "missing_required_field");
            }

            return WithScene(scenePath, (scene, openedForInspection) => GameObjectBoundsResult(scene.GetRootGameObjects(), scenePath, objectPath, context, openedForInspection));
        }

        [McpCommand(
            "scene.inspect_object",
            Title = "Inspect scene object",
            Description = "Read focused details for one scene object path",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SceneInspectSchemaJson,
            Tags = new string[] { "scene", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneInspectObject(McpCommandContext context)
        {
            var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "Argument object_path is required", "object_path", JsonUtil.HasProperty(context.ArgsJson, "object_path") ? "string_too_short" : "missing_required_field");
            }

            return WithScene(scenePath, (scene, openedForInspection) => InspectObjectResult(scene.GetRootGameObjects(), "scene_path", scenePath, objectPath, context, openedForInspection));
        }

        [McpCommand(
            "scene.inspect_component",
            Title = "Inspect scene component",
            Description = "Read focused serialized details for one scene object component",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SceneComponentSchemaJson,
            Tags = new string[] { "scene", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneInspectComponent(McpCommandContext context)
        {
            var pathResult = RequireSceneAssetPath(context.ArgsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            return WithScene(scenePath, (scene, openedForInspection) => InspectComponentResult(scene.GetRootGameObjects(), "scene_path", scenePath, objectPath, context, openedForInspection));
        }

        [McpCommand(
            "prefab.hierarchy",
            Title = "Prefab hierarchy",
            Description = "Read a bounded semantic hierarchy for a Unity prefab",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = PrefabHierarchySchemaJson,
            Tags = new string[] { "prefab", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_tree",
            OutputLimitJson = TreeOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabHierarchy(McpCommandContext context)
        {
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            return WithPrefab(prefabPath, root => CommandExecutionResult.Ok(HierarchyJson("prefab_path", prefabPath, new[] { root }, context)));
        }

        [McpCommand(
            "prefab.inspect_object",
            Title = "Inspect prefab object",
            Description = "Read focused details for one prefab object path",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = PrefabInspectSchemaJson,
            Tags = new string[] { "prefab", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "object_details",
            OutputLimitJson = ObjectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabInspectObject(McpCommandContext context)
        {
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            return WithPrefab(prefabPath, root => InspectObjectResult(new[] { root }, "prefab_path", prefabPath, objectPath, context));
        }

        [McpCommand(
            "prefab.inspect_component",
            Title = "Inspect prefab component",
            Description = "Read focused serialized details for one prefab object component",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = PrefabComponentSchemaJson,
            Tags = new string[] { "prefab", "semantic-inspection" },
            Capability = "semantic_scene_inspection",
            ResultShape = "object_details",
            OutputLimitJson = ComponentOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PrefabInspectComponent(McpCommandContext context)
        {
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            return WithPrefab(prefabPath, root => InspectComponentResult(new[] { root }, "prefab_path", prefabPath, objectPath, context));
        }

        [McpCommand(
            "asset.components.list",
            Title = "List asset components",
            Description = "Read bounded component metadata from one Unity prefab asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = AssetComponentsSchemaJson,
            Tags = new string[] { "asset", "prefab", "component", "semantic-inspection", "unity-ai-assistant" },
            Capability = "semantic_scene_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = AssetComponentsOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetComponentsList(McpCommandContext context)
        {
            var pathResult = RequirePrefabAssetPath(context.ArgsJson, "asset_path", out var prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            return WithPrefab(prefabPath, root => AssetComponentsResult(prefabPath, root, context));
        }

        static CommandExecutionResult WithScene(string scenePath, Func<Scene, bool, CommandExecutionResult> action)
        {
            var scene = FindLoadedScene(scenePath);
            var openedForInspection = !scene.IsValid();
            if (openedForInspection)
            {
                scene = EditorSceneManager.OpenScene(scenePath, OpenSceneMode.Additive);
            }

            try
            {
                return action(scene, openedForInspection);
            }
            finally
            {
                if (openedForInspection && scene.IsValid())
                {
                    EditorSceneManager.CloseScene(scene, true);
                }
            }
        }

        static CommandExecutionResult WithPrefab(string prefabPath, Func<GameObject, CommandExecutionResult> action)
        {
            var root = PrefabUtility.LoadPrefabContents(prefabPath);
            try
            {
                return action(root);
            }
            finally
            {
                PrefabUtility.UnloadPrefabContents(root);
            }
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

        static Scene[] LoadedScenes()
        {
            var scenes = new List<Scene>();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded)
                {
                    scenes.Add(scene);
                }
            }

            return scenes.ToArray();
        }

        static SceneView ActiveSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null && SceneView.sceneViews.Count > 0)
            {
                sceneView = SceneView.sceneViews[0] as SceneView;
            }

            return sceneView;
        }

        static string LoadedScenesJson(McpCommandContext context)
        {
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 100);
            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;
            var activeScene = SceneManager.GetActiveScene();

            builder.Append("{\"scenes\":[");
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"scene_path\":").Append(JsonUtil.String(scene.path));
                builder.Append(",\"scene_name\":").Append(JsonUtil.String(scene.name));
                builder.Append(",\"is_active\":").Append(JsonUtil.Bool(scene.handle == activeScene.handle));
                builder.Append(",\"is_dirty\":").Append(JsonUtil.Bool(scene.isDirty));
                builder.Append(",\"has_unsaved_changes\":").Append(JsonUtil.Bool(scene.isDirty));
                builder.Append(",\"requires_save\":").Append(JsonUtil.Bool(scene.isDirty));
                builder.Append(",\"is_loaded\":").Append(JsonUtil.Bool(scene.isLoaded));
                builder.Append(",\"root_count\":").Append(scene.isLoaded ? scene.rootCount : 0);
                builder.Append(",\"build_index\":").Append(scene.buildIndex);
                builder.Append('}');
                count++;
            }

            builder.Append("],\"truncated\":").Append(JsonUtil.Bool(truncated)).Append('}');
            return builder.ToString();
        }

        static string BuildSettingsScenesJson()
        {
            const int maxItems = 200;
            var scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
            var builder = new StringBuilder();
            var returned = Math.Min(scenes.Length, maxItems);
            var enabledBuildIndex = 0;

            builder.Append("{\"scenes\":[");
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var scene = scenes[index];
                builder.Append("{\"scene_path\":").Append(JsonUtil.String(scene.path));
                builder.Append(",\"guid\":").Append(JsonUtil.String(scene.guid.ToString()));
                builder.Append(",\"enabled\":").Append(JsonUtil.Bool(scene.enabled));
                builder.Append(",\"build_settings_index\":").Append(index);
                builder.Append(",\"enabled_build_index\":");
                if (scene.enabled)
                {
                    builder.Append(enabledBuildIndex);
                    enabledBuildIndex++;
                }
                else
                {
                    builder.Append("null");
                }

                builder.Append('}');
            }

            builder.Append("],\"count\":").Append(scenes.Length);
            builder.Append(",\"returned\":").Append(returned);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(scenes.Length > returned));
            builder.Append(",\"compatible_with\":\"Unity.ManageScene GetBuildSettings\"}");
            return builder.ToString();
        }

        static string ActiveSceneJson()
        {
            var scene = SceneManager.GetActiveScene();
            if (!scene.IsValid())
            {
                return "{\"has_active_scene\":false"
                    + ",\"is_valid\":false"
                    + ",\"scene_count\":" + SceneManager.sceneCount
                    + ",\"compatible_with\":\"Unity.ManageScene GetActive\"}";
            }

            var identityUnknown = IsActiveSceneIdentityUnknown(scene);
            return "{\"has_active_scene\":true"
                + ",\"scene_path\":" + JsonUtil.String(scene.path)
                + ",\"scene_name\":" + JsonUtil.String(scene.name)
                + ",\"identity_status\":" + JsonUtil.String(identityUnknown ? "unnamed_temp_identity_unknown" : "active_scene_identity_known")
                + ",\"identity_unknown\":" + JsonUtil.Bool(identityUnknown)
                + ",\"identity_confidence\":" + JsonUtil.String(identityUnknown ? "low" : "high")
                + ",\"is_active\":true"
                + ",\"is_valid\":true"
                + ",\"is_loaded\":" + JsonUtil.Bool(scene.isLoaded)
                + ",\"is_dirty\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"has_unsaved_changes\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"requires_save\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"root_count\":" + (scene.isLoaded ? scene.rootCount : 0)
                + ",\"build_index\":" + scene.buildIndex
                + ",\"scene_count\":" + SceneManager.sceneCount
                + ",\"warnings\":" + JsonStringArray(ActiveSceneIdentityWarnings(identityUnknown))
                + ",\"next_valid_actions\":" + JsonStringArray(ActiveSceneIdentityActions(identityUnknown))
                + ",\"compatible_with\":\"Unity.ManageScene GetActive\"}";
        }

        static string ComponentTypesSearchJson(McpCommandContext context)
        {
            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 200);
            var includeObsolete = JsonUtil.ExtractBool(context.ArgsJson, "include_obsolete", false);
            var types = ComponentTypes(query, includeObsolete);
            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;

            builder.Append("{\"query\":").Append(JsonUtil.String(query)).Append(",\"types\":[");
            foreach (var type in types)
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

                builder.Append("{\"type\":").Append(JsonUtil.String(type.Name));
                builder.Append(",\"full_type\":").Append(JsonUtil.String(type.FullName ?? type.Name));
                builder.Append(",\"namespace\":").Append(JsonUtil.String(type.Namespace ?? string.Empty));
                builder.Append(",\"assembly\":").Append(JsonUtil.String(type.Assembly.GetName().Name));
                builder.Append(",\"is_obsolete\":").Append(JsonUtil.Bool(IsObsolete(type)));
                builder.Append('}');
                count++;
            }

            builder.Append("],\"truncated\":").Append(JsonUtil.Bool(truncated)).Append('}');
            return builder.ToString();
        }

        static string ComponentTypeInspectJson(McpCommandContext context)
        {
            var typeName = JsonUtil.ExtractString(context.ArgsJson, "type_name");
            var fullTypeName = JsonUtil.ExtractString(context.ArgsJson, "full_type_name");
            var assemblyName = JsonUtil.ExtractString(context.ArgsJson, "assembly_name");
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 20), 1, 50);
            var matches = ComponentTypeInspectMatches(typeName, fullTypeName, assemblyName);
            var count = 0;
            var truncated = false;
            var builder = new StringBuilder();

            builder.Append("{\"selector\":{");
            builder.Append("\"type_name\":").Append(JsonUtil.String(typeName));
            builder.Append(",\"full_type_name\":").Append(JsonUtil.String(fullTypeName));
            builder.Append(",\"assembly_name\":").Append(JsonUtil.String(assemblyName));
            builder.Append("},\"ambiguous\":").Append(JsonUtil.Bool(matches.Count > 1));
            builder.Append(",\"requires_exact_selector\":").Append(JsonUtil.Bool(matches.Count > 1));
            builder.Append(",\"candidates\":[");
            foreach (var type in matches)
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

                AppendComponentTypeCandidate(builder, type);
                count++;
            }

            builder.Append("],\"truncated\":").Append(JsonUtil.Bool(truncated));
            builder.Append(",\"property_schema_command\":\"component.property_schema\"}");
            return builder.ToString();
        }

        static List<Type> ComponentTypeInspectMatches(string typeName, string fullTypeName, string assemblyName)
        {
            var matches = new List<Type>();
            foreach (var type in ComponentTypes(null, true))
            {
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

                if (string.IsNullOrEmpty(typeName))
                {
                    matches.Add(type);
                }
                else
                {
                    if (string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(type.FullName ?? type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add(type);
                    }
                }
            }

            return matches;
        }

        static void AppendComponentTypeCandidate(StringBuilder builder, Type type)
        {
            builder.Append("{\"name\":").Append(JsonUtil.String(type.Name));
            builder.Append(",\"full_type_name\":").Append(JsonUtil.String(type.FullName ?? type.Name));
            builder.Append(",\"namespace\":").Append(JsonUtil.String(type.Namespace ?? string.Empty));
            builder.Append(",\"assembly_name\":").Append(JsonUtil.String(type.Assembly.GetName().Name));
            builder.Append(",\"is_mono_behaviour\":").Append(JsonUtil.Bool(typeof(MonoBehaviour).IsAssignableFrom(type)));
            builder.Append(",\"is_behaviour\":").Append(JsonUtil.Bool(typeof(Behaviour).IsAssignableFrom(type)));
            builder.Append(",\"is_renderer\":").Append(JsonUtil.Bool(typeof(Renderer).IsAssignableFrom(type)));
            builder.Append(",\"is_collider\":").Append(JsonUtil.Bool(typeof(Collider).IsAssignableFrom(type)));
            builder.Append(",\"is_light\":").Append(JsonUtil.Bool(typeof(Light).IsAssignableFrom(type)));
            builder.Append(",\"is_camera\":").Append(JsonUtil.Bool(typeof(Camera).IsAssignableFrom(type)));
            builder.Append(",\"is_rigidbody\":").Append(JsonUtil.Bool(typeof(Rigidbody).IsAssignableFrom(type)));
            builder.Append(",\"property_schema_available\":").Append(JsonUtil.Bool(ComponentPropertySchemaAvailable(type)));
            builder.Append('}');
        }

        static bool ComponentPropertySchemaAvailable(Type type)
        {
            return !typeof(MonoBehaviour).IsAssignableFrom(type)
                && (typeof(Light).IsAssignableFrom(type)
                || typeof(Camera).IsAssignableFrom(type)
                || typeof(Collider).IsAssignableFrom(type)
                || typeof(Renderer).IsAssignableFrom(type)
                || typeof(Rigidbody).IsAssignableFrom(type));
        }

        static string VisibleObjectsJson(Camera camera, McpCommandContext context)
        {
            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 20), 1, 200);
            var includeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", true);
            var visibleObjects = VisibleSceneObjects(camera, query);
            visibleObjects.Sort(CompareVisibleSceneObjects);

            var returned = Math.Max(0, Math.Min(maxResults, visibleObjects.Count - startIndex));
            var nextStartIndex = startIndex + returned < visibleObjects.Count ? startIndex + returned : -1;
            var builder = new StringBuilder();
            for (var offset = 0; offset < returned; offset++)
            {
                if (offset > 0)
                {
                    builder.Append(',');
                }

                AppendVisibleSceneObject(builder, visibleObjects[startIndex + offset], includeComponents);
            }

            return "{\"camera\":" + JsonUtil.String(camera.name)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"total_visible\":" + visibleObjects.Count
                + ",\"returned\":" + returned
                + ",\"next_start_index\":" + (nextStartIndex >= 0 ? nextStartIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"truncated\":" + JsonUtil.Bool(nextStartIndex >= 0)
                + ",\"objects\":[" + builder + "]"
                + ",\"compatible_with\":\"Unity.Camera.GetVisibleObjects\"}";
        }

        static List<VisibleSceneObject> VisibleSceneObjects(Camera camera, string query)
        {
            var result = new List<VisibleSceneObject>();
            var seen = new HashSet<string>();
            var planes = GeometryUtility.CalculateFrustumPlanes(camera);
            var renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            foreach (var renderer in renderers)
            {
                if (renderer == null || renderer.gameObject == null || !renderer.enabled || !IsSceneObject(renderer.gameObject) || !renderer.gameObject.activeInHierarchy)
                {
                    continue;
                }

                var gameObject = renderer.gameObject;
                var path = ObjectPath(gameObject);
                if (!string.IsNullOrEmpty(query) && gameObject.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (!RendererVisible(camera, planes, renderer))
                {
                    continue;
                }

                var objectIdentity = RuntimeObjectIdentity(gameObject);
                if (!seen.Add(objectIdentity))
                {
                    continue;
                }

                result.Add(new VisibleSceneObject
                {
                    GameObject = gameObject,
                    Path = path,
                    Distance = Vector3.Distance(gameObject.transform.position, camera.transform.position),
                    Viewport = camera.WorldToViewportPoint(gameObject.transform.position)
                });
            }

            return result;
        }

        static string RuntimeObjectIdentity(UnityEngine.Object unityObject)
        {
#if UNITY_6000_4_OR_NEWER
            return UnityEngine.EntityId.ToULong(unityObject.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return unityObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        static int CompareVisibleSceneObjects(VisibleSceneObject left, VisibleSceneObject right)
        {
            var byDistance = left.Distance.CompareTo(right.Distance);
            return byDistance != 0 ? byDistance : string.Compare(left.Path, right.Path, StringComparison.Ordinal);
        }

        static bool RendererVisible(Camera camera, Plane[] planes, Renderer renderer)
        {
            var bounds = renderer.bounds;
            if (!GeometryUtility.TestPlanesAABB(planes, bounds))
            {
                return false;
            }

            if (PointInViewport(camera, bounds.center))
            {
                return true;
            }

            var min = bounds.min;
            var max = bounds.max;
            return PointInViewport(camera, new Vector3(min.x, min.y, min.z))
                || PointInViewport(camera, new Vector3(min.x, min.y, max.z))
                || PointInViewport(camera, new Vector3(min.x, max.y, min.z))
                || PointInViewport(camera, new Vector3(min.x, max.y, max.z))
                || PointInViewport(camera, new Vector3(max.x, min.y, min.z))
                || PointInViewport(camera, new Vector3(max.x, min.y, max.z))
                || PointInViewport(camera, new Vector3(max.x, max.y, min.z))
                || PointInViewport(camera, new Vector3(max.x, max.y, max.z));
        }

        static bool PointInViewport(Camera camera, Vector3 worldPosition)
        {
            var viewport = camera.WorldToViewportPoint(worldPosition);
            return viewport.z >= 0f && viewport.x >= 0f && viewport.x <= 1f && viewport.y >= 0f && viewport.y <= 1f;
        }

        static void AppendVisibleSceneObject(StringBuilder builder, VisibleSceneObject item, bool includeComponents)
        {
            var gameObject = item.GameObject;
            builder.Append("{\"path\":").Append(JsonUtil.String(item.Path));
            builder.Append(",\"name\":").Append(JsonUtil.String(gameObject.name));
            builder.Append(",\"scene_name\":").Append(JsonUtil.String(gameObject.scene.name));
            builder.Append(",\"scene_path\":").Append(JsonUtil.String(gameObject.scene.path));
            builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(gameObject.activeInHierarchy));
            AppendRuntimeObjectIds(builder, gameObject);
            builder.Append(",\"distance\":").Append(RoundedFloat(item.Distance));
            AppendVector(builder, "viewport", item.Viewport);
            if (includeComponents)
            {
                AppendComponents(builder, gameObject);
            }

            builder.Append('}');
        }

        static List<Type> ComponentTypes(string query, bool includeObsolete)
        {
            var result = new List<Type>();
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || type.IsAbstract || !typeof(Component).IsAssignableFrom(type))
                    {
                        continue;
                    }

                    if (!includeObsolete && IsObsolete(type))
                    {
                        continue;
                    }

                    if (!MatchesTypeQuery(type, query))
                    {
                        continue;
                    }

                    result.Add(type);
                }
            }

            result.Sort((left, right) =>
            {
                var byType = string.Compare(left.FullName ?? left.Name, right.FullName ?? right.Name, StringComparison.Ordinal);
                return byType != 0 ? byType : string.Compare(left.Assembly.GetName().Name, right.Assembly.GetName().Name, StringComparison.Ordinal);
            });
            return result;
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

        static bool MatchesTypeQuery(Type type, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            return type.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (type.FullName ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (type.Namespace ?? string.Empty).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || type.Assembly.GetName().Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsObsolete(Type type)
        {
            return type.GetCustomAttribute<ObsoleteAttribute>() != null;
        }

        static string HierarchyJson(string pathField, string assetPath, GameObject[] roots, McpCommandContext context)
        {
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 3), 0, 16);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 200), 1, 1000);
            var includeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", false);
            var pathPrefix = NormalizeSearchPath(JsonUtil.ExtractString(context.ArgsJson, "path_prefix"));
            var nodes = new StringBuilder();
            var count = 0;
            var componentCount = 0;
            var truncated = false;

            foreach (var root in roots)
            {
                AppendHierarchy(root, "/" + root.name, 0, maxDepth, maxNodes, includeComponents, pathPrefix, nodes, ref count, ref componentCount, ref truncated);
                if (truncated)
                {
                    break;
                }
            }

            return "{\"" + pathField + "\":" + JsonUtil.String(assetPath)
                + ",\"source\":" + JsonUtil.String(pathField)
                + ",\"selector\":" + HierarchySelectorJson(pathField, pathField, assetPath, pathPrefix, maxDepth, maxNodes, includeComponents)
                + ",\"root_count\":" + roots.Length
                + ",\"node_count\":" + count
                + ",\"returned_node_count\":" + count
                + ",\"component_count\":" + componentCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"warnings\":" + JsonStringArray(HierarchyWarnings(truncated, !string.IsNullOrEmpty(pathPrefix) && count == 0, false))
                + ",\"next_valid_actions\":" + JsonStringArray(HierarchyActions(pathField))
                + ",\"nodes\":[" + nodes + "]}";
        }

        static string SceneHierarchyJson(string scenePath, Scene scene, McpCommandContext context, bool openedForInspection)
        {
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 3), 0, 16);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 200), 1, 1000);
            var includeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", false);
            var pathPrefix = NormalizeSearchPath(JsonUtil.ExtractString(context.ArgsJson, "path_prefix"));
            var roots = scene.GetRootGameObjects();
            var nodes = new StringBuilder();
            var count = 0;
            var componentCount = 0;
            var truncated = false;

            foreach (var root in roots)
            {
                AppendHierarchy(root, "/" + root.name, 0, maxDepth, maxNodes, includeComponents, pathPrefix, nodes, ref count, ref componentCount, ref truncated);
                if (truncated)
                {
                    break;
                }
            }

            return "{\"source\":\"scene_path\""
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"scene_name\":" + JsonUtil.String(scene.name)
                + ",\"scene\":" + SceneMetadataJson(scene, openedForInspection)
                + ",\"selector\":" + HierarchySelectorJson("scene_path", "scene_path", scenePath, pathPrefix, maxDepth, maxNodes, includeComponents)
                + ",\"is_valid\":" + JsonUtil.Bool(scene.IsValid())
                + ",\"is_loaded\":" + JsonUtil.Bool(scene.isLoaded)
                + ",\"is_dirty\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"opened_for_inspection\":" + JsonUtil.Bool(openedForInspection)
                + ",\"build_index\":" + scene.buildIndex
                + ",\"root_count\":" + roots.Length
                + ",\"node_count\":" + count
                + ",\"returned_node_count\":" + count
                + ",\"component_count\":" + componentCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"warnings\":" + JsonStringArray(HierarchyWarnings(truncated, !string.IsNullOrEmpty(pathPrefix) && count == 0, openedForInspection))
                + ",\"next_valid_actions\":" + JsonStringArray(HierarchyActions("scene_path"))
                + ",\"nodes\":[" + nodes + "]"
                + ",\"compatible_with\":\"Unity.ManageScene GetHierarchy\"}";
        }

        static string ActiveHierarchyJson(Scene scene, McpCommandContext context)
        {
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 3), 0, 16);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 200), 1, 1000);
            var includeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", false);
            var pathPrefix = NormalizeSearchPath(JsonUtil.ExtractString(context.ArgsJson, "path_prefix"));
            var roots = scene.GetRootGameObjects();
            var nodes = new StringBuilder();
            var count = 0;
            var componentCount = 0;
            var truncated = false;

            foreach (var root in roots)
            {
                AppendHierarchy(root, "/" + root.name, 0, maxDepth, maxNodes, includeComponents, pathPrefix, nodes, ref count, ref componentCount, ref truncated);
                if (truncated)
                {
                    break;
                }
            }

            return "{\"source\":\"active_scene\""
                + ",\"scene_path\":" + (string.IsNullOrEmpty(scene.path) ? "null" : JsonUtil.String(scene.path))
                + ",\"scene_name\":" + JsonUtil.String(scene.name)
                + ",\"scene\":" + SceneMetadataJson(scene, false)
                + ",\"selector\":" + HierarchySelectorJson("active_scene", "scene_path", string.IsNullOrEmpty(scene.path) ? string.Empty : scene.path, pathPrefix, maxDepth, maxNodes, includeComponents)
                + ",\"is_valid\":" + JsonUtil.Bool(scene.IsValid())
                + ",\"is_loaded\":" + JsonUtil.Bool(scene.isLoaded)
                + ",\"is_dirty\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"build_index\":" + scene.buildIndex
                + ",\"root_count\":" + roots.Length
                + ",\"node_count\":" + count
                + ",\"returned_node_count\":" + count
                + ",\"component_count\":" + componentCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"warnings\":" + JsonStringArray(HierarchyWarnings(truncated, !string.IsNullOrEmpty(pathPrefix) && count == 0, false))
                + ",\"next_valid_actions\":" + JsonStringArray(HierarchyActions("scene_path"))
                + ",\"nodes\":[" + nodes + "]"
                + ",\"compatible_with\":\"Unity.ManageScene GetHierarchy active scene\"}";
        }

        static string HierarchySelectorJson(string source, string pathField, string assetPath, string pathPrefix, int maxDepth, int maxNodes, bool includeComponents)
        {
            return "{\"source\":" + JsonUtil.String(source)
                + ",\"" + pathField + "\":" + (string.IsNullOrEmpty(assetPath) ? "null" : JsonUtil.String(assetPath))
                + ",\"path_prefix\":" + (string.IsNullOrEmpty(pathPrefix) ? "null" : JsonUtil.String(pathPrefix))
                + ",\"max_depth\":" + maxDepth
                + ",\"max_nodes\":" + maxNodes
                + ",\"include_components\":" + JsonUtil.Bool(includeComponents)
                + "}";
        }

        static string SearchSelectorJson(SceneSearchOptions options)
        {
            return "{\"scope\":" + JsonUtil.String(options.Scope)
                + ",\"scene_path\":" + (string.IsNullOrEmpty(options.ScenePath) ? "null" : JsonUtil.String(options.ScenePath))
                + ",\"query\":" + (string.IsNullOrEmpty(options.Query) ? "null" : JsonUtil.String(options.Query))
                + ",\"component_type\":" + (string.IsNullOrEmpty(options.ComponentType) ? "null" : JsonUtil.String(options.ComponentType))
                + ",\"path_prefix\":" + (string.IsNullOrEmpty(options.PathPrefix) ? "null" : JsonUtil.String(options.PathPrefix))
                + ",\"path_exact\":" + (string.IsNullOrEmpty(options.PathExact) ? "null" : JsonUtil.String(options.PathExact))
                + ",\"layer\":" + (string.IsNullOrEmpty(options.Layer) ? "null" : JsonUtil.String(options.Layer))
                + ",\"active_state\":" + JsonUtil.String(options.ActiveState)
                + ",\"start_index\":" + options.StartIndex
                + ",\"max_results\":" + options.MaxResults
                + ",\"include_components\":" + JsonUtil.Bool(options.IncludeComponents)
                + "}";
        }

        static string ObjectSelectorJson(string pathField, string assetPath, string objectPath)
        {
            return "{\"" + pathField + "\":" + JsonUtil.String(assetPath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + "}";
        }

        static string ComponentSelectorJson(string pathField, string assetPath, string objectPath, string componentType, int componentIndex)
        {
            return "{\"" + pathField + "\":" + JsonUtil.String(assetPath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"component_type\":" + (string.IsNullOrEmpty(componentType) ? "null" : JsonUtil.String(componentType))
                + ",\"component_index\":" + (componentIndex >= 0 ? componentIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + "}";
        }

        static string SceneMetadataJson(Scene scene, bool openedForInspection)
        {
            return "{\"scene_path\":" + (string.IsNullOrEmpty(scene.path) ? "null" : JsonUtil.String(scene.path))
                + ",\"scene_name\":" + JsonUtil.String(scene.name)
                + ",\"is_valid\":" + JsonUtil.Bool(scene.IsValid())
                + ",\"is_loaded\":" + JsonUtil.Bool(scene.isLoaded)
                + ",\"is_dirty\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"has_unsaved_changes\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"requires_save\":" + JsonUtil.Bool(scene.isDirty)
                + ",\"root_count\":" + (scene.isLoaded ? scene.rootCount : 0)
                + ",\"build_index\":" + scene.buildIndex
                + ",\"opened_for_inspection\":" + JsonUtil.Bool(openedForInspection)
                + "}";
        }

        static string SceneMetadataListJson(Scene[] scenes, bool openedForInspection)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < scenes.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(SceneMetadataJson(scenes[index], openedForInspection && index == 0));
            }

            return builder.ToString();
        }

        static List<string> HierarchyWarnings(bool truncated, bool emptyPrefixMatch, bool openedForInspection)
        {
            var warnings = new List<string>();
            if (openedForInspection)
            {
                warnings.Add("Scene was opened additively for read-only inspection and closed without saving.");
            }

            if (truncated)
            {
                warnings.Add("Hierarchy output was truncated; rerun with a narrower path_prefix or higher max_nodes.");
            }

            if (emptyPrefixMatch)
            {
                warnings.Add("path_prefix did not match any returned object path.");
            }

            return warnings;
        }

        static bool IsActiveSceneIdentityUnknown(Scene scene)
        {
            return scene.IsValid()
                && scene.isLoaded
                && string.IsNullOrEmpty(scene.path)
                && string.IsNullOrEmpty(scene.name)
                && scene.buildIndex < 0;
        }

        static List<string> ActiveSceneIdentityWarnings(bool identityUnknown)
        {
            var warnings = new List<string>();
            if (identityUnknown)
            {
                warnings.Add("Active scene identity is unknown because scene_path and scene_name are empty and build_index is -1; do not treat active-scene absence as production scene evidence.");
            }

            return warnings;
        }

        static string[] ActiveSceneIdentityActions(bool identityUnknown)
        {
            return identityUnknown
                ? new[] { "scene.loaded", "scene.build_settings", "scene.open", "scene.search" }
                : new[] { "scene.search", "scene.hierarchy.active", "scene.loaded" };
        }

        static List<string> SearchWarnings(bool truncated, bool openedForInspection, bool noScenes, bool noMatches, bool activeSceneIdentityUnknown)
        {
            var warnings = new List<string>();
            if (openedForInspection)
            {
                warnings.Add("Scene was opened additively for read-only inspection and closed without saving.");
            }

            if (noScenes)
            {
                warnings.Add("No loaded scenes were available for the selected search scope.");
            }

            if (noMatches)
            {
                warnings.Add("No GameObjects matched the current scene search selector.");
                if (activeSceneIdentityUnknown)
                {
                    warnings.Add("Active scene identity is unknown because scene_path and scene_name are empty and build_index is -1; this no-match result is not proof of absence from the intended production scene.");
                }
            }

            if (truncated)
            {
                warnings.Add("Search output was truncated; rerun scene.search with next_start_index.");
            }

            return warnings;
        }

        static List<string> ObjectInspectWarnings(bool ambiguous, bool openedForInspection)
        {
            var warnings = new List<string>();
            if (openedForInspection)
            {
                warnings.Add("Scene was opened additively for read-only inspection and closed without saving.");
            }

            if (ambiguous)
            {
                warnings.Add("object_path matched multiple objects because duplicate names produce the same hierarchy path.");
            }

            return warnings;
        }

        static List<string> ComponentInspectWarnings(bool objectAmbiguous, bool componentAmbiguous, bool openedForInspection)
        {
            var warnings = ObjectInspectWarnings(objectAmbiguous, openedForInspection);
            if (componentAmbiguous)
            {
                warnings.Add("component_type matched multiple components on this object; pass component_index for exact follow-up actions.");
            }

            return warnings;
        }

        static string[] HierarchyActions(string pathField)
        {
            return string.Equals(pathField, "prefab_path", StringComparison.Ordinal)
                ? new[] { "prefab.inspect_object", "prefab.inspect_component", "asset.components.list" }
                : new[] { "scene.search", "scene.inspect_object", "scene.inspect_component", "gameobject.bounds" };
        }

        static string[] SearchActions(bool truncated, bool activeSceneIdentityUnknown)
        {
            if (activeSceneIdentityUnknown)
            {
                return new[] { "scene.active", "scene.loaded", "scene.build_settings", "scene.open", "scene.search" };
            }

            return truncated
                ? new[] { "scene.search", "scene.inspect_object", "scene.inspect_component", "gameobject.bounds" }
                : new[] { "scene.inspect_object", "scene.inspect_component", "gameobject.bounds" };
        }

        static string[] ObjectInspectActions(string pathField)
        {
            return string.Equals(pathField, "prefab_path", StringComparison.Ordinal)
                ? new[] { "prefab.inspect_component", "component.type.inspect" }
                : new[] { "scene.inspect_component", "gameobject.bounds", "component.type.inspect" };
        }

        static string[] ComponentInspectActions(string pathField)
        {
            return string.Equals(pathField, "prefab_path", StringComparison.Ordinal)
                ? new[] { "component.type.inspect", "component.property_schema", "prefab.inspect_object" }
                : new[] { "component.type.inspect", "component.property_schema", "scene.inspect_object" };
        }

        static string JsonStringArray(IList<string> values)
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

        static CommandExecutionResult BuildSceneSearchOptions(McpCommandContext context, out SceneSearchOptions options)
        {
            options = new SceneSearchOptions();
            var rawScenePath = JsonUtil.ExtractString(context.ArgsJson, "scene_path");
            options.Scope = JsonUtil.ExtractString(context.ArgsJson, "scope");
            if (string.IsNullOrEmpty(options.Scope))
            {
                options.Scope = string.IsNullOrEmpty(rawScenePath) ? "active" : "scene_path";
            }

            if (!string.Equals(options.Scope, "scene_path", StringComparison.Ordinal)
                && !string.Equals(options.Scope, "active", StringComparison.Ordinal)
                && !string.Equals(options.Scope, "loaded", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "scope must be scene_path, active, or loaded");
            }

            if (string.Equals(options.Scope, "scene_path", StringComparison.Ordinal) && string.IsNullOrEmpty(rawScenePath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument scene_path is required when scope is scene_path");
            }

            options.Query = JsonUtil.ExtractString(context.ArgsJson, "query");
            options.ComponentType = JsonUtil.ExtractString(context.ArgsJson, "component_type");
            if (string.IsNullOrEmpty(options.ComponentType) && options.Query.StartsWith("t:", StringComparison.OrdinalIgnoreCase) && options.Query.Length > 2)
            {
                options.ComponentType = options.Query.Substring(2);
                options.Query = string.Empty;
            }

            options.PathPrefix = NormalizeSearchPath(JsonUtil.ExtractString(context.ArgsJson, "path_prefix"));
            options.PathExact = NormalizeSearchPath(JsonUtil.ExtractString(context.ArgsJson, "path_exact"));
            options.Layer = JsonUtil.ExtractString(context.ArgsJson, "layer");
            options.ActiveState = JsonUtil.ExtractString(context.ArgsJson, "active_state");
            if (string.IsNullOrEmpty(options.ActiveState))
            {
                options.ActiveState = "any";
            }

            if (!string.Equals(options.ActiveState, "any", StringComparison.Ordinal)
                && !string.Equals(options.ActiveState, "active", StringComparison.Ordinal)
                && !string.Equals(options.ActiveState, "inactive", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "active_state must be any, active, or inactive");
            }

            options.StartIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            options.MaxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 100)), 1, 1000);
            options.IncludeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", false);

            if (!options.HasFilter)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query or another scene search filter is required");
            }

            return null;
        }

        static string SearchJson(SceneSearchOptions options, Scene[] scenes, bool openedForInspection)
        {
            var nodes = new StringBuilder();
            var returned = 0;
            var seenMatches = 0;
            var componentCount = 0;
            var truncated = false;

            foreach (var scene in scenes)
            {
                var roots = scene.GetRootGameObjects();
                foreach (var root in roots)
                {
                    AppendSearch(root, "/" + root.name, scene, options, nodes, ref returned, ref seenMatches, ref componentCount, ref truncated);
                    if (truncated)
                    {
                        break;
                    }
                }

                if (truncated)
                {
                    break;
                }
            }

            var activeSceneIdentityUnknown = string.Equals(options.Scope, "active", StringComparison.Ordinal)
                && scenes.Length == 1
                && IsActiveSceneIdentityUnknown(scenes[0]);
            var absenceProvable = returned == 0 && scenes.Length > 0 && !truncated && !activeSceneIdentityUnknown;

            return "{\"scope\":" + JsonUtil.String(options.Scope)
                + ",\"scene_path\":" + (string.IsNullOrEmpty(options.ScenePath) ? "null" : JsonUtil.String(options.ScenePath))
                + ",\"selector\":" + SearchSelectorJson(options)
                + ",\"query\":" + (string.IsNullOrEmpty(options.Query) ? "null" : JsonUtil.String(options.Query))
                + ",\"component_type\":" + (string.IsNullOrEmpty(options.ComponentType) ? "null" : JsonUtil.String(options.ComponentType))
                + ",\"path_prefix\":" + (string.IsNullOrEmpty(options.PathPrefix) ? "null" : JsonUtil.String(options.PathPrefix))
                + ",\"path_exact\":" + (string.IsNullOrEmpty(options.PathExact) ? "null" : JsonUtil.String(options.PathExact))
                + ",\"layer\":" + (string.IsNullOrEmpty(options.Layer) ? "null" : JsonUtil.String(options.Layer))
                + ",\"active_state\":" + JsonUtil.String(options.ActiveState)
                + ",\"start_index\":" + options.StartIndex
                + ",\"next_start_index\":" + (truncated ? (options.StartIndex + returned).ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"max_results\":" + options.MaxResults
                + ",\"active_scene_identity_unknown\":" + JsonUtil.Bool(activeSceneIdentityUnknown)
                + ",\"identity_confidence\":" + JsonUtil.String(activeSceneIdentityUnknown ? "low" : "high")
                + ",\"absence_provable\":" + JsonUtil.Bool(absenceProvable)
                + ",\"opened_for_inspection\":" + JsonUtil.Bool(openedForInspection)
                + ",\"searched_scenes\":" + scenes.Length
                + ",\"searched_scene_count\":" + scenes.Length
                + ",\"scenes\":[" + SceneMetadataListJson(scenes, openedForInspection) + "]"
                + ",\"matched_nodes\":" + returned
                + ",\"seen_match_count\":" + seenMatches
                + ",\"returned_node_count\":" + returned
                + ",\"component_count\":" + componentCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"warnings\":" + JsonStringArray(SearchWarnings(truncated, openedForInspection, scenes.Length == 0, returned == 0, activeSceneIdentityUnknown))
                + ",\"next_valid_actions\":" + JsonStringArray(SearchActions(truncated, activeSceneIdentityUnknown))
                + ",\"compatible_with\":\"Unity.FindSceneObjects\""
                + ",\"nodes\":[" + nodes + "]}";
        }

        static CommandExecutionResult InspectObjectResult(GameObject[] roots, string pathField, string assetPath, string objectPath, McpCommandContext context, bool openedForInspection = false)
        {
            var normalizedObjectPath = NormalizeObjectPath(objectPath);
            var target = FindByPath(roots, normalizedObjectPath);
            var matchedObjectCount = CountPathMatches(roots, normalizedObjectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Object path was not found", ObjectNotFoundDetailsJson(pathField, assetPath, normalizedObjectPath));
            }

            var includeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", true);
            var builder = new StringBuilder();
            builder.Append("{\"").Append(pathField).Append("\":").Append(JsonUtil.String(assetPath));
            builder.Append(",\"selector\":").Append(ObjectSelectorJson(pathField, assetPath, normalizedObjectPath));
            builder.Append(",\"matched_object_count\":").Append(matchedObjectCount);
            builder.Append(",\"ambiguous\":").Append(JsonUtil.Bool(matchedObjectCount > 1));
            builder.Append(",\"opened_for_inspection\":").Append(JsonUtil.Bool(openedForInspection));
            builder.Append(',');
            AppendGameObjectFields(builder, target, normalizedObjectPath, includeComponents);
            builder.Append(",\"warnings\":").Append(JsonStringArray(ObjectInspectWarnings(matchedObjectCount > 1, openedForInspection)));
            builder.Append(",\"next_valid_actions\":").Append(JsonStringArray(ObjectInspectActions(pathField)));
            builder.Append('}');
            return CommandExecutionResult.Ok(builder.ToString());
        }

        static CommandExecutionResult BuiltinAssetsResult(string assetType)
        {
            if (string.Equals(assetType, "DefaultMaterial", StringComparison.OrdinalIgnoreCase))
            {
                var material = Resources.GetBuiltinResource<Material>("Default-Material.mat");
                if (material == null)
                {
                    return CommandExecutionResult.Error("asset_not_found", "Default material was not found");
                }

                return CommandExecutionResult.Ok(BuiltinAssetsJson("DefaultMaterial", "Retrieved default material.", new[] { BuiltinAssetJson("DefaultMaterial", material) }));
            }

            if (string.Equals(assetType, "PrimitiveMesh", StringComparison.OrdinalIgnoreCase))
            {
                var assets = new List<string>();
                var primitiveNames = new[] { "Sphere", "Capsule", "Cylinder", "Cube", "Plane", "Quad" };
                for (var index = 0; index < primitiveNames.Length; index++)
                {
                    var meshResult = BuiltinPrimitiveMeshJson(primitiveNames[index], out var meshJson);
                    if (meshResult != null)
                    {
                        return meshResult;
                    }

                    assets.Add(meshJson);
                }

                return CommandExecutionResult.Ok(BuiltinAssetsJson("PrimitiveMesh", "Retrieved 6 primitive meshes.", assets.ToArray()));
            }

            var primitiveName = CanonicalPrimitiveName(assetType);
            if (!string.IsNullOrEmpty(primitiveName))
            {
                var meshResult = BuiltinPrimitiveMeshJson(primitiveName, out var meshJson);
                return meshResult ?? CommandExecutionResult.Ok(BuiltinAssetsJson(primitiveName, "Retrieved primitive mesh '" + primitiveName + "'.", new[] { meshJson }));
            }

            return CommandExecutionResult.Error("invalid_field_value", "asset_type must be PrimitiveMesh, DefaultMaterial, Cube, Sphere, Capsule, Cylinder, Plane, or Quad");
        }

        static CommandExecutionResult BuiltinPrimitiveMeshJson(string primitiveName, out string meshJson)
        {
            meshJson = string.Empty;
            var mesh = Resources.GetBuiltinResource<Mesh>(primitiveName + ".fbx");
            if (mesh == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Primitive mesh was not found: " + primitiveName);
            }

            meshJson = BuiltinAssetJson(primitiveName, mesh);
            return null;
        }

        static string BuiltinAssetsJson(string assetType, string message, string[] assets)
        {
            var builder = new StringBuilder();
            builder.Append("{\"asset_type\":").Append(JsonUtil.String(assetType));
            builder.Append(",\"message\":").Append(JsonUtil.String(message));
            builder.Append(",\"assets\":[");
            for (var index = 0; index < assets.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(assets[index]);
            }

            builder.Append("],\"compatible_with\":\"Unity.GameObject.GetBuiltinAssets\"}");
            return builder.ToString();
        }

        static string BuiltinAssetJson(string name, UnityEngine.Object asset)
        {
            var builder = new StringBuilder();
            builder.Append("{\"name\":").Append(JsonUtil.String(name));
            builder.Append(",\"type\":").Append(JsonUtil.String(asset.GetType().Name));
            AppendRuntimeObjectIds(builder, asset);
            builder.Append('}');
            return builder.ToString();
        }

        static string CanonicalPrimitiveName(string assetType)
        {
            if (string.Equals(assetType, "Sphere", StringComparison.OrdinalIgnoreCase)) return "Sphere";
            if (string.Equals(assetType, "Capsule", StringComparison.OrdinalIgnoreCase)) return "Capsule";
            if (string.Equals(assetType, "Cylinder", StringComparison.OrdinalIgnoreCase)) return "Cylinder";
            if (string.Equals(assetType, "Cube", StringComparison.OrdinalIgnoreCase)) return "Cube";
            if (string.Equals(assetType, "Plane", StringComparison.OrdinalIgnoreCase)) return "Plane";
            if (string.Equals(assetType, "Quad", StringComparison.OrdinalIgnoreCase)) return "Quad";
            return string.Empty;
        }

        static CommandExecutionResult GameObjectBoundsResult(GameObject[] roots, string scenePath, string objectPath, McpCommandContext context, bool openedForInspection)
        {
            var normalizedObjectPath = NormalizeObjectPath(objectPath);
            var target = FindByPath(roots, normalizedObjectPath);
            var matchedObjectCount = CountPathMatches(roots, normalizedObjectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Object path was not found", ObjectNotFoundDetailsJson("scene_path", scenePath, normalizedObjectPath));
            }

            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", false);
            if (!TryComputeRendererWorldBounds(target, includeInactive, out var bounds, out var rendererCount))
            {
                return CommandExecutionResult.Error("bounds_not_found", "Could not find renderer bounds for object_path", ObjectSelectorJson("scene_path", scenePath, normalizedObjectPath));
            }

            var builder = new StringBuilder();
            builder.Append("{\"scene_path\":").Append(JsonUtil.String(scenePath));
            builder.Append(",\"object_path\":").Append(JsonUtil.String(normalizedObjectPath));
            builder.Append(",\"selector\":").Append(ObjectSelectorJson("scene_path", scenePath, normalizedObjectPath));
            builder.Append(",\"name\":").Append(JsonUtil.String(target.name));
            builder.Append(",\"include_inactive\":").Append(JsonUtil.Bool(includeInactive));
            builder.Append(",\"matched_object_count\":").Append(matchedObjectCount);
            builder.Append(",\"ambiguous\":").Append(JsonUtil.Bool(matchedObjectCount > 1));
            builder.Append(",\"opened_for_inspection\":").Append(JsonUtil.Bool(openedForInspection));
            builder.Append(",\"renderer_count\":").Append(rendererCount);
            AppendVector(builder, "center", bounds.center);
            AppendVector(builder, "size", bounds.size);
            AppendVector(builder, "min", bounds.min);
            AppendVector(builder, "max", bounds.max);
            builder.Append(",\"warnings\":").Append(JsonStringArray(ObjectInspectWarnings(matchedObjectCount > 1, openedForInspection)));
            builder.Append(",\"next_valid_actions\":[\"scene.inspect_object\",\"scene.inspect_component\"]");
            builder.Append(",\"compatible_with\":\"Unity.GameObject.GetGameObjectBounds\"}");
            return CommandExecutionResult.Ok(builder.ToString());
        }

        static bool TryComputeRendererWorldBounds(GameObject target, bool includeInactive, out Bounds bounds, out int rendererCount)
        {
            bounds = default;
            rendererCount = 0;
            var renderers = target.GetComponentsInChildren<Renderer>(includeInactive);
            foreach (var renderer in renderers)
            {
                if (rendererCount == 0)
                {
                    bounds = renderer.bounds;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }

                rendererCount++;
            }

            return rendererCount > 0;
        }

        static void AppendVector(StringBuilder builder, string field, Vector3 value)
        {
            builder.Append(",\"").Append(field).Append("\":{");
            builder.Append("\"x\":").Append(RoundedFloat(value.x));
            builder.Append(",\"y\":").Append(RoundedFloat(value.y));
            builder.Append(",\"z\":").Append(RoundedFloat(value.z));
            builder.Append('}');
        }

        static string RoundedFloat(float value)
        {
            return Math.Round(value, 2).ToString(CultureInfo.InvariantCulture);
        }

        static CommandExecutionResult InspectComponentResult(GameObject[] roots, string pathField, string assetPath, string objectPath, McpCommandContext context, bool openedForInspection = false)
        {
            var normalizedObjectPath = NormalizeObjectPath(objectPath);
            var target = FindByPath(roots, normalizedObjectPath);
            var matchedObjectCount = CountPathMatches(roots, normalizedObjectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Object path was not found", ObjectNotFoundDetailsJson(pathField, assetPath, normalizedObjectPath));
            }

            var components = target.GetComponents<Component>();
            var componentIndex = JsonUtil.ExtractInt(context.ArgsJson, "component_index", -1);
            var componentType = JsonUtil.ExtractString(context.ArgsJson, "component_type");
            if (componentIndex < 0 && string.IsNullOrEmpty(componentType))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument component_type or component_index is required");
            }

            var selectedIndex = FindComponentIndex(components, componentIndex, componentType);
            if (selectedIndex < 0)
            {
                return CommandExecutionResult.Error("asset_not_found", "Component was not found", ComponentNotFoundDetailsJson(pathField, assetPath, normalizedObjectPath, componentType, componentIndex));
            }

            var matchedComponentCount = componentIndex >= 0 ? 1 : CountComponentMatches(components, componentType);
            var componentAmbiguous = componentIndex < 0 && matchedComponentCount > 1;
            var builder = new StringBuilder();
            builder.Append("{\"").Append(pathField).Append("\":").Append(JsonUtil.String(assetPath));
            builder.Append(",\"object_path\":").Append(JsonUtil.String(normalizedObjectPath));
            builder.Append(",\"selector\":").Append(ComponentSelectorJson(pathField, assetPath, normalizedObjectPath, componentType, componentIndex));
            builder.Append(",\"component_count\":").Append(components.Length);
            builder.Append(",\"matched_object_count\":").Append(matchedObjectCount);
            builder.Append(",\"object_ambiguous\":").Append(JsonUtil.Bool(matchedObjectCount > 1));
            builder.Append(",\"matched_component_count\":").Append(matchedComponentCount);
            builder.Append(",\"component_ambiguous\":").Append(JsonUtil.Bool(componentAmbiguous));
            builder.Append(",\"requires_exact_selector\":").Append(JsonUtil.Bool(componentAmbiguous));
            builder.Append(",\"opened_for_inspection\":").Append(JsonUtil.Bool(openedForInspection));
            AppendComponentDetails(builder, components[selectedIndex], selectedIndex, Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_properties", 80), 1, 200));
            builder.Append(",\"warnings\":").Append(JsonStringArray(ComponentInspectWarnings(matchedObjectCount > 1, componentAmbiguous, openedForInspection)));
            builder.Append(",\"next_valid_actions\":").Append(JsonStringArray(ComponentInspectActions(pathField)));
            builder.Append('}');
            return CommandExecutionResult.Ok(builder.ToString());
        }

        static CommandExecutionResult AssetComponentsResult(string assetPath, GameObject root, McpCommandContext context)
        {
            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", true);
            var maxObjects = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_objects", 50), 1, 200);
            var maxComponents = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_components", 200), 1, 1000);
            var objects = new StringBuilder();
            var objectCount = 0;
            var componentCount = 0;
            var truncated = false;

            if (!string.IsNullOrEmpty(objectPath))
            {
                var target = NormalizeObjectPath(objectPath) == "/" ? root : FindByPath(new[] { root }, objectPath);
                if (target == null)
                {
                    return CommandExecutionResult.Error("asset_not_found", "Object path was not found");
                }

                AppendComponentObject(objects, target, NormalizeObjectPath(objectPath) == "/" ? "/" + root.name : NormalizeObjectPath(objectPath), maxComponents, ref componentCount, ref truncated, false);
                objectCount = 1;
            }
            else
            {
                AppendComponentObjectsRecursive(objects, root, "/" + root.name, includeInactive, maxObjects, maxComponents, ref objectCount, ref componentCount, ref truncated);
            }

            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"object_path\":" + (string.IsNullOrEmpty(objectPath) ? "null" : JsonUtil.String(NormalizeObjectPath(objectPath)))
                + ",\"object_count\":" + objectCount
                + ",\"component_count\":" + componentCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"objects\":[" + objects + "]"
                + ",\"compatible_with\":\"Unity.ManageAsset GetComponents\"}");
        }

        static int FindComponentIndex(Component[] components, int componentIndex, string componentType)
        {
            if (componentIndex >= 0)
            {
                return componentIndex < components.Length ? componentIndex : -1;
            }

            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                var typeName = component == null ? "MissingScript" : component.GetType().Name;
                var fullName = component == null ? "MissingScript" : component.GetType().FullName;
                if (string.Equals(typeName, componentType, StringComparison.OrdinalIgnoreCase) || string.Equals(fullName, componentType, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        static int CountComponentMatches(Component[] components, string componentType)
        {
            var count = 0;
            for (var index = 0; index < components.Length; index++)
            {
                if (ComponentMatches(components[index], componentType))
                {
                    count++;
                }
            }

            return count;
        }

        static bool ComponentMatches(Component component, string componentType)
        {
            if (string.IsNullOrEmpty(componentType))
            {
                return false;
            }

            var typeName = component == null ? "MissingScript" : component.GetType().Name;
            var fullName = component == null ? "MissingScript" : component.GetType().FullName;
            return string.Equals(typeName, componentType, StringComparison.OrdinalIgnoreCase)
                || string.Equals(fullName, componentType, StringComparison.OrdinalIgnoreCase);
        }

        static string ComponentNotFoundDetailsJson(string pathField, string assetPath, string objectPath, string componentType, int componentIndex)
        {
            return "{\"selector\":" + ComponentSelectorJson(pathField, assetPath, objectPath, componentType, componentIndex)
                + ",\"next_valid_actions\":" + JsonStringArray(ObjectInspectActions(pathField))
                + "}";
        }

        static void AppendComponentDetails(StringBuilder builder, Component component, int componentIndex, int maxProperties)
        {
            builder.Append(",\"component\":{");
            builder.Append("\"index\":").Append(componentIndex);
            if (component == null)
            {
                builder.Append(",\"type\":\"MissingScript\",\"missing\":true,\"properties\":[]}");
                return;
            }

            builder.Append(",\"type\":").Append(JsonUtil.String(component.GetType().Name));
            builder.Append(",\"full_type\":").Append(JsonUtil.String(component.GetType().FullName));
            if (component is Behaviour behaviour)
            {
                builder.Append(",\"enabled\":").Append(JsonUtil.Bool(behaviour.enabled));
            }

            builder.Append(",\"properties\":[");
            var serialized = new SerializedObject(component);
            var property = serialized.GetIterator();
            var count = 0;
            var truncated = false;
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (count >= maxProperties)
                {
                    truncated = true;
                    break;
                }

                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"path\":").Append(JsonUtil.String(property.propertyPath));
                builder.Append(",\"type\":").Append(JsonUtil.String(property.propertyType.ToString()));
                builder.Append(",\"value\":").Append(SerializedPropertyValueJson(property));
                builder.Append('}');
                count++;
            }

            builder.Append(']');
            builder.Append(",\"properties_truncated\":").Append(JsonUtil.Bool(truncated));
            builder.Append('}');
        }

        internal static string SerializedPropertyValueJson(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Vector2:
                    var vector2 = property.vector2Value;
                    return "{\"x\":" + SerializedFloatJson(vector2.x) + ",\"y\":" + SerializedFloatJson(vector2.y) + "}";
                case SerializedPropertyType.Vector3:
                    var vector3 = property.vector3Value;
                    return "{\"x\":" + SerializedFloatJson(vector3.x) + ",\"y\":" + SerializedFloatJson(vector3.y) + ",\"z\":" + SerializedFloatJson(vector3.z) + "}";
                case SerializedPropertyType.Vector4:
                    var vector4 = property.vector4Value;
                    return "{\"x\":" + SerializedFloatJson(vector4.x) + ",\"y\":" + SerializedFloatJson(vector4.y) + ",\"z\":" + SerializedFloatJson(vector4.z) + ",\"w\":" + SerializedFloatJson(vector4.w) + "}";
                case SerializedPropertyType.Quaternion:
                    var quaternion = property.quaternionValue;
                    return "{\"x\":" + SerializedFloatJson(quaternion.x) + ",\"y\":" + SerializedFloatJson(quaternion.y) + ",\"z\":" + SerializedFloatJson(quaternion.z) + ",\"w\":" + SerializedFloatJson(quaternion.w) + "}";
                default:
                    return JsonUtil.String(SerializedValue(property));
            }
        }

        static string SerializedFloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        static string SerializedValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue.ToString();
                case SerializedPropertyType.Boolean: return property.boolValue ? "true" : "false";
                case SerializedPropertyType.Float: return property.floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.String: return property.stringValue ?? string.Empty;
                case SerializedPropertyType.ObjectReference: return property.objectReferenceValue == null ? string.Empty : property.objectReferenceValue.name;
                case SerializedPropertyType.Enum: return property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString();
                default: return property.hasVisibleChildren ? "" : property.displayName;
            }
        }

        static void AppendHierarchy(GameObject gameObject, string objectPath, int depth, int maxDepth, int maxNodes, bool includeComponents, string pathPrefix, StringBuilder nodes, ref int count, ref int componentCount, ref bool truncated)
        {
            if (truncated || depth > maxDepth)
            {
                return;
            }

            var includeNode = string.IsNullOrEmpty(pathPrefix) || objectPath.StartsWith(pathPrefix, StringComparison.OrdinalIgnoreCase);
            if (includeNode)
            {
                if (count >= maxNodes)
                {
                    truncated = true;
                    return;
                }

                AppendNode(nodes, gameObject, objectPath, includeComponents, count > 0);
                count++;
                componentCount += CountComponents(gameObject);
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                var child = gameObject.transform.GetChild(index).gameObject;
                AppendHierarchy(child, objectPath + "/" + child.name, depth + 1, maxDepth, maxNodes, includeComponents, pathPrefix, nodes, ref count, ref componentCount, ref truncated);
            }
        }

        static void AppendSearch(GameObject gameObject, string objectPath, Scene scene, SceneSearchOptions options, StringBuilder nodes, ref int returned, ref int seenMatches, ref int componentCount, ref bool truncated)
        {
            if (truncated)
            {
                return;
            }

            if (SceneSearchMatches(gameObject, objectPath, options))
            {
                if (seenMatches++ >= options.StartIndex)
                {
                    if (returned >= options.MaxResults)
                    {
                        truncated = true;
                        return;
                    }

                    AppendSearchNode(nodes, gameObject, objectPath, scene, options.IncludeComponents, returned > 0);
                    returned++;
                    componentCount += CountComponents(gameObject);
                }
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                var child = gameObject.transform.GetChild(index).gameObject;
                AppendSearch(child, objectPath + "/" + child.name, scene, options, nodes, ref returned, ref seenMatches, ref componentCount, ref truncated);
            }
        }

        static bool SceneSearchMatches(GameObject gameObject, string objectPath, SceneSearchOptions options)
        {
            if (!string.IsNullOrEmpty(options.Query)
                && gameObject.name.IndexOf(options.Query, StringComparison.OrdinalIgnoreCase) < 0
                && objectPath.IndexOf(options.Query, StringComparison.OrdinalIgnoreCase) < 0)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(options.PathPrefix) && !objectPath.StartsWith(options.PathPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(options.PathExact) && !string.Equals(objectPath, options.PathExact, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(options.ComponentType) && !HasComponentType(gameObject, options.ComponentType))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(options.Layer) && !LayerMatches(gameObject, options.Layer))
            {
                return false;
            }

            if (string.Equals(options.ActiveState, "active", StringComparison.Ordinal) && !gameObject.activeInHierarchy)
            {
                return false;
            }

            if (string.Equals(options.ActiveState, "inactive", StringComparison.Ordinal) && gameObject.activeInHierarchy)
            {
                return false;
            }

            return true;
        }

        static bool HasComponentType(GameObject gameObject, string componentType)
        {
            var components = gameObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (type.Name.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) >= 0
                    || (!string.IsNullOrEmpty(type.FullName) && type.FullName.IndexOf(componentType, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }
            }

            return false;
        }

        static bool LayerMatches(GameObject gameObject, string layer)
        {
            return string.Equals(gameObject.layer.ToString(CultureInfo.InvariantCulture), layer, StringComparison.OrdinalIgnoreCase)
                || string.Equals(LayerMask.LayerToName(gameObject.layer), layer, StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeSearchPath(string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                return string.Empty;
            }

            var normalized = rawPath.Replace('\\', '/').Trim();
            return normalized.StartsWith("/", StringComparison.Ordinal) ? normalized : "/" + normalized;
        }

        static void AppendSearchNode(StringBuilder builder, GameObject gameObject, string objectPath, Scene scene, bool includeComponents, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append('{');
            AppendGameObjectFields(builder, gameObject, objectPath, includeComponents);
            builder.Append(",\"scene_path\":").Append(string.IsNullOrEmpty(scene.path) ? "null" : JsonUtil.String(scene.path));
            builder.Append(",\"scene_name\":").Append(JsonUtil.String(scene.name));
            builder.Append(",\"layer\":").Append(gameObject.layer);
            builder.Append(",\"layer_name\":").Append(JsonUtil.String(LayerMask.LayerToName(gameObject.layer)));
            builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(gameObject.activeInHierarchy));
            builder.Append('}');
        }

        static void AppendNode(StringBuilder builder, GameObject gameObject, string objectPath, bool includeComponents, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append('{');
            AppendGameObjectFields(builder, gameObject, objectPath, includeComponents);
            builder.Append('}');
        }

        static void AppendGameObjectFields(StringBuilder builder, GameObject gameObject, string objectPath, bool includeComponents)
        {
            builder.Append("\"path\":").Append(JsonUtil.String(objectPath));
            builder.Append(",\"name\":").Append(JsonUtil.String(gameObject.name));
            builder.Append(",\"active\":").Append(JsonUtil.Bool(gameObject.activeSelf));
            builder.Append(",\"child_count\":").Append(gameObject.transform.childCount);
            builder.Append(",\"component_count\":").Append(CountComponents(gameObject));
            if (includeComponents)
            {
                AppendComponents(builder, gameObject);
            }
        }

        static void AppendComponents(StringBuilder builder, GameObject gameObject)
        {
            var components = gameObject.GetComponents<Component>();
            builder.Append(",\"components\":[");
            for (var index = 0; index < components.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendComponentSummary(builder, components[index], index);
            }

            builder.Append(']');
        }

        static int CountComponents(GameObject gameObject)
        {
            return gameObject.GetComponents<Component>().Length;
        }

        static void AppendRuntimeObjectIds(StringBuilder builder, UnityEngine.Object unityObject)
        {
#if UNITY_6000_4_OR_NEWER
            var entityId = UnityEngine.EntityId.ToULong(unityObject.GetEntityId());
            var legacyInstanceId = entityId <= int.MaxValue ? (int)entityId : 0;
            builder.Append(",\"instance_id\":").Append(legacyInstanceId);
            builder.Append(",\"entity_id\":").Append(JsonUtil.String(entityId.ToString(CultureInfo.InvariantCulture)));
#else
            builder.Append(",\"instance_id\":").Append(unityObject.GetInstanceID());
#endif
        }

        static bool IsSceneObject(GameObject gameObject)
        {
            return gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }

        static string ObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "/";
            }

            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            var builder = new StringBuilder();
            for (var index = 0; index < names.Count; index++)
            {
                builder.Append('/').Append(names[index]);
            }

            return builder.Length == 0 ? "/" : builder.ToString();
        }

        static void AppendComponentObjectsRecursive(StringBuilder builder, GameObject gameObject, string objectPath, bool includeInactive, int maxObjects, int maxComponents, ref int objectCount, ref int componentCount, ref bool truncated)
        {
            if (truncated)
            {
                return;
            }

            if (includeInactive || gameObject.activeSelf)
            {
                if (objectCount >= maxObjects)
                {
                    truncated = true;
                    return;
                }

                AppendComponentObject(builder, gameObject, objectPath, maxComponents, ref componentCount, ref truncated, objectCount > 0);
                objectCount++;
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                var child = gameObject.transform.GetChild(index).gameObject;
                AppendComponentObjectsRecursive(builder, child, objectPath + "/" + child.name, includeInactive, maxObjects, maxComponents, ref objectCount, ref componentCount, ref truncated);
            }
        }

        static void AppendComponentObject(StringBuilder builder, GameObject gameObject, string objectPath, int maxComponents, ref int componentCount, ref bool truncated, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            var components = gameObject.GetComponents<Component>();
            builder.Append("{\"path\":").Append(JsonUtil.String(objectPath));
            builder.Append(",\"name\":").Append(JsonUtil.String(gameObject.name));
            builder.Append(",\"active\":").Append(JsonUtil.Bool(gameObject.activeSelf));
            builder.Append(",\"components\":[");
            var written = 0;
            for (var index = 0; index < components.Length; index++)
            {
                if (componentCount >= maxComponents)
                {
                    truncated = true;
                    break;
                }

                if (written > 0)
                {
                    builder.Append(',');
                }

                AppendComponentSummary(builder, components[index], index);
                componentCount++;
                written++;
            }

            builder.Append("]}");
        }

        static void AppendComponentSummary(StringBuilder builder, Component component, int componentIndex)
        {
            builder.Append("{\"component_index\":").Append(componentIndex);
            if (component == null)
            {
                builder.Append(",\"type\":\"MissingScript\",\"full_type\":\"MissingScript\",\"is_missing_script\":true}");
                return;
            }

            builder.Append(",\"type\":").Append(JsonUtil.String(component.GetType().Name));
            builder.Append(",\"full_type\":").Append(JsonUtil.String(component.GetType().FullName));
            builder.Append(",\"is_missing_script\":false");
            if (component is Behaviour behaviour)
            {
                builder.Append(",\"enabled\":").Append(JsonUtil.Bool(behaviour.enabled));
            }

            builder.Append('}');
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

        static int CountPathMatches(GameObject[] roots, string objectPath)
        {
            var normalized = NormalizeObjectPath(objectPath);
            var count = 0;
            foreach (var root in roots)
            {
                CountPathMatchesRecursive(root, "/" + root.name, normalized, ref count);
            }

            return count;
        }

        static void CountPathMatchesRecursive(GameObject gameObject, string currentPath, string targetPath, ref int count)
        {
            if (string.Equals(currentPath, targetPath, StringComparison.Ordinal))
            {
                count++;
            }

            for (var index = 0; index < gameObject.transform.childCount; index++)
            {
                var child = gameObject.transform.GetChild(index).gameObject;
                CountPathMatchesRecursive(child, currentPath + "/" + child.name, targetPath, ref count);
            }
        }

        static string ObjectNotFoundDetailsJson(string pathField, string assetPath, string objectPath)
        {
            return "{\"selector\":" + ObjectSelectorJson(pathField, assetPath, objectPath)
                + ",\"next_valid_actions\":" + JsonStringArray(HierarchyActions(pathField))
                + "}";
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

        static CommandExecutionResult RequirePrefabAssetPath(string argsJson, out string prefabPath)
        {
            return RequirePrefabAssetPath(argsJson, "prefab_path", out prefabPath);
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

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        sealed class SceneSearchOptions
        {
            public string Scope = "active";
            public string ScenePath = string.Empty;
            public string Query = string.Empty;
            public string ComponentType = string.Empty;
            public string PathPrefix = string.Empty;
            public string PathExact = string.Empty;
            public string Layer = string.Empty;
            public string ActiveState = "any";
            public int StartIndex;
            public int MaxResults = 100;
            public bool IncludeComponents;

            public bool HasFilter
            {
                get
                {
                    return !string.IsNullOrEmpty(Query)
                        || !string.IsNullOrEmpty(ComponentType)
                        || !string.IsNullOrEmpty(PathPrefix)
                        || !string.IsNullOrEmpty(PathExact)
                        || !string.IsNullOrEmpty(Layer)
                        || !string.Equals(ActiveState, "any", StringComparison.Ordinal);
                }
            }
        }

        sealed class VisibleSceneObject
        {
            public GameObject GameObject;
            public string Path;
            public float Distance;
            public Vector3 Viewport;
        }
    }
}
