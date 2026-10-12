import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { InMemoryAuditSink } from "../src/observability/audit.js";
import { ProjectRegistry, type HeartbeatRecord, type ProjectResolution, type ProjectResolutionOptions, type ReplacementProjectResolution } from "../src/registry/project-registry.js";
import { ProjectQueueManager } from "../src/routing/project-queue.js";

const tempDirs: string[] = [];
const initialNow = Date.parse("2026-07-11T08:13:54.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("MCP-PR-070 empty-body read reconnect", () => {
  it("reconnects an exact instance selector to an immediate replacement by canonical identity", async () => {
    const oldRecord = heartbeat({ instance_id: "game-old", bridge_port: 18142 });
    const replacement = heartbeat({ instance_id: "game-new", bridge_port: 18143, token: "replacement-secret" });
    const context = makeContext([oldRecord, replacement]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("ok");
    expect(bridge.invokedInstanceIds).toEqual(["game-old", "game-new"]);
    expect(bridge.invokedRequests).toEqual([
      { instance_id: "game-old", args: { timeout_ms: 5_000, poll_interval_ms: 50 } },
      { instance_id: "game-new", args: { timeout_ms: 5_000, poll_interval_ms: 50 } },
    ]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        instance_id: "game-new",
        empty_body_read_recovery: {
          attempted: true,
          attempt_count: 1,
          replacement_request_count: 1,
          selector_strategy: "failed_heartbeat_canonical_project_identity",
          failed_instance: { instance_id: "game-old", bridge_port: 18142 },
          replacement_instance: { instance_id: "game-new", bridge_port: 18143 },
          original_error_retained: true,
          original_error: { code: "bridge_protocol_error", classification: { response_body_bytes: 0 } },
          terminal_status: "ready",
        },
      });
      const serialized = JSON.stringify(result);
      expect(serialized).not.toContain("C:/private/project");
      expect(serialized).not.toContain("c:/private/project");
      expect(serialized).not.toContain("replacement-secret");
    }
  });

  it("holds one project queue admission across the failed and replacement bridge attempts", async () => {
    let releaseSleep!: () => void;
    let notifySleep!: () => void;
    const sleepEntered = new Promise<void>((resolve) => { notifySleep = resolve; });
    const sleepReleased = new Promise<void>((resolve) => { releaseSleep = resolve; });
    const oldRecord = heartbeat({ instance_id: "game-old" });
    const replacement = heartbeat({ instance_id: "game-new", bridge_port: 18143 });
    const context = makeContext([oldRecord]);
    const queues = new CountingProjectQueueManager();
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.queues = queues;
    context.bridge = bridge;
    context.sleepMs = async () => {
      writeHeartbeat(context, replacement);
      notifySleep();
      await sleepReleased;
    };

    const recovering = callWaitReady(context, "game-old");
    await sleepEntered;
    const interleaving = callWaitReady(context, "game-new");
    await Promise.resolve();
    await Promise.resolve();
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);

    releaseSleep();
    const [recovered, second] = await Promise.all([recovering, interleaving]);

    expect(recovered.status).toBe("ok");
    expect(second.status).toBe("ok");
    expect(bridge.invokedInstanceIds).toEqual(["game-old", "game-new", "game-new"]);
    expect(queues.admissions).toBe(2);
  });

  it("re-resolves a project selector until a delayed replacement appears after the old instance is removed", async () => {
    let nowMs = initialNow;
    const oldRecord = heartbeat({ instance_id: "game-old", bridge_port: 18142 });
    const replacement = heartbeat({ instance_id: "game-new", bridge_port: 18143, last_heartbeat: new Date(initialNow + 50).toISOString() });
    const context = makeContext([oldRecord]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.bridge = bridge;
    context.now = () => new Date(nowMs);
    let sleeps = 0;
    context.sleepMs = async (ms) => {
      nowMs += ms;
      sleeps += 1;
      if (sleeps === 2) {
        fs.rmSync(path.join(context.instancesDir as string, "game-old.json"));
        writeHeartbeat(context, replacement);
      }
    };

    const result = await callWaitReady(context, "game", { timeout_ms: 10_000, poll_interval_ms: 50 });

    expect(result.status).toBe("ok");
    expect(sleeps).toBe(2);
    expect(bridge.invokedInstanceIds).toEqual(["game-old", "game-new"]);
    expect(bridge.invokedRequests).toEqual([
      { instance_id: "game-old", args: { timeout_ms: 5_000, poll_interval_ms: 50 } },
      { instance_id: "game-new", args: { timeout_ms: 4_900, poll_interval_ms: 50 } },
    ]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({ empty_body_read_recovery: { attempt_count: 3, replacement_request_count: 1, terminal_status: "ready" } });
    }
  });

  it("does not remain pinned to an exact instance selector after that heartbeat is removed", async () => {
    let nowMs = initialNow;
    const oldRecord = heartbeat({ instance_id: "game-old" });
    const context = makeContext([oldRecord]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.bridge = bridge;
    context.now = () => new Date(nowMs);
    context.sleepMs = async (ms) => {
      nowMs += ms;
      fs.rmSync(path.join(context.instancesDir as string, "game-old.json"), { force: true });
      writeHeartbeat(context, heartbeat({ instance_id: "game-new", bridge_port: 18143, last_heartbeat: new Date(nowMs).toISOString() }));
    };

    const result = await callWaitReady(context, "game-old", { timeout_ms: 10_000, poll_interval_ms: 50 });

    expect(result.status).toBe("ok");
    expect(bridge.invokedInstanceIds).toEqual(["game-old", "game-new"]);
  });

  it("expires the original budget without issuing a replacement request", async () => {
    let nowMs = initialNow;
    const context = makeContext([heartbeat({ instance_id: "game-old" })]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.bridge = bridge;
    context.now = () => new Date(nowMs);
    context.sleepMs = async (ms) => { nowMs += ms; };

    const result = await callWaitReady(context, "game-old", { timeout_ms: 100, poll_interval_ms: 50 });

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.error.details).toMatchObject({
        empty_body_read_recovery: { attempted: true, attempt_count: 2, replacement_request_count: 0, terminal_status: "timeout" },
      });
      expect(JSON.stringify(result)).not.toContain("private/project");
    }
  });

  it("fails closed on immediate replacement ambiguity without a replacement POST", async () => {
    const context = makeContext([
      heartbeat({ instance_id: "game-old" }),
      heartbeat({ instance_id: "game-new-a", bridge_port: 18143 }),
      heartbeat({ instance_id: "game-new-b", bridge_port: 18144 }),
    ]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);
    if (result.status === "error") expect(result.error.code).toBe("duplicate_project_id");
  });

  it("uses the transactional replacement result instead of the prior public-list plus exact-id scans", async () => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    const registry = new InjectedAmbiguityRegistry(context.instancesDir as string);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.registry = registry;
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);
    expect(registry.resolveCalls).toBe(1);
    expect(registry.replacementCalls).toBe(1);
  });

  it("fails closed when delayed replacement discovery becomes ambiguous", async () => {
    let nowMs = initialNow;
    const context = makeContext([heartbeat({ instance_id: "game-old" })]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.bridge = bridge;
    context.now = () => new Date(nowMs);
    context.sleepMs = async (ms) => {
      nowMs += ms;
      writeHeartbeat(context, heartbeat({ instance_id: "game-new-a", bridge_port: 18143, last_heartbeat: new Date(nowMs).toISOString() }));
      writeHeartbeat(context, heartbeat({ instance_id: "game-new-b", bridge_port: 18144, last_heartbeat: new Date(nowMs).toISOString() }));
    };

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);
    if (result.status === "error") expect(result.error.code).toBe("duplicate_project_id");
  });

  it("fails closed on replacement catalog drift without a replacement POST", async () => {
    const context = makeContext([
      heartbeat({ instance_id: "game-old" }),
      heartbeat({ instance_id: "game-new", bridge_port: 18143, commands_hash: "catalog-b" }),
    ]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);
    if (result.status === "error") expect(result.error.code).toBe("catalog_stale");
  });

  it.each([
    ["malformed nonempty", { failure_kind: "response_parse", parse_error_kind: "invalid_json", response_body_bytes: 1, response_body_empty: false, response_body_truncated: false, response_content_type: "application/json" }],
    ["truncated", { failure_kind: "response_parse", parse_error_kind: "truncated_json", response_body_bytes: 12, response_body_empty: false, response_body_truncated: false, response_content_type: "application/json" }],
    ["wrong content type", { failure_kind: "invalid_content_type", parse_error_kind: "invalid_content_type", response_body_bytes: 0, response_body_empty: true, response_body_truncated: false, response_content_type: "text/plain" }],
    ["inconsistent content length", { failure_kind: "response_parse", parse_error_kind: "empty_body", response_content_length: 12, response_body_bytes: 0, response_body_empty: true, response_body_truncated: false, response_content_type: "application/json" }],
    ["shape mismatch", { failure_kind: "response_shape", parse_error_kind: "response_shape_mismatch", response_body_bytes: 2, response_body_empty: false, response_body_truncated: false, response_content_type: "application/json" }],
  ] as const)("does not reconnect %s protocol failures", async (_label, details) => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    const bridge = new ProtocolFailureBridge(context.bridge as BridgeClient, details);
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (result.status === "error") expect(result.error.details).not.toHaveProperty("empty_body_read_recovery");
  });

  it.each([
    ["protocol_schema", "private.schema"],
    ["protocol_reason", "schema_validation_failed"],
    ["expected_protocol_version", 1],
    ["received_protocol_version", 2],
    ["protocol_issues", [{ path: ["status"], code: "invalid_type" }]],
    ["response_error_code", "bridge_failed"],
    ["response_too_large", true],
    ["abort_source", "deadline"],
    ["http_error_kind", "server"],
    ["bridge_endpoint_available", false],
    ["protocol_failure", "invalid_bridge_response"],
    ["cause_class", "Error"],
    ["response_body_sha256", "not-the-empty-body-hash"],
  ] as const)("does not reconnect contradictory empty-body detail %s", async (key, value) => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    const bridge = new ProtocolFailureBridge(context.bridge as BridgeClient, { ...completeEmptyDetails(), [key]: value });
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
  });

  it("accepts null optional protocol diagnostics and the canonical empty-body hash", async () => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    context.bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old", false, {
      ...completeEmptyDetails(),
      protocol_schema: null as never,
      protocol_reason: null as never,
      expected_protocol_version: null as never,
      received_protocol_version: null,
      protocol_issues: null as never,
      response_body_sha256: "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
      cause_class: "SyntaxError",
    });

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("ok");
  });

  it("stops after an uncertain replacement timeout", async () => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    const bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old", true);
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old", "game-new"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_timeout");
      expect(result.error.details).toMatchObject({ empty_body_read_recovery: { replacement_request_count: 1, terminal_status: "timeout" } });
    }
  });

  it("does not retry an initially uncertain timeout", async () => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    const bridge = new TimeoutBridge(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (result.status === "error") expect(result.error.code).toBe("bridge_timeout");
  });

  it("does not retry a protocol version mismatch", async () => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    const bridge = new VersionMismatchBridge(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (result.status === "error") expect(result.error.code).toBe("protocol_version_mismatch");
  });

  it.each(["write", "execute", "destructive", "network"] as const)("never reconnects a %s command", async (risk) => {
    const descriptor = command("unsafe.command", risk);
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })], [descriptor]);
    const bridge = new ProtocolFailureBridge(context.bridge as BridgeClient, completeEmptyDetails());
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", { project: "game-old", command: descriptor.id, args_json: "{}", mode: "wait" }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
  });

  it("writes one audit pair with the read risk and actual invoke attempts", async () => {
    const context = makeContext([heartbeat({ instance_id: "game-old" }), heartbeat({ instance_id: "game-new", bridge_port: 18143 })]);
    context.bridge = new EmptyThenReplacementBridge(context.bridge as BridgeClient, "game-old");
    const audit = new InMemoryAuditSink();
    context.audit = audit;

    const result = await callWaitReady(context, "game-old");

    expect(result.status).toBe("ok");
    expect(audit.starts).toHaveLength(1);
    expect(audit.events).toHaveLength(1);
    expect(audit.events[0]).toMatchObject({
      command: "compile.wait_ready",
      command_risk: "read",
      bridge_invoke_attempts: 2,
      read_recovery_attempted: true,
      read_recovery_status: "ready",
    });
    expect(audit.events[0]).not.toHaveProperty("write_command_repeated");
  });
});

function makeContext(records: HeartbeatRecord[], commands: CommandDescriptor[] = [waitReadyCommand()]): DispatcherContext {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-070-"));
  tempDirs.push(instancesDir);
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => new Date(initialNow) }),
    bridge: new FakeUnityBridgeClient(commands),
    now: () => new Date(initialNow),
  };
  for (const record of records) writeHeartbeat(context, record);
  return context;
}

function writeHeartbeat(context: DispatcherContext, record: HeartbeatRecord): void {
  fs.writeFileSync(path.join(context.instancesDir as string, `${record.instance_id}.json`), JSON.stringify(record));
}

function callWaitReady(context: DispatcherContext, project: string, args: Record<string, unknown> = { timeout_ms: 10_000, poll_interval_ms: 50 }) {
  return dispatchTool("sgg_unity_call", { project, command: "compile.wait_ready", args_json: JSON.stringify(args), mode: "wait" }, context);
}

function command(id: string, risk: CommandDescriptor["risk"]): CommandDescriptor {
  return { id, title: id, description: id, package: "com.example.test", risk, idempotency: "safe", timeout_ms: 5_000, schema: { type: "object", additionalProperties: false } };
}

function waitReadyCommand(): CommandDescriptor {
  return {
    ...command("compile.wait_ready", "read"),
    timeout_ms: 300_000,
    schema: {
      type: "object",
      properties: {
        timeout_ms: { type: "integer", minimum: 1, maximum: 300_000 },
        poll_interval_ms: { type: "integer", minimum: 50, maximum: 5_000 },
      },
      additionalProperties: false,
    },
  };
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-old",
    project: "game",
    display_name: "Game",
    project_root: "C:/private/project",
    canonical_project_root: "c:/private/project",
    aliases: [],
    unity_version: "6000.4.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "old-secret",
    status: "ready",
    commands_hash: "catalog-a",
    last_heartbeat: new Date(initialNow).toISOString(),
    ...overrides,
  };
}

function completeEmptyDetails(): BridgeRequestError["details"] {
  return {
    endpoint: "/invoke",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    http_status: 200,
    failure_kind: "response_parse",
    parse_error_kind: "empty_body",
    response_content_type: "application/json",
    response_body_bytes: 0,
    response_body_empty: true,
    response_body_truncated: false,
  };
}

class EmptyThenReplacementBridge implements BridgeClient {
  readonly invokedInstanceIds: string[] = [];
  readonly invokedRequests: Array<{ instance_id: string; args: Record<string, unknown> }> = [];

  constructor(readonly inner: BridgeClient, readonly failedInstanceId: string, readonly timeoutReplacement = false, readonly failedDetails: BridgeRequestError["details"] = completeEmptyDetails()) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokedInstanceIds.push(record.instance_id);
    this.invokedRequests.push({ instance_id: record.instance_id, args: request.args });
    if (record.instance_id === this.failedInstanceId) throw new BridgeRequestError("bridge_protocol_error", { ...this.failedDetails, bridge_port: record.bridge_port });
    if (this.timeoutReplacement) throw new BridgeRequestError("bridge_timeout", { endpoint: "/invoke", bridge_host: record.bridge_host, bridge_port: record.bridge_port, failure_kind: "timeout", abort_source: "deadline" });
    return Promise.resolve({ status: "ok", data: { instance_id: record.instance_id, command: request.command, deadline_unix_ms: request.deadline_unix_ms, transport_deadline_unix_ms: transport?.deadlineUnixMs } });
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class ProtocolFailureBridge implements BridgeClient {
  invokeCount = 0;

  constructor(readonly inner: BridgeClient, readonly details: Partial<BridgeRequestError["details"]>) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  invoke(record: HeartbeatRecord): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    throw new BridgeRequestError("bridge_protocol_error", { endpoint: "/invoke", bridge_host: record.bridge_host, bridge_port: record.bridge_port, http_status: 200, ...this.details });
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class TimeoutBridge implements BridgeClient {
  invokeCount = 0;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  invoke(record: HeartbeatRecord): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    throw new BridgeRequestError("bridge_timeout", { endpoint: "/invoke", bridge_host: record.bridge_host, bridge_port: record.bridge_port, failure_kind: "timeout", abort_source: "deadline" });
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class VersionMismatchBridge extends TimeoutBridge {
  override invoke(record: HeartbeatRecord): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    throw new BridgeRequestError("protocol_version_mismatch", {
      endpoint: "/invoke",
      bridge_host: record.bridge_host,
      bridge_port: record.bridge_port,
      failure_kind: "response_shape",
      protocol_schema: "sgg.unity_mcp.bridge.invoke.response.v1",
      protocol_reason: "unsupported_schema_version",
      expected_protocol_version: 1,
      received_protocol_version: 2,
    });
  }
}

class CountingProjectQueueManager extends ProjectQueueManager {
  admissions = 0;

  override run<T>(project: string, task: () => Promise<T>): Promise<T> {
    this.admissions += 1;
    return super.run(project, task);
  }
}

class InjectedAmbiguityRegistry extends ProjectRegistry {
  resolveCalls = 0;
  replacementCalls = 0;

  constructor(instancesDir: string) {
    super({ instancesDir, staleAfterMs: 90_000, now: () => new Date(initialNow) });
  }

  override resolveProject(project: string, options: ProjectResolutionOptions = {}): ProjectResolution {
    this.resolveCalls += 1;
    if (this.resolveCalls > 1) throw new Error("replacement recovery must not perform a second exact-id scan");
    return super.resolveProject(project, options);
  }

  override listProjectRecords(): never {
    throw new Error("replacement recovery must not build a separate public snapshot");
  }

  override resolveReplacementProject(): ReplacementProjectResolution {
    this.replacementCalls += 1;
    return { status: "error", code: "duplicate_project_id" };
  }
}
