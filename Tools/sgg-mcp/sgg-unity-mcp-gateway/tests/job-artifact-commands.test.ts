import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const jobArtifactCommandsPath = path.join(packageRoot, "Editor", "Commands", "JobArtifactCommands.cs");

describe("Unity job artifact command source-contract", () => {
  it("declares a bounded project-local artifact read command", () => {
    const source = fs.readFileSync(jobArtifactCommandsPath, "utf8");

    expect(source).toContain('"job.artifact.read"');
    expect(source).toContain('ArtifactRoot = "Library/SGGUnityMcp/job-artifacts/"');
    expect(source).toContain("NormalizeArtifactPath");
    expect(source).toContain("max_chars");
    expect(source).toContain('Risk = "read"');
  });

  it("uses filesystem-bound containment and reparse-point checks for artifact reads", () => {
    const source = fs.readFileSync(jobArtifactCommandsPath, "utf8");

    expect(source).toContain("RequireProjectLocalFilePath(relativePath, ArtifactRoot)");
    expect(source).toContain("Path.GetFullPath(Path.Combine(projectRoot");
    expect(source).toContain("IsSameOrChildPath(fullPath, fullRoot)");
    expect(source).toContain("FileAttributes.ReparsePoint");
    expect(source).toContain('normalized.StartsWith("/", StringComparison.Ordinal)');
    expect(source).toContain("IsDriveAbsolute(normalized)");
    expect(source).not.toContain("TrimStart('/')");
  });
});
