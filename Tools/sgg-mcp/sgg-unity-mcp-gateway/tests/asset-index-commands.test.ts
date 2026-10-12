import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const assetIndexCommandsPath = path.join(packageRoot, "Editor", "Commands", "AssetIndexCommands.cs");
const assetIndexCacheInvalidatorPath = path.join(packageRoot, "Editor", "Commands", "AssetIndexCacheInvalidator.cs");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");

describe("Unity asset index command source-contract", () => {
  it("declares the first asset index and reference graph command IDs", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain('"asset.index.status"');
    expect(source).toContain('"asset.index.rebuild"');
    expect(source).toContain('"asset.index.clear"');
    expect(source).toContain('"asset.list"');
    expect(source).toContain('"asset.search"');
    expect(source).toContain('"asset.semantic_index.status"');
    expect(source).toContain('"asset.semantic_search"');
    expect(source).toContain('"asset.info"');
    expect(source).toContain('"asset.serialized.inspect"');
    expect(source).toContain('"asset.references"');
    expect(source).toContain('"asset.dependencies"');
    expect(source).toContain('"dependency.query"');
    expect(source).toContain('"asset.relocation.preflight"');
    expect(source).toContain('"asset.text.read"');
    expect(source).toContain('"asset.text.find"');
    expect(source).toContain('"asset.text.search"');
    expect(source).toContain('"asset.guid.resolve"');
    expect(source).toContain('"asset.risk_report"');
  });

  it("keeps asset index commands feature-gated through the catalog", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "asset_index"');
    expect(source).toContain('const string SemanticFeatureFlag = "asset_semantic_index"');
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(16);
    expect(source.match(/FeatureFlag = SemanticFeatureFlag/g)).toHaveLength(2);
    expect(source).toContain('Capability = "asset_index"');
    expect(source).toContain('Capability = "asset_semantic_index"');
  });

  it("adds local deterministic semantic asset search without embeddings or network", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"asset.semantic_index.status"'), source.indexOf('[McpCommand(\n            "asset.info"'));
    const helperSource = source.slice(source.indexOf("static string SemanticStatusJson"), source.indexOf("static bool Matches"));
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    expect(source).toContain('const string SemanticSearchSchemaJson =');
    expect(source).toContain('const string SemanticSearchOutputLimitJson = "{\\"max_results\\":200,\\"max_scan_assets\\":20000,\\"local_only\\":true,\\"embedding_provider\\":\\"none\\"}"');
    expect(commandSource).toContain('Title = "Semantic asset index status"');
    expect(commandSource).toContain('Title = "Semantic asset search"');
    expect(commandSource).toContain('Risk = "read"');
    expect(commandSource).toContain('Idempotency = "safe"');
    expect(commandSource).toContain('Tags = new string[] { "asset", "semantic", "index", "unity-ai-assistant" }');
    expect(commandSource).toContain('ResultShape = "bounded_list"');
    expect(commandSource).toContain('RequireAssetPrefix(context.ArgsJson, out var pathPrefix)');
    expect(commandSource).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 5000), 1, MaxSemanticScanAssets)');
    expect(commandSource).toContain('ScoreSemanticAsset(entry, query, queryTokens, includeLabels, includeImporter, includeScriptSymbols)');
    expect(commandSource).toContain('deterministic_lexical_metadata');
    expect(commandSource).toContain('"network_access\\":false');
    expect(commandSource).toContain('"embedding_provider\\":\\"none\\"');
    expect(helperSource).toContain('deterministic_lexical_overlay');
    expect(helperSource).toContain('"backing_index\\":\\"asset_index\\"');
    expect(helperSource).toContain('"metadata_sources\\":[\\"path\\",\\"name\\",\\"type\\",\\"extension\\",\\"guid\\",\\"source\\",\\"label\\",\\"importer\\",\\"script_symbol\\"]');
    expect(helperSource).toContain("AssetDatabase.GetLabels(asset)");
    expect(helperSource).toContain("AssetImporter.GetAtPath(assetPath)");
    expect(helperSource).toContain("ExtractDependencyDeclaredTypes(content, MaxDependencyDeclaredTypes");
    expect(helperSource).toContain("ReadBoundedBytes(fullPath, MaxSemanticScriptBytes");
    expect(helperSource).toContain("ContainsOrdinalIgnoreCase(values, value)");
    expect(helperSource).not.toContain("Process");
    expect(helperSource).not.toContain("HttpClient");

    for (const text of [docs, docsRu, profiles, policyExample]) {
      expect(text).toContain("asset.semantic_index.status");
      expect(text).toContain("asset.semantic_search");
    }
  });

  it("uses Unity AssetDatabase for GUIDs, dependencies and indexed search", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("AssetDatabase.FindAssets(string.Empty, new[] { \"Assets\", \"Packages\" })");
    expect(source).toContain("AssetDatabase.GUIDToAssetPath(guid)");
    expect(source).toContain("AssetDatabase.AssetPathToGUID(assetPath)");
    expect(source).toContain("AssetDatabase.GetDependencies(assetPath, recursive)");
    expect(source).toContain("AssetDatabase.GetMainAssetTypeAtPath(assetPath)");
    expect(source).toContain("AssetDatabase.LoadMainAssetAtPath(assetPath)");
    expect(source).toContain("AssetImporter.GetAtPath(assetPath)");
  });

  it("validates model-provided asset paths and bounds list output", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawPath)");
    expect(source).toContain('\\"maximum\\":1000');
    expect(source).toContain('\\"maximum\\":65536');
    expect(source).toContain('\\"maximum\\":1048576');
    expect(source).toContain('\\"maximum\\":8192');
    expect(source).toContain('\\"maximum\\":200');
    expect(source).toContain("Clamp(JsonUtil.ExtractInt(argsJson, \"max_results\", defaultValue), 1, 1000)");
    expect(source).toContain("Clamp(JsonUtil.ExtractInt(context.ArgsJson, \"max_bytes\", 65536), 1, 65536)");
    expect(source).toContain("Clamp(JsonUtil.ExtractInt(context.ArgsJson, \"max_scan_bytes\", 1048576), 1, 1048576)");
    expect(source).toContain("OutputLimitJson = ListOutputLimitJson");
  });

  it("declares bounded asset.list with safe prefix validation", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult AssetList");
    expect(source).toContain('const string ListSchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('\\"path_prefix\\"');
    expect(source).toContain('\\"include_packages\\"');
    expect(source).toContain('\\"max_results\\"');
    expect(source).toContain("RequireAssetPrefix(context.ArgsJson, out var pathPrefix)");
    expect(source).toContain('pathPrefix = "Assets/"');
    expect(source).toContain('normalized == "Assets" || normalized == "Assets/"');
    expect(source).toContain('normalized == "Packages" || normalized == "Packages/"');
    expect(source).toContain("PathGuard.AssertNoNul(rawPrefix)");
    expect(source).toContain("PathGuard.RequireUnityAssetPath(candidate)");
    expect(source).toContain("pathPrefix.StartsWith(\"Packages/\", StringComparison.Ordinal)");
    expect(source).toContain("entry.Path.StartsWith(pathPrefix, StringComparison.Ordinal)");
    expect(source).toContain('\\"assets\\"');
  });

  it("documents and allows cloudless asset.list in policy", () => {
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    for (const text of [docs, docsRu, profiles, policyExample]) {
      expect(text).toContain("asset.list");
      expect(text).toContain("asset.info");
      expect(text).toContain("asset.serialized.inspect");
    }

    expect(docs).toContain("listing indexed assets/resources");
    expect(docs).toContain("defaults to `Assets/`");
    expect(docsRu).toContain("listing indexed assets/resources");
    expect(docsRu).toContain("по умолчанию берет `Assets/`");
  });

  it("adds bounded read-only Unity ManageAsset GetInfo parity", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult AssetInfo"), source.indexOf('[McpCommand(\n            "asset.references"'));
    const helperSource = source.slice(source.indexOf("static string AssetInfoJson"), source.indexOf("static int CountDependencies"));

    expect(source).toContain('const string AssetInfoSchemaJson =');
    expect(source).toContain('const string AssetInfoOutputLimitJson = "{\\"max_assets\\":1,\\"max_dependencies\\":200}"');
    expect(source).toContain('\\"include_labels\\"');
    expect(source).toContain('\\"include_importer\\"');
    expect(source).toContain('\\"include_dependencies\\"');
    expect(source).toContain('\\"recursive_dependencies\\"');
    expect(source).toContain('\\"max_dependencies\\"');
    expect(commandSource).toContain("PathGuard.RequireUnityAssetPath(rawPath)");
    expect(commandSource).toContain("AssetInfoJson(assetPath, context)");
    expect(helperSource).toContain('\\"exists\\":false');
    expect(helperSource).toContain('\\"compatible_with\\":\\"Unity.ManageAsset GetInfo\\"');
    expect(helperSource).toContain("AssetDatabase.GetMainAssetTypeAtPath(assetPath)");
    expect(helperSource).toContain("AssetDatabase.IsValidFolder(assetPath)");
    expect(helperSource).toContain("AssetDatabase.GetLabels(asset)");
    expect(helperSource).toContain("AssetImporter.GetAtPath(assetPath)");
    expect(helperSource).toContain("AssetDatabase.GetDependencies(assetPath, recursive)");
    expect(helperSource).toContain("Clamp(JsonUtil.ExtractInt(context.ArgsJson, \"max_dependencies\", 50), 1, 200)");
    expect(helperSource).not.toContain("resolvedPath");
    expect(helperSource).not.toContain("ProjectRoot");
    expect(helperSource).not.toContain("full_path");
    expect(helperSource).not.toContain("absolute_path");
    expect(helperSource).not.toContain("ImportAsset");
    expect(helperSource).not.toContain("Refresh");
  });

  it("adds bounded read-only Unity GetObjectData serialized asset parity", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"asset.serialized.inspect"'), source.indexOf('[McpCommand(\n            "asset.references"'));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult SerializedAssetInspectResult"), source.indexOf("static string LabelsJson"));

    expect(source).toContain('const string SerializedInspectSchemaJson = "{\\"type\\":\\"object\\"');
    expect(source).toContain('const string SerializedInspectOutputLimitJson = "{\\"max_assets\\":1,\\"max_depth\\":6,\\"max_array_elements\\":20,\\"max_chars\\":8192}"');
    expect(source).toContain('\\"property_path\\"');
    expect(source).toContain('\\"max_depth\\"');
    expect(source).toContain('\\"maximum\\":6');
    expect(source).toContain('\\"max_array_elements\\"');
    expect(source).toContain('\\"maximum\\":20');
    expect(source).toContain('\\"max_chars\\"');
    expect(commandSource).toContain('Risk = "read"');
    expect(commandSource).toContain('Idempotency = "safe"');
    expect(commandSource).toContain('Tags = new string[] { "asset", "serialized", "unity-ai-assistant" }');
    expect(commandSource).toContain('ResultShape = "object_details"');
    expect(commandSource).toContain('RequireAssetPath(context.ArgsJson, "asset_path", out var assetPath)');
    expect(commandSource).toContain('string.Equals(requestedPath, "ProjectSettings/QualitySettings.asset"');
    expect(commandSource).toContain('\\"replacement_command\\":\\"project.settings.inspect\\"');
    expect(commandSource).toContain('\\"replacement_args\\":{\\"name\\":\\"QualitySettings\\"}');
    expect(commandSource).toContain('\\"runtime_counterpart\\":\\"runtime.quality_settings.inspect\\"');
    expect(commandSource).toContain("SerializedAssetInspectResult(assetPath, propertyPath, maxDepth, maxArrayElements, maxChars)");
    expect(helperSource).toContain("new SerializedObject(asset)");
    expect(helperSource).toContain("serializedObject.FindProperty(propertyPath)");
    expect(helperSource).toContain("propertyPath.Replace('/', '.')");
    expect(helperSource).toContain("IsArrayElementPastLimit(property.propertyPath, maxArrayElements)");
    expect(helperSource).toContain("builder.Length + separatorLength + entryJson.Length + 2 > maxChars");
    expect(helperSource).toContain("Unity.GetObjectData");
    expect(helperSource).not.toContain("File.");
    expect(helperSource).not.toContain("Directory.");
    expect(helperSource).not.toContain("ImportAsset");
    expect(helperSource).not.toContain("Refresh");
  });

  it("implements bounded local text read/find/search commands without absolute path arguments", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult AssetTextRead");
    expect(source).toContain("public static CommandExecutionResult AssetTextFind");
    expect(source).toContain("public static CommandExecutionResult AssetTextSearch");
    expect(source).toContain("RequireAssetPath(context.ArgsJson, \"asset_path\", out var assetPath)");
    expect(source).toContain("RequireAssetPrefix(context.ArgsJson, out var pathPrefix)");
    expect(source).toContain("TryResolveAssetFilePath(assetPath, out var fullPath)");
    expect(source).toContain("assetPath.StartsWith(\"Assets/\", StringComparison.Ordinal)");
    expect(source).toContain("assetPath.StartsWith(\"Packages/\", StringComparison.Ordinal)");
    expect(source).toContain("UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages()");
    expect(source).not.toContain("using UnityEditor.PackageManager;");
    expect(source).toContain("package.resolvedPath");
    expect(source).toContain("IsSameOrChildPath(candidate, packageRoot)");
    expect(source).not.toContain('"full_path"');
    expect(source).not.toContain('"absolute_path"');
  });

  it("rejects binary-like or invalid UTF-8 text assets", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("bytes[index] == 0");
    expect(source).toContain("DecodeText(bytes, truncated, out var content)");
    expect(source).toContain("DecodeText(bytes, scanTruncated, out var content)");
    expect(source).toContain('CommandExecutionResult.Error("invalid_field_value", "Asset content looks binary');
    expect(source).toContain("new UTF8Encoding(false, true).GetString(bytes, 0, byteCount)");
    expect(source).toContain("catch (DecoderFallbackException)");
    expect(source).toContain("if (truncated)");
    expect(source).toContain("for (var trim = 1; trim <= 3 && trim <= bytes.Length; trim++)");
    expect(source).toContain("DecodeStrictUtf8(bytes, bytes.Length - trim)");
    expect(source).toContain('CommandExecutionResult.Error("invalid_field_value", "Asset content is not valid UTF-8 text")');
  });

  it("returns only actual bytes read from bounded streams", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("var bytes = new byte[readLength]");
    expect(source).toContain("var offset = 0");
    expect(source).toContain("offset += read");
    expect(source).toContain("if (offset != bytes.Length)");
    expect(source).toContain("Array.Resize(ref bytes, offset)");
    expect(source).toContain("return bytes");
  });

  it("supports bounded literal and regex text search", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain('\\"match_mode\\"');
    expect(source).toContain('\\"literal\\",\\"regex\\"');
    expect(source).toContain('\\"context_lines\\"');
    expect(source).toContain('\\"regex_timeout_ms\\"');
    expect(source).toContain("TryCreateTextRegex(query, matchMode, caseSensitive, regexTimeoutMs, out var contentRegex)");
    expect(source).toContain("new Regex(pattern, options, TimeSpan.FromMilliseconds(timeoutMs))");
    expect(source).toContain("catch (RegexMatchTimeoutException)");
    expect(source).toContain("FindTextMatches(content, query, comparison, contentRegex, contextLines, maxResults, out truncated)");
    expect(source).toContain("FindTextMatches(content, query, comparison, contentRegex, contextLines, remainingResults, out fileTruncated, out fileMatchCount)");
    expect(source).toContain("line.Text.IndexOf(query, comparison)");
    expect(source).toContain("line.Text.IndexOf(query, matchIndex + Math.Max(query.Length, 1), comparison)");
    expect(source).toContain('\\"line\\"');
    expect(source).toContain('\\"column\\"');
    expect(source).toContain('\\"excerpt\\"');
    expect(source).toContain('\\"context_before\\"');
    expect(source).toContain('\\"context_after\\"');
  });

  it("marks raw Unity YAML reads and reports text read/find bounds", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("IsUnityYamlTextAsset(assetPath)");
    expect(source).toContain('\\"raw_unity_yaml\\"');
    expect(source).toContain('\\"byte_length\\"');
    expect(source).toContain('\\"max_bytes\\"');
    expect(source).toContain('\\"scan_bytes\\"');
    expect(source).toContain('\\"scan_truncated\\"');
    expect(source).toContain('\\"truncated\\"');
  });

  it("adds bounded project-wide text search parity without shelling out", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"asset.text.search"'), source.indexOf('[McpCommand(\n            "asset.guid.resolve"'));

    expect(source).toContain('const string TextSearchSchemaJson =');
    expect(source).toContain('const string TextSearchOutputLimitJson = "{\\"max_results\\":200,\\"max_scan_bytes\\":1048576,\\"max_files\\":10000,\\"max_context_lines\\":5,\\"regex_timeout_ms\\":1000}"');
    expect(commandSource).toContain('Tags = new string[] { "asset", "text", "index", "unity-ai-assistant" }');
    expect(commandSource).toContain('ResultShape = "bounded_list"');
    expect(commandSource).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_files", 1000), 1, 10000)');
    expect(commandSource).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "start_index", 0), 0, 100000)');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "name_regex")');
    expect(commandSource).toContain('TryCreateOptionalRegex(nameRegexPattern, caseSensitive, regexTimeoutMs, out var nameRegex)');
    expect(commandSource).toContain('NameRegexMatches(entry, nameRegex)');
    expect(commandSource).toContain('entry.Path.StartsWith(pathPrefix, StringComparison.Ordinal)');
    expect(commandSource).toContain('IsTextSearchCandidate(entry.Path)');
    expect(commandSource).toContain('PathGuard.RequireUnityAssetPath(entry.Path)');
    expect(commandSource).toContain('TryResolveAssetFilePath(assetPath, out var fullPath)');
    expect(commandSource).toContain('DecodeText(bytes, scanTruncated, out var content)');
    expect(commandSource).toContain('\\"compatible_with\\":\\"Unity.Grep\\"');
    expect(source).toContain('assetPath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('AssetDatabase.IsValidFolder(assetPath)');
    expect(source).toContain('IsKnownBinaryAssetExtension(Path.GetExtension(assetPath))');
    expect(source).toContain('string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase)');
    expect(commandSource).not.toContain("Process");
  });

  it("adds bounded local Unity GetDependency graph parity", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"dependency.query"'), source.indexOf('[McpCommand(\n            "asset.text.read"'));
    const helperSource = source.slice(source.indexOf("static void AppendOutgoingDependencyEdges"), source.indexOf("static string AssetInfoJson"));

    expect(source).toContain('const string DependencyQuerySchemaJson =');
    expect(source).toContain('const string DependencyQueryOutputLimitJson = "{\\"max_edges\\":1000,\\"max_yaml_references\\":200,\\"max_script_types\\":50,\\"max_script_relationships\\":200,\\"max_using_namespaces\\":100,\\"max_assembly_references\\":200,\\"artifact_root\\":\\"Library/SGGUnityMcp/job-artifacts/asset-reports\\"}"');
    expect(commandSource).toContain('Title = "Dependency query"');
    expect(commandSource).toContain('Tags = new string[] { "asset", "dependency", "reference-graph", "unity-ai-assistant" }');
    expect(commandSource).toContain('ResultShape = "bounded_graph"');
    expect(commandSource).toContain('ResolveTargetAsset(context.ArgsJson, out var target)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_outgoing", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_incoming", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_yaml_guid_refs", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_script_context", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_assembly_references", true)');
    expect(commandSource).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_edges", 200), 1, 1000)');
    expect(commandSource).toContain('AppendOutgoingDependencyEdges');
    expect(commandSource).toContain('AppendIncomingDependencyEdges');
    expect(commandSource).toContain('AppendYamlGuidReferenceEdges');
    expect(commandSource).toContain('AppendAsmdefAssemblyReferenceEdges');
    expect(commandSource).toContain('BuildScriptDependencyContext(edges, target');
    expect(commandSource).toContain('\\"assembly_reference_count\\"');
    expect(commandSource).toContain('\\"assembly_references_truncated\\"');
    expect(commandSource).toContain('\\"compatible_with\\":\\"Unity.GetDependency local asset graph\\"');
    expect(helperSource).toContain('AssetDatabase.GetDependencies(target.Path, recursive)');
    expect(helperSource).toContain('DependenciesContain(entry.Path, target.Path, recursive)');
    expect(helperSource).toContain('YamlGuidReferences(target.Path, maxReferences');
    expect(helperSource).toContain('script_compiles_into_assembly');
    expect(helperSource).toContain('script_declares_type');
    expect(helperSource).toContain('assembly_references_assembly');
    expect(helperSource).toContain('ReadAsmdefStringArray(content, "references", MaxDependencyAsmdefReferences');
    expect(helperSource).toContain('AssetDatabase.GUIDToAssetPath(guid)');
    expect(helperSource).toContain('DependencyAssemblyFromAsmdefPath');
    expect(helperSource).toContain('MaxDependencyAsmdefBytes');
    expect(helperSource).toContain('PredictDependencyAssembly(target.Path)');
    expect(helperSource).toContain('ExtractDependencyDeclaredTypes(content, MaxDependencyDeclaredTypes');
    expect(helperSource).toContain('ExtractDependencyScriptRelationships(content, MaxDependencyScriptRelationships');
    expect(helperSource).toContain('ExtractDependencyUsingNamespaces(content, MaxDependencyUsingNamespaces');
    expect(helperSource).toContain('using directives are reported as namespace context only and do not add assembly references');
    expect(helperSource).toContain('Directory.GetFiles(fullDirectory, "*.asmdef", SearchOption.TopDirectoryOnly)');
    expect(helperSource).toContain('ExtractInlineGuid(line)');
    expect(helperSource).toContain('AssetDatabase.GUIDToAssetPath(guid)');
    expect(helperSource).toContain('IsUnityYamlTextAsset(assetPath)');
    expect(helperSource).not.toContain('AssetDatabase.ImportAsset');
    expect(helperSource).not.toContain('AssetDatabase.Refresh');
  });

  it("adds read-only asset relocation dry-run preflight", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"asset.relocation.preflight"'), source.indexOf('[McpCommand(\n            "asset.text.read"'));
    const helperSource = source.slice(source.indexOf("static CommandExecutionResult RequireRelocationPaths"), source.indexOf("static string AssetInfoJson"));
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    expect(source).toContain('const string AssetRelocationPreflightSchemaJson =');
    expect(source).toContain('const string AssetRelocationPreflightOutputLimitJson = "{\\"max_results\\":1000,\\"max_scan_assets\\":100000,\\"max_scan_bytes\\":1048576,\\"dry_run_only\\":true,\\"artifact_root\\":\\"Library/SGGUnityMcp/job-artifacts/asset-reports\\"}"');
    expect(commandSource).toContain('Title = "Preflight asset relocation"');
    expect(commandSource).toContain('Risk = "read"');
    expect(commandSource).toContain('Idempotency = "safe"');
    expect(commandSource).toContain('ResultShape = "object_details"');
    expect(commandSource).toContain('RequireRelocationPaths(context.ArgsJson, out var sourcePath, out var destinationPath)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_assetdatabase", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_yaml_guid_refs", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_text_includes", true)');
    expect(commandSource).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxRelocationScanAssets)');
    expect(commandSource).toContain('DependenciesContain(entry.Path, sourcePath, true)');
    expect(commandSource).toContain('YamlReferencesGuid(entry.Path, sourceGuid, maxScanBytes');
    expect(commandSource).toContain('ShaderIncludeReferencesTarget(entry.Path, sourcePath, maxScanBytes');
    expect(commandSource).toContain('AssetRelocationScriptContextJson(sourcePath, destinationPath');
    expect(commandSource).toContain('AssetRelocationShaderContextJson(sourcePath, destinationPath');
    expect(commandSource).toContain('\\"guid_stable_after_unity_move\\":true');
    expect(helperSource).toContain('PathGuard.RequireUnityAssetPath(rawSourcePath)');
    expect(helperSource).toContain('PredictDependencyAssembly(sourcePath)');
    expect(helperSource).toContain('TryParseShaderIncludeLine');
    expect(helperSource).toContain('NormalizeIncludeCandidate');
    expect(helperSource).toContain('Unity GUID references usually survive AssetDatabase moves');
    expect(helperSource).toContain('Shader include text references are path-sensitive');
    expect(commandSource).not.toContain('AssetDatabase.MoveAsset');
    expect(commandSource).not.toContain('AssetDatabase.ImportAsset');
    expect(commandSource).not.toContain('AssetDatabase.Refresh');
    expect(commandSource).not.toContain('File.Write');

    for (const text of [docs, docsRu, profiles, policyExample]) {
      expect(text).toContain("asset.relocation.preflight");
    }
  });

  it("adds bounded lexical C# relationship edges to dependency query script context", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const helperSource = source.slice(source.indexOf("static string BuildScriptDependencyContext"), source.indexOf("static string AssetInfoJson"));
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");

    for (const expected of [
      "script_inherits_type",
      "script_implements_interface",
      "script_uses_type",
      "script_declares_field_type",
      "script_declares_property_type",
      "script_declares_method_return_type",
      "script_declares_method_parameter_type",
    ]) {
      expect(helperSource).toContain(expected);
      expect(docs).toContain(expected);
      expect(docsRu).toContain(expected);
    }

    expect(source).toContain("const int MaxDependencyScriptBytes = 65536");
    expect(source).toContain("const int MaxDependencyScriptRelationships = 200");
    expect(source).toContain("const int MaxDependencyUsingNamespaces = 100");
    expect(helperSource).toContain("RemoveCommentsAndStrings(content)");
    expect(helperSource).toContain("ExtractInheritanceRelationships");
    expect(helperSource).toContain("ExtractMemberTypeRelationships");
    expect(helperSource).toContain("ExtractUseTypeRelationships");
    expect(helperSource).toContain('script_relationship_count');
    expect(helperSource).toContain('script_relationships_truncated');
    expect(helperSource).toContain('using_namespace_count');
    expect(helperSource).toContain('using_namespaces_truncated');
    expect(source).toContain('lexical_qualified_name');
    expect(source).toContain('lexical_ambiguous_short_name');
    expect(helperSource).toContain('source_kind');
    expect(helperSource).toContain('using directives are reported as namespace context only');
    expect(helperSource).toContain('short type names are not resolved to assemblies');
    expect(helperSource).not.toContain('CompilationPipeline');
    expect(helperSource).not.toContain('AssetDatabase.ImportAsset');
    expect(helperSource).not.toContain('AssetDatabase.Refresh');
  });

  it("hardens asset reference workflow reports with optional fixed-root artifacts", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");

    expect(source).toContain('const string AssetReportArtifactRoot = "Library/SGGUnityMcp/job-artifacts/asset-reports"');
    expect(source).toContain('const string AssetReportListOutputLimitJson = "{\\"max_results\\":1000,\\"artifact_root\\":\\"Library/SGGUnityMcp/job-artifacts/asset-reports\\"}"');
    expect(source).toContain('const string AssetRiskReportOutputLimitJson = "{\\"max_assets\\":1,\\"artifact_root\\":\\"Library/SGGUnityMcp/job-artifacts/asset-reports\\"}"');
    expect(source).toContain('\\"write_artifact\\"');
    expect(source).toContain('static string AssetWorkflowFieldsJson(AssetIndexEntry entry, List<string> warnings, string[] nextValidActions)');
    expect(source).toContain('\\"asset_path\\"');
    expect(source).toContain('\\"asset_type\\"');
    expect(source).toContain('\\"package_asset\\"');
    expect(source).toContain('\\"asset_index\\"');
    expect(source).toContain('ProjectRelativePathOrEmpty(s_lastCachePath)');
    expect(source).toContain('\\"next_valid_actions\\"');
    expect(source).toContain('WithOptionalAssetReportArtifact(context.ArgsJson, "asset-references", response)');
    expect(source).toContain('WithOptionalAssetReportArtifact(context.ArgsJson, "asset-dependencies", response)');
    expect(source).toContain('WithOptionalAssetReportArtifact(context.ArgsJson, "dependency-query", queryResponse)');
    expect(source).toContain('WithOptionalAssetReportArtifact(context.ArgsJson, "asset-relocation-preflight", relocationResponse)');
    expect(source).toContain('WithOptionalAssetReportArtifact(context.ArgsJson, "asset-risk-report", response)');
    expect(source).toContain('WriteAssetReportArtifact(artifactPath, responseWithArtifact)');
    expect(source).toContain('\\"artifact_path\\"');
    expect(source).toContain('\\"artifact_refs\\"');
    expect(source).toContain('\\"readable_with\\":\\"job.artifact.read\\"');
    expect(source).toContain('static string StringArrayJson(string[] values)');

    for (const text of [docs, docsRu]) {
      expect(text).toContain('"write_artifact":false');
      expect(text).toContain("write_artifact:true");
      expect(text).toContain("Library/SGGUnityMcp/job-artifacts/asset-reports/");
      expect(text).toContain('readable_with:"job.artifact.read"');
      expect(text).toContain("next_valid_actions");
    }
  });

  it("advertises job support for expensive index rebuilds", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("SupportsJob = true");
  });

  it("reports duplicate GUID risk hints from Unity meta files", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("DuplicateGuidPaths(asset.Guid, asset.Path, 20");
    expect(source).toContain("Directory.EnumerateFiles(rootPath, \"*.meta\", SearchOption.AllDirectories)");
    expect(source).toContain("MetaFileHasGuid");
    expect(source).toContain('\\"duplicate_guid_scan_supported\\":true');
    expect(source).toContain('\\"duplicate_guid_paths\\"');
  });

  it("reports missing script risk hints from YAML script references", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("MissingScriptReferences(asset.Path, 20");
    expect(source).toContain('line.IndexOf("m_Script:", StringComparison.Ordinal)');
    expect(source).toContain("lineNumber++");
    expect(source).toContain("ExtractInlineGuid(line)");
    expect(source).toContain("AssetDatabase.GUIDToAssetPath(guid)");
    expect(source).toContain('\\"missing_script_scan_supported\\":true');
    expect(source).toContain('\\"missing_script_guids\\"');
    expect(source).toContain('\\"missing_script_locations\\"');
  });

  it("reports broader broken GUID references from YAML assets", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain("BrokenReferenceReferences(asset.Path, missingScriptGuids, 20");
    expect(source).toContain('line.IndexOf("guid:", StringComparison.Ordinal)');
    expect(source).toContain("IsNullGuid(guid)");
    expect(source).toContain("ContainsOrdinalIgnoreCase(ignoredGuids, guid)");
    expect(source).toContain("ContainsGuidReference(brokenReferences, guid)");
    expect(source).toContain('\\"broken_reference_scan_supported\\":true');
    expect(source).toContain('\\"broken_reference_guids\\"');
    expect(source).toContain('\\"broken_reference_locations\\"');
  });

  it("persists the in-memory asset index to a project-local cache", () => {
    const source = fs.readFileSync(assetIndexCommandsPath, "utf8");

    expect(source).toContain('DefaultCacheRelativeDirectory = "Library/SGGUnityMcp"');
    expect(source).toContain('SettingsAssetIndexCacheDir = "asset_index_cache_dir"');
    expect(source).toContain("JsonUtil.ExtractString(context.SettingsJson, SettingsAssetIndexCacheDir)");
    expect(source).toContain("TryLoadCache(context)");
    expect(source).toContain("WriteCache(entries, builtAtUtc, context)");
    expect(source).toContain("File.WriteAllText(cachePath");
    expect(source).toContain('\\"cache_supported\\":true');
    expect(source).toContain('\\"cache_configured\\"');
    expect(source).toContain('\\"cache_loaded\\"');
  });

  it("invalidates the project-local cache after Unity asset changes", () => {
    const commandsSource = fs.readFileSync(assetIndexCommandsPath, "utf8");
    const invalidatorSource = fs.readFileSync(assetIndexCacheInvalidatorPath, "utf8");

    expect(commandsSource).toContain("internal static void InvalidateCache()");
    expect(invalidatorSource).toContain("AssetPostprocessor");
    expect(invalidatorSource).toContain("OnPostprocessAllAssets(");
    expect(invalidatorSource).toContain("AssetIndexCommands.InvalidateCache()");
  });

  it("documents and allows local cloudless text asset inspection in policy", () => {
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    for (const text of [docs, docsRu, profiles, policyExample]) {
      expect(text).toContain("asset.list");
      expect(text).toContain("asset.text.read");
      expect(text).toContain("asset.text.find");
      expect(text).toContain("asset.text.search");
    }

    expect(docs).toContain("cloudless local reads");
    expect(docs).toContain("do no network access");
    expect(docs).toContain("default to literal substring search");
    expect(docs).toContain("bounded regex mode");
    expect(docs).toContain("project-wide text search");
    expect(docsRu).toContain("cloudless local reads");
    expect(docsRu).toContain("не используют network access");
    expect(docsRu).toContain("по умолчанию делают literal substring search");
    expect(docsRu).toContain("bounded regex mode");
    expect(docsRu).toContain("project-wide text search");
  });
});
