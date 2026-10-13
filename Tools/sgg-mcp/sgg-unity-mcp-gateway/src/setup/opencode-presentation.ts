import fs from "node:fs";
import { createHash, randomUUID } from "node:crypto";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { applyEdits, createScanner, findNodeAtLocation, modify, parse, parseTree, SyntaxKind, visit, type FormattingOptions, type ParseError } from "jsonc-parser/lib/esm/main.js";
import { CanonicalPathService } from "../paths/canonical-path.js";

const OWNED_PLUGIN_SPEC = "./.opencode/sgg-unity-presentation";
const ARTIFACT_DIRECTORY = "sgg-unity-presentation";
const MANIFEST_FILE = "sgg-plugin-manifest.json";
const PACKAGE_FILE = "package.json";
const SERVER_FILE = "server.js";
const ARTIFACT_FILES = [PACKAGE_FILE, SERVER_FILE, MANIFEST_FILE] as const;
const canonicalPaths = new CanonicalPathService();
const MANIFEST_CONTENT_FILES = [PACKAGE_FILE, SERVER_FILE] as const;
const ARTIFACT_META_FILES = ARTIFACT_FILES.map((name) => `${name}.meta`);
const PLUGIN_ID = "sgg-unity.presentation";
const PACKAGE_NAME = "@sungeargames/opencode-sgg-unity-presentation";
const MAX_CONFIG_BYTES = 1024 * 1024;
const MAX_MANIFEST_BYTES = 64 * 1024;
const MAX_PACKAGE_BYTES = 64 * 1024;
const MAX_SERVER_BYTES = 2 * 1024 * 1024;
const TRANSACTION_FILE = ".sgg-unity-presentation-transaction.json";
const MUTATION_LOCK_FILE = ".sgg-unity-presentation.lock";
const MAX_CONTROL_FILE_BYTES = 16 * 1024;

type PresentationAction = "status" | "dry-run" | "write" | "remove";
type RecoveryOutcome = "none" | "finalized" | "rolled_back";
type ArtifactState = "absent" | "current" | "outdated" | "modified" | "unknown";
type ConfigState = "missing" | "not_configured" | "configured" | "duplicate_owned_entries" | "conflict";

export type OpencodePresentationSetupOptions = {
  workspace?: string;
  action: PresentationAction;
  json: boolean;
  help: boolean;
};

export type OpencodePresentationSetupIo = {
  stdout?: (text: string) => void;
  stderr?: (text: string) => void;
  cwd?: string;
  artifactRoot?: string;
};

type ArtifactManifest = {
  schema_version: 1;
  plugin_id: string;
  version: string;
  opencode_range: string;
  trusted_previous_manifest_sha256: string[];
  files: Array<{ path: string; bytes: number; sha256: string }>;
};

type LoadedArtifact = {
  root: string;
  manifest: ArtifactManifest;
  files: Map<string, Buffer>;
  hash: string;
};

type ArtifactInspection = {
  state: ArtifactState;
  installedVersion?: string;
  fingerprint: string;
};

type ConfigInspection = {
  path: string;
  file: string;
  exists: boolean;
  state: ConfigState;
  source: string;
  ownedEntries: number;
  legacyOwnedEntries?: number;
  fingerprint: string;
  blocker?: string;
};

type WorkspaceBinding = {
  requested: string;
  canonical: string;
  device: bigint;
  inode: bigint;
};

type DirectoryBinding = {
  path: string;
  canonical: string;
  device: bigint;
  inode: bigint;
  label: "workspace" | "workspace .opencode";
};

type PresentationInspection = {
  workspace: WorkspaceBinding;
  configRoot: string;
  opencodeRoot: string;
  artifactPath: string;
  artifact: ArtifactInspection;
  config: ConfigInspection;
  blockers: string[];
};

type PresentationPlan = {
  schema_version: 1;
  setup: "opencode-presentation";
  action: PresentationAction;
  workspace: string;
  artifact: {
    state: ArtifactState;
    bundled_version: string;
    installed_version: string | null;
    bundled_hash: string;
  };
  config: {
    file: string;
    state: ConfigState;
    owned_entries: number;
  };
  changes: string[];
  blocked_reasons: string[];
  recovery: RecoveryOutcome;
  restart_required: boolean;
  applied: boolean;
};

type Transaction = {
  artifactOperation: "none" | "install" | "remove";
  configText?: string;
};

type TransactionRecord = {
  schema_version: 2;
  nonce: string;
  artifact_operation: Transaction["artifactOperation"];
  artifact_original_state: ArtifactState;
  artifact_original_fingerprint: string;
  config_file: "opencode.json" | "opencode.jsonc";
  config_scope: "workspace_root";
  config_original_exists: boolean;
  config_original_fingerprint: string;
  config_candidate_fingerprint: string | null;
};

type MutationLock = {
  path: string;
  nonce: string;
  binding: DirectoryBinding;
};

export function parseOpencodePresentationSetupOptions(argv: string[]): OpencodePresentationSetupOptions {
  if (argv.length === 0 || argv[0] === "--help" || argv[0] === "-h") {
    return { action: "status", json: false, help: true };
  }

  let workspace: string | undefined;
  let json = false;
  let help = false;
  const actions: PresentationAction[] = [];
  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--workspace") {
      const value = argv[index + 1];
      if (!value || value.startsWith("--")) throw new Error("--workspace requires a value");
      workspace = value;
      index += 1;
    } else if (arg === "--status") {
      actions.push("status");
    } else if (arg === "--dry-run") {
      actions.push("dry-run");
    } else if (arg === "--write") {
      actions.push("write");
    } else if (arg === "--remove") {
      actions.push("remove");
    } else if (arg === "--json") {
      json = true;
    } else if (arg === "--help" || arg === "-h") {
      help = true;
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  if (help) return { ...(workspace ? { workspace } : {}), action: actions[0] ?? "status", json, help: true };
  if (!workspace) throw new Error("--workspace is required");
  if (actions.length !== 1) throw new Error("choose exactly one action: --status, --dry-run, --write, or --remove");
  return { workspace, action: actions[0]!, json, help: false };
}

export function runOpencodePresentationSetupCli(argv: string[], io: OpencodePresentationSetupIo = {}): number {
  const stdout = io.stdout ?? ((text: string) => process.stdout.write(text));
  const stderr = io.stderr ?? ((text: string) => process.stderr.write(text));
  const cwd = io.cwd ?? process.cwd();
  let options: OpencodePresentationSetupOptions;
  try {
    options = parseOpencodePresentationSetupOptions(argv);
  } catch (error) {
    stderr(`${boundedError(error)}\n`);
    return 1;
  }

  if (options.help) {
    stderr(opencodePresentationSetupHelpText());
    return 0;
  }

  try {
    const bundled = loadBundledArtifact(io.artifactRoot ?? defaultBundledArtifactRoot());
    const workspaceBinding = bindWorkspace(options.workspace!, cwd);
    const recovery = options.action === "write" || options.action === "remove"
      ? recoverWorkspaceIfNeeded(workspaceBinding, bundled)
      : "none";
    let inspection = inspectPresentationWorkspace(workspaceBinding, bundled);
    const changes = plannedChanges(options.action, inspection);
    const blockers = mutationBlockers(options.action, inspection);
    if (options.action === "write" || options.action === "remove") {
      if (blockers.length > 0) {
        const blockedPlan = presentationPlan(options.action, inspection, bundled, reportedChanges(changes, recovery), blockers, recovery, false);
        writePresentationOutput(stdout, options.json, blockedPlan);
        return 1;
      }
      if (hasMutation(changes)) {
        const transaction = createTransaction(options.action, inspection);
        applyTransaction(inspection, bundled, transaction);
        inspection = inspectPresentationWorkspace(workspaceBinding, bundled);
      }
    }

    const actionApplied = (options.action === "write" || options.action === "remove") && hasMutation(changes);
    const plan = presentationPlan(options.action, inspection, bundled, reportedChanges(changes, recovery), blockers, recovery, actionApplied || recovery !== "none");
    writePresentationOutput(stdout, options.json, plan);
    return blockers.length > 0 ? 1 : 0;
  } catch (error) {
    stderr(`opencode presentation setup failed: ${boundedError(error)}\n`);
    return 1;
  }
}

export function opencodePresentationSetupHelpText(): string {
  return `Usage: sgg-unity-mcp-gateway setup opencode-presentation --workspace path --status|--dry-run|--write|--remove [--json]\n\nInstalls only the bundled OpenCode V2 presentation plugin into the exact workspace .opencode directory and native plugins config. Migrates only its owned legacy entry. Mutations fail closed for dual configs, conflicting or disabled owned entries, and unknown or modified artifacts. Running OpenCode/OpenChamber clients require restart after a change.\n`;
}

function defaultBundledArtifactRoot(): string {
  const adjacent = fileURLToPath(new URL("./opencode-presentation/", import.meta.url));
  if (fs.existsSync(adjacent)) return adjacent;
  return fileURLToPath(new URL("../../unity/Packages/com.sungeargames.unity-mcp/Editor/GatewayRuntime/opencode-presentation/", import.meta.url));
}

function loadBundledArtifact(root: string): LoadedArtifact {
  const resolved = absoluteHostPath(root, process.cwd());
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(resolved, { withFileTypes: true });
  } catch {
    throw new Error("bundled presentation artifact is unavailable");
  }
  const unexpected = entries.filter((entry) => !entry.isFile()
    || (!ARTIFACT_FILES.includes(entry.name as typeof ARTIFACT_FILES[number]) && !ARTIFACT_META_FILES.includes(entry.name)));
  if (unexpected.length > 0) throw new Error("bundled presentation artifact contains unexpected files");
  for (const name of ARTIFACT_META_FILES) {
    if (entries.some((entry) => entry.name === name)) readBoundedRegularFile(path.join(resolved, name), 64 * 1024, "bundled presentation artifact is invalid");
  }
  const files = new Map<string, Buffer>();
  for (const name of ARTIFACT_FILES) files.set(name, readBoundedRegularFile(path.join(resolved, name), artifactLimit(name), "bundled presentation artifact is invalid"));
  const manifest = parseArtifactManifest(files.get(MANIFEST_FILE)!, "bundled presentation artifact is invalid");
  validateArtifactFiles(manifest, files, "bundled presentation artifact is invalid");
  validatePackage(files.get(PACKAGE_FILE)!, manifest, "bundled presentation artifact is invalid");
  return { root: resolved, manifest, files, hash: sha256(files.get(MANIFEST_FILE)!) };
}

function inspectPresentationWorkspace(binding: WorkspaceBinding, bundled: LoadedArtifact): PresentationInspection {
  revalidateWorkspace(binding);
  const configRoot = binding.canonical;
  const opencodeRoot = path.join(binding.canonical, ".opencode");
  const artifactPath = path.join(opencodeRoot, ARTIFACT_DIRECTORY);
  const blockers: string[] = [];
  let opencodeUsable = true;
  if (pathOccupied(opencodeRoot)) {
    const stat = safeLstat(opencodeRoot);
    if (!stat?.isDirectory() || stat.isSymbolicLink()) {
      blockers.push("opencode_directory_conflict");
      opencodeUsable = false;
    }
  }
  const config = inspectConfig(configRoot, artifactPath);
  const artifact = opencodeUsable ? inspectInstalledArtifact(artifactPath, bundled) : { state: "unknown" as const, fingerprint: "opencode-conflict" };
  if (config.blocker) blockers.push(config.blocker);
  if (opencodeUsable && pathOccupied(path.join(opencodeRoot, TRANSACTION_FILE))) blockers.push("presentation_transaction_recovery_required");
  return { workspace: binding, configRoot, opencodeRoot, artifactPath, artifact, config, blockers };
}

function bindWorkspace(workspace: string, cwd: string): WorkspaceBinding {
  const requested = absoluteHostPath(workspace, cwd);
  let canonical: string;
  let stat: fs.Stats;
  try {
    canonical = fs.realpathSync.native(requested);
    stat = fs.statSync(canonical, { bigint: true }) as unknown as fs.Stats;
  } catch {
    throw new Error("workspace must be an existing readable directory");
  }
  if (!stat.isDirectory()) throw new Error("workspace must be an existing readable directory");
  const bigintStat = stat as unknown as { dev: bigint; ino: bigint };
  return { requested, canonical, device: bigintStat.dev, inode: bigintStat.ino };
}

function revalidateWorkspace(binding: WorkspaceBinding): void {
  try {
    const canonical = fs.realpathSync.native(binding.requested);
    const stat = fs.statSync(canonical, { bigint: true }) as unknown as { isDirectory(): boolean; dev: bigint; ino: bigint };
    if (canonical !== binding.canonical || !stat.isDirectory() || stat.dev !== binding.device || stat.ino !== binding.inode) throw new Error();
  } catch {
    throw new Error("workspace identity changed before write");
  }
}

function inspectConfig(configRoot: string, artifactPath: string): ConfigInspection {
  const jsonPath = path.join(configRoot, "opencode.json");
  const jsoncPath = path.join(configRoot, "opencode.jsonc");
  const jsonExists = pathOccupied(jsonPath);
  const jsoncExists = pathOccupied(jsoncPath);
  if (jsonExists && jsoncExists) {
    return { path: jsoncPath, file: "opencode.jsonc", exists: true, state: "conflict", source: "", ownedEntries: 0, fingerprint: "dual-config", blocker: "dual_opencode_config" };
  }
  const configPath = jsonExists ? jsonPath : jsoncPath;
  const file = path.basename(configPath);
  const exists = jsonExists || jsoncExists;
  if (!exists) return { path: configPath, file, exists: false, state: "missing", source: "{}\n", ownedEntries: 0, fingerprint: "missing" };
  const stat = safeLstat(configPath);
  if (!stat?.isFile() || stat.isSymbolicLink() || stat.size > MAX_CONFIG_BYTES) {
    return { path: configPath, file, exists: true, state: "conflict", source: "", ownedEntries: 0, fingerprint: "invalid-file", blocker: "opencode_config_conflict" };
  }
  let source: string;
  try {
    source = fs.readFileSync(configPath, "utf8");
  } catch {
    return { path: configPath, file, exists: true, state: "conflict", source: "", ownedEntries: 0, fingerprint: "unreadable", blocker: "opencode_config_conflict" };
  }
  const fingerprint = sha256(Buffer.from(source));
  const parsed = parseOwnedPluginConfig(source, configPath, artifactPath);
  if (!parsed.ok) return { path: configPath, file, exists: true, state: "conflict", source, ownedEntries: 0, fingerprint, blocker: parsed.blocker };
  const state: ConfigState = parsed.ownedEntries === 0 ? "not_configured" : parsed.ownedEntries === 1 ? "configured" : "duplicate_owned_entries";
  return { path: configPath, file, exists: true, state, source, ownedEntries: parsed.ownedEntries, legacyOwnedEntries: parsed.legacyOwnedEntries, fingerprint };
}

function ownedEntrySpec(entry: unknown): string | undefined {
  if (typeof entry === "string") return entry;
  if (isRecord(entry) && typeof entry.package === "string") return entry.package;
  if (Array.isArray(entry) && typeof entry[0] === "string") return entry[0];
  return undefined;
}

function disablesOwnedPlugin(spec: string): boolean {
  if (!spec.startsWith("-")) return false;
  const selector = spec.slice(1);
  return selector === "*" || selector === PLUGIN_ID || selector === OWNED_PLUGIN_SPEC
    || selector === PACKAGE_NAME || (selector.endsWith(".*") && PLUGIN_ID.startsWith(selector.slice(0, -1)));
}

function parseOwnedPluginConfig(source: string, configPath: string, artifactPath: string): { ok: true; ownedEntries: number; legacyOwnedEntries: number } | { ok: false; blocker: string } {
  const errors: ParseError[] = [];
  const value = parse(source, errors, { allowTrailingComma: true }) as unknown;
  if (errors.length > 0 || !isRecord(value)) return { ok: false, blocker: "opencode_config_invalid" };
  const rootPluginKeys = { plugin: 0, plugins: 0 };
  visit(source, {
    onObjectProperty(property, _offset, _length, _line, _character, pathSupplier) {
      if (pathSupplier().length === 0 && (property === "plugin" || property === "plugins")) rootPluginKeys[property] += 1;
    },
  }, { allowTrailingComma: true });
  if (rootPluginKeys.plugin > 1 || rootPluginKeys.plugins > 1) return { ok: false, blocker: "duplicate_plugin_config_key" };
  const owned: { plugin: unknown[]; plugins: unknown[] } = { plugin: [], plugins: [] };
  for (const key of ["plugin", "plugins"] as const) {
    const entries = value[key];
    if (entries === undefined) continue;
    if (!Array.isArray(entries)) return { ok: false, blocker: "plugin_config_not_array" };
    for (const entry of entries) {
      const spec = ownedEntrySpec(entry);
      // Unknown unrelated entries are preserved verbatim, not migrated or interpreted.
      if (spec === undefined) continue;
      if (disablesOwnedPlugin(spec)) return { ok: false, blocker: "owned_plugin_disabled" };
      if (spec === OWNED_PLUGIN_SPEC) {
        if (key === "plugin" && typeof entry !== "string"
          && (!Array.isArray(entry) || entry.length !== 2 || !isRecord(entry[1]))) return { ok: false, blocker: "owned_plugin_entry_conflict" };
        if (key === "plugins" && typeof entry !== "string" && !isRecord(entry)) return { ok: false, blocker: "owned_plugin_entry_conflict" };
        if (key === "plugins" && isRecord(entry) && entry.options !== undefined && !isRecord(entry.options)) return { ok: false, blocker: "owned_plugin_entry_conflict" };
        owned[key].push(entry);
      } else if (resolvesInsideOwnedArtifact(spec, configPath, artifactPath)
        || spec === PLUGIN_ID || spec === PACKAGE_NAME || spec.startsWith(`${PACKAGE_NAME}@`)) {
        return { ok: false, blocker: "owned_plugin_entry_conflict" };
      }
    }
  }
  if (owned.plugin.length > 0 && owned.plugins.length > 0) return { ok: false, blocker: "owned_plugin_native_legacy_conflict" };
  const entries = [...owned.plugin, ...owned.plugins];
  if (entries.some((entry) => JSON.stringify(entry) !== JSON.stringify(entries[0]))) return { ok: false, blocker: "owned_plugin_entry_conflict" };
  return { ok: true, ownedEntries: entries.length, legacyOwnedEntries: owned.plugin.length };
}

function resolvesInsideOwnedArtifact(spec: string, configPath: string, artifactPath: string): boolean {
  let resolved: string;
  try {
    if (spec.startsWith("file://")) resolved = fileURLToPath(spec);
    else if (path.isAbsolute(spec) || /^[A-Za-z]:[\\/]/.test(spec)) resolved = absoluteHostPath(spec, process.cwd());
    else if (spec.startsWith(".")) resolved = absoluteHostPath(spec, path.dirname(configPath));
    else return false;
  } catch {
    return true;
  }
  const projectedResolved = projectedCanonicalPath(resolved);
  const projectedOwned = projectedCanonicalPath(artifactPath);
  if (!projectedResolved || !projectedOwned) return true;
  const canonicalResolved = canonicalPaths.normalize(projectedResolved);
  const canonicalOwned = canonicalPaths.normalize(projectedOwned);
  const caseInsensitive = process.platform === "win32" || process.platform === "darwin" || /^[a-z]:\//.test(canonicalOwned);
  const comparableResolved = caseInsensitive ? canonicalResolved.toLowerCase() : canonicalResolved;
  const comparableOwned = caseInsensitive ? canonicalOwned.toLowerCase() : canonicalOwned;
  return comparableResolved === comparableOwned || comparableResolved.startsWith(`${comparableOwned}/`);
}

function projectedCanonicalPath(filePath: string): string | undefined {
  let current = absoluteHostPath(filePath, process.cwd());
  const suffix: string[] = [];
  while (!pathOccupied(current)) {
    const parent = path.dirname(current);
    if (parent === current) return current;
    suffix.unshift(path.basename(current));
    current = parent;
  }
  try {
    return path.join(fs.realpathSync.native(current), ...suffix);
  } catch {
    return undefined;
  }
}

function inspectInstalledArtifact(target: string, bundled: LoadedArtifact): ArtifactInspection {
  if (!pathOccupied(target)) return { state: "absent", fingerprint: "absent" };
  const targetStat = safeLstat(target);
  if (!targetStat?.isDirectory() || targetStat.isSymbolicLink()) return { state: "unknown", fingerprint: "unknown-target" };
  let entries: fs.Dirent[];
  try {
    entries = fs.readdirSync(target, { withFileTypes: true });
  } catch {
    return { state: "unknown", fingerprint: "unreadable-target" };
  }
  const names = entries.map((entry) => entry.name).sort();
  const manifestPath = path.join(target, MANIFEST_FILE);
  if (!names.includes(MANIFEST_FILE)) return { state: "unknown", fingerprint: sha256(Buffer.from(names.join("\0"))) };
  let manifestBytes: Buffer;
  try {
    manifestBytes = readBoundedRegularFile(manifestPath, MAX_MANIFEST_BYTES, "installed presentation artifact is invalid");
  } catch {
    return { state: "unknown", fingerprint: "invalid-manifest-file" };
  }
  let manifest: ArtifactManifest;
  try {
    manifest = parseArtifactManifest(manifestBytes, "installed presentation artifact is invalid");
  } catch {
    return { state: "unknown", fingerprint: sha256(manifestBytes) };
  }
  const installedVersion = manifest.version;
  const installedManifestHash = sha256(manifestBytes);
  if (manifest.plugin_id !== PLUGIN_ID) return { state: "unknown", installedVersion, fingerprint: installedManifestHash };
  const files = new Map<string, Buffer>([[MANIFEST_FILE, manifestBytes]]);
  try {
    if (names.length !== ARTIFACT_FILES.length || names.some((name) => !ARTIFACT_FILES.includes(name as typeof ARTIFACT_FILES[number]))) throw new Error();
    for (const name of MANIFEST_CONTENT_FILES) files.set(name, readBoundedRegularFile(path.join(target, name), artifactLimit(name), "installed presentation artifact is invalid"));
    validateArtifactFiles(manifest, files, "installed presentation artifact is invalid");
    validatePackage(files.get(PACKAGE_FILE)!, manifest, "installed presentation artifact is invalid", bundled.manifest.trusted_previous_manifest_sha256.includes(installedManifestHash));
  } catch {
    return { state: "modified", installedVersion, fingerprint: artifactFingerprint(names, files) };
  }
  const current = ARTIFACT_FILES.every((name) => files.get(name)?.equals(bundled.files.get(name)!));
  const trustedPrevious = bundled.manifest.trusted_previous_manifest_sha256.includes(installedManifestHash);
  return { state: current ? "current" : trustedPrevious ? "outdated" : "unknown", installedVersion, fingerprint: artifactFingerprint(names, files) };
}

function parseArtifactManifest(bytes: Buffer, message: string): ArtifactManifest {
  let value: unknown;
  try {
    value = JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new Error(message);
  }
  if (!isRecord(value)
    || value.schema_version !== 1
    || value.plugin_id !== PLUGIN_ID
    || typeof value.version !== "string"
    || !/^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$/.test(value.version)
    || typeof value.opencode_range !== "string"
    || value.opencode_range.length === 0
    || value.opencode_range.length > 128
    || !Array.isArray(value.trusted_previous_manifest_sha256)
    || value.trusted_previous_manifest_sha256.length > 32
    || value.trusted_previous_manifest_sha256.some((hash) => typeof hash !== "string" || !/^[a-f0-9]{64}$/.test(hash))
    || new Set(value.trusted_previous_manifest_sha256).size !== value.trusted_previous_manifest_sha256.length
    || !Array.isArray(value.files)) throw new Error(message);
  const files: ArtifactManifest["files"] = [];
  for (const entry of value.files) {
    if (!isRecord(entry)
      || typeof entry.path !== "string"
      || !MANIFEST_CONTENT_FILES.includes(entry.path as typeof MANIFEST_CONTENT_FILES[number])
      || !Number.isSafeInteger(entry.bytes)
      || (entry.bytes as number) < 0
      || typeof entry.sha256 !== "string"
      || !/^[a-f0-9]{64}$/.test(entry.sha256)) throw new Error(message);
    files.push({ path: entry.path, bytes: entry.bytes as number, sha256: entry.sha256 });
  }
  if (files.length !== MANIFEST_CONTENT_FILES.length || new Set(files.map((entry) => entry.path)).size !== MANIFEST_CONTENT_FILES.length) throw new Error(message);
  return {
    schema_version: 1,
    plugin_id: value.plugin_id,
    version: value.version,
    opencode_range: value.opencode_range,
    trusted_previous_manifest_sha256: value.trusted_previous_manifest_sha256 as string[],
    files,
  };
}

function validateArtifactFiles(manifest: ArtifactManifest, files: Map<string, Buffer>, message: string): void {
  for (const expected of manifest.files) {
    const content = files.get(expected.path);
    if (!content || content.length !== expected.bytes || sha256(content) !== expected.sha256 || content.length > artifactLimit(expected.path)) throw new Error(message);
  }
}

function validatePackage(bytes: Buffer, manifest: ArtifactManifest, message: string, trustedPrevious = false): void {
  let value: unknown;
  try {
    value = JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new Error(message);
  }
  if (!isRecord(value)
    || value.name !== PACKAGE_NAME
    || value.version !== manifest.version
    || value.type !== "module"
    || value.private !== true
    || !isRecord(value.exports)
    || !isRecord(value.engines)
    || value.engines.opencode !== manifest.opencode_range) throw new Error(message);
  const native = value.exports["."] === "./server.js" && Object.keys(value.exports).length === 1
    && manifest.opencode_range === ">=2.0.25 <3" && isRecord(value.dependencies)
    && value.dependencies["@opencode/plugin"] === "2.0.25" && Object.keys(value.dependencies).length === 1;
  const legacy = trustedPrevious && manifest.version === "1.0.0" && manifest.opencode_range === ">=1.18.21 <2"
    && value.exports["./server"] === "./server.js" && Object.keys(value.exports).length === 1 && value.dependencies === undefined;
  if (!native && !legacy) throw new Error(message);
}

function plannedChanges(action: PresentationAction, inspection: PresentationInspection): string[] {
  if (action === "status") return [];
  const changes: string[] = [];
  if (action === "remove") {
    if (inspection.artifact.state !== "absent") changes.push("remove_owned_artifact");
    if (inspection.config.ownedEntries > 0) changes.push("remove_owned_config_entry");
    return changes.length > 0 ? changes : ["noop"];
  }
  if (inspection.artifact.state === "absent") changes.push("install_bundled_artifact");
  if (inspection.artifact.state === "outdated") changes.push("update_owned_artifact");
  if (inspection.config.ownedEntries === 0) changes.push("add_owned_config_entry");
  if (inspection.config.legacyOwnedEntries) changes.push("migrate_owned_legacy_config_entry");
  if (inspection.config.ownedEntries > 1) changes.push("deduplicate_owned_config_entry");
  return changes.length > 0 ? changes : ["noop"];
}

function mutationBlockers(action: PresentationAction, inspection: PresentationInspection): string[] {
  const blockers = [...new Set(inspection.blockers)];
  if (action === "write" || action === "dry-run") {
    if (inspection.artifact.state === "modified") blockers.push("installed_artifact_modified");
    if (inspection.artifact.state === "unknown") blockers.push("installed_artifact_unknown");
  }
  if (action === "remove") {
    if (inspection.artifact.state === "modified") blockers.push("installed_artifact_modified");
    if (inspection.artifact.state === "unknown") blockers.push("installed_artifact_unknown");
  }
  return [...new Set(blockers)];
}

function hasMutation(changes: string[]): boolean {
  return changes.some((change) => change !== "noop");
}

function reportedChanges(changes: string[], recovery: RecoveryOutcome): string[] {
  if (recovery === "none") return changes;
  const recoveryChange = recovery === "finalized" ? "finalize_interrupted_transaction" : "rollback_interrupted_transaction";
  const actionChanges = changes.filter((change) => change !== "noop");
  return [recoveryChange, ...actionChanges];
}

function createTransaction(action: PresentationAction, inspection: PresentationInspection): Transaction {
  if (action === "write") {
    return {
      artifactOperation: inspection.artifact.state === "current" ? "none" : "install",
      configText: patchOwnedPluginEntries(inspection.config.source, true),
    };
  }
  if (action === "remove") {
    return {
      artifactOperation: inspection.artifact.state === "absent" ? "none" : "remove",
      ...(inspection.config.exists && inspection.config.ownedEntries > 0 ? { configText: patchOwnedPluginEntries(inspection.config.source, false) } : {}),
    };
  }
  throw new Error("invalid presentation mutation action");
}

function patchOwnedPluginEntries(source: string, install: boolean): string {
  const errors: ParseError[] = [];
  const value = parse(source, errors, { allowTrailingComma: true }) as unknown;
  if (errors.length > 0 || !isRecord(value)) throw new Error("OpenCode config changed before write");
  const formattingOptions = detectFormatting(source);
  let next = source;
  let migratedEntry: unknown = OWNED_PLUGIN_SPEC;
  let nativeOwned = false;
  for (const key of ["plugin", "plugins"] as const) {
    const entries = value[key];
    if (entries === undefined) continue;
    if (!Array.isArray(entries)) throw new Error("OpenCode config changed before write");
    const ownedIndices = entries.flatMap((entry, index) => ownedEntrySpec(entry) === OWNED_PLUGIN_SPEC ? [index] : []);
    if (key === "plugin" && ownedIndices.length > 0) {
      const entry = entries[ownedIndices[0]!];
      if (Array.isArray(entry)) migratedEntry = { package: OWNED_PLUGIN_SPEC, options: entry[1] };
    }
    if (key === "plugins" && ownedIndices.length > 0) nativeOwned = true;
    const removeIndices = install && key === "plugins" ? ownedIndices.slice(1) : ownedIndices;
    for (const index of [...removeIndices].reverse()) next = removeOwnedArrayEntry(next, key, index);
  }
  if (install && !nativeOwned) {
    next = applyEdits(next, modify(next, value.plugins === undefined ? ["plugins"] : ["plugins", -1], value.plugins === undefined ? [migratedEntry] : migratedEntry, { formattingOptions }));
  }
  const candidateErrors: ParseError[] = [];
  if (!isRecord(parse(next, candidateErrors, { allowTrailingComma: true })) || candidateErrors.length > 0) throw new Error("OpenCode config edit validation failed");
  return next;
}

function removeOwnedArrayEntry(source: string, key: "plugin" | "plugins", index: number): string {
  const root = parseTree(source);
  const array = root && findNodeAtLocation(root, [key]);
  const node = array?.children?.[index];
  if (!array || array.type !== "array" || !node || !array.children) throw new Error("OpenCode config changed before write");
  const start = node.offset;
  const end = start + node.length;
  const commaInRange = (from: number, to: number): number | undefined => {
    const scanner = createScanner(source, false);
    scanner.setPosition(from);
    for (let token = scanner.scan(); token !== SyntaxKind.EOF && scanner.getTokenOffset() < to; token = scanner.scan()) {
      if (token === SyntaxKind.CommaToken) return scanner.getTokenOffset();
    }
    return undefined;
  };
  const next = array.children[index + 1];
  const previous = array.children[index - 1];
  const comma = commaInRange(end, next?.offset ?? array.offset + array.length - 1)
    ?? commaInRange(previous ? previous.offset + previous.length : array.offset + 1, start);
  // jsonc-parser's last-element removal assumes whitespace before `]` and can corrupt inline arrays.
  // Remove only the owned syntax and one comma; retain all surrounding and nested comments.
  const comments: string[] = [];
  const ownedSource = source.slice(start, end);
  visit(ownedSource, { onComment(offset, length) { comments.push(ownedSource.slice(offset, offset + length)); } });
  const edits = [{ offset: start, length: node.length, content: comments.length ? `${comments.join("\n")}\n` : "" }];
  if (comma !== undefined) edits.push({ offset: comma, length: 1, content: "" });
  return applyEdits(source, edits);
}

function applyTransaction(inspection: PresentationInspection, bundled: LoadedArtifact, transaction: Transaction): void {
  revalidateWorkspace(inspection.workspace);
  fs.mkdirSync(inspection.opencodeRoot, { recursive: true });
  const opencodeStat = safeLstat(inspection.opencodeRoot);
  if (!opencodeStat?.isDirectory() || opencodeStat.isSymbolicLink()) throw new Error("workspace .opencode directory is not safe to modify");
  const opencodeBinding = bindDirectory(inspection.opencodeRoot, "workspace .opencode");
  const configBinding = bindDirectory(inspection.configRoot, "workspace");
  const mutationLock = acquireMutationLock(opencodeBinding);

  const nonce = randomUUID();
  const paths = transactionPaths(inspection.opencodeRoot, inspection.configRoot, nonce);
  const { artifactStage, artifactBackup, configStage, configBackup } = paths;
  const transactionPath = path.join(inspection.opencodeRoot, TRANSACTION_FILE);
  const configCandidateFingerprint = transaction.configText !== undefined && transaction.configText !== inspection.config.source
    ? sha256(Buffer.from(transaction.configText))
    : null;
  const transactionRecord: TransactionRecord = {
    schema_version: 2,
    nonce,
    artifact_operation: transaction.artifactOperation,
    artifact_original_state: inspection.artifact.state,
    artifact_original_fingerprint: inspection.artifact.fingerprint,
    config_file: inspection.config.file as "opencode.json" | "opencode.jsonc",
    config_scope: "workspace_root",
    config_original_exists: inspection.config.exists,
    config_original_fingerprint: inspection.config.fingerprint,
    config_candidate_fingerprint: configCandidateFingerprint,
  };
  let artifactStaged = false;
  let configStaged = false;
  let artifactBackedUp = false;
  let artifactInstalled = false;
  let configBackedUp = false;
  let configInstalled = false;
  let transactionPublished = false;

  try {
    if (pathOccupied(transactionPath)) throw new Error("an interrupted presentation transaction requires recovery");
    assertDirectoryIdentity(opencodeBinding);
    assertUnchanged(inspection, bundled);
    writeControlFileAtomic(opencodeBinding, transactionPath, transactionRecord);
    transactionPublished = true;
    if (transaction.artifactOperation === "install") {
      assertDirectoryIdentity(opencodeBinding);
      fs.mkdirSync(artifactStage, { mode: 0o700 });
      artifactStaged = true;
      for (const name of ARTIFACT_FILES) {
        assertDirectoryIdentity(opencodeBinding);
        fs.writeFileSync(path.join(artifactStage, name), bundled.files.get(name)!, { flag: "wx", mode: 0o600 });
      }
      const staged = inspectInstalledArtifact(artifactStage, bundled);
      if (staged.state !== "current") throw new Error("staged presentation artifact validation failed");
    }
    if (transaction.configText !== undefined && transaction.configText !== inspection.config.source) {
      assertDirectoryIdentity(configBinding);
      const mode = inspection.config.exists ? (fs.statSync(inspection.config.path).mode & 0o777) : 0o600;
      fs.writeFileSync(configStage, transaction.configText, { flag: "wx", mode });
      configStaged = true;
    }

    revalidateWorkspace(inspection.workspace);
    assertDirectoryIdentity(opencodeBinding);
    assertDirectoryIdentity(configBinding);
    assertUnchanged(inspection, bundled);

    if (transaction.artifactOperation !== "none") {
      assertDirectoryIdentity(opencodeBinding);
      const targetExists = pathOccupied(inspection.artifactPath);
      if (inspection.artifact.state === "absent") {
        if (targetExists) throw new Error("workspace presentation state changed before write");
      } else {
        if (!targetExists) throw new Error("workspace presentation state changed before write");
        fs.renameSync(inspection.artifactPath, artifactBackup);
        artifactBackedUp = true;
        assertDirectoryIdentity(opencodeBinding);
        const moved = inspectInstalledArtifact(artifactBackup, bundled);
        if (moved.state !== inspection.artifact.state || moved.fingerprint !== inspection.artifact.fingerprint) throw new Error("workspace presentation state changed before write");
      }
    }
    if (transaction.artifactOperation === "install") {
      assertDirectoryIdentity(opencodeBinding);
      if (pathOccupied(inspection.artifactPath)) throw new Error("workspace presentation state changed before write");
      fs.renameSync(artifactStage, inspection.artifactPath);
      artifactStaged = false;
      artifactInstalled = true;
    }
    if (configStaged) {
      assertDirectoryIdentity(configBinding);
      if (inspection.config.exists) {
        if (!pathOccupied(inspection.config.path)) throw new Error("workspace presentation state changed before write");
        fs.renameSync(inspection.config.path, configBackup);
        configBackedUp = true;
        assertDirectoryIdentity(configBinding);
        if (regularFileFingerprint(configBackup, MAX_CONFIG_BYTES) !== inspection.config.fingerprint) throw new Error("workspace presentation state changed before write");
      } else if (pathOccupied(inspection.config.path)) {
        throw new Error("workspace presentation state changed before write");
      }
      assertDirectoryIdentity(configBinding);
      fs.linkSync(configStage, inspection.config.path);
      configInstalled = true;
      assertDirectoryIdentity(configBinding);
      fs.unlinkSync(configStage);
      configStaged = false;
    }

    assertDirectoryIdentity(opencodeBinding);
    assertDirectoryIdentity(configBinding);
    verifyCommittedTransaction(inspection, bundled, transaction);

    const artifactBackupRemoved = !artifactBackedUp || tryRemove(opencodeBinding, artifactBackup, true);
    const configBackupRemoved = !configBackedUp || tryRemove(configBinding, configBackup, false);
    if (artifactBackupRemoved && configBackupRemoved) {
      if (tryRemove(opencodeBinding, transactionPath, false)) transactionPublished = false;
    }
  } catch (error) {
    const rollbackFailed = rollbackTransaction({
      artifactPath: inspection.artifactPath,
      artifactBackup,
      artifactBackedUp,
      artifactInstalled,
      configPath: inspection.config.path,
      configBackup,
      configBackedUp,
      configInstalled,
      opencodeBinding,
      configBinding,
      bundled,
      configCandidateFingerprint,
    });
    if (!rollbackFailed && transactionPublished && tryRemove(opencodeBinding, transactionPath, false)) transactionPublished = false;
    if (rollbackFailed) throw new Error("presentation setup failed and rollback was incomplete");
    throw new Error(`presentation transaction failed: ${boundedError(error)}`);
  } finally {
    if (artifactStaged) tryRemove(opencodeBinding, artifactStage, true);
    if (configStaged) tryRemove(configBinding, configStage, false);
    releaseMutationLock(mutationLock);
  }
}

function rollbackTransaction(state: {
  artifactPath: string;
  artifactBackup: string;
  artifactBackedUp: boolean;
  artifactInstalled: boolean;
  configPath: string;
  configBackup: string;
  configBackedUp: boolean;
  configInstalled: boolean;
  opencodeBinding: DirectoryBinding;
  configBinding: DirectoryBinding;
  bundled: LoadedArtifact;
  configCandidateFingerprint: string | null;
}): boolean {
  if (!directoryIdentityMatches(state.opencodeBinding) || !directoryIdentityMatches(state.configBinding)) return true;
  let failed = false;
  try {
    assertDirectoryIdentity(state.configBinding);
    if (state.configInstalled) {
      if (!state.configCandidateFingerprint) throw new Error("missing config rollback fingerprint");
      removeVerifiedRegularFile(state.configBinding, state.configPath, state.configCandidateFingerprint);
    }
    assertDirectoryIdentity(state.configBinding);
    if (state.configBackedUp) {
      if (pathOccupied(state.configPath)) throw new Error("config rollback target is occupied");
      fs.renameSync(state.configBackup, state.configPath);
    }
  } catch {
    failed = true;
  }
  try {
    assertDirectoryIdentity(state.opencodeBinding);
    if (state.artifactInstalled) removeVerifiedArtifact(state.opencodeBinding, state.artifactPath, state.bundled, "current");
    assertDirectoryIdentity(state.opencodeBinding);
    if (state.artifactBackedUp) {
      if (pathOccupied(state.artifactPath)) throw new Error("artifact rollback target is occupied");
      fs.renameSync(state.artifactBackup, state.artifactPath);
    }
  } catch {
    failed = true;
  }
  return failed;
}

function assertUnchanged(inspection: PresentationInspection, bundled: LoadedArtifact): void {
  const currentConfig = inspectConfig(inspection.configRoot, inspection.artifactPath);
  const currentArtifact = inspectInstalledArtifact(inspection.artifactPath, bundled);
  if (currentConfig.path !== inspection.config.path
    || currentConfig.fingerprint !== inspection.config.fingerprint
    || currentArtifact.fingerprint !== inspection.artifact.fingerprint) throw new Error("workspace presentation state changed before write");
}

function verifyCommittedTransaction(inspection: PresentationInspection, bundled: LoadedArtifact, transaction: Transaction): void {
  const artifact = inspectInstalledArtifact(inspection.artifactPath, bundled);
  if (transaction.artifactOperation === "install" && artifact.state !== "current") throw new Error("installed presentation artifact verification failed");
  if (transaction.artifactOperation === "remove" && artifact.state !== "absent") throw new Error("removed presentation artifact verification failed");
  if (transaction.artifactOperation === "none" && artifact.fingerprint !== inspection.artifact.fingerprint) throw new Error("workspace presentation state changed during write");

  const config = inspectConfig(inspection.configRoot, inspection.artifactPath);
  const expectedFingerprint = transaction.configText === undefined ? inspection.config.fingerprint : sha256(Buffer.from(transaction.configText));
  if (config.path !== inspection.config.path || config.fingerprint !== expectedFingerprint || config.blocker) throw new Error("OpenCode config verification failed");
}

function recoverWorkspaceIfNeeded(workspaceBinding: WorkspaceBinding, bundled: LoadedArtifact): RecoveryOutcome {
  const opencodeRoot = path.join(workspaceBinding.canonical, ".opencode");
  const transactionPath = path.join(opencodeRoot, TRANSACTION_FILE);
  if (!pathOccupied(transactionPath)) return "none";
  const rootStat = safeLstat(opencodeRoot);
  if (!rootStat?.isDirectory() || rootStat.isSymbolicLink()) throw new Error("interrupted presentation transaction cannot be recovered safely");
  const opencodeBinding = bindDirectory(opencodeRoot, "workspace .opencode");
  const configBinding = bindDirectory(workspaceBinding.canonical, "workspace");
  const lock = acquireMutationLock(opencodeBinding);
  try {
    revalidateWorkspace(workspaceBinding);
    return recoverInterruptedTransaction(opencodeBinding, configBinding, bundled);
  } finally {
    releaseMutationLock(lock);
  }
}

function recoverInterruptedTransaction(opencodeBinding: DirectoryBinding, configBinding: DirectoryBinding, bundled: LoadedArtifact): Exclude<RecoveryOutcome, "none"> {
  const transactionPath = path.join(opencodeBinding.path, TRANSACTION_FILE);
  const record = readTransactionRecord(transactionPath);
  const paths = transactionPaths(opencodeBinding.path, configBinding.path, record.nonce);
  const configPath = path.join(configBinding.path, record.config_file);
  const alternateConfig = path.join(configBinding.path, record.config_file === "opencode.json" ? "opencode.jsonc" : "opencode.json");
  if (pathOccupied(alternateConfig)) throw new Error("interrupted presentation transaction conflicts with another OpenCode config");

  const artifact = inspectInstalledArtifact(path.join(opencodeBinding.path, ARTIFACT_DIRECTORY), bundled);
  const configFingerprint = pathOccupied(configPath) ? regularFileFingerprint(configPath, MAX_CONFIG_BYTES) : undefined;
  const artifactFinal = record.artifact_operation === "install"
    ? artifact.state === "current"
    : record.artifact_operation === "remove"
      ? artifact.state === "absent"
      : artifact.fingerprint === record.artifact_original_fingerprint;
  const configFinal = record.config_candidate_fingerprint !== null
    ? configFingerprint === record.config_candidate_fingerprint
    : record.config_original_exists
      ? configFingerprint === record.config_original_fingerprint
      : !pathOccupied(configPath);

  validateRecoveryStages(record, paths);
  if (artifactFinal && configFinal) {
    validateRecoveryCleanupBackups(paths);
    removeRecoveryPath(opencodeBinding, paths.artifactBackup, true);
    removeRecoveryPath(configBinding, paths.configBackup, false);
    removeRecoveryPath(opencodeBinding, paths.artifactStage, true);
    removeRecoveryPath(configBinding, paths.configStage, false);
    removeRecoveryPath(opencodeBinding, transactionPath, false);
    return "finalized";
  }

  validateRecoveryBackups(record, paths, bundled);
  validateRollbackState(record, opencodeBinding.path, configBinding.path, paths, bundled);
  rollbackInterruptedTransaction(record, opencodeBinding, configBinding, paths, bundled);
  const restoredArtifact = inspectInstalledArtifact(path.join(opencodeBinding.path, ARTIFACT_DIRECTORY), bundled);
  const restoredConfig = pathOccupied(configPath) ? regularFileFingerprint(configPath, MAX_CONFIG_BYTES) : undefined;
  const artifactRestored = record.artifact_original_state === "absent"
    ? restoredArtifact.state === "absent"
    : restoredArtifact.fingerprint === record.artifact_original_fingerprint;
  const configRestored = record.config_original_exists
    ? restoredConfig === record.config_original_fingerprint
    : !pathOccupied(configPath);
  if (!artifactRestored || !configRestored) throw new Error("interrupted presentation transaction recovery verification failed");
  removeRecoveryPath(opencodeBinding, paths.artifactStage, true);
  removeRecoveryPath(configBinding, paths.configStage, false);
  removeRecoveryPath(opencodeBinding, transactionPath, false);
  return "rolled_back";
}

function readTransactionRecord(filePath: string): TransactionRecord {
  let value: unknown;
  try {
    value = readControlJson(filePath);
  } catch {
    throw new Error("interrupted presentation transaction record is invalid");
  }
  if (!isRecord(value)
    || value.schema_version !== 2
    || typeof value.nonce !== "string"
    || !/^[a-f0-9]{8}-[a-f0-9]{4}-4[a-f0-9]{3}-[89ab][a-f0-9]{3}-[a-f0-9]{12}$/.test(value.nonce)
    || (value.artifact_operation !== "none" && value.artifact_operation !== "install" && value.artifact_operation !== "remove")
    || (value.artifact_original_state !== "absent" && value.artifact_original_state !== "current" && value.artifact_original_state !== "outdated")
    || !validFingerprint(value.artifact_original_fingerprint, value.artifact_original_state === "absent" ? "absent" : undefined)
    || (value.config_file !== "opencode.json" && value.config_file !== "opencode.jsonc")
    || value.config_scope !== "workspace_root"
    || typeof value.config_original_exists !== "boolean"
    || !validFingerprint(value.config_original_fingerprint, value.config_original_exists ? undefined : "missing")
    || (value.config_candidate_fingerprint !== null && !validFingerprint(value.config_candidate_fingerprint))) throw new Error("interrupted presentation transaction record is invalid");
  if (value.artifact_operation === "remove" && value.artifact_original_state === "absent") {
    // A config-only removal legitimately has no artifact operation.
    throw new Error("interrupted presentation transaction record is inconsistent");
  }
  return value as unknown as TransactionRecord;
}

function transactionPaths(opencodeRoot: string, configRoot: string, nonce: string) {
  return {
    artifactStage: path.join(opencodeRoot, `.sgg-unity-presentation-stage-${nonce}`),
    artifactBackup: path.join(opencodeRoot, `.sgg-unity-presentation-backup-${nonce}`),
    configStage: path.join(configRoot, `.sgg-unity-presentation-config-${nonce}.tmp`),
    configBackup: path.join(configRoot, `.sgg-unity-presentation-config-${nonce}.bak`),
  };
}

function validateRecoveryBackups(record: TransactionRecord, paths: ReturnType<typeof transactionPaths>, bundled: LoadedArtifact): void {
  if (pathOccupied(paths.artifactBackup)) {
    if (record.artifact_original_state === "absent") throw new Error("interrupted presentation artifact backup is unexpected");
    const backup = inspectInstalledArtifact(paths.artifactBackup, bundled);
    if (backup.fingerprint !== record.artifact_original_fingerprint) throw new Error("interrupted presentation artifact backup is invalid");
  }
  if (pathOccupied(paths.configBackup)) {
    if (!record.config_original_exists || regularFileFingerprint(paths.configBackup, MAX_CONFIG_BYTES) !== record.config_original_fingerprint) {
      throw new Error("interrupted presentation config backup is invalid");
    }
  }
}

function validateRecoveryCleanupBackups(paths: ReturnType<typeof transactionPaths>): void {
  if (pathOccupied(paths.artifactBackup)) validateRecoveryArtifactDirectory(paths.artifactBackup, "interrupted presentation artifact backup is invalid");
  if (pathOccupied(paths.configBackup)) {
    const stat = safeLstat(paths.configBackup);
    if (!stat?.isFile() || stat.isSymbolicLink() || stat.size > MAX_CONFIG_BYTES) throw new Error("interrupted presentation config backup is invalid");
  }
}

function validateRecoveryStages(record: TransactionRecord, paths: ReturnType<typeof transactionPaths>): void {
  if (pathOccupied(paths.artifactStage)) {
    validateRecoveryArtifactDirectory(paths.artifactStage, "interrupted presentation artifact stage is invalid");
  }
  if (pathOccupied(paths.configStage)) {
    const stat = safeLstat(paths.configStage);
    if (!stat?.isFile() || stat.isSymbolicLink() || stat.size > MAX_CONFIG_BYTES || record.config_candidate_fingerprint === null) {
      throw new Error("interrupted presentation config stage is invalid");
    }
  }
}

function validateRecoveryArtifactDirectory(directory: string, message: string): void {
  const stat = safeLstat(directory);
  if (!stat?.isDirectory() || stat.isSymbolicLink()) throw new Error(message);
  const entries = fs.readdirSync(directory, { withFileTypes: true });
  if (entries.some((entry) => !entry.isFile() || !ARTIFACT_FILES.includes(entry.name as typeof ARTIFACT_FILES[number]))) throw new Error(message);
  for (const entry of entries) readBoundedRegularFile(path.join(directory, entry.name), artifactLimit(entry.name), message);
}

function validateRollbackState(record: TransactionRecord, opencodeRoot: string, configRoot: string, paths: ReturnType<typeof transactionPaths>, bundled: LoadedArtifact): void {
  const artifactPath = path.join(opencodeRoot, ARTIFACT_DIRECTORY);
  const artifact = inspectInstalledArtifact(artifactPath, bundled);
  const artifactBackupExists = pathOccupied(paths.artifactBackup);
  if (artifactBackupExists) {
    if (artifact.state !== "absent" && !(record.artifact_operation === "install" && artifact.state === "current")) {
      throw new Error("interrupted presentation artifact target changed during recovery");
    }
  } else if (record.artifact_original_state === "absent") {
    if (artifact.state !== "absent" && !(record.artifact_operation === "install" && artifact.state === "current")) {
      throw new Error("interrupted presentation artifact target changed during recovery");
    }
  } else if (artifact.fingerprint !== record.artifact_original_fingerprint) {
    throw new Error("interrupted presentation artifact original is unavailable");
  }

  const configPath = path.join(configRoot, record.config_file);
  const configExists = pathOccupied(configPath);
  const configFingerprint = configExists ? regularFileFingerprint(configPath, MAX_CONFIG_BYTES) : undefined;
  const configBackupExists = pathOccupied(paths.configBackup);
  if (configBackupExists) {
    if (configExists) throw new Error("interrupted presentation config swap requires manual recovery");
  } else if (!record.config_original_exists) {
    if (configExists) throw new Error("interrupted presentation config cannot be removed without an original backup");
  } else if (configFingerprint !== record.config_original_fingerprint) {
    throw new Error("interrupted presentation config original is unavailable");
  }
}

function rollbackInterruptedTransaction(record: TransactionRecord, opencodeBinding: DirectoryBinding, configBinding: DirectoryBinding, paths: ReturnType<typeof transactionPaths>, bundled: LoadedArtifact): void {
  const configPath = path.join(configBinding.path, record.config_file);
  if (pathOccupied(paths.configBackup)) {
    assertDirectoryIdentity(configBinding);
    if (pathOccupied(configPath)) throw new Error("interrupted presentation config rollback target is occupied");
    fs.renameSync(paths.configBackup, configPath);
  }

  const artifactPath = path.join(opencodeBinding.path, ARTIFACT_DIRECTORY);
  if (pathOccupied(paths.artifactBackup)) {
    if (pathOccupied(artifactPath)) removeVerifiedArtifact(opencodeBinding, artifactPath, bundled, "current");
    assertDirectoryIdentity(opencodeBinding);
    if (pathOccupied(artifactPath)) throw new Error("interrupted presentation artifact rollback target is occupied");
    fs.renameSync(paths.artifactBackup, artifactPath);
  } else if (record.artifact_original_state === "absent" && pathOccupied(artifactPath)) {
    removeVerifiedArtifact(opencodeBinding, artifactPath, bundled, "current");
  }
}

function removeRecoveryPath(binding: DirectoryBinding, filePath: string, recursive: boolean): void {
  if (!pathOccupied(filePath)) return;
  const tombstone = quarantineForRemoval(binding, filePath);
  try {
    if (recursive) validateRecoveryArtifactDirectory(tombstone, "interrupted presentation cleanup target is invalid");
    else {
      const stat = safeLstat(tombstone);
      if (!stat?.isFile() || stat.isSymbolicLink() || stat.size > MAX_CONFIG_BYTES) throw new Error("interrupted presentation cleanup target is invalid");
    }
  } catch (error) {
    restoreQuarantinedPath(binding, tombstone, filePath);
    throw error;
  }
  if (!tryRemove(binding, tombstone, recursive)) throw new Error("interrupted presentation transaction cleanup failed");
}

function removeVerifiedRegularFile(binding: DirectoryBinding, filePath: string, expectedFingerprint: string): void {
  const tombstone = quarantineForRemoval(binding, filePath);
  if (regularFileFingerprint(tombstone, MAX_CONFIG_BYTES) !== expectedFingerprint) {
    restoreQuarantinedPath(binding, tombstone, filePath);
    throw new Error("OpenCode config changed during rollback");
  }
  if (!tryRemove(binding, tombstone, false)) throw new Error("OpenCode config rollback cleanup failed");
}

function removeVerifiedArtifact(binding: DirectoryBinding, artifactPath: string, bundled: LoadedArtifact, expectedState: ArtifactState): void {
  const tombstone = quarantineForRemoval(binding, artifactPath);
  if (inspectInstalledArtifact(tombstone, bundled).state !== expectedState) {
    restoreQuarantinedPath(binding, tombstone, artifactPath);
    throw new Error("presentation artifact changed during rollback");
  }
  if (!tryRemove(binding, tombstone, true)) throw new Error("presentation artifact rollback cleanup failed");
}

function quarantineForRemoval(binding: DirectoryBinding, filePath: string): string {
  const tombstone = path.join(binding.path, `.sgg-unity-presentation-delete-${randomUUID()}`);
  assertDirectoryIdentity(binding);
  fs.renameSync(filePath, tombstone);
  if (!directoryIdentityMatches(binding)) throw new Error(`${binding.label} directory identity changed during cleanup`);
  return tombstone;
}

function restoreQuarantinedPath(binding: DirectoryBinding, tombstone: string, originalPath: string): void {
  if (!directoryIdentityMatches(binding) || pathOccupied(originalPath) || !pathOccupied(tombstone)) {
    throw new Error("presentation rollback quarantine could not be restored safely");
  }
  fs.renameSync(tombstone, originalPath);
}

function validFingerprint(value: unknown, special?: string): boolean {
  return value === special || (typeof value === "string" && /^[a-f0-9]{64}$/.test(value));
}

function presentationPlan(action: PresentationAction, inspection: PresentationInspection, bundled: LoadedArtifact, changes: string[], blockers: string[], recovery: RecoveryOutcome, applied: boolean): PresentationPlan {
  return {
    schema_version: 1,
    setup: "opencode-presentation",
    action,
    workspace: inspection.workspace.requested,
    artifact: {
      state: inspection.artifact.state,
      bundled_version: bundled.manifest.version,
      installed_version: inspection.artifact.installedVersion ?? null,
      bundled_hash: `sha256:${bundled.hash.slice(0, 12)}`,
    },
    config: {
      file: inspection.config.file,
      state: inspection.config.state,
      owned_entries: inspection.config.ownedEntries,
    },
    changes,
    blocked_reasons: blockers,
    recovery,
    restart_required: applied,
    applied,
  };
}

function writePresentationOutput(stdout: (text: string) => void, json: boolean, plan: PresentationPlan): void {
  if (json) {
    stdout(`${JSON.stringify(plan, null, 2)}\n`);
    return;
  }
  stdout(`setup: ${plan.setup}\n`);
  stdout(`action: ${plan.action}\n`);
  stdout(`workspace: ${safeOutputText(plan.workspace, 512)}\n`);
  stdout(`artifact: ${plan.artifact.state} (${plan.artifact.bundled_version}, ${plan.artifact.bundled_hash})\n`);
  stdout(`config: ${plan.config.state} (${plan.config.file})\n`);
  for (const change of plan.changes) stdout(`change: ${change}\n`);
  for (const blocker of plan.blocked_reasons) stdout(`blocked: ${blocker}\n`);
  if (plan.restart_required) stdout("note: restart OpenCode/OpenChamber to load the presentation change\n");
}

function detectFormatting(source: string): FormattingOptions {
  const indentation = source.match(/^[\t ]+(?="[^"\r\n]+"\s*:)/m)?.[0] ?? "  ";
  return {
    insertSpaces: !indentation.includes("\t"),
    tabSize: indentation.includes("\t") ? 1 : Math.max(1, indentation.length),
    eol: source.includes("\r\n") ? "\r\n" : "\n",
  };
}

function readBoundedRegularFile(filePath: string, limit: number, message: string): Buffer {
  const stat = safeLstat(filePath);
  if (!stat?.isFile() || stat.isSymbolicLink() || stat.size > limit) throw new Error(message);
  const bytes = fs.readFileSync(filePath);
  if (bytes.length !== stat.size || bytes.length > limit) throw new Error(message);
  return bytes;
}

function artifactLimit(name: string): number {
  if (name === MANIFEST_FILE) return MAX_MANIFEST_BYTES;
  if (name === PACKAGE_FILE) return MAX_PACKAGE_BYTES;
  return MAX_SERVER_BYTES;
}

function artifactFingerprint(names: string[], files: Map<string, Buffer>): string {
  const hash = createHash("sha256");
  hash.update(names.join("\0"));
  for (const name of ARTIFACT_FILES) {
    hash.update("\0");
    hash.update(name);
    const bytes = files.get(name);
    if (bytes) hash.update(bytes);
  }
  return hash.digest("hex");
}

function safeLstat(filePath: string): fs.Stats | undefined {
  try {
    return fs.lstatSync(filePath);
  } catch {
    return undefined;
  }
}

function pathOccupied(filePath: string): boolean {
  return safeLstat(filePath) !== undefined;
}

function regularFileFingerprint(filePath: string, limit: number): string | undefined {
  try {
    const bytes = readBoundedRegularFile(filePath, limit, "file is invalid");
    return sha256(bytes);
  } catch {
    return undefined;
  }
}

function bindDirectory(directoryPath: string, label: DirectoryBinding["label"]): DirectoryBinding {
  try {
    const canonical = fs.realpathSync.native(directoryPath);
    const stat = fs.statSync(canonical, { bigint: true });
    if (!stat.isDirectory() || !samePath(canonical, directoryPath)) throw new Error();
    return { path: directoryPath, canonical, device: stat.dev, inode: stat.ino, label };
  } catch {
    throw new Error(`${label} directory identity is invalid`);
  }
}

function directoryIdentityMatches(binding: DirectoryBinding): boolean {
  try {
    const entry = fs.lstatSync(binding.path);
    if (!entry.isDirectory() || entry.isSymbolicLink()) return false;
    const canonical = fs.realpathSync.native(binding.path);
    const stat = fs.statSync(canonical, { bigint: true });
    return samePath(canonical, binding.canonical) && stat.dev === binding.device && stat.ino === binding.inode;
  } catch {
    return false;
  }
}

function assertDirectoryIdentity(binding: DirectoryBinding): void {
  if (!directoryIdentityMatches(binding)) throw new Error(`${binding.label} directory identity changed during write`);
}

function acquireMutationLock(binding: DirectoryBinding): MutationLock {
  const lockPath = path.join(binding.path, MUTATION_LOCK_FILE);
  for (let attempt = 0; attempt < 3; attempt += 1) {
    const nonce = randomUUID();
    const candidate = `${lockPath}.candidate-${nonce}`;
    try {
      writeExclusiveDurableFile(binding, candidate, Buffer.from(`${JSON.stringify({ schema_version: 1, pid: process.pid, nonce })}\n`));
      assertDirectoryIdentity(binding);
      fs.linkSync(candidate, lockPath);
      tryRemove(binding, candidate, false);
      return { path: lockPath, nonce, binding };
    } catch (error) {
      tryRemove(binding, candidate, false);
      const code = isRecord(error) && typeof error.code === "string" ? error.code : "";
      if (code !== "EEXIST") throw new Error("unable to acquire presentation mutation lock");
      const snapshot = readLockSnapshot(lockPath);
      if (processIsLive(snapshot.pid)) throw new Error("another OpenCode presentation setup is already running");
      reclaimLock(binding, lockPath, snapshot);
    }
  }
  throw new Error("another OpenCode presentation setup is already running");
}

function releaseMutationLock(lock: MutationLock): void {
  try {
    if (!directoryIdentityMatches(lock.binding)) return;
    const snapshot = readLockSnapshot(lock.path);
    if (snapshot.nonce !== lock.nonce) return;
    const tombstone = `${lock.path}.released-${randomUUID()}`;
    fs.renameSync(lock.path, tombstone);
    if (!lockSnapshotMatches(tombstone, snapshot)) {
      if (!pathOccupied(lock.path)) fs.renameSync(tombstone, lock.path);
      return;
    }
    fs.rmSync(tombstone);
  } catch {
    // A retained lock fails closed and can only block a later mutation.
  }
}

function readLockSnapshot(filePath: string): { pid: number; nonce: string; device: bigint; inode: bigint } {
  let before: fs.BigIntStats;
  try {
    before = fs.lstatSync(filePath, { bigint: true });
  } catch {
    throw new Error("presentation mutation lock is invalid");
  }
  let value: unknown;
  try {
    value = readControlJson(filePath);
  } catch {
    throw new Error("presentation mutation lock is invalid");
  }
  let after: fs.BigIntStats;
  try {
    after = fs.lstatSync(filePath, { bigint: true });
  } catch {
    throw new Error("presentation mutation lock is invalid");
  }
  if (!before.isFile() || before.isSymbolicLink() || before.dev !== after.dev || before.ino !== after.ino
    || !isRecord(value)
    || value.schema_version !== 1
    || !Number.isSafeInteger(value.pid)
    || (value.pid as number) <= 0
    || typeof value.nonce !== "string"
    || !/^[a-f0-9-]{36}$/.test(value.nonce)) throw new Error("presentation mutation lock is invalid");
  return { pid: value.pid as number, nonce: value.nonce, device: before.dev, inode: before.ino };
}

function reclaimLock(binding: DirectoryBinding, lockPath: string, snapshot: ReturnType<typeof readLockSnapshot>): void {
  const tombstone = `${lockPath}.stale-${randomUUID()}`;
  assertDirectoryIdentity(binding);
  fs.renameSync(lockPath, tombstone);
  if (!lockSnapshotMatches(tombstone, snapshot)) {
    if (!pathOccupied(lockPath)) fs.renameSync(tombstone, lockPath);
    throw new Error("presentation mutation lock changed during reclamation");
  }
  fs.rmSync(tombstone);
}

function lockSnapshotMatches(filePath: string, snapshot: ReturnType<typeof readLockSnapshot>): boolean {
  try {
    const stat = fs.lstatSync(filePath, { bigint: true });
    const value = readControlJson(filePath);
    return stat.isFile()
      && !stat.isSymbolicLink()
      && stat.dev === snapshot.device
      && stat.ino === snapshot.inode
      && isRecord(value)
      && value.pid === snapshot.pid
      && value.nonce === snapshot.nonce;
  } catch {
    return false;
  }
}

function processIsLive(pid: number): boolean {
  try {
    process.kill(pid, 0);
    return true;
  } catch (error) {
    return !(isRecord(error) && error.code === "ESRCH");
  }
}

function writeControlFileAtomic(binding: DirectoryBinding, target: string, value: Record<string, unknown>): void {
  const temporary = `${target}.tmp-${randomUUID()}`;
  const content = Buffer.from(`${JSON.stringify(value)}\n`);
  if (content.length > MAX_CONTROL_FILE_BYTES) throw new Error("presentation transaction record is too large");
  let descriptor: number | undefined;
  try {
    assertDirectoryIdentity(binding);
    descriptor = fs.openSync(temporary, "wx", 0o600);
    fs.writeFileSync(descriptor, content);
    fs.fsyncSync(descriptor);
    fs.closeSync(descriptor);
    descriptor = undefined;
    assertDirectoryIdentity(binding);
    if (pathOccupied(target)) throw new Error("presentation transaction record already exists");
    fs.renameSync(temporary, target);
    fsyncDirectoryBestEffort(binding);
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
    if (pathOccupied(temporary)) tryRemove(binding, temporary, false);
  }
}

function writeExclusiveDurableFile(binding: DirectoryBinding, target: string, content: Buffer): void {
  if (content.length > MAX_CONTROL_FILE_BYTES) throw new Error("presentation control file is too large");
  let descriptor: number | undefined;
  try {
    assertDirectoryIdentity(binding);
    descriptor = fs.openSync(target, "wx", 0o600);
    fs.writeFileSync(descriptor, content);
    fs.fsyncSync(descriptor);
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
  }
}

function readControlJson(filePath: string): unknown {
  const bytes = readBoundedRegularFile(filePath, MAX_CONTROL_FILE_BYTES, "presentation control file is invalid");
  try {
    return JSON.parse(bytes.toString("utf8"));
  } catch {
    throw new Error("presentation control file is invalid");
  }
}

function fsyncDirectoryBestEffort(binding: DirectoryBinding): void {
  let descriptor: number | undefined;
  try {
    assertDirectoryIdentity(binding);
    descriptor = fs.openSync(binding.path, "r");
    fs.fsyncSync(descriptor);
  } catch {
    // Windows and some filesystems do not expose directory fsync through Node.
  } finally {
    if (descriptor !== undefined) fs.closeSync(descriptor);
  }
}

function samePath(left: string, right: string): boolean {
  try {
    const normalizedLeft = canonicalPaths.normalize(left);
    const normalizedRight = canonicalPaths.normalize(right);
    return process.platform === "win32" || process.platform === "darwin"
      ? normalizedLeft.toLowerCase() === normalizedRight.toLowerCase()
      : normalizedLeft === normalizedRight;
  } catch {
    return false;
  }
}

function absoluteHostPath(input: string, cwd: string): string {
  const windowsDrivePath = /^[A-Za-z]:[\\/]/.test(input);
  const wslDrivePath = /^\/mnt\/[A-Za-z](?:\/|$)/.test(input);
  if (/^[A-Za-z]:(?![\\/])/.test(input) || /^\\/.test(input) || /^\/\//.test(input)) {
    throw new Error("foreign absolute path is unavailable on this host");
  }
  if (process.platform === "win32") {
    if (wslDrivePath) return requireHostAlias(input, "windows");
    if (windowsDrivePath) return path.join(input, ".");
    if (path.isAbsolute(input)) throw new Error("foreign absolute path is unavailable on this host");
  } else {
    if (windowsDrivePath) {
      if (process.platform !== "linux") throw new Error("foreign absolute path is unavailable on this host");
      return requireHostAlias(input, "wsl");
    }
    if (path.isAbsolute(input)) return path.join(input, ".");
  }
  return path.join(cwd, input);
}

function requireHostAlias(input: string, environment: "windows" | "wsl"): string {
  const alias = canonicalPaths.projectRootAliases(input).find((candidate) => candidate.environment === environment);
  if (!alias) throw new Error("foreign absolute path is unavailable on this host");
  return alias.host_path;
}

function tryRemove(binding: DirectoryBinding, filePath: string, recursive: boolean): boolean {
  try {
    assertDirectoryIdentity(binding);
    fs.rmSync(filePath, { recursive, force: true });
    return true;
  } catch {
    // The committed generation remains authoritative; a hidden backup can be cleaned manually.
    return false;
  }
}

function sha256(bytes: Buffer): string {
  return createHash("sha256").update(bytes).digest("hex");
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return Boolean(value) && typeof value === "object" && !Array.isArray(value);
}

function boundedError(error: unknown): string {
  const message = error instanceof Error ? error.message : String(error);
  return message.replace(/[\u0000-\u001f\u007f-\u009f]/g, " ").slice(0, 240) || "unknown error";
}

function safeOutputText(value: string, limit: number): string {
  return value
    .replace(/\u001b\][^\u0007]*(?:\u0007|\u001b\\)/g, "")
    .replace(/\u001b\[[0-?]*[ -/]*[@-~]/g, "")
    .replace(/[\u0000-\u001f\u007f-\u009f\u202a-\u202e\u2066-\u2069]/g, " ")
    .slice(0, limit);
}
