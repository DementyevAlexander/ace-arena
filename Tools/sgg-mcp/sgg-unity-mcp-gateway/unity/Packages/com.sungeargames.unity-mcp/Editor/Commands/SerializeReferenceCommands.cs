using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class SerializeReferenceCommands
    {
        const string FeatureFlag = "serialize_reference_index";
        const string CacheFileName = "serialize-reference-index.json";
        const string DefaultCacheRelativeDirectory = "Library/SGGUnityMcp";
        const string SettingsAssetIndexCacheDir = "asset_index_cache_dir";
        const string EditorPrefsCacheDirKey = "SGG.UnityMCP.SerializeReferenceIndexCacheDir";
        const string TypeSearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"base_type\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"query\":{\"type\":[\"string\",\"null\"],\"maxLength\":128},\"assembly\":{\"type\":[\"string\",\"null\"],\"maxLength\":128},\"include_abstract\":{\"type\":\"boolean\"},\"include_obsolete\":{\"type\":\"boolean\"},\"include_generics\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"base_type\"],\"additionalProperties\":false}";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string UsedTypesSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":[\"string\",\"null\"],\"maxLength\":128},\"assembly\":{\"type\":[\"string\",\"null\"],\"maxLength\":128},\"include_generic_arguments\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"additionalProperties\":false}";
        const string UsagesFindSchemaJson = "{\"type\":\"object\",\"properties\":{\"type\":{\"type\":\"string\",\"maxLength\":256},\"class\":{\"type\":\"string\",\"maxLength\":256},\"namespace\":{\"type\":[\"string\",\"null\"],\"maxLength\":256},\"assembly\":{\"type\":[\"string\",\"null\"],\"maxLength\":128},\"include_prefab_overrides\":{\"type\":\"boolean\"},\"include_generic_arguments\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"additionalProperties\":false}";
        const string GraphSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":[\"string\",\"null\"],\"maxLength\":128},\"assembly\":{\"type\":[\"string\",\"null\"],\"maxLength\":128},\"include_prefab_overrides\":{\"type\":\"boolean\"},\"include_generic_arguments\":{\"type\":\"boolean\"},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_edges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000}},\"additionalProperties\":false}";
        const string TypeSearchOutputLimitJson = "{\"max_results\":200}";
        const string IndexOutputLimitJson = "{\"max_results\":1000}";
        const string GraphOutputLimitJson = "{\"max_nodes\":5000,\"max_edges\":10000,\"readonly\":true}";

        static readonly object s_lock = new object();
        static readonly List<SerializeReferenceUsage> s_usages = new List<SerializeReferenceUsage>();
        static DateTime s_builtAtUtc = DateTime.MinValue;
        static bool s_loadedFromCache;
        static string s_lastCachePath = string.Empty;

        static readonly Regex SerializeReferenceRegex = new Regex("type:\\s*\\{class:\\s*(?:'(?<quotedClass>(?:[^']|'')*)'|(?<class>[^,]+)),\\s*ns:\\s*(?<namespace>[^,]*),\\s*asm:\\s*(?<assembly>[^\\}]+)\\}", RegexOptions.Compiled);
        static readonly Regex PrefabOverrideSerializeReferenceTypeRegex = new Regex("value:\\s+'?(?<assembly>[^\\s']+)\\s+(?<fullType>.+?)'?\\s*$", RegexOptions.Compiled);

        [McpCommand(
            "serializereference.types.search",
            Title = "Search SerializeReference types",
            Description = "Search loaded serializable types assignable to a SerializeReference base type",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TypeSearchSchemaJson,
            Tags = new string[] { "serializereference", "type", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = TypeSearchOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TypesSearch(McpCommandContext context)
        {
            var baseTypeName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "base_type"), 256);
            if (string.IsNullOrWhiteSpace(baseTypeName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument base_type is required");
            }

            var baseType = ResolveLoadedType(baseTypeName);
            if (baseType == null)
            {
                return CommandExecutionResult.Error("type_not_found", "base_type was not found in loaded Unity Editor types");
            }

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var assembly = Bounded(JsonUtil.ExtractString(context.ArgsJson, "assembly"), 128);
            var includeAbstract = JsonUtil.ExtractBool(context.ArgsJson, "include_abstract", false);
            var includeObsolete = JsonUtil.ExtractBool(context.ArgsJson, "include_obsolete", false);
            var includeGenerics = JsonUtil.ExtractBool(context.ArgsJson, "include_generics", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 200);

            var results = new List<Type>();
            var matched = 0;
            foreach (var type in TypeCache.GetTypesDerivedFrom(baseType))
            {
                if (!IsSerializeReferenceCandidate(type, query, assembly, includeAbstract, includeObsolete, includeGenerics))
                {
                    continue;
                }

                matched++;
                if (results.Count < maxResults)
                {
                    results.Add(type);
                }
            }

            results.Sort(CompareTypes);
            var builder = new StringBuilder();
            for (var index = 0; index < results.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendAssignableType(builder, results[index]);
            }

            return CommandExecutionResult.Ok("{\"base_type\":" + JsonUtil.String(baseType.FullName ?? baseType.Name)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"assembly\":" + JsonUtil.String(assembly)
                + ",\"include_abstract\":" + JsonUtil.Bool(includeAbstract)
                + ",\"include_obsolete\":" + JsonUtil.Bool(includeObsolete)
                + ",\"include_generics\":" + JsonUtil.Bool(includeGenerics)
                + ",\"matched\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(matched > results.Count)
                + ",\"types\":[" + builder + "]}");
        }

        [McpCommand(
            "serializereference.index.status",
            Title = "SerializeReference index status",
            Description = "Read SerializeReference usage index status",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "serializereference", "index" },
            Capability = FeatureFlag,
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult IndexStatus(McpCommandContext context)
        {
            TryLoadCache(context);
            return CommandExecutionResult.Ok(StatusJson(context, false));
        }

        [McpCommand(
            "serializereference.index.rebuild",
            Title = "Rebuild SerializeReference index",
            Description = "Rebuild the project-local SerializeReference usage index",
            Risk = "read",
            Idempotency = "idempotent",
            TimeoutMs = 60000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "serializereference", "index" },
            Capability = FeatureFlag,
            ResultShape = "status",
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult IndexRebuild(McpCommandContext context)
        {
            RebuildIndex(context);
            return CommandExecutionResult.Ok(StatusJson(context, true));
        }

        [McpCommand(
            "serializereference.types.used",
            Title = "Used SerializeReference types",
            Description = "List bounded SerializeReference types found in Unity YAML assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = UsedTypesSchemaJson,
            Tags = new string[] { "serializereference", "type", "index" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = IndexOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TypesUsed(McpCommandContext context)
        {
            EnsureIndex(context);

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var assembly = Bounded(JsonUtil.ExtractString(context.ArgsJson, "assembly"), 128);
            var includeGenericArguments = JsonUtil.ExtractBool(context.ArgsJson, "include_generic_arguments", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 1000);
            var summaries = BuildTypeSummaries();
            summaries.Sort(CompareTypeSummaries);

            var builder = new StringBuilder();
            var matched = 0;
            var written = 0;
            foreach (var summary in summaries)
            {
                if (!includeGenericArguments && summary.GenericArgumentOnly)
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(assembly) && !string.Equals(summary.Type.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!TypeMatches(summary.Type, query))
                {
                    continue;
                }

                matched++;
                if (written >= maxResults)
                {
                    continue;
                }

                if (written > 0)
                {
                    builder.Append(',');
                }

                AppendTypeSummary(builder, summary);
                written++;
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"assembly\":" + JsonUtil.String(assembly)
                + ",\"include_generic_arguments\":" + JsonUtil.Bool(includeGenericArguments)
                + ",\"matched\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(matched > written)
                + ",\"types\":[" + builder + "]}");
        }

        [McpCommand(
            "serializereference.usages.find",
            Title = "Find SerializeReference usages",
            Description = "Find bounded Unity YAML asset usages for one SerializeReference type",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = UsagesFindSchemaJson,
            Tags = new string[] { "serializereference", "usage", "index" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = IndexOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UsagesFind(McpCommandContext context)
        {
            var targetResult = ResolveRequestedType(context.ArgsJson, out var targetType);
            if (targetResult != null)
            {
                return targetResult;
            }

            EnsureIndex(context);

            var includePrefabOverrides = JsonUtil.ExtractBool(context.ArgsJson, "include_prefab_overrides", true);
            var includeGenericArguments = JsonUtil.ExtractBool(context.ArgsJson, "include_generic_arguments", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 1000);
            var usages = UsagesSnapshot();
            var builder = new StringBuilder();
            var matched = 0;
            var written = 0;

            foreach (var usage in usages)
            {
                if (!includePrefabOverrides && usage.Source == "prefab_override")
                {
                    continue;
                }

                if (!UsageMatches(usage, targetType, includeGenericArguments))
                {
                    continue;
                }

                matched++;
                if (written >= maxResults)
                {
                    continue;
                }

                if (written > 0)
                {
                    builder.Append(',');
                }

                AppendUsage(builder, usage);
                written++;
            }

            return CommandExecutionResult.Ok("{\"type\":" + targetType.ToJson()
                + ",\"count\":" + matched
                + ",\"include_prefab_overrides\":" + JsonUtil.Bool(includePrefabOverrides)
                + ",\"include_generic_arguments\":" + JsonUtil.Bool(includeGenericArguments)
                + ",\"truncated\":" + JsonUtil.Bool(matched > written)
                + ",\"index_stale\":false"
                + ",\"files\":[" + builder + "]}");
        }

        [McpCommand(
            "serializereference.graph",
            Title = "Graph SerializeReference usages",
            Description = "Build a bounded graph of Unity YAML assets, SerializeReference types, and generic argument relationships from the project-local index",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = GraphSchemaJson,
            Tags = new string[] { "serializereference", "usage", "graph", "index" },
            Capability = FeatureFlag,
            ResultShape = "graph",
            OutputLimitJson = GraphOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Graph(McpCommandContext context)
        {
            EnsureIndex(context);

            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var assembly = Bounded(JsonUtil.ExtractString(context.ArgsJson, "assembly"), 128);
            var includePrefabOverrides = JsonUtil.ExtractBool(context.ArgsJson, "include_prefab_overrides", true);
            var includeGenericArguments = JsonUtil.ExtractBool(context.ArgsJson, "include_generic_arguments", true);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 1000), 1, 5000);
            var maxEdges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_edges", 3000), 1, 10000);
            var usages = UsagesSnapshot();
            var graph = new SerializeReferenceGraphBuilder(maxNodes, maxEdges, includeGenericArguments);
            var matchedUsages = 0;

            foreach (var usage in usages)
            {
                if (!includePrefabOverrides && usage.Source == "prefab_override")
                {
                    continue;
                }

                if (!GraphUsageMatches(usage, query, assembly, includeGenericArguments))
                {
                    continue;
                }

                matchedUsages++;
                graph.AddUsage(usage);
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"assembly\":" + JsonUtil.String(assembly)
                + ",\"include_prefab_overrides\":" + JsonUtil.Bool(includePrefabOverrides)
                + ",\"include_generic_arguments\":" + JsonUtil.Bool(includeGenericArguments)
                + ",\"matched_usage_count\":" + matchedUsages
                + ",\"node_count\":" + graph.NodeCount
                + ",\"nodes_truncated\":" + JsonUtil.Bool(graph.NodesTruncated)
                + ",\"nodes\":[" + graph.NodesJson + "]"
                + ",\"edge_count\":" + graph.EdgeCount
                + ",\"edges_truncated\":" + JsonUtil.Bool(graph.EdgesTruncated)
                + ",\"edges\":[" + graph.EdgesJson + "]"
                + ",\"index_stale\":false"
                + ",\"selectors_stable\":\"asset nodes use asset_path; type nodes use assembly|namespace|class SerializeReference keys\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This graph is read-only and is built from the existing SerializeReference index; it does not mutate YAML, rename types, retarget asmdefs, import, refresh, or write assets.\"]"
                + ",\"next_valid_actions\":[\"serializereference.types.used\",\"serializereference.usages.find\",\"scriptable_object.schema\",\"scriptable_object.inspect\"]}");
        }

        internal static void InvalidateCache()
        {
            ClearIndex(null);
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
            var usages = new List<SerializeReferenceUsage>();
            foreach (var assetPath in SerializeReferenceAssetPaths())
            {
                try
                {
                    ParseAsset(assetPath, usages);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }

            usages.Sort(CompareUsages);
            var builtAtUtc = DateTime.UtcNow;
            lock (s_lock)
            {
                s_usages.Clear();
                s_usages.AddRange(usages);
                s_builtAtUtc = builtAtUtc;
                s_loadedFromCache = false;
            }

            WriteCache(usages, builtAtUtc, context);
        }

        static void ClearIndex(McpCommandContext context)
        {
            lock (s_lock)
            {
                s_usages.Clear();
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

        static string StatusJson(McpCommandContext context, bool rebuilt)
        {
            bool indexed;
            int usageCount;
            DateTime builtAtUtc;
            bool loadedFromCache;
            lock (s_lock)
            {
                indexed = s_builtAtUtc != DateTime.MinValue;
                usageCount = s_usages.Count;
                builtAtUtc = s_builtAtUtc;
                loadedFromCache = s_loadedFromCache;
            }

            var summaries = indexed ? BuildTypeSummaries() : new List<SerializeReferenceTypeSummary>();
            var fileCount = indexed ? CountIndexedFiles() : 0;
            var cacheLocation = CacheLocation(context);
            return "{\"indexed\":" + JsonUtil.Bool(indexed)
                + ",\"usage_count\":" + usageCount
                + ",\"type_count\":" + summaries.Count
                + ",\"file_count\":" + fileCount
                + ",\"built_at_utc\":" + (indexed ? JsonUtil.String(builtAtUtc.ToString("o")) : "null")
                + ",\"roots\":[\"Assets\"]"
                + ",\"extensions\":[\".unity\",\".prefab\",\".asset\"]"
                + ",\"cache_supported\":true"
                + ",\"cache_configured\":" + JsonUtil.Bool(cacheLocation.Configured)
                + ",\"cache_loaded\":" + JsonUtil.Bool(loadedFromCache)
                + ",\"cache_path\":" + JsonUtil.String(cacheLocation.DisplayPath)
                + ",\"rebuilt\":" + JsonUtil.Bool(rebuilt)
                + ",\"index_stale\":false}";
        }

        static IEnumerable<string> SerializeReferenceAssetPaths()
        {
            var assetsRoot = Path.Combine(ProjectPaths.ProjectRoot, "Assets");
            if (!Directory.Exists(assetsRoot))
            {
                yield break;
            }

            foreach (var fullPath in Directory.EnumerateFiles(assetsRoot, "*.*", SearchOption.AllDirectories))
            {
                if (!IsSerializeReferenceAssetFile(fullPath))
                {
                    continue;
                }

                yield return ToUnityAssetPath(fullPath);
            }
        }

        static bool IsSerializeReferenceAssetFile(string path)
        {
            return path.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".asset", StringComparison.OrdinalIgnoreCase);
        }

        static string ToUnityAssetPath(string fullPath)
        {
            var root = ProjectPaths.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        static void ParseAsset(string assetPath, List<SerializeReferenceUsage> usages)
        {
            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar));
            var insideReferences = false;
            LineInfo previousLine = new LineInfo(string.Empty, 0);
            LineInfo pendingPrefabOverrideValue = null;
            var lineNumber = 0;

            using (var reader = new StreamReader(fullPath))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    lineNumber++;
                    if (pendingPrefabOverrideValue != null)
                    {
                        var combinedValue = pendingPrefabOverrideValue.Text + " " + line.Trim();
                        if (TryParsePrefabOverrideTypeFromString(combinedValue, out var continuedOverrideType))
                        {
                            usages.Add(new SerializeReferenceUsage(assetPath, pendingPrefabOverrideValue.LineNumber, "prefab_override", continuedOverrideType, false));
                            pendingPrefabOverrideValue = null;
                            previousLine = new LineInfo(line.TrimStart(), lineNumber);
                            continue;
                        }

                        pendingPrefabOverrideValue = new LineInfo(combinedValue, pendingPrefabOverrideValue.LineNumber);
                    }

                    if (previousLine.Text.TrimStart().StartsWith("propertyPath: 'managedReferences[", StringComparison.Ordinal))
                    {
                        if (TryParsePrefabOverrideTypeFromString(line, out var overrideType))
                        {
                            usages.Add(new SerializeReferenceUsage(assetPath, lineNumber, "prefab_override", overrideType, false));
                        }
                        else if (line.TrimStart().StartsWith("value:", StringComparison.Ordinal) && IsUnclosedQuotedValue(line))
                        {
                            pendingPrefabOverrideValue = new LineInfo(line.TrimStart(), lineNumber);
                        }
                    }

                    line = line.TrimStart();
                    var currentLine = new LineInfo(line, lineNumber);
                    if (line.StartsWith("---", StringComparison.Ordinal))
                    {
                        insideReferences = false;
                        previousLine = currentLine;
                        continue;
                    }

                    if (!insideReferences && line.StartsWith("references:", StringComparison.Ordinal))
                    {
                        insideReferences = true;
                        previousLine = currentLine;
                        continue;
                    }

                    if (!insideReferences)
                    {
                        previousLine = currentLine;
                        continue;
                    }

                    if (TryParseReferenceType(currentLine, previousLine, out var type, out var typeLineNumber, out var multiLine))
                    {
                        usages.Add(new SerializeReferenceUsage(assetPath, typeLineNumber, "references", type, multiLine));
                    }

                    previousLine = currentLine;
                }
            }
        }

        static bool TryParseReferenceType(LineInfo currentLine, LineInfo previousLine, out SerializeReferenceUnityType type, out int lineNumber, out bool multiLine)
        {
            type = null;
            lineNumber = currentLine.LineNumber;
            multiLine = false;

            var match = SerializeReferenceRegex.Match(currentLine.Text);
            if (!match.Success && previousLine.Text.Contains("type:") && currentLine.Text.Contains("asm:"))
            {
                match = SerializeReferenceRegex.Match(previousLine.Text + currentLine.Text);
                multiLine = true;
                lineNumber = previousLine.LineNumber;
            }

            if (!match.Success)
            {
                return false;
            }

            type = new SerializeReferenceUnityType(
                GetClassName(match).Trim(),
                match.Groups["namespace"].Value.Trim(),
                match.Groups["assembly"].Value.Trim());
            return true;
        }

        static string GetClassName(Match match)
        {
            var quotedClassName = match.Groups["quotedClass"];
            return quotedClassName.Success ? quotedClassName.Value.Replace("''", "'") : match.Groups["class"].Value;
        }

        static bool TryParsePrefabOverrideTypeFromString(string input, out SerializeReferenceUnityType type)
        {
            type = null;
            if (IsUnclosedQuotedValue(input))
            {
                return false;
            }

            var match = PrefabOverrideSerializeReferenceTypeRegex.Match(input);
            if (!match.Success)
            {
                return false;
            }

            var fullType = match.Groups["fullType"].Value.Trim();
            var genericArgumentsIndex = fullType.IndexOf("[[", StringComparison.Ordinal);
            var namespaceSearchEnd = genericArgumentsIndex >= 0 ? genericArgumentsIndex : fullType.Length;
            if (namespaceSearchEnd <= 0)
            {
                return false;
            }

            var lastDot = fullType.LastIndexOf('.', namespaceSearchEnd - 1, namespaceSearchEnd);
            if (lastDot < 0)
            {
                return false;
            }

            type = new SerializeReferenceUnityType(
                fullType.Substring(lastDot + 1).Replace('+', '/'),
                fullType.Substring(0, lastDot),
                match.Groups["assembly"].Value.Trim());
            return true;
        }

        static bool IsUnclosedQuotedValue(string input)
        {
            var firstQuoteIndex = input.IndexOf('\'');
            return firstQuoteIndex >= 0 && input.LastIndexOf('\'') == firstQuoteIndex;
        }

        static CommandExecutionResult ResolveRequestedType(string argsJson, out SerializeReferenceUnityType type)
        {
            type = null;
            var typeText = Bounded(JsonUtil.ExtractString(argsJson, "type"), 256).Trim();
            var className = Bounded(JsonUtil.ExtractString(argsJson, "class"), 256).Trim();
            var namespaceName = Bounded(JsonUtil.ExtractString(argsJson, "namespace"), 256).Trim();
            var assemblyName = Bounded(JsonUtil.ExtractString(argsJson, "assembly"), 128).Trim();
            if (string.IsNullOrEmpty(className) && string.IsNullOrEmpty(typeText))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument type or class is required");
            }

            if (string.IsNullOrEmpty(className))
            {
                ParseTypeText(typeText, ref className, ref namespaceName, ref assemblyName);
            }

            if (string.IsNullOrEmpty(className))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Could not parse SerializeReference type");
            }

            type = new SerializeReferenceUnityType(className.Replace('+', '/'), namespaceName, assemblyName);
            return null;
        }

        static void ParseTypeText(string typeText, ref string className, ref string namespaceName, ref string assemblyName)
        {
            var comma = typeText.LastIndexOf(',');
            if (comma >= 0)
            {
                if (string.IsNullOrEmpty(assemblyName))
                {
                    assemblyName = typeText.Substring(comma + 1).Trim();
                }

                typeText = typeText.Substring(0, comma).Trim();
            }

            var genericArgumentsIndex = typeText.IndexOf("[[", StringComparison.Ordinal);
            var namespaceSearchEnd = genericArgumentsIndex >= 0 ? genericArgumentsIndex : typeText.Length;
            var lastDot = namespaceSearchEnd > 0 ? typeText.LastIndexOf('.', namespaceSearchEnd - 1, namespaceSearchEnd) : -1;
            if (lastDot < 0)
            {
                className = typeText;
                return;
            }

            if (string.IsNullOrEmpty(namespaceName))
            {
                namespaceName = typeText.Substring(0, lastDot);
            }

            className = typeText.Substring(lastDot + 1);
        }

        static bool UsageMatches(SerializeReferenceUsage usage, SerializeReferenceUnityType targetType, bool includeGenericArguments)
        {
            if (usage.Type.IsSameTypeOrNestedInside(targetType))
            {
                return true;
            }

            return includeGenericArguments && usage.Type.ContainsGenericArgument(targetType);
        }

        static List<SerializeReferenceTypeSummary> BuildTypeSummaries()
        {
            var byKey = new Dictionary<string, SerializeReferenceTypeSummary>(StringComparer.Ordinal);
            foreach (var usage in UsagesSnapshot())
            {
                var primary = usage.Type.GetGenericTypeDefinition();
                IncrementSummary(byKey, primary, true);
                foreach (var argument in usage.Type.GetGenericArgumentTypes())
                {
                    IncrementSummary(byKey, argument, false);
                }
            }

            return new List<SerializeReferenceTypeSummary>(byKey.Values);
        }

        static void IncrementSummary(Dictionary<string, SerializeReferenceTypeSummary> byKey, SerializeReferenceUnityType type, bool primary)
        {
            var key = type.Key;
            if (!byKey.TryGetValue(key, out var summary))
            {
                summary = new SerializeReferenceTypeSummary(type);
                byKey[key] = summary;
            }

            if (primary)
            {
                summary.PrimaryCount++;
            }
            else
            {
                summary.GenericArgumentCount++;
            }
        }

        static int CountIndexedFiles()
        {
            var paths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var usage in UsagesSnapshot())
            {
                paths.Add(usage.AssetPath);
            }

            return paths.Count;
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
                var cache = JsonUtility.FromJson<SerializeReferenceIndexCache>(File.ReadAllText(cachePath));
                if (cache == null || cache.schema_version != 1 || cache.usages == null || string.IsNullOrEmpty(cache.built_at_utc))
                {
                    return false;
                }

                if (!DateTime.TryParse(cache.built_at_utc, null, System.Globalization.DateTimeStyles.RoundtripKind, out var builtAtUtc))
                {
                    return false;
                }

                var usages = new List<SerializeReferenceUsage>();
                foreach (var usage in cache.usages)
                {
                    if (usage == null || string.IsNullOrEmpty(usage.asset_path) || usage.type == null)
                    {
                        continue;
                    }

                    usages.Add(new SerializeReferenceUsage(
                        usage.asset_path,
                        Math.Max(1, usage.line),
                        string.IsNullOrEmpty(usage.source) ? "references" : usage.source,
                        new SerializeReferenceUnityType(usage.type.class_name, usage.type.namespace_name, usage.type.assembly_name),
                        usage.multi_line));
                }

                usages.Sort(CompareUsages);
                lock (s_lock)
                {
                    s_usages.Clear();
                    s_usages.AddRange(usages);
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
            catch (ArgumentException)
            {
                return false;
            }
        }

        static void WriteCache(List<SerializeReferenceUsage> usages, DateTime builtAtUtc, McpCommandContext context)
        {
            try
            {
                var cachePath = CacheLocation(context).Path;
                var directory = Path.GetDirectoryName(cachePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var cache = new SerializeReferenceIndexCache
                {
                    schema_version = 1,
                    built_at_utc = builtAtUtc.ToString("o"),
                    usages = CacheUsages(usages),
                };
                File.WriteAllText(cachePath, JsonUtility.ToJson(cache, true));
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        static SerializeReferenceUsageCache[] CacheUsages(List<SerializeReferenceUsage> usages)
        {
            var cacheUsages = new SerializeReferenceUsageCache[usages.Count];
            for (var index = 0; index < usages.Count; index++)
            {
                var usage = usages[index];
                cacheUsages[index] = new SerializeReferenceUsageCache
                {
                    asset_path = usage.AssetPath,
                    line = usage.Line,
                    source = usage.Source,
                    multi_line = usage.MultiLine,
                    type = new SerializeReferenceTypeCache
                    {
                        class_name = usage.Type.ClassName,
                        namespace_name = usage.Type.Namespace,
                        assembly_name = usage.Type.AssemblyName,
                    },
                };
            }

            return cacheUsages;
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

        static List<SerializeReferenceUsage> UsagesSnapshot()
        {
            lock (s_lock)
            {
                return new List<SerializeReferenceUsage>(s_usages);
            }
        }

        static bool GraphUsageMatches(SerializeReferenceUsage usage, string query, string assembly, bool includeGenericArguments)
        {
            if (!string.IsNullOrEmpty(assembly) && !string.Equals(usage.Type.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase))
            {
                if (!includeGenericArguments)
                {
                    return false;
                }

                var assemblyMatched = false;
                foreach (var argument in usage.Type.GetGenericArgumentTypes())
                {
                    if (string.Equals(argument.AssemblyName, assembly, StringComparison.OrdinalIgnoreCase))
                    {
                        assemblyMatched = true;
                        break;
                    }
                }

                if (!assemblyMatched)
                {
                    return false;
                }
            }

            if (TypeMatches(usage.Type, query))
            {
                return true;
            }

            if (!includeGenericArguments)
            {
                return false;
            }

            foreach (var argument in usage.Type.GetGenericArgumentTypes())
            {
                if (TypeMatches(argument, query))
                {
                    return true;
                }
            }

            return false;
        }

        static bool IsSerializeReferenceCandidate(Type type, string query, string assembly, bool includeAbstract, bool includeObsolete, bool includeGenerics)
        {
            if (type == null || type.IsInterface || (!includeAbstract && type.IsAbstract))
            {
                return false;
            }

            if (!includeGenerics && type.ContainsGenericParameters)
            {
                return false;
            }

            if (!includeObsolete && IsObsolete(type))
            {
                return false;
            }

            if (typeof(UnityEngine.Object).IsAssignableFrom(type))
            {
                return false;
            }

            if (type.GetCustomAttribute<SerializableAttribute>() == null)
            {
                return false;
            }

            if (!string.IsNullOrEmpty(assembly) && !string.Equals(type.Assembly.GetName().Name, assembly, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return TypeMatches(type, query);
        }

        static Type ResolveLoadedType(string typeName)
        {
            var direct = Type.GetType(typeName, false, true);
            if (direct != null)
            {
                return direct;
            }

            Type nameMatch = null;
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null)
                    {
                        continue;
                    }

                    if (string.Equals(type.FullName, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        return type;
                    }

                    if (nameMatch == null && string.Equals(type.Name, typeName, StringComparison.OrdinalIgnoreCase))
                    {
                        nameMatch = type;
                    }
                }
            }

            return nameMatch;
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

        static bool TypeMatches(Type type, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            return Contains(type.Name, query)
                || Contains(type.FullName ?? string.Empty, query)
                || Contains(type.Namespace ?? string.Empty, query)
                || Contains(type.Assembly.GetName().Name, query);
        }

        static bool TypeMatches(SerializeReferenceUnityType type, string query)
        {
            if (string.IsNullOrEmpty(query))
            {
                return true;
            }

            return Contains(type.ClassName, query)
                || Contains(type.Namespace, query)
                || Contains(type.AssemblyName, query)
                || Contains(type.FullTypeName, query)
                || Contains(type.DisplayName, query);
        }

        static bool Contains(string value, string query)
        {
            return string.IsNullOrEmpty(query) || (!string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static bool IsObsolete(Type type)
        {
            return type.GetCustomAttribute<ObsoleteAttribute>() != null;
        }

        static void AppendAssignableType(StringBuilder builder, Type type)
        {
            builder.Append("{\"full_name\":").Append(JsonUtil.String(type.FullName ?? type.Name));
            builder.Append(",\"namespace\":").Append(JsonUtil.String(type.Namespace ?? string.Empty));
            builder.Append(",\"class_name\":").Append(JsonUtil.String(type.Name));
            builder.Append(",\"assembly\":").Append(JsonUtil.String(type.Assembly.GetName().Name));
            builder.Append(",\"is_generic\":").Append(JsonUtil.Bool(type.IsGenericType || type.ContainsGenericParameters));
            builder.Append(",\"is_nested\":").Append(JsonUtil.Bool(type.IsNested));
            builder.Append(",\"is_abstract\":").Append(JsonUtil.Bool(type.IsAbstract));
            builder.Append(",\"is_obsolete\":").Append(JsonUtil.Bool(IsObsolete(type)));
            builder.Append(",\"display_name\":").Append(JsonUtil.String(DisplayName(type))).Append('}');
        }

        static void AppendTypeSummary(StringBuilder builder, SerializeReferenceTypeSummary summary)
        {
            builder.Append("{\"type\":").Append(summary.Type.ToJson());
            builder.Append(",\"primary_count\":").Append(summary.PrimaryCount);
            builder.Append(",\"generic_argument_count\":").Append(summary.GenericArgumentCount);
            builder.Append(",\"total_count\":").Append(summary.TotalCount);
            builder.Append(",\"generic_argument_only\":").Append(JsonUtil.Bool(summary.GenericArgumentOnly));
            builder.Append('}');
        }

        static void AppendUsage(StringBuilder builder, SerializeReferenceUsage usage)
        {
            builder.Append("{\"asset_path\":").Append(JsonUtil.String(usage.AssetPath));
            builder.Append(",\"line\":").Append(usage.Line);
            builder.Append(",\"source\":").Append(JsonUtil.String(usage.Source));
            builder.Append(",\"multi_line\":").Append(JsonUtil.Bool(usage.MultiLine));
            builder.Append(",\"type\":").Append(usage.Type.ToJson()).Append('}');
        }

        static string DisplayName(Type type)
        {
            return string.IsNullOrEmpty(type.Namespace) ? type.Name : type.Namespace + "." + type.Name.Replace('+', '.');
        }

        static string Bounded(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value.Trim();
            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static int CompareTypes(Type left, Type right)
        {
            var byType = string.Compare(left.FullName ?? left.Name, right.FullName ?? right.Name, StringComparison.Ordinal);
            return byType != 0 ? byType : string.Compare(left.Assembly.GetName().Name, right.Assembly.GetName().Name, StringComparison.Ordinal);
        }

        static int CompareUsages(SerializeReferenceUsage left, SerializeReferenceUsage right)
        {
            var byPath = string.Compare(left.AssetPath, right.AssetPath, StringComparison.Ordinal);
            if (byPath != 0)
            {
                return byPath;
            }

            var byLine = left.Line.CompareTo(right.Line);
            if (byLine != 0)
            {
                return byLine;
            }

            return string.Compare(left.Type.Key, right.Type.Key, StringComparison.Ordinal);
        }

        static int CompareTypeSummaries(SerializeReferenceTypeSummary left, SerializeReferenceTypeSummary right)
        {
            var byCount = right.TotalCount.CompareTo(left.TotalCount);
            if (byCount != 0)
            {
                return byCount;
            }

            return string.Compare(left.Type.Key, right.Type.Key, StringComparison.Ordinal);
        }

        sealed class LineInfo
        {
            public LineInfo(string text, int lineNumber)
            {
                Text = text ?? string.Empty;
                LineNumber = lineNumber;
            }

            public string Text { get; private set; }
            public int LineNumber { get; private set; }
        }

        sealed class SerializeReferenceUsage
        {
            public SerializeReferenceUsage(string assetPath, int line, string source, SerializeReferenceUnityType type, bool multiLine)
            {
                AssetPath = assetPath ?? string.Empty;
                Line = line;
                Source = source ?? string.Empty;
                Type = type;
                MultiLine = multiLine;
            }

            public string AssetPath { get; private set; }
            public int Line { get; private set; }
            public string Source { get; private set; }
            public SerializeReferenceUnityType Type { get; private set; }
            public bool MultiLine { get; private set; }
        }

        sealed class SerializeReferenceUnityType
        {
            const string GenericArgumentsStart = "[[";
            const string GenericArgumentsEnd = "]]";

            public SerializeReferenceUnityType(string className, string namespaceName, string assemblyName)
            {
                ClassName = className ?? string.Empty;
                Namespace = namespaceName ?? string.Empty;
                AssemblyName = assemblyName ?? string.Empty;
            }

            public string ClassName { get; private set; }
            public string Namespace { get; private set; }
            public string AssemblyName { get; private set; }
            public string FullTypeName => string.IsNullOrEmpty(Namespace) ? ClassName : Namespace + "." + ClassName;
            public string DisplayName => FullTypeName.Replace('/', '.');
            public string Key => AssemblyName + "|" + Namespace + "|" + GetGenericTypeDefinitionClassName();

            public string ToJson()
            {
                return "{\"class\":" + JsonUtil.String(ClassName)
                    + ",\"namespace\":" + JsonUtil.String(Namespace)
                    + ",\"asm\":" + JsonUtil.String(AssemblyName)
                    + ",\"full_name\":" + JsonUtil.String(FullTypeName)
                    + ",\"display_name\":" + JsonUtil.String(DisplayName) + "}";
            }

            public SerializeReferenceUnityType GetGenericTypeDefinition()
            {
                return new SerializeReferenceUnityType(GetGenericTypeDefinitionClassName(), Namespace, AssemblyName);
            }

            public bool IsSameTypeOrNestedInside(SerializeReferenceUnityType parentType)
            {
                return MatchesAssembly(parentType.AssemblyName)
                    && MatchesNamespace(parentType.Namespace)
                    && IsSameClassPathOrNestedInside(GetGenericTypeDefinitionClassName(), parentType.GetGenericTypeDefinitionClassName());
            }

            public bool ContainsGenericArgument(SerializeReferenceUnityType targetType)
            {
                foreach (var argument in GetGenericArgumentTypes())
                {
                    if (argument.IsSameTypeOrNestedInside(targetType))
                    {
                        return true;
                    }
                }

                return false;
            }

            public List<SerializeReferenceUnityType> GetGenericArgumentTypes()
            {
                var genericArguments = GetGenericArgumentTypeStrings();
                var types = new List<SerializeReferenceUnityType>();
                foreach (var genericArgument in genericArguments)
                {
                    if (TryParseGenericArgumentType(genericArgument, out var type))
                    {
                        types.Add(type);
                    }
                }

                return types;
            }

            bool MatchesAssembly(string targetAssembly)
            {
                return string.IsNullOrEmpty(targetAssembly) || string.Equals(AssemblyName, targetAssembly, StringComparison.OrdinalIgnoreCase);
            }

            bool MatchesNamespace(string targetNamespace)
            {
                return string.IsNullOrEmpty(targetNamespace) || string.Equals(Namespace, targetNamespace, StringComparison.Ordinal);
            }

            string GetGenericTypeDefinitionClassName()
            {
                var genericArgumentsIndex = ClassName.IndexOf(GenericArgumentsStart, StringComparison.Ordinal);
                return genericArgumentsIndex < 0 ? ClassName : ClassName.Substring(0, genericArgumentsIndex);
            }

            List<string> GetGenericArgumentTypeStrings()
            {
                var genericArgumentsIndex = ClassName.IndexOf(GenericArgumentsStart, StringComparison.Ordinal);
                if (genericArgumentsIndex < 0 || !ClassName.EndsWith(GenericArgumentsEnd, StringComparison.Ordinal))
                {
                    return new List<string>();
                }

                var start = genericArgumentsIndex + GenericArgumentsStart.Length;
                var argumentsText = ClassName.Substring(start, ClassName.Length - start - GenericArgumentsEnd.Length);
                return new List<string>(argumentsText.Split(new[] { "],[" }, StringSplitOptions.None));
            }

            static bool TryParseGenericArgumentType(string genericArgument, out SerializeReferenceUnityType type)
            {
                type = null;
                genericArgument = genericArgument.Trim('[', ']');
                var separatorIndex = genericArgument.LastIndexOf(',');
                if (separatorIndex < 0)
                {
                    return false;
                }

                var fullTypeName = genericArgument.Substring(0, separatorIndex).Trim();
                var assemblyName = genericArgument.Substring(separatorIndex + 1).Trim();
                var nestedTypeSeparatorIndex = fullTypeName.IndexOf('+');
                var namespaceSearchEnd = nestedTypeSeparatorIndex >= 0 ? nestedTypeSeparatorIndex : fullTypeName.Length;
                var lastDot = namespaceSearchEnd > 0 ? fullTypeName.LastIndexOf('.', namespaceSearchEnd - 1, namespaceSearchEnd) : -1;
                var namespaceName = string.Empty;
                var className = fullTypeName;
                if (lastDot > 0)
                {
                    namespaceName = fullTypeName.Substring(0, lastDot);
                    className = fullTypeName.Substring(lastDot + 1);
                }

                type = new SerializeReferenceUnityType(className.Replace('+', '/'), namespaceName, assemblyName);
                return true;
            }

            static bool IsSameClassPathOrNestedInside(string className, string parentClassName)
            {
                return className == parentClassName || className.StartsWith(parentClassName + "/", StringComparison.Ordinal);
            }
        }

        sealed class SerializeReferenceTypeSummary
        {
            public SerializeReferenceTypeSummary(SerializeReferenceUnityType type)
            {
                Type = type;
            }

            public SerializeReferenceUnityType Type { get; private set; }
            public int PrimaryCount { get; set; }
            public int GenericArgumentCount { get; set; }
            public int TotalCount => PrimaryCount + GenericArgumentCount;
            public bool GenericArgumentOnly => PrimaryCount == 0 && GenericArgumentCount > 0;
        }

        sealed class SerializeReferenceGraphBuilder
        {
            readonly int MaxNodes;
            readonly int MaxEdges;
            readonly bool IncludeGenericArguments;
            readonly HashSet<string> SeenNodes = new HashSet<string>(StringComparer.Ordinal);

            public SerializeReferenceGraphBuilder(int maxNodes, int maxEdges, bool includeGenericArguments)
            {
                MaxNodes = maxNodes;
                MaxEdges = maxEdges;
                IncludeGenericArguments = includeGenericArguments;
            }

            public int NodeCount { get; private set; }
            public bool NodesTruncated { get; private set; }
            public StringBuilder NodesJson { get; } = new StringBuilder();
            public int EdgeCount { get; private set; }
            public bool EdgesTruncated { get; private set; }
            public StringBuilder EdgesJson { get; } = new StringBuilder();

            public void AddUsage(SerializeReferenceUsage usage)
            {
                var assetNodeId = "asset:" + usage.AssetPath;
                var typeNodeId = TypeNodeId(usage.Type);
                AddNode(assetNodeId, "asset", usage.AssetPath, "{\"asset_path\":" + JsonUtil.String(usage.AssetPath) + "}");
                AddTypeNode(usage.Type, false);
                AddEdge(assetNodeId, typeNodeId, "uses_serialize_reference", "{\"asset_path\":" + JsonUtil.String(usage.AssetPath)
                    + ",\"line\":" + usage.Line
                    + ",\"source\":" + JsonUtil.String(usage.Source)
                    + ",\"multi_line\":" + JsonUtil.Bool(usage.MultiLine) + "}");

                if (!IncludeGenericArguments)
                {
                    return;
                }

                foreach (var argument in usage.Type.GetGenericArgumentTypes())
                {
                    var argumentNodeId = TypeNodeId(argument);
                    AddTypeNode(argument, true);
                    AddEdge(typeNodeId, argumentNodeId, "has_generic_argument", "{\"parent_type\":" + usage.Type.ToJson() + ",\"argument_type\":" + argument.ToJson() + "}");
                }
            }

            void AddTypeNode(SerializeReferenceUnityType type, bool genericArgument)
            {
                AddNode(TypeNodeId(type), genericArgument ? "generic_argument_type" : "serialize_reference_type", type.DisplayName, "{\"type\":" + type.ToJson() + ",\"generic_argument\":" + JsonUtil.Bool(genericArgument) + "}");
            }

            bool AddNode(string id, string kind, string label, string selectorJson)
            {
                if (SeenNodes.Contains(id))
                {
                    return true;
                }

                NodeCount++;
                if (NodeCount > MaxNodes)
                {
                    NodesTruncated = true;
                    return false;
                }

                SeenNodes.Add(id);
                if (NodesJson.Length > 0)
                {
                    NodesJson.Append(',');
                }

                NodesJson.Append("{\"id\":").Append(JsonUtil.String(id))
                    .Append(",\"kind\":").Append(JsonUtil.String(kind))
                    .Append(",\"label\":").Append(JsonUtil.String(label))
                    .Append(",\"selector\":").Append(selectorJson)
                    .Append('}');
                return true;
            }

            void AddEdge(string sourceId, string targetId, string kind, string detailsJson)
            {
                EdgeCount++;
                if (EdgeCount > MaxEdges)
                {
                    EdgesTruncated = true;
                    return;
                }

                if (EdgesJson.Length > 0)
                {
                    EdgesJson.Append(',');
                }

                EdgesJson.Append("{\"selector\":{\"edge_index\":").Append(EdgeCount - 1)
                    .Append(",\"source_id\":").Append(JsonUtil.String(sourceId))
                    .Append(",\"target_id\":").Append(JsonUtil.String(targetId))
                    .Append(",\"kind\":").Append(JsonUtil.String(kind)).Append('}')
                    .Append(",\"source_id\":").Append(JsonUtil.String(sourceId))
                    .Append(",\"target_id\":").Append(JsonUtil.String(targetId))
                    .Append(",\"kind\":").Append(JsonUtil.String(kind))
                    .Append(",\"details\":").Append(detailsJson)
                    .Append('}');
            }

            static string TypeNodeId(SerializeReferenceUnityType type)
            {
                return "type:" + type.Key;
            }
        }

        sealed class CacheLocationInfo
        {
            public bool Configured { get; set; }
            public string DisplayPath { get; set; }
            public string Path { get; set; }
        }

        [Serializable]
        sealed class SerializeReferenceIndexCache
        {
            public int schema_version;
            public string built_at_utc;
            public SerializeReferenceUsageCache[] usages;
        }

        [Serializable]
        sealed class SerializeReferenceUsageCache
        {
            public string asset_path;
            public int line;
            public string source;
            public bool multi_line;
            public SerializeReferenceTypeCache type;
        }

        [Serializable]
        sealed class SerializeReferenceTypeCache
        {
            public string class_name;
            public string namespace_name;
            public string assembly_name;
        }
    }
}
