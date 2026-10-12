import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { BridgeRequestError, FakeUnityBridgeClient, type BridgeClient, type BridgeInvokeRequest, type BridgeInvokeResponse, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";
import { canonicalJsonStringify } from "../src/security/canonical-json.js";

const nowMs = Date.parse("2026-07-16T08:00:00.000Z");
const tempDirs: string[] = [];

afterEach(() => {
  vi.useRealTimers();
  for (const dir of tempDirs.splice(0)) fs.rmSync(dir, { recursive: true, force: true });
});

describe("MCP-PR-083 lifecycle stability", () => {
  it("recovers an admitted compile lifecycle shutdown only through matching replacement verification", async () => {
    const fixture = makeFixture([compileRefreshCommand(), compileWaitReadyCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileResponse = {
      status: "error",
      error: { code: "unity_reloading", message: "Unity bridge stopped the active wait", details: { reason: "bridge_lifecycle_shutdown", write_replay_allowed: false } },
    };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "compile.refresh_and_wait", {});

    expect(result.status).toBe("ok");
    expect(() => canonicalJsonStringify(result)).not.toThrow();
    expect(Reflect.ownKeys(result).every((key) => typeof key === "string")).toBe(true);
    expect(bridge.commands, JSON.stringify(result)).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    expect(bridge.instanceIds).toEqual(["game-old", "game-new"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        post_reload_stability_verified: true,
        recovered_from_lifecycle_shutdown: true,
        original_command_completed: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        compile_refresh_verification: {
          stability_reason: "lifecycle_shutdown",
          post_reload_required: true,
          verification_instance_role: "replacement",
          write_request_count: 1,
          read_request_count: 1,
        },
      });
    }
  });

  it("makes a clean correlated replacement barrier's active compute work primary without replaying refresh", async () => {
    const fixture = makeFixture([compileRefreshCommand(), compileWaitReadyCommand()], heartbeat(), activeComputeHeartbeat());
    fixture.context.sleepMs = async (milliseconds) => { fixture.advanceTime(milliseconds); };
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileResponse = { status: "error", error: { code: "unity_reloading", message: "Bridge stopped", details: { reason: "bridge_lifecycle_shutdown" } } };
    bridge.waitResponseFactory = (operationId) => pendingComputeResponse(operationId);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "compile.refresh_and_wait", {});

    expect(result.status).toBe("error");
    expect(bridge.commands, JSON.stringify(result)).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("background_compilation_pending");
      expect(result.error.details).toMatchObject({
        reason: "background_compilation_pending", original_lifecycle_reason: "lifecycle_shutdown",
        post_reload_stability_verified: true, write_command_repeated: false, write_replay_allowed: false,
        background_compiler_progress: { category: "compute_variant_compile", process_id: 12345, progress_permille: 928 },
        compile_refresh_verification: { verification_status: "background_compilation_pending", verification_instance_role: "replacement", stability_reason: "lifecycle_shutdown", write_request_count: 1, read_request_count: 1 },
      });
    }
  });

  it("keeps lifecycle uncertainty primary when progress is stale, unbound, or merely a log claim", async () => {
    for (const variant of ["different_pid", "stale_progress", "barrier_missing", "stale_catalog", "log_only", "other_error", "package_pending", "script_compiling", "unfinished_generation", "cancellation_true"] as const) {
      const replacement = activeComputeHeartbeat();
      if (variant === "unfinished_generation") {
        replacement.completed_compilation_generation = 7;
        replacement.last_compilation_finished_utc = "";
        replacement.background_compiler_progress_completed_compilation_generation = 7;
      }
      const fixture = makeFixture([compileRefreshCommand(), compileWaitReadyCommand()], heartbeat(), replacement);
      fixture.context.sleepMs = async (milliseconds) => { fixture.advanceTime(milliseconds); };
      const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
      bridge.compileResponse = { status: "error", error: { code: "unity_reloading", message: "Bridge stopped", details: { reason: "bridge_lifecycle_shutdown" } } };
      bridge.waitResponseFactory = (operationId) => {
        const base = pendingComputeResponse(operationId);
        const details = (base as { error: { details: Record<string, unknown> } }).error.details;
        const progress = details.background_compiler_progress as Record<string, unknown>;
        if (variant === "different_pid") progress.process_id = 54321;
        if (variant === "stale_progress") progress.observed_utc = "2026-07-16T07:00:00.000Z";
        if (variant === "barrier_missing") details.compile_barrier = {};
        if (variant === "stale_catalog") progress.catalog_generation = 4;
        if (variant === "log_only") { delete details.background_compiler_progress; details.native_log_claim = "Lost connection with shader compiler process"; }
        if (variant === "other_error") (base as { error: { code: string } }).error.code = "compile_freshness_unverified";
        if (variant === "package_pending") details.package_update = { status: "package_refresh_required", refresh_pending: true, fingerprint: "a".repeat(64) };
        if (variant === "script_compiling") details.is_compiling = true;
        if (variant === "unfinished_generation") progress.completed_compilation_generation = 7;
        if (variant === "cancellation_true") progress.cancellation_attempted = true;
        return base;
      };
      fixture.context.bridge = bridge;

      const result = await call(fixture.context, "compile.refresh_and_wait", {});
      expect(result.status).toBe("error");
      expect(bridge.commands, JSON.stringify(result)).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
      if (result.status === "error") {
        expect(result.error.code).not.toBe("background_compilation_pending");
        expect(result.error.details).toMatchObject({ write_command_repeated: false, write_replay_allowed: false });
      }
    }
  });

  it("waits for a bootstrap-pending replacement before comparing its command catalog", async () => {
    const fixture = makeFixture([compileRefreshCommand(), compileWaitReadyCommand()]);
    let sleepCount = 0;
    const publishPendingReplacement = () => {
      fs.rmSync(path.join(fixture.instancesDir, "game-old.json"), { force: true });
      writeHeartbeat(fixture.instancesDir, heartbeat({
        instance_id: "game-new",
        bridge_port: 0,
        status: "waiting_for_idle",
        commands_hash: "bootstrap-pending",
      }));
    };
    fixture.context.sleepMs = async (milliseconds) => {
      fixture.advanceTime(milliseconds);
      sleepCount += 1;
      writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "ready" }));
    };
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, publishPendingReplacement);
    bridge.compileResponse = {
      status: "error",
      error: { code: "unity_reloading", message: "Unity bridge stopped the active wait", details: { reason: "bridge_lifecycle_shutdown", write_replay_allowed: false } },
    };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "compile.refresh_and_wait", {});

    expect(result.status).toBe("ok");
    expect(sleepCount).toBe(1);
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        post_reload_stability_verified: true,
        recovered_from_lifecycle_shutdown: true,
        compile_refresh_verification: {
          verification_status: "ready_clean",
          attempt_count: 2,
          verification_instance_role: "replacement",
          write_request_count: 1,
          read_request_count: 1,
        },
        write_command_repeated: false,
        write_replay_allowed: false,
      });
    }
  });

  it("accepts exact compile barrier verification when only the same-package catalog hash changes", async () => {
    const fixture = makeFixture([compileRefreshCommand(), compileWaitReadyCommand()], heartbeat(), { commands_hash: "catalog-b" });
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileResponse = {
      status: "error",
      error: { code: "unity_reloading", message: "Unity bridge stopped the active wait", details: { reason: "bridge_lifecycle_shutdown", write_replay_allowed: false } },
    };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "compile.refresh_and_wait", {});

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait", "compile.wait_ready"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        original_command_completed: true,
        write_command_repeated: false,
        compile_refresh_verification: {
          verification_status: "ready_clean",
          verification_instance_role: "replacement",
          verification_catalog_hash_changed: true,
        },
      });
    }
  });

  it("does not label an issued compile write as not dispatched when stabilization reaches the deadline", async () => {
    let releaseWrite!: () => void;
    let markWriteStarted!: () => void;
    const writeStarted = new Promise<void>((resolve) => { markWriteStarted = resolve; });
    const gate = new Promise<void>((resolve) => { releaseWrite = resolve; });
    const fixture = makeFixture([compileRefreshCommand(30), compileWaitReadyCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileGate = gate;
    bridge.markCompileStarted = markWriteStarted;
    fixture.context.bridge = bridge;

    const pending = call(fixture.context, "compile.refresh_and_wait", {});
    await writeStarted;
    const result = await pending;

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("compile_freshness_unverified");
      expect(result.error.details).toMatchObject({
        timeout_kind: "post_reload_stability_timeout",
        delivery_state: "dispatched",
        bridge_request_issued: true,
        write_command_repeated: false,
        write_replay_allowed: false,
      });
      expect(result.error.details).not.toMatchObject({ delivery_state: "not_dispatched" });
    }
    expect(bridge.commands).toEqual(["compile.refresh_and_wait"]);
    releaseWrite();
    await new Promise<void>((resolve) => setTimeout(resolve, 25));
  });

  it("recovers validation.after_edit through its exact compile barrier without replaying the refresh", async () => {
    const fixture = makeFixture([validationAfterEditCommand(), compileWaitReadyCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileResponse = {
      status: "error",
      error: { code: "unity_reloading", message: "Unity bridge stopped the validation wait", details: { reason: "bridge_lifecycle_shutdown", write_replay_allowed: false } },
    };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "validation.after_edit", {});

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["validation.after_edit", "compile.wait_ready"]);
    expect(bridge.instanceIds).toEqual(["game-old", "game-new"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        stage: "after_edit",
        validation_ready: true,
        original_command_completed: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        validation_after_edit_verification: {
          verification_status: "ready_clean",
          verification_instance_role: "replacement",
          write_request_count: 1,
          read_request_count: 1,
        },
      });
    }
  });

  it("reads direct validation barrier evidence from nested compile_status before replacement verification", async () => {
    const fixture = makeFixture([validationAfterEditCommand(), compileWaitReadyCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileResponseFactory = (operationId) => ({
      status: "ok",
      data: {
        stage: "after_edit",
        validation_ready: true,
        refreshed: true,
        checks: { editor_ready: true, compilable: true, no_unsaved_scene_changes: true, timed_out: false },
        compile_status: {
          ready: true,
          editor_ready: true,
          compilable: true,
          timed_out: false,
          has_compile_errors: false,
          compile_error_count: 0,
          lifecycle_settled: true,
          compile_barrier: verifiedBarrier(operationId),
        },
      },
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "validation.after_edit", {});

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["validation.after_edit", "compile.wait_ready"]);
    expect(bridge.instanceIds).toEqual(["game-old", "game-new"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        validation_ready: true,
        post_reload_stability_verified: true,
        original_command_completed: true,
        write_command_repeated: false,
        validation_after_edit_verification: {
          verification_status: "ready_clean",
          stability_reason: "direct_success",
          verification_instance_role: "replacement",
          read_request_count: 1,
        },
      });
    }
  });

  it("fails closed without replay when direct validation returns a mismatched barrier", async () => {
    const fixture = makeFixture([validationAfterEditCommand(), compileWaitReadyCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileResponseFactory = () => ({
      status: "ok",
      data: {
        stage: "after_edit",
        validation_ready: true,
        compile_status: {
          ready: true,
          has_compile_errors: false,
          compile_barrier: verifiedBarrier("fedcba9876543210fedcba9876543210"),
        },
      },
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "validation.after_edit", {});

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["validation.after_edit"]);
    if (result.status === "error") {
      expect(result.error.code).toBe("compile_freshness_unverified");
      expect(result.error.details).toMatchObject({
        barrier_validation_reason: "successful_response_without_matching_finished_barrier",
        delivery_state: "dispatched",
        original_command_completed: "unknown",
        command_bridge_request_issued: true,
        write_command_repeated: false,
        write_replay_allowed: false,
        next_valid_actions: ["compile.wait_ready", "compile.status", "do_not_auto_repeat_validation.after_edit"],
      });
    }
  });

  it.each([
    ["editor.playmode.enter", {}, "playing", true, "playing"],
    ["editor.playmode.exit", {}, "editing", false, "editing"],
    ["editor.playmode.set", { state: "playing" }, "playing", true, "playing"],
  ] as const)("verifies %s with one write and read-only current-instance status", async (command, args, target, actual, status) => {
    const fixture = makeFixture([playModeCommand(command), editorStatusCommand()], command === "editor.playmode.exit" ? heartbeat({ status: "playing" }) : heartbeat());
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = target;
    bridge.statusResponse = playStatus(actual, status);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, command, args);

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual([command, "editor.status"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        playmode_target: target,
        playmode_transition_verified: true,
        verification_command: "editor.status",
        verification_instance_role: "current",
        verification_read_request_count: 1,
        write_command_repeated: false,
        write_replay_allowed: false,
      });
    }
  });

  it("verifies Play Mode on a canonical replacement after a lost write response without replay", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.playError = new BridgeRequestError("bridge_protocol_error", {
      endpoint: "/invoke", bridge_host: "127.0.0.1", bridge_port: 18142, http_status: 200, failure_kind: "response_parse", parse_error_kind: "empty_body",
      response_body_bytes: 0, response_body_empty: true, response_body_truncated: false,
    });
    bridge.publishAfterPlay = true;
    bridge.statusResponse = playStatus(true, "playing");
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["editor.playmode.enter", "editor.status"]);
    expect(bridge.instanceIds).toEqual(["game-old", "game-new"]);
    if (result.status === "ok") expect(result.data).toMatchObject({ verification_instance_role: "replacement", write_command_repeated: false });
  });

  it("keeps Play Mode verification open for a delayed reload beyond the former fifteen-second envelope", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()]);
    fixture.context.sleepMs = async (milliseconds) => { fixture.advanceTime(milliseconds); };
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.playReloadExpected = true;
    bridge.publishAfterStatusReads = 180;
    bridge.statusResponse = playStatus(true, "playing");
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("ok");
    expect(bridge.commands[0]).toBe("editor.playmode.enter");
    expect(bridge.commands.filter((commandId) => commandId === "editor.playmode.enter")).toHaveLength(1);
    expect(bridge.statusReads).toBeGreaterThan(180);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        playmode_transition_verified: true,
        verification_instance_role: "replacement",
        write_command_repeated: false,
        write_replay_allowed: false,
      });
    }
  });

  it.each([
    ["editor.playmode.enter", {}, "playing"],
    ["editor.playmode.set", { state: "playing" }, "playing"],
  ] as const)("returns an exact recoverable receipt when %s cannot read a replacement before its deadline", async (command, args, target) => {
    const fixture = makeFixture([playModeCommand(command), editorStatusCommand()], heartbeat(), { status: "reloading", bridge_port: 0 });
    fixture.context.sleepMs = async (milliseconds) => { fixture.advanceTime(milliseconds); };
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = target;
    bridge.playReloadExpected = true;
    bridge.publishAfterPlay = true;
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, command, args);

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual([command]);
    if (result.status === "error") {
      const operationId = String(result.error.details?.playmode_transition_operation_id);
      expect(operationId).toMatch(/^[0-9a-f]{32}$/);
      expect(result.error.details).toMatchObject({
        playmode_transition_recovery_contract: "sgg.unity_mcp.playmode_transition_recovery.v1",
        operation_id_source: "gateway_reserved",
        playmode_target: target,
        verification_status: "timeout",
        verification_read_request_count: 0,
        delivery_state: "dispatched",
        status_check_required_before_repeat: true,
        status_lookup: {
          tool: "sgg_unity_call",
          project: "c:/u/game",
          command: "editor.status",
          args_json: JSON.stringify({ _playmode_transition_expectation_id: operationId }),
          mode: "wait",
        },
        command_replayed: false,
        write_command_repeated: false,
        write_replay_allowed: false,
      });
      expect(result.next_valid_actions).toContain("poll_exact_editor.status");
    }
  });

  it("retains the exact receipt after an issued Play Mode bridge_unreachable failure", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()], heartbeat(), { status: "reloading", bridge_port: 0 });
    fixture.context.sleepMs = async (milliseconds) => { fixture.advanceTime(milliseconds); };
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.publishAfterPlay = true;
    bridge.playError = new BridgeRequestError("bridge_unreachable", {
      endpoint: "/invoke",
      bridge_host: "127.0.0.1",
      bridge_port: 18142,
      failure_kind: "connection",
      bridge_request_issued: true,
    });
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result).toMatchObject({
      status: "error",
      error: {
        code: "unity_busy",
        details: {
          playmode_transition_operation_id: expect.stringMatching(/^[0-9a-f]{32}$/),
          playmode_target: "playing",
          verification_read_request_count: 0,
          delivery_state: "dispatched",
          status_lookup: { command: "editor.status", mode: "wait" },
          write_command_repeated: false,
          write_replay_allowed: false,
        },
      },
    });
    expect(bridge.commands).toEqual(["editor.playmode.enter"]);
  });

  it("uses the receipt's exact editor.status lookup to observe a later terminal transition", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()], heartbeat(), { status: "reloading", bridge_port: 0 });
    fixture.context.sleepMs = async (milliseconds) => { fixture.advanceTime(milliseconds); };
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.playReloadExpected = true;
    bridge.publishAfterPlay = true;
    bridge.statusResponse = playStatus(true, "playing");
    fixture.context.bridge = bridge;
    const timedOut = await call(fixture.context, "editor.playmode.enter", {});
    if (timedOut.status !== "error") throw new Error("expected timeout receipt");
    const lookup = timedOut.error.details?.status_lookup as { tool: string; project: string; command: string; args_json: string; mode: "wait" };
    const operationId = String(timedOut.error.details?.playmode_transition_operation_id);
    writeHeartbeat(fixture.instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "ready" }));

    const status = await dispatchTool(lookup.tool as "sgg_unity_call", { project: lookup.project, command: lookup.command, args_json: lookup.args_json, mode: lookup.mode }, fixture.context);

    expect(status).toMatchObject({ status: "ok", data: { playmode_transition: { state: "verified", operation_id: operationId, target: "playing", completed: true, post_reload_observed: true } } });
    expect(bridge.commands.filter((commandId) => commandId === "editor.playmode.enter")).toHaveLength(1);
    expect(bridge.commands.at(-1)).toBe("editor.status");
  });

  it.each([
    ["operation", "00000000000000000000000000000000"],
    ["target", "editing"],
  ] as const)("does not accept a Play Mode transition with a mismatched %s", async (field, value) => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()]);
    fixture.context.sleepMs = async (milliseconds) => { fixture.advanceTime(milliseconds); };
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.statusResponse = playStatus(true, "playing");
    if (field === "operation") bridge.reportedPlayOperationId = value;
    else bridge.reportedPlayTarget = value;
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result).toMatchObject({ status: "error", error: { code: "unity_busy", details: { playmode_target: "playing", verification_status: "timeout", status_check_required_before_repeat: true } } });
    expect(bridge.commands.filter((commandId) => commandId === "editor.playmode.enter")).toHaveLength(1);
    expect(bridge.statusReads).toBeGreaterThan(0);
  });

  it("does not read a Play Mode verifier from a different process generation and retains the receipt", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()], heartbeat(), {
      unity_pid: 54321,
      unity_process_start_utc: "2026-07-16T08:00:00.000Z",
    });
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.publishAfterPlay = true;
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result).toMatchObject({ status: "error", error: { code: "unity_busy", details: { verification_status: "unavailable", verification_read_request_count: 0, status_check_required_before_repeat: true } } });
    expect(bridge.commands).toEqual(["editor.playmode.enter"]);
  });

  it("retains the exact receipt when the outer settlement deadline wins after write issuance", async () => {
    vi.useFakeTimers();
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    let releasePlay!: () => void;
    bridge.playGate = new Promise<void>((resolve) => { releasePlay = resolve; });
    fixture.context.bridge = bridge;

    const pending = call(fixture.context, "editor.playmode.enter", {});
    await vi.advanceTimersByTimeAsync(0);
    expect(bridge.commands).toEqual(["editor.playmode.enter"]);
    await vi.advanceTimersByTimeAsync(40_000);
    const result = await pending;

    expect(result).toMatchObject({
      status: "error",
      error: {
        code: "unity_busy",
        details: {
          reason: "playmode_transition_unverified",
          timeout_kind: "playmode_transition_timeout",
          delivery_state: "dispatched",
          playmode_transition_recovery_contract: "sgg.unity_mcp.playmode_transition_recovery.v1",
          playmode_transition_operation_id: expect.stringMatching(/^[0-9a-f]{32}$/),
          playmode_target: "playing",
          status_check_required_before_repeat: true,
          status_lookup: { command: "editor.status", mode: "wait" },
          write_command_repeated: false,
          write_replay_allowed: false,
        },
      },
    });
    releasePlay();
    await vi.advanceTimersByTimeAsync(0);
    expect(bridge.commands.filter((commandId) => commandId === "editor.playmode.enter")).toHaveLength(1);
  });

  it("verifies a Play Mode write that may execute after a main-thread timeout without replay", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.playResponse = {
      status: "error",
      error: {
        code: "command_timeout",
        message: "Unity command timed out",
        details: { main_thread_dispatch: { timed_out_work_state: "running", late_execution_possible: true } },
      },
    };
    bridge.statusResponse = playStatus(true, "playing");
    bridge.beforePlayResponse = () => fixture.advanceTime(10_000);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["editor.playmode.enter", "editor.status"]);
    if (result.status === "ok") expect(result.data).toMatchObject({ playmode_transition_verified: true, write_command_repeated: false });
  });

  it("accepts exact Play Mode verification when only the same-package catalog hash changes", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()], heartbeat(), { commands_hash: "catalog-b" });
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.playReloadExpected = true;
    bridge.publishAfterPlay = true;
    bridge.statusResponse = playStatus(true, "playing");
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("ok");
    expect(bridge.commands).toEqual(["editor.playmode.enter", "editor.status"]);
    if (result.status === "ok") {
      expect(result.data).toMatchObject({
        playmode_transition_verified: true,
        verification_instance_role: "replacement",
        verification_catalog_hash_changed: true,
        write_command_repeated: false,
      });
    }
  });

  it("rejects catalog drift when the replacement package version changes", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()], heartbeat(), {
      commands_hash: "catalog-b",
      package_version: "2026.8.27-3",
    });
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playTarget = "playing";
    bridge.playReloadExpected = true;
    bridge.publishAfterPlay = true;
    bridge.statusResponse = playStatus(true, "playing");
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["editor.playmode.enter"]);
    if (result.status === "error") expect(result.error.details).toMatchObject({ verification_status: "catalog_stale", write_command_repeated: false });
  });

  it("does not read a verifier from a different Unity process generation", async () => {
    const fixture = makeFixture([compileRefreshCommand(), compileWaitReadyCommand()], heartbeat(), {
      commands_hash: "catalog-b",
      unity_pid: 54321,
      unity_process_start_utc: "2026-07-16T08:00:00.000Z",
    });
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.compileResponse = {
      status: "error",
      error: { code: "unity_reloading", message: "Unity bridge stopped the active wait", details: { reason: "bridge_lifecycle_shutdown", write_replay_allowed: false } },
    };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "compile.refresh_and_wait", {});

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["compile.refresh_and_wait"]);
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({
        write_command_repeated: false,
        compile_refresh_verification: { verification_status: "unavailable", read_request_count: 0 },
      });
    }
  });

  it("rejects an old Play Mode catalog before dispatch when correlation is unsupported", async () => {
    const oldCommand = command("editor.playmode.enter", "write", 10_000);
    const fixture = makeFixture([oldCommand, editorStatusCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("error");
    if (result.status === "error") expect(result.error.code).toBe("catalog_stale");
    expect(bridge.commands).toEqual([]);
  });

  it("does not issue an uncorrelated status read when verification schema is incompatible", async () => {
    const oldStatus = command("editor.status", "read", 10_000);
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), oldStatus]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.details).toMatchObject({ verification_status: "catalog_stale", write_command_repeated: false, write_replay_allowed: false });
    }
    expect(bridge.commands).toEqual(["editor.playmode.enter"]);
  });

  it("keeps a rejected Play Mode write terminal without status verification", async () => {
    const fixture = makeFixture([playModeCommand("editor.playmode.enter"), editorStatusCommand()]);
    const bridge = new LifecycleBridge(fixture.context.bridge as BridgeClient, fixture.publishReplacement);
    bridge.playResponse = { status: "error", error: { code: "unity_busy", message: "Play Mode transition refused", details: { command_dispatched: false } } };
    fixture.context.bridge = bridge;

    const result = await call(fixture.context, "editor.playmode.enter", {});

    expect(result.status).toBe("error");
    expect(bridge.commands).toEqual(["editor.playmode.enter"]);
  });
});

function makeFixture(commands: CommandDescriptor[], record: HeartbeatRecord = heartbeat(), replacementOverrides: Partial<HeartbeatRecord> = {}) {
  const instancesDir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-pr-083-"));
  tempDirs.push(instancesDir);
  writeHeartbeat(instancesDir, record);
  let currentTimeMs = nowMs;
  const context: DispatcherContext = {
    instancesDir,
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: () => new Date(nowMs) }),
    bridge: new FakeUnityBridgeClient(commands),
    now: () => new Date(currentTimeMs),
  };
  const publishReplacement = () => {
    fs.rmSync(path.join(instancesDir, `${record.instance_id}.json`), { force: true });
    writeHeartbeat(instancesDir, heartbeat({ instance_id: "game-new", bridge_port: 18143, status: "ready", ...replacementOverrides }));
  };
  return { context, instancesDir, publishReplacement, advanceTime: (milliseconds: number) => { currentTimeMs += milliseconds; } };
}

function call(context: DispatcherContext, command: string, args: Record<string, unknown>) {
  return dispatchTool("sgg_unity_call", { project: "game", command, args_json: JSON.stringify(args), mode: "wait", request_id: "pr-083" }, context);
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
    aliases: [],
    unity_version: "6000.5.3f1",
    package_version: "2026.8.28-1",
    unity_pid: 12345,
    unity_process_start_utc: "2026-07-16T07:00:00.000Z",
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "local-token",
    status: "ready",
    commands_hash: "catalog-a",
    last_heartbeat: new Date(nowMs).toISOString(),
    ...overrides,
  };
}

function activeComputeHeartbeat(): Partial<HeartbeatRecord> {
  return {
    status: "busy", catalog_status: "ready", catalog_generation: 5,
    compilation_generation: 8, completed_compilation_generation: 8,
    last_compilation_started_utc: "2026-07-16T07:59:59.000Z",
    last_compilation_finished_utc: "2026-07-16T08:00:00.030Z",
    background_compiler_progress_state: "active", background_compiler_progress_authoritative: true,
    background_compiler_progress_active: true, background_compiler_progress_failed: false,
    background_compiler_progress_category: "compute_variant_compile",
    background_compiler_progress_catalog_generation: 5,
    background_compiler_progress_compilation_generation: 8,
    background_compiler_progress_completed_compilation_generation: 8,
    background_compiler_progress_observed_utc: "2026-07-16T08:00:00.050Z",
  };
}

function command(id: string, risk: CommandDescriptor["risk"], timeoutMs: number, schema: Record<string, unknown> = { type: "object", additionalProperties: false }): CommandDescriptor {
  return { id, title: id, description: id, package: "com.sungeargames.unity-mcp", risk, idempotency: risk === "read" ? "safe" : "idempotent", timeout_ms: timeoutMs, schema, source: "builtin", package_id: "com.sungeargames.unity-mcp", package_version: "2026.8.28-1", trust: "builtin" };
}

function compileRefreshCommand(timeoutMs = 305_000): CommandDescriptor {
  return command("compile.refresh_and_wait", "write", timeoutMs);
}

function validationAfterEditCommand(): CommandDescriptor {
  return command("validation.after_edit", "write", 305_000);
}

function compileWaitReadyCommand(): CommandDescriptor {
  return command("compile.wait_ready", "read", 305_000);
}

function editorStatusCommand(): CommandDescriptor {
  return command("editor.status", "read", 10_000, correlatedPlayModeSchema());
}

function playModeCommand(id: string): CommandDescriptor {
  return command(id, "write", 10_000, id === "editor.playmode.set"
    ? { type: "object", properties: { state: { type: "string", enum: ["playing", "editing"] }, _playmode_transition_expectation_id: operationIdSchema() }, required: ["state"], additionalProperties: false }
    : correlatedPlayModeSchema());
}

function correlatedPlayModeSchema(): Record<string, unknown> {
  return { type: "object", properties: { _playmode_transition_expectation_id: operationIdSchema() }, additionalProperties: false };
}

function operationIdSchema(): Record<string, unknown> {
  return { type: "string", pattern: "^[0-9a-f]{32}$" };
}

function playStatus(actual: boolean, status: string): BridgeInvokeResponse {
  return {
    status: "ok",
    data: {
      status,
      is_compiling: false,
      is_updating: false,
      is_playing: actual,
      is_playing_actual: actual,
      is_playing_or_will_change_playmode: actual,
    },
  };
}

class LifecycleBridge implements BridgeClient {
  readonly commands: string[] = [];
  readonly instanceIds: string[] = [];
  compileResponse: BridgeInvokeResponse = { status: "ok", data: {} };
  compileResponseFactory: ((operationId: string) => BridgeInvokeResponse) | undefined;
  waitResponseFactory: ((operationId: string) => BridgeInvokeResponse) | undefined;
  compileGate: Promise<void> | undefined;
  markCompileStarted: (() => void) | undefined;
  playTarget: "playing" | "editing" = "playing";
  playResponse: BridgeInvokeResponse | undefined;
  playError: BridgeRequestError | undefined;
  playGate: Promise<void> | undefined;
  publishAfterPlay = false;
  playReloadExpected = false;
  publishAfterStatusReads = 0;
  statusReads = 0;
  beforePlayResponse: (() => void) | undefined;
  statusResponse: BridgeInvokeResponse = playStatus(true, "playing");
  reportedPlayOperationId: string | undefined;
  reportedPlayTarget: "playing" | "editing" | undefined;
  #barrierOperationId = "";
  #playModeOperationId = "";

  constructor(readonly inner: BridgeClient, readonly publishReplacement: () => void) {}

  describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.describe(record, selector, transport);
  }

  async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions): Promise<BridgeInvokeResponse> {
    this.commands.push(request.command);
    this.instanceIds.push(record.instance_id);
    transport?.beforeRequestIssued?.();
    transport?.onRequestIssued?.();
    if (request.command === "compile.refresh_and_wait" || request.command === "validation.after_edit") {
      this.#barrierOperationId = String(request.args._compile_barrier_expectation_id ?? "");
      this.markCompileStarted?.();
      if (this.compileGate) await this.compileGate;
      this.publishReplacement();
      return this.compileResponseFactory?.(this.#barrierOperationId) ?? this.compileResponse;
    }
    if (request.command === "compile.wait_ready") {
      if (this.waitResponseFactory) return this.waitResponseFactory(this.#barrierOperationId);
      return {
        status: "ok",
        data: {
          ready: true, editor_ready: true, compilable: true, timed_out: false, has_compile_errors: false, compile_error_count: 0,
          compile_barrier: verifiedBarrier(this.#barrierOperationId),
        },
      };
    }
    if (request.command.startsWith("editor.playmode.")) {
      this.#playModeOperationId = String(request.args._playmode_transition_expectation_id ?? "");
      if (this.playGate) await this.playGate;
      if (this.publishAfterPlay) this.publishReplacement();
      this.beforePlayResponse?.();
      if (this.playError) throw this.playError;
      return this.playResponse ?? {
        status: "ok",
        data: {
          status: this.playTarget === "playing" ? "playing" : "editing",
          is_playing: this.playTarget === "playing",
          is_playing_actual: false,
          is_playing_or_will_change_playmode: this.playTarget === "playing",
          playmode_target: this.playTarget,
          playmode_transition_requested: true,
        },
      };
    }
    if (request.command === "editor.status" && this.statusResponse.status === "ok") {
      this.statusReads += 1;
      if (this.publishAfterStatusReads > 0 && this.statusReads === this.publishAfterStatusReads) this.publishReplacement();
      return {
        ...this.statusResponse,
        data: {
          ...(isRecord(this.statusResponse.data) ? this.statusResponse.data : {}),
          playmode_transition: verifiedPlayModeTransition(this.reportedPlayOperationId ?? this.#playModeOperationId, this.reportedPlayTarget ?? this.playTarget, this.playReloadExpected, record.instance_id === "game-new"),
        },
      };
    }
    return this.inner.invoke(record, request);
  }

  logs(record: HeartbeatRecord, kind: "console" | "bridge" | "jobs", limit: number, transport?: BridgeTransportOptions): Promise<unknown> {
    return this.inner.logs(record, kind, limit, transport);
  }
}

function pendingComputeResponse(operationId: string): BridgeInvokeResponse {
  const fingerprint = "a".repeat(64);
  return {
    status: "error",
    error: {
      code: "command_timeout", message: "Timed out waiting for authoritative Unity background compute compilation completion",
      details: {
        status: "background_compilation_pending", ready: false, compilable: true, is_compiling: false, is_updating: false,
        timed_out: true, has_compile_errors: false, compile_error_count: 0,
        package_refresh_required: false, is_playing: false,
        package_update: { status: "current", refresh_pending: false, fingerprint },
        compile_barrier: verifiedBarrier(operationId),
        background_compiler_progress: {
          observed: true, authoritative: true, active: true, failed: false, blocks_readiness: true,
          state: "active", category: "compute_variant_compile", relevant_item_count: 1, active_item_count: 1,
          progress_permille: 928, process_id: 12345, catalog_generation: 5,
          compilation_generation: 8, completed_compilation_generation: 8, package_fingerprint: fingerprint,
          observed_utc: "2026-07-16T08:00:00.050Z", correlated_native_failure_observed: false,
          native_failure_category: "none", native_failure_observed_utc: "", native_failure_absence_authoritative: false,
          failure_reason: "", cancellation_attempted: false, work_replayed: false,
        },
      },
    },
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}

function verifiedBarrier(operationId: string): Record<string, unknown> {
  return {
    state: "verified", required: true, operation_id: operationId, requested: true, request_issued: true, started: true, finished: true,
    source_instance_id: "game-old", terminal_compile_result: "clean", terminal_compile_error_count: 0,
    terminal_compile_observed_utc: "2026-07-16T08:00:00.030Z", terminal_compile_failure_without_reload: false,
    reload_started: true, reload_started_utc: "2026-07-16T08:00:00.035Z", reload_source_instance_id: "game-old", replacement_instance_id: "game-new",
    requested_utc: "2026-07-16T08:00:00.000Z", request_issued_utc: "2026-07-16T08:00:00.010Z",
    started_utc: "2026-07-16T08:00:00.020Z", finished_utc: "2026-07-16T08:00:00.030Z",
    post_reload_observed: true, post_reload_observed_utc: "2026-07-16T08:00:00.040Z",
    observed_generation: 1, request_generation: 1, started_generation: 1, finished_generation: 1,
  };
}

function verifiedPlayModeTransition(operationId: string, target: string, reloadExpected: boolean, postReloadObserved: boolean): Record<string, unknown> {
  return {
    state: "verified", operation_id: operationId, target, requested: true, transition_requested: true, completed: true,
    reload_expected: reloadExpected,
    requested_utc: "2026-07-16T08:00:00.000Z", completed_utc: "2026-07-16T08:00:00.010Z",
    post_reload_observed: postReloadObserved,
    post_reload_observed_utc: postReloadObserved ? "2026-07-16T08:00:00.020Z" : "",
  };
}
