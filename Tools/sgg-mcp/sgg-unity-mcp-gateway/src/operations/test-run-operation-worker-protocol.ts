import path from "node:path";

import { absoluteStoragePath } from "../security/safe-storage.js";
import {
  assertTestRunOperationBinding, assertTestRunRequestIssuanceMarker, assertTestRunTerminalNoExecutionEvidence,
  assertTestRunTerminalNoExecutionReceipt, resolveTestRunOperationStoreLimits, testRunOperationBindingHash,
  TEST_RUN_STORAGE_DIAGNOSTICS_CONTRACT, TestRunOperationStoreError,
  type TestRunOperationBinding, type TestRunOperationStoreDiagnostics, type TestRunOperationStoreLimits,
  type TestRunOperationStoreStatus, type TestRunRequestIssuanceMarker, type TestRunTerminalNoExecutionEvidence,
  type TestRunTerminalNoExecutionReceipt, type TestRunPublicationAbortDiagnostics,
} from "./test-run-operation-store.js";

export const TEST_RUN_OPERATION_WORKER_ROLE = "sgg.test_run_operation_store";
export const TEST_RUN_OPERATION_WORKER_PROTOCOL = 1;
export const TEST_RUN_OPERATION_WORKER_GUARD_TIMEOUT_MS = 5_000;
export const TEST_RUN_OPERATION_WORKER_MAX_MESSAGE_BYTES = 4 * 1024 * 1024;

export type TestRunOperationWorkerData = {
  role: typeof TEST_RUN_OPERATION_WORKER_ROLE;
  protocol: 1;
  nonce: string;
  dir: string;
  limits: TestRunOperationStoreLimits;
};
export type TestRunOperationWorkerPhase = "prepare" | "issuance" | "terminal";
type JobBase = { protocol: 1; nonce: string; job_id: number; owner_binding_hash: string; binding: TestRunOperationBinding };
export type TestRunOperationWorkerJob = JobBase & (
  | { phase: "prepare"; deadline_unix_ms: number; guard: SharedArrayBuffer }
  | { phase: "issuance"; issued_at: string }
  | { phase: "terminal"; marker: TestRunRequestIssuanceMarker; evidence: TestRunTerminalNoExecutionEvidence }
);
export type TestRunOperationWorkerClose = { protocol: 1; nonce: string; type: "close" };
export type TestRunOperationWorkerFailure = { reason: TestRunOperationStoreError["reason"]; diagnostics?: TestRunOperationStoreDiagnostics; publication?: TestRunPublicationAbortDiagnostics };
type ResultBase = { protocol: 1; nonce: string; type: "result"; job_id: number; owner_binding_hash: string; phase: TestRunOperationWorkerPhase; status: TestRunOperationStoreStatus };
export type TestRunOperationWorkerResult = ResultBase & (
  | { outcome: "ok"; value: null | TestRunRequestIssuanceMarker | TestRunTerminalNoExecutionReceipt }
  | { outcome: "error"; error: TestRunOperationWorkerFailure }
);
export type TestRunOperationWorkerMessage =
  | { protocol: 1; nonce: string; type: "ready"; status: TestRunOperationStoreStatus }
  | { protocol: 1; nonce: string; type: "before_commit"; job_id: number; owner_binding_hash: string; guard_deadline_unix_ms: number }
  | TestRunOperationWorkerResult;

const UUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[1-8][0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;
const HASH = /^sha256:[0-9a-f]{64}$/;
const REASONS = ["binding_invalid", "binding_exists", "issuance_invalid", "terminal_outcome_invalid", "capacity_exhausted", "publication_aborted", "storage_unavailable"];
const JOB_KEYS = ["protocol", "nonce", "job_id", "owner_binding_hash", "binding", "phase"];
const RESULT_KEYS = ["protocol", "nonce", "type", "job_id", "owner_binding_hash", "phase", "status", "outcome"];

export function assertTestRunOperationWorkerData(value: unknown): asserts value is TestRunOperationWorkerData {
  exact(value, ["role", "protocol", "nonce", "dir", "limits"]);
  envelope(value);
  if (value.role !== TEST_RUN_OPERATION_WORKER_ROLE || typeof value.dir !== "string" || value.dir.length > 4096
    || value.dir.includes("\0") || !path.isAbsolute(value.dir) || absoluteStoragePath(value.dir) !== value.dir
    || (value.dir !== path.parse(value.dir).root && value.dir.endsWith(path.sep))
    || (process.platform === "win32" && path.parse(value.dir).root === path.sep)) invalid();
  assertTestRunOperationWorkerLimits(value.limits);
}

export function assertTestRunOperationWorkerLimits(value: unknown): asserts value is TestRunOperationStoreLimits {
  exact(value, ["maxBindingBytes", "maxBindings", "maxBytes", "retentionMs"]);
  for (const key of Object.keys(value)) if (typeof value[key] !== "number") invalid();
  try { resolveTestRunOperationStoreLimits(value as TestRunOperationStoreLimits); } catch { invalid(); }
}

export function assertTestRunOperationWorkerJob(value: unknown, data: TestRunOperationWorkerData): asserts value is TestRunOperationWorkerJob {
  record(value);
  const extra = value.phase === "prepare" ? ["deadline_unix_ms", "guard"]
    : value.phase === "issuance" ? ["issued_at"] : value.phase === "terminal" ? ["marker", "evidence"] : invalid();
  exact(value, [...JOB_KEYS, ...extra]);
  envelope(value, data.nonce);
  if (!jobId(value.job_id) || !hash(value.owner_binding_hash)) invalid();
  assertTestRunOperationBinding(value.binding);
  if (testRunOperationBindingHash(value.binding) !== value.owner_binding_hash
    || Buffer.byteLength(`${JSON.stringify(value.binding, null, 2)}\n`) > data.limits.maxBindingBytes) invalid();
  if (value.phase === "prepare") {
    if (!integer(value.deadline_unix_ms) || value.deadline_unix_ms > 8_640_000_000_000_000
      || !(value.guard instanceof SharedArrayBuffer) || value.guard.byteLength !== 4) invalid();
  } else if (value.phase === "issuance") {
    if (!iso(value.issued_at) || Date.parse(value.issued_at) < Date.parse(value.binding.created_at)) invalid();
  } else {
    assertTestRunRequestIssuanceMarker(value.marker, value.binding);
    assertTerminalEvidence(value.evidence);
  }
  boundedMessage(value);
}

export function assertTestRunOperationWorkerClose(value: unknown, nonce: string): asserts value is TestRunOperationWorkerClose {
  exact(value, ["protocol", "nonce", "type"]);
  envelope(value, nonce);
  if (value.type !== "close") invalid();
}

export function assertTestRunOperationWorkerMessage(value: unknown, nonce: string): asserts value is TestRunOperationWorkerMessage {
  record(value);
  envelope(value, nonce);
  if (value.type === "ready") {
    exact(value, ["protocol", "nonce", "type", "status"]);
    assertTestRunOperationWorkerStatus(value.status);
  } else if (value.type === "before_commit") {
    exact(value, ["protocol", "nonce", "type", "job_id", "owner_binding_hash", "guard_deadline_unix_ms"]);
    if (!jobId(value.job_id) || !hash(value.owner_binding_hash) || !integer(value.guard_deadline_unix_ms)
      || value.guard_deadline_unix_ms > 8_640_000_000_000_000) invalid();
  } else if (value.type === "result") {
    if (!jobId(value.job_id) || !hash(value.owner_binding_hash) || !oneOf(value.phase, ["prepare", "issuance", "terminal"])) invalid();
    assertTestRunOperationWorkerStatus(value.status);
    if (value.outcome === "ok") {
      exact(value, [...RESULT_KEYS, "value"]);
      if (value.phase === "prepare") { if (value.value !== null) invalid(); }
      else if (value.phase === "issuance") assertTestRunRequestIssuanceMarker(value.value);
      else {
        assertTestRunTerminalNoExecutionReceipt(value.value);
        assertTerminalEvidence({ bridge_error_code: value.value.bridge_error_code, reason: value.value.reason, package_update: value.value.package_update });
      }
    } else if (value.outcome === "error") {
      exact(value, [...RESULT_KEYS, "error"]);
      assertFailure(value.error);
      if (value.phase !== "prepare" && Object.hasOwn(value.error, "publication")) invalid();
    } else invalid();
  } else invalid();
  boundedMessage(value);
}

export function assertTestRunOperationWorkerStatus(value: unknown): asserts value is TestRunOperationStoreStatus {
  optionalKeys(value, ["schema_version", "state", "bindings", "bytes"], ["lock_recovery", "last_failure"]);
  if (value.schema_version !== 1 || !oneOf(value.state, ["ok", "unavailable", "capacity_exhausted"])
    || !integer(value.bindings) || !integer(value.bytes)) invalid();
  if (Object.hasOwn(value, "lock_recovery")) {
    exact(value.lock_recovery, ["schema_version", "owner_generation_state", "automatic_recovery"]);
    if (value.lock_recovery.schema_version !== 1 || !oneOf(value.lock_recovery.owner_generation_state, ["none", "live", "dead", "replaced", "uncertain"])
      || !oneOf(value.lock_recovery.automatic_recovery, ["not_needed", "reclaimed_dead_owner", "reclaimed_replaced_owner", "resumed_interrupted_transition"])) invalid();
  }
  if (Object.hasOwn(value, "last_failure")) assertDiagnostics(value.last_failure);
}

export function workerFailure(error: unknown): TestRunOperationWorkerFailure {
  if (!(error instanceof TestRunOperationStoreError)) return { reason: "storage_unavailable" };
  try {
    const result = { reason: error.reason, ...(error.diagnostics ? { diagnostics: structuredClone(error.diagnostics) } : {}),
      ...(error.publication ? { publication: structuredClone(error.publication) } : {}) };
    assertFailure(result);
    return result;
  } catch { return { reason: "storage_unavailable" }; }
}

export function assertTerminalEvidence(value: unknown): asserts value is TestRunTerminalNoExecutionEvidence {
  // The durable validator's enum lookups use String(); the wire must not accept coercible objects.
  record(value);
  record(value.package_update);
  record(value.package_update.local_package_code_inventory);
  if (typeof value.reason !== "string" || typeof value.package_update.status !== "string"
    || typeof value.package_update.local_package_code_inventory.status !== "string"
    || typeof value.package_update.local_package_code_inventory.failure_reason !== "string") throw new TestRunOperationStoreError("terminal_outcome_invalid");
  assertTestRunTerminalNoExecutionEvidence(value);
}

function assertFailure(value: unknown): asserts value is TestRunOperationWorkerFailure {
  optionalKeys(value, ["reason"], ["diagnostics", "publication"]);
  if (!oneOf(value.reason, REASONS)) invalid();
  if (Object.hasOwn(value, "diagnostics")) assertDiagnostics(value.diagnostics);
  if (Object.hasOwn(value, "publication")) {
    if (value.reason !== "publication_aborted") invalid();
    assertTestRunPublicationAbortDiagnostics(value.publication);
  }
}

export function assertTestRunPublicationAbortDiagnostics(value: unknown): asserts value is TestRunPublicationAbortDiagnostics {
  exact(value, ["schema_version", "contract", "stage", "reason", "guard_budget_ms"]);
  if (value.schema_version !== 1 || value.contract !== "sgg.unity_mcp.test_run_publication_abort.v1"
    || !oneOf(value.stage, ["prepare_admission", "prepare_queue", "worker_precommit"])
    || !oneOf(value.reason, ["command_deadline_elapsed", "publisher_closed", "guard_refused_or_failed", "guard_handshake_timeout"])
    || !integer(value.guard_budget_ms) || value.guard_budget_ms > TEST_RUN_OPERATION_WORKER_GUARD_TIMEOUT_MS
    || (value.stage === "prepare_admission" && (value.guard_budget_ms !== 0 || value.reason !== "command_deadline_elapsed"))
    || (value.stage === "prepare_queue" && (value.guard_budget_ms !== 0 || !oneOf(value.reason, ["command_deadline_elapsed", "publisher_closed"])))
    || (value.stage === "worker_precommit" && (value.reason === "publisher_closed"
      || value.reason === "guard_handshake_timeout" && value.guard_budget_ms === 0))) invalid();
}

export function publicationAbort(stage: TestRunPublicationAbortDiagnostics["stage"], reason: TestRunPublicationAbortDiagnostics["reason"], guardBudgetMs = 0): TestRunOperationStoreError {
  const publication: TestRunPublicationAbortDiagnostics = { schema_version: 1, contract: "sgg.unity_mcp.test_run_publication_abort.v1", stage, reason, guard_budget_ms: guardBudgetMs };
  assertTestRunPublicationAbortDiagnostics(publication);
  return new TestRunOperationStoreError("publication_aborted", undefined, publication);
}
function assertDiagnostics(value: unknown): asserts value is TestRunOperationStoreDiagnostics {
  exact(value, ["schema_version", "contract", "stage", "category", "owner_generation_state", "automatic_recovery", "retryable", "recovery_hint", "elapsed_ms"]);
  if (value.schema_version !== 1 || value.contract !== TEST_RUN_STORAGE_DIAGNOSTICS_CONTRACT
    || !oneOf(value.stage, ["lock_acquisition", "retained_store_maintenance", "issuance_reservation_publication", "binding_publication", "request_issuance_publication", "terminal_outcome_publication", "lock_release"])
    || !oneOf(value.category, ["lock_owner_live", "lock_owner_generation_uncertain", "transition_gate_live", "transition_gate_generation_uncertain", "lock_contention", "storage_io"])
    || !oneOf(value.owner_generation_state, ["none", "live", "dead", "replaced", "uncertain", "invalid"])
    || value.automatic_recovery !== "not_attempted" || typeof value.retryable !== "boolean"
    || !oneOf(value.recovery_hint, ["retry_after_owner_exit", "inspect_storage_owner_generation", "retry_after_transient_contention", "inspect_private_operation_store"])
    || !integer(value.elapsed_ms)) invalid();
}
function envelope(value: Record<string, unknown>, nonce?: string): void {
  if (value.protocol !== TEST_RUN_OPERATION_WORKER_PROTOCOL || !uuid(value.nonce) || (nonce !== undefined && value.nonce !== nonce)) invalid();
}
function record(value: unknown): asserts value is Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)
    || (Object.getPrototypeOf(value) !== Object.prototype && Object.getPrototypeOf(value) !== null)) invalid();
}
function exact(value: unknown, keys: readonly string[]): asserts value is Record<string, unknown> {
  record(value);
  if (Object.keys(value).length !== keys.length || Object.keys(value).some(key => !keys.includes(key))) invalid();
}
function optionalKeys(value: unknown, required: string[], optional: string[]): asserts value is Record<string, unknown> {
  record(value);
  if (required.some(key => !Object.hasOwn(value, key)) || Object.keys(value).some(key => !required.includes(key) && !optional.includes(key))) invalid();
}
function boundedMessage(value: unknown): void { if (Buffer.byteLength(JSON.stringify(value)) > TEST_RUN_OPERATION_WORKER_MAX_MESSAGE_BYTES) invalid(); }
function oneOf(value: unknown, allowed: readonly string[]): value is string { return typeof value === "string" && allowed.includes(value); }
function uuid(value: unknown): value is string { return typeof value === "string" && UUID.test(value); }
function hash(value: unknown): value is string { return typeof value === "string" && HASH.test(value); }
function integer(value: unknown): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0; }
function jobId(value: unknown): value is number { return integer(value) && value > 0; }
function iso(value: unknown): value is string { return typeof value === "string" && value.length <= 32 && Number.isFinite(Date.parse(value)) && new Date(value).toISOString() === value; }
function invalid(): never { throw new TestRunOperationStoreError("storage_unavailable"); }
