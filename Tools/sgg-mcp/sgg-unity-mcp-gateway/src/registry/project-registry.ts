import { createHash } from "node:crypto";
import fs, { type BigIntStats, type Dirent } from "node:fs";
import path from "node:path";

import { BRIDGE_PROTOCOL_BOUNDS, BridgeProtocolError, parseHeartbeatRecordV1, type HeartbeatRecord } from "../bridge/bridge-protocol.js";
import type { ErrorCode } from "../mcp/results.js";
import { CanonicalPathService } from "../paths/canonical-path.js";
import type { OperationalLogger } from "../observability/operational-logger.js";
import { canonicalJsonStringify } from "../security/canonical-json.js";
import { durableUnlinkFile, readSecureFile, sameFileObjectIdentity, secureFileStat, type SecureFileStat } from "../security/safe-storage.js";

const canonicalPaths = new CanonicalPathService();
const DEFAULT_CACHE_ENTRIES = 1024;
const MAX_CACHE_ENTRIES = 10_000;
const DEFAULT_INVALID_DIAGNOSTICS = 32;
const MAX_INVALID_DIAGNOSTICS = 128;
const DEFAULT_CACHE_TTL_MS = 10 * 60_000;
const MAX_CACHE_TTL_MS = 24 * 60 * 60_000;
const DEFAULT_SCAN_FILES = 1024;
const MAX_SCAN_FILES = 10_000;
const SCAN_ENTRY_SLACK = 32;
const READ_ATTEMPTS = 2;
const NO_FOLLOW = fs.constants.O_NOFOLLOW ?? 0;
const DEFAULT_STALE_QUERY_LIMIT = 8;
const MAX_STALE_QUERY_LIMIT = 16;
const DEFAULT_STALE_CLEANUP_LIMIT = 25;
const MAX_STALE_CLEANUP_LIMIT = 100;
const STALE_QUERY_CURSOR_VERSION = 1;
const STALE_QUERY_CURSOR_DOMAIN = "sgg.unity_mcp.stale_projects.cursor.v1\0";
const STALE_RECORD_ID_DOMAIN = "sgg.unity_mcp.stale_project_record.v1\0";
const STALE_CLEANUP_PRECONDITION_DOMAIN = "sgg.unity_mcp.stale_registry_cleanup.v1\0";
const SAFE_INSTANCE_ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,255}$/;
export { HEARTBEAT_STATUSES, type HeartbeatRecord } from "../bridge/bridge-protocol.js";

export type PublicProjectRecord = Omit<HeartbeatRecord, "token"> & {
  stale: boolean;
};

export type StaleProjectQuery = {
  project?: string | undefined;
  status?: HeartbeatRecord["status"] | undefined;
  last_heartbeat_from?: string | undefined;
  last_heartbeat_to?: string | undefined;
  cursor?: string | undefined;
  limit?: number | undefined;
};

export type StaleProjectListRecord = PublicProjectRecord & {
  stale_record_id: string;
};

export type StaleProjectPage = {
  schema_version: 1;
  projects: StaleProjectListRecord[];
  returned_count: number;
  total_available: number;
  captured_at: string;
  next_cursor: string | null;
  truncated: boolean;
  limits: { default_limit: number; max_limit: number };
};

export type StaleRegistryCleanupArgs = {
  dry_run?: boolean | undefined;
  project?: string | undefined;
  status?: HeartbeatRecord["status"] | undefined;
  last_heartbeat_before?: string | undefined;
  limit?: number | undefined;
  record_ids?: string[] | undefined;
  precondition_hash?: string | undefined;
  confirm_cleanup?: boolean | undefined;
};

export type StaleRegistryCleanupRecord = {
  stale_record_id: string;
  project: string;
  instance_id: string;
  status: HeartbeatRecord["status"];
  last_heartbeat: string;
  age_ms: number;
  heartbeat_generation: number | null;
  process_state: RegistryProcessState;
  action: "would_delete" | "deleted" | "skipped_process_live" | "skipped_process_uncertain";
};

export type StaleRegistryCleanupResult = {
  schema_version: 1;
  operation: "stale_registry_cleanup";
  dry_run: boolean;
  mutation_attempted: boolean;
  cleanup_after_ms: number;
  candidate_count: number;
  selected_count: number;
  would_delete_count: number;
  deleted_count: number;
  skipped_count: number;
  precondition_hash: string | null;
  records: StaleRegistryCleanupRecord[];
  truncated: boolean;
  limits: { default_limit: number; max_delete: number };
  safety: {
    invalid_records_deleted: false;
    live_records_deleted: false;
    project_files_deleted: false;
    arbitrary_paths_accepted: false;
  };
  next_valid_actions: string[];
};

export type RegistryProcessState = "live" | "dead" | "uncertain";

export type RegistryProcessGenerationIdentity = {
  instance_id: string;
  unity_pid: number;
  unity_process_start_utc: string;
  heartbeat_generation: number;
  last_heartbeat: string;
};

export type RegistryProcessGenerationObservation = RegistryProcessGenerationIdentity & {
  state: RegistryProcessState;
};

export type RegistryProcessGenerationInspector = (
  identities: ReadonlyArray<RegistryProcessGenerationIdentity>,
) => ReadonlyArray<RegistryProcessGenerationObservation>;

export class StaleRegistryOperationError extends Error {
  readonly code: Extract<ErrorCode, "invalid_field_value" | "feature_disabled" | "confirmation_required" | "precondition_required" | "precondition_failed" | "unsafe_binding_refused" | "internal_error">;
  readonly details: Record<string, unknown>;

  constructor(code: StaleRegistryOperationError["code"], message: string, details: Record<string, unknown> = {}) {
    super(message);
    this.name = "StaleRegistryOperationError";
    this.code = code;
    this.details = details;
  }
}

export type ProjectResolution =
  | { status: "ok"; record: HeartbeatRecord }
  | ProjectResolutionError;

export type ProjectRegistrySnapshotResolution =
  | { status: "ok"; record: PublicProjectRecord }
  | ProjectResolutionError;

export type ProjectResolutionError =
  | { status: "error"; code: Extract<ErrorCode, "project_not_found" | "project_stale" | "duplicate_project_id">; matches: PublicProjectRecord[] }
  | { status: "error"; code: "unsafe_binding_refused"; matches: []; registryOperationalState: Exclude<RegistryOperationalState, "ok"> };

export type ProjectResolutionOptions = {
  allowStale?: boolean;
};

export type ReplacementProjectResolution =
  | { status: "ok"; record: HeartbeatRecord }
  | { status: "error"; code: "project_not_found" | "duplicate_project_id" };

export type VerificationProjectResolution =
  | { status: "ok"; record: HeartbeatRecord; role: "current" | "replacement" }
  | { status: "error"; code: "project_not_found" | "duplicate_project_id" };

export type RecentProjectIdentity = Pick<PublicProjectRecord, "instance_id" | "project" | "bridge_port" | "last_heartbeat" | "status" | "stale">;

export type RecentReplacementProjectResolution =
  | { status: "ok"; failedRecord: RecentProjectIdentity; replacementRecord: RecentProjectIdentity }
  | { status: "error"; code: "project_not_found" | "duplicate_project_id" };

export type RecentReplacementRecordResolution =
  | { status: "ok"; failedRecord: RecentProjectIdentity; replacementRecord: HeartbeatRecord }
  | { status: "error"; code: "project_not_found" | "duplicate_project_id" };

export type RegistryInvalidDiagnosticCode =
  | "unsafe_registry_directory"
  | "registry_directory_unavailable"
  | "registry_directory_unstable"
  | "unsafe_heartbeat_file"
  | "heartbeat_unreadable"
  | "heartbeat_unstable"
  | "heartbeat_too_large"
  | "heartbeat_invalid_json"
  | "heartbeat_invalid_schema"
  | "heartbeat_expired"
  | "protocol_version_mismatch";

type RegistryFileSystem = {
  readonly platform: NodeJS.Platform;
  lstatSync(file: string, options: { bigint: true }): BigIntStats;
  opendirSync(directory: string): RegistryDirectory;
  openSync(file: string, flags: number): number;
  fstatSync(handle: number, options: { bigint: true }): BigIntStats;
  readSync(handle: number, buffer: Buffer, offset: number, length: number, position: null): number;
  closeSync(handle: number): void;
  realpathSync(file: string): string;
};

type RegistryDirectory = {
  readSync(): Dirent | null;
  closeSync(): void;
};

export type ProjectRegistryOptions = {
  instancesDir: string;
  staleAfterMs: number;
  cleanupAfterMs?: number;
  cacheMaxEntries?: number;
  cacheTtlMs?: number;
  invalidDiagnosticsMax?: number;
  scanMaxFiles?: number;
  now?: () => Date;
  fileSystem?: RegistryFileSystem;
  processState?: (pid: number, processStartUtc?: string, projectRoot?: string) => RegistryProcessState;
  processGenerationInspector?: RegistryProcessGenerationInspector;
  logger?: OperationalLogger;
};

export type ProjectRegistryStats = {
  projectRecordsCount: number;
  liveProjectRecordsCount: number;
  staleProjectRecordsCount: number;
  invalidProjectRecordsCount: number;
  protocolVersionMismatchProjectRecordsCount: number;
  removedProjectRecordsCount: number;
  uniqueProjectsCount: number;
  liveProjectsCount: number;
  staleProjectsCount: number;
  heartbeatCacheHitCount: number;
  heartbeatCacheMissCount: number;
  heartbeatCacheEntriesCount: number;
  invalidProjectRecordDiagnostics: ReadonlyArray<{ code: RegistryInvalidDiagnosticCode }>;
  invalidProjectRecordDiagnosticsTruncated: boolean;
  heartbeatScanFilesCount: number;
  heartbeatScanOverflowCount: number;
  heartbeatScanTruncated: boolean;
  registryOperationalState: RegistryOperationalState;
};

export type ProjectRegistrySnapshot = {
  readonly captured_at: string;
  readonly capture_started_at: string;
  readonly capture_ended_at: string;
  readonly records: ReadonlyArray<PublicProjectRecord>;
  listProjects(): PublicProjectRecord[];
  listProjectRecords(): PublicProjectRecord[];
  getStats(): ProjectRegistryStats;
  resolveProject(project: string, options?: ProjectResolutionOptions): ProjectRegistrySnapshotResolution;
  hasBridgeToken(instanceId: string): boolean;
  findRecentReplacement(selector: string): ProjectRegistryRecentReplacement | undefined;
};

export type ProjectRegistryRecentReplacement = {
  readonly failed_record: PublicProjectRecord;
  readonly replacement_record: PublicProjectRecord;
};

export type RegistryOperationalState = "ok" | "over_capacity" | "unstable" | "unavailable" | "unsafe";

const projectResolutionSnapshots = new WeakMap<object, ProjectRegistrySnapshot>();

export function projectRegistrySnapshotForResolution(resolution: ProjectResolution): ProjectRegistrySnapshot | undefined {
  return projectResolutionSnapshots.get(resolution);
}

export type PublicRegistryOperationalStatus = {
  cache_hits: number;
  cache_misses: number;
  cache_entries: number;
  invalid_records: number;
  invalid_diagnostic_codes: RegistryInvalidDiagnosticCode[];
  invalid_diagnostics_truncated: boolean;
  scan_files: number;
  scan_overflow: number;
  scan_truncated: boolean;
  status: RegistryOperationalState;
};

export function publicRegistryOperationalStatus(stats: ProjectRegistryStats | undefined): PublicRegistryOperationalStatus {
  return {
    cache_hits: stats?.heartbeatCacheHitCount ?? 0,
    cache_misses: stats?.heartbeatCacheMissCount ?? 0,
    cache_entries: stats?.heartbeatCacheEntriesCount ?? 0,
    invalid_records: (stats?.invalidProjectRecordsCount ?? 0) + (stats?.protocolVersionMismatchProjectRecordsCount ?? 0),
    invalid_diagnostic_codes: stats?.invalidProjectRecordDiagnostics.map(({ code }) => code) ?? [],
    invalid_diagnostics_truncated: stats?.invalidProjectRecordDiagnosticsTruncated ?? false,
    scan_files: stats?.heartbeatScanFilesCount ?? 0,
    scan_overflow: stats?.heartbeatScanOverflowCount ?? 0,
    scan_truncated: stats?.heartbeatScanTruncated ?? false,
    status: stats?.registryOperationalState ?? "unavailable",
  };
}

type RegistryReadResult = {
  records: HeartbeatRecord[];
  invalidRecordsCount: number;
  protocolVersionMismatchRecordsCount: number;
  removedRecordsCount: number;
  cacheHitCount: number;
  cacheMissCount: number;
  invalidDiagnostics: Array<{ code: RegistryInvalidDiagnosticCode }>;
  invalidDiagnosticsTruncated: boolean;
  scanFilesCount: number;
  scanOverflowCount: number;
  scanTruncated: boolean;
  operationalState: RegistryOperationalState;
  processDeadRecordKeys: string[];
};

type FileIdentity = {
  dev: bigint;
  ino: bigint;
  size: bigint;
  mtimeNs: bigint;
  ctimeNs: bigint;
};

type CacheEntry = {
  identity: FileIdentity;
  record: HeartbeatRecord;
  cachedAtMs: number;
  seenScan: number;
};

type RecentInstanceEntry = {
  instanceId: string;
  project: string;
  canonicalProjectRoot: string;
  bridgePort: number;
  lastHeartbeat: string;
  status: HeartbeatRecord["status"];
  unityPid: number;
  processStartUtc: string | undefined;
  heartbeatGeneration: number | undefined;
  publicRecord: PublicProjectRecord;
  firstSeenScan: number;
  lastSeenScan: number;
  seenAtMs: number;
};

class ImmutableProjectRegistrySnapshot implements ProjectRegistrySnapshot {
  readonly captured_at: string;
  readonly capture_started_at: string;
  readonly capture_ended_at: string;
  readonly records: ReadonlyArray<PublicProjectRecord>;
  readonly #records: ReadonlyArray<HeartbeatRecord>;
  readonly #publicRecords: ReadonlyArray<PublicProjectRecord>;
  readonly #readResult: RegistryReadResult;
  readonly #cacheEntriesCount: number;
  readonly #recentInstances: ReadonlyMap<string, RecentInstanceEntry>;
  readonly #tokenPresentByInstanceId: ReadonlyMap<string, boolean>;

  constructor(readResult: RegistryReadResult, cacheEntriesCount: number, staleAfterMs: number, captureStartedAt: Date, captureEndedAt: Date, recentInstances: ReadonlyMap<string, RecentInstanceEntry>) {
    this.captured_at = captureStartedAt.toISOString();
    this.capture_started_at = captureStartedAt.toISOString();
    this.capture_ended_at = captureEndedAt.toISOString();
    this.#readResult = freezeSnapshotValue(structuredClone(readResult));
    this.#cacheEntriesCount = cacheEntriesCount;
    this.#records = freezeSnapshotValue(readResult.records.map((record) => structuredClone(record)));
    const deadProcessGenerations = new Set(readResult.processDeadRecordKeys);
    this.#publicRecords = freezeSnapshotValue(this.#records.map((record) => publicRecordAt(
      record,
      captureStartedAt.getTime(),
      staleAfterMs,
      deadProcessGenerations.has(processGenerationKey(record)),
    )));
    this.records = this.#publicRecords;
    this.#recentInstances = new Map([...recentInstances].map(([instanceId, entry]) => [instanceId, structuredClone(entry)]));
    this.#tokenPresentByInstanceId = new Map(this.#records.map((record) => [record.instance_id, Boolean(record.token)]));
    Object.freeze(this);
  }

  listProjects(): PublicProjectRecord[] {
    const liveProjectRoots = new Set(this.#publicRecords.filter((record) => !record.stale).map((record) => record.canonical_project_root));
    return cloneFrozenPublicRecords(this.#publicRecords.filter((record) => !record.stale || !liveProjectRoots.has(record.canonical_project_root)));
  }

  listProjectRecords(): PublicProjectRecord[] {
    return cloneFrozenPublicRecords(this.#publicRecords);
  }

  getStats(): ProjectRegistryStats {
    const liveRecords = this.#publicRecords.filter((record) => !record.stale);
    const staleRecords = this.#publicRecords.filter((record) => record.stale);
    const liveProjectIds = new Set(liveRecords.map((record) => record.project));
    const staleOnlyProjectIds = new Set(staleRecords.filter((record) => !liveProjectIds.has(record.project)).map((record) => record.project));

    return freezeSnapshotValue({
      projectRecordsCount: this.#publicRecords.length,
      liveProjectRecordsCount: liveRecords.length,
      staleProjectRecordsCount: staleRecords.length,
      invalidProjectRecordsCount: this.#readResult.invalidRecordsCount,
      protocolVersionMismatchProjectRecordsCount: this.#readResult.protocolVersionMismatchRecordsCount,
      removedProjectRecordsCount: this.#readResult.removedRecordsCount,
      uniqueProjectsCount: new Set(this.records.map((record) => record.project)).size,
      liveProjectsCount: liveProjectIds.size,
      staleProjectsCount: staleOnlyProjectIds.size,
      heartbeatCacheHitCount: this.#readResult.cacheHitCount,
      heartbeatCacheMissCount: this.#readResult.cacheMissCount,
      heartbeatCacheEntriesCount: this.#cacheEntriesCount,
      invalidProjectRecordDiagnostics: structuredClone(this.#readResult.invalidDiagnostics),
      invalidProjectRecordDiagnosticsTruncated: this.#readResult.invalidDiagnosticsTruncated,
      heartbeatScanFilesCount: this.#readResult.scanFilesCount,
      heartbeatScanOverflowCount: this.#readResult.scanOverflowCount,
      heartbeatScanTruncated: this.#readResult.scanTruncated,
      registryOperationalState: this.#readResult.operationalState,
    });
  }

  resolveProject(project: string, options: ProjectResolutionOptions = {}): ProjectRegistrySnapshotResolution {
    if (this.#readResult.operationalState !== "ok") return nonOperationalProjectResolution(this.#readResult.operationalState);
    const resolution = resolveProjectRecords(this.#publicRecords, project, options, (record) => record.stale, cloneFrozenPublicRecord, cloneFrozenPublicRecord);
    return resolution;
  }

  hasBridgeToken(instanceId: string): boolean {
    return this.#tokenPresentByInstanceId.get(instanceId) === true;
  }

  findRecentReplacement(selector: string): ProjectRegistryRecentReplacement | undefined {
    const currentInstanceIds = new Set(this.#publicRecords.map((record) => record.instance_id));
    const matchingFailedRecords = [...this.#recentInstances.values()]
      .filter((entry) => !currentInstanceIds.has(entry.instanceId) && matchesProjectSelector(entry.publicRecord, selector));
    if (matchingFailedRecords.length !== 1) return undefined;
    const failedRecord = matchingFailedRecords[0];
    if (!failedRecord) return undefined;
    const replacements = this.#publicRecords.filter((record) => !record.stale
      && record.instance_id !== failedRecord.instanceId
      && record.canonical_project_root === failedRecord.canonicalProjectRoot);
    if (replacements.length !== 1) return undefined;
    const replacementRecord = replacements[0];
    if (!replacementRecord) return undefined;
    const replacementEntry = this.#recentInstances.get(replacementRecord.instance_id);
    if (!replacementEntry || replacementEntry.firstSeenScan <= failedRecord.lastSeenScan) return undefined;
    return {
      failed_record: cloneFrozenPublicRecord(failedRecord.publicRecord),
      replacement_record: cloneFrozenPublicRecord(replacementRecord),
    };
  }
}

type DirectoryComponentIdentity = {
  path: string;
  dev: bigint;
  ino: bigint;
};

type DirectorySnapshot = {
  realPath: string;
  components: DirectoryComponentIdentity[];
};

class RegistryReadError extends Error {
  readonly code: RegistryInvalidDiagnosticCode;

  constructor(code: RegistryInvalidDiagnosticCode) {
    super(code);
    this.name = "RegistryReadError";
    this.code = code;
  }
}

class DirectoryReadError extends RegistryReadError {
  readonly retryable: boolean;

  constructor(code: Extract<RegistryInvalidDiagnosticCode, "unsafe_registry_directory" | "registry_directory_unavailable" | "registry_directory_unstable">, retryable = false) {
    super(code);
    this.name = "DirectoryReadError";
    this.retryable = retryable;
  }
}

class RegistryCapacityError extends Error {
  constructor() { super("registry_over_capacity"); this.name = "RegistryCapacityError"; }
}

export class ProjectRegistry {
  readonly #instancesDir: string;
  readonly #staleAfterMs: number;
  readonly #cleanupAfterMs: number | undefined;
  readonly #cacheMaxEntries: number;
  readonly #cacheTtlMs: number;
  readonly #invalidDiagnosticsMax: number;
  readonly #scanMaxFiles: number;
  readonly #now: () => Date;
  readonly #fileSystem: RegistryFileSystem;
  readonly #platform: NodeJS.Platform;
  readonly #processState: (pid: number, processStartUtc?: string, projectRoot?: string) => RegistryProcessState;
  readonly #processGenerationInspector: RegistryProcessGenerationInspector | undefined;
  readonly #cache = new Map<string, CacheEntry>();
  readonly #recentInstances = new Map<string, RecentInstanceEntry>();
  readonly #logger: OperationalLogger | undefined;
  #lastOperationalState: RegistryOperationalState = "ok";
  #scan = 0;

  constructor(options: ProjectRegistryOptions) {
    this.#instancesDir = path.isAbsolute(options.instancesDir) ? options.instancesDir : path.join(process.cwd(), options.instancesDir);
    this.#staleAfterMs = options.staleAfterMs;
    this.#cleanupAfterMs = options.cleanupAfterMs;
    this.#cacheMaxEntries = boundedInteger(options.cacheMaxEntries, DEFAULT_CACHE_ENTRIES, MAX_CACHE_ENTRIES);
    this.#cacheTtlMs = boundedInteger(options.cacheTtlMs, options.cleanupAfterMs ?? DEFAULT_CACHE_TTL_MS, MAX_CACHE_TTL_MS);
    this.#invalidDiagnosticsMax = boundedInteger(options.invalidDiagnosticsMax, DEFAULT_INVALID_DIAGNOSTICS, MAX_INVALID_DIAGNOSTICS);
    this.#scanMaxFiles = boundedInteger(options.scanMaxFiles, DEFAULT_SCAN_FILES, MAX_SCAN_FILES);
    if (this.#cacheMaxEntries < this.#scanMaxFiles) throw new TypeError("Registry cache_max_entries must be at least scan_max_files");
    this.#now = options.now ?? (() => new Date());
    this.#fileSystem = options.fileSystem ?? nodeFileSystem();
    this.#platform = this.#fileSystem.platform;
    this.#processState = options.processState ?? registryProcessState;
    this.#processGenerationInspector = options.processGenerationInspector;
    this.#logger = options.logger;
  }

  captureSnapshot(options: { cleanup?: boolean } = {}): ProjectRegistrySnapshot {
    const captureStartedAt = this.#now();
    const readResult = this.#readRecords(options, captureStartedAt.getTime());
    const captureEndedAt = this.#now();
    return new ImmutableProjectRegistrySnapshot(readResult, this.#cache.size, this.#staleAfterMs, captureStartedAt, captureEndedAt, this.#recentInstances);
  }

  listProjects(): PublicProjectRecord[] {
    return this.captureSnapshot().listProjects();
  }

  listProjectRecords(): PublicProjectRecord[] {
    return this.captureSnapshot().listProjectRecords();
  }

  queryStaleProjects(query: StaleProjectQuery = {}): StaleProjectPage {
    const snapshot = this.captureSnapshot();
    assertRegistryOperational(snapshot);
    return staleProjectPage(snapshot, query);
  }

  cleanupStaleRecords(args: StaleRegistryCleanupArgs = {}): StaleRegistryCleanupResult {
    if (this.#cleanupAfterMs === undefined) {
      throw new StaleRegistryOperationError("feature_disabled", "Stale registry cleanup requires registry.cleanup_after_ms");
    }
    if (this.#cleanupAfterMs <= this.#staleAfterMs) {
      throw new StaleRegistryOperationError("unsafe_binding_refused", "registry.cleanup_after_ms must be greater than registry.stale_after_ms");
    }

    const dryRun = args.dry_run !== false;
    if (!dryRun && args.confirm_cleanup !== true) {
      throw new StaleRegistryOperationError("confirmation_required", "Non-dry stale registry cleanup requires confirm_cleanup=true", {
        mutation_attempted: false,
        automatic_retry_allowed: false,
      });
    }
    if (!dryRun && (!args.record_ids?.length || !args.precondition_hash)) {
      throw new StaleRegistryOperationError("precondition_required", "Non-dry stale registry cleanup requires record_ids and precondition_hash", {
        mutation_attempted: false,
        next_valid_actions: ["run_sgg_unity_stale_registry_cleanup_with_dry_run_true"],
      });
    }

    const capturedAt = this.#now();
    const snapshot = this.captureSnapshot();
    assertRegistryOperational(snapshot);
    const limit = Math.min(args.limit ?? DEFAULT_STALE_CLEANUP_LIMIT, MAX_STALE_CLEANUP_LIMIT);
    const eligible = uniqueCleanupCandidates(cleanupCandidates(snapshot.records, capturedAt.getTime(), this.#cleanupAfterMs, args)
      .filter((record) => this.#matchesCanonicalCleanupFile(record, capturedAt.getTime())));
    const byId = new Map(eligible.map((record) => [staleRecordId(record), record]));
    let selected: PublicProjectRecord[];
    if (args.record_ids?.length) {
      selected = args.record_ids.map((recordId) => byId.get(recordId)).filter((record): record is PublicProjectRecord => record !== undefined);
      if (selected.length !== args.record_ids.length) {
        throw new StaleRegistryOperationError("precondition_failed", "One or more stale registry cleanup records are missing, changed, or no longer eligible", {
          mutation_attempted: false,
          requested_count: args.record_ids.length,
          matched_count: selected.length,
          automatic_retry_allowed: false,
        });
      }
    } else {
      selected = eligible.slice(0, limit);
    }

    const planned = selected.map((record) => cleanupRecord(record, capturedAt.getTime(), this.#processState(record.unity_pid, record.unity_process_start_utc, record.project_root)));
    const deletable = planned.filter((record) => record.process_state === "dead");
    const preconditionHash = deletable.length > 0 ? staleCleanupPreconditionHash(this.#cleanupAfterMs, deletable.map((record) => record.stale_record_id)) : null;
    if (!dryRun && args.precondition_hash !== preconditionHash) {
      throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup precondition changed", {
        mutation_attempted: false,
        expected_precondition_hash: preconditionHash,
        automatic_retry_allowed: false,
      });
    }
    if (!dryRun && deletable.length !== planned.length) {
      throw new StaleRegistryOperationError("precondition_failed", "Every selected stale registry record must have a confirmed dead owner process", {
        mutation_attempted: false,
        selected_count: planned.length,
        deletable_count: deletable.length,
        automatic_retry_allowed: false,
      });
    }

    let deletedCount = 0;
    if (!dryRun) {
      const verified = selected.map((record) => this.#verifyCleanupCandidate(record, capturedAt.getTime()));
      const verifiedHash = staleCleanupPreconditionHash(this.#cleanupAfterMs, verified.map(({ record }) => staleRecordId(record)));
      if (verifiedHash !== args.precondition_hash) {
        throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup records changed during final verification", {
          mutation_attempted: false,
          automatic_retry_allowed: false,
        });
      }
      for (const { record } of verified) {
        const fileName = `${record.instance_id}.json`;
        const filePath = path.join(this.#instancesDir, fileName);
        let finalVerification: { record: PublicProjectRecord; identity: SecureFileStat };
        try {
          finalVerification = this.#verifyCleanupCandidate(record, capturedAt.getTime());
        } catch (error) {
          if (deletedCount === 0) throw error;
          throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup stopped because a remaining record changed", {
            mutation_attempted: true,
            deleted_count_confirmed: deletedCount,
            delivery_state: "partial",
            automatic_retry_allowed: false,
            next_valid_actions: ["do_not_retry_cleanup_automatically", "run_a_fresh_dry_run_before_any_further_cleanup"],
          });
        }
        try {
          durableUnlinkFile(this.#instancesDir, filePath, { expectedIdentity: finalVerification.identity });
          this.#cache.delete(fileName);
          deletedCount += 1;
        } catch {
          throw new StaleRegistryOperationError("internal_error", "Stale registry cleanup stopped after a storage failure", {
            mutation_attempted: true,
            deleted_count_confirmed: deletedCount,
            delivery_state: "uncertain",
            automatic_retry_allowed: false,
            next_valid_actions: ["do_not_retry_cleanup_automatically", "run_a_fresh_dry_run_before_any_further_cleanup"],
          });
        }
      }
    }

    const records = planned.map((record) => ({
      ...record,
      action: !dryRun && record.process_state === "dead" ? "deleted" as const : record.action,
    }));
    return {
      schema_version: 1,
      operation: "stale_registry_cleanup",
      dry_run: dryRun,
      mutation_attempted: !dryRun && selected.length > 0,
      cleanup_after_ms: this.#cleanupAfterMs,
      candidate_count: eligible.length,
      selected_count: selected.length,
      would_delete_count: deletable.length,
      deleted_count: deletedCount,
      skipped_count: planned.length - deletable.length,
      precondition_hash: preconditionHash,
      records,
      truncated: !args.record_ids?.length && eligible.length > selected.length,
      limits: { default_limit: DEFAULT_STALE_CLEANUP_LIMIT, max_delete: MAX_STALE_CLEANUP_LIMIT },
      safety: {
        invalid_records_deleted: false,
        live_records_deleted: false,
        project_files_deleted: false,
        arbitrary_paths_accepted: false,
      },
      next_valid_actions: dryRun
        ? deletable.length > 0
          ? ["obtain_exact_human_confirmation", "rerun_with_dry_run_false_confirm_cleanup_true_record_ids_and_precondition_hash"]
          : ["no_cleanup_action_required"]
        : ["call_sgg_unity_projects", "do_not_retry_cleanup_automatically_after_uncertain_delivery"],
    };
  }

  close(): void {
    // Scans retain no filesystem handles.
  }

  [Symbol.dispose](): void {
    this.close();
  }

  getStats(options: { cleanup?: boolean } = {}): ProjectRegistryStats {
    return this.captureSnapshot(options).getStats();
  }

  resolveProject(project: string, options: ProjectResolutionOptions = {}): ProjectResolution {
    const captureStartedAt = this.#now();
    const capturedAtMs = captureStartedAt.getTime();
    const readResult = this.#readRecords({}, capturedAtMs);
    const deadProcessGenerations = new Set(readResult.processDeadRecordKeys);
    const resolution = readResult.operationalState !== "ok"
      ? nonOperationalProjectResolution(readResult.operationalState)
      : resolveProjectRecords(
        readResult.records,
        project,
        options,
        (record) => this.#isStale(record, capturedAtMs, deadProcessGenerations),
        (record) => structuredClone(record),
        (record) => this.#toPublicRecord(record, capturedAtMs, deadProcessGenerations),
      );
    if (resolution.status === "error") {
      const captureEndedAt = this.#now();
      projectResolutionSnapshots.set(resolution, new ImmutableProjectRegistrySnapshot(readResult, this.#cache.size, this.#staleAfterMs, captureStartedAt, captureEndedAt, this.#recentInstances));
    }
    return resolution;
  }

  resolveReplacementProject(canonicalProjectRoot: string, failedInstanceId: string): ReplacementProjectResolution {
    const capturedAtMs = this.#now().getTime();
    const readResult = this.#readRecords({}, capturedAtMs);
    const deadProcessGenerations = new Set(readResult.processDeadRecordKeys);
    const candidates = readResult.records.filter((record) => !this.#isStale(record, capturedAtMs, deadProcessGenerations)
      && record.instance_id !== failedInstanceId
      && record.canonical_project_root === canonicalProjectRoot);
    if (candidates.length === 0) return { status: "error", code: "project_not_found" };
    if (candidates.length !== 1) return { status: "error", code: "duplicate_project_id" };
    const record = candidates[0];
    return record ? { status: "ok", record: structuredClone(record) } : { status: "error", code: "project_not_found" };
  }

  resolveVerificationProject(canonicalProjectRoot: string, failedInstanceId: string): VerificationProjectResolution {
    const capturedAtMs = this.#now().getTime();
    const readResult = this.#readRecords({}, capturedAtMs);
    const deadProcessGenerations = new Set(readResult.processDeadRecordKeys);
    const candidates = readResult.records.filter((record) => !this.#isStale(record, capturedAtMs, deadProcessGenerations)
      && record.canonical_project_root === canonicalProjectRoot);
    const replacements = candidates.filter((record) => record.instance_id !== failedInstanceId);
    if (replacements.length > 1) return { status: "error", code: "duplicate_project_id" };
    const replacement = replacements[0];
    if (replacement) return { status: "ok", record: structuredClone(replacement), role: "replacement" };

    const current = candidates.filter((record) => record.instance_id === failedInstanceId);
    if (current.length !== 1) {
      return { status: "error", code: current.length > 1 ? "duplicate_project_id" : "project_not_found" };
    }
    const record = current[0];
    return record ? { status: "ok", record: structuredClone(record), role: "current" } : { status: "error", code: "project_not_found" };
  }

  resolveRecentReplacementProject(failedInstanceSelector: string): RecentReplacementProjectResolution {
    const resolution = this.resolveRecentReplacementRecord(failedInstanceSelector);
    if (resolution.status === "error") return resolution;
    return {
      status: "ok",
      failedRecord: resolution.failedRecord,
      replacementRecord: this.#recentProjectIdentity(resolution.replacementRecord),
    };
  }

  resolveRecentReplacementRecord(failedInstanceSelector: string): RecentReplacementRecordResolution {
    this.#pruneRecentInstances();
    const failed = this.#recentInstances.get(failedInstanceSelector);
    if (!failed) return { status: "error", code: "project_not_found" };
    const replacement = this.resolveReplacementProject(failed.canonicalProjectRoot, failed.instanceId);
    if (replacement.status === "error") return replacement;
    return {
      status: "ok",
      failedRecord: {
        instance_id: failed.instanceId,
        project: failed.project,
        bridge_port: failed.bridgePort,
        last_heartbeat: failed.lastHeartbeat,
        status: failed.status,
        stale: failed.publicRecord.stale || this.#now().getTime() - new Date(failed.lastHeartbeat).getTime() > this.#staleAfterMs,
      },
      replacementRecord: replacement.record,
    };
  }

  #verifyCleanupCandidate(expected: PublicProjectRecord, capturedAtMs: number): { record: PublicProjectRecord; identity: SecureFileStat } {
    if (!SAFE_INSTANCE_ID.test(expected.instance_id)) {
      throw new StaleRegistryOperationError("unsafe_binding_refused", "Stale registry record has an unsafe instance identity", { mutation_attempted: false });
    }
    const filePath = path.join(this.#instancesDir, `${expected.instance_id}.json`);
    const stat = secureFileStat(this.#instancesDir, filePath);
    if (!stat || stat.size > BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes) {
      throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup file is missing or changed", { mutation_attempted: false });
    }
    let parsed: HeartbeatRecord;
    try {
      parsed = parseHeartbeatRecordV1(JSON.parse(readSecureFile(this.#instancesDir, filePath, BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes).toString("utf8")));
    } catch {
      throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup file is no longer a valid matching heartbeat", { mutation_attempted: false });
    }
    const finalStat = secureFileStat(this.#instancesDir, filePath);
    if (!finalStat
      || !sameFileObjectIdentity(stat, finalStat, process.platform)
      || stat.size !== finalStat.size
      || stat.mtimeMs !== finalStat.mtimeMs) {
      throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup file changed during final verification", { mutation_attempted: false });
    }
    const current = publicRecordAt(parsed, capturedAtMs, this.#staleAfterMs);
    if (staleRecordId(current) !== staleRecordId(expected)
      || !current.stale
      || capturedAtMs - Date.parse(current.last_heartbeat) <= this.#cleanupAfterMs!
      || this.#processState(current.unity_pid, current.unity_process_start_utc, current.project_root) !== "dead") {
      throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup record refreshed or its owner process is not confirmed dead", {
        mutation_attempted: false,
        automatic_retry_allowed: false,
      });
    }
    return { record: current, identity: finalStat };
  }

  #matchesCanonicalCleanupFile(expected: PublicProjectRecord, capturedAtMs: number): boolean {
    if (!SAFE_INSTANCE_ID.test(expected.instance_id)) return false;
    const filePath = path.join(this.#instancesDir, `${expected.instance_id}.json`);
    try {
      const parsed = parseHeartbeatRecordV1(JSON.parse(readSecureFile(this.#instancesDir, filePath, BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes).toString("utf8")));
      return staleRecordId(publicRecordAt(parsed, capturedAtMs, this.#staleAfterMs)) === staleRecordId(expected);
    } catch {
      return false;
    }
  }

  #readRecords(options: { cleanup?: boolean } = {}, capturedAtMs = this.#now().getTime()): RegistryReadResult {
    for (let attempt = 0; attempt < 2; attempt += 1) {
      const result = emptyReadResult();
      const scan = this.#scan + 1;
      const cache = cloneCache(this.#cache);
      try {
        const directory = this.#captureDirectory();
        this.#assertDirectory(directory);
        const entries = this.#readCompleteDirectorySnapshot(directory);
        this.#assertDirectory(directory);
        result.scanFilesCount = entries.length;

        const recordsByFile = new Map<string, HeartbeatRecord>();
        for (const entry of entries) {
          this.#assertDirectory(directory);
          const filePath = path.join(this.#instancesDir, entry.name);
          let before: FileIdentity;
          try {
            const stat = this.#fileSystem.lstatSync(filePath, { bigint: true });
            this.#assertDirectory(directory);
            if (!entry.isFile() || !stat.isFile() || stat.isSymbolicLink()) throw new RegistryReadError("unsafe_heartbeat_file");
            before = identity(stat);
          } catch (error) {
            if (error instanceof DirectoryReadError) throw error;
            cache.delete(entry.name);
            this.#addInvalid(result, safeReadCode(error, "unsafe_heartbeat_file"));
            continue;
          }

          const nowMs = capturedAtMs;
          const cached = cache.get(entry.name);
          let record: HeartbeatRecord;
          if (identityCertain(before, this.#platform) && cached && sameIdentity(cached.identity, before) && nowMs - cached.cachedAtMs <= this.#cacheTtlMs) {
            result.cacheHitCount += 1;
            cached.seenScan = scan;
            touchCache(cache, entry.name, cached);
            record = structuredClone(cached.record);
          } else {
            result.cacheMissCount += 1;
            cache.delete(entry.name);
            try {
              const loaded = this.#readStableRecord(filePath, directory);
              record = loaded.record;
              if (identityCertain(loaded.identity, this.#platform)) cacheRecord(cache, this.#cacheMaxEntries, entry.name, loaded.identity, record, nowMs, scan);
            } catch (error) {
              if (error instanceof DirectoryReadError) throw error;
              const code = this.#shouldIgnoreExpiredIdentity(before, options.cleanup, capturedAtMs) ? "heartbeat_expired" : safeReadCode(error, "heartbeat_unreadable");
              this.#addInvalid(result, code);
              continue;
            }
          }

          if (this.#shouldCleanupRecord(record, options.cleanup, capturedAtMs)) {
            cache.delete(entry.name);
            this.#addInvalid(result, "heartbeat_expired");
            continue;
          }
          recordsByFile.set(entry.name, record);
        }

        this.#assertDirectory(directory);
        for (const [name, cached] of cache) {
          if (cached.seenScan !== scan || capturedAtMs - cached.cachedAtMs > this.#cacheTtlMs || this.#shouldCleanupRecord(cached.record, options.cleanup, capturedAtMs)) cache.delete(name);
        }
        result.records = sortedRecords(recordsByFile);
        result.processDeadRecordKeys = this.#inspectProcessGenerations(result.records);
        this.#rememberRecentInstances(result.records, capturedAtMs, scan, new Set(result.processDeadRecordKeys));
        this.#cache.clear();
        for (const [name, cached] of cache) this.#cache.set(name, cached);
        this.#scan = scan;
        return this.#observe(result);
      } catch (error) {
        if (error instanceof RegistryCapacityError) {
          this.#cache.clear();
          return this.#observe(capacityReadResult());
        }
        const failure = error instanceof DirectoryReadError ? error : new DirectoryReadError("registry_directory_unavailable");
        if (attempt === 0 && failure.code !== "unsafe_registry_directory") continue;
        this.#cache.clear();
        const failed = emptyReadResult();
        failed.operationalState = directoryOperationalState(failure.code);
        if (failure.code === "unsafe_registry_directory") this.#addInvalid(failed, failure.code);
        return this.#observe(failed);
      }
    }
    const result = emptyReadResult();
    this.#cache.clear();
    result.operationalState = "unavailable";
    return this.#observe(result);
  }

  #rememberRecentInstances(records: HeartbeatRecord[], seenAtMs: number, scan: number, deadProcessGenerations: ReadonlySet<string>): void {
    this.#pruneRecentInstances(seenAtMs);
    for (const record of records) {
      const previous = this.#recentInstances.get(record.instance_id);
      this.#recentInstances.delete(record.instance_id);
      this.#recentInstances.set(record.instance_id, {
        instanceId: record.instance_id,
        project: record.project,
        canonicalProjectRoot: record.canonical_project_root,
        bridgePort: record.bridge_port,
        lastHeartbeat: record.last_heartbeat,
        status: record.status,
        unityPid: record.unity_pid,
        processStartUtc: record.unity_process_start_utc,
        heartbeatGeneration: record.heartbeat_generation,
        publicRecord: this.#toPublicRecord(record, seenAtMs, deadProcessGenerations),
        firstSeenScan: previous?.firstSeenScan ?? scan,
        lastSeenScan: scan,
        seenAtMs,
      });
    }
    while (this.#recentInstances.size > this.#cacheMaxEntries) {
      const oldest = this.#recentInstances.keys().next().value;
      if (typeof oldest !== "string") break;
      this.#recentInstances.delete(oldest);
    }
  }

  #pruneRecentInstances(nowMs = this.#now().getTime()): void {
    for (const [instanceId, entry] of this.#recentInstances) {
      if (nowMs - entry.seenAtMs > this.#cacheTtlMs) this.#recentInstances.delete(instanceId);
    }
  }

  #recentProjectIdentity(record: HeartbeatRecord): RecentProjectIdentity {
    return {
      instance_id: record.instance_id,
      project: record.project,
      bridge_port: record.bridge_port,
      last_heartbeat: record.last_heartbeat,
      status: record.status,
      stale: this.#isStale(record),
    };
  }

  #inspectProcessGenerations(records: HeartbeatRecord[]): string[] {
    if (!this.#processGenerationInspector) return [];
    const identities = records
      .map(processGenerationIdentity)
      .filter((identity): identity is RegistryProcessGenerationIdentity => identity !== undefined);
    if (identities.length === 0) return [];
    const requested = new Set(identities.map(processGenerationKey));
    let observations: ReadonlyArray<RegistryProcessGenerationObservation>;
    try {
      const input = Object.freeze(identities.map((identity) => Object.freeze({ ...identity })));
      observations = this.#processGenerationInspector(input);
    } catch {
      return [];
    }
    if (!Array.isArray(observations) || observations.length !== identities.length) return [];
    const states = new Map<string, RegistryProcessState>();
    const ambiguous = new Set<string>();
    for (const observation of observations) {
      if (!isProcessGenerationObservation(observation)) continue;
      const key = processGenerationKey(observation);
      if (!requested.has(key) || !isRegistryProcessState(observation.state)) continue;
      if (states.has(key)) {
        ambiguous.add(key);
        continue;
      }
      states.set(key, observation.state);
    }
    return identities
      .map(processGenerationKey)
      .filter((key) => !ambiguous.has(key) && states.get(key) === "dead");
  }

  #observe(result: RegistryReadResult): RegistryReadResult {
    if (result.operationalState === this.#lastOperationalState) return result;
    const invalidRecords = result.invalidRecordsCount + result.protocolVersionMismatchRecordsCount;
    if (result.operationalState === "ok") {
      this.#logger?.log("SGG-OP-REGISTRY-RECOVERED-001", result.scanFilesCount, invalidRecords);
    } else {
      this.#logger?.log("SGG-OP-REGISTRY-STATE-001", result.operationalState, result.scanFilesCount, result.scanOverflowCount, invalidRecords);
    }
    this.#lastOperationalState = result.operationalState;
    return result;
  }

  #readCompleteDirectorySnapshot(directory: DirectorySnapshot): Dirent[] {
    let handle: RegistryDirectory | undefined;
    const entries: Dirent[] = [];
    let observedEntries = 0;
    try {
      try { handle = this.#fileSystem.opendirSync(this.#instancesDir); }
      catch {
        this.#assertDirectory(directory);
        throw new DirectoryReadError("registry_directory_unavailable", true);
      }
      this.#assertDirectory(directory);
      while (true) {
        let entry: Dirent | null;
        try { entry = handle.readSync(); }
        catch { throw new DirectoryReadError("registry_directory_unavailable", true); }
        if (entry === null) break;
        observedEntries += 1;
        if (observedEntries > this.#scanMaxFiles + SCAN_ENTRY_SLACK) throw new RegistryCapacityError();
        if (!safeHeartbeatName(entry.name)) continue;
        entries.push(entry);
        if (entries.length > this.#scanMaxFiles) throw new RegistryCapacityError();
      }
      this.#assertDirectory(directory);
      return entries;
    } catch (error) {
      if (error instanceof RegistryCapacityError) this.#assertDirectory(directory);
      throw error;
    } finally {
      if (handle) {
        try { handle.closeSync(); }
        catch { throw new DirectoryReadError("registry_directory_unavailable", true); }
      }
    }
  }

  #readStableRecord(filePath: string, directory: DirectorySnapshot): { record: HeartbeatRecord; identity: FileIdentity } {
    let failure: RegistryReadError | undefined;
    for (let attempt = 0; attempt < READ_ATTEMPTS; attempt += 1) {
      let handle: number | undefined;
      try {
        this.#assertDirectory(directory);
        const beforeStat = this.#fileSystem.lstatSync(filePath, { bigint: true });
        if (!beforeStat.isFile() || beforeStat.isSymbolicLink()) throw new RegistryReadError("unsafe_heartbeat_file");
        const before = identity(beforeStat);
        if (!identityCertain(before, this.#platform)) throw new RegistryReadError("unsafe_heartbeat_file");
        if (before.size > BigInt(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes)) throw new RegistryReadError("heartbeat_too_large");
        this.#assertDirectory(directory);
        handle = this.#fileSystem.openSync(filePath, fs.constants.O_RDONLY | NO_FOLLOW);
        const openedStat = this.#fileSystem.fstatSync(handle, { bigint: true });
        const opened = identity(openedStat);
        if (!openedStat.isFile() || !identityCertain(opened, this.#platform)) throw new RegistryReadError("unsafe_heartbeat_file");
        if (!samePathAndDescriptorIdentity(before, opened, this.#platform)) throw new RegistryReadError("heartbeat_unstable");
        if (openedStat.size > BigInt(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes)) throw new RegistryReadError("heartbeat_too_large");
        const readBuffer = Buffer.alloc(BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes + 1);
        let bytesRead = 0;
        while (bytesRead < readBuffer.byteLength) {
          const count = this.#fileSystem.readSync(handle, readBuffer, bytesRead, readBuffer.byteLength - bytesRead, null);
          if (!Number.isSafeInteger(count) || count < 0 || count > readBuffer.byteLength - bytesRead) throw new RegistryReadError("heartbeat_unreadable");
          if (count === 0) break;
          bytesRead += count;
        }
        if (bytesRead > BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes) throw new RegistryReadError("heartbeat_too_large");
        const heartbeatBytes = readBuffer.subarray(0, bytesRead);
        const openedAfterStat = this.#fileSystem.fstatSync(handle, { bigint: true });
        const openedAfter = identity(openedAfterStat);
        if (!openedAfterStat.isFile() || !identityCertain(openedAfter, this.#platform)) throw new RegistryReadError("unsafe_heartbeat_file");
        const pathAfterStat = this.#fileSystem.lstatSync(filePath, { bigint: true });
        this.#assertDirectory(directory);
        if (!pathAfterStat.isFile() || pathAfterStat.isSymbolicLink()) throw new RegistryReadError("unsafe_heartbeat_file");
        const pathAfter = identity(pathAfterStat);
        if (!identityCertain(pathAfter, this.#platform)) throw new RegistryReadError("unsafe_heartbeat_file");
        if (!sameIdentity(before, pathAfter) || !sameIdentity(opened, openedAfter)
          || !samePathAndDescriptorIdentity(pathAfter, openedAfter, this.#platform) || BigInt(heartbeatBytes.byteLength) !== pathAfter.size) {
          failure = new RegistryReadError("heartbeat_unstable");
          continue;
        }
        if (heartbeatBytes.byteLength > BRIDGE_PROTOCOL_BOUNDS.heartbeatBytes) throw new RegistryReadError("heartbeat_too_large");
        let parsed: unknown;
        try { parsed = JSON.parse(heartbeatBytes.toString("utf8")); }
        catch { throw new RegistryReadError("heartbeat_invalid_json"); }
        try { return { record: parseHeartbeatRecordV1(parsed), identity: pathAfter }; }
        catch (error) {
          if (error instanceof BridgeProtocolError && error.code === "protocol_version_mismatch") throw new RegistryReadError("protocol_version_mismatch");
          throw new RegistryReadError("heartbeat_invalid_schema");
        }
      } catch (error) {
        const safe = error instanceof RegistryReadError ? error : new RegistryReadError("heartbeat_unreadable");
        if (safe.code !== "heartbeat_unstable") throw safe;
        failure = safe;
      } finally {
        if (handle !== undefined) {
          try { this.#fileSystem.closeSync(handle); }
          catch { throw new RegistryReadError("heartbeat_unreadable"); }
        }
      }
    }
    throw failure ?? new RegistryReadError("heartbeat_unstable");
  }

  #captureDirectory(): DirectorySnapshot {
    const components: string[] = [];
    let current = this.#instancesDir;
    while (true) {
      components.push(current);
      const parent = path.dirname(current);
      if (parent === current) break;
      current = parent;
    }
    components.reverse();
    const identities: DirectoryComponentIdentity[] = [];
    for (const [index, component] of components.entries()) {
      try {
        const stat = this.#fileSystem.lstatSync(component, { bigint: true });
        if (!stat.isDirectory() || stat.isSymbolicLink() || !directoryIdentityCertain(stat, this.#platform)) throw new DirectoryReadError("unsafe_registry_directory");
        if (index === components.length - 1 && !privateRegistryDirectory(stat, this.#platform)) throw new DirectoryReadError("unsafe_registry_directory");
        identities.push({ path: component, dev: stat.dev, ino: stat.ino });
      } catch (error) {
        if (error instanceof DirectoryReadError) throw error;
        throw new DirectoryReadError(isMissing(error) ? "registry_directory_unavailable" : "unsafe_registry_directory");
      }
    }
    let realPath: string;
    try { realPath = this.#fileSystem.realpathSync(this.#instancesDir); }
    catch { throw new DirectoryReadError("registry_directory_unavailable"); }
    return { realPath, components: identities };
  }

  #assertDirectory(expected: DirectorySnapshot): void {
    let current: DirectorySnapshot;
    try { current = this.#captureDirectory(); }
    catch { throw new DirectoryReadError("registry_directory_unstable"); }
    if (!sameDirectory(expected, current)) throw new DirectoryReadError("registry_directory_unstable");
  }

  #addInvalid(result: RegistryReadResult, code: RegistryInvalidDiagnosticCode): void {
    if (code === "protocol_version_mismatch") result.protocolVersionMismatchRecordsCount += 1;
    else result.invalidRecordsCount += 1;
    if (result.invalidDiagnostics.length < this.#invalidDiagnosticsMax) result.invalidDiagnostics.push({ code });
    else result.invalidDiagnosticsTruncated = true;
  }

  #toPublicRecord(record: HeartbeatRecord, capturedAtMs = this.#now().getTime(), deadProcessGenerations: ReadonlySet<string> = new Set()): PublicProjectRecord {
    return publicRecordAt(record, capturedAtMs, this.#staleAfterMs, deadProcessGenerations.has(processGenerationKey(record)));
  }

  #isStale(record: HeartbeatRecord, capturedAtMs = this.#now().getTime(), deadProcessGenerations: ReadonlySet<string> = new Set()): boolean {
    return deadProcessGenerations.has(processGenerationKey(record))
      || capturedAtMs - Date.parse(record.last_heartbeat) > this.#staleAfterMs;
  }

  #shouldCleanupRecord(record: HeartbeatRecord, cleanup: boolean | undefined, capturedAtMs = this.#now().getTime()): boolean {
    return Boolean(cleanup && this.#cleanupAfterMs && capturedAtMs - Date.parse(record.last_heartbeat) > this.#cleanupAfterMs);
  }

  #shouldIgnoreExpiredIdentity(fileIdentity: FileIdentity, cleanup: boolean | undefined, capturedAtMs = this.#now().getTime()): boolean {
    return Boolean(cleanup && this.#cleanupAfterMs && capturedAtMs - Number(fileIdentity.mtimeNs / 1_000_000n) > this.#cleanupAfterMs);
  }
}

function assertRegistryOperational(snapshot: ProjectRegistrySnapshot): void {
  const status = snapshot.getStats().registryOperationalState;
  if (status !== "ok") {
    throw new StaleRegistryOperationError("unsafe_binding_refused", "Registry state is not complete and safe for stale record access", {
      registry_operational_state: status,
      mutation_attempted: false,
    });
  }
}

function staleProjectPage(snapshot: ProjectRegistrySnapshot, query: StaleProjectQuery): StaleProjectPage {
  const limit = Math.min(query.limit ?? DEFAULT_STALE_QUERY_LIMIT, MAX_STALE_QUERY_LIMIT);
  const filters = normalizedStaleFilters(query);
  const records = snapshot.records
    .filter((record) => record.stale && matchesStaleFilters(record, filters))
    .map((record) => ({ ...cloneFrozenPublicRecord(record), stale_record_id: staleRecordId(record) }))
    .sort(compareStaleProjectRecords);
  const queryHash = sha256(STALE_QUERY_CURSOR_DOMAIN, canonicalJsonStringify(filters));
  const snapshotHash = sha256(STALE_QUERY_CURSOR_DOMAIN, canonicalJsonStringify(records.map((record) => record.stale_record_id)));
  const offset = query.cursor ? decodeStaleCursor(query.cursor, queryHash, snapshotHash, records.length) : 0;
  const projects = records.slice(offset, offset + limit);
  const nextOffset = offset + projects.length;
  const nextCursor = nextOffset < records.length ? encodeStaleCursor({ v: STALE_QUERY_CURSOR_VERSION, q: queryHash, s: snapshotHash, o: nextOffset }) : null;
  return freezeSnapshotValue({
    schema_version: 1 as const,
    projects,
    returned_count: projects.length,
    total_available: records.length,
    captured_at: snapshot.captured_at,
    next_cursor: nextCursor,
    truncated: nextCursor !== null,
    limits: { default_limit: DEFAULT_STALE_QUERY_LIMIT, max_limit: MAX_STALE_QUERY_LIMIT },
  });
}

type NormalizedStaleFilters = {
  project: string | null;
  status: HeartbeatRecord["status"] | null;
  last_heartbeat_from: string | null;
  last_heartbeat_to: string | null;
};

function normalizedStaleFilters(query: StaleProjectQuery): NormalizedStaleFilters {
  return {
    project: query.project ?? null,
    status: query.status ?? null,
    last_heartbeat_from: query.last_heartbeat_from ? new Date(query.last_heartbeat_from).toISOString() : null,
    last_heartbeat_to: query.last_heartbeat_to ? new Date(query.last_heartbeat_to).toISOString() : null,
  };
}

function matchesStaleFilters(record: PublicProjectRecord, filters: NormalizedStaleFilters): boolean {
  const lastHeartbeatMs = Date.parse(record.last_heartbeat);
  return (filters.project === null || matchesProjectSelector(record, filters.project))
    && (filters.status === null || record.status === filters.status)
    && (filters.last_heartbeat_from === null || lastHeartbeatMs >= Date.parse(filters.last_heartbeat_from))
    && (filters.last_heartbeat_to === null || lastHeartbeatMs <= Date.parse(filters.last_heartbeat_to));
}

function compareStaleProjectRecords(left: StaleProjectListRecord, right: StaleProjectListRecord): number {
  return compareCodePoints(right.last_heartbeat, left.last_heartbeat)
    || compareCodePoints(left.project, right.project)
    || compareCodePoints(left.instance_id, right.instance_id)
    || compareCodePoints(left.canonical_project_root, right.canonical_project_root)
    || compareCodePoints(left.stale_record_id, right.stale_record_id);
}

type StaleCursor = { v: number; q: string; s: string; o: number };

function encodeStaleCursor(cursor: StaleCursor): string {
  return Buffer.from(canonicalJsonStringify(cursor), "utf8").toString("base64url");
}

function decodeStaleCursor(value: string, queryHash: string, snapshotHash: string, recordCount: number): number {
  let parsed: unknown;
  try {
    const bytes = Buffer.from(value, "base64url");
    if (bytes.toString("base64url") !== value) throw new Error("noncanonical_cursor");
    parsed = JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new StaleRegistryOperationError("invalid_field_value", "Stale project cursor is malformed", { restart_without_cursor: true });
  }
  if (!isStaleCursor(parsed)
    || parsed.v !== STALE_QUERY_CURSOR_VERSION
    || parsed.q !== queryHash
    || parsed.s !== snapshotHash
    || parsed.o < 0
    || parsed.o > recordCount) {
    throw new StaleRegistryOperationError("invalid_field_value", "Stale project cursor does not match the current query snapshot", { restart_without_cursor: true });
  }
  return parsed.o;
}

function isStaleCursor(value: unknown): value is StaleCursor {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const record = value as Record<string, unknown>;
  return Object.keys(record).sort().join(",") === "o,q,s,v"
    && Number.isSafeInteger(record.v)
    && typeof record.q === "string"
    && typeof record.s === "string"
    && Number.isSafeInteger(record.o);
}

function cleanupCandidates(records: ReadonlyArray<PublicProjectRecord>, capturedAtMs: number, cleanupAfterMs: number, args: StaleRegistryCleanupArgs): PublicProjectRecord[] {
  const lastHeartbeatBeforeMs = args.last_heartbeat_before === undefined ? undefined : Date.parse(args.last_heartbeat_before);
  return records
    .filter((record) => record.stale
      && capturedAtMs - Date.parse(record.last_heartbeat) > cleanupAfterMs
      && (args.project === undefined || matchesProjectSelector(record, args.project))
      && (args.status === undefined || record.status === args.status)
      && (lastHeartbeatBeforeMs === undefined || Date.parse(record.last_heartbeat) <= lastHeartbeatBeforeMs)
      && SAFE_INSTANCE_ID.test(record.instance_id))
    .map(cloneFrozenPublicRecord)
    .sort((left, right) => compareStaleProjectRecords(
      { ...left, stale_record_id: staleRecordId(left) },
      { ...right, stale_record_id: staleRecordId(right) },
    ));
}

function cleanupRecord(record: PublicProjectRecord, capturedAtMs: number, processState: RegistryProcessState): StaleRegistryCleanupRecord {
  return {
    stale_record_id: staleRecordId(record),
    project: record.project,
    instance_id: record.instance_id,
    status: record.status,
    last_heartbeat: record.last_heartbeat,
    age_ms: Math.max(0, capturedAtMs - Date.parse(record.last_heartbeat)),
    heartbeat_generation: record.heartbeat_generation ?? null,
    process_state: processState,
    action: processState === "dead" ? "would_delete" : processState === "live" ? "skipped_process_live" : "skipped_process_uncertain",
  };
}

function uniqueCleanupCandidates(records: PublicProjectRecord[]): PublicProjectRecord[] {
  const unique = new Map<string, PublicProjectRecord>();
  for (const record of records) unique.set(staleRecordId(record), record);
  return [...unique.values()];
}

function staleRecordId(record: PublicProjectRecord): string {
  return sha256(STALE_RECORD_ID_DOMAIN, canonicalJsonStringify(record));
}

function staleCleanupPreconditionHash(cleanupAfterMs: number, recordIds: string[]): string {
  return sha256(STALE_CLEANUP_PRECONDITION_DOMAIN, canonicalJsonStringify({
    cleanup_after_ms: cleanupAfterMs,
    record_ids: [...recordIds].sort(compareCodePoints),
  }));
}

function sha256(domain: string, value: string): string {
  return `sha256:${createHash("sha256").update(domain).update(value, "utf8").digest("hex")}`;
}

function registryProcessState(pid: number, _processStartUtc?: string, projectRoot?: string): RegistryProcessState {
  if (process.platform !== "win32" && typeof projectRoot === "string" && /^[A-Za-z]:[\\/]/.test(projectRoot)) return "uncertain";
  try {
    process.kill(pid, 0);
    return "live";
  } catch (error) {
    return isErrorCodeValue(error, "ESRCH") ? "dead" : "uncertain";
  }
}

function isErrorCodeValue(error: unknown, code: string): boolean {
  return typeof error === "object" && error !== null && "code" in error && String(error.code) === code;
}

function emptyReadResult(): RegistryReadResult {
  return { records: [], invalidRecordsCount: 0, protocolVersionMismatchRecordsCount: 0, removedRecordsCount: 0, cacheHitCount: 0, cacheMissCount: 0, invalidDiagnostics: [], invalidDiagnosticsTruncated: false, scanFilesCount: 0, scanOverflowCount: 0, scanTruncated: false, operationalState: "ok", processDeadRecordKeys: [] };
}

function capacityReadResult(): RegistryReadResult {
  return { ...emptyReadResult(), scanOverflowCount: 1, scanTruncated: true, operationalState: "over_capacity" };
}

function directoryOperationalState(code: RegistryInvalidDiagnosticCode): RegistryOperationalState {
  if (code === "registry_directory_unstable") return "unstable";
  if (code === "unsafe_registry_directory") return "unsafe";
  return "unavailable";
}

function identity(stats: BigIntStats): FileIdentity {
  return { dev: stats.dev, ino: stats.ino, size: stats.size, mtimeNs: stats.mtimeNs, ctimeNs: stats.ctimeNs };
}

function identityCertain(value: FileIdentity, platform: NodeJS.Platform): boolean {
  return deviceAndInodeCertain(value.dev, value.ino, platform) && value.size >= 0n && value.mtimeNs > 0n && value.ctimeNs > 0n;
}

function sameIdentity(left: FileIdentity | undefined, right: FileIdentity | undefined): boolean {
  return left !== undefined && right !== undefined && left.dev === right.dev && left.ino === right.ino && left.size === right.size && left.mtimeNs === right.mtimeNs && left.ctimeNs === right.ctimeNs;
}

function sameFileObject(left: FileIdentity, right: FileIdentity, platform: NodeJS.Platform): boolean {
  return identityCertain(left, platform) && identityCertain(right, platform)
    && sameDevice(left.dev, right.dev, platform) && left.ino === right.ino;
}

function samePathAndDescriptorIdentity(left: FileIdentity, right: FileIdentity, platform: NodeJS.Platform): boolean {
  return sameFileObject(left, right, platform) && left.size === right.size && left.mtimeNs === right.mtimeNs && left.ctimeNs === right.ctimeNs;
}

function sameDevice(left: bigint, right: bigint, platform: NodeJS.Platform): boolean {
  return left === right || (platform === "win32" && left === 0n && right !== 0n);
}

function cloneCache(source: ReadonlyMap<string, CacheEntry>): Map<string, CacheEntry> {
  return new Map([...source].map(([name, cached]) => [name, { ...cached }]));
}

function cacheRecord(cache: Map<string, CacheEntry>, maximum: number, name: string, fileIdentity: FileIdentity, record: HeartbeatRecord, cachedAtMs: number, seenScan: number): void {
  while (cache.size >= maximum) {
    const oldest = cache.keys().next().value as string | undefined;
    if (oldest === undefined) break;
    cache.delete(oldest);
  }
  cache.set(name, { identity: fileIdentity, record: structuredClone(record), cachedAtMs, seenScan });
}

function touchCache(cache: Map<string, CacheEntry>, name: string, cached: CacheEntry): void {
  cache.delete(name);
  cache.set(name, cached);
}

function safeHeartbeatName(name: string): boolean {
  return name.endsWith(".json") && name === path.basename(name) && !name.includes("\0");
}

function compareCodePoints(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function sortedRecords(recordsByFile: ReadonlyMap<string, HeartbeatRecord>): HeartbeatRecord[] {
  return [...recordsByFile.entries()]
    .sort(([leftName, left], [rightName, right]) => compareCodePoints(left.project, right.project)
      || compareCodePoints(left.instance_id, right.instance_id)
      || compareCodePoints(left.canonical_project_root, right.canonical_project_root)
      || compareCodePoints(leftName, rightName))
    .map(([, record]) => record);
}

function directoryIdentityCertain(stat: BigIntStats, platform: NodeJS.Platform): boolean {
  return deviceAndInodeCertain(stat.dev, stat.ino, platform) && stat.ctimeNs > 0n;
}

function deviceAndInodeCertain(dev: bigint, ino: bigint, platform: NodeJS.Platform): boolean {
  return ino !== 0n && (dev !== 0n || platform === "win32");
}

function privateRegistryDirectory(stat: BigIntStats, platform: NodeJS.Platform): boolean {
  if (platform === "win32" || typeof process.getuid !== "function") return true;
  return stat.uid === BigInt(process.getuid()) && (stat.mode & 0o777n) === 0o700n;
}

function sameDirectory(left: DirectorySnapshot, right: DirectorySnapshot): boolean {
  return left.realPath === right.realPath && left.components.length === right.components.length && left.components.every((component, index) => {
    const other = right.components[index];
    return other !== undefined && component.path === other.path && component.dev === other.dev && component.ino === other.ino;
  });
}

function nonOperationalProjectResolution(state: Exclude<RegistryOperationalState, "ok">): ProjectResolutionError {
  return { status: "error", code: "unsafe_binding_refused", matches: [], registryOperationalState: state };
}

function nodeFileSystem(): RegistryFileSystem {
  return {
    platform: process.platform,
    lstatSync: (file, options) => fs.lstatSync(file, options),
    opendirSync: (directory) => fs.opendirSync(directory),
    openSync: (file, flags) => fs.openSync(file, flags),
    fstatSync: (handle, options) => fs.fstatSync(handle, options),
    readSync: (handle, buffer, offset, length, position) => fs.readSync(handle, buffer, offset, length, position),
    closeSync: (handle) => fs.closeSync(handle),
    realpathSync: (file) => fs.realpathSync.native(file),
  };
}

function boundedInteger(value: number | undefined, fallback: number, maximum: number): number {
  if (value === undefined) return Math.min(Math.max(1, Math.floor(fallback)), maximum);
  if (!Number.isSafeInteger(value) || value < 1 || value > maximum) throw new TypeError("Invalid bounded registry option");
  return value;
}

type ProjectSelectorRecord = Pick<HeartbeatRecord, "project" | "display_name" | "instance_id" | "project_root" | "canonical_project_root" | "aliases">;

function resolveProjectRecords<T extends ProjectSelectorRecord>(
  records: ReadonlyArray<T>,
  selector: string,
  options: ProjectResolutionOptions,
  isStale: (record: T) => boolean,
  cloneRecord: (record: T) => T,
  toPublicRecord: (record: T) => PublicProjectRecord,
): { status: "ok"; record: T } | { status: "error"; code: Extract<ErrorCode, "project_not_found" | "project_stale" | "duplicate_project_id">; matches: PublicProjectRecord[] } {
  const matches = records.filter((record) => matchesProjectSelector(record, selector));
  const liveMatches = matches.filter((record) => !isStale(record));

  if (liveMatches.length === 1) {
    const record = liveMatches[0];
    if (!record) return { status: "error", code: "project_not_found", matches: [] };
    return { status: "ok", record: cloneRecord(record) };
  }
  if (liveMatches.length > 1) return { status: "error", code: "duplicate_project_id", matches: liveMatches.map(toPublicRecord) };
  if (options.allowStale && matches.length === 1) {
    const record = matches[0];
    if (!record) return { status: "error", code: "project_not_found", matches: [] };
    return { status: "ok", record: cloneRecord(record) };
  }
  if (options.allowStale && matches.length > 1) return { status: "error", code: "duplicate_project_id", matches: matches.map(toPublicRecord) };
  if (matches.length > 0) return { status: "error", code: "project_stale", matches: matches.map(toPublicRecord) };
  return { status: "error", code: "project_not_found", matches: [] };
}

function matchesProjectSelector(record: ProjectSelectorRecord, selector: string): boolean {
  if (record.project === selector || record.display_name === selector || record.instance_id === selector || record.project_root === selector) return true;
  const canonicalSelector = canonicalProjectSelector(selector);
  if (canonicalSelector && record.canonical_project_root === canonicalSelector) return true;
  return record.aliases.some((alias) => alias.host_path === selector || (canonicalSelector !== undefined && alias.canonical_path === canonicalSelector));
}

function publicRecordAt(record: HeartbeatRecord, capturedAtMs: number, staleAfterMs: number, processGenerationDead = false): PublicProjectRecord {
  const { token: _token, ...publicRecord } = structuredClone(record);
  return freezeSnapshotValue({ ...publicRecord, stale: processGenerationDead || capturedAtMs - Date.parse(record.last_heartbeat) > staleAfterMs });
}

function processGenerationIdentity(record: HeartbeatRecord): RegistryProcessGenerationIdentity | undefined {
  if (!Number.isSafeInteger(record.unity_pid) || record.unity_pid <= 0
    || typeof record.unity_process_start_utc !== "string" || record.unity_process_start_utc.length === 0
    || !Number.isSafeInteger(record.heartbeat_generation) || (record.heartbeat_generation ?? 0) < 1) return undefined;
  return {
    instance_id: record.instance_id,
    unity_pid: record.unity_pid,
    unity_process_start_utc: record.unity_process_start_utc,
    heartbeat_generation: record.heartbeat_generation!,
    last_heartbeat: record.last_heartbeat,
  };
}

function processGenerationKey(value: Pick<HeartbeatRecord, "instance_id" | "unity_pid" | "unity_process_start_utc" | "heartbeat_generation" | "last_heartbeat">): string {
  if (typeof value.unity_process_start_utc !== "string" || value.unity_process_start_utc.length === 0
    || !Number.isSafeInteger(value.heartbeat_generation) || (value.heartbeat_generation ?? 0) < 1) return "";
  return canonicalJsonStringify({
    instance_id: value.instance_id,
    unity_pid: value.unity_pid,
    unity_process_start_utc: value.unity_process_start_utc,
    heartbeat_generation: value.heartbeat_generation,
    last_heartbeat: value.last_heartbeat,
  });
}

function isRegistryProcessState(value: unknown): value is RegistryProcessState {
  return value === "live" || value === "dead" || value === "uncertain";
}

function isProcessGenerationObservation(value: unknown): value is RegistryProcessGenerationObservation {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const observation = value as Partial<RegistryProcessGenerationObservation>;
  return typeof observation.instance_id === "string"
    && Number.isSafeInteger(observation.unity_pid) && (observation.unity_pid ?? 0) > 0
    && typeof observation.unity_process_start_utc === "string" && observation.unity_process_start_utc.length > 0
    && Number.isSafeInteger(observation.heartbeat_generation) && (observation.heartbeat_generation ?? 0) > 0
    && typeof observation.last_heartbeat === "string"
    && isRegistryProcessState(observation.state);
}

function cloneFrozenPublicRecord(record: PublicProjectRecord): PublicProjectRecord {
  return freezeSnapshotValue(structuredClone(record));
}

function cloneFrozenPublicRecords(records: ReadonlyArray<PublicProjectRecord>): PublicProjectRecord[] {
  return freezeSnapshotValue(records.map(cloneFrozenPublicRecord));
}

function freezeSnapshotValue<T>(value: T): T {
  if (typeof value !== "object" || value === null || Object.isFrozen(value)) return value;
  for (const child of Object.values(value as Record<string, unknown>)) freezeSnapshotValue(child);
  return Object.freeze(value);
}

function safeReadCode(error: unknown, fallback: RegistryInvalidDiagnosticCode): RegistryInvalidDiagnosticCode {
  return error instanceof RegistryReadError ? error.code : fallback;
}

function isMissing(error: unknown): boolean {
  const code = typeof error === "object" && error !== null && "code" in error ? String(error.code) : "";
  return code === "ENOENT" || code === "ENOTDIR";
}

function canonicalProjectSelector(selector: string): string | undefined {
  try { return canonicalPaths.normalizeProjectRoot(selector); }
  catch { return undefined; }
}
