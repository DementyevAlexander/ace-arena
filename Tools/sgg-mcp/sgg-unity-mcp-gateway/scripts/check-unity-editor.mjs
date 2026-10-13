#!/usr/bin/env node

import { execFileSync, spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { pathToFileURL } from "node:url";

import { normalize as normalizeCanonicalPath, toWin32 as canonicalToWin32, toWSL as canonicalToWSL } from "@romanilyin/canonicalpath";

import { discoverUnityEditors, findUnityEditor } from "./unity-editor-discovery.mjs";

export const defaultUnityEditorTimeoutMs = 15 * 60 * 1000;
export const unityLicensingClientCrashModalCode = "unity_licensing_client_crash_modal";
export const corruptedLibraryModalCode = "blocked_corrupted_library";
export const missingSignatureModalCode = "blocked_missing_signature_packages";
export const externalSceneReloadModalCode = "blocked_external_scene_reload";
export const projectDowngradeModalCode = "blocked_project_downgrade_prompt";
export const duplicateProjectInstanceModalCode = "blocked_duplicate_project_instance";
export const unityStartupStallCode = "unity_startup_stalled_before_bridge";
export const sameProjectEditorOpenCode = "same_project_editor_open";
export const defaultGatewayRegistryStaleAfterMs = 90_000;
export const defaultUnityStartupStallTimeoutMs = 180_000;
const nativeModalPollIntervalMs = 2000;
const startupStallPollIntervalMs = 5000;

const localEnvKeys = new Set([
  "UNITY_EXE",
  "SGG_UNITY_CLI",
  "SGG_UNITY_HUB",
  "SGG_UNITY_EDITOR_ROOTS",
  "SGG_UNITY_EDITOR_VERSION",
  "SGG_UNITY_EDITOR_MATRIX",
  "SGG_UNITY_EDITOR_VERSIONS",
  "SGG_UNITY_EDITOR_MATRIX_REQUIRED",
  "SGG_UNITY_EDITOR_REQUIRED",
  "SGG_UNITY_EDITOR_TIMEOUT_MS",
  "SGG_UNITY_STARTUP_STALL_TIMEOUT_MS",
  "SGG_UNITY_TEST_PROJECT",
  "SGG_UNITY_TEST_LOG",
  "SGG_UNITY_MATRIX_LOG_DIR",
  "SGG_UNITY_WSL_MOUNT_ROOT",
  "SGG_UNITY_EXPECT_PACKAGE_PATH",
  "SGG_UNITY_REQUIRE_LOCAL_PACKAGE",
  "SGG_UNITY_MCP_CONFIG",
  "SGG_UNITY_MCP_INSTANCES_DIR",
]);

export const unityCompileFailurePatterns = [
  { pattern: /error CS\d+:/, message: "Unity log contains C# compiler errors" },
  { pattern: /Scripts have compiler errors/, message: "Unity log reports script compiler errors" },
  { pattern: /Compilation failed/, message: "Unity log reports compilation failure" },
  { pattern: /Project has invalid dependencies/, message: "Unity log reports invalid package dependencies" },
  {
    pattern: /(?:Packages[\\/]com\.sungeargames\.unity-mcp|SGG\.UnityMCP\.Editor).*warning CS0618|warning CS0618.*(?:Packages[\\/]com\.sungeargames\.unity-mcp|SGG\.UnityMCP\.Editor)/,
    message: "Unity package compile produced obsolete API warning CS0618",
  },
];

export function analyzeUnityEditorLog(logText, options = {}) {
  for (const { pattern, message } of unityCompileFailurePatterns) {
    if (pattern.test(logText)) {
      return { ok: false, message, pattern: pattern.toString() };
    }
  }

  if (!logText.includes("SGG.UnityMCP.Editor.dll") && !options.scriptAssemblyExists) {
    return {
      ok: false,
      message: "SGG.UnityMCP.Editor.dll was not mentioned in the log and no compiled assembly was found",
    };
  }

  return { ok: true };
}

export function validateLocalPackageReference(projectPath, expectedPackagePath, requireLocalPackage) {
  if (!requireLocalPackage) {
    return { ok: true };
  }

  const manifestPath = path.join(projectPath, "Packages", "manifest.json");
  if (!fs.existsSync(manifestPath)) {
    return { ok: false, message: `missing Unity manifest at ${manifestPath}` };
  }

  let manifest;
  try {
    manifest = JSON.parse(fs.readFileSync(manifestPath, "utf8"));
  } catch (error) {
    return { ok: false, message: `failed to read Unity manifest: ${error.message}` };
  }

  const dependency = manifest.dependencies?.["com.sungeargames.unity-mcp"];
  if (typeof dependency !== "string" || dependency.length === 0) {
    return { ok: false, message: "Unity manifest does not reference com.sungeargames.unity-mcp" };
  }

  if (!dependency.startsWith("file:")) {
    return { ok: false, message: "SGG_UNITY_REQUIRE_LOCAL_PACKAGE=true requires a file: com.sungeargames.unity-mcp dependency" };
  }

  const dependencyPath = dependency.slice("file:".length);
  const actualPath = path.resolve(path.dirname(manifestPath), dependencyPath);
  const expectedPath = path.resolve(expectedPackagePath);
  if (actualPath !== expectedPath) {
    return { ok: false, message: `Unity manifest package path ${actualPath} does not match expected ${expectedPath}` };
  }

  return { ok: true };
}

export async function runUnityEditorValidation(env = process.env, cwd = process.cwd(), options = {}) {
  const effectiveEnv = loadLocalUnityEditorEnv(env, cwd);
  const unityExe = resolveUnityExe(effectiveEnv);
  const projectPath = effectiveEnv.SGG_UNITY_TEST_PROJECT;
  const requireValidation = options.required === true || effectiveEnv.SGG_UNITY_EDITOR_REQUIRED === "true";

  if (!projectPath) {
    const message = "set UNITY_EXE or SGG_UNITY_EDITOR_VERSION, plus SGG_UNITY_TEST_PROJECT to run Unity Editor validation";
    if (requireValidation) {
      console.error(`[check-unity-editor] required validation unavailable: ${message}`);
      return 1;
    }

    console.log(`[check-unity-editor] skipped: ${message}`);
    return 0;
  }

  if (!unityExe) {
    if (effectiveEnv.SGG_UNITY_EDITOR_VERSION) {
      console.error(`[check-unity-editor] Unity Editor ${effectiveEnv.SGG_UNITY_EDITOR_VERSION} was not discovered; set UNITY_EXE or SGG_UNITY_EDITOR_ROOTS`);
      return 1;
    }

    const message = "set UNITY_EXE or SGG_UNITY_EDITOR_VERSION, plus SGG_UNITY_TEST_PROJECT to run Unity Editor validation";
    if (requireValidation) {
      console.error(`[check-unity-editor] required validation unavailable: ${message}`);
      return 1;
    }

    console.log(`[check-unity-editor] skipped: ${message}`);
    return 0;
  }

  const timeoutResult = parseUnityEditorTimeoutMs(effectiveEnv);
  if (!timeoutResult.ok) {
    console.error(`[check-unity-editor] ${timeoutResult.message}`);
    return 1;
  }
  const startupStallTimeoutResult = parseUnityStartupStallTimeoutMs(effectiveEnv);
  if (!startupStallTimeoutResult.ok) {
    console.error(`[check-unity-editor] ${startupStallTimeoutResult.message}`);
    return 1;
  }

  const localProjectPath = toLocalFsPath(projectPath, { env: effectiveEnv });
  const logFile = effectiveEnv.SGG_UNITY_TEST_LOG ?? joinLocalFsPath(localProjectPath || os.tmpdir(), "Logs", "sgg-unity-mcp-editor-check.log");
  const unityProjectPath = toUnityEditorFsPath(projectPath, { env: effectiveEnv });
  const unityLogFile = toUnityEditorFsPath(logFile, { env: effectiveEnv });
  const expectedPackagePath = toLocalFsPath(effectiveEnv.SGG_UNITY_EXPECT_PACKAGE_PATH ?? path.join(cwd, "unity", "Packages", "com.sungeargames.unity-mcp"), { env: effectiveEnv });
  const packageReferenceResult = validateLocalPackageReference(localProjectPath, expectedPackagePath, effectiveEnv.SGG_UNITY_REQUIRE_LOCAL_PACKAGE === "true");
  if (!packageReferenceResult.ok) {
    console.error(`[check-unity-editor] ${packageReferenceResult.message}`);
    return 1;
  }

  const sameProjectEditorOpen = options.sameProjectEditorOpenInspector
    ? options.sameProjectEditorOpenInspector(localProjectPath)
    : inspectSameProjectEditorOpen(localProjectPath, {
      env: effectiveEnv,
      cwd,
      processExists: options.processExists,
      now: options.now,
      unityProcessScanner: options.unityProcessScanner,
      execFileSync: options.execFileSync,
    });
  if (sameProjectEditorOpen.blocking) {
    printSameProjectEditorBlock(sameProjectEditorOpen);
    return 1;
  }

  const nativeModalScanner = options.nativeModalScanner ?? (() => inspectUnityNativeBlockingModals());
  const preflightNativeModal = nativeModalScanner();
  if (preflightNativeModal.blocking) {
    printNativeModalBlock("preflight", preflightNativeModal);
    return 1;
  }

  const spawnProcess = options.spawn ?? spawn;
  const result = await runUnityEditorProcess(spawnProcess, unityExe, ["-batchmode", "-quit", "-projectPath", unityProjectPath, "-logFile", unityLogFile], {
    timeoutMs: timeoutResult.timeoutMs,
    killGraceMs: options.killGraceMs,
    nativeModalScanner,
    nativeModalPollIntervalMs: options.nativeModalPollIntervalMs,
    startupStallDetector: options.startupStallDetector ?? createUnityStartupStallDetector(logFile, {
      timeoutMs: startupStallTimeoutResult.timeoutMs,
      now: options.now,
    }),
    startupStallPollIntervalMs: options.startupStallPollIntervalMs,
    logFile,
  });

  if (result.blockedByNativeModal) {
    printNativeModalBlock("runtime", result.nativeModal);
    console.error("[check-unity-editor] terminated only the spawned validation process; it does not kill unrelated Unity or licensing processes");
    if (result.pid) {
      console.error(`[check-unity-editor] spawned pid: ${result.pid}`);
    }
    return 1;
  }

  if (result.blockedByStartupStall) {
    printStartupStallBlock("runtime", result.startupStall, logFile);
    console.error("[check-unity-editor] terminated only the spawned validation process; it does not kill unrelated Unity instances and does not delete Library or lock files");
    if (result.pid) {
      console.error(`[check-unity-editor] spawned pid: ${result.pid}`);
    }
    return 1;
  }

  if (result.timedOut) {
    console.error(`[check-unity-editor] Unity Editor validation timed out after ${timeoutResult.timeoutMs} ms`);
    console.error(`[check-unity-editor] log: ${logFile}`);
    if (result.pid) {
      console.error(`[check-unity-editor] spawned pid: ${result.pid}`);
    }
    console.error("[check-unity-editor] attempted to terminate only the spawned validation process; it does not kill unrelated Unity instances");
    const tail = readFileTail(logFile, 12000);
    if (tail) {
      console.error("[check-unity-editor] recent log tail:");
      console.error(tail);
    }
    return 1;
  }

  if (result.error) {
    console.error(`[check-unity-editor] failed to launch Unity: ${result.error.message}`);
    return 1;
  }

  const readableLogFile = resolveExistingLocalPath(logFile);
  const logText = fs.existsSync(readableLogFile) ? fs.readFileSync(readableLogFile, "utf8") : "";
  const scriptAssemblyPath = localProjectPath ? joinLocalFsPath(localProjectPath, "Library", "ScriptAssemblies", "SGG.UnityMCP.Editor.dll") : undefined;
  const analysis = analyzeUnityEditorLog(logText, { scriptAssemblyExists: Boolean(scriptAssemblyPath && fileExistsLocal(scriptAssemblyPath)) });
  if (!analysis.ok) {
    console.error(`[check-unity-editor] ${analysis.message}`);
    if (analysis.pattern) {
      console.error(`[check-unity-editor] matched failure pattern ${analysis.pattern}`);
    }
    console.error(`[check-unity-editor] log: ${logFile}`);
    return 1;
  }

  if (result.status !== 0) {
    console.error(`[check-unity-editor] Unity exited with status ${result.status}`);
    console.error(`[check-unity-editor] log: ${logFile}`);
    return result.status ?? 1;
  }

  console.log(`[check-unity-editor] ok: ${logFile}`);
  return 0;
}

export function parseUnityEditorTimeoutMs(env = process.env) {
  const rawValue = env.SGG_UNITY_EDITOR_TIMEOUT_MS;
  if (rawValue === undefined || rawValue === "") {
    return { ok: true, timeoutMs: defaultUnityEditorTimeoutMs };
  }

  const timeoutMs = Number(rawValue);
  if (!Number.isInteger(timeoutMs) || timeoutMs <= 0) {
    return { ok: false, message: `SGG_UNITY_EDITOR_TIMEOUT_MS must be a positive integer number of milliseconds, got ${JSON.stringify(rawValue)}` };
  }

  return { ok: true, timeoutMs };
}

export function parseUnityStartupStallTimeoutMs(env = process.env) {
  const rawValue = env.SGG_UNITY_STARTUP_STALL_TIMEOUT_MS;
  if (rawValue === undefined || rawValue === "") {
    return { ok: true, timeoutMs: defaultUnityStartupStallTimeoutMs };
  }

  const timeoutMs = Number(rawValue);
  if (!Number.isInteger(timeoutMs) || timeoutMs <= 0) {
    return { ok: false, message: `SGG_UNITY_STARTUP_STALL_TIMEOUT_MS must be a positive integer number of milliseconds, got ${JSON.stringify(rawValue)}` };
  }

  return { ok: true, timeoutMs };
}

export function resolveUnityExe(env = process.env) {
  if (env.UNITY_EXE) {
    return env.UNITY_EXE;
  }

  if (!env.SGG_UNITY_EDITOR_VERSION) {
    return undefined;
  }

  const discovery = discoverUnityEditors({ env });
  const editor = findUnityEditor(discovery.editors, env.SGG_UNITY_EDITOR_VERSION);
  return editor?.executable_path && process.platform === "linux" ? toLocalFsPath(editor.executable_path, { env }) : editor?.executable_path;
}

export function loadLocalUnityEditorEnv(env = process.env, cwd = process.cwd()) {
  if (env.SGG_UNITY_EDITOR_ENV_FILE === "off") {
    return env;
  }

  const envFile = env.SGG_UNITY_EDITOR_ENV_FILE ?? path.join(cwd, ".env.local");
  if (!fs.existsSync(envFile)) {
    return env;
  }

  const loaded = { ...parseUnityEditorEnvFile(envFile), ...env };
  return loaded;
}

function parseUnityEditorEnvFile(filePath) {
  const parsed = {};
  for (const rawLine of fs.readFileSync(filePath, "utf8").split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line || line.startsWith("#")) {
      continue;
    }
    const separator = line.indexOf("=");
    if (separator <= 0) {
      continue;
    }
    const key = line.slice(0, separator).trim();
    if (!localEnvKeys.has(key)) {
      continue;
    }
    parsed[key] = line.slice(separator + 1).trim().replace(/^['"]|['"]$/g, "");
  }
  return parsed;
}

export function runUnityEditorProcess(spawnProcess, command, args, options = {}) {
  const timeoutMs = options.timeoutMs ?? defaultUnityEditorTimeoutMs;
  const killGraceMs = options.killGraceMs ?? 5000;
  return new Promise((resolve) => {
    let child;
    let settled = false;
    let timedOut = false;
    let forceTimer;
    let nativeModalTimer;
    let startupStallTimer;

    const finish = (result) => {
      if (settled) {
        return;
      }

      settled = true;
      clearTimeout(timeoutTimer);
      clearTimeout(forceTimer);
      clearInterval(nativeModalTimer);
      clearInterval(startupStallTimer);
      resolve(result);
    };

    const timeoutTimer = setTimeout(() => {
      timedOut = true;
      const killed = terminateSpawnedProcess(child);
      if (typeof child?.unref === "function") {
        child.unref();
      }

      forceTimer = setTimeout(() => {
        finish({ timedOut: true, killed, pid: child?.pid, status: null, signal: "timeout" });
      }, killGraceMs);
      if (typeof forceTimer.unref === "function") {
        forceTimer.unref();
      }
    }, timeoutMs);

    try {
      child = spawnProcess(command, args, { stdio: "inherit", windowsHide: true });
    } catch (error) {
      finish({ error, timedOut: false, status: null, signal: null });
      return;
    }

    if (options.nativeModalScanner) {
      nativeModalTimer = setInterval(() => {
        const nativeModal = options.nativeModalScanner();
        if (!nativeModal.blocking) {
          return;
        }

        const killed = terminateSpawnedProcess(child);
        if (typeof child?.unref === "function") {
          child.unref();
        }
        finish({ blockedByNativeModal: true, nativeModal, killed, timedOut: false, pid: child?.pid, status: null, signal: "native_modal_block" });
      }, options.nativeModalPollIntervalMs ?? nativeModalPollIntervalMs);
      if (typeof nativeModalTimer.unref === "function") {
        nativeModalTimer.unref();
      }
    }

    if (options.startupStallDetector) {
      startupStallTimer = setInterval(() => {
        const startupStall = options.startupStallDetector();
        if (!startupStall.blocking) {
          return;
        }

        const killed = terminateSpawnedProcess(child);
        if (typeof child?.unref === "function") {
          child.unref();
        }
        finish({ blockedByStartupStall: true, startupStall, killed, timedOut: false, pid: child?.pid, status: null, signal: "startup_stall" });
      }, options.startupStallPollIntervalMs ?? startupStallPollIntervalMs);
      if (typeof startupStallTimer.unref === "function") {
        startupStallTimer.unref();
      }
    }

    child.once("error", (error) => finish({ error, timedOut: false, pid: child.pid, status: null, signal: null }));
    child.once("close", (status, signal) => finish({ timedOut, pid: child.pid, status, signal }));
  });
}

export function createUnityStartupStallDetector(logFile, options = {}) {
  const startTimeMs = options.startTimeMs ?? (options.now ? options.now().getTime() : Date.now());
  const timeoutMs = options.timeoutMs ?? defaultUnityStartupStallTimeoutMs;
  const now = options.now ?? (() => new Date());
  return () => inspectUnityStartupStall(logFile, { startTimeMs, timeoutMs, now });
}

export function inspectUnityStartupStall(logFile, options = {}) {
  const readablePath = resolveExistingLocalPath(logFile);
  if (!readablePath || !fs.existsSync(readablePath)) {
    return { blocking: false, code: null };
  }

  const stat = fs.statSync(readablePath);
  const startTimeMs = options.startTimeMs ?? 0;
  if (stat.mtimeMs + 1000 < startTimeMs) {
    return { blocking: false, code: null };
  }

  const tail = readFileTail(readablePath, 12000);
  const phase = classifyUnityStartupLogTail(tail);
  if (!phase) {
    return { blocking: false, code: null };
  }

  const nowMs = options.now ? options.now().getTime() : Date.now();
  const idleMs = Math.max(0, nowMs - stat.mtimeMs);
  const timeoutMs = options.timeoutMs ?? defaultUnityStartupStallTimeoutMs;
  if (idleMs < timeoutMs) {
    return {
      blocking: false,
      code: null,
      phase: phase.phase,
      evidence: phase.evidence,
      idle_ms: idleMs,
      timeout_ms: timeoutMs,
      log_file: readablePath,
    };
  }

  return {
    blocking: true,
    code: unityStartupStallCode,
    phase: phase.phase,
    evidence: phase.evidence,
    idle_ms: idleMs,
    timeout_ms: timeoutMs,
    log_file: readablePath,
    last_write_utc: new Date(stat.mtimeMs).toISOString(),
  };
}

export function classifyUnityStartupLogTail(logText) {
  const text = String(logText ?? "");
  const markers = [
    { phase: "library_redirect", pattern: /Library Redirect Path:\s*Library\//i },
    { phase: "library_initializing", pattern: /\bLibraryInitializing\b/i },
    { phase: "artifactdb_lock", pattern: /\b(?:ArtifactDB|SourceAssetDB).*(?:lock|locked|LibraryInitializing)/i },
    { phase: "licensing_initialized_no_progress", pattern: /Licensing is initialized[\s\S]*$/i },
    { phase: "bug_reporter_exit_no_progress", pattern: /Exiting without the bug reporter[\s\S]*return code\s+0/i },
  ];

  for (const marker of markers) {
    const match = marker.pattern.exec(text);
    if (match) {
      return {
        phase: marker.phase,
        evidence: match[0].replace(/\s+/g, " ").trim().slice(0, 300),
      };
    }
  }

  return null;
}

export function inspectUnityNativeBlockingModals(options = {}) {
  const result = scanNativeUnityModalWindows(options);
  const blockingCandidates = result.windows.filter((window) => unityNativeBlockingModalCode(window));
  const firstCode = blockingCandidates.length > 0 ? unityNativeBlockingModalCode(blockingCandidates[0]) : null;
  return {
    supported: result.supported,
    blocking: blockingCandidates.length > 0,
    code: firstCode,
    candidates: blockingCandidates,
    windows: result.windows,
    errors: result.errors,
  };
}

export function inspectSameProjectEditorOpen(projectPath, options = {}) {
  const env = options.env ?? process.env;
  const cwd = options.cwd ?? process.cwd();
  const nowMs = options.now ? options.now().getTime() : Date.now();
  const errors = [];
  const warnings = [];
  const canonicalProjectRoot = canonicalizeUnityProjectRoot(projectPath, { env });
  if (!canonicalProjectRoot) {
    return {
      supported: false,
      blocking: false,
      code: null,
      requested_project_path: projectPath,
      canonical_project_root: null,
      registry_instances_dir: null,
      checked_records_count: 0,
      checked_processes_count: 0,
      matches: [],
      process_matches: [],
      errors: ["project_path_canonicalization_failed"],
      warnings,
    };
  }

  const registrySettings = resolveGatewayRegistrySettings({ env, cwd });
  const registryPath = registrySettings.instancesDir;
  if (!registryPath) {
    errors.push("registry_instances_dir_unavailable");
  }

  const records = registryPath ? readGatewayInstanceRecords(registryPath, { errors }) : [];
  const processExists = options.processExists ?? defaultProcessExists;
  const matches = records
    .filter((record) => recordMatchesCanonicalProjectRoot(record, canonicalProjectRoot, { env }))
    .map((record) => sameProjectEditorMatch(record, nowMs, processExists, registrySettings.staleAfterMs));
  const blockingMatches = matches.filter((match) => match.blocking);
  const processScan = options.unityProcessScanner
    ? options.unityProcessScanner()
    : scanUnityEditorProcesses({ execFileSync: options.execFileSync });
  const processRows = Array.isArray(processScan?.processes) ? processScan.processes : [];
  const processMatches = processRows
    .map((processRow) => sameProjectUnityProcessMatch(processRow, canonicalProjectRoot, { env }))
    .filter(Boolean);
  const blockingProcessMatches = processMatches.filter((match) => match.blocking);

  if (processScan && processScan.supported === false) {
    errors.push(...(Array.isArray(processScan.errors) ? processScan.errors : ["unity_process_scan_failed"]));
  }

  if (registryPath && !fs.existsSync(registryPath)) {
    warnings.push("registry_instances_dir_missing");
  }

  const blocking = blockingMatches.length > 0 || blockingProcessMatches.length > 0;
  return {
    supported: Boolean(registryPath) || processScan?.supported === true,
    blocking,
    code: blocking ? sameProjectEditorOpenCode : null,
    requested_project_path: projectPath,
    canonical_project_root: canonicalProjectRoot,
    registry_instances_dir: registryPath,
    registry_stale_after_ms: registrySettings.staleAfterMs,
    checked_records_count: records.length,
    checked_processes_count: processRows.length,
    matches,
    process_matches: processMatches,
    errors,
    warnings,
  };
}

export function scanUnityEditorProcesses(options = {}) {
  const executable = options.execFileSync ?? execFileSync;
  const errors = [];
  for (const candidate of powershellCandidates()) {
    try {
      const output = executable(candidate, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", unityProcessPowerShellScript()], {
        encoding: "utf8",
        timeout: 5000,
        windowsHide: true,
        stdio: "pipe",
      });
      return { supported: true, processes: parseUnityEditorProcessRows(output), errors };
    } catch (error) {
      if (isExecutableMissing(error)) {
        errors.push(`${candidate}: ${error.message}`);
        continue;
      }
      return { supported: false, processes: [], errors: [`unity_process_scan_failed: ${error.message}`] };
    }
  }

  return { supported: false, processes: [], errors: [`unity_process_scan_failed: powershell_not_found${errors.length > 0 ? ` (${errors.join("; ")})` : ""}`] };
}

export function parseUnityEditorProcessRows(output) {
  let parsed;
  try {
    parsed = JSON.parse(String(output || "").trim() || "[]");
  } catch {
    return [];
  }

  const rows = Array.isArray(parsed) ? parsed : [parsed];
  return rows
    .map((row) => {
      if (!row || typeof row !== "object") {
        return undefined;
      }

      const processId = normalizePositiveInteger(row.process_id ?? row.ProcessId ?? row.pid);
      const commandLine = typeof row.command_line === "string"
        ? row.command_line
        : typeof row.CommandLine === "string"
          ? row.CommandLine
          : "";
      const projectPath = parseUnityProjectPathFromCommandLine(commandLine);
      return {
        process_id: processId,
        process_name: typeof row.process_name === "string" ? row.process_name : typeof row.Name === "string" ? row.Name : "Unity.exe",
        command_line: commandLine,
        project_path: projectPath,
      };
    })
    .filter(Boolean);
}

export function parseUnityProjectPathFromCommandLine(commandLine) {
  const tokens = tokenizeWindowsCommandLine(commandLine);
  for (let index = 0; index < tokens.length; index += 1) {
    const token = tokens[index];
    const assignmentMatch = /^-projectpath(?:=|:)(.+)$/i.exec(token);
    if (assignmentMatch) {
      return cleanCommandLineValue(assignmentMatch[1]);
    }

    if (!/^-projectpath$/i.test(token)) {
      continue;
    }

    const projectPathTokens = [];
    for (let valueIndex = index + 1; valueIndex < tokens.length; valueIndex += 1) {
      const value = tokens[valueIndex];
      if (projectPathTokens.length > 0 && /^-[A-Za-z]/.test(value)) {
        break;
      }
      projectPathTokens.push(value);
    }

    return cleanCommandLineValue(projectPathTokens.join(" "));
  }

  return null;
}

function sameProjectUnityProcessMatch(processRow, canonicalProjectRoot, options = {}) {
  const projectPath = typeof processRow?.project_path === "string" && processRow.project_path.length > 0
    ? processRow.project_path
    : parseUnityProjectPathFromCommandLine(processRow?.command_line);
  if (!projectPath) {
    return null;
  }

  const canonicalProcessProjectRoot = canonicalizeUnityProjectRoot(projectPath, options);
  if (!canonicalProcessProjectRoot || !canonicalProjectRootsEqual(canonicalProcessProjectRoot, canonicalProjectRoot)) {
    return null;
  }

  const processId = normalizePositiveInteger(processRow?.process_id ?? processRow?.pid);
  return {
    process_id: processId,
    unity_pid: processId,
    process_name: typeof processRow?.process_name === "string" ? processRow.process_name : "Unity.exe",
    project_path: projectPath,
    canonical_project_root: canonicalProcessProjectRoot,
    command_line: typeof processRow?.command_line === "string" ? processRow.command_line : "",
    blocking: true,
    block_reason: "unity_process_project_path_match",
  };
}

function tokenizeWindowsCommandLine(commandLine) {
  const tokens = [];
  let current = "";
  let quoted = false;

  for (const character of String(commandLine ?? "")) {
    if (character === "\"") {
      quoted = !quoted;
      continue;
    }

    if (!quoted && /\s/.test(character)) {
      if (current.length > 0) {
        tokens.push(current);
        current = "";
      }
      continue;
    }

    current += character;
  }

  if (current.length > 0) {
    tokens.push(current);
  }

  return tokens;
}

function cleanCommandLineValue(value) {
  const trimmed = String(value ?? "").trim().replace(/^['"]|['"]$/g, "");
  return trimmed.length > 0 ? trimmed : null;
}

function normalizePositiveInteger(value) {
  const parsed = Number(value);
  return Number.isInteger(parsed) && parsed > 0 ? parsed : null;
}

export function canonicalizeUnityProjectRoot(projectPath, options = {}) {
  try {
    const normalizedInput = String(projectPath ?? "").replaceAll("\\", "/");
    const mountRoot = wslMountRoot(options);
    const sourceHost = normalizedInput.startsWith(`${mountRoot}/`) ? "wsl" : /^[A-Za-z]:\//.test(normalizedInput) ? "win32" : "posix";
    return trimTrailingSlash(String(normalizeCanonicalPath(normalizedInput, {
      sourceHost,
      targetProfile: "win32-drive",
      wsl: { enabled: true, mountRoot },
    })));
  } catch {
    return undefined;
  }
}

export function isUnityLicensingClientCrashModal(window) {
  const title = String(window?.window_title ?? "").trim();
  const className = String(window?.class_name ?? "");
  const childTexts = Array.isArray(window?.child_texts) ? window.child_texts : [];
  const allText = [title, ...childTexts].join("\n");
  const exceptionCode = /\b0xe0434352\b/i.exec(allText)?.[0]?.toLowerCase();
  const hasDistinctFaultAddress = [...allText.matchAll(/\b0x[0-9a-f]{8,16}\b/gi)]
    .some((match) => match[0].toLowerCase() !== exceptionCode);
  return className === "#32770"
    && /^Unity\.Licensing\.Client\.exe\s+-\s+(?:Application Error|Ошибка приложения)$/iu.test(title)
    && /unknown software exception|Исключение неизвестное программное исключение/iu.test(allText)
    && Boolean(exceptionCode)
    && hasDistinctFaultAddress
    && childTexts.some((text) => typeof text === "string" && /^(?:OK|ОК)$/u.test(text.trim()))
    && childTexts.some((text) => typeof text === "string" && /^(?:Cancel|Отмена)$/u.test(text.trim()));
}

export function isCorruptedLibraryModal(window) {
  const title = String(window?.window_title ?? "").trim();
  const className = String(window?.class_name ?? "");
  const childTexts = Array.isArray(window?.child_texts) ? window.child_texts : [];
  const allText = [title, ...childTexts].join("\n");
  return className === "#32770"
    && /^Corrupted Library Detected$/i.test(title)
    && /Corrupted files in the Library prevented Unity to load your project|rebuild the Library|Don't rebuild Library|Don'?t rebuild Library/i.test(allText);
}

export function isMissingSignatureModal(window) {
  const title = String(window?.window_title ?? "").trim();
  const className = String(window?.class_name ?? "");
  const childTexts = Array.isArray(window?.child_texts) ? window.child_texts : [];
  const allText = [title, ...childTexts].join("\n");
  return (className === "#32770" || className === "UnityContainerWndClass")
    && /^Missing Signature$/i.test(title)
    && (/unsigned\s+packages?|Package Manager|open\s+the\s+Package\s+Manager|\bClose\b|UnityEditor\.PackageManager\.UI\.Internal\.ModalWindowContainer/i.test(allText) || childTexts.length === 0);
}

export function isExternalSceneReloadModal(window) {
  const title = String(window?.window_title ?? "").trim();
  const className = String(window?.class_name ?? "");
  const childTexts = Array.isArray(window?.child_texts) ? window.child_texts.filter((text) => typeof text === "string") : [];
  const allText = [title, ...childTexts].join("\n");
  const completeEvidence = (className === "#32770" || className === "UnityContainerWndClass")
    && title === "The open scene(s) have been modified externally"
    && allText.includes("The following open scene(s) have been changed on disk:")
    && allText.includes("Do you want to reload the scene(s)?")
    && /Assets[\\/][^\r\n]*?\.unity\b/.test(allText)
    && childTexts.some((text) => text.trim() === "Reload")
    && childTexts.some((text) => text.trim() === "Ignore");
  if (completeEvidence) return true;

  const hwnd = String(window?.hwnd ?? "");
  const pid = Number(window?.pid);
  const windowThreadId = Number(window?.window_thread_id);
  const controls = Array.isArray(window?.child_controls) ? window.child_controls : [];
  if ((window?.process_name !== "Unity" && window?.process_name !== "Unity.exe")
    || !/^0x[0-9A-Fa-f]+$/.test(hwnd)
    || !Number.isInteger(pid) || pid <= 0
    || Number.isNaN(Date.parse(String(window?.process_start_utc ?? "")))
    || !Number.isInteger(windowThreadId) || windowThreadId <= 0
    || window?.window_visible !== true || window?.window_enabled !== true
    || className !== "#32770"
    || title !== "The open scene(s) have been modified externally"
    || childTexts.length !== 2 || childTexts[0] !== "Reload" || childTexts[1] !== "Ignore"
    || controls.length !== 2) return false;

  return controls.every((control, index) => control && typeof control === "object"
    && /^0x[0-9A-Fa-f]+$/.test(String(control.hwnd ?? ""))
    && String(control.parent_hwnd ?? "").toLowerCase() === hwnd.toLowerCase()
    && Number(control.pid) === pid
    && Number(control.window_thread_id) === windowThreadId
    && control.visible === true
    && control.enabled === true
    && control.class_name === "Button"
    && control.text === (index === 0 ? "Reload" : "Ignore"));
}

export function isProjectDowngradeModal(window) {
  return projectDowngradeSafeEvidence(window) !== null;
}

export function isDuplicateProjectInstanceModal(window) {
  const title = String(window?.window_title ?? "");
  const className = String(window?.class_name ?? "");
  const processName = String(window?.process_name ?? "");
  const processStartUtc = String(window?.process_start_utc ?? "");
  const windowThreadId = Number(window?.window_thread_id);
  const pid = Number(window?.pid);
  const childTexts = Array.isArray(window?.child_texts) ? window.child_texts.filter((text) => typeof text === "string") : [];
  const controls = Array.isArray(window?.child_controls) ? window.child_controls.filter((control) => control && typeof control === "object" && control.visible !== false) : [];
  if ((processName !== "Unity" && processName !== "Unity.exe")
    || (className !== "#32770" && className !== "UnityContainerWndClass")
    || title !== "Error!"
    || !Number.isInteger(pid) || pid <= 0
    || !Number.isInteger(windowThreadId) || windowThreadId <= 0
    || Number.isNaN(Date.parse(processStartUtc))
    || childTexts.length === 0 || childTexts.length > 32
    || childTexts.some((text) => text.length > 2000)
    || controls.length === 0 || controls.length > 64) return false;

  const body = childTexts.filter((text) => text.trim() !== "OK").join("\n");
  const projectMatches = [...body.matchAll(/(?:^|\s)Project:\s*([A-Za-z]:[\\/][^\r\n]{1,512})$/gm)];
  const projectPath = projectMatches.length === 1 ? projectMatches[0]?.[1]?.trim() : "";
  const buttons = controls.filter((control) => control.class_name === "Button");
  const textControls = controls.filter((control) => typeof control.text === "string" && control.text.length > 0);
  const okButtons = buttons.filter((control) => control.enabled !== false
    && control.text?.trim() === "OK"
    && Number(control.pid) === pid
    && Number(control.window_thread_id) === windowThreadId);
  return body.length <= 16_000
    && body.includes("It looks like another Unity instance is running with this project open!")
    && body.includes("Multiple Unity instances cannot open the same project.")
    && Boolean(projectPath) && !projectPath.includes("<") && !projectPath.includes(">")
    && buttons.length === 1 && okButtons.length === 1
    && textControls.every((control) => Number(control.pid) === pid && Number(control.window_thread_id) === windowThreadId);
}

function projectDowngradeSafeEvidence(window) {
  const title = String(window?.window_title ?? "");
  const className = String(window?.class_name ?? "");
  const processName = String(window?.process_name ?? "");
  const controls = Array.isArray(window?.child_controls) ? window.child_controls : [];
  if (!/^0x[0-9A-Fa-f]+$/.test(window?.hwnd ?? "") || !Number.isInteger(window?.pid) || window.pid <= 0 || controls.length === 0 || controls.length > 64
    || !controls.every((control) => control && typeof control === "object" && /^0x[0-9A-Fa-f]+$/.test(control.hwnd ?? "") && typeof control.class_name === "string" && typeof control.text === "string")) return null;
  const buttons = controls.filter((control) => control?.class_name === "Button");
  const continueButtons = buttons.filter((button) => button.text?.trim() === "Continue" && /^0x[0-9A-Fa-f]+$/.test(button.hwnd ?? ""));
  const quitButtons = buttons.filter((button) => button.text?.trim() === "Quit" && /^0x[0-9A-Fa-f]+$/.test(button.hwnd ?? ""));
  const bodyTexts = controls.filter((control) => control?.class_name !== "Button" && typeof control?.text === "string" && control.text.length > 0).map((control) => control.text);
  if (bodyTexts.length === 0 || bodyTexts.length > 32 || bodyTexts.some((text) => text.length > 2000) || bodyTexts.join("\n").length > 16_000) return null;
  const body = bodyTexts.join("\n");
  const versions = [...new Set([...body.matchAll(/\b\d{1,4}\.\d{1,4}\.\d{1,4}(?:(?:a|b|f|p|rc)\d{1,4})?\b/g)].map((match) => match[0]))];
  if (versions.length !== 2 || compareUnityVersionForDowngrade(versions[0], versions[1]) <= 0) return null;
  const [sourceVersion, targetVersion] = versions;
  const sourceNewer = bodyTexts.some((text) => text.includes(sourceVersion) && /(?:source|project|saved|created|last\s+used)/i.test(text) && /\bnewer\b/i.test(text));
  const targetOlder = bodyTexts.some((text) => text.includes(targetVersion) && /(?:target|current|currently\s+using|opening)/i.test(text) && /\bolder\b/i.test(text));
  const valid = (processName === "Unity" || processName === "Unity.exe")
    && (className === "#32770" || className === "UnityContainerWndClass")
    && title === "Project Downgrade Required"
    && buttons.length === 2
    && continueButtons.length === 1
    && quitButtons.length === 1
    && sourceNewer && targetOlder
    && /\bdowngrad\w*\b[^\r\n.]{0,120}\b(?:is\s+not\s+supported|not\s+supported|unsupported)\b/i.test(body)
    && /\bcontinu(?:e|ing)\b[^\r\n.]{0,120}\bdowngrad\w*\b/i.test(body)
    && /\bassets?\b[^\r\n.]{0,120}\bre-?import\w*\b/i.test(body)
    && (/\bpackages?\b[^\r\n.]{0,160}\bsettings?\b[^\r\n.]{0,160}\b(?:may|might|will)\b[^\r\n.]{0,80}\balso\b[^\r\n.]{0,80}\bdowngrad\w*\b/i.test(body)
      || /\bsettings?\b[^\r\n.]{0,160}\bpackages?\b[^\r\n.]{0,160}\b(?:may|might|will)\b[^\r\n.]{0,80}\balso\b[^\r\n.]{0,80}\bdowngrad\w*\b/i.test(body));
  if (!valid) return null;
  return {
    source_version: sourceVersion,
    target_version: targetVersion,
    button_count: 2,
    continue_button_hwnd: continueButtons[0].hwnd,
    quit_button_hwnd: quitButtons[0].hwnd,
  };
}

function compareUnityVersionForDowngrade(left, right) {
  const parse = (value) => {
    const match = /^(\d{1,4})\.(\d{1,4})\.(\d{1,4})(?:(a|b|rc|f|p)(\d{1,4}))?$/.exec(value ?? "");
    if (!match) return null;
    return [Number(match[1]), Number(match[2]), Number(match[3]), { a: 0, b: 1, rc: 2, f: 3, p: 4 }[match[4] ?? "f"], Number(match[5] ?? 0)];
  };
  const a = parse(left); const b = parse(right);
  if (!a || !b) return 0;
  for (let index = 0; index < a.length; index += 1) {
    if (a[index] !== b[index]) return a[index] > b[index] ? 1 : -1;
  }
  return 0;
}

export function unityNativeBlockingModalCode(window) {
  if (isUnityLicensingClientCrashModal(window)) {
    return unityLicensingClientCrashModalCode;
  }
  if (isCorruptedLibraryModal(window)) {
    return corruptedLibraryModalCode;
  }
  if (isMissingSignatureModal(window)) {
    return missingSignatureModalCode;
  }
  if (isExternalSceneReloadModal(window)) {
    return externalSceneReloadModalCode;
  }
  if (isDuplicateProjectInstanceModal(window)) {
    return duplicateProjectInstanceModalCode;
  }
  if (isProjectDowngradeModal(window)) {
    return projectDowngradeModalCode;
  }
  return null;
}

function resolveGatewayRegistrySettings(options = {}) {
  const env = options.env ?? process.env;
  const config = readGatewayConfig(options);
  const configuredStaleAfterMs = config && Number.isInteger(config?.registry?.stale_after_ms) && config.registry.stale_after_ms > 0
    ? config.registry.stale_after_ms
    : defaultGatewayRegistryStaleAfterMs;

  if (env.SGG_UNITY_MCP_INSTANCES_DIR) {
    return {
      instancesDir: expandEnvPlaceholders(env.SGG_UNITY_MCP_INSTANCES_DIR, env),
      staleAfterMs: configuredStaleAfterMs,
    };
  }

  const instancesDir = config?.registry?.instances_dir;
  return {
    instancesDir: typeof instancesDir === "string" && instancesDir.length > 0 ? expandEnvPlaceholders(instancesDir, env) : undefined,
    staleAfterMs: configuredStaleAfterMs,
  };
}

function readGatewayConfig(options = {}) {
  const configPath = resolveGatewayConfigPath(options);
  if (!configPath || !fs.existsSync(configPath)) {
    return undefined;
  }

  try {
    return JSON.parse(fs.readFileSync(configPath, "utf8"));
  } catch {
    return undefined;
  }
}

function resolveGatewayConfigPath(options = {}) {
  const env = options.env ?? process.env;
  const cwd = options.cwd ?? process.cwd();
  if (env.SGG_UNITY_MCP_CONFIG) {
    return expandEnvPlaceholders(env.SGG_UNITY_MCP_CONFIG, env);
  }

  const localConfig = path.join(cwd, "config", "gateway.json");
  if (fs.existsSync(localConfig)) {
    return localConfig;
  }

  const exampleConfig = path.join(cwd, "config", "gateway.example.json");
  return fs.existsSync(exampleConfig) ? exampleConfig : undefined;
}

function readGatewayInstanceRecords(instancesDir, options = {}) {
  if (!fs.existsSync(instancesDir)) {
    return [];
  }

  const records = [];
  for (const entry of fs.readdirSync(instancesDir, { withFileTypes: true })) {
    if (!entry.isFile() || !entry.name.endsWith(".json")) {
      continue;
    }

    try {
      const parsed = JSON.parse(fs.readFileSync(path.join(instancesDir, entry.name), "utf8"));
      if (parsed && typeof parsed === "object") {
        records.push(parsed);
      }
    } catch (error) {
      options.errors?.push(`invalid_registry_record:${entry.name}:${error.message}`);
    }
  }

  return records;
}

function recordMatchesCanonicalProjectRoot(record, canonicalProjectRoot, options = {}) {
  const candidates = [];
  if (typeof record.canonical_project_root === "string") {
    candidates.push(trimTrailingSlash(record.canonical_project_root));
  }
  if (typeof record.project_root === "string") {
    const canonicalRecordRoot = canonicalizeUnityProjectRoot(record.project_root, options);
    if (canonicalRecordRoot) {
      candidates.push(canonicalRecordRoot);
    }
  }
  if (Array.isArray(record.aliases)) {
    for (const alias of record.aliases) {
      if (typeof alias?.canonical_path === "string") {
        candidates.push(trimTrailingSlash(alias.canonical_path));
      }
      if (typeof alias?.host_path === "string") {
        const canonicalAlias = canonicalizeUnityProjectRoot(alias.host_path, options);
        if (canonicalAlias) {
          candidates.push(canonicalAlias);
        }
      }
    }
  }

  return candidates.some((candidate) => canonicalProjectRootsEqual(candidate, canonicalProjectRoot));
}

function sameProjectEditorMatch(record, nowMs, processExists, staleAfterMs) {
  const lastHeartbeatMs = Date.parse(record.last_heartbeat ?? "");
  const heartbeatAgeMs = Number.isFinite(lastHeartbeatMs) ? Math.max(0, nowMs - lastHeartbeatMs) : null;
  const stale = heartbeatAgeMs === null ? true : heartbeatAgeMs > staleAfterMs;
  const unityPid = Number.isInteger(record.unity_pid) && record.unity_pid > 0 ? record.unity_pid : null;
  const process_alive = unityPid !== null ? processExists(unityPid) : false;
  const blocking = !stale || process_alive;
  return {
    instance_id: typeof record.instance_id === "string" ? record.instance_id : null,
    project: typeof record.project === "string" ? record.project : null,
    display_name: typeof record.display_name === "string" ? record.display_name : null,
    project_root: typeof record.project_root === "string" ? record.project_root : null,
    canonical_project_root: typeof record.canonical_project_root === "string" ? record.canonical_project_root : null,
    unity_pid: unityPid,
    status: typeof record.status === "string" ? record.status : null,
    last_heartbeat: typeof record.last_heartbeat === "string" ? record.last_heartbeat : null,
    heartbeat_age_ms: heartbeatAgeMs,
    stale,
    process_alive,
    blocking,
    block_reason: blocking ? (stale ? "stale_record_process_alive" : "live_heartbeat") : "stale_record_process_dead_or_missing",
  };
}

function canonicalProjectRootsEqual(left, right) {
  const leftValue = trimTrailingSlash(String(left ?? ""));
  const rightValue = trimTrailingSlash(String(right ?? ""));
  if (/^[a-z]:\//i.test(leftValue) && /^[a-z]:\//i.test(rightValue)) {
    return leftValue.toLowerCase() === rightValue.toLowerCase();
  }

  return leftValue === rightValue;
}

function defaultProcessExists(pid) {
  try {
    process.kill(pid, 0);
    return true;
  } catch {
    return false;
  }
}

function printSameProjectEditorBlock(result) {
  const first = result.matches.find((match) => match.blocking);
  const firstProcess = (result.process_matches ?? []).find((match) => match.blocking);
  console.error(`[check-unity-editor] blocked by ${sameProjectEditorOpenCode}: target project is already open in Unity Editor`);
  console.error(`[check-unity-editor] requested project: ${result.requested_project_path}`);
  console.error(`[check-unity-editor] canonical project root: ${result.canonical_project_root}`);
  if (first) {
    console.error(`[check-unity-editor] open editor: instance=${first.instance_id ?? "unknown"} project=${first.project ?? "unknown"} pid=${first.unity_pid ?? "unknown"} status=${first.status ?? "unknown"} stale=${first.stale} process_alive=${first.process_alive}`);
  }
  if (firstProcess) {
    console.error(`[check-unity-editor] unity process: pid=${firstProcess.process_id ?? "unknown"} project_path=${firstProcess.project_path ?? "unknown"} reason=${firstProcess.block_reason}`);
  }
  console.error("[check-unity-editor] close that Unity Editor or validate a separate clone/worktree before retrying batchmode");
}

function scanNativeUnityModalWindows(options = {}) {
  const executable = options.execFileSync ?? execFileSync;
  const errors = [];
  for (const candidate of powershellCandidates()) {
    try {
      const output = executable(candidate, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", nativeUnityModalPowerShellScript()], {
        encoding: "utf8",
        timeout: 5000,
        windowsHide: true,
        stdio: "pipe",
      });
      return { supported: true, windows: parseNativeUnityModalWindows(output), errors };
    } catch (error) {
      if (isExecutableMissing(error)) {
        errors.push(`${candidate}: ${error.message}`);
        continue;
      }
      return { supported: false, windows: [], errors: [`native_modal_scan_failed: ${error.message}`] };
    }
  }

  return { supported: false, windows: [], errors: [`native_modal_scan_failed: powershell_not_found${errors.length > 0 ? ` (${errors.join("; ")})` : ""}`] };
}

export function parseNativeUnityModalWindows(output) {
  let parsed;
  try {
    parsed = JSON.parse(String(output || "").trim() || "[]");
  } catch {
    return [];
  }

  const rows = Array.isArray(parsed) ? parsed : [parsed];
  return rows
    .map((row) => {
      if (!row || typeof row !== "object") {
        return undefined;
      }

      const childTexts = Array.isArray(row.child_texts) ? row.child_texts.filter((value) => typeof value === "string") : [];
      const childControls = Array.isArray(row.child_controls) ? row.child_controls.filter((value) => value && typeof value === "object") : undefined;
      return {
        hwnd: typeof row.hwnd === "string" ? row.hwnd : "",
        pid: Number.isInteger(row.pid) && row.pid > 0 ? row.pid : 0,
        process_name: typeof row.process_name === "string" ? row.process_name : "",
        class_name: typeof row.class_name === "string" ? row.class_name : "",
        window_title: typeof row.window_title === "string" ? row.window_title : "",
        child_texts: childTexts,
        ...(typeof row.window_visible === "boolean" ? { window_visible: row.window_visible } : {}),
        ...(typeof row.window_enabled === "boolean" ? { window_enabled: row.window_enabled } : {}),
        ...(typeof row.process_start_utc === "string" && !Number.isNaN(Date.parse(row.process_start_utc)) ? { process_start_utc: row.process_start_utc } : {}),
        ...(Number.isInteger(row.window_thread_id) && row.window_thread_id > 0 ? { window_thread_id: row.window_thread_id } : {}),
        ...(childControls ? { child_controls: childControls } : {}),
      };
    })
    .filter((window) => window && window.hwnd && window.pid > 0 && window.window_title);
}

function printNativeModalBlock(scope, result) {
  const first = result.candidates[0];
  console.error(`[check-unity-editor] blocked by ${result.code ?? unityLicensingClientCrashModalCode} during ${scope}`);
  if (first) {
    console.error(`[check-unity-editor] native modal: hwnd=${first.hwnd} pid=${first.pid} process=${first.process_name || "unknown"} class=${first.class_name} title=${JSON.stringify(first.window_title)}`);
    if (result.code === projectDowngradeModalCode) {
      const evidence = projectDowngradeSafeEvidence(first);
      if (evidence) console.error(`[check-unity-editor] project downgrade evidence: source_version=${evidence.source_version} target_version=${evidence.target_version} button_count=${evidence.button_count} continue_hwnd=${evidence.continue_button_hwnd} quit_hwnd=${evidence.quit_button_hwnd}`);
    } else {
      const message = first.child_texts.find((text) => /0xe0434352|unknown software exception|Исключение неизвестное программное исключение|Corrupted files in the Library|rebuild the Library|Don't rebuild Library|Don'?t rebuild Library|unsigned packages|Package Manager|open scene\(s\).*changed on disk|Assets[\\/].*\.unity|reload the scene\(s\)/i.test(text));
      if (message) console.error(`[check-unity-editor] native modal message: ${message.replace(/\s+/g, " ").trim()}`);
    }
  }
  if (result.code === corruptedLibraryModalCode) {
    console.error("[check-unity-editor] close the exact Corrupted Library dialog or choose Don't rebuild Library, then retry after reviewing the project cache state; this guard does not rebuild Library automatically");
  } else if (result.code === missingSignatureModalCode) {
    console.error("[check-unity-editor] close only the exact Unity Missing Signature package prompt after operator review, then retry and verify compile/package status; this guard does not open Package Manager or mark unsigned packages trusted automatically");
  } else if (result.code === externalSceneReloadModalCode) {
    console.error("[check-unity-editor] ask the operator to choose Reload or Ignore for the exact matched external-scene dialog, then retry validation only after the modal is gone; this guard does not click either button and does not retry write commands, and sparse evidence does not claim an unobserved body or scene paths");
  } else if (result.code === projectDowngradeModalCode) {
    console.error("[check-unity-editor] review the exact Project Downgrade Required warning with the operator; this guard never clicks Continue or Quit and never infers downgrade completion");
  } else if (result.code === duplicateProjectInstanceModalCode) {
    console.error("[check-unity-editor] inspect the Unity processes and project roots, then ask the operator to close the exact duplicate-project dialog or unintended Editor; this guard never clicks OK, terminates Unity, deletes project locks, or launches another Editor automatically");
  } else {
    console.error("[check-unity-editor] close the exact Unity Licensing Client error dialog and retry; this guard does not auto-click native application error dialogs");
  }
}

function printStartupStallBlock(scope, result, logFile) {
  console.error(`[check-unity-editor] blocked by ${result.code ?? unityStartupStallCode} during ${scope}`);
  console.error(`[check-unity-editor] startup phase: ${result.phase ?? "unknown"} idle_ms=${result.idle_ms ?? "unknown"} timeout_ms=${result.timeout_ms ?? "unknown"}`);
  if (result.evidence) {
    console.error(`[check-unity-editor] startup evidence: ${String(result.evidence).replace(/\s+/g, " ").trim()}`);
  }
  console.error(`[check-unity-editor] log: ${result.log_file ?? logFile}`);
  const tail = readFileTail(result.log_file ?? logFile, 12000);
  if (tail) {
    console.error("[check-unity-editor] recent log tail:");
    console.error(tail);
  }
  console.error("[check-unity-editor] inspect Unity startup state, stale locks and LibraryInitializing markers before retrying; no automatic Library cleanup was performed");
}

function powershellCandidates() {
  if (process.platform === "win32") {
    return ["powershell.exe", "C:\\Windows\\System32\\WindowsPowerShell\\v1.0\\powershell.exe"];
  }

  return ["powershell.exe", "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe"];
}

function isExecutableMissing(error) {
  return error && typeof error === "object" && error.code === "ENOENT";
}

function expandEnvPlaceholders(value, env = process.env) {
  return String(value).replaceAll(/%([A-Za-z_][A-Za-z0-9_]*)%/g, (_match, name) => env[name] ?? `%${name}%`);
}

function trimTrailingSlash(input) {
  if (/^[a-z]:\/$/i.test(input) || input === "/") {
    return input;
  }

  return String(input).replaceAll(/\/+$/g, "");
}

function nativeUnityModalPowerShellScript() {
  return `
$ErrorActionPreference = 'Stop'
$source = @"
using System;
using System.Text;
using System.Runtime.InteropServices;

public static class SGGUnityEditorCheckNativeModalUser32 {
  public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr hWnd, EnumWindowsProc lpEnumFunc, IntPtr lParam);
  [DllImport("user32.dll")] public static extern int GetWindowText(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern int GetClassName(IntPtr hWnd, StringBuilder text, int count);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool IsWindowEnabled(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr hWnd);
}
"@
Add-Type -TypeDefinition $source

function Get-WindowTextValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 1024
  [void][SGGUnityEditorCheckNativeModalUser32]::GetWindowText($handle, $builder, $builder.Capacity)
  return $builder.ToString()
}

function Get-ClassNameValue([IntPtr]$handle) {
  $builder = New-Object System.Text.StringBuilder 256
  [void][SGGUnityEditorCheckNativeModalUser32]::GetClassName($handle, $builder, $builder.Capacity)
  return $builder.ToString()
}

$titlePattern = '(?i)(^Unity\\.Licensing\\.Client\\.exe\\s+-\\s+.+$|^Corrupted Library Detected$|^Missing Signature$|^The open scene\\(s\\) have been modified externally$|^Project Downgrade Required$|^Error!$)'
$results = New-Object System.Collections.Generic.List[object]

[void][SGGUnityEditorCheckNativeModalUser32]::EnumWindows([SGGUnityEditorCheckNativeModalUser32+EnumWindowsProc]{
  param([IntPtr]$handle, [IntPtr]$lparam)
  if (-not [SGGUnityEditorCheckNativeModalUser32]::IsWindowVisible($handle)) { return $true }
  $title = Get-WindowTextValue $handle
  if (-not $title -or $title -notmatch $titlePattern) { return $true }

  [uint32]$windowPid = 0
  $windowThreadId = [SGGUnityEditorCheckNativeModalUser32]::GetWindowThreadProcessId($handle, [ref]$windowPid)
  $processName = ''
  $processStartUtc = ''
  try {
    $process = Get-Process -Id ([int]$windowPid) -ErrorAction Stop
    $processName = $process.ProcessName
    $processStartUtc = $process.StartTime.ToUniversalTime().ToString('o')
  } catch {
    $processName = ''
    $processStartUtc = ''
  }

  $childTexts = New-Object System.Collections.Generic.List[string]
  $childControls = New-Object System.Collections.Generic.List[object]
  [void][SGGUnityEditorCheckNativeModalUser32]::EnumChildWindows($handle, [SGGUnityEditorCheckNativeModalUser32+EnumWindowsProc]{
    param([IntPtr]$childHandle, [IntPtr]$childParam)
    $text = Get-WindowTextValue $childHandle
    [uint32]$childPid = 0
    $childThreadId = [SGGUnityEditorCheckNativeModalUser32]::GetWindowThreadProcessId($childHandle, [ref]$childPid)
    $visible = [SGGUnityEditorCheckNativeModalUser32]::IsWindowVisible($childHandle)
    $enabled = [SGGUnityEditorCheckNativeModalUser32]::IsWindowEnabled($childHandle)
    $childControls.Add([pscustomobject]@{ hwnd = ('0x{0:X}' -f $childHandle.ToInt64()); parent_hwnd = ('0x{0:X}' -f [SGGUnityEditorCheckNativeModalUser32]::GetParent($childHandle).ToInt64()); pid = [int]$childPid; window_thread_id = [int]$childThreadId; visible = $visible; enabled = $enabled; class_name = Get-ClassNameValue $childHandle; text = $text }) | Out-Null
    if ($visible -and $text) {
      $childTexts.Add($text) | Out-Null
    }
    return $true
  }, [IntPtr]::Zero)

  $results.Add([pscustomobject]@{
    hwnd = ('0x{0:X}' -f $handle.ToInt64())
    pid = [int]$windowPid
    process_name = $processName
    process_start_utc = $processStartUtc
    window_thread_id = [int]$windowThreadId
    class_name = Get-ClassNameValue $handle
    window_title = $title
    window_visible = [SGGUnityEditorCheckNativeModalUser32]::IsWindowVisible($handle)
    window_enabled = [SGGUnityEditorCheckNativeModalUser32]::IsWindowEnabled($handle)
    child_texts = @($childTexts)
    child_controls = @($childControls)
  }) | Out-Null
  return $true
}, [IntPtr]::Zero)

$results | ConvertTo-Json -Compress -Depth 6
`.trim();
}

function unityProcessPowerShellScript() {
  return `
$ErrorActionPreference = 'Stop'
$processes = @(Get-CimInstance Win32_Process -Filter "Name = 'Unity.exe'" | ForEach-Object {
  [pscustomobject]@{
    process_id = [int]$_.ProcessId
    process_name = $_.Name
    command_line = [string]$_.CommandLine
  }
})
if ($processes.Count -eq 0) {
  '[]'
} else {
  $processes | ConvertTo-Json -Compress -Depth 4
}
`.trim();
}

function terminateSpawnedProcess(child) {
  if (!child || typeof child.kill !== "function") {
    return false;
  }

  try {
    const killed = child.kill("SIGTERM");
    setTimeout(() => {
      try {
        child.kill("SIGKILL");
      } catch {
        // Best-effort timeout cleanup; diagnostics should still return promptly.
      }
    }, 1000).unref?.();
    return killed;
  } catch {
    return false;
  }
}

export function readFileTail(filePath, maxChars) {
  if (!filePath) {
    return "";
  }

  const readablePath = resolveExistingLocalPath(filePath);
  if (!fs.existsSync(readablePath)) {
    return "";
  }

  const stat = fs.statSync(readablePath);
  const maxBytes = Math.min(stat.size, Math.max(1, maxChars) * 4);
  const fd = fs.openSync(readablePath, "r");
  try {
    const buffer = Buffer.alloc(maxBytes);
    fs.readSync(fd, buffer, 0, maxBytes, Math.max(0, stat.size - maxBytes));
    return buffer.toString("utf8").slice(-maxChars);
  } finally {
    fs.closeSync(fd);
  }
}

function fileExistsLocal(filePath) {
  return fs.existsSync(resolveExistingLocalPath(filePath));
}

function resolveExistingLocalPath(filePath) {
  if (fs.existsSync(filePath)) {
    return filePath;
  }

  const unityEditorPath = toUnityEditorFsPath(filePath);
  if (unityEditorPath !== filePath && fs.existsSync(unityEditorPath)) {
    return unityEditorPath;
  }

  return filePath;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exit(await runUnityEditorValidation(process.env, process.cwd(), { required: process.argv.includes("--required") }));
}

export function toLocalFsPath(value, options = {}) {
  if (hasWin32DriveRoot(value) && (options.hostPlatform ?? process.platform) !== "win32") {
    const canonical = normalizeCanonicalPath(value, { sourceHost: "win32", targetProfile: "win32-drive" });
    return canonicalToWSL(canonical, { mountRoot: wslMountRoot(options) });
  }

  return value;
}

export function joinLocalFsPath(root, ...segments) {
  if (typeof root === "string" && root.replaceAll("\\", "/").startsWith("/")) {
    return path.posix.join(root.replaceAll("\\", "/"), ...segments);
  }

  return path.join(root, ...segments);
}

export function toUnityEditorFsPath(value, options = {}) {
  const normalized = value.replaceAll("\\", "/");
  const mountRoot = wslMountRoot(options);
  if (normalized === mountRoot || normalized.startsWith(`${mountRoot}/`)) {
    const canonical = normalizeCanonicalPath(normalized, {
      sourceHost: "wsl",
      targetProfile: "win32-drive",
      wsl: { enabled: true, mountRoot },
    });
    return canonicalToWin32(canonical);
  }

  return value;
}

function hasWin32DriveRoot(value) {
  return /^[A-Za-z]:[\\/]/.test(value);
}

function wslMountRoot(options = {}) {
  const raw = options.wslMountRoot ?? options.env?.SGG_UNITY_WSL_MOUNT_ROOT ?? "/mnt";
  const normalized = String(raw || "/mnt").replaceAll("\\", "/").replace(/\/+$/g, "");
  return normalized.startsWith("/") ? normalized : "/mnt";
}
