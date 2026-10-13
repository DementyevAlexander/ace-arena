import { execFileSync as nodeExecFileSync } from "node:child_process";

import { CanonicalPathService } from "../paths/canonical-path.js";
import type {
  PublicProjectRecord,
  RegistryProcessGenerationIdentity,
  RegistryProcessGenerationInspector,
  RegistryProcessGenerationObservation,
  RegistryProcessState,
} from "../registry/project-registry.js";

const WINDOWS_TOOL_TIMEOUT_MS = 5000;
const MAX_COMMAND_LINE_CHARS = 2000;
const PROCESS_GENERATION_SCAN_TTL_MS = 1000;
const MAX_PROCESS_GENERATION_PIDS = 1024;
const UNITY_PROCESS_NAMES = new Set(["unity.exe", "unity"]);
const OBSERVED_PROCESS_NAMES = new Set(["unity.exe", "unity", "unity.licensing.client.exe", "unity hub.exe", "unity hub"]);

const canonicalPaths = new CanonicalPathService();

export type ExecFileSyncLike = (file: string, args: string[], options: { encoding: "utf8"; timeout: number; windowsHide: boolean; stdio: "pipe" }) => string;

export type UnityProcessOptions = {
  execFileSync?: ExecFileSyncLike;
  platform?: NodeJS.Platform;
  now?: () => Date;
  pidState?: (pid: number) => RegistryProcessState;
  toolTimeoutMs?: number;
};

export type UnityProcessScanOptions = {
  scan_budget_ms?: number;
};

export type UnityProcessRole = "unity_editor" | "unity_asset_import_worker" | "unity_licensing_client" | "unity_hub" | "other_unity_process";

export type UnityProcessSnapshot = {
  pid: number;
  parent_pid: number | null;
  process_name: string;
  role: UnityProcessRole;
  process_start_utc: string | null;
  executable_path: string | null;
  command_line_redacted: string | null;
  parsed_project_path: string | null;
  canonical_project_root: string | null;
  heartbeat_match_count: number;
  heartbeat_matches: Array<{
    project: string;
    instance_id: string;
    status: string;
    stale: boolean;
    unity_pid: number;
    canonical_project_root: string | null;
    match_reason: "pid" | "canonical_project_root";
  }>;
  current_project_match: boolean;
  eligible_for_terminate: boolean;
  terminate_refusal_reason: string | null;
};

export type UnityProcessListResult = {
  schema_version: 1;
  supported: boolean;
  process_count: number;
  scan_truncated: boolean;
  processes: UnityProcessSnapshot[];
  errors: string[];
  next_valid_actions: string[];
};

export type UnityProcessTerminateArgs = {
  pid?: number;
  canonical_project_root?: string;
  confirm_terminate?: boolean;
  dry_run?: boolean;
  force?: boolean;
};

export type UnityProcessTerminateResult = {
  schema_version: 1;
  dry_run: boolean;
  force: boolean;
  confirm_terminate: boolean;
  target_pid: number | null;
  requested_canonical_project_root: string | null;
  status: "refused" | "would_terminate" | "terminated" | "error";
  terminated_count: number;
  refusal_reason: string | null;
  taskkill_error?: string;
  target_before: UnityProcessSnapshot | null;
  active_before: UnityProcessListResult;
  active_after?: UnityProcessListResult;
  next_valid_actions: string[];
};

export type UnityProcessRuntime = {
  status: (projects: PublicProjectRecord[], currentProject?: PublicProjectRecord, scanOptions?: UnityProcessScanOptions) => UnityProcessListResult;
  terminate: (args: UnityProcessTerminateArgs, projects: PublicProjectRecord[], currentProject?: PublicProjectRecord) => UnityProcessTerminateResult;
};

type RawUnityProcess = {
  pid: number;
  parentPid: number | null;
  name: string;
  executablePath: string | null;
  commandLine: string | null;
  processStartUtc: string | null;
};

type WindowsToolResult =
  | { status: "ok"; executable: string; output: string }
  | { status: "error"; executable?: string; error: string; output?: string };

export function createUnityProcessRuntime(options: UnityProcessOptions = {}): UnityProcessRuntime {
  return {
    status: (projects, currentProject, scanOptions = {}) => inspectUnityProcesses(projects, currentProject, {
      ...options,
      ...(scanOptions.scan_budget_ms !== undefined ? { toolTimeoutMs: scanOptions.scan_budget_ms } : {}),
    }),
    terminate: (args, projects, currentProject) => terminateUnityProcess(args, projects, currentProject, options),
  };
}

export function createUnityProcessGenerationInspector(options: UnityProcessOptions = {}): RegistryProcessGenerationInspector {
  let cached: { capturedAtMs: number; identityKeys: ReadonlySet<string>; scan: ReturnType<typeof scanProcessGenerations> } | undefined;
  return (identities) => {
    if (identities.length === 0) return [];
    if ((options.platform ?? process.platform) !== "win32") return uncertainProcessGenerations(identities);
    const pidState = options.pidState ?? localProcessState;
    const immediateStates = identities.map((identity) => pidState(identity.unity_pid));
    const inspectable = identities.filter((_, index) => immediateStates[index] === "live");
    if (inspectable.length === 0) {
      return identities.map((identity, index) => ({ ...identity, state: immediateStates[index] ?? "uncertain" }));
    }
    const nowMs = (options.now ?? (() => new Date()))().getTime();
    const identityKeys = new Set(inspectable.map(processGenerationCacheKey));
    if (!cached || nowMs - cached.capturedAtMs > PROCESS_GENERATION_SCAN_TTL_MS || [...identityKeys].some((key) => !cached!.identityKeys.has(key))) {
      const pids = [...new Set(inspectable.map((identity) => identity.unity_pid))].sort((left, right) => left - right);
      const scan = scanProcessGenerations(pids, options);
      cached = { capturedAtMs: (options.now ?? (() => new Date()))().getTime(), identityKeys, scan };
    }
    return identities.map((identity, index) => {
      const immediateState = immediateStates[index] ?? "uncertain";
      const state = immediateState !== "live" || !cached!.scan.supported || cached!.scan.errors.length > 0
        ? immediateState === "dead" ? "dead" : "uncertain"
        : processGenerationState(identity, cached!.scan.processes);
      return { ...identity, state };
    });
  };
}

export function inspectUnityProcesses(projects: PublicProjectRecord[] = [], currentProject?: PublicProjectRecord, options: UnityProcessOptions = {}): UnityProcessListResult {
  if ((options.platform ?? process.platform) !== "win32") {
    return {
      schema_version: 1,
      supported: false,
      process_count: 0,
      scan_truncated: false,
      processes: [],
      errors: ["unsupported_platform"],
      next_valid_actions: ["inspect_unity_editor_manually", "diagnostics.report_problem"],
    };
  }

  const scan = scanUnityProcesses(options);
  const observedProcesses = scan.processes
    .filter((process) => OBSERVED_PROCESS_NAMES.has(process.name.toLowerCase()))
    .map((process) => processSnapshot(process, projects, currentProject))
    .sort((left, right) => left.process_name.localeCompare(right.process_name) || left.pid - right.pid);
  const processes = observedProcesses.slice(0, 50);
  const terminatable = processes.filter((process) => process.eligible_for_terminate).length;

  return {
    schema_version: 1,
    supported: scan.supported,
    process_count: processes.length,
    scan_truncated: observedProcesses.length > processes.length,
    processes,
    errors: scan.errors,
    next_valid_actions: uniqueStrings([
      "sgg_unity_projects",
      "diagnostics.unity_modal.status",
      "diagnostics.unity_startup.status",
      ...(terminatable > 0 ? ["dry_run_unity.process.terminate"] : []),
      "diagnostics.report_problem",
    ]),
  };
}

export function terminateUnityProcess(args: UnityProcessTerminateArgs, projects: PublicProjectRecord[] = [], currentProject?: PublicProjectRecord, options: UnityProcessOptions = {}): UnityProcessTerminateResult {
  const dryRun = args.dry_run !== false;
  const force = args.force === true;
  const confirmTerminate = args.confirm_terminate === true;
  const pid = Number.isInteger(args.pid) && (args.pid ?? 0) > 0 ? args.pid ?? null : null;
  const requestedRoot = normalizeProjectRootOrNull(args.canonical_project_root);
  const activeBefore = inspectUnityProcesses(projects, currentProject, options);
  const targetBefore = pid === null ? null : activeBefore.processes.find((process) => process.pid === pid) ?? null;
  const refusalReason = terminateRefusalReason(pid, requestedRoot, confirmTerminate, targetBefore);

  if (refusalReason) {
    return {
      schema_version: 1,
      dry_run: dryRun,
      force,
      confirm_terminate: confirmTerminate,
      target_pid: pid,
      requested_canonical_project_root: requestedRoot,
      status: "refused",
      terminated_count: 0,
      refusal_reason: refusalReason,
      target_before: targetBefore,
      active_before: activeBefore,
      next_valid_actions: ["unity.process.list", "diagnostics.unity_startup.status", "diagnostics.report_problem"],
    };
  }

  if (dryRun) {
    return {
      schema_version: 1,
      dry_run: dryRun,
      force,
      confirm_terminate: confirmTerminate,
      target_pid: pid,
      requested_canonical_project_root: requestedRoot,
      status: "would_terminate",
      terminated_count: 0,
      refusal_reason: null,
      target_before: targetBefore,
      active_before: activeBefore,
      next_valid_actions: ["unity.process.terminate", "unity.process.list"],
    };
  }

  const taskkill = runWindowsTool("taskkill.exe", ["/PID", String(pid), ...(force ? ["/F"] : [])], options);
  if (taskkill.status === "error") {
    return {
      schema_version: 1,
      dry_run: dryRun,
      force,
      confirm_terminate: confirmTerminate,
      target_pid: pid,
      requested_canonical_project_root: requestedRoot,
      status: "error",
      terminated_count: 0,
      refusal_reason: null,
      taskkill_error: taskkill.error,
      target_before: targetBefore,
      active_before: activeBefore,
      next_valid_actions: ["unity.process.list", "diagnostics.report_problem"],
    };
  }

  return {
    schema_version: 1,
    dry_run: dryRun,
    force,
    confirm_terminate: confirmTerminate,
    target_pid: pid,
    requested_canonical_project_root: requestedRoot,
    status: "terminated",
    terminated_count: 1,
    refusal_reason: null,
    target_before: targetBefore,
    active_before: activeBefore,
    active_after: inspectUnityProcesses(projects, currentProject, options),
    next_valid_actions: ["unity.process.list", "sgg_unity_projects"],
  };
}

function scanUnityProcesses(options: UnityProcessOptions): { supported: boolean; processes: RawUnityProcess[]; errors: string[] } {
  const script = [
    "$ErrorActionPreference='Stop';",
    "$names=@('Unity.exe','Unity','Unity.Licensing.Client.exe','Unity Hub.exe','Unity Hub');",
    "$rows=@(Get-CimInstance Win32_Process | Where-Object { $names -contains $_.Name } | Select-Object ProcessId,ParentProcessId,Name,ExecutablePath,CommandLine,@{Name='ProcessStartUtc';Expression={try{(Get-Process -Id $_.ProcessId -ErrorAction Stop).StartTime.ToUniversalTime().ToString('o')}catch{''}}});",
    "@{schema_version=1;query_succeeded=$true;processes=$rows} | ConvertTo-Json -Depth 4 -Compress",
  ].join(" ");
  const powershell = runWindowsTool("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script], options);
  if (powershell.status === "error") {
    return { supported: false, processes: [], errors: [`powershell_failed: ${powershell.error}`] };
  }

  const processes = parsePowerShellProcessJson(powershell.output);
  if (!processes) {
    return { supported: false, processes: [], errors: ["powershell_invalid_json"] };
  }

  return { supported: true, processes, errors: [] };
}

function scanProcessGenerations(pids: number[], options: UnityProcessOptions): { supported: boolean; processes: RawUnityProcess[]; errors: string[] } {
  if (pids.length === 0) return { supported: true, processes: [], errors: [] };
  if (pids.length > MAX_PROCESS_GENERATION_PIDS) return { supported: false, processes: [], errors: ["process_generation_pid_limit_exceeded"] };
  const script = [
    "$ErrorActionPreference='Stop';",
    `$pids=@(${pids.join(",")});`,
    "$rows=@($pids | ForEach-Object { $processId=[int]$_; $process=$null; try { $process=[System.Diagnostics.Process]::GetProcessById($processId); $name='Unity'; $start=''; try { $name=$process.ProcessName } catch {} try { $start=$process.StartTime.ToUniversalTime().ToString('o') } catch {} [pscustomobject]@{ProcessId=$processId;Name=$name;ProcessStartUtc=$start} } catch [System.ArgumentException] {} catch { [pscustomobject]@{ProcessId=$processId;Name='Unity';ProcessStartUtc=''} } finally { if ($null -ne $process) { $process.Dispose() } } });",
    "@{schema_version=1;query_succeeded=$true;processes=$rows} | ConvertTo-Json -Depth 4 -Compress",
  ].join(" ");
  const powershell = runWindowsTool("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", script], options);
  if (powershell.status === "error") {
    return { supported: false, processes: [], errors: [`powershell_failed: ${powershell.error}`] };
  }
  const processes = parsePowerShellProcessJson(powershell.output);
  return processes
    ? { supported: true, processes, errors: [] }
    : { supported: false, processes: [], errors: ["powershell_invalid_json"] };
}

function processSnapshot(process: RawUnityProcess, projects: PublicProjectRecord[], currentProject?: PublicProjectRecord): UnityProcessSnapshot {
  const parsedProjectPath = parseProjectPath(process.commandLine ?? "");
  const canonicalProjectRoot = normalizeProjectRootOrNull(parsedProjectPath);
  const heartbeatMatches = heartbeatMatchesForProcess(process.pid, canonicalProjectRoot, projects);
  const currentProjectMatch = currentProject ? heartbeatRecordMatchesProcess(currentProject, process.pid, canonicalProjectRoot) : false;
  const role = unityProcessRole(process.name, process.commandLine);
  const eligibleForTerminate = role === "unity_editor" && canonicalProjectRoot !== null && currentProjectMatch;
  const terminateRefusalReason = eligibleForTerminate
    ? null
    : role !== "unity_editor"
      ? "unsupported_process_role"
      : canonicalProjectRoot === null
        ? "missing_project_path"
        : "current_project_mismatch";

  return {
    pid: process.pid,
    parent_pid: process.parentPid,
    process_name: process.name,
    role,
    process_start_utc: process.processStartUtc,
    executable_path: process.executablePath,
    command_line_redacted: process.commandLine ? bounded(redactCommandLine(process.commandLine), MAX_COMMAND_LINE_CHARS) : null,
    parsed_project_path: parsedProjectPath,
    canonical_project_root: canonicalProjectRoot,
    heartbeat_match_count: heartbeatMatches.length,
    heartbeat_matches: heartbeatMatches.slice(0, 10),
    current_project_match: currentProjectMatch,
    eligible_for_terminate: eligibleForTerminate,
    terminate_refusal_reason: terminateRefusalReason,
  };
}

function terminateRefusalReason(pid: number | null, requestedRoot: string | null, confirmTerminate: boolean, target: UnityProcessSnapshot | null): string | null {
  if (pid === null) {
    return "invalid_pid";
  }

  if (!confirmTerminate) {
    return "confirm_terminate_required";
  }

  if (requestedRoot === null) {
    return "canonical_project_root_required";
  }

  if (!target) {
    return "process_not_found";
  }

  if (target.role !== "unity_editor") {
    return "unsupported_process_role";
  }

  if (!target.canonical_project_root) {
    return "missing_project_path";
  }

  if (target.canonical_project_root !== requestedRoot) {
    return "project_mismatch";
  }

  if (!target.current_project_match) {
    return "current_project_mismatch";
  }

  return null;
}

function parsePowerShellProcessJson(output: string): RawUnityProcess[] | undefined {
  try {
    const parsed = JSON.parse(output);
    if (typeof parsed !== "object" || parsed === null || Array.isArray(parsed)) return undefined;
    const envelope = parsed as Record<string, unknown>;
    if (envelope.schema_version !== 1 || envelope.query_succeeded !== true || !Array.isArray(envelope.processes)) return undefined;
    const rows = envelope.processes;
    const processes = rows.map(rawProcess);
    return processes.every((process): process is RawUnityProcess => process !== undefined) ? processes : undefined;
  } catch {
    return undefined;
  }
}

function rawProcess(row: unknown): RawUnityProcess | undefined {
  if (typeof row !== "object" || row === null) {
    return undefined;
  }

  const record = row as Record<string, unknown>;
  const pid = numberOrNull(record.ProcessId);
  const name = stringOrNull(record.Name);
  if (pid === null || !name) {
    return undefined;
  }

  return {
    pid,
    parentPid: numberOrNull(record.ParentProcessId),
    name,
    executablePath: stringOrNull(record.ExecutablePath),
    commandLine: stringOrNull(record.CommandLine),
    processStartUtc: datetimeOrNull(record.ProcessStartUtc),
  };
}

function datetimeOrNull(value: unknown): string | null {
  const text = stringOrNull(value);
  return text && canonicalProcessStartIdentity(text) !== undefined ? text : null;
}

export function sameUnityProcessStart(left: string | null | undefined, right: string | null | undefined): boolean {
  const leftIdentity = canonicalProcessStartIdentity(left);
  const rightIdentity = canonicalProcessStartIdentity(right);
  return leftIdentity !== undefined && leftIdentity === rightIdentity;
}

function processGenerationState(identity: RegistryProcessGenerationIdentity, processes: RawUnityProcess[]): RegistryProcessState {
  const samePid = processes.filter((process) => process.pid === identity.unity_pid && UNITY_PROCESS_NAMES.has(process.name.toLowerCase()));
  if (samePid.length === 0) return "dead";
  if (samePid.some((process) => process.processStartUtc === null)) return "uncertain";
  const starts = new Set(samePid.map((process) => canonicalProcessStartIdentity(process.processStartUtc)).filter((value): value is string => value !== undefined));
  if (starts.size !== 1) return "uncertain";
  const currentStart = [...starts][0];
  const expectedStart = canonicalProcessStartIdentity(identity.unity_process_start_utc);
  if (!currentStart || !expectedStart) return "uncertain";
  return currentStart === expectedStart ? "live" : "dead";
}

function uncertainProcessGenerations(identities: ReadonlyArray<RegistryProcessGenerationIdentity>): RegistryProcessGenerationObservation[] {
  return identities.map((identity) => ({ ...identity, state: "uncertain" }));
}

function processGenerationCacheKey(identity: RegistryProcessGenerationIdentity): string {
  return JSON.stringify([
    identity.unity_pid,
    identity.unity_process_start_utc,
  ]);
}

function localProcessState(pid: number): RegistryProcessState {
  try {
    process.kill(pid, 0);
    return "live";
  } catch (error) {
    return typeof error === "object" && error !== null && "code" in error && String(error.code) === "ESRCH" ? "dead" : "uncertain";
  }
}

function canonicalProcessStartIdentity(value: unknown): string | undefined {
  if (typeof value !== "string") return undefined;
  const match = /^(\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2})(?:\.(\d{1,9}))?Z$/.exec(value);
  if (!match || !Number.isFinite(Date.parse(value))) return undefined;
  return `${match[1]}.${(match[2] ?? "").padEnd(9, "0")}Z`;
}

function parseProjectPath(commandLine: string): string | null {
  const tokens = splitCommandLine(commandLine);
  for (let index = 0; index < tokens.length; index++) {
    const token = tokens[index] ?? "";
    if (token.toLowerCase() === "-projectpath") {
      return tokens[index + 1] ?? null;
    }

    const assignment = /^-projectPath=(.+)$/i.exec(token);
    if (assignment) {
      return assignment[1] ?? null;
    }
  }

  return null;
}

function splitCommandLine(commandLine: string): string[] {
  const result: string[] = [];
  const pattern = /"([^"]*)"|'([^']*)'|(\S+)/g;
  let match: RegExpExecArray | null;
  while ((match = pattern.exec(commandLine)) !== null) {
    result.push(match[1] ?? match[2] ?? match[3] ?? "");
  }
  return result;
}

function heartbeatMatchesForProcess(pid: number, canonicalProjectRoot: string | null, projects: PublicProjectRecord[]): UnityProcessSnapshot["heartbeat_matches"] {
  const matches: UnityProcessSnapshot["heartbeat_matches"] = [];
  for (const project of projects) {
    const matchReason = project.unity_pid === pid
      ? "pid"
      : canonicalProjectRoot !== null && canonicalRoots(project).has(canonicalProjectRoot)
        ? "canonical_project_root"
        : undefined;
    if (!matchReason) {
      continue;
    }

    matches.push({
      project: project.project,
      instance_id: project.instance_id,
      status: project.status,
      stale: project.stale,
      unity_pid: project.unity_pid,
      canonical_project_root: project.canonical_project_root ?? null,
      match_reason: matchReason,
    });
  }
  return matches;
}

function heartbeatRecordMatchesProcess(project: PublicProjectRecord, pid: number, canonicalProjectRoot: string | null): boolean {
  return project.unity_pid === pid || (canonicalProjectRoot !== null && canonicalRoots(project).has(canonicalProjectRoot));
}

function canonicalRoots(project: PublicProjectRecord): Set<string> {
  const roots = new Set<string>();
  if (project.canonical_project_root) {
    roots.add(project.canonical_project_root);
  }
  for (const alias of project.aliases ?? []) {
    roots.add(alias.canonical_path);
  }
  return roots;
}

function normalizeProjectRootOrNull(value: unknown): string | null {
  if (typeof value !== "string" || value.trim().length === 0) {
    return null;
  }

  try {
    return canonicalPaths.normalizeProjectRoot(value);
  } catch {
    return null;
  }
}

function unityProcessRole(name: string, commandLine: string | null): UnityProcessRole {
  const lower = name.toLowerCase();
  if (UNITY_PROCESS_NAMES.has(lower)) {
    if (isAssetImportWorkerCommandLine(commandLine)) {
      return "unity_asset_import_worker";
    }
    return "unity_editor";
  }
  if (lower === "unity.licensing.client.exe") {
    return "unity_licensing_client";
  }
  if (lower === "unity hub.exe" || lower === "unity hub") {
    return "unity_hub";
  }
  return "other_unity_process";
}

function isAssetImportWorkerCommandLine(commandLine: string | null): boolean {
  if (!commandLine) return false;
  const tokens = splitCommandLine(commandLine);
  for (let index = 0; index < tokens.length - 1; index++) {
    if (tokens[index]?.toLowerCase() === "-name" && /^assetimportworker(?:hw)?\d+$/i.test(tokens[index + 1] ?? "")) {
      return true;
    }
  }
  return false;
}

function redactCommandLine(commandLine: string): string {
  return commandLine
    .replaceAll(/(token|password|passwd|secret|authorization|api[_-]?key)(=|\s+)[^\s"]+/gi, "$1$2<redacted>")
    .replaceAll(/(--?(?:token|password|passwd|secret|authorization|api[_-]?key))\s+"[^"]*"/gi, "$1 \"<redacted>\"");
}

function runWindowsTool(file: string, args: string[], options: UnityProcessOptions): WindowsToolResult {
  const execFileSync = options.execFileSync ?? nodeExecFileSync;
  try {
    return {
      status: "ok",
      executable: file,
      output: execFileSync(file, args, { encoding: "utf8", timeout: Math.max(1, Math.min(WINDOWS_TOOL_TIMEOUT_MS, Math.floor(options.toolTimeoutMs ?? WINDOWS_TOOL_TIMEOUT_MS))), windowsHide: true, stdio: "pipe" }) as string,
    };
  } catch (error) {
    const code = typeof error === "object" && error !== null ? (error as { code?: unknown }).code : undefined;
    return { status: "error", executable: file, error: code === "ETIMEDOUT" ? "windows_tool_timeout" : code === "ENOBUFS" ? "windows_tool_output_limit_exceeded" : "windows_tool_failed" };
  }
}

function numberOrNull(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function stringOrNull(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

function bounded(value: string, maxChars: number): string {
  return value.length <= maxChars ? value : value.slice(0, maxChars);
}

function uniqueStrings(values: string[]): string[] {
  return [...new Set(values)];
}
