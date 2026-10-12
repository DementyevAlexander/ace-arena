import { randomBytes } from "node:crypto";

import {
  McpServer,
  acceptedContent,
  createRequestStateCodec,
  inputRequired,
  inputResponse,
  type CallToolResult,
  type InputRequiredResult,
  type McpRequestContext,
  type RequestStateCodec,
  type ServerContext,
} from "@modelcontextprotocol/server";
import { z } from "zod";

import { argsHash } from "../approvals/approval-store.js";
import type { ClientRootsRegistry } from "./client-roots.js";
import { TOOL_ARG_SCHEMAS, TOOL_NAMES, gatewayResultOutputSchema, type ToolName } from "./contracts.js";
import { dispatchAcceptedPendingTool, dispatchTool, type DispatcherContext, type DispatchInvocationOptions } from "./dispatcher.js";
import { fail, type GatewayResult } from "./results.js";
import {
  GATEWAY_MCP_SERVER_NAME,
  TOOL_METADATA,
  approvalPromptMessage,
  approvalRequestFromResult,
  gatewayResultToMcpToolResult,
} from "./sdk-server.js";

export const MODERN_MCP_PROTOCOL_VERSION = "2026-07-28";
export const MODERN_MCP_SDK_VERSION = "2.0.0";
const APPROVAL_INPUT_KEY = "approval";
const DEFAULT_APPROVAL_TTL_MS = 300_000;
const MAX_APPROVAL_STATE_STRING_LENGTH = 1_024;

const approvalResponseSchema = z.object({ approve: z.boolean() }).strict();

export type McpV2Metrics = {
  mrtrInputRequiredTotal: number;
  mrtrCompletedTotal: number;
  mrtrDeclinedTotal: number;
  mrtrStateVerificationFailedTotal: number;
};

export type UnityMcpV2FactoryOptions = {
  version: string;
  clientRootsRegistry?: ClientRootsRegistry;
  metrics?: McpV2Metrics;
  requestStateKey?: Uint8Array;
};

type ApprovalRequestState = {
  pending_id: string;
  args_hash: string;
  created_at: string;
  updated_at: string;
  expires_at: string;
  request_id?: string;
};

export function createMcpV2Metrics(): McpV2Metrics {
  return {
    mrtrInputRequiredTotal: 0,
    mrtrCompletedTotal: 0,
    mrtrDeclinedTotal: 0,
    mrtrStateVerificationFailedTotal: 0,
  };
}

export function mcpV2MetricsHealth(metrics: McpV2Metrics): Record<string, number> {
  return {
    mrtr_input_required_total: metrics.mrtrInputRequiredTotal,
    mrtr_completed_total: metrics.mrtrCompletedTotal,
    mrtr_declined_total: metrics.mrtrDeclinedTotal,
    mrtr_state_verification_failed_total: metrics.mrtrStateVerificationFailedTotal,
  };
}

export function createUnityMcpV2ServerFactory(
  context: DispatcherContext,
  options: UnityMcpV2FactoryOptions,
): (requestContext: McpRequestContext) => McpServer {
  const metrics = options.metrics ?? createMcpV2Metrics();
  const codec = createApprovalRequestStateCodec(
    options.requestStateKey ?? randomBytes(32),
    metrics,
    context.approvalDefaultTtlMs ?? DEFAULT_APPROVAL_TTL_MS,
  );
  return (requestContext) => createUnityMcpV2Server(context, {
    ...options,
    era: requestContext.era,
    metrics,
    codec,
  });
}

type UnityMcpV2ServerOptions = UnityMcpV2FactoryOptions & {
  era: McpRequestContext["era"];
  metrics: McpV2Metrics;
  codec: RequestStateCodec<ApprovalRequestState>;
};

function createUnityMcpV2Server(context: DispatcherContext, options: UnityMcpV2ServerOptions): McpServer {
  const server = new McpServer({
    name: GATEWAY_MCP_SERVER_NAME,
    title: "SGG Unity MCP Gateway",
    version: options.version,
  }, {
    cacheHints: {
      "tools/list": { ttlMs: 300_000, cacheScope: "private" },
    },
    inputRequired: {
      roundTimeoutMs: Math.max(1, context.approvalDefaultTtlMs ?? DEFAULT_APPROVAL_TTL_MS),
    },
    requestState: { verify: options.codec.verify },
  });

  const rootsObserver = options.era === "legacy"
    ? options.clientRootsRegistry?.createV2Observer(server)
    : undefined;
  for (const toolName of TOOL_NAMES) {
    server.registerTool(
      toolName,
      {
        ...TOOL_METADATA[toolName],
        inputSchema: TOOL_ARG_SCHEMAS[toolName],
        outputSchema: gatewayResultOutputSchema,
      },
      async (args: unknown, requestContext: ServerContext): Promise<CallToolResult | InputRequiredResult> => {
        await rootsObserver?.observe(requestContext);
        return handleV2ToolCall(server, toolName, args ?? {}, requestContext, context, options.codec, options.metrics);
      },
    );
  }
  return server;
}

async function handleV2ToolCall(
  server: McpServer,
  toolName: ToolName,
  args: unknown,
  requestContext: ServerContext,
  context: DispatcherContext,
  codec: RequestStateCodec<ApprovalRequestState>,
  metrics: McpV2Metrics,
): Promise<CallToolResult | InputRequiredResult> {
  const invocation = {
    nativeMcpFormElicitationAvailable: hasFormElicitation(server.server.getClientCapabilities()),
  };
  const state = requestContext.mcpReq.requestState<ApprovalRequestState>();
  if (state !== undefined) {
    return finishApprovalRound(toolName, args, state, requestContext, context, metrics, invocation);
  }

  const result = await dispatchTool(toolName, args, context, invocation);
  const approvalRequest = isElicitableApprovalTool(toolName) ? approvalRequestFromResult(result) : undefined;
  if (!approvalRequest || !hasFormElicitation(server.server.getClientCapabilities())) {
    return gatewayResultToMcpToolResult(result);
  }

  const statePayload: ApprovalRequestState = {
    pending_id: approvalRequest.pending_id,
    args_hash: approvalRequest.args_hash,
    created_at: approvalRequest.created_at,
    updated_at: approvalRequest.updated_at,
    expires_at: approvalRequest.expires_at,
    ...(approvalRequest.request_id ? { request_id: approvalRequest.request_id } : {}),
  };
  const requestState = await codec.mint(statePayload, requestContext);
  incrementMetric(metrics, "mrtrInputRequiredTotal");
  return inputRequired({
    inputRequests: {
      [APPROVAL_INPUT_KEY]: inputRequired.elicit({
        mode: "form",
        message: approvalPromptMessage(approvalRequest),
        requestedSchema: {
          type: "object",
          properties: { approve: { type: "boolean", title: "Approve once" } },
          required: ["approve"],
        },
      }),
    },
    requestState,
  });
}

async function finishApprovalRound(
  toolName: ToolName,
  args: unknown,
  state: ApprovalRequestState,
  requestContext: ServerContext,
  context: DispatcherContext,
  metrics: McpV2Metrics,
  invocation: DispatchInvocationOptions,
): Promise<CallToolResult> {
  if (!isElicitableApprovalTool(toolName) || !validApprovalRequestState(state)) {
    return gatewayResultToMcpToolResult(invalidApprovalRound("invalid_request_state"));
  }
  if (Date.parse(state.expires_at) <= (context.now?.().getTime() ?? Date.now())) {
    return gatewayResultToMcpToolResult(invalidApprovalRound("expired_request_state", state));
  }
  if (approvalArgsHash(toolName, args) !== state.args_hash) {
    return gatewayResultToMcpToolResult(invalidApprovalRound("approval_arguments_changed", state));
  }

  const response = inputResponse(requestContext.mcpReq.inputResponses, APPROVAL_INPUT_KEY);
  if (response.kind === "elicit" && (response.action === "decline" || response.action === "cancel")) {
    incrementMetric(metrics, "mrtrDeclinedTotal");
    return gatewayResultToMcpToolResult(declinedApprovalRound(response.action, state));
  }
  const accepted = acceptedContent(requestContext.mcpReq.inputResponses, APPROVAL_INPUT_KEY, approvalResponseSchema);
  if (!accepted) {
    return gatewayResultToMcpToolResult(invalidApprovalRound("malformed_approval_response", state));
  }
  if (!accepted.approve) {
    incrementMetric(metrics, "mrtrDeclinedTotal");
    return gatewayResultToMcpToolResult(declinedApprovalRound("decline", state));
  }

  const result = await dispatchAcceptedPendingTool(toolName, structuredClone(args), context, {
    generation: {
      id: state.pending_id,
      created_at: state.created_at,
      updated_at: state.updated_at,
      expires_at: state.expires_at,
    },
    original_confirmation: confirmationFromState(state),
  }, invocation);
  incrementMetric(metrics, "mrtrCompletedTotal");
  return gatewayResultToMcpToolResult(result);
}

function isElicitableApprovalTool(toolName: ToolName): boolean {
  return toolName === "sgg_unity_call" || toolName === "sgg_unity_stale_registry_cleanup";
}

function createApprovalRequestStateCodec(key: Uint8Array, metrics: McpV2Metrics, ttlMs: number): RequestStateCodec<ApprovalRequestState> {
  const codec = createRequestStateCodec<ApprovalRequestState>({
    key,
    ttlSeconds: Math.max(1, Math.ceil(ttlMs / 1_000)),
    bind: (context) => context.mcpReq.method,
  });
  return {
    mint: codec.mint,
    verify: async (state, context) => {
      try {
        return await codec.verify(state, context);
      } catch (error) {
        incrementMetric(metrics, "mrtrStateVerificationFailedTotal");
        throw error;
      }
    },
  };
}

function approvalArgsHash(toolName: ToolName, args: unknown): string | undefined {
  if (toolName === "sgg_unity_stale_registry_cleanup") {
    try {
      const parsed = TOOL_ARG_SCHEMAS.sgg_unity_stale_registry_cleanup.parse(args ?? {});
      return argsHash(Object.fromEntries(Object.entries(parsed).filter(([, value]) => value !== undefined)));
    } catch {
      return undefined;
    }
  }
  if (toolName !== "sgg_unity_call" || !isRecord(args) || typeof args.args_json !== "string") return undefined;
  try {
    const commandArgs = JSON.parse(args.args_json) as unknown;
    return isRecord(commandArgs) ? argsHash(commandArgs) : undefined;
  } catch {
    return undefined;
  }
}

function validApprovalRequestState(value: unknown): value is ApprovalRequestState {
  if (!isRecord(value)) return false;
  const keys = Object.keys(value);
  if (keys.some((key) => !["pending_id", "args_hash", "created_at", "updated_at", "expires_at", "request_id"].includes(key))) return false;
  if (![value.pending_id, value.args_hash, value.created_at, value.updated_at, value.expires_at].every(boundedString)) return false;
  if (value.request_id !== undefined && !boundedString(value.request_id)) return false;
  if (!/^sha256:[0-9a-f]{64}$/.test(value.args_hash as string)) return false;
  const createdAt = parsedTimestamp(value.created_at);
  const updatedAt = parsedTimestamp(value.updated_at);
  const expiresAt = parsedTimestamp(value.expires_at);
  return createdAt !== undefined && updatedAt !== undefined && expiresAt !== undefined
    && createdAt <= updatedAt && updatedAt < expiresAt;
}

function confirmationFromState(state: ApprovalRequestState): GatewayResult {
  return fail({
    ...(state.request_id ? { request_id: state.request_id } : {}),
    error: {
      code: "confirmation_required",
      message: "Human confirmation is required before executing this Unity command",
      details: { approval_request: { ...state } },
    },
  });
}

function declinedApprovalRound(outcome: "decline" | "cancel", state: ApprovalRequestState): GatewayResult {
  return fail({
    ...(state.request_id ? { request_id: state.request_id } : {}),
    error: {
      code: "confirmation_required",
      message: "The Unity command was not executed because confirmation was not accepted",
      details: { approval_request: { ...state }, mrtr: { outcome } },
    },
  });
}

function invalidApprovalRound(reason: string, state?: ApprovalRequestState): GatewayResult {
  return fail({
    ...(state?.request_id ? { request_id: state.request_id } : {}),
    error: {
      code: "schema_validation_failed",
      message: "The multi-round approval response is invalid or expired",
      details: { reason, write_command_repeated: false },
    },
    next_valid_actions: ["retry_the_original_call_to_request_a_fresh_confirmation"],
  });
}

function hasFormElicitation(capabilities: unknown): boolean {
  return isRecord(capabilities) && isRecord(capabilities.elicitation) && isRecord(capabilities.elicitation.form);
}

function incrementMetric(metrics: McpV2Metrics, key: keyof McpV2Metrics): void {
  metrics[key] = Math.min(Number.MAX_SAFE_INTEGER, metrics[key] + 1);
}

function boundedString(value: unknown): value is string {
  return typeof value === "string" && value.length > 0 && value.length <= MAX_APPROVAL_STATE_STRING_LENGTH;
}

function parsedTimestamp(value: unknown): number | undefined {
  if (typeof value !== "string") return undefined;
  const timestamp = Date.parse(value);
  return Number.isFinite(timestamp) && new Date(timestamp).toISOString() === value ? timestamp : undefined;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
