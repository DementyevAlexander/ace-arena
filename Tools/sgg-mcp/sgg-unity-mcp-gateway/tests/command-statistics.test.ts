import { describe, expect, it } from "vitest";

import { measureCommandStatistics, summarizeCommandStatistics, type CommandStatisticsMeasurement } from "../src/observability/command-statistics.js";
import { gatewayResultToMcpToolResult } from "../src/mcp/result-presentation.js";
import type { ErrorCode, GatewayResult } from "../src/mcp/results.js";

describe("command statistics", () => {
  it("measures UTF-8 request and paired full/compact MCP results without retaining payloads", () => {
    const rawArgs = {
      project: "game",
      command: "compile.status",
      args_json: JSON.stringify({ response_mode: "compact", note: "Привет" }),
    };
    const fullResult: GatewayResult = {
      status: "ok",
      project: "game",
      command: "compile.status",
      data: { status: "ready", diagnostics: ["x".repeat(2000)] },
      diagnostics: [],
    };
    const compactResult: GatewayResult = {
      status: "ok",
      project: "game",
      command: "compile.status",
      data: { response_mode: "compact", status: "ready", result: "ready_clean" },
      diagnostics: [],
    };

    const measurement = measureCommandStatistics({
      tool: "sgg_unity_call",
      rawArgs,
      deliveredResult: compactResult,
      fullResult,
      compactResult,
      responseMode: "compact",
    });

    expect(measurement).toMatchObject({
      schema_version: 1,
      operation: "compile.status",
      operation_kind: "command",
      internal: false,
      response_mode: "compact",
      dispatched: true,
      request_bytes: Buffer.byteLength(JSON.stringify({ name: "sgg_unity_call", arguments: rawArgs }), "utf8"),
      command_argument_bytes: Buffer.byteLength(rawArgs.args_json, "utf8"),
      full_response_bytes: Buffer.byteLength(JSON.stringify(gatewayResultToMcpToolResult(fullResult)), "utf8"),
      compact_response_bytes: Buffer.byteLength(JSON.stringify(gatewayResultToMcpToolResult(compactResult)), "utf8"),
      delivered_response_bytes: Buffer.byteLength(JSON.stringify(gatewayResultToMcpToolResult(compactResult)), "utf8"),
    });
    expect(measurement.compact_response_bytes).toBeLessThan(measurement.full_response_bytes!);
    expect(JSON.stringify(measurement)).not.toContain("Привет");
    expect(JSON.stringify(measurement)).not.toContain("xxxx");
  });

  it("bounds unknown identities and records a fixed redacted failure cause", () => {
    const measurement = measureCommandStatistics({
      tool: "sgg_unity_call",
      rawArgs: { command: "../../private command", args_json: JSON.stringify({ token: "secret-token", path: "C:/private/project" }) },
      deliveredResult: failure("permission_denied"),
    });

    expect(measurement).toMatchObject({
      operation: "unknown_command",
      operation_kind: "command",
      response_mode: "full",
      dispatched: false,
      failure_stage: "policy",
      failure_summary: "Policy, approval, or precondition checks blocked execution.",
    });
    expect(JSON.stringify(measurement)).not.toContain("secret-token");
    expect(JSON.stringify(measurement)).not.toContain("C:/private/project");
    expect(JSON.stringify(measurement)).not.toContain("../../private command");
  });

  it("does not count a successful persisted-artifact status fallback as a Bridge dispatch", () => {
    const measurement = measureCommandStatistics({
      tool: "sgg_unity_call",
      rawArgs: { project: "game", command: "test.run.status", args_json: JSON.stringify({ operation_id: "test-20260904-120000-deadbeef" }) },
      deliveredResult: {
        status: "ok",
        project: "game",
        command: "test.run.status",
        data: { status: "completed", status_source: "persisted_project_artifact", owner_endpoint_contacted: false },
        diagnostics: [],
      },
      dispatched: false,
    });

    expect(measurement.dispatched).toBe(false);
  });

  it("aggregates per operation, response mode, failures, pagination, and excludes internal probes", () => {
    const fullSuccess = measurement({ operation: "compile.status", response_mode: "full", request_bytes: 100, full_response_bytes: 1000, compact_response_bytes: 200, delivered_response_bytes: 1000, dispatched: true });
    const compactSuccess = measurement({ operation: "compile.status", response_mode: "compact", request_bytes: 120, full_response_bytes: 1200, compact_response_bytes: 240, delivered_response_bytes: 240, dispatched: true });
    const bridgeFailure = measurement({ operation: "test.run", response_mode: "compact", request_bytes: 200, full_response_bytes: 400, compact_response_bytes: 400, delivered_response_bytes: 400, dispatched: true, failure_stage: "bridge", failure_summary: "Bridge transport or protocol did not produce a valid terminal response." });
    const events = [
      event(fullSuccess, "ok", "2026-08-14T10:00:00.000Z"),
      event(compactSuccess, "ok", "2026-08-14T10:01:00.000Z"),
      event(bridgeFailure, "error", "2026-08-14T10:02:00.000Z", "bridge_timeout"),
      event({ ...compactSuccess, internal: true }, "ok", "2026-08-14T10:03:00.000Z"),
      { phase: "start", timestamp: "2026-08-14T10:04:00.000Z" },
    ];

    const result = summarizeCommandStatistics(events, { cursor: 0, limit: 1, recent_failures_limit: 10 }, {
      collection_enabled: true,
      source_exists: true,
      retained_files: 1,
      files_scanned: 1,
      bytes_read: 4096,
      truncated_by_bytes: false,
      events_considered: events.length,
      parse_errors: 0,
    });

    expect(result).toMatchObject({
      schema_version: 1,
      state: "ok",
      storage: "gateway_audit",
      coverage: { measured_events: 3, ignored_internal_events: 1, ignored_start_events: 1, unmeasured_terminal_events: 0, invalid_measurement_events: 0, retained_file_window_complete: true, complete_retained_history: true },
      totals: {
        calls: 3,
        succeeded: 2,
        failed: 1,
        request_bytes: { samples: 3, total: 420, min: 100, max: 200, average: 140 },
        full_response_bytes: { samples: 3, total: 2600 },
        compact_response_bytes: { samples: 3, total: 840 },
        delivered_by_mode: { full: { samples: 1, total: 1000 }, compact: { samples: 2, total: 640 } },
        errors_by_code: { bridge_timeout: 1 },
        errors_by_stage: { bridge: 1 },
      },
      pagination: { cursor: 0, limit: 1, returned: 1, total_operations: 2, next_cursor: 1 },
      failure_signatures: [{ operation: "test.run", error_code: "bridge_timeout", stage: "bridge", count: 1 }],
      recent_failures: [{ operation: "test.run", error_code: "bridge_timeout", stage: "bridge" }],
      raw_payloads_retained: false,
      remote_telemetry: false,
    });
    expect(result.operations).toHaveLength(1);

    const filtered = summarizeCommandStatistics(events, { operation: "compile.status", response_mode: "compact" }, {
      collection_enabled: true,
      source_exists: true,
      retained_files: 2,
      files_scanned: 1,
      bytes_read: 4096,
      truncated_by_bytes: true,
      events_considered: events.length,
      parse_errors: 0,
    });
    expect(filtered).toMatchObject({ state: "partial", coverage: { measured_events: 1, complete_retained_history: false }, totals: { calls: 1, succeeded: 1, failed: 0 } });

    const withoutRecentFailures = summarizeCommandStatistics(events, { recent_failures_limit: 0 }, {
      collection_enabled: true,
      source_exists: true,
      retained_files: 1,
      files_scanned: 1,
      bytes_read: 4096,
      truncated_by_bytes: false,
      events_considered: events.length,
      parse_errors: 0,
    });
    expect(withoutRecentFailures.recent_failures).toEqual([]);
  });
});

function failure(code: ErrorCode): GatewayResult {
  return { status: "error", error: { code, message: "private failure token=secret" }, diagnostics: [] };
}

function measurement(overrides: Partial<CommandStatisticsMeasurement>): CommandStatisticsMeasurement {
  return {
    schema_version: 1,
    operation: "compile.status",
    operation_kind: "command",
    internal: false,
    response_mode: "full",
    dispatched: true,
    ...overrides,
  };
}

function event(statistics: CommandStatisticsMeasurement, status: "ok" | "error", timestamp: string, errorCode?: ErrorCode): Record<string, unknown> {
  return {
    phase: "terminal",
    audit_id: `audit-${timestamp.slice(11, 19).replaceAll(":", "")}`,
    timestamp,
    status,
    ...(errorCode ? { error_code: errorCode } : {}),
    statistics_schema_version: statistics.schema_version,
    statistics_operation: statistics.operation,
    statistics_operation_kind: statistics.operation_kind,
    statistics_internal: statistics.internal,
    statistics_request_bytes: statistics.request_bytes,
    statistics_command_argument_bytes: statistics.command_argument_bytes,
    statistics_full_response_bytes: statistics.full_response_bytes,
    statistics_compact_response_bytes: statistics.compact_response_bytes,
    statistics_delivered_response_bytes: statistics.delivered_response_bytes,
    statistics_response_mode: statistics.response_mode,
    statistics_dispatched: statistics.dispatched,
    statistics_failure_stage: statistics.failure_stage,
    statistics_failure_summary: statistics.failure_summary,
  };
}
