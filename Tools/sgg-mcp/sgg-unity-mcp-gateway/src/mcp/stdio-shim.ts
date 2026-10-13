import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport, StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import type { Transport } from "@modelcontextprotocol/sdk/shared/transport.js";
import {
  ElicitRequestSchema,
  ListRootsRequestSchema,
  ListRootsResultSchema,
  RootsListChangedNotificationSchema,
  type CallToolResult,
  type Root,
} from "@modelcontextprotocol/sdk/types.js";

import { GATEWAY_MCP_SERVER_NAME, STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS, gatewayResultToMcpToolResult, registerUnityMcpTools, type UnityMcpBeforeTool, type UnityMcpToolExtra } from "./sdk-server.js";
import { CANONICAL_TEXT_MAX_BYTES, canonicalTextProjection } from "./canonical-text-projection.js";
import { boundedOpenCodeSessionMeta, captureStdioOuterFormCapability, hasOuterFormElicitation, StdioFormElicitationRelay } from "./stdio-elicitation-relay.js";

export const STDIO_SHIM_CLIENT_NAME = "sgg-unity-mcp-stdio-shim";
export const STDIO_SHIM_PROTOCOL_POSTURE = "legacy_only" as const;
export const STDIO_SHIM_LEGACY_TEXT_MAX_BYTES = CANONICAL_TEXT_MAX_BYTES;
const STALE_SESSION_ERROR_MAX_BYTES = 16 * 1024;
const STALE_SESSION_ERROR_PREFIX = "Streamable HTTP error: Error POSTing to endpoint: ";

export type StdioShimOptions = {
  url: string;
  version: string;
  token?: string;
  legacyTextResult?: boolean;
};

type RemoteToolCallArguments = [...Parameters<Client["callTool"]>, outerCall?: UnityMcpToolExtra];

export type RemoteToolClient = {
  callTool(...args: RemoteToolCallArguments): Promise<CallToolResult>;
};

type StdioShimCallLifecycle = (call: () => Promise<CallToolResult>) => Promise<CallToolResult>;

export type StdioShimRemoteGeneration = {
  client: RemoteToolClient;
  notifyRootsChanged: () => Promise<void>;
  close: (terminateSession: boolean) => Promise<void>;
};

class StdioShimServer extends McpServer {
  override async connect(transport: Transport): Promise<void> {
    await super.connect(captureStdioOuterFormCapability(this, transport));
  }
}

export class ReconnectableStdioShimClient implements RemoteToolClient {
  readonly #factory: () => Promise<StdioShimRemoteGeneration>;
  #current: { id: number; valid: boolean; remote: StdioShimRemoteGeneration } | undefined;
  #replacement: Promise<{ id: number; valid: boolean; remote: StdioShimRemoteGeneration }> | undefined;
  #nextGeneration = 0;
  #closed = false;

  constructor(factory: () => Promise<StdioShimRemoteGeneration>) {
    this.#factory = factory;
  }

  async connect(): Promise<void> {
    await this.#getGeneration();
  }

  async callTool(...args: RemoteToolCallArguments): Promise<CallToolResult> {
    const generation = await this.#getGeneration();
    try {
      return await generation.remote.client.callTool(...args);
    } catch (error) {
      const staleResult = staleSessionToolResult(error, boundedString(args[0].arguments?.request_id));
      if (!staleResult) throw error;
      if (this.#current === generation) generation.valid = false;
      return staleResult;
    }
  }

  async notifyRootsChanged(): Promise<void> {
    const generation = this.#current;
    if (!generation?.valid || this.#closed) return;
    await generation.remote.notifyRootsChanged();
  }

  async close(): Promise<void> {
    if (this.#closed) return;
    this.#closed = true;
    await this.#replacement?.catch(() => {});
    const generation = this.#current;
    this.#current = undefined;
    if (generation) await generation.remote.close(generation.valid);
  }

  async #getGeneration(): Promise<{ id: number; valid: boolean; remote: StdioShimRemoteGeneration }> {
    if (this.#closed) throw new Error("stdio-shim remote client is closed");
    if (this.#replacement) return await this.#replacement;
    if (this.#current?.valid) return this.#current;

    const previous = this.#current;
    const replacement = this.#replaceGeneration(previous);
    this.#replacement = replacement;
    try {
      return await replacement;
    } finally {
      if (this.#replacement === replacement) this.#replacement = undefined;
    }
  }

  async #replaceGeneration(previous: { id: number; valid: boolean; remote: StdioShimRemoteGeneration } | undefined): Promise<{ id: number; valid: boolean; remote: StdioShimRemoteGeneration }> {
    if (this.#current === previous) this.#current = undefined;
    if (previous) await previous.remote.close(false);
    if (this.#closed) throw new Error("stdio-shim remote client is closed");

    const remote = await this.#factory();
    if (this.#closed) {
      await remote.close(false);
      throw new Error("stdio-shim remote client is closed");
    }

    const generation = { id: ++this.#nextGeneration, valid: true, remote };
    this.#current = generation;
    await remote.notifyRootsChanged().catch(() => {});
    if (this.#closed) {
      if (this.#current === generation) this.#current = undefined;
      await remote.close(false);
      throw new Error("stdio-shim remote client is closed");
    }
    return generation;
  }
}

export function createStdioShimServer(remoteClient: RemoteToolClient, options: { version: string; beforeRemoteCall?: UnityMcpBeforeTool; runAcceptedCall?: StdioShimCallLifecycle; legacyTextResult?: boolean }): McpServer {
  const server = new StdioShimServer({
    name: `${GATEWAY_MCP_SERVER_NAME}-stdio-shim`,
    title: "SGG Unity MCP Gateway stdio shim",
    version: options.version,
  });

  registerStdioShimTools(server, remoteClient, options.beforeRemoteCall, options.runAcceptedCall, options.legacyTextResult);

  return server;
}

export function registerStdioShimTools(server: McpServer, remoteClient: RemoteToolClient, beforeRemoteCall?: UnityMcpBeforeTool, runAcceptedCall?: StdioShimCallLifecycle, legacyTextResult = false): void {
  registerUnityMcpTools(server, {}, async (toolName, args, extra) => await (runAcceptedCall ?? runStdioShimCall)(async () => {
    await beforeRemoteCall?.(extra);
    const uiMeta = boundedOpenCodeSessionMeta(extra._meta);
    const result = await remoteClient.callTool({
      name: toolName,
      arguments: toRecord(args),
      ...(uiMeta ? { _meta: uiMeta } : {}),
    }, undefined, {
      timeout: STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS,
      maxTotalTimeout: STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS,
      signal: extra.signal,
      relatedRequestId: extra.requestId,
    }, extra);
    return legacyTextResult ? legacyTextCompatibleToolResult(result) : result;
  }));
}

async function runStdioShimCall(call: () => Promise<CallToolResult>): Promise<CallToolResult> {
  return await call();
}

export async function runStdioShim(options: StdioShimOptions): Promise<void> {
  const relayedRoots: { current: Root[] } = { current: [] };
  let server: McpServer;
  // Do not initialize HTTP before the outer client's capabilities are known.
  const remoteClient = new ReconnectableStdioShimClient(async () => await createHttpStdioShimGeneration(options, relayedRoots, () => server));

  let activeAcceptedCalls = 0;
  let resolveAcceptedCallsDrained: (() => void) | undefined;
  const runAcceptedCall: StdioShimCallLifecycle = async (call) => {
    activeAcceptedCalls++;
    try {
      return await call();
    } finally {
      activeAcceptedCalls--;
      if (activeAcceptedCalls === 0) {
        resolveAcceptedCallsDrained?.();
        resolveAcceptedCallsDrained = undefined;
      }
    }
  };
  const waitForAcceptedCallsDrained = async (): Promise<void> => {
    if (activeAcceptedCalls === 0) return;
    await new Promise<void>((resolve) => { resolveAcceptedCallsDrained = resolve; });
  };

  let rootsDirty = true;
  let rootsRetryAt = 0;
  let rootsGeneration = 0;
  const refreshOuterRoots: UnityMcpBeforeTool = async (extra) => {
    if (!rootsDirty) return;
    if (Date.now() < rootsRetryAt) return;
    const generation = rootsGeneration;
    if (!server.server.getClientCapabilities()?.roots) {
      relayedRoots.current = [];
      rootsDirty = false;
      return;
    }
    try {
      const result = await extra.sendRequest({ method: "roots/list" }, ListRootsResultSchema, {
        timeout: 1_000,
        signal: extra.signal,
      });
      if (generation !== rootsGeneration) return;
      relayedRoots.current = result.roots
        .filter((root) => typeof root.uri === "string" && root.uri.length > 0 && root.uri.length <= 2_048)
        .slice(0, 8);
      rootsDirty = false;
      await remoteClient.notifyRootsChanged();
    } catch {
      if (generation !== rootsGeneration) return;
      relayedRoots.current = [];
      rootsDirty = true;
      rootsRetryAt = Date.now() + 30_000;
    }
  };
  server = createStdioShimServer(remoteClient, {
    version: options.version,
    beforeRemoteCall: refreshOuterRoots,
    runAcceptedCall,
    ...(options.legacyTextResult ? { legacyTextResult: true } : {}),
  });
  server.server.setNotificationHandler(RootsListChangedNotificationSchema, async () => {
    if (server.server.getClientCapabilities()?.roots?.listChanged === true) {
      rootsDirty = true;
      rootsRetryAt = 0;
      rootsGeneration++;
      relayedRoots.current = [];
      await remoteClient.notifyRootsChanged().catch(() => {});
    }
  });
  let requestShutdown!: () => void;
  const shutdownRequested = new Promise<void>((resolve) => {
    let requested = false;
    requestShutdown = () => {
      if (requested) return;
      requested = true;
      resolve();
    };
  });
  const transport = new StdioServerTransport();
  process.stdin.once("end", requestShutdown);
  process.stdin.once("close", requestShutdown);
  process.once("SIGINT", requestShutdown);
  process.once("SIGTERM", requestShutdown);
  process.once("beforeExit", requestShutdown);

  try {
    await server.connect(transport);
    if (process.stdin.readableEnded || process.stdin.destroyed) requestShutdown();
    await shutdownRequested;
    await new Promise<void>((resolve) => { setImmediate(resolve); });
    await waitForAcceptedCallsDrained();
    await new Promise<void>((resolve) => { setImmediate(resolve); });
    if (process.stdout.writableNeedDrain && !process.stdout.destroyed) {
      await new Promise<void>((resolve) => { process.stdout.once("drain", resolve); });
    }
  } finally {
    process.stdin.off("end", requestShutdown);
    process.stdin.off("close", requestShutdown);
    process.off("SIGINT", requestShutdown);
    process.off("SIGTERM", requestShutdown);
    process.off("beforeExit", requestShutdown);
    await server.close().catch(() => {});
    await remoteClient.close().catch(() => {});
  }
}

export function legacyTextCompatibleToolResult(result: CallToolResult, maxBytes = STDIO_SHIM_LEGACY_TEXT_MAX_BYTES): CallToolResult {
  if (!result.structuredContent) return result;
  return { ...result, content: [{ type: "text", text: canonicalTextProjection(result.structuredContent, maxBytes) }] };
}

export function staleSessionToolResult(error: unknown, requestId?: string): CallToolResult | undefined {
  if (!(error instanceof StreamableHTTPError) || error.code !== 404 || !error.message.startsWith(STALE_SESSION_ERROR_PREFIX)) return undefined;
  const bodyText = error.message.slice(STALE_SESSION_ERROR_PREFIX.length);
  if (Buffer.byteLength(bodyText, "utf8") > STALE_SESSION_ERROR_MAX_BYTES) return undefined;

  let body: unknown;
  try {
    body = JSON.parse(bodyText);
  } catch {
    return undefined;
  }
  if (!isRecord(body) || !isRecord(body.error) || body.error.code !== -32001 || !isRecord(body.error.data)) return undefined;
  const data = body.error.data;
  if (data.code !== "mcp_session_not_found" || data.request_executed !== false) return undefined;

  return gatewayResultToMcpToolResult({
    status: "error",
    ...(requestId ? { request_id: requestId } : {}),
    error: {
      code: "mcp_session_not_found",
      message: "The remote MCP session is no longer registered. Reconnect before issuing the next explicit tool call.",
      details: {
        code: "mcp_session_not_found",
        request_executed: false,
        client_reconnect_required: true,
        new_session_required: true,
        automatic_replay_attempted: false,
      },
    },
    diagnostics: [],
    next_valid_actions: [
      "issue_the_next_tool_call_to_initialize_a_fresh_session",
      "retry_the_original_request_only_as_a_new_explicit_call",
    ],
  });
}

export async function createHttpStdioShimGeneration(options: StdioShimOptions, relayedRoots: { current: Root[] }, outerServer: () => McpServer | undefined = () => undefined): Promise<StdioShimRemoteGeneration> {
  const relay = new StdioFormElicitationRelay(outerServer);
  const formAvailable = hasOuterFormElicitation(outerServer());
  const transport = new StreamableHTTPClientTransport(new URL(options.url), {
    ...(options.token ? { requestInit: { headers: { "X-SGG-Unity-MCP-Token": options.token } } } : {}),
    // Stream resumption cannot prove exact parent binding; never replay a prompt.
    reconnectionOptions: { maxRetries: 0, initialReconnectionDelay: 1_000, maxReconnectionDelay: 1_000, reconnectionDelayGrowFactor: 1 },
    fetch: relay.wrapFetch(async (url, init) => await fetch(url, init)),
  });
  const client = new Client({ name: STDIO_SHIM_CLIENT_NAME, version: options.version }, {
    capabilities: { roots: { listChanged: true }, ...(formAvailable ? { elicitation: { form: {} } } : {}) },
  });
  client.setRequestHandler(ListRootsRequestSchema, () => ({ roots: relayedRoots.current }));
  if (formAvailable) client.setRequestHandler(ElicitRequestSchema, async (request, extra) => await relay.forward(request, extra));

  try {
    await client.connect(relay.wrapTransport(transport as unknown as Transport));
  } catch (error) {
    await transport.close().catch(() => {});
    throw error;
  }

  return {
    client: {
      callTool: async (params, schema, requestOptions, outerCall) => await relay.runCall(outerCall, async () => await client.callTool(params, schema, requestOptions) as CallToolResult),
    },
    notifyRootsChanged: async () => { await client.sendRootsListChanged(); },
    close: async (terminateSession) => {
      relay.close();
      if (terminateSession && transport.sessionId) {
        let timeout: ReturnType<typeof setTimeout> | undefined;
        try {
          await Promise.race([
            transport.terminateSession().catch(() => {}),
            new Promise<void>((resolve) => { timeout = setTimeout(resolve, 1_000); }),
          ]);
        } finally {
          if (timeout) clearTimeout(timeout);
        }
      }
      await client.close().catch(() => {});
    },
  };
}

function boundedString(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value.slice(0, 256) : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function toRecord(value: unknown): Record<string, unknown> {
  if (value && typeof value === "object" && !Array.isArray(value)) {
    return value as Record<string, unknown>;
  }
  return {};
}
