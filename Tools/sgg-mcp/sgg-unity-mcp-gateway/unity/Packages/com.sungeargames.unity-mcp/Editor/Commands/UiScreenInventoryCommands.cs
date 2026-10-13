using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class UiScreenInventoryCommands
    {
        const string FeatureFlag = "ui_readonly";
        const string InventorySchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_scenes\":{\"type\":\"boolean\"},\"include_prefabs\":{\"type\":\"boolean\"},\"include_uxml\":{\"type\":\"boolean\"},\"include_panelsettings\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"max_name_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":50}},\"additionalProperties\":false}";
        const string InspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"guid\":{\"type\":\"string\",\"minLength\":32,\"maxLength\":32,\"pattern\":\"^[A-Fa-f0-9]{32}$\"},\"screen_id\":{\"type\":\"string\",\"minLength\":32,\"maxLength\":32,\"pattern\":\"^[A-Fa-f0-9]{32}$\"},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"max_name_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":50},\"include_delegation_plan\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string InteractionMapSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_scenes\":{\"type\":\"boolean\"},\"include_prefabs\":{\"type\":\"boolean\"},\"include_uxml\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"max_name_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":50},\"max_event_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":50}},\"additionalProperties\":false}";
        const string InventoryOutputLimitJson = "{\"max_results\":200,\"max_scan_assets\":100000,\"max_scan_bytes\":1048576,\"max_name_samples\":50,\"readonly\":true}";
        const string InspectOutputLimitJson = "{\"max_scan_bytes\":1048576,\"max_name_samples\":50,\"readonly\":true}";
        const string InteractionMapOutputLimitJson = "{\"max_results\":200,\"max_scan_assets\":100000,\"max_scan_bytes\":1048576,\"max_name_samples\":50,\"max_event_samples\":50,\"readonly\":true}";
        const int MaxResults = 200;
        const int MaxScanAssets = 100000;
        const int MaxScanBytes = 1048576;
        const int MaxNameSamples = 50;
        const int MaxEventSamples = 50;

        [McpCommand(
            "ui.screen.inventory",
            Title = "UI screen inventory",
            Description = "Inventory bounded cross-UI screen candidates from uGUI, UITK, TMP and localization markers without opening scenes or mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = InventorySchemaJson,
            Tags = new string[] { "ui", "screen", "inventory", "ugui", "uitk", "tmp", "localization", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = InventoryOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Inventory(McpCommandContext context)
        {
            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeScenes = JsonUtil.ExtractBool(context.ArgsJson, "include_scenes", true);
            var includePrefabs = JsonUtil.ExtractBool(context.ArgsJson, "include_prefabs", true);
            var includeUxml = JsonUtil.ExtractBool(context.ArgsJson, "include_uxml", true);
            var includePanelSettings = JsonUtil.ExtractBool(context.ArgsJson, "include_panelsettings", true);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, MaxResults);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxScanAssets);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 262144), 1, MaxScanBytes);
            var maxNameSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_name_samples", 12), 0, MaxNameSamples);
            var paths = CandidatePaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, includeScenes, includePrefabs, includeUxml, includePanelSettings);
            var scannedAssets = 0;
            var skippedAssets = 0;
            var readErrorCount = 0;
            var readTruncatedCount = 0;
            var matchedScreens = 0;
            var returnedResults = 0;
            var scanTruncated = false;
            var resultsTruncated = false;
            var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var subsystemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var tmpFontGuids = new SortedSet<string>(StringComparer.Ordinal);
            var localizationHints = new SortedSet<string>(StringComparer.Ordinal);
            var results = new StringBuilder();

            foreach (var assetPath in paths)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                ScreenCandidate candidate;
                var analyzed = TryAnalyzeCandidate(assetPath, maxScanBytes, maxNameSamples, out candidate, ref readErrorCount, ref readTruncatedCount);
                if (!analyzed)
                {
                    skippedAssets++;
                    continue;
                }

                matchedScreens++;
                if (matchedScreens <= startIndex)
                {
                    continue;
                }

                if (returnedResults >= maxResults)
                {
                    resultsTruncated = true;
                    break;
                }

                Increment(kindCounts, candidate.Kind);
                foreach (var subsystem in candidate.Subsystems)
                {
                    Increment(subsystemCounts, subsystem);
                }

                foreach (var guid in candidate.TmpFontAssetGuids)
                {
                    tmpFontGuids.Add(guid);
                }

                foreach (var hint in candidate.LocalizationHints)
                {
                    localizationHints.Add(hint);
                }

                if (returnedResults > 0)
                {
                    results.Append(',');
                }

                AppendCandidateJson(results, candidate);
                returnedResults++;
            }

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_scenes\":" + JsonUtil.Bool(includeScenes)
                + ",\"include_prefabs\":" + JsonUtil.Bool(includePrefabs)
                + ",\"include_uxml\":" + JsonUtil.Bool(includeUxml)
                + ",\"include_panelsettings\":" + JsonUtil.Bool(includePanelSettings)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"max_name_samples\":" + maxNameSamples
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"skipped_assets\":" + skippedAssets
                + ",\"read_error_count\":" + readErrorCount
                + ",\"read_truncated_count\":" + readTruncatedCount
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"screen_candidates_found\":" + matchedScreens
                + ",\"returned_results\":" + returnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"next_start_index\":" + (resultsTruncated ? (startIndex + returnedResults).ToString() : "null")
                + ",\"kind_counts\":" + CountsJson(kindCounts)
                + ",\"subsystem_counts\":" + CountsJson(subsystemCounts)
                + ",\"coverage_pointers\":{\"tmp_font_asset_guids\":" + JsonArrayStrings(tmpFontGuids)
                + ",\"localization_hints\":" + JsonArrayStrings(localizationHints)
                + ",\"ui_text_inventory_command\":\"ui.text.inventory\",\"ui_text_references_command\":\"ui.text.references.report\",\"tmp_coverage_command\":\"tmp.font_asset.coverage.check\"}"
                + ",\"screen_candidates\":[" + results + "]"
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":[\"This read-only aggregate uses AssetDatabase metadata and bounded serialized text markers only; it does not open scenes, enter prefab mode, import, refresh, capture screenshots, save, write or modify assets.\",\"uGUI, UITK, TMP and localization markers are provenance hints. Use the listed lower-level commands for focused inspection before edits.\"]"
                + ",\"next_valid_actions\":[\"ugui.canvas.scan\",\"ugui.canvas.report\",\"uitk.document.inspect\",\"uitk.uxml.inspect\",\"uitk.panelsettings.inspect\",\"ui.text.inventory\",\"ui.text.references.report\",\"tmp.font_asset.coverage.check\",\"game.view.screenshot\",\"editor.window.screenshot\"]}");
        }

        [McpCommand(
            "ui.screen.inspect",
            Title = "UI screen inspect",
            Description = "Inspect one bounded cross-UI screen candidate by asset path, GUID, or screen ID and return explicit lower-level delegation steps without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = InspectSchemaJson,
            Tags = new string[] { "ui", "screen", "inspect", "ugui", "uitk", "tmp", "localization", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = InspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Inspect(McpCommandContext context)
        {
            var resolveResult = ResolveInspectAssetPath(context.ArgsJson, out var assetPath);
            if (resolveResult != null)
            {
                return resolveResult;
            }

            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 262144), 1, MaxScanBytes);
            var maxNameSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_name_samples", 20), 0, MaxNameSamples);
            var includeDelegationPlan = JsonUtil.ExtractBool(context.ArgsJson, "include_delegation_plan", true);
            var readErrorCount = 0;
            var readTruncatedCount = 0;
            ScreenCandidate candidate;
            if (!TryAnalyzeCandidate(assetPath, maxScanBytes, maxNameSamples, out candidate, ref readErrorCount, ref readTruncatedCount))
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path, guid or screen_id does not resolve to a bounded UI screen candidate", "{\"asset_path\":" + JsonUtil.String(assetPath) + ",\"next_valid_actions\":[\"ui.screen.inventory\",\"asset.info\",\"asset.references\"]}");
            }

            var candidateJson = new StringBuilder();
            AppendCandidateJson(candidateJson, candidate);
            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(candidate.Guid)
                + ",\"kind\":" + JsonUtil.String(candidate.Kind)
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"max_name_samples\":" + maxNameSamples
                + ",\"read_error_count\":" + readErrorCount
                + ",\"read_truncated_count\":" + readTruncatedCount
                + ",\"screen_candidate\":" + candidateJson
                + ",\"delegation_plan\":" + (includeDelegationPlan ? DelegationPlanJson(candidate) : "[]")
                + ",\"focused_inspection_limits\":{\"asset_level_only\":true,\"opens_scenes\":false,\"enters_prefab_mode\":false,\"captures_screenshots\":false,\"mutates_assets\":false}"
                + ",\"warnings\":[\"This read-only focused inspection does not execute lower-level commands; use the delegation_plan entries as explicit follow-up calls when their feature flags and policy allow them.\",\"Scan-dependent commands such as ugui.canvas.inspect_node require a scan_id and node_id returned by ugui.canvas.scan.\"]"
                + ",\"next_valid_actions\":[\"ugui.canvas.scan\",\"uitk.document.inspect\",\"uitk.uxml.inspect\",\"uitk.panelsettings.inspect\",\"ui.text.references.report\",\"tmp.font_asset.coverage.check\",\"game.view.screenshot\",\"editor.window.screenshot\"]}");
        }

        [McpCommand(
            "ui.interaction.map",
            Title = "UI interaction map",
            Description = "Map bounded cross-UI interaction hints from uGUI Selectable/navigation/events and UI Toolkit UXML bindings without executing interactions or mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = InteractionMapSchemaJson,
            Tags = new string[] { "ui", "interaction", "map", "ugui", "uitk", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = InteractionMapOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult InteractionMap(McpCommandContext context)
        {
            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeScenes = JsonUtil.ExtractBool(context.ArgsJson, "include_scenes", true);
            var includePrefabs = JsonUtil.ExtractBool(context.ArgsJson, "include_prefabs", true);
            var includeUxml = JsonUtil.ExtractBool(context.ArgsJson, "include_uxml", true);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, MaxResults);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxScanAssets);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 262144), 1, MaxScanBytes);
            var maxNameSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_name_samples", 12), 0, MaxNameSamples);
            var maxEventSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_event_samples", 12), 0, MaxEventSamples);
            var paths = CandidatePaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, includeScenes, includePrefabs, includeUxml, false);
            var scannedAssets = 0;
            var skippedAssets = 0;
            var readErrorCount = 0;
            var readTruncatedCount = 0;
            var matchedMaps = 0;
            var returnedResults = 0;
            var scanTruncated = false;
            var resultsTruncated = false;
            var kindCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var subsystemCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var interactionCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var results = new StringBuilder();

            foreach (var assetPath in paths)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                InteractionMapCandidate candidate;
                if (!TryAnalyzeInteractionMap(assetPath, maxScanBytes, maxNameSamples, maxEventSamples, out candidate, ref readErrorCount, ref readTruncatedCount))
                {
                    skippedAssets++;
                    continue;
                }

                matchedMaps++;
                if (matchedMaps <= startIndex)
                {
                    continue;
                }

                if (returnedResults >= maxResults)
                {
                    resultsTruncated = true;
                    break;
                }

                Increment(kindCounts, candidate.Kind);
                foreach (var subsystem in candidate.Subsystems)
                {
                    Increment(subsystemCounts, subsystem);
                }

                foreach (var source in candidate.InteractionSources)
                {
                    Increment(interactionCounts, source);
                }

                if (returnedResults > 0)
                {
                    results.Append(',');
                }

                AppendInteractionMapJson(results, candidate);
                returnedResults++;
            }

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_scenes\":" + JsonUtil.Bool(includeScenes)
                + ",\"include_prefabs\":" + JsonUtil.Bool(includePrefabs)
                + ",\"include_uxml\":" + JsonUtil.Bool(includeUxml)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"max_name_samples\":" + maxNameSamples
                + ",\"max_event_samples\":" + maxEventSamples
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"skipped_assets\":" + skippedAssets
                + ",\"read_error_count\":" + readErrorCount
                + ",\"read_truncated_count\":" + readTruncatedCount
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"interaction_maps_found\":" + matchedMaps
                + ",\"returned_results\":" + returnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"next_start_index\":" + (resultsTruncated ? (startIndex + returnedResults).ToString() : "null")
                + ",\"kind_counts\":" + CountsJson(kindCounts)
                + ",\"subsystem_counts\":" + CountsJson(subsystemCounts)
                + ",\"interaction_source_counts\":" + CountsJson(interactionCounts)
                + ",\"interaction_maps\":[" + results + "]"
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":[\"This read-only aggregate uses AssetDatabase metadata and bounded serialized text markers only; it does not open scenes, enter prefab mode, import, refresh, enter Play Mode, execute interactions, capture screenshots, save, write or modify assets.\",\"uGUI navigation/event and UITK binding markers are provenance hints. Use the listed lower-level commands for scan-backed details before any interaction test.\"]"
                + ",\"next_valid_actions\":[\"ugui.canvas.scan\",\"ugui.navigation.analyze\",\"ugui.events.extract\",\"ugui.interaction.smoke\",\"uitk.document.inspect\",\"uitk.uxml.inspect\",\"uitk.bindings.validate\",\"uitk.interaction.smoke\",\"ui.screen.inspect\"]}");
        }

        static CommandExecutionResult ResolveInspectAssetPath(string argsJson, out string assetPath)
        {
            assetPath = string.Empty;
            var rawAssetPath = JsonUtil.ExtractString(argsJson, "asset_path");
            var guid = JsonUtil.ExtractString(argsJson, "guid");
            var screenId = JsonUtil.ExtractString(argsJson, "screen_id");
            var selectorCount = (string.IsNullOrEmpty(rawAssetPath) ? 0 : 1) + (string.IsNullOrEmpty(guid) ? 0 : 1) + (string.IsNullOrEmpty(screenId) ? 0 : 1);
            if (selectorCount > 1)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Pass exactly one of asset_path, guid, or screen_id");
            }

            if (!string.IsNullOrEmpty(guid) || !string.IsNullOrEmpty(screenId))
            {
                var assetGuid = string.IsNullOrEmpty(guid) ? screenId : guid;
                if (!IsGuid(assetGuid))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "guid or screen_id must be a 32-character Unity asset GUID");
                }

                assetPath = AssetDatabase.GUIDToAssetPath(assetGuid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return CommandExecutionResult.Error("asset_not_found", "guid or screen_id did not resolve to a Unity asset");
                }

                try
                {
                    assetPath = PathGuard.RequireUnityAssetPath(assetPath.Replace('\\', '/'));
                }
                catch (ArgumentException)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "guid or screen_id resolved to a path outside safe Unity asset roots");
                }
            }
            else if (!string.IsNullOrEmpty(rawAssetPath))
            {
                try
                {
                    assetPath = PathGuard.RequireUnityAssetPath(rawAssetPath.Replace('\\', '/'));
                }
                catch (ArgumentException)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "asset_path must be a safe Unity-relative Assets/... or Packages/... path");
                }
            }
            else
            {
                return CommandExecutionResult.Error("schema_validation_failed", "ui.screen.inspect requires asset_path, guid, or screen_id");
            }

            if (AssetDatabase.IsValidFolder(assetPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "ui.screen.inspect requires a UI asset path, not a folder");
            }

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                return CommandExecutionResult.Error("asset_not_found", "asset_path was not found in the Unity AssetDatabase");
            }

            return null;
        }

        static string DelegationPlanJson(ScreenCandidate candidate)
        {
            var entries = new List<string>();
            if (candidate.Subsystems.Contains("ugui"))
            {
                entries.Add(DelegationEntryJson("ugui.canvas.scan", "{\"targets\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_inactive\":true,\"max_nodes\":1000}", candidate.Kind == "scene" ? "Scene targets must already be loaded; this aggregate command does not open scenes." : "Creates a scan_id for node-level uGUI follow-up commands."));
                entries.Add(DelegationEntryJson("ugui.canvas.report", "{\"scan_id\":\"<scan_id from ugui.canvas.scan>\"}", "Use after ugui.canvas.scan."));
                entries.Add(DelegationEntryJson("ugui.canvas.inspect_node", "{\"scan_id\":\"<scan_id from ugui.canvas.scan>\",\"node_id\":\"<node_id from scan>\"}", "Use after selecting a node from ugui.canvas.scan/report."));
                entries.Add(DelegationEntryJson("ugui.bounds.snapshot", "{\"scan_id\":\"<scan_id from ugui.canvas.scan>\",\"max_nodes\":1000}", "Use after ugui.canvas.scan when visual bounds context is needed."));
            }

            if (candidate.Subsystems.Contains("uitk"))
            {
                if (candidate.Kind == "scene" || candidate.Kind == "prefab")
                {
                    entries.Add(DelegationEntryJson("uitk.document.inspect", "{\"targets\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_inactive\":true}", candidate.Kind == "scene" ? "Scene targets must already be loaded; this aggregate command does not open scenes." : "Inspect UIDocument references on this prefab asset."));
                }

                if (candidate.Kind == "uxml")
                {
                    entries.Add(DelegationEntryJson("uitk.uxml.inspect", "{\"uxml_paths\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_tree\":false,\"include_names\":true,\"include_classes\":true}", "Inspect focused UXML structure directly."));
                    entries.Add(DelegationEntryJson("uitk.references.report", "{\"uxml_paths\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_images\":true,\"include_fonts\":true,\"include_templates\":true,\"include_style_sheets\":true,\"max_references\":100}", "Inspect UITK references for this UXML asset."));
                }

                if (candidate.Kind == "panelsettings")
                {
                    entries.Add(DelegationEntryJson("uitk.panelsettings.inspect", "{\"panel_settings_paths\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_references\":true}", "Inspect focused PanelSettings asset directly."));
                }
            }

            entries.Add(DelegationEntryJson("ui.text.references.report", "{\"path_prefix\":" + JsonUtil.String(candidate.AssetPath) + ",\"max_sources\":100}", "Aggregate UI text, TMP font and localization hints for this asset."));
            if (candidate.Subsystems.Contains("tmp") || candidate.Subsystems.Contains("localization"))
            {
                entries.Add(DelegationEntryJson("tmp.font_asset.coverage.check", "{\"asset_path\":\"<TMP font asset path>\",\"unicode_text\":\"<unicode text or codepoints from ui.text.references.report>\"}", "Run after choosing a concrete TMP font asset and Unicode input."));
            }

            entries.Add(DelegationEntryJson("game.view.screenshot", "{\"dry_run\":true}", "Optional visual capture; this command does not capture screenshots itself."));
            entries.Add(DelegationEntryJson("editor.window.screenshot", "{\"title\":\"Game\",\"dry_run\":true}", "Optional editor-window capture when policy and feature flags allow it."));
            return JsonArray(entries);
        }

        static string DelegationEntryJson(string command, string argsJson, string note)
        {
            return "{\"command\":" + JsonUtil.String(command) + ",\"args_json\":" + JsonUtil.String(argsJson) + ",\"manual_call_required\":true,\"normal_policy_required\":true,\"note\":" + JsonUtil.String(note) + "}";
        }

        static string JsonArray(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append(value);
            }

            builder.Append(']');
            return builder.ToString();
        }

        static bool TryAnalyzeInteractionMap(string assetPath, int maxScanBytes, int maxNameSamples, int maxEventSamples, out InteractionMapCandidate candidate, ref int readErrorCount, ref int readTruncatedCount)
        {
            candidate = null;
            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            var typeName = type == null ? string.Empty : type.FullName;
            var kind = CandidateKind(assetPath, typeName);
            if (string.IsNullOrEmpty(kind) || !ShouldReadSerializedText(assetPath, typeName))
            {
                return false;
            }

            if (!TryResolveAssetFilePath(assetPath, out var fullPath))
            {
                return false;
            }

            string text;
            long byteLength;
            bool fileTruncated;
            int scannedBytes;
            try
            {
                var bytes = ReadBoundedBytes(fullPath, maxScanBytes, out byteLength, out fileTruncated);
                scannedBytes = bytes.Length;
                if (fileTruncated)
                {
                    readTruncatedCount++;
                }

                if (!TryDecodeText(bytes, fileTruncated, out text))
                {
                    return false;
                }
            }
            catch (Exception exception)
            {
                if (IsExpectedReadException(exception))
                {
                    readErrorCount++;
                    return false;
                }

                throw;
            }

            candidate = new InteractionMapCandidate(assetPath, AssetDatabase.AssetPathToGUID(assetPath), kind, typeName, byteLength, scannedBytes, fileTruncated);
            AnalyzeInteractionMarkers(text, candidate, maxEventSamples);
            CollectGameObjectNameSamples(text, maxNameSamples, candidate.NameSamples);
            if (kind == "uxml")
            {
                CollectUxmlNameSamples(text, maxNameSamples, candidate.NameSamples);
            }

            candidate.Normalize();
            if (candidate.HasInteractionMarkers)
            {
                return true;
            }

            candidate = null;
            return false;
        }

        static void AnalyzeInteractionMarkers(string text, InteractionMapCandidate candidate, int maxEventSamples)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            candidate.UguiSelectableHintCount = CountOccurrences(text, "m_Interactable:") + CountOccurrences(text, "UnityEngine.UI.Selectable");
            candidate.UguiNavigationHintCount = CountOccurrences(text, "m_Navigation:");
            candidate.UguiExplicitNavigationHintCount = CountOccurrences(text, "m_SelectOnUp:")
                + CountOccurrences(text, "m_SelectOnDown:")
                + CountOccurrences(text, "m_SelectOnLeft:")
                + CountOccurrences(text, "m_SelectOnRight:");
            candidate.UguiEventHintCount = CountOccurrences(text, "m_OnClick:")
                + CountOccurrences(text, "m_OnValueChanged:")
                + CountOccurrences(text, "onValueChanged")
                + CountOccurrences(text, "m_OnEndEdit:")
                + CountOccurrences(text, "m_OnSubmit:")
                + CountOccurrences(text, "m_PersistentCalls:")
                + CountOccurrences(text, "UnityEvent");
            candidate.UguiPersistentCallCount = CountOccurrences(text, "m_MethodName:");
            candidate.UguiEventSystemHintCount = CountOccurrences(text, "EventSystem") + CountOccurrences(text, "m_FirstSelected:") + CountOccurrences(text, "m_sendNavigationEvents:");

            candidate.UitkInteractiveElementCount = CountRegex(text, "<\\s*(ui:)?(Button|Toggle|TextField|Slider|Scroller|ScrollView|ListView|TreeView|DropdownField|Foldout|ToolbarButton|EnumField|ObjectField|IntegerField|FloatField|LongField|DoubleField|MinMaxSlider)\\b");
            candidate.UitkBindingAttributeCount = CountRegex(text, "\\b(binding-path|bindingPath)\\s*=");
            candidate.UitkNamedElementCount = CountRegex(text, "\\bname\\s*=");
            candidate.UitkEventHintCount = CountRegex(text, "\\b(clickable|picking-mode|tabindex|focusable|view-data-key)\\s*=");
            CollectInteractionEventSamples(text, maxEventSamples, candidate.EventSamples);
        }

        static void AppendInteractionMapJson(StringBuilder builder, InteractionMapCandidate candidate)
        {
            builder.Append('{');
            builder.Append("\"screen_id\":").Append(JsonUtil.String(candidate.Guid));
            builder.Append(",\"asset_path\":").Append(JsonUtil.String(candidate.AssetPath));
            builder.Append(",\"guid\":").Append(JsonUtil.String(candidate.Guid));
            builder.Append(",\"kind\":").Append(JsonUtil.String(candidate.Kind));
            builder.Append(",\"asset_type\":").Append(JsonUtil.String(candidate.TypeName));
            builder.Append(",\"provenance\":{\"source\":\"asset_database_and_bounded_serialized_text\",\"bytes_on_disk\":").Append(candidate.ByteLength);
            builder.Append(",\"scanned_bytes\":").Append(candidate.ScannedBytes);
            builder.Append(",\"file_truncated\":").Append(JsonUtil.Bool(candidate.FileTruncated)).Append('}');
            builder.Append(",\"subsystems\":").Append(JsonArrayStrings(candidate.Subsystems));
            builder.Append(",\"interaction_sources\":").Append(JsonArrayStrings(candidate.InteractionSources));
            builder.Append(",\"markers\":{\"ugui_selectable_hint_count\":").Append(candidate.UguiSelectableHintCount);
            builder.Append(",\"ugui_navigation_hint_count\":").Append(candidate.UguiNavigationHintCount);
            builder.Append(",\"ugui_explicit_navigation_hint_count\":").Append(candidate.UguiExplicitNavigationHintCount);
            builder.Append(",\"ugui_event_hint_count\":").Append(candidate.UguiEventHintCount);
            builder.Append(",\"ugui_persistent_call_count\":").Append(candidate.UguiPersistentCallCount);
            builder.Append(",\"ugui_event_system_hint_count\":").Append(candidate.UguiEventSystemHintCount);
            builder.Append(",\"uitk_interactive_element_count\":").Append(candidate.UitkInteractiveElementCount);
            builder.Append(",\"uitk_binding_attribute_count\":").Append(candidate.UitkBindingAttributeCount);
            builder.Append(",\"uitk_named_element_count\":").Append(candidate.UitkNamedElementCount);
            builder.Append(",\"uitk_event_hint_count\":").Append(candidate.UitkEventHintCount).Append('}');
            builder.Append(",\"runtime_smoke_eligibility\":").Append(RuntimeSmokeEligibilityJson(candidate));
            builder.Append(",\"uitk_runtime_smoke_eligibility\":").Append(UitkRuntimeSmokeEligibilityJson(candidate));
            builder.Append(",\"name_samples\":").Append(JsonArrayStrings(candidate.NameSamples));
            builder.Append(",\"event_samples\":").Append(JsonArrayStrings(candidate.EventSamples));
            builder.Append(",\"lower_level_commands\":").Append(JsonArrayStrings(InteractionLowerLevelCommands(candidate)));
            builder.Append(",\"delegation_plan\":").Append(InteractionDelegationPlanJson(candidate));
            builder.Append('}');
        }

        static string RuntimeSmokeEligibilityJson(InteractionMapCandidate candidate)
        {
            return "{\"eligible\":" + JsonUtil.Bool(candidate.RuntimeSmokeEligible)
                + ",\"command\":\"ugui.interaction.smoke\""
                + ",\"requires_scan_id\":true"
                + ",\"requires_node_ids\":true"
                + ",\"dry_run_default\":true"
                + ",\"execution_is_ask_gated\":true"
                + ",\"executes_in_this_command\":false"
                + ",\"reason\":" + JsonUtil.String(candidate.RuntimeSmokeEligible ? "uGUI interaction markers were found; run ugui.canvas.scan and choose node_id values before any dry-run smoke plan." : "No uGUI Selectable or UnityEvent markers were found in this bounded asset sample.") + "}";
        }

        static string UitkRuntimeSmokeEligibilityJson(InteractionMapCandidate candidate)
        {
            var eligible = candidate.Subsystems.Contains("uitk")
                && (candidate.UitkInteractiveElementCount > 0
                    || candidate.UitkNamedElementCount > 0
                    || candidate.UitkBindingAttributeCount > 0
                    || candidate.UitkEventHintCount > 0);
            return "{\"eligible\":" + JsonUtil.Bool(eligible)
                + ",\"command\":\"uitk.interaction.smoke\""
                + ",\"requires_scene_path\":true"
                + ",\"requires_document_object_path\":true"
                + ",\"requires_component_index\":true"
                + ",\"requires_exact_loaded_uidocument\":true"
                + ",\"dry_run_default\":true"
                + ",\"execution_is_ask_gated\":true"
                + ",\"executes_in_this_command\":false"
                + ",\"reason\":" + JsonUtil.String(eligible
                    ? "UITK interaction markers were found; inspect the exact loaded UIDocument and use its scene path, object path, component index, and named controls before any dry-run smoke plan."
                    : "No bounded UITK interaction markers were found in this asset sample.") + "}";
        }

        static string UitkRuntimeSmokeEligibilityJson(ScreenCandidate candidate)
        {
            var eligible = candidate.Subsystems.Contains("uitk")
                && (candidate.UxmlElementCount > 0 || candidate.UitkDocumentHintCount > 0 || candidate.Kind == "uxml");
            return "{\"eligible\":" + JsonUtil.Bool(eligible)
                + ",\"command\":\"uitk.interaction.smoke\""
                + ",\"requires_scene_path\":true"
                + ",\"requires_document_object_path\":true"
                + ",\"requires_component_index\":true"
                + ",\"requires_exact_loaded_uidocument\":true"
                + ",\"dry_run_default\":true"
                + ",\"execution_is_ask_gated\":true"
                + ",\"executes_in_this_command\":false"
                + ",\"reason\":" + JsonUtil.String(eligible
                    ? "UITK markers were found; inspect the exact loaded UIDocument before constructing a named-control smoke request."
                    : "No bounded UITK runtime markers were found in this asset sample.") + "}";
        }

        static List<string> InteractionLowerLevelCommands(InteractionMapCandidate candidate)
        {
            var commands = new List<string> { "ui.screen.inspect", "asset.info", "asset.references" };
            if (candidate.Subsystems.Contains("ugui"))
            {
                commands.Add("ugui.canvas.scan");
                commands.Add("ugui.navigation.analyze");
                commands.Add("ugui.events.extract");
                commands.Add("ugui.interaction.smoke");
            }

            if (candidate.Subsystems.Contains("uitk"))
            {
                commands.Add("uitk.document.inspect");
                commands.Add("uitk.uxml.inspect");
                commands.Add("uitk.bindings.validate");
                commands.Add("uitk.interaction.smoke");
            }

            return UniqueSorted(commands);
        }

        static string InteractionDelegationPlanJson(InteractionMapCandidate candidate)
        {
            var entries = new List<string>();
            entries.Add(DelegationEntryJson("ui.screen.inspect", "{\"asset_path\":" + JsonUtil.String(candidate.AssetPath) + ",\"include_delegation_plan\":true}", "Inspect the same candidate with broader cross-UI provenance."));
            if (candidate.Subsystems.Contains("ugui"))
            {
                entries.Add(DelegationEntryJson("ugui.canvas.scan", "{\"targets\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_inactive\":true,\"include_events\":true,\"max_nodes\":1000}", candidate.Kind == "scene" ? "Scene targets must already be loaded; this aggregate command does not open scenes." : "Create a scan_id before navigation, events or smoke-plan follow-up."));
                entries.Add(DelegationEntryJson("ugui.navigation.analyze", "{\"scan_id\":\"<scan_id from ugui.canvas.scan>\",\"include_edges\":true,\"max_selectables\":500,\"max_edges\":1000}", "Use after ugui.canvas.scan to analyze Selectable keyboard/gamepad focus graph."));
                entries.Add(DelegationEntryJson("ugui.events.extract", "{\"scan_id\":\"<scan_id from ugui.canvas.scan>\",\"include_target_paths\":true,\"include_method_signatures\":true,\"max_events\":500}", "Use after ugui.canvas.scan to inspect persistent UnityEvent bindings."));
                entries.Add(DelegationEntryJson("ugui.interaction.smoke", "{\"scan_id\":\"<scan_id from ugui.canvas.scan>\",\"steps\":[{\"node_id\":\"<node_id from scan>\",\"action\":\"focus\"}],\"dry_run\":true,\"max_steps\":32}", "Dry-run-first plan only; execution remains a separate ask-gated command and may trigger UI callbacks."));
            }

            if (candidate.Subsystems.Contains("uitk"))
            {
                if (candidate.Kind == "scene" || candidate.Kind == "prefab")
                {
                    entries.Add(DelegationEntryJson("uitk.document.inspect", "{\"targets\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_inactive\":true,\"include_visual_tree_asset\":true}", candidate.Kind == "scene" ? "Scene targets must already be loaded; this aggregate command does not open scenes." : "Inspect UIDocument references on this prefab asset."));
                }

                if (candidate.Kind == "uxml")
                {
                    entries.Add(DelegationEntryJson("uitk.uxml.inspect", "{\"uxml_paths\":[" + JsonUtil.String(candidate.AssetPath) + "],\"include_tree\":false,\"include_names\":true,\"include_classes\":true}", "Inspect focused UXML names/classes before binding validation."));
                    entries.Add(DelegationEntryJson("uitk.bindings.validate", "{\"binder_paths\":[\"<C# binder path>\"],\"uxml_paths\":[" + JsonUtil.String(candidate.AssetPath) + "],\"max_findings\":100}", "Use after selecting a concrete binder script that queries this UXML."));
                }

                entries.Add(DelegationEntryJson("uitk.interaction.smoke", "{\"scene_path\":\"<exact loaded scene path>\",\"document_object_path\":\"<exact UIDocument object path>\",\"component_index\":0,\"steps\":[{\"control_name\":\"<exact VisualElement.name>\",\"action\":\"focus\"}],\"dry_run\":true,\"settle_frames\":0}", "Replace component_index with the exact integer from uitk.document.inspect. Use only after it identifies a loaded UIDocument runtime identity; this command is Play Mode-only, ask-gated, and never saves scenes or files."));
            }

            return JsonArray(entries);
        }

        static void CollectInteractionEventSamples(string text, int maxEventSamples, List<string> samples)
        {
            if (maxEventSamples <= 0) return;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null && samples.Count < maxEventSamples)
                {
                    var trimmed = line.Trim();
                    if (trimmed.IndexOf("m_On", StringComparison.OrdinalIgnoreCase) >= 0
                        || trimmed.IndexOf("onValueChanged", StringComparison.OrdinalIgnoreCase) >= 0
                        || trimmed.IndexOf("m_PersistentCalls", StringComparison.OrdinalIgnoreCase) >= 0
                        || trimmed.IndexOf("m_MethodName", StringComparison.OrdinalIgnoreCase) >= 0
                        || trimmed.IndexOf("binding-path", StringComparison.OrdinalIgnoreCase) >= 0
                        || trimmed.IndexOf("clickable", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        samples.Add(Bounded(trimmed, 160));
                    }
                }
            }
        }

        static void CollectUxmlNameSamples(string text, int maxNameSamples, List<string> names)
        {
            if (maxNameSamples <= 0 || names.Count >= maxNameSamples) return;
            var matches = Regex.Matches(text, "\\bname\\s*=\\s*['\"]([^'\"]{1,120})['\"]", RegexOptions.IgnoreCase);
            foreach (Match match in matches)
            {
                if (names.Count >= maxNameSamples) break;
                var value = match.Groups.Count > 1 ? match.Groups[1].Value : string.Empty;
                if (!string.IsNullOrEmpty(value) && !names.Contains(value))
                {
                    names.Add(Bounded(value, 120));
                }
            }
        }

        static bool TryAnalyzeCandidate(string assetPath, int maxScanBytes, int maxNameSamples, out ScreenCandidate candidate, ref int readErrorCount, ref int readTruncatedCount)
        {
            candidate = null;
            var extension = Path.GetExtension(assetPath);
            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            var typeName = type == null ? string.Empty : type.FullName;
            var kind = CandidateKind(assetPath, typeName);
            if (string.IsNullOrEmpty(kind))
            {
                return false;
            }

            var text = string.Empty;
            long byteLength = 0;
            var scannedBytes = 0;
            var fileTruncated = false;
            if (ShouldReadSerializedText(assetPath, typeName))
            {
                if (!TryResolveAssetFilePath(assetPath, out var fullPath))
                {
                    return false;
                }

                try
                {
                    byte[] bytes = ReadBoundedBytes(fullPath, maxScanBytes, out byteLength, out fileTruncated);
                    scannedBytes = bytes.Length;
                    if (fileTruncated)
                    {
                        readTruncatedCount++;
                    }

                    if (!TryDecodeText(bytes, fileTruncated, out text))
                    {
                        return false;
                    }
                }
                catch (Exception exception)
                {
                    if (IsExpectedReadException(exception))
                    {
                        readErrorCount++;
                        return false;
                    }

                    throw;
                }
            }

            candidate = new ScreenCandidate(assetPath, AssetDatabase.AssetPathToGUID(assetPath), kind, typeName, byteLength, scannedBytes, fileTruncated);
            AnalyzeSerializedMarkers(text, candidate, maxNameSamples);
            if (string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase))
            {
                candidate.Subsystems.Add("uitk");
                candidate.UxmlElementCount = CountRegex(text, "<\\s*[A-Za-z_][A-Za-z0-9_.:-]*");
                candidate.UxmlTextAttributeCount = CountRegex(text, "\\b(text|label|tooltip|placeholder|title)\\s*=");
            }

            if (typeName.IndexOf("PanelSettings", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                candidate.Subsystems.Add("uitk");
                candidate.PanelSettingsHint = true;
            }

            if (candidate.HasUiMarkers)
            {
                candidate.NormalizeSubsystems();
                return true;
            }

            candidate = null;
            return false;
        }

        static void AnalyzeSerializedMarkers(string text, ScreenCandidate candidate, int maxNameSamples)
        {
            if (string.IsNullOrEmpty(text))
            {
                return;
            }

            candidate.UguiCanvasCount = CountOccurrences(text, "--- !u!223");
            candidate.RectTransformCount = CountOccurrences(text, "--- !u!224");
            candidate.CanvasRendererCount = CountOccurrences(text, "--- !u!222");
            candidate.UitkDocumentHintCount = CountOccurrences(text, "m_PanelSettings:") + CountOccurrences(text, "m_SourceAsset:") + CountOccurrences(text, "UIDocument");
            candidate.TmpTextHintCount = CountOccurrences(text, "m_fontAsset:") + CountOccurrences(text, "m_FontAsset:") + CountOccurrences(text, "TMP_") + CountOccurrences(text, "TextMeshPro");
            candidate.LocalizationHintCount = CountOccurrences(text, "m_TableReference") + CountOccurrences(text, "m_TableEntryReference") + CountOccurrences(text, "LocalizedString") + CountOccurrences(text, "localization");

            if (candidate.UguiCanvasCount > 0 || candidate.RectTransformCount > 0 || candidate.CanvasRendererCount > 0)
            {
                candidate.Subsystems.Add("ugui");
            }

            if (candidate.UitkDocumentHintCount > 0)
            {
                candidate.Subsystems.Add("uitk");
            }

            if (candidate.TmpTextHintCount > 0)
            {
                candidate.Subsystems.Add("tmp");
            }

            if (candidate.LocalizationHintCount > 0)
            {
                candidate.Subsystems.Add("localization");
            }

            CollectTmpFontGuids(text, candidate.TmpFontAssetGuids);
            CollectLocalizationHints(text, candidate.LocalizationHints);
            CollectGameObjectNameSamples(text, maxNameSamples, candidate.NameSamples);
        }

        static void AppendCandidateJson(StringBuilder builder, ScreenCandidate candidate)
        {
            builder.Append('{');
            builder.Append("\"screen_id\":").Append(JsonUtil.String(candidate.Guid));
            builder.Append(",\"asset_path\":").Append(JsonUtil.String(candidate.AssetPath));
            builder.Append(",\"guid\":").Append(JsonUtil.String(candidate.Guid));
            builder.Append(",\"kind\":").Append(JsonUtil.String(candidate.Kind));
            builder.Append(",\"asset_type\":").Append(JsonUtil.String(candidate.TypeName));
            builder.Append(",\"provenance\":{\"source\":\"asset_database_and_bounded_serialized_text\",\"bytes_on_disk\":").Append(candidate.ByteLength);
            builder.Append(",\"scanned_bytes\":").Append(candidate.ScannedBytes);
            builder.Append(",\"file_truncated\":").Append(JsonUtil.Bool(candidate.FileTruncated)).Append('}');
            builder.Append(",\"subsystems\":").Append(JsonArrayStrings(candidate.Subsystems));
            builder.Append(",\"markers\":{\"ugui_canvas_count\":").Append(candidate.UguiCanvasCount);
            builder.Append(",\"rect_transform_count\":").Append(candidate.RectTransformCount);
            builder.Append(",\"canvas_renderer_count\":").Append(candidate.CanvasRendererCount);
            builder.Append(",\"uitk_document_hint_count\":").Append(candidate.UitkDocumentHintCount);
            builder.Append(",\"panelsettings_hint\":").Append(JsonUtil.Bool(candidate.PanelSettingsHint));
            builder.Append(",\"uxml_element_count\":").Append(candidate.UxmlElementCount);
            builder.Append(",\"uxml_text_attribute_count\":").Append(candidate.UxmlTextAttributeCount);
            builder.Append(",\"tmp_text_hint_count\":").Append(candidate.TmpTextHintCount);
            builder.Append(",\"localization_hint_count\":").Append(candidate.LocalizationHintCount).Append('}');
            builder.Append(",\"name_samples\":").Append(JsonArrayStrings(candidate.NameSamples));
            builder.Append(",\"coverage_pointers\":{\"tmp_font_asset_guids\":").Append(JsonArrayStrings(candidate.TmpFontAssetGuids));
            builder.Append(",\"localization_hints\":").Append(JsonArrayStrings(candidate.LocalizationHints));
            builder.Append(",\"commands\":[\"ui.text.references.report\",\"tmp.font_asset.coverage.check\"]}");
            builder.Append(",\"uitk_runtime_smoke_eligibility\":").Append(UitkRuntimeSmokeEligibilityJson(candidate));
            builder.Append(",\"screenshot_targets\":").Append(ScreenshotTargetsJson(candidate));
            builder.Append(",\"lower_level_commands\":").Append(JsonArrayStrings(LowerLevelCommands(candidate)));
            builder.Append('}');
        }

        static string ScreenshotTargetsJson(ScreenCandidate candidate)
        {
            var targets = new List<string>();
            if (candidate.Subsystems.Contains("ugui") || candidate.Subsystems.Contains("uitk"))
            {
                targets.Add("game.view.screenshot");
                targets.Add("editor.window.screenshot");
            }

            if (candidate.Subsystems.Contains("uitk"))
            {
                targets.Add("uitk.preview");
            }

            return JsonArrayStrings(targets);
        }

        static List<string> LowerLevelCommands(ScreenCandidate candidate)
        {
            var commands = new List<string>();
            if (candidate.Subsystems.Contains("ugui"))
            {
                commands.Add("ugui.canvas.scan");
                commands.Add("ugui.canvas.report");
                commands.Add("ugui.canvas.inspect_node");
                commands.Add("ugui.bounds.snapshot");
            }

            if (candidate.Subsystems.Contains("uitk"))
            {
                commands.Add("uitk.document.inspect");
                commands.Add("uitk.uxml.inspect");
                commands.Add("uitk.panelsettings.inspect");
                commands.Add("uitk.layout.analyze");
                commands.Add("uitk.references.report");
                commands.Add("uitk.interaction.smoke");
            }

            if (candidate.Subsystems.Contains("tmp") || candidate.Subsystems.Contains("localization"))
            {
                commands.Add("ui.text.inventory");
                commands.Add("ui.text.references.report");
                commands.Add("tmp.font_asset.coverage.check");
            }

            commands.Add("asset.info");
            commands.Add("asset.references");
            return UniqueSorted(commands);
        }

        static List<string> CandidatePaths(string[] searchRoots, bool includePackages, string pathPrefix, bool includeScenes, bool includePrefabs, bool includeUxml, bool includePanelSettings)
        {
            var guids = AssetDatabase.FindAssets(string.Empty, searchRoots);
            var paths = new List<string>(guids.Length);
            for (var index = 0; index < guids.Length; index++)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath) || (!includePackages && assetPath.StartsWith("Packages/", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!assetPath.StartsWith(pathPrefix, StringComparison.Ordinal) || !IsCandidatePath(assetPath, includeScenes, includePrefabs, includeUxml, includePanelSettings))
                {
                    continue;
                }

                try
                {
                    assetPath = PathGuard.RequireUnityAssetPath(assetPath);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!paths.Contains(assetPath))
                {
                    paths.Add(assetPath);
                }
            }

            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        static bool IsCandidatePath(string assetPath, bool includeScenes, bool includePrefabs, bool includeUxml, bool includePanelSettings)
        {
            var extension = Path.GetExtension(assetPath);
            if (includeScenes && string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)) return true;
            if (includePrefabs && string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)) return true;
            if (includeUxml && string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase)) return true;
            if (!includePanelSettings || !string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase)) return false;
            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            var typeName = type == null ? string.Empty : type.FullName;
            return typeName.IndexOf("PanelSettings", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string CandidateKind(string assetPath, string typeName)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)) return "scene";
            if (string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)) return "prefab";
            if (string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase)) return "uxml";
            if (typeName.IndexOf("PanelSettings", StringComparison.OrdinalIgnoreCase) >= 0) return "panelsettings";
            return string.Empty;
        }

        static bool ShouldReadSerializedText(string assetPath, string typeName)
        {
            var extension = Path.GetExtension(assetPath);
            return string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase)
                || typeName.IndexOf("PanelSettings", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static CommandExecutionResult RequireAssetPrefix(string argsJson, out string pathPrefix)
        {
            pathPrefix = JsonUtil.ExtractString(argsJson, "path_prefix");
            if (string.IsNullOrEmpty(pathPrefix))
            {
                pathPrefix = "Assets/";
                return null;
            }

            try
            {
                pathPrefix = NormalizeAssetPrefix(pathPrefix);
                return null;
            }
            catch (ArgumentException)
            {
                pathPrefix = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", "path_prefix must be a safe Unity-relative prefix under Assets/ or Packages/");
            }
        }

        static string NormalizeAssetPrefix(string rawPrefix)
        {
            PathGuard.AssertNoNul(rawPrefix);
            var normalized = rawPrefix.Replace('\\', '/');
            if (normalized == "Assets" || normalized == "Assets/") return "Assets/";
            if (normalized == "Packages" || normalized == "Packages/") return "Packages/";
            var hadTrailingSlash = normalized.EndsWith("/", StringComparison.Ordinal);
            var candidate = hadTrailingSlash ? normalized.TrimEnd('/') : normalized;
            candidate = PathGuard.RequireUnityAssetPath(candidate);
            return hadTrailingSlash ? candidate + "/" : candidate;
        }

        static string[] SearchRoots(string pathPrefix, bool includePackages)
        {
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal)) return new[] { "Packages" };
            return includePackages ? new[] { "Assets", "Packages" } : new[] { "Assets" };
        }

        static bool TryResolveAssetFilePath(string assetPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath == "Assets")
            {
                fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
                return IsSameOrChildPath(fullPath, ProjectPaths.ProjectRoot) && File.Exists(fullPath);
            }

            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal)) return false;
            var relative = assetPath.Substring("Packages/".Length);
            var slash = relative.IndexOf('/');
            var packageName = slash < 0 ? relative : relative.Substring(0, slash);
            var packageRelative = slash < 0 ? string.Empty : relative.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar);
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (!string.Equals(package.name, packageName, StringComparison.Ordinal)) continue;
                var packageRoot = Path.GetFullPath(package.resolvedPath);
                var candidate = Path.GetFullPath(Path.Combine(packageRoot, packageRelative));
                if (IsSameOrChildPath(candidate, packageRoot) && File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }

            var fallback = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsSameOrChildPath(fallback, ProjectPaths.ProjectRoot) && File.Exists(fallback))
            {
                fullPath = fallback;
                return true;
            }

            return false;
        }

        static bool IsSameOrChildPath(string candidate, string root)
        {
            var normalizedCandidate = Path.GetFullPath(candidate).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedCandidate, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalizedCandidate.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static byte[] ReadBoundedBytes(string fullPath, int maxBytes, out long byteLength, out bool truncated)
        {
            var info = new FileInfo(fullPath);
            byteLength = info.Length;
            truncated = byteLength > maxBytes;
            var readLength = (int)Math.Min(byteLength, maxBytes);
            var bytes = new byte[readLength];
            var offset = 0;
            using (var stream = File.OpenRead(fullPath))
            {
                while (offset < readLength)
                {
                    var read = stream.Read(bytes, offset, readLength - offset);
                    if (read <= 0) break;
                    offset += read;
                }
            }

            if (offset != bytes.Length)
            {
                Array.Resize(ref bytes, offset);
            }

            return bytes;
        }

        static bool TryDecodeText(byte[] bytes, bool truncated, out string content)
        {
            content = string.Empty;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] == 0) return false;
            }

            try
            {
                content = new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length);
                return true;
            }
            catch (DecoderFallbackException)
            {
                if (!truncated) return false;
                for (var trim = 1; trim <= 3 && trim <= bytes.Length; trim++)
                {
                    try
                    {
                        content = new UTF8Encoding(false, true).GetString(bytes, 0, bytes.Length - trim);
                        return true;
                    }
                    catch (DecoderFallbackException)
                    {
                    }
                }

                return false;
            }
        }

        static void CollectGameObjectNameSamples(string text, int maxNameSamples, List<string> names)
        {
            if (maxNameSamples <= 0) return;
            var insideGameObject = false;
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null && names.Count < maxNameSamples)
                {
                    if (line.StartsWith("--- !u!", StringComparison.Ordinal))
                    {
                        insideGameObject = line.StartsWith("--- !u!1 ", StringComparison.Ordinal) || line.StartsWith("--- !u!1 &", StringComparison.Ordinal);
                        continue;
                    }

                    if (!insideGameObject) continue;
                    var trimmed = line.TrimStart();
                    if (!trimmed.StartsWith("m_Name:", StringComparison.Ordinal)) continue;
                    var value = DecodeYamlScalar(trimmed.Substring("m_Name:".Length).Trim());
                    if (!string.IsNullOrEmpty(value) && !names.Contains(value))
                    {
                        names.Add(Bounded(value, 120));
                    }
                }
            }
        }

        static void CollectTmpFontGuids(string text, SortedSet<string> guids)
        {
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if ((line.IndexOf("fontAsset", StringComparison.OrdinalIgnoreCase) >= 0 || line.IndexOf("FontAsset", StringComparison.Ordinal) >= 0) && TryExtractGuid(line, out var guid))
                    {
                        guids.Add(guid);
                    }
                }
            }
        }

        static void CollectLocalizationHints(string text, SortedSet<string> hints)
        {
            using (var reader = new StringReader(text))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    var trimmed = line.Trim();
                    if (trimmed.StartsWith("m_TableReference", StringComparison.Ordinal) || trimmed.StartsWith("m_TableEntryReference", StringComparison.Ordinal) || trimmed.IndexOf("LocalizedString", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hints.Add(Bounded(trimmed, 160));
                    }
                }
            }
        }

        static bool TryExtractGuid(string line, out string guid)
        {
            guid = string.Empty;
            var marker = "guid:";
            var index = line.IndexOf(marker, StringComparison.Ordinal);
            if (index < 0) return false;
            index += marker.Length;
            while (index < line.Length && char.IsWhiteSpace(line[index])) index++;
            var start = index;
            while (index < line.Length && IsGuidChar(line[index])) index++;
            if (index - start != 32) return false;
            guid = line.Substring(start, 32);
            return true;
        }

        static bool IsGuid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32) return false;
            for (var index = 0; index < value.Length; index++)
            {
                if (!IsGuidChar(value[index])) return false;
            }

            return true;
        }

        static bool IsGuidChar(char value)
        {
            return (value >= 'a' && value <= 'f') || (value >= 'A' && value <= 'F') || (value >= '0' && value <= '9');
        }

        static string DecodeYamlScalar(string value)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'') return value.Substring(1, value.Length - 2).Replace("''", "'");
            if (value.Length >= 2 && value[0] == '"')
            {
                var end = value.LastIndexOf('"');
                if (end > 0) return value.Substring(1, end - 1).Replace("\\\"", "\"").Replace("\\n", " ").Replace("\\r", " ").Replace("\\t", " ");
            }

            return value;
        }

        static bool IsExpectedReadException(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException;
        }

        static int CountOccurrences(string text, string token)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return 0;
            var count = 0;
            var index = 0;
            while ((index = text.IndexOf(token, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                count++;
                index += token.Length;
            }

            return count;
        }

        static int CountRegex(string text, string pattern)
        {
            return string.IsNullOrEmpty(text) ? 0 : Regex.Matches(text, pattern, RegexOptions.IgnoreCase).Count;
        }

        static string CountsJson(Dictionary<string, int> counts)
        {
            var keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            builder.Append('{');
            for (var index = 0; index < keys.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append(JsonUtil.String(keys[index])).Append(':').Append(counts[keys[index]]);
            }

            builder.Append('}');
            return builder.ToString();
        }

        static string JsonArrayStrings(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var first = true;
            foreach (var value in values)
            {
                if (!first) builder.Append(',');
                first = false;
                builder.Append(JsonUtil.String(value));
            }

            builder.Append(']');
            return builder.ToString();
        }

        static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            counts.TryGetValue(key, out var count);
            counts[key] = count + 1;
        }

        static List<string> UniqueSorted(List<string> values)
        {
            var set = new SortedSet<string>(values, StringComparer.Ordinal);
            return new List<string>(set);
        }

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Replace('\n', ' ').Replace('\r', ' ').Trim();
            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        sealed class InteractionMapCandidate
        {
            public readonly string AssetPath;
            public readonly string Guid;
            public readonly string Kind;
            public readonly string TypeName;
            public readonly long ByteLength;
            public readonly int ScannedBytes;
            public readonly bool FileTruncated;
            public readonly SortedSet<string> Subsystems = new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedSet<string> InteractionSources = new SortedSet<string>(StringComparer.Ordinal);
            public readonly List<string> NameSamples = new List<string>();
            public readonly List<string> EventSamples = new List<string>();
            public int UguiSelectableHintCount;
            public int UguiNavigationHintCount;
            public int UguiExplicitNavigationHintCount;
            public int UguiEventHintCount;
            public int UguiPersistentCallCount;
            public int UguiEventSystemHintCount;
            public int UitkInteractiveElementCount;
            public int UitkBindingAttributeCount;
            public int UitkNamedElementCount;
            public int UitkEventHintCount;

            public InteractionMapCandidate(string assetPath, string guid, string kind, string typeName, long byteLength, int scannedBytes, bool fileTruncated)
            {
                AssetPath = assetPath;
                Guid = guid;
                Kind = kind;
                TypeName = typeName;
                ByteLength = byteLength;
                ScannedBytes = scannedBytes;
                FileTruncated = fileTruncated;
            }

            public bool RuntimeSmokeEligible
            {
                get { return UguiSelectableHintCount > 0 || UguiEventHintCount > 0 || UguiPersistentCallCount > 0; }
            }

            public bool HasInteractionMarkers
            {
                get { return InteractionSources.Count > 0; }
            }

            public void Normalize()
            {
                if (UguiSelectableHintCount > 0)
                {
                    Subsystems.Add("ugui");
                    InteractionSources.Add("ugui_selectable");
                }

                if (UguiNavigationHintCount > 0 || UguiExplicitNavigationHintCount > 0)
                {
                    Subsystems.Add("ugui");
                    InteractionSources.Add("ugui_navigation");
                }

                if (UguiEventHintCount > 0 || UguiPersistentCallCount > 0)
                {
                    Subsystems.Add("ugui");
                    InteractionSources.Add("ugui_unity_event");
                }

                if (UguiEventSystemHintCount > 0)
                {
                    Subsystems.Add("ugui");
                    InteractionSources.Add("ugui_event_system");
                }

                if (Kind == "uxml" || UitkInteractiveElementCount > 0 || UitkBindingAttributeCount > 0 || UitkNamedElementCount > 0 || UitkEventHintCount > 0)
                {
                    Subsystems.Add("uitk");
                }

                if (UitkInteractiveElementCount > 0)
                {
                    InteractionSources.Add("uitk_interactive_element");
                }

                if (UitkBindingAttributeCount > 0)
                {
                    InteractionSources.Add("uitk_binding");
                }

                if (UitkNamedElementCount > 0)
                {
                    InteractionSources.Add("uitk_named_element");
                }

                if (UitkEventHintCount > 0)
                {
                    InteractionSources.Add("uitk_event_hint");
                }
            }
        }

        sealed class ScreenCandidate
        {
            public readonly string AssetPath;
            public readonly string Guid;
            public readonly string Kind;
            public readonly string TypeName;
            public readonly long ByteLength;
            public readonly int ScannedBytes;
            public readonly bool FileTruncated;
            public readonly SortedSet<string> Subsystems = new SortedSet<string>(StringComparer.Ordinal);
            public readonly List<string> NameSamples = new List<string>();
            public readonly SortedSet<string> TmpFontAssetGuids = new SortedSet<string>(StringComparer.Ordinal);
            public readonly SortedSet<string> LocalizationHints = new SortedSet<string>(StringComparer.Ordinal);
            public int UguiCanvasCount;
            public int RectTransformCount;
            public int CanvasRendererCount;
            public int UitkDocumentHintCount;
            public int TmpTextHintCount;
            public int LocalizationHintCount;
            public int UxmlElementCount;
            public int UxmlTextAttributeCount;
            public bool PanelSettingsHint;

            public ScreenCandidate(string assetPath, string guid, string kind, string typeName, long byteLength, int scannedBytes, bool fileTruncated)
            {
                AssetPath = assetPath;
                Guid = guid;
                Kind = kind;
                TypeName = typeName;
                ByteLength = byteLength;
                ScannedBytes = scannedBytes;
                FileTruncated = fileTruncated;
            }

            public bool HasUiMarkers
            {
                get { return Subsystems.Count > 0 || Kind == "uxml" || Kind == "panelsettings"; }
            }

            public void NormalizeSubsystems()
            {
                if (Kind == "uxml" || Kind == "panelsettings") Subsystems.Add("uitk");
                if (TmpFontAssetGuids.Count > 0) Subsystems.Add("tmp");
                if (LocalizationHints.Count > 0) Subsystems.Add("localization");
            }
        }
    }
}
