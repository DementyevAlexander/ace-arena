using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class UiTextInventoryCommands
    {
        const string FeatureFlag = "ui_text_readonly";
        const string UiTextInventorySchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"include_scenes\":{\"type\":\"boolean\"},\"include_prefabs\":{\"type\":\"boolean\"},\"include_uxml\":{\"type\":\"boolean\"},\"include_text_assets\":{\"type\":\"boolean\"},\"include_localization_assets\":{\"type\":\"boolean\"},\"include_codepoints\":{\"type\":\"boolean\"},\"include_ranges\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"max_text_chars\":{\"type\":\"integer\",\"minimum\":16,\"maximum\":1000},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"additionalProperties\":false}";
        const string UiTextReferencesReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"include_scenes\":{\"type\":\"boolean\"},\"include_prefabs\":{\"type\":\"boolean\"},\"include_uxml\":{\"type\":\"boolean\"},\"include_text_assets\":{\"type\":\"boolean\"},\"include_localization_assets\":{\"type\":\"boolean\"},\"include_codepoints\":{\"type\":\"boolean\"},\"include_ranges\":{\"type\":\"boolean\"},\"max_sources\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1048576},\"max_codepoints\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":20000},\"max_ranges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"additionalProperties\":false}";
        const string UiTextInventoryOutputLimitJson = "{\"max_results\":500,\"max_scan_assets\":100000,\"max_scan_bytes\":1048576,\"max_text_chars\":1000,\"max_codepoints\":20000,\"max_ranges\":5000,\"readonly\":true}";
        const string UiTextReferencesReportOutputLimitJson = "{\"max_sources\":500,\"max_scan_assets\":100000,\"max_scan_bytes\":1048576,\"max_codepoints\":20000,\"max_ranges\":5000,\"readonly\":true}";
        const int MaxResults = 500;
        const int MaxScanAssets = 100000;
        const int MaxScanBytes = 1048576;
        const int MaxTextChars = 1000;
        const int MaxCodepoints = 20000;
        const int MaxRanges = 5000;
        const int MaxEntriesPerAsset = 100;

        [McpCommand(
            "ui.text.inventory",
            Title = "UI text inventory",
            Description = "Inventory bounded UI and localization text samples from scenes, prefabs, UXML, and text assets with TMP font reference hints without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = UiTextInventorySchemaJson,
            Tags = new string[] { "ui", "text", "localization", "tmp", "inventory", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = UiTextInventoryOutputLimitJson,
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

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var includeScenes = JsonUtil.ExtractBool(context.ArgsJson, "include_scenes", true);
            var includePrefabs = JsonUtil.ExtractBool(context.ArgsJson, "include_prefabs", true);
            var includeUxml = JsonUtil.ExtractBool(context.ArgsJson, "include_uxml", true);
            var includeTextAssets = JsonUtil.ExtractBool(context.ArgsJson, "include_text_assets", true);
            var includeLocalizationAssets = JsonUtil.ExtractBool(context.ArgsJson, "include_localization_assets", true);
            var includeCodepoints = JsonUtil.ExtractBool(context.ArgsJson, "include_codepoints", true);
            var includeRanges = JsonUtil.ExtractBool(context.ArgsJson, "include_ranges", true);
            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, MaxResults);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxScanAssets);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 262144), 1, MaxScanBytes);
            var maxTextChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_text_chars", 160), 16, MaxTextChars);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxRanges);
            var paths = CandidateUiTextSourcePaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, includeScenes, includePrefabs, includeUxml, includeTextAssets, includeLocalizationAssets);
            var state = new InventoryState(query, startIndex, maxResults, maxTextChars);
            var scannedAssets = 0;
            var skippedAssets = 0;
            var readErrorCount = 0;
            var readTruncatedCount = 0;
            var scanTruncated = false;

            foreach (var assetPath in paths)
            {
                if (state.Stop)
                {
                    break;
                }

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

                CollectTextEntries(assetPath, byteLength, bytes.Length, fileTruncated, content, state);
            }

            var codepoints = new List<int>(state.Codepoints);
            codepoints.Sort();
            var codepointsTruncated = false;
            var rangesTruncated = false;
            var rangeCount = 0;
            var codepointsJson = includeCodepoints ? CodepointLabelsJson(codepoints, maxCodepoints, out codepointsTruncated) : string.Empty;
            var rangesJson = includeRanges ? UnicodeRangesJson(codepoints, maxRanges, out rangeCount, out rangesTruncated) : string.Empty;

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_scenes\":" + JsonUtil.Bool(includeScenes)
                + ",\"include_prefabs\":" + JsonUtil.Bool(includePrefabs)
                + ",\"include_uxml\":" + JsonUtil.Bool(includeUxml)
                + ",\"include_text_assets\":" + JsonUtil.Bool(includeTextAssets)
                + ",\"include_localization_assets\":" + JsonUtil.Bool(includeLocalizationAssets)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"max_text_chars\":" + maxTextChars
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"skipped_assets\":" + skippedAssets
                + ",\"read_error_count\":" + readErrorCount
                + ",\"read_truncated_count\":" + readTruncatedCount
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"text_entries_found\":" + state.TextEntriesFound
                + ",\"matched_text_entries\":" + state.MatchedEntries
                + ",\"returned_results\":" + state.ReturnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(state.ResultsTruncated)
                + ",\"next_start_index\":" + (state.ResultsTruncated ? (startIndex + state.ReturnedResults).ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"kind_counts\":" + CountsJson(state.KindCounts)
                + ",\"source_counts\":" + CountsJson(state.SourceCounts)
                + ",\"field_counts\":" + CountsJson(state.FieldCounts)
                + ",\"tmp_font_asset_guids\":" + JsonArrayStrings(SortedStrings(state.TmpFontAssetGuids))
                + ",\"localization_hints\":" + JsonArrayStrings(SortedStrings(state.LocalizationHints))
                + ",\"unicode\":{\"format\":\"sorted_unique_unicode_codepoints\",\"codepoint_count\":" + codepoints.Count
                + ",\"include_codepoints\":" + JsonUtil.Bool(includeCodepoints)
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"codepoints_truncated\":" + JsonUtil.Bool(includeCodepoints && codepointsTruncated)
                + ",\"codepoints\":[" + codepointsJson + "]"
                + ",\"include_ranges\":" + JsonUtil.Bool(includeRanges)
                + ",\"max_ranges\":" + maxRanges
                + ",\"range_count\":" + rangeCount
                + ",\"ranges_truncated\":" + JsonUtil.Bool(includeRanges && rangesTruncated)
                + ",\"ranges\":[" + rangesJson + "]}"
                + ",\"text_entries\":[" + state.ResultsJson + "]"
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":[\"This read-only inventory scans bounded UTF-8 text from Unity-relative assets only; it does not open, import, refresh, save, write, move, or fix assets.\",\"TMP font and localization references are best-effort hints extracted from serialized text, not a complete dependency graph.\",\"Large source text is summarized as bounded samples; use focused read/report commands for full approved follow-up context.\"]"
                + ",\"next_valid_actions\":[\"tmp.unicode_set.collect\",\"tmp.font_asset.coverage.check\",\"tmp.font_asset.static.plan\",\"ugui.canvas.scan\",\"uitk.document.inspect\",\"asset.text.read\"]} ");
        }

        [McpCommand(
            "ui.text.references.report",
            Title = "UI text references report",
            Description = "Report bounded UI/localization text source references to TMP font assets, localization origins, and Unicode coverage inputs without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = UiTextReferencesReportSchemaJson,
            Tags = new string[] { "ui", "text", "localization", "tmp", "references", "report", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "status",
            OutputLimitJson = UiTextReferencesReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ReferencesReport(McpCommandContext context)
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

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var includeScenes = JsonUtil.ExtractBool(context.ArgsJson, "include_scenes", true);
            var includePrefabs = JsonUtil.ExtractBool(context.ArgsJson, "include_prefabs", true);
            var includeUxml = JsonUtil.ExtractBool(context.ArgsJson, "include_uxml", true);
            var includeTextAssets = JsonUtil.ExtractBool(context.ArgsJson, "include_text_assets", true);
            var includeLocalizationAssets = JsonUtil.ExtractBool(context.ArgsJson, "include_localization_assets", true);
            var includeCodepoints = JsonUtil.ExtractBool(context.ArgsJson, "include_codepoints", true);
            var includeRanges = JsonUtil.ExtractBool(context.ArgsJson, "include_ranges", true);
            var maxSources = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_sources", 100), 1, MaxResults);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxScanAssets);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", 262144), 1, MaxScanBytes);
            var maxCodepoints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_codepoints", 5000), 1, MaxCodepoints);
            var maxRanges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_ranges", 1000), 1, MaxRanges);
            var paths = CandidateUiTextSourcePaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, includeScenes, includePrefabs, includeUxml, includeTextAssets, includeLocalizationAssets);
            var state = new InventoryState(query, 0, maxSources, 120, false);
            var scannedAssets = 0;
            var skippedAssets = 0;
            var readErrorCount = 0;
            var readTruncatedCount = 0;
            var scanTruncated = false;

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

                CollectTextEntries(assetPath, byteLength, bytes.Length, fileTruncated, content, state);
            }

            var codepoints = new List<int>(state.Codepoints);
            codepoints.Sort();
            var codepointsTruncated = false;
            var rangesTruncated = false;
            var rangeCount = 0;
            var codepointsJson = includeCodepoints ? CodepointLabelsJson(codepoints, maxCodepoints, out codepointsTruncated) : string.Empty;
            var rangesJson = includeRanges ? UnicodeRangesJson(codepoints, maxRanges, out rangeCount, out rangesTruncated) : string.Empty;

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_scenes\":" + JsonUtil.Bool(includeScenes)
                + ",\"include_prefabs\":" + JsonUtil.Bool(includePrefabs)
                + ",\"include_uxml\":" + JsonUtil.Bool(includeUxml)
                + ",\"include_text_assets\":" + JsonUtil.Bool(includeTextAssets)
                + ",\"include_localization_assets\":" + JsonUtil.Bool(includeLocalizationAssets)
                + ",\"max_sources\":" + maxSources
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"max_scan_bytes\":" + maxScanBytes
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"skipped_assets\":" + skippedAssets
                + ",\"read_error_count\":" + readErrorCount
                + ",\"read_truncated_count\":" + readTruncatedCount
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"text_entries_found\":" + state.TextEntriesFound
                + ",\"matched_text_entries\":" + state.MatchedEntries
                + ",\"source_samples_returned\":" + state.ReturnedResults
                + ",\"source_samples_truncated\":" + JsonUtil.Bool(state.ResultsTruncated)
                + ",\"kind_counts\":" + CountsJson(state.KindCounts)
                + ",\"source_counts\":" + CountsJson(state.SourceCounts)
                + ",\"field_counts\":" + CountsJson(state.FieldCounts)
                + ",\"tmp_font_assets\":" + TmpFontAssetsJson(state.TmpFontAssetGuids)
                + ",\"localization_origins\":" + LocalizationOriginsJson(state.LocalizationHints)
                + ",\"unicode\":{\"format\":\"sorted_unique_unicode_codepoints\",\"codepoint_count\":" + codepoints.Count
                + ",\"include_codepoints\":" + JsonUtil.Bool(includeCodepoints)
                + ",\"max_codepoints\":" + maxCodepoints
                + ",\"codepoints_truncated\":" + JsonUtil.Bool(includeCodepoints && codepointsTruncated)
                + ",\"codepoints\":[" + codepointsJson + "]"
                + ",\"include_ranges\":" + JsonUtil.Bool(includeRanges)
                + ",\"max_ranges\":" + maxRanges
                + ",\"range_count\":" + rangeCount
                + ",\"ranges_truncated\":" + JsonUtil.Bool(includeRanges && rangesTruncated)
                + ",\"ranges\":[" + rangesJson + "]}"
                + ",\"source_samples\":[" + state.ResultsJson + "]"
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":[\"This read-only report links bounded text sources to best-effort TMP font and localization hints; it is not a complete dependency graph.\",\"Use tmp.font_asset.coverage.check with the returned Unicode set data for focused TMP coverage verification.\"]"
                + ",\"next_valid_actions\":[\"tmp.font_asset.coverage.check\",\"tmp.font_asset.static.plan\",\"tmp.unicode_set.collect\",\"ui.text.inventory\",\"asset.text.read\"]} ");
        }

        static void CollectTextEntries(string assetPath, long byteLength, int scannedBytes, bool fileTruncated, string content, InventoryState state)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase))
            {
                CollectYamlTextEntries(assetPath, UnicodeSourceKind(assetPath), byteLength, scannedBytes, fileTruncated, content, state);
                return;
            }

            if (string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase))
            {
                CollectUxmlTextEntries(assetPath, byteLength, scannedBytes, fileTruncated, content, state);
                return;
            }

            if (string.Equals(extension, ".xlf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xliff", StringComparison.OrdinalIgnoreCase))
            {
                CollectXliffTextEntries(assetPath, byteLength, scannedBytes, fileTruncated, content, state);
                return;
            }

            CollectTextAssetLines(assetPath, byteLength, scannedBytes, fileTruncated, content, state);
        }

        static void CollectYamlTextEntries(string assetPath, string kind, long byteLength, int scannedBytes, bool fileTruncated, string content, InventoryState state)
        {
            var lineNumber = 0;
            var entriesInAsset = 0;
            var currentTmpFontGuid = string.Empty;
            var currentLocalizationTable = string.Empty;
            var currentLocalizationKey = string.Empty;
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (state.Stop || entriesInAsset >= MaxEntriesPerAsset)
                    {
                        break;
                    }

                    lineNumber++;
                    var trimmed = line.TrimStart();
                    if (trimmed.StartsWith("--- !u!", StringComparison.Ordinal))
                    {
                        currentTmpFontGuid = string.Empty;
                        currentLocalizationTable = string.Empty;
                        currentLocalizationKey = string.Empty;
                        continue;
                    }

                    if (IsTmpFontReferenceLine(trimmed) && TryExtractGuidFromReference(trimmed, out var tmpGuid))
                    {
                        currentTmpFontGuid = tmpGuid;
                    }

                    UpdateLocalizationHints(trimmed, ref currentLocalizationTable, ref currentLocalizationKey);

                    if (!TryExtractYamlTextFieldValue(trimmed, out var fieldName, out var value))
                    {
                        continue;
                    }

                    state.AddTextEntry(assetPath, AssetDatabase.AssetPathToGUID(assetPath), kind, "serialized_yaml_field", fieldName, lineNumber, byteLength, scannedBytes, fileTruncated, value, currentTmpFontGuid, currentLocalizationTable, currentLocalizationKey);
                    entriesInAsset++;
                }
            }
        }

        static void CollectUxmlTextEntries(string assetPath, long byteLength, int scannedBytes, bool fileTruncated, string content, InventoryState state)
        {
            var entriesInAsset = 0;
            foreach (Match match in Regex.Matches(content, "\\b(text|label|tooltip|placeholder|title)\\s*=\\s*(\\\"([^\\\"]*)\\\"|'([^']*)')", RegexOptions.IgnoreCase))
            {
                if (state.Stop || entriesInAsset >= MaxEntriesPerAsset)
                {
                    return;
                }

                var value = match.Groups[3].Success ? match.Groups[3].Value : match.Groups[4].Value;
                state.AddTextEntry(assetPath, AssetDatabase.AssetPathToGUID(assetPath), "uxml", "uxml_attribute", match.Groups[1].Value, LineNumberAt(content, match.Index), byteLength, scannedBytes, fileTruncated, DecodeXmlText(value), string.Empty, string.Empty, string.Empty);
                entriesInAsset++;
            }

            foreach (Match match in Regex.Matches(content, ">([^<>]+)<", RegexOptions.Singleline))
            {
                if (state.Stop || entriesInAsset >= MaxEntriesPerAsset)
                {
                    return;
                }

                state.AddTextEntry(assetPath, AssetDatabase.AssetPathToGUID(assetPath), "uxml", "uxml_element_text", "text", LineNumberAt(content, match.Index), byteLength, scannedBytes, fileTruncated, DecodeXmlText(RemoveXmlTags(match.Groups[1].Value)), string.Empty, string.Empty, string.Empty);
                entriesInAsset++;
            }
        }

        static void CollectXliffTextEntries(string assetPath, long byteLength, int scannedBytes, bool fileTruncated, string content, InventoryState state)
        {
            var entriesInAsset = 0;
            foreach (Match match in Regex.Matches(content, "<(source|target)(?:\\s[^>]*)?>(.*?)</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline))
            {
                if (state.Stop || entriesInAsset >= MaxEntriesPerAsset)
                {
                    return;
                }

                state.AddTextEntry(assetPath, AssetDatabase.AssetPathToGUID(assetPath), "localization_text_asset", "xliff_" + match.Groups[1].Value.ToLowerInvariant(), match.Groups[1].Value.ToLowerInvariant(), LineNumberAt(content, match.Index), byteLength, scannedBytes, fileTruncated, DecodeXmlText(RemoveXmlTags(match.Groups[2].Value)), string.Empty, Path.GetFileNameWithoutExtension(assetPath), string.Empty);
                entriesInAsset++;
            }
        }

        static void CollectTextAssetLines(string assetPath, long byteLength, int scannedBytes, bool fileTruncated, string content, InventoryState state)
        {
            var lineNumber = 0;
            var entriesInAsset = 0;
            using (var reader = new StringReader(content))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    if (state.Stop || entriesInAsset >= MaxEntriesPerAsset)
                    {
                        break;
                    }

                    lineNumber++;
                    var value = ExtractTextAssetLineValue(line);
                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    state.AddTextEntry(assetPath, AssetDatabase.AssetPathToGUID(assetPath), TextAssetKind(assetPath), "text_asset_line", "line", lineNumber, byteLength, scannedBytes, fileTruncated, value, string.Empty, LocalizationTableHintFromPath(assetPath), string.Empty);
                    entriesInAsset++;
                }
            }
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

        static string[] SearchRoots(string pathPrefix, bool includePackages)
        {
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return new[] { "Packages" };
            }

            return includePackages ? new[] { "Assets", "Packages" } : new[] { "Assets" };
        }

        static List<string> CandidateUiTextSourcePaths(string[] searchRoots, bool includePackages, string pathPrefix, bool includeScenes, bool includePrefabs, bool includeUxml, bool includeTextAssets, bool includeLocalizationAssets)
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

                if (!assetPath.StartsWith(pathPrefix, StringComparison.Ordinal) || !IsUiTextSourceCandidate(assetPath, includeScenes, includePrefabs, includeUxml, includeTextAssets, includeLocalizationAssets))
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

        static bool IsUiTextSourceCandidate(string assetPath, bool includeScenes, bool includePrefabs, bool includeUxml, bool includeTextAssets, bool includeLocalizationAssets)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".meta", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (includeScenes && string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)) return true;
            if (includePrefabs && string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)) return true;
            if (includeUxml && string.Equals(extension, ".uxml", StringComparison.OrdinalIgnoreCase)) return true;
            if (includeLocalizationAssets && string.Equals(extension, ".asset", StringComparison.OrdinalIgnoreCase)) return true;
            return includeTextAssets && IsPlainTextAssetExtension(extension);
        }

        static bool IsPlainTextAssetExtension(string extension)
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
                || string.Equals(extension, ".xlf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xliff", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".md", StringComparison.OrdinalIgnoreCase);
        }

        static string UnicodeSourceKind(string assetPath)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".unity", StringComparison.OrdinalIgnoreCase)) return "scene";
            if (string.Equals(extension, ".prefab", StringComparison.OrdinalIgnoreCase)) return "prefab";
            return "localization_asset";
        }

        static string TextAssetKind(string assetPath)
        {
            var extension = Path.GetExtension(assetPath);
            if (string.Equals(extension, ".po", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".strings", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".resx", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xlf", StringComparison.OrdinalIgnoreCase)
                || string.Equals(extension, ".xliff", StringComparison.OrdinalIgnoreCase)
                || assetPath.IndexOf("localization", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return "localization_text_asset";
            }

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

        static bool TryExtractYamlTextFieldValue(string trimmedLine, out string fieldName, out string value)
        {
            fieldName = string.Empty;
            value = string.Empty;
            var separator = trimmedLine.IndexOf(':');
            if (separator <= 0)
            {
                return false;
            }

            fieldName = trimmedLine.Substring(0, separator).Trim();
            if (!IsYamlTextFieldName(fieldName))
            {
                return false;
            }

            var rawValue = trimmedLine.Substring(separator + 1).Trim();
            if (IsIgnoredYamlScalar(rawValue))
            {
                return false;
            }

            value = DecodeYamlScalar(rawValue);
            return HasInventoryText(value);
        }

        static bool IsYamlTextFieldName(string fieldName)
        {
            return string.Equals(fieldName, "m_Text", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_text", StringComparison.Ordinal)
                || string.Equals(fieldName, "text", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_String", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_Value", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_DefaultValue", StringComparison.Ordinal)
                || string.Equals(fieldName, "m_Comment", StringComparison.Ordinal);
        }

        static bool IsIgnoredYamlScalar(string rawValue)
        {
            return string.IsNullOrEmpty(rawValue)
                || rawValue == "|"
                || rawValue == ">"
                || rawValue == "{}"
                || rawValue == "[]"
                || rawValue == "null"
                || rawValue == "true"
                || rawValue == "false";
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
                    case 'u': AppendYamlHexEscape(value, ref index, 4, builder); break;
                    case 'U': AppendYamlHexEscape(value, ref index, 8, builder); break;
                    default: builder.Append(escaped); break;
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

        static bool IsTmpFontReferenceLine(string trimmedLine)
        {
            return trimmedLine.StartsWith("m_fontAsset:", StringComparison.Ordinal)
                || trimmedLine.StartsWith("m_FontAsset:", StringComparison.Ordinal)
                || trimmedLine.StartsWith("fontAsset:", StringComparison.Ordinal)
                || trimmedLine.StartsWith("m_FallbackFontAssetTable:", StringComparison.Ordinal);
        }

        static bool TryExtractGuidFromReference(string line, out string guid)
        {
            guid = string.Empty;
            var marker = line.IndexOf("guid:", StringComparison.Ordinal);
            if (marker < 0)
            {
                return false;
            }

            var start = marker + "guid:".Length;
            while (start < line.Length && char.IsWhiteSpace(line[start]))
            {
                start++;
            }

            var end = start;
            while (end < line.Length && (char.IsLetterOrDigit(line[end]) || line[end] == '-'))
            {
                end++;
            }

            guid = line.Substring(start, end - start);
            return guid.Length > 0;
        }

        static void UpdateLocalizationHints(string trimmedLine, ref string tableReference, ref string keyReference)
        {
            if (TryExtractInlineObjectValue(trimmedLine, "m_TableCollectionName", out var tableName)
                || TryExtractInlineObjectValue(trimmedLine, "m_TableReference", out tableName)
                || TryExtractNamedScalar(trimmedLine, "m_TableCollectionName", out tableName)
                || TryExtractNamedScalar(trimmedLine, "m_TableReference", out tableName))
            {
                tableReference = tableName;
            }

            if (TryExtractInlineObjectValue(trimmedLine, "m_Key", out var key)
                || TryExtractInlineObjectValue(trimmedLine, "m_KeyId", out key)
                || TryExtractInlineObjectValue(trimmedLine, "m_TableEntryReference", out key)
                || TryExtractNamedScalar(trimmedLine, "m_Key", out key)
                || TryExtractNamedScalar(trimmedLine, "m_KeyId", out key)
                || TryExtractNamedScalar(trimmedLine, "m_TableEntryReference", out key))
            {
                keyReference = key;
            }
        }

        static bool TryExtractNamedScalar(string trimmedLine, string fieldName, out string value)
        {
            value = string.Empty;
            if (!trimmedLine.StartsWith(fieldName + ":", StringComparison.Ordinal))
            {
                return false;
            }

            var rawValue = trimmedLine.Substring(fieldName.Length + 1).Trim();
            if (IsIgnoredYamlScalar(rawValue))
            {
                return false;
            }

            value = DecodeYamlScalar(rawValue);
            return value.Length > 0;
        }

        static bool TryExtractInlineObjectValue(string line, string fieldName, out string value)
        {
            value = string.Empty;
            var marker = line.IndexOf(fieldName + ":", StringComparison.Ordinal);
            if (marker < 0)
            {
                return false;
            }

            var start = marker + fieldName.Length + 1;
            while (start < line.Length && char.IsWhiteSpace(line[start]))
            {
                start++;
            }

            if (start >= line.Length)
            {
                return false;
            }

            if (line[start] == '"' || line[start] == '\'')
            {
                var quote = line[start];
                var endQuote = line.IndexOf(quote, start + 1);
                if (endQuote <= start)
                {
                    return false;
                }

                value = DecodeYamlScalar(line.Substring(start, endQuote - start + 1));
                return value.Length > 0;
            }

            var end = start;
            while (end < line.Length && line[end] != ',' && line[end] != '}')
            {
                end++;
            }

            value = line.Substring(start, end - start).Trim();
            return value.Length > 0 && !IsIgnoredYamlScalar(value);
        }

        static string ExtractTextAssetLineValue(string line)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed == "{" || trimmed == "}" || trimmed == "[" || trimmed == "]" || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (trimmed.StartsWith("msgid ", StringComparison.Ordinal) || trimmed.StartsWith("msgstr ", StringComparison.Ordinal))
            {
                return ExtractQuotedValue(trimmed, trimmed.IndexOf(' '));
            }

            var colon = trimmed.IndexOf(':');
            if (colon > 0)
            {
                var quoted = ExtractQuotedValue(trimmed, colon);
                if (HasInventoryText(quoted))
                {
                    return quoted;
                }
            }

            trimmed = trimmed.TrimEnd(',');
            return HasInventoryText(trimmed) ? trimmed : string.Empty;
        }

        static string ExtractQuotedValue(string text, int afterIndex)
        {
            var quote = text.IndexOf('"', Math.Max(0, afterIndex));
            if (quote < 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var escaped = false;
            for (var index = quote + 1; index < text.Length; index++)
            {
                var current = text[index];
                if (escaped)
                {
                    builder.Append(current);
                    escaped = false;
                    continue;
                }

                if (current == '\\')
                {
                    escaped = true;
                    continue;
                }

                if (current == '"')
                {
                    return builder.ToString();
                }

                builder.Append(current);
            }

            return string.Empty;
        }

        static string LocalizationTableHintFromPath(string assetPath)
        {
            return assetPath.IndexOf("localization", StringComparison.OrdinalIgnoreCase) >= 0 ? Path.GetFileNameWithoutExtension(assetPath) : string.Empty;
        }

        static string DecodeXmlText(string value)
        {
            return value.Replace("&quot;", "\"").Replace("&apos;", "'").Replace("&lt;", "<").Replace("&gt;", ">").Replace("&amp;", "&");
        }

        static string RemoveXmlTags(string value)
        {
            return Regex.Replace(value, "<[^>]+>", string.Empty);
        }

        static int LineNumberAt(string content, int offset)
        {
            var line = 1;
            var limit = Math.Min(Math.Max(offset, 0), content.Length);
            for (var index = 0; index < limit; index++)
            {
                if (content[index] == '\n')
                {
                    line++;
                }
            }

            return line;
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

        static bool HasInventoryText(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]) && !char.IsControl(value[index]))
                {
                    return true;
                }
            }

            return false;
        }

        static string NormalizeSample(string value)
        {
            var builder = new StringBuilder();
            var previousWhitespace = false;
            value = value.Trim();
            for (var index = 0; index < value.Length; index++)
            {
                var current = value[index];
                if (char.IsWhiteSpace(current))
                {
                    if (!previousWhitespace)
                    {
                        builder.Append(' ');
                        previousWhitespace = true;
                    }
                    continue;
                }

                builder.Append(current);
                previousWhitespace = false;
            }

            return builder.ToString().Trim();
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

        static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                key = "unknown";
            }

            counts.TryGetValue(key, out var value);
            counts[key] = value + 1;
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

        static List<string> SortedStrings(HashSet<string> values)
        {
            var results = new List<string>();
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    results.Add(value);
                }
            }

            results.Sort(StringComparer.Ordinal);
            return results;
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

        static string TmpFontAssetsJson(HashSet<string> guids)
        {
            var sorted = SortedStrings(guids);
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < sorted.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var guid = sorted[index];
                var path = AssetDatabase.GUIDToAssetPath(guid);
                builder.Append("{\"guid\":").Append(JsonUtil.String(guid))
                    .Append(",\"asset_path\":").Append(string.IsNullOrEmpty(path) ? "null" : JsonUtil.String(path))
                    .Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        static string LocalizationOriginsJson(HashSet<string> hints)
        {
            var sorted = SortedStrings(hints);
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < sorted.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var hint = sorted[index];
                var separator = hint.IndexOf(':');
                var table = separator < 0 ? hint : hint.Substring(0, separator);
                var key = separator < 0 ? string.Empty : hint.Substring(separator + 1);
                builder.Append("{\"hint\":").Append(JsonUtil.String(hint))
                    .Append(",\"table_reference\":").Append(JsonUtil.String(table))
                    .Append(",\"key_reference\":").Append(JsonUtil.String(key))
                    .Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        sealed class InventoryState
        {
            readonly string Query;
            readonly int StartIndex;
            readonly int MaxResults;
            readonly int MaxTextChars;
            readonly bool StopAfterResultLimit;

            public InventoryState(string query, int startIndex, int maxResults, int maxTextChars, bool stopAfterResultLimit = true)
            {
                Query = query;
                StartIndex = startIndex;
                MaxResults = maxResults;
                MaxTextChars = maxTextChars;
                StopAfterResultLimit = stopAfterResultLimit;
            }

            public int TextEntriesFound { get; private set; }
            public int MatchedEntries { get; private set; }
            public int ReturnedResults { get; private set; }
            public bool ResultsTruncated { get; private set; }
            public bool Stop { get; private set; }
            public StringBuilder ResultsJson { get; } = new StringBuilder();
            public HashSet<int> Codepoints { get; } = new HashSet<int>();
            public Dictionary<string, int> KindCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> SourceCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
            public Dictionary<string, int> FieldCounts { get; } = new Dictionary<string, int>(StringComparer.Ordinal);
            public HashSet<string> TmpFontAssetGuids { get; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> LocalizationHints { get; } = new HashSet<string>(StringComparer.Ordinal);

            public void AddTextEntry(string assetPath, string guid, string kind, string source, string field, int lineNumber, long byteLength, int scannedBytes, bool scanTruncated, string text, string tmpFontAssetGuid, string localizationTable, string localizationKey)
            {
                text = NormalizeSample(text);
                if (!HasInventoryText(text))
                {
                    return;
                }

                TextEntriesFound++;
                if (!Matches(assetPath, kind, source, field, text, localizationTable, localizationKey))
                {
                    return;
                }

                var matchedIndex = MatchedEntries++;
                Increment(KindCounts, kind);
                Increment(SourceCounts, source);
                Increment(FieldCounts, field);
                if (!string.IsNullOrEmpty(tmpFontAssetGuid))
                {
                    TmpFontAssetGuids.Add(tmpFontAssetGuid);
                }
                if (!string.IsNullOrEmpty(localizationTable) || !string.IsNullOrEmpty(localizationKey))
                {
                    LocalizationHints.Add((localizationTable ?? string.Empty) + ":" + (localizationKey ?? string.Empty));
                }

                var entryCodepoints = new HashSet<int>();
                AddCollectibleCodepoints(text, entryCodepoints);
                foreach (var codepoint in entryCodepoints)
                {
                    Codepoints.Add(codepoint);
                }

                if (matchedIndex < StartIndex)
                {
                    return;
                }

                if (ReturnedResults >= MaxResults)
                {
                    ResultsTruncated = true;
                    Stop = StopAfterResultLimit;
                    return;
                }

                var sample = text.Length > MaxTextChars ? text.Substring(0, MaxTextChars) : text;
                if (ReturnedResults > 0)
                {
                    ResultsJson.Append(',');
                }

                var tmpFontAssetPath = string.IsNullOrEmpty(tmpFontAssetGuid) ? string.Empty : AssetDatabase.GUIDToAssetPath(tmpFontAssetGuid);
                ResultsJson.Append("{\"index\":").Append(matchedIndex)
                    .Append(",\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"guid\":").Append(JsonUtil.String(guid))
                    .Append(",\"kind\":").Append(JsonUtil.String(kind))
                    .Append(",\"source\":").Append(JsonUtil.String(source))
                    .Append(",\"field\":").Append(JsonUtil.String(field))
                    .Append(",\"line\":").Append(lineNumber)
                    .Append(",\"byte_length\":").Append(byteLength)
                    .Append(",\"scan_bytes\":").Append(scannedBytes)
                    .Append(",\"scan_truncated\":").Append(JsonUtil.Bool(scanTruncated))
                    .Append(",\"text_sample\":").Append(JsonUtil.String(sample))
                    .Append(",\"text_chars\":").Append(text.Length)
                    .Append(",\"text_truncated\":").Append(JsonUtil.Bool(text.Length > MaxTextChars))
                    .Append(",\"codepoint_count\":").Append(entryCodepoints.Count)
                    .Append(",\"tmp_font_asset_guid\":").Append(string.IsNullOrEmpty(tmpFontAssetGuid) ? "null" : JsonUtil.String(tmpFontAssetGuid))
                    .Append(",\"tmp_font_asset_path\":").Append(string.IsNullOrEmpty(tmpFontAssetPath) ? "null" : JsonUtil.String(tmpFontAssetPath))
                    .Append(",\"localization\":").Append(LocalizationJson(localizationTable, localizationKey))
                    .Append('}');
                ReturnedResults++;
            }

            bool Matches(string assetPath, string kind, string source, string field, string text, string localizationTable, string localizationKey)
            {
                if (string.IsNullOrEmpty(Query))
                {
                    return true;
                }

                return assetPath.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || kind.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || source.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || field.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || text.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0
                    || (!string.IsNullOrEmpty(localizationTable) && localizationTable.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0)
                    || (!string.IsNullOrEmpty(localizationKey) && localizationKey.IndexOf(Query, StringComparison.OrdinalIgnoreCase) >= 0);
            }

            static string LocalizationJson(string tableReference, string keyReference)
            {
                if (string.IsNullOrEmpty(tableReference) && string.IsNullOrEmpty(keyReference))
                {
                    return "null";
                }

                return "{\"table_reference\":" + JsonUtil.String(tableReference ?? string.Empty)
                    + ",\"key_reference\":" + JsonUtil.String(keyReference ?? string.Empty) + "}";
            }
        }
    }
}
