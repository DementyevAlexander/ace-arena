import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "ScriptAssemblyInspectionCommands.cs");

describe("script assembly inspection command source-contract", () => {
  it("declares script.assembly.resolve through the Unity command catalog", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('"script.assembly.resolve"');
    expect(source).toContain('"assembly.list"');
    expect(source).toContain('"assembly.sources.list"');
    expect(source).toContain('"assembly.references.graph"');
    expect(source).toContain('"csproj.generated.list"');
    expect(source).toContain('"csproj.generated.inspect"');
    expect(source).toContain('"csproj.generated.compare"');
    expect(source).toContain('"script.relocation.preflight"');
    expect(source).toContain('"assembly.inspect"');
    expect(source).toContain('"assembly.references.explain"');
    expect(source).toContain('"assembly.references.trace"');
    expect(source).toContain('"script.type.resolve"');
    expect(source).toContain('Title = "Inspect Unity script assembly"');
    expect(source).toContain('Title = "List Unity assembly source files"');
    expect(source).toContain('Title = "Read Unity assembly reference graph"');
    expect(source).toContain('Title = "List Unity-generated C# project files"');
    expect(source).toContain('Title = "Inspect Unity-generated C# project file"');
    expect(source).toContain('Title = "Compare Unity-generated C# project files"');
    expect(source).toContain('Title = "Preflight Unity script relocation"');
    expect(source).toContain('Title = "Explain Unity assembly references"');
    expect(source).toContain('Title = "Trace Unity assembly reference evidence"');
    expect(source).toContain('Title = "Resolve Unity script type"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('Capability = FeatureFlag');
    expect(source).toContain('FeatureFlag = FeatureFlag');
    expect(source).toContain('const string FeatureFlag = "unity_api_reflection"');
  });

  it("exposes the expected bounded schema", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('\\"script_path\\"');
    expect(source).toContain('\\"type_name\\"');
    expect(source).toContain('\\"max_candidates\\"');
    expect(source).toContain('\\"include_packages\\"');
    expect(source).toContain('\\"assembly_name\\"');
    expect(source).toContain('\\"asmdef_path\\"');
    expect(source).toContain('\\"include_types\\"');
    expect(source).toContain('\\"max_types\\"');
    expect(source).toContain('\\"source_script_path\\"');
    expect(source).toContain('\\"source_assembly_name\\"');
    expect(source).toContain('\\"source_asmdef_path\\"');
    expect(source).toContain('\\"target_script_path\\"');
    expect(source).toContain('\\"target_assembly_name\\"');
    expect(source).toContain('\\"target_asmdef_path\\"');
    expect(source).toContain('\\"include_scripts\\"');
    expect(source).toContain('\\"max_scan_files\\"');
    expect(source).toContain('\\"max_evidence\\"');
    expect(source).toContain('\\"max_source_hints\\"');
    expect(source).toContain('\\"max_source_files\\"');
    expect(source).toContain('\\"query\\"');
    expect(source).toContain('\\"max_results\\"');
    expect(source).toContain('\\"max_assemblies\\"');
    expect(source).toContain('\\"max_sources_per_assembly\\"');
    expect(source).toContain('\\"max_nodes\\"');
    expect(source).toContain('\\"max_edges\\"');
    expect(source).toContain('\\"csproj_path\\"');
    expect(source).toContain('\\"project_name\\"');
    expect(source).toContain('\\"max_items\\"');
    expect(source).toContain('\\"max_projects\\"');
    expect(source).toContain('\\"max_differences_per_project\\"');
    expect(source).toContain('\\"source_script_path\\"');
    expect(source).toContain('\\"target_script_path\\"');
    expect(source).toContain('\\"max_declared_types\\"');
    expect(source).toContain('\\"minimum\\":1');
    expect(source).toContain('\\"maximum\\":50');
    expect(source).toContain('\\"maximum\\":200');
    expect(source).toContain('\\"maximum\\":1000');
    expect(source).toContain('\\"anyOf\\"');
    expect(source).toContain("MaxScriptBytes = 262144");
    expect(source).toContain("MaxAsmdefBytes = 32768");
    expect(source).toContain("MaxAsmdefFiles = 500");
    expect(source).toContain("MaxCsprojFiles = 200");
    expect(source).toContain("MaxCsprojBytes = 524288");
    expect(source).toContain("MaxCsprojCompareItems = 5000");
    expect(source).toContain("MaxReferencedAssemblies = 100");
    expect(source).toContain("MaxReferenceTraceTypes = 5000");
    expect(source).toContain("MaxReferenceTraceEvidence = 200");
    expect(source).toContain("MaxReferenceTraceSourceHints = 100");
    expect(source).toContain("MaxReferenceTraceSourceFiles = 1000");
    expect(source).toContain("MaxTypeResolveScriptFiles = 1000");
    expect(source).toContain("MaxTypeResolveScriptBytes = 65536");
  });

  it("lists current compilation assembly source ownership without leaking host paths", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("CompilationPipeline.GetAssemblies()");
    expect(source).toContain("assembly.sourceFiles");
    expect(source).toContain("NormalizeCompilationSourceFiles");
    expect(source).toContain("UnityAssetPathFromCompilerPath");
    expect(source).toContain('StartsWith("Assets/"');
    expect(source).toContain('StartsWith("Packages/"');
    expect(source).toContain("CompilationPipeline.GetAssemblies().sourceFiles is authoritative");
    expect(source).toContain("Generated .csproj files are IDE diagnostics and may be stale");
    expect(source).toContain("max_sources_per_assembly");
    expect(source).toContain("sources_truncated");
    expect(source).toContain("project_source_count");
    expect(source).toContain("package_source_count");
  });

  it("returns a bounded local assembly reference graph", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("BuildAssemblyGraphNodes");
    expect(source).toContain("BuildAssemblyGraphEdges");
    expect(source).toContain("SelectAssemblyGraphNodes");
    expect(source).toContain("AssemblyGraphEdgeQueryMatches");
    expect(source).toContain("AssemblyGraphNode");
    expect(source).toContain("AssemblyGraphEdge");
    expect(source).toContain("returned_nodes");
    expect(source).toContain("nodes_truncated");
    expect(source).toContain("returned_edges");
    expect(source).toContain("edges_truncated");
    expect(source).toContain("source_id");
    expect(source).toContain("target_id");
    expect(source).toContain("reference_by");
    expect(source).toContain("autoReferenced");
    expect(source).toContain("implicit_generated_reference");
    expect(source).toContain("explicit_asmdef_reference");
    expect(source).toContain("GUID:");
    expect(source).toContain("Platform, define constraints, versionDefines");
  });

  it("lists generated csproj diagnostics without generating project files", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("ListGeneratedCsproj");
    expect(source).toContain("FindProjectRootCsprojFiles");
    expect(source).toContain('Directory.GetFiles(projectRoot, "*.csproj", SearchOption.TopDirectoryOnly)');
    expect(source).toContain("BuildGeneratedCsprojListEntry");
    expect(source).toContain("GeneratedCsprojListEntry");
    expect(source).toContain("ProjectRelativePathFromFullPath");
    expect(source).toContain("FindCompilationAssemblyByName");
    expect(source).toContain("FindAssemblyListEntryInList");
    expect(source).toContain("Assembly-CSharp-Editor");
    expect(source).toContain("Assembly-CSharp");
    expect(source).toContain("generated_editor");
    expect(source).toContain("generated_runtime");
    expect(source).toContain("asmdef_project");
    expect(source).toContain("known_compilation_assembly");
    expect(source).toContain("unity_generated");
    expect(source).toContain("file_bytes");
    expect(source).toContain("last_write_utc");
    expect(source).toContain("skipped_package_only");
    expect(source).toContain("Generated .csproj files are IDE diagnostics and may be stale");
    expect(source).toContain("does not generate, refresh, compile, import, or write project files");
  });

  it("inspects one generated csproj with sanitized bounded item groups", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("InspectGeneratedCsproj");
    expect(source).toContain("Provide exactly one csproj_path or project_name selector");
    expect(source).toContain("TryResolveProjectRootCsproj");
    expect(source).toContain("IsProjectRootFileName");
    expect(source).toContain("ReadUtf8Prefix(fullPath, MaxCsprojBytes)");
    expect(source).toContain("ReadCsprojPathItems");
    expect(source).toContain("ReadCsprojReferenceItems");
    expect(source).toContain("ReadCsprojDefineConstants");
    expect(source).toContain("NormalizeCsprojAssetPath");
    expect(source).toContain("NormalizeCsprojProjectReferencePath");
    expect(source).toContain("NormalizeCsprojReferenceName");
    expect(source).toContain("DecodeXmlText");
    expect(source).toContain("LooksRootedPath");
    expect(source).toContain('"Compile"');
    expect(source).toContain('"None"');
    expect(source).toContain("ReadCsprojReferenceItems(content");
    expect(source).toContain('"ProjectReference"');
    expect(source).toContain('"Analyzer"');
    expect(source).toContain("DefineConstants");
    expect(source).toContain("compile_items_redacted");
    expect(source).toContain("references_redacted");
    expect(source).toContain("project_references_redacted");
    expect(source).toContain("Host absolute paths and non-project/non-package paths are redacted");
  });

  it("compares generated csproj diagnostics with CompilationPipeline metadata", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("CompareGeneratedCsproj");
    expect(source).toContain("CompareGeneratedCsprojEntry");
    expect(source).toContain("GeneratedCsprojComparison");
    expect(source).toContain("DifferenceValues");
    expect(source).toContain("CsprojProjectReferenceNames");
    expect(source).toContain("ExpectedCsprojProjectReferenceNames");
    expect(source).toContain("FindGeneratedCsprojEntryByAssemblyName");
    expect(source).toContain("BuildAssemblyGraphNodes(includePackages");
    expect(source).toContain("BuildAssemblyGraphEdges(graphNodes)");
    expect(source).toContain("CompilationPipeline.GetAssemblies().sourceFiles is authoritative");
    expect(source).toContain("ProjectReference comparison uses local asmdef/generated graph edges");
    expect(source).toContain("missing_compile_item_count");
    expect(source).toContain("extra_compile_item_count");
    expect(source).toContain("missing_project_reference_count");
    expect(source).toContain("extra_project_reference_count");
    expect(source).toContain("mismatched_projects");
    expect(source).toContain("projects_truncated");
  });

  it("preflights script relocation without moving or importing files", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("PreflightScriptRelocation");
    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawSourceScriptPath)");
    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawTargetScriptPath)");
    expect(source).toContain("ResolvedAssemblyInputFromPrediction");
    expect(source).toContain("BuildRelocationPreflightWarnings");
    expect(source).toContain("ReferenceExplanationJson");
    expect(source).toContain("old_source_can_reference_new_target");
    expect(source).toContain("new_target_can_reference_old_source");
    expect(source).toContain("assembly_changes");
    expect(source).toContain("target_parent_exists");
    expect(source).toContain("declared_types_truncated");
    expect(source).toContain("This is a dry-run preflight only");
    expect(source).toContain("does not move, import, refresh, compile, or write files");
    expect(source).toContain("Code-level symbol usages are not fully solved");
  });

  it("keeps script path input Unity-relative and read-only", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawScriptPath)");
    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawAsmdefPath)");
    expect(source).toContain('StartsWith("Assets/"');
    expect(source).toContain('StartsWith("Packages/"');
    expect(source).toContain('EndsWith(".cs"');
    expect(source).toContain('EndsWith(".asmdef"');
    expect(source).toContain("File.OpenRead(fullPath)");
    expect(source).toContain('Directory.EnumerateFiles(fullRoot, "*.asmdef", SearchOption.AllDirectories)');
    expect(source).toContain("PackageInfo.GetAllRegisteredPackages()");
    expect(source).toContain("IsSameOrChildPath(candidate, packageRoot)");
    expect(source).not.toContain("File.Write");
    expect(source).not.toContain("Directory.CreateDirectory");
    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.Refresh");
    expect(source).not.toContain("SaveAssets");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
  });

  it("detects asmdefs and Assembly-CSharp fallback strings", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('"*.asmdef"');
    expect(source).toContain('\\"name\\"');
    expect(source).toContain("Assembly-CSharp-Editor");
    expect(source).toContain("Assembly-CSharp");
    expect(source).toContain("under_editor_folder");
    expect(source).toContain("auto_generated");
    expect(source).toContain("assembly_name");
    expect(source).toContain("scan_truncated");
    expect(source).toContain("scanned_asmdef_files");
    expect(source).toContain("editor_only");
    expect(source).toContain("runtime_folder_without_asmdef");
    expect(source).toContain("editor_folder_without_asmdef");
  });

  it("returns bounded assembly inspect metadata without fix generation", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("loaded_assembly");
    expect(source).toContain("referenced_assemblies");
    expect(source).toContain("type_count");
    expect(source).toContain("types_truncated");
    expect(source).toContain("root_namespace");
    expect(source).toContain("reference_names");
    expect(source).toContain("reference_guids");
    expect(source).toContain("include_platforms");
    expect(source).toContain("exclude_platforms");
    expect(source).toContain("define_constraints");
    expect(source).toContain("precompiled_references");
    expect(source).toContain("auto_referenced");
    expect(source).toContain("no_engine_references");
    expect(source).not.toContain("required_reference");
    expect(source).not.toContain("missing_reference");
  });

  it("explains assembly references with bounded local rules", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("ResolveAssemblyInput");
    expect(source).toContain("ExplainReference");
    expect(source).toContain("can_reference");
    expect(source).toContain("reference_kind");
    expect(source).toContain("required_action");
    expect(source).toContain("same_assembly");
    expect(source).toContain("explicit_asmdef_reference");
    expect(source).toContain("implicit_generated_reference");
    expect(source).toContain("add_asmdef_reference");
    expect(source).toContain("unsupported_generated_target");
    expect(source).toContain("UnityEditor.AssetDatabase.AssetPathToGUID");
    expect(source).toContain("autoReferenced");
    expect(source).toContain("Provide exactly one ");
  });

  it("traces loaded assembly reference evidence without external tools", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("TraceAssemblyReference");
    expect(source).toContain("FindReferencedAssemblyName");
    expect(source).toContain("TraceLoadedAssemblyReferences");
    expect(source).toContain("ReferenceTraceEvidence");
    expect(source).toContain("metadata_reference_present");
    expect(source).toContain("metadata_reference");
    expect(source).toContain("metadata_kind");
    expect(source).toContain("TypeRef");
    expect(source).toContain("MemberRef");
    expect(source).toContain("CustomAttribute");
    expect(source).toContain("owner_type");
    expect(source).toContain("reference_source");
    expect(source).toContain("referenced_type");
    expect(source).toContain("source_hints");
    expect(source).toContain("CompilationPipeline.GetAssemblies()");
    expect(source).toContain("metadata-only; it does not compile, import, refresh, shell out, use network access, or write files");
    expect(source).not.toContain("Process.Start");
  });

  it("uses best-effort type text extraction and the bounded Editor assembly snapshot", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("ExtractDeclaredTypes");
    expect(source).toContain('\\b(class|struct|interface|enum)');
    expect(source).toContain(String.raw`\\brecord\\s+`);
    expect(source).toContain(String.raw`\\bnamespace\\s+`);
    expect(source).toContain("EditorAssemblyDiscovery.LoadedAssemblies()");
    expect(source).not.toContain("AppDomain.CurrentDomain.GetAssemblies()");
    expect(source).toContain("assembly.GetTypes()");
    expect(source).toContain("ReflectionTypeLoadException");
  });

  it("resolves type names to loaded types and candidate scripts", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("ResolveScriptType");
    expect(source).toContain("FindScriptTypeCandidates");
    expect(source).toContain('UnityEditor.AssetDatabase.FindAssets("t:MonoScript", new[] { "Assets", "Packages" })');
    expect(source).toContain("UnityEditor.AssetDatabase.GUIDToAssetPath(guid)");
    expect(source).toContain("ExtractDeclaredTypes(ReadUtf8Prefix(fullPath, MaxTypeResolveScriptBytes))");
    expect(source).toContain("TypeQueryMatches");
    expect(source).toContain("script_candidates");
    expect(source).toContain("script_candidates_truncated");
    expect(source).toContain("loaded_type_candidates");
    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.Refresh");
  });

  it("returns warnings agents need for assembly reasoning", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("using directives do not add assembly references");
    expect(source).toContain("duplicate loaded type candidates");
    expect(source).toContain("predicted assembly does not match loaded type candidate assemblies");
  });
});
