import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

type PathCorpus = {
  rejected_lexical_identity: Array<{ name: string; input: string; npm_error: string; gateway_error: string }>;
  rejected_unity_asset_paths: Array<{ name: string; input: string; error: string }>;
  filesystem_bound_paths: Array<{ name: string; layer: string; input: string; inside: boolean; error?: string }>;
};

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");
const corpus = JSON.parse(fs.readFileSync(path.join(root, "testdata", "path-corpus.json"), "utf8")) as PathCorpus;
const providerCorpusPath = path.join(packageRoot, "Tests", "CanonicalPath", "CanonicalPathCorpus.json");
const providerCorpus = JSON.parse(fs.readFileSync(providerCorpusPath, "utf8")) as {
  vectors: Array<{ name: string; input: string; canonical: string }>;
};

describe("path corpus source and data contracts", () => {
  it("embeds provider-supported vectors for the conditional Unity EditMode fixture", () => {
    const names = providerCorpus.vectors.map((vector) => vector.name);
    const rejectedNames = corpus.rejected_lexical_identity.map((vector) => vector.name);
    const providerSource = fs.readFileSync(path.join(packageRoot, "Editor", "CanonicalPath", "CanonicalPathBridgeRegistration.cs"), "utf8");
    const serviceSource = fs.readFileSync(path.join(packageRoot, "Editor", "Paths", "CanonicalPathService.cs"), "utf8");
    const fixtureSource = fs.readFileSync(path.join(packageRoot, "Tests", "CanonicalPath", "CanonicalPathProviderCorpusTests.cs"), "utf8");

    expect(names).toEqual(expect.arrayContaining([
      "windows-backslashes",
      "wsl-default-mount",
      "mixed-repeated-separators",
      "dot-segments",
      "unc-cleaned",
      "drive-only-case-folded",
      "unicode-nfc-preserved",
      "unicode-nfd-preserved",
    ]));
    expect(names).toContain("custom-mount-not-shared");
    expect(rejectedNames).toEqual(expect.arrayContaining(["empty", "nul", "drive-relative", "invalid-unc"]));
    expect(providerSource).toContain("s_bridge.Normalize(MapDefaultWslDrive(input)).ToString()");
    expect(serviceSource).toContain("CanonicalPathBridge.TryNormalize(input, out normalized)");
    expect(fixtureSource).toContain("AssetDatabase.LoadAssetAtPath<TextAsset>(CorpusPath)");
    expect(fixtureSource).toContain("CanonicalPathProviderAdapter.Normalize(vector.input)");
  });

  it("covers Phase L path categories with public generic examples", () => {
    const rejectedInputs = corpus.rejected_unity_asset_paths.map((vector) => vector.input);
    const filesystemInputs = corpus.filesystem_bound_paths.map((vector) => vector.input);

    expect(rejectedInputs).toContain("Assets/../ProjectSettings/ProjectSettings.asset");
    expect(rejectedInputs).toContain("C:/Windows/System32/kernel32.dll");
    expect(rejectedInputs).toContain("/tmp/file.txt");
    expect(rejectedInputs).toContain("/mnt/c/u/game/Assets/A.cs");
    expect(rejectedInputs).toContain("C:Windows/System32/kernel32.dll");
    expect(rejectedInputs).toContain("\\\\server\\share\\file.txt");
    expect(rejectedInputs.some((input) => input.includes("\0"))).toBe(true);
    expect(rejectedInputs).toContain("Assets/CON.txt");
    expect(rejectedInputs.some((input) => input.length > 255)).toBe(true);
    expect(filesystemInputs).toContain("Library/SGGUnityMcp/job-artifacts/../asset-index.tsv");
  });

  it("aligns Unity PathGuard source with rejected Unity payload corpus categories", () => {
    const source = fs.readFileSync(path.join(packageRoot, "Editor", "Paths", "PathGuard.cs"), "utf8");

    expect(source).toContain("AssertNoNul(input)");
    expect(source).toContain("RejectEncodedSeparators(input)");
    expect(source).toContain("IsDriveAbsolute(normalized)");
    expect(source).toContain("HasTraversal(normalized)");
    expect(source).toContain('normalized.StartsWith("Assets/", StringComparison.Ordinal)');
    expect(source).toContain('normalized.StartsWith("Packages/", StringComparison.Ordinal)');
    expect(source).toContain("IsWindowsReservedName(part)");
    expect(source).toContain("part.Length > 255");
  });

  it("aligns filesystem-bound Unity sources with corpus layers", () => {
    const artifactSource = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "JobArtifactCommands.cs"), "utf8");
    const runtimeSource = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "RuntimeDebuggingCommands.cs"), "utf8");
    const packageSource = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "PackageManagementCommands.cs"), "utf8");

    expect(corpus.filesystem_bound_paths.some((vector) => vector.layer === "artifact")).toBe(true);
    expect(artifactSource).toContain("RequireProjectLocalFilePath(relativePath, ArtifactRoot)");
    expect(artifactSource).toContain("FileAttributes.ReparsePoint");

    expect(corpus.filesystem_bound_paths.some((vector) => vector.layer === "event_capture_artifact")).toBe(true);
    expect(runtimeSource).toContain("RequireProjectLocalFilePath(relativePath, EventCaptureArtifactRoot)");
    expect(runtimeSource).toContain("FileAttributes.ReparsePoint");

    expect(corpus.filesystem_bound_paths.some((vector) => vector.layer === "package_install")).toBe(true);
    expect(packageSource).toContain("HasTraversal(normalized)");
    expect(packageSource).toContain('normalized.StartsWith("/", StringComparison.Ordinal) || IsDriveAbsolute(normalized)');
  });
});
