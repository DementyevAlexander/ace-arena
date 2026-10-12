using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEngine;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class AddressablesReadonlyCommands
    {
        const string FeatureFlag = "addressables_readonly";
        const string StatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000}},\"additionalProperties\":false}";
        const string GroupsListSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"include_schemas\":{\"type\":\"boolean\"},\"include_entries_summary\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_scan_groups\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_entries_per_group\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000}},\"additionalProperties\":false}";
        const string EntriesSearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"group_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"label\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"include_labels\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_scan_groups\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_entries_per_group\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000}},\"additionalProperties\":false}";
        const string LabelsReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"group_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"include_entries\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_scan_groups\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_entries_per_group\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000},\"max_entry_samples_per_label\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":50}},\"additionalProperties\":false}";
        const string DependenciesReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"group_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"label\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"include_labels\":{\"type\":\"boolean\"},\"include_dependencies\":{\"type\":\"boolean\"},\"recursive\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_scan_groups\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_entries_per_group\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000},\"max_dependencies_per_entry\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500},\"write_artifact\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string BuildPreflightSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_entry_samples\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_scan_groups\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_entries_per_group\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000},\"write_artifact\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string StatusOutputLimitJson = "{\"max_scan_assets\":100000,\"readonly\":true}";
        const string GroupsListOutputLimitJson = "{\"max_results\":500,\"max_scan_groups\":100000,\"max_entries_per_group\":10000,\"readonly\":true}";
        const string EntriesSearchOutputLimitJson = "{\"max_results\":1000,\"max_scan_groups\":100000,\"max_entries_per_group\":10000,\"readonly\":true}";
        const string LabelsReportOutputLimitJson = "{\"max_results\":500,\"max_scan_groups\":100000,\"max_entries_per_group\":10000,\"max_entry_samples_per_label\":50,\"max_groups_per_label\":50,\"readonly\":true}";
        const string DependenciesReportOutputLimitJson = "{\"max_results\":1000,\"max_scan_groups\":100000,\"max_entries_per_group\":10000,\"max_dependencies_per_entry\":500,\"max_asset_risk_report_targets\":50,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/addressables-reports\",\"readonly\":true}";
        const string BuildPreflightOutputLimitJson = "{\"max_results\":1000,\"max_scan_groups\":100000,\"max_entries_per_group\":10000,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/addressables-reports\",\"build_started\":false,\"remote_operations_started\":false,\"network_operations_started\":false,\"readonly\":true}";
        const string AddressablesReportArtifactRoot = "Library/SGGUnityMcp/job-artifacts/addressables-reports";
        const int MaxResults = 500;
        const int MaxEntryResults = 1000;
        const int MaxScanAssets = 100000;
        const int MaxEntriesPerGroup = 10000;
        const int MaxSchemasPerGroup = 50;
        const int MaxLabelsPerEntry = 100;
        const int MaxEntrySamplesPerLabel = 50;
        const int MaxGroupsPerLabel = 50;
        const int MaxDependenciesPerEntry = 500;
        const int MaxRiskReportTargets = 50;

        [McpCommand(
            "addressables.status",
            Title = "Addressables status",
            Description = "Detect local Addressables package, editor API, settings, and group asset availability without a compile-time Addressables dependency",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = StatusSchemaJson,
            Tags = new string[] { "addressables", "content", "delivery", "status", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = StatusOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Status(McpCommandContext context)
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

            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", MaxScanAssets), 1, MaxScanAssets);
            var roots = SearchRoots(pathPrefix, includePackages);
            var settingsPaths = CandidateAssetsByType("AddressableAssetSettings", roots, includePackages, pathPrefix, maxScanAssets, out var scannedSettingsAssets, out var settingsScanTruncated);
            var groupPaths = CandidateAssetsByType("AddressableAssetGroup", roots, includePackages, pathPrefix, maxScanAssets, out var scannedGroupAssets, out var groupScanTruncated);
            var package = FindAddressablesPackage();

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"package_installed\":" + JsonUtil.Bool(package != null)
                + ",\"package\":" + PackageJson(package)
                + ",\"editor_api_loaded\":" + JsonUtil.Bool(AddressablesEditorApiLoaded())
                + ",\"settings_asset_count\":" + settingsPaths.Count
                + ",\"group_asset_count\":" + groupPaths.Count
                + ",\"scanned_settings_assets\":" + scannedSettingsAssets
                + ",\"scanned_group_assets\":" + scannedGroupAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(settingsScanTruncated || groupScanTruncated)
                + ",\"settings_assets\":" + AssetPathsJson(settingsPaths)
                + ",\"group_assets_sample\":" + AssetPathsJson(FirstPaths(groupPaths, 25))
                + ",\"warnings\":[\"This command reads package metadata and AssetDatabase-discovered Addressables assets only; it does not build content, contact remotes, import, refresh, write, move, or fix assets.\",\"Addressables internals are inspected by serialized field names to avoid a compile-time Addressables dependency; field availability can vary by package version.\"]"
                + ",\"next_valid_actions\":[\"addressables.groups.list\",\"addressables.entries.search\",\"addressables.labels.report\",\"addressables.dependencies.report\",\"addressables.build.preflight\",\"asset.risk_report\",\"asset.dependencies\"]}");
        }

        [McpCommand(
            "addressables.groups.list",
            Title = "List Addressables groups",
            Description = "List Addressables group assets with bounded entry, schema, label, and read-only summaries without building or mutating content",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = GroupsListSchemaJson,
            Tags = new string[] { "addressables", "content", "groups", "inventory", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = GroupsListOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GroupsList(McpCommandContext context)
        {
            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeSchemas = JsonUtil.ExtractBool(context.ArgsJson, "include_schemas", true);
            var includeEntriesSummary = JsonUtil.ExtractBool(context.ArgsJson, "include_entries_summary", true);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, MaxResults);
            var maxScanGroups = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_groups", MaxScanAssets), 1, MaxScanAssets);
            var maxEntriesPerGroup = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_entries_per_group", 1000), 1, MaxEntriesPerGroup);
            var paths = CandidateAssetsByType("AddressableAssetGroup", SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, maxScanGroups, out var scannedGroups, out var scanTruncated);
            var builder = new StringBuilder();
            var matchedGroups = 0;
            var returnedResults = 0;
            var resultsTruncated = false;

            foreach (var assetPath in paths)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                var groupName = GroupName(asset, assetPath);
                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!MatchesQuery(assetPath, guid, groupName, query))
                {
                    continue;
                }

                var resultIndex = matchedGroups++;
                if (resultIndex < startIndex)
                {
                    continue;
                }

                if (returnedResults >= maxResults)
                {
                    resultsTruncated = true;
                    break;
                }

                if (returnedResults > 0)
                {
                    builder.Append(',');
                }

                builder.Append(GroupJson(assetPath, asset, includeSchemas, includeEntriesSummary, maxEntriesPerGroup));
                returnedResults++;
            }

            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_schemas\":" + JsonUtil.Bool(includeSchemas)
                + ",\"include_entries_summary\":" + JsonUtil.Bool(includeEntriesSummary)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_groups\":" + maxScanGroups
                + ",\"max_entries_per_group\":" + maxEntriesPerGroup
                + ",\"scanned_groups\":" + scannedGroups
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"matched_groups\":" + matchedGroups
                + ",\"returned_results\":" + returnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"next_start_index\":" + (resultsTruncated ? (startIndex + returnedResults).ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"groups\":[" + builder + "]"
                + ",\"warnings\":[\"Groups are read from AddressableAssetGroup serialized assets by field name; this is separate from AssetDatabase dependency graph results.\",\"This command does not build content, import, refresh, write, move, or fix assets.\"]"
                + ",\"next_valid_actions\":[\"addressables.entries.search\",\"addressables.labels.report\",\"addressables.dependencies.report\",\"addressables.build.preflight\",\"asset.risk_report\",\"asset.dependencies\"]}");
        }

        [McpCommand(
            "addressables.entries.search",
            Title = "Search Addressables entries",
            Description = "Search serialized Addressables entries by address, GUID, asset path, group, or label with bounded output and no content build operations",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = EntriesSearchSchemaJson,
            Tags = new string[] { "addressables", "content", "entries", "search", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = EntriesSearchOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EntriesSearch(McpCommandContext context)
        {
            var groupPathResult = OptionalGroupAssetPath(context.ArgsJson, out var groupAssetPath);
            if (groupPathResult != null)
            {
                return groupPathResult;
            }

            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var label = Bounded(JsonUtil.ExtractString(context.ArgsJson, "label"), 128).Trim();
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeLabels = JsonUtil.ExtractBool(context.ArgsJson, "include_labels", true);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 200), 1, MaxEntryResults);
            var maxScanGroups = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_groups", MaxScanAssets), 1, MaxScanAssets);
            var maxEntriesPerGroup = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_entries_per_group", 2000), 1, MaxEntriesPerGroup);
            int scannedGroups;
            bool scanTruncated;
            List<string> paths;
            if (string.IsNullOrEmpty(groupAssetPath))
            {
                paths = CandidateAssetsByType("AddressableAssetGroup", SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, maxScanGroups, out scannedGroups, out scanTruncated);
            }
            else
            {
                paths = SingleGroupPath(groupAssetPath, out scannedGroups, out scanTruncated);
            }
            var state = new EntrySearchState(query, label, startIndex, maxResults, includeLabels, maxEntriesPerGroup);

            foreach (var assetPath in paths)
            {
                if (state.Stop)
                {
                    break;
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                AppendGroupEntries(assetPath, asset, state);
            }

            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"group_asset_path\":" + (string.IsNullOrEmpty(groupAssetPath) ? "null" : JsonUtil.String(groupAssetPath))
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"label\":" + JsonUtil.String(label)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_labels\":" + JsonUtil.Bool(includeLabels)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_groups\":" + maxScanGroups
                + ",\"max_entries_per_group\":" + maxEntriesPerGroup
                + ",\"scanned_groups\":" + scannedGroups
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"scanned_entries\":" + state.ScannedEntries
                + ",\"entry_scan_truncated\":" + JsonUtil.Bool(state.EntryScanTruncated)
                + ",\"matched_entries\":" + state.MatchedEntries
                + ",\"returned_results\":" + state.ReturnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(state.ResultsTruncated)
                + ",\"next_start_index\":" + (state.ResultsTruncated ? (startIndex + state.ReturnedResults).ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"entries\":[" + state.ResultsJson + "]"
                + ",\"warnings\":[\"Addressable entries are read from serialized AddressableAssetGroup data by field name and are separate from AssetDatabase dependencies.\",\"This command does not build content, contact remotes, import, refresh, write, move, or fix assets.\"]"
                + ",\"next_valid_actions\":[\"asset.risk_report\",\"asset.dependencies\",\"addressables.groups.list\",\"addressables.labels.report\",\"addressables.dependencies.report\",\"addressables.build.preflight\"]}");
        }

        [McpCommand(
            "addressables.labels.report",
            Title = "Report Addressables labels",
            Description = "Report bounded Addressables label usage from serialized group entries without building content or reading AssetDatabase dependency graph",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = LabelsReportSchemaJson,
            Tags = new string[] { "addressables", "content", "labels", "report", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = LabelsReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult LabelsReport(McpCommandContext context)
        {
            var groupPathResult = OptionalGroupAssetPath(context.ArgsJson, out var groupAssetPath);
            if (groupPathResult != null)
            {
                return groupPathResult;
            }

            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeEntries = JsonUtil.ExtractBool(context.ArgsJson, "include_entries", true);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 200), 1, MaxResults);
            var maxScanGroups = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_groups", MaxScanAssets), 1, MaxScanAssets);
            var maxEntriesPerGroup = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_entries_per_group", 2000), 1, MaxEntriesPerGroup);
            var maxEntrySamplesPerLabel = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_entry_samples_per_label", 10), 0, MaxEntrySamplesPerLabel);
            int scannedGroups;
            bool scanTruncated;
            List<string> paths;
            if (string.IsNullOrEmpty(groupAssetPath))
            {
                paths = CandidateAssetsByType("AddressableAssetGroup", SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, maxScanGroups, out scannedGroups, out scanTruncated);
            }
            else
            {
                paths = SingleGroupPath(groupAssetPath, out scannedGroups, out scanTruncated);
            }

            var state = new LabelReportState(query, includeEntries, maxEntriesPerGroup, maxEntrySamplesPerLabel);
            foreach (var assetPath in paths)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                AppendGroupLabelReport(assetPath, asset, state);
            }

            var labelKeys = state.SortedLabelKeys();
            var returned = 0;
            var labelsBuilder = new StringBuilder();
            for (var index = startIndex; index < labelKeys.Count && returned < maxResults; index++)
            {
                if (returned > 0)
                {
                    labelsBuilder.Append(',');
                }

                labelsBuilder.Append(state.Labels[labelKeys[index]].ToJson(MaxGroupsPerLabel));
                returned++;
            }

            var resultsTruncated = labelKeys.Count > startIndex + returned;
            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"group_asset_path\":" + (string.IsNullOrEmpty(groupAssetPath) ? "null" : JsonUtil.String(groupAssetPath))
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_entries\":" + JsonUtil.Bool(includeEntries)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_groups\":" + maxScanGroups
                + ",\"max_entries_per_group\":" + maxEntriesPerGroup
                + ",\"max_entry_samples_per_label\":" + maxEntrySamplesPerLabel
                + ",\"report_mode\":\"addressables_serialized_labels\""
                + ",\"separate_from_assetdatabase_dependencies\":true"
                + ",\"scanned_groups\":" + scannedGroups
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"scanned_entries\":" + state.ScannedEntries
                + ",\"entry_scan_truncated\":" + JsonUtil.Bool(state.EntryScanTruncated)
                + ",\"unlabeled_entry_count\":" + state.UnlabeledEntryCount
                + ",\"matched_labels\":" + labelKeys.Count
                + ",\"returned_results\":" + returned
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"next_start_index\":" + (resultsTruncated ? (startIndex + returned).ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"labels\":[" + labelsBuilder + "]"
                + ",\"warnings\":[\"Addressables labels are read from serialized AddressableAssetGroup entry metadata and are separate from AssetDatabase dependency graph results.\",\"This command does not build content, contact remotes, import, refresh, write, move, fix assets, or modify Addressables labels.\"]"
                + ",\"next_valid_actions\":[\"addressables.entries.search\",\"addressables.groups.list\",\"addressables.dependencies.report\",\"addressables.build.preflight\",\"asset.risk_report\",\"asset.dependencies\"]}");
        }

        [McpCommand(
            "addressables.dependencies.report",
            Title = "Report Addressables dependencies",
            Description = "Report bounded Addressables entry assets and their AssetDatabase dependencies without building Addressables content",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 60000,
            SchemaJson = DependenciesReportSchemaJson,
            Tags = new string[] { "addressables", "content", "dependencies", "report", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = DependenciesReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult DependenciesReport(McpCommandContext context)
        {
            var groupPathResult = OptionalGroupAssetPath(context.ArgsJson, out var groupAssetPath);
            if (groupPathResult != null)
            {
                return groupPathResult;
            }

            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var targetAssetResult = OptionalExistingAssetPath(context.ArgsJson, "asset_path", out var targetAssetPath);
            if (targetAssetResult != null)
            {
                return targetAssetResult;
            }

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var label = Bounded(JsonUtil.ExtractString(context.ArgsJson, "label"), 128).Trim();
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal) || targetAssetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeLabels = JsonUtil.ExtractBool(context.ArgsJson, "include_labels", true);
            var includeDependencies = JsonUtil.ExtractBool(context.ArgsJson, "include_dependencies", true);
            var recursive = JsonUtil.ExtractBool(context.ArgsJson, "recursive", true);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 200), 1, MaxEntryResults);
            var maxScanGroups = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_groups", MaxScanAssets), 1, MaxScanAssets);
            var maxEntriesPerGroup = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_entries_per_group", 2000), 1, MaxEntriesPerGroup);
            var maxDependenciesPerEntry = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_dependencies_per_entry", 100), 0, MaxDependenciesPerEntry);
            int scannedGroups;
            bool scanTruncated;
            List<string> paths;
            if (string.IsNullOrEmpty(groupAssetPath))
            {
                paths = CandidateAssetsByType("AddressableAssetGroup", SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, maxScanGroups, out scannedGroups, out scanTruncated);
            }
            else
            {
                paths = SingleGroupPath(groupAssetPath, out scannedGroups, out scanTruncated);
            }

            var state = new DependencyReportState(query, label, targetAssetPath, includeLabels, includeDependencies, recursive, startIndex, maxResults, maxDependenciesPerEntry, maxEntriesPerGroup);
            foreach (var assetPath in paths)
            {
                if (state.Stop)
                {
                    break;
                }

                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                AppendGroupDependencyReport(assetPath, asset, state);
            }

            var response = "{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"group_asset_path\":" + (string.IsNullOrEmpty(groupAssetPath) ? "null" : JsonUtil.String(groupAssetPath))
                + ",\"asset_path\":" + (string.IsNullOrEmpty(targetAssetPath) ? "null" : JsonUtil.String(targetAssetPath))
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"label\":" + JsonUtil.String(label)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_labels\":" + JsonUtil.Bool(includeLabels)
                + ",\"include_dependencies\":" + JsonUtil.Bool(includeDependencies)
                + ",\"recursive\":" + JsonUtil.Bool(recursive)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_groups\":" + maxScanGroups
                + ",\"max_entries_per_group\":" + maxEntriesPerGroup
                + ",\"max_dependencies_per_entry\":" + maxDependenciesPerEntry
                + ",\"report_mode\":\"addressables_entries_assetdatabase_dependencies\""
                + ",\"addressables_reference_source\":\"serialized_addressable_group_entries\""
                + ",\"assetdatabase_dependency_source\":\"AssetDatabase.GetDependencies\""
                + ",\"separate_from_assetdatabase_dependencies\":true"
                + ",\"artifact_family\":\"addressables-reports\""
                + ",\"scanned_groups\":" + scannedGroups
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"scanned_entries\":" + state.ScannedEntries
                + ",\"entry_scan_truncated\":" + JsonUtil.Bool(state.EntryScanTruncated)
                + ",\"dependency_entries_scanned\":" + state.DependencyEntriesScanned
                + ",\"dependency_edges_scanned\":" + state.DependencyEdgesScanned
                + ",\"dependency_edges_truncated\":" + JsonUtil.Bool(state.DependencyEdgesTruncated)
                + ",\"matched_entries\":" + state.MatchedEntries
                + ",\"returned_results\":" + state.ReturnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(state.ResultsTruncated)
                + ",\"next_start_index\":" + (state.ResultsTruncated ? (startIndex + state.ReturnedResults).ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"asset_risk_report_targets\":" + state.AssetRiskTargetsJson(MaxRiskReportTargets, out var riskTargetsTruncated)
                + ",\"asset_risk_report_targets_truncated\":" + JsonUtil.Bool(riskTargetsTruncated)
                + ",\"entries\":[" + state.ResultsJson + "]"
                + ",\"warnings\":[\"Addressables entries are read from serialized AddressableAssetGroup metadata; dependency edges are read separately from AssetDatabase.GetDependencies.\",\"This command does not build Addressables content, contact remotes, import, refresh, write, move, fix assets, or modify Addressables data.\",\"Use returned asset_risk_report_targets with asset.risk_report before moving or deleting referenced assets.\""
                + (includeDependencies ? string.Empty : ",\"include_dependencies:false omits AssetDatabase dependency edges and asset_path dependency matches.\"")
                + "]"
                + ",\"next_valid_actions\":[\"asset.risk_report\",\"asset.dependencies\",\"dependency.query\",\"addressables.entries.search\",\"addressables.labels.report\",\"addressables.build.preflight\",\"job.artifact.read\",\"artifact.metadata\"]}";
            return CommandExecutionResult.Ok(WithOptionalAddressablesReportArtifact(context.ArgsJson, "addressables-dependencies", response));
        }

        [McpCommand(
            "addressables.build.preflight",
            Title = "Preflight Addressables build",
            Description = "Read-only Addressables content build risk preflight without starting builds, remotes, network operations, imports, or asset mutations",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 60000,
            SchemaJson = BuildPreflightSchemaJson,
            Tags = new string[] { "addressables", "content", "build", "preflight", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = BuildPreflightOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult BuildPreflight(McpCommandContext context)
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

            var includeEntrySamples = JsonUtil.ExtractBool(context.ArgsJson, "include_entry_samples", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 200), 1, MaxEntryResults);
            var maxScanGroups = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_groups", 1000), 1, MaxScanAssets);
            var maxEntriesPerGroup = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_entries_per_group", 2000), 1, MaxEntriesPerGroup);
            var searchRoots = SearchRoots(pathPrefix, includePackages);
            var settingsPaths = CandidateAssetsByType("AddressableAssetSettings", searchRoots, includePackages, pathPrefix, maxScanGroups, out var scannedSettingsAssets, out var settingsScanTruncated);
            var groupPaths = CandidateAssetsByType("AddressableAssetGroup", searchRoots, includePackages, pathPrefix, maxScanGroups, out var scannedGroups, out var groupScanTruncated);
            var package = FindAddressablesPackage();
            var editorApiLoaded = AddressablesEditorApiLoaded();
            var state = new BuildPreflightState(maxResults, includeEntrySamples, maxEntriesPerGroup);

            if (package == null)
            {
                state.AddRisk("error", "addressables_package_missing", "com.unity.addressables is not installed or not registered in Package Manager metadata.", string.Empty, string.Empty, -1, string.Empty, string.Empty, string.Empty, "null");
            }

            if (!editorApiLoaded)
            {
                state.AddRisk("warning", "addressables_editor_api_not_loaded", "Addressables editor API types are not loaded; serialized field inspection still runs but package import state may be incomplete.", string.Empty, string.Empty, -1, string.Empty, string.Empty, string.Empty, "null");
            }

            if (settingsPaths.Count == 0)
            {
                state.AddRisk("warning", "addressables_settings_missing", "No AddressableAssetSettings assets were found under the requested prefix.", string.Empty, string.Empty, -1, string.Empty, string.Empty, string.Empty, "null");
            }

            if (groupPaths.Count == 0)
            {
                state.AddRisk("warning", "addressables_groups_missing", "No AddressableAssetGroup assets were found under the requested prefix.", string.Empty, string.Empty, -1, string.Empty, string.Empty, string.Empty, "null");
            }

            if (settingsScanTruncated || groupScanTruncated)
            {
                state.AddRisk("warning", "addressables_scan_truncated", "Addressables asset discovery hit max_scan_groups; increase the limit before trusting build preflight completeness.", string.Empty, string.Empty, -1, string.Empty, string.Empty, string.Empty, "{\"settings_scan_truncated\":" + JsonUtil.Bool(settingsScanTruncated) + ",\"groups_scan_truncated\":" + JsonUtil.Bool(groupScanTruncated) + "}");
            }

            foreach (var assetPath in groupPaths)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                AppendGroupBuildPreflight(assetPath, asset, state);
            }

            state.AddDuplicateAddressRisks();
            var response = "{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_entry_samples\":" + JsonUtil.Bool(includeEntrySamples)
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_groups\":" + maxScanGroups
                + ",\"max_entries_per_group\":" + maxEntriesPerGroup
                + ",\"preflight_mode\":\"read_only_serialized_addressables\""
                + ",\"build_started\":false"
                + ",\"remote_operations_started\":false"
                + ",\"network_operations_started\":false"
                + ",\"package_installed\":" + JsonUtil.Bool(package != null)
                + ",\"package\":" + PackageJson(package)
                + ",\"editor_api_loaded\":" + JsonUtil.Bool(editorApiLoaded)
                + ",\"settings_asset_count\":" + settingsPaths.Count
                + ",\"group_asset_count\":" + groupPaths.Count
                + ",\"scanned_settings_assets\":" + scannedSettingsAssets
                + ",\"scanned_groups\":" + scannedGroups
                + ",\"scan_truncated\":" + JsonUtil.Bool(settingsScanTruncated || groupScanTruncated || state.EntryScanTruncated)
                + ",\"scanned_entries\":" + state.ScannedEntries
                + ",\"address_count\":" + state.AddressCount
                + ",\"duplicate_address_count\":" + state.DuplicateAddressCount
                + ",\"risk_count\":" + state.RiskCount
                + ",\"returned_risk_count\":" + state.ReturnedRiskCount
                + ",\"risks_truncated\":" + JsonUtil.Bool(state.RisksTruncated)
                + ",\"severity_counts\":" + state.SeverityCountsJson()
                + ",\"risks\":[" + state.RisksJson + "]"
                + ",\"warnings\":[\"This preflight reads package metadata and serialized Addressables settings/group/entry assets only; it never calls Addressables content builders or remote/network APIs.\",\"Use addressables.dependencies.report and asset.risk_report before moving/deleting assets referenced by Addressables entries.\"]"
                + ",\"next_valid_actions\":[\"addressables.dependencies.report\",\"addressables.groups.list\",\"addressables.entries.search\",\"asset.risk_report\",\"build.profile.list\",\"job.artifact.read\",\"artifact.metadata\"]}";
            return CommandExecutionResult.Ok(WithOptionalAddressablesReportArtifact(context.ArgsJson, "addressables-build-preflight", response));
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

        static CommandExecutionResult OptionalGroupAssetPath(string argsJson, out string groupAssetPath)
        {
            groupAssetPath = Bounded(JsonUtil.ExtractString(argsJson, "group_asset_path"), 512);
            if (string.IsNullOrEmpty(groupAssetPath))
            {
                return null;
            }

            try
            {
                groupAssetPath = PathGuard.RequireUnityAssetPath(groupAssetPath);
            }
            catch (ArgumentException)
            {
                groupAssetPath = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", "group_asset_path must be a Unity-relative path under Assets/ or Packages/");
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(groupAssetPath);
            if (!IsAddressablesType(asset, "UnityEditor.AddressableAssets.Settings.AddressableAssetGroup"))
            {
                return CommandExecutionResult.Error("invalid_field_value", "group_asset_path must reference an AddressableAssetGroup asset");
            }

            return null;
        }

        static CommandExecutionResult OptionalExistingAssetPath(string argsJson, string fieldName, out string assetPath)
        {
            assetPath = Bounded(JsonUtil.ExtractString(argsJson, fieldName), 512);
            if (string.IsNullOrEmpty(assetPath))
            {
                return null;
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(assetPath);
            }
            catch (ArgumentException)
            {
                assetPath = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " must be a Unity-relative path under Assets/ or Packages/");
            }

            if (AssetDatabase.LoadMainAssetAtPath(assetPath) == null && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                return CommandExecutionResult.Error("not_found", fieldName + " must reference an existing asset");
            }

            return null;
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
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return new[] { "Packages" };
            }

            return includePackages ? new[] { "Assets", "Packages" } : new[] { "Assets" };
        }

        static List<string> CandidateAssetsByType(string typeName, string[] searchRoots, bool includePackages, string pathPrefix, int maxScanAssets, out int scannedAssets, out bool scanTruncated)
        {
            var guids = AssetDatabase.FindAssets("t:" + typeName, searchRoots);
            var paths = new List<string>(guids.Length);
            scannedAssets = 0;
            scanTruncated = false;
            for (var index = 0; index < guids.Length; index++)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath) || (!includePackages && assetPath.StartsWith("Packages/", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!assetPath.StartsWith(pathPrefix, StringComparison.Ordinal))
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

        static List<string> SingleGroupPath(string groupAssetPath, out int scannedGroups, out bool scanTruncated)
        {
            scannedGroups = 1;
            scanTruncated = false;
            return new List<string> { groupAssetPath };
        }

        static string GroupJson(string assetPath, UnityEngine.Object asset, bool includeSchemas, bool includeEntriesSummary, int maxEntriesPerGroup)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return "{\"asset_path\":" + JsonUtil.String(assetPath)
                    + ",\"guid\":" + JsonUtil.String(guid)
                    + ",\"name\":" + JsonUtil.String(asset == null ? string.Empty : asset.name)
                    + ",\"serialized_available\":false} ";
            }

            var entriesProperty = FindFirstProperty(serializedObject, "m_Entries", "entries");
            var schemasProperty = FindFirstProperty(serializedObject, "m_Schemas", "schemas");
            var entryCount = ArraySize(entriesProperty);
            var schemaCount = ArraySize(schemasProperty);
            var entriesScanned = 0;
            var entriesTruncated = false;
            var labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var entriesSummary = "null";
            if (includeEntriesSummary)
            {
                entriesSummary = EntriesSummaryJson(entriesProperty, maxEntriesPerGroup, out entriesScanned, out entriesTruncated, out labelCounts);
            }
            var schemasTruncated = false;
            var schemasJson = includeSchemas ? ObjectReferenceArrayJson(schemasProperty, MaxSchemasPerGroup, out schemasTruncated) : "[]";

            return "{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"name\":" + JsonUtil.String(GroupName(serializedObject, assetPath))
                + ",\"type\":" + JsonUtil.String(asset == null ? string.Empty : asset.GetType().FullName ?? asset.GetType().Name)
                + ",\"serialized_available\":true"
                + ",\"read_only\":" + JsonUtil.Bool(BoolPropertyValue(serializedObject, "m_ReadOnly", "readOnly"))
                + ",\"entry_count\":" + entryCount
                + ",\"schema_count\":" + schemaCount
                + ",\"schemas\":" + schemasJson
                + ",\"schemas_truncated\":" + JsonUtil.Bool(includeSchemas && schemasTruncated)
                + ",\"entries_summary\":" + entriesSummary
                + ",\"entries_scanned\":" + entriesScanned
                + ",\"entries_truncated\":" + JsonUtil.Bool(entriesTruncated)
                + ",\"labels\":" + CountsJson(labelCounts)
                + "}";
        }

        static string EntriesSummaryJson(SerializedProperty entriesProperty, int maxEntriesPerGroup, out int entriesScanned, out bool entriesTruncated, out Dictionary<string, int> labelCounts)
        {
            entriesScanned = 0;
            entriesTruncated = false;
            labelCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            var count = ArraySize(entriesProperty);
            var limit = Math.Min(count, maxEntriesPerGroup);
            for (var index = 0; index < limit; index++)
            {
                var entry = entriesProperty.GetArrayElementAtIndex(index);
                entriesScanned++;
                foreach (var label in EntryLabels(entry))
                {
                    Increment(labelCounts, label);
                }
            }

            entriesTruncated = count > limit;
            return "{\"entry_count\":" + count
                + ",\"scanned_entries\":" + entriesScanned
                + ",\"entries_truncated\":" + JsonUtil.Bool(entriesTruncated)
                + ",\"label_count\":" + labelCounts.Count + "}";
        }

        static void AppendGroupEntries(string groupAssetPath, UnityEngine.Object asset, EntrySearchState state)
        {
            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return;
            }

            var entriesProperty = FindFirstProperty(serializedObject, "m_Entries", "entries");
            var count = ArraySize(entriesProperty);
            var limit = Math.Min(count, state.MaxEntriesPerGroup);
            var groupGuid = AssetDatabase.AssetPathToGUID(groupAssetPath);
            var groupName = GroupName(serializedObject, groupAssetPath);
            for (var index = 0; index < limit; index++)
            {
                if (state.Stop)
                {
                    break;
                }

                var entry = entriesProperty.GetArrayElementAtIndex(index);
                var entryGuid = EntryString(entry, "m_GUID", "guid", "GUID");
                var address = EntryString(entry, "m_Address", "address", "Address");
                var assetPath = string.IsNullOrEmpty(entryGuid) ? string.Empty : AssetDatabase.GUIDToAssetPath(entryGuid);
                var labels = EntryLabels(entry);
                state.AddEntry(groupAssetPath, groupGuid, groupName, index, entryGuid, assetPath, address, EntryBool(entry, "m_ReadOnly", "readOnly"), EntryBool(entry, "m_IsSubAsset", "isSubAsset"), EntryBool(entry, "m_IsInResources", "isInResources"), labels);
            }

            if (count > limit)
            {
                state.EntryScanTruncated = true;
            }
        }

        static void AppendGroupLabelReport(string groupAssetPath, UnityEngine.Object asset, LabelReportState state)
        {
            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return;
            }

            var entriesProperty = FindFirstProperty(serializedObject, "m_Entries", "entries");
            var count = ArraySize(entriesProperty);
            var limit = Math.Min(count, state.MaxEntriesPerGroup);
            var groupGuid = AssetDatabase.AssetPathToGUID(groupAssetPath);
            var groupName = GroupName(serializedObject, groupAssetPath);
            for (var index = 0; index < limit; index++)
            {
                var entry = entriesProperty.GetArrayElementAtIndex(index);
                var entryGuid = EntryString(entry, "m_GUID", "guid", "GUID");
                var address = EntryString(entry, "m_Address", "address", "Address");
                var assetPath = string.IsNullOrEmpty(entryGuid) ? string.Empty : AssetDatabase.GUIDToAssetPath(entryGuid);
                var labels = EntryLabels(entry);
                state.AddEntry(groupAssetPath, groupGuid, groupName, index, entryGuid, assetPath, address, EntryBool(entry, "m_ReadOnly", "readOnly"), EntryBool(entry, "m_IsSubAsset", "isSubAsset"), EntryBool(entry, "m_IsInResources", "isInResources"), labels);
            }

            if (count > limit)
            {
                state.EntryScanTruncated = true;
            }
        }

        static void AppendGroupDependencyReport(string groupAssetPath, UnityEngine.Object asset, DependencyReportState state)
        {
            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return;
            }

            var entriesProperty = FindFirstProperty(serializedObject, "m_Entries", "entries");
            var count = ArraySize(entriesProperty);
            var limit = Math.Min(count, state.MaxEntriesPerGroup);
            var groupGuid = AssetDatabase.AssetPathToGUID(groupAssetPath);
            var groupName = GroupName(serializedObject, groupAssetPath);
            for (var index = 0; index < limit; index++)
            {
                if (state.Stop)
                {
                    break;
                }

                var entry = entriesProperty.GetArrayElementAtIndex(index);
                var entryGuid = EntryString(entry, "m_GUID", "guid", "GUID");
                var address = EntryString(entry, "m_Address", "address", "Address");
                var assetPath = string.IsNullOrEmpty(entryGuid) ? string.Empty : AssetDatabase.GUIDToAssetPath(entryGuid);
                var labels = EntryLabels(entry);
                state.AddEntry(groupAssetPath, groupGuid, groupName, index, entryGuid, assetPath, address, EntryBool(entry, "m_ReadOnly", "readOnly"), EntryBool(entry, "m_IsSubAsset", "isSubAsset"), EntryBool(entry, "m_IsInResources", "isInResources"), labels);
            }

            if (count > limit)
            {
                state.EntryScanTruncated = true;
            }
        }

        static void AppendGroupBuildPreflight(string groupAssetPath, UnityEngine.Object asset, BuildPreflightState state)
        {
            var groupGuid = AssetDatabase.AssetPathToGUID(groupAssetPath);
            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                state.AddRisk("warning", "addressables_group_serialization_unavailable", "AddressableAssetGroup could not be inspected through SerializedObject.", groupAssetPath, string.Empty, -1, groupGuid, string.Empty, string.Empty, "null");
                return;
            }

            var groupName = GroupName(serializedObject, groupAssetPath);
            var entriesProperty = FindFirstProperty(serializedObject, "m_Entries", "entries");
            var schemasProperty = FindFirstProperty(serializedObject, "m_Schemas", "schemas");
            var entryCount = ArraySize(entriesProperty);
            var schemaCount = ArraySize(schemasProperty);
            if (schemaCount == 0)
            {
                state.AddRisk("warning", "addressables_group_has_no_schemas", "Addressable group has no serialized schemas; build/load path and bundling behavior may be incomplete.", groupAssetPath, groupName, -1, groupGuid, string.Empty, string.Empty, "null");
            }

            if (entryCount == 0)
            {
                state.AddRisk("info", "addressables_group_has_no_entries", "Addressable group has no entries.", groupAssetPath, groupName, -1, groupGuid, string.Empty, string.Empty, "null");
                return;
            }

            var limit = Math.Min(entryCount, state.MaxEntriesPerGroup);
            for (var index = 0; index < limit; index++)
            {
                var entry = entriesProperty.GetArrayElementAtIndex(index);
                var entryGuid = EntryString(entry, "m_GUID", "guid", "GUID");
                var address = EntryString(entry, "m_Address", "address", "Address");
                var assetPath = string.IsNullOrEmpty(entryGuid) ? string.Empty : AssetDatabase.GUIDToAssetPath(entryGuid);
                var readOnly = EntryBool(entry, "m_ReadOnly", "readOnly");
                var isSubAsset = EntryBool(entry, "m_IsSubAsset", "isSubAsset");
                var isInResources = EntryBool(entry, "m_IsInResources", "isInResources");
                state.ScannedEntries++;
                state.AddAddress(address, groupAssetPath, groupGuid, groupName, index, entryGuid, assetPath);

                if (string.IsNullOrEmpty(entryGuid))
                {
                    state.AddRisk("error", "addressables_entry_missing_guid", "Addressable entry has no GUID.", groupAssetPath, groupName, index, entryGuid, assetPath, address, EntryDetailsJson(readOnly, isSubAsset, isInResources));
                }
                else if (string.IsNullOrEmpty(assetPath))
                {
                    state.AddRisk("error", "addressables_entry_missing_asset", "Addressable entry GUID does not resolve to an asset path.", groupAssetPath, groupName, index, entryGuid, assetPath, address, EntryDetailsJson(readOnly, isSubAsset, isInResources));
                }

                if (string.IsNullOrEmpty(address))
                {
                    state.AddRisk("warning", "addressables_entry_empty_address", "Addressable entry has an empty address.", groupAssetPath, groupName, index, entryGuid, assetPath, address, EntryDetailsJson(readOnly, isSubAsset, isInResources));
                }

                if (isInResources)
                {
                    state.AddRisk("warning", "addressables_entry_in_resources", "Addressable entry is also marked as being in a Resources folder; verify content ownership before builds.", groupAssetPath, groupName, index, entryGuid, assetPath, address, EntryDetailsJson(readOnly, isSubAsset, isInResources));
                }
            }

            if (entryCount > limit)
            {
                state.EntryScanTruncated = true;
                state.AddRisk("warning", "addressables_group_entry_scan_truncated", "Group entry scan hit max_entries_per_group; increase the limit before trusting build preflight completeness.", groupAssetPath, groupName, -1, groupGuid, string.Empty, string.Empty, "{\"entry_count\":" + entryCount + ",\"scanned_entries\":" + limit + "}");
            }
        }

        static string EntryDetailsJson(bool readOnly, bool isSubAsset, bool isInResources)
        {
            return "{\"read_only\":" + JsonUtil.Bool(readOnly)
                + ",\"is_sub_asset\":" + JsonUtil.Bool(isSubAsset)
                + ",\"is_in_resources\":" + JsonUtil.Bool(isInResources) + "}";
        }

        static List<string> EntryLabels(SerializedProperty entry)
        {
            var labels = new List<string>();
            var labelsProperty = FindFirstRelativeProperty(entry, "m_SerializedLabels", "m_Labels", "labels");
            if (labelsProperty == null || !labelsProperty.isArray || labelsProperty.propertyType == SerializedPropertyType.String)
            {
                return labels;
            }

            var limit = Math.Min(labelsProperty.arraySize, MaxLabelsPerEntry);
            for (var index = 0; index < limit; index++)
            {
                var item = labelsProperty.GetArrayElementAtIndex(index);
                if (item != null && item.propertyType == SerializedPropertyType.String && !string.IsNullOrEmpty(item.stringValue))
                {
                    labels.Add(item.stringValue);
                }
            }

            labels.Sort(StringComparer.Ordinal);
            return labels;
        }

        static string EntryString(SerializedProperty entry, params string[] propertyPaths)
        {
            var property = FindFirstRelativeProperty(entry, propertyPaths);
            return property != null && property.propertyType == SerializedPropertyType.String ? property.stringValue ?? string.Empty : string.Empty;
        }

        static bool EntryBool(SerializedProperty entry, params string[] propertyPaths)
        {
            var property = FindFirstRelativeProperty(entry, propertyPaths);
            return property != null && property.propertyType == SerializedPropertyType.Boolean && property.boolValue;
        }

        static string GroupName(UnityEngine.Object asset, string assetPath)
        {
            if (TryCreateSerializedObject(asset, out var serializedObject))
            {
                return GroupName(serializedObject, assetPath);
            }

            return asset == null || string.IsNullOrEmpty(asset.name) ? System.IO.Path.GetFileNameWithoutExtension(assetPath) : asset.name;
        }

        static string GroupName(SerializedObject serializedObject, string assetPath)
        {
            var value = StringPropertyValue(serializedObject, "m_GroupName", "groupName", "Name");
            return string.IsNullOrEmpty(value) ? System.IO.Path.GetFileNameWithoutExtension(assetPath) : value;
        }

        static bool MatchesQuery(string assetPath, string guid, string name, string query)
        {
            return string.IsNullOrEmpty(query)
                || assetPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || guid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static PackageManagerPackageInfo FindAddressablesPackage()
        {
            foreach (var package in PackageManagerPackageInfo.GetAllRegisteredPackages())
            {
                if (string.Equals(package.name, "com.unity.addressables", StringComparison.Ordinal))
                {
                    return package;
                }
            }

            return null;
        }

        static bool AddressablesEditorApiLoaded()
        {
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                if (assembly.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetSettings", false) != null
                    || assembly.GetType("UnityEditor.AddressableAssets.Settings.AddressableAssetGroup", false) != null)
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsAddressablesType(UnityEngine.Object asset, string fullName)
        {
            return asset != null && IsTypeNamed(asset.GetType(), fullName);
        }

        static bool IsTypeNamed(Type type, string fullName)
        {
            return type != null && (type.FullName == fullName || IsSubclassNamed(type, fullName));
        }

        static bool IsSubclassNamed(Type type, string fullName)
        {
            for (var current = type.BaseType; current != null; current = current.BaseType)
            {
                if (current.FullName == fullName)
                {
                    return true;
                }
            }

            return false;
        }

        static string PackageJson(PackageManagerPackageInfo package)
        {
            if (package == null)
            {
                return "null";
            }

            return "{\"name\":" + JsonUtil.String(package.name)
                + ",\"display_name\":" + JsonUtil.String(package.displayName)
                + ",\"version\":" + JsonUtil.String(package.version)
                + ",\"source\":" + JsonUtil.String(package.source.ToString())
                + ",\"resolved_path_present\":" + JsonUtil.Bool(!string.IsNullOrEmpty(package.resolvedPath)) + "}";
        }

        static string AssetPathsJson(List<string> paths)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < paths.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var assetPath = paths[index];
                builder.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"guid\":").Append(JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath)))
                    .Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        static List<string> FirstPaths(List<string> paths, int maxItems)
        {
            var results = new List<string>();
            for (var index = 0; index < paths.Count && index < maxItems; index++)
            {
                results.Add(paths[index]);
            }

            return results;
        }

        static string ObjectReferenceArrayJson(SerializedProperty arrayProperty, int maxItems, out bool truncated)
        {
            truncated = false;
            if (arrayProperty == null || !arrayProperty.isArray || arrayProperty.propertyType == SerializedPropertyType.String)
            {
                return "[]";
            }

            var builder = new StringBuilder();
            builder.Append('[');
            var limit = Math.Min(arrayProperty.arraySize, maxItems);
            truncated = arrayProperty.arraySize > limit;
            var returned = 0;
            for (var index = 0; index < limit; index++)
            {
                var item = arrayProperty.GetArrayElementAtIndex(index);
                if (item == null || item.propertyType != SerializedPropertyType.ObjectReference || item.objectReferenceValue == null)
                {
                    continue;
                }

                if (returned > 0)
                {
                    builder.Append(',');
                }

                builder.Append(ObjectReferenceJson(item.objectReferenceValue));
                returned++;
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string ObjectReferenceJson(UnityEngine.Object value)
        {
            var path = AssetDatabase.GetAssetPath(value);
            var type = value.GetType();
            return "{\"path\":" + JsonUtil.String(path)
                + ",\"guid\":" + JsonUtil.String(string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path))
                + ",\"name\":" + JsonUtil.String(value.name)
                + ",\"type\":" + JsonUtil.String(type.FullName ?? type.Name) + "}";
        }

        static bool TryCreateSerializedObject(UnityEngine.Object asset, out SerializedObject serializedObject)
        {
            serializedObject = null;
            if (asset == null)
            {
                return false;
            }

            try
            {
                serializedObject = new SerializedObject(asset);
                serializedObject.Update();
                return true;
            }
            catch (Exception)
            {
                serializedObject = null;
                return false;
            }
        }

        static SerializedProperty FindFirstProperty(SerializedObject serializedObject, params string[] propertyPaths)
        {
            for (var index = 0; index < propertyPaths.Length; index++)
            {
                var property = serializedObject.FindProperty(propertyPaths[index]);
                if (property != null)
                {
                    return property;
                }
            }

            return null;
        }

        static SerializedProperty FindFirstRelativeProperty(SerializedProperty property, params string[] propertyPaths)
        {
            if (property == null)
            {
                return null;
            }

            for (var index = 0; index < propertyPaths.Length; index++)
            {
                var relative = property.FindPropertyRelative(propertyPaths[index]);
                if (relative != null)
                {
                    return relative;
                }
            }

            return null;
        }

        static int ArraySize(SerializedProperty property)
        {
            return property != null && property.isArray && property.propertyType != SerializedPropertyType.String ? property.arraySize : 0;
        }

        static string StringPropertyValue(SerializedObject serializedObject, params string[] propertyPaths)
        {
            var property = FindFirstProperty(serializedObject, propertyPaths);
            return property != null && property.propertyType == SerializedPropertyType.String ? property.stringValue ?? string.Empty : string.Empty;
        }

        static bool BoolPropertyValue(SerializedObject serializedObject, params string[] propertyPaths)
        {
            var property = FindFirstProperty(serializedObject, propertyPaths);
            return property != null && property.propertyType == SerializedPropertyType.Boolean && property.boolValue;
        }

        static string CountsJson(Dictionary<string, int> counts)
        {
            var keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            builder.Append('{');
            for (var index = 0; index < keys.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(keys[index])).Append(':').Append(counts[keys[index]]);
            }

            builder.Append('}');
            return builder.ToString();
        }

        static string JsonArrayStrings(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var index = 0;
            foreach (var value in values)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(value));
                index++;
            }

            builder.Append(']');
            return builder.ToString();
        }

        static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                key = "unknown";
            }

            counts.TryGetValue(key, out var value);
            counts[key] = value + 1;
        }

        static string Bounded(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength);
        }

        static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            return value > maximum ? maximum : value;
        }

        static string WithOptionalAddressablesReportArtifact(string argsJson, string reportKind, string response)
        {
            if (!JsonUtil.ExtractBool(argsJson, "write_artifact", false))
            {
                return response;
            }

            var artifactPath = AddressablesReportArtifactRoot + "/" + reportKind + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
            try
            {
                var responseWithArtifact = AddJsonObjectFields(response, ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                    + ",\"artifact_refs\":[" + JsonUtil.String(artifactPath) + "]"
                    + ",\"readable_with\":\"job.artifact.read\"");
                WriteAddressablesReportArtifact(artifactPath, responseWithArtifact);
                return responseWithArtifact;
            }
            catch (Exception exception)
            {
                return AddJsonObjectFields(response, ",\"artifact_write_error\":" + JsonUtil.String(exception.GetType().Name));
            }
        }

        static void WriteAddressablesReportArtifact(string artifactPath, string content)
        {
            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, AddressablesReportArtifactRoot.Replace('/', Path.DirectorySeparatorChar)));
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, artifactRoot))
            {
                throw new ArgumentException("artifact path escaped Addressables report root");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

        static string AddJsonObjectFields(string json, string fields)
        {
            return !string.IsNullOrEmpty(json) && json.EndsWith("}", StringComparison.Ordinal)
                ? json.Substring(0, json.Length - 1) + fields + "}"
                : json;
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var normalizedRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedPath, normalizedRoot, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || normalizedPath.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        sealed class EntrySearchState
        {
            readonly string Query;
            readonly string Label;
            readonly int StartIndex;
            readonly int MaxResults;
            readonly bool IncludeLabels;

            public EntrySearchState(string query, string label, int startIndex, int maxResults, bool includeLabels, int maxEntriesPerGroup)
            {
                Query = query;
                Label = label;
                StartIndex = startIndex;
                MaxResults = maxResults;
                IncludeLabels = includeLabels;
                MaxEntriesPerGroup = maxEntriesPerGroup;
            }

            public int MaxEntriesPerGroup { get; }
            public int ScannedEntries { get; private set; }
            public int MatchedEntries { get; private set; }
            public int ReturnedResults { get; private set; }
            public bool ResultsTruncated { get; private set; }
            public bool EntryScanTruncated { get; set; }
            public bool Stop { get; private set; }
            public StringBuilder ResultsJson { get; } = new StringBuilder();

            public void AddEntry(string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath, string address, bool readOnly, bool isSubAsset, bool isInResources, List<string> labels)
            {
                ScannedEntries++;
                if (!Matches(groupAssetPath, groupName, entryGuid, assetPath, address, labels))
                {
                    return;
                }

                var matchedIndex = MatchedEntries++;
                if (matchedIndex < StartIndex)
                {
                    return;
                }

                if (ReturnedResults >= MaxResults)
                {
                    ResultsTruncated = true;
                    Stop = true;
                    return;
                }

                if (ReturnedResults > 0)
                {
                    ResultsJson.Append(',');
                }

                ResultsJson.Append("{\"index\":").Append(matchedIndex)
                    .Append(",\"group_asset_path\":").Append(JsonUtil.String(groupAssetPath))
                    .Append(",\"group_guid\":").Append(JsonUtil.String(groupGuid))
                    .Append(",\"group_name\":").Append(JsonUtil.String(groupName))
                    .Append(",\"entry_index\":").Append(entryIndex)
                    .Append(",\"guid\":").Append(JsonUtil.String(entryGuid))
                    .Append(",\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"address\":").Append(JsonUtil.String(address))
                    .Append(",\"read_only\":").Append(JsonUtil.Bool(readOnly))
                    .Append(",\"is_sub_asset\":").Append(JsonUtil.Bool(isSubAsset))
                    .Append(",\"is_in_resources\":").Append(JsonUtil.Bool(isInResources))
                    .Append(",\"labels\":").Append(IncludeLabels ? JsonArrayStrings(labels) : "[]")
                    .Append('}');
                ReturnedResults++;
            }

            bool Matches(string groupAssetPath, string groupName, string entryGuid, string assetPath, string address, List<string> labels)
            {
                if (!string.IsNullOrEmpty(Label))
                {
                    var labelMatched = false;
                    foreach (var item in labels)
                    {
                        if (string.Equals(item, Label, StringComparison.OrdinalIgnoreCase))
                        {
                            labelMatched = true;
                            break;
                        }
                    }

                    if (!labelMatched)
                    {
                        return false;
                    }
                }

                if (string.IsNullOrEmpty(Query))
                {
                    return true;
                }

                if (groupAssetPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || groupName.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || entryGuid.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || assetPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || address.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                foreach (var item in labels)
                {
                    if (item.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        sealed class BuildPreflightState
        {
            readonly int MaxResults;
            readonly bool IncludeEntrySamples;
            readonly Dictionary<string, PreflightAddressSummary> Addresses = new Dictionary<string, PreflightAddressSummary>(StringComparer.Ordinal);

            public BuildPreflightState(int maxResults, bool includeEntrySamples, int maxEntriesPerGroup)
            {
                MaxResults = maxResults;
                IncludeEntrySamples = includeEntrySamples;
                MaxEntriesPerGroup = maxEntriesPerGroup;
            }

            public int MaxEntriesPerGroup { get; }
            public int ScannedEntries { get; set; }
            public int RiskCount { get; private set; }
            public int ReturnedRiskCount { get; private set; }
            public int ErrorCount { get; private set; }
            public int WarningCount { get; private set; }
            public int InfoCount { get; private set; }
            public int DuplicateAddressCount { get; private set; }
            public int AddressCount => Addresses.Count;
            public bool EntryScanTruncated { get; set; }
            public bool RisksTruncated { get; private set; }
            public StringBuilder RisksJson { get; } = new StringBuilder();

            public void AddAddress(string address, string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath)
            {
                if (string.IsNullOrEmpty(address))
                {
                    return;
                }

                if (!Addresses.TryGetValue(address, out var summary))
                {
                    summary = new PreflightAddressSummary(address);
                    Addresses[address] = summary;
                }

                summary.Add(groupAssetPath, groupGuid, groupName, entryIndex, entryGuid, assetPath);
            }

            public void AddDuplicateAddressRisks()
            {
                var keys = new List<string>(Addresses.Keys);
                keys.Sort(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    var summary = Addresses[key];
                    if (summary.Count <= 1)
                    {
                        continue;
                    }

                    DuplicateAddressCount++;
                    AddRisk("warning", "addressables_duplicate_address", "Multiple Addressable entries share the same address.", summary.FirstGroupAssetPath, summary.FirstGroupName, summary.FirstEntryIndex, summary.FirstEntryGuid, summary.FirstAssetPath, key, summary.ToDetailsJson(IncludeEntrySamples));
                }
            }

            public void AddRisk(string severity, string code, string message, string groupAssetPath, string groupName, int entryIndex, string guid, string assetPath, string address, string detailsJson)
            {
                RiskCount++;
                if (string.Equals(severity, "error", StringComparison.Ordinal))
                {
                    ErrorCount++;
                }
                else if (string.Equals(severity, "warning", StringComparison.Ordinal))
                {
                    WarningCount++;
                }
                else
                {
                    InfoCount++;
                }

                if (ReturnedRiskCount >= MaxResults)
                {
                    RisksTruncated = true;
                    return;
                }

                if (ReturnedRiskCount > 0)
                {
                    RisksJson.Append(',');
                }

                RisksJson.Append("{\"severity\":").Append(JsonUtil.String(severity))
                    .Append(",\"code\":").Append(JsonUtil.String(code))
                    .Append(",\"message\":").Append(JsonUtil.String(message))
                    .Append(",\"group_asset_path\":").Append(JsonUtil.String(groupAssetPath))
                    .Append(",\"group_name\":").Append(JsonUtil.String(groupName))
                    .Append(",\"entry_index\":").Append(entryIndex)
                    .Append(",\"guid\":").Append(JsonUtil.String(guid))
                    .Append(",\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"address\":").Append(JsonUtil.String(address))
                    .Append(",\"details\":").Append(string.IsNullOrEmpty(detailsJson) ? "null" : detailsJson)
                    .Append('}');
                ReturnedRiskCount++;
            }

            public string SeverityCountsJson()
            {
                return "{\"error\":" + ErrorCount
                    + ",\"warning\":" + WarningCount
                    + ",\"info\":" + InfoCount + "}";
            }
        }

        sealed class PreflightAddressSummary
        {
            readonly List<PreflightAddressOccurrence> Samples = new List<PreflightAddressOccurrence>();
            const int MaxSamples = 5;

            public PreflightAddressSummary(string address)
            {
                Address = address;
            }

            public string Address { get; }
            public int Count { get; private set; }
            public string FirstGroupAssetPath => Samples.Count == 0 ? string.Empty : Samples[0].GroupAssetPath;
            public string FirstGroupName => Samples.Count == 0 ? string.Empty : Samples[0].GroupName;
            public int FirstEntryIndex => Samples.Count == 0 ? -1 : Samples[0].EntryIndex;
            public string FirstEntryGuid => Samples.Count == 0 ? string.Empty : Samples[0].EntryGuid;
            public string FirstAssetPath => Samples.Count == 0 ? string.Empty : Samples[0].AssetPath;

            public void Add(string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath)
            {
                Count++;
                if (Samples.Count < MaxSamples)
                {
                    Samples.Add(new PreflightAddressOccurrence(groupAssetPath, groupGuid, groupName, entryIndex, entryGuid, assetPath));
                }
            }

            public string ToDetailsJson(bool includeSamples)
            {
                var builder = new StringBuilder();
                builder.Append("{\"address\":").Append(JsonUtil.String(Address))
                    .Append(",\"duplicate_count\":").Append(Count)
                    .Append(",\"samples_truncated\":").Append(JsonUtil.Bool(Count > Samples.Count))
                    .Append(",\"sample_entries\":");
                if (!includeSamples)
                {
                    builder.Append("[]}");
                    return builder.ToString();
                }

                builder.Append('[');
                for (var index = 0; index < Samples.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(Samples[index].ToJson());
                }

                builder.Append("]}");
                return builder.ToString();
            }
        }

        sealed class PreflightAddressOccurrence
        {
            public PreflightAddressOccurrence(string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath)
            {
                GroupAssetPath = groupAssetPath;
                GroupGuid = groupGuid;
                GroupName = groupName;
                EntryIndex = entryIndex;
                EntryGuid = entryGuid;
                AssetPath = assetPath;
            }

            public string GroupAssetPath { get; }
            public string GroupGuid { get; }
            public string GroupName { get; }
            public int EntryIndex { get; }
            public string EntryGuid { get; }
            public string AssetPath { get; }

            public string ToJson()
            {
                return "{\"group_asset_path\":" + JsonUtil.String(GroupAssetPath)
                    + ",\"group_guid\":" + JsonUtil.String(GroupGuid)
                    + ",\"group_name\":" + JsonUtil.String(GroupName)
                    + ",\"entry_index\":" + EntryIndex
                    + ",\"guid\":" + JsonUtil.String(EntryGuid)
                    + ",\"asset_path\":" + JsonUtil.String(AssetPath) + "}";
            }
        }

        sealed class DependencyReportState
        {
            readonly string Query;
            readonly string Label;
            readonly string TargetAssetPath;
            readonly bool IncludeLabels;
            readonly bool IncludeDependencies;
            readonly bool Recursive;
            readonly int StartIndex;
            readonly int MaxResults;
            readonly int MaxDependenciesPerEntry;
            readonly HashSet<string> AssetRiskTargets = new HashSet<string>(StringComparer.Ordinal);

            public DependencyReportState(string query, string label, string targetAssetPath, bool includeLabels, bool includeDependencies, bool recursive, int startIndex, int maxResults, int maxDependenciesPerEntry, int maxEntriesPerGroup)
            {
                Query = query;
                Label = label;
                TargetAssetPath = targetAssetPath;
                IncludeLabels = includeLabels;
                IncludeDependencies = includeDependencies;
                Recursive = recursive;
                StartIndex = startIndex;
                MaxResults = maxResults;
                MaxDependenciesPerEntry = maxDependenciesPerEntry;
                MaxEntriesPerGroup = maxEntriesPerGroup;
            }

            public int MaxEntriesPerGroup { get; }
            public int ScannedEntries { get; private set; }
            public int DependencyEntriesScanned { get; private set; }
            public int DependencyEdgesScanned { get; private set; }
            public int MatchedEntries { get; private set; }
            public int ReturnedResults { get; private set; }
            public bool ResultsTruncated { get; private set; }
            public bool EntryScanTruncated { get; set; }
            public bool DependencyEdgesTruncated { get; private set; }
            public bool Stop { get; private set; }
            public StringBuilder ResultsJson { get; } = new StringBuilder();

            public void AddEntry(string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath, string address, bool readOnly, bool isSubAsset, bool isInResources, List<string> labels)
            {
                ScannedEntries++;
                if (!MatchesLabel(labels))
                {
                    return;
                }

                var dependenciesJson = new StringBuilder();
                var dependencyCount = 0;
                var returnedDependencyCount = 0;
                var dependenciesTruncated = false;
                var dependencyQueryMatched = false;
                var targetDependencyMatched = false;
                if (IncludeDependencies && !string.IsNullOrEmpty(assetPath))
                {
                    DependencyEntriesScanned++;
                    var dependencies = AssetDatabase.GetDependencies(assetPath, Recursive) ?? new string[0];
                    Array.Sort(dependencies, StringComparer.Ordinal);
                    for (var index = 0; index < dependencies.Length; index++)
                    {
                        var dependencyPath = dependencies[index];
                        if (string.Equals(dependencyPath, assetPath, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        dependencyCount++;
                        DependencyEdgesScanned++;
                        if (!string.IsNullOrEmpty(TargetAssetPath) && string.Equals(dependencyPath, TargetAssetPath, StringComparison.Ordinal))
                        {
                            targetDependencyMatched = true;
                        }

                        if (!string.IsNullOrEmpty(Query) && dependencyPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            dependencyQueryMatched = true;
                        }

                        if (returnedDependencyCount >= MaxDependenciesPerEntry)
                        {
                            dependenciesTruncated = true;
                            continue;
                        }

                        if (returnedDependencyCount > 0)
                        {
                            dependenciesJson.Append(',');
                        }

                        dependenciesJson.Append("{\"asset_path\":").Append(JsonUtil.String(dependencyPath))
                            .Append(",\"guid\":").Append(JsonUtil.String(AssetDatabase.AssetPathToGUID(dependencyPath)))
                            .Append(",\"source\":\"AssetDatabase.GetDependencies\"}");
                        returnedDependencyCount++;
                    }
                }

                if (dependenciesTruncated)
                {
                    DependencyEdgesTruncated = true;
                }

                var metadataQueryMatched = MatchesMetadataQuery(groupAssetPath, groupName, entryGuid, assetPath, address, labels);
                if (!string.IsNullOrEmpty(Query) && !metadataQueryMatched && !dependencyQueryMatched)
                {
                    return;
                }

                var targetRelation = string.Empty;
                if (!string.IsNullOrEmpty(TargetAssetPath))
                {
                    if (string.Equals(assetPath, TargetAssetPath, StringComparison.Ordinal))
                    {
                        targetRelation = "addressable_entry_asset";
                    }
                    else if (targetDependencyMatched)
                    {
                        targetRelation = "assetdatabase_dependency";
                    }
                    else
                    {
                        return;
                    }

                    AssetRiskTargets.Add(TargetAssetPath);
                }

                if (!string.IsNullOrEmpty(assetPath))
                {
                    AssetRiskTargets.Add(assetPath);
                }

                var matchedIndex = MatchedEntries++;
                if (matchedIndex < StartIndex)
                {
                    return;
                }

                if (ReturnedResults >= MaxResults)
                {
                    ResultsTruncated = true;
                    Stop = true;
                    return;
                }

                if (ReturnedResults > 0)
                {
                    ResultsJson.Append(',');
                }

                ResultsJson.Append("{\"index\":").Append(matchedIndex)
                    .Append(",\"group_asset_path\":").Append(JsonUtil.String(groupAssetPath))
                    .Append(",\"group_guid\":").Append(JsonUtil.String(groupGuid))
                    .Append(",\"group_name\":").Append(JsonUtil.String(groupName))
                    .Append(",\"entry_index\":").Append(entryIndex)
                    .Append(",\"guid\":").Append(JsonUtil.String(entryGuid))
                    .Append(",\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"address\":").Append(JsonUtil.String(address))
                    .Append(",\"read_only\":").Append(JsonUtil.Bool(readOnly))
                    .Append(",\"is_sub_asset\":").Append(JsonUtil.Bool(isSubAsset))
                    .Append(",\"is_in_resources\":").Append(JsonUtil.Bool(isInResources))
                    .Append(",\"labels\":").Append(IncludeLabels ? JsonArrayStrings(labels) : "[]")
                    .Append(",\"target_relation\":").Append(string.IsNullOrEmpty(targetRelation) ? "null" : JsonUtil.String(targetRelation))
                    .Append(",\"metadata_query_matched\":").Append(JsonUtil.Bool(metadataQueryMatched))
                    .Append(",\"dependency_query_matched\":").Append(JsonUtil.Bool(dependencyQueryMatched))
                    .Append(",\"dependency_count\":").Append(dependencyCount)
                    .Append(",\"returned_dependency_count\":").Append(returnedDependencyCount)
                    .Append(",\"dependencies_truncated\":").Append(JsonUtil.Bool(dependenciesTruncated))
                    .Append(",\"dependencies\":[").Append(dependenciesJson).Append(']')
                    .Append(",\"asset_risk_report_arg\":").Append(string.IsNullOrEmpty(assetPath) ? "null" : "{\"asset_path\":" + JsonUtil.String(assetPath) + "}")
                    .Append('}');
                ReturnedResults++;
            }

            public string AssetRiskTargetsJson(int maxTargets, out bool truncated)
            {
                var values = new List<string>(AssetRiskTargets);
                values.Sort(StringComparer.Ordinal);
                var limit = Math.Min(values.Count, maxTargets);
                truncated = values.Count > limit;
                var builder = new StringBuilder();
                builder.Append('[');
                for (var index = 0; index < limit; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    var assetPath = values[index];
                    builder.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                        .Append(",\"guid\":").Append(JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath)))
                        .Append(",\"command\":\"asset.risk_report\"")
                        .Append(",\"args\":{\"asset_path\":").Append(JsonUtil.String(assetPath)).Append("}}");
                }

                builder.Append(']');
                return builder.ToString();
            }

            bool MatchesLabel(List<string> labels)
            {
                if (string.IsNullOrEmpty(Label))
                {
                    return true;
                }

                foreach (var item in labels)
                {
                    if (string.Equals(item, Label, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            bool MatchesMetadataQuery(string groupAssetPath, string groupName, string entryGuid, string assetPath, string address, List<string> labels)
            {
                if (string.IsNullOrEmpty(Query))
                {
                    return true;
                }

                if (groupAssetPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || groupName.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || entryGuid.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || assetPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || address.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                foreach (var item in labels)
                {
                    if (item.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }

        sealed class LabelReportState
        {
            readonly string Query;
            readonly bool IncludeEntries;
            readonly int MaxEntrySamplesPerLabel;

            public LabelReportState(string query, bool includeEntries, int maxEntriesPerGroup, int maxEntrySamplesPerLabel)
            {
                Query = query;
                IncludeEntries = includeEntries;
                MaxEntriesPerGroup = maxEntriesPerGroup;
                MaxEntrySamplesPerLabel = maxEntrySamplesPerLabel;
            }

            public int MaxEntriesPerGroup { get; }
            public int ScannedEntries { get; private set; }
            public int UnlabeledEntryCount { get; private set; }
            public bool EntryScanTruncated { get; set; }
            public Dictionary<string, LabelSummary> Labels { get; } = new Dictionary<string, LabelSummary>(StringComparer.Ordinal);

            public void AddEntry(string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath, string address, bool readOnly, bool isSubAsset, bool isInResources, List<string> labels)
            {
                ScannedEntries++;
                if (labels.Count == 0)
                {
                    if (Matches(groupAssetPath, groupName, entryGuid, assetPath, address, string.Empty))
                    {
                        UnlabeledEntryCount++;
                    }

                    return;
                }

                foreach (var label in labels)
                {
                    if (!Matches(groupAssetPath, groupName, entryGuid, assetPath, address, label))
                    {
                        continue;
                    }

                    if (!Labels.TryGetValue(label, out var summary))
                    {
                        summary = new LabelSummary(label, IncludeEntries, MaxEntrySamplesPerLabel);
                        Labels[label] = summary;
                    }

                    summary.AddEntry(groupAssetPath, groupGuid, groupName, entryIndex, entryGuid, assetPath, address, readOnly, isSubAsset, isInResources);
                }
            }

            public List<string> SortedLabelKeys()
            {
                var keys = new List<string>(Labels.Keys);
                keys.Sort(StringComparer.Ordinal);
                return keys;
            }

            bool Matches(string groupAssetPath, string groupName, string entryGuid, string assetPath, string address, string label)
            {
                return string.IsNullOrEmpty(Query)
                    || label.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || groupAssetPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || groupName.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || entryGuid.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || assetPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || address.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0;
            }
        }

        sealed class LabelSummary
        {
            readonly bool IncludeEntries;
            readonly int MaxEntrySamples;
            readonly Dictionary<string, LabelGroupSummary> Groups = new Dictionary<string, LabelGroupSummary>(StringComparer.Ordinal);
            readonly List<LabelEntrySample> EntrySamples = new List<LabelEntrySample>();

            public LabelSummary(string label, bool includeEntries, int maxEntrySamples)
            {
                Label = label;
                IncludeEntries = includeEntries;
                MaxEntrySamples = maxEntrySamples;
            }

            public string Label { get; }
            public int EntryCount { get; private set; }
            public int ReadOnlyEntryCount { get; private set; }
            public int SubAssetEntryCount { get; private set; }
            public int ResourcesEntryCount { get; private set; }
            public bool EntriesTruncated { get; private set; }

            public void AddEntry(string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath, string address, bool readOnly, bool isSubAsset, bool isInResources)
            {
                EntryCount++;
                if (readOnly)
                {
                    ReadOnlyEntryCount++;
                }

                if (isSubAsset)
                {
                    SubAssetEntryCount++;
                }

                if (isInResources)
                {
                    ResourcesEntryCount++;
                }

                if (!Groups.TryGetValue(groupAssetPath, out var group))
                {
                    group = new LabelGroupSummary(groupAssetPath, groupGuid, groupName);
                    Groups[groupAssetPath] = group;
                }

                group.EntryCount++;

                if (!IncludeEntries)
                {
                    return;
                }

                if (EntrySamples.Count >= MaxEntrySamples)
                {
                    EntriesTruncated = true;
                    return;
                }

                EntrySamples.Add(new LabelEntrySample(groupAssetPath, groupGuid, groupName, entryIndex, entryGuid, assetPath, address, readOnly, isSubAsset, isInResources));
            }

            public string ToJson(int maxGroups)
            {
                return "{\"label\":" + JsonUtil.String(Label)
                    + ",\"entry_count\":" + EntryCount
                    + ",\"group_count\":" + Groups.Count
                    + ",\"read_only_entry_count\":" + ReadOnlyEntryCount
                    + ",\"sub_asset_entry_count\":" + SubAssetEntryCount
                    + ",\"resources_entry_count\":" + ResourcesEntryCount
                    + ",\"groups\":" + GroupsJson(maxGroups, out var groupsTruncated)
                    + ",\"groups_truncated\":" + JsonUtil.Bool(groupsTruncated)
                    + ",\"sample_entries\":" + EntrySamplesJson()
                    + ",\"entries_truncated\":" + JsonUtil.Bool(EntriesTruncated)
                    + "}";
            }

            string GroupsJson(int maxGroups, out bool groupsTruncated)
            {
                var keys = new List<string>(Groups.Keys);
                keys.Sort(StringComparer.Ordinal);
                var limit = Math.Min(keys.Count, maxGroups);
                groupsTruncated = keys.Count > limit;
                var builder = new StringBuilder();
                builder.Append('[');
                for (var index = 0; index < limit; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(Groups[keys[index]].ToJson());
                }

                builder.Append(']');
                return builder.ToString();
            }

            string EntrySamplesJson()
            {
                var builder = new StringBuilder();
                builder.Append('[');
                for (var index = 0; index < EntrySamples.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(EntrySamples[index].ToJson());
                }

                builder.Append(']');
                return builder.ToString();
            }
        }

        sealed class LabelGroupSummary
        {
            public LabelGroupSummary(string groupAssetPath, string groupGuid, string groupName)
            {
                GroupAssetPath = groupAssetPath;
                GroupGuid = groupGuid;
                GroupName = groupName;
            }

            public string GroupAssetPath { get; }
            public string GroupGuid { get; }
            public string GroupName { get; }
            public int EntryCount { get; set; }

            public string ToJson()
            {
                return "{\"group_asset_path\":" + JsonUtil.String(GroupAssetPath)
                    + ",\"group_guid\":" + JsonUtil.String(GroupGuid)
                    + ",\"group_name\":" + JsonUtil.String(GroupName)
                    + ",\"entry_count\":" + EntryCount + "}";
            }
        }

        sealed class LabelEntrySample
        {
            readonly string GroupAssetPath;
            readonly string GroupGuid;
            readonly string GroupName;
            readonly int EntryIndex;
            readonly string EntryGuid;
            readonly string AssetPath;
            readonly string Address;
            readonly bool ReadOnly;
            readonly bool IsSubAsset;
            readonly bool IsInResources;

            public LabelEntrySample(string groupAssetPath, string groupGuid, string groupName, int entryIndex, string entryGuid, string assetPath, string address, bool readOnly, bool isSubAsset, bool isInResources)
            {
                GroupAssetPath = groupAssetPath;
                GroupGuid = groupGuid;
                GroupName = groupName;
                EntryIndex = entryIndex;
                EntryGuid = entryGuid;
                AssetPath = assetPath;
                Address = address;
                ReadOnly = readOnly;
                IsSubAsset = isSubAsset;
                IsInResources = isInResources;
            }

            public string ToJson()
            {
                return "{\"group_asset_path\":" + JsonUtil.String(GroupAssetPath)
                    + ",\"group_guid\":" + JsonUtil.String(GroupGuid)
                    + ",\"group_name\":" + JsonUtil.String(GroupName)
                    + ",\"entry_index\":" + EntryIndex
                    + ",\"guid\":" + JsonUtil.String(EntryGuid)
                    + ",\"asset_path\":" + JsonUtil.String(AssetPath)
                    + ",\"address\":" + JsonUtil.String(Address)
                    + ",\"read_only\":" + JsonUtil.Bool(ReadOnly)
                    + ",\"is_sub_asset\":" + JsonUtil.Bool(IsSubAsset)
                    + ",\"is_in_resources\":" + JsonUtil.Bool(IsInResources)
                    + "}";
            }
        }
    }
}
