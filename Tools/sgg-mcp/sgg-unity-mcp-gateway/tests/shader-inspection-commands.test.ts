import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "ShaderInspectionCommands.cs");

describe("shader inspection command source-contract", () => {
  it("declares shader commands as read-only asset commands", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('"shader.list"');
    expect(source).toContain('"shader.dependencies"');
    expect(source).toContain('"shader.references.report"');
    expect(source).toContain('Title = "List shader assets"');
    expect(source).toContain('Title = "Inspect shader dependencies"');
    expect(source).toContain('Title = "Report shader references"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('const string FeatureFlag = "asset_readonly"');
    expect(source).toContain('Capability = FeatureFlag');
    expect(source).toContain('FeatureFlag = FeatureFlag');
    expect(source).toContain('ResultShape = "bounded_list"');
  });

  it("exposes bounded shader inventory schema controls", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("path_prefix");
    expect(source).toContain("query");
    expect(source).toContain("kind");
    expect(source).toContain("include_packages");
    expect(source).toContain("start_index");
    expect(source).toContain("max_results");
    expect(source).toContain("max_scan_assets");
    expect(source).toContain("asset_path");
    expect(source).toContain("recursive");
    expect(source).toContain("include_text_includes");
    expect(source).toContain("include_assetdatabase");
    expect(source).toContain("include_yaml_guid_refs");
    expect(source).toContain("max_dependencies");
    expect(source).toContain("max_include_matches");
    expect(source).toContain("max_scan_bytes");
    expect(source).toContain("MaxResults = 1000");
    expect(source).toContain("MaxScanAssets = 100000");
    expect(source).toContain("MaxDependencies = 500");
    expect(source).toContain("MaxIncludeMatches = 200");
    expect(source).toContain("MaxShaderTextBytes = 262144");
    expect(source).toContain('"shadergraph_subgraph"');
    expect(source).toContain('"include"');
  });

  it("recognizes shader and include asset extensions", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('AssetDatabase.FindAssets(string.Empty, searchRoots)');
    expect(source).toContain('AssetDatabase.GetMainAssetTypeAtPath(assetPath)');
    expect(source).toContain('PathGuard.RequireUnityAssetPath(candidate)');
    expect(source).toContain('".shader"');
    expect(source).toContain('".shadergraph"');
    expect(source).toContain('".shadersubgraph"');
    expect(source).toContain('".compute"');
    expect(source).toContain('".hlsl"');
    expect(source).toContain('".cginc"');
    expect(source).toContain('".mat"');
    expect(source).toContain("Shader assets are asset/reference inventory, not C# assembly membership.");
  });

  it("inspects shader dependencies and bounded include references", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("ShaderDependencies");
    expect(source).toContain("AssetDatabase.GetDependencies(assetPath, recursive)");
    expect(source).toContain("ShaderDependenciesJson");
    expect(source).toContain("ShaderIncludesJson");
    expect(source).toContain("TryParseIncludeLine");
    expect(source).toContain("#include_with_pragmas");
    expect(source).toContain("#include");
    expect(source).toContain("ResolveIncludeAssetPath");
    expect(source).toContain("ReadUtf8Prefix");
    expect(source).toContain("PackageManagerPackageInfo.FindForAssetPath(assetPath)");
    expect(source).toContain("include_references_truncated");
    expect(source).toContain("AssetDatabase dependencies may omit text-only includes");
  });

  it("reports incoming shader references without mutating assets", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("ShaderReferencesReport");
    expect(source).toContain("RequireShaderReferenceTarget");
    expect(source).toContain("AssetDatabaseReferencesTarget");
    expect(source).toContain("TextIncludesReferenceTarget");
    expect(source).toContain("YamlGuidReferencesTarget");
    expect(source).toContain("IsYamlGuidScanCandidate");
    expect(source).toContain("ReferenceReportEntryJson");
    expect(source).toContain("reference_kinds");
    expect(source).toContain("assetdatabase_dependency");
    expect(source).toContain("shader_include_text");
    expect(source).toContain("yaml_guid");
    expect(source).toContain("Incoming reference scanning is bounded and best-effort");
  });

  it("does not mutate, import, compile, or use ShaderUtil", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.Refresh");
    expect(source).not.toContain("AssetDatabase.CreateAsset");
    expect(source).not.toContain("AssetDatabase.MoveAsset");
    expect(source).not.toContain("AssetDatabase.DeleteAsset");
    expect(source).not.toContain("File.Write");
    expect(source).not.toContain("Directory.CreateDirectory");
    expect(source).not.toContain("SaveAssets");
    expect(source).not.toContain("ShaderUtil");
    expect(source).not.toContain("CompilationPipeline");
    expect(source).not.toContain("LoadAssetAtPath");
  });
});
