import { randomUUID } from "node:crypto";
import http, { type IncomingMessage, type ServerResponse } from "node:http";

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import { isInitializeRequest, SUPPORTED_PROTOCOL_VERSIONS } from "@modelcontextprotocol/sdk/types.js";
import { createMcpHandler, isLegacyRequest, type McpServerFactory as ModernMcpServerFactory } from "@modelcontextprotocol/server";
import { hostHeaderValidation, toWebRequest } from "@modelcontextprotocol/node";

import { canonicalizeIpAddress } from "./ip-address.js";
import type { OperationalLogger } from "../observability/operational-logger.js";

const TOKEN_HEADER = "x-sgg-unity-mcp-token";
export const SUPPORTED_MCP_PROTOCOL_VERSION = "2025-06-18";
export const LEGACY_MCP_PROTOCOL_VERSIONS = Object.freeze([...SUPPORTED_PROTOCOL_VERSIONS]);

const DEFAULT_MAX_BODY_BYTES = 1024 * 1024;
const DEFAULT_REQUEST_TIMEOUT_MS = 30_000;
const DEFAULT_RATE_LIMIT_WINDOW_MS = 60_000;
const DEFAULT_RATE_LIMIT_MAX_REQUESTS = 120;
const DEFAULT_MAX_ACTIVE_SESSIONS = 128;
const DEFAULT_SESSION_IDLE_TTL_MS = 30 * 60_000;
const DEFAULT_SESSION_CLEANUP_INTERVAL_MS = 60_000;
const DEFAULT_RATE_LIMIT_MAX_BUCKETS = 4096;
const DEFAULT_RATE_LIMIT_CLEANUP_INTERVAL_MS = 60_000;
const DEFAULT_SHUTDOWN_TIMEOUT_MS = 5_000;
const MAX_ACTIVE_SESSIONS = 10_000;
const MAX_SESSION_IDLE_TTL_MS = 7 * 24 * 60 * 60_000;
const MAX_CLEANUP_INTERVAL_MS = 24 * 60 * 60_000;
const MAX_RATE_LIMIT_WINDOW_MS = 24 * 60 * 60_000;
const MAX_RATE_LIMIT_REQUESTS = 1_000_000;
const MAX_RATE_LIMIT_BUCKETS = 1_000_000;
const MAX_TRUSTED_PROXY_PEERS = 64;
const MAX_SHUTDOWN_TIMEOUT_MS = 60_000;

export type HttpMcpServerOptions = {
  host: string;
  port: number;
  path?: string;
  healthPath?: string;
  token?: string;
  allowedOrigins?: string[];
  allowNonLoopback?: boolean;
  maxBodyBytes?: number;
  requestTimeoutMs?: number;
  shutdownTimeoutMs?: number;
  sessions?: {
    maxActive: number;
    idleTtlMs: number;
    cleanupIntervalMs: number;
  };
  rateLimit?: {
    windowMs: number;
    maxRequests: number;
    maxBuckets?: number;
    cleanupIntervalMs?: number;
    trustedProxy?: boolean;
    trustedProxyPeers?: string[];
  };
  health?: () => Record<string, unknown>;
  clock?: HttpMcpClock;
  logger?: OperationalLogger;
  modernServerFactory?: ModernMcpServerFactory;
};

export type HttpMcpClock = {
  now: () => number;
  setInterval: (callback: () => void, delayMs: number) => HttpMcpTimer;
  clearInterval: (timer: HttpMcpTimer) => void;
  setTimeout: (callback: () => void, delayMs: number) => HttpMcpTimer;
  clearTimeout: (timer: HttpMcpTimer) => void;
};

export type HttpMcpTimer = {
  unref?: () => void;
};

export type RunningHttpMcpServer = {
  url: string;
  close: () => Promise<void>;
};

export type HttpMcpServerFactory = () => McpServer;

export async function startHttpMcpServer(mcpServerFactory: HttpMcpServerFactory, options: HttpMcpServerOptions): Promise<RunningHttpMcpServer> {
  assertSafeBinding(options);

  const mcpPath = options.path ?? "/mcp";
  const healthPath = options.healthPath ?? "/health";
  const maxBodyBytes = options.maxBodyBytes ?? DEFAULT_MAX_BODY_BYTES;
  const requestTimeoutMs = options.requestTimeoutMs ?? DEFAULT_REQUEST_TIMEOUT_MS;
  const shutdownTimeoutMs = options.shutdownTimeoutMs ?? DEFAULT_SHUTDOWN_TIMEOUT_MS;
  const sessionLimits = options.sessions ?? {
    maxActive: DEFAULT_MAX_ACTIVE_SESSIONS,
    idleTtlMs: DEFAULT_SESSION_IDLE_TTL_MS,
    cleanupIntervalMs: DEFAULT_SESSION_CLEANUP_INTERVAL_MS,
  };
  const rateLimit = {
    windowMs: options.rateLimit?.windowMs ?? DEFAULT_RATE_LIMIT_WINDOW_MS,
    maxRequests: options.rateLimit?.maxRequests ?? DEFAULT_RATE_LIMIT_MAX_REQUESTS,
    maxBuckets: options.rateLimit?.maxBuckets ?? DEFAULT_RATE_LIMIT_MAX_BUCKETS,
    cleanupIntervalMs: options.rateLimit?.cleanupIntervalMs ?? DEFAULT_RATE_LIMIT_CLEANUP_INTERVAL_MS,
    trustedProxy: options.rateLimit?.trustedProxy ?? false,
    trustedProxyPeers: options.rateLimit?.trustedProxyPeers ?? [],
  };
  assertOperationalLimits(sessionLimits, rateLimit, shutdownTimeoutMs);
  const validateHost = createMcpHostValidation(options);
  const trustedProxyPeers = new Set(rateLimit.trustedProxyPeers);
  const clock = options.clock ?? systemClock;
  const sessionById = new Map<string, HttpMcpSession>();
  const sessions = new Set<HttpMcpSession>();
  const transientSessions = new Set<HttpTransientSession>();
  const rateLimitBuckets = new Map<string, RateLimitBucket>();
  const metrics: HttpOperationalMetrics = {
    sessionCapacityRejections: 0,
    expiredSessions: 0,
    rateLimitCapacityRejections: 0,
    expiredRateLimitBuckets: 0,
  };
  const capacityLogState = { sessions: false, rateLimit: false };
  const modernHandler = options.modernServerFactory
    ? createMcpHandler(options.modernServerFactory, {
      legacy: "reject",
    })
    : undefined;
  let modernRequestsInFlight = 0;
  let shuttingDown = false;
  let shutdownPromise: Promise<void> | undefined;

  const cleanupTimer = clock.setInterval(() => {
    expireRateLimitBuckets(rateLimitBuckets, clock.now(), metrics, options.logger);
    void expireIdleSessions(sessionById, sessions, sessionLimits.idleTtlMs, clock.now(), metrics, options.logger);
  }, Math.min(sessionLimits.cleanupIntervalMs, rateLimit.cleanupIntervalMs));
  cleanupTimer.unref?.();

  const httpServer = http.createServer(async (request, response) => {
    if (!validateHost(request, response)) {
      return;
    }
    if (shuttingDown && matchesPath(request, mcpPath)) {
      writeJson(response, 503, jsonRpcError(null, -32002, "Session capacity reached"));
      return;
    }
    if (request.method === "GET" && matchesPath(request, healthPath)) {
      if (!isAuthorized(request, options.token)) {
        writeJson(response, 401, { error: "invalid_token" });
        return;
      }

      writeJson(response, 200, readHealth(options, sessions, transientSessions, modernRequestsInFlight, rateLimitBuckets, sessionLimits.maxActive, rateLimit.maxBuckets, metrics));
      return;
    }

    if (!matchesPath(request, mcpPath)) {
      writeJson(response, 404, { error: "not_found" });
      return;
    }

    if (!isAllowedOrigin(request, options.allowedOrigins ?? [])) {
      writeJson(response, 403, { error: "invalid_origin" });
      return;
    }

    if (!consumeRateLimit(request, rateLimitBuckets, rateLimit, trustedProxyPeers, clock.now(), metrics, capacityLogState, options.logger)) {
      writeJson(response, 429, { error: "rate_limited" });
      return;
    }

    if (!isAuthorized(request, options.token)) {
      writeJson(response, 401, { error: "invalid_token" });
      return;
    }

    try {
      if (request.method !== "POST" && requestHasBody(request)) {
        request.resume();
        writeJson(response, 400, jsonRpcError(null, -32000, "Request bodies are only accepted for MCP POST requests"));
        return;
      }
      const parsedBody = request.method === "POST" ? await readMcpPostBody(request, { maxBodyBytes, requestTimeoutMs }) : undefined;
      await handleRoutedMcpRequest(
        mcpServerFactory,
        modernHandler,
        request,
        response,
        parsedBody,
        sessionById,
        sessions,
        sessionLimits.maxActive,
        clock,
        metrics,
        () => shuttingDown,
        transientSessions,
        capacityLogState,
        options.logger,
        {
          increment: () => { modernRequestsInFlight = incrementCounter(modernRequestsInFlight); },
          decrement: () => { modernRequestsInFlight = Math.max(0, modernRequestsInFlight - 1); },
        },
      );
    } catch (error) {
      if (error instanceof HttpRequestError) {
        writeJson(response, error.statusCode, jsonRpcError(null, -32000, error.message));
        return;
      }
      if (!response.headersSent) {
        writeJson(response, 500, jsonRpcError(null, -32603, "Internal server error"));
      }
    }
  });

  try {
    await listen(httpServer, options.host, options.port);
  } catch (error) {
    clock.clearInterval(cleanupTimer);
    throw error;
  }
  const address = httpServer.address();
  const port = typeof address === "object" && address ? address.port : options.port;
  options.logger?.log("SGG-OP-HTTP-LISTEN-001", port);

  return {
    url: `http://${options.host}:${port}${mcpPath}`,
    close: () => {
      if (!shutdownPromise) {
        shuttingDown = true;
        options.logger?.log("SGG-OP-HTTP-SHUTDOWN-001", "started", sessions.size, transientSessions.size + modernRequestsInFlight, rateLimitBuckets.size);
        clock.clearInterval(cleanupTimer);
        const stopAccepting = closeHttpServer(httpServer);
        void stopAccepting.catch(() => {});
        shutdownPromise = (async () => {
          const pendingCleanup = [
            ...(modernHandler ? [modernHandler.close()] : []),
            ...[...sessions].map((session) => requestSessionClose(session, sessionById, sessions, true)),
            ...[...transientSessions].map((session) => requestTransientSessionClose(session, transientSessions)),
          ];
          await settleByDeadline(pendingCleanup, shutdownTimeoutMs, clock);
          sessionById.clear();
          sessions.clear();
          transientSessions.clear();
          rateLimitBuckets.clear();
          httpServer.closeAllConnections();
          options.logger?.log("SGG-OP-HTTP-SHUTDOWN-001", "completed", 0, 0, 0);
        })();
      }
      return shutdownPromise;
    },
  };
}

type McpRequestLimits = {
  maxBodyBytes: number;
  requestTimeoutMs: number;
};

type McpPostBody = {
  raw: string;
  parsed?: unknown;
  valid: boolean;
};

type RateLimitBucket = {
  resetAt: number;
  count: number;
};

type HttpMcpSession = {
  server: McpServer;
  transport: StreamableHTTPServerTransport;
  lastActivityAt: number;
  inFlight: number;
  closing: boolean;
  connecting: boolean;
  forceDisposeRequested: boolean;
  disposeStarted: boolean;
  disposed: boolean;
  closedResolved: boolean;
  closedPromise: Promise<void>;
  resolveClosed: () => void;
};

type HttpOperationalMetrics = {
  sessionCapacityRejections: number;
  expiredSessions: number;
  rateLimitCapacityRejections: number;
  expiredRateLimitBuckets: number;
};

type HttpTransientSession = {
  server: McpServer;
  transport: StreamableHTTPServerTransport;
  connecting: boolean;
  closeRequested: boolean;
  disposeStarted: boolean;
  closedPromise: Promise<void>;
  resolveClosed: () => void;
};

class HttpRequestError extends Error {
  constructor(
    readonly statusCode: number,
    message: string,
  ) {
    super(message);
  }
}

async function handleRoutedMcpRequest(
  legacyServerFactory: HttpMcpServerFactory,
  modernHandler: ReturnType<typeof createMcpHandler> | undefined,
  request: IncomingMessage,
  response: ServerResponse,
  postBody: McpPostBody | undefined,
  sessionById: Map<string, HttpMcpSession>,
  sessions: Set<HttpMcpSession>,
  maxActiveSessions: number,
  clock: HttpMcpClock,
  metrics: HttpOperationalMetrics,
  isShuttingDown: () => boolean,
  transientSessions: Set<HttpTransientSession>,
  capacityLogState: { sessions: boolean },
  logger: OperationalLogger | undefined,
  modernInFlight: { increment: () => void; decrement: () => void },
): Promise<void> {
  const abortController = new AbortController();
  const abortRequest = () => abortController.abort();
  const abortClosedResponse = () => { if (!response.writableFinished) abortController.abort(); };
  request.once("aborted", abortRequest);
  response.once("close", abortClosedResponse);
  if (request.aborted || response.destroyed) abortController.abort();
  const webRequest = postBody?.valid === false
    ? webRequestFromRawBody(request, postBody.raw, abortController.signal)
    : await toWebRequest(request as unknown as Parameters<typeof toWebRequest>[0], postBody?.parsed, { signal: abortController.signal });
  try {
    const legacy = await isLegacyRequest(webRequest, postBody?.valid ? postBody.parsed : undefined);
    if (legacy) {
      if (postBody?.valid === false) {
        writeJson(response, 400, jsonRpcError(null, -32700, "Parse error: Invalid JSON"));
        return;
      }
      await handleLegacyMcpRequest(
        legacyServerFactory,
        request,
        response,
        postBody?.parsed,
        sessionById,
        sessions,
        maxActiveSessions,
        clock,
        metrics,
        isShuttingDown,
        transientSessions,
        capacityLogState,
        logger,
      );
      return;
    }

    if (!modernHandler) {
      writeJson(response, 400, jsonRpcError(null, -32022, "Unsupported MCP protocol version"));
      return;
    }
    const modernSessionHeader = strictHeader(request, "mcp-session-id");
    if (modernSessionHeader.invalid || modernSessionHeader.value !== undefined) {
      writeJson(response, 400, jsonRpcError(null, -32000, "MCP session headers are not valid for modern protocol requests"));
      return;
    }

    modernInFlight.increment();
    try {
      const responseCompleted = waitForResponseCompletion(response, mcpRequestKind(postBody?.parsed), logger);
      const modernResponse = await modernHandler.fetch(webRequest, postBody?.valid ? { parsedBody: postBody.parsed } : undefined);
      await writeWebResponse(response, modernResponse);
      requireFinishedResponse(await responseCompleted);
    } finally {
      modernInFlight.decrement();
    }
  } finally {
    request.off("aborted", abortRequest);
    response.off("close", abortClosedResponse);
  }
}

async function handleLegacyMcpRequest(
  mcpServerFactory: HttpMcpServerFactory,
  request: IncomingMessage,
  response: ServerResponse,
  parsedBody: unknown,
  sessionById: Map<string, HttpMcpSession>,
  sessions: Set<HttpMcpSession>,
  maxActiveSessions: number,
  clock: HttpMcpClock,
  metrics: HttpOperationalMetrics,
  isShuttingDown: () => boolean,
  transientSessions: Set<HttpTransientSession>,
  capacityLogState: { sessions: boolean },
  logger?: OperationalLogger,
): Promise<void> {
  const sessionHeader = strictHeader(request, "mcp-session-id");
  if (sessionHeader.invalid || (sessionHeader.value !== undefined && !isSessionId(sessionHeader.value))) {
    writeJson(response, 400, jsonRpcError(null, -32000, "Invalid MCP session header"));
    return;
  }

  const protocolHeader = strictHeader(request, "mcp-protocol-version");
  if (protocolHeader.invalid) {
    writeJson(response, 400, jsonRpcError(null, -32000, "Invalid MCP protocol version header"));
    return;
  }
  if (protocolHeader.value !== undefined && !SUPPORTED_PROTOCOL_VERSIONS.includes(protocolHeader.value)) {
    writeJson(response, 400, jsonRpcError(null, -32000, "Unsupported MCP protocol version"));
    return;
  }

  const sessionId = sessionHeader.value;
  if (sessionId) {
    const session = sessionById.get(sessionId);
    if (!session || session.closing) {
      writeJson(response, 404, jsonRpcError(
        null,
        -32001,
        "MCP session not found. This Gateway process is running, but the supplied session ID is not registered; it may belong to a previous Gateway process after restart or to a closed session. Reconnect this MCP client and send a new initialize request without mcp-session-id before retrying. The original request was not executed.",
        {
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
      ));
      return;
    }

    session.inFlight += 1;
    if (request.method === "DELETE") {
      session.closing = true;
      sessionById.delete(sessionId);
    }
    try {
      const responseCompleted = waitForResponseCompletion(response, mcpRequestKind(parsedBody), logger);
      await session.transport.handleRequest(request, response, parsedBody);
      requireFinishedResponse(await responseCompleted);
      session.lastActivityAt = clock.now();
    } finally {
      session.inFlight -= 1;
      if (request.method === "DELETE") {
        await requestSessionClose(session, sessionById, sessions);
      } else {
        beginSessionDisposalIfReady(session, sessionById, sessions);
        finishClosedSessionIfReady(session, sessionById, sessions);
      }
    }
    return;
  }

  if (request.method !== "POST") {
    writeJson(
      response,
      request.method === "GET" || request.method === "DELETE" ? 400 : 405,
      jsonRpcError(null, -32000, "Bad Request: No valid session ID provided"),
    );
    return;
  }

  if (!parsedBodyRequestsPersistentSession(parsedBody)) {
    if (isShuttingDown()) {
      writeJson(response, 503, jsonRpcError(null, -32002, "Session capacity reached"));
      return;
    }
    await handleStatelessMcpRequest(mcpServerFactory, request, response, parsedBody, transientSessions, isShuttingDown, logger);
    return;
  }
  if (!parsedBodyContainsInitialize(parsedBody)) {
    writeJson(response, 400, jsonRpcError(null, -32000, "Bad Request: No valid session ID provided"));
    return;
  }

  if (isShuttingDown() || sessions.size >= maxActiveSessions) {
    incrementMetric(metrics, "sessionCapacityRejections");
    if (!capacityLogState.sessions) logger?.log("SGG-OP-SESSION-CAPACITY-001", sessions.size, maxActiveSessions);
    capacityLogState.sessions = true;
    writeJson(response, 503, jsonRpcError(null, -32002, "Session capacity reached"));
    return;
  }
  capacityLogState.sessions = false;

  const session = createHttpMcpSession(mcpServerFactory, sessionById, sessions, clock.now());
  session.inFlight += 1;
  try {
    session.connecting = true;
    try {
      await session.server.connect(session.transport as unknown as Transport);
    } finally {
      session.connecting = false;
      beginSessionDisposalIfReady(session, sessionById, sessions);
      finishClosedSessionIfReady(session, sessionById, sessions);
    }
    if (session.closing || isShuttingDown()) {
      if (!response.headersSent) writeJson(response, 503, jsonRpcError(null, -32002, "Session capacity reached"));
      return;
    }
    const responseCompleted = waitForResponseCompletion(response, mcpRequestKind(parsedBody), logger);
    await session.transport.handleRequest(request, response, parsedBody);
    requireFinishedResponse(await responseCompleted);
    const initializedSessionId = session.transport.sessionId;
    if (initializedSessionId && !session.closing) {
      session.lastActivityAt = clock.now();
      sessionById.set(initializedSessionId, session);
      return;
    }
  } finally {
    session.inFlight -= 1;
    if (!session.transport.sessionId || session.closing || isShuttingDown()) {
      await requestSessionClose(session, sessionById, sessions, isShuttingDown());
    } else {
      beginSessionDisposalIfReady(session, sessionById, sessions);
    }
  }
}

function createHttpMcpSession(
  mcpServerFactory: HttpMcpServerFactory,
  sessionById: Map<string, HttpMcpSession>,
  sessions: Set<HttpMcpSession>,
  now: number,
): HttpMcpSession {
  const server = mcpServerFactory();
  const transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: () => randomUUID(),
  });
  let resolveClosed!: () => void;
  const closedPromise = new Promise<void>((resolve) => { resolveClosed = resolve; });
  const session: HttpMcpSession = {
    server,
    transport,
    lastActivityAt: now,
    inFlight: 0,
    closing: false,
    connecting: false,
    forceDisposeRequested: false,
    disposeStarted: false,
    disposed: false,
    closedResolved: false,
    closedPromise,
    resolveClosed,
  };
  sessions.add(session);
  transport.onclose = () => {
    const closedSessionId = transport.sessionId;
    if (closedSessionId) {
      sessionById.delete(closedSessionId);
    }
    session.closing = true;
    queueMicrotask(() => {
      beginSessionDisposalIfReady(session, sessionById, sessions);
      finishClosedSessionIfReady(session, sessionById, sessions);
    });
  };
  return session;
}

function parsedBodyContainsInitialize(parsedBody: unknown): boolean {
  return Array.isArray(parsedBody) ? parsedBody.some(isInitializeRequest) : isInitializeRequest(parsedBody);
}

function parsedBodyRequestsPersistentSession(parsedBody: unknown): boolean {
  const initializeRequest = Array.isArray(parsedBody) ? parsedBody.find(isInitializeRequest) : isInitializeRequest(parsedBody) ? parsedBody : undefined;
  if (!initializeRequest || !isRecord(initializeRequest.params.capabilities)) {
    return false;
  }
  const elicitation = initializeRequest.params.capabilities.elicitation;
  return (isRecord(elicitation) && isRecord(elicitation.form))
    || isRecord(initializeRequest.params.capabilities.roots);
}

async function handleStatelessMcpRequest(
  mcpServerFactory: HttpMcpServerFactory,
  request: IncomingMessage,
  response: ServerResponse,
  parsedBody: unknown,
  transientSessions: Set<HttpTransientSession>,
  isShuttingDown: () => boolean,
  logger?: OperationalLogger,
): Promise<void> {
  const server = mcpServerFactory();
  const transport = new StreamableHTTPServerTransport({
    sessionIdGenerator: undefined,
    enableJsonResponse: true,
  } as unknown as ConstructorParameters<typeof StreamableHTTPServerTransport>[0]);
  let resolveClosed!: () => void;
  const closedPromise = new Promise<void>((resolve) => { resolveClosed = resolve; });
  const session: HttpTransientSession = {
    server,
    transport,
    connecting: true,
    closeRequested: false,
    disposeStarted: false,
    closedPromise,
    resolveClosed,
  };
  transientSessions.add(session);
  try {
    try {
      await server.connect(transport as unknown as Transport);
    } finally {
      session.connecting = false;
      beginTransientSessionDisposalIfReady(session, transientSessions);
    }
    if (isShuttingDown()) {
      session.closeRequested = true;
      if (!response.headersSent) writeJson(response, 503, jsonRpcError(null, -32002, "Session capacity reached"));
      return;
    }
    const responseCompleted = waitForResponseCompletion(response, mcpRequestKind(parsedBody), logger);
    await transport.handleRequest(request, response, parsedBody);
    requireFinishedResponse(await responseCompleted);
  } finally {
    await requestTransientSessionClose(session, transientSessions);
  }
}

function requestTransientSessionClose(session: HttpTransientSession, sessions: Set<HttpTransientSession>): Promise<void> {
  session.closeRequested = true;
  beginTransientSessionDisposalIfReady(session, sessions);
  return session.closedPromise;
}

function beginTransientSessionDisposalIfReady(session: HttpTransientSession, sessions: Set<HttpTransientSession>): void {
  if (!session.closeRequested || session.connecting || session.disposeStarted) return;
  session.disposeStarted = true;
  void (async () => {
    try {
      await session.server.close();
    } catch {
      await session.transport.close().catch(() => {});
    } finally {
      sessions.delete(session);
      session.resolveClosed();
    }
  })();
}

function jsonRpcError(id: string | number | null, code: number, message: string, data?: Record<string, unknown>): Record<string, unknown> {
  return {
    jsonrpc: "2.0",
    id,
    error: { code, message, ...(data ? { data } : {}) },
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function readRequestBody(request: IncomingMessage, limits: McpRequestLimits): Promise<string> {
  const declaredLength = Number(request.headers["content-length"]);
  if (Number.isFinite(declaredLength) && declaredLength > limits.maxBodyBytes) {
    throw new HttpRequestError(413, "Request body too large");
  }

  return new Promise((resolve, reject) => {
    let body = "";
    let receivedBytes = 0;
    let settled = false;
    const timeout = setTimeout(() => {
      finish(new HttpRequestError(408, "Request timeout"));
      request.destroy();
    }, limits.requestTimeoutMs);

    const finish = (error?: unknown, value?: string) => {
      if (settled) {
        return;
      }
      settled = true;
      clearTimeout(timeout);
      if (error) {
        reject(error);
        return;
      }
      resolve(value ?? "");
    };

    request.setEncoding("utf8");
    request.on("data", (chunk: string) => {
      receivedBytes += Buffer.byteLength(chunk, "utf8");
      if (receivedBytes > limits.maxBodyBytes) {
        finish(new HttpRequestError(413, "Request body too large"));
        request.destroy();
        return;
      }
      body += chunk;
    });
    request.on("end", () => finish(undefined, body));
    request.on("error", finish);
  });
}

async function readMcpPostBody(request: IncomingMessage, limits: McpRequestLimits): Promise<McpPostBody> {
  const raw = await readRequestBody(request, limits);
  try {
    return { raw, parsed: JSON.parse(raw) as unknown, valid: true };
  } catch {
    return { raw, valid: false };
  }
}

function webRequestFromRawBody(request: IncomingMessage, body: string, signal: AbortSignal): Request {
  const headers = new Headers();
  for (let index = 0; index < request.rawHeaders.length; index += 2) {
    headers.append(request.rawHeaders[index] ?? "", request.rawHeaders[index + 1] ?? "");
  }
  return new Request(new URL(request.url ?? "/", `http://${request.headers.host ?? "127.0.0.1"}`), {
    method: request.method ?? "POST",
    headers,
    body,
    signal,
  });
}

async function writeWebResponse(response: ServerResponse, webResponse: Response): Promise<void> {
  response.statusCode = webResponse.status;
  for (const [name, value] of webResponse.headers) response.setHeader(name, value);
  if (!webResponse.body) {
    response.end();
    return;
  }
  const reader = webResponse.body.getReader();
  const cancelReader = () => { void reader.cancel().catch(() => undefined); };
  response.once("close", cancelReader);
  try {
    if (response.destroyed) {
      await reader.cancel().catch(() => undefined);
      return;
    }
    while (!response.destroyed) {
      const chunk = await reader.read();
      if (chunk.done) break;
      await new Promise<void>((resolve, reject) => {
        response.write(Buffer.from(chunk.value), (error) => error ? reject(error) : resolve());
      });
    }
    if (!response.destroyed) response.end();
  } finally {
    response.off("close", cancelReader);
    reader.releaseLock();
  }
}

type McpHttpRequestKind = "initialize" | "tools_call" | "batch" | "other";
type McpHttpResponseState = "finished" | "closed";

function waitForResponseCompletion(response: ServerResponse, requestKind: McpHttpRequestKind, logger?: OperationalLogger): Promise<McpHttpResponseState> {
  if (response.writableFinished) {
    logMcpHttpResponse(response, requestKind, "finished", 0, logger);
    return Promise.resolve("finished");
  }
  if (response.destroyed) {
    logMcpHttpResponse(response, requestKind, "closed", 0, logger);
    return Promise.resolve("closed");
  }
  const bodyCounter = logger ? observeResponseBodyBytes(response) : { bytes: () => 0, restore: () => {} };
  return new Promise((resolve) => {
    const finish = () => settle("finished");
    const close = () => settle(response.writableFinished ? "finished" : "closed");
    const settle = (state: McpHttpResponseState) => {
      response.off("finish", finish);
      response.off("close", close);
      bodyCounter.restore();
      logMcpHttpResponse(response, requestKind, state, bodyCounter.bytes(), logger);
      resolve(state);
    };
    response.once("finish", finish);
    response.once("close", close);
  });
}

function requireFinishedResponse(state: McpHttpResponseState): void {
  if (state !== "finished") throw new Error("mcp_http_response_closed_before_finish");
}

function logMcpHttpResponse(response: ServerResponse, requestKind: McpHttpRequestKind, state: McpHttpResponseState, bodyBytes: number, logger?: OperationalLogger): void {
  const rawLength = response.getHeader("content-length");
  const parsedLength = typeof rawLength === "number" ? rawLength : typeof rawLength === "string" && /^\d+$/.test(rawLength) ? Number(rawLength) : undefined;
  const validLength = parsedLength !== undefined && Number.isSafeInteger(parsedLength) && parsedLength >= 0 ? parsedLength : undefined;
  const completedBodyBytes = state === "finished" ? bodyBytes : 0;
  logger?.log("SGG-OP-MCP-HTTP-RESPONSE-001", requestKind, state, response.headersSent ? response.statusCode : 0, response.headersSent, validLength !== undefined, state === "finished" && (validLength === undefined || validLength === completedBodyBytes), bodyBytes, completedBodyBytes);
}

function observeResponseBodyBytes(response: ServerResponse): { bytes: () => number; restore: () => void } {
  const originalWrite = response.write;
  const originalEnd = response.end;
  let bodyBytes = 0;
  response.write = ((chunk: unknown, ...args: unknown[]) => {
    const written = Reflect.apply(originalWrite, response, [chunk, ...args]) as boolean;
    bodyBytes += responseChunkBytes(chunk, args[0]);
    return written;
  }) as typeof response.write;
  response.end = ((chunk?: unknown, ...args: unknown[]) => {
    const ended = Reflect.apply(originalEnd, response, [chunk, ...args]) as ServerResponse;
    bodyBytes += responseChunkBytes(chunk, args[0]);
    return ended;
  }) as typeof response.end;
  return {
    bytes: () => bodyBytes,
    restore: () => {
      response.write = originalWrite;
      response.end = originalEnd;
    },
  };
}

function responseChunkBytes(chunk: unknown, encoding: unknown): number {
  if (typeof chunk === "string") return Buffer.byteLength(chunk, typeof encoding === "string" && Buffer.isEncoding(encoding) ? encoding : "utf8");
  if (Buffer.isBuffer(chunk)) return chunk.byteLength;
  if (ArrayBuffer.isView(chunk)) return chunk.byteLength;
  if (chunk instanceof ArrayBuffer) return chunk.byteLength;
  return 0;
}

function mcpRequestKind(body: unknown): McpHttpRequestKind {
  if (Array.isArray(body)) return "batch";
  if (!isRecord(body)) return "other";
  if (body.method === "initialize") return "initialize";
  return body.method === "tools/call" ? "tools_call" : "other";
}

function isAllowedOrigin(request: IncomingMessage, allowedOrigins: string[]): boolean {
  const origin = singleHeader(request.headers.origin);
  if (!origin) {
    return true;
  }

  if (allowedOrigins.includes(origin)) {
    return true;
  }

  if (allowedOrigins.length > 0) {
    return false;
  }

  return isLoopbackOrigin(origin);
}

function consumeRateLimit(
  request: IncomingMessage,
  buckets: Map<string, RateLimitBucket>,
  limit: { windowMs: number; maxRequests: number; maxBuckets: number; trustedProxy: boolean },
  trustedProxyPeers: ReadonlySet<string>,
  now: number,
  metrics: HttpOperationalMetrics,
  capacityLogState: { rateLimit: boolean },
  logger?: OperationalLogger,
): boolean {
  const key = rateLimitIdentity(request, limit.trustedProxy, trustedProxyPeers);
  if (!key) return false;
  const bucket = buckets.get(key);
  if (!bucket || bucket.resetAt <= now) {
    if (!bucket && buckets.size >= limit.maxBuckets) {
      expireRateLimitBuckets(buckets, now, metrics, logger);
      if (buckets.size >= limit.maxBuckets) {
        incrementMetric(metrics, "rateLimitCapacityRejections");
        if (!capacityLogState.rateLimit) logger?.log("SGG-OP-RATE-CAPACITY-001", buckets.size, limit.maxBuckets);
        capacityLogState.rateLimit = true;
        return false;
      }
    }
    buckets.set(key, { resetAt: now + limit.windowMs, count: 1 });
    capacityLogState.rateLimit = false;
    return true;
  }

  bucket.count += 1;
  return bucket.count <= limit.maxRequests;
}

function rateLimitIdentity(request: IncomingMessage, trustedProxy: boolean, trustedProxyPeers: ReadonlySet<string>): string | undefined {
  const remoteAddress = canonicalizeIpAddress(request.socket.remoteAddress ?? "");
  if (!remoteAddress) return undefined;
  if (!trustedProxy) return remoteAddress;
  if (!trustedProxyPeers.has(remoteAddress)) return undefined;

  const forwarded = strictHeader(request, "forwarded");
  const xForwardedFor = strictHeader(request, "x-forwarded-for");
  if (forwarded.invalid || forwarded.value !== undefined || xForwardedFor.invalid) return undefined;
  if (xForwardedFor.value === undefined) return remoteAddress;
  return canonicalizeIpAddress(xForwardedFor.value);
}

function expireRateLimitBuckets(buckets: Map<string, RateLimitBucket>, now: number, metrics: HttpOperationalMetrics, logger?: OperationalLogger): void {
  let expired = 0;
  for (const [key, bucket] of buckets) {
    if (bucket.resetAt <= now) {
      buckets.delete(key);
      incrementMetric(metrics, "expiredRateLimitBuckets");
      expired += 1;
    }
  }
  if (expired > 0) logger?.log("SGG-OP-RATE-CLEANUP-001", expired, buckets.size);
}

async function expireIdleSessions(
  sessionById: Map<string, HttpMcpSession>,
  sessions: Set<HttpMcpSession>,
  idleTtlMs: number,
  now: number,
  metrics: HttpOperationalMetrics,
  logger?: OperationalLogger,
): Promise<void> {
  const expired = [...sessions].filter((session) => !session.closing && session.inFlight === 0 && session.lastActivityAt + idleTtlMs <= now);
  let transitioned = 0;
  for (const session of expired) {
    if (session.closing || session.inFlight !== 0 || session.lastActivityAt + idleTtlMs > now) continue;
    await requestSessionClose(session, sessionById, sessions);
    incrementMetric(metrics, "expiredSessions");
    transitioned += 1;
  }
  if (transitioned > 0) logger?.log("SGG-OP-SESSION-CLEANUP-001", transitioned, sessions.size);
}

function requestSessionClose(
  session: HttpMcpSession,
  sessionById: Map<string, HttpMcpSession>,
  sessions: Set<HttpMcpSession>,
  forceDispose = false,
): Promise<void> {
  session.closing = true;
  const sessionId = session.transport.sessionId;
  if (sessionId) sessionById.delete(sessionId);
  session.forceDisposeRequested ||= forceDispose;
  beginSessionDisposalIfReady(session, sessionById, sessions);
  finishClosedSessionIfReady(session, sessionById, sessions);
  return session.closedPromise;
}

function beginSessionDisposalIfReady(
  session: HttpMcpSession,
  sessionById: Map<string, HttpMcpSession>,
  sessions: Set<HttpMcpSession>,
): void {
  if (
    !session.closing
    || session.disposeStarted
    || session.connecting
    || (!session.forceDisposeRequested && session.inFlight !== 0)
  ) return;
  session.disposeStarted = true;
  void (async () => {
    try {
      await session.server.close();
    } catch {
      await session.transport.close().catch(() => {});
    } finally {
      session.disposed = true;
      finishClosedSessionIfReady(session, sessionById, sessions);
    }
  })();
}

function finishClosedSessionIfReady(session: HttpMcpSession, sessionById: Map<string, HttpMcpSession>, sessions: Set<HttpMcpSession>): void {
  if (!session.disposed || session.inFlight !== 0 || session.closedResolved) return;
  const sessionId = session.transport.sessionId;
  if (sessionId) sessionById.delete(sessionId);
  sessions.delete(session);
  session.closedResolved = true;
  session.resolveClosed();
}

function settleByDeadline(promises: Promise<unknown>[], timeoutMs: number, clock: HttpMcpClock): Promise<void> {
  return new Promise((resolve) => {
    let settled = false;
    let timer: HttpMcpTimer | undefined;
    const finish = () => {
      if (settled) return;
      settled = true;
      if (timer) clock.clearTimeout(timer);
      resolve();
    };
    timer = clock.setTimeout(finish, timeoutMs);
    timer.unref?.();
    void Promise.allSettled(promises).then(finish, finish);
  });
}

function incrementMetric(metrics: HttpOperationalMetrics, key: keyof HttpOperationalMetrics): void {
  metrics[key] = Math.min(Number.MAX_SAFE_INTEGER, metrics[key] + 1);
}

function assertOperationalLimits(
  sessions: { maxActive: number; idleTtlMs: number; cleanupIntervalMs: number },
  rateLimit: { windowMs: number; maxRequests: number; maxBuckets: number; cleanupIntervalMs: number; trustedProxy: boolean; trustedProxyPeers: string[] },
  shutdownTimeoutMs: number,
): void {
  const values = [sessions.maxActive, sessions.idleTtlMs, sessions.cleanupIntervalMs, rateLimit.windowMs, rateLimit.maxRequests, rateLimit.maxBuckets, rateLimit.cleanupIntervalMs, shutdownTimeoutMs];
  if (values.some((value) => !Number.isSafeInteger(value) || value <= 0)) {
    throw new Error("HTTP MCP operational limits must be positive safe integers");
  }
  if (
    sessions.maxActive > MAX_ACTIVE_SESSIONS
    || sessions.idleTtlMs > MAX_SESSION_IDLE_TTL_MS
    || sessions.cleanupIntervalMs > MAX_CLEANUP_INTERVAL_MS
    || rateLimit.windowMs > MAX_RATE_LIMIT_WINDOW_MS
    || rateLimit.maxRequests > MAX_RATE_LIMIT_REQUESTS
    || rateLimit.maxBuckets > MAX_RATE_LIMIT_BUCKETS
    || rateLimit.cleanupIntervalMs > MAX_CLEANUP_INTERVAL_MS
    || shutdownTimeoutMs > MAX_SHUTDOWN_TIMEOUT_MS
  ) {
    throw new Error("HTTP MCP operational limits exceed safe maximums");
  }
  const canonicalPeers = rateLimit.trustedProxyPeers.map(canonicalizeIpAddress);
  if (rateLimit.trustedProxy && canonicalPeers.length === 0) {
    throw new Error("Trusted proxy mode requires at least one trusted proxy peer");
  }
  if (canonicalPeers.some((peer, index) => !peer || peer !== rateLimit.trustedProxyPeers[index]) || new Set(canonicalPeers).size !== canonicalPeers.length) {
    throw new Error("Trusted proxy peers must be unique canonical IP addresses");
  }
  if (canonicalPeers.length > MAX_TRUSTED_PROXY_PEERS) {
    throw new Error("Trusted proxy peer count exceeds safe maximum");
  }
}

function assertSafeBinding(options: HttpMcpServerOptions): void {
  if (isLoopbackHost(options.host)) {
    return;
  }

  if (!options.allowNonLoopback) {
    throw new Error("Refusing non-loopback MCP bind without allow_non_loopback=true");
  }
  if (!options.token) {
    throw new Error("Refusing non-loopback MCP bind without token");
  }
  if (!options.allowedOrigins || options.allowedOrigins.length === 0) {
    throw new Error("Refusing non-loopback MCP bind without allowed origins");
  }
}

function isLoopbackHost(host: string): boolean {
  const normalized = host.toLowerCase();
  return normalized === "localhost" || normalized === "127.0.0.1" || normalized === "::1" || normalized === "[::1]";
}

function isLoopbackOrigin(origin: string): boolean {
  try {
    const url = new URL(origin);
    return (url.protocol === "http:" || url.protocol === "https:") && isLoopbackHost(url.hostname);
  } catch {
    return false;
  }
}

function createMcpHostValidation(options: HttpMcpServerOptions): (request: IncomingMessage, response: ServerResponse) => boolean {
  if (!isWildcardHost(options.host)) return hostHeaderValidation(allowedMcpHostnames(options));
  return (request, response) => {
    const localAddress = canonicalizeIpAddress(request.socket.localAddress ?? "");
    const allowed = allowedMcpHostnames(options);
    if (localAddress) allowed.push(bracketIpv6(localAddress));
    return hostHeaderValidation([...new Set(allowed)])(request, response);
  };
}

function allowedMcpHostnames(options: HttpMcpServerOptions): string[] {
  if (isLoopbackHost(options.host)) return ["localhost", "127.0.0.1", "[::1]"];
  const hostnames = new Set<string>();
  if (!isWildcardHost(options.host)) hostnames.add(bracketIpv6(options.host.toLowerCase()));
  for (const origin of options.allowedOrigins ?? []) {
    try {
      hostnames.add(bracketIpv6(new URL(origin).hostname.toLowerCase()));
    } catch {
      // Invalid configured origins are denied by the existing exact Origin gate.
    }
  }
  return [...hostnames];
}

function requestHasBody(request: IncomingMessage): boolean {
  const contentLength = strictHeader(request, "content-length");
  const transferEncoding = strictHeader(request, "transfer-encoding");
  if (contentLength.invalid || transferEncoding.invalid) return true;
  if (transferEncoding.value !== undefined) return true;
  return contentLength.value !== undefined && contentLength.value !== "0";
}

function isWildcardHost(host: string): boolean {
  return host === "0.0.0.0" || host === "::" || host === "[::]";
}

function bracketIpv6(host: string): string {
  return host.includes(":") && !host.startsWith("[") ? `[${host}]` : host;
}

function singleHeader(value: string | string[] | undefined): string | undefined {
  return Array.isArray(value) ? value[0] : value;
}

function strictHeader(request: IncomingMessage, name: string): { value?: string; invalid: boolean } {
  const values: string[] = [];
  for (let index = 0; index < request.rawHeaders.length; index += 2) {
    if (request.rawHeaders[index]?.toLowerCase() === name) {
      values.push(request.rawHeaders[index + 1] ?? "");
    }
  }
  if (values.length === 0) {
    return { invalid: false };
  }
  const value = values[0];
  if (values.length !== 1 || !value || value !== value.trim() || value.includes(",")) {
    return { invalid: true };
  }
  return { value, invalid: false };
}

function isSessionId(value: string): boolean {
  return /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i.test(value);
}

function matchesPath(request: IncomingMessage, expectedPath: string): boolean {
  const requestUrl = new URL(request.url ?? "/", `http://${request.headers.host ?? "127.0.0.1"}`);
  return requestUrl.pathname === expectedPath;
}

function isAuthorized(request: IncomingMessage, token: string | undefined): boolean {
  if (!token) {
    return true;
  }

  const value = request.headers[TOKEN_HEADER];
  return (Array.isArray(value) ? value[0] : value) === token;
}

function readHealth(
  options: HttpMcpServerOptions,
  sessions: Set<HttpMcpSession>,
  transientSessions: Set<HttpTransientSession>,
  modernRequestsInFlight: number,
  rateLimitBuckets: Map<string, RateLimitBucket>,
  maxActiveSessions: number,
  maxRateLimitBuckets: number,
  metrics: HttpOperationalMetrics,
): Record<string, unknown> {
  const operational = {
    active_sessions: sessions.size,
    legacy_sessions_active: sessions.size,
    closing_sessions: [...sessions].filter((session) => session.closing).length,
    in_flight_session_requests: [...sessions].reduce((total, session) => total + session.inFlight, 0),
    active_stateless_requests: transientSessions.size,
    legacy_stateless_requests_active: transientSessions.size,
    modern_requests_in_flight: modernRequestsInFlight,
    max_active_sessions: maxActiveSessions,
    expired_sessions_total: metrics.expiredSessions,
    session_capacity_rejections_total: metrics.sessionCapacityRejections,
    rate_limit_buckets: rateLimitBuckets.size,
    max_rate_limit_buckets: maxRateLimitBuckets,
    expired_rate_limit_buckets_total: metrics.expiredRateLimitBuckets,
    rate_limit_capacity_rejections_total: metrics.rateLimitCapacityRejections,
  };
  try {
    return { ok: true, ...(options.health?.() ?? {}), http_mcp: operational };
  } catch {
    return {
      ok: true,
      health_error: "health_unavailable",
      http_mcp: operational,
    };
  }
}

function incrementCounter(value: number): number {
  return Math.min(Number.MAX_SAFE_INTEGER, value + 1);
}

const systemClock: HttpMcpClock = {
  now: Date.now,
  setInterval: (callback, delayMs) => setInterval(callback, delayMs),
  clearInterval: (timer) => clearInterval(timer as NodeJS.Timeout),
  setTimeout: (callback, delayMs) => setTimeout(callback, delayMs),
  clearTimeout: (timer) => clearTimeout(timer as NodeJS.Timeout),
};

function writeJson(response: ServerResponse, statusCode: number, body: Record<string, unknown>): void {
  response.writeHead(statusCode, { "content-type": "application/json" });
  response.end(JSON.stringify(body));
}

function listen(server: http.Server, host: string, port: number): Promise<void> {
  return new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen({ host, port }, () => {
      server.off("error", reject);
      resolve();
    });
  });
}

function closeHttpServer(server: http.Server): Promise<void> {
  return new Promise((resolve, reject) => {
    server.close((error) => {
      if (error) {
        reject(error);
        return;
      }
      resolve();
    });
  });
}
