#!/usr/bin/env node

import { spawnSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { pathToFileURL } from "node:url";

const unityVersionRegex = /(?:^|[^0-9])((?:20[0-9]{2}|6000)\.[0-9]+\.[0-9]+(?:[abfp][0-9]+)?)(?:[^0-9]|$)/;
const maxDiagnosticChars = 600;

export function discoverUnityEditors(options = {}) {
  const env = options.env ?? process.env;
  const platform = options.platform ?? process.platform;
  const fsImpl = options.fs ?? fs;
  const runCommand = options.runCommand ?? ((command, args) => runExternalCommand(command, args, env, options.cwd ?? process.cwd()));
  const probes = [];

  if (options.skipUnityCli !== true) {
    const unityCli = stringValue(env.SGG_UNITY_CLI) || "unity";
    const result = discoverWithUnityCli(unityCli, { fs: fsImpl, runCommand });
    probes.push(result.probe);
    if (result.editors.length > 0) {
      return discoveryResult(result.editors, probes);
    }
  }

  if (options.skipHubCli !== true) {
    const hubResult = discoverWithHubCli({ env, fs: fsImpl, platform, runCommand });
    probes.push(...hubResult.probes);
    if (hubResult.editors.length > 0) {
      return discoveryResult(hubResult.editors, probes);
    }
  }

  const roots = options.editorRoots ?? parseEditorRoots(env.SGG_UNITY_EDITOR_ROOTS, platform);
  const rootResult = discoverWithEditorRoots(roots, { fs: fsImpl });
  probes.push(...rootResult.probes);
  return discoveryResult(rootResult.editors, probes);
}

export function findUnityEditor(editors, versionSelector) {
  const selector = stringValue(versionSelector);
  if (!selector) {
    return undefined;
  }

  const sorted = [...editors].sort(compareEditorsDescending);
  return sorted.find((editor) => editor.version === selector)
    ?? sorted.find((editor) => editor.version.startsWith(`${selector}.`))
    ?? sorted.find((editor) => editor.version.startsWith(selector));
}

export function parseUnityCliEditorsJson(text, source = "unity_cli", fsImpl = fs) {
  let parsed;
  try {
    parsed = JSON.parse(text);
  } catch {
    return [];
  }

  const editors = [];
  for (const value of collectObjects(parsed)) {
    const rawVersion = firstString(value, ["version", "editorVersion", "editor_version", "displayVersion", "name", "id"]);
    const rawPath = firstString(value, ["executable_path", "executablePath", "executable", "path", "location", "install_path", "installPath", "directory"]);
    const editor = normalizeEditorCandidate({ version: rawVersion, path: rawPath, source, fs: fsImpl });
    if (editor) {
      editors.push(editor);
    }
  }

  return dedupeEditors(editors);
}

export function parseEditorListText(text, source = "hub_cli", fsImpl = fs) {
  const editors = [];
  for (const rawLine of text.split(/\r?\n/)) {
    const line = rawLine.trim();
    if (!line) {
      continue;
    }

    const version = extractUnityVersion(line);
    if (!version) {
      continue;
    }

    const editorPath = extractEditorPathFromLine(line, version);
    const editor = normalizeEditorCandidate({ version, path: editorPath, source, fs: fsImpl });
    if (editor) {
      editors.push(editor);
    }
  }

  return dedupeEditors(editors);
}

export function scanUnityEditorRoots(roots, options = {}) {
  const fsImpl = options.fs ?? fs;
  const editors = [];
  const probes = [];

  for (const root of roots.map((value) => value.trim()).filter(Boolean)) {
    if (!fsImpl.existsSync(root)) {
      probes.push({ source: "editor_root", root, status: "missing" });
      continue;
    }

    let entries;
    try {
      entries = fsImpl.readdirSync(root, { withFileTypes: true });
    } catch (error) {
      probes.push({ source: "editor_root", root, status: "error", error: boundedError(error) });
      continue;
    }

    for (const entry of entries) {
      if (!entry.isDirectory() || extractUnityVersion(entry.name) !== entry.name) {
        continue;
      }

      const installPath = joinPathLike(root, entry.name);
      const executablePath = findUnityExecutable(installPath, fsImpl);
      if (!executablePath) {
        continue;
      }

      editors.push(editorRecord(entry.name, executablePath, installPath, "editor_root", fsImpl));
    }

    probes.push({ source: "editor_root", root, status: "ok", editors_found: editors.length });
  }

  return { editors: dedupeEditors(editors), probes };
}

export function parseEditorRoots(value, platform = process.platform) {
  const raw = stringValue(value);
  if (!raw) {
    return [];
  }

  const newlineOrSemicolonParts = raw.split(/[;\n\r]+/).map((part) => part.trim()).filter(Boolean);
  if (newlineOrSemicolonParts.length > 1 || /^[A-Za-z]:[\\/]/.test(raw)) {
    return newlineOrSemicolonParts.length > 0 ? newlineOrSemicolonParts : [raw.trim()];
  }

  const delimiter = platform === "win32" ? ";" : path.delimiter;
  return raw.split(delimiter).map((part) => part.trim()).filter(Boolean);
}

export function runUnityEditorDiscoveryCli(args = process.argv.slice(2), options = {}) {
  let json = true;
  let versionSelector;
  let roots;
  let skipUnityCli = options.skipUnityCli === true;
  let skipHubCli = options.skipHubCli === true;

  try {
    for (let index = 0; index < args.length; index += 1) {
      const arg = args[index];
      if (arg === "--json") {
        json = true;
      } else if (arg === "--text") {
        json = false;
      } else if (arg === "--version") {
        versionSelector = requireValue(args, index += 1, arg);
      } else if (arg === "--roots") {
        roots = parseEditorRoots(requireValue(args, index += 1, arg), options.platform ?? process.platform);
      } else if (arg === "--skip-unity-cli") {
        skipUnityCli = true;
      } else if (arg === "--skip-hub-cli") {
        skipHubCli = true;
      } else if (arg === "--roots-only") {
        skipUnityCli = true;
        skipHubCli = true;
      } else if (arg === "--help" || arg === "-h") {
        writeOutput(options.stdout, usageText());
        return 0;
      } else {
        throw new Error(`Unknown argument: ${arg}`);
      }
    }
  } catch (error) {
    writeOutput(options.stderr ?? options.stdout, `[unity-editor-discovery] ${boundedError(error)}\n`);
    return 1;
  }

  const discovery = discoverUnityEditors({ ...options, skipUnityCli, skipHubCli, ...(roots ? { editorRoots: roots } : {}) });
  const editors = versionSelector ? discovery.editors.filter((editor) => findUnityEditor([editor], versionSelector) !== undefined) : discovery.editors;
  const payload = { ...discovery, editors, status: editors.length > 0 ? "ok" : "not_found" };

  if (json) {
    writeOutput(options.stdout, `${JSON.stringify(payload, null, 2)}\n`);
  } else {
    for (const editor of editors) {
      writeOutput(options.stdout, `${editor.version}\t${editor.executable_path}\t${editor.source}\n`);
    }
    if (editors.length === 0) {
      writeOutput(options.stdout, "No Unity Editors discovered. Set SGG_UNITY_EDITOR_ROOTS or install the Unity CLI.\n");
    }
  }

  return payload.status === "ok" ? 0 : 1;
}

function discoverWithUnityCli(command, options) {
  const args = ["editors", "-i", "--format", "json"];
  const result = options.runCommand(command, args);
  const baseProbe = { source: "unity_cli", command, args };
  if (!commandSucceeded(result)) {
    return { editors: [], probe: { ...baseProbe, status: "error", error: commandError(result) } };
  }

  const editors = parseUnityCliEditorsJson(result.stdout ?? "", "unity_cli", options.fs);
  const fallbackEditors = editors.length > 0 ? editors : parseEditorListText(result.stdout ?? "", "unity_cli", options.fs);
  return { editors: fallbackEditors, probe: { ...baseProbe, status: fallbackEditors.length > 0 ? "ok" : "empty", editors_found: fallbackEditors.length } };
}

function discoverWithHubCli(options) {
  const probes = [];
  for (const command of hubCommandCandidates(options.env, options.fs, options.platform)) {
    for (const args of hubCliArgVariants(options.platform)) {
      const result = options.runCommand(command, args);
      const probe = { source: "hub_cli", command, args };
      if (!commandSucceeded(result)) {
        probes.push({ ...probe, status: "error", error: commandError(result) });
        continue;
      }

      const jsonEditors = parseUnityCliEditorsJson(result.stdout ?? "", "hub_cli", options.fs);
      const editors = jsonEditors.length > 0 ? jsonEditors : parseEditorListText(result.stdout ?? "", "hub_cli", options.fs);
      probes.push({ ...probe, status: editors.length > 0 ? "ok" : "empty", editors_found: editors.length });
      if (editors.length > 0) {
        return { editors, probes };
      }
    }
  }

  return { editors: [], probes };
}

function discoverWithEditorRoots(roots, options) {
  if (roots.length === 0) {
    return { editors: [], probes: [{ source: "editor_root", status: "not_configured", env_var: "SGG_UNITY_EDITOR_ROOTS" }] };
  }

  return scanUnityEditorRoots(roots, options);
}

function hubCommandCandidates(env, fsImpl, platform) {
  const explicit = [stringValue(env.SGG_UNITY_HUB), stringValue(env.UNITY_HUB_EXE)].filter(Boolean);
  const candidates = [...explicit, "Unity Hub", "unityhub"];

  if (platform === "darwin") {
    candidates.push("/Applications/Unity Hub.app/Contents/MacOS/Unity Hub");
  } else if (platform === "win32") {
    candidates.push("C:\\Program Files\\Unity Hub\\Unity Hub.exe");
  } else {
    candidates.push("/opt/unityhub/unityhub", "/usr/bin/unityhub", "/mnt/c/Program Files/Unity Hub/Unity Hub.exe");
  }

  return [...new Set(candidates.filter((candidate) => !candidate.includes(path.sep) || fsImpl.existsSync(candidate) || explicit.includes(candidate)))];
}

function hubCliArgVariants(platform) {
  const withSeparator = ["--", "--headless", "editors", "--installed"];
  const linuxStyle = ["--headless", "editors", "--installed"];
  return platform === "linux" ? [linuxStyle, withSeparator] : [withSeparator, linuxStyle];
}

function normalizeEditorCandidate(input) {
  const rawPath = stringValue(input.path);
  const rawVersion = stringValue(input.version);
  const version = extractUnityVersion(rawVersion) ?? extractUnityVersion(rawPath);
  if (!version || !rawPath) {
    return undefined;
  }

  const installPath = inferInstallPath(rawPath, version);
  const executablePath = inferExecutablePath(rawPath, installPath, input.fs);
  if (!executablePath) {
    return undefined;
  }

  return editorRecord(version, executablePath, installPath, input.source, input.fs);
}

function editorRecord(version, executablePath, installPath, source, fsImpl) {
  return {
    version,
    executable_path: executablePath,
    install_path: installPath,
    source,
    exists: fsImpl.existsSync(executablePath),
  };
}

function inferInstallPath(rawPath, version) {
  const normalized = trimPath(rawPath);
  const normalizedSlashes = normalized.replaceAll("\\", "/");
  const macIndex = normalizedSlashes.indexOf("/Unity.app");
  if (macIndex >= 0) {
    return normalized.slice(0, macIndex);
  }

  const versionIndex = normalizedSlashes.indexOf(`/${version}/`);
  if (versionIndex >= 0) {
    return normalized.slice(0, versionIndex + version.length + 1);
  }

  const relativeVersionIndex = normalizedSlashes.indexOf(`${version}/`);
  if (relativeVersionIndex === 0) {
    return normalized.slice(0, version.length);
  }

  if (basenameAny(dirnameAny(normalized)) === "Editor" && basenameAny(normalized).toLowerCase().startsWith("unity")) {
    return dirnameAny(dirnameAny(normalized));
  }

  if (basenameAny(normalized) === version) {
    return normalized;
  }

  return normalized;
}

function inferExecutablePath(rawPath, installPath, fsImpl) {
  const trimmed = trimPath(rawPath);
  const basename = basenameAny(trimmed).toLowerCase();
  if (basename === "unity.exe" || basename === "unity") {
    return trimmed;
  }

  if (basename === "unity.app") {
    return joinPathLike(trimmed, "Contents", "MacOS", "Unity");
  }

  return findUnityExecutable(installPath, fsImpl) ?? joinPathLike(installPath, "Editor", "Unity.exe");
}

function findUnityExecutable(installPath, fsImpl) {
  const candidates = [
    joinPathLike(installPath, "Editor", "Unity.exe"),
    joinPathLike(installPath, "Editor", "Unity"),
    joinPathLike(installPath, "Unity.app", "Contents", "MacOS", "Unity"),
  ];
  return candidates.find((candidate) => fsImpl.existsSync(candidate));
}

function joinPathLike(base, ...segments) {
  const separator = /^[A-Za-z]:[\\/]/.test(base) || base.includes("\\") ? "\\" : "/";
  return [base.replace(/[\\/]+$/g, ""), ...segments].join(separator);
}

function basenameAny(value) {
  return value.replace(/[\\/]+$/g, "").replaceAll("\\", "/").split("/").pop() ?? value;
}

function dirnameAny(value) {
  const trimmed = value.replace(/[\\/]+$/g, "");
  const slash = Math.max(trimmed.lastIndexOf("/"), trimmed.lastIndexOf("\\"));
  return slash > 0 ? trimmed.slice(0, slash) : ".";
}

function extractEditorPathFromLine(line, version) {
  const atMatch = /\bat\s+(.+)$/i.exec(line);
  if (atMatch?.[1]) {
    return trimPath(atMatch[1]);
  }

  const cells = line.split(/\t| {2,}/).map((cell) => cell.trim()).filter(Boolean);
  for (const cell of cells.slice().reverse()) {
    if (looksLikeEditorPath(cell)) {
      return trimPath(cell);
    }
  }

  const windowsMatch = /([A-Za-z]:[\\/].+)$/i.exec(line);
  if (windowsMatch?.[1]) {
    return trimPath(windowsMatch[1]);
  }

  const versionIndex = line.indexOf(version);
  if (versionIndex >= 0) {
    const afterVersion = line.slice(versionIndex + version.length).replace(/\b(installed|recommended|default|lts|editor|path)\b/gi, "").trim();
    if (looksLikeEditorPath(afterVersion)) {
      return trimPath(afterVersion);
    }
  }

  return undefined;
}

function looksLikeEditorPath(value) {
  return /^[A-Za-z]:[\\/]/.test(value) || value.startsWith("/") || value.includes("Unity.app");
}

function trimPath(value) {
  return value.trim().replace(/^['"]|['"]$/g, "").replace(/[.,;]$/g, "");
}

function collectObjects(value) {
  if (Array.isArray(value)) {
    return value.flatMap((entry) => collectObjects(entry));
  }

  if (!value || typeof value !== "object") {
    return [];
  }

  const nested = Object.values(value).flatMap((entry) => collectObjects(entry));
  return [value, ...nested];
}

function firstString(value, keys) {
  for (const key of keys) {
    const candidate = value[key];
    if (typeof candidate === "string" && candidate.trim().length > 0) {
      return candidate;
    }
  }

  return undefined;
}

function extractUnityVersion(value) {
  const match = unityVersionRegex.exec(value ?? "");
  return match?.[1];
}

function discoveryResult(editors, probes) {
  const deduped = dedupeEditors(editors);
  return {
    status: deduped.length > 0 ? "ok" : "not_found",
    editors: deduped,
    probes,
  };
}

function dedupeEditors(editors) {
  const byKey = new Map();
  for (const editor of editors) {
    byKey.set(`${editor.version}\0${editor.executable_path}`, editor);
  }

  return [...byKey.values()].sort(compareEditorsDescending);
}

function compareEditorsDescending(left, right) {
  return right.version.localeCompare(left.version, undefined, { numeric: true, sensitivity: "base" }) || left.executable_path.localeCompare(right.executable_path);
}

function runExternalCommand(command, args, env, cwd) {
  const result = spawnSync(command, args, {
    cwd,
    env,
    encoding: "utf8",
    windowsHide: true,
  });
  return {
    status: result.status,
    stdout: result.stdout ?? "",
    stderr: result.stderr ?? "",
    error: result.error,
  };
}

function commandSucceeded(result) {
  return !result.error && result.status === 0;
}

function commandError(result) {
  if (result.error) {
    return boundedError(result.error);
  }

  return bounded((result.stderr || result.stdout || `exit ${result.status}`).trim());
}

function boundedError(error) {
  return bounded(error instanceof Error ? error.message : String(error));
}

function bounded(value) {
  return value.length > maxDiagnosticChars ? `${value.slice(0, maxDiagnosticChars)}...` : value;
}

function stringValue(value) {
  return typeof value === "string" ? value.trim() : "";
}

function requireValue(args, index, flag) {
  const value = args[index];
  if (!value || value.startsWith("--")) {
    throw new Error(`${flag} requires a value`);
  }
  return value;
}

function writeOutput(writer, text) {
  if (writer) {
    writer(text);
  } else {
    process.stdout.write(text);
  }
}

function usageText() {
  return `Usage: node scripts/unity-editor-discovery.mjs [--json|--text] [--version <selector>] [--roots <paths>] [--roots-only]\n\nDiscovers installed Unity Editors through, in order: standalone Unity CLI, Unity Hub CLI, and explicit SGG_UNITY_EDITOR_ROOTS.\nEnvironment: SGG_UNITY_CLI, SGG_UNITY_HUB, UNITY_HUB_EXE, SGG_UNITY_EDITOR_ROOTS.\n`;
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) {
  process.exit(runUnityEditorDiscoveryCli());
}
