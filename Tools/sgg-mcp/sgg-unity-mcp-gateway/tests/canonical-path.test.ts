import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

import { normalize as normalizeNpmCanonicalPath } from "@romanilyin/canonicalpath";

import { CanonicalPathService, makeSafeFileName, validateUnityAssetPath } from "../src/paths/canonical-path.js";

type PathVectors = {
  rejected_lexical_identity: Array<{ name: string; input: string; npm_error: string; gateway_error: string }>;
  project_roots: Array<{ input: string; canonical_project_root: string; aliases: Array<{ environment: string; host_path: string; canonical_path: string }> }>;
  unity_asset_paths: Array<{ input: string; normalized: string }>;
  rejected_unity_asset_paths: Array<{ input: string; error: string }>;
  inside_project: Array<{ project_root: string; candidate: string; inside: boolean; error?: string }>;
  filesystem_bound_paths: Array<{ layer: string; input: string; inside: boolean; error?: string }>;
};

const pathVectors = JSON.parse(fs.readFileSync(path.join(process.cwd(), "testdata", "path-corpus.json"), "utf8")) as PathVectors;
const providerCorpus = JSON.parse(fs.readFileSync(path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp", "Tests", "CanonicalPath", "CanonicalPathCorpus.json"), "utf8")) as {
  vectors: Array<{ name: string; input: string; canonical: string }>;
};

describe("CanonicalPathService", () => {
  it("matches the npm CanonicalPath lexical identity corpus", () => {
    const paths = new CanonicalPathService();
    const options = { wsl: { enabled: true, mountRoot: "/mnt" } } as const;

    for (const vector of providerCorpus.vectors) {
      expect(normalizeNpmCanonicalPath(vector.input, options), vector.name).toBe(vector.canonical);
      expect(paths.normalize(vector.input), vector.name).toBe(vector.canonical);
    }

    for (const vector of pathVectors.rejected_lexical_identity) {
      expect(() => normalizeNpmCanonicalPath(vector.input, options), vector.name).toThrow(vector.npm_error);
      expect(() => paths.normalize(vector.input), vector.name).toThrow(vector.gateway_error);
    }
  });

  it("normalizes Windows, WSL and Unity-style paths lexically", () => {
    const paths = new CanonicalPathService();

    expect(paths.normalizeProjectRoot("C:\\u\\game\\")).toBe("c:/u/game");
    expect(paths.normalizeProjectRoot("/mnt/c/u/game")).toBe("c:/u/game");
    expect(validateUnityAssetPath("Assets\\Scripts\\A.cs")).toBe("Assets/Scripts/A.cs");
  });

  it("matches shared project root alias vectors", () => {
    const paths = new CanonicalPathService();

    for (const vector of pathVectors.project_roots) {
      expect(paths.normalizeProjectRoot(vector.input)).toBe(vector.canonical_project_root);
      expect(paths.projectRootAliases(vector.input)).toEqual(vector.aliases);
    }
  });

  it("matches shared Unity asset path vectors", () => {
    for (const vector of pathVectors.unity_asset_paths) {
      expect(validateUnityAssetPath(vector.input)).toBe(vector.normalized);
    }

    for (const vector of pathVectors.rejected_unity_asset_paths) {
      expect(() => validateUnityAssetPath(vector.input)).toThrow(vector.error);
    }
  });

  it("keeps unicode forms as deterministic lexical identity", () => {
    const nfc = validateUnityAssetPath("Assets/Unicode/é.txt");
    const nfd = validateUnityAssetPath("Assets/Unicode/é.txt");

    expect(nfc).not.toBe(nfd);
    expect(nfc.normalize("NFD")).toBe(nfd);
  });

  it("matches shared inside-project vectors", () => {
    const paths = new CanonicalPathService();

    for (const vector of pathVectors.inside_project) {
      if (vector.inside) {
        expect(() => paths.assertInsideProject(vector.project_root, vector.candidate)).not.toThrow();
      } else {
        expect(() => paths.assertInsideProject(vector.project_root, vector.candidate)).toThrow(vector.error);
      }
    }
  });

  it("rejects empty and UNC project roots", () => {
    const paths = new CanonicalPathService();

    expect(() => paths.normalizeProjectRoot("")).toThrow("path_not_allowed");
    expect(() => paths.normalizeProjectRoot("   ")).toThrow("path_not_allowed");
    expect(() => paths.normalizeProjectRoot("\\\\server\\share\\game")).toThrow("absolute_path_rejected");
    expect(() => paths.assertInsideProject("", "c:/u/game/Assets/A.cs")).toThrow("path_not_allowed");
    expect(() => paths.assertInsideProject("c:/u/game", "\\\\server\\share\\game\\Assets\\A.cs")).toThrow(
      "absolute_path_rejected",
    );
    expect(() => paths.assertInsideProject("\\\\server\\share\\game", "//server/share/game/Assets/A.cs")).toThrow(
      "absolute_path_rejected",
    );
  });

  it("rejects absolute Unity payload paths and traversal", () => {
    expect(() => validateUnityAssetPath("C:/Windows/System32/kernel32.dll")).toThrow("absolute_path_rejected");
    expect(() => validateUnityAssetPath("C:Windows/System32/kernel32.dll")).toThrow("absolute_path_rejected");
    expect(() => validateUnityAssetPath("/tmp/file.txt")).toThrow("absolute_path_rejected");
    expect(() => validateUnityAssetPath("\\\\server\\share\\file.txt")).toThrow("absolute_path_rejected");
    expect(() => validateUnityAssetPath("Assets/../ProjectSettings/ProjectSettings.asset")).toThrow("path_traversal_rejected");
    expect(() => validateUnityAssetPath("ProjectSettings/ProjectSettings.asset")).toThrow("path_not_allowed");
  });

  it("rejects unsafe Unity payload components", () => {
    expect(() => validateUnityAssetPath("Assets")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Packages")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("AssetsSibling/A.cs")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/%2e%2e%2fProjectSettings.asset")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/%5cProjectSettings.asset")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/%252fProjectSettings.asset")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/%255cProjectSettings.asset")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/Foo./A.cs")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/Foo /A.cs")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/A.cs:Zone.Identifier")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath("Assets/CON.txt")).toThrow("path_not_allowed");
    expect(() => validateUnityAssetPath(`Assets/${"a".repeat(256)}.txt`)).toThrow("path_not_allowed");
  });

  it("converts Unity asset paths against a project root", () => {
    const paths = new CanonicalPathService();
    const root = paths.normalizeProjectRoot("C:/u/game");
    const fullPath = paths.fromUnityAssetPath(root, "Assets/Scripts/A.cs");

    expect(fullPath).toBe("c:/u/game/Assets/Scripts/A.cs");
    expect(paths.toUnityAssetPath(root, fullPath)).toBe("Assets/Scripts/A.cs");
  });

  it("checks project containment component-wise", () => {
    const paths = new CanonicalPathService();
    const root = paths.normalizeProjectRoot("C:/u/game");

    expect(() => paths.assertInsideProject(root, "c:/u/game/Assets/A.cs")).not.toThrow();
    expect(() => paths.assertInsideProject(root, "/mnt/c/u/game/Assets/A.cs")).not.toThrow();
    expect(() => paths.assertInsideProject(root, "c:/u/game-evil/Assets/A.cs")).toThrow("path_not_allowed");
  });

  it("sanitizes generated filenames with stable hash suffixes when truncated", () => {
    expect(makeSafeFileName("Hello unsafe:/name.asset", 80)).toBe("Hello-unsafe-name.asset");

    const safe = makeSafeFileName("Very long model generated filename with many unsafe characters !@#$%^&*().asset", 32);
    expect(safe).toMatch(/^Very-long-model--[a-f0-9]{8}\.asset$/);
    expect(safe.length).toBeLessThanOrEqual(32);
  });
});
