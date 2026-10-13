import { createHash } from "node:crypto";
import fs, { type BigIntStats } from "node:fs";
import path from "node:path";

import { visit } from "jsonc-parser/lib/esm/main.js";

import { durableUnlinkCustodiedFile, sameFileObjectIdentity, type SecureFileStat, type StorageDirectoryCustody } from "../security/safe-storage.js";
import { ProjectArtifactReadError, readBoundTestRunArtifactCleanupSnapshot } from "../security/project-artifact-reader.js";
import {
  assertTestRunOperationBinding, assertTestRunRequestIssuanceMarker, assertTestRunTerminalNoExecutionReceipt,
  isTestRunOperationId, type TestRunOperationBinding, type TestRunOperationStoreLimits,
} from "./test-run-operation-store.js";
import { hasAuthoritativeClosedTestRunOutcome } from "./test-run-terminal-validation.js";
import { runNativeStorageCommit, type NativeFileEvidence } from "./test-run-storage-native.js";
import { TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS, TEST_RUN_STORAGE_DEFAULT_MAX_BYTES, TEST_RUN_STORAGE_DEFAULT_RETENTION_DAYS } from "./test-run-storage-settings.js";

export const TEST_RUN_STORE_ADMIN_MAX_FILES = 4_000_256;
export const TEST_RUN_STORE_ADMIN_MAX_CANDIDATES = 256;
export const TEST_RUN_STORE_ADMIN_DEADLINE_MS = 10_000;
const PREVIEW_WINDOW_MS = 5 * 60_000;
const SCOPE = "gateway_test_run_owner_metadata";
const SUFFIXES = [".json", ".issued", ".reserve", ".terminal"] as const;
type Counts = { bindings: number; files: number; bytes: number };
type FileSnapshot = { file: string; name: string; stat: SecureFileStat & { mtimeNs: bigint; ctimeNs: bigint }; hash?: string; body?: Buffer; nativeEvidence?: NativeFileEvidence };
type Candidate = { operationId: string; files: FileSnapshot[]; binding: TestRunOperationBinding; noExecutionReceipt: boolean; artifactWitness: string; artifactEvidence?: NativeFileEvidence };
export type TestRunStoreAdminStatus = {
  bytes: number;
  max_bytes: number;
  bindings: number;
  max_bindings: number;
  retention_days: number;
  capacity_state: "ok" | "near_capacity" | "capacity_exhausted";
  usage: { bytes_ratio: number; bindings_ratio: number };
  scope: typeof SCOPE;
  settings_source: "saved_config_restart_required";
  defaults: { max_bytes: number; max_bindings: number; retention_days: number };
};
export type TestRunStoreCleanupPreview = TestRunStoreAdminStatus & {
  preview_fingerprint: string;
  preview_expires_at: string;
  candidates: Counts;
  preserved: { active: number; unknown: number; invalid: number; foreign: number; batch_limit: number };
  scan: { files: number; max_files: number; max_candidates: number; complete: true };
  cleanup_evidence: "bound_authoritative_terminal_artifact_or_no_execution_receipt";
};
export class TestRunStoreAdminError extends Error {
  constructor(readonly code: "confirmation_required" | "preview_changed" | "scan_limit_exceeded" | "deadline_exceeded" | "storage_unavailable" | "config_unavailable" | "config_changed" | "invalid_limits") {
    super("Test run owner metadata administration was refused");
  }
}

/** Internal lock-held operations. Never calls a bridge, executes tests or alters project artifacts. */
export function inspectTestRunStoreMetadata(custody: StorageDirectoryCustody, limits: TestRunOperationStoreLimits, deadlineUnixMs: number): TestRunStoreAdminStatus {
  return statusFromFiles(scanFiles(custody, deadlineUnixMs), limits);
}

export function previewTestRunStoreCleanup(custody: StorageDirectoryCustody, limits: TestRunOperationStoreLimits, now: Date, deadlineUnixMs: number, fingerprintScope = ""): TestRunStoreCleanupPreview {
  return cleanupPlan(custody, limits, now, deadlineUnixMs, fingerprintScope).preview;
}

export function cleanupTestRunStoreMetadata(custody: StorageDirectoryCustody, limits: TestRunOperationStoreLimits, now: Date, deadlineUnixMs: number, confirmation: { confirm: boolean; previewFingerprint: string; beforeCommit?: () => boolean; nativeConfig?: NativeFileEvidence }, fingerprintScope = "") {
  if (confirmation.confirm !== true || !/^sha256:[0-9a-f]{64}$/.test(confirmation.previewFingerprint)) throw new TestRunStoreAdminError("confirmation_required");
  if (process.platform !== "win32") custody.requireMutationCustody();
  const plan = cleanupPlan(custody, limits, now, deadlineUnixMs, fingerprintScope);
  if (plan.preview.preview_fingerprint !== confirmation.previewFingerprint) throw new TestRunStoreAdminError("preview_changed");
  // Revalidate every candidate before deleting anything. The shared store lock excludes publishers.
  for (const candidate of plan.candidates) {
    custody.assertCurrent();
    if (candidateArtifactWitness(candidate) !== candidate.artifactWitness) throw new TestRunStoreAdminError("preview_changed");
    custody.assertCurrent();
    for (const file of candidate.files) {
      checkDeadline(deadlineUnixMs);
      const current = captureFile(custody, file.name, true, limits.maxBindingBytes);
      if (signature(current) !== signature(file)) throw new TestRunStoreAdminError("preview_changed");
    }
  }
  const removed: Counts = { bindings: 0, files: 0, bytes: 0 };
  if (!custody.anchored && process.platform === "win32" && plan.candidates.length > 0) {
    if (confirmation.beforeCommit && confirmation.beforeCommit() !== true) throw new TestRunStoreAdminError("config_changed");
    custody.assertCurrent();
    const owner = captureNativeEvidence(path.join(custody.pathFor(".store.lock"), "owner.json"), limits.maxBindingBytes);
    const metadata = plan.candidates.flatMap(candidate => [...candidate.files].sort((a, b) => Number(a.name.endsWith(".json")) - Number(b.name.endsWith(".json"))).map(file => file.nativeEvidence!));
    if (metadata.some(file => !file)) throw new TestRunStoreAdminError("preview_changed");
    for (const candidate of plan.candidates) {
      checkDeadline(deadlineUnixMs);
      if (candidateArtifactWitness(candidate) !== candidate.artifactWitness) throw new TestRunStoreAdminError("preview_changed");
    }
    runNativeStorageCommit({ operation: "cleanup", root: custody.root, root_dev: custody.identity.dev.toString(), root_ino: custody.identity.ino.toString(), lock: owner, metadata,
      artifacts: plan.candidates.flatMap(candidate => candidate.artifactEvidence ? [candidate.artifactEvidence] : []), ...(confirmation.nativeConfig ? { config: confirmation.nativeConfig } : {}) }, deadlineUnixMs);
    custody.assertCurrent();
    return { ...statusFromFiles(scanFiles(custody, deadlineUnixMs), limits), removed: countCandidates(plan.candidates), preview_fingerprint: confirmation.previewFingerprint };
  }
  for (const candidate of plan.candidates) {
    checkDeadline(deadlineUnixMs);
    if (confirmation.beforeCommit && confirmation.beforeCommit() !== true) throw new TestRunStoreAdminError("config_changed");
    custody.assertCurrent();
    if (candidateArtifactWitness(candidate) !== candidate.artifactWitness) throw new TestRunStoreAdminError("preview_changed");
    // Delete the owner binding last. Interrupted records remain unknown, never proof of non-issuance.
    const ordered = [...candidate.files].sort((a, b) => Number(a.name.endsWith(".json")) - Number(b.name.endsWith(".json")));
    for (const file of ordered) {
      checkDeadline(deadlineUnixMs);
      const current = captureFile(custody, file.name, true, limits.maxBindingBytes);
      if (signature(current) !== signature(file)) throw new TestRunStoreAdminError("preview_changed");
      custody.assertCurrent();
      if (candidateArtifactWitness(candidate) !== candidate.artifactWitness) throw new TestRunStoreAdminError("preview_changed");
      durableUnlinkCustodiedFile(custody, file.name, file.stat, { ...file.stat, contentHash: file.hash! });
      removed.files++;
      removed.bytes += file.stat.size;
    }
    removed.bindings++;
  }
  return { ...statusFromFiles(scanFiles(custody, deadlineUnixMs), limits), removed, preview_fingerprint: confirmation.previewFingerprint };
}

function cleanupPlan(custody: StorageDirectoryCustody, limits: TestRunOperationStoreLimits, now: Date, deadline: number, fingerprintScope: string) {
  if (!Number.isFinite(now.getTime()) || now.getTime() < 0) throw new TestRunStoreAdminError("storage_unavailable");
  custody.assertCurrent();
  const files = scanFiles(custody, deadline);
  const groups = new Map<string, FileSnapshot[]>();
  const preserved = { active: 0, unknown: 0, invalid: 0, foreign: 0, batch_limit: 0 };
  const inventory: string[] = [];
  for (const file of files) {
    checkDeadline(deadline);
    const suffix = SUFFIXES.find(value => file.name.endsWith(value))!;
    const operationId = file.name.slice(0, -suffix.length);
    // Foreign metadata is included in capacity/fingerprint but is never interpreted or removed.
    if (!isTestRunOperationId(operationId)) { preserved.foreign++; inventory.push(signature(file)); continue; }
    let snapshot = file;
    if (file.stat.size > 0 && file.stat.size <= limits.maxBindingBytes) {
      const captured = captureFile(custody, file.name, true, limits.maxBindingBytes);
      // Do not retain whole history bodies in memory; only the bounded per-record validator needs them.
      snapshot = { file: captured.file, name: captured.name, stat: captured.stat, hash: captured.hash!, nativeEvidence: captured.nativeEvidence! };
    }
    inventory.push(signature(snapshot));
    const group = groups.get(operationId) ?? [];
    group.push(snapshot);
    groups.set(operationId, group);
  }
  const candidates: Candidate[] = [];
  for (const [operationId, group] of [...groups.entries()].sort(([a], [b]) => a.localeCompare(b))) {
    checkDeadline(deadline);
    const bySuffix = (suffix: string) => group.find(file => file.name === `${operationId}${suffix}`);
    try {
      const owner = bySuffix(".json");
      if (!owner) { preserved.unknown++; continue; }
      const binding = parseMetadata(custody, owner, limits.maxBindingBytes);
      assertTestRunOperationBinding(binding);
      if (binding.operation_id !== operationId || Date.parse(binding.created_at) > now.getTime()) throw new Error("invalid owner");
      const issued = bySuffix(".issued");
      const terminal = bySuffix(".terminal");
      // Reservations are bounded unknown, including a simultaneous .issued publication.
      if (bySuffix(".reserve") || !issued) { preserved.unknown++; continue; }
      const marker = parseMetadata(custody, issued, limits.maxBindingBytes);
      assertTestRunRequestIssuanceMarker(marker, binding);
      if (Date.parse(marker.issued_at) > now.getTime()) throw new Error("future issuance");
      if (candidates.length >= TEST_RUN_STORE_ADMIN_MAX_CANDIDATES) { preserved.batch_limit++; continue; }
      let witness: string | undefined;
      let artifactEvidence: NativeFileEvidence | undefined;
      if (terminal) {
        const receipt = parseMetadata(custody, terminal, limits.maxBindingBytes);
        assertTestRunTerminalNoExecutionReceipt(receipt, binding, marker);
        if (Date.parse(receipt.recorded_at) > now.getTime()) throw new Error("future receipt");
        // A refusal receipt cannot override evidence that the same run exists.
        // Unreadable/foreign/changing artifacts are uncertainty, not absence.
        witness = noExecutionArtifactWitness(binding);
        if (!witness) { preserved.unknown++; continue; }
      } else {
        try {
          custody.assertCurrent();
          const artifact = readBoundTestRunArtifactCleanupSnapshot(binding, operationId);
          custody.assertCurrent();
          if (["running", "execution_may_have_started", "awaiting_filter_revalidation_after_reload"].includes(String(artifact.data.status))) { preserved.active++; continue; }
          witness = closedArtifactWitness(artifact);
          if (!witness) { preserved.unknown++; continue; }
          artifactEvidence = artifact.cleanupEvidence;
        } catch { preserved.unknown++; continue; }
      }
      candidates.push({ operationId, files: group, binding, noExecutionReceipt: !!terminal, artifactWitness: witness!, ...(artifactEvidence ? { artifactEvidence } : {}) });
    } catch (error) {
      if (error instanceof TestRunStoreAdminError) throw error;
      preserved.invalid++;
    }
  }
  const previewBucket = Math.floor(now.getTime() / PREVIEW_WINDOW_MS);
  custody.assertCurrent();
  const directoryIdentity = [custody.identity.dev.toString(), custody.identity.ino.toString()];
  const previewFingerprint = hash(JSON.stringify({ contract: "sgg.test_run_store_cleanup.v1", dir: custody.root, directoryIdentity, limits, previewBucket, fingerprintScope, inventory, candidates: candidates.map(candidate => [candidate.operationId, candidate.artifactWitness ?? "no_execution_receipt"]) }));
  checkDeadline(deadline);
  const preview: TestRunStoreCleanupPreview = {
    ...statusFromFiles(files, limits),
    preview_fingerprint: previewFingerprint,
    preview_expires_at: new Date((previewBucket + 1) * PREVIEW_WINDOW_MS).toISOString(),
    candidates: countCandidates(candidates), preserved,
    scan: { files: files.length, max_files: TEST_RUN_STORE_ADMIN_MAX_FILES, max_candidates: TEST_RUN_STORE_ADMIN_MAX_CANDIDATES, complete: true },
    cleanup_evidence: "bound_authoritative_terminal_artifact_or_no_execution_receipt",
  };
  return { preview, candidates };
}

function scanFiles(custody: StorageDirectoryCustody, deadline: number): FileSnapshot[] {
  let entries = 0;
  const files: FileSnapshot[] = [];
  checkDeadline(deadline);
  custody.assertCurrent();
  const directory = fs.opendirSync(path.dirname(custody.pathFor(".store.lock")), { bufferSize: 32 });
  try {
    while (true) {
      checkDeadline(deadline); custody.assertCurrent();
      const entry = directory.readSync();
      if (!entry) break;
      if (++entries > TEST_RUN_STORE_ADMIN_MAX_FILES) throw new TestRunStoreAdminError("scan_limit_exceeded");
      if (entry.name.startsWith(".") || !SUFFIXES.some(suffix => entry.name.endsWith(suffix))) continue;
      if (!entry.isFile() || entry.isSymbolicLink()) throw new TestRunStoreAdminError("storage_unavailable");
      files.push(captureFile(custody, entry.name, false));
    }
  } finally { directory.closeSync(); }
  custody.assertCurrent(); checkDeadline(deadline);
  files.sort((a, b) => a.name.localeCompare(b.name));
  checkDeadline(deadline);
  return files;
}
function captureFile(custody: StorageDirectoryCustody, name: string, body: boolean, maxBytes = 16 * 1024): FileSnapshot {
  const file = custody.pathFor(name);
  const before = fs.lstatSync(file, { bigint: true });
  if (!before.isFile() || before.isSymbolicLink() || before.nlink !== 1n) throw new TestRunStoreAdminError("storage_unavailable");
  const stat: FileSnapshot["stat"] = { dev: before.dev, ino: before.ino, size: Number(before.size), nlink: Number(before.nlink), mtime: before.mtime, mtimeMs: Number(before.mtimeMs), mtimeNs: before.mtimeNs, ctimeNs: before.ctimeNs };
  if (!body) return { file, name: path.basename(file), stat };
  if (before.size <= 0n || before.size > BigInt(maxBytes)) throw new TestRunStoreAdminError("storage_unavailable");
  const handle = fs.openSync(file, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
  let content: Buffer;
  try {
    const opened = fs.fstatSync(handle, { bigint: true });
    if (!sameSnapshot(before, opened)) throw new TestRunStoreAdminError("preview_changed");
    content = Buffer.alloc(Number(before.size) + 1);
    let length = 0;
    while (length < content.length) {
      const read = fs.readSync(handle, content, length, content.length - length, null);
      if (read === 0) break;
      length += read;
    }
    custody.assertCurrent();
    if (length !== Number(before.size) || !sameSnapshot(opened, fs.fstatSync(handle, { bigint: true })) || !sameSnapshot(before, fs.lstatSync(file, { bigint: true }))) throw new TestRunStoreAdminError("preview_changed");
    content = content.subarray(0, length);
  } finally { fs.closeSync(handle); }
  custody.assertCurrent();
  const contentHash = hash(content);
  return { file, name: path.basename(file), stat, body: content, hash: contentHash, nativeEvidence: { path: path.join(custody.root, name), dev: before.dev.toString(), ino: before.ino.toString(), size: before.size.toString(), mtime_ns: before.mtimeNs.toString(), ctime_ns: before.ctimeNs.toString(), content_hash: contentHash } };
}
function captureNativeEvidence(file: string, maximum: number): NativeFileEvidence {
  const before = fs.lstatSync(file, { bigint: true });
  if (!before.isFile() || before.isSymbolicLink() || before.nlink !== 1n || before.size < 1n || before.size > BigInt(maximum)) throw new TestRunStoreAdminError("preview_changed");
  const fd = fs.openSync(file, fs.constants.O_RDONLY | (fs.constants.O_NOFOLLOW ?? 0));
  try {
    if (!sameSnapshot(before, fs.fstatSync(fd, { bigint: true }))) throw new TestRunStoreAdminError("preview_changed");
    const bytes = Buffer.alloc(Number(before.size) + 1); let length = 0;
    while (length < bytes.length) { const read = fs.readSync(fd, bytes, length, bytes.length - length, null); if (!read) break; length += read; }
    if (length !== Number(before.size) || !sameSnapshot(before, fs.fstatSync(fd, { bigint: true })) || !sameSnapshot(before, fs.lstatSync(file, { bigint: true }))) throw new TestRunStoreAdminError("preview_changed");
    return { path: file, dev: before.dev.toString(), ino: before.ino.toString(), size: before.size.toString(), mtime_ns: before.mtimeNs.toString(), ctime_ns: before.ctimeNs.toString(), content_hash: hash(bytes.subarray(0, length)) };
  } finally { fs.closeSync(fd); }
}
function sameSnapshot(a: BigIntStats, b: BigIntStats): boolean {
  return b.isFile() && !b.isSymbolicLink() && b.nlink === 1n && sameFileObjectIdentity(a, b, process.platform) && a.size === b.size && a.mtimeNs === b.mtimeNs && a.ctimeNs === b.ctimeNs;
}
function parseMetadata(custody: StorageDirectoryCustody, file: FileSnapshot, maxBytes: number): unknown {
  if (!file.hash) throw new Error("metadata unavailable");
  const captured = captureFile(custody, file.name, true, maxBytes);
  if (signature(captured) !== signature(file)) throw new TestRunStoreAdminError("preview_changed");
  const source = captured.body!.toString("utf8");
  const value: unknown = JSON.parse(source);
  const keys: Set<string>[] = [];
  visit(source, {
    onObjectBegin: () => { keys.push(new Set()); },
    onObjectProperty: name => { if (keys.at(-1)!.has(name)) throw new Error("duplicate metadata member"); keys.at(-1)!.add(name); },
    onObjectEnd: () => { keys.pop(); },
  });
  return value;
}
function signature(file: FileSnapshot): string {
  return JSON.stringify([file.name, file.stat.dev.toString(), file.stat.ino.toString(), file.stat.size, file.stat.mtimeNs.toString(), file.stat.ctimeNs.toString(), file.stat.nlink, file.hash ?? ""]);
}
function hash(value: string | Buffer): string { return `sha256:${createHash("sha256").update(value).digest("hex")}`; }
function closedArtifactWitness(snapshot: ReturnType<typeof readBoundTestRunArtifactCleanupSnapshot>): string | undefined {
  const data = snapshot.data;
  if (data.artifact_path_advanced_during_read === true || data.artifact_ready !== true || data.artifact_persisted !== true || !hasAuthoritativeClosedTestRunOutcome(data)) return undefined;
  return snapshot.cleanupWitness;
}
function artifactWitness(binding: TestRunOperationBinding): string | undefined {
  try { return closedArtifactWitness(readBoundTestRunArtifactCleanupSnapshot(binding, binding.operation_id)); } catch { return undefined; }
}
function noExecutionArtifactWitness(binding: TestRunOperationBinding): string | undefined {
  try { readBoundTestRunArtifactCleanupSnapshot(binding, binding.operation_id); }
  catch (error) {
    if (error instanceof ProjectArtifactReadError && error.reason === "artifact_not_found") return "no_execution_receipt_no_artifact";
  }
  return undefined;
}
function candidateArtifactWitness(candidate: Candidate): string | undefined {
  return candidate.noExecutionReceipt ? noExecutionArtifactWitness(candidate.binding) : artifactWitness(candidate.binding);
}
function checkDeadline(deadline: number): void { if (Date.now() >= deadline) throw new TestRunStoreAdminError("deadline_exceeded"); }
function countCandidates(candidates: Candidate[]): Counts {
  return { bindings: candidates.length, files: candidates.reduce((sum, candidate) => sum + candidate.files.length, 0), bytes: candidates.reduce((sum, candidate) => sum + candidate.files.reduce((bytes, file) => bytes + file.stat.size, 0), 0) };
}
function statusFromFiles(files: FileSnapshot[], limits: TestRunOperationStoreLimits): TestRunStoreAdminStatus {
  const bytes = files.reduce((sum, file) => sum + file.stat.size, 0);
  const bindings = files.filter(file => file.name.endsWith(".json")).length;
  const usage = { bytes_ratio: bytes / limits.maxBytes, bindings_ratio: bindings / limits.maxBindings };
  const ratio = Math.max(usage.bytes_ratio, usage.bindings_ratio);
  return { bytes, bindings, max_bytes: limits.maxBytes, max_bindings: limits.maxBindings, retention_days: limits.retentionMs / 86_400_000,
    capacity_state: ratio >= 1 ? "capacity_exhausted" : ratio >= 0.9 ? "near_capacity" : "ok", usage, scope: SCOPE, settings_source: "saved_config_restart_required",
    defaults: { max_bytes: TEST_RUN_STORAGE_DEFAULT_MAX_BYTES, max_bindings: TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS, retention_days: TEST_RUN_STORAGE_DEFAULT_RETENTION_DAYS } };
}
