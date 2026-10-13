import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import type { RequestHandlerExtra } from "@modelcontextprotocol/sdk/shared/protocol.js";
import type { CallToolResult, ServerNotification, ServerRequest } from "@modelcontextprotocol/sdk/types.js";

import { dispatchAcceptedPendingTool, dispatchTool, type DispatcherContext, type DispatchInvocationOptions } from "./dispatcher.js";
import type { ClientRootsRegistry } from "./client-roots.js";
import { TOOL_ARG_SCHEMAS, TOOL_NAMES, gatewayResultOutputSchema, type ToolName } from "./contracts.js";
import { gatewayResultToMcpToolResult } from "./result-presentation.js";
import type { GatewayResult } from "./results.js";
import { boundedOpenCodeSessionMeta } from "./stdio-elicitation-relay.js";

export { gatewayResultToMcpToolResult } from "./result-presentation.js";

export const GATEWAY_MCP_SERVER_NAME = "sgg-unity-mcp-gateway";
export const RECOMMENDED_MCP_CLIENT_TIMEOUT_MS = 3_900_000;
export const STDIO_SHIM_REMOTE_CALL_TIMEOUT_MS = 86_400_000;
const NATIVE_ELICITATION_TIMEOUT_MS = 30_000;

export type UnityMcpServerOptions = {
  version: string;
  clientRootsRegistry?: ClientRootsRegistry;
};

export type UnityMcpToolHandler = (toolName: ToolName, args: unknown, extra: UnityMcpToolExtra) => Promise<CallToolResult>;
export type UnityMcpBeforeTool = (extra: UnityMcpToolExtra) => Promise<void>;

export type UnityMcpToolExtra = RequestHandlerExtra<ServerRequest, ServerNotification>;

export type ToolMetadata = {
  title: string;
  description: string;
  annotations?: {
    readOnlyHint?: boolean;
    destructiveHint?: boolean;
    idempotentHint?: boolean;
    openWorldHint?: boolean;
  };
};

export const TOOL_METADATA: Record<ToolName, ToolMetadata> = {
  sgg_unity_projects: {
    title: "Unity Projects",
    description: "List live Unity Editor projects and aggregate live/stale registry counts.",
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  },
  sgg_unity_stale_projects: {
    title: "Stale Unity Projects",
    description: "Query bounded stale Unity project heartbeat records retained by the local gateway registry.",
    annotations: { readOnlyHint: true, destructiveHint: false, idempotentHint: true, openWorldHint: false },
  },
  sgg_unity_stale_registry_cleanup: {
    title: "Clean Stale Unity Registry",
    description: "Preview or explicitly remove bounded expired stale heartbeat records whose owner process is confirmed dead.",
    annotations: { readOnlyHint: false, destructiveHint: true, idempotentHint: false, openWorldHint: false },
  },
  sgg_unity_describe: {
    title: "Describe Unity Project",
    description: "Describe a Unity project status, command catalog, or a single command schema.",
  },
  sgg_unity_call: {
    title: "Call Unity Command",
    description: "Invoke one Unity bridge command for an explicit project using args_json.",
  },
  sgg_unity_batch: {
    title: "Batch Unity Commands",
    description: "Invoke multiple explicit-project Unity commands sequentially or by project queue.",
  },
  sgg_unity_job: {
    title: "Unity Job",
    description: "Inspect a Unity gateway job or request cancellation when the job command advertises supports_cancel.",
  },
  sgg_unity_logs: {
    title: "Unity Logs",
    description: "Read bounded Unity bridge, console, or job logs for an explicit project.",
  },
};

export function createUnityMcpServer(context: DispatcherContext, options: UnityMcpServerOptions): McpServer {
  const server = new McpServer({
    name: GATEWAY_MCP_SERVER_NAME,
    title: "SGG Unity MCP Gateway",
    version: options.version,
  });

  const rootsObserver = options.clientRootsRegistry?.createObserver(server);
  registerUnityMcpTools(server, context, undefined, rootsObserver ? (extra) => rootsObserver.observe(extra) : undefined);
  return server;
}

export function registerUnityMcpTools(server: McpServer, context: DispatcherContext, handler?: UnityMcpToolHandler, beforeTool?: UnityMcpBeforeTool): void {
  for (const toolName of TOOL_NAMES) {
    server.registerTool(
      toolName,
      {
        ...TOOL_METADATA[toolName],
        inputSchema: TOOL_ARG_SCHEMAS[toolName],
        outputSchema: gatewayResultOutputSchema,
      },
      async (args: unknown, extra: UnityMcpToolExtra): Promise<CallToolResult> => {
        await beforeTool?.(extra);
        if (handler) {
          return handler(toolName, args ?? {}, extra);
        }

        const invocation = {
          nativeMcpFormElicitationAvailable: hasFormElicitation(server.server.getClientCapabilities()),
        };
        const result = await dispatchTool(toolName, args ?? {}, context, invocation);
        return gatewayResultToMcpToolResult(await maybeElicitApproval(server, toolName, args ?? {}, result, context, extra, invocation));
      },
    );
  }
}

async function maybeElicitApproval(server: McpServer, toolName: ToolName, args: unknown, result: GatewayResult, context: DispatcherContext, extra: UnityMcpToolExtra, invocation: DispatchInvocationOptions): Promise<GatewayResult> {
  if (!isElicitableApprovalTool(toolName) || !context.approvalStore || !hasFormElicitation(server.server.getClientCapabilities())) {
    return result;
  }

  const approvalRequest = approvalRequestFromResult(result);
  if (!approvalRequest) {
    return result;
  }

  try {
    const acceptedArgs = structuredClone(args);
    const uiMeta = boundedOpenCodeSessionMeta(extra._meta);
    const elicitation = await server.server.elicitInput({
      mode: "form",
      message: approvalPromptMessage(approvalRequest),
      ...(uiMeta ? { _meta: uiMeta } : {}),
      requestedSchema: {
        type: "object",
        properties: {
          approve: {
            type: "boolean",
            title: "Approve once",
            description: "Claim this displayed request once, revalidate its current context, and execute the exact Unity command call.",
            default: false,
          },
        },
        required: ["approve"],
      },
    }, {
      timeout: Math.min(context.approvalDefaultTtlMs ?? NATIVE_ELICITATION_TIMEOUT_MS, NATIVE_ELICITATION_TIMEOUT_MS),
      signal: extra.signal,
      relatedRequestId: extra.requestId,
    });

    if (elicitation.action === "decline" || elicitation.action === "cancel" || (elicitation.action === "accept" && elicitation.content?.approve === false)) {
      return result;
    }
    if (elicitation.action !== "accept" || elicitation.content?.approve !== true) {
      return nativeElicitationFailed(result);
    }

    return dispatchAcceptedPendingTool(toolName, acceptedArgs, context, {
      generation: {
        id: approvalRequest.pending_id,
        created_at: approvalRequest.created_at,
        updated_at: approvalRequest.updated_at,
        expires_at: approvalRequest.expires_at,
      },
      original_confirmation: result,
    }, invocation);
  } catch {
    return nativeElicitationFailed(result);
  }
}

function hasFormElicitation(capabilities: unknown): boolean {
  return isRecord(capabilities) && isRecord(capabilities.elicitation) && isRecord(capabilities.elicitation.form);
}

function nativeElicitationFailed(result: GatewayResult): GatewayResult {
  if (result.status !== "error") {
    return result;
  }
  return {
    ...result,
    error: {
      ...result.error,
      details: {
        ...result.error.details,
        native_mcp_elicitation: {
          last_attempt: {
            outcome: "failed",
            diagnostic_code: "mcp_elicitation_failed",
          },
        },
      },
    },
  };
}

export type ElicitableApprovalRequest = {
  approval_type: "unity_command" | "gateway_operation";
  project: string;
  command: string;
  risk: string;
  args_hash: string;
  pending_id: string;
  created_at: string;
  updated_at: string;
  request_id?: string;
  expires_at: string;
};

export function approvalRequestFromResult(result: GatewayResult): ElicitableApprovalRequest | undefined {
  if (result.status !== "error" || result.error.code !== "confirmation_required") {
    return undefined;
  }

  const approvalRequest = isRecord(result.error.details?.approval_request) ? result.error.details.approval_request : undefined;
  if (!approvalRequest) {
    return undefined;
  }

  const project = stringField(approvalRequest.project);
  const command = stringField(approvalRequest.command);
  const risk = stringField(approvalRequest.risk);
  const argsHashValue = stringField(approvalRequest.args_hash);
  const pendingId = stringField(approvalRequest.pending_id);
  const createdAt = stringField(approvalRequest.created_at);
  const updatedAt = stringField(approvalRequest.updated_at);
  const expiresAt = stringField(approvalRequest.expires_at);
  const approvalType = approvalRequest.approval_type === "gateway_operation" ? "gateway_operation" : approvalRequest.approval_type === "unity_command" ? "unity_command" : undefined;
  if (!approvalType || !project || !command || !risk || !argsHashValue || !pendingId || !createdAt || !updatedAt || !expiresAt) {
    return undefined;
  }

  return {
    approval_type: approvalType,
    project,
    command,
    risk,
    args_hash: argsHashValue,
    pending_id: pendingId,
    created_at: createdAt,
    updated_at: updatedAt,
    expires_at: expiresAt,
    ...(typeof approvalRequest.request_id === "string" ? { request_id: approvalRequest.request_id } : {}),
  };
}

export function approvalPromptMessage(request: ElicitableApprovalRequest): string {
  const actionType = request.approval_type === "gateway_operation" ? "Gateway operation" : "Unity command";
  return [
    `Approve this ${actionType.toLowerCase()} once?`,
    `Project: ${request.project}`,
    `Command: ${request.command}`,
    `Risk: ${request.risk}`,
    `Args hash: ${request.args_hash}`,
    `Pending approval: ${request.pending_id}`,
    ...(request.request_id ? [`Request id: ${request.request_id}`] : []),
    `Expires at: ${request.expires_at}`,
    "Only approve if you requested this exact action. No raw args or secrets are requested.",
  ].join("\n");
}

function isElicitableApprovalTool(toolName: ToolName): boolean {
  return toolName === "sgg_unity_call" || toolName === "sgg_unity_stale_registry_cleanup";
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function stringField(value: unknown): string | undefined {
  return typeof value === "string" && value.length > 0 ? value : undefined;
}
