using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class TmpFontAssetCommands
    {
        const string FeatureFlag = "asset_readonly";
        const string TmpFontAssetListSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"include_fallbacks\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000}},\"additionalProperties\":false}";
        const string TmpFontAssetInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"include_fallbacks\":{\"type\":\"boolean\"},\"include_atlas_textures\":{\"type\":\"boolean\"},\"include_tables\":{\"type\":\"boolean\"},\"max_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string TmpFontAssetUnicodeSetExtractSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"include_codepoints\":{\"type\":\"boolean\"},\"include_ranges\":{\"type\":\"boolean\"},\"include_string\":{\"type\":\"boolean\"},\"max_scan_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_string_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":65536}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string TmpUnicodeSetCollectSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_scenes\":{\"type\":\"boolean\"},\"include_prefabs\":{\"type\":\"boolean\"},\"include_text_assets\":{\"type\":\"boolean\"},\"include_codepoints\":{\"type\":\"boolean\"},\"include_ranges\":{\"type\":\"boolean\"},\"include_string\":{\"type\":\"boolean\"},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"max_sources\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_string_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":65536}},\"additionalProperties\":false}";
        const string TmpUnicodeSetNormalizeSchemaJson = "{\"type\":\"object\",\"properties\":{\"unicode_text\":{\"type\":\"string\",\"maxLength\":65536},\"codepoints_text\":{\"type\":\"string\",\"maxLength\":262144},\"ranges_text\":{\"type\":\"string\",\"maxLength\":262144},\"include_codepoints\":{\"type\":\"boolean\"},\"include_ranges\":{\"type\":\"boolean\"},\"include_string\":{\"type\":\"boolean\"},\"max_input_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_string_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":65536}},\"additionalProperties\":false}";
        const string TmpFontAssetCoverageCheckSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"unicode_text\":{\"type\":\"string\",\"maxLength\":65536},\"codepoints_text\":{\"type\":\"string\",\"maxLength\":262144},\"ranges_text\":{\"type\":\"string\",\"maxLength\":262144},\"include_fallbacks\":{\"type\":\"boolean\"},\"include_missing_codepoints\":{\"type\":\"boolean\"},\"include_covered_codepoints\":{\"type\":\"boolean\"},\"include_ranges\":{\"type\":\"boolean\"},\"max_input_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_font_scan_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_fallback_assets\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string TmpFontAssetStaticPlanSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"target_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"unicode_text\":{\"type\":\"string\",\"maxLength\":65536},\"codepoints_text\":{\"type\":\"string\",\"maxLength\":262144},\"ranges_text\":{\"type\":\"string\",\"maxLength\":262144},\"include_fallbacks\":{\"type\":\"boolean\"},\"atlas_width\":{\"type\":\"integer\",\"minimum\":16,\"maximum\":8192},\"atlas_height\":{\"type\":\"integer\",\"minimum\":16,\"maximum\":8192},\"atlas_padding\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":128},\"max_input_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_font_scan_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_fallback_assets\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string TmpFontAssetReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"target_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"unicode_text\":{\"type\":\"string\",\"maxLength\":65536},\"codepoints_text\":{\"type\":\"string\",\"maxLength\":262144},\"ranges_text\":{\"type\":\"string\",\"maxLength\":262144},\"format\":{\"type\":\"string\",\"enum\":[\"markdown\",\"json\",\"markdown_and_json\"]},\"include_packages\":{\"type\":\"boolean\"},\"include_fallbacks\":{\"type\":\"boolean\"},\"include_static_plan\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_input_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_font_scan_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_fallback_assets\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_chars\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":1000000}},\"additionalProperties\":false}";
        const string TmpFontAssetValidateSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"unicode_text\":{\"type\":\"string\",\"maxLength\":65536},\"codepoints_text\":{\"type\":\"string\",\"maxLength\":262144},\"ranges_text\":{\"type\":\"string\",\"maxLength\":262144},\"include_fallbacks\":{\"type\":\"boolean\"},\"require_static\":{\"type\":\"boolean\"},\"require_source_font\":{\"type\":\"boolean\"},\"require_material\":{\"type\":\"boolean\"},\"require_atlas_texture\":{\"type\":\"boolean\"},\"max_input_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_font_scan_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000},\"max_fallback_assets\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_findings\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string TmpFontAssetResourceReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"uri\":{\"type\":\"string\",\"maxLength\":512},\"format\":{\"type\":\"string\",\"enum\":[\"text\",\"json\",\"markdown\"]},\"max_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000}},\"required\":[\"uri\"],\"additionalProperties\":false}";
        const string TmpFontAssetListOutputLimitJson = "{\"max_results\":500,\"max_scan_assets\":100000,\"readonly\":true}";
        const string TmpFontAssetInspectOutputLimitJson = "{\"max_items\":500,\"readonly\":true}";
        const string TmpFontAssetUnicodeSetExtractOutputLimitJson = "{\"max_scan_items\":200000,\"max_codepoints\":20000,\"max_ranges\":5000,\"max_string_chars\":65536,\"readonly\":true}";
        const string TmpUnicodeSetCollectOutputLimitJson = "{\"max_scan_assets\":100000,\"max_scan_bytes\":1048576,\"max_sources\":500,\"max_codepoints\":20000,\"max_ranges\":5000,\"max_string_chars\":65536,\"readonly\":true}";
        const string TmpUnicodeSetNormalizeOutputLimitJson = "{\"max_input_items\":200000,\"max_codepoints\":20000,\"max_ranges\":5000,\"max_string_chars\":65536,\"readonly\":true}";
        const string TmpFontAssetCoverageCheckOutputLimitJson = "{\"max_input_items\":200000,\"max_font_scan_items\":200000,\"max_fallback_assets\":100,\"max_codepoints\":20000,\"max_ranges\":5000,\"readonly\":true}";
        const string TmpFontAssetStaticPlanOutputLimitJson = "{\"max_input_items\":200000,\"max_font_scan_items\":200000,\"max_fallback_assets\":100,\"max_codepoints\":20000,\"max_ranges\":5000,\"readonly\":true,\"dry_run\":true}";
        const string TmpFontAssetReportOutputLimitJson = "{\"max_results\":100,\"max_scan_assets\":100000,\"max_input_items\":200000,\"max_font_scan_items\":200000,\"max_fallback_assets\":100,\"max_codepoints\":20000,\"max_ranges\":5000,\"max_chars\":1000000,\"readonly\":true}";
        const string TmpFontAssetValidateOutputLimitJson = "{\"max_input_items\":200000,\"max_font_scan_items\":200000,\"max_fallback_assets\":100,\"max_codepoints\":20000,\"max_ranges\":5000,\"max_findings\":500,\"readonly\":true}";
        const string TmpFontAssetResourceReadOutputLimitJson = "{\"max_chars\":1000000,\"readonly\":true}";
        const int MaxResults = 500;
        const int MaxScanAssets = 100000;
        const int MaxTableItems = 500;
        const int MaxReferenceItems = 100;
        const int MaxUnicodeScanItems = 200000;
        const int MaxUnicodeInputItems = 200000;
        const int MaxUnicodeInputChars = 262144;
        const int MaxUnicodeCollectBytes = 1048576;
        const int MaxUnicodeSources = 500;
        const int MaxUnicodeFallbackAssets = 100;
        const int MaxUnicodeCodepoints = 20000;
        const int MaxUnicodeRanges = 5000;
        const int MaxUnicodeStringChars = 65536;
        const int MaxReportChars = 1000000;
        const int MaxValidationFindings = 500;
        const int MaxCachedResources = 64;

        static readonly object ResourceCacheLock = new object();
        static readonly Dictionary<string, TmpResourceSnapshot> ResourceCache = new Dictionary<string, TmpResourceSnapshot>();
        static readonly Queue<string> ResourceCacheOrder = new Queue<string>();

        [McpCommand(
            "tmp.font_asset.list",
            Title = "List TMP font assets",
            Description = "List TextMeshPro TMP_FontAsset assets with bounded dynamic/static, source font, atlas, material, fallback, glyph, and character summaries",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpFontAssetListSchemaJson,
            Tags = new string[] { "tmp", "font", "asset", "inventory", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = TmpFontAssetListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ListTmpFontAssets(McpCommandContext context)
        {
            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeFallbacks = JsonUtil.ExtractBool(context.ArgsJson, "include_fallbacks", false);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, MaxResults);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", MaxScanAssets), 1, MaxScanAssets);
            var paths = CandidateAssetPaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix);
            var builder = new StringBuilder();
            var scannedAssets = 0;
            var matchedFontAssets = 0;
            var returnedResults = 0;
            var scanTruncated = false;
            var resultsTruncated = false;

            foreach (var assetPath in paths)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (!IsTmpFontAsset(asset) || !TmpFontAssetMatches(assetPath, asset, query))
                {
                    continue;
                }

                var resultIndex = matchedFontAssets++;
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

                builder.Append(TmpFontAssetJson(assetPath, asset, includeFallbacks, false, false, MaxReferenceItems));
                returnedResults++;
            }

            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_fallbacks\":" + JsonUtil.Bool(includeFallbacks)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"matched_font_assets\":" + matchedFontAssets
                + ",\"returned_results\":" + returnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"next_start_index\":" + (resultsTruncated ? (startIndex + returnedResults).ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"font_assets\":[" + builder + "]"
                + ",\"warnings\":[\"TextMeshPro is inspected through AssetDatabase and SerializedObject only; this command does not require a compile-time TMPro dependency.\",\"This command does not generate atlases, import, refresh, write, move, or fix assets.\"]}");
        }

        [McpCommand(
            "tmp.font_asset.inspect",
            Title = "Inspect TMP font asset",
            Description = "Inspect one TextMeshPro TMP_FontAsset serialized state with bounded atlas, material, fallback, glyph, and character details",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpFontAssetInspectSchemaJson,
            Tags = new string[] { "tmp", "font", "asset", "inspect", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TmpFontAssetInspectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult InspectTmpFontAsset(McpCommandContext context)
        {
            var assetResult = RequireTmpFontAssetPath(context.ArgsJson, out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            var includeFallbacks = JsonUtil.ExtractBool(context.ArgsJson, "include_fallbacks", true);
            var includeAtlasTextures = JsonUtil.ExtractBool(context.ArgsJson, "include_atlas_textures", true);
            var includeTables = JsonUtil.ExtractBool(context.ArgsJson, "include_tables", false);
            var maxItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_items", 100), 1, MaxTableItems);
            return CommandExecutionResult.Ok(TmpFontAssetJson(assetPath, asset, includeFallbacks, includeAtlasTextures, includeTables, maxItems));
        }

        [McpCommand(
            "tmp.font_asset.unicode_set.extract",
            Title = "Extract TMP font asset Unicode set",
            Description = "Extract a deterministic sorted Unicode set from one TextMeshPro TMP_FontAsset character table without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpFontAssetUnicodeSetExtractSchemaJson,
            Tags = new string[] { "tmp", "font", "unicode", "asset", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TmpFontAssetUnicodeSetExtractOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ExtractTmpFontAssetUnicodeSet(McpCommandContext context)
        {
            var assetResult = RequireTmpFontAssetPath(context.ArgsJson, out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return CommandExecutionResult.Error("invalid_field_value", "TMP font asset could not be inspected with SerializedObject");
            }

            var includeCodepoints = JsonUtil.ExtractBool(context.ArgsJson, "include_codepoints", true);
            var includeRanges = JsonUtil.ExtractBool(context.ArgsJson, "include_ranges", true);
            var includeString = JsonUtil.ExtractBool(context.ArgsJson, "include_string", false);
            var maxScanItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_items", MaxUnicodeScanItems), 1, MaxUnicodeScanItems);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxUnicodeCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxUnicodeRanges);
            var maxStringChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_string_chars", 8192), 1, MaxUnicodeStringChars);
            var codepoints = ExtractCharacterCodepoints(serializedObject, maxScanItems, out var characterTableCount, out var scannedItems, out var scanTruncated);
            var codepointsTruncated = false;
            var rangeCount = 0;
            var rangesTruncated = false;
            var stringTruncated = false;
            var codepointLabels = includeCodepoints ? CodepointLabelsJson(codepoints, maxCodepoints, out codepointsTruncated) : string.Empty;
            var rangesJson = includeRanges ? UnicodeRangesJson(codepoints, maxRanges, out rangeCount, out rangesTruncated) : string.Empty;
            var unicodeString = includeString ? UnicodeString(codepoints, maxStringChars, out stringTruncated) : string.Empty;

            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"name\":" + JsonUtil.String(asset.name)
                + ",\"format\":\"sorted_unique_unicode_codepoints\""
                + ",\"character_table_count\":" + characterTableCount
                + ",\"scanned_items\":" + scannedItems
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"codepoint_count\":" + codepoints.Count
                + ",\"include_codepoints\":" + JsonUtil.Bool(includeCodepoints)
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"codepoints_truncated\":" + JsonUtil.Bool(includeCodepoints && codepointsTruncated)
                + ",\"codepoints\":[" + codepointLabels + "]"
                + ",\"include_ranges\":" + JsonUtil.Bool(includeRanges)
                + ",\"max_ranges\":" + maxRanges
                + ",\"range_count\":" + rangeCount
                + ",\"ranges_truncated\":" + JsonUtil.Bool(includeRanges && rangesTruncated)
                + ",\"ranges\":[" + rangesJson + "]"
                + ",\"include_string\":" + JsonUtil.Bool(includeString)
                + ",\"max_string_chars\":" + maxStringChars
                + ",\"string_truncated\":" + JsonUtil.Bool(includeString && stringTruncated)
                + ",\"unicode_string\":" + JsonUtil.String(unicodeString)
                + ",\"warnings\":[\"Unicode set is extracted from the serialized TMP character table, not from source text or localization assets.\",\"This command does not generate atlases, import, refresh, write, move, or fix assets.\"]}");
        }

        [McpCommand(
            "tmp.unicode_set.collect",
            Title = "Collect TMP Unicode set",
            Description = "Collect a deterministic sorted Unicode set from UI text in scenes/prefabs and localization/text assets without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpUnicodeSetCollectSchemaJson,
            Tags = new string[] { "tmp", "font", "unicode", "text", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TmpUnicodeSetCollectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CollectTmpUnicodeSet(McpCommandContext context)
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
            var includeTextAssets = JsonUtil.ExtractBool(context.ArgsJson, "include_text_assets", true);
            var includeCodepoints = JsonUtil.ExtractBool(context.ArgsJson, "include_codepoints", true);
            var includeRanges = JsonUtil.ExtractBool(context.ArgsJson, "include_ranges", true);
            var includeString = JsonUtil.ExtractBool(context.ArgsJson, "include_string", false);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", MaxScanAssets), 1, MaxScanAssets);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", MaxUnicodeCollectBytes), 1, MaxUnicodeCollectBytes);
            var maxSources = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_sources", 100), 1, MaxUnicodeSources);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxUnicodeCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxUnicodeRanges);
            var maxStringChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_string_chars", 8192), 1, MaxUnicodeStringChars);
            var paths = CandidateUnicodeSourcePaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, includeScenes, includePrefabs, includeTextAssets);
            var codepointSet = new HashSet<int>();
            var sources = new StringBuilder();
            var scannedAssets = 0;
            var skippedAssets = 0;
            var readErrorCount = 0;
            var sourceCount = 0;
            var returnedSources = 0;
            var scanTruncated = false;
            var sourcesTruncated = false;
            var readTruncatedCount = 0;

            foreach (var assetPath in paths)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                if (!TryResolveAssetFilePath(assetPath, out var fullPath))
                {
                    skippedAssets++;
                    continue;
                }

                byte[] bytes;
                long byteLength;
                bool fileTruncated;
                try
                {
                    bytes = ReadBoundedBytes(fullPath, maxScanBytes, out byteLength, out fileTruncated);
                }
                catch (Exception exception)
                {
                    if (IsExpectedReadException(exception))
                    {
                        readErrorCount++;
                        continue;
                    }

                    throw;
                }

                if (fileTruncated)
                {
                    readTruncatedCount++;
                }

                var textResult = DecodeText(bytes, fileTruncated, out var content);
                if (textResult != null)
                {
                    skippedAssets++;
                    continue;
                }

                var sourceCodepoints = new HashSet<int>();
                var collectedChars = CollectUnicodeFromContent(assetPath, content, sourceCodepoints);
                if (sourceCodepoints.Count == 0)
                {
                    continue;
                }

                foreach (var codepoint in sourceCodepoints)
                {
                    codepointSet.Add(codepoint);
                }

                sourceCount++;
                if (returnedSources < maxSources)
                {
                    if (returnedSources > 0)
                    {
                        sources.Append(',');
                    }

                    sources.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                        .Append(",\"guid\":").Append(JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath)))
                        .Append(",\"kind\":").Append(JsonUtil.String(UnicodeSourceKind(assetPath)))
                        .Append(",\"byte_length\":").Append(byteLength)
                        .Append(",\"scan_bytes\":").Append(bytes.Length)
                        .Append(",\"scan_truncated\":").Append(JsonUtil.Bool(fileTruncated))
                        .Append(",\"collected_characters\":").Append(collectedChars)
                        .Append(",\"codepoint_count\":").Append(sourceCodepoints.Count)
                        .Append('}');
                    returnedSources++;
                }
                else
                {
                    sourcesTruncated = true;
                }
            }

            var codepoints = new List<int>(codepointSet);
            codepoints.Sort();
            var codepointsTruncated = false;
            var rangeCount = 0;
            var rangesTruncated = false;
            var stringTruncated = false;
            var codepointLabels = includeCodepoints ? CodepointLabelsJson(codepoints, maxCodepoints, out codepointsTruncated) : string.Empty;
            var rangesJson = includeRanges ? UnicodeRangesJson(codepoints, maxRanges, out rangeCount, out rangesTruncated) : string.Empty;
            var unicodeString = includeString ? UnicodeString(codepoints, maxStringChars, out stringTruncated) : string.Empty;

            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_scenes\":" + JsonUtil.Bool(includeScenes)
                + ",\"include_prefabs\":" + JsonUtil.Bool(includePrefabs)
                + ",\"include_text_assets\":" + JsonUtil.Bool(includeTextAssets)
                + ",\"format\":\"sorted_unique_unicode_codepoints\""
                + ",\"candidate_assets\":" + paths.Count
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"skipped_assets\":" + skippedAssets
                + ",\"read_error_count\":" + readErrorCount
                + ",\"read_truncated_count\":" + readTruncatedCount
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"source_count\":" + sourceCount
                + ",\"returned_sources\":" + returnedSources
                + ",\"sources_truncated\":" + JsonUtil.Bool(sourcesTruncated)
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"max_sources\":" + maxSources
                + ",\"codepoint_count\":" + codepoints.Count
                + ",\"include_codepoints\":" + JsonUtil.Bool(includeCodepoints)
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"codepoints_truncated\":" + JsonUtil.Bool(includeCodepoints && codepointsTruncated)
                + ",\"codepoints\":[" + codepointLabels + "]"
                + ",\"include_ranges\":" + JsonUtil.Bool(includeRanges)
                + ",\"max_ranges\":" + maxRanges
                + ",\"range_count\":" + rangeCount
                + ",\"ranges_truncated\":" + JsonUtil.Bool(includeRanges && rangesTruncated)
                + ",\"ranges\":[" + rangesJson + "]"
                + ",\"include_string\":" + JsonUtil.Bool(includeString)
                + ",\"max_string_chars\":" + maxStringChars
                + ",\"string_truncated\":" + JsonUtil.Bool(includeString && stringTruncated)
                + ",\"unicode_string\":" + JsonUtil.String(unicodeString)
                + ",\"sources\":[" + sources + "]"
                + ",\"warnings\":[\"Unity YAML sources are scanned for known UI/localization text fields; plain text/localization files are scanned as UTF-8 text.\",\"This command does not generate atlases, import, refresh, write, move, or fix assets.\"]}");
        }

        [McpCommand(
            "tmp.unicode_set.normalize",
            Title = "Normalize TMP Unicode set",
            Description = "Normalize, sort, dedupe, merge, and summarize explicit Unicode sets for deterministic TMP static font planning",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TmpUnicodeSetNormalizeSchemaJson,
            Tags = new string[] { "tmp", "font", "unicode", "normalize", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TmpUnicodeSetNormalizeOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult NormalizeTmpUnicodeSet(McpCommandContext context)
        {
            var unicodeText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "unicode_text"), MaxUnicodeStringChars);
            var codepointsText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "codepoints_text"), MaxUnicodeInputChars);
            var rangesText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "ranges_text"), MaxUnicodeInputChars);
            if (string.IsNullOrEmpty(unicodeText) && string.IsNullOrEmpty(codepointsText) && string.IsNullOrEmpty(rangesText))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide unicode_text, codepoints_text, or ranges_text");
            }

            var includeCodepoints = JsonUtil.ExtractBool(context.ArgsJson, "include_codepoints", true);
            var includeRanges = JsonUtil.ExtractBool(context.ArgsJson, "include_ranges", true);
            var includeString = JsonUtil.ExtractBool(context.ArgsJson, "include_string", false);
            var maxInputItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_input_items", MaxUnicodeInputItems), 1, MaxUnicodeInputItems);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxUnicodeCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxUnicodeRanges);
            var maxStringChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_string_chars", 8192), 1, MaxUnicodeStringChars);
            var codepointSet = new HashSet<int>();
            var inputItemCount = 0;
            var parsedTokenCount = 0;
            var invalidTokenCount = 0;
            var inputTruncated = false;

            AddCollectibleCodepoints(unicodeText, codepointSet, maxInputItems, ref inputItemCount, ref inputTruncated);
            AddUnicodeCodepointTokens(codepointsText, codepointSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
            AddUnicodeRangeTokens(rangesText, codepointSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);

            var codepoints = new List<int>(codepointSet);
            codepoints.Sort();
            var codepointsTruncated = false;
            var rangeCount = 0;
            var rangesTruncated = false;
            var stringTruncated = false;
            var codepointLabels = includeCodepoints ? CodepointLabelsJson(codepoints, maxCodepoints, out codepointsTruncated) : string.Empty;
            var rangesJson = includeRanges ? UnicodeRangesJson(codepoints, maxRanges, out rangeCount, out rangesTruncated) : string.Empty;
            var unicodeString = includeString ? UnicodeString(codepoints, maxStringChars, out stringTruncated) : string.Empty;

            return CommandExecutionResult.Ok("{\"format\":\"sorted_unique_unicode_codepoints\""
                + ",\"unicode_text_chars\":" + unicodeText.Length
                + ",\"codepoints_text_chars\":" + codepointsText.Length
                + ",\"ranges_text_chars\":" + rangesText.Length
                + ",\"max_input_items\":" + maxInputItems
                + ",\"input_item_count\":" + inputItemCount
                + ",\"parsed_token_count\":" + parsedTokenCount
                + ",\"invalid_token_count\":" + invalidTokenCount
                + ",\"input_truncated\":" + JsonUtil.Bool(inputTruncated)
                + ",\"codepoint_count\":" + codepoints.Count
                + ",\"include_codepoints\":" + JsonUtil.Bool(includeCodepoints)
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"codepoints_truncated\":" + JsonUtil.Bool(includeCodepoints && codepointsTruncated)
                + ",\"codepoints\":[" + codepointLabels + "]"
                + ",\"include_ranges\":" + JsonUtil.Bool(includeRanges)
                + ",\"max_ranges\":" + maxRanges
                + ",\"range_count\":" + rangeCount
                + ",\"ranges_truncated\":" + JsonUtil.Bool(includeRanges && rangesTruncated)
                + ",\"ranges\":[" + rangesJson + "]"
                + ",\"include_string\":" + JsonUtil.Bool(includeString)
                + ",\"max_string_chars\":" + maxStringChars
                + ",\"string_truncated\":" + JsonUtil.Bool(includeString && stringTruncated)
                + ",\"unicode_string\":" + JsonUtil.String(unicodeString)
                + ",\"warnings\":[\"Normalization is lexical and read-only; it does not check glyph availability in any TMP font asset.\",\"Use U+XXXX labels or 0x-prefixed hex to avoid ambiguity in codepoints_text and ranges_text.\"]}");
        }

        [McpCommand(
            "tmp.font_asset.coverage.check",
            Title = "Check TMP font asset Unicode coverage",
            Description = "Check requested Unicode codepoints against one TMP font asset serialized character table and optional direct fallbacks without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpFontAssetCoverageCheckSchemaJson,
            Tags = new string[] { "tmp", "font", "unicode", "coverage", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TmpFontAssetCoverageCheckOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CheckTmpFontAssetCoverage(McpCommandContext context)
        {
            var assetResult = RequireTmpFontAssetPath(context.ArgsJson, out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return CommandExecutionResult.Error("invalid_field_value", "TMP font asset could not be inspected with SerializedObject");
            }

            var unicodeText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "unicode_text"), MaxUnicodeStringChars);
            var codepointsText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "codepoints_text"), MaxUnicodeInputChars);
            var rangesText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "ranges_text"), MaxUnicodeInputChars);
            if (string.IsNullOrEmpty(unicodeText) && string.IsNullOrEmpty(codepointsText) && string.IsNullOrEmpty(rangesText))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide unicode_text, codepoints_text, or ranges_text");
            }

            var includeFallbacks = JsonUtil.ExtractBool(context.ArgsJson, "include_fallbacks", true);
            var includeMissingCodepoints = JsonUtil.ExtractBool(context.ArgsJson, "include_missing_codepoints", true);
            var includeCoveredCodepoints = JsonUtil.ExtractBool(context.ArgsJson, "include_covered_codepoints", false);
            var includeRanges = JsonUtil.ExtractBool(context.ArgsJson, "include_ranges", true);
            var maxInputItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_input_items", MaxUnicodeInputItems), 1, MaxUnicodeInputItems);
            var maxFontScanItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_font_scan_items", MaxUnicodeScanItems), 1, MaxUnicodeScanItems);
            var maxFallbackAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_fallback_assets", 20), 0, MaxUnicodeFallbackAssets);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxUnicodeCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxUnicodeRanges);
            var requestedSet = new HashSet<int>();
            var inputItemCount = 0;
            var parsedTokenCount = 0;
            var invalidTokenCount = 0;
            var inputTruncated = false;

            AddCollectibleCodepoints(unicodeText, requestedSet, maxInputItems, ref inputItemCount, ref inputTruncated);
            AddUnicodeCodepointTokens(codepointsText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
            AddUnicodeRangeTokens(rangesText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
            if (requestedSet.Count == 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Requested Unicode set did not contain collectible codepoints");
            }

            var coveredSet = new HashSet<int>();
            var primaryCodepoints = ExtractCharacterCodepoints(serializedObject, maxFontScanItems, out var primaryCharacterTableCount, out var primaryScannedItems, out var primaryScanTruncated);
            for (var index = 0; index < primaryCodepoints.Count; index++)
            {
                coveredSet.Add(primaryCodepoints[index]);
            }

            var fallbackFontAssetCount = 0;
            var fallbackAssetsConsidered = 0;
            var fallbacksTruncated = false;
            var fallbackAssetsJson = includeFallbacks ? FallbackCoverageAssetsJson(serializedObject, maxFallbackAssets, maxFontScanItems, coveredSet, out fallbackFontAssetCount, out fallbackAssetsConsidered, out fallbacksTruncated) : string.Empty;

            var requestedCodepoints = new List<int>(requestedSet);
            requestedCodepoints.Sort();
            var coveredRequested = new List<int>();
            var missing = new List<int>();
            for (var index = 0; index < requestedCodepoints.Count; index++)
            {
                var codepoint = requestedCodepoints[index];
                if (coveredSet.Contains(codepoint))
                {
                    coveredRequested.Add(codepoint);
                }
                else
                {
                    missing.Add(codepoint);
                }
            }

            var missingCodepointsTruncated = false;
            var coveredCodepointsTruncated = false;
            var missingRangeCount = 0;
            var missingRangesTruncated = false;
            var coveredRangeCount = 0;
            var coveredRangesTruncated = false;
            var missingCodepointsJson = includeMissingCodepoints ? CodepointLabelsJson(missing, maxCodepoints, out missingCodepointsTruncated) : string.Empty;
            var coveredCodepointsJson = includeCoveredCodepoints ? CodepointLabelsJson(coveredRequested, maxCodepoints, out coveredCodepointsTruncated) : string.Empty;
            var missingRangesJson = includeRanges ? UnicodeRangesJson(missing, maxRanges, out missingRangeCount, out missingRangesTruncated) : string.Empty;
            var coveredRangesJson = includeRanges ? UnicodeRangesJson(coveredRequested, maxRanges, out coveredRangeCount, out coveredRangesTruncated) : string.Empty;
            var coveragePercent = requestedCodepoints.Count == 0 ? 100f : (float)coveredRequested.Count * 100f / requestedCodepoints.Count;

            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"name\":" + JsonUtil.String(asset.name)
                + ",\"format\":\"sorted_unique_unicode_codepoints\""
                + ",\"unicode_text_chars\":" + unicodeText.Length
                + ",\"codepoints_text_chars\":" + codepointsText.Length
                + ",\"ranges_text_chars\":" + rangesText.Length
                + ",\"max_input_items\":" + maxInputItems
                + ",\"input_item_count\":" + inputItemCount
                + ",\"parsed_token_count\":" + parsedTokenCount
                + ",\"invalid_token_count\":" + invalidTokenCount
                + ",\"input_truncated\":" + JsonUtil.Bool(inputTruncated)
                + ",\"requested_count\":" + requestedCodepoints.Count
                + ",\"covered_count\":" + coveredRequested.Count
                + ",\"missing_count\":" + missing.Count
                + ",\"coverage_percent\":" + FloatJson(coveragePercent)
                + ",\"primary_character_table_count\":" + primaryCharacterTableCount
                + ",\"primary_scanned_items\":" + primaryScannedItems
                + ",\"primary_scan_truncated\":" + JsonUtil.Bool(primaryScanTruncated)
                + ",\"include_fallbacks\":" + JsonUtil.Bool(includeFallbacks)
                + ",\"max_fallback_assets\":" + maxFallbackAssets
                + ",\"fallback_font_asset_count\":" + fallbackFontAssetCount
                + ",\"fallback_assets_considered\":" + fallbackAssetsConsidered
                + ",\"fallbacks_truncated\":" + JsonUtil.Bool(fallbacksTruncated)
                + ",\"fallback_assets\":[" + fallbackAssetsJson + "]"
                + ",\"max_font_scan_items\":" + maxFontScanItems
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"include_missing_codepoints\":" + JsonUtil.Bool(includeMissingCodepoints)
                + ",\"missing_codepoints_truncated\":" + JsonUtil.Bool(includeMissingCodepoints && missingCodepointsTruncated)
                + ",\"missing_codepoints\":[" + missingCodepointsJson + "]"
                + ",\"include_covered_codepoints\":" + JsonUtil.Bool(includeCoveredCodepoints)
                + ",\"covered_codepoints_truncated\":" + JsonUtil.Bool(includeCoveredCodepoints && coveredCodepointsTruncated)
                + ",\"covered_codepoints\":[" + coveredCodepointsJson + "]"
                + ",\"include_ranges\":" + JsonUtil.Bool(includeRanges)
                + ",\"max_ranges\":" + maxRanges
                + ",\"missing_range_count\":" + missingRangeCount
                + ",\"missing_ranges_truncated\":" + JsonUtil.Bool(includeRanges && missingRangesTruncated)
                + ",\"missing_ranges\":[" + missingRangesJson + "]"
                + ",\"covered_range_count\":" + coveredRangeCount
                + ",\"covered_ranges_truncated\":" + JsonUtil.Bool(includeRanges && coveredRangesTruncated)
                + ",\"covered_ranges\":[" + coveredRangesJson + "]"
                + ",\"warnings\":[\"Coverage uses serialized TMP character tables only; it does not render, bake, generate atlases, import, refresh, write, move, or fix assets.\",\"Fallback coverage is limited to direct fallback TMP font assets referenced by the inspected asset.\"]}");
        }

        [McpCommand(
            "tmp.font_asset.static.plan",
            Title = "Plan static TMP font asset",
            Description = "Create a dry-run static TMP SDF font asset generation plan from one TMP font asset and explicit Unicode set without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpFontAssetStaticPlanSchemaJson,
            Tags = new string[] { "tmp", "font", "unicode", "static", "plan", "dry-run", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TmpFontAssetStaticPlanOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PlanTmpStaticFontAsset(McpCommandContext context)
        {
            var assetResult = RequireTmpFontAssetPath(context.ArgsJson, out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return CommandExecutionResult.Error("invalid_field_value", "TMP font asset could not be inspected with SerializedObject");
            }

            var targetResult = ResolveStaticPlanTargetPath(context.ArgsJson, assetPath, asset.name, out var targetAssetPath);
            if (targetResult != null)
            {
                return targetResult;
            }

            var unicodeText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "unicode_text"), MaxUnicodeStringChars);
            var codepointsText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "codepoints_text"), MaxUnicodeInputChars);
            var rangesText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "ranges_text"), MaxUnicodeInputChars);
            if (string.IsNullOrEmpty(unicodeText) && string.IsNullOrEmpty(codepointsText) && string.IsNullOrEmpty(rangesText))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide unicode_text, codepoints_text, or ranges_text");
            }

            var includeFallbacks = JsonUtil.ExtractBool(context.ArgsJson, "include_fallbacks", true);
            var maxInputItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_input_items", MaxUnicodeInputItems), 1, MaxUnicodeInputItems);
            var maxFontScanItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_font_scan_items", MaxUnicodeScanItems), 1, MaxUnicodeScanItems);
            var maxFallbackAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_fallback_assets", 20), 0, MaxUnicodeFallbackAssets);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxUnicodeCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxUnicodeRanges);
            var requestedSet = new HashSet<int>();
            var inputItemCount = 0;
            var parsedTokenCount = 0;
            var invalidTokenCount = 0;
            var inputTruncated = false;

            AddCollectibleCodepoints(unicodeText, requestedSet, maxInputItems, ref inputItemCount, ref inputTruncated);
            AddUnicodeCodepointTokens(codepointsText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
            AddUnicodeRangeTokens(rangesText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
            if (requestedSet.Count == 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Requested Unicode set did not contain collectible codepoints");
            }

            var coveredSet = new HashSet<int>();
            var primaryCodepoints = ExtractCharacterCodepoints(serializedObject, maxFontScanItems, out var primaryCharacterTableCount, out var primaryScannedItems, out var primaryScanTruncated);
            for (var index = 0; index < primaryCodepoints.Count; index++)
            {
                coveredSet.Add(primaryCodepoints[index]);
            }

            var fallbackFontAssetCount = 0;
            var fallbackAssetsConsidered = 0;
            var fallbacksTruncated = false;
            var fallbackAssetsJson = includeFallbacks ? FallbackCoverageAssetsJson(serializedObject, maxFallbackAssets, maxFontScanItems, coveredSet, out fallbackFontAssetCount, out fallbackAssetsConsidered, out fallbacksTruncated) : string.Empty;

            var requestedCodepoints = new List<int>(requestedSet);
            requestedCodepoints.Sort();
            var missing = new List<int>();
            for (var index = 0; index < requestedCodepoints.Count; index++)
            {
                if (!coveredSet.Contains(requestedCodepoints[index]))
                {
                    missing.Add(requestedCodepoints[index]);
                }
            }

            var missingCodepointsTruncated = false;
            var missingRangeCount = 0;
            var missingRangesTruncated = false;
            var requestedRangeCount = 0;
            var requestedRangesTruncated = false;
            var missingCodepointsJson = CodepointLabelsJson(missing, maxCodepoints, out missingCodepointsTruncated);
            var missingRangesJson = UnicodeRangesJson(missing, maxRanges, out missingRangeCount, out missingRangesTruncated);
            var requestedRangesJson = UnicodeRangesJson(requestedCodepoints, maxRanges, out requestedRangeCount, out requestedRangesTruncated);
            var sourceFontAvailable = SourceFontAvailable(serializedObject);
            var targetParentPath = ParentAssetPath(targetAssetPath);
            var targetExists = !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(targetAssetPath));
            var targetParentExists = !string.IsNullOrEmpty(targetParentPath) && AssetDatabase.IsValidFolder(targetParentPath);
            var atlasWidth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "atlas_width", IntPropertyValue(serializedObject, 1024, "m_AtlasWidth", "atlasWidth")), 16, 8192);
            var atlasHeight = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "atlas_height", IntPropertyValue(serializedObject, 1024, "m_AtlasHeight", "atlasHeight")), 16, 8192);
            var atlasPadding = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "atlas_padding", IntPropertyValue(serializedObject, 5, "m_AtlasPadding", "atlasPadding")), 0, 128);
            var ready = sourceFontAvailable && targetParentExists && !targetExists && requestedCodepoints.Count > 0;

            return CommandExecutionResult.Ok("{\"dry_run\":true"
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"name\":" + JsonUtil.String(asset.name)
                + ",\"target_asset_path\":" + JsonUtil.String(targetAssetPath)
                + ",\"target_parent_path\":" + JsonUtil.String(targetParentPath)
                + ",\"target_parent_exists\":" + JsonUtil.Bool(targetParentExists)
                + ",\"target_exists\":" + JsonUtil.Bool(targetExists)
                + ",\"would_overwrite\":" + JsonUtil.Bool(targetExists)
                + ",\"ready_for_later_static_create\":" + JsonUtil.Bool(ready)
                + ",\"source_font_available\":" + JsonUtil.Bool(sourceFontAvailable)
                + ",\"source_font\":" + SourceFontJson(serializedObject)
                + ",\"source_atlas_population_mode\":" + AtlasPopulationModeJson(serializedObject)
                + ",\"planned_atlas_population_mode\":\"Static\""
                + ",\"planned_atlas_settings\":{\"width\":" + atlasWidth + ",\"height\":" + atlasHeight + ",\"padding\":" + atlasPadding + "}"
                + ",\"unicode_text_chars\":" + unicodeText.Length
                + ",\"codepoints_text_chars\":" + codepointsText.Length
                + ",\"ranges_text_chars\":" + rangesText.Length
                + ",\"max_input_items\":" + maxInputItems
                + ",\"input_item_count\":" + inputItemCount
                + ",\"parsed_token_count\":" + parsedTokenCount
                + ",\"invalid_token_count\":" + invalidTokenCount
                + ",\"input_truncated\":" + JsonUtil.Bool(inputTruncated)
                + ",\"requested_count\":" + requestedCodepoints.Count
                + ",\"requested_range_count\":" + requestedRangeCount
                + ",\"requested_ranges_truncated\":" + JsonUtil.Bool(requestedRangesTruncated)
                + ",\"requested_ranges\":[" + requestedRangesJson + "]"
                + ",\"covered_count\":" + (requestedCodepoints.Count - missing.Count)
                + ",\"missing_count\":" + missing.Count
                + ",\"missing_codepoints_truncated\":" + JsonUtil.Bool(missingCodepointsTruncated)
                + ",\"missing_codepoints\":[" + missingCodepointsJson + "]"
                + ",\"missing_range_count\":" + missingRangeCount
                + ",\"missing_ranges_truncated\":" + JsonUtil.Bool(missingRangesTruncated)
                + ",\"missing_ranges\":[" + missingRangesJson + "]"
                + ",\"primary_character_table_count\":" + primaryCharacterTableCount
                + ",\"primary_scanned_items\":" + primaryScannedItems
                + ",\"primary_scan_truncated\":" + JsonUtil.Bool(primaryScanTruncated)
                + ",\"include_fallbacks\":" + JsonUtil.Bool(includeFallbacks)
                + ",\"max_fallback_assets\":" + maxFallbackAssets
                + ",\"fallback_font_asset_count\":" + fallbackFontAssetCount
                + ",\"fallback_assets_considered\":" + fallbackAssetsConsidered
                + ",\"fallbacks_truncated\":" + JsonUtil.Bool(fallbacksTruncated)
                + ",\"fallback_assets\":[" + fallbackAssetsJson + "]"
                + ",\"max_font_scan_items\":" + maxFontScanItems
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"max_ranges\":" + maxRanges
                + ",\"next_safe_write_command\":\"tmp.font_asset.static.create\""
                + ",\"warnings\":[\"This is a dry-run plan only; it does not generate atlases, create font assets, bake, import, refresh, write, move, overwrite, or fix assets.\",\"Missing codepoints are compared against current serialized TMP character tables and direct fallbacks; source font glyph availability is not rendered or baked here.\",\"Later write commands must use explicit target paths, dry-run/precondition checks, and policy approval.\"]}");
        }

        [McpCommand(
            "tmp.font_asset.report",
            Title = "TMP font asset report",
            Description = "Create a bounded read-only Markdown/JSON report for TMP font assets, Unicode coverage, and optional static generation readiness",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpFontAssetReportSchemaJson,
            Tags = new string[] { "tmp", "font", "unicode", "report", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "text_artifact",
            OutputLimitJson = TmpFontAssetReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ReportTmpFontAssets(McpCommandContext context)
        {
            var format = Bounded(JsonUtil.ExtractString(context.ArgsJson, "format"), 32).Trim();
            if (string.IsNullOrEmpty(format))
            {
                format = "markdown";
            }

            if (format != "markdown" && format != "json" && format != "markdown_and_json")
            {
                return CommandExecutionResult.Error("invalid_field_value", "format must be markdown, json, or markdown_and_json");
            }

            var rawAssetPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "asset_path"), 512);
            var singleAsset = !string.IsNullOrEmpty(rawAssetPath);
            var rawTargetAssetPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "target_asset_path"), 512);
            if (!string.IsNullOrEmpty(rawTargetAssetPath) && !singleAsset)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "target_asset_path requires asset_path in tmp.font_asset.report");
            }

            var includeFallbacks = JsonUtil.ExtractBool(context.ArgsJson, "include_fallbacks", true);
            var includeStaticPlan = JsonUtil.ExtractBool(context.ArgsJson, "include_static_plan", false);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 20), 1, 100);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", MaxScanAssets), 1, MaxScanAssets);
            var maxInputItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_input_items", MaxUnicodeInputItems), 1, MaxUnicodeInputItems);
            var maxFontScanItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_font_scan_items", MaxUnicodeScanItems), 1, MaxUnicodeScanItems);
            var maxFallbackAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_fallback_assets", 20), 0, MaxUnicodeFallbackAssets);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxUnicodeCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxUnicodeRanges);
            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 65536), 1024, MaxReportChars);
            var unicodeText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "unicode_text"), MaxUnicodeStringChars);
            var codepointsText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "codepoints_text"), MaxUnicodeInputChars);
            var rangesText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "ranges_text"), MaxUnicodeInputChars);
            var hasUnicodeInput = !string.IsNullOrEmpty(unicodeText) || !string.IsNullOrEmpty(codepointsText) || !string.IsNullOrEmpty(rangesText);
            if (includeStaticPlan && !hasUnicodeInput)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "include_static_plan requires unicode_text, codepoints_text, or ranges_text");
            }

            var requestedSet = new HashSet<int>();
            var inputItemCount = 0;
            var parsedTokenCount = 0;
            var invalidTokenCount = 0;
            var inputTruncated = false;
            if (hasUnicodeInput)
            {
                AddCollectibleCodepoints(unicodeText, requestedSet, maxInputItems, ref inputItemCount, ref inputTruncated);
                AddUnicodeCodepointTokens(codepointsText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
                AddUnicodeRangeTokens(rangesText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
                if (requestedSet.Count == 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Requested Unicode set did not contain collectible codepoints");
                }
            }

            var requestedCodepoints = new List<int>(requestedSet);
            requestedCodepoints.Sort();
            var includeMarkdown = format != "json";
            var includeJsonDetails = format != "markdown";
            var pathPrefix = string.Empty;
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var candidatePaths = new List<string>();
            UnityEngine.Object directAsset = null;

            if (singleAsset)
            {
                var assetResult = RequireTmpFontAssetPath(context.ArgsJson, out var assetPath, out directAsset);
                if (assetResult != null)
                {
                    return assetResult;
                }

                candidatePaths.Add(assetPath);
                pathPrefix = ParentAssetPath(assetPath) + "/";
                includePackages = assetPath.StartsWith("Packages/", StringComparison.Ordinal);
            }
            else
            {
                var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out pathPrefix);
                if (pathPrefixResult != null)
                {
                    return pathPrefixResult;
                }

                if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    includePackages = true;
                }

                candidatePaths = CandidateAssetPaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix);
            }

            var entries = new List<TmpFontAssetReportEntry>();
            var entriesJson = new StringBuilder();
            var scannedAssets = 0;
            var matchedFontAssets = 0;
            var scanTruncated = false;
            var resultsTruncated = false;

            for (var pathIndex = 0; pathIndex < candidatePaths.Count; pathIndex++)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                var assetPath = candidatePaths[pathIndex];
                var asset = singleAsset ? directAsset : AssetDatabase.LoadMainAssetAtPath(assetPath);
                if (!IsTmpFontAsset(asset) || !TmpFontAssetMatches(assetPath, asset, query))
                {
                    continue;
                }

                matchedFontAssets++;
                if (entries.Count >= maxResults)
                {
                    resultsTruncated = true;
                    break;
                }

                var entry = BuildTmpFontAssetReportEntry(
                    context.ArgsJson,
                    assetPath,
                    asset,
                    includeFallbacks,
                    hasUnicodeInput,
                    includeStaticPlan,
                    requestedCodepoints,
                    maxFontScanItems,
                    maxFallbackAssets,
                    maxCodepoints,
                    maxRanges,
                    out var entryError);
                if (entryError != null)
                {
                    return entryError;
                }

                entries.Add(entry);
                if (includeJsonDetails)
                {
                    if (entriesJson.Length > 0)
                    {
                        entriesJson.Append(',');
                    }

                    entriesJson.Append(TmpFontAssetReportEntryJson(entry));
                }
            }

            var reportTruncated = false;
            var fullReport = includeMarkdown
                ? BuildTmpFontAssetReportMarkdown(pathPrefix, query, singleAsset, includePackages, hasUnicodeInput, includeStaticPlan, requestedCodepoints.Count, entries, maxChars, out reportTruncated)
                : string.Empty;
            var report = includeMarkdown ? Bounded(fullReport, maxChars) : string.Empty;
            var reportResourceUri = includeMarkdown ? TmpResourceUri("reports", NewTmpResourceId("tmp_font_report"), "markdown") : string.Empty;
            if (includeMarkdown)
            {
                StoreResource(reportResourceUri, fullReport, "text/markdown");
            }

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"format\":" + JsonUtil.String(format)
                + ",\"content_type\":" + JsonUtil.String(includeMarkdown ? "text/markdown" : "application/json")
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_fallbacks\":" + JsonUtil.Bool(includeFallbacks)
                + ",\"include_static_plan\":" + JsonUtil.Bool(includeStaticPlan)
                + ",\"unicode_text_chars\":" + unicodeText.Length
                + ",\"codepoints_text_chars\":" + codepointsText.Length
                + ",\"ranges_text_chars\":" + rangesText.Length
                + ",\"max_input_items\":" + maxInputItems
                + ",\"input_item_count\":" + inputItemCount
                + ",\"parsed_token_count\":" + parsedTokenCount
                + ",\"invalid_token_count\":" + invalidTokenCount
                + ",\"input_truncated\":" + JsonUtil.Bool(inputTruncated)
                + ",\"requested_count\":" + requestedCodepoints.Count
                + ",\"candidate_assets\":" + candidatePaths.Count
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"matched_font_assets\":" + matchedFontAssets
                + ",\"returned_results\":" + entries.Count
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"max_font_scan_items\":" + maxFontScanItems
                + ",\"max_fallback_assets\":" + maxFallbackAssets
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"max_ranges\":" + maxRanges
                + ",\"max_chars\":" + maxChars
                + ",\"report_truncated\":" + JsonUtil.Bool(reportTruncated)
                + ",\"report\":" + (includeMarkdown ? JsonUtil.String(report) : "null")
                + ",\"font_assets\":[" + entriesJson + "]"
                + ",\"resource_refs\":" + (includeMarkdown ? ResourceRefsJson(reportResourceUri) : "[]")
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":[\"This report is generated from AssetDatabase and SerializedObject reads only; it does not generate atlases, import, refresh, write, move, overwrite, or fix assets.\",\"Coverage and static readiness use serialized TMP character tables and direct fallbacks only; source font glyph availability is not rendered or baked here.\"]}");
        }

        [McpCommand(
            "tmp.font_asset.validate",
            Title = "Validate TMP font asset",
            Description = "Validate one TMP font asset serialized state, static readiness, references, and optional Unicode coverage without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TmpFontAssetValidateSchemaJson,
            Tags = new string[] { "tmp", "font", "unicode", "validate", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TmpFontAssetValidateOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ValidateTmpFontAsset(McpCommandContext context)
        {
            var assetResult = RequireTmpFontAssetPath(context.ArgsJson, out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return CommandExecutionResult.Error("invalid_field_value", "TMP font asset could not be inspected with SerializedObject");
            }

            var includeFallbacks = JsonUtil.ExtractBool(context.ArgsJson, "include_fallbacks", true);
            var requireStatic = JsonUtil.ExtractBool(context.ArgsJson, "require_static", false);
            var requireSourceFont = JsonUtil.ExtractBool(context.ArgsJson, "require_source_font", false);
            var requireMaterial = JsonUtil.ExtractBool(context.ArgsJson, "require_material", true);
            var requireAtlasTexture = JsonUtil.ExtractBool(context.ArgsJson, "require_atlas_texture", true);
            var maxInputItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_input_items", MaxUnicodeInputItems), 1, MaxUnicodeInputItems);
            var maxFontScanItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_font_scan_items", MaxUnicodeScanItems), 1, MaxUnicodeScanItems);
            var maxFallbackAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_fallback_assets", 20), 0, MaxUnicodeFallbackAssets);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxUnicodeCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxUnicodeRanges);
            var maxFindings = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_findings", 100), 1, MaxValidationFindings);
            var unicodeText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "unicode_text"), MaxUnicodeStringChars);
            var codepointsText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "codepoints_text"), MaxUnicodeInputChars);
            var rangesText = Bounded(JsonUtil.ExtractString(context.ArgsJson, "ranges_text"), MaxUnicodeInputChars);
            var hasUnicodeInput = !string.IsNullOrEmpty(unicodeText) || !string.IsNullOrEmpty(codepointsText) || !string.IsNullOrEmpty(rangesText);
            var requestedSet = new HashSet<int>();
            var inputItemCount = 0;
            var parsedTokenCount = 0;
            var invalidTokenCount = 0;
            var inputTruncated = false;

            if (hasUnicodeInput)
            {
                AddCollectibleCodepoints(unicodeText, requestedSet, maxInputItems, ref inputItemCount, ref inputTruncated);
                AddUnicodeCodepointTokens(codepointsText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
                AddUnicodeRangeTokens(rangesText, requestedSet, maxInputItems, ref inputItemCount, ref parsedTokenCount, ref invalidTokenCount, ref inputTruncated);
                if (requestedSet.Count == 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "Requested Unicode set did not contain collectible codepoints");
                }
            }

            var requestedCodepoints = new List<int>(requestedSet);
            requestedCodepoints.Sort();
            var findings = new StringBuilder();
            var findingCount = 0;
            var returnedFindings = 0;
            var errorCount = 0;
            var warningCount = 0;
            AtlasPopulationModeFlags(serializedObject, out var atlasPopulationMode, out var dynamicMode, out var staticMode);
            var sourceFontAvailable = SourceFontAvailable(serializedObject);
            var materialAvailable = ObjectReferenceAvailable(FindFirstProperty(serializedObject, "m_Material", "material"));
            var atlasTextureCount = ObjectReferenceArrayCount(FindFirstProperty(serializedObject, "m_AtlasTextures", "atlasTextures"), out var atlasTextureReferenceCount, out var atlasTextureNullCount);
            var fallbackCount = ObjectReferenceArrayCount(FindFirstProperty(serializedObject, "m_FallbackFontAssetTable", "fallbackFontAssetTable"), out var fallbackReferenceCount, out var fallbackNullCount);
            var characterCount = ArraySize(serializedObject, "m_CharacterTable", "characterTable");
            var glyphCount = ArraySize(serializedObject, "m_GlyphTable", "glyphTable");

            if (requireStatic && !staticMode)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "error", "TMP001", "TMP font asset is not in Static atlas population mode", "atlas_population_mode");
            }
            else if (dynamicMode)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "warning", "TMP002", "TMP font asset is dynamic; use tmp.font_asset.static.plan before generating a static copy", "atlas_population_mode");
            }

            if (requireSourceFont && !sourceFontAvailable)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "error", "TMP003", "Source font reference or GUID is missing", "source_font");
            }
            else if (!sourceFontAvailable)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "warning", "TMP004", "Source font reference or GUID is missing; future static regeneration may be blocked", "source_font");
            }

            if (requireMaterial && !materialAvailable)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "error", "TMP005", "Material reference is missing", "material");
            }

            if (requireAtlasTexture && atlasTextureReferenceCount == 0)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "error", "TMP006", "No atlas texture references are present", "atlas_textures");
            }

            if (atlasTextureNullCount > 0)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "warning", "TMP007", "Atlas texture table contains null entries", "atlas_textures");
            }

            if (fallbackNullCount > 0)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "warning", "TMP008", "Fallback font asset table contains null entries", "fallback_font_assets");
            }

            if (characterCount == 0)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "warning", "TMP009", "Character table is empty", "character_table");
            }

            if (glyphCount == 0)
            {
                AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "warning", "TMP010", "Glyph table is empty", "glyph_table");
            }

            var coverageJson = "null";
            var coverageEntry = new TmpFontAssetReportEntry();
            if (hasUnicodeInput)
            {
                coverageJson = TmpFontAssetReportCoverageJson(coverageEntry, serializedObject, includeFallbacks, requestedCodepoints, maxFontScanItems, maxFallbackAssets, maxCodepoints, maxRanges);
                if (coverageEntry.MissingCount > 0)
                {
                    AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "error", "TMP011", "Requested Unicode set has missing codepoints in the TMP font asset and selected direct fallbacks", "coverage");
                }

                if (coverageEntry.PrimaryScanTruncated || coverageEntry.FallbacksTruncated)
                {
                    AddValidationFinding(findings, maxFindings, ref findingCount, ref returnedFindings, ref errorCount, ref warningCount, "warning", "TMP012", "Coverage scan was truncated by max_font_scan_items or max_fallback_assets", "coverage");
                }
            }

            var findingsTruncated = findingCount > returnedFindings;
            var validationId = NewTmpResourceId("tmp_font_validation");
            var validationResourceUri = TmpResourceUri("validations", validationId, "json");
            var response = "{\"status\":\"ok\""
                + ",\"validation_id\":" + JsonUtil.String(validationId)
                + ",\"valid\":" + JsonUtil.Bool(errorCount == 0)
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"name\":" + JsonUtil.String(asset.name)
                + ",\"atlas_population_mode\":" + JsonUtil.String(atlasPopulationMode)
                + ",\"atlas_population_mode_static\":" + JsonUtil.Bool(staticMode)
                + ",\"atlas_population_mode_dynamic\":" + JsonUtil.Bool(dynamicMode)
                + ",\"source_font_available\":" + JsonUtil.Bool(sourceFontAvailable)
                + ",\"source_font\":" + SourceFontJson(serializedObject)
                + ",\"material_available\":" + JsonUtil.Bool(materialAvailable)
                + ",\"material\":" + ObjectReferencePropertyJson(FindFirstProperty(serializedObject, "m_Material", "material"))
                + ",\"atlas_texture_count\":" + atlasTextureCount
                + ",\"atlas_texture_reference_count\":" + atlasTextureReferenceCount
                + ",\"atlas_texture_null_count\":" + atlasTextureNullCount
                + ",\"fallback_font_asset_count\":" + fallbackCount
                + ",\"fallback_font_asset_reference_count\":" + fallbackReferenceCount
                + ",\"fallback_font_asset_null_count\":" + fallbackNullCount
                + ",\"character_count\":" + characterCount
                + ",\"glyph_count\":" + glyphCount
                + ",\"unicode_text_chars\":" + unicodeText.Length
                + ",\"codepoints_text_chars\":" + codepointsText.Length
                + ",\"ranges_text_chars\":" + rangesText.Length
                + ",\"max_input_items\":" + maxInputItems
                + ",\"input_item_count\":" + inputItemCount
                + ",\"parsed_token_count\":" + parsedTokenCount
                + ",\"invalid_token_count\":" + invalidTokenCount
                + ",\"input_truncated\":" + JsonUtil.Bool(inputTruncated)
                + ",\"requested_count\":" + requestedCodepoints.Count
                + ",\"include_fallbacks\":" + JsonUtil.Bool(includeFallbacks)
                + ",\"max_font_scan_items\":" + maxFontScanItems
                + ",\"max_fallback_assets\":" + maxFallbackAssets
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"max_ranges\":" + maxRanges
                + ",\"coverage\":" + coverageJson
                + ",\"finding_count\":" + findingCount
                + ",\"returned_findings\":" + returnedFindings
                + ",\"findings_truncated\":" + JsonUtil.Bool(findingsTruncated)
                + ",\"error_count\":" + errorCount
                + ",\"warning_count\":" + warningCount
                + ",\"findings\":[" + findings + "]"
                + ",\"resource_refs\":" + ResourceRefsJson(validationResourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":[\"Validation is read-only and uses serialized TMP fields plus direct fallback TMP font assets only.\",\"This command does not generate atlases, create font assets, import, refresh, write, move, swap references, or change fallbacks.\"]}";
            StoreResource(validationResourceUri, response, "application/json");
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "tmp.font_asset.resource.read",
            Title = "Read TMP font asset resource",
            Description = "Read a bounded process-local TMP font asset resource emitted by previous TMP report or validation commands",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TmpFontAssetResourceReadSchemaJson,
            Tags = new string[] { "tmp", "font", "resource", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "text_artifact",
            OutputLimitJson = TmpFontAssetResourceReadOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ReadTmpFontAssetResource(McpCommandContext context)
        {
            var uri = JsonUtil.ExtractString(context.ArgsJson, "uri");
            var format = JsonUtil.ExtractString(context.ArgsJson, "format");
            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 200000), 1, MaxReportChars);
            if (!IsTmpResourceUri(uri))
            {
                return CommandExecutionResult.Ok("{\"status\":\"invalid\""
                    + ",\"uri\":" + JsonUtil.String(uri)
                    + ",\"reason\":\"uri must be a unity-ui://tmp/... resource emitted by this Unity Bridge process\""
                    + ",\"read_only\":true}");
            }

            var resource = FindResource(uri);
            if (resource == null)
            {
                return CommandExecutionResult.Ok("{\"status\":\"not_found\""
                    + ",\"uri\":" + JsonUtil.String(uri)
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

        static TmpFontAssetReportEntry BuildTmpFontAssetReportEntry(
            string argsJson,
            string assetPath,
            UnityEngine.Object asset,
            bool includeFallbacks,
            bool includeCoverage,
            bool includeStaticPlan,
            List<int> requestedCodepoints,
            int maxFontScanItems,
            int maxFallbackAssets,
            int maxCodepoints,
            int maxRanges,
            out CommandExecutionResult error)
        {
            error = null;
            var entry = new TmpFontAssetReportEntry();
            entry.AssetPath = assetPath;
            entry.Guid = AssetDatabase.AssetPathToGUID(assetPath);
            entry.Name = asset == null ? Path.GetFileNameWithoutExtension(assetPath) : asset.name;
            entry.InventoryJson = TmpFontAssetJson(assetPath, asset, includeFallbacks, false, false, MaxReferenceItems);
            entry.CoverageJson = "null";
            entry.StaticPlanJson = "null";

            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return entry;
            }

            entry.SerializedAvailable = true;
            entry.AtlasPopulationMode = AtlasPopulationModeLabel(serializedObject);
            entry.CharacterCount = ArraySize(serializedObject, "m_CharacterTable", "characterTable");
            entry.GlyphCount = ArraySize(serializedObject, "m_GlyphTable", "glyphTable");
            entry.FallbackCount = ArraySize(serializedObject, "m_FallbackFontAssetTable", "fallbackFontAssetTable");
            entry.SourceFontAvailable = SourceFontAvailable(serializedObject);
            entry.SourceFont = SourceFontLabel(serializedObject);

            if (includeCoverage)
            {
                entry.CoverageJson = TmpFontAssetReportCoverageJson(entry, serializedObject, includeFallbacks, requestedCodepoints, maxFontScanItems, maxFallbackAssets, maxCodepoints, maxRanges);
            }

            if (includeStaticPlan)
            {
                var targetResult = ResolveStaticPlanTargetPath(argsJson, assetPath, entry.Name, out var targetAssetPath);
                if (targetResult != null)
                {
                    error = targetResult;
                    return null;
                }

                entry.StaticPlanJson = TmpFontAssetReportStaticPlanJson(entry, serializedObject, argsJson, requestedCodepoints.Count, targetAssetPath);
            }

            return entry;
        }

        static string TmpFontAssetReportCoverageJson(TmpFontAssetReportEntry entry, SerializedObject serializedObject, bool includeFallbacks, List<int> requestedCodepoints, int maxFontScanItems, int maxFallbackAssets, int maxCodepoints, int maxRanges)
        {
            var coveredSet = new HashSet<int>();
            var primaryCodepoints = ExtractCharacterCodepoints(serializedObject, maxFontScanItems, out var primaryCharacterTableCount, out var primaryScannedItems, out var primaryScanTruncated);
            for (var index = 0; index < primaryCodepoints.Count; index++)
            {
                coveredSet.Add(primaryCodepoints[index]);
            }

            var fallbackFontAssetCount = 0;
            var fallbackAssetsConsidered = 0;
            var fallbacksTruncated = false;
            var fallbackAssetsJson = includeFallbacks ? FallbackCoverageAssetsJson(serializedObject, maxFallbackAssets, maxFontScanItems, coveredSet, out fallbackFontAssetCount, out fallbackAssetsConsidered, out fallbacksTruncated) : string.Empty;
            var missing = new List<int>();
            var coveredCount = 0;
            for (var index = 0; index < requestedCodepoints.Count; index++)
            {
                if (coveredSet.Contains(requestedCodepoints[index]))
                {
                    coveredCount++;
                }
                else
                {
                    missing.Add(requestedCodepoints[index]);
                }
            }

            var coveragePercent = requestedCodepoints.Count == 0 ? 100f : (float)coveredCount * 100f / requestedCodepoints.Count;
            entry.RequestedCount = requestedCodepoints.Count;
            entry.CoveredCount = coveredCount;
            entry.MissingCount = missing.Count;
            entry.CoveragePercent = coveragePercent;
            entry.PrimaryScanTruncated = primaryScanTruncated;
            entry.FallbacksTruncated = fallbacksTruncated;
            var missingCodepointsJson = CodepointLabelsJson(missing, maxCodepoints, out var missingCodepointsTruncated);
            var missingRangesJson = UnicodeRangesJson(missing, maxRanges, out var missingRangeCount, out var missingRangesTruncated);

            return "{\"requested_count\":" + requestedCodepoints.Count
                + ",\"covered_count\":" + coveredCount
                + ",\"missing_count\":" + missing.Count
                + ",\"coverage_percent\":" + FloatJson(coveragePercent)
                + ",\"primary_character_table_count\":" + primaryCharacterTableCount
                + ",\"primary_scanned_items\":" + primaryScannedItems
                + ",\"primary_scan_truncated\":" + JsonUtil.Bool(primaryScanTruncated)
                + ",\"include_fallbacks\":" + JsonUtil.Bool(includeFallbacks)
                + ",\"fallback_font_asset_count\":" + fallbackFontAssetCount
                + ",\"fallback_assets_considered\":" + fallbackAssetsConsidered
                + ",\"fallbacks_truncated\":" + JsonUtil.Bool(fallbacksTruncated)
                + ",\"fallback_assets\":[" + fallbackAssetsJson + "]"
                + ",\"missing_codepoints_truncated\":" + JsonUtil.Bool(missingCodepointsTruncated)
                + ",\"missing_codepoints\":[" + missingCodepointsJson + "]"
                + ",\"missing_range_count\":" + missingRangeCount
                + ",\"missing_ranges_truncated\":" + JsonUtil.Bool(missingRangesTruncated)
                + ",\"missing_ranges\":[" + missingRangesJson + "]}";
        }

        static string TmpFontAssetReportStaticPlanJson(TmpFontAssetReportEntry entry, SerializedObject serializedObject, string argsJson, int requestedCount, string targetAssetPath)
        {
            var targetParentPath = ParentAssetPath(targetAssetPath);
            var targetExists = !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(targetAssetPath));
            var targetParentExists = !string.IsNullOrEmpty(targetParentPath) && AssetDatabase.IsValidFolder(targetParentPath);
            var atlasWidth = Clamp(JsonUtil.ExtractInt(argsJson, "atlas_width", IntPropertyValue(serializedObject, 1024, "m_AtlasWidth", "atlasWidth")), 16, 8192);
            var atlasHeight = Clamp(JsonUtil.ExtractInt(argsJson, "atlas_height", IntPropertyValue(serializedObject, 1024, "m_AtlasHeight", "atlasHeight")), 16, 8192);
            var atlasPadding = Clamp(JsonUtil.ExtractInt(argsJson, "atlas_padding", IntPropertyValue(serializedObject, 5, "m_AtlasPadding", "atlasPadding")), 0, 128);
            var ready = entry.SourceFontAvailable && targetParentExists && !targetExists && requestedCount > 0;
            entry.TargetAssetPath = targetAssetPath;
            entry.TargetParentPath = targetParentPath;
            entry.TargetParentExists = targetParentExists;
            entry.TargetExists = targetExists;
            entry.ReadyForLaterStaticCreate = ready;

            return "{\"dry_run\":true"
                + ",\"target_asset_path\":" + JsonUtil.String(targetAssetPath)
                + ",\"target_parent_path\":" + JsonUtil.String(targetParentPath)
                + ",\"target_parent_exists\":" + JsonUtil.Bool(targetParentExists)
                + ",\"target_exists\":" + JsonUtil.Bool(targetExists)
                + ",\"would_overwrite\":" + JsonUtil.Bool(targetExists)
                + ",\"ready_for_later_static_create\":" + JsonUtil.Bool(ready)
                + ",\"source_font_available\":" + JsonUtil.Bool(entry.SourceFontAvailable)
                + ",\"source_atlas_population_mode\":" + AtlasPopulationModeJson(serializedObject)
                + ",\"planned_atlas_population_mode\":\"Static\""
                + ",\"planned_atlas_settings\":{\"width\":" + atlasWidth + ",\"height\":" + atlasHeight + ",\"padding\":" + atlasPadding + "}"
                + ",\"next_safe_write_command\":\"tmp.font_asset.static.create\"}";
        }

        static string TmpFontAssetReportEntryJson(TmpFontAssetReportEntry entry)
        {
            return "{\"asset_path\":" + JsonUtil.String(entry.AssetPath)
                + ",\"guid\":" + JsonUtil.String(entry.Guid)
                + ",\"name\":" + JsonUtil.String(entry.Name)
                + ",\"serialized_available\":" + JsonUtil.Bool(entry.SerializedAvailable)
                + ",\"atlas_population_mode\":" + JsonUtil.String(entry.AtlasPopulationMode)
                + ",\"source_font_available\":" + JsonUtil.Bool(entry.SourceFontAvailable)
                + ",\"source_font\":" + JsonUtil.String(entry.SourceFont)
                + ",\"glyph_count\":" + entry.GlyphCount
                + ",\"character_count\":" + entry.CharacterCount
                + ",\"fallback_font_asset_count\":" + entry.FallbackCount
                + ",\"inventory\":" + entry.InventoryJson
                + ",\"coverage\":" + entry.CoverageJson
                + ",\"static_plan\":" + entry.StaticPlanJson + "}";
        }

        static string BuildTmpFontAssetReportMarkdown(string pathPrefix, string query, bool singleAsset, bool includePackages, bool includeCoverage, bool includeStaticPlan, int requestedCount, List<TmpFontAssetReportEntry> entries, int maxChars, out bool truncated)
        {
            var builder = new StringBuilder();
            builder.AppendLine("# TMP font asset report");
            builder.AppendLine();
            builder.Append("- Scope: ").AppendLine(singleAsset ? "single TMP font asset" : "TMP font assets under `" + pathPrefix + "`");
            builder.Append("- Query: ").AppendLine(string.IsNullOrEmpty(query) ? "none" : "`" + query + "`");
            builder.Append("- Include packages: ").AppendLine(includePackages ? "yes" : "no");
            builder.Append("- Returned assets: ").Append(entries.Count).AppendLine();
            builder.Append("- Unicode coverage: ").AppendLine(includeCoverage ? requestedCount.ToString(CultureInfo.InvariantCulture) + " requested codepoints" : "not requested");
            builder.Append("- Static generation readiness: ").AppendLine(includeStaticPlan ? "included as dry-run preflight" : "not requested");
            builder.AppendLine();
            builder.AppendLine("| Asset | Mode | Chars | Glyphs | Fallbacks | Source font | Coverage | Static plan |");
            builder.AppendLine("| --- | --- | ---: | ---: | ---: | --- | --- | --- |");
            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                var coverage = includeCoverage
                    ? entry.CoveredCount.ToString(CultureInfo.InvariantCulture) + "/" + entry.RequestedCount.ToString(CultureInfo.InvariantCulture) + " (" + FloatJson(entry.CoveragePercent) + "%), missing " + entry.MissingCount.ToString(CultureInfo.InvariantCulture)
                    : "n/a";
                var staticPlan = includeStaticPlan
                    ? (entry.ReadyForLaterStaticCreate ? "ready" : "blocked") + (string.IsNullOrEmpty(entry.TargetAssetPath) ? string.Empty : " -> `" + entry.TargetAssetPath + "`")
                    : "n/a";
                builder.Append("| `").Append(MarkdownCell(entry.AssetPath)).Append("` | ")
                    .Append(MarkdownCell(entry.AtlasPopulationMode)).Append(" | ")
                    .Append(entry.CharacterCount).Append(" | ")
                    .Append(entry.GlyphCount).Append(" | ")
                    .Append(entry.FallbackCount).Append(" | ")
                    .Append(entry.SourceFontAvailable ? MarkdownCell(entry.SourceFont) : "missing").Append(" | ")
                    .Append(MarkdownCell(coverage)).Append(" | ")
                    .Append(MarkdownCell(staticPlan)).AppendLine(" |");
            }

            builder.AppendLine();
            builder.AppendLine("## Notes");
            builder.AppendLine("- Report data is read from AssetDatabase and SerializedObject only.");
            builder.AppendLine("- No atlases are generated and no assets are imported, refreshed, written, moved, overwritten, or fixed.");
            builder.AppendLine("- Coverage and static readiness use serialized TMP character tables and direct fallback TMP font assets only.");
            var report = builder.ToString();
            truncated = report.Length > maxChars;
            return report;
        }

        static void AddValidationFinding(StringBuilder builder, int maxFindings, ref int findingCount, ref int returnedFindings, ref int errorCount, ref int warningCount, string severity, string code, string message, string field)
        {
            findingCount++;
            if (string.Equals(severity, "error", StringComparison.OrdinalIgnoreCase))
            {
                errorCount++;
            }
            else if (string.Equals(severity, "warning", StringComparison.OrdinalIgnoreCase))
            {
                warningCount++;
            }

            if (returnedFindings >= maxFindings)
            {
                return;
            }

            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            builder.Append("{\"severity\":").Append(JsonUtil.String(severity))
                .Append(",\"code\":").Append(JsonUtil.String(code))
                .Append(",\"field\":").Append(JsonUtil.String(field))
                .Append(",\"message\":").Append(JsonUtil.String(message))
                .Append('}');
            returnedFindings++;
        }

        static bool ObjectReferenceAvailable(SerializedProperty property)
        {
            return property != null && property.propertyType == SerializedPropertyType.ObjectReference && property.objectReferenceValue != null;
        }

        static int ObjectReferenceArrayCount(SerializedProperty arrayProperty, out int referenceCount, out int nullCount)
        {
            referenceCount = 0;
            nullCount = 0;
            if (arrayProperty == null || !arrayProperty.isArray)
            {
                return 0;
            }

            for (var index = 0; index < arrayProperty.arraySize; index++)
            {
                var item = arrayProperty.GetArrayElementAtIndex(index);
                if (item == null || item.propertyType != SerializedPropertyType.ObjectReference || item.objectReferenceValue == null)
                {
                    nullCount++;
                    continue;
                }

                referenceCount++;
            }

            return arrayProperty.arraySize;
        }

        static void AtlasPopulationModeFlags(SerializedObject serializedObject, out string name, out bool dynamicMode, out bool staticMode)
        {
            var property = FindFirstProperty(serializedObject, "m_AtlasPopulationMode", "atlasPopulationMode");
            if (property == null)
            {
                name = string.Empty;
                dynamicMode = false;
                staticMode = false;
                return;
            }

            var value = SerializedInt(property, -1);
            name = SerializedEnumName(property);
            dynamicMode = name.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0 || (string.IsNullOrEmpty(name) && value == 1);
            staticMode = name.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0 || (string.IsNullOrEmpty(name) && value == 0);
            if (string.IsNullOrEmpty(name))
            {
                name = value.ToString(CultureInfo.InvariantCulture);
            }
        }

        static string ResourceRefsJson(string uri)
        {
            return "[" + JsonUtil.String(uri) + "]";
        }

        static string ResourceWarningsJson(bool truncated)
        {
            return truncated
                ? "[\"resource content was truncated by max_chars; rerun tmp.font_asset.resource.read with a higher max_chars within schema limits\"]"
                : "[]";
        }

        static string TmpResourceUri(string category, string id, string format)
        {
            return "unity-ui://tmp/font-assets/" + category + "/" + id + "/" + format;
        }

        static string NewTmpResourceId(string prefix)
        {
            return prefix + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static void StoreResource(string uri, string content, string mimeType)
        {
            if (!IsTmpResourceUri(uri))
            {
                return;
            }

            lock (ResourceCacheLock)
            {
                if (!ResourceCache.ContainsKey(uri))
                {
                    ResourceCacheOrder.Enqueue(uri);
                }

                ResourceCache[uri] = new TmpResourceSnapshot(uri, content, mimeType);
                while (ResourceCacheOrder.Count > MaxCachedResources)
                {
                    var evicted = ResourceCacheOrder.Dequeue();
                    ResourceCache.Remove(evicted);
                }
            }
        }

        static TmpResourceSnapshot FindResource(string uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return null;
            }

            lock (ResourceCacheLock)
            {
                return ResourceCache.TryGetValue(uri, out var resource) ? resource : null;
            }
        }

        static bool IsTmpResourceUri(string uri)
        {
            return !string.IsNullOrEmpty(uri)
                && uri.StartsWith("unity-ui://tmp/font-assets/", StringComparison.Ordinal)
                && uri.IndexOf("..", StringComparison.Ordinal) < 0
                && uri.IndexOf('\\') < 0;
        }

        static string AtlasPopulationModeLabel(SerializedObject serializedObject)
        {
            var property = FindFirstProperty(serializedObject, "m_AtlasPopulationMode", "atlasPopulationMode");
            if (property == null)
            {
                return string.Empty;
            }

            var name = SerializedEnumName(property);
            return string.IsNullOrEmpty(name) ? SerializedInt(property, -1).ToString(CultureInfo.InvariantCulture) : name;
        }

        static string SourceFontLabel(SerializedObject serializedObject)
        {
            var sourceFont = FindFirstProperty(serializedObject, "m_SourceFontFile_EditorRef", "m_SourceFontFile", "sourceFontFile");
            if (sourceFont != null && sourceFont.propertyType == SerializedPropertyType.ObjectReference && sourceFont.objectReferenceValue != null)
            {
                return sourceFont.objectReferenceValue.name;
            }

            var guid = StringPropertyValue(serializedObject, "m_SourceFontFileGUID", "sourceFontFileGUID");
            var path = string.IsNullOrEmpty(guid) ? string.Empty : AssetDatabase.GUIDToAssetPath(guid);
            return string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileNameWithoutExtension(path);
        }

        static string MarkdownCell(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
        }

        sealed class TmpFontAssetReportEntry
        {
            public string AssetPath = string.Empty;
            public string Guid = string.Empty;
            public string Name = string.Empty;
            public string AtlasPopulationMode = string.Empty;
            public string SourceFont = string.Empty;
            public string TargetAssetPath = string.Empty;
            public string TargetParentPath = string.Empty;
            public string InventoryJson = "{}";
            public string CoverageJson = "null";
            public string StaticPlanJson = "null";
            public bool SerializedAvailable;
            public bool SourceFontAvailable;
            public bool TargetParentExists;
            public bool TargetExists;
            public bool ReadyForLaterStaticCreate;
            public bool PrimaryScanTruncated;
            public bool FallbacksTruncated;
            public int CharacterCount;
            public int GlyphCount;
            public int FallbackCount;
            public int RequestedCount;
            public int CoveredCount;
            public int MissingCount;
            public float CoveragePercent;
        }

        sealed class TmpResourceSnapshot
        {
            public TmpResourceSnapshot(string uri, string content, string mimeType)
            {
                Uri = uri;
                Content = content;
                MimeType = mimeType;
            }

            public string Uri { get; }
            public string Content { get; }
            public string MimeType { get; }
        }

        static CommandExecutionResult RequireTmpFontAssetPath(string argsJson, out string assetPath, out UnityEngine.Object asset)
        {
            assetPath = string.Empty;
            asset = null;
            var rawPath = JsonUtil.ExtractString(argsJson, "asset_path");
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument asset_path is required");
            }

            if (rawPath.Length > 512)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must be at most 512 characters");
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must be a Unity-relative path under Assets/ or Packages/");
            }

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                return CommandExecutionResult.Error("asset_not_found", "asset_path does not exist");
            }

            asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return IsTmpFontAsset(asset) ? null : CommandExecutionResult.Error("invalid_field_value", "asset_path must reference a TextMeshPro TMP_FontAsset asset");
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

        static CommandExecutionResult ResolveStaticPlanTargetPath(string argsJson, string sourceAssetPath, string assetName, out string targetAssetPath)
        {
            targetAssetPath = Bounded(JsonUtil.ExtractString(argsJson, "target_asset_path"), 512);
            if (string.IsNullOrEmpty(targetAssetPath))
            {
                targetAssetPath = DefaultStaticTargetPath(sourceAssetPath, assetName);
                return null;
            }

            try
            {
                targetAssetPath = PathGuard.RequireUnityAssetPath(targetAssetPath);
            }
            catch (ArgumentException)
            {
                targetAssetPath = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", "target_asset_path must be a Unity-relative asset path under Assets/");
            }

            if (!targetAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "target_asset_path must be under Assets/");
            }

            if (!targetAssetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "target_asset_path must end with .asset");
            }

            if (string.Equals(targetAssetPath, sourceAssetPath, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "target_asset_path must not overwrite the source TMP font asset");
            }

            return null;
        }

        static string DefaultStaticTargetPath(string sourceAssetPath, string assetName)
        {
            var fileName = SafeAssetFileName(string.IsNullOrEmpty(assetName) ? Path.GetFileNameWithoutExtension(sourceAssetPath) : assetName) + " Static SDF.asset";
            if (sourceAssetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return ParentAssetPath(sourceAssetPath) + "/" + fileName;
            }

            return "Assets/TMPStatic/" + fileName;
        }

        static string ParentAssetPath(string assetPath)
        {
            var slash = assetPath.LastIndexOf('/');
            return slash <= 0 ? string.Empty : assetPath.Substring(0, slash);
        }

        static string SafeAssetFileName(string name)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < name.Length; index++)
            {
                var current = name[index];
                builder.Append(char.IsLetterOrDigit(current) || current == ' ' || current == '-' || current == '_' ? current : '_');
            }

            var result = builder.ToString().Trim();
            return string.IsNullOrEmpty(result) ? "TMP Font" : result;
        }

        static string[] SearchRoots(string pathPrefix, bool includePackages)
        {
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return new[] { "Packages" };
            }

            return includePackages ? new[] { "Assets", "Packages" } : new[] { "Assets" };
        }

        static List<string> CandidateAssetPaths(string[] searchRoots, bool includePackages, string pathPrefix)
        {
            var guids = AssetDatabase.FindAssets("t:TMP_FontAsset", searchRoots);
            var paths = new List<string>(guids.Length);
            for (var index = 0; index < guids.Length; index++)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (string.IsNullOrEmpty(assetPath) || (!includePackages && assetPath.StartsWith("Packages/", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!assetPath.StartsWith(pathPrefix, StringComparison.Ordinal))
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

        static List<string> CandidateUnicodeSourcePaths(string[] searchRoots, bool includePackages, string pathPrefix, bool includeScenes, bool includePrefabs, bool includeTextAssets)
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

                if (!assetPath.StartsWith(pathPrefix, StringComparison.Ordinal) || !IsUnicodeSourceCandidate(assetPath, includeScenes, includePrefabs, includeTextAssets))
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

        static bool IsUnicodeSourceCandidate(string assetPath, bool includeScenes, bool includePrefabs, bool includeTextAssets)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".meta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (includeScenes && string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (includePrefabs && string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return includeTextAssets && (IsPlainTextUnicodeAssetExtension(extension) || string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase));
        }

        static bool IsPlainTextUnicodeAssetExtension(string extension)
        {
            return string.Equals(extension, ".txt", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".csv", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".tsv", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".json", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".po", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".strings", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase);
        }

        static string UnicodeSourceKind(string assetPath)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)) return "scene";
            if (string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)) return "prefab";
            if (string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase)) return "yaml_asset";
            return "text_asset";
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
                    return CommandExecutionResult.Error("invalid_field_value", "Asset content looks binary and cannot be scanned as text");
                }
            }

            try
            {
                content = DecodeStrictUtf8(bytes, bytes.Length);
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

        static string DecodeStrictUtf8(byte[] bytes, int byteCount)
        {
            return new UTF8Encoding(false, true).GetString(bytes, 0, byteCount);
        }

        static bool IsExpectedReadException(Exception exception)
        {
            return exception is IOException || exception is UnauthorizedAccessException || exception is ArgumentException || exception is NotSupportedException;
        }

        static bool TmpFontAssetMatches(string assetPath, UnityEngine.Object asset, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            return assetPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || AssetDatabase.AssetPathToGUID(assetPath).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || (asset != null && asset.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static string TmpFontAssetJson(string assetPath, UnityEngine.Object asset, bool includeFallbacks, bool includeAtlasTextures, bool includeTables, int maxItems)
        {
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            var type = asset == null ? null : asset.GetType();
            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return "{\"asset_path\":" + JsonUtil.String(assetPath)
                    + ",\"guid\":" + JsonUtil.String(guid)
                    + ",\"name\":" + JsonUtil.String(asset == null ? Path.GetFileNameWithoutExtension(assetPath) : asset.name)
                    + ",\"type\":" + JsonUtil.String(type == null ? string.Empty : type.FullName ?? type.Name)
                    + ",\"serialized_available\":false"
                    + ",\"warnings\":[\"TMP font asset could not be inspected with SerializedObject.\"]}";
            }

            var atlasTextures = ObjectReferenceArrayJson(FindFirstProperty(serializedObject, "m_AtlasTextures", "atlasTextures"), includeAtlasTextures, maxItems, out var atlasTextureCount, out var atlasTexturesTruncated);
            var fallbackFontAssets = ObjectReferenceArrayJson(FindFirstProperty(serializedObject, "m_FallbackFontAssetTable", "fallbackFontAssetTable"), includeFallbacks, maxItems, out var fallbackCount, out var fallbackTruncated);
            var glyphCount = ArraySize(serializedObject, "m_GlyphTable", "glyphTable");
            var characterCount = ArraySize(serializedObject, "m_CharacterTable", "characterTable");
            var charactersTruncated = false;
            var glyphsTruncated = false;
            var charactersJson = includeTables ? CharacterTableJson(serializedObject, maxItems, out charactersTruncated) : string.Empty;
            var glyphsJson = includeTables ? GlyphTableJson(serializedObject, maxItems, out glyphsTruncated) : string.Empty;

            return "{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"name\":" + JsonUtil.String(asset == null ? Path.GetFileNameWithoutExtension(assetPath) : asset.name)
                + ",\"type\":" + JsonUtil.String(type == null ? string.Empty : type.FullName ?? type.Name)
                + ",\"serialized_available\":true"
                + ",\"atlas_population_mode\":" + AtlasPopulationModeJson(serializedObject)
                + ",\"source_font\":" + SourceFontJson(serializedObject)
                + ",\"material\":" + ObjectReferencePropertyJson(FindFirstProperty(serializedObject, "m_Material", "material"))
                + ",\"atlas_settings\":" + AtlasSettingsJson(serializedObject)
                + ",\"atlas_texture_count\":" + atlasTextureCount
                + ",\"atlas_textures_truncated\":" + JsonUtil.Bool(atlasTexturesTruncated)
                + ",\"atlas_textures\":[" + atlasTextures + "]"
                + ",\"fallback_font_asset_count\":" + fallbackCount
                + ",\"fallback_font_assets_truncated\":" + JsonUtil.Bool(fallbackTruncated)
                + ",\"fallback_font_assets\":[" + fallbackFontAssets + "]"
                + ",\"glyph_count\":" + glyphCount
                + ",\"character_count\":" + characterCount
                + ",\"include_tables\":" + JsonUtil.Bool(includeTables)
                + ",\"max_items\":" + maxItems
                + ",\"characters\":[" + charactersJson + "]"
                + ",\"characters_truncated\":" + JsonUtil.Bool(includeTables && charactersTruncated)
                + ",\"glyphs\":[" + glyphsJson + "]"
                + ",\"glyphs_truncated\":" + JsonUtil.Bool(includeTables && glyphsTruncated)
                + ",\"warnings\":[\"TMP font asset data is read from serialized fields by name; field availability may vary by TextMeshPro version.\",\"This command does not generate atlases, import, refresh, write, move, or fix assets.\"]}";
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

        static string AtlasPopulationModeJson(SerializedObject serializedObject)
        {
            var property = FindFirstProperty(serializedObject, "m_AtlasPopulationMode", "atlasPopulationMode");
            if (property == null)
            {
                return "{\"value\":-1,\"name\":\"\",\"dynamic\":false,\"static\":false}";
            }

            var value = SerializedInt(property, -1);
            var name = SerializedEnumName(property);
            var dynamicMode = name.IndexOf("Dynamic", StringComparison.OrdinalIgnoreCase) >= 0 || (string.IsNullOrEmpty(name) && value == 1);
            var staticMode = name.IndexOf("Static", StringComparison.OrdinalIgnoreCase) >= 0 || (string.IsNullOrEmpty(name) && value == 0);
            return "{\"value\":" + value
                + ",\"name\":" + JsonUtil.String(name)
                + ",\"dynamic\":" + JsonUtil.Bool(dynamicMode)
                + ",\"static\":" + JsonUtil.Bool(staticMode) + "}";
        }

        static string SourceFontJson(SerializedObject serializedObject)
        {
            var sourceFont = FindFirstProperty(serializedObject, "m_SourceFontFile_EditorRef", "m_SourceFontFile", "sourceFontFile");
            if (sourceFont != null && sourceFont.propertyType == SerializedPropertyType.ObjectReference && sourceFont.objectReferenceValue != null)
            {
                return ObjectReferenceJson(sourceFont.objectReferenceValue);
            }

            var guid = StringPropertyValue(serializedObject, "m_SourceFontFileGUID", "sourceFontFileGUID");
            if (string.IsNullOrEmpty(guid))
            {
                return "null";
            }

            var path = AssetDatabase.GUIDToAssetPath(guid);
            return "{\"path\":" + JsonUtil.String(path)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"name\":" + JsonUtil.String(string.IsNullOrEmpty(path) ? string.Empty : Path.GetFileNameWithoutExtension(path))
                + ",\"type\":\"\"}";
        }

        static bool SourceFontAvailable(SerializedObject serializedObject)
        {
            var sourceFont = FindFirstProperty(serializedObject, "m_SourceFontFile_EditorRef", "m_SourceFontFile", "sourceFontFile");
            if (sourceFont != null && sourceFont.propertyType == SerializedPropertyType.ObjectReference && sourceFont.objectReferenceValue != null)
            {
                return true;
            }

            var guid = StringPropertyValue(serializedObject, "m_SourceFontFileGUID", "sourceFontFileGUID");
            return !string.IsNullOrEmpty(guid) && !string.IsNullOrEmpty(AssetDatabase.GUIDToAssetPath(guid));
        }

        static string AtlasSettingsJson(SerializedObject serializedObject)
        {
            return "{\"width\":" + IntPropertyValue(serializedObject, -1, "m_AtlasWidth", "atlasWidth")
                + ",\"height\":" + IntPropertyValue(serializedObject, -1, "m_AtlasHeight", "atlasHeight")
                + ",\"padding\":" + IntPropertyValue(serializedObject, -1, "m_AtlasPadding", "atlasPadding")
                + ",\"render_mode\":" + EnumPropertyJson(FindFirstProperty(serializedObject, "m_AtlasRenderMode", "atlasRenderMode"))
                + ",\"face_info\":" + FaceInfoJson(FindFirstProperty(serializedObject, "m_FaceInfo", "faceInfo")) + "}";
        }

        static string FaceInfoJson(SerializedProperty faceInfo)
        {
            if (faceInfo == null)
            {
                return "null";
            }

            return "{\"point_size\":" + FloatJson(FindRelativeFloat(faceInfo, -1f, "m_PointSize", "pointSize"))
                + ",\"scale\":" + FloatJson(FindRelativeFloat(faceInfo, -1f, "m_Scale", "scale"))
                + ",\"line_height\":" + FloatJson(FindRelativeFloat(faceInfo, -1f, "m_LineHeight", "lineHeight"))
                + ",\"ascent_line\":" + FloatJson(FindRelativeFloat(faceInfo, -1f, "m_AscentLine", "ascentLine"))
                + ",\"descent_line\":" + FloatJson(FindRelativeFloat(faceInfo, -1f, "m_DescentLine", "descentLine")) + "}";
        }

        static string CharacterTableJson(SerializedObject serializedObject, int maxItems, out bool truncated)
        {
            truncated = false;
            var table = FindFirstProperty(serializedObject, "m_CharacterTable", "characterTable");
            if (table == null || !table.isArray)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var limit = Math.Min(table.arraySize, maxItems);
            truncated = table.arraySize > limit;
            for (var index = 0; index < limit; index++)
            {
                var item = table.GetArrayElementAtIndex(index);
                if (index > 0)
                {
                    builder.Append(',');
                }

                var unicode = FindRelativeInt(item, -1, "m_Unicode", "unicode");
                builder.Append("{\"index\":").Append(index)
                    .Append(",\"unicode\":").Append(unicode)
                    .Append(",\"codepoint\":").Append(JsonUtil.String(CodepointLabel(unicode)))
                    .Append(",\"glyph_index\":").Append(FindRelativeInt(item, -1, "m_GlyphIndex", "glyphIndex"))
                    .Append('}');
            }

            return builder.ToString();
        }

        static string GlyphTableJson(SerializedObject serializedObject, int maxItems, out bool truncated)
        {
            truncated = false;
            var table = FindFirstProperty(serializedObject, "m_GlyphTable", "glyphTable");
            if (table == null || !table.isArray)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var limit = Math.Min(table.arraySize, maxItems);
            truncated = table.arraySize > limit;
            for (var index = 0; index < limit; index++)
            {
                var item = table.GetArrayElementAtIndex(index);
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"index\":").Append(index)
                    .Append(",\"glyph_index\":").Append(FindRelativeInt(item, -1, "m_Index", "index"))
                    .Append(",\"atlas_index\":").Append(FindRelativeInt(item, -1, "m_AtlasIndex", "atlasIndex"))
                    .Append(",\"scale\":").Append(FloatJson(FindRelativeFloat(item, -1f, "m_Scale", "scale")))
                    .Append(",\"glyph_rect\":").Append(GlyphRectJson(FindFirstRelativeProperty(item, "m_GlyphRect", "glyphRect")))
                    .Append('}');
            }

            return builder.ToString();
        }

        static string GlyphRectJson(SerializedProperty rect)
        {
            if (rect == null)
            {
                return "null";
            }

            return "{\"x\":" + FindRelativeInt(rect, -1, "m_X", "x")
                + ",\"y\":" + FindRelativeInt(rect, -1, "m_Y", "y")
                + ",\"width\":" + FindRelativeInt(rect, -1, "m_Width", "width")
                + ",\"height\":" + FindRelativeInt(rect, -1, "m_Height", "height") + "}";
        }

        static List<int> ExtractCharacterCodepoints(SerializedObject serializedObject, int maxScanItems, out int characterTableCount, out int scannedItems, out bool scanTruncated)
        {
            characterTableCount = 0;
            scannedItems = 0;
            scanTruncated = false;
            var codepoints = new List<int>();
            var seen = new HashSet<int>();
            var table = FindFirstProperty(serializedObject, "m_CharacterTable", "characterTable");
            if (table == null || !table.isArray)
            {
                return codepoints;
            }

            characterTableCount = table.arraySize;
            var limit = Math.Min(characterTableCount, maxScanItems);
            scanTruncated = characterTableCount > limit;
            for (var index = 0; index < limit; index++)
            {
                scannedItems++;
                var item = table.GetArrayElementAtIndex(index);
                var unicode = FindRelativeInt(item, -1, "m_Unicode", "unicode");
                if (!IsValidUnicodeScalar(unicode) || seen.Contains(unicode))
                {
                    continue;
                }

                seen.Add(unicode);
                codepoints.Add(unicode);
            }

            codepoints.Sort();
            return codepoints;
        }

        static string FallbackCoverageAssetsJson(SerializedObject serializedObject, int maxFallbackAssets, int maxFontScanItems, HashSet<int> coveredCodepoints, out int fallbackFontAssetCount, out int fallbackAssetsConsidered, out bool fallbacksTruncated)
        {
            fallbackFontAssetCount = 0;
            fallbackAssetsConsidered = 0;
            fallbacksTruncated = false;
            var fallbackTable = FindFirstProperty(serializedObject, "m_FallbackFontAssetTable", "fallbackFontAssetTable");
            if (fallbackTable == null || !fallbackTable.isArray || maxFallbackAssets <= 0)
            {
                fallbacksTruncated = fallbackTable != null && fallbackTable.isArray && fallbackTable.arraySize > 0 && maxFallbackAssets <= 0;
                fallbackFontAssetCount = fallbackTable != null && fallbackTable.isArray ? fallbackTable.arraySize : 0;
                return string.Empty;
            }

            fallbackFontAssetCount = fallbackTable.arraySize;
            var limit = Math.Min(fallbackFontAssetCount, maxFallbackAssets);
            fallbacksTruncated = fallbackFontAssetCount > limit;
            var builder = new StringBuilder();
            for (var index = 0; index < limit; index++)
            {
                var property = fallbackTable.GetArrayElementAtIndex(index);
                if (property == null || property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue == null || !IsTmpFontAsset(property.objectReferenceValue))
                {
                    continue;
                }

                if (!TryCreateSerializedObject(property.objectReferenceValue, out var fallbackSerializedObject))
                {
                    continue;
                }

                var fallbackPath = AssetDatabase.GetAssetPath(property.objectReferenceValue);
                var fallbackCodepoints = ExtractCharacterCodepoints(fallbackSerializedObject, maxFontScanItems, out var characterTableCount, out var scannedItems, out var scanTruncated);
                for (var codepointIndex = 0; codepointIndex < fallbackCodepoints.Count; codepointIndex++)
                {
                    coveredCodepoints.Add(fallbackCodepoints[codepointIndex]);
                }

                if (fallbackAssetsConsidered > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"asset_path\":").Append(JsonUtil.String(fallbackPath))
                    .Append(",\"guid\":").Append(JsonUtil.String(string.IsNullOrEmpty(fallbackPath) ? string.Empty : AssetDatabase.AssetPathToGUID(fallbackPath)))
                    .Append(",\"name\":").Append(JsonUtil.String(property.objectReferenceValue.name))
                    .Append(",\"character_table_count\":").Append(characterTableCount)
                    .Append(",\"scanned_items\":").Append(scannedItems)
                    .Append(",\"scan_truncated\":").Append(JsonUtil.Bool(scanTruncated))
                    .Append(",\"codepoint_count\":").Append(fallbackCodepoints.Count)
                    .Append('}');
                fallbackAssetsConsidered++;
            }

            return builder.ToString();
        }

        static int CollectUnicodeFromContent(string assetPath, string content, HashSet<int> codepoints)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase))
            {
                return CollectUnicodeFromYamlTextFields(content, codepoints);
            }

            AddCollectibleCodepoints(content, codepoints);
            return content.Length;
        }

        static int CollectUnicodeFromYamlTextFields(string content, HashSet<int> codepoints)
        {
            var collectedChars = 0;
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (!TryExtractYamlTextFieldValue(line, out var value))
                    {
                        continue;
                    }

                    collectedChars += value.Length;
                    AddCollectibleCodepoints(value, codepoints);
                }
            }

            return collectedChars;
        }

        static bool TryExtractYamlTextFieldValue(string line, out string value)
        {
            value = string.Empty;
            var trimmed = line.TrimStart();
            var separator = trimmed.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            var fieldName = trimmed.Substring(0, separator).Trim();
            if (!IsYamlTextFieldName(fieldName))
            {
                return false;
            }

            var rawValue = trimmed.Substring(separator + 1).Trim();
            if (string.IsNullOrEmpty(rawValue) || rawValue == "|" || rawValue == ">" || rawValue == "{}" || rawValue == "[]" || rawValue == "null")
            {
                return false;
            }

            value = DecodeYamlScalar(rawValue);
            return value.Length > 0;
        }

        static bool IsYamlTextFieldName(string fieldName)
        {
            return string.Equals(fieldName, "m_Text", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_text", StringComparison.Ordinal)
                || string.Equals(fieldName, "text", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_String", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_Localized", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_Value", StringComparison.Ordinal);
        }

        static string DecodeYamlScalar(string value)
        {
            if (value.Length >= 2 && value[0] == '\'' && value[value.Length - 1] == '\'')
            {
                return value.Substring(1, value.Length - 2).Replace("''", "'");
            }

            if (value.Length >= 2 && value[0] == '"')
            {
                var end = value.LastIndexOf('"');
                if (end > 0)
                {
                    return DecodeYamlEscapes(value.Substring(1, end - 1));
                }
            }

            if (value[0] == '{' || value[0] == '[')
            {
                return string.Empty;
            }

            return value;
        }

        static string DecodeYamlEscapes(string value)
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

                var escaped = value[++index];
                switch (escaped)
                {
                    case '0': builder.Append('\0'); break;
                    case 'a': builder.Append('\a'); break;
                    case 'b': builder.Append('\b'); break;
                    case 't': builder.Append('\t'); break;
                    case 'n': builder.Append('\n'); break;
                    case 'v': builder.Append('\v'); break;
                    case 'f': builder.Append('\f'); break;
                    case 'r': builder.Append('\r'); break;
                    case '"': builder.Append('"'); break;
                    case '\\': builder.Append('\\'); break;
                    case 'u':
                        AppendYamlHexEscape(value, ref index, 4, builder);
                        break;
                    case 'U':
                        AppendYamlHexEscape(value, ref index, 8, builder);
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return builder.ToString();
        }

        static void AppendYamlHexEscape(string value, ref int index, int digits, StringBuilder builder)
        {
            if (index + digits >= value.Length)
            {
                builder.Append(value[index]);
                return;
            }

            var hex = value.Substring(index + 1, digits);
            if (int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var codepoint) && IsValidUnicodeScalar(codepoint))
            {
                builder.Append(char.ConvertFromUtf32(codepoint));
                index += digits;
                return;
            }

            builder.Append(value[index]);
        }

        static void AddCollectibleCodepoints(string text, HashSet<int> codepoints)
        {
            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                int codepoint;
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    {
                        continue;
                    }

                    codepoint = char.ConvertToUtf32(current, text[++index]);
                }
                else if (char.IsLowSurrogate(current))
                {
                    continue;
                }
                else
                {
                    codepoint = current;
                }

                if (IsCollectibleUnicodeScalar(codepoint))
                {
                    codepoints.Add(codepoint);
                }
            }
        }

        static void AddCollectibleCodepoints(string text, HashSet<int> codepoints, int maxInputItems, ref int inputItemCount, ref bool inputTruncated)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (inputItemCount >= maxInputItems)
                {
                    inputTruncated = true;
                    return;
                }

                var current = text[index];
                int codepoint;
                if (char.IsHighSurrogate(current))
                {
                    if (index + 1 >= text.Length || !char.IsLowSurrogate(text[index + 1]))
                    {
                        continue;
                    }

                    codepoint = char.ConvertToUtf32(current, text[++index]);
                }
                else if (char.IsLowSurrogate(current))
                {
                    continue;
                }
                else
                {
                    codepoint = current;
                }

                if (IsCollectibleUnicodeScalar(codepoint))
                {
                    inputItemCount++;
                    codepoints.Add(codepoint);
                }
            }
        }

        static void AddUnicodeCodepointTokens(string tokensText, HashSet<int> codepoints, int maxInputItems, ref int inputItemCount, ref int parsedTokenCount, ref int invalidTokenCount, ref bool inputTruncated)
        {
            if (string.IsNullOrEmpty(tokensText))
            {
                return;
            }

            var tokens = SplitUnicodeTokens(tokensText);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (inputItemCount >= maxInputItems)
                {
                    inputTruncated = true;
                    return;
                }

                var token = NormalizeUnicodeToken(tokens[index]);
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (TryParseUnicodeCodepointToken(token, out var codepoint) && IsCollectibleUnicodeScalar(codepoint))
                {
                    parsedTokenCount++;
                    inputItemCount++;
                    codepoints.Add(codepoint);
                }
                else
                {
                    invalidTokenCount++;
                }
            }
        }

        static void AddUnicodeRangeTokens(string tokensText, HashSet<int> codepoints, int maxInputItems, ref int inputItemCount, ref int parsedTokenCount, ref int invalidTokenCount, ref bool inputTruncated)
        {
            if (string.IsNullOrEmpty(tokensText))
            {
                return;
            }

            var tokens = SplitUnicodeTokens(tokensText);
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                var token = NormalizeUnicodeToken(tokens[tokenIndex]);
                if (string.IsNullOrEmpty(token))
                {
                    continue;
                }

                if (!TryParseUnicodeRangeToken(token, out var rangeStart, out var rangeEnd))
                {
                    invalidTokenCount++;
                    continue;
                }

                parsedTokenCount++;
                for (var codepoint = rangeStart; codepoint <= rangeEnd; codepoint++)
                {
                    if (inputItemCount >= maxInputItems)
                    {
                        inputTruncated = true;
                        return;
                    }

                    if (IsCollectibleUnicodeScalar(codepoint))
                    {
                        inputItemCount++;
                        codepoints.Add(codepoint);
                    }

                    if (codepoint == int.MaxValue)
                    {
                        break;
                    }
                }
            }
        }

        static List<string> SplitUnicodeTokens(string text)
        {
            var tokens = new List<string>();
            var builder = new StringBuilder();
            for (var index = 0; index < text.Length; index++)
            {
                var current = text[index];
                if (char.IsWhiteSpace(current) || current == ',' || current == ';')
                {
                    if (builder.Length > 0)
                    {
                        tokens.Add(builder.ToString());
                        builder.Length = 0;
                    }

                    continue;
                }

                builder.Append(current);
            }

            if (builder.Length > 0)
            {
                tokens.Add(builder.ToString());
            }

            return tokens;
        }

        static string NormalizeUnicodeToken(string token)
        {
            return token.Trim().Trim('[', ']', '{', '}', '"', '\'', ':');
        }

        static bool TryParseUnicodeRangeToken(string token, out int rangeStart, out int rangeEnd)
        {
            rangeStart = 0;
            rangeEnd = 0;
            var separator = token.IndexOf("..", StringComparison.Ordinal);
            var separatorLength = 2;
            if (separator < 0)
            {
                separator = token.IndexOf('-', 1);
                separatorLength = 1;
            }

            if (separator <= 0 || separator + separatorLength >= token.Length)
            {
                if (!TryParseUnicodeCodepointToken(token, out rangeStart))
                {
                    return false;
                }

                rangeEnd = rangeStart;
                return true;
            }

            if (!TryParseUnicodeCodepointToken(token.Substring(0, separator), out rangeStart)
                || !TryParseUnicodeCodepointToken(token.Substring(separator + separatorLength), out rangeEnd))
            {
                return false;
            }

            if (rangeEnd < rangeStart)
            {
                var temp = rangeStart;
                rangeStart = rangeEnd;
                rangeEnd = temp;
            }

            return true;
        }

        static bool TryParseUnicodeCodepointToken(string token, out int codepoint)
        {
            codepoint = 0;
            token = NormalizeUnicodeToken(token);
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (token.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseUnicodeInt(token.Substring(2), NumberStyles.HexNumber, out codepoint);
            }

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return TryParseUnicodeInt(token.Substring(2), NumberStyles.HexNumber, out codepoint);
            }

            if (ContainsHexLetter(token))
            {
                return TryParseUnicodeInt(token, NumberStyles.HexNumber, out codepoint);
            }

            if (IsAllDigits(token))
            {
                var style = token.Length >= 4 ? NumberStyles.HexNumber : NumberStyles.Integer;
                return TryParseUnicodeInt(token, style, out codepoint);
            }

            if (token.Length == 1)
            {
                codepoint = token[0];
                return IsValidUnicodeScalar(codepoint);
            }

            if (token.Length == 2 && char.IsHighSurrogate(token[0]) && char.IsLowSurrogate(token[1]))
            {
                codepoint = char.ConvertToUtf32(token[0], token[1]);
                return IsValidUnicodeScalar(codepoint);
            }

            return false;
        }

        static bool TryParseUnicodeInt(string token, NumberStyles style, out int codepoint)
        {
            return int.TryParse(token, style, CultureInfo.InvariantCulture, out codepoint) && IsValidUnicodeScalar(codepoint);
        }

        static bool ContainsHexLetter(string token)
        {
            for (var index = 0; index < token.Length; index++)
            {
                var current = token[index];
                if ((current >= 'a' && current <= 'f') || (current >= 'A' && current <= 'F'))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsAllDigits(string token)
        {
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            for (var index = 0; index < token.Length; index++)
            {
                if (!char.IsDigit(token[index]))
                {
                    return false;
                }
            }

            return true;
        }

        static string CodepointLabelsJson(List<int> codepoints, int maxCodepoints, out bool truncated)
        {
            truncated = codepoints.Count > maxCodepoints;
            var builder = new StringBuilder();
            var limit = Math.Min(codepoints.Count, maxCodepoints);
            for (var index = 0; index < limit; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(CodepointLabel(codepoints[index])));
            }

            return builder.ToString();
        }

        static string UnicodeRangesJson(List<int> codepoints, int maxRanges, out int rangeCount, out bool truncated)
        {
            rangeCount = 0;
            truncated = false;
            if (codepoints.Count == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var rangeStart = codepoints[0];
            var previous = codepoints[0];
            for (var index = 1; index <= codepoints.Count; index++)
            {
                var closesRange = index == codepoints.Count || codepoints[index] != previous + 1;
                if (!closesRange)
                {
                    previous = codepoints[index];
                    continue;
                }

                rangeCount++;
                if (rangeCount <= maxRanges)
                {
                    if (builder.Length > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append("{\"start\":").Append(rangeStart)
                        .Append(",\"end\":").Append(previous)
                        .Append(",\"start_hex\":").Append(JsonUtil.String(CodepointLabel(rangeStart)))
                        .Append(",\"end_hex\":").Append(JsonUtil.String(CodepointLabel(previous)))
                        .Append(",\"count\":").Append(previous - rangeStart + 1)
                        .Append('}');
                }
                else
                {
                    truncated = true;
                }

                if (index < codepoints.Count)
                {
                    rangeStart = codepoints[index];
                    previous = codepoints[index];
                }
            }

            return builder.ToString();
        }

        static string UnicodeString(List<int> codepoints, int maxStringChars, out bool truncated)
        {
            truncated = false;
            var builder = new StringBuilder();
            for (var index = 0; index < codepoints.Count; index++)
            {
                var value = char.ConvertFromUtf32(codepoints[index]);
                if (builder.Length + value.Length > maxStringChars)
                {
                    truncated = true;
                    break;
                }

                builder.Append(value);
            }

            return builder.ToString();
        }

        static string ObjectReferenceArrayJson(SerializedProperty arrayProperty, bool includeItems, int maxItems, out int count, out bool truncated)
        {
            count = arrayProperty != null && arrayProperty.isArray ? arrayProperty.arraySize : 0;
            truncated = includeItems && count > maxItems;
            if (!includeItems || arrayProperty == null || !arrayProperty.isArray)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var limit = Math.Min(count, maxItems);
            for (var index = 0; index < limit; index++)
            {
                var item = arrayProperty.GetArrayElementAtIndex(index);
                if (item == null || item.propertyType != SerializedPropertyType.ObjectReference || item.objectReferenceValue == null)
                {
                    continue;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append(ObjectReferenceJson(item.objectReferenceValue));
            }

            return builder.ToString();
        }

        static string ObjectReferencePropertyJson(SerializedProperty property)
        {
            return property != null && property.propertyType == SerializedPropertyType.ObjectReference ? ObjectReferenceJson(property.objectReferenceValue) : "null";
        }

        static string ObjectReferenceJson(UnityEngine.Object value)
        {
            if (value == null)
            {
                return "null";
            }

            var path = AssetDatabase.GetAssetPath(value);
            var type = value.GetType();
            return "{\"path\":" + JsonUtil.String(path)
                + ",\"guid\":" + JsonUtil.String(string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path))
                + ",\"name\":" + JsonUtil.String(value.name)
                + ",\"type\":" + JsonUtil.String(type.FullName ?? type.Name) + "}";
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

        static int ArraySize(SerializedObject serializedObject, params string[] propertyPaths)
        {
            var property = FindFirstProperty(serializedObject, propertyPaths);
            return property != null && property.isArray ? property.arraySize : 0;
        }

        static int IntPropertyValue(SerializedObject serializedObject, int defaultValue, params string[] propertyPaths)
        {
            var property = FindFirstProperty(serializedObject, propertyPaths);
            return SerializedInt(property, defaultValue);
        }

        static string StringPropertyValue(SerializedObject serializedObject, params string[] propertyPaths)
        {
            var property = FindFirstProperty(serializedObject, propertyPaths);
            return property != null && property.propertyType == SerializedPropertyType.String ? property.stringValue ?? string.Empty : string.Empty;
        }

        static int FindRelativeInt(SerializedProperty property, int defaultValue, params string[] propertyPaths)
        {
            return SerializedInt(FindFirstRelativeProperty(property, propertyPaths), defaultValue);
        }

        static float FindRelativeFloat(SerializedProperty property, float defaultValue, params string[] propertyPaths)
        {
            var relative = FindFirstRelativeProperty(property, propertyPaths);
            if (relative == null)
            {
                return defaultValue;
            }

            if (relative.propertyType == SerializedPropertyType.Float)
            {
                return relative.floatValue;
            }

            return relative.propertyType == SerializedPropertyType.Integer ? relative.intValue : defaultValue;
        }

        static int SerializedInt(SerializedProperty property, int defaultValue)
        {
            if (property == null)
            {
                return defaultValue;
            }

            if (property.propertyType == SerializedPropertyType.Integer)
            {
                return property.intValue;
            }

            if (property.propertyType == SerializedPropertyType.Enum)
            {
                return property.enumValueIndex;
            }

            return defaultValue;
        }

        static string SerializedEnumName(SerializedProperty property)
        {
            if (property == null)
            {
                return string.Empty;
            }

            if (property.propertyType == SerializedPropertyType.Enum && property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length)
            {
                return property.enumDisplayNames[property.enumValueIndex];
            }

            return property.propertyType == SerializedPropertyType.Enum ? property.enumValueIndex.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }

        static string EnumPropertyJson(SerializedProperty property)
        {
            if (property == null)
            {
                return "null";
            }

            return "{\"value\":" + SerializedInt(property, -1) + ",\"name\":" + JsonUtil.String(SerializedEnumName(property)) + "}";
        }

        static string CodepointLabel(int codepoint)
        {
            return codepoint < 0 ? string.Empty : "U+" + codepoint.ToString(codepoint <= 0xFFFF ? "X4" : "X6", CultureInfo.InvariantCulture);
        }

        static bool IsValidUnicodeScalar(int codepoint)
        {
            return codepoint >= 0 && codepoint <= 0x10FFFF && (codepoint < 0xD800 || codepoint > 0xDFFF);
        }

        static bool IsCollectibleUnicodeScalar(int codepoint)
        {
            return IsValidUnicodeScalar(codepoint) && codepoint >= 0x20 && codepoint != 0x7F;
        }

        static string FloatJson(float value)
        {
            if (float.IsNaN(value) || float.IsInfinity(value))
            {
                return "null";
            }

            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        static bool IsTmpFontAsset(UnityEngine.Object asset)
        {
            return asset != null && IsTypeNamed(asset.GetType(), "TMPro.TMP_FontAsset");
        }

        static bool IsTypeNamed(Type type, string fullName)
        {
            return type != null && (type.FullName == fullName || IsSubclassNamed(type, fullName));
        }

        static bool IsSubclassNamed(Type type, string fullName)
        {
            var current = type;
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

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
