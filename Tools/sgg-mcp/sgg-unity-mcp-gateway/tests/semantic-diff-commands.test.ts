import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const semanticDiffCommandsPath = path.join(packageRoot, "Editor", "Commands", "SemanticDiffCommands.cs");

describe("Unity semantic diff command source-contract", () => {
  it("declares semantic diff, inspect, preview and merge command IDs", () => {
    const source = fs.readFileSync(semanticDiffCommandsPath, "utf8");

    expect(source).toContain('"diff.unity_yaml.semantic"');
    expect(source).toContain('"diff.unity_yaml.preview"');
    expect(source).toContain('"diff.unity_yaml.inspect_change"');
    expect(source).toContain('"merge.unity_yaml.preview"');
    expect(source).toContain('"merge.unity_yaml.plan"');
    expect(source).toContain('"merge.unity_yaml.apply"');
  });

  it("keeps Unity YAML diff commands feature-gated and gates apply as write", () => {
    const source = fs.readFileSync(semanticDiffCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "semantic_unity_diff"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(5);
    expect(source.match(/Risk = "write"/g)).toHaveLength(1);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(6);
  });

  it("validates Unity-relative supported YAML asset paths", () => {
    const source = fs.readFileSync(semanticDiffCommandsPath, "utf8");

    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawPath)");
    expect(source).toContain('EndsWith(".unity", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('EndsWith(".prefab", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('EndsWith(".mat", StringComparison.OrdinalIgnoreCase)');
    expect(source).toContain('EndsWith(".asset", StringComparison.OrdinalIgnoreCase)');
  });

  it("parses Unity YAML object headers and bounds output", () => {
    const source = fs.readFileSync(semanticDiffCommandsPath, "utf8");

    expect(source).toContain('line.StartsWith("--- !u!"');
    expect(source).toContain('StartsWith("m_Name:"');
    expect(source).toContain("StableHash(blockText)");
    expect(source).toContain("ParseScalarFields(blockText)");
    expect(source).toContain("BuildFieldChanges(left, right, 20");
    expect(source).toContain("field_changes");
    expect(source).toContain('\\"maximum\\":500');
    expect(source).toContain('max_field_changes_per_object');
  });

  it("implements focused change inspection and conservative merge apply", () => {
    const source = fs.readFileSync(semanticDiffCommandsPath, "utf8");

    expect(source).toContain("InspectChange(");
    expect(source).toContain("max_field_changes");
    expect(source).toContain("MergeApply(");
    expect(source).toContain("MergePlan(");
    expect(source).toContain("BuildMergePlan");
    expect(source).toContain("max_actions");
    expect(source).toContain("RequireUnityYamlPathForWrite");
    expect(source).toContain("ConservativeMergeSource");
    expect(source).toContain("AssetDatabase.ImportAsset(outputPath)");
  });
});
