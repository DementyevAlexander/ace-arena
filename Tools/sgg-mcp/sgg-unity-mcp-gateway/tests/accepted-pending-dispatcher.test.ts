import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { ApprovalStore, type ApprovalPendingGeneration } from "../src/approvals/approval-store.js";
import { FakeUnityBridgeClient, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import { dispatchAcceptedPendingCall, dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import type { GatewayResult } from "../src/mcp/results.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { PolicyManager } from "../src/policy/policy-manager.js";

const dirs: string[] = [];
const now = new Date("2026-07-11T10:00:00.000Z");

afterEach(() => {
  for (const dir of dirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("accepted pending dispatcher", () => {
  it.each([
    ["project selector", (env: Environment) => { env.acceptedArgs.project = "Game"; }],
    ["canonical root", (env: Environment) => env.writeHeartbeat({ canonical_project_root: "c:/u/other", project_root: "C:/u/other" })],
    ["instance", (env: Environment) => env.writeHeartbeat({ instance_id: "game-b" })],
    ["catalog", (env: Environment) => env.writeHeartbeat({ commands_hash: "catalog-b" })],
    ["descriptor", (env: Environment) => { env.descriptor.title = "Changed title"; }],
    ["args", (env: Environment) => { env.acceptedArgs.args_json = "{\"dry_run\":false}"; }],
    ["request", (env: Environment) => { env.acceptedArgs.request_id = "request-b"; }],
    ["mode", (env: Environment) => { env.acceptedArgs.mode = "job"; }],
    ["policy pattern", (env: Environment) => { env.context.policy = askPolicy("test.*"); }],
  ])("invalidates %s drift and creates a fresh ask confirmation", async (_name, mutate) => {
    const env = await environment();
    mutate(env);

    const result = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, {
      generation: generation(env.original),
      original_confirmation: env.original,
    });

    expect(result).toMatchObject({
      status: "error",
      error: { code: "confirmation_required", details: { approval_request: { invalidation_reason: "accepted_request_context_changed" } } },
    });
    expect(result).not.toEqual(env.original);
    expect(env.invokeCount()).toBe(0);
    expect(env.context.approvalStore?.listPending({ now })).toHaveLength(1);
  });

  it("returns a stronger current deny and permanently invalidates the accepted generation", async () => {
    const env = await environment();
    env.context.policy = { ...askPolicy(), projects: { game: { allow: [], ask: [], deny: ["test.run"] } } };

    const denied = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, { generation: generation(env.original), original_confirmation: env.original });
    expect(denied).toMatchObject({ status: "error", error: { code: "permission_denied" } });

    env.context.policy = askPolicy();
    const restored = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, { generation: generation(env.original), original_confirmation: env.original });
    expect(restored).toEqual(env.original);
    expect(env.invokeCount()).toBe(0);
  });

  it("claims the stale ask generation once before executing under a current allow decision", async () => {
    const env = await environment();
    env.context.policy = { ...askPolicy(), projects: { game: { allow: ["test.run"], ask: [], deny: [] } } };

    const first = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, { generation: generation(env.original), original_confirmation: env.original });
    const loser = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, { generation: generation(env.original), original_confirmation: env.original });

    expect(first.status).toBe("ok");
    expect(loser).toEqual(env.original);
    expect(env.invokeCount()).toBe(1);
  });

  it("returns a stronger current schema error and restoring the descriptor cannot revive the claim", async () => {
    const env = await environment();
    const originalSchema = env.descriptor.schema;
    env.descriptor.schema = { type: "object", required: ["required_now"], properties: { required_now: { type: "boolean" } }, additionalProperties: false };

    const invalid = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, { generation: generation(env.original), original_confirmation: env.original });
    expect(invalid).toMatchObject({ status: "error", error: { code: "schema_validation_failed" } });

    env.descriptor.schema = originalSchema;
    const restored = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, { generation: generation(env.original), original_confirmation: env.original });
    expect(restored).toEqual(env.original);
    expect(env.invokeCount()).toBe(0);
  });

  it("does not let an accepted native prompt bypass a degraded policy", async () => {
    const env = await environment();
    const policyPath = path.join(tempDir("policy"), "policy.json");
    fs.writeFileSync(policyPath, JSON.stringify(askPolicy()));
    env.context.policyManager = new PolicyManager(policyPath);
    fs.rmSync(policyPath);

    const result = await dispatchAcceptedPendingCall(env.acceptedArgs, env.context, { generation: generation(env.original), original_confirmation: env.original });

    expect(result).toMatchObject({ status: "error", error: { code: "policy_degraded" } });
    expect(env.invokeCount()).toBe(0);
  });
});

type CallArgs = { project: string; command: string; args_json: string; mode: "wait" | "job"; request_id: string };
type Environment = {
  context: DispatcherContext;
  descriptor: CommandDescriptor;
  original: GatewayResult;
  acceptedArgs: CallArgs;
  writeHeartbeat: (overrides: Partial<HeartbeatRecord>) => void;
  invokeCount: () => number;
};

async function environment(): Promise<Environment> {
  const instancesDir = tempDir("instances");
  const approvalsDir = tempDir("approvals");
  const descriptor: CommandDescriptor = {
    id: "test.run", title: "Run", description: "Run", package: "builtin", risk: "execute", idempotency: "unsafe", timeout_ms: 1000,
    supports_job: true,
    schema: { type: "object", properties: { dry_run: { type: "boolean" } }, additionalProperties: false },
  };
  let currentHeartbeat = heartbeat();
  const heartbeatPath = path.join(instancesDir, "game.json");
  const writeHeartbeat = (overrides: Partial<HeartbeatRecord>) => {
    currentHeartbeat = { ...currentHeartbeat, ...overrides };
    fs.writeFileSync(heartbeatPath, JSON.stringify(currentHeartbeat));
  };
  writeHeartbeat({});
  const fake = new FakeUnityBridgeClient([descriptor]);
  let invokes = 0;
  const context: DispatcherContext = {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 60_000, now: () => now }),
    bridge: {
      describe: (record, selector) => fake.describe(record, selector),
      invoke: async (record, request) => { invokes += 1; return fake.invoke(record, request); },
      logs: (record, kind, limit) => fake.logs(record, kind, limit),
    },
    policy: askPolicy(),
    approvalStore: new ApprovalStore(approvalsDir),
    approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }),
    approvalDefaultTtlMs: 60_000,
    now: () => now,
  };
  const acceptedArgs: CallArgs = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", mode: "wait", request_id: "request-a" };
  const original = await dispatchTool("sgg_unity_call", acceptedArgs, context);
  expect(original).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
  return { context, descriptor, original, acceptedArgs: { ...acceptedArgs }, writeHeartbeat, invokeCount: () => invokes };
}

function generation(result: GatewayResult): ApprovalPendingGeneration {
  if (result.status !== "error") throw new Error("Expected confirmation");
  const request = result.error.details?.approval_request as Record<string, unknown>;
  return { id: String(request.pending_id), created_at: String(request.created_at), updated_at: String(request.updated_at), expires_at: String(request.expires_at) };
}

function askPolicy(pattern = "test.run"): PolicyConfig {
  return { schema_version: 1, defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" }, projects: { game: { allow: [], ask: [pattern], deny: [] } } };
}

function heartbeat(): HeartbeatRecord {
  return { schema_version: 1, instance_id: "game-a", project: "game", display_name: "Game", project_root: "C:/u/game", canonical_project_root: "c:/u/game", aliases: [], unity_version: "6000.0.0f1", unity_pid: 1, bridge_host: "127.0.0.1", bridge_port: 1, token: "secret", status: "ready", commands_hash: "catalog-a", last_heartbeat: now.toISOString() };
}

function tempDir(name: string): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), `sgg-${name}-`));
  dirs.push(dir);
  return dir;
}
