import { createHash } from "node:crypto";
import path from "node:path";

import { CanonicalPathService } from "../paths/canonical-path.js";
import { absoluteStoragePath, acquireStorageLock, acquireStorageAdminLock, openStorageDirectoryCustody, durableUnlinkFile, ensureSecureRoot, maintainDeleteTombstones, readSecureFile, secureFileStat, secureRegularFiles, StorageLockUnavailableError, writeNewFileDurable, type SecureFileStat, type StorageDirectoryCustody, type StorageLockRecovery } from "../security/safe-storage.js";
import { TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS, TEST_RUN_STORAGE_DEFAULT_MAX_BYTES, TEST_RUN_STORAGE_MAX_BINDINGS } from "./test-run-storage-settings.js";
import { cleanupTestRunStoreMetadata, inspectTestRunStoreMetadata, previewTestRunStoreCleanup, TEST_RUN_STORE_ADMIN_DEADLINE_MS, TestRunStoreAdminError } from "./test-run-store-admin.js";
import type { NativeFileEvidence } from "./test-run-storage-native.js";

export const TEST_RUN_OPERATION_OWNER_CONTRACT = "sgg.unity_mcp.test_run_operation_owner.v1";
export const TEST_RUN_REQUEST_ISSUANCE_CONTRACT = "sgg.unity_mcp.test_run_request_issuance.v1";
export const TEST_RUN_TERMINAL_NO_EXECUTION_CONTRACT = "sgg.unity_mcp.test_run_terminal_no_execution.v1";
export const TEST_RUN_STORAGE_DIAGNOSTICS_CONTRACT = "sgg.unity_mcp.test_run_storage_diagnostics.v1";
export const TEST_RUN_ARTIFACT_CAPABILITY = "test_run_artifact_v3";
export const TEST_RUN_ARTIFACT_SCHEMA_VERSION = 3;
export const TEST_RUN_ARTIFACT_ROOT = "Library/SGGUnityMcp/job-artifacts/test-runs";

const OPERATION_ID_PATTERN = /^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/;
const HASH_PATTERN = /^sha256:[0-9a-f]{64}$/;
const ROOT_HASH_DOMAIN = Buffer.from("sgg.unity_mcp.test_run_operation_owner.v1\0", "utf8");
export type TestRunOperationStoreLimits = Readonly<{ maxBindingBytes: number; maxBindings: number; maxBytes: number; retentionMs: number }>;
const DEFAULT_LIMITS: TestRunOperationStoreLimits = Object.freeze({ maxBindingBytes: 16 * 1024, maxBindings: TEST_RUN_STORAGE_DEFAULT_MAX_BINDINGS, maxBytes: TEST_RUN_STORAGE_DEFAULT_MAX_BYTES, retentionMs: 30 * 86_400_000 });
const canonicalPaths = new CanonicalPathService();

export type TestRunOperationBinding = {
  schema_version: 1;
  contract: typeof TEST_RUN_OPERATION_OWNER_CONTRACT;
  command: "test.run";
  operation_id: string;
  project: string;
  original_project_selector: string;
  origin_project_root: string;
  canonical_project_root: string;
  canonical_project_root_hash: string;
  origin_instance_id: string;
  origin_unity_pid: number;
  origin_unity_process_start_utc: string;
  caller_args_hash: string;
  artifact_path: string;
  artifact_schema_version: typeof TEST_RUN_ARTIFACT_SCHEMA_VERSION;
  package_id: string;
  package_version: string;
  commands_hash: string;
  catalog_generation: number;
  artifact_schema_capability: typeof TEST_RUN_ARTIFACT_CAPABILITY;
  created_at: string;
};

export type TestRunOperationLookup = { status: "found"; binding: TestRunOperationBinding } | { status: "not_found" };
export type TestRunRequestIssuanceMarker = {
  schema_version: 1;
  contract: typeof TEST_RUN_REQUEST_ISSUANCE_CONTRACT;
  command: "test.run";
  state: "issued";
  operation_id: string;
  owner_binding_hash: string;
  project: string;
  canonical_project_root_hash: string;
  origin_instance_id: string;
  origin_unity_pid: number;
  origin_unity_process_start_utc: string;
  caller_args_hash: string;
  artifact_path: string;
  artifact_schema_version: typeof TEST_RUN_ARTIFACT_SCHEMA_VERSION;
  package_id: string;
  package_version: string;
  commands_hash: string;
  catalog_generation: number;
  artifact_schema_capability: typeof TEST_RUN_ARTIFACT_CAPABILITY;
  issued_at: string;
};
export type TestRunRequestIssuanceLookup = { status: "found"; marker: TestRunRequestIssuanceMarker } | { status: "reserved" } | { status: "not_found" };
export type TestRunTerminalNoExecutionReason = keyof typeof TEST_RUN_TERMINAL_NO_EXECUTION_CODES;
export type TestRunTerminalPackageInventoryEvidence = {
  status: "current" | "stale" | "unavailable";
  failure_reason: "" | "not_scanned" | "snapshot_unavailable" | "input_unavailable" | "package_limit_exceeded" | "asset_path_limit_exceeded" | "package_identity_invalid" | "resolved_path_unavailable" | "path_security_rejected" | "budget_exhausted" | "directory_limit_exceeded" | "file_limit_exceeded" | "duplicate_asset_path" | "compiler_source_limit_exceeded" | "compiler_source_read_exception" | "read_exception";
  mismatch_package: string;
  local_package_count: number;
  source_file_count: number;
  asset_database_file_count: number;
  missing_asset_file_count: number;
  unexpected_asset_file_count: number;
  compiler_source_file_count?: number;
  missing_compiler_source_file_count?: number;
  unexpected_compiler_source_file_count?: number;
  source_fingerprint: string;
  asset_database_fingerprint: string;
  source_state_fingerprint: string;
  compiler_source_fingerprint?: string;
};
export type TestRunTerminalPackageUpdateEvidence = {
  status: "current" | "unavailable" | "package_refresh_required" | "stale_package_resolution";
  refresh_pending: boolean;
  package_refresh_required: boolean;
  package_refresh_wait_can_advance: boolean;
  stale_package_resolution: boolean;
  fingerprint: string;
  pending_fingerprint: string;
  last_transition_utc: string;
  local_package_code_inventory: TestRunTerminalPackageInventoryEvidence;
};
export type TestRunTerminalNoExecutionEvidence = {
  bridge_error_code: string;
  reason: TestRunTerminalNoExecutionReason;
  package_update: TestRunTerminalPackageUpdateEvidence;
};
export type TestRunTerminalNoExecutionReceipt = {
  schema_version: 1;
  contract: typeof TEST_RUN_TERMINAL_NO_EXECUTION_CONTRACT;
  command: "test.run";
  state: "not_executed";
  operation_created: false;
  command_dispatched: false;
  test_runner_execute_invoked: false;
  artifact_persisted: false;
  operation_id: string;
  owner_binding_hash: string;
  request_issuance_hash: string;
  issued_at: string;
  project: string;
  canonical_project_root_hash: string;
  origin_instance_id: string;
  origin_unity_pid: number;
  origin_unity_process_start_utc: string;
  caller_args_hash: string;
  expected_artifact_path: string;
  artifact_schema_version: typeof TEST_RUN_ARTIFACT_SCHEMA_VERSION;
  package_id: string;
  package_version: string;
  commands_hash: string;
  catalog_generation: number;
  artifact_schema_capability: typeof TEST_RUN_ARTIFACT_CAPABILITY;
  bridge_error_code: string;
  reason: TestRunTerminalNoExecutionReason;
  package_update: TestRunTerminalPackageUpdateEvidence;
  recorded_at: string;
};
export type TestRunTerminalNoExecutionLookup = { status: "found"; receipt: TestRunTerminalNoExecutionReceipt } | { status: "not_found" };
export type TestRunOperationStoreStage = "lock_acquisition" | "retained_store_maintenance" | "issuance_reservation_publication" | "binding_publication" | "request_issuance_publication" | "terminal_outcome_publication" | "lock_release";
export type TestRunOperationStoreDiagnostics = {
  schema_version: 1;
  contract: typeof TEST_RUN_STORAGE_DIAGNOSTICS_CONTRACT;
  stage: TestRunOperationStoreStage;
  category: "lock_owner_live" | "lock_owner_generation_uncertain" | "transition_gate_live" | "transition_gate_generation_uncertain" | "lock_contention" | "storage_io";
  owner_generation_state: "none" | "live" | "dead" | "replaced" | "uncertain" | "invalid";
  automatic_recovery: "not_attempted";
  retryable: boolean;
  recovery_hint: "retry_after_owner_exit" | "inspect_storage_owner_generation" | "retry_after_transient_contention" | "inspect_private_operation_store";
  elapsed_ms: number;
};
export type TestRunOperationStoreStatus = {
  schema_version: 1;
  state: "ok" | "unavailable" | "capacity_exhausted";
  bindings: number;
  bytes: number;
  lock_recovery?: StorageLockRecovery;
  last_failure?: TestRunOperationStoreDiagnostics;
};

export interface TestRunOperationStoreLike {
  readonly dir: string;
  readonly limits?: TestRunOperationStoreLimits;
  publish(binding: TestRunOperationBinding, options?: TestRunOperationPublishOptions): void;
  lookup(operationId: string, now?: Date): TestRunOperationLookup;
  publishRequestIssuance(binding: TestRunOperationBinding, issuedAt?: Date): TestRunRequestIssuanceMarker;
  lookupRequestIssuance(binding: TestRunOperationBinding, now?: Date): TestRunRequestIssuanceLookup;
  publishTerminalNoExecution(binding: TestRunOperationBinding, marker: TestRunRequestIssuanceMarker, evidence: TestRunTerminalNoExecutionEvidence): TestRunTerminalNoExecutionReceipt;
  lookupTerminalNoExecution(binding: TestRunOperationBinding, marker: TestRunRequestIssuanceMarker, now?: Date): TestRunTerminalNoExecutionLookup;
  snapshotStatus(): TestRunOperationStoreStatus;
}

export type TestRunOperationPublishOptions = {
  beforeCommit?: () => boolean;
};

export type TestRunOperationStoreOptions = {
  maxBindingBytes?: number;
  maxBindings?: number;
  maxBytes?: number;
  retentionMs?: number;
  now?: () => Date;
  adminFingerprintScope?: string;
};

export function resolveTestRunOperationStoreLimits(options: TestRunOperationStoreOptions = {}): TestRunOperationStoreLimits {
  return Object.freeze({
    maxBindingBytes: positiveLimit(options.maxBindingBytes, DEFAULT_LIMITS.maxBindingBytes, 1024 * 1024),
    maxBindings: positiveLimit(options.maxBindings, DEFAULT_LIMITS.maxBindings, TEST_RUN_STORAGE_MAX_BINDINGS),
    maxBytes: positiveLimit(options.maxBytes, DEFAULT_LIMITS.maxBytes, 1024 * 1024 * 1024),
    retentionMs: positiveLimit(options.retentionMs, DEFAULT_LIMITS.retentionMs, 366 * 86_400_000),
  });
}

export class TestRunOperationStoreError extends Error {
  constructor(readonly reason: "binding_invalid" | "binding_exists" | "issuance_invalid" | "terminal_outcome_invalid" | "capacity_exhausted" | "publication_aborted" | "storage_unavailable", readonly diagnostics?: TestRunOperationStoreDiagnostics,
    readonly publication?: TestRunPublicationAbortDiagnostics) {
    super("Test run operation owner store is unavailable");
    this.name = "TestRunOperationStoreError";
  }
}

/** Observation of a refused prepare, never proof about an issued TestRunner operation. */
export type TestRunPublicationAbortDiagnostics = {
  schema_version: 1;
  contract: "sgg.unity_mcp.test_run_publication_abort.v1";
  stage: "prepare_admission" | "prepare_queue" | "worker_precommit";
  reason: "command_deadline_elapsed" | "publisher_closed" | "guard_refused_or_failed" | "guard_handshake_timeout";
  guard_budget_ms: number;
};

export class TestRunOperationStore implements TestRunOperationStoreLike {
  readonly dir: string;
  readonly #limits: TestRunOperationStoreLimits;
  readonly #now: () => Date;
  readonly #adminFingerprintScope: string;
  #lastStatus: TestRunOperationStoreStatus = { schema_version: 1, state: "ok", bindings: 0, bytes: 0 };

  constructor(dir: string, options: TestRunOperationStoreOptions = {}) {
    this.dir = absoluteStoragePath(dir);
    this.#limits = resolveTestRunOperationStoreLimits(options);
    this.#now = options.now ?? (() => new Date());
    this.#adminFingerprintScope = options.adminFingerprintScope ?? "";
  }

  get limits(): TestRunOperationStoreLimits { return this.#limits; }

  publish(binding: TestRunOperationBinding, options: TestRunOperationPublishOptions = {}): void {
    const storageStartedAt = Date.now();
    let storageStage: TestRunOperationStoreStage = "lock_acquisition";
    try {
      assertTestRunOperationBinding(binding);
      const serialized = `${JSON.stringify(binding, null, 2)}\n`;
      const incomingBytes = Buffer.byteLength(serialized);
      const issuanceReservationBytes = testRunRequestIssuanceReservationBytes(binding);
      if (incomingBytes > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("binding_invalid");
      if (issuanceReservationBytes > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("issuance_invalid");
      this.withStoreLock(() => {
        storageStage = "retained_store_maintenance";
        const status = this.maintain(this.#now());
        const file = this.bindingFile(binding.operation_id);
        const reservationFile = this.issuanceReservationFile(binding.operation_id);
        if (secureFileStat(this.dir, file)) throw new TestRunOperationStoreError("binding_exists");
        if (status.bindings + 1 > this.#limits.maxBindings || status.bytes + incomingBytes + issuanceReservationBytes > this.#limits.maxBytes) {
          this.#lastStatus = { ...status, state: "capacity_exhausted" };
          throw new TestRunOperationStoreError("capacity_exhausted");
        }
        storageStage = "issuance_reservation_publication";
        writeNewFileDurable(this.dir, reservationFile, " ".repeat(issuanceReservationBytes));
        let bindingDurablyPublished = false;
        try {
          storageStage = "binding_publication";
          writeNewFileDurable(this.dir, file, serialized, {
            ...(options.beforeCommit ? { afterTempFsync: () => {
              if (!options.beforeCommit!()) throw new TestRunOperationStoreError("publication_aborted");
            } } : {}),
            afterPublish: () => { bindingDurablyPublished = true; },
          });
        } catch (error) {
          const published = secureFileStat(this.dir, file);
          let publicationCommitted = false;
          if (published && published.size > 0 && published.size <= this.#limits.maxBindingBytes) {
            try {
              const parsed = JSON.parse(readSecureFile(this.dir, file, this.#limits.maxBindingBytes).toString("utf8")) as unknown;
              assertTestRunOperationBinding(parsed);
              publicationCommitted = testRunOperationBindingHash(parsed) === testRunOperationBindingHash(binding);
            } catch { }
          }
          if (!bindingDurablyPublished || !publicationCommitted) {
            if (!published) {
              const reservation = secureFileStat(this.dir, reservationFile);
              if (reservation) durableUnlinkFile(this.dir, reservationFile, { expectedIdentity: reservation });
            }
            throw error;
          }
        }
        this.#lastStatus = { ...status, state: "ok", bindings: status.bindings + 1, bytes: status.bytes + incomingBytes + issuanceReservationBytes };
      }, () => { storageStage = "lock_release"; });
    } catch (error) {
      if (error instanceof TestRunOperationStoreError) throw error;
      throw this.storageUnavailable(storageStage, storageStartedAt, error);
    }
  }

  lookup(operationId: string, now: Date = this.#now()): TestRunOperationLookup {
    if (!OPERATION_ID_PATTERN.test(operationId)) throw new TestRunOperationStoreError("binding_invalid");
    try {
      const root = ensureSecureRoot(this.dir);
      const file = this.bindingFile(operationId);
      const stat = secureFileStat(root, file);
      if (!stat) return { status: "not_found" };
      if (stat.size <= 0 || stat.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("binding_invalid");
      const parsed = JSON.parse(readSecureFile(root, file, this.#limits.maxBindingBytes).toString("utf8")) as unknown;
      assertTestRunOperationBinding(parsed);
      if (parsed.operation_id !== operationId) throw new TestRunOperationStoreError("binding_invalid");
      if (now.getTime() - Date.parse(parsed.created_at) >= this.#limits.retentionMs) return { status: "not_found" };
      return { status: "found", binding: parsed };
    } catch (error) {
      if (error instanceof TestRunOperationStoreError) throw error;
      throw new TestRunOperationStoreError("storage_unavailable");
    }
  }

  publishRequestIssuance(binding: TestRunOperationBinding, issuedAt?: Date): TestRunRequestIssuanceMarker {
    const storageStartedAt = Date.now();
    let storageStage: TestRunOperationStoreStage = "lock_acquisition";
    try {
      assertTestRunOperationBinding(binding);
      let now: Date;
      if (issuedAt === undefined) {
        now = new Date(Math.max(this.#now().getTime(), Date.parse(binding.created_at)));
      } else {
        let timestamp: number;
        try { timestamp = Date.prototype.getTime.call(issuedAt); }
        catch { throw new TestRunOperationStoreError("issuance_invalid"); }
        if (!Number.isFinite(timestamp) || timestamp < Date.parse(binding.created_at)) throw new TestRunOperationStoreError("issuance_invalid");
        now = new Date(timestamp);
      }
      const marker = testRunRequestIssuanceMarker(binding, now);
      assertTestRunRequestIssuanceMarker(marker, binding);
      let publishedMarker = marker;
      const serialized = `${JSON.stringify(marker, null, 2)}\n`;
      const incomingBytes = Buffer.byteLength(serialized);
      if (incomingBytes > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("issuance_invalid");
      this.withStoreLock(() => {
        storageStage = "retained_store_maintenance";
        const status = this.maintain(this.#now());
        const persisted = this.lookup(binding.operation_id, this.#now());
        if (persisted.status !== "found" || testRunOperationBindingHash(persisted.binding) !== testRunOperationBindingHash(binding)) throw new TestRunOperationStoreError("issuance_invalid");
        const file = this.issuanceFile(binding.operation_id);
        const reservationFile = this.issuanceReservationFile(binding.operation_id);
        const reservation = secureFileStat(this.dir, reservationFile);
        const existing = secureFileStat(this.dir, file);
        if (existing) {
          if (existing.size <= 0 || existing.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("issuance_invalid");
          const parsed = JSON.parse(readSecureFile(this.dir, file, this.#limits.maxBindingBytes).toString("utf8")) as unknown;
          assertTestRunRequestIssuanceMarker(parsed, binding);
          publishedMarker = parsed;
          if (reservation) {
            durableUnlinkFile(this.dir, reservationFile, { expectedIdentity: reservation });
            const bytes = status.bytes - reservation.size;
            this.#lastStatus = { ...status, state: storeCapacityState(status.bindings, bytes, this.#limits), bytes };
          }
          return;
        }
        if (reservation && reservation.size < incomingBytes) throw new TestRunOperationStoreError("issuance_invalid");
        const replacedBytes = reservation?.size ?? 0;
        const publishedBytes = status.bytes - replacedBytes + incomingBytes;
        if (publishedBytes > this.#limits.maxBytes) {
          this.#lastStatus = { ...status, state: "capacity_exhausted" };
          throw new TestRunOperationStoreError("capacity_exhausted");
        }
        storageStage = "request_issuance_publication";
        writeNewFileDurable(this.dir, file, serialized);
        if (reservation) durableUnlinkFile(this.dir, reservationFile, { expectedIdentity: reservation });
        this.#lastStatus = { ...status, state: storeCapacityState(status.bindings, publishedBytes, this.#limits), bytes: publishedBytes };
      }, () => { storageStage = "lock_release"; });
      return publishedMarker;
    } catch (error) {
      if (error instanceof TestRunOperationStoreError) throw error;
      throw this.storageUnavailable(storageStage, storageStartedAt, error);
    }
  }

  lookupRequestIssuance(binding: TestRunOperationBinding, now: Date = this.#now()): TestRunRequestIssuanceLookup {
    try {
      assertTestRunOperationBinding(binding);
      const persisted = this.lookup(binding.operation_id, now);
      if (persisted.status !== "found" || testRunOperationBindingHash(persisted.binding) !== testRunOperationBindingHash(binding)) throw new TestRunOperationStoreError("issuance_invalid");
      const root = ensureSecureRoot(this.dir);
      // Publication writes and fsyncs .issued before deleting .reserve. Seeing the reservation
      // therefore remains bounded unknown; its absence is the commit point for reading .issued.
      const reservation = secureFileStat(root, this.issuanceReservationFile(binding.operation_id));
      if (reservation) {
        if (reservation.size <= 0 || reservation.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("issuance_invalid");
        return { status: "reserved" };
      }
      const file = this.issuanceFile(binding.operation_id);
      const stat = secureFileStat(root, file);
      if (!stat) return { status: "not_found" };
      if (stat.size <= 0 || stat.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("issuance_invalid");
      const parsed = JSON.parse(readSecureFile(root, file, this.#limits.maxBindingBytes).toString("utf8")) as unknown;
      assertTestRunRequestIssuanceMarker(parsed, binding);
      return { status: "found", marker: parsed };
    } catch (error) {
      if (error instanceof TestRunOperationStoreError) throw error;
      throw new TestRunOperationStoreError("storage_unavailable");
    }
  }

  publishTerminalNoExecution(binding: TestRunOperationBinding, marker: TestRunRequestIssuanceMarker, evidence: TestRunTerminalNoExecutionEvidence): TestRunTerminalNoExecutionReceipt {
    const storageStartedAt = Date.now();
    let storageStage: TestRunOperationStoreStage = "lock_acquisition";
    try {
      assertTestRunOperationBinding(binding);
      assertTestRunRequestIssuanceMarker(marker, binding);
      assertTestRunTerminalNoExecutionEvidence(evidence);
      const now = this.#now();
      const recordedAt = new Date(Math.max(now.getTime(), Date.parse(marker.issued_at)));
      const receipt = testRunTerminalNoExecutionReceipt(binding, marker, evidence, recordedAt);
      assertTestRunTerminalNoExecutionReceipt(receipt, binding, marker);
      let publishedReceipt = receipt;
      const serialized = `${JSON.stringify(receipt, null, 2)}\n`;
      const incomingBytes = Buffer.byteLength(serialized);
      if (incomingBytes > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("terminal_outcome_invalid");
      this.withStoreLock(() => {
        storageStage = "retained_store_maintenance";
        const status = this.maintain(this.#now());
        const persisted = this.lookup(binding.operation_id, this.#now());
        if (persisted.status !== "found" || testRunOperationBindingHash(persisted.binding) !== testRunOperationBindingHash(binding)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
        const issuance = this.lookupRequestIssuance(binding, this.#now());
        if (issuance.status !== "found" || testRunRequestIssuanceMarkerHash(issuance.marker) !== testRunRequestIssuanceMarkerHash(marker)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
        const file = this.terminalNoExecutionFile(binding.operation_id);
        const existing = secureFileStat(this.dir, file);
        if (existing) {
          if (existing.size <= 0 || existing.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("terminal_outcome_invalid");
          const parsed = JSON.parse(readSecureFile(this.dir, file, this.#limits.maxBindingBytes).toString("utf8")) as unknown;
          assertTestRunTerminalNoExecutionReceipt(parsed, binding, marker);
          if (!sameTerminalNoExecutionOutcome(parsed, receipt)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
          publishedReceipt = parsed;
          return;
        }
        if (status.bytes + incomingBytes > this.#limits.maxBytes) {
          this.#lastStatus = { ...status, state: "capacity_exhausted" };
          throw new TestRunOperationStoreError("capacity_exhausted");
        }
        storageStage = "terminal_outcome_publication";
        writeNewFileDurable(this.dir, file, serialized);
        this.#lastStatus = { ...status, state: storeCapacityState(status.bindings, status.bytes + incomingBytes, this.#limits), bytes: status.bytes + incomingBytes };
      }, () => { storageStage = "lock_release"; });
      return publishedReceipt;
    } catch (error) {
      if (error instanceof TestRunOperationStoreError) throw error;
      throw this.storageUnavailable(storageStage, storageStartedAt, error);
    }
  }

  lookupTerminalNoExecution(binding: TestRunOperationBinding, marker: TestRunRequestIssuanceMarker, now: Date = this.#now()): TestRunTerminalNoExecutionLookup {
    try {
      assertTestRunOperationBinding(binding);
      assertTestRunRequestIssuanceMarker(marker, binding);
      const persisted = this.lookup(binding.operation_id, now);
      if (persisted.status !== "found" || testRunOperationBindingHash(persisted.binding) !== testRunOperationBindingHash(binding)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
      const issuance = this.lookupRequestIssuance(binding, now);
      if (issuance.status !== "found" || testRunRequestIssuanceMarkerHash(issuance.marker) !== testRunRequestIssuanceMarkerHash(marker)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
      const root = ensureSecureRoot(this.dir);
      const file = this.terminalNoExecutionFile(binding.operation_id);
      const stat = secureFileStat(root, file);
      if (!stat) return { status: "not_found" };
      if (stat.size <= 0 || stat.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("terminal_outcome_invalid");
      const parsed = JSON.parse(readSecureFile(root, file, this.#limits.maxBindingBytes).toString("utf8")) as unknown;
      assertTestRunTerminalNoExecutionReceipt(parsed, binding, marker);
      return { status: "found", receipt: parsed };
    } catch (error) {
      if (error instanceof TestRunOperationStoreError) throw error;
      throw new TestRunOperationStoreError("storage_unavailable");
    }
  }

  snapshotStatus(): TestRunOperationStoreStatus {
    return { ...this.#lastStatus };
  }

  inspectStorage(deadlineUnixMs?: number) {
    return this.withAdminStoreLock((custody, deadline) => inspectTestRunStoreMetadata(custody, this.#limits, deadline), false, deadlineUnixMs);
  }

  previewCleanup(deadlineUnixMs?: number) {
    return this.withAdminStoreLock((custody, deadline) => previewTestRunStoreCleanup(custody, this.#limits, this.#now(), deadline, this.#adminFingerprintScope), false, deadlineUnixMs);
  }

  cleanupMetadata(confirmation: { confirm: boolean; previewFingerprint: string; beforeCommit?: () => boolean; nativeConfig?: NativeFileEvidence }, deadlineUnixMs?: number) {
    // Refuse before acquiring a lock or performing any storage maintenance.
    if (confirmation.confirm !== true || !/^sha256:[0-9a-f]{64}$/.test(confirmation.previewFingerprint)) throw new TestRunStoreAdminError("confirmation_required");
    return this.withAdminStoreLock((custody, deadline) => cleanupTestRunStoreMetadata(custody, this.#limits, this.#now(), deadline, confirmation, this.#adminFingerprintScope), true, deadlineUnixMs);
  }

  private withAdminStoreLock<T>(action: (custody: StorageDirectoryCustody, deadlineUnixMs: number) => T, mutation = false, deadlineUnixMs?: number): T {
    const deadline = Math.min(Date.now() + TEST_RUN_STORE_ADMIN_DEADLINE_MS, deadlineUnixMs ?? Number.MAX_SAFE_INTEGER);
    if (!Number.isFinite(deadline) || Date.now() >= deadline) throw new TestRunStoreAdminError("deadline_exceeded");
    const custody = openStorageDirectoryCustody(this.dir);
    try {
      if (mutation && process.platform !== "win32") custody.requireMutationCustody();
      if (Date.now() >= deadline) throw new TestRunStoreAdminError("deadline_exceeded");
      const lock = acquireStorageAdminLock(custody, ".store.lock", deadline);
      try { custody.assertCurrent(); return action(custody, deadline); }
      finally { lock.release(); }
    } finally { custody.close(); }
  }

  private withStoreLock<T>(action: () => T, onReleaseFailure?: () => void): T {
    ensureSecureRoot(this.dir);
    const lock = acquireStorageLock(this.dir, ".store.lock");
    this.#lastStatus = { ...this.#lastStatus, lock_recovery: lock.diagnostics };
    let result: T | undefined;
    let actionError: unknown;
    try { result = action(); } catch (error) { actionError = error; }
    try { lock.release(); } catch (error) { onReleaseFailure?.(); throw error; }
    if (actionError !== undefined) throw actionError;
    return result as T;
  }

  private storageUnavailable(stage: TestRunOperationStoreStage, startedAt: number, error: unknown): TestRunOperationStoreError {
    const lockFailure = error instanceof StorageLockUnavailableError ? error.diagnostics : undefined;
    const diagnostics: TestRunOperationStoreDiagnostics = {
      schema_version: 1,
      contract: TEST_RUN_STORAGE_DIAGNOSTICS_CONTRACT,
      stage,
      category: lockFailure?.category ?? "storage_io",
      owner_generation_state: lockFailure?.owner_generation_state ?? "none",
      automatic_recovery: "not_attempted",
      retryable: lockFailure?.retryable ?? false,
      recovery_hint: lockFailure?.recovery_hint ?? "inspect_private_operation_store",
      elapsed_ms: Math.max(0, Date.now() - startedAt),
    };
    this.#lastStatus = { ...this.#lastStatus, state: "unavailable", last_failure: diagnostics };
    return new TestRunOperationStoreError("storage_unavailable", diagnostics);
  }

  private maintain(now: Date): TestRunOperationStoreStatus {
    ensureSecureRoot(this.dir);
    maintainDeleteTombstones(this.dir, this.dir);
    let bindings = 0;
    let bytes = 0;
    let capacityExhausted = false;
    const retainedOperationIds = new Set<string>();
    const stats = new Map<string, SecureFileStat>();
    const files = secureRegularFiles(this.dir, this.dir, (name) => name.endsWith(".json"), {
      afterEntryLstat: (file, stat) => stats.set(file, stat),
    });
    for (const file of files) {
      const operationId = path.basename(file, ".json");
      if (!OPERATION_ID_PATTERN.test(operationId)) throw new TestRunOperationStoreError("binding_invalid");
      const stat = stats.get(file);
      if (!stat || stat.size <= 0 || stat.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("binding_invalid");
      // Immutable bodies are validated on exact lookup; maintenance only needs one when retention may delete it.
      if (bindingMayBeExpired(operationId, stat, now.getTime(), this.#limits.retentionMs)) {
        let binding: unknown;
        try { binding = JSON.parse(readSecureFile(this.dir, file, this.#limits.maxBindingBytes).toString("utf8")); }
        catch { throw new TestRunOperationStoreError("binding_invalid"); }
        assertTestRunOperationBinding(binding);
        if (binding.operation_id !== operationId) throw new TestRunOperationStoreError("binding_invalid");
        if (now.getTime() - Date.parse(binding.created_at) >= this.#limits.retentionMs) {
          durableUnlinkFile(this.dir, file, { expectedIdentity: stat });
          const issuanceFile = this.issuanceFile(operationId);
          const issuanceStat = secureFileStat(this.dir, issuanceFile);
          if (issuanceStat) durableUnlinkFile(this.dir, issuanceFile, { expectedIdentity: issuanceStat });
          const reservationFile = this.issuanceReservationFile(operationId);
          const reservationStat = secureFileStat(this.dir, reservationFile);
          if (reservationStat) durableUnlinkFile(this.dir, reservationFile, { expectedIdentity: reservationStat });
          const terminalFile = this.terminalNoExecutionFile(operationId);
          const terminalStat = secureFileStat(this.dir, terminalFile);
          if (terminalStat) durableUnlinkFile(this.dir, terminalFile, { expectedIdentity: terminalStat });
          continue;
        }
      }
      retainedOperationIds.add(operationId);
      bindings += 1;
      bytes += stat.size;
      capacityExhausted ||= bindings > this.#limits.maxBindings || bytes > this.#limits.maxBytes;
    }
    const issuanceStats = new Map<string, SecureFileStat>();
    const issuedOperationIds = new Set<string>();
    const issuanceFiles = secureRegularFiles(this.dir, this.dir, (name) => name.endsWith(".issued"), {
      afterEntryLstat: (file, stat) => issuanceStats.set(file, stat),
    });
    for (const file of issuanceFiles) {
      const operationId = path.basename(file, ".issued");
      if (!OPERATION_ID_PATTERN.test(operationId)) throw new TestRunOperationStoreError("issuance_invalid");
      const stat = issuanceStats.get(file);
      if (!stat || stat.size <= 0 || stat.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("issuance_invalid");
      if (!retainedOperationIds.has(operationId)) {
        durableUnlinkFile(this.dir, file, { expectedIdentity: stat });
        continue;
      }
      issuedOperationIds.add(operationId);
      bytes += stat.size;
      capacityExhausted ||= bytes > this.#limits.maxBytes;
    }
    const reservationStats = new Map<string, SecureFileStat>();
    const reservationFiles = secureRegularFiles(this.dir, this.dir, (name) => name.endsWith(".reserve"), {
      afterEntryLstat: (file, stat) => reservationStats.set(file, stat),
    });
    for (const file of reservationFiles) {
      const operationId = path.basename(file, ".reserve");
      if (!OPERATION_ID_PATTERN.test(operationId)) throw new TestRunOperationStoreError("issuance_invalid");
      const stat = reservationStats.get(file);
      if (!stat || stat.size <= 0 || stat.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("issuance_invalid");
      if (!retainedOperationIds.has(operationId) || issuedOperationIds.has(operationId)) {
        durableUnlinkFile(this.dir, file, { expectedIdentity: stat });
        continue;
      }
      bytes += stat.size;
      capacityExhausted ||= bytes > this.#limits.maxBytes;
    }
    const terminalStats = new Map<string, SecureFileStat>();
    const terminalFiles = secureRegularFiles(this.dir, this.dir, (name) => name.endsWith(".terminal"), {
      afterEntryLstat: (file, stat) => terminalStats.set(file, stat),
    });
    for (const file of terminalFiles) {
      const operationId = path.basename(file, ".terminal");
      if (!OPERATION_ID_PATTERN.test(operationId)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
      const stat = terminalStats.get(file);
      if (!stat || stat.size <= 0 || stat.size > this.#limits.maxBindingBytes) throw new TestRunOperationStoreError("terminal_outcome_invalid");
      if (!retainedOperationIds.has(operationId)) {
        durableUnlinkFile(this.dir, file, { expectedIdentity: stat });
        continue;
      }
      bytes += stat.size;
      capacityExhausted ||= bytes > this.#limits.maxBytes;
    }
    const status: TestRunOperationStoreStatus = {
      schema_version: 1,
      state: capacityExhausted ? "capacity_exhausted" : "ok",
      bindings,
      bytes,
      ...(this.#lastStatus.lock_recovery ? { lock_recovery: this.#lastStatus.lock_recovery } : {}),
    };
    this.#lastStatus = status;
    return status;
  }

  private bindingFile(operationId: string): string {
    return path.join(this.dir, `${operationId}.json`);
  }

  private issuanceFile(operationId: string): string {
    return path.join(this.dir, `${operationId}.issued`);
  }

  private issuanceReservationFile(operationId: string): string {
    return path.join(this.dir, `${operationId}.reserve`);
  }

  private terminalNoExecutionFile(operationId: string): string {
    return path.join(this.dir, `${operationId}.terminal`);
  }
}

export function testRunOperationStoreDir(instancesDir: string): string {
  const resolvedInstancesDir = absoluteStoragePath(instancesDir);
  return path.join(path.dirname(resolvedInstancesDir), "operations", "test-runs");
}

export function testRunCanonicalProjectRootHash(canonicalRoot: string): string {
  return `sha256:${createHash("sha256").update(ROOT_HASH_DOMAIN).update(canonicalRoot, "utf8").digest("hex")}`;
}

export function testRunOperationBindingHash(binding: TestRunOperationBinding): string {
  assertTestRunOperationBinding(binding);
  const canonical = Object.fromEntries(BINDING_KEYS.map((key) => [key, binding[key as keyof TestRunOperationBinding]]));
  return `sha256:${createHash("sha256").update(Buffer.from(`${TEST_RUN_OPERATION_OWNER_CONTRACT}\0`, "utf8")).update(JSON.stringify(canonical), "utf8").digest("hex")}`;
}

export function testRunRequestIssuanceMarkerHash(marker: TestRunRequestIssuanceMarker): string {
  assertTestRunRequestIssuanceMarker(marker);
  const canonical = Object.fromEntries(ISSUANCE_KEYS.map((key) => [key, marker[key as keyof TestRunRequestIssuanceMarker]]));
  return `sha256:${createHash("sha256").update(Buffer.from(`${TEST_RUN_REQUEST_ISSUANCE_CONTRACT}\0`, "utf8")).update(JSON.stringify(canonical), "utf8").digest("hex")}`;
}

export function testRunTerminalNoExecutionCode(reason: TestRunTerminalNoExecutionReason): string {
  return TEST_RUN_TERMINAL_NO_EXECUTION_CODES[reason];
}

export function isTestRunOperationId(value: unknown): value is string {
  return typeof value === "string" && OPERATION_ID_PATTERN.test(value);
}

export function assertTestRunOperationBinding(value: unknown): asserts value is TestRunOperationBinding {
  if (!isRecord(value) || !hasExactKeys(value, BINDING_KEYS)) throw new TestRunOperationStoreError("binding_invalid");
  if (value.schema_version !== 1 || value.contract !== TEST_RUN_OPERATION_OWNER_CONTRACT || value.command !== "test.run" || !isTestRunOperationId(value.operation_id)) throw new TestRunOperationStoreError("binding_invalid");
  if (!boundedString(value.project, 256) || !boundedString(value.original_project_selector, 4096) || !boundedString(value.origin_project_root, 4096) || !boundedString(value.canonical_project_root, 4096)) throw new TestRunOperationStoreError("binding_invalid");
  if (!boundedString(value.origin_instance_id, 256) || !Number.isSafeInteger(value.origin_unity_pid) || Number(value.origin_unity_pid) <= 0 || !isProcessStartTimestamp(value.origin_unity_process_start_utc)) throw new TestRunOperationStoreError("binding_invalid");
  if (!isHash(value.canonical_project_root_hash) || !isHash(value.caller_args_hash) || value.canonical_project_root_hash !== testRunCanonicalProjectRootHash(value.canonical_project_root as string)) throw new TestRunOperationStoreError("binding_invalid");
  try {
    if (canonicalPaths.normalizeProjectRoot(value.origin_project_root as string) !== value.canonical_project_root) throw new TestRunOperationStoreError("binding_invalid");
  } catch (error) {
    if (error instanceof TestRunOperationStoreError) throw error;
    throw new TestRunOperationStoreError("binding_invalid");
  }
  if (value.artifact_path !== `${TEST_RUN_ARTIFACT_ROOT}/${value.operation_id}.json` || value.artifact_schema_version !== TEST_RUN_ARTIFACT_SCHEMA_VERSION) throw new TestRunOperationStoreError("binding_invalid");
  if (value.package_id !== "com.sungeargames.unity-mcp" || !boundedString(value.package_version, 256) || !boundedString(value.commands_hash, 256)) throw new TestRunOperationStoreError("binding_invalid");
  if (!Number.isSafeInteger(value.catalog_generation) || Number(value.catalog_generation) <= 0 || value.artifact_schema_capability !== TEST_RUN_ARTIFACT_CAPABILITY || !isIsoTimestamp(value.created_at)) throw new TestRunOperationStoreError("binding_invalid");
}

export function assertTestRunRequestIssuanceMarker(value: unknown, binding?: TestRunOperationBinding): asserts value is TestRunRequestIssuanceMarker {
  if (!isRecord(value) || !hasExactKeys(value, ISSUANCE_KEYS)) throw new TestRunOperationStoreError("issuance_invalid");
  if (value.schema_version !== 1 || value.contract !== TEST_RUN_REQUEST_ISSUANCE_CONTRACT || value.command !== "test.run" || value.state !== "issued" || !isTestRunOperationId(value.operation_id)) throw new TestRunOperationStoreError("issuance_invalid");
  if (!isHash(value.owner_binding_hash) || !boundedString(value.project, 256) || !isHash(value.canonical_project_root_hash) || !boundedString(value.origin_instance_id, 256)) throw new TestRunOperationStoreError("issuance_invalid");
  if (!Number.isSafeInteger(value.origin_unity_pid) || Number(value.origin_unity_pid) <= 0 || !isProcessStartTimestamp(value.origin_unity_process_start_utc) || !isHash(value.caller_args_hash)) throw new TestRunOperationStoreError("issuance_invalid");
  if (value.artifact_path !== `${TEST_RUN_ARTIFACT_ROOT}/${value.operation_id}.json` || value.artifact_schema_version !== TEST_RUN_ARTIFACT_SCHEMA_VERSION) throw new TestRunOperationStoreError("issuance_invalid");
  if (value.package_id !== "com.sungeargames.unity-mcp" || !boundedString(value.package_version, 256) || !boundedString(value.commands_hash, 256)) throw new TestRunOperationStoreError("issuance_invalid");
  if (!Number.isSafeInteger(value.catalog_generation) || Number(value.catalog_generation) <= 0 || value.artifact_schema_capability !== TEST_RUN_ARTIFACT_CAPABILITY || !isIsoTimestamp(value.issued_at)) throw new TestRunOperationStoreError("issuance_invalid");
  if (binding) {
    assertTestRunOperationBinding(binding);
    const expected = testRunRequestIssuanceMarker(binding, new Date(value.issued_at as string));
    for (const key of ISSUANCE_BINDING_KEYS) {
      if (value[key] !== expected[key]) throw new TestRunOperationStoreError("issuance_invalid");
    }
    if (Date.parse(value.issued_at as string) < Date.parse(binding.created_at)) throw new TestRunOperationStoreError("issuance_invalid");
  }
}

export function assertTestRunTerminalNoExecutionEvidence(value: unknown): asserts value is TestRunTerminalNoExecutionEvidence {
  if (!isRecord(value) || !hasExactKeys(value, TERMINAL_EVIDENCE_KEYS)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (!isTestRunTerminalNoExecutionReason(value.reason) || value.bridge_error_code !== testRunTerminalNoExecutionCode(value.reason)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  assertTestRunTerminalPackageUpdateEvidence(value.package_update);
}

export function assertTestRunTerminalNoExecutionReceipt(value: unknown, binding?: TestRunOperationBinding, marker?: TestRunRequestIssuanceMarker): asserts value is TestRunTerminalNoExecutionReceipt {
  if (!isRecord(value) || !hasExactKeys(value, TERMINAL_RECEIPT_KEYS)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (value.schema_version !== 1 || value.contract !== TEST_RUN_TERMINAL_NO_EXECUTION_CONTRACT || value.command !== "test.run" || value.state !== "not_executed") throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (value.operation_created !== false || value.command_dispatched !== false || value.test_runner_execute_invoked !== false || value.artifact_persisted !== false || !isTestRunOperationId(value.operation_id)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (!isHash(value.owner_binding_hash) || !isHash(value.request_issuance_hash) || !isIsoTimestamp(value.issued_at) || !boundedString(value.project, 256) || !isHash(value.canonical_project_root_hash) || !boundedString(value.origin_instance_id, 256)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (!Number.isSafeInteger(value.origin_unity_pid) || Number(value.origin_unity_pid) <= 0 || !isProcessStartTimestamp(value.origin_unity_process_start_utc) || !isHash(value.caller_args_hash)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (value.expected_artifact_path !== `${TEST_RUN_ARTIFACT_ROOT}/${value.operation_id}.json` || value.artifact_schema_version !== TEST_RUN_ARTIFACT_SCHEMA_VERSION) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (value.package_id !== "com.sungeargames.unity-mcp" || !boundedString(value.package_version, 256) || !boundedString(value.commands_hash, 256)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (!Number.isSafeInteger(value.catalog_generation) || Number(value.catalog_generation) <= 0 || value.artifact_schema_capability !== TEST_RUN_ARTIFACT_CAPABILITY || !isIsoTimestamp(value.recorded_at)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  assertTestRunTerminalNoExecutionEvidence({ bridge_error_code: value.bridge_error_code, reason: value.reason, package_update: value.package_update });
  if (Date.parse(value.recorded_at as string) < Date.parse(value.issued_at as string)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (binding) {
    assertTestRunOperationBinding(binding);
    if (value.operation_id !== binding.operation_id || value.owner_binding_hash !== testRunOperationBindingHash(binding)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
    for (const key of TERMINAL_BINDING_KEYS) {
      if (value[key] !== terminalBindingValue(binding, key)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
    }
  }
  if (marker) {
    assertTestRunRequestIssuanceMarker(marker, binding);
    if (value.request_issuance_hash !== testRunRequestIssuanceMarkerHash(marker) || value.issued_at !== marker.issued_at) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  }
}

const BINDING_KEYS = [
  "schema_version", "contract", "command", "operation_id", "project", "original_project_selector", "origin_project_root", "canonical_project_root", "canonical_project_root_hash", "origin_instance_id", "origin_unity_pid", "origin_unity_process_start_utc", "caller_args_hash", "artifact_path", "artifact_schema_version", "package_id", "package_version", "commands_hash", "catalog_generation", "artifact_schema_capability", "created_at",
];
const ISSUANCE_KEYS = [
  "schema_version", "contract", "command", "state", "operation_id", "owner_binding_hash", "project", "canonical_project_root_hash", "origin_instance_id", "origin_unity_pid", "origin_unity_process_start_utc", "caller_args_hash", "artifact_path", "artifact_schema_version", "package_id", "package_version", "commands_hash", "catalog_generation", "artifact_schema_capability", "issued_at",
];
const ISSUANCE_BINDING_KEYS: Array<keyof TestRunRequestIssuanceMarker> = ISSUANCE_KEYS.filter((key) => key !== "issued_at") as Array<keyof TestRunRequestIssuanceMarker>;
const TERMINAL_EVIDENCE_KEYS = ["bridge_error_code", "reason", "package_update"];
const TERMINAL_RECEIPT_KEYS = [
  "schema_version", "contract", "command", "state", "operation_created", "command_dispatched", "test_runner_execute_invoked", "artifact_persisted", "operation_id", "owner_binding_hash", "request_issuance_hash", "issued_at", "project", "canonical_project_root_hash", "origin_instance_id", "origin_unity_pid", "origin_unity_process_start_utc", "caller_args_hash", "expected_artifact_path", "artifact_schema_version", "package_id", "package_version", "commands_hash", "catalog_generation", "artifact_schema_capability", "bridge_error_code", "reason", "package_update", "recorded_at",
];
const TERMINAL_BINDING_KEYS = ["project", "canonical_project_root_hash", "origin_instance_id", "origin_unity_pid", "origin_unity_process_start_utc", "caller_args_hash", "expected_artifact_path", "artifact_schema_version", "package_id", "package_version", "commands_hash", "catalog_generation", "artifact_schema_capability"] as const;
const PACKAGE_UPDATE_KEYS = ["status", "refresh_pending", "package_refresh_required", "package_refresh_wait_can_advance", "stale_package_resolution", "fingerprint", "pending_fingerprint", "last_transition_utc", "local_package_code_inventory"];
const PACKAGE_INVENTORY_BASE_KEYS = ["status", "failure_reason", "mismatch_package", "local_package_count", "source_file_count", "asset_database_file_count", "missing_asset_file_count", "unexpected_asset_file_count", "source_fingerprint", "asset_database_fingerprint", "source_state_fingerprint"];
const PACKAGE_INVENTORY_COMPILER_KEYS = ["compiler_source_file_count", "missing_compiler_source_file_count", "unexpected_compiler_source_file_count", "compiler_source_fingerprint"];
const PACKAGE_INVENTORY_KEYS = [...PACKAGE_INVENTORY_BASE_KEYS, ...PACKAGE_INVENTORY_COMPILER_KEYS];
const TEST_RUN_TERMINAL_NO_EXECUTION_CODES = Object.freeze({
  background_compilation_failed: "unity_exception",
  background_compilation_unavailable: "unity_exception",
  background_compilation_pending: "unity_reloading",
  compile_freshness_unavailable: "compile_freshness_unavailable",
  asset_update_active: "unity_reloading",
  package_refresh_required: "package_refresh_required",
  package_refresh_pending: "unity_reloading",
  compile_lifecycle_settling: "unity_reloading",
} as const);
const PACKAGE_UPDATE_STATUSES = new Set(["current", "unavailable", "package_refresh_required", "stale_package_resolution"]);
const PACKAGE_INVENTORY_STATUSES = new Set(["current", "stale", "unavailable"]);
const PACKAGE_INVENTORY_FAILURE_REASONS = new Set(["", "not_scanned", "snapshot_unavailable", "input_unavailable", "package_limit_exceeded", "asset_path_limit_exceeded", "package_identity_invalid", "resolved_path_unavailable", "path_security_rejected", "budget_exhausted", "directory_limit_exceeded", "file_limit_exceeded", "duplicate_asset_path", "compiler_source_limit_exceeded", "compiler_source_read_exception", "read_exception"]);

function testRunRequestIssuanceMarker(binding: TestRunOperationBinding, issuedAt: Date): TestRunRequestIssuanceMarker {
  assertTestRunOperationBinding(binding);
  return {
    schema_version: 1,
    contract: TEST_RUN_REQUEST_ISSUANCE_CONTRACT,
    command: "test.run",
    state: "issued",
    operation_id: binding.operation_id,
    owner_binding_hash: testRunOperationBindingHash(binding),
    project: binding.project,
    canonical_project_root_hash: binding.canonical_project_root_hash,
    origin_instance_id: binding.origin_instance_id,
    origin_unity_pid: binding.origin_unity_pid,
    origin_unity_process_start_utc: binding.origin_unity_process_start_utc,
    caller_args_hash: binding.caller_args_hash,
    artifact_path: binding.artifact_path,
    artifact_schema_version: binding.artifact_schema_version,
    package_id: binding.package_id,
    package_version: binding.package_version,
    commands_hash: binding.commands_hash,
    catalog_generation: binding.catalog_generation,
    artifact_schema_capability: binding.artifact_schema_capability,
    issued_at: issuedAt.toISOString(),
  };
}

function testRunTerminalNoExecutionReceipt(binding: TestRunOperationBinding, marker: TestRunRequestIssuanceMarker, evidence: TestRunTerminalNoExecutionEvidence, recordedAt: Date): TestRunTerminalNoExecutionReceipt {
  return {
    schema_version: 1,
    contract: TEST_RUN_TERMINAL_NO_EXECUTION_CONTRACT,
    command: "test.run",
    state: "not_executed",
    operation_created: false,
    command_dispatched: false,
    test_runner_execute_invoked: false,
    artifact_persisted: false,
    operation_id: binding.operation_id,
    owner_binding_hash: testRunOperationBindingHash(binding),
    request_issuance_hash: testRunRequestIssuanceMarkerHash(marker),
    issued_at: marker.issued_at,
    project: binding.project,
    canonical_project_root_hash: binding.canonical_project_root_hash,
    origin_instance_id: binding.origin_instance_id,
    origin_unity_pid: binding.origin_unity_pid,
    origin_unity_process_start_utc: binding.origin_unity_process_start_utc,
    caller_args_hash: binding.caller_args_hash,
    expected_artifact_path: binding.artifact_path,
    artifact_schema_version: binding.artifact_schema_version,
    package_id: binding.package_id,
    package_version: binding.package_version,
    commands_hash: binding.commands_hash,
    catalog_generation: binding.catalog_generation,
    artifact_schema_capability: binding.artifact_schema_capability,
    bridge_error_code: evidence.bridge_error_code,
    reason: evidence.reason,
    package_update: evidence.package_update,
    recorded_at: recordedAt.toISOString(),
  };
}

function terminalBindingValue(binding: TestRunOperationBinding, key: typeof TERMINAL_BINDING_KEYS[number]): unknown {
  return key === "expected_artifact_path" ? binding.artifact_path : binding[key];
}

function sameTerminalNoExecutionOutcome(left: TestRunTerminalNoExecutionReceipt, right: TestRunTerminalNoExecutionReceipt): boolean {
  return left.bridge_error_code === right.bridge_error_code && left.reason === right.reason && JSON.stringify(left.package_update) === JSON.stringify(right.package_update);
}

function assertTestRunTerminalPackageUpdateEvidence(value: unknown): asserts value is TestRunTerminalPackageUpdateEvidence {
  if (!isRecord(value) || !hasExactKeys(value, PACKAGE_UPDATE_KEYS) || !PACKAGE_UPDATE_STATUSES.has(String(value.status))) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (typeof value.refresh_pending !== "boolean" || typeof value.package_refresh_required !== "boolean" || typeof value.package_refresh_wait_can_advance !== "boolean" || typeof value.stale_package_resolution !== "boolean") throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (!boundedOptionalString(value.fingerprint, 256) || !boundedOptionalString(value.pending_fingerprint, 256) || !isBoundedBridgeTimestamp(value.last_transition_utc)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  assertTestRunTerminalPackageInventoryEvidence(value.local_package_code_inventory);
}

function assertTestRunTerminalPackageInventoryEvidence(value: unknown): asserts value is TestRunTerminalPackageInventoryEvidence {
  if (!isRecord(value) || !PACKAGE_INVENTORY_STATUSES.has(String(value.status)) || !PACKAGE_INVENTORY_FAILURE_REASONS.has(String(value.failure_reason))) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  const compilerFieldCount = PACKAGE_INVENTORY_COMPILER_KEYS.filter((key) => Object.hasOwn(value, key)).length;
  if (compilerFieldCount !== 0 && compilerFieldCount !== PACKAGE_INVENTORY_COMPILER_KEYS.length) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (!hasExactKeys(value, compilerFieldCount === 0 ? PACKAGE_INVENTORY_BASE_KEYS : PACKAGE_INVENTORY_KEYS)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  if (!boundedOptionalString(value.mismatch_package, 256) || !boundedInteger(value.local_package_count, 64)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  for (const key of ["source_file_count", "asset_database_file_count", "missing_asset_file_count", "unexpected_asset_file_count"] as const) {
    if (!boundedInteger(value[key], 16_384)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  }
  for (const key of ["source_fingerprint", "asset_database_fingerprint", "source_state_fingerprint"] as const) {
    if (!boundedOptionalString(value[key], 256)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
  }
  if (compilerFieldCount !== 0
    && (!boundedInteger(value.compiler_source_file_count, 16_384)
      || !boundedInteger(value.missing_compiler_source_file_count, 16_384)
      || !boundedInteger(value.unexpected_compiler_source_file_count, 16_384)
      || !boundedOptionalString(value.compiler_source_fingerprint, 256))) {
    throw new TestRunOperationStoreError("terminal_outcome_invalid");
  }
}

function isTestRunTerminalNoExecutionReason(value: unknown): value is TestRunTerminalNoExecutionReason {
  return typeof value === "string" && Object.hasOwn(TEST_RUN_TERMINAL_NO_EXECUTION_CODES, value);
}

function serializedIssuanceMarker(binding: TestRunOperationBinding, issuedAt: Date): { serialized: string; bytes: number } {
  const serialized = `${JSON.stringify(testRunRequestIssuanceMarker(binding, issuedAt), null, 2)}\n`;
  return { serialized, bytes: Buffer.byteLength(serialized) };
}

function testRunRequestIssuanceReservationBytes(binding: TestRunOperationBinding): number {
  const issuedAt = new Date(binding.created_at);
  return serializedIssuanceMarker(binding, issuedAt).bytes + "+275760-09-13T00:00:00.000Z".length - issuedAt.toISOString().length;
}

function isRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
function hasExactKeys(value: Record<string, unknown>, expected: string[]): boolean { const keys = Object.keys(value); return keys.length === expected.length && keys.every((key) => expected.includes(key)); }
function boundedString(value: unknown, max: number): value is string { return typeof value === "string" && value.length > 0 && value.length <= max; }
function boundedOptionalString(value: unknown, max: number): value is string { return typeof value === "string" && value.length <= max; }
function boundedInteger(value: unknown, max: number): value is number { return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 && value <= max; }
function isBoundedBridgeTimestamp(value: unknown): value is string { return value === "" || typeof value === "string" && value.length <= 48 && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z$/.test(value) && Number.isFinite(Date.parse(value)); }
function isHash(value: unknown): value is string { return typeof value === "string" && HASH_PATTERN.test(value); }
function bindingMayBeExpired(operationId: string, stat: SecureFileStat, nowMs: number, retentionMs: number): boolean {
  if (nowMs - stat.mtimeMs >= retentionMs) return true;
  const match = /^test-(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})-[0-9a-f]{8}$/.exec(operationId);
  if (!match) return false;
  const parts = match.slice(1, 7).map(Number);
  const timestamp = Date.UTC(parts[0]!, parts[1]! - 1, parts[2]!, parts[3]!, parts[4]!, parts[5]!);
  const date = new Date(timestamp);
  const valid = date.getUTCFullYear() === parts[0] && date.getUTCMonth() === parts[1]! - 1 && date.getUTCDate() === parts[2]
    && date.getUTCHours() === parts[3] && date.getUTCMinutes() === parts[4] && date.getUTCSeconds() === parts[5];
  return valid && nowMs - timestamp >= retentionMs;
}
function isProcessStartTimestamp(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,9})?Z$/.exec(value);
  if (!match) return false;
  const year = Number(match[1]);
  const month = Number(match[2]);
  const day = Number(match[3]);
  const hour = Number(match[4]);
  const minute = Number(match[5]);
  const second = Number(match[6]);
  if (!year) return false;
  const timestamp = new Date(0);
  timestamp.setUTCFullYear(year, month - 1, day);
  timestamp.setUTCHours(hour, minute, second, 0);
  return timestamp.getUTCFullYear() === year
    && timestamp.getUTCMonth() === month - 1
    && timestamp.getUTCDate() === day
    && timestamp.getUTCHours() === hour
    && timestamp.getUTCMinutes() === minute
    && timestamp.getUTCSeconds() === second;
}
function isIsoTimestamp(value: unknown): value is string { if (typeof value !== "string") return false; const timestamp = Date.parse(value); return Number.isFinite(timestamp) && new Date(timestamp).toISOString() === value; }
function positiveLimit(value: number | undefined, fallback: number, maximum: number): number { const result = value ?? fallback; if (!Number.isSafeInteger(result) || result <= 0 || result > maximum) throw new TypeError("Invalid test run operation store limit"); return result; }
function storeCapacityState(bindings: number, bytes: number, limits: { maxBindings: number; maxBytes: number }): TestRunOperationStoreStatus["state"] { return bindings > limits.maxBindings || bytes > limits.maxBytes ? "capacity_exhausted" : "ok"; }
