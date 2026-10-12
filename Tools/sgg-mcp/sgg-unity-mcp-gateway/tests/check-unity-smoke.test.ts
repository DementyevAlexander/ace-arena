import fs from "node:fs";
import path from "node:path";
import { describe, expect, it, vi } from "vitest";
import {
  classifyTestStatus,
  createOfficialClient,
  createEvidence,
  currentCommit,
  readSmokeConfig,
  resolveExactReadyProject,
  runUnitySmoke,
  serializeEvidence,
// @ts-expect-error The directly executable Node helper has no separate declaration file.
} from "../scripts/check-unity-smoke.mjs";

const VERSION = JSON.parse(fs.readFileSync(path.join(process.cwd(), "package.json"), "utf8")).version;

function environment(overrides: Record<string, string> = {}): Record<string, string> {
  return {
    SGG_UNITY_SMOKE_URL: "http://127.0.0.1:18444/mcp",
    SGG_UNITY_SMOKE_TOKEN: "top-secret-token",
    SGG_UNITY_SMOKE_PACKAGE_VERSION: VERSION,
    SGG_UNITY_SMOKE_6000_4_SELECTOR: "smoke-4",
    SGG_UNITY_SMOKE_6000_4_UNITY_VERSION: "6000.4.10f1",
    SGG_UNITY_SMOKE_6000_4_TEST_FILTER: "SGG.UnityMCP.Tests.PackageSmoke60004",
    SGG_UNITY_SMOKE_6000_4_TEST_ASSEMBLY: "SGG.UnityMCP.Editor.Tests",
    SGG_UNITY_SMOKE_6000_4_EXPECTED_TEST_COUNT: "2",
    SGG_UNITY_SMOKE_6000_5_SELECTOR: "smoke-5",
    SGG_UNITY_SMOKE_6000_5_UNITY_VERSION: "6000.5.3f1",
    SGG_UNITY_SMOKE_6000_5_TEST_FILTER: "SGG.UnityMCP.Tests.PackageSmoke60005",
    SGG_UNITY_SMOKE_6000_5_TEST_ASSEMBLY: "SGG.UnityMCP.Editor.Tests",
    SGG_UNITY_SMOKE_6000_5_EXPECTED_TEST_COUNT: "2",
    SGG_UNITY_SMOKE_ARTIFACT_PATH: "tmp/test-evidence.json",
    ...overrides,
  };
}

function project(line: "4" | "5", overrides: Record<string, unknown> = {}) {
  return {
    project: `smoke-${line}`,
    display_name: `Smoke ${line}`,
    instance_id: `instance-${line}`,
    canonical_project_root: `c:/smoke/${line}`,
    aliases: [],
    unity_version: line === "4" ? "6000.4.10f1" : "6000.5.3f1",
    package_version: VERSION,
    status: "ready",
    stale: false,
    ...overrides,
  };
}

function ok(data: Record<string, unknown>) {
  return { structuredContent: { status: "ok", data, diagnostics: [] } };
}

function gatewayError(code: string) {
  return { structuredContent: { status: "error", data: {}, error: { code, message: "rejected" }, diagnostics: [] } };
}

function descriptor(selector: string) {
  const id = selector.replace("command:", "");
  const properties = id === "test.run"
    ? { mode: {}, filter: {}, assembly: {}, max_seconds: {} }
    : id === "test.run.status" ? { operation_id: {} } : {};
  return selector === "commands" ? { commands: [] } : { command: { id, schema: { type: "object", properties } } };
}

function terminalResult(line: "4" | "5" = "4", counts = { total: 2, passed: 2, failed: 0, skipped: 0 }) {
  const passCount = counts.passed;
  const failCount = counts.failed;
  const skipCount = counts.skipped;
  const inconclusiveCount = 0;
  const raw = {
    pass_count: passCount,
    fail_count: failCount,
    skip_count: skipCount,
    inconclusive_count: inconclusiveCount,
    authoritative_leaf_total: passCount + failCount + skipCount + inconclusiveCount,
    assert_count: 16,
    test_case_count: counts.total,
    test_case_count_source: "unity_test_runner_root.TestCaseCount",
    count_consistency: {
      authoritative_counts_available: true,
      leaf_sum_matches_total: true,
      assert_count_semantics: "not_a_test_count",
      test_case_count_available: true,
      test_case_count_matches_leaf_total: true,
    },
  };
  return {
    status: "completed",
    success: counts.failed === 0,
    artifact_schema_version: 3,
    terminal_counts_authoritative: true,
    terminal_count_source: "unity_test_runner",
    mode: "editmode",
    test_mode: "editmode",
    filter: `SGG.UnityMCP.Tests.PackageSmoke6000${line}`,
    test_filter: `SGG.UnityMCP.Tests.PackageSmoke6000${line}`,
    assembly: "SGG.UnityMCP.Editor.Tests",
    test_assembly: "SGG.UnityMCP.Editor.Tests",
    ...counts,
    ...raw,
    execution_result: { source: "unity_test_runner", available: true, ...counts, ...raw },
  };
}

function successfulClient() {
  const calls: Array<{ name: string; args: Record<string, unknown> }> = [];
  const pollByProject = new Map<string, number>();
  return {
    calls,
    close: vi.fn(async () => undefined),
    callTool: vi.fn(async (name: string, args: Record<string, unknown>) => {
      calls.push({ name, args });
      if (name === "sgg_unity_projects") return ok({ projects: [project("4"), project("5")] });
      if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
      const command = args.command;
      if (command === "compile.status") return ok({ ready: true });
      if (command === "test.run") {
        return ok({ status: "running", operation_id: `operation-${args.project}`, mode: "editmode" });
      }
      if (command === "test.run.status") {
        const key = String(args.project);
        pollByProject.set(key, (pollByProject.get(key) ?? 0) + 1);
        return ok(terminalResult(key.endsWith("5") ? "5" : "4"));
      }
      throw new Error(`unexpected call ${name}:${String(command)}`);
    }),
  };
}

describe("bounded Unity smoke configuration", () => {
  it("binds CLI evidence to the full current commit", () => {
    expect(currentCommit()).toMatch(/^[a-f0-9]{40}$/);
  });

  it("requires separate exact target contracts and the current package version", () => {
    const config = readSmokeConfig(environment({
      SGG_UNITY_SMOKE_PACKAGE_VERSION: "old",
      SGG_UNITY_SMOKE_6000_5_TEST_FILTER: "",
      SGG_UNITY_SMOKE_6000_4_UNITY_VERSION: "6000.4.11f1",
    }), VERSION);
    expect(config.errors).toContain(`SGG_UNITY_SMOKE_PACKAGE_VERSION must equal current package version ${VERSION}`);
    expect(config.targets[0].errors).toContain("SGG_UNITY_SMOKE_6000_4_UNITY_VERSION must equal the current exact baseline 6000.4.10f1");
    expect(config.targets[1].errors).toContain("SGG_UNITY_SMOKE_6000_5_TEST_FILTER is required");
  });

  it("requires reviewed assembly and exact cardinality before connecting", async () => {
    const env = environment({
      SGG_UNITY_SMOKE_6000_4_TEST_ASSEMBLY: "",
      SGG_UNITY_SMOKE_6000_5_EXPECTED_TEST_COUNT: "",
    });
    const config = readSmokeConfig(env, VERSION);
    expect(config.targets[0].errors).toContain("SGG_UNITY_SMOKE_6000_4_TEST_ASSEMBLY is required");
    expect(config.targets[1].errors).toContain("SGG_UNITY_SMOKE_6000_5_EXPECTED_TEST_COUNT is required");
    const factory = vi.fn();
    expect(await runUnitySmoke({
      env, packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      clientFactory: factory, writeEvidence: async () => undefined, log: () => undefined,
    })).toBe(1);
    expect(factory).not.toHaveBeenCalled();
  });

  it("rejects non-loopback endpoints and unbounded settings", () => {
    const config = readSmokeConfig(environment({
      SGG_UNITY_SMOKE_URL: "https://example.invalid/mcp",
      SGG_UNITY_SMOKE_TOTAL_TIMEOUT_MS: "99999999",
    }), VERSION);
    expect(config.errors.join(" ")).toContain("loopback URL");
    expect(config.errors.join(" ")).toContain("SGG_UNITY_SMOKE_TOTAL_TIMEOUT_MS");
  });

  it("requires the overall deadline to cover connection and both target budgets", () => {
    const config = readSmokeConfig(environment({
      SGG_UNITY_SMOKE_CALL_TIMEOUT_MS: "1000",
      SGG_UNITY_SMOKE_TEST_MAX_SECONDS: "1",
      SGG_UNITY_SMOKE_TOTAL_TIMEOUT_MS: "18999",
    }), VERSION);
    expect(config.errors).toContain("SGG_UNITY_SMOKE_TOTAL_TIMEOUT_MS must be at least 19000 for connection plus both configured target budgets");
    expect(readSmokeConfig(environment(), VERSION)).toMatchObject({ totalTimeoutMs: 1_800_000, targetBudgetMs: 840_000 });
  });

  it.each([
    "Namespace\\Fixture.Test",
    "Namespace/Fixture.Test",
    "\\\\server\\share\\Fixture.Test",
    "Namespace\\folder/Test",
  ])("rejects path-like test filter %s before connection", async (testFilter) => {
    const env = environment({ SGG_UNITY_SMOKE_6000_4_TEST_FILTER: testFilter });
    expect(readSmokeConfig(env, VERSION).targets[0].errors.join(" ")).toContain("without URLs, slashes, or backslashes");
    const factory = vi.fn();
    expect(await runUnitySmoke({
      env, packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      clientFactory: factory, writeEvidence: async () => undefined, log: () => undefined,
    })).toBe(1);
    expect(factory).not.toHaveBeenCalled();
  });
});

describe("exact ready target matching", () => {
  const target = readSmokeConfig(environment(), VERSION).targets[0];

  it("returns one exact ready version/package match", () => {
    expect(resolveExactReadyProject([project("4")], target, VERSION)).toMatchObject({ instance_id: "instance-4" });
  });

  it.each([
    [[project("4"), project("4", { instance_id: "other" })], "exactly one"],
    [[project("4", { stale: true })], "stale or not ready"],
    [[project("4", { unity_version: "6000.4.8f1" })], "Unity version"],
    [[project("4", { package_version: "old" })], "package version"],
  ])("rejects ambiguous, stale, or version-mismatched records", (projects, message) => {
    expect(() => resolveExactReadyProject(projects, target, VERSION)).toThrow(message);
  });
});

describe("test polling result classification", () => {
  it("keeps running states nonterminal", () => {
    expect(classifyTestStatus({ status: "running", total: 0 })).toEqual({ terminal: false });
  });

  it("requires authoritative nonzero all-pass counts", () => {
    expect(classifyTestStatus(terminalResult("4", { total: 0, passed: 0, failed: 0, skipped: 0 }))).toMatchObject({ terminal: true, pass: false });
    expect(classifyTestStatus(terminalResult("4", { total: 2, passed: 1, failed: 1, skipped: 0 }))).toMatchObject({ terminal: true, pass: false });
    expect(classifyTestStatus(terminalResult("4", { total: 2, passed: 1, failed: 0, skipped: 1 }))).toMatchObject({ terminal: true, pass: false });
    expect(classifyTestStatus(terminalResult())).toMatchObject({ terminal: true, pass: true });
    expect(classifyTestStatus({ ...terminalResult(), artifact_schema_version: 1 })).toMatchObject({ terminal: true, pass: false, reason: expect.stringContaining("schema-v3") });
    expect(classifyTestStatus({ ...terminalResult(), terminal_counts_authoritative: false })).toMatchObject({ terminal: true, pass: false, reason: expect.stringContaining("schema-v3") });
    expect(classifyTestStatus({ ...terminalResult(), terminal_count_source: "batchmode_xml" })).toMatchObject({ terminal: true, pass: false, reason: expect.stringContaining("schema-v3") });
    expect(classifyTestStatus({ ...terminalResult(), execution_result: { source: "legacy_artifact_v1", available: false } })).toMatchObject({ terminal: true, pass: false, reason: expect.stringContaining("schema-v3") });
    expect(classifyTestStatus({ ...terminalResult(), pass_count: 16 })).toMatchObject({ terminal: true, pass: false, reason: expect.stringContaining("schema-v3") });
  });

  it("rejects coordinated raw pass/fail shifts against collapsed counts", () => {
    const result = terminalResult();
    result.pass_count = 1;
    result.fail_count = 1;
    result.execution_result.pass_count = 1;
    result.execution_result.fail_count = 1;

    expect(classifyTestStatus(result)).toMatchObject({ terminal: true, pass: false, reason: expect.stringContaining("schema-v3") });
  });

  it("rejects top-level and execution_result raw evidence disagreement", () => {
    const result = terminalResult();
    result.execution_result.pass_count = 1;

    expect(classifyTestStatus(result)).toMatchObject({ terminal: true, pass: false, reason: expect.stringContaining("schema-v3") });
  });

  it("fails callback loss explicitly", () => {
    expect(classifyTestStatus({ status: "unknown_after_reload", success: false })).toMatchObject({ terminal: true, pass: false, reason: "test run ended in unknown_after_reload" });
    expect(classifyTestStatus({ status: "terminal_counts_unavailable", success: false })).toMatchObject({ terminal: true, pass: false, reason: "test run ended in terminal_counts_unavailable" });
  });
});

describe("smoke orchestration", () => {
  it("enforces configured known test cardinality", async () => {
    const client = successfulClient();
    let evidence = "";
    const status = await runUnitySmoke({
      env: environment({
        SGG_UNITY_SMOKE_6000_4_EXPECTED_TEST_COUNT: "76",
        SGG_UNITY_SMOKE_6000_5_EXPECTED_TEST_COUNT: "2",
      }),
      packageVersion: VERSION,
      commit: "8156df6",
      now: () => new Date("2026-07-11T12:00:00.000Z"),
      sleep: async () => undefined,
      clientFactory: async () => client,
      writeEvidence: async (_path: string, text: string) => { evidence = text; },
      log: () => undefined,
    });

    expect(status).toBe(1);
    expect(JSON.parse(evidence)).toMatchObject({
      unity_6000_4_smoke: { result: "fail", reason: "test run executed 2 tests; expected exactly 76" },
      unity_6000_5_smoke: { result: "pass" },
    });
  });

  it("uses the required call sequence for both targets and always closes", async () => {
    const client = successfulClient();
    let evidence = "";
    const status = await runUnitySmoke({
      env: environment(),
      packageVersion: VERSION,
      commit: "8156df6",
      now: () => new Date("2026-07-11T12:00:00.000Z"),
      sleep: async () => undefined,
      clientFactory: async () => client,
      writeEvidence: async (_path: string, text: string) => { evidence = text; },
      log: () => undefined,
    });
    expect(status).toBe(0);
    expect(client.close).toHaveBeenCalledOnce();
    expect(client.calls.map(({ name, args }) => `${name}:${String(args.selector ?? args.command ?? "projects")}`)).toEqual([
      "sgg_unity_projects:projects",
      "sgg_unity_describe:commands",
      "sgg_unity_describe:command:compile.status",
      "sgg_unity_call:compile.status",
      "sgg_unity_describe:command:test.run",
      "sgg_unity_describe:command:test.run.status",
      "sgg_unity_call:test.run",
      "sgg_unity_call:test.run.status",
      "sgg_unity_projects:projects",
      "sgg_unity_describe:commands",
      "sgg_unity_describe:command:compile.status",
      "sgg_unity_call:compile.status",
      "sgg_unity_describe:command:test.run",
      "sgg_unity_describe:command:test.run.status",
      "sgg_unity_call:test.run",
      "sgg_unity_call:test.run.status",
    ]);
    expect(JSON.parse(evidence)).toMatchObject({ unity_6000_4_smoke: { result: "pass" }, unity_6000_5_smoke: { result: "pass" }, full_release_matrix: { result: "not_run" } });
    expect(evidence).not.toContain("top-secret-token");
    expect(evidence).not.toContain("127.0.0.1");
    const testRuns = client.calls.filter(({ args }) => args.command === "test.run");
    expect(testRuns.every(({ args }) => JSON.parse(String(args.args_json)).mode === "editmode")).toBe(true);
    expect(testRuns.every(({ args }) => JSON.parse(String(args.args_json)).assembly === "SGG.UnityMCP.Editor.Tests")).toBe(true);
  });

  it("records both target outcomes on isolated precondition failure", async () => {
    const client = successfulClient();
    client.callTool.mockImplementationOnce(async () => ok({ projects: [project("4", { status: "reloading" }), project("5")] }));
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6",
      now: () => new Date("2026-07-11T12:00:00.000Z"), sleep: async () => undefined,
      clientFactory: async () => client, writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(JSON.parse(evidence)).toMatchObject({ unity_6000_4_smoke: { result: "fail" }, unity_6000_5_smoke: { result: "pass" } });
    expect(client.close).toHaveBeenCalledOnce();
  });

  it("fails timed-out polling and makes no further test-run write", async () => {
    const client = successfulClient();
    client.callTool.mockImplementation(async (name: string, args: Record<string, unknown>) => {
      client.calls.push({ name, args });
      if (name === "sgg_unity_projects") return ok({ projects: [project("4"), project("5")] });
      if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
      if (args.command === "compile.status") return ok({ ready: true });
      if (args.command === "test.run") return ok({ status: "running", operation_id: "operation-4", mode: "editmode" });
      return ok({ status: "running", operation_id: "operation-4" });
    });
    let time = Date.parse("2026-07-11T12:00:00.000Z");
    let evidence = "";
    const env = environment({
      SGG_UNITY_SMOKE_CALL_TIMEOUT_MS: "1000",
      SGG_UNITY_SMOKE_TEST_MAX_SECONDS: "1",
      SGG_UNITY_SMOKE_TOTAL_TIMEOUT_MS: "19000",
      SGG_UNITY_SMOKE_POLL_INTERVAL_MS: "30000",
    });
    expect(await runUnitySmoke({
      env, packageVersion: VERSION, commit: "8156df6", now: () => new Date(time),
      sleep: async (ms: number) => { time += ms; }, clientFactory: async () => client,
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(JSON.parse(evidence)).toMatchObject({ unity_6000_4_smoke: { result: "fail" }, unity_6000_5_smoke: { result: "not_run" } });
    expect(client.calls.filter(({ args }) => args.command === "test.run")).toHaveLength(1);
    expect(client.close).toHaveBeenCalledOnce();
  });

  it("suppresses the second write when status is lost after a run starts", async () => {
    const client = successfulClient();
    let statusCalls = 0;
    client.callTool.mockImplementation(async (name: string, args: Record<string, unknown>) => {
      client.calls.push({ name, args });
      if (name === "sgg_unity_projects") return ok({ projects: [project("4"), project("5")] });
      if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
      if (args.command === "compile.status") return ok({ ready: true });
      if (args.command === "test.run") return ok({ status: "running", operation_id: "operation-4", mode: "editmode" });
      statusCalls += 1;
      if (statusCalls === 1) return ok({ status: "running", total: 1, passed: 0, failed: 0, skipped: 0 });
      throw new Error("status transport failed");
    });
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6",
      now: () => new Date("2026-07-11T12:00:00.000Z"), sleep: async () => undefined,
      clientFactory: async () => client, writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(JSON.parse(evidence)).toMatchObject({ unity_6000_4_smoke: { result: "fail" }, unity_6000_5_smoke: { result: "not_run" } });
    expect(client.calls.filter(({ args }) => args.command === "test.run")).toHaveLength(1);
    expect(client.close).toHaveBeenCalledOnce();
    expect(JSON.parse(evidence).unity_6000_4_smoke).toMatchObject({
      project_id: "smoke-4",
      unsafe_active_run: true,
      unsafe_active_reason: expect.stringContaining("may still be active"),
      counts: null,
      commands: expect.arrayContaining([
        { command: "test.run", result: "pass" },
        { command: "test.run.status", result: "pass" },
        { command: "test.run.status", result: "fail" },
      ]),
    });
  });

  it.each(["describe:commands", "compile.status"])("retains resolved identity and command history when %s fails", async (failedLabel) => {
    const client = successfulClient();
    client.callTool.mockImplementation(async (name: string, args: Record<string, unknown>) => {
      client.calls.push({ name, args });
      if (name === "sgg_unity_projects") return ok({ projects: [project("4"), project("5")] });
      const label = name === "sgg_unity_describe" ? `describe:${String(args.selector)}` : String(args.command);
      if (label === failedLabel) throw new Error(`${failedLabel} failed`);
      if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
      if (args.command === "compile.status") return ok({ ready: true });
      if (args.command === "test.run") return ok({ operation_id: "operation-4", ...terminalResult(String(args.project).endsWith("5") ? "5" : "4") });
      throw new Error("unexpected status");
    });
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6",
      now: () => new Date("2026-07-11T12:00:00.000Z"), sleep: async () => undefined,
      clientFactory: async () => client, writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    const first = JSON.parse(evidence).unity_6000_4_smoke;
    expect(first).toMatchObject({ project_id: "smoke-4", result: "fail", unsafe_active_run: false });
    expect(first.commands.at(-1)).toEqual({ command: failedLabel, result: "fail" });
    expect(first.commands[0]).toEqual({ command: "sgg_unity_projects", result: "pass" });
  });

  it("refreshes discovery for the second target and rejects its newly stale heartbeat", async () => {
    const client = successfulClient();
    let discoveryCount = 0;
    client.callTool.mockImplementation(async (name: string, args: Record<string, unknown>) => {
      client.calls.push({ name, args });
      if (name === "sgg_unity_projects") {
        discoveryCount += 1;
        return ok({ projects: [project("4"), project("5", discoveryCount === 2 ? { stale: true } : {})] });
      }
      if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
      if (args.command === "compile.status") return ok({ ready: true });
      if (args.command === "test.run") return ok({ operation_id: "operation-4", ...terminalResult(String(args.project).endsWith("5") ? "5" : "4") });
      throw new Error("unexpected status");
    });
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      sleep: async () => undefined, clientFactory: async () => client,
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(discoveryCount).toBe(2);
    expect(JSON.parse(evidence)).toMatchObject({
      unity_6000_4_smoke: { result: "pass" },
      unity_6000_5_smoke: { result: "fail", commands: [{ command: "sgg_unity_projects", result: "pass" }] },
    });
  });

  it("records both target failures and writes evidence when connection times out", async () => {
    let evidence = "";
    const timers = { setTimeout: (callback: () => void) => { callback(); return 1; }, clearTimeout: vi.fn() };
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      clientFactory: async () => await new Promise(() => {}), timers,
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(JSON.parse(evidence)).toMatchObject({
      cleanup: { result: "fail", reason: expect.stringContaining("cleanup ownership") },
      unity_6000_4_smoke: { result: "fail", reason: expect.stringContaining("connection") },
      unity_6000_5_smoke: { result: "fail", reason: expect.stringContaining("connection") },
    });
  });

  it("attempts close but bounds it and still writes failing cleanup evidence", async () => {
    const client = successfulClient();
    client.close.mockImplementation(async () => await new Promise(() => {}));
    let evidence = "";
    let timeoutCalls = 0;
    const timers = {
      setTimeout: (callback: () => void) => {
        timeoutCalls += 1;
        if (timeoutCalls === 1) return setTimeout(callback, 60_000);
        callback();
        return 2;
      },
      clearTimeout,
    };
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      sleep: async () => undefined, clientFactory: async () => client, timers,
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(client.close).toHaveBeenCalledOnce();
    expect(JSON.parse(evidence)).toMatchObject({ cleanup: { result: "fail", reason: "MCP cleanup orchestration timed out" } });
  });

  it.each([
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
  ])(
    "treats definite pre-execution test.run %s as not started and allows the second target",
    async (code) => {
      const client = successfulClient();
      client.callTool.mockImplementation(async (name: string, args: Record<string, unknown>) => {
        client.calls.push({ name, args });
        if (name === "sgg_unity_projects") return ok({ projects: [project("4"), project("5")] });
        if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
        if (args.command === "compile.status") return ok({ ready: true });
        if (args.command === "test.run" && args.project === "instance-4") return gatewayError(code);
        if (args.command === "test.run") return ok({ operation_id: "operation-5", ...terminalResult("5") });
        throw new Error("unexpected status");
      });
      let evidence = "";
      expect(await runUnitySmoke({
        env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
        sleep: async () => undefined, clientFactory: async () => client,
        writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
      })).toBe(1);
      expect(JSON.parse(evidence)).toMatchObject({
        unity_6000_4_smoke: { result: "fail", unsafe_active_run: false, reason: expect.stringContaining(code) },
        unity_6000_5_smoke: { result: "pass" },
      });
      expect(client.calls.filter(({ args }) => args.command === "test.run")).toHaveLength(2);
    },
  );

  it.each([
    "bridge_timeout",
    "command_timeout",
    "bridge_protocol_error",
    "bridge_unreachable",
    "unity_exception",
    "internal_error",
    "unexpected_code",
  ])("treats structured but non-definite test.run %s as uncertain and suppresses the second write", async (code) => {
    const client = successfulClient();
    client.callTool.mockImplementation(async (name: string, args: Record<string, unknown>) => {
      client.calls.push({ name, args });
      if (name === "sgg_unity_projects") return ok({ projects: [project("4"), project("5")] });
      if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
      if (args.command === "compile.status") return ok({ ready: true });
      if (args.command === "test.run") return gatewayError(code);
      throw new Error("unexpected call");
    });
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      sleep: async () => undefined, clientFactory: async () => client,
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(JSON.parse(evidence)).toMatchObject({
      unity_6000_4_smoke: { result: "fail", unsafe_active_run: true, reason: expect.stringContaining(code) },
      unity_6000_5_smoke: { result: "not_run" },
    });
    expect(client.calls.filter(({ args }) => args.command === "test.run")).toHaveLength(1);
  });

  it("treats an unstructured test.run delivery error as uncertain and suppresses the second write", async () => {
    const client = successfulClient();
    client.callTool.mockImplementation(async (name: string, args: Record<string, unknown>) => {
      client.calls.push({ name, args });
      if (name === "sgg_unity_projects") return ok({ projects: [project("4"), project("5")] });
      if (name === "sgg_unity_describe") return ok(descriptor(String(args.selector)));
      if (args.command === "compile.status") return ok({ ready: true });
      if (args.command === "test.run") throw new Error("delivery failed");
      throw new Error("unexpected call");
    });
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      sleep: async () => undefined, clientFactory: async () => client,
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(JSON.parse(evidence)).toMatchObject({
      unity_6000_4_smoke: { result: "fail", unsafe_active_run: true },
      unity_6000_5_smoke: { result: "not_run" },
    });
    expect(client.calls.filter(({ args }) => args.command === "test.run")).toHaveLength(1);
  });

  it("fails configuration before connecting but still writes both records", async () => {
    const factory = vi.fn();
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment({ SGG_UNITY_SMOKE_6000_4_SELECTOR: "" }), packageVersion: VERSION, commit: "8156df6",
      now: () => new Date("2026-07-11T12:00:00.000Z"), clientFactory: factory,
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(factory).not.toHaveBeenCalled();
    expect(JSON.parse(evidence)).toMatchObject({ unity_6000_4_smoke: { result: "fail" }, unity_6000_5_smoke: { result: "not_run" } });
  });
});

describe("official client cleanup", () => {
  function officialFixture(options: { terminate?: () => Promise<void>; close?: () => Promise<void>; connect?: () => Promise<void> } = {}) {
    const events: string[] = [];
    const transport = {
      sessionId: "stateful-session",
      terminateSession: vi.fn(async () => {
        events.push("terminate");
        await options.terminate?.();
      }),
    };
    const client = {
      connect: vi.fn(async () => {
        events.push("connect");
        await options.connect?.();
      }),
      callTool: vi.fn(),
      close: vi.fn(async () => {
        events.push("close");
        await options.close?.();
      }),
    };
    class Transport {
      constructor() { return transport; }
    }
    class Client {
      constructor() { return client; }
    }
    return { events, transport, client, dependencies: { Transport, Client } };
  }

  it("terminates a stateful session before closing the client", async () => {
    const fixture = officialFixture();
    const official = await createOfficialClient(readSmokeConfig(environment(), VERSION), fixture.dependencies);
    await official.close();
    expect(fixture.events).toEqual(["connect", "terminate", "close"]);
    expect(fixture.client.connect).toHaveBeenCalledWith(fixture.transport, { timeout: 30_000, maxTotalTimeout: 30_000 });
  });

  it("attempts close and rejects cleanup after session termination rejects", async () => {
    const fixture = officialFixture({ terminate: async () => { throw new Error("terminate rejected"); } });
    const official = await createOfficialClient(readSmokeConfig(environment(), VERSION), fixture.dependencies);
    await expect(official.close()).rejects.toThrow("session termination failed: terminate rejected");
    expect(fixture.events).toEqual(["connect", "terminate", "close"]);
  });

  it("propagates client close rejection", async () => {
    const fixture = officialFixture({ close: async () => { throw new Error("close rejected"); } });
    const official = await createOfficialClient(readSmokeConfig(environment(), VERSION), fixture.dependencies);
    await expect(official.close()).rejects.toThrow("client close failed: close rejected");
    expect(fixture.events).toEqual(["connect", "terminate", "close"]);
  });

  it("bounds hanging termination and still attempts close", async () => {
    const fixture = officialFixture({ terminate: async () => await new Promise(() => {}) });
    const timers = { setTimeout: (callback: () => void) => { callback(); return 1; }, clearTimeout: vi.fn() };
    const official = await createOfficialClient(readSmokeConfig(environment(), VERSION), { ...fixture.dependencies, timers });
    await expect(official.close()).rejects.toThrow("session termination failed: MCP session termination timed out");
    expect(fixture.events).toEqual(["connect", "terminate", "close"]);
  });

  it("records successful cleanup after an official connect rejection", async () => {
    const fixture = officialFixture({ connect: async () => { throw new Error("connect rejected"); } });
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      clientFactory: async (config: unknown) => createOfficialClient(config, fixture.dependencies),
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(fixture.events).toEqual(["connect", "terminate", "close"]);
    expect(JSON.parse(evidence)).toMatchObject({ cleanup: { result: "pass" }, unity_6000_4_smoke: { result: "fail" }, unity_6000_5_smoke: { result: "fail" } });
  });

  it("records failed cleanup after official connect and close rejection without leaking secrets", async () => {
    const fixture = officialFixture({
      connect: async () => { throw new Error("connect rejected"); },
      close: async () => { throw new Error("top-secret-token http://127.0.0.1:18444/mcp close rejected"); },
    });
    let evidence = "";
    expect(await runUnitySmoke({
      env: environment(), packageVersion: VERSION, commit: "8156df6", now: () => new Date("2026-07-11T12:00:00.000Z"),
      clientFactory: async (config: unknown) => createOfficialClient(config, fixture.dependencies),
      writeEvidence: async (_path: string, text: string) => { evidence = text; }, log: () => undefined,
    })).toBe(1);
    expect(JSON.parse(evidence).cleanup).toMatchObject({ result: "fail", reason: expect.stringContaining("[redacted]") });
    expect(evidence).not.toContain("top-secret-token");
    expect(evidence).not.toContain("127.0.0.1");
  });
});

describe("evidence contract", () => {
  it("is deterministic, bounded, separated, and rejects paths/URLs", () => {
    const target = { target_line: "6000.4", result: "not_run", reason: "fixture" };
    const evidence = createEvidence({ dateUtc: "2026-07-11T12:00:00.000Z", commit: "8156df6", packageVersion: VERSION, targets: [target, { ...target, target_line: "6000.5" }] });
    expect(serializeEvidence(evidence)).toBe(serializeEvidence(evidence));
    expect(serializeEvidence(evidence).length).toBeLessThan(64_000);
    expect(evidence).toMatchObject({ latest_static: { result: "not_run" }, full_release_matrix: { result: "not_run" } });
    expect(() => serializeEvidence({ ...evidence, leaked: "C:\\Users\\secret\\project" })).toThrow("absolute path");
    expect(() => serializeEvidence({ ...evidence, leaked: "http://localhost:1234/mcp" })).toThrow("URL");
  });
});
