import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");

describe("MCP-PR-090 package state continuity across Play Mode", () => {
  it("fingerprints project package metadata by bounded content rather than timestamp identity", () => {
    const source = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const appendStart = source.indexOf("static bool TryAppendProjectPackageFile");
    const appendEnd = source.indexOf("static bool IsSameOrChildPath", appendStart);
    const projectFingerprintSource = source.slice(appendStart, appendEnd);

    expect(projectFingerprintSource).toContain("TryBuildProjectPackageFileFingerprint");
    expect(projectFingerprintSource).toContain("MaxProjectPackageFileBytes");
    expect(projectFingerprintSource).toContain("SHA256.Create()");
    expect(projectFingerprintSource).toContain("FileShare.ReadWrite | FileShare.Delete");
    expect(projectFingerprintSource).toContain("HasReparsePointBelowRoot");
    expect(projectFingerprintSource).toContain('fingerprint = relativePath + "|file|" + first.Length + "|" + first.Digest');
    expect(projectFingerprintSource).toContain("MaxProjectPackageFingerprintMilliseconds");
    expect(projectFingerprintSource).toContain("TryReadStableProjectPackageFile");
    expect(projectFingerprintSource).not.toContain("Append(info.LastWriteTimeUtc.Ticks)");
    expect(source).toContain('SetStatus(state, "unavailable", FingerprintUnavailableMessage(m_refreshPending), true');
    expect(source).toContain("m_pendingFingerprint, false, true);");
  });

  it("does not mutate package resolution from Play Mode callbacks or heartbeat publication", () => {
    const monitorSource = readPackageFile("Editor/Bridge/PackageUpdateMonitor.cs");
    const trackerSource = readPackageFile("Editor/Bridge/PlayModeTransitionTracker.cs");
    const editorStatusSource = readPackageFile("Editor/Commands/EditorStatusCommands.cs");
    const heartbeatSource = readPackageFile("Editor/Bridge/HeartbeatWriter.cs");
    const runtimeSource = readPackageFile("Editor/Bridge/UnityBridgeRuntime.cs");

    expect(monitorSource).not.toContain("Client.Resolve");
    expect(monitorSource).not.toContain("AssetDatabase.Refresh");
    expect(trackerSource).not.toContain("PackageUpdateMonitor");
    expect(editorStatusSource).not.toContain("MarkPackageRefreshObserved");
    expect(heartbeatSource).not.toContain("package_update");
    expect(runtimeSource.indexOf("PackageUpdateMonitor?.Update(State, EditorApplication.timeSinceStartup)")).toBeLessThan(runtimeSource.indexOf("HeartbeatWriter.WriteIfDue(EditorApplication.timeSinceStartup, HeartbeatIntervalSeconds)"));
    expect(monitorSource).toContain("EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying");
    expect(monitorSource).toContain("Task.Run(() => ComputeFingerprintSafely(request))");
    expect(monitorSource).not.toContain("ScanIntervalSeconds = 2.0");
  });
});

function readPackageFile(relativePath: string): string {
  return fs.readFileSync(path.join(packageRoot, relativePath), "utf8");
}
