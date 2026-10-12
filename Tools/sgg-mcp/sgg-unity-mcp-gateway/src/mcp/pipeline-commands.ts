import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";
import type { ErrorCode, GatewayError } from "./results.js";

const FEATURE_FLAG = "sgg_mcp_pipelines";
const MAX_PIPELINE_CONTENT_CHARS = 64 * 1024;
const MAX_PIPELINE_READ_CHARS = 64 * 1024;
const DEFAULT_PIPELINE_READ_CHARS = 32 * 1024;
const MAX_LIST_RESULTS = 100;
const DEFAULT_LIST_RESULTS = 20;
const MAX_SCAN_ENTRIES = 500;
const MAX_STEPS = 50;
const COMMAND_ID_PATTERN = /^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$/;
const ACTION_ID_PATTERN = /^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)*$/;
const SAFE_ID_PATTERN_SOURCE = "^[A-Za-z0-9._-]{1,80}$";
const SAFE_ID_PATTERN = /^[A-Za-z0-9._-]{1,80}$/;
const BANNED_KEYS = new Set(["approval_bypass", "approvalBypass", "bypass_approval", "bypassApproval", "auto_run", "autoRun", "run_until_gate", "runUntilGate", "run_next", "runNext"]);
const BANNED_KEY_NAMES = new Set([...BANNED_KEYS].map((key) => key.toLowerCase()));
const BANNED_ACTIONS = new Set(["auto-run", "auto_run", "autorun", "run-until-gate", "run_until_gate", "run-next", "run_next"]);
const PIPELINE_RUNS_ROOT = "Library/SGGUnityMcp/pipeline-runs";
const MAX_RUN_STATE_CHARS = 128 * 1024;
const MAX_RECORD_OBJECT_CHARS = 16 * 1024;
const MAX_RECORD_NOTES_CHARS = 4096;
const MAX_RECORD_FAILURE_CHARS = 8192;
const MAX_RECORD_EVIDENCE_ITEMS = 20;
const MAX_RECORD_EVIDENCE_CHARS = 2048;
const MAX_RECORD_ARTIFACT_REFS = 20;
const MAX_RUN_ARTIFACT_REFS = 100;
const MAX_HISTORY_RESULTS = 100;
const DEFAULT_HISTORY_RESULTS = 20;

export const PIPELINE_TEMPLATE = `# SGGMcp Pipeline Template

This is a bounded planning artifact. It is never executed automatically.

\`\`\`json
{
  "schema_version": 1,
  "title": "Bounded Unity workflow",
  "steps": [
    {
      "id": "inspect-compile-status",
      "command": "compile.status",
      "project": "<explicit-project-id>",
      "args": {},
      "notes": "Use sgg_unity_call manually after policy approval if required."
    }
  ]
}
\`\`\`
`;

export const PIPELINE_COMMANDS: CommandDescriptor[] = [
  {
    id: "pipeline.template",
    title: "Pipeline template",
    description: "Return a bounded generic SGGMcp pipeline template without reading project files.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "text_artifact",
    output_limit: { max_chars: PIPELINE_TEMPLATE.length },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.list",
    title: "List pipelines",
    description: "List bounded pipeline files under the local or explicit versioned project pipeline root.",
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
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "object_details",
    output_limit: { max_results: MAX_LIST_RESULTS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.read",
    title: "Read pipeline",
    description: "Read one bounded pipeline file by safe relative .md or .json name under an approved pipeline root.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 160 },
        versioned: { type: "boolean" },
        max_chars: { type: "integer", minimum: 1, maximum: MAX_PIPELINE_READ_CHARS },
      },
      required: ["name"],
      additionalProperties: false,
    },
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "text_artifact",
    output_limit: { max_chars: MAX_PIPELINE_READ_CHARS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.create",
    title: "Create pipeline",
    description: "Create a bounded pipeline file under the local pipeline root, or under .mcp/pipelines only with versioned=true and policy approval.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 160 },
        content: { type: "string", maxLength: MAX_PIPELINE_CONTENT_CHARS },
        versioned: { type: "boolean" },
        overwrite: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "object_details",
    output_limit: { max_chars: 4096 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.validate",
    title: "Validate pipeline",
    description: "Validate bounded pipeline content or a named pipeline for safe structure without executing commands.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 160 },
        content: { type: "string", maxLength: MAX_PIPELINE_CONTENT_CHARS },
        versioned: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "object_details",
    output_limit: { max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.start",
    title: "Start pipeline run",
    description: "Create local manual run state from a validated pipeline definition without executing the first step.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        name: { type: "string", minLength: 1, maxLength: 160 },
        content: { type: "string", maxLength: MAX_PIPELINE_CONTENT_CHARS },
        versioned: { type: "boolean" },
        run_id: { type: "string", minLength: 1, maxLength: 80, pattern: SAFE_ID_PATTERN_SOURCE },
        inputs: { type: "object", additionalProperties: true },
        notes: { type: "string", maxLength: MAX_RECORD_NOTES_CHARS },
      },
      additionalProperties: false,
    },
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "object_details",
    output_limit: { max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.status",
    title: "Pipeline run status",
    description: "Read local manual pipeline run state, current step, recorded evidence and next manual actions without executing steps.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        run_id: { type: "string", minLength: 1, maxLength: 80, pattern: SAFE_ID_PATTERN_SOURCE },
        max_history: { type: "integer", minimum: 1, maximum: MAX_HISTORY_RESULTS },
      },
      required: ["run_id"],
      additionalProperties: false,
    },
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "object_details",
    output_limit: { max_chars: 16384, max_results: MAX_HISTORY_RESULTS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.record_step",
    title: "Record pipeline step",
    description: "Record a manually executed pipeline step result, produced ids, artifact refs and evidence without claiming Gateway execution.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        run_id: { type: "string", minLength: 1, maxLength: 80, pattern: SAFE_ID_PATTERN_SOURCE },
        step_id: { type: "string", minLength: 1, maxLength: 80, pattern: SAFE_ID_PATTERN_SOURCE },
        step_index: { type: "integer", minimum: 0, maximum: MAX_STEPS - 1 },
        result: { type: "string", enum: ["success", "failure", "blocked", "skipped"] },
        produced: { type: "object", additionalProperties: true },
        artifact_refs: { type: "array", maxItems: MAX_RECORD_ARTIFACT_REFS, items: { type: "string", minLength: 1, maxLength: 240 } },
        evidence: { type: "array", maxItems: MAX_RECORD_EVIDENCE_ITEMS, items: { type: "string", minLength: 1, maxLength: MAX_RECORD_EVIDENCE_CHARS } },
        notes: { type: "string", maxLength: MAX_RECORD_NOTES_CHARS },
        failure: {},
        next_step_id: { type: "string", minLength: 1, maxLength: 80, pattern: SAFE_ID_PATTERN_SOURCE },
      },
      required: ["run_id", "result"],
      additionalProperties: false,
    },
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "object_details",
    output_limit: { max_chars: 16384 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "pipeline.cancel",
    title: "Cancel pipeline run",
    description: "Mark a local manual pipeline run cancelled while preserving evidence and without rolling back side effects.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        run_id: { type: "string", minLength: 1, maxLength: 80, pattern: SAFE_ID_PATTERN_SOURCE },
        reason: { type: "string", maxLength: MAX_RECORD_NOTES_CHARS },
        evidence: { type: "array", maxItems: MAX_RECORD_EVIDENCE_ITEMS, items: { type: "string", minLength: 1, maxLength: MAX_RECORD_EVIDENCE_CHARS } },
      },
      required: ["run_id"],
      additionalProperties: false,
    },
    tags: ["pipeline"],
    capability: "pipeline",
    result_shape: "object_details",
    output_limit: { max_chars: 16384 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type PipelineCommandResult = { status: "ok"; data: unknown } | { status: "error"; error: GatewayError };

type PipelineCommandError = Extract<PipelineCommandResult, { status: "error" }>;
type PipelineRoot = { fullPath: string; relativeRoot: string; versioned: boolean };
type BoundPipelinePath = { status: "ok"; root: PipelineRoot; name: string; fullPath: string };
type ValidationIssue = { path: string; message: string };
type PipelineFormat = "json" | "markdown";
type PipelineStepResult = "success" | "failure" | "blocked" | "skipped";
type PipelineRunStatus = "running" | "blocked" | "completed" | "cancelled";
type PipelineRunStepStatus = "pending" | PipelineStepResult;
type BoundRunPath = { status: "ok"; runId: string; runDir: string; fullPath: string; relativePath: string };
type LoadedPipelineDefinition = { status: "ok"; content: string; source: PipelineDefinitionSource };
type LoadedRunState = { status: "ok"; state: PipelineRunState; bound: BoundRunPath };
type PipelineDefinitionSource = { kind: "inline" } | { kind: "file"; name: string; versioned: boolean; relative_path: string };
type PipelineDefinitionAnalysis = {
  valid: boolean;
  format: PipelineFormat;
  step_count: number;
  issues: ValidationIssue[];
  definition?: PipelineDefinition;
};
type PipelineDefinition = {
  pipeline_id?: string;
  title?: string;
  mode?: string;
  steps: PipelineDefinitionStep[];
};
type PipelineDefinitionStep = {
  id: string;
  index: number;
  action: string;
  manual_execution_required: true;
  gateway_executed: false;
  gateway_verified: false;
  command?: string;
  project?: string;
  risk?: string;
  args?: unknown;
  notes?: string;
  produces?: string[];
  transitions?: unknown;
  next?: string;
  requires_user_approval?: boolean;
};
type PipelineRunStepRecord = {
  recorded_at: string;
  step_id: string;
  step_index: number;
  result: PipelineStepResult;
  produced: Record<string, unknown>;
  artifact_refs: string[];
  evidence: string[];
  gateway_executed: false;
  gateway_verified: false;
  automatic_execution: false;
  notes?: string;
  failure?: unknown;
  next_step_id?: string;
};
type PipelineRunStep = PipelineDefinitionStep & {
  status: PipelineRunStepStatus;
  records: PipelineRunStepRecord[];
};
type PipelineRunState = {
  schema_version: 1;
  run_id: string;
  project_id: string;
  created_at: string;
  updated_at: string;
  status: PipelineRunStatus;
  current_step_id?: string;
  current_step_index?: number;
  definition: {
    source: PipelineDefinitionSource;
    content_sha256: string;
    format: PipelineFormat;
    step_count: number;
    pipeline_id?: string;
    title?: string;
    mode?: string;
  };
  inputs: Record<string, unknown>;
  notes?: string;
  steps: PipelineRunStep[];
  history: PipelineRunStepRecord[];
  produced: Record<string, unknown>;
  artifact_refs: string[];
  automatic_execution: false;
  rollback_performed: false;
  cancellation?: {
    cancelled_at: string;
    rollback_performed: false;
    gateway_executed_rollback: false;
    reason?: string;
    evidence: string[];
  };
};
type ResolvedNextStep = { kind: "step"; step: PipelineRunStep } | { kind: "completed" } | { kind: "blocked" } | undefined;

export function findPipelineCommand(commandId: string): CommandDescriptor | undefined {
  return PIPELINE_COMMANDS.find((command) => command.id === commandId);
}

export function runPipelineCommand(commandId: string, args: Record<string, unknown>, record: HeartbeatRecord | undefined, now: Date = new Date()): PipelineCommandResult {
  if (!record) {
    return error("project_offline", "Pipeline commands require a resolved live project heartbeat");
  }

  switch (commandId) {
    case "pipeline.template":
      return { status: "ok", data: { template: PIPELINE_TEMPLATE, formats: ["md", "json"], max_content_chars: MAX_PIPELINE_CONTENT_CHARS, automatic_execution: false } };
    case "pipeline.list":
      return { status: "ok", data: listPipelines(args, record) };
    case "pipeline.read":
      return readPipeline(args, record);
    case "pipeline.create":
      return createPipeline(args, record, now);
    case "pipeline.validate":
      return validatePipelineCommand(args, record);
    case "pipeline.start":
      return startPipelineRun(args, record, now);
    case "pipeline.status":
      return pipelineRunStatus(args, record);
    case "pipeline.record_step":
      return recordPipelineStep(args, record, now);
    case "pipeline.cancel":
      return cancelPipelineRun(args, record, now);
    default:
      return error("command_not_found", "Pipeline command not found");
  }
}

function listPipelines(args: Record<string, unknown>, record: HeartbeatRecord): Record<string, unknown> {
  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_LIST_RESULTS, 1, MAX_LIST_RESULTS);
  const root = pipelinesRoot(record, args.versioned === true);
  if (!fs.existsSync(root.fullPath)) {
    return { root: root.relativeRoot, versioned: root.versioned, pipelines: [], max_results: maxResults, truncated: false };
  }

  const found: Array<{ name: string; relative_path: string; bytes: number; modified_at: string }> = [];
  let scanned = 0;
  scanPipelineFiles(root.fullPath, root.relativeRoot, "", found, () => scanned++ < MAX_SCAN_ENTRIES);
  const pipelines = found.sort((left, right) => right.modified_at.localeCompare(left.modified_at) || left.name.localeCompare(right.name));
  return { root: root.relativeRoot, versioned: root.versioned, pipelines: pipelines.slice(0, maxResults), max_results: maxResults, truncated: pipelines.length > maxResults || scanned >= MAX_SCAN_ENTRIES };
}

function readPipeline(args: Record<string, unknown>, record: HeartbeatRecord): PipelineCommandResult {
  const bound = bindPipelinePath(record, args.versioned === true, typeof args.name === "string" ? args.name : "");
  if (bound.status === "error") {
    return bound;
  }
  if (!fs.existsSync(bound.fullPath)) {
    return error("asset_not_found", "Pipeline file was not found under the approved pipeline root");
  }

  const maxChars = clampInteger(typeof args.max_chars === "number" ? args.max_chars : DEFAULT_PIPELINE_READ_CHARS, 1, MAX_PIPELINE_READ_CHARS);
  const text = fs.readFileSync(bound.fullPath, "utf8");
  return { status: "ok", data: { name: bound.name, relative_path: `${bound.root.relativeRoot}/${bound.name}`, versioned: bound.root.versioned, content: text.slice(0, maxChars), max_chars: maxChars, truncated: text.length > maxChars } };
}

function createPipeline(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): PipelineCommandResult {
  const versioned = args.versioned === true;
  const name = typeof args.name === "string" && args.name.length > 0 ? args.name : defaultPipelineName(now);
  const bound = bindPipelinePath(record, versioned, name);
  if (bound.status === "error") {
    return bound;
  }

  const content = typeof args.content === "string" ? args.content : PIPELINE_TEMPLATE;
  if (content.length > MAX_PIPELINE_CONTENT_CHARS) {
    return error("invalid_field_value", "Pipeline content exceeds the bounded content limit", { field: "content", max_chars: MAX_PIPELINE_CONTENT_CHARS });
  }
  if (fs.existsSync(bound.fullPath) && args.overwrite !== true) {
    return error("invalid_field_value", "Pipeline file already exists; pass overwrite=true to replace it", { field: "overwrite" });
  }

  fs.mkdirSync(path.dirname(bound.fullPath), { recursive: true });
  const metadata = bound.name.endsWith(".md")
    ? ["<!-- sggmcp-pipeline-metadata", `created_at: ${now.toISOString()}`, `project_id: ${record.project}`, `versioned: ${versioned}`, "automatic_execution: false", "-->", ""].join("\n")
    : "";
  fs.writeFileSync(bound.fullPath, metadata + content, { encoding: "utf8", flag: args.overwrite === true ? "w" : "wx" });
  const stat = fs.statSync(bound.fullPath);
  return { status: "ok", data: { created: true, name: bound.name, relative_path: `${bound.root.relativeRoot}/${bound.name}`, versioned, bytes: stat.size, overwrite: args.overwrite === true, automatic_execution: false } };
}

function validatePipelineCommand(args: Record<string, unknown>, record: HeartbeatRecord): PipelineCommandResult {
  let content: string;
  let name: string | undefined;
  let versioned = args.versioned === true;
  if (typeof args.content === "string") {
    content = args.content;
  } else if (typeof args.name === "string") {
    const bound = bindPipelinePath(record, versioned, args.name);
    if (bound.status === "error") {
      return bound;
    }
    if (!fs.existsSync(bound.fullPath)) {
      return error("asset_not_found", "Pipeline file was not found under the approved pipeline root");
    }
    content = fs.readFileSync(bound.fullPath, "utf8");
    name = bound.name;
    versioned = bound.root.versioned;
  } else {
    return error("invalid_field_value", "pipeline.validate requires either content or name", { field: "content" });
  }

  if (content.length > MAX_PIPELINE_CONTENT_CHARS) {
    return error("invalid_field_value", "Pipeline content exceeds the bounded content limit", { field: "content", max_chars: MAX_PIPELINE_CONTENT_CHARS });
  }
  const validation = validatePipelineContent(content, name);
  return { status: "ok", data: { ...validation, ...(name ? { name, versioned } : {}), automatic_execution: false, max_steps: MAX_STEPS } };
}

function startPipelineRun(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): PipelineCommandResult {
  const loaded = loadPipelineDefinition(args, record);
  if (loaded.status === "error") {
    return loaded;
  }
  if (loaded.content.length > MAX_PIPELINE_CONTENT_CHARS) {
    return error("invalid_field_value", "Pipeline content exceeds the bounded content limit", { field: "content", max_chars: MAX_PIPELINE_CONTENT_CHARS });
  }

  const analysis = analyzePipelineContent(loaded.content, loaded.source.kind === "file" ? loaded.source.name : undefined);
  if (!analysis.valid || !analysis.definition) {
    return error("invalid_field_value", "Pipeline definition is not valid for a manual run", { validation: { valid: analysis.valid, format: analysis.format, step_count: analysis.step_count, issues: analysis.issues } });
  }
  if (analysis.definition.steps.length < 1) {
    return error("invalid_field_value", "pipeline.start requires at least one step", { field: "steps" });
  }
  const duplicateStepId = firstDuplicateStepId(analysis.definition.steps);
  if (duplicateStepId) {
    return error("invalid_field_value", "Pipeline step ids must be unique for run state tracking", { field: "steps", step_id: duplicateStepId });
  }

  const runId = typeof args.run_id === "string" && args.run_id.trim().length > 0 ? args.run_id.trim() : defaultRunId(now);
  const bound = bindRunPath(record, runId);
  if (bound.status === "error") {
    return bound;
  }
  if (fs.existsSync(bound.fullPath)) {
    return error("invalid_field_value", "Pipeline run already exists; run state is never overwritten", { field: "run_id", run_id: bound.runId });
  }

  const inputs = boundedRecordValue(args.inputs, "inputs", MAX_RECORD_OBJECT_CHARS);
  if (inputs.status === "error") {
    return inputs;
  }
  const notes = optionalBoundedString(args.notes, "notes", MAX_RECORD_NOTES_CHARS);
  if (notes.status === "error") {
    return notes;
  }

  const firstStep = analysis.definition.steps[0];
  if (!firstStep) {
    return error("invalid_field_value", "pipeline.start requires at least one step", { field: "steps" });
  }
  const createdAt = now.toISOString();
  const state: PipelineRunState = {
    schema_version: 1,
    run_id: bound.runId,
    project_id: record.project,
    created_at: createdAt,
    updated_at: createdAt,
    status: "running",
    current_step_id: firstStep.id,
    current_step_index: firstStep.index,
    definition: {
      source: loaded.source,
      content_sha256: sha256Hex(loaded.content),
      format: analysis.format,
      step_count: analysis.definition.steps.length,
      ...(analysis.definition.pipeline_id ? { pipeline_id: analysis.definition.pipeline_id } : {}),
      ...(analysis.definition.title ? { title: analysis.definition.title } : {}),
      ...(analysis.definition.mode ? { mode: analysis.definition.mode } : {}),
    },
    inputs: inputs.value,
    ...(notes.value ? { notes: notes.value } : {}),
    steps: analysis.definition.steps.map((step) => ({ ...step, status: "pending" as const, records: [] })),
    history: [],
    produced: {},
    artifact_refs: [],
    automatic_execution: false,
    rollback_performed: false,
  };

  const written = writeRunState(state, bound);
  if (written.status === "error") {
    return written;
  }
  return { status: "ok", data: { started: true, ...pipelineRunStatusData(state, bound, DEFAULT_HISTORY_RESULTS) } };
}

function pipelineRunStatus(args: Record<string, unknown>, record: HeartbeatRecord): PipelineCommandResult {
  const loaded = loadRunState(args, record);
  if (loaded.status === "error") {
    return loaded;
  }
  const maxHistory = clampInteger(typeof args.max_history === "number" ? args.max_history : DEFAULT_HISTORY_RESULTS, 1, MAX_HISTORY_RESULTS);
  return { status: "ok", data: pipelineRunStatusData(loaded.state, loaded.bound, maxHistory) };
}

function recordPipelineStep(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): PipelineCommandResult {
  const loaded = loadRunState(args, record);
  if (loaded.status === "error") {
    return loaded;
  }
  const state = loaded.state;
  if (state.status === "cancelled" || state.status === "completed") {
    return error("invalid_field_value", "Terminal pipeline runs cannot record additional steps", { run_id: state.run_id, status: state.status });
  }
  const result = parseStepResult(args.result);
  if (!result) {
    return error("invalid_field_value", "pipeline.record_step result must be success, failure, blocked or skipped", { field: "result" });
  }
  const step = selectRunStep(state, args);
  if (step.status === "error") {
    return step;
  }

  const produced = boundedRecordValue(args.produced, "produced", MAX_RECORD_OBJECT_CHARS);
  if (produced.status === "error") {
    return produced;
  }
  const artifactRefs = boundedStringArray(args.artifact_refs, "artifact_refs", MAX_RECORD_ARTIFACT_REFS, 240);
  if (artifactRefs.status === "error") {
    return artifactRefs;
  }
  const evidence = boundedStringArray(args.evidence, "evidence", MAX_RECORD_EVIDENCE_ITEMS, MAX_RECORD_EVIDENCE_CHARS);
  if (evidence.status === "error") {
    return evidence;
  }
  const notes = optionalBoundedString(args.notes, "notes", MAX_RECORD_NOTES_CHARS);
  if (notes.status === "error") {
    return notes;
  }
  const failure = optionalBoundedJsonValue(args.failure, "failure", MAX_RECORD_FAILURE_CHARS);
  if (failure.status === "error") {
    return failure;
  }
  const manualNextStep = optionalNextStep(state, args.next_step_id);
  if (manualNextStep.status === "error") {
    return manualNextStep;
  }

  const recordedAt = now.toISOString();
  const recordEntry: PipelineRunStepRecord = {
    recorded_at: recordedAt,
    step_id: step.step.id,
    step_index: step.step.index,
    result,
    produced: produced.value,
    artifact_refs: artifactRefs.value,
    evidence: evidence.value,
    gateway_executed: false,
    gateway_verified: false,
    automatic_execution: false,
    ...(notes.value ? { notes: notes.value } : {}),
    ...(failure.hasValue ? { failure: failure.value } : {}),
    ...(manualNextStep.step ? { next_step_id: manualNextStep.step.id } : {}),
  };

  step.step.status = result;
  step.step.records.push(recordEntry);
  state.history.push(recordEntry);
  state.produced = { ...state.produced, ...produced.value };
  state.artifact_refs = uniqueStrings([...state.artifact_refs, ...artifactRefs.value]).slice(0, MAX_RUN_ARTIFACT_REFS);

  const next = manualNextStep.step ? { kind: "step" as const, step: manualNextStep.step } : inferNextStep(state, step.step, result);
  applyNextStep(state, next, result);
  state.updated_at = recordedAt;

  const written = writeRunState(state, loaded.bound);
  if (written.status === "error") {
    return written;
  }
  return { status: "ok", data: { recorded: true, recorded_step: publicStep(step.step, true), manual_record_only: true, ...pipelineRunStatusData(state, loaded.bound, DEFAULT_HISTORY_RESULTS) } };
}

function cancelPipelineRun(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): PipelineCommandResult {
  const loaded = loadRunState(args, record);
  if (loaded.status === "error") {
    return loaded;
  }
  const state = loaded.state;
  if (state.status === "completed") {
    return error("invalid_field_value", "Completed pipeline runs cannot be cancelled", { run_id: state.run_id, status: state.status });
  }
  const reason = optionalBoundedString(args.reason, "reason", MAX_RECORD_NOTES_CHARS);
  if (reason.status === "error") {
    return reason;
  }
  const evidence = boundedStringArray(args.evidence, "evidence", MAX_RECORD_EVIDENCE_ITEMS, MAX_RECORD_EVIDENCE_CHARS);
  if (evidence.status === "error") {
    return evidence;
  }
  const alreadyCancelled = state.status === "cancelled";
  if (!alreadyCancelled) {
    state.status = "cancelled";
    state.updated_at = now.toISOString();
    state.cancellation = {
      cancelled_at: state.updated_at,
      rollback_performed: false,
      gateway_executed_rollback: false,
      evidence: evidence.value,
      ...(reason.value ? { reason: reason.value } : {}),
    };
    const written = writeRunState(state, loaded.bound);
    if (written.status === "error") {
      return written;
    }
  }
  return { status: "ok", data: { cancelled: !alreadyCancelled, already_cancelled: alreadyCancelled, rollback_performed: false, ...pipelineRunStatusData(state, loaded.bound, DEFAULT_HISTORY_RESULTS) } };
}

function validatePipelineContent(content: string, name?: string): Record<string, unknown> {
  const analysis = analyzePipelineContent(content, name);
  return { valid: analysis.valid, format: analysis.format, step_count: analysis.step_count, issues: analysis.issues };
}

function analyzePipelineContent(content: string, name?: string): PipelineDefinitionAnalysis {
  const jsonText = name?.endsWith(".json") || content.trimStart().startsWith("{") ? content : jsonFromMarkdown(content);
  if (jsonText) {
    return analyzeJsonPipeline(jsonText);
  }

  const issues: ValidationIssue[] = [];
  const commands = [...content.matchAll(/\bcommand\s*:\s*`?([a-zA-Z0-9_.-]+)`?/g)].flatMap((match) => match[1] ? [match[1]] : []);
  if (commands.length > MAX_STEPS) {
    issues.push({ path: "commands", message: `pipeline must contain at most ${MAX_STEPS} steps` });
  }
  for (const [index, command] of commands.entries()) {
    if (!COMMAND_ID_PATTERN.test(command)) {
      issues.push({ path: `commands[${index}]`, message: "command id must use lowercase dot notation" });
    }
  }
  if (hasBannedText(content)) {
    issues.push({ path: "content", message: "pipeline must not contain approval bypass or auto-run actions" });
  }
  const steps = commands.map((command, index) => {
    const step: PipelineDefinitionStep = {
      id: `step-${index + 1}`,
      index,
      action: "mcp.command",
      command,
      manual_execution_required: true,
      gateway_executed: false,
      gateway_verified: false,
    };
    return step;
  });
  return { valid: issues.length === 0, format: "markdown", step_count: commands.length, issues, definition: { steps } };
}

function analyzeJsonPipeline(jsonText: string): PipelineDefinitionAnalysis {
  const issues: ValidationIssue[] = [];
  let parsed: unknown;
  try {
    parsed = JSON.parse(jsonText);
  } catch {
    return { valid: false, format: "json", step_count: 0, issues: [{ path: "$", message: "pipeline JSON must parse" }] };
  }

  checkBannedJson(parsed, "$", issues);
  if (!isRecord(parsed)) {
    issues.push({ path: "$", message: "pipeline must be a JSON object" });
    return { valid: false, format: "json", step_count: 0, issues };
  }

  const steps = parsed.steps;
  if (!Array.isArray(steps)) {
    issues.push({ path: "$.steps", message: "pipeline must declare a steps array" });
    return { valid: false, format: "json", step_count: 0, issues };
  }
  if (steps.length > MAX_STEPS) {
    issues.push({ path: "$.steps", message: `pipeline must contain at most ${MAX_STEPS} steps` });
  }
  for (const [index, step] of steps.entries()) {
    validateJsonStep(step, index, issues);
  }
  return { valid: issues.length === 0, format: "json", step_count: steps.length, issues, definition: pipelineDefinitionFromJson(parsed, steps) };
}

function validateJsonStep(step: unknown, index: number, issues: ValidationIssue[]): void {
  const pathPrefix = `$.steps[${index}]`;
  if (!isRecord(step)) {
    issues.push({ path: pathPrefix, message: "step must be an object" });
    return;
  }
  const action = typeof step.action === "string" ? step.action.trim() : undefined;
  const command = typeof step.command === "string" ? step.command.trim() : undefined;
  if (action && isBannedAction(action)) {
    issues.push({ path: `${pathPrefix}.action`, message: "auto-run and run-next actions are not allowed" });
  }
  if (action && !ACTION_ID_PATTERN.test(action)) {
    issues.push({ path: `${pathPrefix}.action`, message: "action id must use lowercase dot notation" });
  }
  const mcpCommandStep = !action || action === "mcp.command";
  if (mcpCommandStep) {
    if (!command || !COMMAND_ID_PATTERN.test(command)) {
      issues.push({ path: `${pathPrefix}.command`, message: "command id must use lowercase dot notation" });
    }
    const hasExplicitProject = typeof step.project === "string" && step.project.trim().length > 0 && !["active", "current", "default"].includes(step.project.trim().toLowerCase());
    const requiresProjectAtRuntime = step.require_project === true || step.requires_project === true;
    if (!hasExplicitProject && !requiresProjectAtRuntime) {
      issues.push({ path: `${pathPrefix}.project`, message: "Unity command steps must use an explicit project or require_project=true" });
    }
  } else if ("command" in step && typeof step.command !== "string") {
    issues.push({ path: `${pathPrefix}.command`, message: "command must be a string when provided" });
  }
}

function pipelineDefinitionFromJson(parsed: Record<string, unknown>, steps: unknown[]): PipelineDefinition {
  const definition: PipelineDefinition = { steps: steps.map((step, index) => pipelineStepFromJson(step, index)) };
  if (typeof parsed.id === "string" && parsed.id.length <= 160) {
    definition.pipeline_id = parsed.id;
  }
  if (typeof parsed.title === "string" && parsed.title.length <= 200) {
    definition.title = parsed.title;
  }
  if (typeof parsed.mode === "string" && parsed.mode.length <= 80) {
    definition.mode = parsed.mode;
  }
  return definition;
}

function pipelineStepFromJson(step: unknown, index: number): PipelineDefinitionStep {
  if (!isRecord(step)) {
    return defaultPipelineDefinitionStep(index);
  }
  const id = typeof step.id === "string" && SAFE_ID_PATTERN.test(step.id) ? step.id : `step-${index + 1}`;
  const command = typeof step.command === "string" ? step.command.trim() : undefined;
  const action = typeof step.action === "string" && ACTION_ID_PATTERN.test(step.action.trim()) ? step.action.trim() : command && COMMAND_ID_PATTERN.test(command) ? "mcp.command" : "manual.step";
  const definitionStep: PipelineDefinitionStep = {
    id,
    index,
    action,
    manual_execution_required: true,
    gateway_executed: false,
    gateway_verified: false,
  };
  if (command) {
    definitionStep.command = command;
  }
  if (typeof step.project === "string" && step.project.trim().length > 0) {
    definitionStep.project = step.project.trim();
  }
  if (typeof step.risk === "string" && step.risk.trim().length > 0 && step.risk.length <= 80) {
    definitionStep.risk = step.risk.trim();
  }
  if ("args" in step) {
    definitionStep.args = step.args;
  }
  if (typeof step.notes === "string" && step.notes.length <= MAX_RECORD_NOTES_CHARS) {
    definitionStep.notes = step.notes;
  }
  const produces = boundedStringList(step.produces, MAX_RECORD_EVIDENCE_ITEMS, 120);
  if (produces.length > 0) {
    definitionStep.produces = produces;
  }
  if ("transitions" in step) {
    definitionStep.transitions = step.transitions;
  }
  if (typeof step.next === "string" && step.next.length <= 80) {
    definitionStep.next = step.next;
  }
  if (step.requires_user_approval === true || step.require_user_approval === true) {
    definitionStep.requires_user_approval = true;
  }
  return definitionStep;
}

function defaultPipelineDefinitionStep(index: number): PipelineDefinitionStep {
  return {
    id: `step-${index + 1}`,
    index,
    action: "manual.step",
    manual_execution_required: true,
    gateway_executed: false,
    gateway_verified: false,
  };
}

function loadPipelineDefinition(args: Record<string, unknown>, record: HeartbeatRecord): LoadedPipelineDefinition | PipelineCommandError {
  if (typeof args.content === "string") {
    return { status: "ok", content: args.content, source: { kind: "inline" } };
  }
  if (typeof args.name === "string") {
    const bound = bindPipelinePath(record, args.versioned === true, args.name);
    if (bound.status === "error") {
      return bound;
    }
    if (!fs.existsSync(bound.fullPath)) {
      return error("asset_not_found", "Pipeline file was not found under the approved pipeline root");
    }
    return { status: "ok", content: fs.readFileSync(bound.fullPath, "utf8"), source: { kind: "file", name: bound.name, versioned: bound.root.versioned, relative_path: `${bound.root.relativeRoot}/${bound.name}` } };
  }
  return error("invalid_field_value", "pipeline.start requires either content or name", { field: "content" });
}

function loadRunState(args: Record<string, unknown>, record: HeartbeatRecord): LoadedRunState | PipelineCommandError {
  const runId = typeof args.run_id === "string" ? args.run_id : "";
  const bound = bindRunPath(record, runId);
  if (bound.status === "error") {
    return bound;
  }
  if (!fs.existsSync(bound.fullPath)) {
    return error("asset_not_found", "Pipeline run state was not found under the local run root", { run_id: bound.runId });
  }
  const stat = fs.statSync(bound.fullPath);
  if (stat.size > MAX_RUN_STATE_CHARS) {
    return error("invalid_field_value", "Pipeline run state exceeds the bounded state limit", { run_id: bound.runId, max_chars: MAX_RUN_STATE_CHARS });
  }
  let parsed: unknown;
  try {
    parsed = JSON.parse(fs.readFileSync(bound.fullPath, "utf8"));
  } catch {
    return error("invalid_json", "Pipeline run state JSON could not be parsed", { run_id: bound.runId });
  }
  if (!isRecord(parsed) || parsed.schema_version !== 1 || parsed.run_id !== bound.runId || !Array.isArray(parsed.steps) || !Array.isArray(parsed.history) || !parsed.steps.every(isStoredRunStep)) {
    return error("invalid_field_value", "Pipeline run state has an unsupported shape", { run_id: bound.runId });
  }
  return { status: "ok", state: parsed as PipelineRunState, bound };
}

function isStoredRunStep(value: unknown): boolean {
  return isRecord(value) && typeof value.id === "string" && typeof value.index === "number" && typeof value.action === "string" && typeof value.status === "string" && Array.isArray(value.records);
}

function writeRunState(state: PipelineRunState, bound: BoundRunPath): { status: "ok" } | PipelineCommandError {
  const text = JSON.stringify(state, null, 2);
  if (text.length > MAX_RUN_STATE_CHARS) {
    return error("invalid_field_value", "Pipeline run state exceeds the bounded state limit", { run_id: state.run_id, max_chars: MAX_RUN_STATE_CHARS });
  }
  if (fs.existsSync(bound.runDir) && fs.lstatSync(bound.runDir).isSymbolicLink()) {
    return error("path_traversal_rejected", "Pipeline run directory must not be a symbolic link", { run_id: state.run_id });
  }
  fs.mkdirSync(bound.runDir, { recursive: true });
  const tempPath = `${bound.fullPath}.tmp`;
  fs.writeFileSync(tempPath, `${text}\n`, "utf8");
  fs.renameSync(tempPath, bound.fullPath);
  return { status: "ok" };
}

function pipelineRunStatusData(state: PipelineRunState, bound: BoundRunPath, maxHistory: number): Record<string, unknown> {
  const currentStep = currentRunStep(state);
  const history = state.history.slice(-maxHistory).map(publicRecord);
  const failures = state.history.filter((entry) => entry.result === "failure" || entry.result === "blocked").slice(-maxHistory).map(publicRecord);
  return {
    run_id: state.run_id,
    project_id: state.project_id,
    status: state.status,
    storage: { root: PIPELINE_RUNS_ROOT, relative_path: bound.relativePath, local_only: true },
    definition: state.definition,
    inputs: state.inputs,
    ...(state.notes ? { notes: state.notes } : {}),
    current_step: currentStep ? publicStep(currentStep, true) : null,
    steps: state.steps.map((step) => publicStep(step, false)),
    step_count: state.steps.length,
    completed_step_count: state.steps.filter((step) => step.status === "success" || step.status === "skipped").length,
    blocked_step_count: state.steps.filter((step) => step.status === "blocked").length,
    failed_step_count: state.steps.filter((step) => step.status === "failure").length,
    produced: state.produced,
    artifact_refs: state.artifact_refs,
    history,
    failures,
    automatic_execution: false,
    manual_run_state_only: true,
    rollback_performed: state.rollback_performed,
    policy_enforcement: {
      pipeline_does_not_bypass_policy: true,
      step_policy_not_preapproved_by_pipeline: true,
      mcp_steps_must_be_invoked_manually: true,
      gateway_executed_recorded_steps: false,
      gateway_verified_recorded_steps: false,
    },
    next_valid_actions: nextValidPipelineActions(state),
    ...(state.cancellation ? { cancellation: state.cancellation } : {}),
  };
}

function publicStep(step: PipelineRunStep, includeDetails: boolean): Record<string, unknown> {
  const data: Record<string, unknown> = {
    id: step.id,
    index: step.index,
    status: step.status,
    action: step.action,
    manual_execution_required: true,
    gateway_executed: false,
    gateway_verified: false,
    normal_policy_required: step.action === "mcp.command",
    record_count: step.records.length,
  };
  if (step.command) {
    data.command = step.command;
  }
  if (step.project) {
    data.project = step.project;
  }
  if (step.risk) {
    data.risk = step.risk;
  }
  if (step.requires_user_approval === true) {
    data.requires_user_approval = true;
  }
  if (step.produces) {
    data.produces = step.produces;
  }
  if (includeDetails) {
    if ("args" in step) {
      data.args = step.args;
    }
    if (step.notes) {
      data.notes = step.notes;
    }
    if ("transitions" in step) {
      data.transitions = step.transitions;
      data.transition_evaluation = "manual_record_only";
    }
    if (step.next) {
      data.next = step.next;
    }
  }
  return data;
}

function publicRecord(record: PipelineRunStepRecord): Record<string, unknown> {
  return {
    recorded_at: record.recorded_at,
    step_id: record.step_id,
    step_index: record.step_index,
    result: record.result,
    produced: record.produced,
    artifact_refs: record.artifact_refs,
    evidence: record.evidence,
    gateway_executed: false,
    gateway_verified: false,
    automatic_execution: false,
    ...(record.notes ? { notes: record.notes } : {}),
    ...(record.failure !== undefined ? { failure: record.failure } : {}),
    ...(record.next_step_id ? { next_step_id: record.next_step_id } : {}),
  };
}

function nextValidPipelineActions(state: PipelineRunState): string[] {
  if (state.status === "running") {
    return ["execute_current_step_manually_with_normal_policy", "pipeline.record_step", "pipeline.cancel"];
  }
  if (state.status === "blocked") {
    return ["inspect_recorded_failure_evidence", "manual_remediate_then_pipeline.record_step", "pipeline.cancel"];
  }
  return ["review_pipeline_run_evidence"];
}

function currentRunStep(state: PipelineRunState): PipelineRunStep | undefined {
  if (!state.current_step_id) {
    return undefined;
  }
  return state.steps.find((step) => step.id === state.current_step_id);
}

function selectRunStep(state: PipelineRunState, args: Record<string, unknown>): { status: "ok"; step: PipelineRunStep } | PipelineCommandError {
  const byId = typeof args.step_id === "string" ? args.step_id.trim() : undefined;
  const byIndex = typeof args.step_index === "number" && Number.isInteger(args.step_index) ? args.step_index : undefined;
  if (byId && !SAFE_ID_PATTERN.test(byId)) {
    return error("invalid_field_value", "step_id must be a safe pipeline step id", { field: "step_id" });
  }
  let selected = byId ? state.steps.find((step) => step.id === byId) : undefined;
  if (byIndex !== undefined) {
    const indexed = state.steps.find((step) => step.index === byIndex);
    if (!indexed) {
      return error("invalid_field_value", "step_index does not match a pipeline step", { field: "step_index" });
    }
    if (selected && selected.id !== indexed.id) {
      return error("invalid_field_value", "step_id and step_index refer to different pipeline steps", { field: "step_id" });
    }
    selected = indexed;
  }
  if (!selected && state.current_step_id) {
    selected = state.steps.find((step) => step.id === state.current_step_id);
  }
  if (!selected) {
    return error("invalid_field_value", "No current pipeline step is available to record", { field: "step_id" });
  }
  if (selected.status !== "pending" && selected.id !== state.current_step_id) {
    return error("invalid_field_value", "Only pending or current pipeline steps can be recorded", { field: "step_id", step_id: selected.id, step_status: selected.status });
  }
  return { status: "ok", step: selected };
}

function parseStepResult(value: unknown): PipelineStepResult | undefined {
  return value === "success" || value === "failure" || value === "blocked" || value === "skipped" ? value : undefined;
}

function inferNextStep(state: PipelineRunState, step: PipelineRunStep, result: PipelineStepResult): ResolvedNextStep {
  const transitionTarget = transitionTargetForResult(step, result);
  if (transitionTarget) {
    return resolveNextStepTarget(state, transitionTarget);
  }
  if ((result === "success" || result === "skipped") && step.next) {
    return resolveNextStepTarget(state, step.next);
  }
  if (result === "success" || result === "skipped") {
    const next = state.steps.find((candidate) => candidate.index > step.index && candidate.status === "pending");
    return next ? { kind: "step", step: next } : { kind: "completed" };
  }
  return undefined;
}

function transitionTargetForResult(step: PipelineRunStep, result: PipelineStepResult): string | undefined {
  if (!Array.isArray(step.transitions)) {
    return undefined;
  }
  for (const transition of step.transitions) {
    if (!isRecord(transition)) {
      continue;
    }
    if (transition.on === result && typeof transition.to === "string" && transition.to.length > 0) {
      return transition.to;
    }
  }
  return undefined;
}

function resolveNextStepTarget(state: PipelineRunState, target: string): ResolvedNextStep {
  const step = state.steps.find((candidate) => candidate.id === target);
  if (step) {
    return { kind: "step", step };
  }
  const lowered = target.toLowerCase();
  if (["done", "complete", "completed", "finish", "finished"].includes(lowered)) {
    return { kind: "completed" };
  }
  if (["blocked", "failed", "failure"].includes(lowered)) {
    return { kind: "blocked" };
  }
  return undefined;
}

function applyNextStep(state: PipelineRunState, next: ResolvedNextStep, result: PipelineStepResult): void {
  if (next?.kind === "step") {
    state.status = "running";
    state.current_step_id = next.step.id;
    state.current_step_index = next.step.index;
    return;
  }
  if (next?.kind === "completed") {
    state.status = "completed";
    delete state.current_step_id;
    delete state.current_step_index;
    return;
  }
  if (next?.kind === "blocked") {
    state.status = "blocked";
    return;
  }
  if (result === "success" || result === "skipped") {
    state.status = "completed";
    delete state.current_step_id;
    delete state.current_step_index;
    return;
  }
  state.status = "blocked";
}

function optionalNextStep(state: PipelineRunState, value: unknown): { status: "ok"; step: PipelineRunStep | undefined } | PipelineCommandError {
  if (value === undefined) {
    return { status: "ok", step: undefined };
  }
  if (typeof value !== "string" || !SAFE_ID_PATTERN.test(value)) {
    return error("invalid_field_value", "next_step_id must be a safe existing pipeline step id", { field: "next_step_id" });
  }
  const step = state.steps.find((candidate) => candidate.id === value);
  if (!step) {
    return error("invalid_field_value", "next_step_id does not match a pipeline step", { field: "next_step_id", next_step_id: value });
  }
  return { status: "ok", step };
}

function boundedRecordValue(value: unknown, field: string, maxChars: number): { status: "ok"; value: Record<string, unknown> } | PipelineCommandError {
  if (value === undefined) {
    return { status: "ok", value: {} };
  }
  if (!isRecord(value)) {
    return error("invalid_field_value", `${field} must be a JSON object`, { field });
  }
  const size = jsonCharLength(value);
  if (size > maxChars) {
    return error("invalid_field_value", `${field} exceeds the bounded JSON limit`, { field, max_chars: maxChars });
  }
  return { status: "ok", value };
}

function optionalBoundedJsonValue(value: unknown, field: string, maxChars: number): { status: "ok"; hasValue: false } | { status: "ok"; hasValue: true; value: unknown } | PipelineCommandError {
  if (value === undefined) {
    return { status: "ok", hasValue: false };
  }
  const size = jsonCharLength(value);
  if (size > maxChars) {
    return error("invalid_field_value", `${field} exceeds the bounded JSON limit`, { field, max_chars: maxChars });
  }
  return { status: "ok", hasValue: true, value };
}

function optionalBoundedString(value: unknown, field: string, maxChars: number): { status: "ok"; value: string | undefined } | PipelineCommandError {
  if (value === undefined) {
    return { status: "ok", value: undefined };
  }
  if (typeof value !== "string") {
    return error("invalid_field_value", `${field} must be a string`, { field });
  }
  if (value.length > maxChars) {
    return error("invalid_field_value", `${field} exceeds the bounded text limit`, { field, max_chars: maxChars });
  }
  return { status: "ok", value };
}

function boundedStringArray(value: unknown, field: string, maxItems: number, maxChars: number): { status: "ok"; value: string[] } | PipelineCommandError {
  if (value === undefined) {
    return { status: "ok", value: [] };
  }
  if (!Array.isArray(value) || value.length > maxItems) {
    return error("invalid_field_value", `${field} must be a bounded string array`, { field, max_items: maxItems });
  }
  const items: string[] = [];
  for (const [index, item] of value.entries()) {
    if (typeof item !== "string" || item.length < 1 || item.length > maxChars) {
      return error("invalid_field_value", `${field} entries must be bounded non-empty strings`, { field: `${field}[${index}]`, max_chars: maxChars });
    }
    items.push(item);
  }
  return { status: "ok", value: uniqueStrings(items) };
}

function boundedStringList(value: unknown, maxItems: number, maxChars: number): string[] {
  if (!Array.isArray(value)) {
    return [];
  }
  const strings: string[] = [];
  for (const item of value.slice(0, maxItems)) {
    if (typeof item === "string" && item.length > 0 && item.length <= maxChars) {
      strings.push(item);
    }
  }
  return uniqueStrings(strings);
}

function jsonCharLength(value: unknown): number {
  return JSON.stringify(value)?.length ?? 0;
}

function firstDuplicateStepId(steps: PipelineDefinitionStep[]): string | undefined {
  const seen = new Set<string>();
  for (const step of steps) {
    if (seen.has(step.id)) {
      return step.id;
    }
    seen.add(step.id);
  }
  return undefined;
}

function uniqueStrings(values: string[]): string[] {
  return [...new Set(values)];
}

function bindRunPath(record: HeartbeatRecord, runId: string): BoundRunPath | PipelineCommandError {
  const normalized = runId.trim();
  if (!SAFE_ID_PATTERN.test(normalized)) {
    return error("invalid_field_value", "run_id must be a safe local pipeline run id", { field: "run_id" });
  }
  const root = pipelineRunsRoot(record);
  const runDir = path.join(root.fullPath, normalized);
  const fullPath = path.join(runDir, "run.json");
  if (!isUnderRoot(runDir, root.fullPath) || !isUnderRoot(fullPath, root.fullPath)) {
    return error("path_traversal_rejected", "Pipeline run state path must stay under the local run root");
  }
  return { status: "ok", runId: normalized, runDir, fullPath, relativePath: `${root.relativeRoot}/${normalized}/run.json` };
}

function pipelineRunsRoot(record: HeartbeatRecord): { fullPath: string; relativeRoot: string } {
  return { fullPath: path.join(record.project_root, PIPELINE_RUNS_ROOT), relativeRoot: PIPELINE_RUNS_ROOT };
}

function sha256Hex(value: string): string {
  return createHash("sha256").update(value, "utf8").digest("hex");
}

function checkBannedJson(value: unknown, currentPath: string, issues: ValidationIssue[]): void {
  if (Array.isArray(value)) {
    value.forEach((item, index) => checkBannedJson(item, `${currentPath}[${index}]`, issues));
    return;
  }
  if (!isRecord(value)) {
    return;
  }
  for (const [key, nested] of Object.entries(value)) {
    if (BANNED_KEY_NAMES.has(key.toLowerCase())) {
      issues.push({ path: `${currentPath}.${key}`, message: "approval bypass and auto-run fields are not allowed" });
    }
    if (typeof nested === "string" && isBannedAction(nested)) {
      issues.push({ path: `${currentPath}.${key}`, message: "auto-run and run-next actions are not allowed" });
    }
    checkBannedJson(nested, `${currentPath}.${key}`, issues);
  }
}

function scanPipelineFiles(root: string, relativeRoot: string, relativeDir: string, found: Array<{ name: string; relative_path: string; bytes: number; modified_at: string }>, canScan: () => boolean): void {
  if (!canScan()) {
    return;
  }
  const base = path.join(root, relativeDir);
  for (const entry of fs.readdirSync(base, { withFileTypes: true })) {
    if (!canScan()) {
      return;
    }
    const relativeName = relativeDir ? `${relativeDir}/${entry.name}` : entry.name;
    const fullPath = path.join(root, relativeName);
    const stat = fs.lstatSync(fullPath);
    if (stat.isSymbolicLink()) {
      continue;
    }
    if (entry.isDirectory()) {
      scanPipelineFiles(root, relativeRoot, relativeName, found, canScan);
    } else if (entry.isFile() && (entry.name.endsWith(".md") || entry.name.endsWith(".json"))) {
      found.push({ name: relativeName, relative_path: `${relativeRoot}/${relativeName}`, bytes: stat.size, modified_at: stat.mtime.toISOString() });
    }
  }
}

function bindPipelinePath(record: HeartbeatRecord, versioned: boolean, name: string): BoundPipelinePath | PipelineCommandError {
  const safeName = safeRelativePipelineName(name);
  if (safeName.status === "error") {
    return safeName;
  }
  const root = pipelinesRoot(record, versioned);
  const fullPath = path.join(root.fullPath, safeName.name);
  if (!isUnderRoot(fullPath, root.fullPath)) {
    return error("path_traversal_rejected", "Pipeline path must stay under the approved pipeline root");
  }
  return { status: "ok", root, name: safeName.name, fullPath };
}

function safeRelativePipelineName(name: string): { status: "ok"; name: string } | PipelineCommandError {
  const normalized = name.replaceAll("\\", "/").trim();
  if (path.isAbsolute(normalized) || /^[A-Za-z]:\//.test(normalized)) {
    return error("absolute_path_rejected", "Pipeline name must be relative to the selected pipeline root");
  }
  if ((!normalized.endsWith(".md") && !normalized.endsWith(".json")) || normalized.length > 160 || normalized.split("/").some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    return error("path_traversal_rejected", "Pipeline name must be a safe relative .md or .json path without traversal");
  }
  if (!/^[A-Za-z0-9._/-]+$/.test(normalized)) {
    return error("invalid_field_value", "Pipeline name contains unsupported characters", { field: "name" });
  }
  return { status: "ok", name: normalized };
}

function pipelinesRoot(record: HeartbeatRecord, versioned: boolean): PipelineRoot {
  const relativeRoot = versioned ? ".mcp/pipelines" : "Library/SGGUnityMcp/pipelines";
  return { fullPath: path.join(record.project_root, relativeRoot), relativeRoot, versioned };
}

function jsonFromMarkdown(content: string): string | undefined {
  const match = content.match(/```json\s*([\s\S]*?)```/i);
  return match?.[1]?.trim();
}

function hasBannedText(content: string): boolean {
  const lowered = content.toLowerCase();
  return [...BANNED_KEY_NAMES, ...BANNED_ACTIONS].some((token) => lowered.includes(token.toLowerCase()));
}

function isBannedAction(value: string): boolean {
  return BANNED_ACTIONS.has(value.toLowerCase());
}

function defaultPipelineName(now: Date): string {
  return `pipeline-${now.toISOString().replaceAll(/[:.]/g, "-")}.md`;
}

function defaultRunId(now: Date): string {
  return `pipeline-run-${now.toISOString().replaceAll(/[:.]/g, "-")}`;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isUnderRoot(candidate: string, root: string): boolean {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

function clampInteger(value: number, min: number, max: number): number {
  if (!Number.isInteger(value)) {
    return min;
  }
  return Math.min(max, Math.max(min, value));
}

function error(code: ErrorCode, message: string, details?: Record<string, unknown>): PipelineCommandError {
  return { status: "error", error: { code, message, ...(details ? { details } : {}) } };
}

function emptySchema(): Record<string, unknown> {
  return { type: "object", properties: {}, additionalProperties: false };
}
