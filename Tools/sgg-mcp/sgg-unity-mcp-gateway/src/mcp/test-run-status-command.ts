import type { CommandDescriptor } from "../bridge/bridge-client.js";
import { CanonicalPathService } from "../paths/canonical-path.js";
import { TEST_RUN_ARTIFACT_CAPABILITY, TEST_RUN_REQUEST_ISSUANCE_CONTRACT, TEST_RUN_TERMINAL_NO_EXECUTION_CONTRACT, assertTestRunOperationBinding, testRunOperationBindingHash, type TestRunOperationBinding, type TestRunRequestIssuanceLookup, type TestRunTerminalNoExecutionReceipt } from "../operations/test-run-operation-store.js";
import { ProjectArtifactReadError, readBoundTestRunArtifactSnapshot, type BoundTestRunArtifactSnapshot, type ProjectArtifactReadOptions } from "../security/project-artifact-reader.js";
import { persistedTestRunStatusObservation, testRunObservationClock, testRunObservationElapsed } from "./test-run-status-observation.js";
import type { GatewayError } from "./results.js";

export const TEST_RUN_STATUS_FALLBACK_DESCRIPTOR: CommandDescriptor = Object.freeze({
  id: "test.run.status",
  title: "Unity test run status",
  description: "Poll a Unity Test Framework run started by test.run and return its bounded summary and artifact path",
  package: "com.sungeargames.unity-mcp",
  risk: "read",
  idempotency: "safe",
  timeout_ms: 10_000,
  schema: {
    type: "object",
    properties: {
      operation_id: { type: "string", pattern: "^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$" },
      run_id: { type: "string", pattern: "^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$" },
      response_mode: { type: "string", enum: ["full", "compact"], default: "full" },
    },
    anyOf: [{ required: ["operation_id"] }, { required: ["run_id"] }],
    additionalProperties: false,
  },
  tags: ["test", "status"],
  capability: "build_test_automation",
  result_shape: "operation_status",
  output_limit: { max_seconds: 3600, max_artifact_chars: 200000, max_matched_tests: 32 },
  feature_flag: "build_test_automation",
  source: "builtin",
  package_id: "com.sungeargames.unity-mcp",
  trust: "builtin",
});

const canonicalPaths = new CanonicalPathService();
const ARTIFACT_CONVERGENCE_WINDOW_MS = 1_500;
const ARTIFACT_CONVERGENCE_RETRY_INTERVAL_MS = 50;
const MAX_ARTIFACT_READ_ATTEMPTS = 32;

export function boundTestRunStatusDescriptor(binding: TestRunOperationBinding): CommandDescriptor {
  return { ...TEST_RUN_STATUS_FALLBACK_DESCRIPTOR, package: binding.package_id, package_id: binding.package_id, package_version: binding.package_version };
}

export function testRunStatusOperationId(args: Record<string, unknown>): string | undefined {
  const operationId = typeof args.operation_id === "string" ? args.operation_id : "";
  const runId = typeof args.run_id === "string" ? args.run_id : "";
  if (operationId && runId && operationId !== runId) return undefined;
  return operationId || runId || undefined;
}

export function testRunStatusSelectorMatchesBinding(selector: string, binding: TestRunOperationBinding): boolean {
  if (selector === binding.project || selector === binding.original_project_selector || selector === binding.origin_instance_id) return true;
  try { return canonicalPaths.normalizeProjectRoot(selector) === binding.canonical_project_root; }
  catch { return false; }
}

export type BoundTestRunStatusResult = { status: "ok"; data: Record<string, unknown> } | { status: "error"; error: GatewayError };

export type BoundTestRunStatusReadOptions = ProjectArtifactReadOptions & {
  deadlineUnixMs?: number;
  convergenceWindowMs?: number;
  retryIntervalMs?: number;
  nowUnixMs?: () => number;
  sleepMs?: (ms: number) => Promise<void>;
  requestIssuance?: TestRunRequestIssuanceObservation;
};

export type TestRunRequestIssuanceObservation = TestRunRequestIssuanceLookup | {
  status: "unavailable";
  evidence_source: "durable_request_issuance_evidence_invalid" | "durable_request_issuance_storage_unavailable" | "durable_request_issuance_publication_outcome_unknown";
};

export async function readBoundTestRunStatus(binding: TestRunOperationBinding, operationId: string, readOptions: BoundTestRunStatusReadOptions = {}): Promise<BoundTestRunStatusResult> {
  const nowUnixMs = testRunObservationClock(readOptions.nowUnixMs ?? Date.now);
  const sleepMs = readOptions.sleepMs ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  const convergenceWindowMs = positiveBoundedInteger(readOptions.convergenceWindowMs, ARTIFACT_CONVERGENCE_WINDOW_MS, ARTIFACT_CONVERGENCE_WINDOW_MS);
  const retryIntervalMs = positiveBoundedInteger(readOptions.retryIntervalMs, ARTIFACT_CONVERGENCE_RETRY_INTERVAL_MS, convergenceWindowMs);
  const startedAt = nowUnixMs();
  const localDeadline = startedAt === null ? null : startedAt + convergenceWindowMs;
  const deadline = readOptions.deadlineUnixMs === undefined ? localDeadline
    : localDeadline !== null && Number.isSafeInteger(readOptions.deadlineUnixMs) && readOptions.deadlineUnixMs >= 0
      ? Math.min(localDeadline, readOptions.deadlineUnixMs) : null;
  let artifactReadAttempts = 0;
  let convergenceWaitMs = 0;
  try {
    try { assertTestRunOperationBinding(binding); }
    catch { throw new ProjectArtifactReadError("artifact_identity_mismatch"); }
    const bindingSnapshot = JSON.stringify(binding);
    let snapshot: BoundTestRunArtifactSnapshot;
    while (true) {
      artifactReadAttempts += 1;
      try {
        if (JSON.stringify(binding) !== bindingSnapshot) throw new ProjectArtifactReadError("artifact_identity_mismatch");
        snapshot = readBoundTestRunArtifactSnapshot(binding, operationId, readOptions);
        if (JSON.stringify(binding) !== bindingSnapshot) throw new ProjectArtifactReadError("artifact_identity_mismatch");
        break;
      } catch (error) {
        if (!(error instanceof ProjectArtifactReadError) || error.reason !== "artifact_changed" || artifactReadAttempts >= MAX_ARTIFACT_READ_ATTEMPTS) throw error;
        const sampledNow = nowUnixMs();
        const remainingMs = deadline === null || sampledNow === null ? 0
          : Math.min(deadline - sampledNow, convergenceWindowMs - convergenceWaitMs);
        if (remainingMs <= 0) throw error;
        const delayMs = Math.min(retryIntervalMs, remainingMs);
        await sleepMs(delayMs);
        convergenceWaitMs += delayMs;
        const afterSleep = nowUnixMs();
        if (deadline === null || afterSleep === null || afterSleep >= deadline || convergenceWaitMs >= convergenceWindowMs) throw error;
        continue;
      }
    }
    const observedAt = nowUnixMs();
    return {
      status: "ok",
      data: {
        ...snapshot.data,
        status_observation: persistedTestRunStatusObservation(observedAt, snapshot.openedMtimeNs),
        status_source: "persisted_project_artifact",
        operation_owner_verified: true,
        owner_endpoint_contacted: false,
        test_run_replayed: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        ...testRunRequestIssuanceEvidence(binding, readOptions.requestIssuance),
        artifact_read_attempts: artifactReadAttempts,
        artifact_read_elapsed_ms: testRunObservationElapsed(startedAt, observedAt),
        artifact_convergence_wait_ms: convergenceWaitMs,
        artifact_convergence_window_ms: convergenceWindowMs,
      },
    };
  } catch (error) {
    const reason = error instanceof ProjectArtifactReadError ? error.reason : "artifact_changed";
    const observedAt = nowUnixMs();
    return {
      status: "error",
      error: {
        code: "test_run_status_unavailable",
        message: "The exact persisted test.run status is unavailable",
        details: {
          reason,
          operation_id: operationId,
          artifact_schema_capability: TEST_RUN_ARTIFACT_CAPABILITY,
          operation_owner_verified: true,
          owner_endpoint_contacted: false,
          command_bridge_request_issued: false,
          test_run_replayed: false,
          write_command_repeated: false,
          write_replay_allowed: false,
          ...testRunRequestIssuanceEvidence(binding, readOptions.requestIssuance, true),
          artifact_read_attempts: artifactReadAttempts,
          artifact_read_elapsed_ms: testRunObservationElapsed(startedAt, observedAt),
          artifact_convergence_wait_ms: convergenceWaitMs,
          artifact_convergence_window_ms: convergenceWindowMs,
          artifact_convergence_exhausted: reason === "artifact_changed",
        },
      },
    };
  }
}

export function testRunRequestIssuanceEvidence(binding: TestRunOperationBinding, lookup: TestRunRequestIssuanceObservation | undefined, unresolved = false): Record<string, unknown> {
  const marker = lookup?.status === "found" ? lookup.marker : undefined;
  const state = marker ? "issued" : "unknown";
  const evidenceSource = marker
    ? "durable_request_issuance_marker"
    : lookup?.status === "reserved"
      ? "durable_request_issuance_reservation_without_positive_proof"
    : lookup?.status === "not_found"
      ? "durable_owner_binding_without_positive_issuance_proof"
      : lookup?.status === "unavailable"
        ? lookup.evidence_source
        : "request_issuance_evidence_not_supplied";
  return {
    test_run_request_issuance: {
      schema_version: 1,
      contract: TEST_RUN_REQUEST_ISSUANCE_CONTRACT,
      state,
      evidence_source: evidenceSource,
      marker_observation: marker ? "found" : lookup?.status === "reserved" ? "reserved" : lookup?.status === "not_found" ? "not_found" : lookup?.status === "unavailable" ? "unavailable" : "not_supplied",
      request_issuance_proven: marker !== undefined,
      request_non_issuance_proven: false,
      publication_outcome: marker ? "committed" : "unknown",
      owner_binding_hash: testRunOperationBindingHash(binding),
      ...(marker ? { issued_at: marker.issued_at } : {}),
    },
    ...(marker ? { test_run_bridge_request_issued: true } : {}),
    ...(unresolved ? {
      test_run_delivery_state: marker ? "uncertain" : "unknown",
      status_or_artifact_check_required_before_repeat: true,
      repeat_blocked_until_delivery_state_checked: true,
    } : {}),
  };
}

export function testRunTerminalNoExecutionStatusData(binding: TestRunOperationBinding, receipt: TestRunTerminalNoExecutionReceipt, requestIssuance: Extract<TestRunRequestIssuanceLookup, { status: "found" }>): Record<string, unknown> {
  return {
    command: "test.run",
    operation_id: receipt.operation_id,
    run_id: receipt.operation_id,
    status: "not_admitted",
    terminal: true,
    result: "NotAdmitted",
    success: false,
    operation_created: false,
    command_dispatched: false,
    test_runner_execute_invoked: false,
    artifact_ready: false,
    artifact_persisted: false,
    expected_artifact_path: receipt.expected_artifact_path,
    package_version: receipt.package_version,
    status_source: "durable_terminal_no_execution_receipt",
    operation_owner_verified: true,
    owner_endpoint_contacted: false,
    test_run_delivery_state: "not_executed",
    test_run_replayed: false,
    write_command_repeated: false,
    write_replay_allowed: false,
    rejected_operation_reusable: false,
    new_operation_required_for_future_run: true,
    test_run_terminal_no_execution: testRunTerminalNoExecutionEvidence(receipt),
    ...testRunRequestIssuanceEvidence(binding, requestIssuance),
  };
}

export function testRunTerminalNoExecutionEvidence(receipt: TestRunTerminalNoExecutionReceipt): Record<string, unknown> {
  return {
    schema_version: 1,
    contract: TEST_RUN_TERMINAL_NO_EXECUTION_CONTRACT,
    state: receipt.state,
    evidence_source: "durable_terminal_no_execution_receipt",
    operation_id: receipt.operation_id,
    owner_binding_hash: receipt.owner_binding_hash,
    request_issuance_hash: receipt.request_issuance_hash,
    issued_at: receipt.issued_at,
    recorded_at: receipt.recorded_at,
    operation_created: false,
    command_dispatched: false,
    test_runner_execute_invoked: false,
    artifact_persisted: false,
    bridge_error_code: receipt.bridge_error_code,
    reason: receipt.reason,
    package_update: receipt.package_update,
  };
}

export function testRunTerminalNoExecutionNextValidActions(receipt: TestRunTerminalNoExecutionReceipt): string[] {
  return receipt.reason === "package_refresh_required"
    ? ["run_compile.refresh_and_wait_only_as_a_new_explicit_action", "verify_package_update_current", "start_a_new_test.run_only_if_still_intended", "do_not_reuse_rejected_operation_id"]
    : ["call_compile.wait_ready", "call_compile.status", "start_a_new_test.run_only_after_fresh_ready_state_if_still_intended", "do_not_reuse_rejected_operation_id"];
}

function positiveBoundedInteger(value: number | undefined, fallback: number, max: number): number {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0 && value <= max ? value : fallback;
}
