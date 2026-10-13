import { createHash, randomUUID } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import { ZodError } from "zod";
import { compactNonPassingEvidence } from "./compact-test-evidence.js";

import { argsHash, canonicalProjectRootHash, type ApprovalPendingGeneration, type ApprovalPendingListEntry, type ApprovalSafeMetadata, type ApprovalStore } from "../approvals/approval-store.js";
import { commandDescriptorRevision, createApprovalBindingEnvelope, effectivePolicyRevision, type ApprovalBindingService } from "../approvals/approval-binding.js";
import { isKnownUnityPopupResponse, KnownPopupPermissionChanged } from "../approvals/known-unity-popups.js";
import { refreshKnownUnityPopupApproval, type KnownUnityPopupSettingsManager } from "../approvals/known-unity-popup-settings.js";
import { BridgeRequestError, BridgeRequestNotIssuedError, type BridgeClient, type BridgeInvokeSettings, type BridgeTransportOptions, type CommandCatalogResponse, type CommandDescriptor, type SingleCommandResponse } from "../bridge/bridge-client.js";
import { commandCatalogRevision, isBoundedJson } from "../bridge/bridge-protocol.js";
import type { PolicyConfig } from "../config/config.js";
import type { UnityCrashReporterRuntime } from "../diagnostics/unity-crash-reporter.js";
import type { UnityProjectOpenRuntime } from "../diagnostics/unity-project-open.js";
import { sameUnityProcessStart, type UnityProcessRuntime } from "../diagnostics/unity-processes.js";
import { externalSceneReloadEvidence, type UnityExternalSceneReloadEvidence, type UnityWindowDiagnosticsRuntime, type UnityWindowDiagnosticsSnapshot, type UnityWindowDiagnosticsStatus } from "../diagnostics/unity-window-diagnostics.js";
import type { InMemoryJobManager } from "../jobs/job-manager.js";
import { createAuditStartEvent, type AuditCommandMetadata, type AuditPolicyMetadata, type AuditSink } from "../observability/audit.js";
import type { OperationalLogger } from "../observability/operational-logger.js";
import { AuditContext, writeInvocationAudit } from "../observability/audit-stage.js";
import { measureCommandStatistics } from "../observability/command-statistics.js";
import { TEST_RUN_ARTIFACT_CAPABILITY, TEST_RUN_ARTIFACT_ROOT, TEST_RUN_ARTIFACT_SCHEMA_VERSION, TestRunOperationStoreError, assertTestRunOperationBinding, assertTestRunTerminalNoExecutionEvidence, testRunCanonicalProjectRootHash, type TestRunOperationBinding, type TestRunOperationStoreLike, type TestRunRequestIssuanceLookup, type TestRunRequestIssuanceMarker, type TestRunTerminalNoExecutionEvidence, type TestRunTerminalNoExecutionReceipt, type TestRunTerminalPackageInventoryEvidence, type TestRunTerminalPackageUpdateEvidence } from "../operations/test-run-operation-store.js";
import type { TestRunOperationPublisher, TestRunOperationPublicationLease } from "../operations/test-run-operation-publisher.js";
import { CanonicalPathService } from "../paths/canonical-path.js";
import { evaluatePolicy, filterVisibleCommands } from "../policy/policy.js";
import { policyManagerSnapshot, type PolicyManager } from "../policy/policy-manager.js";
import { projectRegistrySnapshotForResolution, StaleRegistryOperationError, type HeartbeatRecord, type ProjectRegistry, type ProjectRegistryRecentReplacement, type ProjectRegistrySnapshot, type ProjectResolution, type ProjectResolutionOptions, type PublicProjectRecord, type StaleRegistryCleanupArgs } from "../registry/project-registry.js";
import type { ProjectQueueManager, ProjectQueueOwnerMetadata } from "../routing/project-queue.js";
import { canonicalJsonStringify } from "../security/canonical-json.js";
import { ProjectArtifactReadError, validateBoundLiveTestRunStatusData } from "../security/project-artifact-reader.js";
import { ARTIFACT_COMMANDS, findArtifactCommand, runArtifactCommand } from "./artifact-commands.js";
import { classifyBridgeBootstrapObservation } from "./bridge-bootstrap-observation.js";
import { classifyTestRunCompileReadiness, TEST_RUN_COMPILE_READINESS_CONTRACT } from "./test-run-compile-readiness.js";
import { trustedTestRunStatusObservation, withoutTestRunStatusObservation } from "./test-run-status-observation.js";
import { isValidObservedTestRunDto } from "../operations/test-run-terminal-validation.js";
import { safeCompileReadinessQualification } from "./compile-readiness-qualification.js";
import { CHECKPOINT_COMMANDS, findCheckpointCommand, runCheckpointCommand } from "./checkpoints-commands.js";
import { MAX_POST_ADMISSION_WAIT_MS, MCP_CALLER_BUDGET_MS, MCP_RESPONSE_DELIVERY_RESERVE_MS, TOOL_ARG_SCHEMAS, type ToolName, isCommandGroupSelector, normalizeDescribeSelector, parseArgsJson, parseItemsJson } from "./contracts.js";
import { DIAGNOSTICS_COMMANDS, findDiagnosticsCommand, runDiagnosticsCommand } from "./diagnostics-commands.js";
import { UNITY_DOCS_COMMANDS, findUnityDocsCommand, runUnityDocsCommand } from "./docs-commands.js";
import { EDITOR_MENU_OPERATION_COMMANDS, EDITOR_MENU_OPERATION_CORRELATION_TAG, EDITOR_MENU_OPERATION_START_COMMAND, EDITOR_MENU_OPERATION_STATUS_COMMAND, findEditorMenuOperationCommand, runEditorMenuOperationCommand } from "./editor-menu-operation-commands.js";
import { isCommandFeatureEnabled as isFeatureEnabled } from "./feature-flags.js";
import { OfficialRelayAdapter, officialToolName } from "./official-relay-adapter.js";
import { freezeInvocationRecord, InvocationContextSnapshot } from "./invocation-context.js";
import { executePreparedInvocation, InvocationOutcome } from "./invocation-execution.js";
import { buildInvocationPlan, ReadyInvocationPlan, type PolicyDecision as InvocationPolicyDecision, type ResolvedCommand } from "./invocation-pipeline.js";
import { PIPELINE_COMMANDS, findPipelineCommand, runPipelineCommand } from "./pipeline-commands.js";
import { PLAN_COMMANDS, findPlanCommand, runPlanCommand } from "./plans-commands.js";
import { POLICY_COMMANDS, findPolicyCommand, runPolicyExplain } from "./policy-commands.js";
import { RecoveryContext, isReadRecoveryCommand, selectRecoveryStrategy, type RecoveryHandlers } from "./recovery-strategies.js";
import { fail, isErrorCode, ok, type BatchItemExecution, type ErrorCode, type GatewayError, type GatewayResult } from "./results.js";
import { buildSafeExecutableSchemaRecovery, isSchemaRecoveryErrorCode, safePublicProjectSelector, type SchemaRecoveryErrorCode, type SchemaRecoveryPlan } from "./schema-recovery.js";
import { boundSelectorErrorResult, compactSelectorRecord, dirtySceneSummary, projectSelectorRecords, selectorRecordReference, type SelectorRecordContinuation } from "./selector-response-bounds.js";
import { SKILL_COMMANDS, findSkillCommand, runSkillCommand } from "./skills-commands.js";
import { TEST_RUN_STATUS_FALLBACK_DESCRIPTOR, boundTestRunStatusDescriptor, readBoundTestRunStatus, testRunRequestIssuanceEvidence, testRunStatusOperationId, testRunStatusSelectorMatchesBinding, testRunTerminalNoExecutionEvidence as terminalNoExecutionReceiptEvidence, testRunTerminalNoExecutionNextValidActions, testRunTerminalNoExecutionStatusData } from "./test-run-status-command.js";
import { BatchRoute, InvocationRoute, JobRoute, LogsRoute } from "./tool-routing.js";

const MAX_BATCH_ITEMS = 100;
const MAX_SCHEMA_ISSUES = 10;
const MAX_SCHEMA_ISSUES_COLLECTED = MAX_SCHEMA_ISSUES + 1;
const STALE_CLEANUP_APPROVAL_PROJECT = "gateway-registry";
const STALE_CLEANUP_APPROVAL_COMMAND = "registry.stale.cleanup";
const STALE_CLEANUP_APPROVAL_POLICY = Object.freeze({ action: "ask" as const, reason: "mandatory_stale_registry_cleanup_approval", pattern: STALE_CLEANUP_APPROVAL_COMMAND });
const STALE_CLEANUP_APPROVAL_DESCRIPTOR: CommandDescriptor = Object.freeze({
  id: STALE_CLEANUP_APPROVAL_COMMAND,
  title: "Clean stale registry",
  description: "Remove exact expired stale heartbeat records after final revalidation.",
  package: "sgg-unity-mcp-gateway",
  risk: "destructive",
  idempotency: "unsafe",
  timeout_ms: 30_000,
  schema: { type: "object" },
  source: "builtin",
  package_id: "sgg-unity-mcp-gateway",
  trust: "builtin",
});
const MAX_COMMAND_TIMEOUT_MS = 60 * 60 * 1000;
const COMPILE_REFRESH_AND_WAIT_COMMAND = "compile.refresh_and_wait";
const compileRefreshQueueTimeoutResults = new WeakSet<object>();
const compileRefreshVerificationHandledResults = new WeakSet<object>();
const preDispatchCommandNotFoundResults = new WeakSet<object>();
// Only an actual trusted status response can carry this internal provenance.
// Project-controlled error details cannot forge promotion in the bound flow.
const trustedLiveTestRunMissingResults = new WeakSet<object>();
const trustedTestRunBridgeResponses = new WeakSet<object>();
const trustedEditorQuitBridgeRefusals = new WeakSet<object>();
// A failed selected preflight cannot enter legacy replacement recovery and
// repeat its helper. Public/project-authored lookalike fields confer no brand.
const optedInTestRunPreflightFailures = new WeakSet<object>();
const invalidTestRunObservationResults = new WeakSet<object>();
const PROJECT_QUEUE_EXPIRED = Symbol("project_queue_expired");
const projectResolutionRecentReplacements = new WeakMap<object, HeartbeatRecord>();
const preDispatchRecoveryRecords = new WeakMap<object, HeartbeatRecord>();
const COMPILE_STATUS_COMMAND = "compile.status";
const COMPILE_WAIT_READY_COMMAND = "compile.wait_ready";
const VALIDATION_AFTER_EDIT_COMMAND = "validation.after_edit";
const EDITOR_STATUS_COMMAND = "editor.status";
const EDITOR_QUIT_COMMAND = "editor.quit";
const EDITOR_QUIT_RECONCILIATION_CONTRACT = "sgg.unity_mcp.editor_quit_reconciliation.v1";
const EDITOR_QUIT_DEFAULT_WAIT_MS = 35_000;
const EDITOR_QUIT_POLL_INTERVAL_MS = 250;
const EDITOR_QUIT_OS_POLL_INTERVAL_MS = 2_000;
const EDITOR_QUIT_OS_SCAN_BUDGET_MS = 5_000;
const EDITOR_QUIT_MIN_HELPER_BUDGET_MS = 50;
const EDITOR_QUIT_WINDOW_CLEANUP_GRACE_MS = 250;
const PLAY_MODE_TRANSITION_COMMANDS = new Set(["editor.playmode.enter", "editor.playmode.exit", "editor.playmode.set"]);
const PLAY_MODE_TRANSITION_STABILITY_OVERHEAD_MS = 30_000;
const PLAY_MODE_TRANSITION_RECOVERY_CONTRACT = "sgg.unity_mcp.playmode_transition_recovery.v1";
const UNITY_PROJECT_OPEN_COMMAND = "unity.project.open";
const UNITY_PROJECT_OPEN_STATUS_COMMAND = "unity.project.open_status";
const DEFAULT_COMPILE_WAIT_READY_TIMEOUT_MS = 120_000;
const DEFAULT_COMPILE_WAIT_READY_POLL_INTERVAL_MS = 250;
const COMPILE_WAIT_READY_DELIVERY_RESERVE_MS = 5_000;
const MAX_COMPILE_REFRESH_VERIFICATION_ATTEMPTS = 6_101;
const DEFAULT_READ_RECOVERY_TIMEOUT_MS = 10_000;
const DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS = 250;
const POST_RELOAD_ROUTING_TIMEOUT_MS = 1_500;
const TIMEOUT_RECOVERY_DIAGNOSTICS_CONTRACT = "sgg.unity_mcp.timeout_recovery.v1";
const STALE_COMMAND_RECOVERY_CONTRACT = "sgg.unity_mcp.stale_command_recovery.v2";
const BRIDGE_UNREACHABLE_RECOVERY_CONTRACT = "sgg.unity_mcp.bridge_unreachable_recovery.v2";
const BRIDGE_PROTOCOL_RECOVERY_CONTRACT = "sgg.unity_mcp.bridge_protocol_recovery.v2";
const BRIDGE_LIFECYCLE_RECOVERY_CONTRACT = "sgg.unity_mcp.bridge_lifecycle_recovery.v2";
const TEST_RUN_RESCUE_CONTRACT = "sgg.unity_mcp.test_run_rescue.v2";
const TEST_RUN_TIMEOUT_RECOVERY_CONTRACT = "sgg.unity_mcp.test_run_timeout.v1";
const TEST_RUN_DISCOVERY_PREFLIGHT_CONTRACT = "sgg.unity_mcp.test_run_discovery_preflight.v1";
const TEST_RUN_CORRELATION_TAG = "test_run_operation_correlated";
const TEST_RUN_STARTUP_RESPONSE_GRACE_MS = 2_000;
const TEST_RUN_BINDING_PUBLICATION_RESERVE_MS = 4_000;
const TEST_RUN_DISCOVERY_POLL_INTERVAL_MS = 250;
const APPROVAL_WORKFLOW_CONTRACT = "sgg.unity_mcp.approval_workflow.v1";
const TEST_RUN_COMMAND = "test.run";
const TEST_RUN_STATUS_COMMAND = "test.run.status";
const TEST_RUN_AWAITING_FILTER_REVALIDATION_STATUS = "awaiting_filter_revalidation_after_reload";
const TEST_RUN_CANCEL_COMMAND = "test.run.cancel";
const BUILD_RUN_COMMAND = "build.run";
const BUILD_RUN_STATUS_COMMAND = "build.run.status";
const BUILD_RUN_CORRELATION_TAG = "build_run_operation_correlated";
const BUILD_RUN_MAX_WAIT_MS = 60 * 60 * 1000;
const BUILD_RUN_STARTUP_RECEIPT_SLACK_MS = 2_000;
const DYNAMIC_CSHARP_READONLY_COMMAND = "editor.execute_csharp.readonly";
const DYNAMIC_CSHARP_COMMAND = "editor.execute_csharp";
const DYNAMIC_CSHARP_EXECUTION_COMMANDS = new Set([DYNAMIC_CSHARP_COMMAND, DYNAMIC_CSHARP_READONLY_COMMAND]);
const DYNAMIC_CSHARP_READONLY_STATUS_COMMAND = "editor.execute_csharp.readonly.status";
const DYNAMIC_CSHARP_READONLY_CORRELATION_TAG = "dynamic_csharp_readonly_operation_correlated";
const DYNAMIC_CSHARP_READONLY_RECOVERY_CONTRACT = "sgg.unity_mcp.dynamic_csharp_readonly_recovery.v1";
const DYNAMIC_CSHARP_READONLY_ARTIFACT_ROOT = "Library/SGGUnityMcp/job-artifacts/dynamic-csharp-readonly";
const EDITOR_MENU_OPERATION_TIMEOUT_RECOVERY_CONTRACT = "sgg.unity_mcp.editor_menu_operation_timeout.v1";
const TEST_LIST_COMMAND = "test.list";
const COMPACT_RESPONSE_COMMANDS = new Set([COMPILE_STATUS_COMMAND, COMPILE_WAIT_READY_COMMAND, COMPILE_REFRESH_AND_WAIT_COMMAND, TEST_LIST_COMMAND, TEST_RUN_COMMAND, TEST_RUN_STATUS_COMMAND]);
const ASSET_IMPORT_COMMAND = "asset.import";
const ASSET_IMPORT_PATH_PATTERN = "^Assets[\\\\/].+";
const LOCAL_PACKAGE_SCRIPT_IMPORT_COMMAND = "package.local_script.import";
const GAME_VIEW_SCREENSHOT_COMMAND = "game.view.screenshot";
const GAME_VIEW_SCREENSHOT_BACKENDS = ["camera_render", "end_of_frame", "screen_capture"] as const;
const SCREENSHOT_CAPTURE_RECOVERY_CONTRACT = "sgg.unity_mcp.screenshot_capture_recovery.v1";
const SCREENSHOT_CAPTURE_COMMANDS = new Set([
  "scene.view.screenshot",
  "scene.view.capture_multi_angle",
  GAME_VIEW_SCREENSHOT_COMMAND,
  "camera.screenshot",
  "editor.window.screenshot",
  "desktop.region.screenshot",
]);
const EDITOR_WINDOWS_LIST_COMMAND = "editor.windows.list";
const EDITOR_WINDOWS_TIMEOUT_EVIDENCE_CONTRACT = "sgg.unity_mcp.editor_windows_timeout.v1";
const EXTERNAL_SCENE_RELOAD_BLOCKER_CONTRACT = "sgg.unity_mcp.external_scene_reload_blocker.v1";
const EXTERNAL_SCENE_RELOAD_RESPOND_COMMAND = "diagnostics.unity_external_scene_reload.respond";
const PROJECT_DOWNGRADE_CONTINUE_COMMAND = "diagnostics.unity_project_downgrade.continue";
const SCOPED_REGISTRY_IMPORT_STATUS_COMMAND = "diagnostics.unity_scoped_registry_import.status";
const SCOPED_REGISTRY_IMPORT_CLOSE_COMMAND = "diagnostics.unity_scoped_registry_import.close";
const SAFE_MODE_ENTER_COMMAND = "diagnostics.unity_safe_mode.enter";
const SCENE_BACKUP_KEEP_COMMAND = "diagnostics.unity_scene_backup.keep_backups";
const DIAGNOSTICS_PROBLEM_MARK_COMMAND = "diagnostics.problem.mark";
const canonicalPolicyPaths = new CanonicalPathService();
const EXTERNAL_SCENE_RELOAD_POLL_INTERVAL_MS = 250;
const EXTERNAL_SCENE_RELOAD_MAX_EVIDENCE_AGE_MS = 5000;
const EXTERNAL_SCENE_RELOAD_EVIDENCE_TOKEN_TTL_MS = 60_000;
const MAX_EDITOR_WINDOWS_TIMEOUT_CACHE_ROWS = 100;
const SAVE_PROMPT_COMMANDS = new Set(["editor.save_prompt.get", "editor.save_prompt.respond"]);
const RECOVERY_PROMPT_RESCUE_COMMANDS = new Set(["diagnostics.unity_recovery_prompt.status", "diagnostics.unity_recovery_prompt.respond", EXTERNAL_SCENE_RELOAD_RESPOND_COMMAND, PROJECT_DOWNGRADE_CONTINUE_COMMAND, SCOPED_REGISTRY_IMPORT_STATUS_COMMAND, SCOPED_REGISTRY_IMPORT_CLOSE_COMMAND, SAFE_MODE_ENTER_COMMAND, SCENE_BACKUP_KEEP_COMMAND]);
const EXTERNAL_SCENE_RELOAD_INLINE_COMMANDS = new Set([COMPILE_REFRESH_AND_WAIT_COMMAND, COMPILE_STATUS_COMMAND, COMPILE_WAIT_READY_COMMAND]);
const DEGRADED_POLICY_RECOVERY_COMMANDS = new Set(["diagnostics.status", "diagnostics.report_problem", "policy.explain"]);
const BATCH_TEST_RUN_POLL_INTERVAL_MS = 500;
const BATCH_TEST_RUN_EXTRA_TIMEOUT_MS = 10_000;
const BRIDGE_LIFECYCLE_TRANSIENT_STATUSES = new Set<HeartbeatRecord["status"]>(["starting", "reloading", "waiting_for_idle", "stopping"]);
const BRIDGE_LIFECYCLE_RETRY_AFTER_MS = DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS;
const BRIDGE_RELOAD_TRACE_LINES = 80;
const BRIDGE_RELOAD_TRACE_BYTES = 64 * 1024;
const BRIDGE_PROCESS_SCAN_MIN_REMAINING_MS = 5_500;
const STARTUP_BLOCKER_TOTAL_SCAN_BUDGET_MS = 2_500;
const STARTUP_BLOCKER_WINDOW_SCAN_BUDGET_MS = 1_500;
const STARTUP_BLOCKER_PROCESS_SCAN_BUDGET_MS = 1_000;
const STARTUP_BLOCKER_CACHE_MAX_AGE_MS = 1_000;
const MAX_STARTUP_BLOCKER_CANDIDATES = 8;
const MAX_STARTUP_BLOCKER_ERRORS = 8;
const BRIDGE_RELOAD_TRACE_RELATIVE_PATH = path.join("Library", "SGGUnityMcp", "bridge-reload-trace.log");
const BRIDGE_RELOAD_TRACE_ARTIFACT_PATH = "Library/SGGUnityMcp/bridge-reload-trace.log";
const BRIDGE_THROWN_ERROR_CODES = ["bridge_unreachable", "bridge_protocol_error", "protocol_version_mismatch", "bridge_timeout", "invalid_token", "selector_not_supported"] as const satisfies readonly ErrorCode[];
const COMMAND_REPLACEMENTS: Record<string, string> = {
  "menu.execute": "editor.menu.execute",
  "editor.execute_menu_item": "editor.menu.execute",
  "tests.run": "test.run",
};

type JsonSchemaType = "string" | "number" | "integer" | "boolean" | "object" | "array" | "null";

export type DispatcherContext = {
  registry?: ProjectRegistry;
  bridge?: BridgeClient;
  policy?: PolicyConfig;
  policyManager?: PolicyManager;
  queues?: ProjectQueueManager;
  jobs?: InMemoryJobManager;
  audit?: AuditSink;
  operationalLogger?: OperationalLogger;
  features?: Record<string, boolean>;
  assetIndexCacheRoot?: string;
  monoBehaviourAllowlist?: Array<{ assembly_name: string; full_type_name: string; note?: string | undefined }>;
  configPath?: string;
  policyPath?: string;
  instancesDir?: string;
  auditEnabled?: boolean;
  auditDir?: string;
  auditFilePath?: string;
  mcpTokenPresent?: boolean;
  approvalStore?: ApprovalStore;
  approvalBindingService?: ApprovalBindingService;
  testRunOperationStore?: TestRunOperationStoreLike;
  testRunOperationPublisher?: TestRunOperationPublisher;
  approvalDefaultTtlMs?: number;
  approvalAutoAllowAsk?: boolean;
  approvalAutoAllowKnownUnityPopups?: boolean;
  knownUnityPopupSettings?: KnownUnityPopupSettingsManager;
  beforeKnownUnityPopupResponse?: () => void;
  approvalAutoAllowStaleCleanupWithoutElicitation?: boolean;
  nativeMcpFormElicitationAvailable?: boolean;
  officialRelay?: OfficialRelayAdapter;
  unityCrashReporter?: UnityCrashReporterRuntime;
  unityProjectOpen?: UnityProjectOpenRuntime;
  unityProcessDiagnostics?: UnityProcessRuntime;
  unityWindowDiagnostics?: UnityWindowDiagnosticsRuntime;
  unityEditorDiscovery?: { status: (args?: { version?: string }) => Record<string, unknown> };
  skillUnityUserSkillsRoot?: string;
  now?: () => Date;
  sleepMs?: (ms: number) => Promise<void>;
  runId?: string;
  gatewayRuntimeVersion?: string;
  internalDeadlineUnixMs?: number;
  statisticsInternal?: boolean;
};

export type AcceptedPendingRedispatch = {
  generation: ApprovalPendingGeneration;
  original_confirmation: GatewayResult;
};

export type DispatchInvocationOptions = {
  nativeMcpFormElicitationAvailable?: boolean;
};

export async function dispatchTool(toolName: ToolName, rawArgs: unknown, context: DispatcherContext = {}, invocation: DispatchInvocationOptions = {}): Promise<GatewayResult> {
  return dispatchWithRuntime(toolName, rawArgs, context, undefined, invocation);
}

export async function dispatchAcceptedPendingCall(rawArgs: unknown, context: DispatcherContext, accepted: AcceptedPendingRedispatch): Promise<GatewayResult> {
  return dispatchAcceptedPendingTool("sgg_unity_call", rawArgs, context, accepted);
}

export async function dispatchAcceptedPendingTool(toolName: ToolName, rawArgs: unknown, context: DispatcherContext, accepted: AcceptedPendingRedispatch, invocation: DispatchInvocationOptions = {}): Promise<GatewayResult> {
  return dispatchWithRuntime(toolName, rawArgs, context, { ...accepted, handled: false }, invocation);
}

async function dispatchWithRuntime(toolName: ToolName, rawArgs: unknown, context: DispatcherContext, acceptedPending?: AcceptedPendingRuntime, invocation: DispatchInvocationOptions = {}): Promise<GatewayResult> {
  const startedAtMs = context.now?.().getTime() ?? Date.now();
  const runtimeContext: DispatcherRuntimeContext = { ...context, ...invocation, ...(acceptedPending ? { acceptedPending } : {}) };
  const auditId = randomUUID();
  if (context.audit) {
    await context.audit.prepare(createAuditStartEvent(toolName, rawArgs, context.now ?? (() => new Date()), context.runId ?? "default", auditId));
  }
  refreshManagedPolicy(runtimeContext);
  refreshKnownUnityPopupApproval(runtimeContext);
  let result = await dispatchToolInternal(toolName, rawArgs, runtimeContext);
  if (acceptedPending && !acceptedPending.handled) {
    const claimed = context.approvalStore?.claimPendingGeneration(acceptedPending.generation, undefined, context.now?.() ?? new Date());
    result = claimed && claimed.status !== "not_found" ? result : acceptedPending.original_confirmation;
  }
  if (runtimeContext.batchItemTrace
    && runtimeContext.batchItemIndex !== undefined
    && toolName === "sgg_unity_call"
    && isObjectRecord(rawArgs)
    && rawArgs.command === runtimeContext.batchItemTrace.command) {
    const batchItem = batchItemExecution(runtimeContext.batchItemIndex, result, runtimeContext.batchItemTrace);
    result = { ...result, batch_item: batchItem };
    if (runtimeContext.statisticsFullResult) runtimeContext.statisticsFullResult = { ...runtimeContext.statisticsFullResult, batch_item: batchItem };
    if (runtimeContext.statisticsCompactResult) runtimeContext.statisticsCompactResult = { ...runtimeContext.statisticsCompactResult, batch_item: batchItem };
  }
  const policyStatus = policyManagerSnapshot(runtimeContext.policyManager, runtimeContext.policy !== undefined);
  if (result.status === "error") {
    result = { ...result, policy_status: policyStatus };
  }
  result = canonicalGatewayResultOrFailure(toolName, rawArgs, result, runtimeContext);
  result = boundSelectorErrorResult(result);
  if (context.audit) {
    const statistics = measureCommandStatistics({
      tool: toolName,
      rawArgs,
      deliveredResult: result,
      ...(result.status === "ok" && runtimeContext.statisticsFullResult?.status === "ok" ? { fullResult: runtimeContext.statisticsFullResult } : {}),
      ...(result.status === "ok" && runtimeContext.statisticsCompactResult?.status === "ok" ? { compactResult: runtimeContext.statisticsCompactResult } : {}),
      ...(runtimeContext.statisticsResponseMode ? { responseMode: runtimeContext.statisticsResponseMode } : {}),
      internal: runtimeContext.statisticsInternal === true,
      ...(runtimeContext.statisticsCommandDispatched !== undefined ? { dispatched: runtimeContext.statisticsCommandDispatched } : {}),
    });
    const auditContext = AuditContext.create({
      tool: toolName,
      rawArgs,
      result,
      startedAtMs,
      ...(context.now ? { now: context.now } : {}),
      metadata: {
        ...(runtimeContext.auditCommandMetadata ? { command: runtimeContext.auditCommandMetadata } : {}),
        ...(runtimeContext.auditPolicyMetadata ? { policy: runtimeContext.auditPolicyMetadata } : {}),
        ...(runtimeContext.auditBridgeDurationMs !== undefined ? { bridge_duration_ms: runtimeContext.auditBridgeDurationMs } : {}),
        ...(runtimeContext.auditBridgeInvokeAttempts !== undefined ? { bridge_invoke_attempts: runtimeContext.auditBridgeInvokeAttempts } : {}),
        ...(runtimeContext.auditReadRecoveryStatus ? { read_recovery_attempted: true, read_recovery_status: runtimeContext.auditReadRecoveryStatus } : {}),
        ...(runtimeContext.auditPostReloadRouting ? {
          post_reload_routing_attempted: true,
          post_reload_routing_status: runtimeContext.auditPostReloadRouting.status,
          post_reload_routing_attempts: runtimeContext.auditPostReloadRouting.attempts,
          post_reload_routing_command_risk: runtimeContext.auditPostReloadRouting.commandRisk,
        } : {}),
        ...(runtimeContext.auditCompileVerification ? {
          compile_verification_attempted: true,
          compile_verification_status: runtimeContext.auditCompileVerification.status,
          compile_verification_attempts: runtimeContext.auditCompileVerification.attempts,
          compile_verification_read_requests: runtimeContext.auditCompileVerification.readRequests,
          write_command_repeated: false,
        } : {}),
        policy_status: policyStatus,
        statistics,
      },
      ...(context.runId ? { runId: context.runId } : {}),
      auditId,
    });
    await writeInvocationAudit(context.audit, auditContext);
  }
  return result;
}

function canonicalGatewayResultOrFailure(toolName: ToolName, rawArgs: unknown, result: GatewayResult, context: DispatcherRuntimeContext): GatewayResult {
  try {
    canonicalJsonStringify(result);
    return result;
  } catch (error) {
    if (error instanceof Error && error.message.includes("undefined")) {
      const normalized = omitUndefinedObjectProperties(result, new Set()) as GatewayResult;
      try {
        canonicalJsonStringify(normalized);
        return normalized;
      } catch {
        // Fall through to a typed noncanonical-result error.
      }
    }
    const args = isObjectRecord(rawArgs) ? rawArgs : {};
    const project = typeof args.project === "string" && args.project.length <= 512 ? args.project : undefined;
    const command = typeof args.command === "string" && /^[A-Za-z0-9_.-]{1,256}$/.test(args.command) ? args.command : undefined;
    const unsafe = context.auditCommandMetadata?.risk !== undefined && context.auditCommandMetadata.risk !== "read";
    const reason = error instanceof Error && error.message.includes("undefined")
      ? "undefined_value"
      : error instanceof Error && error.message.includes("enumerable string keys")
        ? "non_json_property"
        : "noncanonical_value";
    return fail({
      ...(project ? { project } : {}),
      ...(command ? { command } : {}),
      error: {
        code: "internal_error",
        message: "Gateway produced a noncanonical result; the original command will not be replayed automatically",
        details: {
          reason: "noncanonical_gateway_result",
          canonical_failure_kind: reason,
          tool: toolName,
          original_status: result.status,
          delivery_state: unsafe ? "uncertain" : "unknown",
          automatic_replay_attempted: false,
          automatic_replay_allowed: false,
          write_command_repeated: false,
        },
      },
      next_valid_actions: command === COMPILE_REFRESH_AND_WAIT_COMMAND
        ? ["do_not_repeat_compile.refresh_and_wait", "call_compile.wait_ready_with_the_original_expected_barrier_when_available", "call_diagnostics.report_problem"]
        : ["inspect_current_state_before_repeating_non_read_commands", "call_diagnostics.report_problem"],
    });
  }
}

function omitUndefinedObjectProperties(value: unknown, ancestors: Set<object>): unknown {
  if (value === null || typeof value !== "object") return value;
  if (ancestors.has(value)) return value;
  if (Array.isArray(value)) {
    ancestors.add(value);
    try {
      return value.map((item) => omitUndefinedObjectProperties(item, ancestors));
    } finally {
      ancestors.delete(value);
    }
  }
  const prototype = Object.getPrototypeOf(value) as unknown;
  const keys = Reflect.ownKeys(value);
  if ((prototype !== Object.prototype && prototype !== null) || keys.some((key) => typeof key !== "string" || !Object.prototype.propertyIsEnumerable.call(value, key))) return value;
  ancestors.add(value);
  try {
    return Object.fromEntries(Object.entries(value).flatMap(([key, child]) => child === undefined ? [] : [[key, omitUndefinedObjectProperties(child, ancestors)]]));
  } finally {
    ancestors.delete(value);
  }
}

function refreshManagedPolicy(context: DispatcherRuntimeContext): void {
  if (!context.policyManager) {
    return;
  }
  context.policyManager.reload();
  const policy = context.policyManager.policy;
  if (policy) {
    context.policy = policy;
  } else {
    delete context.policy;
  }
}

type DispatcherRuntimeContext = DispatcherContext & {
  approvedKnownPopupMode?: "wait" | "job";
  auditCommandMetadata?: AuditCommandMetadata;
  auditPolicyMetadata?: AuditPolicyMetadata;
  auditBridgeDurationMs?: number;
  auditBridgeInvokeAttempts?: number;
  auditReadRecoveryStatus?: "ready" | "error" | "timeout";
  auditPostReloadRouting?: { status: PostReloadRoutingEvidence["terminal_status"]; attempts: number; commandRisk: PostReloadRoutingEvidence["command_risk"] };
  auditCompileVerification?: { status: CompileRefreshVerificationStatus; attempts: number; readRequests: number };
  batchItemTrace?: BatchItemTrace;
  batchItemIndex?: number;
  acceptedPending?: AcceptedPendingRuntime;
  statisticsFullResult?: GatewayResult;
  statisticsCompactResult?: GatewayResult;
  statisticsResponseMode?: "full" | "compact";
  statisticsCommandDispatched?: boolean;
};

type AcceptedPendingRuntime = AcceptedPendingRedispatch & { handled: boolean };

async function dispatchToolInternal(toolName: ToolName, rawArgs: unknown, context: DispatcherRuntimeContext): Promise<GatewayResult> {
  try {
    switch (toolName) {
      case "sgg_unity_projects": {
        TOOL_ARG_SCHEMAS.sgg_unity_projects.parse(rawArgs ?? {});
        const snapshot = context.registry?.captureSnapshot();
        const stats = snapshot?.getStats();
        const liveProjects = snapshot?.records.filter((record) => !record.stale) ?? [];
        const startupBlockers = await startupBlockersForSnapshot(context, snapshot?.records ?? []);
        return ok({ data: {
          projects: liveProjects,
          counts: {
            total: stats?.projectRecordsCount ?? 0,
            live: stats?.liveProjectRecordsCount ?? 0,
            stale: stats?.staleProjectRecordsCount ?? 0,
            unique_projects: stats?.uniqueProjectsCount ?? 0,
          },
          registry_operational_state: stats?.registryOperationalState ?? "unavailable",
          ...(startupBlockers ? { startup_blockers: startupBlockers } : {}),
        } });
      }

      case "sgg_unity_stale_projects": {
        const args = TOOL_ARG_SCHEMAS.sgg_unity_stale_projects.parse(rawArgs ?? {});
        if (!context.registry) {
          return fail({ error: { code: "feature_disabled", message: "Project registry is unavailable" } });
        }
        return ok({ data: context.registry.queryStaleProjects(args) });
      }

      case "sgg_unity_stale_registry_cleanup": {
        const args = TOOL_ARG_SCHEMAS.sgg_unity_stale_registry_cleanup.parse(rawArgs ?? {});
        if (!context.registry) {
          return fail({ error: { code: "feature_disabled", message: "Project registry is unavailable" } });
        }
        if (args.dry_run === false) {
          if (args.confirm_cleanup !== true || !args.record_ids?.length || !args.precondition_hash) {
            return ok({ data: context.registry.cleanupStaleRecords(args) });
          }
          const preview = context.registry.cleanupStaleRecords({ ...args, dry_run: true, confirm_cleanup: false });
          if (preview.precondition_hash !== args.precondition_hash || preview.would_delete_count !== preview.selected_count) {
            throw new StaleRegistryOperationError("precondition_failed", "Stale registry cleanup precondition changed", {
              mutation_attempted: false,
              automatic_retry_allowed: false,
            });
          }
          const permission = checkStaleCleanupApproval(context, args);
          if (permission.status === "error") return permission.result;
          if (permission.approvalMode === "temporary_auto_allow_without_native_elicitation") {
            const cleanup = context.registry.cleanupStaleRecords(args);
            return ok({ data: {
              ...cleanup,
              approval_compatibility: {
                mode: permission.approvalMode,
                scope: STALE_CLEANUP_APPROVAL_COMMAND,
                native_mcp_form_elicitation_available: false,
              },
            } });
          }
        }
        return ok({ data: context.registry.cleanupStaleRecords(args) });
      }

      case "sgg_unity_describe": {
        const args = TOOL_ARG_SCHEMAS.sgg_unity_describe.parse(rawArgs);
        const describeArgs = { ...args, selector: normalizeDescribeSelector(args.selector) };
        const recoveryContext = RecoveryContext.describe();
        const strategy = selectRecoveryStrategy(recoveryContext);
        const outcome = await strategy.execute(recoveryContext, recoveryHandlers({
          read: () => describeUnityWithBridgeRecovery(context, describeArgs),
          noAutomaticRecovery: () => describeUnityOnce(context, describeArgs),
        }));
        return outcome.result;
      }

      case "sgg_unity_call": {
        const args = TOOL_ARG_SCHEMAS.sgg_unity_call.parse(rawArgs);
        const commandArgs = parseArgsJson(args.args_json);
        const terminalWaitDeadlineUnixMs = args.wait_for_test_run_terminal_ms === undefined ? undefined : nowMs(context) + Math.min(args.wait_for_test_run_terminal_ms, MAX_POST_ADMISSION_WAIT_MS);
        if (args.wait_for_replacement_ms !== undefined && (args.mode !== "wait" || args.command !== GAME_VIEW_SCREENSHOT_COMMAND)) {
          const result = replacementWaitValidationFailed(context, args);
          return args.command === COMPILE_WAIT_READY_COMMAND ? withCompileWaitReadyDeliveryContract(result, commandArgs, 0) : result;
        }
        if (args.wait_for_test_run_terminal_ms !== undefined && (args.mode !== "wait" || args.command !== TEST_RUN_COMMAND || commandArgs.response_mode !== "compact" || commandArgs.dry_run === true)) {
          const result = testRunTerminalWaitValidationFailed(context, args);
          return args.command === COMPILE_WAIT_READY_COMMAND ? withCompileWaitReadyDeliveryContract(result, commandArgs, 0) : result;
        }
        const boundStatus = await invokeBoundTestRunStatus(context, args, commandArgs);
        let outcome = boundStatus === undefined
          ? await withInternalDeadline(context, terminalWaitDeadlineUnixMs, async () => {
            const recoveryContext = RecoveryContext.call({ command: args.command, mode: args.mode, acceptedPending: context.acceptedPending !== undefined });
            const strategy = selectRecoveryStrategy(recoveryContext);
            const execute = () => strategy.execute(recoveryContext, recoveryHandlers({
              read: () => invokeUnityCallWithRiskAwareRecovery(context, args, commandArgs),
              riskAware: () => args.wait_for_replacement_ms !== undefined
                ? invokeUnityCallWithPreDispatchReplacementWait(context, args, commandArgs)
                : invokeUnityCallWithRiskAwareRecovery(context, args, commandArgs),
              compileRefresh: () => invokeCompileRefreshAndWaitWithVerification(context, args, commandArgs),
              validationAfterEdit: () => invokeValidationAfterEditWithVerification(context, args, commandArgs),
              noAutomaticRecovery: () => context.acceptedPending && args.mode === "wait" && isReadRecoveryCommand(args.command)
                ? invokeAcceptedReadOnceWithEmptyBodyRecovery(context, args, commandArgs)
                : args.wait_for_replacement_ms !== undefined
                  ? invokeUnityCallWithPreDispatchReplacementWait(context, args, commandArgs)
                  : invokeUnityCallOnce(context, args, commandArgs, terminalWaitDeadlineUnixMs),
            }));
            return terminalWaitDeadlineUnixMs === undefined ? execute() : waitForUndispatchedTestRunCatalog(context, terminalWaitDeadlineUnixMs, execute);
          })
          : { result: boundStatus };
        if (boundStatus === undefined && testRunStatusBindingRecheckEligible(context, args, outcome.result)) {
          const rechecked = await recheckBoundTestRunStatus(context, args, commandArgs);
          if (rechecked !== undefined) outcome = { result: rechecked };
        }
        const awaitedBeforeDeliveryContract = args.wait_for_test_run_terminal_ms === undefined
          ? outcome.result
          : await waitForDirectTestRunOperation(args, commandArgs, outcome.result, context, terminalWaitDeadlineUnixMs!);
        const awaited = args.command === COMPILE_WAIT_READY_COMMAND
          ? withCompileWaitReadyDeliveryContract(awaitedBeforeDeliveryContract, commandArgs, 0)
          : awaitedBeforeDeliveryContract;
        const compact = compactSuccessfulCommandResult(args.command, { ...commandArgs, response_mode: "compact" }, awaited);
        context.statisticsFullResult = awaited;
        context.statisticsCompactResult = compact;
        context.statisticsResponseMode = commandArgs.response_mode === "compact" ? "compact" : "full";
        return context.statisticsResponseMode === "compact" ? compact : awaited;
      }

      case "sgg_unity_batch": {
        const args = TOOL_ARG_SCHEMAS.sgg_unity_batch.parse(rawArgs);
        const items = parseItemsJson(args.items_json);
        if (items.length > MAX_BATCH_ITEMS) {
          return inputError("schema_validation_failed", "Batch item count exceeds the gateway limit", { validation_issue_count: 1 });
        }
        const correlation = {
          ...(args.checkpoint_id ? { checkpoint_id: args.checkpoint_id } : {}),
          ...(args.plan_id ? { plan_id: args.plan_id } : {}),
        };
        const route = new BatchRoute(args.mode, items.length);
        const execution = await route.execute({
          sequential: () => dispatchBatchSequential(items, context, correlation),
          parallelByProject: () => dispatchBatchParallelByProject(items, context, correlation),
        });
        if (execution.interrupted) {
          return fail({
            ...optionalRequestId(args.request_id),
            error: {
              code: execution.interrupted_error_code,
              message: "Sequential batch interrupted because an unsafe item has uncertain delivery",
              details: execution,
            },
            next_valid_actions: [
              "verify_current_unity_state_before_repeating_interrupted_item",
              "do_not_replay_interrupted_item_automatically",
              "submit_remaining_items_explicitly_only_after_uncertain_delivery_is_resolved",
            ],
          });
        }
        return ok({ ...optionalRequestId(args.request_id), data: execution });
      }

      case "sgg_unity_job": {
        const args = TOOL_ARG_SCHEMAS.sgg_unity_job.parse(rawArgs);
        const jobs = context.jobs;
        if (!jobs) {
          return jobNotFound(args.project, args.job_id);
        }

        const projectKey = resolveProjectKeyOrInput(context, args.project);
        const job = jobs.get(projectKey, args.job_id);
        if (!job) {
          return jobNotFound(projectKey, args.job_id);
        }

        return new JobRoute(args.action).execute({
          status: () => ok({ project: projectKey, data: job }),
          cancel: () => {
            if (isCancellableJobStatus(job.status) && !job.supports_cancel) {
              return ok({ project: projectKey, data: jobCancellationUnsupported(job) });
            }

            const cancelled = jobs.cancel(projectKey, args.job_id);
            if (!cancelled) {
              return jobNotFound(projectKey, args.job_id);
            }
            return ok({ project: projectKey, data: jobCancellationResult(cancelled) });
          },
        });
      }

      case "sgg_unity_logs": {
        const args = TOOL_ARG_SCHEMAS.sgg_unity_logs.parse(rawArgs);
        return readUnityLogsWithBridgeRecovery(context, args);
      }
    }
  } catch (error) {
    if (error instanceof StaleRegistryOperationError) {
      const nextValidActions = Array.isArray(error.details.next_valid_actions)
        ? error.details.next_valid_actions.filter((value): value is string => typeof value === "string")
        : error.code === "invalid_field_value"
          ? ["retry_without_cursor"]
          : error.code === "confirmation_required" || error.code === "precondition_required" || error.code === "precondition_failed"
            ? ["run_sgg_unity_stale_registry_cleanup_with_dry_run_true", "do_not_retry_cleanup_automatically"]
            : ["inspect_registry_configuration_and_state"];
      return fail({
        error: { code: error.code, message: error.message, details: error.details },
        next_valid_actions: nextValidActions,
      });
    }
    return normalizeInputError(error, rawArgs, toolName, context);
  }
}

async function readUnityLogsWithBridgeRecovery(context: DispatcherRuntimeContext, args: { project: string; kind: "console" | "bridge" | "jobs"; limit: number }): Promise<GatewayResult> {
  const retry = { timeoutMs: POST_RELOAD_ROUTING_TIMEOUT_MS, pollIntervalMs: DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS };
  const startedAtMs = nowMs(context);
  const deadlineMs = earlierDeadline(startedAtMs + retry.timeoutMs, context.internalDeadlineUnixMs);
  if (context.internalDeadlineUnixMs !== deadlineMs) {
    return withInternalDeadline(context, deadlineMs, () => readUnityLogsWithBridgeRecovery(context, args));
  }
  let attempts = 0;
  let lastResult: GatewayResult;
  const maxAttempts = Math.ceil(retry.timeoutMs / retry.pollIntervalMs) + 1;

  while (attempts < maxAttempts) {
    attempts += 1;
    lastResult = await readUnityLogsOnce(context, args, recoveryTransport(context, deadlineMs));
    if (!isDescribeRecoveryRetryableError(lastResult, context)) return lastResult;
    if (lastResult.status === "error" && lastResult.error.code === "project_not_found" && context.registry?.resolveRecentReplacementProject(args.project).status === "ok") return lastResult;
    const remainingMs = deadlineMs - nowMs(context);
    if (remainingMs <= 0) {
      return bridgeRecoveryRetryExhausted(lastResult, attempts, retry.timeoutMs, Math.max(0, nowMs(context) - startedAtMs), { tool: "sgg_unity_logs", kind: args.kind });
    }
    await sleepMs(context, Math.min(retry.pollIntervalMs, remainingMs));
  }
  if (lastResult!.status !== "error") return lastResult!;
  return bridgeRecoveryRetryExhausted(lastResult!, attempts, retry.timeoutMs, Math.max(0, nowMs(context) - startedAtMs), { tool: "sgg_unity_logs", kind: args.kind });
}

async function readUnityLogsOnce(context: DispatcherRuntimeContext, args: { project: string; kind: "console" | "bridge" | "jobs"; limit: number }, transport?: BridgeTransportOptions): Promise<GatewayResult> {
  const resolution = resolveProject(context, args.project);
  if (resolution.status === "error") return projectResolutionError(args.project, resolution, undefined, undefined, context.registry, {}, context);
  const bridge = context.bridge;
  if (!bridge) return bridgeUnavailable(args.project);
  const projectKey = resolution.record.project;
  const lifecycleGate = bridgeLifecycleGateResult({
    project: projectKey,
    projectSelector: args.project,
    selector: `logs.${args.kind}`,
    record: resolution.record,
    registry: context.registry,
  }, context);
  if (lifecycleGate) return lifecycleGate;
  const route = new LogsRoute(args.kind, args.limit);
  try {
    return ok({ project: projectKey, data: await route.execute((kind, limit) => bridge.logs(resolution.record, kind, limit, transport)) });
  } catch (error) {
    const bridgeError = bridgeThrownError(error, { project: projectKey, projectSelector: args.project, record: resolution.record, registry: context.registry }, context);
    if (bridgeError) return bridgeError;
    throw error;
  }
}

async function invokeBoundTestRunStatus(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>): Promise<GatewayResult | undefined> {
  const store = context.testRunOperationStore;
  if (args.command !== TEST_RUN_STATUS_COMMAND || args.mode !== "wait" || !store) return undefined;

  const validation = validateCommandArgs(TEST_RUN_STATUS_FALLBACK_DESCRIPTOR.schema, commandArgs, true);
  if (validation.status === "schema_unavailable") return schemaUnavailable(undefined, undefined, args.command, args.request_id);
  if (validation.status === "error") {
    return commandSchemaValidationFailed(undefined, undefined, args.command, args.request_id, validation.issues, TEST_RUN_STATUS_FALLBACK_DESCRIPTOR.schema, commandArgs, TEST_RUN_STATUS_FALLBACK_DESCRIPTOR);
  }
  const operationId = testRunStatusOperationId(commandArgs);
  if (!operationId) {
    return fail({
      ...optionalRequestId(args.request_id),
      command: args.command,
      error: { code: "invalid_field_value", message: "operation_id and run_id must identify the same test run", details: preDispatchNonDeliveryDetails() },
    });
  }

  let lookup: ReturnType<TestRunOperationStoreLike["lookup"]>;
  try {
    lookup = store.lookup(operationId, context.now?.() ?? new Date());
  } catch (error) {
    const reason = error instanceof TestRunOperationStoreError && error.reason === "binding_invalid" ? "operation_binding_unavailable" : "operation_binding_unavailable";
    return testRunStatusUnavailable(args, operationId, reason, false);
  }
  if (lookup.status === "not_found") {
    const liveResolution = resolveProject(context, args.project);
    const nativeModalRecovery = liveResolution.status === "error"
      && staleDiagnosticsAllowed(context, liveResolution.matches, args.project, TEST_RUN_STATUS_COMMAND)
      ? staleNativeModalRecoveryDetails(liveResolution.matches, currentCandidateInstances(liveResolution.matches, context.registry, projectRegistrySnapshotForResolution(liveResolution)), context)
      : undefined;
    return liveResolution.status === "ok" ? undefined : testRunStatusUnavailable(args, operationId, "operation_binding_not_found", false, nativeModalRecovery);
  }

  return invokeBoundTestRunStatusFromBinding(context, args, commandArgs, store, lookup.binding, operationId);
}

function testRunStatusBindingRecheckEligible(context: DispatcherRuntimeContext, args: UnityCallArgs, result: GatewayResult): boolean {
  if (args.command !== TEST_RUN_STATUS_COMMAND || args.mode !== "wait" || !context.testRunOperationStore || result.status !== "error") return false;
  if (!["project_not_found", "project_stale", "unity_reloading", "catalog_pending", "catalog_stale", "catalog_unavailable", "command_not_found", "bridge_unreachable", "bridge_timeout"].includes(result.error.code)) return false;
  if (result.error.code === "command_not_found") return preDispatchCommandNotFoundResults.has(result);
  const details = result.error.details;
  return (details?.command_delivery_state === "not_dispatched" && details.command_bridge_request_issued === false)
    || (details?.command_dispatched === false && details.bridge_request_issued === false);
}

async function recheckBoundTestRunStatus(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>): Promise<GatewayResult | undefined> {
  const store = context.testRunOperationStore;
  const operationId = testRunStatusOperationId(commandArgs);
  if (!store || !operationId) return undefined;
  let binding: TestRunOperationBinding;
  try {
    const lookup = store.lookup(operationId, context.now?.() ?? new Date());
    if (lookup.status !== "found") return undefined;
    assertTestRunOperationBinding(lookup.binding);
    binding = lookup.binding;
  } catch {
    return undefined;
  }
  return invokeBoundTestRunStatusFromBinding(context, args, commandArgs, store, binding, operationId);
}

async function invokeBoundTestRunStatusFromBinding(
  context: DispatcherRuntimeContext,
  args: UnityCallArgs,
  commandArgs: Record<string, unknown>,
  store: TestRunOperationStoreLike,
  binding: TestRunOperationBinding,
  operationId: string,
): Promise<GatewayResult> {

  if (!testRunStatusSelectorMatchesBinding(args.project, binding)) {
    return fail({
      ...optionalRequestId(args.request_id),
      project: args.project,
      command: args.command,
      error: {
        code: "unsafe_binding_refused",
        message: "The requested project selector does not match the persisted test.run owner binding",
        details: {
          ...preDispatchNonDeliveryDetails(),
          reason: "project_binding_mismatch",
          operation_id: operationId,
          requested_project_selector_hash: `sha256:${createHash("sha256").update(args.project, "utf8").digest("hex")}`,
          operation_owner_verified: false,
          owner_endpoint_contacted: false,
        },
      },
      next_valid_actions: ["use_the_original_project_or_instance_selector", "do_not_probe_other_projects_for_the_operation", "call_diagnostics.report_problem"],
    });
  }

  const descriptor = boundTestRunStatusDescriptor(binding);
  const resolved: ResolvedInvocation = { project: binding.project, command: descriptor, commandsHash: binding.commands_hash, virtual: true };
  refreshManagedPolicy(context);
  const policy = applyInvocationPolicy(context, TEST_RUN_STATUS_COMMAND, args.request_id, resolved, args.project, true);
  if (policy.state === "blocked") return withCommandNotDispatched(policy.result);
  const fallbackInvocation = InvocationContextSnapshot.create({
    projectSelector: args.project,
    project: binding.project,
    command: TEST_RUN_STATUS_COMMAND,
    descriptor,
    commandsHash: binding.commands_hash,
  });
  const permission = checkInvocationPermission(context, fallbackInvocation, args.mode, args.request_id, commandArgs);
  if (permission.status === "error") return withCommandNotDispatched(permission.result);

  let requestIssuance: TestRunRequestIssuanceLookup;
  try {
    requestIssuance = store.lookupRequestIssuance(binding, context.now?.() ?? new Date());
  } catch (error) {
    const evidenceSource = error instanceof TestRunOperationStoreError && error.reason === "issuance_invalid"
      ? "durable_request_issuance_evidence_invalid"
      : "durable_request_issuance_storage_unavailable";
    return testRunStatusIssuanceUnavailable(args, binding, evidenceSource);
  }

  if (requestIssuance.status === "found") {
    try {
      const terminal = store.lookupTerminalNoExecution(binding, requestIssuance.marker, context.now?.() ?? new Date());
      if (terminal.status === "found") {
        context.statisticsCommandDispatched = false;
        return ok({
          ...optionalRequestId(args.request_id),
          project: binding.project,
          command: args.command,
          data: testRunTerminalNoExecutionStatusData(binding, terminal.receipt, requestIssuance),
          next_valid_actions: testRunTerminalNoExecutionNextValidActions(terminal.receipt),
        });
      }
    } catch (error) {
      const evidenceSource = error instanceof TestRunOperationStoreError && error.reason === "terminal_outcome_invalid"
        ? "durable_terminal_no_execution_evidence_invalid"
        : "durable_terminal_no_execution_storage_unavailable";
      return testRunStatusTerminalNoExecutionUnavailable(args, binding, requestIssuance, evidenceSource);
    }
  }

  const ownerResolution = resolveProject(context, binding.origin_instance_id);
  if (ownerResolution.status === "ok" && testRunOwnerRecordMatchesBinding(ownerResolution.record, binding)) {
    const liveResult = await invokeUnityCallOnce(
      context,
      args,
      commandArgs,
      context.internalDeadlineUnixMs,
      false,
      undefined,
      false,
      ownerResolution.record,
      binding.commands_hash,
      undefined,
      undefined,
      undefined,
      { permissionAlreadyChecked: true },
    );
    if (!testRunStatusArtifactFallbackEligible(liveResult)) return withBoundLiveTestRunStatusEvidence(context, args, liveResult, binding, requestIssuance);
  }

  return boundTestRunArtifactResult(context, args, binding, operationId, requestIssuance);
}

function testRunStatusArtifactFallbackEligible(result: GatewayResult): boolean {
  if (result.status !== "error" || !["unity_reloading", "catalog_pending", "catalog_unavailable"].includes(result.error.code)) return false;
  return result.error.details?.bridge_request_issued === false && result.error.details?.command_dispatched === false;
}

async function boundTestRunArtifactResult(context: DispatcherRuntimeContext, args: UnityCallArgs, binding: TestRunOperationBinding, operationId: string, requestIssuance: TestRunRequestIssuanceLookup): Promise<GatewayResult> {
  context.statisticsCommandDispatched = false;
  const fallback = await readBoundTestRunStatus(binding, operationId, {
    ...(context.internalDeadlineUnixMs !== undefined ? { deadlineUnixMs: context.internalDeadlineUnixMs } : {}),
    nowUnixMs: () => nowMs(context), ...(context.sleepMs ? { sleepMs: context.sleepMs } : {}), requestIssuance,
  });
  if (fallback.status === "error") {
    const nativeModalRecovery = boundTestRunNativeModalRecoveryDetails(context, binding);
    return fail({
      ...optionalRequestId(args.request_id),
      project: binding.project,
      command: args.command,
      error: {
        ...fallback.error,
        ...(nativeModalRecovery ? { details: { ...fallback.error.details, native_modal_recovery: nativeModalRecovery } } : {}),
      },
    });
  }
  return ok({
    ...optionalRequestId(args.request_id),
    project: binding.project,
    command: args.command,
    data: fallback.data,
    artifact_refs: [binding.artifact_path],
  });
}

function withBoundLiveTestRunStatusEvidence(context: DispatcherRuntimeContext, args: UnityCallArgs, result: GatewayResult, binding: TestRunOperationBinding, requestIssuance: TestRunRequestIssuanceLookup): GatewayResult {
  if (result.status === "error" && trustedLiveTestRunMissingResults.has(result)) {
    context.statisticsCommandDispatched = true;
    return fail({
      ...optionalRequestId(args.request_id),
      project: binding.project,
      command: args.command,
      error: {
        code: "test_run_status_unavailable",
        message: "The exact live owner could not locate this test.run operation; its execution outcome remains unresolved",
        details: {
          reason: "live_operation_not_found",
          original_bridge_error_code: "test_run_not_found",
          operation_id: binding.operation_id,
          status_source: "live_owner_bridge",
          operation_owner_verified: true,
          owner_endpoint_contacted: true,
          command_bridge_request_issued: true,
          status_returned: false,
          artifact_availability: "unverified",
          test_run_replayed: false,
          write_command_repeated: false,
          write_replay_allowed: false,
          ...testRunRequestIssuanceEvidence(binding, requestIssuance, true),
          recovery_hint: "Missing operation status is not proof of non-execution. Do not repeat test.run or cancel the unresolved operation. After fresh owner readiness, poll this exact operation's status or report the unresolved evidence.",
        },
      },
      next_valid_actions: ["poll_exact_test.run.status_after_fresh_readiness", "call_diagnostics.report_problem", "do_not_repeat_test.run"],
    });
  }
  if (result.status === "error") {
    const evidenced = withTestRunRequestIssuanceEvidence(result, binding, requestIssuance);
    if (invalidTestRunObservationResults.has(result) && result.error.details?.command_bridge_request_issued === true && evidenced.status === "error") {
      return { ...evidenced, error: { ...evidenced.error, details: { ...evidenced.error.details,
        status_source: "live_owner_bridge", operation_owner_verified: true, owner_endpoint_contacted: true, status_returned: false,
      } } };
    }
    return evidenced;
  }
  context.statisticsCommandDispatched = true;
  let data: Record<string, unknown>;
  try {
    data = validateBoundLiveTestRunStatusData(binding, binding.operation_id, result.data);
  } catch (error) {
    const reason = error instanceof ProjectArtifactReadError ? error.reason : "artifact_identity_mismatch";
    return fail({
      ...optionalRequestId(args.request_id),
      project: binding.project,
      command: args.command,
      error: {
        code: "test_run_status_unavailable",
        message: "The live owner returned test.run status that failed exact persisted owner-binding validation",
        details: {
          reason,
          operation_id: binding.operation_id,
          operation_owner_verified: true,
          owner_endpoint_contacted: true,
          command_bridge_request_issued: true,
          test_run_replayed: false,
          write_command_repeated: false,
          write_replay_allowed: false,
          ...testRunRequestIssuanceEvidence(binding, requestIssuance, true),
        },
      },
      next_valid_actions: ["inspect_the_live_owner_test_run_status_implementation", "call_diagnostics.report_problem", "do_not_repeat_test.run"],
    });
  }
  const { artifact_refs: _artifactRefs, next_valid_actions: _nextValidActions, truncated: _truncated, ...trustedResult } = result;
  return {
    ...trustedResult,
    data: {
      ...data,
      status_source: "live_owner_bridge",
      operation_owner_verified: true,
      owner_endpoint_contacted: true,
      test_run_replayed: false,
      write_command_repeated: false,
      write_replay_allowed: false,
      ...testRunRequestIssuanceEvidence(binding, requestIssuance),
    },
    artifact_refs: [binding.artifact_path],
  };
}

function withTestRunRequestIssuanceEvidence(result: GatewayResult, binding: TestRunOperationBinding, requestIssuance: TestRunRequestIssuanceLookup): GatewayResult {
  if (result.status === "ok") {
    return { ...result, data: mergeRecoveryData(result.data, testRunRequestIssuanceEvidence(binding, requestIssuance)) };
  }
  return {
    ...result,
    error: {
      ...result.error,
      details: { ...result.error.details, ...testRunRequestIssuanceEvidence(binding, requestIssuance, true) },
    },
  };
}

function testRunStatusIssuanceUnavailable(args: UnityCallArgs, binding: TestRunOperationBinding, evidenceSource: "durable_request_issuance_evidence_invalid" | "durable_request_issuance_storage_unavailable"): GatewayResult {
  return fail({
    ...optionalRequestId(args.request_id),
    project: binding.project,
    command: args.command,
    error: {
      code: "test_run_status_unavailable",
      message: evidenceSource === "durable_request_issuance_evidence_invalid"
        ? "The persisted test.run issuance evidence failed exact owner-binding validation"
        : "The persisted test.run issuance evidence is unavailable from durable storage",
      details: {
        reason: "operation_issuance_unavailable",
        operation_id: binding.operation_id,
        operation_owner_verified: true,
        owner_endpoint_contacted: false,
        test_run_replayed: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_bridge_request_issued: false,
        ...testRunRequestIssuanceEvidence(binding, { status: "unavailable", evidence_source: evidenceSource }, true),
      },
    },
    next_valid_actions: ["inspect_the_private_test_run_operation_store", "call_diagnostics.report_problem", "do_not_repeat_test.run"],
  });
}

function testRunStatusTerminalNoExecutionUnavailable(args: UnityCallArgs, binding: TestRunOperationBinding, requestIssuance: Extract<TestRunRequestIssuanceLookup, { status: "found" }>, evidenceSource: "durable_terminal_no_execution_evidence_invalid" | "durable_terminal_no_execution_storage_unavailable"): GatewayResult {
  return fail({
    ...optionalRequestId(args.request_id),
    project: binding.project,
    command: args.command,
    error: {
      code: "test_run_status_unavailable",
      message: evidenceSource === "durable_terminal_no_execution_evidence_invalid"
        ? "The persisted terminal test.run no-execution evidence failed exact owner and issuance validation"
        : "The persisted terminal test.run no-execution evidence is unavailable from durable storage",
      details: {
        reason: "terminal_no_execution_outcome_unavailable",
        operation_id: binding.operation_id,
        operation_owner_verified: true,
        owner_endpoint_contacted: false,
        test_run_delivery_state: "uncertain",
        terminal_no_execution_evidence_source: evidenceSource,
        status_or_artifact_check_required_before_repeat: true,
        repeat_blocked_until_delivery_state_checked: true,
        test_run_replayed: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        ...testRunRequestIssuanceEvidence(binding, requestIssuance, true),
      },
    },
    next_valid_actions: ["inspect_the_private_test_run_operation_store", "call_diagnostics.report_problem", "do_not_repeat_test.run"],
  });
}

function testRunStatusUnavailable(args: UnityCallArgs, operationId: string, reason: "operation_binding_not_found" | "operation_binding_unavailable", ownerVerified: boolean, nativeModalRecovery?: Record<string, unknown>): GatewayResult {
  return fail({
    ...optionalRequestId(args.request_id),
    project: args.project,
    command: args.command,
    error: {
      code: "test_run_status_unavailable",
      message: "The exact persisted test.run status is unavailable",
      details: {
        ...preDispatchNonDeliveryDetails(),
        reason,
        operation_id: operationId,
        operation_owner_verified: ownerVerified,
        owner_endpoint_contacted: false,
        test_run_replayed: false,
        write_command_repeated: false,
        ...(nativeModalRecovery ? { native_modal_recovery: nativeModalRecovery } : {}),
      },
    },
  });
}

function boundTestRunNativeModalRecoveryDetails(context: DispatcherRuntimeContext, binding: TestRunOperationBinding): Record<string, unknown> | undefined {
  const status = safeUnityWindowStatus(context);
  if (!status) return undefined;
  const lowDiskCandidates = (status.low_disk_space_modal_candidates ?? [])
    .filter((modal) => modal.block_code === "blocked_low_disk_space"
      && modal.pid === binding.origin_unity_pid
      && modal.process_start_utc === binding.origin_unity_process_start_utc)
    .slice(0, 3);
  if (lowDiskCandidates.length > 0) {
    return {
      blocking_modal_code: "blocked_low_disk_space",
      correlated_native_modal_candidates: lowDiskCandidates,
      likely_explains_test_status_unavailable: true,
      sole_cause_proven: false,
      response_supported: false,
      disk_cleanup_supported: false,
      test_run_replayed: false,
      write_command_repeated: false,
      recovery_hint: "The exact test.run owner process has one or more fully evidenced Low disk space dialogs. Do not click, focus or close the dialog, free disk space, delete Library/cache files, terminate or restart Unity, or repeat test.run. After operator resolution, require a fresh heartbeat, catalog, and this exact operation status.",
      next_valid_actions: ["call_diagnostics.unity_modal.status", "operator_review_low_disk_space_dialog", "do_not_click_focus_or_close_low_disk_dialog_from_gateway", "do_not_free_disk_or_delete_library_or_cache_from_gateway", "do_not_terminate_or_restart_unity", "poll_exact_test.run.status_after_fresh_heartbeat", "do_not_repeat_test.run"],
    };
  }
  const sceneBackupCandidates = (status.scene_backup_recovery_modal_candidates ?? [])
    .filter((modal) => modal.block_code === "blocked_scene_backup_recovery"
      && modal.pid === binding.origin_unity_pid
      && modal.process_start_utc === binding.origin_unity_process_start_utc)
    .slice(0, 3);
  if (sceneBackupCandidates.length > 0) {
    return {
      blocking_modal_code: "blocked_scene_backup_recovery",
      correlated_native_modal_candidates: sceneBackupCandidates,
      likely_explains_test_status_unavailable: true,
      sole_cause_proven: false,
      response_supported: false,
      test_run_replayed: false,
      write_command_repeated: false,
      recovery_hint: "The exact test.run owner process has one or more fully evidenced Recovering Scene Backups dialogs. Do not click Yes or No, focus, terminate or restart Unity, or repeat test.run. After operator resolution, require a fresh heartbeat, catalog, and this exact operation status.",
      next_valid_actions: ["call_diagnostics.unity_modal.status", "operator_review_scene_backup_recovery_dialog", "do_not_click_yes_or_no_from_gateway", "do_not_focus_terminate_or_restart_unity", "poll_exact_test.run.status_after_fresh_heartbeat", "do_not_repeat_test.run"],
    };
  }
  const candidates = (status.scoped_registry_import_modal_candidates ?? [])
    .filter((modal) => modal.block_code === "blocked_scoped_registry_import"
      && modal.pid === binding.origin_unity_pid
      && modal.process_start_utc === binding.origin_unity_process_start_utc)
    .slice(0, 3);
  if (candidates.length === 0) return undefined;
  return {
    blocking_modal_code: "blocked_scoped_registry_import",
    correlated_native_modal_candidates: candidates,
    likely_explains_test_status_unavailable: true,
    sole_cause_proven: false,
    response_supported: false,
    test_run_replayed: false,
    write_command_repeated: false,
    recovery_hint: "The exact test.run owner process has one or more fully evidenced Importing a scoped registry dialogs. Do not respond, terminate or restart Unity, or repeat test.run. After operator resolution, require a fresh heartbeat, catalog, and this exact operation status.",
    next_valid_actions: ["call_diagnostics.unity_modal.status", "operator_review_scoped_registry_import_dialog", "do_not_click_learn_more_or_close_from_gateway", "do_not_focus_terminate_or_restart_unity", "poll_exact_test.run.status_after_fresh_heartbeat", "do_not_repeat_test.run"],
  };
}

type ResolvedInvocation = { record?: HeartbeatRecord; command: CommandDescriptor; commandsHash: string | null; project: string; virtual?: boolean; official?: boolean };

async function resolveInvocationCommand(context: DispatcherRuntimeContext, project: string, commandId: string, requestId?: string, commandArgs: Record<string, unknown> = {}, transport?: BridgeTransportOptions, expectedRecord?: HeartbeatRecord): Promise<ResolvedCommand<ResolvedInvocation>> {
  const resolution: ProjectResolution = expectedRecord
    ? { status: "ok", record: freezeInvocationRecord(expectedRecord) }
    : resolveProject(context, project, { allowStale: allowsStaleProjectResolution(commandId) });
  if (resolution.status === "error") {
    const staleProjectKey = collapsedStaleProjectKey(resolution);
    const diagnosticsCommand = findDiagnosticsCommand(commandId);
    if (diagnosticsCommand && isOfflineProjectOpenCommand(commandId) && resolution.code === "unsafe_binding_refused") {
      return { state: "blocked", result: projectResolutionError(project, resolution, commandId, requestId, context.registry, commandArgs, context) };
    }
    if (diagnosticsCommand && (commandId === PROJECT_DOWNGRADE_CONTINUE_COMMAND || commandId === SCOPED_REGISTRY_IMPORT_CLOSE_COMMAND || commandId === SAFE_MODE_ENTER_COMMAND || commandId === SCENE_BACKUP_KEEP_COMMAND)) {
      const identity = noHeartbeatProjectDowngradeIdentity(context, project, commandArgs.canonical_project_root, commandId === SCENE_BACKUP_KEEP_COMMAND);
      if (identity.status === "error") {
        return {
          state: "blocked",
          result: fail({
            ...optionalRequestId(requestId),
            command: commandId,
            error: {
              code: "unsafe_binding_refused",
              message: commandId === PROJECT_DOWNGRADE_CONTINUE_COMMAND
                ? "No-heartbeat project downgrade routing requires one exact known project identity for the canonical root"
                : commandId === SAFE_MODE_ENTER_COMMAND
                  ? "No-heartbeat Safe Mode routing requires one exact known project identity for the canonical root"
                  : commandId === SCENE_BACKUP_KEEP_COMMAND
                    ? "No-heartbeat scene-backup response routing requires one exact known project identity for the canonical root"
                    : "No-heartbeat scoped-registry response routing requires one exact known project identity for the canonical root",
              details: {
                ...preDispatchNonDeliveryDetails(),
                project_identity_mapping: identity.reason,
                matching_project_id_count: identity.projectIdCount,
                approval_requested: false,
              },
            },
          }),
        };
      }
      return resolvedCommand(identity.projectId, diagnosticsCommand, undefined, true);
    }
    if (diagnosticsCommand && isOfflineProjectOpenCommand(commandId) && resolution.matches.length > 0 && !staleProjectKey) {
      const codeOverride = resolution.code === "project_stale" ? "duplicate_project_id" : undefined;
      return { state: "blocked", result: projectResolutionError(project, resolution, commandId, requestId, context.registry, commandArgs, context, codeOverride) };
    }
    if (diagnosticsCommand && (diagnosticsCommand.risk === "read" || commandId === UNITY_PROJECT_OPEN_COMMAND || commandId === DIAGNOSTICS_PROBLEM_MARK_COMMAND)) {
      return resolvedCommand(staleProjectKey ?? project, diagnosticsCommand, undefined, true);
    }
    const result = projectResolutionError(project, resolution, commandId, requestId, context.registry, commandArgs, context);
    if (commandId === TEST_RUN_COMMAND) {
      const staleResolution = resolveProject(context, project, { allowStale: true });
      if (staleResolution.status === "ok") preDispatchRecoveryRecords.set(result, staleResolution.record);
    }
    return { state: "blocked", result };
  }
  const projectKey = resolution.record.project;
  const virtualCommand = findVirtualCommand(commandId);
  if (virtualCommand) {
    return resolvedCommand(projectKey, virtualCommand, resolution.record.commands_hash, true, resolution.record);
  }
  if (officialToolName(commandId)) {
    const officialCommand = await context.officialRelay?.findCommandDescriptor(projectKey, commandId);
    if (!officialCommand) {
      return { state: "blocked", result: commandNotFound(projectKey, commandId, requestId) };
    }
    const resolved = resolvedCommand(projectKey, officialCommand, resolution.record.commands_hash, false, resolution.record);
    return { state: "resolved", value: { ...resolved.value, official: true } };
  }
  if (!context.bridge) {
    return { state: "blocked", result: bridgeUnavailable(project, commandId, requestId) };
  }
  const lifecycleGate = bridgeLifecycleGateResult({
    project: projectKey,
    projectSelector: project,
    command: commandId,
    requestId,
    record: resolution.record,
    registry: context.registry,
  }, context);
  if (lifecycleGate) {
    preDispatchRecoveryRecords.set(lifecycleGate, resolution.record);
    return { state: "blocked", result: lifecycleGate };
  }
  let command: CommandDescriptor | undefined;
  try {
    const resolved = await resolveCommandDescriptor(context.bridge, resolution.record, commandId, transport);
    command = resolved?.command;
    if (command) {
      return resolvedCommand(projectKey, command, resolved?.commandsHash ?? resolution.record.commands_hash, false, resolution.record);
    }
  } catch (error) {
    const bridgeError = bridgeThrownError(error, { project: projectKey, projectSelector: project, command: commandId, requestId, record: resolution.record, registry: context.registry }, context);
    if (bridgeError) {
      const notDispatched = withCommandNotDispatched(bridgeError);
      preDispatchRecoveryRecords.set(notDispatched, resolution.record);
      return { state: "blocked", result: notDispatched };
    }
    throw error;
  }
  if (!command) {
    return { state: "blocked", result: commandNotFound(projectKey, commandId, requestId) };
  }
  return { state: "blocked", result: commandNotFound(projectKey, commandId, requestId) };
}

function canonicalProjectIdentityForPolicy(value: unknown): string | undefined {
  if (typeof value !== "string" || value.length === 0) return undefined;
  try {
    return canonicalPolicyPaths.normalizeProjectRoot(value);
  } catch {
    return undefined;
  }
}

function noHeartbeatProjectDowngradeIdentity(context: DispatcherRuntimeContext, projectSelector: string, requestedRootValue: unknown, allowExactStableSelector = false): { status: "ok"; projectId: string } | { status: "error"; reason: "canonical_root_mismatch" | "project_identity_unavailable" | "project_identity_ambiguous"; projectIdCount: number } {
  const selectorRoot = canonicalProjectIdentityForPolicy(projectSelector);
  const requestedRoot = canonicalProjectIdentityForPolicy(requestedRootValue);
  if (!requestedRoot || (!allowExactStableSelector && selectorRoot !== requestedRoot)) return { status: "error", reason: "canonical_root_mismatch", projectIdCount: 0 };
  let records: PublicProjectRecord[];
  try {
    records = context.registry?.listProjectRecords() ?? [];
  } catch {
    return { status: "error", reason: "project_identity_unavailable", projectIdCount: 0 };
  }
  const projectIds = new Set(records
    .filter((record) => record.canonical_project_root === requestedRoot || record.aliases?.some((alias) => alias.canonical_path === requestedRoot))
    .map((record) => record.project)
    .filter((projectId) => typeof projectId === "string" && projectId.length > 0));
  if (projectIds.size === 0) return { status: "error", reason: "project_identity_unavailable", projectIdCount: 0 };
  if (projectIds.size !== 1) return { status: "error", reason: "project_identity_ambiguous", projectIdCount: projectIds.size };
  const projectId = [...projectIds][0]!;
  if (selectorRoot !== requestedRoot) {
    // Only this dedicated rescue supports a stable ID across multiple historical
    // generations. Never collapse the same ID spanning different project roots.
    if (!allowExactStableSelector || projectSelector !== projectId) return { status: "error", reason: "canonical_root_mismatch", projectIdCount: 1 };
    if (records.some((record) => record.project === projectId && record.canonical_project_root !== requestedRoot)) return { status: "error", reason: "project_identity_ambiguous", projectIdCount: 1 };
  }
  return { status: "ok", projectId };
}

function resolvedCommand(project: string, command: CommandDescriptor, commandsHash: string | undefined, virtual: boolean, record?: HeartbeatRecord): Extract<ResolvedCommand<ResolvedInvocation>, { state: "resolved" }> {
  return { state: "resolved", value: { ...(record ? { record } : {}), command, commandsHash: commandsHash ?? null, project, ...(virtual ? { virtual: true } : {}) } };
}

function applyInvocationPolicy(context: DispatcherRuntimeContext, commandId: string, requestId: string | undefined, resolved: ResolvedInvocation, projectSelector: string, blockDegradedRead = false): InvocationPolicyDecision<ResolvedInvocation> {
  const { project: projectKey, command, commandsHash } = resolved;
  const degradedRecovery = isDegradedPolicyRecoveryCommand(context, commandId);
  if (!degradedRecovery && !isFeatureEnabled(command, context.features)) {
    return { state: "blocked", result: featureDisabled(projectKey, commandId, command.feature_flag, requestId) };
  }
  recordResolvedCommandAuditMetadata(context, resolved);
  if (!isValidCommandTimeout(command.timeout_ms)) {
    return { state: "blocked", result: invalidCommandTimeout(resolved.record ? projectKey : undefined, resolved.record ? projectSelector : undefined, commandId, command.timeout_ms, requestId) };
  }
  context.auditCommandMetadata!.command_revision = commandDescriptorRevision(command);
  const degraded = degradedPolicyGate(context, projectKey, commandId, requestId, command.risk, blockDegradedRead);
  if (degraded) {
    return { state: "blocked", result: degraded };
  }
  if (degradedRecovery) {
    delete context.auditPolicyMetadata;
    return { state: "permitted", value: resolved };
  }
  const policyDecision = evaluatePolicy(context.policy, projectKey, command);
  context.auditPolicyMetadata = policyDecision;
  if (policyDecision.action === "deny") {
    return { state: "blocked", result: permissionDenied(projectKey, commandId, requestId, policyDecision) };
  }

  return { state: "permitted", value: resolved };
}

function recordResolvedCommandAuditMetadata(context: DispatcherRuntimeContext, resolved: ResolvedInvocation): void {
  const { command, commandsHash } = resolved;
  context.auditCommandMetadata = {
    risk: command.risk,
    idempotency: command.idempotency,
    timeout_ms: command.timeout_ms,
    ...(command.source ? { source: command.source } : {}),
    ...(command.package_id || command.package ? { package_id: command.package_id ?? command.package } : {}),
    ...(command.package_version ? { package_version: command.package_version } : {}),
    ...(command.manifest_path ? { manifest_path: command.manifest_path } : {}),
    ...(command.trust ? { trust: command.trust } : {}),
    ...(commandsHash ? { commands_hash: commandsHash } : {}),
  };
}

type PermissionCheck = {
  status: "ok";
  approvalMode?: "temporary_auto_allow_without_native_elicitation";
} | { status: "error"; result: GatewayResult };

type UnityCallArgs = {
  project: string;
  command: string;
  args_json: string;
  mode: "wait" | "job";
  request_id?: string | undefined;
  checkpoint_id?: string | undefined;
  plan_id?: string | undefined;
  wait_for_replacement_ms?: number | undefined;
  wait_for_test_run_terminal_ms?: number | undefined;
};

type UnityDescribeArgs = {
  project: string;
  selector: string;
};

function recoveryHandlers(input: {
  read: () => Promise<GatewayResult>;
  riskAware?: () => Promise<GatewayResult>;
  compileRefresh?: () => Promise<GatewayResult>;
  validationAfterEdit?: () => Promise<GatewayResult>;
  noAutomaticRecovery: () => Promise<GatewayResult>;
}): RecoveryHandlers {
  return {
    read: input.read,
    risk_aware: input.riskAware ?? input.noAutomaticRecovery,
    compile_refresh: input.compileRefresh ?? input.noAutomaticRecovery,
    validation_after_edit: input.validationAfterEdit ?? input.noAutomaticRecovery,
    no_automatic_recovery: input.noAutomaticRecovery,
  };
}

async function describeUnityWithBridgeRecovery(context: DispatcherRuntimeContext, args: UnityDescribeArgs): Promise<GatewayResult> {
  const retry = { timeoutMs: DEFAULT_READ_RECOVERY_TIMEOUT_MS, pollIntervalMs: DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS };
  const startedAtMs = nowMs(context);
  const deadlineMs = earlierDeadline(startedAtMs + retry.timeoutMs, context.internalDeadlineUnixMs);
  if (context.internalDeadlineUnixMs !== deadlineMs) {
    return withInternalDeadline(context, deadlineMs, () => describeUnityWithBridgeRecovery(context, args));
  }
  let attempts = 0;
  let lastResult: GatewayResult | undefined;

  while (true) {
    attempts += 1;
    lastResult = await describeUnityOnce(context, args, recoveryTransport(context, deadlineMs));
    if (!isDescribeRecoveryRetryableError(lastResult, context)) {
      return lastResult;
    }

    const remainingMs = deadlineMs - nowMs(context);
    if (remainingMs <= 0) {
      return bridgeRecoveryRetryExhausted(lastResult, attempts, retry.timeoutMs, Math.max(0, nowMs(context) - startedAtMs), { tool: "sgg_unity_describe", selector: args.selector });
    }

    await sleepMs(context, Math.min(retry.pollIntervalMs, remainingMs));
  }
}

async function describeUnityOnce(context: DispatcherRuntimeContext, args: UnityDescribeArgs, transport?: BridgeTransportOptions): Promise<GatewayResult> {
  const resolution = resolveProject(context, args.project, { allowStale: isSavePromptDescribeSelector(args.selector) });
  if (resolution.status === "error") {
    const staleProjectKey = collapsedStaleProjectKey(resolution);
    if (resolution.code === "project_stale" || resolution.code === "project_not_found" || staleProjectKey) {
      if (staleOnlyProjectResolution(resolution) && !staleProjectKey) {
        const codeOverride = resolution.code === "project_stale" ? "duplicate_project_id" : undefined;
        return projectResolutionError(args.project, resolution, undefined, undefined, context.registry, {}, context, codeOverride);
      }
      const diagnosticsProject = staleProjectKey ?? args.project;
      const gatewayLocalDiagnostics = describeGatewayLocalDiagnosticsCommand(context, diagnosticsProject, args.selector);
      if (gatewayLocalDiagnostics) {
        const missingCommand = missingCommandDescribeResult(diagnosticsProject, args.selector, gatewayLocalDiagnostics);
        return missingCommand ?? ok({ project: diagnosticsProject, data: gatewayLocalDiagnostics });
      }
    }
    return projectResolutionError(args.project, resolution, undefined, undefined, context.registry, {}, context);
  }
  const projectKey = resolution.record.project;
  const gatewayLocalDiagnostics = describeGatewayLocalDiagnosticsCommand(context, projectKey, args.selector);
  if (gatewayLocalDiagnostics) {
    const missingCommand = missingCommandDescribeResult(projectKey, args.selector, gatewayLocalDiagnostics);
    return missingCommand ?? ok({ project: projectKey, data: gatewayLocalDiagnostics });
  }
  if (!context.bridge) {
    return bridgeUnavailable(args.project);
  }
  const officialDescribe = await describeOfficialCommand(context, projectKey, args.selector);
  if (officialDescribe) {
    const missingCommand = missingCommandDescribeResult(projectKey, args.selector, officialDescribe);
    if (missingCommand) {
      return missingCommand;
    }
    return ok({ project: projectKey, data: officialDescribe });
  }
  const virtualDescribe = describeVirtualCommand(context, projectKey, args.selector);
  if (virtualDescribe) {
    const missingCommand = missingCommandDescribeResult(projectKey, args.selector, virtualDescribe);
    if (missingCommand) {
      return missingCommand;
    }
    return ok({ project: projectKey, data: virtualDescribe });
  }
  const lifecycleGate = bridgeLifecycleGateResult({
    project: projectKey,
    projectSelector: args.project,
    selector: args.selector,
    record: resolution.record,
    registry: context.registry,
  }, context);
  if (lifecycleGate) {
    return lifecycleGate;
  }
  let describeData: unknown;
  try {
    describeData = await context.bridge.describe(resolution.record, args.selector, transport);
  } catch (error) {
    const bridgeError = bridgeThrownError(error, { project: projectKey, projectSelector: args.project, selector: args.selector, record: resolution.record, registry: context.registry }, context);
    if (bridgeError) {
      return bridgeError;
    }
    throw error;
  }
  const missingCommand = missingCommandDescribeResult(projectKey, args.selector, describeData);
  if (missingCommand) {
    return missingCommand;
  }
  const officialCommands = await officialRelayCommands(context, projectKey, args.selector);
  return ok({ project: projectKey, data: applyDescribePolicy(context, projectKey, describeData, args.selector, officialCommands) });
}

type InvokeUnityCallOnceOptions = { permissionAlreadyChecked?: boolean };

async function invokeUnityCallOnce(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>, recoveryDeadlineUnixMs?: number, enableCompileRefreshEmptyBodyVerification = false, transportCommandArgs?: Record<string, unknown>, queueOwned = false, expectedRecord?: HeartbeatRecord, expectedCommandsHash?: string, expectedCommandRevision?: string, preBridgeDispatchGuard?: () => GatewayResult | undefined, initialBridgeDeadlineUnixMs?: number, options: InvokeUnityCallOnceOptions = {}): Promise<GatewayResult> {
  if (recoveryDeadlineUnixMs !== undefined) {
    const effectiveDeadlineUnixMs = earlierDeadline(recoveryDeadlineUnixMs, context.internalDeadlineUnixMs);
    if (context.internalDeadlineUnixMs !== effectiveDeadlineUnixMs) {
      return withInternalDeadline(context, effectiveDeadlineUnixMs, () => invokeUnityCallOnce(
        context,
        args,
        commandArgs,
        effectiveDeadlineUnixMs,
        enableCompileRefreshEmptyBodyVerification,
        transportCommandArgs,
        queueOwned,
        expectedRecord,
        expectedCommandsHash,
        expectedCommandRevision,
        preBridgeDispatchGuard,
        initialBridgeDeadlineUnixMs,
        options,
      ));
    }
    recoveryDeadlineUnixMs = effectiveDeadlineUnixMs;
  }

  if (args.command === BUILD_RUN_COMMAND && (Object.hasOwn(commandArgs, "_build_run_operation_id") || Object.hasOwn(commandArgs, "_build_run_caller_args_hash"))) {
    const invalidFieldPaths = ["_build_run_operation_id", "_build_run_caller_args_hash"]
      .filter((field) => Object.hasOwn(commandArgs, field))
      .map((field) => `args.${field}`);
    return fail({
      ...optionalRequestId(args.request_id),
      command: args.command,
      error: {
        code: "schema_validation_failed",
        message: "Gateway-reserved build.run correlation fields cannot be supplied by callers",
        details: { invalid_field_paths: invalidFieldPaths, caller_correlation_override_rejected: true },
      },
    });
  }
  if (args.command === TEST_RUN_COMMAND && (Object.hasOwn(commandArgs, "_test_run_operation_id") || Object.hasOwn(commandArgs, "_test_run_caller_args_hash") || Object.hasOwn(commandArgs, "_test_discovery_generation"))) {
    const invalidFieldPaths = ["_test_run_operation_id", "_test_run_caller_args_hash", "_test_discovery_generation"]
      .filter((field) => Object.hasOwn(commandArgs, field))
      .map((field) => `args.${field}`);
    return fail({
      ...optionalRequestId(args.request_id),
      command: args.command,
      error: {
        code: "schema_validation_failed",
        message: "Gateway-reserved test.run correlation fields cannot be supplied by callers",
        details: { invalid_field_paths: invalidFieldPaths, caller_correlation_override_rejected: true },
      },
    });
  }
  if (args.command === DYNAMIC_CSHARP_READONLY_COMMAND && (Object.hasOwn(commandArgs, "_dynamic_csharp_operation_id") || Object.hasOwn(commandArgs, "_dynamic_csharp_caller_args_hash") || Object.hasOwn(commandArgs, "_dynamic_csharp_queue_wait_ms"))) {
    const invalidFieldPaths = ["_dynamic_csharp_operation_id", "_dynamic_csharp_caller_args_hash", "_dynamic_csharp_queue_wait_ms"]
      .filter((field) => Object.hasOwn(commandArgs, field))
      .map((field) => `args.${field}`);
    return fail({
      ...optionalRequestId(args.request_id),
      command: args.command,
      error: {
        code: "schema_validation_failed",
        message: "Gateway-reserved dynamic C# read-only correlation fields cannot be supplied by callers",
        details: { invalid_field_paths: invalidFieldPaths, caller_correlation_override_rejected: true },
      },
    });
  }
  if (args.command === EDITOR_MENU_OPERATION_START_COMMAND && (Object.hasOwn(commandArgs, "_editor_menu_operation_id") || Object.hasOwn(commandArgs, "_editor_menu_caller_args_hash"))) {
    const invalidFieldPaths = ["_editor_menu_operation_id", "_editor_menu_caller_args_hash"]
      .filter((field) => Object.hasOwn(commandArgs, field))
      .map((field) => `args.${field}`);
    return fail({
      ...optionalRequestId(args.request_id),
      command: args.command,
      error: {
        code: "schema_validation_failed",
        message: "Gateway-reserved editor menu operation correlation fields cannot be supplied by callers",
        details: { invalid_field_paths: invalidFieldPaths, caller_correlation_override_rejected: true },
      },
    });
  }
  if (args.command === EDITOR_QUIT_COMMAND && (Object.hasOwn(commandArgs, "_editor_quit_operation_id") || Object.hasOwn(commandArgs, "_editor_quit_caller_args_hash"))) {
    const invalidFieldPaths = ["_editor_quit_operation_id", "_editor_quit_caller_args_hash"]
      .filter((field) => Object.hasOwn(commandArgs, field))
      .map((field) => `args.${field}`);
    return fail({
      ...optionalRequestId(args.request_id),
      command: args.command,
      error: {
        code: "schema_validation_failed",
        message: "Gateway-reserved editor quit correlation fields cannot be supplied by callers",
        details: { invalid_field_paths: invalidFieldPaths, caller_correlation_override_rejected: true },
      },
    });
  }
  const plan = await buildInvocationPlan({
    resolve: () => resolveInvocationCommand(
      context,
      args.project,
      args.command,
      args.request_id,
      commandArgs,
      recoveryDeadlineUnixMs === undefined ? undefined : recoveryTransport(context, recoveryDeadlineUnixMs, args.request_id),
      expectedRecord,
    ),
    applyPolicy: (resolved) => {
      refreshManagedPolicy(context);
      return applyInvocationPolicy(context, args.command, args.request_id, resolved, args.project);
    },
    validate: (permitted) => {
      if (expectedCommandsHash !== undefined && permitted.commandsHash !== expectedCommandsHash) {
        return {
          state: "blocked",
          result: fail({
            ...optionalRequestId(args.request_id),
            project: permitted.project,
            command: args.command,
            error: {
              code: "catalog_stale",
              message: "Replacement instance re-described a different command catalog before dispatch",
              details: preDispatchNonDeliveryDetails(),
            },
          }),
        };
      }
      if (expectedCommandRevision !== undefined && commandDescriptorRevision(permitted.command) !== expectedCommandRevision) {
        return {
          state: "blocked",
          result: fail({
            ...optionalRequestId(args.request_id),
            project: permitted.project,
            command: args.command,
            error: {
              code: "catalog_stale",
              message: "Replacement instance command descriptor changed before dispatch",
              details: preDispatchNonDeliveryDetails(),
            },
          }),
        };
      }
      const wrapperWaitValidation = validateDescriptorBackedWrapperWait(context, args, commandArgs, permitted.command);
      if (wrapperWaitValidation) {
        return { state: "blocked", result: wrapperWaitValidation };
      }
      const validation = validateCommandArgs(permitted.command.schema, commandArgs, isTrustedBuiltInDescriptor(permitted.command));
      if (validation.status === "schema_unavailable") {
        return { state: "blocked", result: schemaUnavailable(permitted.record ? permitted.project : undefined, permitted.record ? args.project : undefined, args.command, args.request_id) };
      }
      if (validation.status === "error") {
        return { state: "blocked", result: commandSchemaValidationFailed(permitted.record ? permitted.project : undefined, permitted.record ? args.project : undefined, args.command, args.request_id, validation.issues, permitted.command.schema, commandArgs, permitted.command) };
      }
      return new ReadyInvocationPlan(InvocationContextSnapshot.create({
        projectSelector: args.project,
        project: permitted.project,
        command: args.command,
        descriptor: permitted.command,
        commandsHash: permitted.commandsHash,
        ...(permitted.record ? { record: permitted.record } : {}),
      }), permitted);
    },
    approve: (invocation) => {
      if (options.permissionAlreadyChecked) return { state: "approved" };
      const permission = checkInvocationPermission(context, invocation, args.mode, args.request_id, commandArgs);
      return permission.status === "error" ? { state: "blocked", result: permission.result } : { state: "approved" };
    },
  });
  if (plan.state === "blocked") {
    return plan.result;
  }
  const invocation = plan.invocation;
  const prepared = plan.resolved;
  const effectiveCommandArgs = transportCommandArgs ?? commandArgs;
  const externalSceneBlocker = prepared.record ? externalSceneReloadBlockerResult(context, [prepared.record], {
    project: prepared.project,
    projectSelector: args.project,
    command: args.command,
    requestId: args.request_id,
    writeRequestIssued: false,
  }) : undefined;
  if (externalSceneBlocker) return externalSceneBlocker;

  return new InvocationRoute(args.mode).execute({
    wait: () => {
      if (args.command === EDITOR_QUIT_COMMAND && effectiveCommandArgs.dry_run === false && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true) {
        const operationId = createEditorQuitOperationId(context);
        const callerArgsHash = argsHash(commandArgs);
        const quitArgs = { ...effectiveCommandArgs, _editor_quit_operation_id: operationId, _editor_quit_caller_args_hash: callerArgsHash };
        if (validateCommandArgs(prepared.command.schema, quitArgs, isTrustedBuiltInDescriptor(prepared.command)).status !== "ok") {
          return fail({
            ...optionalRequestId(args.request_id),
            project: invocation.identity.project,
            command: args.command,
            error: {
              code: "catalog_stale",
              message: "editor.quit catalog does not support operation-correlated lifecycle verification",
              details: { ...preDispatchNonDeliveryDetails(), automatic_second_quit_attempted: false },
            },
          });
        }
        let requestIssued = false;
        return runInProjectQueue(context, invocation.identity.project, async () => {
          const result = await invokePreparedCommand(context, invocation, args.request_id, quitArgs, recoveryDeadlineUnixMs, true, () => { requestIssued = true; });
          return reconcileEditorQuit(context, invocation, args.request_id, commandArgs, result, operationId, callerArgsHash, requestIssued);
        }, false, projectQueueOwner(invocation, earlierDeadline(commandDeadlineUnixMs(context, prepared.command.timeout_ms), recoveryDeadlineUnixMs)));
      }
      if (args.command === BUILD_RUN_COMMAND && effectiveCommandArgs.dry_run !== true && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true && prepared.command.tags?.includes(BUILD_RUN_CORRELATION_TAG)) {
        const operationId = createBuildRunOperationId(context);
        const buildRunCommandArgs = { ...effectiveCommandArgs, _build_run_operation_id: operationId, _build_run_caller_args_hash: argsHash(commandArgs) };
        return startCorrelatedBuildRun(context, invocation, args.request_id, buildRunCommandArgs, operationId, recoveryDeadlineUnixMs);
      }
      if (args.command === TEST_RUN_COMMAND && effectiveCommandArgs.dry_run !== true && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true && prepared.command.tags?.includes(TEST_RUN_CORRELATION_TAG)) {
        const startupStartedAtMs = nowMs(context);
        const queueDeadlineUnixMs = earlierDeadline(commandDeadlineUnixMs(context, prepared.command.timeout_ms), recoveryDeadlineUnixMs);
        const requiresDurableBinding = prepared.command.tags?.includes(TEST_RUN_ARTIFACT_CAPABILITY) === true;
        const bindingPublicationReserveMs = requiresDurableBinding ? TEST_RUN_BINDING_PUBLICATION_RESERVE_MS : 0;
        const discoveryDeadlineUnixMs = queueDeadlineUnixMs - bindingPublicationReserveMs;
        const compileReadinessSelected = args.wait_for_test_run_terminal_ms !== undefined
          && prepared.command.wrapper_wait?.wait_for_test_run_terminal_ms?.compile_readiness_contract === TEST_RUN_COMPILE_READINESS_CONTRACT;
        // Approval was checked by the plan. Freeze its effective decision rather
        // than consuming it a second time after the read-only preflight.
        const readinessPolicyRevision = effectivePolicyRevision(evaluatePolicy(context.policy, invocation.identity.project, invocation.descriptor));
        const operationId = createTestRunOperationId(context);
        const callerArgsHash = argsHash(commandArgs);
        const delivery: { queueAdmitted: boolean; queueExpired: boolean; discoveryPending: boolean; writeRequestIssued: boolean; responseDeadlineUnixMs?: number; bookkeepingPendingResult?: GatewayResult } = { queueAdmitted: false, queueExpired: false, discoveryPending: false, writeRequestIssued: false };
        const queued = runInProjectQueue(context, invocation.identity.project, async () => {
          if (delivery.queueExpired || queueDeadlineUnixMs <= nowMs(context)) return testRunQueueTimeout(invocation, args.request_id, prepared.command.timeout_ms, operationId);
          delivery.queueAdmitted = true;
          delivery.discoveryPending = true;
          let phaseStartedAtMs = nowMs(context);
          const timingFailure = (result: GatewayResult, phase: "compile_readiness" | "discovery" | "binding_prepare") => withTestRunStartupTiming(result, {
            phase, phaseStartedAtMs, startupStartedAtMs, observedAtMs: nowMs(context), deadlineUnixMs: queueDeadlineUnixMs,
            descriptorTimeoutMs: prepared.command.timeout_ms, bindingReserveMs: bindingPublicationReserveMs,
          });
          const readiness = compileReadinessSelected
            ? await awaitTestRunCompileReadinessBeforeDispatch(context, invocation, args.request_id, operationId, discoveryDeadlineUnixMs, () => delivery.queueExpired, readinessPolicyRevision!)
            : undefined;
          if (readiness?.state === "blocked") return timingFailure(readiness.result, "compile_readiness");
          phaseStartedAtMs = nowMs(context);
          const discovery = await awaitTestRunDiscoveryBeforeDispatch(context, invocation, args.request_id, effectiveCommandArgs, operationId, discoveryDeadlineUnixMs, () => delivery.queueExpired, bindingPublicationReserveMs,
            compileReadinessSelected ? () => testRunReadinessDispatchGuard(context, invocation, args.request_id, operationId, discoveryDeadlineUnixMs, readinessPolicyRevision!) : undefined);
          if (discovery.state === "blocked") return timingFailure(discovery.result, "discovery");
          delivery.discoveryPending = false;
          if (delivery.queueExpired || discoveryDeadlineUnixMs <= nowMs(context)) return testRunDiscoveryPending(invocation, args.request_id, effectiveCommandArgs, operationId, prepared.command.timeout_ms, discovery.evidence);
          let binding: TestRunOperationBinding | undefined;
          let bindingStore: TestRunOperationStoreLike | undefined;
          let publisher: TestRunOperationPublisher | undefined;
          if (requiresDurableBinding) {
            try {
              binding = testRunOperationBinding(context, invocation, operationId, callerArgsHash);
              bindingStore = context.testRunOperationStore;
              publisher = context.testRunOperationPublisher;
              if (!bindingStore || !publisher || publisher.dir !== bindingStore.dir) throw new TestRunOperationStoreError("storage_unavailable");
            } catch (error) {
              return testRunBindingPublicationFailure(invocation, args.request_id, operationId, asTestRunOperationStoreError(error));
            }
          }
          const testRunCommandArgs = {
            ...effectiveCommandArgs,
            _test_run_operation_id: operationId,
            _test_run_caller_args_hash: callerArgsHash,
            ...(discovery.discoveryGeneration === undefined ? {} : { _test_discovery_generation: discovery.discoveryGeneration }),
          };
          const responseDeadlineUnixMs = earlierDeadline(commandDeadlineUnixMs(context, prepared.command.timeout_ms + TEST_RUN_STARTUP_RESPONSE_GRACE_MS), recoveryDeadlineUnixMs);
          const publicationBudget: TestRunPublicationBudget = { deadlineUnixMs: responseDeadlineUnixMs,
            monotonicDeadline: performance.now() + Math.max(0, responseDeadlineUnixMs - nowMs(context)) };
          delivery.responseDeadlineUnixMs = responseDeadlineUnixMs;
          let lease: TestRunOperationPublicationLease | undefined;
          let bindingPublished = false;
          const finalDispatchGuard = () => {
            const permissionBlocked = compileReadinessSelected
              ? testRunReadinessDispatchGuard(context, invocation, args.request_id, operationId, queueDeadlineUnixMs, readinessPolicyRevision)
              : testRunPublicationPermissionGuard(context, invocation, args.request_id, operationId, readinessPolicyRevision);
            if (permissionBlocked) return withTestRunBindingPublicationState(permissionBlocked, bindingPublished);
            return binding ? testRunBindingDispatchGuard(context, invocation, binding, queueDeadlineUnixMs, args.request_id, bindingPublished) : undefined;
          };
          const prepareDispatch = async (): Promise<GatewayResult | undefined> => {
            phaseStartedAtMs = nowMs(context);
            const beforePreparation = finalDispatchGuard();
            if (beforePreparation) return beforePreparation;
            if (!binding || !publisher) return undefined;
            let preCommitFailure: GatewayResult | undefined;
            try {
              lease = await publisher.prepare(binding, {
                deadlineUnixMs: queueDeadlineUnixMs,
                beforeCommit: () => {
                  preCommitFailure = finalDispatchGuard();
                  return preCommitFailure === undefined;
                },
              });
              bindingPublished = true;
            } catch (error) {
              return timingFailure(preCommitFailure ?? testRunBindingPublicationFailure(invocation, args.request_id, operationId, asTestRunOperationStoreError(error)), "binding_prepare");
            }
            return finalDispatchGuard();
          };
          try {
            let issuance: Promise<TestRunRequestIssuanceMarker> | undefined;
            const result = await invokePreparedCommand(context, invocation, args.request_id, testRunCommandArgs, responseDeadlineUnixMs, true, () => {
              delivery.writeRequestIssued = true;
              if (!lease) return;
              // Capture the attempt, then enqueue immutable bookkeeping. No filesystem
              // or lock acquisition is allowed in this after-fetch transport hook.
              try { issuance = lease.publishRequestIssuance(context.now?.() ?? new Date()); }
              catch (error) { issuance = Promise.reject(error); }
              void issuance.catch(() => undefined);
            }, prepared.command.timeout_ms + TEST_RUN_STARTUP_RESPONSE_GRACE_MS, undefined, () => {
              if (binding && (!bindingPublished || !lease)) return testRunBindingPublicationFailure(invocation, args.request_id, operationId, new TestRunOperationStoreError("storage_unavailable"));
              return finalDispatchGuard();
            }, prepareDispatch);
            if (binding && lease && issuance) {
              delivery.bookkeepingPendingResult = withTestRunDiscoveryPreflightEvidence(testRunIssuancePublicationFailure(invocation, args.request_id, binding, operationId, new TestRunPublicationDeadlineError(), result), discovery.evidence);
              let requestIssuanceMarker: TestRunRequestIssuanceMarker;
              try {
                requestIssuanceMarker = await awaitTestRunPublicationBeforeDeadline(context, issuance, publicationBudget);
              } catch (error) {
                return withTestRunDiscoveryPreflightEvidence(testRunIssuancePublicationFailure(invocation, args.request_id, binding, operationId, asTestRunOperationStoreError(error), result), discovery.evidence);
              }
              delete delivery.bookkeepingPendingResult;
              const terminalEvidence = terminalNoExecutionEvidenceFromBridgeResult(result);
              if (terminalEvidence) {
                let receipt: TestRunTerminalNoExecutionReceipt;
                try {
                  // Never enqueue a terminal follow-up after the caller budget expired.
                  if (isTestRunPublicationBudgetExpired(context, publicationBudget)) throw new TestRunPublicationDeadlineError();
                  delivery.bookkeepingPendingResult = withTestRunDiscoveryPreflightEvidence(testRunTerminalNoExecutionPublicationFailure(invocation, args.request_id, binding, requestIssuanceMarker, result, new TestRunPublicationDeadlineError()), discovery.evidence);
                  receipt = await awaitTestRunPublicationBeforeDeadline(context,
                    lease.publishTerminalNoExecution(requestIssuanceMarker, terminalEvidence), publicationBudget);
                } catch (error) {
                  return withTestRunDiscoveryPreflightEvidence(testRunTerminalNoExecutionPublicationFailure(invocation, args.request_id, binding, requestIssuanceMarker, result, asTestRunOperationStoreError(error)), discovery.evidence);
                }
                delete delivery.bookkeepingPendingResult;
                return withTestRunDiscoveryPreflightEvidence(testRunTerminalNoExecutionStartResult(result, binding, requestIssuanceMarker, receipt), discovery.evidence);
              }
            }
            return completeCorrelatedTestRun(withTestRunDiscoveryPreflightEvidence(result, discovery.evidence), operationId);
          } finally {
            lease?.release();
          }
        }, false, projectQueueOwner(invocation, queueDeadlineUnixMs));
        return settleTestRunQueueBeforeDeadline(context, queued, queueDeadlineUnixMs, invocation, args.request_id, effectiveCommandArgs, prepared.command.timeout_ms, operationId, delivery);
      }
      if (args.command === DYNAMIC_CSHARP_READONLY_COMMAND && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true && prepared.command.tags?.includes(DYNAMIC_CSHARP_READONLY_CORRELATION_TAG)) {
        const deadlineUnixMs = earlierDeadline(commandDeadlineUnixMs(context, prepared.command.timeout_ms), recoveryDeadlineUnixMs);
        const operationId = createDynamicCSharpReadonlyOperationId(context);
        const callerArgsHash = argsHash(commandArgs);
        const snippetHash = dynamicCSharpSnippetHash(commandArgs);
        const statusProjectSelector = prepared.record.instance_id;
        const queueEnqueuedAtUnixMs = nowMs(context);
        const delivery = { queueAdmitted: false, queueExpired: false, bridgeRequestIssued: false, queueWaitMs: 0 };
        const queued = runInProjectQueue(context, invocation.identity.project, async () => {
          if (delivery.queueExpired || deadlineUnixMs <= nowMs(context)) {
            return dynamicCSharpReadonlyQueueTimeout(invocation, args.request_id, prepared.command.timeout_ms, operationId, statusProjectSelector, Math.max(0, nowMs(context) - queueEnqueuedAtUnixMs));
          }
          delivery.queueAdmitted = true;
          delivery.queueWaitMs = Math.max(0, nowMs(context) - queueEnqueuedAtUnixMs);
          const operationArgs = {
            ...effectiveCommandArgs,
            _dynamic_csharp_operation_id: operationId,
            _dynamic_csharp_caller_args_hash: callerArgsHash,
            _dynamic_csharp_queue_wait_ms: delivery.queueWaitMs,
          };
          const beforeBridgeRequestIssued = () => delivery.queueExpired || deadlineUnixMs <= nowMs(context)
            ? dynamicCSharpReadonlyDispatchTimeout(invocation, args.request_id, prepared.command.timeout_ms, operationId, callerArgsHash, snippetHash, statusProjectSelector, false, delivery.queueWaitMs)
            : undefined;
          const result = await invokePreparedCommand(context, invocation, args.request_id, operationArgs, deadlineUnixMs, true, () => { delivery.bridgeRequestIssued = true; }, undefined, undefined, beforeBridgeRequestIssued);
          return completeCorrelatedDynamicCSharpReadonly(result, operationId, callerArgsHash, snippetHash, statusProjectSelector, delivery.bridgeRequestIssued, delivery.queueWaitMs);
        }, false, projectQueueOwner(invocation, deadlineUnixMs));
        return settleDynamicCSharpReadonlyQueueBeforeDeadline(context, queued, deadlineUnixMs, invocation, args.request_id, prepared.command.timeout_ms, operationId, callerArgsHash, snippetHash, statusProjectSelector, delivery);
      }
      if (args.command === EDITOR_MENU_OPERATION_START_COMMAND && effectiveCommandArgs.dry_run !== true && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true && prepared.command.tags?.includes(EDITOR_MENU_OPERATION_CORRELATION_TAG)) {
        const deadlineUnixMs = earlierDeadline(commandDeadlineUnixMs(context, prepared.command.timeout_ms), recoveryDeadlineUnixMs);
        const operationId = createEditorMenuOperationId(context);
        const statusProjectSelector = prepared.record.instance_id;
        const operationArgs = { ...effectiveCommandArgs, _editor_menu_operation_id: operationId, _editor_menu_caller_args_hash: argsHash(commandArgs) };
        const delivery = { queueAdmitted: false, queueExpired: false, writeRequestIssued: false };
        const queued = runInProjectQueue(context, invocation.identity.project, async () => {
          if (delivery.queueExpired || deadlineUnixMs <= nowMs(context)) return editorMenuOperationQueueTimeout(invocation, args.request_id, prepared.command.timeout_ms, operationId, statusProjectSelector);
          delivery.queueAdmitted = true;
          const result = await invokePreparedCommand(context, invocation, args.request_id, operationArgs, deadlineUnixMs, true, () => { delivery.writeRequestIssued = true; });
          return completeCorrelatedEditorMenuOperation(result, operationId, statusProjectSelector, delivery.writeRequestIssued);
        }, false, projectQueueOwner(invocation, deadlineUnixMs));
        return settleEditorMenuOperationQueueBeforeDeadline(context, queued, deadlineUnixMs, invocation, args.request_id, prepared.command.timeout_ms, operationId, statusProjectSelector, delivery);
      }
      if (PLAY_MODE_TRANSITION_COMMANDS.has(args.command) && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true) {
        const deadlineUnixMs = commandDeadlineUnixMs(context, prepared.command.timeout_ms + PLAY_MODE_TRANSITION_STABILITY_OVERHEAD_MS);
        const delivery = { writeRequestIssued: false };
        const operationId = randomUUID().replaceAll("-", "");
        const target = playModeTarget(args.command, effectiveCommandArgs);
        const playModeCommandArgs = { ...effectiveCommandArgs, _playmode_transition_expectation_id: operationId };
        if (validateCommandArgs(prepared.command.schema, playModeCommandArgs, isTrustedBuiltInDescriptor(prepared.command)).status !== "ok") {
          return fail({
            ...optionalRequestId(args.request_id),
            project: invocation.identity.project,
            command: args.command,
            error: { code: "catalog_stale", message: "Play Mode command catalog does not support operation-correlated lifecycle verification" },
          });
        }
        const queued = runInProjectQueue(context, invocation.identity.project, async () => {
          if (deadlineUnixMs <= nowMs(context)) return playModeQueueTimeout(invocation, args.request_id, prepared.command.timeout_ms);
          const result = await invokePreparedCommand(context, invocation, args.request_id, playModeCommandArgs, deadlineUnixMs, true, () => { delivery.writeRequestIssued = true; });
          return completePlayModeTransitionWithinQueue(context, invocation, args, playModeCommandArgs, result, deadlineUnixMs);
        }, false, projectQueueOwner(invocation, deadlineUnixMs));
        return settlePlayModeQueueBeforeDeadline(context, queued, deadlineUnixMs, invocation, args.request_id, prepared.command.timeout_ms, delivery, operationId, target);
      }
      if (enableCompileRefreshEmptyBodyVerification && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true) {
        const deadlineUnixMs = earlierDeadline(commandDeadlineUnixMs(context, prepared.command.timeout_ms), recoveryDeadlineUnixMs);
        const delivery = { writeRequestIssued: false };
        const queued = runInProjectQueue(context, invocation.identity.project, async () => {
          if (deadlineUnixMs <= nowMs(context)) {
            return compileRefreshQueueTimeout(invocation, args.request_id, prepared.command.timeout_ms);
          }
          const result = await invokePreparedCommand(context, invocation, args.request_id, effectiveCommandArgs, deadlineUnixMs, true, () => { delivery.writeRequestIssued = true; });
          const completed = await completeCompileRefreshWithinQueue(context, invocation, args, effectiveCommandArgs, result, deadlineUnixMs);
          compileRefreshVerificationHandledResults.add(completed);
          return completed;
        }, false, projectQueueOwner(invocation, deadlineUnixMs));
        return settleCompileRefreshQueueBeforeDeadline(context, queued, deadlineUnixMs, invocation, args.request_id, prepared.command.timeout_ms, delivery);
      }
      const canReconnectEmptyBody = recoveryDeadlineUnixMs !== undefined
        && prepared.record !== undefined
        && prepared.command.risk === "read"
        && isReadRecoveryCommand(args.command)
        && prepared.virtual !== true
        && prepared.official !== true;
      const invokeAndRecover = async (queueOwned = false): Promise<GatewayResult> => {
        const bridgeDeadlineUnixMs = initialBridgeDeadlineUnixMs ?? recoveryDeadlineUnixMs;
        const compileWaitPlan = args.command === COMPILE_WAIT_READY_COMMAND && bridgeDeadlineUnixMs !== undefined
          ? compileWaitReadyTransportPlan(effectiveCommandArgs, bridgeDeadlineUnixMs, nowMs(context))
          : undefined;
        if (args.command === COMPILE_WAIT_READY_COMMAND && !compileWaitPlan) {
          return withCompileWaitReadyDeliveryContract(compileWaitReadyDeliveryBudgetExhausted(invocation, args.request_id), commandArgs, 0);
        }
        const delivery = compileWaitPlan ? { effectiveUnityWaitMs: compileWaitPlan.effectiveUnityWaitMs } : undefined;
        const result = await invokePreparedCommand(context, invocation, args.request_id, compileWaitPlan?.args ?? effectiveCommandArgs, bridgeDeadlineUnixMs, queueOwned, undefined, undefined, preBridgeDispatchGuard);
        const recovered = recoveryDeadlineUnixMs !== undefined && result.status === "error" && isCompleteEmptyInvokeBridgeProtocolError(result)
          ? await recoverReadInvocationOnReplacement(context, invocation, args.request_id, commandArgs, effectiveCommandArgs, result, recoveryDeadlineUnixMs, queueOwned, delivery)
          : result;
        return args.command === COMPILE_WAIT_READY_COMMAND
          ? withCompileWaitReadyDeliveryContract(recovered, commandArgs, delivery?.effectiveUnityWaitMs ?? 0)
          : recovered;
      };
      if (canReconnectEmptyBody) {
        return runInProjectQueue(
          context,
          invocation.identity.project,
          () => invokeAndRecover(true),
          false,
          projectQueueOwner(invocation, earlierDeadline(commandDeadlineUnixMs(context, prepared.command.timeout_ms), recoveryDeadlineUnixMs)),
        );
      }
      return invokeAndRecover(queueOwned);
    },
    job: () => {
      if (!prepared.command.supports_job) {
        return jobNotSupported(args.project, args.command, args.request_id);
      }
      if (!context.jobs) {
        return inputError("internal_error", "Job manager is not configured");
      }

      if (args.command === BUILD_RUN_COMMAND && effectiveCommandArgs.dry_run !== true && prepared.record !== undefined && prepared.virtual !== true && prepared.official !== true && prepared.command.tags?.includes(BUILD_RUN_CORRELATION_TAG)) {
        const job = context.jobs.start({
          project: prepared.project,
          command: args.command,
          ...(args.request_id ? { request_id: args.request_id } : {}),
          supports_cancel: false,
          task: async () => {
            const operationId = createBuildRunOperationId(context);
            const statusPreparation = await prepareBuildRunStatusInvocation(context, invocation, operationId);
            if (statusPreparation.status === "error") return statusPreparation.result;
            const buildRunCommandArgs = { ...effectiveCommandArgs, _build_run_operation_id: operationId, _build_run_caller_args_hash: argsHash(commandArgs) };
            const started = await startCorrelatedBuildRun(context, invocation, args.request_id, buildRunCommandArgs, operationId);
            if (started.status === "error") {
              if (started.error.details?.bridge_request_issued !== true) return started;
              const pollStartedAt = nowMs(context);
              return pollBuildRunUntilTerminal(context, statusPreparation.invocation, operationId, pollStartedAt + BUILD_RUN_MAX_WAIT_MS, pollStartedAt + prepared.command.timeout_ms + BUILD_RUN_STARTUP_RECEIPT_SLACK_MS);
            }
            if (!isRunningOperationResult(started)) return started;
            return pollBuildRunUntilTerminal(context, statusPreparation.invocation, operationId, nowMs(context) + BUILD_RUN_MAX_WAIT_MS);
          },
        });
        return ok({ ...optionalRequestId(args.request_id), project: prepared.project, command: args.command, data: job });
      }

      const job = context.jobs.start({
        project: prepared.project,
        command: args.command,
        ...(args.request_id ? { request_id: args.request_id } : {}),
        supports_cancel: prepared.command.supports_cancel === true,
        task: () => invokePreparedCommand(context, invocation, args.request_id, effectiveCommandArgs, recoveryDeadlineUnixMs),
      });
      return ok({ ...optionalRequestId(args.request_id), project: prepared.project, command: args.command, data: job });
    },
  });
}

type PreDispatchReplacementEvidence = {
  attempted: true;
  wait_budget_ms: number;
  deadline_scope: "pre_dispatch_only";
  command_timeout_included: false;
  attempt_count: number;
  bridge_request_count: number;
  selector_strategy: "failed_heartbeat_canonical_project_identity";
  failed_instance: Record<string, unknown>;
  replacement_instance?: Record<string, unknown>;
  terminal_status: "routed" | "error" | "timeout";
};

async function invokeUnityCallWithPreDispatchReplacementWait(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>): Promise<GatewayResult> {
  const waitBudgetMs = args.wait_for_replacement_ms;
  if (waitBudgetMs === undefined) return invokeUnityCallOnce(context, args, commandArgs);
  const boundedWaitBudgetMs = waitBudgetMs;
  if (args.mode !== "wait" || args.command !== GAME_VIEW_SCREENSHOT_COMMAND) {
    return replacementWaitValidationFailed(context, args);
  }

  const resolution = resolveProject(context, args.project, { allowStale: true });
  if (resolution.status === "error") {
    return projectResolutionError(args.project, resolution, args.command, args.request_id, context.registry, commandArgs, context);
  }
  const failedRecord = resolution.record;
  if (!isTransientBridgeLifecycleStatus(failedRecord.status)) {
    return invokeUnityCallOnce(context, args, commandArgs);
  }
  if (!context.registry) {
    return bridgeUnavailable(args.project, args.command, args.request_id);
  }

  const deadlineUnixMs = earlierDeadline(nowMs(context) + boundedWaitBudgetMs, context.internalDeadlineUnixMs);
  let attemptCount = 0;
  let replacementRecord: HeartbeatRecord | undefined;
  let admissionExpired = false;
  let markAdmitted!: () => void;
  const admitted = new Promise<void>((resolve) => { markAdmitted = resolve; });
  const queued = runInProjectQueue(context, failedRecord.project, async () => {
    markAdmitted();
    if (admissionExpired || nowMs(context) >= deadlineUnixMs) return timeoutResult();
    while (nowMs(context) < deadlineUnixMs) {
      attemptCount += 1;
      const replacement = resolveReplacementByCanonicalIdentity(context.registry as ProjectRegistry, failedRecord);
      if (replacement.status === "ambiguous") {
        return fail({
          ...optionalRequestId(args.request_id),
          project: failedRecord.project,
          command: args.command,
          error: {
            code: "duplicate_project_id",
            message: "Multiple live replacement instances match the pre-dispatch project identity",
            details: { pre_dispatch_replacement: evidence("error", 0) },
          },
        });
      }

      if (replacement.status === "ready") {
        replacementRecord = replacement.record;
        if (replacementRecord.project !== failedRecord.project || replacementRecord.canonical_project_root !== failedRecord.canonical_project_root) {
          return fail({
            ...optionalRequestId(args.request_id),
            project: failedRecord.project,
            command: args.command,
            error: {
              code: "unsafe_binding_refused",
              message: "Canonical replacement identity does not match the failed screenshot target",
              details: { pre_dispatch_replacement: evidence("error", 0) },
            },
          });
        }

        const requestsBefore = context.auditBridgeInvokeAttempts ?? 0;
        const result = await invokeUnityCallOnce(
          context,
          args,
          commandArgs,
          undefined,
          false,
          undefined,
          true,
          replacementRecord,
          failedRecord.commands_hash,
          undefined,
          () => nowMs(context) >= deadlineUnixMs ? timeoutResult() : undefined,
        );
        const bridgeRequestCount = Math.max(0, (context.auditBridgeInvokeAttempts ?? 0) - requestsBefore);
        const terminalStatus = result.status === "ok" ? "routed" : result.error.code === "replacement_timeout" ? "timeout" : "error";
        return withPreDispatchReplacementEvidence(result, evidence(terminalStatus, bridgeRequestCount));
      }

      const remainingMs = deadlineUnixMs - nowMs(context);
      if (remainingMs <= 0) break;
      await sleepMs(context, Math.min(DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS, remainingMs));
    }

    return timeoutResult();
  }, false, {
    command: GAME_VIEW_SCREENSHOT_COMMAND,
    risk: "read",
    idempotency: "safe",
    deadline_unix_ms: deadlineUnixMs,
  });
  let admissionTimer: ReturnType<typeof setTimeout> | undefined;
  const admissionTimedOut = new Promise<boolean>((resolve) => {
    admissionTimer = setTimeout(() => {
      admissionExpired = true;
      resolve(true);
    }, Math.max(1, deadlineUnixMs - nowMs(context)));
  });
  const expiredBeforeAdmission = await Promise.race([admitted.then(() => false), admissionTimedOut]);
  if (admissionTimer !== undefined) clearTimeout(admissionTimer);
  if (expiredBeforeAdmission) return timeoutResult();
  return queued;

  function timeoutResult(): GatewayResult {
    return fail({
      ...optionalRequestId(args.request_id),
      project: failedRecord.project,
      command: args.command,
      error: {
        code: "replacement_timeout",
        message: "No unambiguous replacement Unity bridge became ready before the pre-dispatch deadline",
        details: { pre_dispatch_replacement: evidence("timeout", 0) },
      },
    });
  }

  function evidence(terminalStatus: PreDispatchReplacementEvidence["terminal_status"], bridgeRequestCount: number): PreDispatchReplacementEvidence {
    return {
      attempted: true,
      wait_budget_ms: boundedWaitBudgetMs,
      deadline_scope: "pre_dispatch_only",
      command_timeout_included: false,
      attempt_count: attemptCount,
      bridge_request_count: bridgeRequestCount,
      selector_strategy: "failed_heartbeat_canonical_project_identity",
      failed_instance: safeRecoveryInstance(failedRecord, context),
      ...(replacementRecord ? { replacement_instance: safeRecoveryInstance(replacementRecord, context) } : {}),
      terminal_status: terminalStatus,
    };
  }
}

function validateDescriptorBackedWrapperWait(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>, descriptor: CommandDescriptor): GatewayResult | undefined {
  if (args.wait_for_replacement_ms !== undefined) {
    const replacement = descriptor.wrapper_wait?.wait_for_replacement_ms;
    if (descriptor.wrapper_wait?.mode !== "wait" || replacement === undefined) {
      return replacementWaitValidationFailed(context, args, descriptor, "descriptor_contract_missing");
    }
    if (args.wait_for_replacement_ms < replacement.min_ms || args.wait_for_replacement_ms > replacement.max_ms) {
      return replacementWaitValidationFailed(context, args, descriptor, "outside_descriptor_range");
    }
  }

  if (args.wait_for_test_run_terminal_ms !== undefined) {
    const terminal = descriptor.wrapper_wait?.wait_for_test_run_terminal_ms;
    if (descriptor.wrapper_wait?.mode !== "wait" || terminal === undefined) {
      return testRunTerminalWaitValidationFailed(context, args, descriptor, "descriptor_contract_missing");
    }
    if (args.wait_for_test_run_terminal_ms < terminal.min_ms || args.wait_for_test_run_terminal_ms > terminal.max_ms) {
      return testRunTerminalWaitValidationFailed(context, args, descriptor, "outside_descriptor_range");
    }
    if (Object.hasOwn(terminal, "compile_readiness_contract") && terminal.compile_readiness_contract !== TEST_RUN_COMPILE_READINESS_CONTRACT) {
      return testRunTerminalWaitValidationFailed(context, args, descriptor, "unsupported_compile_readiness_contract");
    }
    if (terminal.compile_readiness_contract === TEST_RUN_COMPILE_READINESS_CONTRACT && !descriptor.tags?.includes(TEST_RUN_CORRELATION_TAG)) {
      return testRunTerminalWaitValidationFailed(context, args, descriptor, "correlated_route_required");
    }
    if (commandArgs.response_mode !== terminal.response_mode || commandArgs.dry_run === true) {
      return testRunTerminalWaitValidationFailed(context, args, descriptor, "descriptor_argument_requirement_failed");
    }
  }

  return undefined;
}

function replacementWaitValidationFailed(context: DispatcherRuntimeContext, args: UnityCallArgs, descriptor?: CommandDescriptor, issueCode = "unsupported_for_command"): GatewayResult {
  const recovery = buildSafeExecutableSchemaRecovery({
    ...availablePreResolutionProject(args.project, context),
    command: args.command,
  });
  const contract = descriptor?.wrapper_wait?.wait_for_replacement_ms;
  return fail({
    ...optionalRequestId(args.request_id),
    ...safeRecoveryIdentity(recovery),
    next_valid_actions: recoveryNextValidActions(recovery),
    error: {
      code: "invalid_field_value",
      message: safeSchemaRecoveryMessage("invalid_field_value", recovery.safeCommand),
      details: {
        invalid_arguments: {
          schema_version: 1,
          origin: "mcp_tool",
          issues: [{
            path: ["wait_for_replacement_ms"],
            issue_code: issueCode,
            rejected_value_type: "integer",
            rejected_value_reflected: false,
            accepted: {
              command: GAME_VIEW_SCREENSHOT_COMMAND,
              mode: "wait",
              range: {
                minimum: contract?.min_ms ?? 1,
                maximum: contract?.max_ms ?? MAX_POST_ADMISSION_WAIT_MS,
              },
              deadline_scope: contract?.deadline_scope ?? "pre_dispatch_only",
              descriptor_contract_advertised: contract !== undefined,
            },
          }],
          issues_truncated: false,
          rejected_values_reflected: false,
          automatic_retry_attempted: false,
          descriptor_lookup: recovery.schemaLookup,
        },
        ...schemaRecoveryDetails(recovery),
      },
    },
  });
}

function testRunTerminalWaitValidationFailed(context: DispatcherRuntimeContext, args: UnityCallArgs, descriptor?: CommandDescriptor, issueCode = "unsupported_for_command_or_mode"): GatewayResult {
  const recovery = buildSafeExecutableSchemaRecovery({
    ...availablePreResolutionProject(args.project, context),
    command: args.command,
  });
  const contract = descriptor?.wrapper_wait?.wait_for_test_run_terminal_ms;
  return fail({
    ...optionalRequestId(args.request_id),
    ...safeRecoveryIdentity(recovery),
    next_valid_actions: recoveryNextValidActions(recovery),
    error: {
      code: "invalid_field_value",
      message: safeSchemaRecoveryMessage("invalid_field_value", recovery.safeCommand),
      details: {
        invalid_arguments: {
          schema_version: 1,
          origin: "mcp_tool",
          issues: [{
            path: ["wait_for_test_run_terminal_ms"],
            issue_code: issueCode,
            rejected_value_type: "integer",
            rejected_value_reflected: false,
            accepted: {
              command: TEST_RUN_COMMAND,
              mode: "wait",
              args_json: { response_mode: "compact", dry_run: false },
              range: {
                minimum: contract?.min_ms ?? 1,
                maximum: contract?.max_ms ?? 3_600_000,
              },
              deadline_scope: contract?.deadline_scope ?? "start_and_terminal_poll",
              descriptor_contract_advertised: contract !== undefined,
            },
          }],
          issues_truncated: false,
          rejected_values_reflected: false,
          automatic_retry_attempted: false,
          descriptor_lookup: recovery.schemaLookup,
        },
        ...schemaRecoveryDetails(recovery),
      },
    },
  });
}

function withPreDispatchReplacementEvidence(result: GatewayResult, evidence: PreDispatchReplacementEvidence): GatewayResult {
  if (result.status === "ok") {
    return { ...result, data: mergeRecoveryData(result.data, { pre_dispatch_replacement: evidence }) };
  }
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...result.error.details,
        pre_dispatch_replacement: evidence,
      },
    },
  };
}

type PostReloadRoutingEvidence = {
  schema_version: 1;
  attempted: true;
  attempt_count: number;
  retry_budget_ms: number;
  elapsed_ms: number;
  terminal_status: "read_recovered" | "unsafe_dispatched_once" | "unsafe_not_dispatched" | "unsafe_retry_refused" | "catalog_stale" | "error" | "timeout";
  command_request_replayed: boolean;
  policy_evaluated_for_command_dispatch: boolean;
  command_risk: CommandDescriptor["risk"] | "unknown";
  current_candidate_instance?: Record<string, unknown>;
  current_candidate_selector?: string;
};

async function invokeUnityCallWithRiskAwareRecovery(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>, transportCommandArgs?: Record<string, unknown>, requireInitialReadDescriptor = false, initialBridgeDeadlineUnixMs?: number): Promise<GatewayResult> {
  const knownReadRetry = isReadRecoveryCommand(args.command);
  const retry = knownReadRetry
    ? bridgeRecoveryRetryConfig(args.command, commandArgs)
    : { timeoutMs: POST_RELOAD_ROUTING_TIMEOUT_MS, pollIntervalMs: DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS };
  const initialStartedAtMs = nowMs(context);
  const configuredDeadlineMs = initialStartedAtMs + retry.timeoutMs;
  const initialDeadlineMs = knownReadRetry
    ? context.internalDeadlineUnixMs === undefined ? configuredDeadlineMs : earlierDeadline(configuredDeadlineMs, context.internalDeadlineUnixMs)
    : context.internalDeadlineUnixMs;
  if (initialBridgeDeadlineUnixMs === undefined && args.command === COMPILE_WAIT_READY_COMMAND) {
    initialBridgeDeadlineUnixMs = compileWaitReadyInitialBridgeDeadline(configuredDeadlineMs, context.internalDeadlineUnixMs);
  }
  if (initialDeadlineMs !== undefined && context.internalDeadlineUnixMs !== initialDeadlineMs) {
    return withInternalDeadline(context, initialDeadlineMs, () => invokeUnityCallWithRiskAwareRecovery(context, args, commandArgs, transportCommandArgs, requireInitialReadDescriptor, initialBridgeDeadlineUnixMs));
  }
  let result: GatewayResult;
  if (requireInitialReadDescriptor) {
    const resolved = await resolveInvocationCommand(context, args.project, args.command, args.request_id, commandArgs, recoveryTransport(context, initialDeadlineMs ?? initialStartedAtMs + POST_RELOAD_ROUTING_TIMEOUT_MS));
    if (resolved.state === "blocked") return withCommandNotDispatched(resolved.result);
    if (resolved.value.command.risk !== "read") {
      recordResolvedCommandAuditMetadata(context, resolved.value);
      if (isValidCommandTimeout(resolved.value.command.timeout_ms)) context.auditCommandMetadata!.command_revision = commandDescriptorRevision(resolved.value.command);
      context.auditPostReloadRouting = { status: "unsafe_not_dispatched", attempts: 1, commandRisk: resolved.value.command.risk };
      return fail({
        ...optionalRequestId(args.request_id),
        project: resolved.value.project,
        command: args.command,
        error: {
          code: "unsafe_binding_refused",
          message: "Gateway-generated verification requires a read-risk command descriptor",
          details: {
            ...preDispatchNonDeliveryDetails(),
            verification_command: args.command,
            observed_command_risk: resolved.value.command.risk,
            automatic_verification_dispatch_refused: true,
          },
        },
      });
    }
    const initialRevision = commandDescriptorRevision(resolved.value.command);
    result = await invokeUnityCallOnce(context, args, commandArgs, initialDeadlineMs, false, transportCommandArgs, false, resolved.value.record, resolved.value.commandsHash ?? undefined, initialRevision, undefined, initialBridgeDeadlineUnixMs);
  } else {
    result = await invokeUnityCallOnce(context, args, commandArgs, initialDeadlineMs, false, transportCommandArgs, false, undefined, undefined, undefined, undefined, initialBridgeDeadlineUnixMs);
  }
  if (result.status === "ok") return result;

  const startedAtMs = knownReadRetry ? initialStartedAtMs : nowMs(context);
  const configuredRecoveryDeadlineMs = startedAtMs + retry.timeoutMs;
  const deadlineMs = initialDeadlineMs !== undefined
    ? earlierDeadline(configuredRecoveryDeadlineMs, initialDeadlineMs)
    : configuredRecoveryDeadlineMs;
  const effectiveRetryTimeoutMs = Math.max(0, deadlineMs - startedAtMs);
  const originalResult = result;
  const originalRecoveryRecord = preDispatchRecoveryRecords.get(originalResult);
  const originalCommandRevision = context.auditCommandMetadata?.command_revision;
  const originalCommandsHash = context.auditCommandMetadata?.commands_hash;
  let attemptCount = 0;
  let issuedCommandRequestCount = originalResult.error.details?.command_bridge_request_issued === true ? 1 : 0;
  let commandRequestReplayed = false;
  let lastRisk: PostReloadRoutingEvidence["command_risk"] = "unknown";
  let lastCandidate: HeartbeatRecord | undefined;
  const maxAttempts = Math.ceil(effectiveRetryTimeoutMs / retry.pollIntervalMs) + 1;

  if (result.error.code === "project_not_found" && !context.registry) return result;

  while (attemptCount < maxAttempts && isRiskAwareRecoveryCandidate(result, args.command)) {
    const remainingMs = deadlineMs - nowMs(context);
    if (remainingMs <= 0) {
      if (knownReadRetry) {
        recordRoutingAudit("timeout", lastRisk);
        const observed = withTerminalStaleEditorObservation(result, context, args.project, args.command, originalResult);
        return bridgeRecoveryRetryExhausted(observed, attemptCount + 1, effectiveRetryTimeoutMs, Math.max(0, nowMs(context) - startedAtMs), { tool: "sgg_unity_call", command: args.command }, originalResult);
      }
      return withPostReloadRoutingEvidence(result, evidence("timeout", "unknown"));
    }

    attemptCount += 1;
    const retainedReplacement = projectResolutionRecentReplacements.get(result);
    const recentReplacement = !retainedReplacement && result.error.code === "project_not_found"
      ? context.registry?.resolveRecentReplacementRecord(args.project)
      : undefined;
    const resolved = await resolveInvocationCommand(
      context,
      args.project,
      args.command,
      args.request_id,
      commandArgs,
      recoveryTransport(context, deadlineMs),
      retainedReplacement ?? (recentReplacement?.status === "ok" ? recentReplacement.replacementRecord : undefined),
    );
    if (resolved.state === "resolved") {
      const candidate = resolved.value.record;
      const risk = resolved.value.command.risk;
      lastRisk = risk;
      lastCandidate = candidate;
        if (originalCommandRevision && (commandDescriptorRevision(resolved.value.command) !== originalCommandRevision || (originalCommandsHash && resolved.value.commandsHash !== originalCommandsHash))) {
          const drift = fail({
            ...optionalRequestId(args.request_id),
            project: resolved.value.project,
            command: args.command,
            error: {
              code: "catalog_stale",
              message: "Replacement instance command descriptor changed during post-reload routing",
              details: preDispatchNonDeliveryDetails(),
            },
          });
          return withPostReloadRoutingEvidence(drift, evidence("catalog_stale", risk, candidate));
        }
        if (risk !== "read") {
          if (args.command === TEST_RUN_COMMAND
            && canRouteTestRunBeforeDispatch(context, args.project, originalResult, originalRecoveryRecord, candidate, resolved.value.commandsHash)) {
            const candidateRevision = commandDescriptorRevision(resolved.value.command);
            const testRunDeadlineMs = earlierDeadline(initialStartedAtMs + resolved.value.command.timeout_ms, context.internalDeadlineUnixMs);
            const routed = await invokeUnityCallOnce(context, args, commandArgs, testRunDeadlineMs, false, transportCommandArgs, false, candidate, resolved.value.commandsHash ?? undefined, candidateRevision);
            const requestIssued = routed.status === "ok" || routed.error.details?.command_bridge_request_issued === true;
            const terminalStatus: PostReloadRoutingEvidence["terminal_status"] = requestIssued ? "unsafe_dispatched_once" : "unsafe_not_dispatched";
            return withPostReloadRoutingEvidence(routed, evidence(terminalStatus, risk, candidate));
          }
          recordResolvedCommandAuditMetadata(context, resolved.value);
          if (isValidCommandTimeout(resolved.value.command.timeout_ms)) context.auditCommandMetadata!.command_revision = commandDescriptorRevision(resolved.value.command);
          const terminalStatus = originalResult.error.details?.command_delivery_state === "not_dispatched" ? "unsafe_not_dispatched" : "unsafe_retry_refused";
          return withPostReloadRoutingEvidence(originalResult, evidence(terminalStatus, risk, candidate));
        }
      const candidateRevision = commandDescriptorRevision(resolved.value.command);
      result = await invokeUnityCallOnce(context, args, commandArgs, deadlineMs, false, transportCommandArgs, false, candidate, resolved.value.commandsHash ?? undefined, candidateRevision);
      if (result.status === "ok" || result.error.details?.command_bridge_request_issued === true) issuedCommandRequestCount += 1;
      commandRequestReplayed = issuedCommandRequestCount > 1;
      if (result.status === "ok") return withPostReloadRoutingEvidence(result, evidence("read_recovered", risk, candidate));
      await sleepBeforeRiskAwareRetry();
      continue;
    }
    result = resolved.result;

    const delayMs = Math.min(retry.pollIntervalMs, Math.max(0, deadlineMs - nowMs(context)));
    if (delayMs > 0) await sleepMs(context, delayMs);
  }

  if (isRiskAwareRecoveryCandidate(result, args.command) && attemptCount >= maxAttempts) {
    if (knownReadRetry) {
      recordRoutingAudit("timeout", lastRisk);
      const observed = withTerminalStaleEditorObservation(result, context, args.project, args.command, originalResult);
      return bridgeRecoveryRetryExhausted(observed, attemptCount + 1, effectiveRetryTimeoutMs, Math.max(0, nowMs(context) - startedAtMs), { tool: "sgg_unity_call", command: args.command }, originalResult);
    }
    return withPostReloadRoutingEvidence(result, evidence("timeout", lastRisk, lastCandidate));
  }
  const terminalResult: GatewayResult = knownReadRetry && result.status === "error"
    ? withTerminalStaleEditorObservation(result, context, args.project, args.command, originalResult)
    : result;
  const terminalStatus: PostReloadRoutingEvidence["terminal_status"] = gatewayResultErrorCode(terminalResult) === "catalog_stale" ? "catalog_stale" : "error";
  return attemptCount > 0 ? withPostReloadRoutingEvidence(terminalResult, evidence(terminalStatus, lastRisk, lastCandidate)) : terminalResult;

  function evidence(terminalStatus: PostReloadRoutingEvidence["terminal_status"], risk: PostReloadRoutingEvidence["command_risk"], candidate?: HeartbeatRecord): PostReloadRoutingEvidence {
    recordRoutingAudit(terminalStatus, risk);
    return {
      schema_version: 1,
      attempted: true,
      attempt_count: attemptCount,
      retry_budget_ms: effectiveRetryTimeoutMs,
      elapsed_ms: Math.max(0, nowMs(context) - startedAtMs),
      terminal_status: terminalStatus,
      command_request_replayed: commandRequestReplayed,
      policy_evaluated_for_command_dispatch: terminalStatus === "read_recovered" || terminalStatus === "unsafe_dispatched_once",
      command_risk: risk,
      ...(candidate ? {
        current_candidate_instance: safeRecoveryInstance(candidate, context),
        current_candidate_selector: candidate.instance_id,
      } : {}),
    };
  }

  function recordRoutingAudit(status: PostReloadRoutingEvidence["terminal_status"], risk: PostReloadRoutingEvidence["command_risk"]): void {
    context.auditPostReloadRouting = { status, attempts: attemptCount, commandRisk: risk };
  }

  async function sleepBeforeRiskAwareRetry(): Promise<void> {
    const delayMs = Math.min(retry.pollIntervalMs, Math.max(0, deadlineMs - nowMs(context)));
    if (delayMs > 0) await sleepMs(context, delayMs);
  }
}

function gatewayResultErrorCode(result: GatewayResult): ErrorCode | undefined {
  return result.status === "error" ? result.error.code : undefined;
}

function isRiskAwareRecoveryCandidate(result: GatewayResult, command: string): result is Extract<GatewayResult, { status: "error" }> {
  if (result.status !== "error") return false;
  if (optedInTestRunPreflightFailures.has(result)) return false;
  if (command === GAME_VIEW_SCREENSHOT_COMMAND) return false;
  if (DYNAMIC_CSHARP_EXECUTION_COMMANDS.has(command)
    && (result.error.details?.command_bridge_request_issued === true || result.error.details?.bridge_request_issued === true)) return false;
  if (result.error.details?.command_delivery_state === "not_dispatched") {
    if (result.error.details.command_bridge_request_issued !== false) return false;
    return result.error.code === "project_not_found"
      || result.error.code === "project_stale"
      || result.error.code === "unity_reloading"
      || result.error.code === "bridge_unreachable"
      || result.error.code === "bridge_timeout";
  }
  if (command === TEST_RUN_COMMAND) return false;
  return result.error.details?.command_risk === "read"
    && (result.error.code === "project_not_found" || result.error.code === "project_stale" || result.error.code === "unity_reloading" || result.error.code === "bridge_unreachable");
}

function canRouteTestRunBeforeDispatch(
  context: DispatcherRuntimeContext,
  projectSelector: string,
  originalResult: Extract<GatewayResult, { status: "error" }>,
  retainedRecord: HeartbeatRecord | undefined,
  candidate: HeartbeatRecord | undefined,
  candidateCommandsHash: string | null,
): boolean {
  if (!candidate
    || originalResult.error.details?.command_delivery_state !== "not_dispatched"
    || originalResult.error.details.command_bridge_request_issued !== false) return false;

  let recent: ProjectRegistryRecentReplacement | undefined;
  try {
    recent = context.registry?.captureSnapshot().findRecentReplacement(projectSelector);
  } catch {
    return false;
  }
  const failed = retainedRecord ?? recent?.failed_record;
  if (!failed) return false;
  if (candidate.project !== failed.project || candidate.canonical_project_root !== failed.canonical_project_root) return false;
  if (candidateCommandsHash === null || candidate.commands_hash !== candidateCommandsHash || failed.commands_hash !== candidateCommandsHash) return false;

  if (candidate.instance_id === failed.instance_id) {
    if (candidate.unity_pid !== failed.unity_pid) return false;
    const failedStart = failed.unity_process_start_utc ?? "";
    const candidateStart = candidate.unity_process_start_utc ?? "";
    return (failedStart.length === 0 && candidateStart.length === 0) || (failedStart.length > 0 && failedStart === candidateStart);
  }

  if (projectSelector === failed.instance_id) return false;
  if (candidate.unity_pid !== failed.unity_pid) return false;
  const failedStart = failed.unity_process_start_utc ?? "";
  const candidateStart = candidate.unity_process_start_utc ?? "";
  if (failedStart.length === 0 || candidateStart.length === 0 || failedStart !== candidateStart) return false;
  if (recent && (recent.failed_record.instance_id !== failed.instance_id || recent.replacement_record.instance_id !== candidate.instance_id)) return false;
  return true;
}

function withPostReloadRoutingEvidence(result: GatewayResult, evidence: PostReloadRoutingEvidence): GatewayResult {
  if (result.status === "ok") {
    return { ...result, data: mergeRecoveryData(result.data, { post_reload_routing: evidence }) };
  }
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...result.error.details,
        post_reload_routing: evidence,
      },
    },
  };
}

async function invokeAcceptedReadOnceWithEmptyBodyRecovery(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>): Promise<GatewayResult> {
  const retry = bridgeRecoveryRetryConfig(args.command, commandArgs);
  const configuredDeadlineUnixMs = nowMs(context) + retry.timeoutMs;
  const recoveryDeadlineUnixMs = earlierDeadline(configuredDeadlineUnixMs, context.internalDeadlineUnixMs);
  const initialBridgeDeadlineUnixMs = args.command === COMPILE_WAIT_READY_COMMAND
    ? compileWaitReadyInitialBridgeDeadline(configuredDeadlineUnixMs, context.internalDeadlineUnixMs)
    : recoveryDeadlineUnixMs;
  return invokeUnityCallOnce(context, args, commandArgs, recoveryDeadlineUnixMs, false, undefined, false, undefined, undefined, undefined, undefined, initialBridgeDeadlineUnixMs);
}

async function invokeCompileRefreshAndWaitWithVerification(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>): Promise<GatewayResult> {
  const callerSafeDeadlineUnixMs = earlierDeadline(nowMs(context) + MAX_POST_ADMISSION_WAIT_MS, context.internalDeadlineUnixMs);
  const requestedTimeoutMs = validCompileRefreshTimeout(commandArgs.timeout_ms);
  const effectiveCommandArgs = requestedTimeoutMs !== undefined && requestedTimeoutMs > MAX_POST_ADMISSION_WAIT_MS
    ? { ...commandArgs, timeout_ms: MAX_POST_ADMISSION_WAIT_MS }
    : commandArgs;
  const barrierCommandArgs = {
    ...effectiveCommandArgs,
    _compile_barrier_expectation_id: randomUUID().replaceAll("-", ""),
  };
  const result = await invokeUnityCallOnce(context, args, commandArgs, callerSafeDeadlineUnixMs, true, barrierCommandArgs);

  if (compileRefreshVerificationHandledResults.has(result)) {
    return withPostAdmissionDeliveryContract(result, "compile.refresh_and_wait", requestedTimeoutMs);
  }

  if (isCompileRefreshCancelledBeforeStart(result)) {
    return withPostAdmissionDeliveryContract(normalizeCompileRefreshCancelledBeforeStart(result), "compile.refresh_and_wait", requestedTimeoutMs);
  }

  return withPostAdmissionDeliveryContract(result, "compile.refresh_and_wait", requestedTimeoutMs);
}

function validCompileRefreshTimeout(value: unknown): number | undefined {
  return typeof value === "number" && Number.isInteger(value) && value >= 1 && value <= MCP_CALLER_BUDGET_MS ? value : undefined;
}

function normalizeCompileRefreshCancelledBeforeStart(result: Extract<GatewayResult, { status: "error" }>): GatewayResult {
  const { repeatability_warning: _repeatabilityWarning, ...details } = result.error.details ?? {};
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...details,
        delivery_state: "not_dispatched",
        explicit_repeat_required: false,
      },
    },
  };
}

type PlayModeTarget = "playing" | "editing";

type PlayModeCatalogMetadataEvidence = {
  catalog_metadata_read_retry_count: number;
  catalog_metadata_last_retry_reason?: CompileCatalogMetadataLagReason;
  catalog_metadata_converged?: boolean;
};

type PlayModeVerificationReadEvidence = PlayModeCatalogMetadataEvidence & {
  verification_attempt_count: number;
  verification_read_request_count: number;
};

async function completePlayModeTransitionWithinQueue(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  args: UnityCallArgs,
  commandArgs: Record<string, unknown>,
  result: GatewayResult,
  deadlineUnixMs: number,
): Promise<GatewayResult> {
  const target = playModeTarget(args.command, commandArgs);
  const operationId = typeof commandArgs._playmode_transition_expectation_id === "string" ? commandArgs._playmode_transition_expectation_id : "";
  if (!target) return result;
  if (result.status === "error" && result.error.code !== "unity_reloading" && result.error.code !== "bridge_timeout" && !isDispatchedPlayModeTransportFailure(result) && !isLateExecutableCommandTimeout(result) && !isCompleteEmptyInvokeBridgeProtocolError(result)) {
    return result;
  }
  return verifyPlayModeTransition(context, invocation, args, result, target, operationId, deadlineUnixMs);
}

function isDispatchedPlayModeTransportFailure(result: GatewayResult): boolean {
  return result.status === "error"
    && (result.error.code === "bridge_unreachable" || result.error.code === "bridge_protocol_error")
    && result.error.details?.command_bridge_request_issued === true;
}

function isLateExecutableCommandTimeout(result: GatewayResult): boolean {
  if (result.status !== "error" || result.error.code !== "command_timeout") return false;
  const details = result.error.details;
  const mainThreadDispatch = isObjectRecord(details?.main_thread_dispatch) ? details.main_thread_dispatch : undefined;
  return details?.late_execution_possible === true || mainThreadDispatch?.late_execution_possible === true;
}

async function verifyPlayModeTransition(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  args: UnityCallArgs,
  originalResult: GatewayResult,
  target: PlayModeTarget,
  operationId: string,
  deadlineUnixMs: number,
): Promise<GatewayResult> {
  const originalRecord = invocation.record;
  if (!originalRecord || !context.registry || !context.bridge) return playModeTransitionUnverified(invocation, args, originalResult, target, operationId, "unavailable", 0, 0);
  const pollIntervalMs = 100;
  const startedAtMs = nowMs(context);
  const maxAttempts = Math.max(1, Math.ceil(Math.max(0, deadlineUnixMs - startedAtMs) / pollIntervalMs) + 1);
  let attempts = 0;
  let readRequests = 0;
  let verificationRole: "current" | "replacement" | undefined;
  let verificationRecord: HeartbeatRecord | undefined;
  let catalogMetadataReadRetryCount = 0;
  let catalogMetadataLastRetryReason: CompileCatalogMetadataLagReason | undefined;
  let catalogMetadataPending = false;

  while (attempts < maxAttempts && nowMs(context) < deadlineUnixMs) {
    attempts += 1;
    const blocker = externalSceneReloadBlockerResult(context, [originalRecord], {
      project: originalRecord.project,
      projectSelector: args.project,
      command: args.command,
      requestId: args.request_id,
      writeRequestIssued: true,
    });
    if (blocker) return withPlayModeTransitionRecoveryEvidence(blocker, invocation, target, operationId, catalogMetadataEvidence());

    let resolution: ReturnType<ProjectRegistry["resolveVerificationProject"]>;
    try {
      resolution = context.registry.resolveVerificationProject(originalRecord.canonical_project_root, originalRecord.instance_id);
    } catch {
      return unverified("unavailable");
    }
    if (resolution.status === "error") {
      if (resolution.code === "duplicate_project_id") return unverified("ambiguous");
      await waitForNextAttempt();
      continue;
    }
    verificationRecord = resolution.record;
    verificationRole = resolution.role;
    if (verificationRole === "replacement" && !sameUnityProcessGeneration(originalRecord, verificationRecord)) {
      return unverified("unavailable");
    }
    if (isTransientBridgeLifecycleStatus(verificationRecord.status) || verificationRecord.bridge_port <= 0) {
      await waitForNextAttempt();
      continue;
    }

    let described: SingleCommandResponse;
    try {
      if (catalogMetadataPending) catalogMetadataReadRetryCount += 1;
      const response = await context.bridge.describe(verificationRecord, `command:${EDITOR_STATUS_COMMAND}`, recoveryTransport(context, deadlineUnixMs, args.request_id));
      if (!isObjectRecord(response) || typeof response.commands_hash !== "string" || !("command" in response)) return unverified("unavailable");
      described = response as SingleCommandResponse;
    } catch (error) {
      const metadataLag = compileCatalogMetadataLagReason(error, invocation, verificationRecord);
      if (metadataLag) {
        // The rejected catalog authorizes no status request. Re-resolve the
        // heartbeat and describe again only within the original bounded loop.
        catalogMetadataLastRetryReason = metadataLag;
        catalogMetadataPending = true;
        await waitForNextAttempt();
        continue;
      }
      if (isRetryableVerificationTransportError(error)) {
        await waitForNextAttempt();
        continue;
      }
      return unverified("unavailable");
    }
    if (described.commands_hash === verificationRecord.commands_hash) catalogMetadataPending = false;
    const descriptor = described.command;
    const statusArgs = { _playmode_transition_expectation_id: operationId };
    if (!lifecycleVerificationDescriptorMatches(invocation, verificationRecord, described.commands_hash, descriptor, EDITOR_STATUS_COMMAND)
      || validateCommandArgs(descriptor.schema, statusArgs, isTrustedBuiltInDescriptor(descriptor)).status !== "ok") {
      return unverified("catalog_stale");
    }
    // The legacy same-hash shortcut cannot authorize the newly converged route
    // after the owner or trusted package changes during metadata polling.
    if (catalogMetadataLastRetryReason && !playModeCatalogMetadataProvenanceMatches(invocation, verificationRecord, descriptor)) {
      return unverified("catalog_stale");
    }

    const statusInvocation = InvocationContextSnapshot.create({
      projectSelector: verificationRecord.instance_id,
      project: verificationRecord.project,
      command: EDITOR_STATUS_COMMAND,
      descriptor,
      commandsHash: described.commands_hash,
      record: verificationRecord,
    });
    const statusReadGate = (): GatewayResult | undefined => {
      refreshManagedPolicy(context);
      if (!isFeatureEnabled(statusInvocation.descriptor, context.features)) {
        return featureDisabled(statusInvocation.identity.project, EDITOR_STATUS_COMMAND, statusInvocation.descriptor.feature_flag);
      }
      const policy = evaluatePolicy(context.policy, statusInvocation.identity.project, statusInvocation.descriptor);
      return policy.action === "allow" ? undefined : permissionDenied(statusInvocation.identity.project, EDITOR_STATUS_COMMAND, args.request_id, policy);
    };
    if (statusReadGate()) return unverified("unavailable");
    const statusResult = await invokePreparedCommand(context, statusInvocation, args.request_id, statusArgs, deadlineUnixMs, true,
      () => { readRequests += 1; }, undefined, statusReadGate, statusReadGate);
    const postVerificationBlocker = externalSceneReloadVerificationResult(args, originalResult, statusResult, verificationRecord.project);
    if (postVerificationBlocker) return withPlayModeTransitionRecoveryEvidence(postVerificationBlocker, invocation, target, operationId, catalogMetadataEvidence());
    if (playModeTargetReached(statusResult, target, operationId, verificationRole)) {
      const statusData = statusResult.status === "ok" && isObjectRecord(statusResult.data) ? { ...statusResult.data } : {};
      // These diagnostics describe Gateway metadata reads, not Unity payloads.
      delete statusData.catalog_metadata_read_retry_count;
      delete statusData.catalog_metadata_last_retry_reason;
      delete statusData.catalog_metadata_converged;
      return ok({
        ...optionalRequestId(args.request_id),
        project: verificationRecord.project,
        command: args.command,
        data: {
          ...statusData,
          playmode_target: target,
          playmode_transition_operation_id: operationId,
          playmode_transition_verified: true,
          verification_command: EDITOR_STATUS_COMMAND,
          verification_instance_role: verificationRole,
          verification_catalog_hash_changed: verificationRecord.commands_hash !== invocation.commandsHash,
          ...catalogMetadataEvidence(),
          original_command_completed: true,
          write_command_repeated: false,
          write_replay_allowed: false,
          command_replayed: false,
        },
        diagnostics: originalResult.diagnostics,
        next_valid_actions: [target === "playing" ? "continue_in_play_mode" : "continue_in_edit_mode", "optionally_call_editor.status"],
      });
    }
    if (statusResult.status === "error" && !isRetryableCompileVerificationResult(statusResult)) {
      return unverified("unavailable");
    }
    await waitForNextAttempt();
  }
  return unverified(catalogMetadataPending ? "catalog_stale" : "timeout");

  function catalogMetadataEvidence(): PlayModeVerificationReadEvidence {
    return {
      verification_attempt_count: attempts,
      verification_read_request_count: readRequests,
      catalog_metadata_read_retry_count: catalogMetadataReadRetryCount,
      ...(catalogMetadataLastRetryReason ? {
        catalog_metadata_last_retry_reason: catalogMetadataLastRetryReason,
        catalog_metadata_converged: !catalogMetadataPending,
      } : {}),
    };
  }

  function unverified(status: string): GatewayResult {
    return playModeTransitionUnverified(invocation, args, originalResult, target, operationId, status, attempts, readRequests, catalogMetadataEvidence());
  }

  async function waitForNextAttempt(): Promise<void> {
    const remainingMs = deadlineUnixMs - nowMs(context);
    if (remainingMs > 0) await sleepMs(context, Math.min(pollIntervalMs, remainingMs));
  }
}

function playModeTarget(command: string, commandArgs: Record<string, unknown>): PlayModeTarget | undefined {
  if (command === "editor.playmode.enter") return "playing";
  if (command === "editor.playmode.exit") return "editing";
  return command === "editor.playmode.set" && (commandArgs.state === "playing" || commandArgs.state === "editing") ? commandArgs.state : undefined;
}

function playModeCatalogMetadataProvenanceMatches(invocation: InvocationContextSnapshot, record: HeartbeatRecord, descriptor: CommandDescriptor): boolean {
  const source = invocation.record;
  const packageVersion = invocation.descriptor.package_version;
  return source !== undefined
    && source.project === record.project
    && source.canonical_project_root === record.canonical_project_root
    && sameUnityProcessGeneration(source, record)
    && isTrustedBuiltInDescriptor(invocation.descriptor)
    && isTrustedBuiltInDescriptor(descriptor)
    && typeof packageVersion === "string" && packageVersion.length > 0
    && source.package_version === packageVersion
    && record.package_version === packageVersion
    && descriptor.package_version === packageVersion;
}

function playModeTargetReached(result: GatewayResult, target: PlayModeTarget, operationId: string, role: "current" | "replacement"): boolean {
  if (result.status !== "ok" || !isObjectRecord(result.data)) return false;
  const data = result.data;
  const transition = isObjectRecord(data.playmode_transition) ? data.playmode_transition : undefined;
  if (data.is_compiling === true || data.is_updating === true || typeof data.is_playing_actual !== "boolean") return false;
  const requestedAt = safeBarrierTimestamp(transition?.requested_utc);
  const completedAt = safeBarrierTimestamp(transition?.completed_utc);
  const postReloadAt = safeBarrierTimestamp(transition?.post_reload_observed_utc);
  const reloadProofRequired = transition?.reload_expected !== false || role === "replacement";
  const correlated = transition?.state === "verified"
    && transition.operation_id === operationId
    && transition.target === target
    && transition.requested === true
    && transition.completed === true
    && requestedAt !== undefined
    && completedAt !== undefined
    && completedAt >= requestedAt
    && (!reloadProofRequired || (transition.post_reload_observed === true && postReloadAt !== undefined && postReloadAt >= requestedAt));
  if (!correlated) return false;
  return target === "playing"
    ? data.is_playing_actual === true && (data.status === "playing" || data.status === "playing_paused")
    : data.is_playing_actual === false && data.is_playing_or_will_change_playmode === false && data.status === "editing";
}

function playModeTransitionUnverified(invocation: InvocationContextSnapshot, args: UnityCallArgs, originalResult: GatewayResult, target: PlayModeTarget, operationId: string, status: string, attempts: number, readRequests: number, catalogMetadataEvidence?: PlayModeCatalogMetadataEvidence): GatewayResult {
  return fail({
    ...optionalRequestId(args.request_id),
    project: originalResult.project ?? args.project,
    command: args.command,
    error: {
      code: "unity_busy",
      message: "Play Mode command was dispatched but the target lifecycle state was not verified before the original deadline",
      details: {
        ...playModeTransitionRecoveryEvidence(invocation, target, operationId),
        reason: "playmode_transition_unverified",
        verification_status: status,
        verification_command: EDITOR_STATUS_COMMAND,
        verification_attempt_count: attempts,
        verification_read_request_count: readRequests,
        ...catalogMetadataEvidence,
        delivery_state: "dispatched",
        original_error_code: originalResult.status === "error" ? originalResult.error.code : null,
        original_command_completed: "unknown",
        write_command_repeated: false,
        write_replay_allowed: false,
      },
    },
    diagnostics: originalResult.diagnostics,
    next_valid_actions: ["poll_exact_editor.status", "do_not_auto_repeat_playmode_command", "call_sgg_unity_projects"],
  });
}

function withPlayModeTransitionRecoveryEvidence(result: GatewayResult, invocation: InvocationContextSnapshot, target: PlayModeTarget, operationId: string, catalogMetadataEvidence?: PlayModeVerificationReadEvidence): GatewayResult {
  if (result.status !== "error") return result;
  const details = { ...result.error.details };
  delete details.catalog_metadata_read_retry_count;
  delete details.catalog_metadata_last_retry_reason;
  delete details.catalog_metadata_converged;
  delete details.verification_attempt_count;
  delete details.verification_read_request_count;
  return {
    ...result,
    error: {
      ...result.error,
      details: { ...details, ...catalogMetadataEvidence, ...playModeTransitionRecoveryEvidence(invocation, target, operationId) },
    },
    next_valid_actions: ["poll_exact_editor.status", "do_not_auto_repeat_playmode_command", "call_sgg_unity_projects"],
  };
}

function playModeTransitionRecoveryEvidence(invocation: InvocationContextSnapshot, target: PlayModeTarget | undefined, operationId: string): Record<string, unknown> {
  const project = invocation.record?.canonical_project_root ?? invocation.identity.projectSelector;
  return {
    playmode_transition_recovery_contract: PLAY_MODE_TRANSITION_RECOVERY_CONTRACT,
    playmode_transition_operation_id: operationId,
    operation_id_source: "gateway_reserved",
    ...(target ? { playmode_target: target } : {}),
    status_check_required_before_repeat: true,
    status_lookup: {
      tool: "sgg_unity_call",
      project,
      command: EDITOR_STATUS_COMMAND,
      args_json: JSON.stringify({ _playmode_transition_expectation_id: operationId }),
      mode: "wait",
    },
    command_replayed: false,
    write_command_repeated: false,
    write_replay_allowed: false,
  };
}

type CompileRefreshStabilityReason = "direct_success" | "empty_response" | "lifecycle_shutdown" | "timeout" | "bridge_unreachable";

async function completeCompileRefreshWithinQueue(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  args: UnityCallArgs,
  commandArgs: Record<string, unknown>,
  result: GatewayResult,
  deadlineUnixMs: number,
): Promise<GatewayResult> {
  const expectedBarrier = barrierExpectationId(commandArgs);
  const expectedScriptPaths = expectedScriptPathsForVerification(commandArgs.expected_script_paths);
  if (result.status === "error") {
    if (args.command === VALIDATION_AFTER_EDIT_COMMAND && isDefinitiveValidationRefreshTimeout(result)) return result;
    const cancelledBeforeStart: boolean = isCompileRefreshCancelledBeforeStart(result);
    if (cancelledBeforeStart) return normalizeCompileRefreshCancelledBeforeStart(result);
    if (isCompileFailureVerification(result, expectedBarrier, invocation.record?.instance_id)) return normalizeDirectCompileFailureNoReplay(result);
    if (expectedScriptPaths && result.error.code === "compile_freshness_unverified" && result.error.details?.reason === "expected_script_not_imported") {
      return normalizeUnverifiedDirectExpectedScriptFailure(result, expectedScriptPaths);
    }
    if (result.error.code === "unity_reloading") {
      return verifyCompileRefreshPostReload(context, invocation, args, commandArgs, result, deadlineUnixMs, "lifecycle_shutdown");
    }
    if (isCompleteEmptyInvokeBridgeProtocolError(result)) {
      return verifyCompileRefreshPostReload(context, invocation, args, commandArgs, result, deadlineUnixMs, "empty_response");
    }
    if (isDispatchedCompileRefreshBridgeUnreachable(result)) {
      return verifyCompileRefreshPostReload(context, invocation, args, commandArgs, result, deadlineUnixMs, "bridge_unreachable");
    }
    if (isCompileRefreshTimeoutResult(result)) {
      return verifyCompileRefreshPostReload(context, invocation, args, commandArgs, result, deadlineUnixMs, "timeout");
    }
    return result;
  }
  if (isCompileFailureVerification(result, expectedBarrier, invocation.record?.instance_id)) return normalizeDirectCompileFailureNoReplay(result);
  if (expectedScriptPaths
    && isCompileStatusReadyCleanIgnoringExpectedScriptEvidence(result, expectedBarrier)
    && !isCompileStatusReadyClean(result, expectedBarrier, expectedScriptPaths)) {
    return normalizeMissingExpectedScriptEvidence(result, expectedScriptPaths.length);
  }
  if (isCompileStatusReadyClean(result, expectedBarrier, expectedScriptPaths)) {
    return verifyCompileRefreshPostReload(context, invocation, args, commandArgs, result, deadlineUnixMs, "direct_success");
  }
  if (isCompileRefreshTimeoutResult(result)) return verifyCompileRefreshPostReload(context, invocation, args, commandArgs, result, deadlineUnixMs, "timeout");
  return result;
}

function normalizeUnverifiedDirectExpectedScriptFailure(result: Extract<GatewayResult, { status: "error" }>, expectedScriptPaths: string[]): GatewayResult {
  return fail({
    ...optionalRequestId(result.request_id),
    ...(result.project ? { project: result.project } : {}),
    ...(result.command ? { command: result.command } : {}),
    error: {
      code: "compile_freshness_unverified",
      message: "Expected script import failure was reported without correlated replacement verification",
      details: {
        ...(safeVerifiedCompileResult(result, expectedScriptPaths) ?? {}),
        reason: "expected_script_evidence_unverified",
        reported_reason: "expected_script_not_imported",
        expected_script_count: expectedScriptPaths.length,
        delivery_state: "dispatched",
        original_command_completed: true,
        post_reload_stability_verified: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      },
    },
    diagnostics: result.diagnostics,
    next_valid_actions: ["inspect_expected_script_paths", "use_compile.wait_ready_for_read_only_verification", "run_compile.refresh_and_wait_only_as_a_new_explicit_action"],
  });
}

function normalizeMissingExpectedScriptEvidence(result: GatewayResult, expectedCount: number): GatewayResult {
  if (result.status !== "ok") return result;
  return fail({
    ...optionalRequestId(result.request_id),
    ...(result.project ? { project: result.project } : {}),
    ...(result.command ? { command: result.command } : {}),
    error: {
      code: "compile_freshness_unverified",
      message: "Compile completed without complete evidence for every expected project script",
      details: {
        ...(safeVerifiedCompileResult(result) ?? {}),
        reason: "expected_script_evidence_incomplete",
        expected_script_count: expectedCount,
        delivery_state: "dispatched",
        original_command_completed: true,
        post_reload_stability_verified: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      },
    },
    diagnostics: result.diagnostics,
    next_valid_actions: ["inspect_expected_script_paths", "run_compile.refresh_and_wait_only_as_a_new_explicit_action"],
  });
}

function normalizeDirectCompileFailureNoReplay(result: GatewayResult): GatewayResult {
  if (result.status !== "error") return result;
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...(result.error.details ?? {}),
        delivery_state: "dispatched",
        original_command_completed: true,
        post_reload_required: false,
        post_reload_stability_verified: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      },
    },
  };
}

async function invokeValidationAfterEditWithVerification(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>): Promise<GatewayResult> {
  const barrierCommandArgs = {
    ...commandArgs,
    _compile_barrier_expectation_id: randomUUID().replaceAll("-", ""),
  };
  const result = await invokeUnityCallOnce(context, args, commandArgs, undefined, true, barrierCommandArgs);

  if (compileRefreshVerificationHandledResults.has(result)) {
    return isDefinitiveValidationRefreshTimeout(result) ? verifyValidationAfterEditTimeout(context, args, result) : result;
  }

  if (isValidationAfterEditTimeoutResult(result)) {
    return verifyValidationAfterEditTimeout(context, args, result);
  }

  return result;
}

async function invokeWriteCommandWithEmptyBodyVerification(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>): Promise<GatewayResult> {
  const result = await invokeUnityCallOnce(context, args, commandArgs);
  if (!isInvokeEmptyBodyBridgeProtocolError(result)) {
    return result;
  }

  return verifyEmptyBodyWriteCommand(context, args, commandArgs, result);
}

async function verifyEmptyBodyWriteCommand(context: DispatcherRuntimeContext, args: UnityCallArgs, commandArgs: Record<string, unknown>, result: Extract<GatewayResult, { status: "error" }>): Promise<GatewayResult> {
  const verificationArgs = compileWaitReadyArgsFromWriteArgs(commandArgs);
  const verificationProject = emptyBodyVerificationProjectSelector(args.project, result);
  const verification = await invokeWithPreservedAuditMetadata(context, () => invokeUnityCallWithRiskAwareRecovery(context, {
    project: verificationProject,
    command: COMPILE_WAIT_READY_COMMAND,
    args_json: JSON.stringify(verificationArgs),
    mode: "wait",
    request_id: args.request_id,
  }, verificationArgs, undefined, true));

  return emptyBodyVerificationResult(args, result, verification, verificationProject);
}

type CompileRefreshVerificationStatus = "ready_clean" | "background_compilation_pending" | "compilation_failed" | "expected_script_not_imported" | "timeout" | "unavailable" | "catalog_stale" | "ambiguous";
type CompileCatalogMetadataLagReason = "catalog_generation_mismatch" | "commands_hash_mismatch";

type CompileRefreshVerificationEvidence = {
  verification_status: CompileRefreshVerificationStatus;
  verification_command: typeof COMPILE_WAIT_READY_COMMAND;
  selector_strategy: "failed_heartbeat_canonical_project_identity";
  attempt_count: number;
  attempt_budget: number;
  poll_interval_ms: number;
  verification_timeout_ms: number;
  verification_elapsed_ms: number;
  read_request_count: number;
  catalog_metadata_read_retry_count: number;
  catalog_metadata_last_retry_reason?: CompileCatalogMetadataLagReason;
  catalog_metadata_converged?: boolean;
  write_request_count: 1;
  write_command_repeated: false;
  write_replay_allowed: false;
  command_replayed: false;
  stability_reason: CompileRefreshStabilityReason;
  post_reload_required: boolean;
  verification_catalog_hash_changed: boolean;
  failed_instance: Record<string, unknown>;
  verification_instance_role?: "current" | "replacement";
  replacement_instance?: Record<string, unknown>;
  verified_compile_result?: Record<string, unknown>;
  original_outcome: Record<string, unknown>;
  original_error?: Record<string, unknown>;
};

async function verifyCompileRefreshPostReload(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  args: UnityCallArgs,
  commandArgs: Record<string, unknown>,
  originalResult: GatewayResult,
  deadlineUnixMs: number,
  reason: CompileRefreshStabilityReason,
): Promise<GatewayResult> {
  const failedRecord = invocation.record;
  if (!failedRecord) return originalResult;
  const recoveryRecord = failedRecord;
  const registry = context.registry;
  const verificationArgs = compileWaitReadyArgsFromRefreshArgs(commandArgs);
  const expectedScriptPaths = expectedScriptPathsForVerification(commandArgs.expected_script_paths);
  const pollIntervalMs = integerArg(verificationArgs.poll_interval_ms, DEFAULT_COMPILE_WAIT_READY_POLL_INTERVAL_MS, 50, 5_000);
  const verificationStartedAtMs = nowMs(context);
  const verificationTimeoutMs = Math.max(0, deadlineUnixMs - verificationStartedAtMs);
  const maxAttempts = Math.min(MAX_COMPILE_REFRESH_VERIFICATION_ATTEMPTS, Math.ceil(verificationTimeoutMs / pollIntervalMs) + 1);
  let attemptCount = 0;
  let readRequestCount = 0;
  let verificationRecord: HeartbeatRecord | undefined;
  let verificationRole: "current" | "replacement" | undefined;
  let verifiedCompileResult: Record<string, unknown> | undefined;
  let lastVerification: GatewayResult | undefined;
  let catalogMetadataReadRetryCount = 0;
  let catalogMetadataLastRetryReason: CompileCatalogMetadataLagReason | undefined;
  let catalogMetadataPending = false;
  if (!registry || !context.bridge) return complete("unavailable");

  while (attemptCount < maxAttempts && nowMs(context) < deadlineUnixMs) {
    attemptCount += 1;
    const externalSceneBlocker = externalSceneReloadBlockerResult(context, [recoveryRecord], {
      project: recoveryRecord.project,
      projectSelector: args.project,
      command: args.command,
      requestId: args.request_id,
      writeRequestIssued: true,
    });
    if (externalSceneBlocker) return externalSceneBlocker;
    let resolution: ReturnType<ProjectRegistry["resolveVerificationProject"]>;
    try {
      resolution = registry.resolveVerificationProject(recoveryRecord.canonical_project_root, recoveryRecord.instance_id);
    } catch {
      return complete("unavailable");
    }

    if (resolution.status === "error") {
      if (resolution.code === "duplicate_project_id") return complete("ambiguous");
      await waitForNextAttempt();
      continue;
    }

    verificationRecord = resolution.record;
    verificationRole = resolution.role;
    if (verificationRole !== "current" && verificationRole !== "replacement") {
      await waitForNextAttempt();
      continue;
    }
    if (isTransientBridgeLifecycleStatus(verificationRecord.status) || verificationRecord.bridge_port <= 0) {
      await waitForNextAttempt();
      continue;
    }
    if (verificationRole === "replacement" && !sameUnityProcessGeneration(recoveryRecord, verificationRecord)) return complete("unavailable");

    let resolvedVerification: SingleCommandResponse;
    try {
      if (catalogMetadataPending) catalogMetadataReadRetryCount += 1;
      const described = await context.bridge.describe(
        verificationRecord,
        `command:${COMPILE_WAIT_READY_COMMAND}`,
        recoveryTransport(context, deadlineUnixMs, args.request_id),
      );
      if (!isObjectRecord(described) || typeof described.commands_hash !== "string" || !("command" in described)) return complete("unavailable");
      resolvedVerification = described as SingleCommandResponse;
    } catch (error) {
      const metadataLag = compileCatalogMetadataLagReason(error, invocation, verificationRecord);
      if (metadataLag) {
        // Discard the rejected catalog; the next bounded iteration resolves a
        // fresh heartbeat and describes again. Never invoke against mismatches.
        catalogMetadataLastRetryReason = metadataLag;
        catalogMetadataPending = true;
        await waitForNextAttempt();
        continue;
      }
      if (isRetryableVerificationTransportError(error)) {
        await waitForNextAttempt();
        continue;
      }
      return complete("unavailable");
    }

    if (resolvedVerification.commands_hash === verificationRecord.commands_hash) catalogMetadataPending = false;
    const verificationDescriptor = resolvedVerification.command;
    if (!lifecycleVerificationDescriptorMatches(invocation, verificationRecord, resolvedVerification.commands_hash, verificationDescriptor, COMPILE_WAIT_READY_COMMAND)) {
      return complete("catalog_stale");
    }
    if (validateCommandArgs(verificationDescriptor.schema, compileWaitReadyArgsFromWriteArgs(commandArgs), isTrustedBuiltInDescriptor(verificationDescriptor)).status !== "ok") return complete("catalog_stale");

    let verificationInvocation: InvocationContextSnapshot;
    try {
      verificationInvocation = InvocationContextSnapshot.create({
        projectSelector: verificationRecord.instance_id,
        project: verificationRecord.project,
        command: COMPILE_WAIT_READY_COMMAND,
        descriptor: verificationDescriptor,
        commandsHash: resolvedVerification.commands_hash,
        record: verificationRecord,
      });
    } catch {
      return complete("unavailable");
    }
    readRequestCount += 1;
    let verification: GatewayResult;
    try {
      verification = await invokePreparedCommand(context, verificationInvocation, args.request_id, verificationArgs, deadlineUnixMs, true);
    } catch {
      return complete("unavailable");
    }
    lastVerification = verification;
    const postVerificationBlocker = externalSceneReloadVerificationResult(args, originalResult, verification, verification.project ?? originalResult.project ?? recoveryRecord.project);
    if (postVerificationBlocker) return postVerificationBlocker;
    verifiedCompileResult = safeVerifiedCompileResult(verification);
    if (verificationRole === "current") {
      if (isExactTerminalCompileFailureWithoutReload(verification, barrierExpectationId(commandArgs), recoveryRecord.instance_id)) return complete("compilation_failed");
      if (verification.status === "error" && verification.error.code === "catalog_stale") return complete("catalog_stale");
      if (verification.status === "error" && verification.error.code === "duplicate_project_id") return complete("ambiguous");
      await waitForNextAttempt();
      continue;
    }
    if (sameUnityProcessGeneration(recoveryRecord, verificationRecord)
      && isExpectedScriptImportFailure(verification, expectedScriptPaths, barrierExpectationId(commandArgs), recoveryRecord.instance_id, verificationRecord.instance_id)) return complete("expected_script_not_imported");
    if (sameUnityProcessGeneration(recoveryRecord, verificationRecord)
      && isCompilePostReloadReadyClean(verification, barrierExpectationId(commandArgs), recoveryRecord.instance_id, verificationRecord.instance_id, expectedScriptPaths)) return complete("ready_clean");
    if (isTrustedBuiltInDescriptor(invocation.descriptor) && isTrustedBuiltInDescriptor(verificationDescriptor)
      && sameUnityProcessGeneration(recoveryRecord, verificationRecord)
      && isCorrelatedBackgroundCompilerPending(verification, verificationRecord, context, barrierExpectationId(commandArgs), recoveryRecord.instance_id, verificationRecord.instance_id)) return complete("background_compilation_pending");
    if (isCompileVerificationTimeout(verification)) return complete("timeout");
    if (verification.status === "error" && verification.error.code === "catalog_stale") return complete("catalog_stale");
    if (verification.status === "error" && verification.error.code === "duplicate_project_id") return complete("ambiguous");
    if (isRetryableCompileVerificationResult(verification)) {
      await waitForNextAttempt();
      continue;
    }
    return complete("unavailable");
  }

  return complete(catalogMetadataPending ? "catalog_stale" : "timeout");

  async function waitForNextAttempt(): Promise<void> {
    const remainingMs = deadlineUnixMs - nowMs(context);
    if (remainingMs > 0) await sleepMs(context, Math.min(pollIntervalMs, remainingMs));
  }

  function complete(status: CompileRefreshVerificationStatus): GatewayResult {
    const validationOperation = args.command === VALIDATION_AFTER_EDIT_COMMAND;
    const verificationField = validationOperation ? "validation_after_edit_verification" : "compile_refresh_verification";
    const terminalFailureWithoutReload = status === "compilation_failed"
      && verifiedCompileResult?.terminal_compile_failure_without_reload === true;
    const timeoutRecovery = reason === "timeout" && lastVerification
      ? compileRefreshTimeoutRecoveryMetadata(args.command, originalResult, lastVerification, status === "ready_clean", args.project, barrierExpectationId(commandArgs))
      : undefined;
    const evidence: CompileRefreshVerificationEvidence = {
      verification_status: status,
      verification_command: COMPILE_WAIT_READY_COMMAND,
      selector_strategy: "failed_heartbeat_canonical_project_identity",
      attempt_count: attemptCount,
      attempt_budget: maxAttempts,
      poll_interval_ms: pollIntervalMs,
      verification_timeout_ms: verificationTimeoutMs,
      verification_elapsed_ms: Math.max(0, nowMs(context) - verificationStartedAtMs),
      read_request_count: readRequestCount,
      catalog_metadata_read_retry_count: catalogMetadataReadRetryCount,
      ...(catalogMetadataLastRetryReason ? {
        catalog_metadata_last_retry_reason: catalogMetadataLastRetryReason,
        catalog_metadata_converged: !catalogMetadataPending,
      } : {}),
      write_request_count: 1,
      write_command_repeated: false,
      write_replay_allowed: false,
      command_replayed: false,
      stability_reason: reason,
      post_reload_required: !terminalFailureWithoutReload,
      verification_catalog_hash_changed: verificationRecord !== undefined && verificationRecord.commands_hash !== invocation.commandsHash,
      failed_instance: safeCompileVerificationInstance(recoveryRecord, context),
      ...(verificationRole ? { verification_instance_role: verificationRole } : {}),
      ...(verificationRecord && verificationRole === "replacement" ? { replacement_instance: safeCompileVerificationInstance(verificationRecord, context) } : {}),
      ...(verifiedCompileResult ? { verified_compile_result: verifiedCompileResult } : {}),
      original_outcome: {
        status: originalResult.status,
        ...(originalResult.status === "error" ? { error_code: originalResult.error.code } : {}),
      },
      ...(originalResult.status === "error" ? { original_error: safeOriginalEmptyBodyError(originalResult.error) } : {}),
    };
    context.auditCompileVerification = { status, attempts: attemptCount, readRequests: readRequestCount };
    if (status === "ready_clean" && verifiedCompileResult) {
      const validationReady = verifiedCompileResult.ready === true && verifiedCompileResult.has_unsaved_scene_changes !== true;
      const completedData = validationOperation ? {
        stage: "after_edit",
        validation_ready: validationReady,
        refreshed: true,
        checks: {
          editor_ready: verifiedCompileResult.editor_ready !== false,
          compilable: verifiedCompileResult.compilable !== false,
          no_unsaved_scene_changes: verifiedCompileResult.has_unsaved_scene_changes !== true,
          timed_out: false,
        },
        compile_status: verifiedCompileResult,
        ...verifiedCompileResult,
      } : verifiedCompileResult;
      return ok({
        ...optionalRequestId(args.request_id),
        project: originalResult.project ?? recoveryRecord.project,
        command: args.command,
        data: {
          ...completedData,
          post_reload_stability_verified: true,
          ...(reason === "empty_response" ? { recovered_from_empty_bridge_response: true, original_response_lost: true, recovery_disposition: "verified_complete_no_replay" } : {}),
          ...(reason === "bridge_unreachable" ? { recovered_from_bridge_unreachable: true, original_response_lost: true, recovery_disposition: "verified_complete_no_replay" } : {}),
          ...(reason === "lifecycle_shutdown" ? { recovered_from_lifecycle_shutdown: true, recovery_disposition: "verified_complete_no_replay" } : {}),
          ...(reason === "timeout" ? { recovered_after_timeout: true, recovery_disposition: "verified_complete_no_replay" } : {}),
          ...(timeoutRecovery ?? {}),
          original_command_completed: true,
          write_command_repeated: false,
          write_replay_allowed: false,
          command_replayed: false,
          [verificationField]: evidence,
        },
        diagnostics: originalResult.diagnostics,
        next_valid_actions: validationOperation
          ? ["do_not_repeat_validation.after_edit", validationReady ? "continue_after_verified_validation" : "inspect_validation_checks", "optionally_call_compile.status"]
          : ["do_not_repeat_compile.refresh_and_wait", "continue_after_verified_ready_clean", "optionally_call_compile.status"],
      });
    }

    if (status === "compilation_failed" && verifiedCompileResult) {
      return fail({
        ...optionalRequestId(args.request_id),
        project: originalResult.project ?? recoveryRecord.project,
        command: args.command,
        error: {
          code: "compilation_failed",
          message: validationOperation ? "Validation compilation finished with exact correlated compiler errors without a domain reload" : "Unity compilation finished with exact correlated compiler errors without a domain reload",
          details: {
            ...verifiedCompileResult,
            post_reload_required: false,
            post_reload_stability_verified: false,
            terminal_compile_failure_without_reload: true,
            write_command_repeated: false,
            write_replay_allowed: false,
            command_replayed: false,
            ...(reason === "empty_response" ? { recovered_from_empty_bridge_response: true, original_response_lost: true } : {}),
            ...(reason === "bridge_unreachable" ? { recovered_from_bridge_unreachable: true } : {}),
            ...(timeoutRecovery ?? {}),
            [verificationField]: evidence,
          },
        },
        diagnostics: originalResult.diagnostics,
        ...(originalResult.next_valid_actions ? { next_valid_actions: originalResult.next_valid_actions } : {}),
      });
    }

    if (status === "background_compilation_pending" && verifiedCompileResult) {
      return fail({
        ...optionalRequestId(args.request_id),
        project: originalResult.project ?? recoveryRecord.project,
        command: args.command,
        error: {
          code: "background_compilation_pending",
          message: "The exact post-reload compile barrier is clean; authoritative Unity background compiler work remains active",
          details: {
            ...verifiedCompileResult,
            reason: "background_compilation_pending",
            original_lifecycle_reason: reason,
            original_command_completed: "unknown",
            post_reload_stability_verified: true,
            recovery_disposition: "verified_background_pending_no_replay",
            write_command_repeated: false,
            write_replay_allowed: false,
            command_replayed: false,
            [verificationField]: evidence,
          },
        },
        diagnostics: originalResult.diagnostics,
        next_valid_actions: ["call_compile.wait_ready", "call_compile.status", validationOperation ? "do_not_auto_repeat_validation.after_edit" : "do_not_auto_repeat_compile.refresh_and_wait"],
      });
    }

    if (status === "expected_script_not_imported" && lastVerification?.status === "error" && verifiedCompileResult) {
      return fail({
        ...optionalRequestId(args.request_id),
        project: originalResult.project ?? recoveryRecord.project,
        command: args.command,
        error: {
          code: "compile_freshness_unverified",
          message: "One or more expected project scripts were not imported by Unity",
          details: {
            ...verifiedCompileResult,
            reason: "expected_script_not_imported",
            delivery_state: "dispatched",
            original_command_completed: true,
            post_reload_stability_verified: true,
            recovery_disposition: "verified_incomplete_no_replay",
            write_command_repeated: false,
            write_replay_allowed: false,
            command_replayed: false,
            [verificationField]: evidence,
          },
        },
        diagnostics: originalResult.diagnostics,
        next_valid_actions: ["inspect_expected_script_paths", "fix_missing_or_malformed_script_metadata", "run_compile.refresh_and_wait_only_as_a_new_explicit_action"],
      });
    }

    const originalError = originalResult.status === "error" ? originalResult.error : undefined;
    const verificationFreshnessError = reason === "timeout" && lastVerification?.status === "error"
      && (lastVerification.error.code === "compile_freshness_unverified" || lastVerification.error.code === "compile_freshness_unavailable")
      ? lastVerification.error
      : undefined;
    const preserveOriginalError = reason === "empty_response" || reason === "timeout" || reason === "bridge_unreachable";
    return fail({
      ...optionalRequestId(args.request_id),
      project: originalResult.project ?? recoveryRecord.project,
      command: args.command,
      error: {
        code: verificationFreshnessError?.code ?? (preserveOriginalError && originalError ? originalError.code : "compile_freshness_unverified"),
        message: verificationFreshnessError?.message ?? (preserveOriginalError && originalError ? originalError.message : (validationOperation ? "Validation refresh completed but post-reload lifecycle continuity could not be verified" : "Compile completed but post-reload lifecycle continuity could not be verified")),
        details: {
          ...(originalError ? safeRecoveryErrorDetails(originalError.details) : {}),
          ...(verificationFreshnessError?.details ?? {}),
          ...(timeoutRecovery ?? {}),
          delivery_state: "dispatched",
          original_command_completed: status === "ready_clean" ? true : "unknown",
          post_reload_stability_verified: false,
          write_command_repeated: false,
          write_replay_allowed: false,
          command_replayed: false,
          [verificationField]: evidence,
        },
      },
      diagnostics: originalResult.diagnostics,
      next_valid_actions: [validationOperation ? "do_not_repeat_validation.after_edit" : "do_not_repeat_compile.refresh_and_wait", "call_compile.wait_ready", "call_compile.status"],
    });
  }
}

function isRetryableVerificationTransportError(error: unknown): boolean {
  return error instanceof BridgeRequestError && (error.message === "bridge_unreachable" || error.message === "bridge_timeout");
}

function compileCatalogMetadataLagReason(error: unknown, invocation: InvocationContextSnapshot, record: HeartbeatRecord): CompileCatalogMetadataLagReason | undefined {
  if (!(error instanceof BridgeRequestError) || error.message !== "bridge_protocol_error") return undefined;
  const details = error.details;
  const reason = details?.protocol_reason;
  if (details?.endpoint !== "/commands"
    || details.failure_kind !== "response_shape"
    || details.protocol_schema !== "sgg.unity_mcp.bridge.command_catalog.v1"
    || (reason !== "catalog_generation_mismatch" && reason !== "commands_hash_mismatch")
    || details.bridge_host !== record.bridge_host
    || details.bridge_port !== record.bridge_port) return undefined;

  const source = invocation.record;
  const packageVersion = invocation.descriptor.package_version;
  if (!source || !isTrustedBuiltInDescriptor(invocation.descriptor)
    || !sameUnityProcessGeneration(source, record)
    || source.project !== record.project
    || source.canonical_project_root !== record.canonical_project_root
    || typeof packageVersion !== "string" || packageVersion.length === 0
    || source.package_version !== packageVersion
    || record.package_version !== packageVersion) return undefined;
  return reason;
}

function isRetryableCompileVerificationResult(result: GatewayResult): boolean {
  return result.status === "error" && (result.error.code === "bridge_unreachable"
    || result.error.code === "project_not_found"
    || result.error.code === "project_stale"
    || result.error.code === "unity_reloading"
    || isCompleteEmptyInvokeBridgeProtocolError(result));
}

function isCompileFailureVerification(result: GatewayResult, expectedBarrierOperationId?: string, expectedSourceInstanceId?: string): boolean {
  return isExactTerminalCompileFailureWithoutReload(result, expectedBarrierOperationId, expectedSourceInstanceId);
}

function isCompileVerificationTimeout(result: GatewayResult): boolean {
  if (result.status === "error") return result.error.code === "bridge_timeout" || result.error.code === "command_timeout";
  return isObjectRecord(result.data) && result.data.timed_out === true;
}

const SAFE_COMPILE_RESULT_BOOLEANS = [
  "is_compiling", "is_updating", "is_playing", "ready", "editor_ready", "compilable",
  "package_refresh_required", "package_refresh_wait_can_advance", "stale_package_resolution", "package_metadata_unavailable", "timed_out", "has_compile_errors", "refreshed", "source_changed",
  "generation_started", "generation_finished", "lifecycle_settled", "has_unsaved_scene_changes", "terminal_compile_failure_without_reload",
] as const;
const SAFE_COMPILE_RESULT_INTEGERS = [
  "waited_ms", "timeout_ms", "last_editor_update_age_ms", "status_duration_ms", "compile_error_count", "dirty_scene_count",
] as const;
const MAX_SAFE_COMPILE_EVIDENCE_INTEGER = 2_147_483_647;
const MAX_SAFE_COMPILE_DIAGNOSTICS = 20;
const MAX_SAFE_COMPILE_LOCATION = 10_000_000;
const MAX_COMPILE_RELOAD_EDGE_MS = 300_000;
const COMPILER_CODE_PATTERN = /^[A-Z]{1,8}\d{1,8}$/;
const SAFE_COMPILE_SEVERITIES = new Set(["error", "warning", "info"]);

function safeVerifiedCompileResult(result: GatewayResult, expectedScriptPaths?: string[]): Record<string, unknown> | undefined {
  const value = result.status === "ok" ? result.data : result.error.details;
  if (!isObjectRecord(value)) return undefined;
  const safe: Record<string, unknown> = {};
  for (const key of SAFE_COMPILE_RESULT_BOOLEANS) if (typeof value[key] === "boolean") safe[key] = value[key];
  for (const key of SAFE_COMPILE_RESULT_INTEGERS) {
    const item = boundedEvidenceInteger(value[key], 0, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
    if (item !== undefined) safe[key] = item;
  }
  const diagnostics = safeCompileDiagnostics(value.compile_diagnostics);
  if (diagnostics) safe.compile_diagnostics = diagnostics;
  const barrier = safeCompileBarrier(value.compile_barrier);
  if (barrier) safe.compile_barrier = barrier;
  const qualification = safeCompileReadinessQualification(value.readiness_qualification);
  if (qualification) safe.readiness_qualification = qualification;
  const backgroundProgress = safeBackgroundCompilerProgress(value.background_compiler_progress);
  if (backgroundProgress) safe.background_compiler_progress = backgroundProgress;
  const expectedScriptImports = safeExpectedScriptImports(value.expected_script_imports, expectedScriptPaths);
  if (expectedScriptImports) safe.expected_script_imports = expectedScriptImports;
  return Object.keys(safe).length > 0 ? safe : undefined;
}

function safeExpectedScriptImports(value: unknown, expectedScriptPaths?: string[]): Record<string, unknown> | undefined {
  if (!isObjectRecord(value) || value.required !== true || typeof value.verified !== "boolean") return undefined;
  const checkedCount = boundedEvidenceInteger(value.checked_count, 0, 64);
  const verifiedCount = boundedEvidenceInteger(value.verified_count, 0, 64);
  const missingCount = boundedEvidenceInteger(value.missing_count, 0, 64);
  if (checkedCount === undefined || verifiedCount === undefined || missingCount === undefined
    || verifiedCount + missingCount !== checkedCount) return undefined;
  const normalizedExpectedPaths = expectedScriptPaths?.map(normalizeExpectedScriptPath);
  if (normalizedExpectedPaths
    && (normalizedExpectedPaths.length < 1
      || normalizedExpectedPaths.some((path) => !isCanonicalExpectedScriptPath(path))
      || new Set(normalizedExpectedPaths.map((path) => path.toLowerCase())).size !== normalizedExpectedPaths.length
      || checkedCount !== normalizedExpectedPaths.length)) return undefined;
  const expectedPathSet = normalizedExpectedPaths ? new Set(normalizedExpectedPaths.map((path) => path.toLowerCase())) : undefined;
  if (!Array.isArray(value.items) || value.items.length !== checkedCount || value.items.length > 64) return undefined;
  const itemPathSet = new Set<string>();
  const items: Record<string, unknown>[] = [];
  for (const candidate of value.items) {
    const item = safeExpectedScriptImportItem(candidate);
    if (!item) return undefined;
    const pathKey = (item.asset_path as string).toLowerCase();
    if ((expectedPathSet && !expectedPathSet.has(pathKey)) || itemPathSet.has(pathKey)) return undefined;
    itemPathSet.add(pathKey);
    items.push(item);
  }
  if ((expectedPathSet && [...expectedPathSet].some((path) => !itemPathSet.has(path)))
    || items.filter((item) => item.verified === true).length !== verifiedCount
    || items.filter((item) => item.verified === false).length !== missingCount
    || value.verified !== (missingCount === 0)) return undefined;

  const expectedFailures = items.filter((item) => item.verified === false);
  if (!Array.isArray(value.failures) || value.failures.length !== expectedFailures.length) return undefined;
  const expectedFailureByPath = new Map(expectedFailures.map((item) => [(item.asset_path as string).toLowerCase(), item]));
  const failurePathSet = new Set<string>();
  for (const candidate of value.failures) {
    const failure = safeExpectedScriptImportItem(candidate);
    if (!failure || failure.verified !== false) return undefined;
    const pathKey = (failure.asset_path as string).toLowerCase();
    const expectedFailure = expectedFailureByPath.get(pathKey);
    if (!expectedFailure || failurePathSet.has(pathKey) || JSON.stringify(failure) !== JSON.stringify(expectedFailure)) return undefined;
    failurePathSet.add(pathKey);
  }
  if (failurePathSet.size !== expectedFailures.length) return undefined;
  return {
    required: true,
    verified: value.verified,
    checked_count: checkedCount,
    verified_count: verifiedCount,
    missing_count: missingCount,
    items,
    failures: expectedFailures,
  };
}

function normalizeExpectedScriptPath(value: string): string {
  return value.replaceAll("\\", "/").replace(/\/{2,}/g, "/");
}

function isCanonicalExpectedScriptPath(value: string): boolean {
  if (value.length < 1 || value.length > 512 || value !== normalizeExpectedScriptPath(value)
    || !value.startsWith("Assets/") || !value.toLowerCase().endsWith(".cs")) return false;
  const segments = value.split("/");
  return segments.length >= 2 && segments.every((segment) => segment.length > 0 && segment !== "." && segment !== "..");
}

function safeExpectedScriptImportItem(value: unknown): Record<string, unknown> | undefined {
  if (!isObjectRecord(value) || typeof value.asset_path !== "string" || !isCanonicalExpectedScriptPath(value.asset_path)
    || typeof value.verified !== "boolean"
    || typeof value.guid_present !== "boolean"
    || typeof value.guid_valid !== "boolean"
    || typeof value.mono_script_loaded !== "boolean"
    || typeof value.mono_script_main_asset !== "boolean"
    || typeof value.compiler_source_present !== "boolean"
    || !Array.isArray(value.failure_causes)) return undefined;
  if ((value.guid_valid && !value.guid_present) || value.mono_script_loaded !== value.mono_script_main_asset) return undefined;
  const causes = [
    ...(!value.guid_valid ? ["invalid_guid"] : []),
    ...(!value.mono_script_main_asset ? ["mono_script_missing"] : []),
    ...(!value.compiler_source_present ? ["compiler_source_missing"] : []),
  ];
  if (value.failure_causes.length !== causes.length
    || value.failure_causes.some((cause, index) => cause !== causes[index])) return undefined;
  const verified = value.guid_valid && value.mono_script_main_asset && value.compiler_source_present;
  if (value.verified !== verified) return undefined;
  return {
    asset_path: value.asset_path,
    verified,
    guid_present: value.guid_present,
    guid_valid: value.guid_valid,
    mono_script_loaded: value.mono_script_loaded,
    mono_script_main_asset: value.mono_script_main_asset,
    compiler_source_present: value.compiler_source_present,
    failure_causes: causes,
  };
}

function isExpectedScriptImportFailure(result: GatewayResult, expectedScriptPaths: string[] | undefined, expectedBarrierOperationId?: string, expectedSourceInstanceId?: string, expectedReplacementInstanceId?: string): boolean {
  if (!expectedScriptPaths) return false;
  if (result.status !== "error" || result.error.code !== "compile_freshness_unverified") return false;
  const details = result.error.details;
  if (!details || details.reason !== "expected_script_not_imported"
    || details.has_compile_errors === true
    || (typeof details.compile_error_count === "number" && details.compile_error_count !== 0)
    || !hasCorrelatedCompilePostReloadBarrier(details, expectedBarrierOperationId, expectedSourceInstanceId, expectedReplacementInstanceId)) return false;
  return safeExpectedScriptImports(details.expected_script_imports, expectedScriptPaths)?.verified === false;
}

function safeCompileBarrier(value: unknown): Record<string, unknown> | undefined {
  if (!isObjectRecord(value)) return undefined;
  const result: Record<string, unknown> = {};
  if (typeof value.state === "string" && ["verified", "pending", "failed", "abandoned", "unavailable", "unknown", "not_required"].includes(value.state)) result.state = value.state;
  if (typeof value.operation_id === "string" && /^(?:|[a-f0-9]{32})$/.test(value.operation_id)) result.operation_id = value.operation_id;
  for (const key of ["required", "requested", "request_issued", "started", "finished", "terminal_compile_failure_without_reload", "reload_started", "post_reload_observed", "settling", "reload_pending", "stable"] as const) {
    if (typeof value[key] === "boolean") result[key] = value[key];
  }
  for (const key of ["observed_generation", "request_generation", "started_generation", "finished_generation", "terminal_compile_error_count"] as const) {
    const item = boundedEvidenceInteger(value[key], 0, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
    if (item !== undefined) result[key] = item;
  }
  for (const key of ["requested_utc", "request_issued_utc", "started_utc", "finished_utc", "terminal_compile_observed_utc", "reload_started_utc", "post_reload_observed_utc"] as const) {
    if (typeof value[key] === "string" && (value[key] === "" || (!Number.isNaN(Date.parse(value[key])) && value[key].length <= 40))) result[key] = value[key];
  }
  if (typeof value.recovery === "string" && /^[a-z][a-z0-9_]{0,63}$/.test(value.recovery)) result.recovery = value.recovery;
  if (typeof value.previous_failure === "string" && /^[a-z][a-z0-9_]{0,63}$/.test(value.previous_failure)) result.previous_failure = value.previous_failure;
  if (typeof value.terminal_compile_result === "string" && ["unknown", "clean", "failed"].includes(value.terminal_compile_result)) result.terminal_compile_result = value.terminal_compile_result;
  for (const key of ["source_instance_id", "reload_source_instance_id", "replacement_instance_id"] as const) {
    if (typeof value[key] === "string" && (value[key] === "" || isSafeCompileInstanceId(value[key]))) result[key] = value[key];
  }
  return Object.keys(result).length > 0 ? result : undefined;
}

function safeBackgroundCompilerProgress(value: unknown): Record<string, unknown> | undefined {
  if (!isObjectRecord(value) || typeof value.observed !== "boolean" || typeof value.authoritative !== "boolean"
    || typeof value.active !== "boolean" || typeof value.failed !== "boolean" || typeof value.blocks_readiness !== "boolean"
    || value.cancellation_attempted !== false || value.work_replayed !== false
    || !["unobserved", "active", "failed", "settling", "complete", "unavailable"].includes(String(value.state))
    || !["none", "unknown", "compute_variant_compile", "shader_variant_compile", "native_compiler"].includes(String(value.category))) return undefined;
  const processId = boundedEvidenceInteger(value.process_id, 1, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const catalogGeneration = boundedEvidenceInteger(value.catalog_generation, 0, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const compilationGeneration = boundedEvidenceInteger(value.compilation_generation, 0, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const completedGeneration = boundedEvidenceInteger(value.completed_compilation_generation, 0, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const relevantCount = boundedEvidenceInteger(value.relevant_item_count, 0, 256);
  const activeCount = boundedEvidenceInteger(value.active_item_count, 0, 256);
  const progress = value.progress_permille === null ? null : boundedEvidenceInteger(value.progress_permille, 0, 1000);
  const observedAt = value.observed ? safeBarrierTimestamp(value.observed_utc) : value.observed_utc === "" ? "" : undefined;
  if (processId === undefined || catalogGeneration === undefined || compilationGeneration === undefined || completedGeneration === undefined
    || completedGeneration > compilationGeneration || relevantCount === undefined || activeCount === undefined || activeCount > relevantCount
    || progress === undefined || typeof value.package_fingerprint !== "string" || !/^(?:|[a-f0-9]{64})$/.test(value.package_fingerprint)
    || observedAt === undefined) return undefined;
  if (value.state === "unobserved" && (value.observed !== false || value.authoritative !== false || value.active !== false
    || value.failed !== false || value.blocks_readiness !== false || value.category !== "none" || activeCount !== 0
    || relevantCount !== 0 || progress !== null)) return undefined;
  if (value.state !== "unobserved" && value.observed !== true) return undefined;
  if (value.state === "active" && (value.authoritative !== true || value.active !== true || value.failed !== false || value.blocks_readiness !== true || activeCount < 1)) return undefined;
  if (value.state === "failed" && (value.authoritative !== true || value.active !== false || value.failed !== true || value.blocks_readiness !== true)) return undefined;
  if (value.state === "complete" && (value.authoritative !== true || value.active !== false || value.failed !== false || value.blocks_readiness !== false)) return undefined;
  if (value.state === "unavailable" && (value.authoritative !== false || value.blocks_readiness !== true)) return undefined;
  const nativeObserved = value.correlated_native_failure_observed === true;
  const nativeCategory = value.native_failure_category;
  const nativeAt = value.native_failure_observed_utc;
  if (value.correlated_native_failure_observed !== undefined && typeof value.correlated_native_failure_observed !== "boolean") return undefined;
  if (nativeObserved && (value.authoritative !== true || !["compute_variant_compile", "shader_variant_compile", "native_compiler"].includes(String(nativeCategory))
    || safeBarrierTimestamp(nativeAt) === undefined || (safeBarrierTimestamp(nativeAt) ?? Infinity) > (safeBarrierTimestamp(value.observed_utc) ?? -Infinity))) return undefined;
  if (!nativeObserved && ((nativeCategory !== undefined && nativeCategory !== "none") || (nativeAt !== undefined && nativeAt !== ""))) return undefined;
  if (value.native_failure_absence_authoritative !== undefined && value.native_failure_absence_authoritative !== false) return undefined;
  const failureReason = value.failure_reason;
  if (typeof failureReason !== "string" || !/^[A-Za-z0-9_:]{0,80}$/.test(failureReason)) return undefined;
  return {
    observed: value.observed, authoritative: value.authoritative, active: value.active, failed: value.failed,
    blocks_readiness: value.blocks_readiness, state: value.state, category: value.category,
    relevant_item_count: relevantCount, active_item_count: activeCount, progress_permille: progress,
    process_id: processId, catalog_generation: catalogGeneration, compilation_generation: compilationGeneration,
    completed_compilation_generation: completedGeneration, package_fingerprint: value.package_fingerprint,
    observed_utc: value.observed_utc, correlated_native_failure_observed: nativeObserved,
    native_failure_category: nativeObserved ? nativeCategory : "none",
    native_failure_observed_utc: nativeObserved ? nativeAt : "",
    native_failure_absence_authoritative: false,
    failure_reason: failureReason, cancellation_attempted: false, work_replayed: false,
  };
}

function safeCompileDiagnostics(value: unknown): Record<string, unknown> | undefined {
  if (!isObjectRecord(value)) return undefined;
  const result: Record<string, unknown> = {};
  if (typeof value.has_compile_errors === "boolean") result.has_compile_errors = value.has_compile_errors;
  const errorCount = boundedEvidenceInteger(value.compile_error_count, 0, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  if (errorCount !== undefined) result.compile_error_count = errorCount;
  if (typeof value.truncated === "boolean") result.truncated = value.truncated;
  const sourceErrors = Array.isArray(value.errors) ? value.errors : [];
  const errors = sourceErrors.slice(0, MAX_SAFE_COMPILE_DIAGNOSTICS).flatMap((item) => {
    if (!isObjectRecord(item)) return [];
    const safe: Record<string, unknown> = {};
    if (typeof item.severity === "string" && SAFE_COMPILE_SEVERITIES.has(item.severity)) safe.severity = item.severity;
    if (typeof item.code === "string" && COMPILER_CODE_PATTERN.test(item.code)) safe.code = item.code;
    for (const key of ["line", "column"] as const) {
      const location = boundedEvidenceInteger(item[key], 1, MAX_SAFE_COMPILE_LOCATION);
      if (location !== undefined) safe[key] = location;
    }
    return Object.keys(safe).length > 0 ? [safe] : [];
  });
  if (errors.length > 0) result.errors = errors;
  if (sourceErrors.length > MAX_SAFE_COMPILE_DIAGNOSTICS) result.truncated = true;
  return Object.keys(result).length > 0 ? result : undefined;
}

function boundedEvidenceInteger(value: unknown, minimum: number, maximum: number): number | undefined {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= minimum && value <= maximum ? value : undefined;
}

function safeCompileVerificationInstance(record: HeartbeatRecord, context: DispatcherRuntimeContext): Record<string, unknown> {
  return {
    instance_id: /^[A-Za-z0-9._:-]{1,256}$/.test(record.instance_id) ? record.instance_id : "invalid",
    bridge_port: boundedEvidenceInteger(record.bridge_port, 0, 65_535) ?? 0,
    heartbeat_age_ms: boundedEvidenceInteger(heartbeatAgeMsFor(record, context), 0, MAX_SAFE_COMPILE_EVIDENCE_INTEGER) ?? MAX_SAFE_COMPILE_EVIDENCE_INTEGER,
  };
}

async function verifyValidationAfterEditTimeout(context: DispatcherRuntimeContext, args: UnityCallArgs, result: GatewayResult): Promise<GatewayResult> {
  const verificationArgs: Record<string, unknown> = {};
  const verificationProject = timeoutVerificationProjectSelector(args.project, result);
  const verification = await invokeWithPreservedAuditMetadata(context, () => invokeUnityCallWithRiskAwareRecovery(context, {
    project: verificationProject,
    command: COMPILE_STATUS_COMMAND,
    args_json: "{}",
    mode: "wait",
    request_id: args.request_id,
  }, verificationArgs, undefined, true));

  return validationAfterEditTimeoutVerificationResult(args, result, verification, verificationProject);
}

async function invokeWithPreservedAuditMetadata<T>(context: DispatcherRuntimeContext, callback: () => Promise<T>): Promise<T> {
  const auditCommandMetadata = context.auditCommandMetadata;
  const auditPolicyMetadata = context.auditPolicyMetadata;
  const auditBridgeDurationMs = context.auditBridgeDurationMs;
  try {
    return await callback();
  } finally {
    restoreOptionalContextField(context, "auditCommandMetadata", auditCommandMetadata);
    restoreOptionalContextField(context, "auditPolicyMetadata", auditPolicyMetadata);
    restoreOptionalContextField(context, "auditBridgeDurationMs", auditBridgeDurationMs);
  }
}

function restoreOptionalContextField<K extends keyof DispatcherRuntimeContext>(context: DispatcherRuntimeContext, key: K, value: DispatcherRuntimeContext[K] | undefined): void {
  if (value === undefined) {
    delete context[key];
    return;
  }

  context[key] = value;
}

function isInvokeEmptyBodyBridgeProtocolError(result: GatewayResult): result is Extract<GatewayResult, { status: "error" }> {
  if (result.status !== "error" || result.error.code !== "bridge_protocol_error") {
    return false;
  }

  const details = result.error.details ?? {};
  return details.endpoint === "/invoke" && (details.parse_error_kind === "empty_body" || details.response_body_empty === true);
}

function emptyBodyVerificationProjectSelector(originalProject: string, originalResult: Extract<GatewayResult, { status: "error" }>): string {
  const selector = originalResult.error.details?.current_candidate_selector;
  return typeof selector === "string" && selector.trim().length > 0 ? selector : originalProject;
}

function timeoutVerificationProjectSelector(originalProject: string, originalResult: GatewayResult): string {
  if (originalResult.status !== "error") {
    return originalProject;
  }

  const selector = originalResult.error.details?.current_candidate_selector;
  return typeof selector === "string" && selector.trim().length > 0 ? selector : originalProject;
}

function isCompileRefreshTimeoutResult(result: GatewayResult): boolean {
  if (result.status === "error") {
    return result.error.code === "bridge_timeout" || result.error.code === "command_timeout";
  }

  return isObjectRecord(result.data) && result.data.timed_out === true;
}

function isCompileRefreshCancelledBeforeStart(result: GatewayResult): result is Extract<GatewayResult, { status: "error" }> {
  if (result.status !== "error" || result.error.code !== "command_timeout") return false;
  const details = result.error.details;
  const gatewayQueueTimeout = compileRefreshQueueTimeoutResults.has(result);
  const authoritativePhase = (details?.timeout_kind === "main_thread_dispatch_timeout" && details.failure_phase === "main_thread_dispatch")
    || (gatewayQueueTimeout
      && details?.timeout_kind === "gateway_project_queue_timeout"
      && details.failure_phase === "project_queue"
      && details.delivery_state === "not_dispatched"
      && details.bridge_request_issued === false);
  return details?.recovery_contract === "sgg.unity_mcp.compile_refresh_dispatch_timeout.v1"
    && authoritativePhase
    && details.timed_out_work_state === "cancelled_before_start"
    && details.late_execution_possible === false
    && details.refresh_may_still_run_after_timeout === false
    && details.refresh_started === false
    && details.refresh_completed === false
    && details.compile_barrier_created === false
    && details.script_compilation_requested === false
    && details.prior_compile_barrier_is_conflict === false
    && details.write_command_repeated === false
    && details.write_replay_allowed === false;
}

function compileRefreshQueueTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number): GatewayResult {
  const validationOperation = invocation.identity.command === VALIDATION_AFTER_EDIT_COMMAND;
  const result = fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: invocation.identity.command,
    error: {
      code: "command_timeout",
      message: validationOperation ? "Timed out waiting for project queue admission before validation refresh dispatch" : "Timed out waiting for project queue admission before compile refresh dispatch",
      details: {
        recovery_contract: "sgg.unity_mcp.compile_refresh_dispatch_timeout.v1",
        timeout_kind: "gateway_project_queue_timeout",
        failure_phase: "project_queue",
        timeout_ms: timeoutMs,
        timed_out_work_state: "cancelled_before_start",
        delivery_state: "not_dispatched",
        bridge_request_issued: false,
        late_execution_possible: false,
        refresh_may_still_run_after_timeout: false,
        refresh_started: false,
        refresh_completed: false,
        compile_barrier_created: false,
        script_compilation_requested: false,
        prior_compile_barrier_is_conflict: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      },
    },
  });
  compileRefreshQueueTimeoutResults.add(result);
  return result;
}

function settleCompileRefreshQueueBeforeDeadline(
  context: DispatcherContext,
  queued: Promise<GatewayResult>,
  deadlineUnixMs: number,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  timeoutMs: number,
  delivery: { writeRequestIssued: boolean },
): Promise<GatewayResult> {
  const remainingMs = Math.max(0, deadlineUnixMs - nowMs(context));
  return new Promise((resolve, reject) => {
    let settled = false;
    const settle = (result: GatewayResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    const timer = setTimeout(() => {
      const result = delivery.writeRequestIssued
        ? compileRefreshStabilityDeadlineTimeout(invocation, requestId, timeoutMs)
        : compileRefreshQueueTimeout(invocation, requestId, timeoutMs);
      compileRefreshVerificationHandledResults.add(result);
      settle(result);
    }, remainingMs);
    queued.then(settle, (error: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    });
  });
}

function compileRefreshStabilityDeadlineTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number): GatewayResult {
  const validationOperation = invocation.identity.command === VALIDATION_AFTER_EDIT_COMMAND;
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: invocation.identity.command,
    error: {
      code: "compile_freshness_unverified",
      message: validationOperation ? "Validation refresh was dispatched but post-reload lifecycle continuity was not verified before the original deadline" : "Compile refresh was dispatched but post-reload lifecycle continuity was not verified before the original deadline",
      details: {
        timeout_kind: "post_reload_stability_timeout",
        failure_phase: "post_reload_verification",
        timeout_ms: timeoutMs,
        delivery_state: "dispatched",
        bridge_request_issued: true,
        original_command_completed: "unknown",
        post_reload_stability_verified: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      },
    },
    next_valid_actions: [validationOperation ? "do_not_repeat_validation.after_edit" : "do_not_repeat_compile.refresh_and_wait", "call_compile.wait_ready", "call_compile.status"],
  });
}

function createTestRunOperationId(context: DispatcherContext): string {
  const now = context.now?.() ?? new Date();
  const timestamp = now.toISOString().replace(/[-:]/g, "");
  return `test-${timestamp.slice(0, 8)}-${timestamp.slice(9, 15)}-${randomUUID().replaceAll("-", "").slice(0, 8)}`;
}

function testRunOperationBinding(context: DispatcherContext, invocation: InvocationContextSnapshot, operationId: string, callerArgsHash: string): TestRunOperationBinding {
  const record = invocation.record;
  const descriptor = invocation.descriptor;
  const canonicalProjectRoot = record?.canonical_project_root ?? "";
  const packageId = descriptor.package_id ?? "";
  const packageVersion = descriptor.package_version ?? "";
  const commandsHash = invocation.commandsHash ?? "";
  const catalogGeneration = record?.catalog_generation ?? 0;
  const processStartUtc = record?.unity_process_start_utc ?? "";
  if (!record
    || !isTrustedBuiltInDescriptor(descriptor)
    || packageId !== "com.sungeargames.unity-mcp"
    || packageVersion.length === 0
    || commandsHash.length === 0
    || catalogGeneration <= 0
    || processStartUtc.length === 0) {
    throw new TestRunOperationStoreError("binding_invalid");
  }
  return {
    schema_version: 1,
    contract: "sgg.unity_mcp.test_run_operation_owner.v1",
    command: TEST_RUN_COMMAND,
    operation_id: operationId,
    project: invocation.identity.project,
    original_project_selector: invocation.identity.projectSelector,
    origin_project_root: record.project_root,
    canonical_project_root: canonicalProjectRoot,
    canonical_project_root_hash: testRunCanonicalProjectRootHash(canonicalProjectRoot),
    origin_instance_id: record.instance_id,
    origin_unity_pid: record.unity_pid,
    origin_unity_process_start_utc: processStartUtc,
    caller_args_hash: callerArgsHash,
    artifact_path: `${TEST_RUN_ARTIFACT_ROOT}/${operationId}.json`,
    artifact_schema_version: TEST_RUN_ARTIFACT_SCHEMA_VERSION,
    package_id: packageId,
    package_version: packageVersion,
    commands_hash: commandsHash,
    catalog_generation: catalogGeneration,
    artifact_schema_capability: TEST_RUN_ARTIFACT_CAPABILITY,
    created_at: (context.now?.() ?? new Date()).toISOString(),
  };
}

function testRunBindingDispatchGuard(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, binding: TestRunOperationBinding, deadlineUnixMs: number, requestId: string | undefined, bindingPublished = false): GatewayResult | undefined {
  if (!Number.isFinite(nowMs(context)) || nowMs(context) >= deadlineUnixMs) {
    return testRunBindingDispatchFailure(invocation, requestId, binding.operation_id, "binding_publication_crossed_deadline", bindingPublished);
  }
  const current = resolveProject(context, binding.origin_instance_id);
  if (current.status !== "ok" || !testRunOwnerRecordMatchesBinding(current.record, binding)) {
    return testRunBindingDispatchFailure(invocation, requestId, binding.operation_id, "owner_identity_changed_after_binding_publication", bindingPublished);
  }
  return undefined;
}

function testRunOwnerRecordMatchesBinding(record: HeartbeatRecord, binding: TestRunOperationBinding): boolean {
  return record.project === binding.project
    && record.project_root === binding.origin_project_root
    && record.canonical_project_root === binding.canonical_project_root
    && testRunCanonicalProjectRootHash(record.canonical_project_root) === binding.canonical_project_root_hash
    && record.instance_id === binding.origin_instance_id
    && record.unity_pid === binding.origin_unity_pid
    && record.unity_process_start_utc === binding.origin_unity_process_start_utc
    && record.package_version === binding.package_version
    && record.commands_hash === binding.commands_hash
    && record.catalog_generation === binding.catalog_generation;
}

function asTestRunOperationStoreError(error: unknown): TestRunOperationStoreError {
  return error instanceof TestRunOperationStoreError ? error : new TestRunOperationStoreError("storage_unavailable");
}

class TestRunPublicationDeadlineError extends TestRunOperationStoreError {
  constructor() { super("storage_unavailable"); }
}

type TestRunPublicationBudget = { deadlineUnixMs: number; monotonicDeadline: number };

function isTestRunPublicationBudgetExpired(context: DispatcherContext, budget: TestRunPublicationBudget): boolean {
  return nowMs(context) >= budget.deadlineUnixMs || performance.now() >= budget.monotonicDeadline;
}

function awaitTestRunPublicationBeforeDeadline<T>(context: DispatcherContext, publication: Promise<T>, budget: TestRunPublicationBudget): Promise<T> {
  const remainingMs = Math.max(0, Math.min(budget.deadlineUnixMs - nowMs(context), budget.monotonicDeadline - performance.now()));
  // A late commit is not cancelled or replayed. Its promise stays observed after
  // this caller detaches; absolute and monotonic checks prevent timer-race acceptance.
  return new Promise<T>((resolve, reject) => {
    let settled = false;
    const finish = (action: () => void) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      action();
    };
    const expired = () => isTestRunPublicationBudgetExpired(context, budget);
    const timer = setTimeout(() => finish(() => reject(new TestRunPublicationDeadlineError())), Math.min(remainingMs, 2_147_483_647));
    publication.then((value) => finish(() => expired() ? reject(new TestRunPublicationDeadlineError()) : resolve(value)),
      (error: unknown) => finish(() => reject(error)));
  });
}

function testRunObservedBridgeResponse(binding: TestRunOperationBinding, result: GatewayResult): Record<string, unknown> {
  if (!trustedTestRunBridgeResponses.has(result)) return { status: "unavailable", response_received: false, outcome: "unknown" };
  if (result.status === "error") {
    return {
      status: "error", error_code: result.error.code, response_source: "exact_bound_bridge_request",
      terminal_no_execution_refusal_observed: terminalNoExecutionEvidenceFromBridgeResult(result) !== undefined,
    };
  }
  try {
    const data = validateBoundLiveTestRunStatusData(binding, binding.operation_id, result.data);
    if (!isValidObservedTestRunDto(data)) throw new TypeError("Invalid observed test result shape");
    const compact = compactTestRunData(data);
    compact.terminal = !["running", "execution_may_have_started", TEST_RUN_AWAITING_FILTER_REVALIDATION_STATUS].includes(data.status as string);
    for (const key of ["operation_created", "execute_dispatch_committed", "test_runner_execute_invocation_attempted", "test_runner_execute_invoked"]) copyCompactBoolean(compact, data, key);
    copyCompactInteger(compact, data, "execute_dispatch_count");
    const evidence: Record<string, unknown> = { status: "ok", response_binding_verified: true };
    for (const key of ["operation_id", "status", "terminal", "result", "success", "counts", "operation_created", "lifecycle",
      "artifact_persisted", "artifact_publication_state", "artifact_publication_attempts", "last_published_lifecycle",
      "execute_dispatch_count", "execute_dispatch_committed", "test_runner_execute_invocation_attempted", "test_runner_execute_invoked", "pre_execute_refusal"])
      if (Object.hasOwn(compact, key)) evidence[key === "status" ? "operation_status" : key] = compact[key];
    return evidence;
  } catch {
    return { status: "rejected", response_binding_verified: false, reason: "test_run_response_binding_or_shape_invalid" };
  }
}

function withTestRunBindingPublicationState(result: GatewayResult, published: boolean): GatewayResult {
  const annotated = result.status !== "error" ? result : { ...result, error: { ...result.error,
    details: { ...result.error.details, operation_binding_published: published } } };
  if (optedInTestRunPreflightFailures.has(result)) optedInTestRunPreflightFailures.add(annotated);
  return annotated;
}

function testRunPublicationPermissionGuard(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined, operationId: string, approvedPolicyRevision: string): GatewayResult | undefined {
  refreshManagedPolicy(context);
  const degraded = degradedPolicyGate(context, invocation.identity.project, TEST_RUN_COMMAND, requestId, invocation.descriptor.risk);
  const policy = evaluatePolicy(context.policy, invocation.identity.project, invocation.descriptor);
  const code: ErrorCode | undefined = degraded ? "policy_degraded" : !isFeatureEnabled(invocation.descriptor, context.features) ? "feature_disabled"
    : policy.action === "deny" || effectivePolicyRevision(policy) !== approvedPolicyRevision ? "permission_denied" : undefined;
  if (!code) return undefined;
  return fail({ ...optionalRequestId(requestId), project: invocation.identity.project, command: TEST_RUN_COMMAND, error: {
    code, message: "test.run permission changed during asynchronous preparation",
    details: { ...preDispatchNonDeliveryDetails(), operation_id: operationId, bridge_request_issued: false,
      test_run_bridge_request_issued: false, test_runner_execute_invoked: false, test_run_replayed: false, write_replay_allowed: false,
      reason: code === "feature_disabled" ? "test_feature_changed_before_issuance" : code === "policy_degraded" ? "policy_degraded_before_test_issuance" : "test_policy_changed_before_issuance" },
  }, next_valid_actions: ["inspect_current_permission", "do_not_replay_test.run_automatically"] });
}

function testRunBindingPublicationFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, operationId: string, storeError: TestRunOperationStoreError): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: "internal_error",
      message: "test.run was not dispatched because its durable owner binding could not be published",
      details: {
        ...preDispatchNonDeliveryDetails(),
        reason: "operation_binding_publication_failed",
        storage_reason: storeError.reason,
        ...(storeError.publication ? { test_run_publication_abort: storeError.publication } : {}),
        ...(storeError.diagnostics ? { test_run_storage: storeError.diagnostics } : {}),
        operation_id: operationId,
        bridge_request_issued: false,
        test_run_bridge_request_issued: false,
        test_runner_execute_invoked: false,
        test_run_replayed: false,
        write_command_repeated: false,
      },
    },
    next_valid_actions: ["restore_the_private_test_run_operation_store", "repeat_test.run_only_if_still_intended", "do_not_replay_test.run_automatically"],
  });
}

function testRunIssuancePublicationFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, binding: TestRunOperationBinding, operationId: string, storeError: TestRunOperationStoreError, bridgeResult?: GatewayResult): GatewayResult {
  const pending = storeError instanceof TestRunPublicationDeadlineError;
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: "test_run_status_unavailable",
      message: "test.run crossed the Bridge request-issuance boundary but its durable issuance commit could not be verified within the caller budget",
      details: {
        ...testRunRecoveryEvidence(operationId, invocation.identity.projectSelector, "uncertain", true),
        ...testRunRequestIssuanceEvidence(binding, { status: "unavailable", evidence_source: "durable_request_issuance_publication_outcome_unknown" }),
        reason: pending ? "request_issuance_marker_publication_pending" : "request_issuance_marker_publication_failed",
        ...(pending ? { publication_observation: "acknowledgement_pending_at_caller_deadline" } : { storage_reason: storeError.reason }),
        ...(storeError.diagnostics ? { test_run_storage: storeError.diagnostics } : {}),
        durable_issuance_state: "unknown",
        test_run_delivery_state: "uncertain",
        bridge_request_issued: true,
        command_bridge_request_issued: true,
        test_run_bridge_request_issued: true,
        test_runner_execute_invoked: "unknown",
        ...(bridgeResult ? { observed_bridge_response: testRunObservedBridgeResponse(binding, bridgeResult) } : {}),
      },
    },
    next_valid_actions: testRunRecoveryNextValidActions(false),
  });
}

function testRunBindingDispatchFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, operationId: string, reason: "binding_publication_crossed_deadline" | "owner_identity_changed_after_binding_publication", bindingPublished = false): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: reason === "binding_publication_crossed_deadline" ? "command_timeout" : "unsafe_binding_refused",
      message: "test.run was not dispatched because its owner identity or deadline changed during durable binding publication",
      details: {
        ...preDispatchNonDeliveryDetails(),
        reason,
        operation_id: operationId,
        operation_binding_published: bindingPublished,
        bridge_request_issued: false,
        test_run_bridge_request_issued: false,
        test_runner_execute_invoked: false,
        test_run_replayed: false,
        write_command_repeated: false,
      },
    },
    next_valid_actions: ["inspect_current_project_identity", "repeat_test.run_only_if_still_intended", "do_not_replay_test.run_automatically"],
  });
}

function createBuildRunOperationId(context: DispatcherContext): string {
  const now = context.now?.() ?? new Date();
  const timestamp = now.toISOString().replace(/[-:]/g, "");
  return `build-${timestamp.slice(0, 8)}-${timestamp.slice(9, 15)}-${randomUUID().replaceAll("-", "").slice(0, 8)}`;
}

function createEditorMenuOperationId(context: DispatcherContext): string {
  const now = context.now?.() ?? new Date();
  const timestamp = now.toISOString().replace(/[-:]/g, "");
  return `menu-${timestamp.slice(0, 8)}-${timestamp.slice(9, 15)}-${randomUUID().replaceAll("-", "").slice(0, 8)}`;
}

function createEditorQuitOperationId(context: DispatcherContext): string {
  const now = context.now?.() ?? new Date();
  const timestamp = now.toISOString().replace(/[-:]/g, "");
  return `quit-${timestamp.slice(0, 8)}-${timestamp.slice(9, 15)}-${randomUUID().replaceAll("-", "").slice(0, 8)}`;
}

type EditorQuitObservation = {
  heartbeat: PublicProjectRecord | undefined;
  heartbeatState: string;
  heartbeatBindingMatches: boolean;
  processScanSupported: boolean;
  processScanTruncated: boolean;
  exactProcessGenerationPresent: boolean | null;
  pidReused: boolean;
  processGenerationUnknown: boolean;
  processErrors: string[];
  windowScanSupported: boolean;
  windowAggregateScanAuthoritative: boolean;
  nativeModalScanAuthoritative: boolean;
  windowErrors: string[];
  topLevelWindowPresent: boolean | null;
  windowResponding: boolean | null;
  nativeModalPresent: boolean;
};

async function reconcileEditorQuit(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  commandArgs: Record<string, unknown>,
  initialResult: GatewayResult,
  operationId: string,
  callerArgsHash: string,
  requestIssued: boolean,
): Promise<GatewayResult> {
  const initialRecord = invocation.record;
  if (!initialRecord || !requestIssued) return initialResult;
  if (verifiedEditorQuitPreflightRefusal(invocation, initialResult, operationId, callerArgsHash)) {
    return { ...initialResult, error: { ...initialResult.error, details: { ...initialResult.error.details,
      editor_quit_preflight_refusal_verified: true, bridge_request_issued: true,
      close_request_dispatched_once: false, automatic_second_quit_attempted: false,
      automatic_process_termination_attempted: false,
    } }, next_valid_actions: ["consult_owning_package_or_operator_before_lifecycle_changes", "do_not_exit_playmode_automatically", "do_not_replay_editor.quit_automatically"] };
  }
  const waitMs = integerArg(commandArgs.wait_timeout_ms, EDITOR_QUIT_DEFAULT_WAIT_MS, 1_000, 60_000);
  const startedAtMs = nowMs(context);
  const deadlineMs = startedAtMs + waitMs;
  let nextOsScanMs = startedAtMs;
  let observation = observeEditorQuit(context, initialRecord, operationId, callerArgsHash, true, deadlineMs);

  while (true) {
    const terminal = editorQuitTerminalResult(context, invocation, requestId, initialResult, initialRecord, operationId, callerArgsHash, requestIssued, startedAtMs, waitMs, observation, false);
    if (terminal) return terminal;
    const remainingMs = deadlineMs - nowMs(context);
    if (remainingMs <= 0) break;
    await sleepMs(context, Math.min(EDITOR_QUIT_POLL_INTERVAL_MS, remainingMs));
    const now = nowMs(context);
    const stateNeedsOsEvidence = observation.heartbeatState === "accepted" || observation.heartbeatState === "cancelled" || observation.heartbeatState === "ignored";
    const scanOs = stateNeedsOsEvidence || now >= nextOsScanMs;
    if (scanOs) nextOsScanMs = now + EDITOR_QUIT_OS_POLL_INTERVAL_MS;
    const scanOsWithinBudget = scanOs && deadlineMs - now >= EDITOR_QUIT_MIN_HELPER_BUDGET_MS * 2 + EDITOR_QUIT_WINDOW_CLEANUP_GRACE_MS;
    const latestObservation = observeEditorQuit(context, initialRecord, operationId, callerArgsHash, scanOsWithinBudget, deadlineMs);
    observation = scanOsWithinBudget ? latestObservation : mergeEditorQuitHeartbeatObservation(observation, latestObservation);
  }

  observation = mergeEditorQuitHeartbeatObservation(observation, observeEditorQuit(context, initialRecord, operationId, callerArgsHash, false, deadlineMs));
  return editorQuitTerminalResult(context, invocation, requestId, initialResult, initialRecord, operationId, callerArgsHash, requestIssued, startedAtMs, waitMs, observation, true)
    ?? editorQuitFailure(invocation, requestId, "quit_uncertain", "Unity close lifecycle remained uncertain at the bounded deadline", quitEvidence(context, initialResult, initialRecord, operationId, callerArgsHash, requestIssued, startedAtMs, waitMs, observation, "deadline_uncertain"));
}

function verifiedEditorQuitPreflightRefusal(invocation: InvocationContextSnapshot, result: GatewayResult, operationId: string, callerArgsHash: string): result is Extract<GatewayResult, { status: "error" }> {
  if (result.status !== "error" || !trustedEditorQuitBridgeRefusals.has(result) || !isTrustedBuiltInDescriptor(invocation.descriptor)) return false;
  const record = invocation.record;
  const details = result.error.details;
  const witness = details?.editor_quit_preflight_refusal;
  const falseFacts = ["close_requested", "quit_lifecycle_started", "exit_scheduled", "editor_exit_invoked",
    "automatic_replay_attempted", "automatic_process_termination_attempted", "retained_native_custody_observed", "retained_native_close_supported"];
  const keys = ["schema_version", "contract", "command", "operation_id", "caller_args_hash", "reason", "phase", "scope", "binding_complete", "owner", ...falseFacts];
  if (!record || details?.close_requested !== false || !isJsonObject(witness) || Object.keys(witness).length !== keys.length
    || Object.keys(witness).some(key => !keys.includes(key)) || witness.schema_version !== 1
    || witness.contract !== "sgg.unity_mcp.editor_quit_preflight_refusal.v1" || witness.command !== EDITOR_QUIT_COMMAND
    || witness.phase !== "preflight" || witness.scope !== "this_invocation" || witness.binding_complete !== true
    || witness.operation_id !== operationId || witness.caller_args_hash !== callerArgsHash || witness.reason !== details.reason
    || falseFacts.some(key => witness[key] !== false)) return false;
  const permittedCode = witness.reason === "confirm_close_required" ? "confirmation_required"
    : typeof witness.reason === "string" && ["compiling", "updating", "playmode", "unsaved_scene_changes"].includes(witness.reason) ? "unity_busy" : undefined;
  if (!permittedCode || result.error.code !== permittedCode) return false;
  const owner = witness.owner;
  const ownerKeys = ["project", "instance_id", "canonical_project_root", "unity_pid", "unity_process_start_utc"];
  return isJsonObject(owner) && Object.keys(owner).length === ownerKeys.length && Object.keys(owner).every(key => ownerKeys.includes(key))
    && owner.project === record.project && owner.instance_id === record.instance_id && owner.canonical_project_root === record.canonical_project_root
    && owner.unity_pid === record.unity_pid && typeof owner.unity_process_start_utc === "string" && typeof record.unity_process_start_utc === "string"
    && sameProcessStart(owner.unity_process_start_utc, record.unity_process_start_utc);
}

function mergeEditorQuitHeartbeatObservation(previous: EditorQuitObservation, latest: EditorQuitObservation): EditorQuitObservation {
  return {
    ...previous,
    heartbeat: latest.heartbeat,
    heartbeatState: latest.heartbeatState,
    heartbeatBindingMatches: latest.heartbeatBindingMatches,
  };
}

function editorQuitTerminalResult(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  initialResult: GatewayResult,
  initialRecord: HeartbeatRecord,
  operationId: string,
  callerArgsHash: string,
  requestIssued: boolean,
  startedAtMs: number,
  waitMs: number,
  observation: EditorQuitObservation,
  deadlineReached: boolean,
): GatewayResult | undefined {
  const processStartBound = typeof initialRecord.unity_process_start_utc === "string" && initialRecord.unity_process_start_utc.length > 0;
  const processExitProven = processStartBound && observation.processScanSupported && !observation.processScanTruncated && !observation.pidReused && !observation.processGenerationUnknown && observation.exactProcessGenerationPresent === false;
  const heartbeatAccepted = observation.heartbeat !== undefined
    && observation.heartbeatBindingMatches
    && observation.heartbeat.quit_operation_id === operationId
    && observation.heartbeatState === "accepted";
  if (!observation.heartbeatBindingMatches && observation.heartbeatState !== "missing" && observation.heartbeatState !== "unobserved") {
    return editorQuitFailure(invocation, requestId, "quit_uncertain", "Unity quit lifecycle evidence did not match the reserved operation", quitEvidence(context, initialResult, initialRecord, operationId, callerArgsHash, requestIssued, startedAtMs, waitMs, observation, "operation_binding_mismatch"));
  }

  // Exact PID/start-time absence from a complete OS scan is authoritative; window enumeration remains diagnostic.
  if (processExitProven && heartbeatAccepted) {
    const evidence = quitEvidence(context, initialResult, initialRecord, operationId, callerArgsHash, requestIssued, startedAtMs, waitMs, observation, "closed_verified");
    return ok({
      ...optionalRequestId(requestId),
      project: invocation.identity.project,
      command: EDITOR_QUIT_COMMAND,
      data: {
        ...(initialResult.status === "ok" && isObjectRecord(initialResult.data) ? initialResult.data : {}),
        close_requested: true,
        close_observed: true,
        lifecycle: "closed_verified",
        quit_reconciliation: evidence,
      },
    });
  }

  if ((observation.heartbeatState === "cancelled" || observation.heartbeatState === "ignored") && observation.exactProcessGenerationPresent === true) {
    return editorQuitFailure(invocation, requestId, "quit_not_observed", "Unity remained open after the exactly correlated quit request returned", quitEvidence(context, initialResult, initialRecord, operationId, callerArgsHash, requestIssued, startedAtMs, waitMs, observation, observation.heartbeatState));
  }

  if (!deadlineReached) return undefined;
  const reason = observation.nativeModalPresent
    ? "modal_or_close_prompt_deferred"
    : observation.heartbeatState === "accepted"
      ? "accepted_but_process_exit_not_observed"
      : observation.heartbeatState === "deferred" || observation.heartbeatState === "requesting"
        ? "close_request_deferred"
        : observation.heartbeatState === "uncertain"
          ? "unity_lifecycle_uncertain"
          : "insufficient_terminal_evidence";
  return editorQuitFailure(invocation, requestId, "quit_uncertain", "Unity close lifecycle was not conclusively observed", quitEvidence(context, initialResult, initialRecord, operationId, callerArgsHash, requestIssued, startedAtMs, waitMs, observation, reason));
}

function observeEditorQuit(context: DispatcherRuntimeContext, initialRecord: HeartbeatRecord, operationId: string, callerArgsHash: string, scanOs: boolean, osDeadlineMs?: number): EditorQuitObservation {
  const projects = context.registry?.listProjects() ?? [];
  const heartbeat = projects.find((record) => record.instance_id === initialRecord.instance_id);
  const heartbeatOperationId = heartbeat?.quit_operation_id ?? "";
  const heartbeatState = heartbeat === undefined ? "missing" : heartbeatOperationId.length === 0 ? "unobserved" : heartbeat.quit_request_state ?? "none";
  const heartbeatBindingMatches = heartbeat === undefined
    || heartbeatOperationId.length === 0
    || (heartbeatOperationId === operationId && heartbeat.quit_caller_args_hash === callerArgsHash);
  let processScanSupported = false;
  let processScanTruncated = false;
  let exactProcessGenerationPresent: boolean | null = null;
  let pidReused = false;
  let processGenerationUnknown = false;
  let processErrors: string[] = [];
  let windowScanSupported = false;
  let windowAggregateScanAuthoritative = false;
  let nativeModalScanAuthoritative = false;
  let windowErrors: string[] = [];
  let topLevelWindowPresent: boolean | null = null;
  let windowResponding: boolean | null = null;
  let nativeModalPresent = false;

  if (scanOs) {
    const scanDeadlineMs = Math.min(osDeadlineMs ?? Number.POSITIVE_INFINITY, nowMs(context) + EDITOR_QUIT_OS_SCAN_BUDGET_MS);
    try {
      const currentProject = publicRecordFromHeartbeat(initialRecord, false);
      const remainingBeforeProcess = Math.max(0, scanDeadlineMs - nowMs(context));
      const processBudgetMs = Math.floor(remainingBeforeProcess / 2);
      const processStatus = processBudgetMs >= EDITOR_QUIT_MIN_HELPER_BUDGET_MS
        ? context.unityProcessDiagnostics?.status(projects, currentProject, { scan_budget_ms: processBudgetMs })
        : undefined;
      if (processStatus) {
        processScanSupported = processStatus.supported && processStatus.errors.length === 0;
        processScanTruncated = processStatus.scan_truncated;
        processErrors = processStatus.errors.slice(0, 8);
        const samePid = processStatus.processes.filter((process) => process.pid === initialRecord.unity_pid);
        const initialStart = initialRecord.unity_process_start_utc ?? "";
        exactProcessGenerationPresent = processScanSupported && initialStart.length > 0
          ? samePid.some((process) => sameProcessStart(process.process_start_utc, initialStart))
          : null;
        pidReused = processScanSupported && initialStart.length > 0 && samePid.some((process) => process.process_start_utc !== null && !sameProcessStart(process.process_start_utc, initialStart));
        processGenerationUnknown = processScanSupported && initialStart.length > 0 && samePid.some((process) => process.process_start_utc === null);
      }
    } catch {
      processErrors = ["process_scan_failed"];
    }

    const remainingBeforeWindow = Math.max(0, scanDeadlineMs - nowMs(context));
    const windowStatus = remainingBeforeWindow >= EDITOR_QUIT_MIN_HELPER_BUDGET_MS + EDITOR_QUIT_WINDOW_CLEANUP_GRACE_MS
      ? safeUnityWindowStatus(context, remainingBeforeWindow)
      : undefined;
    if (windowStatus) {
      windowErrors = windowStatus.errors.slice(0, 8);
      windowScanSupported = windowStatus.top_level_window_scan_authoritative;
      windowAggregateScanAuthoritative = windowStatus.aggregate_scan_authoritative;
      nativeModalScanAuthoritative = windowStatus.native_modal_scan_authoritative;
      const activeWindow = windowStatus.active_unity_windows.find((window) => window.pid === initialRecord.unity_pid);
      topLevelWindowPresent = activeWindow !== undefined;
      windowResponding = activeWindow?.responding ?? null;
      nativeModalPresent = nativeModalScanAuthoritative && windowStatus.native_modal_candidates.some((modal) => modal.pid === initialRecord.unity_pid
        && modal.block_code !== undefined
        && modal.process_start_utc !== undefined
        && sameProcessStart(modal.process_start_utc, initialRecord.unity_process_start_utc ?? ""));
    }
  }

  return {
    heartbeat,
    heartbeatState,
    heartbeatBindingMatches,
    processScanSupported,
    processScanTruncated,
    exactProcessGenerationPresent,
    pidReused,
    processGenerationUnknown,
    processErrors,
    windowScanSupported,
    windowAggregateScanAuthoritative,
    nativeModalScanAuthoritative,
    windowErrors,
    topLevelWindowPresent,
    windowResponding,
    nativeModalPresent,
  };
}

function sameProcessStart(left: string | null, right: string): boolean {
  return sameUnityProcessStart(left, right);
}

function createDynamicCSharpReadonlyOperationId(context: DispatcherContext): string {
  const now = context.now?.() ?? new Date();
  const timestamp = now.toISOString().replace(/[-:]/g, "");
  return `csharp-${timestamp.slice(0, 8)}-${timestamp.slice(9, 15)}-${randomUUID().replaceAll("-", "").slice(0, 8)}`;
}

function dynamicCSharpSnippetHash(commandArgs: Record<string, unknown>): string {
  const code = typeof commandArgs.code === "string" ? commandArgs.code : "";
  return `sha256:${createHash("sha256").update(code, "utf8").digest("hex")}`;
}

function completeCorrelatedDynamicCSharpReadonly(
  result: GatewayResult,
  operationId: string,
  callerArgsHash: string,
  snippetHash: string,
  projectSelector: string,
  requestIssued: boolean,
  queueWaitMs: number,
): GatewayResult {
  const delivered = result.status === "ok" || (result.status === "error" && !["bridge_timeout", "command_timeout", "bridge_unreachable", "bridge_protocol_error", "unity_reloading"].includes(result.error.code));
  const deliveryState: "not_dispatched" | "uncertain" | "delivered" = !requestIssued ? "not_dispatched" : delivered ? "delivered" : "uncertain";
  const evidence = dynamicCSharpReadonlyRecoveryEvidence(operationId, callerArgsHash, snippetHash, projectSelector, deliveryState, true, requestIssued, queueWaitMs);
  if (result.status === "error") {
    return {
      ...result,
      error: { ...result.error, details: { ...(result.error.details ?? {}), ...evidence } },
      next_valid_actions: dynamicCSharpReadonlyRecoveryActions(deliveryState),
    };
  }

  const data = isObjectRecord(result.data) ? result.data : undefined;
  const expectedArtifactPath = `${DYNAMIC_CSHARP_READONLY_ARTIFACT_ROOT}/${operationId}.json`;
  if (data?.operation_id === operationId
    && data.artifact_path === expectedArtifactPath
    && data.caller_args_hash === callerArgsHash
    && data.snippet_hash === snippetHash
    && data.terminal === true
    && data.result_available === true
    && isObjectRecord(data.phase_evidence)) {
    return result;
  }

  return fail({
    ...optionalRequestId(result.request_id),
    ...(result.project ? { project: result.project } : {}),
    command: DYNAMIC_CSHARP_READONLY_COMMAND,
    error: {
      code: "bridge_protocol_error",
      message: "editor.execute_csharp.readonly response did not preserve the Gateway-reserved operation and result identity",
      details: {
        ...evidence,
        returned_operation_id: typeof data?.operation_id === "string" ? data.operation_id : null,
        returned_artifact_path: typeof data?.artifact_path === "string" ? data.artifact_path : null,
        caller_args_hash_match: data?.caller_args_hash === callerArgsHash,
        snippet_hash_match: data?.snippet_hash === snippetHash,
        terminal_result_available: data?.terminal === true && data.result_available === true,
      },
    },
    diagnostics: result.diagnostics,
    next_valid_actions: dynamicCSharpReadonlyRecoveryActions("delivered"),
  });
}

function settleDynamicCSharpReadonlyQueueBeforeDeadline(
  context: DispatcherContext,
  queued: Promise<GatewayResult>,
  deadlineUnixMs: number,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  timeoutMs: number,
  operationId: string,
  callerArgsHash: string,
  snippetHash: string,
  statusProjectSelector: string,
  delivery: { queueAdmitted: boolean; queueExpired: boolean; bridgeRequestIssued: boolean; queueWaitMs: number },
): Promise<GatewayResult> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const settle = (result: GatewayResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    const timer = setTimeout(() => {
      delivery.queueExpired = true;
      settle(delivery.queueAdmitted
        ? dynamicCSharpReadonlyDispatchTimeout(invocation, requestId, timeoutMs, operationId, callerArgsHash, snippetHash, statusProjectSelector, delivery.bridgeRequestIssued, delivery.queueWaitMs)
        : dynamicCSharpReadonlyQueueTimeout(invocation, requestId, timeoutMs, operationId, statusProjectSelector, Math.max(0, timeoutMs)));
    }, Math.max(0, deadlineUnixMs - nowMs(context)));
    queued.then(settle, (error: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    });
  });
}

function dynamicCSharpReadonlyQueueTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string, projectSelector: string, queueWaitMs: number): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: DYNAMIC_CSHARP_READONLY_COMMAND,
    error: {
      code: "queue_timeout",
      message: "Timed out waiting for project queue admission before dynamic C# read-only dispatch",
      details: {
        ...dynamicCSharpReadonlyRecoveryEvidence(operationId, "", "", projectSelector, "not_dispatched", false, false, queueWaitMs),
        timeout_ms: timeoutMs,
        operation_created: false,
        late_execution_possible: false,
      },
    },
    next_valid_actions: dynamicCSharpReadonlyRecoveryActions("not_dispatched"),
  });
}

function dynamicCSharpReadonlyDispatchTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string, callerArgsHash: string, snippetHash: string, projectSelector: string, requestIssued: boolean, queueWaitMs: number): GatewayResult {
  const deliveryState = requestIssued ? "uncertain" : "not_dispatched";
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: DYNAMIC_CSHARP_READONLY_COMMAND,
    error: {
      code: "bridge_timeout",
      message: "Dynamic C# read-only execution did not return before its bounded deadline",
      details: {
        ...dynamicCSharpReadonlyRecoveryEvidence(operationId, callerArgsHash, snippetHash, projectSelector, deliveryState, true, requestIssued, queueWaitMs),
        timeout_ms: timeoutMs,
        late_execution_possible: requestIssued,
      },
    },
    next_valid_actions: dynamicCSharpReadonlyRecoveryActions(deliveryState),
  });
}

function dynamicCSharpReadonlyRecoveryEvidence(
  operationId: string,
  callerArgsHash: string,
  snippetHash: string,
  project: string,
  deliveryState: "not_dispatched" | "uncertain" | "delivered",
  queueAdmitted: boolean,
  requestIssued: boolean,
  queueWaitMs: number,
): Record<string, unknown> {
  const failurePhase = requestIssued ? "bridge_response_delivery" : queueAdmitted ? "bridge_request_admission" : "project_queue_admission";
  return {
    dynamic_csharp_readonly_recovery_contract: DYNAMIC_CSHARP_READONLY_RECOVERY_CONTRACT,
    operation_id: operationId,
    operation_id_source: "gateway_reserved",
    ...(callerArgsHash ? { caller_args_hash: callerArgsHash } : {}),
    ...(snippetHash ? { snippet_hash: snippetHash } : {}),
    delivery_state: deliveryState,
    failure_phase: failurePhase,
    project_queue_admission: { state: queueAdmitted ? "completed" : "timed_out", duration_ms: Math.max(0, queueWaitMs) },
    bridge_request_issued: requestIssued,
    automatic_replay_attempted: false,
    command_replayed: false,
    unsafe_execute_recovery_enabled: false,
    expected_artifact_path: `${DYNAMIC_CSHARP_READONLY_ARTIFACT_ROOT}/${operationId}.json`,
    status_lookup: deliveryState === "not_dispatched" ? null : {
      tool: "sgg_unity_call",
      project,
      command: DYNAMIC_CSHARP_READONLY_STATUS_COMMAND,
      args_json: JSON.stringify({ operation_id: operationId }),
      mode: "wait",
    },
  };
}

function dynamicCSharpReadonlyRecoveryActions(deliveryState: "not_dispatched" | "uncertain" | "delivered"): string[] {
  if (deliveryState === "not_dispatched") return ["repeat_editor.execute_csharp.readonly_only_if_still_intended"];
  if (deliveryState === "delivered") return ["call_exact_editor.execute_csharp.readonly.status_if_result_recovery_is_needed", "do_not_rerun_the_snippet_for_recovery"];
  return ["poll_exact_editor.execute_csharp.readonly.status", "do_not_repeat_editor.execute_csharp.readonly_until_delivery_state_is_resolved", "do_not_use_status_to_replay_the_snippet"];
}

function quitEvidence(
  context: DispatcherRuntimeContext,
  initialResult: GatewayResult,
  initialRecord: HeartbeatRecord,
  operationId: string,
  callerArgsHash: string,
  requestIssued: boolean,
  startedAtMs: number,
  waitMs: number,
  observation: EditorQuitObservation,
  terminalReason: string,
): Record<string, unknown> {
  const heartbeat = observation.heartbeat;
  return {
    contract: EDITOR_QUIT_RECONCILIATION_CONTRACT,
    quit_operation_id: operationId,
    caller_args_hash: callerArgsHash,
    initial_instance_id: initialRecord.instance_id,
    initial_unity_pid: initialRecord.unity_pid,
    initial_unity_process_start_utc: initialRecord.unity_process_start_utc || null,
    bridge_request_issued: requestIssued,
    close_request_dispatched_once: requestIssued,
    automatic_second_quit_attempted: false,
    automatic_process_termination_attempted: false,
    elapsed_ms: Math.max(0, nowMs(context) - startedAtMs),
    wait_timeout_ms: waitMs,
    terminal_reason: terminalReason,
    initial_bridge_result: initialResult.status === "ok"
      ? { status: "ok", close_requested: isObjectRecord(initialResult.data) && initialResult.data.close_requested === true }
      : { status: "error", code: initialResult.error.code, bridge_request_issued: initialResult.error.details?.bridge_request_issued === true || initialResult.error.details?.command_bridge_request_issued === true },
    heartbeat: heartbeat ? {
      instance_id: heartbeat.instance_id,
      status: heartbeat.status,
      stale: heartbeat.stale,
      heartbeat_generation: heartbeat.heartbeat_generation ?? null,
      last_heartbeat: heartbeat.last_heartbeat,
      quit_operation_id: heartbeat.quit_operation_id ?? "",
      quit_request_state: heartbeat.quit_request_state ?? "none",
      quit_requested_utc: heartbeat.quit_requested_utc ?? "",
      quit_state_updated_utc: heartbeat.quit_state_updated_utc ?? "",
      binding_matches: observation.heartbeatBindingMatches,
    } : { status: "missing", binding_matches: true },
    process: {
      scan_supported: observation.processScanSupported,
      scan_truncated: observation.processScanTruncated,
      exact_generation_present: observation.exactProcessGenerationPresent,
      pid_reused: observation.pidReused,
      process_generation_unknown: observation.processGenerationUnknown,
      errors: observation.processErrors,
      process_exit_proven: typeof initialRecord.unity_process_start_utc === "string"
        && initialRecord.unity_process_start_utc.length > 0
        && observation.processScanSupported
        && !observation.processScanTruncated
        && !observation.pidReused
        && !observation.processGenerationUnknown
        && observation.exactProcessGenerationPresent === false,
    },
    window: {
      scan_supported: observation.windowScanSupported,
      top_level_window_scan_authoritative: observation.windowScanSupported,
      native_modal_scan_authoritative: observation.nativeModalScanAuthoritative,
      aggregate_scan_authoritative: observation.windowAggregateScanAuthoritative,
      top_level_window_present: observation.topLevelWindowPresent,
      responding: observation.windowResponding,
      native_modal_present: observation.nativeModalPresent,
      errors: observation.windowErrors,
      process_generation_bound: observation.processScanSupported && !observation.processScanTruncated && !observation.pidReused && !observation.processGenerationUnknown,
    },
    next_valid_actions: ["do_not_repeat_editor.quit", "do_not_kill_unity_automatically", "call_sgg_unity_projects", "call_diagnostics.unity_modal.status", "diagnostics.report_problem"],
  };
}

function editorQuitFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, code: Extract<ErrorCode, "quit_not_observed" | "quit_uncertain">, message: string, details: Record<string, unknown>): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: EDITOR_QUIT_COMMAND,
    error: { code, message, details },
    next_valid_actions: ["do_not_repeat_editor.quit", "do_not_kill_unity_automatically", "call_sgg_unity_projects", "call_diagnostics.unity_modal.status", "diagnostics.report_problem"],
  });
}

function completeCorrelatedEditorMenuOperation(result: GatewayResult, operationId: string, projectSelector: string, requestIssued: boolean): GatewayResult {
  if (result.status === "error") {
    if (result.error.details?.operation_created === false && result.error.details.menu_execute_invoked === false) return result;
    const deliveryState = requestIssued ? "uncertain" : "not_dispatched";
    return {
      ...result,
      error: {
        ...result.error,
        details: {
          ...(result.error.details ?? {}),
          ...editorMenuOperationRecoveryEvidence(operationId, projectSelector, deliveryState, requestIssued),
        },
      },
      next_valid_actions: editorMenuOperationRecoveryActions(deliveryState),
    };
  }
  const data = isObjectRecord(result.data) ? result.data : undefined;
  const expectedArtifactPath = `Library/SGGUnityMcp/job-artifacts/menu-operations/${operationId}.json`;
  if (data?.operation_id === operationId
    && data.artifact_path === expectedArtifactPath
    && data.operation_created === true
    && data.artifact_persisted === true
    && data.status === "queued"
    && data.terminal === false
    && data.success === false
    && data.dispatch_count === 0
    && data.menu_execute_invoked === false
    && data.execute_menu_item_returned === null) return result;
  return fail({
    ...optionalRequestId(result.request_id),
    ...(result.project ? { project: result.project } : {}),
    command: EDITOR_MENU_OPERATION_START_COMMAND,
    error: {
      code: "bridge_protocol_error",
      message: "editor.menu.operation.start response did not preserve the Gateway-reserved operation identity",
      details: {
        ...editorMenuOperationRecoveryEvidence(operationId, projectSelector, "uncertain", true),
        returned_operation_id: typeof data?.operation_id === "string" ? data.operation_id : null,
        returned_artifact_path: typeof data?.artifact_path === "string" ? data.artifact_path : null,
      },
    },
    diagnostics: result.diagnostics,
    next_valid_actions: editorMenuOperationRecoveryActions("uncertain"),
  });
}

function settleEditorMenuOperationQueueBeforeDeadline(
  context: DispatcherContext,
  queued: Promise<GatewayResult>,
  deadlineUnixMs: number,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  timeoutMs: number,
  operationId: string,
  statusProjectSelector: string,
  delivery: { queueAdmitted: boolean; queueExpired: boolean; writeRequestIssued: boolean },
): Promise<GatewayResult> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const settle = (result: GatewayResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    const timer = setTimeout(() => {
      delivery.queueExpired = true;
      settle(delivery.queueAdmitted
        ? editorMenuOperationDispatchTimeout(invocation, requestId, timeoutMs, operationId, statusProjectSelector, delivery.writeRequestIssued)
        : editorMenuOperationQueueTimeout(invocation, requestId, timeoutMs, operationId, statusProjectSelector));
    }, Math.max(0, deadlineUnixMs - nowMs(context)));
    queued.then(settle, (error: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    });
  });
}

function editorMenuOperationQueueTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string, statusProjectSelector: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: EDITOR_MENU_OPERATION_START_COMMAND,
    error: {
      code: "queue_timeout",
      message: "Timed out waiting for project queue admission before editor menu operation dispatch",
      details: { ...editorMenuOperationRecoveryEvidence(operationId, statusProjectSelector, "not_dispatched", false), timeout_ms: timeoutMs },
    },
    next_valid_actions: editorMenuOperationRecoveryActions("not_dispatched"),
  });
}

function editorMenuOperationDispatchTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string, statusProjectSelector: string, requestIssued: boolean): GatewayResult {
  const deliveryState = requestIssued ? "uncertain" : "not_dispatched";
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: EDITOR_MENU_OPERATION_START_COMMAND,
    error: {
      code: "bridge_timeout",
      message: "Editor menu operation startup did not return before its bounded deadline",
      details: { ...editorMenuOperationRecoveryEvidence(operationId, statusProjectSelector, deliveryState, requestIssued), timeout_ms: timeoutMs },
    },
    next_valid_actions: editorMenuOperationRecoveryActions(deliveryState),
  });
}

function editorMenuOperationRecoveryEvidence(operationId: string, project: string, deliveryState: "not_dispatched" | "uncertain", requestIssued: boolean): Record<string, unknown> {
  return {
    editor_menu_operation_recovery_contract: EDITOR_MENU_OPERATION_TIMEOUT_RECOVERY_CONTRACT,
    operation_id: operationId,
    operation_id_source: "gateway_reserved",
    delivery_state: deliveryState,
    bridge_request_issued: requestIssued,
    command_replayed: false,
    write_command_repeated: false,
    auto_retry_attempted: false,
    status_or_artifact_check_required_before_repeat: deliveryState === "uncertain",
    expected_artifact_path: `Library/SGGUnityMcp/job-artifacts/menu-operations/${operationId}.json`,
    status_lookup: {
      tool: "sgg_unity_call",
      project,
      command: EDITOR_MENU_OPERATION_STATUS_COMMAND,
      args_json: JSON.stringify({ operation_id: operationId }),
      mode: "wait",
    },
  };
}

function editorMenuOperationRecoveryActions(deliveryState: "not_dispatched" | "uncertain"): string[] {
  return deliveryState === "not_dispatched"
    ? ["repeat_editor.menu.operation.start_only_if_still_intended"]
    : ["poll_exact_editor.menu.operation.status", "inspect_expected_menu_operation_artifact", "do_not_repeat_editor.menu.operation.start_until_delivery_state_is_resolved"];
}

async function prepareBuildRunStatusInvocation(context: DispatcherRuntimeContext, buildInvocation: InvocationContextSnapshot, operationId: string): Promise<{ status: "ok"; invocation: InvocationContextSnapshot } | { status: "error"; result: GatewayResult }> {
  const record = buildInvocation.record;
  if (!record || !context.bridge) {
    return { status: "error", result: bridgeUnavailable(buildInvocation.identity.project, BUILD_RUN_STATUS_COMMAND) };
  }
  try {
    const resolved = await resolveCommandDescriptor(context.bridge, record, BUILD_RUN_STATUS_COMMAND);
    const descriptor = resolved?.command;
    const statusArgs = { operation_id: operationId };
    if (!descriptor
      || descriptor.risk !== "read"
      || descriptor.idempotency !== "safe"
      || resolved.commandsHash !== buildInvocation.commandsHash
      || validateCommandArgs(descriptor.schema, statusArgs, isTrustedBuiltInDescriptor(descriptor)).status !== "ok") {
      return {
        status: "error",
        result: fail({
          project: buildInvocation.identity.project,
          command: BUILD_RUN_COMMAND,
          error: {
            code: "catalog_stale",
            message: "build.run requires a matching safe build.run.status descriptor before the build can start",
            details: { operation_id: operationId, build_run_started: false, write_command_repeated: false },
          },
        }),
      };
    }
    refreshManagedPolicy(context);
    const degraded = degradedPolicyGate(context, buildInvocation.identity.project, BUILD_RUN_COMMAND, undefined, buildInvocation.descriptor.risk);
    if (degraded) return { status: "error", result: degraded };
    if (!isFeatureEnabled(descriptor, context.features)) {
      return { status: "error", result: featureDisabled(buildInvocation.identity.project, BUILD_RUN_STATUS_COMMAND, descriptor.feature_flag) };
    }
    const statusPolicy = evaluatePolicy(context.policy, buildInvocation.identity.project, descriptor);
    if (statusPolicy.action !== "allow") {
      return {
        status: "error",
        result: fail({
          project: buildInvocation.identity.project,
          command: BUILD_RUN_COMMAND,
          error: {
            code: "permission_denied",
            message: "build.run job mode requires build.run.status to be policy-allowed before the build starts",
            details: { operation_id: operationId, status_policy_action: statusPolicy.action, build_run_started: false, write_command_repeated: false },
          },
        }),
      };
    }
    return {
      status: "ok",
      invocation: InvocationContextSnapshot.create({
        projectSelector: record.instance_id,
        project: buildInvocation.identity.project,
        command: BUILD_RUN_STATUS_COMMAND,
        descriptor,
        commandsHash: resolved.commandsHash,
        record,
      }),
    };
  } catch (error) {
    const bridgeError = bridgeThrownError(error, {
      project: buildInvocation.identity.project,
      projectSelector: record.instance_id,
      command: BUILD_RUN_STATUS_COMMAND,
      record,
      registry: context.registry,
    }, context);
    return { status: "error", result: bridgeError ?? fail({ project: buildInvocation.identity.project, command: BUILD_RUN_COMMAND, error: { code: "catalog_unavailable", message: "build.run.status could not be verified before starting the build" } }) };
  }
}

function startCorrelatedBuildRun(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  commandArgs: Record<string, unknown>,
  operationId: string,
  externalDeadlineUnixMs?: number,
): Promise<GatewayResult> {
  const timeoutMs = invocation.descriptor.timeout_ms;
  const deadlineUnixMs = earlierDeadline(commandDeadlineUnixMs(context, timeoutMs), externalDeadlineUnixMs);
  const delivery = { queueAdmitted: false, queueExpired: false, writeRequestIssued: false };
  const queued = runInProjectQueue(context, invocation.identity.project, async () => {
    if (delivery.queueExpired || deadlineUnixMs <= nowMs(context)) return buildRunQueueTimeout(invocation, requestId, timeoutMs, operationId);
    delivery.queueAdmitted = true;
    const result = await invokePreparedCommand(context, invocation, requestId, commandArgs, deadlineUnixMs, true, () => { delivery.writeRequestIssued = true; });
    return completeCorrelatedBuildRun(result, operationId);
  }, false, projectQueueOwner(invocation, deadlineUnixMs));
  return settleBuildRunQueueBeforeDeadline(context, queued, deadlineUnixMs, invocation, requestId, timeoutMs, operationId, delivery);
}

function settleBuildRunQueueBeforeDeadline(
  context: DispatcherContext,
  queued: Promise<GatewayResult>,
  deadlineUnixMs: number,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  timeoutMs: number,
  operationId: string,
  delivery: { queueAdmitted: boolean; queueExpired: boolean; writeRequestIssued: boolean },
): Promise<GatewayResult> {
  return new Promise((resolve, reject) => {
    let settled = false;
    const settle = (result: GatewayResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    const timer = setTimeout(() => {
      delivery.queueExpired = true;
      settle(delivery.queueAdmitted
        ? buildRunDispatchTimeout(invocation, requestId, timeoutMs, operationId, delivery.writeRequestIssued)
        : buildRunQueueTimeout(invocation, requestId, timeoutMs, operationId));
    }, Math.max(0, deadlineUnixMs - nowMs(context)));
    queued.then(settle, (error: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    });
  });
}

function buildRunQueueTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: BUILD_RUN_COMMAND,
    error: {
      code: "queue_timeout",
      message: "Timed out waiting for project queue admission before build.run dispatch",
      details: { operation_id: operationId, timeout_ms: timeoutMs, build_run_started: false, bridge_request_issued: false, build_run_replayed: false, write_command_repeated: false },
    },
    next_valid_actions: ["repeat_build.run_only_if_still_intended", "build.settings"],
  });
}

function buildRunDispatchTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string, requestIssued: boolean): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: BUILD_RUN_COMMAND,
    error: {
      code: "command_timeout",
      message: "build.run startup did not return before its bounded deadline",
      details: { operation_id: operationId, timeout_ms: timeoutMs, build_run_started: requestIssued ? "unknown" : false, bridge_request_issued: requestIssued, build_run_replayed: false, write_command_repeated: false },
    },
    next_valid_actions: requestIssued
      ? ["poll_exact_build.run.status", "inspect_expected_build_run_artifact", "do_not_repeat_build.run_until_delivery_state_is_resolved"]
      : ["repeat_build.run_only_if_still_intended", "build.settings"],
  });
}

function completeCorrelatedBuildRun(result: GatewayResult, operationId: string): GatewayResult {
  if (result.status === "error") {
    return {
      ...result,
      error: {
        ...result.error,
        details: {
          ...(result.error.details ?? {}),
          operation_id: operationId,
          build_run_replayed: false,
          write_command_repeated: false,
          status_request: { command: BUILD_RUN_STATUS_COMMAND, args_json: JSON.stringify({ operation_id: operationId }) },
        },
      },
      next_valid_actions: ["poll_exact_build.run.status", "inspect_expected_build_run_artifact", "do_not_repeat_build.run_until_delivery_state_is_resolved"],
    };
  }
  const data = isObjectRecord(result.data) ? result.data : undefined;
  if (data?.operation_id === operationId) return result;
  return fail({
    ...optionalRequestId(result.request_id),
    ...(result.project ? { project: result.project } : {}),
    command: BUILD_RUN_COMMAND,
    error: {
      code: "bridge_protocol_error",
      message: "build.run response did not preserve the Gateway-reserved operation id",
      details: {
        operation_id: operationId,
        returned_operation_id: typeof data?.operation_id === "string" ? data.operation_id : null,
        build_run_replayed: false,
        write_command_repeated: false,
      },
    },
    diagnostics: result.diagnostics,
    next_valid_actions: ["poll_exact_build.run.status", "inspect_expected_build_run_artifact", "do_not_repeat_build.run_until_delivery_state_is_resolved"],
  });
}

async function pollBuildRunUntilTerminal(context: DispatcherRuntimeContext, statusInvocation: InvocationContextSnapshot, operationId: string, deadlineMs: number, receiptGraceDeadlineMs = 0): Promise<GatewayResult> {
  const project = statusInvocation.identity.project;
  const statusContext = testRunPollContext(context, deadlineMs);
  let lastStatus: GatewayResult | undefined;
  while (nowMs(context) < deadlineMs) {
    lastStatus = await invokePreparedCommand(statusContext as DispatcherRuntimeContext, statusInvocation, undefined, { operation_id: operationId }, deadlineMs, true);
    if (lastStatus.status === "error" && String(lastStatus.error.code) === "build_run_not_found" && nowMs(context) < receiptGraceDeadlineMs) {
      await sleepMs(context, Math.min(BATCH_TEST_RUN_POLL_INTERVAL_MS, Math.max(0, receiptGraceDeadlineMs - nowMs(context))));
      continue;
    }
    if (!isRunningOperationResult(lastStatus)) return lastStatus;
    await sleepMs(context, Math.min(BATCH_TEST_RUN_POLL_INTERVAL_MS, Math.max(0, deadlineMs - nowMs(context))));
  }
  return fail({
    project,
    command: BUILD_RUN_COMMAND,
    error: {
      code: "command_timeout",
      message: "Timed out waiting for the durable build.run operation to reach terminal status",
      details: {
        operation_id: operationId,
        timeout_ms: BUILD_RUN_MAX_WAIT_MS,
        build_run_started: true,
        build_run_replayed: false,
        write_command_repeated: false,
        last_status: lastStatus?.status ?? "unavailable",
      },
    },
    next_valid_actions: ["poll_build.run.status", "inspect_the_exact_build_run_artifact", "do_not_repeat_build.run_until_delivery_state_is_resolved"],
  });
}

type TestRunDiscoveryPreflightEvidence = {
  schema_version: 1;
  contract: typeof TEST_RUN_DISCOVERY_PREFLIGHT_CONTRACT;
  status: "ready" | "pending" | "unavailable" | "incomplete" | "error";
  poll_count: number;
  read_request_count: number;
  elapsed_ms: number;
  metadata_elapsed_ms?: number;
  startup_deadline_ms: number;
  bridge_admission_reserve_ms: number;
  mode: string;
  assembly: string;
  discovery_generation: number | null;
  reason: string;
  test_run_bridge_request_issued: false;
  test_runner_execute_invoked: false;
  command_replayed: false;
  workflow_identity: Record<string, unknown>;
};

function withTestRunStartupTiming(result: GatewayResult, timing: {
  phase: "compile_readiness" | "discovery" | "binding_prepare"; phaseStartedAtMs: number; startupStartedAtMs: number;
  observedAtMs: number; deadlineUnixMs: number; descriptorTimeoutMs: number; bindingReserveMs: number;
}): GatewayResult {
  if (result.status !== "error") return result;
  const bounded = (value: number) => Number.isFinite(value) ? Math.floor(Math.max(0, Math.min(2_147_483_647, value))) : null;
  const annotated = { ...result, error: { ...result.error, details: { ...result.error.details,
    test_run_startup_timing: {
      schema_version: 1, contract: "sgg.unity_mcp.test_run_startup_timing.v1", phase: timing.phase,
      phase_elapsed_ms: bounded(timing.observedAtMs - timing.phaseStartedAtMs),
      startup_elapsed_ms: bounded(timing.observedAtMs - timing.startupStartedAtMs),
      effective_startup_budget_ms: bounded(timing.deadlineUnixMs - timing.startupStartedAtMs),
      remaining_startup_ms: bounded(timing.deadlineUnixMs - timing.observedAtMs),
      descriptor_timeout_ms: bounded(timing.descriptorTimeoutMs), binding_publication_reserve_ms: bounded(timing.bindingReserveMs),
      deadline_scope: "shared_startup_descriptor_and_inherited_caller_minimum", command_replayed: false,
    },
  } } };
  if (optedInTestRunPreflightFailures.has(result)) optedInTestRunPreflightFailures.add(annotated);
  return annotated;
}

/** A single safe Unity wait consumes the same startup deadline as discovery.
 * No refresh, recovery fallback, owner replacement or test request is issued here. */
async function awaitTestRunCompileReadinessBeforeDispatch(
  context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined,
  workflowId: string, deadlineUnixMs: number, expired: () => boolean, policyRevision: string,
): Promise<{ state: "ready" } | { state: "blocked"; result: GatewayResult }> {
  const record = invocation.record;
  const blocked = (code: ErrorCode, reason: string) => ({ state: "blocked" as const, result: testRunCompileReadinessFailure(invocation, requestId, workflowId, code, reason) });
  if (!record || !context.bridge) return blocked("bridge_unreachable", "readiness_bridge_unavailable");
  const guard = () => expired()
    ? testRunCompileReadinessFailure(invocation, requestId, workflowId, "bridge_timeout", "startup_deadline_reached")
    : testRunReadinessDispatchGuard(context, invocation, requestId, workflowId, deadlineUnixMs, policyRevision);
  const initialBlock = guard();
  if (initialBlock) return { state: "blocked", result: initialBlock };
  try {
    const resolved = await resolveCommandDescriptor(context.bridge, record, COMPILE_WAIT_READY_COMMAND, recoveryTransport(context, deadlineUnixMs));
    const descriptor = resolved?.command;
    const metadata = resolved?.metadata;
    if (!descriptor || descriptor.id !== COMPILE_WAIT_READY_COMMAND || !metadata
      || metadata.project !== record.project || metadata.instance_id !== record.instance_id
      || metadata.catalog_generation !== record.catalog_generation || metadata.commands_hash !== invocation.commandsHash
      || resolved.commandsHash !== invocation.commandsHash || !isTrustedBuiltInDescriptor(descriptor)
      || descriptor.package_id !== invocation.descriptor.package_id || descriptor.package_version !== invocation.descriptor.package_version
      || descriptor.risk !== "read" || descriptor.idempotency !== "safe" || !isValidCommandTimeout(descriptor.timeout_ms)) return blocked("catalog_stale", "readiness_descriptor_or_owner_mismatch");
    const afterMetadataBlock = guard();
    if (afterMetadataBlock) return { state: "blocked", result: afterMetadataBlock };
    const helperInvocation = InvocationContextSnapshot.create({
      projectSelector: record.instance_id, project: record.project, command: COMPILE_WAIT_READY_COMMAND,
      descriptor, commandsHash: resolved.commandsHash, record,
    });
    const frozenHelper = helperInvocation.descriptor;
    const helperPolicyGuard = () => {
      const ownerBlock = guard();
      if (ownerBlock) return ownerBlock;
      if (!isFeatureEnabled(frozenHelper, context.features) || evaluatePolicy(context.policy, invocation.identity.project, frozenHelper).action !== "allow") {
        return testRunCompileReadinessFailure(invocation, requestId, workflowId, "permission_denied", "readiness_helper_not_policy_allowed");
      }
      return undefined;
    };
    const helperBlock = helperPolicyGuard();
    if (helperBlock) return { state: "blocked", result: helperBlock };
    const remainingMs = Math.floor(deadlineUnixMs - nowMs(context));
    if (!Number.isSafeInteger(remainingMs) || remainingMs <= COMPILE_WAIT_READY_DELIVERY_RESERVE_MS) return blocked("bridge_timeout", "readiness_delivery_reserve_exhausted");
    const plan = compileWaitReadyTransportPlan({ timeout_ms: Math.min(300_000, remainingMs), poll_interval_ms: DEFAULT_COMPILE_WAIT_READY_POLL_INTERVAL_MS, response_mode: "compact" }, deadlineUnixMs, nowMs(context));
    if (!plan || !supportsTypedReadinessArguments(frozenHelper.schema, plan.args)
      || validateCommandArgs(frozenHelper.schema, plan.args, true).status !== "ok") return blocked("catalog_stale", "readiness_helper_schema_unsupported");
    const evidence = await invokePreparedCommand(context, helperInvocation, undefined, plan.args, deadlineUnixMs, true, undefined, Math.min(frozenHelper.timeout_ms, remainingMs), helperPolicyGuard, helperPolicyGuard);
    if (evidence.status === "error") return blocked(evidence.error.code, "readiness_read_failed");
    const afterReadBlock = guard();
    if (afterReadBlock) return { state: "blocked", result: afterReadBlock };
    const classification = classifyTestRunCompileReadiness(evidence.data);
    if (classification.state !== "ready") return blocked(classification.state === "invalid" ? "bridge_protocol_error" : "compile_freshness_unverified", classification.reason);
    const progress = isObjectRecord(evidence.data) && isObjectRecord(evidence.data.background_compiler_progress) ? evidence.data.background_compiler_progress : undefined;
    if (progress?.observed === true && (progress.process_id !== record.unity_pid || progress.catalog_generation !== record.catalog_generation
      || progress.compilation_generation !== record.compilation_generation || progress.completed_compilation_generation !== record.completed_compilation_generation)) return blocked("catalog_stale", "readiness_observation_generation_mismatch");
    return { state: "ready" };
  } catch (error) {
    const failure = bridgeThrownError(error, { project: record.project, projectSelector: record.instance_id, command: COMPILE_WAIT_READY_COMMAND, record, registry: context.registry }, context);
    return blocked(failure?.status === "error" ? failure.error.code : "bridge_protocol_error", "readiness_metadata_read_failed");
  }
}

function supportsTypedReadinessArguments(schema: Record<string, unknown>, args: Record<string, unknown>): boolean {
  if (!isObjectRecord(schema.properties)) return false;
  for (const [key, value] of Object.entries(args)) {
    if (!Object.hasOwn(schema.properties, key)) return false;
    const property = schema.properties[key];
    if (!isObjectRecord(property) || property.type !== (typeof value === "number" ? "integer" : "string")) return false;
    if (key === "response_mode" && (!Array.isArray(property.enum) || !property.enum.includes("compact"))) return false;
  }
  return true;
}

function testRunCompileReadinessFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, workflowId: string, code: ErrorCode, reason: string): GatewayResult {
  const failure = fail({
    ...optionalRequestId(requestId), project: invocation.identity.project, command: TEST_RUN_COMMAND,
    error: { code, message: "test.run was not dispatched because its opted-in compile-readiness preflight could not be verified", details: {
      ...preDispatchNonDeliveryDetails(), queue_admitted: true, test_run_workflow_id: workflowId,
      test_run_bridge_request_issued: false, test_runner_execute_invoked: false, test_run_replayed: false,
      write_command_repeated: false, write_replay_allowed: false,
      test_run_compile_readiness_preflight: { contract: TEST_RUN_COMPILE_READINESS_CONTRACT, reason, deadline_scope: "shared_startup_before_discovery", command_replayed: false },
    } },
    next_valid_actions: ["inspect_compile_and_test_discovery_evidence", "do_not_replay_test.run_automatically"],
  });
  optedInTestRunPreflightFailures.add(failure);
  return failure;
}

function testRunReadinessDispatchGuard(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined, workflowId: string, deadlineUnixMs: number, policyRevision: string): GatewayResult | undefined {
  const refuse = (code: ErrorCode, reason: string) => testRunCompileReadinessFailure(invocation, requestId, workflowId, code, reason);
  if (!Number.isFinite(nowMs(context)) || deadlineUnixMs <= nowMs(context)) return refuse("bridge_timeout", "startup_deadline_reached");
  refreshManagedPolicy(context);
  if (degradedPolicyGate(context, invocation.identity.project, TEST_RUN_COMMAND, requestId, invocation.descriptor.risk)) return refuse("policy_degraded", "policy_degraded_before_test_issuance");
  if (!isFeatureEnabled(invocation.descriptor, context.features)) return refuse("feature_disabled", "test_feature_changed_before_issuance");
  const policy = evaluatePolicy(context.policy, invocation.identity.project, invocation.descriptor);
  if (policy.action === "deny" || effectivePolicyRevision(policy) !== policyRevision) return refuse("permission_denied", "test_policy_changed_before_issuance");
  const frozen = invocation.record;
  const resolution = resolveProject(context, invocation.identity.projectSelector);
  const current = resolution.status === "ok" ? resolution.record : undefined;
  const keys: Array<keyof HeartbeatRecord> = ["project", "canonical_project_root", "instance_id", "unity_pid", "unity_process_start_utc", "package_version", "bridge_host", "bridge_port", "token", "commands_hash", "catalog_generation", "compilation_generation", "completed_compilation_generation"];
  if (!frozen || !current || !frozen.unity_process_start_utc || !Number.isSafeInteger(frozen.catalog_generation) || !Number.isSafeInteger(frozen.compilation_generation)
    || !Number.isSafeInteger(frozen.completed_compilation_generation) || frozen.commands_hash !== invocation.commandsHash
    || frozen.package_version !== invocation.descriptor.package_version || current.catalog_status !== "ready"
    || keys.some((key) => frozen[key] !== current[key])) return refuse("catalog_stale", "test_owner_changed_before_issuance");
  return undefined;
}

type TestRunDiscoveryPreflightResult =
  | { state: "ready"; discoveryGeneration?: number; evidence?: TestRunDiscoveryPreflightEvidence }
  | { state: "blocked"; result: GatewayResult };

async function awaitTestRunDiscoveryBeforeDispatch(
  context: DispatcherRuntimeContext,
  testRunInvocation: InvocationContextSnapshot,
  requestId: string | undefined,
  commandArgs: Record<string, unknown>,
  workflowId: string,
  deadlineUnixMs: number,
  expired: () => boolean,
  bridgeAdmissionReserveMs = 0,
  optedInGuard?: () => GatewayResult | undefined,
): Promise<TestRunDiscoveryPreflightResult> {
  const filter = typeof commandArgs.filter === "string" ? commandArgs.filter.trim() : "";
  const modeOnly = filter.length === 0;

  const mode = typeof commandArgs.mode === "string" && commandArgs.mode.trim().length > 0 ? commandArgs.mode.trim().toLowerCase() : "editmode";
  const assembly = typeof commandArgs.assembly === "string" ? commandArgs.assembly.trim() : "";
  const listArgs: Record<string, unknown> = { mode, cursor: 0, max_results: 1, response_mode: "compact", ...(assembly ? { assembly } : {}), ...(!modeOnly ? { query: filter.slice(0, 128) } : {}) };
  const initialBlock = optedInGuard?.();
  if (initialBlock) return { state: "blocked", result: initialBlock };
  const startedAtMs = nowMs(context);
  const startupDeadlineMs = Math.max(0, deadlineUnixMs - startedAtMs);
  const prepared = await prepareTestRunDiscoveryInvocation(context, testRunInvocation, requestId, listArgs, workflowId, deadlineUnixMs, optedInGuard !== undefined);
  const metadataElapsedMs = Math.max(0, nowMs(context) - startedAtMs);
  if (prepared.state === "unsupported") return { state: "ready" };
  if (prepared.state === "blocked") return prepared;

  let pollCount = 0;
  let readRequestCount = 0;
  let lastStatus: TestRunDiscoveryPreflightEvidence["status"] = "pending";
  let lastReason = "retrieve_test_list_pending";
  let lastGeneration: number | null = null;

  while (!expired() && nowMs(context) < deadlineUnixMs) {
    const readGuard = optedInGuard ? () => {
      const blocked = optedInGuard();
      if (blocked) return blocked;
      if (!isFeatureEnabled(prepared.invocation.descriptor, context.features) || evaluatePolicy(context.policy, testRunInvocation.identity.project, prepared.invocation.descriptor).action !== "allow") {
        return testRunCompileReadinessFailure(testRunInvocation, requestId, workflowId, "permission_denied", "discovery_helper_not_policy_allowed");
      }
      return undefined;
    } : undefined;
    pollCount += 1;
    const result = await invokePreparedCommand(context, prepared.invocation, undefined, listArgs, deadlineUnixMs, true, () => { readRequestCount += 1; }, undefined, readGuard, readGuard);
    if (result.status === "error") {
      if (optedInGuard) return { state: "blocked", result: testRunCompileReadinessFailure(testRunInvocation, requestId, workflowId, result.error.code, "discovery_read_failed") };
      const evidence = buildEvidence("error", typeof result.error.details?.scan_reason === "string" ? result.error.details.scan_reason : result.error.code, null);
      return { state: "blocked", result: testRunDiscoveryTransportFailure(testRunInvocation, requestId, result, workflowId, evidence) };
    }

    const data = isObjectRecord(result.data) ? result.data : {};
    const status = typeof data.status === "string" ? data.status : "";
    const reason = optedInGuard
      ? status === "ready" ? "" : status === "pending" ? "retrieve_test_list_pending" : "discovery_not_ready"
      : typeof data.scan_reason === "string" ? data.scan_reason : "";
    const generation = boundedEvidenceInteger(data.discovery_generation, 1, MAX_SAFE_COMPILE_EVIDENCE_INTEGER) ?? null;
    if (data.mode !== mode || data.assembly !== assembly) {
      const evidence = buildEvidence("error", "test_list_identity_mismatch", generation);
      return {
        state: "blocked",
        result: testRunDiscoveryFailure(testRunInvocation, requestId, "bridge_protocol_error", "test.run was not dispatched because test.list returned a mismatched mode or assembly identity", workflowId, evidence),
      };
    }
    lastStatus = status === "ready" || status === "pending" || status === "unavailable" || status === "incomplete" ? status : "error";
    lastReason = reason || (status === "pending" ? "retrieve_test_list_pending" : status || "invalid_test_list_status");
    lastGeneration = generation;

    if (status === "ready") {
      const evidence = buildEvidence("ready", lastReason === "ready" ? "" : lastReason, generation);
      if (generation === null) {
        return {
          state: "blocked",
          result: testRunDiscoveryFailure(testRunInvocation, requestId, "catalog_stale", "test.run was not dispatched because test.list did not return a bindable discovery generation", workflowId, evidence),
        };
      }
      if (modeOnly && (data.total_available_status !== "exact" || data.coverage_status !== "complete"
        || boundedEvidenceInteger(data.total_available, 0, 100_000) === undefined
        || boundedEvidenceInteger(data.runnable_leaf_count, 0, 100_000) === undefined
        || (data.runnable_leaf_count as number) > (data.total_available as number))) {
        return { state: "blocked", result: testRunDiscoveryFailure(testRunInvocation, requestId, "bridge_protocol_error", "test.run was not dispatched because mode-only discovery lacked authoritative catalog coverage", workflowId, evidence) };
      }
      if (modeOnly && data.runnable_leaf_count === 0) {
        return { state: "blocked", result: testRunDiscoveryFailure(testRunInvocation, requestId, "test_filter_unresolved", "test.run was not dispatched because the requested mode has no authoritative runnable TestRunner leaves", workflowId, buildEvidence("ready", "mode_only_no_runnable_leaves", generation)) };
      }
      return { state: "ready", discoveryGeneration: generation, evidence };
    }
    if (status === "unavailable" || status === "incomplete") {
      const evidence = buildEvidence(status, lastReason, generation);
      return {
        state: "blocked",
        result: testRunDiscoveryFailure(testRunInvocation, requestId, "test_filter_resolution_unavailable", "test.run was not dispatched because authoritative TestRunner discovery is unavailable or incomplete", workflowId, evidence),
      };
    }
    if (status !== "pending") {
      const evidence = buildEvidence("error", lastReason, generation);
      return {
        state: "blocked",
        result: testRunDiscoveryFailure(testRunInvocation, requestId, "bridge_protocol_error", "test.run was not dispatched because test.list returned an invalid discovery status", workflowId, evidence),
      };
    }

    const remainingMs = deadlineUnixMs - nowMs(context);
    if (remainingMs <= 0) break;
    await sleepMs(context, Math.min(TEST_RUN_DISCOVERY_POLL_INTERVAL_MS, remainingMs));
  }

  const evidence = buildEvidence(lastStatus === "pending" ? "pending" : lastStatus, lastReason, lastGeneration);
  return { state: "blocked", result: testRunDiscoveryPending(testRunInvocation, requestId, commandArgs, workflowId, testRunInvocation.descriptor.timeout_ms, evidence) };

  function buildEvidence(status: TestRunDiscoveryPreflightEvidence["status"], reason: string, discoveryGeneration: number | null): TestRunDiscoveryPreflightEvidence {
    const record = testRunInvocation.record!;
    return {
      schema_version: 1,
      contract: TEST_RUN_DISCOVERY_PREFLIGHT_CONTRACT,
      status,
      poll_count: pollCount,
      read_request_count: readRequestCount,
      elapsed_ms: Math.max(0, nowMs(context) - startedAtMs),
      metadata_elapsed_ms: metadataElapsedMs,
      startup_deadline_ms: startupDeadlineMs,
      bridge_admission_reserve_ms: bridgeAdmissionReserveMs,
      mode,
      assembly,
      discovery_generation: discoveryGeneration,
      reason,
      test_run_bridge_request_issued: false,
      test_runner_execute_invoked: false,
      command_replayed: false,
      workflow_identity: {
        workflow_id: workflowId,
        project_selector: testRunInvocation.identity.projectSelector,
        project: testRunInvocation.identity.project,
        instance_id: record.instance_id,
        unity_pid: record.unity_pid,
        unity_process_start_utc: record.unity_process_start_utc ?? "",
        heartbeat_generation: record.heartbeat_generation ?? null,
        catalog_generation: record.catalog_generation ?? null,
        commands_hash: testRunInvocation.commandsHash,
        compilation_generation: record.compilation_generation ?? null,
        completed_compilation_generation: record.completed_compilation_generation ?? null,
        discovery_generation: discoveryGeneration,
      },
    };
  }
}

async function prepareTestRunDiscoveryInvocation(
  context: DispatcherRuntimeContext,
  testRunInvocation: InvocationContextSnapshot,
  requestId: string | undefined,
  listArgs: Record<string, unknown>,
  workflowId: string,
  deadlineUnixMs: number,
  strictMetadata = false,
): Promise<{ state: "ready"; invocation: InvocationContextSnapshot } | { state: "unsupported" } | { state: "blocked"; result: GatewayResult }> {
  const record = testRunInvocation.record;
  if (!record || !context.bridge) return { state: "blocked", result: bridgeUnavailable(testRunInvocation.identity.project, TEST_RUN_COMMAND, requestId) };
  try {
    const resolved = await resolveCommandDescriptor(context.bridge, record, TEST_LIST_COMMAND, recoveryTransport(context, deadlineUnixMs));
    const descriptor = resolved?.command;
    if (!descriptor) return strictMetadata
      ? { state: "blocked", result: testRunCompileReadinessFailure(testRunInvocation, requestId, workflowId, "catalog_stale", "discovery_helper_unavailable") }
      : { state: "unsupported" };
    if (descriptor.risk !== "read"
      || strictMetadata && descriptor.id !== TEST_LIST_COMMAND
      || descriptor.idempotency !== "safe"
      || !isTrustedBuiltInDescriptor(descriptor)
      || strictMetadata && (resolved.metadata.project !== record.project || resolved.metadata.instance_id !== record.instance_id
        || resolved.metadata.catalog_generation !== record.catalog_generation || resolved.metadata.commands_hash !== testRunInvocation.commandsHash
        || descriptor.package_id !== testRunInvocation.descriptor.package_id || descriptor.package_version !== testRunInvocation.descriptor.package_version)
      || strictMetadata && !supportsTypedReadinessArguments(descriptor.schema, listArgs)
      || resolved.commandsHash !== testRunInvocation.commandsHash
      || validateCommandArgs(descriptor.schema, listArgs, true).status !== "ok") {
      return {
        state: "blocked",
        result: testRunDiscoveryFailure(testRunInvocation, requestId, "catalog_stale", "test.run requires a matching trusted read-only test.list descriptor before filtered execution", workflowId),
      };
    }
    refreshManagedPolicy(context);
    const degraded = degradedPolicyGate(context, testRunInvocation.identity.project, TEST_RUN_COMMAND, requestId, testRunInvocation.descriptor.risk);
    if (degraded) return { state: "blocked", result: withCommandNotDispatched(degraded) };
    if (!isFeatureEnabled(descriptor, context.features)) {
      return { state: "blocked", result: withCommandNotDispatched(featureDisabled(testRunInvocation.identity.project, TEST_LIST_COMMAND, descriptor.feature_flag, requestId)) };
    }
    const policy = evaluatePolicy(context.policy, testRunInvocation.identity.project, descriptor);
    if (policy.action !== "allow") {
      return {
        state: "blocked",
        result: testRunDiscoveryFailure(testRunInvocation, requestId, "permission_denied", "test.run requires policy-allowed read-only test.list preflight before execution", workflowId),
      };
    }
    return {
      state: "ready",
      invocation: InvocationContextSnapshot.create({
        projectSelector: record.instance_id,
        project: testRunInvocation.identity.project,
        command: TEST_LIST_COMMAND,
        descriptor,
        commandsHash: resolved.commandsHash,
        record,
      }),
    };
  } catch (error) {
    const bridgeError = bridgeThrownError(error, {
      project: testRunInvocation.identity.project,
      projectSelector: record.instance_id,
      command: TEST_LIST_COMMAND,
      record,
      registry: context.registry,
    }, context);
    if (strictMetadata) return { state: "blocked", result: testRunCompileReadinessFailure(testRunInvocation, requestId, workflowId, bridgeError?.status === "error" ? bridgeError.error.code : "bridge_protocol_error", "discovery_metadata_read_failed") };
    if (bridgeError?.status === "error") return { state: "blocked", result: testRunDiscoveryTransportFailure(testRunInvocation, requestId, bridgeError, workflowId) };
    throw error;
  }
}

function testRunDiscoveryPending(invocation: InvocationContextSnapshot, requestId: string | undefined, commandArgs: Record<string, unknown>, workflowId: string, timeoutMs: number, evidence?: TestRunDiscoveryPreflightEvidence): GatewayResult {
  const mode = typeof commandArgs.mode === "string" && commandArgs.mode.length > 0 ? commandArgs.mode : "editmode";
  const fallbackEvidence: TestRunDiscoveryPreflightEvidence = evidence ?? {
    schema_version: 1,
    contract: TEST_RUN_DISCOVERY_PREFLIGHT_CONTRACT,
    status: "pending",
    poll_count: 0,
    read_request_count: 0,
    elapsed_ms: timeoutMs,
    startup_deadline_ms: timeoutMs,
    bridge_admission_reserve_ms: 0,
    mode,
    assembly: typeof commandArgs.assembly === "string" ? commandArgs.assembly : "",
    discovery_generation: null,
    reason: "startup_deadline_reached",
    test_run_bridge_request_issued: false,
    test_runner_execute_invoked: false,
    command_replayed: false,
    workflow_identity: { workflow_id: workflowId, project_selector: invocation.identity.projectSelector, project: invocation.identity.project },
  };
  return testRunDiscoveryFailure(invocation, requestId, "test_filter_resolution_pending", "test.run was not dispatched because authoritative TestRunner discovery remained pending until the startup deadline", workflowId, fallbackEvidence);
}

function testRunDiscoveryTransportFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, result: Extract<GatewayResult, { status: "error" }>, workflowId: string, evidence?: TestRunDiscoveryPreflightEvidence): GatewayResult {
  return testRunDiscoveryFailure(invocation, requestId, result.error.code, "test.run was not dispatched because its read-only TestRunner discovery preflight failed", workflowId, evidence, result.error.details);
}

function testRunDiscoveryFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, code: ErrorCode, message: string, workflowId: string, evidence?: TestRunDiscoveryPreflightEvidence, originalDetails: Record<string, unknown> = {}): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code,
      message,
      details: {
        ...originalDetails,
        ...preDispatchNonDeliveryDetails(),
        test_run_workflow_id: workflowId,
        test_run_bridge_request_issued: false,
        test_runner_execute_invoked: false,
        test_run_replayed: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        ...(evidence ? { test_run_discovery_preflight: evidence } : {}),
      },
    },
    next_valid_actions: code === "test_filter_resolution_pending"
      ? ["retry_test.run_after_short_delay_only_if_still_intended", "do_not_replay_test.run_automatically"]
      : ["inspect_test_runner_discovery_and_compile_lifecycle", "repeat_test.run_only_if_still_intended", "do_not_replay_test.run_automatically"],
  });
}

function withTestRunDiscoveryPreflightEvidence(result: GatewayResult, evidence?: TestRunDiscoveryPreflightEvidence): GatewayResult {
  if (!evidence) return result;
  if (result.status === "ok") return { ...result, data: mergeRecoveryData(result.data, { test_run_discovery_preflight: evidence }) };
  const annotated = { ...result, error: { ...result.error, details: { ...result.error.details, test_run_discovery_preflight: evidence } } };
  if (optedInTestRunPreflightFailures.has(result)) optedInTestRunPreflightFailures.add(annotated);
  return annotated;
}

function terminalNoExecutionEvidenceFromBridgeResult(result: GatewayResult): TestRunTerminalNoExecutionEvidence | undefined {
  if (result.status !== "error") return undefined;
  const details = result.error.details;
  if (!isObjectRecord(details)
    || details.command !== TEST_RUN_COMMAND
    || (Object.hasOwn(details, "operation_created") && details.operation_created !== false)
    || (Object.hasOwn(details, "artifact_persisted") && details.artifact_persisted !== false)
    || (Object.hasOwn(details, "execute_dispatch_count") && details.execute_dispatch_count !== 0)
    || (Object.hasOwn(details, "execute_dispatch_committed") && details.execute_dispatch_committed !== false)
    || (Object.hasOwn(details, "test_runner_execute_invocation_attempted") && details.test_runner_execute_invocation_attempted !== false)
    || details.command_dispatched !== false
    || details.test_runner_execute_invoked !== false
    || details.command_bridge_request_issued !== true
    || typeof details.reason !== "string") {
    return undefined;
  }
  const packageUpdate = terminalPackageUpdateEvidence(details.package_update);
  if (!packageUpdate) return undefined;
  const evidence = { bridge_error_code: result.error.code, reason: details.reason, package_update: packageUpdate };
  try { assertTestRunTerminalNoExecutionEvidence(evidence); }
  catch { return undefined; }
  if (details.package_refresh_required !== packageUpdate.package_refresh_required
    || details.package_refresh_wait_can_advance !== packageUpdate.package_refresh_wait_can_advance
    || evidence.reason === "package_refresh_required" && (!packageUpdate.package_refresh_required || packageUpdate.package_refresh_wait_can_advance)) {
    return undefined;
  }
  return evidence;
}

function terminalPackageUpdateEvidence(value: unknown): TestRunTerminalPackageUpdateEvidence | undefined {
  if (!isObjectRecord(value) || !isObjectRecord(value.local_package_code_inventory)) return undefined;
  const inventory = value.local_package_code_inventory;
  const compilerSourceEvidence = ["compiler_source_file_count", "missing_compiler_source_file_count", "unexpected_compiler_source_file_count", "compiler_source_fingerprint"]
    .every((key) => Object.hasOwn(inventory, key))
    ? {
        compiler_source_file_count: inventory.compiler_source_file_count as number,
        missing_compiler_source_file_count: inventory.missing_compiler_source_file_count as number,
        unexpected_compiler_source_file_count: inventory.unexpected_compiler_source_file_count as number,
        compiler_source_fingerprint: inventoryString(inventory.compiler_source_fingerprint),
      }
    : {};
  return {
    status: inventoryString(value.status) as TestRunTerminalPackageUpdateEvidence["status"],
    refresh_pending: value.refresh_pending as boolean,
    package_refresh_required: value.package_refresh_required as boolean,
    package_refresh_wait_can_advance: value.package_refresh_wait_can_advance as boolean,
    stale_package_resolution: value.stale_package_resolution as boolean,
    fingerprint: inventoryString(value.fingerprint),
    pending_fingerprint: inventoryString(value.pending_fingerprint),
    last_transition_utc: inventoryString(value.last_transition_utc),
    local_package_code_inventory: {
      status: inventoryString(inventory.status) as TestRunTerminalPackageInventoryEvidence["status"],
      failure_reason: inventoryString(inventory.failure_reason) as TestRunTerminalPackageInventoryEvidence["failure_reason"],
      mismatch_package: inventoryString(inventory.mismatch_package),
      local_package_count: inventory.local_package_count as number,
      source_file_count: inventory.source_file_count as number,
      asset_database_file_count: inventory.asset_database_file_count as number,
      missing_asset_file_count: inventory.missing_asset_file_count as number,
      unexpected_asset_file_count: inventory.unexpected_asset_file_count as number,
      source_fingerprint: inventoryString(inventory.source_fingerprint),
      asset_database_fingerprint: inventoryString(inventory.asset_database_fingerprint),
      source_state_fingerprint: inventoryString(inventory.source_state_fingerprint),
      ...compilerSourceEvidence,
    },
  };
}

function inventoryString(value: unknown): string {
  return typeof value === "string" ? value : "\0".repeat(257);
}

function testRunTerminalNoExecutionStartResult(result: GatewayResult, binding: TestRunOperationBinding, marker: TestRunRequestIssuanceMarker, receipt: TestRunTerminalNoExecutionReceipt): GatewayResult {
  if (result.status === "ok") return result;
  const preservedDetails = { ...(result.error.details ?? {}) };
  for (const key of ["next_valid_actions", "recovery_actions", "recovery_hint", "repeat_request_template", "explicit_repeat_workflow"] as const) delete preservedDetails[key];
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...preservedDetails,
        operation_id: binding.operation_id,
        run_id: binding.operation_id,
        terminal: true,
        operation_created: false,
        command_dispatched: false,
        test_runner_execute_invoked: false,
        artifact_persisted: false,
        test_run_delivery_state: "not_executed",
        test_run_replayed: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        rejected_operation_reusable: false,
        new_operation_required_for_future_run: true,
        test_run_terminal_no_execution: terminalNoExecutionReceiptEvidence(receipt),
        ...testRunRequestIssuanceEvidence(binding, { status: "found", marker }),
        status_lookup: { tool: "sgg_unity_call", project: binding.original_project_selector, command: TEST_RUN_STATUS_COMMAND, args_json: JSON.stringify({ operation_id: binding.operation_id, response_mode: "compact" }), mode: "wait" },
      },
    },
    next_valid_actions: testRunTerminalNoExecutionNextValidActions(receipt),
  };
}

function testRunTerminalNoExecutionPublicationFailure(invocation: InvocationContextSnapshot, requestId: string | undefined, binding: TestRunOperationBinding, marker: TestRunRequestIssuanceMarker, result: GatewayResult, storeError: TestRunOperationStoreError): GatewayResult {
  const originalDetails = result.status === "error" ? result.error.details : undefined;
  const pending = storeError instanceof TestRunPublicationDeadlineError;
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: "test_run_status_unavailable",
      message: "The Bridge proved that test.run was not executed, but its durable terminal outcome could not be published conclusively",
      details: {
        ...(originalDetails ?? {}),
        preflight_rejection_reason: originalDetails?.reason,
        reason: pending ? "terminal_no_execution_outcome_publication_pending" : "terminal_no_execution_outcome_publication_failed",
        ...(pending ? { publication_observation: "acknowledgement_pending_at_caller_deadline" } : { storage_reason: storeError.reason }),
        ...(storeError.diagnostics ? { test_run_storage: storeError.diagnostics } : {}),
        operation_id: binding.operation_id,
        run_id: binding.operation_id,
        terminal_no_execution_observed_in_current_response: true,
        durable_terminal_no_execution_publication_outcome: "unknown",
        operation_created: false,
        command_dispatched: false,
        test_runner_execute_invoked: false,
        artifact_persisted: false,
        test_run_delivery_state: "uncertain",
        test_run_replayed: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        status_or_artifact_check_required_before_repeat: true,
        repeat_blocked_until_delivery_state_checked: true,
        ...testRunRequestIssuanceEvidence(binding, { status: "found", marker }, true),
      },
    },
    next_valid_actions: ["inspect_the_private_test_run_operation_store", "poll_the_exact_reserved_operation_status", "call_diagnostics.report_problem", "do_not_repeat_test.run"],
  });
}

function completeCorrelatedTestRun(result: GatewayResult, operationId: string): GatewayResult {
  if (result.status === "error") {
    const details = result.error.details;
    if (details?.operation_id === operationId && details.test_run_recovery_contract === TEST_RUN_TIMEOUT_RECOVERY_CONTRACT) {
      return { ...result, next_valid_actions: testRunRecoveryNextValidActions(details.test_run_delivery_state === "not_dispatched") };
    }
    if (result.error.code === "bridge_unreachable" && details?.command_bridge_request_issued === true) {
      const recoveryActions = testRunRecoveryNextValidActions(false);
      const preservedDetails = { ...(details ?? {}) };
      for (const key of ["dirty_scene_state", "explicit_repeat_workflow", "repeat_request_template", "recovery_actions", "recovery_hint"] as const) {
        delete preservedDetails[key];
      }
      return {
        ...result,
        error: {
          ...result.error,
          details: {
            ...preservedDetails,
            ...testRunRecoveryEvidence(operationId, result.project, "uncertain", true),
            command_delivery_state: "uncertain",
            explicit_repeat_required: false,
            explicit_repeat_request_id_required: false,
            explicit_repeat_request_id_recommended: false,
            safe_to_auto_retry_on_single_current_candidate: false,
            write_replay_allowed: false,
            new_operation_id_allocated: false,
            replacement_operation_allocated: false,
            replacement_test_run_dispatched: false,
            repeatability_warning: "Do not repeat test.run while delivery remains uncertain. Poll the exact reserved operation or inspect its expected artifact first.",
            ...(preservedDetails.current_candidate_instances ? { current_candidate_note: "Replacement candidates are diagnostic only; do not route or repeat this issued test.run on another Editor." } : {}),
            recovery_actions: recoveryActions,
            recovery_hint: "The Bridge connection was lost after the exact test.run request was issued. Poll the Gateway-reserved operation with status_lookup; do not allocate or dispatch a replacement run.",
          },
        },
        next_valid_actions: recoveryActions,
      };
    }
    if (result.error.code === "unity_exception") {
      const recovery = testRunRecoveryEvidence(operationId, result.project, "uncertain", true);
      return {
        ...result,
        error: { ...result.error, details: { ...(details ?? {}), ...recovery, startup_error_returned: true } },
        next_valid_actions: testRunRecoveryNextValidActions(false),
      };
    }
    return result;
  }
  const data = isObjectRecord(result.data) ? result.data : undefined;
  const returnedOperationId = typeof data?.operation_id === "string" ? data.operation_id : undefined;
  const returnedRunId = typeof data?.run_id === "string" ? data.run_id : undefined;
  if (returnedOperationId === operationId && returnedRunId === operationId) return result;
  const returnedCandidates = [...new Set([returnedOperationId, returnedRunId].filter((value): value is string => typeof value === "string" && /^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/.test(value)))];
  const details = testRunRecoveryEvidence(operationId, result.project, "uncertain", true, returnedCandidates);
  return fail({
    ...optionalRequestId(result.request_id),
    ...(result.project ? { project: result.project } : {}),
    command: TEST_RUN_COMMAND,
    error: {
      code: "bridge_protocol_error",
      message: "test.run response did not preserve the Gateway-reserved operation id",
      details: {
        ...details,
        protocol_failure: "test_run_operation_id_mismatch",
        returned_operation_id_state: returnedOperationId === undefined ? "missing" : returnedOperationId === operationId ? "matching" : "mismatching",
        returned_run_id_state: returnedRunId === undefined ? "missing" : returnedRunId === operationId ? "matching" : "mismatching",
      },
    },
    diagnostics: result.diagnostics,
    next_valid_actions: testRunRecoveryNextValidActions(false),
  });
}

function settleTestRunQueueBeforeDeadline(
  context: DispatcherContext,
  queued: Promise<GatewayResult>,
  deadlineUnixMs: number,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  commandArgs: Record<string, unknown>,
  timeoutMs: number,
  operationId: string,
  delivery: { queueAdmitted: boolean; queueExpired: boolean; discoveryPending: boolean; writeRequestIssued: boolean; responseDeadlineUnixMs?: number; bookkeepingPendingResult?: GatewayResult },
): Promise<GatewayResult> {
  const remainingMs = Math.max(0, deadlineUnixMs - nowMs(context));
  return new Promise((resolve, reject) => {
    let settled = false;
    const settle = (result: GatewayResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    let timer = setTimeout(onQueueDeadline, remainingMs);
    queued.then(settle, (error: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    });

    function onQueueDeadline(): void {
      const queueRemainingMs = deadlineUnixMs - nowMs(context);
      if (queueRemainingMs > 0) {
        timer = setTimeout(onQueueDeadline, queueRemainingMs);
        return;
      }
      delivery.queueExpired = true;
      if (!delivery.queueAdmitted) {
        settle(testRunQueueTimeout(invocation, requestId, timeoutMs, operationId));
        return;
      }
      if (delivery.discoveryPending && !delivery.writeRequestIssued) {
        settle(testRunDiscoveryPending(invocation, requestId, commandArgs, operationId, timeoutMs));
        return;
      }
      const responseDeadlineUnixMs = delivery.responseDeadlineUnixMs ?? deadlineUnixMs;
      const responseRemainingMs = Math.max(0, responseDeadlineUnixMs - nowMs(context));
      timer = setTimeout(() => settle(delivery.bookkeepingPendingResult ?? (delivery.writeRequestIssued
        ? testRunDispatchTimeout(invocation, requestId, timeoutMs, operationId)
        : testRunRequestNotIssuedTimeout(invocation, requestId, timeoutMs, operationId))), responseRemainingMs);
    }
  });
}

function testRunQueueTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: "queue_timeout",
      message: "Timed out waiting for project queue admission before test.run dispatch",
      details: {
        ...testRunRecoveryEvidence(operationId, invocation.identity.projectSelector, "not_dispatched", false),
        timeout_kind: "gateway_project_queue_timeout",
        failure_phase: "project_queue",
        timeout_ms: timeoutMs,
        bridge_request_issued: false,
      },
    },
    next_valid_actions: testRunRecoveryNextValidActions(true),
  });
}

function testRunDispatchTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: "bridge_timeout",
      message: "test.run request was issued but its startup response was not verified before the command deadline",
      details: {
        ...testRunRecoveryEvidence(operationId, invocation.identity.projectSelector, "uncertain", true),
        timeout_kind: "test_run_startup_timeout",
        failure_phase: "test_run_startup",
        timeout_ms: timeoutMs,
        bridge_request_issued: true,
      },
    },
    next_valid_actions: testRunRecoveryNextValidActions(false),
  });
}

function testRunRequestNotIssuedTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: "bridge_timeout",
      message: "test.run was admitted but no Bridge request was issued before the startup deadline",
      details: {
        ...testRunRecoveryEvidence(operationId, invocation.identity.projectSelector, "not_dispatched", false),
        timeout_kind: "test_run_pre_dispatch_timeout",
        failure_phase: "bridge_request",
        timeout_ms: timeoutMs,
      },
    },
    next_valid_actions: testRunRecoveryNextValidActions(true),
  });
}

function testRunRecoveryEvidence(operationId: string, project: string | undefined, deliveryState: "not_dispatched" | "uncertain", requestIssued: boolean, returnedCandidates: string[] = []): Record<string, unknown> {
  const selector = project ?? "<project_from_original_test_run>";
  const candidateIds = [...new Set([operationId, ...returnedCandidates])];
  return {
    test_run_recovery_contract: TEST_RUN_TIMEOUT_RECOVERY_CONTRACT,
    operation_id: operationId,
    run_id: operationId,
    operation_id_source: "gateway_reserved",
    delivery_state: deliveryState,
    test_run_delivery_state: deliveryState,
    bridge_request_issued: requestIssued,
    command_bridge_request_issued: requestIssued,
    auto_retry_attempted: false,
    test_run_replayed: false,
    command_replayed: false,
    write_command_repeated: false,
    write_replay_allowed: false,
    status_or_artifact_check_required_before_repeat: deliveryState === "uncertain",
    expected_artifact_path: `Library/SGGUnityMcp/job-artifacts/test-runs/${operationId}.json`,
    artifact_presence: "unknown",
    status_lookup: {
      tool: "sgg_unity_call",
      project: selector,
      command: TEST_RUN_STATUS_COMMAND,
      args_json: JSON.stringify({ operation_id: operationId }),
      mode: "wait",
    },
    ...(candidateIds.length > 1 ? {
      uncertain_operation_id_candidates: candidateIds,
      candidate_status_lookups: candidateIds.map((candidate) => ({
        tool: "sgg_unity_call",
        project: selector,
        command: TEST_RUN_STATUS_COMMAND,
        args_json: JSON.stringify({ operation_id: candidate }),
        mode: "wait",
      })),
      all_candidate_statuses_must_be_checked_before_repeat: true,
    } : {}),
    ...(deliveryState === "uncertain" ? { repeat_blocked_until_delivery_state_checked: true } : { explicit_repeat_required: true }),
  };
}

function testRunRecoveryNextValidActions(notDispatched: boolean): string[] {
  return notDispatched
    ? ["repeat_test.run_only_if_still_intended", "use_a_new_request_id"]
    : ["poll_exact_test.run.status", "inspect_expected_test_run_artifact", "call_sgg_unity_projects_if_status_is_unavailable", "do_not_repeat_test.run_until_delivery_state_is_resolved"];
}

function settlePlayModeQueueBeforeDeadline(
  context: DispatcherContext,
  queued: Promise<GatewayResult>,
  deadlineUnixMs: number,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  timeoutMs: number,
  delivery: { writeRequestIssued: boolean },
  operationId: string,
  target: PlayModeTarget | undefined,
): Promise<GatewayResult> {
  const remainingMs = Math.max(0, deadlineUnixMs - nowMs(context));
  return new Promise((resolve, reject) => {
    let settled = false;
    const settle = (result: GatewayResult) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(result);
    };
    const timer = setTimeout(() => settle(delivery.writeRequestIssued
      ? playModeTransitionDeadlineTimeout(invocation, requestId, timeoutMs, operationId, target)
      : playModeQueueTimeout(invocation, requestId, timeoutMs)), remainingMs);
    queued.then(settle, (error: unknown) => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      reject(error);
    });
  });
}

function playModeQueueTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: invocation.identity.command,
    error: {
      code: "queue_timeout",
      message: "Timed out waiting for project queue admission before Play Mode dispatch",
      details: {
        timeout_kind: "gateway_project_queue_timeout",
        failure_phase: "project_queue",
        timeout_ms: timeoutMs,
        delivery_state: "not_dispatched",
        bridge_request_issued: false,
        write_command_repeated: false,
        write_replay_allowed: false,
      },
    },
    next_valid_actions: ["call_editor.status", "repeat_playmode_command_only_if_still_intended"],
  });
}

function playModeTransitionDeadlineTimeout(invocation: InvocationContextSnapshot, requestId: string | undefined, timeoutMs: number, operationId: string, target: PlayModeTarget | undefined): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: invocation.identity.command,
    error: {
      code: "unity_busy",
      message: "Play Mode command was dispatched but transition verification exceeded the original deadline",
      details: {
        ...playModeTransitionRecoveryEvidence(invocation, target, operationId),
        reason: "playmode_transition_unverified",
        timeout_kind: "playmode_transition_timeout",
        failure_phase: "post_transition_verification",
        timeout_ms: timeoutMs,
        delivery_state: "dispatched",
        bridge_request_issued: true,
        original_command_completed: "unknown",
        write_command_repeated: false,
        write_replay_allowed: false,
      },
    },
    next_valid_actions: ["poll_exact_editor.status", "do_not_auto_repeat_playmode_command", "call_sgg_unity_projects"],
  });
}

function isValidationAfterEditTimeoutResult(result: GatewayResult): boolean {
  if (result.status === "error") {
    return result.error.code === "bridge_timeout"
      || result.error.code === "command_timeout"
      || isTimeoutShapedRecord(result.error.details);
  }

  return isTimeoutShapedRecord(result.data);
}

function isDefinitiveValidationRefreshTimeout(result: GatewayResult): boolean {
  if (result.status !== "error" || result.error.code !== "command_timeout") return false;
  const details = result.error.details;
  return details?.refresh_completed === false
    && (details.failure_phase === "asset_database_refresh" || details.failure_phase === "main_thread_dispatch");
}

function isTimeoutShapedRecord(value: unknown): boolean {
  if (!isObjectRecord(value)) {
    return false;
  }

  if (value.timed_out === true || typeof value.timeout_kind === "string") {
    return true;
  }

  const checks = value.checks;
  if (isObjectRecord(checks) && checks.timed_out === true) {
    return true;
  }

  const compileStatus = value.compile_status;
  return isObjectRecord(compileStatus) && compileStatus.timed_out === true;
}

function compileWaitReadyArgsFromRefreshArgs(commandArgs: Record<string, unknown>): Record<string, unknown> {
  const expectedScriptPaths = expectedScriptPathsForVerification(commandArgs.expected_script_paths);
  return {
    ...(typeof commandArgs.timeout_ms === "number" ? { timeout_ms: commandArgs.timeout_ms } : {}),
    ...(typeof commandArgs.poll_interval_ms === "number" ? { poll_interval_ms: commandArgs.poll_interval_ms } : {}),
    ...(barrierExpectationId(commandArgs) ? { _compile_barrier_expectation_id: barrierExpectationId(commandArgs) } : {}),
    ...(expectedScriptPaths ? { _expected_script_paths: expectedScriptPaths } : {}),
    _require_explicit_compile_barrier: true,
  };
}

function expectedScriptPathsForVerification(value: unknown): string[] | undefined {
  if (!Array.isArray(value) || value.length < 1 || value.length > 64) return undefined;
  const paths = value.filter((item): item is string => typeof item === "string" && item.length >= 1 && item.length <= 512);
  return paths.length === value.length ? [...paths] : undefined;
}

function compileWaitReadyArgsFromWriteArgs(commandArgs: Record<string, unknown>): Record<string, unknown> {
  return {
    ...(typeof commandArgs.timeout_ms === "number" ? { timeout_ms: commandArgs.timeout_ms } : {}),
    ...(typeof commandArgs.poll_interval_ms === "number" ? { poll_interval_ms: commandArgs.poll_interval_ms } : {}),
  };
}

function barrierExpectationId(commandArgs: Record<string, unknown>): string | undefined {
  const value = commandArgs._compile_barrier_expectation_id;
  return typeof value === "string" && /^[a-f0-9]{32}$/.test(value) ? value : undefined;
}

function validationAfterEditTimeoutVerificationResult(args: UnityCallArgs, originalResult: GatewayResult, verification: GatewayResult, verificationProject: string): GatewayResult {
  const project = verification.project ?? originalResult.project ?? args.project;
  const externalSceneBlocker = externalSceneReloadVerificationResult(args, originalResult, verification, project);
  if (externalSceneBlocker) return externalSceneBlocker;
  const originalError = originalResult.status === "error" ? originalResult.error : undefined;
  const originalDetails = originalResult.status === "error"
    ? originalResult.error.details ?? {}
    : isObjectRecord(originalResult.data) ? originalResult.data : {};
  const recovery = validationAfterEditTimeoutRecoveryMetadata(args.command, originalResult, verification, isCompileStatusReadyClean(verification), verificationProject);
  const nextValidActions = mergeNextValidActions(
    validationAfterEditTimeoutNextValidActions(verification),
    originalResult.next_valid_actions,
    verification.next_valid_actions,
  );

  return fail({
    ...optionalRequestId(args.request_id),
    project,
    command: args.command,
    error: {
      code: originalError?.code ?? "command_timeout",
      message: originalError?.message ?? "validation.after_edit timed out before validation completed",
      details: {
        ...originalDetails,
        ...recovery,
      },
    },
    diagnostics: originalResult.diagnostics,
    ...(nextValidActions.length > 0 ? { next_valid_actions: nextValidActions } : {}),
    ...(originalResult.artifact_refs ? { artifact_refs: originalResult.artifact_refs } : {}),
    ...(originalResult.truncated !== undefined ? { truncated: originalResult.truncated } : {}),
  });
}

function validationAfterEditTimeoutRecoveryMetadata(originalCommand: string, originalResult: GatewayResult, verification: GatewayResult, compileReadyAfterTimeout: boolean, verificationProject: string): Record<string, unknown> {
  const originalError = originalResult.status === "error" ? originalResult.error : undefined;
  const originalDetails = originalResult.status === "error" ? originalResult.error.details ?? {} : isObjectRecord(originalResult.data) ? originalResult.data : {};
  const verificationErrorCode = verification.status === "error" ? verification.error.code : undefined;
  const staleReloadRecovery = verificationErrorCode === "unity_reloading" || verificationErrorCode === "project_stale" || verificationErrorCode === "bridge_protocol_error";
  return {
    validation_timeout_recovery_attempted: true,
    compile_ready_after_timeout: compileReadyAfterTimeout,
    validation_completed_after_timeout: false,
    validation_success_after_timeout: false,
    compile_ready_is_not_validation_success: true,
    verification_command: COMPILE_STATUS_COMMAND,
    verification_project_selector: verificationProject,
    write_command_repeated: false,
    write_replay_allowed: false,
    command_replayed: false,
    original_command: originalCommand,
    ...(originalError ? { original_error_code: originalError.code } : { original_error_code: "timeout_result" }),
    original_timed_out: true,
    original_timeout_kind: typeof originalDetails.timeout_kind === "string" ? originalDetails.timeout_kind : "validation_after_edit_timeout",
    original_failure_phase: typeof originalDetails.failure_phase === "string" ? originalDetails.failure_phase : "unknown",
    original_refresh_completed: originalDetails.refresh_completed === true,
    original_validation_completed: originalDetails.validation_completed === true,
    original_validation_ready: originalDetails.validation_ready === true,
    verification_status: verification.status,
    ...(verification.status === "error" ? { verification_error_code: verification.error.code, verification_error_message: verification.error.message } : {}),
    ...(staleReloadRecovery ? {
      stale_reload_recovery_required: true,
      user_approved_restart_guidance: "If read-only recovery continues returning empty-body, stale heartbeat, or unity_reloading, restart the affected Unity Editor only after operator approval, then retry validation.after_edit on the fresh ready instance if still intended.",
    } : {}),
  };
}

function validationAfterEditTimeoutNextValidActions(verification: GatewayResult): string[] {
  return [
    "inspect_validation_after_edit_timeout_details",
    "call_sgg_unity_projects",
    "call_compile.status_with_sgg_unity_call",
    "call_compile.wait_ready_with_sgg_unity_call",
    ...(verification.status === "error" && (verification.error.code === "unity_reloading" || verification.error.code === "bridge_protocol_error") ? ["restart_unity_editor_after_user_approval_if_stale_reload_persists"] : []),
    "retry_validation.after_edit_only_if_still_intended_after_recovery",
  ];
}

function mergeNextValidActions(...sources: Array<string[] | undefined>): string[] {
  const merged: string[] = [];
  for (const source of sources) {
    for (const action of source ?? []) {
      if (!merged.includes(action)) {
        merged.push(action);
      }
    }
  }

  return merged;
}

function emptyBodyVerificationResult(args: UnityCallArgs, originalResult: Extract<GatewayResult, { status: "error" }>, verification: GatewayResult, verificationProject: string): GatewayResult {
  const project = verification.project ?? originalResult.project ?? args.project;
  const recovery = emptyBodyRecoveryMetadata(args.command, originalResult, verification.status === "ok", verificationProject);
  if (verification.status === "ok") {
    return ok({
      ...optionalRequestId(args.request_id),
      project,
      command: args.command,
      data: mergeRecoveryData(verification.data, recovery),
      diagnostics: verification.diagnostics,
      ...(verification.next_valid_actions ? { next_valid_actions: verification.next_valid_actions } : {}),
      ...(verification.artifact_refs ? { artifact_refs: verification.artifact_refs } : {}),
      ...(verification.truncated !== undefined ? { truncated: verification.truncated } : {}),
    });
  }

  return fail({
    ...optionalRequestId(args.request_id),
    project,
    command: args.command,
    error: {
      ...verification.error,
      details: {
        ...(verification.error.details ?? {}),
        ...recovery,
      },
    },
    diagnostics: verification.diagnostics,
    ...(verification.next_valid_actions ? { next_valid_actions: verification.next_valid_actions } : {}),
    ...(verification.artifact_refs ? { artifact_refs: verification.artifact_refs } : {}),
    ...(verification.truncated !== undefined ? { truncated: verification.truncated } : {}),
  });
}

function emptyBodyRecoveryMetadata(originalCommand: string, originalResult: Extract<GatewayResult, { status: "error" }>, recovered: boolean, verificationProject: string): Record<string, unknown> {
  const details = originalResult.error.details ?? {};
  return {
    recovered_from_empty_bridge_response: recovered,
    empty_bridge_response_recovery_attempted: true,
    verification_command: COMPILE_WAIT_READY_COMMAND,
    verification_project_selector: verificationProject,
    write_command_repeated: false,
    original_command: originalCommand,
    original_error_code: originalResult.error.code,
    original_protocol_failure: details.protocol_failure ?? "empty_response_body",
    original_parse_error_kind: details.parse_error_kind ?? null,
    original_response_body_empty: details.response_body_empty === true,
  };
}

function externalSceneReloadVerificationResult(args: UnityCallArgs, originalResult: GatewayResult, verification: GatewayResult, project: string): GatewayResult | undefined {
  if (verification.status !== "error" || verification.error.code !== "blocked_external_scene_reload") return undefined;
  return fail({
    ...optionalRequestId(args.request_id),
    project,
    command: args.command,
    error: {
      ...verification.error,
      details: {
        ...(verification.error.details ?? {}),
        original_command: args.command,
        original_error_code: originalResult.status === "error" ? originalResult.error.code : "timeout_result",
        write_request_issued: true,
        write_execution_state: "unknown_after_request_issued",
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      },
    },
    ...(verification.next_valid_actions ? { next_valid_actions: verification.next_valid_actions } : {}),
  });
}

function compileRefreshTimeoutRecoveryMetadata(originalCommand: string, originalResult: GatewayResult, verification: GatewayResult, verified: boolean, verificationProject: string, expectedBarrierOperationId?: string): Record<string, unknown> {
  const originalError = originalResult.status === "error" ? originalResult.error : undefined;
  const verificationValue = verification.status === "ok" ? verification.data : verification.error.details;
  const barrierVerified = isObjectRecord(verificationValue) && isVerifiedCompileBarrier(verificationValue.compile_barrier, expectedBarrierOperationId);
  return {
    timeout_recovery_attempted: true,
    verified_after_timeout: verified,
    compile_barrier_verified_after_timeout: barrierVerified,
    verification_command: COMPILE_WAIT_READY_COMMAND,
    verification_project_selector: verificationProject,
    write_command_repeated: false,
    write_replay_allowed: false,
    command_replayed: false,
    original_command: originalCommand,
    ...(originalError ? { original_error_code: originalError.code } : {}),
    original_timed_out: true,
    verification_status: verification.status,
    ...(verification.status === "error" ? { verification_error_code: verification.error.code, verification_error_message: verification.error.message } : {}),
  };
}

function isCompileStatusReadyClean(result: GatewayResult, expectedBarrierOperationId?: string, expectedScriptPaths?: string[]): boolean {
  if (!isCompileStatusReadyCleanIgnoringExpectedScriptEvidence(result, expectedBarrierOperationId)) return false;
  const data = compileEvidenceRecord(result);
  if (!data) return false;
  const expectedScriptImports = data.expected_script_imports === undefined ? undefined : safeExpectedScriptImports(data.expected_script_imports, expectedScriptPaths);
  return expectedScriptPaths === undefined
    ? data.expected_script_imports === undefined || expectedScriptImports?.verified === true
    : expectedScriptImports?.verified === true;
}

function isCompileStatusReadyCleanIgnoringExpectedScriptEvidence(result: GatewayResult, expectedBarrierOperationId?: string): boolean {
  if (result.status !== "ok") {
    return false;
  }

  const data = compileEvidenceRecord(result);
  if (!data) return false;
  const barrier = isObjectRecord(data.compile_barrier) ? data.compile_barrier : undefined;
  const barrierRequired = barrier?.required === true || expectedBarrierOperationId !== undefined;
  const barrierVerified = !barrierRequired || isVerifiedCompileBarrier(barrier, expectedBarrierOperationId);
  return data.ready === true
    && data.editor_ready !== false
    && data.compilable !== false
    && data.timed_out !== true
    && data.has_compile_errors !== true
    && barrierVerified
    && (typeof data.compile_error_count !== "number" || data.compile_error_count === 0);
}

function isCompilePostReloadReadyClean(result: GatewayResult, expectedBarrierOperationId?: string, expectedSourceInstanceId?: string, expectedReplacementInstanceId?: string, expectedScriptPaths?: string[]): boolean {
  if (!isCompileStatusReadyClean(result, expectedBarrierOperationId, expectedScriptPaths) || result.status !== "ok") return false;
  const data = compileEvidenceRecord(result);
  return hasCorrelatedCompilePostReloadBarrier(data, expectedBarrierOperationId, expectedSourceInstanceId, expectedReplacementInstanceId);
}

function isCorrelatedBackgroundCompilerPending(result: GatewayResult, record: HeartbeatRecord, context: DispatcherRuntimeContext, expectedBarrierOperationId?: string, expectedSourceInstanceId?: string, expectedReplacementInstanceId?: string): boolean {
  if (result.status !== "error" || result.error.code !== "command_timeout") return false;
  const data = compileEvidenceRecord(result);
  if (!data || !hasCorrelatedCompilePostReloadBarrier(data, expectedBarrierOperationId, expectedSourceInstanceId, expectedReplacementInstanceId)
    || data.status !== "background_compilation_pending" || data.timed_out !== true
    || data.ready !== false || data.compilable !== true || data.is_compiling !== false || data.is_updating === true
    || data.has_compile_errors !== false || data.compile_error_count !== 0
    || data.package_refresh_required !== false || data.package_metadata_unavailable === true
    || data.is_playing === true || data.terminal_compile_failure_without_reload === true) return false;
  const packageUpdate = data.package_update;
  const progress = safeBackgroundCompilerProgress(data.background_compiler_progress);
  const barrier = isObjectRecord(data.compile_barrier) ? data.compile_barrier : undefined;
  if (!isObjectRecord(packageUpdate) || packageUpdate.status !== "current" || packageUpdate.refresh_pending !== false
    || packageUpdate.package_refresh_required === true || packageUpdate.stale_package_resolution === true
    || typeof packageUpdate.fingerprint !== "string" || !/^[a-f0-9]{64}$/.test(packageUpdate.fingerprint)
    || !progress || progress.authoritative !== true || progress.active !== true || progress.failed !== false
    || progress.state !== "active" || progress.blocks_readiness !== true || !["compute_variant_compile", "shader_variant_compile", "native_compiler"].includes(String(progress.category))
    || progress.process_id !== record.unity_pid || progress.catalog_generation !== record.catalog_generation
    || progress.compilation_generation !== record.compilation_generation
    || progress.completed_compilation_generation !== record.completed_compilation_generation
    || progress.package_fingerprint !== packageUpdate.fingerprint
    || progress.completed_compilation_generation !== progress.compilation_generation
    || record.background_compiler_progress_state !== "active"
    || record.background_compiler_progress_authoritative !== true
    || record.background_compiler_progress_active !== true
    || record.background_compiler_progress_failed !== false
    || record.background_compiler_progress_category !== progress.category
    || record.background_compiler_progress_catalog_generation !== progress.catalog_generation
    || record.background_compiler_progress_compilation_generation !== progress.compilation_generation
    || record.background_compiler_progress_completed_compilation_generation !== progress.completed_compilation_generation) return false;
  const observedAt = safeBarrierTimestamp(progress.observed_utc);
  const postReloadAt = safeBarrierTimestamp(barrier?.post_reload_observed_utc);
  const heartbeatObservedAt = safeBarrierTimestamp(record.background_compiler_progress_observed_utc);
  return observedAt !== undefined && heartbeatObservedAt !== undefined && postReloadAt !== undefined
    && heartbeatObservedAt >= postReloadAt && heartbeatObservedAt <= observedAt && observedAt >= postReloadAt
    && observedAt <= nowMs(context) + 5_000 && nowMs(context) - observedAt <= 30_000;
}

function hasCorrelatedCompilePostReloadBarrier(data: unknown, expectedBarrierOperationId?: string, expectedSourceInstanceId?: string, expectedReplacementInstanceId?: string): boolean {
  if (!isObjectRecord(data)) return false;
  const barrier = data && isObjectRecord(data.compile_barrier) ? data.compile_barrier : undefined;
  if (!isVerifiedCompileBarrier(barrier, expectedBarrierOperationId)) return false;
  const sourceInstanceId = typeof barrier?.source_instance_id === "string" ? barrier.source_instance_id : undefined;
  const reloadSourceInstanceId = typeof barrier?.reload_source_instance_id === "string" ? barrier.reload_source_instance_id : undefined;
  const replacementInstanceId = typeof barrier?.replacement_instance_id === "string" ? barrier.replacement_instance_id : undefined;
  const startedAt = safeBarrierTimestamp(barrier?.started_utc);
  const finishedAt = safeBarrierTimestamp(barrier?.finished_utc);
  const terminalAt = safeBarrierTimestamp(barrier?.terminal_compile_observed_utc);
  const reloadStartedAt = safeBarrierTimestamp(barrier?.reload_started_utc);
  const postReloadAt = safeBarrierTimestamp(barrier?.post_reload_observed_utc);
  return barrier?.terminal_compile_result === "clean"
    && barrier.reload_started === true
    && barrier.post_reload_observed === true
    && typeof sourceInstanceId === "string"
    && sourceInstanceId.length > 0
    && (expectedSourceInstanceId === undefined || sourceInstanceId === expectedSourceInstanceId)
    && reloadSourceInstanceId === sourceInstanceId
    && typeof replacementInstanceId === "string"
    && replacementInstanceId.length > 0
    && replacementInstanceId !== sourceInstanceId
    && (expectedReplacementInstanceId === undefined || replacementInstanceId === expectedReplacementInstanceId)
    && startedAt !== undefined
    && finishedAt !== undefined
    && terminalAt !== undefined
    && reloadStartedAt !== undefined
    && postReloadAt !== undefined
    && terminalAt >= finishedAt
    && reloadStartedAt >= startedAt
    && reloadStartedAt - startedAt <= MAX_COMPILE_RELOAD_EDGE_MS
    && Math.abs(terminalAt - reloadStartedAt) <= MAX_COMPILE_RELOAD_EDGE_MS
    && postReloadAt >= reloadStartedAt
    && postReloadAt >= terminalAt
    && postReloadAt - reloadStartedAt <= MAX_COMPILE_RELOAD_EDGE_MS
    && postReloadAt - terminalAt <= MAX_COMPILE_RELOAD_EDGE_MS;
}

function isExactTerminalCompileFailureWithoutReload(result: GatewayResult, expectedBarrierOperationId?: string, expectedSourceInstanceId?: string): boolean {
  if (result.status !== "error" || result.error.code !== "compilation_failed") return false;
  const value = compileEvidenceRecord(result);
  return isExactTerminalCompileFailureEvidence(value, expectedBarrierOperationId, expectedSourceInstanceId);
}

function isExactTerminalCompileFailureEvidence(value: unknown, expectedBarrierOperationId?: string, expectedSourceInstanceId?: string): boolean {
  if (!isObjectRecord(value)) return false;
  const barrier = isObjectRecord(value.compile_barrier) ? value.compile_barrier : undefined;
  if (!isVerifiedCompileBarrier(barrier, expectedBarrierOperationId)) return false;
  const finishedAt = safeBarrierTimestamp(barrier?.finished_utc);
  const terminalAt = safeBarrierTimestamp(barrier?.terminal_compile_observed_utc);
  const terminalErrorCount = boundedEvidenceInteger(barrier?.terminal_compile_error_count, 1, 100);
  const compileErrorCount = boundedEvidenceInteger(value.compile_error_count, 1, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  return value.terminal_compile_failure_without_reload === true
    && value.has_compile_errors === true
    && compileErrorCount !== undefined
    && barrier?.terminal_compile_failure_without_reload === true
    && barrier.terminal_compile_result === "failed"
    && (expectedSourceInstanceId === undefined || barrier.source_instance_id === expectedSourceInstanceId)
    && terminalErrorCount !== undefined
    && compileErrorCount === terminalErrorCount
    && barrier.reload_started === false
    && barrier.post_reload_observed === false
    && finishedAt !== undefined
    && terminalAt !== undefined
    && terminalAt >= finishedAt;
}

function sameUnityProcessGeneration(source: HeartbeatRecord, replacement: HeartbeatRecord): boolean {
  if (source.unity_pid !== replacement.unity_pid) return false;
  const sourceStartedAt = source.unity_process_start_utc;
  const replacementStartedAt = replacement.unity_process_start_utc;
  return typeof sourceStartedAt === "string"
    && sourceStartedAt.length > 0
    && typeof replacementStartedAt === "string"
    && replacementStartedAt.length > 0
    && sourceStartedAt === replacementStartedAt;
}

function lifecycleVerificationDescriptorMatches(
  invocation: InvocationContextSnapshot,
  verificationRecord: HeartbeatRecord,
  describedCommandsHash: string,
  descriptor: CommandDescriptor | null,
  expectedCommand: string,
): descriptor is CommandDescriptor {
  if (!descriptor
    || describedCommandsHash !== verificationRecord.commands_hash
    || descriptor.id !== expectedCommand
    || descriptor.risk !== "read") return false;
  if (describedCommandsHash === invocation.commandsHash) return true;

  const sourceRecord = invocation.record;
  const packageVersion = invocation.descriptor.package_version;
  return sourceRecord !== undefined
    && sameUnityProcessGeneration(sourceRecord, verificationRecord)
    && isTrustedBuiltInDescriptor(invocation.descriptor)
    && isTrustedBuiltInDescriptor(descriptor)
    && typeof packageVersion === "string"
    && packageVersion.length > 0
    && sourceRecord.package_version === packageVersion
    && verificationRecord.package_version === packageVersion
    && descriptor.package_version === packageVersion;
}

function compileEvidenceRecord(result: GatewayResult): Record<string, unknown> | undefined {
  const value = result.status === "ok" ? result.data : result.error.details;
  if (!isObjectRecord(value)) return undefined;
  return result.command === VALIDATION_AFTER_EDIT_COMMAND && isObjectRecord(value.compile_status) ? value.compile_status : value;
}

function isVerifiedCompileBarrier(value: unknown, expectedBarrierOperationId?: string): boolean {
  if (!isObjectRecord(value)) return false;
  const requestedAt = safeBarrierTimestamp(value.requested_utc);
  const requestIssuedAt = safeBarrierTimestamp(value.request_issued_utc);
  const startedAt = safeBarrierTimestamp(value.started_utc);
  const finishedAt = safeBarrierTimestamp(value.finished_utc);
  const observedGeneration = boundedEvidenceInteger(value.observed_generation, 1, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const requestGeneration = boundedEvidenceInteger(value.request_generation, 1, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const startedGeneration = boundedEvidenceInteger(value.started_generation, 1, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const finishedGeneration = boundedEvidenceInteger(value.finished_generation, 1, MAX_SAFE_COMPILE_EVIDENCE_INTEGER);
  const terminalAt = safeBarrierTimestamp(value.terminal_compile_observed_utc);
  const terminalErrorCount = boundedEvidenceInteger(value.terminal_compile_error_count, 0, 100);
  return value.state === "verified"
    && value.required === true
    && value.requested === true
    && value.request_issued === true
    && value.started === true
    && value.finished === true
    && requestedAt !== undefined
    && requestIssuedAt !== undefined
    && startedAt !== undefined
    && finishedAt !== undefined
    && terminalAt !== undefined
    && requestedAt <= requestIssuedAt
    && requestIssuedAt <= startedAt
    && startedAt <= finishedAt
    && finishedAt <= terminalAt
    && terminalAt - finishedAt <= MAX_COMPILE_RELOAD_EDGE_MS
    && observedGeneration !== undefined
    && observedGeneration === requestGeneration
    && observedGeneration === startedGeneration
    && observedGeneration === finishedGeneration
    && typeof value.source_instance_id === "string"
    && isSafeCompileInstanceId(value.source_instance_id)
    && (value.terminal_compile_result === "clean" || value.terminal_compile_result === "failed")
    && terminalErrorCount !== undefined
    && ((value.terminal_compile_result === "clean" && terminalErrorCount === 0)
      || (value.terminal_compile_result === "failed" && terminalErrorCount > 0))
    && (expectedBarrierOperationId === undefined || value.operation_id === expectedBarrierOperationId);
}

function safeBarrierTimestamp(value: unknown): number | undefined {
  if (typeof value !== "string" || value.length === 0 || value.length > 40) return undefined;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function isSafeCompileInstanceId(value: string): boolean {
  return value.length > 0 && value.length <= 256 && /^[\p{L}\p{N}._-]+$/u.test(value);
}

function isObjectRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function mergeRecoveryData(data: unknown, recovery: Record<string, unknown>): Record<string, unknown> {
  if (data && typeof data === "object" && !Array.isArray(data)) {
    return { ...(data as Record<string, unknown>), ...recovery };
  }

  return { verification_result: data, ...recovery };
}

function isSavePromptRescueCommand(commandId: string): boolean {
  return SAVE_PROMPT_COMMANDS.has(commandId) || RECOVERY_PROMPT_RESCUE_COMMANDS.has(commandId);
}

function allowsStaleProjectResolution(commandId: string): boolean {
  return isSavePromptRescueCommand(commandId) || commandId === EDITOR_MENU_OPERATION_STATUS_COMMAND;
}

function isSavePromptDescribeSelector(selector: string): boolean {
  if (selector.startsWith("command:")) {
    return isSavePromptRescueCommand(selector.slice("command:".length));
  }

  return isSavePromptRescueCommand(selector);
}

function bridgeRecoveryRetryConfig(commandId: string, args: Record<string, unknown>): { timeoutMs: number; pollIntervalMs: number } {
  if (commandId !== COMPILE_WAIT_READY_COMMAND) {
    return { timeoutMs: DEFAULT_READ_RECOVERY_TIMEOUT_MS, pollIntervalMs: DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS };
  }

  return {
    timeoutMs: integerArg(args.timeout_ms, DEFAULT_COMPILE_WAIT_READY_TIMEOUT_MS, 1, 300_000),
    pollIntervalMs: integerArg(args.poll_interval_ms, DEFAULT_COMPILE_WAIT_READY_POLL_INTERVAL_MS, 50, 5_000),
  };
}

function compileWaitReadyInitialBridgeDeadline(configuredDeadlineUnixMs: number, externalDeadlineUnixMs?: number): number {
  return externalDeadlineUnixMs === undefined ? configuredDeadlineUnixMs : earlierDeadline(configuredDeadlineUnixMs, externalDeadlineUnixMs);
}

function compileWaitReadyTransportPlan(args: Record<string, unknown>, deadlineUnixMs: number, currentTimeMs: number): { args: Record<string, unknown>; effectiveUnityWaitMs: number } | undefined {
  const requestedTimeoutMs = integerArg(args.timeout_ms, DEFAULT_COMPILE_WAIT_READY_TIMEOUT_MS, 1, 300_000);
  const deliveryReserveMs = Math.min(COMPILE_WAIT_READY_DELIVERY_RESERVE_MS, Math.max(0, requestedTimeoutMs - 1));
  const remainingCallerBudgetMs = Math.max(0, deadlineUnixMs - currentTimeMs);
  const effectiveUnityWaitMs = Math.min(requestedTimeoutMs - deliveryReserveMs, Math.max(0, remainingCallerBudgetMs - deliveryReserveMs));
  if (effectiveUnityWaitMs < 1) return undefined;
  return {
    args: {
      ...args,
      timeout_ms: effectiveUnityWaitMs,
    },
    effectiveUnityWaitMs,
  };
}

function compileWaitReadyDeliveryBudgetExhausted(invocation: InvocationContextSnapshot, requestId?: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: invocation.identity.command,
    error: {
      code: "bridge_timeout",
      message: "compile.wait_ready caller budget no longer has room for both a Unity wait and response delivery reserve",
      details: {
        command_dispatched: false,
        write_command_repeated: false,
        write_replay_allowed: false,
      },
    },
  });
}

function withCompileWaitReadyDeliveryContract(result: GatewayResult, args: Record<string, unknown>, actualEffectiveUnityWaitMs?: number): GatewayResult {
  const existingContract = result.status === "ok"
    ? typeof result.data === "object" && result.data !== null && "compile_wait_delivery" in result.data
    : typeof result.error.details === "object" && result.error.details !== null && "compile_wait_delivery" in result.error.details;
  if (existingContract) return result;
  const suppliedTimeout = args.timeout_ms;
  if (suppliedTimeout !== undefined && (typeof suppliedTimeout !== "number" || !Number.isInteger(suppliedTimeout) || suppliedTimeout < 1 || suppliedTimeout > 300_000)) return result;
  const requestedTimeoutMs = suppliedTimeout === undefined ? DEFAULT_COMPILE_WAIT_READY_TIMEOUT_MS : suppliedTimeout;
  const deliveryReserveMs = Math.min(COMPILE_WAIT_READY_DELIVERY_RESERVE_MS, Math.max(0, requestedTimeoutMs - 1));
  const effectiveUnityWaitMs = actualEffectiveUnityWaitMs ?? requestedTimeoutMs - deliveryReserveMs;
  const contract = {
    schema_version: 1,
    requested_timeout_ms: requestedTimeoutMs,
    effective_unity_wait_ms: effectiveUnityWaitMs,
    response_delivery_reserve_ms: deliveryReserveMs,
    pre_dispatch_budget_consumed_ms: Math.max(0, requestedTimeoutMs - deliveryReserveMs - effectiveUnityWaitMs),
    caller_budget_ms: requestedTimeoutMs,
    maximum_supported_caller_budget_ms: 300_000,
    deadline_scope: "unity_wait_and_response_delivery",
  };
  if (result.status === "ok") {
    return { ...result, data: mergeRecoveryData(result.data, { compile_wait_delivery: contract }) };
  }
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...result.error.details,
        compile_wait_delivery: contract,
      },
    },
  };
}

type EmptyBodyReadRecoveryStatus = "ready" | "error" | "timeout";

type EmptyBodyReadRecoveryEvidence = {
  attempted: true;
  attempt_count: number;
  replacement_request_count: number;
  selector_strategy: "failed_heartbeat_canonical_project_identity";
  canonical_project_root_hash: string | null;
  failed_instance: Record<string, unknown>;
  replacement_instance?: Record<string, unknown>;
  original_error_retained: true;
  original_error: Record<string, unknown>;
  terminal_status: EmptyBodyReadRecoveryStatus;
};

async function recoverReadInvocationOnReplacement(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  commandArgs: Record<string, unknown>,
  transportCommandArgs: Record<string, unknown>,
  originalResult: Extract<GatewayResult, { status: "error" }>,
  deadlineUnixMs: number,
  queueOwned: boolean,
  compileWaitDelivery?: { effectiveUnityWaitMs: number },
): Promise<GatewayResult> {
  const failedRecord = invocation.record;
  const command = invocation.descriptor;
  if (!failedRecord || command.risk !== "read" || !isReadRecoveryCommand(invocation.identity.command) || !context.registry) {
    return originalResult;
  }
  const recoveryRecord = failedRecord;

  let attemptCount = 0;
  let replacementRequestCount = 0;
  let replacementRecord: HeartbeatRecord | undefined;
  const retry = bridgeRecoveryRetryConfig(invocation.identity.command, commandArgs);
  const maxAttempts = Math.ceil(retry.timeoutMs / retry.pollIntervalMs) + 1;

  while (attemptCount < maxAttempts) {
    if (nowMs(context) >= deadlineUnixMs) {
      return completeEmptyBodyReadRecovery(context, originalResult, originalResult, recoveryEvidence("timeout"));
    }

    attemptCount += 1;
    const externalSceneBlocker = externalSceneReloadBlockerResult(context, [recoveryRecord], {
      project: recoveryRecord.project,
      projectSelector: invocation.identity.projectSelector,
      command: invocation.identity.command,
      requestId,
      writeRequestIssued: false,
    });
    if (externalSceneBlocker) return externalSceneBlocker;
    const replacement = resolveReplacementByCanonicalIdentity(context.registry, recoveryRecord);
    if (replacement.status === "ambiguous") {
      const result = fail({
        ...optionalRequestId(requestId),
        project: originalResult.project ?? recoveryRecord.project,
        command: invocation.identity.command,
        error: {
          code: "duplicate_project_id",
          message: "Multiple live replacement instances match the failed bridge project identity",
        },
      });
      return completeEmptyBodyReadRecovery(context, originalResult, result, recoveryEvidence("error"));
    }

    if (replacement.status === "ready") {
      replacementRecord = replacement.record;
      if (replacementRecord.commands_hash !== invocation.commandsHash) {
        const result = fail({
          ...optionalRequestId(requestId),
          project: replacementRecord.project,
          command: invocation.identity.command,
          error: {
            code: "catalog_stale",
            message: "Replacement instance command catalog changed during read recovery",
          },
        });
        return completeEmptyBodyReadRecovery(context, originalResult, result, recoveryEvidence("error"));
      }

      const replacementInvocation = InvocationContextSnapshot.create({
        projectSelector: replacementRecord.instance_id,
        project: replacementRecord.project,
        command: invocation.identity.command,
        descriptor: command,
        commandsHash: replacementRecord.commands_hash,
        record: replacementRecord,
      });
      const compileWaitPlan = invocation.identity.command === COMPILE_WAIT_READY_COMMAND
        ? compileWaitReadyTransportPlan(transportCommandArgs, deadlineUnixMs, nowMs(context))
        : undefined;
      if (invocation.identity.command === COMPILE_WAIT_READY_COMMAND && !compileWaitPlan) {
        return completeEmptyBodyReadRecovery(context, originalResult, originalResult, recoveryEvidence("timeout"));
      }
      if (compileWaitPlan && compileWaitDelivery) compileWaitDelivery.effectiveUnityWaitMs = compileWaitPlan.effectiveUnityWaitMs;
      replacementRequestCount += 1;
      const result = await invokePreparedCommand(context, replacementInvocation, requestId, compileWaitPlan?.args ?? transportCommandArgs, deadlineUnixMs, queueOwned);
      if (result.status === "ok") {
        return completeEmptyBodyReadRecovery(context, originalResult, result, recoveryEvidence("ready"));
      }
      if (!isCompleteEmptyInvokeBridgeProtocolError(result)) {
        const status: EmptyBodyReadRecoveryStatus = result.error.code === "bridge_timeout" || result.error.code === "command_timeout" ? "timeout" : "error";
        return completeEmptyBodyReadRecovery(context, originalResult, result, recoveryEvidence(status));
      }
    }

    const remainingMs = deadlineUnixMs - nowMs(context);
    if (remainingMs <= 0) {
      return completeEmptyBodyReadRecovery(context, originalResult, originalResult, recoveryEvidence("timeout"));
    }
    await sleepMs(context, Math.min(retry.pollIntervalMs, remainingMs));
  }

  return completeEmptyBodyReadRecovery(context, originalResult, originalResult, recoveryEvidence("timeout"));

  function recoveryEvidence(terminalStatus: EmptyBodyReadRecoveryStatus): EmptyBodyReadRecoveryEvidence {
    return {
      attempted: true,
      attempt_count: attemptCount,
      replacement_request_count: replacementRequestCount,
      selector_strategy: "failed_heartbeat_canonical_project_identity",
      canonical_project_root_hash: canonicalProjectRootHash(recoveryRecord.canonical_project_root),
      failed_instance: safeRecoveryInstance(recoveryRecord, context),
      ...(replacementRecord ? { replacement_instance: safeRecoveryInstance(replacementRecord, context) } : {}),
      original_error_retained: true,
      original_error: safeOriginalEmptyBodyError(originalResult.error),
      terminal_status: terminalStatus,
    };
  }
}

function resolveReplacementByCanonicalIdentity(registry: ProjectRegistry, failedRecord: HeartbeatRecord): { status: "missing" | "ambiguous" } | { status: "ready"; record: HeartbeatRecord } {
  const resolution = registry.resolveReplacementProject(failedRecord.canonical_project_root, failedRecord.instance_id);
  if (resolution.status === "error") {
    return { status: resolution.code === "duplicate_project_id" ? "ambiguous" : "missing" };
  }
  if (isTransientBridgeLifecycleStatus(resolution.record.status) || resolution.record.bridge_port <= 0) {
    return { status: "missing" };
  }
  return { status: "ready", record: resolution.record };
}

function completeEmptyBodyReadRecovery(
  context: DispatcherRuntimeContext,
  originalResult: Extract<GatewayResult, { status: "error" }>,
  terminalResult: GatewayResult,
  evidence: EmptyBodyReadRecoveryEvidence,
): GatewayResult {
  context.auditReadRecoveryStatus = evidence.terminal_status;
  if (terminalResult.status === "ok") {
    return { ...terminalResult, data: mergeRecoveryData(terminalResult.data, { empty_body_read_recovery: evidence }) };
  }
  const retainedResult = terminalResult === originalResult ? originalResult : terminalResult;
  return {
    ...retainedResult,
    error: {
      code: retainedResult.error.code,
      message: retainedResult.error.message,
      details: {
        ...safeRecoveryErrorDetails(retainedResult.error.details),
        empty_body_read_recovery: evidence,
      },
    },
  };
}

function safeRecoveryErrorDetails(details: Record<string, unknown> | undefined): Record<string, unknown> {
  if (!details) return {};
  const safeKeys = [
    "endpoint",
    "http_status",
    "failure_kind",
    "http_error_kind",
    "response_content_type",
    "response_content_length",
    "response_body_bytes",
    "response_body_empty",
    "response_body_truncated",
    "response_body_sha256",
    "parse_error_kind",
    "abort_source",
    "protocol_schema",
    "protocol_reason",
    "expected_protocol_version",
    "received_protocol_version",
  ] as const;
  return Object.fromEntries(safeKeys.flatMap((key) => details[key] === undefined ? [] : [[key, details[key]]]));
}

function safeRecoveryInstance(record: HeartbeatRecord, context: DispatcherRuntimeContext = {}): Record<string, unknown> {
  return {
    instance_id: record.instance_id,
    bridge_port: record.bridge_port,
    heartbeat_age_ms: heartbeatAgeMsFor(record, context),
  };
}

function safeOriginalEmptyBodyError(error: GatewayError): Record<string, unknown> {
  const details = error.details ?? {};
  return {
    code: error.code,
    classification: safeRecoveryErrorDetails(details),
  };
}

function isDispatchedCompileRefreshBridgeUnreachable(result: GatewayResult): result is Extract<GatewayResult, { status: "error" }> {
  return result.status === "error"
    && result.error.code === "bridge_unreachable"
    && result.error.details?.command_bridge_request_issued === true;
}

function isCompleteEmptyInvokeBridgeProtocolError(result: GatewayResult): boolean {
  return result.status === "error"
    && result.error.code === "bridge_protocol_error"
    && isCompleteEmptyInvokeBridgeProtocolDetails(result.error.details);
}

function isCompleteEmptyInvokeBridgeProtocolDetails(details: Record<string, unknown> | undefined): boolean {
  return details?.endpoint === "/invoke"
    && details.http_status === 200
    && details.failure_kind === "response_parse"
    && details.parse_error_kind === "empty_body"
    && details.response_body_bytes === 0
    && details.response_body_empty === true
    && details.response_body_truncated === false
    && (details.response_content_length === undefined || details.response_content_length === 0)
    && details.http_error_kind == null
    && details.response_error_code == null
    && details.response_too_large == null
    && details.abort_source == null
    && details.bridge_endpoint_available !== false
    && details.protocol_schema == null
    && details.protocol_reason == null
    && details.expected_protocol_version == null
    && details.received_protocol_version == null
    && details.protocol_issues == null
    && (details.protocol_failure == null || details.protocol_failure === "empty_invoke_response_body" || details.protocol_failure === "empty_response_body")
    && (details.cause_class == null || details.cause_class === "SyntaxError")
    && (details.response_body_sha256 == null || details.response_body_sha256 === "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855")
    && (details.response_content_type == null
      || (typeof details.response_content_type === "string"
        && (details.response_content_type === "application/json"
          || /^application\/[a-z0-9!#$&^_.+-]+\+json$/.test(details.response_content_type))));
}

function isDescribeRecoveryRetryableError(result: GatewayResult, context: DispatcherRuntimeContext): result is Extract<GatewayResult, { status: "error" }> {
  if (result.status !== "error") {
    return false;
  }
  if (result.error.code === "bridge_unreachable" || result.error.code === "bridge_timeout") {
    return true;
  }
  if (result.error.code === "unity_reloading") {
    return true;
  }
  if (result.error.code === "project_not_found") {
    return context.registry !== undefined;
  }
  return result.error.code === "project_stale";
}

function bridgeRecoveryRetryExhausted(result: Extract<GatewayResult, { status: "error" }>, attempts: number, timeoutMs: number, elapsedMs: number, context: { tool: "sgg_unity_call"; command: string } | { tool: "sgg_unity_describe"; selector: string } | { tool: "sgg_unity_logs"; kind: string }, initialResult?: Extract<GatewayResult, { status: "error" }>): GatewayResult {
  const subject = context.tool === "sgg_unity_call" ? context.command : context.tool === "sgg_unity_describe" ? `${context.tool} selector '${context.selector}'` : `${context.tool} kind '${context.kind}'`;
  const details = result.error.details ?? {};
  const initialFailure = readRecoveryInitialFailure(initialResult);
  const staleObservation = isObjectRecord(details.stale_editor_observation)
    ? { ...details.stale_editor_observation, ...(initialFailure ? { initial_bridge_failure: initialFailure } : {}) }
    : undefined;
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...details,
        ...(staleObservation ? { stale_editor_observation: staleObservation } : {}),
        diagnostics_contract: TIMEOUT_RECOVERY_DIAGNOSTICS_CONTRACT,
        diagnostics_contract_version: 1,
        recovery_context: context,
        auto_retry_attempted: true,
        auto_retry_scope: "bounded_read_status_wait_only",
        retry_attempts: attempts,
        retry_budget_ms: timeoutMs,
        retry_timeout_ms: timeoutMs,
        retry_elapsed_ms: elapsedMs,
        recovery_actions: ["call_sgg_unity_projects", "use_current_live_project_selector_or_instance_id", "call_diagnostics.unity_modal.status_when_available", "check_for_scene_save_prompt_with_editor.save_prompt.get", "close_scene_save_prompt_with_editor.save_prompt.respond_cancel_if_needed", "inspect_unity_editor_for_modal_dialogs_or_freeze", "retry_after_short_delay", "restart_unity_bridge_if_heartbeat_does_not_recover"],
        recovery_hint: `Call sgg_unity_projects to verify the Unity bridge heartbeat, check for the Unity 'Scene(s) Have Been Modified' save prompt with editor.save_prompt.get and close it with editor.save_prompt.respond action=cancel if needed, inspect Unity for other modal dialogs or a main-thread freeze, then retry ${subject} after a short delay.`,
        limitation: "The Gateway cannot guarantee responsiveness if Unity is blocked by the 'Scene(s) Have Been Modified' save prompt, another modal dialog, or a main-thread freeze.",
      },
    },
  };
}

function readRecoveryInitialFailure(result: Extract<GatewayResult, { status: "error" }> | undefined): Record<string, unknown> | undefined {
  if (!result) return undefined;
  const details = result.error.details ?? {};
  const normalizedFrom = isObjectRecord(details.normalized_from_error) ? details.normalized_from_error : undefined;
  const normalizedCode = typeof normalizedFrom?.code === "string" && isErrorCode(normalizedFrom.code) ? normalizedFrom.code : undefined;
  return {
    code: normalizedCode ?? result.error.code,
    ...(normalizedCode ? { lifecycle_normalized_to_code: result.error.code } : {}),
    command_bridge_request_issued: details.command_bridge_request_issued === true,
    bridge_request_issued: details.bridge_request_issued === true,
    bridge_endpoint_available: typeof details.bridge_endpoint_available === "boolean" ? details.bridge_endpoint_available : null,
    failure_kind: typeof details.failure_kind === "string" ? details.failure_kind : null,
    endpoint: typeof details.endpoint === "string" && /^\/[a-z]+$/.test(details.endpoint) ? details.endpoint : null,
  };
}

function withTerminalStaleEditorObservation(result: Extract<GatewayResult, { status: "error" }>, context: DispatcherRuntimeContext, project: string, command: string, initialResult: Extract<GatewayResult, { status: "error" }>): Extract<GatewayResult, { status: "error" }> {
  if (result.error.code !== "project_stale" && result.error.code !== "bridge_timeout" && result.error.code !== "bridge_unreachable" && result.error.code !== "unity_reloading") return result;
  const staleResolution = context.registry?.resolveProject(project);
  if (!staleResolution || staleResolution.status !== "error" || staleResolution.code !== "project_stale"
    || !staleObservationAllowed(context, staleResolution.matches, project, command)) return result;
  const details = result.error.details ?? {};
  const existing = isObjectRecord(details.stale_editor_observation) ? details.stale_editor_observation : undefined;
  const observed = existing ?? staleEditorObservationDetails(staleResolution.matches, currentCandidateInstances(staleResolution.matches, context.registry, projectRegistrySnapshotForResolution(staleResolution)), context);
  if (!observed) return result;
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...details,
        stale_editor_observation: {
          ...observed,
          initial_bridge_failure: readRecoveryInitialFailure(initialResult),
        },
      },
    },
  };
}

function integerArg(value: unknown, defaultValue: number, min: number, max: number): number {
  if (typeof value !== "number" || !Number.isFinite(value)) {
    return defaultValue;
  }
  return Math.min(max, Math.max(min, Math.trunc(value)));
}

function nowMs(context: DispatcherRuntimeContext): number {
  return context.now?.().getTime() ?? Date.now();
}

function sleepMs(context: DispatcherRuntimeContext, ms: number): Promise<void> {
  if (ms <= 0) {
    return Promise.resolve();
  }
  return context.sleepMs ? context.sleepMs(ms) : new Promise((resolve) => setTimeout(resolve, ms));
}

function checkStaleCleanupApproval(context: DispatcherRuntimeContext, args: StaleRegistryCleanupArgs): PermissionCheck {
  const approvalArgs = Object.fromEntries(Object.entries(args).filter(([, value]) => value !== undefined));
  const approvalArgsHash = argsHash(approvalArgs);
  const approvalNow = context.now?.() ?? new Date();
  context.auditCommandMetadata = {
    risk: STALE_CLEANUP_APPROVAL_DESCRIPTOR.risk,
    idempotency: STALE_CLEANUP_APPROVAL_DESCRIPTOR.idempotency,
    timeout_ms: STALE_CLEANUP_APPROVAL_DESCRIPTOR.timeout_ms,
    source: STALE_CLEANUP_APPROVAL_DESCRIPTOR.source,
    package_id: STALE_CLEANUP_APPROVAL_DESCRIPTOR.package_id,
    trust: STALE_CLEANUP_APPROVAL_DESCRIPTOR.trust,
    command_revision: commandDescriptorRevision(STALE_CLEANUP_APPROVAL_DESCRIPTOR),
  };
  context.auditPolicyMetadata = STALE_CLEANUP_APPROVAL_POLICY;
  if (!context.acceptedPending
    && context.approvalAutoAllowStaleCleanupWithoutElicitation === true
    && context.nativeMcpFormElicitationAvailable !== true) {
    context.auditPolicyMetadata = {
      ...STALE_CLEANUP_APPROVAL_POLICY,
      approval_status: "auto_allowed",
      auto_allow_stale_registry_cleanup_without_elicitation: true,
    };
    return { status: "ok", approvalMode: "temporary_auto_allow_without_native_elicitation" };
  }
  if (!context.approvalStore || !context.approvalBindingService) {
    context.auditPolicyMetadata = { ...STALE_CLEANUP_APPROVAL_POLICY, approval_status: "unavailable" };
    return { status: "error", result: approvalUnavailable(STALE_CLEANUP_APPROVAL_PROJECT, STALE_CLEANUP_APPROVAL_COMMAND) };
  }

  try {
    const envelope = createApprovalBindingEnvelope({
      tool: "sgg_unity_stale_registry_cleanup",
      project_selector: STALE_CLEANUP_APPROVAL_PROJECT,
      project_id: null,
      canonical_project_root: null,
      instance_id: null,
      command: STALE_CLEANUP_APPROVAL_COMMAND,
      args: approvalArgs,
      mode: "wait",
      request_id: null,
      commands_hash: null,
      risk: STALE_CLEANUP_APPROVAL_DESCRIPTOR.risk,
      idempotency: STALE_CLEANUP_APPROVAL_DESCRIPTOR.idempotency,
      descriptor: STALE_CLEANUP_APPROVAL_DESCRIPTOR,
      policy_action: STALE_CLEANUP_APPROVAL_POLICY.action,
      policy_reason: STALE_CLEANUP_APPROVAL_POLICY.reason,
      policy_pattern: STALE_CLEANUP_APPROVAL_POLICY.pattern,
    });
    const metadata: ApprovalSafeMetadata = {
      project_id: null,
      canonical_project_root_hash: null,
      instance_id: null,
      command: STALE_CLEANUP_APPROVAL_COMMAND,
      command_revision: commandDescriptorRevision(STALE_CLEANUP_APPROVAL_DESCRIPTOR),
      commands_hash: null,
      risk: STALE_CLEANUP_APPROVAL_DESCRIPTOR.risk,
      idempotency: STALE_CLEANUP_APPROVAL_DESCRIPTOR.idempotency,
      mode: "wait",
      request_id: null,
      args_hash: approvalArgsHash,
      effective_policy_revision: effectivePolicyRevision(STALE_CLEANUP_APPROVAL_POLICY),
      policy_action: STALE_CLEANUP_APPROVAL_POLICY.action,
      policy_reason: STALE_CLEANUP_APPROVAL_POLICY.reason,
      policy_pattern: STALE_CLEANUP_APPROVAL_POLICY.pattern,
    };
    if (context.acceptedPending) {
      const accepted = context.acceptedPending;
      accepted.handled = true;
      const claimed = context.approvalStore.claimPendingGeneration(accepted.generation, { metadata, envelope, bindingService: context.approvalBindingService }, approvalNow);
      if (claimed.status === "claimed") {
        context.auditPolicyMetadata = { ...STALE_CLEANUP_APPROVAL_POLICY, approval_status: "consumed" };
        return { status: "ok" };
      }
      if (claimed.status === "not_found") return { status: "error", result: accepted.original_confirmation };
      const pending = context.approvalStore.request(metadata, envelope, context.approvalBindingService, { ttl_ms: context.approvalDefaultTtlMs ?? 300_000, now: approvalNow });
      context.auditPolicyMetadata = { ...STALE_CLEANUP_APPROVAL_POLICY, approval_status: "requested" };
      return {
        status: "error",
        result: confirmationRequired(STALE_CLEANUP_APPROVAL_PROJECT, STALE_CLEANUP_APPROVAL_COMMAND, undefined, "destructive", approvalArgsHash, pending.expires_at, pending, "accepted_request_context_changed", "gateway_operation"),
      };
    }
    const consumed = context.approvalStore.consume(metadata, envelope, context.approvalBindingService, approvalNow);
    if (consumed.status === "consumed") {
      context.auditPolicyMetadata = { ...STALE_CLEANUP_APPROVAL_POLICY, approval_status: "consumed" };
      return { status: "ok" };
    }
    const pending = context.approvalStore.request(metadata, envelope, context.approvalBindingService, { ttl_ms: context.approvalDefaultTtlMs ?? 300_000, now: approvalNow });
    context.auditPolicyMetadata = { ...STALE_CLEANUP_APPROVAL_POLICY, approval_status: consumed.status === "expired" ? "expired" : "requested" };
    return {
      status: "error",
      result: confirmationRequired(STALE_CLEANUP_APPROVAL_PROJECT, STALE_CLEANUP_APPROVAL_COMMAND, undefined, "destructive", approvalArgsHash, pending.expires_at, pending, undefined, "gateway_operation"),
    };
  } catch {
    context.auditPolicyMetadata = { ...STALE_CLEANUP_APPROVAL_POLICY, approval_status: "unavailable" };
    return { status: "error", result: approvalUnavailable(STALE_CLEANUP_APPROVAL_PROJECT, STALE_CLEANUP_APPROVAL_COMMAND) };
  }
}

async function withInternalDeadline<T>(context: DispatcherRuntimeContext, deadlineUnixMs: number | undefined, callback: () => Promise<T>): Promise<T> {
  if (deadlineUnixMs === undefined) return callback();
  const previous = context.internalDeadlineUnixMs;
  context.internalDeadlineUnixMs = deadlineUnixMs;
  try {
    return await callback();
  } finally {
    restoreOptionalContextField(context, "internalDeadlineUnixMs", previous);
  }
}

function checkInvocationPermission(
  context: DispatcherRuntimeContext,
  invocation: InvocationContextSnapshot,
  mode: "wait" | "job",
  requestId: string | undefined,
  commandArgs: Record<string, unknown>,
): PermissionCheck {
  refreshKnownUnityPopupApproval(context);
  const { projectSelector, project, command: commandId } = invocation.identity;
  const { commandsHash, record, descriptor: command } = invocation;
  const policyDecision = context.auditPolicyMetadata;
  const requiresHumanApproval = commandId === EXTERNAL_SCENE_RELOAD_RESPOND_COMMAND
    || commandId === PROJECT_DOWNGRADE_CONTINUE_COMMAND
    || (commandId === SCOPED_REGISTRY_IMPORT_CLOSE_COMMAND && commandArgs.dry_run === false)
    || (commandId === SAFE_MODE_ENTER_COMMAND && commandArgs.dry_run === false)
    || (commandId === SCENE_BACKUP_KEEP_COMMAND && commandArgs.dry_run === false)
    || (commandId === BUILD_RUN_COMMAND && commandArgs.dry_run !== true && typeof commandArgs.output_root === "string" && commandArgs.output_root.trim().length > 0);
  if (!policyDecision) {
    return { status: "ok" };
  }
  if (policyDecision.action === "deny") {
    return { status: "error", result: permissionDenied(project, commandId, requestId, policyDecision) };
  }
  if (!context.acceptedPending
    && context.approvalAutoAllowKnownUnityPopups === true
    && isKnownUnityPopupResponse(command)) {
    context.approvedKnownPopupMode = mode;
    context.auditPolicyMetadata = { ...policyDecision, approval_status: "auto_allowed", auto_allow_known_unity_popups: true };
    return { status: "ok" };
  }
  if ((commandId === SCOPED_REGISTRY_IMPORT_CLOSE_COMMAND || commandId === SAFE_MODE_ENTER_COMMAND || commandId === SCENE_BACKUP_KEEP_COMMAND) && commandArgs.dry_run !== false && !context.acceptedPending) {
    return { status: "ok" };
  }
  if (!context.acceptedPending && policyDecision.action === "allow" && !requiresHumanApproval) {
    return { status: "ok" };
  }
  if (!context.acceptedPending && context.approvalAutoAllowAsk === true && !requiresHumanApproval) {
    context.auditPolicyMetadata = { ...policyDecision, approval_status: "auto_allowed", auto_allow_ask: true };
    return { status: "ok" };
  }

  const commandArgsHash = argsHash(commandArgs);
  const approvalNow = context.now?.() ?? new Date();
  if (!context.approvalStore || !context.approvalBindingService) {
    context.auditPolicyMetadata = { ...policyDecision, approval_status: "unavailable" };
    return { status: "error", result: approvalUnavailable(project, commandId, requestId) };
  }
  try {
    const envelope = createApprovalBindingEnvelope({
      project_selector: projectSelector,
      project_id: record ? project : null,
      canonical_project_root: record?.canonical_project_root ?? null,
      instance_id: record?.instance_id ?? null,
      command: commandId,
      args: commandArgs,
      mode,
      request_id: requestId ?? null,
      commands_hash: commandsHash,
      risk: command.risk,
      idempotency: command.idempotency,
      descriptor: command,
      policy_action: policyDecision.action,
      policy_reason: policyDecision.reason,
      policy_pattern: policyDecision.pattern ?? null,
    });
    const metadata: ApprovalSafeMetadata = {
      project_id: record ? project : null,
      canonical_project_root_hash: canonicalProjectRootHash(record?.canonical_project_root ?? null),
      instance_id: record?.instance_id ?? null,
      command: commandId,
      command_revision: commandDescriptorRevision(command),
      commands_hash: commandsHash,
      risk: command.risk,
      idempotency: command.idempotency,
      mode,
      request_id: requestId ?? null,
      args_hash: commandArgsHash,
      effective_policy_revision: effectivePolicyRevision(policyDecision),
      policy_action: policyDecision.action,
      policy_reason: policyDecision.reason,
      policy_pattern: policyDecision.pattern ?? null,
    };
    if (context.acceptedPending) {
      const accepted = context.acceptedPending;
      accepted.handled = true;
      const claimed = context.approvalStore.claimPendingGeneration(accepted.generation, { metadata, envelope, bindingService: context.approvalBindingService }, approvalNow);
      if (claimed.status === "not_found") {
        return { status: "error", result: accepted.original_confirmation };
      }
      if (claimed.status === "claimed") {
        context.auditPolicyMetadata = { ...policyDecision, approval_status: "consumed" };
        return { status: "ok" };
      }
      if (policyDecision.action === "allow" && !requiresHumanApproval) {
        return { status: "ok" };
      }
      const pending = context.approvalStore.request(metadata, envelope, context.approvalBindingService, { ttl_ms: context.approvalDefaultTtlMs ?? 300_000, now: approvalNow });
      context.auditPolicyMetadata = { ...policyDecision, approval_status: "requested" };
      return { status: "error", result: confirmationRequired(project, commandId, requestId, command.risk, commandArgsHash, pending.expires_at, pending, "accepted_request_context_changed", "unity_command", requiresHumanApproval) };
    }
    const consumed = context.approvalStore.consume(metadata, envelope, context.approvalBindingService, approvalNow);
    if (consumed.status === "consumed") {
      context.auditPolicyMetadata = { ...policyDecision, approval_status: "consumed" };
      return { status: "ok" };
    }

    const pending = context.approvalStore.request(metadata, envelope, context.approvalBindingService, { ttl_ms: context.approvalDefaultTtlMs ?? 300_000, now: approvalNow });
    context.auditPolicyMetadata = { ...policyDecision, approval_status: consumed.status === "expired" ? "expired" : "requested" };
    return { status: "error", result: confirmationRequired(project, commandId, requestId, command.risk, commandArgsHash, pending.expires_at, pending, undefined, "unity_command", requiresHumanApproval) };
  } catch {
    context.auditPolicyMetadata = { ...policyDecision, approval_status: "unavailable" };
    return { status: "error", result: approvalUnavailable(project, commandId, requestId) };
  }
}

function revalidateKnownPopupAutoApproval(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined, commandArgs: Record<string, unknown>): GatewayResult | undefined {
  if (!isKnownUnityPopupResponse(invocation.descriptor)) return undefined;
  const root = commandArgs.canonical_project_root ?? invocation.record?.canonical_project_root ?? invocation.identity.projectSelector;
  const currentIdentity = noHeartbeatProjectDowngradeIdentity(context, invocation.identity.project, root, true);
  if (currentIdentity.status !== "ok" || currentIdentity.projectId !== invocation.identity.project) {
    context.statisticsCommandDispatched = false;
    return fail({ ...optionalRequestId(requestId), project: invocation.identity.project, command: invocation.identity.command, error: {
      code: "unsafe_binding_refused", message: "Known-popup project identity changed before response issuance",
      details: { ...preDispatchNonDeliveryDetails(), interaction_attempted: false, native_dispatch_count: 0 },
    } });
  }
  refreshManagedPolicy(context);
  const permitted = applyInvocationPolicy(context, invocation.identity.command, requestId, {
    project: invocation.identity.project, command: invocation.descriptor, commandsHash: invocation.commandsHash,
    ...(invocation.record ? { record: invocation.record } : {}),
  }, invocation.identity.projectSelector);
  const permission = permitted.state === "blocked" ? undefined : checkInvocationPermission(context, invocation, context.approvedKnownPopupMode ?? "wait", requestId, commandArgs);
  const blocked = permitted.state === "blocked" ? permitted.result : permission?.status === "error" ? permission.result : undefined;
  if (!blocked) return undefined;
  context.statisticsCommandDispatched = false;
  return blocked.status === "error" ? { ...blocked, error: { ...blocked.error, details: {
    ...blocked.error.details, interaction_attempted: false, native_dispatch_count: 0,
    command_bridge_request_issued: false, automatic_replay_attempted: false,
  } } } : blocked;
}

async function invokePreparedCommand(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined, commandArgs: Record<string, unknown>, externalDeadlineUnixMs?: number, queueOwned = false, onBridgeRequestIssued?: () => void, bridgeTimeoutMs?: number, preBridgeDispatchGuard?: () => GatewayResult | undefined, beforeBridgeRequestIssued?: () => GatewayResult | undefined, prepareBridgeRequest?: () => Promise<GatewayResult | undefined>): Promise<GatewayResult> {
  const { projectSelector, project, command: commandId } = invocation.identity;
  const { descriptor: command } = invocation;
  const outcome = await executePreparedInvocation({
    refreshPolicy: () => refreshManagedPolicy(context),
    degradedGate: () => degradedPolicyGate(context, project, commandId, requestId, command.risk),
    invokeLocal: () => invokeLocalPreparedCommand(context, invocation, requestId, commandArgs),
    official: officialToolName(commandId) !== undefined,
    invokeOfficial: () => invokeOfficialPreparedCommand(context, invocation, requestId, commandArgs),
    invokeBridge: () => {
      const blocked = preBridgeDispatchGuard?.();
      return blocked
        ? Promise.resolve(new InvocationOutcome("blocked", blocked))
        : invokeBridgePreparedCommand(context, invocation, requestId, commandArgs, externalDeadlineUnixMs, queueOwned, onBridgeRequestIssued, bridgeTimeoutMs, beforeBridgeRequestIssued, prepareBridgeRequest);
    },
  });
  if (outcome.bridgeDurationMs !== undefined) {
    context.auditBridgeDurationMs = outcome.bridgeDurationMs;
  }
  const result = withCommandSchemaRecovery(outcome.result, invocation.record ? projectSelector : undefined, invocation.record ? project : undefined, commandId, command, commandArgs);
  if (trustedTestRunBridgeResponses.has(outcome.result)) trustedTestRunBridgeResponses.add(result);
  if (trustedEditorQuitBridgeRefusals.has(outcome.result)) trustedEditorQuitBridgeRefusals.add(result);
  return result;
}

async function invokeLocalPreparedCommand(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined, commandArgs: Record<string, unknown>): Promise<GatewayResult | undefined> {
  const { project, command: commandId } = invocation.identity;
  const { record } = invocation;
  if (findDiagnosticsCommand(commandId)) {
    const sceneBackupIdentity = !record && commandId === SCENE_BACKUP_KEEP_COMMAND
      ? noHeartbeatProjectDowngradeIdentity(context, invocation.identity.projectSelector, commandArgs.canonical_project_root, true)
      : undefined;
    if (sceneBackupIdentity && (sceneBackupIdentity.status !== "ok" || sceneBackupIdentity.projectId !== project)) {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: {
        code: "unsafe_binding_refused", message: "Scene-backup project identity changed before local dispatch",
        details: preDispatchNonDeliveryDetails(),
      } });
    }
    const projectContext = record || (sceneBackupIdentity?.status === "ok" && sceneBackupIdentity.projectId === project)
      ? { project_resolved: true as const, original_project_selector: invocation.identity.projectSelector, resolved_project_id: project }
      : { project_resolved: false as const, original_project_selector: invocation.identity.projectSelector };
    const popupAutoAllowed = context.knownUnityPopupSettings !== undefined && context.auditPolicyMetadata?.auto_allow_known_unity_popups === true;
    const diagnosticsContext = popupAutoAllowed ? { ...context, beforeKnownUnityPopupResponse: () => {
      const blocked = revalidateKnownPopupAutoApproval(context, invocation, requestId, commandArgs);
      if (blocked) throw new KnownPopupPermissionChanged(blocked);
    } } : context;
    try {
      return ok({ ...optionalRequestId(requestId), project, command: commandId, data: runDiagnosticsCommand(commandId, commandArgs, diagnosticsContext, record, projectContext) });
    } catch (error) {
      if (error instanceof KnownPopupPermissionChanged) return error.result;
      throw error;
    }
  }

  if (findPlanCommand(commandId)) {
    const result = runPlanCommand(commandId, commandArgs, record, context.now?.() ?? new Date());
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  if (findPipelineCommand(commandId)) {
    const result = runPipelineCommand(commandId, commandArgs, record, context.now?.() ?? new Date());
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  if (findSkillCommand(commandId)) {
    const result = runSkillCommand(commandId, commandArgs, record, {
      ...(context.skillUnityUserSkillsRoot ? { unityUserSkillsRoot: context.skillUnityUserSkillsRoot } : {}),
      now: context.now?.() ?? new Date(),
    });
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  if (findCheckpointCommand(commandId)) {
    const result = runCheckpointCommand(commandId, commandArgs, record, context.now?.() ?? new Date());
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  if (findArtifactCommand(commandId)) {
    const result = runArtifactCommand(commandId, commandArgs, record, context.now?.() ?? new Date());
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  if (findEditorMenuOperationCommand(commandId)) {
    const result = runEditorMenuOperationCommand(commandId, commandArgs, record);
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  if (findUnityDocsCommand(commandId)) {
    const result = runUnityDocsCommand(commandId, commandArgs, record);
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  if (findPolicyCommand(commandId)) {
    const targetId = typeof commandArgs.command === "string" ? commandArgs.command : "";
    const target = findVirtualCommand(targetId) ?? (record && context.bridge ? (await resolveCommandDescriptor(context.bridge, record, targetId))?.command : undefined);
    const result = runPolicyExplain(commandArgs, context, project, target);
    if (result.status === "error") {
      return fail({ ...optionalRequestId(requestId), project, command: commandId, error: result.error });
    }
    return ok({ ...optionalRequestId(requestId), project, command: commandId, data: result.data });
  }

  return undefined;
}

async function invokeOfficialPreparedCommand(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined, commandArgs: Record<string, unknown>): Promise<InvocationOutcome> {
  const { project, command: commandId } = invocation.identity;
  if (officialToolName(commandId)) {
    if (!context.officialRelay) {
      return new InvocationOutcome("official", commandNotFound(project, commandId, requestId));
    }

    const response = await runInProjectQueue(context, project, async () => {
      markBatchBridgeRequestIssued(context, commandId, invocation.descriptor.risk);
      const relayResponse = await (context.officialRelay?.invoke(commandId, commandArgs) ?? Promise.reject(new Error("bridge_unreachable")));
      markBatchResponseDelivered(context, commandId);
      return relayResponse;
    }, false, projectQueueOwner(invocation));
    if (response.status === "error") {
      return new InvocationOutcome("official", fail({
        ...optionalRequestId(requestId),
        project,
        command: commandId,
        error: normalizeBridgeError(response.error),
      }), typeof response.duration_ms === "number" ? response.duration_ms : undefined);
    }
    return new InvocationOutcome("official", ok({ ...optionalRequestId(requestId), project, command: commandId, data: response.data }), typeof response.duration_ms === "number" ? response.duration_ms : undefined);
  }
  return new InvocationOutcome("official", commandNotFound(project, commandId, requestId));
}

async function invokeBridgePreparedCommand(context: DispatcherRuntimeContext, invocation: InvocationContextSnapshot, requestId: string | undefined, commandArgs: Record<string, unknown>, externalDeadlineUnixMs?: number, queueOwned = false, onBridgeRequestIssued?: () => void, bridgeTimeoutMs?: number, beforeBridgeRequestIssued?: () => GatewayResult | undefined, prepareBridgeRequest?: () => Promise<GatewayResult | undefined>): Promise<InvocationOutcome> {
  const { projectSelector, project, command: commandId } = invocation.identity;
  const { record, descriptor: command } = invocation;
  const popupAutoAllowed = context.knownUnityPopupSettings !== undefined && context.auditPolicyMetadata?.auto_allow_known_unity_popups === true && isKnownUnityPopupResponse(command);
  const originalBeforeRequestIssued = beforeBridgeRequestIssued;
  if (popupAutoAllowed) beforeBridgeRequestIssued = () => revalidateKnownPopupAutoApproval(context, invocation, requestId, commandArgs) ?? originalBeforeRequestIssued?.();
  if (!record) {
    return new InvocationOutcome("bridge", bridgeUnavailable(project, commandId, requestId));
  }

  let response: Awaited<ReturnType<BridgeClient["invoke"]>>;
  let externalSceneMonitor: ReturnType<typeof createExternalSceneReloadMonitor>;
  let bridgeRequestIssued = false;
  let preBridgeDispatchBlocked: GatewayResult | undefined;
  try {
    const effectiveBridgeTimeoutMs = bridgeTimeoutMs ?? command.timeout_ms;
    const deadlineUnixMs = earlierDeadline(commandDeadlineUnixMs(context, effectiveBridgeTimeoutMs), externalDeadlineUnixMs);
    const invoke = () => {
      if (popupAutoAllowed) {
        preBridgeDispatchBlocked = revalidateKnownPopupAutoApproval(context, invocation, requestId, commandArgs);
        if (preBridgeDispatchBlocked) throw new BridgeRequestNotIssuedError();
      }
      externalSceneMonitor = createExternalSceneReloadMonitor(context, record, {
        project,
        projectSelector,
        command: commandId,
        requestId,
        writeRequestIssued: commandId === COMPILE_REFRESH_AND_WAIT_COMMAND,
      });
      context.auditBridgeInvokeAttempts = (context.auditBridgeInvokeAttempts ?? 0) + 1;
      const tracksExactIssuance = onBridgeRequestIssued !== undefined || beforeBridgeRequestIssued !== undefined;
      if (!tracksExactIssuance) {
        bridgeRequestIssued = true;
        markBatchBridgeRequestIssued(context, commandId, command.risk);
      }
      return context.bridge?.invoke(record, {
        ...optionalRequestId(requestId),
        project,
        command: commandId,
        args: commandArgs,
        deadline_unix_ms: deadlineUnixMs,
        risk: command.risk,
        ...(invocation.catalogGeneration !== null && invocation.commandsHash !== null ? {
          expected_instance_id: record.instance_id,
          expected_catalog_generation: invocation.catalogGeneration,
          expected_commands_hash: invocation.commandsHash,
        } : {}),
        ...optionalBridgeSettings(context, record),
      }, {
        timeoutMs: effectiveBridgeTimeoutMs,
        deadlineUnixMs,
        ...(requestId !== undefined && safeTransportRequestId(requestId) ? { requestId } : {}),
        ...(externalSceneMonitor ? { signal: externalSceneMonitor.signal } : {}),
        ...(prepareBridgeRequest ? { prepareRequest: async () => {
          preBridgeDispatchBlocked = await prepareBridgeRequest();
          if (preBridgeDispatchBlocked) throw new BridgeRequestNotIssuedError();
        } } : {}),
        ...(beforeBridgeRequestIssued ? { beforeRequestIssued: () => {
          preBridgeDispatchBlocked = beforeBridgeRequestIssued();
          if (preBridgeDispatchBlocked) throw new BridgeRequestNotIssuedError();
        } } : {}),
        ...(tracksExactIssuance ? { onRequestIssued: () => {
          bridgeRequestIssued = true;
          markBatchBridgeRequestIssued(context, commandId, command.risk);
          onBridgeRequestIssued?.();
        } } : {}),
      }) ?? Promise.reject(new Error("bridge_unreachable"));
    };
    if (queueOwned || !context.queues || isSavePromptRescueCommand(commandId)) {
      response = await invoke();
    } else {
      let queueExpired = false;
      let resolveAdmission!: (admitted: boolean) => void;
      const admission = new Promise<boolean>((resolve) => { resolveAdmission = resolve; });
      const queueEnqueuedAtUnixMs = nowMs(context);
      if (context.batchItemTrace) context.batchItemTrace.queued = true;
      const queued = context.queues.run(project, async () => {
        if (queueExpired || deadlineUnixMs <= nowMs(context)) {
          resolveAdmission(false);
          return PROJECT_QUEUE_EXPIRED;
        }
        if (context.batchItemTrace) context.batchItemTrace.admitted = true;
        resolveAdmission(true);
        return invoke();
      }, projectQueueOwner(invocation, deadlineUnixMs));
      const admitted = await settleProjectQueueAdmissionBeforeDeadline(context, admission, deadlineUnixMs, () => { queueExpired = true; });
      if (!admitted) {
        return new InvocationOutcome("blocked", genericProjectQueueTimeout(context, invocation, requestId, effectiveBridgeTimeoutMs, queueEnqueuedAtUnixMs));
      }
      const queuedResponse = await queued;
      if (queuedResponse === PROJECT_QUEUE_EXPIRED) {
        return new InvocationOutcome("blocked", genericProjectQueueTimeout(context, invocation, requestId, effectiveBridgeTimeoutMs, queueEnqueuedAtUnixMs));
      }
      response = queuedResponse;
    }
    markBatchResponseDelivered(context, commandId);
  } catch (error) {
    if (preBridgeDispatchBlocked) return new InvocationOutcome("blocked", preBridgeDispatchBlocked);
    const externalSceneBlocker = externalSceneMonitor?.blocker();
    if (externalSceneBlocker) {
      return new InvocationOutcome("bridge", externalSceneBlocker);
    }
    const requestIssued = bridgeRequestIssuedForError(error, bridgeRequestIssued);
    if (context.batchItemTrace?.command === commandId) context.batchItemTrace.bridgeRequestIssued ||= requestIssued;
    const bridgeError = bridgeThrownError(error, {
      project,
      projectSelector,
      command: commandId,
      requestId,
      record,
      registry: context.registry,
      commandDescriptor: command,
      commandArgs,
      bridgeRequestIssued: requestIssued,
      captureDefinitelyNotScheduled: !requestIssued,
    }, context);
    if (bridgeError) {
      const evidenced = withCommandBridgeRequestEvidence(bridgeError, requestIssued);
      return new InvocationOutcome("bridge", withoutUntrustedTestRunObservationResult(commandId, evidenced));
    }
    throw error;
  } finally {
    externalSceneMonitor?.stop();
  }
  const bridgeDurationMs = typeof response.duration_ms === "number" ? response.duration_ms : undefined;
  if (response.status === "error") {
    const externalSceneBlocker = externalSceneMonitor?.blocker() ?? externalSceneReloadBlockerResult(context, [record], {
      project,
      projectSelector,
      command: commandId,
      requestId,
      writeRequestIssued: commandId === COMPILE_REFRESH_AND_WAIT_COMMAND || commandId === VALIDATION_AFTER_EDIT_COMMAND,
    });
    if (externalSceneBlocker) return new InvocationOutcome("bridge", externalSceneBlocker, bridgeDurationMs);
  }
  const compileBarrierError = directCompileRefreshBarrierError(commandId, commandArgs, response, record.instance_id);
  if (compileBarrierError) {
    return new InvocationOutcome("bridge", fail({
      ...optionalRequestId(requestId),
      project,
      command: commandId,
      error: compileBarrierError,
    }), bridgeDurationMs);
  }
  if (response.status === "error") {
    let admittedError = response.error;
    if (commandId === TEST_RUN_COMMAND || commandId === TEST_RUN_STATUS_COMMAND) {
      try {
        const details = safeTestRunErrorDetails(commandId, response.error.details);
        admittedError = { code: response.error.code, message: response.error.message, ...(details ? { details } : {}) };
      } catch {
        return new InvocationOutcome("bridge", invalidTestRunObservationPayload(project, commandId, requestId, bridgeRequestIssued), bridgeDurationMs);
      }
    }
    const recoveryFields = {
      project,
      projectSelector,
      command: commandId,
      requestId,
      record,
      registry: context.registry,
      commandDescriptor: command,
      commandArgs,
      bridgeRequestIssued,
      captureDefinitelyNotScheduled: screenshotDefinitelyNotScheduled(admittedError.details),
    };
    const normalized = normalizeBridgeError(admittedError, recoveryFields, context);
    let safeDetails: unknown;
    try { safeDetails = safeCompileProgressInPayload(commandId, safeTestRunObservationPayload(commandId, normalized.details)); }
    catch (error) {
      if (commandId !== TEST_RUN_COMMAND && commandId !== TEST_RUN_STATUS_COMMAND) throw error;
      return new InvocationOutcome("bridge", invalidTestRunObservationPayload(project, commandId, requestId, bridgeRequestIssued), bridgeDurationMs);
    }
    const normalizedError = { ...normalized, details: { ...(isObjectRecord(safeDetails) ? safeDetails : {}), command_bridge_request_issued: true } };
    const timeoutActions = editorWindowsListTimeoutNextValidActions(commandId, normalizedError.code);
    const screenshotState = screenshotCaptureInterruptionState(recoveryFields);
    const screenshotActions = screenshotState ? screenshotCaptureNextValidActions(screenshotState) : undefined;
    const failedResult = fail({
      ...optionalRequestId(requestId),
      project,
      command: commandId,
      error: normalizedError,
      ...(normalizedError.code === "bridge_protocol_error" ? { next_valid_actions: screenshotActions ?? bridgeProtocolNextValidActions(normalizedError.details, commandId === TEST_RUN_COMMAND && command.risk !== "read") } : {}),
      ...(timeoutActions ? { next_valid_actions: timeoutActions } : {}),
    });
    // Unity may emit a code outside the Gateway's canonical error enum;
    // compare the runtime value exactly, without coercing project data.
    const rawBridgeErrorCode: unknown = response.error.code;
    if (commandId === TEST_RUN_STATUS_COMMAND && command.risk === "read"
      && isTrustedBuiltInDescriptor(command) && bridgeRequestIssued
      && rawBridgeErrorCode === "test_run_not_found") trustedLiveTestRunMissingResults.add(failedResult);
    if (commandId === TEST_RUN_COMMAND && isTrustedBuiltInDescriptor(command) && bridgeRequestIssued) trustedTestRunBridgeResponses.add(failedResult);
    if (commandId === EDITOR_QUIT_COMMAND && isTrustedBuiltInDescriptor(command) && bridgeRequestIssued) trustedEditorQuitBridgeRefusals.add(failedResult);
    return new InvocationOutcome("bridge", failedResult, bridgeDurationMs);
  }
  try {
    const result = ok({ ...optionalRequestId(requestId), project, command: commandId, data: safeCompileProgressInPayload(commandId, safeTestRunObservationPayload(commandId, response.data)) });
    if (commandId === TEST_RUN_COMMAND && isTrustedBuiltInDescriptor(command) && bridgeRequestIssued) trustedTestRunBridgeResponses.add(result);
    return new InvocationOutcome("bridge", result, bridgeDurationMs);
  } catch (error) {
    if (commandId !== TEST_RUN_COMMAND && commandId !== TEST_RUN_STATUS_COMMAND) throw error;
    return new InvocationOutcome("bridge", invalidTestRunObservationPayload(project, commandId, requestId, bridgeRequestIssued), bridgeDurationMs);
  }
}

function safeTestRunObservationPayload(commandId: string, value: unknown): unknown {
  if (commandId !== TEST_RUN_COMMAND && commandId !== TEST_RUN_STATUS_COMMAND || value === undefined) return value;
  const sanitized = withoutTestRunStatusObservation(value);
  if (!isBoundedJson(sanitized)) throw new TypeError("Invalid bounded test observation payload");
  return sanitized;
}

function invalidTestRunObservationPayload(project: string | undefined, command: string, requestId: string | undefined, issued: boolean | undefined): GatewayResult {
  const result = fail({ ...optionalRequestId(requestId), ...(project ? { project } : {}), command, error: invalidTestRunObservationError(issued),
    next_valid_actions: ["inspect_test_response_contract", "do_not_repeat_test.run_automatically"] });
  invalidTestRunObservationResults.add(result);
  return result;
}

function invalidTestRunObservationError(issued: boolean | undefined): GatewayError {
  return {
    code: "bridge_protocol_error", message: "The test response was not a bounded canonical JSON payload",
    details: { ...(issued === undefined ? {} : { command_bridge_request_issued: issued }), command_replayed: false, write_command_repeated: false, write_replay_allowed: false },
  };
}

function safeTestRunErrorDetails(command: string | undefined, value: Record<string, unknown> | undefined): Record<string, unknown> | undefined {
  if (command !== TEST_RUN_COMMAND && command !== TEST_RUN_STATUS_COMMAND || value === undefined) return value;
  const sanitized = safeTestRunObservationPayload(command, value);
  if (!isObjectRecord(sanitized)) throw new TypeError("Invalid bounded test observation payload");
  return sanitized;
}

function withoutUntrustedTestRunObservationResult(commandId: string, result: GatewayResult): GatewayResult {
  if (commandId !== TEST_RUN_COMMAND && commandId !== TEST_RUN_STATUS_COMMAND || result.status !== "error") return result;
  try {
    const details = safeTestRunObservationPayload(commandId, result.error.details);
    const sanitized = { ...result, error: { ...result.error, ...(isObjectRecord(details) ? { details } : {}) } };
    if (trustedLiveTestRunMissingResults.has(result)) trustedLiveTestRunMissingResults.add(sanitized);
    if (optedInTestRunPreflightFailures.has(result)) optedInTestRunPreflightFailures.add(sanitized);
    if (invalidTestRunObservationResults.has(result)) invalidTestRunObservationResults.add(sanitized);
    return sanitized;
  } catch {
    const issued = result.error.details?.command_bridge_request_issued;
    return invalidTestRunObservationPayload(result.project, commandId, result.request_id, typeof issued === "boolean" ? issued : undefined);
  }
}

function safeCompileProgressInPayload(commandId: string, value: unknown): unknown {
  if (![COMPILE_STATUS_COMMAND, COMPILE_WAIT_READY_COMMAND, COMPILE_REFRESH_AND_WAIT_COMMAND, VALIDATION_AFTER_EDIT_COMMAND, "validation.preflight"].includes(commandId)
    || !isObjectRecord(value)) return value;
  const { background_compiler_progress: rawProgress, readiness_qualification: rawQualification, ...rest } = value;
  const progress = safeBackgroundCompilerProgress(rawProgress);
  const qualification = safeCompileReadinessQualification(rawQualification);
  const nested = isObjectRecord(rest.compile_status) ? safeCompileProgressInPayload(commandId, rest.compile_status) : undefined;
  return { ...rest, ...(progress ? { background_compiler_progress: progress } : {}), ...(qualification ? { readiness_qualification: qualification } : {}), ...(nested ? { compile_status: nested } : {}) };
}

function withSafeCompileProgressResult(commandId: string, result: GatewayResult): GatewayResult {
  if (result.status !== "error" || ![COMPILE_STATUS_COMMAND, COMPILE_WAIT_READY_COMMAND, COMPILE_REFRESH_AND_WAIT_COMMAND, VALIDATION_AFTER_EDIT_COMMAND, "validation.preflight"].includes(commandId)) return result;
  const details = safeCompileProgressInPayload(commandId, result.error.details);
  return { ...result, error: { ...result.error, ...(isObjectRecord(details) ? { details } : {}) } };
}

function directCompileRefreshBarrierError(commandId: string, commandArgs: Record<string, unknown>, response: Awaited<ReturnType<BridgeClient["invoke"]>>, sourceInstanceId: string): GatewayError | undefined {
  if (commandId !== COMPILE_REFRESH_AND_WAIT_COMMAND && commandId !== VALIDATION_AFTER_EDIT_COMMAND) return undefined;
  const expectedOperationId = barrierExpectationId(commandArgs);
  if (!expectedOperationId) {
    return compileBarrierCorrelationError(commandId, "missing_gateway_expectation", undefined);
  }

  if (response.status === "error") {
    if (response.error.code !== "compilation_failed") return undefined;
    const details = response.error.details;
    const compileDetails = commandId === VALIDATION_AFTER_EDIT_COMMAND && isObjectRecord(details) && isObjectRecord(details.compile_status) ? details.compile_status : details;
    if (isExactTerminalCompileFailureEvidence(compileDetails, expectedOperationId, sourceInstanceId)) {
      return undefined;
    }
    return compileBarrierCorrelationError(commandId, "compilation_failed_without_matching_finished_barrier", isObjectRecord(compileDetails) ? compileDetails.compile_barrier : undefined);
  }

  const data = response.data;
  const compileData = commandId === VALIDATION_AFTER_EDIT_COMMAND && isObjectRecord(data) && isObjectRecord(data.compile_status) ? data.compile_status : data;
  if (isObjectRecord(compileData) && isVerifiedCompileBarrier(compileData.compile_barrier, expectedOperationId)) return undefined;
  return compileBarrierCorrelationError(commandId, "successful_response_without_matching_finished_barrier", isObjectRecord(compileData) ? compileData.compile_barrier : undefined);
}

function compileBarrierCorrelationError(commandId: string, reason: string, barrier: unknown): GatewayError {
  const validationOperation = commandId === VALIDATION_AFTER_EDIT_COMMAND;
  return {
    code: "compile_freshness_unverified",
    message: `${commandId} did not return a matching verified explicit compilation barrier`,
    details: {
      ready: false,
      compilable: false,
      barrier_validation_reason: reason,
      delivery_state: "dispatched",
      original_command_completed: "unknown",
      command_bridge_request_issued: true,
      write_command_repeated: false,
      write_replay_allowed: false,
      command_replayed: false,
      ...(safeCompileBarrier(barrier) ? { compile_barrier: safeCompileBarrier(barrier) } : {}),
      next_valid_actions: ["compile.wait_ready", "compile.status", validationOperation ? "do_not_auto_repeat_validation.after_edit" : "do_not_auto_repeat_compile.refresh_and_wait"],
    },
  };
}

function degradedPolicyGate(context: DispatcherRuntimeContext, project: string, command: string, requestId: string | undefined, risk: CommandDescriptor["risk"], blockRead = false): GatewayResult | undefined {
  if (!context.policyManager?.degraded || risk === "read" && !blockRead) {
    return undefined;
  }
  return fail({
    ...optionalRequestId(requestId),
    project,
    command,
    error: {
      code: "policy_degraded",
      message: blockRead ? "Command blocked while the configured policy is degraded" : "Risky command blocked while the configured policy is degraded",
      details: { blocked_by: "gateway_policy_manager", command_risk: risk, policy_state: "degraded" },
    },
  });
}

function isDegradedPolicyRecoveryCommand(context: DispatcherContext, commandId: string): boolean {
  return context.policyManager?.degraded === true && DEGRADED_POLICY_RECOVERY_COMMANDS.has(commandId);
}

type BatchCorrelation = { checkpoint_id?: string; plan_id?: string };

type BatchItemTrace = {
  command: string;
  queued: boolean;
  admitted: boolean;
  bridgeRequestCount: number;
  bridgeRequestIssued: boolean;
  responseDelivered: boolean;
  unsafe: boolean;
};

type BatchExecution = {
  mode: "sequential" | "parallel_by_project";
  execution_state: "completed" | "interrupted";
  interrupted: boolean;
  results: GatewayResult[];
  interrupted_at_index?: number;
  interrupted_error_code?: ErrorCode;
  remaining_items_not_started?: number;
};

async function dispatchBatchSequential(items: ReturnType<typeof parseItemsJson>, context: DispatcherRuntimeContext, correlation: BatchCorrelation): Promise<BatchExecution & ({ interrupted: false } | { interrupted: true; interrupted_error_code: ErrorCode })> {
  const results: GatewayResult[] = [];
  const batchContext: DispatcherRuntimeContext = { ...context };
  for (const [index, item] of items.entries()) {
    const trace: BatchItemTrace = { command: item.command, queued: false, admitted: false, bridgeRequestCount: 0, bridgeRequestIssued: false, responseDelivered: false, unsafe: false };
    batchContext.batchItemTrace = trace;
    batchContext.batchItemIndex = index;
    const result = await dispatchTool("sgg_unity_call", batchItemToCallArgs(item, correlation), batchContext);
    const settled = await waitForSequentialBatchOperation(item, result, batchContext, correlation);
    const batchItem = batchItemExecution(index, settled, trace);
    results.push({ ...settled, batch_item: batchItem });
    if (batchItem.state !== "uncertain" || !trace.unsafe || settled.status !== "error") continue;

    for (let skippedIndex = index + 1; skippedIndex < items.length; skippedIndex += 1) {
      results.push(skippedBatchItem(items[skippedIndex]!, skippedIndex, index));
    }
    return {
      mode: "sequential",
      execution_state: "interrupted",
      interrupted: true,
      interrupted_at_index: index,
      interrupted_error_code: settled.error.code,
      remaining_items_not_started: items.length - index - 1,
      results,
    };
  }
  return { mode: "sequential", execution_state: "completed", interrupted: false, results };
}

async function dispatchBatchParallelByProject(items: ReturnType<typeof parseItemsJson>, context: DispatcherContext, correlation: BatchCorrelation): Promise<BatchExecution & { interrupted: false }> {
  const results = await Promise.all(items.map((item) => dispatchTool("sgg_unity_call", batchItemToCallArgs(item, correlation), context)));
  return { mode: "parallel_by_project", execution_state: "completed", interrupted: false, results };
}

function batchItemExecution(index: number, result: GatewayResult, trace: BatchItemTrace): BatchItemExecution {
  const state = trace.responseDelivered
    ? "delivered"
    : trace.bridgeRequestIssued
      ? "uncertain"
      : result.status === "ok"
        ? "completed"
        : "not_dispatched";
  return {
    index,
    state,
    queued: trace.queued,
    admitted: trace.admitted,
    bridge_request_issued: trace.bridgeRequestIssued,
    response_delivered: trace.responseDelivered,
    automatic_replay_attempted: trace.bridgeRequestCount > 1,
  };
}

function skippedBatchItem(item: ReturnType<typeof parseItemsJson>[number], index: number, interruptedAtIndex: number): GatewayResult {
  return fail({
    ...optionalRequestId(item.request_id),
    project: item.project,
    command: item.command,
    error: {
      code: "bridge_protocol_error",
      message: "Sequential batch item was not started because a prior unsafe item has uncertain delivery",
      details: {
        batch_interrupted: true,
        interruption_reason: "prior_unsafe_item_delivery_uncertain",
        interrupted_at_index: interruptedAtIndex,
        delivery_state: "not_dispatched",
        bridge_request_issued: false,
        explicit_repeat_required: false,
      },
    },
    next_valid_actions: ["resolve_prior_uncertain_delivery_before_continuing_batch"],
    batch_item: {
      index,
      state: "not_started",
      queued: false,
      admitted: false,
      bridge_request_issued: false,
      response_delivered: false,
      automatic_replay_attempted: false,
    },
  });
}

function batchItemToCallArgs(item: ReturnType<typeof parseItemsJson>[number], correlation: BatchCorrelation): Record<string, unknown> {
  return {
    project: item.project,
    command: item.command,
    args_json: item.args_json ?? JSON.stringify(item.args ?? {}),
    mode: item.mode,
    ...(item.request_id ? { request_id: item.request_id } : {}),
    ...(correlation.checkpoint_id ? { checkpoint_id: correlation.checkpoint_id } : {}),
    ...(correlation.plan_id ? { plan_id: correlation.plan_id } : {}),
  };
}

type TestRunPollOutcome = {
  operationId: string;
  result: GatewayResult;
  pollCount: number;
  elapsedMs: number;
  timeoutMs: number;
  timedOut: boolean;
};

async function waitForUndispatchedTestRunCatalog<T extends { result: GatewayResult }>(context: DispatcherContext, deadlineMs: number, execute: () => Promise<T>): Promise<T> {
  let outcome = await execute();
  while (isUndispatchedCatalogPending(outcome.result) && nowMs(context) < deadlineMs) {
    await sleepMs(context, Math.min(DEFAULT_READ_RECOVERY_POLL_INTERVAL_MS, Math.max(0, deadlineMs - nowMs(context))));
    if (nowMs(context) >= deadlineMs) break;
    outcome = await execute();
  }
  return outcome;
}

function isUndispatchedCatalogPending(result: GatewayResult): boolean {
  return result.status === "error"
    && result.error.code === "catalog_pending"
    && result.error.details?.command_dispatched === false
    && result.error.details.bridge_request_issued === false;
}

async function waitForDirectTestRunOperation(args: UnityCallArgs, commandArgs: Record<string, unknown>, result: GatewayResult, context: DispatcherContext, deadlineMs: number): Promise<GatewayResult> {
  const waitTimeoutMs = args.wait_for_test_run_terminal_ms;
  if (waitTimeoutMs === undefined) return result;
  const effectiveWaitTimeoutMs = Math.min(waitTimeoutMs, MAX_POST_ADMISSION_WAIT_MS);
  const outcome = await pollTestRunUntilTerminal({
    project: args.project,
    result,
    timeoutMs: effectiveWaitTimeoutMs,
    deadlineMs,
    responseMode: commandArgs.response_mode === "compact" ? "compact" : "full",
    context,
    correlation: {
      ...(args.checkpoint_id ? { checkpoint_id: args.checkpoint_id } : {}),
      ...(args.plan_id ? { plan_id: args.plan_id } : {}),
    },
  });
  const awaited = !outcome
    ? result
    : outcome.timedOut
      ? directTestRunWaitTimeout(args, outcome)
      : annotateDirectTestRunResult(args, outcome);
  return withPostAdmissionDeliveryContract(awaited, "test.run", waitTimeoutMs);
}

function withPostAdmissionDeliveryContract(result: GatewayResult, operation: "compile.refresh_and_wait" | "test.run", requestedWaitMs?: number): GatewayResult {
  const contract = {
    schema_version: 1,
    operation,
    caller_budget_ms: MCP_CALLER_BUDGET_MS,
    response_delivery_reserve_ms: MCP_RESPONSE_DELIVERY_RESERVE_MS,
    maximum_post_admission_wait_ms: MAX_POST_ADMISSION_WAIT_MS,
    ...(requestedWaitMs === undefined ? {} : {
      requested_wait_ms: requestedWaitMs,
      effective_wait_ms: Math.min(requestedWaitMs, MAX_POST_ADMISSION_WAIT_MS),
    }),
    deadline_scope: "bridge_admission_terminal_recovery_and_response_delivery",
    write_command_repeated: false,
    write_replay_allowed: false,
  };
  if (result.status === "ok") {
    return { ...result, data: mergeRecoveryData(result.data, { caller_delivery: contract }) };
  }
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...result.error.details,
        caller_delivery: contract,
      },
    },
  };
}

async function pollTestRunUntilTerminal(input: {
  project: string;
  result: GatewayResult;
  timeoutMs: number;
  deadlineMs?: number;
  responseMode: "full" | "compact";
  context: DispatcherContext;
  correlation: BatchCorrelation;
}): Promise<TestRunPollOutcome | undefined> {
  const operationId = operationIdFromResult(input.result);
  if (!operationId || !isRunningOperationResult(input.result)) return undefined;
  const startedAtMs = input.deadlineMs === undefined ? nowMs(input.context) : input.deadlineMs - input.timeoutMs;
  const deadlineMs = input.deadlineMs ?? startedAtMs + input.timeoutMs;
  const statusContext = testRunPollContext(input.context, deadlineMs);
  let pollCount = 0;
  let lastStatus = input.result;

  while (nowMs(input.context) < deadlineMs) {
    pollCount += 1;
    lastStatus = await dispatchTool(
      "sgg_unity_call",
      {
        project: input.project,
        command: TEST_RUN_STATUS_COMMAND,
        args_json: JSON.stringify({ operation_id: operationId, ...(input.responseMode === "compact" ? { response_mode: "compact" } : {}) }),
        mode: "wait",
        ...(input.correlation.checkpoint_id ? { checkpoint_id: input.correlation.checkpoint_id } : {}),
        ...(input.correlation.plan_id ? { plan_id: input.correlation.plan_id } : {}),
      },
      statusContext,
    );
    if (isTransientTestRunStatusRoutingError(lastStatus)) {
      await sleepMs(input.context, Math.min(BATCH_TEST_RUN_POLL_INTERVAL_MS, Math.max(0, deadlineMs - nowMs(input.context))));
      continue;
    }
    if (!isRunningOperationResult(lastStatus)) {
      return { operationId, result: lastStatus, pollCount, elapsedMs: Math.max(0, nowMs(input.context) - startedAtMs), timeoutMs: input.timeoutMs, timedOut: false };
    }
    await sleepMs(input.context, Math.min(BATCH_TEST_RUN_POLL_INTERVAL_MS, Math.max(0, deadlineMs - nowMs(input.context))));
  }

  return { operationId, result: lastStatus, pollCount, elapsedMs: Math.max(0, nowMs(input.context) - startedAtMs), timeoutMs: input.timeoutMs, timedOut: true };
}

function isTransientTestRunStatusRoutingError(result: GatewayResult): boolean {
  return result.status === "error" && (
    result.error.code === "catalog_pending"
    || result.error.code === "catalog_unavailable"
    || result.error.code === "project_not_found"
    || result.error.code === "project_stale"
    || result.error.code === "unity_reloading"
    || result.error.code === "bridge_unreachable"
    || result.error.code === "bridge_timeout"
    || result.error.code === "bridge_protocol_error"
    || result.error.code === "command_timeout"
  );
}

function testRunPollContext(context: DispatcherContext, deadlineMs: number): DispatcherContext {
  const pollContext = { ...context, internalDeadlineUnixMs: deadlineMs, statisticsInternal: true } as DispatcherRuntimeContext;
  delete pollContext.acceptedPending;
  delete pollContext.auditCommandMetadata;
  delete pollContext.auditPolicyMetadata;
  delete pollContext.auditBridgeDurationMs;
  delete pollContext.auditBridgeInvokeAttempts;
  delete pollContext.auditReadRecoveryStatus;
  delete pollContext.auditPostReloadRouting;
  delete pollContext.auditCompileVerification;
  return pollContext;
}

function annotateDirectTestRunResult(args: UnityCallArgs, outcome: TestRunPollOutcome): GatewayResult {
  if (outcome.result.status === "ok" && isJsonObject(outcome.result.data)) {
    return ok({
      ...optionalRequestId(args.request_id),
      project: outcome.result.project ?? args.project,
      command: TEST_RUN_COMMAND,
      data: {
        ...outcome.result.data,
        operation_id: outcome.operationId,
        awaited_by_server: true,
        status_poll_count: outcome.pollCount,
        server_wait_elapsed_ms: outcome.elapsedMs,
        terminal_command: TEST_RUN_STATUS_COMMAND,
        test_run_replayed: false,
        write_command_repeated: false,
      },
      diagnostics: outcome.result.diagnostics,
      ...(outcome.result.next_valid_actions ? { next_valid_actions: outcome.result.next_valid_actions } : {}),
      ...(outcome.result.artifact_refs ? { artifact_refs: outcome.result.artifact_refs } : {}),
      ...(outcome.result.truncated !== undefined ? { truncated: outcome.result.truncated } : {}),
    });
  }
  if (outcome.result.status === "error") {
    return {
      ...outcome.result,
      ...optionalRequestId(args.request_id),
      project: outcome.result.project ?? args.project,
      command: TEST_RUN_COMMAND,
      error: {
        ...outcome.result.error,
        details: {
          ...outcome.result.error.details,
          operation_id: outcome.operationId,
          awaited_by_server: true,
          status_poll_count: outcome.pollCount,
          server_wait_elapsed_ms: outcome.elapsedMs,
          terminal_command: TEST_RUN_STATUS_COMMAND,
          test_run_started: true,
          command_dispatched: true,
          bridge_request_issued: true,
          delivery_state: "dispatched",
          test_run_replayed: false,
          write_command_repeated: false,
        },
      },
    };
  }
  return outcome.result;
}

function directTestRunWaitTimeout(args: UnityCallArgs, outcome: TestRunPollOutcome): GatewayResult {
  const artifactPath = `Library/SGGUnityMcp/job-artifacts/test-runs/${outcome.operationId}.json`;
  return fail({
    ...optionalRequestId(args.request_id),
    project: outcome.result.project ?? args.project,
    command: TEST_RUN_COMMAND,
    error: {
      code: "command_timeout",
      message: "Server-side terminal wait expired; test.run was not replayed",
      details: {
        operation_id: outcome.operationId,
        run_id: outcome.operationId,
        wait_timeout_ms: outcome.timeoutMs,
        wait_elapsed_ms: outcome.elapsedMs,
        status_poll_count: outcome.pollCount,
        last_status: operationStatus(outcome.result),
        expected_artifact_path: artifactPath,
        test_run_started: true,
        command_dispatched: true,
        bridge_request_issued: true,
        test_run_replayed: false,
        write_command_repeated: false,
        delivery_state: "dispatched",
        status_request: { tool: "sgg_unity_call", project: args.project, command: TEST_RUN_STATUS_COMMAND, args_json: JSON.stringify({ operation_id: outcome.operationId, response_mode: "compact" }), mode: "wait" },
        recovery_hint: "Poll test.run.status first with the returned exact operation_id; inspect that operation's artifact only if available, or report unresolved evidence with diagnostics.report_problem. A wait timeout or tracking cleanup does not prove that Unity Test Runner stopped. Do not automatically cancel, replay or start another run until execution state is resolved.",
      },
    },
    artifact_refs: [artifactPath],
    next_valid_actions: ["poll_test_run_status_with_the_returned_operation_id", "inspect_the_exact_test_run_artifact", "do_not_repeat_test.run_until_delivery_is_resolved"],
  });
}

async function waitForSequentialBatchOperation(item: ReturnType<typeof parseItemsJson>[number], result: GatewayResult, context: DispatcherContext, correlation: BatchCorrelation): Promise<GatewayResult> {
  if (item.command !== TEST_RUN_COMMAND || item.mode === "job") {
    return result;
  }
  const commandArgs = batchItemCommandArgs(item);
  const maxSeconds = integerArg(commandArgs.max_seconds, 600, 1, 3600);
  const outcome = await pollTestRunUntilTerminal({
    project: item.project,
    result,
    timeoutMs: maxSeconds * 1000 + BATCH_TEST_RUN_EXTRA_TIMEOUT_MS,
    responseMode: commandArgs.response_mode === "compact" ? "compact" : "full",
    context,
    correlation,
  });
  if (!outcome) return result;
  if (!outcome.timedOut) return annotateBatchTestRunResult(item, outcome.result, outcome.operationId, outcome.pollCount);

  return fail({
    ...optionalRequestId(item.request_id),
    project: item.project,
    command: item.command,
    error: {
      code: "command_timeout",
      message: "Sequential batch timed out while waiting for test.run to finish",
      details: {
        operation_id: outcome.operationId,
        poll_count: outcome.pollCount,
        wait_timeout_ms: outcome.timeoutMs,
        last_status: operationStatus(outcome.result),
        recovery_hint: "Poll test.run.status first with the returned exact operation_id; inspect that operation's artifact only if available, or report unresolved evidence with diagnostics.report_problem. A wait timeout or tracking cleanup does not prove that Unity Test Runner stopped. Do not automatically cancel, replay or start another run until execution state is resolved.",
      },
    },
    next_valid_actions: ["poll_test_run_status_with_the_returned_operation_id", "inspect_the_exact_test_run_artifact_if_available", "diagnostics.report_problem", "do_not_repeat_test.run_until_execution_state_is_resolved"],
  });
}

function batchItemCommandArgs(item: ReturnType<typeof parseItemsJson>[number]): Record<string, unknown> {
  return item.args_json !== undefined ? parseArgsJson(item.args_json) : (item.args ?? {});
}

function operationIdFromResult(result: GatewayResult): string | undefined {
  if (result.status !== "ok" || !isJsonObject(result.data)) {
    return undefined;
  }
  return stringValue(result.data.operation_id) ?? stringValue(result.data.run_id);
}

function isRunningOperationResult(result: GatewayResult): boolean {
  return isTestRunInProgressStatus(operationStatus(result));
}

function isTestRunInProgressStatus(status: string | undefined): boolean {
  return status === "running" || status === TEST_RUN_AWAITING_FILTER_REVALIDATION_STATUS;
}

function operationStatus(result: GatewayResult): string | undefined {
  if (result.status !== "ok" || !isJsonObject(result.data)) {
    return undefined;
  }
  return stringValue(result.data.status);
}

function annotateBatchTestRunResult(item: ReturnType<typeof parseItemsJson>[number], result: GatewayResult, operationId: string, pollCount: number): GatewayResult {
  if (result.status !== "ok" || !isJsonObject(result.data)) {
    return result;
  }

  return ok({
    ...optionalRequestId(item.request_id),
    project: result.project ?? item.project,
    command: item.command,
    data: {
      ...result.data,
      operation_id: operationId,
      awaited_by_batch: true,
      status_poll_count: pollCount,
      terminal_command: TEST_RUN_STATUS_COMMAND,
    },
    ...(result.artifact_refs ? { artifact_refs: result.artifact_refs } : {}),
    ...(result.truncated !== undefined ? { truncated: result.truncated } : {}),
  });
}

function compactSuccessfulCommandResult(command: string, commandArgs: Record<string, unknown>, result: GatewayResult): GatewayResult {
  if (commandArgs.response_mode !== "compact" || !COMPACT_RESPONSE_COMMANDS.has(command) || result.status !== "ok" || !isJsonObject(result.data)) {
    return result;
  }
  const compactData = command === TEST_LIST_COMMAND
    ? compactTestListData(result.data)
    : command === TEST_RUN_COMMAND || command === TEST_RUN_STATUS_COMMAND
      ? compactTestRunData(result.data)
      : compactCompileData(result.data);
  const keepActions = compactResponseNeedsActions(command, result.data);
  const { next_valid_actions: nextValidActions, ...base } = result;
  return {
    ...base,
    data: compactData,
    diagnostics: result.diagnostics.slice(0, 4).map((diagnostic) => ({ ...diagnostic, message: boundedCompactString(diagnostic.message, 240) })),
    ...(keepActions && nextValidActions ? { next_valid_actions: nextValidActions.slice(0, 8) } : {}),
  };
}

function compactResponseNeedsActions(command: string, data: Record<string, unknown>): boolean {
  if (command === TEST_LIST_COMMAND) return data.scan_incomplete === true || data.truncated === true || data.resolution_status === "pending" || data.resolution_status === "unavailable" || (data.next_cursor !== null && data.next_cursor !== undefined);
  if (command === TEST_RUN_COMMAND || command === TEST_RUN_STATUS_COMMAND) return isTestRunInProgressStatus(stringValue(data.status)) || data.dry_run === true || data.success === false;
  return data.ready !== true || data.compilable !== true || data.has_compile_errors === true || data.timed_out === true;
}

function compactTestRunData(data: Record<string, unknown>): Record<string, unknown> {
  const compact: Record<string, unknown> = {
    response_mode: "compact",
    status: boundedCompactString(stringValue(data.status) ?? "unknown", 64),
    terminal: !isTestRunInProgressStatus(stringValue(data.status)),
    artifact_ref: compactArtifactRef(data),
    diagnostics: compactTestDiagnostics(data),
    ...compactNonPassingEvidence(data),
  };
  copyCompactString(compact, data, "operation_id", 80);
  copyCompactString(compact, data, "result", 80);
  copyCompactBoolean(compact, data, "success");
  copyCompactBoolean(compact, data, "dry_run");
  copyCompactBoolean(compact, data, "would_run");
  copyCompactBoolean(compact, data, "artifact_ready");
  copyCompactBoolean(compact, data, "artifact_persisted");
  if (data.status === "pre_execute_refused") {
    copyCompactBoolean(compact, data, "operation_created");
    copyCompactString(compact, data, "lifecycle", 64);
    copyCompactString(compact, data, "artifact_publication_state", 64);
    copyCompactInteger(compact, data, "artifact_publication_attempts");
    copyCompactString(compact, data, "last_published_lifecycle", 64);
    copyCompactString(compact, data, "artifact_publication_error_code", 80);
    copyCompactInteger(compact, data, "execute_dispatch_count");
    copyCompactBoolean(compact, data, "execute_dispatch_committed");
    copyCompactBoolean(compact, data, "test_runner_execute_invocation_attempted");
    copyCompactBoolean(compact, data, "test_runner_execute_invoked");
    if (isJsonObject(data.pre_execute_refusal)
      && typeof data.pre_execute_refusal.code === "string" && data.pre_execute_refusal.code.trim().length > 0
      && typeof data.pre_execute_refusal.reason === "string" && data.pre_execute_refusal.reason.trim().length > 0) {
      compact.pre_execute_refusal = {
        code: boundedCompactString(data.pre_execute_refusal.code, 128),
        reason: boundedCompactString(data.pre_execute_refusal.reason, 256),
      };
    }
  }
  copyCompactBoolean(compact, data, "terminal_counts_authoritative");
  copyCompactInteger(compact, data, "duration_ms");
  copyCompactBoolean(compact, data, "awaited_by_server");
  copyCompactBoolean(compact, data, "awaited_by_batch");
  copyCompactInteger(compact, data, "status_poll_count");
  copyCompactInteger(compact, data, "server_wait_elapsed_ms");
  copyCompactString(compact, data, "terminal_command", 80);
  copyCompactBoolean(compact, data, "test_run_replayed");
  copyCompactBoolean(compact, data, "write_command_repeated");
  copyCompactString(compact, data, "status_source", 80);
  const statusObservation = trustedTestRunStatusObservation(data.status_observation);
  if (statusObservation) compact.status_observation = statusObservation;
  copyCompactBoolean(compact, data, "operation_owner_verified");
  copyCompactBoolean(compact, data, "owner_endpoint_contacted");
  copyCompactBoolean(compact, data, "test_run_bridge_request_issued");
  copyCompactString(compact, data, "test_run_delivery_state", 32);
  copyCompactBoolean(compact, data, "status_or_artifact_check_required_before_repeat");
  copyCompactBoolean(compact, data, "repeat_blocked_until_delivery_state_checked");
  copyCompactBoolean(compact, data, "write_replay_allowed");
  const requestIssuance = compactTestRunRequestIssuance(data.test_run_request_issuance);
  if (requestIssuance) compact.test_run_request_issuance = requestIssuance;
  const discoveryPreflight = compactTestRunDiscoveryPreflight(data.test_run_discovery_preflight);
  if (discoveryPreflight) compact.test_run_discovery_preflight = discoveryPreflight;
  const callerDelivery = compactCallerDelivery(data.caller_delivery);
  if (callerDelivery) compact.caller_delivery = callerDelivery;
  const counts = compactTestCounts(data);
  if (Object.keys(counts).length > 0) compact.counts = counts;
  return compact;
}

function compactTestRunRequestIssuance(value: unknown): Record<string, unknown> | undefined {
  if (!isJsonObject(value)) return undefined;
  const compact: Record<string, unknown> = {};
  copyCompactInteger(compact, value, "schema_version");
  copyCompactString(compact, value, "contract", 96);
  copyCompactString(compact, value, "state", 32);
  copyCompactString(compact, value, "evidence_source", 64);
  copyCompactString(compact, value, "marker_observation", 32);
  copyCompactBoolean(compact, value, "request_issuance_proven");
  copyCompactBoolean(compact, value, "request_non_issuance_proven");
  copyCompactString(compact, value, "publication_outcome", 32);
  copyCompactString(compact, value, "owner_binding_hash", 80);
  copyCompactString(compact, value, "issued_at", 40);
  return Object.keys(compact).length > 0 ? compact : undefined;
}

function compactTestRunDiscoveryPreflight(value: unknown): Record<string, unknown> | undefined {
  if (!isJsonObject(value)) return undefined;
  const compact: Record<string, unknown> = {};
  copyCompactString(compact, value, "contract", 96);
  copyCompactString(compact, value, "status", 32);
  copyCompactInteger(compact, value, "poll_count");
  copyCompactInteger(compact, value, "read_request_count");
  copyCompactInteger(compact, value, "elapsed_ms");
  copyCompactInteger(compact, value, "metadata_elapsed_ms");
  copyCompactInteger(compact, value, "startup_deadline_ms");
  copyCompactInteger(compact, value, "bridge_admission_reserve_ms");
  copyCompactInteger(compact, value, "discovery_generation");
  copyCompactBoolean(compact, value, "test_run_bridge_request_issued");
  copyCompactBoolean(compact, value, "test_runner_execute_invoked");
  copyCompactBoolean(compact, value, "command_replayed");
  if (isJsonObject(value.workflow_identity)) {
    const identity: Record<string, unknown> = {};
    copyCompactString(identity, value.workflow_identity, "instance_id", 256);
    for (const key of ["heartbeat_generation", "catalog_generation", "compilation_generation", "completed_compilation_generation", "discovery_generation"] as const) {
      copyCompactInteger(identity, value.workflow_identity, key);
    }
    if (Object.keys(identity).length > 0) compact.workflow_identity = identity;
  }
  return Object.keys(compact).length > 0 ? compact : undefined;
}

function compactTestCounts(data: Record<string, unknown>): Record<string, unknown> {
  const counts: Record<string, unknown> = {};
  if (isJsonObject(data.counts)) {
    if (typeof data.counts.authoritative === "boolean") counts.authoritative = data.counts.authoritative;
    copyCompactString(counts, data.counts, "source", 80);
    for (const key of ["total", "passed", "failed", "skipped", "pass_count", "fail_count", "skip_count", "inconclusive_count", "authoritative_leaf_total"] as const) {
      copyCompactInteger(counts, data.counts, key);
    }
  }
  if (typeof data.terminal_counts_authoritative === "boolean") counts.authoritative = data.terminal_counts_authoritative;
  copyCompactString(counts, data, "terminal_count_source", 80, "source");
  for (const key of ["total", "passed", "failed", "skipped", "pass_count", "fail_count", "skip_count", "inconclusive_count", "authoritative_leaf_total"] as const) {
    copyCompactInteger(counts, data, key);
  }
  return counts;
}

function compactTestDiagnostics(data: Record<string, unknown>): Array<Record<string, unknown>> {
  const diagnostics: Array<Record<string, unknown>> = [];
  if (Array.isArray(data.diagnostics)) {
    for (const item of data.diagnostics) {
      if (diagnostics.length >= 4 || !isJsonObject(item)) break;
      const diagnostic: Record<string, unknown> = {};
      copyCompactString(diagnostic, item, "test", 160);
      copyCompactString(diagnostic, item, "message", 240);
      if (Object.keys(diagnostic).length > 0) diagnostics.push(diagnostic);
    }
  }
  if (typeof data.message === "string" && data.message.length > 0) diagnostics.push({ message: boundedCompactString(data.message, 240) });
  const failedTests = Array.isArray(data.failed_tests) ? data.failed_tests : [];
  for (const item of failedTests) {
    if (diagnostics.length >= 4 || !isJsonObject(item)) break;
    const diagnostic: Record<string, unknown> = {};
    const name = stringValue(item.full_name) ?? stringValue(item.name) ?? stringValue(item.test_name);
    if (name) diagnostic.test = boundedCompactString(name, 160);
    if (typeof item.message === "string" && item.message.length > 0) diagnostic.message = boundedCompactString(item.message, 240);
    if (Object.keys(diagnostic).length > 0) diagnostics.push(diagnostic);
  }
  return diagnostics;
}

function compactTestListData(data: Record<string, unknown>): Record<string, unknown> {
  const sourceTests = Array.isArray(data.tests) ? data.tests : [];
  const tests = sourceTests.slice(0, 32).flatMap((item) => {
    if (!isJsonObject(item)) return [];
    const test: Record<string, unknown> = {};
    copyCompactString(test, item, "full_name", 240);
    copyCompactString(test, item, "unique_name", 240);
    copyCompactString(test, item, "assembly", 160);
    copyCompactString(test, item, "mode", 32);
    if (isJsonObject(item.runnable_filters)) copyCompactString(test, item.runnable_filters, "recommended", 240, "recommended_filter");
    return Object.keys(test).length > 0 ? [test] : [];
  });
  const cursor = compactNonnegativeInteger(data.cursor) ?? 0;
  const sourceNextCursor = data.next_cursor === null ? null : compactNonnegativeInteger(data.next_cursor) ?? null;
  const pageTrimmed = tests.length < sourceTests.length;
  const nextCursor = pageTrimmed
    ? cursor + tests.length
    : sourceNextCursor;
  const diagnostics = nextCursor !== null
    ? [`Use test.list with cursor=${nextCursor} to continue the deterministic catalog page.`]
    : compactStringArray(data.diagnostic_hints, 4, 240);
  const compact: Record<string, unknown> = {
    response_mode: "compact",
    status: boundedCompactString(stringValue(data.status) ?? "unknown", 64),
    result: boundedCompactString(stringValue(data.resolution_status) ?? "resolved", 80),
    artifact_ref: null,
    tests,
    tests_truncated: data.truncated === true || pageTrimmed || (nextCursor !== null && nextCursor !== undefined),
    diagnostics,
  };
  compact.cursor = cursor;
  compact.returned = tests.length;
  compact.next_cursor = nextCursor;
  copyCompactString(compact, data, "resolution_status", 80);
  copyCompactInteger(compact, data, "total_available");
  copyCompactInteger(compact, data, "runnable_leaf_count");
  copyCompactString(compact, data, "total_available_status", 80);
  copyCompactString(compact, data, "coverage_status", 80);
  copyCompactInteger(compact, data, "discovery_generation");
  copyCompactBoolean(compact, data, "scan_incomplete");
  copyCompactString(compact, data, "scan_reason", 80);
  return compact;
}

function compactCompileData(data: Record<string, unknown>): Record<string, unknown> {
  const status = stringValue(data.status) ?? (data.ready === true && data.has_compile_errors !== true ? "ready" : "unknown");
  const barrier = isJsonObject(data.compile_barrier) ? data.compile_barrier : undefined;
  const compact: Record<string, unknown> = {
    response_mode: "compact",
    status: boundedCompactString(status, 80),
    result: data.ready === true && data.has_compile_errors !== true ? "ready_clean" : boundedCompactString(status, 80),
    artifact_ref: null,
    diagnostics: compactCompileDiagnostics(data.compile_diagnostics),
  };
  if (barrier) copyCompactString(compact, barrier, "operation_id", 80);
  for (const key of ["ready", "editor_ready", "compilable", "is_compiling", "is_updating", "is_playing", "timed_out", "has_compile_errors", "refreshed", "post_reload_stability_verified", "original_command_completed", "write_command_repeated", "write_replay_allowed"] as const) {
    copyCompactBoolean(compact, data, key);
  }
  const duration = compactNonnegativeInteger(data.waited_ms) ?? compactNonnegativeInteger(data.duration_ms);
  if (duration !== undefined) compact.duration_ms = duration;
  const compileErrorCount = compactNonnegativeInteger(data.compile_error_count);
  if (compileErrorCount !== undefined) compact.counts = { compile_errors: compileErrorCount };
  const qualification = safeCompileReadinessQualification(data.readiness_qualification);
  if (qualification) compact.readiness_qualification = qualification;
  const expectedScriptImports = safeExpectedScriptImports(data.expected_script_imports);
  if (expectedScriptImports) {
    compact.expected_script_imports = {
      required: expectedScriptImports.required,
      verified: expectedScriptImports.verified,
      checked_count: expectedScriptImports.checked_count,
      verified_count: expectedScriptImports.verified_count,
      missing_count: expectedScriptImports.missing_count,
      items: expectedScriptImports.items,
    };
  }
  const backgroundProgress = safeBackgroundCompilerProgress(data.background_compiler_progress);
  if (backgroundProgress && (backgroundProgress.active === true || backgroundProgress.correlated_native_failure_observed === true)) compact.background_compiler_progress = backgroundProgress;
  const inventoryDiagnostics = safeLocalPackageInventoryDiagnostics(data.local_package_inventory_diagnostics);
  if (inventoryDiagnostics) compact.local_package_inventory_diagnostics = inventoryDiagnostics;
  if (isJsonObject(data.compile_wait_delivery)) {
    const delivery: Record<string, unknown> = {};
    copyCompactInteger(delivery, data.compile_wait_delivery, "schema_version");
    copyCompactInteger(delivery, data.compile_wait_delivery, "requested_timeout_ms");
    copyCompactInteger(delivery, data.compile_wait_delivery, "effective_unity_wait_ms");
    copyCompactInteger(delivery, data.compile_wait_delivery, "response_delivery_reserve_ms");
    copyCompactInteger(delivery, data.compile_wait_delivery, "caller_budget_ms");
    copyCompactInteger(delivery, data.compile_wait_delivery, "maximum_supported_caller_budget_ms");
    copyCompactString(delivery, data.compile_wait_delivery, "deadline_scope", 64);
    compact.compile_wait_delivery = delivery;
  }
  const callerDelivery = compactCallerDelivery(data.caller_delivery);
  if (callerDelivery) compact.caller_delivery = callerDelivery;
  return compact;
}

function safeLocalPackageInventoryDiagnostics(value: unknown): Record<string, unknown> | undefined {
  if (!isJsonObject(value) || value.contract !== "sgg.unity_mcp.local_package_inventory_diagnostics.v1"
    || value.compiler_source_authority !== "active_compilation_assemblies"
    || value.automatic_repair_attempted !== false || !Array.isArray(value.details)) return undefined;
  const counts = [value.missing_asset_file_count, value.unexpected_asset_file_count, value.missing_compiler_source_file_count, value.unexpected_compiler_source_file_count];
  if (counts.some((count) => !Number.isInteger(count) || (count as number) < 0 || (count as number) > 16_384)) return undefined;
  const total = value.details_total_count;
  const returned = value.details_returned_count;
  if (!Number.isInteger(total) || (total as number) < 0 || (total as number) > 65_536
    || total !== counts.reduce<number>((sum, count) => sum + (count as number), 0)
    || !Number.isInteger(returned) || (returned as number) < 0 || (returned as number) > 16
    || value.details.length !== returned || value.details_truncated !== ((returned as number) < (total as number))) return undefined;
  const allowedKinds = new Set(["missing_asset", "unexpected_asset", "missing_compiler_source", "unexpected_compiler_source"]);
  const allowedReasons = new Set(["meta_missing", "guid_missing", "guid_ambiguous", "guid_invalid_length", "guid_invalid_format", "meta_size_limit_exceeded", "meta_inspection_unavailable"]);
  const details: Record<string, unknown>[] = [];
  const retainedByKind = new Map<string, number>();
  const kindCounts = new Map([["missing_asset", counts[0] as number], ["unexpected_asset", counts[1] as number], ["missing_compiler_source", counts[2] as number], ["unexpected_compiler_source", counts[3] as number]]);
  for (const candidate of value.details) {
    if (!isJsonObject(candidate) || !allowedKinds.has(candidate.kind as string)
      || typeof candidate.path !== "string" || candidate.path.length > 512
      || !/^Packages\/[a-z0-9._-]{1,128}\/.+/u.test(candidate.path)
      || candidate.path.split("/").some((segment) => !segment || segment === "." || segment === ".." || /[:\\\p{Cc}]/u.test(segment))) return undefined;
    const kind = candidate.kind as string;
    const retained = (retainedByKind.get(kind) ?? 0) + 1;
    if (retained > (kindCounts.get(kind) ?? 0)) return undefined;
    retainedByKind.set(kind, retained);
    const detail: Record<string, unknown> = { kind: candidate.kind, path: candidate.path };
    if (candidate.metadata_issue !== undefined) {
      if (candidate.kind !== "missing_asset" || !isJsonObject(candidate.metadata_issue)
        || !allowedReasons.has(candidate.metadata_issue.reason as string)) return undefined;
      const metadataIssue: Record<string, unknown> = { reason: candidate.metadata_issue.reason };
      if (candidate.metadata_issue.observed_guid_length !== undefined) {
        const length = candidate.metadata_issue.observed_guid_length;
        if (!Number.isInteger(length) || (length as number) < 1 || (length as number) > 4096) return undefined;
        metadataIssue.observed_guid_length = length;
      }
      detail.metadata_issue = metadataIssue;
    }
    details.push(detail);
  }
  return { contract: value.contract, compiler_source_authority: value.compiler_source_authority,
    missing_asset_file_count: counts[0], unexpected_asset_file_count: counts[1],
    missing_compiler_source_file_count: counts[2], unexpected_compiler_source_file_count: counts[3],
    details_total_count: total, details_returned_count: returned, details_truncated: value.details_truncated,
    automatic_repair_attempted: false, details };
}

function compactCallerDelivery(value: unknown): Record<string, unknown> | undefined {
  if (!isJsonObject(value)) return undefined;
  const compact: Record<string, unknown> = {};
  for (const key of ["schema_version", "caller_budget_ms", "response_delivery_reserve_ms", "maximum_post_admission_wait_ms", "requested_wait_ms", "effective_wait_ms"] as const) {
    copyCompactInteger(compact, value, key);
  }
  return Object.keys(compact).length > 0 ? compact : undefined;
}

function compactCompileDiagnostics(value: unknown): Array<Record<string, unknown>> {
  if (!isJsonObject(value) || !Array.isArray(value.errors)) return [];
  return value.errors.slice(0, 4).flatMap((item) => {
    if (!isJsonObject(item)) return [];
    const diagnostic: Record<string, unknown> = {};
    copyCompactString(diagnostic, item, "severity", 16);
    copyCompactString(diagnostic, item, "code", 32);
    copyCompactString(diagnostic, item, "message", 240);
    return Object.keys(diagnostic).length > 0 ? [diagnostic] : [];
  });
}

function compactArtifactRef(data: Record<string, unknown>): string | null {
  if (data.dry_run === true || (data.response_mode !== "compact" && data.artifact_ready !== true && data.artifact_persisted !== true)) return null;
  const value = stringValue(data.artifact_ref) ?? stringValue(data.artifact_path);
  return value && value.length <= 1024 && !path.isAbsolute(value) && !/^[A-Za-z]:/.test(value) && !value.split(/[\\/]+/).includes("..") ? value : null;
}

function compactStringArray(value: unknown, maxItems: number, maxChars: number): string[] {
  return Array.isArray(value) ? value.flatMap((item) => typeof item === "string" && item.length > 0 ? [boundedCompactString(item, maxChars)] : []).slice(0, maxItems) : [];
}

function copyCompactString(target: Record<string, unknown>, source: Record<string, unknown>, key: string, maxChars: number, targetKey = key): void {
  if (typeof source[key] === "string" && source[key].length > 0) target[targetKey] = boundedCompactString(source[key], maxChars);
}

function copyCompactBoolean(target: Record<string, unknown>, source: Record<string, unknown>, key: string): void {
  if (typeof source[key] === "boolean") target[key] = source[key];
}

function copyCompactInteger(target: Record<string, unknown>, source: Record<string, unknown>, key: string): void {
  const value = compactNonnegativeInteger(source[key]);
  if (value !== undefined) target[key] = value;
}

function compactNonnegativeInteger(value: unknown): number | undefined {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? value : undefined;
}

function boundedCompactString(value: string, maxChars: number): string {
  return value.length <= maxChars ? value : `${value.slice(0, Math.max(0, maxChars - 3))}...`;
}

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}

function runInProjectQueue<T>(
  context: DispatcherContext,
  project: string,
  task: () => Promise<T>,
  bypassQueue = false,
  owner?: ProjectQueueOwnerMetadata,
): Promise<T> {
  const admittedTask = () => {
    const trace = (context as DispatcherRuntimeContext).batchItemTrace;
    if (trace) trace.admitted = true;
    return task();
  };
  if (context.queues && !bypassQueue) {
    const trace = (context as DispatcherRuntimeContext).batchItemTrace;
    if (trace) trace.queued = true;
    return context.queues.run(project, admittedTask, owner);
  }
  return admittedTask();
}

function settleProjectQueueAdmissionBeforeDeadline(
  context: DispatcherRuntimeContext,
  admission: Promise<boolean>,
  deadlineUnixMs: number,
  onExpired: () => void,
): Promise<boolean> {
  return new Promise((resolve) => {
    let settled = false;
    const settle = (admitted: boolean): void => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      resolve(admitted);
    };
    const timer = setTimeout(() => {
      onExpired();
      settle(false);
    }, Math.max(0, deadlineUnixMs - nowMs(context)));
    void admission.then(settle, () => settle(false));
  });
}

function projectQueueOwner(invocation: InvocationContextSnapshot, deadlineUnixMs?: number): ProjectQueueOwnerMetadata {
  return {
    command: invocation.identity.command,
    risk: invocation.descriptor.risk,
    idempotency: invocation.descriptor.idempotency,
    ...(deadlineUnixMs === undefined ? {} : { deadline_unix_ms: deadlineUnixMs }),
  };
}

function genericProjectQueueTimeout(
  context: DispatcherContext,
  invocation: InvocationContextSnapshot,
  requestId: string | undefined,
  timeoutMs: number,
  queueEnqueuedAtUnixMs: number,
): GatewayResult {
  const observedAtUnixMs = nowMs(context);
  const owner = context.queues?.activeOwner(invocation.identity.project);
  const deadlineState = owner?.deadline_unix_ms === undefined
    ? "unavailable"
    : owner.deadline_unix_ms <= observedAtUnixMs
      ? "elapsed"
      : "active";
  const recoveryStatus = !owner
    ? "active_owner_released_or_unavailable"
    : deadlineState === "elapsed"
      ? "active_owner_deadline_elapsed_completion_pending"
      : deadlineState === "active"
        ? "active_owner_running"
        : "active_owner_running_deadline_unavailable";
  return fail({
    ...optionalRequestId(requestId),
    project: invocation.identity.project,
    command: invocation.identity.command,
    error: {
      code: "queue_timeout",
      message: `Timed out waiting for project queue admission for '${invocation.identity.command}'.`,
      details: {
        timeout_ms: timeoutMs,
        delivery_state: "not_dispatched",
        bridge_request_issued: false,
        command_bridge_request_issued: false,
        command_replayed: false,
        auto_retry_attempted: false,
        explicit_repeat_required: true,
        project_queue: {
          waiter_queue_age_ms: Math.max(0, observedAtUnixMs - queueEnqueuedAtUnixMs),
          active_owner_present: owner !== undefined,
          ...(owner ? {
            active_owner: {
              command: boundedCompactString(owner.command, 128),
              risk: boundedCompactString(owner.risk, 32),
              idempotency: boundedCompactString(owner.idempotency, 32),
              owner_queue_age_ms: Math.max(0, observedAtUnixMs - owner.enqueued_at_unix_ms),
              owner_active_age_ms: Math.max(0, observedAtUnixMs - owner.admitted_at_unix_ms),
              deadline_state: deadlineState,
              ...(owner.deadline_unix_ms === undefined ? {} : {
                deadline_remaining_ms: Math.max(0, owner.deadline_unix_ms - observedAtUnixMs),
              }),
            },
          } : {}),
          recovery_status: recoveryStatus,
          history_retained: false,
        },
      },
    },
    next_valid_actions: ["call_sgg_unity_projects", "repeat_the_command_only_if_it_is_still_intended"],
  });
}

function markBatchBridgeRequestIssued(context: DispatcherRuntimeContext, command: string, risk: CommandDescriptor["risk"]): void {
  if (!context.batchItemTrace || context.batchItemTrace.command !== command) return;
  context.batchItemTrace.bridgeRequestCount += 1;
  context.batchItemTrace.unsafe = risk !== "read";
}

function markBatchResponseDelivered(context: DispatcherRuntimeContext, command: string): void {
  if (context.batchItemTrace?.command !== command) return;
  context.batchItemTrace.bridgeRequestIssued = true;
  context.batchItemTrace.responseDelivered = true;
}

function isCancellableJobStatus(status: string): boolean {
  return status === "queued" || status === "running";
}

function jobCancellationUnsupported(job: Record<string, unknown>): Record<string, unknown> {
  return {
    ...job,
    status: "cancellation_unsupported",
    job_status: job.status,
    cancel_requested: false,
    supports_cancel: false,
    cancellation_pending: false,
    cancellation_unsupported: true,
    unity_main_thread_interrupted: false,
    cancellation_mode: "unsupported_by_command_descriptor",
    message: `Command '${String(job.command)}' does not advertise supports_cancel; the gateway did not mark this job as cancelled.`,
    next_valid_actions: ["poll_job_status", "use_command_specific_cancel_when_available", "inspect_unity_editor_for_modal_dialogs_or_freeze"],
  };
}

function jobCancellationResult(job: Record<string, unknown>): Record<string, unknown> {
  const cancellationPending = job.status === "cancel_requested";
  return {
    ...job,
    cancellation_pending: cancellationPending,
    cancellation_unsupported: false,
    unity_main_thread_interrupted: false,
    cancellation_mode: cancellationPending ? "cooperative_command_cancel_intent" : "job_already_terminal_or_cancelled",
    next_valid_actions: cancellationPending ? ["poll_job_status", "inspect_unity_editor_for_modal_dialogs_or_freeze"] : ["inspect_job_result"],
  };
}

function commandDeadlineUnixMs(context: DispatcherContext, timeoutMs: number): number {
  return (context.now?.().getTime() ?? Date.now()) + timeoutMs;
}

function earlierDeadline(left: number, right: number | undefined): number {
  return right === undefined ? left : Math.min(left, right);
}

function recoveryTransport(context: DispatcherContext, deadlineUnixMs: number, requestId?: string): BridgeTransportOptions {
  return {
    timeoutMs: Math.max(0, deadlineUnixMs - nowMs(context)),
    deadlineUnixMs,
    ...(requestId !== undefined && safeTransportRequestId(requestId) ? { requestId } : {}),
  };
}

function safeTransportRequestId(requestId: string | undefined): boolean {
  return requestId !== undefined && /^[A-Za-z0-9._-]{1,128}$/.test(requestId);
}

function isValidCommandTimeout(timeoutMs: number): boolean {
  return Number.isInteger(timeoutMs) && timeoutMs > 0 && timeoutMs <= MAX_COMMAND_TIMEOUT_MS;
}

function applyDescribePolicy(context: DispatcherRuntimeContext, project: string, describeData: unknown, selector: string, extraCommands: CommandDescriptor[] = []): unknown {
  if (selector !== "commands" && !isCommandGroupSelector(selector)) {
    return describeData;
  }

  const catalog = describeData as CommandCatalogResponse;
  const commands = [...catalog.commands, ...VIRTUAL_COMMANDS, ...extraCommands];
  const degraded = context.policyManager?.degraded === true;
  const visibleByFeature = commands.filter((command) => (degraded && DEGRADED_POLICY_RECOVERY_COMMANDS.has(command.id)) || isFeatureEnabled(command, context.features));
  const visibleBySelector = filterCommandGroup(visibleByFeature, selector);
  const normallyVisible = filterVisibleCommands(context.policy, project, visibleBySelector);
  const visible = degraded
    ? visibleBySelector.filter((command) => command.risk === "read" && (DEGRADED_POLICY_RECOVERY_COMMANDS.has(command.id) || normallyVisible.some((candidate) => candidate.id === command.id)))
    : normallyVisible;

  return {
    ...catalog,
    catalog_revision: commandCatalogRevision(visible),
    commands: visible.map(withGatewayDescriptorRevision),
    policy_status: policyManagerSnapshot(context.policyManager, context.policy !== undefined),
  };
}

async function officialRelayCommands(context: DispatcherRuntimeContext, project: string, selector: string): Promise<CommandDescriptor[]> {
  if (!context.officialRelay || (selector.startsWith("command:") && !officialToolName(selector.slice("command:".length)))) {
    return [];
  }

  return context.officialRelay.listCommandDescriptors(project);
}

async function describeOfficialCommand(context: DispatcherRuntimeContext, project: string, selector: string): Promise<SingleCommandResponse | undefined> {
  if (!selector.startsWith("command:")) {
    return undefined;
  }

  const commandId = selector.slice("command:".length);
  if (!officialToolName(commandId)) {
    return undefined;
  }

  const command = await context.officialRelay?.findCommandDescriptor(project, commandId);
  if (!command || !isFeatureEnabled(command, context.features) || (context.policyManager?.degraded === true && command.risk !== "read")) {
    return { schema_version: 1, project, commands_hash: "", descriptor_revision: null, command: null };
  }

  const visible = filterVisibleCommands(context.policy, project, [command])[0] ?? null;
  return { schema_version: 1, project, commands_hash: "", descriptor_revision: visible ? commandDescriptorRevision(visible) : null, command: visible ? withGatewayDescriptorRevision(visible) : null };
}

function describeVirtualCommand(context: DispatcherRuntimeContext, project: string, selector: string): SingleCommandResponse | undefined {
  if (!selector.startsWith("command:")) {
    return undefined;
  }

  const command = findVirtualCommand(selector.slice("command:".length));
  if (!command) {
    return undefined;
  }

  const degradedRecovery = isDegradedPolicyRecoveryCommand(context, command.id);
  if ((!degradedRecovery && !isFeatureEnabled(command, context.features)) || (context.policyManager?.degraded === true && command.risk !== "read")) {
    return { schema_version: 1, project, commands_hash: "", descriptor_revision: null, command: null };
  }

  const visible = degradedRecovery ? command : filterVisibleCommands(context.policy, project, [command])[0] ?? null;
  return { schema_version: 1, project, commands_hash: "", descriptor_revision: visible ? commandDescriptorRevision(visible) : null, command: visible ? withGatewayDescriptorRevision(visible) : null };
}

function describeGatewayLocalDiagnosticsCommand(context: DispatcherRuntimeContext, project: string, selector: string): SingleCommandResponse | undefined {
  if (!selector.startsWith("command:") || !findDiagnosticsCommand(selector.slice("command:".length))) {
    return undefined;
  }
  return describeVirtualCommand(context, project, selector);
}

function missingCommandDescribeResult(project: string, selector: string, describeData: unknown): GatewayResult | undefined {
  if (!selector.startsWith("command:") || (describeData as SingleCommandResponse).command !== null) {
    return undefined;
  }

  return commandNotFound(project, selector.slice("command:".length));
}

const VIRTUAL_COMMANDS = [...DIAGNOSTICS_COMMANDS, ...PLAN_COMMANDS, ...PIPELINE_COMMANDS, ...SKILL_COMMANDS, ...CHECKPOINT_COMMANDS, ...ARTIFACT_COMMANDS, ...EDITOR_MENU_OPERATION_COMMANDS, ...UNITY_DOCS_COMMANDS, ...POLICY_COMMANDS];

function findVirtualCommand(commandId: string): CommandDescriptor | undefined {
  return findDiagnosticsCommand(commandId) ?? findPlanCommand(commandId) ?? findPipelineCommand(commandId) ?? findSkillCommand(commandId) ?? findCheckpointCommand(commandId) ?? findArtifactCommand(commandId) ?? findEditorMenuOperationCommand(commandId) ?? findUnityDocsCommand(commandId) ?? findPolicyCommand(commandId);
}

function filterCommandGroup(commands: CommandDescriptor[], selector: string): CommandDescriptor[] {
  if (!isCommandGroupSelector(selector)) {
    return commands;
  }

  const group = selector.slice("commands:".length);
  return commands.filter((command) => commandMatchesGroup(command, group));
}

function commandMatchesGroup(command: CommandDescriptor, group: string): boolean {
  return command.id.startsWith(`${group}.`)
    || command.tags?.includes(group) === true
    || command.capability === group
    || command.capability?.startsWith(`${group}_`) === true;
}

async function resolveCommandDescriptor(bridge: BridgeClient, record: HeartbeatRecord, commandId: string, transport?: BridgeTransportOptions): Promise<{ command: CommandDescriptor; commandsHash: string | null; metadata: SingleCommandResponse } | undefined> {
  const describeData = await bridge.describe(record, `command:${commandId}`, transport) as SingleCommandResponse;
  if (describeData.command) {
    return { command: describeData.command, commandsHash: describeData.commands_hash ?? record.commands_hash ?? null, metadata: describeData };
  }
  return undefined;
}

function withGatewayDescriptorRevision(command: CommandDescriptor): CommandDescriptor & { descriptor_revision: string } {
  return { ...command, descriptor_revision: commandDescriptorRevision(command) };
}

type SchemaValidationIssueCode =
  | "enum_value_not_allowed"
  | "invalid_type"
  | "missing_required_field"
  | "unsupported_field"
  | "below_minimum"
  | "above_maximum"
  | "string_too_short"
  | "string_too_long"
  | "pattern_mismatch"
  | "array_too_short"
  | "array_too_long"
  | "object_too_small"
  | "object_too_large"
  | "relational_constraint_failed";

type SchemaValidationIssue = {
  path: Array<string | number>;
  issueCode: SchemaValidationIssueCode;
  rejectedValueType?: string;
  valueSchema?: Record<string, unknown>;
};

type SchemaValidationResult =
  | { status: "ok" }
  | { status: "error"; issues: SchemaValidationIssue[] }
  | { status: "schema_unavailable" };

type SchemaNormalizationBudget = { nodes: number };

function validateCommandArgs(schema: unknown, args: Record<string, unknown>, allowPatterns = false): SchemaValidationResult {
  if (!isJsonObject(schema)) {
    return { status: "schema_unavailable" };
  }
  if (!allowPatterns && schemaContainsPattern(schema)) {
    return { status: "schema_unavailable" };
  }
  const normalizedSchema = normalizeJsonSchema(schema, schema, 0);
  if (!normalizedSchema || normalizedSchema.type !== "object" || !validateJsonSchemaDefinition(normalizedSchema, 0)) {
    return { status: "schema_unavailable" };
  }

  const issues: SchemaValidationIssue[] = [];
  const schemaOk = validateJsonSchemaValue(normalizedSchema, args, [], issues);
  if (!schemaOk) {
    return { status: "schema_unavailable" };
  }

  return issues.length === 0 ? { status: "ok" } : { status: "error", issues };
}

function schemaContainsPattern(schema: Record<string, unknown>): boolean {
  const pending: Record<string, unknown>[] = [schema];
  let visited = 0;
  while (pending.length > 0) {
    const current = pending.pop();
    visited++;
    if (visited > 512 || !current) return true;
    if (Object.hasOwn(current, "pattern")) return true;
    for (const mapKey of ["properties", "$defs"] as const) {
      const map = current[mapKey];
      if (map !== undefined && !isJsonObject(map)) return true;
      if (isJsonObject(map)) for (const child of Object.values(map)) {
        if (!isJsonObject(child)) return true;
        pending.push(child);
      }
    }
    if (current.items !== undefined) {
      if (!isJsonObject(current.items)) return true;
      pending.push(current.items);
    }
    for (const keyword of ["oneOf", "anyOf", "allOf"] as const) {
      const alternatives = current[keyword];
      if (alternatives === undefined) continue;
      if (!Array.isArray(alternatives)) return true;
      for (const child of alternatives) {
        if (!isJsonObject(child)) return true;
        pending.push(child);
      }
    }
  }
  return false;
}

function normalizeJsonSchema(root: Record<string, unknown>, schema: Record<string, unknown>, depth: number, visitedRefs: Set<string> = new Set(), budget: SchemaNormalizationBudget = { nodes: 0 }): Record<string, unknown> | undefined {
  budget.nodes++;
  if (depth > 12 || budget.nodes > 512) return undefined;
  if (schema.$defs !== undefined) {
    if (!isJsonObject(schema.$defs) || Object.keys(schema.$defs).length > 64) return undefined;
    for (const [name, definition] of Object.entries(schema.$defs)) {
      if (!/^[A-Za-z_][A-Za-z0-9_.-]{0,63}$/.test(name) || !isJsonObject(definition)) return undefined;
      const normalizedDefinition = normalizeJsonSchema(root, definition, depth + 1, new Set([`#/$defs/${name}`]), budget);
      if (!normalizedDefinition || !validateJsonSchemaDefinition(normalizedDefinition, depth + 1)) return undefined;
    }
  }
  let source = schema;
  if (schema.$ref !== undefined) {
    if (Object.keys(schema).some((key) => key !== "$ref")) return undefined;
    if (typeof schema.$ref !== "string" || !schema.$ref.startsWith("#/$defs/") || !isJsonObject(root.$defs)) return undefined;
    const definitionName = schema.$ref.slice("#/$defs/".length);
    const ref = `#/$defs/${definitionName}`;
    if (!/^[A-Za-z_][A-Za-z0-9_.-]{0,63}$/.test(definitionName) || !Object.hasOwn(root.$defs, definitionName) || !isJsonObject(root.$defs[definitionName]) || visitedRefs.has(ref)) return undefined;
    const nextVisited = new Set(visitedRefs);
    nextVisited.add(ref);
    return normalizeJsonSchema(root, root.$defs[definitionName], depth + 1, nextVisited, budget);
  }

  const normalized: Record<string, unknown> = { ...source };
  if (source.properties !== undefined) {
    if (!isJsonObject(source.properties)) return undefined;
    const properties: Record<string, unknown> = Object.create(null) as Record<string, unknown>;
    for (const [key, propertySchema] of Object.entries(source.properties)) {
      if (!isJsonObject(propertySchema)) return undefined;
      const child = normalizeJsonSchema(root, propertySchema, depth + 1, visitedRefs, budget);
      if (!child) return undefined;
      properties[key] = child;
    }
    normalized.properties = properties;
  }
  if (source.items !== undefined) {
    if (!isJsonObject(source.items)) return undefined;
    const items = normalizeJsonSchema(root, source.items, depth + 1, visitedRefs, budget);
    if (!items) return undefined;
    normalized.items = items;
  }
  for (const keyword of ["oneOf", "anyOf", "allOf"] as const) {
    const alternatives = source[keyword];
    if (alternatives === undefined) continue;
    if (!Array.isArray(alternatives) || alternatives.length === 0 || alternatives.length > 16) return undefined;
    const normalizedAlternatives: Record<string, unknown>[] = [];
    for (const alternative of alternatives) {
      if (!isJsonObject(alternative)) return undefined;
      const child = normalizeJsonSchema(root, alternative, depth + 1, visitedRefs, budget);
      if (!child) return undefined;
      normalizedAlternatives.push(child);
    }
    normalized[keyword] = normalizedAlternatives;
  }
  if (normalized.type === undefined) {
    if (normalized.properties !== undefined || normalized.required !== undefined) normalized.type = "object";
    else if (normalized.items !== undefined) normalized.type = "array";
  }
  delete normalized.$defs;
  return normalized;
}

function validateJsonSchemaDefinition(schema: Record<string, unknown>, depth: number): boolean {
  if (depth > 12) return false;
  if (schema.type !== undefined && jsonSchemaTypes(schema.type) === undefined) return false;
  if (schema.enum !== undefined && (!Array.isArray(schema.enum) || schema.enum.length === 0 || schema.enum.length > 64 || !schema.enum.every((value) => value === null || ["string", "number", "boolean"].includes(typeof value)))) return false;
  for (const key of ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum"] as const) {
    if (schema[key] !== undefined && (typeof schema[key] !== "number" || !Number.isFinite(schema[key]))) return false;
  }
  for (const key of ["minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties"] as const) {
    const value = schema[key];
    if (value !== undefined && (typeof value !== "number" || !Number.isInteger(value) || value < 0)) return false;
  }
  if (schema.pattern !== undefined) {
    if (typeof schema.pattern !== "string" || schema.pattern.length === 0 || schema.pattern.length > 256) return false;
    try {
      new RegExp(schema.pattern);
    } catch {
      return false;
    }
  }
  if (schema.required !== undefined && (!Array.isArray(schema.required) || !schema.required.every((value) => typeof value === "string"))) return false;
  if (schema.additionalProperties !== undefined && typeof schema.additionalProperties !== "boolean") return false;
  if (schema.properties !== undefined) {
    if (!isJsonObject(schema.properties)) return false;
    for (const propertySchema of Object.values(schema.properties)) {
      if (!isJsonObject(propertySchema) || !validateJsonSchemaDefinition(propertySchema, depth + 1)) return false;
    }
  }
  if (schema.items !== undefined && (!isJsonObject(schema.items) || !validateJsonSchemaDefinition(schema.items, depth + 1))) return false;
  for (const keyword of ["oneOf", "anyOf", "allOf"] as const) {
    const alternatives = schema[keyword];
    if (alternatives !== undefined && (!Array.isArray(alternatives) || alternatives.length === 0 || alternatives.length > 16 || !alternatives.every((alternative) => isJsonObject(alternative) && validateJsonSchemaDefinition(alternative, depth + 1)))) return false;
  }
  return true;
}

function validateJsonSchemaValue(schema: Record<string, unknown>, value: unknown, path: Array<string | number>, issues: SchemaValidationIssue[]): boolean {
  if (issues.length >= MAX_SCHEMA_ISSUES_COLLECTED) {
    return true;
  }

  const schemaTypes = jsonSchemaTypes(schema.type);
  if (schema.type !== undefined && schemaTypes === undefined) {
    return false;
  }

  if (schemaTypes !== undefined && !schemaTypes.some((type) => matchesJsonSchemaType(value, type))) {
    addSchemaIssue(issues, path, "invalid_type", value, schema);
    return true;
  }

  if (Array.isArray(schema.enum) && !schema.enum.some((candidate) => Object.is(candidate, value))) {
    addSchemaIssue(issues, path, "enum_value_not_allowed", value, schema);
    return true;
  }

  if ((schemaTypes?.includes("number") || schemaTypes?.includes("integer")) && typeof value === "number") {
    validateNumberConstraints(schema, value, path, issues);
  }

  if (schemaTypes?.includes("string") && typeof value === "string") {
    const patternOk = validateStringConstraints(schema, value, path, issues);
    if (!patternOk) {
      return false;
    }
  }

  if (schemaTypes?.includes("array") && Array.isArray(value)) {
    const itemsOk = validateArrayConstraints(schema, value, path, issues);
    if (!itemsOk) {
      return false;
    }
  }

  if (schemaTypes?.includes("object") && isJsonObject(value)) {
    if (!validateObjectConstraints(schema, value, path, issues)) return false;
  }

  return validateSchemaCombinators(schema, value, path, issues);
}

function validateSchemaCombinators(schema: Record<string, unknown>, value: unknown, path: Array<string | number>, issues: SchemaValidationIssue[]): boolean {
  if (Array.isArray(schema.allOf)) {
    for (const branch of schema.allOf) {
      if (!isJsonObject(branch) || !validateJsonSchemaValue(branch, value, path, issues)) return false;
    }
  }
  for (const keyword of ["anyOf", "oneOf"] as const) {
    const alternatives = schema[keyword];
    if (!Array.isArray(alternatives)) continue;
    let matches = 0;
    for (const branch of alternatives) {
      if (!isJsonObject(branch)) return false;
      const branchIssues: SchemaValidationIssue[] = [];
      if (!validateJsonSchemaValue(branch, value, path, branchIssues)) return false;
      if (branchIssues.length === 0) matches++;
    }
    if ((keyword === "anyOf" && matches === 0) || (keyword === "oneOf" && matches !== 1)) {
      addSchemaIssue(issues, path, "relational_constraint_failed", value, schema);
    }
  }
  return true;
}

function validateObjectConstraints(schema: Record<string, unknown>, value: Record<string, unknown>, path: Array<string | number>, issues: SchemaValidationIssue[]): boolean {
  const properties = schema.properties;
  if (properties !== undefined && !isJsonObject(properties)) {
    return false;
  }

  if (schema.required !== undefined && (!Array.isArray(schema.required) || !schema.required.every((item) => typeof item === "string"))) {
    return false;
  }
  if (schema.minProperties !== undefined && (typeof schema.minProperties !== "number" || !Number.isInteger(schema.minProperties) || schema.minProperties < 0)) {
    return false;
  }
  if (schema.maxProperties !== undefined && (typeof schema.maxProperties !== "number" || !Number.isInteger(schema.maxProperties) || schema.maxProperties < 0)) {
    return false;
  }

  const propertyCount = Object.keys(value).length;
  if (typeof schema.minProperties === "number" && propertyCount < schema.minProperties) {
    addSchemaIssue(issues, path, "object_too_small", value, schema);
  }
  if (typeof schema.maxProperties === "number" && propertyCount > schema.maxProperties) {
    addSchemaIssue(issues, path, "object_too_large", value, schema);
  }

  const propertySchemas = (properties ?? {}) as Record<string, unknown>;
  for (const required of (schema.required ?? []) as string[]) {
    if (!Object.hasOwn(value, required)) {
      const requiredSchema = isJsonObject(propertySchemas[required]) ? propertySchemas[required] : undefined;
      addSchemaIssue(issues, [...path, required], "missing_required_field", undefined, requiredSchema);
    }
  }

  if (schema.additionalProperties === false) {
    for (const key of Object.keys(value)) {
      if (!Object.hasOwn(propertySchemas, key)) {
        addSchemaIssue(issues, [...path, key], "unsupported_field", value[key], schema);
      }
    }
  }

  for (const [key, propertySchema] of Object.entries(propertySchemas)) {
    if (Object.hasOwn(value, key)) {
      if (!isJsonObject(propertySchema)) {
        return false;
      }
      const ok = validateJsonSchemaValue(propertySchema, value[key], [...path, key], issues);
      if (!ok) {
        return false;
      }
    }
  }

  return true;
}

function validateNumberConstraints(schema: Record<string, unknown>, value: number, path: Array<string | number>, issues: SchemaValidationIssue[]): void {
  if (typeof schema.minimum === "number" && value < schema.minimum) {
    addSchemaIssue(issues, path, "below_minimum", value, schema);
  }
  if (typeof schema.maximum === "number" && value > schema.maximum) {
    addSchemaIssue(issues, path, "above_maximum", value, schema);
  }
  if (typeof schema.exclusiveMinimum === "number" && value <= schema.exclusiveMinimum) {
    addSchemaIssue(issues, path, "below_minimum", value, schema);
  }
  if (typeof schema.exclusiveMaximum === "number" && value >= schema.exclusiveMaximum) {
    addSchemaIssue(issues, path, "above_maximum", value, schema);
  }
}

function validateStringConstraints(schema: Record<string, unknown>, value: string, path: Array<string | number>, issues: SchemaValidationIssue[]): boolean {
  if (typeof schema.minLength === "number" && value.length < schema.minLength) {
    addSchemaIssue(issues, path, "string_too_short", value, schema);
  }
  if (typeof schema.maxLength === "number" && value.length > schema.maxLength) {
    addSchemaIssue(issues, path, "string_too_long", value, schema);
  }
  if (typeof schema.pattern === "string") {
    let pattern: RegExp;
    try {
      pattern = new RegExp(schema.pattern);
    } catch {
      return false;
    }
    if (!pattern.test(value)) {
      addSchemaIssue(issues, path, "pattern_mismatch", value, schema);
    }
  }
  return true;
}

function validateArrayConstraints(schema: Record<string, unknown>, value: unknown[], path: Array<string | number>, issues: SchemaValidationIssue[]): boolean {
  if (typeof schema.minItems === "number" && value.length < schema.minItems) {
    addSchemaIssue(issues, path, "array_too_short", value, schema);
  }
  if (typeof schema.maxItems === "number" && value.length > schema.maxItems) {
    addSchemaIssue(issues, path, "array_too_long", value, schema);
  }
  if (schema.items !== undefined) {
    if (!isJsonObject(schema.items)) {
      return false;
    }
    for (const [index, item] of value.entries()) {
      const ok = validateJsonSchemaValue(schema.items, item, [...path, index], issues);
      if (!ok) {
        return false;
      }
    }
  }
  return true;
}

function jsonSchemaTypes(type: unknown): JsonSchemaType[] | undefined {
  if (type === undefined) {
    return undefined;
  }
  const types = Array.isArray(type) ? type : [type];
  if (types.length === 0 || !types.every((item): item is JsonSchemaType => typeof item === "string" && isSupportedJsonSchemaType(item))) {
    return undefined;
  }
  return [...new Set(types)];
}

function matchesJsonSchemaType(value: unknown, type: JsonSchemaType): boolean {
  switch (type) {
    case "string":
      return typeof value === "string";
    case "number":
      return typeof value === "number" && Number.isFinite(value);
    case "integer":
      return typeof value === "number" && Number.isInteger(value);
    case "boolean":
      return typeof value === "boolean";
    case "object":
      return isJsonObject(value);
    case "array":
      return Array.isArray(value);
    case "null":
      return value === null;
    default:
      return false;
  }
}

function isSupportedJsonSchemaType(type: string): type is JsonSchemaType {
  return type === "string" || type === "number" || type === "integer" || type === "boolean" || type === "object" || type === "array" || type === "null";
}

function addSchemaIssue(issues: SchemaValidationIssue[], path: Array<string | number>, issueCode: SchemaValidationIssueCode, rejectedValue?: unknown, valueSchema?: Record<string, unknown>): void {
  if (issues.length < MAX_SCHEMA_ISSUES_COLLECTED) {
    issues.push({
      path,
      issueCode,
      ...(rejectedValue !== undefined ? { rejectedValueType: jsonValueType(rejectedValue) } : {}),
      ...(valueSchema ? { valueSchema } : {}),
    });
  }
}

function jsonValueType(value: unknown): string {
  if (value === null) return "null";
  if (Array.isArray(value)) return "array";
  if (isJsonObject(value)) return "object";
  if (typeof value === "number") return Number.isInteger(value) ? "integer" : "number";
  return typeof value;
}

function isJsonObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function resolveProject(context: DispatcherContext, project: string, options: ProjectResolutionOptions = {}): ProjectResolution {
  return context.registry?.resolveProject(project, options) ?? { status: "error", code: "project_not_found", matches: [] };
}

function collapsedStaleProjectKey(resolution: Extract<ProjectResolution, { status: "error" }>): string | undefined {
  if (!staleOnlyProjectResolution(resolution)) return undefined;
  const first = resolution.matches[0];
  if (!first) return undefined;
  if (resolution.matches.length === 1) return first.project;
  if (!first.canonical_project_root) return undefined;
  return resolution.matches.every((record) => record.project === first.project && record.canonical_project_root === first.canonical_project_root)
    ? first.project
    : undefined;
}

function staleOnlyProjectResolution(resolution: Extract<ProjectResolution, { status: "error" }>): boolean {
  return (resolution.code === "project_stale" || resolution.code === "duplicate_project_id")
    && resolution.matches.length > 0
    && resolution.matches.every((record) => record.stale);
}

function isOfflineProjectOpenCommand(commandId: string): boolean {
  return commandId === UNITY_PROJECT_OPEN_COMMAND || commandId === UNITY_PROJECT_OPEN_STATUS_COMMAND;
}

function resolveProjectKeyOrInput(context: DispatcherContext, project: string): string {
  const resolution = resolveProject(context, project);
  return resolution.status === "ok" ? resolution.record.project : project;
}

function projectResolutionError(project: string, resolution: Extract<ProjectResolution, { status: "error" }>, command?: string, requestId?: string, registry?: ProjectRegistry, commandArgs: Record<string, unknown> = {}, context?: DispatcherRuntimeContext, codeOverride?: Extract<ProjectResolution, { status: "error" }>["code"]): GatewayResult {
  const resolutionCode = codeOverride ?? resolution.code;
  const externalSceneBlocker = command && context && resolutionCode === "project_stale" ? externalSceneReloadBlockerBeforeResolution(context, resolution.matches, {
    project,
    projectSelector: project,
    command,
    requestId,
    writeRequestIssued: false,
  }) : undefined;
  if (externalSceneBlocker) return externalSceneBlocker;
  const recentReplacement = resolutionCode === "project_not_found" ? registry?.resolveRecentReplacementRecord(project) : undefined;
  const safeReplacement = recentReplacement?.status === "ok" ? recentProjectIdentity(recentReplacement.replacementRecord, false) : undefined;
  const matchesProjection = projectSelectorRecords(resolution.matches, selectorRecordContinuation(project, resolutionCode, resolution.matches));
  const currentCandidates = currentCandidateInstances(resolution.matches, registry, projectRegistrySnapshotForResolution(resolution));
  const compactNoLiveStale = resolutionCode === "project_stale"
    && command !== undefined
    && commandArgs.response_mode === "compact"
    && COMPACT_RESPONSE_COMMANDS.has(command)
    && currentCandidates.length === 0;
  const result = fail({
    ...optionalRequestId(requestId),
    ...(command ? { command } : {}),
    project,
    ...(resolutionCode === "unsafe_binding_refused" ? {
      next_valid_actions: ["call_sgg_unity_projects", "inspect_registry_operational_state", "retry_explicitly_after_registry_recovery"],
    } : {}),
    error: {
      code: resolutionCode,
      message: projectResolutionMessage(project, resolutionCode, resolution.matches),
      details: {
        ...(command ? { ...preDispatchNonDeliveryDetails(), bridge_request_issued: false } : {}),
        ...(resolution.code === "unsafe_binding_refused" ? {
          registry_operational_state: resolution.registryOperationalState,
          automatic_retry_allowed: false,
        } : {}),
        ...(compactNoLiveStale ? {
          compact_no_live_selector_projection: {
            matches_summary: matchesProjection.summary,
            continuation: selectorRecordContinuation(project, resolution.code),
          },
        } : {}),
        ...(resolution.matches.length > 0 ? { matches: matchesProjection.records, matches_summary: matchesProjection.summary } : {}),
        ...(command && resolutionCode === "project_stale" ? staleCommandRecoveryDetails(project, command, resolution, currentCandidates, requestId, commandArgs, context) : {}),
        ...(command && resolutionCode === "duplicate_project_id" ? duplicateProjectRecoveryDetails(project, command, resolution, commandArgs) : {}),
        ...(recentReplacement?.status === "ok" ? {
          recent_instance_replacement: {
            selector_strategy: "recent_exact_instance_identity",
            exact_selector_retained: true,
            automatic_reroute_attempted: false,
            failed_instance: recentReplacement.failedRecord,
            current_candidate_instance: safeReplacement!,
            current_candidate_selector: safeReplacement!.instance_id,
          },
          current_candidate_instance: safeReplacement!,
          current_candidate_selector: safeReplacement!.instance_id,
        } : {}),
        recovery_hint: projectResolutionRecoveryHint(project, resolutionCode, resolution.matches),
      },
    },
  });
  if (recentReplacement?.status === "ok") projectResolutionRecentReplacements.set(result, recentReplacement.replacementRecord);
  return result;
}

function selectorRecordContinuation(project: string, code: Extract<ProjectResolution, { status: "error" }>["code"], matches: PublicProjectRecord[] = []): SelectorRecordContinuation {
  const staleOnly = matches.length > 0 && matches.every((record) => record.stale);
  return code === "duplicate_project_id" && !staleOnly
    ? { tool: "sgg_unity_projects", args: {} }
    : { tool: "sgg_unity_stale_projects", args: { project, limit: 16 } };
}

function preDispatchNonDeliveryDetails(): Record<string, unknown> {
  return {
    command_delivery_state: "not_dispatched",
    delivery_state: "not_dispatched",
    queue_admitted: false,
    command_bridge_request_issued: false,
    command_replayed: false,
  };
}

function withCommandNotDispatched(result: GatewayResult): GatewayResult {
  if (result.status !== "error") return result;
  const normalized: GatewayResult = {
    ...result,
    error: {
      ...result.error,
      details: { ...result.error.details, ...preDispatchNonDeliveryDetails() },
    },
  };
  const retainedReplacement = projectResolutionRecentReplacements.get(result);
  if (retainedReplacement) projectResolutionRecentReplacements.set(normalized, retainedReplacement);
  const retainedRecoveryRecord = preDispatchRecoveryRecords.get(result);
  if (retainedRecoveryRecord) preDispatchRecoveryRecords.set(normalized, retainedRecoveryRecord);
  return normalized;
}

function withCommandBridgeRequestEvidence(result: GatewayResult, issued: boolean): GatewayResult {
  if (result.status !== "error") return result;
  const evidenced = {
    ...result,
    error: {
      ...result.error,
      details: { ...result.error.details, command_bridge_request_issued: issued },
    },
  };
  if (invalidTestRunObservationResults.has(result)) invalidTestRunObservationResults.add(evidenced);
  return evidenced;
}

function staleCommandRecoveryDetails(project: string, command: string, resolution: Extract<ProjectResolution, { status: "error" }>, currentCandidates: PublicProjectRecord[], requestId?: string, commandArgs: Record<string, unknown> = {}, context?: DispatcherRuntimeContext): Record<string, unknown> {
  const currentCandidateProjection = projectSelectorRecords(currentCandidates, { tool: "sgg_unity_projects", args: {} });
  const readRecoveryCommand = isReadRecoveryCommand(command);
  const diagnosticsAllowed = staleDiagnosticsAllowed(context, resolution.matches, project, command);
  const staleEditorObservation = readRecoveryCommand && diagnosticsAllowed
    ? staleEditorObservationDetails(resolution.matches, currentCandidates, context)
    : undefined;
  const nativeModalRecovery = diagnosticsAllowed
    ? staleNativeModalRecoveryDetails(resolution.matches, currentCandidates, context)
    : undefined;
  const readOnlyModalBlocked = ["blocked_scoped_registry_import", "blocked_scene_backup_recovery", "blocked_low_disk_space"].includes(String(nativeModalRecovery?.blocking_modal_code ?? ""));
  const explicitRepeatProject = currentCandidates.length === 1
    ? currentCandidates[0]?.instance_id
    : "<live_instance_id_or_canonical_project_root_from_sgg_unity_projects>";
  return {
    recovery_contract: STALE_COMMAND_RECOVERY_CONTRACT,
    project_selector: project,
    command,
    command_risk: readRecoveryCommand ? "known_read_status_recovery_command" : "unknown_until_catalog_refresh",
    command_risk_note: readRecoveryCommand
      ? "This command is a Gateway-classified bounded read/status/wait command; the Gateway may retry stale heartbeat recovery with a bounded budget."
      : "The project became stale before the Gateway could refresh the command catalog, so command risk was not resolved.",
    ...(testRunRecoveryDetails(project, command, commandArgs, resolution.code, resolution.matches, currentCandidates, readOnlyModalBlocked) ?? {}),
    auto_retry_eligible: readRecoveryCommand,
    auto_retry_attempted: false,
    explicit_repeat_required: !readRecoveryCommand && !readOnlyModalBlocked,
    explicit_repeat_deferred_until_modal_resolution: !readRecoveryCommand && readOnlyModalBlocked,
    explicit_repeat_request_id_required: !readRecoveryCommand && !readOnlyModalBlocked,
    explicit_repeat_request_id_recommended: !readOnlyModalBlocked,
    ...(requestId ? { original_request_id: requestId } : {}),
    ...preDispatchNonDeliveryDetails(),
    bridge_request_issued: false,
    repeatability_warning: readOnlyModalBlocked
      ? "Do not repeat the blocked test, compile, package, or other operation while this diagnostic-only modal evidence is present. After operator resolution, require a fresh heartbeat, catalog, and operation status before making a new explicit decision."
      : readRecoveryCommand
      ? "This stale recovery path is limited to bounded read/status/wait commands. Do not apply the same auto-retry assumption to write, execute, destructive, or network-risk commands."
      : "The command was rejected during project resolution and was not dispatched. Do not route it automatically to a replacement; repeat explicitly only if the operation is still intended.",
    stale_matched_instances: { count: resolution.matches.length, evidence_ref: "error.details.matches" },
    ...(resolution.matches.length === 1 ? { old_instance: selectorRecordReference(resolution.matches[0]!, "error.details.matches[0]") } : {}),
    ...(nativeModalRecovery ? { native_modal_recovery: nativeModalRecovery } : {}),
    ...(staleEditorObservation ? { stale_editor_observation: staleEditorObservation } : {}),
    ...(currentCandidates.length > 0 ? {
      current_candidate_instances: currentCandidateProjection.records,
      current_candidates_summary: currentCandidateProjection.summary,
      ...(currentCandidates.length === 1 ? {
        current_candidate_instance: selectorRecordReference(currentCandidates[0]!, "error.details.current_candidate_instances[0]"),
        current_candidate_selector: currentCandidates[0]?.instance_id,
      } : {}),
      current_candidate_note: readOnlyModalBlocked
        ? "Do not route or repeat the blocked operation while diagnostic-only modal evidence is present. After operator resolution, require a fresh heartbeat, catalog, and operation status before making a new explicit decision."
        : currentCandidates.length === 1
        ? "Use current_candidate_selector only after validating Unity state and explicitly repeating the original call if it is still intended."
        : "Multiple live candidates matched stale project metadata; call sgg_unity_projects and choose a unique instance_id or canonical project root before explicitly repeating.",
    } : {}),
    ...(!readRecoveryCommand && !readOnlyModalBlocked ? {
      dirty_scene_state: {
        stale_matched_instances: dirtySceneSummary(resolution.matches, "error.details.matches"),
        current_candidate_instances: dirtySceneSummary(currentCandidates, "error.details.current_candidate_instances"),
        note: "Dirty scene state comes from the latest heartbeat metadata and may be stale; inspect Unity state again before repeating write, execute, destructive, or network-risk commands.",
      },
      explicit_repeat_workflow: {
        required: true,
        request_id_required: true,
        request_id_note: "Use a new request_id for the explicit repeat; do not rely on the stale failed call as evidence that the operation did or did not run.",
        repeat_request_template: {
          tool: "sgg_unity_call",
          project: explicitRepeatProject,
          command,
          args_json: "<original args_json only if the operation is still intended>",
          request_id: "<new_unique_request_id>",
        },
      },
    } : {}),
    explicit_repeat_requirements: readOnlyModalBlocked
      ? [
        "do_not_repeat_blocked_test_compile_or_package_operation",
        "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution",
      ]
      : readRecoveryCommand
      ? [
        "call_sgg_unity_projects_and_choose_a_live_selector",
        "retry_read_status_command_after_heartbeat_recovers",
        "do_not_depend_on_gateway_auto_retry_for_unsafe_commands",
      ]
      : [
        "call_sgg_unity_projects_and_choose_a_live_selector",
        "inspect_dirty_scene_or_save_prompt_state_before_repeating_write_or_execute",
        "issue_a_new_explicit_sgg_unity_call_with_request_id_for_auditability",
        "do_not_depend_on_gateway_auto_retry_for_unsafe_commands",
      ],
    recovery_actions: [
      "call_sgg_unity_projects",
      ...(readRecoveryCommand ? ["use_current_live_project_selector_or_instance_id"] : []),
      ...(!readOnlyModalBlocked ? ["check_for_scene_save_prompt_with_editor.save_prompt.get", "close_scene_save_prompt_with_editor.save_prompt.respond_cancel_if_needed"] : []),
      "call_compile.status_or_compile.wait_ready_after_heartbeat_recovers",
      "optionally_call_editor.state.get_or_inspect_dirty_scenes_when_available",
      "call_diagnostics.unity_modal.status_when_available",
      "call_diagnostics.unity_crash_reporter.status_when_available",
      ...(readOnlyModalBlocked
        ? ["do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution"]
        : readRecoveryCommand ? ["retry_read_status_command_after_heartbeat_recovers"] : ["explicitly_repeat_original_call_only_if_still_intended"]),
    ],
  };
}

function staleObservationAllowed(context: DispatcherRuntimeContext | undefined, records: Array<Pick<PublicProjectRecord, "project">>, project: string, command: string): boolean {
  return isReadRecoveryCommand(command) && staleDiagnosticsAllowed(context, records, project, command);
}

function staleDiagnosticsAllowed(context: DispatcherRuntimeContext | undefined, records: Array<Pick<PublicProjectRecord, "project">>, project: string, command: string): boolean {
  if (!context) return false;
  refreshManagedPolicy(context);
  const risk = isReadRecoveryCommand(command) ? "read" : "write";
  if (context.policyManager?.degraded === true && risk !== "read") return false;
  const descriptor = {
    id: command,
    risk,
    source: "builtin",
    package: "com.sungeargames.unity-mcp",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  } as const;
  const projects = [...new Set(records.map((record) => record.project).filter(Boolean))];
  if (projects.length === 0) projects.push(project);
  return projects.every((candidate) => evaluatePolicy(context.policy, candidate, descriptor).action === "allow");
}

function staleEditorObservationDetails(staleMatches: PublicProjectRecord[], currentCandidates: PublicProjectRecord[], context?: DispatcherRuntimeContext, bootstrapObservation?: Record<string, unknown>): Record<string, unknown> | undefined {
  if (staleMatches.length !== 1) {
    return staleMatches.length > 1 ? {
      contract: "sgg.unity_mcp.stale_editor_observation.v1",
      state: "ambiguous_stale_instances",
      stale_instance_count: staleMatches.length,
      readiness_proven: false,
      import_completion_proven: false,
      current_main_thread_state_sampled: false,
    } : undefined;
  }

  const record = staleMatches[0]!;
  const status = safeUnityWindowStatus(context);
  const activeWindow = status?.active_unity_windows.find((window) => window.pid === record.unity_pid);
  const nativeModals = status?.native_modal_candidates.filter((modal) => modal.pid === record.unity_pid && modal.block_code).slice(0, 3) ?? [];
  const progressWindows = status?.native_progress_windows.filter((window) => window.pid === record.unity_pid).slice(0, 3) ?? [];
  const importProgress = progressWindows.some((window) => window.progress_kind === "asset_import");
  const compilationActive = typeof record.compilation_generation === "number"
    && typeof record.completed_compilation_generation === "number"
    && record.compilation_generation > record.completed_compilation_generation;
  const refreshLinked = recentRefreshLinkedToHeartbeat(record);
  const responding = activeWindow?.responding;
  const state = nativeModals.length > 0
    ? "native_modal_blocked"
    : responding === false
      ? "editor_not_responding"
      : importProgress && responding === true
        ? "responsive_asset_import_progress"
        : compilationActive && responding === true
          ? "responsive_compilation"
          : refreshLinked && responding === true
            ? "responsive_post_refresh_busy_uncertain"
            : responding === true
              ? "responsive_editor_busy_uncertain"
              : "editor_state_uncertain";
  const heartbeatAgeMs = heartbeatAgeMsFor(record, context);
  const lastRefreshMs = parseTimestampMs(record.last_asset_refresh_utc);
  const now = context?.now?.().getTime() ?? Date.now();

  return {
    contract: "sgg.unity_mcp.stale_editor_observation.v1",
    state,
    observed_instance_id: record.instance_id,
    observed_unity_pid: record.unity_pid,
    heartbeat_stale: true,
    heartbeat_age_ms: heartbeatAgeMs,
    last_observed_editor_status: record.status,
    last_observed_refresh_generation: typeof record.refresh_generation === "number" ? record.refresh_generation : 0,
    last_observed_asset_refresh_age_ms: lastRefreshMs === undefined ? null : Math.max(0, now - lastRefreshMs),
    last_observed_compilation_generation: typeof record.compilation_generation === "number" ? record.compilation_generation : 0,
    last_observed_completed_compilation_generation: typeof record.completed_compilation_generation === "number" ? record.completed_compilation_generation : 0,
    refresh_linked_to_last_heartbeat: refreshLinked,
    ordinary_compilation_observed: compilationActive,
    active_window_observed: activeWindow !== undefined,
    os_window_responding: responding ?? null,
    native_modal_observed: nativeModals.length > 0,
    native_asset_import_progress_observed: importProgress,
    native_progress_windows: progressWindows.map((window) => ({
      pid: window.pid,
      progress_kind: window.progress_kind,
      ...(window.busy_seconds !== undefined ? { busy_seconds: window.busy_seconds } : {}),
      is_stalled: window.is_stalled,
      diagnostic_only: true,
      automation_attempted: false,
      matched_keywords: window.matched_keywords.slice(0, 8),
    })),
    current_candidate_count: currentCandidates.length,
    current_main_thread_state_sampled: false,
    readiness_proven: false,
    import_completion_proven: false,
    automatic_refresh_or_import_retry_attempted: false,
    bridge_bootstrap_observation: bootstrapObservation ?? bridgeBootstrapObservationDetails(record, currentCandidates, context),
    next_valid_actions: observationNextActions(state),
  };
}

function recentRefreshLinkedToHeartbeat(record: PublicProjectRecord): boolean {
  if (typeof record.refresh_generation !== "number" || record.refresh_generation <= 0) return false;
  const refreshMs = parseTimestampMs(record.last_asset_refresh_utc);
  const heartbeatMs = parseTimestampMs(record.last_heartbeat);
  return refreshMs !== undefined && heartbeatMs !== undefined && heartbeatMs >= refreshMs && heartbeatMs - refreshMs <= 60_000;
}

function parseTimestampMs(value: unknown): number | undefined {
  if (typeof value !== "string" || value.length === 0 || value.length > 40) return undefined;
  const parsed = Date.parse(value);
  return Number.isFinite(parsed) ? parsed : undefined;
}

function observationNextActions(state: string): string[] {
  if (state === "responsive_asset_import_progress" || state === "responsive_post_refresh_busy_uncertain") {
    return ["wait_for_asset_import_or_update_to_finish", "retry_read_status_after_fresh_heartbeat", "do_not_repeat_assets.refresh", "do_not_kill_unity_from_gateway", "diagnostics.report_problem_if_progress_stalls"];
  }
  if (state === "responsive_compilation") return ["wait_for_compilation_to_finish", "retry_read_status_after_fresh_heartbeat", "do_not_repeat_compile_refresh"];
  if (state === "native_modal_blocked") return ["call_diagnostics.unity_modal.status", "resolve_exact_modal_with_operator", "retry_read_status_after_fresh_heartbeat"];
  if (state === "editor_not_responding") return ["inspect_unity_editor_process_and_windows", "do_not_kill_unity_from_gateway", "diagnostics.report_problem"];
  return ["call_sgg_unity_projects", "call_diagnostics.unity_modal.status", "retry_read_status_after_fresh_heartbeat", "do_not_repeat_refresh_or_import_work"];
}

function staleNativeModalRecoveryDetails(staleMatches: PublicProjectRecord[], currentCandidates: PublicProjectRecord[], context?: DispatcherRuntimeContext): Record<string, unknown> | undefined {
  const status = safeUnityWindowStatus(context);
  if (!status || (!status.blocking_modal_code && !status.blocking_progress_code)) {
    return undefined;
  }

  const candidateRecords = [...staleMatches, ...currentCandidates];
  const candidatePids = new Set(candidateRecords.map((record) => record.unity_pid).filter((pid): pid is number => typeof pid === "number" && Number.isInteger(pid) && pid > 0));
  const correlatedNativeModals = status.native_modal_candidates
    .filter((modal) => modal.block_code
      && (candidatePids.size === 0 || candidatePids.has(modal.pid))
      && (!["blocked_scoped_registry_import", "blocked_scene_backup_recovery", "blocked_low_disk_space"].includes(modal.block_code) || candidateRecords.some((record) => record.unity_pid === modal.pid
        && typeof record.unity_process_start_utc === "string"
        && record.unity_process_start_utc === modal.process_start_utc)))
    .slice(0, 10);
  const missingSignatureCandidates = status.missing_signature_modal_candidates
    .filter((modal) => candidatePids.size === 0 || candidatePids.has(modal.pid))
    .slice(0, 10);
  const correlatedProgressWindows = status.native_progress_windows
    .filter((window) => window.block_code && (candidatePids.size === 0 || candidatePids.has(window.pid)))
    .slice(0, 10);
  const correlatedModalCode = correlatedNativeModals[0]?.block_code
    ?? (status.blocking_modal_code === "blocked_missing_signature_packages" ? "blocked_missing_signature_packages" : undefined);

  if (correlatedNativeModals.length === 0 && missingSignatureCandidates.length === 0 && correlatedProgressWindows.length === 0 && status.blocking_modal_code !== "blocked_missing_signature_packages") {
    return undefined;
  }

  if (correlatedProgressWindows.length > 0 && !correlatedModalCode) {
    return {
      blocking_progress_code: status.blocking_progress_code,
      correlated_native_progress_windows: correlatedProgressWindows,
      likely_explains_project_stale: true,
      recovery_hint: "A visible Unity domain reload progress window appears stalled for this project. Do not click Cancel, kill Unity, or delete Library/cache files from the Gateway; ask the operator to inspect the visible progress window, then retry read/status commands only after a fresh ready heartbeat.",
      next_valid_actions: ["call_diagnostics.unity_modal.status", "operator_inspect_visible_unity_progress_window", "wait_briefly_then_recheck_heartbeat", "do_not_click_cancel_from_gateway", "do_not_kill_unity_from_gateway", "do_not_delete_library_or_cache_files", "call_diagnostics.report_problem"],
    };
  }

  return {
    blocking_modal_code: correlatedModalCode,
    ...(status.blocking_progress_code ? { blocking_progress_code: status.blocking_progress_code } : {}),
    correlated_native_modal_candidates: correlatedNativeModals,
    ...(missingSignatureCandidates.length > 0 ? { missing_signature_modal_candidates: missingSignatureCandidates } : {}),
    ...(correlatedProgressWindows.length > 0 ? { correlated_native_progress_windows: correlatedProgressWindows } : {}),
    likely_explains_project_stale: correlatedNativeModals.length > 0 || missingSignatureCandidates.length > 0 || correlatedProgressWindows.length > 0,
    recovery_hint: correlatedModalCode === "blocked_missing_signature_packages"
      ? "A Unity Missing Signature package prompt may be blocking the post-resolve bridge heartbeat. Use diagnostics.unity_modal.status, then diagnostics.unity_recovery_prompt.respond with action=close_missing_signature and dry_run:true to verify the exact dialog and Close button. Run the real close only after operator approval, then call sgg_unity_projects and compile.status/package.info again before claiming the resolve is verified."
      : correlatedModalCode === "blocked_scoped_registry_import"
        ? "One or more exact Unity Importing a scoped registry dialogs may explain the stale or pre-registration heartbeat. This evidence is diagnostic-only and does not prove the sole cause. Do not click Learn More or Close, focus the window, terminate or restart Unity, or repeat the blocked test, compile, or package operation. After operator resolution, require a fresh heartbeat, catalog, and exact operation status."
      : correlatedModalCode === "blocked_scene_backup_recovery"
        ? "One or more exact Unity Recovering Scene Backups dialogs may explain the stale or pre-registration heartbeat. This evidence is diagnostic-only and does not prove the sole cause. Do not click Yes or No, focus the window, terminate or restart Unity, or repeat the blocked test, compile, or package operation. After operator resolution, require a fresh heartbeat, catalog, and exact operation status."
      : correlatedModalCode === "blocked_low_disk_space"
        ? "One or more exact Unity Low disk space dialogs may explain the stale or pre-registration heartbeat. This evidence is diagnostic-only and does not prove the sole cause. Do not click, focus or close the dialog, free disk space, delete Library/cache files, terminate or restart Unity, or repeat the blocked test, compile, or package operation. After operator resolution, require a fresh heartbeat, catalog, and exact operation status."
      : "A native Unity modal may be blocking the stale project heartbeat. Resolve the exact modal first, then call sgg_unity_projects and repeat only safe read/status commands after a fresh heartbeat.",
    next_valid_actions: correlatedModalCode === "blocked_missing_signature_packages"
      ? ["call_diagnostics.unity_modal.status", "dry_run_diagnostics.unity_recovery_prompt.respond_close_missing_signature", "close_only_the_missing_signature_prompt_after_operator_review", "call_sgg_unity_projects_after_modal_resolution", "call_compile.status_after_heartbeat_recovers", "optionally_call_package.info_after_heartbeat_recovers"]
      : correlatedModalCode === "blocked_scoped_registry_import"
        ? ["call_diagnostics.unity_modal.status", "operator_review_scoped_registry_import_dialog", "do_not_click_learn_more_or_close_from_gateway", "do_not_focus_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution"]
      : correlatedModalCode === "blocked_scene_backup_recovery"
        ? ["call_diagnostics.unity_modal.status", "operator_review_scene_backup_recovery_dialog", "do_not_click_yes_or_no_from_gateway", "do_not_focus_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution"]
      : correlatedModalCode === "blocked_low_disk_space"
        ? ["call_diagnostics.unity_modal.status", "operator_review_low_disk_space_dialog", "do_not_click_focus_or_close_low_disk_dialog_from_gateway", "do_not_free_disk_or_delete_library_or_cache_from_gateway", "do_not_terminate_or_restart_unity", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution"]
      : ["call_diagnostics.unity_modal.status", "resolve_native_unity_modal", "call_sgg_unity_projects_after_modal_resolution"],
  };
}

function safeUnityWindowStatus(context?: DispatcherRuntimeContext, scanBudgetMs?: number): UnityWindowDiagnosticsStatus | undefined {
  if (!context?.unityWindowDiagnostics) {
    return undefined;
  }
  try {
    return context.unityWindowDiagnostics.status(scanBudgetMs === undefined ? undefined : { scan_budget_ms: scanBudgetMs });
  } catch {
    return undefined;
  }
}

async function startupBlockersForSnapshot(context: DispatcherRuntimeContext, records: ReadonlyArray<PublicProjectRecord>): Promise<Record<string, unknown>> {
  const runtime = context.unityWindowDiagnostics;
  const now = nowMs(context);
  const scanStartedAtMs = performance.now();
  if (!runtime) {
    return {
      schema_version: 1,
      scan_status: "unsupported",
      diagnostic_only: true,
      read_only: true,
      interaction_attempted: false,
      process_action_attempted: false,
      candidate_count: 0,
      candidates: [],
      candidates_truncated: false,
      zero_candidates_authoritative: false,
      errors: ["window_diagnostics_unavailable"],
      errors_truncated: false,
      next_valid_actions: ["operator_inspect_unity_editor_startup", "do_not_interact_with_unity_prompts_from_gateway", "call_diagnostics.report_problem"],
    };
  }

  let status: UnityWindowDiagnosticsStatus;
  let capturedAt = "";
  let scanSource: "fresh" | "cache" = "fresh";
  const cached = runtime.cachedStatus?.();
  const cachedAtMs = cached ? Date.parse(cached.captured_at) : Number.NaN;
  const cachedAgeMs = Number.isFinite(cachedAtMs) ? Math.max(0, now - cachedAtMs) : Number.POSITIVE_INFINITY;
  try {
    if (cached
      && cachedAgeMs <= STARTUP_BLOCKER_CACHE_MAX_AGE_MS
      && cached.status.scan_budget_ms <= STARTUP_BLOCKER_WINDOW_SCAN_BUDGET_MS) {
      status = cached.status;
      capturedAt = cached.captured_at;
      scanSource = "cache";
    } else {
      status = runtime.status({ scan_budget_ms: STARTUP_BLOCKER_WINDOW_SCAN_BUDGET_MS });
      const freshSnapshot = runtime.cachedStatus?.();
      capturedAt = freshSnapshot?.captured_at ?? new Date(nowMs(context)).toISOString();
    }
  } catch {
    return {
      schema_version: 1,
      scan_status: "failed",
      diagnostic_only: true,
      read_only: true,
      interaction_attempted: false,
      process_action_attempted: false,
      candidate_count: 0,
      candidates: [],
      candidates_truncated: false,
      zero_candidates_authoritative: false,
      scan_budget_ms: STARTUP_BLOCKER_TOTAL_SCAN_BUDGET_MS,
      errors: ["window_diagnostics_failed"],
      errors_truncated: false,
      next_valid_actions: ["operator_inspect_unity_editor_startup", "do_not_interact_with_unity_prompts_from_gateway", "call_diagnostics.report_problem"],
    };
  }

  const nativeModalBlockers = status.native_modal_candidates.filter((candidate) => typeof candidate.block_code === "string");
  const unclassifiedNativeModalCandidates = status.native_modal_candidates.filter((candidate) => typeof candidate.block_code !== "string");
  const nativeModalPids = new Set(nativeModalBlockers.map((candidate) => candidate.pid));
  const startupCandidates = [
    ...nativeModalBlockers.map((candidate) => ({
      blockerCode: candidate.block_code!,
      source: "native_modal" as const,
      pid: candidate.pid,
      processStartUtc: candidate.process_start_utc,
      authority: status.native_modal_scan_authoritative ? "authoritative" as const : "partial" as const,
    })),
    ...status.recovery_prompt_candidates.filter((candidate) => !nativeModalPids.has(candidate.pid)).map((candidate) => ({
      blockerCode: "blocked_unity_recovery_prompt",
      source: "top_level_recovery_prompt" as const,
      pid: candidate.pid,
      processStartUtc: undefined,
      authority: status.top_level_window_scan_authoritative ? "authoritative" as const : "partial" as const,
    })),
    ...status.domain_reload_progress_windows.filter((candidate) => candidate.is_stalled && typeof candidate.block_code === "string").map((candidate) => ({
      blockerCode: candidate.block_code!,
      source: "native_progress" as const,
      pid: candidate.pid,
      processStartUtc: undefined,
      authority: status.native_modal_scan_authoritative ? "authoritative" as const : "partial" as const,
    })),
  ];
  let processStatus: ReturnType<NonNullable<DispatcherRuntimeContext["unityProcessDiagnostics"]>["status"]> | undefined;
  let processScanBudgetMs = 0;
  let processScanElapsedMs = 0;
  const windowScanElapsedMs = scanSource === "cache" ? 0 : Math.max(0, status.scan_elapsed_ms);
  const elapsedBeforeProcessMs = Math.max(performance.now() - scanStartedAtMs, windowScanElapsedMs);
  const remainingScanBudgetMs = Math.max(0, STARTUP_BLOCKER_TOTAL_SCAN_BUDGET_MS - elapsedBeforeProcessMs);
  if (startupCandidates.length > 0 && context.unityProcessDiagnostics && remainingScanBudgetMs > 0) {
    try {
      processScanBudgetMs = Math.min(STARTUP_BLOCKER_PROCESS_SCAN_BUDGET_MS, remainingScanBudgetMs);
      const processScanStartedAtMs = performance.now();
      processStatus = context.unityProcessDiagnostics.status([...records], undefined, { scan_budget_ms: processScanBudgetMs });
      processScanElapsedMs = Math.max(0, performance.now() - processScanStartedAtMs);
    } catch {
      processStatus = undefined;
    }
  }

  const candidates = startupCandidates.slice(0, MAX_STARTUP_BLOCKER_CANDIDATES).map((candidate) => {
    const process = processStatus?.processes.find((entry) => entry.pid === candidate.pid
      && entry.role === "unity_editor"
      && candidate.processStartUtc !== undefined
      && sameUnityProcessStart(entry.process_start_utc, candidate.processStartUtc));
    const exactRecords = process?.canonical_project_root
      ? records.filter((record) => record.unity_pid === process.pid
        && record.canonical_project_root === process.canonical_project_root
        && sameUnityProcessStart(record.unity_process_start_utc, process.process_start_utc))
      : [];
    const exactProjects = [...new Set(exactRecords.map((record) => record.project))];
    const exactProject = exactProjects.length === 1 ? exactProjects[0] : undefined;
    return {
      blocker_code: candidate.blockerCode,
      source: candidate.source,
      authority: candidate.authority,
      diagnostic_only: true,
      read_only: true,
      interaction_supported: false,
      interaction_attempted: false,
      process_action_attempted: false,
      process: {
        pid: candidate.pid,
        generation_verified: process !== undefined,
      },
      project_correlation: exactProject
        ? {
          status: "exact",
          project: exactProject,
          evidence: "process_generation_and_command_line_canonical_root",
          matching_stale_record_count: exactRecords.filter((record) => record.stale).length,
        }
        : {
          status: "unbound",
          evidence: process ? "no_unique_registry_project_for_exact_process_root" : "exact_process_root_evidence_unavailable",
        },
    };
  });
  const processErrors = processStatus?.errors.map((error) => `process_scan:${error}`) ?? (processScanBudgetMs > 0
    ? ["process_scan:failed"]
    : startupCandidates.length > 0 && context.unityProcessDiagnostics ? ["process_scan:budget_exhausted"] : []);
  const allErrors = [...status.errors.map((error) => `window_scan:${error}`), ...processErrors]
    .map((error) => boundedCompactString(error, 160));
  const scanStatus = status.aggregate_scan_authoritative && unclassifiedNativeModalCandidates.length === 0
    ? "authoritative"
    : status.supported ? "partial" : status.errors.some((error) => error === "unsupported_platform") ? "unsupported" : "failed";
  const capturedAtMs = Date.parse(capturedAt);
  const scanAgeMs = Number.isFinite(capturedAtMs) ? Math.max(0, nowMs(context) - capturedAtMs) : 0;
  const totalScanElapsedMs = Math.max(performance.now() - scanStartedAtMs, windowScanElapsedMs + processScanElapsedMs);
  const safeModePresent = startupCandidates.some((candidate) => candidate.blockerCode === "blocked_safe_mode_prompt");
  const nextValidActions = safeModePresent
    ? ["operator_review_visible_safe_mode_prompt", "operator_choose_enter_safe_mode_or_ignore_after_review", "do_not_auto_click_enter_safe_mode_ignore_or_quit", "call_sgg_unity_projects_after_operator_resolution"]
    : startupCandidates.length > 0
      ? ["operator_review_visible_unity_startup_blocker", "do_not_click_focus_close_or_restart_unity_from_gateway", "call_sgg_unity_projects_after_operator_resolution"]
      : unclassifiedNativeModalCandidates.length > 0
        ? ["operator_review_unclassified_native_unity_modal", "do_not_treat_zero_candidates_as_authoritative", "do_not_click_focus_close_or_restart_unity_from_gateway", "call_diagnostics.report_problem"]
      : status.aggregate_scan_authoritative
        ? ["wait_for_fresh_unity_bridge_heartbeat", "operator_inspect_unity_editor_if_startup_remains_blocked"]
        : ["operator_inspect_unity_editor_startup", "do_not_treat_zero_candidates_as_authoritative", "call_diagnostics.report_problem"];

  return {
    schema_version: 1,
    scan_status: scanStatus,
    scan_source: scanSource,
    captured_at: capturedAt,
    scan_age_ms: scanAgeMs,
    cache_max_age_ms: STARTUP_BLOCKER_CACHE_MAX_AGE_MS,
    scan_budget_ms: STARTUP_BLOCKER_TOTAL_SCAN_BUDGET_MS,
    scan_elapsed_ms: totalScanElapsedMs,
    window_scan_budget_ms: scanSource === "cache" ? 0 : status.scan_budget_ms,
    process_scan_budget_ms: processScanBudgetMs,
    diagnostic_only: true,
    read_only: true,
    interaction_attempted: false,
    process_action_attempted: false,
    aggregate_scan_authoritative: status.aggregate_scan_authoritative,
    channels: {
      top_level_inventory: {
        status: status.semantic_channels.top_level_inventory.status,
        authoritative: status.semantic_channels.top_level_inventory.authoritative,
      },
      native_user32: {
        status: status.semantic_channels.native_user32.status,
        authoritative: status.semantic_channels.native_user32.authoritative,
      },
      native_uia_scoped_fallback: {
        status: status.semantic_channels.native_uia_scoped_fallback.status,
        authoritative: status.semantic_channels.native_uia_scoped_fallback.authoritative,
      },
    },
    process_correlation: {
      attempted: processScanBudgetMs > 0,
      supported: processStatus?.supported ?? false,
      authoritative: processStatus?.supported === true && processStatus.errors.length === 0 && !processStatus.scan_truncated,
      scan_truncated: processStatus?.scan_truncated ?? false,
    },
    candidate_count: startupCandidates.length,
    candidates,
    candidates_truncated: startupCandidates.length > candidates.length,
    zero_candidates_authoritative: startupCandidates.length === 0 && unclassifiedNativeModalCandidates.length === 0 && status.aggregate_scan_authoritative,
    errors: allErrors.slice(0, MAX_STARTUP_BLOCKER_ERRORS),
    errors_truncated: allErrors.length > MAX_STARTUP_BLOCKER_ERRORS,
    next_valid_actions: nextValidActions,
  };
}

type ExternalSceneReloadBlockerFields = {
  project: string;
  projectSelector: string;
  command: string;
  requestId?: string | undefined;
  writeRequestIssued: boolean;
};

function externalSceneReloadBlockerBeforeResolution(context: DispatcherRuntimeContext, records: Array<Pick<PublicProjectRecord, "project" | "unity_pid">>, fields: ExternalSceneReloadBlockerFields): GatewayResult | undefined {
  if (!preResolutionExternalSceneInspectionAllowed(context, records, fields)) return undefined;
  return externalSceneReloadBlockerResult(context, records, fields);
}

function preResolutionExternalSceneInspectionAllowed(context: DispatcherRuntimeContext | undefined, records: Array<Pick<PublicProjectRecord, "project">>, fields: Pick<ExternalSceneReloadBlockerFields, "project" | "command">): boolean {
  if (!context || !EXTERNAL_SCENE_RELOAD_INLINE_COMMANDS.has(fields.command)) return false;
  refreshManagedPolicy(context);
  const risk = fields.command === COMPILE_REFRESH_AND_WAIT_COMMAND ? "write" : "read";
  if (context.policyManager?.degraded === true && risk !== "read") return false;
  const descriptor = {
    id: fields.command,
    risk,
    source: "builtin",
    package: "com.sungeargames.unity-mcp",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  } as const;
  const projects = [...new Set(records.map((record) => record.project).filter(Boolean))];
  if (projects.length === 0) projects.push(fields.project);
  return projects.every((project) => evaluatePolicy(context.policy, project, descriptor).action === "allow");
}

function externalSceneReloadBlockerResult(context: DispatcherRuntimeContext, records: Array<Pick<PublicProjectRecord, "project" | "unity_pid">>, fields: ExternalSceneReloadBlockerFields): GatewayResult | undefined {
  if (!EXTERNAL_SCENE_RELOAD_INLINE_COMMANDS.has(fields.command) || !context.unityWindowDiagnostics) return undefined;
  const pids = [...new Set(records.map((record) => record.unity_pid).filter((pid): pid is number => typeof pid === "number" && Number.isInteger(pid) && pid > 0))];
  if (pids.length === 0) return undefined;
  let status: UnityWindowDiagnosticsStatus;
  try {
    status = context.unityWindowDiagnostics.status();
  } catch {
    return undefined;
  }
  return externalSceneReloadBlockerFromStatus(context, status, pids, fields);
}

async function externalSceneReloadBlockerResultAsync(context: DispatcherRuntimeContext, records: Array<Pick<PublicProjectRecord, "project" | "unity_pid">>, fields: ExternalSceneReloadBlockerFields): Promise<GatewayResult | undefined> {
  if (!EXTERNAL_SCENE_RELOAD_INLINE_COMMANDS.has(fields.command) || !context.unityWindowDiagnostics) return undefined;
  const pids = [...new Set(records.map((record) => record.unity_pid).filter((pid): pid is number => typeof pid === "number" && Number.isInteger(pid) && pid > 0))];
  if (pids.length === 0) return undefined;
  let status: UnityWindowDiagnosticsStatus;
  try {
    status = context.unityWindowDiagnostics.statusAsync
      ? await context.unityWindowDiagnostics.statusAsync()
      : context.unityWindowDiagnostics.status();
  } catch {
    return undefined;
  }
  return externalSceneReloadBlockerFromStatus(context, status, pids, fields);
}

function externalSceneReloadBlockerFromStatus(context: DispatcherRuntimeContext, status: UnityWindowDiagnosticsStatus, pids: number[], fields: ExternalSceneReloadBlockerFields): GatewayResult | undefined {
  const runtime = context.unityWindowDiagnostics;
  if (!runtime) return undefined;
  const capturedAt = runtime.cachedStatus?.()?.captured_at ?? new Date(nowMs(context)).toISOString();
  const capturedAtMs = Date.parse(capturedAt);
  const evidenceAgeMs = Number.isFinite(capturedAtMs) ? Math.max(0, nowMs(context) - capturedAtMs) : Number.POSITIVE_INFINITY;
  if (evidenceAgeMs > EXTERNAL_SCENE_RELOAD_MAX_EVIDENCE_AGE_MS) return undefined;

  const evidence = pids.flatMap((pid) => runtime.externalSceneReloadEvidence?.(pid)
    ?? externalSceneReloadEvidence(status, pid, capturedAt)
    ?? []);
  if (evidence.length !== 1) return undefined;
  return externalSceneReloadBlockedResult(fields, evidence[0] as UnityExternalSceneReloadEvidence, evidenceAgeMs, context.approvalAutoAllowKnownUnityPopups === true);
}

function externalSceneReloadBlockedResult(fields: ExternalSceneReloadBlockerFields, evidence: UnityExternalSceneReloadEvidence, evidenceAgeMs: number, popupPreapproved: boolean): GatewayResult {
  const responseArgs = {
    action: popupPreapproved ? "<reload_or_ignore_after_evidence_review>" : "<reload_or_ignore_after_operator_choice>",
    pid: evidence.pid,
    hwnd: evidence.hwnd,
    evidence_id: evidence.evidence_id,
  };
  return fail({
    ...optionalRequestId(fields.requestId),
    project: fields.project,
    command: fields.command,
    error: {
      code: "blocked_external_scene_reload",
      message: popupPreapproved ? "Unity is waiting for a supported response on externally modified open scene files" : "Unity is waiting for an operator decision on externally modified open scene files",
      details: {
        recovery_contract: EXTERNAL_SCENE_RELOAD_BLOCKER_CONTRACT,
        recovery_contract_version: 1,
        project_selector: fields.projectSelector,
        blocking_modal_code: "blocked_external_scene_reload",
        modal_evidence: evidence,
        evidence_age_ms: evidenceAgeMs,
        evidence_max_age_ms: EXTERNAL_SCENE_RELOAD_MAX_EVIDENCE_AGE_MS,
        pid_correlated: true,
        operator_action_required: !popupPreapproved,
        owner_preapproved_known_popup_responses: popupPreapproved,
        allowed_operator_actions: ["reload", "ignore"],
        default_behavior: popupPreapproved ? "review_exact_evidence_then_use_supported_response" : "diagnostic_only_wait_for_operator",
        automation_attempted: false,
        write_request_issued: fields.writeRequestIssued,
        write_execution_state: fields.writeRequestIssued ? "unknown_after_request_issued" : "not_issued_by_this_attempt",
        write_command_repeated: false,
        write_replay_allowed: false,
        response_command: {
          tool: "sgg_unity_call",
          project: fields.projectSelector,
          command: EXTERNAL_SCENE_RELOAD_RESPOND_COMMAND,
          args: responseArgs,
          requires_fresh_one_shot_operator_approval: !popupPreapproved,
          evidence_token_ttl_ms: EXTERNAL_SCENE_RELOAD_EVIDENCE_TOKEN_TTL_MS,
          evidence_token_single_use: true,
          permissive_policy_cannot_bypass_approval: true,
        },
        recovery_hint: popupPreapproved
          ? "The owner enabled known-popup responses. Review the exact evidence and choose Reload or Ignore through the dedicated response command without another human question, subject to deny/feature and exact single-use target checks. Do not repeat compile.refresh_and_wait. Require a fresh heartbeat and compile.status before claiming recovery; submission does not prove closure."
          : "Tell the operator that Unity is waiting on the exact Reload/Ignore popup. Do not choose for them or repeat compile.refresh_and_wait. If the operator explicitly selects one action, submit the exact ask-gated response command, then require a fresh heartbeat and compile.status before claiming recovery.",
      },
    },
    next_valid_actions: [
      ...(popupPreapproved ? ["use_owner_preapproved_dedicated_popup_response"] : [
        "ask_operator_to_choose_reload_or_ignore",
        "ask_gate_diagnostics.unity_external_scene_reload.respond",
        "do_not_auto_respond_external_scene_reload_prompt",
      ]),
      "do_not_repeat_compile.refresh_and_wait",
      "call_sgg_unity_projects_after_modal_resolution",
      "call_compile.status_after_fresh_heartbeat",
    ],
  });
}

function createExternalSceneReloadMonitor(context: DispatcherRuntimeContext, record: HeartbeatRecord, fields: ExternalSceneReloadBlockerFields): { signal: AbortSignal; blocker: () => GatewayResult | undefined; stop: () => void } | undefined {
  if (!EXTERNAL_SCENE_RELOAD_INLINE_COMMANDS.has(fields.command) || !context.unityWindowDiagnostics) return undefined;
  const controller = new AbortController();
  let stopped = false;
  let detected: GatewayResult | undefined;
  let timer: ReturnType<typeof setTimeout> | undefined;
  const poll = async () => {
    if (stopped) return;
    detected = await externalSceneReloadBlockerResultAsync(context, [record], fields);
    if (stopped) return;
    if (detected) {
      controller.abort();
      return;
    }
    timer = setTimeout(() => { void poll(); }, EXTERNAL_SCENE_RELOAD_POLL_INTERVAL_MS);
    timer.unref?.();
  };
  timer = setTimeout(() => { void poll(); }, EXTERNAL_SCENE_RELOAD_POLL_INTERVAL_MS);
  timer.unref?.();
  return {
    signal: controller.signal,
    blocker: () => detected,
    stop: () => {
      stopped = true;
      if (timer) clearTimeout(timer);
    },
  };
}

function safeCachedUnityWindowStatus(context?: DispatcherRuntimeContext): UnityWindowDiagnosticsSnapshot | undefined {
  if (!context?.unityWindowDiagnostics?.cachedStatus) {
    return undefined;
  }
  try {
    return context.unityWindowDiagnostics.cachedStatus();
  } catch {
    return undefined;
  }
}

function duplicateProjectRecoveryDetails(project: string, command: string, resolution: Extract<ProjectResolution, { status: "error" }>, commandArgs: Record<string, unknown>): Record<string, unknown> {
  const staleOnly = resolution.matches.length > 0 && resolution.matches.every((record) => record.stale);
  return {
    duplicate_project_recovery: {
      recovery_contract: "sgg.unity_mcp.duplicate_project_recovery.v2",
      project_selector: project,
      command,
      explicit_selector_required: true,
      auto_retry_eligible: false,
      auto_retry_attempted: false,
      matched_instances: { count: resolution.matches.length, evidence_ref: "error.details.matches" },
      current_candidate_selectors: { count: staleOnly ? 0 : resolution.matches.length, evidence_ref: staleOnly ? null : "error.details.matches" },
      selector_note: staleOnly
        ? "The Gateway will not choose between distinct stale Unity project identities or bind a stale heartbeat generation. Retry with one exact project ID or canonical_project_root after inspecting bounded stale records."
        : "The Gateway will not choose between duplicate live Unity instances. Retry with one instance_id or canonical_project_root after confirming which editor owns the intended operation.",
      recovery_actions: staleOnly
        ? ["call_sgg_unity_stale_projects", "choose_a_unique_project_id_or_canonical_project_root", "do_not_route_bridge_commands_to_stale_instances"]
        : [
          "call_sgg_unity_projects",
          "choose_a_unique_instance_id_or_canonical_project_root",
          "check_for_scene_save_prompt_with_editor.save_prompt.get",
          "close_scene_save_prompt_with_editor.save_prompt.respond_cancel_if_needed",
          ...(isTestRunRecoveryCommand(command) ? ["check_test_run_status_or_artifact_before_repeating_test_run"] : []),
        ],
    },
    ...(testRunRecoveryDetails(project, command, commandArgs, resolution.code, resolution.matches, resolution.matches) ?? {}),
  };
}

function testRunRecoveryDetails(project: string, command: string, commandArgs: Record<string, unknown>, resolutionCode: Extract<ProjectResolution, { status: "error" }>["code"], matchedInstances: PublicProjectRecord[], currentCandidates: PublicProjectRecord[], readOnlyModalBlocked = false): Record<string, unknown> | undefined {
  if (!isTestRunRecoveryCommand(command)) {
    return undefined;
  }

  const operationId = stringValue(commandArgs.operation_id);
  const candidateRecords = currentCandidates.length > 0 ? currentCandidates : matchedInstances;
  const candidatesReferenceMatches = candidateRecords.length === matchedInstances.length
    && candidateRecords.every((record, index) => record.instance_id === matchedInstances[index]?.instance_id);
  const candidateEvidenceRef = candidatesReferenceMatches ? "error.details.matches" : "error.details.current_candidate_instances";
  const retryProject = candidateRecords.length === 1
    ? candidateRecords[0]?.instance_id
    : "<unique_instance_id_or_canonical_project_root_from_sgg_unity_projects>";
  const statusArgsJson = operationId ? JSON.stringify({ operation_id: operationId }) : "{\"operation_id\":\"<operation_id>\"}";
  const artifactPath = operationId && isSafeTestRunOperationId(operationId)
    ? `Library/SGGUnityMcp/job-artifacts/test-runs/${operationId}.json`
    : undefined;
  const dirtyScenes = {
    matched_instances: dirtySceneSummary(matchedInstances, "error.details.matches"),
    current_candidate_instances: dirtySceneSummary(currentCandidates, candidateEvidenceRef),
  };

  return {
    test_run_recovery: {
      recovery_contract: TEST_RUN_RESCUE_CONTRACT,
      project_selector: project,
      command,
      resolution_code: resolutionCode,
      operation_id: operationId ?? null,
      known_operation_ids: operationId ? [operationId] : [],
      stale_operation_ids: operationId ? [operationId] : [],
      active_operation_ids: command === TEST_RUN_COMMAND && operationId ? [operationId] : [],
      ...(artifactPath ? { artifact_path: artifactPath, artifact_refs: [artifactPath] } : {}),
      current_candidate_selectors: {
        count: candidateRecords.length,
        evidence_ref: candidateEvidenceRef,
      },
      dirty_scene_state: dirtyScenes,
      ...(!readOnlyModalBlocked ? { save_prompt_recovery: {
        check_command: "editor.save_prompt.get",
        cancel_command: "editor.save_prompt.respond",
        cancel_args_json: "{\"action\":\"cancel\"}",
        hint: "If Unity is blocked by the Scene(s) Have Been Modified prompt, check the prompt and cancel it before retrying test status or starting another run.",
      } } : { modal_recovery_precedence: "read_only_native_modal_blocker" }),
      ...(operationId && !readOnlyModalBlocked ? {
        test_run_cancel_template: {
          tool: "sgg_unity_call",
          project: retryProject,
          command: TEST_RUN_CANCEL_COMMAND,
          args_json: JSON.stringify({ operation_id: operationId, reason: "cleanup stuck test.run after selector recovery" }),
        },
      } : {}),
      ...(command === TEST_RUN_STATUS_COMMAND ? {
        status_retry_template: {
          tool: "sgg_unity_call",
          project: retryProject,
          command: TEST_RUN_STATUS_COMMAND,
          args_json: statusArgsJson,
          mode: "wait",
        },
        status_retry_scope: "bounded_read_status_only",
      } : {
        explicit_repeat_workflow: {
          allowed: "explicit_only_after_status_artifact_and_save_prompt_checks",
          request_id_required: true,
          repeat_request_template: {
            tool: "sgg_unity_call",
            project: retryProject,
            command: TEST_RUN_COMMAND,
            args_json: "<original args_json only if the test run is still intended>",
            request_id: "<new_unique_request_id>",
          },
        },
      }),
      next_valid_actions: [
        "call_sgg_unity_projects",
        "choose_a_unique_instance_id_or_canonical_project_root",
        ...(!readOnlyModalBlocked ? ["check_for_scene_save_prompt_with_editor.save_prompt.get", "close_scene_save_prompt_with_editor.save_prompt.respond_cancel_if_needed"] : []),
        ...(operationId && !readOnlyModalBlocked ? ["cancel_or_cleanup_test_run_with_test.run.cancel"] : []),
        ...(operationId ? ["read_test_run_artifact_from_artifact_path"] : []),
        command === TEST_RUN_STATUS_COMMAND ? "retry_test_run_status_on_chosen_selector" : "do_not_repeat_test_run_until_status_or_artifact_is_checked",
      ],
      warning: command === TEST_RUN_COMMAND
        ? "test.run is execute/unsafe and is never silently repeated by selector recovery. Repeat explicitly only after verifying the prior run state."
        : "test.run.status is a bounded read/status command; retry only with a unique selector that owns the operation.",
    },
  };
}

function isTestRunRecoveryCommand(command: string): boolean {
  return command === TEST_RUN_COMMAND || command === TEST_RUN_STATUS_COMMAND;
}

function isSafeTestRunOperationId(operationId: string): boolean {
  return /^[A-Za-z0-9._-]+$/.test(operationId);
}

function dirtySceneState(record: PublicProjectRecord): Record<string, unknown> {
  return {
    instance_id: record.instance_id,
    project: record.project,
    stale: record.stale,
    has_unsaved_scene_changes: record.has_unsaved_scene_changes ?? null,
    dirty_scene_count: record.dirty_scene_count ?? null,
    dirty_scene_paths_omitted: true,
  };
}

function currentCandidateInstances(staleMatches: PublicProjectRecord[], registry?: ProjectRegistry, snapshot?: ProjectRegistrySnapshot): PublicProjectRecord[] {
  if (!registry || staleMatches.length === 0) {
    return [];
  }

  const rejectedInstanceIds = new Set(staleMatches.map((record) => record.instance_id));
  const liveRecords = (snapshot?.listProjects() ?? registry.listProjects()).filter((record) => !record.stale && !rejectedInstanceIds.has(record.instance_id));
  const rootMatches = liveRecords.filter((record) => staleMatches.some((staleMatch) => recordsShareCanonicalRoot(staleMatch, record)));
  return uniquePublicRecords(rootMatches.length > 0 ? rootMatches : liveRecords.filter((record) => staleMatches.some((staleMatch) => recordsShareProjectIdentity(staleMatch, record))));
}

function currentReplacementCandidateInstances(failedRecord: HeartbeatRecord, registry?: ProjectRegistry): PublicProjectRecord[] {
  if (!registry) {
    return [];
  }

  const failedPublicRecord = publicRecordFromHeartbeat(failedRecord, false);
  const liveRecords = registry.listProjects().filter((record) => !record.stale && record.instance_id !== failedRecord.instance_id);
  const rootMatches = liveRecords.filter((record) => recordsShareCanonicalRoot(failedPublicRecord, record));
  return uniquePublicRecords(rootMatches.length > 0 ? rootMatches : liveRecords.filter((record) => recordsShareProjectIdentity(failedPublicRecord, record)));
}

function publicRecordFromHeartbeat(record: HeartbeatRecord, stale: boolean): PublicProjectRecord {
  const { token: _token, ...publicRecord } = record;
  return { ...publicRecord, stale };
}

function recentProjectIdentity(record: HeartbeatRecord, stale: boolean): Record<string, unknown> & { instance_id: string } {
  return {
    instance_id: record.instance_id,
    project: record.project,
    bridge_port: record.bridge_port,
    last_heartbeat: record.last_heartbeat,
    status: record.status,
    stale,
  };
}

function failedBridgeInstanceDetails(record: HeartbeatRecord): Record<string, unknown> {
  return compactSelectorRecord(publicRecordFromHeartbeat(record, false));
}

function bridgeLifecycleRecovery(status: HeartbeatRecord["status"]): Record<string, unknown> | undefined {
  if (status === "starting") {
    return {
      status,
      retry_after_status: "ready",
      message: "The bridge instance is still starting and may not have opened its listener or command catalog yet.",
      next_valid_actions: ["wait_for_ready_heartbeat", "retry_after_ready_heartbeat", "inspect_unity_editor_for_startup_modal_or_freeze"],
    };
  }

  if (status === "reloading") {
    return {
      status,
      retry_after_status: "ready",
      message: "The failed bridge instance wrote a reloading heartbeat before Unity domain reload; its listener may already be closed.",
      next_valid_actions: ["wait_for_new_ready_heartbeat", "use_current_candidate_selector_when_available", "inspect_unity_editor_for_reload_modal_or_freeze"],
    };
  }

  if (status === "waiting_for_idle") {
    return {
      status,
      retry_after_status: "ready",
      message: "The failed bridge instance was deferring heavy startup until Unity finished compiling or importing.",
      next_valid_actions: ["wait_for_unity_import_or_compile_to_finish", "retry_after_ready_heartbeat", "inspect_unity_editor_for_modal_or_freeze"],
    };
  }

  if (status === "stopping") {
    return {
      status,
      retry_after_status: "new_instance_ready",
      message: "The failed bridge instance was stopping; wait for a replacement Unity instance heartbeat before retrying.",
      next_valid_actions: ["wait_for_replacement_instance", "use_current_candidate_selector_when_available"],
    };
  }

  return undefined;
}

function isTransientBridgeLifecycleStatus(status: HeartbeatRecord["status"]): boolean {
  return BRIDGE_LIFECYCLE_TRANSIENT_STATUSES.has(status);
}

function isLifecycleNormalizableBridgeErrorCode(code: ErrorCode): boolean {
  return code === "bridge_unreachable" || code === "bridge_protocol_error" || code === "bridge_timeout" || code === "unity_reloading";
}

function bridgeLifecycleGateResult(fields: BridgeErrorRecoveryContext, context?: DispatcherRuntimeContext, sourceError?: GatewayError): GatewayResult | undefined {
  if (fields.record?.catalog_status === "pending" || fields.record?.catalog_status === "unavailable") {
    const project = fields.project ?? fields.projectSelector ?? fields.record.project;
    const code = fields.record.catalog_status === "pending" ? "catalog_pending" : "catalog_unavailable";
    const nextValidActions = ["wait_for_catalog_ready_heartbeat", "call_sgg_unity_projects", "retry_only_as_a_new_explicit_call"];
    return fail({
      ...optionalRequestId(fields.requestId),
      project,
      ...(fields.command ? { command: fields.command } : {}),
      error: {
        code,
        message: code === "catalog_pending"
          ? `Unity bridge for project '${project}' has not published a routable command catalog`
          : `Unity bridge for project '${project}' no longer has a live listener for its published command catalog`,
        details: {
          lifecycle_status: fields.record.status,
          catalog_status: fields.record.catalog_status,
          catalog_generation: fields.record.catalog_generation ?? 0,
          catalog_hash_present: fields.record.commands_hash.length > 0 && fields.record.commands_hash !== "bootstrap-pending",
          bridge_port_positive: fields.record.bridge_port > 0,
          queue_admitted: false,
          bridge_request_issued: false,
          command_dispatched: false,
          automatic_retry_attempted: false,
          command_replayed: false,
          next_valid_actions: nextValidActions,
        },
      },
      next_valid_actions: nextValidActions,
    });
  }
  const externalSceneBlocker = fields.record && isTransientBridgeLifecycleStatus(fields.record.status) && context && fields.command ? externalSceneReloadBlockerBeforeResolution(context, [fields.record], {
    project: fields.project ?? fields.record.project,
    projectSelector: fields.projectSelector ?? fields.record.project,
    command: fields.command,
    requestId: fields.requestId,
    writeRequestIssued: false,
  }) : undefined;
  if (externalSceneBlocker) return externalSceneBlocker;
  if (fields.command === COMPILE_WAIT_READY_COMMAND && fields.record?.status === "playing") {
    const project = fields.project ?? fields.projectSelector ?? fields.record.project;
    const nextValidActions = [
      "wait_for_play_mode_to_end",
      "retry_compile.wait_ready_after_ready_heartbeat",
      "use_compile.status_for_non_waiting_compile_diagnostics",
    ];
    return fail({
      ...optionalRequestId(fields.requestId),
      project,
      command: fields.command,
      error: {
        code: "unity_busy",
        message: `Unity Editor for project '${project}' is in Play Mode; compile.wait_ready was not queued or dispatched`,
        details: {
          lifecycle_status: "playing",
          play_mode_state: "playing",
          ready: false,
          retryable: true,
          retry_after_status: "ready",
          reason: "play_mode_active",
          queue_admitted: false,
          bridge_request_issued: false,
          next_valid_actions: nextValidActions,
        },
      },
      next_valid_actions: nextValidActions,
    });
  }

  const error = bridgeLifecycleError(fields, context, sourceError);
  if (!error) {
    return undefined;
  }

  const project = fields.project ?? fields.projectSelector ?? fields.record?.project;
  return fail({
    ...optionalRequestId(fields.requestId),
    ...(project ? { project } : {}),
    ...(fields.command ? { command: fields.command } : {}),
    error,
  });
}

function bridgeLifecycleError(fields: BridgeErrorRecoveryContext, context?: DispatcherRuntimeContext, sourceError?: GatewayError): GatewayError | undefined {
  if (!fields.record || !isTransientBridgeLifecycleStatus(fields.record.status)) {
    return undefined;
  }

  const project = fields.project ?? fields.projectSelector ?? fields.record.project;
  const sourceDetails = sourceError?.details ?? {};
  const normalizedFrom = sourceError && sourceError.code !== "unity_reloading"
    ? { normalized_from_error: { code: sourceError.code, message: sourceError.message || sourceError.code } }
    : {};
  const captureState = screenshotCaptureInterruptionState(fields);
  return {
    code: "unity_reloading",
    message: captureState === "uncertain"
      ? `Unity bridge for project '${project}' is ${fields.record.status}; screenshot capture delivery is uncertain`
      : captureState === "not_scheduled"
        ? `Unity bridge for project '${project}' is ${fields.record.status}; screenshot capture was not scheduled`
        : `Unity bridge for project '${project}' is ${fields.record.status}; bridge command was not executed`,
    details: {
      ...sourceDetails,
      ...bridgeLifecycleRecoveryDetails(fields, context),
      ...(captureState ? screenshotCaptureRecoveryDetails(fields, captureState) : {}),
      ...normalizedFrom,
    },
  };
}

function bridgeLifecycleRecoveryDetails(fields: BridgeErrorRecoveryContext, context?: DispatcherRuntimeContext): Record<string, unknown> {
  const record = fields.record as HeartbeatRecord;
  const currentCandidates = currentReplacementCandidateInstances(record, fields.registry);
  const currentCandidateProjection = projectSelectorRecords(currentCandidates, { tool: "sgg_unity_projects", args: {} });
  const readRecoveryCommand = fields.command ? isReadRecoveryCommand(fields.command) : fields.selector !== undefined;
  const lifecycleRecovery = bridgeLifecycleRecovery(record.status);
  const heartbeatAgeMs = heartbeatAgeMsFor(record, context);
  const staleLifecycle = heartbeatAgeMs >= DEFAULT_READ_RECOVERY_TIMEOUT_MS;
  const retryProject = currentCandidates.length === 1 ? currentCandidates[0]?.instance_id : (fields.projectSelector ?? fields.project ?? record.project);
  const visibleProgressRecovery = visibleProgressRecoveryDetails([publicRecordFromHeartbeat(record, false), ...currentCandidates], context);
  const screenshotCaptureState = screenshotCaptureInterruptionState(fields);
  const commandNotDispatched = fields.command !== undefined && fields.bridgeRequestIssued !== true;
  const bridgeBootstrapObservation = bridgeBootstrapObservationDetails(record, currentCandidates, context);
  const staleEditorObservation = staleEditorObservationDetails([publicRecordFromHeartbeat(record, false)], currentCandidates, context, bridgeBootstrapObservation);
  const details: Record<string, unknown> = {
    recovery_contract: BRIDGE_LIFECYCLE_RECOVERY_CONTRACT,
    project_selector: fields.projectSelector ?? fields.project ?? record.project,
    lifecycle_status: record.status,
    retry_after_ms: BRIDGE_LIFECYCLE_RETRY_AFTER_MS,
    retry_after_status: lifecycleRecovery?.retry_after_status ?? "ready",
    heartbeat_age_ms: heartbeatAgeMs,
    heartbeat_age_limit_ms: DEFAULT_READ_RECOVERY_TIMEOUT_MS,
    stale_lifecycle: staleLifecycle,
    ...(commandNotDispatched ? { ...preDispatchNonDeliveryDetails(), bridge_request_issued: false } : {}),
    failed_instance: failedBridgeInstanceDetails(record),
    command_risk: fields.commandDescriptor?.risk ?? (readRecoveryCommand ? "known_read_status_recovery_command" : "unknown_until_catalog_refresh"),
    command_idempotency: fields.commandDescriptor?.idempotency ?? (readRecoveryCommand ? "safe_or_idempotent_read_status" : "unknown_until_catalog_refresh"),
    auto_retry_eligible: readRecoveryCommand,
    safe_to_auto_retry_on_single_current_candidate: readRecoveryCommand && currentCandidates.length === 1,
    explicit_repeat_required: !readRecoveryCommand,
    ...(fields.command ? { command: fields.command } : {}),
    ...(fields.selector ? { selector: fields.selector } : {}),
    ...(fields.requestId ? { original_request_id: fields.requestId } : {}),
    ...(lifecycleRecovery ? { lifecycle_recovery: lifecycleRecovery } : {}),
    ...(visibleProgressRecovery ? { visible_progress_recovery: visibleProgressRecovery } : {}),
    bridge_bootstrap_observation: bridgeBootstrapObservation,
    ...(staleEditorObservation ? { stale_editor_observation: staleEditorObservation } : {}),
    bridge_reload_trace_tail: bridgeReloadTraceTail(record),
    recovery_actions: screenshotCaptureState
      ? screenshotCaptureNextValidActions(screenshotCaptureState)
      : [
          "call_sgg_unity_projects",
          "wait_for_bridge_lifecycle_status_to_become_ready",
          "retry_after_short_delay",
          ...(currentCandidates.length === 1 ? ["retry_on_current_candidate_selector"] : []),
          ...(visibleProgressRecovery ? ["operator_inspect_visible_unity_progress_window", "do_not_click_cancel_from_gateway", "do_not_kill_unity_from_gateway", "do_not_delete_library_or_cache_files"] : []),
          "inspect_unity_editor_for_reload_modal_or_freeze",
        ],
    recovery_hint: screenshotCaptureState === "uncertain"
      ? "The screenshot request reached transport, but no terminal capture result was delivered. Preserve uncertain state, inspect any known capture status or artifact after Unity is ready, and do not repeat automatically."
      : screenshotCaptureState === "not_scheduled"
        ? "The screenshot request did not reach the bridge and was not scheduled. Wait for a fresh ready heartbeat; any repeat must be a new explicit operator action."
        : "Unity is in a transitional bridge lifecycle state. Wait for a ready heartbeat, then retry read/status commands; repeat write or execute commands only after validating Unity state.",
  };

  if (currentCandidates.length > 0) {
    details.current_candidate_instances = currentCandidateProjection.records;
    details.current_candidates_summary = currentCandidateProjection.summary;
    details.current_candidate_note = currentCandidates.length === 1
      ? "Use current_candidate_selector when retrying read/status recovery, or explicitly repeat unsafe commands only after validating Unity state."
      : "Multiple live candidates matched failed project metadata; call sgg_unity_projects and choose a unique instance_id or canonical project root before retrying.";
  }

  if (currentCandidates.length === 1) {
    details.current_candidate_instance = selectorRecordReference(currentCandidates[0]!, "error.details.current_candidate_instances[0]");
    details.current_candidate_selector = currentCandidates[0]?.instance_id;
  }

  if (fields.command && screenshotCaptureState !== "uncertain") {
    details.retry_request_template = {
      tool: "sgg_unity_call",
      project: retryProject,
      command: fields.command,
      args_json: "<same args_json only if the operation is still intended>",
      mode: "wait",
      ...(fields.requestId ? { request_id: "<new_unique_request_id>" } : {}),
    };
  } else if (fields.selector) {
    details.retry_request_template = {
      tool: "sgg_unity_describe",
      project: retryProject,
      selector: fields.selector,
    };
  }

  if (!readRecoveryCommand) {
    details.dirty_scene_state = {
      failed_instance: dirtySceneState(publicRecordFromHeartbeat(record, false)),
      current_candidate_instances: dirtySceneSummary(currentCandidates, "error.details.current_candidate_instances"),
      note: "Dirty scene state comes from heartbeat metadata and may be stale; inspect Unity state again before repeating write, execute, destructive, or network-risk commands.",
    };
    details.explicit_repeat_workflow = {
      required: true,
      request_id_required: true,
      ...(screenshotCaptureState === "uncertain"
        ? {
            repeat_blocked_until_delivery_state_checked: true,
            request_id_note: "The screenshot request reached transport, but delivery is uncertain. Do not repeat it unless known capture status or artifact evidence first resolves that uncertainty.",
          }
        : {
            request_id_note: "Use a new request_id for an explicit repeat; the reload-state response means the gateway did not dispatch the command to the bridge in this attempt.",
          }),
    };
  }

  return details;
}

type InterruptedScreenshotCaptureState = "not_scheduled" | "uncertain";

function screenshotCaptureInterruptionState(fields: BridgeErrorRecoveryContext): InterruptedScreenshotCaptureState | undefined {
  if (!fields.command || !SCREENSHOT_CAPTURE_COMMANDS.has(fields.command)) return undefined;
  if (fields.captureDefinitelyNotScheduled === true) return "not_scheduled";
  return fields.bridgeRequestIssued === true ? "uncertain" : "not_scheduled";
}

function screenshotDefinitelyNotScheduled(details: Record<string, unknown> | undefined): boolean {
  const mainThreadDispatch = isObjectRecord(details?.main_thread_dispatch) ? details.main_thread_dispatch : undefined;
  return details?.command_dispatched === false
    || details?.bridge_request_issued === false
    || details?.main_thread_state === "cancelled_before_start"
    || details?.execution_state === "cancelled_before_start"
    || mainThreadDispatch?.timed_out_work_state === "cancelled_before_start";
}

function bridgeRequestIssuedForError(error: unknown, attempted: boolean): boolean {
  return error instanceof BridgeRequestError && typeof error.details.bridge_request_issued === "boolean"
    ? error.details.bridge_request_issued
    : attempted;
}

function screenshotTransportInterruptionDetails(code: string, fields: BridgeErrorRecoveryContext): Record<string, unknown> | undefined {
  if (!["bridge_unreachable", "bridge_protocol_error", "bridge_timeout", "command_timeout", "unity_reloading"].includes(code)) {
    return undefined;
  }
  const captureState = screenshotCaptureInterruptionState(fields);
  return captureState ? screenshotCaptureRecoveryDetails(fields, captureState) : undefined;
}

function screenshotCaptureRecoveryDetails(fields: BridgeErrorRecoveryContext, captureState: InterruptedScreenshotCaptureState): Record<string, unknown> {
  return {
    screenshot_recovery_contract: SCREENSHOT_CAPTURE_RECOVERY_CONTRACT,
    capture_state: captureState,
    capture_state_values: ["not_scheduled", "scheduled", "running", "uncertain"],
    bridge_request_issued: fields.bridgeRequestIssued === true,
    automatic_retry_attempted: false,
    capture_replayed: false,
    explicit_operator_action_required: true,
    ...(captureState === "uncertain"
      ? {
          status_or_artifact_check_required_before_repeat: true,
          repeat_blocked_until_delivery_state_checked: true,
          recovery_hint: "A screenshot request reached transport without a terminal result. Inspect any known capture status or artifact after Unity is ready; do not repeat the capture while delivery remains uncertain.",
        }
      : {
          safe_to_repeat_automatically: false,
          recovery_hint: "The screenshot request was not issued and no capture was scheduled. Wait for Unity to become ready; any later capture must be a new explicit operator action.",
        }),
  };
}

function screenshotCaptureNextValidActions(captureState: InterruptedScreenshotCaptureState): string[] {
  return captureState === "uncertain"
    ? [
        "call_sgg_unity_projects",
        "wait_for_fresh_ready_heartbeat",
        "inspect_known_capture_status_or_project_local_artifact_if_available",
        "do_not_repeat_screenshot_while_delivery_is_uncertain",
      ]
    : [
        "call_sgg_unity_projects",
        "wait_for_fresh_ready_heartbeat",
        "operator_explicitly_repeat_screenshot_if_still_intended",
        "do_not_retry_screenshot_automatically",
      ];
}

function visibleProgressRecoveryDetails(records: PublicProjectRecord[], context?: DispatcherRuntimeContext): Record<string, unknown> | undefined {
  const status = safeUnityWindowStatus(context);
  if (!status?.blocking_progress_code) {
    return undefined;
  }

  const candidatePids = new Set(records.map((record) => record.unity_pid).filter((pid): pid is number => typeof pid === "number" && Number.isInteger(pid) && pid > 0));
  const correlatedProgressWindows = status.domain_reload_progress_windows
    .filter((window) => window.block_code && (candidatePids.size === 0 || candidatePids.has(window.pid)))
    .slice(0, 10);
  if (correlatedProgressWindows.length === 0) {
    return undefined;
  }

  return {
    blocking_progress_code: status.blocking_progress_code,
    correlated_native_progress_windows: correlatedProgressWindows,
    likely_explains_reload_lifecycle: true,
    recovery_hint: "A visible Unity domain reload progress window appears stalled for this bridge lifecycle. Recovery is diagnostic-only: do not click Cancel, kill Unity, delete Library/cache files, or repeat write/execute commands automatically.",
    next_valid_actions: ["call_diagnostics.unity_modal.status", "operator_inspect_visible_unity_progress_window", "wait_briefly_then_recheck_heartbeat", "do_not_click_cancel_from_gateway", "do_not_kill_unity_from_gateway", "do_not_delete_library_or_cache_files", "retry_read_status_after_fresh_ready_heartbeat", "call_diagnostics.report_problem"],
  };
}

function heartbeatAgeMsFor(record: HeartbeatRecord, context?: DispatcherRuntimeContext): number {
  const heartbeatMs = Date.parse(record.last_heartbeat);
  if (!Number.isFinite(heartbeatMs)) {
    return Number.POSITIVE_INFINITY;
  }

  return Math.max(0, (context?.now?.().getTime() ?? Date.now()) - heartbeatMs);
}

type BridgeReloadTraceRecord = Pick<HeartbeatRecord, "project_root" | "canonical_project_root" | "aliases" | "unity_pid" | "unity_process_start_utc" | "status">;
type BridgeReloadTraceTail = Record<string, unknown> & { readable?: boolean; lines?: string[] };

function bridgeBootstrapObservationDetails(record: BridgeReloadTraceRecord, currentCandidates: PublicProjectRecord[], context?: DispatcherRuntimeContext): Record<string, unknown> {
  const trace = bridgeReloadTraceTail(record);
  const processGenerationBound = validTraceProcessStart(record.unity_process_start_utc);
  const unregisteredProcesses = unregisteredReplacementProcesses(record, context);
  return {
    ...classifyBridgeBootstrapObservation(
    trace.readable === true,
    parseBridgeBootstrapTraceEvents(trace, record.unity_pid, record.unity_process_start_utc),
    record,
    currentCandidates,
    unregisteredProcesses,
    ),
    trace_process_generation_bound: processGenerationBound,
  };
}

function unregisteredReplacementProcesses(record: BridgeReloadTraceRecord, context?: DispatcherRuntimeContext): Array<{ unity_pid: number; unity_process_start_utc: string; status: "unregistered" }> {
  if (!context?.unityProcessDiagnostics || !context.registry) {
    return [];
  }
  if (context.internalDeadlineUnixMs !== undefined
    && context.internalDeadlineUnixMs - (context.now?.().getTime() ?? Date.now()) < BRIDGE_PROCESS_SCAN_MIN_REMAINING_MS) {
    return [];
  }

  try {
    const projects = context.registry.listProjects();
    const processStatus = context.unityProcessDiagnostics.status(projects);
    if (!processStatus.supported || processStatus.scan_truncated) {
      return [];
    }

    const roots = new Set([record.canonical_project_root, ...(record.aliases ?? []).map((alias) => alias.canonical_path)].filter((root): root is string => typeof root === "string" && root.length > 0));
    return processStatus.processes
      .filter((process) => process.role === "unity_editor"
        && validTraceProcessStart(process.process_start_utc ?? undefined)
        && !projects.some((project) => project.unity_pid === process.pid
          && (!validTraceProcessStart(project.unity_process_start_utc)
            || Date.parse(project.unity_process_start_utc) === Date.parse(process.process_start_utc!)))
        && process.canonical_project_root !== null
        && roots.has(process.canonical_project_root)
        && (process.pid !== record.unity_pid
          || (validTraceProcessStart(record.unity_process_start_utc)
            && Date.parse(process.process_start_utc!) !== Date.parse(record.unity_process_start_utc))))
      .slice(0, 4)
      .map((process) => ({ unity_pid: process.pid, unity_process_start_utc: process.process_start_utc!, status: "unregistered" as const }));
  } catch {
    return [];
  }
}

function parseBridgeBootstrapTraceEvents(trace: BridgeReloadTraceTail, unityPid: number, processStartUtc: string | undefined): Array<{ event: string }> {
  if (trace.readable !== true || !Array.isArray(trace.lines) || !validTraceProcessStart(processStartUtc)) return [];
  const events: Array<{ event: string }> = [];
  for (const line of trace.lines) {
    if (typeof line !== "string" || line.length === 0 || line.length > 8192) continue;
    try {
      const parsed = JSON.parse(line) as unknown;
      if (!isObjectRecord(parsed)
        || typeof parsed.event !== "string"
        || parsed.event.length === 0
        || parsed.event.length > 128
        || parsed.process_id !== unityPid
        || typeof parsed.process_start_utc !== "string"
        || !validTraceProcessStart(parsed.process_start_utc)
        || Date.parse(parsed.process_start_utc) !== Date.parse(processStartUtc)) {
        continue;
      }
      events.push({ event: parsed.event });
    } catch {
      // Legacy and partially-written trace lines remain diagnostic-only and are ignored.
    }
  }
  return events.slice(-BRIDGE_RELOAD_TRACE_LINES);
}

function validTraceProcessStart(value: string | undefined): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= 40 && Number.isFinite(Date.parse(value));
}

function bridgeReloadTraceTail(record: BridgeReloadTraceRecord): BridgeReloadTraceTail {
  for (const root of projectRootCandidates(record)) {
    const tracePath = path.join(root, BRIDGE_RELOAD_TRACE_RELATIVE_PATH);
    try {
      const stat = fs.lstatSync(tracePath);
      if (stat.isSymbolicLink()) {
        return { exists: true, project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH, readable: false, reason: "trace_path_symlink_refused" };
      }
      if (!stat.isFile()) continue;

      const tail = readBoundedTail(tracePath, BRIDGE_RELOAD_TRACE_BYTES, stat);
      if (!tail) {
        return { exists: true, project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH, readable: false, reason: "trace_identity_changed" };
      }
      const lines = tail.text.split(/\r?\n/).filter((line) => line.length > 0).slice(-BRIDGE_RELOAD_TRACE_LINES).map((line) => redactRecoveryText(line));
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
      };
    } catch {
      continue;
    }
  }

  return { exists: false, project_relative_path: BRIDGE_RELOAD_TRACE_ARTIFACT_PATH, reason: "trace_not_found" };
}

function projectRootCandidates(record: Pick<HeartbeatRecord, "project_root" | "aliases">): string[] {
  const candidates = new Set<string>();
  candidates.add(record.project_root);
  for (const alias of record.aliases) {
    candidates.add(alias.host_path);
  }
  return [...candidates].filter((candidate) => candidate.length > 0);
}

function readBoundedTail(filePath: string, maxBytes: number, expectedStat: fs.Stats): { text: string; bytesRead: number; truncatedByBytes: boolean } | undefined {
  const start = Math.max(0, expectedStat.size - maxBytes);
  const length = expectedStat.size - start;
  const fd = fs.openSync(filePath, "r");
  try {
    const openedStat = fs.fstatSync(fd);
    if (!sameTraceFileIdentity(expectedStat, openedStat)) return undefined;
    const buffer = Buffer.alloc(length);
    fs.readSync(fd, buffer, 0, length, start);
    return { text: buffer.toString("utf8"), bytesRead: length, truncatedByBytes: start > 0 };
  } finally {
    fs.closeSync(fd);
  }
}

function sameTraceFileIdentity(left: fs.Stats, right: fs.Stats): boolean {
  if (!left.isFile() || !right.isFile() || left.size !== right.size || left.mtimeMs !== right.mtimeMs) return false;
  if (left.ino !== 0 || right.ino !== 0) return left.ino === right.ino && left.dev === right.dev;
  return false;
}

function redactRecoveryText(value: string): string {
  return value
    .replace(/(authorization\s*[:=]\s*)(bearer\s+)?[^\s,;}]+/gi, "$1[REDACTED]")
    .replace(/((?:token|api[_-]?key|password|secret|cookie)\s*[:=]\s*)[^\s,;}]+/gi, "$1[REDACTED]")
    .replace(/("(?:token|api[_-]?key|password|secret|cookie|authorization)"\s*:\s*")[^"]*(")/gi, "$1[REDACTED]$2");
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

function projectResolutionMessage(project: string, code: Extract<ProjectResolution, { status: "error" }>["code"], matches: PublicProjectRecord[] = []): string {
  if (code === "unsafe_binding_refused") {
    return `Registry state is not safe to resolve project '${project}'`;
  }
  if (code === "duplicate_project_id") {
    if (matches.length > 0 && matches.every((record) => record.stale)) {
      return `Project '${project}' matches multiple stale Unity project identities`;
    }
    return `Project '${project}' has multiple live Unity instances`;
  }
  if (code === "project_stale") {
    return `Project '${project}' is stale`;
  }
  return `Project '${project}' is not registered`;
}

function projectResolutionRecoveryHint(project: string, code: Extract<ProjectResolution, { status: "error" }>["code"], matches: PublicProjectRecord[] = []): string {
  if (code === "unsafe_binding_refused") {
    return `Call sgg_unity_projects to inspect the registry state for '${project}', then retry explicitly only after registry_operational_state is ok.`;
  }
  if (code === "duplicate_project_id") {
    if (matches.length > 0 && matches.every((record) => record.stale)) {
      return "Call sgg_unity_stale_projects and retry with a unique project ID or canonical project root; stale heartbeat generations are not live Bridge routing targets.";
    }
    return "Call sgg_unity_projects and retry with a unique instance_id, display_name, or canonical project root.";
  }
  if (code === "project_stale") {
    return `Call sgg_unity_projects to inspect '${project}', check for the Unity 'Scene(s) Have Been Modified' save prompt or editor freeze, then wait for Unity to refresh its heartbeat or restart the Unity bridge.`;
  }
  return `Call sgg_unity_projects to verify '${project}' is registered, then use the listed project id, display name, instance id, or canonical root.`;
}

function bridgeUnavailable(project: string, command?: string, requestId?: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    ...(command ? { command } : {}),
    project,
    error: {
      code: "bridge_unreachable",
      message: `Bridge for project '${project}' is not configured`,
      details: { recovery_hint: "Call sgg_unity_projects to verify a live Unity bridge heartbeat, then retry after the bridge is configured." },
    },
  });
}

function commandNotFound(project: string, command: string, requestId?: string): GatewayResult {
  const replacement = COMMAND_REPLACEMENTS[command];
  const result = fail({
    ...optionalRequestId(requestId),
    project,
    command,
    error: {
      code: "command_not_found",
      message: replacement
        ? `Command '${command}' is not available for project '${project}'. Use '${replacement}' instead.`
        : `Command '${command}' is not available for project '${project}'`,
      ...(replacement ? {
        details: {
          requested_command: command,
          suggested_command: replacement,
          recovery_hint: `Describe command:${replacement}, then call '${replacement}' if it is present and allowed by policy.`,
        },
      } : {}),
    },
    ...(replacement ? { next_valid_actions: ["retry_with_suggested_command", "call_sgg_unity_describe_selector_commands"] } : {}),
  });
  preDispatchCommandNotFoundResults.add(result);
  return result;
}

function permissionDenied(project: string, command: string, requestId?: string, policyDecision?: AuditPolicyMetadata): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project,
    command,
    error: {
      code: "permission_denied",
      message: `Command '${command}' is blocked by Gateway policy, not by a Unity command failure`,
      details: {
        blocked_by: "gateway_policy",
        policy_action: "deny",
        ...(policyDecision?.reason ? { policy_reason: policyDecision.reason } : {}),
        ...(policyDecision?.pattern ? { policy_pattern: policyDecision.pattern } : {}),
        recovery_hint: "Adjust the Gateway policy for this project, then retry the same command; do not report this as a Unity command bug.",
      },
    },
  });
}

function approvalUnavailable(project: string, command: string, requestId?: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project,
    command,
    error: {
      code: "approval_unavailable",
      message: "Approval service is unavailable; authorization was denied",
      details: {
        blocked_by: "gateway_approval_service",
        recovery_hint: "Check the local Gateway approval key and store configuration, then retry the original call.",
      },
    },
  });
}

function confirmationRequired(project: string, command: string, requestId: string | undefined, risk: CommandDescriptor["risk"], argsHashValue: string, expiresAt: string, pending?: ApprovalPendingListEntry, invalidationReason?: "accepted_request_context_changed", approvalType: "unity_command" | "gateway_operation" = "unity_command", mandatoryHumanApproval = false): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project,
    command,
    error: {
      code: "confirmation_required",
      message: `Command '${command}' requires confirmation`,
      details: {
        approval_request: {
          approval_type: approvalType,
          project,
          command,
          risk,
          args_hash: argsHashValue,
          ...(requestId ? { request_id: requestId } : {}),
          scope: "single_call_only",
          expires_at: expiresAt,
          ...(pending ? {
            pending_id: pending.id,
            created_at: pending.created_at,
            updated_at: pending.updated_at,
            status: pending.status,
            action_hint: pending.action_hint,
          } : {}),
          ...(invalidationReason ? { invalidation_reason: invalidationReason } : {}),
        },
        approval_workflow: approvalWorkflowDetails(pending, approvalType, mandatoryHumanApproval),
      },
    },
  });
}

function approvalWorkflowDetails(pending?: ApprovalPendingListEntry, approvalType: "unity_command" | "gateway_operation" = "unity_command", mandatoryHumanApproval = false): Record<string, unknown> {
  return {
    contract: APPROVAL_WORKFLOW_CONTRACT,
    native_mcp_elicitation: {
      method: "elicitation/create",
      gateway_status: "implemented_for_clients_that_support_mcp_elicitation",
      opencode_status: "active_connection_capability_and_frontend_rendering_are_authoritative",
      upstream_opencode_v2_status: "released_2_0_25_forms_implemented_frontend_human_acceptance_requires_validation",
      requires: ["nested_request_capable_transport", "client_capabilities.elicitation.form", "client_approval_ui", "gateway_approval_store"],
      behavior: "When all requirements are met, Gateway asks the MCP client for user approval and retries the exact tool call after acceptance.",
    },
    fallback: pending ? {
      available: true,
      pending_id: pending.id,
      approve_command: pending.action_hint,
      retry_original_call_required: true,
      expires_at: pending.expires_at,
    } : {
      available: false,
      reason: "approval_store_unavailable_or_pending_request_not_created",
      retry_original_call_required: true,
    },
    policy_options: approvalType === "gateway_operation"
      ? [
          "keep_gateway_operation_ask_gated_and_approve_one_pending_request",
          "use_exact_stale_cleanup_no_elicitation_compatibility_only_when_explicitly_configured",
        ]
      // Mandatory human approval cannot be bypassed by these generic policy
      // workarounds. Reuse the actual permission gate, not a second allowlist.
      : mandatoryHumanApproval ? ["keep_command_ask_gated_and_approve_one_pending_request"] : [
          "keep_command_ask_gated_and_approve_one_pending_request",
          "apply_a_bounded_local_policy_profile_after_user_review",
          "use_approvals.auto_allow_ask_only_as_temporary_trusted_local_validation_workaround",
        ],
    forbidden_actions: [
      "do_not_add_or_use_self_approval_mcp_tool",
      "do_not_consume_or_create_approvals_through_mcp",
      "do_not_retry_unsafe_command_without_user_confirmation",
    ],
  };
}

function featureDisabled(project: string, command: string, featureFlag: string | undefined, requestId?: string): GatewayResult {
  return fail({
    ...optionalRequestId(requestId),
    project,
    command,
    error: {
      code: "feature_disabled",
      message: `Command '${command}' is disabled by feature flag`,
      ...(featureFlag ? { details: { feature_flag: featureFlag } } : {}),
    },
  });
}

function commandSchemaValidationFailed(project: string | undefined, projectSelector: string | undefined, command: string, requestId: string | undefined, issues: SchemaValidationIssue[], schema: unknown, commandArgs: Record<string, unknown>, descriptor: CommandDescriptor): GatewayResult {
  const hints = commandSchemaValidationHints(projectSelector, project, command, schema);
  const invalidArguments = descriptorValidationDetails(command, issues, descriptor, hints.recovery);
  const screenshotBackend = screenshotBackendValidationDetails(command, issues, schema, commandArgs, descriptor, hints.recovery);
  const assetImportRecovery = assetImportValidationDetails(command, issues, schema, commandArgs, descriptor, hints.recovery);
  return fail({
    ...optionalRequestId(requestId),
    ...safeRecoveryIdentity(hints.recovery),
    next_valid_actions: recoveryNextValidActions(hints.recovery),
    error: {
      code: "schema_validation_failed",
      message: safeSchemaRecoveryMessage("schema_validation_failed", hints.recovery.safeCommand),
      details: {
        validation_issue_count: Math.min(issues.length, MAX_SCHEMA_ISSUES),
        ...hints.details,
        ...(invalidArguments ? { invalid_arguments: invalidArguments } : {}),
        ...(screenshotBackend ? { invalid_argument: screenshotBackend } : {}),
        ...(assetImportRecovery ? { asset_import_recovery: assetImportRecovery } : {}),
      },
    },
  });
}

function descriptorValidationDetails(command: string, issues: SchemaValidationIssue[], descriptor: CommandDescriptor, recovery: SchemaRecoveryPlan): Record<string, unknown> | undefined {
  if (!isTrustedBuiltInDescriptor(descriptor)) return undefined;
  const safeIssues = issues.slice(0, MAX_SCHEMA_ISSUES).map(projectSchemaValidationIssue).filter((issue): issue is Record<string, unknown> => issue !== undefined);
  if (safeIssues.length === 0) return undefined;
  return {
    schema_version: 1,
    origin: "descriptor",
    issues: safeIssues,
    issues_truncated: safeIssues.length < issues.length,
    rejected_values_reflected: false,
    descriptor_source: `command:${command}`,
    descriptor_lookup: recovery.schemaLookup,
  };
}

function projectSchemaValidationIssue(issue: SchemaValidationIssue): Record<string, unknown> | undefined {
  const fieldNameOmitted = issue.issueCode === "unsupported_field";
  const path = safeValidationPath(fieldNameOmitted ? issue.path.slice(0, -1) : issue.path);
  if (!path) return undefined;
  const expected = descriptorExpectation(issue.valueSchema);
  return {
    path,
    issue_code: issue.issueCode,
    ...(issue.rejectedValueType ? { rejected_value_type: issue.rejectedValueType } : {}),
    rejected_value_reflected: false,
    ...(fieldNameOmitted ? { unsupported_field_name_reflected: false } : {}),
    ...(expected ? { accepted: expected } : {}),
  };
}

function descriptorExpectation(schema: Record<string, unknown> | undefined): Record<string, unknown> | undefined {
  if (!schema) return undefined;
  const expected: Record<string, unknown> = {};
  const types = jsonSchemaTypes(schema.type);
  if (types) expected.types = types;
  if (Array.isArray(schema.enum) && schema.enum.length <= 16) {
    const values = schema.enum.filter((value) => value === null || typeof value === "boolean" || typeof value === "number" || (typeof value === "string" && value.length <= 128));
    if (values.length === schema.enum.length) expected.values = values;
  }
  if (schema.additionalProperties === false && isJsonObject(schema.properties)) {
    const properties = Object.keys(schema.properties).filter((key) => /^[A-Za-z_][A-Za-z0-9_.-]{0,63}$/.test(key));
    if (properties.length === Object.keys(schema.properties).length && properties.length <= 64) expected.properties = properties;
  }
  if (typeof schema.pattern === "string" && schema.pattern.length <= 256) expected.pattern = schema.pattern;
  for (const key of ["minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength", "minItems", "maxItems", "minProperties", "maxProperties"] as const) {
    const value = schema[key];
    if (typeof value === "number" && Number.isFinite(value)) expected[key] = value;
  }
  return Object.keys(expected).length > 0 ? expected : undefined;
}

function safeValidationPath(path: Array<string | number>): Array<string | number> | undefined {
  if (path.length > 8) return undefined;
  const safe: Array<string | number> = [];
  for (const segment of path) {
    if (typeof segment === "number") {
      if (!Number.isInteger(segment) || segment < 0 || segment > 10_000) return undefined;
      safe.push(segment);
      continue;
    }
    if (!/^[A-Za-z_][A-Za-z0-9_.-]{0,63}$/.test(segment)) return undefined;
    safe.push(segment);
  }
  return safe;
}

function isTrustedBuiltInDescriptor(descriptor: CommandDescriptor): boolean {
  return descriptor.source === "builtin" && descriptor.trust === "builtin" && descriptor.package_id === "com.sungeargames.unity-mcp";
}

function assetImportValidationDetails(command: string, issues: SchemaValidationIssue[], schema: unknown, commandArgs: Record<string, unknown>, descriptor: CommandDescriptor, recovery: SchemaRecoveryPlan): Record<string, unknown> | undefined {
  if (command !== ASSET_IMPORT_COMMAND || !isTrustedBuiltInDescriptor(descriptor)) return undefined;
  if (!issues.some((issue) => issue.issueCode === "pattern_mismatch" && issue.path.length === 1 && issue.path[0] === "asset_path")) return undefined;
  if (!isJsonObject(schema) || !isJsonObject(schema.properties) || !isJsonObject(schema.properties.asset_path)) return undefined;
  const assetPathSchema = schema.properties.asset_path;
  if (assetPathSchema.type !== "string" || assetPathSchema.minLength !== 1 || assetPathSchema.pattern !== ASSET_IMPORT_PATH_PATTERN) return undefined;
  const rejectedPath = commandArgs.asset_path;
  if (typeof rejectedPath !== "string") return undefined;

  const packageScriptCandidate = rejectedPath.length <= 512 && /^Packages\/[^/]+\/.+[.]cs$/i.test(rejectedPath);
  const packageRecovery = packageScriptCandidate
    ? buildSafeExecutableSchemaRecovery({ resolvedProjectId: recovery.safeProject, command: LOCAL_PACKAGE_SCRIPT_IMPORT_COMMAND })
    : undefined;
  return {
    reason: "asset_path_must_be_under_assets",
    path: ["asset_path"],
    rejected_value_type: "string",
    rejected_value_reflected: false,
    accepted: { unity_relative_root: "Assets/", pattern: ASSET_IMPORT_PATH_PATTERN },
    automatic_retry_attempted: false,
    automatic_reroute_attempted: false,
    ...(packageRecovery
      ? {
          package_script_route: {
            command: LOCAL_PACKAGE_SCRIPT_IMPORT_COMMAND,
            eligibility: "registered_local_or_embedded_package_csharp_only",
            descriptor_lookup: packageRecovery.schemaLookup,
          },
        }
      : {}),
  };
}

function screenshotBackendValidationDetails(command: string, issues: SchemaValidationIssue[], schema: unknown, commandArgs: Record<string, unknown>, descriptor: CommandDescriptor, recovery: SchemaRecoveryPlan): Record<string, unknown> | undefined {
  if (command !== GAME_VIEW_SCREENSHOT_COMMAND || !issues.some((issue) => issue.path.length === 1 && issue.path[0] === "backend")) {
    return undefined;
  }
  if (!isTrustedBuiltInDescriptor(descriptor)) return undefined;
  if (!isJsonObject(schema) || !isJsonObject(schema.properties) || !isJsonObject(schema.properties.backend) || !Array.isArray(schema.properties.backend.enum)) {
    return undefined;
  }

  const descriptorValues = schema.properties.backend.enum;
  if (descriptorValues.length !== GAME_VIEW_SCREENSHOT_BACKENDS.length || !GAME_VIEW_SCREENSHOT_BACKENDS.every((value) => descriptorValues.includes(value))) return undefined;
  const acceptedValues = [...GAME_VIEW_SCREENSHOT_BACKENDS];
  const rejectedValue = commandArgs.backend;
  const rejectedValueType = rejectedValue === null ? "null" : Array.isArray(rejectedValue) ? "array" : typeof rejectedValue;
  return {
    path: ["backend"],
    issue_code: "enum_value_not_allowed",
    rejected_value_type: rejectedValueType,
    rejected_value_reflected: false,
    accepted_values: acceptedValues,
    descriptor_source: `command:${GAME_VIEW_SCREENSHOT_COMMAND}`,
    descriptor_lookup: recovery.schemaLookup,
    corrected_call_guidance: {
      requires_explicit_operator_choice: true,
      automatic_retry_attempted: false,
      instruction: "Choose one descriptor-backed backend value and issue a new explicit call. The rejected value is intentionally not reflected.",
      ...(recovery.safeProject
        ? {
            examples: acceptedValues.map((backend) => ({
              action_kind: "mcp_tool",
              tool: "sgg_unity_call",
              arguments: {
                project: recovery.safeProject,
                command: GAME_VIEW_SCREENSHOT_COMMAND,
                args_json: JSON.stringify({ backend }),
                mode: "wait",
              },
            })),
          }
        : {}),
    },
  };
}

function commandSchemaValidationHints(projectSelector: string | undefined, resolvedProjectId: string | undefined, command: string, schema: unknown): { details: Record<string, unknown>; recovery: SchemaRecoveryPlan } {
  if (!isJsonObject(schema) || !isJsonObject(schema.properties)) {
    const recovery = buildSafeExecutableSchemaRecovery({ projectSelector, resolvedProjectId, command });
    return {
      recovery,
      details: schemaRecoveryDetails(recovery),
    };
  }

  const recovery = buildSafeExecutableSchemaRecovery({ projectSelector, resolvedProjectId, command });

  return {
    recovery,
    details: schemaRecoveryDetails(recovery),
  };
}

function schemaRecoveryDetails(recovery: SchemaRecoveryPlan): Record<string, unknown> {
  return {
    schema_lookup: recovery.schemaLookup,
  };
}

function recoveryNextValidActions(recovery: SchemaRecoveryPlan): string[] {
  return recovery.actions.map((action) => action.tool);
}

function safeRecoveryIdentity(recovery: SchemaRecoveryPlan): Record<string, string> {
  return {
    ...(recovery.safeProject ? { project: recovery.safeProject } : {}),
    ...(recovery.safeCommand ? { command: recovery.safeCommand } : {}),
  };
}

function schemaUnavailable(project: string | undefined, projectSelector: string | undefined, command: string, requestId?: string): GatewayResult {
  const recovery = buildSafeExecutableSchemaRecovery({ projectSelector, resolvedProjectId: project, command });
  return fail({
    ...optionalRequestId(requestId),
    ...safeRecoveryIdentity(recovery),
    next_valid_actions: recoveryNextValidActions(recovery),
    error: {
      code: "schema_unavailable",
      message: safeSchemaRecoveryMessage("schema_unavailable", recovery.safeCommand),
      details: schemaRecoveryDetails(recovery),
    },
  });
}

function invalidCommandTimeout(project: string | undefined, projectSelector: string | undefined, command: string, _timeoutMs: number, requestId?: string): GatewayResult {
  const recovery = buildSafeExecutableSchemaRecovery({ projectSelector, resolvedProjectId: project, command });
  return fail({
    ...optionalRequestId(requestId),
    ...safeRecoveryIdentity(recovery),
    next_valid_actions: recoveryNextValidActions(recovery),
    error: {
      code: "invalid_field_value",
      message: safeSchemaRecoveryMessage("invalid_field_value", recovery.safeCommand),
      details: schemaRecoveryDetails(recovery),
    },
  });
}

function jobNotSupported(project: string, command: string, requestId?: string): GatewayResult {
  const details = command === "test.run"
    ? { hint: "test.run is not gateway job-capable. Call it with mode='wait', then poll test.run.status with the returned operation_id." }
    : undefined;
  return fail({
    ...optionalRequestId(requestId),
    project,
    command,
    error: {
      code: "job_not_supported",
      message: `Command '${command}' does not support job mode`,
      ...(details ? { details } : {}),
    },
  });
}

function jobNotFound(project: string, jobId: string): GatewayResult {
  return fail({
    project,
    error: {
      code: "job_not_found",
      message: `Job '${jobId}' is not registered`,
    },
  });
}

function optionalRequestId(requestId: string | undefined): { request_id: string } | Record<string, never> {
  return requestId ? { request_id: requestId } : {};
}

function optionalBridgeSettings(context: DispatcherContext, record: HeartbeatRecord): { settings?: BridgeInvokeSettings } {
  const settings: BridgeInvokeSettings = {};

  if (context.assetIndexCacheRoot) {
    settings.asset_index_cache_dir = path.join(context.assetIndexCacheRoot, projectCacheSegment(record));
  }

  if (context.features && Object.keys(context.features).length > 0) {
    settings.feature_flags = context.features;
  }

  if (context.monoBehaviourAllowlist && context.monoBehaviourAllowlist.length > 0) {
    settings.mono_behaviour_allowlist = context.monoBehaviourAllowlist;
  }

  return Object.keys(settings).length > 0 ? { settings } : {};
}

function projectCacheSegment(record: HeartbeatRecord): string {
  const hash = createHash("sha256").update(record.canonical_project_root).digest("hex").slice(0, 12);
  return `${safeProjectCacheSegment(record.project)}-${hash}`;
}

function safeProjectCacheSegment(project: string): string {
  const segment = project.replace(/[^A-Za-z0-9_-]+/g, "_").replace(/^_+|_+$/g, "");
  return segment || "project";
}

function normalizeInputError(error: unknown, rawArgs: unknown, toolName: ToolName, context: DispatcherRuntimeContext): GatewayResult {
  if (error instanceof Error) {
    if (error.message === "args_json_must_be_object") {
      return commandInputError("args_json_must_be_object", "args_json must parse to a JSON object", rawArgs, toolName, context);
    }
    if (error.message === "items_json_must_be_array") {
      return inputError("items_json_must_be_array", "items_json must parse to a JSON array");
    }
    if (error.message === "args_json_invalid_json" || error.message === "items_json_invalid_json") {
      return error.message === "args_json_invalid_json"
        ? commandInputError("invalid_json", error.message, rawArgs, toolName, context)
        : inputError("invalid_json", error.message);
    }
  }

  if (error instanceof ZodError) {
    const selectorHint = rawCommandSelectorHint(rawArgs, context);
    if (selectorHint) {
      return selectorHint;
    }

    return commandInputError("schema_validation_failed", "Tool arguments failed schema validation", rawArgs, toolName, context, {
      validation_issue_count: error.issues.length,
    });
  }

  return inputError("internal_error", "Unexpected gateway error");
}

type BridgeErrorRecoveryContext = {
  project?: string;
  projectSelector?: string;
  command?: string;
  selector?: string;
  requestId?: string | undefined;
  record?: HeartbeatRecord | undefined;
  registry?: ProjectRegistry | undefined;
  commandDescriptor?: CommandDescriptor | undefined;
  commandArgs?: Record<string, unknown> | undefined;
  bridgeRequestIssued?: boolean | undefined;
  captureDefinitelyNotScheduled?: boolean | undefined;
};

function normalizeBridgeError(error: GatewayError, fields: BridgeErrorRecoveryContext = {}, context?: DispatcherRuntimeContext): GatewayError {
  if (fields.command === TEST_RUN_COMMAND || fields.command === TEST_RUN_STATUS_COMMAND) {
    try {
      const details = safeTestRunErrorDetails(fields.command, error.details);
      error = { code: error.code, message: error.message, ...(details ? { details } : {}) };
    } catch { return invalidTestRunObservationError(fields.bridgeRequestIssued); }
  }
  const code: ErrorCode = isErrorCode(error.code) ? error.code : "unity_exception";
  const lifecycleError = isLifecycleNormalizableBridgeErrorCode(code) ? bridgeLifecycleError(fields, context, { ...error, code }) : undefined;
  if (lifecycleError) {
    return lifecycleError;
  }

  const screenshotRecovery = screenshotTransportInterruptionDetails(code, fields);
  const recoveryHint = isEditorWindowsListTimeout(fields.command, code) || screenshotRecovery ? undefined : bridgeRecoveryHint(code, fields.commandDescriptor);
  const recoveryDetails = bridgeRecoveryDetailsForCode(code, fields, error.details, context);
  return {
    code,
    message: error.message || code,
    ...(error.details || recoveryHint || recoveryDetails || screenshotRecovery ? { details: { ...(error.details ?? {}), ...(recoveryDetails ?? {}), ...(recoveryHint ? { recovery_hint: recoveryHint } : {}), ...(screenshotRecovery ?? {}) } } : {}),
  };
}

function bridgeThrownError(error: unknown, fields: BridgeErrorRecoveryContext = {}, context?: DispatcherRuntimeContext): GatewayResult | undefined {
  if (!(error instanceof Error) || !isBridgeThrownErrorCode(error.message)) {
    return undefined;
  }
  const code = error.message;
  let details: Record<string, unknown> | undefined;
  try { details = safeTestRunErrorDetails(fields.command, error instanceof BridgeRequestError ? error.details : undefined); }
  catch { return invalidTestRunObservationPayload(fields.project, fields.command!, fields.requestId, fields.bridgeRequestIssued); }
  const lifecycleError = isLifecycleNormalizableBridgeErrorCode(code) && !(code === "bridge_protocol_error" && isCompleteEmptyInvokeBridgeProtocolDetails(details))
    ? bridgeLifecycleError(fields, context, { code, message: error.message, ...(details ? { details } : {}) })
    : undefined;
  if (lifecycleError) {
    const result = fail({
      ...optionalRequestId(fields.requestId),
      ...(fields.project ? { project: fields.project } : {}),
      ...(fields.command ? { command: fields.command } : {}),
      error: lifecycleError,
    });
    return withSafeCompileProgressResult(fields.command ?? "", withoutUntrustedTestRunObservationResult(fields.command ?? "", result));
  }

  const screenshotState = screenshotCaptureInterruptionState(fields);
  const screenshotRecovery = screenshotTransportInterruptionDetails(error.message, fields);
  const recoveryHint = isEditorWindowsListTimeout(fields.command, error.message) || screenshotRecovery ? undefined : bridgeRecoveryHint(error.message, fields.commandDescriptor);
  const recoveryDetails = bridgeRecoveryDetailsForCode(error.message, fields, details, context);
  const timeoutActions = editorWindowsListTimeoutNextValidActions(fields.command, error.message);

  const result = fail({
    ...optionalRequestId(fields.requestId),
    ...(fields.project ? { project: fields.project } : {}),
    ...(fields.command ? { command: fields.command } : {}),
    error: {
      code: error.message,
      message: error.message,
      ...(details || recoveryHint || recoveryDetails || screenshotRecovery ? { details: { ...(details ?? {}), ...(recoveryDetails ?? {}), ...(recoveryHint ? { recovery_hint: recoveryHint } : {}), ...(screenshotRecovery ?? {}) } } : {}),
    },
    ...(error.message === "bridge_protocol_error"
      ? { next_valid_actions: screenshotState ? screenshotCaptureNextValidActions(screenshotState) : bridgeProtocolNextValidActions(details, fields.command === TEST_RUN_COMMAND && fields.commandDescriptor !== undefined && fields.commandDescriptor.risk !== "read") }
      : {}),
    ...(timeoutActions ? { next_valid_actions: timeoutActions } : {}),
  });
  return withSafeCompileProgressResult(fields.command ?? "", withoutUntrustedTestRunObservationResult(fields.command ?? "", result));
}

function bridgeRecoveryDetailsForCode(code: ErrorCode, fields: BridgeErrorRecoveryContext, details?: Record<string, unknown>, context?: DispatcherRuntimeContext): Record<string, unknown> | undefined {
  if (code === "bridge_unreachable") {
    return bridgeUnreachableRecoveryDetails(fields);
  }

  if (code === "bridge_protocol_error") {
    return bridgeProtocolRecoveryDetails(fields, details, context);
  }

  if (isEditorWindowsListTimeout(fields.command, code)) {
    return editorWindowsListTimeoutRecoveryDetails(code, fields, context);
  }

  if ((code === "bridge_timeout" || code === "command_timeout") && fields.command === TEST_RUN_COMMAND) {
    const operationId = testRunReservedOperationId(fields);
    if (operationId) return testRunTimeoutRecoveryDetails(fields, details, operationId);
  }

  if ((code === "bridge_timeout" || code === "command_timeout") && fields.commandDescriptor?.risk !== undefined && fields.commandDescriptor.risk !== "read") {
    return unsafeTimeoutRecoveryDetails(fields);
  }

  return undefined;
}

function isEditorWindowsListTimeout(command: string | undefined, code: string): boolean {
  return command === EDITOR_WINDOWS_LIST_COMMAND && (code === "bridge_timeout" || code === "command_timeout");
}

function editorWindowsListTimeoutNextValidActions(command: string | undefined, code: string): string[] | undefined {
  if (!isEditorWindowsListTimeout(command, code)) {
    return undefined;
  }

  return [
    "inspect_editor_windows_timeout_evidence",
    "call_sgg_unity_projects",
    "wait_for_fresh_ready_heartbeat",
    "operator_focus_existing_unity_window_if_needed",
    "retry_editor.windows.list_explicitly_once",
    "do_not_open_or_close_package_windows_automatically",
  ];
}

function editorWindowsListTimeoutRecoveryDetails(code: ErrorCode, fields: BridgeErrorRecoveryContext, context?: DispatcherRuntimeContext): Record<string, unknown> {
  const record = fields.record;
  const heartbeatAgeMs = record ? heartbeatAgeMsFor(record, context) : undefined;
  return {
    timeout_evidence_contract: EDITOR_WINDOWS_TIMEOUT_EVIDENCE_CONTRACT,
    timeout_source: code === "bridge_timeout" ? "gateway_bridge_transport" : "unity_command",
    command: EDITOR_WINDOWS_LIST_COMMAND,
    command_read_only: true,
    bounded_timeout_evidence: true,
    auto_retry_attempted: false,
    command_replayed: false,
    second_bridge_command_issued: false,
    window_automation_attempted: false,
    package_window_open_close_attempted: false,
    cached_heartbeat: record
      ? {
          available: true,
          source: "project_registry_record",
          instance_id: record.instance_id,
          unity_pid: record.unity_pid,
          lifecycle_status: record.status,
          bridge_port: record.bridge_port,
          last_heartbeat: record.last_heartbeat,
          heartbeat_age_ms: heartbeatAgeMs !== undefined && Number.isFinite(heartbeatAgeMs) ? Math.max(0, Math.floor(heartbeatAgeMs)) : null,
          ...(record.package_version ? { package_version: record.package_version } : {}),
        }
      : { available: false, source: "project_registry_record" },
    native_window_evidence: editorWindowsListNativeTimeoutEvidence(record, context),
    explicit_repeat_required: true,
    recovery_actions: editorWindowsListTimeoutNextValidActions(fields.command, code),
    recovery_hint: "Editor window inspection timed out. Review the cached heartbeat and local native-window evidence, wait for a fresh ready heartbeat, optionally ask the operator to focus the already-open Unity window, then retry editor.windows.list explicitly once. The Gateway did not replay the command or open, close, or refocus any window.",
  };
}

function editorWindowsListNativeTimeoutEvidence(record: HeartbeatRecord | undefined, context?: DispatcherRuntimeContext): Record<string, unknown> {
  const snapshot = safeCachedUnityWindowStatus(context);
  if (!snapshot) {
    return {
      available: false,
      cached_snapshot_available: false,
      scan_attempted_after_timeout: false,
      diagnostic_only: true,
      automation_attempted: false,
    };
  }

  const status = snapshot.status;
  const capturedAtMs = Date.parse(snapshot.captured_at);
  const snapshotAgeMs = Number.isFinite(capturedAtMs) ? Math.max(0, (context?.now?.().getTime() ?? Date.now()) - capturedAtMs) : null;
  const targetPid = typeof record?.unity_pid === "number" && Number.isInteger(record.unity_pid) && record.unity_pid > 0 ? record.unity_pid : undefined;
  const matchesTarget = (pid: number): boolean => targetPid === undefined || pid === targetPid;
  const activeWindowRows = status.active_unity_windows.slice(0, MAX_EDITOR_WINDOWS_TIMEOUT_CACHE_ROWS);
  const nativeModalRows = status.native_modal_candidates.slice(0, MAX_EDITOR_WINDOWS_TIMEOUT_CACHE_ROWS);
  const progressWindowRows = status.native_progress_windows.slice(0, MAX_EDITOR_WINDOWS_TIMEOUT_CACHE_ROWS);
  const activeWindows = activeWindowRows.filter((window) => matchesTarget(window.pid)).slice(0, 5);
  const nativeModals = nativeModalRows.filter((window) => matchesTarget(window.pid)).slice(0, 5);
  const progressWindows = progressWindowRows.filter((window) => matchesTarget(window.pid)).slice(0, 5);
  const classifications: string[] = [];
  if (nativeModals.some((window) => window.block_code)) classifications.push("blocking_native_modal");
  else if (nativeModals.length > 0) classifications.push("native_modal_candidate");
  if (progressWindows.some((window) => window.is_stalled)) classifications.push("stalled_native_progress");
  else if (progressWindows.length > 0) classifications.push("native_progress");
  if (activeWindows.length > 0) classifications.push("unity_window_present");
  if (classifications.length === 0) classifications.push("no_correlated_native_window_evidence");

  return {
    available: true,
    supported: status.supported,
    top_level_window_scan_authoritative: status.top_level_window_scan_authoritative,
    native_modal_scan_authoritative: status.native_modal_scan_authoritative,
    aggregate_scan_authoritative: status.aggregate_scan_authoritative,
    cached_snapshot_available: true,
    cached_snapshot_captured_at: snapshot.captured_at,
    cached_snapshot_age_ms: snapshotAgeMs,
    scan_attempted_after_timeout: false,
    bounded: true,
    cache_row_limit: MAX_EDITOR_WINDOWS_TIMEOUT_CACHE_ROWS,
    cache_scan_truncated: status.active_unity_windows.length > activeWindowRows.length
      || status.native_modal_candidates.length > nativeModalRows.length
      || status.native_progress_windows.length > progressWindowRows.length,
    diagnostic_only: true,
    automation_attempted: false,
    correlation: targetPid === undefined ? "unscoped_no_unity_pid" : "unity_pid",
    ...(targetPid === undefined ? {} : { target_unity_pid: targetPid }),
    observed_classifications: classifications,
    active_unity_windows_count: status.active_unity_windows_count,
    native_modal_candidates_count: status.native_modal_candidates_count,
    native_progress_windows_count: status.native_progress_windows_count,
    correlated_active_unity_windows: activeWindows.map((window) => ({
      pid: window.pid,
      image_name: boundedTimeoutEvidenceText(window.image_name, 80),
      window_title: boundedTimeoutEvidenceText(window.window_title, 300),
      likely_modal: window.likely_modal,
      likely_recovery_prompt: window.likely_recovery_prompt,
      matched_keywords: window.matched_keywords.slice(0, 10).map((value) => boundedTimeoutEvidenceText(value, 80)),
    })),
    correlated_native_modal_candidates: nativeModals.map((window) => ({
      hwnd: boundedTimeoutEvidenceText(window.hwnd, 40),
      pid: window.pid,
      process_name: boundedTimeoutEvidenceText(window.process_name, 80),
      class_name: boundedTimeoutEvidenceText(window.class_name, 120),
      window_title: boundedTimeoutEvidenceText(window.window_title, 300),
      ...(window.block_code ? { block_code: window.block_code } : {}),
      matched_keywords: window.matched_keywords.slice(0, 10).map((value) => boundedTimeoutEvidenceText(value, 80)),
    })),
    correlated_native_progress_windows: progressWindows.map((window) => ({
      hwnd: boundedTimeoutEvidenceText(window.hwnd, 40),
      pid: window.pid,
      process_name: boundedTimeoutEvidenceText(window.process_name, 80),
      class_name: boundedTimeoutEvidenceText(window.class_name, 120),
      window_title: boundedTimeoutEvidenceText(window.window_title, 300),
      progress_kind: window.progress_kind,
      ...(window.busy_seconds !== undefined ? { busy_seconds: window.busy_seconds } : {}),
      is_stalled: window.is_stalled,
      ...(window.block_code ? { block_code: window.block_code } : {}),
      matched_keywords: window.matched_keywords.slice(0, 10).map((value) => boundedTimeoutEvidenceText(value, 80)),
    })),
    errors: status.errors.slice(0, 5).map((value) => boundedTimeoutEvidenceText(value, 300)),
  };
}

function boundedTimeoutEvidenceText(value: string, maxChars: number): string {
  const redacted = redactRecoveryText(value);
  return redacted.length <= maxChars ? redacted : redacted.slice(0, maxChars);
}

function unsafeTimeoutRecoveryDetails(fields: BridgeErrorRecoveryContext): Record<string, unknown> {
  return {
    auto_retry_attempted: false,
    explicit_repeat_required: true,
    write_command_repeated: false,
    command_risk: fields.commandDescriptor?.risk,
    command_idempotency: fields.commandDescriptor?.idempotency,
    ...(fields.command ? { command: fields.command } : {}),
    ...(fields.requestId ? { original_request_id: fields.requestId } : {}),
    repeatability_warning: "The timed-out write, execute, destructive, or network command may have run. It was not repeated automatically; issue a new explicit request only after verifying current Unity state and confirming the operation is still intended.",
  };
}

function testRunReservedOperationId(fields: BridgeErrorRecoveryContext): string | undefined {
  const value = fields.commandArgs?._test_run_operation_id;
  return typeof value === "string" && /^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/.test(value) ? value : undefined;
}

function testRunTimeoutRecoveryDetails(fields: BridgeErrorRecoveryContext, details: Record<string, unknown> | undefined, operationId: string): Record<string, unknown> {
  const mainThreadDispatch = isObjectRecord(details?.main_thread_dispatch) ? details.main_thread_dispatch : undefined;
  const cancelledBeforeStart = mainThreadDispatch?.timed_out_work_state === "cancelled_before_start"
    && mainThreadDispatch.late_execution_possible === false;
  const transportNotIssued = details?.bridge_request_issued === false;
  const deliveryState = cancelledBeforeStart || transportNotIssued ? "not_dispatched" : "uncertain";
  return {
    ...testRunRecoveryEvidence(operationId, fields.projectSelector ?? fields.project, deliveryState, fields.bridgeRequestIssued === true),
    recovery_contract: TEST_RUN_TIMEOUT_RECOVERY_CONTRACT,
    timeout_source: details?.abort_source === "deadline" || details?.abort_source === "transport_timeout" || details?.abort_source === "endpoint_timeout"
      ? "gateway_bridge_transport"
      : "unity_command",
    unity_dispatch_state: cancelledBeforeStart ? "cancelled_before_start" : transportNotIssued ? "bridge_request_not_issued" : mainThreadDispatch?.timed_out_work_state ?? "unknown",
  };
}

function bridgeProtocolRecoveryDetails(fields: BridgeErrorRecoveryContext, details: Record<string, unknown> | undefined, context?: DispatcherRuntimeContext): Record<string, unknown> | undefined {
  const rawProtocolFailure = typeof details?.protocol_failure === "string" ? details.protocol_failure : undefined;
  const protocolFailure = isCompleteEmptyInvokeBridgeProtocolDetails(details) || rawProtocolFailure === "empty_invoke_response_body"
    ? "empty_response_body"
    : rawProtocolFailure ?? "invalid_bridge_response";
  const project = fields.projectSelector ?? fields.project ?? fields.record?.project ?? "<project_from_sgg_unity_projects>";
  const currentCandidates = fields.record ? currentReplacementCandidateInstances(fields.record, fields.registry) : [];
  const currentCandidateProjection = projectSelectorRecords(currentCandidates, { tool: "sgg_unity_projects", args: {} });
  const retryProject = currentCandidates.length === 1 ? currentCandidates[0]?.instance_id : project;
  const commandRisk = fields.commandDescriptor?.risk ?? "unknown_until_catalog_refresh";
  const commandIdempotency = fields.commandDescriptor?.idempotency ?? "unknown_until_catalog_refresh";
  const repeatRequiresDirtyStateCheck = commandRisk !== "read";
  const bridgeRequestIssued = fields.bridgeRequestIssued === true;
  const correlatedTestRun = fields.command === TEST_RUN_COMMAND && fields.commandDescriptor !== undefined && fields.commandDescriptor.risk !== "read";
  const uncertainTestRunDelivery = correlatedTestRun && bridgeRequestIssued;
  const uncertainScreenshotDelivery = screenshotCaptureInterruptionState(fields) === "uncertain";
  const recoveryDetails: Record<string, unknown> = {
    recovery_contract: BRIDGE_PROTOCOL_RECOVERY_CONTRACT,
    project_selector: project,
    protocol_failure: protocolFailure,
    auto_retry_attempted: false,
    explicit_repeat_required: true,
    ...(fields.commandDescriptor?.risk !== undefined && fields.commandDescriptor.risk !== "read"
      ? { write_command_repeated: false, command_replayed: false, bridge_request_issued: bridgeRequestIssued, delivery_state: bridgeRequestIssued ? "uncertain" : "not_dispatched" }
      : {}),
    ...(correlatedTestRun
      ? {
          test_run_delivery_state: bridgeRequestIssued ? "uncertain" : "not_dispatched",
          test_run_replayed: false,
          status_or_artifact_check_required_before_repeat: bridgeRequestIssued,
          ...(testRunReservedOperationId(fields)
            ? testRunRecoveryEvidence(testRunReservedOperationId(fields) as string, fields.projectSelector ?? fields.project, bridgeRequestIssued ? "uncertain" : "not_dispatched", bridgeRequestIssued)
            : {}),
        }
      : {}),
    top_level_mcp_recovery_tool: "sgg_unity_projects",
    unity_recovery_command: "compile.status",
    command_risk: commandRisk,
    command_idempotency: commandIdempotency,
    ...(fields.command ? { command: fields.command } : {}),
    ...(fields.selector ? { selector: fields.selector } : {}),
    ...(fields.requestId ? { original_request_id: fields.requestId } : {}),
    ...(fields.record
      ? {
          lifecycle_status: fields.record.status,
          heartbeat_age_ms: heartbeatAgeMsFor(fields.record, context),
          failed_instance: failedBridgeInstanceDetails(fields.record),
          bridge_reload_trace_tail: bridgeReloadTraceTail(fields.record),
        }
      : {}),
    recovery_call_sequence: [
      { tool: "sgg_unity_projects", args: {} },
      { tool: "sgg_unity_call", project, command: "compile.status", args_json: "{}", mode: "wait" },
      ...(uncertainTestRunDelivery || uncertainScreenshotDelivery
        ? []
        : [{ tool: "sgg_unity_call", project, command: "diagnostics.recovery.suggest", args_json: JSON.stringify({ error_code: "bridge_protocol_error", command: fields.command ?? "<command>" }), mode: "wait" }]),
    ],
    recovery_actions: uncertainScreenshotDelivery
      ? screenshotCaptureNextValidActions("uncertain")
      : bridgeProtocolNextValidActions({ ...(details ?? {}), protocol_failure: protocolFailure }, uncertainTestRunDelivery),
    recovery_hint: uncertainScreenshotDelivery
      ? "The screenshot response was not delivered, so capture state is uncertain. Inspect any known status or project-local artifact after Unity is ready, and do not repeat the screenshot automatically."
      : uncertainTestRunDelivery
      ? "The test.run response was not delivered, so execution state is uncertain. Do not repeat test.run automatically or explicitly until a known operation status or artifact has been checked; first verify the current project and Bridge lifecycle."
      : protocolFailure === "empty_response_body"
        ? "The Unity bridge returned HTTP 200 with an empty body. Call the top-level MCP tool sgg_unity_projects first, then use sgg_unity_call with compile.status on the selected project to verify the bridge before explicitly repeating the original command."
      : "The Unity bridge response was not valid invoke JSON. Verify the project with sgg_unity_projects, run compile.status through sgg_unity_call, then repeat the original command only if it is still intended.",
  };

  if (currentCandidates.length > 0) {
    recoveryDetails.current_candidate_instances = currentCandidateProjection.records;
    recoveryDetails.current_candidates_summary = currentCandidateProjection.summary;
    recoveryDetails.current_candidate_note = currentCandidates.length === 1
      ? "Use current_candidate_selector for recovery verification, or explicitly repeat the original command only after validating Unity state."
      : "Multiple live candidates matched failed project metadata; call sgg_unity_projects and choose a unique instance_id or canonical project root before repeating the original command.";
  }

  if (currentCandidates.length === 1) {
    recoveryDetails.current_candidate_instance = selectorRecordReference(currentCandidates[0]!, "error.details.current_candidate_instances[0]");
    recoveryDetails.current_candidate_selector = currentCandidates[0]?.instance_id;
  }

  if (fields.command && !uncertainTestRunDelivery && !uncertainScreenshotDelivery) {
    recoveryDetails.repeat_request_template = {
      tool: "sgg_unity_call",
      project: retryProject,
      command: fields.command,
      args_json: "<original args_json only if the operation is still intended>",
      mode: "wait",
      ...(fields.requestId ? { request_id: "<new_unique_request_id>" } : {}),
    };
  }

  if (fields.record && repeatRequiresDirtyStateCheck) {
    recoveryDetails.dirty_scene_state = {
      failed_instance: dirtySceneState(publicRecordFromHeartbeat(fields.record, false)),
      current_candidate_instances: dirtySceneSummary(currentCandidates, "error.details.current_candidate_instances"),
      note: "Dirty scene state comes from heartbeat metadata and may be stale; inspect Unity state again before explicitly repeating write, execute, destructive, or network-risk commands.",
    };
    recoveryDetails.explicit_repeat_workflow = {
      required: true,
      request_id_required: true,
      request_id_note: "Use a new request_id for the explicit repeat; do not rely on the failed protocol response as evidence that the operation did or did not run.",
      ...(uncertainTestRunDelivery
        ? {
            repeat_blocked_until_delivery_state_checked: true,
            note: "Inspect a known test operation status or artifact if available. If no operation identity was returned, preserve uncertain delivery and do not infer that the test did not run.",
          }
        : {}),
      verify_first: [
        { tool: "sgg_unity_projects", args: {} },
        { tool: "sgg_unity_call", project, command: "compile.status", args_json: "{}", mode: "wait" },
      ],
    };
  }

  return recoveryDetails;
}

function bridgeProtocolNextValidActions(details: Record<string, unknown> | undefined, uncertainTestRunDelivery = false): string[] {
  const emptyBody = details?.parse_error_kind === "empty_body" || details?.response_body_empty === true || details?.protocol_failure === "empty_invoke_response_body" || details?.protocol_failure === "empty_response_body";
  return [
    "call_sgg_unity_projects",
    "call_compile.status_with_sgg_unity_call",
    ...(emptyBody ? ["inspect_empty_bridge_response_body"] : ["inspect_bridge_protocol_error_details"]),
    ...(uncertainTestRunDelivery ? [] : ["call_diagnostics.recovery.suggest"]),
    ...(uncertainTestRunDelivery
      ? ["inspect_known_test_run_status_or_artifact_if_available", "do_not_repeat_test.run_until_delivery_state_is_resolved"]
      : ["retry_original_command_only_if_still_intended"]),
  ];
}

function bridgeUnreachableRecoveryDetails(fields: BridgeErrorRecoveryContext): Record<string, unknown> | undefined {
  if (!fields.record) {
    return undefined;
  }

  const currentCandidates = currentReplacementCandidateInstances(fields.record, fields.registry);
  const currentCandidateProjection = projectSelectorRecords(currentCandidates, { tool: "sgg_unity_projects", args: {} });
  const readRecoveryCommand = fields.command ? isReadRecoveryCommand(fields.command) : false;
  const uncertainScreenshotDelivery = screenshotCaptureInterruptionState(fields) === "uncertain";
  const lifecycleRecovery = bridgeLifecycleRecovery(fields.record.status);
  const explicitRepeatProject = currentCandidates.length === 1
    ? currentCandidates[0]?.instance_id
    : "<live_instance_id_or_canonical_project_root_from_sgg_unity_projects>";
  const details: Record<string, unknown> = {
    recovery_contract: BRIDGE_UNREACHABLE_RECOVERY_CONTRACT,
    project_selector: fields.projectSelector ?? fields.project ?? fields.record.project,
    failed_instance: failedBridgeInstanceDetails(fields.record),
    command_risk: fields.commandDescriptor?.risk ?? (readRecoveryCommand ? "known_read_status_recovery_command" : "unknown_until_catalog_refresh"),
    command_idempotency: fields.commandDescriptor?.idempotency ?? (readRecoveryCommand ? "safe_or_idempotent_read_status" : "unknown_until_catalog_refresh"),
    auto_retry_eligible: readRecoveryCommand,
    safe_to_auto_retry_on_single_current_candidate: readRecoveryCommand && currentCandidates.length === 1,
    explicit_repeat_required: !readRecoveryCommand,
    explicit_repeat_request_id_required: !readRecoveryCommand,
    explicit_repeat_request_id_recommended: true,
    ...(fields.command ? { command: fields.command } : {}),
    ...(fields.selector ? { selector: fields.selector } : {}),
    ...(fields.requestId ? { original_request_id: fields.requestId } : {}),
    repeatability_warning: uncertainScreenshotDelivery
      ? "The screenshot request reached transport, but no terminal result was delivered. Inspect any known capture status or project-local artifact; do not repeat while delivery remains uncertain."
      : readRecoveryCommand
      ? "This bridge_unreachable recovery path is limited to bounded read/status/wait commands. Do not apply the same auto-retry assumption to write, execute, destructive, or network-risk commands."
      : "Do not assume the prior call was safe to repeat automatically. Unsafe or non-idempotent Unity commands may have partially run before the bridge became unreachable; repeat explicitly only if the operation is still intended.",
    ...(lifecycleRecovery ? { lifecycle_recovery: lifecycleRecovery } : {}),
    recovery_actions: uncertainScreenshotDelivery
      ? screenshotCaptureNextValidActions("uncertain")
      : readRecoveryCommand
      ? ["call_sgg_unity_projects", ...(lifecycleRecovery ? ["wait_for_bridge_lifecycle_status_to_become_ready"] : []), "retry_read_status_on_current_candidate", "inspect_unity_editor_for_reload_modal_or_freeze"]
      : ["call_sgg_unity_projects", ...(lifecycleRecovery ? ["wait_for_bridge_lifecycle_status_to_become_ready"] : []), "call_compile.status_or_validation.preflight_on_current_candidate", "explicitly_repeat_original_call_only_if_still_intended"],
  };

  if (currentCandidates.length > 0) {
    details.current_candidate_instances = currentCandidateProjection.records;
    details.current_candidates_summary = currentCandidateProjection.summary;
    details.current_candidate_note = currentCandidates.length === 1
      ? uncertainScreenshotDelivery
        ? "Use current_candidate_selector only for recovery verification. Do not repeat the screenshot while capture delivery remains uncertain."
        : "Use current_candidate_selector for read/status recovery, or explicitly repeat unsafe commands only after validating Unity state."
      : "Multiple live candidates matched failed project metadata; call sgg_unity_projects and choose a unique instance_id or canonical project root before retrying.";
  }

  if (currentCandidates.length === 1) {
    details.current_candidate_instance = selectorRecordReference(currentCandidates[0]!, "error.details.current_candidate_instances[0]");
    details.current_candidate_selector = currentCandidates[0]?.instance_id;
  }

  if (!readRecoveryCommand && !uncertainScreenshotDelivery) {
    details.dirty_scene_state = {
      failed_instance: dirtySceneState(publicRecordFromHeartbeat(fields.record, false)),
      current_candidate_instances: dirtySceneSummary(currentCandidates, "error.details.current_candidate_instances"),
      note: "Dirty scene state comes from heartbeat metadata and may be stale; inspect Unity state again before repeating write, execute, destructive, or network-risk commands.",
    };
    details.explicit_repeat_workflow = {
      required: true,
      request_id_required: true,
      request_id_note: "Use a new request_id for the explicit repeat; do not rely on the unreachable failed call as evidence that the operation did or did not run.",
      repeat_request_template: {
        tool: "sgg_unity_call",
        project: explicitRepeatProject,
        command: fields.command ?? "<original command>",
        args_json: "<original args_json only if the operation is still intended>",
        request_id: "<new_unique_request_id>",
      },
    };
  }

  if (uncertainScreenshotDelivery) {
    details.explicit_repeat_workflow = {
      required: true,
      repeat_blocked_until_delivery_state_checked: true,
      note: "Inspect a known capture status or project-local artifact if available. If no capture identity was returned, preserve uncertain delivery and do not infer that the screenshot did not run.",
    };
  }

  return details;
}

function bridgeRecoveryHint(code: ErrorCode, commandDescriptor?: CommandDescriptor): string | undefined {
  switch (code) {
    case "bridge_unreachable":
      return "Call sgg_unity_projects to verify the Unity bridge heartbeat, then retry after the bridge is reachable.";
    case "bridge_timeout":
      return commandDescriptor?.supports_job === true
        ? "Call sgg_unity_projects to verify the Unity bridge is live, then retry or use mode='job' for long-running work."
        : "Call sgg_unity_projects to verify the Unity bridge is live, then follow the command-specific delivery evidence before retrying.";
    case "bridge_protocol_error":
      return "Call the top-level MCP tool sgg_unity_projects, verify the selected project with sgg_unity_call command=compile.status, then repeat the original command only if it is still intended.";
    case "protocol_version_mismatch":
      return "Update the Gateway and Unity package to matching protocol versions, then restart both sides. This request was not retried.";
    case "invalid_token":
      return "Restart the gateway or Unity bridge so both sides use the same local bridge token.";
    case "command_timeout":
      return commandDescriptor?.supports_job === true
        ? "Retry after Unity is idle, or use mode='job' for this job-capable command."
        : "Retry after Unity is idle and follow the command-specific delivery evidence before repeating the command.";
    default:
      return undefined;
  }
}

function isBridgeThrownErrorCode(value: string): value is (typeof BRIDGE_THROWN_ERROR_CODES)[number] {
  return (BRIDGE_THROWN_ERROR_CODES as readonly string[]).includes(value);
}

function rawCommandSelectorHint(rawArgs: unknown, context: DispatcherRuntimeContext): GatewayResult | undefined {
  if (!isJsonObject(rawArgs) || typeof rawArgs.project !== "string") {
    return undefined;
  }

  const selector = rawArgs.selector;
  if (typeof selector !== "string" || !/^[A-Za-z0-9_.-]+$/.test(selector) || !selector.includes(".")) {
    return undefined;
  }

  const availableProject = availablePreResolutionProject(rawArgs.project, context);
  const recovery = buildSafeExecutableSchemaRecovery({ ...availableProject, command: selector });

  return fail({
    ...safeRecoveryIdentity(recovery),
    error: {
      code: "selector_not_supported",
      message: `Selector '${selector}' is not supported. Use selector='command:${selector}' to describe a command schema.`,
      details: {
        selector,
        normalized_selector: `command:${selector}`,
        hint: "Use command:<id> for command schema lookup, then construct a new explicit corrected sgg_unity_call.",
        ...schemaRecoveryDetails(recovery),
      },
    },
    next_valid_actions: recoveryNextValidActions(recovery),
  });
}

function commandInputError(
  code: SchemaRecoveryErrorCode,
  _message: string,
  rawArgs: unknown,
  toolName: ToolName,
  context: DispatcherRuntimeContext,
  details: Record<string, unknown> = {},
): GatewayResult {
  if (toolName !== "sgg_unity_call" || !isJsonObject(rawArgs)) {
    return inputError(code, _message, details);
  }

  const recovery = buildSafeExecutableSchemaRecovery({
    ...availablePreResolutionProject(rawArgs.project, context),
    command: rawArgs.command,
  });

  return fail({
    ...safeRecoveryIdentity(recovery),
    next_valid_actions: recoveryNextValidActions(recovery),
    error: {
      code,
      message: safeSchemaRecoveryMessage(code, recovery.safeCommand),
      details: { ...trustedPreDispatchSchemaMetadata(details), ...schemaRecoveryDetails(recovery) },
    },
  });
}

function availablePreResolutionProject(value: unknown, context: DispatcherRuntimeContext): { projectSelector?: string; resolvedProjectId?: string } {
  const projectSelector = safePublicProjectSelector(value);
  if (!projectSelector) return {};
  const resolution = resolveProject(context, projectSelector);
  return resolution.status === "ok" ? { projectSelector, resolvedProjectId: resolution.record.project } : {};
}

function withCommandSchemaRecovery(result: GatewayResult, projectSelector: string | undefined, resolvedProjectId: string | undefined, command: string, descriptor: CommandDescriptor, commandArgs: Record<string, unknown>): GatewayResult {
  if (result.status !== "error" || !isSchemaRecoveryErrorCode(result.error.code)) {
    return result;
  }

  const recovery = buildSafeExecutableSchemaRecovery({
    projectSelector,
    resolvedProjectId,
    command,
  });
  const runtimeValidation = runtimeValidationDetails(result.error.details, command, descriptor, commandArgs, recovery);
  const { project: _unsafeProject, command: _unsafeCommand, ...safeResult } = result;

  return {
    ...safeResult,
    ...safeRecoveryIdentity(recovery),
    next_valid_actions: recoveryNextValidActions(recovery),
    error: {
      code: result.error.code,
      message: safeSchemaRecoveryMessage(result.error.code, recovery.safeCommand),
      details: {
        ...schemaRecoveryDetails(recovery),
        ...(runtimeValidation ? { invalid_arguments: runtimeValidation } : {}),
      },
    },
  };
}

function runtimeValidationDetails(details: Record<string, unknown> | undefined, command: string, descriptor: CommandDescriptor, commandArgs: Record<string, unknown>, recovery: SchemaRecoveryPlan): Record<string, unknown> | undefined {
  if (!isTrustedBuiltInDescriptor(descriptor) || !isJsonObject(details) || !isJsonObject(details.runtime_validation)) return undefined;
  if (!isJsonObject(descriptor.schema)) return undefined;
  const normalizedDescriptorSchema = normalizeJsonSchema(descriptor.schema, descriptor.schema, 0);
  if (!normalizedDescriptorSchema) return undefined;
  const contract = details.runtime_validation;
  if (contract.contract !== "sgg.unity_mcp.runtime_validation.v1" || !Array.isArray(contract.issues) || contract.issues.length === 0 || contract.issues.length > MAX_SCHEMA_ISSUES) return undefined;
  const issues: Record<string, unknown>[] = [];
  for (const candidate of contract.issues) {
    if (!isJsonObject(candidate) || !Array.isArray(candidate.path) || typeof candidate.issue_code !== "string" || !isRuntimeValidationIssueCode(candidate.issue_code)) return undefined;
    const path = safeValidationPath(candidate.path.filter((segment): segment is string | number => typeof segment === "string" || typeof segment === "number"));
    if (!path || path.length !== candidate.path.length) return undefined;
    const valueSchema = descriptorSchemaAtPath(normalizedDescriptorSchema, path);
    const rejectedValue = valueAtPath(commandArgs, path);
    if (!valueSchema || !runtimeIssueMatchesSchema(candidate.issue_code, valueSchema, rejectedValue, normalizedDescriptorSchema, path)) return undefined;
    const expected = descriptorExpectation(valueSchema);
    issues.push({
      path,
      issue_code: candidate.issue_code,
      ...(rejectedValue !== undefined ? { rejected_value_type: jsonValueType(rejectedValue) } : {}),
      rejected_value_reflected: false,
      ...(expected ? { accepted: expected } : {}),
    });
  }
  return {
    schema_version: 1,
    origin: "unity_runtime",
    issues,
    issues_truncated: false,
    rejected_values_reflected: false,
    descriptor_source: `command:${command}`,
    descriptor_lookup: recovery.schemaLookup,
  };
}

function isRuntimeValidationIssueCode(value: string): boolean {
  return value === "missing_required_field"
    || value === "invalid_type"
    || value === "enum_value_not_allowed"
    || value === "out_of_range"
    || value === "string_too_short"
    || value === "string_too_long"
    || value === "pattern_mismatch"
    || value === "invalid_path"
    || value === "unsupported_for_target"
    || value === "state_constraint_failed"
    || value === "relational_constraint_failed";
}

function runtimeIssueMatchesSchema(issueCode: string, schema: Record<string, unknown>, value: unknown, rootSchema: unknown, path: Array<string | number>): boolean {
  if (issueCode === "enum_value_not_allowed") return value !== undefined && Array.isArray(schema.enum) && !schema.enum.some((candidate) => Object.is(candidate, value));
  if (issueCode === "out_of_range") {
    return typeof value === "number" && (
      (typeof schema.minimum === "number" && value < schema.minimum)
      || (typeof schema.maximum === "number" && value > schema.maximum)
      || (typeof schema.exclusiveMinimum === "number" && value <= schema.exclusiveMinimum)
      || (typeof schema.exclusiveMaximum === "number" && value >= schema.exclusiveMaximum)
    );
  }
  if (issueCode === "string_too_short") return typeof value === "string" && typeof schema.minLength === "number" && value.length < schema.minLength;
  if (issueCode === "string_too_long") return typeof value === "string" && typeof schema.maxLength === "number" && value.length > schema.maxLength;
  if (issueCode === "pattern_mismatch") {
    if (typeof value !== "string" || typeof schema.pattern !== "string") return false;
    try {
      return !new RegExp(schema.pattern).test(value);
    } catch {
      return false;
    }
  }
  if (issueCode === "invalid_type") {
    const types = jsonSchemaTypes(schema.type);
    return value !== undefined && types !== undefined && !types.some((type) => matchesJsonSchemaType(value, type));
  }
  if (issueCode === "invalid_path") return typeof value === "string" && jsonSchemaTypes(schema.type)?.includes("string") === true;
  if (issueCode === "missing_required_field") return value === undefined && descriptorPathIsRequired(rootSchema, path);
  return issueCode === "unsupported_for_target"
    || issueCode === "state_constraint_failed"
    || issueCode === "relational_constraint_failed";
}

function descriptorPathIsRequired(rootSchema: unknown, path: Array<string | number>): boolean {
  if (!isJsonObject(rootSchema) || path.length === 0) return false;
  let current: Record<string, unknown> = rootSchema;
  for (let index = 0; index < path.length - 1; index++) {
    const segment = path[index];
    if (segment === undefined) return false;
    if (typeof segment === "number") {
      if (!isJsonObject(current.items)) return false;
      current = current.items;
    } else {
      if (!isJsonObject(current.properties) || !Object.hasOwn(current.properties, segment) || !isJsonObject(current.properties[segment])) return false;
      current = current.properties[segment];
    }
  }
  const leaf = path[path.length - 1];
  return typeof leaf === "string" && Array.isArray(current.required) && current.required.includes(leaf);
}

function descriptorSchemaAtPath(schema: unknown, path: Array<string | number>): Record<string, unknown> | undefined {
  if (!isJsonObject(schema)) return undefined;
  let current: Record<string, unknown> = schema;
  for (const segment of path) {
    if (typeof segment === "number") {
      if (!isJsonObject(current.items)) return undefined;
      current = current.items;
      continue;
    }
    if (!isJsonObject(current.properties) || !Object.hasOwn(current.properties, segment) || !isJsonObject(current.properties[segment])) return undefined;
    current = current.properties[segment];
  }
  return current;
}

function valueAtPath(value: unknown, path: Array<string | number>): unknown {
  let current = value;
  for (const segment of path) {
    if (typeof segment === "number") {
      if (!Array.isArray(current) || segment >= current.length) return undefined;
      current = current[segment];
      continue;
    }
    if (!isJsonObject(current) || !Object.hasOwn(current, segment)) return undefined;
    current = current[segment];
  }
  return current;
}

function trustedPreDispatchSchemaMetadata(details: Record<string, unknown>): Record<string, number> {
  const issueCount = details.validation_issue_count;
  return typeof issueCount === "number" && Number.isInteger(issueCount) && issueCount >= 0
    ? { validation_issue_count: Math.min(issueCount, MAX_SCHEMA_ISSUES) }
    : {};
}

function safeSchemaRecoveryMessage(code: SchemaRecoveryErrorCode, command: string | undefined): string {
  const subject = command ? `Command '${command}'` : "Command";
  if (code === "schema_unavailable") return `${subject} schema is unavailable or malformed`;
  if (code === "schema_validation_failed") return `${subject} returned a schema validation error; inspect the descriptor before constructing a new explicit call`;
  if (code === "invalid_field_value") return `${subject} returned invalid_field_value; inspect the descriptor before constructing a new explicit call`;
  if (code === "args_json_must_be_object") return `${subject} args_json must be a JSON object; inspect the descriptor before constructing a new explicit call`;
  if (code === "items_json_must_be_array") return "Batch items_json must be a JSON array; inspect projects and descriptors before constructing a new explicit batch";
  if (code === "missing_required_field") return `${subject} is missing a required field; inspect the descriptor before constructing a new explicit call`;
  return `${subject} arguments contain invalid JSON; inspect the descriptor before constructing a new explicit call`;
}

function inputError(code: SchemaRecoveryErrorCode | "internal_error", message: string, details?: Record<string, unknown>): GatewayResult {
  if (isSchemaRecoveryErrorCode(code)) {
    const recovery = buildSafeExecutableSchemaRecovery({});
    return fail({
      next_valid_actions: recoveryNextValidActions(recovery),
      error: {
        code,
        message: safeSchemaRecoveryMessage(code, undefined),
        details: { ...trustedPreDispatchSchemaMetadata(details ?? {}), ...schemaRecoveryDetails(recovery) },
      },
    });
  }
  return fail({
    error: {
      code,
      message,
      ...(details ? { details } : {}),
    },
  });
}
