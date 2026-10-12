import fs from "node:fs";
import path from "node:path";
import { createHash, randomUUID } from "node:crypto";

import type { ApprovalBindingEnvelope, ApprovalBindingRecord, ApprovalBindingService } from "./approval-binding.js";
import type { CommandDescriptor } from "../bridge/bridge-client.js";
import { hashRedactedArgs } from "../observability/audit.js";
import { acquireStorageLock, currentProcessOwner, durableRenameFile, durableUnlinkFile, ensureSecureDirectory, ensureSecureRoot, maintainDeleteTombstones, maintainPublisherTemps, ownerIdentityTag, processIdentityTagOwnerState, publisherTempFiles, readSecureFile, secureFileStat, secureRegularFiles, writeNewFileDurable } from "../security/safe-storage.js";
import type { OperationalLogger } from "../observability/operational-logger.js";

const PROJECT_ROOT_HASH_DOMAIN = Buffer.from("sgg.unity_mcp.approval_project_root.v1\0", "utf8");
const HASH_PATTERN = /^sha256:[0-9a-f]{64}$/;

export type ApprovalRisk = CommandDescriptor["risk"];
export type ApprovalIdempotency = CommandDescriptor["idempotency"];
export type ApprovalMode = "wait" | "job";

export type ApprovalSafeMetadata = {
  project_id: string | null;
  canonical_project_root_hash: string | null;
  instance_id: string | null;
  command: string;
  command_revision: string;
  commands_hash: string | null;
  risk: ApprovalRisk;
  idempotency: ApprovalIdempotency;
  mode: ApprovalMode;
  request_id: string | null;
  args_hash: string;
  effective_policy_revision: string;
  policy_action: "allow" | "ask" | "deny";
  policy_reason: string;
  policy_pattern: string | null;
};

type ApprovalPublicMetadata = {
  project: string | null;
  command: string;
  risk: ApprovalRisk;
  idempotency: ApprovalIdempotency;
  mode: ApprovalMode;
  args_hash: string;
  request_id?: string;
  created_at: string;
  expires_at: string;
  remaining_ms: number;
};

export type ApprovalListEntry = ApprovalPublicMetadata & { status: "active" | "expired" };

export type ApprovalPendingListEntry = ApprovalPublicMetadata & {
  id: string;
  scope: "single_call_only";
  updated_at: string;
  action_hint: string;
  status: "pending" | "expired";
};

export type ApprovalListOptions = { project?: string; includeExpired?: boolean; now?: Date };
export type ApprovalConsumeOutcome = { status: "consumed" | "not_found" | "expired" };
export type ApprovalPendingGeneration = Pick<ApprovalPendingListEntry, "id" | "created_at" | "updated_at" | "expires_at">;
export type ApprovalPendingClaimOutcome = { status: "claimed" | "not_found" | "expired" | "context_changed" };
export type ApprovalStoreOptions = {
  maxRecords?: number;
  maxBytes?: number;
  maxQuarantineFiles?: number;
  maxQuarantineBytes?: number;
  quarantineMaxAgeMs?: number;
  claimTtlMs?: number;
  now?: () => Date;
  logger?: OperationalLogger;
};
export type ApprovalStoreStatus = {
  schema_version: 1;
  state: "ok" | "unavailable" | "capacity_exhausted";
  records: number;
  bytes: number;
  quarantine_files: number;
  quarantine_bytes: number;
};

const DEFAULT_STORE_LIMITS = Object.freeze({ maxRecords: 1024, maxBytes: 8 * 1024 * 1024, maxQuarantineFiles: 128, maxQuarantineBytes: 2 * 1024 * 1024, quarantineMaxAgeMs: 7 * 86_400_000, claimTtlMs: 15 * 60_000 });

type InternalCommonRecord = ApprovalSafeMetadata & {
  schema_version: 3;
  id: string;
  scope: "single_call_only";
  created_at: string;
  expires_at: string;
  binding: ApprovalBindingRecord;
};

type InternalActiveRecord = InternalCommonRecord & {
  record_type: "approval";
  status: "active";
};

type InternalPendingRecord = InternalCommonRecord & {
  record_type: "approval_request";
  status: "pending";
  updated_at: string;
};

export class ApprovalStoreError extends Error {
  readonly code = "approval_unavailable";

  constructor() {
    super("Approval service is unavailable");
    this.name = "ApprovalStoreError";
  }
}

export class ApprovalStore {
  readonly #limits: Required<Omit<ApprovalStoreOptions, "now" | "logger">>;
  readonly #now: () => Date;
  readonly #logger: OperationalLogger | undefined;
  #lastStatus: ApprovalStoreStatus = { schema_version: 1, state: "ok", records: 0, bytes: 0, quarantine_files: 0, quarantine_bytes: 0 };

  constructor(readonly dir: string, options: ApprovalStoreOptions = {}) {
    this.#limits = {
      maxRecords: positiveLimit(options.maxRecords, DEFAULT_STORE_LIMITS.maxRecords, 100_000),
      maxBytes: positiveLimit(options.maxBytes, DEFAULT_STORE_LIMITS.maxBytes, 1024 * 1024 * 1024),
      maxQuarantineFiles: positiveLimit(options.maxQuarantineFiles, DEFAULT_STORE_LIMITS.maxQuarantineFiles, 10_000),
      maxQuarantineBytes: positiveLimit(options.maxQuarantineBytes, DEFAULT_STORE_LIMITS.maxQuarantineBytes, 1024 * 1024 * 1024),
      quarantineMaxAgeMs: positiveLimit(options.quarantineMaxAgeMs, DEFAULT_STORE_LIMITS.quarantineMaxAgeMs, 366 * 86_400_000),
      claimTtlMs: positiveLimit(options.claimTtlMs, DEFAULT_STORE_LIMITS.claimTtlMs, 86_400_000),
    };
    this.#now = options.now ?? (() => new Date());
    this.#logger = options.logger;
  }

  status(now: Date = this.#now()): ApprovalStoreStatus {
    try { return this.withStoreLock(() => { this.maintain(now); return { ...this.#lastStatus }; }); }
    catch {
      this.observeStatus({ ...this.#lastStatus, state: "unavailable" });
      return { ...this.#lastStatus, state: "unavailable" };
    }
  }

  snapshotStatus(): ApprovalStoreStatus {
    return { ...this.#lastStatus };
  }

  request(metadata: ApprovalSafeMetadata, envelope: ApprovalBindingEnvelope, bindingService: ApprovalBindingService, options: { ttl_ms: number; now?: Date }): ApprovalPendingListEntry {
    try {
      assertSafeMetadata(metadata);
      assertPositiveTtl(options.ttl_ms);
      const now = options.now ?? this.#now();
      const binding = bindingService.create(envelope);
      const id = pendingRequestId(binding);
      return this.withStoreLock(() => {
        this.maintain(now);
        this.assertOperational();
        return this.withPendingLock(id, () => {
        const filePath = this.pendingFilePath(id);
        const existing = parsePendingFile(filePath, false);
        if (existing && !isExpired(existing, now)) {
          if (!sameSafeMetadata(existing, metadata) || !bindingService.verify(envelope, existing.binding)) throw new ApprovalStoreError();
          return pendingProjection(existing, now);
        }
        if (existing) {
          const claimPath = this.claim(filePath, "pending", now);
          if (!claimPath) throw new ApprovalStoreError();
          const claimed = parsePendingFile(claimPath, true);
          if (!claimed || !isExpired(claimed, now) || !sameSafeMetadata(claimed, metadata) || !bindingService.verify(envelope, claimed.binding)) throw new ApprovalStoreError();
        } else if (fs.existsSync(filePath)) {
          throw new ApprovalStoreError();
        }

        const timestamp = now.toISOString();
        const record: InternalPendingRecord = {
          schema_version: 3,
          record_type: "approval_request",
          status: "pending",
          id,
          ...copySafeMetadata(metadata),
          scope: "single_call_only",
          created_at: timestamp,
          updated_at: timestamp,
          expires_at: new Date(now.getTime() + options.ttl_ms).toISOString(),
          binding,
        };
        this.assertCapacity(record);
        writeNewJson(filePath, record);
        return pendingProjection(record, now);
        });
      });
    } catch (error) {
      if (error instanceof ApprovalStoreError) throw error;
      throw new ApprovalStoreError();
    }
  }

  consume(metadata: ApprovalSafeMetadata, envelope: ApprovalBindingEnvelope, bindingService: ApprovalBindingService, now: Date = this.#now()): ApprovalConsumeOutcome {
    try {
      assertSafeMetadata(metadata);
      const currentBinding = bindingService.create(envelope);
      return this.withStoreLock(() => {
      this.maintain(now, false);
      this.assertOperational();
      for (const filePath of this.activeFiles()) {
        const candidate = parseActiveFile(filePath, true);
        if (!candidate || candidate.binding.key_id !== currentBinding.key_id || !sameSafeMetadata(candidate, metadata)) continue;
        const claimPath = this.claim(filePath, "active", now);
        if (!claimPath) continue;
        const claimed = parseActiveFile(claimPath, false);
        if (!claimed || !sameSafeMetadata(claimed, metadata) || !bindingService.verify(envelope, claimed.binding)) {
          if (fs.existsSync(claimPath)) this.quarantine(claimPath, now);
          return { status: "not_found" };
        }
        if (isExpired(claimed, now)) { durableUnlinkFile(this.dir, claimPath); return { status: "expired" }; }
        durableUnlinkFile(this.dir, claimPath);
        return { status: "consumed" };
      }
      return { status: "not_found" };
      });
    } catch (error) {
      if (error instanceof ApprovalStoreError) throw error;
      throw new ApprovalStoreError();
    }
  }

  list(options: ApprovalListOptions = {}): ApprovalListEntry[] {
    try {
      const now = options.now ?? this.#now();
      return this.withStoreLock(() => {
      this.maintain(now);
      return this.activeFiles().map((filePath) => parseActiveFile(filePath, true)).filter((record): record is InternalActiveRecord => Boolean(record))
        .filter((record) => !options.project || record.project_id === options.project)
        .sort((a, b) => a.created_at.localeCompare(b.created_at)).map((record) => activeProjection(record, now))
        .filter((entry) => options.includeExpired || entry.status === "active");
      });
    } catch {
      throw new ApprovalStoreError();
    }
  }

  listPending(options: ApprovalListOptions = {}): ApprovalPendingListEntry[] {
    try {
      const now = options.now ?? this.#now();
      return this.withStoreLock(() => {
      this.maintain(now);
      return this.pendingFiles().map((filePath) => parsePendingFile(filePath, true)).filter((record): record is InternalPendingRecord => Boolean(record))
        .filter((record) => !options.project || record.project_id === options.project)
        .sort((a, b) => a.updated_at.localeCompare(b.updated_at)).map((record) => pendingProjection(record, now))
        .filter((entry) => options.includeExpired || entry.status === "pending");
      });
    } catch {
      throw new ApprovalStoreError();
    }
  }

  approvePending(input: { pending_id: string; ttl_ms: number; now?: Date }): ApprovalListEntry {
    try {
      assertPositiveTtl(input.ttl_ms);
      const now = input.now ?? this.#now();
      return this.withStoreLock(() => {
        this.maintain(now);
        this.assertOperational();
        return this.withPendingLock(input.pending_id, () => {
        const claimPath = this.claim(this.pendingFilePath(input.pending_id), "pending", now);
        if (!claimPath) throw new ApprovalStoreError();
        const pending = parsePendingFile(claimPath, true);
        if (!pending || pending.id !== input.pending_id || isExpired(pending, now)) {
          if (fs.existsSync(claimPath)) this.quarantine(claimPath, now);
          throw new ApprovalStoreError();
        }
        const active: InternalActiveRecord = {
          schema_version: 3,
          record_type: "approval",
          status: "active",
          id: randomUUID(),
          ...copySafeMetadata(pending),
          scope: "single_call_only",
          created_at: now.toISOString(),
          expires_at: new Date(now.getTime() + input.ttl_ms).toISOString(),
          binding: pending.binding,
        };
        this.assertCapacity(active, -fileSize(this.dir, claimPath));
        writeNewJson(path.join(this.dir, `${active.id}.json`), active);
        durableUnlinkFile(this.dir, claimPath);
        return activeProjection(active, now);
        });
      });
    } catch (error) {
      if (error instanceof ApprovalStoreError) throw error;
      throw new ApprovalStoreError();
    }
  }

  claimPendingGeneration(
    generation: ApprovalPendingGeneration,
    current: { metadata: ApprovalSafeMetadata; envelope: ApprovalBindingEnvelope; bindingService: ApprovalBindingService } | undefined,
    now: Date = this.#now(),
  ): ApprovalPendingClaimOutcome {
    try {
      if (!validGeneration(generation)) return { status: "not_found" };
      if (current) assertSafeMetadata(current.metadata);
      return this.withStoreLock(() => {
        this.maintain(now);
        this.assertOperational();
        return this.withPendingLock(generation.id, () => {
        const filePath = this.pendingFilePath(generation.id);
        const pending = parsePendingFile(filePath, true);
        if (!pending || !sameGeneration(pending, generation)) return { status: "not_found" };
        const claimPath = this.claim(filePath, "pending", now);
        if (!claimPath) return { status: "not_found" };
        const claimed = parsePendingFile(claimPath, false);
        if (!claimed || !sameGeneration(claimed, generation)) {
          if (fs.existsSync(claimPath)) this.quarantine(claimPath, now);
          return { status: "not_found" };
        }
        if (isExpired(claimed, now)) { durableUnlinkFile(this.dir, claimPath); return { status: "expired" }; }
        if (current) {
          const metadataMatches = sameSafeMetadata(claimed, current.metadata);
          const bindingMatches = current.bindingService.verify(current.envelope, claimed.binding);
          if (!metadataMatches || !bindingMatches) { durableUnlinkFile(this.dir, claimPath); return { status: "context_changed" }; }
        }
        durableUnlinkFile(this.dir, claimPath);
        return { status: "claimed" };
        });
      });
    } catch (error) {
      if (error instanceof ApprovalStoreError) return { status: "not_found" };
      throw new ApprovalStoreError();
    }
  }

  private activeFiles(): string[] {
    return secureRegularFiles(this.dir, this.dir, (name) => name.endsWith(".json"));
  }

  private pendingFiles(): string[] {
    return secureRegularFiles(this.dir, this.pendingDir, (name) => name.endsWith(".json"));
  }

  private claim(source: string, kind: "active" | "pending", now: Date): string | undefined {
    ensureSecureDirectory(this.dir, "claims");
    const owner = currentProcessOwner(now);
    const destination = path.join(this.claimsDir, `${kind}-c1-${owner.pid}-${ownerIdentityTag(owner)}-${Date.parse(owner.acquired_at)}-${owner.nonce}.json`);
    try {
      durableRenameFile(this.dir, source, destination);
      return destination;
    } catch (error) {
      if (isNotFound(error)) return undefined;
      throw error;
    }
  }

  private withPendingLock<T>(id: string, action: () => T): T {
    ensureSecureDirectory(this.dir, "pending-locks");
    const lock = acquireStorageLock(this.dir, path.join("pending-locks", `${safeFileSegment(id)}.lock`));
    try { return action(); } finally { lock.release(); }
  }

  private withStoreLock<T>(action: () => T): T {
    ensureSecureRoot(this.dir);
    const lock = acquireStorageLock(this.dir, ".store.lock");
    try { return action(); } finally { lock.release(); }
  }

  private maintain(now: Date, cleanupExpired = true): void {
    ensureSecureRoot(this.dir);
    ensureSecureDirectory(this.dir, "pending");
    ensureSecureDirectory(this.dir, "claims");
    ensureSecureDirectory(this.dir, "quarantine");
    const managedDirectories = [this.dir, this.pendingDir, this.claimsDir, this.quarantineDir];
    for (const directory of managedDirectories) { maintainPublisherTemps(this.dir, directory); maintainDeleteTombstones(this.dir, directory); }
    for (const temp of [...tempFiles(this.dir, this.dir), ...tempFiles(this.dir, this.pendingDir)]) durableUnlinkFile(this.dir, temp);
    for (const filePath of [...this.activeFiles(), ...this.pendingFiles()]) {
      const record = filePath.startsWith(`${this.pendingDir}${path.sep}`) ? parsePendingFile(filePath, true) : parseActiveFile(filePath, true);
      if (record && cleanupExpired && isExpired(record, now)) { durableUnlinkFile(this.dir, filePath); continue; }
      if (!record && fs.existsSync(filePath)) this.quarantine(filePath, now);
    }
    this.cleanupOrphanClaims(now);
    this.cleanupQuarantine(now);
    const records = [...this.activeFiles(), ...this.pendingFiles(), ...jsonFiles(this.dir, this.claimsDir), ...managedDirectories.flatMap((directory) => publisherTempFiles(this.dir, directory))];
    const bytes = records.reduce((total, filePath) => total + fileSize(this.dir, filePath), 0);
    const quarantine = jsonFiles(this.dir, this.quarantineDir);
    const quarantineBytes = quarantine.reduce((total, filePath) => total + fileSize(this.dir, filePath), 0);
    const capacity = records.length > this.#limits.maxRecords || bytes > this.#limits.maxBytes;
    this.observeStatus({ schema_version: 1, state: capacity ? "capacity_exhausted" : "ok", records: records.length, bytes, quarantine_files: quarantine.length, quarantine_bytes: quarantineBytes });
  }

  private assertCapacity(value: unknown, byteAdjustment = 0): void {
    const incoming = Buffer.byteLength(`${JSON.stringify(value, null, 2)}\n`) + byteAdjustment;
    if (this.#lastStatus.records + (byteAdjustment < 0 ? 0 : 1) > this.#limits.maxRecords || this.#lastStatus.bytes + incoming > this.#limits.maxBytes) {
      this.observeStatus({ ...this.#lastStatus, state: "capacity_exhausted" });
      throw new ApprovalStoreError();
    }
  }

  private assertOperational(): void { if (this.#lastStatus.state !== "ok") throw new ApprovalStoreError(); }

  private observeStatus(status: ApprovalStoreStatus): void {
    const previous = this.#lastStatus.state;
    this.#lastStatus = status;
    if (status.state === previous) return;
    if (status.state === "ok") this.#logger?.log("SGG-OP-APPROVAL-RECOVERED-001", status.records, status.bytes, status.quarantine_files);
    else this.#logger?.log("SGG-OP-APPROVAL-STATE-001", status.state, status.records, status.bytes, status.quarantine_files);
  }

  private quarantine(filePath: string, now: Date): void {
    ensureSecureDirectory(this.dir, "quarantine");
    const destination = path.join(this.quarantineDir, `${now.getTime()}-${randomUUID()}.json`);
    try { durableRenameFile(this.dir, filePath, destination); } catch (error) { if (!isNotFound(error)) throw error; }
  }

  private cleanupQuarantine(now: Date): void {
    const files = jsonFiles(this.dir, this.quarantineDir).map((filePath) => ({ filePath, stat: secureFileStat(this.dir, filePath)! })).sort((a, b) => b.stat.mtimeMs - a.stat.mtimeMs);
    let bytes = 0;
    for (const [index, file] of files.entries()) {
      const keep = now.getTime() - file.stat.mtimeMs <= this.#limits.quarantineMaxAgeMs && index < this.#limits.maxQuarantineFiles && bytes + file.stat.size <= this.#limits.maxQuarantineBytes;
      if (keep) bytes += file.stat.size; else durableUnlinkFile(this.dir, file.filePath);
    }
  }

  private cleanupOrphanClaims(now: Date): void {
    for (const filePath of jsonFiles(this.dir, this.claimsDir)) {
      const lease = parseClaimLease(path.basename(filePath));
      if (!lease) {
        const stat = secureFileStat(this.dir, filePath)!;
        if (now.getTime() - stat.mtimeMs >= this.#limits.claimTtlMs) this.quarantine(filePath, now);
        continue;
      }
      if (now.getTime() - lease.acquiredAtMs >= this.#limits.claimTtlMs && processIdentityTagOwnerState(lease.pid, lease.processTag) === "dead") this.quarantine(filePath, now);
    }
  }

  private get pendingDir(): string { return path.join(this.dir, "pending"); }
  private get claimsDir(): string { return path.join(this.dir, "claims"); }
  private get pendingLocksDir(): string { return path.join(this.dir, "pending-locks"); }
  private get quarantineDir(): string { return path.join(this.dir, "quarantine"); }
  private pendingFilePath(id: string): string { return path.join(this.pendingDir, `${safeFileSegment(id)}.json`); }
}

export function argsHash(args: Record<string, unknown>): string { return hashRedactedArgs(args); }

export function canonicalProjectRootHash(canonicalRoot: string | null): string | null {
  if (canonicalRoot === null) return null;
  return `sha256:${createHash("sha256").update(PROJECT_ROOT_HASH_DOMAIN).update(canonicalRoot, "utf8").digest("hex")}`;
}

function pendingRequestId(binding: ApprovalBindingRecord): string {
  return `pending-${createHash("sha256").update("sgg.approval.pending.v3\0").update(binding.request_hmac, "hex").digest("hex").slice(0, 24)}`;
}

function activeProjection(record: InternalActiveRecord, now: Date): ApprovalListEntry {
  const remaining_ms = remainingMs(record, now);
  return { project: record.project_id, command: record.command, risk: record.risk, idempotency: record.idempotency, mode: record.mode, args_hash: record.args_hash, ...(record.request_id ? { request_id: record.request_id } : {}), created_at: record.created_at, expires_at: record.expires_at, status: remaining_ms > 0 ? "active" : "expired", remaining_ms };
}

function pendingProjection(record: InternalPendingRecord, now: Date): ApprovalPendingListEntry {
  return { ...activeProjection({ ...record, record_type: "approval", status: "active" }, now), id: record.id, scope: record.scope, updated_at: record.updated_at, action_hint: `sgg-unity-mcp-gateway approval approve --dir <configured-approval-dir> --pending-id ${record.id}`, status: isExpired(record, now) ? "expired" : "pending" };
}

function parseActiveFile(filePath: string, invalidateLegacy: boolean): InternalActiveRecord | undefined {
  const parsed = readJson(filePath);
  if (isLegacy(parsed) || hasLegacyBinding(parsed)) return undefined;
  if (!isPlainRecord(parsed) || !hasExactKeys(parsed, [...COMMON_KEYS, "record_type", "status"])) return undefined;
  if (parsed.schema_version !== 3 || parsed.record_type !== "approval" || parsed.status !== "active" || !validCommonRecord(parsed)) return undefined;
  return parsed as InternalActiveRecord;
}

function parsePendingFile(filePath: string, invalidateLegacy: boolean): InternalPendingRecord | undefined {
  const parsed = readJson(filePath);
  if (isLegacy(parsed) || hasLegacyBinding(parsed)) return undefined;
  if (!isPlainRecord(parsed) || !hasExactKeys(parsed, [...COMMON_KEYS, "record_type", "status", "updated_at"])) return undefined;
  if (parsed.schema_version !== 3 || parsed.record_type !== "approval_request" || parsed.status !== "pending" || !validCommonRecord(parsed) || !isIsoTimestamp(parsed.created_at) || !isIsoTimestamp(parsed.expires_at) || !isIsoTimestamp(parsed.updated_at) || Date.parse(parsed.updated_at) < Date.parse(parsed.created_at) || Date.parse(parsed.updated_at) >= Date.parse(parsed.expires_at)) return undefined;
  return parsed as InternalPendingRecord;
}

const COMMON_KEYS = ["schema_version", "id", "project_id", "canonical_project_root_hash", "instance_id", "command", "command_revision", "commands_hash", "risk", "idempotency", "mode", "request_id", "args_hash", "effective_policy_revision", "policy_action", "policy_reason", "policy_pattern", "scope", "created_at", "expires_at", "binding"];

function validCommonRecord(value: Record<string, unknown>): boolean {
  try {
    assertSafeMetadata(metadataFrom(value));
    return value.scope === "single_call_only" && typeof value.id === "string" && value.id.length > 0 && isIsoTimestamp(value.created_at) && isIsoTimestamp(value.expires_at) && Date.parse(value.expires_at) > Date.parse(value.created_at) && validBinding(value.binding);
  } catch {
    return false;
  }
}

function assertSafeMetadata(value: ApprovalSafeMetadata): void {
  if (!isPlainRecord(value) || !hasExactKeys(value, METADATA_KEYS)) throw new TypeError("Invalid approval metadata shape");
  if (!nullableNonempty(value.project_id) || !nullableHash(value.canonical_project_root_hash) || !nullableNonempty(value.instance_id)) throw new TypeError("Invalid approval identity");
  if (typeof value.command !== "string" || value.command.length === 0 || value.command.length > 1024 || !HASH_PATTERN.test(value.command_revision) || !nullableNonempty(value.commands_hash)) throw new TypeError("Invalid approval command metadata");
  if (!isApprovalRisk(value.risk) || !isApprovalIdempotency(value.idempotency) || (value.mode !== "wait" && value.mode !== "job") || !nullableNonempty(value.request_id) || !HASH_PATTERN.test(value.args_hash)) throw new TypeError("Invalid approval request metadata");
  if (!HASH_PATTERN.test(value.effective_policy_revision) || !isPolicyAction(value.policy_action) || typeof value.policy_reason !== "string" || value.policy_reason.length === 0 || value.policy_reason.length > 1024 || !nullableNonempty(value.policy_pattern)) throw new TypeError("Invalid approval policy metadata");
}

function copySafeMetadata(record: ApprovalSafeMetadata): ApprovalSafeMetadata {
  return { project_id: record.project_id, canonical_project_root_hash: record.canonical_project_root_hash, instance_id: record.instance_id, command: record.command, command_revision: record.command_revision, commands_hash: record.commands_hash, risk: record.risk, idempotency: record.idempotency, mode: record.mode, request_id: record.request_id, args_hash: record.args_hash, effective_policy_revision: record.effective_policy_revision, policy_action: record.policy_action, policy_reason: record.policy_reason, policy_pattern: record.policy_pattern };
}

function sameSafeMetadata(record: InternalCommonRecord, metadata: ApprovalSafeMetadata): boolean {
  const stored = copySafeMetadata(record);
  return (Object.keys(stored) as Array<keyof ApprovalSafeMetadata>).every((key) => stored[key] === metadata[key]);
}

function metadataFrom(value: Record<string, unknown>): ApprovalSafeMetadata {
  return { project_id: value.project_id, canonical_project_root_hash: value.canonical_project_root_hash, instance_id: value.instance_id, command: value.command, command_revision: value.command_revision, commands_hash: value.commands_hash, risk: value.risk, idempotency: value.idempotency, mode: value.mode, request_id: value.request_id, args_hash: value.args_hash, effective_policy_revision: value.effective_policy_revision, policy_action: value.policy_action, policy_reason: value.policy_reason, policy_pattern: value.policy_pattern } as ApprovalSafeMetadata;
}

const METADATA_KEYS = ["project_id", "canonical_project_root_hash", "instance_id", "command", "command_revision", "commands_hash", "risk", "idempotency", "mode", "request_id", "args_hash", "effective_policy_revision", "policy_action", "policy_reason", "policy_pattern"];

function validBinding(value: unknown): value is ApprovalBindingRecord {
  return isPlainRecord(value) && hasExactKeys(value, ["schema_version", "algorithm", "key_id", "request_hmac"]) && value.schema_version === 2 && value.algorithm === "hmac-sha256" && typeof value.key_id === "string" && HASH_PATTERN.test(value.key_id) && typeof value.request_hmac === "string" && /^[0-9a-f]{64}$/.test(value.request_hmac);
}

function isIsoTimestamp(value: unknown): value is string {
  if (typeof value !== "string") return false;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) && new Date(timestamp).toISOString() === value;
}

function assertPositiveTtl(value: number): void { if (!Number.isInteger(value) || value <= 0) throw new TypeError("Invalid approval TTL"); }

function remainingMs(record: { expires_at: string }, now: Date): number { return Math.max(0, Date.parse(record.expires_at) - now.getTime()); }
function isExpired(record: { expires_at: string }, now: Date): boolean { return remainingMs(record, now) === 0; }

function readJson(filePath: string): unknown {
  try { return JSON.parse(readSecureFile(approvalRoot(filePath), filePath, 1024 * 1024).toString("utf8")); } catch (error) { if (isNotFound(error)) return undefined; if (error instanceof SyntaxError || (error instanceof Error && error.message === "Storage file exceeds read limit")) return {}; throw error; }
}

function writeNewJson(filePath: string, value: unknown): void {
  writeNewFileDurable(approvalRoot(filePath), filePath, `${JSON.stringify(value, null, 2)}\n`);
}
function isPlainRecord(value: unknown): value is Record<string, unknown> { return typeof value === "object" && value !== null && !Array.isArray(value); }
function hasExactKeys(value: Record<string, unknown>, expected: string[]): boolean { const keys = Object.keys(value); return keys.length === expected.length && keys.every((key) => expected.includes(key)); }
function nullableNonempty(value: unknown): value is string | null { return value === null || (typeof value === "string" && value.length > 0 && value.length <= 1024); }
function nullableHash(value: unknown): value is string | null { return value === null || (typeof value === "string" && HASH_PATTERN.test(value)); }
function isApprovalRisk(value: unknown): value is ApprovalRisk { return value === "read" || value === "write" || value === "execute" || value === "destructive" || value === "network"; }
function isApprovalIdempotency(value: unknown): value is ApprovalIdempotency { return value === "safe" || value === "idempotent" || value === "unsafe"; }
function isLegacy(value: unknown): boolean { return isPlainRecord(value) && (value.schema_version === 1 || value.schema_version === 2); }
function hasLegacyBinding(value: unknown): boolean { return isPlainRecord(value) && isPlainRecord(value.binding) && value.binding.schema_version === 1; }
function isPolicyAction(value: unknown): value is ApprovalSafeMetadata["policy_action"] { return value === "allow" || value === "ask" || value === "deny"; }
function validGeneration(value: ApprovalPendingGeneration): boolean { return typeof value.id === "string" && value.id.length > 0 && isIsoTimestamp(value.created_at) && isIsoTimestamp(value.updated_at) && isIsoTimestamp(value.expires_at); }
function sameGeneration(record: InternalPendingRecord, generation: ApprovalPendingGeneration): boolean { return record.id === generation.id && record.created_at === generation.created_at && record.updated_at === generation.updated_at && record.expires_at === generation.expires_at; }
function isNotFound(error: unknown): boolean { return isPlainRecord(error) && error.code === "ENOENT"; }
function safeFileSegment(value: string): string { return value.replace(/[^A-Za-z0-9_.-]+/g, "_") || "approval"; }
function positiveLimit(value: number | undefined, fallback: number, maximum: number): number { const result = value ?? fallback; if (!Number.isSafeInteger(result) || result <= 0 || result > maximum) throw new TypeError("Invalid approval store limit"); return result; }
function jsonFiles(root: string, dir: string): string[] { return secureRegularFiles(root, dir, (name) => name.endsWith(".json")); }
function tempFiles(root: string, dir: string): string[] { return secureRegularFiles(root, dir, (name) => /^\..+\.\d+\.[0-9a-f-]+\.tmp$/.test(name)); }
function fileSize(root: string, filePath: string): number { return secureFileStat(root, filePath)?.size ?? 0; }
function approvalRoot(filePath: string): string { const parent = path.dirname(filePath); return ["pending", "claims", "quarantine"].includes(path.basename(parent)) ? path.dirname(parent) : parent; }
function parseClaimLease(name: string): { pid: number; processTag: string; acquiredAtMs: number } | undefined {
  const match = /^(?:active|pending)-c1-(\d+)-([a-f0-9]{16}|unknown)-(\d+)-[0-9a-f-]+\.json$/.exec(name);
  if (!match) return undefined;
  const pid = Number(match[1]);
  const acquiredAtMs = Number(match[3]);
  return Number.isSafeInteger(pid) && pid > 0 && Number.isSafeInteger(acquiredAtMs) ? { pid, processTag: match[2]!, acquiredAtMs } : undefined;
}
