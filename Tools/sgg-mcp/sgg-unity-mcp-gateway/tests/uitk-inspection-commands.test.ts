import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const uitkCommandsPath = path.join(packageRoot, "Editor", "Commands", "UitkInspectionCommands.cs");
const contractsPath = path.join(process.cwd(), "src", "mcp", "contracts.ts");
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");

describe("Unity UI Toolkit command descriptors", () => {
  it("declares the UITK command IDs", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).toContain('"uitk.scan"');
    expect(source).toContain('"uitk.plan"');
    expect(source).toContain('"uitk.generate"');
    expect(source).toContain('"uitk.preview"');
    expect(source).toContain('"uitk.document.inspect"');
    expect(source).toContain('"uitk.uxml.inspect"');
    expect(source).toContain('"uitk.uss.inspect"');
    expect(source).toContain('"uitk.panelsettings.inspect"');
    expect(source).toContain('"uitk.validate"');
    expect(source).toContain('"uitk.bindings.validate"');
    expect(source).toContain('"uitk.layout.analyze"');
    expect(source).toContain('"uitk.references.report"');
    expect(source).toContain('"uitk.resource.read"');
    expect(source).toContain('"uitk.document.create"');
    expect(source).toContain('"uitk.binder.generate"');
    expect(source).toContain('"uitk.template.generate"');
    expect(source).toContain('"uitk.report"');
  });

  it("keeps UITK read and safe-write commands feature-gated", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).toContain('const string InspectionFeatureFlag = "uitk_inspection"');
    expect(source).toContain('const string ValidationFeatureFlag = "uitk_validation"');
    expect(source).toContain('const string SafeWritesFeatureFlag = "uitk_safe_writes"');
    expect(source).toContain('const string MigrationFeatureFlag = "uitk_migration"');
    expect(source).toContain('const string MigrationPreviewFeatureFlag = "uitk_migration_preview"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(11);
    expect(source.match(/Idempotency = "safe"/g)).toHaveLength(11);
    expect(source.match(/Risk = "write"/g)).toHaveLength(6);
    expect(source.match(/Idempotency = "unsafe"/g)).toHaveLength(6);
    expect(source.match(/FeatureFlag = InspectionFeatureFlag/g)).toHaveLength(9);
    expect(source.match(/FeatureFlag = ValidationFeatureFlag/g)).toHaveLength(2);
    expect(source.match(/FeatureFlag = SafeWritesFeatureFlag/g)).toHaveLength(3);
    expect(source.match(/FeatureFlag = MigrationFeatureFlag/g)).toHaveLength(2);
    expect(source.match(/FeatureFlag = MigrationPreviewFeatureFlag/g)).toHaveLength(1);
    expect(source).not.toContain("UITK-0 registers safe command descriptors");
    expect(source).toContain('"status\\":\\"not_implemented\\"');
    expect(source).toContain('"read_only\\":true');
  });

  it("implements UITK-1 read-only inspection and cached reports", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult MigrationScan");
    expect(source).toContain("UguiInspectionCommands.CanvasScan(context)");
    expect(source).toContain('"migration_mode\\":\\"ugui_to_uitk\\"');
    expect(source).toContain('"source_command\\":\\"ugui.canvas.scan\\"');
    expect(source).toContain("public static CommandExecutionResult MigrationPlan");
    expect(source).toContain("UguiInspectionCommands.CanvasReport(new McpCommandContext");
    expect(source).toContain('MigrationPlanArtifactRoot = "Library/SGGUnityMcp/job-artifacts/uitk-migration-plans/"');
    expect(source).toContain('"artifacts_to_create\\"');
    expect(source).toContain('"manual_tasks\\"');
    expect(source).toContain('"node_mappings\\":[]');
    expect(source).toContain("public static CommandExecutionResult MigrationGenerate");
    expect(source).toContain("FindResource(PlanResourceUri(planId))");
    expect(source).toContain("GenerationResourceUri(generationId)");
    expect(source).toContain('"generation_resource\\"');
    expect(source).toContain('"mode\\":\\"create_only\\"');
    expect(source).toContain('"legacy_modified\\":false');
    expect(source).toContain('"files_to_modify\\":[]');
    expect(source).toContain('"target_name\\"');
    expect(source).toContain(".conversion-report.md");
    expect(source).toContain(".migration-map.json");
    expect(source).toContain("public static CommandExecutionResult DocumentInspect");
    expect(source).toContain("public static CommandExecutionResult UxmlInspect");
    expect(source).toContain("public static CommandExecutionResult UssInspect");
    expect(source).toContain("public static CommandExecutionResult PanelSettingsInspect");
    expect(source).toContain("public static CommandExecutionResult Report");
    expect(source).toContain("GetComponentsInChildren<UIDocument>");
    expect(source).toContain("XDocument.Parse");
    expect(source).toContain("ExtractUssRules");
    expect(source).toContain("AssetPathFromReference");
    expect(source).toContain("SerializedObject(asset)");
    expect(source).toContain("PanelSettings");
    expect(source).toContain("ReadTextAsset");
    expect(source).toContain("ReadBoundedBytes");
    expect(source).toContain("StoreReport");
    expect(source).toContain("FindReportBySourceId");
    expect(source).toContain("StoreResource");
    expect(source).toContain("ResourceRefsJson");
    expect(source).toContain("public static CommandExecutionResult ResourceRead");
    expect(source).toContain('"unity-ui://uitk/"');
    expect(source).toContain("MaxCachedReports = 32");
    expect(source).toContain("MaxCachedResources = 64");
    expect(source).toContain('"documents_found\\"');
    expect(source).toContain('"elements_found\\"');
    expect(source).toContain('"rules_found\\"');
    expect(source).toContain('"references_found\\"');
    expect(source).toContain('"component_index\\"');
    expect(source).toContain('runtime_interaction_eligibility');
    expect(source).toContain('interaction_command_guidance');
    expect(source).toContain('uitk.interaction.smoke');
    expect(source).not.toContain('\\"dry_run\\":true,\\"confirm_execute\\":false');
  });

  it("implements UITK-2 read-only validation and binder query checks", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult Validate");
    expect(source).toContain("public static CommandExecutionResult BindingsValidate");
    expect(source).toContain("ValidateUxmlPath");
    expect(source).toContain("ValidateUssPath");
    expect(source).toContain("ValidateBinderPaths");
    expect(source).toContain("LoadMigrationValidationBundle");
    expect(source).toContain("ValidateMigrationBundle");
    expect(source).toContain('"generation_id"');
    expect(source).toContain('"migration_map_path"');
    expect(source).toContain("ExtractBinderQueries");
    expect(source).toContain("CompileStatusJson");
    expect(source).toContain("FindingList");
    expect(source).toContain('"read_only\\":true');
    expect(source).toContain('"migration_gate\\"');
    expect(source).toContain('"migration_bundle\\"');
    expect(source).toContain('"UITK_MIGRATION_ARTIFACT_MISSING"');
    expect(source).toContain('"findings_by_severity\\"');
    expect(source).toContain('"UITK008"');
  });

  it("implements UITK migration preview artifacts and pixel diff without apply behavior", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult MigrationPreview");
    expect(source).toContain('Capability = "uitk_migration_preview"');
    expect(source).toContain('MigrationPreviewArtifactRoot = "Library/SGGUnityMcp/job-artifacts/uitk-previews/"');
    expect(source).toContain("BuildPreviewScreenshotRef");
    expect(source).toContain("ExecutePreviewScreenshotCaptures");
    expect(source).toContain("PreviewGameViewScreenshotArgsJson");
    expect(source).toContain("PreviewCaptureResult");
    expect(source).toContain("ComparePreviewScreenshots");
    expect(source).toContain("PixelComparisonResult");
    expect(source).toContain("PreviewBoundsDiffJson");
    expect(source).toContain("PreviewScreenshotTargetsJson");
    expect(source).toContain("PreviewSourceResourceRef");
    expect(source).toContain("WritePreviewArtifact");
    expect(source).toContain("WritePreviewBinaryArtifact");
    expect(source).toContain('\\"pixel_threshold\\":{\\"type\\":\\"integer\\"');
    expect(source).toContain('\\"capture_screenshots\\":{\\"type\\":\\"boolean\\"');
    expect(source).toContain('\\"capture_role\\":{\\"type\\":\\"string\\"');
    expect(source).toContain('\\"automatic_capture\\":\\"explicit_capture_screenshots_only\\"');
    expect(source).toContain('\\"screenshot_target_selection\\":true');
    expect(source).toContain('"diff_mode\\":\\"metadata_bounds_and_pixel_preflight\\"');
    expect(source).toContain('pixel_diff_available\\":" + JsonUtil.Bool(pixelDiff.Computed)');
    expect(source).toContain('\\"screenshot_targets\\":" + screenshotTargetsJson');
    expect(source).toContain('capture.Enabled ? "capture_execution" : "target_selection_only"');
    expect(source).toContain('\\"role\\":\\"old_ugui\\"');
    expect(source).toContain('\\"role\\":\\"new_uitk\\"');
    expect(source).toContain("ugui.canvas.report(source_scan_id)");
    expect(source).toContain('capture_started=").Append(capture.CaptureStarted ? "true" : "false")');
    expect(source).toContain('\\"capture_started\\":" + JsonUtil.Bool(capture.CaptureStarted)');
    expect(source).toContain("EditorScreenshotCommands.GameViewScreenshot");
    expect(source).toContain("game.view.screenshot.status");
    expect(source).toContain("automatic capture skipped to avoid overwriting an existing artifact path");
    expect(source).toContain(".pixel-diff.png");
    expect(source).toContain("ImageConversion.LoadImage");
    expect(source).toContain('"screenshot_contract\\"');
    expect(source).toContain("scene.view.screenshot");
    expect(source).toContain("game.view.screenshot");
    expect(source).toContain("camera.screenshot");
    expect(source).not.toContain('"uitk.apply"');
    expect(source).not.toContain('"uitk.rollback"');
  });

  it("implements UITK-3 read-only layout and reference diagnostics", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult LayoutAnalyze");
    expect(source).toContain("public static CommandExecutionResult ReferencesReport");
    expect(source).toContain("AnalyzeUxmlLayoutPath");
    expect(source).toContain("AnalyzeUssLayoutPath");
    expect(source).toContain("ReportUxmlReferencesPath");
    expect(source).toContain("ReportUssReferencesPath");
    expect(source).toContain("ExtractScreenSizes");
    expect(source).toContain("ReferenceList");
    expect(source).toContain('"UITK_LAYOUT_FIXED_PIXEL"');
    expect(source).toContain('"references_by_kind\\"');
  });

  it("adds feature flags, commands:uitk selector support, and policy visibility", () => {
    const contracts = fs.readFileSync(contractsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    expect(contracts).toContain('"uitk"');
    expect(cli).toContain('"uitk.validate": "uitk_validation"');
    expect(cli).toContain('"uitk.bindings.validate": "uitk_validation"');
    expect(cli).toContain('"uitk.plan": "uitk_migration"');
    expect(cli).toContain('"uitk.generate": "uitk_migration"');
    expect(cli).toContain('"uitk.preview": "uitk_migration_preview"');
    expect(cli).toContain('"uitk.interaction.": "uitk_interaction_tests"');
    expect(cli).toContain('"uitk.": "uitk_inspection"');
    expect(gatewayExample).toContain('"uitk_inspection": false');
    expect(gatewayExample).toContain('"uitk_validation": false');
    expect(gatewayExample).toContain('"uitk_safe_writes": false');
    expect(gatewayExample).toContain('"uitk_migration": false');
    expect(gatewayExample).toContain('"uitk_migration_preview": false');
    expect(gatewayExample).toContain('"uitk_interaction_tests": false');
    expect(policyExample).toContain('"uitk.document.inspect"');
    expect(policyExample).toContain('"uitk.scan"');
    expect(policyExample).toContain('"uitk.plan"');
    expect(policyExample).toContain('"uitk.generate"');
    expect(policyExample).toContain('"uitk.preview"');
    expect(policyExample).toContain('"uitk.validate"');
    expect(policyExample).toContain('"uitk.bindings.validate"');
    expect(policyExample).toContain('"uitk.layout.analyze"');
    expect(policyExample).toContain('"uitk.references.report"');
    expect(policyExample).toContain('"uitk.resource.read"');
    expect(policyExample).toContain('"uitk.document.create"');
    expect(policyExample).toContain('"uitk.binder.generate"');
    expect(policyExample).toContain('"uitk.template.generate"');
    expect(policyExample).toContain('"uitk.interaction.smoke"');
  });

  it("keeps validation fixtures with good and broken UXML, USS, and binder examples", () => {
    const fixtureRoot = path.join(process.cwd(), "tests", "fixtures", "uitk-validation", "Assets");
    const fixtureFiles = [
      "UI/Good.uxml",
      "UI/Broken.uxml",
      "UI/Good.uss",
      "UI/Broken.uss",
      "Scripts/UI/GoodBinder.cs",
      "Scripts/UI/BrokenBinder.cs",
    ];

    for (const fixtureFile of fixtureFiles) {
      expect(fs.existsSync(path.join(fixtureRoot, fixtureFile))).toBe(true);
    }

    const goodUxml = fs.readFileSync(path.join(fixtureRoot, "UI", "Good.uxml"), "utf8");
    const brokenUxml = fs.readFileSync(path.join(fixtureRoot, "UI", "Broken.uxml"), "utf8");
    const brokenBinder = fs.readFileSync(path.join(fixtureRoot, "Scripts", "UI", "BrokenBinder.cs"), "utf8");

    expect(goodUxml).toContain('name="StartButton"');
    expect(brokenUxml).toContain('name="Duplicate"');
    expect(brokenBinder).toContain('root.Q<Button>("MissingButton")');
  });

  it("implements UITK-3 isolated safe artifact creation", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult DocumentCreate");
    expect(source).toContain("public static CommandExecutionResult BinderGenerate");
    expect(source).toContain("public static CommandExecutionResult TemplateGenerate");
    expect(source).toContain("SafeWritesFeatureFlag");
    expect(source).toContain("RequireOutputRoot");
    expect(source).toContain("RequireOutputAssetPath");
    expect(source).toContain("PathGuard.RequireUnityAssetPath");
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(source).toContain('"files_to_create\\"');
    expect(source).toContain("GenerateBinderCsharp");
    expect(source).toContain("ExtractNamedElementsForBinder");
    expect(source).toContain("PrefabUtility.SaveAsPrefabAsset");
    expect(source).toContain("ScriptableObject.CreateInstance<PanelSettings>");
    expect(source).toContain("AssetDatabase.ImportAsset");
    expect(source).toContain("File.WriteAllText");
    expect(source).toContain("Directory.CreateDirectory");
    expect(source).not.toContain("AssetDatabase.SaveAssets");
  });

  it("does not add UITK modify, interaction, apply, rollback, network, process, or destructive file behavior", () => {
    const source = fs.readFileSync(uitkCommandsPath, "utf8");

    expect(source).not.toContain('"uitk.interaction.smoke"');
    expect(source).not.toContain('"uitk.document.modify"');
    expect(source).not.toContain('"uitk.apply"');
    expect(source).not.toContain('"uitk.rollback"');
    expect(source).not.toContain('Risk = "execute"');
    expect(source).not.toContain("AssetDatabase.SaveAssets");
    expect(source).not.toContain("AssetDatabase.DeleteAsset");
    expect(source).not.toContain("File.Delete");
    expect(source).not.toContain("File.Move");
    expect(source).not.toContain("Directory.Delete");
    expect(source).not.toContain("Directory.Move");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
    expect(source).not.toContain("Process.Start");
    expect(source).not.toContain("Undo.");
  });
});
