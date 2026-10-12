using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using PackageManagerPackageInfo = UnityEditor.PackageManager.PackageInfo;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ShaderInspectionCommands
    {
        const string FeatureFlag = "asset_readonly";
        const string ShaderListSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"query\":{\"type\":\"string\",\"maxLength\":128},\"kind\":{\"type\":\"string\",\"enum\":[\"all\",\"shader\",\"shadergraph\",\"shadergraph_subgraph\",\"compute\",\"hlsl\",\"cginc\",\"include\"]},\"include_packages\":{\"type\":\"boolean\"},\"start_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100000},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000}},\"additionalProperties\":false}";
        const string ShaderDependenciesSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"recursive\":{\"type\":\"boolean\"},\"include_text_includes\":{\"type\":\"boolean\"},\"max_dependencies\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_include_matches\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":200},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":262144}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string ShaderReferencesReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_assetdatabase\":{\"type\":\"boolean\"},\"include_yaml_guid_refs\":{\"type\":\"boolean\"},\"include_text_includes\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_scan_bytes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":262144}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string ShaderListOutputLimitJson = "{\"max_results\":1000,\"max_scan_assets\":100000,\"roots\":[\"Assets\",\"Packages\"],\"readonly\":true}";
        const string ShaderDependenciesOutputLimitJson = "{\"max_dependencies\":500,\"max_include_matches\":200,\"max_scan_bytes\":262144,\"readonly\":true}";
        const string ShaderReferencesReportOutputLimitJson = "{\"max_results\":1000,\"max_scan_assets\":100000,\"max_scan_bytes\":262144,\"readonly\":true}";
        const int MaxResults = 1000;
        const int MaxScanAssets = 100000;
        const int MaxDependencies = 500;
        const int MaxIncludeMatches = 200;
        const int MaxShaderTextBytes = 262144;

        [McpCommand(
            "shader.list",
            Title = "List shader assets",
            Description = "List shader, Shader Graph, compute shader, and include assets under Assets and Packages without compiling or importing",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ShaderListSchemaJson,
            Tags = new string[] { "shader", "asset", "inventory", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = ShaderListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ListShaders(McpCommandContext context)
        {
            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var kind = JsonUtil.ExtractString(context.ArgsJson, "kind");
            if (string.IsNullOrEmpty(kind))
            {
                kind = "all";
            }

            kind = kind.Trim().ToLowerInvariant();
            if (!IsShaderKindFilter(kind))
            {
                return CommandExecutionResult.Error("invalid_field_value", "kind must be all, shader, shadergraph, shadergraph_subgraph, compute, hlsl, cginc, or include");
            }

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128).Trim();
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var startIndex = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, MaxResults);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", MaxScanAssets), 1, MaxScanAssets);
            var paths = CandidateAssetPaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix);
            var builder = new StringBuilder();
            var scannedAssets = 0;
            var matchedShaderAssets = 0;
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
                var assetKind = ShaderAssetKind(assetPath);
                if (string.IsNullOrEmpty(assetKind) || !KindMatches(assetKind, kind))
                {
                    continue;
                }

                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                if (!MatchesQuery(assetPath, guid, assetKind, query))
                {
                    continue;
                }

                matchedShaderAssets++;
                if (matchedShaderAssets <= startIndex)
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

                builder.Append(ShaderAssetJson(assetPath, guid, assetKind));
                returnedResults++;
            }

            return CommandExecutionResult.Ok("{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"kind\":" + JsonUtil.String(kind)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"start_index\":" + startIndex
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_assets\":" + maxScanAssets
                + ",\"scanned_assets\":" + scannedAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"matched_shader_assets\":" + matchedShaderAssets
                + ",\"returned_results\":" + returnedResults
                + ",\"results_truncated\":" + JsonUtil.Bool(resultsTruncated)
                + ",\"next_start_index\":" + (resultsTruncated ? (startIndex + returnedResults).ToString() : "null")
                + ",\"results\":[" + builder + "]"
                + ",\"warnings\":[\"Shader assets are asset/reference inventory, not C# assembly membership.\",\"This command uses AssetDatabase metadata only; it does not compile, import, refresh, load shader assets, write, move, or fix assets.\"]}");
        }

        [McpCommand(
            "shader.dependencies",
            Title = "Inspect shader dependencies",
            Description = "Inspect shader-related AssetDatabase dependencies and bounded include references for one shader, include, Shader Graph, compute shader, or material asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ShaderDependenciesSchemaJson,
            Tags = new string[] { "shader", "asset", "dependencies", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = ShaderDependenciesOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ShaderDependencies(McpCommandContext context)
        {
            var rawAssetPath = JsonUtil.ExtractString(context.ArgsJson, "asset_path");
            if (string.IsNullOrEmpty(rawAssetPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument asset_path is required");
            }

            string assetPath;
            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(rawAssetPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must be a Unity-relative path under Assets/ or Packages/");
            }

            var targetKind = ShaderDependencyKind(assetPath);
            if (string.IsNullOrEmpty(targetKind))
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must reference a .shader, .shadergraph, .shadersubgraph, .compute, .hlsl, .cginc, or .mat asset");
            }

            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (string.IsNullOrEmpty(guid))
            {
                return CommandExecutionResult.Error("asset_not_found", "asset_path does not exist");
            }

            var recursive = JsonUtil.ExtractBool(context.ArgsJson, "recursive", false);
            var includeTextIncludes = JsonUtil.ExtractBool(context.ArgsJson, "include_text_includes", true);
            var maxDependencies = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_dependencies", 100), 1, MaxDependencies);
            var maxIncludeMatches = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_include_matches", 50), 0, MaxIncludeMatches);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", MaxShaderTextBytes), 1, MaxShaderTextBytes);
            var dependenciesJson = ShaderDependenciesJson(assetPath, recursive, maxDependencies, out var dependencyCount, out var dependenciesTruncated);
            var includesJson = "";
            var includeCount = 0;
            var includesTruncated = false;
            var textScanned = false;
            var textByteLength = 0;
            var textTruncated = false;
            var textReadError = string.Empty;
            if (includeTextIncludes && maxIncludeMatches > 0 && IsTextShaderAsset(assetPath))
            {
                textScanned = true;
                if (TryResolveAssetFilePath(assetPath, out var fullPath))
                {
                    var content = ReadUtf8Prefix(fullPath, maxScanBytes, out textByteLength, out textTruncated, out textReadError);
                    if (string.IsNullOrEmpty(textReadError))
                    {
                        includesJson = ShaderIncludesJson(assetPath, content, maxIncludeMatches, out includeCount, out includesTruncated);
                    }
                }
                else
                {
                    textReadError = "asset_file_not_resolved";
                }
            }

            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"target\":" + ShaderAssetJson(assetPath, guid, targetKind)
                + ",\"recursive\":" + JsonUtil.Bool(recursive)
                + ",\"include_text_includes\":" + JsonUtil.Bool(includeTextIncludes)
                + ",\"max_dependencies\":" + maxDependencies
                + ",\"dependency_count\":" + dependencyCount
                + ",\"dependencies_truncated\":" + JsonUtil.Bool(dependenciesTruncated)
                + ",\"dependencies\":[" + dependenciesJson + "]"
                + ",\"max_include_matches\":" + maxIncludeMatches
                + ",\"text_scanned\":" + JsonUtil.Bool(textScanned)
                + ",\"text_byte_length\":" + textByteLength
                + ",\"text_truncated\":" + JsonUtil.Bool(textTruncated)
                + ",\"text_read_error\":" + JsonUtil.String(textReadError)
                + ",\"include_reference_count\":" + includeCount
                + ",\"include_references_truncated\":" + JsonUtil.Bool(includesTruncated)
                + ",\"include_references\":[" + includesJson + "]"
                + ",\"warnings\":[\"Shader assets are asset/reference inventory, not C# assembly membership.\",\"AssetDatabase dependencies may omit text-only includes; bounded include parsing is best-effort and does not resolve built-in include names.\",\"This command does not compile, import, refresh, load shader assets, write, move, or fix assets.\"]}");
        }

        [McpCommand(
            "shader.references.report",
            Title = "Report shader references",
            Description = "Report bounded incoming material, asset, YAML GUID, and include-text references to one shader or include asset",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ShaderReferencesReportSchemaJson,
            Tags = new string[] { "shader", "asset", "references", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = ShaderReferencesReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ShaderReferencesReport(McpCommandContext context)
        {
            var targetResult = RequireShaderReferenceTarget(context.ArgsJson, out var assetPath, out var guid, out var targetKind);
            if (targetResult != null)
            {
                return targetResult;
            }

            var pathPrefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (pathPrefixResult != null)
            {
                return pathPrefixResult;
            }

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                includePackages = true;
            }

            var includeAssetDatabase = JsonUtil.ExtractBool(context.ArgsJson, "include_assetdatabase", true);
            var includeYamlGuidRefs = JsonUtil.ExtractBool(context.ArgsJson, "include_yaml_guid_refs", true);
            var includeTextIncludes = JsonUtil.ExtractBool(context.ArgsJson, "include_text_includes", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, MaxResults);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", MaxScanAssets), 1, MaxScanAssets);
            var maxScanBytes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_bytes", MaxShaderTextBytes), 1, MaxShaderTextBytes);
            var candidates = CandidateAssetPaths(SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix);
            var builder = new StringBuilder();
            var scannedAssets = 0;
            var referenceCount = 0;
            var returnedResults = 0;
            var scanTruncated = false;
            var resultsTruncated = false;
            var readErrorCount = 0;
            foreach (var candidatePath in candidates)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                if (string.Equals(candidatePath, assetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var referenceKinds = new List<string>();
                var includeLine = 0;
                var includeRawReference = string.Empty;
                var readError = string.Empty;
                if (includeAssetDatabase && AssetDatabaseReferencesTarget(candidatePath, assetPath))
                {
                    referenceKinds.Add("assetdatabase_dependency");
                }

                if (includeTextIncludes && IsTextShaderAsset(candidatePath) && TextIncludesReferenceTarget(candidatePath, assetPath, maxScanBytes, out includeLine, out includeRawReference, out readError))
                {
                    referenceKinds.Add("shader_include_text");
                }

                if (!string.IsNullOrEmpty(readError))
                {
                    readErrorCount++;
                }

                if (includeYamlGuidRefs && IsYamlGuidScanCandidate(candidatePath) && YamlGuidReferencesTarget(candidatePath, guid, maxScanBytes, out var yamlReadError))
                {
                    referenceKinds.Add("yaml_guid");
                    if (!string.IsNullOrEmpty(yamlReadError))
                    {
                        readErrorCount++;
                    }
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
                    builder.Append(',');
                }

                builder.Append(ReferenceReportEntryJson(candidatePath, referenceKinds, includeLine, includeRawReference, readError));
                returnedResults++;
            }

            return CommandExecutionResult.Ok("{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"target\":" + ShaderAssetJson(assetPath, guid, targetKind)
                + ",\"path_prefix\":" + JsonUtil.String(pathPrefix)
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
                + ",\"read_error_count\":" + readErrorCount
                + ",\"references\":[" + builder + "]"
                + ",\"warnings\":[\"Shader assets are asset/reference inventory, not C# assembly membership.\",\"Incoming reference scanning is bounded and best-effort; AssetDatabase, YAML GUID, and text include scans can disagree.\",\"This command does not compile, import, refresh, load shader assets, write, move, or fix assets.\"]}");
        }

        static CommandExecutionResult RequireAssetPrefix(string argsJson, out string pathPrefix)
        {
            pathPrefix = JsonUtil.ExtractString(argsJson, "path_prefix");
            if (string.IsNullOrEmpty(pathPrefix))
            {
                pathPrefix = string.Empty;
                return null;
            }

            if (pathPrefix.Length > 512)
            {
                pathPrefix = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", "path_prefix must be at most 512 characters");
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
            if (pathPrefix.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return new string[] { "Assets" };
            }

            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return new string[] { "Packages" };
            }

            return includePackages ? new string[] { "Assets", "Packages" } : new string[] { "Assets" };
        }

        static List<string> CandidateAssetPaths(string[] searchRoots, bool includePackages, string pathPrefix)
        {
            var paths = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var guids = AssetDatabase.FindAssets(string.Empty, searchRoots);
            foreach (var guid in guids)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath) || !seen.Add(assetPath))
                {
                    continue;
                }

                if (!includePackages && assetPath.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(pathPrefix) && !assetPath.StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                paths.Add(assetPath);
            }

            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        static string ShaderAssetKind(string assetPath)
        {
            var extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (extension == ".shader") return "shader";
            if (extension == ".shadergraph") return "shadergraph";
            if (extension == ".shadersubgraph") return "shadergraph_subgraph";
            if (extension == ".compute") return "compute";
            if (extension == ".hlsl") return "hlsl";
            if (extension == ".cginc") return "cginc";
            return string.Empty;
        }

        static string ShaderDependencyKind(string assetPath)
        {
            var shaderKind = ShaderAssetKind(assetPath);
            if (!string.IsNullOrEmpty(shaderKind))
            {
                return shaderKind;
            }

            return Path.GetExtension(assetPath).Equals(".mat", StringComparison.OrdinalIgnoreCase) ? "material" : string.Empty;
        }

        static CommandExecutionResult RequireShaderReferenceTarget(string argsJson, out string assetPath, out string guid, out string targetKind)
        {
            assetPath = string.Empty;
            guid = string.Empty;
            targetKind = string.Empty;
            var rawAssetPath = JsonUtil.ExtractString(argsJson, "asset_path");
            if (string.IsNullOrEmpty(rawAssetPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument asset_path is required");
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(rawAssetPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must be a Unity-relative path under Assets/ or Packages/");
            }

            targetKind = ShaderAssetKind(assetPath);
            if (string.IsNullOrEmpty(targetKind))
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must reference a .shader, .shadergraph, .shadersubgraph, .compute, .hlsl, or .cginc asset");
            }

            guid = AssetDatabase.AssetPathToGUID(assetPath);
            return string.IsNullOrEmpty(guid) ? CommandExecutionResult.Error("asset_not_found", "asset_path does not exist") : null;
        }

        static string ShaderAssetJson(string assetPath, string guid, string kind)
        {
            var mainAssetType = AssetDatabase.GetMainAssetTypeAtPath(assetPath);
            var typeName = mainAssetType == null ? string.Empty : mainAssetType.FullName;
            return "{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"name\":" + JsonUtil.String(Path.GetFileNameWithoutExtension(assetPath))
                + ",\"kind\":" + JsonUtil.String(kind)
                + ",\"extension\":" + JsonUtil.String(Path.GetExtension(assetPath).ToLowerInvariant())
                + ",\"source\":" + JsonUtil.String(assetPath.StartsWith("Packages/", StringComparison.Ordinal) ? "Packages" : "Assets")
                + ",\"main_asset_type\":" + JsonUtil.String(typeName) + "}";
        }

        static bool IsShaderKindFilter(string kind)
        {
            return kind == "all" || kind == "shader" || kind == "shadergraph" || kind == "shadergraph_subgraph" || kind == "compute" || kind == "hlsl" || kind == "cginc" || kind == "include";
        }

        static bool KindMatches(string actual, string requested)
        {
            if (requested == "all") return true;
            if (requested == "include") return actual == "hlsl" || actual == "cginc";
            return actual == requested;
        }

        static bool MatchesQuery(string assetPath, string guid, string kind, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            return assetPath.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || Path.GetFileNameWithoutExtension(assetPath).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || kind.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0
                || guid.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool AssetDatabaseReferencesTarget(string candidatePath, string targetPath)
        {
            var dependencies = AssetDatabase.GetDependencies(candidatePath, true) ?? new string[0];
            foreach (var dependency in dependencies)
            {
                if (string.Equals(dependency, targetPath, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static bool TextIncludesReferenceTarget(string candidatePath, string targetPath, int maxScanBytes, out int includeLine, out string includeRawReference, out string readError)
        {
            includeLine = 0;
            includeRawReference = string.Empty;
            readError = string.Empty;
            if (!TryResolveAssetFilePath(candidatePath, out var fullPath))
            {
                readError = "asset_file_not_resolved";
                return false;
            }

            var content = ReadUtf8Prefix(fullPath, maxScanBytes, out _, out _, out readError);
            if (!string.IsNullOrEmpty(readError))
            {
                return false;
            }

            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (!TryParseIncludeLine(lines[index], out _, out var rawReference))
                {
                    continue;
                }

                var resolvedPath = ResolveIncludeAssetPath(candidatePath, rawReference);
                if (string.Equals(resolvedPath, targetPath, StringComparison.Ordinal) || string.Equals(rawReference.Replace('\\', '/'), targetPath, StringComparison.Ordinal))
                {
                    includeLine = index + 1;
                    includeRawReference = rawReference;
                    return true;
                }
            }

            return false;
        }

        static bool YamlGuidReferencesTarget(string candidatePath, string targetGuid, int maxScanBytes, out string readError)
        {
            readError = string.Empty;
            if (!TryResolveAssetFilePath(candidatePath, out var fullPath))
            {
                readError = "asset_file_not_resolved";
                return false;
            }

            var content = ReadUtf8Prefix(fullPath, maxScanBytes, out _, out _, out readError);
            return string.IsNullOrEmpty(readError) && content.IndexOf(targetGuid, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool IsYamlGuidScanCandidate(string assetPath)
        {
            var extension = Path.GetExtension(assetPath).ToLowerInvariant();
            return extension == ".mat" || extension == ".prefab" || extension == ".unity" || extension == ".asset" || extension == ".controller" || extension == ".overridecontroller" || extension == ".playable" || extension == ".anim" || extension == ".shadergraph" || extension == ".shadersubgraph";
        }

        static string ReferenceReportEntryJson(string assetPath, List<string> referenceKinds, int includeLine, string includeRawReference, string readError)
        {
            return "{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(assetPath))
                + ",\"name\":" + JsonUtil.String(Path.GetFileNameWithoutExtension(assetPath))
                + ",\"kind\":" + JsonUtil.String(ReferenceCandidateKind(assetPath))
                + ",\"source\":" + JsonUtil.String(assetPath.StartsWith("Packages/", StringComparison.Ordinal) ? "Packages" : "Assets")
                + ",\"reference_kinds\":[" + StringArrayJson(referenceKinds) + "]"
                + ",\"include_line\":" + includeLine
                + ",\"include_raw_reference\":" + JsonUtil.String(includeRawReference)
                + ",\"read_error\":" + JsonUtil.String(readError) + "}";
        }

        static string ReferenceCandidateKind(string assetPath)
        {
            var shaderKind = ShaderDependencyKind(assetPath);
            if (!string.IsNullOrEmpty(shaderKind))
            {
                return shaderKind;
            }

            var extension = Path.GetExtension(assetPath).ToLowerInvariant();
            if (extension == ".prefab") return "prefab";
            if (extension == ".unity") return "scene";
            if (extension == ".asset") return "asset";
            return string.IsNullOrEmpty(extension) ? "asset" : extension.TrimStart('.');
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

        static string ShaderDependenciesJson(string assetPath, bool recursive, int maxDependencies, out int dependencyCount, out bool dependenciesTruncated)
        {
            var builder = new StringBuilder();
            dependencyCount = 0;
            dependenciesTruncated = false;
            var dependencies = AssetDatabase.GetDependencies(assetPath, recursive) ?? new string[0];
            Array.Sort(dependencies, StringComparer.Ordinal);
            foreach (var dependencyPath in dependencies)
            {
                if (string.IsNullOrEmpty(dependencyPath) || string.Equals(dependencyPath, assetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                var kind = ShaderDependencyKind(dependencyPath);
                if (string.IsNullOrEmpty(kind))
                {
                    continue;
                }

                dependencyCount++;
                if (dependencyCount > maxDependencies)
                {
                    dependenciesTruncated = true;
                    break;
                }

                if (dependencyCount > 1)
                {
                    builder.Append(',');
                }

                builder.Append(ShaderAssetJson(dependencyPath, AssetDatabase.AssetPathToGUID(dependencyPath), kind));
            }

            return builder.ToString();
        }

        static string ShaderIncludesJson(string assetPath, string content, int maxIncludeMatches, out int includeCount, out bool includesTruncated)
        {
            var builder = new StringBuilder();
            includeCount = 0;
            includesTruncated = false;
            var lines = content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
            {
                if (!TryParseIncludeLine(lines[index], out var directive, out var rawReference))
                {
                    continue;
                }

                includeCount++;
                if (includeCount > maxIncludeMatches)
                {
                    includesTruncated = true;
                    break;
                }

                if (includeCount > 1)
                {
                    builder.Append(',');
                }

                var resolvedPath = ResolveIncludeAssetPath(assetPath, rawReference);
                var targetExists = !string.IsNullOrEmpty(resolvedPath) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(resolvedPath));
                builder.Append("{\"line\":").Append(index + 1)
                    .Append(",\"directive\":").Append(JsonUtil.String(directive))
                    .Append(",\"raw_reference\":").Append(JsonUtil.String(rawReference))
                    .Append(",\"resolved_asset_path\":").Append(JsonUtil.String(resolvedPath))
                    .Append(",\"target_exists\":").Append(JsonUtil.Bool(targetExists))
                    .Append('}');
            }

            return builder.ToString();
        }

        static bool TryParseIncludeLine(string line, out string directive, out string rawReference)
        {
            directive = string.Empty;
            rawReference = string.Empty;
            var trimmed = (line ?? string.Empty).TrimStart();
            if (trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            if (trimmed.StartsWith("#include_with_pragmas", StringComparison.Ordinal))
            {
                directive = "#include_with_pragmas";
            }
            else if (trimmed.StartsWith("#include", StringComparison.Ordinal))
            {
                directive = "#include";
            }
            else
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

        static string ResolveIncludeAssetPath(string assetPath, string rawReference)
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

        static string ParentAssetPath(string assetPath)
        {
            var index = assetPath.LastIndexOf('/');
            return index <= 0 ? string.Empty : assetPath.Substring(0, index);
        }

        static bool IsTextShaderAsset(string assetPath)
        {
            var kind = ShaderAssetKind(assetPath);
            return kind == "shader" || kind == "compute" || kind == "hlsl" || kind == "cginc";
        }

        static bool TryResolveAssetFilePath(string assetPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
                return File.Exists(fullPath);
            }

            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                var package = PackageManagerPackageInfo.FindForAssetPath(assetPath);
                if (package != null && !string.IsNullOrEmpty(package.resolvedPath))
                {
                    var packageAssetRoot = "Packages/" + package.name;
                    if (assetPath == packageAssetRoot || assetPath.StartsWith(packageAssetRoot + "/", StringComparison.Ordinal))
                    {
                        var relative = assetPath.Length == packageAssetRoot.Length ? string.Empty : assetPath.Substring(packageAssetRoot.Length + 1);
                        fullPath = Path.GetFullPath(Path.Combine(package.resolvedPath, relative.Replace('/', Path.DirectorySeparatorChar)));
                        return File.Exists(fullPath);
                    }
                }
            }

            fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            return File.Exists(fullPath);
        }

        static string ReadUtf8Prefix(string fullPath, int maxBytes, out int byteLength, out bool truncated, out string error)
        {
            byteLength = 0;
            truncated = false;
            error = string.Empty;
            try
            {
                using (var stream = File.OpenRead(fullPath))
                {
                    byteLength = (int)Math.Min(stream.Length, int.MaxValue);
                    var bytesToRead = (int)Math.Min(stream.Length, maxBytes);
                    var buffer = new byte[bytesToRead];
                    var offset = 0;
                    while (offset < bytesToRead)
                    {
                        var read = stream.Read(buffer, offset, bytesToRead - offset);
                        if (read <= 0)
                        {
                            break;
                        }

                        offset += read;
                    }

                    truncated = stream.Length > maxBytes;
                    return new UTF8Encoding(false, true).GetString(buffer, 0, offset);
                }
            }
            catch (DecoderFallbackException)
            {
                error = "invalid_utf8";
            }
            catch (IOException exception)
            {
                error = exception.Message;
            }
            catch (UnauthorizedAccessException exception)
            {
                error = exception.Message;
            }

            return string.Empty;
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
