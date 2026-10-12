import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { ApprovalBindingService, createApprovalBindingEnvelope } from "../src/approvals/approval-binding.js";
import { FakeUnityBridgeClient } from "../src/bridge/bridge-client.js";
import type { GatewayConfig, PolicyConfig } from "../src/config/config.js";
import { createUnityWindowDiagnosticsRuntime, inspectUnityWindowDiagnostics } from "../src/diagnostics/unity-window-diagnostics.js";
import { createGatewayContext } from "../src/gateway/context.js";
import { dispatchTool } from "../src/mcp/dispatcher.js";
import { FileAuditSink, InMemoryAuditSink } from "../src/observability/audit.js";
import { TestRunOperationStore, testRunOperationStoreDir } from "../src/operations/test-run-operation-store.js";
import type { HeartbeatRecord } from "../src/registry/project-registry.js";
import { inlineTestRunOperationPublisher } from "./helpers/test-run-operation-publisher.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-12T10:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("createGatewayContext", () => {
  it.each([
    undefined,
    { max_bytes: 8388608, max_bindings: 4096, retention_days: 30 },
    { max_bytes: 1048576, max_bindings: 1000000, retention_days: 45 },
  ])("captures identical effective storage limits for reads and worker publication: %j", async storage => {
    const settings = { ...config(makeTempDir()), ...(storage ? { test_runs: { storage } } : {}) };
    const context = createGatewayContext(settings);
    const expected = { maxBindingBytes: 16384, maxBytes: storage?.max_bytes ?? 209715200, maxBindings: storage?.max_bindings ?? 192000, retentionMs: (storage?.retention_days ?? 30) * 86400000 };
    expect(context.testRunOperationStore?.limits).toEqual(expected);
    expect(context.testRunOperationPublisher?.limits).toEqual(expected);
    if (settings.test_runs) settings.test_runs.storage.max_bytes = 209715200;
    expect(context.testRunOperationStore?.limits).toEqual(expected);
    expect(context.testRunOperationPublisher?.limits).toEqual(expected);
    expect(Object.isFrozen(context.testRunOperationStore?.limits)).toBe(true);
    await context.testRunOperationPublisher?.close();
    context.registry?.close();
  });

  it("wires registry, bridge, policy, queues and audit", async () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const audit = new InMemoryAuditSink();
    const context = createGatewayContext(config(instancesDir), policy(), {
      bridge: new FakeUnityBridgeClient(),
      audit,
      now: () => now,
      runId: "gateway-run-1",
      gatewayRuntimeVersion: "2026.9.4-4",
    });

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "compile.status", args_json: "{}", request_id: "req-1" }, context);

    expect(result.status).toBe("ok");
    expect(context.unityProcessDiagnostics).toBeDefined();
    expect(context.unityWindowDiagnostics).toBeDefined();
    expect(context.testRunOperationStore?.dir).toBe(testRunOperationStoreDir(instancesDir));
    expect(context.testRunOperationPublisher?.dir).toBe(context.testRunOperationStore?.dir);
    expect(context.runId).toBe("gateway-run-1");
    expect(context.gatewayRuntimeVersion).toBe("2026.9.4-4");
    expect(audit.starts).toMatchObject([{ phase: "start", tool: "sgg_unity_call" }]);
    expect(audit.events).toMatchObject([{ tool: "sgg_unity_call", status: "ok", project: "game", command: "compile.status" }]);
  });

  it("never silently pairs a custom read store with a default mutation worker", () => {
    const instancesDir = makeTempDir();
    const store = new TestRunOperationStore(path.join(makeTempDir(), "custom-operations"), { maxBindings: 1 });
    const context = createGatewayContext(config(instancesDir), policy(), { testRunOperationStore: store });
    expect(context.testRunOperationStore).toBe(store);
    expect(context.testRunOperationPublisher).toBeUndefined();
  });

  it("preserves an explicitly supplied publisher and reads its validated status cache for diagnostics", async () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const store = new TestRunOperationStore(path.join(makeTempDir(), "custom-operations"));
    const publisher = inlineTestRunOperationPublisher(() => store);
    const cachedStatus = { ...store.snapshotStatus(), state: "unavailable" as const };
    publisher.snapshotStatus = () => cachedStatus;
    const context = createGatewayContext({ ...config(instancesDir), features: { sgg_mcp_diagnostics: true } }, policy(), {
      testRunOperationStore: store, testRunOperationPublisher: publisher, bridge: new FakeUnityBridgeClient(), now: () => now,
    });
    expect(context.testRunOperationPublisher).toBe(publisher);
    useBoundedWindowDiagnostics(context);
    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "diagnostics.status", args_json: "{}" }, context);
    expect(result).toMatchObject({ status: "ok", data: { runtime: { test_run_operation_store: cachedStatus } } });
    expect(store.snapshotStatus().state).toBe("ok");
  });

  it("omits audit sink when config disables audit", () => {
    const context = createGatewayContext({ ...config(makeTempDir()), logging: { level: "info", audit_enabled: false } });
    expect(context.audit).toBeUndefined();
  });

  it("uses file audit sink when audit is enabled and audit_dir is configured", async () => {
    const instancesDir = makeTempDir();
    const auditDir = path.join(makeTempDir(), "audit");
    writeHeartbeat(instancesDir, heartbeat());
    const context = createGatewayContext({ ...config(instancesDir), logging: { level: "info", audit_enabled: true, audit_dir: auditDir } }, policy(), {
      bridge: new FakeUnityBridgeClient(),
      now: () => now,
      runId: "run-1",
    });

    expect(context.audit).toBeInstanceOf(FileAuditSink);
    useBoundedWindowDiagnostics(context);

    await dispatchTool("sgg_unity_call", { project: "game", command: "compile.status", args_json: "{}" }, context);

    const lines = fs.readFileSync(path.join(auditDir, "audit.ndjson"), "utf8").trim().split("\n");
    expect(lines).toHaveLength(2);
    expect(lines.map((line) => JSON.parse(line))).toMatchObject([
      { phase: "start", run_id: "run-1", tool: "sgg_unity_call" },
      { phase: "terminal", run_id: "run-1", tool: "sgg_unity_call", status: "ok" },
    ]);
  });

  it("passes configured asset index cache root to bridge calls", async () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const context = createGatewayContext({ ...config(instancesDir), cache: { asset_index_root: "C:/cache/root" } }, policy(), {
      bridge: new FakeUnityBridgeClient(),
      now: () => now,
    });

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "compile.status", args_json: "{}" }, context);

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({ settings: { asset_index_cache_dir: path.join("C:/cache/root", `game-${shortHash("c:/u/game")}`) } });
    }
  });

  it("passes configured MonoBehaviour allowlist to bridge calls", async () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const context = createGatewayContext(
      {
        ...config(instancesDir),
        scene_authoring: {
          mono_behaviour_allowlist: [{ assembly_name: "Game.UI", full_type_name: "Game.UI.MetaQuestUiProvider", note: "test" }],
        },
      },
      policy(),
      {
        bridge: new FakeUnityBridgeClient(),
        now: () => now,
      },
    );

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "compile.status", args_json: "{}" }, context);

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        settings: {
          mono_behaviour_allowlist: [{ assembly_name: "Game.UI", full_type_name: "Game.UI.MetaQuestUiProvider", note: "test" }],
        },
      });
    }
  });

  it("passes configured feature flags to bridge calls", async () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const context = createGatewayContext({ ...config(instancesDir), features: { scene_authoring_composite_safe_writes: true, ugui_safe_writes: true } }, policy(), {
      bridge: new FakeUnityBridgeClient(),
      now: () => now,
    });

    const result = await dispatchTool("sgg_unity_call", { project: "game", command: "compile.status", args_json: "{}" }, context);

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        settings: {
          feature_flags: { scene_authoring_composite_safe_writes: true, ugui_safe_writes: true },
        },
      });
    }
  });

  it("wires exact process-generation liveness into project discovery", async () => {
    const instancesDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat({
      unity_process_start_utc: "2026-05-12T08:00:00.1234000Z",
      heartbeat_generation: 7,
    }));
    const context = createGatewayContext(config(instancesDir), policy(), {
      bridge: new FakeUnityBridgeClient(),
      now: () => now,
      registryProcessGenerationInspector: (identities) => identities.map((identity) => ({ ...identity, state: "dead" })),
    });

    const result = await dispatchTool("sgg_unity_projects", {}, context);

    expect(result).toMatchObject({
      status: "ok",
      data: { projects: [], counts: { total: 1, live: 0, stale: 1, unique_projects: 1 } },
    });
  });

  it("passes approval auto-allow flag to dispatcher context", () => {
    const approvalsDir = makeTempDir();
    const context = createGatewayContext({ ...config(makeTempDir()), approvals: { dir: approvalsDir, hmac_key_file: path.join(approvalsDir, "key"), default_ttl_ms: 300000, auto_allow_ask: true, auto_allow_known_unity_popups: true, auto_allow_stale_registry_cleanup_without_elicitation: true } });

    expect(context.approvalAutoAllowAsk).toBe(true);
    expect(context.approvalAutoAllowKnownUnityPopups).toBe(true);
    expect(context.approvalAutoAllowStaleCleanupWithoutElicitation).toBe(true);
  });

  it("injects the approval binding service without loading its key", () => {
    const approvalsDir = makeTempDir();
    let getKeyCalls = 0;
    const approvalBindingService = new ApprovalBindingService({ getKey: () => { getKeyCalls += 1; return Buffer.alloc(32); } });
    const context = createGatewayContext(
      { ...config(makeTempDir()), approvals: { dir: approvalsDir, hmac_key_file: path.join(approvalsDir, "key"), default_ttl_ms: 300000, auto_allow_ask: false, auto_allow_known_unity_popups: false, auto_allow_stale_registry_cleanup_without_elicitation: true } },
      undefined,
      { approvalBindingService },
    );

    expect(context.approvalBindingService).toBe(approvalBindingService);
    expect(getKeyCalls).toBe(0);
  });

  it("reloads only known-popup consent from the original config in one run, not unrelated saved settings", async () => {
    const instancesDir = makeTempDir();
    const approvalsDir = makeTempDir();
    writeHeartbeat(instancesDir, heartbeat());
    const original: GatewayConfig = { ...config(instancesDir), features: { sgg_mcp_diagnostics: true }, approvals: {
      dir: approvalsDir, hmac_key_file: path.join(approvalsDir, "key"), default_ttl_ms: 300000,
      auto_allow_ask: false, auto_allow_known_unity_popups: false, auto_allow_stale_registry_cleanup_without_elicitation: false,
    } };
    const file = path.join(makeTempDir(), "gateway.json");
    fs.writeFileSync(file, JSON.stringify(original));
    const context = createGatewayContext(original, policy(), { configPath: file, bridge: new FakeUnityBridgeClient(), now: () => now, runId: "same-popup-run" });
    // This test exercises config/context wiring, not real Windows helper latency.
    // Keep the original dispatcher paths and timeout; supply bounded empty scans.
    useBoundedWindowDiagnostics(context);
    const store = context.approvalStore;
    const binding = context.approvalBindingService;
    const call = () => dispatchTool("sgg_unity_call", { project: "game", command: "compile.status", args_json: "{}" }, context);
    await call();
    expect(context.knownUnityPopupSettings!.snapshot().saved_enabled).toBe(false);
    const saved = structuredClone(original);
    saved.approvals!.auto_allow_known_unity_popups = true;
    saved.approvals!.auto_allow_ask = true;
    saved.approvals!.auto_allow_stale_registry_cleanup_without_elicitation = true;
    saved.approvals!.dir = "new-approval-directory";
    saved.approvals!.hmac_key_file = "new-key-file";
    saved.approvals!.default_ttl_ms = 1000;
    saved.features = { sgg_mcp_diagnostics: false, sgg_mcp_dynamic_csharp: true };
    saved.registry.instances_dir = "new-registry";
    saved.mcp.port = 10001;
    fs.writeFileSync(file, JSON.stringify(saved));
    expect(await call()).toMatchObject({ status: "ok", data: { settings: { feature_flags: original.features } } });
    expect(context.knownUnityPopupSettings!.snapshot()).toMatchObject({ source_status: "valid", saved_enabled: true, restart_required: false });
    expect(context.runId).toBe("same-popup-run");
    expect(context.features).toEqual(original.features);
    expect(context.instancesDir).toBe(instancesDir);
    expect(context.approvalAutoAllowAsk).toBe(false);
    expect(context.approvalAutoAllowStaleCleanupWithoutElicitation).toBe(false);
    expect(context.approvalDefaultTtlMs).toBe(300000);
    expect(context.approvalStore).toBe(store);
    expect(context.approvalBindingService).toBe(binding);
    expect(fs.existsSync(original.approvals!.hmac_key_file)).toBe(false);
    saved.approvals!.auto_allow_known_unity_popups = false;
    fs.writeFileSync(file, JSON.stringify(saved));
    await call();
    expect(context.knownUnityPopupSettings!.snapshot().saved_enabled).toBe(false);
  });

  it("does not create the configured approval key during context construction or dispatch", async () => {
    const instancesDir = makeTempDir();
    const approvalsDir = makeTempDir();
    const hmacKeyFile = path.join(approvalsDir, "secrets", "approval-hmac-v1.key");
    writeHeartbeat(instancesDir, heartbeat());
    const context = createGatewayContext(
      { ...config(instancesDir), approvals: { dir: approvalsDir, hmac_key_file: hmacKeyFile, default_ttl_ms: 300000, auto_allow_ask: false, auto_allow_known_unity_popups: false, auto_allow_stale_registry_cleanup_without_elicitation: true } },
      policy(),
      { bridge: new FakeUnityBridgeClient(), now: () => now },
    );

    expect(fs.existsSync(hmacKeyFile)).toBe(false);
    await dispatchTool("sgg_unity_call", { project: "game", command: "compile.status", args_json: "{}" }, context);
    expect(fs.existsSync(hmacKeyFile)).toBe(false);
    const envelope = createApprovalBindingEnvelope({
      project_selector: "game", project_id: "game", canonical_project_root: "c:/u/game", instance_id: "game-a", command: "asset.import",
      args: {}, mode: "wait", request_id: null, commands_hash: "hash", risk: "write", idempotency: "idempotent",
      policy_action: "ask", policy_reason: "risk_default", policy_pattern: null,
      descriptor: { id: "asset.import", title: "Import", description: "Import", package: "builtin", risk: "write", idempotency: "idempotent", timeout_ms: 1000, schema: { type: "object" } },
    });
    expect(context.approvalBindingService?.create(envelope).request_hmac).toMatch(/^[0-9a-f]{64}$/);
    expect(fs.existsSync(hmacKeyFile)).toBe(true);
  });
});

// These contracts exercise context/cache/audit wiring, not native popup scan
// latency. Preserve dispatcher execution and deadlines with a complete empty scan.
function useBoundedWindowDiagnostics(context: ReturnType<typeof createGatewayContext>): void {
  context.unityWindowDiagnostics = createUnityWindowDiagnosticsRuntime({ inspect: () => inspectUnityWindowDiagnostics({ execFileSync: file => file.endsWith("tasklist.exe")
    ? "INFO: No tasks are running which match the specified criteria."
    : JSON.stringify({ rows: [], user32_scan_succeeded: true, user32_scan_complete: true, user32_candidate_count: 0, uia_scan_succeeded: false, uia_scan_status: "not_required" }) }) });
}

function makeTempDir(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-gateway-context-"));
  tempDirs.push(dir);
  return dir;
}

function writeHeartbeat(instancesDir: string, record: HeartbeatRecord): void {
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record, null, 2));
}

function config(instancesDir: string): GatewayConfig {
  return {
    mcp: {
      host: "127.0.0.1",
      port: 27182,
      token_env: "SGG_UNITY_MCP_TOKEN",
      allowed_origins: [],
      allow_non_loopback: false,
      max_body_bytes: 1048576,
      request_timeout_ms: 30000,
      shutdown_timeout_ms: 5000,
      sessions: { max_active: 128, idle_ttl_ms: 1800000, cleanup_interval_ms: 60000 },
      rate_limit: { window_ms: 60000, max_requests: 120, max_buckets: 4096, cleanup_interval_ms: 60000, trusted_proxy: false, trusted_proxy_peers: [] },
    },
    registry: { instances_dir: instancesDir, stale_after_ms: 90000, cleanup_after_ms: 600000, cache_max_entries: 1024, cache_ttl_ms: 600000, invalid_diagnostics_max: 32, scan_max_files: 1024 },
    logging: { level: "info", audit_enabled: true },
  };
}

function policy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: {
      read: "allow",
      write: "ask",
      execute: "deny",
      destructive: "ask",
      network: "deny",
    },
    projects: {},
  };
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [
      { environment: "unity", host_path: "C:/u/game", canonical_path: "c:/u/game" },
      { environment: "windows", host_path: "C:\\u\\game", canonical_path: "c:/u/game" },
      { environment: "wsl", host_path: "/mnt/c/u/game", canonical_path: "c:/u/game" },
    ],
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "hash",
    last_heartbeat: "2026-05-12T09:59:30.000Z",
    ...overrides,
  };
}

function shortHash(input: string): string {
  return createHash("sha256").update(input).digest("hex").slice(0, 12);
}
