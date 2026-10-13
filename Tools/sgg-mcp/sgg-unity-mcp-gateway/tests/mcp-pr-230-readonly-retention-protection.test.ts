import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { ARTIFACT_COMMANDS, runArtifactCommand } from "../src/mcp/artifact-commands.js";
import type { HeartbeatRecord } from "../src/registry/project-registry.js";

const ROOT = "Library/SGGUnityMcp/job-artifacts";
const PROTECTED = `${ROOT}/dynamic-csharp-readonly`;
const now = new Date("2026-10-08T00:00:00.000Z");
const old = new Date("2025-01-01T00:00:00.000Z");
const tempDirs: string[] = [];

afterEach(() => {
  vi.restoreAllMocks();
  for (const root of tempDirs.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe("MCP-PR-230 read-only receipt retention protection", () => {
  it.each([
    ["active", '{"status":"running","terminal":false}'],
    ["unknown", '{"status":"unknown"}'],
    ["malformed", "not-json"],
    ["recoverable", '{"status":"completed","result_available":true}'],
    ["response-written", '{"terminal":true,"bridge_response_write":{"state":"completed"}}'],
  ])("does not select or delete aged %s entries by default or job-family pruning", (_label, content) => {
    const fixture = makeFixture();
    const relative = `${PROTECTED}/receipt.json`;
    const file = fixture.write(relative, content);
    const before = fs.readFileSync(file);
    const beforeMtime = fs.statSync(file).mtimeMs;
    const read = vi.spyOn(fs, "readFileSync");
    const remove = vi.spyOn(fs, "rmSync");

    for (const family of [undefined, "job"]) {
      for (const dry_run of [true, false]) {
        const result = fixture.command("artifact.prune", { older_than_days: 1, max_delete: 100, dry_run, ...(family ? { family } : {}) });
        expect(result).toMatchObject({ candidate_count: 0, selected_count: 0, deleted_count: 0, would_delete: [], deleted: [], skipped: [], dry_run });
      }
    }

    expect(read).not.toHaveBeenCalled();
    expect(remove).not.toHaveBeenCalled();
    read.mockRestore();
    expect(fs.readFileSync(file)).toEqual(before);
    expect(fs.statSync(file).mtimeMs).toBe(beforeMtime);
  });

  it.each(["dynamic-csharp-readonly/receipt.json", "dynamic-csharp-readonly/nested/receipt.json", "DYNAMIC-CSHARP-READONLY/receipt.json"])("lists %s with an additive non-prunable witness without reading content", (suffix) => {
    const fixture = makeFixture();
    const relative = `${ROOT}/${suffix}`;
    fixture.write(relative, "opaque");
    const read = vi.spyOn(fs, "readFileSync");

    const result = fixture.command("artifact.list", { family: "job", max_results: 100 });
    const entries = result.artifacts as Array<Record<string, unknown>>;
    expect(entries.find((entry) => entry.artifact_path === relative)).toEqual({
      type: "job", family: "job", artifact_path: relative, bytes: 6,
      modified_at: old.toISOString(), directory: false, extension: ".json", prunable: false,
    });
    expect(read).not.toHaveBeenCalled();
    expect(fixture.command("artifact.prune", { dry_run: false, older_than_days: 1 })).toMatchObject({ selected_count: 0, deleted_count: 0 });
  });

  it.each([`${PROTECTED}/receipt.json`, "job-artifacts/dynamic-csharp-readonly/receipt.json", "job-artifacts\\dynamic-csharp-readonly\\receipt.json"])("marks normalized metadata for %s non-prunable and still readable", (input) => {
    const fixture = makeFixture();
    fixture.write(`${PROTECTED}/receipt.json`, '{"command":"editor.execute_csharp.readonly","package_version":"fixture"}');
    expect(fixture.command("artifact.metadata", { artifact_path: input })).toMatchObject({
      artifact_path: `${PROTECTED}/receipt.json`, family: "job", prunable: false,
      readable_with: "job.artifact.read", reproducibility: { command: "editor.execute_csharp.readonly", package_version: "fixture" },
    });
  });

  it("protects only the exact segment-bound subtree and preserves ordinary retention", () => {
    const fixture = makeFixture();
    const protectedPath = `${PROTECTED}/receipt.json`;
    const siblings = [`${ROOT}/dynamic-csharp-readonly-old/receipt.json`, `${ROOT}/dynamic-csharp-readonly.json`, `${ROOT}/ordinary.json`];
    fixture.write(protectedPath, "protected");
    for (const relative of siblings) fixture.write(relative, "ordinary");

    const listed = fixture.command("artifact.list", { max_results: 100 });
    const entries = listed.artifacts as Array<Record<string, unknown>>;
    for (const relative of siblings) {
      expect(entries.find((entry) => entry.artifact_path === relative)).not.toHaveProperty("prunable");
      expect(fixture.command("artifact.metadata", { artifact_path: relative })).toMatchObject({ prunable: true });
    }
    const preview = fixture.command("artifact.prune", { older_than_days: 1, max_delete: 100 });
    expect(preview).toMatchObject({ candidate_count: 3, selected_count: 3, deleted_count: 0 });
    expect(preview.would_delete).toEqual([...siblings].sort());
    const deleted = fixture.command("artifact.prune", { older_than_days: 1, max_delete: 100, dry_run: false });
    expect(deleted).toMatchObject({ candidate_count: 3, deleted_count: 3, skipped_count: 0 });
    expect(deleted.deleted).toEqual([...siblings].sort());
    expect(fs.readFileSync(path.join(fixture.root, protectedPath), "utf8")).toBe("protected");
    for (const relative of siblings) expect(fs.existsSync(path.join(fixture.root, relative))).toBe(false);
  });

  it("retains traversal, offline and descriptor permission/schema gates", () => {
    const fixture = makeFixture();
    expect(runArtifactCommand("artifact.metadata", { artifact_path: `${PROTECTED}/../ordinary.json` }, fixture.record)).toMatchObject({ status: "error", error: { code: "path_traversal_rejected" } });
    expect(runArtifactCommand("artifact.prune", {}, undefined)).toMatchObject({ status: "error", error: { code: "project_offline" } });
    const prune = ARTIFACT_COMMANDS.find((command) => command.id === "artifact.prune")!;
    expect(prune).toMatchObject({ risk: "destructive", idempotency: "unsafe", feature_flag: "sgg_mcp_artifacts", schema: { additionalProperties: false } });
    expect(prune.schema.properties).not.toHaveProperty("release_readonly_receipts");
  });

  it("guards deletion independently after canonical binding and before any remove call", () => {
    const source = fs.readFileSync(path.join(process.cwd(), "src/mcp/artifact-commands.ts"), "utf8");
    const deletion = source.slice(source.indexOf("function deleteSelectedArtifacts("), source.indexOf("function scanArtifacts("));
    const guard = deletion.indexOf("if (isProtectedReadonlyCSharpArtifact(bound.relativePath))");
    expect(guard).toBeGreaterThan(deletion.indexOf("bindArtifactPath(record, artifact.artifact_path)"));
    expect(deletion.indexOf("fs.rmSync(bound.fullPath")).toBeGreaterThan(guard);
    expect(deletion.slice(guard, deletion.indexOf("try {", guard))).toContain('reason: "readonly_csharp_receipt_protected"');
    expect(deletion.slice(guard, deletion.indexOf("try {", guard))).toContain("continue;");
  });

  it("keeps admission observational while preserving publication temporary-file cleanup", () => {
    const source = fs.readFileSync(path.join(process.cwd(), "unity/Packages/com.sungeargames.unity-mcp/Editor/Commands/DynamicCSharpReadonlyResultStore.cs"), "utf8");
    const admission = source.slice(source.indexOf("static bool TryPrepareArtifactSlot("), source.indexOf("static bool TryPublish("));
    expect(admission).toContain('Directory.EnumerateFiles(root, "*.json", SearchOption.TopDirectoryOnly)');
    expect(admission).not.toContain("File.Delete");
    expect(admission).not.toContain("GetLastWriteTime");
    expect(admission).not.toContain("TryReadStable");
    expect(admission).not.toContain("TryPublish");
    expect(source).not.toContain("MaxArtifactAgeHours");
    expect(source).toContain("File.Delete(temporaryPath)");
    expect(source).toContain('"artifact_capacity_exhausted"');
    expect(source).toContain('"artifact_capacity_unavailable"');
    expect(source).toContain('"artifact_status_unavailable"');
  });
});

function makeFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr230-artifact-protection-"));
  tempDirs.push(root);
  const record: HeartbeatRecord = {
    schema_version: 1, instance_id: "game-current", project: "game", display_name: "Game",
    package_version: "fixture", project_root: root, canonical_project_root: root, aliases: [],
    unity_version: "6000.6.2f1", unity_pid: 12345, bridge_host: "127.0.0.1", bridge_port: 18142,
    token: "fixture-token", status: "ready", commands_hash: "catalog-fixture", last_heartbeat: now.toISOString(),
  };
  return {
    root, record,
    write(relative: string, content: string) {
      const file = path.join(root, relative);
      fs.mkdirSync(path.dirname(file), { recursive: true });
      fs.writeFileSync(file, content);
      fs.utimesSync(file, old, old);
      return file;
    },
    command(id: string, args: Record<string, unknown>) {
      const result = runArtifactCommand(id, args, record, now);
      expect(result.status).toBe("ok");
      if (result.status !== "ok") throw new Error(`Unexpected fixture result: ${result.error.code}`);
      return result.data as Record<string, unknown>;
    },
  };
}
