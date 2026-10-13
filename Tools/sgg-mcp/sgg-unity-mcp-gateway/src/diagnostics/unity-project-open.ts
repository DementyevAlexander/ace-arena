import { spawn as nodeSpawn } from "node:child_process";
import fs from "node:fs";
import path from "node:path";

import { CanonicalPathService } from "../paths/canonical-path.js";
import type { PublicProjectRecord } from "../registry/project-registry.js";
import type { UnityProcessListResult, UnityProcessSnapshot } from "./unity-processes.js";

const MAX_PROJECT_MATCHES = 10;
const MAX_PROCESS_MATCHES = 10;
const MAX_EDITOR_MATCHES = 20;

const canonicalPaths = new CanonicalPathService();

export type UnityProjectOpenArgs = {
  project_selector?: string;
  canonical_project_root?: string;
  unity_version?: string;
  dry_run?: boolean;
  confirm_open?: boolean;
};

export type UnityProjectOpenStatusValue = "open_with_mcp_heartbeat" | "open_without_mcp_heartbeat" | "stale_or_offline" | "not_detected" | "ambiguous" | "invalid_project_root";

export type UnityProjectOpenStatusResult = {
  schema_version: 1;
  gateway_local: true;
  read_only: true;
  launch_unity_editor_attempted: false;
  requested_project_selector: string;
  requested_canonical_project_root: string | null;
  status: UnityProjectOpenStatusValue;
  mcp_heartbeat_match_count: number;
  mcp_heartbeat_matches: UnityProjectHeartbeatEvidence[];
  unity_editor_process_match_count: number;
  unity_editor_process_matches: UnityProcessSnapshot[];
  process_scan_supported: boolean;
  process_errors: string[];
  resolution_hint: string;
  errors: string[];
  next_valid_actions: string[];
};

export type UnityProjectOpenResult = {
  schema_version: 1;
  gateway_local: true;
  dry_run: boolean;
  confirm_open: boolean;
  launch_unity_editor_attempted: boolean;
  arbitrary_editor_executable_accepted: false;
  closes_existing_editors: false;
  kills_unity_processes: false;
  deletes_library_or_cache: false;
  requested_project_selector: string;
  requested_canonical_project_root: string | null;
  project_path_for_unity: string | null;
  host_project_path_checked: string | null;
  unity_version_selector: string | null;
  status: "refused" | "would_launch" | "launched" | "error";
  refusal_reason: string | null;
  open_status_before: UnityProjectOpenStatusResult;
  selected_editor: UnityEditorEvidence | null;
  editor_discovery_status: string;
  editor_candidates_count: number;
  editor_candidates: UnityEditorEvidence[];
  launched_pid?: number;
  launch_error?: string;
  next_valid_actions: string[];
};

export type UnityProjectOpenRuntime = {
  status: (args: UnityProjectOpenArgs, context: UnityProjectOpenRuntimeContext) => UnityProjectOpenStatusResult;
  open: (args: UnityProjectOpenArgs, context: UnityProjectOpenRuntimeContext) => UnityProjectOpenResult;
};

export type UnityProjectOpenRuntimeContext = {
  projectSelector?: string | undefined;
  projects?: PublicProjectRecord[] | undefined;
  processStatus?: UnityProcessListResult | undefined;
  editorDiscovery?: UnityEditorDiscoveryRuntime | undefined;
  now?: (() => Date) | undefined;
};

export type UnityEditorDiscoveryRuntime = {
  status: (args?: { version?: string }) => Record<string, unknown>;
};

type UnityProjectOpenRuntimeOptions = {
  platform?: NodeJS.Platform;
  fs?: Pick<typeof fs, "existsSync" | "statSync">;
  spawn?: (file: string, args: string[], options: { cwd?: string; detached: boolean; stdio: "ignore"; windowsHide: boolean }) => SpawnedProcessLike;
  wslMountRoot?: string;
};

type SpawnedProcessLike = {
  pid?: number | undefined;
  unref?: (() => void) | undefined;
};

type UnityProjectHeartbeatEvidence = {
  project: string;
  display_name?: string | undefined;
  instance_id: string;
  canonical_project_root: string | null;
  unity_pid: number;
  unity_version: string;
  status: string;
  stale: boolean;
  last_heartbeat: string;
  last_heartbeat_age_ms: number | null;
};

type UnityEditorEvidence = {
  version: string | null;
  executable_path: string;
  source: string | null;
  exists: boolean | null;
};

type NormalizedRequest = {
  selector: string;
  canonicalRoot: string | null;
  errors: string[];
};

type ProjectRootInspection = {
  canonicalRoot: string;
  hostProjectPath: string;
  unityProjectPath: string;
  exists: boolean;
  isDirectory: boolean;
  hasProjectSettings: boolean;
  error: string | null;
};

export function createUnityProjectOpenRuntime(options: UnityProjectOpenRuntimeOptions = {}): UnityProjectOpenRuntime {
  return {
    status: (args, context) => inspectUnityProjectOpenStatus(args, context, options),
    open: (args, context) => openUnityProject(args, context, options),
  };
}

export function inspectUnityProjectOpenStatus(args: UnityProjectOpenArgs, context: UnityProjectOpenRuntimeContext, options: UnityProjectOpenRuntimeOptions = {}): UnityProjectOpenStatusResult {
  const request = normalizeRequest(args, context.projectSelector);
  const projects = context.projects ?? [];
  const processStatus = context.processStatus ?? emptyProcessStatus();
  const now = context.now?.() ?? new Date();
  const projectMatches = projects.filter((project) => projectMatchesRequest(project, request));
  const liveMatches = projectMatches.filter((project) => !project.stale);
  const staleMatches = projectMatches.filter((project) => project.stale);
  const processMatches = processStatus.processes.filter((process) => processMatchesRequest(process, request, projectMatches));
  const status = classifyStatus(request, liveMatches, staleMatches, processMatches);
  const heartbeatMatches = projectMatches.slice(0, MAX_PROJECT_MATCHES).map((project) => heartbeatEvidence(project, now));

  return {
    schema_version: 1,
    gateway_local: true,
    read_only: true,
    launch_unity_editor_attempted: false,
    requested_project_selector: request.selector,
    requested_canonical_project_root: request.canonicalRoot,
    status,
    mcp_heartbeat_match_count: projectMatches.length,
    mcp_heartbeat_matches: heartbeatMatches,
    unity_editor_process_match_count: processMatches.length,
    unity_editor_process_matches: processMatches.slice(0, MAX_PROCESS_MATCHES),
    process_scan_supported: processStatus.supported,
    process_errors: processStatus.errors,
    resolution_hint: statusHint(status),
    errors: request.errors,
    next_valid_actions: statusNextActions(status, request, processStatus.supported),
  };
}

export function openUnityProject(args: UnityProjectOpenArgs, context: UnityProjectOpenRuntimeContext, options: UnityProjectOpenRuntimeOptions = {}): UnityProjectOpenResult {
  const dryRun = args.dry_run !== false;
  const confirmOpen = args.confirm_open === true;
  const openStatus = inspectUnityProjectOpenStatus(args, context, options);
  const projectRoot = projectRootForOpen(openStatus);
  const unityVersionSelector = typeof args.unity_version === "string" && args.unity_version.trim().length > 0 ? args.unity_version.trim() : unityVersionFromStatus(openStatus);
  const projectInspection = projectRoot ? inspectProjectRoot(projectRoot, options) : null;
  const discovery = context.editorDiscovery?.status(unityVersionSelector ? { version: unityVersionSelector } : undefined) ?? { status: "not_configured", editors: [] };
  const editors = editorCandidates(discovery);
  const selectedEditor = selectEditor(editors, unityVersionSelector);
  const refusalReason = openRefusalReason(openStatus, projectInspection, selectedEditor, dryRun, confirmOpen);
  const base = openResultBase(args, openStatus, projectInspection, unityVersionSelector, discovery, editors, selectedEditor, dryRun, confirmOpen);

  if (refusalReason) {
    return {
      ...base,
      status: "refused",
      refusal_reason: refusalReason,
      launch_unity_editor_attempted: false,
      next_valid_actions: openRefusalNextActions(refusalReason, openStatus),
    };
  }

  if (dryRun) {
    return {
      ...base,
      status: "would_launch",
      refusal_reason: null,
      launch_unity_editor_attempted: false,
      next_valid_actions: ["call_unity.project.open_with_dry_run_false_after_approval", "call_sgg_unity_projects_after_launch", "wait_for_fresh_mcp_heartbeat"],
    };
  }

  try {
    const executable = hostExecutablePath(selectedEditor?.executable_path ?? "", options);
    const spawned = (options.spawn ?? nodeSpawn)(executable, ["-projectPath", projectInspection?.unityProjectPath ?? ""], {
      ...(projectInspection?.hostProjectPath ? { cwd: projectInspection.hostProjectPath } : {}),
      detached: true,
      stdio: "ignore",
      windowsHide: true,
    });
    spawned.unref?.();
    return {
      ...base,
      status: "launched",
      refusal_reason: null,
      launch_unity_editor_attempted: true,
      ...(typeof spawned.pid === "number" ? { launched_pid: spawned.pid } : {}),
      next_valid_actions: ["call_sgg_unity_projects_after_launch", "wait_for_fresh_mcp_heartbeat", "use_returned_project_selector_before_mcp_commands"],
    };
  } catch (error) {
    return {
      ...base,
      status: "error",
      refusal_reason: null,
      launch_unity_editor_attempted: true,
      launch_error: error instanceof Error ? error.message.slice(0, 2000) : String(error).slice(0, 2000),
      next_valid_actions: ["unity.editor.installations.list", "call_unity.project.open_status", "diagnostics.report_problem"],
    };
  }
}

function normalizeRequest(args: UnityProjectOpenArgs, projectSelector: string | undefined): NormalizedRequest {
  const selector = (typeof args.project_selector === "string" && args.project_selector.trim().length > 0 ? args.project_selector : projectSelector ?? "").trim();
  const rootCandidate = typeof args.canonical_project_root === "string" && args.canonical_project_root.trim().length > 0
    ? args.canonical_project_root.trim()
    : looksLikeProjectRoot(selector) ? selector : undefined;
  const errors: string[] = [];
  let canonicalRoot: string | null = null;
  if (rootCandidate) {
    try {
      canonicalRoot = canonicalPaths.normalizeProjectRoot(rootCandidate);
    } catch {
      errors.push("invalid_canonical_project_root");
    }
  }

  return {
    selector: selector || "<missing_project_selector>",
    canonicalRoot,
    errors,
  };
}

function projectMatchesRequest(project: PublicProjectRecord, request: NormalizedRequest): boolean {
  if (request.canonicalRoot) {
    return projectCanonicalRoots(project).has(request.canonicalRoot);
  }

  if (project.project === request.selector || project.display_name === request.selector || project.instance_id === request.selector) {
    return true;
  }

  return (project.aliases ?? []).some((alias) => alias.host_path === request.selector);
}

function processMatchesRequest(process: UnityProcessSnapshot, request: NormalizedRequest, projects: PublicProjectRecord[]): boolean {
  if (process.role !== "unity_editor") {
    return false;
  }

  if (request.canonicalRoot) {
    return process.canonical_project_root === request.canonicalRoot;
  }

  const matchedProjects = new Map(projects.map((project) => [project.instance_id, project]));
  return process.heartbeat_matches.some((match) => {
    const project = matchedProjects.get(match.instance_id);
    if (!project) return false;
    if (!process.canonical_project_root) return true;
    const roots = projectCanonicalRoots(project);
    return roots.size === 0 || roots.has(process.canonical_project_root);
  });
}

function classifyStatus(request: NormalizedRequest, liveMatches: PublicProjectRecord[], staleMatches: PublicProjectRecord[], processMatches: UnityProcessSnapshot[]): UnityProjectOpenStatusValue {
  if (request.errors.length > 0) {
    return "invalid_project_root";
  }
  const liveProjectIdentities = projectIdentityCount(liveMatches);
  const staleProjectIdentities = projectIdentityCount(staleMatches);
  if (liveProjectIdentities > 1) {
    return "ambiguous";
  }
  if (liveProjectIdentities === 1) {
    return "open_with_mcp_heartbeat";
  }
  if (processMatches.length > 0) {
    return "open_without_mcp_heartbeat";
  }
  if (staleProjectIdentities > 1) {
    return "ambiguous";
  }
  if (staleProjectIdentities === 1) {
    return "stale_or_offline";
  }
  return "not_detected";
}

function projectIdentityCount(projects: PublicProjectRecord[]): number {
  const identities = new Set<string>();
  for (const project of projects) {
    const roots = projectCanonicalRoots(project);
    if (roots.size > 0) {
      for (const root of roots) identities.add(`root:${root}`);
    } else {
      identities.add(`instance:${project.instance_id}`);
    }
  }
  return identities.size;
}

function heartbeatEvidence(project: PublicProjectRecord, now: Date): UnityProjectHeartbeatEvidence {
  const heartbeatMs = Date.parse(project.last_heartbeat);
  return {
    project: project.project,
    ...(project.display_name ? { display_name: project.display_name } : {}),
    instance_id: project.instance_id,
    canonical_project_root: project.canonical_project_root ?? null,
    unity_pid: project.unity_pid,
    unity_version: project.unity_version,
    status: project.status,
    stale: project.stale,
    last_heartbeat: project.last_heartbeat,
    last_heartbeat_age_ms: Number.isFinite(heartbeatMs) ? Math.max(0, now.getTime() - heartbeatMs) : null,
  };
}

function statusHint(status: UnityProjectOpenStatusValue): string {
  switch (status) {
    case "open_with_mcp_heartbeat":
      return "The requested Unity project is already open with a live MCP heartbeat; use the returned instance_id/project selector for MCP commands.";
    case "open_without_mcp_heartbeat":
      return "A Unity Editor process for this project appears open, but no live MCP heartbeat is available; wait for heartbeat recovery or inspect the Editor/Gateway setup before launching another Editor.";
    case "stale_or_offline":
      return "Only stale/offline MCP heartbeat evidence exists for this project; it can be opened after confirming no active Editor process owns the same project.";
    case "ambiguous":
      return "The project selector is ambiguous; choose a unique instance_id or canonical_project_root before opening or using MCP commands.";
    case "invalid_project_root":
      return "The provided project root could not be normalized; provide a valid canonical_project_root or known project selector.";
    case "not_detected":
      return "No open Editor or MCP heartbeat was detected for this project selector.";
  }
}

function statusNextActions(status: UnityProjectOpenStatusValue, request: NormalizedRequest, processScanSupported: boolean): string[] {
  const actions = ["sgg_unity_projects"];
  if (status === "open_with_mcp_heartbeat") {
    actions.push("use_existing_mcp_project", "call_compile.status_with_returned_project_selector");
  } else if (status === "open_without_mcp_heartbeat") {
    actions.push("wait_for_heartbeat", "diagnostics.unity_startup.status", "diagnostics.unity_modal.status");
  } else if (status === "stale_or_offline" || status === "not_detected") {
    actions.push("dry_run_unity.project.open", "unity.editor.installations.list");
  } else if (status === "ambiguous") {
    actions.push("choose_unique_project", "retry_with_instance_id_or_canonical_project_root");
  } else if (status === "invalid_project_root") {
    actions.push("retry_with_valid_canonical_project_root");
  }
  if (!processScanSupported) {
    actions.push("process_scan_unavailable_inspect_editor_manually");
  }
  if (!request.canonicalRoot && (status === "not_detected" || status === "invalid_project_root")) {
    actions.push("provide_canonical_project_root");
  }
  return uniqueStrings(actions);
}

function projectRootForOpen(status: UnityProjectOpenStatusResult): string | null {
  if (status.requested_canonical_project_root) {
    return status.requested_canonical_project_root;
  }
  const roots = uniqueStrings(status.mcp_heartbeat_matches.map((match) => match.canonical_project_root).filter((root): root is string => typeof root === "string" && root.length > 0));
  return roots.length === 1 ? roots[0] ?? null : null;
}

function unityVersionFromStatus(status: UnityProjectOpenStatusResult): string | null {
  const versions = uniqueStrings(status.mcp_heartbeat_matches.map((match) => match.unity_version).filter((version) => version.length > 0));
  return versions.length === 1 ? versions[0] ?? null : null;
}

function inspectProjectRoot(canonicalRoot: string, options: UnityProjectOpenRuntimeOptions): ProjectRootInspection {
  const fsImpl = options.fs ?? fs;
  const hostProjectPath = hostProjectPathForCanonicalRoot(canonicalRoot, options);
  const unityProjectPath = unityProjectPathForCanonicalRoot(canonicalRoot, options);
  try {
    const stat = fsImpl.statSync(hostProjectPath);
    const hasProjectSettings = fsImpl.existsSync(pathForHost(hostProjectPath, "ProjectSettings"));
    return {
      canonicalRoot,
      hostProjectPath,
      unityProjectPath,
      exists: true,
      isDirectory: stat.isDirectory(),
      hasProjectSettings,
      error: null,
    };
  } catch (error) {
    return {
      canonicalRoot,
      hostProjectPath,
      unityProjectPath,
      exists: false,
      isDirectory: false,
      hasProjectSettings: false,
      error: error instanceof Error ? error.message.slice(0, 1000) : String(error).slice(0, 1000),
    };
  }
}

function openRefusalReason(status: UnityProjectOpenStatusResult, project: ProjectRootInspection | null, editor: UnityEditorEvidence | null, dryRun: boolean, confirmOpen: boolean): string | null {
  if (status.status === "invalid_project_root") {
    return "invalid_project_root";
  }
  if (status.status === "ambiguous") {
    return "ambiguous_project_selector";
  }
  if (status.status === "open_with_mcp_heartbeat") {
    return "already_open_with_mcp_heartbeat";
  }
  if (status.status === "open_without_mcp_heartbeat") {
    return "already_open_without_mcp_heartbeat";
  }
  if (!project) {
    return "canonical_project_root_required";
  }
  if (!project.exists) {
    return "project_root_not_found";
  }
  if (!project.isDirectory) {
    return "project_root_not_directory";
  }
  if (!project.hasProjectSettings) {
    return "project_root_not_unity_project";
  }
  if (!editor) {
    return "unity_editor_not_found";
  }
  if (!dryRun && !confirmOpen) {
    return "confirm_open_required";
  }
  return null;
}

function openResultBase(args: UnityProjectOpenArgs, status: UnityProjectOpenStatusResult, project: ProjectRootInspection | null, unityVersionSelector: string | null, discovery: Record<string, unknown>, editors: UnityEditorEvidence[], selectedEditor: UnityEditorEvidence | null, dryRun: boolean, confirmOpen: boolean): Omit<UnityProjectOpenResult, "status" | "refusal_reason" | "launch_unity_editor_attempted" | "next_valid_actions"> {
  return {
    schema_version: 1,
    gateway_local: true,
    dry_run: dryRun,
    confirm_open: confirmOpen,
    arbitrary_editor_executable_accepted: false,
    closes_existing_editors: false,
    kills_unity_processes: false,
    deletes_library_or_cache: false,
    requested_project_selector: status.requested_project_selector,
    requested_canonical_project_root: status.requested_canonical_project_root,
    project_path_for_unity: project?.unityProjectPath ?? null,
    host_project_path_checked: project?.hostProjectPath ?? null,
    unity_version_selector: unityVersionSelector,
    open_status_before: status,
    selected_editor: selectedEditor,
    editor_discovery_status: typeof discovery.status === "string" ? discovery.status : editors.length > 0 ? "ok" : "not_found",
    editor_candidates_count: editors.length,
    editor_candidates: editors.slice(0, MAX_EDITOR_MATCHES),
  };
}

function openRefusalNextActions(reason: string, status: UnityProjectOpenStatusResult): string[] {
  const actions = ["unity.project.open_status"];
  if (reason === "already_open_with_mcp_heartbeat") {
    actions.push("use_existing_mcp_project", "call_compile.status_with_returned_project_selector");
  } else if (reason === "already_open_without_mcp_heartbeat") {
    actions.push("wait_for_heartbeat", "diagnostics.unity_startup.status", "diagnostics.unity_modal.status");
  } else if (reason === "confirm_open_required") {
    actions.push("retry_with_confirm_open_true_after_operator_approval");
  } else if (reason === "unity_editor_not_found") {
    actions.push("unity.editor.installations.list", "set_SGG_UNITY_EDITOR_ROOTS_if_missing");
  } else if (reason === "canonical_project_root_required" || reason === "invalid_project_root") {
    actions.push("retry_with_valid_canonical_project_root");
  } else if (reason === "ambiguous_project_selector") {
    actions.push("choose_unique_project", "retry_with_instance_id_or_canonical_project_root");
  } else {
    actions.push("inspect_project_root", "diagnostics.report_problem");
  }
  if (status.status === "stale_or_offline") {
    actions.push("verify_no_active_editor_before_launch");
  }
  return uniqueStrings(actions);
}

function editorCandidates(discovery: Record<string, unknown>): UnityEditorEvidence[] {
  const rows = Array.isArray(discovery.editors) ? discovery.editors : [];
  return rows
    .map((row): UnityEditorEvidence | undefined => {
      if (typeof row !== "object" || row === null) {
        return undefined;
      }
      const record = row as Record<string, unknown>;
      const executablePath = typeof record.executable_path === "string" ? record.executable_path : undefined;
      if (!executablePath) {
        return undefined;
      }
      return {
        version: typeof record.version === "string" ? record.version : null,
        executable_path: executablePath,
        source: typeof record.source === "string" ? record.source : null,
        exists: typeof record.exists === "boolean" ? record.exists : null,
      };
    })
    .filter((editor): editor is UnityEditorEvidence => editor !== undefined);
}

function selectEditor(editors: UnityEditorEvidence[], versionSelector: string | null): UnityEditorEvidence | null {
  const sorted = [...editors].sort((left, right) => String(right.version ?? "").localeCompare(String(left.version ?? "")));
  if (!versionSelector) {
    return sorted[0] ?? null;
  }
  return sorted.find((editor) => editor.version === versionSelector)
    ?? sorted.find((editor) => typeof editor.version === "string" && editor.version.startsWith(`${versionSelector}.`))
    ?? sorted.find((editor) => typeof editor.version === "string" && editor.version.startsWith(versionSelector))
    ?? null;
}

function emptyProcessStatus(): UnityProcessListResult {
  return {
    schema_version: 1,
    supported: false,
    process_count: 0,
    scan_truncated: false,
    processes: [],
    errors: ["process_status_not_provided"],
    next_valid_actions: ["unity.process.list"],
  };
}

function projectCanonicalRoots(project: PublicProjectRecord): Set<string> {
  const roots = new Set<string>();
  if (project.canonical_project_root) {
    roots.add(project.canonical_project_root);
  }
  for (const alias of project.aliases ?? []) {
    roots.add(alias.canonical_path);
  }
  return roots;
}

function looksLikeProjectRoot(value: string): boolean {
  return /^[A-Za-z]:[\\/]/.test(value) || value.startsWith("/") || value.includes("\\");
}

function hostProjectPathForCanonicalRoot(canonicalRoot: string, options: UnityProjectOpenRuntimeOptions): string {
  if ((options.platform ?? process.platform) !== "win32") {
    const wslPath = canonicalDriveRootToWsl(canonicalRoot, options.wslMountRoot ?? "/mnt");
    if (wslPath) {
      return wslPath;
    }
  }
  return canonicalRoot;
}

function unityProjectPathForCanonicalRoot(canonicalRoot: string, _options: UnityProjectOpenRuntimeOptions): string {
  return canonicalDriveRootToWindows(canonicalRoot) ?? canonicalRoot;
}

function hostExecutablePath(executablePath: string, options: UnityProjectOpenRuntimeOptions): string {
  if ((options.platform ?? process.platform) !== "win32") {
    const canonical = normalizeDriveRoot(executablePath);
    const wslPath = canonical ? canonicalDriveRootToWsl(canonical, options.wslMountRoot ?? "/mnt") : null;
    if (wslPath) {
      return wslPath;
    }
  }
  return executablePath;
}

function normalizeDriveRoot(value: string): string | null {
  const normalized = value.replaceAll("\\", "/");
  const match = /^([A-Za-z]):\/(.*)$/.exec(normalized);
  if (!match) {
    return null;
  }
  const drive = match[1]?.toLowerCase() ?? "";
  const rest = match[2] ?? "";
  return `${drive}:/${rest}`.replace(/\/+$/g, "");
}

function canonicalDriveRootToWindows(canonicalRoot: string): string | null {
  const match = /^([a-z]):\/(.*)$/i.exec(canonicalRoot);
  if (!match) {
    return null;
  }
  const drive = (match[1] ?? "").toUpperCase();
  const rest = match[2] ?? "";
  return `${drive}:\\${rest.replaceAll("/", "\\")}`.replace(/\\+$/g, "");
}

function canonicalDriveRootToWsl(canonicalRoot: string, mountRoot: string): string | null {
  const match = /^([a-z]):\/(.*)$/i.exec(canonicalRoot);
  if (!match) {
    return null;
  }
  const drive = (match[1] ?? "").toLowerCase();
  const rest = match[2] ?? "";
  return `${mountRoot.replace(/\/+$/g, "")}/${drive}${rest ? `/${rest}` : ""}`;
}

function pathForHost(root: string, child: string): string {
  return root.replaceAll("\\", "/").startsWith("/") ? path.posix.join(root.replaceAll("\\", "/"), child) : path.join(root, child);
}

function uniqueStrings(values: string[]): string[] {
  return [...new Set(values)];
}
