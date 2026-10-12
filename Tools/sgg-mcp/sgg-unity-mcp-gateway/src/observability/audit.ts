import { createHash, randomUUID } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { ToolName } from "../mcp/contracts.js";
import type { ErrorCode, GatewayResult } from "../mcp/results.js";
import type { PolicyManagerSnapshot } from "../policy/policy-manager.js";
import { canonicalJsonStringify } from "../security/canonical-json.js";
import { acquireStorageLock, durableRenameFile, durableUnlinkFile, ensureSecureRoot, openSecureRegularFile, publisherTempFiles, secureFileStat, secureRegularFiles, writeNewFileDurable } from "../security/safe-storage.js";
import type { SecureFileStat } from "../security/safe-storage.js";
import type { CommandStatisticsMeasurement } from "./command-statistics.js";
import type { OperationalLogger } from "./operational-logger.js";

const SECRET_KEY_PATTERN = /token|authorization|api_key|secret|password|cookie|private_key|binding|root/i;

export type AuditEvent = {
  schema_version: 1;
  phase: "start" | "terminal";
  audit_id: string;
  timestamp: string;
  run_id: string;
  tool: ToolName;
  status: "ok" | "error";
  duration_ms: number;
  args_hash: string;
  request_id?: string;
  checkpoint_id?: string;
  plan_id?: string;
  project?: string;
  command?: string;
  command_risk?: CommandDescriptor["risk"];
  idempotency?: CommandDescriptor["idempotency"];
  timeout_ms?: number;
  command_source?: CommandDescriptor["source"];
  package_id?: string;
  package_version?: string;
  manifest_path?: string;
  trust?: CommandDescriptor["trust"];
  commands_hash?: string;
  command_revision?: string;
  policy_action?: "allow" | "ask" | "deny";
  policy_reason?: string;
  policy_pattern?: string;
  approval_status?: "requested" | "consumed" | "expired" | "unavailable" | "auto_allowed";
  auto_allow_ask?: boolean;
  auto_allow_known_unity_popups?: boolean;
  auto_allow_stale_registry_cleanup_without_elicitation?: boolean;
  bridge_duration_ms?: number;
  bridge_invoke_attempts?: number;
  read_recovery_attempted?: boolean;
  read_recovery_status?: "ready" | "error" | "timeout";
  post_reload_routing_attempted?: boolean;
  post_reload_routing_status?: "read_recovered" | "unsafe_dispatched_once" | "unsafe_not_dispatched" | "unsafe_retry_refused" | "catalog_stale" | "error" | "timeout";
  post_reload_routing_attempts?: number;
  post_reload_routing_command_risk?: CommandDescriptor["risk"] | "unknown";
  compile_verification_attempted?: boolean;
  compile_verification_status?: "ready_clean" | "background_compilation_pending" | "compilation_failed" | "expected_script_not_imported" | "timeout" | "unavailable" | "catalog_stale" | "ambiguous";
  compile_verification_attempts?: number;
  compile_verification_read_requests?: number;
  write_command_repeated?: false;
  batch_item_index?: number;
  batch_item_state?: NonNullable<GatewayResult["batch_item"]>["state"];
  batch_item_queued?: boolean;
  batch_item_admitted?: boolean;
  batch_item_bridge_request_issued?: boolean;
  batch_item_response_delivered?: boolean;
  batch_item_automatic_replay_attempted?: boolean;
  output_chars?: number;
  truncated?: boolean;
  artifact_refs?: string[];
  error_code?: ErrorCode;
  policy_state?: PolicyManagerSnapshot["state"];
  effective_policy_revision?: string | null;
  policy_loaded_at?: string | null;
  policy_last_attempt_at?: string | null;
  policy_degraded?: boolean;
  policy_error_kind?: PolicyManagerSnapshot["last_error_kind"];
  policy_error_code?: PolicyManagerSnapshot["last_error_code"];
  policy_degraded_since?: string | null;
  statistics_schema_version?: 1;
  statistics_operation?: string;
  statistics_operation_kind?: CommandStatisticsMeasurement["operation_kind"];
  statistics_internal?: boolean;
  statistics_request_bytes?: number;
  statistics_command_argument_bytes?: number;
  statistics_full_response_bytes?: number;
  statistics_compact_response_bytes?: number;
  statistics_delivered_response_bytes?: number;
  statistics_response_mode?: CommandStatisticsMeasurement["response_mode"];
  statistics_dispatched?: boolean;
  statistics_failure_stage?: CommandStatisticsMeasurement["failure_stage"];
  statistics_failure_summary?: string;
};

export type AuditCommandMetadata = Pick<CommandDescriptor, "risk" | "idempotency" | "timeout_ms" | "source" | "package_id" | "package_version" | "manifest_path" | "trust"> & {
  commands_hash?: string;
  command_revision?: string;
};

export type AuditPolicyMetadata = {
  action: "allow" | "ask" | "deny";
  reason: string;
  pattern?: string;
  approval_status?: "requested" | "consumed" | "expired" | "unavailable" | "auto_allowed";
  auto_allow_ask?: boolean;
  auto_allow_known_unity_popups?: boolean;
  auto_allow_stale_registry_cleanup_without_elicitation?: boolean;
};

export type AuditRuntimeMetadata = {
  command?: AuditCommandMetadata;
  policy?: AuditPolicyMetadata;
  bridge_duration_ms?: number;
  bridge_invoke_attempts?: number;
  read_recovery_attempted?: boolean;
  read_recovery_status?: "ready" | "error" | "timeout";
  post_reload_routing_attempted?: boolean;
  post_reload_routing_status?: AuditEvent["post_reload_routing_status"];
  post_reload_routing_attempts?: number;
  post_reload_routing_command_risk?: AuditEvent["post_reload_routing_command_risk"];
  compile_verification_attempted?: boolean;
  compile_verification_status?: AuditEvent["compile_verification_status"];
  compile_verification_attempts?: number;
  compile_verification_read_requests?: number;
  write_command_repeated?: false;
  policy_status?: PolicyManagerSnapshot;
  statistics?: CommandStatisticsMeasurement;
};

export interface AuditSink {
  prepare(startEvent: AuditEvent): void | Promise<void>;
  write(event: AuditEvent): void | Promise<void>;
  status?(): AuditStoreStatus;
  snapshotStatus?(): AuditStoreStatus;
}

export type AuditRetentionOptions = {
  maxFileBytes?: number;
  maxFiles?: number;
  maxAgeMs?: number;
  maxTotalBytes?: number;
  now?: () => Date;
  logger?: OperationalLogger;
};

export type AuditStoreStatus = {
  schema_version: 1;
  state: "ok" | "unavailable";
  files: number;
  total_bytes: number;
  rotation_period: "utc_day";
};

export const MAX_AUDIT_RECORD_BYTES = 64 * 1024;
export const MIN_AUDIT_FILE_BYTES = 2 * MAX_AUDIT_RECORD_BYTES;
const DEFAULT_AUDIT_LIMITS = Object.freeze({ maxFileBytes: 10 * 1024 * 1024, maxFiles: 31, maxAgeMs: 30 * 86_400_000, maxTotalBytes: 256 * 1024 * 1024 });

export class InMemoryAuditSink implements AuditSink {
  readonly starts: AuditEvent[] = [];
  readonly events: AuditEvent[] = [];

  prepare(startEvent: AuditEvent): void {
    this.starts.push(startEvent);
  }

  write(event: AuditEvent): void {
    this.events.push(event);
  }
}

export class FileAuditSink implements AuditSink {
  readonly filePath: string;
  readonly #limits: Required<Omit<AuditRetentionOptions, "now" | "logger">>;
  readonly #now: () => Date;
  readonly #logger: OperationalLogger | undefined;
  #loggedUnavailable = false;
  #lastStatus: AuditStoreStatus = { schema_version: 1, state: "ok", files: 0, total_bytes: 0, rotation_period: "utc_day" };

  constructor(readonly auditDir: string, options: AuditRetentionOptions = {}) {
    this.filePath = path.join(auditDir, "audit.ndjson");
    this.#limits = {
      maxFileBytes: boundedPositive(options.maxFileBytes, DEFAULT_AUDIT_LIMITS.maxFileBytes, 1024 * 1024 * 1024, MIN_AUDIT_FILE_BYTES),
      maxFiles: boundedPositive(options.maxFiles, DEFAULT_AUDIT_LIMITS.maxFiles, 10_000),
      maxAgeMs: boundedPositive(options.maxAgeMs, DEFAULT_AUDIT_LIMITS.maxAgeMs, 366 * 86_400_000),
      maxTotalBytes: boundedPositive(options.maxTotalBytes, DEFAULT_AUDIT_LIMITS.maxTotalBytes, 10 * 1024 * 1024 * 1024),
    };
    if (this.#limits.maxTotalBytes < this.#limits.maxFileBytes) throw new TypeError("Audit total byte limit must cover one file");
    this.#now = options.now ?? (() => new Date());
    this.#logger = options.logger;
  }

  prepare(startEvent: AuditEvent): void {
    const startLine = serializeAuditEvent(startEvent);
    this.withLock(() => {
      const probe = path.join(this.auditDir, `.audit-probe-${process.pid}-${randomUUID()}`);
      writeNewFileDurable(this.auditDir, probe, "");
      durableUnlinkFile(this.auditDir, probe);
      this.repairIncompleteTail();
      this.cleanup(this.#now());
      const startBytes = Buffer.byteLength(startLine);
      if (this.#limits.maxFileBytes < startBytes + MAX_AUDIT_RECORD_BYTES || this.#limits.maxTotalBytes < startBytes + MAX_AUDIT_RECORD_BYTES) throw new Error("Audit capacity cannot reserve a terminal record");
      this.appendLine(startLine, this.#now());
      this.cleanup(this.#now());
    });
    this.markAvailable();
  }

  write(event: AuditEvent): void {
    const line = serializeAuditEvent(event);
    this.withLock(() => {
      const now = this.#now();
      this.repairIncompleteTail();
      this.appendLine(line, now);
      this.cleanup(now);
    });
    this.markAvailable();
  }

  status(): AuditStoreStatus {
    try {
      this.withLock(() => this.cleanup(this.#now()));
      this.markAvailable();
      return { ...this.#lastStatus };
    } catch {
      this.markUnavailable();
      return { ...this.#lastStatus, state: "unavailable" };
    }
  }

  snapshotStatus(): AuditStoreStatus {
    return { ...this.#lastStatus };
  }

  private rotateIfNeeded(now: Date, incomingBytes: number): void {
    const stat = secureFileStat(this.auditDir, this.filePath);
    if (!stat) return;
    const activeDay = auditFileDay(this.filePath) ?? utcDay(stat.mtime);
    const differentDay = activeDay !== utcDay(now);
    if (!differentDay && stat.size + incomingBytes <= this.#limits.maxFileBytes) return;
    durableRenameFile(this.auditDir, this.filePath, path.join(this.auditDir, `audit-${activeDay}-${stat.mtimeMs.toFixed(0)}-${randomUUID()}.ndjson`));
  }

  private appendLine(line: string, now: Date): void {
    this.rotateIfNeeded(now, Buffer.byteLength(line));
    if (!secureFileStat(this.auditDir, this.filePath)) {
      writeNewFileDurable(this.auditDir, this.filePath, line);
      return;
    }
    const handle = openSecureRegularFile(this.auditDir, this.filePath, fs.constants.O_WRONLY | fs.constants.O_APPEND);
    try { fs.writeFileSync(handle, line, "utf8"); fs.fsyncSync(handle); } finally { fs.closeSync(handle); }
  }

  private cleanup(now: Date): void {
    const active = secureFileStat(this.auditDir, this.filePath);
    const unresolvedTemps = publisherTempFiles(this.auditDir, this.auditDir);
    const rotated = secureRegularFiles(this.auditDir, this.auditDir, (name) => /^audit-(?:corrupt-)?\d{4}-\d{2}-\d{2}-\d+-[0-9a-f-]+\.ndjson$/.test(name))
      .map((filePath) => ({ path: filePath, stat: secureFileStat(this.auditDir, filePath)! }))
      .sort((a, b) => b.stat.mtimeMs - a.stat.mtimeMs);
    let retainedBytes = (active?.size ?? 0) + unresolvedTemps.reduce((total, file) => total + (secureFileStat(this.auditDir, file)?.size ?? 0), 0);
    let retainedFiles = (active ? 1 : 0) + unresolvedTemps.length;
    const baseFiles = retainedFiles;
    for (const [index, file] of rotated.entries()) {
      const keep = now.getTime() - file.stat.mtimeMs <= this.#limits.maxAgeMs
        && index < this.#limits.maxFiles - baseFiles
        && retainedBytes + file.stat.size <= this.#limits.maxTotalBytes;
      if (keep) { retainedBytes += file.stat.size; retainedFiles += 1; } else { durableUnlinkFile(this.auditDir, file.path); }
    }
    this.#lastStatus = { schema_version: 1, state: "ok", files: retainedFiles, total_bytes: retainedBytes, rotation_period: "utc_day" };
  }

  private withLock<T>(action: () => T): T {
    let lock: ReturnType<typeof acquireStorageLock> | undefined;
    try {
      ensureSecureRoot(this.auditDir);
      lock = acquireStorageLock(this.auditDir, ".audit.lock");
      return action();
    } catch (error) {
      this.#lastStatus = { ...this.#lastStatus, state: "unavailable" };
      this.markUnavailable();
      throw error;
    } finally {
      if (lock) {
        try { lock.release(); }
        catch (error) { this.#lastStatus = { ...this.#lastStatus, state: "unavailable" }; this.markUnavailable(); throw error; }
      }
    }
  }

  private markUnavailable(): void {
    if (this.#loggedUnavailable) return;
    this.#loggedUnavailable = true;
    this.#logger?.log("SGG-OP-AUDIT-STATE-001", "unavailable");
  }

  private markAvailable(): void {
    if (!this.#loggedUnavailable) return;
    this.#loggedUnavailable = false;
    this.#logger?.log("SGG-OP-AUDIT-RECOVERED-001");
  }

  private repairIncompleteTail(): void {
    const stat = secureFileStat(this.auditDir, this.filePath);
    if (!stat || stat.size === 0) return;
    if (stat.size > this.#limits.maxFileBytes) { this.rotateCorrupt(stat); return; }
    const readHandle = openSecureRegularFile(this.auditDir, this.filePath, fs.constants.O_RDONLY);
    let lastNewline: number;
    try {
      const finalByte = Buffer.alloc(1);
      fs.readSync(readHandle, finalByte, 0, 1, stat.size - 1);
      if (finalByte[0] === 0x0a) return;
      lastNewline = findLastNewline(readHandle, stat.size, MAX_AUDIT_RECORD_BYTES);
    } finally { fs.closeSync(readHandle); }
    if (lastNewline < 0) { this.rotateCorrupt(stat); return; }
    const handle = openSecureRegularFile(this.auditDir, this.filePath, fs.constants.O_RDWR);
    try { fs.ftruncateSync(handle, lastNewline < 0 ? 0 : lastNewline + 1); fs.fsyncSync(handle); } finally { fs.closeSync(handle); }
  }

  private rotateCorrupt(stat: SecureFileStat): void {
    durableRenameFile(this.auditDir, this.filePath, path.join(this.auditDir, `audit-corrupt-${utcDay(stat.mtime)}-${stat.mtimeMs.toFixed(0)}-${randomUUID()}.ndjson`));
  }
}

export function createAuditEvent(
  tool: ToolName,
  rawArgs: unknown,
  result: GatewayResult,
  startedAtMs: number,
  now: () => Date = () => new Date(),
  runtimeMetadata: AuditRuntimeMetadata | AuditCommandMetadata = {},
  runId = "default",
  auditId: string = randomUUID(),
): AuditEvent {
  const args = getObject(rawArgs);
  const metadata = normalizeRuntimeMetadata(runtimeMetadata);
  const event = {
    schema_version: 1,
    phase: "terminal",
    audit_id: safeIdentifier(auditId, "invalid"),
    timestamp: now().toISOString(),
    run_id: safeIdentifier(runId, "default"),
    tool,
    status: result.status,
    duration_ms: Math.max(0, now().getTime() - startedAtMs),
    args_hash: hashRedactedArgs(rawArgs),
    ...optionalSafeString("request_id", result.request_id ?? args?.request_id),
    ...optionalSafeString("checkpoint_id", args?.checkpoint_id),
    ...optionalSafeString("plan_id", args?.plan_id),
    ...optionalSafeIdentifier("project", result.project ?? args?.project),
    ...optionalSafeString("command", result.command ?? args?.command),
    ...optionalCommandMetadata(metadata.command),
    ...optionalPolicyMetadata(metadata.policy),
    ...optionalNumber("bridge_duration_ms", metadata.bridge_duration_ms),
    ...optionalPositiveInteger("bridge_invoke_attempts", metadata.bridge_invoke_attempts),
    ...(metadata.read_recovery_attempted === true ? { read_recovery_attempted: true } : {}),
    ...(metadata.read_recovery_status ? { read_recovery_status: metadata.read_recovery_status } : {}),
    ...(metadata.post_reload_routing_attempted === true ? { post_reload_routing_attempted: true } : {}),
    ...(metadata.post_reload_routing_status ? { post_reload_routing_status: metadata.post_reload_routing_status } : {}),
    ...optionalNonnegativeInteger("post_reload_routing_attempts", metadata.post_reload_routing_attempts),
    ...(metadata.post_reload_routing_command_risk ? { post_reload_routing_command_risk: metadata.post_reload_routing_command_risk } : {}),
    ...(metadata.compile_verification_attempted === true ? { compile_verification_attempted: true } : {}),
    ...(metadata.compile_verification_status ? { compile_verification_status: metadata.compile_verification_status } : {}),
    ...optionalNonnegativeInteger("compile_verification_attempts", metadata.compile_verification_attempts),
    ...optionalNonnegativeInteger("compile_verification_read_requests", metadata.compile_verification_read_requests),
    ...(metadata.write_command_repeated === false ? { write_command_repeated: false } : {}),
    ...(result.batch_item ? {
      batch_item_index: result.batch_item.index,
      batch_item_state: result.batch_item.state,
      batch_item_queued: result.batch_item.queued,
      batch_item_admitted: result.batch_item.admitted,
      batch_item_bridge_request_issued: result.batch_item.bridge_request_issued,
      batch_item_response_delivered: result.batch_item.response_delivered,
      batch_item_automatic_replay_attempted: result.batch_item.automatic_replay_attempted,
    } : {}),
    ...auditPolicyStatus(metadata.policy_status),
    ...optionalCommandStatistics(metadata.statistics),
    ...optionalOutputMetadata(result),
    ...(result.status === "error" ? { error_code: result.error.code } : {}),
  } satisfies AuditEvent;

  return redactSecrets(event) as AuditEvent;
}

function optionalCommandStatistics(measurement: CommandStatisticsMeasurement | undefined): Pick<AuditEvent,
  "statistics_schema_version" | "statistics_operation" | "statistics_operation_kind" | "statistics_internal" |
  "statistics_request_bytes" | "statistics_command_argument_bytes" | "statistics_full_response_bytes" |
  "statistics_compact_response_bytes" | "statistics_delivered_response_bytes" | "statistics_response_mode" |
  "statistics_dispatched" | "statistics_failure_stage" | "statistics_failure_summary"
> {
  if (!measurement) return {};
  return {
    statistics_schema_version: measurement.schema_version,
    statistics_operation: measurement.operation,
    statistics_operation_kind: measurement.operation_kind,
    statistics_internal: measurement.internal,
    ...optionalNonnegativeInteger("statistics_request_bytes", measurement.request_bytes),
    ...optionalNonnegativeInteger("statistics_command_argument_bytes", measurement.command_argument_bytes),
    ...optionalNonnegativeInteger("statistics_full_response_bytes", measurement.full_response_bytes),
    ...optionalNonnegativeInteger("statistics_compact_response_bytes", measurement.compact_response_bytes),
    ...optionalNonnegativeInteger("statistics_delivered_response_bytes", measurement.delivered_response_bytes),
    statistics_response_mode: measurement.response_mode,
    statistics_dispatched: measurement.dispatched,
    ...(measurement.failure_stage ? { statistics_failure_stage: measurement.failure_stage } : {}),
    ...(measurement.failure_summary ? { statistics_failure_summary: measurement.failure_summary } : {}),
  };
}

export function createAuditStartEvent(tool: ToolName, rawArgs: unknown, now: () => Date = () => new Date(), runId = "default", auditId: string = randomUUID()): AuditEvent {
  const args = getObject(rawArgs);
  return {
    schema_version: 1,
    phase: "start",
    audit_id: safeIdentifier(auditId, "invalid"),
    timestamp: now().toISOString(),
    run_id: safeIdentifier(runId, "default"),
    tool,
    status: "ok",
    duration_ms: 0,
    args_hash: hashRedactedArgs(rawArgs),
    ...optionalSafeString("request_id", args?.request_id),
    ...optionalSafeString("checkpoint_id", args?.checkpoint_id),
    ...optionalSafeString("plan_id", args?.plan_id),
    ...optionalSafeIdentifier("project", args?.project),
    ...optionalSafeString("command", args?.command),
    output_chars: 0,
  };
}

function auditPolicyStatus(snapshot: PolicyManagerSnapshot | undefined): Pick<AuditEvent, "policy_state" | "effective_policy_revision" | "policy_loaded_at" | "policy_last_attempt_at" | "policy_degraded" | "policy_error_kind" | "policy_error_code" | "policy_degraded_since"> {
  return {
    policy_state: snapshot?.state ?? "unconfigured",
    effective_policy_revision: snapshot?.effective_policy_revision ?? null,
    policy_loaded_at: snapshot?.loaded_at ?? null,
    policy_last_attempt_at: snapshot?.last_attempt_at ?? null,
    policy_degraded: snapshot?.degraded ?? false,
    policy_error_kind: snapshot?.last_error_kind ?? null,
    policy_error_code: snapshot?.last_error_code ?? null,
    policy_degraded_since: snapshot?.degraded_since ?? null,
  };
}

export function hashRedactedArgs(rawArgs: unknown): string {
  return `sha256:${createHash("sha256").update(canonicalJsonStringify(redactSecrets(rawArgs))).digest("hex")}`;
}

export function redactSecrets(value: unknown): unknown {
  if (Array.isArray(value)) {
    return value.map((item) => redactSecrets(item));
  }

  if (typeof value !== "object" || value === null) {
    return value;
  }

  const redacted: Record<string, unknown> = {};
  for (const [key, child] of Object.entries(value)) {
    redacted[key] = SECRET_KEY_PATTERN.test(key) ? "[REDACTED]" : redactSecrets(child);
  }
  return redacted;
}

function getObject(value: unknown): Record<string, unknown> | undefined {
  return typeof value === "object" && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : undefined;
}

function optionalSafeIdentifier(key: "project", value: unknown): Record<string, string> {
  return typeof value === "string" && isSafeIdentifier(value) ? { [key]: value } : {};
}

function optionalSafeString(key: "request_id" | "checkpoint_id" | "plan_id" | "command", value: unknown): Record<string, string> {
  return typeof value === "string" && isSafeIdentifier(value) ? { [key]: value } : {};
}

function safeIdentifier(value: string, fallback: string): string { return isSafeIdentifier(value) ? value : fallback; }
function isSafeIdentifier(value: string): boolean { return /^[A-Za-z0-9_.:-]{1,256}$/.test(value); }

function boundedPositive(value: number | undefined, fallback: number, maximum: number, minimum = 1): number {
  const resolved = value ?? fallback;
  if (!Number.isSafeInteger(resolved) || resolved < minimum || resolved > maximum) throw new TypeError("Invalid audit retention limit");
  return resolved;
}

function utcDay(value: Date): string { return value.toISOString().slice(0, 10); }
function auditFileDay(filePath: string): string | undefined {
  const handle = openSecureRegularFile(path.dirname(filePath), filePath, fs.constants.O_RDONLY);
  try {
    const buffer = Buffer.alloc(4096);
    const bytes = fs.readSync(handle, buffer, 0, buffer.length, 0);
    const line = buffer.subarray(0, bytes).toString("utf8").split("\n", 1)[0];
    if (!line) return undefined;
    const timestamp = (JSON.parse(line) as { timestamp?: unknown }).timestamp;
    return typeof timestamp === "string" && /^\d{4}-\d{2}-\d{2}T/.test(timestamp) ? timestamp.slice(0, 10) : undefined;
  } catch { return undefined; } finally { fs.closeSync(handle); }
}

function findLastNewline(handle: number, size: number, maxScanBytes: number): number {
  const chunkSize = 64 * 1024;
  const scanStart = Math.max(0, size - maxScanBytes);
  for (let end = size; end > scanStart;) {
    const start = Math.max(scanStart, end - chunkSize);
    const chunk = Buffer.alloc(end - start);
    fs.readSync(handle, chunk, 0, chunk.length, start);
    const index = chunk.lastIndexOf(0x0a);
    if (index >= 0) return start + index;
    end = start;
  }
  return -1;
}

function serializeAuditEvent(event: AuditEvent): string {
  const line = `${JSON.stringify(redactSecrets(event))}\n`;
  if (Buffer.byteLength(line) > MAX_AUDIT_RECORD_BYTES) throw new Error("Audit event exceeds the strict record limit");
  return line;
}

function optionalNumber(key: "bridge_duration_ms", value: unknown): Record<string, number> {
  return typeof value === "number" && Number.isFinite(value) && value >= 0 ? { [key]: value } : {};
}

function optionalPositiveInteger(key: "bridge_invoke_attempts", value: unknown): Record<string, number> {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 ? { [key]: value } : {};
}

function optionalNonnegativeInteger(key: "compile_verification_attempts" | "compile_verification_read_requests" | "post_reload_routing_attempts" | "statistics_request_bytes" | "statistics_command_argument_bytes" | "statistics_full_response_bytes" | "statistics_compact_response_bytes" | "statistics_delivered_response_bytes", value: unknown): Record<string, number> {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? { [key]: value } : {};
}

function optionalCommandMetadata(metadata: AuditCommandMetadata | undefined): Pick<AuditEvent, "command_risk" | "idempotency" | "timeout_ms" | "command_source" | "package_id" | "package_version" | "manifest_path" | "trust" | "commands_hash" | "command_revision"> {
  return metadata ? {
    command_risk: metadata.risk,
    idempotency: metadata.idempotency,
    timeout_ms: metadata.timeout_ms,
    ...(metadata.source ? { command_source: metadata.source } : {}),
    ...(safeMetadataString(metadata.package_id) ? { package_id: metadata.package_id } : {}),
    ...(safeMetadataString(metadata.package_version) ? { package_version: metadata.package_version } : {}),
    ...(metadata.manifest_path && isSafeRelativePath(metadata.manifest_path) ? { manifest_path: metadata.manifest_path } : {}),
    ...(metadata.trust ? { trust: metadata.trust } : {}),
    ...(safeMetadataString(metadata.commands_hash) ? { commands_hash: metadata.commands_hash } : {}),
    ...(safeMetadataString(metadata.command_revision) ? { command_revision: metadata.command_revision } : {}),
  } : {};
}

function optionalPolicyMetadata(metadata: AuditPolicyMetadata | undefined): Pick<AuditEvent, "policy_action" | "policy_reason" | "policy_pattern" | "approval_status" | "auto_allow_ask" | "auto_allow_known_unity_popups" | "auto_allow_stale_registry_cleanup_without_elicitation"> {
  return metadata ? {
    policy_action: metadata.action,
    policy_reason: safeMetadataString(metadata.reason) ? metadata.reason : "unspecified",
    ...(metadata.pattern && /^[A-Za-z0-9_.*:-]{1,256}$/.test(metadata.pattern) ? { policy_pattern: metadata.pattern } : {}),
    ...(metadata.approval_status ? { approval_status: metadata.approval_status } : {}),
    ...(metadata.auto_allow_ask ? { auto_allow_ask: true } : {}),
    ...(metadata.auto_allow_known_unity_popups ? { auto_allow_known_unity_popups: true } : {}),
    ...(metadata.auto_allow_stale_registry_cleanup_without_elicitation ? { auto_allow_stale_registry_cleanup_without_elicitation: true } : {}),
  } : {};
}

function optionalOutputMetadata(result: GatewayResult): Pick<AuditEvent, "output_chars" | "truncated" | "artifact_refs"> {
  return {
    output_chars: canonicalJsonStringify(redactSecrets(result)).length,
    ...(typeof result.truncated === "boolean" ? { truncated: result.truncated } : {}),
    ...(Array.isArray(result.artifact_refs) ? { artifact_refs: result.artifact_refs.filter(isSafeRelativePath).slice(0, 16) } : {}),
  };
}

function safeMetadataString(value: unknown): value is string { return typeof value === "string" && value.length > 0 && value.length <= 1024; }

function isSafeRelativePath(value: unknown): value is string {
  return typeof value === "string" && value.length <= 1024 && !path.isAbsolute(value) && !/^[A-Za-z]:/.test(value) && !value.split(/[\\/]+/).includes("..");
}

function normalizeRuntimeMetadata(metadata: AuditRuntimeMetadata | AuditCommandMetadata): AuditRuntimeMetadata {
  if ("risk" in metadata && "idempotency" in metadata && "timeout_ms" in metadata) {
    return { command: metadata };
  }
  return metadata;
}
