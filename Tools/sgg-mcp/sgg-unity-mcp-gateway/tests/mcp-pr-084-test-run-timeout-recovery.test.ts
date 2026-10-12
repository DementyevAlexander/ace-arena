import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { argsHash } from "../src/approvals/approval-store.js";
import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { ProjectQueueManager } from "../src/routing/project-queue.js";

const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("MCP-PR-084 test.run timeout recovery", () => {
  it("reserves an exact operation id before one issued request and exposes executable timeout recovery", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_timeout", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "timeout",
      abort_source: "deadline",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", { mode: "editmode", filter: "Example" });

    expect(bridge.invokeCount).toBe(1);
    expect(bridge.operationId).toMatch(/^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        test_run_recovery_contract: "sgg.unity_mcp.test_run_timeout.v1",
        operation_id: bridge.operationId,
        operation_id_source: "gateway_reserved",
        delivery_state: "uncertain",
        test_run_delivery_state: "uncertain",
        bridge_request_issued: true,
        test_run_replayed: false,
        repeat_blocked_until_delivery_state_checked: true,
        artifact_presence: "unknown",
        status_lookup: {
          tool: "sgg_unity_call",
          project: "game",
          command: "test.run.status",
          args_json: JSON.stringify({ operation_id: bridge.operationId }),
          mode: "wait",
        },
      });
      expect(result.error.details).not.toHaveProperty("artifact_refs");
      expect(result.next_valid_actions).toContain("do_not_repeat_test.run_until_delivery_state_is_resolved");
    }
  });

  it("never replays an issued test.run when a malformed descriptor misstates its risk as read", async () => {
    const fixture = makeFixture([{ ...testRunCommand(), risk: "read", idempotency: "safe" }]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_unreachable", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "connection",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", { mode: "editmode" });

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        test_run_recovery_contract: "sgg.unity_mcp.test_run_timeout.v1",
        operation_id: bridge.operationId,
        run_id: bridge.operationId,
        operation_id_source: "gateway_reserved",
        delivery_state: "uncertain",
        test_run_delivery_state: "uncertain",
        command_delivery_state: "uncertain",
        bridge_request_issued: true,
        command_bridge_request_issued: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        test_run_replayed: false,
        new_operation_id_allocated: false,
        replacement_operation_allocated: false,
        replacement_test_run_dispatched: false,
        status_lookup: {
          tool: "sgg_unity_call",
          project: "game",
          command: "test.run.status",
          args_json: JSON.stringify({ operation_id: bridge.operationId }),
          mode: "wait",
        },
      });
      expect(result.error.details).not.toHaveProperty("explicit_repeat_workflow");
      expect(result.error.details).not.toHaveProperty("repeat_request_template");
      expect(result.error.details?.recovery_actions).toEqual([
        "poll_exact_test.run.status",
        "inspect_expected_test_run_artifact",
        "call_sgg_unity_projects_if_status_is_unavailable",
        "do_not_repeat_test.run_until_delivery_state_is_resolved",
      ]);
      expect(result.next_valid_actions).toEqual(result.error.details?.recovery_actions);
      expect(JSON.stringify(result.error.details)).not.toContain("new_unique_request_id");
      expect(JSON.stringify(result.error.details)).not.toContain("explicitly_repeat_original");
    }
  });

  it("prefers Gateway request evidence over a Bridge non-delivery claim", async () => {
    const fixture = makeFixture([{ ...testRunCommand(), risk: "read", idempotency: "safe" }]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.response = {
      status: "error",
      error: {
        code: "bridge_unreachable",
        message: "Bridge claimed the issued request was not dispatched",
        details: { command_delivery_state: "not_dispatched" },
      },
    };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", { mode: "editmode" });

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        operation_id: bridge.operationId,
        run_id: bridge.operationId,
        operation_id_source: "gateway_reserved",
        delivery_state: "uncertain",
        test_run_delivery_state: "uncertain",
        command_delivery_state: "uncertain",
        bridge_request_issued: true,
        command_bridge_request_issued: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        test_run_replayed: false,
        new_operation_id_allocated: false,
        replacement_operation_allocated: false,
        replacement_test_run_dispatched: false,
      });
      expect(result.next_valid_actions).toContain("poll_exact_test.run.status");
      expect(result.next_valid_actions).toContain("do_not_repeat_test.run_until_delivery_state_is_resolved");
      expect(result.next_valid_actions).not.toContain("retry_original_command_only_if_still_intended");
      expect(result.error.details).not.toHaveProperty("explicit_repeat_workflow");
    }
  });

  it("preserves the reserved operation id on a successful response", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", { mode: "editmode" });

    expect(bridge.invokeCount).toBe(1);
    expect(bridge.callerArgsHash).toBe(argsHash({ mode: "editmode" }));
    expect(result.status).toBe("ok");
    if (result.status === "ok") expect(result.data).toMatchObject({ operation_id: bridge.operationId, run_id: bridge.operationId });
  });

  it("treats authoritative pre-request transport expiry as not dispatched", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_timeout", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "timeout",
      abort_source: "deadline",
      bridge_request_issued: false,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ bridge_request_issued: false, test_run_delivery_state: "not_dispatched", unity_dispatch_state: "bridge_request_not_issued" });
      expect(result.next_valid_actions).toContain("repeat_test.run_only_if_still_intended");
    }
  });

  it("keeps a typed cancelled-before-start timeout authoritative without claiming an artifact", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.response = {
      status: "error",
      error: {
        code: "command_timeout",
        message: "Unity command timed out",
        details: { main_thread_dispatch: { timed_out_work_state: "cancelled_before_start", late_execution_possible: false } },
      },
    };
    bridge.beforeResponse = () => fixture.advanceTime(10_000);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ operation_id: bridge.operationId, test_run_delivery_state: "not_dispatched", artifact_presence: "unknown" });
      expect(result.next_valid_actions).toContain("repeat_test.run_only_if_still_intended");
    }
    expect(bridge.lastTransport?.deadlineUnixMs).toBe(fixture.startedAtMs + 12_000);
    expect(bridge.lastTransport?.timeoutMs).toBe(12_000);
  });

  it("starts the Unity response grace after queue admission instead of consuming it in the queue", async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const queues = new ProjectQueueManager();
    const blocker = queues.run("game", () => gate);
    const fixture = makeFixture([testRunCommand()]);
    fixture.context.queues = queues;
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.response = {
      status: "error",
      error: { code: "command_timeout", message: "Unity command timed out", details: { main_thread_dispatch: { timed_out_work_state: "cancelled_before_start", late_execution_possible: false } } },
    };
    bridge.beforeResponse = () => fixture.advanceTime(10_000);
    fixture.context.bridge = bridge;

    const pending = call(fixture.context, "test.run", {});
    fixture.advanceTime(5_000);
    release();
    await blocker;
    const result = await pending;

    expect(result.status).toBe("error");
    expect(bridge.lastTransport?.deadlineUnixMs).toBe(fixture.startedAtMs + 17_000);
    expect(bridge.lastTransport?.timeoutMs).toBe(12_000);
  });

  it("attaches the exact operation recovery path to an empty response without replay", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_protocol_error", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "response_parse",
      parse_error_kind: "empty_body",
      http_status: 200,
      response_body_bytes: 0,
      response_body_empty: true,
      response_body_truncated: false,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(bridge.invokeCount).toBe(1);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ operation_id: bridge.operationId, test_run_delivery_state: "uncertain", test_run_replayed: false });
      expect(result.error.details).not.toHaveProperty("repeat_request_template");
    }
  });

  it("expires in the project queue without issuing test.run later", async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const queues = new ProjectQueueManager();
    const blocker = queues.run("game", () => gate);
    const fixture = makeFixture([testRunCommand(30)], true);
    fixture.context.queues = queues;
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ test_run_delivery_state: "not_dispatched", bridge_request_issued: false });
    expect(bridge.invokeCount).toBe(0);
    release();
    await blocker;
    await new Promise<void>((resolve) => setTimeout(resolve, 20));
    expect(bridge.invokeCount).toBe(0);
  });

  it("rejects identifier-free test.run.status before Bridge dispatch", async () => {
    const fixture = makeFixture([testRunStatusCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run.status", {});

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("schema_validation_failed");
    expect(bridge.invokeCount).toBe(0);
  });

  it("rejects caller-supplied reserved correlation fields before approval or Bridge dispatch", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", { _test_run_operation_id: "test-20260716-120000-deadbeef" });

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ caller_correlation_override_rejected: true, invalid_field_paths: ["args._test_run_operation_id"] });
    expect(bridge.invokeCount).toBe(0);
  });

  it("preserves every safe returned operation candidate when response aliases conflict", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.returnConflictingRunId = true;
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        protocol_failure: "test_run_operation_id_mismatch",
        returned_operation_id_state: "matching",
        returned_run_id_state: "mismatching",
        uncertain_operation_id_candidates: [bridge.operationId, "test-20260716-120000-bad0cafe"],
        all_candidate_statuses_must_be_checked_before_repeat: true,
      });
      expect(result.error.details?.candidate_status_lookups).toHaveLength(2);
    }
  });

  it("keeps the reserved recovery identity on a post-dispatch startup failure", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.response = { status: "error", error: { code: "unity_exception", message: "Unity test run failed during startup" } };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ operation_id: bridge.operationId, startup_error_returned: true, test_run_delivery_state: "uncertain" });
      expect(result.next_valid_actions).toContain("poll_exact_test.run.status");
    }
  });

  it.each(["test_run_dirty_scene_blocked", "test_run_scene_state_unavailable"] as const)(
    "preserves definite pre-execution blocker %s without uncertain operation recovery",
    async (code) => {
      const fixture = makeFixture([testRunCommand()]);
      const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
      bridge.response = {
        status: "error",
        error: {
          code,
          message: "Unity refused test.run before Execute",
          details: { test_runner_execute_invoked: false, native_save_prompt_answered: false, scene_state_mutated: false },
        },
      };
      fixture.context.bridge = bridge;

      const result = await call(fixture.context, "test.run", {});

      expect(result.status).toBe("error");
      if (result.status === "error") {
        expect(result.error.code).toBe(code);
        expect(result.error.details).toMatchObject({ test_runner_execute_invoked: false, native_save_prompt_answered: false, scene_state_mutated: false });
        expect(result.error.details).not.toHaveProperty("test_run_recovery_contract");
        expect(result.next_valid_actions ?? []).not.toContain("poll_exact_test.run.status");
      }
    },
  );

  it("preserves the existing operation identity when Unity rejects a second active test run", async () => {
    const fixture = makeFixture([testRunCommand()]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    bridge.response = {
      status: "error",
      error: {
        code: "unity_test_run_busy",
        message: "A Unity test run is already active or awaiting callback restoration",
        details: { operation_created: false, test_runner_execute_invoked: false, active_operation_id: "test-20260822-194309-84e6e091" },
      },
    };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("unity_test_run_busy");
      expect(result.error.details).toMatchObject({ operation_created: false, test_runner_execute_invoked: false, active_operation_id: "test-20260822-194309-84e6e091" });
      expect(result.error.details).not.toHaveProperty("operation_id", bridge.operationId);
      expect(result.next_valid_actions).toContain("poll_active_test_run_status");
    }
  });

  it("does not claim correlation for an older descriptor without the capability tag", async () => {
    const fixture = makeFixture([testRunCommand(10_000, false)]);
    const bridge = new TestRunBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "test.run", {});

    expect(bridge.operationId).toBe("");
    expect(result.status).toBe("ok");
  });
});

function makeFixture(commands: CommandDescriptor[], realClock = false) {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-084-"));
  tempDirs.push(instancesDir);
  const record = heartbeat();
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));
  const startedAtMs = Date.parse("2026-07-16T12:00:00.000Z");
  let currentTimeMs = startedAtMs;
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000 }),
    bridge: new FakeUnityBridgeClient(commands),
    ...(realClock ? {} : { now: () => new Date(currentTimeMs) }),
  };
  return { context, startedAtMs, advanceTime: (milliseconds: number) => { currentTimeMs += milliseconds; } };
}

function call(context: DispatcherContext, command: string, args: Record<string, unknown>) {
  return dispatchTool("sgg_unity_call", { project: "game", command, args_json: JSON.stringify(args), mode: "wait", request_id: "pr-084" }, context);
}

function heartbeat(): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-current",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [],
    unity_version: "6000.5.3f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-a",
    last_heartbeat: new Date().toISOString(),
  };
}

function command(id: string, risk: CommandDescriptor["risk"], timeoutMs: number, schema: Record<string, unknown>): CommandDescriptor {
  return { id, title: id, description: id, package: "com.sungeargames.unity-mcp", risk, idempotency: risk === "read" ? "safe" : "unsafe", timeout_ms: timeoutMs, schema, source: "builtin", package_id: "com.sungeargames.unity-mcp", trust: "builtin" };
}

function testRunCommand(timeoutMs = 10_000, correlated = true): CommandDescriptor {
  return {
    ...command("test.run", "execute", timeoutMs, {
      type: "object",
      properties: {
        mode: { type: "string", enum: ["editmode", "playmode"] },
        filter: { type: "string", maxLength: 160 },
      },
      additionalProperties: false,
    }),
    ...(correlated ? { tags: ["test", "automation", "test_run_operation_correlated"] } : {}),
  };
}

function testRunStatusCommand(): CommandDescriptor {
  return command("test.run.status", "read", 10_000, {
    type: "object",
    properties: {
      operation_id: { type: "string", pattern: "^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$" },
      run_id: { type: "string", pattern: "^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$" },
    },
    minProperties: 1,
    additionalProperties: false,
  });
}

class TestRunBridge implements BridgeClient {
  invokeCount = 0;
  operationId = "";
  callerArgsHash = "";
  response: BridgeInvokeResponse | undefined;
  thrownError: BridgeRequestError | undefined;
  beforeResponse: (() => void) | undefined;
  returnConflictingRunId = false;
  lastTransport: BridgeTransportOptions | undefined;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    this.lastTransport = transport;
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    this.operationId = typeof request.args._test_run_operation_id === "string" ? request.args._test_run_operation_id : "";
    this.callerArgsHash = typeof request.args._test_run_caller_args_hash === "string" ? request.args._test_run_caller_args_hash : "";
    this.beforeResponse?.();
    if (this.thrownError) throw this.thrownError;
    if (this.response) return this.response;
    if (request.command === "test.run") {
      const operationId = this.operationId || "test-20260716-120000-deadbeef";
      return { status: "ok", data: { operation_id: operationId, run_id: this.returnConflictingRunId ? "test-20260716-120000-bad0cafe" : operationId, status: "running" } };
    }
    return this.inner.invoke(record, request, transport);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}
