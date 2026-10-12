import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");

describe("MCP-PR-102 project package fingerprint failures", () => {
  it("keeps the stable read fail-closed while preserving typed bounded reasons", () => {
    const source = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");

    for (const reason of [
      "budget_exhausted",
      "replacement_race",
      "unstable_metadata",
      "missing_file",
      "path_security_rejected",
      "size_limit_exceeded",
      "read_exception",
    ]) {
      expect(source).toContain(`failureReason = "${reason}"`);
    }
    expect(source).toContain("HasReparsePointBelowRoot(fullPath, canonicalRoot)");
    expect(source).toContain("MaxProjectPackageFileBytes");
    expect(source).toContain("MaxProjectPackageFingerprintMilliseconds");
    expect(source).toContain("FileShare.ReadWrite | FileShare.Delete");
    expect(source).toContain("afterFirstRead?.Invoke()");
    expect(source).not.toContain("Project package metadata could not be fingerprinted safely.");
  });

  it("publishes only the evidence class, reason, and safe file kind", () => {
    const monitor = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const state = readPackageFile("Editor/Bridge/BridgeRuntimeState.cs");
    const protocol = fs.readFileSync(path.join(process.cwd(), "src", "bridge", "bridge-protocol.ts"), "utf8");

    expect(monitor).toContain('FingerprintSnapshot.Failure("project_packages", failureReason, failureFile, total)');
    expect(monitor).toContain("Project package metadata fingerprint is unavailable");
    expect(monitor).toContain("The background monitor will retry the bounded read-only scan.");
    expect(monitor).not.toContain("could not inspect the package");
    expect(state).toContain('\\"fingerprint_failure\\"');
    expect(state).toContain('BoundedJsonString(PackageFingerprintFailureReason, 64)');
    expect(protocol).toContain('evidence_class: z.enum(["package_runtime", "project_packages"])');
    expect(protocol).toContain('file: z.enum(["manifest", "packages_lock", "unknown"])');
  });

  it("retries through event-driven background scans and never resolves packages automatically", () => {
    const source = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const fingerprintRead = source.slice(source.indexOf("FingerprintSnapshot ComputeFingerprintSafely"), source.indexOf("static bool TryAppendProjectPackageFile"));

    expect(source).toContain("Task.Run(() => ComputeFingerprintSafely(request))");
    expect(source).toContain("FileSystemWatcher");
    expect(source).toContain("FallbackScanIntervalSeconds = 120.0");
    expect(source).toContain("FailureRetrySeconds = 30.0");
    expect(source).toContain("EditorApplication.isPlayingOrWillChangePlaymode");
    expect(source).toContain("LogWarning()");
    expect(source).toContain("m_nextWarningUtc = now.AddSeconds(30)");
    expect(fingerprintRead).not.toContain("BridgeReloadTrace.LogError");
    expect(source).not.toContain("Client.Resolve");
    expect(source).not.toContain("AssetDatabase.Refresh");
    expect(source).not.toContain("ScanIntervalSeconds = 2.0");
  });
});

function readPackageFile(relativePath: string): string {
  return fs.readFileSync(path.join(packageRoot, relativePath), "utf8");
}
