using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class AssetIndexCommands
    {
        const string FeatureFlag = "asset_index";
        const string SemanticFeatureFlag = "asset_semantic_index";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string ListSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\"},\"include_packages\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"additionalProperties\":false}";
        const string SearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"include_packages\":{\"type\":\"boolean\"}},\"required\":[\"query\"],\"additionalProperties\":false}";
        const string SemanticSearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_labels\":{\"type\":\"boolean\"},\"include_importer\":{\"type\":\"boolean\"},\"include_script_symbols\":{\"type\":\"boolean\"},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"query\"],\"additionalProperties\":false}";
        const string AssetInfoSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"include_labels\":{\"type\":\"boolean\"},\"include_importer\":{\"type\":\"boolean\"},\"include_dependencies\":{\"type\":\"boolean\"},\"recursive_dependencies\":{\"type\":\"boolean\"},\"max_dependencies\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string SerializedInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"max_depth\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":6},\"max_array_elements\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20},\"max_chars\":{\"type\":\"integer\",\"minimum\":256,\"maximum\":8192}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string AssetTargetSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"guid\":{\"type\":\"string\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"write_artifact\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string AssetPathSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"recursive\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"write_artifact\":{\"type\":\"boolean\"}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string DependencyQuerySchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"guid\":{\"type\":\"string\"},\"recursive\":{\"type\":\"boolean\"},\"include_outgoing\":{\"type\":\"boolean\"},\"include_incoming\":{\"type\":\"boolean\"},\"include_yaml_guid_refs\":{\"type\":\"boolean\"},\"include_script_context\":{\"type\":\"boolean\"},\"include_assembly_references\":{\"type\":\"boolean\"},\"max_edges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_yaml_references\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"write_artifact\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string AssetRelocationPreflightSchemaJson = "{\"type\":\"object\",\"properties\":{\"source_path\":{\"type\":\"string\",\"maxLength\":512},\"destination_path\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_assetdatabase\":{\"type\":\"boolean\"},\"include_yaml_guid_refs\":{\"type\":\"boolean\"},\"include_text_includes\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"write_artifact\":{\"type\":\"boolean\"}},\"required\":[\"source_path\",\"destination_path\"],\"additionalProperties\":false}";
        const string TextReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"max_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":65536}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string TextFindSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"query\":{\"type\":\"string\"},\"match_mode\":{\"type\":\"string\",\"enum\":[\"literal\",\"regex\"]},\"case_sensitive\":{\"type\":\"boolean\"},\"context_lines\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":5},\"regex_timeout_ms\":{\"type\":\"integer\",\"minimum\":10,\"maximum\":1000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576}},\"required\":[\"asset_path\",\"query\"],\"additionalProperties\":false}";
        const string TextSearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\"},\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"match_mode\":{\"type\":\"string\",\"enum\":[\"literal\",\"regex\"]},\"name_regex\":{\"type\":\"string\",\"maxLength\":256},\"case_sensitive\":{\"type\":\"boolean\"},\"include_packages\":{\"type\":\"boolean\"},\"context_lines\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":5},\"regex_timeout_ms\":{\"type\":\"integer\",\"minimum\":10,\"maximum\":1000},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"max_files\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000}},\"required\":[\"query\"],\"additionalProperties\":false}";
        const string GuidSchemaJson = "{\"type\":\"object\",\"properties\":{\"guid\":{\"type\":\"string\"}},\"required\":[\"guid\"],\"additionalProperties\":false}";
        const string RiskReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\"},\"write_artifact\":{\"type\":\"boolean\"}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string ListOutputLimitJson = "{\"max_results\":1000}";
        const string SemanticSearchOutputLimitJson = "{\"max_results\":200,\"max_scan_assets\":20000,\"local_only\":true,\"embedding_provider\":\"none\"}";
        const string AssetReportListOutputLimitJson = "{\"max_results\":1000,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/asset-reports\"}";
        const string SingleAssetOutputLimitJson = "{\"max_assets\":1}";
        const string AssetRiskReportOutputLimitJson = "{\"max_assets\":1,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/asset-reports\"}";
        const string AssetInfoOutputLimitJson = "{\"max_assets\":1,\"max_dependencies\":200}";
        const string SerializedInspectOutputLimitJson = "{\"max_assets\":1,\"max_depth\":6,\"max_array_elements\":20,\"max_chars\":8192}";
        const string DependencyQueryOutputLimitJson = "{\"max_edges\":1000,\"max_yaml_references\":200,\"max_script_types\":50,\"max_script_relationships\":200,\"max_using_namespaces\":100,\"max_assembly_references\":200,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/asset-reports\"}";
        const string AssetRelocationPreflightOutputLimitJson = "{\"max_results\":1000,\"max_scan_assets\":100000,\"max_scan_bytes\":1048576,\"dry_run_only\":true,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/asset-reports\"}";
        const string TextReadOutputLimitJson = "{\"max_bytes\":65536}";
        const string TextFindOutputLimitJson = "{\"max_results\":200,\"max_scan_bytes\":1048576,\"max_context_lines\":5,\"regex_timeout_ms\":1000}";
        const string TextSearchOutputLimitJson = "{\"max_results\":200,\"max_scan_bytes\":1048576,\"max_files\":10000,\"max_context_lines\":5,\"regex_timeout_ms\":1000}";
        const string CacheFileName = "asset-index.tsv";
        const string DefaultCacheRelativeDirectory = "Library/SGGUnityMcp";
        const string CacheHeader = "# sgg-unity-mcp asset index cache v1";
        const string SettingsAssetIndexCacheDir = "asset_index_cache_dir";
        const string EditorPrefsCacheDirKey = "SGG.UnityMCP.AssetIndexCacheDir";
        const int DefaultSerializedMaxDepth = 6;
        const int DefaultSerializedMaxArrayElements = 20;
        const int DefaultSerializedMaxChars = 8192;
        const int MaxSerializedPropertyPathLength = 512;
        const int MaxSerializedStringChars = 512;
        const int MaxDependencyScriptBytes = 65536;
        const int MaxDependencyAsmdefBytes = 32768;
        const int MaxDependencyDeclaredTypes = 50;
        const int MaxDependencyScriptRelationships = 200;
        const int MaxDependencyUsingNamespaces = 100;
        const int MaxDependencyAsmdefReferences = 200;
        const int MaxAsmdefReferenceScan = 500;
        const int MaxRelocationScanAssets = 100000;
        const int MaxRelocationScanBytes = 1048576;
        const int MaxSemanticQueryTokens = 12;
        const int MaxSemanticScanAssets = 20000;
        const int MaxSemanticScriptBytes = 65536;
        const string AssetReportArtifactRoot = "Library/SGGUnityMcp/job-artifacts/asset-reports";

        static readonly object s_lock = new object();
        static readonly List<AssetIndexEntry> s_entries = new List<AssetIndexEntry>();
        static readonly Dictionary<string, AssetIndexEntry> s_byPath = new Dictionary<string, AssetIndexEntry>(StringComparer.Ordinal);
        static readonly Dictionary<string, AssetIndexEntry> s_byGuid = new Dictionary<string, AssetIndexEntry>(StringComparer.OrdinalIgnoreCase);
        static DateTime s_builtAtUtc = DateTime.MinValue;
        static bool s_loadedFromCache;
        static string s_lastCachePath = string.Empty;

        [McpCommand(
            "asset.index.status",
            Title = "Asset index status",
            Description = "Read Unity asset index status",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "asset", "index" },
            Capability = "asset_index",
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetIndexStatus(McpCommandContext context)
        {
            TryLoadCache(context);
            return CommandExecutionResult.Ok(StatusJson(context, false));
        }

        [McpCommand(
            "asset.index.rebuild",
            Title = "Rebuild asset index",
            Description = "Rebuild the in-memory Unity asset index",
            Risk = "read",
            Idempotency = "idempotent",
            TimeoutMs = 60000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "asset", "index" },
            Capability = "asset_index",
            ResultShape = "status",
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetIndexRebuild(McpCommandContext context)
        {
            RebuildIndex(context);
            return CommandExecutionResult.Ok(StatusJson(context, true));
        }

        [McpCommand(
            "asset.index.clear",
            Title = "Clear asset index",
            Description = "Clear the in-memory Unity asset index",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "asset", "index" },
            Capability = "asset_index",
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetIndexClear(McpCommandContext context)
        {
            ClearIndex(context);
            return CommandExecutionResult.Ok(StatusJson(context, false));
        }

        [McpCommand(
            "asset.list",
            Title = "Asset list",
            Description = "List bounded indexed Unity assets by safe Unity-relative prefix",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ListSchemaJson,
            Tags = new string[] { "asset", "index" },
            Capability = "asset_index",
            ResultShape = "bounded_list",
            OutputLimitJson = ListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetList(McpCommandContext context)
        {
            var prefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (prefixResult != null)
            {
                return prefixResult;
            }

            EnsureIndex(context);

            var maxResults = MaxResults(context.ArgsJson, 100);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;

            foreach (var entry in EntriesSnapshot())
            {
                if (!includePackages && entry.Source == "Packages")
                {
                    continue;
                }

                if (!entry.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                AppendEntry(builder, entry, count > 0);
                count++;
            }

            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"max_results\":" + maxResults
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"assets\":[" + builder + "]}");
        }

        [McpCommand(
            "asset.search",
            Title = "Asset search",
            Description = "Search indexed Unity assets by path, name, type or GUID",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SearchSchemaJson,
            Tags = new string[] { "asset", "index" },
            Capability = "asset_index",
            ResultShape = "bounded_list",
            OutputLimitJson = ListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetSearch(McpCommandContext context)
        {
            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            if (string.IsNullOrEmpty(query))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query is required");
            }

            EnsureIndex(context);

            var maxResults = MaxResults(context.ArgsJson, 100);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;

            lock (s_lock)
            {
                foreach (var entry in s_entries)
                {
                    if (!includePackages && entry.Source == "Packages")
                    {
                        continue;
                    }

                    if (!Matches(entry, query))
                    {
                        continue;
                    }

                    if (count >= maxResults)
                    {
                        truncated = true;
                        break;
                    }

                    AppendEntry(builder, entry, count > 0);
                    count++;
                }
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"results\":[" + builder + "]}");
        }

        [McpCommand(
            "asset.semantic_index.status",
            Title = "Semantic asset index status",
            Description = "Read deterministic local semantic asset search index status",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "asset", "semantic", "index", "unity-ai-assistant" },
            Capability = "asset_semantic_index",
            ResultShape = "status",
            FeatureFlag = SemanticFeatureFlag)]
        public static CommandExecutionResult AssetSemanticIndexStatus(McpCommandContext context)
        {
            TryLoadCache(context);
            return CommandExecutionResult.Ok(SemanticStatusJson(context));
        }

        [McpCommand(
            "asset.semantic_search",
            Title = "Semantic asset search",
            Description = "Search Unity assets with deterministic local lexical metadata scoring",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 60000,
            SchemaJson = SemanticSearchSchemaJson,
            Tags = new string[] { "asset", "semantic", "index", "unity-ai-assistant" },
            Capability = "asset_semantic_index",
            ResultShape = "bounded_list",
            OutputLimitJson = SemanticSearchOutputLimitJson,
            FeatureFlag = SemanticFeatureFlag)]
        public static CommandExecutionResult AssetSemanticSearch(McpCommandContext context)
        {
            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            if (!string.IsNullOrEmpty(query) && query.Length > 256)
            {
                query = query.Substring(0, 256);
            }

            query = (query ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(query))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query is required");
            }

            var queryTokens = SemanticQueryTokens(query);
            if (queryTokens.Count == 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "query must contain at least one searchable token");
            }

            var prefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (prefixResult != null)
            {
                return prefixResult;
            }

            EnsureIndex(context);

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            var includeLabels = JsonUtil.ExtractBool(context.ArgsJson, "include_labels", true);
            var includeImporter = JsonUtil.ExtractBool(context.ArgsJson, "include_importer", true);
            var includeScriptSymbols = JsonUtil.ExtractBool(context.ArgsJson, "include_script_symbols", true);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 5000), 1, MaxSemanticScanAssets);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 200);
            var hits = new List<SemanticAssetSearchHit>();
            var scannedAssets = 0;
            var matchedAssets = 0;
            var scanTruncated = false;

            foreach (var entry in EntriesSnapshot())
            {
                if (!includePackages && entry.Source == "Packages")
                {
                    continue;
                }

                if (!entry.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                var hit = ScoreSemanticAsset(entry, query, queryTokens, includeLabels, includeImporter, includeScriptSymbols);
                if (hit.Score <= 0)
                {
                    continue;
                }

                matchedAssets++;
                hits.Add(hit);
            }

            hits.Sort(CompareSemanticHits);
            var includedResults = Math.Min(hits.Count, maxResults);
            var resultsTruncated = hits.Count > includedResults;
            var builder = new StringBuilder();
            for (var index = 0; index < includedResults; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(SemanticHitJson(hits[index]));
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"query_tokens\":[" + StringArrayJson(queryTokens) + "]"
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_labels\":" + JsonUtil.Bool(includeLabels)
                + ",\"include_importer\":" + JsonUtil.Bool(includeImporter)
                + ",\"include_script_symbols\":" + JsonUtil.Bool(includeScriptSymbols)
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"matched_assets\":" + matchedAssets
                + ",\"max_results\":" + maxResults
                + ",\"truncated\":" + JsonUtil.Bool(scanTruncated || resultsTruncated)
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"scoring\":{\"mode\":\"deterministic_lexical_metadata\",\"local_only\":true,\"network_access\":false,\"embedding_provider\":\"none\",\"fields\":[\"path\",\"name\",\"type\",\"extension\",\"guid\",\"source\",\"label\",\"importer\",\"script_symbol\"]}"
                + ",\"results\":[" + builder + "]}");
        }

        [McpCommand(
            "asset.info",
            Title = "Asset info",
            Description = "Read bounded Unity asset metadata for one Unity-relative asset path",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = AssetInfoSchemaJson,
            Tags = new string[] { "asset", "index", "unity-ai-assistant" },
            Capability = "asset_index",
            ResultShape = "object_details",
            OutputLimitJson = AssetInfoOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetInfo(McpCommandContext context)
        {
            var rawPath = JsonUtil.ExtractString(context.ArgsJson, "asset_path");
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument asset_path is required");
            }

            var assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            return CommandExecutionResult.Ok(AssetInfoJson(assetPath, context));
        }

        [McpCommand(
            "asset.serialized.inspect",
            Title = "Inspect serialized asset",
            Description = "Read bounded SerializedObject data for one Assets/ or Packages/ asset; use project.settings.inspect for allowlisted ProjectSettings",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = SerializedInspectSchemaJson,
            Tags = new string[] { "asset", "serialized", "unity-ai-assistant" },
            Capability = "asset_index",
            ResultShape = "object_details",
            OutputLimitJson = SerializedInspectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetSerializedInspect(McpCommandContext context)
        {
            var requestedPath = JsonUtil.ExtractString(context.ArgsJson, "asset_path");
            if (string.Equals(requestedPath, "ProjectSettings/QualitySettings.asset", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error(
                    "path_not_allowed",
                    "asset.serialized.inspect accepts only Assets/ or Packages/ paths; use project.settings.inspect for persisted QualitySettings",
                    "{\"replacement_command\":\"project.settings.inspect\",\"replacement_args\":{\"name\":\"QualitySettings\"},\"runtime_counterpart\":\"runtime.quality_settings.inspect\"}");
            }
            if (requestedPath.StartsWith("ProjectSettings/", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error(
                    "path_not_allowed",
                    "asset.serialized.inspect accepts only Assets/ or Packages/ paths; use project.settings.inspect for an allowlisted settings name",
                    "{\"replacement_command\":\"project.settings.inspect\"}");
            }

            var pathResult = RequireAssetPath(context.ArgsJson, "asset_path", out var assetPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var argsResult = RequireSerializedInspectArgs(context.ArgsJson, out var propertyPath, out var maxDepth, out var maxArrayElements, out var maxChars);
            if (argsResult != null)
            {
                return argsResult;
            }

            return SerializedAssetInspectResult(assetPath, propertyPath, maxDepth, maxArrayElements, maxChars);
        }

        [McpCommand(
            "asset.references",
            Title = "Asset references",
            Description = "Find indexed assets that depend on one Unity asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 60000,
            SchemaJson = AssetTargetSchemaJson,
            Tags = new string[] { "asset", "reference-graph" },
            Capability = "asset_index",
            ResultShape = "bounded_list",
            OutputLimitJson = AssetReportListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetReferences(McpCommandContext context)
        {
            var targetResult = ResolveTargetAsset(context.ArgsJson, out var target);
            if (targetResult != null)
            {
                return targetResult;
            }

            EnsureIndex(context);

            var maxResults = MaxResults(context.ArgsJson, 100);
            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;

            foreach (var entry in EntriesSnapshot())
            {
                if (string.Equals(entry.Path, target.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!DependenciesContain(entry.Path, target.Path, true))
                {
                    continue;
                }

                if (count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                AppendEntry(builder, entry, count > 0);
                count++;
            }

            var warnings = AssetWorkflowWarnings(target, truncated);
            var response = "{\"asset\":" + EntryJson(target)
                + AssetWorkflowFieldsJson(target, warnings, new[] { "asset.dependencies", "dependency.query", "asset.risk_report", "asset.relocation.preflight" })
                + ",\"max_results\":" + maxResults
                + ",\"reference_count\":" + count
                + ",\"returned_reference_count\":" + count
                + ",\"reference_count_truncated\":" + JsonUtil.Bool(truncated)
                + ",\"references_truncated\":" + JsonUtil.Bool(truncated)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"references\":[" + builder + "]}";
            return CommandExecutionResult.Ok(WithOptionalAssetReportArtifact(context.ArgsJson, "asset-references", response));
        }

        [McpCommand(
            "asset.dependencies",
            Title = "Asset dependencies",
            Description = "Read Unity AssetDatabase dependencies for one asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = AssetPathSchemaJson,
            Tags = new string[] { "asset", "reference-graph" },
            Capability = "asset_index",
            ResultShape = "bounded_list",
            OutputLimitJson = AssetReportListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetDependencies(McpCommandContext context)
        {
            var pathResult = RequireAssetPath(context.ArgsJson, "asset_path", out var assetPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var recursive = JsonUtil.ExtractBool(context.ArgsJson, "recursive", true);
            var maxResults = MaxResults(context.ArgsJson, 200);
            var dependencies = AssetDatabase.GetDependencies(assetPath, recursive);
            Array.Sort(dependencies, StringComparer.Ordinal);

            var dependencyCount = 0;
            for (var index = 0; index < dependencies.Length; index++)
            {
                if (!string.Equals(dependencies[index], assetPath, StringComparison.Ordinal))
                {
                    dependencyCount++;
                }
            }

            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;
            foreach (var dependencyPath in dependencies)
            {
                if (string.Equals(dependencyPath, assetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (count >= maxResults)
                {
                    truncated = true;
                    break;
                }

                AppendEntry(builder, EntryFromPath(dependencyPath), count > 0);
                count++;
            }

            var asset = EntryFromPath(assetPath);
            var warnings = AssetWorkflowWarnings(asset, truncated);
            var response = "{\"asset\":" + EntryJson(asset)
                + AssetWorkflowFieldsJson(asset, warnings, new[] { "asset.references", "dependency.query", "asset.risk_report", "asset.relocation.preflight" })
                + ",\"recursive\":" + JsonUtil.Bool(recursive)
                + ",\"max_results\":" + maxResults
                + ",\"dependency_count\":" + dependencyCount
                + ",\"returned_dependency_count\":" + count
                + ",\"dependency_count_truncated\":" + JsonUtil.Bool(truncated)
                + ",\"dependencies_truncated\":" + JsonUtil.Bool(truncated)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"dependencies\":[" + builder + "]}";
            return CommandExecutionResult.Ok(WithOptionalAssetReportArtifact(context.ArgsJson, "asset-dependencies", response));
        }

        [McpCommand(
            "dependency.query",
            Title = "Dependency query",
            Description = "Read a bounded local asset dependency graph for one Unity asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 60000,
            SchemaJson = DependencyQuerySchemaJson,
            Tags = new string[] { "asset", "dependency", "reference-graph", "unity-ai-assistant" },
            Capability = "asset_index",
            ResultShape = "bounded_graph",
            OutputLimitJson = DependencyQueryOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult DependencyQuery(McpCommandContext context)
        {
            var targetResult = ResolveTargetAsset(context.ArgsJson, out var target);
            if (targetResult != null)
            {
                return targetResult;
            }

            EnsureIndex(context);

            var recursive = JsonUtil.ExtractBool(context.ArgsJson, "recursive", true);
            var includeOutgoing = JsonUtil.ExtractBool(context.ArgsJson, "include_outgoing", true);
            var includeIncoming = JsonUtil.ExtractBool(context.ArgsJson, "include_incoming", true);
            var includeYamlGuidRefs = JsonUtil.ExtractBool(context.ArgsJson, "include_yaml_guid_refs", true);
            var includeScriptContext = JsonUtil.ExtractBool(context.ArgsJson, "include_script_context", true);
            var includeAssemblyReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_assembly_references", true);
            var maxEdges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_edges", 200), 1, 1000);
            var maxYamlReferences = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_yaml_references", 50), 1, 200);
            var edges = new StringBuilder();
            var edgeCount = 0;
            var truncated = false;
            var yamlReferenceCount = 0;
            var yamlReferencesTruncated = false;
            var assemblyReferenceCount = 0;
            var assemblyReferencesTruncated = false;
            var scriptContextJson = "null";

            if (includeOutgoing)
            {
                AppendOutgoingDependencyEdges(edges, target, recursive, maxEdges, ref edgeCount, ref truncated);
            }

            if (!truncated && includeIncoming)
            {
                AppendIncomingDependencyEdges(edges, target, recursive, maxEdges, ref edgeCount, ref truncated);
            }

            if (!truncated && includeYamlGuidRefs)
            {
                AppendYamlGuidReferenceEdges(edges, target, maxEdges, maxYamlReferences, ref edgeCount, ref truncated, out yamlReferenceCount, out yamlReferencesTruncated);
            }

            if (!truncated && includeAssemblyReferences)
            {
                AppendAsmdefAssemblyReferenceEdges(edges, target, maxEdges, ref edgeCount, ref truncated, out assemblyReferenceCount, out assemblyReferencesTruncated);
            }

            if (!truncated && includeScriptContext)
            {
                scriptContextJson = BuildScriptDependencyContext(edges, target, maxEdges, ref edgeCount, ref truncated);
            }

            var queryWarnings = AssetWorkflowWarnings(target, truncated || yamlReferencesTruncated || assemblyReferencesTruncated);
            var queryResponse = "{\"asset\":" + EntryJson(target)
                + AssetWorkflowFieldsJson(target, queryWarnings, new[] { "asset.references", "asset.dependencies", "asset.risk_report", "asset.relocation.preflight" })
                + ",\"recursive\":" + JsonUtil.Bool(recursive)
                + ",\"include_outgoing\":" + JsonUtil.Bool(includeOutgoing)
                + ",\"include_incoming\":" + JsonUtil.Bool(includeIncoming)
                + ",\"include_yaml_guid_refs\":" + JsonUtil.Bool(includeYamlGuidRefs)
                + ",\"include_script_context\":" + JsonUtil.Bool(includeScriptContext)
                + ",\"include_assembly_references\":" + JsonUtil.Bool(includeAssemblyReferences)
                + ",\"max_edges\":" + maxEdges
                + ",\"edge_count\":" + edgeCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"yaml_reference_count\":" + yamlReferenceCount
                + ",\"yaml_references_truncated\":" + JsonUtil.Bool(yamlReferencesTruncated)
                + ",\"assembly_reference_count\":" + assemblyReferenceCount
                + ",\"assembly_references_truncated\":" + JsonUtil.Bool(assemblyReferencesTruncated)
                + ",\"script_context\":" + scriptContextJson
                + ",\"compatible_with\":\"Unity.GetDependency local asset graph\""
                + ",\"edges\":[" + edges + "]}";
            return CommandExecutionResult.Ok(WithOptionalAssetReportArtifact(context.ArgsJson, "dependency-query", queryResponse));
        }

        [McpCommand(
            "asset.relocation.preflight",
            Title = "Preflight asset relocation",
            Description = "Dry-run GUID reference, script assembly, and shader include/path risks for moving one Unity asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 60000,
            SchemaJson = AssetRelocationPreflightSchemaJson,
            Tags = new string[] { "asset", "relocation", "reference-graph", "dry-run", "unity-ai-assistant" },
            Capability = "asset_index",
            ResultShape = "object_details",
            OutputLimitJson = AssetRelocationPreflightOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetRelocationPreflight(McpCommandContext context)
        {
            var pathResult = RequireRelocationPaths(context.ArgsJson, out var sourcePath, out var destinationPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var sourceGuid = AssetDatabase.AssetPathToGUID(sourcePath);
            if (string.IsNullOrEmpty(sourceGuid))
            {
                return CommandExecutionResult.Error("asset_not_found", "source_path does not exist");
            }

            EnsureIndex(context);

            var destinationGuid = AssetDatabase.AssetPathToGUID(destinationPath);
            var sourceIsFolder = AssetDatabase.IsValidFolder(sourcePath);
            var destinationExists = !string.IsNullOrEmpty(destinationGuid) || AssetDatabase.IsValidFolder(destinationPath);
            var destinationParentPath = ParentAssetPath(destinationPath);
            var destinationParentExists = !string.IsNullOrEmpty(destinationParentPath) && AssetDatabase.IsValidFolder(destinationParentPath);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var includeAssetDatabase = JsonUtil.ExtractBool(context.ArgsJson, "include_assetdatabase", true);
            var includeYamlGuidRefs = JsonUtil.ExtractBool(context.ArgsJson, "include_yaml_guid_refs", true);
            var includeTextIncludes = JsonUtil.ExtractBool(context.ArgsJson, "include_text_includes", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 200), 1, 1000);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxRelocationScanAssets);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 262144), 1, MaxRelocationScanBytes);

            var references = new StringBuilder();
            var scannedAssets = 0;
            var scanTruncated = false;
            var referenceCount = 0;
            var returnedResults = 0;
            var resultsTruncated = false;
            var assetDatabaseReferenceCount = 0;
            var yamlGuidReferenceCount = 0;
            var shaderIncludeReferenceCount = 0;
            var readErrorCount = 0;
            var entries = EntriesSnapshot();
            Array.Sort(entries, CompareEntriesByPath);
            for (var index = 0; index < entries.Length; index++)
            {
                var entry = entries[index];
                if (string.Equals(entry.Path, sourcePath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!includePackages && string.Equals(entry.Source, "Packages", StringComparison.Ordinal))
                {
                    continue;
                }

                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                var referenceKinds = new List<string>();
                var includeLine = 0;
                var includeRawReference = string.Empty;
                var yamlLine = 0;
                var yamlReadError = string.Empty;
                var includeReadError = string.Empty;
                var readErrors = new List<string>();

                if (includeAssetDatabase && DependenciesContain(entry.Path, sourcePath, true))
                {
                    referenceKinds.Add("assetdatabase_dependency");
                    assetDatabaseReferenceCount++;
                }

                if (includeYamlGuidRefs && IsUnityYamlTextAsset(entry.Path) && YamlReferencesGuid(entry.Path, sourceGuid, maxScanBytes, out yamlLine, out yamlReadError))
                {
                    referenceKinds.Add("yaml_guid");
                    yamlGuidReferenceCount++;
                }

                if (!string.IsNullOrEmpty(yamlReadError))
                {
                    readErrorCount++;
                    readErrors.Add("yaml_guid:" + yamlReadError);
                }

                if (includeTextIncludes && IsShaderIncludeTextCandidate(entry.Path) && ShaderIncludeReferencesTarget(entry.Path, sourcePath, maxScanBytes, out includeLine, out includeRawReference, out includeReadError))
                {
                    referenceKinds.Add("shader_include_text");
                    shaderIncludeReferenceCount++;
                }

                if (!string.IsNullOrEmpty(includeReadError))
                {
                    readErrorCount++;
                    readErrors.Add("shader_include_text:" + includeReadError);
                }

                if (referenceKinds.Count == 0)
                {
                    continue;
                }

                referenceCount++;
                if (returnedResults >= maxResults)
                {
                    resultsTruncated = true;
                    break;
                }

                if (returnedResults > 0)
                {
                    references.Append(',');
                }

                references.Append(RelocationReferenceJson(entry, referenceKinds, includeLine, includeRawReference, yamlLine, readErrors));
                returnedResults++;
            }

            var scriptContextJson = AssetRelocationScriptContextJson(sourcePath, destinationPath, out var scriptAssemblyChanges);
            var shaderContextJson = AssetRelocationShaderContextJson(sourcePath, destinationPath, shaderIncludeReferenceCount);
            var warnings = BuildAssetRelocationWarnings(sourcePath, destinationPath, sourceIsFolder, destinationExists, destinationParentExists, scriptAssemblyChanges, shaderIncludeReferenceCount, scanTruncated, resultsTruncated);

            var relocationActions = new[] { "review_warnings", "checkpoint.create", "asset.move_after_approval", "asset.references", "asset.risk_report" };
            var relocationResponse = "{\"dry_run\":true"
                + ",\"source_path\":" + JsonUtil.String(sourcePath)
                + ",\"destination_path\":" + JsonUtil.String(destinationPath)
                + ",\"source_guid\":" + JsonUtil.String(sourceGuid)
                + ",\"source_asset\":" + EntryJson(EntryFromPath(sourcePath, sourceGuid))
                + ",\"source_is_folder\":" + JsonUtil.Bool(sourceIsFolder)
                + ",\"source_package_asset\":" + JsonUtil.Bool(IsPackageAssetPath(sourcePath))
                + ",\"destination\":" + RelocationDestinationJson(destinationPath, destinationGuid, destinationExists)
                + ",\"destination_package_asset\":" + JsonUtil.Bool(IsPackageAssetPath(destinationPath))
                + ",\"destination_parent_path\":" + JsonUtil.String(destinationParentPath)
                + ",\"destination_parent_exists\":" + JsonUtil.Bool(destinationParentExists)
                + ",\"same_path\":" + JsonUtil.Bool(false)
                + ",\"extension_changes\":" + JsonUtil.Bool(!string.Equals(Path.GetExtension(sourcePath), Path.GetExtension(destinationPath), StringComparison.OrdinalIgnoreCase))
                + ",\"source_changes\":" + JsonUtil.Bool(!string.Equals(SourceForPath(sourcePath), SourceForPath(destinationPath), StringComparison.Ordinal))
                + ",\"guid_stable_after_unity_move\":true"
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_assetdatabase\":" + JsonUtil.Bool(includeAssetDatabase)
                + ",\"include_yaml_guid_refs\":" + JsonUtil.Bool(includeYamlGuidRefs)
                + ",\"include_text_includes\":" + JsonUtil.Bool(includeTextIncludes)
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"reference_count\":" + referenceCount
                + ",\"returned_results\":" + returnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"assetdatabase_reference_count\":" + assetDatabaseReferenceCount
                + ",\"yaml_guid_reference_count\":" + yamlGuidReferenceCount
                + ",\"shader_include_reference_count\":" + shaderIncludeReferenceCount
                + ",\"read_error_count\":" + readErrorCount
                + ",\"script_context\":" + scriptContextJson
                + ",\"shader_context\":" + shaderContextJson
                + ",\"references\":[" + references + "]"
                + ",\"truncated\":" + JsonUtil.Bool(scanTruncated || resultsTruncated)
                + ",\"warnings\":[" + StringArrayJson(warnings) + "]"
                + ",\"next_valid_actions\":[" + StringArrayJson(relocationActions) + "]}";
            return CommandExecutionResult.Ok(WithOptionalAssetReportArtifact(context.ArgsJson, "asset-relocation-preflight", relocationResponse));
        }

        [McpCommand(
            "asset.text.read",
            Title = "Read text asset",
            Description = "Read bounded UTF-8 text from one Unity-relative asset path without network access",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TextReadSchemaJson,
            Tags = new string[] { "asset", "text", "index" },
            Capability = "asset_index",
            ResultShape = "object_details",
            OutputLimitJson = TextReadOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetTextRead(McpCommandContext context)
        {
            var pathResult = RequireAssetPath(context.ArgsJson, "asset_path", out var assetPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var maxBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_bytes", 65536), 1, 65536);
            if (!TryResolveAssetFilePath(assetPath, out var fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "Asset file was not found");
            }

            var bytes = ReadBoundedBytes(fullPath, maxBytes, out var byteLength, out var truncated);
            var textResult = DecodeText(bytes, truncated, out var content);
            if (textResult != null)
            {
                return textResult;
            }

            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"encoding\":\"utf-8\""
                + ",\"byte_length\":" + byteLength
                + ",\"max_bytes\":" + maxBytes
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"raw_unity_yaml\":" + JsonUtil.Bool(IsUnityYamlTextAsset(assetPath))
                + ",\"content\":" + JsonUtil.String(content) + "}");
        }

        [McpCommand(
            "asset.text.find",
            Title = "Find in text asset",
            Description = "Find literal text matches in one bounded UTF-8 Unity asset without network access",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TextFindSchemaJson,
            Tags = new string[] { "asset", "text", "index" },
            Capability = "asset_index",
            ResultShape = "bounded_list",
            OutputLimitJson = TextFindOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetTextFind(McpCommandContext context)
        {
            var pathResult = RequireAssetPath(context.ArgsJson, "asset_path", out var assetPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            if (string.IsNullOrEmpty(query))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query is required");
            }

            var caseSensitive = JsonUtil.ExtractBool(context.ArgsJson, "case_sensitive", false);
            var matchMode = TextMatchMode(context.ArgsJson, out var matchModeError);
            if (matchModeError != null)
            {
                return matchModeError;
            }

            var contextLines = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "context_lines", 0), 0, 5);
            var regexTimeoutMs = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "regex_timeout_ms", 250), 10, 1000);
            var regexResult = TryCreateTextRegex(query, matchMode, caseSensitive, regexTimeoutMs, out var contentRegex);
            if (regexResult != null)
            {
                return regexResult;
            }

            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 200);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 1048576), 1, 1048576);
            if (!TryResolveAssetFilePath(assetPath, out var fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "Asset file was not found");
            }

            var bytes = ReadBoundedBytes(fullPath, maxScanBytes, out var byteLength, out var scanTruncated);
            var textResult = DecodeText(bytes, scanTruncated, out var content);
            if (textResult != null)
            {
                return textResult;
            }

            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            string matches;
            bool truncated;
            try
            {
                matches = FindTextMatches(content, query, comparison, contentRegex, contextLines, maxResults, out truncated);
            }
            catch (RegexMatchTimeoutException)
            {
                return CommandExecutionResult.Error("regex_timeout", "Regex search timed out");
            }

            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"match_mode\":" + JsonUtil.String(matchMode)
                + ",\"case_sensitive\":" + JsonUtil.Bool(caseSensitive)
                + ",\"context_lines\":" + contextLines
                + ",\"regex_timeout_ms\":" + regexTimeoutMs
                + ",\"byte_length\":" + byteLength
                + ",\"scan_bytes\":" + bytes.Length
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"raw_unity_yaml\":" + JsonUtil.Bool(IsUnityYamlTextAsset(assetPath))
                + ",\"matches\":[" + matches + "]}");
        }

        [McpCommand(
            "asset.text.search",
            Title = "Search text assets",
            Description = "Search literal text across bounded local UTF-8 Unity assets by safe prefix without network access",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TextSearchSchemaJson,
            Tags = new string[] { "asset", "text", "index", "unity-ai-assistant" },
            Capability = "asset_index",
            ResultShape = "bounded_list",
            OutputLimitJson = TextSearchOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetTextSearch(McpCommandContext context)
        {
            var prefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (prefixResult != null)
            {
                return prefixResult;
            }

            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            if (string.IsNullOrEmpty(query))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query is required");
            }

            if (query.Length > 256)
            {
                return CommandExecutionResult.Error("invalid_field_value", "query must be 1..256 characters");
            }

            EnsureIndex(context);

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", pathPrefix.StartsWith("Packages/", StringComparison.Ordinal));
            var caseSensitive = JsonUtil.ExtractBool(context.ArgsJson, "case_sensitive", false);
            var matchMode = TextMatchMode(context.ArgsJson, out var matchModeError);
            if (matchModeError != null)
            {
                return matchModeError;
            }

            var nameRegexPattern = JsonUtil.ExtractString(context.ArgsJson, "name_regex");
            if (!string.IsNullOrEmpty(nameRegexPattern) && nameRegexPattern.Length > 256)
            {
                return CommandExecutionResult.Error("invalid_field_value", "name_regex must be 1..256 characters");
            }

            var contextLines = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "context_lines", 0), 0, 5);
            var regexTimeoutMs = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "regex_timeout_ms", 250), 10, 1000);
            var regexResult = TryCreateTextRegex(query, matchMode, caseSensitive, regexTimeoutMs, out var contentRegex);
            if (regexResult != null)
            {
                return regexResult;
            }

            regexResult = TryCreateOptionalRegex(nameRegexPattern, caseSensitive, regexTimeoutMs, out var nameRegex);
            if (regexResult != null)
            {
                return regexResult;
            }

            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 200);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 1048576), 1, 1048576);
            var maxFiles = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_files", 1000), 1, 10000);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            var builder = new StringBuilder();
            var candidateIndex = 0;
            var scannedFiles = 0;
            var skippedFiles = 0;
            var matchedFiles = 0;
            var matchCount = 0;
            var nextStartIndex = -1;
            var truncated = false;

            foreach (var entry in EntriesSnapshot())
            {
                if (!includePackages && entry.Source == "Packages")
                {
                    continue;
                }

                if (!entry.Path.StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (nameRegex != null)
                {
                    try
                    {
                        if (!NameRegexMatches(entry, nameRegex))
                        {
                            continue;
                        }
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        return CommandExecutionResult.Error("regex_timeout", "name_regex search timed out");
                    }
                }

                if (candidateIndex++ < startIndex)
                {
                    continue;
                }

                if (scannedFiles >= maxFiles)
                {
                    truncated = true;
                    nextStartIndex = candidateIndex - 1;
                    break;
                }

                scannedFiles++;
                if (!IsTextSearchCandidate(entry.Path))
                {
                    skippedFiles++;
                    continue;
                }

                string assetPath;
                try
                {
                    assetPath = PathGuard.RequireUnityAssetPath(entry.Path);
                }
                catch (ArgumentException)
                {
                    skippedFiles++;
                    continue;
                }

                if (!TryResolveAssetFilePath(assetPath, out var fullPath))
                {
                    skippedFiles++;
                    continue;
                }

                byte[] bytes;
                long byteLength;
                bool scanTruncated;
                try
                {
                    bytes = ReadBoundedBytes(fullPath, maxScanBytes, out byteLength, out scanTruncated);
                }
                catch (IOException)
                {
                    skippedFiles++;
                    continue;
                }
                catch (UnauthorizedAccessException)
                {
                    skippedFiles++;
                    continue;
                }
                catch (ArgumentException)
                {
                    skippedFiles++;
                    continue;
                }
                catch (NotSupportedException)
                {
                    skippedFiles++;
                    continue;
                }

                var textResult = DecodeText(bytes, scanTruncated, out var content);
                if (textResult != null)
                {
                    skippedFiles++;
                    continue;
                }

                var remainingResults = maxResults - matchCount;
                if (remainingResults <= 0)
                {
                    truncated = true;
                    nextStartIndex = candidateIndex - 1;
                    break;
                }

                string matches;
                bool fileTruncated;
                int fileMatchCount;
                try
                {
                    matches = FindTextMatches(content, query, comparison, contentRegex, contextLines, remainingResults, out fileTruncated, out fileMatchCount);
                }
                catch (RegexMatchTimeoutException)
                {
                    return CommandExecutionResult.Error("regex_timeout", "Regex search timed out");
                }

                if (fileMatchCount <= 0)
                {
                    continue;
                }

                if (matchedFiles > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"guid\":").Append(JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath)))
                    .Append(",\"byte_length\":").Append(byteLength)
                    .Append(",\"scan_bytes\":").Append(bytes.Length)
                    .Append(",\"scan_truncated\":").Append(JsonUtil.Bool(scanTruncated))
                    .Append(",\"raw_unity_yaml\":").Append(JsonUtil.Bool(IsUnityYamlTextAsset(assetPath)))
                    .Append(",\"matches\":[").Append(matches).Append("]}");
                matchedFiles++;
                matchCount += fileMatchCount;

                if (fileTruncated || matchCount >= maxResults)
                {
                    truncated = true;
                    nextStartIndex = candidateIndex;
                    break;
                }
            }

            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"match_mode\":" + JsonUtil.String(matchMode)
                + ",\"name_regex\":" + (string.IsNullOrEmpty(nameRegexPattern) ? "null" : JsonUtil.String(nameRegexPattern))
                + ",\"case_sensitive\":" + JsonUtil.Bool(caseSensitive)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"context_lines\":" + contextLines
                + ",\"regex_timeout_ms\":" + regexTimeoutMs
                + ",\"start_index\":" + startIndex
                + ",\"next_start_index\":" + (nextStartIndex >= 0 ? nextStartIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"max_results\":" + maxResults
                + ",\"max_files\":" + maxFiles
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"scanned_files\":" + scannedFiles
                + ",\"skipped_files\":" + skippedFiles
                + ",\"matched_files\":" + matchedFiles
                + ",\"match_count\":" + matchCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"compatible_with\":\"Unity.Grep\""
                + ",\"results\":[" + builder + "]}");
        }

        [McpCommand(
            "asset.guid.resolve",
            Title = "Resolve asset GUID",
            Description = "Resolve a Unity asset GUID to an asset path",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = GuidSchemaJson,
            Tags = new string[] { "asset", "guid" },
            Capability = "asset_index",
            ResultShape = "object_details",
            OutputLimitJson = SingleAssetOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetGuidResolve(McpCommandContext context)
        {
            var guid = JsonUtil.ExtractString(context.ArgsJson, "guid");
            if (string.IsNullOrEmpty(guid))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument guid is required");
            }

            var assetPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "GUID was not found");
            }

            return CommandExecutionResult.Ok("{\"asset\":" + EntryJson(EntryFromPath(assetPath, guid)) + "}");
        }

        [McpCommand(
            "asset.risk_report",
            Title = "Asset risk report",
            Description = "Read bounded reference and dependency risk hints before asset edits",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 60000,
            SchemaJson = RiskReportSchemaJson,
            Tags = new string[] { "asset", "reference-graph" },
            Capability = "asset_index",
            ResultShape = "object_details",
            OutputLimitJson = AssetRiskReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AssetRiskReport(McpCommandContext context)
        {
            var pathResult = RequireAssetPath(context.ArgsJson, "asset_path", out var assetPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            EnsureIndex(context);

            var asset = EntryFromPath(assetPath);
            var dependencyCount = CountDependencies(assetPath);
            var duplicateGuidPaths = DuplicateGuidPaths(asset.Guid, asset.Path, 20, out var duplicateGuidTruncated);
            var missingScriptReferences = MissingScriptReferences(asset.Path, 20, out var missingScriptTruncated);
            var missingScriptGuids = ReferenceGuids(missingScriptReferences);
            var brokenReferenceReferences = BrokenReferenceReferences(asset.Path, missingScriptGuids, 20, out var brokenReferenceTruncated);
            var brokenReferenceGuids = ReferenceGuids(brokenReferenceReferences);
            var referenceCount = 0;
            var referenceCountTruncated = false;
            foreach (var entry in EntriesSnapshot())
            {
                if (string.Equals(entry.Path, assetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (DependenciesContain(entry.Path, assetPath, true))
                {
                    referenceCount++;
                    if (referenceCount >= 1000)
                    {
                        referenceCountTruncated = true;
                        break;
                    }
                }
            }

            var risk = duplicateGuidPaths.Count > 0 || missingScriptGuids.Count > 0 || brokenReferenceGuids.Count > 0 ? "high" : referenceCount > 0 ? "medium" : "low";
            var warnings = AssetWorkflowWarnings(asset, referenceCountTruncated || duplicateGuidTruncated || missingScriptTruncated || brokenReferenceTruncated);
            if (duplicateGuidPaths.Count > 0)
            {
                warnings.Add("Duplicate GUID risk was found; do not move/delete until duplicate .meta files are resolved.");
            }

            if (missingScriptGuids.Count > 0)
            {
                warnings.Add("Missing script references were found in this asset; inspect missing_script_locations before editing.");
            }

            if (brokenReferenceGuids.Count > 0)
            {
                warnings.Add("Broken GUID references were found in this asset; inspect broken_reference_locations before editing.");
            }

            var response = "{\"asset\":" + EntryJson(asset)
                + AssetWorkflowFieldsJson(asset, warnings, new[] { "asset.references", "asset.dependencies", "asset.relocation.preflight", "checkpoint.create" })
                + ",\"risk\":" + JsonUtil.String(risk)
                + ",\"reference_count\":" + referenceCount
                + ",\"reference_count_truncated\":" + JsonUtil.Bool(referenceCountTruncated)
                + ",\"dependency_count\":" + dependencyCount
                + ",\"duplicate_guid_count\":" + duplicateGuidPaths.Count
                + ",\"duplicate_guid_paths\":[" + StringArrayJson(duplicateGuidPaths) + "]"
                + ",\"duplicate_guid_truncated\":" + JsonUtil.Bool(duplicateGuidTruncated)
                + ",\"missing_script_scan_supported\":true"
                + ",\"missing_script_count\":" + missingScriptGuids.Count
                + ",\"missing_script_guids\":[" + StringArrayJson(missingScriptGuids) + "]"
                + ",\"missing_script_locations\":[" + ReferenceArrayJson(missingScriptReferences) + "]"
                + ",\"missing_script_truncated\":" + JsonUtil.Bool(missingScriptTruncated)
                + ",\"broken_reference_scan_supported\":true"
                + ",\"broken_reference_count\":" + brokenReferenceGuids.Count
                + ",\"broken_reference_guids\":[" + StringArrayJson(brokenReferenceGuids) + "]"
                + ",\"broken_reference_locations\":[" + ReferenceArrayJson(brokenReferenceReferences) + "]"
                + ",\"broken_reference_truncated\":" + JsonUtil.Bool(brokenReferenceTruncated)
                + ",\"duplicate_guid_scan_supported\":true}";
            return CommandExecutionResult.Ok(WithOptionalAssetReportArtifact(context.ArgsJson, "asset-risk-report", response));
        }

        static void EnsureIndex(McpCommandContext context)
        {
            lock (s_lock)
            {
                if (s_builtAtUtc != DateTime.MinValue)
                {
                    return;
                }
            }

            if (TryLoadCache(context))
            {
                return;
            }

            RebuildIndex(context);
        }

        static void RebuildIndex(McpCommandContext context)
        {
            var guids = AssetDatabase.FindAssets(string.Empty, new[] { "Assets", "Packages" });
            var entries = new List<AssetIndexEntry>(guids.Length);
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath) || !IsUnityAssetPath(assetPath))
                {
                    continue;
                }

                entries.Add(EntryFromPath(assetPath, guid));
            }

            entries.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.Ordinal));

            var builtAtUtc = DateTime.UtcNow;
            lock (s_lock)
            {
                s_entries.Clear();
                s_byPath.Clear();
                s_byGuid.Clear();
                foreach (var entry in entries)
                {
                    s_entries.Add(entry);
                    s_byPath[entry.Path] = entry;
                    if (!string.IsNullOrEmpty(entry.Guid))
                    {
                        s_byGuid[entry.Guid] = entry;
                    }
                }

                s_builtAtUtc = builtAtUtc;
                s_loadedFromCache = false;
            }

            WriteCache(entries, builtAtUtc, context);
        }

        static void ClearIndex(McpCommandContext context)
        {
            lock (s_lock)
            {
                s_entries.Clear();
                s_byPath.Clear();
                s_byGuid.Clear();
                s_builtAtUtc = DateTime.MinValue;
                s_loadedFromCache = false;
            }

            foreach (var cachePath in CachePathsToDelete(context))
            {
                try
                {
                    if (File.Exists(cachePath))
                    {
                        File.Delete(cachePath);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }

        internal static void InvalidateCache()
        {
            ClearIndex(null);
        }

        static string StatusJson(McpCommandContext context, bool rebuilt)
        {
            bool indexed;
            int count;
            DateTime builtAtUtc;
            bool loadedFromCache;
            lock (s_lock)
            {
                indexed = s_builtAtUtc != DateTime.MinValue;
                count = s_entries.Count;
                builtAtUtc = s_builtAtUtc;
                loadedFromCache = s_loadedFromCache;
            }

            var cacheLocation = CacheLocation(context);
            return "{\"indexed\":" + JsonUtil.Bool(indexed)
                + ",\"asset_count\":" + count
                + ",\"built_at_utc\":" + (indexed ? JsonUtil.String(builtAtUtc.ToString("o")) : "null")
                + ",\"roots\":[\"Assets\",\"Packages\"]"
                + ",\"cache_supported\":true"
                + ",\"cache_configured\":" + JsonUtil.Bool(cacheLocation.Configured)
                + ",\"cache_loaded\":" + JsonUtil.Bool(loadedFromCache)
                + ",\"cache_path\":" + JsonUtil.String(cacheLocation.DisplayPath)
                + ",\"rebuilt\":" + JsonUtil.Bool(rebuilt) + "}";
        }

        static string SemanticStatusJson(McpCommandContext context)
        {
            bool indexed;
            int count;
            DateTime builtAtUtc;
            bool loadedFromCache;
            lock (s_lock)
            {
                indexed = s_builtAtUtc != DateTime.MinValue;
                count = s_entries.Count;
                builtAtUtc = s_builtAtUtc;
                loadedFromCache = s_loadedFromCache;
            }

            var cacheLocation = CacheLocation(context);
            return "{\"available\":true"
                + ",\"index_kind\":\"deterministic_lexical_overlay\""
                + ",\"backing_index\":\"asset_index\""
                + ",\"indexed\":" + JsonUtil.Bool(indexed)
                + ",\"asset_count\":" + count
                + ",\"built_at_utc\":" + (indexed ? JsonUtil.String(builtAtUtc.ToString("o")) : "null")
                + ",\"roots\":[\"Assets\",\"Packages\"]"
                + ",\"cache_supported\":true"
                + ",\"cache_configured\":" + JsonUtil.Bool(cacheLocation.Configured)
                + ",\"cache_loaded\":" + JsonUtil.Bool(loadedFromCache)
                + ",\"cache_path\":" + JsonUtil.String(cacheLocation.DisplayPath)
                + ",\"local_only\":true"
                + ",\"network_access\":false"
                + ",\"embedding_provider\":\"none\""
                + ",\"tokenizer\":\"case_insensitive_ascii_word_tokens\""
                + ",\"metadata_sources\":[\"path\",\"name\",\"type\",\"extension\",\"guid\",\"source\",\"label\",\"importer\",\"script_symbol\"]"
                + ",\"limits\":{\"max_results\":200,\"max_scan_assets\":" + MaxSemanticScanAssets + ",\"max_query_tokens\":" + MaxSemanticQueryTokens + ",\"max_script_bytes\":" + MaxSemanticScriptBytes + "}}";
        }

        static bool TryLoadCache(McpCommandContext context)
        {
            lock (s_lock)
            {
                if (s_builtAtUtc != DateTime.MinValue)
                {
                    return false;
                }
            }

            var cachePath = CacheLocation(context).Path;
            if (!File.Exists(cachePath))
            {
                return false;
            }

            try
            {
                var lines = File.ReadAllLines(cachePath);
                if (lines.Length < 2 || lines[0] != CacheHeader || !lines[1].StartsWith("built_at_utc\t", StringComparison.Ordinal))
                {
                    return false;
                }

                if (!DateTime.TryParse(lines[1].Substring("built_at_utc\t".Length), null, System.Globalization.DateTimeStyles.RoundtripKind, out var builtAtUtc))
                {
                    return false;
                }

                var entries = new List<AssetIndexEntry>();
                for (var index = 2; index < lines.Length; index++)
                {
                    var parts = lines[index].Split('\t');
                    if (parts.Length != 6)
                    {
                        continue;
                    }

                    entries.Add(new AssetIndexEntry
                    {
                        Path = UnescapeCacheField(parts[0]),
                        Guid = UnescapeCacheField(parts[1]),
                        Name = UnescapeCacheField(parts[2]),
                        Extension = UnescapeCacheField(parts[3]),
                        Type = UnescapeCacheField(parts[4]),
                        Source = UnescapeCacheField(parts[5]),
                    });
                }

                entries.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.Ordinal));
                lock (s_lock)
                {
                    s_entries.Clear();
                    s_byPath.Clear();
                    s_byGuid.Clear();
                    foreach (var entry in entries)
                    {
                        s_entries.Add(entry);
                        s_byPath[entry.Path] = entry;
                        if (!string.IsNullOrEmpty(entry.Guid))
                        {
                            s_byGuid[entry.Guid] = entry;
                        }
                    }

                    s_builtAtUtc = builtAtUtc;
                    s_loadedFromCache = true;
                }

                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        static void WriteCache(List<AssetIndexEntry> entries, DateTime builtAtUtc, McpCommandContext context)
        {
            try
            {
                var cachePath = CacheLocation(context).Path;
                var directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var builder = new StringBuilder();
                builder.AppendLine(CacheHeader);
                builder.Append("built_at_utc\t").AppendLine(builtAtUtc.ToString("o"));
                foreach (var entry in entries)
                {
                    builder.Append(EscapeCacheField(entry.Path)).Append('\t');
                    builder.Append(EscapeCacheField(entry.Guid)).Append('\t');
                    builder.Append(EscapeCacheField(entry.Name)).Append('\t');
                    builder.Append(EscapeCacheField(entry.Extension)).Append('\t');
                    builder.Append(EscapeCacheField(entry.Type)).Append('\t');
                    builder.Append(EscapeCacheField(entry.Source)).AppendLine();
                }

                File.WriteAllText(cachePath, builder.ToString());
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static IEnumerable<string> CachePathsToDelete(McpCommandContext context)
        {
            var paths = new List<string>();
            AddUniquePath(paths, CacheLocation(context).Path);
            AddUniquePath(paths, DefaultCachePath());
            AddUniquePath(paths, LastCachePath());
            return paths;
        }

        static CacheLocationInfo CacheLocation(McpCommandContext context)
        {
            var configuredDirectory = CacheDirectorySetting(context);
            var configured = !string.IsNullOrEmpty(configuredDirectory);
            var accepted = false;
            var directory = ResolveCacheDirectory(configuredDirectory, out accepted);
            var cachePath = Path.Combine(directory, CacheFileName);
            RememberCachePath(cachePath);

            return new CacheLocationInfo
            {
                Configured = configured && accepted,
                DisplayPath = configured && accepted ? DisplayCachePath(configuredDirectory) : DefaultCacheDisplayPath(),
                Path = cachePath,
            };
        }

        static string CacheDirectorySetting(McpCommandContext context)
        {
            if (context != null)
            {
                var configuredDirectory = JsonUtil.ExtractString(context.SettingsJson, SettingsAssetIndexCacheDir);
                if (!string.IsNullOrEmpty(configuredDirectory))
                {
                    EditorPrefs.SetString(EditorPrefsCacheDirKey, configuredDirectory);
                    return configuredDirectory;
                }

                EditorPrefs.DeleteKey(EditorPrefsCacheDirKey);
                return string.Empty;
            }

            return EditorPrefs.GetString(EditorPrefsCacheDirKey, string.Empty);
        }

        static string ResolveCacheDirectory(string configuredDirectory, out bool accepted)
        {
            accepted = false;
            if (string.IsNullOrEmpty(configuredDirectory))
            {
                return DefaultCacheDirectory();
            }

            try
            {
                var normalized = configuredDirectory.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                if (Path.IsPathRooted(normalized))
                {
                    accepted = true;
                    return Path.GetFullPath(normalized);
                }

                var fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, normalized));
                var libraryRoot = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, "Library"));
                if (IsSameOrChildPath(fullPath, libraryRoot))
                {
                    accepted = true;
                    return fullPath;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (NotSupportedException)
            {
            }

            return DefaultCacheDirectory();
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static string DefaultCachePath()
        {
            return Path.Combine(DefaultCacheDirectory(), CacheFileName);
        }

        static string DefaultCacheDirectory()
        {
            return Path.Combine(ProjectPaths.ProjectRoot, DefaultCacheRelativeDirectory.Replace('/', Path.DirectorySeparatorChar));
        }

        static string DefaultCacheDisplayPath()
        {
            return DefaultCacheRelativeDirectory + "/" + CacheFileName;
        }

        static string DisplayCachePath(string configuredDirectory)
        {
            return configuredDirectory.Replace('\\', '/').TrimEnd('/') + "/" + CacheFileName;
        }

        static void RememberCachePath(string cachePath)
        {
            lock (s_lock)
            {
                s_lastCachePath = cachePath;
            }
        }

        static string LastCachePath()
        {
            lock (s_lock)
            {
                return s_lastCachePath;
            }
        }

        static void AddUniquePath(List<string> paths, string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            for (var index = 0; index < paths.Count; index++)
            {
                if (string.Equals(paths[index], path, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            paths.Add(path);
        }

        static string EscapeCacheField(string value)
        {
            return (value ?? string.Empty).Replace("\\", "\\\\").Replace("\t", "\\t").Replace("\r", "\\r").Replace("\n", "\\n");
        }

        static string UnescapeCacheField(string value)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                if (current != '\\' || index + 1 >= value.Length)
                {
                    builder.Append(current);
                    continue;
                }

                var next = value[++index];
                switch (next)
                {
                    case 't': builder.Append('\t'); break;
                    case 'r': builder.Append('\r'); break;
                    case 'n': builder.Append('\n'); break;
                    case '\\': builder.Append('\\'); break;
                    default: builder.Append(next); break;
                }
            }

            return builder.ToString();
        }

        static CommandExecutionResult ResolveTargetAsset(string argsJson, out AssetIndexEntry target)
        {
            target = null;
            var rawPath = JsonUtil.ExtractString(argsJson, "asset_path");
            var guid = JsonUtil.ExtractString(argsJson, "guid");
            if (string.IsNullOrEmpty(rawPath) && string.IsNullOrEmpty(guid))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument asset_path or guid is required");
            }

            if (!string.IsNullOrEmpty(rawPath))
            {
                var pathResult = RequireAssetPath(argsJson, "asset_path", out var assetPath);
                if (pathResult != null)
                {
                    return pathResult;
                }

                var actualGuid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!string.IsNullOrEmpty(guid) && !string.Equals(actualGuid, guid, StringComparison.OrdinalIgnoreCase))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "asset_path and guid do not refer to the same asset");
                }

                target = EntryFromPath(assetPath, actualGuid);
                return null;
            }

            var resolvedPath = AssetDatabase.GUIDToAssetPath(guid);
            if (string.IsNullOrEmpty(resolvedPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "GUID was not found");
            }

            target = EntryFromPath(resolvedPath, guid);
            return null;
        }

        static CommandExecutionResult RequireAssetPath(string argsJson, string field, out string assetPath)
        {
            assetPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                return CommandExecutionResult.Error("asset_not_found", "Asset path was not found");
            }

            return null;
        }

        static CommandExecutionResult RequireRelocationPaths(string argsJson, out string sourcePath, out string destinationPath)
        {
            sourcePath = string.Empty;
            destinationPath = string.Empty;
            var rawSourcePath = JsonUtil.ExtractString(argsJson, "source_path");
            var rawDestinationPath = JsonUtil.ExtractString(argsJson, "destination_path");
            if (string.IsNullOrEmpty(rawSourcePath) || string.IsNullOrEmpty(rawDestinationPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Arguments source_path and destination_path are required");
            }

            if (rawSourcePath.Length > 512 || rawDestinationPath.Length > 512)
            {
                return CommandExecutionResult.Error("invalid_field_value", "source_path and destination_path must be at most 512 characters");
            }

            try
            {
                sourcePath = PathGuard.RequireUnityAssetPath(rawSourcePath);
                destinationPath = PathGuard.RequireUnityAssetPath(rawDestinationPath);
            }
            catch (ArgumentException)
            {
                sourcePath = string.Empty;
                destinationPath = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", "source_path and destination_path must be Unity-relative paths under Assets/ or Packages/");
            }

            if (sourcePath == "Assets" || sourcePath == "Packages")
            {
                return CommandExecutionResult.Error("invalid_field_value", "source_path must reference an asset or child folder under Assets/ or Packages/");
            }

            if (string.Equals(sourcePath, destinationPath, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "source_path and destination_path must differ");
            }

            return null;
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
            if (normalized == "Assets" || normalized == "Assets/")
            {
                return "Assets/";
            }

            if (normalized == "Packages" || normalized == "Packages/")
            {
                return "Packages/";
            }

            var hadTrailingSlash = normalized.EndsWith("/", StringComparison.Ordinal);
            var candidate = hadTrailingSlash ? normalized.TrimEnd('/') : normalized;
            candidate = PathGuard.RequireUnityAssetPath(candidate);
            return hadTrailingSlash ? candidate + "/" : candidate;
        }

        static CommandExecutionResult RequireSerializedInspectArgs(string argsJson, out string propertyPath, out int maxDepth, out int maxArrayElements, out int maxChars)
        {
            propertyPath = JsonUtil.ExtractString(argsJson, "property_path");
            maxDepth = Clamp(JsonUtil.ExtractInt(argsJson, "max_depth", DefaultSerializedMaxDepth), 1, 6);
            maxArrayElements = Clamp(JsonUtil.ExtractInt(argsJson, "max_array_elements", DefaultSerializedMaxArrayElements), 1, 20);
            maxChars = Clamp(JsonUtil.ExtractInt(argsJson, "max_chars", DefaultSerializedMaxChars), 256, 8192);

            if (string.IsNullOrEmpty(propertyPath))
            {
                return null;
            }

            propertyPath = propertyPath.Trim();
            if (propertyPath.Length < 1 || propertyPath.Length > MaxSerializedPropertyPathLength)
            {
                return CommandExecutionResult.Error("invalid_field_value", "property_path must be 1..512 characters");
            }

            if (PropertyPathLooksUnsafe(propertyPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "property_path must be a SerializedProperty path returned by this command, not a URL or filesystem path");
            }

            return null;
        }

        static bool PropertyPathLooksUnsafe(string propertyPath)
        {
            if (propertyPath.IndexOf("://", StringComparison.Ordinal) >= 0 || propertyPath.IndexOf('\\') >= 0)
            {
                return true;
            }

            for (var index = 0; index < propertyPath.Length; index++)
            {
                var character = propertyPath[index];
                if (char.IsControl(character) || character == '"' || character == '{' || character == '}')
                {
                    return true;
                }
            }

            return false;
        }

        static AssetIndexEntry[] EntriesSnapshot()
        {
            lock (s_lock)
            {
                return s_entries.ToArray();
            }
        }

        static AssetIndexEntry EntryFromPath(string assetPath)
        {
            return EntryFromPath(assetPath, AssetDatabase.AssetPathToGUID(assetPath));
        }

        static AssetIndexEntry EntryFromPath(string assetPath, string guid)
        {
            lock (s_lock)
            {
                if (s_byPath.TryGetValue(assetPath, out var indexedByPath))
                {
                    return indexedByPath;
                }

                if (!string.IsNullOrEmpty(guid) && s_byGuid.TryGetValue(guid, out var indexedByGuid))
                {
                    return indexedByGuid;
                }
            }

            var type = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            return new AssetIndexEntry
            {
                Path = assetPath,
                Guid = guid ?? string.Empty,
                Name = Path.GetFileNameWithoutExtension(assetPath),
                Extension = Path.GetExtension(assetPath),
                Type = type == null ? string.Empty : type.Name,
                Source = SourceForPath(assetPath),
            };
        }

        static bool DependenciesContain(string assetPath, string targetPath, bool recursive)
        {
            var dependencies = AssetDatabase.GetDependencies(assetPath, recursive);
            for (var index = 0; index < dependencies.Length; index++)
            {
                if (string.Equals(dependencies[index], targetPath, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static void AppendOutgoingDependencyEdges(StringBuilder builder, AssetIndexEntry target, bool recursive, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            var dependencies = AssetDatabase.GetDependencies(target.Path, recursive);
            Array.Sort(dependencies, StringComparer.Ordinal);
            for (var index = 0; index < dependencies.Length; index++)
            {
                var dependencyPath = dependencies[index];
                if (string.Equals(dependencyPath, target.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!AppendDependencyEdge(builder, "asset_depends_on_asset", "outgoing", target, EntryFromPath(dependencyPath), recursive ? "AssetDatabase.GetDependencies(recursive)" : "AssetDatabase.GetDependencies(direct)", 0, maxEdges, ref edgeCount, ref truncated))
                {
                    return;
                }
            }
        }

        static void AppendIncomingDependencyEdges(StringBuilder builder, AssetIndexEntry target, bool recursive, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            foreach (var entry in EntriesSnapshot())
            {
                if (string.Equals(entry.Path, target.Path, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!DependenciesContain(entry.Path, target.Path, recursive))
                {
                    continue;
                }

                if (!AppendDependencyEdge(builder, "asset_depends_on_asset", "incoming", entry, target, recursive ? "reverse AssetDatabase.GetDependencies(recursive)" : "reverse AssetDatabase.GetDependencies(direct)", 0, maxEdges, ref edgeCount, ref truncated))
                {
                    return;
                }
            }
        }

        static void AppendYamlGuidReferenceEdges(StringBuilder builder, AssetIndexEntry target, int maxEdges, int maxReferences, ref int edgeCount, ref bool truncated, out int referenceCount, out bool referencesTruncated)
        {
            referenceCount = 0;
            referencesTruncated = false;
            foreach (var reference in YamlGuidReferences(target.Path, maxReferences, out referencesTruncated))
            {
                referenceCount++;
                if (!AppendYamlGuidReferenceEdge(builder, target, reference, maxEdges, ref edgeCount, ref truncated))
                {
                    return;
                }
            }
        }

        static bool AppendDependencyEdge(StringBuilder builder, string edgeKind, string direction, AssetIndexEntry source, AssetIndexEntry target, string via, int line, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            if (edgeCount >= maxEdges)
            {
                truncated = true;
                return false;
            }

            if (edgeCount > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"edge_kind\":").Append(JsonUtil.String(edgeKind));
            builder.Append(",\"direction\":").Append(JsonUtil.String(direction));
            builder.Append(",\"source\":").Append(EntryJson(source));
            builder.Append(",\"target\":").Append(EntryJson(target));
            builder.Append(",\"via\":").Append(JsonUtil.String(via));
            if (line > 0)
            {
                builder.Append(",\"line\":").Append(line);
            }

            builder.Append('}');
            edgeCount++;
            return true;
        }

        static bool AppendYamlGuidReferenceEdge(StringBuilder builder, AssetIndexEntry source, YamlGuidReference reference, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            if (edgeCount >= maxEdges)
            {
                truncated = true;
                return false;
            }

            if (edgeCount > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"edge_kind\":\"yaml_guid_reference\"");
            builder.Append(",\"direction\":\"outgoing\"");
            builder.Append(",\"source\":").Append(EntryJson(source));
            builder.Append(",\"target_guid\":").Append(JsonUtil.String(reference.Guid));
            builder.Append(",\"target_path\":").Append(string.IsNullOrEmpty(reference.TargetPath) ? "null" : JsonUtil.String(reference.TargetPath));
            builder.Append(",\"target_exists\":").Append(JsonUtil.Bool(!string.IsNullOrEmpty(reference.TargetPath)));
            builder.Append(",\"via\":\"Unity YAML guid reference\"");
            builder.Append(",\"line\":").Append(reference.Line);
            builder.Append('}');
            edgeCount++;
            return true;
        }

        static void AppendAsmdefAssemblyReferenceEdges(StringBuilder builder, AssetIndexEntry target, int maxEdges, ref int edgeCount, ref bool truncated, out int referenceCount, out bool referencesTruncated)
        {
            referenceCount = 0;
            referencesTruncated = false;
            var scanTruncated = false;
            if (!target.Path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) || !TryReadAsmdefContent(target.Path, out var content, out scanTruncated))
            {
                referencesTruncated = scanTruncated;
                return;
            }

            var references = ReadAsmdefStringArray(content, "references", MaxDependencyAsmdefReferences, out var arrayTruncated);
            referencesTruncated = scanTruncated || arrayTruncated;
            var sourceAssembly = DependencyAssemblyFromAsmdefPath(target.Path, content, "target_asmdef");
            for (var index = 0; index < references.Count; index++)
            {
                referenceCount++;
                var reference = references[index];
                var targetAssembly = ResolveReferencedAssembly(reference, out var referenceKind, out var targetExists);
                if (!AppendAsmdefAssemblyReferenceEdge(builder, target, sourceAssembly, targetAssembly, reference, referenceKind, targetExists, maxEdges, ref edgeCount, ref truncated))
                {
                    referencesTruncated = true;
                    return;
                }
            }
        }

        static bool AppendAsmdefAssemblyReferenceEdge(StringBuilder builder, AssetIndexEntry source, DependencyAssemblyPrediction sourceAssembly, DependencyAssemblyPrediction targetAssembly, string reference, string referenceKind, bool targetExists, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            if (edgeCount >= maxEdges)
            {
                truncated = true;
                return false;
            }

            if (edgeCount > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"edge_kind\":\"assembly_references_assembly\"");
            builder.Append(",\"direction\":\"outgoing\"");
            builder.Append(",\"source\":").Append(EntryJson(source));
            builder.Append(",\"source_assembly\":").Append(sourceAssembly.ToJson());
            builder.Append(",\"target_assembly\":").Append(targetAssembly.ToJson());
            builder.Append(",\"reference\":").Append(JsonUtil.String(reference));
            builder.Append(",\"reference_kind\":").Append(JsonUtil.String(referenceKind));
            builder.Append(",\"target_exists\":").Append(JsonUtil.Bool(targetExists));
            builder.Append(",\"via\":\"asmdef references field\"}");
            edgeCount++;
            return true;
        }

        static DependencyAssemblyPrediction ResolveReferencedAssembly(string reference, out string referenceKind, out bool targetExists)
        {
            targetExists = false;
            if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
            {
                referenceKind = "guid";
                var guid = reference.Substring("GUID:".Length);
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase) && TryReadAsmdefContent(path, out var content, out var ignoredScanTruncated))
                {
                    targetExists = true;
                    return DependencyAssemblyFromAsmdefPath(path, content, "asmdef_reference_guid");
                }

                return new DependencyAssemblyPrediction(string.Empty, reference, false, false, "unresolved_asmdef_reference");
            }

            referenceKind = "name";
            var entries = EntriesSnapshot();
            Array.Sort(entries, CompareEntriesByPath);
            var scanned = 0;
            for (var index = 0; index < entries.Length && scanned < MaxAsmdefReferenceScan; index++)
            {
                var entry = entries[index];
                if (!entry.Path.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                scanned++;
                if (!TryReadAsmdefContent(entry.Path, out var content, out var ignoredScanTruncated))
                {
                    continue;
                }

                var assembly = DependencyAssemblyFromAsmdefPath(entry.Path, content, "asmdef_reference_name");
                if (string.Equals(assembly.AssemblyName, reference, StringComparison.Ordinal))
                {
                    targetExists = true;
                    return assembly;
                }
            }

            return new DependencyAssemblyPrediction(string.Empty, reference, false, false, "unresolved_asmdef_reference");
        }

        static DependencyAssemblyPrediction DependencyAssemblyFromAsmdefPath(string asmdefPath, string content, string reason)
        {
            return new DependencyAssemblyPrediction(asmdefPath, ReadAsmdefName(asmdefPath, content), false, IsEditorOnlyAsmdef(content) || IsUnderEditorFolder(asmdefPath), reason);
        }

        static bool TryReadAsmdefContent(string asmdefPath, out string content, out bool scanTruncated)
        {
            content = string.Empty;
            scanTruncated = false;
            if (!TryResolveAssetFilePath(asmdefPath, out var fullPath))
            {
                return false;
            }

            var bytes = ReadBoundedBytes(fullPath, MaxDependencyAsmdefBytes, out var ignoredByteLength, out scanTruncated);
            var textResult = DecodeText(bytes, scanTruncated, out content);
            if (textResult != null)
            {
                content = string.Empty;
                return false;
            }

            return true;
        }

        static int CompareEntriesByPath(AssetIndexEntry left, AssetIndexEntry right)
        {
            return string.Compare(left == null ? string.Empty : left.Path, right == null ? string.Empty : right.Path, StringComparison.Ordinal);
        }

        static string BuildScriptDependencyContext(StringBuilder builder, AssetIndexEntry target, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            if (!target.Path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return "null";
            }

            if (!TryResolveAssetFilePath(target.Path, out var fullPath))
            {
                return "{\"kind\":\"script\",\"available\":false,\"error_code\":\"asset_file_not_found\"}";
            }

            var bytes = ReadBoundedBytes(fullPath, MaxDependencyScriptBytes, out var byteLength, out var scanTruncated);
            var textResult = DecodeText(bytes, scanTruncated, out var content);
            if (textResult != null)
            {
                return "{\"kind\":\"script\",\"available\":false,\"error_code\":" + JsonUtil.String(textResult.ErrorCode)
                    + ",\"error_message\":" + JsonUtil.String(textResult.ErrorMessage) + "}";
            }

            var declaredTypes = ExtractDependencyDeclaredTypes(content, MaxDependencyDeclaredTypes, out var declaredTypesTruncated);
            var scriptRelationships = ExtractDependencyScriptRelationships(content, MaxDependencyScriptRelationships, out var scriptRelationshipsTruncated);
            var usingNamespaces = ExtractDependencyUsingNamespaces(content, MaxDependencyUsingNamespaces, out var usingNamespacesTruncated);
            var assembly = PredictDependencyAssembly(target.Path);
            AppendScriptAssemblyEdge(builder, target, assembly, maxEdges, ref edgeCount, ref truncated);
            for (var index = 0; index < declaredTypes.Count && !truncated; index++)
            {
                AppendScriptDeclaredTypeEdge(builder, target, declaredTypes[index], maxEdges, ref edgeCount, ref truncated);
            }

            for (var index = 0; index < scriptRelationships.Count && !truncated; index++)
            {
                AppendScriptRelationshipEdge(builder, target, scriptRelationships[index], maxEdges, ref edgeCount, ref truncated);
            }

            var json = new StringBuilder();
            json.Append("{\"kind\":\"script\",\"available\":true");
            json.Append(",\"script_bytes\":").Append(byteLength);
            json.Append(",\"script_scan_truncated\":").Append(JsonUtil.Bool(scanTruncated));
            json.Append(",\"assembly\":").Append(assembly.ToJson());
            json.Append(",\"declared_type_count\":").Append(declaredTypes.Count);
            json.Append(",\"declared_types_truncated\":").Append(JsonUtil.Bool(declaredTypesTruncated));
            json.Append(",\"declared_types\":[");
            for (var index = 0; index < declaredTypes.Count; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append(declaredTypes[index].ToJson());
            }

            json.Append("],\"using_namespace_count\":").Append(usingNamespaces.Count);
            json.Append(",\"using_namespaces_truncated\":").Append(JsonUtil.Bool(usingNamespacesTruncated));
            json.Append(",\"using_namespaces\":[").Append(StringArrayJson(usingNamespaces)).Append(']');
            json.Append(",\"script_relationship_count\":").Append(scriptRelationships.Count);
            json.Append(",\"script_relationships_truncated\":").Append(JsonUtil.Bool(scriptRelationshipsTruncated));
            json.Append(",\"script_relationships\":[");
            for (var index = 0; index < scriptRelationships.Count; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append(scriptRelationships[index].ToJson());
            }

            json.Append("],\"warnings\":[\"using directives are reported as namespace context only and do not add assembly references; asmdef references control cross-assembly access.\",\"script relationship edges are bounded best-effort lexical scan results; ambiguous short type names are not resolved to assemblies.\"]}");
            return json.ToString();
        }

        static bool AppendScriptAssemblyEdge(StringBuilder builder, AssetIndexEntry source, DependencyAssemblyPrediction assembly, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            if (builder == null || source == null || assembly == null)
            {
                return true;
            }

            if (edgeCount >= maxEdges)
            {
                truncated = true;
                return false;
            }

            if (edgeCount > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"edge_kind\":\"script_compiles_into_assembly\"");
            builder.Append(",\"direction\":\"outgoing\"");
            builder.Append(",\"source\":").Append(EntryJson(source));
            builder.Append(",\"target_assembly\":").Append(assembly.ToJson());
            builder.Append(",\"via\":\"Unity script assembly prediction\"}");
            edgeCount++;
            return true;
        }

        static bool AppendScriptDeclaredTypeEdge(StringBuilder builder, AssetIndexEntry source, DependencyDeclaredType declaredType, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            if (edgeCount >= maxEdges)
            {
                truncated = true;
                return false;
            }

            if (edgeCount > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"edge_kind\":\"script_declares_type\"");
            builder.Append(",\"direction\":\"outgoing\"");
            builder.Append(",\"source\":").Append(EntryJson(source));
            builder.Append(",\"target_type\":").Append(declaredType.ToJson());
            builder.Append(",\"via\":\"bounded C# lexical declaration scan\"}");
            edgeCount++;
            return true;
        }

        static bool AppendScriptRelationshipEdge(StringBuilder builder, AssetIndexEntry source, DependencyScriptRelationship relationship, int maxEdges, ref int edgeCount, ref bool truncated)
        {
            if (edgeCount >= maxEdges)
            {
                truncated = true;
                return false;
            }

            if (edgeCount > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"edge_kind\":").Append(JsonUtil.String(relationship.EdgeKind));
            builder.Append(",\"direction\":\"outgoing\"");
            builder.Append(",\"source\":").Append(EntryJson(source));
            builder.Append(",\"target_type\":").Append(relationship.TypeReference.ToJson());
            builder.Append(",\"via\":\"bounded C# lexical relationship scan\"");
            builder.Append(",\"confidence\":").Append(JsonUtil.String(relationship.TypeReference.Confidence));
            builder.Append(",\"source_kind\":").Append(JsonUtil.String(relationship.TypeReference.Source));
            if (relationship.Line > 0)
            {
                builder.Append(",\"line\":").Append(relationship.Line);
            }

            builder.Append('}');
            edgeCount++;
            return true;
        }

        static DependencyAssemblyPrediction PredictDependencyAssembly(string scriptPath)
        {
            var underEditorFolder = IsUnderEditorFolder(scriptPath);
            var directory = ParentAssetPath(scriptPath);
            while (!string.IsNullOrEmpty(directory) && IsUnityAssetDirectory(directory))
            {
                var asmdef = FirstAsmdefPath(directory);
                if (!string.IsNullOrEmpty(asmdef))
                {
                    var asmdefContent = string.Empty;
                    if (TryResolveAssetFilePath(asmdef, out var asmdefFullPath))
                    {
                        var bytes = ReadBoundedBytes(asmdefFullPath, MaxDependencyAsmdefBytes, out var ignoredByteLength, out var scanTruncated);
                        var textResult = DecodeText(bytes, scanTruncated, out asmdefContent);
                        if (textResult != null)
                        {
                            asmdefContent = string.Empty;
                        }
                    }

                    return new DependencyAssemblyPrediction(asmdef, ReadAsmdefName(asmdef, asmdefContent), false, IsEditorOnlyAsmdef(asmdefContent) || underEditorFolder, "nearest_asmdef");
                }

                directory = ParentAssetPath(directory);
            }

            return new DependencyAssemblyPrediction(string.Empty, underEditorFolder ? "Assembly-CSharp-Editor" : "Assembly-CSharp", true, underEditorFolder, underEditorFolder ? "editor_folder_without_asmdef" : "runtime_folder_without_asmdef");
        }

        static string FirstAsmdefPath(string assetDirectory)
        {
            if (!TryResolveAssetDirectoryPath(assetDirectory, out var fullDirectory))
            {
                return string.Empty;
            }

            var files = Directory.GetFiles(fullDirectory, "*.asmdef", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);
            return files.Length == 0 ? string.Empty : assetDirectory.TrimEnd('/') + "/" + Path.GetFileName(files[0]);
        }

        static List<DependencyDeclaredType> ExtractDependencyDeclaredTypes(string content, int maxTypes, out bool truncated)
        {
            truncated = false;
            var types = new List<DependencyDeclaredType>();
            var regex = new Regex("\\b(class|struct|interface|enum)\\s+([A-Za-z_][A-Za-z0-9_]*)\\b|\\brecord\\s+(?:(class|struct)\\s+)?([A-Za-z_][A-Za-z0-9_]*)\\b", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            var strippedContent = RemoveComments(content);
            foreach (Match match in regex.Matches(strippedContent))
            {
                if (types.Count >= maxTypes)
                {
                    truncated = true;
                    break;
                }

                var kind = match.Groups[1].Success ? match.Groups[1].Value : (match.Groups[3].Success ? "record " + match.Groups[3].Value : "record");
                var name = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
                types.Add(new DependencyDeclaredType(kind, name, NamespaceBefore(strippedContent, match.Index)));
            }

            return types;
        }

        static List<string> ExtractDependencyUsingNamespaces(string content, int maxNamespaces, out bool truncated)
        {
            truncated = false;
            var namespaces = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var strippedContent = RemoveCommentsAndStrings(content);
            var regex = new Regex("^\\s*using\\s+(?!static\\b)([A-Za-z_][A-Za-z0-9_]*(?:\\.[A-Za-z_][A-Za-z0-9_]*)*)\\s*;", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            foreach (Match match in regex.Matches(strippedContent))
            {
                if (namespaces.Count >= maxNamespaces)
                {
                    truncated = true;
                    break;
                }

                var namespaceName = match.Groups[1].Value;
                if (seen.Add(namespaceName))
                {
                    namespaces.Add(namespaceName);
                }
            }

            return namespaces;
        }

        static List<DependencyScriptRelationship> ExtractDependencyScriptRelationships(string content, int maxRelationships, out bool truncated)
        {
            truncated = false;
            var relationships = new List<DependencyScriptRelationship>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var strippedContent = RemoveCommentsAndStrings(content);

            ExtractInheritanceRelationships(strippedContent, relationships, seen, maxRelationships, ref truncated);
            ExtractMemberTypeRelationships(strippedContent, relationships, seen, maxRelationships, ref truncated);
            ExtractUseTypeRelationships(strippedContent, relationships, seen, maxRelationships, ref truncated);
            return relationships;
        }

        static void ExtractInheritanceRelationships(string content, List<DependencyScriptRelationship> relationships, HashSet<string> seen, int maxRelationships, ref bool truncated)
        {
            var regex = new Regex("\\b(class|struct|interface|record)\\s+[A-Za-z_][A-Za-z0-9_]*(?:\\s*<[^>{}]+>)?\\s*:\\s*([^\\{;]+)", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            foreach (Match match in regex.Matches(content))
            {
                var declaredKind = match.Groups[1].Value;
                var bases = SplitTopLevelComma(match.Groups[2].Value);
                for (var index = 0; index < bases.Count; index++)
                {
                    var typeName = NormalizeDependencyTypeName(bases[index]);
                    if (string.IsNullOrEmpty(typeName))
                    {
                        continue;
                    }

                    var edgeKind = index == 0 && !string.Equals(declaredKind, "interface", StringComparison.Ordinal) ? "script_inherits_type" : "script_implements_interface";
                    AddScriptRelationship(relationships, seen, edgeKind, typeName, LineNumber(content, match.Index), maxRelationships, ref truncated);
                    if (truncated)
                    {
                        return;
                    }
                }
            }
        }

        static void ExtractMemberTypeRelationships(string content, List<DependencyScriptRelationship> relationships, HashSet<string> seen, int maxRelationships, ref bool truncated)
        {
            var propertyRegex = new Regex("\\b([A-Za-z_][A-Za-z0-9_.<>\\[\\]?]*)\\s+[A-Za-z_][A-Za-z0-9_]*\\s*\\{\\s*(?:get|set|init)\\b", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            foreach (Match match in propertyRegex.Matches(content))
            {
                AddScriptRelationship(relationships, seen, "script_declares_property_type", NormalizeDependencyTypeName(match.Groups[1].Value), LineNumber(content, match.Index), maxRelationships, ref truncated);
                if (truncated)
                {
                    return;
                }
            }

            var methodRegex = new Regex("\\b([A-Za-z_][A-Za-z0-9_.<>\\[\\]?]*)\\s+[A-Za-z_][A-Za-z0-9_]*\\s*\\(([^)]*)\\)", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            foreach (Match match in methodRegex.Matches(content))
            {
                var returnType = NormalizeDependencyTypeName(match.Groups[1].Value);
                if (!IsIgnoredDependencyType(returnType))
                {
                    AddScriptRelationship(relationships, seen, "script_declares_method_return_type", returnType, LineNumber(content, match.Index), maxRelationships, ref truncated);
                    if (truncated)
                    {
                        return;
                    }
                }

                var parameters = SplitTopLevelComma(match.Groups[2].Value);
                for (var index = 0; index < parameters.Count; index++)
                {
                    var parameterType = NormalizeDependencyParameterType(parameters[index]);
                    if (IsIgnoredDependencyType(parameterType))
                    {
                        continue;
                    }

                    AddScriptRelationship(relationships, seen, "script_declares_method_parameter_type", parameterType, LineNumber(content, match.Index), maxRelationships, ref truncated);
                    if (truncated)
                    {
                        return;
                    }
                }
            }

            var fieldRegex = new Regex("(?:^|[;{}]\\s*)(?:public|private|protected|internal|static|readonly|const|volatile|new|sealed|unsafe|event|partial|extern|async|virtual|override|abstract|[\\s])*\\b([A-Za-z_][A-Za-z0-9_.<>\\[\\]?]*)\\s+[A-Za-z_][A-Za-z0-9_]*\\s*(?:=|;)", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            foreach (Match match in fieldRegex.Matches(content))
            {
                AddScriptRelationship(relationships, seen, "script_declares_field_type", NormalizeDependencyTypeName(match.Groups[1].Value), LineNumber(content, match.Index), maxRelationships, ref truncated);
                if (truncated)
                {
                    return;
                }
            }
        }

        static void ExtractUseTypeRelationships(string content, List<DependencyScriptRelationship> relationships, HashSet<string> seen, int maxRelationships, ref bool truncated)
        {
            var regex = new Regex("\\b(?:new|typeof|nameof)\\s*(?:\\(|\\s+)\\s*([A-Za-z_][A-Za-z0-9_.<>]*)", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            foreach (Match match in regex.Matches(content))
            {
                AddScriptRelationship(relationships, seen, "script_uses_type", NormalizeDependencyTypeName(match.Groups[1].Value), LineNumber(content, match.Index), maxRelationships, ref truncated);
                if (truncated)
                {
                    return;
                }
            }
        }

        static void AddScriptRelationship(List<DependencyScriptRelationship> relationships, HashSet<string> seen, string edgeKind, string typeName, int line, int maxRelationships, ref bool truncated)
        {
            if (string.IsNullOrEmpty(typeName) || IsIgnoredDependencyType(typeName))
            {
                return;
            }

            if (relationships.Count >= maxRelationships)
            {
                truncated = true;
                return;
            }

            var key = edgeKind + "|" + typeName + "|" + line.ToString(CultureInfo.InvariantCulture);
            if (!seen.Add(key))
            {
                return;
            }

            relationships.Add(new DependencyScriptRelationship(edgeKind, new DependencyScriptTypeReference(typeName), line));
        }

        static string NormalizeDependencyParameterType(string parameter)
        {
            var cleaned = Regex.Replace(parameter, "\\s*=.*$", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim();
            cleaned = Regex.Replace(cleaned, "^(?:ref|out|in|params|this)\\s+", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim();
            var separator = cleaned.LastIndexOf(' ');
            return separator <= 0 ? string.Empty : NormalizeDependencyTypeName(cleaned.Substring(0, separator));
        }

        static string NormalizeDependencyTypeName(string rawTypeName)
        {
            var value = rawTypeName.Trim();
            value = Regex.Replace(value, "\\b(global::|ref|out|in|params|this|where|new)\\b", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(250)).Trim();
            value = Regex.Replace(value, "\\s+", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(250));
            var generic = value.IndexOf('<');
            if (generic >= 0)
            {
                value = value.Substring(0, generic);
            }

            value = value.Trim('[', ']', '?', '&', '*', ',');
            return Regex.IsMatch(value, "^[A-Za-z_][A-Za-z0-9_.]*$", RegexOptions.None, TimeSpan.FromMilliseconds(250)) ? value : string.Empty;
        }

        static bool IsIgnoredDependencyType(string typeName)
        {
            return string.IsNullOrEmpty(typeName)
                || string.Equals(typeName, "void", StringComparison.Ordinal)
                || string.Equals(typeName, "var", StringComparison.Ordinal)
                || string.Equals(typeName, "bool", StringComparison.Ordinal)
                || string.Equals(typeName, "byte", StringComparison.Ordinal)
                || string.Equals(typeName, "sbyte", StringComparison.Ordinal)
                || string.Equals(typeName, "char", StringComparison.Ordinal)
                || string.Equals(typeName, "decimal", StringComparison.Ordinal)
                || string.Equals(typeName, "double", StringComparison.Ordinal)
                || string.Equals(typeName, "float", StringComparison.Ordinal)
                || string.Equals(typeName, "int", StringComparison.Ordinal)
                || string.Equals(typeName, "uint", StringComparison.Ordinal)
                || string.Equals(typeName, "long", StringComparison.Ordinal)
                || string.Equals(typeName, "ulong", StringComparison.Ordinal)
                || string.Equals(typeName, "object", StringComparison.Ordinal)
                || string.Equals(typeName, "short", StringComparison.Ordinal)
                || string.Equals(typeName, "ushort", StringComparison.Ordinal)
                || string.Equals(typeName, "string", StringComparison.Ordinal);
        }

        static List<string> SplitTopLevelComma(string value)
        {
            var values = new List<string>();
            var start = 0;
            var depth = 0;
            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] == '<')
                {
                    depth++;
                }
                else if (value[index] == '>' && depth > 0)
                {
                    depth--;
                }
                else if (value[index] == ',' && depth == 0)
                {
                    values.Add(value.Substring(start, index - start));
                    start = index + 1;
                }
            }

            values.Add(value.Substring(start));
            return values;
        }

        static int LineNumber(string content, int index)
        {
            var line = 1;
            var end = Math.Min(index, content.Length);
            for (var position = 0; position < end; position++)
            {
                if (content[position] == '\n')
                {
                    line++;
                }
            }

            return line;
        }

        static string NamespaceBefore(string content, int index)
        {
            var prefix = index > content.Length ? content : content.Substring(0, index);
            var matches = Regex.Matches(prefix, "\\bnamespace\\s+([A-Za-z_][A-Za-z0-9_.]*)", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return matches.Count == 0 ? string.Empty : matches[matches.Count - 1].Groups[1].Value;
        }

        static string RemoveComments(string content)
        {
            return Regex.Replace(Regex.Replace(content, "/\\*.*?\\*/", string.Empty, RegexOptions.Singleline, TimeSpan.FromMilliseconds(250)), "//.*", string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(250));
        }

        static string RemoveCommentsAndStrings(string content)
        {
            var withoutComments = RemoveComments(content);
            return Regex.Replace(withoutComments, "@?\"(?:\"\"|\\\\.|[^\"])*\"|'(?:\\\\.|[^'])*'", string.Empty, RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
        }

        static string ReadAsmdefName(string asmdefPath, string content)
        {
            var match = Regex.Match(content, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]{1,256})\\\"", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(asmdefPath);
        }

        static List<string> ReadAsmdefStringArray(string content, string field, int maxValues, out bool truncated)
        {
            truncated = false;
            var values = new List<string>();
            var match = Regex.Match(content, "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
            if (!match.Success)
            {
                return values;
            }

            foreach (Match value in Regex.Matches(match.Groups[1].Value, "\\\"([^\\\"]{0,512})\\\"", RegexOptions.None, TimeSpan.FromMilliseconds(250)))
            {
                if (values.Count >= maxValues)
                {
                    truncated = true;
                    break;
                }

                values.Add(value.Groups[1].Value);
            }

            return values;
        }

        static bool IsEditorOnlyAsmdef(string content)
        {
            var includePlatforms = Regex.Match(content, "\\\"includePlatforms\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
            return includePlatforms.Success && includePlatforms.Groups[1].Value.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsUnderEditorFolder(string assetPath)
        {
            return ("/" + assetPath + "/").IndexOf("/Editor/", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsUnityAssetDirectory(string assetPath)
        {
            return assetPath == "Assets" || assetPath == "Packages" || assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath.StartsWith("Packages/", StringComparison.Ordinal);
        }

        static string ParentAssetPath(string assetPath)
        {
            var separator = assetPath.TrimEnd('/').LastIndexOf('/');
            return separator <= 0 ? string.Empty : assetPath.Substring(0, separator);
        }

        static List<YamlGuidReference> YamlGuidReferences(string assetPath, int maxReferences, out bool truncated)
        {
            truncated = false;
            var references = new List<YamlGuidReference>();
            if (!IsUnityYamlTextAsset(assetPath) || !TryResolveAssetFilePath(assetPath, out var fullPath))
            {
                return references;
            }

            try
            {
                using (var reader = new StreamReader(fullPath, new UTF8Encoding(false, true), true))
                {
                    var lineNumber = 0;
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineNumber++;
                        if (line.IndexOf("guid:", StringComparison.Ordinal) < 0)
                        {
                            continue;
                        }

                        var guid = ExtractInlineGuid(line);
                        if (string.IsNullOrEmpty(guid) || IsNullGuid(guid))
                        {
                            continue;
                        }

                        if (references.Count >= maxReferences)
                        {
                            truncated = true;
                            break;
                        }

                        references.Add(new YamlGuidReference
                        {
                            Guid = guid,
                            TargetPath = AssetDatabase.GUIDToAssetPath(guid),
                            Line = lineNumber,
                        });
                    }
                }
            }
            catch (IOException)
            {
                return references;
            }
            catch (UnauthorizedAccessException)
            {
                return references;
            }
            catch (DecoderFallbackException)
            {
                return references;
            }

            return references;
        }

        static bool YamlReferencesGuid(string assetPath, string targetGuid, int maxScanBytes, out int lineNumber, out string readError)
        {
            lineNumber = 0;
            readError = string.Empty;
            if (string.IsNullOrEmpty(targetGuid))
            {
                return false;
            }

            if (!TryReadTextAssetPrefix(assetPath, maxScanBytes, out var content, out readError))
            {
                return false;
            }

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index];
                if (line.IndexOf("guid:", StringComparison.Ordinal) < 0 || line.IndexOf(targetGuid, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                lineNumber = index + 1;
                return true;
            }

            return false;
        }

        static bool ShaderIncludeReferencesTarget(string assetPath, string targetPath, int maxScanBytes, out int includeLine, out string includeRawReference, out string readError)
        {
            includeLine = 0;
            includeRawReference = string.Empty;
            readError = string.Empty;
            if (!TryReadTextAssetPrefix(assetPath, maxScanBytes, out var content, out readError))
            {
                return false;
            }

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (!TryParseShaderIncludeLine(lines[index], out var rawReference))
                {
                    continue;
                }

                var normalizedRawReference = rawReference.Replace('\\', '/');
                var resolvedPath = ResolveShaderIncludeAssetPath(assetPath, rawReference);
                if (string.Equals(resolvedPath, targetPath, StringComparison.Ordinal) || string.Equals(normalizedRawReference, targetPath, StringComparison.Ordinal))
                {
                    includeLine = index + 1;
                    includeRawReference = rawReference;
                    return true;
                }
            }

            return false;
        }

        static bool TryReadTextAssetPrefix(string assetPath, int maxScanBytes, out string content, out string readError)
        {
            content = string.Empty;
            readError = string.Empty;
            if (!TryResolveAssetFilePath(assetPath, out var fullPath))
            {
                readError = "asset_file_not_resolved";
                return false;
            }

            try
            {
                var bytes = ReadBoundedBytes(fullPath, maxScanBytes, out var ignoredByteLength, out var scanTruncated);
                var textResult = DecodeText(bytes, scanTruncated, out content);
                if (textResult != null)
                {
                    readError = textResult.ErrorCode;
                    content = string.Empty;
                    return false;
                }

                return true;
            }
            catch (IOException)
            {
                readError = "io_error";
            }
            catch (UnauthorizedAccessException)
            {
                readError = "unauthorized";
            }

            return false;
        }

        static bool TryParseShaderIncludeLine(string line, out string rawReference)
        {
            rawReference = string.Empty;
            var trimmed = (line ?? string.Empty).TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            if (!trimmed.StartsWith("#include_with_pragmas", StringComparison.Ordinal) && !trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                return false;
            }

            var start = trimmed.IndexOf('"');
            var endToken = '"';
            if (start < 0)
            {
                start = trimmed.IndexOf('<');
                endToken = '>';
            }

            if (start < 0 || start >= trimmed.Length - 1)
            {
                return false;
            }

            var end = trimmed.IndexOf(endToken, start + 1);
            if (end <= start)
            {
                return false;
            }

            rawReference = trimmed.Substring(start + 1, end - start - 1).Trim();
            return rawReference.Length > 0 && rawReference.Length <= 512;
        }

        static string ResolveShaderIncludeAssetPath(string assetPath, string rawReference)
        {
            if (string.IsNullOrEmpty(rawReference) || rawReference.IndexOf('\0') >= 0)
            {
                return string.Empty;
            }

            var normalized = rawReference.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return NormalizeIncludeCandidate(normalized);
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.IndexOf(':') >= 0)
            {
                return string.Empty;
            }

            var parent = ParentAssetPath(assetPath);
            return string.IsNullOrEmpty(parent) ? string.Empty : NormalizeIncludeCandidate(parent + "/" + normalized);
        }

        static string NormalizeIncludeCandidate(string candidate)
        {
            var parts = candidate.Replace('\\', '/').Split('/');
            var stack = new List<string>();
            foreach (var part in parts)
            {
                if (string.IsNullOrEmpty(part) || part == ".")
                {
                    continue;
                }

                if (part == "..")
                {
                    if (stack.Count == 0)
                    {
                        return string.Empty;
                    }

                    stack.RemoveAt(stack.Count - 1);
                    continue;
                }

                if (part.Length > 255 || part.IndexOf(':') >= 0)
                {
                    return string.Empty;
                }

                stack.Add(part);
            }

            var normalized = string.Join("/", stack.ToArray());
            return normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.StartsWith("Packages/", StringComparison.Ordinal) ? normalized : string.Empty;
        }

        static bool IsShaderIncludeTextCandidate(string assetPath)
        {
            var extension = Path.GetExtension(assetPath);
            return string.Equals(extension, ".shader", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".compute", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".hlsl", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".cginc", StringComparison.OrdinalIgnoreCase);
        }

        static string RelocationDestinationJson(string destinationPath, string destinationGuid, bool destinationExists)
        {
            var mainType = destinationExists ? AssetDatabase.GetMainAssetTypeAtPath(destinationPath) : null;
            return "{\"path\":" + JsonUtil.String(destinationPath)
                + ",\"guid\":" + JsonUtil.String(destinationGuid)
                + ",\"exists\":" + JsonUtil.Bool(destinationExists)
                + ",\"is_folder\":" + JsonUtil.Bool(destinationExists && AssetDatabase.IsValidFolder(destinationPath))
                + ",\"extension\":" + JsonUtil.String(Path.GetExtension(destinationPath))
                + ",\"source\":" + JsonUtil.String(SourceForPath(destinationPath))
                + ",\"type\":" + JsonUtil.String(mainType == null ? string.Empty : mainType.FullName ?? mainType.Name) + "}";
        }

        static string RelocationReferenceJson(AssetIndexEntry entry, List<string> referenceKinds, int includeLine, string includeRawReference, int yamlLine, List<string> readErrors)
        {
            return "{\"asset\":" + EntryJson(entry)
                + ",\"reference_kinds\":[" + StringArrayJson(referenceKinds) + "]"
                + ",\"include_line\":" + includeLine
                + ",\"include_raw_reference\":" + JsonUtil.String(includeRawReference)
                + ",\"yaml_guid_line\":" + yamlLine
                + ",\"path_sensitive\":" + JsonUtil.Bool(referenceKinds.Contains("shader_include_text"))
                + ",\"read_errors\":[" + StringArrayJson(readErrors) + "]}";
        }

        static string AssetRelocationScriptContextJson(string sourcePath, string destinationPath, out bool assemblyChanges)
        {
            assemblyChanges = false;
            var sourceIsScript = sourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var destinationIsScript = destinationPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            if (!sourceIsScript && !destinationIsScript)
            {
                return "null";
            }

            var sourceAssembly = sourceIsScript ? PredictDependencyAssembly(sourcePath) : null;
            var destinationAssembly = destinationIsScript ? PredictDependencyAssembly(destinationPath) : null;
            assemblyChanges = sourceAssembly != null && destinationAssembly != null && !string.Equals(sourceAssembly.AssemblyName, destinationAssembly.AssemblyName, StringComparison.Ordinal);
            return "{\"kind\":\"script\""
                + ",\"source_is_script\":" + JsonUtil.Bool(sourceIsScript)
                + ",\"destination_is_script\":" + JsonUtil.Bool(destinationIsScript)
                + ",\"source_assembly\":" + (sourceAssembly == null ? "null" : sourceAssembly.ToJson())
                + ",\"destination_assembly\":" + (destinationAssembly == null ? "null" : destinationAssembly.ToJson())
                + ",\"assembly_changes\":" + JsonUtil.Bool(assemblyChanges)
                + ",\"script_specific_command\":\"script.relocation.preflight\"}";
        }

        static string AssetRelocationShaderContextJson(string sourcePath, string destinationPath, int shaderIncludeReferenceCount)
        {
            var sourceKind = ShaderRelocationKind(sourcePath);
            var destinationKind = ShaderRelocationKind(destinationPath);
            if (string.IsNullOrEmpty(sourceKind) && string.IsNullOrEmpty(destinationKind) && shaderIncludeReferenceCount == 0)
            {
                return "null";
            }

            return "{\"kind\":\"shader_path_context\""
                + ",\"source_kind\":" + JsonUtil.String(sourceKind)
                + ",\"destination_kind\":" + JsonUtil.String(destinationKind)
                + ",\"source_path_sensitive\":" + JsonUtil.Bool(IsShaderIncludeTextCandidate(sourcePath))
                + ",\"shader_include_reference_count\":" + shaderIncludeReferenceCount
                + ",\"shader_specific_command\":\"shader.references.report\"}";
        }

        static string ShaderRelocationKind(string assetPath)
        {
            var extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (extension == ".shader") return "shader";
            if (extension == ".shadergraph") return "shadergraph";
            if (extension == ".shadersubgraph") return "shadergraph_subgraph";
            if (extension == ".compute") return "compute";
            if (extension == ".hlsl") return "hlsl";
            if (extension == ".cginc") return "cginc";
            if (extension == ".mat") return "material";
            return string.Empty;
        }

        static List<string> BuildAssetRelocationWarnings(string sourcePath, string destinationPath, bool sourceIsFolder, bool destinationExists, bool destinationParentExists, bool scriptAssemblyChanges, int shaderIncludeReferenceCount, bool scanTruncated, bool resultsTruncated)
        {
            var warnings = new List<string>
            {
                "This command is dry-run only and does not move, import, refresh, write, overwrite, or delete assets.",
                "Unity GUID references usually survive AssetDatabase moves; path-string references such as shader includes may require manual edits."
            };

            if (destinationExists)
            {
                warnings.Add("destination_path already exists; safe move commands do not overwrite assets.");
            }

            if (!destinationParentExists)
            {
                warnings.Add("destination parent folder does not exist.");
            }

            if (sourceIsFolder)
            {
                warnings.Add("Folder relocation preflight reports only the folder asset GUID and direct path-level risks; run per-asset preflight for child assets.");
            }

            if (!string.Equals(SourceForPath(sourcePath), SourceForPath(destinationPath), StringComparison.Ordinal))
            {
                warnings.Add("Relocation crosses Assets/Packages boundary; package mutability and asmdef/package constraints are not fully solved.");
            }

            if (!string.Equals(Path.GetExtension(sourcePath), Path.GetExtension(destinationPath), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add("destination_path changes the asset extension, which may change importer behavior and references.");
            }

            if (scriptAssemblyChanges)
            {
                warnings.Add("Script relocation changes predicted assembly ownership; run script.relocation.preflight for detailed reference visibility checks.");
            }

            if (shaderIncludeReferenceCount > 0)
            {
                warnings.Add("Shader include text references are path-sensitive and may need edits after relocation.");
            }

            if (scanTruncated || resultsTruncated)
            {
                warnings.Add("Reference scan was truncated by max_scan_assets or max_results; increase limits for a fuller report.");
            }

            return warnings;
        }

        static string AssetInfoJson(string assetPath, McpCommandContext context)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                return "{\"asset_path\":" + JsonUtil.String(assetPath)
                    + ",\"exists\":false"
                    + ",\"guid\":\"\""
                    + ",\"compatible_with\":\"Unity.ManageAsset GetInfo\"}";
            }

            var includeLabels = JsonUtil.ExtractBool(context.ArgsJson, "include_labels", true);
            var includeImporter = JsonUtil.ExtractBool(context.ArgsJson, "include_importer", true);
            var includeDependencies = JsonUtil.ExtractBool(context.ArgsJson, "include_dependencies", false);
            var recursiveDependencies = JsonUtil.ExtractBool(context.ArgsJson, "recursive_dependencies", false);
            var maxDependencies = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_dependencies", 50), 1, 200);
            var entry = EntryFromPath(assetPath, guid);
            var mainType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);

            return "{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"exists\":true"
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"name\":" + JsonUtil.String(entry.Name)
                + ",\"extension\":" + JsonUtil.String(entry.Extension)
                + ",\"source\":" + JsonUtil.String(entry.Source)
                + ",\"is_folder\":" + JsonUtil.Bool(AssetDatabase.IsValidFolder(assetPath))
                + ",\"main_asset_type\":" + JsonUtil.String(mainType == null ? string.Empty : mainType.FullName ?? mainType.Name)
                + ",\"labels\":[" + LabelsJson(assetPath, includeLabels) + "]"
                + ",\"importer\":" + ImporterJson(assetPath, includeImporter)
                + ",\"dependencies\":" + DependenciesInfoJson(assetPath, includeDependencies, recursiveDependencies, maxDependencies)
                + ",\"compatible_with\":\"Unity.ManageAsset GetInfo\"}";
        }

        static CommandExecutionResult SerializedAssetInspectResult(string assetPath, string propertyPath, int maxDepth, int maxArrayElements, int maxChars)
        {
            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Asset could not be loaded");
            }

            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(asset);
            }
            catch (Exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Asset cannot be inspected with SerializedObject");
            }

            var dataResult = SerializedDataJson(serializedObject, propertyPath, maxDepth, maxArrayElements, maxChars, out var dataJson, out var truncated, out var propertyCount);
            if (dataResult != null)
            {
                return dataResult;
            }

            var type = asset.GetType();
            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"name\":" + JsonUtil.String(asset.name)
                + ",\"type\":" + JsonUtil.String(type.FullName ?? type.Name)
                + ",\"property_path\":" + JsonUtil.String(propertyPath)
                + ",\"property_count\":" + propertyCount
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"limits\":{\"max_depth\":" + maxDepth
                + ",\"max_array_elements\":" + maxArrayElements
                + ",\"max_chars\":" + maxChars + "}"
                + ",\"data\":" + dataJson
                + ",\"compatible_with\":\"Unity.GetObjectData\"}");
        }

        static CommandExecutionResult SerializedDataJson(SerializedObject serializedObject, string propertyPath, int maxDepth, int maxArrayElements, int maxChars, out string dataJson, out bool truncated, out int propertyCount)
        {
            dataJson = string.Empty;
            truncated = false;
            propertyCount = 0;

            var builder = new StringBuilder();
            builder.Append("{\"properties\":[");
            if (!string.IsNullOrEmpty(propertyPath))
            {
                var property = FindSerializedProperty(serializedObject, propertyPath);
                if (property == null)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "property_path was not found on the serialized asset");
                }

                AppendSerializedPropertyTree(builder, property, property.depth, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount);
            }
            else
            {
                AppendSerializedObjectProperties(builder, serializedObject, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount);
            }

            builder.Append("]}");
            dataJson = builder.ToString();
            return null;
        }

        static SerializedProperty FindSerializedProperty(SerializedObject serializedObject, string propertyPath)
        {
            var property = serializedObject.FindProperty(propertyPath);
            if (property != null || propertyPath.IndexOf('/') < 0)
            {
                return property;
            }

            return serializedObject.FindProperty(propertyPath.Replace('/', '.'));
        }

        static void AppendSerializedObjectProperties(StringBuilder builder, SerializedObject serializedObject, int maxDepth, int maxArrayElements, int maxChars, ref bool truncated, ref int propertyCount)
        {
            var property = serializedObject.GetIterator();
            var enterChildren = true;
            while (property.NextVisible(enterChildren))
            {
                enterChildren = AppendSerializedProperty(builder, property, property.depth, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount, out var stop);
                if (stop)
                {
                    break;
                }
            }
        }

        static void AppendSerializedPropertyTree(StringBuilder builder, SerializedProperty rootProperty, int baseDepth, int maxDepth, int maxArrayElements, int maxChars, ref bool truncated, ref int propertyCount)
        {
            var property = rootProperty.Copy();
            var endProperty = rootProperty.GetEndProperty();
            var enterChildren = true;
            while (!SerializedProperty.EqualContents(property, endProperty))
            {
                var relativeDepth = property.depth - baseDepth;
                enterChildren = AppendSerializedProperty(builder, property, relativeDepth, maxDepth, maxArrayElements, maxChars, ref truncated, ref propertyCount, out var stop);
                if (stop || !property.NextVisible(enterChildren))
                {
                    break;
                }
            }
        }

        static bool AppendSerializedProperty(StringBuilder builder, SerializedProperty property, int relativeDepth, int maxDepth, int maxArrayElements, int maxChars, ref bool truncated, ref int propertyCount, out bool stop)
        {
            stop = false;
            if (relativeDepth > maxDepth || IsArrayElementPastLimit(property.propertyPath, maxArrayElements))
            {
                truncated = true;
                return false;
            }

            if (relativeDepth >= maxDepth && property.hasVisibleChildren)
            {
                truncated = true;
            }

            if (IsSerializedArray(property) && property.arraySize > maxArrayElements)
            {
                truncated = true;
            }

            var entryJson = SerializedPropertyEntryJson(property, relativeDepth);
            var separatorLength = propertyCount > 0 ? 1 : 0;
            if (builder.Length + separatorLength + entryJson.Length + 2 > maxChars)
            {
                truncated = true;
                stop = true;
                return false;
            }

            if (propertyCount > 0)
            {
                builder.Append(',');
            }

            builder.Append(entryJson);
            propertyCount++;
            return relativeDepth < maxDepth;
        }

        static string SerializedPropertyEntryJson(SerializedProperty property, int relativeDepth)
        {
            var builder = new StringBuilder();
            builder.Append("{\"path\":").Append(JsonUtil.String(property.propertyPath));
            builder.Append(",\"display_name\":").Append(JsonUtil.String(property.displayName));
            builder.Append(",\"type\":").Append(JsonUtil.String(property.propertyType.ToString()));
            builder.Append(",\"depth\":").Append(relativeDepth);
            builder.Append(",\"is_array\":").Append(JsonUtil.Bool(IsSerializedArray(property)));
            if (IsSerializedArray(property))
            {
                builder.Append(",\"array_size\":").Append(property.arraySize);
            }

            builder.Append(",\"value\":").Append(SerializedPropertyValueJson(property));
            builder.Append('}');
            return builder.ToString();
        }

        static string SerializedPropertyValueJson(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean: return JsonUtil.Bool(property.boolValue);
                case SerializedPropertyType.Float: return FloatJson(property.floatValue);
                case SerializedPropertyType.String: return JsonUtil.String(TruncateSerializedString(property.stringValue));
                case SerializedPropertyType.Color: return ColorJson(property.colorValue);
                case SerializedPropertyType.ObjectReference: return ObjectReferenceJson(property.objectReferenceValue);
                case SerializedPropertyType.LayerMask: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum: return JsonUtil.String(property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString(CultureInfo.InvariantCulture));
                case SerializedPropertyType.Vector2: return Vector2Json(property.vector2Value);
                case SerializedPropertyType.Vector3: return Vector3Json(property.vector3Value);
                case SerializedPropertyType.Vector4: return Vector4Json(property.vector4Value);
                case SerializedPropertyType.Rect: return RectJson(property.rectValue);
                case SerializedPropertyType.ArraySize: return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character: return JsonUtil.String(((char)property.intValue).ToString());
                case SerializedPropertyType.Bounds: return BoundsJson(property.boundsValue);
                case SerializedPropertyType.Quaternion: return QuaternionJson(property.quaternionValue);
                default: return "null";
            }
        }

        static bool IsSerializedArray(SerializedProperty property)
        {
            return property.isArray && property.propertyType != SerializedPropertyType.String;
        }

        static bool IsArrayElementPastLimit(string propertyPath, int maxArrayElements)
        {
            var searchFrom = 0;
            const string Marker = "Array.data[";
            while (searchFrom < propertyPath.Length)
            {
                var marker = propertyPath.IndexOf(Marker, searchFrom, StringComparison.Ordinal);
                if (marker < 0)
                {
                    return false;
                }

                var start = marker + Marker.Length;
                var end = propertyPath.IndexOf(']', start);
                if (end < 0)
                {
                    return false;
                }

                if (int.TryParse(propertyPath.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var arrayIndex) && arrayIndex >= maxArrayElements)
                {
                    return true;
                }

                searchFrom = end + 1;
            }

            return false;
        }

        static string ObjectReferenceJson(UnityEngine.Object value)
        {
            if (value == null)
            {
                return "null";
            }

            var type = value.GetType();
            var assetPath = AssetDatabase.GetAssetPath(value);
            return "{\"name\":" + JsonUtil.String(value.name)
                + ",\"type\":" + JsonUtil.String(type.FullName ?? type.Name)
                + ",\"asset_path\":" + JsonUtil.String(IsUnityAssetPath(assetPath) ? assetPath : string.Empty) + "}";
        }

        static string ColorJson(Color value)
        {
            return "{\"r\":" + FloatJson(value.r)
                + ",\"g\":" + FloatJson(value.g)
                + ",\"b\":" + FloatJson(value.b)
                + ",\"a\":" + FloatJson(value.a) + "}";
        }

        static string Vector2Json(Vector2 value)
        {
            return "{\"x\":" + FloatJson(value.x)
                + ",\"y\":" + FloatJson(value.y) + "}";
        }

        static string Vector3Json(Vector3 value)
        {
            return "{\"x\":" + FloatJson(value.x)
                + ",\"y\":" + FloatJson(value.y)
                + ",\"z\":" + FloatJson(value.z) + "}";
        }

        static string Vector4Json(Vector4 value)
        {
            return "{\"x\":" + FloatJson(value.x)
                + ",\"y\":" + FloatJson(value.y)
                + ",\"z\":" + FloatJson(value.z)
                + ",\"w\":" + FloatJson(value.w) + "}";
        }

        static string RectJson(Rect value)
        {
            return "{\"x\":" + FloatJson(value.x)
                + ",\"y\":" + FloatJson(value.y)
                + ",\"width\":" + FloatJson(value.width)
                + ",\"height\":" + FloatJson(value.height) + "}";
        }

        static string BoundsJson(Bounds value)
        {
            return "{\"center\":" + Vector3Json(value.center)
                + ",\"size\":" + Vector3Json(value.size) + "}";
        }

        static string QuaternionJson(Quaternion value)
        {
            return "{\"x\":" + FloatJson(value.x)
                + ",\"y\":" + FloatJson(value.y)
                + ",\"z\":" + FloatJson(value.z)
                + ",\"w\":" + FloatJson(value.w) + "}";
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? JsonUtil.String(value.ToString(CultureInfo.InvariantCulture)) : value.ToString(CultureInfo.InvariantCulture);
        }

        static string TruncateSerializedString(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= MaxSerializedStringChars)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, MaxSerializedStringChars);
        }

        static string LabelsJson(string assetPath, bool included)
        {
            if (!included)
            {
                return string.Empty;
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            var labels = asset == null ? new string[0] : AssetDatabase.GetLabels(asset) ?? new string[0];
            Array.Sort(labels, StringComparer.Ordinal);
            return StringArrayJson(labels);
        }

        static string ImporterJson(string assetPath, bool included)
        {
            if (!included)
            {
                return "null";
            }

            var importer = AssetImporter.GetAtPath(assetPath);
            if (importer == null)
            {
                return "null";
            }

            return "{\"type\":" + JsonUtil.String(importer.GetType().FullName ?? importer.GetType().Name)
                + ",\"asset_bundle_name\":" + JsonUtil.String(importer.assetBundleName ?? string.Empty)
                + ",\"asset_bundle_variant\":" + JsonUtil.String(importer.assetBundleVariant ?? string.Empty) + "}";
        }

        static string DependenciesInfoJson(string assetPath, bool included, bool recursive, int maxDependencies)
        {
            if (!included)
            {
                return "{\"included\":false,\"recursive\":" + JsonUtil.Bool(recursive)
                    + ",\"truncated\":false,\"assets\":[]}";
            }

            var dependencies = AssetDatabase.GetDependencies(assetPath, recursive);
            Array.Sort(dependencies, StringComparer.Ordinal);
            var builder = new StringBuilder();
            var count = 0;
            var truncated = false;
            for (var index = 0; index < dependencies.Length; index++)
            {
                var dependencyPath = dependencies[index];
                if (string.Equals(dependencyPath, assetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (count >= maxDependencies)
                {
                    truncated = true;
                    break;
                }

                AppendEntry(builder, EntryFromPath(dependencyPath), count > 0);
                count++;
            }

            return "{\"included\":true,\"recursive\":" + JsonUtil.Bool(recursive)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"assets\":[" + builder + "]}";
        }

        static int CountDependencies(string assetPath)
        {
            var dependencies = AssetDatabase.GetDependencies(assetPath, true);
            var count = 0;
            for (var index = 0; index < dependencies.Length; index++)
            {
                if (!string.Equals(dependencies[index], assetPath, StringComparison.Ordinal))
                {
                    count++;
                }
            }

            return count;
        }

        static List<string> DuplicateGuidPaths(string guid, string ownAssetPath, int maxPaths, out bool truncated)
        {
            var paths = new List<string>();
            truncated = false;
            if (string.IsNullOrEmpty(guid))
            {
                return paths;
            }

            ScanDuplicateGuidRoot("Assets", guid, ownAssetPath, maxPaths, paths, ref truncated);
            ScanDuplicateGuidRoot("Packages", guid, ownAssetPath, maxPaths, paths, ref truncated);
            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        static void ScanDuplicateGuidRoot(string rootName, string guid, string ownAssetPath, int maxPaths, List<string> paths, ref bool truncated)
        {
            var rootPath = Path.Combine(ProjectPaths.ProjectRoot, rootName);
            if (!Directory.Exists(rootPath))
            {
                return;
            }

            foreach (var metaPath in Directory.EnumerateFiles(rootPath, "*.meta", SearchOption.AllDirectories))
            {
                if (!MetaFileHasGuid(metaPath, guid))
                {
                    continue;
                }

                var assetPath = UnityAssetPathFromMeta(metaPath);
                if (string.Equals(assetPath, ownAssetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (paths.Count >= maxPaths)
                {
                    truncated = true;
                    continue;
                }

                paths.Add(assetPath);
            }
        }

        static bool MetaFileHasGuid(string metaPath, string guid)
        {
            try
            {
                foreach (var line in File.ReadLines(metaPath))
                {
                    if (!line.StartsWith("guid:", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    return string.Equals(line.Substring("guid:".Length).Trim(), guid, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }

            return false;
        }

        static string UnityAssetPathFromMeta(string metaPath)
        {
            var root = ProjectPaths.ProjectRoot.TrimEnd(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
            var relative = metaPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? metaPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : metaPath;
            relative = relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            return relative.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) ? relative.Substring(0, relative.Length - ".meta".Length) : relative;
        }

        static List<GuidReference> MissingScriptReferences(string assetPath, int maxReferences, out bool truncated)
        {
            var missingReferences = new List<GuidReference>();
            truncated = false;
            if (!IsUnityYamlTextAsset(assetPath))
            {
                return missingReferences;
            }

            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                return missingReferences;
            }

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(fullPath))
                {
                    lineNumber++;
                    if (line.IndexOf("m_Script:", StringComparison.Ordinal) < 0 || line.IndexOf("guid:", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    var guid = ExtractInlineGuid(line);
                    if (string.IsNullOrEmpty(guid) || !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)) || ContainsGuidReference(missingReferences, guid))
                    {
                        continue;
                    }

                    if (missingReferences.Count >= maxReferences)
                    {
                        truncated = true;
                        continue;
                    }

                    missingReferences.Add(new GuidReference { Guid = guid, Line = lineNumber });
                }
            }
            catch (IOException)
            {
                return missingReferences;
            }
            catch (UnauthorizedAccessException)
            {
                return missingReferences;
            }

            missingReferences.Sort((left, right) => string.Compare(left.Guid, right.Guid, StringComparison.OrdinalIgnoreCase));
            return missingReferences;
        }

        static string ExtractInlineGuid(string line)
        {
            var guidIndex = line.IndexOf("guid:", StringComparison.Ordinal);
            if (guidIndex < 0)
            {
                return string.Empty;
            }

            var start = guidIndex + "guid:".Length;
            var end = line.IndexOf(',', start);
            if (end < 0)
            {
                end = line.IndexOf('}', start);
            }

            if (end < 0)
            {
                end = line.Length;
            }

            return line.Substring(start, end - start).Trim();
        }

        static bool ContainsOrdinalIgnoreCase(List<string> values, string value)
        {
            for (var index = 0; index < values.Count; index++)
            {
                if (string.Equals(values[index], value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static bool ContainsGuidReference(List<GuidReference> references, string guid)
        {
            for (var index = 0; index < references.Count; index++)
            {
                if (string.Equals(references[index].Guid, guid, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static List<GuidReference> BrokenReferenceReferences(string assetPath, List<string> ignoredGuids, int maxReferences, out bool truncated)
        {
            var brokenReferences = new List<GuidReference>();
            truncated = false;
            if (!IsUnityYamlTextAsset(assetPath))
            {
                return brokenReferences;
            }

            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                return brokenReferences;
            }

            try
            {
                var lineNumber = 0;
                foreach (var line in File.ReadLines(fullPath))
                {
                    lineNumber++;
                    if (line.IndexOf("guid:", StringComparison.Ordinal) < 0)
                    {
                        continue;
                    }

                    var guid = ExtractInlineGuid(line);
                    if (string.IsNullOrEmpty(guid) || IsNullGuid(guid) || ContainsOrdinalIgnoreCase(ignoredGuids, guid) || ContainsGuidReference(brokenReferences, guid) || !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid)))
                    {
                        continue;
                    }

                    if (brokenReferences.Count >= maxReferences)
                    {
                        truncated = true;
                        continue;
                    }

                    brokenReferences.Add(new GuidReference { Guid = guid, Line = lineNumber });
                }
            }
            catch (IOException)
            {
                return brokenReferences;
            }
            catch (UnauthorizedAccessException)
            {
                return brokenReferences;
            }

            brokenReferences.Sort((left, right) => string.Compare(left.Guid, right.Guid, StringComparison.OrdinalIgnoreCase));
            return brokenReferences;
        }

        static bool IsNullGuid(string guid)
        {
            for (var index = 0; index < guid.Length; index++)
            {
                if (guid[index] != '0')
                {
                    return false;
                }
            }

            return true;
        }

        static bool IsUnityYamlTextAsset(string assetPath)
        {
            return assetPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                || assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || assetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase)
                || assetPath.EndsWith(".mat", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsTextSearchCandidate(string assetPath)
        {
            if (assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase) || AssetDatabase.IsValidFolder(assetPath))
            {
                return false;
            }

            return !IsKnownBinaryAssetExtension(Path.GetExtension(assetPath));
        }

        static bool IsKnownBinaryAssetExtension(string extension)
        {
            return string.Equals(extension, ".a", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".aar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".aiff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".apk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".astc", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".avi", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bin", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".blend", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bmp", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bundle", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".bytes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dds", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dll", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".dylib", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".exr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".fbx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".gif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".hdr", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jar", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ktx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ktx2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".mp4", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ogg", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".otf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".psd", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".so", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tga", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tif", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tiff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".ttf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".unitypackage", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".webm", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".woff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".woff2", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
        }

        static bool TryResolveAssetFilePath(string assetPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath == "Assets")
            {
                fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
                return IsSameOrChildPath(fullPath, ProjectPaths.ProjectRoot) && File.Exists(fullPath);
            }

            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return false;
            }

            var relative = assetPath.Substring("Packages/".Length);
            var slash = relative.IndexOf('/');
            var packageName = slash < 0 ? relative : relative.Substring(0, slash);
            var packageRelative = slash < 0 ? string.Empty : relative.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar);
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (!string.Equals(package.name, packageName, StringComparison.Ordinal))
                {
                    continue;
                }

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

        static bool TryResolveAssetDirectoryPath(string assetPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath == "Assets")
            {
                fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
                return IsSameOrChildPath(fullPath, ProjectPaths.ProjectRoot) && Directory.Exists(fullPath);
            }

            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return false;
            }

            var relative = assetPath.Substring("Packages/".Length);
            var slash = relative.IndexOf('/');
            var packageName = slash < 0 ? relative : relative.Substring(0, slash);
            var packageRelative = slash < 0 ? string.Empty : relative.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar);
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (!string.Equals(package.name, packageName, StringComparison.Ordinal))
                {
                    continue;
                }

                var packageRoot = Path.GetFullPath(package.resolvedPath);
                var candidate = Path.GetFullPath(Path.Combine(packageRoot, packageRelative));
                if (IsSameOrChildPath(candidate, packageRoot) && Directory.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }

            var fallback = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsSameOrChildPath(fallback, ProjectPaths.ProjectRoot) && Directory.Exists(fallback))
            {
                fullPath = fallback;
                return true;
            }

            return false;
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
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }

            if (offset != bytes.Length)
            {
                Array.Resize(ref bytes, offset);
            }

            return bytes;
        }

        static CommandExecutionResult DecodeText(byte[] bytes, bool truncated, out string content)
        {
            content = string.Empty;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] == 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Asset content looks binary and cannot be returned as text");
                }
            }

            try
            {
                content = DecodeStrictUtf8(bytes);
                return null;
            }
            catch (DecoderFallbackException)
            {
                if (truncated)
                {
                    for (var trim = 1; trim <= 3 && trim <= bytes.Length; trim++)
                    {
                        try
                        {
                            content = DecodeStrictUtf8(bytes, bytes.Length - trim);
                            return null;
                        }
                        catch (DecoderFallbackException)
                        {
                        }
                    }
                }

                return CommandExecutionResult.Error("invalid_field_value", "Asset content is not valid UTF-8 text");
            }
        }

        static string DecodeStrictUtf8(byte[] bytes)
        {
            return DecodeStrictUtf8(bytes, bytes.Length);
        }

        static string DecodeStrictUtf8(byte[] bytes, int byteCount)
        {
            var content = new UTF8Encoding(false, true).GetString(bytes, 0, byteCount);
            return content.Length > 0 && content[0] == '\ufeff' ? content.Substring(1) : content;
        }

        static string TextMatchMode(string argsJson, out CommandExecutionResult error)
        {
            error = null;
            var mode = JsonUtil.ExtractString(argsJson, "match_mode");
            if (string.IsNullOrEmpty(mode))
            {
                return "literal";
            }

            if (string.Equals(mode, "literal", StringComparison.OrdinalIgnoreCase))
            {
                return "literal";
            }

            if (string.Equals(mode, "regex", StringComparison.OrdinalIgnoreCase))
            {
                return "regex";
            }

            error = CommandExecutionResult.Error("invalid_field_value", "match_mode must be literal or regex");
            return "literal";
        }

        static CommandExecutionResult TryCreateTextRegex(string query, string matchMode, bool caseSensitive, int timeoutMs, out Regex regex)
        {
            regex = null;
            if (!string.Equals(matchMode, "regex", StringComparison.Ordinal))
            {
                return null;
            }

            if (query.Length > 256)
            {
                return CommandExecutionResult.Error("invalid_field_value", "regex query must be 1..256 characters");
            }

            return TryCreateOptionalRegex(query, caseSensitive, timeoutMs, out regex);
        }

        static CommandExecutionResult TryCreateOptionalRegex(string pattern, bool caseSensitive, int timeoutMs, out Regex regex)
        {
            regex = null;
            if (string.IsNullOrEmpty(pattern))
            {
                return null;
            }

            var options = RegexOptions.CultureInvariant;
            if (!caseSensitive)
            {
                options |= RegexOptions.IgnoreCase;
            }

            try
            {
                regex = new Regex(pattern, options, TimeSpan.FromMilliseconds(timeoutMs));
                return null;
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Invalid regex pattern: " + exception.Message);
            }
        }

        static bool NameRegexMatches(AssetIndexEntry entry, Regex regex)
        {
            return regex.IsMatch(entry.Path) || regex.IsMatch(entry.Name);
        }

        static string FindTextMatches(string content, string query, StringComparison comparison, Regex regex, int contextLines, int maxResults, out bool truncated)
        {
            return FindTextMatches(content, query, comparison, regex, contextLines, maxResults, out truncated, out var count);
        }

        static string FindTextMatches(string content, string query, StringComparison comparison, Regex regex, int contextLines, int maxResults, out bool truncated, out int count)
        {
            var builder = new StringBuilder();
            var lines = SplitTextLines(content);
            count = 0;
            truncated = false;
            for (var lineIndex = 0; lineIndex < lines.Count; lineIndex++)
            {
                var line = lines[lineIndex];
                if (regex == null)
                {
                    var matchIndex = line.Text.IndexOf(query, comparison);
                    while (matchIndex >= 0)
                    {
                        if (count >= maxResults)
                        {
                            truncated = true;
                            return builder.ToString();
                        }

                        AppendTextMatch(builder, count, lines, lineIndex, matchIndex, query.Length, contextLines);
                        count++;
                        matchIndex = line.Text.IndexOf(query, matchIndex + Math.Max(query.Length, 1), comparison);
                    }
                }
                else
                {
                    foreach (Match match in regex.Matches(line.Text))
                    {
                        if (count >= maxResults)
                        {
                            truncated = true;
                            return builder.ToString();
                        }

                        AppendTextMatch(builder, count, lines, lineIndex, match.Index, Math.Max(match.Length, 1), contextLines);
                        count++;
                    }
                }
            }

            return builder.ToString();
        }

        static List<TextLine> SplitTextLines(string content)
        {
            var lines = new List<TextLine>();
            var line = 1;
            var lineStart = 0;
            while (lineStart <= content.Length)
            {
                var lineEnd = content.IndexOf('\n', lineStart);
                if (lineEnd < 0)
                {
                    lineEnd = content.Length;
                }

                lines.Add(new TextLine(line, content.Substring(lineStart, lineEnd - lineStart).TrimEnd('\r')));

                if (lineEnd >= content.Length)
                {
                    break;
                }

                line++;
                lineStart = lineEnd + 1;
            }

            return lines;
        }

        static void AppendTextMatch(StringBuilder builder, int matchNumber, List<TextLine> lines, int lineIndex, int matchIndex, int queryLength, int contextLines)
        {
            var line = lines[lineIndex];
            if (matchNumber > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"line\":").Append(line.Number)
                .Append(",\"column\":").Append(matchIndex + 1)
                .Append(",\"excerpt\":").Append(JsonUtil.String(Excerpt(line.Text, matchIndex, queryLength)));
            if (contextLines > 0)
            {
                builder.Append(",\"context_before\":[");
                AppendContextLines(builder, lines, Math.Max(0, lineIndex - contextLines), lineIndex);
                builder.Append("],\"context_after\":[");
                AppendContextLines(builder, lines, lineIndex + 1, Math.Min(lines.Count, lineIndex + 1 + contextLines));
                builder.Append(']');
            }

            builder.Append('}');
        }

        static void AppendContextLines(StringBuilder builder, List<TextLine> lines, int startIndex, int endIndex)
        {
            for (var index = startIndex; index < endIndex; index++)
            {
                if (index > startIndex)
                {
                    builder.Append(',');
                }

                builder.Append("{\"line\":").Append(lines[index].Number)
                    .Append(",\"text\":").Append(JsonUtil.String(lines[index].Text)).Append('}');
            }
        }

        static string Excerpt(string line, int matchIndex, int queryLength)
        {
            const int Radius = 80;
            var start = Math.Max(0, matchIndex - Radius);
            var end = Math.Min(line.Length, matchIndex + queryLength + Radius);
            return (start > 0 ? "..." : string.Empty) + line.Substring(start, end - start) + (end < line.Length ? "..." : string.Empty);
        }

        static List<string> SemanticQueryTokens(string query)
        {
            var tokens = new List<string>();
            var current = new StringBuilder();
            for (var index = 0; index < query.Length; index++)
            {
                var character = query[index];
                if (char.IsLetterOrDigit(character))
                {
                    current.Append(char.ToLowerInvariant(character));
                    continue;
                }

                FlushSemanticToken(tokens, current);
                if (tokens.Count >= MaxSemanticQueryTokens)
                {
                    break;
                }
            }

            if (tokens.Count < MaxSemanticQueryTokens)
            {
                FlushSemanticToken(tokens, current);
            }

            return tokens;
        }

        static void FlushSemanticToken(List<string> tokens, StringBuilder current)
        {
            if (current.Length == 0)
            {
                return;
            }

            var token = current.ToString();
            current.Length = 0;
            AddUniqueString(tokens, token, MaxSemanticQueryTokens);
        }

        static SemanticAssetSearchHit ScoreSemanticAsset(AssetIndexEntry entry, string query, List<string> queryTokens, bool includeLabels, bool includeImporter, bool includeScriptSymbols)
        {
            var hit = new SemanticAssetSearchHit(entry);
            AddSemanticFieldScore(hit, query, queryTokens, "path", entry.Path, 12);
            AddSemanticFieldScore(hit, query, queryTokens, "name", entry.Name, 30);
            AddSemanticFieldScore(hit, query, queryTokens, "type", entry.Type, 24);
            AddSemanticFieldScore(hit, query, queryTokens, "extension", entry.Extension, 10);
            AddSemanticFieldScore(hit, query, queryTokens, "guid", entry.Guid, 18);
            AddSemanticFieldScore(hit, query, queryTokens, "source", entry.Source, 6);

            if (includeLabels)
            {
                AddSemanticLabelScore(hit, query, queryTokens, entry.Path);
            }

            if (includeImporter)
            {
                AddSemanticImporterScore(hit, query, queryTokens, entry.Path);
            }

            if (includeScriptSymbols)
            {
                AddSemanticScriptSymbolScore(hit, query, queryTokens, entry.Path);
            }

            return hit;
        }

        static void AddSemanticLabelScore(SemanticAssetSearchHit hit, string query, List<string> queryTokens, string assetPath)
        {
            try
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                var labels = asset == null ? new string[0] : AssetDatabase.GetLabels(asset) ?? new string[0];
                for (var index = 0; index < labels.Length; index++)
                {
                    AddSemanticFieldScore(hit, query, queryTokens, "label", labels[index], 28);
                }
            }
            catch (Exception)
            {
            }
        }

        static void AddSemanticImporterScore(SemanticAssetSearchHit hit, string query, List<string> queryTokens, string assetPath)
        {
            try
            {
                var importer = AssetImporter.GetAtPath(assetPath);
                if (importer == null)
                {
                    return;
                }

                var importerType = importer.GetType();
                AddSemanticFieldScore(hit, query, queryTokens, "importer", importerType.FullName ?? importerType.Name, 18);
                AddSemanticFieldScore(hit, query, queryTokens, "importer", importer.assetBundleName ?? string.Empty, 14);
                AddSemanticFieldScore(hit, query, queryTokens, "importer", importer.assetBundleVariant ?? string.Empty, 10);
            }
            catch (Exception)
            {
            }
        }

        static void AddSemanticScriptSymbolScore(SemanticAssetSearchHit hit, string query, List<string> queryTokens, string assetPath)
        {
            if (!assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !TryResolveAssetFilePath(assetPath, out var fullPath))
            {
                return;
            }

            try
            {
                var bytes = ReadBoundedBytes(fullPath, MaxSemanticScriptBytes, out var ignoredByteLength, out var scanTruncated);
                var textResult = DecodeText(bytes, scanTruncated, out var content);
                if (textResult != null)
                {
                    return;
                }

                var declaredTypes = ExtractDependencyDeclaredTypes(content, MaxDependencyDeclaredTypes, out var ignoredTypesTruncated);
                for (var index = 0; index < declaredTypes.Count; index++)
                {
                    AddSemanticFieldScore(hit, query, queryTokens, "script_symbol", declaredTypes[index].Name, 26);
                    AddSemanticFieldScore(hit, query, queryTokens, "script_symbol", declaredTypes[index].FullName, 20);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (RegexMatchTimeoutException)
            {
            }
        }

        static void AddSemanticFieldScore(SemanticAssetSearchHit hit, string query, List<string> queryTokens, string fieldName, string value, int weight)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }

            var normalizedValue = value.ToLowerInvariant();
            var normalizedQuery = query.ToLowerInvariant();
            if (normalizedQuery.Length > 1 && normalizedValue.IndexOf(normalizedQuery, StringComparison.Ordinal) >= 0)
            {
                hit.Score += weight * 3;
                AddSemanticMatch(hit, fieldName, normalizedQuery, fieldName + ":phrase");
            }

            for (var index = 0; index < queryTokens.Count; index++)
            {
                var token = queryTokens[index];
                if (normalizedValue.IndexOf(token, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                hit.Score += weight;
                if (string.Equals(normalizedValue, token, StringComparison.Ordinal))
                {
                    hit.Score += weight * 2;
                    AddSemanticMatch(hit, fieldName, token, fieldName + ":exact");
                }
                else if (SemanticValueStartsWithToken(normalizedValue, token))
                {
                    hit.Score += weight;
                    AddSemanticMatch(hit, fieldName, token, fieldName + ":prefix");
                }
                else
                {
                    AddSemanticMatch(hit, fieldName, token, fieldName + ":token");
                }
            }
        }

        static bool SemanticValueStartsWithToken(string value, string token)
        {
            if (value.StartsWith(token, StringComparison.Ordinal))
            {
                return true;
            }

            for (var index = 1; index < value.Length; index++)
            {
                if (!char.IsLetterOrDigit(value[index - 1]) && value.Substring(index).StartsWith(token, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static void AddSemanticMatch(SemanticAssetSearchHit hit, string fieldName, string token, string reason)
        {
            AddUniqueString(hit.MatchedFields, fieldName, 20);
            AddUniqueString(hit.MatchedTokens, token, MaxSemanticQueryTokens);
            AddUniqueString(hit.Reasons, reason, 20);
        }

        static void AddUniqueString(List<string> values, string value, int maxValues)
        {
            if (string.IsNullOrEmpty(value) || values.Count >= maxValues || ContainsOrdinalIgnoreCase(values, value))
            {
                return;
            }

            values.Add(value);
        }

        static int CompareSemanticHits(SemanticAssetSearchHit left, SemanticAssetSearchHit right)
        {
            var byScore = right.Score.CompareTo(left.Score);
            return byScore != 0 ? byScore : string.Compare(left.Entry.Path, right.Entry.Path, StringComparison.Ordinal);
        }

        static string SemanticHitJson(SemanticAssetSearchHit hit)
        {
            return "{\"score\":" + hit.Score.ToString(CultureInfo.InvariantCulture)
                + ",\"asset\":" + EntryJson(hit.Entry)
                + ",\"matched_fields\":[" + StringArrayJson(hit.MatchedFields) + "]"
                + ",\"matched_tokens\":[" + StringArrayJson(hit.MatchedTokens) + "]"
                + ",\"reasons\":[" + StringArrayJson(hit.Reasons) + "]}";
        }

        static bool Matches(AssetIndexEntry entry, string query)
        {
            return entry.Path.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Type.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || entry.Guid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static void AppendEntry(StringBuilder builder, AssetIndexEntry entry, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append(EntryJson(entry));
        }

        static string EntryJson(AssetIndexEntry entry)
        {
            return "{\"path\":" + JsonUtil.String(entry.Path)
                + ",\"guid\":" + JsonUtil.String(entry.Guid)
                + ",\"name\":" + JsonUtil.String(entry.Name)
                + ",\"type\":" + JsonUtil.String(entry.Type)
                + ",\"extension\":" + JsonUtil.String(entry.Extension)
                + ",\"source\":" + JsonUtil.String(entry.Source) + "}";
        }

        static string AssetWorkflowFieldsJson(AssetIndexEntry entry, List<string> warnings, string[] nextValidActions)
        {
            return ",\"asset_path\":" + JsonUtil.String(entry.Path)
                + ",\"guid\":" + JsonUtil.String(entry.Guid)
                + ",\"asset_type\":" + JsonUtil.String(entry.Type)
                + ",\"source\":" + JsonUtil.String(entry.Source)
                + ",\"is_folder\":" + JsonUtil.Bool(AssetDatabase.IsValidFolder(entry.Path))
                + ",\"package_asset\":" + JsonUtil.Bool(IsPackageAssetPath(entry.Path))
                + ",\"asset_index\":" + AssetIndexSnapshotJson()
                + ",\"warnings\":[" + StringArrayJson(warnings) + "]"
                + ",\"next_valid_actions\":[" + StringArrayJson(nextValidActions) + "]";
        }

        static List<string> AssetWorkflowWarnings(AssetIndexEntry entry, bool truncated)
        {
            var warnings = new List<string>();
            if (truncated)
            {
                warnings.Add("Results were truncated; increase limits or request write_artifact:true for a project-local report artifact.");
            }

            if (IsPackageAssetPath(entry.Path))
            {
                warnings.Add("Target is under Packages/...; treat package assets as read-only unless a discovered write command explicitly supports them.");
            }

            if (string.IsNullOrEmpty(entry.Guid))
            {
                warnings.Add("Asset has no GUID; refresh/rebuild the asset index before planning writes.");
            }

            if (IsGeneratedLikeAsset(entry.Path))
            {
                warnings.Add("Asset path looks generated or tool-owned; prefer source asset edits over generated output edits.");
            }

            if (AssetIndexLoadedFromCache())
            {
                warnings.Add("Asset index was loaded from cache; call asset.index.rebuild if references look stale.");
            }

            return warnings;
        }

        static string AssetIndexSnapshotJson()
        {
            lock (s_lock)
            {
                return "{\"entry_count\":" + s_entries.Count
                    + ",\"built_at_utc\":" + JsonUtil.String(s_builtAtUtc == DateTime.MinValue ? string.Empty : s_builtAtUtc.ToString("o", CultureInfo.InvariantCulture))
                    + ",\"loaded_from_cache\":" + JsonUtil.Bool(s_loadedFromCache)
                    + ",\"cache_path\":" + JsonUtil.String(ProjectRelativePathOrEmpty(s_lastCachePath)) + "}";
            }
        }

        static string ProjectRelativePathOrEmpty(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                return string.Empty;
            }

            try
            {
                var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullPath = Path.GetFullPath(path);
                if (!IsSameOrChildPath(fullPath, projectRoot))
                {
                    return string.Empty;
                }

                if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), projectRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return string.Empty;
                }

                return fullPath.Substring(projectRoot.Length + 1).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
            catch (NotSupportedException)
            {
                return string.Empty;
            }
        }

        static bool AssetIndexLoadedFromCache()
        {
            lock (s_lock)
            {
                return s_loadedFromCache;
            }
        }

        static bool IsPackageAssetPath(string assetPath)
        {
            return assetPath == "Packages" || assetPath.StartsWith("Packages/", StringComparison.Ordinal);
        }

        static bool IsGeneratedLikeAsset(string assetPath)
        {
            return assetPath.IndexOf("/Generated/", StringComparison.OrdinalIgnoreCase) >= 0
                || assetPath.IndexOf("/GeneratedFiles/", StringComparison.OrdinalIgnoreCase) >= 0
                || assetPath.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)
                || assetPath.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase);
        }

        static string WithOptionalAssetReportArtifact(string argsJson, string reportKind, string response)
        {
            if (!JsonUtil.ExtractBool(argsJson, "write_artifact", false))
            {
                return response;
            }

            var artifactPath = AssetReportArtifactRoot + "/" + reportKind + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json";
            try
            {
                var responseWithArtifact = AddJsonObjectFields(response, ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                    + ",\"artifact_refs\":[" + JsonUtil.String(artifactPath) + "]"
                    + ",\"readable_with\":\"job.artifact.read\"");
                WriteAssetReportArtifact(artifactPath, responseWithArtifact);
                return responseWithArtifact;
            }
            catch (Exception exception)
            {
                return AddJsonObjectFields(response, ",\"artifact_write_error\":" + JsonUtil.String(exception.GetType().Name));
            }
        }

        static void WriteAssetReportArtifact(string artifactPath, string content)
        {
            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, AssetReportArtifactRoot.Replace('/', Path.DirectorySeparatorChar)));
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, artifactRoot))
            {
                throw new ArgumentException("artifact path escaped asset report root");
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

            return builder.ToString();
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

            return builder.ToString();
        }

        static List<string> ReferenceGuids(List<GuidReference> references)
        {
            var guids = new List<string>(references.Count);
            for (var index = 0; index < references.Count; index++)
            {
                guids.Add(references[index].Guid);
            }

            return guids;
        }

        static string ReferenceArrayJson(List<GuidReference> references)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < references.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var reference = references[index];
                builder.Append("{\"guid\":").Append(JsonUtil.String(reference.Guid));
                builder.Append(",\"line\":").Append(reference.Line);
                builder.Append('}');
            }

            return builder.ToString();
        }

        static int MaxResults(string argsJson, int defaultValue)
        {
            return Clamp(JsonUtil.ExtractInt(argsJson, "max_results", defaultValue), 1, 1000);
        }

        static bool IsUnityAssetPath(string assetPath)
        {
            return assetPath == "Assets"
                || assetPath == "Packages"
                || assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                || assetPath.StartsWith("Packages/", StringComparison.Ordinal);
        }

        static string SourceForPath(string assetPath)
        {
            return assetPath.StartsWith("Packages", StringComparison.Ordinal) ? "Packages" : "Assets";
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        sealed class CacheLocationInfo
        {
            public bool Configured;
            public string DisplayPath = string.Empty;
            public string Path = string.Empty;
        }

        sealed class GuidReference
        {
            public string Guid = string.Empty;
            public int Line;
        }

        sealed class YamlGuidReference
        {
            public string Guid = string.Empty;
            public string TargetPath = string.Empty;
            public int Line;
        }

        sealed class DependencyAssemblyPrediction
        {
            public DependencyAssemblyPrediction(string asmdefPath, string assemblyName, bool autoGenerated, bool editorOnly, string reason)
            {
                AsmdefPath = asmdefPath;
                AssemblyName = assemblyName;
                AutoGenerated = autoGenerated;
                EditorOnly = editorOnly;
                Reason = reason;
            }

            public string AsmdefPath;
            public string AssemblyName;
            public bool AutoGenerated;
            public bool EditorOnly;
            public string Reason;

            public string ToJson()
            {
                return "{\"assembly_name\":" + JsonUtil.String(AssemblyName)
                    + ",\"asmdef_path\":" + JsonUtil.String(AsmdefPath)
                    + ",\"auto_generated\":" + JsonUtil.Bool(AutoGenerated)
                    + ",\"editor_only\":" + JsonUtil.Bool(EditorOnly)
                    + ",\"reason\":" + JsonUtil.String(Reason) + "}";
            }
        }

        sealed class DependencyDeclaredType
        {
            public DependencyDeclaredType(string kind, string name, string namespaceName)
            {
                Kind = kind;
                Name = name;
                NamespaceName = namespaceName;
            }

            public string Kind;
            public string Name;
            public string NamespaceName;

            public string FullName
            {
                get { return string.IsNullOrEmpty(NamespaceName) ? Name : NamespaceName + "." + Name; }
            }

            public string ToJson()
            {
                return "{\"kind\":" + JsonUtil.String(Kind)
                    + ",\"name\":" + JsonUtil.String(Name)
                    + ",\"namespace\":" + JsonUtil.String(NamespaceName)
                    + ",\"full_name\":" + JsonUtil.String(FullName) + "}";
            }
        }

        sealed class DependencyScriptTypeReference
        {
            public DependencyScriptTypeReference(string name)
            {
                Name = name;
            }

            public string Name;

            public bool IsQualified
            {
                get { return Name.IndexOf('.') >= 0; }
            }

            public string Confidence
            {
                get { return IsQualified ? "lexical_qualified_name" : "lexical_ambiguous_short_name"; }
            }

            public string Source
            {
                get { return "lexical"; }
            }

            public string ToJson()
            {
                return "{\"name\":" + JsonUtil.String(Name)
                    + ",\"qualified\":" + JsonUtil.Bool(IsQualified)
                    + ",\"confidence\":" + JsonUtil.String(Confidence)
                    + ",\"source\":" + JsonUtil.String(Source) + "}";
            }
        }

        sealed class DependencyScriptRelationship
        {
            public DependencyScriptRelationship(string edgeKind, DependencyScriptTypeReference typeReference, int line)
            {
                EdgeKind = edgeKind;
                TypeReference = typeReference;
                Line = line;
            }

            public string EdgeKind;
            public DependencyScriptTypeReference TypeReference;
            public int Line;

            public string ToJson()
            {
                return "{\"edge_kind\":" + JsonUtil.String(EdgeKind)
                    + ",\"target_type\":" + TypeReference.ToJson()
                    + ",\"line\":" + Line.ToString(CultureInfo.InvariantCulture) + "}";
            }
        }

        sealed class TextLine
        {
            public TextLine(int number, string text)
            {
                Number = number;
                Text = text;
            }

            public int Number;
            public string Text;
        }

        sealed class SemanticAssetSearchHit
        {
            public SemanticAssetSearchHit(AssetIndexEntry entry)
            {
                Entry = entry;
            }

            public readonly AssetIndexEntry Entry;
            public int Score;
            public readonly List<string> MatchedFields = new List<string>();
            public readonly List<string> MatchedTokens = new List<string>();
            public readonly List<string> Reasons = new List<string>();
        }

        sealed class AssetIndexEntry
        {
            public string Path = string.Empty;
            public string Guid = string.Empty;
            public string Name = string.Empty;
            public string Type = string.Empty;
            public string Extension = string.Empty;
            public string Source = string.Empty;
        }
    }
}
