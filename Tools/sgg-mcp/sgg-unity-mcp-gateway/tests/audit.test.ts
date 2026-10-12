import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawn } from "node:child_process";

import { afterEach, describe, expect, it } from "vitest";

import { FileAuditSink, InMemoryAuditSink, createAuditEvent, createAuditStartEvent, hashRedactedArgs, redactSecrets } from "../src/observability/audit.js";
import { dispatchTool } from "../src/mcp/dispatcher.js";
import { PolicyManager } from "../src/policy/policy-manager.js";

const tempDirs: string[] = [];
const auditChildFixture = path.join(process.cwd(), "tests", "fixtures", "audit-child.ts");
const storageChildFixture = path.join(process.cwd(), "tests", "fixtures", "storage-child.ts");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("audit events", () => {
  it("records stable fields for dispatcher calls", async () => {
    const audit = new InMemoryAuditSink();
    const now = () => new Date("2026-05-12T10:00:00.000Z");
    const rawArgs = { project: "game", command: "compile.status", args_json: "{}", request_id: "req-1" };

    await dispatchTool("sgg_unity_call", rawArgs, { audit, now, runId: "run-1" });

    expect(audit.starts).toMatchObject([{ phase: "start", tool: "sgg_unity_call", audit_id: expect.any(String) }]);
    expect(audit.events).toEqual([
      {
        schema_version: 1,
        phase: "terminal",
        audit_id: expect.stringMatching(/^[0-9a-f-]{36}$/),
        timestamp: "2026-05-12T10:00:00.000Z",
        run_id: "run-1",
        tool: "sgg_unity_call",
        status: "error",
        duration_ms: 0,
        args_hash: hashRedactedArgs(rawArgs),
        request_id: "req-1",
        project: "game",
        command: "compile.status",
        policy_state: "unconfigured",
        effective_policy_revision: null,
        policy_loaded_at: null,
        policy_last_attempt_at: null,
        policy_degraded: false,
        policy_error_kind: null,
        policy_error_code: null,
        policy_degraded_since: null,
        statistics_schema_version: 1,
        statistics_operation: "compile.status",
        statistics_operation_kind: "command",
        statistics_internal: false,
        statistics_request_bytes: expect.any(Number),
        statistics_command_argument_bytes: 2,
        statistics_full_response_bytes: expect.any(Number),
        statistics_compact_response_bytes: expect.any(Number),
        statistics_delivered_response_bytes: expect.any(Number),
        statistics_response_mode: "full",
        statistics_dispatched: false,
        statistics_failure_stage: "routing",
        statistics_failure_summary: "The project, command, catalog, or operation could not be resolved.",
        output_chars: expect.any(Number),
        error_code: "project_not_found",
      },
    ]);
  });

  it("records safe degraded policy status for top-level non-command calls", async () => {
    const dir = makeTempDir();
    const policyPath = path.join(dir, "private-policy.json");
    fs.writeFileSync(policyPath, JSON.stringify({ schema_version: 1, defaults: { read: "allow", write: "ask", execute: "deny", destructive: "ask", network: "deny" }, projects: {} }));
    const manager = new PolicyManager(policyPath, { now: () => new Date("2026-07-11T10:00:00.000Z") });
    fs.rmSync(policyPath);
    const audit = new InMemoryAuditSink();

    await dispatchTool("sgg_unity_projects", {}, { audit, policyManager: manager, now: () => new Date("2026-07-11T10:01:00.000Z") });

    expect(audit.events[0]).toMatchObject({ tool: "sgg_unity_projects", policy_state: "degraded", policy_degraded: true, policy_error_code: "policy_reload_failed", policy_error_kind: "missing", effective_policy_revision: expect.stringMatching(/^sha256:/) });
    expect(JSON.stringify(audit.events[0])).not.toContain(policyPath);
  });

  it("uses result fields before raw argument fields", () => {
    const event = createAuditEvent(
      "sgg_unity_call",
      { project: "raw", command: "raw.command" },
      {
        status: "error",
        project: "result",
        command: "result.command",
        error: { code: "permission_denied", message: "denied" },
        diagnostics: [],
      },
      Date.parse("2026-05-12T09:59:59.000Z"),
      () => new Date("2026-05-12T10:00:00.000Z"),
      {},
      "run-1",
    );

    expect(event).toMatchObject({ run_id: "run-1", project: "result", command: "result.command", duration_ms: 1000 });
  });

  it("records optional command runtime metadata", () => {
    const event = createAuditEvent(
      "sgg_unity_call",
      { project: "game", command: "asset.import" },
      {
        status: "ok",
        project: "game",
        command: "asset.import",
        data: {},
        diagnostics: [],
      },
      Date.parse("2026-05-12T09:59:59.000Z"),
      () => new Date("2026-05-12T10:00:00.000Z"),
      { risk: "write", idempotency: "idempotent", timeout_ms: 1000, command_revision: "sha256:revision" },
    );

    expect(event).toMatchObject({ command_risk: "write", idempotency: "idempotent", timeout_ms: 1000, command_revision: "sha256:revision" });
  });

  it("records optional extension governance metadata", () => {
    const event = createAuditEvent(
      "sgg_unity_call",
      { project: "game", command: "asset.search" },
      {
        status: "ok",
        project: "game",
        command: "asset.search",
        data: {},
        diagnostics: [],
      },
      Date.parse("2026-05-12T10:00:00.000Z"),
      () => new Date("2026-05-12T10:00:00.000Z"),
      {
        risk: "read",
        idempotency: "safe",
        timeout_ms: 1000,
        source: "manifest",
        package_id: "com.example.tools",
        package_version: "1.0.0",
        manifest_path: "Packages/com.example.tools/mcp.commands.json",
        trust: "untrusted_package",
        commands_hash: "abc123",
      },
    );

    expect(event).toMatchObject({
      command_source: "manifest",
      package_id: "com.example.tools",
      package_version: "1.0.0",
      manifest_path: "Packages/com.example.tools/mcp.commands.json",
      trust: "untrusted_package",
      commands_hash: "abc123",
    });
  });

  it("records args hashes without raw args", () => {
    const rawArgs = { prompt: "do not log", token: "secret", nested: { b: 2, a: 1 } };
    const event = createAuditEvent(
      "sgg_unity_projects",
      rawArgs,
      { status: "ok", data: {}, diagnostics: [] },
      Date.parse("2026-05-12T10:00:00.000Z"),
      () => new Date("2026-05-12T10:00:00.000Z"),
    );

    expect(event.args_hash).toBe(`sha256:${createHash("sha256").update('{"nested":{"a":1,"b":2},"prompt":"do not log","token":"[REDACTED]"}').digest("hex")}`);
    expect(JSON.stringify(event)).not.toContain("do not log");
    expect(JSON.stringify(event)).not.toContain("secret");
  });

  it("records stable correlation ids without raw args_json payloads", () => {
    const rawArgs = {
      project: "game",
      command: "asset.import",
      args_json: JSON.stringify({ asset_path: "Assets/Foo.prefab", secret_note: "do-not-log" }),
      checkpoint_id: "checkpoint-1",
      plan_id: "plan-1",
    };
    const event = createAuditEvent(
      "sgg_unity_call",
      rawArgs,
      { status: "ok", project: "game", command: "asset.import", data: {}, diagnostics: [] },
      Date.parse("2026-05-12T10:00:00.000Z"),
      () => new Date("2026-05-12T10:00:00.000Z"),
    );

    expect(event).toMatchObject({ checkpoint_id: "checkpoint-1", plan_id: "plan-1" });
    expect(JSON.stringify(event)).not.toContain("Assets/Foo.prefab");
    expect(JSON.stringify(event)).not.toContain("do-not-log");
  });

  it("records policy, bridge duration and output hints", () => {
    const event = createAuditEvent(
      "sgg_unity_call",
      { project: "game", command: "asset.search", args_json: "{}" },
      {
        status: "ok",
        project: "game",
        command: "asset.search",
        data: { matches: [], artifact_refs: ["Library/SGGUnityMcp/job-artifacts/search/result.json"], truncated: true },
        diagnostics: [],
        artifact_refs: ["Library/SGGUnityMcp/job-artifacts/search/result.json"],
        truncated: true,
      },
      Date.parse("2026-05-12T09:59:59.000Z"),
      () => new Date("2026-05-12T10:00:00.000Z"),
      {
        command: { risk: "read", idempotency: "safe", timeout_ms: 1000 },
        policy: { action: "allow", reason: "project_rule", pattern: "asset.*" },
        bridge_duration_ms: 12,
      },
      "run-1",
    );

    expect(event).toMatchObject({
      command_risk: "read",
      policy_action: "allow",
      policy_reason: "project_rule",
      policy_pattern: "asset.*",
      bridge_duration_ms: 12,
      output_chars: expect.any(Number),
      truncated: true,
      artifact_refs: ["Library/SGGUnityMcp/job-artifacts/search/result.json"],
    });
  });

  it("writes redacted NDJSON audit events to a stable file", () => {
    const dir = makeTempDir();
    const sink = new FileAuditSink(path.join(dir, "audit"));

    const event = {
      schema_version: 1,
      phase: "terminal",
      audit_id: "audit-1",
      timestamp: "2026-05-12T10:00:00.000Z",
      run_id: "run-1",
      tool: "sgg_unity_projects",
      status: "ok",
      duration_ms: 0,
      args_hash: "hash",
      token: "secret-token",
      output_chars: 2,
    } as const;
    sink.write(event);

    const lines = fs.readFileSync(path.join(dir, "audit", "audit.ndjson"), "utf8").trim().split("\n");
    expect(lines).toHaveLength(1);
    expect(JSON.parse(lines[0] ?? "{}")).toEqual({
      schema_version: 1,
      phase: "terminal",
      audit_id: "audit-1",
      timestamp: "2026-05-12T10:00:00.000Z",
      run_id: "run-1",
      tool: "sgg_unity_projects",
      status: "ok",
      duration_ms: 0,
      args_hash: "hash",
      token: "[REDACTED]",
      output_chars: 2,
    });
  });

  it("serializes writers and enforces size, UTC-day, file-count, and byte retention", () => {
    const dir = path.join(makeTempDir(), "audit");
    let clock = new Date("2026-07-10T23:59:00.000Z");
    const options = { maxFileBytes: 128 * 1024, maxFiles: 2, maxAgeMs: 86_400_000, maxTotalBytes: 256 * 1024, now: () => clock };
    const first = new FileAuditSink(dir, options);
    const second = new FileAuditSink(dir, options);
    const event = createAuditEvent("sgg_unity_projects", {}, { status: "ok", data: {}, diagnostics: [] }, clock.getTime(), () => clock);

    first.write(event);
    second.write({ ...event, run_id: "second" });
    clock = new Date("2026-07-11T00:01:00.000Z");
    first.write({ ...event, timestamp: clock.toISOString(), run_id: "third" });

    const status = first.status();
    expect(status).toMatchObject({ schema_version: 1, state: "ok", files: 2, rotation_period: "utc_day" });
    expect(status.total_bytes).toBeLessThanOrEqual(256 * 1024);
    const lines = fs.readdirSync(dir).filter((name) => name.endsWith(".ndjson")).flatMap((name) => fs.readFileSync(path.join(dir, name), "utf8").trim().split("\n"));
    expect(lines.map((line) => JSON.parse(line).run_id)).toEqual(expect.arrayContaining(["second", "third"]));
  });

  it("rejects linked audit roots and active files without following them", () => {
    const parent = makeTempDir();
    const target = path.join(parent, "target");
    const linkedRoot = path.join(parent, "linked-root");
    fs.mkdirSync(target);
    fs.symlinkSync(target, linkedRoot, "dir");
    expect(() => new FileAuditSink(linkedRoot).prepare(auditStart())).toThrow();

    const root = path.join(parent, "audit");
    fs.mkdirSync(root);
    const outside = path.join(parent, "outside.ndjson");
    fs.writeFileSync(outside, "outside\n");
    fs.symlinkSync(outside, path.join(root, "audit.ndjson"));
    expect(() => new FileAuditSink(root).prepare(auditStart())).toThrow();
    expect(fs.readFileSync(outside, "utf8")).toBe("outside\n");
  });

  it("truncates an incomplete NDJSON tail before the next durable append", () => {
    const root = path.join(makeTempDir(), "audit");
    const sink = new FileAuditSink(root, { now: () => new Date(0) });
    const event = createAuditEvent("sgg_unity_projects", {}, { status: "ok", data: {}, diagnostics: [] }, 0, () => new Date(0));
    sink.write(event);
    fs.appendFileSync(path.join(root, "audit.ndjson"), '{"partial":');

    sink.prepare(auditStart());
    sink.write({ ...event, run_id: "after-crash" });

    const lines = fs.readFileSync(path.join(root, "audit.ndjson"), "utf8").trim().split("\n");
    expect(lines).toHaveLength(3);
    expect(lines.map((line) => JSON.parse(line))).toMatchObject([
      { phase: "terminal", run_id: "default" },
      { phase: "start", run_id: "run-test" },
      { phase: "terminal", run_id: "after-crash" },
    ]);
  });

  it("serializes actual child-process appends and size rotation", async () => {
    const root = path.join(makeTempDir(), "audit");
    const timestamp = new Date().toISOString();
    const ids = Array.from({ length: 10 }, (_, index) => `audit-child-${index}`);
    const results = await Promise.all(ids.map((id) => runAuditChild(root, timestamp, id)));
    expect(results).toEqual(Array(10).fill(0));

    const lines = fs.readdirSync(root).filter((name) => name.endsWith(".ndjson")).flatMap((name) => fs.readFileSync(path.join(root, name), "utf8").trim().split("\n"));
    expect(lines).toHaveLength(10);
    expect(lines.map((line) => JSON.parse(line).run_id).sort()).toEqual(ids.sort());
    expect(fs.readdirSync(root).filter((name) => name.startsWith("audit-") && name.endsWith(".ndjson")).length).toBeGreaterThan(0);
  });

  it("recovers a crashed durable deletion of the active audit file", async () => {
    const root = path.join(makeTempDir(), "audit");
    const sink = new FileAuditSink(root, { now: () => new Date(0) });
    sink.write(createAuditEvent("sgg_unity_projects", {}, { status: "ok", data: {}, diagnostics: [] }, 0, () => new Date(0)));
    const auditPath = path.join(root, "audit.ndjson");
    expect(await runStorageDeleteCrash(root, auditPath)).toBe(0);
    const metadataName = fs.readdirSync(root).find((name) => name.endsWith(".json") && name.startsWith(".delete-v1-"))!;
    expect(fs.readFileSync(path.join(root, metadataName), "utf8")).not.toContain("audit.ndjson");

    sink.prepare(auditStart());

    expect(fs.readdirSync(root).filter((name) => name.startsWith(".delete-v1-"))).toEqual([]);
    expect(fs.readFileSync(auditPath, "utf8").trim().split("\n").map((line) => JSON.parse(line).phase)).toEqual(["start"]);
  });

  it("recovers delete-metadata publication crashes without losing the active audit", async () => {
    for (const mode of ["delete-metadata-temp-crash", "delete-metadata-link-crash"]) {
      const root = path.join(makeTempDir(), "audit");
      const sink = new FileAuditSink(root, { now: () => new Date(0) });
      sink.write(createAuditEvent("sgg_unity_projects", {}, { status: "ok", data: {}, diagnostics: [] }, 0, () => new Date(0)));
      const auditPath = path.join(root, "audit.ndjson");
      expect(await runStorageChild(mode, root, auditPath)).toBe(0);

      sink.prepare(auditStart());

      expect(fs.existsSync(auditPath)).toBe(true);
      expect(fs.readdirSync(root).filter((name) => name.startsWith(".publish-v1-") || name.startsWith(".delete-v1-"))).toEqual([]);
    }
  });

  it("preserves audit history while recovering a complete dead transition gate", async () => {
    const root = path.join(makeTempDir(), "audit");
    const sink = new FileAuditSink(root, { now: () => new Date(0) });
    const event = createAuditEvent("sgg_unity_projects", {}, { status: "ok", data: {}, diagnostics: [] }, 0, () => new Date(0));
    sink.write(event);
    const gate = path.join(root, ".audit.lock.transition-gate");
    fs.mkdirSync(gate, { mode: 0o700 });
    expect(await runStorageChild("lock-owner-write", root, path.join(gate, "owner.json"))).toBe(0);

    sink.write({ ...event, run_id: "after-dead-gate" });

    const lines = fs.readFileSync(path.join(root, "audit.ndjson"), "utf8").trim().split("\n").map((line) => JSON.parse(line));
    expect(lines.map((line) => line.run_id)).toEqual(["default", "after-dead-gate"]);
    expect(fs.readdirSync(root).filter((name) => name.includes(".audit.lock"))).toEqual([]);
  });

  it("redacts token-like keys recursively", () => {
    expect(
      redactSecrets({
        token: "abc",
        nested: { authorization: "Bearer abc", ok: "value" },
        list: [{ private_key: "key" }],
      }),
    ).toEqual({
      token: "[REDACTED]",
      nested: { authorization: "[REDACTED]", ok: "value" },
      list: [{ private_key: "[REDACTED]" }],
    });
  });
});

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-audit-"));
  tempDirs.push(dir);
  return dir;
}

function runAuditChild(root: string, timestamp: string, runId: string): Promise<number | null> {
  return new Promise((resolve, reject) => {
    const child = spawn(process.execPath, ["--import", "tsx", auditChildFixture, root, timestamp, runId], { stdio: ["ignore", "ignore", "pipe"] });
    let stderr = "";
    child.stderr.on("data", (data) => { stderr += String(data); });
    child.once("error", reject);
    child.once("exit", (code) => stderr ? reject(new Error(stderr)) : resolve(code));
  });
}
function auditStart() { return createAuditStartEvent("sgg_unity_projects", {}, () => new Date(0), "run-test", "audit-test"); }
function runStorageDeleteCrash(root: string, file: string): Promise<number | null> { return runStorageChild("delete-crash", root, file); }
function runStorageChild(mode: string, root: string, file: string): Promise<number | null> { return new Promise((resolve, reject) => { const child = spawn(process.execPath, ["--import", "tsx", storageChildFixture, mode, root, file], { stdio: ["ignore", "ignore", "pipe"] }); let stderr = ""; child.stderr.on("data", (data) => { stderr += String(data); }); child.once("error", reject); child.once("exit", (code) => stderr ? reject(new Error(stderr)) : resolve(code)); }); }
