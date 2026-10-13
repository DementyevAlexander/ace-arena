import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { argsHash } from "../src/approvals/approval-store.js";
import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { EDITOR_MENU_OPERATION_ARTIFACT_ROOT, EDITOR_MENU_OPERATION_CONTRACT, runEditorMenuOperationCommand } from "../src/mcp/editor-menu-operation-commands.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { ProjectQueueManager } from "../src/routing/project-queue.js";

const tempDirs: string[] = [];

afterEach(() => {
  for (const directory of tempDirs.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

describe("MCP-PR-117 durable editor menu operations", () => {
  it("reserves one operation identity and binds the original caller args", async () => {
    const fixture = makeFixture();
    const bridge = new MenuOperationBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;
    expect(fixture.context.registry?.listProjects()).toHaveLength(1);

    const result = await call(fixture.context, { menu_path: "SGG/Sky/Build" });

    expect(bridge.invokeCount, JSON.stringify(result)).toBe(1);
    expect(bridge.operationId).toMatch(/^menu-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/);
    expect(bridge.callerArgsHash).toBe(argsHash({ menu_path: "SGG/Sky/Build" }));
    expect(result.status).toBe("ok");
    if (result.status === "ok") expect(result.data).toMatchObject({ operation_id: bridge.operationId, status: "queued", operation_created: true });
  });

  it("returns exact no-replay recovery after uncertain Bridge delivery", async () => {
    const fixture = makeFixture();
    const bridge = new MenuOperationBridge(fixture.context.bridge as BridgeClient);
    bridge.thrownError = new BridgeRequestError("bridge_timeout", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "timeout",
      abort_source: "deadline",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, { menu_path: "SGG/Sky/Build" });

    expect(bridge.invokeCount, JSON.stringify(result)).toBe(1);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        editor_menu_operation_recovery_contract: "sgg.unity_mcp.editor_menu_operation_timeout.v1",
        operation_id: bridge.operationId,
        operation_id_source: "gateway_reserved",
        delivery_state: "uncertain",
        bridge_request_issued: true,
        command_replayed: false,
        auto_retry_attempted: false,
        expected_artifact_path: `${EDITOR_MENU_OPERATION_ARTIFACT_ROOT}/${bridge.operationId}.json`,
        status_lookup: { project: fixture.record.instance_id, command: "editor.menu.operation.status", args_json: JSON.stringify({ operation_id: bridge.operationId }) },
      });
      expect(result.next_valid_actions).toContain("do_not_repeat_editor.menu.operation.start_until_delivery_state_is_resolved");
    }
  });

  it("preserves definite Unity refusal without claiming uncertain delivery", async () => {
    const fixture = makeFixture();
    const bridge = new MenuOperationBridge(fixture.context.bridge as BridgeClient);
    bridge.response = { status: "error", error: { code: "menu_operation_not_eligible", message: "not eligible", details: { operation_created: false, menu_execute_invoked: false } } };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, { menu_path: "Window/Unmarked" });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ operation_created: false, menu_execute_invoked: false });
      expect(result.error.details).not.toHaveProperty("editor_menu_operation_recovery_contract");
    }
  });

  it("rejects caller correlation overrides before Bridge dispatch", async () => {
    const fixture = makeFixture();
    const bridge = new MenuOperationBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, { menu_path: "SGG/Sky/Build", _editor_menu_operation_id: "menu-20260806-123456-deadbeef" });

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ caller_correlation_override_rejected: true, invalid_field_paths: ["args._editor_menu_operation_id"] });
    expect(bridge.invokeCount).toBe(0);
  });

  it("expires in the project queue without a late menu dispatch", async () => {
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const queues = new ProjectQueueManager();
    const blocker = queues.run("game", () => gate);
    const fixture = makeFixture(30);
    fixture.context.queues = queues;
    const bridge = new MenuOperationBridge(fixture.context.bridge as BridgeClient);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, { menu_path: "SGG/Sky/Build" });

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.details).toMatchObject({ delivery_state: "not_dispatched", bridge_request_issued: false });
    expect(bridge.invokeCount).toBe(0);
    release();
    await blocker;
    await new Promise<void>((resolve) => setTimeout(resolve, 20));
    expect(bridge.invokeCount).toBe(0);
  });

  it("reads a validated receipt by exact stale generation when the project has older heartbeat history", async () => {
    const fixture = makeFixture();
    const operationId = "menu-20260806-123456-deadbeef";
    writeReceipt(fixture.projectRoot, operationId, "running");
    const staleRecord = { ...fixture.record, last_heartbeat: "2026-01-01T00:00:00.000Z" };
    fs.writeFileSync(path.join(fixture.instancesDir, `${staleRecord.instance_id}.json`), JSON.stringify(staleRecord));
    fs.writeFileSync(path.join(fixture.instancesDir, "game-previous.json"), JSON.stringify({
      ...staleRecord,
      instance_id: "game-previous",
      unity_pid: 12344,
      bridge_port: 18141,
      last_heartbeat: "2025-12-31T23:59:00.000Z",
    }));
    fixture.context.registry = new ProjectRegistry({ instancesDir: fixture.instancesDir, staleAfterMs: 1 });

    const result = await dispatchTool("sgg_unity_call", {
      project: staleRecord.instance_id,
      command: "editor.menu.operation.status",
      args_json: JSON.stringify({ operation_id: operationId }),
      mode: "wait",
    }, fixture.context);

    expect(result.status).toBe("ok");
    if (result.status === "ok") expect(result.data).toMatchObject({ operation_id: operationId, status: "running", terminal: false, dispatch_count: 1 });
  });

  it("rejects pathname replacement during a read and accepts the replacement on a new read", () => {
    const fixture = makeFixture();
    const operationId = "menu-20260806-123456-deadbeef";
    const receiptPath = writeReceipt(fixture.projectRoot, operationId, "running");
    const replacementPath = `${receiptPath}.replacement`;
    const replacement = JSON.parse(fs.readFileSync(receiptPath, "utf8")) as Record<string, unknown>;
    fs.writeFileSync(replacementPath, JSON.stringify({
      ...replacement,
      status: "succeeded",
      terminal: true,
      success: true,
      terminal_at_utc: "2026-08-06T12:35:58.000Z",
      execute_menu_item_returned: true,
    }));

    const result = runEditorMenuOperationCommand("editor.menu.operation.status", { operation_id: operationId }, fixture.record, {
      afterRead: () => fs.renameSync(replacementPath, receiptPath),
    });

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("menu_operation_receipt_unavailable");
    expect(runEditorMenuOperationCommand("editor.menu.operation.status", { operation_id: operationId }, fixture.record)).toMatchObject({
      status: "ok",
      data: { operation_id: operationId, status: "succeeded", terminal: true },
    });
  });

  it("rejects in-place mutation of an opened receipt", () => {
    const fixture = makeFixture();
    const operationId = "menu-20260806-123456-deadbeef";
    const receiptPath = writeReceipt(fixture.projectRoot, operationId, "running");
    const original = fs.readFileSync(receiptPath, "utf8");

    const result = runEditorMenuOperationCommand("editor.menu.operation.status", { operation_id: operationId }, fixture.record, {
      afterOpen: () => fs.writeFileSync(receiptPath, original.replace("SGG/Sky/Build", "SGG/Sky/Other")),
    });

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("menu_operation_receipt_unavailable");
  });

  it("fails closed for a receipt identity mismatch", () => {
    const fixture = makeFixture();
    const operationId = "menu-20260806-123456-deadbeef";
    const receipt = writeReceipt(fixture.projectRoot, operationId, "succeeded");
    fs.writeFileSync(receipt, JSON.stringify({ ...JSON.parse(fs.readFileSync(receipt, "utf8")), operation_id: "menu-20260806-123456-bad0cafe" }));

    const result = runEditorMenuOperationCommand("editor.menu.operation.status", { operation_id: operationId }, fixture.record);

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("menu_operation_receipt_invalid");
  });

  it("rejects impossible terminal lifecycle combinations", () => {
    const fixture = makeFixture();
    const operationId = "menu-20260806-123456-deadbeef";
    const receiptPath = writeReceipt(fixture.projectRoot, operationId, "succeeded");
    const receipt = JSON.parse(fs.readFileSync(receiptPath, "utf8")) as Record<string, unknown>;
    fs.writeFileSync(receiptPath, JSON.stringify({ ...receipt, dispatch_count: 0, menu_execute_invoked: false }));

    const result = runEditorMenuOperationCommand("editor.menu.operation.status", { operation_id: operationId }, fixture.record);

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("menu_operation_receipt_invalid");

    fs.writeFileSync(receiptPath, JSON.stringify({
      ...receipt,
      status: "failed",
      terminal: true,
      success: false,
      execute_menu_item_returned: true,
    }));
    const failedWithTrueReturn = runEditorMenuOperationCommand("editor.menu.operation.status", { operation_id: operationId }, fixture.record);
    expect(failedWithTrueReturn.status).toBe("error");

    fs.writeFileSync(receiptPath, JSON.stringify({
      ...receipt,
      status: "failed",
      terminal: true,
      success: false,
      execute_menu_item_returned: false,
      running_at_utc: null,
    }));
    const dispatchedWithoutRunningTimestamp = runEditorMenuOperationCommand("editor.menu.operation.status", { operation_id: operationId }, fixture.record);
    expect(dispatchedWithoutRunningTimestamp.status).toBe("error");
  });
});

function makeFixture(timeoutMs = 10_000) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-117-"));
  tempDirs.push(root);
  const instancesDir = path.join(root, "instances");
  const projectRoot = path.join(root, "project");
  fs.mkdirSync(instancesDir, { recursive: true, mode: 0o700 });
  fs.mkdirSync(projectRoot, { recursive: true });
  const record = heartbeat(projectRoot);
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000 }),
    bridge: new FakeUnityBridgeClient([menuOperationStartCommand(timeoutMs)]),
    features: { editor_utilities: true },
  };
  return { context, record, instancesDir, projectRoot };
}

function call(context: DispatcherContext, args: Record<string, unknown>) {
  return dispatchTool("sgg_unity_call", {
    project: "game",
    command: "editor.menu.operation.start",
    args_json: JSON.stringify(args),
    mode: "wait",
    request_id: "pr-117",
  }, context);
}

function menuOperationStartCommand(timeoutMs: number): CommandDescriptor {
  return {
    id: "editor.menu.operation.start",
    title: "Start durable editor menu operation",
    description: "Start durable editor menu operation",
    package: "com.sungeargames.unity-mcp",
    risk: "execute",
    idempotency: "unsafe",
    timeout_ms: timeoutMs,
    schema: {
      type: "object",
      properties: { menu_path: { type: "string", minLength: 1, maxLength: 128 }, dry_run: { type: "boolean" } },
      required: ["menu_path"],
      additionalProperties: false,
    },
    tags: ["editor", "menu", "editor_menu_operation_correlated"],
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  };
}

function heartbeat(projectRoot: string): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-current",
    project: "game",
    display_name: "Game",
    project_root: projectRoot,
    canonical_project_root: projectRoot,
    aliases: [],
    unity_version: "6000.5.5f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-a",
    last_heartbeat: new Date().toISOString(),
  };
}

function writeReceipt(projectRoot: string, operationId: string, status: "running" | "succeeded"): string {
  const artifactPath = `${EDITOR_MENU_OPERATION_ARTIFACT_ROOT}/${operationId}.json`;
  const fullPath = path.join(projectRoot, artifactPath);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  const terminal = status === "succeeded";
  const hash = `sha256:${"a".repeat(64)}`;
  fs.writeFileSync(fullPath, JSON.stringify({
    contract: EDITOR_MENU_OPERATION_CONTRACT,
    artifact_schema_version: 1,
    command: "editor.menu.operation.start",
    operation_id: operationId,
    status,
    terminal,
    success: terminal,
    created_at_utc: "2026-08-06T12:34:56.000Z",
    queued_at_utc: "2026-08-06T12:34:56.000Z",
    running_at_utc: "2026-08-06T12:34:57.000Z",
    terminal_at_utc: terminal ? "2026-08-06T12:35:58.000Z" : null,
    queue_deadline_utc: "2026-08-06T12:35:06.000Z",
    menu_path: "SGG/Sky/Build",
    args_hash: hash,
    caller_args_hash: hash,
    dispatch_count: 1,
    execute_menu_item_returned: terminal ? true : null,
    artifact_path: artifactPath,
    artifact_persisted: true,
    operation_created: true,
    menu_execute_invoked: true,
    failure_code: "",
    message: "",
    eligibility: {
      marker_type: "SGG.Sky.Editor.SGGUnityMcpLongRunningMenuAttribute",
      marker_assembly: "SGG.Sky.Editor",
      method_identity: { assembly: "SGG.Sky.Editor", declaring_type: "SGG.Sky.Editor.Builder", name: "Build", signature: "System.Void SGG.Sky.Editor.Builder.Build()" },
    },
  }));
  return fullPath;
}

class MenuOperationBridge implements BridgeClient {
  invokeCount = 0;
  operationId = "";
  callerArgsHash = "";
  response: BridgeInvokeResponse | undefined;
  thrownError: BridgeRequestError | undefined;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    this.operationId = typeof request.args._editor_menu_operation_id === "string" ? request.args._editor_menu_operation_id : "";
    this.callerArgsHash = typeof request.args._editor_menu_caller_args_hash === "string" ? request.args._editor_menu_caller_args_hash : "";
    if (this.thrownError) throw this.thrownError;
    if (this.response) return this.response;
    if (request.command === "editor.menu.operation.start") {
      return {
        status: "ok",
        data: {
          operation_id: this.operationId,
          artifact_path: `${EDITOR_MENU_OPERATION_ARTIFACT_ROOT}/${this.operationId}.json`,
          status: "queued",
          terminal: false,
          success: false,
          dispatch_count: 0,
          execute_menu_item_returned: null,
          artifact_persisted: true,
          operation_created: true,
          menu_execute_invoked: false,
        },
      };
    }
    return this.inner.invoke(record, request, transport);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}
