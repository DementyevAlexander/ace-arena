import { createHash } from "node:crypto";

import { z } from "zod";

import { CanonicalPathService } from "../paths/canonical-path.js";
import { canonicalJsonStringify } from "../security/canonical-json.js";

export const BRIDGE_PROTOCOL_VERSION = 1 as const;
export const BRIDGE_RISKS = ["read", "write", "execute", "destructive", "network"] as const;
export const BRIDGE_IDEMPOTENCY = ["safe", "idempotent", "unsafe"] as const;
export const BRIDGE_COMMAND_SOURCES = ["builtin", "attribute", "manifest", "official_relay"] as const;
export const BRIDGE_COMMAND_TRUST = ["builtin", "trusted_package", "untrusted_package"] as const;
export const BRIDGE_EDITOR_STATUSES = ["editing", "playing", "playing_paused"] as const;
export const HEARTBEAT_STATUSES = ["starting", "ready", "compiling", "reloading", "waiting_for_idle", "playing", "busy", "updating", "stopping", "error"] as const;

export const BRIDGE_PROTOCOL_BOUNDS = {
  catalogCommands: 1_024,
  logEntries: 1_000,
  logMessageChars: 8_000,
  logStackChars: 12_000,
  diagnostics: 512,
  identifierChars: 256,
  pathChars: 4_096,
  descriptionChars: 16_384,
  statusValues: 16,
  tags: 128,
  timeoutMs: 3_600_000,
  jsonDepth: 32,
  jsonNodes: 50_000,
  jsonObjectKeys: 2_048,
  jsonArrayItems: 4_096,
  jsonKeyChars: 256,
  jsonStringChars: 262_144,
  jsonTotalStringChars: 1_048_576,
  heartbeatBytes: 256 * 1_024,
  counter: 2_147_483_647,
  protocolIssues: 32,
  receivedPrimitiveChars: 64,
  invokeBytes: 4 * 1024 * 1024,
  requestIdChars: 128,
  featureFlags: 256,
  monoBehaviourAllowlist: 1_000,
  invokeDiagnostics: 100,
  errorCodeChars: 64,
  durationMs: 3_600_000,
} as const;

export const BRIDGE_PROTOCOL_ERROR_CODES = ["protocol_version_mismatch", "protocol_validation_error"] as const;
export type BridgeProtocolErrorCode = (typeof BRIDGE_PROTOCOL_ERROR_CODES)[number];
export type BridgeProtocolEnvelope = "heartbeat" | "health" | "command_catalog" | "single_command" | "logs" | "invoke_request" | "invoke_response";
export type BridgeProtocolIssue = { path: Array<string | number>; code: string };
export type BridgeProtocolDiagnostic = {
  code: BridgeProtocolErrorCode;
  envelope: BridgeProtocolEnvelope;
  protocol_schema: string;
  reason: "missing_schema_version" | "unsupported_schema_version" | "schema_validation_failed" | "project_mismatch" | "instance_id_mismatch" | "catalog_generation_mismatch" | "commands_hash_mismatch" | "request_id_mismatch" | "serialized_size_exceeded";
  expected_version: typeof BRIDGE_PROTOCOL_VERSION;
  received_version?: string | number | boolean | null;
  issues?: BridgeProtocolIssue[];
};

export class BridgeProtocolError extends Error {
  readonly code: BridgeProtocolErrorCode;
  readonly diagnostic: BridgeProtocolDiagnostic;

  constructor(code: BridgeProtocolErrorCode, envelope: BridgeProtocolEnvelope, diagnostic?: Omit<BridgeProtocolDiagnostic, "code" | "envelope" | "protocol_schema" | "expected_version">) {
    super(code);
    this.name = "BridgeProtocolError";
    this.code = code;
    this.diagnostic = {
      code,
      envelope,
      protocol_schema: `sgg.unity_mcp.bridge.${envelope}.v1`,
      expected_version: BRIDGE_PROTOCOL_VERSION,
      reason: diagnostic?.reason ?? "schema_validation_failed",
      ...(diagnostic?.received_version !== undefined ? { received_version: diagnostic.received_version } : {}),
      ...(diagnostic?.issues ? { issues: diagnostic.issues } : {}),
    };
  }
}

const canonicalPaths = new CanonicalPathService();
const boundedIdentifier = z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.identifierChars);
const boundedPath = z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.pathChars);
const boundedDescription = z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.descriptionChars);
const jsonObjectSchema = z.custom<Record<string, unknown>>((value) => isBoundedJson(value, true), { message: "bounded JSON object required" });
const boundedJsonSchema = z.custom<unknown>((value) => isBoundedJson(value), { message: "bounded JSON required" });
const safeRequestId = z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.requestIdChars).regex(/^[A-Za-z0-9._-]+$/);
const safeErrorCode = z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.errorCodeChars).regex(/^[a-z][a-z0-9_]*$/);

const wrapperWaitRangeV1Shape = {
  min_ms: z.number().int().min(1).max(BRIDGE_PROTOCOL_BOUNDS.durationMs),
  max_ms: z.number().int().min(1).max(BRIDGE_PROTOCOL_BOUNDS.durationMs),
};

const wrapperWaitContractV1Schema = z.object({
  mode: z.literal("wait"),
  wait_for_replacement_ms: z.object({
    ...wrapperWaitRangeV1Shape,
    deadline_scope: z.literal("pre_dispatch_only"),
  }).refine((range) => range.min_ms <= range.max_ms, { message: "wrapper wait minimum must not exceed maximum" }).optional(),
  wait_for_test_run_terminal_ms: z.object({
    ...wrapperWaitRangeV1Shape,
    compile_readiness_contract: z.string().min(1).max(128).optional(),
    deadline_scope: z.literal("start_and_terminal_poll"),
    response_mode: z.literal("compact"),
    dry_run: z.literal(false),
  }).refine((range) => range.min_ms <= range.max_ms, { message: "wrapper wait minimum must not exceed maximum" }).optional(),
}).refine((contract) => contract.wait_for_replacement_ms !== undefined || contract.wait_for_test_run_terminal_ms !== undefined, {
  message: "wrapper wait contract must advertise at least one field",
});

const pathAliasV1Schema = z.object({
  environment: boundedIdentifier,
  host_path: boundedPath,
  canonical_path: boundedPath,
});

const dirtySceneV1Schema = z.object({
  scene_path: z.string().max(BRIDGE_PROTOCOL_BOUNDS.pathChars),
  scene_name: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
  is_active: z.boolean(),
});

const packageUpdateV1Schema = z.object({
  status: z.enum(["current", "unavailable", "package_refresh_required", "stale_package_resolution"]),
  auto_refresh_enabled: z.union([z.boolean(), z.literal("unknown")]),
  refresh_pending: z.boolean(),
  package_refresh_required: z.boolean(),
  refresh_wait_can_advance: z.boolean().optional(),
  stale_package_resolution: z.boolean(),
  fingerprint: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
  pending_fingerprint: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
  refresh_count: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter),
  last_refresh_utc: z.union([z.literal(""), z.string().datetime()]),
  last_observed_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  last_transition_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  fingerprint_failure: z.object({
    evidence_class: z.enum(["package_runtime", "project_packages"]),
    reason: z.enum(["budget_exhausted", "replacement_race", "unstable_metadata", "missing_file", "path_security_rejected", "size_limit_exceeded", "read_exception"]),
    file: z.enum(["manifest", "packages_lock", "unknown"]),
  }).nullable().optional(),
  local_package_code_inventory: z.object({
    status: z.enum(["current", "stale", "unavailable"]),
    failure_reason: z.enum([
      "",
      "not_scanned",
      "snapshot_unavailable",
      "input_unavailable",
      "package_limit_exceeded",
      "asset_path_limit_exceeded",
      "package_identity_invalid",
      "resolved_path_unavailable",
      "path_security_rejected",
      "budget_exhausted",
      "directory_limit_exceeded",
      "file_limit_exceeded",
      "duplicate_asset_path",
      "compiler_source_limit_exceeded",
      "compiler_source_read_exception",
      "read_exception",
    ]),
    mismatch_package: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
    local_package_count: z.number().int().min(0).max(64),
    source_file_count: z.number().int().min(0).max(16_384),
    asset_database_file_count: z.number().int().min(0).max(16_384),
    missing_asset_file_count: z.number().int().min(0).max(16_384),
    unexpected_asset_file_count: z.number().int().min(0).max(16_384),
    compiler_source_file_count: z.number().int().min(0).max(16_384).optional(),
    missing_compiler_source_file_count: z.number().int().min(0).max(16_384).optional(),
    unexpected_compiler_source_file_count: z.number().int().min(0).max(16_384).optional(),
    source_fingerprint: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
    asset_database_fingerprint: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
    source_state_fingerprint: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
    compiler_source_fingerprint: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars).optional(),
  }).superRefine((inventory, context) => {
    const compilerFields = [inventory.compiler_source_file_count, inventory.missing_compiler_source_file_count, inventory.unexpected_compiler_source_file_count, inventory.compiler_source_fingerprint];
    const presentCount = compilerFields.filter((field) => field !== undefined).length;
    if (presentCount !== 0 && presentCount !== compilerFields.length) {
      context.addIssue({ code: "custom", path: ["compiler_source_file_count"], message: "Compiler source inventory evidence must be complete or omitted" });
    }
  }).optional(),
  message: z.string().max(BRIDGE_PROTOCOL_BOUNDS.descriptionChars),
});

const backgroundCompilerProgressV1Schema = z.object({
  observed: z.boolean(),
  authoritative: z.boolean(),
  active: z.boolean(),
  failed: z.boolean(),
  blocks_readiness: z.boolean(),
  state: z.enum(["unobserved", "settling", "active", "complete", "failed", "unavailable"]),
  category: z.enum(["none", "compute_variant_compile", "shader_variant_compile", "native_compiler", "unknown"]),
  relevant_item_count: z.number().int().min(0).max(256),
  active_item_count: z.number().int().min(0).max(256),
  progress_permille: z.number().int().min(0).max(1000).nullable(),
  process_id: z.number().int().positive().max(2_147_483_647),
  catalog_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter),
  compilation_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter),
  completed_compilation_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter),
  package_fingerprint: z.string().max(BRIDGE_PROTOCOL_BOUNDS.identifierChars),
  observed_utc: z.union([z.literal(""), z.string().datetime()]),
  failure_reason: z.string().max(BRIDGE_PROTOCOL_BOUNDS.descriptionChars),
  correlated_native_failure_observed: z.boolean().optional(),
  native_failure_category: z.enum(["none", "compute_variant_compile", "shader_variant_compile", "native_compiler"]).optional(),
  native_failure_observed_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  native_failure_absence_authoritative: z.literal(false).optional(),
  cancellation_attempted: z.literal(false),
  work_replayed: z.literal(false),
}).superRefine((progress, context) => {
  if (progress.correlated_native_failure_observed === true
    && (!progress.authoritative || progress.native_failure_category === "none" || progress.native_failure_category === undefined
      || !progress.native_failure_observed_utc || !progress.observed_utc
      || Date.parse(progress.native_failure_observed_utc) > Date.parse(progress.observed_utc))) {
    context.addIssue({ code: "custom", path: ["correlated_native_failure_observed"], message: "native failure observation requires a correlated authoritative progress item" });
  }
  if (progress.correlated_native_failure_observed !== true
    && ((progress.native_failure_category !== undefined && progress.native_failure_category !== "none") || Boolean(progress.native_failure_observed_utc))) {
    context.addIssue({ code: "custom", path: ["native_failure_category"], message: "absent native failure evidence cannot name an observed failure" });
  }
  if (progress.completed_compilation_generation > progress.compilation_generation) {
    context.addIssue({ code: "custom", path: ["completed_compilation_generation"], message: "background compiler completed generation exceeds observed generation" });
  }
  if (progress.state === "active" && (!progress.authoritative || !progress.active || !progress.blocks_readiness)) {
    context.addIssue({ code: "custom", path: ["state"], message: "active background compiler state requires authoritative active blocking evidence" });
  }
  if (progress.state === "complete" && (!progress.authoritative || progress.active || progress.failed || progress.blocks_readiness)) {
    context.addIssue({ code: "custom", path: ["state"], message: "complete background compiler state must be authoritative and non-blocking" });
  }
});

export const healthResponseV1Schema = z.object({
  schema_version: z.literal(BRIDGE_PROTOCOL_VERSION),
  instance_id: boundedIdentifier,
  project: boundedIdentifier,
  status: z.enum(HEARTBEAT_STATUSES),
  unity_version: boundedIdentifier,
  commands_hash: boundedIdentifier,
  is_compiling: z.boolean(),
  is_updating: z.boolean(),
  is_playing: z.boolean(),
  ready: z.boolean(),
  background_compiler_progress: backgroundCompilerProgressV1Schema.optional(),
  catalog_status: z.enum(["pending", "ready", "unavailable"]).optional(),
  catalog_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  catalog_ready: z.boolean().optional(),
  listener_ready: z.boolean().optional(),
  package_update: packageUpdateV1Schema,
  has_unsaved_scene_changes: z.boolean(),
  dirty_scene_count: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter),
  dirty_scenes: z.array(dirtySceneV1Schema).max(20),
  queue_depth: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter),
}).superRefine((health, context) => {
  const catalogFields = [health.catalog_status, health.catalog_generation, health.catalog_ready, health.listener_ready];
  const present = catalogFields.filter((value) => value !== undefined).length;
  if (present !== 0 && present !== catalogFields.length) {
    context.addIssue({ code: "custom", path: ["catalog_status"], message: "catalog health fields must be supplied together" });
  }
  if (health.catalog_status === "ready" && (health.catalog_generation === undefined || health.catalog_generation <= 0 || health.catalog_ready !== true || health.listener_ready !== true)) {
    context.addIssue({ code: "custom", path: ["catalog_status"], message: "ready catalog health requires a published catalog and live listener" });
  }
  if (health.ready && health.background_compiler_progress?.blocks_readiness === true) {
    context.addIssue({ code: "custom", path: ["background_compiler_progress"], message: "ready health cannot include blocking background compiler progress" });
  }
});

export type HealthResponseV1 = z.infer<typeof healthResponseV1Schema>;

export const heartbeatRecordV1Schema = z.object({
  schema_version: z.literal(BRIDGE_PROTOCOL_VERSION),
  instance_id: boundedIdentifier,
  project: boundedIdentifier,
  display_name: boundedIdentifier.optional(),
  package_version: boundedIdentifier.optional(),
  project_root: boundedPath,
  canonical_project_root: boundedPath.optional(),
  aliases: z.array(pathAliasV1Schema).max(32).optional(),
  unity_version: boundedIdentifier,
  unity_pid: z.number().int().positive().max(2_147_483_647),
  unity_process_start_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  bridge_host: boundedIdentifier,
  bridge_port: z.number().int().min(0).max(65_535),
  token: z.string().max(4_096).optional(),
  status: z.enum(HEARTBEAT_STATUSES),
  has_unsaved_scene_changes: z.boolean().optional(),
  dirty_scene_count: z.number().int().min(0).max(1_000_000).optional(),
  dirty_scenes: z.array(dirtySceneV1Schema).max(20).optional(),
  commands_hash: boundedIdentifier,
  catalog_status: z.enum(["pending", "ready", "unavailable"]).optional(),
  catalog_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  heartbeat_generation: z.number().int().min(1).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  refresh_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  last_asset_refresh_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  compilation_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  completed_compilation_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  last_compilation_started_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  last_compilation_finished_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  background_compiler_progress_state: z.enum(["unobserved", "settling", "active", "complete", "failed", "unavailable"]).optional(),
  background_compiler_progress_authoritative: z.boolean().optional(),
  background_compiler_progress_active: z.boolean().optional(),
  background_compiler_progress_failed: z.boolean().optional(),
  background_compiler_progress_category: z.enum(["none", "compute_variant_compile", "shader_variant_compile", "native_compiler", "unknown"]).optional(),
  background_compiler_progress_catalog_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  background_compiler_progress_compilation_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  background_compiler_progress_completed_compilation_generation: z.number().int().min(0).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  background_compiler_progress_observed_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  quit_operation_id: z.union([z.literal(""), boundedIdentifier]).optional(),
  quit_caller_args_hash: z.union([z.literal(""), z.string().regex(/^sha256:[a-f0-9]{64}$/)]).optional(),
  quit_request_state: z.enum(["none", "deferred", "requesting", "accepted", "cancelled", "ignored", "uncertain"]).optional(),
  quit_requested_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  quit_state_updated_utc: z.union([z.literal(""), z.string().datetime()]).optional(),
  last_heartbeat: z.string().datetime(),
}).superRefine((record, context) => {
  const compilationGeneration = record.compilation_generation ?? 0;
  const completedGeneration = record.completed_compilation_generation ?? 0;
  const backgroundProgressFields = [
    record.background_compiler_progress_state,
    record.background_compiler_progress_authoritative,
    record.background_compiler_progress_active,
    record.background_compiler_progress_failed,
    record.background_compiler_progress_category,
    record.background_compiler_progress_catalog_generation,
    record.background_compiler_progress_compilation_generation,
    record.background_compiler_progress_completed_compilation_generation,
    record.background_compiler_progress_observed_utc,
  ];
  const backgroundProgressFieldsPresent = backgroundProgressFields.filter((value) => value !== undefined).length;
  if (backgroundProgressFieldsPresent !== 0 && backgroundProgressFieldsPresent !== backgroundProgressFields.length) {
    context.addIssue({ code: "custom", path: ["background_compiler_progress_state"], message: "background compiler heartbeat fields must be supplied together" });
  }
  if (record.background_compiler_progress_state === "active"
    && (record.background_compiler_progress_authoritative !== true || record.background_compiler_progress_active !== true || record.background_compiler_progress_failed === true)) {
    context.addIssue({ code: "custom", path: ["background_compiler_progress_state"], message: "active background compiler heartbeat requires authoritative active evidence" });
  }
  if ((record.status === "ready" || record.status === "playing")
    && record.background_compiler_progress_state !== undefined
    && record.background_compiler_progress_state !== "complete"
    && record.background_compiler_progress_state !== "unobserved") {
    context.addIssue({ code: "custom", path: ["background_compiler_progress_state"], message: "routable heartbeat cannot publish blocking background compiler progress" });
  }
  if (completedGeneration > compilationGeneration) {
    context.addIssue({ code: "custom", path: ["completed_compilation_generation"], message: "completed compilation generation exceeds observed generation" });
  }
  if (compilationGeneration > 0 && !record.last_compilation_started_utc) {
    context.addIssue({ code: "custom", path: ["last_compilation_started_utc"], message: "observed compilation generation requires a start timestamp" });
  }
  if (compilationGeneration === 0 && record.last_compilation_started_utc) {
    context.addIssue({ code: "custom", path: ["last_compilation_started_utc"], message: "compilation start timestamp requires an observed generation" });
  }
  if (completedGeneration > 0 && completedGeneration === compilationGeneration && !record.last_compilation_finished_utc) {
    context.addIssue({ code: "custom", path: ["last_compilation_finished_utc"], message: "current completed compilation generation requires a finish timestamp" });
  }
  if (completedGeneration === 0 && record.last_compilation_finished_utc) {
    context.addIssue({ code: "custom", path: ["last_compilation_finished_utc"], message: "compilation finish timestamp requires a completed generation" });
  }
  if (completedGeneration < compilationGeneration && record.last_compilation_finished_utc) {
    context.addIssue({ code: "custom", path: ["last_compilation_finished_utc"], message: "active compilation generation cannot have a finish timestamp" });
  }
  if ((record.refresh_generation ?? 0) > 0 && !record.last_asset_refresh_utc) {
    context.addIssue({ code: "custom", path: ["last_asset_refresh_utc"], message: "observed refresh generation requires a refresh timestamp" });
  }
  if ((record.refresh_generation ?? 0) === 0 && record.last_asset_refresh_utc) {
    context.addIssue({ code: "custom", path: ["last_asset_refresh_utc"], message: "refresh timestamp requires an observed refresh generation" });
  }
  if ((record.status === "ready" || record.status === "playing") && record.bridge_port <= 0) {
    context.addIssue({ code: "custom", path: ["bridge_port"], message: "routable heartbeat status requires a positive bridge port" });
  }
  if ((record.status === "ready" || record.status === "playing") && record.catalog_generation !== undefined && record.catalog_generation <= 0) {
    context.addIssue({ code: "custom", path: ["catalog_generation"], message: "routable heartbeat status requires a published catalog generation" });
  }
  if ((record.status === "ready" || record.status === "playing") && record.catalog_status !== undefined && record.catalog_status !== "ready") {
    context.addIssue({ code: "custom", path: ["catalog_status"], message: "routable heartbeat status requires a ready catalog" });
  }
  const catalogFields = [record.catalog_status, record.catalog_generation];
  const catalogFieldsPresent = catalogFields.filter((value) => value !== undefined).length;
  if (catalogFieldsPresent !== 0 && catalogFieldsPresent !== catalogFields.length) {
    context.addIssue({ code: "custom", path: ["catalog_status"], message: "catalog heartbeat fields must be supplied together" });
  }
  if (record.catalog_status === "ready" && (record.catalog_generation === undefined || record.catalog_generation <= 0 || record.bridge_port <= 0 || record.commands_hash === "bootstrap-pending")) {
    context.addIssue({ code: "custom", path: ["catalog_status"], message: "ready catalog heartbeat requires a published catalog and live listener" });
  }
  if (record.catalog_status === "pending" && record.catalog_generation !== undefined && record.catalog_generation !== 0) {
    context.addIssue({ code: "custom", path: ["catalog_generation"], message: "pending catalog heartbeat cannot publish a catalog generation" });
  }
  const quitOperationId = record.quit_operation_id ?? "";
  const quitState = record.quit_request_state ?? "none";
  if (quitOperationId && (!record.quit_caller_args_hash || !record.quit_requested_utc || !record.quit_state_updated_utc || quitState === "none")) {
    context.addIssue({ code: "custom", path: ["quit_operation_id"], message: "quit operation requires caller binding, timestamps, and lifecycle state" });
  }
  if (!quitOperationId && (record.quit_caller_args_hash || record.quit_requested_utc || record.quit_state_updated_utc || quitState !== "none")) {
    context.addIssue({ code: "custom", path: ["quit_request_state"], message: "quit lifecycle evidence requires an operation id" });
  }
}).transform((record) => ({
  ...record,
  canonical_project_root: canonicalPaths.normalizeProjectRoot(record.project_root),
  aliases: canonicalPaths.projectRootAliases(record.project_root),
}));

export type HeartbeatRecordV1 = z.infer<typeof heartbeatRecordV1Schema>;
export type HeartbeatRecord = HeartbeatRecordV1;

export const commandDescriptorV1Schema = z.object({
  id: boundedIdentifier,
  title: boundedIdentifier,
  description: boundedDescription,
  package: boundedIdentifier,
  risk: z.enum(BRIDGE_RISKS),
  idempotency: z.enum(BRIDGE_IDEMPOTENCY),
  timeout_ms: z.number().int().positive().max(BRIDGE_PROTOCOL_BOUNDS.timeoutMs),
  schema: jsonObjectSchema.refine((schema) => schema.type === "object", { message: "command schema type must be object" }),
  tags: z.array(boundedIdentifier).max(BRIDGE_PROTOCOL_BOUNDS.tags).optional(),
  capability: boundedIdentifier.optional(),
  requires_editor_status: z.array(z.enum(BRIDGE_EDITOR_STATUSES)).max(BRIDGE_PROTOCOL_BOUNDS.statusValues).optional(),
  result_shape: boundedIdentifier.optional(),
  output_limit: jsonObjectSchema.optional(),
  supports_job: z.boolean().optional(),
  supports_cancel: z.boolean().optional(),
  wrapper_wait: wrapperWaitContractV1Schema.optional(),
  feature_flag: boundedIdentifier.optional(),
  source: z.enum(BRIDGE_COMMAND_SOURCES).optional(),
  package_id: boundedIdentifier.optional(),
  package_version: boundedIdentifier.optional(),
  manifest_path: boundedPath.optional(),
  trust: z.enum(BRIDGE_COMMAND_TRUST).optional(),
});

export type CommandDescriptorV1 = z.infer<typeof commandDescriptorV1Schema>;
export type CommandDescriptor = CommandDescriptorV1;

export const manifestScanDiagnosticV1Schema = z.object({
  code: boundedIdentifier,
  package_id: boundedIdentifier.optional(),
  package_version: boundedIdentifier.optional(),
  manifest_path: boundedPath.optional(),
  message: z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.descriptionChars),
});

export const projectManifestCandidateV1Schema = z.object({
  manifest_path: boundedPath,
  loadable: z.literal(false),
  reason: z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.descriptionChars),
});

export type ManifestScanDiagnostic = z.infer<typeof manifestScanDiagnosticV1Schema>;
export type ProjectManifestCandidate = z.infer<typeof projectManifestCandidateV1Schema>;

export const commandCatalogV1Schema = z.object({
  schema_version: z.literal(BRIDGE_PROTOCOL_VERSION),
  instance_id: boundedIdentifier.optional(),
  project: boundedIdentifier,
  commands_hash: boundedIdentifier,
  catalog_generation: z.number().int().min(1).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  commands: z.array(commandDescriptorV1Schema).max(BRIDGE_PROTOCOL_BOUNDS.catalogCommands).refine((commands) => new Set(commands.map((command) => command.id)).size === commands.length, { message: "command ids must be unique" }),
  manifest_diagnostics: z.array(manifestScanDiagnosticV1Schema).max(BRIDGE_PROTOCOL_BOUNDS.diagnostics).optional(),
  project_manifest_candidates: z.array(projectManifestCandidateV1Schema).max(BRIDGE_PROTOCOL_BOUNDS.diagnostics).optional(),
}).superRefine((catalog, context) => {
  if ((catalog.instance_id === undefined) !== (catalog.catalog_generation === undefined)) {
    context.addIssue({ code: "custom", path: ["catalog_generation"], message: "catalog identity fields must be supplied together" });
  }
});

export type CommandCatalogV1 = z.infer<typeof commandCatalogV1Schema>;

export const singleCommandResponseV1Schema = z.object({
  schema_version: z.literal(BRIDGE_PROTOCOL_VERSION),
  instance_id: boundedIdentifier.optional(),
  project: boundedIdentifier,
  commands_hash: boundedIdentifier,
  catalog_generation: z.number().int().min(1).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  command: commandDescriptorV1Schema.nullable(),
}).superRefine((response, context) => {
  if ((response.instance_id === undefined) !== (response.catalog_generation === undefined)) {
    context.addIssue({ code: "custom", path: ["catalog_generation"], message: "catalog identity fields must be supplied together" });
  }
});

export type SingleCommandResponseV1 = z.infer<typeof singleCommandResponseV1Schema>;

export const logEntryV1Schema = z.object({
  timestamp: z.string().datetime(),
  type: z.enum(["Error", "Assert", "Warning", "Log", "Exception"]),
  message: z.string().max(BRIDGE_PROTOCOL_BOUNDS.logMessageChars),
  stack_trace: z.string().max(BRIDGE_PROTOCOL_BOUNDS.logStackChars).optional(),
});

const consoleLogBufferMetadataV1Schema = z.object({
  epoch: z.string().regex(/^[0-9a-f]{32}$/),
  initialized_at_utc: z.string().datetime(),
  reset_loss: z.boolean(),
  history_complete: z.literal(false),
});

export const logsResponseV1Schema = z.object({
  schema_version: z.literal(BRIDGE_PROTOCOL_VERSION),
  entries: z.array(logEntryV1Schema).max(BRIDGE_PROTOCOL_BOUNDS.logEntries),
  buffer: consoleLogBufferMetadataV1Schema.optional(),
});

export type LogEntryV1 = z.infer<typeof logEntryV1Schema>;
export type LogsResponseV1 = z.infer<typeof logsResponseV1Schema>;

const bridgeInvokeSettingsV1Schema = z.object({
  asset_index_cache_dir: boundedPath.optional(),
  feature_flags: z.record(z.string().min(1).max(BRIDGE_PROTOCOL_BOUNDS.identifierChars).regex(/^[A-Za-z0-9._-]+$/), z.boolean())
    .refine((flags) => Object.keys(flags).length <= BRIDGE_PROTOCOL_BOUNDS.featureFlags, { message: "too many feature flags" })
    .optional(),
  mono_behaviour_allowlist: z.array(z.object({
    assembly_name: boundedIdentifier,
    full_type_name: boundedIdentifier,
    note: z.string().max(BRIDGE_PROTOCOL_BOUNDS.descriptionChars).optional(),
  })).max(BRIDGE_PROTOCOL_BOUNDS.monoBehaviourAllowlist).optional(),
});

export const bridgeInvokeRequestV1Schema = z.object({
  schema_version: z.literal(BRIDGE_PROTOCOL_VERSION),
  request_id: safeRequestId.optional(),
  project: boundedIdentifier,
  command: boundedIdentifier,
  args: jsonObjectSchema,
  settings: bridgeInvokeSettingsV1Schema.optional(),
  deadline_unix_ms: z.number().int().positive().safe().optional(),
  risk: z.enum(BRIDGE_RISKS).optional(),
  expected_instance_id: boundedIdentifier.optional(),
  expected_catalog_generation: z.number().int().min(1).max(BRIDGE_PROTOCOL_BOUNDS.counter).optional(),
  expected_commands_hash: boundedIdentifier.optional(),
}).superRefine((request, context) => {
  const bindings = [request.expected_instance_id, request.expected_catalog_generation, request.expected_commands_hash];
  const present = bindings.filter((value) => value !== undefined).length;
  if (present !== 0 && present !== bindings.length) {
    context.addIssue({ code: "custom", path: ["expected_catalog_generation"], message: "catalog binding fields must be supplied together" });
  }
});

const bridgeInvokeDiagnosticV1Schema = z.object({
  severity: z.enum(["info", "warning", "error"]),
  source: z.enum(["gateway", "bridge", "unity"]),
  message: boundedDescription,
  details: jsonObjectSchema.optional(),
});

const invokeResponseCommon = {
  schema_version: z.literal(BRIDGE_PROTOCOL_VERSION),
  request_id: safeRequestId.optional(),
  diagnostics: z.array(bridgeInvokeDiagnosticV1Schema).max(BRIDGE_PROTOCOL_BOUNDS.invokeDiagnostics),
  duration_ms: z.number().finite().min(0).max(BRIDGE_PROTOCOL_BOUNDS.durationMs),
};

export const bridgeInvokeOkResponseV1Schema = z.object({
  ...invokeResponseCommon,
  status: z.literal("ok"),
  data: boundedJsonSchema,
});

export const bridgeInvokeErrorResponseV1Schema = z.object({
  ...invokeResponseCommon,
  status: z.literal("error"),
  error: z.object({
    code: safeErrorCode,
    message: boundedDescription,
    details: jsonObjectSchema.optional(),
  }),
});

export const bridgeInvokeResponseV1Schema = z.discriminatedUnion("status", [bridgeInvokeOkResponseV1Schema, bridgeInvokeErrorResponseV1Schema]);
export type BridgeInvokeRequestV1 = z.infer<typeof bridgeInvokeRequestV1Schema>;
export type BridgeInvokeResponseV1 = z.infer<typeof bridgeInvokeResponseV1Schema>;

export function parseHeartbeatRecordV1(value: unknown): HeartbeatRecordV1 {
  return parseVersionedEnvelope(value, "heartbeat", heartbeatRecordV1Schema);
}

export function parseCommandDescriptorV1(value: unknown): CommandDescriptorV1 {
  const parsed = commandDescriptorV1Schema.safeParse(value);
  if (!parsed.success) throw validationError("command_catalog", parsed.error);
  return parsed.data;
}

export function parseCommandCatalogV1(value: unknown): CommandCatalogV1 {
  return parseVersionedEnvelope(value, "command_catalog", commandCatalogV1Schema);
}

export function parseHealthResponseV1(value: unknown): HealthResponseV1 {
  return parseVersionedEnvelope(value, "health", healthResponseV1Schema);
}

export function parseSingleCommandResponseV1(value: unknown): SingleCommandResponseV1 {
  return parseVersionedEnvelope(value, "single_command", singleCommandResponseV1Schema);
}

export function parseLogsResponseV1(value: unknown): LogsResponseV1 {
  return parseVersionedEnvelope(value, "logs", logsResponseV1Schema);
}

export function parseBridgeInvokeRequestV1(value: unknown): BridgeInvokeRequestV1 {
  return parseVersionedEnvelope(value, "invoke_request", bridgeInvokeRequestV1Schema);
}

export function serializeBridgeInvokeRequestV1(value: unknown): string {
  const serialized = JSON.stringify(parseBridgeInvokeRequestV1(value));
  if (Buffer.byteLength(serialized, "utf8") > BRIDGE_PROTOCOL_BOUNDS.invokeBytes) {
    throw new BridgeProtocolError("protocol_validation_error", "invoke_request", { reason: "serialized_size_exceeded" });
  }
  return serialized;
}

export function parseBridgeInvokeResponseV1(value: unknown, expectedRequestId?: string): BridgeInvokeResponseV1 {
  const response = parseVersionedEnvelope(value, "invoke_response", bridgeInvokeResponseV1Schema);
  if (expectedRequestId !== undefined && response.request_id !== expectedRequestId) {
    throw new BridgeProtocolError("protocol_validation_error", "invoke_response", {
      reason: "request_id_mismatch",
      issues: [{ path: ["request_id"], code: "custom" }],
    });
  }
  return response;
}

export function assertBridgeResponseIdentity(record: Pick<HeartbeatRecordV1, "project" | "instance_id" | "commands_hash" | "catalog_generation">, response: Pick<HealthResponseV1, "project" | "instance_id" | "ready" | "commands_hash" | "catalog_generation"> | Pick<CommandCatalogV1, "project" | "instance_id" | "commands_hash" | "catalog_generation">): void {
  const envelope = "ready" in response ? "health" : "command_catalog";
  if (response.project !== record.project) {
    throw new BridgeProtocolError("protocol_validation_error", envelope, {
      reason: "project_mismatch",
      issues: [{ path: ["project"], code: "custom" }],
    });
  }
  if ("instance_id" in response && response.instance_id !== record.instance_id) {
    throw new BridgeProtocolError("protocol_validation_error", envelope, {
      reason: "instance_id_mismatch",
      issues: [{ path: ["instance_id"], code: "custom" }],
    });
  }
  if (record.catalog_generation !== undefined) {
    if (response.instance_id !== record.instance_id) {
      throw new BridgeProtocolError("protocol_validation_error", envelope, {
        reason: "instance_id_mismatch",
        issues: [{ path: ["instance_id"], code: "custom" }],
      });
    }
    if (response.catalog_generation !== record.catalog_generation) {
      throw new BridgeProtocolError("protocol_validation_error", envelope, {
        reason: "catalog_generation_mismatch",
        issues: [{ path: ["catalog_generation"], code: "custom" }],
      });
    }
    if (response.commands_hash !== record.commands_hash) {
      throw new BridgeProtocolError("protocol_validation_error", envelope, {
        reason: "commands_hash_mismatch",
        issues: [{ path: ["commands_hash"], code: "custom" }],
      });
    }
  }
}

export type EffectiveCommandDescriptorV1 = {
  [Key in keyof Required<CommandDescriptorV1>]: undefined extends CommandDescriptorV1[Key]
    ? Exclude<CommandDescriptorV1[Key], undefined> | null
    : CommandDescriptorV1[Key];
};

export function normalizeEffectiveCommandDescriptor(descriptor: CommandDescriptorV1): EffectiveCommandDescriptorV1 {
  const parsed = parseCommandDescriptorV1(descriptor);
  return {
    id: parsed.id,
    title: parsed.title,
    description: parsed.description,
    package: parsed.package,
    risk: parsed.risk,
    idempotency: parsed.idempotency,
    timeout_ms: parsed.timeout_ms,
    schema: parsed.schema,
    tags: normalizeSet(parsed.tags),
    capability: parsed.capability ?? null,
    requires_editor_status: normalizeSet(parsed.requires_editor_status),
    result_shape: parsed.result_shape ?? null,
    output_limit: parsed.output_limit ?? null,
    supports_job: parsed.supports_job ?? null,
    supports_cancel: parsed.supports_cancel ?? null,
    wrapper_wait: parsed.wrapper_wait ?? null,
    feature_flag: parsed.feature_flag ?? null,
    source: parsed.source ?? null,
    package_id: parsed.package_id ?? null,
    package_version: parsed.package_version ?? null,
    manifest_path: parsed.manifest_path ?? null,
    trust: parsed.trust ?? null,
  };
}

export function commandDescriptorRevision(descriptor: CommandDescriptorV1): string {
  return hashRevision(normalizeEffectiveCommandDescriptor(descriptor));
}

export function commandCatalogRevision(descriptors: readonly CommandDescriptorV1[]): string {
  if (descriptors.length > BRIDGE_PROTOCOL_BOUNDS.catalogCommands) throw new BridgeProtocolError("protocol_validation_error", "command_catalog");
  const normalized = descriptors.map((descriptor) => normalizeEffectiveCommandDescriptor(descriptor));
  if (new Set(normalized.map((descriptor) => descriptor.id)).size !== normalized.length) throw new BridgeProtocolError("protocol_validation_error", "command_catalog");
  normalized.sort((left, right) => compareOrdinal(left.id, right.id));
  return hashRevision(normalized);
}

export function isBoundedJson(value: unknown, requireObject = false): boolean {
  if (requireObject && !isPlainObject(value)) return false;
  const stack: Array<{ value: unknown; depth: number }> = [{ value, depth: 0 }];
  let nodes = 0;
  let stringChars = 0;
  while (stack.length > 0) {
    const current = stack.pop();
    if (!current || ++nodes > BRIDGE_PROTOCOL_BOUNDS.jsonNodes || current.depth > BRIDGE_PROTOCOL_BOUNDS.jsonDepth) return false;
    const item = current.value;
    if (item === null || typeof item === "boolean") continue;
    if (typeof item === "string") {
      stringChars += item.length;
      if (item.length > BRIDGE_PROTOCOL_BOUNDS.jsonStringChars || stringChars > BRIDGE_PROTOCOL_BOUNDS.jsonTotalStringChars) return false;
      continue;
    }
    if (typeof item === "number") {
      if (!Number.isFinite(item)) return false;
      continue;
    }
    if (Array.isArray(item)) {
      if (item.length > BRIDGE_PROTOCOL_BOUNDS.jsonArrayItems) return false;
      for (let index = item.length - 1; index >= 0; index -= 1) stack.push({ value: item[index], depth: current.depth + 1 });
      continue;
    }
    if (!isPlainObject(item)) return false;
    const entries = Object.entries(item);
    if (entries.length > BRIDGE_PROTOCOL_BOUNDS.jsonObjectKeys) return false;
    for (let index = entries.length - 1; index >= 0; index -= 1) {
      const entry = entries[index];
      if (!entry || entry[0].length > BRIDGE_PROTOCOL_BOUNDS.jsonKeyChars) return false;
      stringChars += entry[0].length;
      if (stringChars > BRIDGE_PROTOCOL_BOUNDS.jsonTotalStringChars) return false;
      stack.push({ value: entry[1], depth: current.depth + 1 });
    }
  }
  return true;
}

function parseVersionedEnvelope<T>(value: unknown, envelope: BridgeProtocolEnvelope, schema: z.ZodType<T>): T {
  if (!isPlainObject(value) || !Object.hasOwn(value, "schema_version")) {
    throw new BridgeProtocolError("protocol_version_mismatch", envelope, { reason: "missing_schema_version" });
  }
  if (value.schema_version !== BRIDGE_PROTOCOL_VERSION) {
    throw new BridgeProtocolError("protocol_version_mismatch", envelope, {
      reason: "unsupported_schema_version",
      ...safeReceivedVersion(value.schema_version),
    });
  }
  const parsed = schema.safeParse(value);
  if (!parsed.success) throw validationError(envelope, parsed.error);
  return parsed.data;
}

function validationError(envelope: BridgeProtocolEnvelope, error: z.ZodError): BridgeProtocolError {
  return new BridgeProtocolError("protocol_validation_error", envelope, {
    reason: "schema_validation_failed",
    issues: error.issues.slice(0, BRIDGE_PROTOCOL_BOUNDS.protocolIssues).map((issue) => ({
      path: issue.path.filter((part): part is string | number => typeof part === "string" || typeof part === "number").slice(0, 16),
      code: issue.code,
    })),
  });
}

function safeReceivedVersion(value: unknown): Pick<BridgeProtocolDiagnostic, "received_version"> {
  if (value === null || typeof value === "number" || typeof value === "boolean") return { received_version: value };
  if (typeof value === "string") return { received_version: value.slice(0, BRIDGE_PROTOCOL_BOUNDS.receivedPrimitiveChars) };
  return {};
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) return false;
  const prototype = Object.getPrototypeOf(value) as unknown;
  return prototype === Object.prototype || prototype === null;
}

function normalizeSet<T extends string>(values: readonly T[] | undefined): T[] | null {
  return values === undefined ? null : [...new Set(values)].sort(compareOrdinal);
}

function compareOrdinal(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function hashRevision(value: unknown): string {
  return `sha256:${createHash("sha256").update(canonicalJsonStringify(value)).digest("hex")}`;
}
