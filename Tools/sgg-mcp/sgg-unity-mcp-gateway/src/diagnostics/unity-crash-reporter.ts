import { execFileSync as nodeExecFileSync } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

const BUG_REPORTER_IMAGE = "UnityBugReporter.exe";
const BUG_REPORTER_WINDOW_TITLE = "Unity Bug Reporter";
const DEFAULT_MAX_ARTIFACTS = 8;
const DEFAULT_MAX_ARTIFACT_AGE_MS = 24 * 60 * 60 * 1000;
const WINDOWS_TOOL_TIMEOUT_MS = 5000;

export type ExecFileSyncLike = (file: string, args: string[], options: { encoding: "utf8"; timeout: number; windowsHide: boolean; stdio: "pipe" }) => string;

export type UnityCrashReporterOptions = {
  env?: NodeJS.ProcessEnv;
  now?: () => Date;
  tmpdir?: () => string;
  execFileSync?: ExecFileSyncLike;
  tempRoots?: string[];
  maxArtifacts?: number;
  maxArtifactAgeMs?: number;
};

export type UnityCrashReporterRuntime = {
  status: () => UnityCrashReporterStatus;
  close: (args: UnityCrashReporterCloseArgs) => UnityCrashReporterCloseResult;
};

export type UnityCrashReporterProcess = {
  pid: number;
  image_name: string;
  window_title: string;
  source: "tasklist";
};

export type UnityCrashFileSummary = {
  name: string;
  bytes: number;
  last_write_utc: string;
  role: "crash_dump" | "editor_log" | "system_info" | "crash_report_json" | "other";
};

export type UnityCrashArtifact = {
  kind: "unity_editor_crash_directory" | "bug_report_json" | "bug_report_temp_directory";
  path: string;
  name: string;
  bytes: number;
  last_write_utc: string;
  age_ms: number;
  files: UnityCrashFileSummary[];
  fs_path?: string;
};

export type UnityCrashTempRootProbe = {
  source: string;
  path: string;
  fs_path?: string;
  exists: boolean;
};

export type UnityCrashReporterStatus = {
  schema_version: 1;
  supported: boolean;
  unity_crashed: boolean;
  blocking_agent_work: boolean;
  active_reporters_count: number;
  active_reporters: UnityCrashReporterProcess[];
  recent_crash_artifacts: {
    max_age_ms: number;
    crash_directories: UnityCrashArtifact[];
    bug_report_json: UnityCrashArtifact[];
    bug_report_temp_directories: UnityCrashArtifact[];
  };
  temp_roots: UnityCrashTempRootProbe[];
  errors: string[];
  next_valid_actions: string[];
};

export type UnityCrashReporterCloseArgs = {
  pids?: number[];
  force?: boolean;
  dry_run?: boolean;
};

export type UnityCrashReporterCloseResult = {
  schema_version: 1;
  dry_run: boolean;
  force: boolean;
  submit_attempted: false;
  close_method: "taskkill_window_close" | "taskkill_force";
  target_pids: number[];
  closed_count: number;
  skipped: Array<{ pid: number; reason: string }>;
  results: Array<{ pid: number; status: "closed" | "would_close" | "error"; executable?: string; error?: string }>;
  active_before: UnityCrashReporterStatus;
  active_after?: UnityCrashReporterStatus;
};

type WindowsToolResult =
  | { status: "ok"; executable: string; output: string }
  | { status: "error"; executable?: string; error: string; output?: string };

type TempRoot = {
  source: string;
  displayPath: string;
  fsPath: string;
};

export function inspectUnityCrashReporter(options: UnityCrashReporterOptions = {}): UnityCrashReporterStatus {
  const now = options.now?.() ?? new Date();
  const maxArtifacts = options.maxArtifacts ?? DEFAULT_MAX_ARTIFACTS;
  const maxArtifactAgeMs = options.maxArtifactAgeMs ?? DEFAULT_MAX_ARTIFACT_AGE_MS;
  const processScan = scanBugReporterProcesses(options);
  const tempRoots = resolveTempRoots(options);
  const artifactScan = scanCrashArtifacts(tempRoots, now, maxArtifacts, maxArtifactAgeMs);
  const artifactCount = artifactScan.crashDirectories.length + artifactScan.bugReportJson.length + artifactScan.bugReportTempDirectories.length;
  const blockingAgentWork = processScan.processes.length > 0;
  const unityCrashed = blockingAgentWork || artifactCount > 0;
  const nextValidActions = ["call_diagnostics_status", "inspect_unity_editor_crash_artifacts"];

  if (blockingAgentWork) {
    nextValidActions.push("call_diagnostics_unity_crash_reporter_close");
  }

  return {
    schema_version: 1,
    supported: processScan.supported,
    unity_crashed: unityCrashed,
    blocking_agent_work: blockingAgentWork,
    active_reporters_count: processScan.processes.length,
    active_reporters: processScan.processes,
    recent_crash_artifacts: {
      max_age_ms: maxArtifactAgeMs,
      crash_directories: artifactScan.crashDirectories,
      bug_report_json: artifactScan.bugReportJson,
      bug_report_temp_directories: artifactScan.bugReportTempDirectories,
    },
    temp_roots: tempRoots.map((root) => ({
      source: root.source,
      path: root.displayPath,
      ...(root.displayPath !== root.fsPath ? { fs_path: root.fsPath } : {}),
      exists: fs.existsSync(root.fsPath),
    })),
    errors: [...processScan.errors, ...artifactScan.errors],
    next_valid_actions: nextValidActions,
  };
}

export function closeUnityCrashReporters(args: UnityCrashReporterCloseArgs, options: UnityCrashReporterOptions = {}): UnityCrashReporterCloseResult {
  const activeBefore = inspectUnityCrashReporter(options);
  const activePids = new Set(activeBefore.active_reporters.map((process) => process.pid));
  const requestedPids = args.pids ?? activeBefore.active_reporters.map((process) => process.pid);
  const targetPids: number[] = [];
  const skipped: Array<{ pid: number; reason: string }> = [];

  for (const pid of requestedPids) {
    if (!Number.isInteger(pid) || pid <= 0) {
      skipped.push({ pid, reason: "invalid_pid" });
    } else if (!activePids.has(pid)) {
      skipped.push({ pid, reason: "not_active_unity_bug_reporter" });
    } else if (!targetPids.includes(pid)) {
      targetPids.push(pid);
    }
  }

  const dryRun = args.dry_run === true;
  const force = args.force === true;
  const closeMethod = force ? "taskkill_force" : "taskkill_window_close";
  const results: UnityCrashReporterCloseResult["results"] = [];

  for (const pid of targetPids) {
    if (dryRun) {
      results.push({ pid, status: "would_close" });
      continue;
    }

    const taskkillArgs = ["/PID", String(pid), ...(force ? ["/F"] : [])];
    const taskkill = runWindowsTool("taskkill.exe", taskkillArgs, options);
    if (taskkill.status === "ok") {
      results.push({ pid, status: "closed", executable: taskkill.executable });
    } else {
      results.push({ pid, status: "error", ...(taskkill.executable ? { executable: taskkill.executable } : {}), error: taskkill.error });
    }
  }

  const closedCount = results.filter((result) => result.status === "closed").length;
  return {
    schema_version: 1,
    dry_run: dryRun,
    force,
    submit_attempted: false,
    close_method: closeMethod,
    target_pids: targetPids,
    closed_count: closedCount,
    skipped,
    results,
    active_before: activeBefore,
    ...(dryRun ? {} : { active_after: inspectUnityCrashReporter(options) }),
  };
}

function scanBugReporterProcesses(options: UnityCrashReporterOptions): { supported: boolean; processes: UnityCrashReporterProcess[]; errors: string[] } {
  const tasklist = runWindowsTool("tasklist.exe", ["/FI", `IMAGENAME eq ${BUG_REPORTER_IMAGE}`, "/V", "/FO", "CSV", "/NH"], options);
  if (tasklist.status === "error") {
    return { supported: false, processes: [], errors: [`tasklist_failed: ${tasklist.error}`] };
  }

  const processes = parseCsvRows(tasklist.output)
    .map((row): UnityCrashReporterProcess | undefined => {
      const imageName = row[0] ?? "";
      const pid = Number(row[1] ?? "");
      const windowTitle = row[8] ?? "";
      if (imageName.toLowerCase() !== BUG_REPORTER_IMAGE.toLowerCase() || !Number.isInteger(pid) || pid <= 0) {
        return undefined;
      }
      if (windowTitle !== BUG_REPORTER_WINDOW_TITLE) {
        return undefined;
      }
      return { pid, image_name: imageName, window_title: windowTitle, source: "tasklist" };
    })
    .filter((process): process is UnityCrashReporterProcess => process !== undefined);

  return { supported: true, processes, errors: [] };
}

function scanCrashArtifacts(tempRoots: TempRoot[], now: Date, maxArtifacts: number, maxArtifactAgeMs: number): { crashDirectories: UnityCrashArtifact[]; bugReportJson: UnityCrashArtifact[]; bugReportTempDirectories: UnityCrashArtifact[]; errors: string[] } {
  const errors: string[] = [];
  const crashDirectories: UnityCrashArtifact[] = [];
  const bugReportJson: UnityCrashArtifact[] = [];
  const bugReportTempDirectories: UnityCrashArtifact[] = [];

  for (const root of tempRoots) {
    crashDirectories.push(...scanArtifactDirectory({
      root,
      relativeParts: ["Unity", "Editor", "Crashes"],
      prefix: "Crash_",
      kind: "unity_editor_crash_directory",
      now,
      maxArtifactAgeMs,
      errors,
    }));
    bugReportJson.push(...scanArtifactDirectory({
      root,
      relativeParts: ["BugReporterCrashReportJson"],
      prefix: "CrashReport_",
      suffix: ".json",
      kind: "bug_report_json",
      now,
      maxArtifactAgeMs,
      errors,
    }));
    bugReportTempDirectories.push(...scanArtifactDirectory({
      root,
      relativeParts: [],
      prefix: "UnityBugReporter_",
      kind: "bug_report_temp_directory",
      now,
      maxArtifactAgeMs,
      errors,
    }));
  }

  return {
    crashDirectories: mostRecentUniqueArtifacts(crashDirectories, maxArtifacts),
    bugReportJson: mostRecentUniqueArtifacts(bugReportJson, maxArtifacts),
    bugReportTempDirectories: mostRecentUniqueArtifacts(bugReportTempDirectories, maxArtifacts),
    errors,
  };
}

function scanArtifactDirectory(options: { root: TempRoot; relativeParts: string[]; prefix: string; suffix?: string; kind: UnityCrashArtifact["kind"]; now: Date; maxArtifactAgeMs: number; errors: string[] }): UnityCrashArtifact[] {
  const rootFsPath = joinFsPath(options.root.fsPath, options.relativeParts);
  if (!fs.existsSync(rootFsPath)) {
    return [];
  }

  let entries: string[];
  try {
    entries = fs.readdirSync(rootFsPath);
  } catch (error) {
    options.errors.push(`artifact_scan_failed: ${rootFsPath}: ${safeErrorMessage(error)}`);
    return [];
  }

  const artifacts: UnityCrashArtifact[] = [];
  for (const name of entries) {
    if (!name.startsWith(options.prefix) || (options.suffix && !name.endsWith(options.suffix))) {
      continue;
    }

    const fsPath = path.join(rootFsPath, name);
    const stat = safeStat(fsPath);
    if (!stat) {
      continue;
    }
    const ageMs = Math.max(0, options.now.getTime() - stat.mtime.getTime());
    if (ageMs > options.maxArtifactAgeMs) {
      continue;
    }

    const displayPath = joinDisplayPath(options.root.displayPath, [...options.relativeParts, name]);
    artifacts.push({
      kind: options.kind,
      path: displayPath,
      ...(displayPath !== fsPath ? { fs_path: fsPath } : {}),
      name,
      bytes: stat.isDirectory() ? directorySize(fsPath) : stat.size,
      last_write_utc: stat.mtime.toISOString(),
      age_ms: ageMs,
      files: summarizeArtifactFiles(fsPath, stat),
    });
  }

  return artifacts;
}

function summarizeArtifactFiles(fsPath: string, stat: fs.Stats): UnityCrashFileSummary[] {
  if (!stat.isDirectory()) {
    return [{ name: path.basename(fsPath), bytes: stat.size, last_write_utc: stat.mtime.toISOString(), role: classifyCrashFile(path.basename(fsPath)) }];
  }

  let names: string[];
  try {
    names = fs.readdirSync(fsPath).slice(0, 20);
  } catch {
    return [];
  }

  return names.flatMap((name) => {
    const childPath = path.join(fsPath, name);
    const childStat = safeStat(childPath);
    if (!childStat || !childStat.isFile()) {
      return [];
    }
    return [{ name, bytes: childStat.size, last_write_utc: childStat.mtime.toISOString(), role: classifyCrashFile(name) }];
  });
}

function classifyCrashFile(name: string): UnityCrashFileSummary["role"] {
  const lower = name.toLowerCase();
  if (lower === "crash.dmp" || lower.endsWith(".dmp")) {
    return "crash_dump";
  }
  if (lower === "editor.log" || lower === "editor-prev.log") {
    return "editor_log";
  }
  if (lower === "system_info.txt") {
    return "system_info";
  }
  if (lower.startsWith("crashreport_") && lower.endsWith(".json")) {
    return "crash_report_json";
  }
  return "other";
}

function directorySize(fsPath: string): number {
  let total = 0;
  let names: string[];
  try {
    names = fs.readdirSync(fsPath);
  } catch {
    return total;
  }

  for (const name of names) {
    const stat = safeStat(path.join(fsPath, name));
    if (stat?.isFile()) {
      total += stat.size;
    }
  }
  return total;
}

function mostRecentUniqueArtifacts(artifacts: UnityCrashArtifact[], maxArtifacts: number): UnityCrashArtifact[] {
  const unique = new Map<string, UnityCrashArtifact>();
  for (const artifact of artifacts) {
    unique.set(artifact.path, artifact);
  }
  return [...unique.values()].sort((left, right) => Date.parse(right.last_write_utc) - Date.parse(left.last_write_utc)).slice(0, maxArtifacts);
}

function resolveTempRoots(options: UnityCrashReporterOptions): TempRoot[] {
  const env = options.env ?? process.env;
  const candidates: Array<{ source: string; value: string | undefined }> = [];

  for (const value of options.tempRoots ?? []) {
    candidates.push({ source: "configured", value });
  }

  candidates.push(
    { source: "env:TEMP", value: env.TEMP },
    { source: "env:TMP", value: env.TMP },
    { source: "node:os.tmpdir", value: options.tmpdir?.() ?? os.tmpdir() },
  );

  for (const [name, value] of Object.entries(readCmdEnvironment(options, ["TEMP", "TMP"]))) {
    candidates.push({ source: `cmd:${name}`, value });
  }

  const roots: TempRoot[] = [];
  const seen = new Set<string>();
  for (const candidate of candidates) {
    const value = candidate.value?.trim();
    if (!value) {
      continue;
    }

    const fsPath = toReadableFsPath(value);
    const key = normalizeFsPathKey(fsPath);
    if (seen.has(key)) {
      continue;
    }
    seen.add(key);
    roots.push({ source: candidate.source, displayPath: normalizeDisplayPath(value), fsPath });
  }

  return roots;
}

function readCmdEnvironment(options: UnityCrashReporterOptions, names: string[]): Record<string, string> {
  const output = runWindowsTool("cmd.exe", ["/C", "set"], options);
  if (output.status !== "ok") {
    return {};
  }

  const wanted = new Set(names.map((name) => name.toUpperCase()));
  const values: Record<string, string> = {};
  for (const line of output.output.split(/\r?\n/)) {
    const index = line.indexOf("=");
    if (index <= 0) {
      continue;
    }
    const name = line.slice(0, index).toUpperCase();
    if (wanted.has(name)) {
      values[name] = line.slice(index + 1).trim();
    }
  }
  return values;
}

function runWindowsTool(toolName: "tasklist.exe" | "taskkill.exe" | "cmd.exe", args: string[], options: UnityCrashReporterOptions): WindowsToolResult {
  const execFileSync = options.execFileSync ?? defaultExecFileSync;
  const errors: string[] = [];
  for (const executable of windowsToolCandidates(toolName)) {
    try {
      return {
        status: "ok",
        executable,
        output: execFileSync(executable, args, { encoding: "utf8", timeout: WINDOWS_TOOL_TIMEOUT_MS, windowsHide: true, stdio: "pipe" }),
      };
    } catch (error) {
      const message = safeErrorMessage(error);
      if (isExecutableMissing(error)) {
        errors.push(`${executable}: ${message}`);
        continue;
      }
      const output = childProcessOutput(error);
      return { status: "error", executable, error: message, ...(output ? { output } : {}) };
    }
  }
  return { status: "error", error: `tool_not_found: ${toolName}${errors.length > 0 ? ` (${errors.join("; ")})` : ""}` };
}

function defaultExecFileSync(file: string, args: string[], options: { encoding: "utf8"; timeout: number; windowsHide: boolean; stdio: "pipe" }): string {
  return nodeExecFileSync(file, args, options);
}

function windowsToolCandidates(toolName: "tasklist.exe" | "taskkill.exe" | "cmd.exe"): string[] {
  if (process.platform === "win32") {
    return [toolName];
  }
  return [toolName, `/mnt/c/Windows/System32/${toolName}`];
}

function parseCsvRows(text: string): string[][] {
  return text.split(/\r?\n/).map((line) => line.trim()).filter(Boolean).map(parseCsvRow);
}

function parseCsvRow(line: string): string[] {
  const values: string[] = [];
  let current = "";
  let quoted = false;

  for (let index = 0; index < line.length; index += 1) {
    const char = line[index];
    if (char === '"') {
      if (quoted && line[index + 1] === '"') {
        current += '"';
        index += 1;
      } else {
        quoted = !quoted;
      }
    } else if (char === "," && !quoted) {
      values.push(current);
      current = "";
    } else {
      current += char;
    }
  }

  values.push(current);
  return values;
}

function toReadableFsPath(input: string): string {
  const normalized = input.trim();
  if (process.platform !== "win32") {
    const match = /^([A-Za-z]):[\\/]*(.*)$/.exec(normalized);
    if (match) {
      const drive = match[1]?.toLowerCase() ?? "c";
      const rest = (match[2] ?? "").replaceAll("\\", "/").replace(/^\/+/, "");
      return `/mnt/${drive}${rest ? `/${rest}` : ""}`;
    }
  }
  return normalized;
}

function normalizeDisplayPath(input: string): string {
  const trimmed = input.trim();
  return /^[A-Za-z]:[\\/]/.test(trimmed) ? trimmed.replaceAll("/", "\\") : trimmed;
}

function normalizeFsPathKey(input: string): string {
  return input.replaceAll("\\", "/").replace(/\/+$/, "").toLowerCase();
}

function joinFsPath(root: string, parts: string[]): string {
  return parts.length === 0 ? root : path.join(root, ...parts);
}

function joinDisplayPath(root: string, parts: string[]): string {
  if (/^[A-Za-z]:[\\/]/.test(root)) {
    return [root.replace(/[\\/]+$/, ""), ...parts].join("\\");
  }
  return path.posix.join(root.replaceAll("\\", "/"), ...parts);
}

function safeStat(fsPath: string): fs.Stats | undefined {
  try {
    return fs.statSync(fsPath);
  } catch {
    return undefined;
  }
}

function childProcessOutput(error: unknown): string | undefined {
  if (typeof error !== "object" || error === null) {
    return undefined;
  }
  const output = (error as { stdout?: unknown; stderr?: unknown }).stderr ?? (error as { stdout?: unknown }).stdout;
  return typeof output === "string" && output.length > 0 ? output : undefined;
}

function isExecutableMissing(error: unknown): boolean {
  return typeof error === "object" && error !== null && (error as { code?: unknown }).code === "ENOENT";
}

function safeErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}
