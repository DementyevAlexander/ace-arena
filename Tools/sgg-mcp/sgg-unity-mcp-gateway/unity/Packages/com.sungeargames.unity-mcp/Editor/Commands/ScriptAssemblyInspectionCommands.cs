using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor.PackageManager;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ScriptAssemblyInspectionCommands
    {
        const string FeatureFlag = "unity_api_reflection";
        const string ResolveSchemaJson = "{\"type\":\"object\",\"properties\":{\"script_path\":{\"type\":\"string\"},\"type_name\":{\"type\":\"string\",\"maxLength\":256},\"max_candidates\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50}},\"anyOf\":[{\"required\":[\"script_path\"]},{\"required\":[\"type_name\"]}],\"additionalProperties\":false}";
        const string AssemblyListSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string AssemblySourcesListSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"max_assemblies\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_sources_per_assembly\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string AssemblyReferencesGraphSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_edges\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000}},\"additionalProperties\":false}";
        const string CsprojGeneratedListSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string CsprojGeneratedInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"csproj_path\":{\"type\":\"string\",\"maxLength\":256},\"project_name\":{\"type\":\"string\",\"maxLength\":256},\"max_items\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"anyOf\":[{\"required\":[\"csproj_path\"]},{\"required\":[\"project_name\"]}],\"additionalProperties\":false}";
        const string CsprojGeneratedCompareSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"include_packages\":{\"type\":\"boolean\"},\"max_projects\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_differences_per_project\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string ScriptRelocationPreflightSchemaJson = "{\"type\":\"object\",\"properties\":{\"source_script_path\":{\"type\":\"string\",\"maxLength\":512},\"target_script_path\":{\"type\":\"string\",\"maxLength\":512},\"max_declared_types\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":50}},\"required\":[\"source_script_path\",\"target_script_path\"],\"additionalProperties\":false}";
        const string AssemblyInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"assembly_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"asmdef_path\":{\"type\":\"string\",\"maxLength\":512},\"include_types\":{\"type\":\"boolean\"},\"max_types\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"anyOf\":[{\"required\":[\"assembly_name\"]},{\"required\":[\"asmdef_path\"]}],\"additionalProperties\":false}";
        const string ReferencesExplainSchemaJson = "{\"type\":\"object\",\"properties\":{\"source_script_path\":{\"type\":\"string\",\"maxLength\":512},\"source_assembly_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"source_asmdef_path\":{\"type\":\"string\",\"maxLength\":512},\"target_script_path\":{\"type\":\"string\",\"maxLength\":512},\"target_assembly_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"target_asmdef_path\":{\"type\":\"string\",\"maxLength\":512}},\"anyOf\":[{\"required\":[\"source_script_path\"]},{\"required\":[\"source_assembly_name\"]},{\"required\":[\"source_asmdef_path\"]}],\"allOf\":[{\"anyOf\":[{\"required\":[\"target_script_path\"]},{\"required\":[\"target_assembly_name\"]},{\"required\":[\"target_asmdef_path\"]}]}],\"additionalProperties\":false}";
        const string ReferencesTraceSchemaJson = "{\"type\":\"object\",\"properties\":{\"source_script_path\":{\"type\":\"string\",\"maxLength\":512},\"source_assembly_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"source_asmdef_path\":{\"type\":\"string\",\"maxLength\":512},\"target_script_path\":{\"type\":\"string\",\"maxLength\":512},\"target_assembly_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"target_asmdef_path\":{\"type\":\"string\",\"maxLength\":512},\"max_types\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_evidence\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"max_source_hints\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"max_source_files\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000}},\"anyOf\":[{\"required\":[\"source_script_path\"]},{\"required\":[\"source_assembly_name\"]},{\"required\":[\"source_asmdef_path\"]}],\"allOf\":[{\"anyOf\":[{\"required\":[\"target_script_path\"]},{\"required\":[\"target_assembly_name\"]},{\"required\":[\"target_asmdef_path\"]}]}],\"additionalProperties\":false}";
        const string TypeResolveSchemaJson = "{\"type\":\"object\",\"properties\":{\"type_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":256},\"include_scripts\":{\"type\":\"boolean\"},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":50},\"max_scan_files\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"required\":[\"type_name\"],\"additionalProperties\":false}";
        const string OutputLimitJson = "{\"max_candidates\":50,\"max_script_bytes\":262144,\"max_asmdef_bytes\":32768}";
        const string AssemblyListOutputLimitJson = "{\"max_results\":200,\"max_asmdef_files\":500,\"max_asmdef_bytes\":32768}";
        const string AssemblySourcesListOutputLimitJson = "{\"max_assemblies\":200,\"max_sources_per_assembly\":500}";
        const string AssemblyReferencesGraphOutputLimitJson = "{\"max_nodes\":200,\"max_edges\":1000,\"max_asmdef_files\":500,\"max_asmdef_bytes\":32768}";
        const string CsprojGeneratedListOutputLimitJson = "{\"max_results\":200,\"max_csproj_files\":200,\"project_root_only\":true}";
        const string CsprojGeneratedInspectOutputLimitJson = "{\"max_items_per_group\":500,\"max_csproj_bytes\":524288,\"project_root_only\":true}";
        const string CsprojGeneratedCompareOutputLimitJson = "{\"max_projects\":200,\"max_differences_per_project\":500,\"max_csproj_files\":200,\"max_csproj_bytes\":524288,\"project_root_only\":true}";
        const string ScriptRelocationPreflightOutputLimitJson = "{\"max_declared_types\":50,\"max_script_bytes\":262144,\"dry_run_only\":true}";
        const string AssemblyInspectOutputLimitJson = "{\"max_types\":200,\"max_referenced_assemblies\":100,\"max_asmdef_bytes\":32768}";
        const string ReferencesExplainOutputLimitJson = "{\"max_asmdef_files\":500,\"max_asmdef_bytes\":32768}";
        const string ReferencesTraceOutputLimitJson = "{\"max_types\":5000,\"max_evidence\":200,\"max_source_hints\":100,\"max_source_files\":1000,\"max_asmdef_files\":500,\"metadata_only\":true}";
        const string TypeResolveOutputLimitJson = "{\"max_results\":50,\"max_scan_files\":1000,\"max_script_bytes\":65536}";
        const int MaxScriptBytes = 262144;
        const int MaxAsmdefBytes = 32768;
        const int MaxAsmdefFiles = 500;
        const int MaxCsprojFiles = 200;
        const int MaxCsprojBytes = 524288;
        const int MaxCsprojCompareItems = 5000;
        const int MaxReferencedAssemblies = 100;
        const int MaxReferenceTraceTypes = 5000;
        const int MaxReferenceTraceEvidence = 200;
        const int MaxReferenceTraceGraphEdges = 20;
        const int MaxReferenceTraceSourceHints = 100;
        const int MaxReferenceTraceSourceFiles = 1000;
        const int MaxTypeResolveScriptFiles = 1000;
        const int MaxTypeResolveScriptBytes = 65536;

        [McpCommand(
            "script.assembly.resolve",
            Title = "Resolve script assembly",
            Description = "Resolve the likely Unity assembly for a script path or type without compiling, importing, or writing assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ResolveSchemaJson,
            Tags = new string[] { "script", "assembly", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Resolve(McpCommandContext context)
        {
            var rawScriptPath = JsonUtil.ExtractString(context.ArgsJson, "script_path");
            var typeName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "type_name"), 256);
            if (string.IsNullOrWhiteSpace(rawScriptPath) && string.IsNullOrWhiteSpace(typeName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument script_path or type_name is required");
            }

            var maxCandidates = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_candidates", 20), 1, 50);
            var scriptPath = string.Empty;
            var underEditorFolder = false;
            AssemblyPrediction prediction = null;
            var declaredTypes = new List<DeclaredType>();

            if (!string.IsNullOrWhiteSpace(rawScriptPath))
            {
                scriptPath = PathGuard.RequireUnityAssetPath(rawScriptPath);
                if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "script_path must point to a .cs file under Assets/ or Packages/");
                }

                underEditorFolder = IsUnderEditorFolder(scriptPath);
                prediction = PredictAssembly(scriptPath, underEditorFolder);
                if (!TryResolveUnityPath(scriptPath, true, out var fullPath))
                {
                    return CommandExecutionResult.Error("asset_not_found", "script_path does not exist");
                }

                declaredTypes = ExtractDeclaredTypes(ReadUtf8Prefix(fullPath, MaxScriptBytes));
            }

            var loadedTypes = FindLoadedTypes(typeName, declaredTypes, maxCandidates, out var matchedLoadedTypes);
            var warnings = BuildWarnings(prediction, loadedTypes, matchedLoadedTypes);
            var json = new StringBuilder();
            json.Append('{');
            if (!string.IsNullOrEmpty(scriptPath))
            {
                json.Append("\"script_path\":").Append(JsonUtil.String(scriptPath)).Append(',');
            }

            json.Append("\"type_name\":").Append(JsonUtil.String(typeName));
            json.Append(",\"max_candidates\":").Append(maxCandidates);
            json.Append(",\"under_editor_folder\":").Append(JsonUtil.Bool(underEditorFolder));
            if (prediction != null)
            {
                json.Append(",\"assembly\":").Append(prediction.ToJson());
            }

            json.Append(",\"declared_type_candidates\":[").Append(DeclaredTypesJson(declaredTypes, maxCandidates)).Append(']');
            json.Append(",\"loaded_type_candidates\":[").Append(string.Join(",", loadedTypes.ToArray())).Append(']');
            json.Append(",\"loaded_type_matches\":").Append(matchedLoadedTypes);
            json.Append(",\"loaded_type_candidates_truncated\":").Append(JsonUtil.Bool(matchedLoadedTypes > loadedTypes.Count));
            json.Append(",\"warnings\":[").Append(WarningsJson(warnings)).Append("]}");
            return CommandExecutionResult.Ok(json.ToString());
        }

        [McpCommand(
            "assembly.list",
            Title = "List Unity script assemblies",
            Description = "List generated and asmdef script assemblies for local assembly ownership reasoning",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = AssemblyListSchemaJson,
            Tags = new string[] { "script", "assembly", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = AssemblyListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ListAssemblies(McpCommandContext context)
        {
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 200);
            var results = new List<string>();
            var matched = 0;
            var scannedAsmdefs = 0;
            var scanTruncated = false;

            AddAssemblyListResult(results, query, maxResults, ref matched, new AssemblyListEntry(string.Empty, "Assembly-CSharp", "auto", true, false, "runtime_folder_without_asmdef"));
            AddAssemblyListResult(results, query, maxResults, ref matched, new AssemblyListEntry(string.Empty, "Assembly-CSharp-Editor", "auto", true, true, "editor_folder_without_asmdef"));
            foreach (var entry in FindAsmdefAssemblyEntries(includePackages, out scannedAsmdefs, out scanTruncated))
            {
                AddAssemblyListResult(results, query, maxResults, ref matched, entry);
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"max_results\":" + maxResults
                + ",\"max_asmdef_files\":" + MaxAsmdefFiles
                + ",\"scanned_asmdef_files\":" + scannedAsmdefs
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"matched\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(matched > results.Count)
                + ",\"results\":[" + string.Join(",", results.ToArray()) + "]}");
        }

        [McpCommand(
            "assembly.sources.list",
            Title = "List Unity assembly source files",
            Description = "List .cs source files owned by current Unity compilation assemblies using CompilationPipeline metadata",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = AssemblySourcesListSchemaJson,
            Tags = new string[] { "script", "assembly", "unity-api", "source-inventory" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = AssemblySourcesListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ListAssemblySources(McpCommandContext context)
        {
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var maxAssemblies = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_assemblies", 100), 1, 200);
            var maxSourcesPerAssembly = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_sources_per_assembly", 200), 0, 500);
            var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
            Array.Sort(assemblies, CompareCompilationAssemblies);

            var results = new List<string>();
            var matchedAssemblies = 0;
            foreach (var assembly in assemblies)
            {
                var sources = NormalizeCompilationSourceFiles(assembly.sourceFiles);
                if (!includePackages && SourcesArePackagesOnly(sources))
                {
                    continue;
                }

                if (!AssemblySourcesMatch(assembly.name, sources, query))
                {
                    continue;
                }

                matchedAssemblies++;
                if (results.Count < maxAssemblies)
                {
                    results.Add(AssemblySourcesJson(assembly, sources, maxSourcesPerAssembly));
                }
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"max_assemblies\":" + maxAssemblies
                + ",\"max_sources_per_assembly\":" + maxSourcesPerAssembly
                + ",\"compilation_assemblies\":" + assemblies.Length
                + ",\"matched_assemblies\":" + matchedAssemblies
                + ",\"returned_assemblies\":" + results.Count
                + ",\"assemblies_truncated\":" + JsonUtil.Bool(matchedAssemblies > results.Count)
                + ",\"results\":[" + string.Join(",", results.ToArray()) + "]"
                + ",\"warnings\":[\"CompilationPipeline.GetAssemblies().sourceFiles is authoritative for current Unity script ownership.\",\"Generated .csproj files are IDE diagnostics and may be stale.\",\"using directives do not add assembly references; asmdef references control cross-assembly access.\"]}");
        }

        [McpCommand(
            "assembly.references.graph",
            Title = "Read Unity assembly reference graph",
            Description = "Read bounded local assembly reference graph edges from predefined assemblies and asmdef references",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = AssemblyReferencesGraphSchemaJson,
            Tags = new string[] { "script", "assembly", "unity-api", "reference-graph" },
            Capability = FeatureFlag,
            ResultShape = "graph",
            OutputLimitJson = AssemblyReferencesGraphOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ReadAssemblyReferenceGraph(McpCommandContext context)
        {
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 100), 1, 200);
            var maxEdges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_edges", 500), 0, 1000);
            var nodes = BuildAssemblyGraphNodes(includePackages, out var scannedAsmdefs, out var scanTruncated);
            var allEdges = BuildAssemblyGraphEdges(nodes);
            var candidateNodes = SelectAssemblyGraphNodes(nodes, allEdges, query);
            var returnedNodes = new List<AssemblyGraphNode>();
            foreach (var node in candidateNodes)
            {
                if (returnedNodes.Count < maxNodes)
                {
                    returnedNodes.Add(node);
                }
            }

            var returnedEdges = new List<AssemblyGraphEdge>();
            var matchedEdges = 0;
            foreach (var edge in allEdges)
            {
                if (!AssemblyGraphEdgeMatches(edge, returnedNodes, query))
                {
                    continue;
                }

                matchedEdges++;
                if (returnedEdges.Count < maxEdges)
                {
                    returnedEdges.Add(edge);
                }
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"max_nodes\":" + maxNodes
                + ",\"max_edges\":" + maxEdges
                + ",\"scanned_asmdef_files\":" + scannedAsmdefs
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"matched_nodes\":" + candidateNodes.Count
                + ",\"returned_nodes\":" + returnedNodes.Count
                + ",\"nodes_truncated\":" + JsonUtil.Bool(candidateNodes.Count > returnedNodes.Count)
                + ",\"matched_edges\":" + matchedEdges
                + ",\"returned_edges\":" + returnedEdges.Count
                + ",\"edges_truncated\":" + JsonUtil.Bool(matchedEdges > returnedEdges.Count)
                + ",\"nodes\":[" + AssemblyGraphNodesJson(returnedNodes) + "]"
                + ",\"edges\":[" + AssemblyGraphEdgesJson(returnedEdges) + "]"
                + ",\"warnings\":[\"using directives do not add assembly references; asmdef references control cross-assembly access.\",\"Platform, define constraints, versionDefines, and optional Unity references are reported by assembly.inspect and not fully solved by this graph.\"]}");
        }

        [McpCommand(
            "csproj.generated.list",
            Title = "List Unity-generated C# project files",
            Description = "List bounded project-root Unity-generated .csproj diagnostics and classify predefined, Editor, asmdef, and package assemblies",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CsprojGeneratedListSchemaJson,
            Tags = new string[] { "script", "assembly", "csproj", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = CsprojGeneratedListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ListGeneratedCsproj(McpCommandContext context)
        {
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 200);
            var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
            Array.Sort(assemblies, CompareCompilationAssemblies);
            var asmdefEntries = FindAsmdefAssemblyEntries(includePackages, out var scannedAsmdefs, out var asmdefScanTruncated);
            var csprojFiles = FindProjectRootCsprojFiles(out var scannedCsprojFiles, out var csprojScanTruncated);
            var results = new List<string>();
            var matched = 0;
            var skippedPackageOnly = 0;
            foreach (var csprojFile in csprojFiles)
            {
                var entry = BuildGeneratedCsprojListEntry(csprojFile, assemblies, asmdefEntries);
                if (!includePackages && entry.PackageSourceCount > 0 && entry.ProjectSourceCount == 0)
                {
                    skippedPackageOnly++;
                    continue;
                }

                if (!GeneratedCsprojListMatches(entry, query))
                {
                    continue;
                }

                matched++;
                if (results.Count < maxResults)
                {
                    results.Add(entry.ToJson());
                }
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"max_results\":" + maxResults
                + ",\"max_csproj_files\":" + MaxCsprojFiles
                + ",\"scanned_csproj_files\":" + scannedCsprojFiles
                + ",\"csproj_scan_truncated\":" + JsonUtil.Bool(csprojScanTruncated)
                + ",\"scanned_asmdef_files\":" + scannedAsmdefs
                + ",\"asmdef_scan_truncated\":" + JsonUtil.Bool(asmdefScanTruncated)
                + ",\"compilation_assemblies\":" + assemblies.Length
                + ",\"skipped_package_only\":" + skippedPackageOnly
                + ",\"matched\":" + matched
                + ",\"returned\":" + results.Count
                + ",\"truncated\":" + JsonUtil.Bool(matched > results.Count)
                + ",\"results\":[" + string.Join(",", results.ToArray()) + "]"
                + ",\"warnings\":[\"Generated .csproj files are IDE diagnostics and may be stale.\",\"CompilationPipeline.GetAssemblies().sourceFiles is authoritative for current Unity script ownership.\",\"This command lists project-root .csproj metadata only and does not generate, refresh, compile, import, or write project files.\"]}");
        }

        [McpCommand(
            "csproj.generated.inspect",
            Title = "Inspect Unity-generated C# project file",
            Description = "Inspect one bounded project-root Unity-generated .csproj diagnostic file without generating or refreshing project files",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CsprojGeneratedInspectSchemaJson,
            Tags = new string[] { "script", "assembly", "csproj", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = CsprojGeneratedInspectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult InspectGeneratedCsproj(McpCommandContext context)
        {
            var rawCsprojPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "csproj_path"), 256);
            var rawProjectName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "project_name"), 256);
            var selectors = (!string.IsNullOrWhiteSpace(rawCsprojPath) ? 1 : 0) + (!string.IsNullOrWhiteSpace(rawProjectName) ? 1 : 0);
            if (selectors != 1)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide exactly one csproj_path or project_name selector");
            }

            if (!TryResolveProjectRootCsproj(rawCsprojPath, rawProjectName, out var fullPath, out var error))
            {
                return CommandExecutionResult.Error("invalid_field_value", error);
            }

            var maxItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_items", 100), 0, 500);
            var fileInfo = new FileInfo(fullPath);
            var readBytes = (int)Math.Min(fileInfo.Length, MaxCsprojBytes);
            var content = ReadUtf8Prefix(fullPath, MaxCsprojBytes);
            var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
            Array.Sort(assemblies, CompareCompilationAssemblies);
            var asmdefEntries = FindAsmdefAssemblyEntries(true, out var scannedAsmdefs, out var asmdefScanTruncated);
            var listEntry = BuildGeneratedCsprojListEntry(fullPath, assemblies, asmdefEntries);
            var compileItems = ReadCsprojPathItems(content, "Compile", maxItems, CsprojPathItemKind.Asset, out var compileCount, out var compileRedacted);
            var noneItems = ReadCsprojPathItems(content, "None", maxItems, CsprojPathItemKind.Asset, out var noneCount, out var noneRedacted);
            var projectReferences = ReadCsprojPathItems(content, "ProjectReference", maxItems, CsprojPathItemKind.ProjectReference, out var projectReferenceCount, out var projectReferenceRedacted);
            var analyzers = ReadCsprojPathItems(content, "Analyzer", maxItems, CsprojPathItemKind.Asset, out var analyzerCount, out var analyzerRedacted);
            var references = ReadCsprojReferenceItems(content, maxItems, out var referenceCount, out var referenceRedacted);
            var defineConstants = ReadCsprojDefineConstants(content, maxItems, out var defineConstantCount);
            return CommandExecutionResult.Ok("{\"csproj\":" + listEntry.ToJson()
                + ",\"max_items\":" + maxItems
                + ",\"max_csproj_bytes\":" + MaxCsprojBytes
                + ",\"read_bytes\":" + readBytes
                + ",\"content_truncated\":" + JsonUtil.Bool(fileInfo.Length > readBytes)
                + ",\"scanned_asmdef_files\":" + scannedAsmdefs
                + ",\"asmdef_scan_truncated\":" + JsonUtil.Bool(asmdefScanTruncated)
                + ",\"compile_item_count\":" + compileCount
                + ",\"compile_items_truncated\":" + JsonUtil.Bool(compileCount > compileItems.Count)
                + ",\"compile_items_redacted\":" + compileRedacted
                + ",\"compile_items\":[" + StringArrayJson(compileItems) + "]"
                + ",\"none_item_count\":" + noneCount
                + ",\"none_items_truncated\":" + JsonUtil.Bool(noneCount > noneItems.Count)
                + ",\"none_items_redacted\":" + noneRedacted
                + ",\"none_items\":[" + StringArrayJson(noneItems) + "]"
                + ",\"reference_count\":" + referenceCount
                + ",\"references_truncated\":" + JsonUtil.Bool(referenceCount > references.Count)
                + ",\"references_redacted\":" + referenceRedacted
                + ",\"references\":[" + StringArrayJson(references) + "]"
                + ",\"project_reference_count\":" + projectReferenceCount
                + ",\"project_references_truncated\":" + JsonUtil.Bool(projectReferenceCount > projectReferences.Count)
                + ",\"project_references_redacted\":" + projectReferenceRedacted
                + ",\"project_references\":[" + StringArrayJson(projectReferences) + "]"
                + ",\"define_constant_count\":" + defineConstantCount
                + ",\"define_constants_truncated\":" + JsonUtil.Bool(defineConstantCount > defineConstants.Count)
                + ",\"define_constants\":[" + StringArrayJson(defineConstants) + "]"
                + ",\"analyzer_count\":" + analyzerCount
                + ",\"analyzers_truncated\":" + JsonUtil.Bool(analyzerCount > analyzers.Count)
                + ",\"analyzers_redacted\":" + analyzerRedacted
                + ",\"analyzers\":[" + StringArrayJson(analyzers) + "]"
                + ",\"warnings\":[\"Generated .csproj files are IDE diagnostics and may be stale.\",\"CompilationPipeline.GetAssemblies().sourceFiles is authoritative for current Unity script ownership.\",\"Host absolute paths and non-project/non-package paths are redacted from item arrays.\",\"This command does not generate, refresh, compile, import, or write project files.\"]}");
        }

        [McpCommand(
            "csproj.generated.compare",
            Title = "Compare Unity-generated C# project files",
            Description = "Compare bounded project-root generated .csproj Compile and ProjectReference diagnostics with current Unity CompilationPipeline metadata",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CsprojGeneratedCompareSchemaJson,
            Tags = new string[] { "script", "assembly", "csproj", "unity-api", "compare" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = CsprojGeneratedCompareOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CompareGeneratedCsproj(McpCommandContext context)
        {
            var query = Bounded(JsonUtil.ExtractString(context.ArgsJson, "query"), 128);
            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", true);
            var maxProjects = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_projects", 50), 1, 200);
            var maxDifferences = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_differences_per_project", 100), 0, 500);
            var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
            Array.Sort(assemblies, CompareCompilationAssemblies);
            var asmdefEntries = FindAsmdefAssemblyEntries(includePackages, out var scannedAsmdefs, out var asmdefScanTruncated);
            var csprojFiles = FindProjectRootCsprojFiles(out var scannedCsprojFiles, out var csprojScanTruncated);
            var csprojEntries = new List<GeneratedCsprojListEntry>();
            foreach (var csprojFile in csprojFiles)
            {
                csprojEntries.Add(BuildGeneratedCsprojListEntry(csprojFile, assemblies, asmdefEntries));
            }

            var graphNodes = BuildAssemblyGraphNodes(includePackages, out var graphScannedAsmdefs, out var graphScanTruncated);
            var graphEdges = BuildAssemblyGraphEdges(graphNodes);
            var results = new List<string>();
            var matchedProjects = 0;
            var comparedProjects = 0;
            var mismatchedProjects = 0;
            var skippedPackageOnly = 0;
            foreach (var entry in csprojEntries)
            {
                if (!includePackages && entry.PackageSourceCount > 0 && entry.ProjectSourceCount == 0)
                {
                    skippedPackageOnly++;
                    continue;
                }

                if (!GeneratedCsprojListMatches(entry, query))
                {
                    continue;
                }

                matchedProjects++;
                if (results.Count >= maxProjects)
                {
                    continue;
                }

                var comparison = CompareGeneratedCsprojEntry(entry, assemblies, csprojEntries, graphEdges, maxDifferences);
                comparedProjects++;
                if (comparison.Mismatched)
                {
                    mismatchedProjects++;
                }

                results.Add(comparison.ToJson());
            }

            return CommandExecutionResult.Ok("{\"query\":" + JsonUtil.String(query)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"max_projects\":" + maxProjects
                + ",\"max_differences_per_project\":" + maxDifferences
                + ",\"max_csproj_files\":" + MaxCsprojFiles
                + ",\"max_csproj_bytes\":" + MaxCsprojBytes
                + ",\"scanned_csproj_files\":" + scannedCsprojFiles
                + ",\"csproj_scan_truncated\":" + JsonUtil.Bool(csprojScanTruncated)
                + ",\"scanned_asmdef_files\":" + Math.Max(scannedAsmdefs, graphScannedAsmdefs)
                + ",\"asmdef_scan_truncated\":" + JsonUtil.Bool(asmdefScanTruncated || graphScanTruncated)
                + ",\"compilation_assemblies\":" + assemblies.Length
                + ",\"skipped_package_only\":" + skippedPackageOnly
                + ",\"matched_projects\":" + matchedProjects
                + ",\"compared_projects\":" + comparedProjects
                + ",\"mismatched_projects\":" + mismatchedProjects
                + ",\"projects_truncated\":" + JsonUtil.Bool(matchedProjects > results.Count)
                + ",\"results\":[" + string.Join(",", results.ToArray()) + "]"
                + ",\"warnings\":[\"Generated .csproj files are IDE diagnostics and may be stale.\",\"CompilationPipeline.GetAssemblies().sourceFiles is authoritative for current Unity script ownership.\",\"ProjectReference comparison uses local asmdef/generated graph edges and only compares target assemblies that have project-root .csproj files.\",\"Host absolute paths and non-project/non-package paths are redacted from item arrays.\",\"This command does not generate, refresh, compile, import, or write project files.\"]}");
        }

        [McpCommand(
            "script.relocation.preflight",
            Title = "Preflight Unity script relocation",
            Description = "Dry-run predicted assembly ownership and reference visibility risks for moving one Unity .cs script path",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ScriptRelocationPreflightSchemaJson,
            Tags = new string[] { "script", "assembly", "relocation", "unity-api", "dry-run" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = ScriptRelocationPreflightOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult PreflightScriptRelocation(McpCommandContext context)
        {
            var rawSourceScriptPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "source_script_path"), 512);
            var rawTargetScriptPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "target_script_path"), 512);
            var maxDeclaredTypes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_declared_types", 20), 0, 50);
            string sourceScriptPath;
            string targetScriptPath;
            try
            {
                sourceScriptPath = PathGuard.RequireUnityAssetPath(rawSourceScriptPath);
                targetScriptPath = PathGuard.RequireUnityAssetPath(rawTargetScriptPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "source_script_path and target_script_path must be Unity-relative paths under Assets/ or Packages/");
            }

            if (!sourceScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !targetScriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "source_script_path and target_script_path must point to .cs files");
            }

            if (!TryResolveUnityPath(sourceScriptPath, true, out var sourceFullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "source_script_path does not exist");
            }

            var targetExists = TryResolveUnityPath(targetScriptPath, true, out _);
            var targetParentPath = ParentAssetPath(targetScriptPath);
            var targetParentExists = !string.IsNullOrEmpty(targetParentPath) && TryResolveUnityPath(targetParentPath, false, out _);
            var sourcePrediction = PredictAssembly(sourceScriptPath, IsUnderEditorFolder(sourceScriptPath));
            var targetPrediction = PredictAssembly(targetScriptPath, IsUnderEditorFolder(targetScriptPath));
            var sourceResolved = ResolvedAssemblyInputFromPrediction("source_script_path", sourceScriptPath, sourcePrediction);
            var targetResolved = ResolvedAssemblyInputFromPrediction("target_script_path", targetScriptPath, targetPrediction);
            var sourceToTarget = ExplainReference(sourceResolved, targetResolved);
            var targetToSource = ExplainReference(targetResolved, sourceResolved);
            var declaredTypes = ExtractDeclaredTypes(ReadUtf8Prefix(sourceFullPath, MaxScriptBytes));
            var assemblyChanges = !string.Equals(sourcePrediction.AssemblyName, targetPrediction.AssemblyName, StringComparison.Ordinal);
            var warnings = BuildRelocationPreflightWarnings(sourceScriptPath, targetScriptPath, targetExists, targetParentExists, assemblyChanges, sourceToTarget, targetToSource);
            return CommandExecutionResult.Ok("{\"dry_run\":true"
                + ",\"source_script_path\":" + JsonUtil.String(sourceScriptPath)
                + ",\"target_script_path\":" + JsonUtil.String(targetScriptPath)
                + ",\"target_exists\":" + JsonUtil.Bool(targetExists)
                + ",\"target_parent_path\":" + JsonUtil.String(targetParentPath)
                + ",\"target_parent_exists\":" + JsonUtil.Bool(targetParentExists)
                + ",\"max_declared_types\":" + maxDeclaredTypes
                + ",\"declared_type_count\":" + declaredTypes.Count
                + ",\"declared_types_truncated\":" + JsonUtil.Bool(declaredTypes.Count > maxDeclaredTypes)
                + ",\"declared_types\":[" + DeclaredTypesJson(declaredTypes, maxDeclaredTypes) + "]"
                + ",\"source_assembly\":" + sourcePrediction.ToJson()
                + ",\"target_assembly\":" + targetPrediction.ToJson()
                + ",\"assembly_changes\":" + JsonUtil.Bool(assemblyChanges)
                + ",\"old_source_can_reference_new_target\":" + ReferenceExplanationJson(sourceToTarget)
                + ",\"new_target_can_reference_old_source\":" + ReferenceExplanationJson(targetToSource)
                + ",\"warnings\":[" + WarningsJson(warnings) + "]}");
        }

        [McpCommand(
            "assembly.inspect",
            Title = "Inspect Unity script assembly",
            Description = "Inspect one generated or asmdef Unity script assembly and return bounded reference metadata",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = AssemblyInspectSchemaJson,
            Tags = new string[] { "script", "assembly", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = AssemblyInspectOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult InspectAssembly(McpCommandContext context)
        {
            var requestedAssemblyName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "assembly_name"), 256);
            var rawAsmdefPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "asmdef_path"), 512);
            if (string.IsNullOrWhiteSpace(requestedAssemblyName) && string.IsNullOrWhiteSpace(rawAsmdefPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument assembly_name or asmdef_path is required");
            }

            var includeTypes = JsonUtil.ExtractBool(context.ArgsJson, "include_types", false);
            var maxTypes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_types", 50), 1, 200);
            var asmdefContent = string.Empty;
            var ambiguous = false;
            AssemblyListEntry entry;
            try
            {
                entry = string.IsNullOrWhiteSpace(rawAsmdefPath)
                    ? FindAssemblyListEntryByName(requestedAssemblyName, out ambiguous)
                    : AssemblyListEntryFromAsmdefPath(rawAsmdefPath, out asmdefContent);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asmdef_path must point to a .asmdef file under Assets/ or Packages/");
            }
            if (string.IsNullOrWhiteSpace(rawAsmdefPath) && ambiguous)
            {
                return CommandExecutionResult.Error("assembly_ambiguous", "assembly_name matches multiple asmdefs; pass asmdef_path");
            }

            if (entry == null)
            {
                return CommandExecutionResult.Error("assembly_not_found", "Assembly was not found in generated assemblies, asmdefs, or loaded AppDomain assemblies");
            }

            if (!string.IsNullOrWhiteSpace(requestedAssemblyName) && !string.Equals(entry.AssemblyName, requestedAssemblyName, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "assembly_name does not match asmdef name");
            }

            if (entry.Kind == "asmdef" && string.IsNullOrEmpty(asmdefContent))
            {
                asmdefContent = ReadUtf8Prefix(ResolveUnityPathOrFallback(entry.AsmdefPath, true), MaxAsmdefBytes);
            }

            var loadedAssembly = FindLoadedAssemblyByName(entry.AssemblyName);
            var loadedTypes = loadedAssembly == null ? new List<Type>() : SafeGetTypes(loadedAssembly);
            loadedTypes.Sort(CompareTypes);
            var includedTypes = includeTypes ? Math.Min(loadedTypes.Count, maxTypes) : 0;
            var json = new StringBuilder();
            json.Append('{');
            json.Append("\"assembly_name\":").Append(JsonUtil.String(entry.AssemblyName));
            json.Append(",\"kind\":").Append(JsonUtil.String(entry.Kind));
            json.Append(",\"asmdef_path\":").Append(JsonUtil.String(entry.AsmdefPath));
            json.Append(",\"auto_generated\":").Append(JsonUtil.Bool(entry.AutoGenerated));
            json.Append(",\"editor_only\":").Append(JsonUtil.Bool(entry.EditorOnly));
            json.Append(",\"reason\":").Append(JsonUtil.String(entry.Reason));
            json.Append(",\"loaded\":").Append(JsonUtil.Bool(loadedAssembly != null));
            json.Append(",\"loaded_assembly\":").Append(loadedAssembly == null ? "null" : LoadedAssemblyJson(loadedAssembly, loadedTypes.Count));
            json.Append(",\"asmdef\":").Append(entry.Kind == "asmdef" ? AsmdefDetailsJson(asmdefContent) : "null");
            json.Append(",\"types\":[");
            for (var index = 0; index < includedTypes; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append(LoadedTypeJson(loadedTypes[index]));
            }

            json.Append(']');
            json.Append(",\"type_count\":").Append(loadedTypes.Count);
            json.Append(",\"types_truncated\":").Append(JsonUtil.Bool(includeTypes && loadedTypes.Count > includedTypes));
            json.Append(",\"warnings\":[\"using directives do not add assembly references; asmdef references control cross-assembly access.\"]}");
            return CommandExecutionResult.Ok(json.ToString());
        }

        [McpCommand(
            "assembly.references.explain",
            Title = "Explain Unity assembly references",
            Description = "Explain whether one Unity script assembly can reference another using asmdef and generated assembly rules",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReferencesExplainSchemaJson,
            Tags = new string[] { "script", "assembly", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = ReferencesExplainOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ExplainAssemblyReference(McpCommandContext context)
        {
            var sourceResult = ResolveAssemblyInput(context.ArgsJson, "source", out var source);
            if (sourceResult != null)
            {
                return sourceResult;
            }

            var targetResult = ResolveAssemblyInput(context.ArgsJson, "target", out var target);
            if (targetResult != null)
            {
                return targetResult;
            }

            var explanation = ExplainReference(source, target);
            return CommandExecutionResult.Ok("{\"source\":" + source.ToJson()
                + ",\"target\":" + target.ToJson()
                + ",\"can_reference\":" + JsonUtil.Bool(explanation.CanReference)
                + ",\"reference_kind\":" + JsonUtil.String(explanation.ReferenceKind)
                + ",\"required_action\":" + JsonUtil.String(explanation.RequiredAction)
                + ",\"checks\":[" + string.Join(",", explanation.Checks.ToArray()) + "]"
                + ",\"warnings\":[" + WarningsJson(explanation.Warnings) + "]}");
        }

        [McpCommand(
            "assembly.references.trace",
            Title = "Trace Unity assembly reference evidence",
            Description = "Trace bounded loaded-assembly TypeRef, MemberRef, CustomAttribute, asmdef, and source-hint evidence for why one assembly references another",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReferencesTraceSchemaJson,
            Tags = new string[] { "script", "assembly", "unity-api", "reference-trace" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = ReferencesTraceOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TraceAssemblyReference(McpCommandContext context)
        {
            var sourceResult = ResolveAssemblyInput(context.ArgsJson, "source", out var source);
            if (sourceResult != null)
            {
                return sourceResult;
            }

            var targetResult = ResolveAssemblyInput(context.ArgsJson, "target", out var target);
            if (targetResult != null)
            {
                return targetResult;
            }

            var maxTypes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_types", 1000), 1, MaxReferenceTraceTypes);
            var maxEvidence = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_evidence", 100), 1, MaxReferenceTraceEvidence);
            var maxSourceHints = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_source_hints", 50), 0, MaxReferenceTraceSourceHints);
            var maxSourceFiles = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_source_files", 500), 0, MaxReferenceTraceSourceFiles);
            var explanation = ExplainReference(source, target);
            var sourceAssembly = FindLoadedAssemblyByName(source.Entry.AssemblyName);
            var targetAssembly = FindLoadedAssemblyByName(target.Entry.AssemblyName);
            var metadataReference = sourceAssembly == null ? null : FindReferencedAssemblyName(sourceAssembly, target.Entry.AssemblyName);
            var graphEdges = FindReferenceTraceGraphEdges(source, target, out var scannedAsmdefs, out var asmdefScanTruncated, out var matchedGraphEdges);
            var scannedTypes = 0;
            var typesTruncated = false;
            var evidenceTruncated = false;
            var reflectionErrors = 0;
            var evidence = sourceAssembly == null
                ? new List<ReferenceTraceEvidence>()
                : TraceLoadedAssemblyReferences(sourceAssembly, target.Entry.AssemblyName, maxTypes, maxEvidence, out scannedTypes, out typesTruncated, out evidenceTruncated, out reflectionErrors);
            var sourceHints = BuildReferenceTraceSourceHints(source, evidence, maxSourceHints, maxSourceFiles, out var scannedSourceFiles, out var matchedSourceHints, out var sourceHintScanTruncated);
            var warnings = BuildReferenceTraceWarnings(sourceAssembly, metadataReference, explanation, evidenceTruncated, typesTruncated, sourceHintScanTruncated, reflectionErrors);

            return CommandExecutionResult.Ok("{\"source\":" + source.ToJson()
                + ",\"target\":" + target.ToJson()
                + ",\"target_assembly_loaded\":" + JsonUtil.Bool(targetAssembly != null)
                + ",\"max_types\":" + maxTypes
                + ",\"max_evidence\":" + maxEvidence
                + ",\"max_source_hints\":" + maxSourceHints
                + ",\"max_source_files\":" + maxSourceFiles
                + ",\"source_assembly_loaded\":" + JsonUtil.Bool(sourceAssembly != null)
                + ",\"metadata_reference_present\":" + JsonUtil.Bool(metadataReference != null)
                + ",\"metadata_reference\":" + AssemblyNameReferenceJson(metadataReference)
                + ",\"reference_rule\":" + ReferenceExplanationJson(explanation)
                + ",\"scanned_asmdef_files\":" + scannedAsmdefs
                + ",\"asmdef_scan_truncated\":" + JsonUtil.Bool(asmdefScanTruncated)
                + ",\"matched_graph_edges\":" + matchedGraphEdges
                + ",\"graph_edges_truncated\":" + JsonUtil.Bool(matchedGraphEdges > graphEdges.Count)
                + ",\"graph_edges\":[" + AssemblyGraphEdgesJson(graphEdges) + "]"
                + ",\"scanned_types\":" + scannedTypes
                + ",\"types_truncated\":" + JsonUtil.Bool(typesTruncated)
                + ",\"reflection_error_count\":" + reflectionErrors
                + ",\"evidence_count\":" + evidence.Count
                + ",\"evidence_truncated\":" + JsonUtil.Bool(evidenceTruncated)
                + ",\"evidence\":[" + ReferenceTraceEvidenceJson(evidence) + "]"
                + ",\"scanned_source_files\":" + scannedSourceFiles
                + ",\"matched_source_hints\":" + matchedSourceHints
                + ",\"source_hints_truncated\":" + JsonUtil.Bool(matchedSourceHints > sourceHints.Count || sourceHintScanTruncated)
                + ",\"source_hint_scan_truncated\":" + JsonUtil.Bool(sourceHintScanTruncated)
                + ",\"source_hints\":[" + string.Join(",", sourceHints.ToArray()) + "]"
                + ",\"warnings\":[" + WarningsJson(warnings) + "]}");
        }

        [McpCommand(
            "script.type.resolve",
            Title = "Resolve Unity script type",
            Description = "Resolve a script type name to loaded type candidates and bounded candidate script declarations",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TypeResolveSchemaJson,
            Tags = new string[] { "script", "type", "assembly", "unity-api" },
            Capability = FeatureFlag,
            ResultShape = "bounded_list",
            OutputLimitJson = TypeResolveOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ResolveScriptType(McpCommandContext context)
        {
            var typeName = Bounded(JsonUtil.ExtractString(context.ArgsJson, "type_name"), 256);
            if (string.IsNullOrWhiteSpace(typeName))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument type_name is required");
            }

            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 20), 1, 50);
            var maxScanFiles = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_files", 500), 1, MaxTypeResolveScriptFiles);
            var includeScripts = JsonUtil.ExtractBool(context.ArgsJson, "include_scripts", true);
            var loadedTypes = FindLoadedTypes(typeName, new List<DeclaredType>(), maxResults, out var matchedLoadedTypes);
            var scriptCandidates = new List<string>();
            var scannedScripts = 0;
            var matchedScripts = 0;
            var scanTruncated = false;
            if (includeScripts)
            {
                scriptCandidates = FindScriptTypeCandidates(typeName, maxResults, maxScanFiles, out scannedScripts, out matchedScripts, out scanTruncated);
            }

            var warnings = BuildWarnings(null, loadedTypes, matchedLoadedTypes);
            if (scanTruncated)
            {
                warnings.Add("script candidate scan was truncated by max_scan_files.");
            }

            return CommandExecutionResult.Ok("{\"type_name\":" + JsonUtil.String(typeName)
                + ",\"include_scripts\":" + JsonUtil.Bool(includeScripts)
                + ",\"max_results\":" + maxResults
                + ",\"max_scan_files\":" + maxScanFiles
                + ",\"scanned_script_files\":" + scannedScripts
                + ",\"script_type_matches\":" + matchedScripts
                + ",\"script_candidates_truncated\":" + JsonUtil.Bool(scanTruncated || matchedScripts > scriptCandidates.Count)
                + ",\"loaded_type_candidates\":[" + string.Join(",", loadedTypes.ToArray()) + "]"
                + ",\"loaded_type_matches\":" + matchedLoadedTypes
                + ",\"loaded_type_candidates_truncated\":" + JsonUtil.Bool(matchedLoadedTypes > loadedTypes.Count)
                + ",\"script_candidates\":[" + string.Join(",", scriptCandidates.ToArray()) + "]"
                + ",\"warnings\":[" + WarningsJson(warnings) + "]}");
        }

        static CommandExecutionResult ResolveAssemblyInput(string argsJson, string prefix, out ResolvedAssemblyInput resolved)
        {
            resolved = null;
            var scriptPath = Bounded(JsonUtil.ExtractString(argsJson, prefix + "_script_path"), 512);
            var assemblyName = Bounded(JsonUtil.ExtractString(argsJson, prefix + "_assembly_name"), 256);
            var asmdefPath = Bounded(JsonUtil.ExtractString(argsJson, prefix + "_asmdef_path"), 512);
            var selectors = (!string.IsNullOrWhiteSpace(scriptPath) ? 1 : 0) + (!string.IsNullOrWhiteSpace(assemblyName) ? 1 : 0) + (!string.IsNullOrWhiteSpace(asmdefPath) ? 1 : 0);
            if (selectors != 1)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide exactly one " + prefix + " selector");
            }

            try
            {
                if (!string.IsNullOrWhiteSpace(scriptPath))
                {
                    return ResolveScriptAssemblyInput(prefix, scriptPath, out resolved);
                }

                if (!string.IsNullOrWhiteSpace(asmdefPath))
                {
                    var entry = AssemblyListEntryFromAsmdefPath(asmdefPath, out var asmdefContent);
                    if (entry == null)
                    {
                        return CommandExecutionResult.Error(prefix + "_assembly_not_found", prefix + " asmdef_path was not found");
                    }

                    resolved = new ResolvedAssemblyInput("asmdef_path", string.Empty, entry, asmdefContent);
                    return null;
                }
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", prefix + " path must be Unity-relative and point to a .cs or .asmdef file under Assets/ or Packages/");
            }

            var resolvedEntry = FindAssemblyListEntryByName(assemblyName, out var ambiguous);
            if (ambiguous)
            {
                return CommandExecutionResult.Error(prefix + "_assembly_ambiguous", prefix + " assembly_name matches multiple asmdefs; pass " + prefix + "_asmdef_path");
            }

            if (resolvedEntry == null)
            {
                return CommandExecutionResult.Error(prefix + "_assembly_not_found", prefix + " assembly was not found");
            }

            resolved = new ResolvedAssemblyInput("assembly_name", string.Empty, resolvedEntry, LoadAsmdefContent(resolvedEntry));
            return null;
        }

        static CommandExecutionResult ResolveScriptAssemblyInput(string prefix, string rawScriptPath, out ResolvedAssemblyInput resolved)
        {
            resolved = null;
            var scriptPath = PathGuard.RequireUnityAssetPath(rawScriptPath);
            if (!scriptPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", prefix + "_script_path must point to a .cs file under Assets/ or Packages/");
            }

            if (!TryResolveUnityPath(scriptPath, true, out _))
            {
                return CommandExecutionResult.Error(prefix + "_assembly_not_found", prefix + "_script_path does not exist");
            }

            var prediction = PredictAssembly(scriptPath, IsUnderEditorFolder(scriptPath));
            var entry = new AssemblyListEntry(prediction.AsmdefPath, prediction.AssemblyName, prediction.AutoGenerated ? "auto" : "asmdef", prediction.AutoGenerated, prediction.EditorOnly, prediction.Reason);
            resolved = new ResolvedAssemblyInput("script_path", scriptPath, entry, LoadAsmdefContent(entry));
            return null;
        }

        static ResolvedAssemblyInput ResolvedAssemblyInputFromPrediction(string inputKind, string scriptPath, AssemblyPrediction prediction)
        {
            var entry = new AssemblyListEntry(prediction.AsmdefPath, prediction.AssemblyName, prediction.AutoGenerated ? "auto" : "asmdef", prediction.AutoGenerated, prediction.EditorOnly, prediction.Reason);
            return new ResolvedAssemblyInput(inputKind, scriptPath, entry, LoadAsmdefContent(entry));
        }

        static string ReferenceExplanationJson(ReferenceExplanation explanation)
        {
            return "{\"can_reference\":" + JsonUtil.Bool(explanation.CanReference)
                + ",\"reference_kind\":" + JsonUtil.String(explanation.ReferenceKind)
                + ",\"required_action\":" + JsonUtil.String(explanation.RequiredAction)
                + ",\"checks\":[" + string.Join(",", explanation.Checks.ToArray()) + "]"
                + ",\"warnings\":[" + WarningsJson(explanation.Warnings) + "]}";
        }

        static List<string> BuildRelocationPreflightWarnings(string sourceScriptPath, string targetScriptPath, bool targetExists, bool targetParentExists, bool assemblyChanges, ReferenceExplanation sourceToTarget, ReferenceExplanation targetToSource)
        {
            var warnings = new List<string> { "This is a dry-run preflight only; it does not move, import, refresh, compile, or write files.", "using directives do not add assembly references; asmdef references control cross-assembly access.", "Code-level symbol usages are not fully solved by this command; run compile validation after an approved move." };
            if (targetExists)
            {
                warnings.Add("target_script_path already exists; an actual move/write would require overwrite-safe policy and approval.");
            }

            if (!targetParentExists)
            {
                warnings.Add("target parent directory does not exist; prediction uses nearest existing parent assembly rules.");
            }

            if (sourceScriptPath.StartsWith("Packages/", StringComparison.Ordinal) || targetScriptPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                warnings.Add("Packages/ script relocation may cross package ownership boundaries and should be handled with package-aware policy.");
            }

            if (assemblyChanges)
            {
                warnings.Add("source and target assemblies differ; references to moved types may need asmdef changes or dependent script relocation.");
            }

            if (!sourceToTarget.CanReference)
            {
                warnings.Add("old source assembly may not be able to reference moved types in the predicted target assembly.");
            }

            if (!targetToSource.CanReference)
            {
                warnings.Add("moved script may lose visibility to types that remain in the old source assembly.");
            }

            return warnings;
        }

        static ReferenceExplanation ExplainReference(ResolvedAssemblyInput source, ResolvedAssemblyInput target)
        {
            var explanation = new ReferenceExplanation();
            explanation.Warnings.Add("using directives do not add assembly references; asmdef references control cross-assembly access.");
            if (string.Equals(source.Entry.AssemblyName, target.Entry.AssemblyName, StringComparison.Ordinal))
            {
                return explanation.Pass("same_assembly", "none", CheckJson("same_assembly", true, "Source and target resolve to the same assembly."));
            }

            explanation.Checks.Add(CheckJson("same_assembly", false, "Source and target are different assemblies."));
            if (!source.Entry.EditorOnly && target.Entry.EditorOnly)
            {
                explanation.Checks.Add(CheckJson("runtime_to_editor", false, "Runtime assembly cannot reference an Editor-only target assembly."));
                return explanation.Block("blocked", "move_source_to_editor_assembly");
            }

            if (source.Entry.Kind == "loaded" || target.Entry.Kind == "loaded")
            {
                explanation.Warnings.Add("loaded AppDomain assembly lacks local asmdef or generated assembly metadata; reference visibility is unknown.");
                explanation.Checks.Add(CheckJson("local_metadata_available", false, "Source or target has only loaded AppDomain metadata."));
                return explanation.Block("unknown", "unknown");
            }

            if (source.Entry.Kind == "auto" && target.Entry.Kind == "auto")
            {
                var allowed = string.Equals(source.Entry.AssemblyName, "Assembly-CSharp-Editor", StringComparison.Ordinal) && string.Equals(target.Entry.AssemblyName, "Assembly-CSharp", StringComparison.Ordinal);
                explanation.Checks.Add(CheckJson("implicit_generated_reference", allowed, allowed ? "Editor predefined assembly can reference runtime predefined assembly." : "Generated assembly direction is not referenceable."));
                return allowed ? explanation.Pass("implicit_generated_reference", "none") : explanation.Block("blocked", "unsupported_generated_target");
            }

            if (source.Entry.Kind == "auto" && target.Entry.Kind == "asmdef")
            {
                var autoReferenced = ReadAsmdefBoolField(target.AsmdefContent, "autoReferenced", true);
                explanation.Checks.Add(CheckJson("target_auto_referenced", autoReferenced, autoReferenced ? "Target asmdef is auto-referenced by predefined assemblies." : "Target asmdef has autoReferenced=false."));
                return autoReferenced ? explanation.Pass("implicit_generated_reference", "none") : explanation.Block("blocked", "move_source_to_asmdef");
            }

            if (source.Entry.Kind == "asmdef" && target.Entry.Kind == "auto")
            {
                explanation.Checks.Add(CheckJson("asmdef_to_generated", false, "Asmdef assemblies should not depend on predefined Assembly-CSharp assemblies; move shared target code into an asmdef."));
                return explanation.Block("blocked", "unsupported_generated_target");
            }

            if (source.Entry.Kind == "asmdef" && target.Entry.Kind == "asmdef")
            {
                return ExplainAsmdefToAsmdefReference(source, target, explanation);
            }

            explanation.Checks.Add(CheckJson("reference_rule_known", false, "No local rule matched this source and target assembly pair."));
            return explanation.Block("unknown", "unknown");
        }

        static ReferenceExplanation ExplainAsmdefToAsmdefReference(ResolvedAssemblyInput source, ResolvedAssemblyInput target, ReferenceExplanation explanation)
        {
            var references = ReadAsmdefStringArray(source.AsmdefContent, "references", 200);
            var targetGuid = string.IsNullOrEmpty(target.Entry.AsmdefPath) ? string.Empty : UnityEditor.AssetDatabase.AssetPathToGUID(target.Entry.AsmdefPath);
            var guidReference = string.IsNullOrEmpty(targetGuid) ? string.Empty : "GUID:" + targetGuid;
            var byName = ContainsExact(references, target.Entry.AssemblyName);
            var byGuid = !string.IsNullOrEmpty(guidReference) && ContainsExactIgnoreCase(references, guidReference);
            explanation.Checks.Add(CheckJson("source_asmdef_references_target_name", byName, byName ? "Source asmdef references target by assembly name." : "Source asmdef does not reference target by assembly name."));
            explanation.Checks.Add(CheckJson("source_asmdef_references_target_guid", byGuid, byGuid ? "Source asmdef references target by GUID." : "Source asmdef does not reference target by GUID."));
            explanation.Warnings.Add("platform, define constraints, and versionDefines are reported but not fully solved by this command.");
            return byName || byGuid
                ? explanation.Pass("explicit_asmdef_reference", "none")
                : explanation.Block("blocked", "add_asmdef_reference");
        }

        static string LoadAsmdefContent(AssemblyListEntry entry)
        {
            return entry.Kind == "asmdef" && !string.IsNullOrEmpty(entry.AsmdefPath) ? ReadUtf8Prefix(ResolveUnityPathOrFallback(entry.AsmdefPath, true), MaxAsmdefBytes) : string.Empty;
        }

        static bool ContainsExact(List<string> values, string expected)
        {
            foreach (var value in values)
            {
                if (string.Equals(value, expected, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static bool ContainsExactIgnoreCase(List<string> values, string expected)
        {
            foreach (var value in values)
            {
                if (string.Equals(value, expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static string CheckJson(string name, bool passed, string detail)
        {
            return "{\"name\":" + JsonUtil.String(name) + ",\"passed\":" + JsonUtil.Bool(passed) + ",\"detail\":" + JsonUtil.String(detail) + "}";
        }

        static List<AssemblyGraphEdge> FindReferenceTraceGraphEdges(ResolvedAssemblyInput source, ResolvedAssemblyInput target, out int scannedAsmdefs, out bool scanTruncated, out int matchedEdges)
        {
            var graphNodes = BuildAssemblyGraphNodes(true, out scannedAsmdefs, out scanTruncated);
            var graphEdges = BuildAssemblyGraphEdges(graphNodes);
            var results = new List<AssemblyGraphEdge>();
            matchedEdges = 0;
            foreach (var edge in graphEdges)
            {
                if (!GraphNodeMatchesResolvedInput(edge.Source, source) || !GraphNodeMatchesResolvedInput(edge.Target, target))
                {
                    continue;
                }

                matchedEdges++;
                if (results.Count < MaxReferenceTraceGraphEdges)
                {
                    results.Add(edge);
                }
            }

            return results;
        }

        static bool GraphNodeMatchesResolvedInput(AssemblyGraphNode node, ResolvedAssemblyInput input)
        {
            if (!string.Equals(node.Entry.AssemblyName, input.Entry.AssemblyName, StringComparison.Ordinal))
            {
                return false;
            }

            return string.IsNullOrEmpty(input.Entry.AsmdefPath) || string.Equals(node.Entry.AsmdefPath, input.Entry.AsmdefPath, StringComparison.Ordinal);
        }

        static AssemblyName FindReferencedAssemblyName(Assembly assembly, string targetAssemblyName)
        {
            var references = assembly.GetReferencedAssemblies();
            Array.Sort(references, CompareAssemblyNames);
            foreach (var reference in references)
            {
                if (string.Equals(reference.Name ?? string.Empty, targetAssemblyName, StringComparison.Ordinal))
                {
                    return reference;
                }
            }

            return null;
        }

        static string AssemblyNameReferenceJson(AssemblyName reference)
        {
            if (reference == null)
            {
                return "null";
            }

            return "{\"name\":" + JsonUtil.String(reference.Name ?? string.Empty)
                + ",\"version\":" + JsonUtil.String(reference.Version == null ? string.Empty : reference.Version.ToString())
                + ",\"culture\":" + JsonUtil.String(string.IsNullOrEmpty(reference.CultureName) ? "neutral" : reference.CultureName)
                + ",\"public_key_token\":" + JsonUtil.String(PublicKeyTokenHex(reference.GetPublicKeyToken())) + "}";
        }

        static string PublicKeyTokenHex(byte[] token)
        {
            if (token == null || token.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var value in token)
            {
                builder.Append(value.ToString("x2"));
            }

            return builder.ToString();
        }

        static List<ReferenceTraceEvidence> TraceLoadedAssemblyReferences(Assembly sourceAssembly, string targetAssemblyName, int maxTypes, int maxEvidence, out int scannedTypes, out bool typesTruncated, out bool evidenceTruncated, out int reflectionErrors)
        {
            scannedTypes = 0;
            typesTruncated = false;
            evidenceTruncated = false;
            reflectionErrors = 0;
            var evidence = new List<ReferenceTraceEvidence>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var types = SafeGetTypes(sourceAssembly);
            types.Sort(CompareTypes);
            foreach (var type in types)
            {
                if (scannedTypes >= maxTypes)
                {
                    typesTruncated = true;
                    break;
                }

                scannedTypes++;
                AddTypeReferenceTrace(type, targetAssemblyName, maxEvidence, evidence, seen, ref evidenceTruncated, ref reflectionErrors);
            }

            return evidence;
        }

        static void AddTypeReferenceTrace(Type type, string targetAssemblyName, int maxEvidence, List<ReferenceTraceEvidence> evidence, HashSet<string> seen, ref bool evidenceTruncated, ref int reflectionErrors)
        {
            var ownerType = ReflectionTypeName(type);
            AddTypeReferenceEvidence(evidence, seen, "TypeRef", "type_ref", ownerType, string.Empty, "base_type", type.BaseType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
            foreach (var interfaceType in SafeGetInterfaces(type, ref reflectionErrors))
            {
                AddTypeReferenceEvidence(evidence, seen, "TypeRef", "type_ref", ownerType, string.Empty, "interface", interfaceType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
            }

            AddGenericConstraintEvidence(evidence, seen, "TypeRef", "type_ref", ownerType, string.Empty, "generic_constraint", type.GetGenericArguments(), targetAssemblyName, maxEvidence, ref evidenceTruncated, ref reflectionErrors);
            AddCustomAttributeReferenceEvidence(evidence, seen, ownerType, string.Empty, "type_attribute", SafeGetCustomAttributes(type, ref reflectionErrors), targetAssemblyName, maxEvidence, ref evidenceTruncated);

            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (var field in SafeGetFields(type, flags, ref reflectionErrors))
            {
                var member = "field " + field.Name;
                AddTypeReferenceEvidence(evidence, seen, "MemberRef", "member_ref", ownerType, member, "field_type", field.FieldType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
                AddCustomAttributeReferenceEvidence(evidence, seen, ownerType, member, "field_attribute", SafeGetCustomAttributes(field, ref reflectionErrors), targetAssemblyName, maxEvidence, ref evidenceTruncated);
            }

            foreach (var property in SafeGetProperties(type, flags, ref reflectionErrors))
            {
                var member = "property " + property.Name;
                AddTypeReferenceEvidence(evidence, seen, "MemberRef", "member_ref", ownerType, member, "property_type", property.PropertyType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
                foreach (var parameter in SafeGetIndexParameters(property, ref reflectionErrors))
                {
                    AddTypeReferenceEvidence(evidence, seen, "MemberRef", "member_ref", ownerType, member, "index_parameter_type:" + parameter.Name, parameter.ParameterType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
                }

                AddCustomAttributeReferenceEvidence(evidence, seen, ownerType, member, "property_attribute", SafeGetCustomAttributes(property, ref reflectionErrors), targetAssemblyName, maxEvidence, ref evidenceTruncated);
            }

            foreach (var eventInfo in SafeGetEvents(type, flags, ref reflectionErrors))
            {
                var member = "event " + eventInfo.Name;
                AddTypeReferenceEvidence(evidence, seen, "MemberRef", "member_ref", ownerType, member, "event_handler_type", eventInfo.EventHandlerType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
                AddCustomAttributeReferenceEvidence(evidence, seen, ownerType, member, "event_attribute", SafeGetCustomAttributes(eventInfo, ref reflectionErrors), targetAssemblyName, maxEvidence, ref evidenceTruncated);
            }

            foreach (var constructor in SafeGetConstructors(type, flags, ref reflectionErrors))
            {
                AddMethodBaseReferenceEvidence(evidence, seen, ownerType, "constructor .ctor", constructor, targetAssemblyName, maxEvidence, ref evidenceTruncated, ref reflectionErrors);
            }

            foreach (var method in SafeGetMethods(type, flags, ref reflectionErrors))
            {
                var member = "method " + method.Name;
                AddTypeReferenceEvidence(evidence, seen, "MemberRef", "member_ref", ownerType, member, "method_return_type", method.ReturnType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
                AddMethodBaseReferenceEvidence(evidence, seen, ownerType, member, method, targetAssemblyName, maxEvidence, ref evidenceTruncated, ref reflectionErrors);
            }
        }

        static void AddMethodBaseReferenceEvidence(List<ReferenceTraceEvidence> evidence, HashSet<string> seen, string ownerType, string member, MethodBase method, string targetAssemblyName, int maxEvidence, ref bool evidenceTruncated, ref int reflectionErrors)
        {
            foreach (var parameter in SafeGetParameters(method, ref reflectionErrors))
            {
                AddTypeReferenceEvidence(evidence, seen, "MemberRef", "member_ref", ownerType, member, "parameter_type:" + parameter.Name, parameter.ParameterType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
                AddCustomAttributeReferenceEvidence(evidence, seen, ownerType, member, "parameter_attribute:" + parameter.Name, SafeGetCustomAttributes(parameter, ref reflectionErrors), targetAssemblyName, maxEvidence, ref evidenceTruncated);
            }

            AddGenericConstraintEvidence(evidence, seen, "MemberRef", "member_ref", ownerType, member, "generic_constraint", method.IsGenericMethod ? method.GetGenericArguments() : new Type[0], targetAssemblyName, maxEvidence, ref evidenceTruncated, ref reflectionErrors);
            AddCustomAttributeReferenceEvidence(evidence, seen, ownerType, member, "member_attribute", SafeGetCustomAttributes(method, ref reflectionErrors), targetAssemblyName, maxEvidence, ref evidenceTruncated);
        }

        static void AddGenericConstraintEvidence(List<ReferenceTraceEvidence> evidence, HashSet<string> seen, string metadataKind, string evidenceKind, string ownerType, string member, string referenceSource, Type[] genericArguments, string targetAssemblyName, int maxEvidence, ref bool evidenceTruncated, ref int reflectionErrors)
        {
            foreach (var argument in genericArguments)
            {
                if (!argument.IsGenericParameter)
                {
                    continue;
                }

                try
                {
                    foreach (var constraint in argument.GetGenericParameterConstraints())
                    {
                        AddTypeReferenceEvidence(evidence, seen, metadataKind, evidenceKind, ownerType, member, referenceSource + ":" + argument.Name, constraint, targetAssemblyName, maxEvidence, ref evidenceTruncated);
                    }
                }
                catch (Exception)
                {
                    reflectionErrors++;
                }
            }
        }

        static void AddTypeReferenceEvidence(List<ReferenceTraceEvidence> evidence, HashSet<string> seen, string metadataKind, string evidenceKind, string ownerType, string member, string referenceSource, Type type, string targetAssemblyName, int maxEvidence, ref bool evidenceTruncated)
        {
            AddTypeReferenceEvidence(evidence, seen, metadataKind, evidenceKind, ownerType, member, referenceSource, type, targetAssemblyName, maxEvidence, ref evidenceTruncated, 0);
        }

        static void AddTypeReferenceEvidence(List<ReferenceTraceEvidence> evidence, HashSet<string> seen, string metadataKind, string evidenceKind, string ownerType, string member, string referenceSource, Type type, string targetAssemblyName, int maxEvidence, ref bool evidenceTruncated, int depth)
        {
            if (type == null || depth > 4)
            {
                return;
            }

            var normalized = NormalizeReflectionReferenceType(type);
            if (normalized != null && string.Equals(normalized.Assembly.GetName().Name ?? string.Empty, targetAssemblyName, StringComparison.Ordinal))
            {
                AddReferenceTraceEvidence(evidence, seen, new ReferenceTraceEvidence(metadataKind, evidenceKind, ownerType, member, referenceSource, ReflectionTypeName(normalized), targetAssemblyName), maxEvidence, ref evidenceTruncated);
            }

            if (type.HasElementType)
            {
                AddTypeReferenceEvidence(evidence, seen, metadataKind, evidenceKind, ownerType, member, referenceSource + ":element", type.GetElementType(), targetAssemblyName, maxEvidence, ref evidenceTruncated, depth + 1);
            }

            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    AddTypeReferenceEvidence(evidence, seen, metadataKind, evidenceKind, ownerType, member, referenceSource + ":generic_argument", argument, targetAssemblyName, maxEvidence, ref evidenceTruncated, depth + 1);
                }
            }
        }

        static Type NormalizeReflectionReferenceType(Type type)
        {
            var current = type;
            while (current != null && current.HasElementType)
            {
                current = current.GetElementType();
            }

            return current != null && !current.IsGenericParameter ? current : null;
        }

        static void AddCustomAttributeReferenceEvidence(List<ReferenceTraceEvidence> evidence, HashSet<string> seen, string ownerType, string member, string referenceSource, IList<CustomAttributeData> attributes, string targetAssemblyName, int maxEvidence, ref bool evidenceTruncated)
        {
            foreach (var attribute in attributes)
            {
                AddTypeReferenceEvidence(evidence, seen, "CustomAttribute", "custom_attribute", ownerType, member, referenceSource, attribute.AttributeType, targetAssemblyName, maxEvidence, ref evidenceTruncated);
            }
        }

        static void AddReferenceTraceEvidence(List<ReferenceTraceEvidence> evidence, HashSet<string> seen, ReferenceTraceEvidence item, int maxEvidence, ref bool evidenceTruncated)
        {
            var key = item.MetadataKind + "|" + item.EvidenceKind + "|" + item.OwnerType + "|" + item.Member + "|" + item.ReferenceSource + "|" + item.ReferencedType;
            if (!seen.Add(key))
            {
                return;
            }

            if (evidence.Count >= maxEvidence)
            {
                evidenceTruncated = true;
                return;
            }

            evidence.Add(item);
        }

        static List<string> BuildReferenceTraceSourceHints(ResolvedAssemblyInput source, List<ReferenceTraceEvidence> evidence, int maxSourceHints, int maxSourceFiles, out int scannedSourceFiles, out int matchedSourceHints, out bool scanTruncated)
        {
            scannedSourceFiles = 0;
            matchedSourceHints = 0;
            scanTruncated = false;
            var hints = new List<string>();
            if (maxSourceHints == 0 || maxSourceFiles == 0 || evidence.Count == 0)
            {
                return hints;
            }

            var ownerTypes = ReferenceTraceOwnerTypes(evidence);
            var assemblies = UnityEditor.Compilation.CompilationPipeline.GetAssemblies();
            Array.Sort(assemblies, CompareCompilationAssemblies);
            var compilationAssembly = FindCompilationAssemblyByName(assemblies, source.Entry.AssemblyName);
            var sources = compilationAssembly == null ? new List<string>() : NormalizeCompilationSourceFiles(compilationAssembly.sourceFiles);
            var seenHints = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sourcePath in sources)
            {
                if (scannedSourceFiles >= maxSourceFiles)
                {
                    scanTruncated = true;
                    break;
                }

                scannedSourceFiles++;
                if (!TryResolveUnityPath(sourcePath, true, out var fullPath))
                {
                    continue;
                }

                foreach (var declaredType in ExtractDeclaredTypes(ReadUtf8Prefix(fullPath, MaxTypeResolveScriptBytes)))
                {
                    foreach (var ownerType in ownerTypes)
                    {
                        var matchKind = ReferenceTraceTypeHintMatchKind(ownerType, declaredType);
                        if (string.IsNullOrEmpty(matchKind))
                        {
                            continue;
                        }

                        var key = ownerType + "|" + sourcePath + "|" + declaredType.FullName;
                        if (!seenHints.Add(key))
                        {
                            continue;
                        }

                        matchedSourceHints++;
                        if (hints.Count < maxSourceHints)
                        {
                            hints.Add("{\"owner_type\":" + JsonUtil.String(ownerType)
                                + ",\"source_path\":" + JsonUtil.String(sourcePath)
                                + ",\"match_kind\":" + JsonUtil.String(matchKind)
                                + ",\"declared_type\":" + declaredType.ToJson() + "}");
                        }
                    }
                }
            }

            return hints;
        }

        static List<string> ReferenceTraceOwnerTypes(List<ReferenceTraceEvidence> evidence)
        {
            var ownerTypes = new List<string>();
            foreach (var item in evidence)
            {
                AddUnique(ownerTypes, item.OwnerType);
            }

            return ownerTypes;
        }

        static string ReferenceTraceTypeHintMatchKind(string ownerType, DeclaredType declaredType)
        {
            var normalizedOwner = NormalizeReferenceTraceTypeName(ownerType);
            if (string.Equals(normalizedOwner, declaredType.FullName, StringComparison.Ordinal))
            {
                return "declared_full_name";
            }

            var shortName = ReferenceTraceShortTypeName(normalizedOwner);
            return string.Equals(shortName, declaredType.Name, StringComparison.Ordinal) ? "declared_type_name" : string.Empty;
        }

        static string NormalizeReferenceTraceTypeName(string typeName)
        {
            var value = string.IsNullOrEmpty(typeName) ? string.Empty : typeName.Replace('+', '.');
            var genericMarker = value.IndexOf('`');
            if (genericMarker >= 0)
            {
                value = value.Substring(0, genericMarker);
            }

            var genericArguments = value.IndexOf('[');
            if (genericArguments >= 0)
            {
                value = value.Substring(0, genericArguments);
            }

            return value;
        }

        static string ReferenceTraceShortTypeName(string typeName)
        {
            var separator = typeName.LastIndexOf('.');
            return separator < 0 ? typeName : typeName.Substring(separator + 1);
        }

        static List<string> BuildReferenceTraceWarnings(Assembly sourceAssembly, AssemblyName metadataReference, ReferenceExplanation explanation, bool evidenceTruncated, bool typesTruncated, bool sourceHintScanTruncated, int reflectionErrors)
        {
            var warnings = new List<string>
            {
                "This command is metadata-only; it does not compile, import, refresh, shell out, use network access, or write files.",
                "TypeRef, MemberRef, and CustomAttribute evidence comes from loaded AppDomain reflection metadata and may miss compiler-generated or stripped metadata."
            };

            if (sourceAssembly == null)
            {
                warnings.Add("source assembly is not loaded in the current AppDomain; TypeRef/MemberRef/CustomAttribute evidence is unavailable.");
            }
            else if (metadataReference == null)
            {
                warnings.Add("source loaded assembly does not declare the target assembly in Assembly.GetReferencedAssemblies().");
            }

            if (!explanation.CanReference)
            {
                warnings.Add("local asmdef/generated rules do not prove that the source can reference the target; inspect reference_rule for required action.");
            }

            if (evidenceTruncated)
            {
                warnings.Add("reference evidence was truncated by max_evidence.");
            }

            if (typesTruncated)
            {
                warnings.Add("type scan was truncated by max_types.");
            }

            if (sourceHintScanTruncated)
            {
                warnings.Add("source hint scan was truncated by max_source_files or max_source_hints.");
            }

            if (reflectionErrors > 0)
            {
                warnings.Add("some reflection metadata could not be read; evidence is best-effort.");
            }

            return warnings;
        }

        static string ReferenceTraceEvidenceJson(List<ReferenceTraceEvidence> evidence)
        {
            var values = new List<string>();
            foreach (var item in evidence)
            {
                values.Add(item.ToJson());
            }

            return string.Join(",", values.ToArray());
        }

        static string ReflectionTypeName(Type type)
        {
            return Bounded(type == null ? string.Empty : (type.FullName ?? type.Name), 256);
        }

        static Type[] SafeGetInterfaces(Type type, ref int reflectionErrors)
        {
            try
            {
                var values = type.GetInterfaces();
                Array.Sort(values, CompareTypes);
                return values;
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new Type[0];
            }
        }

        static FieldInfo[] SafeGetFields(Type type, BindingFlags flags, ref int reflectionErrors)
        {
            try
            {
                var values = type.GetFields(flags);
                Array.Sort(values, CompareMembers);
                return values;
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new FieldInfo[0];
            }
        }

        static PropertyInfo[] SafeGetProperties(Type type, BindingFlags flags, ref int reflectionErrors)
        {
            try
            {
                var values = type.GetProperties(flags);
                Array.Sort(values, CompareMembers);
                return values;
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new PropertyInfo[0];
            }
        }

        static EventInfo[] SafeGetEvents(Type type, BindingFlags flags, ref int reflectionErrors)
        {
            try
            {
                var values = type.GetEvents(flags);
                Array.Sort(values, CompareMembers);
                return values;
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new EventInfo[0];
            }
        }

        static ConstructorInfo[] SafeGetConstructors(Type type, BindingFlags flags, ref int reflectionErrors)
        {
            try
            {
                var values = type.GetConstructors(flags);
                Array.Sort(values, CompareMembers);
                return values;
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new ConstructorInfo[0];
            }
        }

        static MethodInfo[] SafeGetMethods(Type type, BindingFlags flags, ref int reflectionErrors)
        {
            try
            {
                var values = type.GetMethods(flags);
                Array.Sort(values, CompareMembers);
                return values;
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new MethodInfo[0];
            }
        }

        static ParameterInfo[] SafeGetParameters(MethodBase method, ref int reflectionErrors)
        {
            try
            {
                return method.GetParameters();
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new ParameterInfo[0];
            }
        }

        static ParameterInfo[] SafeGetIndexParameters(PropertyInfo property, ref int reflectionErrors)
        {
            try
            {
                return property.GetIndexParameters();
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new ParameterInfo[0];
            }
        }

        static IList<CustomAttributeData> SafeGetCustomAttributes(Type type, ref int reflectionErrors)
        {
            try
            {
                return CustomAttributeData.GetCustomAttributes(type);
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new List<CustomAttributeData>();
            }
        }

        static IList<CustomAttributeData> SafeGetCustomAttributes(MemberInfo member, ref int reflectionErrors)
        {
            try
            {
                return CustomAttributeData.GetCustomAttributes(member);
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new List<CustomAttributeData>();
            }
        }

        static IList<CustomAttributeData> SafeGetCustomAttributes(ParameterInfo parameter, ref int reflectionErrors)
        {
            try
            {
                return CustomAttributeData.GetCustomAttributes(parameter);
            }
            catch (Exception)
            {
                reflectionErrors++;
                return new List<CustomAttributeData>();
            }
        }

        static int CompareMembers(MemberInfo left, MemberInfo right)
        {
            var byName = string.Compare(left.Name, right.Name, StringComparison.Ordinal);
            if (byName != 0)
            {
                return byName;
            }

            return string.Compare(left.MemberType.ToString(), right.MemberType.ToString(), StringComparison.Ordinal);
        }

        static List<string> FindScriptTypeCandidates(string typeName, int maxResults, int maxScanFiles, out int scannedScripts, out int matchedScripts, out bool scanTruncated)
        {
            scannedScripts = 0;
            matchedScripts = 0;
            scanTruncated = false;
            var candidates = new List<string>();
            var guids = UnityEditor.AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets", "Packages" });
            var paths = new List<string>();
            foreach (var guid in guids)
            {
                var path = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    paths.Add(path);
                }
            }

            paths.Sort(StringComparer.Ordinal);
            foreach (var scriptPath in paths)
            {
                if (scannedScripts >= maxScanFiles)
                {
                    scanTruncated = true;
                    break;
                }

                scannedScripts++;
                if (!TryResolveUnityPath(scriptPath, true, out var fullPath))
                {
                    continue;
                }

                var declaredTypes = ExtractDeclaredTypes(ReadUtf8Prefix(fullPath, MaxTypeResolveScriptBytes));
                foreach (var declaredType in declaredTypes)
                {
                    if (!TypeQueryMatches(declaredType, typeName))
                    {
                        continue;
                    }

                    matchedScripts++;
                    if (candidates.Count < maxResults)
                    {
                        var prediction = PredictAssembly(scriptPath, IsUnderEditorFolder(scriptPath));
                        candidates.Add("{\"script_path\":" + JsonUtil.String(scriptPath)
                            + ",\"declared_type\":" + declaredType.ToJson()
                            + ",\"assembly\":" + prediction.ToJson() + "}");
                    }
                }
            }

            return candidates;
        }

        static bool TypeQueryMatches(DeclaredType declaredType, string typeName)
        {
            return string.Equals(declaredType.FullName, typeName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(declaredType.Name, typeName, StringComparison.OrdinalIgnoreCase);
        }

        static List<AssemblyListEntry> FindAsmdefAssemblyEntries(bool includePackages, out int scannedAsmdefs, out bool scanTruncated)
        {
            var entries = new List<AssemblyListEntry>();
            scannedAsmdefs = 0;
            scanTruncated = false;
            if (TryResolveUnityPath("Assets", false, out var assetsRoot))
            {
                ScanAsmdefsUnder(assetsRoot, "Assets", entries, ref scannedAsmdefs, ref scanTruncated);
            }

            if (includePackages)
            {
                foreach (var package in PackageInfo.GetAllRegisteredPackages())
                {
                    if (scanTruncated)
                    {
                        break;
                    }

                    if (string.IsNullOrEmpty(package.resolvedPath))
                    {
                        continue;
                    }

                    var packageRoot = Path.GetFullPath(package.resolvedPath);
                    if (!Directory.Exists(packageRoot))
                    {
                        continue;
                    }

                    ScanAsmdefsUnder(packageRoot, "Packages/" + package.name, entries, ref scannedAsmdefs, ref scanTruncated);
                }
            }

            entries.Sort(CompareAssemblyListEntries);
            return entries;
        }

        static void ScanAsmdefsUnder(string fullRoot, string assetRoot, List<AssemblyListEntry> entries, ref int scannedAsmdefs, ref bool scanTruncated)
        {
            try
            {
                foreach (var rawPath in Directory.EnumerateFiles(fullRoot, "*.asmdef", SearchOption.AllDirectories))
                {
                    if (scannedAsmdefs >= MaxAsmdefFiles)
                    {
                        scanTruncated = true;
                        return;
                    }

                    var fullPath = Path.GetFullPath(rawPath);
                    if (!IsSameOrChildPath(fullPath, fullRoot))
                    {
                        continue;
                    }

                    var asmdefPath = UnityAssetPathFromFullPath(fullPath, fullRoot, assetRoot);
                    var content = ReadUtf8Prefix(fullPath, MaxAsmdefBytes);
                    entries.Add(new AssemblyListEntry(asmdefPath, ReadAsmdefName(asmdefPath, content), "asmdef", false, IsEditorOnlyAsmdef(content) || IsUnderEditorFolder(asmdefPath), "asmdef"));
                    scannedAsmdefs++;
                }
            }
            catch (IOException)
            {
                scanTruncated = true;
            }
            catch (UnauthorizedAccessException)
            {
                scanTruncated = true;
            }
        }

        static string UnityAssetPathFromFullPath(string fullPath, string fullRoot, string assetRoot)
        {
            var root = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = fullPath.Length <= root.Length ? string.Empty : fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrEmpty(relative) ? assetRoot : assetRoot + "/" + relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        static int CompareAssemblyListEntries(AssemblyListEntry left, AssemblyListEntry right)
        {
            var byName = string.Compare(left.AssemblyName, right.AssemblyName, StringComparison.Ordinal);
            return byName != 0 ? byName : string.Compare(left.AsmdefPath, right.AsmdefPath, StringComparison.Ordinal);
        }

        static void AddAssemblyListResult(List<string> results, string query, int maxResults, ref int matched, AssemblyListEntry entry)
        {
            if (!AssemblyListMatches(entry, query))
            {
                return;
            }

            matched++;
            if (results.Count < maxResults)
            {
                results.Add(entry.ToJson());
            }
        }

        static int CompareCompilationAssemblies(UnityEditor.Compilation.Assembly left, UnityEditor.Compilation.Assembly right)
        {
            return string.Compare(left == null ? string.Empty : left.name, right == null ? string.Empty : right.name, StringComparison.Ordinal);
        }

        static List<string> NormalizeCompilationSourceFiles(string[] rawSourceFiles)
        {
            var sources = new List<string>();
            if (rawSourceFiles == null)
            {
                return sources;
            }

            foreach (var rawSourceFile in rawSourceFiles)
            {
                var sourcePath = UnityAssetPathFromCompilerPath(rawSourceFile);
                if (string.IsNullOrEmpty(sourcePath) || !sourcePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!sources.Contains(sourcePath))
                {
                    sources.Add(sourcePath);
                }
            }

            sources.Sort(StringComparer.Ordinal);
            return sources;
        }

        static string UnityAssetPathFromCompilerPath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                return string.Empty;
            }

            var normalized = rawPath.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal) || normalized.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return normalized;
            }

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(rawPath);
            }
            catch (ArgumentException)
            {
                return string.Empty;
            }
            catch (NotSupportedException)
            {
                return string.Empty;
            }

            foreach (var package in PackageInfo.GetAllRegisteredPackages())
            {
                if (string.IsNullOrEmpty(package.name) || string.IsNullOrEmpty(package.resolvedPath))
                {
                    continue;
                }

                var packageRoot = Path.GetFullPath(package.resolvedPath);
                if (!IsSameOrChildPath(fullPath, packageRoot))
                {
                    continue;
                }

                var relative = fullPath.Substring(packageRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return string.IsNullOrEmpty(relative) ? "Packages/" + package.name : "Packages/" + package.name + "/" + relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            }

            var projectRoot = ProjectPaths.ProjectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!IsSameOrChildPath(fullPath, projectRoot))
            {
                return string.Empty;
            }

            var projectRelative = fullPath.Substring(projectRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
            return projectRelative.StartsWith("Assets/", StringComparison.Ordinal) || projectRelative.StartsWith("Packages/", StringComparison.Ordinal) ? projectRelative : string.Empty;
        }

        static bool SourcesArePackagesOnly(List<string> sources)
        {
            if (sources.Count == 0)
            {
                return false;
            }

            foreach (var source in sources)
            {
                if (!source.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    return false;
                }
            }

            return true;
        }

        static bool AssemblySourcesMatch(string assemblyName, List<string> sources, string query)
        {
            if (Contains(assemblyName, query))
            {
                return true;
            }

            foreach (var source in sources)
            {
                if (Contains(source, query))
                {
                    return true;
                }
            }

            return false;
        }

        static string AssemblySourcesJson(UnityEditor.Compilation.Assembly assembly, List<string> sources, int maxSourcesPerAssembly)
        {
            var returned = Math.Min(sources.Count, maxSourcesPerAssembly);
            var projectSourceCount = 0;
            var packageSourceCount = 0;
            for (var index = 0; index < sources.Count; index++)
            {
                if (sources[index].StartsWith("Packages/", StringComparison.Ordinal))
                {
                    packageSourceCount++;
                }
                else if (sources[index].StartsWith("Assets/", StringComparison.Ordinal))
                {
                    projectSourceCount++;
                }
            }

            var returnedSources = new List<string>();
            for (var index = 0; index < returned; index++)
            {
                returnedSources.Add(sources[index]);
            }

            return "{\"assembly_name\":" + JsonUtil.String(assembly == null ? string.Empty : assembly.name)
                + ",\"source_count\":" + sources.Count
                + ",\"project_source_count\":" + projectSourceCount
                + ",\"package_source_count\":" + packageSourceCount
                + ",\"returned_source_count\":" + returned
                + ",\"sources_truncated\":" + JsonUtil.Bool(sources.Count > returned)
                + ",\"sources\":[" + StringArrayJson(returnedSources) + "]}";
        }

        static List<AssemblyGraphNode> BuildAssemblyGraphNodes(bool includePackages, out int scannedAsmdefs, out bool scanTruncated)
        {
            var nodes = new List<AssemblyGraphNode>();
            nodes.Add(new AssemblyGraphNode(new AssemblyListEntry(string.Empty, "Assembly-CSharp", "auto", true, false, "runtime_folder_without_asmdef"), string.Empty, string.Empty));
            nodes.Add(new AssemblyGraphNode(new AssemblyListEntry(string.Empty, "Assembly-CSharp-Editor", "auto", true, true, "editor_folder_without_asmdef"), string.Empty, string.Empty));
            foreach (var entry in FindAsmdefAssemblyEntries(includePackages, out scannedAsmdefs, out scanTruncated))
            {
                var content = LoadAsmdefContent(entry);
                var guid = string.IsNullOrEmpty(entry.AsmdefPath) ? string.Empty : UnityEditor.AssetDatabase.AssetPathToGUID(entry.AsmdefPath);
                nodes.Add(new AssemblyGraphNode(entry, content, guid));
            }

            nodes.Sort(CompareAssemblyGraphNodes);
            return nodes;
        }

        static List<AssemblyGraphEdge> BuildAssemblyGraphEdges(List<AssemblyGraphNode> nodes)
        {
            var edges = new List<AssemblyGraphEdge>();
            var runtimePredefined = FindAssemblyGraphNode(nodes, "Assembly-CSharp");
            var editorPredefined = FindAssemblyGraphNode(nodes, "Assembly-CSharp-Editor");
            if (runtimePredefined != null && editorPredefined != null)
            {
                AddAssemblyGraphEdge(edges, editorPredefined, runtimePredefined, "implicit_generated_reference", "predefined", string.Empty);
            }

            foreach (var source in nodes)
            {
                if (source.Entry.Kind == "auto")
                {
                    foreach (var target in nodes)
                    {
                        if (target.Entry.Kind != "asmdef" || !target.AutoReferenced || (!source.Entry.EditorOnly && target.Entry.EditorOnly))
                        {
                            continue;
                        }

                        AddAssemblyGraphEdge(edges, source, target, "implicit_generated_reference", "autoReferenced", string.Empty);
                    }
                }

                if (source.Entry.Kind != "asmdef")
                {
                    continue;
                }

                foreach (var reference in ReadAsmdefStringArray(source.AsmdefContent, "references", 200))
                {
                    if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                    {
                        var guid = reference.Substring("GUID:".Length);
                        foreach (var target in FindAssemblyGraphNodesByGuid(nodes, guid))
                        {
                            AddAssemblyGraphEdge(edges, source, target, "explicit_asmdef_reference", "guid", reference);
                        }
                    }
                    else
                    {
                        foreach (var target in FindAssemblyGraphNodesByName(nodes, reference))
                        {
                            AddAssemblyGraphEdge(edges, source, target, "explicit_asmdef_reference", "name", reference);
                        }
                    }
                }
            }

            edges.Sort(CompareAssemblyGraphEdges);
            return edges;
        }

        static List<AssemblyGraphNode> SelectAssemblyGraphNodes(List<AssemblyGraphNode> nodes, List<AssemblyGraphEdge> edges, string query)
        {
            var selected = new List<AssemblyGraphNode>();
            foreach (var node in nodes)
            {
                if (AssemblyGraphNodeMatches(node, query))
                {
                    AddAssemblyGraphNode(selected, node);
                }
            }

            foreach (var edge in edges)
            {
                if (AssemblyGraphEdgeQueryMatches(edge, query))
                {
                    AddAssemblyGraphNode(selected, edge.Source);
                    AddAssemblyGraphNode(selected, edge.Target);
                }
            }

            return selected;
        }

        static void AddAssemblyGraphNode(List<AssemblyGraphNode> nodes, AssemblyGraphNode node)
        {
            foreach (var existing in nodes)
            {
                if (string.Equals(existing.Id, node.Id, StringComparison.Ordinal))
                {
                    return;
                }
            }

            nodes.Add(node);
        }

        static int CompareAssemblyGraphNodes(AssemblyGraphNode left, AssemblyGraphNode right)
        {
            var byName = string.Compare(left.Entry.AssemblyName, right.Entry.AssemblyName, StringComparison.Ordinal);
            return byName != 0 ? byName : string.Compare(left.Id, right.Id, StringComparison.Ordinal);
        }

        static int CompareAssemblyGraphEdges(AssemblyGraphEdge left, AssemblyGraphEdge right)
        {
            var bySource = string.Compare(left.Source.Id, right.Source.Id, StringComparison.Ordinal);
            if (bySource != 0)
            {
                return bySource;
            }

            var byTarget = string.Compare(left.Target.Id, right.Target.Id, StringComparison.Ordinal);
            return byTarget != 0 ? byTarget : string.Compare(left.ReferenceValue, right.ReferenceValue, StringComparison.Ordinal);
        }

        static AssemblyGraphNode FindAssemblyGraphNode(List<AssemblyGraphNode> nodes, string assemblyName)
        {
            foreach (var node in nodes)
            {
                if (string.Equals(node.Entry.AssemblyName, assemblyName, StringComparison.Ordinal))
                {
                    return node;
                }
            }

            return null;
        }

        static List<AssemblyGraphNode> FindAssemblyGraphNodesByName(List<AssemblyGraphNode> nodes, string assemblyName)
        {
            var matches = new List<AssemblyGraphNode>();
            foreach (var node in nodes)
            {
                if (string.Equals(node.Entry.AssemblyName, assemblyName, StringComparison.Ordinal))
                {
                    matches.Add(node);
                }
            }

            return matches;
        }

        static List<AssemblyGraphNode> FindAssemblyGraphNodesByGuid(List<AssemblyGraphNode> nodes, string guid)
        {
            var matches = new List<AssemblyGraphNode>();
            foreach (var node in nodes)
            {
                if (!string.IsNullOrEmpty(node.Guid) && string.Equals(node.Guid, guid, StringComparison.OrdinalIgnoreCase))
                {
                    matches.Add(node);
                }
            }

            return matches;
        }

        static void AddAssemblyGraphEdge(List<AssemblyGraphEdge> edges, AssemblyGraphNode source, AssemblyGraphNode target, string referenceKind, string referenceBy, string referenceValue)
        {
            if (source == null || target == null || string.Equals(source.Id, target.Id, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var edge in edges)
            {
                if (string.Equals(edge.Source.Id, source.Id, StringComparison.Ordinal)
                    && string.Equals(edge.Target.Id, target.Id, StringComparison.Ordinal)
                    && string.Equals(edge.ReferenceKind, referenceKind, StringComparison.Ordinal)
                    && string.Equals(edge.ReferenceBy, referenceBy, StringComparison.Ordinal)
                    && string.Equals(edge.ReferenceValue, referenceValue, StringComparison.Ordinal))
                {
                    return;
                }
            }

            edges.Add(new AssemblyGraphEdge(source, target, referenceKind, referenceBy, referenceValue));
        }

        static bool AssemblyGraphNodeMatches(AssemblyGraphNode node, string query)
        {
            return Contains(node.Id, query)
                || Contains(node.Entry.AssemblyName, query)
                || Contains(node.Entry.AsmdefPath, query)
                || Contains(node.Entry.Kind, query)
                || Contains(node.Entry.Reason, query);
        }

        static bool AssemblyGraphEdgeMatches(AssemblyGraphEdge edge, List<AssemblyGraphNode> returnedNodes, string query)
        {
            if (!ContainsAssemblyGraphNode(returnedNodes, edge.Source) || !ContainsAssemblyGraphNode(returnedNodes, edge.Target))
            {
                return false;
            }

            return AssemblyGraphEdgeQueryMatches(edge, query);
        }

        static bool AssemblyGraphEdgeQueryMatches(AssemblyGraphEdge edge, string query)
        {
            return Contains(edge.Source.Entry.AssemblyName, query)
                || Contains(edge.Target.Entry.AssemblyName, query)
                || Contains(edge.Source.Entry.AsmdefPath, query)
                || Contains(edge.Target.Entry.AsmdefPath, query)
                || Contains(edge.ReferenceKind, query)
                || Contains(edge.ReferenceBy, query)
                || Contains(edge.ReferenceValue, query);
        }

        static bool ContainsAssemblyGraphNode(List<AssemblyGraphNode> nodes, AssemblyGraphNode expected)
        {
            foreach (var node in nodes)
            {
                if (string.Equals(node.Id, expected.Id, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static string AssemblyGraphNodesJson(List<AssemblyGraphNode> nodes)
        {
            var values = new List<string>();
            foreach (var node in nodes)
            {
                values.Add(node.ToJson());
            }

            return string.Join(",", values.ToArray());
        }

        static string AssemblyGraphEdgesJson(List<AssemblyGraphEdge> edges)
        {
            var values = new List<string>();
            foreach (var edge in edges)
            {
                values.Add(edge.ToJson());
            }

            return string.Join(",", values.ToArray());
        }

        static List<string> FindProjectRootCsprojFiles(out int scannedCsprojFiles, out bool scanTruncated)
        {
            scannedCsprojFiles = 0;
            scanTruncated = false;
            var files = new List<string>();
            try
            {
                var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
                var rawFiles = Directory.GetFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly);
                Array.Sort(rawFiles, StringComparer.Ordinal);
                foreach (var rawFile in rawFiles)
                {
                    if (scannedCsprojFiles >= MaxCsprojFiles)
                    {
                        scanTruncated = true;
                        break;
                    }

                    var fullPath = Path.GetFullPath(rawFile);
                    if (!IsSameOrChildPath(fullPath, projectRoot))
                    {
                        continue;
                    }

                    files.Add(fullPath);
                    scannedCsprojFiles++;
                }
            }
            catch (IOException)
            {
                scanTruncated = true;
            }
            catch (UnauthorizedAccessException)
            {
                scanTruncated = true;
            }

            return files;
        }

        static GeneratedCsprojListEntry BuildGeneratedCsprojListEntry(string fullPath, UnityEditor.Compilation.Assembly[] assemblies, List<AssemblyListEntry> asmdefEntries)
        {
            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var fileInfo = new FileInfo(fullPath);
            var csprojPath = ProjectRelativePathFromFullPath(fullPath, projectRoot);
            var projectName = Path.GetFileNameWithoutExtension(fullPath);
            var assembly = FindCompilationAssemblyByName(assemblies, projectName);
            var sources = assembly == null ? new List<string>() : NormalizeCompilationSourceFiles(assembly.sourceFiles);
            var asmdefEntry = FindAssemblyListEntryInList(asmdefEntries, projectName);
            var kind = "unknown_csproj";
            var reason = "project_root_csproj_without_matching_compilation_assembly";
            var asmdefPath = string.Empty;
            var editorOnly = SourcesContainEditorFolder(sources) || Contains(projectName, "Editor");
            if (string.Equals(projectName, "Assembly-CSharp", StringComparison.Ordinal))
            {
                kind = "generated_runtime";
                reason = "runtime_predefined_project";
                editorOnly = false;
            }
            else if (string.Equals(projectName, "Assembly-CSharp-Editor", StringComparison.Ordinal))
            {
                kind = "generated_editor";
                reason = "editor_predefined_project";
                editorOnly = true;
            }
            else if (asmdefEntry != null)
            {
                kind = "asmdef";
                reason = "asmdef_project";
                asmdefPath = asmdefEntry.AsmdefPath;
                editorOnly = asmdefEntry.EditorOnly;
            }
            else if (assembly != null)
            {
                kind = "compilation_assembly";
                reason = "compilation_pipeline_project";
            }

            CountSourceRoots(sources, out var projectSourceCount, out var packageSourceCount);
            return new GeneratedCsprojListEntry(
                csprojPath,
                Path.GetFileName(fullPath),
                projectName,
                assembly == null ? projectName : assembly.name,
                kind,
                reason,
                asmdefPath,
                assembly != null,
                !string.Equals(kind, "unknown_csproj", StringComparison.Ordinal),
                editorOnly,
                sources.Count,
                projectSourceCount,
                packageSourceCount,
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.ToString("o"));
        }

        static string ProjectRelativePathFromFullPath(string fullPath, string projectRoot)
        {
            var root = projectRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = fullPath.Length <= root.Length ? string.Empty : fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        static UnityEditor.Compilation.Assembly FindCompilationAssemblyByName(UnityEditor.Compilation.Assembly[] assemblies, string assemblyName)
        {
            foreach (var assembly in assemblies)
            {
                if (assembly != null && string.Equals(assembly.name, assemblyName, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }

            return null;
        }

        static AssemblyListEntry FindAssemblyListEntryInList(List<AssemblyListEntry> entries, string assemblyName)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.AssemblyName, assemblyName, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        static bool SourcesContainEditorFolder(List<string> sources)
        {
            foreach (var source in sources)
            {
                if (IsUnderEditorFolder(source))
                {
                    return true;
                }
            }

            return false;
        }

        static void CountSourceRoots(List<string> sources, out int projectSourceCount, out int packageSourceCount)
        {
            projectSourceCount = 0;
            packageSourceCount = 0;
            foreach (var source in sources)
            {
                if (source.StartsWith("Packages/", StringComparison.Ordinal))
                {
                    packageSourceCount++;
                }
                else if (source.StartsWith("Assets/", StringComparison.Ordinal))
                {
                    projectSourceCount++;
                }
            }
        }

        static bool GeneratedCsprojListMatches(GeneratedCsprojListEntry entry, string query)
        {
            return Contains(entry.CsprojPath, query)
                || Contains(entry.ProjectName, query)
                || Contains(entry.AssemblyName, query)
                || Contains(entry.Kind, query)
                || Contains(entry.AsmdefPath, query)
                || Contains(entry.Reason, query);
        }

        static bool TryResolveProjectRootCsproj(string rawCsprojPath, string rawProjectName, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            var fileName = string.Empty;
            if (!string.IsNullOrWhiteSpace(rawCsprojPath))
            {
                fileName = rawCsprojPath.Trim().Replace('\\', '/');
                if (!IsProjectRootFileName(fileName) || !fileName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    error = "csproj_path must be a project-root .csproj file name";
                    return false;
                }
            }
            else
            {
                var projectName = rawProjectName.Trim().Replace('\\', '/');
                if (projectName.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    projectName = Path.GetFileNameWithoutExtension(projectName);
                }

                if (!IsProjectRootFileName(projectName))
                {
                    error = "project_name must be a project-root generated project name without path separators";
                    return false;
                }

                fileName = projectName + ".csproj";
            }

            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var candidate = Path.GetFullPath(Path.Combine(projectRoot, fileName));
            if (!IsSameOrChildPath(candidate, projectRoot) || !string.Equals(Path.GetDirectoryName(candidate), projectRoot, StringComparison.OrdinalIgnoreCase))
            {
                error = "csproj selector must resolve under the Unity project root";
                return false;
            }

            if (!File.Exists(candidate))
            {
                error = "csproj file was not found in the Unity project root";
                return false;
            }

            fullPath = candidate;
            return true;
        }

        static bool IsProjectRootFileName(string value)
        {
            return !string.IsNullOrWhiteSpace(value)
                && !LooksRootedPath(value)
                && value.IndexOf('/') < 0
                && value.IndexOf("..", StringComparison.Ordinal) < 0
                && string.Equals(Path.GetFileName(value), value, StringComparison.Ordinal);
        }

        static List<string> ReadCsprojPathItems(string content, string elementName, int maxItems, CsprojPathItemKind itemKind, out int matched, out int redacted)
        {
            matched = 0;
            redacted = 0;
            var values = new List<string>();
            foreach (Match match in Regex.Matches(content, "<" + Regex.Escape(elementName) + "\\b[^>]*\\bInclude\\s*=\\s*([\\\"'])(.*?)\\1", RegexOptions.Singleline, TimeSpan.FromMilliseconds(500)))
            {
                matched++;
                if (values.Count >= maxItems)
                {
                    continue;
                }

                var rawValue = DecodeXmlText(match.Groups[2].Value);
                var normalized = itemKind == CsprojPathItemKind.ProjectReference ? NormalizeCsprojProjectReferencePath(rawValue) : NormalizeCsprojAssetPath(rawValue);
                if (string.IsNullOrEmpty(normalized))
                {
                    redacted++;
                    continue;
                }

                AddUnique(values, normalized);
            }

            return values;
        }

        static List<string> ReadCsprojReferenceItems(string content, int maxItems, out int matched, out int redacted)
        {
            matched = 0;
            redacted = 0;
            var values = new List<string>();
            foreach (Match match in Regex.Matches(content, "<Reference\\b[^>]*\\bInclude\\s*=\\s*([\\\"'])(.*?)\\1", RegexOptions.Singleline, TimeSpan.FromMilliseconds(500)))
            {
                matched++;
                if (values.Count >= maxItems)
                {
                    continue;
                }

                var normalized = NormalizeCsprojReferenceName(DecodeXmlText(match.Groups[2].Value));
                if (string.IsNullOrEmpty(normalized))
                {
                    redacted++;
                    continue;
                }

                AddUnique(values, normalized);
            }

            return values;
        }

        static List<string> ReadCsprojDefineConstants(string content, int maxItems, out int matched)
        {
            matched = 0;
            var values = new List<string>();
            foreach (Match match in Regex.Matches(content, "<DefineConstants\\b[^>]*>(.*?)</DefineConstants>", RegexOptions.Singleline, TimeSpan.FromMilliseconds(500)))
            {
                var raw = DecodeXmlText(match.Groups[1].Value);
                var parts = raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    var value = NormalizeCsprojDefineConstant(part);
                    if (string.IsNullOrEmpty(value))
                    {
                        continue;
                    }

                    matched++;
                    if (values.Count < maxItems)
                    {
                        AddUnique(values, value);
                    }
                }
            }

            return values;
        }

        static string NormalizeCsprojAssetPath(string rawValue)
        {
            var value = rawValue.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            if (value.StartsWith("Assets/", StringComparison.Ordinal) || value.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return Bounded(value, 512);
            }

            try
            {
                var candidate = LooksRootedPath(value)
                    ? value
                    : Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, value.Replace('/', Path.DirectorySeparatorChar)));
                return Bounded(UnityAssetPathFromCompilerPath(candidate), 512);
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

        static string NormalizeCsprojProjectReferencePath(string rawValue)
        {
            var value = rawValue.Trim().Replace('\\', '/');
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            try
            {
                var candidate = LooksRootedPath(value)
                    ? Path.GetFullPath(value)
                    : Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, value.Replace('/', Path.DirectorySeparatorChar)));
                var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
                if (!candidate.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !IsSameOrChildPath(candidate, projectRoot))
                {
                    return string.Empty;
                }

                return Bounded(ProjectRelativePathFromFullPath(candidate, projectRoot), 512);
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

        static string NormalizeCsprojReferenceName(string rawValue)
        {
            var value = rawValue.Trim();
            if (string.IsNullOrEmpty(value) || value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0 || LooksRootedPath(value))
            {
                return string.Empty;
            }

            var comma = value.IndexOf(',');
            if (comma > 0)
            {
                value = value.Substring(0, comma).Trim();
            }

            return Bounded(value, 256);
        }

        static string NormalizeCsprojDefineConstant(string rawValue)
        {
            var value = rawValue.Trim();
            if (string.IsNullOrEmpty(value) || value.IndexOf('/') >= 0 || value.IndexOf('\\') >= 0 || value.IndexOf(':') >= 0)
            {
                return string.Empty;
            }

            return Bounded(value, 128);
        }

        static string DecodeXmlText(string value)
        {
            return value.Replace("&quot;", "\"")
                .Replace("&apos;", "'")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .Replace("&amp;", "&");
        }

        static bool LooksRootedPath(string value)
        {
            return Path.IsPathRooted(value) || Regex.IsMatch(value, "^[A-Za-z]:[\\\\/]", RegexOptions.None, TimeSpan.FromMilliseconds(250));
        }

        static GeneratedCsprojComparison CompareGeneratedCsprojEntry(GeneratedCsprojListEntry entry, UnityEditor.Compilation.Assembly[] assemblies, List<GeneratedCsprojListEntry> csprojEntries, List<AssemblyGraphEdge> graphEdges, int maxDifferences)
        {
            var fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, entry.CsprojPath.Replace('/', Path.DirectorySeparatorChar)));
            var fileInfo = new FileInfo(fullPath);
            var readBytes = (int)Math.Min(fileInfo.Length, MaxCsprojBytes);
            var content = ReadUtf8Prefix(fullPath, MaxCsprojBytes);
            var compileItems = ReadCsprojPathItems(content, "Compile", MaxCsprojCompareItems, CsprojPathItemKind.Asset, out var compileItemCount, out var compileItemsRedacted);
            var projectReferencePaths = ReadCsprojPathItems(content, "ProjectReference", MaxCsprojCompareItems, CsprojPathItemKind.ProjectReference, out var projectReferenceCount, out var projectReferencesRedacted);
            var projectReferenceNames = CsprojProjectReferenceNames(projectReferencePaths);
            var assembly = FindCompilationAssemblyByName(assemblies, entry.AssemblyName);
            var expectedSources = assembly == null ? new List<string>() : NormalizeCompilationSourceFiles(assembly.sourceFiles);
            var expectedProjectReferences = ExpectedCsprojProjectReferenceNames(entry, csprojEntries, graphEdges);
            var missingCompileItems = DifferenceValues(expectedSources, compileItems, maxDifferences, out var missingCompileItemCount);
            var extraCompileItems = DifferenceValues(compileItems, expectedSources, maxDifferences, out var extraCompileItemCount);
            var missingProjectReferences = DifferenceValues(expectedProjectReferences, projectReferenceNames, maxDifferences, out var missingProjectReferenceCount);
            var extraProjectReferences = DifferenceValues(projectReferenceNames, expectedProjectReferences, maxDifferences, out var extraProjectReferenceCount);
            return new GeneratedCsprojComparison(
                entry,
                fileInfo.Length,
                readBytes,
                fileInfo.Length > readBytes,
                compileItemCount,
                expectedSources.Count,
                compileItemsRedacted,
                compileItemCount > compileItems.Count,
                missingCompileItemCount,
                extraCompileItemCount,
                missingCompileItems,
                extraCompileItems,
                projectReferenceCount,
                expectedProjectReferences.Count,
                projectReferencesRedacted,
                projectReferenceCount > projectReferenceNames.Count,
                missingProjectReferenceCount,
                extraProjectReferenceCount,
                missingProjectReferences,
                extraProjectReferences);
        }

        static List<string> CsprojProjectReferenceNames(List<string> projectReferences)
        {
            var names = new List<string>();
            foreach (var projectReference in projectReferences)
            {
                AddUnique(names, Path.GetFileNameWithoutExtension(projectReference));
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        static List<string> ExpectedCsprojProjectReferenceNames(GeneratedCsprojListEntry entry, List<GeneratedCsprojListEntry> csprojEntries, List<AssemblyGraphEdge> graphEdges)
        {
            var names = new List<string>();
            foreach (var edge in graphEdges)
            {
                if (!string.Equals(edge.Source.Entry.AssemblyName, entry.AssemblyName, StringComparison.Ordinal))
                {
                    continue;
                }

                var target = FindGeneratedCsprojEntryByAssemblyName(csprojEntries, edge.Target.Entry.AssemblyName);
                if (target != null && target.UnityGenerated)
                {
                    AddUnique(names, target.ProjectName);
                }
            }

            names.Sort(StringComparer.Ordinal);
            return names;
        }

        static GeneratedCsprojListEntry FindGeneratedCsprojEntryByAssemblyName(List<GeneratedCsprojListEntry> entries, string assemblyName)
        {
            foreach (var entry in entries)
            {
                if (string.Equals(entry.AssemblyName, assemblyName, StringComparison.Ordinal))
                {
                    return entry;
                }
            }

            return null;
        }

        static List<string> DifferenceValues(List<string> expected, List<string> actual, int maxValues, out int differenceCount)
        {
            differenceCount = 0;
            var values = new List<string>();
            foreach (var value in expected)
            {
                if (ContainsExact(actual, value))
                {
                    continue;
                }

                differenceCount++;
                if (values.Count < maxValues)
                {
                    AddUnique(values, value);
                }
            }

            return values;
        }

        static bool AssemblyListMatches(AssemblyListEntry entry, string query)
        {
            return Contains(entry.AssemblyName, query) || Contains(entry.AsmdefPath, query) || Contains(entry.Reason, query);
        }

        static AssemblyListEntry FindAssemblyListEntryByName(string assemblyName, out bool ambiguous)
        {
            ambiguous = false;
            if (string.Equals(assemblyName, "Assembly-CSharp", StringComparison.Ordinal))
            {
                return new AssemblyListEntry(string.Empty, "Assembly-CSharp", "auto", true, false, "runtime_folder_without_asmdef");
            }

            if (string.Equals(assemblyName, "Assembly-CSharp-Editor", StringComparison.Ordinal))
            {
                return new AssemblyListEntry(string.Empty, "Assembly-CSharp-Editor", "auto", true, true, "editor_folder_without_asmdef");
            }

            var matches = new List<AssemblyListEntry>();
            int ignoredScanned;
            bool ignoredTruncated;
            foreach (var entry in FindAsmdefAssemblyEntries(true, out ignoredScanned, out ignoredTruncated))
            {
                if (string.Equals(entry.AssemblyName, assemblyName, StringComparison.Ordinal))
                {
                    matches.Add(entry);
                }
            }

            if (matches.Count > 1)
            {
                ambiguous = true;
                return null;
            }

            if (matches.Count == 1)
            {
                return matches[0];
            }

            return FindLoadedAssemblyByName(assemblyName) == null ? null : new AssemblyListEntry(string.Empty, assemblyName, "loaded", false, Contains(assemblyName, "Editor"), "loaded_appdomain");
        }

        static AssemblyListEntry AssemblyListEntryFromAsmdefPath(string rawAsmdefPath, out string asmdefContent)
        {
            asmdefContent = string.Empty;
            var asmdefPath = PathGuard.RequireUnityAssetPath(rawAsmdefPath);
            if (!asmdefPath.EndsWith(".asmdef", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("invalid_field_value");
            }

            if (!TryResolveUnityPath(asmdefPath, true, out var fullPath))
            {
                return null;
            }

            asmdefContent = ReadUtf8Prefix(fullPath, MaxAsmdefBytes);
            return new AssemblyListEntry(asmdefPath, ReadAsmdefName(asmdefPath, asmdefContent), "asmdef", false, IsEditorOnlyAsmdef(asmdefContent) || IsUnderEditorFolder(asmdefPath), "asmdef");
        }

        static Assembly FindLoadedAssemblyByName(string assemblyName)
        {
            var assemblies = EditorAssemblyDiscovery.LoadedAssemblies();
            Array.Sort(assemblies, CompareLoadedAssemblies);
            foreach (var assembly in assemblies)
            {
                if (string.Equals(assembly.GetName().Name ?? string.Empty, assemblyName, StringComparison.Ordinal))
                {
                    return assembly;
                }
            }

            return null;
        }

        static int CompareLoadedAssemblies(Assembly left, Assembly right)
        {
            return string.Compare(left.GetName().Name ?? string.Empty, right.GetName().Name ?? string.Empty, StringComparison.Ordinal);
        }

        static int CompareAssemblyNames(AssemblyName left, AssemblyName right)
        {
            return string.Compare(left.Name ?? string.Empty, right.Name ?? string.Empty, StringComparison.Ordinal);
        }

        static int CompareTypes(Type left, Type right)
        {
            return string.Compare(left.FullName ?? left.Name, right.FullName ?? right.Name, StringComparison.Ordinal);
        }

        static string LoadedAssemblyJson(Assembly assembly, int typeCount)
        {
            var name = assembly.GetName();
            var references = assembly.GetReferencedAssemblies();
            Array.Sort(references, CompareAssemblyNames);
            var values = new List<string>();
            for (var index = 0; index < references.Length && index < MaxReferencedAssemblies; index++)
            {
                values.Add(JsonUtil.String(references[index].Name ?? string.Empty));
            }

            return "{\"name\":" + JsonUtil.String(name.Name ?? string.Empty)
                + ",\"version\":" + JsonUtil.String(name.Version == null ? string.Empty : name.Version.ToString())
                + ",\"type_count\":" + typeCount
                + ",\"referenced_assemblies\":[" + string.Join(",", values.ToArray()) + "]"
                + ",\"referenced_assemblies_truncated\":" + JsonUtil.Bool(references.Length > values.Count) + "}";
        }

        static string LoadedTypeJson(Type type)
        {
            return "{\"full_type_name\":" + JsonUtil.String(Bounded(type.FullName ?? type.Name, 256))
                + ",\"type_name\":" + JsonUtil.String(Bounded(type.Name, 128))
                + ",\"namespace\":" + JsonUtil.String(Bounded(type.Namespace ?? string.Empty, 160)) + "}";
        }

        static string AsmdefDetailsJson(string content)
        {
            var references = ReadAsmdefStringArray(content, "references", 200);
            var referenceNames = new List<string>();
            var referenceGuids = new List<string>();
            foreach (var reference in references)
            {
                if (reference.StartsWith("GUID:", StringComparison.OrdinalIgnoreCase))
                {
                    referenceGuids.Add(reference);
                }
                else
                {
                    referenceNames.Add(reference);
                }
            }

            return "{\"name\":" + JsonUtil.String(ReadAsmdefStringField(content, "name"))
                + ",\"root_namespace\":" + JsonUtil.String(ReadAsmdefStringField(content, "rootNamespace"))
                + ",\"references\":[" + StringArrayJson(references) + "]"
                + ",\"reference_names\":[" + StringArrayJson(referenceNames) + "]"
                + ",\"reference_guids\":[" + StringArrayJson(referenceGuids) + "]"
                + ",\"include_platforms\":[" + StringArrayJson(ReadAsmdefStringArray(content, "includePlatforms", 64)) + "]"
                + ",\"exclude_platforms\":[" + StringArrayJson(ReadAsmdefStringArray(content, "excludePlatforms", 64)) + "]"
                + ",\"define_constraints\":[" + StringArrayJson(ReadAsmdefStringArray(content, "defineConstraints", 64)) + "]"
                + ",\"version_defines\":" + CountAsmdefObjects(content, "versionDefines")
                + ",\"optional_unity_references\":[" + StringArrayJson(ReadAsmdefStringArray(content, "optionalUnityReferences", 64)) + "]"
                + ",\"override_references\":" + JsonUtil.Bool(ReadAsmdefBoolField(content, "overrideReferences", false))
                + ",\"precompiled_references\":[" + StringArrayJson(ReadAsmdefStringArray(content, "precompiledReferences", 64)) + "]"
                + ",\"auto_referenced\":" + JsonUtil.Bool(ReadAsmdefBoolField(content, "autoReferenced", true))
                + ",\"no_engine_references\":" + JsonUtil.Bool(ReadAsmdefBoolField(content, "noEngineReferences", false)) + "}";
        }

        static string ReadAsmdefStringField(string content, string field)
        {
            var match = Regex.Match(content, "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*\\\"([^\\\"]{0,512})\\\"", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return match.Success ? match.Groups[1].Value : string.Empty;
        }

        static bool ReadAsmdefBoolField(string content, string field, bool defaultValue)
        {
            var match = Regex.Match(content, "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*(true|false)", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
            return match.Success ? string.Equals(match.Groups[1].Value, "true", StringComparison.OrdinalIgnoreCase) : defaultValue;
        }

        static List<string> ReadAsmdefStringArray(string content, string field, int maxValues)
        {
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
                    break;
                }

                values.Add(value.Groups[1].Value);
            }

            return values;
        }

        static int CountAsmdefObjects(string content, string field)
        {
            var match = Regex.Match(content, "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
            return match.Success ? Regex.Matches(match.Groups[1].Value, "\\{", RegexOptions.None, TimeSpan.FromMilliseconds(250)).Count : 0;
        }

        static string StringArrayJson(List<string> values)
        {
            var json = new List<string>();
            foreach (var value in values)
            {
                json.Add(JsonUtil.String(value));
            }

            return string.Join(",", json.ToArray());
        }

        static AssemblyPrediction PredictAssembly(string scriptPath, bool underEditorFolder)
        {
            var directory = ParentAssetPath(scriptPath);
            while (!string.IsNullOrEmpty(directory) && IsUnityAssetDirectory(directory))
            {
                var asmdef = FirstAsmdefPath(directory);
                if (!string.IsNullOrEmpty(asmdef))
                {
                    var asmdefContent = ReadUtf8Prefix(ResolveUnityPathOrFallback(asmdef, true), MaxAsmdefBytes);
                    return new AssemblyPrediction(asmdef, ReadAsmdefName(asmdef, asmdefContent), false, IsEditorOnlyAsmdef(asmdefContent) || underEditorFolder, "nearest_asmdef");
                }

                directory = ParentAssetPath(directory);
            }

            return new AssemblyPrediction(string.Empty, underEditorFolder ? "Assembly-CSharp-Editor" : "Assembly-CSharp", true, underEditorFolder, underEditorFolder ? "editor_folder_without_asmdef" : "runtime_folder_without_asmdef");
        }

        static string FirstAsmdefPath(string assetDirectory)
        {
            if (!TryResolveUnityPath(assetDirectory, false, out var fullDirectory))
            {
                return string.Empty;
            }

            var files = Directory.GetFiles(fullDirectory, "*.asmdef", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);
            if (files.Length == 0)
            {
                return string.Empty;
            }

            return assetDirectory.TrimEnd('/') + "/" + Path.GetFileName(files[0]);
        }

        static string ReadAsmdefName(string asmdefPath, string content)
        {
            var match = Regex.Match(content, "\\\"name\\\"\\s*:\\s*\\\"([^\\\"]{1,256})\\\"", RegexOptions.None, TimeSpan.FromMilliseconds(250));
            return match.Success ? match.Groups[1].Value : Path.GetFileNameWithoutExtension(asmdefPath);
        }

        static bool IsEditorOnlyAsmdef(string content)
        {
            var includePlatforms = Regex.Match(content, "\\\"includePlatforms\\\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline, TimeSpan.FromMilliseconds(250));
            return includePlatforms.Success && includePlatforms.Groups[1].Value.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static List<DeclaredType> ExtractDeclaredTypes(string content)
        {
            var types = new List<DeclaredType>();
            var regex = new Regex("\\b(class|struct|interface|enum)\\s+([A-Za-z_][A-Za-z0-9_]*)\\b|\\brecord\\s+(?:(class|struct)\\s+)?([A-Za-z_][A-Za-z0-9_]*)\\b", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
            var strippedContent = RemoveComments(content);
            foreach (Match match in regex.Matches(strippedContent))
            {
                if (types.Count >= 50)
                {
                    break;
                }

                var kind = match.Groups[1].Success ? match.Groups[1].Value : (match.Groups[3].Success ? "record " + match.Groups[3].Value : "record");
                var name = match.Groups[2].Success ? match.Groups[2].Value : match.Groups[4].Value;
                var namespaceName = NamespaceBefore(strippedContent, match.Index);
                types.Add(new DeclaredType(kind, name, namespaceName));
            }

            return types;
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

        static List<string> FindLoadedTypes(string typeName, List<DeclaredType> declaredTypes, int maxCandidates, out int matched)
        {
            var queries = new List<string>();
            AddUnique(queries, typeName);
            foreach (var declaredType in declaredTypes)
            {
                AddUnique(queries, declaredType.FullName);
                AddUnique(queries, declaredType.Name);
            }

            var results = new List<string>();
            matched = 0;
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || !MatchesAny(type, queries))
                    {
                        continue;
                    }

                    matched++;
                    if (results.Count < maxCandidates)
                    {
                        results.Add("{\"assembly\":" + JsonUtil.String(assembly.GetName().Name ?? string.Empty)
                            + ",\"full_type_name\":" + JsonUtil.String(type.FullName ?? type.Name)
                            + ",\"type_name\":" + JsonUtil.String(type.Name) + "}");
                    }
                }
            }

            results.Sort(StringComparer.Ordinal);
            return results;
        }

        static List<string> BuildWarnings(AssemblyPrediction prediction, List<string> loadedTypes, int matchedLoadedTypes)
        {
            var warnings = new List<string> { "using directives do not add assembly references; asmdef references control cross-assembly access." };
            if (matchedLoadedTypes > loadedTypes.Count)
            {
                warnings.Add("loaded type candidates were truncated by max_candidates.");
            }

            if (HasDuplicateLoadedTypeNames(loadedTypes))
            {
                warnings.Add("duplicate loaded type candidates were found across assemblies.");
            }

            if (prediction != null && loadedTypes.Count > 0 && !AnyLoadedAssemblyMatches(loadedTypes, prediction.AssemblyName))
            {
                warnings.Add("predicted assembly does not match loaded type candidate assemblies; Unity may not have recompiled or the query may resolve another type.");
            }

            return warnings;
        }

        static bool HasDuplicateLoadedTypeNames(List<string> loadedTypes)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var json in loadedTypes)
            {
                var name = JsonUtil.ExtractString(json, "full_type_name");
                if (!names.Add(name))
                {
                    return true;
                }
            }

            return false;
        }

        static bool AnyLoadedAssemblyMatches(List<string> loadedTypes, string assemblyName)
        {
            foreach (var json in loadedTypes)
            {
                if (string.Equals(JsonUtil.ExtractString(json, "assembly"), assemblyName, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        static bool MatchesAny(Type type, List<string> queries)
        {
            if (queries.Count == 0)
            {
                return false;
            }

            foreach (var query in queries)
            {
                if (string.Equals(type.FullName, query, StringComparison.OrdinalIgnoreCase) || string.Equals(type.Name, query, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        static bool Contains(string value, string query)
        {
            return string.IsNullOrEmpty(query) || (!string.IsNullOrEmpty(value) && value.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static List<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return new List<Type>(assembly.GetTypes());
            }
            catch (ReflectionTypeLoadException exception)
            {
                var types = new List<Type>();
                foreach (var type in exception.Types)
                {
                    if (type != null)
                    {
                        types.Add(type);
                    }
                }

                return types;
            }
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

        static string ResolveUnityPathOrFallback(string assetPath, bool file)
        {
            return TryResolveUnityPath(assetPath, file, out var fullPath) ? fullPath : AbsoluteProjectPath(assetPath);
        }

        static bool TryResolveUnityPath(string assetPath, bool file, out string fullPath)
        {
            fullPath = string.Empty;
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal) || assetPath == "Assets")
            {
                var candidate = AbsoluteProjectPath(assetPath);
                if (IsSameOrChildPath(candidate, ProjectPaths.ProjectRoot) && Exists(candidate, file))
                {
                    fullPath = candidate;
                    return true;
                }

                return false;
            }

            if (assetPath.StartsWith("Packages/", StringComparison.Ordinal) || assetPath == "Packages")
            {
                var relative = assetPath == "Packages" ? string.Empty : assetPath.Substring("Packages/".Length);
                var slash = relative.IndexOf('/');
                var packageName = slash < 0 ? relative : relative.Substring(0, slash);
                var packageRelative = slash < 0 ? string.Empty : relative.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar);
                if (!string.IsNullOrEmpty(packageName))
                {
                    foreach (var package in PackageInfo.GetAllRegisteredPackages())
                    {
                        if (!string.Equals(package.name, packageName, StringComparison.Ordinal))
                        {
                            continue;
                        }

                        var packageRoot = Path.GetFullPath(package.resolvedPath);
                        var candidate = Path.GetFullPath(Path.Combine(packageRoot, packageRelative));
                        if (IsSameOrChildPath(candidate, packageRoot) && Exists(candidate, file))
                        {
                            fullPath = candidate;
                            return true;
                        }
                    }
                }

                var fallback = AbsoluteProjectPath(assetPath);
                if (IsSameOrChildPath(fallback, ProjectPaths.ProjectRoot) && Exists(fallback, file))
                {
                    fullPath = fallback;
                    return true;
                }
            }

            return false;
        }

        static string AbsoluteProjectPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static bool Exists(string path, bool file)
        {
            return file ? File.Exists(path) : Directory.Exists(path);
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static string ReadUtf8Prefix(string fullPath, int maxBytes)
        {
            var info = new FileInfo(fullPath);
            var bytesToRead = (int)Math.Min(info.Length, maxBytes);
            var buffer = new byte[bytesToRead];
            using (var stream = File.OpenRead(fullPath))
            {
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
            }

            return Encoding.UTF8.GetString(buffer, 0, bytesToRead);
        }

        static string DeclaredTypesJson(List<DeclaredType> types, int maxCandidates)
        {
            var values = new List<string>();
            for (var index = 0; index < types.Count && index < maxCandidates; index++)
            {
                values.Add(types[index].ToJson());
            }

            return string.Join(",", values.ToArray());
        }

        static string WarningsJson(List<string> warnings)
        {
            var values = new List<string>();
            foreach (var warning in warnings)
            {
                values.Add(JsonUtil.String(warning));
            }

            return string.Join(",", values.ToArray());
        }

        static void AddUnique(List<string> values, string value)
        {
            if (!string.IsNullOrWhiteSpace(value) && !values.Contains(value))
            {
                values.Add(value);
            }
        }

        static string Bounded(string value, int maxChars)
        {
            return string.IsNullOrEmpty(value) || value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        sealed class ResolvedAssemblyInput
        {
            public ResolvedAssemblyInput(string inputKind, string scriptPath, AssemblyListEntry entry, string asmdefContent)
            {
                InputKind = inputKind;
                ScriptPath = scriptPath;
                Entry = entry;
                AsmdefContent = asmdefContent;
            }

            public string InputKind { get; private set; }
            public string ScriptPath { get; private set; }
            public AssemblyListEntry Entry { get; private set; }
            public string AsmdefContent { get; private set; }

            public string ToJson()
            {
                return "{\"input_kind\":" + JsonUtil.String(InputKind)
                    + ",\"script_path\":" + JsonUtil.String(ScriptPath)
                    + ",\"assembly_name\":" + JsonUtil.String(Entry.AssemblyName)
                    + ",\"kind\":" + JsonUtil.String(Entry.Kind)
                    + ",\"asmdef_path\":" + JsonUtil.String(Entry.AsmdefPath)
                    + ",\"auto_generated\":" + JsonUtil.Bool(Entry.AutoGenerated)
                    + ",\"editor_only\":" + JsonUtil.Bool(Entry.EditorOnly)
                    + ",\"reason\":" + JsonUtil.String(Entry.Reason) + "}";
            }
        }

        sealed class ReferenceExplanation
        {
            public ReferenceExplanation()
            {
                ReferenceKind = "unknown";
                RequiredAction = "unknown";
                Checks = new List<string>();
                Warnings = new List<string>();
            }

            public bool CanReference { get; private set; }
            public string ReferenceKind { get; private set; }
            public string RequiredAction { get; private set; }
            public List<string> Checks { get; private set; }
            public List<string> Warnings { get; private set; }

            public ReferenceExplanation Pass(string referenceKind, string requiredAction, string checkJson = null)
            {
                CanReference = true;
                ReferenceKind = referenceKind;
                RequiredAction = requiredAction;
                if (!string.IsNullOrEmpty(checkJson))
                {
                    Checks.Add(checkJson);
                }

                return this;
            }

            public ReferenceExplanation Block(string referenceKind, string requiredAction)
            {
                CanReference = false;
                ReferenceKind = referenceKind;
                RequiredAction = requiredAction;
                return this;
            }
        }

        sealed class ReferenceTraceEvidence
        {
            public ReferenceTraceEvidence(string metadataKind, string evidenceKind, string ownerType, string member, string referenceSource, string referencedType, string referencedAssembly)
            {
                MetadataKind = metadataKind;
                EvidenceKind = evidenceKind;
                OwnerType = ownerType;
                Member = member;
                ReferenceSource = referenceSource;
                ReferencedType = referencedType;
                ReferencedAssembly = referencedAssembly;
            }

            public string MetadataKind { get; private set; }
            public string EvidenceKind { get; private set; }
            public string OwnerType { get; private set; }
            public string Member { get; private set; }
            public string ReferenceSource { get; private set; }
            public string ReferencedType { get; private set; }
            public string ReferencedAssembly { get; private set; }

            public string ToJson()
            {
                return "{\"metadata_kind\":" + JsonUtil.String(MetadataKind)
                    + ",\"evidence_kind\":" + JsonUtil.String(EvidenceKind)
                    + ",\"owner_type\":" + JsonUtil.String(OwnerType)
                    + ",\"member\":" + JsonUtil.String(Member)
                    + ",\"reference_source\":" + JsonUtil.String(ReferenceSource)
                    + ",\"referenced_type\":" + JsonUtil.String(ReferencedType)
                    + ",\"referenced_assembly\":" + JsonUtil.String(ReferencedAssembly) + "}";
            }
        }

        sealed class GeneratedCsprojListEntry
        {
            public GeneratedCsprojListEntry(string csprojPath, string fileName, string projectName, string assemblyName, string kind, string reason, string asmdefPath, bool knownCompilationAssembly, bool unityGenerated, bool editorOnly, int sourceCount, int projectSourceCount, int packageSourceCount, long fileBytes, string lastWriteUtc)
            {
                CsprojPath = csprojPath;
                FileName = fileName;
                ProjectName = projectName;
                AssemblyName = assemblyName;
                Kind = kind;
                Reason = reason;
                AsmdefPath = asmdefPath;
                KnownCompilationAssembly = knownCompilationAssembly;
                UnityGenerated = unityGenerated;
                EditorOnly = editorOnly;
                SourceCount = sourceCount;
                ProjectSourceCount = projectSourceCount;
                PackageSourceCount = packageSourceCount;
                FileBytes = fileBytes;
                LastWriteUtc = lastWriteUtc;
            }

            public string CsprojPath { get; private set; }
            public string FileName { get; private set; }
            public string ProjectName { get; private set; }
            public string AssemblyName { get; private set; }
            public string Kind { get; private set; }
            public string Reason { get; private set; }
            public string AsmdefPath { get; private set; }
            public bool KnownCompilationAssembly { get; private set; }
            public bool UnityGenerated { get; private set; }
            public bool EditorOnly { get; private set; }
            public int SourceCount { get; private set; }
            public int ProjectSourceCount { get; private set; }
            public int PackageSourceCount { get; private set; }
            public long FileBytes { get; private set; }
            public string LastWriteUtc { get; private set; }

            public string ToJson()
            {
                return "{\"csproj_path\":" + JsonUtil.String(CsprojPath)
                    + ",\"file_name\":" + JsonUtil.String(FileName)
                    + ",\"project_name\":" + JsonUtil.String(ProjectName)
                    + ",\"assembly_name\":" + JsonUtil.String(AssemblyName)
                    + ",\"kind\":" + JsonUtil.String(Kind)
                    + ",\"reason\":" + JsonUtil.String(Reason)
                    + ",\"asmdef_path\":" + JsonUtil.String(AsmdefPath)
                    + ",\"known_compilation_assembly\":" + JsonUtil.Bool(KnownCompilationAssembly)
                    + ",\"unity_generated\":" + JsonUtil.Bool(UnityGenerated)
                    + ",\"editor_only\":" + JsonUtil.Bool(EditorOnly)
                    + ",\"source_count\":" + SourceCount
                    + ",\"project_source_count\":" + ProjectSourceCount
                    + ",\"package_source_count\":" + PackageSourceCount
                    + ",\"file_bytes\":" + FileBytes
                    + ",\"last_write_utc\":" + JsonUtil.String(LastWriteUtc) + "}";
            }
        }

        sealed class GeneratedCsprojComparison
        {
            public GeneratedCsprojComparison(GeneratedCsprojListEntry entry, long fileBytes, int readBytes, bool contentTruncated, int compileItemCount, int expectedSourceCount, int compileItemsRedacted, bool compileItemsTruncatedForCompare, int missingCompileItemCount, int extraCompileItemCount, List<string> missingCompileItems, List<string> extraCompileItems, int projectReferenceCount, int expectedProjectReferenceCount, int projectReferencesRedacted, bool projectReferencesTruncatedForCompare, int missingProjectReferenceCount, int extraProjectReferenceCount, List<string> missingProjectReferences, List<string> extraProjectReferences)
            {
                Entry = entry;
                FileBytes = fileBytes;
                ReadBytes = readBytes;
                ContentTruncated = contentTruncated;
                CompileItemCount = compileItemCount;
                ExpectedSourceCount = expectedSourceCount;
                CompileItemsRedacted = compileItemsRedacted;
                CompileItemsTruncatedForCompare = compileItemsTruncatedForCompare;
                MissingCompileItemCount = missingCompileItemCount;
                ExtraCompileItemCount = extraCompileItemCount;
                MissingCompileItems = missingCompileItems;
                ExtraCompileItems = extraCompileItems;
                ProjectReferenceCount = projectReferenceCount;
                ExpectedProjectReferenceCount = expectedProjectReferenceCount;
                ProjectReferencesRedacted = projectReferencesRedacted;
                ProjectReferencesTruncatedForCompare = projectReferencesTruncatedForCompare;
                MissingProjectReferenceCount = missingProjectReferenceCount;
                ExtraProjectReferenceCount = extraProjectReferenceCount;
                MissingProjectReferences = missingProjectReferences;
                ExtraProjectReferences = extraProjectReferences;
            }

            public GeneratedCsprojListEntry Entry { get; private set; }
            public long FileBytes { get; private set; }
            public int ReadBytes { get; private set; }
            public bool ContentTruncated { get; private set; }
            public int CompileItemCount { get; private set; }
            public int ExpectedSourceCount { get; private set; }
            public int CompileItemsRedacted { get; private set; }
            public bool CompileItemsTruncatedForCompare { get; private set; }
            public int MissingCompileItemCount { get; private set; }
            public int ExtraCompileItemCount { get; private set; }
            public List<string> MissingCompileItems { get; private set; }
            public List<string> ExtraCompileItems { get; private set; }
            public int ProjectReferenceCount { get; private set; }
            public int ExpectedProjectReferenceCount { get; private set; }
            public int ProjectReferencesRedacted { get; private set; }
            public bool ProjectReferencesTruncatedForCompare { get; private set; }
            public int MissingProjectReferenceCount { get; private set; }
            public int ExtraProjectReferenceCount { get; private set; }
            public List<string> MissingProjectReferences { get; private set; }
            public List<string> ExtraProjectReferences { get; private set; }
            public bool Mismatched => MissingCompileItemCount > 0 || ExtraCompileItemCount > 0 || MissingProjectReferenceCount > 0 || ExtraProjectReferenceCount > 0;

            public string ToJson()
            {
                return "{\"csproj\":" + Entry.ToJson()
                    + ",\"mismatched\":" + JsonUtil.Bool(Mismatched)
                    + ",\"file_bytes\":" + FileBytes
                    + ",\"read_bytes\":" + ReadBytes
                    + ",\"content_truncated\":" + JsonUtil.Bool(ContentTruncated)
                    + ",\"compile_item_count\":" + CompileItemCount
                    + ",\"expected_source_count\":" + ExpectedSourceCount
                    + ",\"compile_items_redacted\":" + CompileItemsRedacted
                    + ",\"compile_items_truncated_for_compare\":" + JsonUtil.Bool(CompileItemsTruncatedForCompare)
                    + ",\"missing_compile_item_count\":" + MissingCompileItemCount
                    + ",\"missing_compile_items_truncated\":" + JsonUtil.Bool(MissingCompileItemCount > MissingCompileItems.Count)
                    + ",\"missing_compile_items\":[" + StringArrayJson(MissingCompileItems) + "]"
                    + ",\"extra_compile_item_count\":" + ExtraCompileItemCount
                    + ",\"extra_compile_items_truncated\":" + JsonUtil.Bool(ExtraCompileItemCount > ExtraCompileItems.Count)
                    + ",\"extra_compile_items\":[" + StringArrayJson(ExtraCompileItems) + "]"
                    + ",\"project_reference_count\":" + ProjectReferenceCount
                    + ",\"expected_project_reference_count\":" + ExpectedProjectReferenceCount
                    + ",\"project_references_redacted\":" + ProjectReferencesRedacted
                    + ",\"project_references_truncated_for_compare\":" + JsonUtil.Bool(ProjectReferencesTruncatedForCompare)
                    + ",\"missing_project_reference_count\":" + MissingProjectReferenceCount
                    + ",\"missing_project_references_truncated\":" + JsonUtil.Bool(MissingProjectReferenceCount > MissingProjectReferences.Count)
                    + ",\"missing_project_references\":[" + StringArrayJson(MissingProjectReferences) + "]"
                    + ",\"extra_project_reference_count\":" + ExtraProjectReferenceCount
                    + ",\"extra_project_references_truncated\":" + JsonUtil.Bool(ExtraProjectReferenceCount > ExtraProjectReferences.Count)
                    + ",\"extra_project_references\":[" + StringArrayJson(ExtraProjectReferences) + "]}";
            }
        }

        enum CsprojPathItemKind
        {
            Asset,
            ProjectReference
        }

        sealed class AssemblyGraphNode
        {
            public AssemblyGraphNode(AssemblyListEntry entry, string asmdefContent, string guid)
            {
                Entry = entry;
                AsmdefContent = asmdefContent;
                Guid = guid;
                AutoReferenced = entry.Kind != "asmdef" || ReadAsmdefBoolField(asmdefContent, "autoReferenced", true);
            }

            public AssemblyListEntry Entry { get; private set; }
            public string AsmdefContent { get; private set; }
            public string Guid { get; private set; }
            public bool AutoReferenced { get; private set; }
            public string Id => Entry.Kind == "asmdef" && !string.IsNullOrEmpty(Entry.AsmdefPath) ? Entry.AsmdefPath : Entry.AssemblyName;

            public string ToJson()
            {
                return "{\"id\":" + JsonUtil.String(Id)
                    + ",\"assembly_name\":" + JsonUtil.String(Entry.AssemblyName)
                    + ",\"kind\":" + JsonUtil.String(Entry.Kind)
                    + ",\"asmdef_path\":" + JsonUtil.String(Entry.AsmdefPath)
                    + ",\"guid\":" + JsonUtil.String(Guid)
                    + ",\"auto_generated\":" + JsonUtil.Bool(Entry.AutoGenerated)
                    + ",\"editor_only\":" + JsonUtil.Bool(Entry.EditorOnly)
                    + ",\"auto_referenced\":" + JsonUtil.Bool(AutoReferenced)
                    + ",\"reason\":" + JsonUtil.String(Entry.Reason) + "}";
            }
        }

        sealed class AssemblyGraphEdge
        {
            public AssemblyGraphEdge(AssemblyGraphNode source, AssemblyGraphNode target, string referenceKind, string referenceBy, string referenceValue)
            {
                Source = source;
                Target = target;
                ReferenceKind = referenceKind;
                ReferenceBy = referenceBy;
                ReferenceValue = referenceValue;
            }

            public AssemblyGraphNode Source { get; private set; }
            public AssemblyGraphNode Target { get; private set; }
            public string ReferenceKind { get; private set; }
            public string ReferenceBy { get; private set; }
            public string ReferenceValue { get; private set; }

            public string ToJson()
            {
                return "{\"source_id\":" + JsonUtil.String(Source.Id)
                    + ",\"source_assembly\":" + JsonUtil.String(Source.Entry.AssemblyName)
                    + ",\"target_id\":" + JsonUtil.String(Target.Id)
                    + ",\"target_assembly\":" + JsonUtil.String(Target.Entry.AssemblyName)
                    + ",\"reference_kind\":" + JsonUtil.String(ReferenceKind)
                    + ",\"reference_by\":" + JsonUtil.String(ReferenceBy)
                    + ",\"reference_value\":" + JsonUtil.String(ReferenceValue) + "}";
            }
        }

        sealed class AssemblyListEntry
        {
            public AssemblyListEntry(string asmdefPath, string assemblyName, string kind, bool autoGenerated, bool editorOnly, string reason)
            {
                AsmdefPath = asmdefPath;
                AssemblyName = assemblyName;
                Kind = kind;
                AutoGenerated = autoGenerated;
                EditorOnly = editorOnly;
                Reason = reason;
            }

            public string AsmdefPath { get; private set; }
            public string AssemblyName { get; private set; }
            public string Kind { get; private set; }
            public bool AutoGenerated { get; private set; }
            public bool EditorOnly { get; private set; }
            public string Reason { get; private set; }

            public string ToJson()
            {
                return "{\"assembly_name\":" + JsonUtil.String(AssemblyName)
                    + ",\"kind\":" + JsonUtil.String(Kind)
                    + ",\"asmdef_path\":" + JsonUtil.String(AsmdefPath)
                    + ",\"auto_generated\":" + JsonUtil.Bool(AutoGenerated)
                    + ",\"editor_only\":" + JsonUtil.Bool(EditorOnly)
                    + ",\"reason\":" + JsonUtil.String(Reason) + "}";
            }
        }

        sealed class AssemblyPrediction
        {
            public AssemblyPrediction(string asmdefPath, string assemblyName, bool autoGenerated, bool editorOnly, string reason)
            {
                AsmdefPath = asmdefPath;
                AssemblyName = assemblyName;
                AutoGenerated = autoGenerated;
                EditorOnly = editorOnly;
                Reason = reason;
            }

            public string AsmdefPath { get; private set; }
            public string AssemblyName { get; private set; }
            public bool AutoGenerated { get; private set; }
            public bool EditorOnly { get; private set; }
            public string Reason { get; private set; }

            public string ToJson()
            {
                return "{\"asmdef_path\":" + JsonUtil.String(AsmdefPath)
                    + ",\"assembly_name\":" + JsonUtil.String(AssemblyName)
                    + ",\"kind\":" + JsonUtil.String(AutoGenerated ? "auto" : "asmdef")
                    + ",\"auto_generated\":" + JsonUtil.Bool(AutoGenerated)
                    + ",\"editor_only\":" + JsonUtil.Bool(EditorOnly)
                    + ",\"reason\":" + JsonUtil.String(Reason) + "}";
            }
        }

        sealed class DeclaredType
        {
            public DeclaredType(string kind, string name, string namespaceName)
            {
                Kind = kind;
                Name = name;
                NamespaceName = namespaceName;
            }

            public string Kind { get; private set; }
            public string Name { get; private set; }
            public string NamespaceName { get; private set; }
            public string FullName => string.IsNullOrEmpty(NamespaceName) ? Name : NamespaceName + "." + Name;

            public string ToJson()
            {
                return "{\"kind\":" + JsonUtil.String(Kind)
                    + ",\"namespace\":" + JsonUtil.String(NamespaceName)
                    + ",\"name\":" + JsonUtil.String(Name)
                    + ",\"full_name\":" + JsonUtil.String(FullName) + "}";
            }
        }
    }
}
