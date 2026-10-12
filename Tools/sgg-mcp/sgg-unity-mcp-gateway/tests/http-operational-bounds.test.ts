import http from "node:http";

import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { afterEach, describe, expect, it, vi } from "vitest";

import { type HttpMcpClock, type HttpMcpTimer, startHttpMcpServer, SUPPORTED_MCP_PROTOCOL_VERSION } from "../src/mcp/http-server.js";
import { canonicalizeIpAddress } from "../src/mcp/ip-address.js";
import { createUnityMcpServer } from "../src/mcp/sdk-server.js";
import { OperationalLogger } from "../src/observability/operational-logger.js";

afterEach(() => vi.restoreAllMocks());

describe("HTTP MCP operational bounds", () => {
  it("records nonempty completed tools/call HTTP response bytes without response content", async () => {
    const lines: string[] = [];
    const logger = new OperationalLogger({
      level: "debug",
      writer: { write: (line) => { lines.push(line); return true; }, once: () => undefined },
    });
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      logger,
    });
    try {
      const response = await post(running.url, {
        jsonrpc: "2.0",
        id: 1,
        method: "tools/call",
        params: { name: "sgg_unity_projects", arguments: {} },
      });
      const body = await response.text();

      expect(response.status).toBe(200);
      expect(Buffer.byteLength(body)).toBeGreaterThan(0);
      const events = lines.map((line) => JSON.parse(line)).filter((event) => event.event_code === "SGG-OP-MCP-HTTP-RESPONSE-001");
      expect(events).toEqual([expect.objectContaining({
        request_kind: "tools_call",
        state: "finished",
        http_status: 200,
        headers_sent: true,
        content_length_matches: true,
        response_body_write_bytes: Buffer.byteLength(body),
        response_body_bytes: Buffer.byteLength(body),
      })]);
      expect(JSON.stringify(events)).not.toContain(body);
    } finally {
      await running.close();
    }
  });

  it("records a client-closed tools/call without claiming attempted bytes were delivered", async () => {
    const blocked = blockedSessionServer("tools/call");
    const lines: string[] = [];
    const logger = new OperationalLogger({
      level: "debug",
      writer: { write: (line) => { lines.push(line); return true; }, once: () => undefined },
    });
    const running = await startHttpMcpServer(() => blocked.server, { host: "127.0.0.1", port: 0, shutdownTimeoutMs: 100, logger });
    const controller = new AbortController();
    try {
      const initialized = await initialize(running.url, 1);
      const sessionId = initialized.headers.get("mcp-session-id") ?? "";
      const pending = fetch(running.url, {
        method: "POST",
        headers: {
          accept: "application/json, text/event-stream",
          "content-type": "application/json",
          "mcp-session-id": sessionId,
        },
        body: JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "sgg_unity_projects", arguments: {} } }),
        signal: controller.signal,
      });
      await within(blocked.started, "blocked tools/call start");
      controller.abort();
      await within(expect(pending).rejects.toMatchObject({ name: "AbortError" }), "client abort");
      await within(waitFor(() => responseEvents(lines).some((event) => event.request_kind === "tools_call" && event.state === "closed")), "closed response telemetry");

      const events = responseEvents(lines).filter((event) => event.request_kind === "tools_call");
      expect(events).toEqual([expect.objectContaining({
        state: "closed",
        headers_sent: false,
        response_body_write_bytes: 0,
        response_body_bytes: 0,
      })]);
    } finally {
      blocked.release();
      await running.close();
    }
  });

  it("reserves session capacity concurrently and releases it on DELETE", async () => {
    const logger = new OperationalLogger({
      level: "debug",
      writer: { write: () => { throw new Error("hostile sink secret"); }, once: () => undefined },
    });
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      sessions: { maxActive: 1, idleTtlMs: 60_000, cleanupIntervalMs: 1_000 },
      logger,
    });
    try {
      const responses = await Promise.all([initialize(running.url, 1), initialize(running.url, 2)]);
      expect(responses.map((response) => response.status).sort()).toEqual([200, 503]);
      const rejected = responses.find((response) => response.status === 503);
      expect(await rejected?.json()).toEqual({ jsonrpc: "2.0", id: null, error: { code: -32002, message: "Session capacity reached" } });

      const accepted = responses.find((response) => response.status === 200);
      const sessionId = accepted?.headers.get("mcp-session-id") ?? "";
      expect(sessionId).toBeTruthy();
      expect((await fetch(running.url, { method: "DELETE", headers: { "mcp-session-id": sessionId } })).status).toBe(200);
      expect((await initialize(running.url, 3)).status).toBe(200);

      const health = await readHealth(running.url);
      expect(health.http_mcp).toMatchObject({ active_sessions: 1, max_active_sessions: 1, session_capacity_rejections_total: 1 });
      expect(JSON.stringify(health)).not.toContain(sessionId);
      expect(logger.status().errors).toBeGreaterThan(0);
    } finally {
      await running.close();
    }
  });

  it("does not expire an in-flight session and expires it only after later idle TTL", async () => {
    const fake = fakeClock();
    let releaseRequest!: () => void;
    let markStarted!: () => void;
    const requestStarted = new Promise<void>((resolve) => { markStarted = resolve; });
    const requestGate = new Promise<void>((resolve) => { releaseRequest = resolve; });
    const running = await startHttpMcpServer(() => {
      const server = createUnityMcpServer({}, { version: "0.0.0" });
      const connect = server.connect.bind(server);
      server.connect = async (transport: Transport) => {
        await connect(transport);
        const httpTransport = transport as Transport & { handleRequest: (request: unknown, response: unknown, body: unknown) => Promise<void> };
        const handleRequest = httpTransport.handleRequest.bind(httpTransport);
        httpTransport.handleRequest = async (request, response, body) => {
          if (isMethod(body, "tools/list")) {
            markStarted();
            await requestGate;
          }
          await handleRequest(request, response, body);
        };
      };
      return server;
    }, {
      host: "127.0.0.1",
      port: 0,
      sessions: { maxActive: 2, idleTtlMs: 100, cleanupIntervalMs: 10 },
      clock: fake.clock,
    });
    try {
      const initialized = await initialize(running.url, 1);
      const sessionId = initialized.headers.get("mcp-session-id") ?? "";
      const pending = post(running.url, { jsonrpc: "2.0", id: 2, method: "tools/list" }, { "mcp-session-id": sessionId });
      await requestStarted;
      await fake.advance(100);
      expect((await readHealth(running.url)).http_mcp).toMatchObject({ active_sessions: 1, in_flight_session_requests: 1, expired_sessions_total: 0 });

      releaseRequest();
      expect((await pending).status).toBe(200);
      await fake.advance(99);
      expect((await readHealth(running.url)).http_mcp.active_sessions).toBe(1);
      await fake.advance(1);
      expect((await readHealth(running.url)).http_mcp).toMatchObject({ active_sessions: 0, expired_sessions_total: 1 });
      expect((await post(running.url, { jsonrpc: "2.0", id: 3, method: "tools/list" }, { "mcp-session-id": sessionId })).status).toBe(404);
    } finally {
      await running.close();
    }
  });

  it("counts only revalidated closed sessions across overlapping cleanup and renewed activity", async () => {
    const fake = fakeClock();
    let releaseClose!: () => void;
    let markCloseStarted!: () => void;
    const closeStarted = new Promise<void>((resolve) => { markCloseStarted = resolve; });
    const closeGate = new Promise<void>((resolve) => { releaseClose = resolve; });
    const first = createUnityMcpServer({}, { version: "0.0.0" });
    const firstClose = first.close.bind(first);
    first.close = async () => { markCloseStarted(); await closeGate; await firstClose(); };
    const servers = [first, createUnityMcpServer({}, { version: "0.0.0" })];
    const lines: string[] = [];
    const logger = new OperationalLogger({
      level: "debug",
      writer: { write: (line) => { lines.push(line); return true; }, once: () => undefined },
    });
    const running = await startHttpMcpServer(() => servers.shift()!, {
      host: "127.0.0.1",
      port: 0,
      sessions: { maxActive: 2, idleTtlMs: 100, cleanupIntervalMs: 10 },
      clock: fake.clock,
      logger,
    });
    try {
      const firstSession = (await initialize(running.url, 1)).headers.get("mcp-session-id") ?? "";
      const secondSession = (await initialize(running.url, 2)).headers.get("mcp-session-id") ?? "";
      expect(firstSession).toBeTruthy();
      expect(secondSession).toBeTruthy();

      await fake.advance(100);
      await closeStarted;
      expect((await post(running.url, { jsonrpc: "2.0", id: 3, method: "tools/list" }, { "mcp-session-id": secondSession })).status).toBe(200);
      await fake.advance(10);
      releaseClose();
      await within(waitForHealthExpired(running.url, 1), "one completed idle expiration");

      expect((await readHealth(running.url)).http_mcp).toMatchObject({ active_sessions: 1, expired_sessions_total: 1 });
      const cleanup = lines.map((line) => JSON.parse(line)).filter((event) => event.event_code === "SGG-OP-SESSION-CLEANUP-001");
      expect(cleanup).toEqual([expect.objectContaining({ expired_sessions: 1, active_sessions: 1 })]);
    } finally {
      releaseClose?.();
      await running.close();
    }
  });

  it("retains capacity while DELETE closes a session with an in-flight request", async () => {
    const blocked = blockedSessionServer("tools/list");
    const servers = [blocked.server, createUnityMcpServer({}, { version: "0.0.0" })];
    const firstClose = vi.spyOn(blocked.server, "close");
    const running = await startHttpMcpServer(() => servers.shift()!, {
      host: "127.0.0.1",
      port: 0,
      sessions: { maxActive: 1, idleTtlMs: 60_000, cleanupIntervalMs: 1_000 },
    });
    let bodyError: unknown;
    try {
      const initialized = await initialize(running.url, 1);
      const sessionId = initialized.headers.get("mcp-session-id") ?? "";
      const pendingRequest = post(running.url, { jsonrpc: "2.0", id: 2, method: "tools/list" }, { "mcp-session-id": sessionId });
      await within(blocked.started, "blocked request start");

      const deleting = deleteRequest(running.url, sessionId);
      await within(waitForClosing(running.url), "DELETE start");
      expect((await within(initialize(running.url, 3), "capacity response")).status).toBe(503);
      const closingHealth = (await readHealth(running.url)).http_mcp;
      expect(closingHealth.active_sessions).toBe(1);
      expect(closingHealth.in_flight_session_requests).toBeGreaterThanOrEqual(1);
      expect(firstClose).not.toHaveBeenCalled();

      blocked.release();
      const deleteResponse = await within(deleting, "DELETE response");
      expect(deleteResponse.status).toBe(200);
      const pendingResponse = await within(pendingRequest, "blocked request completion");
      await pendingResponse.body?.cancel();
      await within(waitFor(() => firstClose.mock.calls.length === 1), "session disposal");
      expect((await within(initialize(running.url, 4), "replacement session")).status).toBe(200);
      expect(firstClose).toHaveBeenCalledOnce();
    } catch (error) {
      bodyError = error;
    } finally {
      blocked.release();
      try {
        await within(running.close(), "test shutdown");
      } catch (error) {
        if (!bodyError) throw error;
      }
    }
    if (bodyError) throw bodyError;
  });

  it("gates initialization synchronously and closes a transport connected during shutdown", async () => {
    let releaseConnect!: () => void;
    let markConnectStarted!: () => void;
    const connectStarted = new Promise<void>((resolve) => { markConnectStarted = resolve; });
    const connectGate = new Promise<void>((resolve) => { releaseConnect = resolve; });
    const server = createUnityMcpServer({}, { version: "0.0.0" });
    const serverClose = vi.spyOn(server, "close");
    const connect = server.connect.bind(server);
    let transportClose: ReturnType<typeof vi.spyOn> | undefined;
    server.connect = async (transport: Transport) => {
      transportClose = vi.spyOn(transport, "close");
      markConnectStarted();
      await connectGate;
      await connect(transport);
    };
    let created = 0;
    const running = await startHttpMcpServer(() => {
      created += 1;
      return server;
    }, { host: "127.0.0.1", port: 0, sessions: { maxActive: 1, idleTtlMs: 60_000, cleanupIntervalMs: 1_000 } });

    const pendingInitialize = initialize(running.url, 1);
    await connectStarted;
    const firstShutdown = running.close();
    const secondShutdown = running.close();
    expect(firstShutdown).toBe(secondShutdown);
    expect(serverClose).not.toHaveBeenCalled();

    const afterShutdown = await initialize(running.url, 2).catch(() => undefined);
    if (afterShutdown) expect(afterShutdown.status).toBe(503);
    expect(created).toBe(1);

    releaseConnect();
    const initializeResponse = await pendingInitialize;
    expect(initializeResponse.status).toBe(503);
    await Promise.all([firstShutdown, secondShutdown]);
    expect(serverClose).toHaveBeenCalledOnce();
    expect(transportClose).toHaveBeenCalledOnce();
    await running.close();
  });

  it("tracks a stateless transport still connecting during shutdown", async () => {
    let releaseConnect!: () => void;
    let markConnectStarted!: () => void;
    const connectStarted = new Promise<void>((resolve) => { markConnectStarted = resolve; });
    const connectGate = new Promise<void>((resolve) => { releaseConnect = resolve; });
    const server = createUnityMcpServer({}, { version: "0.0.0" });
    const serverClose = vi.spyOn(server, "close");
    const connect = server.connect.bind(server);
    let transportClose: ReturnType<typeof vi.spyOn> | undefined;
    server.connect = async (transport: Transport) => {
      transportClose = vi.spyOn(transport, "close");
      markConnectStarted();
      await connectGate;
      await connect(transport);
    };
    const running = await startHttpMcpServer(() => server, { host: "127.0.0.1", port: 0 });
    const pendingInitialize = initializeStateless(running.url, 1);
    await connectStarted;
    const shutdown = running.close();
    expect(serverClose).not.toHaveBeenCalled();
    releaseConnect();
    expect((await pendingInitialize).status).toBe(503);
    await shutdown;
    expect(serverClose).toHaveBeenCalledOnce();
    expect(transportClose).toHaveBeenCalledOnce();
  });

  it("bounds shutdown when stateful SDK connect never settles", async () => {
    const fake = fakeClock();
    let markConnectStarted!: () => void;
    const connectStarted = new Promise<void>((resolve) => { markConnectStarted = resolve; });
    const never = new Promise<void>(() => {});
    const server = createUnityMcpServer({}, { version: "0.0.0" });
    server.connect = async () => {
      markConnectStarted();
      await never;
    };
    const running = await startHttpMcpServer(() => server, {
      host: "127.0.0.1",
      port: 0,
      shutdownTimeoutMs: 100,
      clock: fake.clock,
    });
    const pendingInitialize = initialize(running.url, 1);
    void pendingInitialize.catch(() => {});
    await connectStarted;

    let closed = false;
    const firstClose = running.close().then(() => { closed = true; });
    const secondClose = running.close();
    await fake.advance(99);
    expect(closed).toBe(false);
    await fake.advance(1);
    await Promise.all([firstClose, secondClose]);
    expect(closed).toBe(true);
    expect(fake.unref).toHaveBeenCalledTimes(2);
    expect(fake.clear).toHaveBeenCalledOnce();
    expect(fake.clearTimeout).toHaveBeenCalledOnce();
  });

  it("best-effort closes a stateful SDK pair that connects after shutdown deadline", async () => {
    const fake = fakeClock();
    let releaseConnect!: () => void;
    let markConnectStarted!: () => void;
    const connectStarted = new Promise<void>((resolve) => { markConnectStarted = resolve; });
    const connectGate = new Promise<void>((resolve) => { releaseConnect = resolve; });
    const server = createUnityMcpServer({}, { version: "0.0.0" });
    const serverClose = vi.spyOn(server, "close");
    const connect = server.connect.bind(server);
    let transportClose: ReturnType<typeof vi.spyOn> | undefined;
    server.connect = async (transport: Transport) => {
      transportClose = vi.spyOn(transport, "close");
      markConnectStarted();
      await connectGate;
      await connect(transport);
    };
    const unhandled: unknown[] = [];
    const onUnhandled = (reason: unknown) => { unhandled.push(reason); };
    process.on("unhandledRejection", onUnhandled);
    try {
      const running = await startHttpMcpServer(() => server, {
        host: "127.0.0.1",
        port: 0,
        shutdownTimeoutMs: 100,
        clock: fake.clock,
      });
      const pendingInitialize = initialize(running.url, 1);
      void pendingInitialize.catch(() => {});
      await connectStarted;
      const shutdown = running.close();
      await fake.advance(100);
      await shutdown;
      expect(serverClose).not.toHaveBeenCalled();

      releaseConnect();
      await pendingInitialize.catch(() => undefined);
      await within(waitFor(() => serverClose.mock.calls.length === 1), "late SDK disposal");
      await new Promise<void>((resolve) => setImmediate(resolve));
      expect(serverClose).toHaveBeenCalledOnce();
      expect(transportClose).toHaveBeenCalledOnce();
      expect(unhandled).toEqual([]);
      await running.close();
    } finally {
      releaseConnect?.();
      process.off("unhandledRejection", onUnhandled);
    }
  });

  it("unrefs its cleanup timer and clears it during shutdown", async () => {
    const fake = fakeClock();
    const server = createUnityMcpServer({}, { version: "0.0.0" });
    const close = vi.spyOn(server, "close");
    const running = await startHttpMcpServer(() => server, {
      host: "127.0.0.1",
      port: 0,
      clock: fake.clock,
    });
    expect(fake.unref).toHaveBeenCalledOnce();
    expect((await initialize(running.url, 1)).status).toBe(200);
    await running.close();
    expect(fake.clear).toHaveBeenCalledOnce();
    expect(close).toHaveBeenCalled();
  });

  it("ignores spoofed forwarding headers unless trusted proxy mode is explicit", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      rateLimit: { windowMs: 60_000, maxRequests: 1, maxBuckets: 4 },
    });
    try {
      expect((await initialize(running.url, 1, { "x-forwarded-for": "192.0.2.1", forwarded: "for=192.0.2.1" })).status).toBe(200);
      expect((await initialize(running.url, 2, { "x-forwarded-for": "198.51.100.2" })).status).toBe(429);
    } finally {
      await running.close();
    }
  });

  it("requires canonical trusted peers and rejects forwarding from other socket peers", async () => {
    await expect(startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      rateLimit: { windowMs: 100, maxRequests: 1, trustedProxy: true },
    })).rejects.toThrow(/trusted proxy peer/i);
    await expect(startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      rateLimit: { windowMs: 100, maxRequests: 1, trustedProxy: true, trustedProxyPeers: ["::ffff:127.0.0.1"] },
    })).rejects.toThrow(/canonical IP/i);

    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      rateLimit: { windowMs: 100, maxRequests: 1, trustedProxy: true, trustedProxyPeers: ["192.0.2.10"] },
    });
    try {
      expect((await initialize(running.url, 1, { "x-forwarded-for": "192.0.2.1" })).status).toBe(429);
    } finally {
      await running.close();
    }
  });

  it("canonicalizes equivalent forwarded IPv4 and IPv6 identities into one bucket", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      rateLimit: {
        windowMs: 60_000,
        maxRequests: 1,
        maxBuckets: 4,
        trustedProxy: true,
        trustedProxyPeers: ["127.0.0.1"],
      },
    });
    try {
      expect((await initialize(running.url, 1, { "x-forwarded-for": "::ffff:c000:201" })).status).toBe(200);
      expect((await initialize(running.url, 2, { "x-forwarded-for": "192.0.2.1" })).status).toBe(429);
      expect((await initialize(running.url, 3, { "x-forwarded-for": "2001:0DB8:0:0:0:0:0:1" })).status).toBe(200);
      expect((await initialize(running.url, 4, { "x-forwarded-for": "2001:db8::1" })).status).toBe(429);
      expect((await initialize(running.url, 5, { "x-forwarded-for": "fe80::1%eth0" })).status).toBe(429);
    } finally {
      await running.close();
    }
  });

  it("strictly keys trusted proxy clients, caps buckets, and reuses expired capacity", async () => {
    const fake = fakeClock();
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), {
      host: "127.0.0.1",
      port: 0,
      clock: fake.clock,
      rateLimit: {
        windowMs: 100,
        maxRequests: 5,
        maxBuckets: 2,
        cleanupIntervalMs: 10,
        trustedProxy: true,
        trustedProxyPeers: ["127.0.0.1"],
      },
    });
    try {
      expect((await initialize(running.url, 1, { "x-forwarded-for": "192.0.2.1" })).status).toBe(200);
      expect((await initialize(running.url, 2, { "x-forwarded-for": "192.0.2.2" })).status).toBe(200);
      expect((await initialize(running.url, 3, { "x-forwarded-for": "192.0.2.3" })).status).toBe(429);
      expect((await initialize(running.url, 4, { "x-forwarded-for": "192.0.2.3, 192.0.2.4" })).status).toBe(429);
      expect((await initialize(running.url, 5, { "x-forwarded-for": "192.0.2.3", forwarded: "for=192.0.2.3" })).status).toBe(429);

      await fake.advance(100);
      expect((await initialize(running.url, 6, { "x-forwarded-for": "192.0.2.3" })).status).toBe(200);
      expect((await readHealth(running.url)).http_mcp).toMatchObject({
        rate_limit_buckets: 1,
        max_rate_limit_buckets: 2,
        expired_rate_limit_buckets_total: 2,
        rate_limit_capacity_rejections_total: 1,
      });
    } finally {
      await running.close();
    }
  });

  it("canonicalizes IPv6 safely and rejects scoped or non-canonical boundary forms", () => {
    expect(canonicalizeIpAddress("192.0.2.1")).toBe("192.0.2.1");
    expect(canonicalizeIpAddress("0:0:0:0:0:0:0:1")).toBe("::1");
    expect(canonicalizeIpAddress("2001:0DB8:0:0:0:0:0:1")).toBe("2001:db8::1");
    expect(canonicalizeIpAddress("::ffff:192.0.2.1")).toBe("192.0.2.1");
    expect(canonicalizeIpAddress("::ffff:c000:201")).toBe("192.0.2.1");
    expect(canonicalizeIpAddress("fe80::1%eth0")).toBeUndefined();
    expect(canonicalizeIpAddress("127.000.000.001")).toBeUndefined();
    expect(canonicalizeIpAddress("[::1]")).toBeUndefined();
  });
});

function initialize(url: string, id: number, headers: Record<string, string> = {}): Promise<Response> {
  return post(url, {
    jsonrpc: "2.0",
    id,
    method: "initialize",
    params: {
      protocolVersion: SUPPORTED_MCP_PROTOCOL_VERSION,
      capabilities: { elicitation: { form: {} } },
      clientInfo: { name: "bounded-test", version: "0.0.0" },
    },
  }, headers);
}

function initializeStateless(url: string, id: number): Promise<Response> {
  return post(url, {
    jsonrpc: "2.0",
    id,
    method: "initialize",
    params: {
      protocolVersion: SUPPORTED_MCP_PROTOCOL_VERSION,
      capabilities: {},
      clientInfo: { name: "bounded-stateless-test", version: "0.0.0" },
    },
  });
}

function post(url: string, body: unknown, headers: Record<string, string> = {}): Promise<Response> {
  return fetch(url, {
    method: "POST",
    headers: { accept: "application/json, text/event-stream", "content-type": "application/json", ...headers },
    body: JSON.stringify(body),
  });
}

function responseEvents(lines: string[]): Array<Record<string, unknown>> {
  return lines.map((line) => JSON.parse(line) as Record<string, unknown>).filter((event) => event.event_code === "SGG-OP-MCP-HTTP-RESPONSE-001");
}

function deleteRequest(url: string, sessionId: string): Promise<{ status: number }> {
  return new Promise((resolve, reject) => {
    const request = http.request(url, { method: "DELETE", headers: { "mcp-session-id": sessionId } }, (response) => {
      response.resume();
      response.on("end", () => resolve({ status: response.statusCode ?? 0 }));
    });
    request.on("error", reject);
    request.end();
  });
}

async function readHealth(url: string): Promise<{ http_mcp: Record<string, number> }> {
  return await (await fetch(new URL("/health", url))).json() as { http_mcp: Record<string, number> };
}

function isMethod(body: unknown, method: string): boolean {
  return typeof body === "object" && body !== null && "method" in body && (body as { method?: unknown }).method === method;
}

function blockedSessionServer(method: string): {
  server: ReturnType<typeof createUnityMcpServer>;
  started: Promise<void>;
  release: () => void;
} {
  let release!: () => void;
  let markStarted!: () => void;
  const started = new Promise<void>((resolve) => { markStarted = resolve; });
  const gate = new Promise<void>((resolve) => { release = resolve; });
  const server = createUnityMcpServer({}, { version: "0.0.0" });
  const connect = server.connect.bind(server);
  server.connect = async (transport: Transport) => {
    await connect(transport);
    const httpTransport = transport as Transport & { handleRequest: (request: unknown, response: unknown, body: unknown) => Promise<void> };
    const handleRequest = httpTransport.handleRequest.bind(httpTransport);
    httpTransport.handleRequest = async (request, response, body) => {
      if (isMethod(body, method)) {
        markStarted();
        await gate;
      }
      await handleRequest(request, response, body);
    };
  };
  return { server, started, release };
}

async function waitFor(predicate: () => boolean): Promise<void> {
  for (let attempt = 0; attempt < 100; attempt += 1) {
    if (predicate()) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 1));
  }
  throw new Error("Timed out waiting for test condition");
}

async function waitForClosing(url: string): Promise<void> {
  for (let attempt = 0; attempt < 500; attempt += 1) {
    if (((await readHealth(url)).http_mcp.closing_sessions ?? 0) >= 1) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 1));
  }
  throw new Error("Timed out waiting for closing session");
}

async function waitForHealthExpired(url: string, expected: number): Promise<void> {
  for (let attempt = 0; attempt < 500; attempt += 1) {
    if (((await readHealth(url)).http_mcp.expired_sessions_total ?? 0) === expected) return;
    await new Promise<void>((resolve) => setTimeout(resolve, 1));
  }
  throw new Error("Timed out waiting for expired session count");
}

async function within<T>(promise: Promise<T>, label: string): Promise<T> {
  return await Promise.race([
    promise,
    new Promise<never>((_, reject) => setTimeout(() => reject(new Error(`Timed out waiting for ${label}`)), 1_000)),
  ]);
}

function fakeClock(initial = 0): {
  clock: HttpMcpClock;
  advance: (milliseconds: number) => Promise<void>;
  unref: ReturnType<typeof vi.fn>;
  clear: ReturnType<typeof vi.fn>;
  clearTimeout: ReturnType<typeof vi.fn>;
} {
  let now = initial;
  const callbacks = new Map<HttpMcpTimer, { callback: () => void; due: number; interval?: number }>();
  const unref = vi.fn();
  const clear = vi.fn((timer: HttpMcpTimer) => callbacks.delete(timer));
  const clearTimeout = vi.fn((timer: HttpMcpTimer) => callbacks.delete(timer));
  return {
    clock: {
      now: () => now,
      setInterval: (callback, delayMs) => {
        const timer = { unref };
        callbacks.set(timer, { callback, due: now + delayMs, interval: delayMs });
        return timer;
      },
      clearInterval: clear,
      setTimeout: (callback, delayMs) => {
        const timer = { unref };
        callbacks.set(timer, { callback, due: now + delayMs });
        return timer;
      },
      clearTimeout,
    },
    advance: async (milliseconds) => {
      now += milliseconds;
      for (const [timer, scheduled] of [...callbacks]) {
        if (scheduled.due > now) continue;
        if (scheduled.interval === undefined) callbacks.delete(timer);
        else scheduled.due += scheduled.interval;
        scheduled.callback();
      }
      await new Promise<void>((resolve) => setImmediate(resolve));
    },
    unref,
    clear,
    clearTimeout,
  };
}
