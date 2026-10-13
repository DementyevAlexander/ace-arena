import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { ProjectQueueManager } from "../src/routing/project-queue.js";

const tempDirs: string[] = [];
const initialNow = Date.parse("2026-07-14T06:41:54.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("MCP-PR-079 pre-dispatch replacement routing", () => {
  it("preserves immediate unity_reloading by default without a bridge request", async () => {
    const context = makeContext([heartbeat()]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", {
      project: "game-old",
      command: "game.view.screenshot",
      args_json: "{}",
      mode: "wait",
      request_id: "screenshot-reload-not-dispatched",
    }, context);

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("unity_reloading");
      expect(result.request_id).toBe("screenshot-reload-not-dispatched");
      expect(result.error.message).toContain("was not scheduled");
      expect(result.error.details).toMatchObject({
        screenshot_recovery_contract: "sgg.unity_mcp.screenshot_capture_recovery.v1",
        capture_state: "not_scheduled",
        capture_state_values: ["not_scheduled", "scheduled", "running", "uncertain"],
        bridge_request_issued: false,
        automatic_retry_attempted: false,
        capture_replayed: false,
      });
    }
    expect(bridge.invokedInstanceIds).toEqual([]);
  });

  it("routes one screenshot request to an immediate canonical replacement", async () => {
    const context = makeContext([
      heartbeat(),
      heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "playing", token: "replacement-secret" }),
    ]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await callScreenshot(context, "game-old", 1_000);

    expect(result.status).toBe("ok");
    expect(bridge.invokedInstanceIds).toEqual(["game-new"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        instance_id: "game-new",
        pre_dispatch_replacement: {
          attempted: true,
          wait_budget_ms: 1_000,
          deadline_scope: "pre_dispatch_only",
          command_timeout_included: false,
          attempt_count: 1,
          bridge_request_count: 1,
          selector_strategy: "failed_heartbeat_canonical_project_identity",
          failed_instance: { instance_id: "game-old", bridge_port: 18142 },
          replacement_instance: { instance_id: "game-new", bridge_port: 18143 },
          terminal_status: "routed",
        },
      });
      expect(JSON.stringify(result)).not.toContain("private/project");
      expect(JSON.stringify(result)).not.toContain("replacement-secret");
    }
  });

  it("holds one queue admission while a delayed replacement appears", async () => {
    let nowMs = initialNow;
    const context = makeContext([heartbeat()]);
    const queues = new CountingProjectQueueManager();
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
    context.now = () => new Date(nowMs);
    context.queues = queues;
    context.bridge = bridge;
    context.sleepMs = async (ms) => {
      nowMs += ms;
      writeHeartbeat(context, heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "playing", last_heartbeat: new Date(nowMs).toISOString() }));
    };

    const result = await callScreenshot(context, "game-old", 1_000);

    expect(result.status).toBe("ok");
    expect(queues.admissions).toBe(1);
    expect(bridge.invokedInstanceIds).toEqual(["game-new"]);
    expect(bridge.deadlines).toEqual([initialNow + 250 + 30_000]);
  });

  it("fails closed on replacement ambiguity and catalog drift before POST", async () => {
    const cases = [
      {
        expected: "duplicate_project_id",
        records: [
          heartbeat(),
          heartbeat({ instance_id: "game-new-a", bridge_port: 18143, status: "playing" }),
          heartbeat({ instance_id: "game-new-b", bridge_port: 18144, status: "playing" }),
        ],
      },
      {
        expected: "catalog_stale",
        records: [heartbeat(), heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "playing", commands_hash: "catalog-b" })],
      },
    ];

    for (const testCase of cases) {
      const context = makeContext(testCase.records);
      const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
      context.bridge = bridge;

      const result = await callScreenshot(context, "game-old", 1_000);

      expect(result.status).toBe("error");
      if (result.status === "error") expect(result.error.code).toBe(testCase.expected);
      expect(bridge.invokedInstanceIds).toEqual([]);
    }
  });

  it("uses the re-described catalog instead of stale replacement heartbeat metadata", async () => {
    const context = makeContext([
      heartbeat(),
      heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "playing", commands_hash: "stale-heartbeat-catalog" }),
    ]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient, false, "catalog-a");
    context.bridge = bridge;

    const result = await callScreenshot(context, "game-old", 1_000);

    expect(result.status).toBe("ok");
    expect(bridge.invokedInstanceIds).toEqual(["game-new"]);
  });

  it("returns replacement_timeout under the original deadline without POST", async () => {
    let nowMs = initialNow;
    const context = makeContext([heartbeat()]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
    context.now = () => new Date(nowMs);
    context.bridge = bridge;
    context.sleepMs = async (ms) => { nowMs += ms; };

    const result = await callScreenshot(context, "game-old", 500);

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual([]);
    if (result.status === "error") {
      expect(result.error.code).toBe("replacement_timeout");
      expect(result.error.details).toMatchObject({
        pre_dispatch_replacement: { attempt_count: 2, bridge_request_count: 0, terminal_status: "timeout" },
      });
    }
  });

  it("does not POST when replacement descriptor resolution crosses the pre-dispatch deadline", async () => {
    let nowMs = initialNow;
    const context = makeContext([heartbeat(), heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "playing" })]);
    context.now = () => new Date(nowMs);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient, false, undefined, false, false, () => { nowMs += 500; });
    context.bridge = bridge;

    const result = await callScreenshot(context, "game-old", 500);

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual([]);
    if (result.status === "error") {
      expect(result.error.code).toBe("replacement_timeout");
      expect(result.error.details).toMatchObject({
        pre_dispatch_replacement: { bridge_request_count: 0, terminal_status: "timeout" },
      });
    }
  });

  it("bounds queue admission and prevents a late POST after the caller times out", async () => {
    let releaseBlocker!: () => void;
    const context = makeContext([heartbeat(), heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "playing" })]);
    const queues = new CountingProjectQueueManager();
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
    context.queues = queues;
    context.bridge = bridge;
    const blocker = queues.run("game", () => new Promise<void>((resolve) => { releaseBlocker = resolve; }));
    await Promise.resolve();

    const result = await callScreenshot(context, "game-old", 20);

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("replacement_timeout");
    expect(bridge.invokedInstanceIds).toEqual([]);
    releaseBlocker();
    await blocker;
    await queues.run("game", async () => undefined);
    expect(bridge.invokedInstanceIds).toEqual([]);
  });

  it("never replays after a replacement screenshot transport timeout", async () => {
    const context = makeContext([heartbeat(), heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "playing" })]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient, true);
    context.bridge = bridge;

    const result = await callScreenshot(context, "game-old", 1_000);

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-new"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_timeout");
      expect(result.error.details).toMatchObject({
        pre_dispatch_replacement: { bridge_request_count: 1, terminal_status: "error" },
        screenshot_recovery_contract: "sgg.unity_mcp.screenshot_capture_recovery.v1",
        capture_state: "uncertain",
        bridge_request_issued: true,
        automatic_retry_attempted: false,
        capture_replayed: false,
        repeat_blocked_until_delivery_state_checked: true,
      });
      expect(result.error.details).not.toHaveProperty("repeat_request_template");
    }
  });

  it("preserves not_scheduled when Unity proves reload won before command dispatch", async () => {
    const context = makeContext([heartbeat({ status: "playing" })]);
    const bridge = new ReloadRejectedScreenshotBridge(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", {
      project: "game-old",
      command: "game.view.screenshot",
      args_json: "{}",
      mode: "wait",
      request_id: "screenshot-shutdown-response",
    }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (result.status === "error") {
      expect(result.error.code).toBe("unity_reloading");
      expect(result.request_id).toBe("screenshot-shutdown-response");
      expect(result.error.details).toMatchObject({
        command_dispatched: false,
        capture_state: "not_scheduled",
        bridge_request_issued: true,
        automatic_retry_attempted: false,
      });
      expect(result.error.details).not.toHaveProperty("repeat_blocked_until_delivery_state_checked");
    }
  });

  it("reports not_scheduled when an expired transport deadline prevents the HTTP request", async () => {
    const context = makeContext([heartbeat({ status: "playing" })]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient, false, undefined, false, true);
    context.bridge = bridge;

    const result = await callScreenshot(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_timeout");
      expect(result.error.details).toMatchObject({
        bridge_request_issued: false,
        capture_state: "not_scheduled",
        automatic_retry_attempted: false,
        safe_to_repeat_automatically: false,
      });
      expect(result.error.details).not.toHaveProperty("repeat_blocked_until_delivery_state_checked");
    }
  });

  it("does not expose replay diagnostics after uncertain screenshot protocol loss", async () => {
    const context = makeContext([heartbeat({ status: "playing" })]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient, false, undefined, true);
    context.bridge = bridge;

    const result = await callScreenshot(context, "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual(["game-old"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.next_valid_actions).toContain("do_not_repeat_screenshot_while_delivery_is_uncertain");
      expect(result.error.details).toMatchObject({ capture_state: "uncertain", repeat_blocked_until_delivery_state_checked: true });
      expect(result.error.details?.recovery_call_sequence).not.toEqual(expect.arrayContaining([
        expect.objectContaining({ command: "diagnostics.recovery.suggest" }),
      ]));
      expect(result.error.details).not.toHaveProperty("repeat_request_template");
    }
  });

  it("returns descriptor-backed backend correction calls without reflecting the rejected value", async () => {
    const context = makeContext([heartbeat({ status: "playing" })]);
    const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", {
      project: "game-old",
      command: "game.view.screenshot",
      args_json: JSON.stringify({ backend: "private-invalid-backend" }),
      mode: "wait",
    }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokedInstanceIds).toEqual([]);
    if (result.status === "error") {
      expect(result.error.code).toBe("schema_validation_failed");
      expect(result.error.details).toMatchObject({
        invalid_argument: {
          path: ["backend"],
          issue_code: "enum_value_not_allowed",
          rejected_value_type: "string",
          rejected_value_reflected: false,
          accepted_values: ["camera_render", "end_of_frame", "screen_capture"],
          descriptor_source: "command:game.view.screenshot",
          corrected_call_guidance: {
            requires_explicit_operator_choice: true,
            automatic_retry_attempted: false,
            examples: expect.arrayContaining([
              expect.objectContaining({
                tool: "sgg_unity_call",
                arguments: {
                  project: "game-old",
                  command: "game.view.screenshot",
                  args_json: JSON.stringify({ backend: "end_of_frame" }),
                  mode: "wait",
                },
              }),
            ]),
          },
        },
      });
      expect(JSON.stringify(result)).not.toContain("private-invalid-backend");
    }
  });

  it("does not publish backend values or correction calls from an untrusted duplicate descriptor", async () => {
    const untrusted = {
      ...screenshotCommand(),
      source: "attribute" as const,
      package_id: "com.example.untrusted",
      trust: "untrusted_package" as const,
      schema: {
        type: "object",
        properties: {
          backend: { type: "string", enum: ["camera_render", "end_of_frame", "screen_capture", "Bearer injected-secret"] },
        },
        additionalProperties: false,
      },
    };
    const context = makeContext([heartbeat({ status: "playing" })], [untrusted]);

    const result = await dispatchTool("sgg_unity_call", {
      project: "game-old",
      command: "game.view.screenshot",
      args_json: JSON.stringify({ backend: "invalid" }),
      mode: "wait",
    }, context);

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("schema_validation_failed");
      expect(result.error.details).not.toHaveProperty("invalid_argument");
      expect(JSON.stringify(result)).not.toContain("injected-secret");
    }
  });

  it("recovers only after a separate explicit Scene view screenshot call", async () => {
    const context = makeContext([heartbeat({ status: "playing" })], [command("scene.view.screenshot", "read")]);
    const bridge = new RecoveringSceneViewBridge(context.bridge as BridgeClient);
    context.bridge = bridge;

    const first = await dispatchTool("sgg_unity_call", { project: "game-old", command: "scene.view.screenshot", args_json: "{}", mode: "wait" }, context);

    expect(first.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (first.status === "error") {
      expect(first.error.details).toMatchObject({
        capture_state: "not_scheduled",
        automatic_window_action_attempted: false,
        operator_action_required: true,
      });
    }

    const second = await dispatchTool("sgg_unity_call", { project: "game-old", command: "scene.view.screenshot", args_json: "{}", mode: "wait" }, context);

    expect(second.status).toBe("ok");
    expect(bridge.invokeCount).toBe(2);
  });

  it("rejects the opt-in for commands and modes where pre-dispatch replay safety is not defined", async () => {
    const context = makeContext([heartbeat()], [screenshotCommand(), command("scene.view.screenshot", "read")]);

    const wrongCommand = await dispatchTool("sgg_unity_call", {
      project: "game-old",
      command: "scene.view.screenshot",
      args_json: "{}",
      mode: "wait",
      wait_for_replacement_ms: 1_000,
    }, context);
    const wrongMode = await dispatchTool("sgg_unity_call", {
      project: "game-old",
      command: "game.view.screenshot",
      args_json: "{}",
      mode: "job",
      wait_for_replacement_ms: 1_000,
    }, context);

    expect(wrongCommand.status).toBe("error");
    expect(wrongMode.status).toBe("error");
    if (wrongCommand.status === "error") expect(wrongCommand.error.code).toBe("invalid_field_value");
    if (wrongMode.status === "error") expect(wrongMode.error.code).toBe("invalid_field_value");
  });

  it("rejects missing or narrower descriptor contracts before screenshot POST", async () => {
    const missing = screenshotCommand();
    delete missing.wrapper_wait;
    const narrow = screenshotCommand();
    narrow.wrapper_wait = {
      mode: "wait",
      wait_for_replacement_ms: { min_ms: 1, max_ms: 500, deadline_scope: "pre_dispatch_only" },
    };

    for (const [descriptor, issueCode] of [[missing, "descriptor_contract_missing"], [narrow, "outside_descriptor_range"]] as const) {
      const context = makeContext([heartbeat({ status: "playing" })], [descriptor]);
      const bridge = new ScreenshotBridge(context.bridge as BridgeClient);
      context.bridge = bridge;

      const result = await callScreenshot(context, "game-old", 1_000);

      expect(result.status).toBe("error");
      expect(bridge.invokedInstanceIds).toEqual([]);
      if (result.status === "error") {
        expect(result.error.details?.invalid_arguments).toMatchObject({
          automatic_retry_attempted: false,
          issues: [{ path: ["wait_for_replacement_ms"], issue_code: issueCode }],
        });
      }
    }
  });
});

function makeContext(records: HeartbeatRecord[], commands: CommandDescriptor[] = [screenshotCommand()]): DispatcherContext {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-079-"));
  tempDirs.push(instancesDir);
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => new Date(initialNow) }),
    bridge: new FakeUnityBridgeClient(commands),
  };
  for (const record of records) writeHeartbeat(context, record);
  return context;
}

function writeHeartbeat(context: DispatcherContext, record: HeartbeatRecord): void {
  fs.writeFileSync(path.join(context.instancesDir as string, `${record.instance_id}.json`), JSON.stringify(record));
}

function callScreenshot(context: DispatcherContext, project: string, waitForReplacementMs?: number) {
  return dispatchTool("sgg_unity_call", {
    project,
    command: "game.view.screenshot",
    args_json: "{}",
    mode: "wait",
    ...(waitForReplacementMs === undefined ? {} : { wait_for_replacement_ms: waitForReplacementMs }),
  }, context);
}

function command(id: string, risk: CommandDescriptor["risk"]): CommandDescriptor {
  return { id, title: id, description: id, package: "com.example.test", risk, idempotency: "unsafe", timeout_ms: 30_000, schema: { type: "object", additionalProperties: false } };
}

function screenshotCommand(): CommandDescriptor {
  return {
    ...command("game.view.screenshot", "read"),
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
    wrapper_wait: {
      mode: "wait",
      wait_for_replacement_ms: { min_ms: 1, max_ms: 3_600_000, deadline_scope: "pre_dispatch_only" },
    },
    schema: {
      type: "object",
      properties: {
        backend: { type: "string", enum: ["camera_render", "end_of_frame", "screen_capture"] },
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
    unity_version: "6000.5.3f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "old-secret",
    status: "reloading",
    commands_hash: "catalog-a",
    last_heartbeat: new Date(initialNow).toISOString(),
    ...overrides,
  };
}

class ScreenshotBridge implements BridgeClient {
  readonly invokedInstanceIds: string[] = [];
  readonly deadlines: number[] = [];

  constructor(readonly inner: BridgeClient, readonly timeout = false, readonly describedCommandsHash?: string, readonly protocolFailure = false, readonly preflightFailure = false, readonly afterCommandDescribe?: () => void) {}

  async describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    const result = await this.inner.describe(record, selector, transport);
    if (this.describedCommandsHash !== undefined && selector.startsWith("command:") && typeof result === "object" && result !== null) {
      return { ...result, commands_hash: this.describedCommandsHash };
    }
    if (selector.startsWith("command:")) this.afterCommandDescribe?.();
    return result;
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokedInstanceIds.push(record.instance_id);
    if (transport?.deadlineUnixMs !== undefined) this.deadlines.push(transport.deadlineUnixMs);
    if (this.preflightFailure) {
      throw new BridgeRequestError("bridge_timeout", {
        endpoint: "/invoke",
        bridge_host: record.bridge_host,
        bridge_port: record.bridge_port,
        failure_kind: "timeout",
        abort_source: "deadline",
        bridge_request_issued: false,
      });
    }
    if (this.timeout) {
      throw new BridgeRequestError("bridge_timeout", {
        endpoint: "/invoke",
        bridge_host: record.bridge_host,
        bridge_port: record.bridge_port,
        failure_kind: "timeout",
        abort_source: "deadline",
      });
    }
    if (this.protocolFailure) {
      throw new BridgeRequestError("bridge_protocol_error", {
        endpoint: "/invoke",
        bridge_host: record.bridge_host,
        bridge_port: record.bridge_port,
        failure_kind: "response_parse",
        parse_error_kind: "empty_body",
        bridge_request_issued: true,
      });
    }
    return Promise.resolve({ status: "ok", data: { instance_id: record.instance_id, command: request.command } });
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class ReloadRejectedScreenshotBridge implements BridgeClient {
  invokeCount = 0;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  invoke(): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    return Promise.resolve({
      status: "error",
      error: {
        code: "unity_reloading",
        message: "Unity is reloading before command dispatch",
        details: { command_dispatched: false, lifecycle_status: "reloading" },
      },
    });
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class RecoveringSceneViewBridge implements BridgeClient {
  invokeCount = 0;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    if (this.invokeCount === 1) {
      return Promise.resolve({
        status: "error",
        error: {
          code: "unity_busy",
          message: "No Scene view camera is available",
          details: {
            capture_state: "not_scheduled",
            automatic_window_action_attempted: false,
            operator_action_required: true,
            recovery_commands: [{ command: "camera.list", args_json: "{}" }],
          },
        },
      });
    }
    return Promise.resolve({ status: "ok", data: { command: request.command, artifact_path: "Library/SGGUnityMcp/screenshots/scene-view.png" } });
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class CountingProjectQueueManager extends ProjectQueueManager {
  admissions = 0;

  override run<T>(project: string, task: () => Promise<T>): Promise<T> {
    this.admissions += 1;
    return super.run(project, task);
  }
}
