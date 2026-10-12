import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { dispatchAcceptedPendingCall, dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { ApprovalStore } from "../src/approvals/approval-store.js";
import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { TestRunOperationStore } from "../src/operations/test-run-operation-store.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { TEST_RUN_COMPILE_READINESS_CONTRACT } from "../src/mcp/test-run-compile-readiness.js";
import { inlineTestRunOperationPublisher } from "./helpers/test-run-operation-publisher.js";

const tempDirs: string[] = [];
const startedAtMs = Date.parse("2026-08-18T14:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("MCP-PR-133 post-reload TestRunner continuity", () => {
  it("PR221 performs one bounded readiness read before discovery and one opted-in test request", async () => {
    const fixture = makeReadinessFixture();
    fixture.bridge.onDescribe = (selector) => { if (selector === "command:compile.wait_ready") fixture.advance(3000); };
    fixture.bridge.onInvoke = (command) => { if (command === "compile.wait_ready") fixture.advance(4000); };
    const result = await fixture.call();
    expect(result.status).toBe("ok");
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready", "test.list", "test.run"]);
    const readiness = fixture.bridge.requests[0]!;
    expect(readiness.args).toEqual({ timeout_ms: 12_000, poll_interval_ms: 250, response_mode: "compact" });
    expect(readiness.deadline_unix_ms).toBe(startedAtMs + 20_000);
    expect(fixture.bridge.requests[2]?.args._test_discovery_generation).toBe(17);
    expect(fixture.bridge.testRunCount).toBe(1);
  });

  it("PR221 preserves the binding-publication reserve in the shared readiness/discovery budget", async () => {
    const fixture = makeReadinessFixture(true);
    const result = await fixture.call();
    expect(result.status).toBe("ok");
    expect(fixture.bridge.requests[0]?.args.timeout_ms).toBe(11_000);
    expect(fixture.bridge.requests[0]?.deadline_unix_ms).toBe(startedAtMs + 16_000);
    expect(fixture.bridge.testRunCount).toBe(1);
  });

  it.each(["metadata", "read"])("PR243 measures discovery %s failure including metadata latency without extending startup", async phase => {
    const fixture = makeReadinessFixture(true);
    fixture.bridge.onDescribe = selector => { if (selector === "command:test.list") fixture.advance(3000); };
    fixture.bridge.discoveryMetadataError = phase === "metadata";
    fixture.bridge.discoveryError = phase === "read";
    fixture.bridge.onInvoke = command => { if (command === "test.list") fixture.advance(2000); };
    const result = await fixture.call();
    expect(result).toMatchObject({ status: "error", error: { details: {
      test_run_bridge_request_issued: false,
      test_run_compile_readiness_preflight: { reason: phase === "metadata" ? "discovery_metadata_read_failed" : "discovery_read_failed" },
      test_run_startup_timing: { contract: "sgg.unity_mcp.test_run_startup_timing.v1", phase: "discovery",
        phase_elapsed_ms: phase === "metadata" ? 3000 : 5000,
        effective_startup_budget_ms: 20000, descriptor_timeout_ms: 20000, binding_publication_reserve_ms: 4000,
        remaining_startup_ms: phase === "metadata" ? 17000 : 15000, command_replayed: false },
    } } });
    expect(fixture.bridge.testRunCount).toBe(0);
  });

  it("PR243 includes discovery metadata in successful elapsed evidence while keeping one shared deadline", async () => {
    const fixture = makeReadinessFixture();
    fixture.bridge.onDescribe = selector => { if (selector === "command:test.list") fixture.advance(3000); };
    fixture.bridge.onInvoke = command => { if (command === "test.list") fixture.advance(2000); };
    expect(await fixture.call()).toMatchObject({ status: "ok", data: { test_run_discovery_preflight: {
      elapsed_ms: 5000, metadata_elapsed_ms: 3000, startup_deadline_ms: 20000,
    } } });
    // The helper's own shorter descriptor can reduce, never extend, the shared deadline.
    expect(fixture.bridge.requests.filter(request => request.command === "test.list").every(request => request.deadline_unix_ms! <= startedAtMs + 20000)).toBe(true);
    expect(fixture.bridge.testRunCount).toBe(1);
  });

  it("PR221 does not restart the startup deadline between readiness and pending discovery", async () => {
    const fixture = makeReadinessFixture();
    fixture.bridge.discoveryStatus = "pending";
    fixture.bridge.onInvoke = (command) => { if (command === "compile.wait_ready") fixture.advance(12_000); };
    const result = await fixture.call();
    expect(result).toMatchObject({ status: "error", error: { code: "test_filter_resolution_pending", details: { test_run_bridge_request_issued: false } } });
    expect(fixture.now().getTime()).toBe(startedAtMs + 20_000);
    expect(fixture.bridge.requests.filter((request) => request.command === "test.list").every((request) => request.deadline_unix_ms === startedAtMs + 20_000)).toBe(true);
    expect(fixture.bridge.testRunCount).toBe(0);
    expect(fixture.bridge.invokedCommands).not.toContain("compile.refresh_and_wait");
  });

  it.each([false, true])("PR221 leaves ordinary async and absent-contract legacy terminal routes unchanged (wait=%s)", async (wait) => {
    const fixture = makeReadinessFixture();
    if (wait) delete fixture.bridge.runDescriptor.wrapper_wait!.wait_for_test_run_terminal_ms!.compile_readiness_contract;
    const result = await fixture.call(wait);
    expect(result.status).toBe("ok");
    expect(fixture.bridge.invokedCommands).toEqual(["test.list", "test.run"]);
  });

  it("PR221 preserves future metadata without activating it absent caller opt-in", async () => {
    const fixture = makeReadinessFixture();
    fixture.bridge.runDescriptor.wrapper_wait!.wait_for_test_run_terminal_ms!.compile_readiness_contract = "future.v2";
    expect((await fixture.call(false)).status).toBe("ok");
    expect(fixture.bridge.invokedCommands).toEqual(["test.list", "test.run"]);
  });

  it("PR221 refuses an explicitly selected unsupported contract before any command request", async () => {
    const fixture = makeReadinessFixture();
    fixture.bridge.runDescriptor.wrapper_wait!.wait_for_test_run_terminal_ms!.compile_readiness_contract = "future.v2";
    expect(await fixture.call()).toMatchObject({ status: "error", error: { code: "invalid_field_value" } });
    expect(fixture.bridge.requests).toEqual([]);
  });

  it.each(["metadata_missing", "wrong_instance", "wrong_generation", "wrong_package", "unsafe_helper", "unsupported_schema", "helper_denied", "ready_missing_field", "ready_timed_out", "inventory_unavailable", "background_wrong_generation", "read_timeout", "delivery_reserve"])("PR221 refuses %s before discovery or test issuance", async (failure) => {
    const fixture = makeReadinessFixture();
    if (failure === "metadata_missing") fixture.bridge.metadataOverrides = { instance_id: undefined, catalog_generation: undefined };
    if (failure === "wrong_instance") fixture.bridge.metadataOverrides = { instance_id: "other-owner" };
    if (failure === "wrong_generation") fixture.bridge.metadataOverrides = { catalog_generation: 9 };
    if (failure === "wrong_package") fixture.bridge.helperDescriptor.package_version = "other";
    if (failure === "unsafe_helper") fixture.bridge.helperDescriptor.idempotency = "unsafe";
    if (failure === "unsupported_schema") delete (fixture.bridge.helperDescriptor.schema.properties as Record<string, unknown>).timeout_ms;
    if (failure === "helper_denied") fixture.context.policy!.projects.game!.deny.push("compile.wait_ready");
    if (failure === "ready_missing_field") delete fixture.bridge.readiness.ready;
    if (failure === "ready_timed_out") fixture.bridge.readiness.timed_out = true;
    if (failure === "inventory_unavailable") Object.assign((fixture.bridge.readiness.package_update as Record<string, unknown>).local_package_code_inventory!, { status: "unavailable", failure_reason: "" });
    if (failure === "background_wrong_generation") Object.assign(fixture.bridge.readiness.background_compiler_progress!, { observed: true, authoritative: true, state: "complete", package_fingerprint: "a".repeat(64), observed_utc: "2026-08-18T14:00:00.0000000Z", catalog_generation: 999, compilation_generation: 5, completed_compilation_generation: 5 });
    if (failure === "read_timeout") fixture.bridge.readFailure = true;
    if (failure === "delivery_reserve") fixture.bridge.onDescribe = (selector) => { if (selector === "command:compile.wait_ready") fixture.advance(15_000); };
    const result = await fixture.call();
    expect(result).toMatchObject({ status: "error", error: { details: { test_run_bridge_request_issued: false, test_runner_execute_invoked: false, test_run_replayed: false } } });
    expect(fixture.bridge.testRunCount).toBe(0);
    expect(fixture.bridge.invokedCommands.every((command) => command === "compile.wait_ready")).toBe(true);
  });

  it.each(["policy", "feature", "owner", "package", "catalog", "compilation", "deadline"])("PR221 rechecks %s after discovery immediately before the only test request", async (drift) => {
    const fixture = makeReadinessFixture();
    fixture.bridge.onInvoke = (command) => {
      if (command !== "test.list") return;
      if (drift === "policy") fixture.context.policy!.projects.game!.deny.push("test.run");
      if (drift === "feature") fixture.context.features!.build_test_automation = false;
      if (["owner", "package", "catalog", "compilation"].includes(drift)) writeHeartbeat(fixture.instancesDir, heartbeat({
        ...(drift === "owner" ? { unity_process_start_utc: "2026-08-18T13:00:00.000Z" } : {}),
        ...(drift === "package" ? { package_version: "other" } : {}),
        ...(drift === "catalog" ? { commands_hash: "other-catalog", catalog_generation: 9 } : {}),
        ...(drift === "compilation" ? { compilation_generation: 6, completed_compilation_generation: 6 } : {}),
      }));
      if (drift === "deadline") fixture.advance(20_001);
    };
    expect(await fixture.call()).toMatchObject({ status: "error", error: { details: { test_run_bridge_request_issued: false } } });
    expect(fixture.bridge.testRunCount).toBe(0);
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready", "test.list"]);
  });

  it("PR221 retains the human ask gate and claims one accepted approval before its safe preflight", async () => {
    const fixture = makeReadinessFixture();
    fixture.context.policy!.projects.game!.ask.push("test.run");
    const store = new ApprovalStore(path.join(fixture.instancesDir, "approvals"));
    fixture.context.approvalStore = store;
    fixture.context.approvalBindingService = new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) });
    const rawArgs = { project: "game", command: "test.run", mode: "wait" as const,
      args_json: '{"mode":"editmode","filter":"SampleTests","response_mode":"compact"}', wait_for_test_run_terminal_ms: 20_000 };
    const pending = await dispatchTool("sgg_unity_call", rawArgs, fixture.context);
    expect(pending).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(fixture.bridge.requests).toEqual([]);
    if (pending.status !== "error") throw new Error("Expected human approval gate");
    const request = pending.error.details!.approval_request as Record<string, unknown>;
    const generation = { id: String(request.pending_id), created_at: String(request.created_at), updated_at: String(request.updated_at), expires_at: String(request.expires_at) };
    const claim = vi.spyOn(store, "claimPendingGeneration");
    const consume = vi.spyOn(store, "consume");
    const result = await dispatchAcceptedPendingCall(rawArgs, fixture.context, { generation, original_confirmation: pending });
    expect(result.status).toBe("ok");
    expect(claim).toHaveBeenCalledTimes(1);
    expect(consume).not.toHaveBeenCalled();
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready", "test.list", "test.run"]);
    claim.mockRestore();
    consume.mockRestore();
  });

  it("PR221 refuses late pre-issuance drift with no binding publication or test request", async () => {
    const fixture = makeReadinessFixture(true);
    const publish = vi.spyOn(fixture.context.testRunOperationStore!, "publish");
    fixture.bridge.beforeIssue = (command) => { if (command === "test.run") fixture.context.policy!.projects.game!.deny.push("test.run"); };
    expect(await fixture.call()).toMatchObject({ status: "error", error: { details: { test_run_bridge_request_issued: false } } });
    expect(publish).not.toHaveBeenCalled();
    expect(fixture.bridge.testRunCount).toBe(0);
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready", "test.list"]);
    publish.mockRestore();
  });

  it("PR221 rechecks drift during binding publication before the sole test request", async () => {
    const fixture = makeReadinessFixture(true);
    const store = fixture.context.testRunOperationStore!;
    const original = store.publish.bind(store);
    const publish = vi.spyOn(store, "publish").mockImplementation((binding, options) => {
      fixture.context.features!.build_test_automation = false;
      return original(binding, options);
    });
    expect(await fixture.call()).toMatchObject({ status: "error", error: { details: { test_run_bridge_request_issued: false } } });
    expect(publish).toHaveBeenCalledTimes(1);
    expect(fixture.bridge.testRunCount).toBe(0);
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready", "test.list"]);
    publish.mockRestore();
  });

  it.each(["readiness", "discovery_metadata", "discovery_read"])("PR221 refuses owner drift during %s without a test request", async (stage) => {
    const fixture = makeReadinessFixture();
    const replace = () => writeHeartbeat(fixture.instancesDir, heartbeat({ unity_pid: 54321, unity_process_start_utc: "2026-08-18T13:00:00.000Z" }));
    fixture.bridge.onDescribe = (selector) => { if (stage === "discovery_metadata" && selector === "command:test.list") replace(); };
    fixture.bridge.onInvoke = (command) => { if (stage === "readiness" && command === "compile.wait_ready" || stage === "discovery_read" && command === "test.list") replace(); };
    expect(await fixture.call()).toMatchObject({ status: "error", error: { details: { test_run_bridge_request_issued: false } } });
    expect(fixture.bridge.testRunCount).toBe(0);
    if (stage === "discovery_metadata") expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready"]);
  });

  it("PR221 does not promote a native wait returning pending evidence to ready or issue a second helper read", async () => {
    const fixture = makeReadinessFixture();
    Object.assign(fixture.bridge.readiness, { status: "compiling", editor_status: "compiling", ready: false, editor_ready: false, is_compiling: true });
    expect(await fixture.call()).toMatchObject({ status: "error", error: { code: "compile_freshness_unverified", details: { test_run_bridge_request_issued: false } } });
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready"]);
  });

  it("PR221 blocks a helper with a compact-only schema that cannot admit the generated reduced timeout", async () => {
    const fixture = makeReadinessFixture();
    (fixture.bridge.helperDescriptor.schema.properties as Record<string, unknown>).timeout_ms = { type: "integer", minimum: 16_000, maximum: 300_000 };
    expect(await fixture.call()).toMatchObject({ status: "error", error: { code: "catalog_stale", details: { test_run_bridge_request_issued: false } } });
    expect(fixture.bridge.invokedCommands).toEqual([]);
  });

  it.each(["timeout_ms", "poll_interval_ms", "response_mode"])("PR221 requires typed generated-field schema support for %s", async (field) => {
    const fixture = makeReadinessFixture();
    (fixture.bridge.helperDescriptor.schema.properties as Record<string, unknown>)[field] = {};
    expect(await fixture.call()).toMatchObject({ status: "error", error: { code: "catalog_stale", details: { test_run_bridge_request_issued: false } } });
    expect(fixture.bridge.requests).toEqual([]);
  });

  it("PR221 gates the frozen helper descriptor rather than its mutable metadata reference", async () => {
    const fixture = makeReadinessFixture();
    fixture.bridge.helperDescriptor.feature_flag = "ready_feature";
    fixture.context.features!.ready_feature = true;
    fixture.bridge.beforeIssue = (command) => {
      if (command !== "compile.wait_ready") return;
      delete fixture.bridge.helperDescriptor.feature_flag;
      fixture.context.features!.ready_feature = false;
    };
    expect(await fixture.call()).toMatchObject({ status: "error", error: { code: "permission_denied", details: { test_run_bridge_request_issued: false } } });
    expect(fixture.bridge.requests).toEqual([]);
  });

  it("PR221 rejects a discovery descriptor with a different command identity through bounded refusal", async () => {
    const fixture = makeReadinessFixture();
    fixture.bridge.listDescriptor.id = "other.read";
    expect(await fixture.call()).toMatchObject({ status: "error", error: { code: "catalog_stale", details: { test_run_bridge_request_issued: false } } });
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready"]);
  });

  it.each(["error", "pending", "unavailable", "metadata"])("PR221 projects bounded discovery %s evidence without forwarding untrusted failure text", async (failure) => {
    const fixture = makeReadinessFixture();
    const canary = "private-canary-discovery-secret".repeat(2000);
    fixture.bridge.discoveryStatus = failure === "pending" ? "pending" : "unavailable";
    fixture.bridge.scanReason = canary;
    fixture.bridge.discoveryError = failure === "error";
    fixture.bridge.discoveryMetadataError = failure === "metadata";
    const result = await fixture.call();
    expect(result).toMatchObject({ status: "error", error: { details: { test_run_bridge_request_issued: false } } });
    expect(JSON.stringify(result)).not.toContain("private-canary-discovery-secret");
    expect(fixture.bridge.testRunCount).toBe(0);
  });

  it.each(["read_timeout", "read_reloading", "discovery_metadata_timeout"])("PR221 never repeats the selected helper after %s with a real recent-replacement cache", async (failure) => {
    const fixture = makeReadinessFixture();
    const registry = fixture.context.registry!;
    registry.captureSnapshot();
    fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
    writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, heartbeat_generation: 42 }));
    const recent = registry.captureSnapshot().findRecentReplacement("game");
    expect(recent).toMatchObject({ failed_record: { instance_id: "game-old" }, replacement_record: { instance_id: "game-new" } });
    fixture.bridge.readFailure = failure === "read_timeout";
    fixture.bridge.readReloading = failure === "read_reloading";
    fixture.bridge.discoveryMetadataError = failure === "discovery_metadata_timeout";
    fixture.bridge.discoveryMetadataErrorCode = "bridge_timeout";
    const result = await fixture.call();
    expect(result).toMatchObject({ status: "error", error: { details: { queue_admitted: true, test_run_bridge_request_issued: false, test_run_replayed: false } } });
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready"]);
    expect(fixture.bridge.requests[0]?.deadline_unix_ms).toBe(startedAtMs + 20_000);
    expect(fixture.now().getTime()).toBe(startedAtMs);
    expect(fixture.bridge.testRunCount).toBe(0);
    expect(JSON.stringify(result)).not.toContain("post_reload_routing");
  });

  it("PR221 keeps recovery before the selected preflight and then issues one helper and test request", async () => {
    const fixture = makeReadinessFixture();
    fixture.context.registry!.captureSnapshot();
    fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
    writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, heartbeat_generation: 42 }));
    expect(fixture.context.registry!.captureSnapshot().findRecentReplacement("game")).toBeDefined();
    fixture.bridge.runMetadataFailures = 1;
    expect((await fixture.call()).status).toBe("ok");
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready", "test.list", "test.run"]);
    expect(fixture.bridge.testRunCount).toBe(1);
  });

  it.each(["before_commit", "after_commit"])("never re-enters post-reload routing after an annotated readiness timeout at %s", async (phase) => {
    const fixture = makeReadinessFixture(true);
    const registry = fixture.context.registry!;
    registry.captureSnapshot();
    fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
    writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, heartbeat_generation: 42 }));
    expect(registry.captureSnapshot().findRecentReplacement("game")).toMatchObject({
      failed_record: { instance_id: "game-old" }, replacement_record: { instance_id: "game-new" },
    });
    const publisher = fixture.context.testRunOperationPublisher!;
    const preparedIds: string[] = [];
    fixture.context.testRunOperationPublisher = {
      ...publisher,
      async prepare(binding, options) {
        preparedIds.push(binding.operation_id);
        const lease = await publisher.prepare(binding, phase === "before_commit" ? {
          ...options, beforeCommit: () => { fixture.advance(20_000); return options.beforeCommit(); },
        } : options);
        if (phase === "after_commit") fixture.advance(20_000);
        return lease;
      },
    };
    const result = await fixture.call();
    expect(result).toMatchObject({ status: "error", error: { code: "bridge_timeout", details: {
      test_run_workflow_id: preparedIds[0], operation_binding_published: phase === "after_commit",
      test_run_bridge_request_issued: false, test_run_replayed: false,
    } } });
    expect(preparedIds).toHaveLength(1);
    expect(fixture.bridge.invokedCommands).toEqual(["compile.wait_ready", "test.list"]);
    expect(fixture.bridge.testRunCount).toBe(0);
    expect(fixture.context.testRunOperationStore!.snapshotStatus().bindings).toBe(phase === "after_commit" ? 1 : 0);
    expect(JSON.stringify(result)).not.toContain("post_reload_routing");
  });

  it("PR221 project-authored lookalike details cannot disable legacy pre-dispatch recovery", async () => {
    const fixture = makeReadinessFixture();
    delete fixture.bridge.runDescriptor.wrapper_wait!.wait_for_test_run_terminal_ms!.compile_readiness_contract;
    fixture.context.registry!.captureSnapshot();
    fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
    writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, heartbeat_generation: 42 }));
    expect(fixture.context.registry!.captureSnapshot().findRecentReplacement("game")).toBeDefined();
    fixture.bridge.discoveryErrorCode = "bridge_timeout";
    fixture.bridge.lookalikeReadinessFailure = true;
    let reads = 0;
    fixture.bridge.onInvoke = (command) => { if (command === "test.list") fixture.bridge.discoveryError = ++reads === 1; };
    const result = await fixture.call();
    expect(result.status).toBe("ok");
    expect(fixture.bridge.invokedCommands).toEqual(["test.list", "test.list", "test.run"]);
    expect(fixture.bridge.testRunCount).toBe(1);
  });

  it("polls read-only discovery until one generation is ready, then dispatches test.run once", async () => {
    const fixture = makeFixture(2_000);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, (poll) => poll < 3 ? "pending" : "ready");
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

    expect(result.status).toBe("ok");
    expect(bridge.invokedCommands).toEqual(["test.list", "test.list", "test.list", "test.run"]);
    expect(bridge.testRunCount).toBe(1);
    expect(bridge.testRunArgs).toMatchObject({ _test_discovery_generation: 17 });
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        status: "running",
        test_run_discovery_preflight: {
          contract: "sgg.unity_mcp.test_run_discovery_preflight.v1",
          status: "ready",
          poll_count: 3,
          read_request_count: 3,
          discovery_generation: 17,
          test_run_bridge_request_issued: false,
          test_runner_execute_invoked: false,
          command_replayed: false,
          workflow_identity: {
            instance_id: "game-old",
            unity_pid: 12345,
            unity_process_start_utc: "2026-08-18T12:00:00.000Z",
            heartbeat_generation: 41,
            catalog_generation: 8,
            compilation_generation: 5,
            completed_compilation_generation: 5,
            discovery_generation: 17,
          },
        },
      });
    }
  });

  it("requires current mode-only PlayMode discovery before one Execute and binds its generation", async () => {
    const fixture = makeFixture(2_000);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, (poll) => poll < 2 ? "pending" : "ready");
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "playmode" });

    expect(result.status).toBe("ok");
    expect(bridge.invokedCommands).toEqual(["test.list", "test.list", "test.run"]);
    expect(bridge.testRunCount).toBe(1);
    expect(bridge.testRunArgs).toMatchObject({ mode: "playmode", _test_discovery_generation: 17 });
    if (result.status === "ok") expect(result.data).toMatchObject({ test_run_discovery_preflight: { status: "ready", mode: "playmode", read_request_count: 2 } });
  });

  it("refuses mode-only zero candidates without binding publication or a TestRunner Execute", async () => {
    const fixture = makeFixture(15_000, heartbeat(), true);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "ready", 0, undefined, 3, 0);
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "playmode" });

    expect(result.status).toBe("error");
    expect(bridge.invokedCommands).toEqual(["test.list"]);
    expect(bridge.testRunCount).toBe(0);
    expect(fixture.context.testRunOperationStore?.snapshotStatus()).toMatchObject({ bindings: 0 });
    if (result.status === "error") expect(result.error).toMatchObject({ code: "test_filter_unresolved", details: {
      test_run_bridge_request_issued: false, test_runner_execute_invoked: false, write_replay_allowed: false,
      test_run_discovery_preflight: { status: "ready", reason: "mode_only_no_runnable_leaves", discovery_generation: 17 },
    } });
  });

  it("reserves binding publication time and dispatches one artifact-capable filtered run", async () => {
    const fixture = makeFixture(15_000, heartbeat(), true);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, (poll) => poll < 33 ? "pending" : "ready");
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

    expect(result.status).toBe("ok");
    expect(bridge.testRunCount).toBe(1);
    expect(fixture.context.testRunOperationStore?.snapshotStatus()).toMatchObject({ state: "ok", bindings: 1 });
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        test_run_discovery_preflight: {
          status: "ready",
          bridge_admission_reserve_ms: 4_000,
          discovery_generation: 17,
        },
      });
    }
  });

  it("ends artifact-capable discovery at the reserved phase deadline without publishing a binding", async () => {
    const fixture = makeFixture(15_000, heartbeat(), true);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "pending");
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

    expect(result.status).toBe("error");
    expect(bridge.testRunCount).toBe(0);
    expect(fixture.context.testRunOperationStore?.snapshotStatus()).toMatchObject({ bindings: 0 });
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        command_bridge_request_issued: false,
        test_run_bridge_request_issued: false,
        test_runner_execute_invoked: false,
        test_run_discovery_preflight: {
          status: "pending",
          startup_deadline_ms: 11_000,
          bridge_admission_reserve_ms: 4_000,
        },
      });
    }
  });

  it("retains bounded discovery-generation evidence in compact test.run responses", async () => {
    const fixture = makeFixture(2_000);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "ready");
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test", response_mode: "compact" });

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        response_mode: "compact",
        test_run_discovery_preflight: {
          status: "ready",
          discovery_generation: 17,
          test_run_bridge_request_issued: false,
          test_runner_execute_invoked: false,
          workflow_identity: { instance_id: "game-old", heartbeat_generation: 41, catalog_generation: 8, discovery_generation: 17 },
        },
      });
    }
  });

  it("returns typed pending with zero test.run requests, then a later explicit call dispatches once", async () => {
    const fixture = makeFixture(500);
    let ready = false;
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => ready ? "ready" : "pending");
    fixture.context.bridge = bridge;

    const pending = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

    expect(pending.status).toBe("error");
    expect(bridge.testRunCount).toBe(0);
    if (pending.status === "error") {
      expect(pending.error.code).toBe("test_filter_resolution_pending");
      expect(pending.error.details).toMatchObject({
        command_delivery_state: "not_dispatched",
        command_bridge_request_issued: false,
        test_run_bridge_request_issued: false,
        test_runner_execute_invoked: false,
        test_run_replayed: false,
        test_run_discovery_preflight: { status: "pending", discovery_generation: null },
      });
    }

    ready = true;
    const explicitRetry = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" }, "pr-133-explicit-retry");

    expect(explicitRetry.status).toBe("ok");
    expect(bridge.testRunCount).toBe(1);
  });

  it("keeps unavailable discovery distinct and never dispatches test.run", async () => {
    const fixture = makeFixture(2_000);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "unavailable");
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "playmode", filter: "Play.Fixture.Test", assembly: "Game.PlayModeTests" });

    expect(result.status).toBe("error");
    expect(bridge.invokedCommands).toEqual(["test.list"]);
    expect(bridge.testRunCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("test_filter_resolution_unavailable");
      expect(result.error.details).toMatchObject({
        test_runner_execute_invoked: false,
        test_run_discovery_preflight: { status: "unavailable", mode: "playmode", assembly: "Game.PlayModeTests", reason: "retrieve_test_list_timeout" },
      });
    }
  });

  it("rejects mismatched discovery identity before test.run", async () => {
    const fixture = makeFixture(2_000);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "ready", 0, "playmode");
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

    expect(result.status).toBe("error");
    expect(bridge.testRunCount).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.error.details).toMatchObject({ test_run_discovery_preflight: { status: "error", reason: "test_list_identity_mismatch" } });
    }
  });

  it("follows one proven same-process replacement for a stable selector before any unsafe POST", async () => {
    const fixture = makeFixture(2_000, heartbeat({ status: "reloading" }));
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "ready");
    fixture.context.bridge = bridge;
    let replaced = false;
    fixture.context.sleepMs = async (ms) => {
      fixture.advance(ms);
      if (replaced) return;
      replaced = true;
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
      writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "ready", heartbeat_generation: 42, last_heartbeat: fixture.now().toISOString() }));
    };

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

    expect(result.status).toBe("ok");
    expect(bridge.invokedInstances).toEqual(["game-new", "game-new"]);
    expect(bridge.invokedCommands).toEqual(["test.list", "test.run"]);
    expect(bridge.testRunCount).toBe(1);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        post_reload_routing: {
          terminal_status: "unsafe_dispatched_once",
          command_request_replayed: false,
          command_risk: "execute",
          current_candidate_instance: { instance_id: "game-new" },
        },
      });
    }
  });

  it("waits through a transient command-catalog timeout before one same-instance test dispatch", async () => {
    const fixture = makeFixture(2_000);
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "ready", 1);
    fixture.context.bridge = bridge;

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

    expect(result.status).toBe("ok");
    expect(bridge.testRunCount).toBe(1);
    expect(bridge.invokedCommands).toEqual(["test.list", "test.run"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({ post_reload_routing: { terminal_status: "unsafe_dispatched_once", command_request_replayed: false } });
    }
  });

  it("keeps an exact instance selector strict after same-process replacement", async () => {
    const fixture = makeFixture(2_000, heartbeat({ status: "reloading" }));
    const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "ready");
    fixture.context.bridge = bridge;
    let replaced = false;
    fixture.context.sleepMs = async (ms) => {
      fixture.advance(ms);
      if (replaced) return;
      replaced = true;
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
      writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "ready", heartbeat_generation: 42, last_heartbeat: fixture.now().toISOString() }));
    };

    const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" }, "pr-133-exact", "game-old");

    expect(result.status).toBe("error");
    expect(bridge.invokedCommands).toEqual([]);
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        command_delivery_state: "not_dispatched",
        post_reload_routing: { terminal_status: "unsafe_not_dispatched", command_request_replayed: false, current_candidate_instance: { instance_id: "game-new" } },
      });
    }
  });

  it("fails closed on cross-process replacement and catalog drift", async () => {
    for (const replacement of [
      heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "ready", unity_pid: 54321 }),
      heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "ready", commands_hash: "catalog-b" }),
    ]) {
      const fixture = makeFixture(2_000, heartbeat({ status: "reloading" }));
      const bridge = new DiscoveryBridge(fixture.context.bridge as BridgeClient, () => "ready");
      fixture.context.bridge = bridge;
      let replaced = false;
      fixture.context.sleepMs = async (ms) => {
        fixture.advance(ms);
        if (replaced) return;
        replaced = true;
        fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
        writeHeartbeat(fixture.instancesDir, { ...replacement, last_heartbeat: fixture.now().toISOString() });
      };

      const result = await callTestRun(fixture.context, { mode: "editmode", filter: "Focused.Fixture.Test" });

      expect(result.status).toBe("error");
      expect(bridge.invokedCommands).toEqual([]);
      if (result.status === "error") expect(result.error.details).toMatchObject({ command_delivery_state: "not_dispatched" });
    }
  });
});

function makeReadinessFixture(artifactCapable = false) {
  const fixture = makeFixture(20_000, heartbeat(), artifactCapable);
  const bridge = new ReadinessBridge(fixture.context.bridge!, testRunCommand(20_000, artifactCapable));
  fixture.context.bridge = bridge;
  fixture.context.features = { build_test_automation: true };
  fixture.context.policy = { schema_version: 1, defaults: { read: "allow", write: "allow", execute: "allow", destructive: "ask", network: "ask" }, projects: { game: { allow: [], ask: [], deny: [] } } };
  return { ...fixture, bridge, call: (wait = true) => dispatchTool("sgg_unity_call", {
    project: "game", command: "test.run", mode: "wait", args_json: '{"mode":"editmode","filter":"SampleTests","response_mode":"compact"}',
    ...(wait ? { wait_for_test_run_terminal_ms: 20_000 } : {}),
  }, fixture.context) };
}

function compileReadySourceDto(): Record<string, unknown> {
  const emptyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
  return {
    status: "ready", editor_status: "ready", ready: true, editor_ready: true, lifecycle_settled: true, compilable: true,
    is_compiling: false, is_updating: false, is_playing: false, terminal_compile_failure_without_reload: false,
    package_refresh_required: false, package_refresh_wait_can_advance: false, stale_package_resolution: false, package_metadata_unavailable: false,
    timed_out: false, has_compile_errors: false, compile_error_count: 0, waited_ms: 0, timeout_ms: 0, last_editor_update_age_ms: 0, status_duration_ms: 0,
    package_update: { status: "current", refresh_pending: false, package_refresh_required: false, refresh_wait_can_advance: false,
      stale_package_resolution: false, fingerprint: "a".repeat(64), pending_fingerprint: "", refresh_count: 0, fingerprint_failure: null,
      local_package_code_inventory: { status: "current", failure_reason: "", mismatch_package: "", local_package_count: 0,
        source_file_count: 0, asset_database_file_count: 0, missing_asset_file_count: 0, unexpected_asset_file_count: 0,
        source_fingerprint: emptyHash, asset_database_fingerprint: emptyHash, source_state_fingerprint: emptyHash } },
    compile_barrier: { state: "not_required", required: false, requested: false, request_issued: false, started: false, finished: false,
      terminal_compile_result: "unknown", terminal_compile_error_count: 0, terminal_compile_failure_without_reload: false,
      reload_started: false, post_reload_observed: false, settling: false, reload_pending: false, stable: true, failure: "", operation_id: "", source_instance_id: "" },
    background_compiler_progress: { observed: false, authoritative: false, active: false, failed: false, blocks_readiness: false,
      state: "unobserved", category: "none", relevant_item_count: 0, active_item_count: 0, progress_permille: null,
      process_id: 12345, catalog_generation: 0, compilation_generation: 0, completed_compilation_generation: 0,
      package_fingerprint: "", observed_utc: "", failure_reason: "", correlated_native_failure_observed: false,
      native_failure_category: "none", native_failure_observed_utc: "", native_failure_absence_authoritative: false, cancellation_attempted: false, work_replayed: false },
  };
}

class ReadinessBridge implements BridgeClient {
  readonly invokedCommands: string[] = [];
  readonly requests: BridgeInvokeRequest[] = [];
  readonly helperDescriptor = baseCommand("compile.wait_ready", "read", 300_000, { type: "object", properties: {
    timeout_ms: { type: "integer", minimum: 1, maximum: 300_000 }, poll_interval_ms: { type: "integer", minimum: 50, maximum: 5000 },
    response_mode: { type: "string", enum: ["full", "compact"] },
  }, additionalProperties: false });
  readonly listDescriptor = testListCommand();
  readiness = compileReadySourceDto();
  metadataOverrides: Record<string, unknown> = {};
  onDescribe: ((selector: string) => void) | undefined;
  onInvoke: ((command: string) => void) | undefined;
  beforeIssue: ((command: string) => void) | undefined;
  readFailure = false;
  readReloading = false;
  discoveryStatus = "ready";
  scanReason = "retrieve_test_list_pending";
  discoveryError = false;
  discoveryErrorCode: "test_filter_resolution_unavailable" | "bridge_timeout" = "test_filter_resolution_unavailable";
  lookalikeReadinessFailure = false;
  discoveryMetadataError = false;
  discoveryMetadataErrorCode: "bridge_protocol_error" | "bridge_timeout" = "bridge_protocol_error";
  testRunCount = 0;
  runMetadataFailures = 0;
  constructor(readonly inner: BridgeClient, readonly runDescriptor: CommandDescriptor) {
    runDescriptor.feature_flag = "build_test_automation";
    runDescriptor.wrapper_wait = { mode: "wait", wait_for_test_run_terminal_ms: {
      min_ms: 1, max_ms: 295_000, deadline_scope: "start_and_terminal_poll", response_mode: "compact", dry_run: false,
      compile_readiness_contract: TEST_RUN_COMPILE_READINESS_CONTRACT,
    } };
  }
  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    this.onDescribe?.(selector);
    if (selector === "command:test.run" && this.runMetadataFailures > 0) {
      this.runMetadataFailures--;
      throw new BridgeRequestError("bridge_timeout", { endpoint: "/commands", bridge_host: record.bridge_host, bridge_port: record.bridge_port, failure_kind: "timeout", bridge_request_issued: false });
    }
    if (selector === "command:test.list" && this.discoveryMetadataError) throw new BridgeRequestError(this.discoveryMetadataErrorCode, { endpoint: "/commands", bridge_host: record.bridge_host, bridge_port: record.bridge_port, failure_kind: this.discoveryMetadataErrorCode === "bridge_timeout" ? "timeout" : "response_shape", protocol_reason: this.scanReason });
    if (["command:test.run", "command:test.list", "command:compile.wait_ready"].includes(selector)) return Promise.resolve({
      schema_version: 1, project: record.project, instance_id: record.instance_id, catalog_generation: record.catalog_generation,
      commands_hash: record.commands_hash, command: selector === "command:test.run" ? this.runDescriptor : selector === "command:test.list" ? this.listDescriptor : this.helperDescriptor,
      ...(selector === "command:compile.wait_ready" ? this.metadataOverrides : {}),
    });
    return this.inner.describe(record, selector, transport);
  }
  async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.beforeIssue?.(request.command);
    if (transport?.prepareRequest) await transport.prepareRequest();
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    this.requests.push(request);
    this.invokedCommands.push(request.command);
    this.onInvoke?.(request.command);
    if (request.command === "compile.wait_ready") {
      if (this.readReloading) return Promise.resolve({ status: "error", error: { code: "unity_reloading", message: "Fixture reload" } });
      if (this.readFailure) throw new BridgeRequestError("bridge_timeout", { endpoint: "/invoke", bridge_host: record.bridge_host, bridge_port: record.bridge_port, failure_kind: "timeout", bridge_request_issued: true });
      return Promise.resolve({ status: "ok", data: this.readiness });
    }
    if (request.command === "test.list" && this.discoveryError) return Promise.resolve({ status: "error", error: { code: this.discoveryErrorCode, message: this.scanReason, details: { scan_reason: this.scanReason,
      ...(this.lookalikeReadinessFailure ? { test_run_compile_readiness_preflight: { contract: TEST_RUN_COMPILE_READINESS_CONTRACT, reason: "readiness_read_failed" } } : {}) } } });
    if (request.command === "test.list") return Promise.resolve({ status: "ok", data: {
      status: this.discoveryStatus, mode: request.args.mode, assembly: request.args.assembly ?? "", discovery_generation: this.discoveryStatus === "ready" ? 17 : null,
      scan_reason: this.discoveryStatus === "ready" ? "" : this.scanReason, tests: [],
    } });
    if (request.command === "test.run") {
      this.testRunCount++;
      return Promise.resolve({ status: "ok", data: { operation_id: request.args._test_run_operation_id, run_id: request.args._test_run_operation_id, status: "completed", terminal: true } });
    }
    return this.inner.invoke(record, request, transport);
  }
  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> { return this.inner.logs(record, kind, limit, transport); }
}

function makeFixture(timeoutMs: number, record = heartbeat(), artifactCapable = false) {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-133-"));
  tempDirs.push(instancesDir);
  writeHeartbeat(instancesDir, record);
  let currentMs = startedAtMs;
  const now = () => new Date(currentMs);
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now }),
    bridge: new FakeUnityBridgeClient([testRunCommand(timeoutMs, artifactCapable), testListCommand()]),
    ...(artifactCapable ? { testRunOperationStore: new TestRunOperationStore(path.join(instancesDir, "operations"), { now }) } : {}),
    now,
    sleepMs: async (ms) => { currentMs += ms; },
  };
  if (artifactCapable) context.testRunOperationPublisher = inlineTestRunOperationPublisher(() => context.testRunOperationStore!);
  return { context, instancesDir, now, advance: (ms: number) => { currentMs += ms; } };
}

function callTestRun(context: DispatcherContext, args: Record<string, unknown>, requestId = "pr-133", project = "game") {
  return dispatchTool("sgg_unity_call", { project, command: "test.run", args_json: JSON.stringify(args), mode: "wait", request_id: requestId }, context);
}

function writeHeartbeat(instancesDir: string, record: HeartbeatRecord): void {
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-old",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    package_version: "2026.8.29-1",
    aliases: [],
    unity_version: "6000.5.3f1",
    unity_pid: 12345,
    unity_process_start_utc: "2026-08-18T12:00:00.000Z",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-a",
    catalog_status: "ready",
    catalog_generation: 8,
    heartbeat_generation: 41,
    compilation_generation: 5,
    completed_compilation_generation: 5,
    last_compilation_started_utc: "2026-08-18T13:58:00.000Z",
    last_compilation_finished_utc: "2026-08-18T13:58:05.000Z",
    last_heartbeat: new Date(startedAtMs).toISOString(),
    ...overrides,
  };
}

function baseCommand(id: string, risk: CommandDescriptor["risk"], timeoutMs: number, schema: Record<string, unknown>): CommandDescriptor {
  return {
    id,
    title: id,
    description: id,
    package: "com.sungeargames.unity-mcp",
    package_version: "2026.8.29-1",
    risk,
    idempotency: risk === "read" ? "safe" : "unsafe",
    timeout_ms: timeoutMs,
    schema,
    source: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    trust: "builtin",
  };
}

function testRunCommand(timeoutMs: number, artifactCapable = false): CommandDescriptor {
  return {
    ...baseCommand("test.run", "execute", timeoutMs, {
      type: "object",
      properties: {
        mode: { type: "string", enum: ["editmode", "playmode"] },
        filter: { type: "string", maxLength: 160 },
        assembly: { type: "string", maxLength: 160 },
        response_mode: { type: "string", enum: ["full", "compact"] },
      },
      additionalProperties: false,
    }),
    tags: ["test", "automation", "test_run_operation_correlated", ...(artifactCapable ? ["test_run_artifact_v3"] : [])],
  };
}

function testListCommand(): CommandDescriptor {
  return baseCommand("test.list", "read", 10_000, {
    type: "object",
    properties: {
      query: { type: "string", maxLength: 128 },
      mode: { type: "string", enum: ["editmode", "playmode"] },
      assembly: { type: "string", maxLength: 160 },
      cursor: { type: "integer", minimum: 0 },
      max_results: { type: "integer", minimum: 1, maximum: 500 },
      response_mode: { type: "string", enum: ["full", "compact"] },
    },
    additionalProperties: false,
  });
}

class DiscoveryBridge implements BridgeClient {
  readonly invokedCommands: string[] = [];
  readonly invokedInstances: string[] = [];
  testRunCount = 0;
  testRunArgs: Record<string, unknown> = {};
  discoveryPolls = 0;
  describeFailures: number;

  constructor(readonly inner: BridgeClient, readonly discoveryStatus: (poll: number) => "pending" | "ready" | "unavailable" | "incomplete", describeFailures = 0, readonly returnedMode?: string, readonly readyTotal = 39, readonly runnableLeafCount = readyTotal) {
    this.describeFailures = describeFailures;
  }

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    if (selector === "command:test.run" && this.describeFailures > 0) {
      this.describeFailures -= 1;
      throw new BridgeRequestError("bridge_timeout", { endpoint: "/commands", bridge_host: record.bridge_host, bridge_port: record.bridge_port, failure_kind: "timeout", bridge_request_issued: false });
    }
    return this.inner.describe(record, selector, transport);
  }

  async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.invokedCommands.push(request.command);
    this.invokedInstances.push(record.instance_id);
    if (transport?.prepareRequest) await transport.prepareRequest();
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    if (request.command === "test.list") {
      this.discoveryPolls += 1;
      const status = this.discoveryStatus(this.discoveryPolls);
      return Promise.resolve({
        status: "ok",
        data: {
          status,
          mode: this.returnedMode ?? request.args.mode,
          assembly: request.args.assembly ?? "",
          discovery_generation: status === "ready" ? 17 : null,
          total_available: status === "ready" ? this.readyTotal : null,
          runnable_leaf_count: status === "ready" ? this.runnableLeafCount : null,
          total_available_status: status === "ready" ? "exact" : status,
          coverage_status: status === "ready" ? "complete" : status,
          scan_reason: status === "pending" ? "retrieve_test_list_pending" : status === "ready" ? "" : "retrieve_test_list_timeout",
          tests: [],
        },
      });
    }
    if (request.command === "test.run") {
      this.testRunCount += 1;
      this.testRunArgs = request.args;
      const operationId = String(request.args._test_run_operation_id);
      return Promise.resolve({ status: "ok", data: { operation_id: operationId, run_id: operationId, status: "running" } });
    }
    return this.inner.invoke(record, request, transport);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}
