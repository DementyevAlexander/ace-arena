import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const taxonomyCommandsPath = path.join(packageRoot, "Editor", "Commands", "ProjectAssetTaxonomyCommands.cs");

describe("Unity project asset taxonomy command source-contract", () => {
  it("declares a bounded project asset taxonomy command through the catalog", () => {
    const source = fs.readFileSync(taxonomyCommandsPath, "utf8");

    expect(source).toContain('"project.assets.taxonomy"');
    expect(source).toContain('const string FeatureFlag = "asset_index"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('Capability = "asset_index"');
    expect(source).toContain('ResultShape = "bounded_tree"');
    expect(source).toContain('Tags = new string[] { "project", "asset", "taxonomy", "unity-ai-assistant" }');
  });

  it("maps Unity.GetProjectData to local AssetDatabase metadata only", () => {
    const source = fs.readFileSync(taxonomyCommandsPath, "utf8");

    expect(source).toContain("Unity.GetProjectData");
    expect(source).toContain("AssetDatabase.FindAssets(string.Empty, new[] { folderPath })");
    expect(source).toContain("AssetDatabase.GUIDToAssetPath");
    expect(source).toContain("AssetDatabase.GetMainAssetTypeAtPath");
    expect(source).toContain("AssetDatabase.IsValidFolder");
    expect(source).toContain("taxonomy_markdown");
    expect(source).toContain("matched_items");
    expect(source).toContain("included_items");
    expect(source).toContain("truncated");
  });

  it("validates bounded Unity-relative folder prefixes and output limits", () => {
    const source = fs.readFileSync(taxonomyCommandsPath, "utf8");

    expect(source).toContain('\\"max_depth\\"');
    expect(source).toContain('\\"maximum\\":16');
    expect(source).toContain('\\"max_items\\"');
    expect(source).toContain('\\"maximum\\":5000');
    expect(source).toContain('\\"max_chars\\"');
    expect(source).toContain('\\"maximum\\":100000');
    expect(source).toContain("RequireFolderPrefix");
    expect(source).toContain("PathGuard.RequireUnityAssetPath");
    expect(source).toContain('folderPath = "Assets"');
    expect(source).toContain('folderPath = "Packages"');
    expect(source).toContain("path_prefix must be a safe Unity-relative folder under Assets/ or Packages/");
    expect(source).toContain("path_prefix folder was not found");
  });

  it("does not read files, load assets, use network, or mutate the project", () => {
    const source = fs.readFileSync(taxonomyCommandsPath, "utf8");

    expect(source).not.toContain("File.");
    expect(source).not.toContain("Directory.");
    expect(source).not.toContain("LoadAssetAtPath");
    expect(source).not.toContain("ImportAsset");
    expect(source).not.toContain("Refresh");
    expect(source).not.toContain("CreateAsset");
    expect(source).not.toContain("DeleteAsset");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
    expect(source).not.toContain("Process.Start");
  });
});
