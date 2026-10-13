import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";
import type { ErrorCode, GatewayError } from "./results.js";

const FEATURE_FLAG = "sgg_mcp_checkpoints";
const CHECKPOINT_ROOT = "Library/SGGUnityMcp/checkpoints";
const MAX_INCLUDE_PATHS = 50;
const MAX_FILES = 200;
const MAX_FILE_BYTES = 1024 * 1024;
const MAX_TOTAL_BYTES = 20 * 1024 * 1024;
const MAX_LIST_RESULTS = 50;
const DEFAULT_LIST_RESULTS = 20;
const MAX_DIFF_RESULTS = 100;
const DEFAULT_DIFF_RESULTS = 50;
const MAX_RESTORE_RESULTS = MAX_FILES;
const DEFAULT_RESTORE_RESULTS = MAX_FILES;
const MAX_PRUNE_DELETE = 50;
const DEFAULT_PRUNE_KEEP_LAST = 20;
const DEFAULT_PRUNE_DELETE = 20;
const DEFAULT_EXTENSIONS = new Set([".asmdef", ".asset", ".controller", ".cs", ".json", ".mat", ".prefab", ".shader", ".unity", ".uss", ".uxml"]);

export const CHECKPOINT_COMMANDS: CommandDescriptor[] = [
  {
    id: "checkpoint.status",
    title: "Checkpoint status",
    description: "Report fixed project-local checkpoint storage status without reading Assets content.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: emptySchema(),
    tags: ["checkpoint"],
    capability: "checkpoint",
    result_shape: "object_details",
    output_limit: { max_chars: 4096 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "checkpoint.list",
    title: "List checkpoints",
    description: "List bounded checkpoint metadata under Library/SGGUnityMcp/checkpoints/.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5000,
    schema: {
      type: "object",
      properties: {
        max_results: { type: "integer", minimum: 1, maximum: MAX_LIST_RESULTS },
      },
      additionalProperties: false,
    },
    tags: ["checkpoint"],
    capability: "checkpoint",
    result_shape: "object_details",
    output_limit: { max_results: MAX_LIST_RESULTS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "checkpoint.create",
    title: "Create checkpoint",
    description: "Create a bounded snapshot of selected Assets files plus .meta companions under fixed project-local storage.",
    package: "com.sungeargames.unity-mcp",
    risk: "write",
    idempotency: "unsafe",
    timeout_ms: 30000,
    schema: {
      type: "object",
      properties: {
        label: { type: "string", maxLength: 80, pattern: "^[A-Za-z0-9._ -]+$" },
        paths: {
          type: "array",
          maxItems: MAX_INCLUDE_PATHS,
          items: { type: "string", minLength: 1, maxLength: 240, pattern: "^Assets(/.+)?$" },
        },
      },
      additionalProperties: false,
    },
    tags: ["checkpoint"],
    capability: "checkpoint",
    result_shape: "object_details",
    output_limit: { max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "checkpoint.diff",
    title: "Diff checkpoint",
    description: "Compare a checkpoint manifest with current Assets files without modifying the project.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10000,
    schema: {
      type: "object",
      properties: {
        id: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9._-]+$" },
        max_results: { type: "integer", minimum: 1, maximum: MAX_DIFF_RESULTS },
      },
      required: ["id"],
      additionalProperties: false,
    },
    tags: ["checkpoint"],
    capability: "checkpoint",
    result_shape: "object_details",
    output_limit: { max_results: MAX_DIFF_RESULTS },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "checkpoint.delete",
    title: "Delete checkpoint",
    description: "Delete one checkpoint directory under fixed project-local storage; never deletes Assets files.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 10000,
    schema: {
      type: "object",
      properties: {
        id: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9._-]+$" },
      },
      required: ["id"],
      additionalProperties: false,
    },
    tags: ["checkpoint"],
    capability: "checkpoint",
    result_shape: "object_details",
    output_limit: { max_chars: 4096 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "checkpoint.restore",
    title: "Restore checkpoint",
    description: "Restore tracked Assets files from one checkpoint after policy approval; writes only manifest-listed Assets paths.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 30000,
    schema: {
      type: "object",
      properties: {
        id: { type: "string", minLength: 1, maxLength: 80, pattern: "^[A-Za-z0-9._-]+$" },
        max_results: { type: "integer", minimum: 1, maximum: MAX_RESTORE_RESULTS },
      },
      required: ["id"],
      additionalProperties: false,
    },
    tags: ["checkpoint"],
    capability: "checkpoint",
    result_shape: "object_details",
    output_limit: { max_results: MAX_RESTORE_RESULTS, max_chars: 16384 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
  {
    id: "checkpoint.prune",
    title: "Prune checkpoints",
    description: "Delete older valid checkpoint directories under the fixed checkpoint root after policy approval.",
    package: "com.sungeargames.unity-mcp",
    risk: "destructive",
    idempotency: "unsafe",
    timeout_ms: 30000,
    schema: {
      type: "object",
      properties: {
        keep_last: { type: "integer", minimum: 1, maximum: MAX_LIST_RESULTS },
        max_delete: { type: "integer", minimum: 1, maximum: MAX_PRUNE_DELETE },
        dry_run: { type: "boolean" },
      },
      additionalProperties: false,
    },
    tags: ["checkpoint"],
    capability: "checkpoint",
    result_shape: "object_details",
    output_limit: { max_results: MAX_PRUNE_DELETE, max_chars: 8192 },
    feature_flag: FEATURE_FLAG,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type CheckpointCommandResult = { status: "ok"; data: unknown } | { status: "error"; error: GatewayError };

type CheckpointManifest = {
  schema_version: 1;
  id: string;
  label?: string;
  created_at: string;
  project_id: string;
  storage_root: string;
  restore_supported: boolean;
  limits: { max_files: number; max_file_bytes: number; max_total_bytes: number };
  files: Array<{ path: string; bytes: number; sha256: string; meta_for?: string }>;
  truncated: boolean;
};

type CheckpointChange = { path: string; status: "missing" | "changed" | "unchanged"; bytes?: number; checkpoint_bytes: number };

type CheckpointError = Extract<CheckpointCommandResult, { status: "error" }>;

export function findCheckpointCommand(commandId: string): CommandDescriptor | undefined {
  return CHECKPOINT_COMMANDS.find((command) => command.id === commandId);
}

export function runCheckpointCommand(commandId: string, args: Record<string, unknown>, record: HeartbeatRecord | undefined, now: Date = new Date()): CheckpointCommandResult {
  if (!record) {
    return error("project_offline", "Checkpoint commands require a resolved live project heartbeat");
  }

  switch (commandId) {
    case "checkpoint.status":
      return { status: "ok", data: checkpointStatus(record) };
    case "checkpoint.list":
      return { status: "ok", data: listCheckpoints(args, record) };
    case "checkpoint.create":
      return createCheckpoint(args, record, now);
    case "checkpoint.diff":
      return diffCheckpoint(args, record);
    case "checkpoint.delete":
      return deleteCheckpoint(args, record);
    case "checkpoint.restore":
      return restoreCheckpoint(args, record, now);
    case "checkpoint.prune":
      return pruneCheckpoints(args, record);
    default:
      return error("command_not_found", "Checkpoint command not found");
  }
}

function checkpointStatus(record: HeartbeatRecord): Record<string, unknown> {
  const root = checkpointsRoot(record);
  const checkpoints = fs.existsSync(root.fullPath) ? listCheckpointDirs(root.fullPath) : [];
  return {
    storage_root: root.relativeRoot,
    exists: fs.existsSync(root.fullPath),
    checkpoint_count: checkpoints.length,
    snapshot_scope: "Assets/ only",
    default_scope: "bounded supported files under Assets/ plus .meta companions",
    max_files: MAX_FILES,
    max_file_bytes: MAX_FILE_BYTES,
    max_total_bytes: MAX_TOTAL_BYTES,
    restore_supported: true,
  };
}

function listCheckpoints(args: Record<string, unknown>, record: HeartbeatRecord): Record<string, unknown> {
  const root = checkpointsRoot(record);
  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_LIST_RESULTS, 1, MAX_LIST_RESULTS);
  if (!fs.existsSync(root.fullPath)) {
    return { storage_root: root.relativeRoot, checkpoints: [], max_results: maxResults, truncated: false };
  }

  const checkpoints = listCheckpointDirs(root.fullPath)
    .map((id) => readManifest(record, id))
    .filter((manifest): manifest is CheckpointManifest => manifest !== undefined)
    .map((manifest) => ({
      id: manifest.id,
      ...(manifest.label ? { label: manifest.label } : {}),
      created_at: manifest.created_at,
      file_count: manifest.files.length,
      total_bytes: manifest.files.reduce((sum, file) => sum + file.bytes, 0),
      manifest_path: `${root.relativeRoot}/${manifest.id}/manifest.json`,
      restore_supported: true,
    }))
    .sort((left, right) => right.created_at.localeCompare(left.created_at) || left.id.localeCompare(right.id));

  return { storage_root: root.relativeRoot, checkpoints: checkpoints.slice(0, maxResults), max_results: maxResults, truncated: checkpoints.length > maxResults };
}

function createCheckpoint(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): CheckpointCommandResult {
  const paths = Array.isArray(args.paths) ? args.paths : ["Assets"];
  const labels = typeof args.label === "string" && args.label.trim().length > 0 ? args.label.trim() : undefined;
  const collected = collectSnapshotFiles(record, paths);
  if (collected.status === "error") {
    return collected;
  }

  const root = checkpointsRoot(record);
  const id = checkpointId(now);
  const checkpointDir = path.join(root.fullPath, id);
  const filesDir = path.join(checkpointDir, "files");
  if (fs.existsSync(checkpointDir)) {
    return error("invalid_field_value", "Checkpoint id already exists; retry with a later timestamp", { field: "id" });
  }

  fs.mkdirSync(filesDir, { recursive: true });
  const manifestFiles: CheckpointManifest["files"] = [];
  for (const file of collected.files) {
    const source = projectPath(record, file);
    const destination = path.join(filesDir, file);
    fs.mkdirSync(path.dirname(destination), { recursive: true });
    fs.copyFileSync(source, destination);
    const bytes = fs.statSync(source).size;
    manifestFiles.push({ path: file, bytes, sha256: sha256File(source), ...(file.endsWith(".meta") ? { meta_for: file.slice(0, -".meta".length) } : {}) });
  }

  const manifest: CheckpointManifest = {
    schema_version: 1,
    id,
    ...(labels ? { label: labels } : {}),
    created_at: now.toISOString(),
    project_id: record.project,
    storage_root: root.relativeRoot,
    restore_supported: true,
    limits: { max_files: MAX_FILES, max_file_bytes: MAX_FILE_BYTES, max_total_bytes: MAX_TOTAL_BYTES },
    files: manifestFiles,
    truncated: collected.truncated,
  };
  fs.writeFileSync(path.join(checkpointDir, "manifest.json"), JSON.stringify(manifest, null, 2), "utf8");
  return {
    status: "ok",
    data: {
      id,
      ...(labels ? { label: labels } : {}),
      storage_root: root.relativeRoot,
      manifest_path: `${root.relativeRoot}/${id}/manifest.json`,
      file_count: manifestFiles.length,
      total_bytes: manifestFiles.reduce((sum, file) => sum + file.bytes, 0),
      truncated: collected.truncated,
      restore_supported: true,
      artifact_refs: [`${root.relativeRoot}/${id}/manifest.json`],
    },
  };
}

function diffCheckpoint(args: Record<string, unknown>, record: HeartbeatRecord): CheckpointCommandResult {
  const id = typeof args.id === "string" ? args.id : "";
  const manifest = readManifest(record, id);
  if (!manifest) {
    return error("asset_not_found", "Checkpoint manifest was not found under the fixed checkpoint root");
  }

  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_DIFF_RESULTS, 1, MAX_DIFF_RESULTS);
  const diff = summarizeManifestChanges(manifest, record, maxResults);

  return {
    status: "ok",
    data: {
      id: manifest.id,
      file_count: manifest.files.length,
      summary: diff.summary,
      changes: diff.changes,
      max_results: maxResults,
      truncated: diff.truncated,
      restore_supported: true,
    },
  };
}

function deleteCheckpoint(args: Record<string, unknown>, record: HeartbeatRecord): CheckpointCommandResult {
  const id = typeof args.id === "string" ? args.id : "";
  const bound = bindCheckpointDir(record, id);
  if (bound.status === "error") {
    return bound;
  }
  if (!fs.existsSync(bound.fullPath)) {
    return error("asset_not_found", "Checkpoint was not found under the fixed checkpoint root");
  }

  fs.rmSync(bound.fullPath, { recursive: true, force: true });
  return { status: "ok", data: { id, deleted: true, assets_deleted: false, storage_root: checkpointsRoot(record).relativeRoot } };
}

function restoreCheckpoint(args: Record<string, unknown>, record: HeartbeatRecord, now: Date): CheckpointCommandResult {
  const id = typeof args.id === "string" ? args.id : "";
  const manifest = readManifest(record, id);
  if (!manifest) {
    return error("asset_not_found", "Checkpoint manifest was not found under the fixed checkpoint root");
  }

  const bound = bindCheckpointDir(record, id);
  if (bound.status === "error") {
    return bound;
  }

  const maxResults = clampInteger(typeof args.max_results === "number" ? args.max_results : DEFAULT_RESTORE_RESULTS, 1, MAX_RESTORE_RESULTS);
  const diff = summarizeManifestChanges(manifest, record, maxResults);
  const preflight = validateRestorePreflight(record, bound.fullPath, manifest);
  if (preflight.status === "error") {
    const diagnostic = writeRestoreReport(record, manifest.id, now, { status: "failed", phase: "preflight", error: preflight.error, summary_before: diff.summary, changes: diff.changes });
    return error(preflight.error.code, preflight.error.message, { ...preflight.error.details, ...(diagnostic ? { diagnostic_artifact: diagnostic } : {}) });
  }

  const filesRoot = path.join(bound.fullPath, "files");
  const restored: Array<{ path: string; previous_status: "missing" | "changed" }> = [];
  try {
    for (const file of manifest.files) {
      const before = diff.all_changes.find((change) => change.path === file.path);
      if (!before || before.status === "unchanged") {
        continue;
      }

      const source = path.join(filesRoot, file.path);
      const destination = projectPath(record, file.path);
      copyFileAtomic(source, destination, record, file.path);
      restored.push({ path: file.path, previous_status: before.status });
    }
  } catch (restoreError) {
    const diagnostic = writeRestoreReport(record, manifest.id, now, {
      status: "failed",
      phase: "write",
      error: restoreError instanceof Error ? restoreError.message : String(restoreError),
      restored,
      summary_before: diff.summary,
      changes: diff.changes,
    });
    return error("internal_error", "Checkpoint restore failed; a bounded diagnostic artifact was written under the checkpoint directory", { id: manifest.id, restored_count: restored.length, ...(diagnostic ? { diagnostic_artifact: diagnostic } : {}) });
  }

  return {
    status: "ok",
    data: {
      id: manifest.id,
      storage_root: checkpointsRoot(record).relativeRoot,
      file_count: manifest.files.length,
      summary_before: diff.summary,
      changes: diff.changes,
      restored,
      restored_count: restored.length,
      skipped_unchanged: diff.summary.unchanged,
      max_results: maxResults,
      truncated: diff.truncated,
      assets_deleted: false,
      restore_supported: true,
      next_valid_actions: ["assets.refresh", "compile.status"],
    },
  };
}

function pruneCheckpoints(args: Record<string, unknown>, record: HeartbeatRecord): CheckpointCommandResult {
  const root = checkpointsRoot(record);
  const keepLast = clampInteger(typeof args.keep_last === "number" ? args.keep_last : DEFAULT_PRUNE_KEEP_LAST, 1, MAX_LIST_RESULTS);
  const maxDelete = clampInteger(typeof args.max_delete === "number" ? args.max_delete : DEFAULT_PRUNE_DELETE, 1, MAX_PRUNE_DELETE);
  const dryRun = args.dry_run === true;
  if (!fs.existsSync(root.fullPath)) {
    return { status: "ok", data: { storage_root: root.relativeRoot, keep_last: keepLast, max_delete: maxDelete, dry_run: dryRun, candidate_count: 0, deleted_count: 0, deleted: [], kept_count: 0, invalid_checkpoint_dirs: 0, truncated: false, assets_deleted: false } };
  }

  let invalidCheckpointDirs = 0;
  const checkpoints = listCheckpointDirs(root.fullPath)
    .map((id) => {
      const manifest = readManifest(record, id);
      if (!manifest) {
        invalidCheckpointDirs += 1;
      }
      return manifest;
    })
    .filter((manifest): manifest is CheckpointManifest => manifest !== undefined)
    .sort((left, right) => right.created_at.localeCompare(left.created_at) || left.id.localeCompare(right.id));
  const candidates = checkpoints.slice(keepLast);
  const selected = [...candidates].reverse().slice(0, maxDelete);
  if (!dryRun) {
    for (const manifest of selected) {
      const bound = bindCheckpointDir(record, manifest.id);
      if (bound.status === "ok") {
        fs.rmSync(bound.fullPath, { recursive: true, force: true });
      }
    }
  }

  return {
    status: "ok",
    data: {
      storage_root: root.relativeRoot,
      keep_last: keepLast,
      max_delete: maxDelete,
      dry_run: dryRun,
      candidate_count: candidates.length,
      deleted_count: dryRun ? 0 : selected.length,
      deleted: selected.map((manifest) => manifest.id),
      kept_count: Math.min(checkpoints.length, keepLast),
      invalid_checkpoint_dirs: invalidCheckpointDirs,
      truncated: candidates.length > maxDelete,
      assets_deleted: false,
    },
  };
}

function summarizeManifestChanges(manifest: CheckpointManifest, record: HeartbeatRecord, maxResults: number): { summary: { missing: number; changed: number; unchanged: number }; changes: CheckpointChange[]; all_changes: CheckpointChange[]; truncated: boolean } {
  const changes: CheckpointChange[] = [];
  const allChanges: CheckpointChange[] = [];
  const summary = { missing: 0, changed: 0, unchanged: 0 };
  for (const file of manifest.files) {
    const current = projectPath(record, file.path);
    let change: CheckpointChange;
    if (!fs.existsSync(current)) {
      summary.missing += 1;
      change = { path: file.path, status: "missing", checkpoint_bytes: file.bytes };
    } else {
      const stat = fs.lstatSync(current);
      const same = stat.isFile() && !stat.isSymbolicLink() && stat.size === file.bytes && sha256File(current) === file.sha256;
      if (same) {
        summary.unchanged += 1;
        change = { path: file.path, status: "unchanged", bytes: stat.size, checkpoint_bytes: file.bytes };
      } else {
        summary.changed += 1;
        change = { path: file.path, status: "changed", ...(stat.isFile() ? { bytes: stat.size } : {}), checkpoint_bytes: file.bytes };
      }
    }

    allChanges.push(change);
    if (changes.length < maxResults) {
      changes.push(change);
    }
  }

  return { summary, changes, all_changes: allChanges, truncated: manifest.files.length > maxResults };
}

function validateRestorePreflight(record: HeartbeatRecord, checkpointDir: string, manifest: CheckpointManifest): { status: "ok" } | CheckpointError {
  const filesRoot = path.join(checkpointDir, "files");
  for (const file of manifest.files) {
    const snapshot = path.join(filesRoot, file.path);
    if (!isUnderRoot(snapshot, filesRoot)) {
      return error("path_traversal_rejected", "Checkpoint snapshot path must stay under the checkpoint files root", { path: file.path });
    }
    if (!fs.existsSync(snapshot)) {
      return error("asset_not_found", "Checkpoint snapshot file is missing", { path: file.path });
    }
    const snapshotStat = fs.lstatSync(snapshot);
    if (snapshotStat.isSymbolicLink() || !snapshotStat.isFile()) {
      return error("path_not_allowed", "Checkpoint snapshot files must be regular files", { path: file.path });
    }
    if (snapshotStat.size !== file.bytes || sha256File(snapshot) !== file.sha256) {
      return error("invalid_field_value", "Checkpoint snapshot file does not match its manifest hash", { path: file.path });
    }

    const destination = validateRestoreDestination(record, file.path);
    if (destination.status === "error") {
      return destination;
    }
  }

  return { status: "ok" };
}

function validateRestoreDestination(record: HeartbeatRecord, relativePath: string): { status: "ok" } | CheckpointError {
  const assetsRoot = path.join(record.project_root, "Assets");
  const destination = projectPath(record, relativePath);
  if (!isUnderRoot(destination, assetsRoot)) {
    return error("path_traversal_rejected", "Checkpoint restore paths must stay under Assets/", { path: relativePath });
  }

  let current = record.project_root;
  const parentSegments = relativePath.split("/").slice(0, -1);
  for (const segment of parentSegments) {
    current = path.join(current, segment);
    if (!fs.existsSync(current)) {
      continue;
    }
    const stat = fs.lstatSync(current);
    if (stat.isSymbolicLink() || !stat.isDirectory()) {
      return error("path_not_allowed", "Checkpoint restore parent paths must be real directories", { path: relativePath });
    }
  }

  if (fs.existsSync(destination)) {
    const destinationStat = fs.lstatSync(destination);
    if (destinationStat.isSymbolicLink() || !destinationStat.isFile()) {
      return error("path_not_allowed", "Checkpoint restore destinations must be regular files or missing files", { path: relativePath });
    }
  }

  return { status: "ok" };
}

function copyFileAtomic(source: string, destination: string, record: HeartbeatRecord, relativePath: string): void {
  fs.mkdirSync(path.dirname(destination), { recursive: true });
  const validation = validateRestoreDestination(record, relativePath);
  if (validation.status === "error") {
    throw new Error(validation.error.message);
  }

  const tempPath = path.join(path.dirname(destination), `.${path.basename(destination)}.${process.pid}.restore.tmp`);
  try {
    fs.copyFileSync(source, tempPath);
    fs.renameSync(tempPath, destination);
  } catch (restoreError) {
    fs.rmSync(tempPath, { force: true });
    throw restoreError;
  }
}

function writeRestoreReport(record: HeartbeatRecord, id: string, now: Date, report: Record<string, unknown>): string | undefined {
  const bound = bindCheckpointDir(record, id);
  if (bound.status === "error") {
    return undefined;
  }

  const root = checkpointsRoot(record);
  const reportsDir = path.join(bound.fullPath, "restore-reports");
  const fileName = `restore-${now.toISOString().replaceAll(/[:.]/g, "-")}.json`;
  try {
    fs.mkdirSync(reportsDir, { recursive: true });
    fs.writeFileSync(path.join(reportsDir, fileName), JSON.stringify({ schema_version: 1, id, created_at: now.toISOString(), ...report }, null, 2), "utf8");
    return `${root.relativeRoot}/${id}/restore-reports/${fileName}`;
  } catch {
    return undefined;
  }
}

function collectSnapshotFiles(record: HeartbeatRecord, rawPaths: unknown[]): { status: "ok"; files: string[]; truncated: boolean } | CheckpointError {
  const defaultScope = rawPaths.length === 1 && rawPaths[0] === "Assets";
  const requested = rawPaths.map((item) => typeof item === "string" ? item : "");
  const normalized: string[] = [];
  for (const item of requested) {
    const safe = safeAssetsPath(item);
    if (safe.status === "error") {
      return safe;
    }
    normalized.push(safe.path);
  }

  const files = new Set<string>();
  let totalBytes = 0;
  let truncated = false;
  for (const relativePath of normalized) {
    const fullPath = projectPath(record, relativePath);
    if (!isUnderRoot(fullPath, path.join(record.project_root, "Assets"))) {
      return error("path_traversal_rejected", "Checkpoint paths must stay under Assets/");
    }
    if (!fs.existsSync(fullPath)) {
      return error("asset_not_found", "Checkpoint path was not found under Assets/", { path: relativePath });
    }
    const added = addSnapshotPath(record, relativePath, files, totalBytes, defaultScope || fs.lstatSync(fullPath).isDirectory());
    if (added.status === "error") {
      return added;
    }
    totalBytes = added.totalBytes;
    truncated = truncated || added.truncated;
  }

  return { status: "ok", files: [...files].sort(), truncated };
}

function addSnapshotPath(record: HeartbeatRecord, relativePath: string, files: Set<string>, initialBytes: number, filterExtensions: boolean): { status: "ok"; totalBytes: number; truncated: boolean } | CheckpointError {
  let totalBytes = initialBytes;
  let truncated = false;
  const fullPath = projectPath(record, relativePath);
  const stat = fs.lstatSync(fullPath);
  if (stat.isSymbolicLink()) {
    return error("path_not_allowed", "Checkpoint paths must not be symbolic links", { path: relativePath });
  }

  if (stat.isDirectory()) {
    for (const entry of fs.readdirSync(fullPath, { withFileTypes: true })) {
      if (files.size >= MAX_FILES || totalBytes >= MAX_TOTAL_BYTES) {
        return { status: "ok", totalBytes, truncated: true };
      }
      const child = `${relativePath}/${entry.name}`;
      const added = addSnapshotPath(record, child, files, totalBytes, filterExtensions);
      if (added.status === "error") {
        return added;
      }
      totalBytes = added.totalBytes;
      truncated = truncated || added.truncated;
    }
    return { status: "ok", totalBytes, truncated };
  }

  if (!stat.isFile() || relativePath.endsWith(".meta")) {
    return { status: "ok", totalBytes, truncated };
  }
  if (relativePath === "Assets" || !relativePath.includes("/")) {
    return { status: "ok", totalBytes, truncated };
  }
  if (filterExtensions && !DEFAULT_EXTENSIONS.has(path.extname(relativePath).toLowerCase())) {
    return { status: "ok", totalBytes, truncated };
  }
  if (stat.size > MAX_FILE_BYTES || files.size >= MAX_FILES || totalBytes + stat.size > MAX_TOTAL_BYTES) {
    return { status: "ok", totalBytes, truncated: true };
  }

  files.add(relativePath);
  totalBytes += stat.size;
  const metaPath = `${relativePath}.meta`;
  const metaFullPath = projectPath(record, metaPath);
  if (fs.existsSync(metaFullPath) && files.size < MAX_FILES) {
    const metaStat = fs.lstatSync(metaFullPath);
    if (!metaStat.isSymbolicLink() && metaStat.isFile() && metaStat.size <= MAX_FILE_BYTES && totalBytes + metaStat.size <= MAX_TOTAL_BYTES) {
      files.add(metaPath);
      totalBytes += metaStat.size;
    }
  }
  return { status: "ok", totalBytes, truncated };
}

function readManifest(record: HeartbeatRecord, id: string): CheckpointManifest | undefined {
  const bound = bindCheckpointDir(record, id);
  if (bound.status === "error") {
    return undefined;
  }
  const manifestPath = path.join(bound.fullPath, "manifest.json");
  if (!fs.existsSync(manifestPath)) {
    return undefined;
  }
  try {
    const manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8")) as Partial<CheckpointManifest>;
    if (manifest.schema_version !== 1 || manifest.id !== id || !Array.isArray(manifest.files) || !manifest.files.every(isSafeManifestFile)) {
      return undefined;
    }
    return manifest as CheckpointManifest;
  } catch {
    return undefined;
  }
}

function isSafeManifestFile(file: unknown): file is CheckpointManifest["files"][number] {
  if (typeof file !== "object" || file === null) {
    return false;
  }
  const entry = file as { path?: unknown; bytes?: unknown; sha256?: unknown; meta_for?: unknown };
  if (typeof entry.path !== "string" || entry.path === "Assets" || safeAssetsPath(entry.path).status === "error") {
    return false;
  }
  if (typeof entry.bytes !== "number" || !Number.isInteger(entry.bytes) || entry.bytes < 0 || entry.bytes > MAX_FILE_BYTES) {
    return false;
  }
  if (typeof entry.sha256 !== "string" || !/^[a-f0-9]{64}$/i.test(entry.sha256)) {
    return false;
  }
  if (entry.meta_for !== undefined && (typeof entry.meta_for !== "string" || safeAssetsPath(entry.meta_for).status === "error")) {
    return false;
  }
  return true;
}

function bindCheckpointDir(record: HeartbeatRecord, id: string): { status: "ok"; fullPath: string } | CheckpointError {
  if (!/^[A-Za-z0-9._-]+$/.test(id) || id.includes("..") || path.isAbsolute(id) || /^[A-Za-z]:[\\/]/.test(id)) {
    return error(path.isAbsolute(id) || /^[A-Za-z]:[\\/]/.test(id) ? "absolute_path_rejected" : "path_traversal_rejected", "Checkpoint id must be a safe single relative name");
  }
  const root = checkpointsRoot(record);
  const fullPath = path.join(root.fullPath, id);
  if (!isUnderRoot(fullPath, root.fullPath)) {
    return error("path_traversal_rejected", "Checkpoint path must stay under the fixed checkpoint root");
  }
  return { status: "ok", fullPath };
}

function safeAssetsPath(input: string): { status: "ok"; path: string } | CheckpointError {
  const normalized = input.replaceAll("\\", "/").trim();
  if (normalized.includes("\0")) {
    return error("invalid_field_value", "Checkpoint path must not contain NUL", { field: "paths" });
  }
  if (path.isAbsolute(normalized) || /^[A-Za-z]:\//.test(normalized)) {
    return error("absolute_path_rejected", "Checkpoint paths must be Unity-relative Assets/ paths");
  }
  if (normalized !== "Assets" && !normalized.startsWith("Assets/")) {
    return error("path_not_allowed", "Checkpoint paths must stay under Assets/");
  }
  if (normalized.split("/").some((segment) => segment.length === 0 || segment === "." || segment === "..")) {
    return error("path_traversal_rejected", "Checkpoint paths must not contain traversal segments");
  }
  return { status: "ok", path: normalized };
}

function checkpointsRoot(record: HeartbeatRecord): { fullPath: string; relativeRoot: string } {
  return { fullPath: path.join(record.project_root, CHECKPOINT_ROOT), relativeRoot: CHECKPOINT_ROOT };
}

function projectPath(record: HeartbeatRecord, relativePath: string): string {
  return path.join(record.project_root, relativePath);
}

function listCheckpointDirs(root: string): string[] {
  return fs.readdirSync(root, { withFileTypes: true }).filter((entry) => entry.isDirectory()).map((entry) => entry.name);
}

function checkpointId(now: Date): string {
  return `checkpoint-${now.toISOString().replaceAll(/[:.]/g, "-")}`;
}

function sha256File(filePath: string): string {
  return createHash("sha256").update(fs.readFileSync(filePath)).digest("hex");
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

function error(code: ErrorCode, message: string, details?: Record<string, unknown>): CheckpointError {
  return { status: "error", error: { code, message, ...(details ? { details } : {}) } };
}

function emptySchema(): Record<string, unknown> {
  return { type: "object", properties: {}, additionalProperties: false };
}
