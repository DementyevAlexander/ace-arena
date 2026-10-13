import fs from "node:fs";
import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import { KNOWN_UNITY_POPUP_RESPONSE_COMMAND_IDS } from "../approvals/known-unity-popups.js";
import { refreshKnownUnityPopupApproval } from "../approvals/known-unity-popup-settings.js";
import { closeUnityCrashReporters, inspectUnityCrashReporter } from "../diagnostics/unity-crash-reporter.js";
import { PROBLEM_REPORT_MAX_BYTES, PROBLEM_REPORT_MAX_REGISTRY_RECORDS, PROBLEM_REPORT_SCHEMA_VERSION, redactProblemReport, serializeProblemReport, summarizeProblemCrashReporter } from "../diagnostics/problem-report.js";
import { createUnityProjectOpenRuntime, type UnityProjectOpenArgs } from "../diagnostics/unity-project-open.js";
import { createUnityProcessRuntime, type UnityProcessListResult, type UnityProcessSnapshot, type UnityProcessTerminateArgs } from "../diagnostics/unity-processes.js";
import { externalSceneReloadEvidence, inspectUnityWindowDiagnostics, respondToUnityExternalSceneReload, respondToUnityRecoveryPrompt, type UnityExternalSceneReloadRespondArgs, type UnityProjectDowngradeContinueArgs, type UnityRecoveryPromptRespondArgs, type UnitySafeModeEnterArgs, type UnityScopedRegistryImportCloseArgs, type UnityWindowDiagnosticsStatus } from "../diagnostics/unity-window-diagnostics.js";
import type { AuditEvent } from "../observability/audit.js";
import { summarizeCommandStatistics } from "../observability/command-statistics.js";
import { CanonicalPathService } from "../paths/canonical-path.js";
import { policyManagerSnapshot } from "../policy/policy-manager.js";
import type { HeartbeatRecord, ProjectRegistrySnapshot, PublicProjectRecord } from "../registry/project-registry.js";
import type { DispatcherContext } from "./dispatcher.js";
import { buildSafeExecutableSchemaRecovery, isSchemaRecoveryErrorCode, safePublicProjectSelector, type ExecutableMcpAction, type SchemaRecoveryPlan } from "./schema-recovery.js";

const FEATURE_FLAG = "sgg_mcp_diagnostics";
const MAX_TAIL_LINES = 200;
const DEFAULT_TAIL_LINES = 50;
const MAX_TAIL_BYTES = 128 * 1024;
const DEFAULT_TAIL_BYTES = 32 * 1024;
const MAX_SUMMARY_EVENTS = 1000;
const MAX_SUMMARY_BYTES = 1024 * 1024;
const MAX_STATISTICS_EVENTS = 10_000;
const DEFAULT_STATISTICS_BYTES = 1024 * 1024;
const MAX_STATISTICS_BYTES = 4 * 1024 * 1024;
const REPORT_AUDIT_TAIL_LINES = 50;
const REPORT_AUDIT_TAIL_BYTES = 64 * 1024;
const MAX_PROBLEM_REPORTS = 100;
const DEFAULT_PROBLEM_REPORTS = 20;
const MAX_PROBLEM_REPORT_READ_CHARS = PROBLEM_REPORT_MAX_BYTES;
const DEFAULT_PROBLEM_REPORT_READ_CHARS = MAX_PROBLEM_REPORT_READ_CHARS;
const TRIAGE_DECISION_SCHEMA_VERSION = 1;
const MAX_PROBLEM_AUDIT_EVENTS = 200;
const MAX_PROBLEM_ARTIFACT_REFS = 20;
const MAX_TRIAGE_REPORT_IDS = 20;
const MAX_PROBLEM_UNITY_ERRORS = 10;
const BRIDGE_RELOAD_TRACE_LINES = 80;
const BRIDGE_RELOAD_TRACE_BYTES = 64 * 1024;
const UNITY_EDITOR_DISCOVERY_TIMEOUT_MS = 10000;
const UNITY_EDITOR_DISCOVERY_MAX_OUTPUT_CHARS = 256 * 1024;
const TRIAGE_DECISIONS_FILE = "problem-triage.json";
const BRIDGE_RELOAD_TRACE_RELATIVE_PATH = path.join("Library", "SGGUnityMcp", "bridge-reload-trace.log");
const BRIDGE_RELOAD_TRACE_ARTIFACT_PATH = "Library/SGGUnityMcp/bridge-reload-trace.log";
const UNITY_LIBRARY_LOCKS = ["Library/ArtifactDB-lock", "Library/SourceAssetDB-lock"] as const;
const UNITY_STARTUP_LOG_TAIL_LINES = 80;
const UNITY_STARTUP_LOG_TAIL_BYTES = 64 * 1024;
const UNITY_STARTUP_LOG_MARKERS = [
  { code: "library_redirect", patterns: ["Library Redirect Path"] },
  { code: "library_initializing", patterns: ["LibraryInitializing", "Initialize engine version"] },
  { code: "artifactdb", patterns: ["ArtifactDB", "Artifact Database"] },
  { code: "sourceassetdb", patterns: ["SourceAssetDB", "Source Asset DB"] },
  { code: "licensing", patterns: ["Licensing", "Unity.Licensing.Client", "Entitlement"] },
  { code: "package_manager", patterns: ["Package Manager", "UPM", "PackageCache"] },
  { code: "bug_reporter", patterns: ["Bug Reporter", "UnityBugReporter"] },
] as const;
const SCOPED_REGISTRY_PROCESS_SCAN_BUDGET_MS = 2_500;
const SCOPED_REGISTRY_WINDOW_SCAN_BUDGET_MS = 2_500;
const MAX_SCOPED_REGISTRY_CANDIDATES = 8;
const ARTIFACT_PRODUCER_PREFIXES = ["build.", "test.", "profiler.", "tmp.", "diagnostics."];
const UNITY_ERROR_COMMAND_PREFIXES = ["compile.", "logs.console.", "build.", "test."];
const canonicalPaths = new CanonicalPathService();

type ProblemLastError = {
  last_tool: string | null;
  last_command: string | null;
  last_error_code: string | null;
  last_error_message: string | null;
  last_error_details_json: string | null;
  last_error_details_parse_error: string | null;
  last_error_details: unknown;
  expected: string | null;
  actual: string | null;
  artifact_refs: string[];
  recovery_actions: RecoveryAction[];
  next_valid_actions: string[];
};

type RecoveryAction = {
  action: string;
  description: string;
  action_kind?: "mcp_tool";
  tool?: "sgg_unity_projects" | "sgg_unity_describe" | "sgg_unity_call";
  arguments?: Record<string, unknown>;
  selector_source?: "original_selector" | "resolved_project_id";
};

type DiagnosticsProjectResolutionContext =
  | { project_resolved: true; original_project_selector: string; resolved_project_id: string }
  | { project_resolved: false; original_project_selector?: string };

type SceneBackupRootFingerprint = `sha256:${string}` | "unavailable";

export type UnitySceneBackupBindingWitness = {
  schema_version: 1;
  stage: "input_validation" | "project_identity" | "process_scan" | "current_process" | "runtime_admission";
  input_validation: {
    canonical_root_exact: boolean | "unavailable";
    pid_positive: boolean;
    process_start_utc_parseable: boolean;
  };
  root_fingerprints: { input: SceneBackupRootFingerprint; normalized: SceneBackupRootFingerprint; selected: SceneBackupRootFingerprint };
  selected_root_equal: boolean | "unavailable";
  process_scan:
    | { authoritative: true; matching_editor_count: number }
    | { authoritative: false | "unavailable"; matching_editor_count: "unavailable" };
  pid_equal: boolean | "unavailable";
  process_start_utc_equal: boolean | "unavailable";
};

type ProblemRegistryEvidenceSelection = {
  record: PublicProjectRecord | undefined;
  reason: string;
  stale: boolean;
  diagnosticOnly: boolean;
};

type ProcessIdentityRecord = Pick<HeartbeatRecord, "instance_id" | "project" | "canonical_project_root" | "unity_pid" | "unity_process_start_utc" | "heartbeat_generation">;
type ProjectTraceRecord = Pick<HeartbeatRecord, "project_root" | "canonical_project_root" | "aliases" | "unity_pid" | "unity_process_start_utc" | "status">;

type TriageStatus = "active" | "deferred" | "closed";
type RegressionStatus = "unknown" | "test_added" | "eval_added" | "deferred_with_reason" | "not_needed";

type TriageDecision = {
  status: TriageStatus;
  root_cause?: string;
  decision?: string;
  regression_status?: RegressionStatus;
  defer_reason?: string;
  private_backlog_ref?: string;
  tags?: string[];
  updated_utc: string;
};

type ProblemReportRecord = {
  report_id: string;
  file_name: string;
  stat: fs.Stats;
  summary: Record<string, unknown>;
  parsed?: Record<string, unknown>;
  parse_error?: string;
};

export const DIAGNOSTICS_COMMANDS: CommandDescriptor[] = [
  {
    id: "diagnostics.report_problem",
    title: "Diagnostics problem report",
    description: "Write a bounded redacted Gateway-local problem report artifact for troubleshooting.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        summary: { type: "string", minLength: 1, maxLength: 500 },
        severity: { type: "string", enum: ["info", "warning", "error"] },
        category: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9_.:-]+$" },
        last_tool: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9_.:-]+$" },
        last_command: { type: "string", minLength: 1, maxLength: 120, pattern: "^[A-Za-z0-9_.:-]+$" },
        last_error_code: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9_.:-]+$" },
        last_error_message: { type: "string", maxLength: 2000 },
        last_error_details_json: { type: "string", maxLength: 4000 },
        last_artifact_refs: { type: "array", maxItems: MAX_PROBLEM_ARTIFACT_REFS, items: { type: "string", minLength: 1, maxLength: 512 } },
        expected: { type: "string", maxLength: 2000 },
        actual: { type: "string", maxLength: 2000 },
        include_snapshot: { type: "boolean" },
        include_audit_tail: { type: "boolean" },
        tags: { type: "array", maxItems: 12, items: { type: "string", minLength: 1, maxLength: 40, pattern: "^[A-Za-z0-9_.:-]+$" } },
      },
      required: ["summary"],
      additionalProperties: false,
    },
    tags: ["diagnostics"],
    capability: "diagnostics_report",
    result_shape: "object_details",
    output_limit: { max_bytes: 131072, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.status",
    title: "Diagnostics status",
    description: "Read bounded Gateway diagnostics status without secret values.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.problem.list",
    title: "Diagnostics problem report list",
    description: "List bounded Gateway-local problem report metadata without reading arbitrary files.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        max_results: { type: "integer", minimum: 1, maximum: MAX_PROBLEM_REPORTS },
        project: { type: "string", minLength: 1, maxLength: 120 },
        severity: { type: "string", enum: ["info", "warning", "error"] },
        triage_status: { type: "string", enum: ["active", "deferred", "closed", "untriaged"] },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "problem"],
    capability: "diagnostics_report",
    result_shape: "bounded_list",
    output_limit: { max_results: MAX_PROBLEM_REPORTS, max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.problem.read",
    title: "Diagnostics problem report read",
    description: "Read one bounded Gateway-local problem report by report_id from the fixed problem report directory.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        report_id: { type: "string", minLength: 1, maxLength: 120, pattern: "^problem_[A-Za-z0-9_-]+$" },
        max_chars: { type: "integer", minimum: 1024, maximum: MAX_PROBLEM_REPORT_READ_CHARS },
      },
      required: ["report_id"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "problem"],
    capability: "diagnostics_report",
    result_shape: "text_artifact",
    output_limit: { max_chars: MAX_PROBLEM_REPORT_READ_CHARS, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.problem.triage",
    title: "Diagnostics problem report triage",
    description: "Group bounded Gateway-local problem reports by command, error code, and root cause with fixed-dir triage decisions.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        max_reports: { type: "integer", minimum: 1, maximum: MAX_PROBLEM_REPORTS },
        project: { type: "string", minLength: 1, maxLength: 120 },
        severity: { type: "string", enum: ["info", "warning", "error"] },
        triage_status: { type: "string", enum: ["active", "deferred", "closed", "untriaged"] },
        include_closed: { type: "boolean" },
        min_count: { type: "integer", minimum: 1, maximum: MAX_PROBLEM_REPORTS },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "problem", "triage"],
    capability: "diagnostics_report",
    result_shape: "bounded_list",
    output_limit: { max_results: MAX_PROBLEM_REPORTS, max_bytes: 131072, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.problem.mark",
    title: "Diagnostics problem report mark",
    description: "Persist active/deferred/closed triage decisions for fixed-dir Gateway-local problem reports.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "idempotent",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        report_ids: { type: "array", minItems: 1, maxItems: MAX_TRIAGE_REPORT_IDS, items: { type: "string", minLength: 1, maxLength: 120, pattern: "^problem_[A-Za-z0-9_-]+$" } },
        status: { type: "string", enum: ["active", "deferred", "closed"] },
        root_cause: { type: "string", minLength: 1, maxLength: 160 },
        decision: { type: "string", minLength: 1, maxLength: 1000 },
        regression_status: { type: "string", enum: ["unknown", "test_added", "eval_added", "deferred_with_reason", "not_needed"] },
        defer_reason: { type: "string", minLength: 1, maxLength: 500 },
        private_backlog_ref: { type: "string", minLength: 1, maxLength: 120, pattern: "^[A-Za-z0-9_.:-]+$" },
        tags: { type: "array", maxItems: 12, items: { type: "string", minLength: 1, maxLength: 40, pattern: "^[A-Za-z0-9_.:-]+$" } },
      },
      required: ["report_ids", "status"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "problem", "triage"],
    capability: "diagnostics_report",
    result_shape: "object_details",
    output_limit: { max_results: MAX_TRIAGE_REPORT_IDS, max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.recovery.suggest",
    title: "Diagnostics recovery suggestion",
    description: "Suggest bounded next actions for common Gateway, bridge, policy, Unity busy, compile, and test status errors.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        error_code: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9_.:-]+$" },
        command: { type: "string", minLength: 1, maxLength: 120, pattern: "^[A-Za-z0-9_.:-]+$" },
        tool: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9_.:-]+$" },
        message: { type: "string", maxLength: 2000 },
        details_json: { type: "string", maxLength: 4000 },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_actions: 16, max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_crash_reporter.status",
    title: "Unity crash reporter status",
    description: "Detect active Unity Bug Reporter windows and recent Unity crash artifacts without reading large logs.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics", "unity", "crash"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.process.list",
    title: "Unity process list",
    description: "List bounded Unity-related processes with parsed project ownership and heartbeat correlation.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics", "unity", "process"],
    capability: "diagnostics_status",
    result_shape: "bounded_list",
    output_limit: { max_results: 50, max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.editor.installations.list",
    title: "Unity editor installations list",
    description: "Discover installed Unity Editor executables from Gateway-local CLI, Unity Hub, and configured environment roots without accepting arbitrary model-provided paths.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 12000,
    schema: {
      type: "object",
      properties: {
        version: { type: "string", minLength: 1, maxLength: 40, pattern: "^[A-Za-z0-9_.-]+$" },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "editor", "installations"],
    capability: "diagnostics_status",
    result_shape: "bounded_list",
    output_limit: { max_results: 50, max_bytes: 131072, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.project.open_status",
    title: "Unity project open status",
    description: "Check whether a requested Unity project is already open in an Editor and whether it has an MCP heartbeat before issuing project-scoped commands.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        project_selector: { type: "string", minLength: 1, maxLength: 512 },
        canonical_project_root: { type: "string", minLength: 1, maxLength: 512 },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "project", "editor"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_bytes: 131072, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.project.open",
    title: "Open Unity project",
    description: "Dry-run or ask-gated launch of one exact Unity project in a discovered Unity Editor installation without closing existing Editors or deleting project caches.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 12000,
    schema: {
      type: "object",
      properties: {
        project_selector: { type: "string", minLength: 1, maxLength: 512 },
        canonical_project_root: { type: "string", minLength: 1, maxLength: 512 },
        unity_version: { type: "string", minLength: 1, maxLength: 40, pattern: "^[A-Za-z0-9_.-]+$" },
        dry_run: { type: "boolean" },
        confirm_open: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "project", "editor", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 131072, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_startup.status",
    title: "Unity startup status",
    description: "Correlate Unity process snapshots, heartbeat records and native modal diagnostics for startup/lock recovery.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics", "unity", "process", "startup"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_bytes: 131072, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.process.terminate",
    title: "Terminate Unity process",
    description: "Dry-run or terminate one verified project-owned Unity Editor process by exact PID and canonical project root.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        pid: { type: "integer", minimum: 1 },
        canonical_project_root: { type: "string", minLength: 1, maxLength: 512 },
        confirm_terminate: { type: "boolean" },
        dry_run: { type: "boolean" },
        force: { type: "boolean" },
      },
      required: ["pid", "canonical_project_root", "confirm_terminate"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "process", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 131072, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_modal.status",
    title: "Unity modal status",
    description: "Detect Unity Editor and native Windows modal blocks, including startup recovery, Unity Licensing Client crash and Missing Signature package dialogs.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics", "unity", "modal"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_scoped_registry_import.status",
    title: "Scoped registry import modal status",
    description: "Run one fresh bounded process-wide scan for exact Unity Importing a scoped registry dialogs, including when the target project heartbeat is absent.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10_000,
    schema: {
      type: "object",
      properties: {
        pid: { type: "integer", minimum: 1 },
        process_start_utc: { type: "string", minLength: 20, maxLength: 40 },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "modal", "package", "recovery"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_results: MAX_SCOPED_REGISTRY_CANDIDATES, max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_scoped_registry_import.close",
    title: "Close exact scoped registry import modal",
    description: "Dry-run or submit one human-approved Close message to one exact short-lived scoped-registry modal binding without using the Unity Bridge. Learn More and generic UI automation are never supported.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 10_000,
    schema: {
      type: "object",
      properties: {
        pid: { type: "integer", minimum: 1 },
        process_start_utc: { type: "string", minLength: 20, maxLength: 40 },
        canonical_project_root: { type: "string", minLength: 1, maxLength: 512 },
        dry_run: { type: "boolean" },
        confirm_close: { type: "boolean", const: true },
        precondition_hash: { type: "string", pattern: "^sha256:[a-f0-9]{64}$" },
      },
      required: ["pid", "process_start_utc", "canonical_project_root"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "modal", "package", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_safe_mode.enter",
    title: "Enter exact Unity Safe Mode prompt",
    description: "Dry-run or submit one human-approved Enter Safe Mode message to one exact short-lived Safe Mode prompt binding. Ignore, Quit and generic UI interaction are never supported.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 10_000,
    schema: {
      type: "object",
      properties: {
        pid: { type: "integer", minimum: 1 },
        process_start_utc: { type: "string", minLength: 20, maxLength: 40 },
        canonical_project_root: { type: "string", minLength: 1, maxLength: 512 },
        dry_run: { type: "boolean" },
        confirm_enter_safe_mode: { type: "boolean", const: true },
        precondition_hash: { type: "string", pattern: "^sha256:[a-f0-9]{64}$" },
      },
      required: ["pid", "process_start_utc", "canonical_project_root"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "modal", "safe-mode", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_scene_backup.keep_backups",
    title: "Keep backups in exact Unity Scene Backup Detected prompt",
    description: "Dry-run-first, single-use Keep Backups response for one exact Scene Backup Detected dialog. Never selects Delete or the legacy Recovering Scene Backups Yes/No prompt. Submission does not prove backups were preserved or the Editor recovered.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 10000,
    schema: {
      type: "object",
      properties: {
        pid: { type: "integer", minimum: 1 },
        process_start_utc: { type: "string", minLength: 20, maxLength: 40 },
        canonical_project_root: { type: "string", minLength: 1, maxLength: 512 },
        dry_run: { type: "boolean", default: true },
        confirm_keep_backups: { type: "boolean", const: true },
        precondition_hash: { type: "string", pattern: "^sha256:[a-f0-9]{64}$" },
      },
      required: ["pid", "process_start_utc", "canonical_project_root"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "modal", "scene", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_recovery_prompt.status",
    title: "Unity recovery prompt status",
    description: "Inspect Unity Editor startup recovery prompt candidates and guarded package-signature prompt close actions without using the Unity bridge.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics", "unity", "modal", "recovery"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_recovery_prompt.respond",
    title: "Respond to Unity recovery prompt",
    description: "Dry-run or click a verified recovery prompt Don't Restore/Discard button, or the Close button on a verified Unity Missing Signature package prompt.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "idempotent",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["do_not_restore", "discard_recovery", "close_missing_signature"] },
        pids: { type: "array", maxItems: 5, items: { type: "integer", minimum: 1 } },
        dry_run: { type: "boolean" },
      },
      required: ["action"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "modal", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_external_scene_reload.respond",
    title: "Respond to external scene reload prompt",
    description: "Ask-gated one-shot Reload or Ignore response for one exact Unity external-scene prompt using a fresh server-issued, single-use evidence token. The result does not claim that the popup closed or compilation recovered.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        action: { type: "string", enum: ["reload", "ignore"] },
        pid: { type: "integer", minimum: 1 },
        hwnd: { type: "string", pattern: "^0x[0-9A-Fa-f]+$", maxLength: 32 },
        evidence_id: { type: "string", pattern: "^sha256:[a-f0-9]{64}$" },
      },
      required: ["action", "pid", "hwnd", "evidence_id"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "modal", "scene", "recovery"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_project_downgrade.continue",
    title: "Continue exact Unity project downgrade prompt",
    description: "Human-approved one-shot Continue message for one exact, fully evidenced Unity project downgrade prompt. Submission never proves popup closure or downgrade completion.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        evidence_id: { type: "string", pattern: "^sha256:[a-f0-9]{64}$" },
        canonical_project_root: { type: "string", minLength: 1, maxLength: 512 },
        confirm_continue: { type: "boolean", const: true },
        pid: { type: "integer", minimum: 1 },
        process_name: { type: "string", enum: ["Unity", "Unity.exe"] },
        process_start_utc: { type: "string", minLength: 20, maxLength: 40 },
        hwnd: { type: "string", pattern: "^0x[0-9A-Fa-f]+$", maxLength: 32 },
        window_thread_id: { type: "integer", minimum: 1 },
        class_name: { type: "string", enum: ["#32770", "UnityContainerWndClass"] },
        window_title: { type: "string", const: "Project Downgrade Required" },
        source_version: { type: "string", pattern: "^[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}(?:(?:a|b|f|p|rc)[0-9]{1,4})?$", maxLength: 32 },
        target_version: { type: "string", pattern: "^[0-9]{1,4}\\.[0-9]{1,4}\\.[0-9]{1,4}(?:(?:a|b|f|p|rc)[0-9]{1,4})?$", maxLength: 32 },
        body_sha256: { type: "string", pattern: "^sha256:[a-f0-9]{64}$" },
        button_count: { type: "integer", const: 2 },
        continue_button_hwnd: { type: "string", pattern: "^0x[0-9A-Fa-f]+$", maxLength: 32 },
        quit_button_hwnd: { type: "string", pattern: "^0x[0-9A-Fa-f]+$", maxLength: 32 },
      },
      required: ["evidence_id", "canonical_project_root", "confirm_continue", "pid", "process_name", "process_start_utc", "hwnd", "window_thread_id", "class_name", "window_title", "source_version", "target_version", "body_sha256", "button_count", "continue_button_hwnd", "quit_button_hwnd"],
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "modal", "project", "downgrade"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.unity_crash_reporter.close",
    title: "Close Unity crash reporter",
    description: "Close active Unity Bug Reporter windows without submitting crash reports.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "idempotent",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        pids: { type: "array", maxItems: 20, items: { type: "integer", minimum: 1 } },
        dry_run: { type: "boolean" },
        force: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "unity", "crash"],
    capability: "diagnostics_recovery",
    result_shape: "object_details",
    output_limit: { max_bytes: 65536, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.command_statistics",
    title: "Command invocation statistics",
    description: "Read bounded local per-command call, request/response byte, and redacted failure statistics from retained Gateway audit events.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        operation: { type: "string", pattern: "^[A-Za-z0-9_.:-]{1,256}$" },
        outcome: { type: "string", enum: ["ok", "error"] },
        response_mode: { type: "string", enum: ["full", "compact"] },
        cursor: { type: "integer", minimum: 0 },
        limit: { type: "integer", minimum: 1, maximum: 50 },
        recent_failures_limit: { type: "integer", minimum: 0, maximum: 50 },
        max_bytes: { type: "integer", minimum: 4096, maximum: MAX_STATISTICS_BYTES },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "statistics", "audit", "performance"],
    capability: "diagnostics_audit",
    result_shape: "object_details",
    output_limit: { max_events: MAX_STATISTICS_EVENTS, max_bytes: 128 * 1024, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.audit.summary",
    title: "Diagnostics audit summary",
    description: "Summarize bounded Gateway audit events without returning raw secrets.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics", "audit"],
    capability: "diagnostics_audit",
    result_shape: "object_details",
    output_limit: { max_events: MAX_SUMMARY_EVENTS, max_bytes: MAX_SUMMARY_BYTES, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "diagnostics.audit.tail",
    title: "Diagnostics audit tail",
    description: "Read a bounded redacted tail of the Gateway audit NDJSON file.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        lines: { type: "integer", minimum: 1, maximum: MAX_TAIL_LINES },
        max_bytes: { type: "integer", minimum: 1024, maximum: MAX_TAIL_BYTES },
      },
      additionalProperties: false,
    },
    tags: ["diagnostics", "audit"],
    capability: "diagnostics_audit",
    result_shape: "text_artifact",
    output_limit: { max_lines: MAX_TAIL_LINES, max_bytes: MAX_TAIL_BYTES, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "unity.relay.status",
    title: "Unity relay status",
    description: "Read diagnostic-only official Unity relay availability without launching it.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["diagnostics", "relay", "unity"],
    capability: "diagnostics_status",
    result_shape: "object_details",
    output_limit: { max_bytes: 32768, redacted: true },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export function findDiagnosticsCommand(commandId: string): CommandDescriptor | undefined {
  return DIAGNOSTICS_COMMANDS.find((command) => command.id === commandId);
}

export function runDiagnosticsCommand(commandId: string, args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, projectContext: DiagnosticsProjectResolutionContext): unknown {
  const reportedProject = projectContext.original_project_selector ?? (projectContext.project_resolved ? projectContext.resolved_project_id : undefined);
  switch (commandId) {
    case "diagnostics.report_problem":
      return diagnosticsReportProblem(args, context, record, reportedProject, projectContext);
    case "diagnostics.status":
      return diagnosticsStatus(context, record);
    case "diagnostics.problem.list":
      return diagnosticsProblemList(args, context);
    case "diagnostics.problem.read":
      return diagnosticsProblemRead(args, context);
    case "diagnostics.problem.triage":
      return diagnosticsProblemTriage(args, context);
    case "diagnostics.problem.mark":
      return diagnosticsProblemMark(args, context);
    case "diagnostics.recovery.suggest":
      return diagnosticsRecoverySuggest(args, projectContext);
    case "diagnostics.unity_crash_reporter.status":
      return unityCrashReporterStatus(context);
    case "unity.process.list":
      return unityProcessList(context, record);
    case "unity.editor.installations.list":
      return unityEditorInstallationsList(args, context);
    case "unity.project.open_status":
      return unityProjectOpenStatus(args, context, record, reportedProject);
    case "unity.project.open":
      return unityProjectOpen(args, context, record, reportedProject);
    case "diagnostics.unity_startup.status":
      return unityStartupStatus(context, record);
    case "unity.process.terminate":
      return unityProcessTerminate(args, context, record);
    case "diagnostics.unity_modal.status":
      return unityModalStatus(context, record, projectContext);
    case "diagnostics.unity_scoped_registry_import.status":
      return unityScopedRegistryImportStatus(args, context, record);
    case "diagnostics.unity_scoped_registry_import.close":
      return unityScopedRegistryImportClose(args, context, record, projectContext);
    case "diagnostics.unity_safe_mode.enter":
      return unitySafeModeEnter(args, context, record, projectContext);
    case "diagnostics.unity_scene_backup.keep_backups":
      return unitySceneBackupKeep(args, context, record, projectContext);
    case "diagnostics.unity_recovery_prompt.status":
      return unityRecoveryPromptStatus(context);
    case "diagnostics.unity_recovery_prompt.respond":
      return unityRecoveryPromptRespond(args, context);
    case "diagnostics.unity_external_scene_reload.respond":
      return unityExternalSceneReloadRespond(args, context, record);
    case "diagnostics.unity_project_downgrade.continue":
      return unityProjectDowngradeContinue(args, context, record, projectContext);
    case "diagnostics.unity_crash_reporter.close":
      return unityCrashReporterClose(args, context);
    case "diagnostics.command_statistics":
      return diagnosticsCommandStatistics(args, context);
    case "diagnostics.audit.summary":
      return diagnosticsAuditSummary(context);
    case "diagnostics.audit.tail":
      return diagnosticsAuditTail(args, context);
    case "unity.relay.status":
      return unityRelayStatus(context);
    default:
      throw new Error("command_not_found");
  }
}

function diagnosticsReportProblem(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, reportedProject: string | undefined, projectContext: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  const now = context.now?.() ?? new Date();
  const reportDir = problemReportDir(context);
  fs.mkdirSync(reportDir, { recursive: true });
  const summary = String(args.summary);
  const reportId = safeReportId(now, reportedProject ?? "project", summary);
  const filename = `${reportId}.json`;
  const reportPath = path.join(reportDir, filename);
  const artifactRef = `gateway://problem-reports/${filename}`;
  const snapshotWritten = args.include_snapshot !== false;
  const lastError = problemLastError(args, projectContext);
  const nextValidActions = problemReportNextValidActions(lastError);
  const serialized = serializeProblemReport({
    schema_version: PROBLEM_REPORT_SCHEMA_VERSION,
    report_id: reportId,
    created_utc: now.toISOString(),
    reported_project_selector: reportedProject ?? null,
    project_resolution_context: publicDiagnosticsProjectContext(projectContext),
    summary,
    severity: typeof args.severity === "string" ? args.severity : "error",
    category: typeof args.category === "string" ? args.category : "gateway",
    tags: Array.isArray(args.tags) ? args.tags : [],
    reported_last_error: lastError,
    snapshot: snapshotWritten ? problemSnapshot(context, record, reportedProject, projectContext, args.include_audit_tail === true, lastError, now) : null,
    redaction_applied: true,
    path_redaction_applied: true,
    next_valid_actions: nextValidActions,
  });
  fs.writeFileSync(reportPath, serialized.text, "utf8");

  return {
    report_id: reportId,
    artifact_refs: [artifactRef],
    report_path: "[REDACTED_PATH]",
    report_bytes: serialized.bytes,
    report_truncated: serialized.truncated,
    summary: redactProblemReport(summary),
    redaction_applied: true,
    snapshot_written: snapshotWritten,
    next_valid_actions: nextValidActions,
  };
}

function problemSnapshot(context: DispatcherContext, record: HeartbeatRecord | undefined, reportedProject: string | undefined, projectContext: DiagnosticsProjectResolutionContext, includeAuditTail: boolean, lastError: ProblemLastError, now: Date): Record<string, unknown> {
  const auditPath = context.auditFilePath ?? auditPathFromDir(context.auditDir);
  const registrySnapshot = context.registry?.captureSnapshot({ cleanup: false });
  const resolution = reportedProject ? registrySnapshot?.resolveProject(reportedProject) : undefined;
  const evidenceSelection = selectProblemRegistryEvidence(registrySnapshot, resolution, reportedProject);
  const registryRecords = problemRegistryRecords(registrySnapshot, resolution, evidenceSelection);
  const selectedRecord = resolution?.status === "ok" ? resolution.record : undefined;
  const auditEvents = recentAuditEvents(auditPath, MAX_PROBLEM_AUDIT_EVENTS);
  const lastAuditEvent = lastRelevantAuditEvent(auditEvents, reportedProject);
  const artifactRefs = recentArtifactRefs(auditEvents, lastError);
  const unityErrors = recentUnityErrorEvidence(auditEvents, lastError);
  const unityProcesses = unityProcessList(context, selectedRecord, registrySnapshot);
  const unityModal = unityModalStatus(context);
  const libraryLockDiagnostics = unityLibraryLockDiagnostics(context, selectedRecord, unityProcesses, registrySnapshot, registryRecords.records);
  const startupLogDiagnostics = problemStartupLogDiagnostics(unityStartupLogDiagnostics(selectedRecord));
  const lifecycleNow = registrySnapshot ? new Date(registrySnapshot.captured_at) : now;
  const lifecycle = lifecycleSnapshot(resolution, selectedRecord, lifecycleNow, replacementClassification(record, selectedRecord, registrySnapshot, reportedProject));
  const trace = evidenceSelection.record
    ? bridgeReloadTraceTail(evidenceSelection.record, evidenceSelection.stale, evidenceSelection.reason)
    : noProblemTraceEvidence(evidenceSelection.reason);
  return {
    schema_version: PROBLEM_REPORT_SCHEMA_VERSION,
    reported_project_selector: reportedProject ?? null,
    project_resolution_context: publicDiagnosticsProjectContext(projectContext),
    registry: {
      snapshot: registrySnapshot ? {
        captured_at: registrySnapshot.captured_at,
        capture_started_at: registrySnapshot.capture_started_at,
        capture_ended_at: registrySnapshot.capture_ended_at,
        selection_reason: evidenceSelection.reason,
      } : null,
      stats: registrySnapshot?.getStats() ?? null,
      projects: registryRecords.records,
      projects_summary: registryRecords.summary,
    },
    project_resolution: sanitizeProjectResolution(resolution, selectedRecord),
    resolved_heartbeat: selectedRecord ? publicHeartbeatRecord(selectedRecord) : null,
    lifecycle,
    evidence_selection: {
      reason: evidenceSelection.reason,
      stale: evidenceSelection.stale,
      diagnostic_only: evidenceSelection.diagnosticOnly,
      command_routing_eligible: false,
      ...(evidenceSelection.record ? {
        instance_id: evidenceSelection.record.instance_id,
        ...(evidenceSelection.record.heartbeat_generation !== undefined ? { heartbeat_generation: evidenceSelection.record.heartbeat_generation } : {}),
        ...(evidenceSelection.stale ? {
          identity_complete: hasCompleteProjectIdentity(evidenceSelection.record),
          unity_pid: evidenceSelection.record.unity_pid,
          unity_process_start_utc: evidenceSelection.record.unity_process_start_utc ?? null,
        } : {}),
      } : {}),
    },
    gateway: {
      gateway_runtime_version: context.gatewayRuntimeVersion ?? null,
      run_id: context.runId ?? "default",
      configPath: context.configPath ?? null,
      instancesDir: context.instancesDir ?? null,
      testRunOperationStore: context.testRunOperationPublisher?.snapshotStatus() ?? context.testRunOperationStore?.snapshotStatus() ?? null,
      auditPath,
      features: context.features ?? {},
      authentication: {
        mcp: context.mcpTokenPresent === true ? "present" : "missing",
        bridge: selectedRecord && registrySnapshot?.hasBridgeToken(selectedRecord.instance_id) ? "present" : "missing",
      },
    },
    policy_status: policyManagerSnapshot(context.policyManager, context.policy !== undefined),
    package_version: selectedRecord?.package_version ?? stringOrNull(lastAuditEvent?.package_version),
    commands_hash: selectedRecord?.commands_hash ?? stringOrNull(lastAuditEvent?.commands_hash),
    current_policy_decision: currentPolicyDecision(context),
    last_audit_event: lastAuditEvent ? auditEventSummary(lastAuditEvent) : null,
    last_policy_decision: auditPolicyDecision(lastAuditEvent),
    recent_artifact_refs: artifactRefs,
    recent_unity_errors: unityErrors,
    bridge_reload_trace_tail: trace,
    unity_crash_reporter: summarizeProblemCrashReporter(unityCrashReporterStatus(context)),
    unity_processes: unityProcesses,
    unity_modal: unityModal,
    library_lock_diagnostics: libraryLockDiagnostics,
    startup_log_diagnostics: startupLogDiagnostics,
    recent_audit_errors: auditErrorSummaries(auditEvents, 10),
    audit_tail: includeAuditTail ? diagnosticsAuditTail({ lines: REPORT_AUDIT_TAIL_LINES, max_bytes: REPORT_AUDIT_TAIL_BYTES }, context) : undefined,
  };
}

function problemRegistryRecords(snapshot: ProjectRegistrySnapshot | undefined, resolution: ReturnType<ProjectRegistrySnapshot["resolveProject"]> | undefined, evidenceSelection: ProblemRegistryEvidenceSelection): { records: PublicProjectRecord[]; summary: Record<string, unknown> } {
  const all = snapshot?.listProjectRecords() ?? [];
  const candidates = resolution?.status === "ok"
    ? [resolution.record]
    : resolution?.status === "error" && resolution.matches.length > 0
      ? resolution.matches
      : evidenceSelection.record
        ? [evidenceSelection.record]
        : [];
  const records = [...new Map(candidates.map((record) => [record.instance_id, record])).values()].slice(0, PROBLEM_REPORT_MAX_REGISTRY_RECORDS);
  return {
    records,
    summary: {
      total_records: all.length,
      candidate_records: candidates.length,
      retained_records: records.length,
      max_records: PROBLEM_REPORT_MAX_REGISTRY_RECORDS,
      truncated: candidates.length > records.length,
      selection: resolution?.status === "ok" ? "resolved_record" : resolution?.status === "error" && resolution.matches.length > 0 ? "resolution_matches" : evidenceSelection.record ? "diagnostic_evidence" : "no_selector_match",
    },
  };
}

function sanitizeProjectResolution(resolution: ReturnType<NonNullable<DispatcherContext["registry"]>["resolveProject"]> | undefined, record: HeartbeatRecord | undefined): Record<string, unknown> {
  if (resolution?.status === "ok") {
    return { status: "ok", record: publicHeartbeatRecord(resolution.record) };
  }
  if (resolution?.status === "error") {
    const matches = resolution.matches.slice(0, PROBLEM_REPORT_MAX_REGISTRY_RECORDS);
    return {
      status: "error",
      code: resolution.code,
      ...(resolution.code === "unsafe_binding_refused" ? { registry_operational_state: resolution.registryOperationalState } : {}),
      matches,
      matches_summary: {
        total_records: resolution.matches.length,
        retained_records: matches.length,
        max_records: PROBLEM_REPORT_MAX_REGISTRY_RECORDS,
        truncated: resolution.matches.length > matches.length,
      },
    };
  }
  return record ? { status: "ok", record: publicHeartbeatRecord(record) } : { status: "error", code: "project_not_found", matches: [] };
}

function publicHeartbeatRecord(record: HeartbeatRecord | PublicProjectRecord): Record<string, unknown> {
  if ("stale" in record) {
    return structuredClone(record);
  }
  const { token: _token, ...publicRecord } = record;
  return publicRecord;
}

function publicProjectRecordOrUndefined(record: HeartbeatRecord | PublicProjectRecord | undefined): PublicProjectRecord | undefined {
  if (!record) {
    return undefined;
  }

  if ("stale" in record) {
    return structuredClone(record);
  }
  const { token: _token, ...publicRecord } = record;
  return { ...publicRecord, stale: false };
}

function selectProblemRegistryEvidence(snapshot: ProjectRegistrySnapshot | undefined, resolution: ReturnType<ProjectRegistrySnapshot["resolveProject"]> | undefined, selector: string | undefined): ProblemRegistryEvidenceSelection {
  if (resolution?.status === "ok") {
    return { record: resolution.record, reason: "live_selector_match", stale: false, diagnosticOnly: false };
  }
  if (!snapshot || !selector) {
    return { record: undefined, reason: "no_live_match", stale: false, diagnosticOnly: false };
  }
  if (resolution?.status === "error" && resolution.code === "duplicate_project_id") {
    return { record: undefined, reason: "ambiguous_live_selector", stale: false, diagnosticOnly: false };
  }

  const publicRecords = new Map(snapshot.listProjectRecords().map((record) => [record.instance_id, record]));
  const staleMatches = snapshot.records.filter((record) => publicRecords.get(record.instance_id)?.stale === true && exactProjectSelectorOrRootMatch(record, selector));
  if (staleMatches.length !== 1) {
    return {
      record: undefined,
      reason: staleMatches.length > 1 ? "ambiguous_exact_stale_identity" : "no_live_match",
      stale: staleMatches.length > 0,
      diagnosticOnly: staleMatches.length > 0,
    };
  }

  const staleRecord = staleMatches[0];
  if (!staleRecord || !hasCompleteProjectIdentity(staleRecord)) {
    return { record: undefined, reason: "incomplete_exact_stale_identity", stale: true, diagnosticOnly: true };
  }
  return { record: structuredClone(staleRecord), reason: "unique_exact_stale_identity", stale: true, diagnosticOnly: true };
}

function exactProjectSelectorOrRootMatch(record: PublicProjectRecord, selector: string): boolean {
  if (record.project === selector || record.display_name === selector || record.instance_id === selector || record.project_root === selector) return true;
  const canonicalSelector = canonicalProjectSelectorForDiagnostics(selector);
  return (canonicalSelector !== undefined && (record.canonical_project_root === canonicalSelector || record.aliases.some((alias) => alias.canonical_path === canonicalSelector)))
    || record.aliases.some((alias) => alias.host_path === selector);
}

function canonicalProjectSelectorForDiagnostics(selector: string): string | undefined {
  try {
    return canonicalPaths.normalizeProjectRoot(selector);
  } catch {
    return undefined;
  }
}

function hasCompleteProjectIdentity(record: ProcessIdentityRecord): record is ProcessIdentityRecord & { unity_process_start_utc: string; heartbeat_generation: number } {
  return Number.isInteger(record.unity_pid)
    && record.unity_pid > 0
    && typeof record.instance_id === "string"
    && record.instance_id.length > 0
    && typeof record.unity_process_start_utc === "string"
    && record.unity_process_start_utc.length > 0
    && record.unity_process_start_utc.length <= 40
    && canonicalProcessStartUtc(record.unity_process_start_utc) !== undefined
    && typeof record.heartbeat_generation === "number"
    && Number.isInteger(record.heartbeat_generation)
    && record.heartbeat_generation > 0;
}

function replacementClassification(referenceRecord: HeartbeatRecord | undefined, selectedRecord: PublicProjectRecord | undefined, snapshot: ProjectRegistrySnapshot | undefined, selector: string | undefined): Record<string, unknown> | undefined {
  if (!snapshot) return undefined;
  const recentReplacement = selector ? snapshot.findRecentReplacement(selector) : undefined;
  const replacement = recentReplacement?.replacement_record;
  if (!replacement) return undefined;
  if (referenceRecord && referenceRecord.instance_id !== recentReplacement.failed_record.instance_id) return undefined;
  if (selectedRecord && selectedRecord.instance_id !== replacement.instance_id) return undefined;
  const reference: ProcessIdentityRecord = referenceRecord ?? recentReplacement.failed_record;

  const sameProcess = hasCompleteProjectIdentity(reference)
    && hasCompleteProjectIdentity(replacement)
    && exactSameProcessIdentity(reference, replacement);
  const processIdentitiesComplete = hasCompleteProjectIdentity(reference) && hasCompleteProjectIdentity(replacement);
  const classification = sameProcess
    ? "same_process_replacement"
    : processIdentitiesComplete
      ? "different_process_replacement"
      : "replacement_identity_incomplete";
  return {
    replacement_observed: true,
    replacement_classification: classification,
    same_process_replacement: sameProcess,
    process_identity_complete: processIdentitiesComplete,
    automatic_command_replay_attempted: false,
    automatic_process_restart_attempted: false,
    command_routing_eligible: false,
  };
}

function exactSameProcessIdentity(left: ProcessIdentityRecord, right: ProcessIdentityRecord): boolean {
  const leftProcessStart = canonicalProcessStartUtc(left.unity_process_start_utc);
  const rightProcessStart = canonicalProcessStartUtc(right.unity_process_start_utc);
  return left.unity_pid === right.unity_pid
    && leftProcessStart !== undefined
    && leftProcessStart === rightProcessStart;
}

function canonicalProcessStartUtc(value: string | undefined): string | undefined {
  if (!value || value.length > 40) return undefined;
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?Z$/.exec(value);
  if (!match || !Number.isFinite(Date.parse(value))) return undefined;
  return `${match[1]}.${(match[2] ?? "").padEnd(9, "0")}Z`;
}

function problemLastError(args: Record<string, unknown>, projectContext: DiagnosticsProjectResolutionContext): ProblemLastError {
  const lastCommand = stringOrNull(args.last_command);
  const lastErrorCode = stringOrNull(args.last_error_code);
  const detailsJson = stringOrNull(args.last_error_details_json);
  const schemaError = isSchemaRecoveryErrorCode(lastErrorCode ?? "");
  const parsedDetails = schemaError ? { value: null, parseError: null } : parseDetailsJson(detailsJson ?? "");
  const recovery = recoveryActions(lastErrorCode ?? "", lastCommand ?? "", { value: parsedDetails.value }, lastCommand ? commandReplacement(lastCommand) : undefined, trustedOriginalProjectSelector(projectContext), trustedResolvedProjectId(projectContext));
  const schemaRecovery = schemaError ? diagnosticsSchemaRecovery(lastCommand ?? "", projectContext) : undefined;
  const artifactRefs = boundedArtifactRefs([
    ...(!schemaError ? stringArray(args.last_artifact_refs) : []),
    ...(!schemaError ? artifactRefsFromUnknown(parsedDetails.value) : []),
  ]);

  return {
    last_tool: schemaError ? null : stringOrNull(args.last_tool),
    last_command: schemaError ? schemaRecovery?.safeCommand ?? null : lastCommand,
    last_error_code: lastErrorCode,
    last_error_message: schemaError ? null : stringOrNull(args.last_error_message),
    last_error_details_json: schemaError ? null : detailsJson,
    last_error_details_parse_error: schemaError ? null : parsedDetails.parseError,
    last_error_details: schemaError ? null : parsedDetails.value,
    expected: schemaError ? null : stringOrNull(args.expected),
    actual: schemaError ? null : stringOrNull(args.actual),
    artifact_refs: artifactRefs,
    recovery_actions: recovery,
    next_valid_actions: recovery.map((action) => action.action),
  };
}

function problemReportNextValidActions(lastError: ProblemLastError): string[] {
  return uniqueStrings([
    "diagnostics.problem.read",
    ...lastError.next_valid_actions,
    "diagnostics.status",
    "diagnostics.recovery.suggest",
    "sgg_unity_projects",
  ]).slice(0, 16);
}

function lifecycleSnapshot(resolution: ReturnType<NonNullable<DispatcherContext["registry"]>["resolveProject"]> | undefined, record: HeartbeatRecord | undefined, now: Date, replacement?: Record<string, unknown>): Record<string, unknown> {
  if (resolution?.status === "ok") {
    return { ...lifecycleFromRecord(resolution.record, false, now), ...(replacement ? { replacement } : {}) };
  }
  if (resolution?.status === "error") {
    return {
      resolution_status: "error",
      code: resolution.code,
      ...(resolution.code === "unsafe_binding_refused" ? { registry_operational_state: resolution.registryOperationalState } : {}),
      candidate_count: resolution.matches.length,
      candidates: resolution.matches.slice(0, 10).map((candidate) => lifecycleFromPublicRecord(candidate, now)),
      ...(replacement ? { replacement } : {}),
    };
  }
  return record
    ? { ...lifecycleFromRecord(record, false, now), ...(replacement ? { replacement } : {}) }
    : { resolution_status: "error", code: "project_not_found", candidate_count: 0, candidates: [], ...(replacement ? { replacement } : {}) };
}

function lifecycleFromRecord(record: HeartbeatRecord, stale: boolean, now: Date): Record<string, unknown> {
  return {
    resolution_status: "ok",
    project: record.project,
    instance_id: record.instance_id,
    status: record.status,
    stale,
    last_heartbeat: record.last_heartbeat,
    heartbeat_age_ms: heartbeatAgeMs(record.last_heartbeat, now),
    has_unsaved_scene_changes: record.has_unsaved_scene_changes ?? null,
    dirty_scene_count: record.dirty_scene_count ?? null,
  };
}

function lifecycleFromPublicRecord(record: PublicProjectRecord, now: Date): Record<string, unknown> {
  return {
    project: record.project,
    instance_id: record.instance_id,
    status: record.status,
    stale: record.stale,
    last_heartbeat: record.last_heartbeat,
    heartbeat_age_ms: heartbeatAgeMs(record.last_heartbeat, now),
    has_unsaved_scene_changes: record.has_unsaved_scene_changes ?? null,
    dirty_scene_count: record.dirty_scene_count ?? null,
  };
}

function heartbeatAgeMs(lastHeartbeat: string, now: Date): number | null {
  const timestamp = Date.parse(lastHeartbeat);
  return Number.isFinite(timestamp) ? Math.max(0, now.getTime() - timestamp) : null;
}

function currentPolicyDecision(context: DispatcherContext): Record<string, unknown> | null {
  const runtimeContext = context as DispatcherContext & { auditPolicyMetadata?: Record<string, unknown> };
  return runtimeContext.auditPolicyMetadata ? policyDecisionSummary(runtimeContext.auditPolicyMetadata) : null;
}

function policyDecisionSummary(policy: Record<string, unknown>): Record<string, unknown> {
  return {
    action: stringOrNull(policy.action),
    reason: stringOrNull(policy.reason),
    pattern: stringOrNull(policy.pattern),
    approval_status: stringOrNull(policy.approval_status),
    auto_allow_ask: policy.auto_allow_ask === true,
    auto_allow_known_unity_popups: policy.auto_allow_known_unity_popups === true,
    auto_allow_stale_registry_cleanup_without_elicitation: policy.auto_allow_stale_registry_cleanup_without_elicitation === true,
  };
}

function diagnosticsStatus(context: DispatcherContext, record: HeartbeatRecord | undefined): Record<string, unknown> {
  const auditPath = context.auditFilePath ?? auditPathFromDir(context.auditDir);
  const auditStat = safeStat(auditPath);
  return redactDiagnostics({
    runtime: {
      run_id: context.runId ?? "default",
      config_path: context.configPath ?? null,
      instances_dir: context.instancesDir ?? null,
      test_run_operation_store: context.testRunOperationPublisher?.snapshotStatus() ?? context.testRunOperationStore?.snapshotStatus() ?? null,
      audit_path: auditPath,
      asset_index_cache_root: context.assetIndexCacheRoot ?? null,
    },
    registry: context.registry?.getStats({ cleanup: false }) ?? null,
    policy: policyManagerSnapshot(context.policyManager, context.policy !== undefined),
    features: context.features ?? {},
    popup_response_approval: knownUnityPopupApprovalStatus(context),
    audit: {
      enabled: context.auditEnabled ?? context.audit !== undefined,
      path: auditPath,
      exists: auditStat !== undefined,
      bytes: auditStat?.size ?? 0,
      last_write_utc: auditStat?.mtime.toISOString() ?? null,
    },
    authentication: {
      bridge: record?.token ? "present" : "missing",
      mcp: context.mcpTokenPresent === true ? "present" : "missing",
    },
    safeguards: {
      canonical_path_dependency: "unity_package_dependency",
      path_guard: "unity_bridge_enforced_for_path_commands",
    },
    unity_crash_reporter: unityCrashReporterStatus(context),
    unity_processes: unityProcessList(context, record),
    unity_modal: unityModalStatus(context),
    last_errors: lastAuditErrors(auditPath, 5),
    redaction_applied: true,
  });
}

function diagnosticsProblemList(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const reportDir = problemReportDir(context);
  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_PROBLEM_REPORTS, 1, MAX_PROBLEM_REPORTS);
  const projectFilter = typeof args.project === "string" && args.project.length > 0 ? args.project : undefined;
  const severityFilter = typeof args.severity === "string" && args.severity.length > 0 ? args.severity : undefined;
  const triageStatusFilter = typeof args.triage_status === "string" && args.triage_status.length > 0 ? args.triage_status : undefined;
  const decisions = readTriageDecisions(reportDir);
  const reports = readProblemReportEntries(reportDir)
    .map((entry) => problemReportSummary(reportDir, entry, decisions))
    .filter((entry) => projectFilter === undefined || entry.project === projectFilter)
    .filter((entry) => severityFilter === undefined || entry.severity === severityFilter)
    .filter((entry) => triageStatusFilter === undefined || entry.triage_status === triageStatusFilter)
    .slice(0, maxResults);

  return redactProblemReport({
    problem_reports_dir: reportDir,
    exists: fs.existsSync(reportDir),
    max_results: maxResults,
    count: reports.length,
    reports,
    filters: {
      project: projectFilter ?? null,
      severity: severityFilter ?? null,
      triage_status: triageStatusFilter ?? null,
    },
    decisions_count: Object.keys(decisions).length,
    redaction_applied: true,
    next_valid_actions: ["diagnostics.problem.read", "diagnostics.problem.triage", "diagnostics.problem.mark", "diagnostics.report_problem", "diagnostics.recovery.suggest"],
  });
}

function diagnosticsProblemRead(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const reportId = typeof args.report_id === "string" ? args.report_id : "";
  const maxChars = clampInteger(typeof args.max_chars === "number" ? args.max_chars : DEFAULT_PROBLEM_REPORT_READ_CHARS, 1024, MAX_PROBLEM_REPORT_READ_CHARS);
  if (!/^problem_[A-Za-z0-9_-]+$/.test(reportId)) {
    return {
      report_id: reportId,
      exists: false,
      valid_report_id: false,
      report: null,
      redaction_applied: true,
      next_valid_actions: ["diagnostics.problem.list", "diagnostics.report_problem"],
    };
  }
  const reportPath = path.join(problemReportDir(context), `${reportId}.json`);
  const stat = safeStat(reportPath);
  if (!stat || !stat.isFile()) {
    return {
      report_id: reportId,
      exists: false,
      report: null,
      artifact_ref: `gateway://problem-reports/${reportId}.json`,
      redaction_applied: true,
      next_valid_actions: ["diagnostics.problem.list", "diagnostics.report_problem"],
    };
  }

  const oversized = stat.size > PROBLEM_REPORT_MAX_BYTES;
  const bounded = readBoundedFileStart(reportPath, maxChars);
  const text = bounded.text;
  let report: unknown = null;
  let parse_error: string | null = null;
  let reportSummaryOnly = false;
  if (bounded.truncated && !oversized) {
    const entry = { fileName: `${reportId}.json`, reportId, stat };
    const parsed = parseProblemReport(path.dirname(reportPath), entry.fileName, stat);
    if (parsed.parsed) {
      report = problemReportSummaryFromParsed(entry, parsed.parsed, undefined, readTriageDecisions(path.dirname(reportPath))[reportId]);
      reportSummaryOnly = true;
    } else {
      parse_error = parsed.parseError ?? "parse_failed";
    }
  } else {
    try {
      report = JSON.parse(text);
    } catch (error) {
      parse_error = error instanceof Error ? error.message : "parse_failed";
    }
  }

  return redactProblemReport({
    report_id: reportId,
    artifact_ref: `gateway://problem-reports/${reportId}.json`,
    report_path: reportPath,
    exists: true,
    bytes: stat.size,
    oversized,
    max_chars: maxChars,
    bytes_read: bounded.bytesRead,
    truncated: bounded.truncated,
    report,
    ...(reportSummaryOnly ? { report_projection: "summary", report_summary_only: true } : {}),
    ...(parse_error ? { parse_error, text_omitted: true } : {}),
    redaction_applied: true,
    next_valid_actions: ["diagnostics.recovery.suggest", "diagnostics.problem.list", "call_sgg_unity_projects"],
  });
}

function diagnosticsProblemTriage(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const reportDir = problemReportDir(context);
  const maxReports = clampInteger(typeof args.max_reports === "number" ? args.max_reports : MAX_PROBLEM_REPORTS, 1, MAX_PROBLEM_REPORTS);
  const minCount = clampInteger(typeof args.min_count === "number" ? args.min_count : 1, 1, MAX_PROBLEM_REPORTS);
  const projectFilter = typeof args.project === "string" && args.project.length > 0 ? args.project : undefined;
  const severityFilter = typeof args.severity === "string" && args.severity.length > 0 ? args.severity : undefined;
  const triageStatusFilter = typeof args.triage_status === "string" && args.triage_status.length > 0 ? args.triage_status : undefined;
  const includeClosed = args.include_closed === true || triageStatusFilter === "closed";
  const decisions = readTriageDecisions(reportDir);
  const records = readProblemReports(reportDir, decisions)
    .filter((record) => projectFilter === undefined || record.summary.project === projectFilter)
    .filter((record) => severityFilter === undefined || record.summary.severity === severityFilter)
    .filter((record) => triageStatusFilter === undefined || record.summary.triage_status === triageStatusFilter)
    .filter((record) => includeClosed || record.summary.triage_status !== "closed")
    .slice(0, maxReports);
  const groups = triageGroups(records, decisions)
    .filter((group) => group.count >= minCount)
    .sort((left, right) => right.active_count - left.active_count || right.count - left.count || String(right.latest_created_utc).localeCompare(String(left.latest_created_utc)))
    .slice(0, MAX_PROBLEM_REPORTS);

  return redactProblemReport({
    problem_reports_dir: reportDir,
    decision_path: triageDecisionPath(reportDir),
    exists: fs.existsSync(reportDir),
    max_reports: maxReports,
    reports_considered: records.length,
    decisions_count: Object.keys(decisions).length,
    untriaged_count: records.filter((record) => record.summary.triage_status === "untriaged").length,
    active_group_count: groups.filter((group) => group.active_count > 0).length,
    groups,
    filters: {
      project: projectFilter ?? null,
      severity: severityFilter ?? null,
      triage_status: triageStatusFilter ?? null,
      include_closed: includeClosed,
      min_count: minCount,
    },
    triage_cadence: [
      "Review active groups first, grouped by command/error_code/root_cause.",
      "Mark recurring issues closed/deferred/active with diagnostics.problem.mark in the fixed report directory.",
      "For recurring issues, attach a private backlog decision outside public docs and add a regression test/eval or explicit defer reason.",
    ],
    redaction_applied: true,
    next_valid_actions: ["diagnostics.problem.read", "diagnostics.problem.mark", "diagnostics.report_problem", "diagnostics.recovery.suggest"],
  });
}

function diagnosticsProblemMark(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const reportDir = problemReportDir(context);
  fs.mkdirSync(reportDir, { recursive: true });
  const reportIds = stringArray(args.report_ids).filter((reportId) => /^problem_[A-Za-z0-9_-]+$/.test(reportId)).slice(0, MAX_TRIAGE_REPORT_IDS);
  const status = stringOrNull(args.status);
  if (!isTriageStatus(status)) {
    return { status: "error", code: "invalid_triage_status", redaction_applied: true };
  }

  const decisions = readTriageDecisions(reportDir);
  const now = context.now?.() ?? new Date();
  const decision = sanitizeTriageDecision(args, status, now);
  const missing_report_ids: string[] = [];
  for (const reportId of reportIds) {
    if (!safeStat(path.join(reportDir, `${reportId}.json`))?.isFile()) {
      missing_report_ids.push(reportId);
    }
    decisions[reportId] = decision;
  }
  writeTriageDecisions(reportDir, decisions);

  return redactProblemReport({
    decision_path: triageDecisionPath(reportDir),
    marked_count: reportIds.length,
    report_ids: reportIds,
    missing_report_ids,
    decision: publicTriageDecision(decision),
    regression_guardrail: regressionGuardrail(decision),
    redaction_applied: true,
    next_valid_actions: ["diagnostics.problem.triage", "diagnostics.problem.list", "diagnostics.problem.read"],
  });
}

function diagnosticsRecoverySuggest(args: Record<string, unknown>, projectContext: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  const errorCode = stringOrEmpty(args.error_code);
  const command = stringOrEmpty(args.command);
  if (isSchemaRecoveryErrorCode(errorCode)) {
    const recovery = diagnosticsSchemaRecovery(command, projectContext);
    const actions = recovery.actions.map(recoveryActionFromExecutable);
    return redactDiagnostics({
      error_code: errorCode,
      command: recovery.safeCommand ?? null,
      ...publicDiagnosticsProjectContext(projectContext),
      schema_recovery_input_details_omitted: true,
      recovery_actions: actions,
      recovery_hint: actions[0]?.description ?? "Inspect project discovery before constructing a new explicit corrected call.",
      redaction_applied: true,
      next_valid_actions: actions.map((action) => action.action),
    });
  }

  const details = parseDetailsJson(stringOrEmpty(args.details_json));
  const suggestedCommand = commandReplacement(command);
  const actions = recoveryActions(errorCode, command, details, suggestedCommand, trustedOriginalProjectSelector(projectContext), trustedResolvedProjectId(projectContext));

  return redactDiagnostics({
    error_code: errorCode || null,
    command: command || null,
    ...publicDiagnosticsProjectContext(projectContext),
    tool: stringOrEmpty(args.tool) || null,
    message: stringOrEmpty(args.message) || null,
    details_parse_error: details.parseError,
    details: details.value,
    suggested_command: suggestedCommand ?? null,
    recovery_actions: actions,
    recovery_hint: actions[0]?.description ?? "Inspect the exact error code and command schema, then retry only after the blocking condition is resolved.",
    redaction_applied: true,
    next_valid_actions: actions.map((action) => action.action),
  });
}

function unityCrashReporterStatus(context: DispatcherContext): Record<string, unknown> {
  return context.unityCrashReporter?.status() ?? inspectUnityCrashReporter();
}

function unityProcessList(context: DispatcherContext, record: HeartbeatRecord | PublicProjectRecord | undefined, snapshot?: ProjectRegistrySnapshot): UnityProcessListResult {
  return unityProcessRuntime(context).status(snapshot?.listProjectRecords() ?? context.registry?.listProjectRecords() ?? [], publicProjectRecordOrUndefined(record));
}

function unityEditorInstallationsList(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const version = typeof args.version === "string" && args.version.length > 0 ? args.version : undefined;
  const discovery = context.unityEditorDiscovery?.status({ ...(version ? { version } : {}) }) ?? runUnityEditorDiscoveryScript(version);
  const editors = Array.isArray(discovery.editors) ? discovery.editors.slice(0, 50) : [];
  return redactDiagnostics({
    schema_version: 1,
    status: typeof discovery.status === "string" ? discovery.status : editors.length > 0 ? "ok" : "not_found",
    gateway_local: true,
    launch_unity_editor_attempted: false,
    arbitrary_roots_accepted: false,
    configured_roots_env_var: "SGG_UNITY_EDITOR_ROOTS",
    version_filter: version ?? null,
    editors_count: editors.length,
    editors,
    probes: Array.isArray(discovery.probes) ? discovery.probes.slice(0, 30) : [],
    ...(typeof discovery.error === "string" ? { error: discovery.error } : {}),
    redaction_applied: true,
    next_valid_actions: uniqueStrings([
      "use_discovered_unity_executable_for_UNITY_EXE",
      "set_SGG_UNITY_EDITOR_VERSION_for_validation",
      "set_SGG_UNITY_EDITOR_ROOTS_if_missing",
      "retry_unity_editor_validation_with_selected_version",
    ]),
  });
}

function runUnityEditorDiscoveryScript(version: string | undefined): Record<string, unknown> {
  const scriptPath = unityEditorDiscoveryScriptPath();
  if (!fs.existsSync(scriptPath)) {
    return {
      status: "unsupported",
      error: "unity-editor-discovery.mjs not found from current gateway working directory",
      editors: [],
      probes: [{ source: "gateway_script", status: "missing", path: scriptPath }],
    };
  }

  const scriptArgs = [scriptPath, "--json", ...(version ? ["--version", version] : [])];
  const result = spawnSync(process.execPath, scriptArgs, {
    cwd: process.cwd(),
    env: process.env,
    encoding: "utf8",
    timeout: UNITY_EDITOR_DISCOVERY_TIMEOUT_MS,
    windowsHide: true,
    maxBuffer: UNITY_EDITOR_DISCOVERY_MAX_OUTPUT_CHARS,
  });
  const stdout = (result.stdout ?? "").slice(0, UNITY_EDITOR_DISCOVERY_MAX_OUTPUT_CHARS);
  const stderr = (result.stderr ?? "").slice(0, 4000);
  if (stdout.trim().length > 0) {
    try {
      const parsed = JSON.parse(stdout) as unknown;
      if (isRecord(parsed)) {
        return {
          ...parsed,
          exit_status: result.status,
          signal: result.signal ?? null,
          ...(stderr ? { stderr } : {}),
        };
      }
    } catch (error) {
      return {
        status: "error",
        error: error instanceof Error ? error.message.slice(0, 4000) : "discovery_json_parse_failed",
        stdout,
        stderr,
        exit_status: result.status,
        signal: result.signal ?? null,
        editors: [],
        probes: [{ source: "gateway_script", status: "parse_error", path: scriptPath }],
      };
    }
  }

  return {
    status: result.error ? "error" : "not_found",
    error: result.error instanceof Error ? result.error.message.slice(0, 4000) : stderr || `exit ${result.status ?? "unknown"}`,
    exit_status: result.status,
    signal: result.signal ?? null,
    editors: [],
    probes: [{ source: "gateway_script", status: "error", path: scriptPath }],
  };
}

function unityEditorDiscoveryScriptPath(): string {
  for (const candidate of uniqueStrings([
    fileURLToPath(new URL("unity-editor-discovery.mjs", import.meta.url)),
    path.join(process.cwd(), "scripts", "unity-editor-discovery.mjs"),
  ])) {
    if (fs.existsSync(candidate)) {
      return candidate;
    }
  }

  return path.join(process.cwd(), "scripts", "unity-editor-discovery.mjs");
}

function unityProjectOpenStatus(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, reportedProject: string | undefined): Record<string, unknown> {
  const projects = context.registry?.listProjectRecords() ?? [];
  const processStatus = unityProcessRuntime(context).status(projects, publicProjectRecordOrUndefined(record));
  return redactDiagnostics(unityProjectOpenRuntime(context).status(unityProjectOpenArgs(args), {
    projectSelector: reportedProject,
    projects,
    processStatus,
    now: context.now,
  }));
}

function unityProjectOpen(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, reportedProject: string | undefined): Record<string, unknown> {
  const projects = context.registry?.listProjectRecords() ?? [];
  const processStatus = unityProcessRuntime(context).status(projects, publicProjectRecordOrUndefined(record));
  return redactDiagnostics(unityProjectOpenRuntime(context).open(unityProjectOpenArgs(args), {
    projectSelector: reportedProject,
    projects,
    processStatus,
    editorDiscovery: context.unityEditorDiscovery ?? { status: (discoveryArgs) => runUnityEditorDiscoveryScript(discoveryArgs?.version) },
    now: context.now,
  }));
}

function unityProjectOpenRuntime(context: DispatcherContext) {
  return context.unityProjectOpen ?? createUnityProjectOpenRuntime();
}

function unityProjectOpenArgs(args: Record<string, unknown>): UnityProjectOpenArgs {
  return {
    ...(typeof args.project_selector === "string" ? { project_selector: args.project_selector } : {}),
    ...(typeof args.canonical_project_root === "string" ? { canonical_project_root: args.canonical_project_root } : {}),
    ...(typeof args.unity_version === "string" ? { unity_version: args.unity_version } : {}),
    ...(typeof args.dry_run === "boolean" ? { dry_run: args.dry_run } : {}),
    ...(typeof args.confirm_open === "boolean" ? { confirm_open: args.confirm_open } : {}),
  };
}

function unityStartupStatus(context: DispatcherContext, record: HeartbeatRecord | undefined): Record<string, unknown> {
  const projects = context.registry?.listProjectRecords() ?? [];
  const processStatus = unityProcessRuntime(context).status(projects, publicProjectRecordOrUndefined(record));
  const modalStatus = unityModalStatus(context, record);
  const libraryLockDiagnostics = unityLibraryLockDiagnostics(context, record, processStatus);
  const startupLogDiagnostics = unityStartupLogDiagnostics(record);
  const staleProjects = projects.filter((project) => project.stale).slice(0, 20);
  const currentProjectProcesses = processStatus.processes.filter((process) => process.current_project_match);
  const hasTerminatableCurrentProjectProcess = currentProjectProcesses.some((process) => process.eligible_for_terminate);
  const readOnlyModalBlocked = ["blocked_scoped_registry_import", "blocked_scene_backup_recovery", "blocked_low_disk_space"]
    .includes(String((modalStatus as { blocking_modal_code?: unknown }).blocking_modal_code ?? ""));
  const hasSuspectedStaleLibraryLocks = typeof libraryLockDiagnostics.suspected_stale_lock_count === "number" && libraryLockDiagnostics.suspected_stale_lock_count > 0;
  const hasStartupLogMarkers = typeof startupLogDiagnostics.matched_marker_count === "number" && startupLogDiagnostics.matched_marker_count > 0;

  return redactDiagnostics({
    schema_version: 1,
    blocking_startup_possible: hasTerminatableCurrentProjectProcess
      || staleProjects.length > 0
      || hasSuspectedStaleLibraryLocks
      || Boolean((modalStatus as { blocking_startup_possible?: unknown }).blocking_startup_possible),
    current_project_process_count: currentProjectProcesses.length,
    current_project_processes: currentProjectProcesses,
    stale_project_count: staleProjects.length,
    stale_projects: staleProjects,
    library_lock_diagnostics: libraryLockDiagnostics,
    startup_log_diagnostics: startupLogDiagnostics,
    unity_processes: processStatus,
    unity_modal: modalStatus,
    next_valid_actions: uniqueStrings([
      "unity.process.list",
      "diagnostics.unity_modal.status",
      ...(hasTerminatableCurrentProjectProcess && !readOnlyModalBlocked ? ["dry_run_unity.process.terminate"] : []),
      ...(hasSuspectedStaleLibraryLocks ? ["operator_verify_no_unity_owner_before_removing_library_lock_files"] : []),
      ...(hasStartupLogMarkers ? ["inspect_startup_log_marker_evidence"] : []),
      ...(staleProjects.length > 0 ? ["sgg_unity_projects"] : []),
      "diagnostics.report_problem",
    ]),
    redaction_applied: true,
  });
}

function unityLibraryLockDiagnostics(context: DispatcherContext, record: HeartbeatRecord | PublicProjectRecord | undefined, processStatus?: UnityProcessListResult, snapshot?: ProjectRegistrySnapshot, reportRecords?: PublicProjectRecord[]): Record<string, unknown> {
  const projects = (reportRecords ?? unityLibraryLockProjectRecords(context, record, snapshot)).slice(0, PROBLEM_REPORT_MAX_REGISTRY_RECORDS);
  const processes = processStatus ?? unityProcessRuntime(context).status(snapshot?.listProjectRecords() ?? context.registry?.listProjectRecords() ?? [], publicProjectRecordOrUndefined(record));
  const now = context.now?.() ?? new Date();
  const locks = projects.flatMap((project) => UNITY_LIBRARY_LOCKS.map((relativePath) => unityLibraryLockInspection(project, relativePath, processes, now)));
  const existingLocks = locks.filter((lock) => lock.exists === true);
  const suspectedStaleLocks = locks.filter((lock) => lock.suspected_stale === true);
  const activeOwnerLocks = locks.filter((lock) => lock.owner_status === "active_owner_detected");

  return {
    schema_version: 1,
    diagnostic_only: true,
    read_only: true,
    deletion_attempted: false,
    fixed_relative_paths: [...UNITY_LIBRARY_LOCKS],
    inspected_project_count: projects.length,
    inspected_lock_count: locks.length,
    existing_lock_count: existingLocks.length,
    suspected_stale_lock_count: suspectedStaleLocks.length,
    active_owner_lock_count: activeOwnerLocks.length,
    process_scan_supported: processes.supported,
    process_errors: processes.errors,
    locks,
    operator_guidance: suspectedStaleLocks.length > 0
      ? "Read-only diagnostics found Unity Library lock files with no active owner evidence. Verify Unity is closed for the project, preserve the report evidence, then have the operator remove only the reported Library lock files before retrying; the Gateway did not delete them."
      : existingLocks.length > 0
        ? "Unity Library lock files exist, but active owner evidence or unsupported process scanning makes automatic cleanup unsafe. Inspect owner evidence before any manual cleanup; the Gateway did not delete them."
        : "No Unity Library lock files were found in accessible project root aliases.",
    next_valid_actions: uniqueStrings([
      "unity.process.list",
      "sgg_unity_projects",
      ...(suspectedStaleLocks.length > 0 ? ["operator_verify_no_unity_owner_before_removing_library_lock_files", "retry_after_operator_lock_cleanup"] : []),
      "diagnostics.report_problem",
    ]),
  };
}

function unityStartupLogDiagnostics(record: HeartbeatRecord | undefined): Record<string, unknown> {
  const candidates = unityStartupLogPathCandidates(record);
  const probes = candidates.slice(0, 20).map((candidate) => unityStartupLogProbe(candidate));
  const readable = probes.find((probe) => probe.exists === true && probe.readable === true);
  const markerSummaries = readable ? unityStartupLogMarkerSummaries(stringArray(readable.lines)) : [];

  return {
    schema_version: 1,
    diagnostic_only: true,
    read_only: true,
    deletion_attempted: false,
    max_tail_lines: UNITY_STARTUP_LOG_TAIL_LINES,
    max_tail_bytes: UNITY_STARTUP_LOG_TAIL_BYTES,
    candidate_count: candidates.length,
    selected_log_path: readable?.path ?? null,
    selected_source: readable?.source ?? null,
    selected_exists: readable?.exists ?? false,
    selected_readable: readable?.readable ?? false,
    matched_marker_count: markerSummaries.length,
    markers: markerSummaries,
    probes,
    next_valid_actions: uniqueStrings([
      "unity.process.list",
      "diagnostics.unity_modal.status",
      ...(markerSummaries.length > 0 ? ["inspect_startup_log_marker_evidence"] : []),
      "diagnostics.report_problem",
    ]),
  };
}

function problemStartupLogDiagnostics(value: Record<string, unknown>): Record<string, unknown> {
  const probes = Array.isArray(value.probes) ? value.probes.map((probe) => {
    if (!isRecord(probe)) return probe;
    const { lines, ...summary } = probe;
    return {
      ...summary,
      lines_omitted_from_problem_report: Array.isArray(lines) && lines.length > 0,
      omitted_line_count: Array.isArray(lines) ? lines.length : 0,
    };
  }) : [];
  const markers = Array.isArray(value.markers) ? value.markers.map((marker) => {
    if (!isRecord(marker)) return marker;
    const { latest_line: latestLine, ...summary } = marker;
    return {
      ...summary,
      latest_line_omitted_from_problem_report: typeof latestLine === "string" && latestLine.length > 0,
    };
  }) : [];
  return { ...value, markers, probes };
}

function unityStartupLogProbe(candidate: { source: string; path: string }): Record<string, unknown> & { exists: boolean; readable: boolean; lines?: string[] } {
  try {
    const stat = fs.lstatSync(candidate.path);
    if (stat.isSymbolicLink()) {
      return { source: candidate.source, path: candidate.path, exists: true, readable: false, is_symlink: true, reason: "startup_log_symlink_refused" };
    }
    if (!stat.isFile()) {
      return { source: candidate.source, path: candidate.path, exists: true, readable: false, is_file: false, reason: "startup_log_not_regular_file" };
    }

    const tail = readBoundedTail(candidate.path, UNITY_STARTUP_LOG_TAIL_BYTES);
    const lines = tail.text.split(/\r?\n/).filter((line) => line.length > 0).slice(-UNITY_STARTUP_LOG_TAIL_LINES).map((line) => redactText(line));
    return {
      source: candidate.source,
      path: candidate.path,
      exists: true,
      readable: true,
      is_file: true,
      size_bytes: stat.size,
      last_write_utc: stat.mtime.toISOString(),
      max_lines: UNITY_STARTUP_LOG_TAIL_LINES,
      max_bytes: UNITY_STARTUP_LOG_TAIL_BYTES,
      line_count: lines.length,
      bytes_read: tail.bytesRead,
      truncated_by_bytes: tail.truncatedByBytes,
      lines,
    };
  } catch (error) {
    if (isFsNotFound(error)) {
      return { source: candidate.source, path: candidate.path, exists: false, readable: false, reason: "startup_log_not_found" };
    }
    return { source: candidate.source, path: candidate.path, exists: false, readable: false, reason: "startup_log_probe_error", error: fsErrorSummary(error) };
  }
}

function unityStartupLogMarkerSummaries(lines: string[]): Array<Record<string, unknown>> {
  const markers: Array<Record<string, unknown>> = [];
  for (const marker of UNITY_STARTUP_LOG_MARKERS) {
    const matches = lines.filter((line) => marker.patterns.some((pattern) => line.toLowerCase().includes(pattern.toLowerCase())));
    if (matches.length === 0) {
      continue;
    }
    markers.push({
      code: marker.code,
      count: matches.length,
      latest_line: matches[matches.length - 1] ?? "",
      patterns: [...marker.patterns],
    });
  }
  return markers;
}

function unityStartupLogPathCandidates(record: HeartbeatRecord | undefined): Array<{ source: string; path: string }> {
  const candidates: Array<{ source: string; path: string }> = [];
  const add = (source: string, candidatePath: string | undefined) => {
    if (candidatePath && candidatePath.trim().length > 0 && !candidates.some((candidate) => candidate.path === candidatePath)) {
      candidates.push({ source, path: candidatePath });
    }
  };

  add("env:SGG_UNITY_EDITOR_LOG_PATH", process.env.SGG_UNITY_EDITOR_LOG_PATH);
  if (record) {
    for (const root of projectRootCandidates(record).slice(0, 10)) {
      add("project:validation_log", path.join(root, "Logs", "sgg-unity-mcp-editor-check.log"));
    }
  }
  add("env:LOCALAPPDATA", process.env.LOCALAPPDATA ? path.join(process.env.LOCALAPPDATA, "Unity", "Editor", "Editor.log") : undefined);
  add("env:USERPROFILE", process.env.USERPROFILE ? path.join(process.env.USERPROFILE, "AppData", "Local", "Unity", "Editor", "Editor.log") : undefined);
  add("home:unity3d", path.join(os.homedir(), ".config", "unity3d", "Editor.log"));
  const username = os.userInfo().username;
  add("wsl:windows-localappdata", username ? path.join("/mnt/c/Users", username, "AppData", "Local", "Unity", "Editor", "Editor.log") : undefined);
  return candidates;
}

function unityLibraryLockInspection(project: PublicProjectRecord, relativePath: typeof UNITY_LIBRARY_LOCKS[number], processStatus: UnityProcessListResult, now: Date): Record<string, unknown> & { exists: boolean; suspected_stale: boolean; owner_status: string; matching_process_count: number } {
  const probe = inspectUnityLibraryLockPath(project, relativePath);
  const matchingProcesses = matchingUnityProcessesForProject(project, processStatus).slice(0, 10);
  const ownerStatus = matchingProcesses.length > 0
    ? "active_owner_detected"
    : processStatus.supported
      ? "no_owner_detected"
      : "owner_scan_unsupported";
  const classification = unityLibraryLockClassification(probe.exists, probe.isSymlink, probe.isFile, ownerStatus, project.stale);

  return {
    lock_name: relativePath.slice(relativePath.lastIndexOf("/") + 1),
    relative_path: relativePath,
    lock_path: probe.lockPath,
    inspected_project_root: probe.projectRoot,
    project: project.project,
    instance_id: project.instance_id,
    display_name: project.display_name ?? null,
    project_status: project.status,
    project_stale: project.stale,
    unity_pid: project.unity_pid,
    last_heartbeat: project.last_heartbeat,
    heartbeat_age_ms: heartbeatAgeMs(project.last_heartbeat, now),
    project_root: project.project_root,
    canonical_project_root: project.canonical_project_root ?? null,
    exists: probe.exists,
    is_symlink: probe.isSymlink,
    is_file: probe.isFile,
    size_bytes: probe.stat?.size ?? null,
    last_write_utc: probe.stat?.mtime.toISOString() ?? null,
    age_ms: probe.stat ? Math.max(0, now.getTime() - probe.stat.mtime.getTime()) : null,
    owner_status: ownerStatus,
    matching_process_count: matchingProcesses.length,
    matching_processes: matchingProcesses,
    process_scan_supported: processStatus.supported,
    process_errors: processStatus.errors,
    suspected_stale: classification.suspectedStale,
    stale_classification: classification.reason,
    safe_to_auto_delete: false,
    operator_cleanup_required: classification.suspectedStale,
    cleanup_guidance: probe.exists
      ? "Do not delete automatically. If owner evidence confirms no Unity process owns this project, an operator can remove this specific Library lock file and retry Unity startup or validation."
      : "No cleanup action is needed for this lock path because the file was not found.",
    probe_errors: probe.errors,
  };
}

function unityLibraryLockProjectRecords(context: DispatcherContext, record: HeartbeatRecord | PublicProjectRecord | undefined, snapshot?: ProjectRegistrySnapshot): PublicProjectRecord[] {
  const records = snapshot?.listProjectRecords() ?? context.registry?.listProjectRecords() ?? [];
  const current = publicProjectRecordOrUndefined(record);
  return uniquePublicRecords(current ? [current, ...records] : records);
}

function inspectUnityLibraryLockPath(project: PublicProjectRecord, relativePath: typeof UNITY_LIBRARY_LOCKS[number]): { exists: boolean; projectRoot: string | null; lockPath: string | null; stat: fs.Stats | null; isSymlink: boolean; isFile: boolean; errors: string[] } {
  const candidates = projectRootCandidates(project).slice(0, 10);
  const errors: string[] = [];
  for (const projectRoot of candidates) {
    const lockPath = projectRelativeFilePath(projectRoot, relativePath);
    try {
      const stat = fs.lstatSync(lockPath);
      return { exists: true, projectRoot, lockPath, stat, isSymlink: stat.isSymbolicLink(), isFile: stat.isFile(), errors };
    } catch (error) {
      if (!isFsNotFound(error)) {
        errors.push(`${projectRoot}: ${fsErrorSummary(error)}`);
      }
    }
  }

  const projectRoot = firstExistingProjectRoot(candidates) ?? candidates[0] ?? null;
  return {
    exists: false,
    projectRoot,
    lockPath: projectRoot ? projectRelativeFilePath(projectRoot, relativePath) : null,
    stat: null,
    isSymlink: false,
    isFile: false,
    errors,
  };
}

function matchingUnityProcessesForProject(project: PublicProjectRecord, processStatus: UnityProcessListResult): UnityProcessSnapshot[] {
  const roots = canonicalRoots(project);
  return processStatus.processes.filter((process) => {
    if (process.pid === project.unity_pid) {
      return true;
    }
    if (process.canonical_project_root && roots.has(process.canonical_project_root)) {
      return true;
    }
    return process.heartbeat_matches.some((match) => match.instance_id === project.instance_id || (match.canonical_project_root !== null && roots.has(match.canonical_project_root)));
  });
}

function unityLibraryLockClassification(exists: boolean, isSymlink: boolean, isFile: boolean, ownerStatus: string, projectStale: boolean): { suspectedStale: boolean; reason: string } {
  if (!exists) {
    return { suspectedStale: false, reason: "not_present" };
  }
  if (isSymlink) {
    return { suspectedStale: false, reason: "lock_path_symlink_refused" };
  }
  if (!isFile) {
    return { suspectedStale: false, reason: "lock_path_not_regular_file" };
  }
  if (ownerStatus === "active_owner_detected") {
    return { suspectedStale: false, reason: "active_owner_detected" };
  }
  if (ownerStatus === "owner_scan_unsupported") {
    return projectStale
      ? { suspectedStale: true, reason: "suspected_stale_stale_heartbeat_owner_scan_unsupported" }
      : { suspectedStale: false, reason: "indeterminate_owner_scan_unsupported" };
  }
  return { suspectedStale: true, reason: "suspected_stale_no_owner" };
}

function firstExistingProjectRoot(candidates: string[]): string | undefined {
  for (const candidate of candidates) {
    try {
      if (fs.lstatSync(candidate).isDirectory()) {
        return candidate;
      }
    } catch {
      // Try the next alias; Windows paths are often inaccessible from WSL and vice versa.
    }
  }
  return undefined;
}

function projectRelativeFilePath(projectRoot: string, relativePath: string): string {
  return path.join(projectRoot, ...relativePath.split("/"));
}

function isFsNotFound(error: unknown): boolean {
  return typeof error === "object" && error !== null && "code" in error && (error as NodeJS.ErrnoException).code === "ENOENT";
}

function fsErrorSummary(error: unknown): string {
  if (typeof error === "object" && error !== null && "code" in error) {
    const fsError = error as NodeJS.ErrnoException;
    return `${fsError.code ?? "fs_error"}: ${fsError.message}`.slice(0, 400);
  }
  return String(error).slice(0, 400);
}

function unityProcessTerminate(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined): Record<string, unknown> {
  const terminateArgs: UnityProcessTerminateArgs = {
    ...(typeof args.pid === "number" ? { pid: args.pid } : {}),
    ...(typeof args.canonical_project_root === "string" ? { canonical_project_root: args.canonical_project_root } : {}),
    ...(typeof args.confirm_terminate === "boolean" ? { confirm_terminate: args.confirm_terminate } : {}),
    ...(typeof args.dry_run === "boolean" ? { dry_run: args.dry_run } : {}),
    ...(typeof args.force === "boolean" ? { force: args.force } : {}),
  };
  return unityProcessRuntime(context).terminate(terminateArgs, context.registry?.listProjectRecords() ?? [], publicProjectRecordOrUndefined(record));
}

function unityProcessRuntime(context: DispatcherContext) {
  return context.unityProcessDiagnostics ?? createUnityProcessRuntime();
}

function unityScopedRegistryImportStatus(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined): Record<string, unknown> {
  const requestedPid = typeof args.pid === "number" ? args.pid : record?.unity_pid;
  const requestedProcessStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : record?.unity_process_start_utc;
  const targetBindingComplete = requestedPid === undefined && requestedProcessStartUtc === undefined
    || (Number.isInteger(requestedPid) && (requestedPid ?? 0) > 0 && typeof requestedProcessStartUtc === "string" && !Number.isNaN(Date.parse(requestedProcessStartUtc)));
  const status = context.unityWindowDiagnostics?.status({ scan_budget_ms: SCOPED_REGISTRY_WINDOW_SCAN_BUDGET_MS })
    ?? inspectUnityWindowDiagnostics({ scanBudgetMs: SCOPED_REGISTRY_WINDOW_SCAN_BUDGET_MS });
  const records = context.registry?.listProjectRecords() ?? [];
  const processStatus = unityProcessRuntime(context).status(records, publicProjectRecordOrUndefined(record), { scan_budget_ms: SCOPED_REGISTRY_PROCESS_SCAN_BUDGET_MS });
  const exactCandidates = (status.scoped_registry_import_modal_candidates ?? [])
    .filter((candidate) => candidate.block_code === "blocked_scoped_registry_import")
    .filter((candidate) => requestedPid === undefined || candidate.pid === requestedPid)
    .filter((candidate) => requestedProcessStartUtc === undefined || candidate.process_start_utc === requestedProcessStartUtc);
  const candidates = exactCandidates.slice(0, MAX_SCOPED_REGISTRY_CANDIDATES).map((candidate) => {
    const scoped = candidate.scoped_registry_import!;
    return {
      blocker_code: "blocked_scoped_registry_import",
      pid: candidate.pid,
      process_name: candidate.process_name,
      process_start_utc: candidate.process_start_utc,
      window_thread_id: candidate.window_thread_id,
      class_name: candidate.class_name,
      modal_identity_sha256: `sha256:${createHash("sha256").update(`${candidate.pid}\n${candidate.process_start_utc}\n${candidate.hwnd}\n${candidate.window_thread_id}`).digest("hex")}`,
      title_sha256: scoped.title_sha256,
      body_sha256: scoped.body_sha256,
      visible_control_count: scoped.visible_control_count,
      button_count: scoped.button_count,
      learn_more_button_count: scoped.learn_more_button_count,
      close_button_count: scoped.close_button_count,
      diagnostic_only: true,
      interaction_supported_by_status: false,
      project_correlation: scopedRegistryProjectCorrelation(candidate.pid, candidate.process_start_utc, processStatus, records),
    };
  });
  const user32Authoritative = status.native_modal_scan_authoritative && status.semantic_channels.native_user32.authoritative;
  const scanAuthoritative = status.aggregate_scan_authoritative && user32Authoritative && processStatus.supported && !processStatus.scan_truncated && processStatus.errors.length === 0;
  return redactDiagnostics({
    schema_version: 1,
    scan_scope: "fresh_process_wide_exact_scoped_registry_import",
    scan_fresh: true,
    diagnostic_only: true,
    read_only: true,
    interaction_attempted: false,
    target_binding_complete: targetBindingComplete,
    target_pid: requestedPid ?? null,
    target_process_start_utc: requestedProcessStartUtc ?? null,
    scan_authoritative: scanAuthoritative,
    native_user32_authoritative: user32Authoritative,
    process_scan_authoritative: processStatus.supported && !processStatus.scan_truncated && processStatus.errors.length === 0,
    candidate_count: candidates.length,
    candidates_truncated: exactCandidates.length > candidates.length,
    zero_candidates_authoritative: targetBindingComplete && scanAuthoritative && candidates.length === 0,
    candidates,
    errors: uniqueStrings([
      ...status.errors.slice(0, 8).map((error) => `window_scan:${error}`),
      ...processStatus.errors.slice(0, 8).map((error) => `process_scan:${error}`),
      ...(!targetBindingComplete ? ["target_process_binding_incomplete"] : []),
    ]),
    next_valid_actions: candidates.length === 1 && candidates[0]?.project_correlation.status === "exact" && user32Authoritative
      ? ["dry_run_diagnostics.unity_scoped_registry_import.close", "do_not_click_learn_more", "do_not_repeat_blocked_operation"]
      : ["retry_fresh_diagnostics.unity_scoped_registry_import.status", "require_exact_process_and_project_correlation", "do_not_interact_with_unbound_candidates"],
  });
}

function scopedRegistryProjectCorrelation(pid: number, processStartUtc: string | undefined, processStatus: UnityProcessListResult, records: PublicProjectRecord[]): Record<string, unknown> {
  if (!processStatus.supported || processStatus.scan_truncated || processStatus.errors.length > 0 || !processStartUtc) {
    return { status: "unbound", evidence: "authoritative_process_scan_unavailable" };
  }
  const processes = processStatus.processes.filter((process) => process.role === "unity_editor" && process.pid === pid && process.process_start_utc === processStartUtc);
  if (processes.length !== 1 || !processes[0]?.canonical_project_root) {
    return { status: "unbound", evidence: processes.length > 1 ? "process_identity_ambiguous" : "exact_process_root_evidence_unavailable" };
  }
  const process = processes[0];
  const projectIds = new Set(records
    .filter((candidate) => candidate.unity_pid === pid
      && candidate.unity_process_start_utc === processStartUtc
      && candidate.canonical_project_root === process.canonical_project_root)
    .map((candidate) => candidate.project));
  if (projectIds.size !== 1) {
    return { status: "unbound", evidence: projectIds.size > 1 ? "registry_project_identity_ambiguous" : "no_unique_registry_project_for_exact_process_root" };
  }
  return { status: "exact", project: [...projectIds][0], evidence: "process_generation_and_command_line_canonical_root" };
}

function unityScopedRegistryImportClose(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, projectContext: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  const pid = typeof args.pid === "number" ? args.pid : 0;
  const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
  const requestedRoot = canonicalProjectRootForDowngrade(args.canonical_project_root);
  const dryRun = args.dry_run !== false;
  const refuse = (error: string): Record<string, unknown> => ({
    schema_version: 1,
    action: "close",
    dry_run: dryRun,
    status: "refused",
    interaction_attempted: false,
    native_dispatch_count: 0,
    delivery_state: "not_attempted",
    popup_state: "not_checked",
    popup_disappearance_verified: false,
    blocked_operation_replayed: false,
    target_pid: pid > 0 ? pid : null,
    process_start_utc: Number.isNaN(Date.parse(processStartUtc)) ? null : processStartUtc,
    error,
    next_valid_actions: ["call_diagnostics.unity_scoped_registry_import.status", "create_a_new_dry_run_plan", "do_not_click_learn_more", "do_not_repeat_blocked_operation"],
  });
  if (!requestedRoot || requestedRoot !== args.canonical_project_root || pid <= 0 || Number.isNaN(Date.parse(processStartUtc))) return refuse("invalid_scoped_registry_project_or_process_binding");
  if (record && (record.unity_pid !== pid || record.unity_process_start_utc !== processStartUtc || record.canonical_project_root !== requestedRoot)) return refuse("scoped_registry_selected_project_heartbeat_mismatch");
  const records = context.registry?.listProjectRecords() ?? [];
  const exactRecords = records.filter((candidate) => candidate.project === (record?.project ?? candidate.project)
    && candidate.unity_pid === pid
    && candidate.unity_process_start_utc === processStartUtc
    && candidate.canonical_project_root === requestedRoot);
  const matchingProjectIds = new Set(exactRecords.map((candidate) => candidate.project));
  const selector = projectContext.original_project_selector;
  const selectorRoot = canonicalProjectRootForDowngrade(selector);
  const selectorMatches = record !== undefined
    || selectorRoot === requestedRoot
    || (typeof selector === "string" && matchingProjectIds.has(selector));
  if (matchingProjectIds.size !== 1 || !selectorMatches) return refuse(matchingProjectIds.size > 1 ? "scoped_registry_project_identity_ambiguous" : "scoped_registry_project_identity_mismatch");
  const processStatus = unityProcessRuntime(context).status(records, publicProjectRecordOrUndefined(record), { scan_budget_ms: SCOPED_REGISTRY_PROCESS_SCAN_BUDGET_MS });
  if (!processStatus.supported || processStatus.scan_truncated || processStatus.errors.length > 0) return refuse("authoritative_scoped_registry_process_scan_required");
  const processes = processStatus.processes.filter((process) => process.role === "unity_editor"
    && process.pid === pid
    && process.process_start_utc === processStartUtc
    && process.canonical_project_root === requestedRoot);
  if (processes.length !== 1) return refuse(processes.length > 1 ? "scoped_registry_process_identity_ambiguous" : "scoped_registry_process_identity_mismatch");
  const runtime = context.unityWindowDiagnostics;
  if (!runtime?.planScopedRegistryImportClose || !runtime.closeScopedRegistryImport) return refuse("scoped_registry_response_runtime_unavailable");
  const runtimeArgs: UnityScopedRegistryImportCloseArgs = {
    pid,
    process_start_utc: processStartUtc,
    project_identity_sha256: `sha256:${createHash("sha256").update(requestedRoot).digest("hex")}`,
    dry_run: dryRun,
    ...(args.confirm_close === true ? { confirm_close: true } : {}),
    ...(typeof args.precondition_hash === "string" ? { precondition_hash: args.precondition_hash } : {}),
  };
  if (!dryRun && (args.confirm_close !== true || typeof args.precondition_hash !== "string")) return refuse("scoped_registry_close_requires_exact_plan_and_confirmation");
  return redactDiagnostics(dryRun ? runtime.planScopedRegistryImportClose(runtimeArgs) : guardedPopupResponse(context, runtime.closeScopedRegistryImport, runtimeArgs));
}

function unitySafeModeEnter(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, projectContext: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  const pid = typeof args.pid === "number" ? args.pid : 0;
  const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
  const requestedRoot = canonicalProjectRootForDowngrade(args.canonical_project_root);
  const dryRun = args.dry_run !== false;
  const refuse = (error: string): Record<string, unknown> => ({
    schema_version: 1,
    action: "enter_safe_mode",
    dry_run: dryRun,
    status: "refused",
    interaction_attempted: false,
    native_dispatch_count: 0,
    delivery_state: "not_attempted",
    safe_mode_entered_verified: false,
    popup_closed_verified: false,
    target_pid: pid > 0 ? pid : null,
    process_start_utc: Number.isNaN(Date.parse(processStartUtc)) ? null : processStartUtc,
    error,
    next_valid_actions: ["call_diagnostics.unity_modal.status", "create_a_new_dry_run_plan", "do_not_choose_ignore_or_quit_from_gateway"],
  });
  if (!requestedRoot || requestedRoot !== args.canonical_project_root || pid <= 0 || Number.isNaN(Date.parse(processStartUtc))) return refuse("invalid_safe_mode_project_or_process_binding");
  if (record && (record.unity_pid !== pid || record.unity_process_start_utc !== processStartUtc || record.canonical_project_root !== requestedRoot)) return refuse("safe_mode_selected_project_heartbeat_mismatch");
  const records = context.registry?.listProjectRecords() ?? [];
  const exactRecords = records.filter((candidate) => candidate.project === (record?.project ?? candidate.project)
    && candidate.unity_pid === pid
    && candidate.unity_process_start_utc === processStartUtc
    && candidate.canonical_project_root === requestedRoot);
  const matchingProjectIds = new Set(exactRecords.map((candidate) => candidate.project));
  const selector = projectContext.original_project_selector;
  const selectorRoot = canonicalProjectRootForDowngrade(selector);
  const selectorMatches = record !== undefined
    || selectorRoot === requestedRoot
    || (typeof selector === "string" && matchingProjectIds.has(selector));
  if (matchingProjectIds.size !== 1 || !selectorMatches) return refuse(matchingProjectIds.size > 1 ? "safe_mode_project_identity_ambiguous" : "safe_mode_project_identity_mismatch");
  const processStatus = unityProcessRuntime(context).status(records, publicProjectRecordOrUndefined(record), { scan_budget_ms: SCOPED_REGISTRY_PROCESS_SCAN_BUDGET_MS });
  if (!processStatus.supported || processStatus.scan_truncated || processStatus.errors.length > 0) return refuse("authoritative_safe_mode_process_scan_required");
  const processes = processStatus.processes.filter((process) => process.role === "unity_editor"
    && process.pid === pid
    && process.process_start_utc === processStartUtc
    && process.canonical_project_root === requestedRoot);
  if (processes.length !== 1) return refuse(processes.length > 1 ? "safe_mode_process_identity_ambiguous" : "safe_mode_process_identity_mismatch");
  const runtime = context.unityWindowDiagnostics;
  if (!runtime?.planSafeModeEnter || !runtime.enterSafeMode) return refuse("safe_mode_response_runtime_unavailable");
  const runtimeArgs: UnitySafeModeEnterArgs = {
    pid,
    process_start_utc: processStartUtc,
    project_identity_sha256: `sha256:${createHash("sha256").update(requestedRoot).digest("hex")}`,
    dry_run: dryRun,
    ...(args.confirm_enter_safe_mode === true ? { confirm_enter_safe_mode: true } : {}),
    ...(typeof args.precondition_hash === "string" ? { precondition_hash: args.precondition_hash } : {}),
  };
  if (!dryRun && (args.confirm_enter_safe_mode !== true || typeof args.precondition_hash !== "string")) return refuse("safe_mode_enter_requires_exact_plan_and_confirmation");
  return redactDiagnostics(dryRun ? runtime.planSafeModeEnter(runtimeArgs) : guardedPopupResponse(context, runtime.enterSafeMode, runtimeArgs));
}

function sceneBackupRootFingerprint(value: unknown): SceneBackupRootFingerprint {
  if (typeof value !== "string" || value.length === 0 || value.length > 512) return "unavailable";
  return `sha256:${createHash("sha256").update("sgg-unity-mcp:pr219:scene-backup-root:v1\0").update(value).digest("hex")}`;
}

function unitySceneBackupKeep(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, projectContext: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  const pid = typeof args.pid === "number" ? args.pid : 0;
  const processStartUtc = typeof args.process_start_utc === "string" ? args.process_start_utc : "";
  const requestedRoot = canonicalProjectRootForDowngrade(args.canonical_project_root);
  const dryRun = args.dry_run !== false;
  const bindingWitness: UnitySceneBackupBindingWitness = {
    schema_version: 1, stage: "input_validation",
    input_validation: {
      canonical_root_exact: requestedRoot ? requestedRoot === args.canonical_project_root : "unavailable",
      pid_positive: pid > 0,
      process_start_utc_parseable: !Number.isNaN(Date.parse(processStartUtc)),
    },
    root_fingerprints: {
      input: sceneBackupRootFingerprint(args.canonical_project_root),
      normalized: sceneBackupRootFingerprint(requestedRoot),
      selected: sceneBackupRootFingerprint(record?.canonical_project_root),
    },
    selected_root_equal: "unavailable",
    process_scan: { authoritative: "unavailable", matching_editor_count: "unavailable" },
    pid_equal: "unavailable", process_start_utc_equal: "unavailable",
  };
  const refuse = (error: string): Record<string, unknown> => ({
    schema_version: 1, action: "keep_scene_backups", dry_run: dryRun,
    status: "refused", interaction_attempted: false, native_dispatch_count: 0,
    delivery_state: "not_attempted", backups_kept_verified: false, popup_closed_verified: false,
    error, binding_witness: bindingWitness,
    next_valid_actions: ["call_diagnostics.unity_modal.status", "create_a_new_dry_run_plan", "do_not_select_delete_or_replay_blocked_operations"],
  });
  if (!requestedRoot || requestedRoot !== args.canonical_project_root || pid <= 0 || !bindingWitness.input_validation.process_start_utc_parseable) return refuse("invalid_scene_backup_project_or_process_binding");
  bindingWitness.stage = "project_identity";
  if (record) bindingWitness.selected_root_equal = record.canonical_project_root === requestedRoot;
  if (record && record.canonical_project_root !== requestedRoot) return refuse("scene_backup_selected_project_root_mismatch");
  const records = context.registry?.listProjectRecords() ?? [];
  // A restarted Editor can be blocked before its first heartbeat. Historical
  // records prove the project identity only, never the current process owner.
  const projectIds = new Set(records.filter((candidate) => candidate.canonical_project_root === requestedRoot).map((candidate) => candidate.project));
  const selectorRoot = canonicalProjectRootForDowngrade(projectContext.original_project_selector);
  const selectedProject = record?.project ?? (projectContext.project_resolved ? projectContext.resolved_project_id : undefined);
  if (projectIds.size !== 1 || (selectedProject ? !projectIds.has(selectedProject) : selectorRoot !== requestedRoot)) return refuse("scene_backup_known_project_identity_mismatch_or_ambiguous");
  bindingWitness.root_fingerprints.selected = sceneBackupRootFingerprint(record?.canonical_project_root ?? requestedRoot);
  bindingWitness.selected_root_equal = true;
  bindingWitness.stage = "process_scan";
  const processStatus = unityProcessRuntime(context).status(records, publicProjectRecordOrUndefined(record), { scan_budget_ms: SCOPED_REGISTRY_PROCESS_SCAN_BUDGET_MS });
  if (!processStatus.supported || processStatus.scan_truncated || processStatus.errors.length > 0) {
    bindingWitness.process_scan = { authoritative: false, matching_editor_count: "unavailable" };
    return refuse("authoritative_scene_backup_process_scan_required");
  }
  const processes = processStatus.processes.filter((process) => process.role === "unity_editor" && process.canonical_project_root === requestedRoot);
  bindingWitness.process_scan = { authoritative: true, matching_editor_count: processes.length };
  bindingWitness.stage = "current_process";
  if (processes.length !== 1) return refuse("scene_backup_current_process_identity_mismatch_or_ambiguous");
  bindingWitness.pid_equal = processes[0]!.pid === pid;
  if (!bindingWitness.pid_equal) return refuse("scene_backup_current_process_identity_mismatch_or_ambiguous");
  bindingWitness.process_start_utc_equal = typeof processes[0]!.process_start_utc === "string" ? processes[0]!.process_start_utc === processStartUtc : "unavailable";
  if (processes[0]!.process_start_utc !== processStartUtc) return refuse("scene_backup_current_process_identity_mismatch_or_ambiguous");
  bindingWitness.stage = "runtime_admission";
  if (!dryRun && (args.confirm_keep_backups !== true || typeof args.precondition_hash !== "string")) return refuse("scene_backup_keep_requires_exact_plan_and_confirmation");
  const runtime = context.unityWindowDiagnostics;
  if (!runtime?.keepSceneBackups) return refuse("scene_backup_keep_runtime_unavailable");
  const result = guardedPopupResponse(context, runtime.keepSceneBackups, {
    pid, process_start_utc: processStartUtc, canonical_project_root: requestedRoot, dry_run: dryRun,
    ...(args.confirm_keep_backups === true ? { confirm_keep_backups: true } : {}),
    ...(typeof args.precondition_hash === "string" ? { precondition_hash: args.precondition_hash } : {}),
  });
  return redactDiagnostics(result.status === "refused" ? { ...result, binding_witness: bindingWitness } : result);
}

function guardedPopupResponse<T, R>(context: DispatcherContext, respond: (args: T, beforeInteraction?: () => void) => R, args: T): R {
  return context.beforeKnownUnityPopupResponse ? respond(args, context.beforeKnownUnityPopupResponse) : respond(args);
}

function knownUnityPopupApprovalStatus(context: DispatcherContext): Record<string, unknown> {
  refreshKnownUnityPopupApproval(context);
  return {
    config_key: "approvals.auto_allow_known_unity_popups",
    enabled: context.approvalAutoAllowKnownUnityPopups === true,
    default_enabled: false,
    ...(context.knownUnityPopupSettings ? context.knownUnityPopupSettings.snapshot() : {}),
    supported_command_ids: KNOWN_UNITY_POPUP_RESPONSE_COMMAND_IDS,
    deny_and_feature_guards_preserved: true,
    exact_target_checks_preserved: true,
    automatic_replay_allowed: false,
  };
}

function unityModalStatus(context: DispatcherContext, record?: HeartbeatRecord, projectContext?: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  const status = context.unityWindowDiagnostics?.status() ?? inspectUnityWindowDiagnostics();
  refreshKnownUnityPopupApproval(context);
  const nowMs = context.now?.().getTime() ?? Date.now();
  const capturedAt = context.unityWindowDiagnostics?.cachedStatus?.()?.captured_at ?? new Date(nowMs).toISOString();
  const externalSceneReloadResponseEvidence = [...new Set(status.external_scene_reload_modal_candidates.map((candidate) => candidate.pid))]
    .flatMap((pid) => context.unityWindowDiagnostics?.externalSceneReloadEvidence?.(pid) ?? externalSceneReloadEvidence(status, pid, capturedAt) ?? []);
  const sparseExternalSceneReloadDetected = status.external_scene_reload_modal_candidates
    .some((candidate) => candidate.external_scene_reload_evidence_scope === "sparse_control_topology");
  const requestedProjectRoot = (status.project_downgrade_modal_candidates_count ?? 0) > 0
    ? canonicalProjectRootForDowngrade(record?.canonical_project_root ?? projectContext?.original_project_selector)
    : undefined;
  const processStatus = requestedProjectRoot ? unityProcessRuntime(context).status(context.registry?.listProjectRecords() ?? [], publicProjectRecordOrUndefined(record)) : undefined;
  const matchingProjectProcesses = processStatus?.processes.filter((process) => process.role === "unity_editor" && process.canonical_project_root === requestedProjectRoot) ?? [];
  const projectDowngradeResponseEvidence = matchingProjectProcesses.length === 1
    ? context.unityWindowDiagnostics?.projectDowngradeEvidence?.(matchingProjectProcesses[0]!.pid, requestedProjectRoot!)
    : undefined;
  const projectRecords = context.registry?.listProjectRecords() ?? [];
  const staleProjectRecords = projectRecords
    .filter((project) => project.stale)
    .slice(0, 20);
  const staleProjects = staleProjectRecords.map((project) => staleUnityModalProjectContext(project, projectRecords, status, nowMs));
  const liveRecoveryCandidates = uniquePublicRecords(staleProjectRecords.flatMap((project) => currentCandidateInstances(project, projectRecords))).slice(0, 20);
  const sceneBackupDetected = status.blocking_modal_code === "blocked_scene_backup_recovery"
    && status.scene_backup_recovery_modal_candidates?.length === 1
    && status.scene_backup_recovery_modal_candidates[0]?.scene_backup_recovery_variant === "scene_backup_detected";
  const popupPreapproved = context.approvalAutoAllowKnownUnityPopups === true
    && (["blocked_safe_mode_prompt", "blocked_external_scene_reload", "blocked_project_downgrade_prompt", "blocked_scoped_registry_import"].includes(String(status.blocking_modal_code)) || sceneBackupDetected);

  const recoveryHint = status.blocking_modal_code === "unity_licensing_client_crash_modal"
    ? "Unity Licensing Client native crash dialog is blocking automation. Close the exact dialog manually, keep the problem report snapshot, then retry Unity validation; do not use unity_recovery_prompt.respond because this is not a Unity startup recovery prompt."
    : status.blocking_modal_code === "blocked_corrupted_library"
      ? "Unity Corrupted Library dialog is blocking startup or validation. Close the exact dialog or choose Don't rebuild Library after reviewing the project cache state; do not use recovery-prompt auto response and do not rebuild Library automatically from the agent."
    : status.blocking_modal_code === "blocked_safe_mode_prompt"
      ? "Unity Safe Mode prompt is blocking startup or validation. Only the dedicated dry-run-first diagnostics.unity_safe_mode.enter command can target Enter Safe Mode after exact process/window/control binding and fresh human approval. Ignore, Quit and generic interaction are unsupported."
    : status.blocking_modal_code === "blocked_missing_signature_packages"
      ? "Unity Missing Signature package prompt is blocking PackageManager resolve confirmation or bridge heartbeat recovery. Use diagnostics.unity_recovery_prompt.respond action=close_missing_signature with dry_run:true to verify the exact Missing Signature window and Close button, run the real close only after operator approval, then call sgg_unity_projects and compile.status/package.info again before claiming the resolve is verified."
    : status.blocking_modal_code === "blocked_external_scene_reload"
      ? sparseExternalSceneReloadDetected
        ? "Unity is waiting for an operator decision in an external-scene dialog with sparse evidence. Inspect the exact reported PID/process generation, thread/window identity, and Reload/Ignore controls; no body or scene paths were observed. Do not respond automatically or retry the write command. After the operator explicitly chooses one action, use diagnostics.unity_external_scene_reload.respond with the exact fresh evidence binding, then re-establish a fresh heartbeat and verify compile/scene state with read-only commands."
        : "Unity is waiting for an operator decision about externally modified scene files. Inspect the reported PID, window, body, Assets scene paths, and Reload/Ignore buttons; do not respond automatically or retry the write command. After the operator explicitly chooses one action, use diagnostics.unity_external_scene_reload.respond with the exact fresh evidence binding, then re-establish a fresh heartbeat and verify compile/scene state with read-only commands."
      : status.blocking_modal_code === "blocked_duplicate_project_instance"
        ? "Unity reports that the same project is already open in another Editor instance. The bounded modal evidence is diagnostic-only and exposes no project path or button handle. Inspect Unity processes and project roots, preserve the intended existing Editor, and ask the operator to close the duplicate-project dialog or unintended process. Do not click OK, launch another Editor, terminate a process, delete a lock, or claim recovery automatically."
      : status.blocking_modal_code === "blocked_project_downgrade_prompt"
        ? "Unity requires a destructive project downgrade decision. Review the bounded version and warning evidence; no response is automatic. Continue is available only through the dedicated human-approved one-shot command with the complete current binding. Quit is never automated. A submitted message does not prove closure or downgrade completion."
      : status.blocking_modal_code === "blocked_scoped_registry_import"
        ? "Unity is blocked by the exact Importing a scoped registry dialog. This evidence is diagnostic-only: the Gateway must not click Learn More or Close, focus the window, terminate or restart Unity, or repeat the blocked test, compile, or package operation. After operator resolution, require a fresh heartbeat, catalog, and exact operation status before continuing."
       : status.blocking_modal_code === "blocked_scene_backup_recovery"
         ? sceneBackupDetected
           ? "Unity is blocked by Scene Backup Detected. Use only diagnostics.unity_scene_backup.keep_backups with a fresh exact canonical root/PID/process-start binding, dry-run plan and explicit confirmation after human approval. Delete and legacy Yes/No are unsupported. A submitted message does not prove backups were preserved or recovery; require fresh heartbeat, catalog and exact operation status without replay."
           : "Unity is blocked by the exact Recovering Scene Backups dialog. This evidence is diagnostic-only: the Gateway must not click Yes or No, focus the window, terminate or restart Unity, or repeat the blocked test, compile, or package operation. After operator resolution, require a fresh heartbeat, catalog, and exact operation status before continuing."
      : status.blocking_modal_code === "blocked_low_disk_space"
        ? "Unity is blocked by the exact Low disk space dialog. This evidence is diagnostic-only: the Gateway must not click, focus or close the dialog, free disk space, delete Library/cache files, terminate or restart Unity, or repeat the blocked test, compile, or package operation. After operator resolution, require a fresh heartbeat, catalog, and exact operation status before continuing."
      : status.blocking_progress_code === "blocked_domain_reload_progress"
      ? "Unity appears stuck on a visible Reloading Domain progress window. This is diagnostic-only evidence: do not click Cancel from Gateway, kill Unity, delete Library/cache files, or repeat write/execute commands automatically. Ask the operator to inspect the visible progress window, then retry read/status commands only after a fresh ready heartbeat."
    : !status.native_modal_scan_authoritative
      ? "Native Unity modal inspection was incomplete, so the reported candidate set is not authoritative. Retry diagnostics.unity_modal.status and inspect visible Unity windows manually; do not infer that startup is unblocked."
    : !status.top_level_window_scan_authoritative || !status.aggregate_scan_authoritative
      ? "Top-level Unity window inventory was incomplete even though native modal inspection succeeded. Retry diagnostics.unity_modal.status and inspect visible Unity windows manually; do not infer an aggregate clean or unblocked state."
    : status.blocking_startup_possible || staleProjectRecords.length > 0
      ? liveRecoveryCandidates.length === 1
        ? "Unity recovery modal may be blocking startup; call diagnostics.unity_recovery_prompt.status, use diagnostics.unity_recovery_prompt.respond with dry_run:true to verify the matched window/button, and only run non-dry-run after operator approval. Then call sgg_unity_projects again and use current_candidate_selector only if that fresh instance is still intended."
        : "Unity recovery modal may be blocking startup; call diagnostics.unity_recovery_prompt.status, use diagnostics.unity_recovery_prompt.respond with dry_run:true to verify the matched window/button, and only run non-dry-run after operator approval. Then call sgg_unity_projects again."
      : "No likely Unity recovery or native Unity modal was detected.";

  return redactDiagnostics({
    ...status,
    popup_response_approval: knownUnityPopupApprovalStatus(context),
    external_scene_reload_response_evidence_count: externalSceneReloadResponseEvidence.length,
    external_scene_reload_response_evidence: externalSceneReloadResponseEvidence,
    project_downgrade_response_evidence_count: projectDowngradeResponseEvidence ? 1 : 0,
    project_downgrade_response_evidence: projectDowngradeResponseEvidence ? [projectDowngradeResponseEvidence] : [],
    project_downgrade_project_process_match_count: matchingProjectProcesses.length,
    stale_project_count: staleProjectRecords.length,
    stale_project_context: staleProjects,
    live_recovery_candidates_count: liveRecoveryCandidates.length,
    live_recovery_candidates: liveRecoveryCandidates,
    ...(liveRecoveryCandidates.length === 1 ? {
      current_candidate_instance: liveRecoveryCandidates[0],
      current_candidate_selector: liveRecoveryCandidates[0]?.instance_id,
    } : {}),
    recovery_hint: popupPreapproved
      ? "The owner explicitly enabled approvals.auto_allow_known_unity_popups. The agent may choose the dedicated supported response without another human question, subject to current deny/feature gates, exact project/process/window evidence, required dry-run plans and confirm arguments. This does not authorize unknown dialogs, generic recovery responses, process termination, or replay of a blocked operation. Verify fresh heartbeat and exact operation status after one response; a sent message is not proof of popup closure or recovery."
      : recoveryHint,
    next_valid_actions: [
      ...status.next_valid_actions,
      "call_diagnostics.unity_recovery_prompt.status",
      ...(status.blocking_modal_code === "unity_licensing_client_crash_modal" ? ["operator_close_unity_licensing_client_error_dialog", "call_diagnostics.report_problem"] : []),
      ...(status.blocking_modal_code === "blocked_corrupted_library" ? ["operator_choose_dont_rebuild_library_or_close_dialog", "call_diagnostics.report_problem"] : []),
      ...(status.blocking_modal_code === "blocked_safe_mode_prompt" ? ["dry_run_diagnostics.unity_safe_mode.enter", "ask_gate_exact_enter_safe_mode_only", "do_not_choose_ignore_or_quit_from_gateway", "do_not_auto_click_safe_mode_prompt", "call_diagnostics.report_problem"] : []),
      ...(status.blocking_modal_code === "blocked_missing_signature_packages" ? ["dry_run_diagnostics.unity_recovery_prompt.respond_close_missing_signature", "close_only_the_missing_signature_prompt_after_operator_review", "call_sgg_unity_projects_after_modal_resolution", "call_compile.status_after_heartbeat_recovers", "optionally_call_package.info_after_heartbeat_recovers"] : []),
      ...(status.blocking_modal_code === "blocked_external_scene_reload" ? ["operator_choose_reload_or_ignore_external_scene_changes", "ask_gate_diagnostics.unity_external_scene_reload.respond", "do_not_auto_respond_external_scene_reload_prompt", "do_not_retry_write_command", "call_sgg_unity_projects_after_modal_resolution", "call_compile.status_after_heartbeat_recovers", "call_diagnostics.report_problem"] : []),
      ...(status.blocking_modal_code === "blocked_duplicate_project_instance" ? ["inspect_unity_processes_and_project_roots", "operator_close_duplicate_project_dialog_after_review", "do_not_auto_click_duplicate_project_dialog", "do_not_launch_another_unity_instance", "do_not_terminate_unity_or_delete_project_locks_automatically", "call_sgg_unity_projects_after_modal_resolution", "wait_for_fresh_mcp_heartbeat", "call_diagnostics.report_problem"] : []),
      ...(status.blocking_modal_code === "blocked_project_downgrade_prompt" ? ["review_project_downgrade_warning", "ask_gate_diagnostics.unity_project_downgrade.continue", "do_not_auto_continue_or_quit_project_downgrade", "require_fresh_project_state_after_operator_resolution", "call_diagnostics.report_problem"] : []),
      ...(status.blocking_modal_code === "blocked_scoped_registry_import" ? ["operator_review_scoped_registry_import_dialog", "do_not_click_learn_more_or_close_from_gateway", "do_not_focus_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution", "call_diagnostics.report_problem"] : []),
       ...(status.blocking_modal_code === "blocked_scene_backup_recovery" ? [
         ...(sceneBackupDetected ? ["dry_run_diagnostics.unity_scene_backup.keep_backups", "ask_gate_exact_keep_backups_only", "do_not_select_delete_or_legacy_yes_no"] : ["operator_review_scene_backup_recovery_dialog", "do_not_click_yes_or_no_from_gateway"]),
         "do_not_focus_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution", "call_diagnostics.report_problem",
       ] : []),
      ...(status.blocking_modal_code === "blocked_low_disk_space" ? ["operator_review_low_disk_space_dialog", "do_not_click_focus_or_close_low_disk_dialog_from_gateway", "do_not_free_disk_or_delete_library_or_cache_from_gateway", "do_not_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution", "call_diagnostics.report_problem"] : []),
      ...(status.blocking_progress_code === "blocked_domain_reload_progress" ? ["operator_inspect_visible_unity_progress_window", "wait_briefly_then_recheck_heartbeat", "do_not_click_cancel_from_gateway", "do_not_kill_unity_from_gateway", "do_not_delete_library_or_cache_files", "call_diagnostics.report_problem"] : []),
      ...(status.recovery_prompt_candidates_count > 0 && !["blocked_scene_backup_recovery", "blocked_low_disk_space"].includes(String(status.blocking_modal_code ?? "")) ? ["dry_run_diagnostics.unity_recovery_prompt.respond"] : []),
      ...(staleProjectRecords.length > 0 ? ["inspect_stale_project_unity_pid", "call_sgg_unity_projects_after_modal_resolution"] : []),
      ...(liveRecoveryCandidates.length === 1 ? ["use_current_candidate_instance_id_after_modal_resolution"] : []),
      ...(liveRecoveryCandidates.length > 1 ? ["choose_unique_live_instance_id_after_modal_resolution"] : []),
      ...(popupPreapproved ? ["use_owner_preapproved_dedicated_popup_response", "do_not_click_unsupported_buttons_or_replay_blocked_operations"] : []),
    ].filter((action) => !popupPreapproved || ![
      "operator_choose_reload_or_ignore_external_scene_changes",
      "ask_gate_diagnostics.unity_external_scene_reload.respond",
      "do_not_auto_respond_external_scene_reload_prompt",
       "ask_gate_exact_enter_safe_mode_only",
       "operator_choose_enter_safe_mode_or_ignore_after_review",
      "do_not_auto_click_safe_mode_prompt",
      "ask_gate_diagnostics.unity_project_downgrade.continue",
      "do_not_auto_continue_or_quit_project_downgrade",
       "do_not_click_learn_more_or_close_from_gateway",
       "ask_gate_exact_keep_backups_only",
       "operator_review_scene_backup_recovery_dialog",
    ].includes(action)),
    redaction_applied: true,
  });
}

function unityRecoveryPromptStatus(context: DispatcherContext): Record<string, unknown> {
  const status = unityModalStatus(context);
  return redactDiagnostics({
    ...status,
    command_scope: "unity_recovery_prompt_only",
    supported_actions: ["do_not_restore", "discard_recovery", "close_missing_signature"],
    dry_run_default: true,
    non_dry_run_requires_policy_approval: true,
    arbitrary_modal_close_supported: false,
    safety_contract: "Only Unity windows already classified as recovery prompt candidates or Missing Signature package prompt candidates are eligible; the respond command verifies a matching title plus a narrow button before clicking. For close_missing_signature it also requires Missing Signature body text and clicks only Close, never Open Package Manager or Learn More.",
    next_valid_actions: uniqueStrings([
      ...stringArray(status.next_valid_actions),
      "dry_run_diagnostics.unity_recovery_prompt.respond",
      "call_sgg_unity_projects_after_modal_resolution",
    ]),
    redaction_applied: true,
  });
}

function unityRecoveryPromptRespond(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const respondArgs: UnityRecoveryPromptRespondArgs = {
    ...(typeof args.action === "string" ? { action: args.action } : {}),
    ...(Array.isArray(args.pids) ? { pids: args.pids.filter((pid): pid is number => typeof pid === "number") } : {}),
    ...(typeof args.dry_run === "boolean" ? { dry_run: args.dry_run } : {}),
  };
  return context.unityWindowDiagnostics?.respond?.(respondArgs) ?? respondToUnityRecoveryPrompt(respondArgs);
}

function unityExternalSceneReloadRespond(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined): Record<string, unknown> {
  const respondArgs: UnityExternalSceneReloadRespondArgs = {
    ...(typeof args.action === "string" ? { action: args.action } : {}),
    ...(typeof args.pid === "number" ? { pid: args.pid } : {}),
    ...(typeof args.hwnd === "string" ? { hwnd: args.hwnd } : {}),
    ...(typeof args.evidence_id === "string" ? { evidence_id: args.evidence_id } : {}),
  };
  if (!record || record.unity_pid !== respondArgs.pid) {
    return {
      schema_version: 1,
      action: respondArgs.action === "reload" || respondArgs.action === "ignore" ? respondArgs.action : null,
      status: "refused",
      interaction_attempted: false,
      message_sent: false,
      delivery_state: "not_attempted",
      popup_closed_verified: false,
      compile_state_verified: false,
      target_pid: respondArgs.pid ?? null,
      target_hwnd: respondArgs.hwnd ?? null,
      error: "popup_pid_does_not_match_selected_project_heartbeat",
      next_valid_actions: ["call_sgg_unity_projects", "call_diagnostics.unity_modal.status", "obtain_fresh_external_scene_reload_evidence"],
    };
  }
  if (context.unityWindowDiagnostics?.respondExternalSceneReload) return guardedPopupResponse(context, context.unityWindowDiagnostics.respondExternalSceneReload, respondArgs);
  return respondToUnityExternalSceneReload(respondArgs, { ...(context.beforeKnownUnityPopupResponse ? { beforeInteraction: context.beforeKnownUnityPopupResponse } : {}) });
}

function unityProjectDowngradeContinue(args: Record<string, unknown>, context: DispatcherContext, record: HeartbeatRecord | undefined, projectContext: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  const respondArgs = args as UnityProjectDowngradeContinueArgs;
  const requestedRoot = canonicalProjectRootForDowngrade(respondArgs.canonical_project_root);
  const selectorRoot = record ? canonicalProjectRootForDowngrade(record.canonical_project_root) : canonicalProjectRootForDowngrade(projectContext.original_project_selector);
  const refuse = (error: string): Record<string, unknown> => {
    if (typeof respondArgs.evidence_id === "string") context.unityWindowDiagnostics?.invalidateProjectDowngradeEvidence?.(respondArgs.evidence_id);
    return {
      schema_version: 1,
      status: "refused",
      success: false,
      interaction_attempted: false,
      message_submitted: false,
      delivery_state: "not_attempted",
      popup_closed_verified: false,
      fresh_project_state_verified: false,
      target_pid: respondArgs.pid ?? null,
      target_hwnd: respondArgs.hwnd ?? null,
      error,
      next_valid_actions: ["call_diagnostics.unity_modal.status", "obtain_fresh_project_downgrade_evidence", "require_exact_canonical_project_process_binding"],
    };
  };
  if (!requestedRoot || !selectorRoot || requestedRoot !== respondArgs.canonical_project_root || selectorRoot !== requestedRoot) return refuse("project_downgrade_project_binding_mismatch");
  if (record && (record.unity_pid !== respondArgs.pid || canonicalProjectRootForDowngrade(record.canonical_project_root) !== requestedRoot)) return refuse("project_downgrade_heartbeat_binding_mismatch");
  const processStatus = unityProcessRuntime(context).status(context.registry?.listProjectRecords() ?? [], publicProjectRecordOrUndefined(record));
  const projectProcesses = processStatus.processes.filter((process) => process.role === "unity_editor" && process.canonical_project_root === requestedRoot);
  if (projectProcesses.length !== 1) return refuse(projectProcesses.length === 0 ? "project_downgrade_unity_process_not_found" : "project_downgrade_unity_process_ambiguous");
  const process = projectProcesses[0]!;
  if (process.pid !== respondArgs.pid || (process.process_name !== "Unity" && process.process_name !== "Unity.exe")) return refuse("project_downgrade_process_binding_mismatch");
  if (!context.unityWindowDiagnostics?.continueProjectDowngrade) return refuse("project_downgrade_runtime_evidence_unavailable");
  return guardedPopupResponse(context, context.unityWindowDiagnostics.continueProjectDowngrade, respondArgs);
}

function canonicalProjectRootForDowngrade(value: unknown): string | undefined {
  if (typeof value !== "string" || value.length === 0) return undefined;
  try {
    return canonicalPaths.normalizeProjectRoot(value);
  } catch {
    return undefined;
  }
}

function staleUnityModalProjectContext(project: PublicProjectRecord, allProjects: PublicProjectRecord[], status: UnityWindowDiagnosticsStatus, nowMs: number): Record<string, unknown> {
  const candidates = currentCandidateInstances(project, allProjects).slice(0, 20);
  const activeWindow = status.active_unity_windows.find((window) => window.pid === project.unity_pid) ?? null;
  const nativeModal = status.native_modal_candidates.find((modal) => modal.pid === project.unity_pid) ?? null;
  return {
    project: project.project,
    instance_id: project.instance_id,
    display_name: project.display_name ?? null,
    unity_pid: project.unity_pid,
    status: project.status,
    project_root: project.project_root,
    canonical_project_root: project.canonical_project_root ?? null,
    last_heartbeat: project.last_heartbeat,
    heartbeat_age_ms: Math.max(0, nowMs - Date.parse(project.last_heartbeat)),
    active_window: activeWindow,
    native_modal: nativeModal,
    recovery_prompt_detected_for_pid: activeWindow?.likely_recovery_prompt ?? false,
    blocking_native_modal_detected_for_pid: nativeModal?.block_code ?? null,
    current_candidate_instances: candidates,
    ...(candidates.length === 1 ? {
      current_candidate_instance: candidates[0],
      current_candidate_selector: candidates[0]?.instance_id,
    } : {}),
    ...(candidates.length > 0 ? {
      current_candidate_note: candidates.length === 1
        ? "Use current_candidate_selector only after resolving the modal and confirming the fresh Unity instance is still intended."
        : "Multiple live candidates match this stale project; call sgg_unity_projects and choose a unique instance_id or canonical project root.",
    } : {}),
  };
}

function currentCandidateInstances(staleProject: PublicProjectRecord, allProjects: PublicProjectRecord[]): PublicProjectRecord[] {
  const liveRecords = allProjects.filter((record) => !record.stale && record.instance_id !== staleProject.instance_id);
  const rootMatches = liveRecords.filter((record) => recordsShareCanonicalRoot(staleProject, record));
  return uniquePublicRecords(rootMatches.length > 0 ? rootMatches : liveRecords.filter((record) => recordsShareProjectIdentity(staleProject, record)));
}

function recordsShareProjectIdentity(left: PublicProjectRecord, right: PublicProjectRecord): boolean {
  return left.project === right.project || (left.display_name !== undefined && left.display_name === right.display_name);
}

function recordsShareCanonicalRoot(left: PublicProjectRecord, right: PublicProjectRecord): boolean {
  const leftRoots = canonicalRoots(left);
  if (leftRoots.size === 0) {
    return false;
  }

  for (const root of canonicalRoots(right)) {
    if (leftRoots.has(root)) {
      return true;
    }
  }

  return false;
}

function canonicalRoots(record: PublicProjectRecord): Set<string> {
  const roots = new Set<string>();
  if (record.canonical_project_root) {
    roots.add(record.canonical_project_root);
  }
  for (const alias of record.aliases) {
    roots.add(alias.canonical_path);
  }
  return roots;
}

function uniquePublicRecords(records: PublicProjectRecord[]): PublicProjectRecord[] {
  const unique = new Map<string, PublicProjectRecord>();
  for (const record of records) {
    unique.set(record.instance_id, record);
  }
  return [...unique.values()];
}

function unityCrashReporterClose(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const closeArgs = {
    ...(Array.isArray(args.pids) ? { pids: args.pids.filter((pid): pid is number => typeof pid === "number") } : {}),
    ...(typeof args.dry_run === "boolean" ? { dry_run: args.dry_run } : {}),
    ...(typeof args.force === "boolean" ? { force: args.force } : {}),
  };
  return context.unityCrashReporter?.close(closeArgs) ?? closeUnityCrashReporters(closeArgs);
}

function diagnosticsAuditSummary(context: DispatcherContext): Record<string, unknown> {
  const auditPath = context.auditFilePath ?? auditPathFromDir(context.auditDir);
  const tail = readBoundedTail(auditPath, MAX_SUMMARY_BYTES);
  const lines = tail.text.split(/\r?\n/).filter((line) => line.trim().length > 0).slice(-MAX_SUMMARY_EVENTS);
  const byStatus: Record<string, number> = {};
  const byTool: Record<string, number> = {};
  const byCommand: Record<string, number> = {};
  const byErrorCode: Record<string, number> = {};
  let parsedEvents = 0;
  let completedEvents = 0;
  let startEvents = 0;
  let unknownPhaseEvents = 0;
  let parseErrors = 0;
  let firstTimestamp: string | null = null;
  let lastTimestamp: string | null = null;
  const startedAuditIds = new Set<string>();
  const completedAuditIds = new Set<string>();

  for (const line of lines) {
    try {
      const event = JSON.parse(line) as Record<string, unknown>;
      parsedEvents += 1;
      if (event.phase === "start") {
        startEvents += 1;
        if (typeof event.audit_id === "string") startedAuditIds.add(event.audit_id);
        continue;
      }
      if (!isTerminalAuditEvent(event)) { unknownPhaseEvents += 1; continue; }
      completedEvents += 1;
      if (typeof event.audit_id === "string") completedAuditIds.add(event.audit_id);
      increment(byStatus, event.status);
      increment(byTool, event.tool);
      increment(byCommand, event.command);
      increment(byErrorCode, event.error_code);
      if (typeof event.timestamp === "string") {
        firstTimestamp ??= event.timestamp;
        lastTimestamp = event.timestamp;
      }
    } catch {
      parseErrors += 1;
    }
  }

  return redactDiagnostics({
    audit_path: auditPath,
    exists: tail.exists,
    bytes_read: tail.bytesRead,
    truncated_by_bytes: tail.truncatedByBytes,
    max_events: MAX_SUMMARY_EVENTS,
    events_considered: lines.length,
    parsed_events: parsedEvents,
    completed_events: completedEvents,
    start_events: startEvents,
    unknown_phase_events: unknownPhaseEvents,
    orphan_start_events: [...startedAuditIds].filter((id) => !completedAuditIds.has(id)).length,
    parse_errors: parseErrors,
    first_timestamp: firstTimestamp,
    last_timestamp: lastTimestamp,
    by_status: byStatus,
    by_tool: byTool,
    by_command: byCommand,
    by_error_code: byErrorCode,
    redaction_applied: true,
  });
}

function diagnosticsCommandStatistics(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const maxBytes = clampInteger(typeof args.max_bytes === "number" ? args.max_bytes : DEFAULT_STATISTICS_BYTES, 4096, MAX_STATISTICS_BYTES);
  const auditPath = context.auditFilePath ?? auditPathFromDir(context.auditDir);
  const window = readAuditStatisticsWindow(auditPath, maxBytes);
  const allLines = window.text.split(/\r?\n/).filter((line) => line.trim().length > 0);
  const lines = allLines.slice(-MAX_STATISTICS_EVENTS);
  const events: Array<Record<string, unknown>> = [];
  let parseErrors = 0;
  for (const line of lines) {
    try {
      const parsed = JSON.parse(line) as unknown;
      if (typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)) events.push(parsed as Record<string, unknown>);
      else parseErrors += 1;
    } catch {
      parseErrors += 1;
    }
  }
  return summarizeCommandStatistics(events, {
    ...(typeof args.operation === "string" ? { operation: args.operation } : {}),
    ...(args.outcome === "ok" || args.outcome === "error" ? { outcome: args.outcome } : {}),
    ...(args.response_mode === "full" || args.response_mode === "compact" ? { response_mode: args.response_mode } : {}),
    ...(typeof args.cursor === "number" ? { cursor: args.cursor } : {}),
    ...(typeof args.limit === "number" ? { limit: args.limit } : {}),
    ...(typeof args.recent_failures_limit === "number" ? { recent_failures_limit: args.recent_failures_limit } : {}),
  }, {
    collection_enabled: context.auditEnabled === true || context.audit !== undefined,
    source_exists: window.exists,
    retained_files: window.retainedFiles,
    files_scanned: window.filesScanned,
    bytes_read: window.bytesRead,
    truncated_by_bytes: window.truncatedByBytes || allLines.length > MAX_STATISTICS_EVENTS,
    events_considered: lines.length,
    parse_errors: parseErrors,
  });
}

function readAuditStatisticsWindow(auditPath: string | undefined, maxBytes: number): { exists: boolean; text: string; bytesRead: number; truncatedByBytes: boolean; retainedFiles: number; filesScanned: number } {
  if (!auditPath) return { exists: false, text: "", bytesRead: 0, truncatedByBytes: false, retainedFiles: 0, filesScanned: 0 };
  try {
    const auditDir = path.dirname(auditPath);
    const files = fs.readdirSync(auditDir)
      .filter((name) => name === path.basename(auditPath) || /^audit-(?:corrupt-)?\d{4}-\d{2}-\d{2}-\d+-[0-9a-f-]+\.ndjson$/.test(name))
      .flatMap((name) => {
        const filePath = path.join(auditDir, name);
        const stat = fs.lstatSync(filePath);
        return stat.isFile() && !stat.isSymbolicLink() ? [{ filePath, mtimeMs: stat.mtimeMs }] : [];
      })
      .sort((left, right) => right.mtimeMs - left.mtimeMs || right.filePath.localeCompare(left.filePath));
    const selected: string[] = [];
    let remainingBytes = maxBytes;
    let bytesRead = 0;
    let truncatedByBytes = false;
    for (const file of files) {
      if (remainingBytes <= 0) {
        truncatedByBytes = true;
        break;
      }
      const tail = readBoundedTail(file.filePath, remainingBytes);
      let text = tail.text;
      if (tail.truncatedByBytes) {
        const firstNewline = text.indexOf("\n");
        text = firstNewline >= 0 ? text.slice(firstNewline + 1) : "";
      }
      selected.push(text);
      bytesRead += tail.bytesRead;
      remainingBytes -= tail.bytesRead;
      truncatedByBytes ||= tail.truncatedByBytes;
    }
    const filesScanned = selected.length;
    return {
      exists: files.length > 0,
      text: selected.reverse().join("\n"),
      bytesRead,
      truncatedByBytes: truncatedByBytes || filesScanned < files.length,
      retainedFiles: files.length,
      filesScanned,
    };
  } catch {
    return { exists: false, text: "", bytesRead: 0, truncatedByBytes: false, retainedFiles: 0, filesScanned: 0 };
  }
}

function diagnosticsAuditTail(args: Record<string, unknown>, context: DispatcherContext): Record<string, unknown> {
  const requestedLines = typeof args.lines === "number" ? args.lines : DEFAULT_TAIL_LINES;
  const requestedBytes = typeof args.max_bytes === "number" ? args.max_bytes : DEFAULT_TAIL_BYTES;
  const maxLines = clampInteger(requestedLines, 1, MAX_TAIL_LINES);
  const maxBytes = clampInteger(requestedBytes, 1024, MAX_TAIL_BYTES);
  const auditPath = context.auditFilePath ?? auditPathFromDir(context.auditDir);
  const tail = readBoundedTail(auditPath, maxBytes);
  const allLines = tail.text.split(/\r?\n/).filter((line) => line.length > 0);
  const selected = allLines.slice(-maxLines).map((line) => redactText(line));
  return {
    audit_path: auditPath,
    exists: tail.exists,
    lines: selected,
    line_count: selected.length,
    max_lines: maxLines,
    bytes_read: tail.bytesRead,
    max_bytes: maxBytes,
    truncated_by_bytes: tail.truncatedByBytes,
    redaction_applied: true,
  };
}

function unityRelayStatus(context: DispatcherContext): Record<string, unknown> {
  const envPath = process.env.UNITY_MCP_RELAY_PATH?.trim();
  const homeRelayRoot = path.join(os.homedir(), ".unity", "relay");
  const probes = [
    relayProbe("env:UNITY_MCP_RELAY_PATH", envPath),
    relayProbe("home:.unity/relay", homeRelayRoot),
  ];
  const configuredProbe = probes[0];
  const existingProbes = probes.filter((probe) => probe.exists);
  const availability = configuredProbe?.configured === true
    ? configuredProbe.exists === true ? "configured_candidate_found" : "configured_missing"
    : existingProbes.length > 0 ? "candidate_root_found" : "not_configured";

  return redactDiagnostics({
    availability,
    baseline_dependency: false,
    diagnostic_only: true,
    launch_attempted: false,
    adapter_enabled: context.officialRelay?.enabled === true,
    adapter: context.officialRelay?.currentStatus() ?? { enabled: false, diagnostic_only: true },
    official_relay_required: false,
    local_sggmcp_commands_remain_available: true,
    approval_required_outside_mcp: true,
    configured_env_var: "UNITY_MCP_RELAY_PATH",
    probes,
    notes: [
      "Official Unity relay support is optional interoperability, not the local SGGMcp baseline.",
      "Missing, paywalled, or approval-blocked relay access should remain diagnostic-only.",
    ],
    redaction_applied: true,
  });
}

function readProblemReportEntries(reportDir: string): Array<{ fileName: string; reportId: string; stat: fs.Stats }> {
  if (!fs.existsSync(reportDir)) {
    return [];
  }

  const entries: Array<{ fileName: string; reportId: string; stat: fs.Stats }> = [];
  let fileNames: string[];
  try {
    fileNames = fs.readdirSync(reportDir);
  } catch {
    return [];
  }
  for (const fileName of fileNames) {
    if (!/^problem_[A-Za-z0-9_-]+\.json$/.test(fileName)) {
      continue;
    }
    const reportPath = path.join(reportDir, fileName);
    const stat = safeStat(reportPath);
    if (stat?.isFile()) {
      entries.push({ fileName, reportId: fileName.slice(0, -".json".length), stat });
    }
  }

  entries.sort((left, right) => right.stat.mtimeMs - left.stat.mtimeMs || right.fileName.localeCompare(left.fileName));
  return entries;
}

function readProblemReports(reportDir: string, decisions: Record<string, TriageDecision>): ProblemReportRecord[] {
  return readProblemReportEntries(reportDir).map((entry) => {
    const { parsed, parseError } = parseProblemReport(reportDir, entry.fileName, entry.stat);
    return {
      report_id: entry.reportId,
      file_name: entry.fileName,
      stat: entry.stat,
      summary: problemReportSummaryFromParsed(entry, parsed, parseError, decisions[entry.reportId]),
      ...(parsed ? { parsed } : {}),
      ...(parseError ? { parse_error: parseError } : {}),
    };
  });
}

function problemReportSummary(reportDir: string, entry: { fileName: string; reportId: string; stat: fs.Stats }, decisions: Record<string, TriageDecision> = {}): Record<string, unknown> {
  const { parsed, parseError } = parseProblemReport(reportDir, entry.fileName, entry.stat);
  return problemReportSummaryFromParsed(entry, parsed, parseError, decisions[entry.reportId]);
}

function parseProblemReport(reportDir: string, fileName: string, stat: fs.Stats): { parsed?: Record<string, unknown>; parseError?: string } {
  const reportPath = path.join(reportDir, fileName);
  let parsed: Record<string, unknown> | undefined;
  let parseError: string | undefined;
  if (stat.size <= PROBLEM_REPORT_MAX_BYTES) {
    try {
      parsed = JSON.parse(fs.readFileSync(reportPath, "utf8")) as Record<string, unknown>;
    } catch (error) {
      parseError = error instanceof Error ? error.message : "parse_failed";
    }
  }

  return { ...(parsed ? { parsed } : {}), ...(parseError ? { parseError } : {}) };
}

function problemReportSummaryFromParsed(entry: { fileName: string; reportId: string; stat: fs.Stats }, parsed: Record<string, unknown> | undefined, parseError: string | undefined, decision: TriageDecision | undefined): Record<string, unknown> {
  return {
    report_id: entry.reportId,
    artifact_ref: `gateway://problem-reports/${entry.fileName}`,
    created_utc: stringOrNull(parsed?.created_utc),
    project: stringOrNull(parsed?.reported_project_selector) ?? stringOrNull(parsed?.project),
    severity: stringOrNull(parsed?.severity),
    category: stringOrNull(parsed?.category),
    summary: stringOrNull(parsed?.summary),
    bytes: entry.stat.size,
    last_write_utc: entry.stat.mtime.toISOString(),
    oversized: entry.stat.size > PROBLEM_REPORT_MAX_BYTES,
    triage_status: decision?.status ?? "untriaged",
    root_cause: decision?.root_cause ?? inferredRootCause(parsed),
    decision: decision?.decision ?? null,
    regression_status: decision?.regression_status ?? null,
    private_backlog_ref_present: decision?.private_backlog_ref ? true : false,
    triage_updated_utc: decision?.updated_utc ?? null,
    ...(parseError ? { parse_error: parseError } : {}),
  };
}

function triageGroups(records: ProblemReportRecord[], decisions: Record<string, TriageDecision>): Array<Record<string, unknown> & { count: number; active_count: number; latest_created_utc: string | null }> {
  const groups = new Map<string, {
    command: string | null;
    error_code: string | null;
    category: string | null;
    root_cause: string;
    reports: ProblemReportRecord[];
  }>();

  for (const record of records) {
    const command = reportCommand(record.parsed);
    const errorCode = reportErrorCode(record.parsed);
    const category = stringOrNull(record.parsed?.category);
    const rootCause = stringOrNull(record.summary.root_cause) ?? "unknown";
    const key = `${command ?? "unknown"}\n${errorCode ?? "unknown"}\n${rootCause}`;
    const group = groups.get(key) ?? { command, error_code: errorCode, category, root_cause: rootCause, reports: [] };
    group.reports.push(record);
    groups.set(key, group);
  }

  return [...groups.values()].map((group) => {
    const statuses = group.reports.map((record) => stringOrNull(record.summary.triage_status) ?? "untriaged");
    const latest = group.reports[0];
    const activeCount = statuses.filter((status) => status === "active" || status === "untriaged").length;
    const representative = group.reports.find((record) => record.summary.triage_status !== "closed") ?? latest;
    const decision = representative ? decisions[representative.report_id] : undefined;
    return {
      group_id: triageGroupId(group.command, group.error_code, group.root_cause),
      command: group.command,
      error_code: group.error_code,
      category: group.category,
      root_cause: group.root_cause,
      count: group.reports.length,
      active_count: activeCount,
      deferred_count: statuses.filter((status) => status === "deferred").length,
      closed_count: statuses.filter((status) => status === "closed").length,
      untriaged_count: statuses.filter((status) => status === "untriaged").length,
      latest_report_id: latest?.report_id ?? null,
      latest_created_utc: stringOrNull(latest?.summary.created_utc),
      report_ids: group.reports.map((record) => record.report_id).slice(0, MAX_TRIAGE_REPORT_IDS),
      severity_counts: countBy(group.reports.map((record) => stringOrNull(record.summary.severity) ?? "unknown")),
      regression_status: decision?.regression_status ?? null,
      private_backlog_ref_present: decision?.private_backlog_ref ? true : false,
      recommended_action: triageRecommendedAction(activeCount, decision),
      next_valid_actions: triageNextActions(activeCount, decision),
    };
  });
}

function readTriageDecisions(reportDir: string): Record<string, TriageDecision> {
  const decisionPath = triageDecisionPath(reportDir);
  const stat = safeStat(decisionPath);
  if (!stat?.isFile() || stat.size > PROBLEM_REPORT_MAX_BYTES) {
    return {};
  }

  try {
    const parsed = JSON.parse(fs.readFileSync(decisionPath, "utf8")) as Record<string, unknown>;
    const rawDecisions = isRecord(parsed.decisions) ? parsed.decisions : {};
    const decisions: Record<string, TriageDecision> = {};
    for (const [reportId, value] of Object.entries(rawDecisions)) {
      if (/^problem_[A-Za-z0-9_-]+$/.test(reportId) && isRecord(value)) {
        const decision = parseTriageDecision(value);
        if (decision) {
          decisions[reportId] = decision;
        }
      }
    }
    return decisions;
  } catch {
    return {};
  }
}

function writeTriageDecisions(reportDir: string, decisions: Record<string, TriageDecision>): void {
  fs.mkdirSync(reportDir, { recursive: true });
  const decisionPath = triageDecisionPath(reportDir);
  const tmpPath = path.join(reportDir, `.${TRIAGE_DECISIONS_FILE}.${process.pid}.tmp`);
  fs.writeFileSync(tmpPath, `${JSON.stringify({ schema_version: TRIAGE_DECISION_SCHEMA_VERSION, decisions }, null, 2)}\n`, "utf8");
  fs.renameSync(tmpPath, decisionPath);
}

function parseTriageDecision(value: Record<string, unknown>): TriageDecision | undefined {
  const status = stringOrNull(value.status);
  const updated = stringOrNull(value.updated_utc);
  if (!isTriageStatus(status) || !updated) {
    return undefined;
  }
  return {
    status,
    ...(stringOrNull(value.root_cause) ? { root_cause: stringOrNull(value.root_cause)! } : {}),
    ...(stringOrNull(value.decision) ? { decision: stringOrNull(value.decision)! } : {}),
    ...(isRegressionStatus(stringOrNull(value.regression_status)) ? { regression_status: stringOrNull(value.regression_status) as RegressionStatus } : {}),
    ...(stringOrNull(value.defer_reason) ? { defer_reason: stringOrNull(value.defer_reason)! } : {}),
    ...(stringOrNull(value.private_backlog_ref) ? { private_backlog_ref: stringOrNull(value.private_backlog_ref)! } : {}),
    ...(stringArray(value.tags).length > 0 ? { tags: stringArray(value.tags).slice(0, 12) } : {}),
    updated_utc: updated,
  };
}

function sanitizeTriageDecision(args: Record<string, unknown>, status: TriageStatus, now: Date): TriageDecision {
  const regression = stringOrNull(args.regression_status);
  return {
    status,
    ...(stringOrNull(args.root_cause) ? { root_cause: stringOrNull(args.root_cause)!.slice(0, 160) } : {}),
    ...(stringOrNull(args.decision) ? { decision: stringOrNull(args.decision)!.slice(0, 1000) } : {}),
    ...(isRegressionStatus(regression) ? { regression_status: regression } : { regression_status: "unknown" }),
    ...(stringOrNull(args.defer_reason) ? { defer_reason: stringOrNull(args.defer_reason)!.slice(0, 500) } : {}),
    ...(stringOrNull(args.private_backlog_ref) ? { private_backlog_ref: stringOrNull(args.private_backlog_ref)!.slice(0, 120) } : {}),
    ...(stringArray(args.tags).length > 0 ? { tags: stringArray(args.tags).slice(0, 12) } : {}),
    updated_utc: now.toISOString(),
  };
}

function inferredRootCause(parsed: Record<string, unknown> | undefined): string | null {
  const reportedError = isRecord(parsed?.reported_last_error) ? parsed.reported_last_error : undefined;
  const details = isRecord(reportedError?.last_error_details) ? reportedError.last_error_details : undefined;
  const failureSummary = isRecord(details?.failure_summary) ? details.failure_summary : undefined;
  const snapshot = isRecord(parsed?.snapshot) ? parsed.snapshot : undefined;
  const lifecycle = isRecord(snapshot?.lifecycle) ? snapshot.lifecycle : undefined;
  const errorCode = reportErrorCode(parsed);
  if (stringOrNull(failureSummary?.root_cause)) {
    return stringOrNull(failureSummary?.root_cause);
  }
  if (errorCode === "compilation_failed") {
    return "compile_errors";
  }
  if (errorCode === "permission_denied") {
    return "policy_block";
  }
  if (errorCode === "confirmation_required") {
    return "approval_required";
  }
  if (errorCode === "project_stale" || lifecycle?.stale === true) {
    return "stale_heartbeat";
  }
  if (errorCode === "bridge_unreachable" || errorCode === "bridge_timeout" || errorCode === "command_timeout") {
    return "bridge_or_unity_blocked";
  }
  return stringOrNull(parsed?.category) ?? errorCode ?? null;
}

function reportCommand(parsed: Record<string, unknown> | undefined): string | null {
  const reportedError = isRecord(parsed?.reported_last_error) ? parsed.reported_last_error : undefined;
  return stringOrNull(reportedError?.last_command);
}

function reportErrorCode(parsed: Record<string, unknown> | undefined): string | null {
  const reportedError = isRecord(parsed?.reported_last_error) ? parsed.reported_last_error : undefined;
  return stringOrNull(reportedError?.last_error_code);
}

function triageRecommendedAction(activeCount: number, decision: TriageDecision | undefined): string {
  if (activeCount === 0) {
    return "closed_or_deferred_no_action";
  }
  if (!decision) {
    return activeCount > 1 ? "mark_recurring_with_regression_decision" : "inspect_and_mark_triage_status";
  }
  if (decision.status === "deferred" && !decision.defer_reason) {
    return "add_defer_reason";
  }
  if (decision.status !== "closed" && (decision.regression_status === undefined || decision.regression_status === "unknown")) {
    return "add_regression_test_eval_or_defer_reason";
  }
  return "continue_tracking_active_reports";
}

function triageNextActions(activeCount: number, decision: TriageDecision | undefined): string[] {
  return uniqueStrings([
    "diagnostics.problem.read",
    ...(activeCount > 0 ? ["diagnostics.problem.mark"] : []),
    ...(!decision || decision.regression_status === "unknown" ? ["add_regression_test_or_eval_or_defer_reason"] : []),
    "diagnostics.recovery.suggest",
  ]);
}

function regressionGuardrail(decision: TriageDecision): string {
  if (decision.status === "closed" && (decision.regression_status === "test_added" || decision.regression_status === "eval_added" || decision.regression_status === "not_needed")) {
    return "closed_with_regression_decision";
  }
  if (decision.status === "deferred" && decision.defer_reason) {
    return "deferred_with_reason";
  }
  return "needs_regression_test_eval_or_defer_reason_before_closing_recurring_issue";
}

function publicTriageDecision(decision: TriageDecision): Record<string, unknown> {
  const { private_backlog_ref: _privateBacklogRef, ...publicDecision } = decision;
  return { ...publicDecision, private_backlog_ref_present: decision.private_backlog_ref ? true : false };
}

function triageGroupId(command: string | null, errorCode: string | null, rootCause: string): string {
  return createHash("sha256").update(`${command ?? "unknown"}\n${errorCode ?? "unknown"}\n${rootCause}`).digest("hex").slice(0, 12);
}

function countBy(values: string[]): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const value of values) {
    counts[value] = (counts[value] ?? 0) + 1;
  }
  return counts;
}

function isTriageStatus(value: unknown): value is TriageStatus {
  return value === "active" || value === "deferred" || value === "closed";
}

function isRegressionStatus(value: unknown): value is RegressionStatus {
  return value === "unknown" || value === "test_added" || value === "eval_added" || value === "deferred_with_reason" || value === "not_needed";
}

function triageDecisionPath(reportDir: string): string {
  return path.join(reportDir, TRIAGE_DECISIONS_FILE);
}

function readBoundedFileStart(filePath: string, maxChars: number): { text: string; bytesRead: number; truncated: boolean } {
  const stat = fs.statSync(filePath);
  const maxBytes = Math.min(stat.size, PROBLEM_REPORT_MAX_BYTES, Math.max(0, maxChars * 4));
  const fd = fs.openSync(filePath, "r");
  try {
    const buffer = Buffer.alloc(maxBytes);
    const bytesRead = fs.readSync(fd, buffer, 0, maxBytes, 0);
    const text = buffer.subarray(0, bytesRead).toString("utf8");
    return { text: text.slice(0, maxChars), bytesRead, truncated: stat.size > bytesRead || text.length > maxChars };
  } finally {
    fs.closeSync(fd);
  }
}

function parseDetailsJson(value: string): { value: unknown; parseError: string | null } {
  if (!value) {
    return { value: null, parseError: null };
  }
  try {
    return { value: JSON.parse(value), parseError: null };
  } catch (error) {
    return { value: null, parseError: error instanceof Error ? error.message : "parse_failed" };
  }
}

function recoveryActions(errorCode: string, command: string, details: { value: unknown }, suggestedCommand: string | undefined, projectSelector?: string, resolvedProjectId?: string): RecoveryAction[] {
  const actions: RecoveryAction[] = [];
  const add = (action: string, description: string, execution?: Omit<RecoveryAction, "action" | "description">) => {
    if (!actions.some((candidate) => candidate.action === action)) {
      actions.push({ action, description, ...execution });
    }
  };

  if (suggestedCommand) {
    add("retry_with_suggested_command", `Use '${suggestedCommand}' instead of obsolete command '${command}'.`);
    add("call_sgg_unity_describe_command", `Describe command:${suggestedCommand} before calling it.`);
  }

  const detailsObject = isRecord(details.value) ? details.value : {};
  if (errorCode === "permission_denied" || detailsObject.blocked_by === "gateway_policy") {
    add("do_not_bypass_policy", "This is a Gateway policy block, not a Unity command bug.");
    add("call_policy.explain", "Use policy.explain to see why the command is hidden, ask-gated, or denied.");
    add("ask_user_to_update_policy", "Ask the user to adjust project policy only if this command is intended.");
    return actions;
  }

  if (errorCode === "command_not_found") {
    add("call_sgg_unity_describe_selector_commands", "Call sgg_unity_describe with selector=\"commands\" to refresh and inspect the live command catalog for the selected project.");
    add("check_feature_flags_and_package_install", "The command may be feature-gated, hidden by policy, or absent from the installed Unity package.");
    return actions;
  }

  if (errorCode === "unity_licensing_client_crash_modal") {
    add("call_diagnostics.unity_modal.status", "Inspect the exact native Unity Licensing Client crash dialog and keep its snapshot for the problem report.");
    add("operator_close_unity_licensing_client_error_dialog", "Close only the matched Unity.Licensing.Client.exe application error dialog manually; do not use recovery-prompt auto response.");
    add("retry_after_modal_closed", "Retry Unity validation only after the native crash dialog is gone.");
    add("call_diagnostics.report_problem", "Record the blocking modal snapshot if the failure recurs.");
    return actions;
  }

  if (errorCode === "blocked_corrupted_library") {
    add("call_diagnostics.unity_modal.status", "Inspect the exact Corrupted Library native dialog and keep its snapshot for the problem report.");
    add("operator_choose_dont_rebuild_library_or_close_dialog", "Close the matched dialog or choose Don't rebuild Library only after reviewing the project cache state; do not let the agent click Rebuild Library automatically.");
    add("retry_after_modal_closed", "Retry Unity validation only after the Corrupted Library dialog is gone and the project cache state is understood.");
    add("call_diagnostics.report_problem", "Record the blocking modal snapshot if the failure recurs.");
    return actions;
  }

  if (errorCode === "blocked_safe_mode_prompt") {
    add("call_diagnostics.unity_modal.status", "Inspect the exact Enter Safe Mode native dialog and keep its snapshot for the problem report.");
    add("operator_choose_enter_safe_mode_or_ignore_after_review", "Ask the operator to decide whether to enter Safe Mode, ignore, or close Unity after reviewing compile/cache state.");
    add("do_not_auto_click_safe_mode_prompt", "Do not use recovery-prompt auto response for Safe Mode; this prompt is not a Don't Restore/Discard recovery modal.");
    add("retry_after_modal_closed", "Retry Unity validation only after the Safe Mode prompt is gone and the selected editor state is understood.");
    add("call_diagnostics.report_problem", "Record the blocking modal snapshot if the failure recurs.");
    return actions;
  }

  if (errorCode === "blocked_external_scene_reload") {
    add("call_diagnostics.unity_modal.status", "Inspect the exact Unity external scene reload modal, including PID, window handle/title, required body text, Assets scene paths, and Reload/Ignore buttons.");
    add("operator_choose_reload_or_ignore_external_scene_changes", "Ask the operator to choose Reload or Ignore after reviewing whether disk or in-memory scene changes should win.");
    add("ask_gate_diagnostics.unity_external_scene_reload.respond", "Only after the operator explicitly selects Reload or Ignore, submit the exact fresh evidence binding to diagnostics.unity_external_scene_reload.respond; permissive policy and auto_allow_ask cannot bypass this one-shot approval.");
    add("do_not_auto_respond_external_scene_reload_prompt", "Do not choose an action for the operator, reuse an approval, switch actions, or persist an automatic preference.");
    add("do_not_retry_write_command", "Do not repeat the write command that preceded this modal; resolve the scene state and verify with read-only status commands.");
    add("call_sgg_unity_projects_after_modal_resolution", "After the operator resolves the modal, require a fresh ready heartbeat before continuing.");
    add("call_diagnostics.report_problem", "Record bounded modal and heartbeat evidence if the blocker recurs.");
    return actions;
  }

  if (errorCode === "blocked_duplicate_project_instance") {
    add("call_diagnostics.unity_modal.status", "Inspect the strict Unity-owned Error! modal evidence, exact duplicate-project clauses, process generation, project-identity hash, and one-button topology without exposing the project path or button handle.");
    add("inspect_unity_processes_and_project_roots", "Use unity.process.list and unity.project.open_status to identify the intended already-open Editor and any unintended duplicate process without relying on heartbeat registration.");
    add("operator_close_duplicate_project_dialog_after_review", "Ask the operator to close the exact duplicate-project dialog or unintended Editor after reviewing project ownership.");
    add("do_not_auto_click_duplicate_project_dialog", "No Gateway response command exists for this modal; do not click OK or infer closure from a submitted window message.");
    add("do_not_terminate_unity_or_delete_project_locks_automatically", "Do not terminate Unity, delete project locks, or launch another Editor automatically.");
    add("wait_for_fresh_mcp_heartbeat", "After operator resolution, rediscover projects and require a fresh heartbeat before continuing.");
    add("call_diagnostics.report_problem", "Record only bounded modal identity and hashes if the blocker recurs; do not include raw project paths.");
    return actions;
  }

  if (errorCode === "blocked_project_downgrade_prompt") {
    add("call_diagnostics.unity_modal.status", "Inspect the bounded exact-title downgrade evidence, ordered source/target Unity versions, process identity, and exact Continue/Quit control count.");
    add("review_project_downgrade_warning", "Require the operator to review that downgrade is unsupported, Continue downgrades, assets reimport, and packages/settings may also be downgraded.");
    add("ask_gate_diagnostics.unity_project_downgrade.continue", "Only after explicit human approval, submit the complete fresh one-shot binding to diagnostics.unity_project_downgrade.continue; permissive policy and auto_allow_ask cannot bypass approval.");
    add("do_not_auto_continue_or_quit", "Never choose Continue automatically and never expose an automated Quit action.");
    add("require_fresh_popup_process_project_package_compile_state", "A submitted button message does not prove popup closure or downgrade completion; freshly scan the popup, rediscover the project/process, inspect package operation resolution status, and verify compile.status after a fresh heartbeat before any recovery claim.");
    add("call_diagnostics.report_problem", "Record only bounded modal identity and hash evidence if the blocker recurs; do not include raw body text, paths, or tokens.");
    return actions;
  }

  if (errorCode === "blocked_domain_reload_progress") {
    add("call_diagnostics.unity_modal.status", "Inspect the exact visible Unity Reloading Domain progress window, including PID, title/body text, busy duration, and heartbeat correlation.");
    add("operator_inspect_visible_unity_progress_window", "Ask the operator to inspect Unity directly; long domain reloads may be legitimate on large projects, but this is blocker evidence for agents.");
    add("wait_briefly_then_recheck_heartbeat", "Wait briefly and call sgg_unity_projects or compile.status again; retry only read/status commands after a fresh ready heartbeat.");
    add("do_not_click_cancel_from_gateway", "Do not click the progress window Cancel button from Gateway automation.");
    add("do_not_kill_unity_from_gateway", "Do not kill Unity automatically for a domain reload progress stall; require operator decision.");
    add("do_not_delete_library_or_cache_files", "Do not delete Library/cache files automatically as recovery.");
    add("call_diagnostics.report_problem", "Record bounded progress-window and heartbeat evidence if the stall recurs.");
    return actions;
  }

  if (errorCode === "unity_startup_stalled_before_bridge") {
    add("inspect_editor_log_tail", "Inspect the bounded Editor.log tail for startup phase evidence such as Library Redirect Path, LibraryInitializing, ArtifactDB/SourceAssetDB locks, licensing lines, or bug reporter exit lines.");
    add("inspect_unity_processes_for_project_path", "Check whether a Unity process still owns the target project path outside the heartbeat registry.");
    add("do_not_delete_library_automatically", "Do not delete Library or lock files automatically; require exact process/project evidence and operator approval before cleanup.");
    add("close_stuck_validation_owned_unity_process", "Terminate only the validation-owned Unity process if it is still running and no interactive editor state is at risk.");
    add("retry_after_startup_state_is_clean", "Retry validation only after the Unity startup state is clean or a separate clone/worktree is selected.");
    add("call_diagnostics.report_problem", "Record the startup stall evidence if the failure recurs.");
    return actions;
  }

  if (errorCode === "same_project_editor_open" || errorCode === "unity_project_already_open" || errorCode === "unity_same_project_open") {
    add("call_sgg_unity_projects", "Verify which live Unity Editor instance owns the target canonical project root.");
    add("use_open_editor_test_run_flow", "Use the in-editor test.run/test.run.status/test.run.cancel flow instead of starting batchmode over the already-open project.");
    add("close_interactive_unity_editor_before_batchmode", "Close the interactive Unity Editor for that project before retrying any batchmode fallback or validation launch.");
    add("use_separate_clone_for_batchmode", "If batchmode is required while work continues in Editor, run it against a separate clone or worktree with a different Library.");
    add("call_diagnostics.report_problem", "Record a problem report if the Gateway suggested a batchmode launch without this preflight warning.");
    return actions;
  }

  if (errorCode === "command_timeout" || errorCode === "bridge_timeout" || errorCode === "bridge_unreachable" || errorCode === "project_stale" || errorCode === "project_offline" || errorCode === "project_not_found") {
    add("call_sgg_unity_projects", "Verify the current Unity bridge heartbeat and project status.");
    add("call_diagnostics.unity_modal.status", "Check whether a Unity recovery, licensing, save, or modal prompt is blocking startup or heartbeat updates.");
    add("call_diagnostics.unity_recovery_prompt.status", "If a startup recovery prompt is suspected, inspect the narrow recovery-prompt status before any response.");
    add("dry_run_diagnostics.unity_recovery_prompt.respond", "Only after operator approval, dry-run the do_not_restore/discard_recovery response to verify the exact Unity window and button.");
    add("check_scene_save_prompt", "Check for the Unity 'Scene(s) Have Been Modified' prompt with editor.save_prompt.get.");
    add("cancel_scene_save_prompt_if_needed", "If that save prompt is open and no save/discard is intended, close it with editor.save_prompt.respond action=cancel.");
    add("inspect_unity_freeze", "Verify Unity is not blocked by another modal dialog or a main-thread freeze.");
    add("retry_after_short_delay", "Retry the read/status command after the heartbeat recovers.");
    return actions;
  }

  if (errorCode === "bridge_protocol_error") {
    add("call_sgg_unity_projects", "Call the top-level MCP tool sgg_unity_projects to verify the current Unity bridge heartbeat and choose the intended project selector.");
    add("call_compile.status_with_sgg_unity_call", "Use sgg_unity_call with command=compile.status and args_json={} on that project to verify the bridge invoke path before repeating the original command.");
    add("inspect_bridge_protocol_error_details", "Inspect failure_kind, http_error_kind, parse_error_kind, bounded response metadata, endpoint, and recovery_contract in the returned error.details; response bodies are never exposed.");
    add("retry_original_command_only_if_still_intended", "Repeat the original command only after the bridge returns valid JSON and the operation is still intended.");
    return actions;
  }

  if (errorCode === "compilation_failed" || command === "compile.wait_ready") {
    add("call_compile.status", "Read compile_diagnostics and confirm whether compiler errors remain.");
    add("call_compile.errors.recent", "Read bounded recent compiler errors captured from CompilationPipeline and console error logs.");
    const { action, description, ...execution } = executableConsoleErrorRecoveryAction(projectSelector, resolvedProjectId);
    add(action, description, execution);
    add("fix_compile_errors_then_retry", "Fix reported compiler errors before running tests or write/execute commands.");
    add("call_compile.refresh_and_wait", "After fixing source files outside Unity, refresh assets and wait for strict compile readiness.");
    add("call_validation.after_edit", "For post-edit validation, refresh assets and return the strict validation status wrapper.");
    return actions;
  }

  if (errorCode === "unity_busy") {
    add("call_editor.status", "Inspect editor state, dirty scenes, play mode, compile, and update status.");
    add("check_scene_save_prompt", "Check for dirty scene save-prompt risk before retrying.");
    add("retry_after_unity_ready", "Retry only after Unity returns to editing/ready status.");
    return actions;
  }

  if (errorCode === "confirmation_required") {
    add("ask_user_for_confirmation", "Ask the user to approve the one-shot request or choose a safer read-only command.");
    add("choose_safer_command", "Prefer dry_run or read-only preflight commands when possible.");
    return actions;
  }

  if (isSchemaRecoveryErrorCode(errorCode)) {
    return buildSafeExecutableSchemaRecovery({ projectSelector, resolvedProjectId, command }).actions.map(recoveryActionFromExecutable);
  }

  if (errorCode === "test_run_not_found") {
    add("call_test.run.status_again", "Poll again after Unity finishes domain reload if the run may still be active.");
    add("read_returned_artifact", "If an artifact_path was returned earlier, read it with job.artifact.read.");
    add("start_new_test_run_if_needed", "Start a new bounded test.run only if the previous operation is no longer intended.");
    return actions;
  }

  if (errorCode === "catalog_stale") {
    add("call_sgg_unity_describe", "Refresh the command catalog before retrying.");
    return actions;
  }

  add("inspect_exact_error", "Inspect error.code, details, project status, command schema, and recent logs before retrying.");
  add("call_diagnostics.report_problem", "If the failure blocks work, write a bounded redacted problem report.");
  return actions;
}

function executableConsoleErrorRecoveryAction(projectSelector?: string, resolvedProjectId?: string): RecoveryAction {
  const originalProject = safePublicProjectSelector(projectSelector);
  const resolvedProject = safePublicProjectSelector(resolvedProjectId);
  const project = originalProject ?? resolvedProject;
  if (!project) {
    return {
      action: "sgg_unity_projects",
      description: "Call sgg_unity_projects, select a safe public project selector, then call logs.console.query with severity=error, limit=20, and no stack traces.",
      action_kind: "mcp_tool",
      tool: "sgg_unity_projects",
      arguments: {},
    };
  }

  return {
    action: "sgg_unity_call",
    description: "Call logs.console.query with severity=error, limit=20, and no stack traces.",
    action_kind: "mcp_tool",
    tool: "sgg_unity_call",
    arguments: {
      project,
      command: "logs.console.query",
      args_json: JSON.stringify({ severity: "error", limit: 20, include_stack_trace: false }),
      mode: "wait",
    },
    selector_source: originalProject ? "original_selector" : "resolved_project_id",
  };
}

function diagnosticsSchemaRecovery(command: string, projectContext: DiagnosticsProjectResolutionContext): SchemaRecoveryPlan {
  return buildSafeExecutableSchemaRecovery({
    projectSelector: trustedOriginalProjectSelector(projectContext),
    resolvedProjectId: trustedResolvedProjectId(projectContext),
    command,
  });
}

function trustedOriginalProjectSelector(context: DiagnosticsProjectResolutionContext): string | undefined {
  return context.project_resolved ? context.original_project_selector : undefined;
}

function trustedResolvedProjectId(context: DiagnosticsProjectResolutionContext): string | undefined {
  return context.project_resolved ? context.resolved_project_id : undefined;
}

function publicDiagnosticsProjectContext(context: DiagnosticsProjectResolutionContext): Record<string, unknown> {
  return {
    project_resolved: context.project_resolved,
    ...(context.project_resolved ? { resolved_project_id: context.resolved_project_id } : {}),
  };
}

function recoveryActionFromExecutable(action: ExecutableMcpAction): RecoveryAction {
  const description = action.tool === "sgg_unity_projects"
    ? "Call the top-level MCP tool sgg_unity_projects with {} and select a safe public project selector."
    : `Call the top-level MCP tool sgg_unity_describe with selector=${String(action.arguments.selector)} and construct a new explicit corrected call from the advertised schema.`;
  return { ...action, description };
}

function commandReplacement(command: string): string | undefined {
  switch (command) {
    case "menu.execute":
    case "editor.execute_menu_item":
      return "editor.menu.execute";
    case "tests.run":
      return "test.run";
    default:
      return undefined;
  }
}

function stringOrEmpty(value: unknown): string {
  return typeof value === "string" ? value : "";
}

function stringOrNull(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function relayProbe(source: string, candidatePath: string | undefined): Record<string, unknown> {
  const configured = typeof candidatePath === "string" && candidatePath.length > 0;
  const stat = configured ? safeStat(candidatePath) : undefined;
  return {
    source,
    configured,
    path: configured ? candidatePath : null,
    exists: stat !== undefined,
    is_file: stat?.isFile() ?? false,
    is_directory: stat?.isDirectory() ?? false,
    executable: stat ? (stat.mode & 0o111) !== 0 : false,
    bytes: stat?.isFile() ? stat.size : 0,
    last_write_utc: stat?.mtime.toISOString() ?? null,
  };
}

function recentAuditEvents(auditPath: string | undefined, maxEvents: number): AuditEvent[] {
  const tail = readBoundedTail(auditPath, MAX_SUMMARY_BYTES);
  if (!tail.exists) {
    return [];
  }

  const events: AuditEvent[] = [];
  const lines = tail.text.split(/\r?\n/).filter((line) => line.trim().length > 0).slice(-maxEvents);
  for (const line of lines) {
    try {
      const parsed = JSON.parse(line) as unknown;
      if (isRecord(parsed)) {
        events.push(parsed as AuditEvent);
      }
    } catch {
      // Ignore malformed legacy audit lines in diagnostics snapshots.
    }
  }
  return events;
}

function lastRelevantAuditEvent(events: AuditEvent[], project: string | undefined): AuditEvent | undefined {
  const fallback = lastAuditEvent(events, (event) => isTerminalAuditEvent(event) && event.command !== "diagnostics.report_problem");
  if (!project) {
    return fallback;
  }
  return lastAuditEvent(events, (event) => isTerminalAuditEvent(event) && event.command !== "diagnostics.report_problem" && event.project === project) ?? fallback;
}

function lastAuditEvent(events: AuditEvent[], predicate: (event: AuditEvent) => boolean): AuditEvent | undefined {
  for (let index = events.length - 1; index >= 0; index -= 1) {
    const event = events[index];
    if (event && predicate(event)) {
      return event;
    }
  }
  return undefined;
}

function auditEventSummary(event: AuditEvent): Record<string, unknown> {
  return {
    timestamp: stringOrNull(event.timestamp),
    phase: event.phase === "start" ? "start" : "terminal",
    run_id: stringOrNull(event.run_id),
    tool: stringOrNull(event.tool),
    status: stringOrNull(event.status),
    duration_ms: typeof event.duration_ms === "number" ? event.duration_ms : null,
    bridge_duration_ms: typeof event.bridge_duration_ms === "number" ? event.bridge_duration_ms : null,
    request_id: stringOrNull(event.request_id),
    project: stringOrNull(event.project),
    command: stringOrNull(event.command),
    error_code: stringOrNull(event.error_code),
    command_risk: stringOrNull(event.command_risk),
    idempotency: stringOrNull(event.idempotency),
    commands_hash: stringOrNull(event.commands_hash),
    package_version: stringOrNull(event.package_version),
    policy_decision: auditPolicyDecision(event),
    artifact_refs: stringArray(event.artifact_refs).slice(0, MAX_PROBLEM_ARTIFACT_REFS),
  };
}

function auditPolicyDecision(event: AuditEvent | undefined): Record<string, unknown> | null {
  if (!event?.policy_action) {
    return null;
  }
  return {
    action: event.policy_action,
    reason: event.policy_reason ?? null,
    pattern: event.policy_pattern ?? null,
    approval_status: event.approval_status ?? null,
    auto_allow_ask: event.auto_allow_ask === true,
    auto_allow_known_unity_popups: event.auto_allow_known_unity_popups === true,
    auto_allow_stale_registry_cleanup_without_elicitation: event.auto_allow_stale_registry_cleanup_without_elicitation === true,
  };
}

function auditErrorSummaries(events: AuditEvent[], maxErrors: number): unknown[] {
  return events
    .filter((event) => isTerminalAuditEvent(event) && event.status === "error")
    .slice(-maxErrors)
    .map((event) => auditEventSummary(event));
}

function recentArtifactRefs(events: AuditEvent[], lastError: ProblemLastError): string[] {
  const refs = [...lastError.artifact_refs];
  for (const event of events) {
    if (!isTerminalAuditEvent(event)) continue;
    if (event.status === "error" || (event.command && ARTIFACT_PRODUCER_PREFIXES.some((prefix) => event.command?.startsWith(prefix)))) {
      refs.push(...stringArray(event.artifact_refs));
    }
  }
  return boundedArtifactRefs(refs);
}

function recentUnityErrorEvidence(events: AuditEvent[], lastError: ProblemLastError): unknown[] {
  const evidence: unknown[] = [];
  if (isUnityErrorContext(lastError.last_command, lastError.last_error_code)) {
    evidence.push({
      source: "reported_last_error",
      command: lastError.last_command,
      error_code: lastError.last_error_code,
      message: lastError.last_error_message,
      details: unityErrorDetailsSummary(lastError.last_error_details),
      artifact_refs: lastError.artifact_refs,
      next_valid_actions: lastError.next_valid_actions,
    });
  }

  for (const event of events) {
    if (isTerminalAuditEvent(event) && event.status === "error" && isUnityErrorContext(event.command, event.error_code)) {
      evidence.push({ source: "audit", ...auditEventSummary(event) });
    }
  }

  return evidence.slice(-MAX_PROBLEM_UNITY_ERRORS);
}

function isUnityErrorContext(command: string | null | undefined, errorCode: string | null | undefined): boolean {
  return (typeof command === "string" && UNITY_ERROR_COMMAND_PREFIXES.some((prefix) => command.startsWith(prefix)))
    || errorCode === "compilation_failed"
    || errorCode === "unity_exception"
    || errorCode === "unity_busy"
    || errorCode === "command_timeout"
    || errorCode === "bridge_timeout";
}

function unityErrorDetailsSummary(details: unknown): unknown {
  if (!isRecord(details)) {
    return null;
  }
  return pickDefined({
    failure_summary: details.failure_summary,
    compile_diagnostics: details.compile_diagnostics,
    compiler_errors: details.compiler_errors,
    compile_error_count: details.compile_error_count,
    failed_tests: details.failed_tests,
    failed: details.failed,
    total: details.total,
    mode: details.mode ?? details.test_mode,
    filter: details.filter ?? details.test_filter,
    dirty_scene_preflight: details.dirty_scene_preflight,
    next_valid_actions: details.next_valid_actions,
  });
}

function bridgeReloadTraceTail(record: ProjectTraceRecord | undefined, stale = false, selectionReason = "live_selector_match"): Record<string, unknown> {
  if (!record) {
    return { exists: false, project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH, reason: "no_resolved_heartbeat" };
  }

  for (const root of projectRootCandidates(record)) {
    const tracePath = path.join(root, BRIDGE_RELOAD_TRACE_RELATIVE_PATH);
    let stat: fs.Stats | undefined;
    try {
      stat = fs.lstatSync(tracePath);
    } catch {
      continue;
    }

    if (stat.isSymbolicLink()) {
      return {
        exists: true,
        project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH,
        readable: false,
        reason: "trace_path_symlink_refused",
        ...(stale ? { stale: true, diagnostic_only: true, selection_reason: selectionReason, command_routing_eligible: false } : {}),
      };
    }
    if (!stat.isFile()) {
      continue;
    }

    const tail = readBoundedTail(tracePath, BRIDGE_RELOAD_TRACE_BYTES);
    const lines = tail.text.split(/\r?\n/).filter((line) => line.length > 0).slice(-BRIDGE_RELOAD_TRACE_LINES).map((line) => redactText(line));
    return {
      exists: true,
      readable: true,
      project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH,
      max_lines: BRIDGE_RELOAD_TRACE_LINES,
      max_bytes: BRIDGE_RELOAD_TRACE_BYTES,
      line_count: lines.length,
      bytes_read: tail.bytesRead,
      truncated_by_bytes: tail.truncatedByBytes,
      lines,
      ...(stale ? { stale: true, diagnostic_only: true, selection_reason: selectionReason, command_routing_eligible: false } : {}),
    };
  }

  return {
    exists: false,
    project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH,
    reason: "trace_not_found",
    ...(stale ? { stale: true, diagnostic_only: true, selection_reason: selectionReason, command_routing_eligible: false } : {}),
  };
}

function noProblemTraceEvidence(selectionReason: string): Record<string, unknown> {
  const staleDiagnostic = selectionReason === "ambiguous_exact_stale_identity" || selectionReason === "incomplete_exact_stale_identity";
  return {
    exists: false,
    project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH,
    reason: selectionReason === "no_live_match" ? "no_resolved_heartbeat" : "no_eligible_diagnostic_evidence",
    ...(selectionReason !== "no_live_match" ? { selection_reason: selectionReason, command_routing_eligible: false } : {}),
    ...(staleDiagnostic ? { stale: true, diagnostic_only: true } : {}),
  };
}

function projectRootCandidates(record: Pick<HeartbeatRecord, "project_root" | "aliases">): string[] {
  return uniqueStrings([
    record.project_root,
    ...record.aliases.map((alias) => alias.host_path),
  ]).filter((candidate) => candidate.length > 0);
}

function artifactRefsFromUnknown(value: unknown): string[] {
  const refs: string[] = [];
  collectArtifactRefs(value, refs);
  return refs;
}

function collectArtifactRefs(value: unknown, refs: string[]): void {
  if (refs.length >= MAX_PROBLEM_ARTIFACT_REFS) {
    return;
  }
  if (Array.isArray(value)) {
    for (const item of value) {
      collectArtifactRefs(item, refs);
    }
    return;
  }
  if (!isRecord(value)) {
    return;
  }
  for (const [key, child] of Object.entries(value)) {
    if ((key === "artifact_ref" || key === "artifact_path") && typeof child === "string") {
      refs.push(child);
      continue;
    }
    if (key === "artifact_refs") {
      refs.push(...stringArray(child));
      continue;
    }
    collectArtifactRefs(child, refs);
  }
}

function boundedArtifactRefs(values: string[]): string[] {
  return uniqueStrings(values.filter((value) => value.length > 0).map((value) => value.slice(0, 512))).slice(0, MAX_PROBLEM_ARTIFACT_REFS);
}

function stringArray(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.length > 0) : [];
}

function uniqueStrings(values: string[]): string[] {
  return [...new Set(values)];
}

function pickDefined(values: Record<string, unknown>): Record<string, unknown> | null {
  const result = Object.fromEntries(Object.entries(values).filter(([, value]) => value !== undefined && value !== null));
  return Object.keys(result).length > 0 ? result : null;
}

function lastAuditErrors(auditPath: string | undefined, maxErrors: number): unknown[] {
  const tail = readBoundedTail(auditPath, 128 * 1024);
  if (!tail.exists) {
    return [];
  }
  const errors: unknown[] = [];
  const lines = tail.text.split(/\r?\n/).filter((line) => line.trim().length > 0);
  for (const line of lines.slice().reverse()) {
    try {
      const event = JSON.parse(line) as Record<string, unknown>;
      if (isTerminalAuditEvent(event) && event.status === "error") {
        errors.push(redactDiagnostics({ timestamp: event.timestamp, tool: event.tool, command: event.command, project: event.project, error_code: event.error_code }));
      }
    } catch {
      // Ignore malformed legacy audit lines in diagnostics.
    }
    if (errors.length >= maxErrors) {
      break;
    }
  }
  return errors.reverse();
}

function isTerminalAuditEvent(event: Record<string, unknown> | AuditEvent): boolean {
  return event.phase === "terminal" || (!Object.prototype.hasOwnProperty.call(event, "phase") && !Object.prototype.hasOwnProperty.call(event, "schema_version"));
}

function readBoundedTail(filePath: string | undefined, maxBytes: number): { exists: boolean; text: string; bytesRead: number; truncatedByBytes: boolean } {
  if (!filePath || !fs.existsSync(filePath)) {
    return { exists: false, text: "", bytesRead: 0, truncatedByBytes: false };
  }
  const stat = fs.statSync(filePath);
  const start = Math.max(0, stat.size - maxBytes);
  const length = stat.size - start;
  const fd = fs.openSync(filePath, "r");
  try {
    const buffer = Buffer.alloc(length);
    fs.readSync(fd, buffer, 0, length, start);
    return { exists: true, text: buffer.toString("utf8"), bytesRead: length, truncatedByBytes: start > 0 };
  } finally {
    fs.closeSync(fd);
  }
}

function redactDiagnostics<T>(value: T): T {
  if (Array.isArray(value)) {
    return value.map((item) => redactDiagnostics(item)) as T;
  }
  if (typeof value === "string") {
    return redactText(value) as T;
  }
  if (typeof value !== "object" || value === null) {
    return value;
  }
  const redacted: Record<string, unknown> = {};
  for (const [key, child] of Object.entries(value)) {
    redacted[key] = isSecretKey(key) ? "[REDACTED]" : redactDiagnostics(child);
  }
  return redacted as T;
}

function redactText(value: string): string {
  return value
    .replace(/(authorization\s*[:=]\s*)(bearer\s+)?[^\s,;}]+/gi, "$1[REDACTED]")
    .replace(/((?:token|api[_-]?key|password|secret|cookie)\s*[:=]\s*)[^\s,;}]+/gi, "$1[REDACTED]")
    .replace(/("(?:token|api[_-]?key|password|secret|cookie|authorization)"\s*:\s*")[^"]*(")/gi, "$1[REDACTED]$2")
    .replace(/\b[A-Za-z]:[\\/][^\s"',;}]+/g, "[REDACTED_PATH]")
    .replace(/(^|[\s"',:])\/(?:mnt\/[A-Za-z]\/|home\/|Users\/|tmp\/|var\/folders\/)[^\s"',;}]+/g, "$1[REDACTED_PATH]");
}

function isSecretKey(key: string): boolean {
  return /token|authorization|api[_-]?key|secret|password|cookie/i.test(key);
}

function increment(target: Record<string, number>, value: unknown): void {
  if (typeof value === "string" && value.length > 0) {
    target[value] = (target[value] ?? 0) + 1;
  }
}

function clampInteger(value: number, min: number, max: number): number {
  if (!Number.isInteger(value)) {
    return min;
  }
  return Math.min(max, Math.max(min, value));
}

function safeStat(filePath: string | undefined): fs.Stats | undefined {
  if (!filePath || !fs.existsSync(filePath)) {
    return undefined;
  }
  return fs.statSync(filePath);
}

function auditPathFromDir(auditDir: string | undefined): string | undefined {
  return auditDir ? path.join(auditDir, "audit.ndjson") : undefined;
}

function problemReportDir(context: DispatcherContext): string {
  if (context.auditDir) {
    return path.join(context.auditDir, "problem-reports");
  }
  return path.join(os.tmpdir(), "sgg-unity-mcp", "problem-reports");
}

function safeReportId(now: Date, project: string, summary: string): string {
  const timestamp = now.toISOString().replace(/[^0-9A-Za-z]+/g, "").slice(0, 15);
  const hash = createHash("sha256").update(`${now.toISOString()}\n${project}\n${summary}\n${process.pid}`).digest("hex").slice(0, 12);
  return `problem_${timestamp}_${hash}`;
}

function emptySchema(): Record<string, unknown> {
  return { type: "object", properties: {}, additionalProperties: false };
}
