import { isRawCommandSelector } from "./contracts.js";

const MAX_PUBLIC_PROJECT_SELECTOR_CHARS = 256;
const PUBLIC_PROJECT_SELECTOR_PATTERN = /^[A-Za-z0-9][A-Za-z0-9 ._():-]{0,255}$/;
const COMMAND_ID_PATTERN = /^[A-Za-z0-9][A-Za-z0-9_-]*(?:\.[A-Za-z0-9][A-Za-z0-9_-]*)+$/;

export const SCHEMA_RECOVERY_ERROR_CODES = [
  "schema_validation_failed",
  "invalid_field_value",
  "schema_unavailable",
  "args_json_must_be_object",
  "items_json_must_be_array",
  "invalid_json",
  "missing_required_field",
] as const;

export type SchemaRecoveryErrorCode = (typeof SCHEMA_RECOVERY_ERROR_CODES)[number];

export type SchemaRecoverySelectorSource = "original_selector" | "resolved_project_id";

export type ExecutableMcpAction = {
  action: "sgg_unity_projects" | "sgg_unity_describe";
  action_kind: "mcp_tool";
  tool: "sgg_unity_projects" | "sgg_unity_describe";
  arguments: Record<string, unknown>;
  selector_source?: SchemaRecoverySelectorSource;
};

export type SchemaRecoveryPlan = {
  actions: ExecutableMcpAction[];
  schemaLookup: Record<string, unknown>;
  safeProject?: string;
  safeCommand?: string;
  selectorSource?: SchemaRecoverySelectorSource;
};

export function buildSafeExecutableSchemaRecovery(input: {
  projectSelector?: unknown;
  resolvedProjectId?: unknown;
  command?: unknown;
}): SchemaRecoveryPlan {
  const originalProject = safePublicProjectSelector(input.projectSelector);
  const resolvedProject = safePublicProjectSelector(input.resolvedProjectId);
  const safeProject = originalProject ?? resolvedProject;
  const selectorSource: SchemaRecoverySelectorSource | undefined = originalProject
    ? "original_selector"
    : resolvedProject
      ? "resolved_project_id"
      : undefined;
  const safeCommand = safeCommandId(input.command);

  const lookupAction = !safeProject
    ? mcpAction("sgg_unity_projects", {})
    : mcpAction("sgg_unity_describe", {
        project: safeProject,
        selector: safeCommand ? `command:${safeCommand}` : "commands",
      }, selectorSource);
  const schemaLookup = compatibilitySchemaLookup(lookupAction, safeCommand);

  return {
    actions: [lookupAction],
    schemaLookup,
    ...(safeProject ? { safeProject } : {}),
    ...(safeCommand ? { safeCommand } : {}),
    ...(selectorSource ? { selectorSource } : {}),
  };
}

export function safePublicProjectSelector(value: unknown): string | undefined {
  if (typeof value !== "string" || value.length === 0 || value.length > MAX_PUBLIC_PROJECT_SELECTOR_CHARS || value !== value.trim()) return undefined;
  if (!PUBLIC_PROJECT_SELECTOR_PATTERN.test(value) || /[\u0000-\u001f\u007f]/.test(value)) return undefined;
  if (value === "." || value === ".." || /^[A-Za-z]:/.test(value) || value.includes("/") || value.includes("\\")) return undefined;
  return value;
}

export function safeCommandId(value: unknown): string | undefined {
  return typeof value === "string" && value.length <= 256 && isRawCommandSelector(value) && COMMAND_ID_PATTERN.test(value) ? value : undefined;
}

export function isSchemaRecoveryErrorCode(value: unknown): value is SchemaRecoveryErrorCode {
  return typeof value === "string" && (SCHEMA_RECOVERY_ERROR_CODES as readonly string[]).includes(value);
}

function mcpAction<TTool extends ExecutableMcpAction["tool"]>(tool: TTool, args: Record<string, unknown>, selectorSource?: SchemaRecoverySelectorSource): ExecutableMcpAction {
  return {
    action: tool,
    action_kind: "mcp_tool",
    tool,
    arguments: args,
    ...(selectorSource ? { selector_source: selectorSource } : {}),
  };
}

function compatibilitySchemaLookup(action: ExecutableMcpAction, command: string | undefined): Record<string, unknown> {
  const project = typeof action.arguments.project === "string" ? action.arguments.project : undefined;
  const selector = typeof action.arguments.selector === "string" ? action.arguments.selector : undefined;
  return {
    action_kind: "mcp_tool",
    tool: action.tool,
    ...(project ? { project } : {}),
    ...(selector ? { selector } : {}),
    arguments: action.arguments,
    ...(command && action.tool === "sgg_unity_describe" ? { bridge_command_id: command } : {}),
    ...(action.selector_source ? { selector_source: action.selector_source } : {}),
    metadata_lookup: action,
  };
}
