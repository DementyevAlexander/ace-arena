import fs, { type BigIntStats } from "node:fs";
import path from "node:path";

import type { CommandDescriptor } from "../bridge/bridge-client.js";
import { CanonicalPathService } from "../paths/canonical-path.js";
import type { HeartbeatRecord } from "../registry/project-registry.js";
import { sameFileObjectIdentity } from "../security/safe-storage.js";
import type { GatewayError } from "./results.js";

export const EDITOR_MENU_OPERATION_START_COMMAND = "editor.menu.operation.start";
export const EDITOR_MENU_OPERATION_STATUS_COMMAND = "editor.menu.operation.status";
export const EDITOR_MENU_OPERATION_CORRELATION_TAG = "editor_menu_operation_correlated";
export const EDITOR_MENU_OPERATION_CONTRACT = "sgg.unity_mcp.editor_menu_operation.v1";
export const EDITOR_MENU_OPERATION_ARTIFACT_ROOT = "Library/SGGUnityMcp/job-artifacts/menu-operations";

const MAX_RECEIPT_BYTES = 65_536;
const OPERATION_ID_PATTERN = /^menu-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$/;
const HASH_PATTERN = /^sha256:[0-9a-f]{64}$/;
const TERMINAL_STATUSES = new Set(["succeeded", "failed", "expired", "unknown_after_reload"]);
const KNOWN_STATUSES = new Set(["queued", "running", ...TERMINAL_STATUSES]);
const NO_FOLLOW = fs.constants.O_NOFOLLOW ?? 0;
const canonicalPaths = new CanonicalPathService();

export const EDITOR_MENU_OPERATION_COMMANDS: CommandDescriptor[] = [
  {
    id: EDITOR_MENU_OPERATION_STATUS_COMMAND,
    title: "Editor menu operation status",
    description: "Read and validate the bounded project-local receipt for an explicitly eligible long-running editor menu operation.",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 5_000,
    schema: {
      type: "object",
      properties: {
        operation_id: { type: "string", pattern: "^menu-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$" },
      },
      required: ["operation_id"],
      additionalProperties: false,
    },
    tags: ["editor", "menu", "status", "artifact"],
    capability: "editor_utilities",
    result_shape: "operation_status",
    output_limit: { max_chars: MAX_RECEIPT_BYTES },
    feature_flag: "editor_utilities",
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  },
];

export type EditorMenuOperationCommandResult = { status: "ok"; data: unknown } | { status: "error"; error: GatewayError };
export type EditorMenuOperationReadOptions = { afterOpen?: (file: string) => void; afterRead?: (file: string) => void };

export function findEditorMenuOperationCommand(commandId: string): CommandDescriptor | undefined {
  return EDITOR_MENU_OPERATION_COMMANDS.find((command) => command.id === commandId);
}

export function runEditorMenuOperationCommand(commandId: string, args: Record<string, unknown>, record: HeartbeatRecord | undefined, options: EditorMenuOperationReadOptions = {}): EditorMenuOperationCommandResult {
  if (commandId !== EDITOR_MENU_OPERATION_STATUS_COMMAND) {
    return error("command_not_found", "Editor menu operation command not found");
  }
  if (!record) {
    return error("project_offline", "Editor menu operation status requires a known project heartbeat");
  }

  const operationId = typeof args.operation_id === "string" ? args.operation_id : "";
  if (!OPERATION_ID_PATTERN.test(operationId)) {
    return error("invalid_field_value", "operation_id has an invalid format");
  }

  const artifactPath = `${EDITOR_MENU_OPERATION_ARTIFACT_ROOT}/${operationId}.json`;
  const root = path.join(record.project_root, EDITOR_MENU_OPERATION_ARTIFACT_ROOT);
  const fullPath = path.join(root, `${operationId}.json`);
  if (!isSameOrChild(fullPath, root) || hasSymbolicLinkInExistingChain(fullPath, root) || hasSymbolicLinkInExistingChain(root, record.project_root)) {
    return error("path_not_allowed", "The editor menu operation receipt path is not safe");
  }

  let content: string;
  let descriptor: number | undefined;
  try {
    if (!fs.existsSync(fullPath)) {
      return error("menu_operation_not_found", "The editor menu operation receipt was not found", { operation_id: operationId, artifact_path: artifactPath });
    }
    const pathStat = fs.lstatSync(fullPath, { bigint: true });
    if (!pathStat.isFile() || pathStat.isSymbolicLink()) {
      return error("path_not_allowed", "The editor menu operation receipt must be a regular file");
    }
    descriptor = fs.openSync(fullPath, fs.constants.O_RDONLY | NO_FOLLOW);
    const openedStat = fs.fstatSync(descriptor, { bigint: true });
    if (!openedStat.isFile() || !sameFileIdentity(pathStat, openedStat)) {
      return error("menu_operation_receipt_unavailable", "The editor menu operation receipt changed while it was opened", { operation_id: operationId, artifact_path: artifactPath });
    }
    if (openedStat.size <= 0n || openedStat.size > BigInt(MAX_RECEIPT_BYTES)) {
      return error("menu_operation_receipt_invalid", "The editor menu operation receipt is outside its bounded size", { operation_id: operationId, artifact_path: artifactPath, bytes: Number(openedStat.size), max_bytes: MAX_RECEIPT_BYTES });
    }
    options.afterOpen?.(fullPath);
    const size = Number(openedStat.size);
    const buffer = Buffer.alloc(size);
    let offset = 0;
    while (offset < buffer.length) {
      const read = fs.readSync(descriptor, buffer, offset, buffer.length - offset, offset);
      if (read <= 0) break;
      offset += read;
    }
    options.afterRead?.(fullPath);
    const finalDescriptorStat = fs.fstatSync(descriptor, { bigint: true });
    fs.closeSync(descriptor);
    descriptor = undefined;
    const finalPathStat = fs.lstatSync(fullPath, { bigint: true });
    if (offset !== buffer.length
      || !sameFileIdentity(openedStat, finalDescriptorStat)
      || !sameFileIdentity(openedStat, finalPathStat)
      || !finalPathStat.isFile()
      || finalPathStat.isSymbolicLink()
      || hasSymbolicLinkInExistingChain(fullPath, root)
      || hasSymbolicLinkInExistingChain(root, record.project_root)) {
      return error("menu_operation_receipt_unavailable", "The editor menu operation receipt changed while it was read", { operation_id: operationId, artifact_path: artifactPath });
    }
    content = buffer.toString("utf8");
  } catch {
    return error("menu_operation_receipt_unavailable", "The editor menu operation receipt could not be read safely", { operation_id: operationId, artifact_path: artifactPath });
  } finally {
    if (descriptor !== undefined) {
      try { fs.closeSync(descriptor); } catch { }
    }
  }

  let receipt: unknown;
  try {
    receipt = JSON.parse(content);
  } catch {
    return error("menu_operation_receipt_invalid", "The editor menu operation receipt is not valid JSON", { operation_id: operationId, artifact_path: artifactPath });
  }
  if (!isValidReceipt(receipt, operationId, artifactPath)) {
    return error("menu_operation_receipt_invalid", "The editor menu operation receipt identity or lifecycle fields are invalid", { operation_id: operationId, artifact_path: artifactPath });
  }

  return { status: "ok", data: receipt };
}

function isValidReceipt(value: unknown, operationId: string, artifactPath: string): value is Record<string, unknown> {
  if (!isRecord(value)
    || value.contract !== EDITOR_MENU_OPERATION_CONTRACT
    || value.artifact_schema_version !== 1
    || value.command !== EDITOR_MENU_OPERATION_START_COMMAND
    || value.operation_id !== operationId
    || value.artifact_path !== artifactPath
    || value.artifact_persisted !== true
    || value.operation_created !== true
    || typeof value.status !== "string"
    || !KNOWN_STATUSES.has(value.status)
    || typeof value.terminal !== "boolean"
    || typeof value.success !== "boolean"
    || typeof value.menu_execute_invoked !== "boolean"
    || !(value.execute_menu_item_returned === null || typeof value.execute_menu_item_returned === "boolean")
    || typeof value.dispatch_count !== "number"
    || !Number.isInteger(value.dispatch_count)
    || (value.dispatch_count !== 0 && value.dispatch_count !== 1)
    || typeof value.menu_path !== "string"
    || value.menu_path.length === 0
    || value.menu_path.length > 128
    || typeof value.args_hash !== "string"
    || !HASH_PATTERN.test(value.args_hash)
    || typeof value.caller_args_hash !== "string"
    || !HASH_PATTERN.test(value.caller_args_hash)
    || !safeTimestamp(value.created_at_utc)
    || !safeTimestamp(value.queued_at_utc)
    || !safeTimestamp(value.queue_deadline_utc)
    || !isRecord(value.eligibility)
    || typeof value.eligibility.marker_type !== "string"
    || typeof value.eligibility.marker_assembly !== "string"
    || !isRecord(value.eligibility.method_identity)) {
    return false;
  }

  const terminal = TERMINAL_STATUSES.has(value.status);
  if (value.terminal !== terminal
    || value.success !== (value.status === "succeeded")
    || (value.dispatch_count === 1 && !safeTimestamp(value.running_at_utc))
    || (terminal && !safeTimestamp(value.terminal_at_utc))) {
    return false;
  }

  const lifecycleValid = value.status === "queued"
    ? value.dispatch_count === 0 && value.menu_execute_invoked === false && value.execute_menu_item_returned === null
    : value.status === "running"
      ? value.dispatch_count === 1 && value.menu_execute_invoked === true && value.execute_menu_item_returned === null
      : value.status === "succeeded"
        ? value.dispatch_count === 1 && value.menu_execute_invoked === true && value.execute_menu_item_returned === true
        : value.status === "expired"
          ? value.dispatch_count === 0 && value.menu_execute_invoked === false && value.execute_menu_item_returned === null
          : value.status === "unknown_after_reload"
            ? value.dispatch_count === 1 && value.menu_execute_invoked === true && value.execute_menu_item_returned === null
            : value.dispatch_count === 0
              ? value.menu_execute_invoked === false && value.execute_menu_item_returned === null
              : value.menu_execute_invoked === true && value.execute_menu_item_returned !== true;
  if (!lifecycleValid) return false;

  const identity = value.eligibility.method_identity;
  return typeof identity.assembly === "string" && identity.assembly.length > 0
    && typeof identity.declaring_type === "string" && identity.declaring_type.length > 0
    && typeof identity.name === "string" && identity.name.length > 0
    && typeof identity.signature === "string" && identity.signature.length > 0;
}

function safeTimestamp(value: unknown): boolean {
  if (typeof value !== "string" || value.length === 0 || value.length > 40) return false;
  return Number.isFinite(Date.parse(value));
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isSameOrChild(candidate: string, root: string): boolean {
  try {
    canonicalPaths.assertInsideProject(canonicalPaths.normalizeProjectRoot(root), canonicalPaths.normalize(candidate));
    return true;
  } catch {
    return false;
  }
}

function sameFileIdentity(left: BigIntStats, right: BigIntStats): boolean {
  return sameFileObject(left, right)
    && left.size === right.size
    && left.mtimeNs === right.mtimeNs
    && left.ctimeNs === right.ctimeNs;
}

function sameFileObject(left: BigIntStats, right: BigIntStats): boolean {
  return sameFileObjectIdentity(left, right, process.platform);
}

function hasSymbolicLinkInExistingChain(candidate: string, root: string): boolean {
  let current = candidate;
  while (isSameOrChild(current, root)) {
    try {
      if (fs.lstatSync(current).isSymbolicLink()) return true;
    } catch (caught) {
      if (!(caught instanceof Error) || !("code" in caught) || (caught as NodeJS.ErrnoException).code !== "ENOENT") return true;
    }
    if (current === root) break;
    const parent = path.dirname(current);
    if (parent === current) return true;
    current = parent;
  }
  return false;
}

function error(code: GatewayError["code"], message: string, details?: Record<string, unknown>): EditorMenuOperationCommandResult {
  return { status: "error", error: { code, message, ...(details ? { details } : {}) } };
}
