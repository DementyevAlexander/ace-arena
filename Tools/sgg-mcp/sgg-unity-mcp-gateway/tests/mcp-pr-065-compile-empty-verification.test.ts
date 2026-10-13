import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { inspect } from "node:util";

import { afterEach, describe, expect, it, vi } from "vitest";

import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { InMemoryAuditSink } from "../src/observability/audit.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { ProjectQueueManager } from "../src/routing/project-queue.js";
import { canonicalJsonStringify } from "../src/security/canonical-json.js";

const initialNow = Date.parse("2026-07-13T09:00:00.000Z");
const tempDirs: string[] = [];

afterEach(() => {
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("MCP-PR-065 compile.refresh_and_wait empty-body verification", () => {
  it.each([
    ["missing", undefined],
    ["pending", { state: "pending", requested: true, request_issued: true, started: false, finished: false }],
    ["mismatch", { state: "verified", operation_id: "b".repeat(32), requested: true, request_issued: true, started: true, finished: true }],
  ] as const)("rejects ordinary successful response with %s barrier", async (_label, barrier) => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "ok",
      data: {
        ready: true,
        editor_ready: true,
        compilable: true,
        timed_out: false,
        has_compile_errors: false,
        compile_error_count: 0,
        ...(barrier ? { compile_barrier: { required: true, operation_id: request.args._compile_barrier_expectation_id, ...barrier } } : {}),
      },
    }));

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("compile_freshness_unverified");
  });

  it("accepts ordinary successful response only with matching verified barrier", async () => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, cleanResult, cleanResult);

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("ok");
    if (result.status === "ok") expect(result.data).toMatchObject({ ready: true, compile_barrier: { state: "verified", finished: true } });
  });

  it("accepts ordinary compilation_failed only with matching finished barrier", async () => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "error",
      error: {
        code: "compilation_failed",
        message: "Compilation failed",
        details: {
          ready: false,
          has_compile_errors: true,
          compile_error_count: 1,
          terminal_compile_failure_without_reload: true,
          compile_barrier: failedBarrier(request),
        },
      },
    }));

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("compilation_failed");
      expect(result.error.details).toMatchObject({
        original_command_completed: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      });
    }
  });

  it.each(["unmarked", "wrong-source"] as const)("rejects direct %s compiler errors despite a matching operation id", async (variant) => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, (request) => {
      const barrier = failedBarrier(request);
      return {
        status: "error",
        error: {
          code: "compilation_failed",
          message: "Compilation failed",
          details: {
            ready: false,
            has_compile_errors: true,
            compile_error_count: 1,
            terminal_compile_failure_without_reload: variant === "wrong-source",
            compile_barrier: {
              ...barrier,
              terminal_compile_failure_without_reload: variant === "wrong-source",
              ...(variant === "wrong-source" ? { source_instance_id: "other-instance" } : {}),
            },
          },
        },
      };
    });

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("compile_freshness_unverified");
      expect(result.error.details).toMatchObject({
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      });
    }
  });

  it.each([
    ["missing reload-start flag", { reload_started: undefined }],
    ["missing post-reload flag", { post_reload_observed: undefined }],
    ["mismatched terminal error count", { terminal_compile_error_count: 2 }],
    ["stale terminal observation", { terminal_compile_observed_utc: "2026-07-13T09:10:00.030Z" }],
  ] as const)("rejects direct terminal failure with %s", async (_label, barrierOverrides) => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "error",
      error: {
        code: "compilation_failed",
        message: "Compilation failed",
        details: {
          ready: false,
          has_compile_errors: true,
          compile_error_count: 1,
          terminal_compile_failure_without_reload: true,
          compile_barrier: { ...failedBarrier(request), ...barrierOverrides },
        },
      },
    }));

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("compile_freshness_unverified");
  });

  it.each(["game-old", "game", "Game"])("verifies ready-clean through the replacement instance for selector %s without repeating the write", async (selector) => {
    const fixture = makeFixture([heartbeat()]);
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult);
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, selector);

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(1);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        recovered_from_empty_bridge_response: true,
        original_response_lost: true,
        recovery_disposition: "verified_complete_no_replay",
        original_command_completed: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
        compile_refresh_verification: {
          verification_status: "ready_clean",
          verification_instance_role: "replacement",
          attempt_count: 1,
          attempt_budget: 1181,
          poll_interval_ms: 250,
          verification_timeout_ms: 295_000,
          verification_elapsed_ms: 0,
          read_request_count: 1,
          write_request_count: 1,
          write_command_repeated: false,
          write_replay_allowed: false,
          command_replayed: false,
          selector_strategy: "failed_heartbeat_canonical_project_identity",
          failed_instance: { instance_id: "game-old", bridge_port: 18142, heartbeat_age_ms: 0 },
          verified_compile_result: { ready: true, compile_error_count: 0 },
        },
      });
      expect(result.next_valid_actions).toEqual(["do_not_repeat_compile.refresh_and_wait", "continue_after_verified_ready_clean", "optionally_call_compile.status"]);
    }
  });

  it("carries caller-declared expected scripts into replacement verification", async () => {
    const fixture = makeFixture([heartbeat()]);
    let readArgs: Record<string, unknown> | undefined;
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => {
      readArgs = request.args;
      const response = cleanResult(request);
      if (response.status !== "ok") return response;
      return {
        ...response,
        data: {
          ...(response.data as Record<string, unknown>),
          expected_script_imports: verifiedExpectedScriptEvidence("Assets/Tests/PlayMode/Expected.cs"),
        },
      };
    });
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { expected_script_paths: ["Assets/Tests/PlayMode/Expected.cs"] });

    expect(readArgs).toMatchObject({ _expected_script_paths: ["Assets/Tests/PlayMode/Expected.cs"], _require_explicit_compile_barrier: true });
    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        ready: true,
        expected_script_imports: verifiedExpectedScriptEvidence("Assets/Tests/PlayMode/Expected.cs"),
        compile_refresh_verification: { verification_status: "ready_clean" },
      });
    }
  });

  it("retains bounded expected-script evidence in compact success", async () => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok") return response;
      return {
        ...response,
        data: {
          ...(response.data as Record<string, unknown>),
          expected_script_imports: verifiedExpectedScriptEvidence("Assets/Tests/PlayMode/Expected.cs"),
        },
      };
    });

    const result = await callRefresh(fixture.context, "game", { response_mode: "compact", expected_script_paths: ["Assets/Tests/PlayMode/Expected.cs"] });

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        response_mode: "compact",
        result: "ready_clean",
        expected_script_imports: {
          required: true,
          verified: true,
          checked_count: 1,
          verified_count: 1,
          missing_count: 0,
          items: [expectedScriptEvidenceItem("Assets/Tests/PlayMode/Expected.cs")],
        },
      });
      expect((result.data as Record<string, unknown>).expected_script_imports).not.toHaveProperty("failures");
    }
  });

  it("retains complete per-path GUID, main-asset, and compiler-source proof", async () => {
    const fixture = makeFixture([heartbeat()]);
    const paths = ["Assets/Tests/PlayMode/First.cs", "Assets/Tests/PlayMode/Second.cs"];
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok") return response;
      return {
        ...response,
        data: {
          ...(response.data as Record<string, unknown>),
          expected_script_imports: expectedScriptEvidence(paths.map((item) => expectedScriptEvidenceItem(item))),
        },
      };
    });

    const result = await callRefresh(fixture.context, "game", { expected_script_paths: paths });

    expect(result.status).toBe("ok");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        expected_script_imports: {
          verified: true,
          checked_count: 2,
          verified_count: 2,
          missing_count: 0,
          items: paths.map((assetPath) => ({
            asset_path: assetPath,
            verified: true,
            guid_valid: true,
            mono_script_main_asset: true,
            compiler_source_present: true,
            failure_causes: [],
          })),
        },
      });
    }
  });

  it("accepts 64 complete expected-script proofs and rejects 65 before dispatch", async () => {
    const paths = Array.from({ length: 65 }, (_, index) => `Assets/Tests/PlayMode/Expected${index}.cs`);
    const acceptedFixture = makeFixture([heartbeat()]);
    acceptedFixture.context.bridge = new CompileVerificationBridge(acceptedFixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok") return response;
      return {
        ...response,
        data: {
          ...(response.data as Record<string, unknown>),
          expected_script_imports: expectedScriptEvidence(paths.slice(0, 64).map((item) => expectedScriptEvidenceItem(item))),
        },
      };
    });

    const accepted = await callRefresh(acceptedFixture.context, "game", { expected_script_paths: paths.slice(0, 64) });

    expect(accepted.status).toBe("ok");
    if (accepted.status === "ok") {
      expect(accepted.data).toMatchObject({ expected_script_imports: { checked_count: 64, verified_count: 64, missing_count: 0 } });
      const evidence = (accepted.data as Record<string, unknown>).expected_script_imports as Record<string, unknown>;
      expect(evidence.items).toHaveLength(64);
    }

    const rejectedFixture = makeFixture([heartbeat()]);
    const rejectedBridge = new DirectCompileResponseBridge(rejectedFixture.context.bridge as BridgeClient, cleanResult);
    rejectedFixture.context.bridge = rejectedBridge;
    const rejected = await callRefresh(rejectedFixture.context, "game", { expected_script_paths: paths });

    expect(rejected.status).toBe("error");
    expect(rejectedBridge.writeRequests).toBe(0);
    if (rejected.status === "error") {
      expect(rejected.error.code).toBe("schema_validation_failed");
      expect(rejected.error.details?.invalid_arguments).toMatchObject({
        issues: [expect.objectContaining({ path: ["expected_script_paths"], issue_code: "array_too_long" })],
      });
    }
  });

  it.each([
    "Packages/com.example/Expected.cs",
    "Assets/Tests/PlayMode/Expected.asmdef",
  ])("rejects non-Assets C# expectation %s with an indexed descriptor issue", async (pathValue) => {
    const fixture = makeFixture([heartbeat()]);
    const bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, cleanResult);
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { expected_script_paths: [pathValue] });

    expect(result.status).toBe("error");
    expect(bridge.writeRequests).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("schema_validation_failed");
      expect(result.error.details?.invalid_arguments).toMatchObject({
        origin: "descriptor",
        issues: [expect.objectContaining({ path: ["expected_script_paths", 0], issue_code: "pattern_mismatch" })],
        rejected_values_reflected: false,
      });
    }
  });

  it("fails closed when direct success omits expected-script evidence", async () => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, cleanResult);

    const result = await callRefresh(fixture.context, "game", { expected_script_paths: ["Assets/Tests/PlayMode/Expected.cs"] });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("compile_freshness_unverified");
      expect(result.error.details).toMatchObject({
        reason: "expected_script_evidence_incomplete",
        expected_script_count: 1,
        write_command_repeated: false,
        write_replay_allowed: false,
      });
    }
  });

  it.each([
    ["unverified evidence", missingExpectedScriptEvidence("Assets/Tests/PlayMode/Expected.cs")],
    ["malformed evidence", { required: true, verified: true, checked_count: 0, missing_count: 0, failures: [] }],
  ] as const)("fails closed when direct success contains %s", async (_label, evidence) => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok") return response;
      return { ...response, data: { ...(response.data as Record<string, unknown>), expected_script_imports: evidence } };
    });

    const result = await callRefresh(fixture.context, "game", { expected_script_paths: ["Assets/Tests/PlayMode/Expected.cs"] });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        reason: "expected_script_evidence_incomplete",
        write_command_repeated: false,
        write_replay_allowed: false,
      });
    }
  });

  it("sanitizes a direct expected-script failure as unverified without replay", async () => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new DirectCompileResponseBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "error",
      error: {
        code: "compile_freshness_unverified",
        message: "One or more expected project scripts were not imported by Unity",
        details: {
          ready: false,
          editor_ready: true,
          compilable: true,
          timed_out: false,
          has_compile_errors: false,
          compile_error_count: 0,
          compile_barrier: verifiedBarrier(request),
          reason: "expected_script_not_imported",
          expected_script_imports: missingExpectedScriptEvidence("Assets/Tests/PlayMode/Missing.cs"),
        },
      },
    }));

    const result = await callRefresh(fixture.context, "game", { expected_script_paths: ["Assets/Tests/PlayMode/Missing.cs"] });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        reason: "expected_script_evidence_unverified",
        reported_reason: "expected_script_not_imported",
        post_reload_stability_verified: false,
        write_command_repeated: false,
        write_replay_allowed: false,
      });
    }
  });

  it.each([
    ["missing evidence", undefined],
    ["under-counted evidence", { required: true, verified: true, checked_count: 0, missing_count: 0, failures: [] }],
    ["undeclared failure", missingExpectedScriptEvidence("Assets/Tests/PlayMode/Other.cs")],
    ["contradictory failure", { required: true, verified: false, checked_count: 1, missing_count: 1, failures: [{ asset_path: "Assets/Tests/PlayMode/Expected.cs", guid_present: true, mono_script_loaded: true }] }],
    ["oversized failure list", { required: true, verified: false, checked_count: 1, missing_count: 1, failures: Array.from({ length: 65 }, () => ({ asset_path: "Assets/Tests/PlayMode/Expected.cs", guid_present: false, mono_script_loaded: false })) }],
  ] as const)("fails closed on replacement %s", async (_label, evidence) => {
    const fixture = makeFixture([heartbeat()], 100);
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok" || !evidence) return response;
      return { ...response, data: { ...(response.data as Record<string, unknown>), expected_script_imports: evidence } };
    });

    const result = await callRefresh(fixture.context, "game", {
      timeout_ms: 100,
      poll_interval_ms: 50,
      expected_script_paths: ["Assets/Tests/PlayMode/Expected.cs"],
    });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).not.toMatchObject({ reason: "expected_script_not_imported", post_reload_stability_verified: true });
      expect(result.error.details).toMatchObject({
        write_command_repeated: false,
        write_replay_allowed: false,
        compile_refresh_verification: { verification_status: "unavailable" },
      });
    }
  });

  it("preserves a replacement-proven expected script import failure without replay", async () => {
    const fixture = makeFixture([heartbeat()]);
    const audit = new InMemoryAuditSink();
    fixture.context.audit = audit;
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok") return response;
      return {
        status: "error",
        error: {
          code: "compile_freshness_unverified",
          message: "One or more expected project scripts were not imported by Unity",
          details: {
            ...(response.data as Record<string, unknown>),
            ready: false,
            status: "expected_script_not_imported",
            reason: "expected_script_not_imported",
            expected_script_imports: missingExpectedScriptEvidence("Assets/Tests/PlayMode/Missing.cs"),
          },
        },
      };
    });
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { expected_script_paths: ["Assets/Tests/PlayMode/Missing.cs"] });

    expect(result.status).toBe("error");
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(1);
    if (result.status === "error") {
      expect(result.error.code).toBe("compile_freshness_unverified");
      expect(result.error.details).toMatchObject({
        reason: "expected_script_not_imported",
        original_command_completed: true,
        post_reload_stability_verified: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
        expected_script_imports: {
          required: true,
          verified: false,
          checked_count: 1,
          missing_count: 1,
          failures: [{ asset_path: "Assets/Tests/PlayMode/Missing.cs", guid_present: false, mono_script_loaded: false }],
        },
        compile_refresh_verification: { verification_status: "expected_script_not_imported", read_request_count: 1, write_request_count: 1 },
      });
    }
    expect(audit.events[0]).toMatchObject({
      compile_verification_attempted: true,
      compile_verification_status: "expected_script_not_imported",
      write_command_repeated: false,
    });
  });

  it.each([
    ["current instance", false, {}],
    ["wrong source", true, { source_instance_id: "other-source", reload_source_instance_id: "other-source" }],
    ["conflicting compile errors", true, { has_compile_errors: true, compile_error_count: 1 }],
  ] as const)("rejects uncorrelated expected-script failure from %s", async (_label, includeReplacement, overrides) => {
    const fixture = makeFixture([heartbeat()], 100, includeReplacement);
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok") return response;
      const data = response.data as Record<string, unknown>;
      const barrier = data.compile_barrier as Record<string, unknown>;
      const barrierOverrides = "source_instance_id" in overrides
        ? { source_instance_id: overrides.source_instance_id, reload_source_instance_id: overrides.reload_source_instance_id }
        : {};
      return {
        status: "error",
        error: {
          code: "compile_freshness_unverified",
          message: "One or more expected project scripts were not imported by Unity",
          details: {
            ...data,
            ...overrides,
            ready: false,
            reason: "expected_script_not_imported",
            compile_barrier: { ...barrier, ...barrierOverrides },
            expected_script_imports: missingExpectedScriptEvidence("Assets/Tests/PlayMode/Missing.cs"),
          },
        },
      };
    });

    const result = await callRefresh(fixture.context, "game", {
      timeout_ms: 100,
      poll_interval_ms: 50,
      expected_script_paths: ["Assets/Tests/PlayMode/Missing.cs"],
    });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).not.toMatchObject({ reason: "expected_script_not_imported", post_reload_stability_verified: true });
      expect(result.error.details).toMatchObject({ write_command_repeated: false, write_replay_allowed: false });
    }
  });

  it.each([
    ["wrong source", { source_instance_id: "other-source", reload_source_instance_id: "other-source" }],
    ["wrong replacement", { replacement_instance_id: "other-replacement" }],
    ["stale reload edge", { post_reload_observed_utc: "2026-07-13T09:10:00.040Z" }],
  ] as const)("fails closed on %s clean barrier evidence", async (_label, barrierOverrides) => {
    const fixture = makeFixture([heartbeat()], 100);
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => {
      const response = cleanResult(request);
      if (response.status !== "ok" || typeof response.data !== "object" || response.data === null || Array.isArray(response.data)) return response;
      const data = response.data as Record<string, unknown>;
      const barrier = data.compile_barrier as Record<string, unknown>;
      return { ...response, data: { ...data, compile_barrier: { ...barrier, ...barrierOverrides } } };
    });
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { timeout_ms: 100, poll_interval_ms: 50 });

    expect(result.status).toBe("error");
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(1);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.error.details).toMatchObject({
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
      });
    }
  });

  it.each([
    ["missing process start", { unity_process_start_utc: undefined }],
    ["different process", { unity_pid: 54321, unity_process_start_utc: "2026-07-13T08:05:00.000Z" }],
  ] as const)("fails closed on replacement from %s", async (_label, replacementOverrides) => {
    const fixture = makeFixture([heartbeat()], 100, false);
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult, () => {
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
      writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, ...replacementOverrides }));
    });
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { timeout_ms: 100, poll_interval_ms: 50 });

    expect(result.status).toBe("error");
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(0);
    if (result.status === "error") expect(result.error.code).toBe("bridge_protocol_error");
  });

  it("holds one queue admission and deadline while an exact stale selector rotates to a delayed replacement", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    const queues = new CountingProjectQueueManager();
    fixture.context.queues = queues;
    const replacement = heartbeat({ instance_id: "game-new", bridge_port: 18143, token: "replacement-secret", last_heartbeat: new Date(initialNow + 100).toISOString() });
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult, () => {
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
    });
    fixture.context.bridge = bridge;
    let sleeps = 0;
    fixture.context.sleepMs = async (ms) => {
      fixture.clock.value += ms;
      sleeps += 1;
      if (sleeps === 2) writeHeartbeat(fixture.instancesDir, replacement);
    };

    const result = await callRefresh(fixture.context, "game-old", { timeout_ms: 1_000, poll_interval_ms: 50 });

    expect(result.status).toBe("ok");
    expect(queues.admissions).toBe(1);
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    expect(bridge.instanceIds).toEqual(["game-old", "game-new"]);
    expect(bridge.deadlines[1]).toBe(bridge.deadlines[0]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        compile_refresh_verification: {
          verification_status: "ready_clean",
          verification_instance_role: "replacement",
          attempt_count: 3,
          attempt_budget: 5_901,
          poll_interval_ms: 50,
          verification_timeout_ms: 295_000,
          verification_elapsed_ms: 100,
          read_request_count: 1,
          replacement_instance: { instance_id: "game-new", bridge_port: 18143 },
        },
      });
      expect(JSON.stringify(result)).not.toContain("replacement-secret");
    }
  });

  it("verifies a dispatched bridge_unreachable compile refresh through one exact replacement read without replay", async () => {
    const fixture = makeFixture([heartbeat()]);
    const bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      cleanResult,
      undefined,
      bridgeUnreachableError(true),
    );
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { timeout_ms: 5_000, poll_interval_ms: 50 });

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(1);
    expect(() => canonicalJsonStringify(result)).not.toThrow();
    const rendered = JSON.stringify(result);
    expect(rendered).not.toContain("old-secret");
    expect(rendered).not.toContain("C:/private/project");
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        recovered_from_bridge_unreachable: true,
        original_response_lost: true,
        recovery_disposition: "verified_complete_no_replay",
        original_command_completed: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        compile_refresh_verification: {
          verification_status: "ready_clean",
          stability_reason: "bridge_unreachable",
          verification_instance_role: "replacement",
          read_request_count: 1,
          write_request_count: 1,
          write_command_repeated: false,
        },
      });
    }
  });

  it("returns verified compilation_failed with a bounded bridge_unreachable recovery marker", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    const bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      (request) => ({
        status: "error",
        error: {
          code: "compilation_failed",
          message: "Compilation failed after bridge reload",
          details: {
            ready: false,
            editor_ready: true,
            compilable: false,
            has_compile_errors: true,
            compile_error_count: 1,
            terminal_compile_failure_without_reload: true,
            compile_barrier: failedBarrier(request),
            compile_diagnostics: { errors: [{ severity: "error", code: "CS1002", line: 8, column: 3 }] },
          },
        },
      }),
      undefined,
      bridgeUnreachableError(true),
    );
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { timeout_ms: 5_000, poll_interval_ms: 50 });

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(1);
    expect(() => canonicalJsonStringify(result)).not.toThrow();
    if (result.status === "error") {
      expect(result.error.code).toBe("compilation_failed");
      expect(result.error.details).toMatchObject({
        recovered_from_bridge_unreachable: true,
        has_compile_errors: true,
        compile_error_count: 1,
        write_command_repeated: false,
        write_replay_allowed: false,
        command_replayed: false,
        compile_refresh_verification: {
          verification_status: "compilation_failed",
          stability_reason: "bridge_unreachable",
          verification_instance_role: "current",
          post_reload_required: false,
          original_error: { code: "bridge_unreachable" },
          write_command_repeated: false,
          write_replay_allowed: false,
          command_replayed: false,
        },
      });
    }
  });

  it("does not promote compiler errors without post-reload barrier observation", async () => {
    const fixture = makeFixture([heartbeat()]);
    const bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      (request) => ({
        status: "error",
        error: {
          code: "compilation_failed",
          message: "Compilation failed before replacement observation",
          details: {
            ready: false,
            compilable: false,
            has_compile_errors: true,
            compile_error_count: 1,
            compile_barrier: { ...verifiedBarrier(request), post_reload_observed: false, post_reload_observed_utc: "" },
          },
        },
      }),
      undefined,
      bridgeUnreachableError(true),
    );
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game", { timeout_ms: 5_000, poll_interval_ms: 50 });

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(1);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_unreachable");
      expect(result.error.details).not.toHaveProperty("recovered_from_bridge_unreachable");
      expect(result.error.details).toMatchObject({
        post_reload_stability_verified: false,
        write_command_repeated: false,
        write_replay_allowed: false,
        compile_refresh_verification: {
          verification_status: "unavailable",
          stability_reason: "bridge_unreachable",
          verified_compile_result: {
            has_compile_errors: true,
            compile_error_count: 1,
            compile_barrier: { state: "verified", finished: true, post_reload_observed: false },
          },
        },
      });
    }
  });

  it("preserves dispatched bridge_unreachable uncertainty when no replacement becomes available before the deadline", async () => {
    const fixture = makeFixture([heartbeat()], 100, false);
    const bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      cleanResult,
      undefined,
      bridgeUnreachableError(true),
    );
    fixture.context.bridge = bridge;
    fixture.context.sleepMs = async (ms) => { fixture.clock.value += ms; };

    const result = await callRefresh(fixture.context, "game", { timeout_ms: 50, poll_interval_ms: 50 });

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready", "compile.wait_ready"]);
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(2);
    expect(() => canonicalJsonStringify(result)).not.toThrow();
    if (result.status === "error") {
      expect(result.error).toMatchObject({
        code: "bridge_unreachable",
        message: "bridge_unreachable",
        details: {
          endpoint: "/invoke",
          failure_kind: "connection",
          delivery_state: "dispatched",
          write_command_repeated: false,
          write_replay_allowed: false,
          compile_refresh_verification: {
            verification_status: "timeout",
            stability_reason: "bridge_unreachable",
            read_request_count: 2,
            write_request_count: 1,
            write_command_repeated: false,
          },
        },
      });
    }
  });

  it("does not verify an unissued bridge_unreachable compile refresh", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    const bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      cleanResult,
      undefined,
      bridgeUnreachableError(false),
    );
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait"]);
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(0);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_unreachable");
      expect(result.error.details).toMatchObject({ command_bridge_request_issued: false });
      expect(result.error.details).not.toHaveProperty("compile_refresh_verification");
    }
  });

  it("expires the compile refresh deadline in the project queue without dispatching or verifying", async () => {
    let releaseBlocker!: () => void;
    const fixture = makeFixture([heartbeat()], 50);
    const queues = new CountingProjectQueueManager();
    fixture.context.queues = queues;
    const invoke = vi.spyOn(fixture.context.bridge!, "invoke");
    const blocker = queues.run("game", () => new Promise<void>((resolve) => { releaseBlocker = resolve; }));
    await Promise.resolve();

    const pending = callRefresh(fixture.context, "game");
    for (let attempt = 0; attempt < 20 && queues.admissions < 2; attempt += 1) {
      await new Promise<void>((resolve) => setImmediate(resolve));
    }
    expect(queues.admissions).toBe(2);
    fixture.clock.value += 51;

    const result = await pending;
    expect(invoke).not.toHaveBeenCalled();
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("command_timeout");
      expect(result.error.details).toMatchObject({
        recovery_contract: "sgg.unity_mcp.compile_refresh_dispatch_timeout.v1",
        timeout_kind: "gateway_project_queue_timeout",
        failure_phase: "project_queue",
        timed_out_work_state: "cancelled_before_start",
        delivery_state: "not_dispatched",
        bridge_request_issued: false,
        late_execution_possible: false,
        refresh_started: false,
        write_command_repeated: false,
        write_replay_allowed: false,
      });
    }
    releaseBlocker();
    await blocker;
    await queues.run("game", async () => undefined);
    expect(invoke).not.toHaveBeenCalled();
  });

  it("bounds and reports the observed 36-attempt replacement recovery without repeating refresh", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    const replacement = heartbeat({ instance_id: "game-new", bridge_port: 18143, last_heartbeat: new Date(initialNow + 100).toISOString() });
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult, () => {
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
    });
    fixture.context.bridge = bridge;
    let sleeps = 0;
    fixture.context.sleepMs = async (ms) => {
      fixture.clock.value += ms;
      sleeps += 1;
      if (sleeps === 35) writeHeartbeat(fixture.instancesDir, replacement);
    };

    const result = await callRefresh(fixture.context, "game-old", { timeout_ms: 5_000, poll_interval_ms: 50 });

    expect(result.status).toBe("ok");
    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(1);
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        recovery_disposition: "verified_complete_no_replay",
        write_command_repeated: false,
        write_replay_allowed: false,
        compile_refresh_verification: {
          verification_status: "ready_clean",
          attempt_count: 36,
          attempt_budget: 5_901,
          poll_interval_ms: 50,
          verification_timeout_ms: 295_000,
          verification_elapsed_ms: 1_750,
          read_request_count: 1,
          write_request_count: 1,
          write_command_repeated: false,
        },
      });
    }
  });

  it("returns verified compiler errors after the original response is lost", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "error",
      error: {
        code: "compilation_failed",
        message: "Compilation failed",
        details: {
          ready: false,
          editor_ready: true,
          compilable: false,
          has_compile_errors: true,
          compile_error_count: 1,
          terminal_compile_failure_without_reload: true,
          compile_barrier: failedBarrier(request),
          compile_diagnostics: { errors: [{ severity: "error", code: "CS1002", message: "; expected", line: 8, column: 3 }] },
        },
      },
    }));

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("compilation_failed");
      expect(result.error.details).toMatchObject({
        recovered_from_empty_bridge_response: true,
        original_response_lost: true,
        compile_refresh_verification: {
          verification_status: "compilation_failed",
          original_error: { code: "bridge_protocol_error" },
          verified_compile_result: {
            has_compile_errors: true,
            compile_error_count: 1,
            compile_diagnostics: { errors: [{ severity: "error", code: "CS1002", line: 8, column: 3 }] },
          },
        },
      });
      expect(result.next_valid_actions).toEqual(expect.arrayContaining(["call_compile.status_with_sgg_unity_call", "call_diagnostics.recovery.suggest"]));
    }
  });

  it("verifies an exact zero-byte response without Content-Type", async () => {
    const fixture = makeFixture([heartbeat()]);
    const details = emptyDetails();
    delete details.response_content_type;
    const bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      cleanResult,
      undefined,
      new BridgeRequestError("bridge_protocol_error", details),
    );
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
  });

  it("does not turn freshness-unverified read recovery into ready-clean", async () => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "ok",
      data: {
        ready: true,
        editor_ready: true,
        compilable: true,
        timed_out: false,
        has_compile_errors: false,
        compile_error_count: 0,
        compile_barrier: {
          state: "pending",
          required: true,
          operation_id: "a".repeat(32),
          requested: true,
          request_issued: true,
          started: false,
          finished: false,
        },
      },
    }));

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.error.details).toMatchObject({
        compile_refresh_verification: {
          verification_status: "unavailable",
          verified_compile_result: {
            ready: true,
            compile_barrier: { state: "pending", required: true, requested: true, request_issued: true, started: false, finished: false },
          },
        },
      });
    }
  });

  it.each(["not_required", "unknown"])("does not promote %s barrier state to ready-clean", async (state) => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "ok",
      data: {
        ready: true,
        editor_ready: true,
        compilable: true,
        timed_out: false,
        has_compile_errors: false,
        compile_error_count: 0,
        compile_barrier: {
          state,
          required: true,
          operation_id: state === "unknown" ? request.args._compile_barrier_expectation_id : "",
          requested: false,
          request_issued: false,
          started: false,
          finished: false,
        },
      },
    }));

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ compile_refresh_verification: { verification_status: "unavailable" } });
    }
  });

  it("returns typed freshness-unverified when timeout recovery sees no completed barrier", async () => {
    const fixture = makeFixture([heartbeat()]);
    fixture.context.bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      (request) => ({
        status: "error",
        error: {
          code: "compile_freshness_unverified",
          message: "Explicit compilation did not finish",
          details: {
            ready: false,
            compilable: false,
            compile_barrier: {
              state: "pending",
              required: true,
              operation_id: request.args._compile_barrier_expectation_id,
              requested: true,
              request_issued: true,
              started: false,
              finished: false,
            },
          },
        },
      }),
      undefined,
      new BridgeRequestError("bridge_timeout", { endpoint: "/invoke", bridge_host: "127.0.0.1", bridge_port: 18142, failure_kind: "timeout", abort_source: "deadline" }),
    );

    const result = await callRefresh(fixture.context, "game");

    expect(fixture.context.bridge).toMatchObject({ commands: ["compile.refresh_and_wait", "compile.wait_ready"] });
    expect(result.status).toBe("error");
    expect(() => canonicalJsonStringify(result)).not.toThrow();
    if (result.status === "error") {
      expect(result.error.code).toBe("compile_freshness_unverified");
      expect(result.error.details).toMatchObject({
        timeout_recovery_attempted: true,
        verified_after_timeout: false,
        verification_command: "compile.wait_ready",
        compile_barrier: { state: "pending", started: false, finished: false },
      });
      expect(result.error.details?.compile_refresh_verification).toMatchObject({
        original_error: {
          classification: { endpoint: "/invoke", failure_kind: "timeout", abort_source: "deadline" },
        },
      });
    }
  });

  it("surfaces matching verified compiler errors after timeout as compilation_failed", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    fixture.context.bridge = new CompileVerificationBridge(
      fixture.context.bridge as BridgeClient,
      (request) => ({
        status: "error",
        error: {
          code: "compilation_failed",
          message: "Compilation failed after timeout",
          details: {
            ready: false,
            compilable: false,
            has_compile_errors: true,
            compile_error_count: 1,
            terminal_compile_failure_without_reload: true,
            compile_diagnostics: { errors: [{ severity: "error", code: "CS1002", line: 3, column: 4 }] },
            compile_barrier: failedBarrier(request),
          },
        },
      }),
      undefined,
      new BridgeRequestError("bridge_timeout", { endpoint: "/invoke", bridge_host: "127.0.0.1", bridge_port: 18142, failure_kind: "timeout", abort_source: "deadline" }),
    );

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("compilation_failed");
      expect(result.error.details).toMatchObject({
        has_compile_errors: true,
        compile_error_count: 1,
        timeout_recovery_attempted: true,
        verified_after_timeout: false,
        compile_barrier_verified_after_timeout: true,
        original_error_code: "bridge_timeout",
        compile_barrier: { state: "verified", finished: true },
      });
    }
  });

  it("times out under the original deadline when no current or replacement instance remains", async () => {
    const fixture = makeFixture([heartbeat()], 100, false);
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult, () => {
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
    });
    fixture.context.bridge = bridge;
    fixture.context.sleepMs = async (ms) => { fixture.clock.value += ms; };

    const result = await callRefresh(fixture.context, "game-old", { timeout_ms: 50, poll_interval_ms: 50 });

    expect(bridge.writeRequests).toBe(1);
    expect(bridge.readRequests).toBe(0);
    expect(fixture.clock.value).toBe(initialNow + 100);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.error.details).toMatchObject({ compile_refresh_verification: { verification_status: "timeout", attempt_count: 2, read_request_count: 0 } });
    }
  });

  it("fails closed on replacement ambiguity without issuing a read request", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult, () => {
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
      writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new-a", bridge_port: 18143 }));
      writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new-b", bridge_port: 18144 }));
    });
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game-old");

    expect(bridge.commands).toEqual(["compile.refresh_and_wait"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.error.details).toMatchObject({ compile_refresh_verification: { verification_status: "ambiguous", read_request_count: 0 } });
    }
  });

  it("fails closed on replacement catalog drift without issuing a read request", async () => {
    const fixture = makeFixture([heartbeat()], 305_000, false);
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult, () => {
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"));
      writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, commands_hash: "catalog-b" }));
    });
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game");

    expect(bridge.commands).toEqual(["compile.refresh_and_wait"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("bridge_protocol_error");
      expect(result.error.details).toMatchObject({ compile_refresh_verification: { verification_status: "catalog_stale", read_request_count: 0 } });
    }
  });

  it.each([
    ["malformed nonempty", { ...emptyDetails(), response_body_bytes: 1, response_body_empty: false, parse_error_kind: "invalid_json" }],
    ["wrong content type", { ...emptyDetails(), response_content_type: "text/plain" }],
    ["version mismatch evidence", { ...emptyDetails(), expected_protocol_version: 1, received_protocol_version: 2 }],
  ] as const)("does not verify %s failures", async (_label, details) => {
    const fixture = makeFixture([heartbeat()]);
    const bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult, undefined, new BridgeRequestError("bridge_protocol_error", details));
    fixture.context.bridge = bridge;

    const result = await callRefresh(fixture.context, "game");

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait"]);
    if (result.status === "error") expect(result.error.details).not.toHaveProperty("compile_refresh_verification");
  });

  it("writes one terminal success audit with safe verification metadata", async () => {
    const fixture = makeFixture([heartbeat()]);
    const audit = new InMemoryAuditSink();
    fixture.context.audit = audit;
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, cleanResult);

    await callRefresh(fixture.context, "game");

    expect(audit.starts).toHaveLength(1);
    expect(audit.events).toHaveLength(1);
    expect(audit.events[0]).toMatchObject({
      status: "ok",
      command: "compile.refresh_and_wait",
      command_risk: "write",
      bridge_invoke_attempts: 2,
      compile_verification_attempted: true,
      compile_verification_status: "ready_clean",
      compile_verification_attempts: 1,
      compile_verification_read_requests: 1,
      write_command_repeated: false,
    });
  });

  it("structurally excludes free-form diagnostics, paths, credentials, args, and bodies from stringify and inspect", async () => {
    const forbidden = [
      "C:/path with spaces/Assets/Foo.cs",
      "\\\\server\\share with spaces\\Foo.cs",
      "/workspace/private/Foo.cs",
      "/root/private/Foo.cs",
      "/Users/private/Foo.cs",
      "/mnt/c/private/Foo.cs",
      "Authorization: Bearer bearer-secret",
      "Cookie: session=cookie-secret",
      '{"token":"json-token","password":"json-password"}',
      "секрет-凭据-🔒",
      "raw-private-body",
      "client-args-secret",
      "free-form compiler failure message",
    ];
    const fixture = makeFixture([heartbeat({ project_root: "C:/private/project", canonical_project_root: "c:/private/project", token: "old-secret" })], 305_000, false);
    fixture.context.bridge = new CompileVerificationBridge(fixture.context.bridge as BridgeClient, (request) => ({
      status: "error",
      error: {
        code: "compilation_failed",
        message: forbidden[11]!,
        details: {
          has_compile_errors: true,
          compile_error_count: 3,
          terminal_compile_failure_without_reload: true,
          compile_barrier: failedBarrier(request, 3),
          status: forbidden.join(" | "),
          compile_diagnostics: {
            errors: [
              {
                severity: "error",
                code: "CS1002",
                line: 8,
                column: 3,
                file: forbidden[0],
                assembly_path: forbidden[1],
                message: forbidden.join(" | "),
                stack_trace: forbidden.join("\n"),
                source: forbidden[2],
                assembly: forbidden[3],
                args: { value: forbidden[10] },
                body: forbidden[9],
                headers: { authorization: forbidden[6], cookie: forbidden[7] },
              },
              { severity: "warning", code: "UE12345678", line: 10_000_000, column: 1 },
              { severity: "ERROR", code: "../../client-controlled", line: -1, column: 10_000_001, message: forbidden[8] },
            ],
            raw: forbidden,
          },
          response_body: forbidden[9],
          request_args: forbidden[10],
        },
      },
    }));

    const result = await callRefresh(fixture.context, "game");
    const rendered = [JSON.stringify(result), inspect(result, { depth: null })];

    for (const output of rendered) {
      for (const value of [...forbidden, "C:/private/project", "c:/private/project", "old-secret", "../../client-controlled"]) {
        expect(output).not.toContain(value);
      }
    }
    if (result.status === "error") {
      const verification = result.error.details?.compile_refresh_verification as Record<string, unknown>;
      expect(verification).toMatchObject({ verification_status: "compilation_failed" });
      expect(verification.verified_compile_result).toMatchObject({
        has_compile_errors: true,
        compile_error_count: 3,
        compile_barrier: {
          state: "verified",
          required: true,
          operation_id: expect.stringMatching(/^[a-f0-9]{32}$/),
          requested: true,
          request_issued: true,
          started: true,
          finished: true,
        },
        compile_diagnostics: {
          errors: [
            { severity: "error", code: "CS1002", line: 8, column: 3 },
            { severity: "warning", code: "UE12345678", line: 10_000_000, column: 1 },
          ],
        },
      });
    }
  });
});

function makeFixture(records: HeartbeatRecord[], refreshTimeoutMs = 305_000, includeReplacement = true) {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-065-"));
  tempDirs.push(instancesDir);
  const clock = { value: initialNow };
  const commands = [refreshCommand(refreshTimeoutMs), waitReadyCommand()];
  const baseBridge = new FakeUnityBridgeClient(commands);
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => new Date(clock.value) }),
    bridge: includeReplacement
      ? new ReplacementPublishingBridge(baseBridge, () => writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143 })))
      : baseBridge,
    now: () => new Date(clock.value),
  };
  for (const record of records) writeHeartbeat(instancesDir, record);
  return { context, instancesDir, clock };
}

function writeHeartbeat(instancesDir: string, record: HeartbeatRecord): void {
  fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));
}

function callRefresh(context: DispatcherContext, project: string, args: Record<string, unknown> = {}) {
  return dispatchTool("sgg_unity_call", { project, command: "compile.refresh_and_wait", args_json: JSON.stringify(args), mode: "wait", request_id: "pr-065" }, context);
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1,
    instance_id: "game-old",
    project: "game",
    display_name: "Game",
    project_root: "C:/private/project",
    canonical_project_root: "c:/private/project",
    aliases: [],
    unity_version: "6000.4.0f1",
    unity_pid: 12345,
    unity_process_start_utc: "2026-07-13T08:00:00.000Z",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "old-secret",
    status: "ready",
    commands_hash: "catalog-a",
    last_heartbeat: new Date(initialNow).toISOString(),
    ...overrides,
  };
}

function baseCommand(id: string, risk: CommandDescriptor["risk"], timeoutMs: number): CommandDescriptor {
  return { id, title: id, description: id, package: "com.example.test", risk, idempotency: "idempotent", timeout_ms: timeoutMs, schema: { type: "object", additionalProperties: false } };
}

function refreshCommand(timeoutMs: number): CommandDescriptor {
  return {
    ...baseCommand("compile.refresh_and_wait", "write", timeoutMs),
    source: "builtin",
    trust: "builtin",
    package_id: "com.sungeargames.unity-mcp",
    schema: {
      type: "object",
      properties: {
        timeout_ms: { type: "integer", minimum: 1, maximum: 300_000 },
        poll_interval_ms: { type: "integer", minimum: 50, maximum: 5_000 },
        allow_unsaved_scene_changes: { type: "boolean" },
        response_mode: { type: "string", enum: ["full", "compact"] },
        expected_script_paths: {
          type: "array",
          maxItems: 64,
          uniqueItems: true,
          description: "Exact Unity-relative C# script paths under Assets/. Local or Embedded package scripts use package.local_script.import.",
          items: { type: "string", minLength: 1, maxLength: 512, pattern: "^Assets/.+[.][cC][sS]$" },
        },
      },
      additionalProperties: false,
    },
  };
}

function waitReadyCommand(): CommandDescriptor {
  return {
    ...baseCommand("compile.wait_ready", "read", 305_000),
    schema: {
      type: "object",
      properties: {
        timeout_ms: { type: "integer", minimum: 1, maximum: 300_000 },
        poll_interval_ms: { type: "integer", minimum: 50, maximum: 5_000 },
      },
      additionalProperties: false,
    },
  };
}

function expectedScriptEvidenceItem(assetPath: string, options: {
  guidPresent?: boolean;
  guidValid?: boolean;
  monoScriptMainAsset?: boolean;
  compilerSourcePresent?: boolean;
} = {}): Record<string, unknown> {
  const guidValid = options.guidValid ?? true;
  const guidPresent = options.guidPresent ?? guidValid;
  const monoScriptMainAsset = options.monoScriptMainAsset ?? true;
  const compilerSourcePresent = options.compilerSourcePresent ?? true;
  const failureCauses = [
    ...(!guidValid ? ["invalid_guid"] : []),
    ...(!monoScriptMainAsset ? ["mono_script_missing"] : []),
    ...(!compilerSourcePresent ? ["compiler_source_missing"] : []),
  ];
  return {
    asset_path: assetPath,
    verified: guidValid && monoScriptMainAsset && compilerSourcePresent,
    guid_present: guidPresent,
    guid_valid: guidValid,
    mono_script_loaded: monoScriptMainAsset,
    mono_script_main_asset: monoScriptMainAsset,
    compiler_source_present: compilerSourcePresent,
    failure_causes: failureCauses,
  };
}

function expectedScriptEvidence(items: Record<string, unknown>[]): Record<string, unknown> {
  const failures = items.filter((item) => item.verified === false);
  return {
    required: true,
    verified: failures.length === 0,
    checked_count: items.length,
    verified_count: items.length - failures.length,
    missing_count: failures.length,
    items,
    failures,
  };
}

function verifiedExpectedScriptEvidence(assetPath: string): Record<string, unknown> {
  return expectedScriptEvidence([expectedScriptEvidenceItem(assetPath)]);
}

function missingExpectedScriptEvidence(assetPath: string): Record<string, unknown> {
  return expectedScriptEvidence([expectedScriptEvidenceItem(assetPath, {
    guidPresent: false,
    guidValid: false,
    monoScriptMainAsset: false,
    compilerSourcePresent: false,
  })]);
}

function cleanResult(request?: BridgeInvokeRequest): BridgeInvokeResponse {
  return {
    status: "ok",
    data: {
      ready: true,
      editor_ready: true,
      compilable: true,
      timed_out: false,
      has_compile_errors: false,
      compile_error_count: 0,
      compile_barrier: request ? verifiedBarrier(request) : { state: "not_required", required: true },
    },
  };
}

function verifiedBarrier(request: BridgeInvokeRequest): Record<string, unknown> {
  return {
    state: "verified",
    required: true,
    operation_id: request.args._compile_barrier_expectation_id,
    requested: true,
    request_issued: true,
    started: true,
    finished: true,
    source_instance_id: "game-old",
    terminal_compile_result: "clean",
    terminal_compile_error_count: 0,
    terminal_compile_observed_utc: "2026-07-13T09:00:00.030Z",
    terminal_compile_failure_without_reload: false,
    reload_started: true,
    reload_started_utc: "2026-07-13T09:00:00.035Z",
    reload_source_instance_id: "game-old",
    replacement_instance_id: "game-new",
    requested_utc: "2026-07-13T09:00:00.000Z",
    request_issued_utc: "2026-07-13T09:00:00.010Z",
    started_utc: "2026-07-13T09:00:00.020Z",
    finished_utc: "2026-07-13T09:00:00.030Z",
    post_reload_observed: true,
    post_reload_observed_utc: "2026-07-13T09:00:00.040Z",
    observed_generation: 1,
    request_generation: 1,
    started_generation: 1,
    finished_generation: 1,
  };
}

function failedBarrier(request: BridgeInvokeRequest, errorCount = 1): Record<string, unknown> {
  return {
    state: "verified",
    required: true,
    operation_id: request.args._compile_barrier_expectation_id,
    requested: true,
    request_issued: true,
    started: true,
    finished: true,
    source_instance_id: "game-old",
    terminal_compile_result: "failed",
    terminal_compile_error_count: errorCount,
    terminal_compile_observed_utc: "2026-07-13T09:00:00.030Z",
    terminal_compile_failure_without_reload: true,
    reload_started: false,
    reload_started_utc: "",
    reload_source_instance_id: "",
    replacement_instance_id: "",
    requested_utc: "2026-07-13T09:00:00.000Z",
    request_issued_utc: "2026-07-13T09:00:00.010Z",
    started_utc: "2026-07-13T09:00:00.020Z",
    finished_utc: "2026-07-13T09:00:00.030Z",
    post_reload_observed: false,
    post_reload_observed_utc: "",
    observed_generation: 1,
    request_generation: 1,
    started_generation: 1,
    finished_generation: 1,
  };
}

function emptyDetails(): BridgeRequestError["details"] {
  return {
    endpoint: "/invoke",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    http_status: 200,
    failure_kind: "response_parse",
    parse_error_kind: "empty_body",
    response_content_type: "application/json",
    response_body_bytes: 0,
    response_body_empty: true,
    response_body_truncated: false,
  };
}

function bridgeUnreachableError(issued: boolean): BridgeRequestError {
  return new BridgeRequestError("bridge_unreachable", {
    endpoint: "/invoke",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    failure_kind: "connection",
    bridge_request_issued: issued,
  });
}

class CompileVerificationBridge implements BridgeClient {
  readonly commands: string[] = [];
  readonly instanceIds: string[] = [];
  readonly deadlines: Array<number | undefined> = [];
  writeRequests = 0;
  readRequests = 0;

  constructor(
    readonly inner: BridgeClient,
    readonly verification: (request: BridgeInvokeRequest) => BridgeInvokeResponse,
    readonly afterWrite?: (() => void) | undefined,
    readonly writeError = new BridgeRequestError("bridge_protocol_error", emptyDetails()),
  ) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    this.commands.push(request.command);
    this.instanceIds.push(record.instance_id);
    this.deadlines.push(request.deadline_unix_ms);
    if (request.command === "compile.refresh_and_wait") {
      this.writeRequests += 1;
      this.afterWrite?.();
      throw this.writeError;
    }
    if (request.command === "compile.wait_ready") {
      this.readRequests += 1;
      return Promise.resolve(this.verification(request));
    }
    return this.inner.invoke(record, request);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class ReplacementPublishingBridge implements BridgeClient {
  #published = false;

  constructor(readonly inner: BridgeClient, readonly publish: () => void) {}

  async describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    const result = await this.inner.describe(record, selector, transport);
    if (!this.#published && selector === "command:compile.refresh_and_wait") {
      this.#published = true;
      this.publish();
    }
    return result;
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    return this.inner.invoke(record, request, transport);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class DirectCompileResponseBridge implements BridgeClient {
  writeRequests = 0;

  constructor(
    readonly inner: BridgeClient,
    readonly response: (request: BridgeInvokeRequest) => BridgeInvokeResponse,
    readonly verification?: ((request: BridgeInvokeRequest) => BridgeInvokeResponse) | undefined,
  ) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  invoke(record: HeartbeatRecord, request: BridgeInvokeRequest): Promise<BridgeInvokeResponse> {
    if (request.command === "compile.refresh_and_wait") {
      this.writeRequests += 1;
      return Promise.resolve(this.response(request));
    }
    if (request.command === "compile.wait_ready" && this.verification) return Promise.resolve(this.verification(request));
    return this.inner.invoke(record, request);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

class CountingProjectQueueManager extends ProjectQueueManager {
  admissions = 0;

  override run<T>(project: string, task: () => Promise<T>): Promise<T> {
    this.admissions += 1;
    return super.run(project, task);
  }
}
