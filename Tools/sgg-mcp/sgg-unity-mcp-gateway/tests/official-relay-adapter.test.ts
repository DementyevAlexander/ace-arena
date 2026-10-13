import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalStore, argsHash } from "../src/approvals/approval-store.js";
import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { FakeUnityBridgeClient, compileStatusCommand } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import { TOOL_NAMES } from "../src/mcp/contracts.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { OfficialRelayAdapter, type OfficialRelayClient, type OfficialRelayConfig, type OfficialRelayTool } from "../src/mcp/official-relay-adapter.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const now = new Date("2026-05-20T10:00:00.000Z");
const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("official Unity MCP relay adapter", () => {
  it("keeps the top-level MCP tool surface unchanged", () => {
    expect(TOOL_NAMES).toEqual(["sgg_unity_projects", "sgg_unity_stale_projects", "sgg_unity_stale_registry_cleanup", "sgg_unity_describe", "sgg_unity_call", "sgg_unity_batch", "sgg_unity_job", "sgg_unity_logs"]);
  });

  it("imports only allowlisted tools as official.<toolName> command descriptors", async () => {
    const relayClient = new FakeOfficialRelayClient([
      tool("ping", { readOnlyHint: true }),
      tool("dangerousDelete", { destructiveHint: true }),
    ]);
    const context = makeContext(relayClient, officialConfig({ allow_tools: ["ping"] }));

    const result = await dispatchTool("sgg_unity_describe", { project: "game", selector: "commands:official" }, context);

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({ commands: [{ id: "official.ping", source: "official_relay", trust: "untrusted_package" }] });
      expect(JSON.stringify(result.data)).not.toContain("dangerousDelete");
    }
  });

  it("requires policy approval before forwarding official tool calls", async () => {
    const relayClient = new FakeOfficialRelayClient([tool("ping")]);
    const config = officialConfig({ allow_tools: ["ping"], tool_risks: { ping: "write" } });
    const approvalsDir = tempDir("sgg-official-relay-approvals-");
    const approvalStore = new ApprovalStore(approvalsDir);
    const context = makeContext(relayClient, config, policy());
    context.approvalStore = approvalStore;
    context.approvalDefaultTtlMs = 300000;
    context.now = () => now;

    const blocked = await dispatchTool("sgg_unity_call", { project: "game", command: "official.ping", args_json: "{}" }, context);

    expect(blocked.status).toBe("error");
    expect(relayClient.callCount).toBe(0);
    if (blocked.status === "error") {
      expect(blocked.error.code).toBe("confirmation_required");
    }

    const approvalRequest = (blocked.status === "error" ? blocked.error.details?.approval_request : undefined) as Record<string, unknown> | undefined;
    approvalStore.approvePending({ pending_id: String(approvalRequest?.pending_id), ttl_ms: 300000, now });
    const allowed = await dispatchTool("sgg_unity_call", { project: "game", command: "official.ping", args_json: "{}" }, context);

    expect(allowed.status).toBe("ok");
    expect(relayClient.callCount).toBe(1);
    expect(relayClient.lastCall).toEqual({ name: "ping", args: {} });
  });

  it("validates configured Unity-relative path fields before official tools/call", async () => {
    const relayClient = new FakeOfficialRelayClient([tool("inspectAsset", { readOnlyHint: true }, { asset_path: { type: "string" } })]);
    const context = makeContext(
      relayClient,
      officialConfig({ allow_tools: ["inspectAsset"], tool_risks: { inspectAsset: "read" }, unity_relative_path_fields: { inspectAsset: ["asset_path"] } }),
      { ...policy(), trusted_packages: ["com.unity.official-mcp-relay"], projects: { game: { allow: ["official.inspectAsset"], ask: [], deny: [] } } },
    );

    const rejected = await dispatchTool("sgg_unity_call", { project: "game", command: "official.inspectAsset", args_json: JSON.stringify({ asset_path: "/tmp/foo.prefab" }) }, context);

    expect(rejected.status).toBe("error");
    expect(relayClient.callCount).toBe(0);
    if (rejected.status === "error") {
      expect(rejected.error.code).toBe("absolute_path_rejected");
    }

    const accepted = await dispatchTool("sgg_unity_call", { project: "game", command: "official.inspectAsset", args_json: JSON.stringify({ asset_path: "Assets/Foo.prefab" }) }, context);

    expect(accepted.status).toBe("ok");
    expect(relayClient.callCount).toBe(1);
    expect(relayClient.lastCall).toEqual({ name: "inspectAsset", args: { asset_path: "Assets/Foo.prefab" } });
  });
});

class FakeOfficialRelayClient implements OfficialRelayClient {
  callCount = 0;
  lastCall?: { name: string; args: Record<string, unknown> };

  constructor(readonly tools: OfficialRelayTool[]) {}

  async listTools(): Promise<OfficialRelayTool[]> {
    return this.tools;
  }

  async callTool(name: string, args: Record<string, unknown>): Promise<unknown> {
    this.callCount += 1;
    this.lastCall = { name, args };
    return { name, args };
  }
}

function makeContext(relayClient: OfficialRelayClient, config: OfficialRelayConfig, policyConfig?: PolicyConfig): DispatcherContext {
  const instancesDir = tempDir("sgg-official-relay-instances-");
  fs.writeFileSync(path.join(instancesDir, "game.json"), JSON.stringify(heartbeat(), null, 2));
  return {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90000, now: () => now }),
    bridge: new FakeUnityBridgeClient([compileStatusCommand]),
    features: { official_unity_mcp_relay: true },
    officialRelay: new OfficialRelayAdapter(config, relayClient),
    approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }),
    ...(policyConfig ? { policy: policyConfig } : {}),
  };
}

function officialConfig(overrides: Partial<OfficialRelayConfig>): OfficialRelayConfig {
  return {
    enabled: true,
    command: "fake-unity-relay",
    args: [],
    env: {},
    allow_tools: [],
    tool_risks: {},
    unity_relative_path_fields: {},
    connect_timeout_ms: 3000,
    call_timeout_ms: 30000,
    ...overrides,
  };
}

function tool(name: string, annotations: OfficialRelayTool["annotations"] = {}, properties: Record<string, unknown> = {}): OfficialRelayTool {
  return {
    name,
    title: name,
    description: `${name} description`,
    inputSchema: { type: "object", properties, additionalProperties: false },
    annotations,
  };
}

function policy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
    trusted_packages: [],
    untrusted_package_default: "ask",
    projects: { game: { allow: [], ask: [], deny: [] } },
  };
}

function heartbeat(): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game",
    project: "game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [{ environment: "unity", host_path: "C:/u/game", canonical_path: "c:/u/game" }],
    unity_pid: 1234,
    bridge_host: "127.0.0.1",
    bridge_port: 27182,
    status: "ready",
    unity_version: "6000.4.10f1",
    last_heartbeat: now.toISOString(),
    commands_hash: "hash",
  };
}

function tempDir(prefix: string): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), prefix));
  tempDirs.push(dir);
  return dir;
}
