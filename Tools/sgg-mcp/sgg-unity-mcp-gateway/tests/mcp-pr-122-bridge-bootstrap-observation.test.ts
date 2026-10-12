import { describe, expect, it } from "vitest";

import { classifyBridgeBootstrapObservation } from "../src/mcp/bridge-bootstrap-observation.js";

const failed = { unity_pid: 100, unity_process_start_utc: "2026-08-08T00:00:00.000Z", status: "reloading" };

describe("MCP-PR-122 bridge bootstrap observation", () => {
  it.each([
    [["bridge.reload.before"], "post_reload_callback_not_observed"],
    [["bridge.reload.before", "bridge.bootstrap.did_reload_scripts.runtime_initializer_not_observed"], "initialize_on_load_not_observed"],
    [["bridge.reload.before", "bridge.bootstrap.runtime_initializer_observed"], "editor_update_not_observed"],
    [["bridge.reload.before", "bridge.bootstrap.runtime_initializer_observed", "bridge.bootstrap.editor_update_observed"], "bootstrap_idle_gate_pending"],
    [["bridge.reload.before", "bridge.bootstrap.editor_update_observed", "bridge.bootstrap.idle_gate.compiling"], "bootstrap_delayed_compiling"],
    [["bridge.reload.before", "bridge.bootstrap.editor_update_observed", "bridge.bootstrap.idle_gate.updating"], "bootstrap_delayed_updating"],
    [["bridge.reload.before", "bridge.bootstrap.editor_update_observed", "bridge.bootstrap.idle_gate.compiling_and_updating"], "bootstrap_delayed_compiling_and_updating"],
    [["bridge.reload.before", "bridge.bootstrap.lifecycle_started", "bridge.bootstrap.start"], "bootstrap_startup_in_progress"],
    [["bridge.reload.before", "bridge.bootstrap.lifecycle_failed.unexpected"], "bootstrap_failed"],
    [["bridge.reload.before", "bridge.bootstrap.first_heartbeat_failed.io"], "heartbeat_publication_failed"],
    [["bridge.reload.before", "bridge.bootstrap.first_heartbeat_published"], "heartbeat_publication_registration_unverified"],
  ])("classifies %j as %s", (events, state) => {
    const result = classifyBridgeBootstrapObservation(true, events.map((event) => ({ event })), failed, []);

    expect(result).toMatchObject({
      contract: "sgg.unity_mcp.bridge_bootstrap_observation.v1",
      state,
      diagnostic_only: true,
      authoritative: false,
      automatic_command_replay_attempted: false,
      automatic_process_restart_attempted: false,
    });
  });

  it("distinguishes same-process replacement from operator restart", () => {
    const events = [{ event: "bridge.reload.before" }];
    const sameProcess = classifyBridgeBootstrapObservation(true, events, failed, [
      { unity_pid: 100, unity_process_start_utc: "2026-08-08T00:00:00.000Z", status: "ready" },
    ]);
    const restarted = classifyBridgeBootstrapObservation(true, events, failed, [
      { unity_pid: 200, unity_process_start_utc: "2026-08-08T01:00:00.000Z", status: "ready" },
    ]);

    expect(sameProcess.state).toBe("same_process_replacement_ready");
    expect(sameProcess.same_process_replacement_ready).toBe(true);
    expect(restarted.state).toBe("operator_restart_recovery");
    expect(restarted.operator_restart_recovery_observed).toBe(true);
  });

  it("normalizes equivalent process-start timestamp precision", () => {
    const result = classifyBridgeBootstrapObservation(true, [{ event: "bridge.reload.before" }], {
      unity_pid: 100,
      unity_process_start_utc: "2026-08-08T00:00:00.0000000Z",
      status: "reloading",
    }, [{
      unity_pid: 100,
      unity_process_start_utc: "2026-08-08T00:00:00.000Z",
      status: "ready",
    }]);

    expect(result.state).toBe("same_process_replacement_ready");
    expect(result.same_process_replacement_ready).toBe(true);
    expect(result.operator_restart_recovery_observed).toBe(false);
  });

  it("distinguishes a fresh Unity process that has no bridge registration", () => {
    const result = classifyBridgeBootstrapObservation(true, [{ event: "bridge.reload.before" }], failed, [], [
      { unity_pid: 200, unity_process_start_utc: "2026-08-08T01:00:00.000Z", status: "unregistered" },
    ]);

    expect(result).toMatchObject({
      state: "fresh_process_registration_missing",
      unregistered_process_count: 1,
      fresh_process_without_registration: true,
      fresh_process_pid: 200,
      fresh_process_start_utc: "2026-08-08T01:00:00.000Z",
      automatic_command_replay_attempted: false,
      automatic_process_restart_attempted: false,
    });
    expect(result.next_valid_actions).toContain("do_not_replay_compile_or_test_write");
  });

  it("does not claim freshness when the failed process generation is unknown", () => {
    const result = classifyBridgeBootstrapObservation(true, [{ event: "bridge.reload.before" }], {
      unity_pid: 100,
      status: "reloading",
    }, [], [{
      unity_pid: 200,
      unity_process_start_utc: "2026-08-08T01:00:00.000Z",
      status: "unregistered",
    }]);

    expect(result.fresh_process_without_registration).toBe(false);
    expect(result.fresh_process_pid).toBeNull();
    expect(result.state).toBe("post_reload_callback_not_observed");
  });

  it("retains bounded early-registration and bootstrap-recovery events", () => {
    const result = classifyBridgeBootstrapObservation(true, [
      { event: "bridge.reload.before" },
      { event: "bridge.bootstrap.did_reload_scripts.runtime_initializer_not_observed" },
      { event: "bridge.bootstrap.did_reload_scripts.recovery_scheduled" },
      { event: "bridge.bootstrap.waiting_heartbeat_published" },
      { event: "bridge.bootstrap.lifecycle_recovery_scheduled.1" },
      { event: "bridge.bootstrap.heartbeat_retry_published" },
    ], failed, []);

    expect(result.waiting_heartbeat_published).toBe(true);
    expect(result.bootstrap_recovery_scheduled).toBe(true);
    expect(result.heartbeat_retry_published).toBe(true);
    expect(result.observed_events).toContain("bridge.bootstrap.lifecycle_recovery_scheduled.1");
  });

  it("does not treat trace evidence as authority", () => {
    const result = classifyBridgeBootstrapObservation(false, [], failed, []);

    expect(result.state).toBe("bootstrap_trace_unavailable");
    expect(result.authoritative).toBe(false);
    expect(result.next_valid_actions).toContain("do_not_replay_compile_or_test_write");
  });

  it("drops unrecognized event names instead of reflecting trace content", () => {
    const result = classifyBridgeBootstrapObservation(true, [
      { event: "bridge.reload.before" },
      { event: "bridge.bootstrap.first_heartbeat_failed.io;path=C:/secret" },
      { event: "bridge.bootstrap.caller_supplied_event" },
    ], failed, []);

    expect(result.state).toBe("post_reload_callback_not_observed");
    expect(result.observed_events).toEqual([]);
    expect(JSON.stringify(result)).not.toContain("secret");
    expect(JSON.stringify(result)).not.toContain("caller_supplied");
  });
});
