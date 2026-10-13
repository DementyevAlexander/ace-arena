import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalStore, argsHash } from "../src/approvals/approval-store.js";
import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type CommandDescriptor, compileStatusCommand } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import { TOOL_NAMES } from "../src/mcp/contracts.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-19T10:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("agent harness evals", () => {
  it("missing-project: Unity command calls without explicit project are rejected", async () => {
    const context = makeContext([heartbeat()]);

    const result = await dispatchTool("sgg_unity_call", { command: "compile.status", args_json: "{}" }, context);

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("schema_validation_failed");
      expect(result.next_valid_actions).toEqual(["sgg_unity_projects"]);
      expect(result.error.details).toMatchObject({
        schema_lookup: {
          tool: "sgg_unity_projects",
          arguments: {},
          metadata_lookup: { action_kind: "mcp_tool", tool: "sgg_unity_projects", arguments: {} },
        },
      });
    }
  });

  it("permission-bypass: denied command direct call is blocked before bridge invoke", async () => {
    const context = makeContext([heartbeat()], [command("shell.run", "execute")], policy());
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "shell.run", args_json: "{}" }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("permission_denied");
      expect(result.next_valid_actions).toEqual(["do_not_bypass_policy", "choose_allowed_command", "ask_user_to_update_policy"]);
    }
  });

  it("invalid-args-json: command schema validation fails before bridge invoke", async () => {
    const context = makeContext([heartbeat()], [assetImportCommand()], policy());
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "asset.import", args_json: JSON.stringify({ asset_path: 123 }) }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("schema_validation_failed");
      expect(result.next_valid_actions).toEqual(["sgg_unity_describe"]);
      const lookup = result.error.details?.schema_lookup as {
        bridge_command_id: string;
        metadata_lookup: { action_kind: string; tool: "sgg_unity_describe"; arguments: { project: string; selector: string } };
      };
      expect(lookup).toMatchObject({
        bridge_command_id: "asset.import",
        tool: "sgg_unity_describe",
        project: "game",
        selector: "command:asset.import",
        arguments: { project: "game", selector: "command:asset.import" },
        selector_source: "original_selector",
        metadata_lookup: {
          action_kind: "mcp_tool",
          tool: "sgg_unity_describe",
          arguments: { project: "game", selector: "command:asset.import" },
          selector_source: "original_selector",
        },
      });
      const descriptor = await dispatchTool(lookup.metadata_lookup.tool, lookup.metadata_lookup.arguments, context);
      expect(descriptor).toMatchObject({ status: "ok", data: { command: { id: "asset.import" } } });
    }
  });

  it("pathguard-traversal: path traversal bridge error remains stable with safe next actions", async () => {
    const context = makeContext([heartbeat()], [assetImportCommand()], allowWritePolicy());
    context.bridge = new ErrorBridgeClient(context.bridge as BridgeClient, {
      status: "error",
      error: { code: "path_traversal_rejected", message: "path_traversal_rejected" },
    });

    const result = await dispatchTool(
      "sgg_unity_call",
      { project: "game", command: "asset.import", args_json: JSON.stringify({ asset_path: "Assets/../ProjectSettings/ProjectSettings.asset" }) },
      context,
    );

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("path_traversal_rejected");
      expect(result.next_valid_actions).toEqual(["use_unity_relative_path", "remove_path_traversal"]);
      expect(result.next_valid_actions).not.toContain("ask_user_to_update_policy");
    }
  });

  it("absolute-path: absolute asset paths are rejected with Unity-relative next actions", async () => {
    const context = makeContext([heartbeat()], [assetImportCommand()], allowWritePolicy());
    context.bridge = new ErrorBridgeClient(context.bridge as BridgeClient, {
      status: "error",
      error: { code: "absolute_path_rejected", message: "absolute_path_rejected" },
    });

    const result = await dispatchTool(
      "sgg_unity_call",
      { project: "game", command: "asset.import", args_json: JSON.stringify({ asset_path: "C:/repo/game/Assets/Foo.prefab" }) },
      context,
    );

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("absolute_path_rejected");
      expect(result.next_valid_actions).toEqual(["use_unity_relative_path", "remove_path_traversal"]);
    }
  });

  it("feature-disabled-direct-call: feature-gated command direct call is blocked", async () => {
    const gatedCommand = { ...command("scene.hierarchy", "read"), feature_flag: "semantic_scene_inspection" };
    const context = makeContext([heartbeat()], [gatedCommand]);
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "scene.hierarchy", args_json: "{}" }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("feature_disabled");
      expect(result.next_valid_actions).toEqual(["call_sgg_unity_describe", "ask_user_to_enable_feature_flag"]);
    }
  });

  it("approval-required-retry: first ask returns approval_request, matching one-shot approval executes, repeated call asks again", async () => {
    const args = { asset_path: "Assets/Foo.prefab" };
    const request = { project: "game", command: "asset.import", args_json: JSON.stringify(args), request_id: "req-1" };
    const context = makeContext([heartbeat()], [assetImportCommand()], policy());
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;
    context.now = () => now;
    context.approvalDefaultTtlMs = 60_000;
    context.approvalStore = new ApprovalStore(tempApprovalDir());

    const first = await dispatchTool("sgg_unity_call", request, context);
    approveResult(context, first);
    const second = await dispatchTool("sgg_unity_call", request, context);
    const third = await dispatchTool("sgg_unity_call", request, context);

    expect(first.status).toBe("error");
    if (first.status === "error") {
      expect(first.error.code).toBe("confirmation_required");
      expect(first.error.details).toMatchObject({ approval_request: { scope: "single_call_only", args_hash: argsHash(args) } });
    }
    expect(second.status).toBe("ok");
    expect(third.status).toBe("error");
    expect(bridge.invokeCount).toBe(1);
    if (third.status === "error") {
      expect(third.error.code).toBe("confirmation_required");
    }
  });

  it("confirmation-bypass: changing request_id does not bypass approval args matching", async () => {
    const args = { asset_path: "Assets/Foo.prefab" };
    const context = makeContext([heartbeat()], [assetImportCommand()], policy());
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;
    context.now = () => now;
    context.approvalDefaultTtlMs = 60_000;
    context.approvalStore = new ApprovalStore(tempApprovalDir());

    const first = await dispatchTool("sgg_unity_call", { project: "game", command: "asset.import", args_json: JSON.stringify(args), request_id: "req-1" }, context);
    approveResult(context, first);
    const bypass = await dispatchTool("sgg_unity_call", { project: "game", command: "asset.import", args_json: JSON.stringify(args), request_id: "req-2" }, context);

    expect(first.status).toBe("error");
    expect(bypass.status).toBe("error");
    expect(bridge.invokeCount).toBe(0);
    if (bypass.status === "error") {
      expect(bypass.error.code).toBe("confirmation_required");
      expect(bypass.next_valid_actions).toEqual(["ask_user_for_confirmation", "choose_safer_command"]);
    }
  });

  it("scene-write-without-approval: scene save remains confirmation-gated before bridge invoke", async () => {
    const context = makeContext([heartbeat()], [sceneSaveCommand()], {
      schema_version: 1,
      defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
      projects: { game: { allow: ["compile.status"], ask: ["scene.save"], deny: [] } },
    });
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "scene.save", args_json: JSON.stringify({ scene_path: "Assets/Scenes/Main.unity" }) }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("confirmation_required");
    }
  });

  it("duplicate-project-id: duplicate project id is not guessed", async () => {
    const context = makeContext([
      heartbeat({ instance_id: "game-a" }),
      heartbeat({ instance_id: "game-b", unity_pid: 22222, bridge_port: 18143 }),
    ]);

    const result = await dispatchTool("sgg_unity_describe", { project: "game", selector: "status" }, context);

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("duplicate_project_id");
      expect(result.error.details).toMatchObject({ matches: [{ instance_id: "game-a" }, { instance_id: "game-b" }] });
    }
  });

  it("stale-project-id: stale heartbeat is reported instead of routed", async () => {
    const context = makeContext([heartbeat({ last_heartbeat: "2026-05-19T09:00:00.000Z" })], [assetImportCommand()]);
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "asset.import", args_json: JSON.stringify({ asset_path: "Assets/Foo.prefab" }) }, context);

    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("project_stale");
      expect(result.next_valid_actions).toEqual(["call_sgg_unity_projects", "inspect_project_status"]);
    }
  });

  it("untrusted-manifest-under-allow-wildcard: package command stays ask-gated", async () => {
    const context = makeContext([heartbeat()], [{
      ...command("demo.status", "read"),
      source: "manifest",
      package_id: "com.example.untrusted-demo",
      trust: "untrusted_package",
    }], {
      schema_version: 1,
      defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
      projects: { game: { allow: ["demo.*"], ask: [], deny: [] } },
      trusted_packages: [],
      untrusted_package_default: "ask",
    });
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;

    const describe = await dispatchTool("sgg_unity_describe", { project: "game", selector: "commands" }, context);
    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "demo.status", args_json: "{}" }, context);

    expect(describe.status).toBe("ok");
    if (describe.status === "ok") {
      expect(describe.data).toMatchObject({ commands: [{ id: "demo.status", requires_confirmation: true }] });
    }
    expect(result.status).toBe("error");
    expect(bridge.invokeCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("confirmation_required");
    }
  });

  it("checkpoint-restore-follow-up: restore reports assets.refresh and compile.status as next actions", async () => {
    const projectRoot = tempProjectRoot();
    writeProjectFile(projectRoot, "Assets/Scenes/Main.unity", "before");
    const context = makeContext([heartbeat({ project_root: projectRoot })], [compileStatusCommand]);
    context.features = { sgg_mcp_checkpoints: true };
    context.now = () => now;

    const created = await dispatchTool("sgg_unity_call", { project: "game", command: "checkpoint.create", args_json: JSON.stringify({ paths: ["Assets/Scenes/Main.unity"] }) }, context);
    writeProjectFile(projectRoot, "Assets/Scenes/Main.unity", "after");
    const restored = await dispatchTool("sgg_unity_call", { project: "game", command: "checkpoint.restore", args_json: JSON.stringify({ id: "checkpoint-2026-05-19T10-00-00-000Z" }) }, context);

    expect(created.status).toBe("ok");
    expect(restored.status).toBe("ok");
    if (restored.status === "ok") {
      expect(restored.next_valid_actions).toEqual(["assets.refresh", "compile.status"]);
      expect(restored.data).toMatchObject({ restored_count: 1 });
    }
  });

  it("huge-output-truncation: truncation metadata and artifact refs pass through", async () => {
    const context = makeContext([heartbeat()], [command("asset.search", "read")]);
    context.bridge = new DataBridgeClient(context.bridge as BridgeClient, {
      matches_preview: "x".repeat(1024),
      artifact_refs: ["Library/SGGUnityMcp/job-artifacts/search/result.json"],
      truncated: true,
      omitted_chars: 100000,
    });

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "asset.search", args_json: "{}" }, context);

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.truncated).toBe(true);
      expect(result.artifact_refs).toEqual(["Library/SGGUnityMcp/job-artifacts/search/result.json"]);
      expect(result.data).toMatchObject({ truncated: true, omitted_chars: 100000 });
    }
  });

  it("malicious-command-manifest: command catalog entries do not become MCP top-level tools", async () => {
    const context = makeContext([heartbeat()], [compileStatusCommand, command("set_active", "execute"), command("sgg_unity_eval_root", "execute")]);

    const result = await dispatchTool("sgg_unity_describe", { project: "game", selector: "commands" }, context);

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({ commands: [{ id: "compile.status" }, { id: "set_active" }, { id: "sgg_unity_eval_root" }] });
    }
    expect(TOOL_NAMES).toEqual(["sgg_unity_projects", "sgg_unity_stale_projects", "sgg_unity_stale_registry_cleanup", "sgg_unity_describe", "sgg_unity_call", "sgg_unity_batch", "sgg_unity_job", "sgg_unity_logs"]);
    expect(TOOL_NAMES).not.toContain("set_active");
    expect(TOOL_NAMES).not.toContain("sgg_unity_eval_root");
  });
});

function makeContext(records: HeartbeatRecord[], commands: CommandDescriptor[] = [compileStatusCommand], policyConfig?: PolicyConfig): DispatcherContext {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-agent-evals-"));
  tempDirs.push(instancesDir);

  for (const record of records) {
    fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record, null, 2));
  }

  return {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now }),
    bridge: new FakeUnityBridgeClient(commands),
    approvalStore: new ApprovalStore(tempApprovalDir()),
    approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }),
    ...(policyConfig ? { policy: policyConfig } : {}),
  };
}

function approveResult(context: DispatcherContext, result: Awaited<ReturnType<typeof dispatchTool>>): void {
  if (result.status !== "error" || result.error.code !== "confirmation_required") throw new Error("Expected pending approval request");
  const request = result.error.details?.approval_request as Record<string, unknown> | undefined;
  context.approvalStore!.approvePending({ pending_id: String(request?.pending_id), ttl_ms: 60_000, now });
}

function tempApprovalDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-agent-eval-approvals-"));
  tempDirs.push(dir);
  return dir;
}

function tempProjectRoot(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-agent-eval-project-"));
  tempDirs.push(dir);
  return dir;
}

function writeProjectFile(projectRoot: string, relativePath: string, content: string): void {
  const fullPath = path.join(projectRoot, relativePath);
  fs.mkdirSync(path.dirname(fullPath), { recursive: true });
  fs.writeFileSync(fullPath, content, "utf8");
}

function command(id: string, risk: CommandDescriptor["risk"]): CommandDescriptor {
  return {
    id,
    title: id,
    description: id,
    package: "com.example.eval",
    risk,
    idempotency: "safe",
    timeout_ms: 1000,
    schema: { type: "object" },
  };
}

function assetImportCommand(): CommandDescriptor {
  return {
    ...command("asset.import", "write"),
    schema: {
      type: "object",
      properties: {
        asset_path: { type: "string", minLength: 1 },
      },
      required: ["asset_path"],
      additionalProperties: false,
    },
  };
}

function sceneSaveCommand(): CommandDescriptor {
  return {
    ...command("scene.save", "write"),
    schema: {
      type: "object",
      properties: {
        scene_path: { type: "string", minLength: 1 },
      },
      required: ["scene_path"],
      additionalProperties: false,
    },
  };
}

function policy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
    projects: { game: { allow: ["compile.status"], ask: [], deny: ["shell.*"] } },
  };
}

function allowWritePolicy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: { read: "allow", write: "allow", execute: "deny", destructive: "ask", network: "deny" },
    projects: {},
  };
}

class ErrorBridgeClient implements BridgeClient {
  constructor(
    readonly inner: BridgeClient,
    readonly invokeResult: BridgeInvokeResponse,
  ) {}

  describe(record: HeartbeatRecord, selector: string): Promise<unknown> {
    return this.inner.describe(record, selector);
  }

  invoke(_record: HeartbeatRecord, _request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    return Promise.resolve(this.invokeResult);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number): Promise<unknown> {
    return this.inner.logs(record, kind, limit);
  }
}

class DataBridgeClient implements BridgeClient {
  constructor(
    readonly inner: BridgeClient,
    readonly data: Record<string, unknown>,
  ) {}

  describe(record: HeartbeatRecord, selector: string): Promise<unknown> {
    return this.inner.describe(record, selector);
  }

  invoke(_record: HeartbeatRecord, _request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    return Promise.resolve({ status: "ok", data: this.data });
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number): Promise<unknown> {
    return this.inner.logs(record, kind, limit);
  }
}

class CountingBridgeClient implements BridgeClient {
  invokeCount = 0;

  constructor(readonly inner: BridgeClient) {}

  describe(record: HeartbeatRecord, selector: string): Promise<unknown> {
    return this.inner.describe(record, selector);
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    return this.inner.invoke(record, request);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number): Promise<unknown> {
    return this.inner.logs(record, kind, limit);
  }
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [
      { environment: "unity", host_path: "C:/u/game", canonical_path: "c:/u/game" },
      { environment: "windows", host_path: "C:\\u\\game", canonical_path: "c:/u/game" },
      { environment: "wsl", host_path: "/mnt/c/u/game", canonical_path: "c:/u/game" },
    ],
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "evals",
    last_heartbeat: "2026-05-19T09:59:30.000Z",
    ...overrides,
  };
}
