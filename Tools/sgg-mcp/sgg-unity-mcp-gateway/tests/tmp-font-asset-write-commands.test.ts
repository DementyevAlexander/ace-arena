import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "TmpFontAssetWriteCommands.cs");
const metaPath = `${commandPath}.meta`;
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");

describe("TMP font asset write command source-contract", () => {
  it("declares TMP write commands behind asset_safe_writes", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const meta = fs.readFileSync(metaPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "asset_safe_writes"');
    expect(source).toContain('"tmp.font_asset.static.create"');
    expect(source).toContain('"tmp.font_asset.static.bake"');
    expect(source).toContain('"tmp.font_asset.static.convert"');
    expect(source).toContain('"tmp.font_asset.references.swap"');
    expect(source).toContain('"tmp.font_asset.fallbacks.set"');
    expect(source).toContain('Risk = "write"');
    expect(source).toContain('Risk = "destructive"');
    expect(source).toContain('Capability = FeatureFlag');
    expect(source).toContain('FeatureFlag = FeatureFlag');
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("uses dry-run and precondition guards for real writes", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(source).toContain('RequireWritableTmpFontAssetPath(context.ArgsJson, "asset_path"');
    expect(source).toContain('write target must be under Assets/');
    expect(source).toContain("RequirePrecondition(context.ArgsJson, precondition)");
    expect(source).toContain('precondition_hash is required when dry_run is false');
    expect(source).toContain("AssetDatabase.CopyAsset");
    expect(source).toContain("AssetDatabase.SaveAssets");
    expect(source).toContain("ApplyModifiedPropertiesWithoutUndo");
    expect(source).toContain("SetAtlasPopulationModeStatic");
    expect(source).toContain("TryAddCharacters");
  });

  it("avoids compile-time TextMeshPro dependencies", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('IsTypeNamed(asset.GetType(), "TMPro.TMP_FontAsset")');
    expect(source).toContain('GetMethod("TryAddCharacters"');
    expect(source).not.toContain("using TMPro");
    expect(source).not.toContain("TMPro.TMP_FontAsset ");
    expect(source).not.toContain("com.unity.textmeshpro");
  });

  it("supports newer TMP TryAddCharacters overloads and reports missing glyph details", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain('GetMethod("TryAddCharacters", new[] { typeof(string), typeof(string).MakeByRefType(), typeof(bool) })');
    expect(source).toContain("new object[] { characters, string.Empty, false }");
    expect(source).toContain('GetMethod("TryAddCharacters", new[] { typeof(string), typeof(string).MakeByRefType() })');
    expect(source).toContain("MissingCharactersDetailsJson(missingCharacters)");
    expect(source).toContain("missing_count");
    expect(source).toContain("missing_characters_truncated");
    expect(source).toContain("missing_codepoints_truncated");
    expect(source).toContain("missing_codepoints");
    expect(source).toContain("missing_range_count");
    expect(source).toContain("missing_ranges_truncated");
    expect(source).toContain("missing_ranges");
    expect(source).toContain("CodepointLabel");
  });

  it("documents and confirmation-gates TMP write commands", () => {
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    for (const text of [docs, docsRu, profiles, policyExample]) {
      expect(text).toContain("tmp.font_asset.static.create");
      expect(text).toContain("tmp.font_asset.static.bake");
      expect(text).toContain("tmp.font_asset.static.convert");
      expect(text).toContain("tmp.font_asset.references.swap");
      expect(text).toContain("tmp.font_asset.fallbacks.set");
    }

    expect(docs).toContain("precondition_hash");
    expect(docs).toContain("asset_safe_writes");
    expect(docs).toContain("missing_count");
    expect(docsRu).toContain("missing_count");
  });
});
