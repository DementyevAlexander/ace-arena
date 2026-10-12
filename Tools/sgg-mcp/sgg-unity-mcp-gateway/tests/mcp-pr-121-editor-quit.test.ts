import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { argsHash } from "../src/approvals/approval-store.js";
import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { UnityProcessListResult } from "../src/diagnostics/unity-processes.js";
import type { UnityNativeModalInfo, UnityWindowDiagnosticsStatus } from "../src/diagnostics/unity-window-diagnostics.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];

afterEach(() => {
  for (const directory of tempDirs.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

describe("MCP-PR-121 editor.quit lifecycle reconciliation", () => {
  it("PR246 preserves an exactly bound delivered Play refusal without starting shutdown observations", async () => {
    const fixture = makeFixture();
    fixture.bridge.onInvoke = undefined;
    fixture.bridge.response = (record, request) => quitRefusal(record, request);
    const result = await call(fixture);
    expect(result).toMatchObject({ status: "error", error: { code: "unity_busy", details: {
      reason: "playmode", close_requested: false, editor_quit_preflight_refusal_verified: true,
      bridge_request_issued: true, close_request_dispatched_once: false,
    } } });
    expect(fixture.bridge.invokeCount).toBe(1);
    expect(fixture.processScanCalls).toBe(0);
    expect(fixture.windowScanCalls).toBe(0);
    expect(fixture.context.registry!.listProjects()[0]).toMatchObject({ quit_operation_id: "", quit_request_state: "none" });
  });

  it.each(["missing", "operation", "owner", "root", "timestamp", "hash", "scheduled", "incomplete", "extra", "code"])("PR246 does not infer pre-close nonexecution from %s evidence", async drift => {
    const fixture = makeFixture();
    fixture.bridge.onInvoke = undefined;
    fixture.bridge.response = (record, request) => {
      const result = quitRefusal(record, request);
      if (result.status !== "error") throw new Error("Expected refusal fixture");
      const details = result.error.details!;
      const witness = details.editor_quit_preflight_refusal as Record<string, unknown>;
      const owner = witness.owner as Record<string, unknown>;
      if (drift === "missing") delete details.editor_quit_preflight_refusal;
      if (drift === "operation") witness.operation_id = "quit-20260807-100000-deadbeef";
      if (drift === "owner") owner.unity_pid = 54321;
      if (drift === "root") owner.canonical_project_root = "c:/u/other";
      if (drift === "timestamp") owner.unity_process_start_utc = "2026-08-07T08:00:01.000Z";
      if (drift === "hash") witness.caller_args_hash = `sha256:${"0".repeat(64)}`;
      if (drift === "scheduled") witness.exit_scheduled = true;
      if (drift === "incomplete") witness.binding_complete = false;
      if (drift === "extra") witness.extra = "lookalike";
      if (drift === "code") result.error.code = "bridge_timeout";
      return result;
    };
    expect(await call(fixture)).toMatchObject({ status: "error", error: { code: "quit_uncertain" } });
    expect(fixture.bridge.invokeCount).toBe(1);
    expect(fixture.processScanCalls).toBeGreaterThan(0);
  });
  it.each([
    ["omitted", {}],
    ["explicit true", { dry_run: true }],
  ])("keeps %s dry_run preview-only without quit correlation or reconciliation", async (_label, args) => {
    const fixture = makeFixture();

    const result = await callWithArgs(fixture, args);

    expect(fixture.bridge.invokeCount).toBe(1);
    expect(fixture.bridge.operationId).toBe("");
    expect(fixture.bridge.callerArgsHash).toBe("");
    expect(fixture.processScanCalls).toBe(0);
    expect(fixture.windowScanCalls).toBe(0);
    expect(result).toMatchObject({
      status: "ok",
      data: { command: "editor.quit", dry_run: true, close_requested: false, lifecycle: "preview" },
    });
  });

  it("binds one quit dispatch to the exact process generation and verifies clean close", async () => {
    const fixture = makeFixture();
    fixture.onSleep = () => {
      fixture.writeLifecycle("accepted", "stopping");
      fixture.processPresent = false;
      fixture.windowPresent = false;
    };

    const result = await call(fixture);

    expect(fixture.bridge.invokeCount).toBe(1);
    expect(fixture.bridge.operationId).toMatch(/^quit-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/);
    expect(fixture.bridge.callerArgsHash).toBe(argsHash({ dry_run: false, confirm_close: true, wait_timeout_ms: 1_000 }));
    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        close_requested: true,
        close_observed: true,
        lifecycle: "closed_verified",
        quit_reconciliation: {
          contract: "sgg.unity_mcp.editor_quit_reconciliation.v1",
          initial_instance_id: "game-current",
          initial_unity_pid: 12345,
          initial_unity_process_start_utc: "2026-08-07T08:00:00.000Z",
          close_request_dispatched_once: true,
          automatic_second_quit_attempted: false,
          automatic_process_termination_attempted: false,
          terminal_reason: "closed_verified",
          process: { exact_generation_present: false, process_exit_proven: true },
          window: { top_level_window_present: false },
        },
      });
    }
  });

  it.each(["cancelled", "ignored"] as const)("returns quit_not_observed for a correlated %s outcome without replay", async (state) => {
    const fixture = makeFixture();
    fixture.onSleep = () => fixture.writeLifecycle(state, "ready");

    const result = await call(fixture);

    expect(fixture.bridge.invokeCount).toBe(1);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("quit_not_observed");
      expect(result.error.details).toMatchObject({
        terminal_reason: state,
        automatic_second_quit_attempted: false,
        automatic_process_termination_attempted: false,
        heartbeat: { quit_request_state: state, binding_matches: true },
        process: { exact_generation_present: true, process_exit_proven: false },
      });
      expect(result.next_valid_actions).toContain("do_not_repeat_editor.quit");
    }
  });

  it("returns bounded modal-deferred uncertainty while the exact process remains responsive", async () => {
    const fixture = makeFixture();
    fixture.modalPresent = true;

    const result = await call(fixture);

    expect(fixture.bridge.invokeCount).toBe(1);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("quit_uncertain");
      expect(result.error.details).toMatchObject({
        wait_timeout_ms: 1_000,
        terminal_reason: "modal_or_close_prompt_deferred",
        heartbeat: { quit_request_state: "deferred", binding_matches: true },
        process: { exact_generation_present: true },
        window: { top_level_window_present: true, responding: true, native_modal_present: true },
      });
    }
  });

  it("keeps accepted-but-alive close lifecycle uncertain at the deadline", async () => {
    const fixture = makeFixture();
    fixture.onSleep = () => fixture.writeLifecycle("accepted", "stopping");

    const result = await call(fixture);

    expect(fixture.bridge.invokeCount).toBe(1);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("quit_uncertain");
      expect(result.error.details).toMatchObject({ terminal_reason: "accepted_but_process_exit_not_observed", automatic_second_quit_attempted: false });
    }
  });

  it("does not claim close when the PID was reused by another process generation", async () => {
    const fixture = makeFixture();
    fixture.processStartUtc = "2026-08-07T10:00:01.000Z";
    fixture.windowPresent = false;
    fixture.onSleep = () => fixture.writeLifecycle("accepted", "stopping");

    const result = await call(fixture);

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("quit_uncertain");
      expect(result.error.details).toMatchObject({
        process: { exact_generation_present: false, pid_reused: true, process_exit_proven: false },
        window: { top_level_window_present: false, process_generation_bound: false },
      });
    }
  });

  it("does not prove process exit from a truncated process scan", async () => {
    const fixture = makeFixture();
    fixture.processPresent = false;
    fixture.processScanTruncated = true;
    fixture.windowPresent = false;
    fixture.onSleep = () => fixture.writeLifecycle("accepted", "stopping");

    const result = await call(fixture);

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ process: { scan_truncated: true, process_exit_proven: false } });
  });

  it("does not prove process exit when an observed PID has no process-start identity", async () => {
    const fixture = makeFixture();
    fixture.processStartUtc = null;
    fixture.windowPresent = false;
    fixture.onSleep = () => fixture.writeLifecycle("accepted", "stopping");

    const result = await call(fixture);

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ process: { process_generation_unknown: true, process_exit_proven: false } });
  });

  it("accepts exact process exit when the independent window scan is partial", async () => {
    const fixture = makeFixture();
    fixture.processPresent = false;
    fixture.windowPresent = false;
    fixture.windowErrors = ["tasklist_failed:Unity.exe: access_denied"];
    fixture.onSleep = () => fixture.writeLifecycle("accepted", "stopping");

    const result = await call(fixture);

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        close_observed: true,
        lifecycle: "closed_verified",
        quit_reconciliation: {
          terminal_reason: "closed_verified",
          process: { exact_generation_present: false, process_exit_proven: true },
          window: { scan_supported: false, errors: fixture.windowErrors },
        },
      });
    }
  });

  it("keeps process and window reconciliation inside one quit deadline", async () => {
    const fixture = makeFixture();
    fixture.processScanDelayMs = 500;
    fixture.windowScanDelayMs = 500;

    const result = await call(fixture);

    expect(fixture.processScanCalls).toBe(1);
    expect(fixture.windowScanCalls).toBe(1);
    expect(fixture.processScanBudgets).toEqual([500]);
    expect(fixture.windowScanBudgets).toEqual([500]);
    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ elapsed_ms: 1000, automatic_second_quit_attempted: false });
  });

  it("does not start a final OS scan after a window helper consumes the quit deadline", async () => {
    const fixture = makeFixture();
    fixture.windowScanDelayMs = 1000;

    const result = await call(fixture);

    expect(fixture.processScanCalls).toBe(1);
    expect(fixture.windowScanCalls).toBe(1);
    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ elapsed_ms: 1000, automatic_second_quit_attempted: false });
  });

  it("keeps cancelled lifecycle uncertain when process evidence is unavailable", async () => {
    const fixture = makeFixture();
    fixture.processSupported = false;
    fixture.onSleep = () => fixture.writeLifecycle("cancelled", "ready");

    const result = await call(fixture);

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("quit_uncertain");
  });

  it("does not reinterpret an old cancelled operation from a generic stopping heartbeat", async () => {
    const fixture = makeFixture();
    fixture.processPresent = false;
    fixture.windowPresent = false;
    fixture.onSleep = () => fixture.writeLifecycle("cancelled", "stopping");

    const result = await call(fixture);

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("quit_uncertain");
  });

  it("fails closed when process-start identity is unavailable", async () => {
    const fixture = makeFixture({ unity_process_start_utc: "" });
    fixture.onSleep = () => {
      fixture.writeLifecycle("accepted", "stopping");
      fixture.processPresent = false;
      fixture.windowPresent = false;
    };

    const result = await call(fixture);

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("quit_uncertain");
      expect(result.error.details).toMatchObject({
        initial_unity_process_start_utc: null,
        process: { exact_generation_present: null, process_exit_proven: false },
      });
    }
  });

  it("recovers an uncertain Bridge timeout only from read-only exact close evidence", async () => {
    const fixture = makeFixture();
    fixture.bridge.thrownError = new BridgeRequestError("bridge_timeout", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "timeout",
      bridge_request_issued: true,
    });
    fixture.onSleep = () => {
      fixture.writeLifecycle("accepted", "stopping");
      fixture.processPresent = false;
      fixture.windowPresent = false;
    };

    const result = await call(fixture);

    expect(fixture.bridge.invokeCount).toBe(1);
    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        close_observed: true,
        quit_reconciliation: {
          initial_bridge_result: { status: "error", code: "bridge_timeout", bridge_request_issued: true },
          close_request_dispatched_once: true,
          automatic_second_quit_attempted: false,
        },
      });
    }
  });

  it("rejects caller-supplied correlation before Bridge dispatch", async () => {
    const fixture = makeFixture();
    const result = await call(fixture, { _editor_quit_operation_id: "quit-20260807-123456-deadbeef" });

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ caller_correlation_override_rejected: true, invalid_field_paths: ["args._editor_quit_operation_id"] });
    expect(fixture.bridge.invokeCount).toBe(0);
  });
});

function makeFixture(overrides: Partial<HeartbeatRecord> = {}) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-121-"));
  tempDirs.push(root);
  const instancesDir = path.join(root, "instances");
  fs.mkdirSync(instancesDir, { recursive: true, mode: 0o700 });
  let nowMs = Date.parse("2026-08-07T10:00:00.000Z");
  let heartbeatGeneration = 1;
  const baseRecord = heartbeat({ ...overrides, last_heartbeat: new Date(nowMs).toISOString() });
  const heartbeatPath = path.join(instancesDir, `${baseRecord.instance_id}.json`);
  fs.writeFileSync(heartbeatPath, JSON.stringify(baseRecord));
  const registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => new Date(nowMs) });
  const bridge = new QuitBridge(new FakeUnityBridgeClient([editorQuitCommand()]));
  const fixture = {
    context: undefined as unknown as DispatcherContext,
    bridge,
    processPresent: true,
    processSupported: true,
    processStartUtc: baseRecord.unity_process_start_utc || null,
    processScanTruncated: false,
    windowPresent: true,
    windowErrors: [] as string[],
    modalPresent: false,
    processScanDelayMs: 0,
    windowScanDelayMs: 0,
    processScanCalls: 0,
    windowScanCalls: 0,
    processScanBudgets: [] as number[],
    windowScanBudgets: [] as number[],
    onSleep: undefined as (() => void) | undefined,
    writeLifecycle: (state: HeartbeatRecord["quit_request_state"], status: HeartbeatRecord["status"]) => {
      heartbeatGeneration += 1;
      fs.writeFileSync(heartbeatPath, JSON.stringify({
        ...baseRecord,
        status,
        heartbeat_generation: heartbeatGeneration,
        last_heartbeat: new Date(nowMs).toISOString(),
        quit_operation_id: bridge.operationId,
        quit_caller_args_hash: bridge.callerArgsHash,
        quit_request_state: state,
        quit_requested_utc: "2026-08-07T10:00:00.000Z",
        quit_state_updated_utc: new Date(nowMs).toISOString(),
      }));
    },
  };
  fixture.context = {
    instancesDir,
    registry,
    bridge,
    now: () => new Date(nowMs),
    sleepMs: async (ms) => {
      nowMs += ms;
      fixture.onSleep?.();
    },
    unityProcessDiagnostics: {
      status: (_projects, _currentProject, scanOptions) => {
        fixture.processScanCalls += 1;
        fixture.processScanBudgets.push(scanOptions?.scan_budget_ms ?? 5000);
        nowMs += fixture.processScanDelayMs;
        return processStatus(fixture.processPresent, baseRecord, fixture.processStartUtc, fixture.processScanTruncated, fixture.processSupported);
      },
      terminate: () => { throw new Error("editor.quit reconciliation must not terminate Unity"); },
    },
    unityWindowDiagnostics: {
      status: (scanOptions) => {
        fixture.windowScanCalls += 1;
        fixture.windowScanBudgets.push(scanOptions?.scan_budget_ms ?? 5000);
        nowMs += fixture.windowScanDelayMs;
        return windowStatus(fixture.windowPresent, fixture.modalPresent, baseRecord.unity_pid, fixture.windowErrors);
      },
    },
  };
  bridge.onInvoke = () => fixture.writeLifecycle("deferred", "ready");
  return fixture;
}

function call(fixture: ReturnType<typeof makeFixture>, overrides: Record<string, unknown> = {}) {
  return callWithArgs(fixture, { dry_run: false, confirm_close: true, wait_timeout_ms: 1_000, ...overrides });
}

function callWithArgs(fixture: ReturnType<typeof makeFixture>, args: Record<string, unknown>) {
  return dispatchTool("sgg_unity_call", {
    project: "game",
    command: "editor.quit",
    args_json: JSON.stringify(args),
    mode: "wait",
    request_id: "pr-121",
  }, fixture.context);
}

function editorQuitCommand(): CommandDescriptor {
  return {
    id: "editor.quit",
    title: "Quit Unity Editor safely",
    description: "Quit Unity Editor safely",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 70_000,
    schema: {
      type: "object",
      properties: {
        dry_run: { type: "boolean" },
        confirm_close: { type: "boolean" },
        wait_timeout_ms: { type: "integer", minimum: 1_000, maximum: 60_000 },
        _editor_quit_operation_id: { type: "string", pattern: "^quit-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$" },
        _editor_quit_caller_args_hash: { type: "string", pattern: "^sha256:[0-9a-f]{64}$" },
      },
      additionalProperties: false,
    },
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  };
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-current",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [],
    unity_version: "6000.5.5f1",
    unity_pid: 12345,
    unity_process_start_utc: "2026-08-07T08:00:00.000Z",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-a",
    heartbeat_generation: 1,
    quit_operation_id: "",
    quit_caller_args_hash: "",
    quit_request_state: "none",
    quit_requested_utc: "",
    quit_state_updated_utc: "",
    last_heartbeat: "2026-08-07T10:00:00.000Z",
    ...overrides,
  };
}

function processStatus(present: boolean, record: HeartbeatRecord, processStartUtc: string | null, scanTruncated: boolean, supported: boolean): UnityProcessListResult {
  return {
    schema_version: 1,
    supported,
    process_count: present ? 1 : 0,
    scan_truncated: scanTruncated,
    processes: present ? [{
      pid: record.unity_pid,
      parent_pid: null,
      process_name: "Unity.exe",
      role: "unity_editor",
      process_start_utc: processStartUtc,
      executable_path: null,
      command_line_redacted: "Unity.exe -projectPath <redacted>",
      parsed_project_path: "C:/u/game",
      canonical_project_root: "c:/u/game",
      heartbeat_match_count: 1,
      heartbeat_matches: [],
      current_project_match: true,
      eligible_for_terminate: true,
      terminate_refusal_reason: null,
    }] : [],
    errors: [],
    next_valid_actions: [],
  };
}

function windowStatus(present: boolean, modalPresent: boolean, pid: number, errors: string[]): UnityWindowDiagnosticsStatus {
  const modal: UnityNativeModalInfo = {
    hwnd: "0x500500",
    pid,
    process_name: "Unity",
    process_start_utc: "2026-08-07T08:00:00.000Z",
    class_name: "#32770",
    window_title: "Save changes?",
    child_texts: [],
    source: "user32",
    likely_modal: true,
    likely_unity_licensing_client_crash: false,
    likely_corrupted_library: false,
    likely_safe_mode_prompt: false,
    likely_missing_signature_prompt: false,
    likely_external_scene_reload_prompt: false,
    matched_keywords: ["save"],
    block_code: "blocked_safe_mode_prompt",
  };
  return {
    schema_version: 1,
    supported: true,
    active_unity_windows_count: present ? 1 : 0,
    active_unity_windows: present ? [{ pid, image_name: "Unity.exe", window_title: "Game - Unity", source: "tasklist", responding: true, likely_modal: false, likely_recovery_prompt: false, matched_keywords: [] }] : [],
    native_modal_candidates_count: modalPresent ? 1 : 0,
    native_modal_candidates: modalPresent ? [modal] : [],
    top_level_window_scan_authoritative: errors.length === 0,
    native_modal_scan_authoritative: true,
    aggregate_scan_authoritative: errors.length === 0,
    scan_budget_ms: 5000,
    scan_elapsed_ms: 2,
    helper_observations: {
      tasklist: helperObservation(errors.length === 0),
      native_powershell: helperObservation(true),
    },
    semantic_channels: {
      top_level_inventory: { status: errors.length === 0 ? "authoritative" : "failed", authoritative: errors.length === 0, coverage: "fixture", parsed_count: present ? 1 : 0, source_helper: "tasklist" },
      native_user32: { status: "authoritative", authoritative: true, coverage: "fixture", parsed_count: modalPresent ? 1 : 0, source_helper: "native_powershell" },
      native_uia_scoped_fallback: { status: "unsupported", authoritative: false, coverage: "fixture", parsed_count: 0, source_helper: "native_powershell" },
    },
    licensing_client_crash_modal_candidates_count: 0,
    licensing_client_crash_modal_candidates: [],
    corrupted_library_modal_candidates_count: 0,
    corrupted_library_modal_candidates: [],
    safe_mode_modal_candidates_count: 0,
    safe_mode_modal_candidates: [],
    missing_signature_modal_candidates_count: 0,
    missing_signature_modal_candidates: [],
    external_scene_reload_modal_candidates_count: 0,
    external_scene_reload_modal_candidates: [],
    native_progress_windows_count: 0,
    native_progress_windows: [],
    domain_reload_progress_windows_count: 0,
    domain_reload_progress_windows: [],
    domain_reload_progress_stall_count: 0,
    recovery_prompt_candidates_count: 0,
    recovery_prompt_candidates: [],
    blocking_startup_possible: modalPresent,
    blocking_automation_possible: modalPresent,
    blocking_progress_possible: false,
    blocking_reload_possible: false,
    errors,
    next_valid_actions: [],
  };
}

function helperObservation(authoritative: boolean): UnityWindowDiagnosticsStatus["helper_observations"]["tasklist"] {
  return {
    status: authoritative ? "ok" : "error",
    authoritative,
    elapsed_ms: 1,
    exit_code: authoritative ? 0 : null,
    timed_out: false,
    stdout_bytes: 0,
    stderr_bytes: 0,
    stdout_truncated: false,
    stderr_truncated: false,
    parsed_row_count: 0,
    normalized_error_code: authoritative ? null : "fixture_error",
    last_stage: null,
    stdout_encoding: "utf8",
    cleanup_status: "not_required",
  };
}

class QuitBridge implements BridgeClient {
  invokeCount = 0;
  operationId = "";
  callerArgsHash = "";
  onInvoke: (() => void) | undefined;
  thrownError: BridgeRequestError | undefined;
  response: ((record: HeartbeatRecord, request: BridgeInvokeRequest) => BridgeInvokeResponse) | undefined;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector?: string, transport?: BridgeTransportOptions) {
    return this.inner.describe(record, selector ?? "commands", transport);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge", limit: number, transport?: BridgeTransportOptions) {
    return this.inner.logs(record, kind, limit, transport);
  }

  async invoke(_record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    this.operationId = String(request.args._editor_quit_operation_id ?? "");
    this.callerArgsHash = String(request.args._editor_quit_caller_args_hash ?? "");
    if (request.args.dry_run !== false) {
      return {
        status: "ok",
        data: { command: "editor.quit", dry_run: true, close_requested: false, lifecycle: "preview" },
      };
    }
    this.onInvoke?.();
    if (this.thrownError) throw this.thrownError;
    if (this.response) return this.response(_record, request);
    return {
      status: "ok",
      data: {
        command: "editor.quit",
        close_requested: true,
        lifecycle: "scheduled",
        quit_operation_id: this.operationId,
        quit_request_state: "deferred",
      },
    };
  }
}

function quitRefusal(record: HeartbeatRecord, request: BridgeInvokeRequest): BridgeInvokeResponse {
  return { status: "error", error: { code: "unity_busy", message: "Play close is unsupported", details: {
    command: "editor.quit", reason: "playmode", close_requested: false,
    editor_quit_preflight_refusal: {
      schema_version: 1, contract: "sgg.unity_mcp.editor_quit_preflight_refusal.v1", command: "editor.quit",
      operation_id: request.args._editor_quit_operation_id, caller_args_hash: request.args._editor_quit_caller_args_hash,
      reason: "playmode", phase: "preflight", scope: "this_invocation", binding_complete: true,
      owner: { project: record.project, instance_id: record.instance_id, canonical_project_root: record.canonical_project_root,
        unity_pid: record.unity_pid, unity_process_start_utc: record.unity_process_start_utc },
      close_requested: false, quit_lifecycle_started: false, exit_scheduled: false, editor_exit_invoked: false,
      automatic_replay_attempted: false, automatic_process_termination_attempted: false,
      retained_native_custody_observed: false, retained_native_close_supported: false,
    },
  } } };
}
