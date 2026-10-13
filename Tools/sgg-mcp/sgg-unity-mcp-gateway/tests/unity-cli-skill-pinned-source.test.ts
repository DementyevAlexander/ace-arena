import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const setup = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp", "Editor", "Setup");
const installer = fs.readFileSync(path.join(setup, "UnityCliProjectSkillInstaller.cs"), "utf8");
const download = fs.readFileSync(path.join(setup, "UnityCliReviewedSkillDownload.cs"), "utf8");

// Source checks only; primary owns actual Unity compilation/behavioral tests.
describe("immutable current official skill staging", () => {
  it("uses only system PowerShell and the exact immutable official raw source in production", () => {
    expect(installer).toContain('ReviewedSourceCommit = "cb1dccb8f5adffcca43a5a26993fdeb8eae59433"');
    expect(installer).toContain('ReviewedSource = "https://raw.githubusercontent.com/Unity-Technologies/skills/" + ReviewedSourceCommit + "/skills/unity-cli/"');
    const production = installer.slice(installer.indexOf("static bool TryStartStaging("), installer.indexOf("internal static bool HasPendingUpdateBackup("));
    expect(production).toContain("UnityCliReviewedSkillDownload.BuildArguments(stagingRoot)");
    expect(production).toContain("CanStageReviewedSkill(out string error)");
    expect(production).not.toContain("NodeRuntimeInstaller");
    expect(production).not.toContain("npmCliPath");
    expect(production).not.toContain("Directory.Delete");
    expect(download).toContain('Path.Combine(system, "WindowsPowerShell", "v1.0", "powershell.exe")');
    expect(download).toContain('new[] { "-NoProfile", "-NonInteractive", "-Command", script }');
    expect(installer).toContain("internal static string[] BuildInstallArguments(string npmCliPath)");
  });

  it("has exactly the fourteen safe official source paths and retains all older generations", () => {
    const whitelist = download.slice(download.indexOf("Array.AsReadOnly(new[]"), download.indexOf("internal static IReadOnlyList<string> RelativePaths"));
    const files = [...whitelist.matchAll(/"([^"]+\.md)"/g)].map(match => match[1]!);
    expect(files).toHaveLength(14);
    expect(new Set(files).size).toBe(14);
    expect(files).toContain("references/cloud-automation.md");
    expect(files).toContain("references/playmode-verification-loop.md");
    expect(files).toContain("references/version-control.md");
    expect(files.every(file => /^[A-Za-z0-9/-]+\.md$/.test(file))).toBe(true);
    for (const generation of ["Previous", "Earlier", "Legacy"]) {
      expect(installer).toContain(`new ReviewedSkillGeneration(${generation}ReviewedSourceCommit, ${generation}ReviewedSkillsHash, ${generation}ReviewedContentHash)`);
    }
  });

  it("fails closed on transport, streaming limits, reparse points and preexisting files", () => {
    for (const guard of ["$request.AllowAutoRedirect=$false", "$request.UseDefaultCredentials=$false", "$request.Proxy=$null", "$response.StatusCode -ne [Net.HttpStatusCode]::OK", "RemainingMilliseconds $fileBudget", "$fileBytes -gt $maxFileBytes -or $totalBytes -gt $maxTotalBytes", "[IO.FileMode]::CreateNew", "[IO.FileAttributes]::ReparsePoint", "$current=$current.Parent", "Staging root must be newly created and empty"]) {
      expect(download).toContain(guard);
    }
    for (const unsafe of ["Invoke-Expression", "Start-Process", "Expand-Archive", "DownloadString", "git clone", "npm exec"]) expect(download).not.toContain(unsafe);
    expect(download.indexOf('script = script.Replace("{{ROOT}}", QuoteLiteral(root))')).toBeGreaterThan(download.indexOf('.Replace("{{LOCK}}"'));
    expect(installer).toContain("Nothing was deleted or reused.");
  });

  it("requires actual full byte hashing before metadata, provenance and copy", () => {
    expect(download).toContain("[Array]::Sort($ordered,[StringComparer]::Ordinal)");
    expect(download).toContain("$fileStream.CopyTo($hashBuffer,4096)");
    expect(download.indexOf("if (-not [string]::Equals($contentHash,$expectedHash,")).toBeLessThan(download.indexOf("$lockStream=[IO.File]::Open"));
    expect(download).toContain("gateway_reviewed_acquisition_metadata");
    const finalization = installer.slice(installer.indexOf("static bool TryPrepareReviewedStagedSkill("));
    expect(finalization.indexOf("var contentHash = ComputeContentHash(stagedSkillRoot)")).toBeLessThan(finalization.indexOf("File.WriteAllText(Path.Combine(stagedSkillRoot, ProvenanceFileName)"));
    expect(finalization).toContain("!string.Equals(contentHash, expectedContentHash, StringComparison.OrdinalIgnoreCase)");
    expect(installer).toContain('ReviewedContentHash = "20cce0a4ca795c0f2d192da90d32c27a27686210da435c3b758487c62055768b"');
    expect(installer).toContain('ReviewedSkillsHash = "3dc226a7f8fdb938126606c6f7ed4c97a1eee541e9f0ae1a38e8d7918f37964e"');
  });

  it("publishes under effective native directory custody without merging or recursively deleting uncertain targets", () => {
    expect(installer).toContain("CreateFile(current, 0x81, 3,");
    expect(installer).toContain("Publication ancestor identity changed while custody was acquired.");
    expect(installer).toContain("custody.HoldChain(Path.GetDirectoryName(source))");
    expect(installer).toContain("custody.MoveDirectory(source, target, sourceIdentity");
    expect(installer).toContain("Marshal.WriteIntPtr(buffer, rootOffset, parent.DangerousGetHandle())");
    expect(installer).toContain("NtSetInformationFile(handle, out _, buffer, (uint)length, 10)");
    expect(installer).toContain("custody.Matches(backupRoot, previousIdentity)");
    expect(installer).toContain("custody.Matches(targetRoot, replacementIdentity)");
    expect(installer).toContain('"unity-cli-skill-failed-" + Guid.NewGuid()');
    expect(installer).not.toContain("Directory.Delete");
    expect(installer).not.toContain("CopyDirectory");
    const regression = fs.readFileSync(path.join(setup, "../../Tests/Editor/UnityCliSkillPinnedSourceTests.cs"), "utf8");
    expect(regression).toContain("PublicationRefusesAnAncestorChangedToJunctionAfterStaging");
    expect(regression).toContain("HeldPublicationParentsCannotBeRenamedDuringFinalMove");
  });
});
