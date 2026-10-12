import { spawn, type ChildProcess } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, expect, it } from "vitest";

import { CanonicalPathService } from "../src/paths/canonical-path.js";
import { ProjectArtifactReadError, readBoundTestRunArtifact, readBoundTestRunArtifactCleanupSnapshot, readBoundTestRunArtifactSnapshot } from "../src/security/project-artifact-reader.js";
import { TEST_RUN_ARTIFACT_CAPABILITY, TEST_RUN_ARTIFACT_ROOT, testRunCanonicalProjectRootHash, type TestRunOperationBinding } from "../src/operations/test-run-operation-store.js";

const roots: string[] = [];
const children: ChildProcess[] = [];
const windowsIt = process.platform === "win32" ? it : it.skip;
const windowsNode22It = process.platform === "win32" && Number(process.versions.node.split(".")[0]) === 22 ? it : it.skip;
const sleep = new Int32Array(new SharedArrayBuffer(4));

afterEach(() => {
  for (const child of children.splice(0)) if (child.exitCode === null) child.kill();
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

windowsIt("accepts one stable artifact across Node 22 Windows path/descriptor device ids", () => {
  const originProjectRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-project-artifact-win-"));
  roots.push(originProjectRoot);
  const canonicalProjectRoot = new CanonicalPathService().normalizeProjectRoot(originProjectRoot);
  const operationId = "test-20260830-120000-deadbeef";
  const callerArgsHash = `sha256:${"a".repeat(64)}`;
  const artifactPath = `${TEST_RUN_ARTIFACT_ROOT}/${operationId}.json`;
  const binding: TestRunOperationBinding = {
    schema_version: 1,
    contract: "sgg.unity_mcp.test_run_operation_owner.v1",
    command: "test.run",
    operation_id: operationId,
    project: "game",
    original_project_selector: "game",
    origin_project_root: originProjectRoot,
    canonical_project_root: canonicalProjectRoot,
    canonical_project_root_hash: testRunCanonicalProjectRootHash(canonicalProjectRoot),
    origin_instance_id: "game-owner",
    origin_unity_pid: 12345,
    origin_unity_process_start_utc: "2026-08-30T11:00:00.000Z",
    caller_args_hash: callerArgsHash,
    artifact_path: artifactPath,
    artifact_schema_version: 3,
    package_id: "com.sungeargames.unity-mcp",
    package_version: "2026.8.30-1",
    commands_hash: "catalog-v3",
    catalog_generation: 1,
    artifact_schema_capability: TEST_RUN_ARTIFACT_CAPABILITY,
    created_at: "2026-08-30T12:00:00.000Z",
  };
  const file = path.join(originProjectRoot, artifactPath);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({
    command: "test.run",
    artifact_schema_version: 3,
    package_version: "2026.8.30-1",
    operation_id: operationId,
    run_id: operationId,
    artifact_path: artifactPath,
    args_hash: `sha256:${"b".repeat(64)}`,
    caller_args_hash: callerArgsHash,
    status: "completed",
    success: true,
  }));

  expect(readBoundTestRunArtifact(binding, operationId)).toMatchObject({
    operation_id: operationId,
    status: "completed",
    artifact_path_advanced_during_read: false,
  });
  const snapshot = readBoundTestRunArtifactSnapshot(binding, operationId);
  expect(snapshot.data).toEqual(readBoundTestRunArtifact(binding, operationId));
  expect(snapshot.openedMtimeNs).toBe(fs.statSync(file, { bigint: true }).mtimeNs);
  expect(snapshot.data).not.toHaveProperty("openedMtimeNs");
  const cleanup = readBoundTestRunArtifactCleanupSnapshot(binding, operationId);
  expect(cleanup.cleanupEvidence).toMatchObject({ path: file, ino: fs.statSync(file, { bigint: true }).ino.toString(), size: fs.statSync(file, { bigint: true }).size.toString() });
  expect(cleanup.cleanupWitness).toMatch(/^sha256:[0-9a-f]{64}$/);
  fs.linkSync(file, path.join(originProjectRoot, "linked-artifact.json"));
  expect(() => readBoundTestRunArtifactCleanupSnapshot(binding, operationId)).toThrow(ProjectArtifactReadError);
  // Status observation semantics remain unchanged by the stricter admin evidence route.
  expect(readBoundTestRunArtifact(binding, operationId).status).toBe("completed");
});

windowsNode22It("lets a bounded C# MoveFileEx publisher converge after the secure reader releases its descriptor", async () => {
  const originProjectRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-project-artifact-publish-win-"));
  roots.push(originProjectRoot);
  const operationId = "test-20260830-120001-feedface";
  const callerArgsHash = `sha256:${"c".repeat(64)}`;
  const artifactPath = `${TEST_RUN_ARTIFACT_ROOT}/${operationId}.json`;
  const file = path.join(originProjectRoot, artifactPath);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  const artifact = (generation: number) => JSON.stringify({
    command: "test.run",
    artifact_schema_version: 3,
    package_version: "2026.8.30-1",
    operation_id: operationId,
    run_id: operationId,
    artifact_path: artifactPath,
    args_hash: `sha256:${"d".repeat(64)}`,
    caller_args_hash: callerArgsHash,
    status: generation === 4 ? "completed" : "running",
    lifecycle: `generation_${generation}`,
    success: generation === 4,
  });
  fs.writeFileSync(file, artifact(0));
  const tempPrefix = `${file}.native-fixture-`;
  for (let generation = 1; generation <= 4; generation++) fs.writeFileSync(`${tempPrefix}${generation}`, artifact(generation));
  const marker = path.join(originProjectRoot, "first-move-error.txt");
  const binding: TestRunOperationBinding = {
    schema_version: 1,
    contract: "sgg.unity_mcp.test_run_operation_owner.v1",
    command: "test.run",
    operation_id: operationId,
    project: "game",
    original_project_selector: "game",
    origin_project_root: originProjectRoot,
    canonical_project_root: new CanonicalPathService().normalizeProjectRoot(originProjectRoot),
    canonical_project_root_hash: "",
    origin_instance_id: "game-owner",
    origin_unity_pid: 12345,
    origin_unity_process_start_utc: "2026-08-30T11:00:00.000Z",
    caller_args_hash: callerArgsHash,
    artifact_path: artifactPath,
    artifact_schema_version: 3,
    package_id: "com.sungeargames.unity-mcp",
    package_version: "2026.8.30-1",
    commands_hash: "catalog-v3",
    catalog_generation: 1,
    artifact_schema_capability: TEST_RUN_ARTIFACT_CAPABILITY,
    created_at: "2026-08-30T12:00:00.000Z",
  };
  binding.canonical_project_root_hash = testRunCanonicalProjectRootHash(binding.canonical_project_root);

  let writer: ChildProcess | undefined;
  try {
    readBoundTestRunArtifact(binding, operationId, {
      afterRead: () => {
        writer = startMoveFileExPublisher(file, tempPrefix, marker);
        const deadline = Date.now() + 5_000;
        while (!fs.existsSync(marker) && Date.now() < deadline) Atomics.wait(sleep, 0, 0, 5);
        if (!fs.existsSync(marker)) throw new Error("C# MoveFileEx fixture did not report its descriptor-held attempt");
      },
    });
  } catch (error) {
    expect(error).toBeInstanceOf(ProjectArtifactReadError);
    expect((error as ProjectArtifactReadError).reason).toBe("artifact_changed");
  }
  expect(writer).toBeDefined();
  await childExit(writer!);
  children.splice(children.indexOf(writer!), 1);
  expect([5, 32, 33, 1175]).toContain(Number(fs.readFileSync(marker, "utf8")));
  expect(readBoundTestRunArtifact(binding, operationId)).toMatchObject({ lifecycle: "generation_4", status: "completed", success: true });
}, 30_000);

function startMoveFileExPublisher(file: string, tempPrefix: string, marker: string): ChildProcess {
  const script = String.raw`
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SggMoveFileFixture {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);
}
'@
$transient = @(5, 32, 33, 1175)
for ($generation = 1; $generation -le 4; $generation++) {
  $temp = "$($env:SGG_TEMP_PREFIX)$generation"
  $attempt = 1
  $moved = [SggMoveFileFixture]::MoveFileEx($temp, $env:SGG_FINAL_PATH, 9)
  $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
  if ($generation -eq 1) {
    [IO.File]::WriteAllText($env:SGG_MARKER_PATH, [string]$errorCode)
    if ($moved) { exit 41 }
  }
  while (-not $moved -and $attempt -lt 8 -and $transient -contains $errorCode) {
    Start-Sleep -Milliseconds 25
    $attempt++
    $moved = [SggMoveFileFixture]::MoveFileEx($temp, $env:SGG_FINAL_PATH, 9)
    $errorCode = [Runtime.InteropServices.Marshal]::GetLastWin32Error()
  }
  if (-not $moved) { [Console]::Error.WriteLine("MoveFileEx failed: $errorCode"); exit 42 }
}
`;
  const child = spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], {
    env: { ...process.env, SGG_FINAL_PATH: file, SGG_TEMP_PREFIX: tempPrefix, SGG_MARKER_PATH: marker },
    stdio: ["ignore", "pipe", "pipe"],
  });
  children.push(child);
  return child;
}

function childExit(child: ChildProcess): Promise<void> {
  return new Promise((resolve, reject) => {
    let stderr = "";
    child.stderr?.on("data", (data) => { stderr += String(data); });
    child.once("error", reject);
    child.once("exit", (code) => code === 0 ? resolve() : reject(new Error(stderr || `C# MoveFileEx fixture exited ${code}`)));
  });
}
