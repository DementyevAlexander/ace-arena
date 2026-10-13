import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalStore, argsHash } from "../src/approvals/approval-store.js";
import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, compileStatusCommand } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { PIPELINE_COMMANDS, runPipelineCommand } from "../src/mcp/pipeline-commands.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-27T10:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("pipeline virtual commands", () => {
  it("declares bounded feature-gated command descriptors", () => {
    expect(PIPELINE_COMMANDS.map((command) => command.id)).toEqual(["pipeline.template", "pipeline.list", "pipeline.read", "pipeline.create", "pipeline.validate", "pipeline.start", "pipeline.status", "pipeline.record_step", "pipeline.cancel"]);
    expect(new Set(PIPELINE_COMMANDS.map((command) => command.feature_flag))).toEqual(new Set(["sgg_mcp_pipelines"]));
    expect(new Map(PIPELINE_COMMANDS.map((command) => [command.id, command.risk]))).toEqual(new Map([
      ["pipeline.template", "read"],
      ["pipeline.list", "read"],
      ["pipeline.read", "read"],
      ["pipeline.create", "write"],
      ["pipeline.validate", "read"],
      ["pipeline.start", "write"],
      ["pipeline.status", "read"],
      ["pipeline.record_step", "write"],
      ["pipeline.cancel", "write"],
    ]));
    expect(PIPELINE_COMMANDS.filter((command) => command.risk === "read").every((command) => command.idempotency === "safe")).toBe(true);
  });

  it("exposes pipeline commands behind the feature flag without invoking the bridge", async () => {
    const disabled = makeContext(makeProjectRoot());
    const disabledResult = await dispatchTool("sgg_unity_describe", { project: "game", selector: "commands:pipeline" }, disabled);
    expect(disabledResult.status).toBe("ok");
    if (disabledResult.status === "ok") {
      expect((disabledResult.data as { commands: unknown[] }).commands).toEqual([]);
    }

    const enabled = makeContext(makeProjectRoot());
    const bridge = new CountingBridgeClient(enabled.bridge as BridgeClient);
    enabled.bridge = bridge;
    enabled.features = { sgg_mcp_pipelines: true };
    const enabledResult = await dispatchTool("sgg_unity_describe", { project: "game", selector: "commands:pipeline" }, enabled);
    expect(enabledResult.status).toBe("ok");
    if (enabledResult.status === "ok") {
      expect((enabledResult.data as { commands: Array<{ id: string }> }).commands.map((command) => command.id)).toEqual(["pipeline.template", "pipeline.list", "pipeline.read", "pipeline.create", "pipeline.validate", "pipeline.start", "pipeline.status", "pipeline.record_step", "pipeline.cancel"]);
    }

    const called = await dispatchTool("sgg_unity_call", { project: "game", command: "pipeline.template", args_json: "{}" }, enabled);
    expect(called.status).toBe("ok");
    expect(bridge.invokeCount).toBe(0);
  });

  it("creates local and versioned files only through safe relative names", async () => {
    const projectRoot = makeProjectRoot();
    const record = heartbeat({ project_root: projectRoot });
    const local = runPipelineCommand("pipeline.create", { name: "qa/compile.json", content: validPipelineJson(), overwrite: false }, record, now);
    const versioned = runPipelineCommand("pipeline.create", { name: "release.md", content: "# Pipeline\n", versioned: true }, record, now);
    const traversal = runPipelineCommand("pipeline.create", { name: "../secret.json", content: "{}" }, record, now);
    const absolute = runPipelineCommand("pipeline.read", { name: "/tmp/secret.json" }, record, now);

    expect(local.status).toBe("ok");
    expect(versioned.status).toBe("ok");
    expect(traversal.status).toBe("error");
    expect(absolute.status).toBe("error");
    expect(fs.existsSync(path.join(projectRoot, "Library", "SGGUnityMcp", "pipelines", "qa", "compile.json"))).toBe(true);
    expect(fs.existsSync(path.join(projectRoot, ".mcp", "pipelines", "release.md"))).toBe(true);
  });

  it("lists and reads bounded pipeline files under selected roots", async () => {
    const projectRoot = makeProjectRoot();
    const root = path.join(projectRoot, "Library", "SGGUnityMcp", "pipelines");
    fs.mkdirSync(path.join(root, "nested"), { recursive: true });
    fs.writeFileSync(path.join(root, "a.json"), "a".repeat(20), "utf8");
    fs.writeFileSync(path.join(root, "nested", "b.md"), "b".repeat(20), "utf8");
    fs.writeFileSync(path.join(root, "ignored.txt"), "no", "utf8");
    const record = heartbeat({ project_root: projectRoot });

    const list = runPipelineCommand("pipeline.list", { max_results: 1 }, record, now);
    const read = runPipelineCommand("pipeline.read", { name: "nested/b.md", max_chars: 5 }, record, now);

    expect(list.status).toBe("ok");
    if (list.status === "ok") {
      expect((list.data as { pipelines: unknown[]; truncated: boolean }).pipelines).toHaveLength(1);
      expect((list.data as { truncated: boolean }).truncated).toBe(true);
    }
    expect(read.status).toBe("ok");
    if (read.status === "ok") {
      expect(read.data).toMatchObject({ content: "bbbbb", truncated: true, max_chars: 5 });
    }
  });

  it("validates safe structure and rejects auto-run or implicit project pipelines", () => {
    const record = heartbeat({ project_root: makeProjectRoot() });
    const valid = runPipelineCommand("pipeline.validate", { content: validPipelineJson() }, record, now);
    const manual = runPipelineCommand("pipeline.validate", { content: validManualPipelineJson() }, record, now);
    const invalid = runPipelineCommand("pipeline.validate", { content: JSON.stringify({ schema_version: 1, steps: [{ command: "Compile.Status", project: "active" }, { command: "compile.status", action: "run_next", approval_bypass: true }] }) }, record, now);
    const hyphenated = runPipelineCommand("pipeline.validate", { content: JSON.stringify({ schema_version: 1, steps: [{ command: "compile.status", project: "game", action: "run-next", BypassApproval: true }] }) }, record, now);

    expect(valid.status).toBe("ok");
    if (valid.status === "ok") {
      expect(valid.data).toMatchObject({ valid: true, format: "json", step_count: 1, automatic_execution: false });
    }
    expect(manual.status).toBe("ok");
    if (manual.status === "ok") {
      expect(manual.data).toMatchObject({ valid: true, format: "json", step_count: 2, automatic_execution: false });
    }
    expect(invalid.status).toBe("ok");
    if (invalid.status === "ok") {
      expect((invalid.data as { valid: boolean }).valid).toBe(false);
      expect(JSON.stringify((invalid.data as { issues: unknown[] }).issues)).toContain("lowercase dot notation");
      expect(JSON.stringify((invalid.data as { issues: unknown[] }).issues)).toContain("auto-run");
      expect(JSON.stringify((invalid.data as { issues: unknown[] }).issues)).toContain("explicit project");
    }
    expect(hyphenated.status).toBe("ok");
    if (hyphenated.status === "ok") {
      expect((hyphenated.data as { valid: boolean }).valid).toBe(false);
      expect(JSON.stringify((hyphenated.data as { issues: unknown[] }).issues)).toContain("auto-run");
      expect(JSON.stringify((hyphenated.data as { issues: unknown[] }).issues)).toContain("approval bypass");
    }
  });

  it("tracks manual run state without executing or verifying steps", () => {
    const projectRoot = makeProjectRoot();
    const record = heartbeat({ project_root: projectRoot });

    const started = runPipelineCommand("pipeline.start", { run_id: "run-1", content: validManualPipelineJson(), inputs: { target: "Main" } }, record, now);

    expect(started.status).toBe("ok");
    if (started.status === "ok") {
      expect(started.data).toMatchObject({ started: true, run_id: "run-1", status: "running", automatic_execution: false, manual_run_state_only: true });
      expect((started.data as { current_step: { id: string; command: string; gateway_executed: boolean } }).current_step).toMatchObject({ id: "inspect", command: "compile.status", gateway_executed: false });
    }
    expect(fs.existsSync(path.join(projectRoot, "Library", "SGGUnityMcp", "pipeline-runs", "run-1", "run.json"))).toBe(true);

    const recorded = runPipelineCommand("pipeline.record_step", { run_id: "run-1", step_id: "inspect", result: "success", produced: { compile_ready: true }, artifact_refs: ["unity://compile/status"], notes: "Checked manually." }, record, now);
    expect(recorded.status).toBe("ok");
    if (recorded.status === "ok") {
      expect(recorded.data).toMatchObject({ recorded: true, status: "running", manual_record_only: true, current_step: { id: "review" }, produced: { compile_ready: true } });
      expect((recorded.data as { history: Array<{ gateway_executed: boolean; gateway_verified: boolean }> }).history[0]).toMatchObject({ gateway_executed: false, gateway_verified: false });
    }

    const blocked = runPipelineCommand("pipeline.record_step", { run_id: "run-1", step_id: "review", result: "blocked", evidence: ["Waiting for human review"], failure: { reason: "approval_pending" } }, record, now);
    expect(blocked.status).toBe("ok");
    if (blocked.status === "ok") {
      expect(blocked.data).toMatchObject({ status: "blocked", current_step: { id: "review" }, rollback_performed: false });
      expect((blocked.data as { next_valid_actions: string[] }).next_valid_actions).toContain("pipeline.cancel");
    }

    const cancelled = runPipelineCommand("pipeline.cancel", { run_id: "run-1", reason: "User stopped the manual run" }, record, now);
    expect(cancelled.status).toBe("ok");
    if (cancelled.status === "ok") {
      expect(cancelled.data).toMatchObject({ cancelled: true, status: "cancelled", rollback_performed: false, cancellation: { gateway_executed_rollback: false } });
    }
  });

  it("ask-gates pipeline.create through public policy and never auto-executes", async () => {
    const projectRoot = makeProjectRoot();
    const context = makeContext(projectRoot);
    const bridge = new CountingBridgeClient(context.bridge as BridgeClient);
    context.bridge = bridge;
    context.features = { sgg_mcp_pipelines: true };
    context.now = () => now;
    context.approvalStore = new ApprovalStore(tempApprovalDir());
    context.policy = {
      schema_version: 1,
      defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
      trusted_packages: [],
      untrusted_package_default: "ask",
      projects: { game: { allow: ["compile.status", "pipeline.template", "pipeline.list", "pipeline.read", "pipeline.validate"], ask: ["pipeline.create"], deny: [] } },
    };
    const args = { name: "manual.json", content: validPipelineJson(), overwrite: false };

    const blocked = await dispatchTool("sgg_unity_call", { project: "game", command: "pipeline.create", args_json: JSON.stringify(args), request_id: "pipeline-1" }, context);
    expect(blocked.status).toBe("error");
    if (blocked.status === "error") {
      expect(blocked.error.code).toBe("confirmation_required");
    }

    const approvalRequest = (blocked.status === "error" ? blocked.error.details?.approval_request : undefined) as Record<string, unknown> | undefined;
    context.approvalStore.approvePending({ pending_id: String(approvalRequest?.pending_id), ttl_ms: 60_000, now });
    const created = await dispatchTool("sgg_unity_call", { project: "game", command: "pipeline.create", args_json: JSON.stringify(args), request_id: "pipeline-1" }, context);
    expect(created.status).toBe("ok");
    expect(bridge.invokeCount).toBe(0);
    if (created.status === "ok") {
      expect(created.data).toMatchObject({ relative_path: "Library/SGGUnityMcp/pipelines/manual.json", automatic_execution: false });
    }
  });
});

function validPipelineJson(): string {
  return JSON.stringify({ schema_version: 1, title: "Manual compile inspection", steps: [{ id: "inspect", command: "compile.status", project: "game", args: {} }] });
}

function validManualPipelineJson(): string {
  return JSON.stringify({
    schema_version: 1,
    id: "manual.review.pipeline.v1",
    title: "Manual compile and review",
    mode: "manual_step_execution",
    steps: [
      { id: "inspect", action: "mcp.command", command: "compile.status", project: "game", args: {}, transitions: [{ on: "success", to: "review" }] },
      { id: "review", action: "manual.review", requires_user_approval: true },
    ],
  });
}

function makeProjectRoot(): string {
  const projectRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pipeline-project-"));
  tempDirs.push(projectRoot);
  return projectRoot;
}

function makeContext(projectRoot: string): DispatcherContext {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pipeline-registry-"));
  tempDirs.push(instancesDir);
  fs.writeFileSync(path.join(instancesDir, "game-a.json"), JSON.stringify(heartbeat({ project_root: projectRoot }), null, 2));
  return {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now }),
    bridge: new FakeUnityBridgeClient([compileStatusCommand]),
    approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }),
  };
}

function tempApprovalDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pipeline-approvals-"));
  tempDirs.push(dir);
  return dir;
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
    commands_hash: "9f12a3c0",
    last_heartbeat: "2026-05-27T09:59:30.000Z",
    ...overrides,
  };
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
