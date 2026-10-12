import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { argsHash } from "../src/approvals/approval-store.js";
import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { PolicyManager } from "../src/policy/policy-manager.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { ProjectQueueManager } from "../src/routing/project-queue.js";

const ARTIFACT_ROOT = "Library/SGGUnityMcp/job-artifacts/dynamic-csharp-readonly";
const tempDirs: string[] = [];

afterEach(() => {
  for (const directory of tempDirs.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

describe("MCP-PR-213 dynamic C# read-only result continuity", () => {
  it.each([
    ["artifact_capacity_exhausted", 64, true],
    ["artifact_capacity_unavailable", 257, false],
    ["artifact_status_unavailable", 3, false],
  ])("preserves %s count-only admission evidence without automatic status, fallback or replay", async (reason, observed, complete) => {
    const fixture = makeFixture();
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;
    const capacity = {
      observation_source: "matching_file_enumeration", observed_matching_files: observed, enumeration_complete: complete,
      atomic_snapshot: false, max_retained_artifacts: 64, max_matching_files_scan: 256,
      automatic_retention_cleanup_enabled: false, automatic_release_eligible_count: 0,
      automatic_release_eligibility_source: "disabled_by_contract", receipt_state_classification: "not_performed", receipt_age_classification: "not_performed",
    };
    bridge.responseForOperation = (operationId) => ({ status: "error", error: {
      code: "dynamic_csharp_result_unavailable", message: "Dynamic C# read-only result store is unavailable",
      details: { operation_id: operationId, reason, execution_may_have_started: false, command_replayed: false, capacity_observation: capacity },
    } });

    const result = await call(fixture.context, "editor.execute_csharp.readonly", { code: "return 42;" });

    expect(bridge.invokeCount).toBe(1);
    expect(bridge.issuedCount).toBe(1);
    expect(result).toMatchObject({ status: "error", error: { code: "dynamic_csharp_result_unavailable", details: {
      operation_id: bridge.operationId, reason, execution_may_have_started: false, command_replayed: false, capacity_observation: capacity,
    } } });
    // The one synthetic Bridge request returned a store-admission refusal;
    // these counters do not claim real Unity handler/body execution evidence.
    expect(JSON.stringify(result)).not.toContain("unsafe_execute_recovery_enabled\":true");
  });

  it("reserves one operation identity and binds caller, snippet, queue, and owner catalog evidence", async () => {
    const fixture = makeFixture();
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;
    const callerArgs = { code: "return 42;", response_mode: "compact" };

    const result = await call(fixture.context, "editor.execute_csharp.readonly", callerArgs);

    expect(bridge.invokeCount, JSON.stringify(result)).toBe(1);
    expect(bridge.operationId).toMatch(/^csharp-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/);
    expect(bridge.callerArgsHash).toBe(argsHash(callerArgs));
    expect(bridge.snippetHash).toBe(sha256("return 42;"));
    expect(bridge.queueWaitMs).toBeGreaterThanOrEqual(0);
    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        operation_id: bridge.operationId,
        artifact_path: `${ARTIFACT_ROOT}/${bridge.operationId}.json`,
        caller_args_hash: bridge.callerArgsHash,
        snippet_hash: bridge.snippetHash,
        terminal: true,
        result_available: true,
      });
    }
  });

  it("returns one exact status lookup after uncertain Bridge delivery without replay", async () => {
    const fixture = makeFixture();
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_timeout", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "timeout",
      abort_source: "deadline",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.execute_csharp.readonly", { code: "return 42;" });

    expect(bridge.invokeCount, JSON.stringify(result)).toBe(1);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        dynamic_csharp_readonly_recovery_contract: "sgg.unity_mcp.dynamic_csharp_readonly_recovery.v1",
        operation_id: bridge.operationId,
        operation_id_source: "gateway_reserved",
        caller_args_hash: bridge.callerArgsHash,
        snippet_hash: bridge.snippetHash,
        delivery_state: "uncertain",
        failure_phase: "bridge_response_delivery",
        bridge_request_issued: true,
        automatic_replay_attempted: false,
        command_replayed: false,
        unsafe_execute_recovery_enabled: false,
        expected_artifact_path: `${ARTIFACT_ROOT}/${bridge.operationId}.json`,
        status_lookup: {
          project: fixture.record.instance_id,
          command: "editor.execute_csharp.readonly.status",
          args_json: JSON.stringify({ operation_id: bridge.operationId }),
        },
      });
      expect(result.next_valid_actions).toContain("do_not_repeat_editor.execute_csharp.readonly_until_delivery_state_is_resolved");
    }
  });

  it("rejects caller correlation overrides before policy or Bridge dispatch", async () => {
    const fixture = makeFixture();
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;
    const policyPath = path.join(fixture.root, "policy.json");
    fs.writeFileSync(policyPath, JSON.stringify({ schema_version: 1, defaults: { read: "allow", write: "deny", execute: "deny", destructive: "deny", network: "deny" }, projects: {} }));
    fixture.context.policyManager = new PolicyManager(policyPath);

    const result = await call(fixture.context, "editor.execute_csharp.readonly", {
      code: "return 42;",
      _dynamic_csharp_operation_id: "csharp-20260922-010203-deadbeef",
      _dynamic_csharp_caller_args_hash: sha256("caller"),
      _dynamic_csharp_queue_wait_ms: 12,
    });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        caller_correlation_override_rejected: true,
        invalid_field_paths: ["args._dynamic_csharp_operation_id", "args._dynamic_csharp_caller_args_hash", "args._dynamic_csharp_queue_wait_ms"],
      });
    }
    expect(bridge.invokeCount).toBe(0);
  });

  it("expires in the project queue without a late read-only execution", async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const queues = new ProjectQueueManager();
    const blocker = queues.run("game", () => gate);
    const fixture = makeFixture(30);
    fixture.context.queues = queues;
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.execute_csharp.readonly", { code: "return 42;" });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        delivery_state: "not_dispatched",
        failure_phase: "project_queue_admission",
        bridge_request_issued: false,
        operation_created: false,
        status_lookup: null,
      });
    }
    expect(bridge.invokeCount).toBe(0);
    release();
    await blocker;
    await new Promise<void>((resolve) => setTimeout(resolve, 20));
    expect(bridge.invokeCount).toBe(0);
  });

  it("expires after queue admission without issuing a late Bridge request", async () => {
    const fixture = makeFixture(30);
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    bridge.beforeIssueDelayMs = 60;
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.execute_csharp.readonly", { code: "return 42;" });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        caller_args_hash: argsHash({ code: "return 42;" }),
        snippet_hash: sha256("return 42;"),
        delivery_state: "not_dispatched",
        failure_phase: "bridge_request_admission",
        project_queue_admission: { state: "completed" },
        bridge_request_issued: false,
        status_lookup: null,
      });
    }
    await new Promise<void>((resolve) => setTimeout(resolve, 80));
    expect(bridge.invokeCount).toBe(1);
    expect(bridge.issuedCount).toBe(0);
  });

  it("never replays issued C# execution when a catalog misclassifies its risk as read", async () => {
    const fixture = makeFixture(100, "read");
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_unreachable", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "connection",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;
    const result = await call(fixture.context, "editor.execute_csharp", { code: "return 42;" });

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    expect(bridge.issuedCount).toBe(1);
  });

  it("never replays issued read-only C# when its catalog risk drifts to read", async () => {
    const fixture = makeFixture(100, "execute", "read");
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_unreachable", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "connection",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.execute_csharp.readonly", { code: "return 42;" });

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    expect(bridge.issuedCount).toBe(1);
  });

  it("preserves exact recovery binding when an issued request exceeds the outer deadline", async () => {
    const fixture = makeFixture(30);
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    bridge.afterIssueDelayMs = 60;
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.execute_csharp.readonly", { code: "return 42;" });

    expect(result.status).toBe("error");
    let operationId = "";
    if (result.status === "error") {
      operationId = typeof result.error.details?.operation_id === "string" ? result.error.details.operation_id : "";
      expect(operationId).toMatch(/^csharp-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/);
      expect(result.error.details).toMatchObject({
        operation_id: operationId,
        caller_args_hash: argsHash({ code: "return 42;" }),
        snippet_hash: sha256("return 42;"),
        delivery_state: "uncertain",
        failure_phase: "bridge_response_delivery",
        bridge_request_issued: true,
        status_lookup: {
          project: fixture.record.instance_id,
          command: "editor.execute_csharp.readonly.status",
          args_json: JSON.stringify({ operation_id: operationId }),
        },
      });
    }
    await new Promise<void>((resolve) => setTimeout(resolve, 80));
    expect(bridge.invokeCount).toBe(1);
    expect(bridge.issuedCount).toBe(1);
    expect(bridge.operationId).toBe(operationId);
  });

  it("keeps malformed delivered correlation evidence internally consistent", async () => {
    const fixture = makeFixture();
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    bridge.response = { status: "ok", data: {} };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.execute_csharp.readonly", { code: "return 42;" });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ delivery_state: "delivered", bridge_request_issued: true });
      expect(result.next_valid_actions).toEqual([
        "call_exact_editor.execute_csharp.readonly.status_if_result_recovery_is_needed",
        "do_not_rerun_the_snippet_for_recovery",
      ]);
    }
  });

  it("does not attach read-only recovery identity to ordinary unsafe execute", async () => {
    const fixture = makeFixture();
    const bridge = new DynamicCSharpBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_timeout", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "timeout",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.execute_csharp", { code: "return 42;" });

    expect(bridge.invokeCount).toBe(1);
    expect(bridge.operationId).toBe("");
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).not.toHaveProperty("dynamic_csharp_readonly_recovery_contract");
      expect(result.error.details).not.toHaveProperty("operation_id");
    }
  });
});

function makeFixture(timeoutMs = 10_000, unsafeRisk: CommandDescriptor["risk"] = "execute", readonlyRisk: CommandDescriptor["risk"] = "execute") {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-213-"));
  tempDirs.push(root);
  const instancesDir = path.join(root, "instances");
  const projectRoot = path.join(root, "project");
  fs.mkdirSync(instancesDir, { recursive: true, mode: 0o700 });
  fs.mkdirSync(projectRoot, { recursive: true });
  const record = heartbeat(projectRoot);
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));
  const unsafe = unsafeCommand(timeoutMs);
  unsafe.risk = unsafeRisk;
  const readonly = readonlyCommand(timeoutMs);
  readonly.risk = readonlyRisk;
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000 }),
    bridge: new FakeUnityBridgeClient([readonly, readonlyStatusCommand(), unsafe]),
    features: { editor_csharp_execution: true },
  };
  return { context, record, root };
}

function call(context: DispatcherContext, command: string, args: Record<string, unknown>) {
  return dispatchTool("sgg_unity_call", {
    project: "game",
    command,
    args_json: JSON.stringify(args),
    mode: "wait",
    request_id: "pr-213",
  }, context);
}

function readonlyCommand(timeoutMs: number): CommandDescriptor {
  return {
    id: "editor.execute_csharp.readonly",
    title: "Execute read-only C#",
    description: "Execute bounded read-only C#",
    package: "com.sungeargames.unity-mcp",
    risk: "execute",
    idempotency: "unsafe",
    timeout_ms: timeoutMs,
    schema: executeSchema(),
    tags: ["editor", "csharp", "readonly", "dynamic_csharp_readonly_operation_correlated"],
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  };
}

function unsafeCommand(timeoutMs: number): CommandDescriptor {
  return {
    ...readonlyCommand(timeoutMs),
    id: "editor.execute_csharp",
    title: "Execute C#",
    tags: ["editor", "csharp"],
  };
}

function readonlyStatusCommand(): CommandDescriptor {
  return {
    id: "editor.execute_csharp.readonly.status",
    title: "Read dynamic C# result",
    description: "Read one exact result",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10_000,
    schema: {
      type: "object",
      properties: { operation_id: { type: "string", pattern: "^csharp-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$" } },
      required: ["operation_id"],
      additionalProperties: false,
    },
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    package_version: "2026.9.22-2",
    trust: "builtin",
  };
}

function executeSchema(): CommandDescriptor["schema"] {
  return {
    type: "object",
    properties: {
      code: { type: "string", minLength: 1, maxLength: 50_000 },
      max_output_chars: { type: "integer", minimum: 1, maximum: 20_000 },
      response_mode: { type: "string", enum: ["full", "compact"] },
    },
    required: ["code"],
    additionalProperties: false,
  };
}

function heartbeat(projectRoot: string): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-current",
    project: "game",
    display_name: "Game",
    package_version: "2026.9.22-2",
    project_root: projectRoot,
    canonical_project_root: projectRoot,
    aliases: [],
    unity_version: "6000.6.2f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-a",
    last_heartbeat: new Date().toISOString(),
  };
}

function sha256(value: string): string {
  return `sha256:${createHash("sha256").update(value, "utf8").digest("hex")}`;
}

class DynamicCSharpBridge implements BridgeClient {
  invokeCount = 0;
  issuedCount = 0;
  operationId = "";
  callerArgsHash = "";
  snippetHash = "";
  queueWaitMs = -1;
  response: BridgeInvokeResponse | undefined;
  responseForOperation: ((operationId: string) => BridgeInvokeResponse) | undefined;
  thrownError: BridgeRequestError | undefined;
  beforeIssueDelayMs = 0;
  afterIssueDelayMs = 0;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    if (this.beforeIssueDelayMs > 0) await new Promise<void>((resolve) => setTimeout(resolve, this.beforeIssueDelayMs));
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    this.issuedCount += 1;
    if (this.afterIssueDelayMs > 0) await new Promise<void>((resolve) => setTimeout(resolve, this.afterIssueDelayMs));
    this.operationId = typeof request.args._dynamic_csharp_operation_id === "string" ? request.args._dynamic_csharp_operation_id : "";
    this.callerArgsHash = typeof request.args._dynamic_csharp_caller_args_hash === "string" ? request.args._dynamic_csharp_caller_args_hash : "";
    this.queueWaitMs = typeof request.args._dynamic_csharp_queue_wait_ms === "number" ? request.args._dynamic_csharp_queue_wait_ms : -1;
    this.snippetHash = typeof request.args.code === "string" ? sha256(request.args.code) : "";
    if (this.thrownError) throw this.thrownError;
    if (this.responseForOperation) return this.responseForOperation(this.operationId);
    if (this.response) return this.response;
    if (request.command === "editor.execute_csharp.readonly") {
      return {
        status: "ok",
        data: {
          operation_id: this.operationId,
          artifact_path: `${ARTIFACT_ROOT}/${this.operationId}.json`,
          caller_args_hash: this.callerArgsHash,
          snippet_hash: this.snippetHash,
          terminal: true,
          result_available: true,
          phase_evidence: { project_queue_admission: { state: "completed", duration_ms: this.queueWaitMs } },
        },
      };
    }
    return this.inner.invoke(record, request, transport);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}
