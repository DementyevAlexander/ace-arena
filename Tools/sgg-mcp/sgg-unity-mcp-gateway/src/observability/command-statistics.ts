import type { ToolName } from "../mcp/contracts.js";
import { gatewayResultToMcpToolResult } from "../mcp/result-presentation.js";
import { isErrorCode, type ErrorCode, type GatewayResult } from "../mcp/results.js";

export type CommandStatisticsFailureStage = "input" | "policy" | "routing" | "bridge" | "unity" | "gateway";

export type CommandStatisticsMeasurement = {
  schema_version: 1;
  operation: string;
  operation_kind: "command" | "tool";
  internal: boolean;
  request_bytes?: number;
  command_argument_bytes?: number;
  full_response_bytes?: number;
  compact_response_bytes?: number;
  delivered_response_bytes?: number;
  response_mode: "full" | "compact";
  dispatched: boolean;
  failure_stage?: CommandStatisticsFailureStage;
  failure_summary?: string;
};

export type CommandStatisticsQuery = {
  operation?: string;
  outcome?: "ok" | "error";
  response_mode?: "full" | "compact";
  cursor?: number;
  limit?: number;
  recent_failures_limit?: number;
};

export type CommandStatisticsCoverage = {
  collection_enabled: boolean;
  source_exists: boolean;
  retained_files: number;
  files_scanned: number;
  bytes_read: number;
  truncated_by_bytes: boolean;
  events_considered: number;
  parse_errors: number;
};

type StatisticsEvent = {
  audit_id?: unknown;
  phase?: unknown;
  timestamp?: unknown;
  status?: unknown;
  error_code?: unknown;
  statistics_schema_version?: unknown;
  statistics_operation?: unknown;
  statistics_operation_kind?: unknown;
  statistics_internal?: unknown;
  statistics_request_bytes?: unknown;
  statistics_command_argument_bytes?: unknown;
  statistics_full_response_bytes?: unknown;
  statistics_compact_response_bytes?: unknown;
  statistics_delivered_response_bytes?: unknown;
  statistics_response_mode?: unknown;
  statistics_dispatched?: unknown;
  statistics_failure_stage?: unknown;
  statistics_failure_summary?: unknown;
};

type ByteAccumulator = { samples: number; total: number; min: number; max: number };
type OperationAccumulator = {
  operation: string;
  operation_kind: "command" | "tool";
  calls: number;
  dispatched: number;
  succeeded: number;
  failed: number;
  first_timestamp: string | null;
  last_timestamp: string | null;
  request_bytes: ByteAccumulator;
  command_argument_bytes: ByteAccumulator;
  full_response_bytes: ByteAccumulator;
  compact_response_bytes: ByteAccumulator;
  delivered_response_bytes: ByteAccumulator;
  delivered_by_mode: Record<"full" | "compact", ByteAccumulator>;
  errors_by_code: Record<string, number>;
  errors_by_stage: Record<string, number>;
};

const SAFE_OPERATION = /^[A-Za-z0-9_.:-]{1,256}$/;
const SAFE_TIMESTAMP = /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{3})?Z$/;
const FAILURE_STAGES = new Set<CommandStatisticsFailureStage>(["input", "policy", "routing", "bridge", "unity", "gateway"]);
const INPUT_ERRORS = new Set<ErrorCode>([
  "invalid_json", "missing_required_field", "invalid_field_value", "selector_not_supported", "batch_mode_not_supported",
  "args_json_must_be_object", "items_json_must_be_array", "schema_validation_failed", "project_required", "project_required_for_log_kind",
]);
const POLICY_ERRORS = new Set<ErrorCode>([
  "permission_denied", "confirmation_required", "approval_unavailable", "policy_reload_failed", "policy_invalid", "policy_degraded",
  "unsafe_binding_refused", "precondition_required", "precondition_failed",
]);
const ROUTING_ERRORS = new Set<ErrorCode>([
  "project_not_found", "project_offline", "project_stale", "duplicate_project_id", "command_not_found", "command_handler_unavailable",
  "catalog_pending", "catalog_stale", "catalog_unavailable", "schema_unavailable", "feature_disabled", "job_not_supported", "job_not_found",
  "build_run_not_found", "menu_operation_not_found", "package_operation_not_found",
]);
const BRIDGE_ERRORS = new Set<ErrorCode>([
  "bridge_unreachable", "bridge_protocol_error", "protocol_version_mismatch", "bridge_timeout", "replacement_timeout", "invalid_token",
  "mcp_session_not_found",
]);

export function measureCommandStatistics(input: {
  tool: ToolName;
  rawArgs: unknown;
  deliveredResult: GatewayResult;
  fullResult?: GatewayResult;
  compactResult?: GatewayResult;
  responseMode?: "full" | "compact";
  internal?: boolean;
  dispatched?: boolean;
}): CommandStatisticsMeasurement {
  const operation = statisticsOperation(input.tool, input.rawArgs, input.deliveredResult);
  const failureStage = input.deliveredResult.status === "error" ? classifyFailureStage(input.deliveredResult.error.code) : undefined;
  const fullResult = input.fullResult ?? input.deliveredResult;
  const compactResult = input.compactResult ?? input.deliveredResult;
  const responseMode = input.responseMode ?? requestedResponseMode(input.rawArgs);
  const deliveredResponseBytes = serializedBytes(gatewayResultToMcpToolResult(input.deliveredResult));
  const fullResponseBytes = fullResult === input.deliveredResult ? deliveredResponseBytes : serializedBytes(gatewayResultToMcpToolResult(fullResult));
  const compactResponseBytes = compactResult === input.deliveredResult
    ? deliveredResponseBytes
    : compactResult === fullResult
      ? fullResponseBytes
      : serializedBytes(gatewayResultToMcpToolResult(compactResult));
  return {
    schema_version: 1,
    operation: operation.value,
    operation_kind: operation.kind,
    internal: input.internal === true,
    ...optionalBytes("request_bytes", serializedBytes({ name: input.tool, arguments: input.rawArgs })),
    ...optionalBytes("command_argument_bytes", commandArgumentBytes(input.tool, input.rawArgs)),
    ...optionalBytes("full_response_bytes", fullResponseBytes),
    ...optionalBytes("compact_response_bytes", compactResponseBytes),
    ...optionalBytes("delivered_response_bytes", deliveredResponseBytes),
    response_mode: responseMode,
    dispatched: input.dispatched ?? commandWasDispatched(input.deliveredResult, failureStage),
    ...(failureStage ? {
      failure_stage: failureStage,
      failure_summary: safeFailureSummary(failureStage),
    } : {}),
  };
}

export function summarizeCommandStatistics(events: StatisticsEvent[], query: CommandStatisticsQuery, coverage: CommandStatisticsCoverage): Record<string, unknown> {
  const operationFilter = query.operation && SAFE_OPERATION.test(query.operation) ? query.operation : undefined;
  const cursor = boundedInteger(query.cursor, 0, Number.MAX_SAFE_INTEGER, 0);
  const limit = boundedInteger(query.limit, 1, 50, 25);
  const recentFailuresLimit = boundedInteger(query.recent_failures_limit, 0, 50, 20);
  const totals = createAccumulator("all", "tool");
  const byOperation = new Map<string, OperationAccumulator>();
  const recentFailures: Array<Record<string, unknown>> = [];
  const signatures = new Map<string, { operation: string; error_code: string; stage: string; summary: string; count: number; first_timestamp: string | null; last_timestamp: string | null }>();
  let measuredEvents = 0;
  let ignoredInternalEvents = 0;
  let ignoredStartEvents = 0;
  let unmeasuredTerminalEvents = 0;
  let invalidMeasurementEvents = 0;

  for (const event of events) {
    if (event.statistics_schema_version === undefined) {
      if (event.phase === "start") ignoredStartEvents += 1;
      else unmeasuredTerminalEvents += 1;
      continue;
    }
    if (event.statistics_internal === true) {
      ignoredInternalEvents += 1;
      continue;
    }
    const parsed = parseStatisticsEvent(event);
    if (!parsed) {
      invalidMeasurementEvents += 1;
      continue;
    }
    if (operationFilter && parsed.operation !== operationFilter) continue;
    if (query.outcome && parsed.status !== query.outcome) continue;
    if (query.response_mode && parsed.responseMode !== query.response_mode) continue;
    measuredEvents += 1;
    addEvent(totals, parsed);
    const operation = byOperation.get(parsed.operation) ?? createAccumulator(parsed.operation, parsed.operationKind);
    addEvent(operation, parsed);
    byOperation.set(parsed.operation, operation);

    if (parsed.status === "error") {
      recentFailures.push({
        timestamp: parsed.timestamp,
        audit_id: parsed.auditId,
        operation: parsed.operation,
        operation_kind: parsed.operationKind,
        error_code: parsed.errorCode,
        stage: parsed.failureStage,
        summary: parsed.failureSummary,
        response_mode: parsed.responseMode,
        request_bytes: parsed.requestBytes,
        full_response_bytes: parsed.fullResponseBytes,
        compact_response_bytes: parsed.compactResponseBytes,
        delivered_response_bytes: parsed.deliveredResponseBytes,
      });
      const signatureKey = `${parsed.operation}\u0000${parsed.errorCode}\u0000${parsed.failureStage}`;
      const signature = signatures.get(signatureKey) ?? {
        operation: parsed.operation,
        error_code: parsed.errorCode,
        stage: parsed.failureStage,
        summary: parsed.failureSummary,
        count: 0,
        first_timestamp: parsed.timestamp,
        last_timestamp: parsed.timestamp,
      };
      signature.count += 1;
      signature.first_timestamp ??= parsed.timestamp;
      signature.last_timestamp = parsed.timestamp;
      signatures.set(signatureKey, signature);
    }
  }

  const operations = [...byOperation.values()].sort((left, right) => compareStrings(left.operation, right.operation));
  const page = operations.slice(cursor, cursor + limit);
  const nextCursor = cursor + page.length < operations.length ? cursor + page.length : null;
  const failureSignatures = [...signatures.values()]
    .sort((left, right) => right.count - left.count || compareStrings(left.operation, right.operation) || compareStrings(left.error_code, right.error_code))
    .slice(0, 50);
  const retainedFileWindowComplete = coverage.source_exists && coverage.retained_files === coverage.files_scanned && !coverage.truncated_by_bytes && coverage.parse_errors === 0;
  const completeRetainedHistory = retainedFileWindowComplete && unmeasuredTerminalEvents === 0 && invalidMeasurementEvents === 0;
  const state = !coverage.collection_enabled
    ? "disabled"
    : !coverage.source_exists
      ? "unavailable"
      : coverage.parse_errors > 0 && measuredEvents === 0
        ? "corrupt"
        : completeRetainedHistory
          ? "ok"
          : "partial";

  return {
    schema_version: 1,
    state,
    storage: "gateway_audit",
    request_measurement_scope: "mcp_tool_name_and_arguments_json_utf8_without_jsonrpc_or_transport_headers",
    response_measurement_scope: "mcp_tool_result_json_utf8_without_jsonrpc_or_transport_headers_or_compression",
    collection_enabled: coverage.collection_enabled,
    coverage: {
      source_exists: coverage.source_exists,
      retained_files: coverage.retained_files,
      files_scanned: coverage.files_scanned,
      bytes_read: coverage.bytes_read,
      truncated_by_bytes: coverage.truncated_by_bytes,
      events_considered: coverage.events_considered,
      measured_events: measuredEvents,
      ignored_internal_events: ignoredInternalEvents,
      ignored_start_events: ignoredStartEvents,
      unmeasured_terminal_events: unmeasuredTerminalEvents,
      invalid_measurement_events: invalidMeasurementEvents,
      parse_errors: coverage.parse_errors,
      retained_file_window_complete: retainedFileWindowComplete,
      complete_retained_history: completeRetainedHistory,
    },
    filters: {
      operation: operationFilter ?? null,
      outcome: query.outcome ?? null,
      response_mode: query.response_mode ?? null,
    },
    totals: finalizeAccumulator(totals),
    operations: page.map(finalizeAccumulator),
    pagination: { cursor, limit, returned: page.length, total_operations: operations.length, next_cursor: nextCursor },
    failure_signatures: failureSignatures,
    recent_failures: recentFailuresLimit === 0 ? [] : recentFailures.slice(-recentFailuresLimit).reverse(),
    redaction_applied: true,
    raw_payloads_retained: false,
    remote_telemetry: false,
  };
}

function statisticsOperation(tool: ToolName, rawArgs: unknown, result: GatewayResult): { value: string; kind: "command" | "tool" } {
  if (tool !== "sgg_unity_call") return { value: tool, kind: "tool" };
  if (result.status === "error" && result.error.code === "command_not_found") return { value: "unknown_command", kind: "command" };
  const args = isRecord(rawArgs) ? rawArgs : undefined;
  const candidate = result.command ?? args?.command;
  return typeof candidate === "string" && SAFE_OPERATION.test(candidate)
    ? { value: candidate, kind: "command" }
    : { value: "unknown_command", kind: "command" };
}

function commandArgumentBytes(tool: ToolName, rawArgs: unknown): number | undefined {
  if (tool !== "sgg_unity_call" || !isRecord(rawArgs) || typeof rawArgs.args_json !== "string") return undefined;
  return Buffer.byteLength(rawArgs.args_json, "utf8");
}

function requestedResponseMode(rawArgs: unknown): "full" | "compact" {
  if (!isRecord(rawArgs) || typeof rawArgs.args_json !== "string") return "full";
  try {
    const parsed = JSON.parse(rawArgs.args_json) as unknown;
    return isRecord(parsed) && parsed.response_mode === "compact" ? "compact" : "full";
  } catch {
    return "full";
  }
}

function classifyFailureStage(code: ErrorCode): CommandStatisticsFailureStage {
  if (INPUT_ERRORS.has(code)) return "input";
  if (POLICY_ERRORS.has(code)) return "policy";
  if (ROUTING_ERRORS.has(code)) return "routing";
  if (BRIDGE_ERRORS.has(code)) return "bridge";
  return code === "internal_error" ? "gateway" : "unity";
}

function commandWasDispatched(result: GatewayResult, failureStage: CommandStatisticsFailureStage | undefined): boolean {
  if (result.status === "ok") return true;
  const details = result.error.details;
  if (details) {
    for (const key of ["command_bridge_request_issued", "bridge_request_issued", "request_executed"] as const) {
      if (details[key] === true) return true;
      if (details[key] === false) return false;
    }
    if (details.delivery_state === "not_dispatched") return false;
  }
  return failureStage !== "input" && failureStage !== "policy" && failureStage !== "routing";
}

function safeFailureSummary(stage: CommandStatisticsFailureStage): string {
  switch (stage) {
    case "input": return "Input or schema validation rejected the request.";
    case "policy": return "Policy, approval, or precondition checks blocked execution.";
    case "routing": return "The project, command, catalog, or operation could not be resolved.";
    case "bridge": return "Bridge transport or protocol did not produce a valid terminal response.";
    case "unity": return "The Unity command returned a typed execution failure.";
    case "gateway": return "Gateway result processing failed internally.";
  }
}

function serializedBytes(value: unknown): number | undefined {
  try {
    return Buffer.byteLength(JSON.stringify(value), "utf8");
  } catch {
    return undefined;
  }
}

function optionalBytes<Key extends keyof Pick<CommandStatisticsMeasurement, "request_bytes" | "command_argument_bytes" | "full_response_bytes" | "compact_response_bytes" | "delivered_response_bytes">>(key: Key, value: number | undefined): Partial<Pick<CommandStatisticsMeasurement, Key>> {
  return value === undefined ? {} : { [key]: value } as Partial<Pick<CommandStatisticsMeasurement, Key>>;
}

function parseStatisticsEvent(event: StatisticsEvent): {
  operation: string;
  operationKind: "command" | "tool";
  status: "ok" | "error";
  timestamp: string | null;
  auditId: string | null;
  responseMode: "full" | "compact";
  dispatched: boolean;
  requestBytes?: number;
  commandArgumentBytes?: number;
  fullResponseBytes?: number;
  compactResponseBytes?: number;
  deliveredResponseBytes?: number;
  errorCode: string;
  failureStage: string;
  failureSummary: string;
} | undefined {
  if (event.statistics_schema_version !== 1 || typeof event.statistics_operation !== "string" || !SAFE_OPERATION.test(event.statistics_operation)) return undefined;
  if (event.statistics_operation_kind !== "command" && event.statistics_operation_kind !== "tool") return undefined;
  if (event.status !== "ok" && event.status !== "error") return undefined;
  if (event.statistics_response_mode !== "full" && event.statistics_response_mode !== "compact") return undefined;
  const errorCode = event.status === "error" && typeof event.error_code === "string" && isErrorCode(event.error_code) ? event.error_code : "unknown_error";
  const failureStage = event.status === "error" && typeof event.statistics_failure_stage === "string" && FAILURE_STAGES.has(event.statistics_failure_stage as CommandStatisticsFailureStage) ? event.statistics_failure_stage : "unknown";
  const expectedFailureSummary = failureStage !== "unknown" ? safeFailureSummary(failureStage as CommandStatisticsFailureStage) : undefined;
  const failureSummary = event.status === "error" && expectedFailureSummary !== undefined && event.statistics_failure_summary === expectedFailureSummary
    ? expectedFailureSummary
    : "No bounded failure explanation is available.";
  return {
    operation: event.statistics_operation,
    operationKind: event.statistics_operation_kind,
    status: event.status,
    timestamp: typeof event.timestamp === "string" && SAFE_TIMESTAMP.test(event.timestamp) ? event.timestamp : null,
    auditId: typeof event.audit_id === "string" && SAFE_OPERATION.test(event.audit_id) ? event.audit_id : null,
    responseMode: event.statistics_response_mode,
    dispatched: event.statistics_dispatched === true,
    ...optionalNonnegativeInteger("requestBytes", event.statistics_request_bytes),
    ...optionalNonnegativeInteger("commandArgumentBytes", event.statistics_command_argument_bytes),
    ...optionalNonnegativeInteger("fullResponseBytes", event.statistics_full_response_bytes),
    ...optionalNonnegativeInteger("compactResponseBytes", event.statistics_compact_response_bytes),
    ...optionalNonnegativeInteger("deliveredResponseBytes", event.statistics_delivered_response_bytes),
    errorCode,
    failureStage,
    failureSummary,
  };
}

function createAccumulator(operation: string, operationKind: "command" | "tool"): OperationAccumulator {
  return {
    operation,
    operation_kind: operationKind,
    calls: 0,
    dispatched: 0,
    succeeded: 0,
    failed: 0,
    first_timestamp: null,
    last_timestamp: null,
    request_bytes: createByteAccumulator(),
    command_argument_bytes: createByteAccumulator(),
    full_response_bytes: createByteAccumulator(),
    compact_response_bytes: createByteAccumulator(),
    delivered_response_bytes: createByteAccumulator(),
    delivered_by_mode: { full: createByteAccumulator(), compact: createByteAccumulator() },
    errors_by_code: {},
    errors_by_stage: {},
  };
}

function addEvent(accumulator: OperationAccumulator, event: NonNullable<ReturnType<typeof parseStatisticsEvent>>): void {
  accumulator.calls += 1;
  if (event.dispatched) accumulator.dispatched += 1;
  if (event.status === "ok") accumulator.succeeded += 1;
  else accumulator.failed += 1;
  if (event.timestamp !== null) {
    accumulator.first_timestamp ??= event.timestamp;
    accumulator.last_timestamp = event.timestamp;
  }
  addBytes(accumulator.request_bytes, event.requestBytes);
  addBytes(accumulator.command_argument_bytes, event.commandArgumentBytes);
  addBytes(accumulator.full_response_bytes, event.fullResponseBytes);
  addBytes(accumulator.compact_response_bytes, event.compactResponseBytes);
  addBytes(accumulator.delivered_response_bytes, event.deliveredResponseBytes);
  addBytes(accumulator.delivered_by_mode[event.responseMode], event.deliveredResponseBytes);
  if (event.status === "error") {
    increment(accumulator.errors_by_code, event.errorCode);
    increment(accumulator.errors_by_stage, event.failureStage);
  }
}

function finalizeAccumulator(accumulator: OperationAccumulator): Record<string, unknown> {
  const full = finalizeBytes(accumulator.full_response_bytes);
  const compact = finalizeBytes(accumulator.compact_response_bytes);
  const pairedSamples = accumulator.full_response_bytes.samples === accumulator.compact_response_bytes.samples ? accumulator.full_response_bytes.samples : 0;
  const reductionBytes = pairedSamples > 0 ? accumulator.full_response_bytes.total - accumulator.compact_response_bytes.total : null;
  const reductionRatio = pairedSamples > 0 && accumulator.full_response_bytes.total > 0
    ? rounded(1 - accumulator.compact_response_bytes.total / accumulator.full_response_bytes.total)
    : null;
  return {
    operation: accumulator.operation,
    operation_kind: accumulator.operation_kind,
    calls: accumulator.calls,
    dispatched: accumulator.dispatched,
    succeeded: accumulator.succeeded,
    failed: accumulator.failed,
    first_timestamp: accumulator.first_timestamp,
    last_timestamp: accumulator.last_timestamp,
    request_bytes: finalizeBytes(accumulator.request_bytes),
    command_argument_bytes: finalizeBytes(accumulator.command_argument_bytes),
    full_response_bytes: full,
    compact_response_bytes: compact,
    delivered_response_bytes: finalizeBytes(accumulator.delivered_response_bytes),
    delivered_by_mode: {
      full: finalizeBytes(accumulator.delivered_by_mode.full),
      compact: finalizeBytes(accumulator.delivered_by_mode.compact),
    },
    compact_reduction: { paired_samples: pairedSamples, bytes: reductionBytes, ratio: reductionRatio },
    errors_by_code: accumulator.errors_by_code,
    errors_by_stage: accumulator.errors_by_stage,
  };
}

function createByteAccumulator(): ByteAccumulator {
  return { samples: 0, total: 0, min: Number.MAX_SAFE_INTEGER, max: 0 };
}

function addBytes(accumulator: ByteAccumulator, value: number | undefined): void {
  if (value === undefined) return;
  accumulator.samples += 1;
  accumulator.total += value;
  accumulator.min = Math.min(accumulator.min, value);
  accumulator.max = Math.max(accumulator.max, value);
}

function finalizeBytes(accumulator: ByteAccumulator): Record<string, number | null> {
  return {
    samples: accumulator.samples,
    total: accumulator.total,
    min: accumulator.samples > 0 ? accumulator.min : null,
    max: accumulator.samples > 0 ? accumulator.max : null,
    average: accumulator.samples > 0 ? rounded(accumulator.total / accumulator.samples) : null,
  };
}

function optionalNonnegativeInteger<Key extends string>(key: Key, value: unknown): Partial<Record<Key, number>> {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= 0 ? { [key]: value } as Record<Key, number> : {};
}

function boundedInteger(value: number | undefined, minimum: number, maximum: number, fallback: number): number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= minimum && value <= maximum ? value : fallback;
}

function rounded(value: number): number {
  return Math.round(value * 10_000) / 10_000;
}

function increment(target: Record<string, number>, key: string): void {
  target[key] = (target[key] ?? 0) + 1;
}

function compareStrings(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
