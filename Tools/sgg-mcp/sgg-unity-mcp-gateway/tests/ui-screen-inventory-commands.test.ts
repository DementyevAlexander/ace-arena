import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "UiScreenInventoryCommands.cs");
const metaPath = `${commandPath}.meta`;
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const securityDocsPath = path.join(process.cwd(), "docs", "en", "reference", "security.md");

describe("UI screen inventory command source-contract", () => {
  it("declares a read-only feature-gated cross-UI inventory command", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const meta = fs.readFileSync(metaPath, "utf8");

    expect(source).toContain('"ui.screen.inventory"');
    expect(source).toContain('Title = "UI screen inventory"');
    expect(source).toContain('const string FeatureFlag = "ui_readonly"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('ResultShape = "bounded_list"');
    expect(source).toContain('SupportsJob = true');
    expect(source).toContain('Tags = new string[] { "ui", "screen", "inventory", "ugui", "uitk", "tmp", "localization", "readonly" }');
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("declares a read-only feature-gated focused screen inspect command", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('"ui.screen.inspect"');
    expect(source).toContain('Title = "UI screen inspect"');
    expect(source).toContain('ResultShape = "object_details"');
    expect(source).toContain('FeatureFlag = FeatureFlag');
    expect(source).toContain('\\"asset_path\\"');
    expect(source).toContain('\\"guid\\"');
    expect(source).toContain('\\"screen_id\\"');
    expect(source).toContain('ResolveInspectAssetPath');
    expect(source).toContain('DelegationPlanJson');
    expect(source).toContain('manual_call_required');
    expect(source).toContain('normal_policy_required');
  });

  it("declares a read-only feature-gated cross-UI interaction map command", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('"ui.interaction.map"');
    expect(source).toContain('Title = "UI interaction map"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('ResultShape = "bounded_list"');
    expect(source).toContain('InteractionMapSchemaJson');
    expect(source).toContain('InteractionMapOutputLimitJson');
    expect(source).toContain('TryAnalyzeInteractionMap');
    expect(source).toContain('runtime_smoke_eligibility');
    expect(source).toContain('executes_in_this_command');
    expect(source).toContain('"ugui.navigation.analyze"');
    expect(source).toContain('"ugui.events.extract"');
    expect(source).toContain('"ugui.interaction.smoke"');
    expect(source).toContain('"uitk.bindings.validate"');
    expect(source).toContain('"uitk.interaction.smoke"');
    expect(source).toContain('"uitk.interaction.smoke"');
    expect(source).toContain('uitk_runtime_smoke_eligibility');
    expect(source).toContain('requires_exact_loaded_uidocument');
    expect(source).not.toContain('\\"dry_run\\":true,\\"confirm_execute\\":false');
  });

  it("bounds inputs and exposes provenance plus lower-level follow-ups", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("SchemaJson");
    expect(source).toContain("OutputLimitJson");
    expect(source).toContain('\\"path_prefix\\"');
    expect(source).toContain('\\"include_scenes\\"');
    expect(source).toContain('\\"include_prefabs\\"');
    expect(source).toContain('\\"include_uxml\\"');
    expect(source).toContain('\\"include_panelsettings\\"');
    expect(source).toContain('\\"max_scan_assets\\"');
    expect(source).toContain('\\"max_scan_bytes\\"');
    expect(source).toContain('const int MaxResults = 200');
    expect(source).toContain('const int MaxScanAssets = 100000');
    expect(source).toContain('const int MaxScanBytes = 1048576');
    expect(source).toContain('\\"source\\":\\"asset_database_and_bounded_serialized_text\\"');
    expect(source).toContain('\\"lower_level_commands\\"');
    expect(source).toContain('\\"screenshot_targets\\"');
    expect(source).toContain('\\"delegation_plan\\"');
    expect(source).toContain('\\"max_event_samples\\"');
    expect(source).toContain('"ui.text.references.report"');
    expect(source).toContain('"tmp.font_asset.coverage.check"');
    expect(source).toContain('loaded UIDocument runtime identity');
  });

  it("detects uGUI, UITK, TMP and localization markers without opening scenes", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('AssetDatabase.FindAssets(string.Empty, searchRoots)');
    expect(source).toContain('AssetDatabase.GetMainAssetTypeAtPath(assetPath)');
    expect(source).toContain('CountOccurrences(text, "--- !u!223")');
    expect(source).toContain('CountOccurrences(text, "--- !u!224")');
    expect(source).toContain('CountRegex(text, "<\\\\s*[A-Za-z_][A-Za-z0-9_.:-]*")');
    expect(source).toContain('m_fontAsset');
    expect(source).toContain('m_TableReference');
    expect(source).toContain('m_TableEntryReference');
    expect(source).toContain('PanelSettings');
    expect(source).toContain('UIDocument');
    expect(source).toContain('m_Navigation');
    expect(source).toContain('m_PersistentCalls');
    expect(source).toContain('m_MethodName');
    expect(source).toContain('binding-path');
  });

  it("does not mutate, import, refresh, save or capture screenshots", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("File.OpenRead");
    expect(source).toContain("PathGuard.RequireUnityAssetPath");
    expect(source).not.toContain("EditorSceneManager.OpenScene");
    expect(source).not.toContain("PrefabStageUtility");
    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.Refresh");
    expect(source).not.toContain("AssetDatabase.CreateAsset");
    expect(source).not.toContain("AssetDatabase.MoveAsset");
    expect(source).not.toContain("AssetDatabase.DeleteAsset");
    expect(source).not.toContain("AssetDatabase.SaveAssets");
    expect(source).not.toContain("File.Write");
    expect(source).not.toContain("Directory.CreateDirectory");
    expect(source).not.toContain("ScreenCapture");
    expect(source).not.toContain("CaptureScreenshot");
    expect(source).not.toContain("EditorApplication.EnterPlaymode");
    expect(source).not.toContain("EditorApplication.isPlaying = true");
  });

  it("documents and exposes the cross-UI readonly feature intentionally", () => {
    const cli = fs.readFileSync(cliPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const securityDocs = fs.readFileSync(securityDocsPath, "utf8");

    expect(cli).toContain('"ui.screen.": "ui_readonly"');
    expect(cli).toContain('"ui.interaction.": "ui_readonly"');
    expect(gatewayExample).toContain('"ui_readonly": false');
    expect(policyExample).toContain('"ui.screen.inventory"');
    expect(policyExample).toContain('"ui.screen.inspect"');
    expect(policyExample).toContain('"ui.interaction.map"');
    expect(policyProfiles).toContain('"ui.screen.inventory"');
    expect(policyProfiles).toContain('"ui.screen.inspect"');
    expect(policyProfiles).toContain('"ui.interaction.map"');
    expect(policyProfiles).toContain('"ui_readonly"');
    expect(docs).toContain("Feature-gated cross-UI read-only commands");
    expect(docs).toContain("ui.screen.inventory");
    expect(docs).toContain("ui.screen.inspect");
    expect(docs).toContain("ui.interaction.map");
    expect(docsRu).toContain("ui.screen.inventory");
    expect(docsRu).toContain("ui.screen.inspect");
    expect(docsRu).toContain("ui.interaction.map");
    expect(securityDocs).toContain("features.ui_readonly");
  });
});
