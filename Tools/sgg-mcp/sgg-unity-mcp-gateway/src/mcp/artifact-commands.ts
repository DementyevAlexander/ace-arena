import fs from "node:fs";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";
import type { ErrorCode, GatewayError } from "./results.js";

const FEATURE_FLAG = "sgg_mcp_artifacts";
const MAX_RESULTS = 100;
const DEFAULT_RESULTS = 50;
const MAX_SCAN = 1000;
const MAX_PRUNE = 100;
const DEFAULT_PRUNE_DAYS = 30;
const DEFAULT_PRUNE_MAX = 50;
const MAX_REPRODUCIBILITY_BYTES = 262_144;
const PROTECTED_READONLY_CSHARP_ROOT = "Library/SGGUnityMcp/job-artifacts/dynamic-csharp-readonly";
const ARTIFACT_FAMILIES = ["job", "asset_report", "build_run", "test_run", "editor_menu_operation", "runtime_event_capture", "profiler_report", "profiler_capture", "screenshot", "build_output"] as const;
const PRUNABLE_ARTIFACT_FAMILIES = ["job", "asset_report", "build_run", "test_run", "editor_menu_operation", "runtime_event_capture", "profiler_report", "profiler_capture", "screenshot"] as const;
const ARTIFACT_PRUNE_PRESETS = ["default", "runtime", "screenshots", "builds", "tests", "profiler"] as const;

const ARTIFACT_ROOTS = [
  { type: "job", root: "Library/SGGUnityMcp/job-artifacts", recursive: true, prunable: true },
  { type: "screenshot", root: "Library/SGGUnityMcp/screenshots", recursive: true, prunable: true },
  { type: "screenshot", root: "CaptureArtifacts", recursive: true, prunable: true },
  { type: "build_output", root: "BuildArtifacts/SGGUnityMcp", recursive: false, prunable: false },
] as const;

type ArtifactType = (typeof ARTIFACT_ROOTS)[number]["type"];
type ArtifactRoot = (typeof ARTIFACT_ROOTS)[number];
type ArtifactFamily = (typeof ARTIFACT_FAMILIES)[number];
type PrunableArtifactFamily = (typeof PRUNABLE_ARTIFACT_FAMILIES)[number];
type ArtifactPrunePreset = (typeof ARTIFACT_PRUNE_PRESETS)[number];
type ArtifactEntry = { type: ArtifactType; family: ArtifactFamily; artifact_path: string; bytes: number; modified_at: string; directory: boolean; extension?: string; prunable?: false };
type RetentionPreset = {
  type?: "job" | "screenshot";
  family?: PrunableArtifactFamily;
  families?: PrunableArtifactFamily[];
  older_than_days: number;
  max_delete: number;
  description: string;
  storage_note?: string;
  build_outputs_deleted?: false;
};
type PruneSettings = {
  preset: ArtifactPrunePreset;
  presetConfig: RetentionPreset;
  type?: "job" | "screenshot";
  family?: PrunableArtifactFamily;
  families?: PrunableArtifactFamily[];
  olderThanDays: number;
  maxDelete: number;
  dryRun: boolean;
};

const RETENTION_PRESETS: Record<ArtifactPrunePreset, RetentionPreset> = {
  default: { older_than_days: DEFAULT_PRUNE_DAYS, max_delete: DEFAULT_PRUNE_MAX, description: "All prunable job and screenshot artifacts under approved roots." },
  runtime: { type: "job", family: "runtime_event_capture", older_than_days: 14, max_delete: DEFAULT_PRUNE_MAX, description: "Runtime event capture terminal artifacts." },
  screenshots: { type: "screenshot", family: "screenshot", older_than_days: 30, max_delete: DEFAULT_PRUNE_MAX, description: "Scene/Game/Editor screenshot artifacts." },
  builds: { type: "job", family: "build_run", older_than_days: 30, max_delete: DEFAULT_PRUNE_MAX, description: "Build summary JSON artifacts only; build output directories are never deleted.", build_outputs_deleted: false },
  tests: { type: "job", family: "test_run", older_than_days: 30, max_delete: DEFAULT_PRUNE_MAX, description: "Unity test run summary JSON artifacts." },
  profiler: {
    type: "job",
    families: ["profiler_report", "profiler_capture"],
    older_than_days: 7,
    max_delete: DEFAULT_PRUNE_MAX,
    description: "File-backed profiler report, memory-compare, and capture export artifacts.",
    storage_note: "Covers profiler.report.create/profiler.memory.snapshot.compare JSON artifacts under Library/SGGUnityMcp/job-artifacts/profiler-reports/ and profiler.capture.save artifacts under Library/SGGUnityMcp/job-artifacts/profiler-captures/.",
  },
};

export const ARTIFACT_COMMANDS: CommandDescriptor[] = [
  {
    id: "artifact.list",
    title: "List artifacts",
    description: "List bounded project-local artifact metadata under approved SGGUnityMcp artifact roots.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10000,
    schema: {
      type: "object",
      properties: {
        type: { type: "string", enum: ["job", "screenshot", "build_output"] },
        family: { type: "string", enum: [...ARTIFACT_FAMILIES] },
        max_results: { type: "integer", minimum: 1, maximum: MAX_RESULTS },
      },
      additionalProperties: false,
    },
    tags: ["artifact"],
    capability: "artifacts",
    result_shape: "bounded_list",
    output_limit: { max_results: MAX_RESULTS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "artifact.metadata",
    title: "Artifact metadata",
    description: "Read bounded metadata for one artifact path returned by artifact.list without reading file content.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        artifact_path: { type: "string", minLength: 1, maxLength: 260 },
      },
      required: ["artifact_path"],
      additionalProperties: false,
    },
    tags: ["artifact"],
    capability: "artifacts",
    result_shape: "object_details",
    output_limit: { max_chars: 4096 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "artifact.prune",
    title: "Prune artifacts",
    description: "Delete old project-local job and screenshot artifacts using dry-run-first retention presets under approved roots after policy approval.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 30000,
    schema: {
      type: "object",
      properties: {
        preset: { type: "string", enum: [...ARTIFACT_PRUNE_PRESETS] },
        type: { type: "string", enum: ["job", "screenshot"] },
        family: { type: "string", enum: [...PRUNABLE_ARTIFACT_FAMILIES] },
        older_than_days: { type: "integer", minimum: 1, maximum: 3650 },
        max_delete: { type: "integer", minimum: 1, maximum: MAX_PRUNE },
        dry_run: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["artifact"],
    capability: "artifacts",
    result_shape: "object_details",
    output_limit: { max_results: MAX_PRUNE, max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type ArtifactCommandResult = { status: "ok"; data: unknown } | { status: "error"; error: GatewayError };
type ArtifactError = Extract<ArtifactCommandResult, { status: "error" }>;

export function findArtifactCommand(commandId: string): CommandDescriptor | undefined {
  return ARTIFACT_COMMANDS.find((command) => command.id === commandId);
}

export function runArtifactCommand(commandId: string, args: Record<string, unknown>, record: HeartbeatRecord | undefined, now: Date = new Date()): ArtifactCommandResult {
  if (!record) {
    return error("project_offline", "Artifact commands require a resolved live project heartbeat");
  }

  switch (commandId) {
    case "artifact.list":
      return { status: "ok", data: listArtifacts(args, record) };
    case "artifact.metadata":
      return artifactMetadata(args, record);
    case "artifact.prune":
      return pruneArtifacts(args, record, now);
    default:
      return error("command_not_found", "Artifact command not found");
  }
}

function listArtifacts(args: Record<string, unknown>, record: HeartbeatRecord): Record<string, unknown> {
  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_RESULTS, 1, MAX_RESULTS);
  const type = typeof args.type === "string" && isArtifactType(args.type) ? args.type : undefined;
  const family = typeof args.family === "string" && isArtifactFamily(args.family) ? args.family : undefined;
  const roots = ARTIFACT_ROOTS.filter((root) => !type || root.type === type);
  const scan = scanArtifacts(record, roots, MAX_SCAN);
  const filtered = scan.entries.filter((entry) => !family || entry.family === family);
  const artifacts = filtered
    .sort((left, right) => right.modified_at.localeCompare(left.modified_at) || left.artifact_path.localeCompare(right.artifact_path))
    .slice(0, maxResults);

  return {
    roots: roots.map((root) => root.root),
    families: Array.from(new Set(scan.entries.map((entry) => entry.family))).sort(),
    artifacts,
    max_results: maxResults,
    scanned: scan.scanned,
    truncated: scan.truncated || filtered.length > maxResults,
  };
}

function artifactMetadata(args: Record<string, unknown>, record: HeartbeatRecord): ArtifactCommandResult {
  const artifactPath = typeof args.artifact_path === "string" ? args.artifact_path : "";
  const bound = bindArtifactPath(record, artifactPath);
  if (bound.status === "error") {
    return bound;
  }
  if (!fs.existsSync(bound.fullPath)) {
    return error("asset_not_found", "Artifact was not found under approved artifact roots");
  }
  const stat = fs.lstatSync(bound.fullPath);
  if (stat.isSymbolicLink() || hasSymbolicLinkInExistingChain(bound.fullPath, projectPath(record, bound.root.root))) {
    return error("path_not_allowed", "Artifact paths must not be symbolic links");
  }

  return {
    status: "ok",
    data: {
      type: bound.root.type,
      family: artifactFamily(bound.root, bound.relativePath),
      artifact_path: bound.relativePath,
      root: bound.root.root,
      bytes: stat.isDirectory() ? directoryEntrySize(bound.fullPath) : stat.size,
      modified_at: stat.mtime.toISOString(),
      directory: stat.isDirectory(),
      prunable: bound.root.prunable && stat.isFile() && !isProtectedReadonlyCSharpArtifact(bound.relativePath),
      ...(stat.isFile() ? { extension: path.extname(bound.relativePath).toLowerCase() } : {}),
      ...(bound.root.type === "job" && stat.isFile() ? { readable_with: "job.artifact.read" } : {}),
      ...(stat.isFile() ? { reproducibility: readReproducibility(bound.fullPath, stat) } : {}),
    },
  };
}

function pruneArtifacts(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): ArtifactCommandResult {
  const resolved = resolvePruneSettings(args);
  if (resolved.status === "error") {
    return resolved;
  }

  const { preset, presetConfig, type, family, families, olderThanDays, maxDelete, dryRun } = resolved.settings;
  const roots = ARTIFACT_ROOTS.filter((root) => root.prunable && (!type || root.type === type));
  const cutoff = now.getTime() - olderThanDays * 24 * 60 * 60 * 1000;
  const scan = scanArtifacts(record, roots, MAX_SCAN);
  const candidates = scan.entries
    .filter((entry) => !entry.directory && !isProtectedReadonlyCSharpArtifact(entry.artifact_path) && familyMatches(entry.family, family, families) && new Date(entry.modified_at).getTime() < cutoff)
    .sort((left, right) => left.modified_at.localeCompare(right.modified_at) || left.artifact_path.localeCompare(right.artifact_path));
  const selected = candidates.slice(0, maxDelete);
  const deletion = dryRun ? { deleted: [] as string[], skipped: [] as Array<{ artifact_path: string; reason: string }> } : deleteSelectedArtifacts(record, selected);

  return {
    status: "ok",
    data: {
      roots: roots.map((root) => root.root),
      preset,
      ...(type ? { type } : {}),
      ...(family ? { family } : {}),
      ...(!family && families ? { families } : {}),
      dry_run: dryRun,
      dry_run_default: true,
      retention_plan: retentionPlan(preset, presetConfig, type, family, families, olderThanDays, maxDelete, dryRun),
      retention_presets: retentionPresets(roots),
      older_than_days: olderThanDays,
      max_delete: maxDelete,
      candidate_count: candidates.length,
      selected_count: selected.length,
      deleted_count: deletion.deleted.length,
      deleted: deletion.deleted,
      would_delete: dryRun ? selected.map((entry) => entry.artifact_path) : [],
      skipped_count: deletion.skipped.length,
      skipped: deletion.skipped,
      truncated: scan.truncated || candidates.length > maxDelete,
      build_outputs_deleted: false,
    },
  };
}

function resolvePruneSettings(args: Record<string, unknown>): { status: "ok"; settings: PruneSettings } | ArtifactError {
  if (typeof args.preset === "string" && !isArtifactPrunePreset(args.preset)) {
    return error("invalid_field_value", "preset must be one of default, runtime, screenshots, builds, tests, profiler");
  }
  if (typeof args.type === "string" && !isPrunableArtifactType(args.type)) {
    return error("invalid_field_value", "type must be job or screenshot for artifact.prune");
  }
  if (typeof args.family === "string" && !isPrunableArtifactFamily(args.family)) {
    return error("invalid_field_value", "family must be a prunable artifact family");
  }

  const preset: ArtifactPrunePreset = typeof args.preset === "string" && isArtifactPrunePreset(args.preset) ? args.preset : "default";
  const presetConfig = RETENTION_PRESETS[preset];
  const requestedType = typeof args.type === "string" && isPrunableArtifactType(args.type) ? args.type : undefined;
  const requestedFamily = typeof args.family === "string" && isPrunableArtifactFamily(args.family) ? args.family : undefined;
  if (requestedType && presetConfig.type && requestedType !== presetConfig.type) {
    return error("invalid_field_value", `preset ${preset} requires type ${presetConfig.type}`);
  }
  if (requestedFamily && presetConfig.family && requestedFamily !== presetConfig.family) {
    return error("invalid_field_value", `preset ${preset} requires family ${presetConfig.family}`);
  }
  if (requestedFamily && presetConfig.families && !presetConfig.families.includes(requestedFamily)) {
    return error("invalid_field_value", `preset ${preset} requires one of families ${presetConfig.families.join(", ")}`);
  }

  const family = requestedFamily ?? presetConfig.family;
  const families = requestedFamily ? undefined : presetConfig.families;
  const type = requestedType ?? presetConfig.type ?? (family ? artifactTypeForFamily(family) : undefined);
  if (type && family && artifactTypeForFamily(family) !== type) {
    return error("invalid_field_value", `family ${family} does not belong to artifact type ${type}`);
  }
  if (type && families?.some((candidate) => artifactTypeForFamily(candidate) !== type)) {
    return error("invalid_field_value", `preset ${preset} family list does not belong to artifact type ${type}`);
  }

  return {
    status: "ok",
    settings: {
      preset,
      presetConfig,
      ...(type ? { type } : {}),
      ...(family ? { family } : {}),
      ...(families ? { families } : {}),
      olderThanDays: clampInteger(typeof args.older_than_days === "number" ? args.older_than_days : presetConfig.older_than_days, 1, 3650),
      maxDelete: clampInteger(typeof args.max_delete === "number" ? args.max_delete : presetConfig.max_delete, 1, MAX_PRUNE),
      dryRun: args.dry_run !== false,
    },
  };
}

function deleteSelectedArtifacts(record: HeartbeatRecord, selected: ArtifactEntry[]): { deleted: string[]; skipped: Array<{ artifact_path: string; reason: string }> } {
  const deleted: string[] = [];
  const skipped: Array<{ artifact_path: string; reason: string }> = [];
  for (const artifact of selected) {
    const bound = bindArtifactPath(record, artifact.artifact_path);
    if (bound.status === "error") {
      skipped.push({ artifact_path: artifact.artifact_path, reason: bound.error.code });
      continue;
    }
    if (isProtectedReadonlyCSharpArtifact(bound.relativePath)) {
      skipped.push({ artifact_path: artifact.artifact_path, reason: "readonly_csharp_receipt_protected" });
      continue;
    }
    try {
      if (!fs.existsSync(bound.fullPath)) {
        skipped.push({ artifact_path: artifact.artifact_path, reason: "not_found" });
        continue;
      }
      const stat = fs.lstatSync(bound.fullPath);
      if (stat.isSymbolicLink()) {
        skipped.push({ artifact_path: artifact.artifact_path, reason: "symlink_skipped" });
        continue;
      }
      if (!stat.isFile()) {
        skipped.push({ artifact_path: artifact.artifact_path, reason: "not_a_file" });
        continue;
      }
      fs.rmSync(bound.fullPath, { force: true });
      if (fs.existsSync(bound.fullPath)) {
        skipped.push({ artifact_path: artifact.artifact_path, reason: "delete_failed" });
        continue;
      }
      deleted.push(artifact.artifact_path);
    } catch (cause) {
      skipped.push({ artifact_path: artifact.artifact_path, reason: cause instanceof Error ? `delete_failed:${cause.name}` : "delete_failed" });
    }
  }
  return { deleted, skipped };
}

function scanArtifacts(record: HeartbeatRecord, roots: readonly ArtifactRoot[], maxScan: number): { entries: ArtifactEntry[]; scanned: number; truncated: boolean } {
  const entries: ArtifactEntry[] = [];
  let scanned = 0;
  let truncated = false;
  for (const root of roots) {
    const fullRoot = projectPath(record, root.root);
    if (!fs.existsSync(fullRoot)) {
      continue;
    }
    if (hasSymbolicLinkInExistingChain(fullRoot, record.project_root)) {
      truncated = true;
      continue;
    }
    const listed = scanRoot(record, root, root.root, fullRoot, maxScan - scanned);
    entries.push(...listed.entries);
    scanned += listed.scanned;
    truncated = truncated || listed.truncated;
    if (scanned >= maxScan) {
      truncated = true;
      break;
    }
  }
  return { entries, scanned, truncated };
}

function scanRoot(record: HeartbeatRecord, root: ArtifactRoot, relativeDir: string, fullDir: string, remaining: number): { entries: ArtifactEntry[]; scanned: number; truncated: boolean } {
  const entries: ArtifactEntry[] = [];
  let scanned = 0;
  let truncated = false;
  for (const entry of fs.readdirSync(fullDir, { withFileTypes: true })) {
    if (scanned >= remaining) {
      truncated = true;
      break;
    }
    const relativePath = `${relativeDir}/${entry.name}`;
    const fullPath = projectPath(record, relativePath);
    if (!isUnderRoot(fullPath, projectPath(record, root.root))) {
      truncated = true;
      continue;
    }
    const stat = fs.lstatSync(fullPath);
    if (stat.isSymbolicLink()) {
      continue;
    }
    scanned += 1;
    if (stat.isDirectory()) {
      entries.push(toArtifactEntry(root, relativePath, stat, true, fullPath));
      if (root.recursive) {
        const child = scanRoot(record, root, relativePath, fullPath, remaining - scanned);
        entries.push(...child.entries);
        scanned += child.scanned;
        truncated = truncated || child.truncated;
      }
    } else if (stat.isFile()) {
      entries.push(toArtifactEntry(root, relativePath, stat, false, fullPath));
    }
  }
  return { entries, scanned, truncated };
}

function toArtifactEntry(root: ArtifactRoot, relativePath: string, stat: fs.Stats, directory: boolean, fullPath: string): ArtifactEntry {
  return {
    type: root.type,
    family: artifactFamily(root, relativePath),
    artifact_path: relativePath,
    bytes: directory ? directoryEntrySize(fullPath) : stat.size,
    modified_at: stat.mtime.toISOString(),
    directory,
    ...(isProtectedReadonlyCSharpArtifact(relativePath) ? { prunable: false as const } : {}),
    ...(directory ? {} : { extension: path.extname(relativePath).toLowerCase() }),
  };
}

function isProtectedReadonlyCSharpArtifact(relativePath: string): boolean {
  // Segment-bound and case-insensitive for the Windows producer; near-prefix
  // siblings stay governed by their existing retention policy. No receipt
  // parsing or acknowledgement inference is needed to protect this namespace.
  const normalized = relativePath.replace(/\\/g, "/").toLowerCase();
  const root = PROTECTED_READONLY_CSHARP_ROOT.toLowerCase();
  return normalized === root || normalized.startsWith(`${root}/`);
}

function artifactFamily(root: ArtifactRoot, relativePath: string): ArtifactFamily {
  if (root.type === "build_output") {
    return "build_output";
  }
  if (root.type === "screenshot") {
    return "screenshot";
  }
  if (relativePath.startsWith("Library/SGGUnityMcp/job-artifacts/build-runs/")) {
    return "build_run";
  }
  if (relativePath.startsWith("Library/SGGUnityMcp/job-artifacts/test-runs/")) {
    return "test_run";
  }
  if (relativePath.startsWith("Library/SGGUnityMcp/job-artifacts/menu-operations/")) {
    return "editor_menu_operation";
  }
  if (relativePath.startsWith("Library/SGGUnityMcp/job-artifacts/asset-reports/")) {
    return "asset_report";
  }
  if (relativePath.startsWith("Library/SGGUnityMcp/job-artifacts/runtime-event-captures/")) {
    return "runtime_event_capture";
  }
  if (relativePath.startsWith("Library/SGGUnityMcp/job-artifacts/profiler-reports/")) {
    return "profiler_report";
  }
  if (relativePath.startsWith("Library/SGGUnityMcp/job-artifacts/profiler-captures/")) {
    return "profiler_capture";
  }
  return "job";
}

function retentionPresets(roots: readonly ArtifactRoot[]): Record<string, unknown> {
  return {
    default_older_than_days: DEFAULT_PRUNE_DAYS,
    max_delete: MAX_PRUNE,
    dry_run_first: true,
    prunable_roots: roots.filter((root) => root.prunable).map((root) => root.root),
    presets: Object.fromEntries(Object.entries(RETENTION_PRESETS).map(([name, preset]) => [
      name,
      {
        ...(preset.type ? { type: preset.type } : {}),
        ...(preset.family ? { family: preset.family } : {}),
        ...(preset.families ? { families: preset.families } : {}),
        older_than_days: preset.older_than_days,
        max_delete: preset.max_delete,
        description: preset.description,
        ...(preset.storage_note ? { storage_note: preset.storage_note } : {}),
        ...(preset.build_outputs_deleted === false ? { build_outputs_deleted: false } : {}),
      },
    ])),
    families: {
      job: { older_than_days: 30 },
      asset_report: { older_than_days: 30 },
      build_run: { older_than_days: 30 },
      test_run: { older_than_days: 30 },
      runtime_event_capture: { older_than_days: 14 },
      profiler_report: { older_than_days: 7, storage_note: RETENTION_PRESETS.profiler.storage_note },
      profiler_capture: { older_than_days: 7, storage_note: RETENTION_PRESETS.profiler.storage_note },
      screenshot: { older_than_days: 30 },
    },
  };
}

function retentionPlan(
  preset: ArtifactPrunePreset,
  presetConfig: RetentionPreset,
  type: "job" | "screenshot" | undefined,
  family: PrunableArtifactFamily | undefined,
  families: PrunableArtifactFamily[] | undefined,
  olderThanDays: number,
  maxDelete: number,
  dryRun: boolean,
): Record<string, unknown> {
  return {
    preset,
    description: presetConfig.description,
    ...(type ? { type } : {}),
    ...(family ? { family } : {}),
    ...(!family && families ? { families } : {}),
    older_than_days: olderThanDays,
    max_delete: maxDelete,
    dry_run: dryRun,
    dry_run_first: true,
    build_outputs_deleted: false,
    ...(presetConfig.storage_note ? { storage_note: presetConfig.storage_note } : {}),
  };
}

function familyMatches(entryFamily: ArtifactFamily, family: PrunableArtifactFamily | undefined, families: PrunableArtifactFamily[] | undefined): boolean {
  if (family) {
    return entryFamily === family;
  }
  if (families) {
    return families.includes(entryFamily as PrunableArtifactFamily);
  }
  return true;
}

function readReproducibility(fullPath: string, stat: fs.Stats): Record<string, unknown> | undefined {
  if (stat.size <= 0 || stat.size > MAX_REPRODUCIBILITY_BYTES || path.extname(fullPath).toLowerCase() !== ".json") {
    return undefined;
  }
  try {
    const parsed = JSON.parse(fs.readFileSync(fullPath, "utf8")) as Record<string, unknown>;
    return pickDefined({
      command: stringValue(parsed.command),
      args_hash: stringValue(parsed.args_hash),
      package_version: stringValue(parsed.package_version),
      unity_version: stringValue(parsed.unity_version),
      build_target: stringValue(parsed.build_target) ?? stringValue(parsed.active_build_target),
      test_mode: stringValue(parsed.test_mode) ?? stringValue(parsed.mode),
      test_filter: stringValue(parsed.test_filter) ?? stringValue(parsed.filter),
      created_at: stringValue(parsed.created_at),
    });
  } catch {
    return undefined;
  }
}

function pickDefined(values: Record<string, unknown>): Record<string, unknown> | undefined {
  const result = Object.fromEntries(Object.entries(values).filter(([, value]) => value !== undefined && value !== ""));
  return Object.keys(result).length > 0 ? result : undefined;
}

function stringValue(value: unknown): string | undefined {
  return typeof value === "string" ? value : undefined;
}

function bindArtifactPath(record: HeartbeatRecord, input: string): { status: "ok"; root: ArtifactRoot; relativePath: string; fullPath: string } | ArtifactError {
  const normalized = normalizeArtifactPath(input);
  if (!normalized) {
    return error(path.isAbsolute(input) || /^[A-Za-z]:[\\/]/.test(input) ? "absolute_path_rejected" : "path_traversal_rejected", "artifact_path must be project-relative and stay under approved artifact roots");
  }
  const root = ARTIFACT_ROOTS.find((candidate) => normalized === candidate.root || normalized.startsWith(`${candidate.root}/`));
  if (!root) {
    return error("path_not_allowed", "artifact_path must stay under approved artifact roots");
  }
  const rootRelativePath = normalized === root.root ? "" : normalized.slice(root.root.length + 1);
  if (root.type === "build_output" && rootRelativePath.includes("/")) {
    return error("path_not_allowed", "build output metadata is limited to top-level entries under BuildArtifacts/SGGUnityMcp");
  }

  const fullRoot = projectPath(record, root.root);
  const fullPath = projectPath(record, normalized);
  if (!isUnderRoot(fullPath, fullRoot)) {
    return error("path_traversal_rejected", "artifact_path must stay under approved artifact roots");
  }
  return { status: "ok", root, relativePath: normalized, fullPath };
}

function normalizeArtifactPath(input: string): string {
  const normalized = input.replaceAll("\\", "/").trim();
  if (normalized.length === 0 || normalized.includes("\0") || normalized.includes(":")) {
    return "";
  }
  if (path.isAbsolute(normalized) || /^[A-Za-z]:\//.test(normalized)) {
    return "";
  }
  if (normalized.split("/").some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    return "";
  }
  for (const root of ARTIFACT_ROOTS) {
    if (normalized === root.root || normalized.startsWith(`${root.root}/`)) return normalized;
  }
  if (normalized.startsWith("Library/SGGUnityMcp/")) {
    return normalized;
  }
  for (const root of ARTIFACT_ROOTS) {
    if (!root.root.startsWith("Library/SGGUnityMcp/")) continue;
    const shortRoot = root.root.slice("Library/SGGUnityMcp/".length);
    if (normalized === shortRoot || normalized.startsWith(`${shortRoot}/`)) {
      return `Library/SGGUnityMcp/${normalized}`;
    }
  }
  return "";
}

function directoryEntrySize(fullPath: string): number {
  try {
    return fs.readdirSync(fullPath).length;
  } catch {
    return 0;
  }
}

function projectPath(record: HeartbeatRecord, relativePath: string): string {
  return path.join(record.project_root, relativePath);
}

function isUnderRoot(candidate: string, root: string): boolean {
  const relative = path.relative(root, candidate);
  return relative === "" || (!relative.startsWith("..") && !path.isAbsolute(relative));
}

function hasSymbolicLinkInExistingChain(candidate: string, root: string): boolean {
  let current = candidate;
  if (!isUnderRoot(current, root)) return true;
  while (current !== root) {
    if (fs.existsSync(current) && fs.lstatSync(current).isSymbolicLink()) return true;
    const parent = path.dirname(current);
    if (parent === current) return true;
    current = parent;
  }
  return fs.existsSync(root) && fs.lstatSync(root).isSymbolicLink();
}

function clampInteger(value: number, min: number, max: number): number {
  if (!Number.isInteger(value)) {
    return min;
  }
  return Math.min(max, Math.max(min, value));
}

function isArtifactType(value: string): value is ArtifactType {
  return value === "job" || value === "screenshot" || value === "build_output";
}

function isArtifactFamily(value: string): value is ArtifactFamily {
  return (ARTIFACT_FAMILIES as readonly string[]).includes(value);
}

function isPrunableArtifactType(value: string): value is "job" | "screenshot" {
  return value === "job" || value === "screenshot";
}

function isPrunableArtifactFamily(value: string): value is PrunableArtifactFamily {
  return (PRUNABLE_ARTIFACT_FAMILIES as readonly string[]).includes(value);
}

function isArtifactPrunePreset(value: string): value is ArtifactPrunePreset {
  return (ARTIFACT_PRUNE_PRESETS as readonly string[]).includes(value);
}

function artifactTypeForFamily(family: PrunableArtifactFamily): "job" | "screenshot" {
  return family === "screenshot" ? "screenshot" : "job";
}

function error(code: ErrorCode, message: string, details?: Record<string, unknown>): ArtifactError {
  return { status: "error", error: { code, message, ...(details ? { details } : {}) } };
}
