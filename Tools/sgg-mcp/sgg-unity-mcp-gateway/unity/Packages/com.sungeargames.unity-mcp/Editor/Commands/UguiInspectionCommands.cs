using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using SGG.UnityMCP.Editor.Paths;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class UguiInspectionCommands
    {
        const string FeatureFlag = "ugui_inspection";
        const string VisualInspectionFeatureFlag = "ugui_visual_inspection";
        const string InteractionFeatureFlag = "ugui_interaction_tests";
        const string SafeWriteFeatureFlag = "ugui_safe_writes";
        const string PrefabLayoutOverrideSchemaJson = "{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\"},\"root_path\":{\"type\":\"string\"},\"size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"parent_size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchored_position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"note\":{\"type\":\"string\"}},\"required\":[\"prefab_path\",\"root_path\"],\"additionalProperties\":false}}";
        const string CanvasScanSchemaJson = "{\"type\":\"object\",\"properties\":{\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"include_inactive\":{\"type\":\"boolean\"},\"include_prefab_variants\":{\"type\":\"boolean\"},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"include_components\":{\"type\":\"boolean\"},\"include_events\":{\"type\":\"boolean\"},\"prefab_layout_overrides\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\"},\"root_path\":{\"type\":\"string\"},\"size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"parent_size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchored_position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"note\":{\"type\":\"string\"}},\"required\":[\"prefab_path\",\"root_path\"],\"additionalProperties\":false}}},\"additionalProperties\":false}";
        const string CanvasReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"format\":{\"type\":\"string\",\"enum\":[\"markdown\",\"json\",\"markdown_and_json\"]},\"include_nodes_table\":{\"type\":\"boolean\"},\"include_risks\":{\"type\":\"boolean\"}},\"required\":[\"scan_id\"],\"additionalProperties\":false}";
        const string InspectNodeSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"node_id\":{\"type\":\"string\"},\"include_serialized_fields\":{\"type\":\"boolean\"},\"include_events\":{\"type\":\"boolean\"},\"max_field_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":4096}},\"required\":[\"scan_id\",\"node_id\"],\"additionalProperties\":false}";
        const string ScreenDiffSchemaJson = "{\"type\":\"object\",\"properties\":{\"left_scan_id\":{\"type\":\"string\"},\"right_scan_id\":{\"type\":\"string\"},\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_components\":{\"type\":\"boolean\",\"default\":true},\"max_diffs\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"required\":[\"left_scan_id\",\"right_scan_id\"],\"additionalProperties\":false}";
        const string CanvasModifySchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"node_id\":{\"type\":\"string\"},\"active_self\":{\"type\":\"boolean\"},\"anchor_min\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchor_max\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchored_position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"size_delta\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"pivot\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"local_scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"local_euler_angles\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scan_id\",\"node_id\"],\"additionalProperties\":false}";
        const string UguiObjectCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"parent_path\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"active\":{\"type\":\"boolean\"},\"anchor_min\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchor_max\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"pivot\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchored_position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"size_delta\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"sibling_index\":{\"type\":\"integer\",\"minimum\":-1,\"maximum\":10000},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"scene_path\",\"parent_path\",\"name\",\"active\",\"anchor_min\",\"anchor_max\",\"pivot\",\"anchored_position\",\"size_delta\",\"sibling_index\",\"dry_run\"],\"additionalProperties\":false}";
        const string UguiComponentAddSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string ResourceReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"uri\":{\"type\":\"string\"},\"format\":{\"type\":\"string\",\"enum\":[\"text\",\"json\"]},\"max_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000}},\"required\":[\"uri\"],\"additionalProperties\":false}";
        const string BoundsSnapshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"include_inactive\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"scan_id\"],\"additionalProperties\":false}";
        const string InteractionSmokeSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"steps\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{\"node_id\":{\"type\":\"string\"},\"action\":{\"type\":\"string\",\"enum\":[\"focus\",\"click\",\"submit\",\"toggle\",\"type\"]},\"text\":{\"type\":\"string\",\"maxLength\":4096},\"toggle_value\":{\"type\":\"boolean\"},\"expected_interactable\":{\"type\":\"boolean\"}},\"required\":[\"node_id\",\"action\"],\"additionalProperties\":false}},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_execute\":{\"type\":\"boolean\",\"default\":false},\"allow_edit_mode\":{\"type\":\"boolean\",\"default\":false},\"max_steps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":32}},\"required\":[\"scan_id\",\"steps\"],\"additionalProperties\":false}";
        const string EventsExtractSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"event_types\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":64},\"include_target_paths\":{\"type\":\"boolean\"},\"include_method_signatures\":{\"type\":\"boolean\"},\"max_events\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000}},\"required\":[\"scan_id\"],\"additionalProperties\":false}";
        const string LayoutAnalyzeSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"screen_sizes\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"maxItems\":16},\"include_absolute_positions\":{\"type\":\"boolean\"},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"prefab_layout_overrides\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\"},\"root_path\":{\"type\":\"string\"},\"size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"parent_size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchored_position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"note\":{\"type\":\"string\"}},\"required\":[\"prefab_path\",\"root_path\"],\"additionalProperties\":false}}},\"required\":[\"scan_id\"],\"additionalProperties\":false}";
        const string NavigationAnalyzeSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"include_edges\":{\"type\":\"boolean\"},\"max_selectables\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_edges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"required\":[\"scan_id\"],\"additionalProperties\":false}";
        const string RaycastAnalyzeSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"include_overlaps\":{\"type\":\"boolean\"},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"required\":[\"scan_id\"],\"additionalProperties\":false}";
        const string ReferencesReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"include_sprites\":{\"type\":\"boolean\"},\"include_textures\":{\"type\":\"boolean\"},\"include_fonts\":{\"type\":\"boolean\"},\"include_materials\":{\"type\":\"boolean\"},\"include_scripts\":{\"type\":\"boolean\"},\"max_references\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"required\":[\"scan_id\"],\"additionalProperties\":false}";
        const string ScanOutputLimitJson = "{\"max_depth\":64,\"max_nodes\":5000,\"max_targets\":100}";
        const string ScreenDiffOutputLimitJson = "{\"max_diffs\":1000,\"source\":\"cached_scans\",\"read_only\":true}";
        const string CanvasModifyOutputLimitJson = "{\"max_nodes_modified\":1,\"dry_run_default\":true,\"requires_precondition_hash\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":false}";
        const string UguiSceneAuthoringOutputLimitJson = "{\"max_objects\":1,\"max_components\":1,\"dry_run_default\":true,\"loaded_scenes_only\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":false}";
        const string ResourceReadOutputLimitJson = "{\"max_chars\":1000000}";
        const string BoundsSnapshotOutputLimitJson = "{\"max_nodes\":5000,\"source\":\"cached_scan\",\"resource_storage\":\"process_memory\"}";
        const string InteractionSmokeOutputLimitJson = "{\"max_steps\":32,\"dry_run_default\":true,\"requires_confirm_execute\":true,\"requires_play_mode_by_default\":true,\"resource_storage\":\"process_memory\",\"files_written\":false}";
        const string EventsExtractOutputLimitJson = "{\"max_events\":2000}";
        const string NavigationAnalyzeOutputLimitJson = "{\"max_selectables\":5000,\"max_edges\":5000,\"max_findings\":250}";
        const string RaycastAnalyzeOutputLimitJson = "{\"max_nodes\":5000,\"max_findings\":250,\"max_overlap_checks\":20000}";
        const string ReferencesReportOutputLimitJson = "{\"max_references\":5000,\"max_cached_references\":10000}";
        const string PlaceholderOutputLimitJson = "{\"max_items\":0}";
        const int MaxCachedScans = 16;
        const int MaxCachedResources = 64;
        const int MaxLayoutFindings = 250;
        const int MaxNavigationFindings = 250;
        const int MaxRaycastFindings = 250;
        const int MaxRaycastOverlapChecks = 20000;
        const int MaxAssetReferencesPerScan = 10000;

        static readonly object ScanCacheLock = new object();
        static readonly Dictionary<string, ScanSnapshot> ScanCache = new Dictionary<string, ScanSnapshot>();
        static readonly Queue<string> ScanCacheOrder = new Queue<string>();
        static readonly object ResourceCacheLock = new object();
        static readonly Dictionary<string, UguiResourceSnapshot> ResourceCache = new Dictionary<string, UguiResourceSnapshot>();
        static readonly Queue<string> ResourceCacheOrder = new Queue<string>();

        static readonly string[] UguiComponentTypeNames = new string[]
        {
            "UnityEngine.UI.CanvasScaler",
            "UnityEngine.UI.GraphicRaycaster",
            "UnityEngine.UI.Image",
            "UnityEngine.UI.RawImage",
            "UnityEngine.UI.Text",
            "TMPro.TMP_Text",
            "UnityEngine.UI.Graphic",
            "UnityEngine.UI.Button",
            "UnityEngine.UI.Toggle",
            "UnityEngine.UI.Slider",
            "UnityEngine.UI.Scrollbar",
            "UnityEngine.UI.InputField",
            "TMPro.TMP_InputField",
            "UnityEngine.UI.Dropdown",
            "TMPro.TMP_Dropdown",
            "UnityEngine.UI.ScrollRect",
            "UnityEngine.UI.Selectable",
            "UnityEngine.UI.Mask",
            "UnityEngine.UI.RectMask2D",
            "UnityEngine.UI.HorizontalLayoutGroup",
            "UnityEngine.UI.VerticalLayoutGroup",
            "UnityEngine.UI.GridLayoutGroup",
            "UnityEngine.UI.ContentSizeFitter",
            "UnityEngine.UI.LayoutElement",
            "UnityEngine.UI.AspectRatioFitter",
            "UnityEngine.CanvasGroup",
            "UnityEngine.EventSystems.EventSystem",
            "UnityEngine.Animator"
        };

        static readonly string[] UguiAuthoringComponentTypeNames = new string[]
        {
            "UnityEngine.UI.Image",
            "UnityEngine.UI.RawImage",
            "UnityEngine.UI.Button",
            "UnityEngine.UI.Toggle",
            "UnityEngine.UI.Slider",
            "UnityEngine.UI.ScrollRect",
            "UnityEngine.UI.HorizontalLayoutGroup",
            "UnityEngine.UI.VerticalLayoutGroup",
            "UnityEngine.UI.GridLayoutGroup",
            "UnityEngine.UI.ContentSizeFitter",
            "UnityEngine.UI.LayoutElement",
            "UnityEngine.UI.AspectRatioFitter",
            "UnityEngine.CanvasGroup",
            "UnityEngine.UI.Mask",
            "UnityEngine.UI.RectMask2D",
            "TMPro.TextMeshProUGUI"
        };

        [McpCommand(
            "ugui.canvas.scan",
            Title = "uGUI canvas scan",
            Description = "Read a bounded uGUI Canvas and RectTransform snapshot from loaded scenes or prefab assets without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = CanvasScanSchemaJson,
            Tags = new string[] { "ugui", "canvas", "ui", "semantic-inspection" },
            Capability = "ugui_inspection",
            ResultShape = "bounded_tree",
            OutputLimitJson = ScanOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CanvasScan(McpCommandContext context)
        {
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 16), 0, 64);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 1000), 1, 5000);
            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", true);
            var includeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", true);
            var targets = ExtractStringArray(context.ArgsJson, "targets", 100);
            var overrides = ExtractPrefabLayoutOverrides(context.ArgsJson, 100);
            var scanId = "ugui_scan_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var state = new ScanState(maxDepth, maxNodes, includeInactive, includeComponents);
            var snapshot = new ScanSnapshot(scanId, targets, overrides);
            var targetResults = new StringBuilder();

            if (targets.Count == 0)
            {
                for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount && state.NodesIncluded < maxNodes; sceneIndex++)
                {
                    var scene = SceneManager.GetSceneAt(sceneIndex);
                    if (scene.IsValid() && scene.isLoaded)
                    {
                        AppendTargetScan(targetResults, scene.path, "loaded_scene", scene.GetRootGameObjects(), state, snapshot);
                    }
                }
            }
            else
            {
                foreach (var target in targets)
                {
                    if (state.NodesIncluded >= maxNodes)
                    {
                        break;
                    }

                    if (!IsUnityRelativePath(target))
                    {
                        AppendTargetStatus(targetResults, target, "invalid", "target must be a Unity-relative Assets/... or Packages/... path", snapshot);
                        continue;
                    }

                    if (target.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(target);
                        if (prefab == null)
                        {
                            AppendTargetStatus(targetResults, target, "not_found", "prefab asset was not found", snapshot);
                            continue;
                        }

                        AppendPrefabPreviewTargetScan(targetResults, target, prefab, overrides, state, snapshot);
                        continue;
                    }

                    if (target.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
                    {
                        var scene = LoadedSceneByPath(target);
                        if (scene.IsValid() && scene.isLoaded)
                        {
                            AppendTargetScan(targetResults, target, "loaded_scene", scene.GetRootGameObjects(), state, snapshot);
                        }
                        else
                        {
                            AppendTargetStatus(targetResults, target, "skipped", "scene target is not loaded; this read-only iteration does not open scenes", snapshot);
                        }
                        continue;
                    }

                    AppendTargetStatus(targetResults, target, "skipped", "target type is not supported by this first uGUI read-only iteration", snapshot);
                }
            }

            snapshot.CanvasRoots = state.CanvasRoots;
            snapshot.NodesIncluded = state.NodesIncluded;
            snapshot.Truncated = state.NodesIncluded >= maxNodes || state.Truncated;
            StoreScan(snapshot);
            var scanResourceUri = ScanResourceUri(scanId);
            var geometryEvidenceJson = RectTransformGeometryEvidenceJson(snapshot, scanResourceUri, string.Empty);

            var resultJson = "{\"status\":\"ok\""
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"targets_found\":" + state.TargetsFound
                + ",\"canvas_roots\":" + state.CanvasRoots
                + ",\"nodes_included\":" + state.NodesIncluded
                + ",\"truncated\":" + JsonUtil.Bool(state.NodesIncluded >= maxNodes || state.Truncated)
                + ",\"targets\":[" + targetResults + "]"
                + ",\"prefab_layout_overrides\":" + PrefabLayoutOverridesJson(overrides)
                + ",\"resource_refs\":" + ResourceRefsJson(scanResourceUri)
                + ",\"geometry_evidence\":" + geometryEvidenceJson
                + ",\"next_valid_actions\":[\"ugui.resource.read\",\"ugui.canvas.report\",\"ugui.canvas.inspect_node\",\"ugui.bounds.snapshot\",\"ugui.canvas.modify\",\"ugui.object.create\",\"ugui.component.add\",\"ugui.events.extract\",\"ugui.layout.analyze\",\"ugui.navigation.analyze\",\"ugui.raycast.analyze\",\"ugui.references.report\",\"ugui.interaction.smoke\"]}";
            StoreResource(scanResourceUri, resultJson, "application/json");
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "ugui.canvas.report",
            Title = "uGUI canvas report",
            Description = "Read a bounded report for a previous uGUI scan cached in this Unity Bridge process",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CanvasReportSchemaJson,
            Tags = new string[] { "ugui", "canvas", "report" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = PlaceholderOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CanvasReport(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var reportResourceUri = ReportResourceUri(snapshot.ScanId);
            var geometryEvidenceJson = RectTransformGeometryEvidenceJson(snapshot, ScanResourceUri(snapshot.ScanId), string.Empty);
            var resultJson = "{\"status\":\"ok\""
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"cache\":{\"storage\":\"memory\",\"max_scans\":" + MaxCachedScans + "}"
                + ",\"source_targets\":" + StringArrayJson(snapshot.SourceTargets)
                + ",\"targets\":" + TargetSummariesJson(snapshot.Targets)
                + ",\"canvas_roots\":" + snapshot.CanvasRoots
                + ",\"nodes_included\":" + snapshot.NodesIncluded
                + ",\"truncated\":" + JsonUtil.Bool(snapshot.Truncated)
                + ",\"component_counts\":" + CountsJson(snapshot.ComponentCounts)
                + ",\"type_counts\":" + CountsJson(snapshot.ComponentCounts)
                + ",\"missing_scripts\":" + snapshot.MissingScripts
                + ",\"override_summary\":" + OverrideSummaryJson(snapshot.Overrides)
                + ",\"summary\":" + JsonUtil.String("Cached uGUI scan report for " + snapshot.NodesIncluded + " node(s)")
                + ",\"resource_refs\":" + ResourceRefsJson(reportResourceUri)
                + ",\"geometry_evidence\":" + geometryEvidenceJson
                + ",\"artifact_refs\":[]"
                + ",\"read_only\":true"
                + ",\"next_valid_actions\":[\"ugui.resource.read\",\"ugui.bounds.snapshot\",\"ugui.canvas.inspect_node\",\"ugui.layout.analyze\",\"ugui.raycast.analyze\"]}";
            StoreResource(reportResourceUri, resultJson, "application/json");
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "ugui.canvas.inspect_node",
            Title = "uGUI inspect node",
            Description = "Read focused information for one node from a previous uGUI scan cached in this Unity Bridge process",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = InspectNodeSchemaJson,
            Tags = new string[] { "ugui", "canvas", "node" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = PlaceholderOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult InspectNode(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var nodeId = JsonUtil.ExtractString(context.ArgsJson, "node_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            NodeSnapshot node;
            if (string.IsNullOrEmpty(nodeId) || !snapshot.NodesById.TryGetValue(nodeId, out node))
            {
                return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"scan_id\":" + JsonUtil.String(scanId)
                    + ",\"node_id\":" + JsonUtil.String(nodeId)
                    + ",\"reason\":\"node_id was not found in cached scan\"}");
            }

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"node\":" + NodeInspectJson(node)
                + ",\"read_only\":true}");
        }

        [McpCommand(
            "ugui.screen.diff",
            Title = "uGUI screen diff",
            Description = "Read-only bounded diff between two cached uGUI scan snapshots by target and hierarchy path",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ScreenDiffSchemaJson,
            Tags = new string[] { "ugui", "screen", "diff", "read-only" },
            Capability = "ugui_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = ScreenDiffOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ScreenDiff(McpCommandContext context)
        {
            var leftScanId = JsonUtil.ExtractString(context.ArgsJson, "left_scan_id");
            var rightScanId = JsonUtil.ExtractString(context.ArgsJson, "right_scan_id");
            var left = FindScan(leftScanId);
            if (left == null) return MissingScan(leftScanId);
            var right = FindScan(rightScanId);
            if (right == null) return MissingScan(rightScanId);
            var pathPrefix = JsonUtil.ExtractString(context.ArgsJson, "path_prefix");
            if (!string.IsNullOrEmpty(pathPrefix)) pathPrefix = NormalizeObjectPath(pathPrefix);
            var includeComponents = JsonUtil.ExtractBool(context.ArgsJson, "include_components", true);
            var maxDiffs = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_diffs", 250), 1, 1000);
            var leftNodes = ScreenDiffNodeMap(left, pathPrefix);
            var rightNodes = ScreenDiffNodeMap(right, pathPrefix);
            return CommandExecutionResult.Ok(ScreenDiffJson(left, right, leftNodes, rightNodes, pathPrefix, includeComponents, maxDiffs));
        }

        [McpCommand(
            "ugui.canvas.modify",
            Title = "uGUI canvas modify",
            Description = "Dry-run-first focused uGUI loaded-scene RectTransform and active-state modification from a cached scan node",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = CanvasModifySchemaJson,
            Tags = new string[] { "ugui", "canvas", "rect-transform", "safe-write" },
            Capability = "ugui_safe_writes",
            ResultShape = "status",
            OutputLimitJson = CanvasModifyOutputLimitJson,
            FeatureFlag = SafeWriteFeatureFlag)]
        public static CommandExecutionResult CanvasModify(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var nodeId = JsonUtil.ExtractString(context.ArgsJson, "node_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            NodeSnapshot node;
            if (string.IsNullOrEmpty(nodeId) || !snapshot.NodesById.TryGetValue(nodeId, out node))
            {
                return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"scan_id\":" + JsonUtil.String(scanId)
                    + ",\"node_id\":" + JsonUtil.String(nodeId)
                    + ",\"reason\":\"node_id was not found in cached scan\"}");
            }

            var request = ExtractCanvasModifyRequest(context.ArgsJson);
            if (!request.HasAnyField)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "At least one allowlisted field is required: active_self, anchor_min, anchor_max, anchored_position, size_delta, pivot, local_scale, or local_euler_angles");
            }

            var targetResult = ValidateCanvasModifyTarget(snapshot, node, request, out var gameObject, out var rectTransform, out var scenePath, out var scene);
            if (targetResult != null)
            {
                return targetResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var current = CaptureCanvasModifyState(gameObject, rectTransform);
            var requested = RequestedCanvasModifyState(current, request);
            var changedFields = CanvasModifyChangedFields(current, requested, request);
            var changed = changedFields.Count > 0;
            var preconditionHash = CanvasModifyPreconditionHash(snapshot, node, scenePath, current, requested);

            if (!dryRun)
            {
                var editingResult = RequireCanvasModifyEditing();
                if (editingResult != null)
                {
                    return editingResult;
                }

                if (changed)
                {
                    var preconditionResult = RequireCanvasModifyPrecondition(context.ArgsJson, scanId, node, preconditionHash);
                    if (preconditionResult != null)
                    {
                        return preconditionResult;
                    }

                    ApplyCanvasModify(gameObject, rectTransform, current, requested, changedFields);
                    EditorSceneManager.MarkSceneDirty(scene);
                    Canvas.ForceUpdateCanvases();
                }
            }

            return CommandExecutionResult.Ok(CanvasModifyResultJson(scanId, node, scenePath, dryRun, changed, changedFields, preconditionHash, current, requested, scene.isDirty));
        }

        [McpCommand(
            "ugui.object.create",
            Title = "uGUI object create",
            Description = "Dry-run-first loaded-scene uGUI RectTransform child creation under an existing Canvas or RectTransform parent",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = UguiObjectCreateSchemaJson,
            Tags = new string[] { "ugui", "rect-transform", "scene", "safe-write" },
            Capability = "ugui_safe_writes",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = UguiSceneAuthoringOutputLimitJson,
            FeatureFlag = SafeWriteFeatureFlag)]
        public static CommandExecutionResult UguiObjectCreate(McpCommandContext context)
        {
            var requestResult = ExtractUguiObjectCreateRequest(context.ArgsJson, out var request);
            if (requestResult != null)
            {
                return requestResult;
            }

            var sceneResult = RequireLoadedUguiScene(context.ArgsJson, out var scene, out var scenePath);
            if (sceneResult != null)
            {
                return sceneResult;
            }

            var parentResult = RequireUguiParent(context.ArgsJson, scene, out var parent, out var parentPath);
            if (parentResult != null)
            {
                return parentResult;
            }

            var siblingCollision = SiblingNameExists(parent.transform, request.Name, null);
            var plannedObjectPath = ChildObjectPath(parent, request.Name);
            if (request.DryRun)
            {
                return CommandExecutionResult.Ok(UguiObjectCreateResultJson(scene, scenePath, parent, parentPath, plannedObjectPath, request, false, !siblingCollision, siblingCollision, null));
            }

            if (siblingCollision)
            {
                return CommandExecutionResult.Error("invalid_field_value", "A sibling with this name already exists", "{\"command\":\"ugui.object.create\",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"parent_path\":" + JsonUtil.String(parentPath) + ",\"planned_object_path\":" + JsonUtil.String(plannedObjectPath) + ",\"sibling_collision\":true}");
            }

            var editingResult = RequireUguiSceneAuthoringEditing("ugui.object.create");
            if (editingResult != null)
            {
                return editingResult;
            }

            var gameObject = new GameObject(request.Name, typeof(RectTransform));
            Undo.RegisterCreatedObjectUndo(gameObject, "Create uGUI Object");
            SceneManager.MoveGameObjectToScene(gameObject, scene);
            Undo.SetTransformParent(gameObject.transform, parent.transform, "Parent uGUI Object");

            var rectTransform = gameObject.GetComponent<RectTransform>();
            Undo.RecordObject(rectTransform, "Configure uGUI RectTransform");
            ApplyRectTransformCreateState(rectTransform, request);
            if (request.SiblingIndex >= 0)
            {
                rectTransform.SetSiblingIndex(Math.Min(request.SiblingIndex, parent.transform.childCount - 1));
            }

            if (!request.Active)
            {
                Undo.RecordObject(gameObject, "Set uGUI Object Active");
                gameObject.SetActive(false);
            }

            EditorSceneManager.MarkSceneDirty(scene);
            Canvas.ForceUpdateCanvases();
            return CommandExecutionResult.Ok(UguiObjectCreateResultJson(scene, scenePath, parent, parentPath, ObjectPath(gameObject), request, true, false, false, gameObject));
        }

        [McpCommand(
            "ugui.component.add",
            Title = "uGUI component add",
            Description = "Dry-run-first loaded-scene add of one allowlisted Unity UI or TextMeshPro MonoBehaviour component to an existing uGUI object",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = UguiComponentAddSchemaJson,
            Tags = new string[] { "ugui", "component", "scene", "safe-write" },
            Capability = "ugui_safe_writes",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "object_details",
            OutputLimitJson = UguiSceneAuthoringOutputLimitJson,
            FeatureFlag = SafeWriteFeatureFlag)]
        public static CommandExecutionResult UguiComponentAdd(McpCommandContext context)
        {
            var sceneResult = RequireLoadedUguiScene(context.ArgsJson, out var scene, out var scenePath);
            if (sceneResult != null)
            {
                return sceneResult;
            }

            var objectResult = RequireSceneObject(context.ArgsJson, scene, out var target, out var objectPath);
            if (objectResult != null)
            {
                return objectResult;
            }

            var targetResult = RequireUguiSceneObject(target, objectPath);
            if (targetResult != null)
            {
                return targetResult;
            }

            var typeResult = ResolveAllowedUguiAuthoringComponentType(context.ArgsJson, out var componentType);
            if (typeResult != null)
            {
                return typeResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (dryRun)
            {
                return CommandExecutionResult.Ok(UguiComponentAddResultJson(scene, scenePath, target, objectPath, componentType, null, -1, true));
            }

            var editingResult = RequireUguiSceneAuthoringEditing("ugui.component.add");
            if (editingResult != null)
            {
                return editingResult;
            }

            var component = Undo.AddComponent(target, componentType);
            if (component == null)
            {
                return CommandExecutionResult.Error("unity_exception", "uGUI component add failed");
            }

            EditorUtility.SetDirty(target);
            EditorUtility.SetDirty(component);
            EditorSceneManager.MarkSceneDirty(scene);
            Canvas.ForceUpdateCanvases();
            return CommandExecutionResult.Ok(UguiComponentAddResultJson(scene, scenePath, target, objectPath, componentType, component, ComponentIndex(target, component), false));
        }

        [McpCommand(
            "ugui.resource.read",
            Title = "uGUI resource read",
            Description = "Read a bounded process-local uGUI resource emitted by previous uGUI scan, report, bounds, layout, events, navigation, raycast, or references commands",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ResourceReadSchemaJson,
            Tags = new string[] { "ugui", "resource", "cache" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = ResourceReadOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ResourceRead(McpCommandContext context)
        {
            var uri = JsonUtil.ExtractString(context.ArgsJson, "uri");
            var format = JsonUtil.ExtractString(context.ArgsJson, "format");
            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 200000), 1, 1000000);
            if (!IsUguiResourceUri(uri))
            {
                return CommandExecutionResult.Ok("{\"status\":\"invalid\",\"uri\":" + JsonUtil.String(uri)
                    + ",\"reason\":\"uri must be a unity-ui://ugui/... resource emitted by this Unity Bridge process\""
                    + ",\"read_only\":true}");
            }

            var resource = FindResource(uri);
            if (resource == null)
            {
                return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"uri\":" + JsonUtil.String(uri)
                    + ",\"reason\":\"resource was not found in this Unity Bridge process in-memory cache\""
                    + ",\"cache\":{\"storage\":\"memory\",\"max_resources\":" + MaxCachedResources + "}}");
            }

            var content = resource.Content;
            var returned = Math.Min(maxChars, content.Length);
            var truncated = returned < content.Length;
            var returnedContent = truncated ? content.Substring(0, returned) : content;
            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"uri\":" + JsonUtil.String(resource.Uri)
                + ",\"format\":" + JsonUtil.String(string.IsNullOrEmpty(format) ? "text" : format)
                + ",\"mime_type\":" + JsonUtil.String(resource.MimeType)
                + ",\"content\":" + JsonUtil.String(returnedContent)
                + ",\"chars\":" + returned
                + ",\"total_chars\":" + content.Length
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"warnings\":" + ResourceWarningsJson(truncated)
                + ",\"resource_refs\":[]"
                + ",\"artifact_refs\":[]"
                + ",\"read_only\":true"
                + ",\"risk\":\"low\""
                + ",\"requires_approval\":false}");
        }

        [McpCommand(
            "ugui.bounds.snapshot",
            Title = "uGUI bounds snapshot",
            Description = "Read cached RectTransform world bounds for a previous uGUI scan and bind them to the scan resource without capturing screenshots or mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = BoundsSnapshotSchemaJson,
            Tags = new string[] { "ugui", "bounds", "rect-transform", "visual-inspection" },
            Capability = "ugui_visual_inspection",
            ResultShape = "bounded_list",
            OutputLimitJson = BoundsSnapshotOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = VisualInspectionFeatureFlag)]
        public static CommandExecutionResult BoundsSnapshot(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", snapshot.NodesIncluded), 1, 5000);
            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", true);
            var boundsId = "ugui_bounds_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var resourceUri = BoundsSnapshotResourceUri(boundsId);
            var resultJson = BoundsSnapshotJson(snapshot, boundsId, maxNodes, includeInactive, resourceUri);
            StoreResource(resourceUri, resultJson, "application/json");
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "ugui.interaction.smoke",
            Title = "uGUI interaction smoke",
            Description = "Dry-run-first uGUI interaction smoke plan with explicit opt-in Play Mode execution for bounded focus, click, submit, toggle, and type steps from a cached uGUI scan",
            Risk = "execute",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = InteractionSmokeSchemaJson,
            Tags = new string[] { "ugui", "interaction", "smoke", "playmode" },
            Capability = "ugui_interaction_tests",
            ResultShape = "status",
            OutputLimitJson = InteractionSmokeOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InteractionFeatureFlag)]
        public static CommandExecutionResult InteractionSmoke(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var maxSteps = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_steps", 32), 1, 32);
            var steps = ExtractInteractionSteps(context.ArgsJson, maxSteps);
            if (steps.Count == 0)
            {
                return CommandExecutionResult.Ok("{\"status\":\"invalid\",\"scan_id\":" + JsonUtil.String(scanId)
                    + ",\"reason\":\"steps must contain at least one bounded interaction step\""
                    + ",\"dry_run\":true"
                    + ",\"executed\":false"
                    + ",\"callbacks_may_mutate_game_state\":true"
                    + ",\"writes_files\":false"
                    + ",\"resource_refs\":[]"
                    + ",\"artifact_refs\":[]}");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var confirmExecute = JsonUtil.ExtractBool(context.ArgsJson, "confirm_execute", false);
            var allowEditMode = JsonUtil.ExtractBool(context.ArgsJson, "allow_edit_mode", false);
            if (!dryRun && !confirmExecute)
            {
                return CommandExecutionResult.Error(
                    "invalid_field_value",
                    "confirm_execute must be true when ugui.interaction.smoke runs with dry_run:false",
                    "{\"command\":\"ugui.interaction.smoke\",\"required_arg\":\"confirm_execute\",\"dry_run_default\":true,\"callbacks_may_mutate_game_state\":true}");
            }

            if (!dryRun && EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying)
            {
                return CommandExecutionResult.Ok(InteractionSmokeBlockedJson(scanId, "playmode_transition", dryRun, allowEditMode, "Unity Editor is changing Play Mode state; retry after the transition completes."));
            }

            if (!dryRun && !EditorApplication.isPlaying && !allowEditMode)
            {
                return CommandExecutionResult.Ok(InteractionSmokeBlockedJson(scanId, "playmode_required", dryRun, allowEditMode, "Non-dry-run interaction smoke requires Play Mode unless allow_edit_mode:true is explicitly provided for an editor test context."));
            }

            var eventSystem = FindCurrentEventSystem();
            var stepResults = BuildInteractionStepResults(snapshot, steps, eventSystem);
            var canExecuteAll = InteractionStepsCanExecute(stepResults, eventSystem);
            if (!dryRun && !canExecuteAll)
            {
                var blockedId = "ugui_interaction_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
                var blockedResourceUri = InteractionSmokeResourceUri(blockedId);
                var blockedJson = InteractionSmokeResultJson(snapshot, blockedId, dryRun, false, allowEditMode, eventSystem, "preflight_failed", stepResults, 0, blockedResourceUri, "One or more requested interaction steps could not be executed safely.");
                StoreResource(blockedResourceUri, blockedJson, "application/json");
                return CommandExecutionResult.Ok(blockedJson);
            }

            var executedSteps = 0;
            if (!dryRun)
            {
                for (var index = 0; index < stepResults.Count; index++)
                {
                    var result = stepResults[index];
                    if (!TryExecuteInteractionStep(result, eventSystem, out var executionStatus, out var executionMessage))
                    {
                        result.Status = executionStatus;
                        result.Message = executionMessage;
                        result.Executed = false;
                        result.CanExecute = false;
                        break;
                    }

                    result.Status = "executed";
                    result.Message = executionMessage;
                    result.Executed = true;
                    executedSteps++;
                }
            }

            var smokeId = "ugui_interaction_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var resourceUri = InteractionSmokeResourceUri(smokeId);
            var status = dryRun ? "dry_run" : executedSteps == stepResults.Count ? "ok" : "partial";
            var resultJson = InteractionSmokeResultJson(snapshot, smokeId, dryRun, !dryRun, allowEditMode, eventSystem, status, stepResults, executedSteps, resourceUri, string.Empty);
            StoreResource(resourceUri, resultJson, "application/json");
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "ugui.events.extract",
            Title = "uGUI events extract",
            Description = "Read bounded persistent UnityEvent bindings for a previous uGUI scan cached in this Unity Bridge process",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = EventsExtractSchemaJson,
            Tags = new string[] { "ugui", "events", "unityevent" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = EventsExtractOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EventsExtract(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var eventTypes = ExtractStringArray(context.ArgsJson, "event_types", 64);
            var includeTargetPaths = JsonUtil.ExtractBool(context.ArgsJson, "include_target_paths", true);
            var includeMethodSignatures = JsonUtil.ExtractBool(context.ArgsJson, "include_method_signatures", false);
            var maxEvents = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_events", 2000), 1, 2000);
            return CommandExecutionResult.Ok(EventsExtractJson(snapshot, eventTypes, includeTargetPaths, includeMethodSignatures, maxEvents));
        }

        [McpCommand(
            "ugui.layout.analyze",
            Title = "uGUI layout analyze",
            Description = "Read bounded layout analysis findings for a previous uGUI scan cached in this Unity Bridge process",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = LayoutAnalyzeSchemaJson,
            Tags = new string[] { "ugui", "layout", "canvas" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = PlaceholderOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult LayoutAnalyze(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var screenSizes = ExtractScreenSizes(context.ArgsJson, 16);
            if (screenSizes.Count == 0)
            {
                screenSizes.Add(new Vector2(1920, 1080));
            }
            var includeAbsolutePositions = JsonUtil.ExtractBool(context.ArgsJson, "include_absolute_positions", false);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", snapshot.NodesIncluded), 1, 5000);
            var requestOverrides = ExtractPrefabLayoutOverrides(context.ArgsJson, 100);

            return CommandExecutionResult.Ok(LayoutAnalysisJson(snapshot, screenSizes, includeAbsolutePositions, maxNodes, requestOverrides));
        }

        [McpCommand(
            "ugui.navigation.analyze",
            Title = "uGUI navigation analyze",
            Description = "Read bounded Selectable navigation graph diagnostics for a previous uGUI scan cached in this Unity Bridge process",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = NavigationAnalyzeSchemaJson,
            Tags = new string[] { "ugui", "navigation", "selectable", "input" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = NavigationAnalyzeOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult NavigationAnalyze(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var includeEdges = JsonUtil.ExtractBool(context.ArgsJson, "include_edges", true);
            var maxSelectables = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_selectables", snapshot.NavigationNodes.Count == 0 ? 1 : snapshot.NavigationNodes.Count), 1, 5000);
            var maxEdges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_edges", 1000), 1, 5000);
            return CommandExecutionResult.Ok(NavigationAnalysisJson(snapshot, includeEdges, maxSelectables, maxEdges));
        }

        [McpCommand(
            "ugui.raycast.analyze",
            Title = "uGUI raycast analyze",
            Description = "Read bounded Graphic raycast and click-blocker diagnostics for a previous uGUI scan cached in this Unity Bridge process",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = RaycastAnalyzeSchemaJson,
            Tags = new string[] { "ugui", "raycast", "graphic", "input" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = RaycastAnalyzeOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult RaycastAnalyze(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var includeOverlaps = JsonUtil.ExtractBool(context.ArgsJson, "include_overlaps", true);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", snapshot.RaycastGraphics.Count == 0 ? 1 : snapshot.RaycastGraphics.Count), 1, 5000);
            return CommandExecutionResult.Ok(RaycastAnalysisJson(snapshot, includeOverlaps, maxNodes));
        }

        [McpCommand(
            "ugui.references.report",
            Title = "uGUI references report",
            Description = "Read a bounded uGUI-focused asset reference report for sprites, textures, fonts, materials, and scripts from a cached scan",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ReferencesReportSchemaJson,
            Tags = new string[] { "ugui", "references", "assets", "scripts" },
            Capability = "ugui_inspection",
            ResultShape = "status",
            OutputLimitJson = ReferencesReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ReferencesReport(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            var snapshot = FindScan(scanId);
            if (snapshot == null)
            {
                return MissingScan(scanId);
            }

            var includeSprites = JsonUtil.ExtractBool(context.ArgsJson, "include_sprites", true);
            var includeTextures = JsonUtil.ExtractBool(context.ArgsJson, "include_textures", true);
            var includeFonts = JsonUtil.ExtractBool(context.ArgsJson, "include_fonts", true);
            var includeMaterials = JsonUtil.ExtractBool(context.ArgsJson, "include_materials", true);
            var includeScripts = JsonUtil.ExtractBool(context.ArgsJson, "include_scripts", true);
            var maxReferences = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_references", 1000), 1, 5000);
            return CommandExecutionResult.Ok(ReferencesReportJson(snapshot, includeSprites, includeTextures, includeFonts, includeMaterials, includeScripts, maxReferences));
        }

        static CommandExecutionResult Placeholder(string command, string reason)
        {
            return CommandExecutionResult.Ok("{\"status\":\"not_implemented\",\"command\":" + JsonUtil.String(command) + ",\"read_only\":true,\"reason\":" + JsonUtil.String(reason) + ",\"resource_refs\":[]}");
        }

        static CommandExecutionResult ExtractUguiObjectCreateRequest(string json, out UguiObjectCreateRequest request)
        {
            request = new UguiObjectCreateRequest();
            var nameResult = RequireObjectName(json, "name", out request.Name);
            if (nameResult != null)
            {
                return nameResult;
            }

            if (!HasJsonProperty(json, "active"))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument active is required");
            }
            request.Active = JsonUtil.ExtractBool(json, "active", true);

            if (!HasJsonProperty(json, "dry_run"))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument dry_run is required and defaults to true in the schema");
            }
            request.DryRun = JsonUtil.ExtractBool(json, "dry_run", true);

            var siblingResult = RequireIntInRange(json, "sibling_index", -1, 10000, out request.SiblingIndex);
            if (siblingResult != null)
            {
                return siblingResult;
            }

            var vectorResult = RequireVector2(json, "anchor_min", out request.AnchorMin)
                ?? RequireVector2(json, "anchor_max", out request.AnchorMax)
                ?? RequireVector2(json, "pivot", out request.Pivot)
                ?? RequireVector2(json, "anchored_position", out request.AnchoredPosition)
                ?? RequireVector2(json, "size_delta", out request.SizeDelta);
            return vectorResult;
        }

        static CommandExecutionResult RequireLoadedUguiScene(string argsJson, out Scene scene, out string scenePath)
        {
            scene = default;
            scenePath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, "scene_path");
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument scene_path is required");
            }

            try
            {
                scenePath = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "scene_path must be a Unity-relative Assets/... or Packages/... path without traversal", "{\"field\":\"scene_path\",\"reason\":" + JsonUtil.String(exception.Message) + "}");
            }

            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "scene_path must end with .unity");
            }

            scene = LoadedSceneByPath(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Error("asset_not_found", "scene_path must reference a loaded scene");
            }

            return null;
        }

        static CommandExecutionResult RequireUguiParent(string argsJson, Scene scene, out GameObject parent, out string parentPath)
        {
            parent = null;
            parentPath = JsonUtil.ExtractString(argsJson, "parent_path");
            if (string.IsNullOrEmpty(parentPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument parent_path is required");
            }

            var pathResult = ValidateHierarchyPath(parentPath, "parent_path");
            if (pathResult != null)
            {
                return pathResult;
            }

            parentPath = NormalizeObjectPath(parentPath);
            parent = FindByPath(scene.GetRootGameObjects(), parentPath);
            if (parent == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "parent_path was not found in the loaded scene");
            }

            return RequireUguiSceneObject(parent, parentPath);
        }

        static CommandExecutionResult RequireSceneObject(string argsJson, Scene scene, out GameObject target, out string objectPath)
        {
            target = null;
            objectPath = JsonUtil.ExtractString(argsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            var pathResult = ValidateHierarchyPath(objectPath, "object_path");
            if (pathResult != null)
            {
                return pathResult;
            }

            objectPath = NormalizeObjectPath(objectPath);
            target = FindByPath(scene.GetRootGameObjects(), objectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "object_path was not found in the loaded scene");
            }

            return null;
        }

        static CommandExecutionResult RequireUguiSceneObject(GameObject gameObject, string objectPath)
        {
            if (gameObject == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "uGUI object was not found");
            }

            if (gameObject.GetComponent<RectTransform>() == null && gameObject.GetComponent<Canvas>() == null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "uGUI scene authoring requires a loaded-scene GameObject with RectTransform or Canvas", "{\"object_path\":" + JsonUtil.String(objectPath) + ",\"requires\":[\"RectTransform\",\"Canvas\"]}");
            }

            return null;
        }

        static CommandExecutionResult RequireUguiSceneAuthoringEditing(string command)
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_busy", command + " requires editing status; Unity is compiling");
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", command + " requires Edit Mode; Unity is in or entering Play Mode");
            }

            return null;
        }

        static CommandExecutionResult ValidateHierarchyPath(string path, string field)
        {
            if (path.Length > 512 || path.IndexOf('\0') >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be 512 characters or fewer and cannot contain NUL");
            }

            if (path.IndexOf("..", StringComparison.Ordinal) >= 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " cannot contain traversal segments");
            }

            return null;
        }

        static CommandExecutionResult RequireObjectName(string argsJson, string field, out string name)
        {
            name = JsonUtil.ExtractString(argsJson, field);
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

        static CommandExecutionResult RequireIntInRange(string argsJson, string field, int minimum, int maximum, out int value)
        {
            value = 0;
            if (!HasJsonProperty(argsJson, field))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            value = JsonUtil.ExtractInt(argsJson, field, minimum - 1);
            if (value < minimum || value > maximum)
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be between " + minimum + " and " + maximum);
            }

            return null;
        }

        static CommandExecutionResult RequireVector2(string argsJson, string field, out Vector2 value)
        {
            value = Vector2.zero;
            if (!HasJsonProperty(argsJson, field))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            if (!TryExtractVector2(argsJson, field, out value) || !IsFinite(value))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must be a finite two-number array");
            }

            return null;
        }

        static void ApplyRectTransformCreateState(RectTransform rectTransform, UguiObjectCreateRequest request)
        {
            rectTransform.anchorMin = request.AnchorMin;
            rectTransform.anchorMax = request.AnchorMax;
            rectTransform.pivot = request.Pivot;
            rectTransform.anchoredPosition = request.AnchoredPosition;
            rectTransform.sizeDelta = request.SizeDelta;
            rectTransform.localScale = Vector3.one;
            rectTransform.localEulerAngles = Vector3.zero;
        }

        static CommandExecutionResult ResolveAllowedUguiAuthoringComponentType(string argsJson, out Type componentType)
        {
            componentType = null;
            var requestedType = JsonUtil.ExtractString(argsJson, "component_type").Trim();
            var fullTypeName = JsonUtil.ExtractString(argsJson, "full_type_name").Trim();
            var assemblyName = JsonUtil.ExtractString(argsJson, "assembly_name").Trim();
            if (string.IsNullOrEmpty(requestedType) && string.IsNullOrEmpty(fullTypeName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument component_type or full_type_name is required");
            }

            if (!string.IsNullOrEmpty(fullTypeName) && !IsAllowedUguiAuthoringFullTypeName(fullTypeName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "component type is not allowlisted for ugui.component.add", UguiAuthoringAllowlistJson());
            }

            var matches = new List<Type>();
            for (var index = 0; index < UguiAuthoringComponentTypeNames.Length; index++)
            {
                var allowedFullName = UguiAuthoringComponentTypeNames[index];
                if (!string.IsNullOrEmpty(fullTypeName) && !string.Equals(fullTypeName, allowedFullName, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(requestedType) && !MatchesRequestedUguiComponentType(allowedFullName, requestedType))
                {
                    continue;
                }

                var type = FindTypeByFullName(allowedFullName, assemblyName);
                if (type != null)
                {
                    matches.Add(type);
                }
            }

            if (matches.Count == 0)
            {
                if (!string.IsNullOrEmpty(requestedType) && !MatchesAnyAllowedUguiComponentType(requestedType))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "component type is not allowlisted for ugui.component.add", UguiAuthoringAllowlistJson());
                }

                return CommandExecutionResult.Error("asset_not_found", "uGUI component type was not found in loaded assemblies or did not match assembly_name", UguiAuthoringAllowlistJson());
            }

            if (matches.Count > 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", "component_type is ambiguous; use full_type_name and assembly_name", UguiComponentTypeCandidatesJson(matches));
            }

            componentType = matches[0];
            if (componentType.IsAbstract || !typeof(Component).IsAssignableFrom(componentType))
            {
                return CommandExecutionResult.Error("invalid_field_value", "resolved type must be a concrete Unity Component");
            }

            if (!IsAllowedUguiAuthoringFullTypeName(componentType.FullName ?? componentType.Name))
            {
                return CommandExecutionResult.Error("invalid_field_value", "arbitrary MonoBehaviour components are not allowlisted by ugui.component.add", UguiAuthoringAllowlistJson());
            }

            return null;
        }

        static bool MatchesRequestedUguiComponentType(string allowedFullName, string requestedType)
        {
            return string.Equals(allowedFullName, requestedType, StringComparison.Ordinal)
                || string.Equals(ShortTypeName(allowedFullName), requestedType, StringComparison.Ordinal);
        }

        static bool MatchesAnyAllowedUguiComponentType(string requestedType)
        {
            for (var index = 0; index < UguiAuthoringComponentTypeNames.Length; index++)
            {
                if (MatchesRequestedUguiComponentType(UguiAuthoringComponentTypeNames[index], requestedType))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsAllowedUguiAuthoringFullTypeName(string fullTypeName)
        {
            for (var index = 0; index < UguiAuthoringComponentTypeNames.Length; index++)
            {
                if (string.Equals(fullTypeName, UguiAuthoringComponentTypeNames[index], StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static Type FindTypeByFullName(string fullTypeName, string assemblyName)
        {
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                if (!string.IsNullOrEmpty(assemblyName) && !string.Equals(assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type != null && string.Equals(type.FullName ?? type.Name, fullTypeName, StringComparison.Ordinal))
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        static Type[] SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types ?? new Type[0];
            }
            catch
            {
                return new Type[0];
            }
        }

        static string ShortTypeName(string fullTypeName)
        {
            var separator = fullTypeName.LastIndexOf('.');
            return separator >= 0 && separator < fullTypeName.Length - 1 ? fullTypeName.Substring(separator + 1) : fullTypeName;
        }

        static string UguiObjectCreateResultJson(Scene scene, string scenePath, GameObject parent, string parentPath, string objectPath, UguiObjectCreateRequest request, bool created, bool wouldCreate, bool siblingCollision, GameObject gameObject)
        {
            return "{\"status\":" + JsonUtil.String(created ? "ok" : wouldCreate ? "dry_run" : "blocked")
                + ",\"command\":\"ugui.object.create\""
                + ",\"dry_run\":" + JsonUtil.Bool(request.DryRun)
                + ",\"created\":" + JsonUtil.Bool(created)
                + ",\"would_create\":" + JsonUtil.Bool(wouldCreate)
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"parent_path\":" + JsonUtil.String(parentPath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"planned_object_path\":" + JsonUtil.String(objectPath)
                + ",\"sibling_collision\":" + JsonUtil.Bool(siblingCollision)
                + ",\"sibling_index\":" + request.SiblingIndex
                + ",\"active\":" + JsonUtil.Bool(request.Active)
                + ",\"rect_transform\":" + UguiObjectCreateRectJson(request)
                + ",\"object\":" + (gameObject == null ? "null" : UguiSceneObjectJson(gameObject))
                + ",\"changed_objects\":" + (created ? "[" + JsonUtil.String(objectPath) + "]" : "[]")
                + ",\"changed_components\":[]"
                + UguiSceneWriteContractJson(scene, request.DryRun, created, parent, created ? new[] { "ugui.component.add", "ugui.canvas.scan", "scene.inspect_object", "scene.save" } : new[] { "checkpoint.create", "ugui.object.create", "ugui.canvas.scan", "scene.save" })
                + "}";
        }

        static string UguiComponentAddResultJson(Scene scene, string scenePath, GameObject target, string objectPath, Type componentType, Component component, int componentIndex, bool dryRun)
        {
            var plannedIndex = dryRun ? target.GetComponents<Component>().Length : componentIndex;
            var componentJson = component == null ? UguiPlannedComponentJson(componentType, plannedIndex) : ComponentJson(component, componentIndex);
            return "{\"status\":\"ok\""
                + ",\"command\":\"ugui.component.add\""
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"added\":" + JsonUtil.Bool(!dryRun)
                + ",\"would_add\":" + JsonUtil.Bool(dryRun)
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"component_type\":" + JsonUtil.String(componentType.Name)
                + ",\"full_type_name\":" + JsonUtil.String(componentType.FullName ?? componentType.Name)
                + ",\"assembly_name\":" + JsonUtil.String(componentType.Assembly.GetName().Name)
                + ",\"component_index\":" + plannedIndex
                + ",\"component\":" + componentJson
                + ",\"changed_objects\":[]"
                + ",\"changed_components\":" + (dryRun ? "[]" : "[" + componentJson + "]")
                + ",\"event_rewiring\":false"
                + ",\"unity_event_mutation\":false"
                + ",\"serialized_fields_configured\":false"
                + UguiSceneWriteContractJson(scene, dryRun, !dryRun, target, dryRun ? new[] { "checkpoint.create", "ugui.component.add", "scene.inspect_object", "scene.save" } : new[] { "scene.inspect_component", "scene.inspect_object", "ugui.canvas.scan", "scene.save" })
                + "}";
        }

        static string UguiObjectCreateRectJson(UguiObjectCreateRequest request)
        {
            return "{\"anchor_min\":" + Vector2Json(request.AnchorMin)
                + ",\"anchor_max\":" + Vector2Json(request.AnchorMax)
                + ",\"pivot\":" + Vector2Json(request.Pivot)
                + ",\"anchored_position\":" + Vector2Json(request.AnchoredPosition)
                + ",\"size_delta\":" + Vector2Json(request.SizeDelta) + "}";
        }

        static string UguiSceneWriteContractJson(Scene scene, bool dryRun, bool changed, GameObject target, string[] nextValidActions)
        {
            return ",\"scene_dirty\":" + JsonUtil.Bool(scene.IsValid() && scene.isDirty)
                + ",\"scene_saved\":false"
                + ",\"saved_prefab\":false"
                + ",\"opens_scenes\":false"
                + ",\"writes_prefab_assets\":false"
                + ",\"loaded_scenes_only\":true"
                + ",\"warnings\":" + UguiSceneWriteWarningsJson(scene, dryRun, changed, target)
                + ",\"next_valid_actions\":" + StringArrayJson(nextValidActions);
        }

        static string UguiSceneWriteWarningsJson(Scene scene, bool dryRun, bool changed, GameObject target)
        {
            var warnings = new List<string>();
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
                warnings.Add("target_is_prefab_instance_override");
            }

            warnings.Add("loaded_scene_only_no_prefab_asset_writes");
            return StringArrayJson(warnings);
        }

        static string UguiSceneObjectJson(GameObject gameObject)
        {
            var rectTransform = gameObject.GetComponent<RectTransform>();
            return "{\"path\":" + JsonUtil.String(ObjectPath(gameObject))
                + ",\"name\":" + JsonUtil.String(gameObject.name)
                + ",\"active_self\":" + JsonUtil.Bool(gameObject.activeSelf)
                + ",\"active_in_hierarchy\":" + JsonUtil.Bool(gameObject.activeInHierarchy)
                + ",\"sibling_index\":" + gameObject.transform.GetSiblingIndex()
                + ",\"has_rect_transform\":" + JsonUtil.Bool(rectTransform != null)
                + ",\"rect_transform\":" + (rectTransform == null ? "null" : RectTransformJson(rectTransform))
                + ",\"component_count\":" + gameObject.GetComponents<Component>().Length
                + ObjectIdentityJson(gameObject) + "}";
        }

        static string ObjectIdentityJson(UnityEngine.Object unityObject)
        {
            if (unityObject == null)
            {
                return string.Empty;
            }

#if UNITY_6000_4_OR_NEWER
            return ",\"entity_id\":" + UnityEngine.EntityId.ToULong(unityObject.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return ",\"instance_id\":" + unityObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        static string ComponentJson(Component component, int componentIndex)
        {
            if (component == null)
            {
                return "{\"component_index\":" + componentIndex + ",\"missing\":true}";
            }

            var type = component.GetType();
            return "{\"component_index\":" + componentIndex
                + ",\"type\":" + JsonUtil.String(type.Name)
                + ",\"full_type_name\":" + JsonUtil.String(type.FullName ?? type.Name)
                + ",\"assembly_name\":" + JsonUtil.String(type.Assembly.GetName().Name)
                + ",\"enabled\":" + JsonUtil.Bool(ComponentEnabled(component, true)) + "}";
        }

        static string UguiPlannedComponentJson(Type componentType, int componentIndex)
        {
            return "{\"component_index\":" + componentIndex
                + ",\"type\":" + JsonUtil.String(componentType.Name)
                + ",\"full_type_name\":" + JsonUtil.String(componentType.FullName ?? componentType.Name)
                + ",\"assembly_name\":" + JsonUtil.String(componentType.Assembly.GetName().Name)
                + ",\"planned\":true}";
        }

        static bool ComponentEnabled(Component component, bool defaultValue)
        {
            var property = component.GetType().GetProperty("enabled", BindingFlags.Instance | BindingFlags.Public);
            if (property == null || property.PropertyType != typeof(bool) || !property.CanRead)
            {
                return defaultValue;
            }

            return (bool)property.GetValue(component, null);
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

        static string UguiAuthoringAllowlistJson()
        {
            return "{\"allowlisted_full_type_names\":" + StringArrayJson(UguiAuthoringComponentTypeNames) + ",\"arbitrary_mono_behaviour_components\":false}";
        }

        static string UguiComponentTypeCandidatesJson(List<Type> matches)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < matches.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"full_type_name\":").Append(JsonUtil.String(matches[index].FullName ?? matches[index].Name));
                builder.Append(",\"assembly_name\":").Append(JsonUtil.String(matches[index].Assembly.GetName().Name));
                builder.Append('}');
            }

            return "{\"candidates\":[" + builder + "]}";
        }

        static bool SiblingNameExists(Transform parent, string name, GameObject ignore)
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

        static string ChildObjectPath(GameObject parent, string childName)
        {
            return ObjectPath(parent) + "/" + childName;
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

        static UguiModifyRequest ExtractCanvasModifyRequest(string json)
        {
            var request = new UguiModifyRequest
            {
                HasActiveSelf = HasJsonProperty(json, "active_self"),
                ActiveSelf = JsonUtil.ExtractBool(json, "active_self", false),
            };

            ExtractModifyVector2(json, "anchor_min", value => { request.HasAnchorMin = true; request.AnchorMin = value; }, request);
            ExtractModifyVector2(json, "anchor_max", value => { request.HasAnchorMax = true; request.AnchorMax = value; }, request);
            ExtractModifyVector2(json, "anchored_position", value => { request.HasAnchoredPosition = true; request.AnchoredPosition = value; }, request);
            ExtractModifyVector2(json, "size_delta", value => { request.HasSizeDelta = true; request.SizeDelta = value; }, request);
            ExtractModifyVector2(json, "pivot", value => { request.HasPivot = true; request.Pivot = value; }, request);
            ExtractModifyVector3(json, "local_scale", value => { request.HasLocalScale = true; request.LocalScale = value; }, request);
            ExtractModifyVector3(json, "local_euler_angles", value => { request.HasLocalEulerAngles = true; request.LocalEulerAngles = value; }, request);
            return request;
        }

        static void ExtractModifyVector2(string json, string property, Action<Vector2> apply, UguiModifyRequest request)
        {
            if (!HasJsonProperty(json, property))
            {
                return;
            }

            if (!TryExtractVector2(json, property, out var value) || !IsFinite(value))
            {
                request.InvalidReason = property + " must be a finite two-number array";
                return;
            }

            apply(value);
        }

        static void ExtractModifyVector3(string json, string property, Action<Vector3> apply, UguiModifyRequest request)
        {
            if (!HasJsonProperty(json, property))
            {
                return;
            }

            if (!TryExtractVector3(json, property, out var value) || !IsFinite(value))
            {
                request.InvalidReason = property + " must be a finite three-number array";
                return;
            }

            apply(value);
        }

        static CommandExecutionResult ValidateCanvasModifyTarget(ScanSnapshot snapshot, NodeSnapshot node, UguiModifyRequest request, out GameObject gameObject, out RectTransform rectTransform, out string scenePath, out Scene scene)
        {
            gameObject = null;
            rectTransform = null;
            scenePath = string.Empty;
            scene = default;

            if (!string.IsNullOrEmpty(request.InvalidReason))
            {
                return CommandExecutionResult.Error("invalid_field_value", request.InvalidReason);
            }

            if (request.HasRectTransformField && !node.HasRectTransform)
            {
                return CommandExecutionResult.Error("invalid_field_value", "RectTransform fields require a cached node with has_rect_transform:true");
            }

            var targetKind = TargetKind(snapshot, node.Target);
            if (!string.Equals(targetKind, "loaded_scene", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Ok("{\"status\":\"blocked\",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                    + ",\"node_id\":" + JsonUtil.String(node.NodeId)
                    + ",\"reason\":\"ugui.canvas.modify first slice only supports cached nodes from loaded_scene targets\""
                    + ",\"target\":" + JsonUtil.String(node.Target)
                    + ",\"target_kind\":" + JsonUtil.String(targetKind)
                    + ",\"dry_run\":true"
                    + ",\"applied\":false"
                    + ",\"saves_prefabs\":false"
                    + ",\"next_valid_actions\":[\"load_scene_then_rescan\",\"ugui.canvas.scan\",\"ugui.canvas.inspect_node\"]}");
            }

            if (node.GameObject == null)
            {
                return CommandExecutionResult.Ok("{\"status\":\"blocked\",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                    + ",\"node_id\":" + JsonUtil.String(node.NodeId)
                    + ",\"reason\":\"cached scan node no longer references a live loaded-scene GameObject; rescan before modifying\""
                    + ",\"dry_run\":true"
                    + ",\"applied\":false"
                    + ",\"next_valid_actions\":[\"ugui.canvas.scan\",\"ugui.canvas.inspect_node\"]}");
            }

            gameObject = node.GameObject;
            scene = gameObject.scene;
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Ok("{\"status\":\"blocked\",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                    + ",\"node_id\":" + JsonUtil.String(node.NodeId)
                    + ",\"reason\":\"target scene is no longer loaded; rescan after loading the scene\""
                    + ",\"dry_run\":true"
                    + ",\"applied\":false"
                    + ",\"next_valid_actions\":[\"scene.open\",\"ugui.canvas.scan\"]}");
            }

            scenePath = scene.path ?? string.Empty;
            rectTransform = gameObject.GetComponent<RectTransform>();
            if (request.HasRectTransformField && rectTransform == null)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Target no longer has a RectTransform component; rescan before modifying RectTransform fields");
            }

            return null;
        }

        static CommandExecutionResult RequireCanvasModifyEditing()
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_busy", "ugui.canvas.modify requires editing status; Unity is compiling");
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "ugui.canvas.modify requires Edit Mode; Unity is in or entering Play Mode");
            }

            return null;
        }

        static UguiModifyState CaptureCanvasModifyState(GameObject gameObject, RectTransform rectTransform)
        {
            var state = new UguiModifyState
            {
                ActiveSelf = gameObject != null && gameObject.activeSelf,
                HasRectTransform = rectTransform != null,
            };

            if (rectTransform != null)
            {
                state.AnchorMin = rectTransform.anchorMin;
                state.AnchorMax = rectTransform.anchorMax;
                state.AnchoredPosition = rectTransform.anchoredPosition;
                state.SizeDelta = rectTransform.sizeDelta;
                state.Pivot = rectTransform.pivot;
                state.LocalScale = rectTransform.localScale;
                state.LocalEulerAngles = rectTransform.localEulerAngles;
            }

            return state;
        }

        static UguiModifyState RequestedCanvasModifyState(UguiModifyState current, UguiModifyRequest request)
        {
            return new UguiModifyState
            {
                ActiveSelf = request.HasActiveSelf ? request.ActiveSelf : current.ActiveSelf,
                HasRectTransform = current.HasRectTransform,
                AnchorMin = request.HasAnchorMin ? request.AnchorMin : current.AnchorMin,
                AnchorMax = request.HasAnchorMax ? request.AnchorMax : current.AnchorMax,
                AnchoredPosition = request.HasAnchoredPosition ? request.AnchoredPosition : current.AnchoredPosition,
                SizeDelta = request.HasSizeDelta ? request.SizeDelta : current.SizeDelta,
                Pivot = request.HasPivot ? request.Pivot : current.Pivot,
                LocalScale = request.HasLocalScale ? request.LocalScale : current.LocalScale,
                LocalEulerAngles = request.HasLocalEulerAngles ? request.LocalEulerAngles : current.LocalEulerAngles,
            };
        }

        static List<string> CanvasModifyChangedFields(UguiModifyState current, UguiModifyState requested, UguiModifyRequest request)
        {
            var fields = new List<string>();
            if (request.HasActiveSelf && current.ActiveSelf != requested.ActiveSelf) fields.Add("active_self");
            if (request.HasAnchorMin && !Approximately(current.AnchorMin, requested.AnchorMin)) fields.Add("anchor_min");
            if (request.HasAnchorMax && !Approximately(current.AnchorMax, requested.AnchorMax)) fields.Add("anchor_max");
            if (request.HasAnchoredPosition && !Approximately(current.AnchoredPosition, requested.AnchoredPosition)) fields.Add("anchored_position");
            if (request.HasSizeDelta && !Approximately(current.SizeDelta, requested.SizeDelta)) fields.Add("size_delta");
            if (request.HasPivot && !Approximately(current.Pivot, requested.Pivot)) fields.Add("pivot");
            if (request.HasLocalScale && !Approximately(current.LocalScale, requested.LocalScale)) fields.Add("local_scale");
            if (request.HasLocalEulerAngles && !Approximately(current.LocalEulerAngles, requested.LocalEulerAngles)) fields.Add("local_euler_angles");
            return fields;
        }

        static void ApplyCanvasModify(GameObject gameObject, RectTransform rectTransform, UguiModifyState current, UguiModifyState requested, List<string> changedFields)
        {
            if (ContainsString(changedFields, "active_self"))
            {
                Undo.RecordObject(gameObject, "uGUI Canvas Modify Active State");
                gameObject.SetActive(requested.ActiveSelf);
            }

            if (rectTransform == null || !HasRectTransformChange(changedFields))
            {
                return;
            }

            Undo.RecordObject(rectTransform, "uGUI Canvas Modify RectTransform");
            if (ContainsString(changedFields, "anchor_min")) rectTransform.anchorMin = requested.AnchorMin;
            if (ContainsString(changedFields, "anchor_max")) rectTransform.anchorMax = requested.AnchorMax;
            if (ContainsString(changedFields, "anchored_position")) rectTransform.anchoredPosition = requested.AnchoredPosition;
            if (ContainsString(changedFields, "size_delta")) rectTransform.sizeDelta = requested.SizeDelta;
            if (ContainsString(changedFields, "pivot")) rectTransform.pivot = requested.Pivot;
            if (ContainsString(changedFields, "local_scale")) rectTransform.localScale = requested.LocalScale;
            if (ContainsString(changedFields, "local_euler_angles")) rectTransform.localEulerAngles = requested.LocalEulerAngles;
        }

        static bool HasRectTransformChange(List<string> changedFields)
        {
            return ContainsString(changedFields, "anchor_min")
                || ContainsString(changedFields, "anchor_max")
                || ContainsString(changedFields, "anchored_position")
                || ContainsString(changedFields, "size_delta")
                || ContainsString(changedFields, "pivot")
                || ContainsString(changedFields, "local_scale")
                || ContainsString(changedFields, "local_euler_angles");
        }

        static CommandExecutionResult RequireCanvasModifyPrecondition(string argsJson, string scanId, NodeSnapshot node, string preconditionHash)
        {
            var actual = JsonUtil.ExtractString(argsJson, "precondition_hash").Trim();
            if (string.IsNullOrEmpty(actual))
            {
                return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when ugui.canvas.modify runs with dry_run:false and would change the scene", CanvasModifyPreconditionDetailsJson(scanId, node, preconditionHash));
            }

            if (!string.Equals(actual, preconditionHash, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match current uGUI node state or requested operation", CanvasModifyPreconditionDetailsJson(scanId, node, preconditionHash));
            }

            return null;
        }

        static string CanvasModifyResultJson(string scanId, NodeSnapshot node, string scenePath, bool dryRun, bool changed, List<string> changedFields, string preconditionHash, UguiModifyState current, UguiModifyState requested, bool sceneDirty)
        {
            return "{\"status\":\"ok\""
                + ",\"command\":\"ugui.canvas.modify\""
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"node_id\":" + JsonUtil.String(node.NodeId)
                + ",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(node.Path)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"applied\":" + JsonUtil.Bool(!dryRun && changed)
                + ",\"would_apply\":" + JsonUtil.Bool(dryRun && changed)
                + ",\"changed\":" + JsonUtil.Bool(changed)
                + ",\"changed_fields\":" + StringArrayJson(changedFields)
                + ",\"changed_objects\":" + (!dryRun && changed ? "[" + JsonUtil.String(node.Path) + "]" : "[]")
                + ",\"current\":" + CanvasModifyStateJson(current)
                + ",\"requested\":" + CanvasModifyStateJson(requested)
                + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash)
                + ",\"scene_dirty\":" + JsonUtil.Bool(sceneDirty)
                + ",\"scene_saved\":false"
                + ",\"saved_prefab\":false"
                + ",\"opens_scenes\":false"
                + ",\"event_rewiring\":false"
                + ",\"warnings\":" + CanvasModifyWarningsJson(dryRun, changed, sceneDirty, node)
                + ",\"next_valid_actions\":[\"checkpoint.create\",\"ugui.canvas.modify\",\"ugui.canvas.scan\",\"ugui.canvas.inspect_node\",\"scene.save\"]}";
        }

        static string CanvasModifyStateJson(UguiModifyState state)
        {
            return "{\"active_self\":" + JsonUtil.Bool(state.ActiveSelf)
                + ",\"has_rect_transform\":" + JsonUtil.Bool(state.HasRectTransform)
                + ",\"rect_transform\":" + (state.HasRectTransform
                    ? "{\"anchor_min\":" + Vector2Json(state.AnchorMin)
                        + ",\"anchor_max\":" + Vector2Json(state.AnchorMax)
                        + ",\"anchored_position\":" + Vector2Json(state.AnchoredPosition)
                        + ",\"size_delta\":" + Vector2Json(state.SizeDelta)
                        + ",\"pivot\":" + Vector2Json(state.Pivot)
                        + ",\"local_scale\":" + Vector3Json(state.LocalScale)
                        + ",\"local_euler_angles\":" + Vector3Json(state.LocalEulerAngles) + "}"
                    : "null") + "}";
        }

        static string CanvasModifyWarningsJson(bool dryRun, bool changed, bool sceneDirty, NodeSnapshot node)
        {
            var warnings = new List<string>();
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

            if (sceneDirty)
            {
                warnings.Add("scene_has_unsaved_changes");
            }

            if (node.GameObject != null && PrefabUtility.IsPartOfPrefabInstance(node.GameObject))
            {
                warnings.Add("target_is_prefab_instance_override");
            }

            warnings.Add("only_active_self_and_rect_transform_fields_are_allowlisted");
            return StringArrayJson(warnings);
        }

        static string CanvasModifyPreconditionDetailsJson(string scanId, NodeSnapshot node, string preconditionHash)
        {
            return "{\"required_precondition\":\"precondition_hash\""
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"node_id\":" + JsonUtil.String(node == null ? string.Empty : node.NodeId)
                + ",\"object_path\":" + JsonUtil.String(node == null ? string.Empty : node.Path)
                + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash)
                + ",\"next_valid_actions\":[\"ugui.canvas.modify_dry_run_true\",\"checkpoint.create\",\"retry_with_precondition_hash\"]}";
        }

        static string CanvasModifyPreconditionHash(ScanSnapshot snapshot, NodeSnapshot node, string scenePath, UguiModifyState current, UguiModifyState requested)
        {
            return Sha256Prefixed("ugui.canvas.modify\n"
                + (snapshot == null ? string.Empty : snapshot.ScanId) + "\n"
                + (node == null ? string.Empty : node.NodeId) + "\n"
                + (node == null ? string.Empty : node.Path) + "\n"
                + scenePath + "\n"
                + CanvasModifyStateJson(current) + "\n"
                + CanvasModifyStateJson(requested));
        }

        static string TargetKind(ScanSnapshot snapshot, string target)
        {
            for (var index = 0; index < snapshot.Targets.Count; index++)
            {
                if (string.Equals(snapshot.Targets[index].Target, target, StringComparison.Ordinal))
                {
                    return snapshot.Targets[index].Kind ?? string.Empty;
                }
            }

            return string.Empty;
        }

        static bool ContainsString(List<string> values, string value)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (string.Equals(values[index], value, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static List<InteractionStep> ExtractInteractionSteps(string json, int maxItems)
        {
            var steps = new List<InteractionStep>();
            var start = FindArrayStart(json, "steps");
            if (start < 0)
            {
                return steps;
            }

            var index = start + 1;
            while (index < json.Length && steps.Count < maxItems)
            {
                while (index < json.Length && (char.IsWhiteSpace(json[index]) || json[index] == ','))
                {
                    index++;
                }

                if (index >= json.Length || json[index] == ']')
                {
                    break;
                }

                if (json[index] != '{')
                {
                    index++;
                    continue;
                }

                var objectEnd = FindMatchingObjectEnd(json, index);
                if (objectEnd < 0)
                {
                    break;
                }

                var objectJson = json.Substring(index, objectEnd - index + 1);
                var step = new InteractionStep
                {
                    NodeId = JsonUtil.ExtractString(objectJson, "node_id"),
                    Action = NormalizeInteractionAction(JsonUtil.ExtractString(objectJson, "action")),
                    Text = JsonUtil.ExtractString(objectJson, "text"),
                    HasToggleValue = HasJsonProperty(objectJson, "toggle_value"),
                    ToggleValue = JsonUtil.ExtractBool(objectJson, "toggle_value", false),
                    HasExpectedInteractable = HasJsonProperty(objectJson, "expected_interactable"),
                    ExpectedInteractable = JsonUtil.ExtractBool(objectJson, "expected_interactable", true),
                };
                steps.Add(step);
                index = objectEnd + 1;
            }

            return steps;
        }

        static string NormalizeInteractionAction(string action)
        {
            return string.IsNullOrEmpty(action) ? string.Empty : action.Trim().ToLowerInvariant();
        }

        static bool HasJsonProperty(string json, string property)
        {
            return !string.IsNullOrEmpty(json)
                && !string.IsNullOrEmpty(property)
                && json.IndexOf("\"" + property + "\"", StringComparison.Ordinal) >= 0;
        }

        static List<InteractionStepResult> BuildInteractionStepResults(ScanSnapshot snapshot, List<InteractionStep> steps, Component eventSystem)
        {
            var results = new List<InteractionStepResult>();
            for (var index = 0; index < steps.Count; index++)
            {
                results.Add(BuildInteractionStepResult(snapshot, steps[index], index, eventSystem));
            }
            return results;
        }

        static InteractionStepResult BuildInteractionStepResult(ScanSnapshot snapshot, InteractionStep step, int index, Component eventSystem)
        {
            var result = new InteractionStepResult
            {
                Index = index,
                Step = step,
                Status = "ok",
                Message = "step can be executed after policy approval, confirm_execute:true, and a valid execution context",
                CanExecute = true,
                EventSystemRequired = true,
                EventSystemFound = eventSystem != null,
            };

            if (!IsSupportedInteractionAction(step.Action))
            {
                result.Status = "invalid_action";
                result.Message = "action must be one of focus, click, submit, toggle, or type";
                result.CanExecute = false;
                return result;
            }

            NodeSnapshot node;
            if (string.IsNullOrEmpty(step.NodeId) || !snapshot.NodesById.TryGetValue(step.NodeId, out node))
            {
                result.Status = "node_not_found";
                result.Message = "node_id was not found in the cached uGUI scan";
                result.CanExecute = false;
                return result;
            }

            result.Node = node;
            if (node.GameObject == null)
            {
                result.Status = "cached_object_unavailable";
                result.Message = "cached GameObject reference is no longer available; prefab preview scans and destroyed objects cannot be executed";
                result.CanExecute = false;
                return result;
            }

            if (!node.GameObject.activeInHierarchy)
            {
                result.Status = "inactive_object";
                result.Message = "target GameObject is inactive in hierarchy";
                result.CanExecute = false;
                return result;
            }

            if (string.Equals(step.Action, "type", StringComparison.Ordinal) && string.IsNullOrEmpty(step.Text))
            {
                result.Status = "missing_text";
                result.Message = "type action requires a non-empty text value";
                result.CanExecute = false;
                return result;
            }

            result.Component = FindInteractionComponent(node.GameObject, step.Action, out var expectedComponent);
            result.ExpectedComponent = expectedComponent;
            if (result.Component == null)
            {
                result.Status = "component_not_found";
                result.Message = "expected component was not found for this action";
                result.CanExecute = false;
                return result;
            }

            result.ComponentType = result.Component.GetType().FullName ?? result.Component.GetType().Name;
            result.Interactable = IsInteractionComponentInteractable(result.Component, node.GameObject);
            if (step.ExpectedInteractable && !result.Interactable)
            {
                result.Status = "not_interactable";
                result.Message = "target component is disabled, inactive, or exposes interactable=false";
                result.CanExecute = false;
                return result;
            }

            if (eventSystem == null)
            {
                result.Status = "event_system_not_found";
                result.Message = "no active UnityEngine.EventSystems.EventSystem was found; real smoke execution is blocked";
                result.CanExecute = false;
            }

            return result;
        }

        static bool IsSupportedInteractionAction(string action)
        {
            return action == "focus" || action == "click" || action == "submit" || action == "toggle" || action == "type";
        }

        static Component FindInteractionComponent(GameObject gameObject, string action, out string expectedComponent)
        {
            expectedComponent = string.Empty;
            if (gameObject == null)
            {
                return null;
            }

            if (action == "focus")
            {
                expectedComponent = "UnityEngine.UI.Selectable";
                return FindComponentByTypeName(gameObject, expectedComponent);
            }
            if (action == "click")
            {
                expectedComponent = "UnityEngine.UI.Button";
                return FindComponentByTypeName(gameObject, expectedComponent);
            }
            if (action == "toggle")
            {
                expectedComponent = "UnityEngine.UI.Toggle";
                return FindComponentByTypeName(gameObject, expectedComponent);
            }
            if (action == "type")
            {
                expectedComponent = "UnityEngine.UI.InputField|TMPro.TMP_InputField";
                return FindComponentByTypeName(gameObject, "UnityEngine.UI.InputField") ?? FindComponentByTypeName(gameObject, "TMPro.TMP_InputField");
            }
            if (action == "submit")
            {
                expectedComponent = "UnityEngine.UI.Button|UnityEngine.UI.InputField|TMPro.TMP_InputField";
                return FindComponentByTypeName(gameObject, "UnityEngine.UI.Button")
                    ?? FindComponentByTypeName(gameObject, "UnityEngine.UI.InputField")
                    ?? FindComponentByTypeName(gameObject, "TMPro.TMP_InputField");
            }

            return null;
        }

        static Component FindComponentByTypeName(GameObject gameObject, string fullName)
        {
            var components = gameObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index];
                if (component != null && IsTypeNamed(component.GetType(), fullName))
                {
                    return component;
                }
            }
            return null;
        }

        static bool IsInteractionComponentInteractable(Component component, GameObject gameObject)
        {
            if (component == null || gameObject == null || !gameObject.activeInHierarchy)
            {
                return false;
            }

            var behaviour = component as Behaviour;
            if (behaviour != null && !behaviour.enabled)
            {
                return false;
            }

            return GetBoolProperty(component, "interactable", true);
        }

        static bool InteractionStepsCanExecute(List<InteractionStepResult> stepResults, Component eventSystem)
        {
            if (eventSystem == null)
            {
                return false;
            }

            for (var index = 0; index < stepResults.Count; index++)
            {
                if (!stepResults[index].CanExecute)
                {
                    return false;
                }
            }
            return true;
        }

        static bool TryExecuteInteractionStep(InteractionStepResult result, Component eventSystem, out string status, out string message)
        {
            status = "execute_failed";
            message = string.Empty;
            if (result == null || result.Node == null || result.Node.GameObject == null || result.Component == null)
            {
                message = "step preflight target is no longer available";
                return false;
            }

            if (!SetEventSystemSelectedGameObject(eventSystem, result.Node.GameObject, out message))
            {
                status = "focus_failed";
                return false;
            }

            try
            {
                var action = result.Step.Action;
                if (action == "focus")
                {
                    status = "executed";
                    message = "selected GameObject through current EventSystem";
                    return true;
                }
                if (action == "click")
                {
                    return InvokeUnityEventProperty(result.Component, "onClick", null, out status, out message);
                }
                if (action == "toggle")
                {
                    var currentValue = GetBoolProperty(result.Component, "isOn", false);
                    var targetValue = result.Step.HasToggleValue ? result.Step.ToggleValue : !currentValue;
                    if (!SetPropertyValue(result.Component, "isOn", targetValue, out message))
                    {
                        status = "toggle_failed";
                        return false;
                    }
                    status = "executed";
                    message = "set Toggle.isOn to " + JsonUtil.Bool(targetValue);
                    return true;
                }
                if (action == "type")
                {
                    if (!SetPropertyValue(result.Component, "text", result.Step.Text, out message))
                    {
                        status = "type_failed";
                        return false;
                    }
                    status = "executed";
                    message = "set input text through reflected text property";
                    return true;
                }
                if (action == "submit")
                {
                    if (IsTypeNamed(result.Component.GetType(), "UnityEngine.UI.Button"))
                    {
                        return InvokeUnityEventProperty(result.Component, "onClick", null, out status, out message);
                    }

                    var submittedText = GetPropertyValue(result.Component, "text") as string ?? string.Empty;
                    if (InvokeUnityEventProperty(result.Component, "onSubmit", submittedText, out status, out message)
                        || InvokeUnityEventProperty(result.Component, "onEndEdit", submittedText, out status, out message))
                    {
                        return true;
                    }

                    status = "submit_failed";
                    message = "component has no onSubmit/onEndEdit event property that accepts the current text";
                    return false;
                }
            }
            catch (TargetInvocationException exception)
            {
                var inner = exception.InnerException ?? exception;
                status = "callback_exception";
                message = inner.GetType().Name + ": " + inner.Message;
                return false;
            }
            catch (Exception exception)
            {
                status = "execute_exception";
                message = exception.GetType().Name + ": " + exception.Message;
                return false;
            }

            status = "invalid_action";
            message = "action was not recognized during execution";
            return false;
        }

        static Component FindCurrentEventSystem()
        {
            var eventSystemType = FindLoadedType("UnityEngine.EventSystems.EventSystem");
            if (eventSystemType == null)
            {
                return null;
            }

            var currentProperty = eventSystemType.GetProperty("current", BindingFlags.Static | BindingFlags.Public);
            var current = currentProperty == null ? null : currentProperty.GetValue(null, null) as Component;
            if (current != null)
            {
                return current;
            }

            var objects = Resources.FindObjectsOfTypeAll(eventSystemType);
            for (var index = 0; index < objects.Length; index++)
            {
                var component = objects[index] as Component;
                if (component != null && component.gameObject.activeInHierarchy)
                {
                    return component;
                }
            }

            return null;
        }

        static Type FindLoadedType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName))
            {
                return null;
            }

            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }
            return null;
        }

        static bool SetEventSystemSelectedGameObject(Component eventSystem, GameObject gameObject, out string message)
        {
            message = string.Empty;
            if (eventSystem == null)
            {
                message = "EventSystem is missing";
                return false;
            }
            if (gameObject == null)
            {
                message = "target GameObject is missing";
                return false;
            }

            var method = eventSystem.GetType().GetMethod("SetSelectedGameObject", new Type[] { typeof(GameObject) });
            if (method == null)
            {
                message = "EventSystem.SetSelectedGameObject(GameObject) was not found";
                return false;
            }

            method.Invoke(eventSystem, new object[] { gameObject });
            return true;
        }

        static bool SetPropertyValue(object target, string propertyName, object value, out string message)
        {
            message = string.Empty;
            if (target == null)
            {
                message = "target is null";
                return false;
            }

            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.CanWrite || property.GetIndexParameters().Length != 0)
            {
                message = "writable property " + propertyName + " was not found on " + (target.GetType().FullName ?? target.GetType().Name);
                return false;
            }

            property.SetValue(target, value, null);
            return true;
        }

        static bool InvokeUnityEventProperty(Component component, string propertyName, object argument, out string status, out string message)
        {
            status = "execute_failed";
            message = string.Empty;
            var unityEvent = GetPropertyValue(component, propertyName);
            if (unityEvent == null)
            {
                status = "event_not_found";
                message = "event property " + propertyName + " was not found";
                return false;
            }

            MethodInfo invoke;
            object[] args;
            if (argument == null)
            {
                invoke = unityEvent.GetType().GetMethod("Invoke", Type.EmptyTypes);
                args = new object[0];
            }
            else
            {
                invoke = unityEvent.GetType().GetMethod("Invoke", new Type[] { argument.GetType() });
                args = new object[] { argument };
            }

            if (invoke == null)
            {
                status = "invoke_method_not_found";
                message = "event property " + propertyName + " has no compatible Invoke method";
                return false;
            }

            invoke.Invoke(unityEvent, args);
            status = "executed";
            message = "invoked " + propertyName + " on " + (component.GetType().FullName ?? component.GetType().Name);
            return true;
        }

        static string InteractionSmokeBlockedJson(string scanId, string status, bool dryRun, bool allowEditMode, string reason)
        {
            return "{\"status\":" + JsonUtil.String(status)
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"allow_edit_mode\":" + JsonUtil.Bool(allowEditMode)
                + ",\"executed\":false"
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"callbacks_may_mutate_game_state\":true"
                + ",\"writes_files\":false"
                + ",\"resource_refs\":[]"
                + ",\"artifact_refs\":[]"
                + ",\"next_valid_actions\":[\"enter_play_mode_or_rerun_dry_run\",\"retry_with_confirm_execute_true_after_operator_approval\"]}";
        }

        static string InteractionSmokeResultJson(ScanSnapshot snapshot, string smokeId, bool dryRun, bool executed, bool allowEditMode, Component eventSystem, string status, List<InteractionStepResult> stepResults, int executedSteps, string resourceUri, string reason)
        {
            return "{\"status\":" + JsonUtil.String(status)
                + ",\"interaction_id\":" + JsonUtil.String(smokeId)
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"executed\":" + JsonUtil.Bool(executed)
                + ",\"allow_edit_mode\":" + JsonUtil.Bool(allowEditMode)
                + ",\"play_mode\":" + JsonUtil.Bool(EditorApplication.isPlaying)
                + ",\"event_system\":" + InteractionEventSystemJson(eventSystem)
                + ",\"steps_requested\":" + stepResults.Count
                + ",\"steps_executed\":" + executedSteps
                + ",\"steps\":" + InteractionStepResultsJson(stepResults, dryRun)
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"callbacks_may_mutate_game_state\":true"
                + ",\"writes_files\":false"
                + ",\"saves_scenes\":false"
                + ",\"opens_scenes\":false"
                + ",\"uses_cached_scan\":true"
                + ",\"risk\":\"execute\""
                + ",\"requires_approval\":true"
                + ",\"next_valid_actions\":" + InteractionNextActionsJson(dryRun, status) + "}";
        }

        static string InteractionEventSystemJson(Component eventSystem)
        {
            if (eventSystem == null)
            {
                return "{\"found\":false}";
            }

            return "{\"found\":true"
                + ",\"name\":" + JsonUtil.String(eventSystem.gameObject == null ? string.Empty : eventSystem.gameObject.name)
                + ",\"type\":" + JsonUtil.String(eventSystem.GetType().FullName ?? eventSystem.GetType().Name)
                + ",\"active_in_hierarchy\":" + JsonUtil.Bool(eventSystem.gameObject != null && eventSystem.gameObject.activeInHierarchy)
                + "}";
        }

        static string InteractionStepResultsJson(List<InteractionStepResult> stepResults, bool dryRun)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < stepResults.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var result = stepResults[index];
                builder.Append("{\"index\":").Append(result.Index);
                builder.Append(",\"node_id\":").Append(JsonUtil.String(result.Step.NodeId));
                builder.Append(",\"action\":").Append(JsonUtil.String(result.Step.Action));
                builder.Append(",\"path\":").Append(JsonUtil.String(result.Node == null ? string.Empty : result.Node.Path));
                builder.Append(",\"target\":").Append(JsonUtil.String(result.Node == null ? string.Empty : result.Node.Target));
                builder.Append(",\"status\":").Append(JsonUtil.String(result.Status));
                builder.Append(",\"message\":").Append(JsonUtil.String(result.Message));
                builder.Append(",\"expected_component\":").Append(JsonUtil.String(result.ExpectedComponent));
                builder.Append(",\"component_type\":").Append(JsonUtil.String(result.ComponentType));
                builder.Append(",\"interactable\":").Append(JsonUtil.Bool(result.Interactable));
                builder.Append(",\"event_system_required\":").Append(JsonUtil.Bool(result.EventSystemRequired));
                builder.Append(",\"event_system_found\":").Append(JsonUtil.Bool(result.EventSystemFound));
                builder.Append(",\"can_execute\":").Append(JsonUtil.Bool(result.CanExecute));
                builder.Append(",\"would_execute\":").Append(JsonUtil.Bool(dryRun && result.CanExecute));
                builder.Append(",\"executed\":").Append(JsonUtil.Bool(result.Executed));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string InteractionNextActionsJson(bool dryRun, string status)
        {
            var actions = new List<string>();
            if (dryRun)
            {
                actions.Add("review_steps_and_rerun_with_dry_run_false_confirm_execute_true");
                actions.Add("run_ugui.navigation.analyze_or_ugui.raycast.analyze_for_preflight_context");
            }
            else if (status == "ok")
            {
                actions.Add("inspect_game_state_or_logs_after_smoke");
            }
            else
            {
                actions.Add("inspect_failed_step");
                actions.Add("rerun_dry_run_after_fixing_ui_state");
            }

            return StringArrayJson(actions);
        }

        static void AppendTargetScan(StringBuilder builder, string target, string kind, GameObject[] roots, ScanState state, ScanSnapshot snapshot)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            state.TargetsFound++;
            AddSourceTarget(snapshot, target);
            snapshot.Targets.Add(new TargetSnapshot { Target = target, Kind = kind, Status = "ok", Reason = string.Empty });
            var nodes = new StringBuilder();
            for (var index = 0; index < roots.Length && state.NodesIncluded < state.MaxNodes; index++)
            {
                AppendNode(nodes, roots[index], roots[index].name, 0, string.Empty, target, state, snapshot);
            }

            builder.Append("{\"target\":").Append(JsonUtil.String(target));
            builder.Append(",\"kind\":").Append(JsonUtil.String(kind));
            builder.Append(",\"status\":\"ok\",\"nodes\":[").Append(nodes).Append("]}");
        }

        static void AppendTargetStatus(StringBuilder builder, string target, string status, string reason, ScanSnapshot snapshot)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            AddSourceTarget(snapshot, target);
            snapshot.Targets.Add(new TargetSnapshot { Target = target, Kind = string.Empty, Status = status, Reason = reason });
            builder.Append("{\"target\":").Append(JsonUtil.String(target));
            builder.Append(",\"status\":").Append(JsonUtil.String(status));
            builder.Append(",\"reason\":").Append(JsonUtil.String(reason)).Append('}');
        }

        static void AppendPrefabPreviewTargetScan(StringBuilder builder, string target, GameObject prefabAsset, List<PrefabLayoutOverride> overrides, ScanState state, ScanSnapshot snapshot)
        {
            var previewScene = EditorSceneManager.NewPreviewScene();
            try
            {
                var instance = PrefabUtility.InstantiatePrefab(prefabAsset, previewScene) as GameObject;
                if (instance == null)
                {
                    AppendTargetStatus(builder, target, "invalid", "prefab could not be instantiated in a temporary preview scene", snapshot);
                    return;
                }

                ApplyPrefabLayoutOverridesToPreviewInstance(target, instance, overrides);
                Canvas.ForceUpdateCanvases();
                AppendTargetScan(builder, target, "prefab_asset", new GameObject[] { instance }, state, snapshot);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(previewScene);
            }
        }

        static void ApplyPrefabLayoutOverridesToPreviewInstance(string prefabPath, GameObject previewRoot, List<PrefabLayoutOverride> overrides)
        {
            for (var index = 0; index < overrides.Count; index++)
            {
                var item = overrides[index];
                if (item.Status == "invalid" || !string.Equals(item.PrefabPath, prefabPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var transform = FindTransformByPath(previewRoot, item.RootPath);
                if (transform == null)
                {
                    item.Status = "invalid";
                    item.Reason = "root_path was not found inside the temporary preview instance";
                    continue;
                }

                var rectTransform = transform as RectTransform;
                if (rectTransform == null)
                {
                    item.Status = "invalid";
                    item.Reason = "root_path does not resolve to a RectTransform in the temporary preview instance";
                    continue;
                }

                if (!item.HasSize && !item.HasParentSize && !item.HasAnchoredPosition && !item.HasScale)
                {
                    item.Status = "ignored";
                    item.Reason = "override matched the prefab but no size, parent_size, anchored_position, or scale values were provided";
                    continue;
                }

                if (item.HasParentSize)
                {
                    var parentRect = rectTransform.parent as RectTransform;
                    if (parentRect != null)
                    {
                        parentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.ParentSize.x);
                        parentRect.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.ParentSize.y);
                    }
                }
                if (item.HasSize)
                {
                    rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Horizontal, item.Size.x);
                    rectTransform.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, item.Size.y);
                }
                if (item.HasAnchoredPosition)
                {
                    rectTransform.anchoredPosition = item.AnchoredPosition;
                }
                if (item.HasScale)
                {
                    rectTransform.localScale = item.Scale;
                }

                item.Applied = true;
                item.Status = "applied";
                item.Reason = "applied to temporary preview instance only; prefab asset was not saved or dirtied";
            }
        }

        static Transform FindTransformByPath(GameObject root, string rootPath)
        {
            if (root == null || string.IsNullOrEmpty(rootPath) || rootPath.IndexOf("..", StringComparison.Ordinal) >= 0 || rootPath.StartsWith("/", StringComparison.Ordinal))
            {
                return null;
            }

            var parts = rootPath.Split('/');
            var current = root.transform;
            var start = parts.Length > 0 && string.Equals(parts[0], root.name, StringComparison.Ordinal) ? 1 : 0;
            for (var index = start; index < parts.Length; index++)
            {
                if (string.IsNullOrEmpty(parts[index]))
                {
                    return null;
                }
                current = FindDirectChild(current, parts[index]);
                if (current == null)
                {
                    return null;
                }
            }
            return current;
        }

        static Transform FindDirectChild(Transform parent, string name)
        {
            for (var index = 0; index < parent.childCount; index++)
            {
                var child = parent.GetChild(index);
                if (string.Equals(child.name, name, StringComparison.Ordinal))
                {
                    return child;
                }
            }
            return null;
        }

        static void AppendNode(StringBuilder builder, GameObject gameObject, string path, int depth, string parentNodeId, string target, ScanState state, ScanSnapshot snapshot)
        {
            if (gameObject == null || state.NodesIncluded >= state.MaxNodes || depth > state.MaxDepth)
            {
                state.Truncated = true;
                return;
            }

            if (!state.IncludeInactive && !gameObject.activeInHierarchy)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            var canvas = gameObject.GetComponent<Canvas>();
            var rectTransform = gameObject.GetComponent<RectTransform>();
            if (canvas != null)
            {
                state.CanvasRoots++;
            }

            var nodeId = "node_" + state.NodesIncluded.ToString("D5");
            var node = CreateNodeSnapshot(nodeId, path, depth, parentNodeId, target, gameObject, canvas, rectTransform, snapshot);
            snapshot.NodesById[nodeId] = node;
            for (var index = 0; index < node.Components.Count; index++)
            {
                Increment(snapshot.ComponentCounts, node.Components[index].Type);
            }
            snapshot.MissingScripts += node.MissingScripts;

            builder.Append("{\"node_id\":").Append(JsonUtil.String(nodeId));
            builder.Append(",\"path\":").Append(JsonUtil.String(path));
            builder.Append(",\"name\":").Append(JsonUtil.String(gameObject.name));
            builder.Append(",\"depth\":").Append(depth);
            builder.Append(",\"active_self\":").Append(JsonUtil.Bool(gameObject.activeSelf));
            builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(gameObject.activeInHierarchy));
            builder.Append(",\"sibling_index\":").Append(gameObject.transform.GetSiblingIndex());
            builder.Append(",\"has_rect_transform\":").Append(JsonUtil.Bool(rectTransform != null));
            if (canvas != null)
            {
                builder.Append(",\"canvas\":{\"render_mode\":").Append(JsonUtil.String(canvas.renderMode.ToString())).Append('}');
            }
            if (state.IncludeComponents)
            {
                AppendUguiComponents(builder, gameObject);
            }
            builder.Append('}');
            state.NodesIncluded++;

            var transform = gameObject.transform;
            for (var index = 0; index < transform.childCount && state.NodesIncluded < state.MaxNodes; index++)
            {
                var child = transform.GetChild(index).gameObject;
                AppendNode(builder, child, path + "/" + child.name, depth + 1, nodeId, target, state, snapshot);
            }
        }

        static void AppendUguiComponents(StringBuilder builder, GameObject gameObject)
        {
            var components = gameObject.GetComponents<Component>();
            var first = true;
            builder.Append(",\"components\":[");
            foreach (var component in components)
            {
                if (component == null)
                {
                    continue;
                }

                var type = component.GetType();
                if (type == typeof(RectTransform) || type == typeof(Canvas) || IsKnownUguiType(type))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }
                    first = false;
                    builder.Append("{\"type\":").Append(JsonUtil.String(type.FullName));
                    builder.Append(",\"name\":").Append(JsonUtil.String(type.Name)).Append('}');
                }
            }
            builder.Append(']');
        }

        static NodeSnapshot CreateNodeSnapshot(string nodeId, string path, int depth, string parentNodeId, string target, GameObject gameObject, Canvas canvas, RectTransform rectTransform, ScanSnapshot snapshot)
        {
            var node = new NodeSnapshot
            {
                NodeId = nodeId,
                Path = path,
                Depth = depth,
                ParentNodeId = parentNodeId,
                Name = gameObject.name,
                Target = target,
                GameObject = gameObject,
                ActiveSelf = gameObject.activeSelf,
                ActiveInHierarchy = gameObject.activeInHierarchy,
                SiblingIndex = gameObject.transform.GetSiblingIndex(),
                ChildCount = gameObject.transform.childCount,
                GameObjectInstanceId = RuntimeObjectIdentity(gameObject),
                TransformInstanceId = RuntimeObjectIdentity(gameObject.transform),
                HasRectTransform = rectTransform != null,
                CanvasRenderMode = canvas == null ? string.Empty : canvas.renderMode.ToString(),
            };
            snapshot.ObjectPathsByInstanceId[node.GameObjectInstanceId] = path;
            snapshot.ObjectPathsByInstanceId[node.TransformInstanceId] = path;

            if (rectTransform != null)
            {
                snapshot.ObjectPathsByInstanceId[RuntimeObjectIdentity(rectTransform)] = path;
                node.RectTransformJson = RectTransformJson(rectTransform);
                node.AnchorMin = rectTransform.anchorMin;
                node.AnchorMax = rectTransform.anchorMax;
                node.AnchoredPosition = rectTransform.anchoredPosition;
                node.SizeDelta = rectTransform.sizeDelta;
                node.Pivot = rectTransform.pivot;
                node.LocalScale = rectTransform.localScale;
                node.LocalEulerAngles = rectTransform.localEulerAngles;
                node.RectSize = rectTransform.rect.size;
                var corners = new Vector3[4];
                rectTransform.GetWorldCorners(corners);
                node.WorldMin = new Vector2(Math.Min(Math.Min(corners[0].x, corners[1].x), Math.Min(corners[2].x, corners[3].x)), Math.Min(Math.Min(corners[0].y, corners[1].y), Math.Min(corners[2].y, corners[3].y)));
                node.WorldMax = new Vector2(Math.Max(Math.Max(corners[0].x, corners[1].x), Math.Max(corners[2].x, corners[3].x)), Math.Max(Math.Max(corners[0].y, corners[1].y), Math.Max(corners[2].y, corners[3].y)));
            }

            var components = gameObject.GetComponents<Component>();
            foreach (var component in components)
            {
                if (component == null)
                {
                    node.MissingScripts++;
                    continue;
                }

                var type = component.GetType();
                if (type == typeof(RectTransform) || type == typeof(Canvas) || IsKnownUguiType(type))
                {
                    var componentSnapshot = new ComponentSnapshot
                    {
                        InstanceId = RuntimeObjectIdentity(component),
                        Type = type.FullName ?? type.Name,
                        Name = type.Name,
                    };
                    node.Components.Add(componentSnapshot);
                    snapshot.ObjectPathsByInstanceId[componentSnapshot.InstanceId] = path;
                    if (IsSelectableComponent(type))
                    {
                        AddNavigationNodeSnapshot(component, node, snapshot);
                    }
                    if (IsGraphicComponent(type))
                    {
                        AddRaycastGraphicSnapshot(component, node, snapshot);
                    }
                    if (IsCanvasGroupComponent(type))
                    {
                        AddCanvasGroupSnapshot(component, node, snapshot);
                    }
                    if (IsGraphicRaycasterComponent(type))
                    {
                        AddGraphicRaycasterSnapshot(component, node, snapshot);
                    }
                    if (IsEventSystemComponent(type))
                    {
                        AddEventSystemSnapshot(component, node, snapshot);
                    }
                    ExtractPersistentEvents(component, node, snapshot);
                    ExtractAssetReferences(component, node, snapshot);
                }
            }

            return node;
        }

        static void AddNavigationNodeSnapshot(Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            var type = component.GetType();
            var item = new NavigationNodeSnapshot
            {
                NodeId = node.NodeId,
                Path = node.Path,
                Target = node.Target,
                ComponentType = type.FullName ?? type.Name,
                ComponentName = type.Name,
                ComponentInstanceId = RuntimeObjectIdentity(component),
                ActiveSelf = node.ActiveSelf,
                ActiveInHierarchy = node.ActiveInHierarchy,
                ComponentEnabled = !(component is Behaviour) || ((Behaviour)component).enabled,
                Interactable = GetBoolProperty(component, "interactable", true),
            };

            var navigation = GetPropertyValue(component, "navigation");
            if (navigation != null)
            {
                item.Mode = GetPropertyString(navigation, "mode");
                item.WrapAround = GetBoolProperty(navigation, "wrapAround", false);
                AddNavigationEdge(item, navigation, "up", "selectOnUp");
                AddNavigationEdge(item, navigation, "down", "selectOnDown");
                AddNavigationEdge(item, navigation, "left", "selectOnLeft");
                AddNavigationEdge(item, navigation, "right", "selectOnRight");
            }

            snapshot.NavigationNodes.Add(item);
        }

        static void AddNavigationEdge(NavigationNodeSnapshot item, object navigation, string direction, string propertyName)
        {
            var target = GetPropertyValue(navigation, propertyName) as UnityEngine.Object;
            if (target == null)
            {
                return;
            }

            item.Edges.Add(new NavigationEdgeSnapshot
            {
                FromNodeId = item.NodeId,
                FromPath = item.Path,
                FromComponentId = item.ComponentInstanceId,
                Direction = direction,
                TargetObjectId = RuntimeObjectIdentity(target),
                TargetName = target.name,
                TargetType = target.GetType().FullName ?? target.GetType().Name,
            });
        }

        static void AddEventSystemSnapshot(Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            var type = component.GetType();
            var firstSelected = GetPropertyValue(component, "firstSelectedGameObject") as UnityEngine.Object;
            snapshot.EventSystems.Add(new EventSystemSnapshot
            {
                NodeId = node.NodeId,
                Path = node.Path,
                ComponentType = type.FullName ?? type.Name,
                ComponentName = type.Name,
                FirstSelectedObjectId = RuntimeObjectIdentity(firstSelected),
                FirstSelectedName = firstSelected == null ? string.Empty : firstSelected.name,
                FirstSelectedType = firstSelected == null ? string.Empty : firstSelected.GetType().FullName ?? firstSelected.GetType().Name,
            });
        }

        static void ExtractAssetReferences(Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            if (component == null || snapshot.AssetReferences.Count >= MaxAssetReferencesPerScan)
            {
                snapshot.AssetReferencesTruncated = snapshot.AssetReferences.Count >= MaxAssetReferencesPerScan;
                return;
            }

            if (component is MonoBehaviour)
            {
                AddAssetReference(MonoScript.FromMonoBehaviour((MonoBehaviour)component), node, component, "m_Script", snapshot);
            }

            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(component);
            }
            catch
            {
                return;
            }

            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                {
                    AddAssetReference(iterator.objectReferenceValue, node, component, iterator.propertyPath, snapshot);
                    if (snapshot.AssetReferences.Count >= MaxAssetReferencesPerScan)
                    {
                        snapshot.AssetReferencesTruncated = true;
                        return;
                    }
                }
            }
        }

        static void AddAssetReference(UnityEngine.Object value, NodeSnapshot node, Component component, string propertyPath, ScanSnapshot snapshot)
        {
            if (value == null || snapshot.AssetReferences.Count >= MaxAssetReferencesPerScan)
            {
                return;
            }

            var assetPath = AssetDatabase.GetAssetPath(value);
            if (string.IsNullOrEmpty(assetPath))
            {
                return;
            }

            var referenceType = ReferenceType(value);
            if (referenceType == "other")
            {
                return;
            }

            var valueType = value.GetType();
            var componentType = component.GetType();
            snapshot.AssetReferences.Add(new AssetReferenceSnapshot
            {
                NodeId = node.NodeId,
                Path = node.Path,
                Target = node.Target,
                ComponentType = componentType.FullName ?? componentType.Name,
                ComponentName = componentType.Name,
                PropertyPath = propertyPath,
                ReferenceType = referenceType,
                AssetPath = assetPath,
                AssetGuid = AssetDatabase.AssetPathToGUID(assetPath),
                Name = value.name,
                ObjectType = valueType.FullName ?? valueType.Name,
            });
        }

        static string ReferenceType(UnityEngine.Object value)
        {
            if (value is Sprite)
            {
                return "sprite";
            }
            if (value is Texture)
            {
                return "texture";
            }
            if (value is Material)
            {
                return "material";
            }
            if (value is MonoScript)
            {
                return "script";
            }
            if (value is Font || IsTypeNamed(value.GetType(), "TMPro.TMP_FontAsset"))
            {
                return "font";
            }
            return "other";
        }

        static void AddRaycastGraphicSnapshot(Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            var type = component.GetType();
            var colorValue = GetPropertyValue(component, "color");
            var colorAlpha = colorValue is Color ? ((Color)colorValue).a : 1f;
            snapshot.RaycastGraphics.Add(new RaycastGraphicSnapshot
            {
                NodeId = node.NodeId,
                Path = node.Path,
                Target = node.Target,
                ComponentType = type.FullName ?? type.Name,
                ComponentName = type.Name,
                ActiveSelf = node.ActiveSelf,
                ActiveInHierarchy = node.ActiveInHierarchy,
                ComponentEnabled = !(component is Behaviour) || ((Behaviour)component).enabled,
                RaycastTarget = GetBoolProperty(component, "raycastTarget", false),
                ColorAlpha = colorAlpha,
                RectSize = node.RectSize,
                WorldMin = node.WorldMin,
                WorldMax = node.WorldMax,
                SiblingIndex = node.SiblingIndex,
                Depth = node.Depth,
            });
        }

        static void AddCanvasGroupSnapshot(Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            var type = component.GetType();
            snapshot.CanvasGroups.Add(new CanvasGroupSnapshot
            {
                NodeId = node.NodeId,
                Path = node.Path,
                ComponentType = type.FullName ?? type.Name,
                ComponentName = type.Name,
                ActiveInHierarchy = node.ActiveInHierarchy,
                ComponentEnabled = !(component is Behaviour) || ((Behaviour)component).enabled,
                Alpha = GetFloatProperty(component, "alpha", 1f),
                BlocksRaycasts = GetBoolProperty(component, "blocksRaycasts", true),
                Interactable = GetBoolProperty(component, "interactable", true),
                IgnoreParentGroups = GetBoolProperty(component, "ignoreParentGroups", false),
            });
        }

        static void AddGraphicRaycasterSnapshot(Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            var type = component.GetType();
            snapshot.GraphicRaycasters.Add(new GraphicRaycasterSnapshot
            {
                NodeId = node.NodeId,
                Path = node.Path,
                ComponentType = type.FullName ?? type.Name,
                ComponentName = type.Name,
                ActiveInHierarchy = node.ActiveInHierarchy,
                ComponentEnabled = !(component is Behaviour) || ((Behaviour)component).enabled,
                IgnoreReversedGraphics = GetBoolProperty(component, "ignoreReversedGraphics", true),
                BlockingObjects = GetPropertyString(component, "blockingObjects"),
                BlockingMask = GetPropertyString(component, "blockingMask"),
            });
        }

        static string RectTransformJson(RectTransform rectTransform)
        {
            return "{\"anchor_min\":" + Vector2Json(rectTransform.anchorMin)
                + ",\"anchor_max\":" + Vector2Json(rectTransform.anchorMax)
                + ",\"anchored_position\":" + Vector2Json(rectTransform.anchoredPosition)
                + ",\"size_delta\":" + Vector2Json(rectTransform.sizeDelta)
                + ",\"pivot\":" + Vector2Json(rectTransform.pivot)
                + ",\"local_scale\":" + Vector3Json(rectTransform.localScale) + "}";
        }

        static string NodeInspectJson(NodeSnapshot node)
        {
            return "{\"node_id\":" + JsonUtil.String(node.NodeId)
                + ",\"path\":" + JsonUtil.String(node.Path)
                + ",\"name\":" + JsonUtil.String(node.Name)
                + ",\"target\":" + JsonUtil.String(node.Target)
                + ",\"active_self\":" + JsonUtil.Bool(node.ActiveSelf)
                + ",\"active_in_hierarchy\":" + JsonUtil.Bool(node.ActiveInHierarchy)
                + ",\"sibling_index\":" + node.SiblingIndex
                + ",\"has_rect_transform\":" + JsonUtil.Bool(node.HasRectTransform)
                + ",\"rect_transform\":" + (string.IsNullOrEmpty(node.RectTransformJson) ? "null" : node.RectTransformJson)
                + ",\"canvas\":" + (string.IsNullOrEmpty(node.CanvasRenderMode) ? "null" : "{\"render_mode\":" + JsonUtil.String(node.CanvasRenderMode) + "}")
                + ",\"components\":" + ComponentSummariesJson(node.Components)
                + ",\"missing_scripts\":" + node.MissingScripts + "}";
        }

        static string BoundsSnapshotJson(ScanSnapshot snapshot, string boundsId, int maxNodes, bool includeInactive, string resourceUri)
        {
            var bounds = new StringBuilder();
            var rectNodesFound = 0;
            var returned = 0;
            var nodes = new List<NodeSnapshot>(snapshot.NodesById.Values);
            nodes.Sort((left, right) => StringComparer.Ordinal.Compare(left.NodeId, right.NodeId));
            foreach (var node in nodes)
            {
                if (!node.HasRectTransform)
                {
                    continue;
                }

                rectNodesFound++;
                if (!includeInactive && !node.ActiveInHierarchy)
                {
                    continue;
                }
                if (returned >= maxNodes)
                {
                    continue;
                }

                if (bounds.Length > 0)
                {
                    bounds.Append(',');
                }
                bounds.Append(BoundsNodeJson(node));
                returned++;
            }

            return "{\"status\":\"ok\""
                + ",\"bounds_id\":" + JsonUtil.String(boundsId)
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"scan_resource_ref\":" + JsonUtil.String(ScanResourceUri(snapshot.ScanId))
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"source_targets\":" + StringArrayJson(snapshot.SourceTargets)
                + ",\"targets\":" + TargetSummariesJson(snapshot.Targets)
                + ",\"coordinate_space\":\"cached_rect_transform_world_aabb\""
                + ",\"source_contract\":{\"requires_scan_id\":true,\"source_resource_ref\":" + JsonUtil.String(ScanResourceUri(snapshot.ScanId))
                + ",\"uses_generic_scene_screenshot\":false,\"captures_screenshot\":false}"
                + ",\"geometry_evidence\":" + RectTransformGeometryEvidenceJson(snapshot, ScanResourceUri(snapshot.ScanId), resourceUri)
                + ",\"include_inactive\":" + JsonUtil.Bool(includeInactive)
                + ",\"rect_nodes_found\":" + rectNodesFound
                + ",\"bounds_returned\":" + returned
                + ",\"truncated\":" + JsonUtil.Bool(returned < rectNodesFound && returned >= maxNodes)
                + ",\"bounds\":[" + bounds + "]"
                + ",\"artifact_refs\":[]"
                + ",\"read_only\":true"
                + ",\"next_valid_actions\":[\"ugui.resource.read\",\"ugui.layout.analyze\",\"ugui.raycast.analyze\",\"uitk.preview\"]"
                + "}";
        }

        static string RectTransformGeometryEvidenceJson(ScanSnapshot snapshot, string scanResourceRef, string boundsResourceRef)
        {
            var rectNodesFound = RectTransformNodeCount(snapshot);
            var followUpMaxNodes = Math.Max(1, Math.Min(snapshot.NodesIncluded, 5000));
            return "{\"kind\":\"rect_transform_geometry\""
                + ",\"inline_in_scan_default\":false"
                + ",\"cached_at_scan_time\":true"
                + ",\"bounds_command\":\"ugui.bounds.snapshot\""
                + ",\"bounds_follow_up\":{\"command\":\"ugui.bounds.snapshot\",\"args\":{\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"max_nodes\":" + followUpMaxNodes
                + ",\"include_inactive\":true}}"
                + ",\"coordinate_space\":\"cached_rect_transform_world_aabb\""
                + ",\"scan_resource_ref\":" + JsonUtil.String(scanResourceRef)
                + (string.IsNullOrEmpty(boundsResourceRef) ? string.Empty : ",\"bounds_resource_ref\":" + JsonUtil.String(boundsResourceRef))
                + ",\"rect_nodes_found\":" + rectNodesFound
                + ",\"world_bounds_source\":\"RectTransform.GetWorldCorners\""
                + ",\"captures_screenshot\":false"
                + ",\"uses_generic_scene_screenshot\":false"
                + "}";
        }

        static int RectTransformNodeCount(ScanSnapshot snapshot)
        {
            var count = 0;
            foreach (var node in snapshot.NodesById.Values)
            {
                if (node.HasRectTransform)
                {
                    count++;
                }
            }
            return count;
        }

        static string BoundsNodeJson(NodeSnapshot node)
        {
            var worldSize = new Vector2(node.WorldMax.x - node.WorldMin.x, node.WorldMax.y - node.WorldMin.y);
            return "{\"node_id\":" + JsonUtil.String(node.NodeId)
                + ",\"path\":" + JsonUtil.String(node.Path)
                + ",\"target\":" + JsonUtil.String(node.Target)
                + ",\"depth\":" + node.Depth
                + ",\"active_self\":" + JsonUtil.Bool(node.ActiveSelf)
                + ",\"active_in_hierarchy\":" + JsonUtil.Bool(node.ActiveInHierarchy)
                + ",\"canvas_render_mode\":" + JsonUtil.String(node.CanvasRenderMode)
                + ",\"rect_size\":" + Vector2Json(node.RectSize)
                + ",\"world_bounds\":{\"min\":" + Vector2Json(node.WorldMin)
                + ",\"max\":" + Vector2Json(node.WorldMax)
                + ",\"size\":" + Vector2Json(worldSize) + "}"
                + ",\"rect_transform\":" + (string.IsNullOrEmpty(node.RectTransformJson) ? "null" : node.RectTransformJson)
                + "}";
        }

        static string ComponentSummariesJson(List<ComponentSnapshot> components)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < components.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"type\":").Append(JsonUtil.String(components[index].Type));
                builder.Append(",\"name\":").Append(JsonUtil.String(components[index].Name)).Append('}');
            }
            return "[" + builder + "]";
        }

        static void ExtractPersistentEvents(Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            if (component == null)
            {
                return;
            }

            var seenEvents = new HashSet<string>();
            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            for (var type = component.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                var fields = type.GetFields(flags);
                for (var index = 0; index < fields.Length; index++)
                {
                    if (!typeof(UnityEventBase).IsAssignableFrom(fields[index].FieldType))
                    {
                        continue;
                    }

                    var eventName = NormalizeEventMemberName(fields[index].Name);
                    if (!seenEvents.Add(eventName))
                    {
                        continue;
                    }

                    try
                    {
                        AddPersistentEventBindings(fields[index].GetValue(component) as UnityEventBase, eventName, component, node, snapshot);
                    }
                    catch
                    {
                        // Some third-party components expose serialized event fields that throw when reflected in edit mode.
                    }
                }

                var properties = type.GetProperties(flags);
                for (var index = 0; index < properties.Length; index++)
                {
                    if (!properties[index].CanRead || properties[index].GetIndexParameters().Length != 0 || !typeof(UnityEventBase).IsAssignableFrom(properties[index].PropertyType))
                    {
                        continue;
                    }

                    var eventName = NormalizeEventMemberName(properties[index].Name);
                    if (!seenEvents.Add(eventName))
                    {
                        continue;
                    }

                    try
                    {
                        AddPersistentEventBindings(properties[index].GetValue(component, null) as UnityEventBase, eventName, component, node, snapshot);
                    }
                    catch
                    {
                        // Keep scan read-only and resilient when a property getter is not safe in edit mode.
                    }
                }
            }
        }

        static void AddPersistentEventBindings(UnityEventBase unityEvent, string eventName, Component component, NodeSnapshot node, ScanSnapshot snapshot)
        {
            if (unityEvent == null)
            {
                return;
            }

            var componentType = component.GetType();
            var componentTypeName = componentType.FullName ?? componentType.Name;
            var eventType = TypeShortName(componentTypeName) + "." + eventName;
            var eventClass = unityEvent.GetType().FullName ?? unityEvent.GetType().Name;
            var count = unityEvent.GetPersistentEventCount();
            for (var index = 0; index < count; index++)
            {
                var target = unityEvent.GetPersistentTarget(index);
                var methodName = unityEvent.GetPersistentMethodName(index) ?? string.Empty;
                var methodAmbiguous = false;
                var methodSignature = TryMethodSignature(target, methodName, out methodAmbiguous);
                snapshot.EventBindings.Add(new EventBindingSnapshot
                {
                    NodeId = node.NodeId,
                    Path = node.Path,
                    ComponentType = componentTypeName,
                    ComponentName = componentType.Name,
                    EventName = eventName,
                    EventType = eventType,
                    EventClass = eventClass,
                    ListenerIndex = index,
                    TargetType = target == null ? string.Empty : target.GetType().FullName ?? target.GetType().Name,
                    TargetName = target == null ? string.Empty : target.name,
                    TargetObjectId = RuntimeObjectIdentity(target),
                    MethodName = methodName,
                    ListenerState = unityEvent.GetPersistentListenerState(index).ToString(),
                    MethodSignature = methodSignature,
                    MethodAmbiguous = methodAmbiguous,
                });
                Increment(snapshot.EventCounts, eventType);
            }
        }

        static string EventsExtractJson(ScanSnapshot snapshot, List<string> eventTypes, bool includeTargetPaths, bool includeMethodSignatures, int maxEvents)
        {
            var returned = new List<EventBindingSnapshot>();
            var eventCounts = new Dictionary<string, int>();
            var bindingsFound = 0;
            var missingTargets = 0;
            var ambiguousMethods = 0;
            for (var index = 0; index < snapshot.EventBindings.Count; index++)
            {
                var binding = snapshot.EventBindings[index];
                if (!EventBindingMatchesFilters(binding, eventTypes))
                {
                    continue;
                }

                bindingsFound++;
                Increment(eventCounts, binding.EventType);
                if (string.IsNullOrEmpty(binding.TargetObjectId))
                {
                    missingTargets++;
                }
                if (binding.MethodAmbiguous)
                {
                    ambiguousMethods++;
                }
                if (returned.Count < maxEvents)
                {
                    returned.Add(binding);
                }
            }

            var eventsTruncated = bindingsFound > returned.Count;
            var eventsId = "ugui_events_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var eventsResourceUri = EventsResourceUri(eventsId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"events_id\":" + JsonUtil.String(eventsId)
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"read_only\":true"
                + ",\"cache\":{\"storage\":\"memory\",\"max_scans\":" + MaxCachedScans + "}"
                + ",\"scan_truncated\":" + JsonUtil.Bool(snapshot.Truncated)
                + ",\"event_types_filter\":" + StringArrayJson(eventTypes)
                + ",\"bindings_found\":" + bindingsFound
                + ",\"returned_bindings_count\":" + returned.Count
                + ",\"max_events\":" + maxEvents
                + ",\"events_truncated\":" + JsonUtil.Bool(eventsTruncated)
                + ",\"missing_targets\":" + missingTargets
                + ",\"ambiguous_methods\":" + ambiguousMethods
                + ",\"event_counts\":" + CountsJson(eventCounts)
                + ",\"events\":" + EventBindingsJson(returned, snapshot, includeTargetPaths, includeMethodSignatures)
                + ",\"summary\":" + JsonUtil.String(bindingsFound + " persistent UnityEvent binding(s) matched the request")
                + ",\"resource_refs\":" + ResourceRefsJson(eventsResourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + EventsWarningsJson(snapshot.Truncated, eventsTruncated)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"ugui.canvas.inspect_node\",\"ugui.canvas.report\"]"
                + ",\"risk\":\"low\""
                + ",\"requires_approval\":false}";
            StoreResource(eventsResourceUri, resultJson, "application/json");
            return resultJson;
        }

        static bool EventBindingMatchesFilters(EventBindingSnapshot binding, List<string> eventTypes)
        {
            if (eventTypes.Count == 0)
            {
                return true;
            }

            for (var index = 0; index < eventTypes.Count; index++)
            {
                var filter = eventTypes[index];
                if (string.Equals(binding.EventType, filter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(binding.EventName, filter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(binding.ComponentName, filter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(binding.ComponentType, filter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(binding.ComponentType + "." + binding.EventName, filter, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(binding.EventClass, filter, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        static string EventBindingsJson(List<EventBindingSnapshot> bindings, ScanSnapshot snapshot, bool includeTargetPaths, bool includeMethodSignatures)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < bindings.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                var binding = bindings[index];
                builder.Append("{\"node_id\":").Append(JsonUtil.String(binding.NodeId));
                builder.Append(",\"path\":").Append(JsonUtil.String(binding.Path));
                builder.Append(",\"component_type\":").Append(JsonUtil.String(binding.ComponentType));
                builder.Append(",\"component_name\":").Append(JsonUtil.String(binding.ComponentName));
                builder.Append(",\"event_name\":").Append(JsonUtil.String(binding.EventName));
                builder.Append(",\"event_type\":").Append(JsonUtil.String(binding.EventType));
                builder.Append(",\"event_class\":").Append(JsonUtil.String(binding.EventClass));
                builder.Append(",\"listener_index\":").Append(binding.ListenerIndex);
                builder.Append(",\"target_type\":").Append(JsonUtil.String(binding.TargetType));
                builder.Append(",\"target_name\":").Append(JsonUtil.String(binding.TargetName));
                builder.Append(",\"method_name\":").Append(JsonUtil.String(binding.MethodName));
                builder.Append(",\"listener_state\":").Append(JsonUtil.String(binding.ListenerState));
                builder.Append(",\"missing_target\":").Append(JsonUtil.Bool(string.IsNullOrEmpty(binding.TargetObjectId)));
                builder.Append(",\"method_ambiguous\":").Append(JsonUtil.Bool(binding.MethodAmbiguous));
                if (includeTargetPaths)
                {
                    builder.Append(",\"target_path\":").Append(JsonUtil.String(PathForObject(snapshot, binding.TargetObjectId)));
                }
                if (includeMethodSignatures)
                {
                    builder.Append(",\"method_signature\":").Append(JsonUtil.String(binding.MethodSignature));
                }
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string EventsWarningsJson(bool scanTruncated, bool eventsTruncated)
        {
            var warnings = new List<string>();
            if (scanTruncated)
            {
                warnings.Add("event extraction covers only scanned nodes; omitted hierarchy may contain additional bindings");
            }
            if (eventsTruncated)
            {
                warnings.Add("inline event bindings are capped by max_events; rerun with narrower event_types or a higher max_events within schema limits");
            }
            return StringArrayJson(warnings);
        }

        static string NormalizeEventMemberName(string name)
        {
            if (name.StartsWith("m_", StringComparison.Ordinal) && name.Length > 2)
            {
                var trimmed = name.Substring(2);
                return char.ToLowerInvariant(trimmed[0]) + trimmed.Substring(1);
            }
            return name;
        }

        static string PathForObject(ScanSnapshot snapshot, string instanceId)
        {
            if (string.IsNullOrEmpty(instanceId))
            {
                return string.Empty;
            }

            string path;
            return snapshot.ObjectPathsByInstanceId.TryGetValue(instanceId, out path) ? path : string.Empty;
        }

        static string RuntimeObjectIdentity(UnityEngine.Object unityObject)
        {
            if (unityObject == null)
            {
                return string.Empty;
            }

#if UNITY_6000_4_OR_NEWER
            return UnityEngine.EntityId.ToULong(unityObject.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return unityObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        static string TryMethodSignature(UnityEngine.Object target, string methodName, out bool ambiguous)
        {
            ambiguous = false;
            if (target == null || string.IsNullOrEmpty(methodName))
            {
                return string.Empty;
            }

            var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            MethodInfo found = null;
            var count = 0;
            for (var type = target.GetType(); type != null && type != typeof(object); type = type.BaseType)
            {
                var methods = type.GetMethods(flags);
                for (var index = 0; index < methods.Length; index++)
                {
                    if (methods[index].Name != methodName)
                    {
                        continue;
                    }
                    found = methods[index];
                    count++;
                }
            }

            ambiguous = count > 1;
            return count == 1 && found != null ? FormatMethodSignature(found) : string.Empty;
        }

        static string FormatMethodSignature(MethodInfo method)
        {
            var builder = new StringBuilder();
            builder.Append(TypeShortName(method.ReturnType.FullName ?? method.ReturnType.Name));
            builder.Append(' ').Append(method.Name).Append('(');
            var parameters = method.GetParameters();
            for (var index = 0; index < parameters.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(", ");
                }
                builder.Append(TypeShortName(parameters[index].ParameterType.FullName ?? parameters[index].ParameterType.Name));
            }
            builder.Append(')');
            return builder.ToString();
        }

        static string LayoutAnalysisJson(ScanSnapshot snapshot, List<Vector2> screenSizes, bool includeAbsolutePositions, int maxNodes, List<PrefabLayoutOverride> requestOverrides)
        {
            var findings = new List<LayoutFinding>();
            var severityCounts = new Dictionary<string, int>();
            var codeCounts = new Dictionary<string, int>();
            var totalFindings = 0;
            var overrides = snapshot.Overrides.Count == 0 ? requestOverrides : snapshot.Overrides;
            for (var index = 0; index < overrides.Count; index++)
            {
                var applied = overrides[index].Status == "applied";
                var ignored = overrides[index].Status == "ignored";
                var code = applied ? "prefab_layout_override_applied" : ignored ? "prefab_layout_override_ignored" : "prefab_layout_override_invalid";
                var severity = applied || ignored ? "info" : "warning";
                var message = applied ? "Prefab layout override was applied to a temporary preview instance during scan; prefab asset was not saved or dirtied."
                    : ignored ? "Prefab layout override was ignored for this cached scan."
                    : "Prefab layout override is invalid or could not be applied to the cached scan.";
                AddLayoutFinding(findings, new LayoutFinding(severity, code, string.Empty, overrides[index].RootPath, message, true), ref totalFindings, severityCounts, codeCounts);
            }

            var nodes = new List<NodeSnapshot>(snapshot.NodesById.Values);
            nodes.Sort((left, right) => string.CompareOrdinal(left.NodeId, right.NodeId));
            var nodesAnalyzed = 0;
            for (var index = 0; index < nodes.Count && nodesAnalyzed < maxNodes; index++)
            {
                AnalyzeLayoutNode(snapshot, nodes[index], screenSizes, includeAbsolutePositions, findings, ref totalFindings, severityCounts, codeCounts);
                nodesAnalyzed++;
            }

            SortLayoutFindingsBySeverity(findings);
            var findingsTruncated = findings.Count > MaxLayoutFindings;
            if (findingsTruncated)
            {
                findings.RemoveRange(MaxLayoutFindings, findings.Count - MaxLayoutFindings);
            }

            var analysisId = "ugui_layout_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var layoutResourceUri = LayoutResourceUri(analysisId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"analysis_id\":" + JsonUtil.String(analysisId)
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"read_only\":true"
                + ",\"cache\":{\"storage\":\"memory\",\"max_scans\":" + MaxCachedScans + "}"
                + ",\"screen_sizes\":" + ScreenSizesJson(screenSizes)
                + ",\"include_absolute_positions\":" + JsonUtil.Bool(includeAbsolutePositions)
                + ",\"nodes_analyzed\":" + nodesAnalyzed
                + ",\"max_nodes\":" + maxNodes
                + ",\"snapshot_truncated\":" + JsonUtil.Bool(snapshot.Truncated)
                + ",\"analysis_truncated\":" + JsonUtil.Bool(nodes.Count > maxNodes)
                + ",\"findings_truncated\":" + JsonUtil.Bool(findingsTruncated)
                + ",\"findings_count\":" + totalFindings
                + ",\"returned_findings_count\":" + findings.Count
                + ",\"findings_by_severity\":" + CountsJson(severityCounts)
                + ",\"findings_by_code\":" + CountsJson(codeCounts)
                + ",\"prefab_layout_overrides\":" + PrefabLayoutOverridesJson(overrides)
                + ",\"override_summary\":" + OverrideSummaryJson(overrides)
                + ",\"findings\":" + LayoutFindingsJson(findings)
                + ",\"resource_refs\":" + ResourceRefsJson(layoutResourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + LayoutWarningsJson(snapshot.Truncated, nodes.Count > maxNodes, findingsTruncated)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"ugui.canvas.inspect_node\",\"ugui.canvas.report\"]"
                + ",\"risk\":\"low\""
                + ",\"requires_approval\":false}";
            StoreResource(layoutResourceUri, resultJson, "application/json");
            return resultJson;
        }

        static void AnalyzeLayoutNode(ScanSnapshot snapshot, NodeSnapshot node, List<Vector2> screenSizes, bool includeAbsolutePositions, List<LayoutFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            if (!node.HasRectTransform)
            {
                return;
            }

            var stretchX = !Approximately(node.AnchorMin.x, node.AnchorMax.x);
            var stretchY = !Approximately(node.AnchorMin.y, node.AnchorMax.y);
            if (!stretchX && !stretchY && Math.Abs(node.SizeDelta.x) > 0.5f && Math.Abs(node.SizeDelta.y) > 0.5f)
            {
                AddLayoutFinding(findings, "info", "fixed_size_anchors", node, "RectTransform uses fixed anchors and sizeDelta; verify it adapts to requested screen sizes.", ref totalFindings, severityCounts, codeCounts);
            }
            if ((stretchX && Math.Abs(node.SizeDelta.x) > 0.5f) || (stretchY && Math.Abs(node.SizeDelta.y) > 0.5f))
            {
                AddLayoutFinding(findings, "warning", "stretch_with_size_delta", node, "Stretched RectTransform also has non-zero sizeDelta on a stretched axis.", ref totalFindings, severityCounts, codeCounts);
            }
            if (stretchX != stretchY)
            {
                AddLayoutFinding(findings, "info", "mixed_anchor_modes", node, "RectTransform stretches on only one axis; verify the mixed anchor mode is intentional.", ref totalFindings, severityCounts, codeCounts);
            }
            if (Math.Abs(node.RectSize.x) <= 0.5f || Math.Abs(node.RectSize.y) <= 0.5f)
            {
                AddLayoutFinding(findings, "warning", "zero_size_rect", node, "RectTransform has near-zero width or height.", ref totalFindings, severityCounts, codeCounts);
            }
            if (Math.Abs(node.LocalScale.x - node.LocalScale.y) > 0.01f || Math.Abs(node.LocalScale.x - node.LocalScale.z) > 0.01f)
            {
                AddLayoutFinding(findings, "warning", "non_uniform_scale", node, "RectTransform has non-uniform local scale, which can distort layout and hit areas.", ref totalFindings, severityCounts, codeCounts);
            }
            if (Math.Abs(NormalizeAngle(node.LocalEulerAngles.x)) > 0.01f || Math.Abs(NormalizeAngle(node.LocalEulerAngles.y)) > 0.01f || Math.Abs(NormalizeAngle(node.LocalEulerAngles.z)) > 0.01f)
            {
                AddLayoutFinding(findings, "info", "rotated_rect_transform", node, "RectTransform has local rotation; verify bounds and hit areas at target screen sizes.", ref totalFindings, severityCounts, codeCounts);
            }

            if (HasAnyLayoutGroup(node))
            {
                var childOffsetCount = CountLayoutGroupChildrenWithOffsets(snapshot, node);
                if (childOffsetCount > 0)
                {
                    AddLayoutFinding(findings, "info", "layout_group_child_offsets", node, "LayoutGroup has immediate RectTransform children with anchored offsets in the cached snapshot.", ref totalFindings, severityCounts, codeCounts);
                }
            }
            if (HasAnyLayoutGroup(node) && HasComponentNamed(node, "UnityEngine.UI.ContentSizeFitter"))
            {
                AddLayoutFinding(findings, "warning", "content_size_fitter_layout_group", node, "ContentSizeFitter is on the same object as a LayoutGroup; verify there is no layout rebuild loop.", ref totalFindings, severityCounts, codeCounts);
            }
            if (HasComponentNamed(node, "UnityEngine.UI.ScrollRect") && !HasScrollRectStructure(snapshot, node))
            {
                AddLayoutFinding(findings, "warning", "scrollrect_missing_viewport_or_content", node, "ScrollRect does not expose obvious Viewport and Content descendants in the cached hierarchy.", ref totalFindings, severityCounts, codeCounts);
            }
            if (includeAbsolutePositions)
            {
                AddOffscreenPreviewFindings(node, screenSizes, findings, ref totalFindings, severityCounts, codeCounts);
            }
        }

        static void AddOffscreenPreviewFindings(NodeSnapshot node, List<Vector2> screenSizes, List<LayoutFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            for (var index = 0; index < screenSizes.Count; index++)
            {
                var size = screenSizes[index];
                if (node.WorldMax.x < 0 || node.WorldMin.x > size.x || node.WorldMax.y < 0 || node.WorldMin.y > size.y)
                {
                    var finding = new LayoutFinding("info", "absolute_bounds_outside_screen_preview", node.NodeId, node.Path, "Cached world bounds are outside one requested screen size; treat this as a preview because Canvas render mode can change coordinate meaning.", false);
                    finding.ScreenSize = size;
                    AddLayoutFinding(findings, finding, ref totalFindings, severityCounts, codeCounts);
                    return;
                }
            }
        }

        static void AddLayoutFinding(List<LayoutFinding> findings, string severity, string code, NodeSnapshot node, string message, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            AddLayoutFinding(findings, new LayoutFinding(severity, code, node.NodeId, node.Path, message, false), ref totalFindings, severityCounts, codeCounts);
        }

        static void AddLayoutFinding(List<LayoutFinding> findings, LayoutFinding finding, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            totalFindings++;
            Increment(severityCounts, finding.Severity);
            Increment(codeCounts, finding.Code);
            if (findings.Count < MaxLayoutFindings + 1)
            {
                findings.Add(finding);
                return;
            }

            var lowestIndex = FindLowestSeverityFindingIndex(findings);
            if (lowestIndex >= 0 && FindingRank(finding) > FindingRank(findings[lowestIndex]))
            {
                findings[lowestIndex] = finding;
            }
        }

        static int FindLowestSeverityFindingIndex(List<LayoutFinding> findings)
        {
            var indexToReplace = -1;
            var lowestRank = int.MaxValue;
            for (var index = 0; index < findings.Count; index++)
            {
                var rank = FindingRank(findings[index]);
                if (rank < lowestRank)
                {
                    lowestRank = rank;
                    indexToReplace = index;
                }
            }
            return indexToReplace;
        }

        static void SortLayoutFindingsBySeverity(List<LayoutFinding> findings)
        {
            findings.Sort((left, right) =>
            {
                var rank = FindingRank(right).CompareTo(FindingRank(left));
                return rank != 0 ? rank : string.CompareOrdinal(left.NodeId + left.Code, right.NodeId + right.Code);
            });
        }

        static int FindingRank(LayoutFinding finding)
        {
            return SeverityRank(finding.Severity) + (finding.OverrideDependent ? 10 : 0);
        }

        static int SeverityRank(string severity)
        {
            switch (severity)
            {
                case "error":
                    return 3;
                case "warning":
                    return 2;
                case "info":
                    return 1;
                default:
                    return 0;
            }
        }

        static int CountLayoutGroupChildrenWithOffsets(ScanSnapshot snapshot, NodeSnapshot node)
        {
            var count = 0;
            foreach (var child in snapshot.NodesById.Values)
            {
                if (child.ParentNodeId == node.NodeId && child.HasRectTransform && (Math.Abs(child.AnchoredPosition.x) > 0.5f || Math.Abs(child.AnchoredPosition.y) > 0.5f))
                {
                    count++;
                }
            }
            return count;
        }

        static bool HasScrollRectStructure(ScanSnapshot snapshot, NodeSnapshot node)
        {
            var hasViewport = false;
            var hasContent = false;
            var prefix = node.Path + "/";
            foreach (var child in snapshot.NodesById.Values)
            {
                if (!child.Path.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }
                if (string.Equals(child.Name, "Viewport", StringComparison.OrdinalIgnoreCase) || HasComponentNamed(child, "UnityEngine.UI.RectMask2D"))
                {
                    hasViewport = true;
                }
                if (string.Equals(child.Name, "Content", StringComparison.OrdinalIgnoreCase))
                {
                    hasContent = true;
                }
            }
            return hasViewport && hasContent;
        }

        static bool HasAnyLayoutGroup(NodeSnapshot node)
        {
            return HasComponentNamed(node, "UnityEngine.UI.HorizontalLayoutGroup")
                || HasComponentNamed(node, "UnityEngine.UI.VerticalLayoutGroup")
                || HasComponentNamed(node, "UnityEngine.UI.GridLayoutGroup");
        }

        static bool HasComponentNamed(NodeSnapshot node, string typeName)
        {
            for (var index = 0; index < node.Components.Count; index++)
            {
                if (node.Components[index].Type == typeName || node.Components[index].Type.EndsWith("." + TypeShortName(typeName), StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        static string TypeShortName(string typeName)
        {
            var index = typeName.LastIndexOf('.');
            return index < 0 ? typeName : typeName.Substring(index + 1);
        }

        static string LayoutFindingsJson(List<LayoutFinding> findings)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < findings.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"severity\":").Append(JsonUtil.String(findings[index].Severity));
                builder.Append(",\"code\":").Append(JsonUtil.String(findings[index].Code));
                if (!string.IsNullOrEmpty(findings[index].NodeId))
                {
                    builder.Append(",\"node_id\":").Append(JsonUtil.String(findings[index].NodeId));
                }
                if (!string.IsNullOrEmpty(findings[index].Path))
                {
                    builder.Append(",\"path\":").Append(JsonUtil.String(findings[index].Path));
                }
                if (findings[index].ScreenSize.x > 0 && findings[index].ScreenSize.y > 0)
                {
                    builder.Append(",\"screen_size\":").Append(Vector2Json(findings[index].ScreenSize));
                }
                builder.Append(",\"message\":").Append(JsonUtil.String(findings[index].Message));
                builder.Append(",\"override_dependent\":").Append(JsonUtil.Bool(findings[index].OverrideDependent));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string LayoutWarningsJson(bool scanTruncated, bool analysisTruncated, bool findingsTruncated)
        {
            var warnings = new List<string>();
            if (scanTruncated)
            {
                warnings.Add("layout analysis covers only scanned nodes; omitted hierarchy may contain additional issues");
            }
            if (analysisTruncated)
            {
                warnings.Add("layout analysis stopped at max_nodes; rerun with a higher max_nodes or narrower target for fuller coverage");
            }
            if (findingsTruncated)
            {
                warnings.Add("inline findings are capped and prioritized by severity; use findings_by_severity and findings_by_code for aggregate counts");
            }
            return StringArrayJson(warnings);
        }

        static string NavigationAnalysisJson(ScanSnapshot snapshot, bool includeEdges, int maxSelectables, int maxEdges)
        {
            var nodes = new List<NavigationNodeSnapshot>(snapshot.NavigationNodes);
            nodes.Sort((left, right) => string.CompareOrdinal(left.Path + left.ComponentName, right.Path + right.ComponentName));

            var nodesByComponentId = new Dictionary<string, NavigationNodeSnapshot>();
            var nodesByPath = new Dictionary<string, NavigationNodeSnapshot>();
            for (var index = 0; index < nodes.Count; index++)
            {
                if (!string.IsNullOrEmpty(nodes[index].ComponentInstanceId))
                {
                    nodesByComponentId[nodes[index].ComponentInstanceId] = nodes[index];
                }
                if (!string.IsNullOrEmpty(nodes[index].Path) && !nodesByPath.ContainsKey(nodes[index].Path))
                {
                    nodesByPath[nodes[index].Path] = nodes[index];
                }
            }

            var modeCounts = new Dictionary<string, int>();
            var severityCounts = new Dictionary<string, int>();
            var codeCounts = new Dictionary<string, int>();
            var findings = new List<NavigationFinding>();
            var totalFindings = 0;
            var activeSelectables = 0;
            var enabledSelectables = 0;
            var interactableSelectables = 0;
            var viableDefaultCandidates = 0;
            var automaticNavigation = 0;
            var explicitNavigation = 0;
            var noneNavigation = 0;
            var serializedEdges = 0;
            var explicitEdges = 0;
            var missingTargetEdges = 0;
            var inactiveTargetEdges = 0;
            var disabledTargetEdges = 0;
            var nonInteractableTargetEdges = 0;

            if (nodes.Count == 0)
            {
                AddNavigationFinding(findings, "info", "no_selectables_found", string.Empty, string.Empty, "No UnityEngine.UI.Selectable components were found in the cached uGUI scan.", ref totalFindings, severityCounts, codeCounts);
            }

            for (var index = 0; index < nodes.Count; index++)
            {
                var node = nodes[index];
                Increment(modeCounts, string.IsNullOrEmpty(node.Mode) ? "unknown" : node.Mode);
                if (node.ActiveInHierarchy)
                {
                    activeSelectables++;
                }
                if (node.ComponentEnabled)
                {
                    enabledSelectables++;
                }
                if (node.Interactable)
                {
                    interactableSelectables++;
                }
                if (IsViableSelectable(node))
                {
                    viableDefaultCandidates++;
                }
                if (IsAutomaticNavigationMode(node.Mode))
                {
                    automaticNavigation++;
                }
                if (IsExplicitNavigation(node.Mode))
                {
                    explicitNavigation++;
                }
                if (string.Equals(node.Mode, "None", StringComparison.OrdinalIgnoreCase))
                {
                    noneNavigation++;
                }

                AnalyzeNavigationNode(node, findings, ref totalFindings, severityCounts, codeCounts);
                for (var edgeIndex = 0; edgeIndex < node.Edges.Count; edgeIndex++)
                {
                    var edge = node.Edges[edgeIndex];
                    serializedEdges++;
                    edge.ActiveInMode = IsExplicitNavigation(node.Mode);
                    NavigationNodeSnapshot targetNode;
                    if (nodesByComponentId.TryGetValue(edge.TargetObjectId, out targetNode))
                    {
                        edge.ToNodeId = targetNode.NodeId;
                        edge.ToPath = targetNode.Path;
                    }
                    else
                    {
                        edge.TargetMissingInScan = true;
                    }

                    if (edge.ActiveInMode)
                    {
                        explicitEdges++;
                        AnalyzeNavigationEdge(edge, targetNode, findings, ref totalFindings, severityCounts, codeCounts, ref missingTargetEdges, ref inactiveTargetEdges, ref disabledTargetEdges, ref nonInteractableTargetEdges);
                    }
                }
            }

            if (nodes.Count > 0 && viableDefaultCandidates == 0)
            {
                AddNavigationFinding(findings, "warning", "missing_default_selection_candidates", string.Empty, string.Empty, "No active, enabled and interactable Selectable was found as a safe default keyboard/gamepad focus candidate.", ref totalFindings, severityCounts, codeCounts);
            }
            if (nodes.Count > 0 && snapshot.EventSystems.Count == 0)
            {
                AddNavigationFinding(findings, "info", "event_system_not_in_scan", string.Empty, string.Empty, "No EventSystem component was found in this scan; full-scene keyboard/gamepad focus may need separate verification.", ref totalFindings, severityCounts, codeCounts);
            }

            AnalyzeEventSystems(snapshot, nodesByPath, findings, ref totalFindings, severityCounts, codeCounts);
            DetectNavigationCycles(nodes, nodesByComponentId, findings, ref totalFindings, severityCounts, codeCounts);

            SortNavigationFindingsBySeverity(findings);
            var findingsTruncated = findings.Count > MaxNavigationFindings;
            if (findingsTruncated)
            {
                findings.RemoveRange(MaxNavigationFindings, findings.Count - MaxNavigationFindings);
            }

            var returnedSelectables = Math.Min(maxSelectables, nodes.Count);
            var selectablesTruncated = returnedSelectables < nodes.Count;
            var returnedEdges = Math.Min(maxEdges, serializedEdges);
            var edgesTruncated = returnedEdges < serializedEdges;
            var navigationId = "ugui_navigation_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var navigationResourceUri = NavigationResourceUri(navigationId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"navigation_id\":" + JsonUtil.String(navigationId)
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"read_only\":true"
                + ",\"cache\":{\"storage\":\"memory\",\"max_scans\":" + MaxCachedScans + "}"
                + ",\"scan_truncated\":" + JsonUtil.Bool(snapshot.Truncated)
                + ",\"selectables_found\":" + nodes.Count
                + ",\"returned_selectables_count\":" + returnedSelectables
                + ",\"max_selectables\":" + maxSelectables
                + ",\"selectables_truncated\":" + JsonUtil.Bool(selectablesTruncated)
                + ",\"active_selectables\":" + activeSelectables
                + ",\"enabled_selectables\":" + enabledSelectables
                + ",\"interactable_selectables\":" + interactableSelectables
                + ",\"viable_default_candidates\":" + viableDefaultCandidates
                + ",\"event_systems_found\":" + snapshot.EventSystems.Count
                + ",\"automatic_navigation\":" + automaticNavigation
                + ",\"explicit_navigation\":" + explicitNavigation
                + ",\"none_navigation\":" + noneNavigation
                + ",\"mode_counts\":" + CountsJson(modeCounts)
                + ",\"serialized_edges_count\":" + serializedEdges
                + ",\"explicit_edges_count\":" + explicitEdges
                + ",\"returned_edges_count\":" + (includeEdges ? returnedEdges : 0)
                + ",\"max_edges\":" + maxEdges
                + ",\"edges_truncated\":" + JsonUtil.Bool(includeEdges && edgesTruncated)
                + ",\"missing_target_edges\":" + missingTargetEdges
                + ",\"inactive_target_edges\":" + inactiveTargetEdges
                + ",\"disabled_target_edges\":" + disabledTargetEdges
                + ",\"non_interactable_target_edges\":" + nonInteractableTargetEdges
                + ",\"findings_truncated\":" + JsonUtil.Bool(findingsTruncated)
                + ",\"findings_count\":" + totalFindings
                + ",\"returned_findings_count\":" + findings.Count
                + ",\"findings_by_severity\":" + CountsJson(severityCounts)
                + ",\"findings_by_code\":" + CountsJson(codeCounts)
                + ",\"event_systems\":" + EventSystemsJson(snapshot)
                + ",\"selectables\":" + NavigationSelectablesJson(nodes, returnedSelectables)
                + ",\"edges\":" + (includeEdges ? NavigationEdgesJson(nodes, returnedEdges) : "[]")
                + ",\"findings\":" + NavigationFindingsJson(findings)
                + ",\"summary\":" + JsonUtil.String(nodes.Count + " Selectable navigation node(s) analyzed")
                + ",\"resource_refs\":" + ResourceRefsJson(navigationResourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + NavigationWarningsJson(snapshot.Truncated, selectablesTruncated, includeEdges && edgesTruncated, findingsTruncated)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"ugui.canvas.inspect_node\",\"ugui.canvas.report\",\"ugui.events.extract\",\"ugui.layout.analyze\"]"
                + ",\"risk\":\"low\""
                + ",\"requires_approval\":false}";
            StoreResource(navigationResourceUri, resultJson, "application/json");
            return resultJson;
        }

        static void AnalyzeNavigationNode(NavigationNodeSnapshot node, List<NavigationFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            if (!node.ActiveInHierarchy)
            {
                AddNavigationFinding(findings, "warning", "inactive_selectable", node.NodeId, node.Path, "Selectable is inactive in hierarchy and cannot receive keyboard/gamepad focus.", ref totalFindings, severityCounts, codeCounts);
            }
            else if (!node.ComponentEnabled)
            {
                AddNavigationFinding(findings, "warning", "disabled_selectable", node.NodeId, node.Path, "Selectable component is disabled and cannot receive keyboard/gamepad focus.", ref totalFindings, severityCounts, codeCounts);
            }
            else if (!node.Interactable)
            {
                AddNavigationFinding(findings, "info", "non_interactable_selectable", node.NodeId, node.Path, "Selectable is active but not interactable.", ref totalFindings, severityCounts, codeCounts);
            }

            if (IsAutomaticNavigationMode(node.Mode))
            {
                AddNavigationFinding(findings, "info", "automatic_navigation", node.NodeId, node.Path, "Selectable uses automatic navigation; verify generated focus order with the current layout.", ref totalFindings, severityCounts, codeCounts);
            }
            else if (IsExplicitNavigation(node.Mode))
            {
                if (node.Edges.Count == 0)
                {
                    AddNavigationFinding(findings, "warning", "explicit_navigation_without_targets", node.NodeId, node.Path, "Selectable uses explicit navigation but has no explicit direction targets.", ref totalFindings, severityCounts, codeCounts);
                }
                else if (node.Edges.Count < 4)
                {
                    AddNavigationFinding(findings, "info", "explicit_navigation_partial", node.NodeId, node.Path, "Selectable uses explicit navigation with one or more missing direction targets.", ref totalFindings, severityCounts, codeCounts);
                }
            }
            else if (string.Equals(node.Mode, "None", StringComparison.OrdinalIgnoreCase) && IsViableSelectable(node))
            {
                AddNavigationFinding(findings, "info", "navigation_mode_none", node.NodeId, node.Path, "Selectable is viable for pointer input but has navigation mode None for keyboard/gamepad focus.", ref totalFindings, severityCounts, codeCounts);
            }
            else if (string.IsNullOrEmpty(node.Mode))
            {
                AddNavigationFinding(findings, "warning", "navigation_mode_unknown", node.NodeId, node.Path, "Selectable navigation mode could not be read through the cached scan.", ref totalFindings, severityCounts, codeCounts);
            }
        }

        static void AnalyzeNavigationEdge(NavigationEdgeSnapshot edge, NavigationNodeSnapshot targetNode, List<NavigationFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts, ref int missingTargetEdges, ref int inactiveTargetEdges, ref int disabledTargetEdges, ref int nonInteractableTargetEdges)
        {
            if (edge.TargetMissingInScan || targetNode == null)
            {
                missingTargetEdges++;
                AddNavigationFinding(findings, "warning", "navigation_target_not_in_scan", edge.FromNodeId, edge.FromPath, "Explicit navigation target for direction " + edge.Direction + " was not found in the cached scan.", ref totalFindings, severityCounts, codeCounts);
                return;
            }
            if (edge.TargetObjectId == edge.FromComponentId)
            {
                AddNavigationFinding(findings, "warning", "navigation_self_cycle", edge.FromNodeId, edge.FromPath, "Explicit navigation direction " + edge.Direction + " points back to the same Selectable.", ref totalFindings, severityCounts, codeCounts);
            }
            if (!targetNode.ActiveInHierarchy)
            {
                inactiveTargetEdges++;
                AddNavigationFinding(findings, "warning", "navigation_target_inactive", edge.FromNodeId, edge.FromPath, "Explicit navigation direction " + edge.Direction + " targets an inactive Selectable.", ref totalFindings, severityCounts, codeCounts);
            }
            else if (!targetNode.ComponentEnabled)
            {
                disabledTargetEdges++;
                AddNavigationFinding(findings, "warning", "navigation_target_disabled", edge.FromNodeId, edge.FromPath, "Explicit navigation direction " + edge.Direction + " targets a disabled Selectable.", ref totalFindings, severityCounts, codeCounts);
            }
            else if (!targetNode.Interactable)
            {
                nonInteractableTargetEdges++;
                AddNavigationFinding(findings, "info", "navigation_target_not_interactable", edge.FromNodeId, edge.FromPath, "Explicit navigation direction " + edge.Direction + " targets a non-interactable Selectable.", ref totalFindings, severityCounts, codeCounts);
            }
        }

        static void AnalyzeEventSystems(ScanSnapshot snapshot, Dictionary<string, NavigationNodeSnapshot> nodesByPath, List<NavigationFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            for (var index = 0; index < snapshot.EventSystems.Count; index++)
            {
                var eventSystem = snapshot.EventSystems[index];
                if (string.IsNullOrEmpty(eventSystem.FirstSelectedObjectId))
                {
                    AddNavigationFinding(findings, "info", "default_selection_not_configured", eventSystem.NodeId, eventSystem.Path, "EventSystem has no firstSelectedGameObject configured in the cached scan.", ref totalFindings, severityCounts, codeCounts);
                    continue;
                }

                var firstSelectedPath = PathForObject(snapshot, eventSystem.FirstSelectedObjectId);
                if (string.IsNullOrEmpty(firstSelectedPath))
                {
                    AddNavigationFinding(findings, "warning", "default_selection_target_not_in_scan", eventSystem.NodeId, eventSystem.Path, "EventSystem firstSelectedGameObject was not found in the cached scan.", ref totalFindings, severityCounts, codeCounts);
                    continue;
                }

                NavigationNodeSnapshot targetNode;
                if (!nodesByPath.TryGetValue(firstSelectedPath, out targetNode))
                {
                    AddNavigationFinding(findings, "warning", "default_selection_not_selectable", eventSystem.NodeId, eventSystem.Path, "EventSystem firstSelectedGameObject is present but is not a Selectable in the cached scan.", ref totalFindings, severityCounts, codeCounts);
                    continue;
                }
                if (!IsViableSelectable(targetNode))
                {
                    AddNavigationFinding(findings, "warning", "default_selection_not_interactable", targetNode.NodeId, targetNode.Path, "EventSystem firstSelectedGameObject is not active, enabled and interactable.", ref totalFindings, severityCounts, codeCounts);
                }
            }
        }

        static void DetectNavigationCycles(List<NavigationNodeSnapshot> nodes, Dictionary<string, NavigationNodeSnapshot> nodesByComponentId, List<NavigationFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            var reported = new HashSet<string>();
            for (var index = 0; index < nodes.Count; index++)
            {
                if (!IsExplicitNavigation(nodes[index].Mode) || string.IsNullOrEmpty(nodes[index].ComponentInstanceId))
                {
                    continue;
                }
                VisitNavigationCycles(nodes[index].ComponentInstanceId, nodes[index].ComponentInstanceId, nodesByComponentId, new List<string>(), reported, findings, ref totalFindings, severityCounts, codeCounts, 0);
            }
        }

        static void VisitNavigationCycles(string startId, string currentId, Dictionary<string, NavigationNodeSnapshot> nodesByComponentId, List<string> path, HashSet<string> reported, List<NavigationFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts, int depth)
        {
            if (depth > 12 || path.Contains(currentId))
            {
                return;
            }

            NavigationNodeSnapshot node;
            if (!nodesByComponentId.TryGetValue(currentId, out node) || !IsExplicitNavigation(node.Mode))
            {
                return;
            }

            path.Add(currentId);
            for (var index = 0; index < node.Edges.Count; index++)
            {
                var targetId = node.Edges[index].TargetObjectId;
                if (string.IsNullOrEmpty(targetId) || !nodesByComponentId.ContainsKey(targetId))
                {
                    continue;
                }
                if (targetId == startId && path.Count > 1)
                {
                    var key = NavigationCycleKey(path);
                    if (reported.Add(key))
                    {
                        AddNavigationFinding(findings, "info", "navigation_cycle", node.NodeId, node.Path, "Explicit navigation graph contains a cycle involving " + path.Count + " Selectable nodes.", ref totalFindings, severityCounts, codeCounts);
                    }
                    continue;
                }
                VisitNavigationCycles(startId, targetId, nodesByComponentId, path, reported, findings, ref totalFindings, severityCounts, codeCounts, depth + 1);
            }
            path.RemoveAt(path.Count - 1);
        }

        static string NavigationCycleKey(List<string> path)
        {
            var copy = new List<string>(path);
            copy.Sort(StringComparer.Ordinal);
            return string.Join("|", copy.ToArray());
        }

        static bool IsExplicitNavigation(string mode)
        {
            return string.Equals(mode, "Explicit", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsAutomaticNavigationMode(string mode)
        {
            return string.Equals(mode, "Automatic", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Horizontal", StringComparison.OrdinalIgnoreCase)
                || string.Equals(mode, "Vertical", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsViableSelectable(NavigationNodeSnapshot node)
        {
            return node.ActiveInHierarchy && node.ComponentEnabled && node.Interactable;
        }

        static void AddNavigationFinding(List<NavigationFinding> findings, string severity, string code, string nodeId, string path, string message, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            AddNavigationFinding(findings, new NavigationFinding(severity, code, nodeId, path, message), ref totalFindings, severityCounts, codeCounts);
        }

        static void AddNavigationFinding(List<NavigationFinding> findings, NavigationFinding finding, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            totalFindings++;
            Increment(severityCounts, finding.Severity);
            Increment(codeCounts, finding.Code);
            if (findings.Count < MaxNavigationFindings + 1)
            {
                findings.Add(finding);
                return;
            }

            var lowestIndex = FindLowestNavigationFindingIndex(findings);
            if (lowestIndex >= 0 && SeverityRank(finding.Severity) > SeverityRank(findings[lowestIndex].Severity))
            {
                findings[lowestIndex] = finding;
            }
        }

        static int FindLowestNavigationFindingIndex(List<NavigationFinding> findings)
        {
            var indexToReplace = -1;
            var lowestRank = int.MaxValue;
            for (var index = 0; index < findings.Count; index++)
            {
                var rank = SeverityRank(findings[index].Severity);
                if (rank < lowestRank)
                {
                    lowestRank = rank;
                    indexToReplace = index;
                }
            }
            return indexToReplace;
        }

        static void SortNavigationFindingsBySeverity(List<NavigationFinding> findings)
        {
            findings.Sort((left, right) =>
            {
                var rank = SeverityRank(right.Severity).CompareTo(SeverityRank(left.Severity));
                return rank != 0 ? rank : string.CompareOrdinal(left.NodeId + left.Code, right.NodeId + right.Code);
            });
        }

        static string NavigationSelectablesJson(List<NavigationNodeSnapshot> nodes, int maxItems)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < nodes.Count && index < maxItems; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"node_id\":").Append(JsonUtil.String(nodes[index].NodeId));
                builder.Append(",\"path\":").Append(JsonUtil.String(nodes[index].Path));
                builder.Append(",\"target\":").Append(JsonUtil.String(nodes[index].Target));
                builder.Append(",\"component_type\":").Append(JsonUtil.String(nodes[index].ComponentType));
                builder.Append(",\"component_name\":").Append(JsonUtil.String(nodes[index].ComponentName));
                builder.Append(",\"active_self\":").Append(JsonUtil.Bool(nodes[index].ActiveSelf));
                builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(nodes[index].ActiveInHierarchy));
                builder.Append(",\"component_enabled\":").Append(JsonUtil.Bool(nodes[index].ComponentEnabled));
                builder.Append(",\"interactable\":").Append(JsonUtil.Bool(nodes[index].Interactable));
                builder.Append(",\"navigation_mode\":").Append(JsonUtil.String(nodes[index].Mode));
                builder.Append(",\"wrap_around\":").Append(JsonUtil.Bool(nodes[index].WrapAround));
                builder.Append(",\"serialized_target_count\":").Append(nodes[index].Edges.Count);
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string NavigationEdgesJson(List<NavigationNodeSnapshot> nodes, int maxItems)
        {
            var builder = new StringBuilder();
            var count = 0;
            for (var nodeIndex = 0; nodeIndex < nodes.Count && count < maxItems; nodeIndex++)
            {
                for (var edgeIndex = 0; edgeIndex < nodes[nodeIndex].Edges.Count && count < maxItems; edgeIndex++)
                {
                    var edge = nodes[nodeIndex].Edges[edgeIndex];
                    if (count > 0)
                    {
                        builder.Append(',');
                    }
                    builder.Append("{\"from_node_id\":").Append(JsonUtil.String(edge.FromNodeId));
                    builder.Append(",\"from_path\":").Append(JsonUtil.String(edge.FromPath));
                    builder.Append(",\"direction\":").Append(JsonUtil.String(edge.Direction));
                    builder.Append(",\"active_in_mode\":").Append(JsonUtil.Bool(edge.ActiveInMode));
                    builder.Append(",\"target_name\":").Append(JsonUtil.String(edge.TargetName));
                    builder.Append(",\"target_type\":").Append(JsonUtil.String(edge.TargetType));
                    builder.Append(",\"target_missing_in_scan\":").Append(JsonUtil.Bool(edge.TargetMissingInScan));
                    if (!string.IsNullOrEmpty(edge.ToNodeId))
                    {
                        builder.Append(",\"to_node_id\":").Append(JsonUtil.String(edge.ToNodeId));
                        builder.Append(",\"to_path\":").Append(JsonUtil.String(edge.ToPath));
                    }
                    builder.Append('}');
                    count++;
                }
            }
            return "[" + builder + "]";
        }

        static string EventSystemsJson(ScanSnapshot snapshot)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < snapshot.EventSystems.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"node_id\":").Append(JsonUtil.String(snapshot.EventSystems[index].NodeId));
                builder.Append(",\"path\":").Append(JsonUtil.String(snapshot.EventSystems[index].Path));
                builder.Append(",\"component_type\":").Append(JsonUtil.String(snapshot.EventSystems[index].ComponentType));
                builder.Append(",\"first_selected_name\":").Append(JsonUtil.String(snapshot.EventSystems[index].FirstSelectedName));
                builder.Append(",\"first_selected_type\":").Append(JsonUtil.String(snapshot.EventSystems[index].FirstSelectedType));
                builder.Append(",\"first_selected_path\":").Append(JsonUtil.String(PathForObject(snapshot, snapshot.EventSystems[index].FirstSelectedObjectId)));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string NavigationFindingsJson(List<NavigationFinding> findings)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < findings.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"severity\":").Append(JsonUtil.String(findings[index].Severity));
                builder.Append(",\"code\":").Append(JsonUtil.String(findings[index].Code));
                if (!string.IsNullOrEmpty(findings[index].NodeId))
                {
                    builder.Append(",\"node_id\":").Append(JsonUtil.String(findings[index].NodeId));
                }
                if (!string.IsNullOrEmpty(findings[index].Path))
                {
                    builder.Append(",\"path\":").Append(JsonUtil.String(findings[index].Path));
                }
                builder.Append(",\"message\":").Append(JsonUtil.String(findings[index].Message));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string NavigationWarningsJson(bool scanTruncated, bool selectablesTruncated, bool edgesTruncated, bool findingsTruncated)
        {
            var warnings = new List<string>();
            if (scanTruncated)
            {
                warnings.Add("navigation analysis covers only scanned nodes; omitted hierarchy may contain additional Selectables or targets");
            }
            if (selectablesTruncated)
            {
                warnings.Add("inline selectables are capped by max_selectables; use the emitted resource for the same bounded result shape");
            }
            if (edgesTruncated)
            {
                warnings.Add("inline navigation edges are capped by max_edges; rerun with a higher max_edges within schema limits");
            }
            if (findingsTruncated)
            {
                warnings.Add("inline navigation findings are capped and prioritized by severity; use findings_by_severity and findings_by_code for aggregate counts");
            }
            return StringArrayJson(warnings);
        }

        static string RaycastAnalysisJson(ScanSnapshot snapshot, bool includeOverlaps, int maxNodes)
        {
            var graphics = new List<RaycastGraphicSnapshot>(snapshot.RaycastGraphics);
            graphics.Sort((left, right) => string.CompareOrdinal(left.Path + left.ComponentName, right.Path + right.ComponentName));
            var analyzedGraphics = Math.Min(maxNodes, graphics.Count);
            var findings = new List<RaycastFinding>();
            var severityCounts = new Dictionary<string, int>();
            var codeCounts = new Dictionary<string, int>();
            var totalFindings = 0;
            var raycastTargets = 0;
            var activeRaycastTargets = 0;
            var invisibleRaycastTargets = 0;
            var nonBlockingCanvasGroups = 0;
            var blockingNonInteractableGroups = 0;
            var invisibleBlockingGroups = 0;

            for (var index = 0; index < snapshot.CanvasGroups.Count; index++)
            {
                var group = snapshot.CanvasGroups[index];
                if (!group.BlocksRaycasts)
                {
                    nonBlockingCanvasGroups++;
                    AddRaycastFinding(findings, "info", "canvas_group_not_blocking_raycasts", group.NodeId, group.Path, "CanvasGroup has blocksRaycasts disabled; child Graphics will not block pointer input through this group.", ref totalFindings, severityCounts, codeCounts);
                }
                if (group.BlocksRaycasts && !group.Interactable)
                {
                    blockingNonInteractableGroups++;
                    AddRaycastFinding(findings, "warning", "canvas_group_blocks_not_interactable", group.NodeId, group.Path, "CanvasGroup blocks raycasts while interactable is false; it may block clicks without allowing interaction.", ref totalFindings, severityCounts, codeCounts);
                }
                if (group.BlocksRaycasts && group.Alpha <= 0.01f)
                {
                    invisibleBlockingGroups++;
                    AddRaycastFinding(findings, "warning", "invisible_canvas_group_blocks_raycasts", group.NodeId, group.Path, "CanvasGroup is effectively invisible but still blocks raycasts.", ref totalFindings, severityCounts, codeCounts);
                }
            }

            for (var index = 0; index < graphics.Count; index++)
            {
                var graphic = graphics[index];
                if (!graphic.RaycastTarget)
                {
                    continue;
                }

                raycastTargets++;
                if (IsActiveRaycastGraphic(graphic))
                {
                    activeRaycastTargets++;
                }
                if (IsInvisibleRaycastGraphic(graphic))
                {
                    invisibleRaycastTargets++;
                }
            }

            for (var index = 0; index < analyzedGraphics; index++)
            {
                AnalyzeRaycastGraphic(graphics[index], snapshot, findings, ref totalFindings, severityCounts, codeCounts);
            }

            var overlapChecks = 0;
            var overlapsFound = 0;
            var overlapChecksTruncated = false;
            if (includeOverlaps)
            {
                AnalyzeRaycastOverlaps(graphics, analyzedGraphics, findings, ref totalFindings, severityCounts, codeCounts, ref overlapChecks, ref overlapsFound, ref overlapChecksTruncated);
            }

            if (activeRaycastTargets > 0 && snapshot.EventSystems.Count == 0)
            {
                AddRaycastFinding(findings, "warning", "event_system_not_in_scan", string.Empty, string.Empty, "Active raycast target Graphics were found, but no EventSystem component was present in the cached scan.", ref totalFindings, severityCounts, codeCounts);
            }
            if (activeRaycastTargets > 0 && snapshot.GraphicRaycasters.Count == 0)
            {
                AddRaycastFinding(findings, "warning", "graphic_raycast_targets_without_raycaster", string.Empty, string.Empty, "Active raycast target Graphics were found, but no GraphicRaycaster component was present in the cached scan.", ref totalFindings, severityCounts, codeCounts);
            }
            if (snapshot.GraphicRaycasters.Count > 0 && snapshot.EventSystems.Count == 0)
            {
                AddRaycastFinding(findings, "warning", "graphic_raycaster_without_event_system", string.Empty, string.Empty, "GraphicRaycaster is present but no EventSystem was found in the cached scan.", ref totalFindings, severityCounts, codeCounts);
            }

            SortRaycastFindingsBySeverity(findings);
            var findingsTruncated = findings.Count > MaxRaycastFindings;
            if (findingsTruncated)
            {
                findings.RemoveRange(MaxRaycastFindings, findings.Count - MaxRaycastFindings);
            }

            var raycastId = "ugui_raycast_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var raycastResourceUri = RaycastResourceUri(raycastId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"raycast_id\":" + JsonUtil.String(raycastId)
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"read_only\":true"
                + ",\"cache\":{\"storage\":\"memory\",\"max_scans\":" + MaxCachedScans + "}"
                + ",\"scan_truncated\":" + JsonUtil.Bool(snapshot.Truncated)
                + ",\"graphics_found\":" + graphics.Count
                + ",\"graphics_analyzed\":" + analyzedGraphics
                + ",\"max_nodes\":" + maxNodes
                + ",\"analysis_truncated\":" + JsonUtil.Bool(graphics.Count > analyzedGraphics)
                + ",\"raycast_targets\":" + raycastTargets
                + ",\"active_raycast_targets\":" + activeRaycastTargets
                + ",\"invisible_raycast_targets\":" + invisibleRaycastTargets
                + ",\"canvas_groups_found\":" + snapshot.CanvasGroups.Count
                + ",\"non_blocking_canvas_groups\":" + nonBlockingCanvasGroups
                + ",\"blocking_non_interactable_canvas_groups\":" + blockingNonInteractableGroups
                + ",\"invisible_blocking_canvas_groups\":" + invisibleBlockingGroups
                + ",\"graphic_raycasters_found\":" + snapshot.GraphicRaycasters.Count
                + ",\"event_systems_found\":" + snapshot.EventSystems.Count
                + ",\"include_overlaps\":" + JsonUtil.Bool(includeOverlaps)
                + ",\"overlap_checks\":" + overlapChecks
                + ",\"overlap_checks_truncated\":" + JsonUtil.Bool(overlapChecksTruncated)
                + ",\"overlaps_found\":" + overlapsFound
                + ",\"findings_truncated\":" + JsonUtil.Bool(findingsTruncated)
                + ",\"findings_count\":" + totalFindings
                + ",\"returned_findings_count\":" + findings.Count
                + ",\"findings_by_severity\":" + CountsJson(severityCounts)
                + ",\"findings_by_code\":" + CountsJson(codeCounts)
                + ",\"graphics\":" + RaycastGraphicsJson(graphics, analyzedGraphics)
                + ",\"canvas_groups\":" + CanvasGroupsJson(snapshot.CanvasGroups)
                + ",\"graphic_raycasters\":" + GraphicRaycastersJson(snapshot.GraphicRaycasters)
                + ",\"findings\":" + RaycastFindingsJson(findings)
                + ",\"summary\":" + JsonUtil.String(graphics.Count + " Graphic raycast component(s) analyzed")
                + ",\"resource_refs\":" + ResourceRefsJson(raycastResourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + RaycastWarningsJson(snapshot.Truncated, graphics.Count > analyzedGraphics, includeOverlaps && overlapChecksTruncated, findingsTruncated)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"ugui.canvas.inspect_node\",\"ugui.canvas.report\",\"ugui.layout.analyze\",\"ugui.navigation.analyze\"]"
                + ",\"risk\":\"low\""
                + ",\"requires_approval\":false}";
            StoreResource(raycastResourceUri, resultJson, "application/json");
            return resultJson;
        }

        static void AnalyzeRaycastGraphic(RaycastGraphicSnapshot graphic, ScanSnapshot snapshot, List<RaycastFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            if (!graphic.RaycastTarget)
            {
                return;
            }
            if (!graphic.ActiveInHierarchy || !graphic.ComponentEnabled)
            {
                AddRaycastFinding(findings, "info", "inactive_raycast_target", graphic.NodeId, graphic.Path, "Graphic has raycastTarget enabled but is inactive or disabled in the cached scan.", ref totalFindings, severityCounts, codeCounts);
                return;
            }
            if (IsInvisibleRaycastGraphic(graphic))
            {
                AddRaycastFinding(findings, "warning", "invisible_graphic_blocks_raycast", graphic.NodeId, graphic.Path, "Graphic is effectively invisible or has zero bounds but still has raycastTarget enabled.", ref totalFindings, severityCounts, codeCounts);
            }
            else
            {
                AddRaycastFinding(findings, "info", "graphic_raycast_target_enabled", graphic.NodeId, graphic.Path, "Graphic has raycastTarget enabled and can participate in pointer blocking.", ref totalFindings, severityCounts, codeCounts);
            }

            for (var index = 0; index < snapshot.CanvasGroups.Count; index++)
            {
                var group = snapshot.CanvasGroups[index];
                if (!CanvasGroupAppliesToGraphic(group, graphic))
                {
                    continue;
                }
                if (group.BlocksRaycasts && !group.Interactable)
                {
                    AddRaycastFinding(findings, "warning", "raycast_target_under_non_interactable_group", graphic.NodeId, graphic.Path, "Raycast target Graphic is under a CanvasGroup that blocks raycasts but is not interactable.", ref totalFindings, severityCounts, codeCounts);
                    return;
                }
                if (group.BlocksRaycasts && group.Alpha <= 0.01f)
                {
                    AddRaycastFinding(findings, "warning", "raycast_target_under_invisible_group", graphic.NodeId, graphic.Path, "Raycast target Graphic is under an invisible CanvasGroup that still blocks raycasts.", ref totalFindings, severityCounts, codeCounts);
                    return;
                }
            }
        }

        static void AnalyzeRaycastOverlaps(List<RaycastGraphicSnapshot> graphics, int analyzedGraphics, List<RaycastFinding> findings, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts, ref int overlapChecks, ref int overlapsFound, ref bool overlapChecksTruncated)
        {
            for (var leftIndex = 0; leftIndex < analyzedGraphics; leftIndex++)
            {
                var left = graphics[leftIndex];
                if (!IsActiveRaycastGraphic(left) || IsInvisibleRaycastGraphic(left))
                {
                    continue;
                }
                for (var rightIndex = leftIndex + 1; rightIndex < analyzedGraphics; rightIndex++)
                {
                    if (overlapChecks >= MaxRaycastOverlapChecks)
                    {
                        overlapChecksTruncated = true;
                        return;
                    }
                    var right = graphics[rightIndex];
                    if (!IsActiveRaycastGraphic(right) || IsInvisibleRaycastGraphic(right))
                    {
                        continue;
                    }
                    overlapChecks++;
                    if (!string.Equals(left.Target, right.Target, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (!RectsOverlap(left.WorldMin, left.WorldMax, right.WorldMin, right.WorldMax))
                    {
                        continue;
                    }
                    overlapsFound++;
                    AddRaycastFinding(findings, "info", "overlapping_raycast_targets", HigherDrawOrder(left, right).NodeId, HigherDrawOrder(left, right).Path, "Two active raycast target Graphics overlap in the cached world bounds; verify sibling order and click blocker intent.", ref totalFindings, severityCounts, codeCounts);
                }
            }
        }

        static bool IsActiveRaycastGraphic(RaycastGraphicSnapshot graphic)
        {
            return graphic.RaycastTarget && graphic.ActiveInHierarchy && graphic.ComponentEnabled;
        }

        static bool IsInvisibleRaycastGraphic(RaycastGraphicSnapshot graphic)
        {
            return graphic.ColorAlpha <= 0.01f || Math.Abs(graphic.RectSize.x) <= 0.5f || Math.Abs(graphic.RectSize.y) <= 0.5f;
        }

        static bool CanvasGroupAppliesToGraphic(CanvasGroupSnapshot group, RaycastGraphicSnapshot graphic)
        {
            if (!group.ActiveInHierarchy || !group.ComponentEnabled || group.IgnoreParentGroups)
            {
                return false;
            }
            return string.Equals(group.Path, graphic.Path, StringComparison.Ordinal) || graphic.Path.StartsWith(group.Path + "/", StringComparison.Ordinal);
        }

        static bool RectsOverlap(Vector2 leftMin, Vector2 leftMax, Vector2 rightMin, Vector2 rightMax)
        {
            return leftMin.x < rightMax.x && leftMax.x > rightMin.x && leftMin.y < rightMax.y && leftMax.y > rightMin.y;
        }

        static RaycastGraphicSnapshot HigherDrawOrder(RaycastGraphicSnapshot left, RaycastGraphicSnapshot right)
        {
            if (left.Depth != right.Depth)
            {
                return left.Depth > right.Depth ? left : right;
            }
            return left.SiblingIndex >= right.SiblingIndex ? left : right;
        }

        static void AddRaycastFinding(List<RaycastFinding> findings, string severity, string code, string nodeId, string path, string message, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            AddRaycastFinding(findings, new RaycastFinding(severity, code, nodeId, path, message), ref totalFindings, severityCounts, codeCounts);
        }

        static void AddRaycastFinding(List<RaycastFinding> findings, RaycastFinding finding, ref int totalFindings, Dictionary<string, int> severityCounts, Dictionary<string, int> codeCounts)
        {
            totalFindings++;
            Increment(severityCounts, finding.Severity);
            Increment(codeCounts, finding.Code);
            if (findings.Count < MaxRaycastFindings + 1)
            {
                findings.Add(finding);
                return;
            }

            var lowestIndex = FindLowestRaycastFindingIndex(findings);
            if (lowestIndex >= 0 && SeverityRank(finding.Severity) > SeverityRank(findings[lowestIndex].Severity))
            {
                findings[lowestIndex] = finding;
            }
        }

        static int FindLowestRaycastFindingIndex(List<RaycastFinding> findings)
        {
            var indexToReplace = -1;
            var lowestRank = int.MaxValue;
            for (var index = 0; index < findings.Count; index++)
            {
                var rank = SeverityRank(findings[index].Severity);
                if (rank < lowestRank)
                {
                    lowestRank = rank;
                    indexToReplace = index;
                }
            }
            return indexToReplace;
        }

        static void SortRaycastFindingsBySeverity(List<RaycastFinding> findings)
        {
            findings.Sort((left, right) =>
            {
                var rank = SeverityRank(right.Severity).CompareTo(SeverityRank(left.Severity));
                return rank != 0 ? rank : string.CompareOrdinal(left.NodeId + left.Code, right.NodeId + right.Code);
            });
        }

        static string RaycastGraphicsJson(List<RaycastGraphicSnapshot> graphics, int maxItems)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < graphics.Count && index < maxItems; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"node_id\":").Append(JsonUtil.String(graphics[index].NodeId));
                builder.Append(",\"path\":").Append(JsonUtil.String(graphics[index].Path));
                builder.Append(",\"component_type\":").Append(JsonUtil.String(graphics[index].ComponentType));
                builder.Append(",\"component_name\":").Append(JsonUtil.String(graphics[index].ComponentName));
                builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(graphics[index].ActiveInHierarchy));
                builder.Append(",\"component_enabled\":").Append(JsonUtil.Bool(graphics[index].ComponentEnabled));
                builder.Append(",\"raycast_target\":").Append(JsonUtil.Bool(graphics[index].RaycastTarget));
                builder.Append(",\"color_alpha\":").Append(Number(graphics[index].ColorAlpha));
                builder.Append(",\"rect_size\":").Append(Vector2Json(graphics[index].RectSize));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string CanvasGroupsJson(List<CanvasGroupSnapshot> groups)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < groups.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"node_id\":").Append(JsonUtil.String(groups[index].NodeId));
                builder.Append(",\"path\":").Append(JsonUtil.String(groups[index].Path));
                builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(groups[index].ActiveInHierarchy));
                builder.Append(",\"component_enabled\":").Append(JsonUtil.Bool(groups[index].ComponentEnabled));
                builder.Append(",\"alpha\":").Append(Number(groups[index].Alpha));
                builder.Append(",\"blocks_raycasts\":").Append(JsonUtil.Bool(groups[index].BlocksRaycasts));
                builder.Append(",\"interactable\":").Append(JsonUtil.Bool(groups[index].Interactable));
                builder.Append(",\"ignore_parent_groups\":").Append(JsonUtil.Bool(groups[index].IgnoreParentGroups));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string GraphicRaycastersJson(List<GraphicRaycasterSnapshot> raycasters)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < raycasters.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"node_id\":").Append(JsonUtil.String(raycasters[index].NodeId));
                builder.Append(",\"path\":").Append(JsonUtil.String(raycasters[index].Path));
                builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(raycasters[index].ActiveInHierarchy));
                builder.Append(",\"component_enabled\":").Append(JsonUtil.Bool(raycasters[index].ComponentEnabled));
                builder.Append(",\"ignore_reversed_graphics\":").Append(JsonUtil.Bool(raycasters[index].IgnoreReversedGraphics));
                builder.Append(",\"blocking_objects\":").Append(JsonUtil.String(raycasters[index].BlockingObjects));
                builder.Append(",\"blocking_mask\":").Append(JsonUtil.String(raycasters[index].BlockingMask));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string RaycastFindingsJson(List<RaycastFinding> findings)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < findings.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"severity\":").Append(JsonUtil.String(findings[index].Severity));
                builder.Append(",\"code\":").Append(JsonUtil.String(findings[index].Code));
                if (!string.IsNullOrEmpty(findings[index].NodeId))
                {
                    builder.Append(",\"node_id\":").Append(JsonUtil.String(findings[index].NodeId));
                }
                if (!string.IsNullOrEmpty(findings[index].Path))
                {
                    builder.Append(",\"path\":").Append(JsonUtil.String(findings[index].Path));
                }
                builder.Append(",\"message\":").Append(JsonUtil.String(findings[index].Message));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string RaycastWarningsJson(bool scanTruncated, bool analysisTruncated, bool overlapChecksTruncated, bool findingsTruncated)
        {
            var warnings = new List<string>();
            if (scanTruncated)
            {
                warnings.Add("raycast analysis covers only scanned nodes; omitted hierarchy may contain additional raycast blockers");
            }
            if (analysisTruncated)
            {
                warnings.Add("inline raycast graphics are capped by max_nodes; rerun with a higher max_nodes or narrower target for fuller coverage");
            }
            if (overlapChecksTruncated)
            {
                warnings.Add("overlap checks reached the bounded cap; rerun with a narrower scan or include_overlaps=false for faster aggregate analysis");
            }
            if (findingsTruncated)
            {
                warnings.Add("inline raycast findings are capped and prioritized by severity; use findings_by_severity and findings_by_code for aggregate counts");
            }
            return StringArrayJson(warnings);
        }

        static string ReferencesReportJson(ScanSnapshot snapshot, bool includeSprites, bool includeTextures, bool includeFonts, bool includeMaterials, bool includeScripts, int maxReferences)
        {
            var returned = new List<AssetReferenceSnapshot>();
            var countsByType = new Dictionary<string, int>();
            var countsByAsset = new Dictionary<string, int>();
            var assetSummaries = new Dictionary<string, AssetReferenceSummary>();
            var referencesFound = 0;

            for (var index = 0; index < snapshot.AssetReferences.Count; index++)
            {
                var item = snapshot.AssetReferences[index];
                if (!ReferenceIncluded(item.ReferenceType, includeSprites, includeTextures, includeFonts, includeMaterials, includeScripts))
                {
                    continue;
                }

                referencesFound++;
                Increment(countsByType, item.ReferenceType);
                Increment(countsByAsset, item.AssetPath);
                var key = item.ReferenceType + "|" + item.AssetPath;
                AssetReferenceSummary summary;
                if (!assetSummaries.TryGetValue(key, out summary))
                {
                    summary = new AssetReferenceSummary
                    {
                        ReferenceType = item.ReferenceType,
                        AssetPath = item.AssetPath,
                        AssetGuid = item.AssetGuid,
                        Name = item.Name,
                        ObjectType = item.ObjectType,
                    };
                    assetSummaries[key] = summary;
                }
                summary.Count++;

                if (returned.Count < maxReferences)
                {
                    returned.Add(item);
                }
            }

            var referencesTruncated = referencesFound > returned.Count;
            var referencesId = "ugui_references_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            var referencesResourceUri = ReferencesResourceUri(referencesId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"references_id\":" + JsonUtil.String(referencesId)
                + ",\"scan_id\":" + JsonUtil.String(snapshot.ScanId)
                + ",\"read_only\":true"
                + ",\"cache\":{\"storage\":\"memory\",\"max_scans\":" + MaxCachedScans + "}"
                + ",\"scan_truncated\":" + JsonUtil.Bool(snapshot.Truncated)
                + ",\"reference_scan_truncated\":" + JsonUtil.Bool(snapshot.AssetReferencesTruncated)
                + ",\"include_sprites\":" + JsonUtil.Bool(includeSprites)
                + ",\"include_textures\":" + JsonUtil.Bool(includeTextures)
                + ",\"include_fonts\":" + JsonUtil.Bool(includeFonts)
                + ",\"include_materials\":" + JsonUtil.Bool(includeMaterials)
                + ",\"include_scripts\":" + JsonUtil.Bool(includeScripts)
                + ",\"cached_references_count\":" + snapshot.AssetReferences.Count
                + ",\"references_found\":" + referencesFound
                + ",\"returned_references_count\":" + returned.Count
                + ",\"max_references\":" + maxReferences
                + ",\"references_truncated\":" + JsonUtil.Bool(referencesTruncated)
                + ",\"distinct_assets_count\":" + assetSummaries.Count
                + ",\"counts_by_type\":" + CountsJson(countsByType)
                + ",\"counts_by_asset\":" + CountsJson(countsByAsset)
                + ",\"assets\":" + AssetReferenceSummariesJson(assetSummaries)
                + ",\"references\":" + AssetReferencesJson(returned)
                + ",\"summary\":" + JsonUtil.String(referencesFound + " uGUI asset reference(s) matched the request")
                + ",\"resource_refs\":" + ResourceRefsJson(referencesResourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + ReferencesWarningsJson(snapshot.Truncated, snapshot.AssetReferencesTruncated, referencesTruncated)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"ugui.canvas.inspect_node\",\"ugui.canvas.report\",\"ugui.events.extract\",\"ugui.layout.analyze\",\"ugui.navigation.analyze\",\"ugui.raycast.analyze\"]"
                + ",\"risk\":\"low\""
                + ",\"requires_approval\":false}";
            StoreResource(referencesResourceUri, resultJson, "application/json");
            return resultJson;
        }

        static bool ReferenceIncluded(string referenceType, bool includeSprites, bool includeTextures, bool includeFonts, bool includeMaterials, bool includeScripts)
        {
            return (includeSprites && referenceType == "sprite")
                || (includeTextures && referenceType == "texture")
                || (includeFonts && referenceType == "font")
                || (includeMaterials && referenceType == "material")
                || (includeScripts && referenceType == "script");
        }

        static string AssetReferencesJson(List<AssetReferenceSnapshot> references)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < references.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"node_id\":").Append(JsonUtil.String(references[index].NodeId));
                builder.Append(",\"path\":").Append(JsonUtil.String(references[index].Path));
                builder.Append(",\"target\":").Append(JsonUtil.String(references[index].Target));
                builder.Append(",\"component_type\":").Append(JsonUtil.String(references[index].ComponentType));
                builder.Append(",\"component_name\":").Append(JsonUtil.String(references[index].ComponentName));
                builder.Append(",\"property_path\":").Append(JsonUtil.String(references[index].PropertyPath));
                builder.Append(",\"reference_type\":").Append(JsonUtil.String(references[index].ReferenceType));
                builder.Append(",\"asset_path\":").Append(JsonUtil.String(references[index].AssetPath));
                builder.Append(",\"asset_guid\":").Append(JsonUtil.String(references[index].AssetGuid));
                builder.Append(",\"name\":").Append(JsonUtil.String(references[index].Name));
                builder.Append(",\"object_type\":").Append(JsonUtil.String(references[index].ObjectType));
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string AssetReferenceSummariesJson(Dictionary<string, AssetReferenceSummary> summaries)
        {
            var items = new List<AssetReferenceSummary>(summaries.Values);
            items.Sort((left, right) =>
            {
                var count = right.Count.CompareTo(left.Count);
                return count != 0 ? count : string.CompareOrdinal(left.AssetPath, right.AssetPath);
            });

            var builder = new StringBuilder();
            for (var index = 0; index < items.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"reference_type\":").Append(JsonUtil.String(items[index].ReferenceType));
                builder.Append(",\"asset_path\":").Append(JsonUtil.String(items[index].AssetPath));
                builder.Append(",\"asset_guid\":").Append(JsonUtil.String(items[index].AssetGuid));
                builder.Append(",\"name\":").Append(JsonUtil.String(items[index].Name));
                builder.Append(",\"object_type\":").Append(JsonUtil.String(items[index].ObjectType));
                builder.Append(",\"reference_count\":").Append(items[index].Count);
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string ReferencesWarningsJson(bool scanTruncated, bool referenceScanTruncated, bool referencesTruncated)
        {
            var warnings = new List<string>();
            if (scanTruncated)
            {
                warnings.Add("reference report covers only scanned nodes; omitted hierarchy may contain additional uGUI references");
            }
            if (referenceScanTruncated)
            {
                warnings.Add("scan-time uGUI asset references reached the cached cap; rerun with a narrower scan for full reference coverage");
            }
            if (referencesTruncated)
            {
                warnings.Add("inline references are capped by max_references; use counts_by_type, counts_by_asset and assets for aggregate coverage");
            }
            return StringArrayJson(warnings);
        }

        static string ScreenSizesJson(List<Vector2> values)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(Vector2Json(values[index]));
            }
            return "[" + builder + "]";
        }

        static string TargetSummariesJson(List<TargetSnapshot> targets)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < targets.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"target\":").Append(JsonUtil.String(targets[index].Target));
                if (!string.IsNullOrEmpty(targets[index].Kind))
                {
                    builder.Append(",\"kind\":").Append(JsonUtil.String(targets[index].Kind));
                }
                builder.Append(",\"status\":").Append(JsonUtil.String(targets[index].Status));
                if (!string.IsNullOrEmpty(targets[index].Reason))
                {
                    builder.Append(",\"reason\":").Append(JsonUtil.String(targets[index].Reason));
                }
                builder.Append('}');
            }
            return "[" + builder + "]";
        }

        static string CountsJson(Dictionary<string, int> counts)
        {
            var builder = new StringBuilder();
            var first = true;
            foreach (var count in counts)
            {
                if (!first)
                {
                    builder.Append(',');
                }
                first = false;
                builder.Append(JsonUtil.String(count.Key)).Append(':').Append(count.Value);
            }
            return "{" + builder + "}";
        }

        static string OverrideSummaryJson(List<PrefabLayoutOverride> overrides)
        {
            var applied = 0;
            var ignored = 0;
            var invalid = 0;
            for (var index = 0; index < overrides.Count; index++)
            {
                if (overrides[index].Status == "applied")
                {
                    applied++;
                }
                else if (overrides[index].Status == "invalid")
                {
                    invalid++;
                }
                else
                {
                    ignored++;
                }
            }

            return "{\"total\":" + overrides.Count
                + ",\"applied\":" + applied
                + ",\"ignored\":" + ignored
                + ",\"invalid\":" + invalid
                + ",\"items\":" + PrefabLayoutOverridesJson(overrides) + "}";
        }

        static string StringArrayJson(List<string> values)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(JsonUtil.String(values[index]));
            }
            return "[" + builder + "]";
        }

        static string StringArrayJson(string[] values)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < values.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(JsonUtil.String(values[index]));
            }
            return "[" + builder + "]";
        }

        static string ResourceRefsJson(string uri)
        {
            return StringArrayJson(new List<string> { uri });
        }

        static string ScanResourceUri(string scanId)
        {
            return "unity-ui://ugui/scans/" + scanId + "/json";
        }

        static string ReportResourceUri(string scanId)
        {
            return "unity-ui://ugui/reports/" + scanId + "/json";
        }

        static string LayoutResourceUri(string analysisId)
        {
            return "unity-ui://ugui/layout/" + analysisId + "/json";
        }

        static string EventsResourceUri(string eventsId)
        {
            return "unity-ui://ugui/events/" + eventsId + "/json";
        }

        static string NavigationResourceUri(string navigationId)
        {
            return "unity-ui://ugui/navigation/" + navigationId + "/json";
        }

        static string RaycastResourceUri(string raycastId)
        {
            return "unity-ui://ugui/raycast/" + raycastId + "/json";
        }

        static string ReferencesResourceUri(string referencesId)
        {
            return "unity-ui://ugui/references/" + referencesId + "/json";
        }

        static string BoundsSnapshotResourceUri(string boundsId)
        {
            return "unity-ui://ugui/bounds/" + boundsId + "/json";
        }

        static string InteractionSmokeResourceUri(string smokeId)
        {
            return "unity-ui://ugui/interaction/" + smokeId + "/json";
        }

        static string ResourceWarningsJson(bool truncated)
        {
            var warnings = new List<string>();
            if (truncated)
            {
                warnings.Add("resource content was truncated by max_chars; rerun ugui.resource.read with a higher max_chars within schema limits");
            }
            return StringArrayJson(warnings);
        }

        static string Vector2Json(Vector2 value)
        {
            return "[" + Number(value.x) + "," + Number(value.y) + "]";
        }

        static string Vector3Json(Vector3 value)
        {
            return "[" + Number(value.x) + "," + Number(value.y) + "," + Number(value.z) + "]";
        }

        static string Number(float value)
        {
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                key = "unknown";
            }

            int count;
            counts.TryGetValue(key, out count);
            counts[key] = count + 1;
        }

        static void AddSourceTarget(ScanSnapshot snapshot, string target)
        {
            if (!string.IsNullOrEmpty(target) && !snapshot.SourceTargets.Contains(target))
            {
                snapshot.SourceTargets.Add(target);
            }
        }

        static void StoreScan(ScanSnapshot snapshot)
        {
            lock (ScanCacheLock)
            {
                if (!ScanCache.ContainsKey(snapshot.ScanId))
                {
                    ScanCacheOrder.Enqueue(snapshot.ScanId);
                }
                ScanCache[snapshot.ScanId] = snapshot;

                while (ScanCacheOrder.Count > MaxCachedScans)
                {
                    var evicted = ScanCacheOrder.Dequeue();
                    ScanCache.Remove(evicted);
                }
            }
        }

        static void StoreResource(string uri, string content, string mimeType)
        {
            if (!IsUguiResourceUri(uri))
            {
                return;
            }

            lock (ResourceCacheLock)
            {
                if (!ResourceCache.ContainsKey(uri))
                {
                    ResourceCacheOrder.Enqueue(uri);
                }
                ResourceCache[uri] = new UguiResourceSnapshot(uri, content, mimeType);

                while (ResourceCacheOrder.Count > MaxCachedResources)
                {
                    var evicted = ResourceCacheOrder.Dequeue();
                    ResourceCache.Remove(evicted);
                }
            }
        }

        static UguiResourceSnapshot FindResource(string uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return null;
            }

            lock (ResourceCacheLock)
            {
                UguiResourceSnapshot resource;
                return ResourceCache.TryGetValue(uri, out resource) ? resource : null;
            }
        }

        static bool IsUguiResourceUri(string uri)
        {
            return !string.IsNullOrEmpty(uri)
                && uri.StartsWith("unity-ui://ugui/", StringComparison.Ordinal)
                && uri.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        static ScanSnapshot FindScan(string scanId)
        {
            if (string.IsNullOrEmpty(scanId))
            {
                return null;
            }

            lock (ScanCacheLock)
            {
                ScanSnapshot snapshot;
                return ScanCache.TryGetValue(scanId, out snapshot) ? snapshot : null;
            }
        }

        static CommandExecutionResult MissingScan(string scanId)
        {
            return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"reason\":\"scan_id was not found in this Unity Bridge process in-memory cache\""
                + ",\"cache\":{\"storage\":\"memory\",\"max_scans\":" + MaxCachedScans + "}}");
        }

        static bool IsKnownUguiType(Type type)
        {
            var fullName = type.FullName ?? string.Empty;
            for (var index = 0; index < UguiComponentTypeNames.Length; index++)
            {
                if (fullName == UguiComponentTypeNames[index] || IsSubclassNamed(type, UguiComponentTypeNames[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsSubclassNamed(Type type, string fullName)
        {
            var current = type.BaseType;
            while (current != null)
            {
                if (current.FullName == fullName)
                {
                    return true;
                }
                current = current.BaseType;
            }

            return false;
        }

        static bool IsTypeNamed(Type type, string fullName)
        {
            return type != null && (type.FullName == fullName || IsSubclassNamed(type, fullName));
        }

        static bool IsSelectableComponent(Type type)
        {
            return type.FullName == "UnityEngine.UI.Selectable" || IsSubclassNamed(type, "UnityEngine.UI.Selectable");
        }

        static bool IsEventSystemComponent(Type type)
        {
            return type.FullName == "UnityEngine.EventSystems.EventSystem" || IsSubclassNamed(type, "UnityEngine.EventSystems.EventSystem");
        }

        static bool IsGraphicComponent(Type type)
        {
            return type.FullName == "UnityEngine.UI.Graphic" || IsSubclassNamed(type, "UnityEngine.UI.Graphic");
        }

        static bool IsCanvasGroupComponent(Type type)
        {
            return type.FullName == "UnityEngine.CanvasGroup" || IsSubclassNamed(type, "UnityEngine.CanvasGroup");
        }

        static bool IsGraphicRaycasterComponent(Type type)
        {
            return type.FullName == "UnityEngine.UI.GraphicRaycaster" || IsSubclassNamed(type, "UnityEngine.UI.GraphicRaycaster");
        }

        static object GetPropertyValue(object target, string propertyName)
        {
            if (target == null || string.IsNullOrEmpty(propertyName))
            {
                return null;
            }

            try
            {
                var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property == null || !property.CanRead || property.GetIndexParameters().Length != 0)
                {
                    return null;
                }
                return property.GetValue(target, null);
            }
            catch
            {
                return null;
            }
        }

        static bool GetBoolProperty(object target, string propertyName, bool defaultValue)
        {
            var value = GetPropertyValue(target, propertyName);
            return value is bool ? (bool)value : defaultValue;
        }

        static float GetFloatProperty(object target, string propertyName, float defaultValue)
        {
            var value = GetPropertyValue(target, propertyName);
            return value is float ? (float)value : defaultValue;
        }

        static string GetPropertyString(object target, string propertyName)
        {
            var value = GetPropertyValue(target, propertyName);
            return value == null ? string.Empty : value.ToString();
        }

        static Scene LoadedSceneByPath(string scenePath)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase))
                {
                    return scene;
                }
            }

            return new Scene();
        }

        static bool IsUnityRelativePath(string path)
        {
            return !string.IsNullOrEmpty(path)
                && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal))
                && path.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        static List<string> ExtractStringArray(string json, string property, int maxItems)
        {
            var values = new List<string>();
            var start = FindArrayStart(json, property);
            if (start < 0)
            {
                return values;
            }

            var inString = false;
            var escaped = false;
            var current = new StringBuilder();
            for (var index = start + 1; index < json.Length && values.Count < maxItems; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    if (inString)
                    {
                        current.Append(character);
                    }
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                {
                    if (inString)
                    {
                        values.Add(current.ToString());
                        current.Length = 0;
                    }
                    inString = !inString;
                    continue;
                }
                if (inString)
                {
                    current.Append(character);
                    continue;
                }
                if (character == ']')
                {
                    break;
                }
            }

            return values;
        }

        static List<Vector2> ExtractScreenSizes(string json, int maxItems)
        {
            var values = new List<Vector2>();
            var arrayStart = FindArrayStart(json, "screen_sizes");
            if (arrayStart < 0)
            {
                return values;
            }

            var arrayEnd = FindMatchingArrayEnd(json, arrayStart);
            if (arrayEnd < 0)
            {
                return values;
            }

            var index = arrayStart + 1;
            while (index < arrayEnd && values.Count < maxItems)
            {
                var itemStart = json.IndexOf('[', index);
                if (itemStart < 0 || itemStart >= arrayEnd)
                {
                    break;
                }
                var itemEnd = FindMatchingArrayEnd(json, itemStart);
                if (itemEnd < 0 || itemEnd > arrayEnd)
                {
                    break;
                }
                var numbers = ExtractFloatArray(json.Substring(itemStart, itemEnd - itemStart + 1), 2);
                if (numbers.Count == 2 && numbers[0] > 0 && numbers[1] > 0)
                {
                    values.Add(new Vector2(numbers[0], numbers[1]));
                }
                index = itemEnd + 1;
            }

            return values;
        }

        static List<float> ExtractFloatArray(string json, int maxItems)
        {
            var values = new List<float>();
            var token = new StringBuilder();
            for (var index = 0; index < json.Length && values.Count < maxItems; index++)
            {
                var character = json[index];
                if ((character >= '0' && character <= '9') || character == '-' || character == '+' || character == '.' || character == 'e' || character == 'E')
                {
                    token.Append(character);
                    continue;
                }

                FlushFloatToken(token, values, maxItems);
            }
            FlushFloatToken(token, values, maxItems);
            return values;
        }

        static void FlushFloatToken(StringBuilder token, List<float> values, int maxItems)
        {
            if (token.Length == 0 || values.Count >= maxItems)
            {
                token.Length = 0;
                return;
            }

            float value;
            if (float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                values.Add(value);
            }
            token.Length = 0;
        }

        static bool TryExtractVector2(string json, string property, out Vector2 value)
        {
            value = Vector2.zero;
            var arrayStart = FindArrayStart(json, property);
            if (arrayStart < 0)
            {
                return false;
            }
            var arrayEnd = FindMatchingArrayEnd(json, arrayStart);
            if (arrayEnd < 0)
            {
                return false;
            }
            var numbers = ExtractFloatArray(json.Substring(arrayStart, arrayEnd - arrayStart + 1), 2);
            if (numbers.Count != 2)
            {
                return false;
            }
            value = new Vector2(numbers[0], numbers[1]);
            return true;
        }

        static bool TryExtractVector3(string json, string property, out Vector3 value)
        {
            value = Vector3.one;
            var arrayStart = FindArrayStart(json, property);
            if (arrayStart < 0)
            {
                return false;
            }
            var arrayEnd = FindMatchingArrayEnd(json, arrayStart);
            if (arrayEnd < 0)
            {
                return false;
            }
            var numbers = ExtractFloatArray(json.Substring(arrayStart, arrayEnd - arrayStart + 1), 3);
            if (numbers.Count != 3)
            {
                return false;
            }
            value = new Vector3(numbers[0], numbers[1], numbers[2]);
            return true;
        }

        static List<PrefabLayoutOverride> ExtractPrefabLayoutOverrides(string json, int maxItems)
        {
            var result = new List<PrefabLayoutOverride>();
            var arrayStart = FindArrayStart(json, "prefab_layout_overrides");
            if (arrayStart < 0)
            {
                return result;
            }

            var index = arrayStart + 1;
            while (index < json.Length && result.Count < maxItems)
            {
                var objectStart = json.IndexOf('{', index);
                if (objectStart < 0)
                {
                    break;
                }
                var objectEnd = FindMatchingObjectEnd(json, objectStart);
                if (objectEnd < 0)
                {
                    break;
                }
                var objectJson = json.Substring(objectStart, objectEnd - objectStart + 1);
                var prefabPath = JsonUtil.ExtractString(objectJson, "prefab_path");
                var rootPath = JsonUtil.ExtractString(objectJson, "root_path");
                var item = new PrefabLayoutOverride
                {
                    PrefabPath = prefabPath,
                    RootPath = rootPath,
                    Note = JsonUtil.ExtractString(objectJson, "note"),
                    Status = IsUnityRelativePath(prefabPath) && !string.IsNullOrEmpty(rootPath) && rootPath.IndexOf("..", StringComparison.Ordinal) < 0 ? "ignored" : "invalid",
                    Reason = IsUnityRelativePath(prefabPath) && !string.IsNullOrEmpty(rootPath) && rootPath.IndexOf("..", StringComparison.Ordinal) < 0 ? "override did not match a scanned prefab target" : "prefab_path must be Unity-relative and root_path must stay inside the prefab hierarchy",
                };
                Vector2 vector2;
                Vector3 vector3;
                if (TryExtractVector2(objectJson, "size", out vector2))
                {
                    item.HasSize = true;
                    item.Size = vector2;
                }
                if (TryExtractVector2(objectJson, "parent_size", out vector2))
                {
                    item.HasParentSize = true;
                    item.ParentSize = vector2;
                }
                if (TryExtractVector2(objectJson, "anchored_position", out vector2))
                {
                    item.HasAnchoredPosition = true;
                    item.AnchoredPosition = vector2;
                }
                if (TryExtractVector3(objectJson, "scale", out vector3))
                {
                    item.HasScale = true;
                    item.Scale = vector3;
                }
                result.Add(item);
                index = objectEnd + 1;
            }

            return result;
        }

        static string PrefabLayoutOverridesJson(List<PrefabLayoutOverride> overrides)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < overrides.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append("{\"prefab_path\":").Append(JsonUtil.String(overrides[index].PrefabPath));
                builder.Append(",\"root_path\":").Append(JsonUtil.String(overrides[index].RootPath));
                builder.Append(",\"status\":").Append(JsonUtil.String(overrides[index].Status));
                if (overrides[index].HasSize)
                {
                    builder.Append(",\"size\":").Append(Vector2Json(overrides[index].Size));
                }
                if (overrides[index].HasParentSize)
                {
                    builder.Append(",\"parent_size\":").Append(Vector2Json(overrides[index].ParentSize));
                }
                if (overrides[index].HasAnchoredPosition)
                {
                    builder.Append(",\"anchored_position\":").Append(Vector2Json(overrides[index].AnchoredPosition));
                }
                if (overrides[index].HasScale)
                {
                    builder.Append(",\"scale\":").Append(Vector3Json(overrides[index].Scale));
                }
                if (!string.IsNullOrEmpty(overrides[index].Note))
                {
                    builder.Append(",\"note\":").Append(JsonUtil.String(overrides[index].Note));
                }
                builder.Append(",\"applied\":").Append(JsonUtil.Bool(overrides[index].Applied));
                builder.Append(",\"reason\":").Append(JsonUtil.String(overrides[index].Reason)).Append('}');
            }

            return "[" + builder + "]";
        }

        static Dictionary<string, NodeSnapshot> ScreenDiffNodeMap(ScanSnapshot snapshot, string pathPrefix)
        {
            var nodes = new Dictionary<string, NodeSnapshot>(StringComparer.Ordinal);
            foreach (var pair in snapshot.NodesById)
            {
                var node = pair.Value;
                if (node == null) continue;
                if (!string.IsNullOrEmpty(pathPrefix) && !string.Equals(node.Path, pathPrefix, StringComparison.Ordinal) && !node.Path.StartsWith(pathPrefix + "/", StringComparison.Ordinal)) continue;
                var key = node.Target + "|" + node.Path;
                if (!nodes.ContainsKey(key)) nodes.Add(key, node);
            }
            return nodes;
        }

        static string ScreenDiffJson(ScanSnapshot left, ScanSnapshot right, Dictionary<string, NodeSnapshot> leftNodes, Dictionary<string, NodeSnapshot> rightNodes, string pathPrefix, bool includeComponents, int maxDiffs)
        {
            var added = new StringBuilder();
            var removed = new StringBuilder();
            var changed = new StringBuilder();
            var addedTotal = 0;
            var removedTotal = 0;
            var changedTotal = 0;
            var returned = 0;
            var leftKeys = SortedKeys(leftNodes);
            var rightKeys = SortedKeys(rightNodes);
            for (var index = 0; index < rightKeys.Count; index++)
            {
                if (leftNodes.ContainsKey(rightKeys[index])) continue;
                addedTotal++;
                if (returned >= maxDiffs) continue;
                if (added.Length > 0) added.Append(',');
                added.Append(ScreenDiffNodeJson(rightNodes[rightKeys[index]], includeComponents));
                returned++;
            }
            for (var index = 0; index < leftKeys.Count; index++)
            {
                if (rightNodes.ContainsKey(leftKeys[index])) continue;
                removedTotal++;
                if (returned >= maxDiffs) continue;
                if (removed.Length > 0) removed.Append(',');
                removed.Append(ScreenDiffNodeJson(leftNodes[leftKeys[index]], includeComponents));
                returned++;
            }
            for (var index = 0; index < leftKeys.Count; index++)
            {
                var key = leftKeys[index];
                if (!rightNodes.ContainsKey(key)) continue;
                var fields = ScreenDiffChangedFields(leftNodes[key], rightNodes[key], includeComponents);
                if (fields.Count == 0) continue;
                changedTotal++;
                if (returned >= maxDiffs) continue;
                if (changed.Length > 0) changed.Append(',');
                changed.Append("{\"key\":").Append(JsonUtil.String(key))
                    .Append(",\"changed_fields\":").Append(StringArrayJson(fields))
                    .Append(",\"left\":").Append(ScreenDiffNodeJson(leftNodes[key], includeComponents))
                    .Append(",\"right\":").Append(ScreenDiffNodeJson(rightNodes[key], includeComponents)).Append('}');
                returned++;
            }
            var total = addedTotal + removedTotal + changedTotal;
            return "{\"status\":\"ok\",\"command\":\"ugui.screen.diff\",\"read_only\":true"
                + ",\"left_scan_id\":" + JsonUtil.String(left.ScanId)
                + ",\"right_scan_id\":" + JsonUtil.String(right.ScanId)
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_components\":" + JsonUtil.Bool(includeComponents)
                + ",\"left_nodes\":" + leftNodes.Count
                + ",\"right_nodes\":" + rightNodes.Count
                + ",\"diff_counts\":{\"added\":" + addedTotal + ",\"removed\":" + removedTotal + ",\"changed\":" + changedTotal + ",\"total\":" + total + ",\"returned\":" + returned + ",\"truncated\":" + JsonUtil.Bool(total > returned) + "}"
                + ",\"added\":[" + added + "]"
                + ",\"removed\":[" + removed + "]"
                + ",\"changed\":[" + changed + "]"
                + ",\"next_valid_actions\":[\"ugui.canvas.inspect_node\",\"ugui.canvas.report\",\"ugui.resource.read\"]}";
        }

        static List<string> SortedKeys(Dictionary<string, NodeSnapshot> nodes)
        {
            var keys = new List<string>(nodes.Keys);
            keys.Sort(StringComparer.Ordinal);
            return keys;
        }

        static string ScreenDiffNodeJson(NodeSnapshot node, bool includeComponents)
        {
            return "{\"target\":" + JsonUtil.String(node.Target)
                + ",\"path\":" + JsonUtil.String(node.Path)
                + ",\"node_id\":" + JsonUtil.String(node.NodeId)
                + ",\"name\":" + JsonUtil.String(node.Name)
                + ",\"depth\":" + node.Depth
                + ",\"active_self\":" + JsonUtil.Bool(node.ActiveSelf)
                + ",\"active_in_hierarchy\":" + JsonUtil.Bool(node.ActiveInHierarchy)
                + ",\"sibling_index\":" + node.SiblingIndex
                + ",\"child_count\":" + node.ChildCount
                + ",\"has_rect_transform\":" + JsonUtil.Bool(node.HasRectTransform)
                + ",\"rect_transform\":" + (string.IsNullOrEmpty(node.RectTransformJson) ? "null" : node.RectTransformJson)
                + ",\"components\":" + (includeComponents ? ScreenDiffComponentsJson(node) : "[]") + "}";
        }

        static string ScreenDiffComponentsJson(NodeSnapshot node)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < node.Components.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append("{\"type\":").Append(JsonUtil.String(node.Components[index].Type)).Append(",\"name\":").Append(JsonUtil.String(node.Components[index].Name)).Append('}');
            }
            return builder.Append(']').ToString();
        }

        static List<string> ScreenDiffChangedFields(NodeSnapshot left, NodeSnapshot right, bool includeComponents)
        {
            var fields = new List<string>();
            if (left.ActiveSelf != right.ActiveSelf) fields.Add("active_self");
            if (left.ActiveInHierarchy != right.ActiveInHierarchy) fields.Add("active_in_hierarchy");
            if (left.SiblingIndex != right.SiblingIndex) fields.Add("sibling_index");
            if (left.ChildCount != right.ChildCount) fields.Add("child_count");
            if (left.HasRectTransform != right.HasRectTransform || left.RectTransformJson != right.RectTransformJson) fields.Add("rect_transform");
            if (includeComponents && ScreenDiffComponentSignature(left) != ScreenDiffComponentSignature(right)) fields.Add("components");
            return fields;
        }

        static string ScreenDiffComponentSignature(NodeSnapshot node)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < node.Components.Count; index++)
            {
                if (index > 0) builder.Append('|');
                builder.Append(node.Components[index].Type).Append('#').Append(node.Components[index].Name);
            }
            return builder.ToString();
        }

        static int FindArrayStart(string json, string property)
        {
            if (string.IsNullOrEmpty(json))
            {
                return -1;
            }
            var propertyIndex = json.IndexOf("\"" + property + "\"", StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return -1;
            }
            var colon = json.IndexOf(':', propertyIndex);
            if (colon < 0)
            {
                return -1;
            }
            for (var index = colon + 1; index < json.Length; index++)
            {
                if (char.IsWhiteSpace(json[index]))
                {
                    continue;
                }
                return json[index] == '[' ? index : -1;
            }
            return -1;
        }

        static int FindMatchingObjectEnd(string json, int start)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString)
                {
                    continue;
                }
                if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }
                }
            }
            return -1;
        }

        static int FindMatchingArrayEnd(string json, int start)
        {
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString)
                {
                    continue;
                }
                if (character == '[')
                {
                    depth++;
                }
                else if (character == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return index;
                    }
                }
            }
            return -1;
        }

        static bool Approximately(float left, float right)
        {
            return Math.Abs(left - right) <= 0.0001f;
        }

        static bool Approximately(Vector2 left, Vector2 right)
        {
            return Approximately(left.x, right.x) && Approximately(left.y, right.y);
        }

        static bool Approximately(Vector3 left, Vector3 right)
        {
            return Approximately(left.x, right.x) && Approximately(left.y, right.y) && Approximately(left.z, right.z);
        }

        static bool IsFinite(Vector2 value)
        {
            return IsFinite(value.x) && IsFinite(value.y);
        }

        static bool IsFinite(Vector3 value)
        {
            return IsFinite(value.x) && IsFinite(value.y) && IsFinite(value.z);
        }

        static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
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

        static float NormalizeAngle(float value)
        {
            while (value > 180f)
            {
                value -= 360f;
            }
            while (value < -180f)
            {
                value += 360f;
            }
            return value;
        }

        static int Clamp(int value, int minimum, int maximum)
        {
            return Math.Min(maximum, Math.Max(minimum, value));
        }

        sealed class UguiModifyRequest
        {
            public string InvalidReason = string.Empty;
            public bool HasActiveSelf;
            public bool ActiveSelf;
            public bool HasAnchorMin;
            public Vector2 AnchorMin;
            public bool HasAnchorMax;
            public Vector2 AnchorMax;
            public bool HasAnchoredPosition;
            public Vector2 AnchoredPosition;
            public bool HasSizeDelta;
            public Vector2 SizeDelta;
            public bool HasPivot;
            public Vector2 Pivot;
            public bool HasLocalScale;
            public Vector3 LocalScale;
            public bool HasLocalEulerAngles;
            public Vector3 LocalEulerAngles;

            public bool HasAnyField
            {
                get { return HasActiveSelf || HasRectTransformField; }
            }

            public bool HasRectTransformField
            {
                get { return HasAnchorMin || HasAnchorMax || HasAnchoredPosition || HasSizeDelta || HasPivot || HasLocalScale || HasLocalEulerAngles; }
            }
        }

        sealed class UguiObjectCreateRequest
        {
            public string Name = string.Empty;
            public bool Active = true;
            public bool DryRun = true;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 Pivot = new Vector2(0.5f, 0.5f);
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public int SiblingIndex = -1;
        }

        sealed class UguiModifyState
        {
            public bool ActiveSelf;
            public bool HasRectTransform;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public Vector2 Pivot;
            public Vector3 LocalScale;
            public Vector3 LocalEulerAngles;
        }

        sealed class ScanState
        {
            public ScanState(int maxDepth, int maxNodes, bool includeInactive, bool includeComponents)
            {
                MaxDepth = maxDepth;
                MaxNodes = maxNodes;
                IncludeInactive = includeInactive;
                IncludeComponents = includeComponents;
            }

            public int MaxDepth { get; private set; }
            public int MaxNodes { get; private set; }
            public bool IncludeInactive { get; private set; }
            public bool IncludeComponents { get; private set; }
            public int TargetsFound { get; set; }
            public int CanvasRoots { get; set; }
            public int NodesIncluded { get; set; }
            public bool Truncated { get; set; }
        }

        sealed class ScanSnapshot
        {
            public ScanSnapshot(string scanId, List<string> sourceTargets, List<PrefabLayoutOverride> overrides)
            {
                ScanId = scanId;
                SourceTargets = new List<string>(sourceTargets);
                Overrides = new List<PrefabLayoutOverride>(overrides);
            }

            public string ScanId { get; private set; }
            public List<string> SourceTargets { get; private set; }
            public List<PrefabLayoutOverride> Overrides { get; private set; }
            public List<TargetSnapshot> Targets = new List<TargetSnapshot>();
            public Dictionary<string, NodeSnapshot> NodesById = new Dictionary<string, NodeSnapshot>();
            public Dictionary<string, string> ObjectPathsByInstanceId = new Dictionary<string, string>();
            public Dictionary<string, int> ComponentCounts = new Dictionary<string, int>();
            public Dictionary<string, int> EventCounts = new Dictionary<string, int>();
            public List<EventBindingSnapshot> EventBindings = new List<EventBindingSnapshot>();
            public List<NavigationNodeSnapshot> NavigationNodes = new List<NavigationNodeSnapshot>();
            public List<EventSystemSnapshot> EventSystems = new List<EventSystemSnapshot>();
            public List<RaycastGraphicSnapshot> RaycastGraphics = new List<RaycastGraphicSnapshot>();
            public List<CanvasGroupSnapshot> CanvasGroups = new List<CanvasGroupSnapshot>();
            public List<GraphicRaycasterSnapshot> GraphicRaycasters = new List<GraphicRaycasterSnapshot>();
            public List<AssetReferenceSnapshot> AssetReferences = new List<AssetReferenceSnapshot>();
            public int CanvasRoots;
            public int NodesIncluded;
            public int MissingScripts;
            public bool Truncated;
            public bool AssetReferencesTruncated;
        }

        sealed class UguiResourceSnapshot
        {
            public UguiResourceSnapshot(string uri, string content, string mimeType)
            {
                Uri = uri;
                Content = content ?? string.Empty;
                MimeType = mimeType;
            }

            public string Uri { get; private set; }
            public string Content { get; private set; }
            public string MimeType { get; private set; }
        }

        sealed class TargetSnapshot
        {
            public string Target = string.Empty;
            public string Kind = string.Empty;
            public string Status = string.Empty;
            public string Reason = string.Empty;
        }

        sealed class NodeSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public int Depth;
            public string ParentNodeId = string.Empty;
            public string Name = string.Empty;
            public string Target = string.Empty;
            public GameObject GameObject;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public int SiblingIndex;
            public int ChildCount;
            public string GameObjectInstanceId = string.Empty;
            public string TransformInstanceId = string.Empty;
            public bool HasRectTransform;
            public string RectTransformJson = string.Empty;
            public Vector2 AnchorMin;
            public Vector2 AnchorMax;
            public Vector2 AnchoredPosition;
            public Vector2 SizeDelta;
            public Vector2 Pivot;
            public Vector3 LocalScale;
            public Vector3 LocalEulerAngles;
            public Vector2 RectSize;
            public Vector2 WorldMin;
            public Vector2 WorldMax;
            public string CanvasRenderMode = string.Empty;
            public List<ComponentSnapshot> Components = new List<ComponentSnapshot>();
            public int MissingScripts;
        }

        sealed class InteractionStep
        {
            public string NodeId = string.Empty;
            public string Action = string.Empty;
            public string Text = string.Empty;
            public bool HasToggleValue;
            public bool ToggleValue;
            public bool HasExpectedInteractable;
            public bool ExpectedInteractable = true;
        }

        sealed class InteractionStepResult
        {
            public int Index;
            public InteractionStep Step;
            public NodeSnapshot Node;
            public Component Component;
            public string ExpectedComponent = string.Empty;
            public string ComponentType = string.Empty;
            public bool Interactable;
            public bool EventSystemRequired;
            public bool EventSystemFound;
            public bool CanExecute;
            public bool Executed;
            public string Status = string.Empty;
            public string Message = string.Empty;
        }

        sealed class LayoutFinding
        {
            public LayoutFinding(string severity, string code, string nodeId, string path, string message, bool overrideDependent)
            {
                Severity = severity;
                Code = code;
                NodeId = nodeId;
                Path = path;
                Message = message;
                OverrideDependent = overrideDependent;
            }

            public string Severity = string.Empty;
            public string Code = string.Empty;
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string Message = string.Empty;
            public bool OverrideDependent;
            public Vector2 ScreenSize;
        }

        sealed class ComponentSnapshot
        {
            public string InstanceId = string.Empty;
            public string Type = string.Empty;
            public string Name = string.Empty;
        }

        sealed class EventBindingSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string ComponentType = string.Empty;
            public string ComponentName = string.Empty;
            public string EventName = string.Empty;
            public string EventType = string.Empty;
            public string EventClass = string.Empty;
            public int ListenerIndex;
            public string TargetType = string.Empty;
            public string TargetName = string.Empty;
            public string TargetObjectId = string.Empty;
            public string MethodName = string.Empty;
            public string ListenerState = string.Empty;
            public string MethodSignature = string.Empty;
            public bool MethodAmbiguous;
        }

        sealed class NavigationNodeSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string Target = string.Empty;
            public string ComponentType = string.Empty;
            public string ComponentName = string.Empty;
            public string ComponentInstanceId = string.Empty;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public bool ComponentEnabled = true;
            public bool Interactable = true;
            public string Mode = string.Empty;
            public bool WrapAround;
            public List<NavigationEdgeSnapshot> Edges = new List<NavigationEdgeSnapshot>();
        }

        sealed class NavigationEdgeSnapshot
        {
            public string FromNodeId = string.Empty;
            public string FromPath = string.Empty;
            public string FromComponentId = string.Empty;
            public string Direction = string.Empty;
            public string TargetObjectId = string.Empty;
            public string TargetName = string.Empty;
            public string TargetType = string.Empty;
            public string ToNodeId = string.Empty;
            public string ToPath = string.Empty;
            public bool ActiveInMode;
            public bool TargetMissingInScan;
        }

        sealed class EventSystemSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string ComponentType = string.Empty;
            public string ComponentName = string.Empty;
            public string FirstSelectedObjectId = string.Empty;
            public string FirstSelectedName = string.Empty;
            public string FirstSelectedType = string.Empty;
        }

        sealed class NavigationFinding
        {
            public NavigationFinding(string severity, string code, string nodeId, string path, string message)
            {
                Severity = severity;
                Code = code;
                NodeId = nodeId;
                Path = path;
                Message = message;
            }

            public string Severity = string.Empty;
            public string Code = string.Empty;
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string Message = string.Empty;
        }

        sealed class AssetReferenceSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string Target = string.Empty;
            public string ComponentType = string.Empty;
            public string ComponentName = string.Empty;
            public string PropertyPath = string.Empty;
            public string ReferenceType = string.Empty;
            public string AssetPath = string.Empty;
            public string AssetGuid = string.Empty;
            public string Name = string.Empty;
            public string ObjectType = string.Empty;
        }

        sealed class AssetReferenceSummary
        {
            public string ReferenceType = string.Empty;
            public string AssetPath = string.Empty;
            public string AssetGuid = string.Empty;
            public string Name = string.Empty;
            public string ObjectType = string.Empty;
            public int Count;
        }

        sealed class RaycastGraphicSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string Target = string.Empty;
            public string ComponentType = string.Empty;
            public string ComponentName = string.Empty;
            public bool ActiveSelf;
            public bool ActiveInHierarchy;
            public bool ComponentEnabled = true;
            public bool RaycastTarget;
            public float ColorAlpha = 1f;
            public Vector2 RectSize;
            public Vector2 WorldMin;
            public Vector2 WorldMax;
            public int SiblingIndex;
            public int Depth;
        }

        sealed class CanvasGroupSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string ComponentType = string.Empty;
            public string ComponentName = string.Empty;
            public bool ActiveInHierarchy;
            public bool ComponentEnabled = true;
            public float Alpha = 1f;
            public bool BlocksRaycasts = true;
            public bool Interactable = true;
            public bool IgnoreParentGroups;
        }

        sealed class GraphicRaycasterSnapshot
        {
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string ComponentType = string.Empty;
            public string ComponentName = string.Empty;
            public bool ActiveInHierarchy;
            public bool ComponentEnabled = true;
            public bool IgnoreReversedGraphics = true;
            public string BlockingObjects = string.Empty;
            public string BlockingMask = string.Empty;
        }

        sealed class RaycastFinding
        {
            public RaycastFinding(string severity, string code, string nodeId, string path, string message)
            {
                Severity = severity;
                Code = code;
                NodeId = nodeId;
                Path = path;
                Message = message;
            }

            public string Severity = string.Empty;
            public string Code = string.Empty;
            public string NodeId = string.Empty;
            public string Path = string.Empty;
            public string Message = string.Empty;
        }

        sealed class PrefabLayoutOverride
        {
            public string PrefabPath = string.Empty;
            public string RootPath = string.Empty;
            public string Status = string.Empty;
            public string Reason = string.Empty;
            public string Note = string.Empty;
            public bool HasSize;
            public Vector2 Size;
            public bool HasParentSize;
            public Vector2 ParentSize;
            public bool HasAnchoredPosition;
            public Vector2 AnchoredPosition;
            public bool HasScale;
            public Vector3 Scale = Vector3.one;
            public bool Applied;
        }
    }
}
