using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class TmpFontAssetWriteCommands
    {
        const string FeatureFlag = "asset_safe_writes";
        const string StaticCopySchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"target_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"unicode_text\":{\"type\":\"string\",\"maxLength\":65536},\"codepoints_text\":{\"type\":\"string\",\"maxLength\":262144},\"ranges_text\":{\"type\":\"string\",\"maxLength\":262144},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"},\"max_input_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000}},\"required\":[\"asset_path\",\"target_asset_path\"],\"additionalProperties\":false}";
        const string StaticConvertSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"unicode_text\":{\"type\":\"string\",\"maxLength\":65536},\"codepoints_text\":{\"type\":\"string\",\"maxLength\":262144},\"ranges_text\":{\"type\":\"string\",\"maxLength\":262144},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"},\"max_input_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string ReferencesSwapSchemaJson = "{\"type\":\"object\",\"properties\":{\"source_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"target_asset_path\":{\"type\":\"string\",\"maxLength\":512},\"asset_paths_text\":{\"type\":\"string\",\"maxLength\":262144},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"},\"max_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}},\"required\":[\"source_asset_path\",\"target_asset_path\",\"asset_paths_text\"],\"additionalProperties\":false}";
        const string FallbacksSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"fallback_asset_paths_text\":{\"type\":\"string\",\"maxLength\":262144},\"mode\":{\"type\":\"string\",\"enum\":[\"replace\",\"append\"]},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"dry_run\":{\"type\":\"boolean\"},\"max_fallback_assets\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100}},\"required\":[\"asset_path\",\"fallback_asset_paths_text\"],\"additionalProperties\":false}";
        const string StaticCopyOutputLimitJson = "{\"max_input_items\":200000,\"max_missing_characters\":4096,\"max_missing_codepoints\":20000,\"max_missing_ranges\":5000,\"dry_run_supported\":true,\"precondition_hash_required\":true}";
        const string ReferencesSwapOutputLimitJson = "{\"max_assets\":500,\"dry_run_supported\":true,\"precondition_hash_required\":true}";
        const string FallbacksSetOutputLimitJson = "{\"max_fallback_assets\":100,\"dry_run_supported\":true,\"precondition_hash_required\":true}";
        const int MaxUnicodeInputItems = 200000;
        const int MaxUnicodeInputChars = 262144;
        const int MaxUnicodeStringChars = 65536;
        const int MaxMissingCharacters = 4096;
        const int MaxMissingCodepoints = 20000;
        const int MaxMissingRanges = 5000;
        const int MaxSwapAssets = 500;
        const int MaxFallbackAssets = 100;

        [McpCommand(
            "tmp.font_asset.static.create",
            Title = "Create static TMP font asset",
            Description = "Create a new static TMP font asset copy under Assets from an existing TMP font asset without overwriting the source",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 60000,
            SchemaJson = StaticCopySchemaJson,
            Tags = new string[] { "tmp", "font", "static", "write", "dry-run" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = StaticCopyOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CreateStaticTmpFontAsset(McpCommandContext context)
        {
            return StaticCopy(context, "tmp.font_asset.static.create");
        }

        [McpCommand(
            "tmp.font_asset.static.bake",
            Title = "Bake static TMP font asset",
            Description = "Bake a new static TMP font asset copy under Assets from an existing TMP font asset and explicit Unicode set",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 60000,
            SchemaJson = StaticCopySchemaJson,
            Tags = new string[] { "tmp", "font", "static", "bake", "write", "dry-run" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = StaticCopyOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult BakeStaticTmpFontAsset(McpCommandContext context)
        {
            return StaticCopy(context, "tmp.font_asset.static.bake");
        }

        [McpCommand(
            "tmp.font_asset.static.convert",
            Title = "Convert TMP font asset to static",
            Description = "Destructive-risk in-place conversion of one TMP font asset to Static atlas population mode with precondition hash and dry-run support",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 60000,
            SchemaJson = StaticConvertSchemaJson,
            Tags = new string[] { "tmp", "font", "static", "convert", "destructive", "dry-run" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = StaticCopyOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ConvertTmpFontAssetToStatic(McpCommandContext context)
        {
            var assetResult = RequireWritableTmpFontAssetPath(context.ArgsJson, "asset_path", out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var maxInputItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_input_items", MaxUnicodeInputItems), 1, MaxUnicodeInputItems);
            var unicode = BuildUnicodeString(context.ArgsJson, maxInputItems, out var unicodeCount, out var inputTruncated, out var invalidTokenCount);
            var precondition = PreconditionHash("tmp.font_asset.static.convert", AssetStamp(assetPath), unicode);
            if (!dryRun)
            {
                var preconditionResult = RequirePrecondition(context.ArgsJson, precondition);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }
            }

            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_convert\":true"
                    + ",\"asset_path\":" + JsonUtil.String(assetPath)
                    + ",\"precondition_hash\":" + JsonUtil.String(precondition)
                    + ",\"unicode_count\":" + unicodeCount
                    + ",\"input_truncated\":" + JsonUtil.Bool(inputTruncated)
                    + ",\"invalid_token_count\":" + invalidTokenCount
                    + ",\"warnings\":[\"This is destructive-risk: dry_run:false mutates the source TMP font asset in place. Create a checkpoint first.\"]}");
            }

            var addResult = TryAddCharacters(asset, unicode, out var missingCharacters, out var addWarning);
            SetAtlasPopulationModeStatic(asset);
            EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"converted\":true"
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"unicode_count\":" + unicodeCount
                + ",\"try_add_characters_result\":" + JsonUtil.Bool(addResult)
                + MissingCharactersDetailsJson(missingCharacters)
                + ",\"warning\":" + JsonUtil.String(addWarning) + "}");
        }

        [McpCommand(
            "tmp.font_asset.references.swap",
            Title = "Swap TMP font asset references",
            Description = "Swap explicit TMP font asset object references in selected Assets using dry-run and precondition hash guards",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 60000,
            SchemaJson = ReferencesSwapSchemaJson,
            Tags = new string[] { "tmp", "font", "references", "write", "dry-run" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = ReferencesSwapOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SwapTmpFontAssetReferences(McpCommandContext context)
        {
            var sourceResult = RequireTmpFontAssetPath(context.ArgsJson, "source_asset_path", out var sourcePath, out var sourceAsset);
            if (sourceResult != null)
            {
                return sourceResult;
            }

            var targetResult = RequireTmpFontAssetPath(context.ArgsJson, "target_asset_path", out var targetPath, out var targetAsset);
            if (targetResult != null)
            {
                return targetResult;
            }

            var maxAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_assets", 100), 1, MaxSwapAssets);
            var pathResult = ParseWritableAssetPaths(JsonUtil.ExtractString(context.ArgsJson, "asset_paths_text"), maxAssets, out var assetPaths, out var assetPathsTruncated);
            if (pathResult != null)
            {
                return pathResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var precondition = PreconditionHash("tmp.font_asset.references.swap", AssetStamp(sourcePath), AssetStamp(targetPath), AssetStamps(assetPaths));
            if (!dryRun)
            {
                var preconditionResult = RequirePrecondition(context.ArgsJson, precondition);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }
            }

            var assetsWithMatches = 0;
            var totalReferences = 0;
            var changedObjects = 0;
            for (var index = 0; index < assetPaths.Count; index++)
            {
                var refs = CountOrSwapReferences(assetPaths[index], sourceAsset, targetAsset, !dryRun, out var objectChanges);
                if (refs > 0)
                {
                    assetsWithMatches++;
                    totalReferences += refs;
                    changedObjects += objectChanges;
                }
            }

            if (!dryRun && totalReferences > 0)
            {
                AssetDatabase.SaveAssets();
            }

            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"source_asset_path\":" + JsonUtil.String(sourcePath)
                + ",\"target_asset_path\":" + JsonUtil.String(targetPath)
                + ",\"asset_count\":" + assetPaths.Count
                + ",\"asset_paths_truncated\":" + JsonUtil.Bool(assetPathsTruncated)
                + ",\"assets_with_matches\":" + assetsWithMatches
                + ",\"reference_count\":" + totalReferences
                + ",\"changed_object_count\":" + changedObjects
                + ",\"precondition_hash\":" + JsonUtil.String(precondition)
                + ",\"warnings\":[\"Reference swapping is limited to serialized object references found in explicitly selected Assets; inspect results with tmp.font_asset.validate after real writes.\"]}");
        }

        [McpCommand(
            "tmp.font_asset.fallbacks.set",
            Title = "Set TMP font asset fallbacks",
            Description = "Replace or append direct fallback TMP font assets on one TMP font asset using dry-run and precondition hash guards",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 60000,
            SchemaJson = FallbacksSetSchemaJson,
            Tags = new string[] { "tmp", "font", "fallbacks", "write", "dry-run" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = FallbacksSetOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SetTmpFontAssetFallbacks(McpCommandContext context)
        {
            var assetResult = RequireWritableTmpFontAssetPath(context.ArgsJson, "asset_path", out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            if (!TryCreateSerializedObject(asset, out var serializedObject))
            {
                return CommandExecutionResult.Error("invalid_field_value", "TMP font asset could not be inspected with SerializedObject");
            }

            var maxFallbackAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_fallback_assets", 20), 0, MaxFallbackAssets);
            var fallbackResult = ParseTmpFontAssetPaths(JsonUtil.ExtractString(context.ArgsJson, "fallback_asset_paths_text"), maxFallbackAssets, out var fallbackPaths, out var fallbackAssets, out var fallbackPathsTruncated);
            if (fallbackResult != null)
            {
                return fallbackResult;
            }

            var mode = Bounded(JsonUtil.ExtractString(context.ArgsJson, "mode"), 16);
            if (string.IsNullOrEmpty(mode))
            {
                mode = "replace";
            }

            if (mode != "replace" && mode != "append")
            {
                return CommandExecutionResult.Error("invalid_field_value", "mode must be replace or append");
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var precondition = PreconditionHash("tmp.font_asset.fallbacks.set", AssetStamp(assetPath), mode, string.Join("\n", fallbackPaths.ToArray()));
            if (!dryRun)
            {
                var preconditionResult = RequirePrecondition(context.ArgsJson, precondition);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }
            }

            var fallbackTable = serializedObject.FindProperty("m_FallbackFontAssetTable") ?? serializedObject.FindProperty("fallbackFontAssetTable");
            if (fallbackTable == null || !fallbackTable.isArray)
            {
                return CommandExecutionResult.Error("invalid_field_value", "TMP fallback font asset table could not be found");
            }

            var existingCount = fallbackTable.arraySize;
            var plannedCount = mode == "replace" ? fallbackAssets.Count : existingCount + CountNewFallbacks(fallbackTable, fallbackAssets);
            if (!dryRun)
            {
                ApplyFallbacks(serializedObject, fallbackTable, fallbackAssets, mode);
                EditorUtility.SetDirty(asset);
                AssetDatabase.SaveAssets();
            }

            return CommandExecutionResult.Ok("{\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"mode\":" + JsonUtil.String(mode)
                + ",\"existing_fallback_count\":" + existingCount
                + ",\"requested_fallback_count\":" + fallbackAssets.Count
                + ",\"planned_fallback_count\":" + plannedCount
                + ",\"fallback_paths_truncated\":" + JsonUtil.Bool(fallbackPathsTruncated)
                + ",\"precondition_hash\":" + JsonUtil.String(precondition)
                + ",\"changed\":" + JsonUtil.Bool(!dryRun)
                + "}");
        }

        static CommandExecutionResult StaticCopy(McpCommandContext context, string operation)
        {
            var assetResult = RequireTmpFontAssetPath(context.ArgsJson, "asset_path", out var assetPath, out var asset);
            if (assetResult != null)
            {
                return assetResult;
            }

            var targetResult = RequireNewTargetAssetPath(context.ArgsJson, assetPath, out var targetAssetPath, out var targetParentPath, out var targetParentExists, out var targetExists);
            if (targetResult != null)
            {
                return targetResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var maxInputItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_input_items", MaxUnicodeInputItems), 1, MaxUnicodeInputItems);
            var unicode = BuildUnicodeString(context.ArgsJson, maxInputItems, out var unicodeCount, out var inputTruncated, out var invalidTokenCount);
            var precondition = PreconditionHash(operation, AssetStamp(assetPath), targetAssetPath, unicode);
            var canWrite = targetParentExists && !targetExists;
            if (!dryRun)
            {
                var preconditionResult = RequirePrecondition(context.ArgsJson, precondition);
                if (preconditionResult != null)
                {
                    return preconditionResult;
                }

                if (!canWrite)
                {
                    return CommandExecutionResult.Error("precondition_failed", "target parent must exist and target asset must not exist");
                }
            }

            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_create\":" + JsonUtil.Bool(canWrite)
                    + ",\"operation\":" + JsonUtil.String(operation)
                    + ",\"asset_path\":" + JsonUtil.String(assetPath)
                    + ",\"target_asset_path\":" + JsonUtil.String(targetAssetPath)
                    + ",\"target_parent_path\":" + JsonUtil.String(targetParentPath)
                    + ",\"target_parent_exists\":" + JsonUtil.Bool(targetParentExists)
                    + ",\"target_exists\":" + JsonUtil.Bool(targetExists)
                    + ",\"unicode_count\":" + unicodeCount
                    + ",\"input_truncated\":" + JsonUtil.Bool(inputTruncated)
                    + ",\"invalid_token_count\":" + invalidTokenCount
                    + ",\"precondition_hash\":" + JsonUtil.String(precondition)
                    + ",\"warnings\":[\"dry_run:false copies the source TMP font asset to target_asset_path, tries to add requested characters through reflected TMP APIs, sets atlas population mode to Static, and saves assets.\"]}");
            }

            if (!AssetDatabase.CopyAsset(assetPath, targetAssetPath))
            {
                return CommandExecutionResult.Error("unity_operation_failed", "AssetDatabase.CopyAsset failed for target_asset_path");
            }

            var targetAsset = AssetDatabase.LoadMainAssetAtPath(targetAssetPath);
            if (!IsTmpFontAsset(targetAsset))
            {
                return CommandExecutionResult.Error("unity_operation_failed", "Copied asset is not a TMP font asset");
            }

            var addResult = TryAddCharacters(targetAsset, unicode, out var missingCharacters, out var addWarning);
            SetAtlasPopulationModeStatic(targetAsset);
            EditorUtility.SetDirty(targetAsset);
            AssetDatabase.SaveAssets();
            return CommandExecutionResult.Ok("{\"dry_run\":false,\"created\":true"
                + ",\"operation\":" + JsonUtil.String(operation)
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"target_asset_path\":" + JsonUtil.String(targetAssetPath)
                + ",\"target_guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(targetAssetPath))
                + ",\"unicode_count\":" + unicodeCount
                + ",\"try_add_characters_result\":" + JsonUtil.Bool(addResult)
                + MissingCharactersDetailsJson(missingCharacters)
                + ",\"warning\":" + JsonUtil.String(addWarning) + "}");
        }

        static CommandExecutionResult RequireTmpFontAssetPath(string argsJson, string propertyName, out string assetPath, out UnityEngine.Object asset)
        {
            assetPath = string.Empty;
            asset = null;
            var rawPath = JsonUtil.ExtractString(argsJson, propertyName);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + propertyName + " is required");
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", propertyName + " must be a Unity-relative path under Assets/ or Packages/");
            }

            if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
            {
                return CommandExecutionResult.Error("asset_not_found", propertyName + " does not exist");
            }

            asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            return IsTmpFontAsset(asset) ? null : CommandExecutionResult.Error("invalid_field_value", propertyName + " must reference a TextMeshPro TMP_FontAsset asset");
        }

        static CommandExecutionResult RequireWritableTmpFontAssetPath(string argsJson, string propertyName, out string assetPath, out UnityEngine.Object asset)
        {
            var result = RequireTmpFontAssetPath(argsJson, propertyName, out assetPath, out asset);
            if (result != null)
            {
                return result;
            }

            return assetPath.StartsWith("Assets/", StringComparison.Ordinal)
                ? null
                : CommandExecutionResult.Error("invalid_field_value", propertyName + " write target must be under Assets/");
        }

        static CommandExecutionResult RequireNewTargetAssetPath(string argsJson, string sourceAssetPath, out string targetAssetPath, out string targetParentPath, out bool targetParentExists, out bool targetExists)
        {
            targetAssetPath = string.Empty;
            targetParentPath = string.Empty;
            targetParentExists = false;
            targetExists = false;
            var rawPath = JsonUtil.ExtractString(argsJson, "target_asset_path");
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument target_asset_path is required");
            }

            try
            {
                targetAssetPath = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "target_asset_path must be a Unity-relative path under Assets/");
            }

            if (!targetAssetPath.StartsWith("Assets/", StringComparison.Ordinal) || !targetAssetPath.EndsWith(".asset", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "target_asset_path must be under Assets/ and end with .asset");
            }

            if (string.Equals(targetAssetPath, sourceAssetPath, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "target_asset_path must not overwrite the source TMP font asset");
            }

            targetParentPath = ParentAssetPath(targetAssetPath);
            targetParentExists = !string.IsNullOrEmpty(targetParentPath) && AssetDatabase.IsValidFolder(targetParentPath);
            targetExists = !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(targetAssetPath));
            return null;
        }

        static CommandExecutionResult ParseTmpFontAssetPaths(string text, int maxItems, out List<string> paths, out List<UnityEngine.Object> assets, out bool truncated)
        {
            paths = new List<string>();
            assets = new List<UnityEngine.Object>();
            truncated = false;
            var tokens = SplitTokens(text);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (paths.Count >= maxItems)
                {
                    truncated = true;
                    break;
                }

                var fakeJson = "{\"path\":" + JsonUtil.String(tokens[index]) + "}";
                var result = RequireTmpFontAssetPath(fakeJson, "path", out var path, out var asset);
                if (result != null)
                {
                    return result;
                }

                if (!paths.Contains(path))
                {
                    paths.Add(path);
                    assets.Add(asset);
                }
            }

            return null;
        }

        static CommandExecutionResult ParseWritableAssetPaths(string text, int maxItems, out List<string> paths, out bool truncated)
        {
            paths = new List<string>();
            truncated = false;
            var tokens = SplitTokens(text);
            if (tokens.Count == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "asset_paths_text must contain at least one Assets/... path");
            }

            for (var index = 0; index < tokens.Count; index++)
            {
                if (paths.Count >= maxItems)
                {
                    truncated = true;
                    break;
                }

                string path;
                try
                {
                    path = PathGuard.RequireUnityAssetPath(tokens[index]);
                }
                catch (ArgumentException)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "asset_paths_text must contain Unity-relative paths");
                }

                if (!path.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "asset_paths_text write targets must be under Assets/");
                }

                if (string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                {
                    return CommandExecutionResult.Error("asset_not_found", "asset_paths_text contains an asset that does not exist");
                }

                if (!paths.Contains(path))
                {
                    paths.Add(path);
                }
            }

            return null;
        }

        static int CountOrSwapReferences(string assetPath, UnityEngine.Object sourceAsset, UnityEngine.Object targetAsset, bool apply, out int changedObjects)
        {
            changedObjects = 0;
            var references = 0;
            var objects = AssetDatabase.LoadAllAssetsAtPath(assetPath);
            for (var objectIndex = 0; objectIndex < objects.Length; objectIndex++)
            {
                var obj = objects[objectIndex];
                if (obj == null || !TryCreateSerializedObject(obj, out var serializedObject))
                {
                    continue;
                }

                var objectChanges = 0;
                var property = serializedObject.GetIterator();
                var enterChildren = true;
                while (property.NextVisible(enterChildren))
                {
                    enterChildren = false;
                    if (property.propertyType != SerializedPropertyType.ObjectReference || property.objectReferenceValue != sourceAsset)
                    {
                        continue;
                    }

                    references++;
                    objectChanges++;
                    if (apply)
                    {
                        property.objectReferenceValue = targetAsset;
                    }
                }

                if (apply && objectChanges > 0)
                {
                    serializedObject.ApplyModifiedPropertiesWithoutUndo();
                    EditorUtility.SetDirty(obj);
                    changedObjects++;
                }
            }

            return references;
        }

        static void ApplyFallbacks(SerializedObject serializedObject, SerializedProperty fallbackTable, List<UnityEngine.Object> fallbackAssets, string mode)
        {
            var next = new List<UnityEngine.Object>();
            if (mode == "append")
            {
                for (var index = 0; index < fallbackTable.arraySize; index++)
                {
                    var item = fallbackTable.GetArrayElementAtIndex(index);
                    if (item != null && item.propertyType == SerializedPropertyType.ObjectReference && item.objectReferenceValue != null && !next.Contains(item.objectReferenceValue))
                    {
                        next.Add(item.objectReferenceValue);
                    }
                }
            }

            for (var index = 0; index < fallbackAssets.Count; index++)
            {
                if (!next.Contains(fallbackAssets[index]))
                {
                    next.Add(fallbackAssets[index]);
                }
            }

            fallbackTable.arraySize = next.Count;
            for (var index = 0; index < next.Count; index++)
            {
                fallbackTable.GetArrayElementAtIndex(index).objectReferenceValue = next[index];
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        static int CountNewFallbacks(SerializedProperty fallbackTable, List<UnityEngine.Object> fallbackAssets)
        {
            var count = 0;
            for (var index = 0; index < fallbackAssets.Count; index++)
            {
                var exists = false;
                for (var tableIndex = 0; tableIndex < fallbackTable.arraySize; tableIndex++)
                {
                    var item = fallbackTable.GetArrayElementAtIndex(tableIndex);
                    if (item != null && item.propertyType == SerializedPropertyType.ObjectReference && item.objectReferenceValue == fallbackAssets[index])
                    {
                        exists = true;
                        break;
                    }
                }

                if (!exists)
                {
                    count++;
                }
            }

            return count;
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

        static bool TryAddCharacters(UnityEngine.Object tmpFontAsset, string characters, out string missingCharacters, out string warning)
        {
            missingCharacters = string.Empty;
            warning = string.Empty;
            if (tmpFontAsset == null || string.IsNullOrEmpty(characters))
            {
                return true;
            }

            try
            {
                var method = tmpFontAsset.GetType().GetMethod("TryAddCharacters", new[] { typeof(string), typeof(string).MakeByRefType(), typeof(bool) });
                if (method != null)
                {
                    var argsWithFeatures = new object[] { characters, string.Empty, false };
                    var resultWithFeatures = method.Invoke(tmpFontAsset, argsWithFeatures);
                    missingCharacters = argsWithFeatures[1] as string ?? string.Empty;
                    return resultWithFeatures is bool && (bool)resultWithFeatures;
                }

                method = tmpFontAsset.GetType().GetMethod("TryAddCharacters", new[] { typeof(string), typeof(string).MakeByRefType() });
                if (method == null)
                {
                    warning = "TMP TryAddCharacters(string, out string, bool) or TryAddCharacters(string, out string) was not found; copied existing glyph table only";
                    return false;
                }

                var argsWithoutFeatures = new object[] { characters, string.Empty };
                var resultWithoutFeatures = method.Invoke(tmpFontAsset, argsWithoutFeatures);
                missingCharacters = argsWithoutFeatures[1] as string ?? string.Empty;
                return resultWithoutFeatures is bool && (bool)resultWithoutFeatures;
            }
            catch (Exception exception)
            {
                warning = "TMP TryAddCharacters failed: " + exception.GetType().Name + ": " + exception.Message;
                return false;
            }
        }

        static string MissingCharactersDetailsJson(string missingCharacters)
        {
            var codepoints = TextCodepoints(missingCharacters);
            var codepointsJson = CodepointLabelsJson(codepoints, MaxMissingCodepoints, out var codepointsTruncated);
            var rangesJson = UnicodeRangesJson(codepoints, MaxMissingRanges, out var rangeCount, out var rangesTruncated);
            return ",\"missing_count\":" + codepoints.Count
                + ",\"missing_characters\":" + JsonUtil.String(Bounded(missingCharacters, MaxMissingCharacters))
                + ",\"missing_characters_truncated\":" + JsonUtil.Bool(!string.IsNullOrEmpty(missingCharacters) && missingCharacters.Length > MaxMissingCharacters)
                + ",\"missing_codepoints_truncated\":" + JsonUtil.Bool(codepointsTruncated)
                + ",\"missing_codepoints\":[" + codepointsJson + "]"
                + ",\"missing_range_count\":" + rangeCount
                + ",\"missing_ranges_truncated\":" + JsonUtil.Bool(rangesTruncated)
                + ",\"missing_ranges\":[" + rangesJson + "]";
        }

        static List<int> TextCodepoints(string text)
        {
            var codepoints = new List<int>();
            if (string.IsNullOrEmpty(text))
            {
                return codepoints;
            }

            var seen = new HashSet<int>();
            for (var index = 0; index < text.Length; index++)
            {
                var codepoint = NextCodepoint(text, ref index);
                if (IsValidUnicodeScalar(codepoint) && seen.Add(codepoint))
                {
                    codepoints.Add(codepoint);
                }
            }

            codepoints.Sort();
            return codepoints;
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
            return "U+" + codepoint.ToString(codepoint <= 0xFFFF ? "X4" : "X6", CultureInfo.InvariantCulture);
        }

        static void SetAtlasPopulationModeStatic(UnityEngine.Object tmpFontAsset)
        {
            if (!TryCreateSerializedObject(tmpFontAsset, out var serializedObject))
            {
                return;
            }

            var property = serializedObject.FindProperty("m_AtlasPopulationMode") ?? serializedObject.FindProperty("atlasPopulationMode");
            if (property == null)
            {
                return;
            }

            if (property.propertyType == SerializedPropertyType.Enum)
            {
                property.enumValueIndex = 0;
            }
            else if (property.propertyType == SerializedPropertyType.Integer)
            {
                property.intValue = 0;
            }

            serializedObject.ApplyModifiedPropertiesWithoutUndo();
        }

        static string BuildUnicodeString(string argsJson, int maxInputItems, out int unicodeCount, out bool inputTruncated, out int invalidTokenCount)
        {
            var values = new List<int>();
            var seen = new HashSet<int>();
            unicodeCount = 0;
            inputTruncated = false;
            invalidTokenCount = 0;
            AddTextCodepoints(Bounded(JsonUtil.ExtractString(argsJson, "unicode_text"), MaxUnicodeStringChars), values, seen, maxInputItems, ref inputTruncated);
            AddCodepointTokens(Bounded(JsonUtil.ExtractString(argsJson, "codepoints_text"), MaxUnicodeInputChars), values, seen, maxInputItems, ref inputTruncated, ref invalidTokenCount);
            AddRangeTokens(Bounded(JsonUtil.ExtractString(argsJson, "ranges_text"), MaxUnicodeInputChars), values, seen, maxInputItems, ref inputTruncated, ref invalidTokenCount);
            values.Sort();
            unicodeCount = values.Count;
            var builder = new StringBuilder();
            for (var index = 0; index < values.Count; index++)
            {
                builder.Append(char.ConvertFromUtf32(values[index]));
            }

            return builder.ToString();
        }

        static void AddTextCodepoints(string text, List<int> values, HashSet<int> seen, int maxInputItems, ref bool truncated)
        {
            for (var index = 0; index < text.Length; index++)
            {
                if (values.Count >= maxInputItems)
                {
                    truncated = true;
                    return;
                }

                var codepoint = NextCodepoint(text, ref index);
                if (IsCollectibleUnicodeScalar(codepoint) && seen.Add(codepoint))
                {
                    values.Add(codepoint);
                }
            }
        }

        static void AddCodepointTokens(string text, List<int> values, HashSet<int> seen, int maxInputItems, ref bool truncated, ref int invalidTokenCount)
        {
            var tokens = SplitTokens(text);
            for (var index = 0; index < tokens.Count; index++)
            {
                if (values.Count >= maxInputItems)
                {
                    truncated = true;
                    return;
                }

                if (TryParseCodepoint(tokens[index], out var codepoint) && IsCollectibleUnicodeScalar(codepoint))
                {
                    if (seen.Add(codepoint))
                    {
                        values.Add(codepoint);
                    }
                }
                else
                {
                    invalidTokenCount++;
                }
            }
        }

        static void AddRangeTokens(string text, List<int> values, HashSet<int> seen, int maxInputItems, ref bool truncated, ref int invalidTokenCount)
        {
            var tokens = SplitTokens(text);
            for (var tokenIndex = 0; tokenIndex < tokens.Count; tokenIndex++)
            {
                if (!TryParseRange(tokens[tokenIndex], out var start, out var end))
                {
                    invalidTokenCount++;
                    continue;
                }

                for (var codepoint = start; codepoint <= end; codepoint++)
                {
                    if (values.Count >= maxInputItems)
                    {
                        truncated = true;
                        return;
                    }

                    if (IsCollectibleUnicodeScalar(codepoint) && seen.Add(codepoint))
                    {
                        values.Add(codepoint);
                    }
                }
            }
        }

        static int NextCodepoint(string text, ref int index)
        {
            var current = text[index];
            if (char.IsHighSurrogate(current) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                return char.ConvertToUtf32(current, text[++index]);
            }

            return current;
        }

        static bool TryParseRange(string token, out int start, out int end)
        {
            start = 0;
            end = 0;
            token = NormalizeToken(token);
            var separator = token.IndexOf("..", StringComparison.Ordinal);
            var length = 2;
            if (separator < 0)
            {
                separator = token.IndexOf('-', 1);
                length = 1;
            }

            if (separator <= 0 || separator + length >= token.Length)
            {
                if (!TryParseCodepoint(token, out start))
                {
                    return false;
                }

                end = start;
                return true;
            }

            if (!TryParseCodepoint(token.Substring(0, separator), out start) || !TryParseCodepoint(token.Substring(separator + length), out end))
            {
                return false;
            }

            if (end < start)
            {
                var temp = start;
                start = end;
                end = temp;
            }

            return true;
        }

        static bool TryParseCodepoint(string token, out int codepoint)
        {
            codepoint = 0;
            token = NormalizeToken(token);
            if (string.IsNullOrEmpty(token))
            {
                return false;
            }

            if (token.StartsWith("U+", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codepoint) && IsValidUnicodeScalar(codepoint);
            }

            if (token.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                return int.TryParse(token.Substring(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codepoint) && IsValidUnicodeScalar(codepoint);
            }

            return int.TryParse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out codepoint) && IsValidUnicodeScalar(codepoint);
        }

        static List<string> SplitTokens(string text)
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

        static string NormalizeToken(string token)
        {
            return token.Trim().Trim('[', ']', '{', '}', '"', '\'', ':');
        }

        static bool IsCollectibleUnicodeScalar(int codepoint)
        {
            return IsValidUnicodeScalar(codepoint) && codepoint >= 0x20 && codepoint != 0x7F;
        }

        static bool IsValidUnicodeScalar(int codepoint)
        {
            return codepoint >= 0 && codepoint <= 0x10FFFF && (codepoint < 0xD800 || codepoint > 0xDFFF);
        }

        static string PreconditionHash(params string[] values)
        {
            using (var sha256 = SHA256.Create())
            {
                return Hex(sha256.ComputeHash(Encoding.UTF8.GetBytes(string.Join("\n---\n", values))));
            }
        }

        static CommandExecutionResult RequirePrecondition(string argsJson, string expected)
        {
            var actual = JsonUtil.ExtractString(argsJson, "precondition_hash").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(actual))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "precondition_hash is required when dry_run is false");
            }

            return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase)
                ? null
                : CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match current TMP asset state or requested operation");
        }

        static string AssetStamps(List<string> paths)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < paths.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append('\n');
                }

                builder.Append(AssetStamp(paths[index]));
            }

            return builder.ToString();
        }

        static string AssetStamp(string path)
        {
            return path + "|" + AssetDatabase.AssetPathToGUID(path) + "|" + AssetDatabase.GetAssetDependencyHash(path).ToString();
        }

        static string Hex(byte[] bytes)
        {
            var builder = new StringBuilder(bytes.Length * 2);
            for (var index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
            }

            return builder.ToString();
        }

        static string ParentAssetPath(string assetPath)
        {
            var slash = assetPath.LastIndexOf('/');
            return slash <= 0 ? string.Empty : assetPath.Substring(0, slash);
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
