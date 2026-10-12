import fs from "node:fs";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { ErrorCode, GatewayError } from "./results.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";

const FEATURE_FLAG = "sgg_mcp_plans";
const MAX_PLAN_CONTENT_CHARS = 32 * 1024;
const MAX_PLAN_READ_CHARS = 64 * 1024;
const DEFAULT_PLAN_READ_CHARS = 32 * 1024;
const MAX_LIST_RESULTS = 100;
const DEFAULT_LIST_RESULTS = 20;
const CORRELATION_ID_PATTERN = "^[A-Za-z0-9._-]{1,128}$";

export const PLAN_TEMPLATE = `# SGGMcp Plan

## Goal

## Project

## Read-only discovery

## Proposed changes

## Commands to run

## Files to touch

## Risks

## Checkpoint / rollback

## Approval required

## Rollback
`;

export const PLAN_COMMANDS: CommandDescriptor[] = [
  {
    id: "plan.template",
    title: "Plan template",
    description: "Return the bounded SGGMcp plan template without reading project files.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["plan"],
    capability: "plan",
    result_shape: "text_artifact",
    output_limit: { max_chars: PLAN_TEMPLATE.length },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "plan.list",
    title: "List plans",
    description: "List latest bounded plan files under the local or explicit versioned project plans root.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        versioned: { type: "boolean" },
        max_results: { type: "integer", minimum: 1, maximum: MAX_LIST_RESULTS },
      },
      additionalProperties: false,
    },
    tags: ["plan"],
    capability: "plan",
    result_shape: "object_details",
    output_limit: { max_results: MAX_LIST_RESULTS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "plan.read",
    title: "Read plan",
    description: "Read one bounded plan file by relative name under an approved plans root.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 160 },
        versioned: { type: "boolean" },
        max_chars: { type: "integer", minimum: 1, maximum: MAX_PLAN_READ_CHARS },
      },
      required: ["name"],
      additionalProperties: false,
    },
    tags: ["plan"],
    capability: "plan",
    result_shape: "text_artifact",
    output_limit: { max_chars: MAX_PLAN_READ_CHARS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "plan.create",
    title: "Create plan",
    description: "Create a bounded plan file under the local plans root, or under .mcp/plans only with versioned=true and policy approval.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 160 },
        content: { type: "string", maxLength: MAX_PLAN_CONTENT_CHARS },
        versioned: { type: "boolean" },
        overwrite: { type: "boolean" },
        gateway_version: { type: "string", maxLength: 64 },
        unity_package_version: { type: "string", maxLength: 64 },
        checkpoint_id: { type: "string", minLength: 1, maxLength: 128, pattern: CORRELATION_ID_PATTERN },
      },
      additionalProperties: false,
    },
    tags: ["plan"],
    capability: "plan",
    result_shape: "object_details",
    output_limit: { max_chars: 4096 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type PlanCommandResult =
  | { status: "ok"; data: unknown }
  | { status: "error"; error: GatewayError };

type PlanCommandError = Extract<PlanCommandResult, { status: "error" }>;
type PlansRoot = { fullPath: string; relativeRoot: string; versioned: boolean };
type BoundPlanPath = { status: "ok"; root: PlansRoot; name: string; fullPath: string };
type SafePlanName = { status: "ok"; name: string };

export function findPlanCommand(commandId: string): CommandDescriptor | undefined {
  return PLAN_COMMANDS.find((command) => command.id === commandId);
}

export function runPlanCommand(commandId: string, args: Record<string, unknown>, record: HeartbeatRecord | undefined, now: Date = new Date()): PlanCommandResult {
  if (!record) {
    return error("project_offline", "Plan commands require a resolved live project heartbeat");
  }

  switch (commandId) {
    case "plan.template":
      return { status: "ok", data: { template: PLAN_TEMPLATE, max_content_chars: MAX_PLAN_CONTENT_CHARS } };
    case "plan.list":
      return { status: "ok", data: listPlans(args, record) };
    case "plan.read":
      return readPlan(args, record);
    case "plan.create":
      return createPlan(args, record, now);
    default:
      return error("command_not_found", "Plan command not found");
  }
}

function listPlans(args: Record<string, unknown>, record: HeartbeatRecord): Record<string, unknown> {
  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_LIST_RESULTS, 1, MAX_LIST_RESULTS);
  const root = plansRoot(record, args.versioned === true);
  if (!fs.existsSync(root.fullPath)) {
    return { root: root.relativeRoot, versioned: root.versioned, plans: [], max_results: maxResults, truncated: false };
  }

  const plans = fs.readdirSync(root.fullPath, { withFileTypes: true })
    .filter((entry) => entry.isFile() && entry.name.endsWith(".md"))
    .map((entry) => {
      const fullPath = path.join(root.fullPath, entry.name);
      const stat = fs.statSync(fullPath);
      return { name: entry.name, relative_path: `${root.relativeRoot}/${entry.name}`, bytes: stat.size, modified_at: stat.mtime.toISOString() };
    })
    .sort((left, right) => right.modified_at.localeCompare(left.modified_at) || left.name.localeCompare(right.name));

  return { root: root.relativeRoot, versioned: root.versioned, plans: plans.slice(0, maxResults), max_results: maxResults, truncated: plans.length > maxResults };
}

function readPlan(args: Record<string, unknown>, record: HeartbeatRecord): PlanCommandResult {
  const name = typeof args.name === "string" ? args.name : "";
  const bound = bindPlanPath(record, args.versioned === true, name);
  if (bound.status === "error") {
    return bound;
  }

  if (!fs.existsSync(bound.fullPath)) {
    return error("asset_not_found", "Plan file was not found under the approved plans root");
  }

  const maxChars = clampInteger(typeof args.max_chars === "number" ? args.max_chars : DEFAULT_PLAN_READ_CHARS, 1, MAX_PLAN_READ_CHARS);
  const text = fs.readFileSync(bound.fullPath, "utf8");
  return {
    status: "ok",
    data: {
      name: bound.name,
      relative_path: `${bound.root.relativeRoot}/${bound.name}`,
      versioned: bound.root.versioned,
      content: text.slice(0, maxChars),
      max_chars: maxChars,
      truncated: text.length > maxChars,
    },
  };
}

function createPlan(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): PlanCommandResult {
  const versioned = args.versioned === true;
  const name = typeof args.name === "string" && args.name.length > 0 ? args.name : defaultPlanName(now);
  const bound = bindPlanPath(record, versioned, name);
  if (bound.status === "error") {
    return bound;
  }

  const content = typeof args.content === "string" ? args.content : PLAN_TEMPLATE;
  if (content.length > MAX_PLAN_CONTENT_CHARS) {
    return error("invalid_field_value", "Plan content exceeds the bounded content limit", { field: "content", max_chars: MAX_PLAN_CONTENT_CHARS });
  }

  if (fs.existsSync(bound.fullPath) && args.overwrite !== true) {
    return error("invalid_field_value", "Plan file already exists; pass overwrite=true to replace it", { field: "overwrite" });
  }

  const checkpointId = typeof args.checkpoint_id === "string" ? args.checkpoint_id : undefined;

  fs.mkdirSync(bound.root.fullPath, { recursive: true });
  const metadata = [
    "<!-- sggmcp-plan-metadata",
    `created_at: ${now.toISOString()}`,
    `project_id: ${record.project}`,
    `gateway_version: ${typeof args.gateway_version === "string" && args.gateway_version.length > 0 ? args.gateway_version : "unknown"}`,
    `unity_package_version: ${typeof args.unity_package_version === "string" && args.unity_package_version.length > 0 ? args.unity_package_version : "unknown"}`,
    ...(checkpointId ? [`checkpoint_id: ${checkpointId}`] : []),
    `versioned: ${versioned}`,
    "-->",
    "",
  ].join("\n");
  fs.writeFileSync(bound.fullPath, metadata + content, { encoding: "utf8", flag: args.overwrite === true ? "w" : "wx" });
  const stat = fs.statSync(bound.fullPath);
  return {
    status: "ok",
    data: {
      created: true,
      name: bound.name,
      relative_path: `${bound.root.relativeRoot}/${bound.name}`,
      versioned,
      bytes: stat.size,
      overwrite: args.overwrite === true,
      automatic_apply: false,
      ...(checkpointId ? { checkpoint_id: checkpointId } : {}),
    },
  };
}

function bindPlanPath(record: HeartbeatRecord, versioned: boolean, name: string): BoundPlanPath | PlanCommandError {
  const safeName = safeRelativePlanName(name);
  if (safeName.status === "error") {
    return safeName;
  }

  const root = plansRoot(record, versioned);
  const fullPath = path.join(root.fullPath, safeName.name);
  if (!isUnderRoot(fullPath, root.fullPath)) {
    return error("path_traversal_rejected", "Plan path must stay under the approved plans root");
  }

  return { status: "ok", root, name: safeName.name, fullPath };
}

function safeRelativePlanName(name: string): SafePlanName | PlanCommandError {
  const normalized = name.replaceAll("\\", "/").trim();
  if (path.isAbsolute(normalized) || /^[A-Za-z]:\//.test(normalized)) {
    return error("absolute_path_rejected", "Plan name must be relative to the selected plans root");
  }
  if (!normalized.endsWith(".md") || normalized.length > 160 || normalized.split("/").some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    return error("path_traversal_rejected", "Plan name must be a safe relative .md path without traversal");
  }
  if (!/^[A-Za-z0-9._/-]+$/.test(normalized)) {
    return error("invalid_field_value", "Plan name contains unsupported characters", { field: "name" });
  }
  return { status: "ok", name: normalized };
}

function plansRoot(record: HeartbeatRecord, versioned: boolean): PlansRoot {
  const relativeRoot = versioned ? ".mcp/plans" : "Library/SGGUnityMcp/plans";
  return { fullPath: path.join(record.project_root, relativeRoot), relativeRoot, versioned };
}

function isUnderRoot(candidate: string, root: string): boolean {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

function defaultPlanName(now: Date): string {
  return `plan-${now.toISOString().replaceAll(/[:.]/g, "-")}.md`;
}

function clampInteger(value: number, min: number, max: number): number {
  if (!Number.isInteger(value)) {
    return min;
  }
  return Math.min(max, Math.max(min, value));
}

function error(code: ErrorCode, message: string, details?: Record<string, unknown>): PlanCommandError {
  return { status: "error", error: { code, message, ...(details ? { details } : {}) } };
}

function emptySchema(): Record<string, unknown> {
  return { type: "object", properties: {}, additionalProperties: false };
}
