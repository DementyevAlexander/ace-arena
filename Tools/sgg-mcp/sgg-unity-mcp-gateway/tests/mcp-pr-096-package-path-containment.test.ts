import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const monitorPath = path.join(
  process.cwd(),
  "unity",
  "Packages",
  "com.sungeargames.unity-mcp",
  "Editor",
  "Bridge",
  "PackageUpdateMonitor.cs",
);

describe("MCP-PR-096 project package path containment", () => {
  it("uses one root-aware canonical pipeline for production metadata fingerprinting", () => {
    const source = fs.readFileSync(monitorPath, "utf8");
    const appendStart = source.indexOf("static bool TryAppendProjectPackageFile");
    const appendEnd = source.indexOf("internal static bool TryBuildProjectPackageFileFingerprint", appendStart);
    const appendBody = source.slice(appendStart, appendEnd);
    const rootAwareStart = source.indexOf("internal static bool TryBuildProjectPackageFileFingerprintWithinRoot", appendEnd);
    const rootAwareEnd = source.indexOf("static bool TryBuildProjectPackageFileFingerprint(string fullPath", rootAwareStart);
    const rootAwareBody = source.slice(rootAwareStart, rootAwareEnd);

    expect(appendBody).toContain("TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot");
    expect(rootAwareBody).toContain("canonicalRoot = NormalizeNativePath(trustedRoot)");
    expect(rootAwareBody).toContain("Path.Combine(canonicalRoot");
    expect(rootAwareBody).toContain("IsSameOrChildPath(fullPath, canonicalRoot)");
    expect(rootAwareBody).toContain("HasReparsePointBelowRoot(fullPath, canonicalRoot)");
    expect(rootAwareBody).toContain("TryBuildProjectPackageFileFingerprint(fullPath, relativePath, canonicalRoot");
  });

  it("keeps separator-bound containment and platform-aware comparison fail-closed", () => {
    const source = fs.readFileSync(monitorPath, "utf8");
    const containmentStart = source.indexOf("internal static bool IsSameOrChildPath");
    const containmentEnd = source.indexOf("void ApplyCurrentSnapshot", containmentStart);
    const containmentBody = source.slice(containmentStart, containmentEnd);

    expect(containmentBody).toContain("NormalizeNativePath(path)");
    expect(containmentBody).toContain("NormalizeNativePath(root)");
    expect(containmentBody).toContain("canonicalRoot + Path.DirectorySeparatorChar");
    expect(containmentBody).toContain("Path.DirectorySeparatorChar == '\\\\'");
    expect(containmentBody).toContain("StringComparison.OrdinalIgnoreCase");
    expect(containmentBody).toContain("StringComparison.Ordinal");
    expect(containmentBody).toContain("catch");
    expect(containmentBody).toContain("return false");
  });
});
