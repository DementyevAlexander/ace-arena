import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { McpServer as ModernMcpServer, createMcpHandler, createRequestStateCodec } from "@modelcontextprotocol/server";
import { NodeStreamableHTTPServerTransport } from "@modelcontextprotocol/node";
import { serveStdio } from "@modelcontextprotocol/server/stdio";

export const MCP_SDK_VERSION = "2.0.0";
export const LEGACY_MCP_SDK_VERSION = "1.29.0";

type SdkPublicSurface = {
  createMcpServer: () => unknown;
  createStreamableHttpTransport: () => unknown;
};

type ModernSdkPublicSurface = {
  createMcpServer: () => unknown;
  createStreamableHttpTransport: () => unknown;
  createHttpHandler: () => unknown;
  createRequestStateCodec: () => unknown;
  serveStdio: unknown;
};

export type McpSdkIncompatibleEvidence = {
  component: "McpServer" | "McpServer.server" | "StreamableHTTPServerTransport" | "ModernMcpServer" | "ModernMcpServer.server" | "ModernMcpHttpHandler" | "ModernRequestStateCodec" | "ModernStdio";
  member: "connect" | "close" | "getClientCapabilities" | "elicitInput" | "handleRequest" | "fetch" | "mint" | "verify" | "serveStdio";
  expected: "function";
};

export class McpSdkIncompatibleError extends Error {
  readonly code = "mcp_sdk_incompatible";

  constructor(readonly evidence: McpSdkIncompatibleEvidence[]) {
    super("MCP SDK public surface is incompatible");
    this.name = "McpSdkIncompatibleError";
  }
}

export function assertMcpSdkCompatible(surface: SdkPublicSurface = {
  createMcpServer: () => new McpServer({ name: "sdk-compat", version: LEGACY_MCP_SDK_VERSION }),
  createStreamableHttpTransport: () => new StreamableHTTPServerTransport({
    sessionIdGenerator: undefined,
    enableJsonResponse: true,
  } as unknown as ConstructorParameters<typeof StreamableHTTPServerTransport>[0]),
}, modernSurface: ModernSdkPublicSurface = {
  createMcpServer: () => new ModernMcpServer({ name: "sdk-compat-modern", version: MCP_SDK_VERSION }),
  createStreamableHttpTransport: () => new NodeStreamableHTTPServerTransport({ sessionIdGenerator: undefined }),
  createHttpHandler: () => createMcpHandler(() => new ModernMcpServer({ name: "sdk-compat-modern", version: MCP_SDK_VERSION }), { legacy: "reject" }),
  createRequestStateCodec: () => createRequestStateCodec({ key: new Uint8Array(32) }),
  serveStdio,
}): void {
  const evidence: McpSdkIncompatibleEvidence[] = [];
  const server = createSurfaceValue(surface.createMcpServer);
  const transport = createSurfaceValue(surface.createStreamableHttpTransport);
  requireFunction(evidence, "McpServer", server, "connect");
  requireFunction(evidence, "McpServer", server, "close");

  const lowLevel = readMember(server, "server");
  requireFunction(evidence, "McpServer.server", lowLevel, "getClientCapabilities");
  requireFunction(evidence, "McpServer.server", lowLevel, "elicitInput");
  requireFunction(evidence, "StreamableHTTPServerTransport", transport, "handleRequest");
  requireFunction(evidence, "StreamableHTTPServerTransport", transport, "close");

  const modernServer = createSurfaceValue(modernSurface.createMcpServer);
  const modernTransport = createSurfaceValue(modernSurface.createStreamableHttpTransport);
  const modernHandler = createSurfaceValue(modernSurface.createHttpHandler);
  const modernCodec = createSurfaceValue(modernSurface.createRequestStateCodec);
  requireFunction(evidence, "ModernMcpServer", modernServer, "connect");
  requireFunction(evidence, "ModernMcpServer", modernServer, "close");
  requireFunction(evidence, "ModernMcpServer.server", readMember(modernServer, "server"), "getClientCapabilities");
  requireFunction(evidence, "StreamableHTTPServerTransport", modernTransport, "handleRequest");
  requireFunction(evidence, "ModernMcpHttpHandler", modernHandler, "fetch");
  requireFunction(evidence, "ModernMcpHttpHandler", modernHandler, "close");
  requireFunction(evidence, "ModernRequestStateCodec", modernCodec, "mint");
  requireFunction(evidence, "ModernRequestStateCodec", modernCodec, "verify");
  requireFunction(evidence, "ModernStdio", { serveStdio: modernSurface.serveStdio }, "serveStdio");

  if (evidence.length > 0) {
    throw new McpSdkIncompatibleError(evidence.slice(0, 6));
  }
}

function createSurfaceValue(factory: () => unknown): unknown {
  try {
    return factory();
  } catch {
    return undefined;
  }
}

function requireFunction(
  evidence: McpSdkIncompatibleEvidence[],
  component: McpSdkIncompatibleEvidence["component"],
  target: unknown,
  member: McpSdkIncompatibleEvidence["member"],
): void {
  if (typeof readMember(target, member) !== "function") {
    evidence.push({ component, member, expected: "function" });
  }
}

function readMember(target: unknown, member: string): unknown {
  if ((typeof target !== "object" && typeof target !== "function") || target === null) {
    return undefined;
  }
  return Reflect.get(target, member);
}
