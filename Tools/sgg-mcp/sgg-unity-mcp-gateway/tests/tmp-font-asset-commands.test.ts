import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "TmpFontAssetCommands.cs");
const metaPath = `${commandPath}.meta`;
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");

describe("TMP font asset command source-contract", () => {
  it("declares TMP font asset inventory commands as read-only asset commands", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const meta = fs.readFileSync(metaPath, "utf8");

    expect(source).toContain('"tmp.font_asset.list"');
    expect(source).toContain('"tmp.font_asset.inspect"');
    expect(source).toContain('"tmp.font_asset.unicode_set.extract"');
    expect(source).toContain('"tmp.unicode_set.collect"');
    expect(source).toContain('"tmp.unicode_set.normalize"');
    expect(source).toContain('"tmp.font_asset.coverage.check"');
    expect(source).toContain('"tmp.font_asset.static.plan"');
    expect(source).toContain('"tmp.font_asset.report"');
    expect(source).toContain('"tmp.font_asset.validate"');
    expect(source).toContain('"tmp.font_asset.resource.read"');
    expect(source).toContain('Title = "List TMP font assets"');
    expect(source).toContain('Title = "Inspect TMP font asset"');
    expect(source).toContain('Title = "Extract TMP font asset Unicode set"');
    expect(source).toContain('Title = "Collect TMP Unicode set"');
    expect(source).toContain('Title = "Normalize TMP Unicode set"');
    expect(source).toContain('Title = "Check TMP font asset Unicode coverage"');
    expect(source).toContain('Title = "Plan static TMP font asset"');
    expect(source).toContain('Title = "TMP font asset report"');
    expect(source).toContain('Title = "Validate TMP font asset"');
    expect(source).toContain('Title = "Read TMP font asset resource"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('const string FeatureFlag = "asset_readonly"');
    expect(source).toContain('Capability = FeatureFlag');
    expect(source).toContain('FeatureFlag = FeatureFlag');
    expect(source).toContain('ResultShape = "bounded_list"');
    expect(source).toContain('ResultShape = "object_details"');
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("finds TMP font assets without a compile-time TextMeshPro dependency", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('AssetDatabase.FindAssets("t:TMP_FontAsset", searchRoots)');
    expect(source).toContain('AssetDatabase.LoadMainAssetAtPath(assetPath)');
    expect(source).toContain('new SerializedObject(asset)');
    expect(source).toContain('IsTypeNamed(asset.GetType(), "TMPro.TMP_FontAsset")');
    expect(source).not.toContain("using TMPro");
    expect(source).not.toContain("TMPro.TMP_FontAsset ");
    expect(source).not.toContain("com.unity.textmeshpro");
  });

  it("reports bounded TMP serialized state needed for static SDF planning", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("m_AtlasPopulationMode");
    expect(source).toContain("m_SourceFontFileGUID");
    expect(source).toContain("m_SourceFontFile_EditorRef");
    expect(source).toContain("m_AtlasTextures");
    expect(source).toContain("m_Material");
    expect(source).toContain("m_FallbackFontAssetTable");
    expect(source).toContain("m_GlyphTable");
    expect(source).toContain("m_CharacterTable");
    expect(source).toContain("AtlasPopulationModeJson");
    expect(source).toContain("SourceFontJson");
    expect(source).toContain("CharacterTableJson");
    expect(source).toContain("GlyphTableJson");
    expect(source).toContain("ExtractCharacterCodepoints");
    expect(source).toContain("CollectUnicodeFromContent");
    expect(source).toContain("CollectUnicodeFromYamlTextFields");
    expect(source).toContain("AddUnicodeCodepointTokens");
    expect(source).toContain("AddUnicodeRangeTokens");
    expect(source).toContain("TryParseUnicodeCodepointToken");
    expect(source).toContain("TryParseUnicodeRangeToken");
    expect(source).toContain("FallbackCoverageAssetsJson");
    expect(source).toContain("ResolveStaticPlanTargetPath");
    expect(source).toContain("SourceFontAvailable");
    expect(source).toContain("CodepointLabelsJson");
    expect(source).toContain("UnicodeRangesJson");
    expect(source).toContain("UnicodeString");
    expect(source).toContain("CodepointLabel");
    expect(source).toContain("IsValidUnicodeScalar");
    expect(source).toContain("IsCollectibleUnicodeScalar");
    expect(source).toContain("MaxTableItems = 500");
    expect(source).toContain("MaxUnicodeCollectBytes = 1048576");
    expect(source).toContain("MaxUnicodeInputItems = 200000");
    expect(source).toContain("MaxUnicodeCodepoints = 20000");
    expect(source).toContain("MaxUnicodeRanges = 5000");
    expect(source).toContain("MaxScanAssets = 100000");
  });

  it("extracts deterministic sorted Unicode sets from TMP character tables", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const commandSource = source.slice(source.indexOf('"tmp.font_asset.unicode_set.extract"'), source.indexOf("static CommandExecutionResult RequireTmpFontAssetPath"));

    expect(source).toContain('const string TmpFontAssetUnicodeSetExtractSchemaJson =');
    expect(source).toContain('const string TmpFontAssetUnicodeSetExtractOutputLimitJson = "{\\"max_scan_items\\":200000,\\"max_codepoints\\":20000,\\"max_ranges\\":5000,\\"max_string_chars\\":65536,\\"readonly\\":true}"');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_codepoints", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_ranges", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_string", false)');
    expect(commandSource).toContain('ExtractCharacterCodepoints(serializedObject, maxScanItems');
    expect(commandSource).toContain('sorted_unique_unicode_codepoints');
    expect(commandSource).toContain('Unicode set is extracted from the serialized TMP character table');
  });

  it("collects deterministic Unicode sets from UI and localization text assets", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const commandSource = source.slice(source.indexOf('"tmp.unicode_set.collect"'), source.indexOf("static CommandExecutionResult RequireTmpFontAssetPath"));

    expect(source).toContain('const string TmpUnicodeSetCollectSchemaJson =');
    expect(source).toContain('const string TmpUnicodeSetCollectOutputLimitJson = "{\\"max_scan_assets\\":100000,\\"max_scan_bytes\\":1048576,\\"max_sources\\":500,\\"max_codepoints\\":20000,\\"max_ranges\\":5000,\\"max_string_chars\\":65536,\\"readonly\\":true}"');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_scenes", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_prefabs", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_text_assets", true)');
    expect(commandSource).toContain("CandidateUnicodeSourcePaths");
    expect(commandSource).toContain("ReadBoundedBytes(fullPath, maxScanBytes");
    expect(commandSource).toContain("CollectUnicodeFromContent(assetPath, content, sourceCodepoints)");
    expect(source).toContain("m_text");
    expect(source).toContain("m_Localized");
    expect(commandSource).toContain("sorted_unique_unicode_codepoints");
    expect(commandSource).toContain("Unity YAML sources are scanned for known UI/localization text fields");
  });

  it("normalizes explicit Unicode set text without touching assets", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const commandSource = source.slice(source.indexOf('"tmp.unicode_set.normalize"'), source.indexOf("static CommandExecutionResult RequireTmpFontAssetPath"));

    expect(source).toContain('const string TmpUnicodeSetNormalizeSchemaJson =');
    expect(source).toContain('const string TmpUnicodeSetNormalizeOutputLimitJson = "{\\"max_input_items\\":200000,\\"max_codepoints\\":20000,\\"max_ranges\\":5000,\\"max_string_chars\\":65536,\\"readonly\\":true}"');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "unicode_text")');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "codepoints_text")');
    expect(commandSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "ranges_text")');
    expect(commandSource).toContain("AddCollectibleCodepoints(unicodeText, codepointSet, maxInputItems");
    expect(commandSource).toContain("AddUnicodeCodepointTokens(codepointsText, codepointSet, maxInputItems");
    expect(commandSource).toContain("AddUnicodeRangeTokens(rangesText, codepointSet, maxInputItems");
    expect(commandSource).toContain("parsed_token_count");
    expect(commandSource).toContain("invalid_token_count");
    expect(commandSource).toContain("Normalization is lexical and read-only");
  });

  it("checks requested Unicode coverage against TMP character tables", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const commandSource = source.slice(source.indexOf('"tmp.font_asset.coverage.check"'), source.indexOf("static CommandExecutionResult RequireTmpFontAssetPath"));

    expect(source).toContain('const string TmpFontAssetCoverageCheckSchemaJson =');
    expect(source).toContain('const string TmpFontAssetCoverageCheckOutputLimitJson = "{\\"max_input_items\\":200000,\\"max_font_scan_items\\":200000,\\"max_fallback_assets\\":100,\\"max_codepoints\\":20000,\\"max_ranges\\":5000,\\"readonly\\":true}"');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_fallbacks", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_missing_codepoints", true)');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_covered_codepoints", false)');
    expect(commandSource).toContain("ExtractCharacterCodepoints(serializedObject, maxFontScanItems");
    expect(commandSource).toContain("FallbackCoverageAssetsJson(serializedObject, maxFallbackAssets");
    expect(commandSource).toContain("coverage_percent");
    expect(commandSource).toContain("missing_codepoints");
    expect(commandSource).toContain("Coverage uses serialized TMP character tables only");
  });

  it("plans static TMP font asset generation as dry-run only", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const commandSource = source.slice(source.indexOf('"tmp.font_asset.static.plan"'), source.indexOf("static CommandExecutionResult RequireTmpFontAssetPath"));

    expect(source).toContain('const string TmpFontAssetStaticPlanSchemaJson =');
    expect(source).toContain('const string TmpFontAssetStaticPlanOutputLimitJson = "{\\"max_input_items\\":200000,\\"max_font_scan_items\\":200000,\\"max_fallback_assets\\":100,\\"max_codepoints\\":20000,\\"max_ranges\\":5000,\\"readonly\\":true,\\"dry_run\\":true}"');
    expect(commandSource).toContain("dry_run");
    expect(commandSource).toContain("ResolveStaticPlanTargetPath(context.ArgsJson, assetPath, asset.name");
    expect(commandSource).toContain("target_asset_path");
    expect(commandSource).toContain("planned_atlas_population_mode");
    expect(commandSource).toContain("ready_for_later_static_create");
    expect(commandSource).toContain("tmp.font_asset.static.create");
    expect(commandSource).toContain("This is a dry-run plan only");
  });

  it("creates bounded read-only TMP font asset reports", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const commandSource = source.slice(source.indexOf('"tmp.font_asset.report"'), source.indexOf("static CommandExecutionResult RequireTmpFontAssetPath"));

    expect(source).toContain('const string TmpFontAssetReportSchemaJson =');
    expect(source).toContain('const string TmpFontAssetReportOutputLimitJson = "{\\"max_results\\":100,\\"max_scan_assets\\":100000,\\"max_input_items\\":200000,\\"max_font_scan_items\\":200000,\\"max_fallback_assets\\":100,\\"max_codepoints\\":20000,\\"max_ranges\\":5000,\\"max_chars\\":1000000,\\"readonly\\":true}"');
    expect(commandSource).toContain('ResultShape = "text_artifact"');
    expect(commandSource).toContain('"markdown_and_json"');
    expect(commandSource).toContain('BuildTmpFontAssetReportMarkdown');
    expect(commandSource).toContain('TmpFontAssetReportCoverageJson');
    expect(commandSource).toContain('TmpFontAssetReportStaticPlanJson');
    expect(commandSource).toContain('content_type');
    expect(commandSource).toContain('artifact_refs');
    expect(commandSource).toContain('This report is generated from AssetDatabase and SerializedObject reads only');
  });

  it("validates TMP font assets and reads emitted TMP resources without writes", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const commandSource = source.slice(source.indexOf('"tmp.font_asset.validate"'), source.indexOf("static CommandExecutionResult RequireTmpFontAssetPath"));

    expect(source).toContain('const string TmpFontAssetValidateSchemaJson =');
    expect(source).toContain('const string TmpFontAssetResourceReadSchemaJson =');
    expect(source).toContain('const string TmpFontAssetValidateOutputLimitJson = "{\\"max_input_items\\":200000,\\"max_font_scan_items\\":200000,\\"max_fallback_assets\\":100,\\"max_codepoints\\":20000,\\"max_ranges\\":5000,\\"max_findings\\":500,\\"readonly\\":true}"');
    expect(source).toContain('const string TmpFontAssetResourceReadOutputLimitJson = "{\\"max_chars\\":1000000,\\"readonly\\":true}"');
    expect(commandSource).toContain('require_static');
    expect(commandSource).toContain('require_source_font');
    expect(commandSource).toContain('require_material');
    expect(commandSource).toContain('require_atlas_texture');
    expect(commandSource).toContain('AddValidationFinding');
    expect(commandSource).toContain('TMP011');
    expect(commandSource).toContain('StoreResource(validationResourceUri, response, "application/json")');
    expect(commandSource).toContain('unity-ui://tmp/font-assets/');
    expect(commandSource).toContain('tmp.font_asset.resource.read');
  });

  it("does not mutate, import, refresh, bake, or generate TMP assets", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.Refresh");
    expect(source).not.toContain("AssetDatabase.CreateAsset");
    expect(source).not.toContain("AssetDatabase.MoveAsset");
    expect(source).not.toContain("AssetDatabase.DeleteAsset");
    expect(source).not.toContain("File.Write");
    expect(source).not.toContain("Directory.CreateDirectory");
    expect(source).not.toContain("SaveAssets");
    expect(source).not.toContain("FontAssetCreationSettings");
    expect(source).not.toContain("CreateFontAsset");
    expect(source).not.toContain("TryAddCharacters");
  });

  it("documents and allows TMP font asset inspection", () => {
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    for (const text of [docs, docsRu, profiles, policyExample]) {
      expect(text).toContain("tmp.font_asset.list");
      expect(text).toContain("tmp.font_asset.inspect");
      expect(text).toContain("tmp.font_asset.unicode_set.extract");
      expect(text).toContain("tmp.unicode_set.collect");
      expect(text).toContain("tmp.unicode_set.normalize");
      expect(text).toContain("tmp.font_asset.coverage.check");
      expect(text).toContain("tmp.font_asset.static.plan");
      expect(text).toContain("tmp.font_asset.report");
      expect(text).toContain("tmp.font_asset.validate");
      expect(text).toContain("tmp.font_asset.resource.read");
    }

    expect(docs).toContain("TMP_FontAsset");
    expect(docs).toContain("dynamic/static atlas population mode");
    expect(docsRu).toContain("TMP_FontAsset");
    expect(docsRu).toContain("dynamic/static atlas population mode");
  });
});
