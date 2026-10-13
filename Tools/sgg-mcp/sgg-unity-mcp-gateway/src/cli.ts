#!/usr/bin/env node
/*! SGG Unity MCP Gateway licensing: see THIRD_PARTY_NOTICES.md for bundled third-party licenses. */

import fs from "node:fs";
import { createHash, randomUUID } from "node:crypto";
import { isIP } from "node:net";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { isMainThread, parentPort, workerData } from "node:worker_threads";
import type { EventEmitter } from "node:events";

import { serveStdio, StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { applyEdits, findNodeAtLocation, modify, parse, parseTree, visit, type FormattingOptions, type ParseError } from "jsonc-parser/lib/esm/main.js";

import { runApprovalCli } from "./approvals/approval-cli.js";
import { loadGatewayConfig } from "./config/config.js";
import { inspectCurrentPolicyFile, inspectCurrentPolicyFileResult, PolicyManager } from "./policy/policy-manager.js";
import { inspectUnityCrashReporter, type UnityCrashReporterRuntime, type UnityCrashReporterStatus } from "./diagnostics/unity-crash-reporter.js";
import { PROBLEM_REPORT_MAX_REGISTRY_RECORDS, PROBLEM_REPORT_SCHEMA_VERSION, serializeProblemReport, summarizeProblemCrashReporter } from "./diagnostics/problem-report.js";
import { createUnityProcessGenerationInspector } from "./diagnostics/unity-processes.js";
import { createGatewayContext } from "./gateway/context.js";
import { LEGACY_MCP_PROTOCOL_VERSIONS, startHttpMcpServer } from "./mcp/http-server.js";
import { TOOL_NAMES } from "./mcp/contracts.js";
import { ClientRootsRegistry } from "./mcp/client-roots.js";
import { createUnityMcpServer, RECOMMENDED_MCP_CLIENT_TIMEOUT_MS } from "./mcp/sdk-server.js";
import { createMcpV2Metrics, createUnityMcpV2ServerFactory, mcpV2MetricsHealth, MODERN_MCP_PROTOCOL_VERSION, MODERN_MCP_SDK_VERSION } from "./mcp/sdk-v2-server.js";
import { assertMcpSdkCompatible, LEGACY_MCP_SDK_VERSION } from "./mcp/sdk-compat.js";
import { runStdioShim, STDIO_SHIM_PROTOCOL_POSTURE } from "./mcp/stdio-shim.js";
import { runPolicyCli } from "./policy/policy-cli.js";
import { ProjectRegistry, publicRegistryOperationalStatus, type ProjectRegistrySnapshot, type ProjectRegistrySnapshotResolution, type PublicProjectRecord } from "./registry/project-registry.js";
import { OperationalLogger } from "./observability/operational-logger.js";
import { opencodePresentationSetupHelpText, runOpencodePresentationSetupCli } from "./setup/opencode-presentation.js";
import { runTestRunOperationStoreWorker } from "./operations/test-run-operation-worker.js";
import { runTestRunStoreAdminCli } from "./operations/test-run-store-admin-cli.js";

type CliOptions = {
  configPath?: string;
  policyPath?: string;
  transport: "http" | "stdio";
  mcpPath: string;
  help: boolean;
};

type CliIo = {
  stdout?: (text: string) => void;
  stderr?: (text: string) => void;
  env?: NodeJS.ProcessEnv;
  cwd?: string;
  now?: () => Date;
  sleep?: (ms: number) => Promise<void>;
  unityCrashReporter?: UnityCrashReporterRuntime;
  presentationArtifactRoot?: string;
};

export type StdioShimCliOptions = {
  url?: string;
  configPath?: string;
  mcpPath: string;
  tokenEnv: string;
  legacyTextResult?: boolean;
  help: boolean;
};

type SetupTarget = "opencode" | "cursor" | "assistant";
type OpencodeConnectionMode = "remote-http" | "windows-stdio";

export type SetupCliOptions = {
  target: SetupTarget;
  configPath?: string;
  outputPath?: string;
  url?: string;
  connectionMode: OpencodeConnectionMode;
  connectionModeExplicit?: boolean;
  mcpPath: string;
  write: boolean;
  json: boolean;
  help: boolean;
};

type SetupPlan = {
  target: SetupTarget;
  path: string;
  mode: "dry-run" | "write";
  connection_mode?: OpencodeConnectionMode;
  endpoint_recommendation?: Record<string, unknown>;
  changes: string[];
  patch: Record<string, unknown>;
  notes: string[];
  checklist: Record<string, unknown>[];
};

export async function main(argv: string[] = process.argv.slice(2), env: NodeJS.ProcessEnv = process.env): Promise<void> {
  assertMcpSdkCompatible();
  if (argv.includes("--test-run-store")) {
    process.exitCode = runTestRunStoreAdminCli(argv, { env });
    return;
  }
  if (argv[0] === "policy") {
    process.exitCode = runPolicyCli(argv.slice(1));
    return;
  }
  if (argv[0] === "approval") {
    process.exitCode = runApprovalCli(argv.slice(1), { env });
    return;
  }
  if (argv[0] === "doctor") {
    process.exitCode = runDoctorCli(argv.slice(1), { env });
    return;
  }
  if (argv[0] === "wait-ready") {
    process.exitCode = await runWaitReadyCli(argv.slice(1), { env });
    return;
  }
  if (argv[0] === "report-problem") {
    process.exitCode = runReportProblemCli(argv.slice(1), { env, cwd: process.cwd() });
    return;
  }
  if (argv[0] === "setup") {
    process.exitCode = runSetupCli(argv.slice(1), { env, cwd: process.cwd() });
    return;
  }
  if (argv[0] === "stdio-shim") {
    process.exitCode = await runStdioShimCli(argv.slice(1), { env, cwd: process.cwd() });
    return;
  }

  const cli = parseCliOptions(argv);
  if (cli.help) {
    process.stderr.write(helpText());
    return;
  }

  const configPath = cli.configPath ?? env.SGG_UNITY_MCP_CONFIG ?? defaultConfigPath(process.cwd(), env);
  const policyPath = cli.policyPath ?? env.SGG_UNITY_MCP_POLICY;
  const config = loadGatewayConfig(configPath, env);
  const runId = randomUUID();
  const operationalLogger = new OperationalLogger({ level: config.logging.level, runId });
  const token = config.mcp.token_env ? env[config.mcp.token_env] : undefined;
  const version = readPackageVersion();
  operationalLogger.log("SGG-OP-STARTUP-001", cli.transport, config.logging.level, config.logging.audit_enabled, policyPath !== undefined, token !== undefined && token.length > 0, version);
  const policyManager = new PolicyManager(policyPath, { logger: operationalLogger });
  const policy = policyManager.policy;
  const context = createGatewayContext(config, policy, {
    configPath,
    ...(policyPath ? { policyPath } : {}),
    policyManager,
    mcpTokenPresent: token !== undefined && token.length > 0,
    runId,
    gatewayRuntimeVersion: version,
    operationalLogger,
    ...(path.extname(fileURLToPath(import.meta.url)) === ".mjs" ? { testRunOperationWorkerEntry: new URL(import.meta.url) } : {}),
  });
  const clientRootsRegistry = new ClientRootsRegistry();
  const mcpV2Metrics = createMcpV2Metrics();
  const modernServerFactory = createUnityMcpV2ServerFactory(context, { version, clientRootsRegistry, metrics: mcpV2Metrics });
  const closeContext = async () => {
    try { await context.testRunOperationPublisher?.close(); }
    finally { context.registry?.close(); }
  };
  if (cli.transport === "stdio") {
    const transport = new StdioServerTransport();
    const running = serveStdio(modernServerFactory, {
      transport,
      onerror: () => process.stderr.write("MCP stdio transport error\n"),
    });
    await waitForShutdown(async () => {
      // Begin bounded publication cleanup immediately, not after a server that
      // might still be waiting for that same immutable publication.
      const contextClosed = closeContext();
      try { await running.close(); }
      finally { await contextClosed; }
    }, { input: process.stdin, transport });
    return;
  }

  const running = await startHttpMcpServer(() => createUnityMcpServer(context, { version, clientRootsRegistry }), {
    host: config.mcp.host,
    port: config.mcp.port,
    path: cli.mcpPath,
    allowedOrigins: config.mcp.allowed_origins,
    allowNonLoopback: config.mcp.allow_non_loopback,
    maxBodyBytes: config.mcp.max_body_bytes,
    requestTimeoutMs: config.mcp.request_timeout_ms,
    shutdownTimeoutMs: config.mcp.shutdown_timeout_ms,
    sessions: {
      maxActive: config.mcp.sessions.max_active,
      idleTtlMs: config.mcp.sessions.idle_ttl_ms,
      cleanupIntervalMs: config.mcp.sessions.cleanup_interval_ms,
    },
    rateLimit: {
      windowMs: config.mcp.rate_limit.window_ms,
      maxRequests: config.mcp.rate_limit.max_requests,
      maxBuckets: config.mcp.rate_limit.max_buckets,
      cleanupIntervalMs: config.mcp.rate_limit.cleanup_interval_ms,
      trustedProxy: config.mcp.rate_limit.trusted_proxy,
      trustedProxyPeers: config.mcp.rate_limit.trusted_proxy_peers,
    },
    ...(token ? { token } : {}),
    health: () => {
      policyManager.reload();
      const stats = context.registry?.getStats({ cleanup: true });
      return {
        version,
        mcp_path: cli.mcpPath,
        mcp_sdk_version: MODERN_MCP_SDK_VERSION,
        legacy_mcp_sdk_version: LEGACY_MCP_SDK_VERSION,
        modern_protocol_versions: [MODERN_MCP_PROTOCOL_VERSION],
        legacy_protocol_versions: LEGACY_MCP_PROTOCOL_VERSIONS,
        stdio_shim_protocol_posture: STDIO_SHIM_PROTOCOL_POSTURE,
        modern_client_roots: "not_applicable",
        ...mcpV2MetricsHealth(mcpV2Metrics),
        projects_count: stats?.liveProjectsCount ?? 0,
        live_projects_count: stats?.liveProjectsCount ?? 0,
        stale_projects_count: stats?.staleProjectsCount ?? 0,
        unique_projects_count: stats?.uniqueProjectsCount ?? 0,
        project_records_count: stats?.projectRecordsCount ?? 0,
        live_project_records_count: stats?.liveProjectRecordsCount ?? 0,
        stale_project_records_count: stats?.staleProjectRecordsCount ?? 0,
        invalid_project_records_count: stats?.invalidProjectRecordsCount ?? 0,
        protocol_version_mismatch_project_records_count: stats?.protocolVersionMismatchProjectRecordsCount ?? 0,
        removed_project_records_count: stats?.removedProjectRecordsCount ?? 0,
        registry: publicRegistryOperationalStatus(stats),
        degraded: policyManager.degraded,
        policy: policyManager.snapshot(),
        ...(context.audit?.snapshotStatus ? { audit: context.audit.snapshotStatus() } : {}),
        ...(context.approvalStore ? { approvals: context.approvalStore.snapshotStatus() } : {}),
        client_roots: clientRootsRegistry.health(),
        logger: operationalLogger.status(),
      };
    },
    logger: operationalLogger,
    modernServerFactory,
  });

  await waitForShutdown(async () => {
    const contextClosed = closeContext();
    try { await running.close(); }
    finally { await contextClosed; }
  });
}

export function parseCliOptions(argv: string[]): CliOptions {
  const options: CliOptions = {
    transport: "http",
    mcpPath: "/mcp",
    help: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
    } else if (arg === "--stdio") {
      options.transport = "stdio";
    } else if (arg === "--config") {
      options.configPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--policy") {
      options.policyPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--path") {
      options.mcpPath = requiredValue(argv, (index += 1), arg);
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  return options;
}

export async function runStdioShimCli(argv: string[], io: CliIo = {}): Promise<number> {
  const stderr = io.stderr ?? ((text: string) => process.stderr.write(text));
  const env = io.env ?? process.env;
  const cwd = io.cwd ?? process.cwd();
  let options: StdioShimCliOptions;

  try {
    options = parseStdioShimOptions(argv);
  } catch (error) {
    stderr(`${safeErrorMessage(error)}\n`);
    return 1;
  }

  if (options.help) {
    stderr(stdioShimHelpText());
    return 0;
  }

  try {
    const resolved = resolveStdioShimConnection(options, env, cwd);
    await runStdioShim({
      url: resolved.url,
      version: readPackageVersion(),
      ...(resolved.token ? { token: resolved.token } : {}),
      ...(options.legacyTextResult ? { legacyTextResult: true } : {}),
    });
    return 0;
  } catch (error) {
    stderr(`stdio-shim failed: ${safeErrorMessage(error)}\n`);
    stderr("Start the HTTP Gateway first and verify --url, --config, --path, and token environment settings.\n");
    return 1;
  }
}

export function parseStdioShimOptions(argv: string[]): StdioShimCliOptions {
  const options: StdioShimCliOptions = {
    mcpPath: "/mcp",
    tokenEnv: "SGG_UNITY_MCP_TOKEN",
    legacyTextResult: false,
    help: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
    } else if (arg === "--url") {
      options.url = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--config") {
      options.configPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--path") {
      options.mcpPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--token-env") {
      options.tokenEnv = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--legacy-text-result") {
      options.legacyTextResult = true;
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  return options;
}

export function resolveStdioShimConnection(options: StdioShimCliOptions, env: NodeJS.ProcessEnv = process.env, cwd = process.cwd()): { url: string; token?: string; tokenEnv: string } {
  let tokenEnv = options.tokenEnv;
  let url = options.url ?? env.SGG_UNITY_MCP_URL;

  if (!url || options.configPath) {
    const configPath = options.configPath ?? env.SGG_UNITY_MCP_CONFIG ?? defaultConfigPath(cwd, env);
    const config = loadGatewayConfig(configPath, env);
    url ??= `http://${config.mcp.host}:${config.mcp.port}${options.mcpPath}`;
    if (options.tokenEnv === "SGG_UNITY_MCP_TOKEN" && config.mcp.token_env) {
      tokenEnv = config.mcp.token_env;
    }
  }

  const parsedUrl = new URL(url);
  if (parsedUrl.username || parsedUrl.password) {
    throw new Error("stdio-shim URL must not contain credentials");
  }
  const token = env[tokenEnv];
  return {
    url,
    tokenEnv,
    ...(token ? { token } : {}),
  };
}

export function runSetupCli(argv: string[], io: CliIo = {}): number {
  const stdout = io.stdout ?? ((text: string) => process.stdout.write(text));
  const stderr = io.stderr ?? ((text: string) => process.stderr.write(text));
  const env = io.env ?? process.env;
  const cwd = io.cwd ?? process.cwd();
  if (argv[0] === "opencode-presentation") {
    return runOpencodePresentationSetupCli(argv.slice(1), {
      stdout,
      stderr,
      cwd,
      ...(io.presentationArtifactRoot ? { artifactRoot: io.presentationArtifactRoot } : {}),
    });
  }
  let options: SetupCliOptions;

  try {
    options = parseSetupOptions(argv);
  } catch (error) {
    stderr(`${safeErrorMessage(error)}\n`);
    return 1;
  }

  if (options.help) {
    stderr(setupHelpText());
    return 0;
  }

  try {
    const plan = createSetupPlan(options, env, cwd);
    if (options.write) {
      applySetupPlan(plan);
    }
    writeSetupOutput(stdout, options.json, plan);
    return 0;
  } catch (error) {
    stderr(`setup failed: ${safeErrorMessage(error)}\n`);
    return 1;
  }
}

export function parseSetupOptions(argv: string[]): SetupCliOptions {
  const first = argv[0];
  if (!first || first === "--help" || first === "-h") {
    return { target: "opencode", connectionMode: "remote-http", mcpPath: "/mcp", write: false, json: false, help: true };
  }
  if (!isSetupTarget(first)) {
    throw new Error("setup target must be one of: opencode, cursor, assistant");
  }

  const options: SetupCliOptions = {
    target: first,
    connectionMode: "remote-http",
    mcpPath: "/mcp",
    write: false,
    json: false,
    help: false,
  };

  for (let index = 1; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      options.help = true;
    } else if (arg === "--config") {
      options.configPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--output") {
      options.outputPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--url") {
      options.url = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--connection-mode") {
      options.connectionMode = parseOpencodeConnectionMode(requiredValue(argv, (index += 1), arg));
      options.connectionModeExplicit = true;
    } else if (arg === "--path") {
      options.mcpPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--write") {
      options.write = true;
    } else if (arg === "--dry-run") {
      options.write = false;
    } else if (arg === "--json") {
      options.json = true;
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  return options;
}

function createSetupPlan(options: SetupCliOptions, env: NodeJS.ProcessEnv, cwd: string): SetupPlan {
  const configPath = options.configPath ?? env.SGG_UNITY_MCP_CONFIG ?? defaultConfigPath(cwd, env);
  const outputPath = path.resolve(cwd, options.outputPath ?? (options.target === "opencode" ? defaultOpencodeSetupOutputPath(cwd) : defaultSetupOutputPath(options.target)));
  const config = loadGatewayConfig(configPath, env);
  const tokenEnv = config.mcp.token_env;
  const endpointRecommendation = wslEndpointRecommendation(config.mcp.host, config.mcp.port, options.mcpPath, env, {
    allowNonLoopback: config.mcp.allow_non_loopback,
    tokenEnv,
    tokenPresent: tokenEnv ? Boolean(env[tokenEnv]) : false,
    allowedOrigins: config.mcp.allowed_origins,
    requestedUrl: options.url,
  });
  const effectiveOptions = options.target === "opencode"
    && options.connectionModeExplicit !== true
    && endpointRecommendation?.recommended_connection_mode === "windows_stdio_shim"
    ? { ...options, connectionMode: "windows-stdio" as const }
    : options;
  if (options.target === "opencode" && effectiveOptions.connectionMode === "remote-http" && endpointRecommendation && endpointRecommendation.protected_remote_http_eligible !== true) {
    throw new Error(`${String(endpointRecommendation?.code ?? "remote_http_not_ready")}: WSL Remote HTTP requires a concrete non-loopback URL, allow_non_loopback, a present token, and allowed_origins; use --connection-mode windows-stdio`);
  }
  const notes: string[] = [];
  if (tokenEnv) {
    notes.push(`token value is not written; configure ${tokenEnv} in the client environment or secret store`);
  }
  if (options.target === "opencode" && effectiveOptions.connectionMode === "windows-stdio") {
    notes.push("windows-stdio keeps the Gateway bound to Windows loopback and runs the stdio shim through powershell.exe");
    notes.push("windows-stdio is a legacy MCP compatibility transport; use direct HTTP or direct stdio for MCP 2026-07-28 and MRTR");
  }
  if (options.target === "opencode" && options.connectionModeExplicit !== true && effectiveOptions.connectionMode === "windows-stdio") {
    notes.push("WSL detected with a Windows loopback Gateway; windows-stdio was selected as the safe default instead of an unreachable WSL 127.0.0.1 remote URL");
  }
  if (options.target === "opencode") {
    notes.push("OpenCode v2 must load project or global MCP config before exposing the Gateway. Code Mode is enabled by default: discover sgg_unity_* in its catalog, not only in the provider-native tool list; diagnose target-client config/API exposure only after both checks.");
    notes.push("Setup updates the selected file only; active client configuration, ancestor overrides and visual rendering require separate verification.");
  }

  const recommendedOptions = endpointRecommendation?.protected_remote_http_url && effectiveOptions.connectionMode === "remote-http"
    ? { ...effectiveOptions, url: String(endpointRecommendation.protected_remote_http_url) }
    : effectiveOptions;
  const setupUrl = options.target === "opencode" ? validatedOpencodeSetupUrl(recommendedOptions, config, env) : undefined;
  const patchOptions = setupUrl ? { ...recommendedOptions, url: setupUrl } : recommendedOptions;
  const existing = readOptionalJsonObject(outputPath);
  if (options.target === "opencode") assertOpencodeSetupTarget(outputPath, existing);
  const generatedPatch = setupPatch(patchOptions, configPath, config, tokenEnv);
  const patch = options.target === "opencode" ? preserveExistingOpencodeTimeout(existing, generatedPatch) : generatedPatch;
  const next = options.target === "opencode" ? mergeOpencodeConfig(existing, patch) : mergeObjects(existing, patch);
  const changes = setupChanges(existing, next, options.target);
  return {
    target: options.target,
    path: outputPath,
    mode: options.write ? "write" : "dry-run",
    ...(options.target === "opencode" ? { connection_mode: effectiveOptions.connectionMode } : {}),
    ...(endpointRecommendation ? { endpoint_recommendation: endpointRecommendation } : {}),
    changes,
    patch,
    notes,
    checklist: setupChecklist(options.target, outputPath),
  };
}

function preserveExistingOpencodeTimeout(existing: Record<string, unknown>, patch: Record<string, unknown>): Record<string, unknown> {
  const server = opencodeOwnedServer(existing);
  const defaults = readNested(existing, ["mcp", "timeout"]);
  const legacyDefault = readNested(existing, ["experimental", "mcp_timeout"]);
  const timeout = server?.timeout;
  const nativeTimeout = isPlainObject(timeout) ? timeout : undefined;
  const inheritedExecution = (isPlainObject(defaults) ? defaults.execution : undefined) ?? legacyDefault;
  const execution = typeof timeout === "number" ? timeout : nativeTimeout?.execution ?? inheritedExecution;
  const preservedTimeout: Record<string, unknown> = {
    ...(isPositiveTimeout(legacyDefault) ? { catalog: legacyDefault } : {}),
    ...(isPlainObject(defaults) ? defaults : {}),
    ...(nativeTimeout ?? {}),
    execution: isPositiveTimeout(execution) ? Math.max(execution, RECOMMENDED_MCP_CLIENT_TIMEOUT_MS) : RECOMMENDED_MCP_CLIENT_TIMEOUT_MS,
  };
  const disabled = server?.disabled ?? (typeof server?.enabled === "boolean" ? !server.enabled : false);
  return mergeObjects(patch, { mcp: { servers: { "sgg-unity-mcp": { timeout: preservedTimeout, disabled } } } });
}

function isPositiveTimeout(value: unknown): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value > 0;
}

function opencodeOwnedServer(config: Record<string, unknown>): Record<string, unknown> | undefined {
  const native = readNested(config, ["mcp", "servers", "sgg-unity-mcp"]);
  const legacy = readNested(config, ["mcp", "sgg-unity-mcp"]);
  return isPlainObject(native) ? native : isPlainObject(legacy) ? legacy : undefined;
}

function setupPatch(options: SetupCliOptions, configPath: string, config: ReturnType<typeof loadGatewayConfig>, tokenEnv: string | undefined): Record<string, unknown> {
  const serverName = "sgg-unity-mcp";
  if (options.target === "opencode") {
    const url = options.url ?? `http://${config.mcp.host}:${config.mcp.port}${options.mcpPath}`;
    if (options.connectionMode === "windows-stdio") {
      const shimHost = isWildcardHost(config.mcp.host) ? "127.0.0.1" : config.mcp.host;
      const shimUrl = options.url ?? gatewayHttpUrl(shimHost, config.mcp.port, options.mcpPath);
      return {
        mcp: {
          servers: { [serverName]: {
            type: "local",
            command: windowsStdioShimCommand(shimUrl, tokenEnv ?? "SGG_UNITY_MCP_TOKEN"),
            disabled: false,
            protocol: "legacy",
            timeout: { startup: 30_000, catalog: 30_000, execution: RECOMMENDED_MCP_CLIENT_TIMEOUT_MS },
          } },
        },
      };
    }

    return {
      mcp: {
        servers: { [serverName]: {
          type: "remote",
          url,
          ...(tokenEnv ? { headers: { "X-SGG-Unity-MCP-Token": `{env:${tokenEnv}}` } } : {}),
          oauth: false,
          disabled: false,
          timeout: { startup: 30_000, catalog: 30_000, execution: RECOMMENDED_MCP_CLIENT_TIMEOUT_MS },
        } },
      },
    };
  }

  if (options.target === "assistant") {
    return {
      enabled: true,
      path: "",
      mcpServers: {
        [serverName]: {
          type: "stdio",
          command: "sgg-unity-mcp-gateway",
          args: ["stdio-shim", "--config", configPath],
        },
      },
    };
  }

  return {
    mcpServers: {
      [serverName]: {
        command: "sgg-unity-mcp-gateway",
        args: ["stdio-shim", "--config", configPath],
      },
    },
  };
}

function applySetupPlan(plan: SetupPlan): void {
  if (plan.target === "opencode") {
    const source = fs.existsSync(plan.path) ? fs.readFileSync(plan.path, "utf8") : "{}\n";
    const next = patchOpencodeConfig(source, plan.patch);
    if (next !== source) writeTextAtomic(plan.path, next);
    return;
  }
  const existing = readOptionalJsonObject(plan.path);
  const next = mergeObjects(existing, plan.patch);
  writeJsonAtomic(plan.path, next);
}

function patchOpencodeConfig(source: string, patch: Record<string, unknown>): string {
  const serverPath = ["mcp", "servers", "sgg-unity-mcp"];
  const generatedServer = readNested(patch, serverPath);
  if (!isPlainObject(generatedServer)) {
    throw new Error("OpenCode setup patch is missing the owned MCP server entry");
  }

  const formattingOptions = detectJsoncFormatting(source);
  let next = migrateOwnedOpencodeServer(source, formattingOptions);
  const existing = parseJsoncObject(next, "OpenCode setup config");
  const stalePaths = generatedServer.type === "local"
    ? [[...serverPath, "url"], [...serverPath, "oauth"], [...serverPath, "headers", "X-SGG-Unity-MCP-Token"]]
    : [[...serverPath, "command"], ...(generatedServer.headers === undefined
      ? [[...serverPath, "headers", "X-SGG-Unity-MCP-Token"]]
      : [])];
  stalePaths.push([...serverPath, "enabled"]);
  if (generatedServer.type === "remote") {
    stalePaths.push([...serverPath, "command"]);
  }
  for (const stalePath of stalePaths) {
    if (readNested(existing, stalePath) === undefined) continue;
    next = applyEdits(next, modify(next, stalePath, undefined, { formattingOptions }));
  }
  for (const [field, value] of Object.entries(generatedServer)) {
    if (field === "headers" && isPlainObject(value)) {
      for (const [header, headerValue] of Object.entries(value)) {
        next = applyEdits(next, modify(next, [...serverPath, "headers", header], headerValue, { formattingOptions }));
      }
    } else if (field === "timeout" && isPlainObject(value)) {
      const previous = readNested(existing, [...serverPath, "timeout"]);
      if (previous !== undefined && !isPlainObject(previous)) next = applyEdits(next, modify(next, [...serverPath, "timeout"], {}, { formattingOptions }));
      for (const [purpose, milliseconds] of Object.entries(value)) {
        next = applyEdits(next, modify(next, [...serverPath, "timeout", purpose], milliseconds, { formattingOptions }));
      }
    } else {
      next = applyEdits(next, modify(next, [...serverPath, field], value, { formattingOptions }));
    }
  }
  return next;
}

function mergeOpencodeConfig(existing: Record<string, unknown>, patch: Record<string, unknown>): Record<string, unknown> {
  const migrated = parseJsoncObject(migrateOwnedOpencodeServer(JSON.stringify(existing), { insertSpaces: true, tabSize: 2, eol: "\n" }), "OpenCode setup config");
  const next = mergeObjects(migrated, patch);
  const generatedServer = readNested(patch, ["mcp", "servers", "sgg-unity-mcp"]);
  const nextServer = readNested(next, ["mcp", "servers", "sgg-unity-mcp"]);
  if (!isPlainObject(generatedServer) || !isPlainObject(nextServer)) return next;
  const nextHeaders = isPlainObject(nextServer.headers) ? nextServer.headers : undefined;
  if (generatedServer.type === "local") {
    delete nextServer.url;
    delete nextServer.oauth;
    if (nextHeaders) delete nextHeaders["X-SGG-Unity-MCP-Token"];
  } else {
    delete nextServer.command;
    if (generatedServer.headers === undefined && nextHeaders) delete nextHeaders["X-SGG-Unity-MCP-Token"];
  }
  delete nextServer.enabled;
  return next;
}

function migrateOwnedOpencodeServer(source: string, formattingOptions: FormattingOptions): string {
  const config = parseJsoncObject(source, "OpenCode setup config");
  const legacyPath = ["mcp", "sgg-unity-mcp"];
  const legacy = readNested(config, legacyPath);
  if (legacy === undefined) return source;
  if (readNested(config, ["mcp", "servers", "sgg-unity-mcp"]) !== undefined) throw new Error("OpenCode setup has competing legacy and native owned MCP entries; resolve the conflict before writing");
  const node = findNodeAtLocation(parseTree(source)!, legacyPath);
  if (!node || !isPlainObject(legacy)) throw new Error("OpenCode owned MCP entry must be an object");
  const originalObject = source.slice(node.offset, node.offset + node.length);
  let next = applyEdits(source, modify(source, legacyPath, undefined, { formattingOptions }));
  next = applyEdits(next, modify(next, ["mcp", "servers", "sgg-unity-mcp"], legacy, { formattingOptions }));
  const moved = findNodeAtLocation(parseTree(next)!, ["mcp", "servers", "sgg-unity-mcp"]);
  if (!moved) throw new Error("OpenCode owned MCP entry migration failed");
  return next.slice(0, moved.offset) + originalObject + next.slice(moved.offset + moved.length);
}

function defaultOpencodeSetupOutputPath(cwd: string): string {
  const choices: string[] = [];
  for (const root of [path.join(cwd, ".opencode"), cwd]) {
    if (fs.existsSync(root) && fs.lstatSync(root).isSymbolicLink()) throw new Error("OpenCode config directory must not be a symlink");
    const json = path.join(root, "opencode.json");
    const jsonc = path.join(root, "opencode.jsonc");
    if (fs.existsSync(json) && fs.existsSync(jsonc)) throw new Error("Both OpenCode config files exist in one layer; choose neither until the conflict is resolved");
    if (fs.existsSync(json)) choices.push(json);
    if (fs.existsSync(jsonc)) choices.push(jsonc);
  }
  return choices[0] ?? path.join(cwd, "opencode.jsonc");
}

function assertOpencodeSetupTarget(outputPath: string, config: Record<string, unknown>): void {
  if (fs.existsSync(outputPath) && (!fs.lstatSync(outputPath).isFile() || fs.lstatSync(outputPath).isSymbolicLink())) throw new Error("OpenCode config must be a regular non-symlink file");
  if (fs.existsSync(path.dirname(outputPath)) && fs.lstatSync(path.dirname(outputPath)).isSymbolicLink()) throw new Error("OpenCode config directory must not be a symlink");
  for (const segments of [["mcp"], ["mcp", "servers"], ["mcp", "sgg-unity-mcp"], ["mcp", "servers", "sgg-unity-mcp"], ["mcp", "timeout"]]) {
    const value = readNested(config, segments);
    if (value !== undefined && !isPlainObject(value)) throw new Error("OpenCode owned MCP containers must be objects");
  }
  if (readNested(config, ["mcp", "sgg-unity-mcp"]) !== undefined && readNested(config, ["mcp", "servers", "sgg-unity-mcp"]) !== undefined) throw new Error("OpenCode setup has competing legacy and native owned MCP entries; resolve the conflict before writing");
  const server = opencodeOwnedServer(config);
  if (server?.disabled !== undefined && typeof server.disabled !== "boolean") throw new Error("OpenCode disabled must be a boolean");
  if (server?.enabled !== undefined && typeof server.enabled !== "boolean") throw new Error("Legacy OpenCode enabled must be a boolean");
  if (typeof server?.disabled === "boolean" && typeof server.enabled === "boolean" && server.disabled === server.enabled) throw new Error("OpenCode enabled/disabled settings conflict; resolve them before setup");
  for (const timeout of [server?.timeout, readNested(config, ["mcp", "timeout"])]) {
    if (timeout === undefined) continue;
    if (isPositiveTimeout(timeout)) continue;
    if (!isPlainObject(timeout) || Object.values(timeout).some((value) => !isPositiveTimeout(value))) throw new Error("OpenCode timeouts must be positive integer milliseconds");
  }
  const legacyDefault = readNested(config, ["experimental", "mcp_timeout"]);
  if (legacyDefault !== undefined && !isPositiveTimeout(legacyDefault)) throw new Error("Legacy OpenCode default timeout must be positive integer milliseconds");
  if (path.basename(path.dirname(outputPath)) === ".opencode") {
    if (!opencodeOwnedServer(config)) {
      const directRoot = path.dirname(path.dirname(outputPath));
      for (const name of ["opencode.json", "opencode.jsonc"]) {
        const direct = path.join(directRoot, name);
        if (fs.existsSync(direct) && opencodeOwnedServer(readOptionalJsonObject(direct))) throw new Error("Selected hidden config has no owned server but a lower direct config does; refuse silent server shadowing");
      }
    }
    return;
  }
  const hiddenRoot = path.join(path.dirname(outputPath), ".opencode");
  const hiddenFiles = ["opencode.json", "opencode.jsonc"].map((name) => path.join(hiddenRoot, name)).filter((file) => fs.existsSync(file));
  if (hiddenFiles.length > 1) throw new Error("Higher-precedence .opencode config filenames conflict; resolve them before setup");
  for (const file of hiddenFiles) {
    if (opencodeOwnedServer(readOptionalJsonObject(file))) throw new Error("Higher-precedence .opencode config defines this MCP server; select that exact output file instead");
  }
}

function detectJsoncFormatting(source: string): FormattingOptions {
  const indentation = source.match(/^[\t ]+(?="[^"\r\n]+"\s*:)/m)?.[0] ?? "  ";
  return {
    insertSpaces: !indentation.includes("\t"),
    tabSize: indentation.includes("\t") ? 1 : Math.max(1, indentation.length),
    eol: source.includes("\r\n") ? "\r\n" : "\n",
  };
}

function readOptionalJsonObject(filePath: string): Record<string, unknown> {
  if (!fs.existsSync(filePath)) {
    return {};
  }
  return parseJsoncObject(fs.readFileSync(filePath, "utf8"), filePath);
}

function parseJsoncObject(source: string, label: string): Record<string, unknown> {
  const errors: ParseError[] = [];
  const parsed = parse(source, errors, { allowTrailingComma: true }) as unknown;
  if (errors.length > 0) throw new Error(`setup config must be valid JSONC: ${label}`);
  if (!isPlainObject(parsed)) throw new Error(`setup config must be a JSON object: ${label}`);

  const seen = new Set<string>();
  let duplicateOwnedKey = false;
  visit(source, {
    onObjectProperty(property, _offset, _length, _line, _character, pathSupplier) {
      const fullPath = [...pathSupplier(), property];
      const legacyDefaultPath = fullPath[0] === "experimental" && (fullPath.length === 1 || fullPath[1] === "mcp_timeout");
      if (!legacyDefaultPath && (fullPath[0] !== "mcp" || (fullPath.length > 1 && !["sgg-unity-mcp", "servers", "timeout"].includes(String(fullPath[1])))
        || (fullPath[1] === "servers" && fullPath.length > 2 && fullPath[2] !== "sgg-unity-mcp"))) return;
      const key = JSON.stringify(fullPath);
      if (seen.has(key)) duplicateOwnedKey = true;
      seen.add(key);
    },
  }, { allowTrailingComma: true });
  if (duplicateOwnedKey) throw new Error(`setup config has duplicate keys in the owned MCP section: ${label}`);
  return parsed;
}

function mergeObjects(base: Record<string, unknown>, patch: Record<string, unknown>): Record<string, unknown> {
  const next: Record<string, unknown> = { ...base };
  for (const [key, value] of Object.entries(patch)) {
    const current = next[key];
    next[key] = isPlainObject(current) && isPlainObject(value) ? mergeObjects(current, value) : value;
  }
  return next;
}

function setupChanges(existing: Record<string, unknown>, next: Record<string, unknown>, target: SetupTarget): string[] {
  const serverPath = target === "opencode" ? ["mcp", "servers", "sgg-unity-mcp"] : ["mcpServers", "sgg-unity-mcp"];
  const previousServer = readNested(existing, serverPath);
  const nextServer = readNested(next, serverPath);
  if (target === "assistant") {
    const changes: string[] = [];
    if (existing.enabled !== next.enabled) {
      changes.push(`${existing.enabled === undefined ? "add" : "update"}: enabled`);
    }
    if (existing.path !== next.path) {
      changes.push(`${existing.path === undefined ? "add" : "update"}: path`);
    }
    if (JSON.stringify(previousServer) !== JSON.stringify(nextServer)) {
      changes.push(`${previousServer === undefined ? "add" : "update"}: ${serverPath.join(".")}`);
    }
    return changes.length > 0 ? changes : ["noop: sgg-unity-mcp entry already matches"];
  }
  if (JSON.stringify(previousServer) === JSON.stringify(nextServer)) {
    return ["noop: sgg-unity-mcp entry already matches"];
  }
  return [
    ...(target === "opencode" && readNested(existing, ["mcp", "sgg-unity-mcp"]) !== undefined ? ["migrate: mcp.sgg-unity-mcp -> mcp.servers.sgg-unity-mcp"] : []),
    `${previousServer === undefined ? "add" : "update"}: ${serverPath.join(".")}`,
  ];
}

function writeSetupOutput(stdout: (text: string) => void, json: boolean, plan: SetupPlan): void {
  if (json) {
    stdout(`${JSON.stringify(plan, null, 2)}\n`);
    return;
  }

  stdout(`setup: ${plan.target}\n`);
  stdout(`path: ${plan.path}\n`);
  stdout(`mode: ${plan.mode}\n`);
  for (const change of plan.changes) {
    stdout(`change: ${change}\n`);
  }
  for (const note of plan.notes) {
    stdout(`note: ${note}\n`);
  }
  for (const item of plan.checklist) {
    stdout(`checklist: ${String(item.step)} - ${String(item.description)}\n`);
  }
  stdout("patch:\n");
  stdout(`${JSON.stringify(plan.patch, null, 2)}\n`);
}

function setupChecklist(target: SetupTarget, outputPath: string): Record<string, unknown>[] {
  if (target !== "opencode") {
    return [];
  }

  return [
    {
      step: "create_project_or_global_opencode_config",
      optional: false,
      description: "Create or update project opencode.jsonc, or the equivalent global OpenCode config, so the OpenCode session loads the sgg-unity-mcp server.",
      configured_path: outputPath,
      expected_tools: TOOL_NAMES,
    },
    {
      step: "restart_or_reload_opencode_session",
      optional: false,
      description: "Reload MCP configuration, check opencode mcp list or /mcps, and discover sgg_unity_projects in the Code Mode catalog (or direct tool list when codemode:false) before Unity commands.",
      failure_code_if_missing: "target_client_tools_missing_after_gateway_tools_list_ok",
    },
  ];
}

function defaultSetupOutputPath(target: SetupTarget): string {
  switch (target) {
    case "opencode":
      return "opencode.jsonc";
    case "cursor":
      return path.join(".cursor", "mcp.json");
    case "assistant":
      return path.join("UserSettings", "mcp.json");
  }
}

function writeJsonAtomic(filePath: string, data: Record<string, unknown>): void {
  writeTextAtomic(filePath, `${JSON.stringify(data, null, 2)}\n`);
}

function writeTextAtomic(filePath: string, text: string): void {
  fs.mkdirSync(path.dirname(filePath), { recursive: true });
  const tempPath = path.join(path.dirname(filePath), `.${path.basename(filePath)}.${process.pid}.tmp`);
  const mode = fs.existsSync(filePath) ? fs.statSync(filePath).mode & 0o777 : 0o600;
  fs.writeFileSync(tempPath, text, { encoding: "utf8", mode });
  fs.chmodSync(tempPath, mode);
  fs.renameSync(tempPath, filePath);
}

function readNested(value: Record<string, unknown>, pathSegments: string[]): unknown {
  let current: unknown = value;
  for (const segment of pathSegments) {
    if (!isPlainObject(current)) {
      return undefined;
    }
    current = current[segment];
  }
  return current;
}

function isPlainObject(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function isSetupTarget(value: string): value is SetupTarget {
  return value === "opencode" || value === "cursor" || value === "assistant";
}

function parseOpencodeConnectionMode(value: string): OpencodeConnectionMode {
  if (value === "remote-http" || value === "windows-stdio") {
    return value;
  }
  throw new Error("--connection-mode must be one of: remote-http, windows-stdio");
}

function windowsStdioShimCommand(url: string, tokenEnv: string): string[] {
  const escapedUrl = powerShellSingleQuoted(url);
  const escapedTokenEnv = powerShellSingleQuoted(tokenEnv);
  return [
    "powershell.exe",
    "-NoProfile",
    "-ExecutionPolicy",
    "Bypass",
    "-Command",
    `$root=Join-Path $env:LOCALAPPDATA 'SGG\\UnityMcpGateway'; $pointer=Join-Path $root 'gateway\\active-runtime.json'; $runtime=(Get-Content -Raw $pointer | ConvertFrom-Json).runtime_root; $node=Join-Path $root 'runtime\\node-v22.16.0-win-x64\\node.exe'; $cli=Join-Path $runtime 'gateway-cli.mjs'; & $node $cli stdio-shim --url ${escapedUrl} --token-env ${escapedTokenEnv} --legacy-text-result`,
  ];
}

function powerShellSingleQuoted(value: string): string {
  return `'${value.replaceAll("'", "''")}'`;
}

export function runDoctorCli(argv: string[], io: CliIo = {}): number {
  const stdout = io.stdout ?? ((text: string) => process.stdout.write(text));
  const stderr = io.stderr ?? ((text: string) => process.stderr.write(text));
  const env = io.env ?? process.env;
  const cwd = io.cwd ?? process.cwd();
  let options: { configPath?: string; policyPath?: string; mcpPath: string; strict: boolean; json: boolean };

  try {
    options = parseSupportOptions(argv, { requireProject: false });
  } catch (error) {
    stderr(`${error instanceof Error ? error.message : String(error)}\n`);
    return 1;
  }

  const configPath = options.configPath ?? env.SGG_UNITY_MCP_CONFIG ?? defaultConfigPath(cwd, env);
  const policyPath = options.policyPath ?? env.SGG_UNITY_MCP_POLICY;
  const policyResult = inspectCurrentPolicyFileResult(policyPath, { ...(io.now ? { now: io.now } : {}) });
  const diagnostics: Record<string, unknown> = {
    config: { path: configPath, ok: false },
    policy: policyResult.inspection,
    checks: [],
  };
  const lines: string[] = [];
  const warnings: string[] = [];
  const errors: string[] = [];
  if (policyResult.inspection.current_file_status !== "valid" && policyResult.inspection.current_file_status !== "not_configured") {
    errors.push(`policy ${policyResult.inspection.current_file_status}`);
  }

  try {
    const config = loadGatewayConfig(configPath, env);
    diagnostics.config = { path: configPath, ok: true };
    lines.push(`config: ok (${configPath})`);

    if (policyResult.inspection.current_file_status === "valid") {
      lines.push("policy: ok (current file valid)");
    } else if (policyResult.inspection.current_file_status === "not_configured") {
      lines.push("policy: not configured");
    } else {
      lines.push(`policy: current file ${policyResult.inspection.current_file_status} (${policyResult.inspection.last_error_code}/${policyResult.inspection.last_error_kind})`);
    }

    const tokenPresent = config.mcp.token_env ? Boolean(env[config.mcp.token_env]) : false;
    const mcp = {
      host: config.mcp.host,
      port: config.mcp.port,
      path: options.mcpPath,
      loopback: isLoopbackHost(config.mcp.host),
      allow_non_loopback: config.mcp.allow_non_loopback,
    };
    diagnostics.token = { configured: Boolean(config.mcp.token_env), present: tokenPresent };
    diagnostics.mcp = mcp;
    const endpointRecommendation = wslEndpointRecommendation(config.mcp.host, config.mcp.port, options.mcpPath, env, {
      allowNonLoopback: config.mcp.allow_non_loopback,
      tokenEnv: config.mcp.token_env,
      tokenPresent,
      allowedOrigins: config.mcp.allowed_origins,
    });
    diagnostics.target_client_exposure = targetClientExposureDiagnostics(
      `http://${config.mcp.host}:${config.mcp.port}${options.mcpPath}`,
      endpointRecommendation?.code === "windows_loopback_not_wsl_reachable",
    );
    lines.push(`token: ${tokenPresent ? "present" : "missing"}`);
    lines.push(`mcp: ${config.mcp.host}:${config.mcp.port}${options.mcpPath} (${mcp.loopback ? "loopback" : "non-loopback"})`);
    lines.push("target_client_exposure: not verified (if Gateway initialize/tools-list returns sgg_unity_* but OpenCode API-visible tools do not, classify as target-client config/API exposure)");
    if (endpointRecommendation) {
      diagnostics.endpoint_recommendation = endpointRecommendation;
      if (endpointRecommendation.code === "windows_loopback_not_wsl_reachable") {
        diagnostics.wsl_loopback_reachability = endpointRecommendation;
      }
      warnings.push(String(endpointRecommendation.message));
    }
    if (!mcp.loopback && !config.mcp.allow_non_loopback) {
      warnings.push("MCP host is non-loopback while allow_non_loopback is false");
    } else if (!mcp.loopback) {
      warnings.push("MCP host is non-loopback; expose only on trusted local networks");
    }

    diagnostics.instances_dir = inspectDirectory(config.registry.instances_dir);
    const dir = diagnostics.instances_dir as { path: string; exists: boolean; readable: boolean; error?: string };
    lines.push(`instances_dir: ${dir.exists ? "exists" : "missing"}, ${dir.readable ? "readable" : "not readable"} (${dir.path})`);
    if (!dir.exists || !dir.readable) {
      warnings.push("instances_dir is missing or not readable; project discovery will be empty");
    }

    try {
      const registry = new ProjectRegistry({
        instancesDir: config.registry.instances_dir,
        staleAfterMs: config.registry.stale_after_ms,
        ...(config.registry.cleanup_after_ms ? { cleanupAfterMs: config.registry.cleanup_after_ms } : {}),
        cacheMaxEntries: config.registry.cache_max_entries,
        cacheTtlMs: config.registry.cache_ttl_ms,
        invalidDiagnosticsMax: config.registry.invalid_diagnostics_max,
        scanMaxFiles: config.registry.scan_max_files,
        processGenerationInspector: createUnityProcessGenerationInspector(io.now ? { now: io.now } : {}),
        ...(io.now ? { now: io.now } : {}),
      });
      const stats = registry.getStats();
      diagnostics.registry = stats;
      lines.push(`registry: ${stats.projectRecordsCount} heartbeat records, ${stats.liveProjectsCount} live projects, ${stats.staleProjectsCount} stale projects`);
      if (stats.invalidProjectRecordsCount > 0) {
        warnings.push(`${stats.invalidProjectRecordsCount} invalid heartbeat record(s) ignored`);
      }
      if (stats.protocolVersionMismatchProjectRecordsCount > 0) {
        warnings.push(`${stats.protocolVersionMismatchProjectRecordsCount} heartbeat protocol version mismatch record(s) ignored`);
      }
    } catch (error) {
      diagnostics.registry = { ok: false, error: safeErrorMessage(error) };
      warnings.push("registry could not be read");
      lines.push(`registry: warning (${safeErrorMessage(error)})`);
    }

    const unityCrashReporter = getUnityCrashReporterStatus(io);
    diagnostics.unity_crash_reporter = unityCrashReporter;
    lines.push(`unity_crash_reporter: ${unityCrashReporter.active_reporters_count} active reporter(s), crashed=${unityCrashReporter.unity_crashed ? "yes" : "no"}`);
    if (unityCrashReporter.blocking_agent_work) {
      warnings.push("Unity Bug Reporter window is active; Unity likely crashed and agents may be blocked");
    } else if (unityCrashReporter.unity_crashed) {
      warnings.push("Recent Unity crash artifacts were found");
    }

    const featureHints = policyResult.policy ? featurePolicyHints(config.features ?? {}, policyResult.policy) : [];
    diagnostics.feature_policy_hints = featureHints;
    for (const hint of featureHints) {
      warnings.push(hint);
    }

    const hardErrors = errors.length;
    diagnostics.summary = { errors: hardErrors, warnings: warnings.length, ok: hardErrors === 0 };
    writeDoctorOutput(stdout, options.json, diagnostics, lines, warnings, errors);
    return hardErrors > 0 || (options.strict && warnings.length > 0) ? 1 : 0;
  } catch (error) {
    const outputErrors = [...errors, "config parse failed"];
    diagnostics.config = { path: configPath, ok: false, error: safeErrorMessage(error) };
    diagnostics.summary = { errors: outputErrors.length, warnings: warnings.length, ok: false };
    lines.push(`config: error (${safeErrorMessage(error)})`);
    writeDoctorOutput(stdout, options.json, diagnostics, lines, warnings, outputErrors);
    return 1;
  }
}

export async function runWaitReadyCli(argv: string[], io: CliIo = {}): Promise<number> {
  const stdout = io.stdout ?? ((text: string) => process.stdout.write(text));
  const stderr = io.stderr ?? ((text: string) => process.stderr.write(text));
  const env = io.env ?? process.env;
  const cwd = io.cwd ?? process.cwd();
  const sleep = io.sleep ?? ((ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms)));
  let options: { configPath?: string; project?: string; timeoutMs: number; intervalMs: number; json: boolean };

  try {
    options = parseSupportOptions(argv, { requireProject: true });
  } catch (error) {
    stderr(`${error instanceof Error ? error.message : String(error)}\n`);
    return 1;
  }

  const configPath = options.configPath ?? env.SGG_UNITY_MCP_CONFIG ?? defaultConfigPath(cwd, env);
  let registry: ProjectRegistry;
  try {
    const config = loadGatewayConfig(configPath, env);
    registry = new ProjectRegistry({
      instancesDir: config.registry.instances_dir,
      staleAfterMs: config.registry.stale_after_ms,
      ...(config.registry.cleanup_after_ms ? { cleanupAfterMs: config.registry.cleanup_after_ms } : {}),
      cacheMaxEntries: config.registry.cache_max_entries,
      cacheTtlMs: config.registry.cache_ttl_ms,
      invalidDiagnosticsMax: config.registry.invalid_diagnostics_max,
      scanMaxFiles: config.registry.scan_max_files,
      processGenerationInspector: createUnityProcessGenerationInspector(io.now ? { now: io.now } : {}),
      ...(io.now ? { now: io.now } : {}),
    });
  } catch (error) {
    writeWaitReadyOutput(stdout, options.json, { status: "error", cause: "config_load_failed", error: safeErrorMessage(error) });
    return 1;
  }

  const deadline = Date.now() + options.timeoutMs;
  let lastCause = "project_not_found";
  let attempts = 0;
  while (Date.now() <= deadline) {
    attempts += 1;
    const resolved = registry.resolveProject(options.project ?? "");
    if (resolved.status === "ok") {
      const catalogPublished = resolved.record.catalog_generation === undefined || resolved.record.catalog_generation > 0;
      const catalogRoutable = resolved.record.catalog_status === undefined || resolved.record.catalog_status === "ready";
      if (resolved.record.status === "ready" && resolved.record.bridge_port > 0 && catalogPublished && catalogRoutable && resolved.record.commands_hash !== "bootstrap-pending") {
        writeWaitReadyOutput(stdout, options.json, { status: "ready", project: resolved.record.project, instance_id: resolved.record.instance_id, heartbeat_status: resolved.record.status, attempts });
        return 0;
      }
      lastCause = resolved.record.status === "ready"
        ? "project heartbeat is ready but listener/catalog publication is unavailable"
        : `project heartbeat is live but status is ${resolved.record.status}`;
    } else {
      lastCause = resolved.code;
    }

    await sleep(Math.min(options.intervalMs, Math.max(0, deadline - Date.now())));
  }

  writeWaitReadyOutput(stdout, options.json, { status: "timeout", project: options.project, cause: lastCause, timeout_ms: options.timeoutMs });
  return 1;
}

export function runReportProblemCli(argv: string[], io: CliIo = {}): number {
  const stdout = io.stdout ?? ((text: string) => process.stdout.write(text));
  const stderr = io.stderr ?? ((text: string) => process.stderr.write(text));
  const env = io.env ?? process.env;
  const cwd = io.cwd ?? process.cwd();
  let options: { configPath?: string; policyPath?: string; project?: string; summary?: string; includeDoctor: boolean; json: boolean; help: boolean };

  try {
    options = parseReportProblemOptions(argv);
  } catch (error) {
    stderr(`${safeErrorMessage(error)}\n`);
    return 1;
  }

  if (options.help) {
    stderr(reportProblemHelpText());
    return 0;
  }

  const now = io.now?.() ?? new Date();
  const gatewayRuntimeVersion = readPackageVersion();
  const runId = randomUUID();
  const configPath = options.configPath ?? env.SGG_UNITY_MCP_CONFIG ?? defaultConfigPath(cwd, env);
  const policyPath = options.policyPath ?? env.SGG_UNITY_MCP_POLICY;
  const configStatus: Record<string, unknown> = { path: configPath, ok: false };
  const policyStatus = inspectCurrentPolicyFile(policyPath, { ...(io.now ? { now: io.now } : {}) });
  let reportDir = path.join(os.tmpdir(), "sgg-unity-mcp", "problem-reports");
  let registry: ProjectRegistry | undefined;
  let mcp: Record<string, unknown> | undefined;
  let wslLoopback: Record<string, unknown> | undefined;
  let endpointRecommendation: Record<string, unknown> | undefined;
  let targetClientExposure: Record<string, unknown> | undefined;
  let authentication: Record<string, unknown> | undefined;
  let instancesDir: ReturnType<typeof inspectDirectory> | undefined;
  let features: Record<string, boolean> = {};
  const warnings: string[] = [];

  try {
    const config = loadGatewayConfig(configPath, env);
    configStatus.ok = true;
    if (config.logging.audit_dir) {
      reportDir = path.join(config.logging.audit_dir, "problem-reports");
    }
    registry = new ProjectRegistry({
      instancesDir: config.registry.instances_dir,
      staleAfterMs: config.registry.stale_after_ms,
      ...(config.registry.cleanup_after_ms ? { cleanupAfterMs: config.registry.cleanup_after_ms } : {}),
      cacheMaxEntries: config.registry.cache_max_entries,
      cacheTtlMs: config.registry.cache_ttl_ms,
      invalidDiagnosticsMax: config.registry.invalid_diagnostics_max,
      scanMaxFiles: config.registry.scan_max_files,
      processGenerationInspector: createUnityProcessGenerationInspector(io.now ? { now: io.now } : {}),
      ...(io.now ? { now: io.now } : {}),
    });
    mcp = {
      host: config.mcp.host,
      port: config.mcp.port,
      path: "/mcp",
      loopback: isLoopbackHost(config.mcp.host),
      allow_non_loopback: config.mcp.allow_non_loopback,
    };
    endpointRecommendation = wslEndpointRecommendation(config.mcp.host, config.mcp.port, "/mcp", env, {
      allowNonLoopback: config.mcp.allow_non_loopback,
      tokenEnv: config.mcp.token_env,
      tokenPresent: config.mcp.token_env ? Boolean(env[config.mcp.token_env]) : false,
      allowedOrigins: config.mcp.allowed_origins,
    });
    targetClientExposure = targetClientExposureDiagnostics(
      `http://${config.mcp.host}:${config.mcp.port}/mcp`,
      endpointRecommendation?.code === "windows_loopback_not_wsl_reachable",
    );
    wslLoopback = endpointRecommendation?.code === "windows_loopback_not_wsl_reachable" ? endpointRecommendation : undefined;
    if (endpointRecommendation) {
      warnings.push(String(endpointRecommendation.message));
    }
    authentication = { mcp: { configured: Boolean(config.mcp.token_env), present: config.mcp.token_env ? Boolean(env[config.mcp.token_env]) : false } };
    instancesDir = inspectDirectory(config.registry.instances_dir);
    features = config.features ?? {};
  } catch (error) {
    configStatus.error = safeErrorMessage(error);
    warnings.push("config_load_failed; wrote report to temp fallback");
  }

  if (policyPath && policyStatus.current_file_status !== "valid") {
    warnings.push(`policy_${policyStatus.current_file_status}`);
  }
  if (isLikelyTargetClientExposureFailure(options.summary)) {
    warnings.push(targetClientExposureWarning());
  }

  const reportId = safeSupportReportId(now, options.project, options.summary ?? "problem");
  const reportPath = path.join(reportDir, `${reportId}.json`);
  const registrySnapshot = registry?.captureSnapshot({ cleanup: false });
  const registryStats = registrySnapshot?.getStats() ?? null;
  const resolution = options.project && registrySnapshot ? registrySnapshot.resolveProject(options.project) : undefined;
  const projects = supportProblemProjectRecords(registrySnapshot, resolution);
  const unityCrashReporter = summarizeProblemCrashReporter(getUnityCrashReporterStatus(io));
  const doctor = options.includeDoctor ? {
    config: configStatus,
    policy: policyStatus,
    authentication: authentication ?? { mcp: { configured: false, present: false } },
    mcp: mcp ?? null,
    wsl_loopback_reachability: wslLoopback ?? null,
    endpoint_recommendation: endpointRecommendation ?? null,
    target_client_exposure: targetClientExposure ?? null,
    instances_dir: instancesDir ?? null,
    registry: registryStats,
    features,
    unity_crash_reporter: unityCrashReporter,
  } : undefined;
  const serialized = serializeProblemReport({
    schema_version: PROBLEM_REPORT_SCHEMA_VERSION,
    source: "cli.report-problem",
    gateway: {
      gateway_runtime_version: gatewayRuntimeVersion,
      run_id: runId,
    },
    report_id: reportId,
    created_utc: now.toISOString(),
    project: options.project,
    summary: options.summary,
    config: configStatus,
    policy: policyStatus,
    registry: {
      snapshot: registrySnapshot ? {
        captured_at: registrySnapshot.captured_at,
        capture_started_at: registrySnapshot.capture_started_at,
        capture_ended_at: registrySnapshot.capture_ended_at,
      } : null,
      stats: registryStats,
      projects: projects.records,
      projects_summary: projects.summary,
      project_resolution: resolution ? sanitizeSupportResolution(resolution) : null,
    },
    wsl_loopback_reachability: wslLoopback ?? null,
    endpoint_recommendation: endpointRecommendation ?? null,
    target_client_exposure: targetClientExposure ?? null,
    unity_crash_reporter: unityCrashReporter,
    doctor,
    warnings,
    redaction_applied: true,
    path_redaction_applied: true,
  });

  fs.mkdirSync(reportDir, { recursive: true });
  fs.writeFileSync(reportPath, serialized.text, "utf8");
  writeReportProblemOutput(stdout, options.json, {
    report_id: reportId,
    report_path: reportPath,
    report_bytes: serialized.bytes,
    report_truncated: serialized.truncated,
    redaction_applied: true,
    path_redaction_applied: true,
    warnings,
  });
  return 0;
}

function targetClientExposureDiagnostics(gatewayUrl: string, windowsLocalOnly = false): Record<string, unknown> {
  return {
    code: "target_client_tool_exposure_checklist",
    status: "not_verified",
    gateway_url: gatewayUrl,
    gateway_url_scope: windowsLocalOnly ? "windows_local_only_not_wsl_usable" : "configured_gateway_endpoint",
    gateway_probe: {
      initialize_method: "initialize",
      tools_list_method: "tools/list",
      expected_tools: TOOL_NAMES,
    },
    target_client_failure: {
      code: "target_client_tools_missing_after_gateway_tools_list_ok",
      classification: "target_client_config_or_api_exposure",
      message: "If manual MCP initialize and tools/list against the Gateway return sgg_unity_* tools but the OpenCode API-visible tool list does not, the Gateway and Unity bridge are not proven unavailable; reload/fix the OpenCode MCP client config or track upstream client exposure behavior.",
    },
    opencode_setup: {
      project_config: "opencode.jsonc",
      dry_run_command: "sgg-unity-mcp-gateway setup opencode --connection-mode windows-stdio --dry-run",
      verify_client_tool: "sgg_unity_projects",
      fallback_connection_mode: "windows-stdio",
    },
    next_valid_actions: [
      "run_sgg_unity_mcp_gateway_setup_opencode_dry_run",
      "create_or_update_project_or_global_opencode_jsonc",
      "restart_or_reload_opencode_session",
      "verify_client_visible_sgg_unity_projects",
      "compare_manual_gateway_tools_list_with_client_api_tool_list",
    ],
  };
}

function isLikelyTargetClientExposureFailure(summary: string | undefined): boolean {
  if (!summary) {
    return false;
  }
  const normalized = summary.toLowerCase();
  return (normalized.includes("opencode") || normalized.includes("client") || normalized.includes("api"))
    && (normalized.includes("sgg_unity") || normalized.includes("tool") || normalized.includes("mcp"))
    && (normalized.includes("missing") || normalized.includes("not expose") || normalized.includes("no ") || normalized.includes("empty"));
}

function targetClientExposureWarning(): string {
  return "target_client_tools_missing_after_gateway_tools_list_ok: if manual Gateway initialize/tools-list returns sgg_unity_* tools but OpenCode/API-visible tools are missing, diagnose target-client config/API exposure rather than Gateway or Unity unavailability";
}

export function parseReportProblemOptions(argv: string[]): { configPath?: string; policyPath?: string; project?: string; summary?: string; includeDoctor: boolean; json: boolean; help: boolean } {
  const parsed: { configPath?: string; policyPath?: string; project?: string; summary?: string; includeDoctor: boolean; json: boolean; help: boolean } = {
    includeDoctor: false,
    json: false,
    help: false,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--help" || arg === "-h") {
      parsed.help = true;
    } else if (arg === "--json") {
      parsed.json = true;
    } else if (arg === "--include-doctor") {
      parsed.includeDoctor = true;
    } else if (arg === "--config") {
      parsed.configPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--policy") {
      parsed.policyPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--project") {
      parsed.project = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--summary") {
      parsed.summary = requiredValue(argv, (index += 1), arg);
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  if (!parsed.help && !parsed.project) {
    throw new Error("--project is required");
  }
  if (!parsed.help && !parsed.summary) {
    throw new Error("--summary is required");
  }
  if (parsed.summary && parsed.summary.length > 500) {
    throw new Error("--summary must be at most 500 characters");
  }

  return parsed;
}

function sanitizeSupportResolution(resolution: ProjectRegistrySnapshotResolution): Record<string, unknown> {
  if (resolution.status === "ok") {
    return { status: "ok", record: structuredClone(resolution.record) };
  }
  const matches = resolution.matches.slice(0, PROBLEM_REPORT_MAX_REGISTRY_RECORDS);
  return {
    status: "error",
    code: resolution.code,
    ...(resolution.code === "unsafe_binding_refused" ? { registry_operational_state: resolution.registryOperationalState } : {}),
    matches,
    matches_summary: {
      total_records: resolution.matches.length,
      retained_records: matches.length,
      max_records: PROBLEM_REPORT_MAX_REGISTRY_RECORDS,
      truncated: resolution.matches.length > matches.length,
    },
  };
}

function supportProblemProjectRecords(snapshot: ProjectRegistrySnapshot | undefined, resolution: ProjectRegistrySnapshotResolution | undefined): { records: PublicProjectRecord[]; summary: Record<string, unknown> } {
  const all = snapshot?.listProjectRecords() ?? [];
  const candidates = resolution?.status === "ok"
    ? [resolution.record]
    : resolution?.status === "error" && resolution.matches.length > 0
      ? resolution.matches
      : [];
  const records = [...new Map(candidates.map((record) => [record.instance_id, record])).values()].slice(0, PROBLEM_REPORT_MAX_REGISTRY_RECORDS);
  return {
    records,
    summary: {
      total_records: all.length,
      candidate_records: candidates.length,
      retained_records: records.length,
      max_records: PROBLEM_REPORT_MAX_REGISTRY_RECORDS,
      truncated: candidates.length > records.length,
      selection: resolution?.status === "ok" ? "resolved_record" : resolution?.status === "error" && resolution.matches.length > 0 ? "resolution_matches" : "no_selector_match",
    },
  };
}

function getUnityCrashReporterStatus(io: CliIo): UnityCrashReporterStatus {
  return io.unityCrashReporter?.status() ?? inspectUnityCrashReporter({
    ...(io.env ? { env: io.env } : {}),
    ...(io.now ? { now: io.now } : {}),
  });
}

function safeSupportReportId(now: Date, project: string | undefined, summary: string): string {
  const timestamp = now.toISOString().replace(/[^0-9A-Za-z]+/g, "").slice(0, 15);
  const hash = createHash("sha256").update(`${now.toISOString()}\n${project ?? "project"}\n${summary}\n${process.pid}`).digest("hex").slice(0, 12);
  return `problem_${timestamp}_${hash}`;
}

function writeReportProblemOutput(stdout: (text: string) => void, json: boolean, result: Record<string, unknown>): void {
  if (json) {
    stdout(`${JSON.stringify(result, null, 2)}\n`);
    return;
  }

  stdout(`report: ${String(result.report_path)}\n`);
  if (Array.isArray(result.warnings)) {
    for (const warning of result.warnings) {
      stdout(`warn: ${String(warning)}\n`);
    }
  }
}

function requiredValue(argv: string[], index: number, flag: string): string {
  const value = argv[index];
  if (!value || value.startsWith("--")) {
    throw new Error(`${flag} requires a value`);
  }
  return value;
}

function parseSupportOptions(argv: string[], options: { requireProject: boolean }): { configPath?: string; policyPath?: string; project?: string; mcpPath: string; strict: boolean; json: boolean; timeoutMs: number; intervalMs: number } {
  const parsed: { configPath?: string; policyPath?: string; project?: string; mcpPath: string; strict: boolean; json: boolean; timeoutMs: number; intervalMs: number } = {
    mcpPath: "/mcp",
    strict: false,
    json: false,
    timeoutMs: 30_000,
    intervalMs: 500,
  };

  for (let index = 0; index < argv.length; index += 1) {
    const arg = argv[index];
    if (arg === "--json") {
      parsed.json = true;
    } else if (arg === "--strict") {
      parsed.strict = true;
    } else if (arg === "--config") {
      parsed.configPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--policy") {
      parsed.policyPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--project") {
      parsed.project = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--path") {
      parsed.mcpPath = requiredValue(argv, (index += 1), arg);
    } else if (arg === "--timeout-ms") {
      parsed.timeoutMs = positiveInteger(requiredValue(argv, (index += 1), arg), arg);
    } else if (arg === "--interval-ms") {
      parsed.intervalMs = positiveInteger(requiredValue(argv, (index += 1), arg), arg);
    } else {
      throw new Error(`Unknown argument: ${arg}`);
    }
  }

  if (options.requireProject && !parsed.project) {
    throw new Error("--project is required");
  }

  return parsed;
}

function positiveInteger(value: string, flag: string): number {
  const parsed = Number(value);
  if (!Number.isInteger(parsed) || parsed <= 0) {
    throw new Error(`${flag} must be a positive integer`);
  }
  return parsed;
}

function safeErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function isLoopbackHost(host: string): boolean {
  const normalized = host.toLowerCase();
  return normalized === "localhost" || normalized === "::1" || normalized.startsWith("127.");
}

function isWslEnvironment(env: NodeJS.ProcessEnv): boolean {
  return Boolean(env.WSL_DISTRO_NAME || env.WSL_INTEROP);
}

function wslEndpointRecommendation(
  host: string,
  port: number,
  mcpPath: string,
  env: NodeJS.ProcessEnv,
  security: {
    allowNonLoopback: boolean;
    tokenEnv: string | undefined;
    tokenPresent: boolean;
    allowedOrigins: string[];
    requestedUrl?: string | undefined;
  },
): Record<string, unknown> | undefined {
  if (!isWslEnvironment(env)) {
    return undefined;
  }

  const bindLoopback = isLoopbackHost(host);
  const advertisedUrl = wslRemoteHttpUrl(host, port, mcpPath, security.requestedUrl, env.SGG_UNITY_MCP_WINDOWS_HOST);
  const missingRemotePreconditions = [
    ...(!bindLoopback ? [] : ["non_loopback_bind"]),
    ...(security.allowNonLoopback ? [] : ["allow_non_loopback"]),
    ...(security.tokenEnv ? [] : ["token_env"]),
    ...(security.tokenPresent ? [] : ["token_present"]),
    ...(security.allowedOrigins.length > 0 ? [] : ["allowed_origins"]),
    ...(advertisedUrl ? [] : ["concrete_non_loopback_url"]),
  ];
  const protectedRemoteHttpEligible = missingRemotePreconditions.length === 0;
  if (!bindLoopback && protectedRemoteHttpEligible) {
    return {
      code: "protected_remote_http_configured",
      running_in_wsl: true,
      gateway_bind_host: host,
      gateway_bind_loopback: false,
      recommended_connection_mode: "remote_http",
      protected_remote_http_eligible: true,
      protected_remote_http_url: advertisedUrl,
      remote_http_security: {
        allow_non_loopback: true,
        token_configured: true,
        token_present: true,
        allowed_origins_configured: true,
      },
      message: "A concrete protected non-loopback Gateway endpoint is configured for WSL Remote HTTP.",
      next_valid_actions: ["use_protected_remote_http_url", "verify_gateway_from_wsl", "keep_token_and_allowed_origins_enabled"],
    };
  }

  return {
    code: bindLoopback ? "windows_loopback_not_wsl_reachable" : "protected_remote_http_not_ready",
    running_in_wsl: true,
    gateway_bind_host: host,
    gateway_bind_loopback: bindLoopback,
    ...(bindLoopback ? { windows_loopback_url: `http://${host}:${port}${mcpPath}` } : {}),
    direct_wsl_loopback_may_target_wsl_namespace: bindLoopback,
    direct_wsl_http_reliable: protectedRemoteHttpEligible,
    recommended_connection_mode: "windows_stdio_shim",
    protected_remote_http_eligible: false,
    protected_remote_http_candidate: advertisedUrl ?? null,
    missing_remote_http_preconditions: missingRemotePreconditions,
    safe_alternative: "remote_http_requires_operator_approved_non_loopback_bind_token_and_allowed_origins",
    message: bindLoopback
      ? "Windows loopback is not a WSL-usable Gateway endpoint; use the generated Windows stdio shim or an operator-approved protected Remote HTTP configuration."
      : "The non-loopback Gateway endpoint is not ready for protected WSL Remote HTTP; use the Windows stdio shim until every security precondition is satisfied.",
    next_valid_actions: [
      "run_setup_opencode_windows_stdio",
      "use_powershell_stdio_shim",
      "verify_gateway_from_windows_powershell",
      "use_remote_http_only_with_token_allowed_origins_and_operator_approval",
    ],
  };
}

function wslRemoteHttpUrl(host: string, port: number, mcpPath: string, requestedUrl: string | undefined, windowsHost: string | undefined): string | undefined {
  const advertisedHostValue = isConcreteNonLoopbackHost(host) ? host : windowsHost;
  const advertisedHost = advertisedHostValue?.trim().replace(/^\[|\]$/g, "");
  if (!advertisedHost || !isConcreteNonLoopbackHost(advertisedHost)) {
    return undefined;
  }
  const expectedUrl = gatewayHttpUrl(advertisedHost, port, mcpPath);
  if (requestedUrl) {
    try {
      const parsed = new URL(requestedUrl);
      if (parsed.protocol === "http:"
        && !parsed.username
        && !parsed.password
        && !parsed.search
        && !parsed.hash
        && parsed.hostname.replace(/^\[|\]$/g, "").toLowerCase() === advertisedHost.replace(/^\[|\]$/g, "").toLowerCase()
        && Number(parsed.port || 80) === port
        && parsed.pathname === mcpPath) {
        return expectedUrl;
      }
    } catch {
      return undefined;
    }
    return undefined;
  }

  return expectedUrl;
}

function gatewayHttpUrl(host: string, port: number, mcpPath: string): string {
  const urlHost = host.includes(":") ? `[${host}]` : host;
  return `http://${urlHost}:${port}${mcpPath}`;
}

function validatedOpencodeSetupUrl(options: SetupCliOptions, config: ReturnType<typeof loadGatewayConfig>, env: NodeJS.ProcessEnv): string {
  const configuredHost = config.mcp.host.trim().replace(/^\[|\]$/g, "");
  const shimHost = isWildcardHost(configuredHost) ? "127.0.0.1" : configuredHost;
  const fallbackHost = options.connectionMode === "windows-stdio" ? shimHost : configuredHost;
  const candidate = options.url ?? gatewayHttpUrl(fallbackHost, config.mcp.port, options.mcpPath);
  let parsed: URL;
  try {
    parsed = new URL(candidate);
  } catch {
    throw new Error("invalid_opencode_endpoint: URL must be a bounded HTTP Gateway endpoint without credentials, query, or fragment");
  }
  const candidateHost = parsed.hostname.replace(/^\[|\]$/g, "").toLowerCase();
  if (parsed.protocol !== "http:"
    || parsed.username
    || parsed.password
    || parsed.search
    || parsed.hash
    || Number(parsed.port || 80) !== config.mcp.port
    || parsed.pathname !== options.mcpPath) {
    throw new Error("invalid_opencode_endpoint: URL must match the configured Gateway port and MCP path without credentials, query, or fragment");
  }

  const expectedHost = (options.connectionMode === "windows-stdio" ? shimHost : configuredHost).toLowerCase();
  if (options.connectionMode === "windows-stdio") {
    if (candidateHost !== expectedHost) {
      throw new Error("invalid_opencode_endpoint: windows-stdio URL must target the configured Windows Gateway endpoint");
    }
    return gatewayHttpUrl(candidateHost, config.mcp.port, options.mcpPath);
  }

  if (isWildcardHost(configuredHost)) {
    const expectedWslHost = env.SGG_UNITY_MCP_WINDOWS_HOST?.trim().replace(/^\[|\]$/g, "").toLowerCase();
    if (!isPrivateNetworkHost(candidateHost) || (expectedWslHost && candidateHost !== expectedWslHost)) {
      throw new Error("invalid_opencode_endpoint: wildcard Remote HTTP requires the configured private Windows host candidate");
    }
  } else if (candidateHost !== expectedHost) {
    throw new Error("invalid_opencode_endpoint: Remote HTTP URL host must match the configured Gateway bind host");
  }

  const tokenEnv = config.mcp.token_env;
  if (!isLoopbackHost(candidateHost) && (!config.mcp.allow_non_loopback || !tokenEnv || !env[tokenEnv] || config.mcp.allowed_origins.length === 0)) {
    throw new Error("remote_http_not_ready: non-loopback setup requires allow_non_loopback, a present token, and allowed_origins");
  }
  return gatewayHttpUrl(candidateHost, config.mcp.port, options.mcpPath);
}

function isWildcardHost(host: string): boolean {
  const normalized = host.trim().toLowerCase().replace(/^\[|\]$/g, "");
  return normalized === "0.0.0.0" || normalized === "::";
}

function isPrivateNetworkHost(host: string): boolean {
  const normalized = host.trim().toLowerCase().replace(/^\[|\]$/g, "");
  const family = isIP(normalized);
  if (family === 4) {
    const [first = -1, second = -1] = normalized.split(".").map(Number);
    return first === 10
      || (first === 172 && second >= 16 && second <= 31)
      || (first === 192 && second === 168)
      || (first === 169 && second === 254);
  }
  return family === 6 && (normalized.startsWith("fc") || normalized.startsWith("fd") || normalized.startsWith("fe8") || normalized.startsWith("fe9") || normalized.startsWith("fea") || normalized.startsWith("feb"));
}

function isConcreteNonLoopbackHost(host: string): boolean {
  const normalized = host.trim().toLowerCase().replace(/^\[|\]$/g, "");
  if (!normalized || isLoopbackHost(normalized) || isWildcardHost(normalized)) {
    return false;
  }
  return /^[a-z0-9._:-]+$/.test(normalized);
}

function inspectDirectory(dir: string): { path: string; exists: boolean; readable: boolean; error?: string } {
  try {
    if (!fs.existsSync(dir)) {
      return { path: dir, exists: false, readable: false };
    }
    fs.accessSync(dir, fs.constants.R_OK);
    return { path: dir, exists: true, readable: true };
  } catch (error) {
    return { path: dir, exists: true, readable: false, error: safeErrorMessage(error) };
  }
}

function featurePolicyHints(features: Record<string, boolean>, policy: { projects: Record<string, { allow: string[]; ask: string[]; deny: string[] }> }): string[] {
  const commandFeaturePrefixes: Record<string, string> = {
    "diagnostics.": "sgg_mcp_diagnostics",
    "plan.": "sgg_mcp_plans",
    "skill.": "sgg_mcp_skills",
    "checkpoint.": "sgg_mcp_checkpoints",
    "artifact.": "sgg_mcp_artifacts",
    "policy.": "sgg_mcp_policy",
    "build.": "build_test_automation",
    "test.": "build_test_automation",
    "addressables.": "addressables_readonly",
    "scriptable_object.": "gameplay_data_readonly",
    "gameplay.data.": "gameplay_data_readonly",
    "animation.": "gameplay_data_readonly",
    "timeline.": "gameplay_data_readonly",
    "package.search": "package_registry_network",
    "package.install.git": "package_registry_network",
    "unity.api.": "unity_api_reflection",
    "unity.docs.": "unity_docs_local",
    "profiler.recording.": "profiler_recording",
    "profiler.capture.": "profiler_recording",
    "profiler.status": "profiler_readonly",
    "profiler.modules": "profiler_readonly",
    "profiler.frame.": "profiler_readonly",
    "profiler.marker.": "profiler_readonly",
    "profiler.report.": "profiler_readonly",
    "profiler.memory.snapshot.compare": "profiler_readonly",
    "desktop.region.screenshot": "desktop_screenshots",
    "editor.window.screenshot": "editor_screenshots",
    "scene.view.": "editor_screenshots",
    "game.view.screenshot": "editor_screenshots",
    "game.view.focus_capture": "game_view_focus_capture",
    "game.view.focus": "editor_screenshots",
    "camera.": "editor_screenshots",
    "ui.screen.": "ui_readonly",
    "ui.interaction.": "ui_readonly",
    "ui.text.": "ui_text_readonly",
    "ugui.canvas.modify": "ugui_safe_writes",
    "ugui.screen.diff": "ugui_inspection",
    "ugui.object.": "ugui_safe_writes",
    "ugui.component.": "ugui_safe_writes",
    "ugui.text.": "ugui_safe_writes",
    "ugui.image.": "ugui_safe_writes",
    "ugui.canvas_group.": "ugui_safe_writes",
    "ugui.layout_group.": "ugui_safe_writes",
    "ugui.button.": "ugui_safe_writes",
    "ugui.selectable.": "ugui_safe_writes",
    "ugui.screen.": "ugui_safe_writes",
    "scene.authoring.": "scene_authoring_composite_safe_writes",
    "prefab.authoring.": "prefab_authoring_composite_safe_writes",
    "prefab.mono_behaviour.": "mono_behaviour_safe_writes",
    "prefab.serialized.": "serialized_safe_writes",
    "ugui.canvas.": "ugui_inspection",
    "ugui.resource.": "ugui_inspection",
    "ugui.events.": "ugui_inspection",
    "ugui.layout.": "ugui_inspection",
    "ugui.navigation.": "ugui_inspection",
    "ugui.raycast.": "ugui_inspection",
    "ugui.references.": "ugui_inspection",
    "ugui.bounds.": "ugui_visual_inspection",
    "ugui.interaction.": "ugui_interaction_tests",
    "mono_behaviour.": "mono_behaviour_safe_writes",
    "serialized.": "serialized_safe_writes",
    "unityevent.": "unityevent_safe_writes",
    "uitk.plan": "uitk_migration",
    "uitk.generate": "uitk_migration",
    "uitk.preview": "uitk_migration_preview",
    "uitk.validate": "uitk_validation",
    "uitk.bindings.validate": "uitk_validation",
    "uitk.document.create": "uitk_safe_writes",
    "uitk.binder.generate": "uitk_safe_writes",
    "uitk.template.generate": "uitk_safe_writes",
    "uitk.interaction.": "uitk_interaction_tests",
    "uitk.": "uitk_inspection",
  };
  const hints = new Set<string>();

  for (const [project, rules] of Object.entries(policy.projects)) {
    for (const pattern of [...rules.allow, ...rules.ask]) {
      for (const [prefix, feature] of Object.entries(commandFeaturePrefixes)) {
        if ((pattern === prefix || pattern.startsWith(prefix)) && features[feature] !== true) {
          hints.add(`policy for ${project} references ${pattern}, but feature ${feature} is disabled`);
          break;
        }
      }
    }
  }

  return [...hints].sort();
}

function writeDoctorOutput(stdout: (text: string) => void, json: boolean, diagnostics: Record<string, unknown>, lines: string[], warnings: string[], errors: string[]): void {
  if (json) {
    stdout(`${JSON.stringify(diagnostics, null, 2)}\n`);
    return;
  }

  for (const line of lines) {
    stdout(`${line}\n`);
  }
  for (const warning of warnings) {
    stdout(`warn: ${warning}\n`);
  }
  for (const error of errors) {
    stdout(`error: ${error}\n`);
  }
  const status = errors.length > 0 ? "error" : warnings.length > 0 ? "warn" : "ok";
  stdout(`summary: ${status} (${warnings.length} warn, ${errors.length} error)\n`);
}

function writeWaitReadyOutput(stdout: (text: string) => void, json: boolean, result: Record<string, unknown>): void {
  if (json) {
    stdout(`${JSON.stringify(result, null, 2)}\n`);
    return;
  }

  if (result.status === "ready") {
    stdout(`ready: project ${String(result.project)} instance ${String(result.instance_id)}\n`);
  } else if (result.status === "timeout") {
    stdout(`timeout: project ${String(result.project)} was not ready (${String(result.cause)})\n`);
  } else {
    stdout(`error: ${String(result.cause)} (${String(result.error)})\n`);
  }
}

function defaultConfigPath(cwd: string, env: NodeJS.ProcessEnv = process.env): string {
  const localConfig = path.join(cwd, "config", "gateway.json");
  if (fs.existsSync(localConfig)) {
    return localConfig;
  }
  const localExample = path.join(cwd, "config", "gateway.example.json");
  if (fs.existsSync(localExample)) {
    return localExample;
  }
  const localAppData = env.LOCALAPPDATA;
  if (localAppData) {
    const installedConfig = path.join(localAppData, "SGG", "UnityMcpGateway", "config", "gateway.json");
    if (fs.existsSync(installedConfig)) {
      return installedConfig;
    }
  }
  return localExample;
}

function readPackageVersion(): string {
  for (const relativePath of ["../package.json", "./gateway-runtime.json"]) {
    try {
      const filePath = new URL(relativePath, import.meta.url);
      const parsed = JSON.parse(fs.readFileSync(filePath, "utf8")) as { version?: unknown };
      if (typeof parsed.version === "string") {
        return parsed.version;
      }
    } catch {
      // Bundled Unity runtime keeps gateway-runtime.json next to the entrypoint.
    }
  }

  return "0.0.0";
}

export function waitForShutdown(onShutdown: () => Promise<void>, options: {
  input?: NodeJS.ReadableStream;
  transport?: { onclose?: () => void };
  signals?: Pick<EventEmitter, "once" | "off">;
} = {}): Promise<void> {
  return new Promise((resolve, reject) => {
    const signals = options.signals ?? process;
    const originalOnClose = options.transport?.onclose;
    let closing = false;
    const close = async () => {
      if (closing) return;
      closing = true;
      signals.off("SIGINT", close);
      signals.off("SIGTERM", close);
      options.input?.off("end", close);
      options.input?.off("close", close);
      if (options.transport) {
        if (originalOnClose) options.transport.onclose = originalOnClose;
        else delete options.transport.onclose;
      }
      try { await onShutdown(); resolve(); }
      catch (error) { reject(error); }
    };
    signals.once("SIGINT", close);
    signals.once("SIGTERM", close);
    options.input?.once("end", close);
    options.input?.once("close", close);
    if (options.transport) options.transport.onclose = () => {
      try { originalOnClose?.(); }
      finally { void close(); }
    };
    if (options.input && "readableEnded" in options.input && options.input.readableEnded === true) void close();
  });
}

function helpText(): string {
  return `Usage: sgg-unity-mcp-gateway [--config path] [--policy path] [--path /mcp] [--stdio]
       sgg-unity-mcp-gateway stdio-shim [--url http://127.0.0.1:27182/mcp] [--config path] [--path /mcp] [--token-env ENV] [--legacy-text-result]
       sgg-unity-mcp-gateway doctor [--config path] [--policy path] [--path /mcp] [--strict] [--json]
       sgg-unity-mcp-gateway wait-ready --project id [--config path] [--timeout-ms ms] [--interval-ms ms] [--json]
       sgg-unity-mcp-gateway report-problem --project id --summary text [--include-doctor] [--config path] [--policy path] [--json]
       sgg-unity-mcp-gateway setup <opencode|cursor|assistant> [--config path] [--output path] [--path /mcp] [--url http://127.0.0.1:27182/mcp] [--dry-run|--write] [--json]
       sgg-unity-mcp-gateway setup opencode-presentation --workspace path --status|--dry-run|--write|--remove [--json]
       sgg-unity-mcp-gateway policy --help
       sgg-unity-mcp-gateway approval --help
       sgg-unity-mcp-gateway --test-run-store <status|preview-cleanup|cleanup|configure> --config absolute-path [--policy absolute-path] [--json] [--confirm --preview-fingerprint sha256:hash]
       sgg-unity-mcp-gateway --test-run-store configure --config absolute-path --max-bytes bytes --max-bindings count --confirm --config-fingerprint sha256:hash --json
`;
}

function reportProblemHelpText(): string {
  return `Usage: sgg-unity-mcp-gateway report-problem --project id --summary text [--include-doctor] [--config path] [--policy path] [--json]

Writes a bounded redacted JSON report under logging.audit_dir/problem-reports when config loads, otherwise a temp fallback. No output path argument is accepted.
`;
}

function stdioShimHelpText(): string {
  return `Usage: sgg-unity-mcp-gateway stdio-shim [--url http://127.0.0.1:27182/mcp] [--config path] [--path /mcp] [--token-env ENV] [--legacy-text-result]

Legacy MCP compatibility transport only. Use direct HTTP or --stdio for MCP 2026-07-28 and MRTR.
--legacy-text-result projects canonical structured results into bounded JSON text for generated OpenCode Windows stdio configurations.
`;
}

function setupHelpText(): string {
  return `Usage: sgg-unity-mcp-gateway setup <opencode|cursor|assistant> [--config path] [--output path] [--path /mcp] [--url http://127.0.0.1:27182/mcp] [--connection-mode remote-http|windows-stdio] [--dry-run|--write] [--json]

Default mode is dry-run. In WSL, OpenCode setup defaults to windows-stdio when the Gateway is bound to Windows loopback. Remote HTTP requires an explicit protected non-loopback configuration. Token values are never written to client config files.

${opencodePresentationSetupHelpText()}
`;
}

function isMainModule(): boolean {
  const entry = process.argv[1];
  return entry ? fileURLToPath(import.meta.url) === path.resolve(entry) : false;
}

if (isMainModule()) {
  if (!isMainThread) {
    // This private role is never a public CLI command. Invalid worker data must
    // fail inside the worker service, not fall through to an MCP listener.
    if (!parentPort) throw new Error("Test operation store worker port is unavailable");
    runTestRunOperationStoreWorker(parentPort, workerData);
  } else {
    main().catch((error) => {
      process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
      process.exitCode = 1;
    });
  }
}
