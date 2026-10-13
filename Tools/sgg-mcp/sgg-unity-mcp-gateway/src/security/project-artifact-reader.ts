import fs, { type BigIntStats } from "node:fs";
import { createHash } from "node:crypto";
import path from "node:path";
import { visit } from "jsonc-parser/lib/esm/main.js";

import { CanonicalPathService } from "../paths/canonical-path.js";
import { TEST_RUN_ARTIFACT_ROOT, TEST_RUN_ARTIFACT_SCHEMA_VERSION, assertTestRunOperationBinding, isTestRunOperationId, testRunCanonicalProjectRootHash, type TestRunOperationBinding } from "../operations/test-run-operation-store.js";
import { absoluteStoragePath, sameFileObjectIdentity } from "./safe-storage.js";

const NO_FOLLOW = fs.constants.O_NOFOLLOW ?? 0;
const HASH_PATTERN = /^sha256:[0-9a-f]{64}$/;
const DEFAULT_MAX_BYTES = 200_000;
const GATEWAY_OWNED_TEST_RUN_FIELDS = new Set([
  "artifact_ref", "artifact_refs", "next_valid_actions", "recovery_actions", "truncated", "response_mode", "readable_with",
  "bridge_request_issued", "command_bridge_request_issued", "test_run_bridge_request_issued", "test_run_request_issuance",
  "command_delivery_state", "delivery_state", "test_run_delivery_state", "status_or_artifact_check_required_before_repeat", "repeat_blocked_until_delivery_state_checked",
  "auto_retry_attempted", "automatic_replay_attempted", "automatic_replay_allowed", "command_replayed", "test_run_replayed", "write_command_repeated", "write_replay_allowed",
  "new_operation_id_allocated", "replacement_operation_allocated", "replacement_test_run_dispatched", "status_lookup", "candidate_status_lookups",
  "uncertain_operation_id_candidates", "all_candidate_statuses_must_be_checked_before_repeat", "explicit_repeat_required", "explicit_repeat_workflow", "repeat_request_template",
  "explicit_repeat_request_id_required", "explicit_repeat_request_id_recommended", "safe_to_auto_retry_on_single_current_candidate", "auto_retry_eligible", "retry_allowed",
  "repeatability_warning", "recovery_hint", "current_candidate_note", "safe_to_repeat_automatically", "test_run_recovery_contract", "batchmode_fallback",
  "operation_id_source", "operation_owner_verified", "owner_endpoint_contacted", "status_source", "command_dispatched",
  "status_observation",
  "observed_bridge_response", "publication_observation", "durable_issuance_state",
]);
const canonicalPaths = new CanonicalPathService();

export type ProjectArtifactFailureReason = "artifact_not_found" | "artifact_too_large" | "artifact_invalid" | "artifact_identity_mismatch" | "artifact_changed";
export type ProjectArtifactReadOptions = { maxBytes?: number; afterOpen?: (file: string) => void; afterRead?: (file: string) => void };

export class ProjectArtifactReadError extends Error {
  constructor(readonly reason: ProjectArtifactFailureReason) {
    super("Project artifact is unavailable");
    this.name = "ProjectArtifactReadError";
  }
}

export function readBoundTestRunArtifact(binding: TestRunOperationBinding, requestedOperationId: string, options: ProjectArtifactReadOptions = {}): Record<string, unknown> {
  return readBoundTestRunArtifactSnapshot(binding, requestedOperationId, options).data;
}

export type BoundTestRunArtifactSnapshot = { data: Record<string, unknown>; openedMtimeNs: bigint };
export type BoundTestRunArtifactCleanupSnapshot = BoundTestRunArtifactSnapshot & { cleanupWitness: string; cleanupEvidence: { path: string; dev: string; ino: string; size: string; mtime_ns: string; ctime_ns: string; content_hash: string } };

/** Destructive eligibility uses an exact stable, single-link object and strict JSON.
 * Ordinary status observation retains its existing atomic-publication semantics. */
export function readBoundTestRunArtifactCleanupSnapshot(binding: TestRunOperationBinding, requestedOperationId: string, options: ProjectArtifactReadOptions = {}): BoundTestRunArtifactCleanupSnapshot {
  const snapshot = readArtifactSnapshot(binding, requestedOperationId, options, true);
  if (typeof snapshot.cleanupWitness !== "string" || !snapshot.cleanupEvidence) throw new ProjectArtifactReadError("artifact_invalid");
  return snapshot as BoundTestRunArtifactCleanupSnapshot;
}

/** Metadata belongs to the stable validated opened handle, never the final path
 * (which may already name a newer atomically published artifact) or JSON. */
export function readBoundTestRunArtifactSnapshot(binding: TestRunOperationBinding, requestedOperationId: string, options: ProjectArtifactReadOptions = {}): BoundTestRunArtifactSnapshot {
  return readArtifactSnapshot(binding, requestedOperationId, options, false);
}

function readArtifactSnapshot(binding: TestRunOperationBinding, requestedOperationId: string, options: ProjectArtifactReadOptions, strictCleanupEvidence: boolean): BoundTestRunArtifactSnapshot & { cleanupWitness?: string; cleanupEvidence?: BoundTestRunArtifactCleanupSnapshot["cleanupEvidence"] } {
  let bindingSnapshot: string;
  try { assertTestRunOperationBinding(binding); }
  catch { throw new ProjectArtifactReadError("artifact_identity_mismatch"); }
  bindingSnapshot = JSON.stringify(binding);
  if (!isTestRunOperationId(requestedOperationId) || requestedOperationId !== binding.operation_id) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  const expectedArtifactPath = `${TEST_RUN_ARTIFACT_ROOT}/${requestedOperationId}.json`;
  if (binding.artifact_path !== expectedArtifactPath || binding.canonical_project_root_hash !== testRunCanonicalProjectRootHash(binding.canonical_project_root)) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  try {
    if (canonicalPaths.normalizeProjectRoot(binding.origin_project_root) !== binding.canonical_project_root) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  } catch (error) {
    if (error instanceof ProjectArtifactReadError) throw error;
    throw new ProjectArtifactReadError("artifact_identity_mismatch");
  }

  const projectRoot = absoluteStoragePath(binding.origin_project_root);
  const artifactRoot = path.join(projectRoot, TEST_RUN_ARTIFACT_ROOT);
  const file = path.join(artifactRoot, `${requestedOperationId}.json`);
  if (!isSameOrChild(artifactRoot, projectRoot) || !isSameOrChild(file, artifactRoot)) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  const maxBytes = positiveMax(options.maxBytes ?? DEFAULT_MAX_BYTES);
  let handle: number | undefined;
  try {
    assertNoSymbolicLinkInChain(projectRoot, artifactRoot);
    assertNoSymbolicLinkInChain(projectRoot, file);
    const before = lstatRequired(file);
    if (!before.isFile() || before.isSymbolicLink()) throw new ProjectArtifactReadError("artifact_identity_mismatch");
    if (strictCleanupEvidence && before.nlink !== 1n) throw new ProjectArtifactReadError("artifact_identity_mismatch");
    if (before.size <= 0n) throw new ProjectArtifactReadError("artifact_invalid");
    if (before.size > BigInt(maxBytes)) throw new ProjectArtifactReadError("artifact_too_large");
    handle = fs.openSync(file, fs.constants.O_RDONLY | NO_FOLLOW);
    const opened = fs.fstatSync(handle, { bigint: true });
    if (!opened.isFile() || !sameIdentity(before, opened)) throw new ProjectArtifactReadError("artifact_changed");
    if (strictCleanupEvidence && opened.nlink !== 1n) throw new ProjectArtifactReadError("artifact_identity_mismatch");
    options.afterOpen?.(file);
    const size = Number(opened.size);
    const buffer = Buffer.alloc(size);
    let offset = 0;
    while (offset < size) {
      const read = fs.readSync(handle, buffer, offset, size - offset, offset);
      if (read <= 0) break;
      offset += read;
    }
    options.afterRead?.(file);
    const finalOpened = fs.fstatSync(handle, { bigint: true });
    if (!strictCleanupEvidence) { fs.closeSync(handle); handle = undefined; }
    const finalPath = lstatRequired(file);
    assertNoSymbolicLinkInChain(projectRoot, artifactRoot);
    assertNoSymbolicLinkInChain(projectRoot, file);
    if (!finalPath.isFile() || finalPath.isSymbolicLink()) throw new ProjectArtifactReadError("artifact_identity_mismatch");
    const artifactPathAdvancedDuringRead = !sameFileObject(opened, finalPath);
    const stableOpenedSnapshot = sameIdentity(opened, finalOpened);
    if (strictCleanupEvidence && (artifactPathAdvancedDuringRead || finalOpened.nlink !== 1n || finalPath.nlink !== 1n)) throw new ProjectArtifactReadError("artifact_changed");
    if (offset !== size || !stableOpenedSnapshot || (!artifactPathAdvancedDuringRead && !sameIdentity(opened, finalPath))) {
      throw new ProjectArtifactReadError("artifact_changed");
    }
    let parsed: unknown;
    try {
      const source = strictCleanupEvidence ? new TextDecoder("utf-8", { fatal: true }).decode(buffer) : buffer.toString("utf8");
      parsed = JSON.parse(source);
      if (strictCleanupEvidence) {
        const keys: Set<string>[] = [];
        visit(source, {
          onObjectBegin: () => { keys.push(new Set()); },
          onObjectProperty: name => { if (keys.at(-1)!.has(name)) throw new Error("Duplicate artifact member"); keys.at(-1)!.add(name); },
          onObjectEnd: () => { keys.pop(); },
        });
      }
    }
    catch { throw new ProjectArtifactReadError("artifact_invalid"); }
    const artifact = validateBoundTestRunArtifactData(binding, requestedOperationId, parsed);
    try { assertTestRunOperationBinding(binding); }
    catch { throw new ProjectArtifactReadError("artifact_identity_mismatch"); }
    if (JSON.stringify(binding) !== bindingSnapshot) throw new ProjectArtifactReadError("artifact_identity_mismatch");
    if (strictCleanupEvidence) {
      const held = fs.fstatSync(handle!, { bigint: true });
      const named = lstatRequired(file);
      if (held.nlink !== 1n || named.nlink !== 1n || !sameIdentity(opened, held) || !sameIdentity(opened, named)) throw new ProjectArtifactReadError("artifact_changed");
    }
    const cleanupEvidence = strictCleanupEvidence ? { path: file, dev: opened.dev.toString(), ino: opened.ino.toString(), size: opened.size.toString(), mtime_ns: opened.mtimeNs.toString(), ctime_ns: opened.ctimeNs.toString(), content_hash: `sha256:${createHash("sha256").update(buffer).digest("hex")}` } : undefined;
    const cleanupWitness = cleanupEvidence ? `sha256:${createHash("sha256").update(JSON.stringify([cleanupEvidence, opened.nlink.toString()])).digest("hex")}` : undefined;
    return { data: { ...artifact, artifact_path_advanced_during_read: artifactPathAdvancedDuringRead }, openedMtimeNs: opened.mtimeNs, ...(cleanupWitness && cleanupEvidence ? { cleanupWitness, cleanupEvidence } : {}) };
  } catch (error) {
    if (error instanceof ProjectArtifactReadError) throw error;
    if (isNotFound(error)) throw new ProjectArtifactReadError("artifact_not_found");
    throw new ProjectArtifactReadError("artifact_changed");
  } finally {
    if (handle !== undefined) {
      try { fs.closeSync(handle); } catch { }
    }
  }
}

export function validateBoundTestRunArtifactData(binding: TestRunOperationBinding, requestedOperationId: string, value: unknown): Record<string, unknown> {
  try { assertTestRunOperationBinding(binding); }
  catch { throw new ProjectArtifactReadError("artifact_identity_mismatch"); }
  const expectedArtifactPath = `${TEST_RUN_ARTIFACT_ROOT}/${requestedOperationId}.json`;
  if (!isTestRunOperationId(requestedOperationId)
    || requestedOperationId !== binding.operation_id
    || !isRecord(value)
    || value.command !== "test.run"
    || value.artifact_schema_version !== TEST_RUN_ARTIFACT_SCHEMA_VERSION
    || value.operation_id !== requestedOperationId
    || value.run_id !== requestedOperationId
    || value.artifact_path !== expectedArtifactPath
    || (value.artifact_ref !== undefined && value.artifact_ref !== expectedArtifactPath)
    || typeof value.args_hash !== "string"
    || !HASH_PATTERN.test(value.args_hash)
    || value.caller_args_hash !== binding.caller_args_hash
    || value.package_version !== binding.package_version) {
    throw new ProjectArtifactReadError("artifact_identity_mismatch");
  }
  return { ...withoutGatewayOwnedTestRunFields(value), artifact_path: expectedArtifactPath, artifact_ref: expectedArtifactPath };
}

export function validateBoundLiveTestRunStatusData(binding: TestRunOperationBinding, requestedOperationId: string, value: unknown): Record<string, unknown> {
  try { assertTestRunOperationBinding(binding); }
  catch { throw new ProjectArtifactReadError("artifact_identity_mismatch"); }
  if (!isTestRunOperationId(requestedOperationId) || requestedOperationId !== binding.operation_id) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  const expectedArtifactPath = `${TEST_RUN_ARTIFACT_ROOT}/${requestedOperationId}.json`;
  if (isRecord(value) && value.status === "artifact_too_large") {
    const expectedKeys = new Set(["operation_id", "run_id", "status", "success", "result", "artifact_path", "message"]);
    if (Object.keys(value).length !== expectedKeys.size
      || Object.keys(value).some((key) => !expectedKeys.has(key))
      || value.operation_id !== requestedOperationId
      || value.run_id !== requestedOperationId
      || value.success !== false
      || value.result !== "artifact_too_large"
      || value.artifact_path !== expectedArtifactPath
      || typeof value.message !== "string"
      || value.message.length === 0
      || value.message.length > 1_024) {
      throw new ProjectArtifactReadError("artifact_identity_mismatch");
    }
    return {
      command: "test.run",
      operation_id: requestedOperationId,
      run_id: requestedOperationId,
      status: "artifact_too_large",
      success: false,
      result: "artifact_too_large",
      artifact_path: expectedArtifactPath,
      artifact_ref: expectedArtifactPath,
      message: "Persisted test.run artifact exceeds the Unity status read limit",
    };
  }
  return validateBoundTestRunArtifactData(binding, requestedOperationId, value);
}

function assertNoSymbolicLinkInChain(root: string, candidate: string): void {
  if (!isSameOrChild(candidate, root)) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  const rootStat = lstatRequired(root);
  if (!rootStat.isDirectory() || rootStat.isSymbolicLink()) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  let current = root;
  const relative = path.relative(root, candidate);
  for (const segment of relative.split(path.sep).filter(Boolean)) {
    current = path.join(current, segment);
    let stat: BigIntStats;
    try { stat = fs.lstatSync(current, { bigint: true }); }
    catch (error) { if (isNotFound(error)) throw new ProjectArtifactReadError("artifact_not_found"); throw error; }
    if (stat.isSymbolicLink()) throw new ProjectArtifactReadError("artifact_identity_mismatch");
    if (current !== candidate && !stat.isDirectory()) throw new ProjectArtifactReadError("artifact_identity_mismatch");
  }
}

function lstatRequired(file: string): BigIntStats {
  try { return fs.lstatSync(file, { bigint: true }); }
  catch (error) { if (isNotFound(error)) throw new ProjectArtifactReadError("artifact_not_found"); throw error; }
}

function sameIdentity(left: BigIntStats, right: BigIntStats): boolean {
  return sameFileObject(left, right) && left.size === right.size && left.mtimeNs === right.mtimeNs && left.ctimeNs === right.ctimeNs;
}

function sameFileObject(left: BigIntStats, right: BigIntStats): boolean {
  return sameFileObjectIdentity(left, right, process.platform);
}

function isSameOrChild(candidate: string, root: string): boolean { const relative = path.relative(root, candidate); return relative === "" || (!relative.startsWith(`..${path.sep}`) && relative !== ".." && !path.isAbsolute(relative)); }
function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
function withoutGatewayOwnedTestRunFields(value: Record<string, unknown>): Record<string, unknown> {
  return sanitizeRecord(value, 0);
}
function sanitizeRecord(value: Record<string, unknown>, depth: number): Record<string, unknown> {
  if (depth >= 32) return {};
  return Object.fromEntries(Object.entries(value).flatMap(([key, child]) => {
    if (isGatewayOwnedTestRunField(key)) return [];
    return [[key, sanitizeValue(child, depth + 1)]];
  }));
}
function sanitizeValue(value: unknown, depth: number): unknown {
  if (depth >= 32) return null;
  if (Array.isArray(value)) return value.map((item) => sanitizeValue(item, depth + 1));
  return isRecord(value) ? sanitizeRecord(value, depth) : value;
}
function isGatewayOwnedTestRunField(key: string): boolean {
  return GATEWAY_OWNED_TEST_RUN_FIELDS.has(key)
    || key.includes("bridge_request")
    || key.includes("delivery_state")
    || key.includes("request_issuance")
    || key.includes("repeat")
    || key.includes("replay")
    || key.includes("retry")
    || key === "action"
    || key.endsWith("_action")
    || key.endsWith("_actions");
}
function isNotFound(error: unknown): boolean { return typeof error === "object" && error !== null && "code" in error && error.code === "ENOENT"; }
function positiveMax(value: number): number { if (!Number.isSafeInteger(value) || value <= 0 || value > 4 * 1024 * 1024) throw new TypeError("Invalid project artifact read limit"); return value; }
