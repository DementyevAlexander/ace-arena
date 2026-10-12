#!/usr/bin/env node

import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { execFileSync } from "node:child_process";
import { pathToFileURL } from "node:url";

import { Client } from "@modelcontextprotocol/sdk/client/index.js";
import { StreamableHTTPClientTransport } from "@modelcontextprotocol/sdk/client/streamableHttp.js";

import { RELEASE_UNITY_6000_4_VERSION, RELEASE_UNITY_6000_5_VERSION } from "./release-matrix.mjs";

export const TARGET_LINES = Object.freeze(["6000_4", "6000_5"]);
export const COMMANDS = Object.freeze([
  "sgg_unity_projects",
  "sgg_unity_describe:commands",
  "sgg_unity_describe:command:compile.status",
  "sgg_unity_call:compile.status",
  "sgg_unity_describe:command:test.run",
  "sgg_unity_describe:command:test.run.status",
  "sgg_unity_call:test.run",
  "sgg_unity_call:test.run.status",
]);

const DEFAULT_CALL_TIMEOUT_MS = 30_000;
const DEFAULT_TOTAL_TIMEOUT_MS = 1_800_000;
const DEFAULT_POLL_INTERVAL_MS = 1_000;
const DEFAULT_TEST_MAX_SECONDS = 600;
const DEFAULT_CLEANUP_TIMEOUT_MS = 5_000;
const MAX_CALL_TIMEOUT_MS = 120_000;
const MAX_TOTAL_TIMEOUT_MS = 14_400_000;
const MAX_POLL_INTERVAL_MS = 30_000;
const MAX_CLEANUP_TIMEOUT_MS = 30_000;
const TARGET_CALL_ALLOWANCE_COUNT = 8;
const MAX_EVIDENCE_CHARS = 64_000;
const TOKEN_HEADER = "x-sgg-unity-mcp-token";
const TERMINAL_FAILURES = new Set([
  "callback_lost_after_playmode_exit",
  "cancelled",
  "failed",
  "legacy_terminal_counts_untrusted",
  "startup_failed",
  "timed_out",
  "terminal_counts_unavailable",
  "timeout",
  "unknown_after_reload",
]);
export const TEST_RUN_DEFINITE_PRE_EXECUTION_CODES = new Set([
  "permission_denied",
  "confirmation_required",
  "schema_validation_error",
  "schema_validation_failed",
  "invalid_field_value",
  "feature_disabled",
  "command_not_found",
  "project_required",
  "project_not_found",
  "project_offline",
  "project_stale",
  "duplicate_project_id",
  "compilation_failed",
  "test_run_dirty_scene_blocked",
  "test_run_scene_state_unavailable",
  "unity_busy",
  "unity_test_framework_unavailable",
  "unity_test_run_busy",
]);

export function readSmokeConfig(env, currentPackageVersion) {
  const errors = [];
  const urlText = required(env, "SGG_UNITY_SMOKE_URL", errors);
  const token = required(env, "SGG_UNITY_SMOKE_TOKEN", errors);
  const expectedPackageVersion = required(env, "SGG_UNITY_SMOKE_PACKAGE_VERSION", errors);
  let url;
  if (urlText) {
    try {
      url = new URL(urlText);
      if (!/^https?:$/.test(url.protocol) || !isLoopback(url.hostname) || url.username || url.password) {
        errors.push("SGG_UNITY_SMOKE_URL must be an HTTP(S) loopback URL without embedded credentials");
      }
    } catch {
      errors.push("SGG_UNITY_SMOKE_URL must be a valid URL");
    }
  }
  if (expectedPackageVersion && expectedPackageVersion !== currentPackageVersion) {
    errors.push(`SGG_UNITY_SMOKE_PACKAGE_VERSION must equal current package version ${currentPackageVersion}`);
  }

  const targets = TARGET_LINES.map((key) => {
    const prefix = `SGG_UNITY_SMOKE_${key}`;
    const targetErrors = [];
    const selector = required(env, `${prefix}_SELECTOR`, targetErrors);
    const unityVersion = required(env, `${prefix}_UNITY_VERSION`, targetErrors);
    const testFilter = required(env, `${prefix}_TEST_FILTER`, targetErrors);
    const testAssembly = required(env, `${prefix}_TEST_ASSEMBLY`, targetErrors);
    const expectedTestCount = requiredBoundedInteger(env, `${prefix}_EXPECTED_TEST_COUNT`, 1, 2_147_483_647, targetErrors);
    const testFilterSafeForEvidence = Boolean(testFilter) && testFilter.length <= 160 && !/[\\/]/.test(testFilter) && !/https?:\/\//i.test(testFilter);
    const testAssemblySafeForEvidence = Boolean(testAssembly) && testAssembly.length <= 160 && /^[A-Za-z0-9_.-]+$/.test(testAssembly);
    const line = key.replace("_", ".");
    const requiredUnityVersion = key === "6000_4" ? RELEASE_UNITY_6000_4_VERSION : RELEASE_UNITY_6000_5_VERSION;
    if (unityVersion && unityVersion !== requiredUnityVersion) {
      targetErrors.push(`${prefix}_UNITY_VERSION must equal the current exact baseline ${requiredUnityVersion}`);
    }
    if (testFilter && (testFilter.length > 160 || /[\\/]/.test(testFilter) || /https?:\/\//i.test(testFilter))) {
      targetErrors.push(`${prefix}_TEST_FILTER must be a test name/filter of at most 160 characters without URLs, slashes, or backslashes`);
    }
    if (testAssembly && !testAssemblySafeForEvidence) {
      targetErrors.push(`${prefix}_TEST_ASSEMBLY must be an assembly name of at most 160 safe characters`);
    }
    return {
      key,
      line,
      selector,
      unityVersion,
      testFilter,
      testAssembly,
      expectedTestCount,
      evidenceTestFilter: testFilterSafeForEvidence ? testFilter : null,
      evidenceTestAssembly: testAssemblySafeForEvidence ? testAssembly : null,
      expectedPackageVersion,
      errors: targetErrors,
    };
  });

  const callTimeoutMs = boundedInteger(env, "SGG_UNITY_SMOKE_CALL_TIMEOUT_MS", DEFAULT_CALL_TIMEOUT_MS, 1_000, MAX_CALL_TIMEOUT_MS, errors);
  const totalTimeoutMs = boundedInteger(env, "SGG_UNITY_SMOKE_TOTAL_TIMEOUT_MS", DEFAULT_TOTAL_TIMEOUT_MS, 10_000, MAX_TOTAL_TIMEOUT_MS, errors);
  const testMaxSeconds = boundedInteger(env, "SGG_UNITY_SMOKE_TEST_MAX_SECONDS", DEFAULT_TEST_MAX_SECONDS, 1, 3_600, errors);
  const targetBudgetMs = testMaxSeconds * 1_000 + TARGET_CALL_ALLOWANCE_COUNT * callTimeoutMs;
  const minimumTotalTimeoutMs = callTimeoutMs + TARGET_LINES.length * targetBudgetMs;
  if (totalTimeoutMs < minimumTotalTimeoutMs) {
    errors.push(`SGG_UNITY_SMOKE_TOTAL_TIMEOUT_MS must be at least ${minimumTotalTimeoutMs} for connection plus both configured target budgets`);
  }

  return {
    url,
    token,
    expectedPackageVersion,
    artifactPath: optional(env, "SGG_UNITY_SMOKE_ARTIFACT_PATH") || path.join("tmp", "harden-07b-unity-smoke.json"),
    callTimeoutMs,
    totalTimeoutMs,
    pollIntervalMs: boundedInteger(env, "SGG_UNITY_SMOKE_POLL_INTERVAL_MS", DEFAULT_POLL_INTERVAL_MS, 10, MAX_POLL_INTERVAL_MS, errors),
    testMaxSeconds,
    cleanupTimeoutMs: boundedInteger(env, "SGG_UNITY_SMOKE_CLEANUP_TIMEOUT_MS", DEFAULT_CLEANUP_TIMEOUT_MS, 100, MAX_CLEANUP_TIMEOUT_MS, errors),
    targetBudgetMs,
    targets,
    errors,
  };
}

export function resolveExactReadyProject(projects, target, expectedPackageVersion) {
  if (!Array.isArray(projects)) throw new Error("sgg_unity_projects did not return a projects array");
  const matches = projects.filter((project) => projectMatchesSelector(project, target.selector));
  if (matches.length !== 1) throw new Error(`selector must resolve to exactly one project; resolved ${matches.length}`);
  const project = matches[0];
  if (!isObject(project) || project.stale !== false || project.status !== "ready") {
    throw new Error("selected project heartbeat is stale or not ready");
  }
  if (project.unity_version !== target.unityVersion) {
    throw new Error(`selected project Unity version does not equal ${target.unityVersion}`);
  }
  if (project.package_version !== expectedPackageVersion) {
    throw new Error(`selected project package version does not equal ${expectedPackageVersion}`);
  }
  if (typeof project.project !== "string" || typeof project.instance_id !== "string") {
    throw new Error("selected project is missing public project identity");
  }
  return project;
}

export function classifyTestStatus(data) {
  if (!isObject(data)) throw new Error("test status data must be an object");
  const status = stringField(data, "status");
  const result = typeof data.result === "string" ? data.result : "";
  if (status === "running" || (!status && result === "running")) return { terminal: false };
  if (TERMINAL_FAILURES.has(status) || TERMINAL_FAILURES.has(result)) {
    return { terminal: true, pass: false, reason: `test run ended in ${status || result}`, counts: testCounts(data) };
  }
  const counts = testCounts(data);
  const execution = isObject(data.execution_result) ? data.execution_result : undefined;
  const authoritative = data.artifact_schema_version === 3
    && data.terminal_counts_authoritative === true
    && data.terminal_count_source === "unity_test_runner"
    && execution?.available === true
    && execution.source === "unity_test_runner"
    && hasAuthoritativeCountEvidence(data, execution);
  if (!authoritative) {
    return status === "completed"
      ? { terminal: true, pass: false, reason: "test run lacks authoritative schema-v3 Unity TestRunner terminal counts", counts }
      : { terminal: false };
  }
  if (counts.total <= 0) return { terminal: true, pass: false, reason: "test run executed zero tests", counts };
  if (data.success !== true || counts.passed !== counts.total || counts.failed !== 0 || counts.skipped !== 0) {
    return { terminal: true, pass: false, reason: "test run has non-pass or inconsistent terminal counts", counts };
  }
  return { terminal: true, pass: true, counts };
}

export function createEvidence({ dateUtc, commit, packageVersion, targets, cleanup = { result: "not_run", reason: "cleanup outcome not supplied" } }) {
  return {
    schema_version: 2,
    evidence_kind: "harden-07b-bounded-unity-smoke",
    date_utc: dateUtc,
    commit,
    package_version: packageVersion,
    cleanup,
    latest_static: { result: "not_run", reason: "not run by check:unity-smoke" },
    unity_6000_4_smoke: targets[0],
    unity_6000_5_smoke: targets[1],
    full_release_matrix: { result: "not_run", reason: "outside HARDEN-07B scope" },
  };
}

export function serializeEvidence(evidence) {
  assertRedactedEvidence(evidence);
  const text = `${JSON.stringify(evidence, null, 2)}\n`;
  if (text.length > MAX_EVIDENCE_CHARS) throw new Error("evidence exceeds bounded output limit");
  return text;
}

export async function runUnitySmoke(options) {
  const env = options.env ?? process.env;
  const packageVersion = options.packageVersion;
  const config = readSmokeConfig(env, packageVersion);
  const now = options.now ?? (() => new Date());
  const commit = options.commit ?? "unknown";
  const log = options.log ?? console.log;
  const sleep = options.sleep ?? ((ms) => new Promise((resolve) => setTimeout(resolve, ms)));
  const outcomes = config.targets.map(initialOutcome);
  const overallDeadline = now().getTime() + config.totalTimeoutMs;
  let client;
  let unsafeActiveRun = false;
  let cleanup = { result: "not_run", reason: "MCP client was not established" };
  let infrastructureFailed = false;
  let connectionAttempted = false;

  try {
    if (config.errors.length > 0 || config.targets.some((target) => target.errors.length > 0)) {
      for (let index = 0; index < config.targets.length; index += 1) {
        const target = config.targets[index];
        outcomes[index] = target.errors.length > 0
          ? failedOutcome(target, target.errors.join("; "))
          : notRunOutcome(target, `global configuration failed: ${config.errors.join("; ") || "another target is invalid"}`);
      }
    } else {
      const connectTimeoutMs = Math.min(config.callTimeoutMs, Math.max(1, overallDeadline - now().getTime()));
      connectionAttempted = true;
      client = await boundedPromise(
        Promise.resolve().then(() => (options.clientFactory ?? createOfficialClient)(config)),
        connectTimeoutMs,
        "MCP connection timed out",
        options.timers,
      );
      for (let index = 0; index < config.targets.length; index += 1) {
        const target = config.targets[index];
        if (unsafeActiveRun) {
          outcomes[index] = notRunOutcome(target, "a prior test run has an uncertain active state; no further write was attempted");
          continue;
        }
        const result = await runTarget({ client, config, target, overallDeadline, now, sleep, secrets: [config.token, config.url?.href] });
        outcomes[index] = result.outcome;
        unsafeActiveRun = result.unsafeActiveRun;
        log(`[check-unity-smoke] Unity ${target.line}: ${result.outcome.result}`);
      }
    }
  } catch (error) {
    infrastructureFailed = true;
    if (isObject(error) && isObject(error.cleanupResult)) {
      cleanup = error.cleanupResult;
    } else if (connectionAttempted) {
      cleanup = { result: "fail", reason: "connection cleanup ownership was unavailable after connection failure or timeout" };
    }
    const reason = `MCP connection/precondition failed: ${safeError(error, [config.token, config.url?.href])}`;
    for (let index = 0; index < outcomes.length; index += 1) {
      if (outcomes[index].result === "not_run") outcomes[index] = failedOutcome(config.targets[index], reason);
    }
  } finally {
    if (client) {
      try {
        await boundedPromise(Promise.resolve().then(() => client.close()), config.cleanupTimeoutMs * 2 + 100, "MCP cleanup orchestration timed out", options.timers);
        cleanup = { result: "pass" };
      } catch (error) {
        infrastructureFailed = true;
        cleanup = { result: "fail", reason: safeError(error, [config.token, config.url?.href]) };
      }
    }
    const evidence = createEvidence({
      dateUtc: now().toISOString(),
      commit,
      packageVersion,
      targets: outcomes,
      cleanup,
    });
    const evidenceText = serializeEvidence(evidence);
    await (options.writeEvidence ?? writeEvidence)(config.artifactPath, evidenceText);
  }

  return !infrastructureFailed && cleanup.result === "pass" && outcomes.every((outcome) => outcome.result === "pass") ? 0 : 1;
}

async function runTarget({ client, config, target, overallDeadline, now, sleep, secrets }) {
  let project;
  let counts = null;
  let runMayBeActive = false;
  let terminal = false;
  const commandResults = [];
  const targetDeadline = Math.min(overallDeadline, now().getTime() + config.targetBudgetMs);
  const record = async (label, tool, args) => {
    try {
      ensureBeforeDeadline(targetDeadline, now);
      const result = await callGateway(client, tool, args, Math.min(config.callTimeoutMs, Math.max(1, targetDeadline - now().getTime())));
      commandResults.push({ command: label, result: "pass" });
      return result;
    } catch (error) {
      commandResults.push({ command: label, result: "fail" });
      throw error;
    }
  };

  try {
    const projectsResult = await record("sgg_unity_projects", "sgg_unity_projects", {});
    project = resolveExactReadyProject(objectData(projectsResult).projects, target, config.expectedPackageVersion);
    const selector = project.instance_id;
    await record("describe:commands", "sgg_unity_describe", { project: selector, selector: "commands" });
    assertCommandSchema(objectData(await record("describe:command:compile.status", "sgg_unity_describe", { project: selector, selector: "command:compile.status" })), "compile.status", []);
    await record("compile.status", "sgg_unity_call", { project: selector, command: "compile.status", args_json: "{}", mode: "wait" });
    assertCommandSchema(objectData(await record("describe:command:test.run", "sgg_unity_describe", { project: selector, selector: "command:test.run" })), "test.run", ["mode", "filter", "assembly", "max_seconds"]);
    assertCommandSchema(objectData(await record("describe:command:test.run.status", "sgg_unity_describe", { project: selector, selector: "command:test.run.status" })), "test.run.status", ["operation_id"]);
    let run;
    try {
      run = objectData(await record("test.run", "sgg_unity_call", {
        project: selector,
        command: "test.run",
        args_json: JSON.stringify({ mode: "editmode", filter: target.testFilter, assembly: target.testAssembly, max_seconds: config.testMaxSeconds }),
        mode: "wait",
      }));
      runMayBeActive = true;
    } catch (error) {
      runMayBeActive = !isDefiniteTestRunPreExecutionError(error);
      throw error;
    }
    counts = knownTestCounts(run);
    if (run.mode === "playmode" || run.test_mode === "playmode") throw new Error("test.run unexpectedly reported PlayMode");
    const operationId = typeof run.operation_id === "string" ? run.operation_id : "";
    if (!operationId || operationId.length > 80) throw new Error("test.run did not return a bounded operation_id");

    let last = run;
    let polls = 0;
    while (true) {
      const classification = classifyTestStatus(last);
      if (classification.counts) counts = classification.counts;
      if (classification.terminal) {
        terminal = true;
        const attestation = terminalAttestation(last, target, classification.counts);
        const terminalMode = typeof last.test_mode === "string" ? last.test_mode : last.mode;
        const terminalFilter = typeof last.test_filter === "string" ? last.test_filter : last.filter;
        const terminalAssembly = typeof last.test_assembly === "string" ? last.test_assembly : last.assembly;
        const contractMismatch = terminalMode !== "editmode" || terminalFilter !== target.testFilter || terminalAssembly !== target.testAssembly;
        const cardinalityMismatch = classification.pass
          && classification.counts.total !== target.expectedTestCount;
        const outcome = classification.pass
          ? contractMismatch
            ? failedOutcome(target, "test run terminal mode/filter does not match the configured cardinality contract", project, commandResults, classification.counts, false, attestation)
            : cardinalityMismatch
            ? failedOutcome(target, `test run executed ${classification.counts.total} tests; expected exactly ${target.expectedTestCount}`, project, commandResults, classification.counts, false, attestation)
            : passedOutcome(target, project, commandResults, classification.counts, attestation)
          : failedOutcome(target, classification.reason, project, commandResults, classification.counts, false, attestation);
        return { outcome, unsafeActiveRun: false };
      }
      if (now().getTime() >= targetDeadline) throw new Error("target test polling timed out before a terminal state");
      await sleep(Math.min(config.pollIntervalMs, Math.max(0, targetDeadline - now().getTime())));
      if (now().getTime() >= targetDeadline) throw new Error("target test polling timed out before a terminal state");
      last = objectData(await record("test.run.status", "sgg_unity_call", {
        project: selector,
        command: "test.run.status",
        args_json: JSON.stringify({ operation_id: operationId }),
        mode: "wait",
      }));
      counts = knownTestCounts(last) ?? counts;
      polls += 1;
      if (polls > Math.ceil(config.targetBudgetMs / Math.max(1, config.pollIntervalMs)) + 1) {
        throw new Error("test polling exceeded bounded target poll count");
      }
    }
  } catch (error) {
    const unsafeActiveRun = runMayBeActive && !terminal;
    const reason = safeError(error, secrets);
    return {
      outcome: failedOutcome(target, reason, project, commandResults, counts, unsafeActiveRun),
      unsafeActiveRun,
    };
  }
}

function isDefiniteTestRunPreExecutionError(error) {
  return error instanceof AuthoritativeGatewayError && TEST_RUN_DEFINITE_PRE_EXECUTION_CODES.has(error.code);
}

function assertCommandSchema(data, commandId, requiredProperties) {
  const command = isObject(data.command) ? data.command : isObject(data.descriptor) ? data.descriptor : undefined;
  if (!command || command.id !== commandId || !isObject(command.schema) || !isObject(command.schema.properties)) {
    throw new Error(`describe did not return the public ${commandId} command schema`);
  }
  for (const property of requiredProperties) {
    if (!Object.hasOwn(command.schema.properties, property)) throw new Error(`${commandId} schema is missing ${property}`);
  }
}

export async function createOfficialClient(config, dependencies = {}) {
  const Transport = dependencies.Transport ?? StreamableHTTPClientTransport;
  const ClientConstructor = dependencies.Client ?? Client;
  const transport = new Transport(config.url, {
    requestInit: { headers: { [TOKEN_HEADER]: config.token } },
    reconnectionOptions: { maxReconnectionDelay: 1_000, initialReconnectionDelay: 100, reconnectionDelayGrowFactor: 1.5, maxRetries: 1 },
  });
  const client = new ClientConstructor({ name: "sgg-unity-smoke-check", version: config.expectedPackageVersion });
  try {
    await client.connect(transport, { timeout: config.callTimeoutMs, maxTotalTimeout: config.callTimeoutMs });
  } catch (error) {
    const cleanupResult = await cleanupOfficialClient(client, transport, config, dependencies.timers);
    throw new OfficialConnectError(error, cleanupResult);
  }
  return {
    callTool: async (name, args, timeout) => client.callTool({ name, arguments: args }, undefined, { timeout }),
    close: async () => assertCleanupPassed(await cleanupOfficialClient(client, transport, config, dependencies.timers)),
  };
}

async function cleanupOfficialClient(client, transport, config, timers) {
  const failures = [];
  if (transport.sessionId) {
    try {
      await boundedPromise(Promise.resolve().then(() => transport.terminateSession()), config.cleanupTimeoutMs, "MCP session termination timed out", timers);
    } catch (error) {
      failures.push(`session termination failed: ${safeError(error, [config.token, config.url?.href])}`);
    }
  }
  try {
    await boundedPromise(Promise.resolve().then(() => client.close()), config.cleanupTimeoutMs, "MCP client close timed out", timers);
  } catch (error) {
    failures.push(`client close failed: ${safeError(error, [config.token, config.url?.href])}`);
  }
  return failures.length === 0 ? { result: "pass" } : { result: "fail", reason: boundedReason(failures.join("; ")) };
}

function assertCleanupPassed(result) {
  if (result.result !== "pass") throw new CleanupError(result.reason);
}

class CleanupError extends Error {
  constructor(message) {
    super(message);
    this.name = "CleanupError";
  }
}

class OfficialConnectError extends Error {
  constructor(error, cleanupResult) {
    super(error instanceof Error ? error.message : String(error));
    this.name = "OfficialConnectError";
    this.cleanupResult = cleanupResult;
  }
}

export class AuthoritativeGatewayError extends Error {
  constructor(tool, code) {
    super(`${tool} failed with ${code}`);
    this.name = "AuthoritativeGatewayError";
    this.code = code;
    this.authoritative = true;
  }
}

export async function boundedPromise(promise, timeoutMs, label, timers = {}) {
  const schedule = timers.setTimeout ?? setTimeout;
  const cancel = timers.clearTimeout ?? clearTimeout;
  let timer;
  const timeout = new Promise((_, reject) => {
    timer = schedule(() => reject(new Error(label)), timeoutMs);
  });
  try {
    return await Promise.race([promise, timeout]);
  } finally {
    if (timer !== undefined) cancel(timer);
  }
}

async function callGateway(client, name, args, timeout) {
  const result = await client.callTool(name, args, timeout);
  const value = result?.structuredContent;
  if (!isObject(value)) throw new Error(`${name} returned no structured MCP result`);
  if (value.status !== "ok") {
    const code = isObject(value.error) && typeof value.error.code === "string" ? value.error.code : "gateway_error";
    throw new AuthoritativeGatewayError(name, code);
  }
  return value;
}

function objectData(result) {
  if (!isObject(result.data)) throw new Error("Gateway result data must be an object");
  return result.data;
}

function projectMatchesSelector(project, selector) {
  if (!isObject(project)) return false;
  if ([project.project, project.display_name, project.instance_id, project.canonical_project_root].includes(selector)) return true;
  return Array.isArray(project.aliases) && project.aliases.some((alias) => isObject(alias) && (alias.host_path === selector || alias.canonical_path === selector));
}

function initialOutcome(target) {
  return notRunOutcome(target, "target was not attempted");
}

function baseOutcome(target) {
  return {
    target_line: target.line,
    expected_unity_version: target.unityVersion || null,
    expected_package_version: target.expectedPackageVersion || null,
    project_selector_hash: target.selector ? hashIdentity(target.selector) : null,
    project_id: null,
    instance_id_hash: null,
    test_mode: "editmode",
    test_filter: target.evidenceTestFilter || null,
    test_assembly: target.evidenceTestAssembly || null,
    expected_test_count: target.expectedTestCount ?? null,
    actual_test_count: null,
    count_matches: false,
    artifact_schema_version: null,
    terminal_counts_authoritative: false,
    terminal_count_source: null,
    count_evidence_valid: false,
    raw_terminal_counts: null,
    counts: null,
    commands: [],
    unsafe_active_run: false,
    unsafe_active_reason: null,
  };
}

function passedOutcome(target, project, commands, counts, attestation) {
  return { ...baseOutcome(target), ...projectEvidence(project), ...attestation, expected_package_version: project.package_version, commands, counts, result: "pass" };
}

function failedOutcome(target, reason, project, commands = [], counts = null, unsafeActiveRun = false, attestation = {}) {
  return {
    ...baseOutcome(target),
    ...(project ? projectEvidence(project) : {}),
    ...attestation,
    commands,
    counts,
    unsafe_active_run: unsafeActiveRun,
    unsafe_active_reason: unsafeActiveRun ? "test.run may still be active; no subsequent write was attempted" : null,
    result: "fail",
    reason: boundedReason(reason),
  };
}

function terminalAttestation(data, target, counts) {
  const execution = isObject(data.execution_result) ? data.execution_result : undefined;
  const actualTestCount = counts?.total ?? null;
  return {
    actual_test_count: actualTestCount,
    count_matches: Number.isInteger(actualTestCount) && actualTestCount === target.expectedTestCount,
    artifact_schema_version: Number.isInteger(data.artifact_schema_version) ? data.artifact_schema_version : null,
    terminal_counts_authoritative: data.terminal_counts_authoritative === true,
    terminal_count_source: typeof data.terminal_count_source === "string" ? data.terminal_count_source : null,
    count_evidence_valid: hasAuthoritativeCountEvidence(data, execution),
    raw_terminal_counts: smokeRawTerminalCounts(data, execution),
  };
}

function smokeRawTerminalCounts(data, execution) {
  if (!hasAuthoritativeCountEvidence(data, execution)) return null;
  const testCaseCountAvailable = data.count_consistency.test_case_count_available;
  return {
    pass_count: data.pass_count,
    fail_count: data.fail_count,
    skip_count: data.skip_count,
    inconclusive_count: data.inconclusive_count,
    authoritative_leaf_total: data.authoritative_leaf_total,
    assert_count: data.assert_count,
    test_case_count: testCaseCountAvailable ? data.test_case_count : null,
    test_case_count_source: testCaseCountAvailable ? data.test_case_count_source : null,
    count_consistency: {
      leaf_sum_matches_total: true,
      assert_count_semantics: "not_a_test_count",
      test_case_count_available: testCaseCountAvailable,
      test_case_count_matches_leaf_total: testCaseCountAvailable ? data.count_consistency.test_case_count_matches_leaf_total : null,
    },
  };
}

function hasAuthoritativeCountEvidence(data, execution) {
  if (!isObject(execution) || !isObject(data.count_consistency) || !isObject(execution.count_consistency)) return false;
  const fields = ["pass_count", "fail_count", "skip_count", "inconclusive_count", "authoritative_leaf_total", "assert_count"];
  if (fields.some((field) => !Number.isSafeInteger(data[field]) || data[field] < 0 || data[field] !== execution[field])) return false;
  if (JSON.stringify(rawCountProjection(data)) !== JSON.stringify(rawCountProjection(execution))) return false;
  const leafTotal = data.pass_count + data.fail_count + data.skip_count + data.inconclusive_count;
  if (!Number.isSafeInteger(leafTotal) || leafTotal !== data.authoritative_leaf_total || leafTotal !== data.total || leafTotal !== execution.total) return false;
  if (data.pass_count !== data.passed || execution.pass_count !== execution.passed || execution.passed !== data.passed) return false;
  if (data.fail_count !== data.failed || execution.fail_count !== execution.failed || execution.failed !== data.failed) return false;
  if (data.skipped !== data.skip_count + data.inconclusive_count || execution.skipped !== data.skipped) return false;
  for (const consistency of [data.count_consistency, execution.count_consistency]) {
    if (consistency.authoritative_counts_available !== true
      || consistency.leaf_sum_matches_total !== true
      || consistency.assert_count_semantics !== "not_a_test_count"
      || typeof consistency.test_case_count_available !== "boolean") return false;
  }
  if (data.count_consistency.test_case_count_available !== execution.count_consistency.test_case_count_available) return false;
  if (!data.count_consistency.test_case_count_available) {
    return data.test_case_count === undefined && execution.test_case_count === undefined;
  }
  return Number.isSafeInteger(data.test_case_count)
    && data.test_case_count >= 0
    && data.test_case_count === execution.test_case_count
    && data.test_case_count_source === "unity_test_runner_root.TestCaseCount"
    && execution.test_case_count_source === data.test_case_count_source
    && data.count_consistency.test_case_count_matches_leaf_total === (data.test_case_count === leafTotal)
    && execution.count_consistency.test_case_count_matches_leaf_total === data.count_consistency.test_case_count_matches_leaf_total;
}

function rawCountProjection(source) {
  const consistency = source.count_consistency;
  return {
    pass_count: source.pass_count,
    fail_count: source.fail_count,
    skip_count: source.skip_count,
    inconclusive_count: source.inconclusive_count,
    authoritative_leaf_total: source.authoritative_leaf_total,
    assert_count: source.assert_count,
    test_case_count_present: Object.hasOwn(source, "test_case_count"),
    test_case_count: Object.hasOwn(source, "test_case_count") ? source.test_case_count : null,
    test_case_count_source_present: Object.hasOwn(source, "test_case_count_source"),
    test_case_count_source: Object.hasOwn(source, "test_case_count_source") ? source.test_case_count_source : null,
    count_consistency: {
      authoritative_counts_available: consistency.authoritative_counts_available,
      leaf_sum_matches_total: consistency.leaf_sum_matches_total,
      assert_count_semantics: consistency.assert_count_semantics,
      test_case_count_available: consistency.test_case_count_available,
      test_case_count_matches_leaf_total_present: Object.hasOwn(consistency, "test_case_count_matches_leaf_total"),
      test_case_count_matches_leaf_total: Object.hasOwn(consistency, "test_case_count_matches_leaf_total") ? consistency.test_case_count_matches_leaf_total : null,
    },
  };
}

function notRunOutcome(target, reason) {
  return { ...baseOutcome(target), result: "not_run", reason: boundedReason(reason) };
}

function projectEvidence(project) {
  return {
    project_id: project.project,
    instance_id_hash: hashIdentity(project.instance_id),
  };
}

function testCounts(data) {
  const source = isObject(data.execution_result) ? data.execution_result : data;
  return {
    total: nonnegativeInteger(source.total),
    passed: nonnegativeInteger(source.passed),
    failed: nonnegativeInteger(source.failed),
    skipped: nonnegativeInteger(source.skipped),
  };
}

function knownTestCounts(data) {
  const execution = isObject(data.execution_result) ? data.execution_result : undefined;
  return data.artifact_schema_version === 3
    && data.terminal_counts_authoritative === true
    && data.terminal_count_source === "unity_test_runner"
    && execution?.available === true
    && execution.source === "unity_test_runner"
    && hasAuthoritativeCountEvidence(data, execution)
    ? testCounts(data)
    : null;
}

function nonnegativeInteger(value) {
  return Number.isInteger(value) && value >= 0 ? value : 0;
}

function stringField(value, key) {
  return typeof value[key] === "string" ? value[key] : "";
}

function required(env, name, errors) {
  const value = optional(env, name);
  if (!value) errors.push(`${name} is required`);
  return value;
}

function optional(env, name) {
  return typeof env[name] === "string" ? env[name].trim() : "";
}

function boundedInteger(env, name, fallback, min, max, errors) {
  const text = optional(env, name);
  if (!text) return fallback;
  const value = Number(text);
  if (!Number.isInteger(value) || value < min || value > max) {
    errors.push(`${name} must be an integer from ${min} to ${max}`);
    return fallback;
  }
  return value;
}

function requiredBoundedInteger(env, name, min, max, errors) {
  const text = required(env, name, errors);
  if (!text) return null;
  const value = Number(text);
  if (!Number.isInteger(value) || value < min || value > max) {
    errors.push(`${name} must be an integer from ${min} to ${max}`);
    return null;
  }
  return value;
}

function isLoopback(hostname) {
  const normalized = hostname.replace(/^\[|\]$/g, "").toLowerCase();
  return normalized === "localhost" || normalized === "127.0.0.1" || normalized === "::1";
}

function hashIdentity(value) {
  return `sha256:${createHash("sha256").update("sgg.unity-smoke.identity.v1\0").update(value).digest("hex")}`;
}

function boundedReason(reason) {
  return String(reason).replace(/[\r\n\t]+/g, " ").slice(0, 240);
}

function safeError(error, secrets = []) {
  const message = error instanceof Error ? error.message : String(error);
  let redacted = message;
  for (const secret of secrets) {
    if (secret) redacted = redacted.replaceAll(String(secret), "[redacted]");
  }
  return boundedReason(redacted.replace(/https?:\/\/\S+/gi, "[redacted-url]").replace(/[A-Za-z]:[\\/][^\s]+|\/(?:[^\s/]+\/)+[^\s]+/g, "[redacted-path]"));
}

function ensureBeforeDeadline(deadline, now) {
  if (now().getTime() >= deadline) throw new Error("smoke deadline exceeded");
}

function assertRedactedEvidence(evidence) {
  const text = JSON.stringify(evidence);
  if (/https?:\/\//i.test(text) || /authorization|x-sgg-unity-mcp-token|\\[A-Za-z0-9_. -]+\\|(?:^|[":])\/(?:[^/"\s]+\/)+/i.test(text)) {
    throw new Error("evidence contains a URL, header, or absolute path");
  }
  if (/"(?:token|headers?|url)"\s*:/i.test(text)) throw new Error("evidence contains a forbidden secret-bearing field");
}

async function writeEvidence(filePath, text) {
  const absolute = path.resolve(filePath);
  await fs.promises.mkdir(path.dirname(absolute), { recursive: true });
  await fs.promises.writeFile(absolute, text, { encoding: "utf8", mode: 0o600 });
}

function isObject(value) {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

export function currentCommit() {
  try {
    return execFileSync("git", ["rev-parse", "HEAD"], { encoding: "utf8", stdio: ["ignore", "pipe", "ignore"] }).trim();
  } catch {
    return "unknown";
  }
}

const isMain = process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href;
if (isMain) {
  const packageJson = JSON.parse(fs.readFileSync(path.resolve("package.json"), "utf8"));
  runUnitySmoke({ packageVersion: packageJson.version, commit: currentCommit() })
    .then((status) => { process.exitCode = status; })
    .catch((error) => {
      console.error(`[check-unity-smoke] ${safeError(error, [process.env.SGG_UNITY_SMOKE_TOKEN, process.env.SGG_UNITY_SMOKE_URL])}`);
      process.exitCode = 1;
    });
}
