import fs from "node:fs";
import http from "node:http";
import os from "node:os";
import path from "node:path";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { ElicitRequestSchema, ListRootsRequestSchema, SUPPORTED_PROTOCOL_VERSIONS as SDK_LEGACY_PROTOCOL_VERSIONS, type CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { Client as ModernClient, StreamableHTTPClientTransport as ModernStreamableHTTPClientTransport, type CallToolResult as ModernCallToolResult } from "@modelcontextprotocol/client";
import { InMemoryTransport as ModernInMemoryTransport, McpServer as ModernMcpServer } from "@modelcontextprotocol/server";
import { serveStdio } from "@modelcontextprotocol/server/stdio";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ApprovalStore, argsHash } from "../src/approvals/approval-store.js";
import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { KnownUnityPopupSettingsManager } from "../src/approvals/known-unity-popup-settings.js";
import { FakeUnityBridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import type { GatewayResult } from "../src/mcp/results.js";
import { ClientRootsRegistry, clientRootHash } from "../src/mcp/client-roots.js";
import { LEGACY_MCP_PROTOCOL_VERSIONS, SUPPORTED_MCP_PROTOCOL_VERSION, startHttpMcpServer } from "../src/mcp/http-server.js";
import { TOOL_NAMES } from "../src/mcp/contracts.js";
import { createUnityMcpServer, gatewayResultToMcpToolResult, RECOMMENDED_MCP_CLIENT_TIMEOUT_MS, STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS } from "../src/mcp/sdk-server.js";
import { createMcpV2Metrics, createUnityMcpV2ServerFactory, MODERN_MCP_PROTOCOL_VERSION } from "../src/mcp/sdk-v2-server.js";
import { createStdioShimServer } from "../src/mcp/stdio-shim.js";
import { OperationalLogger } from "../src/observability/operational-logger.js";
import { PolicyManager } from "../src/policy/policy-manager.js";
import { ProjectRegistry, publicRegistryOperationalStatus, type HeartbeatRecord } from "../src/registry/project-registry.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-21T10:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("MCP SDK server", () => {
  it.each([false, true])("applies saved known-popup consent in one SDK connection with form capability %s without self-approval or automatic execution", async (form) => {
    const context = makeApprovalContext();
    const descriptor: CommandDescriptor = { ...testRunCommand(), id: "editor.save_prompt.respond", risk: "destructive", schema: { type: "object", properties: { action: { const: "discard" } }, required: ["action"], additionalProperties: false } };
    const bridge = new FakeUnityBridgeClient([descriptor]);
    const invocations = vi.spyOn(bridge, "invoke");
    context.bridge = bridge;
    const directory = tempApprovalDir();
    const file = path.join(directory, "gateway.json");
    const config = JSON.parse(fs.readFileSync(new URL("../config/gateway.example.json", import.meta.url), "utf8"));
    const save = (enabled: boolean) => { config.approvals.auto_allow_known_unity_popups = enabled; fs.writeFileSync(file, JSON.stringify(config)); };
    save(false);
    context.knownUnityPopupSettings = new KnownUnityPopupSettingsManager(file);
    let questions = 0;
    await withInMemoryClient(context, async (client) => {
      if (form) client.setRequestHandler(ElicitRequestSchema, async () => { questions += 1; return { action: "decline" }; });
      const call = () => client.callTool({ name: "sgg_unity_call", arguments: { project: "game", command: descriptor.id, args_json: '{"action":"discard"}' } });
      const off = (await call()).structuredContent as unknown as GatewayResult;
      expect(off.status).toBe("error");
      expect(invocations).not.toHaveBeenCalled();
      save(true);
      expect(invocations).not.toHaveBeenCalled();
      expect((await call()).structuredContent).toMatchObject({ status: "ok" });
      expect(invocations).toHaveBeenCalledTimes(1);
      expect(questions).toBe(form ? 1 : 0);
      save(false);
      expect((await call()).structuredContent).toMatchObject({ status: "error" });
      expect(invocations).toHaveBeenCalledTimes(1);
      expect(questions).toBe(form ? 2 : 0);
    }, form);
  });

  it("revokes known-popup permission at the bridge before-request-issued boundary", async () => {
    const context = makeApprovalContext();
    const descriptor: CommandDescriptor = { ...testRunCommand(), id: "editor.save_prompt.respond", risk: "destructive", schema: { type: "object", additionalProperties: false } };
    const bridge = new FakeUnityBridgeClient([descriptor]);
    context.bridge = bridge;
    const file = path.join(tempApprovalDir(), "gateway.json");
    const config = JSON.parse(fs.readFileSync(new URL("../config/gateway.example.json", import.meta.url), "utf8"));
    config.approvals.auto_allow_known_unity_popups = true;
    fs.writeFileSync(file, JSON.stringify(config));
    context.knownUnityPopupSettings = new KnownUnityPopupSettingsManager(file);
    const issued = vi.fn();
    const attempt = vi.spyOn(bridge, "invoke").mockImplementation(async (_record, _request, transport) => {
      config.approvals.auto_allow_known_unity_popups = false;
      fs.writeFileSync(file, JSON.stringify(config));
      transport!.beforeRequestIssued!();
      issued();
      transport?.onRequestIssued?.();
      return { status: "ok", data: {} };
    });
    await withInMemoryClient(context, async (client) => {
      const result = await client.callTool({ name: "sgg_unity_call", arguments: { project: "game", command: descriptor.id, args_json: "{}" } });
      expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { command_bridge_request_issued: false, native_dispatch_count: 0 } } });
    });
    expect(attempt).toHaveBeenCalledTimes(1);
    expect(issued).not.toHaveBeenCalled();
  });

  it("exports every protocol version supported by the legacy SDK for health diagnostics", () => {
    expect(LEGACY_MCP_PROTOCOL_VERSIONS).toEqual([...SDK_LEGACY_PROTOCOL_VERSIONS]);
  });

  it("registers exactly the stable public tool surface", async () => {
    await withInMemoryClient({}, async (client) => {
      const tools = (await client.listTools()).tools;
      expect(tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);
      expect(tools.map((tool) => tool.name)).not.toContain("set_active");
      for (const tool of tools) {
        expect(tool.title).toBeTruthy();
        expect(tool.description).toBeTruthy();
        expect(tool.inputSchema).toBeTruthy();
        expect(tool.outputSchema).toBeTruthy();
      }
    });
  });

  it("observes client roots lazily and refreshes them after roots/list_changed", async () => {
    const registry = new ClientRootsRegistry();
    const server = createUnityMcpServer({}, { version: "0.0.0", clientRootsRegistry: registry });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    const client = new Client(
      { name: "roots-client", version: "0.0.0" },
      { capabilities: { roots: { listChanged: true } } },
    );
    let root = "file:///C:/Work/first";
    let blockRequest = false;
    let releaseRequest!: () => void;
    let markRequestStarted!: () => void;
    let requestStarted = new Promise<void>((resolve) => { markRequestStarted = resolve; });
    let requestGate = new Promise<void>((resolve) => { releaseRequest = resolve; });
    client.setRequestHandler(ListRootsRequestSchema, async () => {
      const responseRoot = root;
      if (blockRequest) {
        markRequestStarted();
        await requestGate;
      }
      return { roots: [{ uri: responseRoot }] };
    });
    await server.connect(serverTransport as unknown as Transport);
    await client.connect(clientTransport as unknown as Transport);
    try {
      expect(registry.health().observations).toEqual([]);
      await client.callTool({ name: "sgg_unity_projects", arguments: {} });
      expect(registry.health().observations[0]).toMatchObject({
        status: "observed",
        root_hashes: [clientRootHash("c:/Work/first")],
      });

      root = "file:///C:/Work/second";
      await client.sendRootsListChanged();
      await new Promise((resolve) => setTimeout(resolve, 0));
      expect(registry.health().observations).toEqual([]);
      await client.callTool({ name: "sgg_unity_projects", arguments: {} });
      expect(registry.health().observations[0]).toMatchObject({
        status: "observed",
        root_hashes: [clientRootHash("c:/Work/second")],
      });

      root = "file:///C:/Work/stale";
      await client.sendRootsListChanged();
      blockRequest = true;
      const overlappingCall = client.callTool({ name: "sgg_unity_projects", arguments: {} });
      await requestStarted;
      root = "file:///C:/Work/current";
      await client.sendRootsListChanged();
      releaseRequest();
      await overlappingCall;
      expect(registry.health().observations).toEqual([]);

      blockRequest = false;
      requestStarted = new Promise<void>((resolve) => { markRequestStarted = resolve; });
      requestGate = new Promise<void>((resolve) => { releaseRequest = resolve; });
      await client.callTool({ name: "sgg_unity_projects", arguments: {} });
      expect(registry.health().observations[0]).toMatchObject({
        status: "observed",
        root_hashes: [clientRootHash("c:/Work/current")],
      });
    } finally {
      await client.close().catch(() => {});
      await server.close().catch(() => {});
    }
    expect(registry.health().observations).toEqual([]);
  });

  it("lists the shared GatewayResult output schema for every public tool", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
    });

    try {
      const tools = await listToolsWithSdkClient(running.url);

      expect(tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);
      for (const tool of tools) {
        expect(tool.outputSchema).toMatchObject({
          type: "object",
          properties: {
            status: { type: "string" },
            diagnostics: { type: "array" },
            next_valid_actions: { type: "array" },
            artifact_refs: { type: "array" },
            truncated: { type: "boolean" },
          },
        });
      }
    } finally {
      await running.close();
    }
  });

  it("maps dispatcher success results into MCP tool results", async () => {
    const result = await callToolInMemory({}, "sgg_unity_projects", {});

    expect(result.isError).toBeUndefined();
    expect(result.structuredContent?.status).toBe("ok");
    expect(result.structuredContent).toMatchObject({ status: "ok", data: { projects: [] }, diagnostics: [] });
    expect(textContent(result)).toBe("Gateway result: ok; canonical payload is in structuredContent.");
  });

  it("keeps the canonical payload only in structuredContent and bounds compatibility text", () => {
    const payload = { status: "ok" as const, project: "game", command: "test.run", data: { operation_id: "test-20260722-120000-1234abcd", status: "completed", result: "passed", verbose: "x".repeat(20_000) }, diagnostics: [] };
    const result = gatewayResultToMcpToolResult(payload);

    expect(result.structuredContent).toEqual(payload);
    expect(textContent(result).length).toBeLessThan(320);
    expect(textContent(result)).toContain("canonical payload is in structuredContent");
    expect(textContent(result)).not.toContain("verbose");
    expect(textContent(result)).not.toContain("xxxxx");
  });

  it("maps dispatcher error results into MCP error results", async () => {
    const result = await callToolInMemory({}, "sgg_unity_call", { command: "compile.status" });

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toBeUndefined();
    expect(textContent(result)).toContain("Invalid arguments for tool sgg_unity_call");
  });

  it("uses MCP elicitation to approve and retry one ask-gated call when supported", async () => {
    const context = makeApprovalContext();
    const requests: unknown[] = [];
    const result = await callToolInMemory(context, "sgg_unity_call", {
      project: "game",
      command: "test.run",
      args_json: JSON.stringify({ dry_run: true }),
      request_id: "req-elicitation-1",
    }, async (request) => {
      requests.push(request);
      return { action: "accept", content: { approve: true } };
    });

    expect(result.isError).toBeUndefined();
    expect(result.structuredContent).toMatchObject({ status: "ok", project: "game", command: "test.run" });
    expect(result.structuredContent?.data).toMatchObject({ command: "test.run", args: { dry_run: true } });
    expect(requests).toHaveLength(1);
    expect(requests[0]).toMatchObject({
      method: "elicitation/create",
      params: {
        mode: "form",
        requestedSchema: { properties: { approve: { type: "boolean" } }, required: ["approve"] },
      },
    });
    expect(JSON.stringify(requests[0])).toContain("pending-");
    expect(context.approvalStore?.list()).toHaveLength(0);
  });

  it("keeps server-side test waiting on the consumed native approval generation", async () => {
    const context = makeApprovalContext();
    const result = await callToolInMemory(context, "sgg_unity_call", {
      project: "game",
      command: "test.run",
      args_json: JSON.stringify({ response_mode: "compact" }),
      wait_for_test_run_terminal_ms: 2_000,
    }, async () => ({ action: "accept", content: { approve: true } }));

    expect(result.isError).toBeUndefined();
    expect(result.structuredContent).toMatchObject({
      status: "ok",
      command: "test.run",
      data: { response_mode: "compact", status: "completed", awaited_by_server: true, status_poll_count: 1 },
    });
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual(["test.run", "test.run.status"]);
    expect(context.approvalStore?.list()).toHaveLength(0);
  });

  it("keeps confirmation_required when MCP elicitation is declined", async () => {
    const context = makeApprovalContext();
    const result = await callToolInMemory(context, "sgg_unity_call", {
      project: "game",
      command: "test.run",
      args_json: JSON.stringify({ dry_run: true }),
      request_id: "req-elicitation-2",
    }, async () => ({ action: "decline" }));

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(context.approvalStore?.listPending({ project: "game", now })).toHaveLength(1);
  });

  it.each([
    ["decline", async () => ({ action: "decline" })],
    ["cancel", async () => ({ action: "cancel" })],
    ["approve false", async () => ({ action: "accept", content: { approve: false } })],
  ])("returns the original confirmation deeply unchanged on %s", async (_name, respond) => {
    const context = makeApprovalContext();
    const args = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-nonaccept" };
    const original = await dispatchTool("sgg_unity_call", args, context);
    const result = await callToolInMemory(context, "sgg_unity_call", args, respond);

    expect(result.structuredContent).toEqual(original);
  });

  it.each([
    ["malformed", async () => ({ action: "accept", content: {} })],
    ["SDK failure", async () => { throw new Error("private response token=secret"); }],
  ])("keeps pending identity and adds only the fixed safe marker on %s", async (_name, respond) => {
    const context = makeApprovalContext();
    const args = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-failed-native" };
    const original = await dispatchTool("sgg_unity_call", args, context);
    const result = await callToolInMemory(context, "sgg_unity_call", args, respond);
    const expected = structuredClone(original);
    if (expected.status === "error") {
      expected.error.details = {
        ...expected.error.details,
        native_mcp_elicitation: { last_attempt: { outcome: "failed", diagnostic_code: "mcp_elicitation_failed" } },
      };
    }

    expect(result.structuredContent).toEqual(expected);
    expect(JSON.stringify(result)).not.toContain("private response");
    expect(JSON.stringify(result)).not.toContain("token=secret");
    expect(context.approvalStore?.listPending({ project: "game", now })).toHaveLength(1);
  });

  it("does not request native elicitation when form capability is absent", async () => {
    const context = makeApprovalContext();
    const result = await callToolInMemory(context, "sgg_unity_call", {
      project: "game",
      command: "test.run",
      args_json: "{\"dry_run\":true}",
    });

    expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(result.structuredContent).not.toHaveProperty("error.details.native_mcp_elicitation");
  });

  it("executes the validated argument snapshot when input is mutated during elicitation", async () => {
    const context = makeApprovalContext();
    const args = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-mutation" };

    const result = await callToolInMemory(context, "sgg_unity_call", args, async () => {
      args.args_json = "{\"dry_run\":false}";
      return { action: "accept", content: { approve: true } };
    });

    expect(result.structuredContent?.data).toMatchObject({ args: { dry_run: true } });
  });

  it("allows exactly one of two concurrent native accepters to execute", async () => {
    const context = makeApprovalContext();
    const args = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-race" };
    let release!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    let waiting = 0;
    await withInMemoryClient(context, async (client) => {
      client.setRequestHandler(ElicitRequestSchema, async () => {
      waiting += 1;
      if (waiting === 2) release();
      await gate;
      return { action: "accept", content: { approve: true } };
      });
      const results = await Promise.all([
        client.callTool({ name: "sgg_unity_call", arguments: args }) as Promise<CallToolResult>,
        client.callTool({ name: "sgg_unity_call", arguments: args }) as Promise<CallToolResult>,
      ]);
      expect(results.map((item) => item.structuredContent?.status).sort()).toEqual(["error", "ok"]);
      expect(results.find((item) => item.isError)?.structuredContent).toMatchObject({ error: { code: "confirmation_required" } });
    }, true);
  }, 30_000);

  it("does not consume a replacement pending generation", async () => {
    const context = makeApprovalContext();
    let currentNow = now;
    context.now = () => currentNow;
    const args = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-replaced" };

    const result = await callToolInMemory(context, "sgg_unity_call", args, async () => {
      currentNow = new Date(now.getTime() + 60_001);
      await dispatchTool("sgg_unity_call", args, context);
      return { action: "accept", content: { approve: true } };
    });

    expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(context.approvalStore?.listPending({ now: currentNow })).toHaveLength(1);
  });

  it("invalidates an accepted prompt when the effective ask rule changes and returns a fresh confirmation", async () => {
    const context = makeApprovalContext();
    const args = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-policy-change" };

    const result = await callToolInMemory(context, "sgg_unity_call", args, async () => {
      context.policy = { ...askPolicy(), projects: { game: { allow: [], ask: ["test.*"], deny: [] } } };
      return { action: "accept", content: { approve: true } };
    });

    expect(result.structuredContent).toMatchObject({
      status: "error",
      error: { code: "confirmation_required", details: { approval_request: { invalidation_reason: "accepted_request_context_changed" } } },
    });
    expect(context.approvalStore?.list()).toHaveLength(0);
  });

  it("uses real Streamable HTTP public server elicitation for approval", async () => {
    const context = makeApprovalContext();
    const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
    });
    const client = new Client({ name: "test-client", version: "0.0.0" }, { capabilities: { elicitation: { form: {} } } });
    const transport = new StreamableHTTPClientTransport(new URL(running.url));
    const requests: unknown[] = [];

    client.setRequestHandler(ElicitRequestSchema, async (request) => {
      requests.push(request);
      return { action: "accept", content: { approve: true } };
    });

    try {
      await client.connect(transport as unknown as Transport);
      const result = await client.callTool({
        name: "sgg_unity_call",
        arguments: {
          project: "game",
          command: "test.run",
          args_json: JSON.stringify({ dry_run: true }),
          request_id: "req-http-elicitation-1",
        },
      }) as CallToolResult;

      expect(result.isError).toBeUndefined();
      expect(result.structuredContent).toMatchObject({ status: "ok", project: "game", command: "test.run" });
      expect(requests).toHaveLength(1);
      expect(requests[0]).toMatchObject({ params: { mode: "form", requestedSchema: { properties: { approve: { type: "boolean" } } } } });
      expect(context.approvalStore?.list()).toHaveLength(0);
    } finally {
      await client.close();
      await running.close();
    }
  });

  it.each([
    ["decline", { action: "decline" }],
    ["cancel", { action: "cancel" }],
    ["approve false", { action: "accept", content: { approve: false } }],
  ])("keeps the official stateful HTTP confirmation unchanged on %s", async (_name, response) => {
    const context = makeApprovalContext();
    const args = { project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: `req-http-${_name}` };
    const original = await dispatchTool("sgg_unity_call", args, context);
    const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), { host: "127.0.0.1", port: 0 });
    const client = new Client({ name: "form-client", version: "0.0.0" }, { capabilities: { elicitation: { form: {} } } });
    const transport = new StreamableHTTPClientTransport(new URL(running.url));
    client.setRequestHandler(ElicitRequestSchema, async () => response as never);
    try {
      await client.connect(transport as unknown as Transport);
      const result = await client.callTool({ name: "sgg_unity_call", arguments: args }) as CallToolResult;
      expect(result.structuredContent).toEqual(original);
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("uses official stateless HTTP without elicitation when form capability is absent", async () => {
    const context = makeApprovalContext();
    const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), { host: "127.0.0.1", port: 0 });
    const client = new Client({ name: "no-form-client", version: "0.0.0" });
    const transport = new StreamableHTTPClientTransport(new URL(running.url));
    try {
      await client.connect(transport as unknown as Transport);
      expect(transport.sessionId).toBeUndefined();
      const result = await client.callTool({ name: "sgg_unity_call", arguments: {
        project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-stateless-no-form",
      } }) as CallToolResult;
      expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
      expect(result.structuredContent).not.toHaveProperty("error.details.native_mcp_elicitation");
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("uses a persistent HTTP session when the client advertises roots", async () => {
    const registry = new ClientRootsRegistry();
    const running = await startHttpMcpServer(
      () => createUnityMcpServer({}, { version: "0.0.0", clientRootsRegistry: registry }),
      { host: "127.0.0.1", port: 0 },
    );
    const client = new Client(
      { name: "roots-http-client", version: "0.0.0" },
      { capabilities: { roots: { listChanged: true } } },
    );
    const transport = new StreamableHTTPClientTransport(new URL(running.url));
    client.setRequestHandler(ListRootsRequestSchema, () => ({ roots: [{ uri: "file:///C:/Work/http" }] }));
    try {
      await client.connect(transport as unknown as Transport);
      expect(transport.sessionId).toBeTruthy();
      await client.callTool({ name: "sgg_unity_projects", arguments: {} });
      expect(registry.health().observations[0]).toMatchObject({
        status: "observed",
        root_hashes: [clientRootHash("c:/Work/http")],
      });

      const statelessClient = new Client({ name: "stateless-neighbor", version: "0.0.0" });
      const statelessTransport = new StreamableHTTPClientTransport(new URL(running.url));
      await statelessClient.connect(statelessTransport as unknown as Transport);
      try {
        expect(statelessTransport.sessionId).toBeUndefined();
        await statelessClient.callTool({ name: "sgg_unity_projects", arguments: {} });
        expect(registry.health().observations).toHaveLength(1);
        expect(registry.health().observations[0]).toMatchObject({ status: "observed" });
      } finally {
        await statelessClient.close().catch(() => {});
      }
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("returns a safe failure marker over official stateful HTTP and leaves the pending request usable", async () => {
    const context = makeApprovalContext();
    const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), { host: "127.0.0.1", port: 0 });
    const client = new Client({ name: "failing-form-client", version: "0.0.0" }, { capabilities: { elicitation: { form: {} } } });
    const transport = new StreamableHTTPClientTransport(new URL(running.url));
    client.setRequestHandler(ElicitRequestSchema, async () => { throw new Error("password=hunter2 https://private.example Authorization: secret"); });
    try {
      await client.connect(transport as unknown as Transport);
      const result = await client.callTool({ name: "sgg_unity_call", arguments: {
        project: "game", command: "test.run", args_json: "{\"dry_run\":true}", request_id: "req-http-safe-failure",
      } }) as CallToolResult;
      expect(result.structuredContent).toMatchObject({
        status: "error",
        error: { code: "confirmation_required", details: { native_mcp_elicitation: { last_attempt: {
          outcome: "failed", diagnostic_code: "mcp_elicitation_failed",
        } } } },
      });
      const serialized = JSON.stringify(result);
      expect(serialized).not.toMatch(/hunter2|private\.example|Authorization/);
      expect(context.approvalStore?.listPending({ project: "game", now })).toHaveLength(1);
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it.each([false, true])("registers equivalent stdio shim proxy tools with downstream form capability=%s", async (elicitation) => {
    const calls: Array<{ name: string; arguments?: Record<string, unknown> }> = [];
    const server = createStdioShimServer({
      async callTool(params) {
        calls.push({ name: params.name, ...(params.arguments ? { arguments: params.arguments } : {}) });
        return {
          content: [{ type: "text", text: "remote" }],
          structuredContent: { status: "ok", diagnostics: [] },
        };
      },
    }, { version: "0.0.0" });
    const result = await withServerClient(server, async (client) => {
      expect((await client.listTools()).tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);
      return await client.callTool({ name: "sgg_unity_call", arguments: { project: "game", command: "compile.status", args_json: "{}" } }) as CallToolResult;
    }, elicitation);

    expect(textContent(result)).toBe("remote");
    expect(calls).toEqual([{ name: "sgg_unity_call", arguments: { project: "game", command: "compile.status", args_json: "{}", mode: "wait" } }]);
  });

  it("projects canonical stdio results into JSON text only in explicit compatibility mode", async () => {
    const canonical = {
      status: "error",
      request_id: "req-legacy-text",
      error: { code: "project_not_found", message: "Project not found", details: { request_executed: false } },
      diagnostics: [],
      next_valid_actions: ["call_sgg_unity_projects"],
      artifact_refs: ["artifact://diagnostic"],
      truncated: true,
    };
    const server = createStdioShimServer({
      async callTool() {
        return {
          content: [{ type: "text", text: "remote summary" }],
          structuredContent: canonical,
          isError: true,
        };
      },
    }, { version: "0.0.0", legacyTextResult: true });

    const result = await withServerClient(server, async (client) => await client.callTool({
      name: "sgg_unity_projects",
      arguments: {},
    }) as CallToolResult);

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(canonical);
    expect(JSON.parse(textContent(result))).toEqual(canonical);
  });

  it("gives stdio proxy calls the compile envelope deadline and forwards cancellation", async () => {
    let forwardedOptions: { timeout?: number; maxTotalTimeout?: number; signal?: AbortSignal } | undefined;
    const server = createStdioShimServer({
      async callTool(_params, _schema, options) {
        forwardedOptions = options;
        return {
          content: [{ type: "text", text: "remote" }],
          structuredContent: { status: "ok", diagnostics: [] },
        };
      },
    }, { version: "0.0.0" });

    await withServerClient(server, async (client) => {
      await client.callTool({ name: "sgg_unity_call", arguments: { project: "game", command: "compile.wait_ready", args_json: "{}" } });
    });

    expect(forwardedOptions).toMatchObject({
      timeout: STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS,
      maxTotalTimeout: STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS,
      signal: expect.any(AbortSignal),
    });
    expect(RECOMMENDED_MCP_CLIENT_TIMEOUT_MS).toBeGreaterThan(3_600_000);
    expect(STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS).toBeGreaterThan(RECOMMENDED_MCP_CLIENT_TIMEOUT_MS);
  });

  it("delivers one nonempty stateful response after a delayed compile read crosses heartbeat publication", async () => {
    let releaseRead!: () => void;
    let markReadStarted!: () => void;
    const readStarted = new Promise<void>((resolve) => { markReadStarted = resolve; });
    const readGate = new Promise<void>((resolve) => { releaseRead = resolve; });
    const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-mcp-sdk-delayed-read-"));
    tempDirs.push(instancesDir);
    const initialHeartbeat = heartbeat({ status: "updating", heartbeat_generation: 1 });
    const heartbeatPath = path.join(instancesDir, `${initialHeartbeat.instance_id}.json`);
    fs.writeFileSync(heartbeatPath, JSON.stringify(initialHeartbeat));
    const context: DispatcherContext = {
      instancesDir,
      registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now }),
      bridge: new DelayedCompileWaitBridge(markReadStarted, readGate),
      now: () => now,
    };
    const lines: string[] = [];
    const logger = new OperationalLogger({
      level: "debug",
      writer: { write: (line) => { lines.push(line); return true; }, once: () => undefined },
    });
    const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), { host: "127.0.0.1", port: 0, logger });
    const client = new Client({ name: "delayed-read-client", version: "0.0.0" }, { capabilities: { elicitation: { form: {} } } });
    const transport = new StreamableHTTPClientTransport(new URL(running.url));
    try {
      await client.connect(transport as unknown as Transport);
      const pending = client.callTool({ name: "sgg_unity_call", arguments: {
        project: "game", command: "compile.wait_ready", args_json: "{\"timeout_ms\":120000}",
      } });
      await readStarted;
      fs.writeFileSync(heartbeatPath, JSON.stringify(heartbeat({ status: "updating", heartbeat_generation: 2 })));
      expect(JSON.parse(fs.readFileSync(heartbeatPath, "utf8"))).toMatchObject({ status: "updating", heartbeat_generation: 2 });
      await new Promise((resolve) => setTimeout(resolve, 25));
      releaseRead();

      const result = await pending as CallToolResult;
      expect(result.structuredContent).toMatchObject({ status: "ok", data: { ready: true } });
      const events = lines.map((line) => JSON.parse(line)).filter((event) => event.event_code === "SGG-OP-MCP-HTTP-RESPONSE-001" && event.request_kind === "tools_call");
      expect(events).toEqual([expect.objectContaining({
        state: "finished",
        response_body_write_bytes: expect.any(Number),
        response_body_bytes: expect.any(Number),
      })]);
      expect(events[0].response_body_write_bytes).toBeGreaterThan(0);
      expect(events[0].response_body_bytes).toBe(events[0].response_body_write_bytes);
    } finally {
      releaseRead?.();
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("guards the HTTP MCP endpoint with the configured local token", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      token: "secret",
    });

    try {
      const unauthorized = await fetch(running.url, {
        method: "POST",
        headers: { "content-type": "application/json" },
        body: "{}",
      });
      expect(unauthorized.status).toBe(401);

      const authorized = await fetch(running.url, {
        method: "POST",
        headers: { "content-type": "application/json", "x-sgg-unity-mcp-token": "secret" },
        body: "{}",
      });
      expect(authorized.status).not.toBe(401);
    } finally {
      await running.close();
    }
  });

  it("exposes an authenticated bounded health endpoint", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      token: "secret",
      health: () => ({ version: "0.0.0", projects_count: 0, registry: publicRegistryOperationalStatus(undefined) }),
    });

    try {
      const healthUrl = new URL("/health", running.url);
      const unauthorized = await fetch(healthUrl);
      expect(unauthorized.status).toBe(401);

      const authorized = await fetch(healthUrl, {
        headers: { "x-sgg-unity-mcp-token": "secret" },
      });
      expect(authorized.status).toBe(200);
      expect(await authorized.json()).toEqual({
        ok: true,
        version: "0.0.0",
        projects_count: 0,
        registry: {
          cache_hits: 0,
          cache_misses: 0,
          cache_entries: 0,
          invalid_records: 0,
          invalid_diagnostic_codes: [],
          invalid_diagnostics_truncated: false,
          scan_files: 0,
          scan_overflow: 0,
          scan_truncated: false,
          status: "unavailable",
        },
        http_mcp: {
          active_sessions: 0,
          legacy_sessions_active: 0,
          closing_sessions: 0,
          in_flight_session_requests: 0,
          active_stateless_requests: 0,
          legacy_stateless_requests_active: 0,
          modern_requests_in_flight: 0,
          max_active_sessions: 128,
          expired_sessions_total: 0,
          session_capacity_rejections_total: 0,
          rate_limit_buckets: 0,
          max_rate_limit_buckets: 4096,
          expired_rate_limit_buckets_total: 0,
          rate_limit_capacity_rejections_total: 0,
        },
      });
    } finally {
      await running.close();
    }
  });

  it("keeps authenticated health HTTP 200/ok while reporting degraded policy and refresh recovery", async () => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-health-policy-"));
    tempDirs.push(dir);
    const policyPath = path.join(dir, "policy.json");
    const policy = { schema_version: 1, defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" }, projects: {} } satisfies PolicyConfig;
    fs.writeFileSync(policyPath, JSON.stringify(policy));
    const manager = new PolicyManager(policyPath);
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      token: "secret",
      health: () => {
        manager.reload();
        return { degraded: manager.degraded, policy: manager.snapshot() };
      },
    });

    try {
      const healthUrl = new URL("/health", running.url);
      fs.writeFileSync(`${policyPath}.tmp`, "private invalid policy");
      fs.renameSync(`${policyPath}.tmp`, policyPath);
      const degraded = await fetch(healthUrl, { headers: { "x-sgg-unity-mcp-token": "secret" } });
      expect(degraded.status).toBe(200);
      expect(await degraded.json()).toMatchObject({ ok: true, degraded: true, policy: { state: "degraded", degraded: true, last_error_code: "policy_invalid", last_error_kind: "invalid_json" } });

      fs.writeFileSync(`${policyPath}.tmp`, JSON.stringify(policy));
      fs.renameSync(`${policyPath}.tmp`, policyPath);
      const recovered = await fetch(healthUrl, { headers: { "x-sgg-unity-mcp-token": "secret" } });
      expect(recovered.status).toBe(200);
      expect(await recovered.json()).toMatchObject({ ok: true, degraded: false, policy: { state: "active", degraded: false, last_error_code: null } });
    } finally {
      await running.close();
    }
  });

  it("keeps no-session JSON-RPC compatibility for OpenCode-style clients", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
    });

    try {
      const initialize = await postJson(running.url, initializeRequest(1));
      expect(initialize.status).toBe(200);
      expect(initialize.headers.get("mcp-session-id")).toBeNull();
      expect((await initialize.json()).result).toMatchObject({ protocolVersion: SUPPORTED_MCP_PROTOCOL_VERSION });

      const initialized = await postJson(running.url, { jsonrpc: "2.0", method: "notifications/initialized" });
      expect(initialized.status).toBe(202);

      const list = await postJson(running.url, { jsonrpc: "2.0", id: 2, method: "tools/list" });
      expect(list.status).toBe(200);
      expect(((await list.json()) as { result: { tools: Array<{ name: string }> } }).result.tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);

      const call = await postJson(running.url, { jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "sgg_unity_projects", arguments: {} } });
      expect(call.status).toBe(200);
      expect((await call.json()).result.structuredContent).toMatchObject({ status: "ok", data: { projects: [] } });
    } finally {
      await running.close();
    }
  });

  it("serves modern discover, cached tools, and calls without initialize or sessions", async () => {
    let created = 0;
    const observedMethods: string[] = [];
    let toolsListWireResult: Record<string, unknown> | undefined;
    const baseFactory = createUnityMcpV2ServerFactory({}, { version: "0.0.0" });
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      modernServerFactory: (requestContext) => {
        created += 1;
        return baseFactory(requestContext);
      },
    });
    const captureFetch = async (input: string | URL | Request, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      observedMethods.push(request.headers.get("mcp-method") ?? "missing");
      const response = await fetch(request);
      if (request.headers.get("mcp-method") === "tools/list") {
        const message = await response.clone().json() as { result?: Record<string, unknown> };
        toolsListWireResult = message.result;
      }
      return response;
    };
    const client = new ModernClient(
      { name: "modern-http-client", version: "0.0.0" },
      { versionNegotiation: { mode: "auto" } },
    );
    const transport = new ModernStreamableHTTPClientTransport(new URL(running.url), { fetch: captureFetch });
    try {
      await client.connect(transport);
      const tools = await client.listTools(undefined, { cacheMode: "refresh" });
      const result = await client.callTool({ name: "sgg_unity_projects", arguments: {} }) as ModernCallToolResult;

      expect(client.getNegotiatedProtocolVersion()).toBe(MODERN_MCP_PROTOCOL_VERSION);
      expect(transport.sessionId).toBeUndefined();
      expect(observedMethods).toEqual(["server/discover", "tools/list", "tools/call"]);
      expect(observedMethods).not.toContain("initialize");
      expect(tools.tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);
      expect(toolsListWireResult).toMatchObject({ ttlMs: 300_000, cacheScope: "private" });
      expect(result.structuredContent).toMatchObject({ status: "ok", data: { projects: [] } });
      expect(created).toBe(3);
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("reports modern requests in flight separately from legacy sessions", async () => {
    let release!: () => void;
    let markStarted!: () => void;
    const gate = new Promise<void>((resolve) => { release = resolve; });
    const started = new Promise<void>((resolve) => { markStarted = resolve; });
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      modernServerFactory: () => {
        const server = new ModernMcpServer({ name: "modern-health-test", version: "0.0.0" });
        server.registerTool("wait", {}, async () => {
          markStarted();
          await gate;
          return { content: [{ type: "text", text: "ready" }] };
        });
        return server;
      },
    });
    const client = new ModernClient(
      { name: "modern-health-client", version: "0.0.0" },
      { versionNegotiation: { mode: "auto" } },
    );
    const transport = new ModernStreamableHTTPClientTransport(new URL(running.url));
    try {
      await client.connect(transport);
      const pending = client.callTool({ name: "wait", arguments: {} });
      await started;
      expect((await readHealth(running.url)).http_mcp).toMatchObject({
        modern_requests_in_flight: 1,
        legacy_sessions_active: 0,
        legacy_stateless_requests_active: 0,
      });
      release();
      await pending;
      expect((await readHealth(running.url)).http_mcp.modern_requests_in_flight).toBe(0);
    } finally {
      release?.();
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("streams a modern subscription until the client closes it", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      modernServerFactory: createUnityMcpV2ServerFactory({}, { version: "0.0.0" }),
    });
    const client = new ModernClient(
      { name: "modern-listen-client", version: "0.0.0" },
      { versionNegotiation: { mode: "auto" } },
    );
    const transport = new ModernStreamableHTTPClientTransport(new URL(running.url));
    let subscription: Awaited<ReturnType<ModernClient["listen"]>> | undefined;
    try {
      await client.connect(transport);
      subscription = await client.listen({ toolsListChanged: true }, { timeout: 1_000 });
      expect((await readHealth(running.url)).http_mcp.modern_requests_in_flight).toBe(1);

      await subscription.close();
      subscription = undefined;
      await expect.poll(async () => (await readHealth(running.url)).http_mcp.modern_requests_in_flight).toBe(0);
    } finally {
      await subscription?.close().catch(() => {});
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("keeps modern and legacy direct stdio on their negotiated eras", async () => {
    for (const expected of ["legacy", "modern"] as const) {
      const eras: string[] = [];
      const [clientTransport, serverTransport] = ModernInMemoryTransport.createLinkedPair();
      const baseFactory = createUnityMcpV2ServerFactory({}, { version: "0.0.0" });
      const handle = serveStdio((requestContext) => {
        eras.push(requestContext.era);
        return baseFactory(requestContext);
      }, { transport: serverTransport });
      const client = new ModernClient(
        { name: `${expected}-stdio-client`, version: "0.0.0" },
        expected === "modern" ? { versionNegotiation: { mode: "auto" } } : undefined,
      );
      try {
        await client.connect(clientTransport);
        expect((await client.listTools()).tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);
        expect(client.getNegotiatedProtocolVersion()).toBe(expected === "modern" ? MODERN_MCP_PROTOCOL_VERSION : "2025-11-25");
        expect(eras).toContain(expected);
      } finally {
        await client.close().catch(() => {});
        await handle.close().catch(() => {});
      }
    }
  });

  it("observes Roots only on the legacy direct-stdio era", async () => {
    for (const expected of ["legacy", "modern"] as const) {
      const registry = new ClientRootsRegistry();
      const [clientTransport, serverTransport] = ModernInMemoryTransport.createLinkedPair();
      const factory = createUnityMcpV2ServerFactory({}, { version: "0.0.0", clientRootsRegistry: registry });
      const handle = serveStdio(factory, { transport: serverTransport });
      let rootsRequests = 0;
      const client = new ModernClient(
        { name: `${expected}-roots-client`, version: "0.0.0" },
        {
          capabilities: { roots: { listChanged: true } },
          ...(expected === "modern" ? { versionNegotiation: { mode: "auto" as const } } : {}),
        },
      );
      client.setRequestHandler("roots/list", async () => {
        rootsRequests += 1;
        return { roots: [{ uri: "file:///C:/Work/v2-root" }] };
      });
      try {
        await client.connect(clientTransport);
        await client.callTool({ name: "sgg_unity_projects", arguments: {} });
        expect(rootsRequests).toBe(expected === "legacy" ? 1 : 0);
        expect(registry.health().observations).toHaveLength(expected === "legacy" ? 1 : 0);
      } finally {
        await client.close().catch(() => {});
        await handle.close().catch(() => {});
      }
    }
  });

  it("discards a stale v2 legacy Roots refresh after roots/list_changed", async () => {
    const registry = new ClientRootsRegistry();
    const [clientTransport, serverTransport] = ModernInMemoryTransport.createLinkedPair();
    const handle = serveStdio(createUnityMcpV2ServerFactory({}, { version: "0.0.0", clientRootsRegistry: registry }), { transport: serverTransport });
    const client = new ModernClient(
      { name: "legacy-roots-overlap-client", version: "0.0.0" },
      { capabilities: { roots: { listChanged: true } } },
    );
    let root = "file:///C:/Work/first";
    let blocked = false;
    let release!: () => void;
    let markStarted!: () => void;
    const started = new Promise<void>((resolve) => { markStarted = resolve; });
    const gate = new Promise<void>((resolve) => { release = resolve; });
    client.setRequestHandler("roots/list", async () => {
      const responseRoot = root;
      if (blocked) {
        markStarted();
        await gate;
      }
      return { roots: [{ uri: responseRoot }] };
    });
    try {
      await client.connect(clientTransport);
      await client.callTool({ name: "sgg_unity_projects", arguments: {} });
      expect(registry.health().observations[0]?.root_hashes).toEqual([clientRootHash("c:/Work/first")]);

      root = "file:///C:/Work/stale";
      await client.sendRootsListChanged();
      blocked = true;
      const overlapping = client.callTool({ name: "sgg_unity_projects", arguments: {} });
      await started;
      root = "file:///C:/Work/current";
      await client.sendRootsListChanged();
      release();
      await overlapping;
      expect(registry.health().observations).toEqual([]);

      blocked = false;
      await client.callTool({ name: "sgg_unity_projects", arguments: {} });
      expect(registry.health().observations[0]?.root_hashes).toEqual([clientRootHash("c:/Work/current")]);
    } finally {
      release?.();
      await client.close().catch(() => {});
      await handle.close().catch(() => {});
    }
  });

  it("completes one modern MRTR approval through the existing one-shot dispatcher", async () => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    const requests: unknown[] = [];
    const { result } = await callModernApproval(context, metrics, async (request) => {
      requests.push(request);
      return { action: "accept", content: { approve: true } };
    });

    expect(result.isError).toBeUndefined();
    expect(result.structuredContent).toMatchObject({ status: "ok", project: "game", command: "test.run" });
    expect(requests).toHaveLength(1);
    expect(requests[0]).toMatchObject({ method: "elicitation/create", params: { mode: "form" } });
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual(["test.run"]);
    expect(context.approvalStore?.listPending({ now })).toHaveLength(0);
    expect(metrics).toMatchObject({ mrtrInputRequiredTotal: 1, mrtrCompletedTotal: 1, mrtrDeclinedTotal: 0 });
  });

  it("isolates concurrent capable and non-capable legacy sessions on one dispatcher context", async () => {
    const fixture = await makeStaleCleanupFixture();
    let markPromptStarted!: () => void;
    let releasePrompt!: () => void;
    const promptStarted = new Promise<void>((resolve) => { markPromptStarted = resolve; });
    const promptGate = new Promise<void>((resolve) => { releasePrompt = resolve; });
    const capableCall = callToolInMemory(fixture.context, "sgg_unity_call", approvalCallArgs("req-concurrent-capable"), async () => {
      markPromptStarted();
      await promptGate;
      return { action: "decline" };
    });

    let cleanupResult: CallToolResult;
    try {
      await promptStarted;
      cleanupResult = await callToolInMemory(fixture.context, "sgg_unity_stale_registry_cleanup", fixture.cleanupArgs);
    } finally {
      releasePrompt();
    }
    const capableResult = await capableCall;

    expect(cleanupResult!.structuredContent).toMatchObject({
      status: "ok",
      data: { deleted_count: 1, approval_compatibility: { mode: "temporary_auto_allow_without_native_elicitation" } },
    });
    expect(capableResult.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect((fixture.context.bridge as ApprovalTestRunBridge).commands).toEqual([]);
    expect(fs.existsSync(fixture.deadPath)).toBe(false);
    expect(fs.existsSync(fixture.livePath)).toBe(true);
  });

  it("auto-allows exact stale cleanup for a modern client without form elicitation", async () => {
    const fixture = await makeStaleCleanupFixture();
    const result = await callModernWithoutElicitation(fixture.context, "sgg_unity_stale_registry_cleanup", fixture.cleanupArgs);

    expect(result.structuredContent).toMatchObject({
      status: "ok",
      data: { deleted_count: 1, approval_compatibility: { mode: "temporary_auto_allow_without_native_elicitation" } },
    });
    expect(fs.existsSync(fixture.deadPath)).toBe(false);
    expect(fs.existsSync(fixture.livePath)).toBe(true);
  });

  it("completes modern MRTR approval for an exact stale registry cleanup", async () => {
    const fixture = await makeStaleCleanupFixture();
    const metrics = createMcpV2Metrics();
    const { result } = await callModernApproval(fixture.context, metrics, async () => ({ action: "accept", content: { approve: true } }), {
      toolName: "sgg_unity_stale_registry_cleanup",
      args: fixture.cleanupArgs,
    });

    expect(result.isError).toBeUndefined();
    expect(result.structuredContent).toMatchObject({ status: "ok", data: { deleted_count: 1 } });
    expect(result.structuredContent).not.toHaveProperty("data.approval_compatibility");
    expect(fs.existsSync(fixture.deadPath)).toBe(false);
    expect(fs.existsSync(fixture.livePath)).toBe(true);
    expect(metrics).toMatchObject({ mrtrInputRequiredTotal: 1, mrtrCompletedTotal: 1 });
  });

  it.each(["decline", "cancel"] as const)("finishes modern MRTR %s without executing Unity", async (action) => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    const { result } = await callModernApproval(context, metrics, async () => ({ action }));

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toMatchObject({
      status: "error",
      error: { code: "confirmation_required", details: { mrtr: { outcome: action } } },
    });
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual([]);
    expect(metrics.mrtrDeclinedTotal).toBe(1);
  });

  it("rejects tampered modern requestState before handler re-entry", async () => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    let toolCallRound = 0;
    const tamperFetch = async (input: string | URL | Request, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      if (request.headers.get("mcp-method") === "tools/call" && ++toolCallRound === 2) {
        const body = await request.clone().json() as { params: { requestState: string } };
        body.params.requestState = `${body.params.requestState}tampered`;
        return fetch(new Request(request, { body: JSON.stringify(body) }));
      }
      return fetch(request);
    };

    await expect(callModernApproval(context, metrics, async () => ({ action: "accept", content: { approve: true } }), { fetch: tamperFetch })).rejects.toThrow(
      /Invalid or expired requestState/,
    );
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual([]);
    expect(metrics).toMatchObject({ mrtrInputRequiredTotal: 1, mrtrCompletedTotal: 0, mrtrStateVerificationFailedTotal: 1 });
  });

  it("rejects malformed modern approval content without executing Unity", async () => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    let toolCallRound = 0;
    const malformedFetch = async (input: string | URL | Request, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      if (request.headers.get("mcp-method") === "tools/call" && ++toolCallRound === 2) {
        const body = await request.clone().json() as { params: { inputResponses: { approval: { content: { approve: unknown } } } } };
        body.params.inputResponses.approval.content.approve = "yes";
        return fetch(new Request(request, { body: JSON.stringify(body) }));
      }
      return fetch(request);
    };

    const { result } = await callModernApproval(context, metrics, async () => ({ action: "accept", content: { approve: true } }), { fetch: malformedFetch });
    expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "schema_validation_failed", details: { reason: "malformed_approval_response", write_command_repeated: false } } });
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual([]);
  });

  it("rejects an expired modern pending generation without executing Unity", async () => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    let currentNow = now;
    context.now = () => currentNow;
    const { result } = await callModernApproval(context, metrics, async () => {
      currentNow = new Date(now.getTime() + 60_001);
      return { action: "accept", content: { approve: true } };
    });

    expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "schema_validation_failed", details: { reason: "expired_request_state" } } });
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual([]);
    expect(metrics.mrtrCompletedTotal).toBe(0);
  });

  it("revalidates context changes between modern MRTR rounds", async () => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    const { result } = await callModernApproval(context, metrics, async () => {
      context.policy = { ...askPolicy(), projects: { game: { allow: [], ask: ["test.*"], deny: [] } } };
      return { action: "accept", content: { approve: true } };
    });

    expect(result.structuredContent).toMatchObject({
      status: "error",
      error: { code: "confirmation_required", details: { approval_request: { invalidation_reason: "accepted_request_context_changed" } } },
    });
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual([]);
  });

  it("does not execute again when an accepted modern MRTR round is replayed", async () => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    let retry: { url: string; headers: Record<string, string>; body: string } | undefined;
    let replayMessage: { result: ModernCallToolResult } | undefined;
    let toolCallRound = 0;
    const captureFetch = async (input: string | URL | Request, init?: RequestInit): Promise<Response> => {
      const request = new Request(input, init);
      if (request.headers.get("mcp-method") === "tools/call" && ++toolCallRound === 2) {
        retry = { url: request.url, headers: Object.fromEntries(request.headers), body: await request.clone().text() };
      }
      return fetch(request);
    };

    const { result } = await callModernApproval(context, metrics, async () => ({ action: "accept", content: { approve: true } }), {
      fetch: captureFetch,
      afterCall: async () => {
        expect(retry).toBeDefined();
        const replay = await fetch(retry!.url, {
          method: "POST",
          headers: retry!.headers,
          body: retry!.body,
        });
        replayMessage = await replay.json() as { result: ModernCallToolResult };
      },
    });
    expect(result.structuredContent).toMatchObject({ status: "ok" });
    expect(replayMessage?.result).toMatchObject({ isError: true, structuredContent: { status: "error", error: { code: "confirmation_required" } } });
    expect((context.bridge as ApprovalTestRunBridge).commands).toEqual(["test.run"]);
  });

  it("keeps modern confirmation_required fallback when form capability is absent", async () => {
    const context = makeApprovalContext();
    const metrics = createMcpV2Metrics();
    const modernFactory = createUnityMcpV2ServerFactory(context, { version: "0.0.0", metrics });
    const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      modernServerFactory: modernFactory,
    });
    const client = new ModernClient(
      { name: "modern-no-form", version: "0.0.0" },
      { versionNegotiation: { mode: "auto" } },
    );
    const transport = new ModernStreamableHTTPClientTransport(new URL(running.url));
    try {
      await client.connect(transport);
      const result = await client.callTool({ name: "sgg_unity_call", arguments: approvalCallArgs("req-modern-no-form") }) as ModernCallToolResult;
      expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
      expect(metrics.mrtrInputRequiredTotal).toBe(0);
      expect((context.bridge as ApprovalTestRunBridge).commands).toEqual([]);
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("routes modern header mismatches to the SDK -32020 response", async () => {
    const modernFactory = createUnityMcpV2ServerFactory({}, { version: "0.0.0" });
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      modernServerFactory: modernFactory,
    });
    try {
      const response = await postJson(running.url, modernRequest(1, "tools/list", {}), {
        "mcp-protocol-version": MODERN_MCP_PROTOCOL_VERSION,
        "mcp-method": "tools/call",
      });
      expect(response.status).toBe(400);
      expect(await response.json()).toMatchObject({ error: { code: -32020 } });
    } finally {
      await running.close();
    }
  });

  it("rejects MCP session headers on modern requests", async () => {
    const modernFactory = createUnityMcpV2ServerFactory({}, { version: "0.0.0" });
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      modernServerFactory: modernFactory,
    });
    try {
      const response = await postJson(running.url, modernRequest(1, "tools/list", {}), {
        "mcp-protocol-version": MODERN_MCP_PROTOCOL_VERSION,
        "mcp-method": "tools/list",
        "mcp-session-id": "00000000-0000-4000-8000-000000000000",
      });
      expect(response.status).toBe(400);
      expect(response.headers.get("mcp-session-id")).toBeNull();
      expect(await response.json()).toMatchObject({ error: { code: -32000 } });
    } finally {
      await running.close();
    }
  });

  it("rejects an invalid Host before path, token, or MCP routing", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      token: "secret",
    });
    try {
      const response = await rawRequestWithHost(new URL("/not-mcp", running.url), "evil.example");
      expect(response.status).toBe(403);
      expect(response.body).toMatchObject({ error: { code: -32000 } });
    } finally {
      await running.close();
    }
  });

  it("accepts the concrete local destination Host for a wildcard bind", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "0.0.0.0",
      port: 0,
      allowNonLoopback: true,
      token: "secret",
      allowedOrigins: ["http://allowed.example"],
    });
    try {
      const healthUrl = new URL("/health", running.url);
      healthUrl.hostname = "127.0.0.1";
      const accepted = await rawRequestWithHost(healthUrl, "127.0.0.1", { "x-sgg-unity-mcp-token": "secret" });
      expect(accepted.status).toBe(200);
      const rejected = await rawRequestWithHost(healthUrl, "evil.example", { "x-sgg-unity-mcp-token": "secret" });
      expect(rejected.status).toBe(403);
    } finally {
      await running.close();
    }
  });

  it("uses official stateless tool schema validation and errors", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
    });

    try {
      const invalid = await postJson(running.url, {
        jsonrpc: "2.0",
        id: 1,
        method: "tools/call",
        params: { name: "sgg_unity_call", arguments: { command: "compile.status" } },
      });
      expect(invalid.status).toBe(200);
      const invalidMessage = await invalid.json() as { result: CallToolResult };
      expect(invalidMessage.result.isError).toBe(true);
      expect(textContent(invalidMessage.result)).toContain("Invalid arguments for tool sgg_unity_call");

      const missing = await postJson(running.url, {
        jsonrpc: "2.0",
        id: 2,
        method: "tools/call",
        params: { name: "not_a_tool", arguments: {} },
      });
      expect(missing.status).toBe(200);
      const missingMessage = await missing.json() as { result: CallToolResult };
      expect(missingMessage.result.isError).toBe(true);
      expect(textContent(missingMessage.result)).toBe("MCP error -32602: Tool not_a_tool not found");
    } finally {
      await running.close();
    }
  });

  it("returns a fixed internal JSON-RPC error without exception details", async () => {
    const running = await startHttpMcpServer(() => {
      throw new Error("private-sdk-secret");
    }, { host: "127.0.0.1", port: 0 });

    try {
      const response = await postJson(running.url, initializeRequest(1));
      expect(response.status).toBe(500);
      const body = await response.json();
      expect(body).toEqual({ jsonrpc: "2.0", id: null, error: { code: -32603, message: "Internal server error" } });
      expect(JSON.stringify(body)).not.toContain("private-sdk-secret");
    } finally {
      await running.close();
    }
  });

  it("creates a fresh MCP server for each stateless compatibility request", async () => {
    let created = 0;
    const running = await startHttpMcpServer(() => {
      created += 1;
      return createUnityMcpServer({}, { version: "0.0.0" });
    }, { host: "127.0.0.1", port: 0 });

    try {
      await postJson(running.url, initializeRequest(1));
      await postJson(running.url, { jsonrpc: "2.0", method: "notifications/initialized" });
      await postJson(running.url, { jsonrpc: "2.0", id: 2, method: "tools/list" });
      expect(created).toBe(3);
    } finally {
      await running.close();
    }
  });

  it("allows repeated streamable HTTP client sessions", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
    });

    try {
      await listToolsWithSdkClient(running.url);
      await listToolsWithSdkClient(running.url);
    } finally {
      await running.close();
    }
  });

  it("keeps six-tool schemas, success, and validation envelopes equivalent across official HTTP modes", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), { host: "127.0.0.1", port: 0 });
    const stateless = new Client({ name: "stateless-equivalence", version: "0.0.0" });
    const stateful = new Client({ name: "stateful-equivalence", version: "0.0.0" }, { capabilities: { elicitation: { form: {} } } });
    const statelessTransport = new StreamableHTTPClientTransport(new URL(running.url));
    const statefulTransport = new StreamableHTTPClientTransport(new URL(running.url));
    try {
      await stateless.connect(statelessTransport as unknown as Transport);
      await stateful.connect(statefulTransport as unknown as Transport);
      expect(statelessTransport.sessionId).toBeUndefined();
      expect(statefulTransport.sessionId).toBeTruthy();

      const statelessTools = await stateless.listTools();
      const statefulTools = await stateful.listTools();
      expect(statelessTools.tools).toEqual(statefulTools.tools);
      expect(statelessTools.tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);

      const successParams = { name: "sgg_unity_projects", arguments: {} };
      expect(await stateless.callTool(successParams)).toEqual(await stateful.callTool(successParams));
      const invalidParams = { name: "sgg_unity_call", arguments: { command: "compile.status" } };
      expect(await stateless.callTool(invalidParams)).toEqual(await stateful.callTool(invalidParams));
    } finally {
      await stateless.close().catch(() => {});
      await stateful.close().catch(() => {});
      await running.close();
    }
  });

  it("keeps a persistent HTTP session usable after wrapper validation rejects a call", async () => {
    const context = makeApprovalContext();
    const bridge = context.bridge as ApprovalTestRunBridge;
    const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
    });
    const client = new Client(
      { name: "validation-continuity", version: "0.0.0" },
      { capabilities: { elicitation: { form: {} } } },
    );
    const transport = new StreamableHTTPClientTransport(new URL(running.url));
    try {
      await client.connect(transport as unknown as Transport);
      const sessionId = transport.sessionId;
      expect(sessionId).toBeTruthy();

      const rejected = await client.callTool({
        name: "sgg_unity_call",
        arguments: {
          project: "game",
          command: "compile.refresh_and_wait",
          args_json: "{}",
          wait_for_replacement_ms: 1_000,
        },
      }) as CallToolResult;
      expect(rejected).toMatchObject({
        isError: true,
        structuredContent: { status: "error", error: { code: "invalid_field_value" } },
      });
      expect(bridge.commands).toEqual([]);

      const valid = await client.callTool({ name: "sgg_unity_projects", arguments: {} }) as CallToolResult;
      expect(valid).toMatchObject({
        structuredContent: { status: "ok", data: { counts: { live: 1 } } },
      });
      expect(transport.sessionId).toBe(sessionId);
      expect((await readHealth(running.url)).http_mcp.active_sessions).toBe(1);
      expect(bridge.commands).toEqual([]);
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("hardens missing, unknown, malformed, duplicate, valid, and closed session routing", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), { host: "127.0.0.1", port: 0 });
    try {
      const missing = await fetch(running.url, { method: "DELETE" });
      expect(missing.status).toBe(400);
      expect(await missing.json()).toEqual({ jsonrpc: "2.0", id: null, error: { code: -32000, message: "Bad Request: No valid session ID provided" } });

      const malformed = await postJson(running.url, { jsonrpc: "2.0", id: 1, method: "tools/list" }, { "mcp-session-id": "not-a-session" });
      expect(malformed.status).toBe(400);
      expect(await malformed.json()).toEqual({ jsonrpc: "2.0", id: null, error: { code: -32000, message: "Invalid MCP session header" } });

      const unknown = await postJson(running.url, { jsonrpc: "2.0", id: 2, method: "tools/list" }, { "mcp-session-id": "00000000-0000-4000-8000-000000000000" });
      expect(unknown.status).toBe(404);
      expect(await unknown.json()).toEqual(staleSessionRecoveryError());

      const session = await initializeMcpSession(running.url);
      const duplicate = await rawJsonRequest(running.url, "POST", { jsonrpc: "2.0", id: 3, method: "tools/list" }, [
        ["mcp-session-id", session.sessionId], ["mcp-session-id", session.sessionId],
      ]);
      expect(duplicate.status).toBe(400);
      expect(duplicate.body).toEqual({ jsonrpc: "2.0", id: null, error: { code: -32000, message: "Invalid MCP session header" } });

      const valid = await postJson(running.url, { jsonrpc: "2.0", id: 4, method: "tools/list" }, { "mcp-session-id": session.sessionId });
      expect(valid.status).toBe(200);
      const closed = await fetch(running.url, { method: "DELETE", headers: { "mcp-session-id": session.sessionId } });
      expect(closed.status).toBe(200);
      const reused = await postJson(running.url, { jsonrpc: "2.0", id: 5, method: "tools/list" }, { "mcp-session-id": session.sessionId });
      expect(reused.status).toBe(404);
      expect(await reused.json()).toEqual(staleSessionRecoveryError());
    } finally {
      await running.close();
    }
  });

  it("rejects unsupported MCP protocol headers after initialize", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
    });

    try {
      const session = await initializeMcpSession(running.url);
      expect(session.message.result.protocolVersion).toBe(SUPPORTED_MCP_PROTOCOL_VERSION);

      const unsupported = await postJson(
        running.url,
        { jsonrpc: "2.0", id: 2, method: "tools/list" },
        { "mcp-session-id": session.sessionId, "mcp-protocol-version": "1900-01-01" },
      );
      expect(unsupported.status).toBe(400);
      expect(await unsupported.json()).toEqual({ jsonrpc: "2.0", id: null, error: { code: -32000, message: "Unsupported MCP protocol version" } });

      const duplicate = await rawJsonRequest(running.url, "POST", { jsonrpc: "2.0", id: 3, method: "tools/list" }, [
        ["mcp-session-id", session.sessionId],
        ["mcp-protocol-version", SUPPORTED_MCP_PROTOCOL_VERSION],
        ["mcp-protocol-version", SUPPORTED_MCP_PROTOCOL_VERSION],
      ]);
      expect(duplicate.status).toBe(400);
      expect(duplicate.body).toEqual({ jsonrpc: "2.0", id: null, error: { code: -32000, message: "Invalid MCP protocol version header" } });

      const supported = await postJson(
        running.url,
        { jsonrpc: "2.0", id: 4, method: "tools/list" },
        { "mcp-session-id": session.sessionId, "mcp-protocol-version": SUPPORTED_MCP_PROTOCOL_VERSION },
      );
      expect(supported.status).toBe(200);
    } finally {
      await running.close();
    }
  });

  it("validates Origin when present", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      allowedOrigins: ["http://allowed.example"],
    });

    try {
      const rejected = await postJson(running.url, initializeRequest(1), { origin: "http://evil.example" });
      expect(rejected.status).toBe(403);

      const accepted = await postJson(running.url, initializeRequest(2), { origin: "http://allowed.example" });
      expect(accepted.status).toBe(200);
    } finally {
      await running.close();
    }
  });

  it("bounds MCP request body size", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      maxBodyBytes: 32,
    });

    try {
      const response = await postJson(running.url, { jsonrpc: "2.0", id: 1, method: "tools/list", params: { padding: "too large" } });
      expect(response.status).toBe(413);
    } finally {
      await running.close();
    }
  });

  it("rejects framed non-POST bodies before MCP request conversion", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      maxBodyBytes: 32,
    });
    try {
      const response = await fetch(running.url, { method: "PUT", body: "x".repeat(64) });
      expect(response.status).toBe(400);
      expect(await response.json()).toMatchObject({ error: { code: -32000, message: "Request bodies are only accepted for MCP POST requests" } });
    } finally {
      await running.close();
    }
  });

  it("applies a local MCP request rate limit", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      rateLimit: { windowMs: 60_000, maxRequests: 1 },
    });

    try {
      expect((await postJson(running.url, initializeRequest(1))).status).toBe(200);
      expect((await postJson(running.url, initializeRequest(2))).status).toBe(429);
    } finally {
      await running.close();
    }
  });

  it("refuses unsafe non-loopback binding unless explicitly protected", async () => {
    await expect(startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), { host: "0.0.0.0", port: 0 })).rejects.toThrow(
      /allow_non_loopback/,
    );
    await expect(
      startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
        host: "0.0.0.0",
        port: 0,
        allowNonLoopback: true,
        token: "secret",
      }),
    ).rejects.toThrow(/allowed origins/);
  });

  it("keeps health responsive when diagnostics fail", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      health: () => {
        throw new Error("registry_busy");
      },
    });

    try {
      const healthUrl = new URL("/health", running.url);
      const response = await fetch(healthUrl);
      expect(response.status).toBe(200);
      expect(await response.json()).toMatchObject({ ok: true, health_error: "health_unavailable", http_mcp: { active_sessions: 0, rate_limit_buckets: 0 } });
    } finally {
      await running.close();
    }
  });
});

async function listToolsWithSdkClient(url: string): Promise<Array<{ name: string; outputSchema?: Record<string, unknown> | undefined }>> {
  const client = new Client({ name: "test-client", version: "0.0.0" });
  const transport = new StreamableHTTPClientTransport(new URL(url));
  try {
    await client.connect(transport as unknown as Transport);
    const tools = await client.listTools();
    expect(tools.tools.map((tool) => tool.name)).toEqual([...TOOL_NAMES]);
    return tools.tools;
  } finally {
    await client.close();
  }
}

async function makeStaleCleanupFixture(): Promise<{
  context: DispatcherContext;
  cleanupArgs: Record<string, unknown>;
  deadPath: string;
  livePath: string;
}> {
  const context = makeApprovalContext();
  context.approvalAutoAllowStaleCleanupWithoutElicitation = true;
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-mcp-sdk-cleanup-"));
  tempDirs.push(instancesDir);
  const live = heartbeat();
  const dead = heartbeat({ instance_id: "game-dead", unity_pid: 22222, bridge_port: 18143, last_heartbeat: "2026-05-21T09:00:00.000Z" });
  const livePath = path.join(instancesDir, `${live.instance_id}.json`);
  const deadPath = path.join(instancesDir, `${dead.instance_id}.json`);
  fs.writeFileSync(livePath, JSON.stringify(live));
  fs.writeFileSync(deadPath, JSON.stringify(dead));
  context.registry = new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, cleanupAfterMs: 600_000, processState: (pid) => pid === 22222 ? "dead" : "live", now: () => now });
  const preview = await dispatchTool("sgg_unity_stale_registry_cleanup", {}, context);
  if (preview.status !== "ok") throw new Error("expected stale cleanup preview");
  const previewData = preview.data as { precondition_hash: string; records: Array<{ stale_record_id: string }> };
  return {
    context,
    cleanupArgs: {
      dry_run: false,
      confirm_cleanup: true,
      record_ids: [previewData.records[0]!.stale_record_id],
      precondition_hash: previewData.precondition_hash,
    },
    deadPath,
    livePath,
  };
}

async function callModernWithoutElicitation(
  context: DispatcherContext,
  name: "sgg_unity_call" | "sgg_unity_stale_registry_cleanup",
  args: Record<string, unknown>,
): Promise<ModernCallToolResult> {
  const modernFactory = createUnityMcpV2ServerFactory(context, { version: "0.0.0" });
  const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), {
    host: "127.0.0.1",
    port: 0,
    modernServerFactory: modernFactory,
  });
  const client = new ModernClient(
    { name: "modern-no-form-client", version: "0.0.0" },
    { capabilities: {}, versionNegotiation: { mode: "auto" } },
  );
  const transport = new ModernStreamableHTTPClientTransport(new URL(running.url));
  try {
    await client.connect(transport);
    return await client.callTool({ name, arguments: args }) as ModernCallToolResult;
  } finally {
    await client.close().catch(() => {});
    await running.close();
  }
}

async function callModernApproval(
  context: DispatcherContext,
  metrics: ReturnType<typeof createMcpV2Metrics>,
  respond: (request: unknown) => Promise<{ action: "accept"; content: { approve: boolean } } | { action: "decline" | "cancel" }>,
  options: {
    fetch?: (input: string | URL | Request, init?: RequestInit) => Promise<Response>;
    afterCall?: (result: ModernCallToolResult) => Promise<void>;
    toolName?: "sgg_unity_call" | "sgg_unity_stale_registry_cleanup";
    args?: Record<string, unknown>;
  } = {},
): Promise<{ result: ModernCallToolResult }> {
  const modernFactory = createUnityMcpV2ServerFactory(context, { version: "0.0.0", metrics });
  const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version: "0.0.0" }), {
    host: "127.0.0.1",
    port: 0,
    modernServerFactory: modernFactory,
  });
  const client = new ModernClient(
    { name: "modern-form-client", version: "0.0.0" },
    {
      capabilities: { elicitation: { form: {} } },
      versionNegotiation: { mode: "auto" },
    },
  );
  const transport = new ModernStreamableHTTPClientTransport(new URL(running.url), options.fetch ? { fetch: options.fetch } : undefined);
  client.setRequestHandler("elicitation/create", async (request) => await respond(request));
  try {
    await client.connect(transport);
    const result = await client.callTool({ name: options.toolName ?? "sgg_unity_call", arguments: options.args ?? approvalCallArgs("req-modern-approval") }) as ModernCallToolResult;
    await options.afterCall?.(result);
    return { result };
  } finally {
    await client.close().catch(() => {});
    await running.close();
  }
}

function approvalCallArgs(requestId: string): Record<string, unknown> {
  return {
    project: "game",
    command: "test.run",
    args_json: JSON.stringify({ dry_run: true }),
    request_id: requestId,
  };
}

function modernRequest(id: number, method: string, params: Record<string, unknown>): Record<string, unknown> {
  return {
    jsonrpc: "2.0",
    id,
    method,
    params: {
      ...params,
      _meta: {
        "io.modelcontextprotocol/protocolVersion": MODERN_MCP_PROTOCOL_VERSION,
        "io.modelcontextprotocol/clientInfo": { name: "raw-modern-client", version: "0.0.0" },
        "io.modelcontextprotocol/clientCapabilities": {},
      },
    },
  };
}

describe("MCP-PR-237 mandatory-human guidance across synthetic SDK approval transports", () => {
  it("keeps the exact human fallback without form capability despite allow policy and ask auto-allow", async () => {
    const fixture = makeMandatoryBuildApprovalFixture();

    const result = await callToolInMemory(fixture.context, "sgg_unity_call", fixture.args);

    expect(result.isError).toBe(true);
    expectMandatorySdkGuidance(result.structuredContent, fixture);
    expect(result.structuredContent).not.toHaveProperty("error.details.native_mcp_elicitation");
    expect(fixture.bridge.requests).toEqual([]);
  });

  it("executes one unchanged external-root call only after native acceptance and requires a new approval for repetition", async ({ onTestFailed }) => {
    const started = performance.now();
    const phases: Array<{ stage: string; elapsed_ms: number }> = [];
    let phasesTruncated = false;
    const trace = (stage: string) => {
      if (phases.length < 128) phases.push({ stage, elapsed_ms: Math.round(performance.now() - started) });
      else phasesTruncated = true;
    };
    // Observe real per-fixture dependencies without stubbing lock ownership,
    // durability, binding, validation, authorization or SDK behavior.
    const observe = <T extends object>(target: T, label: string, methods: readonly Extract<keyof T, string>[]): T => new Proxy(target, {
      get(object, property) {
        const value: unknown = Reflect.get(object, property, object);
        if (typeof value !== "function") return value;
        return (...args: unknown[]) => {
          const observed = typeof property === "string" && methods.some((method) => method === property);
          if (observed) trace(`${label}_${String(property)}_start`);
          try { return Reflect.apply(value, object, args); }
          finally { if (observed) trace(`${label}_${String(property)}_complete`); }
        };
      },
    });
    onTestFailed(() => { console.error(JSON.stringify({ event: "pr237_sdk_acceptance_failure_phases", phases, phases_truncated: phasesTruncated })); });
    trace("fixture_start");
    const fixture = makeMandatoryBuildApprovalFixture();
    fixture.context.approvalStore = observe(fixture.context.approvalStore!, "store", ["request", "consume", "listPending", "approvePending", "claimPendingGeneration"]);
    fixture.context.registry = observe(fixture.context.registry!, "registry", ["resolveProject", "captureSnapshot"]);
    trace("initial_pending_start");
    const original = await dispatchTool("sgg_unity_call", fixture.args, fixture.context);
    trace("initial_pending_complete");
    const originalPendingId = expectMandatorySdkGuidance(original, fixture);
    const requests: unknown[] = [];

    const accepted = await callToolInMemory(fixture.context, "sgg_unity_call", fixture.args, async (request) => {
      requests.push(request);
      expect(fixture.bridge.requests).toEqual([]);
      trace("elicitation_accept");
      return { action: "accept", content: { approve: true } };
    }, (stage) => trace(`accepted_${stage}`));
    trace("accepted_call_complete");

    expect(accepted.isError).toBeUndefined();
    expect(accepted.structuredContent).toMatchObject({ status: "ok", project: "game", command: "build.run" });
    expect(requests).toHaveLength(1);
    expect(requests[0]).toMatchObject({ method: "elicitation/create", params: { mode: "form", requestedSchema: { properties: { approve: { type: "boolean" } }, required: ["approve"] } } });
    expect(JSON.stringify(requests[0])).toContain(originalPendingId);
    expect(fixture.bridge.requests).toMatchObject([{ project: "game", command: "build.run", request_id: fixture.args.request_id, risk: "write", args: JSON.parse(fixture.args.args_json) }]);
    expect(fixture.bridge.requests).toHaveLength(1);
    expect(fixture.context.approvalStore!.listPending({ project: "game", now })).toEqual([]);

    const repeat = await callToolInMemory(fixture.context, "sgg_unity_call", fixture.args, undefined, (stage) => trace(`repeat_${stage}`));
    trace("repeat_call_complete");
    // Pending IDs are deterministic for an unchanged binding. A newly pending
    // approval, not a different ID, proves the consumed approval cannot replay.
    expectMandatorySdkGuidance(repeat.structuredContent, fixture);
    expect(fixture.context.approvalStore!.listPending({ project: "game", now })).toHaveLength(1);
    expect(fixture.bridge.requests).toHaveLength(1);
    if (process.env.SGG_MCP_SDK_PHASE_TRACE === "1") console.log(JSON.stringify({ event: "pr237_sdk_acceptance_phases", phases, phases_truncated: phasesTruncated }));
  });

  it.each(["decline", "cancel"] as const)("preserves the mandatory pending confirmation deeply unchanged on native %s", async (action) => {
    const fixture = makeMandatoryBuildApprovalFixture();
    const original = await dispatchTool("sgg_unity_call", fixture.args, fixture.context);
    const pendingId = expectMandatorySdkGuidance(original, fixture);
    const requests: unknown[] = [];

    const result = await callToolInMemory(fixture.context, "sgg_unity_call", fixture.args, async (request) => { requests.push(request); return { action }; });

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(original);
    expect(expectMandatorySdkGuidance(result.structuredContent, fixture)).toBe(pendingId);
    expect(requests).toHaveLength(1);
    expect(fixture.bridge.requests).toEqual([]);
  });

  it("retains human-only guidance and pending identity when native elicitation fails", async () => {
    const fixture = makeMandatoryBuildApprovalFixture();
    const original = await dispatchTool("sgg_unity_call", fixture.args, fixture.context);
    const pendingId = expectMandatorySdkGuidance(original, fixture);

    const result = await callToolInMemory(fixture.context, "sgg_unity_call", fixture.args, async () => { throw new Error("pr237-private-native-ui-failure token=secret"); });

    const expected = structuredClone(original);
    if (expected.status === "error") expected.error.details = { ...expected.error.details, native_mcp_elicitation: { last_attempt: { outcome: "failed", diagnostic_code: "mcp_elicitation_failed" } } };
    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(expected);
    expect(expectMandatorySdkGuidance(result.structuredContent, fixture)).toBe(pendingId);
    expect(JSON.stringify(result)).not.toContain("pr237-private-native-ui-failure");
    expect(JSON.stringify(result)).not.toContain("token=secret");
    expect(fixture.bridge.requests).toEqual([]);
  });

  it("keeps mandatory human fallback on a modern client without form capability", async () => {
    const fixture = makeMandatoryBuildApprovalFixture();
    const metrics = createMcpV2Metrics();
    const modernFactory = createUnityMcpV2ServerFactory(fixture.context, { version: "0.0.0", metrics });
    const running = await startHttpMcpServer(() => createUnityMcpServer(fixture.context, { version: "0.0.0" }), { host: "127.0.0.1", port: 0, modernServerFactory: modernFactory });
    const client = new ModernClient({ name: "pr237-modern-no-form", version: "0.0.0" }, { versionNegotiation: { mode: "auto" } });
    try {
      await client.connect(new ModernStreamableHTTPClientTransport(new URL(running.url)));
      const result = await client.callTool({ name: "sgg_unity_call", arguments: fixture.args }) as ModernCallToolResult;
      expect(result.isError).toBe(true);
      expectMandatorySdkGuidance(result.structuredContent, fixture);
      expect(result.structuredContent).not.toHaveProperty("error.details.native_mcp_elicitation");
      expect(metrics.mrtrInputRequiredTotal).toBe(0);
      expect(fixture.bridge.requests).toEqual([]);
    } finally {
      await client.close().catch(() => {});
      await running.close();
    }
  });

  it("keeps revalidated modern mandatory-human confirmation free of ineffective policy workarounds", async () => {
    const fixture = makeMandatoryBuildApprovalFixture();
    const original = await dispatchTool("sgg_unity_call", fixture.args, fixture.context);
    const originalPendingId = expectMandatorySdkGuidance(original, fixture);
    const metrics = createMcpV2Metrics();
    const requests: unknown[] = [];

    const { result } = await callModernApproval(fixture.context, metrics, async (request) => {
      requests.push(request);
      // The action remains allow, but the exact policy binding changes. The
      // mandatory gate must still require a fresh human-approved generation.
      fixture.context.policy = { ...askPolicy(), projects: { game: { allow: ["build.*"], ask: [], deny: [] } } };
      return { action: "accept", content: { approve: true } };
    }, { args: fixture.args });

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { approval_request: { invalidation_reason: "accepted_request_context_changed" } } } });
    expect(expectMandatorySdkGuidance(result.structuredContent, fixture)).not.toBe(originalPendingId);
    expect(requests).toHaveLength(1);
    expect(fixture.bridge.requests).toEqual([]);
  });
});

function makeMandatoryBuildApprovalFixture() {
  const context = makeApprovalContext();
  const bridge = new MandatoryBuildApprovalBridge();
  context.bridge = bridge;
  context.policy = { ...askPolicy(), projects: { game: { allow: ["build.run"], ask: [], deny: [] } } };
  context.approvalAutoAllowAsk = true;
  context.approvalAutoAllowKnownUnityPopups = false;
  return { context, bridge, args: { project: "game", command: "build.run", args_json: JSON.stringify({ output_root: "C:/builds/game", development: true }), request_id: "pr237-sdk-external-root" } };
}

function expectMandatorySdkGuidance(value: unknown, fixture: ReturnType<typeof makeMandatoryBuildApprovalFixture>): string {
  expect(value).toMatchObject({ status: "error", project: fixture.args.project, command: fixture.args.command, request_id: fixture.args.request_id, error: { code: "confirmation_required" } });
  const result = value as GatewayResult;
  if (result.status !== "error") throw new Error("mandatory human confirmation expected");
  const request = result.error.details?.approval_request as Record<string, unknown>;
  const workflow = result.error.details?.approval_workflow as Record<string, unknown>;
  const pending = fixture.context.approvalStore!.listPending({ project: "game", now }).find((entry) => entry.id === request.pending_id);
  expect(pending).toBeDefined();
  expect(request).toMatchObject({ approval_type: "unity_command", project: "game", command: "build.run", risk: "write", args_hash: argsHash(JSON.parse(fixture.args.args_json) as Record<string, unknown>), request_id: fixture.args.request_id, scope: "single_call_only", pending_id: pending!.id, created_at: pending!.created_at, updated_at: pending!.updated_at, status: "pending", action_hint: pending!.action_hint, expires_at: new Date(now.getTime() + 60_000).toISOString() });
  expect(workflow.policy_options).toEqual(["keep_command_ask_gated_and_approve_one_pending_request"]);
  expect(workflow.contract).toBe("sgg.unity_mcp.approval_workflow.v1");
  expect(workflow.native_mcp_elicitation).toEqual({
    method: "elicitation/create", gateway_status: "implemented_for_clients_that_support_mcp_elicitation",
    opencode_status: "active_connection_capability_and_frontend_rendering_are_authoritative", upstream_opencode_v2_status: "released_2_0_25_forms_implemented_frontend_human_acceptance_requires_validation",
    requires: ["nested_request_capable_transport", "client_capabilities.elicitation.form", "client_approval_ui", "gateway_approval_store"],
    behavior: "When all requirements are met, Gateway asks the MCP client for user approval and retries the exact tool call after acceptance.",
  });
  expect(workflow.fallback).toEqual({ available: true, pending_id: pending!.id, approve_command: pending!.action_hint, retry_original_call_required: true, expires_at: pending!.expires_at });
  expect(workflow.forbidden_actions).toEqual(["do_not_add_or_use_self_approval_mcp_tool", "do_not_consume_or_create_approvals_through_mcp", "do_not_retry_unsafe_command_without_user_confirmation"]);
  expect(JSON.stringify(workflow)).not.toMatch(/bounded_local_policy|auto_allow_ask|auto_allow_known_unity_popups/);
  return pending!.id;
}

class MandatoryBuildApprovalBridge extends FakeUnityBridgeClient {
  readonly requests: BridgeInvokeRequest[] = [];
  constructor() {
    super([{
      id: "build.run", title: "Synthetic external-root build", description: "Approval guidance fixture; no build or filesystem output is produced",
      package: "com.sungeargames.unity-mcp", package_id: "com.sungeargames.unity-mcp", source: "builtin", trust: "builtin",
      risk: "write", idempotency: "unsafe", timeout_ms: 10_000,
      schema: { type: "object", properties: { output_root: { type: "string" }, development: { type: "boolean" }, dry_run: { type: "boolean" } }, additionalProperties: false },
    }]);
  }
  override invoke(record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    this.requests.push(structuredClone(request));
    return super.invoke(record, request);
  }
}

function makeApprovalContext(): DispatcherContext {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-mcp-sdk-instances-"));
  tempDirs.push(instancesDir);
  const record = heartbeat();
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record, null, 2));

  return {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now }),
    bridge: new ApprovalTestRunBridge(),
    policy: askPolicy(),
    approvalStore: new ApprovalStore(tempApprovalDir()),
    approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }),
    approvalDefaultTtlMs: 60_000,
    now: () => now,
  };
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [],
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "abc123",
    last_heartbeat: now.toISOString(),
    ...overrides,
  };
}

function testRunCommand(): CommandDescriptor {
  return {
    id: "test.run",
    title: "Run Unity tests",
    description: "Run Unity tests",
    package: "com.sungeargames.unity-mcp",
    risk: "execute",
    idempotency: "unsafe",
    timeout_ms: 10_000,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
    wrapper_wait: {
      mode: "wait",
      wait_for_test_run_terminal_ms: {
        min_ms: 1,
        max_ms: 295_000,
        deadline_scope: "start_and_terminal_poll",
        response_mode: "compact",
        dry_run: false,
      },
    },
    schema: { type: "object", properties: { dry_run: { type: "boolean" }, response_mode: { type: "string", enum: ["full", "compact"] } }, additionalProperties: false },
    tags: ["test", "automation", "test_run_operation_correlated"],
  };
}

function testRunStatusCommand(): CommandDescriptor {
  return {
    id: "test.run.status",
    title: "Test run status",
    description: "Read Unity test run status",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10_000,
    schema: { type: "object", properties: { operation_id: { type: "string" }, response_mode: { type: "string", enum: ["full", "compact"] } }, required: ["operation_id"], additionalProperties: false },
  };
}

class ApprovalTestRunBridge extends FakeUnityBridgeClient {
  readonly commands: string[] = [];

  constructor() {
    super([testRunCommand(), testRunStatusCommand()]);
  }

  override invoke(record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    this.commands.push(request.command);
    if (request.command === "test.run" && request.args.dry_run !== true) {
      const operationId = String(request.args._test_run_operation_id);
      return Promise.resolve({ status: "ok", data: { operation_id: operationId, run_id: operationId, status: "running" } });
    }
    if (request.command === "test.run.status") {
      return Promise.resolve({ status: "ok", data: { operation_id: request.args.operation_id, run_id: request.args.operation_id, status: "completed", result: "passed", success: true, terminal_counts_authoritative: true, total: 1, passed: 1, failed: 0, skipped: 0, artifact_ready: true, artifact_persisted: true, artifact_ref: `Library/SGGUnityMcp/job-artifacts/test-runs/${String(request.args.operation_id)}.json` } });
    }
    return super.invoke(record, request);
  }
}

function compileWaitReadyCommand(): CommandDescriptor {
  return {
    id: "compile.wait_ready",
    title: "Wait for compile readiness",
    description: "Wait for compile readiness",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 305_000,
    schema: {
      type: "object",
      properties: { timeout_ms: { type: "integer", minimum: 1, maximum: 300_000 } },
      additionalProperties: false,
    },
  };
}

class DelayedCompileWaitBridge extends FakeUnityBridgeClient {
  constructor(readonly markStarted: () => void, readonly gate: Promise<void>) {
    super([compileWaitReadyCommand()]);
  }

  override async invoke(_record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    if (request.command !== "compile.wait_ready") return super.invoke(_record, request);
    this.markStarted();
    await this.gate;
    return {
      status: "ok",
      data: { ready: true, editor_ready: true, compilable: true, timed_out: false, has_compile_errors: false, compile_error_count: 0 },
      diagnostics: [],
      duration_ms: 25,
    };
  }
}

function askPolicy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" },
    projects: { game: { allow: [], ask: ["test.run"], deny: [] } },
  };
}

function tempApprovalDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-mcp-sdk-approvals-"));
  tempDirs.push(dir);
  return dir;
}

function staleSessionRecoveryError(): Record<string, unknown> {
  return {
    jsonrpc: "2.0",
    id: null,
    error: {
      code: -32001,
      message: "MCP session not found. This Gateway process is running, but the supplied session ID is not registered; it may belong to a previous Gateway process after restart or to a closed session. Reconnect this MCP client and send a new initialize request without mcp-session-id before retrying. The original request was not executed.",
      data: {
        code: "mcp_session_not_found",
        session_state: "unknown_or_closed",
        gateway_available: true,
        client_reconnect_required: true,
        new_session_required: true,
        request_executed: false,
        recovery_hint: "Discard the cached mcp-session-id, reconnect to the current Gateway process, and send initialize without a session header. Retry the original request only after initialize succeeds.",
        next_valid_actions: [
          "discard_cached_mcp_session_id",
          "reconnect_mcp_client",
          "send_initialize_without_mcp_session_id",
          "retry_original_request_only_after_initialize_succeeds",
        ],
      },
    },
  };
}

async function postJson(url: string, body: Record<string, unknown>, headers: Record<string, string> = {}): Promise<Response> {
  return await fetch(url, {
    method: "POST",
    headers: { accept: "application/json, text/event-stream", "content-type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
}

async function rawJsonRequest(
  url: string,
  method: string,
  body: Record<string, unknown>,
  headers: Array<[string, string]>,
): Promise<{ status: number; body: unknown }> {
  const target = new URL(url);
  const payload = JSON.stringify(body);
  return await new Promise((resolve, reject) => {
    const request = http.request(target, {
      method,
      headers: [
        "host", target.host,
        "accept", "application/json, text/event-stream",
        "content-type", "application/json",
        "content-length", String(Buffer.byteLength(payload)),
        ...headers.flatMap(([name, value]) => [name, value]),
      ],
    }, (response) => {
      let text = "";
      response.setEncoding("utf8");
      response.on("data", (chunk: string) => { text += chunk; });
      response.on("end", () => resolve({
        status: response.statusCode ?? 0,
        body: text ? JSON.parse(text) as unknown : undefined,
      }));
    });
    request.on("error", reject);
    request.end(payload);
  });
}

async function rawRequestWithHost(url: URL, host: string, headers: Record<string, string> = {}): Promise<{ status: number; body: unknown }> {
  return await new Promise((resolve, reject) => {
    const request = http.request(url, { method: "GET", headers: { ...headers, host } }, (response) => {
      let text = "";
      response.setEncoding("utf8");
      response.on("data", (chunk: string) => { text += chunk; });
      response.on("end", () => resolve({
        status: response.statusCode ?? 0,
        body: text ? JSON.parse(text) as unknown : undefined,
      }));
    });
    request.on("error", reject);
    request.end();
  });
}

async function readHealth(url: string): Promise<{ http_mcp: Record<string, number> }> {
  const response = await fetch(new URL("/health", url));
  expect(response.status).toBe(200);
  return await response.json() as { http_mcp: Record<string, number> };
}

function initializeRequest(id: number, capabilities: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    jsonrpc: "2.0",
    id,
    method: "initialize",
    params: {
      protocolVersion: SUPPORTED_MCP_PROTOCOL_VERSION,
      capabilities,
      clientInfo: { name: "raw-test-client", version: "0.0.0" },
    },
  };
}

async function initializeMcpSession(url: string): Promise<{ sessionId: string; message: { result: { protocolVersion: string } } }> {
  const initialize = await postJson(url, initializeRequest(1, { elicitation: { form: {} } }));
  expect(initialize.status).toBe(200);
  const sessionId = initialize.headers.get("mcp-session-id");
  expect(sessionId).toBeTruthy();
  const message = await readSseJson(initialize) as { result: { protocolVersion: string } };

  const initialized = await postJson(url, { jsonrpc: "2.0", method: "notifications/initialized" }, { "mcp-session-id": sessionId ?? "" });
  expect(initialized.status).toBe(202);
  return { sessionId: sessionId ?? "", message };
}

async function readSseJson(response: Response): Promise<unknown> {
  const text = await response.text();
  const dataLine = text.split(/\r?\n/).find((line) => line.startsWith("data: "));
  if (!dataLine) {
    throw new Error(`Expected SSE data line in response: ${text}`);
  }
  return JSON.parse(dataLine.slice("data: ".length)) as unknown;
}

async function callToolInMemory(
  context: DispatcherContext,
  name: string,
  args: Record<string, unknown>,
  respond?: (request: unknown) => Promise<unknown>,
  trace?: (stage: string) => void,
): Promise<CallToolResult> {
  return await withInMemoryClient(context, async (client) => {
    if (respond) {
      client.setRequestHandler(ElicitRequestSchema, async (request) => await respond(request) as never);
    }
    return await client.callTool({ name, arguments: args }) as CallToolResult;
  }, Boolean(respond), trace);
}

async function withInMemoryClient<T>(
  context: DispatcherContext,
  use: (client: Client) => Promise<T>,
  elicitation = false,
  trace?: (stage: string) => void,
): Promise<T> {
  return await withServerClient(
    createUnityMcpServer(context, { version: "0.0.0" }),
    use,
    elicitation,
    trace,
  );
}

async function withServerClient<T>(
  server: ReturnType<typeof createUnityMcpServer>,
  use: (client: Client) => Promise<T>,
  elicitation = false,
  trace?: (stage: string) => void,
): Promise<T> {
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const client = new Client(
    { name: "in-memory-test-client", version: "0.0.0" },
    elicitation ? { capabilities: { elicitation: { form: {} } } } : undefined,
  );
  trace?.("server_connect_start");
  await server.connect(serverTransport as unknown as Transport);
  trace?.("client_connect_start");
  await client.connect(clientTransport as unknown as Transport);
  trace?.("tool_call_start");
  try {
    return await use(client);
  } finally {
    trace?.("client_close_start");
    await client.close().catch(() => {});
    trace?.("server_close_start");
    await server.close().catch(() => {});
    trace?.("close_complete");
  }
}

function textContent(result: CallToolResult): string {
  const first = result.content[0];
  if (!first || first.type !== "text") {
    throw new Error("Expected text tool content");
  }

  return first.text;
}
