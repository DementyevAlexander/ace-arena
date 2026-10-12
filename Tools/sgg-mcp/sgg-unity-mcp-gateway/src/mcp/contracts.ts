import { z } from "zod";

import { HEARTBEAT_STATUSES } from "../bridge/bridge-protocol.js";

export const MCP_CALLER_BUDGET_MS = 300_000;
export const MCP_RESPONSE_DELIVERY_RESERVE_MS = 5_000;
export const MAX_POST_ADMISSION_WAIT_MS = MCP_CALLER_BUDGET_MS - MCP_RESPONSE_DELIVERY_RESERVE_MS;
import { ERROR_CODES } from "./results.js";

export const TOOL_NAMES = [
  "sgg_unity_projects",
  "sgg_unity_stale_projects",
  "sgg_unity_stale_registry_cleanup",
  "sgg_unity_describe",
  "sgg_unity_call",
  "sgg_unity_batch",
  "sgg_unity_job",
  "sgg_unity_logs",
] as const;

export type ToolName = (typeof TOOL_NAMES)[number];

export const DESCRIBE_SELECTORS = [
  "status",
  "commands",
  "catalog",
  "packages",
  "scenes",
  "selection",
  "logs",
] as const;

export const COMMAND_GROUP_SELECTORS = [
  "scene",
  "prefab",
  "asset",
  "project",
  "gameobject",
  "component",
  "material",
  "editor",
  "package",
  "addressables",
  "build",
  "test",
  "runtime",
  "unity-api",
  "unity-docs",
  "ui",
  "ugui",
  "uitk",
  "diff",
  "knowledge",
  "diagnostics",
  "plan",
  "pipeline",
  "checkpoint",
  "artifact",
  "skill",
  "policy",
  "official",
] as const;

export const BATCH_MODES = ["sequential", "parallel_by_project"] as const;

const nonEmptyString = z.string().trim().min(1);
const correlationIdSchema = z.string().regex(/^[A-Za-z0-9._-]{1,128}$/, "correlation id must match ^[A-Za-z0-9._-]{1,128}$");
const staleRecordIdSchema = z.string().regex(/^sha256:[a-f0-9]{64}$/, "stale record id must be a sha256 digest");

export const commandSelectorSchema = z
  .string()
  .refine((value) => DESCRIBE_SELECTORS.includes(value as (typeof DESCRIBE_SELECTORS)[number]) || isCommandGroupSelector(value) || isCommandGroupAlias(value) || /^command:[A-Za-z0-9_.-]+$/.test(value) || isRawCommandSelector(value), {
    message: "selector must be one of the supported selectors, commands:<group>, command:<id>, a command group alias, or a command id",
  });

export const projectsArgsSchema = z.object({}).strict();

export const staleProjectsArgsSchema = z
  .object({
    project: z.string().trim().min(1).max(4_096).optional(),
    status: z.enum(HEARTBEAT_STATUSES).optional(),
    last_heartbeat_from: z.string().datetime({ offset: true }).optional(),
    last_heartbeat_to: z.string().datetime({ offset: true }).optional(),
    cursor: z.string().regex(/^[A-Za-z0-9_-]{1,512}$/, "cursor must be bounded base64url").optional(),
    limit: z.number().int().min(1).max(16).default(8),
  })
  .strict()
  .superRefine((args, context) => {
    if (args.last_heartbeat_from && args.last_heartbeat_to && Date.parse(args.last_heartbeat_from) > Date.parse(args.last_heartbeat_to)) {
      context.addIssue({ code: "custom", path: ["last_heartbeat_to"], message: "last_heartbeat_to must not precede last_heartbeat_from" });
    }
  });

export const staleRegistryCleanupArgsSchema = z
  .object({
    dry_run: z.boolean().default(true),
    project: z.string().trim().min(1).max(4_096).optional(),
    status: z.enum(HEARTBEAT_STATUSES).optional(),
    last_heartbeat_before: z.string().datetime({ offset: true }).optional(),
    limit: z.number().int().min(1).max(100).default(25),
    record_ids: z.array(staleRecordIdSchema).min(1).max(100).refine((values) => new Set(values).size === values.length, "record_ids must be unique").optional(),
    precondition_hash: staleRecordIdSchema.optional(),
    confirm_cleanup: z.boolean().default(false),
  })
  .strict();

export const describeArgsSchema = z
  .object({
    project: nonEmptyString,
    selector: commandSelectorSchema.default("status"),
  })
  .strict();

export const callArgsSchema = z
  .object({
    project: nonEmptyString,
    command: nonEmptyString,
    args_json: z.string().default("{}"),
    mode: z.enum(["wait", "job"]).default("wait"),
    request_id: nonEmptyString.optional(),
    checkpoint_id: correlationIdSchema.optional(),
    plan_id: correlationIdSchema.optional(),
    wait_for_replacement_ms: z.number().int().min(1).max(3_600_000).optional(),
    wait_for_test_run_terminal_ms: z.number().int().min(1).max(MAX_POST_ADMISSION_WAIT_MS).optional(),
  })
  .strict();

export const batchArgsSchema = z
  .object({
    mode: z.enum(BATCH_MODES).default("sequential"),
    items_json: z.string(),
    request_id: nonEmptyString.optional(),
    checkpoint_id: correlationIdSchema.optional(),
    plan_id: correlationIdSchema.optional(),
  })
  .strict();

export const jobArgsSchema = z
  .object({
    project: nonEmptyString,
    job_id: nonEmptyString,
    action: z.enum(["status", "cancel"]).default("status"),
  })
  .strict();

export const logsArgsSchema = z
  .object({
    project: nonEmptyString,
    kind: z.enum(["console", "bridge", "jobs"]).default("console"),
    limit: z.number().int().min(1).max(1000).default(200),
  })
  .strict();

const diagnosticSchema = z.object({
  severity: z.enum(["info", "warning", "error"]),
  source: z.enum(["gateway", "bridge", "unity"]),
  message: z.string(),
});

const gatewayErrorSchema = z.object({
  code: z.enum(ERROR_CODES),
  message: z.string(),
  details: z.record(z.string(), z.unknown()).optional(),
});

const policyStatusSchema = z.object({
  state: z.enum(["unconfigured", "active", "degraded", "error"]),
  configured: z.boolean(),
  degraded: z.boolean(),
  effective_policy_revision: z.string().nullable(),
  loaded_at: z.string().nullable(),
  last_attempt_at: z.string().nullable(),
  last_error_kind: z.enum(["missing", "unreadable", "too_large", "unstable", "invalid_json", "invalid_schema"]).nullable(),
  last_error_code: z.enum(["policy_reload_failed", "policy_invalid"]).nullable(),
  degraded_since: z.string().nullable(),
}).strict();

const batchItemExecutionSchema = z.object({
  index: z.number().int().nonnegative(),
  state: z.enum(["completed", "delivered", "not_dispatched", "not_started", "uncertain"]),
  queued: z.boolean(),
  admitted: z.boolean(),
  bridge_request_issued: z.boolean(),
  response_delivered: z.boolean(),
  automatic_replay_attempted: z.boolean(),
}).strict();

export const gatewayResultOutputSchema = z
  .object({
    status: z.enum(["ok", "error"]),
    request_id: z.string().optional(),
    project: z.string().optional(),
    command: z.string().optional(),
    data: z.unknown().optional(),
    error: gatewayErrorSchema.optional(),
    diagnostics: z.array(diagnosticSchema),
    next_valid_actions: z.array(z.string()).optional(),
    artifact_refs: z.array(z.string()).optional(),
    truncated: z.boolean().optional(),
    policy_decision: z.unknown().optional(),
    policy_status: policyStatusSchema.optional(),
    batch_item: batchItemExecutionSchema.optional(),
  })
  .strict();

export const TOOL_ARG_SCHEMAS = {
  sgg_unity_projects: projectsArgsSchema,
  sgg_unity_stale_projects: staleProjectsArgsSchema,
  sgg_unity_stale_registry_cleanup: staleRegistryCleanupArgsSchema,
  sgg_unity_describe: describeArgsSchema,
  sgg_unity_call: callArgsSchema,
  sgg_unity_batch: batchArgsSchema,
  sgg_unity_job: jobArgsSchema,
  sgg_unity_logs: logsArgsSchema,
} as const satisfies Record<ToolName, z.ZodType>;

export type JsonObject = Record<string, unknown>;

const batchItemSchema = z
  .object({
    project: nonEmptyString,
    command: nonEmptyString,
    args: z.record(z.string(), z.unknown()).optional(),
    args_json: z.string().optional(),
    mode: z.enum(["wait", "job"]).default("wait"),
    request_id: nonEmptyString.optional(),
  })
  .strict()
  .superRefine((item, ctx) => {
    if (item.args !== undefined && item.args_json !== undefined) {
      ctx.addIssue({
        code: "custom",
        path: ["args_json"],
        message: "args and args_json are mutually exclusive",
      });
    }
  });

export type BatchItem = z.infer<typeof batchItemSchema>;

export function parseArgsJson(value: string): JsonObject {
  const parsed = parseJson(value, "args_json");
  if (!isJsonObject(parsed)) {
    throw new Error("args_json_must_be_object");
  }
  return parsed;
}

export function parseItemsJson(value: string): BatchItem[] {
  const parsed = parseJson(value, "items_json");
  if (!Array.isArray(parsed)) {
    throw new Error("items_json_must_be_array");
  }

  const batchItems = z.array(batchItemSchema).parse(parsed);
  for (const item of batchItems) {
    if (item.args_json !== undefined) {
      parseArgsJson(item.args_json);
    }
  }
  return batchItems;
}

export function isCommandGroupSelector(value: string): boolean {
  const match = /^commands:([A-Za-z0-9_-]+)$/.exec(value);
  return !!match?.[1] && COMMAND_GROUP_SELECTORS.includes(match[1] as (typeof COMMAND_GROUP_SELECTORS)[number]);
}

export function isCommandGroupAlias(value: string): boolean {
  return COMMAND_GROUP_SELECTORS.includes(value as (typeof COMMAND_GROUP_SELECTORS)[number]);
}

export function isRawCommandSelector(value: string): boolean {
  return /^[A-Za-z0-9_.-]+$/.test(value) && value.includes(".");
}

export function normalizeDescribeSelector(value: string): string {
  if (value === "catalog") {
    return "commands";
  }
  if (isCommandGroupAlias(value)) {
    return `commands:${value}`;
  }
  if (isRawCommandSelector(value)) {
    return `command:${value}`;
  }
  return value;
}

function parseJson(value: string, label: string): unknown {
  try {
    return JSON.parse(value);
  } catch {
    throw new Error(`${label}_invalid_json`);
  }
}

function isJsonObject(value: unknown): value is JsonObject {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
