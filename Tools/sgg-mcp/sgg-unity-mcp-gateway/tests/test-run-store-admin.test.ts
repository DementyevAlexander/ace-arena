import { createHash, randomUUID } from "node:crypto";
import { spawn, type ChildProcess } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { main } from "../src/cli.js";
import { parseTestRunStoreAdminOptions, runTestRunStoreAdminCli } from "../src/operations/test-run-store-admin-cli.js";
import { inspectTestRunStoreMetadata, TestRunStoreAdminError, TEST_RUN_STORE_ADMIN_MAX_CANDIDATES, TEST_RUN_STORE_ADMIN_MAX_FILES } from "../src/operations/test-run-store-admin.js";
import { TestRunOperationStore, testRunCanonicalProjectRootHash, testRunOperationStoreDir, TEST_RUN_ARTIFACT_ROOT, type TestRunOperationBinding, type TestRunTerminalNoExecutionEvidence } from "../src/operations/test-run-operation-store.js";
import { CanonicalPathService } from "../src/paths/canonical-path.js";
import * as storage from "../src/security/safe-storage.js";
import * as artifactReader from "../src/security/project-artifact-reader.js";
import * as nativeCommit from "../src/operations/test-run-storage-native.js";
import { readTestRunStorageConfig } from "../src/operations/test-run-storage-config.js";
import { isValidObservedTestRunDto } from "../src/operations/test-run-terminal-validation.js";

const roots: string[] = [];
const now = new Date("2026-10-12T12:01:00.000Z");
let sequence = 0;
afterEach(() => {
  vi.restoreAllMocks();
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe("test run owner metadata administration", () => {
  it.each(["relative.json", "./gateway.json", path.join(os.tmpdir(), "fixture") + path.sep + ".." + path.sep + "gateway.json", path.join(os.tmpdir(), "fixture") + path.sep + "." + path.sep + "gateway.json"])("rejects non-exact host config paths before filesystem access: %s", file => {
    const read = vi.spyOn(fs, "lstatSync");
    expect(() => readTestRunStorageConfig(file)).toThrowError(expect.objectContaining({ code: "config_unavailable" }));
    expect(read).not.toHaveBeenCalled();
  });

  it("reports both truthful quotas and bytes including issuance reservations, without implicit history expiration", () => {
    const fixture = setup();
    const binding = sampleBinding(fixture.root);
    fixture.store.publish(binding);
    const expectedBytes = fs.readdirSync(fixture.dir).reduce((sum, name) => sum + fs.statSync(path.join(fixture.dir, name)).size, 0);
    expect(fixture.store.inspectStorage()).toMatchObject({ bytes: expectedBytes, bindings: 1, max_bytes: 209715200, max_bindings: 192000, retention_days: 30, capacity_state: "ok", scope: "gateway_test_run_owner_metadata", settings_source: "saved_config_restart_required" });
    const smaller = new TestRunOperationStore(fixture.dir, { maxBytes: expectedBytes, maxBindings: 1, now: () => now });
    expect(smaller.inspectStorage()).toMatchObject({ max_bytes: expectedBytes, max_bindings: 1, capacity_state: "capacity_exhausted", usage: { bytes_ratio: 1, bindings_ratio: 1 }, defaults: { max_bytes: 209715200, max_bindings: 192000 } });
    expect(new TestRunOperationStore(fixture.dir, { maxBytes: Math.ceil(expectedBytes / 0.95), now: () => now }).inspectStorage().capacity_state).toBe("near_capacity");
    const later = new TestRunOperationStore(fixture.dir, { now: () => new Date("2027-01-01T00:00:00.000Z") });
    expect(later.inspectStorage().bindings).toBe(1);
    expect(later.previewCleanup()).toMatchObject({ candidates: { bindings: 0 }, preserved: { unknown: 1 } });
    expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}.json`))).toBe(true);
  });

  it("removes only canonical bound no-execution metadata and never artifacts, logs, config or foreign records", () => {
    const fixture = setup();
    const refused = publish(fixture, "refused");
    const running = publish(fixture, "issued");
    const completed = publish(fixture, "issued");
    const reserved = publish(fixture, "reserved");
    const runningArtifact = writeArtifact(running, "running");
    const completedArtifact = writeArtifact(completed, "completed");
    const config = path.join(fixture.root, "config.json");
    const log = path.join(fixture.root, "Editor.log");
    const report = path.join(fixture.root, "raw-test-report.xml");
    for (const file of [config, log, report]) fs.writeFileSync(file, "do not touch", { mode: 0o600 });
    const foreign = path.join(fixture.dir, "foreign.json");
    fs.writeFileSync(foreign, "foreign metadata", { mode: 0o600 });
    const protectedFiles = [runningArtifact, completedArtifact, config, log, report, foreign, ...[running, completed, reserved].map(binding => path.join(fixture.dir, `${binding.operation_id}.json`))];
    const before = protectedFiles.map(file => fs.readFileSync(file));
    const preview = fixture.store.previewCleanup();
    expect(preview).toMatchObject({ candidates: { bindings: 1, files: 3 }, preserved: { active: 1, unknown: 2, foreign: 1 }, cleanup_evidence: "bound_authoritative_terminal_artifact_or_no_execution_receipt", scan: { complete: true } });
    expect(JSON.stringify(preview)).not.toContain(fixture.root);
    const result = fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint });
    expect(result.removed).toEqual(preview.candidates);
    for (const suffix of [".json", ".issued", ".terminal"]) expect(fs.existsSync(path.join(fixture.dir, `${refused.operation_id}${suffix}`))).toBe(false);
    expect(protectedFiles.map(file => fs.readFileSync(file))).toEqual(before);
    expect(fixture.store.previewCleanup().candidates.bindings).toBe(0);
  });

  it.each(["owner", "issuance", "terminal", "duplicate_terminal_key", "future_terminal", "orphan_terminal", "reservation_overlap"])("preserves malformed or uncertain metadata: %s", kind => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const file = (suffix: string) => path.join(fixture.dir, `${binding.operation_id}${suffix}`);
    if (kind === "owner") update(file(".json"), { canonical_project_root_hash: `sha256:${"0".repeat(64)}` });
    if (kind === "issuance") update(file(".issued"), { owner_binding_hash: `sha256:${"0".repeat(64)}` });
    if (kind === "terminal") update(file(".terminal"), { request_issuance_hash: `sha256:${"0".repeat(64)}` });
    if (kind === "future_terminal") update(file(".terminal"), { recorded_at: "2027-01-01T00:00:00.000Z" });
    if (kind === "duplicate_terminal_key") fs.writeFileSync(file(".terminal"), fs.readFileSync(file(".terminal"), "utf8").replace(/}\s*$/, ',"state":"not_executed"}'));
    if (kind === "orphan_terminal") fs.unlinkSync(file(".json"));
    if (kind === "reservation_overlap") fs.writeFileSync(file(".reserve"), " ".repeat(100), { mode: 0o600 });
    const before = contents(fixture.dir);
    const preview = fixture.store.previewCleanup();
    expect(preview.candidates.bindings).toBe(0);
    expect(preview.preserved.invalid + preview.preserved.unknown).toBe(1);
    fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint });
    expect(contents(fixture.dir)).toEqual(before);
  });

  it.each(["running", "completed", "unreadable"])("never lets a no-execution receipt override an existing %s artifact", status => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const artifact = writeArtifact(binding, status);
    if (status === "completed") update(artifact, terminalFields("completed"));
    if (status === "unreadable") fs.writeFileSync(artifact, "{invalid artifact", { mode: 0o600 });
    const metadata = contents(fixture.dir);
    const artifactBytes = fs.readFileSync(artifact);
    const preview = fixture.store.previewCleanup();
    expect(preview).toMatchObject({ candidates: { bindings: 0 }, preserved: { unknown: 1 } });
    expect(fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint }).removed.bindings).toBe(0);
    expect(contents(fixture.dir)).toEqual(metadata);
    expect(fs.readFileSync(artifact)).toEqual(artifactBytes);
  });

  it.each(["after-preview", "before-commit"])("preserves refusal metadata if a contradictory artifact appears %s", phase => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const preview = fixture.store.previewCleanup();
    expect(preview.candidates.bindings).toBe(1);
    const before = contents(fixture.dir);
    if (phase === "after-preview") writeArtifact(binding, "running");
    expect(() => fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint,
      ...(phase === "before-commit" ? { beforeCommit: () => { writeArtifact(binding, "running"); return true; } } : {})
    })).toThrowError(expect.objectContaining({ code: "preview_changed" }));
    expect(contents(fixture.dir)).toEqual(before);
    expect(fs.existsSync(path.join(binding.origin_project_root, binding.artifact_path))).toBe(true);
  });

  it.each(["replace_same_body", "change_body", "new_record", "expiry", "limits"])("refuses an obsolete exact preview before any deletion: %s", change => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const preview = fixture.store.previewCleanup();
    let store = fixture.store;
    const terminal = path.join(fixture.dir, `${binding.operation_id}.terminal`);
    if (change === "replace_same_body") {
      const temporary = path.join(fixture.dir, "replacement.tmp");
      fs.writeFileSync(temporary, fs.readFileSync(terminal), { mode: 0o600 });
      fs.renameSync(temporary, terminal);
    }
    if (change === "change_body") update(terminal, { recorded_at: new Date(now.getTime() - 1).toISOString() });
    if (change === "new_record") publish(fixture, "reserved");
    if (change === "expiry") store = new TestRunOperationStore(fixture.dir, { now: () => new Date(now.getTime() + 300000) });
    if (change === "limits") store = new TestRunOperationStore(fixture.dir, { maxBindings: 4096, now: () => now });
    const before = contents(fixture.dir);
    expect(() => store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint })).toThrowError(expect.objectContaining({ code: "preview_changed" }));
    expect(contents(fixture.dir)).toEqual(before);
  });

  it("revalidates candidate identity/body again before deletion", () => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const preview = fixture.store.previewCleanup();
    const terminal = path.join(fixture.dir, `${binding.operation_id}.terminal`);
    const original = fs.readSync;
    let reads = 0;
    vi.spyOn(fs, "readSync").mockImplementation((...args) => {
      const result = Reflect.apply(original, fs, args) as number;
      if (result > 0 && Buffer.isBuffer(args[1]) && args[1].toString().includes("sgg.unity_mcp.test_run_terminal_no_execution.v1") && ++reads === 3) update(terminal, { state: "running" });
      return result;
    });
    expect(() => fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint })).toThrowError(expect.objectContaining({ code: "preview_changed" }));
    expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}.json`))).toBe(true);
    expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}.issued`))).toBe(true);
  });

  (process.platform === "linux" ? it : it.skip)("preserves same-inode metadata changed during the deletion receipt fsync", () => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const preview = fixture.store.previewCleanup();
    const issued = path.join(fixture.dir, `${binding.operation_id}.issued`);
    const identity = fs.statSync(issued, { bigint: true });
    const before = fs.readFileSync(issued);
    const changed = Buffer.from(before.toString().replace(/("state"\s*:\s*")issued(")/, "$1bogus!$2"));
    expect(changed.length).toBe(before.length);
    expect(changed).not.toEqual(before);
    const fsync = fs.fsyncSync;
    let mutated = false;
    vi.spyOn(fs, "fsyncSync").mockImplementation(fd => {
      fsync(fd);
      const target = fs.readlinkSync(`/proc/self/fd/${fd}`);
      if (!mutated && path.dirname(target) === fixture.dir && /^\.delete-v1-.*\.json$/.test(path.basename(target))) {
        mutated = true;
        fs.writeFileSync(issued, changed);
        fs.utimesSync(issued, Number(identity.atimeNs) / 1e9, Number(identity.mtimeNs) / 1e9);
      }
    });
    expect(() => fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint })).toThrow("snapshot changed");
    expect(mutated).toBe(true);
    expect(storage.sameFileObjectIdentity(identity, fs.statSync(issued, { bigint: true }), process.platform)).toBe(true);
    expect(fs.readFileSync(issued)).toEqual(changed);
    for (const suffix of [".json", ".issued", ".terminal"]) expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}${suffix}`))).toBe(true);
    expect(fs.readdirSync(fixture.dir).some(name => name.endsWith(".tomb"))).toBe(false);
  });

  it("does not touch storage when confirmation is cancelled or absent", () => {
    const fixture = setup();
    const acquire = vi.spyOn(storage, "acquireStorageAdminLock");
    expect(() => fixture.store.cleanupMetadata({ confirm: false, previewFingerprint: `sha256:${"a".repeat(64)}` })).toThrow(TestRunStoreAdminError);
    expect(acquire).not.toHaveBeenCalled();
    expect(fs.existsSync(fixture.dir)).toBe(false);
  });

  it.each(["completed", "failed"])("cleans a strictly bound authoritative %s result without deleting its artifact", status => {
    const fixture = setup();
    const binding = publish(fixture, "issued");
    const file = writeArtifact(binding, status);
    update(file, terminalFields(status));
    const bytes = fs.readFileSync(file);
    const preview = fixture.store.previewCleanup();
    expect(preview.candidates).toMatchObject({ bindings: 1, files: 2 });
    expect(fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint }).removed).toEqual(preview.candidates);
    expect(fs.readFileSync(file)).toEqual(bytes);
  });

  it.each(["cancelled", "timeout", "execution_may_have_started", "running"])("preserves %s even with coherent terminal counts", status => {
    const fixture = setup();
    const binding = publish(fixture, "issued");
    const file = writeArtifact(binding, status);
    update(file, { ...terminalFields("cancelled"), status });
    if (status === "cancelled") expect(isValidObservedTestRunDto(artifactReader.readBoundTestRunArtifactSnapshot(binding, binding.operation_id).data)).toBe(true);
    const before = contents(fixture.dir);
    const preview = fixture.store.previewCleanup();
    expect(preview.candidates.bindings).toBe(0);
    fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint });
    expect(contents(fixture.dir)).toEqual(before);
    expect(fs.existsSync(file)).toBe(true);
  });

  it.each(["not_authoritative", "contradictory_counts", "wrong_owner", "unknown_status", "timeout", "changing"])("preserves unsafe completed-looking artifacts: %s", kind => {
    const fixture = setup();
    const binding = publish(fixture, "issued");
    const file = writeArtifact(binding, "completed");
    update(file, terminalFields("completed"));
    if (kind === "not_authoritative") update(file, { terminal_counts_authoritative: false });
    if (kind === "contradictory_counts") update(file, { authoritative_leaf_total: 2 });
    if (kind === "wrong_owner") update(file, { caller_args_hash: `sha256:${"0".repeat(64)}` });
    if (kind === "unknown_status") update(file, { status: "looks_finished" });
    if (kind === "timeout") update(file, { status: "timeout", success: false });
    if (kind === "changing") {
      const original = artifactReader.readBoundTestRunArtifactCleanupSnapshot;
      vi.spyOn(artifactReader, "readBoundTestRunArtifactCleanupSnapshot").mockImplementation((...args) => {
        const snapshot = original(...args);
        return { ...snapshot, data: { ...snapshot.data, artifact_path_advanced_during_read: true } };
      });
    }
    expect(fixture.store.previewCleanup()).toMatchObject({ candidates: { bindings: 0 }, preserved: { unknown: 1 } });
    expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}.json`))).toBe(true);
  });

  it("rejects an artifact changed after preview or immediately before cleanup commit", () => {
    const fixture = setup();
    const binding = publish(fixture, "issued");
    const file = writeArtifact(binding, "completed");
    update(file, terminalFields("completed"));
    const preview = fixture.store.previewCleanup();
    const original = artifactReader.readBoundTestRunArtifactCleanupSnapshot;
    let reads = 0;
    vi.spyOn(artifactReader, "readBoundTestRunArtifactCleanupSnapshot").mockImplementation((...args) => {
      if (++reads === 2) update(file, { status: "running", terminal_counts_authoritative: false, success: false });
      return original(...args);
    });
    expect(() => fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint })).toThrowError(expect.objectContaining({ code: "preview_changed" }));
    expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}.json`))).toBe(true);
    expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}.issued`))).toBe(true);
  });

  it("fails closed on insecure linked owner metadata and does not delete its target", () => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const owner = path.join(fixture.dir, `${binding.operation_id}.json`);
    const outside = path.join(fixture.root, "outside.json");
    fs.renameSync(owner, outside);
    fs.symlinkSync(outside, owner);
    expect(() => fixture.store.previewCleanup()).toThrow();
    expect(fs.existsSync(outside)).toBe(true);
  });

  it("keeps cleanup batching and filesystem inspection explicitly bounded", () => {
    expect(TEST_RUN_STORE_ADMIN_MAX_CANDIDATES).toBe(256);
    expect(TEST_RUN_STORE_ADMIN_MAX_FILES).toBeGreaterThanOrEqual(192000 * 4 + 256);
    expect(TEST_RUN_STORE_ADMIN_MAX_FILES).toBe(4000256);
  });

  it.each(["status", "preview-cleanup", "stale-cleanup", "configure"])("never maintains retained tombstones or publication temps during %s", action => {
    const fixture = setup();
    const config = writeConfig(fixture);
    publish(fixture, "refused");
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    const protectedFiles = retainedMaintenanceFiles(fixture.dir);
    const before = protectedFiles.map(file => fs.readFileSync(file));
    if (action === "status" || action === "preview-cleanup") expect(cli(["--test-run-store", action, "--config", config]).code).toBe(0);
    if (action === "stale-cleanup") expect(cli(["--test-run-store", "cleanup", "--config", config, "--confirm", "--preview-fingerprint", `sha256:${"0".repeat(64)}`]).code).toBe(1);
    if (action === "configure") expect(cli(["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]).code).toBe(0);
    expect(protectedFiles.map(file => fs.readFileSync(file))).toEqual(before);
    expect(fs.readdirSync(fixture.dir).some(name => name.includes(".store.lock"))).toBe(false);
  });

  it.each(["after-preview", "before-commit"])("rejects a replaced store directory containing the identical candidate file objects: %s", phase => {
    const fixture = setup();
    const binding = publish(fixture, "refused");
    const preview = fixture.store.previewCleanup();
    const owner = path.join(fixture.dir, `${binding.operation_id}.json`);
    const identity = fs.statSync(owner, { bigint: true });
    const originalDirectory = `${fixture.dir}-original`;
    const replace = () => {
      fs.renameSync(fixture.dir, originalDirectory);
      fs.mkdirSync(fixture.dir, { mode: 0o700 });
      for (const name of fs.readdirSync(originalDirectory).filter(name => !name.startsWith("."))) fs.renameSync(path.join(originalDirectory, name), path.join(fixture.dir, name));
    };
    if (phase === "after-preview") replace();
    expect(() => fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint, ...(phase === "before-commit" ? { beforeCommit: () => { replace(); return true; } } : {}) })).toThrow();
    expect(storage.sameFileObjectIdentity(identity, fs.statSync(owner, { bigint: true }), process.platform)).toBe(true);
    for (const suffix of [".json", ".issued", ".terminal"]) expect(fs.existsSync(path.join(fixture.dir, `${binding.operation_id}${suffix}`))).toBe(true);
  });

  it.each(["hardlink", "duplicate-top-level", "duplicate-nested", "same-body-replacement"])("requires strict exact-object artifact evidence: %s", kind => {
    const fixture = setup();
    const binding = publish(fixture, "issued");
    const file = writeArtifact(binding, "completed");
    update(file, terminalFields("completed"));
    if (kind === "same-body-replacement") fs.utimesSync(file, now, now);
    const before = contents(fixture.dir);
    const preview = fixture.store.previewCleanup();
    if (kind === "hardlink") fs.linkSync(file, path.join(fixture.root, "artifact-hardlink.json"));
    if (kind === "duplicate-top-level") fs.writeFileSync(file, fs.readFileSync(file, "utf8").replace(/}$/, ',"status":"completed"}'));
    if (kind === "duplicate-nested") fs.writeFileSync(file, fs.readFileSync(file, "utf8").replace(/}$/, ',"nested":{"count":1,"count":1}}'));
    if (kind === "same-body-replacement") {
      const stat = fs.statSync(file);
      const replacement = path.join(path.dirname(file), "replacement.json");
      fs.writeFileSync(replacement, fs.readFileSync(file), { mode: 0o600 });
      fs.utimesSync(replacement, stat.atime, stat.mtime);
      fs.renameSync(replacement, file);
      expect(fs.statSync(file, { bigint: true }).mtimeNs).toBe(BigInt(stat.mtime.getTime()) * 1000000n);
    }
    expect(() => fixture.store.cleanupMetadata({ confirm: true, previewFingerprint: preview.preview_fingerprint })).toThrowError(expect.objectContaining({ code: "preview_changed" }));
    if (kind !== "same-body-replacement") expect(fixture.store.previewCleanup().candidates.bindings).toBe(0);
    expect(contents(fixture.dir)).toEqual(before);
    expect(fs.existsSync(file)).toBe(true);
  });

  it("enumerates incrementally, checks the entry cap, and closes the handle on refusal", () => {
    const fixture = setup();
    fixture.store.inspectStorage();
    const custody = storage.openStorageDirectoryCustody(fixture.dir);
    let reads = 0;
    const entry = { name: ".ignored", isFile: () => true, isSymbolicLink: () => false };
    const read = () => { reads++; return entry; };
    const close = vi.fn();
    vi.spyOn(fs, "opendirSync").mockReturnValue({ readSync: read, closeSync: close } as unknown as fs.Dir);
    const readdir = vi.spyOn(fs, "readdirSync");
    // Isolate the enumeration gate from filesystem work, so the real fixed cap is exercised.
    const boundedCustody = { ...custody, assertCurrent: () => {}, pathFor: () => path.join(fixture.dir, ".store.lock") };
    try {
      expect(() => inspectTestRunStoreMetadata(boundedCustody, fixture.store.limits, Date.now() + 10000)).toThrowError(expect.objectContaining({ code: "scan_limit_exceeded" }));
      expect(reads).toBe(TEST_RUN_STORE_ADMIN_MAX_FILES + 1);
      expect(close).toHaveBeenCalledOnce();
      expect(readdir).not.toHaveBeenCalled();
    } finally { custody.close(); }
  });

  it("starts the ten-second deadline before admin lock acquisition and refuses enumeration after it expires", () => {
    const fixture = setup();
    fixture.store.inspectStorage();
    const start = Date.now();
    const clock = vi.spyOn(Date, "now").mockReturnValue(start);
    const acquire = storage.acquireStorageAdminLock;
    vi.spyOn(storage, "acquireStorageAdminLock").mockImplementation((custody, name, deadline) => {
      expect(deadline).toBe(start + 10000);
      const lock = acquire(custody, name, deadline);
      clock.mockReturnValue(deadline);
      return lock;
    });
    const enumerate = vi.spyOn(fs, "opendirSync");
    expect(() => fixture.store.inspectStorage()).toThrowError(expect.objectContaining({ code: "deadline_exceeded" }));
    expect(enumerate).not.toHaveBeenCalled();
  });

  it("closes incremental enumeration immediately when its shared deadline expires", () => {
    const fixture = setup();
    fixture.store.inspectStorage();
    const custody = storage.openStorageDirectoryCustody(fixture.dir);
    const start = Date.now();
    const clock = vi.spyOn(Date, "now").mockReturnValue(start);
    const read = vi.fn(() => { clock.mockReturnValue(start + 10000); return { name: ".ignored", isFile: () => true, isSymbolicLink: () => false }; });
    const close = vi.fn();
    vi.spyOn(fs, "opendirSync").mockReturnValue({ readSync: read, closeSync: close } as unknown as fs.Dir);
    try {
      expect(() => inspectTestRunStoreMetadata(custody, fixture.store.limits, start + 10000)).toThrowError(expect.objectContaining({ code: "deadline_exceeded" }));
      expect(read).toHaveBeenCalledOnce();
      expect(close).toHaveBeenCalledOnce();
    } finally { custody.close(); }
  });

  it("rejects artifact publication advancing during the strict evidence read", () => {
    const fixture = setup();
    const binding = publish(fixture, "issued");
    const file = writeArtifact(binding, "completed");
    update(file, terminalFields("completed"));
    const snapshot = artifactReader.readBoundTestRunArtifactCleanupSnapshot(binding, binding.operation_id);
    expect(snapshot.cleanupEvidence).toMatchObject({ ino: fs.statSync(file, { bigint: true }).ino.toString(), size: fs.statSync(file, { bigint: true }).size.toString() });
    const bytes = fs.readFileSync(file);
    expect(() => artifactReader.readBoundTestRunArtifactCleanupSnapshot(binding, binding.operation_id, { afterRead: () => {
      const replacement = `${file}.replacement`;
      fs.writeFileSync(replacement, bytes, { mode: 0o600 });
      fs.renameSync(replacement, file);
    } })).toThrow(artifactReader.ProjectArtifactReadError);
    expect(fs.readFileSync(file)).toEqual(bytes);
  });
});

describe("fixed local test-run-store CLI", () => {
  it("atomically changes only explicit owner quotas after exact confirmation and preserves policy/privacy/retention", () => {
    const fixture = setup();
    const config = writeConfig(fixture, { max_bytes: 8388608, max_bindings: 4096, retention_days: 45 });
    update(config, { features: { build_test_automation: false }, approvals: { dir: path.join(fixture.root, "approvals"), auto_allow_ask: false, auto_allow_known_unity_popups: false }, mcp: { host: "127.0.0.1", port: 27182, token_env: "SECRET_ENV" } });
    const original = JSON.parse(fs.readFileSync(config, "utf8"));
    const policy = path.join(fixture.root, "policy.json");
    const policyText = JSON.stringify({ schema_version: 1, defaults: { read: "allow", write: "ask", execute: "deny", destructive: "deny", network: "deny" }, projects: {} });
    fs.writeFileSync(policy, policyText, { mode: 0o600 });
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string; scope_fingerprint: string } };
    const configured = cli(["--test-run-store", "configure", "--config", config, "--policy", policy, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]);
    expect(configured.code).toBe(0);
    expect(configured.data).toMatchObject({ status: "ok", action: "configure", data: { max_bytes: 209715200, max_bindings: 192000, retention_days: 45, restart_required: true, settings_source: "saved_config_restart_required", scope_fingerprint: status.data.scope_fingerprint } });
    const saved = JSON.parse(fs.readFileSync(config, "utf8"));
    expect(saved).toEqual({ ...original, test_runs: { storage: { max_bytes: 209715200, max_bindings: 192000, retention_days: 45 } } });
    expect(fs.readFileSync(policy, "utf8")).toBe(policyText);
    expect(fs.readdirSync(fixture.root).some(name => name.endsWith(".tmp") || name.includes("config.lock"))).toBe(false);
  });

  it("rejects changed config or missing human confirmation without writing settings", () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    const prefix = ["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--config-fingerprint", status.data.config_fingerprint];
    const before = fs.readFileSync(config);
    expect(cli(prefix).data).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(fs.readFileSync(config)).toEqual(before);
    update(config, { logging: { level: "warn", audit_enabled: false } });
    const changed = fs.readFileSync(config);
    expect(cli([...prefix, "--confirm"]).data).toMatchObject({ status: "error", error: { code: "config_changed" } });
    expect(fs.readFileSync(config)).toEqual(changed);
  });

  it("truthfully reports settings saved even if the separate capacity scan is unavailable, never false zero usage", () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    vi.spyOn(TestRunOperationStore.prototype, "inspectStorage").mockImplementation(() => { throw new TestRunStoreAdminError("deadline_exceeded"); });
    const result = cli(["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]);
    expect(result).toMatchObject({ code: 0, data: { status: "ok", data: { bytes: null, bindings: null, capacity_state: "unavailable", configuration_saved: true, restart_required: true, storage_error_code: "deadline_exceeded" } } });
    expect(JSON.parse(fs.readFileSync(config, "utf8"))).toMatchObject({ test_runs: { storage: { max_bytes: 209715200, max_bindings: 192000 } } });
  });

  it("shares one ten-second configure deadline through commit and scan without misreporting a completed save", () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    const start = Date.now();
    const clock = vi.spyOn(Date, "now").mockReturnValue(start);
    let commitDeadline: number | undefined;
    let scanDeadline: number | undefined;
    const commit = nativeCommit.runNativeStorageCommit;
    vi.spyOn(nativeCommit, "runNativeStorageCommit").mockImplementation((request, deadline) => {
      commitDeadline = deadline;
      commit(request, deadline);
      clock.mockReturnValue(start + 10000);
    });
    const inspect = TestRunOperationStore.prototype.inspectStorage;
    vi.spyOn(TestRunOperationStore.prototype, "inspectStorage").mockImplementation(function (this: TestRunOperationStore, deadline) {
      scanDeadline = deadline;
      return inspect.call(this, deadline);
    });
    const acquire = vi.spyOn(storage, "acquireStorageAdminLock");
    const enumerate = vi.spyOn(fs, "opendirSync");
    const result = cli(["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]);
    expect(commitDeadline).toBe(start + 10000);
    expect(scanDeadline).toBe(commitDeadline);
    expect(acquire).not.toHaveBeenCalled();
    expect(enumerate).not.toHaveBeenCalled();
    expect(result).toMatchObject({ code: 0, data: { status: "ok", data: { configuration_saved: true, restart_required: true,
      bytes: null, bindings: null, capacity_state: "unavailable", storage_error_code: "deadline_exceeded" } } });
    expect(JSON.parse(fs.readFileSync(config, "utf8"))).toMatchObject({ test_runs: { storage: { max_bytes: 209715200, max_bindings: 192000 } } });
  });

  it("does not create directories for a missing exact config", () => {
    const fixture = setup();
    const parent = path.join(fixture.root, "missing");
    expect(cli(["--test-run-store", "status", "--config", path.join(parent, "gateway.json")]).code).toBe(1);
    expect(fs.existsSync(parent)).toBe(false);
  });

  it.each(["symlink", "hardlink", "duplicate_key"])("refuses unsafe config source for status/configure: %s", kind => {
    const fixture = setup();
    const config = writeConfig(fixture);
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    const original = fs.readFileSync(config);
    const other = path.join(fixture.root, "other.json");
    if (kind === "symlink") { fs.renameSync(config, other); fs.symlinkSync(other, config); }
    if (kind === "hardlink") fs.linkSync(config, other);
    if (kind === "duplicate_key") fs.writeFileSync(config, original.toString().replace(/}$/, ',"logging":{"level":"warn","audit_enabled":false}}'));
    const before = fs.readFileSync(config);
    expect(cli(["--test-run-store", "status", "--config", config]).code).toBe(1);
    expect(cli(["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]).code).toBe(1);
    expect(fs.readFileSync(config)).toEqual(before);
    if (kind !== "duplicate_key") expect(fs.readFileSync(other)).toEqual(original);
  });

  it("rejects concurrent privacy changes before the native held-handle compare/commit", () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    const original = nativeCommit.runNativeStorageCommit;
    vi.spyOn(nativeCommit, "runNativeStorageCommit").mockImplementation((request, deadline) => {
      update(config, { logging: { level: "error", audit_enabled: false } });
      original(request, deadline);
    });
    const result = cli(["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]);
    expect(result.data).toMatchObject({ status: "error", error: { code: "config_changed" } });
    expect(JSON.parse(fs.readFileSync(config, "utf8"))).toMatchObject({ logging: { level: "error" } });
    expect(fs.readdirSync(fixture.root).some(name => name.endsWith(".tmp") || name.includes("config.lock"))).toBe(false);
  });

  it.each(["file", "directory"])("rejects same-content config %s identity replacement before native commit", target => {
    const fixture = setup();
    const config = writeConfig(fixture);
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    const before = fs.readFileSync(config);
    const original = nativeCommit.runNativeStorageCommit;
    vi.spyOn(nativeCommit, "runNativeStorageCommit").mockImplementation((request, deadline) => {
      if (target === "file") {
        const replacement = path.join(fixture.root, "replacement.json");
        const stat = fs.statSync(config);
        fs.writeFileSync(replacement, before, { mode: 0o600 });
        fs.utimesSync(replacement, stat.atime, stat.mtime);
        fs.renameSync(replacement, config);
      } else {
        const renamed = `${fixture.root}-old`;
        roots.push(renamed);
        fs.renameSync(fixture.root, renamed);
        fs.mkdirSync(fixture.root, { mode: 0o700 });
        fs.renameSync(path.join(renamed, path.basename(config)), config);
      }
      original(request, deadline);
    });
    expect(cli(["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]).data).toMatchObject({ status: "error", error: { code: "config_changed" } });
    expect(fs.readFileSync(config)).toEqual(before);
  });

  it("shares the existing settings writer sidecar and preserves a concurrent approval/privacy writer", async () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    const status = cli(["--test-run-store", "status", "--config", config]).data as { data: { config_fingerprint: string } };
    const before = fs.readFileSync(config);
    const ready = path.join(fixture.root, "writer-ready");
    const release = path.join(fixture.root, "writer-release");
    const script = '$ErrorActionPreference="Stop"; $ProgressPreference="SilentlyContinue"; $h=New-Object IO.FileStream($env:SGG_CONFIG_LOCK,[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None); try{[IO.File]::WriteAllText($env:SGG_READY,"ready"); $deadline=[DateTime]::UtcNow.AddSeconds(10); while(-not [IO.File]::Exists($env:SGG_RELEASE) -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 5}}finally{$h.Dispose()}';
    const child: ChildProcess = process.platform === "win32"
      ? spawn("powershell.exe", ["-NoProfile", "-NonInteractive", "-Command", script], { env: { ...process.env, SGG_CONFIG_LOCK: `${config}.settings-write.lock`, SGG_READY: ready, SGG_RELEASE: release }, stdio: "pipe" })
      : spawn("/usr/bin/flock", ["--exclusive", `${config}.settings-write.lock`, process.execPath, "--input-type=module", "--eval", 'import fs from "node:fs";fs.writeFileSync(process.argv[1],"ready");const d=Date.now()+10000;const sleep=new Int32Array(new SharedArrayBuffer(4));while(!fs.existsSync(process.argv[2])&&Date.now()<d)Atomics.wait(sleep,0,0,5);', ready, release], { stdio: "pipe" });
    const exit = new Promise<number | null>((resolve, reject) => { child.once("exit", resolve); child.once("error", reject); });
    try {
      const deadline = Date.now() + 2000;
      while (!fs.existsSync(ready) && Date.now() < deadline) await new Promise(resolve => setTimeout(resolve, 5));
      expect(fs.existsSync(ready)).toBe(true);
      const result = cli(["--test-run-store", "configure", "--config", config, "--max-bytes", "209715200", "--max-bindings", "192000", "--confirm", "--config-fingerprint", status.data.config_fingerprint]);
      expect(result.code).toBe(1);
      expect(fs.readFileSync(config)).toEqual(before);
      expect(fs.readdirSync(fixture.root).some(name => name.startsWith(".test-run-storage-"))).toBe(false);
    } finally { fs.writeFileSync(release, "release"); expect(await exit).toBe(0); }
  });

  it("binds cleanup preview to the exact config source as well as the metadata inventory", () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    publish(fixture, "refused");
    const preview = cli(["--test-run-store", "preview-cleanup", "--config", config]).data as { data: { preview_fingerprint: string } };
    update(config, { logging: { level: "warn", audit_enabled: false } });
    expect(cli(["--test-run-store", "cleanup", "--config", config, "--confirm", "--preview-fingerprint", preview.data.preview_fingerprint]).data).toMatchObject({ status: "error", error: { code: "preview_changed" } });
    expect(fixture.store.inspectStorage().bindings).toBe(1);
  });

  it("returns bounded canonical JSON with saved old quotas and new defaults, without config writes", () => {
    const fixture = setup();
    const config = writeConfig(fixture, { max_bytes: 8388608, max_bindings: 4096, retention_days: 30 });
    const original = fs.readFileSync(config);
    const output = cli(["--test-run-store", "status", "--config", config, "--format", "json"]);
    expect(output.code).toBe(0);
    expect(output.data).toMatchObject({ schema_version: 1, status: "ok", action: "status", data: { max_bytes: 8388608, max_bindings: 4096, defaults: { max_bytes: 209715200, max_bindings: 192000 }, settings_source: "saved_config_restart_required" } });
    expect(output.text.length).toBeLessThan(2048);
    expect(output.text).not.toContain(fixture.root);
    expect(fs.readFileSync(config)).toEqual(original);
  });

  it("routes the exact fixed verb through the production entry without starting a Gateway", async () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    let text = "";
    vi.spyOn(process.stdout, "write").mockImplementation(chunk => { text += String(chunk); return true; });
    const previous = process.exitCode;
    try {
      await main(["--config", config, "--test-run-store", "status", "--json"], {});
      expect(JSON.parse(text)).toMatchObject({ status: "ok", action: "status" });
      expect(process.exitCode).toBe(0);
    } finally { process.exitCode = previous; }
  });

  it("requires confirm plus the exact fresh fingerprint and preserves private paths/error contents", () => {
    const fixture = setup();
    const config = writeConfig(fixture);
    publish(fixture, "refused");
    const prefix = ["--test-run-store", "cleanup", "--config", config];
    expect(cli(prefix).data).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    const preview = cli(["--test-run-store", "preview-cleanup", "--config", config]).data as { data: { preview_fingerprint: string } };
    const confirmed = cli([...prefix, "--confirm", "--preview-fingerprint", preview.data.preview_fingerprint]);
    expect(confirmed.data).toMatchObject({ status: "ok", data: { removed: { bindings: 1, files: 3 } } });
    fs.writeFileSync(config, "private_token=do-not-echo", { mode: 0o600 });
    const rejected = cli(["--test-run-store", "status", "--config", config]);
    expect(rejected.data).toMatchObject({ status: "error", error: { code: "storage_unavailable" } });
    expect(rejected.text).not.toContain("do-not-echo");
    expect(rejected.text).not.toContain(config);
  });

  it.each([
    ["--test-run-store", "status"],
    ["--test-run-store", "status", "--config", "relative.json"],
    ["--test-run-store", "force-all", "--config", "/private/config.json"],
    ["--test-run-store", "status", "--config", "/private/config.json", "--config", "/other.json"],
    ["--test-run-store", "status", "--config", "/private/config.json", "--confirm"],
    ["--test-run-store", "cleanup", "--config", "/private/config.json", "--confirm", "--preview-fingerprint", "invalid"],
    ["--test-run-store", "status", "--config", "/private/config.json", "--output", "/arbitrary"],
  ].map(args => [args]))("rejects invalid/non-exact admin arguments before storage access: %j", args => {
    expect(() => parseTestRunStoreAdminOptions(args)).toThrow();
    const acquire = vi.spyOn(storage, "acquireStorageAdminLock");
    expect(cli(args).code).toBe(1);
    expect(acquire).not.toHaveBeenCalled();
  });
});

function setup() {
  const parent = process.platform !== "win32" && fs.existsSync("/tmp/opencode") ? "/tmp/opencode" : os.tmpdir();
  const root = fs.mkdtempSync(path.join(parent, "sgg-store-admin-"));
  roots.push(root);
  const dir = testRunOperationStoreDir(path.join(root, "instances"));
  const store = new TestRunOperationStore(dir, { now: () => now });
  return { root, dir, store };
}
function sampleBinding(root: string): TestRunOperationBinding {
  const operationId = `test-20261012-120000-${(++sequence).toString(16).padStart(8, "0")}`;
  const canonical = new CanonicalPathService().normalizeProjectRoot(root);
  return { schema_version: 1, contract: "sgg.unity_mcp.test_run_operation_owner.v1", command: "test.run", operation_id: operationId,
    project: "fixture", original_project_selector: "fixture", origin_project_root: root, canonical_project_root: canonical,
    canonical_project_root_hash: testRunCanonicalProjectRootHash(canonical), origin_instance_id: "fixture-instance", origin_unity_pid: 1234,
    origin_unity_process_start_utc: "2026-10-12T11:00:00.000Z", caller_args_hash: `sha256:${"a".repeat(64)}`,
    artifact_path: `${TEST_RUN_ARTIFACT_ROOT}/${operationId}.json`, artifact_schema_version: 3, package_id: "com.sungeargames.unity-mcp",
    package_version: "2026.10.11-2", commands_hash: "fixture-hash", catalog_generation: 1,
    artifact_schema_capability: "test_run_artifact_v3", created_at: "2026-10-12T12:00:00.000Z" };
}
function publish(fixture: ReturnType<typeof setup>, kind: "refused" | "issued" | "reserved") {
  const binding = sampleBinding(fixture.root);
  fixture.store.publish(binding);
  if (kind !== "reserved") {
    const marker = fixture.store.publishRequestIssuance(binding);
    if (kind === "refused") fixture.store.publishTerminalNoExecution(binding, marker, evidence());
  }
  return binding;
}
function writeArtifact(binding: TestRunOperationBinding, status: string): string {
  const file = path.join(binding.origin_project_root, binding.artifact_path);
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify({ command: "test.run", artifact_schema_version: 3, package_version: binding.package_version, args_hash: `sha256:${"b".repeat(64)}`, caller_args_hash: binding.caller_args_hash, operation_id: binding.operation_id, run_id: binding.operation_id, status, artifact_path: binding.artifact_path }), { mode: 0o600 });
  return file;
}
function update(file: string, values: Record<string, unknown>): void { fs.writeFileSync(file, JSON.stringify({ ...JSON.parse(fs.readFileSync(file, "utf8")), ...values }), { mode: 0o600 }); }
function contents(dir: string) { return fs.readdirSync(dir).sort().map(name => [name, createHash("sha256").update(fs.readFileSync(path.join(dir, name))).digest("hex")]); }
function writeConfig(fixture: ReturnType<typeof setup>, settings?: { max_bytes: number; max_bindings: number; retention_days: number }): string {
  const config = path.join(fixture.root, "gateway.json");
  fs.writeFileSync(config, JSON.stringify({ mcp: { host: "127.0.0.1", port: 27182 }, registry: { instances_dir: path.join(fixture.root, "instances"), stale_after_ms: 90000 }, logging: { level: "info", audit_enabled: false }, ...(settings ? { test_runs: { storage: settings } } : {}) }), { mode: 0o600 });
  return config;
}
function cli(args: string[]) {
  let text = "";
  const code = runTestRunStoreAdminCli(args, { env: {}, now: () => now, stdout: value => { text += value; } });
  return { code, text, data: JSON.parse(text) as unknown };
}
function evidence(): TestRunTerminalNoExecutionEvidence {
  return { bridge_error_code: "package_refresh_required", reason: "package_refresh_required", package_update: {
    status: "package_refresh_required", refresh_pending: true, package_refresh_required: true, package_refresh_wait_can_advance: false,
    stale_package_resolution: false, fingerprint: "fixture", pending_fingerprint: "pending", last_transition_utc: "",
    local_package_code_inventory: { status: "current", failure_reason: "", mismatch_package: "", local_package_count: 0, source_file_count: 0,
      asset_database_file_count: 0, missing_asset_file_count: 0, unexpected_asset_file_count: 0, source_fingerprint: "", asset_database_fingerprint: "", source_state_fingerprint: "" },
  } };
}
function terminalFields(status: string) {
  return { terminal: true, success: status === "completed", artifact_ready: true, artifact_persisted: true, terminal_counts_authoritative: true, terminal_count_source: "unity_test_runner",
    pass_count: status === "completed" ? 1 : 0, fail_count: status === "failed" ? 1 : 0, skip_count: status === "cancelled" ? 1 : 0, inconclusive_count: 0, authoritative_leaf_total: 1 };
}
function retainedMaintenanceFiles(dir: string): string[] {
  const nonce = randomUUID();
  const tombName = `.delete-v1-${nonce}.tomb`;
  const tomb = path.join(dir, tombName);
  const metadata = path.join(dir, `.delete-v1-${nonce}.json`);
  fs.writeFileSync(tomb, "retained deleted metadata", { mode: 0o600 });
  const stat = fs.statSync(tomb, { bigint: true });
  fs.writeFileSync(metadata, JSON.stringify({ schema_version: 1, operation: "delete", nonce, basename_hash: createHash("sha256").update("old.json").digest("hex"), dev: stat.dev.toString(), ino: stat.ino.toString(), created_at: "2026-01-01T00:00:00.000Z", tomb_name: tombName }), { mode: 0o600 });
  const temp = path.join(dir, `.publish-v1-1-2147483647-${"0".repeat(16)}-${randomUUID()}.tmp`);
  fs.writeFileSync(temp, "retained publisher temporary metadata", { mode: 0o600 });
  return [tomb, metadata, temp];
}
