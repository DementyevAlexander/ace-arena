import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";
import { InMemoryTransport } from "@modelcontextprotocol/sdk/inMemory.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { RequestHandlerExtra } from "@modelcontextprotocol/sdk/shared/protocol.js";
import type { Transport, TransportSendOptions } from "@modelcontextprotocol/sdk/shared/transport.js";
import { ElicitRequestSchema, type ClientCapabilities, type ClientNotification, type ClientRequest, type ElicitRequest, type ElicitRequestFormParams, type ElicitResult, type JSONRPCMessage } from "@modelcontextprotocol/sdk/types.js";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ApprovalStore } from "../src/approvals/approval-store.js";
import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { FakeUnityBridgeClient } from "../src/bridge/bridge-client.js";
import type { DispatcherContext } from "../src/mcp/dispatcher.js";
import { startHttpMcpServer } from "../src/mcp/http-server.js";
import { createUnityMcpServer, type UnityMcpToolExtra } from "../src/mcp/sdk-server.js";
import { boundedOpenCodeSessionMeta, OPENCODE_SESSION_META_MAX_BYTES, StdioFormElicitationRelay } from "../src/mcp/stdio-elicitation-relay.js";
import { createHttpStdioShimGeneration, createStdioShimServer, ReconnectableStdioShimClient } from "../src/mcp/stdio-shim.js";
import { ProjectRegistry } from "../src/registry/project-registry.js";

const directories: string[] = [];
afterEach(() => { for (const directory of directories.splice(0)) fs.rmSync(directory, { recursive: true, force: true }); });

describe("stdio form elicitation relay with disposable HTTP/SDK clients", () => {
  it("forwards native form approval through the actual stdio-shim process after outer initialize", async () => {
    const fixture = approvalFixture();
    let prompts = 0;
    let advertised = false;
    const running = await startHttpMcpServer(() => {
      const server = createUnityMcpServer(fixture.context, { version: "0.0.0" });
      server.server.oninitialized = () => { advertised = server.server.getClientCapabilities()?.elicitation?.form !== undefined; };
      return server;
    }, { host: "127.0.0.1", port: 0 });
    const client = new Client({ name: "synthetic-stdio-ui", version: "0.0.0" }, { capabilities: { elicitation: { form: {} } } });
    const transport = new StdioClientTransport({ command: process.execPath, args: ["--import", "tsx", fileURLToPath(new URL("../src/cli.ts", import.meta.url)), "stdio-shim", "--url", running.url, "--legacy-text-result"], cwd: fileURLToPath(new URL("..", import.meta.url)), stderr: "pipe" });
    client.setRequestHandler(ElicitRequestSchema, async (request) => {
      prompts++;
      expect(request.params._meta).toEqual({ "ai.opencode/sessionID": "opaque-subprocess" });
      expect(request.params.message).toContain("req-stdio");
      return { action: "accept", content: { approve: true } };
    });
    try {
      await client.connect(transport);
      expect(advertised).toBe(false); // HTTP must not initialize ahead of the outer client.
      const result = await client.callTool({ name: "sgg_unity_call", arguments: callArgs("stdio"), _meta: { "ai.opencode/sessionID": "opaque-subprocess", "untrusted/extra": "drop" } });
      expect(result.structuredContent).toMatchObject({ status: "ok", request_id: "req-stdio" });
      expect(advertised).toBe(true);
      expect(prompts).toBe(1);
      expect(fixture.invoke).toHaveBeenCalledTimes(1);
    } finally {
      await client.close(); await running.close();
    }
  }, 30_000);

  it.each([false, true])("binds concurrent different calls to the exact outer request with shared UI session=%s", async (sharedSession) => {
    const fixture = approvalFixture();
    const requests: ElicitRequest[] = [];
    const bothArrived = deferred<void>();
    const relatedIds: number[] = [];
    await withRelay(fixture.context, { elicitation: { form: {} } }, async (client) => {
      // Make outer IDs differ from the inner generation's IDs.
      await client.listTools(); await client.listTools(); await client.listTools();
      client.setRequestHandler(ElicitRequestSchema, async (request) => {
        requests.push(request);
        if (requests.length === 2) bothArrived.resolve();
        await bothArrived.promise;
        const session = request.params._meta?.["ai.opencode/sessionID"];
        expect(request.params._meta).toEqual({ "ai.opencode/sessionID": session });
        if (!sharedSession) expect(request.params.message).toContain(session === "opaque-alpha" ? "req-alpha" : "req-beta");
        return { action: "accept", content: { approve: request.params.message.includes("req-alpha") } };
      });
      const [alpha, beta] = await Promise.all([
        client.callTool({ name: "sgg_unity_call", arguments: callArgs("alpha"), _meta: { "ai.opencode/sessionID": "opaque-alpha", "untrusted/extra": "drop-me", progressToken: "do-not-copy" } }),
        client.callTool({ name: "sgg_unity_call", arguments: callArgs("beta"), _meta: { "ai.opencode/sessionID": sharedSession ? "opaque-alpha" : "opaque-beta" } }),
      ]);
      expect(alpha.structuredContent).toMatchObject({ status: "ok", request_id: "req-alpha" });
      expect(beta.structuredContent).toMatchObject({ status: "error", request_id: "req-beta", error: { code: "confirmation_required" } });
      expect(JSON.stringify([alpha, beta])).not.toContain("opaque-");
      expect(JSON.stringify(fixture.context.approvalStore?.listPending({ project: "game", now: new Date("2026-10-10T12:00:00Z") }))).not.toContain("opaque-");
    }, (capabilities) => { expect(capabilities.elicitation).toEqual({ form: {} }); }, (message, options) => {
      if (!("method" in message) || message.method !== "elicitation/create") return;
      const expectedId = String(message.params?.message).includes("req-alpha") ? 4 : 5;
      expect(options?.relatedRequestId).toBe(expectedId);
      relatedIds.push(expectedId);
    });
    expect(requests).toHaveLength(2);
    expect(relatedIds.sort()).toEqual([4, 5]);
    expect(fixture.invoke).toHaveBeenCalledTimes(1);
    expect(fixture.invoke.mock.calls[0]![1]).toMatchObject({ request_id: "req-alpha", args: { label: "alpha" } });
  });

  it.each([
    { action: "decline" as const }, { action: "cancel" as const }, { action: "accept" as const, content: { approve: false } },
  ])("does not execute on synthetic UI response $action/$content", async (response) => {
    const fixture = approvalFixture();
    await withRelay(fixture.context, { elicitation: { form: {} } }, async (client) => {
      client.setRequestHandler(ElicitRequestSchema, async () => response);
      const result = await client.callTool({ name: "sgg_unity_call", arguments: callArgs("denied") });
      expect(result).toMatchObject({ isError: true, structuredContent: { status: "error", error: { code: "confirmation_required" } } });
      expect(result.structuredContent).not.toHaveProperty("error.details.native_mcp_elicitation.last_attempt");
    });
    expect(fixture.invoke).not.toHaveBeenCalled();
  });

  it.each([{}, { elicitation: {} }, { elicitation: { url: {} } }] as ClientCapabilities[])("does not advertise or prompt without explicit outer form capability %#", async (capabilities) => {
    const fixture = approvalFixture();
    await withRelay(fixture.context, capabilities, async (client) => {
      const result = await client.callTool({ name: "sgg_unity_call", arguments: callArgs("no-form") });
      expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { approval_workflow: { fallback: { available: true } } } } });
      expect(result.structuredContent).not.toHaveProperty("error.details.native_mcp_elicitation.last_attempt");
    }, (advertised) => { expect(advertised).not.toHaveProperty("elicitation"); });
    expect(fixture.invoke).not.toHaveBeenCalled();
  });

  it("lets the existing owner-bound dispatcher reject an expired accepted generation", async () => {
    const fixture = approvalFixture();
    await withRelay(fixture.context, { elicitation: { form: {} } }, async (client) => {
      client.setRequestHandler(ElicitRequestSchema, async () => {
        fixture.advance(61_000);
        return { action: "accept", content: { approve: true } };
      });
      const result = await client.callTool({ name: "sgg_unity_call", arguments: callArgs("expired") });
      expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    });
    expect(fixture.invoke).not.toHaveBeenCalled();
  });

  it.each(["cancel", "disconnect"] as const)("propagates outer %s without granting or rerouting approval", async (action) => {
    const fixture = approvalFixture();
    const displayed = deferred<void>();
    await withRelay(fixture.context, { elicitation: { form: {} } }, async (client) => {
      client.setRequestHandler(ElicitRequestSchema, async (_request, extra) => {
        displayed.resolve();
        await new Promise<void>((resolve) => { extra.signal.addEventListener("abort", () => resolve(), { once: true }); });
        return { action: "accept", content: { approve: true } };
      });
      const abort = new AbortController();
      const result = client.callTool({ name: "sgg_unity_call", arguments: callArgs("cancelled") }, undefined, { signal: abort.signal });
      const failure = expect(result).rejects.toThrow();
      await displayed.promise;
      if (action === "disconnect") await client.close();
      else abort.abort();
      await failure;
    });
    expect(fixture.invoke).not.toHaveBeenCalled();
  });

  it("returns explicit native failure guidance for a non-boolean approval value", async () => {
    const fixture = approvalFixture();
    await withRelay(fixture.context, { elicitation: { form: {} } }, async (client) => {
      client.setRequestHandler(ElicitRequestSchema, async () => ({ action: "accept", content: { approve: "true" } }));
      const result = await client.callTool({ name: "sgg_unity_call", arguments: callArgs("malformed") });
      expect(result.structuredContent).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { native_mcp_elicitation: { last_attempt: { outcome: "failed", diagnostic_code: "mcp_elicitation_failed" } } } } });
    });
    expect(fixture.invoke).not.toHaveBeenCalled();
  });

  it("does not replay an accepted write when the inner generation is forcibly replaced", async () => {
    const fixture = approvalFixture();
    let invalidated = false;
    let invalidate!: () => Promise<void>;
    let calls = 0;
    let prompts = 0;
    const running = await startHttpMcpServer(() => createUnityMcpServer(fixture.context, { version: "0.0.0" }), { host: "127.0.0.1", port: 0 });
    let outer!: McpServer;
    const factory = vi.fn(async () => {
      const generation = await createHttpStdioShimGeneration({ url: running.url, version: "0.0.0" }, { current: [] }, () => outer);
      const original = generation.client.callTool;
      generation.client.callTool = async (...args) => {
        calls++;
        if (invalidated) {
          const { StreamableHTTPError } = await import("@modelcontextprotocol/sdk/client/streamableHttp.js");
          throw new StreamableHTTPError(404, `Error POSTing to endpoint: ${JSON.stringify({ error: { code: -32001, data: { code: "mcp_session_not_found", request_executed: false } } })}`);
        }
        return await original(...args);
      };
      invalidate = async () => { await generation.close(true); invalidated = true; };
      invalidated = false;
      return generation;
    });
    const remote = new ReconnectableStdioShimClient(factory);
    outer = createStdioShimServer(remote, { version: "0.0.0" });
    const client = new Client({ name: "synthetic-approval-ui", version: "0.0.0" }, { capabilities: { elicitation: { form: {} } } });
    const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
    try {
      await outer.connect(serverTransport);
      await client.connect(clientTransport);
      client.setRequestHandler(ElicitRequestSchema, async () => { prompts++; return { action: "accept", content: { approve: true } }; });
      expect((await client.callTool({ name: "sgg_unity_call", arguments: callArgs("accepted") })).structuredContent).toMatchObject({ status: "ok" });
      expect(fixture.invoke).toHaveBeenCalledTimes(1);
      await invalidate();
      const stale = await client.callTool({ name: "sgg_unity_projects", arguments: {} });
      expect(stale.structuredContent).toMatchObject({ error: { code: "mcp_session_not_found", details: { automatic_replay_attempted: false } } });
      expect(factory).toHaveBeenCalledTimes(1);
      expect((await client.callTool({ name: "sgg_unity_projects", arguments: {} })).structuredContent).toMatchObject({ status: "ok" });
      expect(factory).toHaveBeenCalledTimes(2);
      expect(fixture.invoke).toHaveBeenCalledTimes(1);
      expect(prompts).toBe(1);
      expect(calls).toBe(3);
    } finally {
      await client.close(); await outer.close(); await remote.close(); await running.close();
    }
  });
});

describe("stdio relay public transport boundary", () => {
  it("forwards the exact SDK-related outer ID, bounded timeout and combined cancellation signal", async () => {
    const ui = fakeOuter();
    const relay = new StdioFormElicitationRelay(() => ui.server, 25);
    const result = await driveBoundPrompt(relay, ui.extra, async () => await relay.forward(formRequest(), innerExtra()));
    expect(result).toEqual({ action: "accept", content: { approve: true } });
    expect(ui.send).toHaveBeenCalledWith(
      { method: "elicitation/create", params: { mode: "form", message: "Synthetic approval", requestedSchema: formRequest().params.requestedSchema, _meta: { "ai.opencode/sessionID": "opaque-ui" } } },
      expect.anything(), { timeout: 25, maxTotalTimeout: 25, signal: expect.any(AbortSignal) },
    );
  });

  it.each(["inner-cancel", "outer-cancel", "transport-close", "transport-error", "graceful-post-eof"] as const)("rejects late acceptance after %s", async (failure) => {
    const ui = fakeOuter();
    const answer = deferred<ElicitResult>();
    const started = deferred<void>();
    ui.send.mockImplementation(async (_request, _schema, options) => {
      started.resolve();
      return await Promise.race([answer.promise, new Promise<never>((_resolve, reject) => { options!.signal!.addEventListener("abort", () => reject(new Error("aborted")), { once: true }); })]);
    });
    const relay = new StdioFormElicitationRelay(() => ui.server);
    const incoming = new AbortController();
    await driveBoundPrompt(relay, ui.extra, async (transport) => {
      const prompt = relay.forward(formRequest(), innerExtra(incoming.signal));
      const rejected = expect(prompt).rejects.toThrow();
      await started.promise;
      if (failure === "inner-cancel") incoming.abort();
      if (failure === "outer-cancel") ui.abort.abort();
      if (failure === "transport-close") await transport.close();
      if (failure === "transport-error") transport.onerror?.(new Error("disconnected"));
      if (failure === "graceful-post-eof") {
        const fetch = relay.wrapFetch(async () => new Response("", { headers: { "content-type": "text/event-stream" } }));
        await (await fetch("http://test.invalid", { method: "POST" })).text();
      }
      answer.resolve({ action: "accept", content: { approve: true } });
      await rejected;
    });
  });

  it("fails closed for unbound GET events and does not repeat a displayed/replayed prompt", async () => {
    const ui = fakeOuter();
    const relay = new StdioFormElicitationRelay(() => ui.server);
    await expect(relay.forward(formRequest(), innerExtra())).rejects.toThrow(/not bound/);
    await driveBoundPrompt(relay, ui.extra, async (transport, emit) => {
      expect(await relay.forward(formRequest(), innerExtra())).toMatchObject({ action: "accept" });
      emit();
      await expect(relay.forward(formRequest(), innerExtra())).rejects.toThrow(/not bound/);
      expect(ui.send).toHaveBeenCalledTimes(1);
      await expect(transport.send({ jsonrpc: "2.0", id: 4, method: "tools/call", params: { name: "sgg_unity_projects" } }, { relatedRequestId: ui.extra.requestId })).rejects.toThrow(/not bound/);
    });
  });

  it("expires a forwarded prompt within its bounded duration without an accepted response", async () => {
    const ui = fakeOuter();
    ui.send.mockImplementation(async (_request, _schema, options) => await new Promise<never>((_resolve, reject) => {
      options!.signal!.addEventListener("abort", () => reject(new Error("timed out")), { once: true });
    }));
    const relay = new StdioFormElicitationRelay(() => ui.server, 20);
    await driveBoundPrompt(relay, ui.extra, async () => {
      await expect(relay.forward(formRequest(), innerExtra())).rejects.toThrow("timed out");
    });
    expect(ui.send).toHaveBeenCalledTimes(1);
  });

  it("fails closed on URL/task/oversize forms, missing related ID, and pre-cancelled signals", async () => {
    const ui = fakeOuter();
    for (const params of [
      { mode: "url" as const, message: "Synthetic URL", url: "https://example.invalid", elicitationId: "url-test" },
      { ...formRequest().params, task: { ttl: 100 } },
      { ...formRequest().params, message: "x".repeat(65 * 1024) },
    ]) {
      const relay = new StdioFormElicitationRelay(() => ui.server);
      await driveBoundPrompt(relay, ui.extra, async () => {
        await expect(relay.forward({ method: "elicitation/create", params }, innerExtra())).rejects.toThrow(/not bound/);
      });
    }
    const relay = new StdioFormElicitationRelay(() => ui.server);
    const wrapped = relay.wrapTransport({ start: async () => {}, close: async () => {}, send: async () => {} });
    await wrapped.start();
    await relay.runCall(ui.extra, async () => {
      await expect(wrapped.send({ jsonrpc: "2.0", id: 8, method: "tools/call", params: { name: "sgg_unity_projects" } })).rejects.toThrow(/not bound/);
    });
    ui.abort.abort();
    const cancelledRelay = new StdioFormElicitationRelay(() => ui.server);
    await driveBoundPrompt(cancelledRelay, ui.extra, async () => {
      await expect(cancelledRelay.forward(formRequest(), innerExtra())).rejects.toThrow();
    });
    expect(ui.send).not.toHaveBeenCalled();
  });

  it("forwards only byte-bounded opaque session metadata and does not invoke getters", () => {
    expect(boundedOpenCodeSessionMeta({ "ai.opencode/sessionID": "opaque", Authorization: "secret", progressToken: 42 })).toEqual({ "ai.opencode/sessionID": "opaque" });
    for (const value of ["", 123, "💥".repeat(OPENCODE_SESSION_META_MAX_BYTES), { sessionID: "not-a-string" }]) expect(boundedOpenCodeSessionMeta({ "ai.opencode/sessionID": value })).toBeUndefined();
    expect(boundedOpenCodeSessionMeta({ get ["ai.opencode/sessionID"]() { throw new Error("private"); } })).toBeUndefined();
  });
});

async function withRelay(context: DispatcherContext, capabilities: ClientCapabilities, use: (client: Client) => Promise<void>, advertised?: (capabilities: ClientCapabilities) => void, observe?: (message: JSONRPCMessage, options?: TransportSendOptions) => void): Promise<void> {
  const running = await startHttpMcpServer(() => {
    const server = createUnityMcpServer(context, { version: "0.0.0" });
    server.server.oninitialized = () => advertised?.(server.server.getClientCapabilities()!);
    return server;
  }, { host: "127.0.0.1", port: 0 });
  let server!: McpServer;
  const remote = new ReconnectableStdioShimClient(async () => await createHttpStdioShimGeneration({ url: running.url, version: "0.0.0" }, { current: [] }, () => server));
  server = createStdioShimServer(remote, { version: "0.0.0", legacyTextResult: true });
  const client = new Client({ name: "synthetic-ui-fixture", version: "0.0.0" }, { capabilities });
  const [clientTransport, serverTransport] = InMemoryTransport.createLinkedPair();
  const send = serverTransport.send.bind(serverTransport);
  serverTransport.send = async (message, options) => { observe?.(message, options); await send(message, options); };
  try {
    await server.connect(serverTransport); await client.connect(clientTransport); await use(client);
  } finally {
    await client.close(); await server.close(); await remote.close(); await running.close();
  }
}

function approvalFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-stdio-relay-"));
  directories.push(root);
  let now = new Date("2026-10-10T12:00:00Z");
  fs.writeFileSync(path.join(root, "game-a.json"), JSON.stringify({ schema_version: 1, instance_id: "game-a", project: "game", display_name: "Game", project_root: "C:/u/game", canonical_project_root: "c:/u/game", aliases: [], unity_version: "6000.0.0f1", unity_pid: 12345, bridge_host: "127.0.0.1", bridge_port: 18142, token: "fixture-only", status: "ready", commands_hash: "abc123", last_heartbeat: now.toISOString() }));
  const bridge = new FakeUnityBridgeClient([{ id: "asset.synthetic_write", title: "Synthetic write", description: "No Unity execution", risk: "write", idempotency: "unsafe", timeout_ms: 10_000, package: "com.sungeargames.unity-mcp", package_id: "com.sungeargames.unity-mcp", source: "builtin", trust: "builtin", schema: { type: "object", properties: { label: { type: "string" } }, required: ["label"], additionalProperties: false } }]);
  const invoke = vi.spyOn(bridge, "invoke");
  const context: DispatcherContext = {
    registry: new ProjectRegistry({ instancesDir: root, staleAfterMs: 90_000, now: () => now }), bridge,
    policy: { schema_version: 1, defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" }, projects: {} },
    approvalStore: new ApprovalStore(path.join(root, "approvals")), approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }), approvalDefaultTtlMs: 60_000, now: () => now,
  };
  return { context, invoke, advance: (ms: number) => { now = new Date(now.getTime() + ms); } };
}

function callArgs(label: string) { return { project: "game", command: "asset.synthetic_write", args_json: JSON.stringify({ label }), request_id: `req-${label}` }; }
function formRequest(): ElicitRequest & { params: ElicitRequestFormParams } { return { method: "elicitation/create", params: { mode: "form", message: "Synthetic approval", requestedSchema: { type: "object", properties: { approve: { type: "boolean" } }, required: ["approve"] }, _meta: { "untrusted/remote": "must-not-forward" } } }; }
function innerExtra(signal = new AbortController().signal): RequestHandlerExtra<ClientRequest, ClientNotification> { return { requestId: "inner-elicitation", signal, sendNotification: async () => {}, sendRequest: async () => { throw new Error("unused"); } }; }
function deferred<T>() { let resolve!: (value: T | PromiseLike<T>) => void; const promise = new Promise<T>((done) => { resolve = done; }); return { promise, resolve }; }

function fakeOuter() {
  const abort = new AbortController();
  const send = vi.fn<UnityMcpToolExtra["sendRequest"]>().mockResolvedValue({ action: "accept", content: { approve: true } });
  const extra: UnityMcpToolExtra = { requestId: "outer-exact-id", signal: abort.signal, _meta: { "ai.opencode/sessionID": "opaque-ui", "untrusted/outer": "drop" }, sendRequest: send, sendNotification: async () => {} };
  const server = { server: { getClientCapabilities: () => ({ elicitation: { form: {} } }) } } as unknown as McpServer;
  return { abort, send, extra, server };
}

async function driveBoundPrompt<T>(relay: StdioFormElicitationRelay, extra: UnityMcpToolExtra, callback: (inner: Transport, emit: () => void) => Promise<T>): Promise<T> {
  const emit = () => inner.onmessage?.({ ...formRequest(), jsonrpc: "2.0", id: "inner-elicitation" } as JSONRPCMessage);
  const inner: Transport = { start: async () => {}, close: async () => { inner.onclose?.(); }, send: async () => { emit(); } };
  const wrapped = relay.wrapTransport(inner);
  await wrapped.start();
  return await relay.runCall(extra, async () => {
    await wrapped.send({ jsonrpc: "2.0", id: 3, method: "tools/call", params: { name: "sgg_unity_projects" } }, { relatedRequestId: extra.requestId });
    return await callback({ ...inner, send: wrapped.send }, emit);
  });
}
