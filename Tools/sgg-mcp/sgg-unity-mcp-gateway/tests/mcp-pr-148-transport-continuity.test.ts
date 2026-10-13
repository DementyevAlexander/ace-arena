import { spawn, type ChildProcess } from "node:child_process";
import { randomInt } from "node:crypto";
import fs from "node:fs";
import http from "node:http";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it } from "vitest";

import {
  FakeUnityBridgeClient,
  type BridgeInvokeRequest,
  type BridgeInvokeResponse,
  type BridgeTransportOptions,
  type CommandDescriptor,
} from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import type { DispatcherContext } from "../src/mcp/dispatcher.js";
import { startHttpMcpServer, SUPPORTED_MCP_PROTOCOL_VERSION } from "../src/mcp/http-server.js";
import { createUnityMcpServer } from "../src/mcp/sdk-server.js";
import {
  createMcpV2Metrics,
  createUnityMcpV2ServerFactory,
  MODERN_MCP_PROTOCOL_VERSION,
} from "../src/mcp/sdk-v2-server.js";
import { InMemoryAuditSink } from "../src/observability/audit.js";
import { OperationalLogger } from "../src/observability/operational-logger.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const now = new Date("2026-08-28T08:00:00.000Z");
const tempDirs: string[] = [];
const childProcesses = new Set<ChildProcess>();
const childGatewayFixture = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "fixtures/transport/child-gateway.ts");

afterEach(async () => {
  await Promise.all([...childProcesses].map(async (child) => await stopChild(child, "SIGKILL")));
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("MCP-PR-148 outer transport continuity", () => {
  it("does not admit an incomplete client request", async () => {
    const fixture = transportFixture();
    let legacyServerCreations = 0;
    let modernServerCreations = 0;
    const modernFactory = createUnityMcpV2ServerFactory(fixture.context, {
      version: "0.0.0",
      metrics: createMcpV2Metrics(),
    });
    const running = await startHttpMcpServer(
      () => {
        legacyServerCreations += 1;
        return createUnityMcpServer(fixture.context, { version: "0.0.0" });
      },
      {
        host: "127.0.0.1",
        port: await reserveTestPort(),
        logger: fixture.logger,
        modernServerFactory: (requestContext) => {
          modernServerCreations += 1;
          return modernFactory(requestContext);
        },
      },
    );
    const initialLegacyCreations = legacyServerCreations;
    const initialModernCreations = modernServerCreations;
    try {
      await sendIncompleteRequest(running.url, modernToolCall(1, "mcp-pr-148-edge-1a"));
      const health = await readHealth(running.url);

      expect(legacyServerCreations).toBe(initialLegacyCreations);
      expect(modernServerCreations).toBe(initialModernCreations);
      expect(health.http_mcp).toMatchObject({
        active_sessions: 0,
        active_stateless_requests: 0,
        modern_requests_in_flight: 0,
      });
      expect(fixture.audit.starts).toEqual([]);
      expect(fixture.audit.events).toEqual([]);
      expect(fixture.bridge.invokeCount).toBe(0);
    } finally {
      fixture.bridge.release();
      await running.close();
    }
  });

  it("does not replay an admitted unsafe command after the modern HTTP client disconnects", async () => {
    const fixture = transportFixture();
    const modernFactory = createUnityMcpV2ServerFactory(fixture.context, {
      version: "0.0.0",
      metrics: createMcpV2Metrics(),
    });
    const running = await startHttpMcpServer(
      () => createUnityMcpServer(fixture.context, { version: "0.0.0" }),
      {
        host: "127.0.0.1",
        port: await reserveTestPort(),
        logger: fixture.logger,
        modernServerFactory: modernFactory,
      },
    );
    const controller = new AbortController();
    try {
      const pending = fetch(running.url, {
        method: "POST",
        headers: modernHeaders(),
        body: JSON.stringify(modernToolCall(2, "mcp-pr-148-edge-1b")),
        signal: controller.signal,
      });
      await within(Promise.race([
        fixture.bridge.started,
        pending.then(async (response) => {
          throw new Error(`Modern tools/call completed before Bridge admission: ${response.status} ${await response.text()}`);
        }),
      ]), "fake Bridge acceptance");
      expect(fixture.bridge.invokeCount).toBe(1);

      controller.abort();
      await within(expect(pending).rejects.toMatchObject({ name: "AbortError" }), "client abort");
      await within(waitFor(() => responseEvents(fixture.lines).some((event) => event.state === "closed")), "closed response telemetry");

      fixture.bridge.release();
      await within(waitFor(() => fixture.audit.events.length === 1), "terminal audit");
      await new Promise<void>((resolve) => setImmediate(resolve));

      expect(fixture.bridge.invokeCount).toBe(1);
      expect(fixture.bridge.requestIds).toEqual(["mcp-pr-148-edge-1b"]);
      expect(fixture.audit.starts).toHaveLength(1);
      expect(fixture.audit.events).toHaveLength(1);
      expect(fixture.audit.events[0]).toMatchObject({
        phase: "terminal",
        run_id: "mcp-pr-148-stage-1",
        status: "ok",
        bridge_invoke_attempts: 1,
        statistics_dispatched: true,
      });
      expect(responseEvents(fixture.lines)).toEqual([expect.objectContaining({
        request_kind: "tools_call",
        state: "closed",
        headers_sent: false,
        response_body_write_bytes: 0,
        response_body_bytes: 0,
      })]);
    } finally {
      fixture.bridge.release();
      await running.close();
    }
  });

  it("rejects stale and unexpected MCP sessions before Bridge dispatch", async () => {
    const fixture = transportFixture();
    const modernFactory = createUnityMcpV2ServerFactory(fixture.context, {
      version: "0.0.0",
      metrics: createMcpV2Metrics(),
    });
    const running = await startHttpMcpServer(
      () => createUnityMcpServer(fixture.context, { version: "0.0.0" }),
      { host: "127.0.0.1", port: await reserveTestPort(), modernServerFactory: modernFactory },
    );
    try {
      const initialized = await postJson(running.url, legacyInitialize(3));
      expect(initialized.status).toBe(200);
      const staleSessionId = initialized.headers.get("mcp-session-id") ?? "";
      expect(staleSessionId).toBeTruthy();
      await initialized.body?.cancel();

      const deleted = await fetch(running.url, {
        method: "DELETE",
        headers: { "mcp-session-id": staleSessionId },
      });
      expect(deleted.status).toBe(200);
      await deleted.body?.cancel();

      const stale = await postJson(running.url, legacyToolCall(4, "mcp-pr-148-edge-2-legacy"), {
        "mcp-session-id": staleSessionId,
      });
      expect(stale.status).toBe(404);
      expect(await stale.json()).toMatchObject({
        error: {
          code: -32001,
          data: {
            code: "mcp_session_not_found",
            request_executed: false,
          },
        },
      });

      const modern = await postJson(running.url, modernToolCall(5, "mcp-pr-148-edge-2-modern"), {
        ...modernHeaders(),
        "mcp-session-id": staleSessionId,
      });
      expect(modern.status).toBe(400);
      expect(await modern.json()).toMatchObject({ error: { code: -32000 } });

      expect(fixture.audit.starts).toEqual([]);
      expect(fixture.audit.events).toEqual([]);
      expect(fixture.bridge.invokeCount).toBe(0);
    } finally {
      fixture.bridge.release();
      await running.close();
    }
  });

  it("does not treat a finished Gateway response as caller receipt through a truncating proxy", async () => {
    const fixture = transportFixture();
    fixture.bridge.release();
    const modernFactory = createUnityMcpV2ServerFactory(fixture.context, {
      version: "0.0.0",
      metrics: createMcpV2Metrics(),
    });
    const running = await startHttpMcpServer(
      () => createUnityMcpServer(fixture.context, { version: "0.0.0" }),
      {
        host: "127.0.0.1",
        port: await reserveTestPort(),
        logger: fixture.logger,
        modernServerFactory: modernFactory,
      },
    );
    const proxy = await startTruncatingProxy(running.url);
    try {
      const clientOutcome = await fetch(proxy.url, {
        method: "POST",
        headers: modernHeaders(),
        body: JSON.stringify(modernToolCall(6, "mcp-pr-148-edge-3")),
      }).then(async (response) => {
        try {
          await response.text();
          return "complete" as const;
        } catch {
          return "truncated" as const;
        }
      }, () => "truncated" as const);
      const proxyReceipt = await within(proxy.upstreamComplete, "complete upstream proxy response");
      await within(waitFor(() => fixture.audit.events.length === 1), "terminal audit");
      await within(waitFor(() => responseEvents(fixture.lines).some((event) => event.state === "finished")), "finished Gateway telemetry");

      expect(clientOutcome).toBe("truncated");
      expect(proxyReceipt.status).toBe(200);
      expect(proxyReceipt.upstreamBytes).toBeGreaterThan(1);
      expect(proxyReceipt.forwardedBytes).toBeGreaterThan(0);
      expect(proxyReceipt.forwardedBytes).toBeLessThan(proxyReceipt.upstreamBytes);
      expect(fixture.bridge.invokeCount).toBe(1);
      expect(fixture.bridge.requestIds).toEqual(["mcp-pr-148-edge-3"]);
      expect(fixture.audit.events[0]).toMatchObject({
        phase: "terminal",
        status: "ok",
        bridge_invoke_attempts: 1,
        statistics_dispatched: true,
      });
      expect(responseEvents(fixture.lines)).toEqual([expect.objectContaining({
        request_kind: "tools_call",
        state: "finished",
        http_status: 200,
        headers_sent: true,
        response_body_write_bytes: expect.any(Number),
        response_body_bytes: expect.any(Number),
      })]);
    } finally {
      await proxy.close();
      await running.close();
    }
  });

  it("drains one admitted invocation before the listener shutdown deadline", async () => {
    const fixture = transportFixture();
    const modernFactory = createUnityMcpV2ServerFactory(fixture.context, {
      version: "0.0.0",
      metrics: createMcpV2Metrics(),
    });
    const running = await startHttpMcpServer(
      () => createUnityMcpServer(fixture.context, { version: "0.0.0" }),
      {
        host: "127.0.0.1",
        port: await reserveTestPort(),
        logger: fixture.logger,
        modernServerFactory: modernFactory,
        shutdownTimeoutMs: 100,
      },
    );
    const pending = postJson(running.url, modernToolCall(7, "mcp-pr-148-edge-4a"), modernHeaders());
    void pending.catch(() => undefined);
    try {
      await within(fixture.bridge.started, "Bridge admission before graceful shutdown");
      const firstClose = running.close();
      const secondClose = running.close();
      fixture.bridge.release();

      await within(Promise.all([firstClose, secondClose]), "graceful listener drain");
      await pending.then(async (response) => await response.body?.cancel(), () => undefined);
      await within(waitFor(() => fixture.audit.events.length === 1), "graceful shutdown terminal audit");

      expect(fixture.bridge.invokeCount).toBe(1);
      expect(fixture.bridge.requestIds).toEqual(["mcp-pr-148-edge-4a"]);
      expect(fixture.audit.events[0]).toMatchObject({
        phase: "terminal",
        status: "ok",
        bridge_invoke_attempts: 1,
        statistics_dispatched: true,
      });
      expect(shutdownEvents(fixture.lines).map((event) => event.state)).toEqual(["started", "completed"]);
    } finally {
      fixture.bridge.release();
      await running.close();
    }
  });

  it("bounds listener shutdown by its deadline without treating it as operation cancellation", async () => {
    const fixture = transportFixture();
    const running = await startHttpMcpServer(
      () => createUnityMcpServer(fixture.context, { version: "0.0.0" }),
      {
        host: "127.0.0.1",
        port: await reserveTestPort(),
        logger: fixture.logger,
        shutdownTimeoutMs: 100,
      },
    );
    try {
      const initialized = await postJson(running.url, legacyInitialize(8));
      expect(initialized.status).toBe(200);
      const sessionId = initialized.headers.get("mcp-session-id") ?? "";
      expect(sessionId).toBeTruthy();
      await initialized.body?.cancel();
      const notification = await postJson(running.url, {
        jsonrpc: "2.0",
        method: "notifications/initialized",
      }, { "mcp-session-id": sessionId });
      expect(notification.status).toBe(202);
      await notification.body?.cancel();

      const pending = postJson(running.url, legacyToolCall(9, "mcp-pr-148-edge-4b"), {
        "mcp-session-id": sessionId,
      });
      void pending.catch(() => undefined);
      await within(fixture.bridge.started, "Bridge admission before bounded shutdown");
      let closed = false;
      const firstClose = running.close().then(() => { closed = true; });
      const secondClose = running.close();

      await within(Promise.all([firstClose, secondClose]), "bounded listener shutdown");
      expect(closed).toBe(true);
      expect(fixture.bridge.invokeCount).toBe(1);
      expect(fixture.audit.events).toEqual([]);

      fixture.bridge.release();
      await pending.then(async (response) => await response.body?.cancel(), () => undefined);
      await within(waitFor(() => fixture.audit.events.length === 1), "post-shutdown terminal audit");
      await within(waitFor(() => responseEvents(fixture.lines).some((event) => event.request_kind === "tools_call" && event.state === "finished")), "bounded shutdown response telemetry");

      expect(fixture.bridge.invokeCount).toBe(1);
      expect(fixture.bridge.requestIds).toEqual(["mcp-pr-148-edge-4b"]);
      expect(fixture.audit.events[0]).toMatchObject({
        phase: "terminal",
        status: "ok",
        bridge_invoke_attempts: 1,
        statistics_dispatched: true,
      });
      expect(responseEvents(fixture.lines).filter((event) => event.request_kind === "tools_call")).toEqual([expect.objectContaining({
        request_kind: "tools_call",
        state: "finished",
        headers_sent: true,
        response_body_write_bytes: 0,
        response_body_bytes: 0,
      })]);
      expect(shutdownEvents(fixture.lines).map((event) => event.state)).toEqual(["started", "completed"]);
    } finally {
      fixture.bridge.release();
      await running.close();
    }
  });

  it("rejects an old session after pre-issuance child Gateway replacement", async () => {
    const fakeBridge = await startExternalReceiptBridge();
    const instancesDir = childInstancesDir(fakeBridge.port);
    try {
      const gatewayA = await startChildGateway(instancesDir, "mcp-pr-148-edge-5a-a", await reserveTestPort(), "block-audit");
      const sessionId = await initializeLegacySession(gatewayA.url, 10);
      const pending = postJson(gatewayA.url, legacyToolCall(11, "mcp-pr-148-edge-5a"), {
        "mcp-session-id": sessionId,
      });
      void pending.catch(() => undefined);
      await gatewayA.waitForStage("audit_prepare");
      expect(fakeBridge.acceptanceCount()).toBe(0);

      const port = Number(new URL(gatewayA.url).port);
      await stopChild(gatewayA.child, "SIGKILL");
      await within(pending.then(() => undefined, () => undefined), "pre-issuance child request termination");

      const gatewayB = await startChildGateway(instancesDir, "mcp-pr-148-edge-5a-b", port);
      const stale = await postJson(gatewayB.url, legacyToolCall(12, "mcp-pr-148-edge-5a-stale"), {
        "mcp-session-id": sessionId,
      });
      expect(stale.status).toBe(404);
      expect(await stale.json()).toMatchObject({
        error: {
          code: -32001,
          data: { code: "mcp_session_not_found", request_executed: false },
        },
      });

      expect(gatewayB.pid).not.toBe(gatewayA.pid);
      expect(gatewayB.processStartUtc).not.toBe(gatewayA.processStartUtc);
      expect(gatewayB.runId).not.toBe(gatewayA.runId);
      expect(fakeBridge.acceptanceCount()).toBe(0);
      await stopChild(gatewayB.child);
    } finally {
      await fakeBridge.close();
    }
  }, 60_000);

  it("keeps one external receipt after post-acceptance child Gateway replacement", async () => {
    const fakeBridge = await startExternalReceiptBridge();
    const instancesDir = childInstancesDir(fakeBridge.port);
    try {
      const gatewayA = await startChildGateway(instancesDir, "mcp-pr-148-edge-5b-a", await reserveTestPort());
      const sessionId = await initializeLegacySession(gatewayA.url, 13);
      const pending = postJson(gatewayA.url, legacyToolCall(14, "mcp-pr-148-edge-5b"), {
        "mcp-session-id": sessionId,
      });
      void pending.catch(() => undefined);
      const receipt = await fakeBridge.accepted;
      expect(receipt.requestId).toBe("mcp-pr-148-edge-5b");
      expect(fakeBridge.acceptanceCount()).toBe(1);

      const port = Number(new URL(gatewayA.url).port);
      await stopChild(gatewayA.child, "SIGKILL");
      await within(pending.then(() => undefined, () => undefined), "post-acceptance child request termination");

      const gatewayB = await startChildGateway(instancesDir, "mcp-pr-148-edge-5b-b", port);
      const stale = await postJson(gatewayB.url, legacyToolCall(15, "mcp-pr-148-edge-5b-stale"), {
        "mcp-session-id": sessionId,
      });
      expect(stale.status).toBe(404);
      expect(await stale.json()).toMatchObject({
        error: {
          code: -32001,
          data: { code: "mcp_session_not_found", request_executed: false },
        },
      });

      expect(gatewayB.pid).not.toBe(gatewayA.pid);
      expect(gatewayB.processStartUtc).not.toBe(gatewayA.processStartUtc);
      expect(gatewayB.runId).not.toBe(gatewayA.runId);
      expect(fakeBridge.acceptanceCount()).toBe(1);
      await stopChild(gatewayB.child);
    } finally {
      await fakeBridge.close();
    }
  }, 60_000);
});

function transportFixture(): {
  context: DispatcherContext;
  bridge: ReceiptBridge;
  audit: InMemoryAuditSink;
  logger: OperationalLogger;
  lines: string[];
} {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-mcp-pr-148-instances-"));
  tempDirs.push(instancesDir);
  const record = heartbeat();
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));

  const bridge = new ReceiptBridge();
  const audit = new InMemoryAuditSink();
  const lines: string[] = [];
  const logger = new OperationalLogger({
    level: "debug",
    writer: { write: (line) => { lines.push(line); return true; }, once: () => undefined },
  });
  return {
    bridge,
    audit,
    logger,
    lines,
    context: {
      registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => now }),
      bridge,
      audit,
      operationalLogger: logger,
      policy: allowUnsafePolicy(),
      now: () => now,
      runId: "mcp-pr-148-stage-1",
    },
  };
}

class ReceiptBridge extends FakeUnityBridgeClient {
  invokeCount = 0;
  readonly requestIds: string[] = [];
  readonly started: Promise<void>;
  readonly #gate: Promise<void>;
  #markStarted!: () => void;
  #release!: () => void;

  constructor() {
    super([unsafeCommand()]);
    this.started = new Promise<void>((resolve) => { this.#markStarted = resolve; });
    this.#gate = new Promise<void>((resolve) => { this.#release = resolve; });
  }

  release(): void {
    this.#release();
  }

  override async invoke(
    _record: HeartbeatRecord,
    request: BridgeInvokeRequest,
    transport?: BridgeTransportOptions,
  ): Promise<BridgeInvokeResponse> {
    this.invokeCount += 1;
    this.requestIds.push(request.request_id ?? "");
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    this.#markStarted();
    await this.#gate;
    return {
      status: "ok",
      data: {
        receipt_id: "mcp-pr-148-fake-receipt",
        bridge_accepted_once: true,
      },
      duration_ms: 1,
    };
  }
}

function unsafeCommand(): CommandDescriptor {
  return {
    id: "transport.execute",
    title: "Transport continuity execute fixture",
    description: "Test-only unsafe command used to prove outer transport continuity",
    package: "com.sungeargames.unity-mcp",
    risk: "execute",
    idempotency: "unsafe",
    timeout_ms: 10_000,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
    schema: { type: "object", properties: {}, additionalProperties: false },
  };
}

function allowUnsafePolicy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: {
      read: "allow",
      write: "allow",
      execute: "allow",
      destructive: "deny",
      network: "deny",
    },
    projects: {},
  };
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "mcp-pr-148-game-a",
    project: "mcp-pr-148-game",
    display_name: "MCP PR 148 Game",
    project_root: "C:/u/mcp-pr-148-game",
    canonical_project_root: "c:/u/mcp-pr-148-game",
    aliases: [],
    unity_version: "6000.5.9f1",
    unity_pid: 48148,
    unity_process_start_utc: "2026-08-28T07:59:00.1234567Z",
    bridge_host: "127.0.0.1",
    bridge_port: 48148,
    token: "fixture-token",
    status: "ready",
    commands_hash: "mcp-pr-148-commands",
    catalog_status: "ready",
    catalog_generation: 1,
    last_heartbeat: now.toISOString(),
    ...overrides,
  };
}

function modernToolCall(id: number, requestId: string): Record<string, unknown> {
  return {
    jsonrpc: "2.0",
    id,
    method: "tools/call",
    params: {
      name: "sgg_unity_call",
      arguments: {
        project: "mcp-pr-148-game",
        command: "transport.execute",
        args_json: "{}",
        mode: "wait",
        request_id: requestId,
      },
      _meta: {
        "io.modelcontextprotocol/protocolVersion": MODERN_MCP_PROTOCOL_VERSION,
        "io.modelcontextprotocol/clientInfo": { name: "mcp-pr-148-test-client", version: "0.0.0" },
        "io.modelcontextprotocol/clientCapabilities": {},
      },
    },
  };
}

function legacyInitialize(id: number): Record<string, unknown> {
  return {
    jsonrpc: "2.0",
    id,
    method: "initialize",
    params: {
      protocolVersion: SUPPORTED_MCP_PROTOCOL_VERSION,
      capabilities: { elicitation: { form: {} } },
      clientInfo: { name: "mcp-pr-148-legacy-client", version: "0.0.0" },
    },
  };
}

function legacyToolCall(id: number, requestId: string): Record<string, unknown> {
  return {
    jsonrpc: "2.0",
    id,
    method: "tools/call",
    params: {
      name: "sgg_unity_call",
      arguments: {
        project: "mcp-pr-148-game",
        command: "transport.execute",
        args_json: "{}",
        mode: "wait",
        request_id: requestId,
      },
    },
  };
}

function modernHeaders(): Record<string, string> {
  return {
    accept: "application/json, text/event-stream",
    "content-type": "application/json",
    "mcp-method": "tools/call",
    "mcp-name": "sgg_unity_call",
    "mcp-protocol-version": MODERN_MCP_PROTOCOL_VERSION,
  };
}

async function sendIncompleteRequest(url: string, body: Record<string, unknown>): Promise<void> {
  const target = new URL(url);
  const payload = JSON.stringify(body);
  const prefix = payload.slice(0, Math.max(1, Math.floor(payload.length / 2)));
  await within(new Promise<void>((resolve, reject) => {
    const socket = net.createConnection({ host: target.hostname, port: Number(target.port) });
    socket.once("connect", () => {
      socket.write([
        `POST ${target.pathname} HTTP/1.1`,
        `Host: ${target.host}`,
        "Accept: application/json, text/event-stream",
        "Content-Type: application/json",
        `Mcp-Method: tools/call`,
        `Mcp-Name: sgg_unity_call`,
        `Mcp-Protocol-Version: ${MODERN_MCP_PROTOCOL_VERSION}`,
        `Content-Length: ${Buffer.byteLength(payload)}`,
        "Connection: close",
        "",
        prefix,
      ].join("\r\n"), () => socket.destroy());
    });
    socket.once("close", () => resolve());
    socket.once("error", (error) => {
      if ((error as NodeJS.ErrnoException).code === "ECONNRESET") resolve();
      else reject(error);
    });
  }), "incomplete request socket close");
}

function postJson(url: string, body: Record<string, unknown>, headers: Record<string, string> = {}): Promise<Response> {
  return fetch(url, {
    method: "POST",
    headers: { accept: "application/json, text/event-stream", "content-type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
}

async function startTruncatingProxy(upstreamUrl: string): Promise<{
  url: string;
  upstreamComplete: Promise<{ status: number; upstreamBytes: number; forwardedBytes: number }>;
  close: () => Promise<void>;
}> {
  const upstream = new URL(upstreamUrl);
  let resolveUpstream!: (value: { status: number; upstreamBytes: number; forwardedBytes: number }) => void;
  let rejectUpstream!: (error: unknown) => void;
  const upstreamComplete = new Promise<{ status: number; upstreamBytes: number; forwardedBytes: number }>((resolve, reject) => {
    resolveUpstream = resolve;
    rejectUpstream = reject;
  });
  const server = http.createServer((request, response) => {
    const requestChunks: Buffer[] = [];
    request.on("data", (chunk: Buffer) => requestChunks.push(chunk));
    request.once("error", rejectUpstream);
    request.once("end", () => {
      const headers = { ...request.headers, host: upstream.host };
      const upstreamRequest = http.request(upstream, { method: request.method, headers }, (upstreamResponse) => {
        const responseChunks: Buffer[] = [];
        upstreamResponse.on("data", (chunk: Buffer) => responseChunks.push(chunk));
        upstreamResponse.once("error", rejectUpstream);
        upstreamResponse.once("end", () => {
          const body = Buffer.concat(responseChunks);
          const forwardedBytes = Math.max(1, Math.floor(body.byteLength / 2));
          const responseHeaders = { ...upstreamResponse.headers, "content-length": String(body.byteLength) };
          delete responseHeaders["transfer-encoding"];
          response.writeHead(upstreamResponse.statusCode ?? 500, responseHeaders);
          response.write(body.subarray(0, forwardedBytes), () => response.destroy());
          resolveUpstream({
            status: upstreamResponse.statusCode ?? 0,
            upstreamBytes: body.byteLength,
            forwardedBytes,
          });
        });
      });
      upstreamRequest.once("error", rejectUpstream);
      upstreamRequest.end(Buffer.concat(requestChunks));
    });
  });
  const proxyPort = await reserveTestPort();
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(proxyPort, "127.0.0.1", () => resolve());
  });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("Truncating proxy did not bind a TCP port");
  return {
    url: `http://127.0.0.1:${address.port}${upstream.pathname}`,
    upstreamComplete,
    close: async () => {
      server.closeAllConnections();
      if (!server.listening) return;
      await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
    },
  };
}

function childInstancesDir(bridgePort: number): string {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-mcp-pr-148-child-instances-"));
  tempDirs.push(instancesDir);
  const record = heartbeat({
    bridge_port: bridgePort,
    last_heartbeat: new Date().toISOString(),
  });
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));
  return instancesDir;
}

async function initializeLegacySession(url: string, id: number): Promise<string> {
  const initialized = await postJson(url, legacyInitialize(id));
  expect(initialized.status).toBe(200);
  const sessionId = initialized.headers.get("mcp-session-id") ?? "";
  expect(sessionId).toBeTruthy();
  await initialized.body?.cancel();
  const notification = await postJson(url, {
    jsonrpc: "2.0",
    method: "notifications/initialized",
  }, { "mcp-session-id": sessionId });
  expect(notification.status).toBe(202);
  await notification.body?.cancel();
  return sessionId;
}

async function startExternalReceiptBridge(): Promise<{
  port: number;
  accepted: Promise<{ requestId: string }>;
  acceptanceCount: () => number;
  close: () => Promise<void>;
}> {
  let acceptanceCount = 0;
  let resolveAccepted!: (value: { requestId: string }) => void;
  const accepted = new Promise<{ requestId: string }>((resolve) => { resolveAccepted = resolve; });
  const server = http.createServer((request, response) => {
    if (request.method === "GET" && request.url === "/commands") {
      writeJsonResponse(response, 200, {
        schema_version: 1,
        instance_id: "mcp-pr-148-game-a",
        project: "mcp-pr-148-game",
        commands_hash: "mcp-pr-148-commands",
        catalog_generation: 1,
        commands: [unsafeCommand()],
      });
      return;
    }
    if (request.method === "GET" && request.url === "/health") {
      writeJsonResponse(response, 200, {
        schema_version: 1,
        instance_id: "mcp-pr-148-game-a",
        project: "mcp-pr-148-game",
        status: "ready",
        unity_version: "6000.5.9f1",
        commands_hash: "mcp-pr-148-commands",
        is_compiling: false,
        is_updating: false,
        is_playing: false,
        ready: true,
        package_update: { status: "current", refresh_pending: false, package_refresh_required: false, stale_package_resolution: false },
        has_unsaved_scene_changes: false,
        dirty_scene_count: 0,
        dirty_scenes: [],
        queue_depth: 0,
      });
      return;
    }
    if (request.method === "POST" && request.url === "/invoke") {
      const chunks: Buffer[] = [];
      request.on("data", (chunk: Buffer) => chunks.push(chunk));
      request.once("end", () => {
        const body = JSON.parse(Buffer.concat(chunks).toString("utf8")) as { request_id?: unknown };
        acceptanceCount += 1;
        resolveAccepted({ requestId: typeof body.request_id === "string" ? body.request_id : "" });
      });
      return;
    }
    writeJsonResponse(response, 404, { error: "not_found" });
  });
  const bridgePort = await reserveTestPort();
  await new Promise<void>((resolve, reject) => {
    server.once("error", reject);
    server.listen(bridgePort, "127.0.0.1", () => resolve());
  });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("External receipt Bridge did not bind a TCP port");
  return {
    port: address.port,
    accepted,
    acceptanceCount: () => acceptanceCount,
    close: async () => {
      server.closeAllConnections();
      if (!server.listening) return;
      await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
    },
  };
}

function writeJsonResponse(response: http.ServerResponse, status: number, body: unknown): void {
  const payload = JSON.stringify(body);
  response.writeHead(status, {
    "content-type": "application/json",
    "content-length": String(Buffer.byteLength(payload)),
  });
  response.end(payload);
}

type ChildGateway = {
  child: ChildProcess;
  url: string;
  runId: string;
  pid: number;
  processStartUtc: string;
  waitForStage: (stage: string) => Promise<Record<string, unknown>>;
};

async function startChildGateway(
  instancesDir: string,
  runId: string,
  port: number,
  mode = "normal",
): Promise<ChildGateway> {
  const child = spawn(process.execPath, ["--import", "tsx", childGatewayFixture, instancesDir, runId, String(port), mode], {
    cwd: process.cwd(),
    stdio: ["ignore", "pipe", "pipe"],
  });
  childProcesses.add(child);
  child.once("exit", () => childProcesses.delete(child));
  const messages: Array<Record<string, unknown>> = [];
  let stdout = "";
  let stderr = "";
  child.stdout?.on("data", (chunk) => {
    stdout += String(chunk);
    for (;;) {
      const newline = stdout.indexOf("\n");
      if (newline < 0) break;
      const line = stdout.slice(0, newline).trim();
      stdout = stdout.slice(newline + 1);
      if (line) messages.push(JSON.parse(line) as Record<string, unknown>);
    }
  });
  child.stderr?.on("data", (chunk) => { stderr += String(chunk); });
  const waitForStage = async (stage: string): Promise<Record<string, unknown>> => {
    const deadline = Date.now() + 45_000;
    while (Date.now() < deadline) {
      const message = messages.find((candidate) => candidate.stage === stage);
      if (message) return message;
      if (child.exitCode !== null || child.signalCode !== null) {
        throw new Error(`Child Gateway exited before ${stage}: ${stderr || child.exitCode || child.signalCode}`);
      }
      await new Promise<void>((resolve) => setTimeout(resolve, 10));
    }
    throw new Error(`Timed out waiting for child Gateway ${stage}: ${stderr}`);
  };
  const ready = await waitForStage("ready");
  if (typeof ready.url !== "string" || typeof ready.pid !== "number" || typeof ready.process_start_utc !== "string") {
    throw new Error("Child Gateway returned malformed ready evidence");
  }
  return {
    child,
    url: ready.url,
    runId,
    pid: ready.pid,
    processStartUtc: ready.process_start_utc,
    waitForStage,
  };
}

async function stopChild(child: ChildProcess, signal: NodeJS.Signals = "SIGTERM"): Promise<void> {
  if (child.exitCode !== null || child.signalCode !== null) {
    childProcesses.delete(child);
    return;
  }
  child.kill(signal);
  const exited = await Promise.race([
    new Promise<true>((resolve) => child.once("exit", () => resolve(true))),
    new Promise<false>((resolve) => setTimeout(() => resolve(false), 5_000)),
  ]);
  if (!exited) {
    if (signal !== "SIGKILL") child.kill("SIGKILL");
    const forcedExit = await Promise.race([
      new Promise<true>((resolve) => child.once("exit", () => resolve(true))),
      new Promise<false>((resolve) => setTimeout(() => resolve(false), 5_000)),
    ]);
    if (!forcedExit) throw new Error(`Child process ${String(child.pid)} did not exit after ${signal}`);
  }
  childProcesses.delete(child);
}

async function reserveTestPort(): Promise<number> {
  for (let attempt = 0; attempt < 10; attempt += 1) {
    const port = randomInt(30_000, 60_000);
    const server = net.createServer();
    const reserved = await new Promise<boolean>((resolve, reject) => {
      server.once("error", (error: NodeJS.ErrnoException) => {
        if (error.code === "EADDRINUSE") resolve(false);
        else reject(error);
      });
      server.listen(port, "127.0.0.1", () => resolve(true));
    });
    if (!reserved) continue;
    await new Promise<void>((resolve, reject) => server.close((error) => error ? reject(error) : resolve()));
    return port;
  }
  throw new Error("Unable to reserve a non-production test port");
}

async function readHealth(url: string): Promise<{ http_mcp: Record<string, number> }> {
  const response = await fetch(new URL("/health", url));
  expect(response.status).toBe(200);
  return await response.json() as { http_mcp: Record<string, number> };
}

function responseEvents(lines: string[]): Array<Record<string, unknown>> {
  return lines
    .map((line) => JSON.parse(line) as Record<string, unknown>)
    .filter((event) => event.event_code === "SGG-OP-MCP-HTTP-RESPONSE-001");
}

function shutdownEvents(lines: string[]): Array<Record<string, unknown>> {
  return lines
    .map((line) => JSON.parse(line) as Record<string, unknown>)
    .filter((event) => event.event_code === "SGG-OP-HTTP-SHUTDOWN-001");
}

async function waitFor(predicate: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 500; attempt += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 1));
  }
  throw new Error("Timed out waiting for test condition");
}

async function within<T>(promise: Promise<T>, label: string): Promise<T> {
  return await Promise.race([
    promise,
    new Promise<never>((_, reject) => setTimeout(() => reject(new Error(`Timed out waiting for ${label}`)), 2_000)),
  ]);
}
