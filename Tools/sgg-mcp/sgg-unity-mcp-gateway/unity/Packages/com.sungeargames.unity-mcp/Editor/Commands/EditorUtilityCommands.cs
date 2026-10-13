using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class EditorUtilityCommands
    {
        const int DefaultSelectionLimit = 100;
        const int MaxSelectionLimit = 100;
        const int DefaultWindowLimit = 50;
        const int MaxWindowLimit = 100;
        const int DefaultMenuCatalogLimit = 100;
        const int MaxMenuCatalogLimit = 200;
        const int MaxMenuCatalogScan = 1000;
        const int MenuLookupBudgetMs = 750;
        const int MaxMenuPathLength = 128;
        const int MaxTagLength = 64;
        const int MaxLayerLength = 64;
        const string FeatureFlag = "editor_utilities";
        const string EmptyObjectSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string SelectionGetSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"additionalProperties\":false}";
        const string WindowsListSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"additionalProperties\":false}";
        const string WindowInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"type_name\":{\"type\":\"string\"},\"focused\":{\"type\":\"boolean\",\"enum\":[true]},\"window_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":99}},\"additionalProperties\":false}";
        const string WindowPositionSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"type_name\":{\"type\":\"string\"},\"focused\":{\"type\":\"boolean\",\"enum\":[true]},\"window_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":99},\"x\":{\"type\":\"integer\",\"minimum\":-65535,\"maximum\":65535},\"y\":{\"type\":\"integer\",\"minimum\":-65535,\"maximum\":65535},\"width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"x\",\"y\",\"width\",\"height\"],\"additionalProperties\":false}";
#if UNITY_6000_4_OR_NEWER
        const string SelectionSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"instance_id\":{\"type\":\"integer\"},\"entity_id\":{\"type\":\"string\"},\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"}},\"oneOf\":[{\"required\":[\"asset_path\"]},{\"required\":[\"instance_id\"]},{\"required\":[\"entity_id\"]},{\"required\":[\"scene_path\",\"object_path\"]}],\"additionalProperties\":false}";
#else
        const string SelectionSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"instance_id\":{\"type\":\"integer\"},\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"}},\"oneOf\":[{\"required\":[\"asset_path\"]},{\"required\":[\"instance_id\"]},{\"required\":[\"scene_path\",\"object_path\"]}],\"additionalProperties\":false}";
#endif
        const string ActiveToolSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"tool\":{\"type\":\"string\",\"enum\":[\"View\",\"Move\",\"Rotate\",\"Scale\",\"Rect\",\"Transform\"]},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"tool\"],\"additionalProperties\":false}";
        const string MenuListSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string MenuExistsSchemaJson = "{\"type\":\"object\",\"properties\":{\"menu_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128}},\"required\":[\"menu_path\"],\"additionalProperties\":false}";
        const string MenuExecuteSchemaJson = "{\"type\":\"object\",\"properties\":{\"menu_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"menu_path\"],\"additionalProperties\":false}";
        const string TagModifySchemaJson = "{\"type\":\"object\",\"properties\":{\"tag\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"tag\"],\"additionalProperties\":false}";
        const string LayerModifySchemaJson = "{\"type\":\"object\",\"properties\":{\"layer\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":64},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"layer\"],\"additionalProperties\":false}";
        static readonly string[] KnownMenuRoots =
        {
            "File",
            "Edit",
            "Assets",
            "GameObject",
            "Component",
            "Window",
            "Help",
            "SGG",
        };
        static readonly string[] KnownSafeMenuPaths =
        {
            "SGG/Unity MCP/MCP Window",
        };

        internal sealed class MenuExecutionObservation
        {
            internal bool Returned;
            internal bool ExceptionObserved;
            internal string ExceptionCondition = string.Empty;
            internal string ExceptionStackTrace = string.Empty;
        }

        [McpCommand(
            "editor.selection.get",
            Title = "Get editor selection",
            Description = "Read the current bounded Unity Editor selection",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SelectionGetSchemaJson,
            Tags = new string[] { "editor", "selection" },
            Capability = FeatureFlag,
            ResultShape = "selection",
            OutputLimitJson = "{\"max_objects\":100}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorSelectionGet(McpCommandContext context)
        {
            var maxResults = JsonUtil.ExtractInt(context.ArgsJson, "max_results", DefaultSelectionLimit);
            if (maxResults < 1 || maxResults > MaxSelectionLimit)
            {
                return CommandExecutionResult.Error("invalid_field_value", "max_results must be between 1 and 100");
            }

            var selectedObjects = Selection.objects ?? new UnityEngine.Object[0];
            var count = Math.Min(selectedObjects.Length, maxResults);
            var builder = new StringBuilder();
            builder.Append("{\"objects\":[");

            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendSelectedObject(builder, selectedObjects[index]);
            }

            builder.Append("],\"count\":").Append(selectedObjects.Length);
            builder.Append(",\"returned\":").Append(count);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(selectedObjects.Length > count));
            builder.Append('}');
            return CommandExecutionResult.Ok(builder.ToString());
        }

        [McpCommand(
            "editor.selection.set",
            Title = "Set editor selection",
            Description = "Set the Unity Editor selection by Unity-relative asset path, scene object path, or runtime id",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = SelectionSetSchemaJson,
            Tags = new string[] { "editor", "selection" },
            Capability = FeatureFlag,
            ResultShape = "selection",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorSelectionSet(McpCommandContext context)
        {
            var assetPath = JsonUtil.ExtractString(context.ArgsJson, "asset_path");
            var instanceId = JsonUtil.ExtractInt(context.ArgsJson, "instance_id", 0);
            var entityId = JsonUtil.ExtractString(context.ArgsJson, "entity_id").Trim();
            var scenePath = JsonUtil.ExtractString(context.ArgsJson, "scene_path");
            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            var hasAssetPath = !string.IsNullOrEmpty(assetPath);
            var hasInstanceId = instanceId != 0;
            var hasEntityId = !string.IsNullOrEmpty(entityId);
            var hasScenePath = !string.IsNullOrEmpty(scenePath);
            var hasObjectPath = !string.IsNullOrEmpty(objectPath);
            var hasSceneObjectPath = hasScenePath || hasObjectPath;
            var selectorCount = (hasAssetPath ? 1 : 0) + (hasInstanceId ? 1 : 0) + (hasEntityId ? 1 : 0) + (hasSceneObjectPath ? 1 : 0);
            if (selectorCount != 1)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide exactly one of asset_path, instance_id, entity_id, or scene_path with object_path");
            }

            if (hasScenePath != hasObjectPath)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "scene_path and object_path must be provided together");
            }

            UnityEngine.Object selectedObject;
            string selectedAssetPath;
            string selectedScenePath = null;
            string selectedObjectPath = null;
            if (hasAssetPath)
            {
                var pathValidation = TryValidateAssetPath(assetPath, out var validatedPath);
                if (pathValidation != null)
                {
                    return pathValidation;
                }

                selectedObject = AssetDatabase.LoadMainAssetAtPath(validatedPath);
                if (selectedObject == null)
                {
                    return CommandExecutionResult.Error("not_found", "asset_path does not resolve to an asset");
                }

                selectedAssetPath = validatedPath;
            }
            else if (hasSceneObjectPath)
            {
                var pathValidation = TryValidateSceneAssetPath(scenePath, out var validatedPath);
                if (pathValidation != null)
                {
                    return pathValidation;
                }

                var scene = FindLoadedScene(validatedPath);
                if (!scene.IsValid() || !scene.isLoaded)
                {
                    return CommandExecutionResult.Error("scene_not_loaded", "scene_path must identify an already loaded scene");
                }

                var target = FindByPath(scene.GetRootGameObjects(), objectPath);
                if (target == null)
                {
                    return CommandExecutionResult.Error("not_found", "object_path does not resolve in scene_path");
                }

                selectedObject = target;
                selectedAssetPath = string.Empty;
                selectedScenePath = validatedPath;
                selectedObjectPath = NormalizeObjectPath(objectPath);
            }
            else
            {
                var resolveResult = TryResolveObjectByRuntimeId(instanceId, entityId, out selectedObject);
                if (resolveResult != null)
                {
                    return resolveResult;
                }

                selectedAssetPath = AssetDatabase.GetAssetPath(selectedObject) ?? string.Empty;
            }

            Selection.objects = new[] { selectedObject };
            var builder = new StringBuilder();
            builder.Append("{\"selected\":");
            AppendSelectedObject(builder, selectedObject, selectedAssetPath, selectedScenePath, selectedObjectPath);
            builder.Append('}');
            return CommandExecutionResult.Ok(builder.ToString());
        }

        [McpCommand(
            "editor.prefab_stage.get",
            Title = "Get prefab stage",
            Description = "Read current Unity Prefab Mode stage status",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptyObjectSchemaJson,
            Tags = new string[] { "editor", "prefab", "unity-ai-assistant" },
            Capability = FeatureFlag,
            ResultShape = "prefab_stage",
            OutputLimitJson = "{\"max_objects\":1}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorPrefabStageGet(McpCommandContext context)
        {
            var stage = PrefabStageUtility.GetCurrentPrefabStage();
            if (stage == null)
            {
                return CommandExecutionResult.Ok("{\"is_open\":false"
                    + ",\"asset_path\":null"
                    + ",\"prefab_root_name\":null"
                    + ",\"mode\":null"
                    + ",\"is_dirty\":false"
                    + ",\"compatible_with\":\"Unity.ManageEditor GetPrefabStage\"}");
            }

            return CommandExecutionResult.Ok("{\"is_open\":true"
                + ",\"asset_path\":" + JsonUtil.String(stage.assetPath)
                + ",\"prefab_root_name\":" + JsonUtil.String(stage.prefabContentsRoot != null ? stage.prefabContentsRoot.name : null)
                + ",\"mode\":" + JsonUtil.String(stage.mode.ToString())
                + ",\"is_dirty\":" + JsonUtil.Bool(stage.scene.isDirty)
                + ",\"compatible_with\":\"Unity.ManageEditor GetPrefabStage\"}");
        }

        [McpCommand(
            "editor.active_tool.get",
            Title = "Get active editor tool",
            Description = "Read current Unity Editor active tool and handle context",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptyObjectSchemaJson,
            Tags = new string[] { "editor", "tool", "unity-ai-assistant" },
            Capability = FeatureFlag,
            ResultShape = "editor_active_tool",
            OutputLimitJson = "{\"max_objects\":1}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorActiveToolGet(McpCommandContext context)
        {
            var currentTool = Tools.current;
            var isCustom = currentTool == Tool.Custom;
            var activeToolName = ActiveToolName(currentTool);
            return CommandExecutionResult.Ok("{\"active_tool\":" + JsonUtil.String(activeToolName)
                + ",\"is_custom\":" + JsonUtil.Bool(isCustom)
                + ",\"pivot_mode\":" + JsonUtil.String(Tools.pivotMode.ToString())
                + ",\"pivot_rotation\":" + JsonUtil.String(Tools.pivotRotation.ToString())
                + ",\"handle_rotation_euler\":" + Vector3ArrayJson(Tools.handleRotation.eulerAngles)
                + ",\"handle_position\":" + Vector3ArrayJson(Tools.handlePosition)
                + ",\"compatible_with\":\"Unity.ManageEditor GetActiveTool\"}");
        }

        [McpCommand(
            "editor.active_tool.set",
            Title = "Set active editor tool",
            Description = "Set the Unity Editor active built-in tool with optional dry-run preflight",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = ActiveToolSetSchemaJson,
            Tags = new string[] { "editor", "tool", "unity-ai-assistant" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "editor_active_tool",
            OutputLimitJson = "{\"max_objects\":1}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorActiveToolSet(McpCommandContext context)
        {
            var requestedTool = JsonUtil.ExtractString(context.ArgsJson, "tool").Trim();
            if (string.IsNullOrEmpty(requestedTool))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument tool is required");
            }

            if (!TryParseBuiltInTool(requestedTool, out var tool))
            {
                return CommandExecutionResult.Error("invalid_field_value", "tool must be View, Move, Rotate, Scale, Rect, or Transform");
            }

            var previousTool = ActiveToolName(Tools.current);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            if (!dryRun)
            {
                Tools.current = tool;
            }

            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"previous_tool\":" + JsonUtil.String(previousTool)
                + ",\"requested_tool\":" + JsonUtil.String(tool.ToString())
                + ",\"active_tool\":" + JsonUtil.String(ActiveToolName(dryRun ? Tools.current : tool))
                + ",\"compatible_with\":\"Unity.ManageEditor SetActiveTool\"}");
        }

        static string ActiveToolName(Tool currentTool)
        {
            if (currentTool != Tool.Custom)
            {
                return currentTool.ToString();
            }

            var toolManager = Type.GetType("UnityEditor.EditorTools.ToolManager, UnityEditor");
            var activeToolTypeProperty = toolManager == null ? null : toolManager.GetProperty("activeToolType", BindingFlags.Public | BindingFlags.Static);
            var activeToolType = activeToolTypeProperty == null ? null : activeToolTypeProperty.GetValue(null, null) as Type;
            return activeToolType == null ? "Custom" : (activeToolType.FullName ?? activeToolType.Name);
        }

        static bool TryParseBuiltInTool(string value, out Tool tool)
        {
            if ((string.Equals(value, "View", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Move", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Rotate", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Scale", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Rect", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "Transform", StringComparison.OrdinalIgnoreCase))
                && Enum.TryParse(value, true, out tool))
            {
                return true;
            }

            tool = Tool.View;
            return false;
        }

        [McpCommand(
            "editor.windows.list",
            Title = "List editor windows",
            Description = "Read bounded Unity Editor window metadata",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = WindowsListSchemaJson,
            Tags = new string[] { "editor", "windows", "unity-ai-assistant" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = "{\"max_items\":100}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorWindowsList(McpCommandContext context)
        {
            var maxResults = JsonUtil.ExtractInt(context.ArgsJson, "max_results", DefaultWindowLimit);
            if (maxResults < 1 || maxResults > MaxWindowLimit)
            {
                return CommandExecutionResult.Error("invalid_field_value", "max_results must be between 1 and 100");
            }

            return CommandExecutionResult.Ok(EditorWindowsJson(maxResults));
        }

        [McpCommand(
            "editor.window.inspect",
            Title = "Inspect editor window",
            Description = "Read one selected Unity Editor window metadata and selector stability hints",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = WindowInspectSchemaJson,
            Tags = new string[] { "editor", "windows", "unity-ai-assistant" },
            Capability = FeatureFlag,
            ResultShape = "editor_window",
            OutputLimitJson = "{\"max_objects\":1}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorWindowInspect(McpCommandContext context)
        {
            var selectResult = EditorWindowSelector.Select(context.ArgsJson, out var window, out var selectedIndex, out _);
            if (selectResult != null)
            {
                return selectResult;
            }

            var windowsHash = EditorWindowsHash(out var windowsCount);
            return CommandExecutionResult.Ok("{\"selected_window\":" + EditorWindowSelector.MetadataJson(window, selectedIndex)
                + ",\"windows_count\":" + windowsCount
                + ",\"windows_hash\":" + JsonUtil.String(windowsHash)
                + ",\"selector_contract\":\"Use title, type_name, focused:true, or window_index from editor.windows.list; retry editor.windows.list if windows_hash changes.\""
                + ",\"privacy_warning\":\"Editor window titles and positions may expose local project or tool state; keep output in trusted local workflows.\""
                + ",\"next_actions\":[\"editor.windows.list\",\"editor.window.screenshot\",\"editor.window.position.set\"]"
                + ",\"compatible_with\":\"Unity.ManageEditor InspectWindow\"}");
        }

        [McpCommand(
            "editor.window.position.set",
            Title = "Set editor window position",
            Description = "Set a selected Unity Editor window position with optional dry-run preflight",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = WindowPositionSetSchemaJson,
            Tags = new string[] { "editor", "windows", "unity-ai-assistant" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "editor_window",
            OutputLimitJson = "{\"max_objects\":1}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorWindowPositionSet(McpCommandContext context)
        {
            var selectResult = EditorWindowSelector.Select(context.ArgsJson, out var window, out var selectedIndex, out _);
            if (selectResult != null)
            {
                return selectResult;
            }

            var x = JsonUtil.ExtractInt(context.ArgsJson, "x", int.MinValue);
            var y = JsonUtil.ExtractInt(context.ArgsJson, "y", int.MinValue);
            var width = JsonUtil.ExtractInt(context.ArgsJson, "width", 0);
            var height = JsonUtil.ExtractInt(context.ArgsJson, "height", 0);
            if (x == int.MinValue || y == int.MinValue || width == 0 || height == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "x, y, width, and height are required");
            }

            if (x < -65535 || x > 65535 || y < -65535 || y > 65535)
            {
                return CommandExecutionResult.Error("invalid_field_value", "x and y must be between -65535 and 65535");
            }

            if (width < 64 || width > 4096 || height < 64 || height > 4096)
            {
                return CommandExecutionResult.Error("invalid_field_value", "width and height must be between 64 and 4096");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var previousPosition = window.position;
            var requestedPosition = new UnityEngine.Rect(x, y, width, height);
            if (!dryRun)
            {
                window.position = requestedPosition;
                window.Repaint();
            }

            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"selected_window\":" + EditorWindowSelector.MetadataJson(window, selectedIndex)
                + ",\"previous_position\":" + EditorWindowSelector.RectJson(previousPosition)
                + ",\"requested_position\":" + EditorWindowSelector.RectJson(requestedPosition)
                + ",\"applied\":" + JsonUtil.Bool(!dryRun)
                + ",\"compatible_with\":\"Unity.ManageEditor SetWindowPosition\"}");
        }

        static CommandExecutionResult TryResolveObjectByRuntimeId(int instanceId, string entityId, out UnityEngine.Object selectedObject)
        {
            selectedObject = null;
#if UNITY_6000_4_OR_NEWER
            ulong runtimeEntityId;
            if (!string.IsNullOrEmpty(entityId))
            {
                if (!ulong.TryParse(entityId, NumberStyles.None, CultureInfo.InvariantCulture, out runtimeEntityId) || runtimeEntityId == 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "entity_id must be a positive unsigned integer string");
                }
            }
            else
            {
                if (instanceId <= 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "instance_id must be positive in Unity 6; use entity_id when available");
                }

                runtimeEntityId = (ulong)instanceId;
            }

            selectedObject = EditorUtility.EntityIdToObject(UnityEngine.EntityId.FromULong(runtimeEntityId));
#else
            if (!string.IsNullOrEmpty(entityId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "entity_id is available only in Unity 6 or newer");
            }

#pragma warning disable 0618
            selectedObject = EditorUtility.InstanceIDToObject(instanceId);
#pragma warning restore 0618
#endif
            if (selectedObject == null)
            {
                return CommandExecutionResult.Error("not_found", "object id does not resolve to an object");
            }

            return null;
        }

        [McpCommand(
            "editor.menu.list",
            Title = "List editor menu catalog",
            Description = "List bounded Unity Editor menu paths using local catalog reflection",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = MenuListSchemaJson,
            Tags = new string[] { "editor", "menu" },
            Capability = FeatureFlag,
            ResultShape = "menu_list",
            OutputLimitJson = "{\"max_items\":200}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorMenuList(McpCommandContext context)
        {
            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            if (query.Length > MaxMenuPathLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "query must be at most 128 characters");
            }

            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", DefaultMenuCatalogLimit), 1, MaxMenuCatalogLimit);
            var menuPaths = MenuCatalogPaths(query, maxResults, out var catalogAvailable, out var truncated, out var scanned, out var lookupTimedOut);
            return CommandExecutionResult.Ok("{\"source\":" + JsonUtil.String(catalogAvailable ? "unity_menu_catalog" : "known_safe_fallback")
                + ",\"catalog_available\":" + JsonUtil.Bool(catalogAvailable)
                + ",\"query\":" + (string.IsNullOrEmpty(query) ? "null" : JsonUtil.String(query))
                + ",\"count\":" + menuPaths.Length
                + ",\"scanned\":" + scanned
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"lookup_status\":" + JsonUtil.String(lookupTimedOut ? "timeout" : truncated ? "truncated" : "complete")
                + ",\"lookup_timeout\":" + JsonUtil.Bool(lookupTimedOut)
                + ",\"lookup_budget_ms\":" + MenuLookupBudgetMs
                + ",\"menu_paths\":" + StringArrayJson(menuPaths)
                + ",\"known_safe_menu_paths\":" + KnownSafeMenuPathsJson()
                + ",\"execute_command\":\"editor.menu.execute\",\"execute_policy_gated\":true}");
        }

        [McpCommand(
            "editor.menu.exists",
            Title = "Check editor menu path",
            Description = "Check whether a Unity Editor menu path is visible in the local menu catalog",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = MenuExistsSchemaJson,
            Tags = new string[] { "editor", "menu" },
            Capability = FeatureFlag,
            ResultShape = "menu_metadata",
            OutputLimitJson = "{\"max_items\":16}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorMenuExists(McpCommandContext context)
        {
            var menuPath = JsonUtil.ExtractString(context.ArgsJson, "menu_path").Trim();
            if (string.IsNullOrEmpty(menuPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument menu_path is required");
            }

            if (menuPath.Length > MaxMenuPathLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "menu_path must be at most 128 characters");
            }

            var catalogAvailable = TryMenuCatalogContains(menuPath, out var existsInCatalog, out var existsCatalogTruncated, out var existsCatalogScanned, out var existsCatalogSource, out var existsCatalogRefreshAttempted, out var existsMenuItemAttributeScanned, out var existsLookupTimedOut);
            var knownSafe = IsKnownSafeMenuPath(menuPath);
            var lookupComplete = MenuLookupComplete(catalogAvailable, knownSafe, existsInCatalog, existsCatalogTruncated, existsLookupTimedOut);
            return CommandExecutionResult.Ok("{\"menu_path\":" + JsonUtil.String(menuPath)
                + ",\"exists\":" + JsonUtil.Bool(knownSafe || existsInCatalog)
                + ",\"catalog_available\":" + JsonUtil.Bool(catalogAvailable)
                + ",\"known_safe\":" + JsonUtil.Bool(knownSafe)
                + ",\"source\":" + JsonUtil.String(existsCatalogSource)
                + ",\"catalog_scanned\":" + existsCatalogScanned
                + ",\"catalog_truncated\":" + JsonUtil.Bool(existsCatalogTruncated)
                + ",\"lookup_complete\":" + JsonUtil.Bool(lookupComplete)
                + ",\"absence_provable\":" + JsonUtil.Bool(lookupComplete)
                + ",\"lookup_status\":" + JsonUtil.String(MenuLookupStatus(catalogAvailable, knownSafe, existsInCatalog, existsCatalogTruncated, existsLookupTimedOut))
                + ",\"lookup_timeout\":" + JsonUtil.Bool(existsLookupTimedOut)
                + ",\"lookup_budget_ms\":" + MenuLookupBudgetMs
                + ",\"catalog_refresh_attempted\":" + JsonUtil.Bool(existsCatalogRefreshAttempted)
                + ",\"menu_item_attribute_scanned\":" + existsMenuItemAttributeScanned
                + ",\"catalog_age_ms\":0"
                + ",\"can_attempt_execute\":true"
                + ",\"execute_command\":\"editor.menu.execute\",\"execute_policy_gated\":true}");
        }

        [McpCommand(
            "editor.menu.execute",
            Title = "Execute editor menu item",
            Description = "Execute a Unity Editor menu item with optional dry-run preflight",
            Risk = "execute",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = MenuExecuteSchemaJson,
            Tags = new string[] { "editor", "menu" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "menu_execution",
            OutputLimitJson = "{\"max_objects\":16}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorMenuExecute(McpCommandContext context)
        {
            var menuPath = JsonUtil.ExtractString(context.ArgsJson, "menu_path").Trim();
            if (string.IsNullOrEmpty(menuPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument menu_path is required");
            }

            if (menuPath.Length > MaxMenuPathLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "menu_path must be at most 128 characters");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            if (dryRun)
            {
                var knownSafe = IsKnownSafeMenuPath(menuPath);
                var catalogAvailable = TryMenuCatalogContains(menuPath, out var existsInCatalog, out var catalogTruncated, out var catalogScanned, out var catalogSource, out var catalogRefreshAttempted, out var menuItemAttributeScanned, out var lookupTimedOut);
                var lookupComplete = MenuLookupComplete(catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, lookupTimedOut);
                if (lookupTimedOut && !knownSafe && !existsInCatalog)
                {
                    return MenuLookupTimeout(menuPath, true, false, catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, catalogScanned, catalogSource, catalogRefreshAttempted, menuItemAttributeScanned, lookupTimedOut);
                }

                if (catalogAvailable && !catalogTruncated && !knownSafe && !existsInCatalog)
                {
                    return MenuPathNotFound(menuPath, catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, catalogScanned, catalogSource, catalogRefreshAttempted, menuItemAttributeScanned, lookupTimedOut);
                }

                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_execute\":true,\"menu_path\":" + JsonUtil.String(menuPath)
                    + ",\"would_attempt_execute\":true"
                    + ",\"catalog_available\":" + JsonUtil.Bool(catalogAvailable)
                    + ",\"catalog_source\":" + JsonUtil.String(catalogSource)
                    + ",\"exists\":" + JsonUtil.Bool(knownSafe || existsInCatalog)
                    + ",\"known_safe\":" + JsonUtil.Bool(knownSafe)
                    + ",\"catalog_scanned\":" + catalogScanned
                    + ",\"catalog_truncated\":" + JsonUtil.Bool(catalogTruncated)
                    + ",\"lookup_complete\":" + JsonUtil.Bool(lookupComplete)
                    + ",\"absence_provable\":" + JsonUtil.Bool(lookupComplete)
                    + ",\"lookup_status\":" + JsonUtil.String(MenuLookupStatus(catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, lookupTimedOut))
                    + ",\"lookup_timeout\":" + JsonUtil.Bool(lookupTimedOut)
                    + ",\"lookup_budget_ms\":" + MenuLookupBudgetMs
                    + ",\"catalog_refresh_attempted\":" + JsonUtil.Bool(catalogRefreshAttempted)
                    + ",\"menu_item_attribute_scanned\":" + menuItemAttributeScanned
                    + ",\"catalog_age_ms\":0"
                    + ",\"unity_lifecycle\":" + MenuUnityLifecycleJson()
                    + ",\"modal_diagnostics_command\":\"diagnostics.unity_modal.status\""
                    + ",\"can_attempt_execute\":true"
                    + ",\"known_safe_menu_paths\":" + KnownSafeMenuPathsJson() + "}");
            }

            var execution = ExecuteMenuItemObserved(menuPath);
            if (execution.ExceptionObserved)
            {
                return CommandExecutionResult.Error(
                    "unity_exception",
                    "The invoked Unity menu item logged a synchronous exception",
                    MenuExecutionExceptionDetailsJson(menuPath, execution));
            }

            if (!execution.Returned)
            {
                var knownSafe = IsKnownSafeMenuPath(menuPath);
                var catalogAvailable = TryMenuCatalogContains(menuPath, out var existsInCatalog, out var catalogTruncated, out var catalogScanned, out var catalogSource, out var catalogRefreshAttempted, out var menuItemAttributeScanned, out var lookupTimedOut);
                if (lookupTimedOut && !knownSafe && !existsInCatalog)
                {
                    return MenuLookupTimeout(menuPath, false, true, catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, catalogScanned, catalogSource, catalogRefreshAttempted, menuItemAttributeScanned, lookupTimedOut);
                }

                return MenuPathNotFound(menuPath, catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, catalogScanned, catalogSource, catalogRefreshAttempted, menuItemAttributeScanned, lookupTimedOut);
            }

            return CommandExecutionResult.Ok("{\"executed\":true,\"menu_path\":" + JsonUtil.String(menuPath) + "}");
        }

        internal static MenuExecutionObservation ExecuteMenuItemObserved(string menuPath)
        {
            var observation = new MenuExecutionObservation();
            UnityEngine.Application.LogCallback callback = (condition, stackTrace, type) =>
            {
                if (type != UnityEngine.LogType.Exception || observation.ExceptionObserved)
                {
                    return;
                }

                observation.ExceptionObserved = true;
                observation.ExceptionCondition = condition ?? string.Empty;
                observation.ExceptionStackTrace = stackTrace ?? string.Empty;
            };

            UnityEngine.Application.logMessageReceived += callback;
            try
            {
                observation.Returned = EditorApplication.ExecuteMenuItem(menuPath);
            }
            finally
            {
                UnityEngine.Application.logMessageReceived -= callback;
            }

            return observation;
        }

        internal static string MenuExecutionExceptionDetailsJson(string menuPath, MenuExecutionObservation observation)
        {
            const int maxConditionChars = 2048;
            const int maxStackTraceChars = 8192;
            var condition = observation?.ExceptionCondition ?? string.Empty;
            var stackTrace = observation?.ExceptionStackTrace ?? string.Empty;
            var conditionTruncated = condition.Length > maxConditionChars;
            var stackTraceTruncated = stackTrace.Length > maxStackTraceChars;
            if (conditionTruncated) condition = condition.Substring(0, maxConditionChars);
            if (stackTraceTruncated) stackTrace = stackTrace.Substring(0, maxStackTraceChars);
            return "{\"menu_path\":" + JsonUtil.String(menuPath)
                + ",\"execute_menu_item_returned\":" + JsonUtil.Bool(observation != null && observation.Returned)
                + ",\"exception_observed\":true"
                + ",\"exception_condition\":" + JsonUtil.String(condition)
                + ",\"exception_stack_trace\":" + JsonUtil.String(stackTrace)
                + ",\"exception_condition_truncated\":" + JsonUtil.Bool(conditionTruncated)
                + ",\"exception_stack_trace_truncated\":" + JsonUtil.Bool(stackTraceTruncated) + "}";
        }

        [McpCommand(
            "editor.tags.list",
            Title = "List editor tags",
            Description = "Read bounded Unity Editor project tags",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptyObjectSchemaJson,
            Tags = new string[] { "editor", "tags" },
            Capability = FeatureFlag,
            ResultShape = "editor_metadata",
            OutputLimitJson = "{\"max_items\":100}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorTagsList(McpCommandContext context)
        {
            var tags = UnityEditorInternal.InternalEditorUtility.tags ?? new string[0];
            return CommandExecutionResult.Ok("{\"tags\":" + StringArrayJson(tags)
                + ",\"count\":" + tags.Length + "}");
        }

        [McpCommand(
            "editor.tags.add",
            Title = "Add editor tag",
            Description = "Add one Unity Editor project tag with optional dry-run preflight",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = TagModifySchemaJson,
            Tags = new string[] { "editor", "tags", "unity-ai-assistant" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "editor_metadata",
            OutputLimitJson = "{\"max_items\":100}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorTagsAdd(McpCommandContext context)
        {
            var validation = RequireTagName(context.ArgsJson, out var tagName);
            if (validation != null)
            {
                return validation;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var existedBefore = ContainsString(UnityEditorInternal.InternalEditorUtility.tags ?? new string[0], tagName);
            if (!dryRun && !existedBefore)
            {
                UnityEditorInternal.InternalEditorUtility.AddTag(tagName);
            }

            var tags = UnityEditorInternal.InternalEditorUtility.tags ?? new string[0];
            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"tag\":" + JsonUtil.String(tagName)
                + ",\"existed_before\":" + JsonUtil.Bool(existedBefore)
                + ",\"added\":" + JsonUtil.Bool(!dryRun && !existedBefore)
                + ",\"would_add\":" + JsonUtil.Bool(dryRun && !existedBefore)
                + ",\"tags\":" + StringArrayJson(tags)
                + ",\"count\":" + tags.Length
                + ",\"compatible_with\":\"Unity.ManageEditor AddTag\"}");
        }

        [McpCommand(
            "editor.tags.remove",
            Title = "Remove editor tag",
            Description = "Remove one Unity Editor project tag with optional dry-run preflight",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = TagModifySchemaJson,
            Tags = new string[] { "editor", "tags", "unity-ai-assistant" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "editor_metadata",
            OutputLimitJson = "{\"max_items\":100}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorTagsRemove(McpCommandContext context)
        {
            var validation = RequireTagName(context.ArgsJson, out var tagName);
            if (validation != null)
            {
                return validation;
            }

            if (IsBuiltInTag(tagName))
            {
                return CommandExecutionResult.Error("protected_tag", "Built-in Unity tags cannot be removed");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var existedBefore = ContainsString(UnityEditorInternal.InternalEditorUtility.tags ?? new string[0], tagName);
            if (!dryRun && existedBefore)
            {
                UnityEditorInternal.InternalEditorUtility.RemoveTag(tagName);
            }

            var tags = UnityEditorInternal.InternalEditorUtility.tags ?? new string[0];
            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"tag\":" + JsonUtil.String(tagName)
                + ",\"existed_before\":" + JsonUtil.Bool(existedBefore)
                + ",\"removed\":" + JsonUtil.Bool(!dryRun && existedBefore)
                + ",\"would_remove\":" + JsonUtil.Bool(dryRun && existedBefore)
                + ",\"tags\":" + StringArrayJson(tags)
                + ",\"count\":" + tags.Length
                + ",\"compatible_with\":\"Unity.ManageEditor RemoveTag\"}");
        }

        [McpCommand(
            "editor.layers.list",
            Title = "List editor layers",
            Description = "Read bounded Unity Editor project layers",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptyObjectSchemaJson,
            Tags = new string[] { "editor", "layers" },
            Capability = FeatureFlag,
            ResultShape = "editor_metadata",
            OutputLimitJson = "{\"max_items\":32}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorLayersList(McpCommandContext context)
        {
            var layers = UnityEditorInternal.InternalEditorUtility.layers ?? new string[0];
            return CommandExecutionResult.Ok("{\"layers\":" + StringArrayJson(layers)
                + ",\"count\":" + layers.Length + "}");
        }

        [McpCommand(
            "editor.layers.add",
            Title = "Add editor layer",
            Description = "Add one Unity Editor user layer with optional dry-run preflight",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = LayerModifySchemaJson,
            Tags = new string[] { "editor", "layers", "unity-ai-assistant" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "editor_metadata",
            OutputLimitJson = "{\"max_items\":32}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorLayersAdd(McpCommandContext context)
        {
            var validation = RequireLayerName(context.ArgsJson, out var layerName);
            if (validation != null)
            {
                return validation;
            }

            var tagManagerResult = TryGetTagManagerLayers(out var tagManager, out var layersProperty);
            if (tagManagerResult != null)
            {
                return tagManagerResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var existingIndex = FindLayerIndex(layersProperty, layerName, 0);
            var emptyIndex = FindEmptyUserLayerIndex(layersProperty);
            if (existingIndex < 0 && emptyIndex < 0)
            {
                return CommandExecutionResult.Error("layer_slots_full", "No empty user layer slot is available");
            }

            if (!dryRun && existingIndex < 0)
            {
                layersProperty.GetArrayElementAtIndex(emptyIndex).stringValue = layerName;
                tagManager.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }

            var layers = UnityEditorInternal.InternalEditorUtility.layers ?? new string[0];
            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"layer\":" + JsonUtil.String(layerName)
                + ",\"index\":" + (existingIndex >= 0 ? existingIndex : emptyIndex)
                + ",\"existed_before\":" + JsonUtil.Bool(existingIndex >= 0)
                + ",\"added\":" + JsonUtil.Bool(!dryRun && existingIndex < 0)
                + ",\"would_add\":" + JsonUtil.Bool(dryRun && existingIndex < 0)
                + ",\"layers\":" + StringArrayJson(layers)
                + ",\"count\":" + layers.Length
                + ",\"compatible_with\":\"Unity.ManageEditor AddLayer\"}");
        }

        [McpCommand(
            "editor.layers.remove",
            Title = "Remove editor layer",
            Description = "Remove one Unity Editor user layer with optional dry-run preflight",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = LayerModifySchemaJson,
            Tags = new string[] { "editor", "layers", "unity-ai-assistant" },
            Capability = FeatureFlag,
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "editor_metadata",
            OutputLimitJson = "{\"max_items\":32}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorLayersRemove(McpCommandContext context)
        {
            var validation = RequireLayerName(context.ArgsJson, out var layerName);
            if (validation != null)
            {
                return validation;
            }

            var tagManagerResult = TryGetTagManagerLayers(out var tagManager, out var layersProperty);
            if (tagManagerResult != null)
            {
                return tagManagerResult;
            }

            if (FindLayerIndex(layersProperty, layerName, 0, 7) >= 0)
            {
                return CommandExecutionResult.Error("protected_layer", "Built-in Unity layers cannot be removed");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var existingIndex = FindLayerIndex(layersProperty, layerName, 8);
            if (!dryRun && existingIndex >= 0)
            {
                layersProperty.GetArrayElementAtIndex(existingIndex).stringValue = string.Empty;
                tagManager.ApplyModifiedProperties();
                AssetDatabase.SaveAssets();
            }

            var layers = UnityEditorInternal.InternalEditorUtility.layers ?? new string[0];
            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"layer\":" + JsonUtil.String(layerName)
                + ",\"index\":" + (existingIndex >= 0 ? existingIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"existed_before\":" + JsonUtil.Bool(existingIndex >= 0)
                + ",\"removed\":" + JsonUtil.Bool(!dryRun && existingIndex >= 0)
                + ",\"would_remove\":" + JsonUtil.Bool(dryRun && existingIndex >= 0)
                + ",\"layers\":" + StringArrayJson(layers)
                + ",\"count\":" + layers.Length
                + ",\"compatible_with\":\"Unity.ManageEditor RemoveLayer\"}");
        }

        static CommandExecutionResult TryValidateAssetPath(string assetPath, out string validatedPath)
        {
            validatedPath = string.Empty;
            try
            {
                validatedPath = PathGuard.RequireUnityAssetPath(assetPath);
                return null;
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", exception.Message);
            }
        }

        static CommandExecutionResult TryValidateSceneAssetPath(string scenePath, out string validatedPath)
        {
            var pathValidation = TryValidateAssetPath(scenePath, out validatedPath);
            if (pathValidation != null)
            {
                return pathValidation;
            }

            if (!validatedPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "scene_path must end with .unity");
            }

            return null;
        }

        static Scene FindLoadedScene(string scenePath)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && string.Equals(scene.path, scenePath, StringComparison.Ordinal))
                {
                    return scene;
                }
            }

            return default;
        }

        static UnityEngine.GameObject FindByPath(UnityEngine.GameObject[] roots, string objectPath)
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

        static UnityEngine.GameObject FindChild(UnityEngine.GameObject parent, string name)
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

        static CommandExecutionResult RequireTagName(string argsJson, out string tagName)
        {
            tagName = JsonUtil.ExtractString(argsJson, "tag").Trim();
            if (string.IsNullOrEmpty(tagName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument tag is required");
            }

            if (tagName.Length > MaxTagLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "tag must be at most 64 characters");
            }

            if (tagName.IndexOf('\0') >= 0 || tagName.IndexOf('/') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "tag must not contain NUL or slash characters");
            }

            return null;
        }

        static bool IsBuiltInTag(string tagName)
        {
            return string.Equals(tagName, "Untagged", StringComparison.Ordinal)
                || string.Equals(tagName, "Respawn", StringComparison.Ordinal)
                || string.Equals(tagName, "Finish", StringComparison.Ordinal)
                || string.Equals(tagName, "EditorOnly", StringComparison.Ordinal)
                || string.Equals(tagName, "MainCamera", StringComparison.Ordinal)
                || string.Equals(tagName, "Player", StringComparison.Ordinal)
                || string.Equals(tagName, "GameController", StringComparison.Ordinal);
        }

        static bool ContainsString(string[] values, string value)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (string.Equals(values[index], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static CommandExecutionResult RequireLayerName(string argsJson, out string layerName)
        {
            layerName = JsonUtil.ExtractString(argsJson, "layer").Trim();
            if (string.IsNullOrEmpty(layerName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument layer is required");
            }

            if (layerName.Length > MaxLayerLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "layer must be at most 64 characters");
            }

            if (layerName.IndexOf('\0') >= 0 || layerName.IndexOf('/') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "layer must not contain NUL or slash characters");
            }

            return null;
        }

        static CommandExecutionResult TryGetTagManagerLayers(out SerializedObject tagManager, out SerializedProperty layersProperty)
        {
            tagManager = null;
            layersProperty = null;
            var assets = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/TagManager.asset");
            if (assets == null || assets.Length == 0 || assets[0] == null)
            {
                return CommandExecutionResult.Error("not_found", "ProjectSettings/TagManager.asset was not found");
            }

            tagManager = new SerializedObject(assets[0]);
            layersProperty = tagManager.FindProperty("layers");
            if (layersProperty == null || !layersProperty.isArray || layersProperty.arraySize < 32)
            {
                return CommandExecutionResult.Error("not_found", "TagManager layers array was not found");
            }

            return null;
        }

        static int FindLayerIndex(SerializedProperty layersProperty, string layerName, int startIndex)
        {
            return FindLayerIndex(layersProperty, layerName, startIndex, layersProperty.arraySize - 1);
        }

        static int FindLayerIndex(SerializedProperty layersProperty, string layerName, int startIndex, int endIndex)
        {
            var lastIndex = Math.Min(endIndex, layersProperty.arraySize - 1);
            for (var index = Math.Max(0, startIndex); index <= lastIndex; index++)
            {
                if (string.Equals(layersProperty.GetArrayElementAtIndex(index).stringValue, layerName, StringComparison.Ordinal))
                {
                    return index;
                }
            }

            return -1;
        }

        static int FindEmptyUserLayerIndex(SerializedProperty layersProperty)
        {
            for (var index = 8; index < layersProperty.arraySize && index < 32; index++)
            {
                if (string.IsNullOrEmpty(layersProperty.GetArrayElementAtIndex(index).stringValue))
                {
                    return index;
                }
            }

            return -1;
        }

        static string[] MenuCatalogPaths(string query, int maxResults, out bool catalogAvailable, out bool truncated, out int scanned, out bool lookupTimedOut)
        {
            var stopwatch = Stopwatch.StartNew();
            catalogAvailable = TryGetSubmenus(string.Empty, out var rootMenus);
            truncated = false;
            scanned = 0;
            lookupTimedOut = LookupBudgetExceeded(stopwatch);
            if (!catalogAvailable)
            {
                return FilterMenuPaths(KnownSafeMenuPaths, query, maxResults, out truncated, out scanned);
            }

            var queue = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var results = new List<string>();
            AddMenuPaths(queue, rootMenus);
            AddMenuPaths(queue, KnownMenuRoots);

            for (var cursor = 0; cursor < queue.Count && scanned < MaxMenuCatalogScan; cursor++)
            {
                if (LookupBudgetExceeded(stopwatch))
                {
                    lookupTimedOut = true;
                    truncated = true;
                    break;
                }

                var menuPath = queue[cursor];
                if (string.IsNullOrEmpty(menuPath) || !seen.Add(menuPath))
                {
                    continue;
                }

                scanned++;
                if (string.IsNullOrEmpty(query) || menuPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    if (results.Count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    results.Add(menuPath);
                }

                if (!LookupBudgetExceeded(stopwatch) && TryGetSubmenus(menuPath, out var children))
                {
                    AddMenuPaths(queue, children);
                }
            }

            if (scanned >= MaxMenuCatalogScan && queue.Count > scanned)
            {
                truncated = true;
            }

            results.Sort(StringComparer.Ordinal);
            return results.ToArray();
        }

        static string[] FilterMenuPaths(string[] menuPaths, string query, int maxResults, out bool truncated, out int scanned)
        {
            var results = new List<string>();
            scanned = 0;
            truncated = false;
            for (var index = 0; index < menuPaths.Length; index++)
            {
                var menuPath = menuPaths[index];
                scanned++;
                if (!string.IsNullOrEmpty(query) && menuPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                if (results.Count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                results.Add(menuPath);
            }

            return results.ToArray();
        }

        static bool TryMenuCatalogContains(string menuPath, out bool exists)
        {
            return TryMenuCatalogContains(menuPath, out exists, out var truncated, out var scanned, out var lookupTimedOut);
        }

        static bool TryMenuCatalogContains(string menuPath, out bool exists, out bool truncated, out int scanned, out bool lookupTimedOut)
        {
            return TryMenuCatalogContains(menuPath, out exists, out truncated, out scanned, out var catalogSource, out var catalogRefreshAttempted, out var menuItemAttributeScanned, out lookupTimedOut);
        }

        static bool TryMenuCatalogContains(string menuPath, out bool exists, out bool truncated, out int scanned, out string catalogSource, out bool catalogRefreshAttempted, out int menuItemAttributeScanned, out bool lookupTimedOut)
        {
            var stopwatch = Stopwatch.StartNew();
            exists = false;
            truncated = false;
            scanned = 0;
            catalogSource = "known_safe_fallback";
            catalogRefreshAttempted = false;
            menuItemAttributeScanned = 0;
            lookupTimedOut = false;
            var catalogAvailable = TryGetSubmenus(string.Empty, out var rootMenus);
            lookupTimedOut = LookupBudgetExceeded(stopwatch);
            if (!catalogAvailable)
            {
                exists = IsKnownSafeMenuPath(menuPath);
                if (!exists && !lookupTimedOut && IsUnityReadyForMenuCatalogRefresh())
                {
                    catalogRefreshAttempted = true;
                    catalogSource = "menu_item_attribute_scan";
                    exists = TryMenuItemAttributeContains(menuPath, stopwatch, out menuItemAttributeScanned, out truncated, out lookupTimedOut);
                    scanned += menuItemAttributeScanned;
                }

                truncated = truncated || lookupTimedOut;
                return catalogRefreshAttempted;
            }

            catalogSource = "unity_menu_catalog";
            var queue = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            AddMenuPaths(queue, rootMenus);
            AddMenuPaths(queue, KnownMenuRoots);

            for (var cursor = 0; cursor < queue.Count && scanned < MaxMenuCatalogScan; cursor++)
            {
                if (LookupBudgetExceeded(stopwatch))
                {
                    lookupTimedOut = true;
                    truncated = true;
                    break;
                }

                var currentPath = queue[cursor];
                if (string.IsNullOrEmpty(currentPath) || !seen.Add(currentPath))
                {
                    continue;
                }

                scanned++;
                if (string.Equals(currentPath, menuPath, StringComparison.Ordinal))
                {
                    exists = true;
                    break;
                }

                if (!LookupBudgetExceeded(stopwatch) && TryGetSubmenus(currentPath, out var children))
                {
                    AddMenuPaths(queue, children);
                }
            }

            if (!exists && scanned >= MaxMenuCatalogScan && queue.Count > scanned)
            {
                truncated = true;
            }

            if (!exists && !lookupTimedOut && IsUnityReadyForMenuCatalogRefresh())
            {
                catalogRefreshAttempted = true;
                exists = TryMenuItemAttributeContains(menuPath, stopwatch, out menuItemAttributeScanned, out var attributeTruncated, out lookupTimedOut);
                scanned += menuItemAttributeScanned;
                truncated = truncated || attributeTruncated;
                if (exists)
                {
                    catalogSource = "menu_item_attribute_scan";
                }
            }

            return true;
        }

        static bool TryMenuItemAttributeContains(string menuPath, Stopwatch stopwatch, out int scanned, out bool truncated, out bool lookupTimedOut)
        {
            scanned = 0;
            truncated = false;
            lookupTimedOut = false;
            foreach (var method in TypeCache.GetMethodsWithAttribute<MenuItem>())
            {
                var attributes = method.GetCustomAttributes(typeof(MenuItem), false);
                for (var index = 0; index < attributes.Length; index++)
                {
                    if (LookupBudgetExceeded(stopwatch))
                    {
                        truncated = true;
                        lookupTimedOut = true;
                        return false;
                    }

                    if (scanned >= MaxMenuCatalogScan)
                    {
                        truncated = true;
                        return false;
                    }

                    var attribute = attributes[index] as MenuItem;
                    if (attribute == null || attribute.validate)
                    {
                        continue;
                    }

                    scanned++;
                    if (string.Equals(NormalizeMenuItemAttributePath(attribute.menuItem), menuPath, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        static bool LookupBudgetExceeded(Stopwatch stopwatch)
        {
            return stopwatch.ElapsedMilliseconds >= MenuLookupBudgetMs;
        }

        static bool MenuLookupComplete(bool catalogAvailable, bool knownSafe, bool existsInCatalog, bool catalogTruncated, bool lookupTimedOut)
        {
            return knownSafe || existsInCatalog || (catalogAvailable && !catalogTruncated && !lookupTimedOut);
        }

        static string MenuLookupStatus(bool catalogAvailable, bool knownSafe, bool existsInCatalog, bool catalogTruncated, bool lookupTimedOut)
        {
            if (knownSafe)
            {
                return "known_safe";
            }

            if (existsInCatalog)
            {
                return "found";
            }

            if (lookupTimedOut)
            {
                return "timeout";
            }

            if (catalogTruncated)
            {
                return "truncated";
            }

            return catalogAvailable ? "complete_not_found" : "catalog_unavailable";
        }

        static string NormalizeMenuItemAttributePath(string menuPath)
        {
            var trimmed = (menuPath ?? string.Empty).Trim();
            var hotkeyIndex = trimmed.LastIndexOf(' ');
            if (hotkeyIndex > 0 && hotkeyIndex + 1 < trimmed.Length && IsMenuHotkeyMarker(trimmed[hotkeyIndex + 1]))
            {
                return trimmed.Substring(0, hotkeyIndex);
            }

            return trimmed;
        }

        static bool IsMenuHotkeyMarker(char value)
        {
            return value == '_' || value == '%' || value == '#' || value == '&';
        }

        static bool IsUnityReadyForMenuCatalogRefresh()
        {
            return !EditorApplication.isCompiling && !EditorApplication.isUpdating;
        }

        static CommandExecutionResult MenuPathNotFound(string menuPath, bool catalogAvailable, bool knownSafe, bool existsInCatalog, bool catalogTruncated, int catalogScanned, string catalogSource, bool catalogRefreshAttempted, int menuItemAttributeScanned, bool lookupTimedOut)
        {
            var lookupComplete = MenuLookupComplete(catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, lookupTimedOut);
            var detailsJson = "{\"menu_path\":" + JsonUtil.String(menuPath)
                + ",\"catalog_available\":" + JsonUtil.Bool(catalogAvailable)
                + ",\"catalog_source\":" + JsonUtil.String(catalogSource)
                + ",\"known_safe\":" + JsonUtil.Bool(knownSafe)
                + ",\"exists_in_catalog\":" + JsonUtil.Bool(existsInCatalog)
                + ",\"catalog_scanned\":" + catalogScanned
                + ",\"catalog_truncated\":" + JsonUtil.Bool(catalogTruncated)
                + ",\"lookup_complete\":" + JsonUtil.Bool(lookupComplete)
                + ",\"absence_provable\":" + JsonUtil.Bool(lookupComplete)
                + ",\"lookup_status\":" + JsonUtil.String(MenuLookupStatus(catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, lookupTimedOut))
                + ",\"lookup_timeout\":" + JsonUtil.Bool(lookupTimedOut)
                + ",\"lookup_budget_ms\":" + MenuLookupBudgetMs
                + ",\"feature_flag\":\"editor_utilities\""
                + ",\"feature_flag_status\":\"enabled_for_command_context\""
                + ",\"catalog_scan\":\"fresh\""
                + ",\"catalog_refresh_attempted\":" + JsonUtil.Bool(catalogRefreshAttempted)
                + ",\"menu_item_attribute_scanned\":" + menuItemAttributeScanned
                + ",\"catalog_age_ms\":0"
                + ",\"unity_lifecycle\":" + MenuUnityLifecycleJson()
                + ",\"modal_diagnostics_command\":\"diagnostics.unity_modal.status\""
                + ",\"suggested_recovery_actions\":[\"compile.wait_ready\",\"assets.refresh\",\"editor.menu.list\",\"editor.menu.exists\",\"diagnostics.unity_modal.status\"]"
                + ",\"recovery_hint\":\"Verify the exact Unity menu path after compilation and asset refresh, then retry if the path exists.\"}";
            return CommandExecutionResult.Error("menu_path_not_found", "Unity menu path was not found", detailsJson);
        }

        static CommandExecutionResult MenuLookupTimeout(string menuPath, bool dryRun, bool executeReturnedFalse, bool catalogAvailable, bool knownSafe, bool existsInCatalog, bool catalogTruncated, int catalogScanned, string catalogSource, bool catalogRefreshAttempted, int menuItemAttributeScanned, bool lookupTimedOut)
        {
            var detailsJson = "{\"menu_path\":" + JsonUtil.String(menuPath)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"execute_returned_false\":" + JsonUtil.Bool(executeReturnedFalse)
                + ",\"would_execute\":false"
                + ",\"would_attempt_execute\":false"
                + ",\"catalog_available\":" + JsonUtil.Bool(catalogAvailable)
                + ",\"catalog_source\":" + JsonUtil.String(catalogSource)
                + ",\"known_safe\":" + JsonUtil.Bool(knownSafe)
                + ",\"exists_in_catalog\":" + JsonUtil.Bool(existsInCatalog)
                + ",\"catalog_scanned\":" + catalogScanned
                + ",\"catalog_truncated\":" + JsonUtil.Bool(catalogTruncated)
                + ",\"lookup_complete\":false"
                + ",\"absence_provable\":false"
                + ",\"lookup_status\":" + JsonUtil.String(MenuLookupStatus(catalogAvailable, knownSafe, existsInCatalog, catalogTruncated, lookupTimedOut))
                + ",\"lookup_timeout\":" + JsonUtil.Bool(lookupTimedOut)
                + ",\"lookup_budget_ms\":" + MenuLookupBudgetMs
                + ",\"catalog_refresh_attempted\":" + JsonUtil.Bool(catalogRefreshAttempted)
                + ",\"menu_item_attribute_scanned\":" + menuItemAttributeScanned
                + ",\"catalog_age_ms\":0"
                + ",\"unity_lifecycle\":" + MenuUnityLifecycleJson()
                + ",\"modal_diagnostics_command\":\"diagnostics.unity_modal.status\""
                + ",\"suggested_recovery_actions\":[\"compile.wait_ready\",\"assets.refresh\",\"editor.menu.exists\",\"editor.menu.list\",\"diagnostics.unity_modal.status\"]"
                + ",\"recovery_hint\":\"Unity menu lookup exceeded its bounded preflight budget, so menu absence is not provable; dry_run:true does not execute the menu item, while dry_run:false reaches this diagnostic only after ExecuteMenuItem returned false. Verify Unity is idle and retry a narrower editor.menu.exists/list query.\"}";
            return CommandExecutionResult.Error("menu_lookup_timeout", "Unity menu lookup timed out before menu absence could be proven", detailsJson);
        }

        static string MenuUnityLifecycleJson()
        {
            return "{\"is_compiling\":" + JsonUtil.Bool(EditorApplication.isCompiling)
                + ",\"is_updating\":" + JsonUtil.Bool(EditorApplication.isUpdating)
                + ",\"is_playing\":" + JsonUtil.Bool(EditorApplication.isPlaying)
                + ",\"is_batchmode\":" + JsonUtil.Bool(UnityEngine.Application.isBatchMode) + "}";
        }

        static bool TryGetSubmenus(string menuPath, out string[] submenus)
        {
            submenus = new string[0];
            var unsupportedType = typeof(EditorApplication).Assembly.GetType("UnityEditor.Unsupported");
            var method = unsupportedType == null ? null : unsupportedType.GetMethod("GetSubmenus", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new[] { typeof(string) }, null);
            if (method == null)
            {
                return false;
            }

            try
            {
                submenus = method.Invoke(null, new object[] { menuPath }) as string[] ?? new string[0];
                return true;
            }
            catch (TargetInvocationException)
            {
                submenus = new string[0];
                return true;
            }
            catch (ArgumentException)
            {
                submenus = new string[0];
                return true;
            }
        }

        static void AddMenuPaths(List<string> target, string[] menuPaths)
        {
            for (var index = 0; index < menuPaths.Length; index++)
            {
                var menuPath = menuPaths[index];
                if (!string.IsNullOrEmpty(menuPath))
                {
                    target.Add(menuPath);
                }
            }
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static bool IsKnownSafeMenuPath(string menuPath)
        {
            foreach (var knownSafeMenuPath in KnownSafeMenuPaths)
            {
                if (string.Equals(menuPath, knownSafeMenuPath, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static string KnownSafeMenuPathsJson()
        {
            return StringArrayJson(KnownSafeMenuPaths);
        }

        static string StringArrayJson(string[] values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < values.Length; index++)
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

        static string Vector3ArrayJson(UnityEngine.Vector3 value)
        {
            return "[" + FloatJson(value.x) + "," + FloatJson(value.y) + "," + FloatJson(value.z) + "]";
        }

        static string EditorWindowsJson(int maxResults)
        {
            var windows = UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>() ?? new EditorWindow[0];
            var focusedWindow = EditorWindow.focusedWindow;
            var windowsHash = EditorWindowsHash(windows, focusedWindow, out _);
            var builder = new StringBuilder();
            var count = 0;
            var returned = 0;

            builder.Append("{\"windows\":[");
            for (var index = 0; index < windows.Length; index++)
            {
                var window = windows[index];
                if (window == null)
                {
                    continue;
                }

                count++;
                if (returned >= maxResults)
                {
                    continue;
                }

                if (returned > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"title\":").Append(JsonUtil.String(window.titleContent != null ? window.titleContent.text : string.Empty));
                builder.Append(",\"window_index\":").Append(index);
                builder.Append(",\"type_name\":").Append(JsonUtil.String(window.GetType().FullName ?? window.GetType().Name));
                builder.Append(",\"is_focused\":").Append(JsonUtil.Bool(focusedWindow == window));
                AppendRuntimeObjectIds(builder, window);
                builder.Append(",\"position\":").Append(RectJson(window.position));
                builder.Append('}');
                returned++;
            }

            builder.Append("],\"count\":").Append(count);
            builder.Append(",\"returned\":").Append(returned);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(count > returned));
            builder.Append(",\"windows_hash\":").Append(JsonUtil.String(windowsHash));
            builder.Append(",\"compatible_with\":\"Unity.ManageEditor GetWindows\"}");
            return builder.ToString();
        }

        internal static string EditorWindowsHash(out int count)
        {
            var windows = UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>() ?? new EditorWindow[0];
            var focusedWindow = EditorWindow.focusedWindow;
            return EditorWindowsHash(windows, focusedWindow, out count);
        }

        internal static string EditorWindowsHash(EditorWindow[] windows, EditorWindow focusedWindow, out int count)
        {
            uint hash = 2166136261;
            count = 0;

            for (var index = 0; index < windows.Length && index < MaxWindowLimit; index++)
            {
                var window = windows[index];
                if (window == null)
                {
                    continue;
                }

                count++;
                UpdateHash(ref hash, index.ToString(CultureInfo.InvariantCulture));
                UpdateHash(ref hash, EditorWindowSelector.RuntimeIdentity(window));
                UpdateHash(ref hash, window.titleContent != null ? window.titleContent.text : string.Empty);
                UpdateHash(ref hash, window.GetType().FullName ?? window.GetType().Name);
                UpdateHash(ref hash, focusedWindow == window ? "focused" : "unfocused");
                UpdateHash(ref hash, RectJson(window.position));
            }

            return "fnv1a32:" + hash.ToString("x8", CultureInfo.InvariantCulture);
        }

        static void UpdateHash(ref uint hash, string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                hash ^= (uint)value[index];
                hash *= 16777619;
            }

            hash ^= 31;
            hash *= 16777619;
        }

        static string RectJson(UnityEngine.Rect value)
        {
            return "{\"x\":" + FloatJson(value.x)
                + ",\"y\":" + FloatJson(value.y)
                + ",\"width\":" + FloatJson(value.width)
                + ",\"height\":" + FloatJson(value.height) + "}";
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value)
                ? JsonUtil.String(value.ToString(CultureInfo.InvariantCulture))
                : value.ToString(CultureInfo.InvariantCulture);
        }

        static void AppendSelectedObject(StringBuilder builder, UnityEngine.Object selectedObject, string assetPath = null, string scenePath = null, string objectPath = null)
        {
            if (selectedObject == null)
            {
                builder.Append("{\"name\":\"\",\"type\":\"\",\"asset_path\":\"\",\"instance_id\":0");
#if UNITY_6000_4_OR_NEWER
                builder.Append(",\"entity_id\":\"\"");
#endif
                builder.Append('}');
                return;
            }

            builder.Append("{\"name\":").Append(JsonUtil.String(selectedObject.name));
            builder.Append(",\"type\":").Append(JsonUtil.String(selectedObject.GetType().FullName));
            builder.Append(",\"asset_path\":").Append(JsonUtil.String(assetPath ?? AssetDatabase.GetAssetPath(selectedObject) ?? string.Empty));
            if (!string.IsNullOrEmpty(scenePath))
            {
                builder.Append(",\"scene_path\":").Append(JsonUtil.String(scenePath));
            }

            if (!string.IsNullOrEmpty(objectPath))
            {
                builder.Append(",\"object_path\":").Append(JsonUtil.String(objectPath));
            }

            AppendRuntimeObjectIds(builder, selectedObject);
            builder.Append('}');
        }

        static void AppendRuntimeObjectIds(StringBuilder builder, UnityEngine.Object selectedObject)
        {
#if UNITY_6000_4_OR_NEWER
            var entityId = UnityEngine.EntityId.ToULong(selectedObject.GetEntityId());
            var legacyInstanceId = entityId <= int.MaxValue ? (int)entityId : 0;
            builder.Append(",\"instance_id\":").Append(legacyInstanceId);
            builder.Append(",\"entity_id\":").Append(JsonUtil.String(entityId.ToString(CultureInfo.InvariantCulture)));
#else
            builder.Append(",\"instance_id\":").Append(selectedObject.GetInstanceID());
#endif
        }
    }
}
