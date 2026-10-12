import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StdioClientTransport } from "@modelcontextprotocol/sdk/client/stdio.js";

import type { BridgeInvokeResponse, CommandDescriptor } from "../bridge/bridge-client.js";
import { validateUnityAssetPath } from "../paths/canonical-path.js";
import type { ErrorCode } from "./results.js";

export type OfficialRelayRisk = CommandDescriptor["risk"];

export type OfficialRelayConfig = {
  enabled: boolean;
  command?: string | undefined;
  args: string[];
  env: Record<string, string>;
  allow_tools: string[];
  tool_risks: Record<string, OfficialRelayRisk>;
  unity_relative_path_fields: Record<string, string[]>;
  connect_timeout_ms: number;
  call_timeout_ms: number;
};

export type OfficialRelayTool = {
  name: string;
  description?: string | undefined;
  title?: string | undefined;
  inputSchema?: Record<string, unknown>;
  annotations?: {
    readOnlyHint?: boolean | undefined;
    destructiveHint?: boolean | undefined;
    idempotentHint?: boolean | undefined;
    openWorldHint?: boolean | undefined;
  };
};

export interface OfficialRelayClient {
  listTools(): Promise<OfficialRelayTool[]>;
  callTool(name: string, args: Record<string, unknown>): Promise<unknown>;
  close?(): Promise<void>;
}

export class OfficialRelayAdapter {
  readonly #config: OfficialRelayConfig;
  readonly #client: OfficialRelayClient;
  #lastStatus: Record<string, unknown> = { enabled: false, diagnostic_only: true };

  constructor(config: OfficialRelayConfig, client?: OfficialRelayClient) {
    this.#config = config;
    this.#client = client ?? new StdioOfficialRelayClient(config);
    this.#lastStatus = {
      enabled: config.enabled,
      diagnostic_only: !config.enabled,
      allowlist_count: config.allow_tools.length,
      configured_command: config.command ?? "",
    };
  }

  get enabled(): boolean {
    return this.#config.enabled;
  }

  async listCommandDescriptors(project: string): Promise<CommandDescriptor[]> {
    if (!this.#config.enabled || this.#config.allow_tools.length === 0) {
      this.#lastStatus = this.status("disabled", undefined, false);
      return [];
    }

    try {
      const tools = await this.#client.listTools();
      const allowlist = new Set(this.#config.allow_tools);
      const descriptors = tools.filter((tool) => allowlist.has(tool.name)).map((tool) => this.#descriptorForTool(tool));
      this.#lastStatus = this.status("ok", { project, imported_count: descriptors.length, listed_count: tools.length }, true);
      return descriptors;
    } catch (error) {
      this.#lastStatus = this.status(classifyRelayError(error), { error: errorMessage(error) }, true);
      return [];
    }
  }

  async findCommandDescriptor(project: string, commandId: string): Promise<CommandDescriptor | undefined> {
    if (!commandId.startsWith("official.")) {
      return undefined;
    }

    return (await this.listCommandDescriptors(project)).find((descriptor) => descriptor.id === commandId);
  }

  async invoke(commandId: string, args: Record<string, unknown>): Promise<BridgeInvokeResponse> {
    const toolName = officialToolName(commandId);
    if (!toolName || !this.#config.allow_tools.includes(toolName)) {
      return relayError("command_not_found", `Official relay tool '${commandId}' is not allowlisted`);
    }

    const pathValidation = validateConfiguredPathFields(args, this.#config.unity_relative_path_fields[toolName] ?? []);
    if (pathValidation) {
      return pathValidation;
    }

    try {
      const data = await this.#client.callTool(toolName, args);
      return { status: "ok", data, duration_ms: 0 };
    } catch (error) {
      return relayError("bridge_unreachable", `Official Unity MCP relay call failed: ${errorMessage(error)}`);
    }
  }

  currentStatus(): Record<string, unknown> {
    return this.#lastStatus;
  }

  #descriptorForTool(tool: OfficialRelayTool): CommandDescriptor {
    return {
      id: officialCommandId(tool.name),
      title: tool.title ?? tool.name,
      description: tool.description ?? `Official Unity MCP relay tool ${tool.name}`,
      package: "com.unity.official-mcp-relay",
      risk: this.#config.tool_risks[tool.name] ?? inferRisk(tool),
      idempotency: tool.annotations?.idempotentHint ? "idempotent" : "unsafe",
      timeout_ms: this.#config.call_timeout_ms,
      schema: normalizeInputSchema(tool.inputSchema),
      tags: ["official", "unity-ai-assistant"],
      capability: "official_unity_mcp_relay",
      result_shape: "object_details",
      feature_flag: "official_unity_mcp_relay",
      source: "official_relay",
      package_id: "com.unity.official-mcp-relay",
      trust: "untrusted_package",
    };
  }

  private status(state: string, details: Record<string, unknown> | undefined, launchAttempted: boolean): Record<string, unknown> {
    return {
      enabled: this.#config.enabled,
      state,
      diagnostic_only: state !== "ok",
      allowlist_count: this.#config.allow_tools.length,
      configured_command: this.#config.command ?? "",
      launch_attempted: launchAttempted,
      ...(details ?? {}),
    };
  }
}

export class StdioOfficialRelayClient implements OfficialRelayClient {
  readonly #config: OfficialRelayConfig;
  #client: Client | undefined;
  #transport: StdioClientTransport | undefined;

  constructor(config: OfficialRelayConfig) {
    this.#config = config;
  }

  async listTools(): Promise<OfficialRelayTool[]> {
    const client = await this.#connectedClient();
    const response = await client.listTools(undefined, { timeout: this.#config.call_timeout_ms });
    return response.tools.map((tool) => ({
      name: tool.name,
      ...(tool.description ? { description: tool.description } : {}),
      ...(tool.title ? { title: tool.title } : {}),
      inputSchema: tool.inputSchema,
      ...(tool.annotations ? { annotations: tool.annotations } : {}),
    }));
  }

  async callTool(name: string, args: Record<string, unknown>): Promise<unknown> {
    const client = await this.#connectedClient();
    return client.callTool({ name, arguments: args }, undefined, { timeout: this.#config.call_timeout_ms });
  }

  async close(): Promise<void> {
    await this.#transport?.close();
    this.#client = undefined;
    this.#transport = undefined;
  }

  async #connectedClient(): Promise<Client> {
    if (this.#client) {
      return this.#client;
    }
    if (!this.#config.command) {
      throw new Error("official_relay_command_not_configured");
    }

    const client = new Client({ name: "sgg-unity-mcp-gateway", version: "2026.5" }, { capabilities: {} });
    const transport = new StdioClientTransport({
      command: this.#config.command,
      args: this.#config.args,
      env: { ...stringEnv(process.env), ...this.#config.env },
      stderr: "pipe",
    });
    await client.connect(transport, { timeout: this.#config.connect_timeout_ms });
    this.#client = client;
    this.#transport = transport;
    return client;
  }
}

export function officialCommandId(toolName: string): string {
  return `official.${toolName}`;
}

export function officialToolName(commandId: string): string | undefined {
  return commandId.startsWith("official.") ? commandId.slice("official.".length) : undefined;
}

function normalizeInputSchema(schema: Record<string, unknown> | undefined): Record<string, unknown> {
  if (!schema || schema.type !== "object") {
    return { type: "object", properties: {}, additionalProperties: false };
  }

  return schema;
}

function inferRisk(tool: OfficialRelayTool): OfficialRelayRisk {
  if (tool.annotations?.destructiveHint) {
    return "destructive";
  }
  if (tool.annotations?.openWorldHint) {
    return "network";
  }
  if (tool.annotations?.readOnlyHint) {
    return "read";
  }
  return "execute";
}

function validateConfiguredPathFields(args: Record<string, unknown>, fields: string[]): BridgeInvokeResponse | undefined {
  for (const field of fields) {
    const value = args[field];
    if (value === undefined || value === null) {
      continue;
    }
    if (typeof value !== "string") {
      return relayError("invalid_field_value", `Field '${field}' must be a Unity-relative asset path string`);
    }

    try {
      validateUnityAssetPath(value);
    } catch (error) {
      return relayError(pathErrorCode(error), `Field '${field}' must be a Unity-relative Assets/... or Packages/... path`);
    }
  }

  return undefined;
}

function pathErrorCode(error: unknown): ErrorCode {
  const message = error instanceof Error ? error.message : "path_not_allowed";
  return message === "absolute_path_rejected" || message === "path_traversal_rejected" || message === "path_not_allowed" ? message : "path_not_allowed";
}

function relayError(code: ErrorCode, message: string): BridgeInvokeResponse {
  return { status: "error", error: { code, message }, duration_ms: 0 };
}

function classifyRelayError(error: unknown): string {
  const message = errorMessage(error).toLowerCase();
  if (message.includes("paywall") || message.includes("subscription") || message.includes("license") || message.includes("unauthorized") || message.includes("auth")) {
    return "paywalled_or_auth_blocked";
  }
  if (message.includes("timeout")) {
    return "timeout";
  }
  if (message.includes("not_configured") || message.includes("enoent") || message.includes("not found")) {
    return "missing";
  }
  return "protocol_or_relay_error";
}

function errorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function stringEnv(env: NodeJS.ProcessEnv): Record<string, string> {
  return Object.fromEntries(Object.entries(env).filter((entry): entry is [string, string] => typeof entry[1] === "string"));
}
