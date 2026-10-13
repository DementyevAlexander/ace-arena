import { AsyncLocalStorage } from "node:async_hooks";

import type { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { RequestHandlerExtra } from "@modelcontextprotocol/sdk/shared/protocol.js";
import type { FetchLike, Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import {
  ElicitResultSchema, ErrorCode, McpError,
  type ClientNotification, type ClientRequest, type ElicitRequest, type ElicitResult, type RequestId,
} from "@modelcontextprotocol/sdk/types.js";

import type { UnityMcpToolExtra } from "./sdk-server.js";

export const STDIO_ELICITATION_TIMEOUT_MS = 30_000;
export const OPENCODE_SESSION_META_MAX_BYTES = 1_024;
const FORM_REQUEST_MAX_BYTES = 64 * 1024;
const MAX_ACTIVE_RELAY_CALLS = 128;
const outerFormCapabilities = new WeakMap<McpServer, boolean>();

/** SDK 1.x normalizes elicitation:{} to form:{}, so capture the actual declaration. */
export function captureStdioOuterFormCapability(server: McpServer, inner: Transport): Transport {
  outerFormCapabilities.set(server, false);
  let initialized = false;
  const transport: Transport = {
    async start() {
      inner.onmessage = (message, extra) => {
        if (!initialized && "method" in message && message.method === "initialize") {
          initialized = true;
          const capabilities = message.params?.capabilities as ClientCapabilitiesForCapture | undefined;
          const form = capabilities?.elicitation?.form;
          outerFormCapabilities.set(server, typeof form === "object" && form !== null && !Array.isArray(form));
        }
        transport.onmessage?.(message, extra);
      };
      inner.onerror = (error) => transport.onerror?.(error);
      inner.onclose = () => transport.onclose?.();
      await inner.start();
    },
    async send(message, options) { await inner.send(message, options); },
    async close() { await inner.close(); },
    setProtocolVersion(version) { inner.setProtocolVersion?.(version); },
  };
  Object.defineProperty(transport, "sessionId", { get: () => inner.sessionId });
  return transport;
}

type ClientCapabilitiesForCapture = { elicitation?: { form?: unknown } };

/** Opaque UI routing data, not approval identity or authorization. Never persisted. */
export function boundedOpenCodeSessionMeta(meta: unknown): { "ai.opencode/sessionID": string } | undefined {
  if (!meta || typeof meta !== "object") return undefined;
  const descriptor = Object.getOwnPropertyDescriptor(meta, "ai.opencode/sessionID");
  const session = descriptor && "value" in descriptor ? descriptor.value : undefined;
  return typeof session === "string" && session.length > 0 && Buffer.byteLength(session, "utf8") <= OPENCODE_SESSION_META_MAX_BYTES
    ? { "ai.opencode/sessionID": session }
    : undefined;
}

export function hasOuterFormElicitation(server: McpServer | undefined): boolean {
  if (server && outerFormCapabilities.get(server) === false) return false;
  const form = server?.server.getClientCapabilities()?.elicitation?.form;
  return typeof form === "object" && form !== null && !Array.isArray(form);
}

type CallScope = {
  outer: UnityMcpToolExtra;
  active: boolean;
  abort: AbortController;
  innerRequestId?: RequestId;
  promptSeen: boolean;
  elicitationId?: RequestId;
};

/**
 * SDK 1.x delivers each POST response stream from that transport.send()'s async
 * context, but its incoming request extra has no parent request ID. Bind at the
 * public transport boundary instead of guessing from metadata or current calls.
 * Standalone GET events and resumed/replayed requests are deliberately unbound.
 */
export class StdioFormElicitationRelay {
  readonly #contexts = new AsyncLocalStorage<CallScope>();
  readonly #active = new Set<CallScope>();
  readonly #bindings = new Map<RequestId, CallScope>();
  readonly #incomingIds = new Set<RequestId>();
  readonly #outerServer: () => McpServer | undefined;
  readonly #timeout: number;
  #closed = false;

  constructor(outerServer: () => McpServer | undefined, timeoutMs = STDIO_ELICITATION_TIMEOUT_MS) {
    this.#outerServer = outerServer;
    this.#timeout = Number.isSafeInteger(timeoutMs) ? Math.max(1, Math.min(timeoutMs, STDIO_ELICITATION_TIMEOUT_MS)) : STDIO_ELICITATION_TIMEOUT_MS;
  }

  async runCall<T>(outer: UnityMcpToolExtra | undefined, call: () => Promise<T>): Promise<T> {
    if (!outer || outer.taskId !== undefined || this.#closed || this.#active.size >= MAX_ACTIVE_RELAY_CALLS) return await this.#contexts.exit(call);
    const scope: CallScope = { outer, active: true, abort: new AbortController(), promptSeen: false };
    this.#active.add(scope);
    try {
      return await this.#contexts.run(scope, call);
    } finally {
      scope.active = false;
      scope.abort.abort();
      this.#active.delete(scope);
      if (scope.elicitationId !== undefined) {
        this.#bindings.delete(scope.elicitationId);
        this.#incomingIds.delete(scope.elicitationId);
      }
    }
  }

  wrapFetch(fetch: FetchLike): FetchLike {
    return async (url, init) => {
      const scope = this.#contexts.getStore();
      const response = await fetch(url, init);
      if (!scope || init?.method !== "POST" || !response.ok || !response.body || !response.headers.get("content-type")?.includes("text/event-stream")) return response;
      const reader = response.body.getReader();
      // SDK 1.x does not report a graceful POST SSE EOF without a priming ID.
      // Observe the public response stream so a late UI acceptance is rejected.
      const body = new ReadableStream<Uint8Array>({
        async pull(controller) {
          try {
            const chunk = await reader.read();
            if (chunk.done) { scope.abort.abort(); controller.close(); }
            else controller.enqueue(chunk.value);
          } catch (error) {
            scope.abort.abort();
            controller.error(error);
          }
        },
        async cancel(reason) { scope.abort.abort(); await reader.cancel(reason); },
      });
      return new Response(body, { status: response.status, statusText: response.statusText, headers: response.headers });
    };
  }

  wrapTransport(inner: Transport): Transport {
    const relay = this;
    const transport: Transport = {
      async start() {
        inner.onmessage = (message, extra) => {
          if ("method" in message && message.method === "elicitation/create" && "id" in message) {
            const scope = relay.#contexts.getStore();
            if (relay.#incomingIds.has(message.id)) return; // Never repeat a displayed prompt.
            if (scope?.active && scope.innerRequestId !== undefined && !scope.abort.signal.aborted && !scope.promptSeen) {
              scope.promptSeen = true;
              scope.elicitationId = message.id;
              relay.#incomingIds.add(message.id);
              relay.#bindings.set(message.id, scope);
            }
          }
          transport.onmessage?.(message, extra);
        };
        inner.onerror = (error) => {
          // A lost POST/SSE stream cannot return an accepted approval later.
          relay.#abortPrompts();
          transport.onerror?.(error);
        };
        inner.onclose = () => { relay.close(); transport.onclose?.(); };
        await inner.start();
      },
      async send(message, options) {
        const scope = relay.#contexts.getStore();
        if (scope && "method" in message && message.method === "tools/call" && "id" in message) {
          if (!scope.active || scope.innerRequestId !== undefined || options?.resumptionToken || options?.relatedRequestId !== scope.outer.requestId) {
            throw unavailable();
          }
          scope.innerRequestId = message.id;
        }
        await inner.send(message, options);
      },
      async close() { relay.close(); await inner.close(); },
      setProtocolVersion(version) { inner.setProtocolVersion?.(version); },
    };
    Object.defineProperty(transport, "sessionId", { get: () => inner.sessionId });
    return transport;
  }

  async forward(request: ElicitRequest, extra: RequestHandlerExtra<ClientRequest, ClientNotification>): Promise<ElicitResult> {
    const scope = this.#bindings.get(extra.requestId);
    this.#bindings.delete(extra.requestId);
    if (!scope || !scope.active || this.#closed || !hasOuterFormElicitation(this.#outerServer()) || extra.taskId !== undefined || request.params.mode === "url" || request.params.task) throw unavailable();
    const signal = AbortSignal.any([scope.outer.signal, extra.signal, scope.abort.signal, AbortSignal.timeout(this.#timeout)]);
    signal.throwIfAborted();
    const params = {
      mode: "form" as const,
      message: request.params.message,
      requestedSchema: request.params.requestedSchema,
      ...metadataParam(scope.outer._meta),
    };
    if (Buffer.byteLength(JSON.stringify(params), "utf8") > FORM_REQUEST_MAX_BYTES) throw unavailable();
    // The outer SDK sendRequest contract binds to this exact outer request ID.
    const result = await scope.outer.sendRequest({ method: "elicitation/create", params }, ElicitResultSchema, {
      timeout: this.#timeout,
      maxTotalTimeout: this.#timeout,
      signal,
    });
    signal.throwIfAborted();
    if (!scope.active || this.#closed) throw unavailable();
    // No defaults or auto-approval; preserve false/decline/cancel exactly.
    return { action: result.action, ...(result.content ? { content: result.content } : {}) };
  }

  close(): void {
    this.#closed = true;
    this.#abortPrompts();
    this.#bindings.clear();
    this.#incomingIds.clear();
  }

  #abortPrompts(): void {
    for (const scope of this.#active) scope.abort.abort();
  }
}

function metadataParam(meta: unknown): { _meta?: { "ai.opencode/sessionID": string } } {
  const bounded = boundedOpenCodeSessionMeta(meta);
  return bounded ? { _meta: bounded } : {};
}

function unavailable(): McpError {
  return new McpError(ErrorCode.InvalidRequest, "The stdio form elicitation relay is unavailable or not bound to an active tool call; use the Gateway pending approval fallback.");
}
