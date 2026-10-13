import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "UiTextInventoryCommands.cs");
const metaPath = `${commandPath}.meta`;
const contractsPath = path.join(process.cwd(), "src", "mcp", "contracts.ts");
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const securityDocsPath = path.join(process.cwd(), "docs", "en", "reference", "security.md");

describe("UI text inventory command source-contract", () => {
  it("declares read-only feature-gated UI text commands", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const meta = fs.readFileSync(metaPath, "utf8");

    expect(source).toContain('"ui.text.inventory"');
    expect(source).toContain('"ui.text.references.report"');
    expect(source).toContain('Title = "UI text inventory"');
    expect(source).toContain('Title = "UI text references report"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('const string FeatureFlag = "ui_text_readonly"');
    expect(source).toContain('Capability = FeatureFlag');
    expect(source).toContain('FeatureFlag = FeatureFlag');
    expect(source).toContain('ResultShape = "bounded_list"');
    expect(source).toContain('ResultShape = "status"');
    expect(source).toContain('SupportsJob = true');
    expect(source).toContain('Tags = new string[] { "ui", "text", "localization", "tmp", "inventory", "readonly" }');
    expect(source).toContain('Tags = new string[] { "ui", "text", "localization", "tmp", "references", "report", "readonly" }');
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("bounds inputs and output for cross-family UI text inventory", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("UiTextInventorySchemaJson");
    expect(source).toContain("UiTextReferencesReportSchemaJson");
    expect(source).toContain("UiTextInventoryOutputLimitJson");
    expect(source).toContain("UiTextReferencesReportOutputLimitJson");
    expect(source).toContain('\\"path_prefix\\"');
    expect(source).toContain('\\"include_scenes\\"');
    expect(source).toContain('\\"include_prefabs\\"');
    expect(source).toContain('\\"include_uxml\\"');
    expect(source).toContain('\\"include_text_assets\\"');
    expect(source).toContain('\\"include_localization_assets\\"');
    expect(source).toContain('\\"max_scan_assets\\"');
    expect(source).toContain('\\"max_scan_bytes\\"');
    expect(source).toContain('\\"max_text_chars\\"');
    expect(source).toContain('\\"max_sources\\"');
    expect(source).toContain('const int MaxResults = 500');
    expect(source).toContain('const int MaxScanAssets = 100000');
    expect(source).toContain('const int MaxScanBytes = 1048576');
    expect(source).toContain('const int MaxEntriesPerAsset = 100');
  });

  it("scans known UI/localization text surfaces with TMP hints", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("CandidateUiTextSourcePaths");
    expect(source).toContain('AssetDatabase.FindAssets(string.Empty, searchRoots)');
    expect(source).toContain("CollectYamlTextEntries");
    expect(source).toContain("CollectUxmlTextEntries");
    expect(source).toContain("CollectXliffTextEntries");
    expect(source).toContain("CollectTextAssetLines");
    expect(source).toContain("m_Text");
    expect(source).toContain("m_text");
    expect(source).toContain("m_fontAsset");
    expect(source).toContain("m_TableReference");
    expect(source).toContain("m_TableEntryReference");
    expect(source).toContain("tmp_font_asset_guid");
    expect(source).toContain("localization_hints");
    expect(source).toContain("TmpFontAssetsJson");
    expect(source).toContain("LocalizationOriginsJson");
    expect(source).toContain("tmp_font_assets");
    expect(source).toContain("localization_origins");
    expect(source).toContain("sorted_unique_unicode_codepoints");
    expect(source).toContain("CodepointLabelsJson");
    expect(source).toContain("UnicodeRangesJson");
  });

  it("does not mutate, import, refresh, bake, or depend on TextMeshPro", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("File.OpenRead");
    expect(source).toContain("PathGuard.RequireUnityAssetPath");
    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.Refresh");
    expect(source).not.toContain("AssetDatabase.CreateAsset");
    expect(source).not.toContain("AssetDatabase.MoveAsset");
    expect(source).not.toContain("AssetDatabase.DeleteAsset");
    expect(source).not.toContain("AssetDatabase.SaveAssets");
    expect(source).not.toContain("File.Write");
    expect(source).not.toContain("Directory.CreateDirectory");
    expect(source).not.toContain("PrefabUtility.SaveAsPrefabAsset");
    expect(source).not.toContain("using TMPro");
    expect(source).not.toContain("TMPro.TMP_FontAsset ");
    expect(source).not.toContain("CreateFontAsset");
    expect(source).not.toContain("TryAddCharacters");
  });

  it("documents and exposes the UI text readonly feature intentionally", () => {
    const contracts = fs.readFileSync(contractsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const securityDocs = fs.readFileSync(securityDocsPath, "utf8");

    expect(contracts).toContain('"ui"');
    expect(cli).toContain('"ui.text.": "ui_text_readonly"');
    expect(gatewayExample).toContain('"ui_text_readonly": false');
    expect(policyExample).toContain('"ui.text.inventory"');
    expect(policyExample).toContain('"ui.text.references.report"');
    expect(policyProfiles).toContain('"ui.text.inventory"');
    expect(policyProfiles).toContain('"ui.text.references.report"');
    expect(policyProfiles).toContain('"ui_text_readonly"');
    expect(docs).toContain("Feature-gated UI text read-only commands");
    expect(docs).toContain("ui.text.inventory");
    expect(docs).toContain("ui.text.references.report");
    expect(docsRu).toContain("ui.text.inventory");
    expect(docsRu).toContain("ui.text.references.report");
    expect(securityDocs).toContain("features.ui_text_readonly");
  });
});
