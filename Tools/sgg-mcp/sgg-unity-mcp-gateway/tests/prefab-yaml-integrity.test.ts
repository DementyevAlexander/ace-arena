import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const helperPath = path.join(packageRoot, "Editor", "Commands", "PrefabYamlIntegrity.cs");
const fixturePath = path.join(process.cwd(), "tests", "fixtures", "prefab-yaml-integrity", "Assets", "Prefabs", "StaleTransformChild.prefab");

describe("MCP-PR-054 bounded prefab YAML parser source contract", () => {
  it("keeps prefab analysis bounded, read-only, and streaming from a FileStream", () => {
    const source = fs.readFileSync(helperPath, "utf8");

    expect(source).toContain("internal static class PrefabYamlIntegrity");
    expect(source).toContain("internal static PrefabYamlIntegrityResult AnalyzePrefabAsset");
    expect(source).toContain("internal static PrefabYamlIntegrityResult AnalyzeYamlForTests");
    expect(source).toContain("internal static string PrefabImportDiagnosticsJson");
    expect(source).toContain("new FileStream");
    expect(source).toContain("FileMode.Open");
    expect(source).toContain("FileAccess.Read");
    expect(source).toContain("FileOptions.SequentialScan");
    expect(source).toContain("PathGuard.RequireUnityAssetPath");
    expect(source).toContain("ProjectPaths.ProjectRoot");
    expect(source).toContain("MaxIssues = 64");
    expect(source).toContain("DefaultScanBytes = 262144");
    expect(source).toContain("HardMaxScanBytes = 1048576");
    expect(source).toContain("MaxEvidence = 16");
    expect(source).toContain("MaxObjects");
    expect(source).toContain("MaxChildrenPerObject");
    expect(source).toContain("MaxPathChars");
    expect(source).toContain("MaxNameChars");
    expect(source).toContain("MaxLineNumber");
    expect(source).toContain("ContainsReparsePoint");
    expect(source).toContain("FileAttributes.ReparsePoint");
    expect(source).toContain("StructurallyValid");
    expect(source).toContain("invalid_prefab_yaml");
    expect(source).toContain('"prefab.write_risk_report"');
    expect(source).toContain('"prefab.edit.preview"');
    expect(source).toContain('"logs.console.query"');
    expect(source).toContain("parent_transform_file_id");
    expect(source).toContain("stale_child_file_id");
    expect(source).toContain("child_reference_line");
    expect(source).toContain("issues_truncated");
    expect(source).not.toContain("File.ReadAllText");
    expect(source).not.toContain("File.ReadAllLines");
    expect(source).not.toContain("File.WriteAll");
    expect(source).not.toContain("AssetDatabase.");
    expect(source).not.toContain("PrefabUtility.");
    expect(source).not.toContain("SaveAssets");
    expect(source).not.toContain("ImportAsset");
    expect(source).not.toContain("Refresh");
  });

  it("keeps the fixture in bounded Unity YAML shape with the stale child reference", () => {
    const fixture = fs.readFileSync(fixturePath, "utf8");

    expect(fixture).toContain("%YAML 1.1");
    expect(fixture).toContain("--- !u!224 &100100");
    expect(fixture).toContain("--- !u!1 &100101");
    expect(fixture).toContain("m_Name: Canvas");
    expect(fixture).toContain("m_Name: Panel");
    expect(fixture).toContain("m_Father: {fileID: 100100}");
    expect(fixture).toContain("- {fileID: 999999}");
    expect(fixture).toContain("m_Children:");
    expect(fixture).toContain("--- !u!1001 &100300");
    expect(fixture).toContain("m_SourcePrefab:");
  });
});
