import type { BigIntStats } from "node:fs";
import fs from "node:fs";
import { createServer, type IncomingMessage, type ServerResponse, type Server } from "node:http";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { ApprovalStore } from "../src/approvals/approval-store.js";
import { BridgeRequestError, HttpUnityBridgeClient, type BridgeRequestErrorDetails, type BridgeInvokeRequest, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import type { GatewayResult } from "../src/mcp/results.js";
import { InMemoryAuditSink } from "../src/observability/audit.js";
import { PolicyManager } from "../src/policy/policy-manager.js";
import { ProjectRegistry, type HeartbeatRecord, type ProjectResolution, type PublicProjectRecord, type VerificationProjectResolution } from "../src/registry/project-registry.js";

const enter = "editor.playmode.enter";
const exit = "editor.playmode.exit";
const set = "editor.playmode.set";
const editorStatus = "editor.status";
const packageVersion = "2026.10.8-8";
const rejectedPayloadMarker = "rejected-playmode-catalog-payload-must-not-be-retained";
const timeoutMs = 600;
const pollMs = 100;
const playModeStabilityOverheadMs = 30_000;
const statusFeature = "playmode_catalog_test_status";
const patternFreeStatusSchema = { type: "object", properties: { _playmode_transition_expectation_id: { type: "string", minLength: 32, maxLength: 32 } }, additionalProperties: false };
type PlayCommand = typeof enter | typeof exit | typeof set;
type Target = "playing" | "editing";
type Role = "current" | "replacement";
type LagReason = "catalog_generation_mismatch" | "commands_hash_mismatch";
type ErrorDetailPatch = { [Key in keyof BridgeRequestErrorDetails]?: BridgeRequestErrorDetails[Key] | undefined };
type ResolutionFault = "project_not_found" | "duplicate_project_id" | "reloading" | "throw";
type HarnessOptions = {
  command?: PlayCommand;
  target?: Target;
  role?: Role;
  timeoutMs?: number;
  frozenClock?: boolean;
  lags?: LagReason[];
  persistent?: LagReason;
  catalogFault?: string;
  injectedError?: (record: HeartbeatRecord, index: number) => Error | undefined;
  afterWritePatch?: Partial<HeartbeatRecord>;
  postLagRecordPatch?: Partial<HeartbeatRecord>;
  sourcePatch?: Partial<HeartbeatRecord>;
  sourceDescriptorPatch?: Partial<CommandDescriptor>;
  verificationDescriptorPatch?: Partial<CommandDescriptor>;
  resolutionFaults?: Record<number, ResolutionFault>;
  transitionPatch?: Record<string, unknown>;
  omitTransition?: boolean;
  statusPatch?: Record<string, unknown>;
  statusRefusal?: "permission_denied" | "feature_disabled";
  statusBlockerDetails?: Record<string, unknown>;
  statusPolicyAction?: "deny" | "ask";
  autoAllowAsk?: boolean;
  managedPolicyRace?: boolean;
  disableFeatureBeforeStatusRequest?: boolean;
  writeOutcome?: "success" | "unity_reloading" | "command_timeout";
  expectedPlayWriteCount?: 0;
};
type Harness = {
  server: Server;
  registry: MemoryRegistry;
  bridge: RecordingHttpBridge;
  invocations: BridgeInvokeRequest[];
  handlerErrors: string[];
  sleeps: number[];
  audit: InMemoryAuditSink;
  nativeRead: () => never;
  nativeMutation: () => never;
  approvalRequest: ApprovalStore["request"];
  approvalConsume: ApprovalStore["consume"];
  approvalKey: () => never;
  managedPolicyRace?: ManagedPolicyRace;
  command: PlayCommand;
  target: Target;
  expectedPlayWriteCount: 0 | 1;
  elapsedMs: () => number;
  run: () => Promise<GatewayResult>;
};
const harnesses: Harness[] = [];

afterEach(async () => {
  for (const harness of harnesses.splice(0)) {
    harness.server.closeAllConnections();
    await new Promise<void>((resolve, reject) => harness.server.close((error) => error ? reject(error) : resolve()));
    expect(harness.handlerErrors).toEqual([]);
    expect(harness.invocations.filter((request) => request.command === harness.command)).toHaveLength(harness.expectedPlayWriteCount);
    expect(harness.invocations.every((request) => request.command === harness.command || request.command === editorStatus)).toBe(true);
    if (harness.expectedPlayWriteCount === 0) {
      expect(harness.invocations).toEqual([]);
      expect(harness.bridge.invokeAttempts).toEqual([]);
      expect(statusDescribes(harness)).toEqual([]);
      expect(harness.registry.verificationCalls).toEqual([]);
    } else {
      const operationId = harness.invocations[0]!.args._playmode_transition_expectation_id;
      expect(operationId).toMatch(/^[0-9a-f]{32}$/);
      for (const request of harness.invocations) {
        expect(request.args._playmode_transition_expectation_id).toBe(operationId);
        expect(request.args).not.toHaveProperty("_compile_barrier_expectation_id");
        expect(request.args).not.toHaveProperty("_require_explicit_compile_barrier");
        if (request.command === editorStatus) {
          expect(request.risk).toBe("read");
          expect(request.args).toEqual({ _playmode_transition_expectation_id: operationId });
        }
      }
    }
    expect(harness.bridge.described.every((entry) => entry.selector === `command:${harness.command}` || entry.selector === `command:${editorStatus}`)).toBe(true);
    expect(harness.bridge.logCalls).toBe(0);
    expect(harness.nativeRead).not.toHaveBeenCalled();
    expect(harness.nativeMutation).not.toHaveBeenCalled();
    expect(harness.approvalRequest).not.toHaveBeenCalled();
    expect(harness.approvalConsume).not.toHaveBeenCalled();
    expect(harness.approvalKey).not.toHaveBeenCalled();
    expect(JSON.stringify(harness.audit.events)).not.toContain(rejectedPayloadMarker);
  }
});

describe("MCP-PR-226 strict Play Mode catalog metadata convergence (synthetic, no host reproduction claim)", () => {
  it.each<LagReason>(["catalog_generation_mismatch", "commands_hash_mismatch"])("re-resolves a fresh heartbeat after exact %s before one correlated read", async (reason) => {
    const harness = await makeHarness({ lags: [reason] });
    const result = await harness.run();
    expectVerified(result, harness, "replacement");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: reason, catalog_metadata_converged: true, verification_attempt_count: 2, verification_read_request_count: 1 });
    expect(harness.sleeps).toEqual([pollMs]);
    expect(harness.registry.verificationCalls).toHaveLength(2);
    const reads = statusDescribes(harness);
    expect(reads).toHaveLength(2);
    expect(reads[1]!.record).toEqual(harness.registry.current);
    expect(reads[1]!.record.commands_hash === reads[0]!.record.commands_hash && reads[1]!.record.catalog_generation === reads[0]!.record.catalog_generation).toBe(false);
    expect(harness.registry.verificationCalls.map(({ root, instance }) => ({ root, instance }))).toEqual([{ root: "c:/u/game", instance: "game-source" }, { root: "c:/u/game", instance: "game-source" }]);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter, editorStatus]);
    expect(harness.invocations[1]).toMatchObject({ risk: "read", expected_instance_id: "game-replacement", expected_catalog_generation: harness.registry.current.catalog_generation, expected_commands_hash: harness.registry.current.commands_hash });
    expect(harness.bridge.errors[0]).toMatchObject({ message: "bridge_protocol_error", details: { endpoint: "/commands", failure_kind: "response_shape", protocol_schema: "sgg.unity_mcp.bridge.command_catalog.v1", protocol_reason: reason } });
  });

  it.each([
    { command: enter, target: "playing", role: "current" },
    { command: enter, target: "playing", role: "replacement" },
    { command: exit, target: "editing", role: "current" },
    { command: exit, target: "editing", role: "replacement" },
    { command: set, target: "editing", role: "current" },
    { command: set, target: "playing", role: "replacement" },
  ] satisfies Array<{ command: PlayCommand; target: Target; role: Role }>)("keeps $command target $target verified on $role without replay", async (options) => {
    const harness = await makeHarness({ ...options, lags: ["commands_hash_mismatch"] });
    const result = await harness.run();
    expectVerified(result, harness, options.role);
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: "commands_hash_mismatch", catalog_metadata_converged: true, verification_read_request_count: 1 });
    expect(harness.invocations.map((request) => request.command)).toEqual([options.command, editorStatus]);
  });

  it("handles alternating generation/hash rejections only through separate fresh resolutions", async () => {
    const harness = await makeHarness({ lags: ["catalog_generation_mismatch", "commands_hash_mismatch", "catalog_generation_mismatch"] });
    const result = await harness.run();
    expectVerified(result, harness, "replacement");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 3, catalog_metadata_last_retry_reason: "catalog_generation_mismatch", catalog_metadata_converged: true, verification_attempt_count: 4, verification_read_request_count: 1 });
    expect(harness.registry.verificationCalls).toHaveLength(4);
    expect(statusDescribes(harness).map(({ record }) => record)).toEqual(harness.registry.verificationCalls.map(({ record }) => record));
    expect(harness.sleeps).toEqual([pollMs, pollMs, pollMs]);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter, editorStatus]);
  });

  it.each<Role>(["current", "replacement"])("does not label normal %s verification as metadata convergence", async (role) => {
    const harness = await makeHarness({ role, writeOutcome: "success" });
    const result = await harness.run();
    expectVerified(result, harness, role);
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_attempt_count: 1, verification_read_request_count: 1 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(harness.sleeps).toEqual([]);
  });

  it("does not let status payloads forge Gateway-owned metadata evidence", async () => {
    const harness = await makeHarness({ statusPatch: { catalog_metadata_read_retry_count: 999, catalog_metadata_last_retry_reason: rejectedPayloadMarker, catalog_metadata_converged: true } });
    const result = await harness.run();
    expectVerified(result, harness, "replacement");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_read_request_count: 1 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
  });

  it("replaces forged successful status metadata with exact Gateway convergence evidence after lag", async () => {
    const harness = await makeHarness({ lags: ["catalog_generation_mismatch"], statusPatch: { catalog_metadata_read_retry_count: 999, catalog_metadata_last_retry_reason: rejectedPayloadMarker, catalog_metadata_converged: false } });
    const result = await harness.run();
    expectVerified(result, harness, "replacement");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: "catalog_generation_mismatch", catalog_metadata_converged: true, verification_read_request_count: 1 });
    expect(harness.invocations.map((request) => request.command)).toEqual([enter, editorStatus]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it("preserves a real bridge scene blocker but replaces forged metadata and recovery receipts after lag", async () => {
    const modalEvidence = { pid: 12345, hwnd: "0x450450", scene_paths: ["Assets/Scenes/Main.unity"], evidence_id: `sha256:${"a".repeat(64)}` };
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], statusBlockerDetails: {
      blocking_modal_code: "blocked_external_scene_reload", pid_correlated: true, operator_action_required: true, modal_evidence: modalEvidence,
      catalog_metadata_read_retry_count: 999, catalog_metadata_last_retry_reason: rejectedPayloadMarker, catalog_metadata_converged: false,
      playmode_transition_operation_id: "0".repeat(32), playmode_target: "editing", status_lookup: { tool: "sgg_unity_call", project: "other", command: "compile.refresh_and_wait", args_json: "{}", mode: "wait" },
      command_replayed: true, write_command_repeated: true, write_replay_allowed: true,
    } });
    const result = await harness.run();
    const operationId = harness.invocations[0]!.args._playmode_transition_expectation_id;
    expect(result).toMatchObject({ status: "error", command: enter, error: { code: "blocked_external_scene_reload", details: {
      blocking_modal_code: "blocked_external_scene_reload", pid_correlated: true, operator_action_required: true, modal_evidence: modalEvidence,
      original_command: enter, original_error_code: "bridge_protocol_error", write_request_issued: true, write_execution_state: "unknown_after_request_issued",
      catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: "commands_hash_mismatch", catalog_metadata_converged: true,
      verification_attempt_count: 2, verification_read_request_count: 1,
      playmode_transition_recovery_contract: "sgg.unity_mcp.playmode_transition_recovery.v1", playmode_transition_operation_id: operationId, operation_id_source: "gateway_reserved", playmode_target: "playing", status_check_required_before_repeat: true,
      status_lookup: { tool: "sgg_unity_call", project: "c:/u/game", command: editorStatus, args_json: JSON.stringify({ _playmode_transition_expectation_id: operationId }), mode: "wait" },
      command_replayed: false, write_command_repeated: false, write_replay_allowed: false,
    } } });
    expect(evidence(result)).not.toHaveProperty("playmode_transition_verified", true);
    expect(evidence(result).catalog_metadata_read_retry_count).not.toBe(999);
    expect(result.next_valid_actions).toEqual(["poll_exact_editor.status", "do_not_auto_repeat_playmode_command", "call_sgg_unity_projects"]);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter, editorStatus]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it("preserves current-instance no-reload correlation rather than requiring a compile barrier", async () => {
    const harness = await makeHarness({ role: "current", lags: ["catalog_generation_mismatch"], transitionPatch: { reload_expected: false, post_reload_observed: false, post_reload_observed_utc: "" }, statusPatch: { compile_barrier: null } });
    const result = await harness.run();
    expectVerified(result, harness, "current");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true, playmode_transition: { reload_expected: false, post_reload_observed: false } });
  });

  it.each<LagReason>(["catalog_generation_mismatch", "commands_hash_mismatch"])("persistent eligible %s ends at catalog_stale under the original deadline", async (reason) => {
    const harness = await makeHarness({ persistent: reason });
    const result = await harness.run();
    expectUnverified(result, harness, "catalog_stale");
    expect(evidence(result)).toMatchObject({ catalog_metadata_last_retry_reason: reason, catalog_metadata_converged: false, verification_read_request_count: 0 });
    expect(evidence(result).catalog_metadata_read_retry_count).toBe(statusDescribes(harness).length - 1);
    expect(evidence(result).catalog_metadata_read_retry_count).toBe(Number(evidence(result).verification_attempt_count) - 1);
    expect(Number(evidence(result).catalog_metadata_read_retry_count)).toBeGreaterThan(1);
    expect(Number(evidence(result).verification_attempt_count)).toBeLessThanOrEqual(Math.ceil(timeoutMs / pollMs) + 1);
    expect(harness.elapsedMs()).toBe(timeoutMs);
    expect(harness.sleeps.every((duration) => duration > 0 && duration <= pollMs)).toBe(true);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
  });

  it.each<LagReason>(["catalog_generation_mismatch", "commands_hash_mismatch"])("first %s rejection and expiry count zero actual subsequent describes", async (reason) => {
    const harness = await makeHarness({ persistent: reason, timeoutMs: pollMs });
    const result = await harness.run();
    expectUnverified(result, harness, "catalog_stale");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 1, verification_read_request_count: 0, catalog_metadata_read_retry_count: 0, catalog_metadata_last_retry_reason: reason, catalog_metadata_converged: false });
    expect(statusDescribes(harness)).toHaveLength(1);
    expect(harness.registry.verificationCalls).toHaveLength(1);
    expect(harness.sleeps).toEqual([pollMs]);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
  });

  it("retains the existing attempt bound even when the injected clock never advances", async () => {
    const harness = await makeHarness({ persistent: "commands_hash_mismatch", frozenClock: true });
    const result = await harness.run();
    const maxAttempts = Math.ceil(timeoutMs / pollMs) + 1;
    expectUnverified(result, harness, "catalog_stale");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: maxAttempts, verification_read_request_count: 0, catalog_metadata_read_retry_count: maxAttempts - 1, catalog_metadata_last_retry_reason: "commands_hash_mismatch", catalog_metadata_converged: false });
    expect(statusDescribes(harness)).toHaveLength(maxAttempts);
    expect(harness.registry.verificationCalls).toHaveLength(maxAttempts);
    expect(harness.sleeps).toEqual(Array.from({ length: maxAttempts }, () => pollMs));
    expect(harness.elapsedMs()).toBe(0);
  });

  it("does not extend a partial final poll beyond the original verification deadline", async () => {
    const harness = await makeHarness({ persistent: "catalog_generation_mismatch", timeoutMs: 250 });
    const result = await harness.run();
    expectUnverified(result, harness, "catalog_stale");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 3, catalog_metadata_read_retry_count: 2, verification_read_request_count: 0, catalog_metadata_converged: false });
    expect(harness.sleeps).toEqual([100, 100, 50]);
    expect(harness.elapsedMs()).toBe(250);
  });

  it.each<ResolutionFault>(["project_not_found", "reloading"])("does not count skipped %s registry attempts as actual catalog retries", async (fault) => {
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], resolutionFaults: { 1: fault } });
    const result = await harness.run();
    expectVerified(result, harness, "replacement");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 3, verification_read_request_count: 1, catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true });
    expect(harness.registry.verificationCalls).toHaveLength(3);
    expect(statusDescribes(harness)).toHaveLength(2);
    expect(harness.sleeps).toEqual([pollMs, pollMs]);
  });

  it.each([
    { fault: "duplicate_project_id", status: "ambiguous" },
    { fault: "throw", status: "unavailable" },
  ] satisfies Array<{ fault: ResolutionFault; status: string }>)("retains pending metadata evidence when fresh resolution stops on $fault", async ({ fault, status }) => {
    const harness = await makeHarness({ lags: ["catalog_generation_mismatch"], resolutionFaults: { 1: fault } });
    const result = await harness.run();
    expectUnverified(result, harness, status);
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, catalog_metadata_read_retry_count: 0, catalog_metadata_last_retry_reason: "catalog_generation_mismatch", catalog_metadata_converged: false, verification_read_request_count: 0 });
    expect(statusDescribes(harness)).toHaveLength(1);
  });

  it.each(["bridge_timeout", "bridge_unreachable"] as const)("keeps ordinary bounded %s retries separate from metadata drift", async (code) => {
    const harness = await makeHarness({ injectedError: (record) => new BridgeRequestError(code, metadataError(record).details) });
    const result = await harness.run();
    expectUnverified(result, harness, "timeout");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(harness.sleeps.length).toBeGreaterThan(0);
  });

  it("counts a subsequent transient describe while metadata is pending without clearing convergence early", async () => {
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], injectedError: (record, index) => index === 1 ? new BridgeRequestError("bridge_unreachable", metadataError(record).details) : undefined });
    const result = await harness.run();
    expectVerified(result, harness, "replacement");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 3, catalog_metadata_read_retry_count: 2, catalog_metadata_last_retry_reason: "commands_hash_mismatch", catalog_metadata_converged: true, verification_read_request_count: 1 });
    expect(harness.sleeps).toEqual([pollMs, pollMs]);
  });

  it.each(["project", "instance", "schema_version", "payload_shape", "duplicate_commands", "invalid_json", "content_type", "http_401", "http_500"])("does not retry strict-parser fault %s as metadata lag", async (catalogFault) => {
    const harness = await makeHarness({ catalogFault });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(statusDescribes(harness)).toHaveLength(1);
    expect(harness.sleeps).toEqual([]);
  });

  it.each([
    { name: "health_schema", patch: { protocol_schema: "sgg.unity_mcp.bridge.health.v1" } },
    { name: "single_command_schema", patch: { protocol_schema: "sgg.unity_mcp.bridge.single_command.v1" } },
    { name: "missing_schema", patch: { protocol_schema: undefined } },
    { name: "unknown_reason", patch: { protocol_reason: "unknown_catalog_failure" } },
    { name: "missing_reason", patch: { protocol_reason: undefined } },
    { name: "invoke_endpoint", patch: { endpoint: "/invoke" } },
    { name: "endpoint_prefix", patch: { endpoint: "/commands/other" } },
    { name: "endpoint_query", patch: { endpoint: "/commands?selector=commands" } },
    { name: "parse_failure", patch: { failure_kind: "response_parse" as const } },
    { name: "wrong_port", patch: { bridge_port: 1 } },
    { name: "wrong_host", patch: { bridge_host: "127.0.0.2" } },
  ])("does not classify forged metadata evidence $name as retryable", async ({ patch }) => {
    const harness = await makeHarness({ injectedError: (record) => metadataError(record, patch) });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(statusDescribes(harness)).toHaveLength(1);
    expect(harness.sleeps).toEqual([]);
  });

  it("rejects an Error lookalike even with matching name, text and details", async () => {
    const harness = await makeHarness({ injectedError: (record) => Object.assign(new Error("bridge_protocol_error"), { name: "BridgeRequestError", details: metadataError(record).details }) });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(harness.sleeps).toEqual([]);
  });

  it("does not retain an unrecognized later failure as the last metadata reason", async () => {
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], injectedError: (record, index) => index === 1 ? metadataError(record, { protocol_reason: rejectedPayloadMarker }) : undefined });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: "commands_hash_mismatch", catalog_metadata_converged: false, verification_read_request_count: 0 });
    expect(harness.sleeps).toEqual([pollMs]);
  });

  const identityDrift: Array<{ name: string; patch: Partial<HeartbeatRecord> }> = [
    { name: "project", patch: { project: "other" } },
    { name: "root", patch: { canonical_project_root: "c:/u/other", project_root: "C:/u/other" } },
    { name: "pid", patch: { unity_pid: 54321 } },
    { name: "start", patch: { unity_process_start_utc: "2026-10-08T01:00:01.000Z" } },
    { name: "missing_start", patch: { unity_process_start_utc: undefined } },
    { name: "package", patch: { package_version: "2026.10.8-7" } },
    { name: "missing_package", patch: { package_version: undefined } },
  ];
  it.each(identityDrift.flatMap((entry) => (["current", "replacement"] as const).map((role) => ({ ...entry, role }))))("does not retry eligible lag across $role $name drift", async ({ patch, role }) => {
    const harness = await makeHarness({ role, afterWritePatch: patch, persistent: "catalog_generation_mismatch" });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(harness.sleeps).toEqual([]);
  });

  it.each(identityDrift)("accepted CURRENT original-hash catalog after lag cannot authorize a status read across $name drift", async ({ patch }) => {
    const harness = await makeHarness({ role: "current", lags: ["catalog_generation_mismatch"], postLagRecordPatch: patch });
    const result = await harness.run();
    expectUnverified(result, harness, "catalog_stale");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, verification_read_request_count: 0, catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: "catalog_generation_mismatch", catalog_metadata_converged: true });
    const reads = statusDescribes(harness);
    expect(reads).toHaveLength(2);
    expect(reads[1]!.record).toMatchObject(patch);
    expect(reads[1]!.record.commands_hash).toBe(harness.bridge.described[0]!.record.commands_hash);
    expect(harness.bridge.errors).toHaveLength(1);
    expect(harness.registry.verificationCalls).toHaveLength(2);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it.each([
    { name: "source_trust", sourceDescriptorPatch: { trust: "untrusted_package" as const }, expectedPlayWriteCount: 0 as const },
    { name: "source_kind", sourceDescriptorPatch: { source: "manifest" as const }, expectedPlayWriteCount: 0 as const },
    { name: "source_package_id", sourceDescriptorPatch: { package_id: "com.other.extension" }, expectedPlayWriteCount: 0 as const },
    { name: "descriptor_package_version", sourceDescriptorPatch: { package_version: "2026.10.8-7" } },
    { name: "missing_descriptor_package_version", sourceDescriptorPatch: { package_version: undefined } },
    { name: "source_heartbeat_package", sourcePatch: { package_version: "2026.10.8-7" } },
    { name: "missing_source_heartbeat_package", sourcePatch: { package_version: undefined } },
    { name: "missing_source_process_start", sourcePatch: { unity_process_start_utc: undefined } },
  ])("requires original trusted exact source provenance before any retry: $name", async (options) => {
    const harness = await makeHarness({ ...options, role: "current", persistent: "commands_hash_mismatch" });
    const result = await harness.run();
    if (options.expectedPlayWriteCount === 0) {
      expect(result).toMatchObject({ status: "error", command: enter, error: { code: "schema_unavailable", details: { schema_lookup: { metadata_lookup: { tool: "sgg_unity_describe", arguments: { project: "game-source", selector: `command:${enter}` } } } } } });
      expect(result.next_valid_actions).toEqual(["sgg_unity_describe"]);
      expect(harness.invocations).toEqual([]);
      expect(harness.bridge.invokeAttempts).toEqual([]);
      expect(statusDescribes(harness)).toEqual([]);
      expect(harness.registry.verificationCalls).toEqual([]);
      expect(harness.sleeps).toEqual([]);
      expect(harness.approvalRequest).not.toHaveBeenCalled();
      expect(harness.approvalConsume).not.toHaveBeenCalled();
      expect(harness.approvalKey).not.toHaveBeenCalled();
      for (const field of ["playmode_transition_recovery_contract", "playmode_transition_operation_id", "playmode_target", "status_lookup", "status_check_required_before_repeat", "verification_command", "verification_attempt_count", "verification_read_request_count", "catalog_metadata_read_retry_count", "catalog_metadata_last_retry_reason", "catalog_metadata_converged"]) expect(evidence(result)).not.toHaveProperty(field);
      return;
    }
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, verification_read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(harness.sleeps).toEqual([]);
  });

  it.each([
    { name: "source_trust", patch: { trust: "untrusted_package" as const } },
    { name: "source_kind", patch: { source: "manifest" as const } },
    { name: "source_package_id", patch: { package_id: "com.other.extension" } },
  ])("an admitted pattern-free ORIGINAL $name descriptor cannot authorize metadata-lag retries", async ({ patch }) => {
    // The supported, pattern-free string schema admits the original write, so
    // these cases reach the strict metadata predicate rather than a schema gate.
    const harness = await makeHarness({ role: "current", persistent: "commands_hash_mismatch", sourceDescriptorPatch: { schema: patternFreeStatusSchema, ...patch } });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 1, verification_read_request_count: 0, catalog_metadata_read_retry_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.bridge.invokeAttempts.map((request) => request.command)).toEqual([enter]);
    expect(statusDescribes(harness)).toHaveLength(1);
    expect(harness.registry.verificationCalls).toHaveLength(1);
    expect(harness.bridge.errors).toHaveLength(1);
    expect(harness.bridge.errors[0]).toMatchObject({ message: "bridge_protocol_error", details: { endpoint: "/commands", failure_kind: "response_shape", protocol_schema: "sgg.unity_mcp.bridge.command_catalog.v1", protocol_reason: "commands_hash_mismatch" } });
    expect(harness.sleeps).toEqual([]);
    expect(harness.approvalRequest).not.toHaveBeenCalled();
    expect(harness.approvalConsume).not.toHaveBeenCalled();
    expect(harness.approvalKey).not.toHaveBeenCalled();
  });

  it.each([
    { name: "risk", patch: { risk: "write" as const } },
    { name: "trust", patch: { trust: "untrusted_package" as const } },
    { name: "source", patch: { source: "manifest" as const } },
    { name: "package_id", patch: { package_id: "com.other.extension" } },
    { name: "package_version", patch: { package_version: "2026.10.8-7" } },
    { name: "missing_package_version", patch: { package_version: undefined } },
    { name: "missing_command", patch: { id: "editor.other" } },
    { name: "schema", patch: { schema: { type: "object", properties: { _playmode_transition_expectation_id: { type: "integer" } }, additionalProperties: false } } },
  ])("does not weaken the fresh post-convergence verifier $name gate", async ({ patch }) => {
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], verificationDescriptorPatch: patch });
    const result = await harness.run();
    expectUnverified(result, harness, "catalog_stale");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: "commands_hash_mismatch", catalog_metadata_converged: true, verification_read_request_count: 0 });
    expect(statusDescribes(harness)).toHaveLength(2);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
  });

  it.each([
    { name: "untrusted", patch: { trust: "untrusted_package" as const } },
    { name: "manifest", patch: { source: "manifest" as const } },
    { name: "wrong_package_id", patch: { package_id: "com.other.extension" } },
    { name: "wrong_package_version", patch: { package_version: "2026.10.8-7" } },
    { name: "missing_package_version", patch: { package_version: undefined } },
  ])("accepted CURRENT original-hash catalog after lag cannot authorize a $name status descriptor", async ({ patch }) => {
    // A pattern-free correlation schema isolates provenance from the existing
    // schema-pattern restriction on untrusted descriptors.
    const harness = await makeHarness({ role: "current", lags: ["catalog_generation_mismatch"], verificationDescriptorPatch: { schema: patternFreeStatusSchema, ...patch } });
    const result = await harness.run();
    expectUnverified(result, harness, "catalog_stale");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, verification_read_request_count: 0, catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true });
    expect(statusDescribes(harness)[1]!.record.commands_hash).toBe(harness.bridge.described[0]!.record.commands_hash);
    expect(harness.bridge.errors).toHaveLength(1);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it("does not apply the new post-lag provenance gate to legacy CURRENT same-hash no-lag verification", async () => {
    const harness = await makeHarness({ role: "current", verificationDescriptorPatch: { trust: "untrusted_package", schema: patternFreeStatusSchema } });
    const result = await harness.run();
    expectVerified(result, harness, "current");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 1, verification_read_request_count: 1, catalog_metadata_read_retry_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(harness.invocations.map((request) => request.command)).toEqual([enter, editorStatus]);
  });

  it.each([
    { name: "policy_deny", statusPolicyAction: "deny" as const },
    { name: "policy_ask_even_with_auto_allow", statusPolicyAction: "ask" as const, autoAllowAsk: true },
    { name: "disabled_feature", verificationDescriptorPatch: { feature_flag: statusFeature } },
  ])("NO-LAG Gateway $name helper gate permits no status HTTP issuance or approval", async (options) => {
    const harness = await makeHarness({ ...options, role: "current" });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 1, verification_read_request_count: 0, catalog_metadata_read_retry_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(evidence(result)).not.toHaveProperty("approval_pending");
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.bridge.invokeAttempts.map((request) => request.command)).toEqual([enter]);
    expect(statusDescribes(harness)).toHaveLength(1);
    expect(harness.registry.verificationCalls).toHaveLength(1);
    expect(harness.bridge.errors).toEqual([]);
    expect(harness.sleeps).toEqual([]);
    expect(harness.approvalRequest).not.toHaveBeenCalled();
    expect(harness.approvalConsume).not.toHaveBeenCalled();
    expect(harness.approvalKey).not.toHaveBeenCalled();
  });

  it.each(["deny", "ask"] as const)("Gateway status policy %s after convergence permits no HTTP read or approval even with auto_allow_ask", async (statusPolicyAction) => {
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], statusPolicyAction, autoAllowAsk: true });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, verification_read_request_count: 0, catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true });
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.bridge.invokeAttempts.map((request) => request.command)).toEqual([enter]);
    expect(harness.approvalRequest).not.toHaveBeenCalled();
    expect(harness.approvalConsume).not.toHaveBeenCalled();
    expect(evidence(result)).not.toHaveProperty("approval_pending");
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it("Gateway feature gate on a fresh converged status descriptor permits no HTTP read or approval", async () => {
    const harness = await makeHarness({ lags: ["catalog_generation_mismatch"], verificationDescriptorPatch: { feature_flag: statusFeature }, autoAllowAsk: true });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, verification_read_request_count: 0, catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true });
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.bridge.invokeAttempts.map((request) => request.command)).toEqual([enter]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it("managed policy allowed at the helper precheck but denied by prepared refresh issues no status request", async () => {
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], managedPolicyRace: true, autoAllowAsk: true });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, verification_read_request_count: 0, catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true });
    expect(harness.managedPolicyRace?.refreshActions).toEqual(["allow", "deny", "deny"]);
    expect(harness.managedPolicyRace?.manager.policy?.projects.game?.deny).toContain(editorStatus);
    expect(harness.managedPolicyRace?.manager.snapshot()).toMatchObject({ state: "active", configured: true, degraded: false });
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.bridge.invokeAttempts.map((request) => request.command)).toEqual([enter]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it("a feature revoked immediately before transport issuance is guarded and not counted as a status read", async () => {
    const harness = await makeHarness({ lags: ["catalog_generation_mismatch"], verificationDescriptorPatch: { feature_flag: statusFeature }, disableFeatureBeforeStatusRequest: true });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, verification_read_request_count: 0, catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true });
    expect(harness.bridge.invokeAttempts.map((request) => request.command)).toEqual([enter, editorStatus]);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  // Bridge refusals remain terminal even when the Gateway's own read gates allow.
  it.each(["permission_denied", "feature_disabled"] as const)("does not turn a post-convergence bridge %s refusal into Play Mode success", async (statusRefusal) => {
    const harness = await makeHarness({ lags: ["catalog_generation_mismatch"], statusRefusal });
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true, verification_read_request_count: 1 });
    expect(harness.invocations.map((request) => request.command)).toEqual([enter, editorStatus]);
    expect(harness.sleeps).toEqual([pollMs]);
  });

  it.each([
    { name: "operation", transitionPatch: { operation_id: "0".repeat(32) } },
    { name: "target", transitionPatch: { target: "editing" } },
    { name: "state", transitionPatch: { state: "pending" } },
    { name: "requested", transitionPatch: { requested: false } },
    { name: "completed", transitionPatch: { completed: false } },
    { name: "reload_proof", transitionPatch: { post_reload_observed: false } },
    { name: "replacement_even_if_reload_not_expected", transitionPatch: { reload_expected: false, post_reload_observed: false } },
    { name: "post_reload_timestamp", transitionPatch: { post_reload_observed_utc: "" } },
    { name: "completion_precedes_request", transitionPatch: { completed_utc: "2026-10-08T00:59:59.000Z" } },
    { name: "compiling", statusPatch: { is_compiling: true } },
    { name: "updating", statusPatch: { is_updating: true } },
    { name: "actual_target", statusPatch: { is_playing_actual: false } },
    { name: "status", statusPatch: { status: "editing" } },
    { name: "compile_barrier_without_playmode_transition", omitTransition: true },
  ])("converged ready flags cannot substitute for exact Play Mode $name evidence", async (options) => {
    const harness = await makeHarness({ ...options, lags: ["catalog_generation_mismatch"] });
    const result = await harness.run();
    expectUnverified(result, harness, "timeout");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true });
    expect(Number(evidence(result).verification_read_request_count)).toBeGreaterThan(0);
    expect(Number(evidence(result).verification_read_request_count)).toBe(harness.invocations.length - 1);
    expect(harness.elapsedMs()).toBe(timeoutMs);
  });

  it("cannot verify editing while Unity still reports a pending Play Mode change", async () => {
    const harness = await makeHarness({ command: exit, lags: ["commands_hash_mismatch"], statusPatch: { is_playing_or_will_change_playmode: true } });
    const result = await harness.run();
    expectUnverified(result, harness, "timeout");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true, playmode_target: "editing" });
  });

  it.each(["success", "unity_reloading", "command_timeout"] as const)("preserves original %s issuance uncertainty on persistent lag", async (writeOutcome) => {
    const harness = await makeHarness({ persistent: "commands_hash_mismatch", writeOutcome });
    const result = await harness.run();
    expectUnverified(result, harness, "catalog_stale", writeOutcome === "success" ? null : writeOutcome);
    expect(evidence(result)).toMatchObject({ original_command_completed: "unknown", delivery_state: "dispatched", catalog_metadata_converged: false, verification_read_request_count: 0 });
  });
});

function evidence(result: GatewayResult): Record<string, unknown> {
  const value = result.status === "ok" ? result.data : result.error.details;
  expect(value, "Play Mode transition evidence").toBeTypeOf("object");
  expect(JSON.stringify(result)).not.toContain(rejectedPayloadMarker);
  const details = value as Record<string, unknown>;
  if (details.catalog_metadata_last_retry_reason === undefined) expect(details).not.toHaveProperty("catalog_metadata_converged");
  else expect(["catalog_generation_mismatch", "commands_hash_mismatch"]).toContain(details.catalog_metadata_last_retry_reason);
  return details;
}

function expectVerified(result: GatewayResult, harness: Harness, role: Role): void {
  const operationId = harness.invocations[0]!.args._playmode_transition_expectation_id;
  const hashChanged = harness.registry.current.commands_hash !== harness.bridge.described[0]!.record.commands_hash;
  expect(result).toMatchObject({ status: "ok", command: harness.command, data: { status: harness.target, playmode_target: harness.target, playmode_transition_operation_id: operationId, playmode_transition_verified: true, verification_command: editorStatus, verification_instance_role: role, verification_catalog_hash_changed: hashChanged, original_command_completed: true, write_command_repeated: false, write_replay_allowed: false, command_replayed: false, playmode_transition: { state: "verified", operation_id: operationId, target: harness.target, requested: true, completed: true } } });
  expect(result.next_valid_actions).toEqual([harness.target === "playing" ? "continue_in_play_mode" : "continue_in_edit_mode", "optionally_call_editor.status"]);
  expect(evidence(result)).not.toHaveProperty("compile_refresh_verification");
}

function expectUnverified(result: GatewayResult, harness: Harness, status: string, originalError: string | null = "bridge_protocol_error"): void {
  const operationId = harness.invocations[0]!.args._playmode_transition_expectation_id;
  expect(result).toMatchObject({ status: "error", command: harness.command, error: { code: "unity_busy", details: { reason: "playmode_transition_unverified", verification_status: status, verification_command: editorStatus, playmode_transition_recovery_contract: "sgg.unity_mcp.playmode_transition_recovery.v1", playmode_transition_operation_id: operationId, operation_id_source: "gateway_reserved", playmode_target: harness.target, status_check_required_before_repeat: true, delivery_state: "dispatched", original_error_code: originalError, original_command_completed: "unknown", write_command_repeated: false, write_replay_allowed: false, command_replayed: false, status_lookup: { tool: "sgg_unity_call", project: "c:/u/game", command: editorStatus, args_json: JSON.stringify({ _playmode_transition_expectation_id: operationId }), mode: "wait" } } } });
  expect(result.next_valid_actions).toEqual(["poll_exact_editor.status", "do_not_auto_repeat_playmode_command", "call_sgg_unity_projects"]);
  expect(evidence(result)).not.toHaveProperty("playmode_transition_verified", true);
  expect(evidence(result)).not.toHaveProperty("compile_refresh_verification");
}

function statusDescribes(harness: Harness) {
  return harness.bridge.described.filter((entry) => entry.selector === `command:${editorStatus}`);
}

async function makeHarness(options: HarnessOptions = {}): Promise<Harness> {
  let clock = Date.now();
  let verificationStartedAt = clock;
  let writeIssued = false;
  let pendingHeartbeat: HeartbeatRecord | undefined;
  let postWriteCatalogCalls = 0;
  const command = options.command ?? enter;
  const target = options.target ?? (command === exit ? "editing" : "playing");
  const role = options.role ?? "replacement";
  const invocations: BridgeInvokeRequest[] = [];
  const handlerErrors: string[] = [];
  const sleeps: number[] = [];
  const audit = new InMemoryAuditSink();
  const nativeRead = vi.fn(() => { throw new Error("Native inspection is outside this test"); });
  const nativeMutation = vi.fn(() => { throw new Error("Native mutation is forbidden"); });
  const approvalStore = new ApprovalStore("/virtual/never-created-playmode-approval-store");
  const approvalRequest = vi.spyOn(approvalStore, "request").mockImplementation(() => { throw new Error("Automatic verification must not request approval"); });
  const approvalConsume = vi.spyOn(approvalStore, "consume").mockImplementation(() => { throw new Error("Automatic verification must not consume approval"); });
  const approvalKey = vi.fn(() => { throw new Error("Automatic verification must not access an approval key"); });
  const features: Record<string, boolean> = options.disableFeatureBeforeStatusRequest ? { [statusFeature]: true } : {};
  const managedPolicyRace = options.managedPolicyRace ? makeManagedPolicyRace(command, () => new Date(clock)) : undefined;
  const registry = new MemoryRegistry(heartbeat({ ...(command === exit ? { status: "playing" as const } : {}), ...options.sourcePatch }), () => new Date(clock), options.resolutionFaults);
  const sourceDescriptor = descriptor(command, "write", options.sourceDescriptorPatch);
  const verificationDescriptor = descriptor(editorStatus, "read", options.verificationDescriptorPatch);
  const server = createServer((request, response) => {
    void handle(request, response).catch((error: unknown) => {
      handlerErrors.push(String(error));
      response.writeHead(500, { "content-type": "application/json" }).end("{}");
    });
  });
  await new Promise<void>((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("Expected a loopback test port");
  registry.current.bridge_port = address.port;
  const source = structuredClone(registry.current);
  const bridge = new RecordingHttpBridge(() => writeIssued, options.injectedError, options.disableFeatureBeforeStatusRequest ? () => { features[statusFeature] = false; } : undefined);
  const context: DispatcherContext = {
    registry, bridge, audit, policy: allowingPolicy(command, options.statusPolicyAction), features,
    approvalStore, approvalBindingService: new ApprovalBindingService({ getKey: approvalKey }), approvalAutoAllowAsk: options.autoAllowAsk ?? false,
    ...(managedPolicyRace ? { policyManager: managedPolicyRace.manager } : {}),
    now: () => new Date(clock),
    sleepMs: async (duration) => {
      sleeps.push(duration);
      if (!options.frozenClock) clock += duration;
      if (pendingHeartbeat) { registry.current = { ...pendingHeartbeat, ...options.postLagRecordPatch }; pendingHeartbeat = undefined; }
    },
    unityProcessDiagnostics: { status: nativeRead, terminate: nativeMutation },
  };
  const harness: Harness = {
    server, registry, bridge, invocations, handlerErrors, sleeps, audit, nativeRead, nativeMutation, approvalRequest, approvalConsume, approvalKey, command, target,
    expectedPlayWriteCount: options.expectedPlayWriteCount ?? 1,
    ...(managedPolicyRace ? { managedPolicyRace } : {}),
    elapsedMs: () => clock - verificationStartedAt,
    run: () => {
      clock = Date.now();
      return dispatchTool("sgg_unity_call", { project: "game-source", command, request_id: "playmode-catalog-convergence", mode: "wait", args_json: JSON.stringify(command === set ? { state: target } : {}) }, context);
    },
  };
  harnesses.push(harness);
  return harness;

  async function handle(request: IncomingMessage, response: ServerResponse): Promise<void> {
    if (request.url === "/commands") {
      const record = registry.current;
      const catalog: Record<string, unknown> = { schema_version: 1, project: record.project, instance_id: record.instance_id, catalog_generation: record.catalog_generation, commands_hash: record.commands_hash, commands: [sourceDescriptor, verificationDescriptor] };
      if (writeIssued) {
        const lag = options.persistent ?? options.lags?.[postWriteCatalogCalls];
        postWriteCatalogCalls += 1;
        if (lag) {
          const updated = { ...record, ...(lag === "catalog_generation_mismatch" ? { catalog_generation: record.catalog_generation! + 1 } : { commands_hash: `hash-${postWriteCatalogCalls + 2}` }) };
          catalog.catalog_generation = updated.catalog_generation;
          catalog.commands_hash = updated.commands_hash;
          catalog.commands = [sourceDescriptor, { ...verificationDescriptor, risk: "destructive", description: rejectedPayloadMarker }];
          if (!options.persistent) pendingHeartbeat = updated;
        } else managedPolicyRace?.arm();
        switch (options.catalogFault) {
          case "project": catalog.project = "wrong-project"; break;
          case "instance": catalog.instance_id = "wrong-instance"; break;
          case "schema_version": catalog.schema_version = 2; break;
          case "payload_shape": catalog.commands = null; break;
          case "duplicate_commands": catalog.commands = [verificationDescriptor, verificationDescriptor]; break;
          case "invalid_json": response.writeHead(200, { "content-type": "application/json" }).end("{broken"); return;
          case "content_type": response.writeHead(200, { "content-type": "text/html" }).end(JSON.stringify(catalog)); return;
          case "http_401": response.writeHead(401, { "content-type": "application/json" }).end("{}"); return;
          case "http_500": response.writeHead(500, { "content-type": "application/json" }).end("{}"); return;
        }
      }
      response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(catalog));
      return;
    }
    if (request.url !== "/invoke" || request.method !== "POST") throw new Error("Unexpected bridge endpoint");
    let body = "";
    for await (const chunk of request) body += String(chunk);
    const input = JSON.parse(body) as BridgeInvokeRequest;
    invocations.push(input);
    if (input.command === command) {
      if (writeIssued) throw new Error("Play Mode write was replayed");
      writeIssued = true;
      // Consume the existing write/settlement envelope on the virtual clock,
      // leaving a small verification window inside the ORIGINAL Play deadline.
      // No command arguments or new deadline behavior are invented by the test.
      clock += sourceDescriptor.timeout_ms + playModeStabilityOverheadMs - (options.timeoutMs ?? timeoutMs);
      verificationStartedAt = clock;
      registry.current = { ...source, instance_id: role === "replacement" ? "game-replacement" : source.instance_id, status: "ready", ...(role === "replacement" ? { commands_hash: "hash-2", catalog_generation: 2 } : {}), ...options.afterWritePatch };
      if (!options.writeOutcome) {
        response.writeHead(200, { "content-type": "application/json" }).end();
        return;
      }
      const writeResponse = options.writeOutcome === "success"
        ? { status: "ok", data: { status: "transition_pending", playmode_transition_requested: true } }
        : { status: "error", error: { code: options.writeOutcome, message: "Synthetic issued Play Mode interruption", details: options.writeOutcome === "command_timeout" ? { main_thread_dispatch: { timed_out_work_state: "running", late_execution_possible: true } } : { reason: "bridge_lifecycle_shutdown" } } };
      response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ schema_version: 1, request_id: input.request_id, ...writeResponse, diagnostics: [], duration_ms: 0 }));
      return;
    }
    if (input.command !== editorStatus) throw new Error("Unexpected verifier command");
    if (options.statusBlockerDetails) {
      response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ schema_version: 1, request_id: input.request_id, status: "error", error: { code: "blocked_external_scene_reload", message: "Synthetic PID-correlated external scene reload blocker", details: options.statusBlockerDetails }, diagnostics: [], duration_ms: 0 }));
      return;
    }
    if (options.statusRefusal) {
      response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ schema_version: 1, request_id: input.request_id, status: "error", error: { code: options.statusRefusal, message: "Synthetic verifier refused" }, diagnostics: [], duration_ms: 0 }));
      return;
    }
    const operationId = String(input.args._playmode_transition_expectation_id);
    const transition = { ...verifiedTransition(operationId, target), ...options.transitionPatch };
    const actual = target === "playing";
    const data = {
      status: target, is_compiling: false, is_updating: false, is_playing: actual, is_playing_actual: actual, is_playing_or_will_change_playmode: actual,
      // These unrelated ready/compile flags deliberately cannot replace the
      // exact Play Mode operation receipt or its lifecycle proof.
      ready: true, editor_ready: true, compilable: true, lifecycle_settled: true, has_compile_errors: false, compile_error_count: 0,
      compile_barrier: verifiedCompileBarrier(operationId, source.instance_id, registry.current.instance_id),
      ...(options.omitTransition ? {} : { playmode_transition: transition }),
      ...options.statusPatch,
    };
    response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ schema_version: 1, request_id: input.request_id, status: "ok", data, diagnostics: [], duration_ms: 0 }));
  }
}

// Selected synthetic records are intentionally not prefiltered. No registry
// directory, real project, Unity process, or filesystem snapshot is touched.
class MemoryRegistry extends ProjectRegistry {
  readonly verificationCalls: Array<{ root: string; instance: string; record: HeartbeatRecord }> = [];
  constructor(public current: HeartbeatRecord, now: () => Date, readonly faults: Record<number, ResolutionFault> = {}) {
    super({ instancesDir: "/virtual/never-created-playmode-catalog-registry", staleAfterMs: 90_000, now });
  }
  override resolveProject(): ProjectResolution { return { status: "ok", record: structuredClone(this.current) }; }
  override resolveVerificationProject(root: string, instance: string): VerificationProjectResolution {
    const fault = this.faults[this.verificationCalls.length];
    const record = structuredClone(this.current);
    if (fault === "reloading") { record.status = "reloading"; record.bridge_port = 0; }
    this.verificationCalls.push({ root, instance, record });
    if (fault === "throw") throw new Error("Synthetic registry unavailable");
    if (fault === "project_not_found" || fault === "duplicate_project_id") return { status: "error", code: fault };
    return { status: "ok", role: record.instance_id === instance ? "current" : "replacement", record };
  }
  override listProjects(): PublicProjectRecord[] { return this.listProjectRecords(); }
  override listProjectRecords(): PublicProjectRecord[] { return [{ ...this.current, stale: false } as PublicProjectRecord]; }
  override captureSnapshot(): never { throw new Error("Filesystem registry snapshot is forbidden in this fixture"); }
}

class RecordingHttpBridge extends HttpUnityBridgeClient {
  readonly described: Array<{ record: HeartbeatRecord; selector: string }> = [];
  readonly errors: Error[] = [];
  readonly invokeAttempts: BridgeInvokeRequest[] = [];
  logCalls = 0;
  private postWriteDescribeCalls = 0;
  constructor(readonly afterWrite: () => boolean, readonly injectedError?: (record: HeartbeatRecord, index: number) => Error | undefined, readonly beforeStatusRequest?: () => void) { super(); }
  override async describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    this.described.push({ record: structuredClone(record), selector });
    try {
      if (this.afterWrite()) {
        const error = this.injectedError?.(record, this.postWriteDescribeCalls++);
        if (error) throw error;
      }
      return await super.describe(record, selector, transport);
    } catch (error) {
      if (error instanceof Error) this.errors.push(error);
      throw error;
    }
  }
  override async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions) {
    this.invokeAttempts.push(structuredClone(request));
    if (request.command === editorStatus) this.beforeStatusRequest?.();
    return super.invoke(record, request, transport);
  }
  override logs(): Promise<unknown> { this.logCalls += 1; throw new Error("Logs are outside this focused test"); }
}

function metadataError(record: HeartbeatRecord, patch: ErrorDetailPatch = {}): BridgeRequestError {
  const details: BridgeRequestErrorDetails = { endpoint: "/commands", bridge_host: record.bridge_host, bridge_port: record.bridge_port, failure_kind: "response_shape", protocol_schema: "sgg.unity_mcp.bridge.command_catalog.v1", protocol_reason: "catalog_generation_mismatch" };
  for (const [key, value] of Object.entries(patch)) {
    if (value === undefined) Reflect.deleteProperty(details, key);
    else Reflect.set(details, key, value);
  }
  return new BridgeRequestError("bridge_protocol_error", details);
}

function descriptor(id: string, risk: CommandDescriptor["risk"], patch: Partial<CommandDescriptor> = {}): CommandDescriptor {
  const properties: Record<string, unknown> = { _playmode_transition_expectation_id: { type: "string", pattern: "^[0-9a-f]{32}$" } };
  if (id === set) properties.state = { type: "string", enum: ["playing", "editing"] };
  return { id, title: id, description: id, package: "com.sungeargames.unity-mcp", package_id: "com.sungeargames.unity-mcp", package_version: packageVersion, source: "builtin", trust: "builtin", risk, idempotency: risk === "read" ? "safe" : "idempotent", timeout_ms: 10_000, schema: { type: "object", properties, ...(id === set ? { required: ["state"] } : {}), additionalProperties: false }, ...patch };
}

function heartbeat(patch: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return { schema_version: 1, project: "game", display_name: "Game", instance_id: "game-source", project_root: "C:/u/game", canonical_project_root: "c:/u/game", aliases: [], unity_version: "6000.6.2f1", unity_pid: 12345, unity_process_start_utc: "2026-10-08T01:00:00.000Z", package_version: packageVersion, bridge_host: "127.0.0.1", bridge_port: 1, token: "synthetic-test-token", status: "ready", commands_hash: "hash-1", catalog_generation: 1, last_heartbeat: new Date().toISOString(), ...patch };
}

function allowingPolicy(command: PlayCommand, statusAction?: "deny" | "ask"): PolicyConfig {
  // Admission explicitly permits the write and verification read. The unrelated
  // trusted extension permits source-provenance negative cases to reach issuance;
  // it does not satisfy the stricter built-in metadata convergence predicate.
  return { schema_version: 1, defaults: { read: "allow", write: "allow", execute: "deny", destructive: "deny", network: "deny" }, trusted_packages: ["com.other.extension"], untrusted_package_default: "ask", projects: { game: { allow: statusAction ? [command] : [command, editorStatus], ask: statusAction === "ask" ? [editorStatus] : [], deny: statusAction === "deny" ? [editorStatus] : [] } } };
}

type ManagedPolicyRace = { manager: PolicyManager; refreshActions: Array<"allow" | "deny">; arm: () => void };

function makeManagedPolicyRace(command: PlayCommand, now: () => Date): ManagedPolicyRace {
  let raw = Buffer.from(JSON.stringify(allowingPolicy(command)));
  let revision = 1n;
  let armed = false;
  const refreshActions: Array<"allow" | "deny"> = [];
  const manager = new PolicyManager("/virtual/never-created-playmode-managed-policy.json", {
    now,
    fileSystem: {
      statSync: () => ({ dev: 1n, ino: 1n, size: BigInt(raw.byteLength), mtimeNs: revision, ctimeNs: revision }) as BigIntStats,
      readFileSync: () => Buffer.from(raw),
    },
  });
  const reload = manager.reload.bind(manager);
  vi.spyOn(manager, "reload").mockImplementation((startup) => {
    if (armed) {
      // The explicit helper precheck refresh sees allow. The next refresh is
      // executePreparedInvocation's refresh and observes a new valid deny policy.
      const action = refreshActions.length === 0 ? "allow" : "deny";
      refreshActions.push(action);
      if (action === "deny") { raw = Buffer.from(JSON.stringify(allowingPolicy(command, "deny"))); revision += 1n; }
    }
    return reload(startup);
  });
  return { manager, refreshActions, arm: () => { armed = true; } };
}

function verifiedTransition(operationId: string, target: Target): Record<string, unknown> {
  return { state: "verified", operation_id: operationId, target, requested: true, transition_requested: true, completed: true, reload_expected: true, requested_utc: "2026-10-08T01:00:00.000Z", completed_utc: "2026-10-08T01:00:00.010Z", post_reload_observed: true, post_reload_observed_utc: "2026-10-08T01:00:00.020Z" };
}

function verifiedCompileBarrier(operationId: string, source: string, replacement: string): Record<string, unknown> {
  const at = (offset: number) => new Date(Date.parse("2026-10-08T01:00:00.000Z") + offset).toISOString();
  return { state: "verified", required: true, operation_id: operationId, requested: true, request_issued: true, started: true, finished: true, source_instance_id: source, reload_source_instance_id: source, replacement_instance_id: replacement, terminal_compile_result: "clean", terminal_compile_error_count: 0, terminal_compile_failure_without_reload: false, reload_started: true, post_reload_observed: true, requested_utc: at(0), request_issued_utc: at(10), started_utc: at(20), finished_utc: at(30), terminal_compile_observed_utc: at(30), reload_started_utc: at(35), post_reload_observed_utc: at(40), observed_generation: 1, request_generation: 1, started_generation: 1, finished_generation: 1 };
}

type LatePolicyBoundary = "async_yield" | "before_request_issued";
type PolicyRevocation = "deny" | "ask";
type FilePolicyHarness = Harness & {
  root: string;
  policyPath: string;
  manager: PolicyManager;
  initialPolicyRevision: string | undefined;
  reloadActions: Array<"allow" | PolicyRevocation>;
  events: string[];
  revocationCount: () => number;
  statusIssuedCount: () => number;
};
const filePolicyHarnesses: FilePolicyHarness[] = [];
const filePolicyRoots: string[] = [];

// These real-file fixtures are intentionally separate from the original 118
// synthetic controls. Every filesystem mutation/cleanup is confined to a root
// created by this fixture; no project, shared registry, or approval store is read.
afterEach(async () => {
  try {
    for (const harness of filePolicyHarnesses.splice(0)) {
      harness.server.closeAllConnections();
      await new Promise<void>((resolve, reject) => harness.server.close((error) => error ? reject(error) : resolve()));
      expect(harness.handlerErrors).toEqual([]);
      expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
      expect(harness.bridge.logCalls).toBe(0);
      expect(harness.nativeRead).not.toHaveBeenCalled();
      expect(harness.nativeMutation).not.toHaveBeenCalled();
      expect(harness.approvalRequest).not.toHaveBeenCalled();
      expect(harness.approvalConsume).not.toHaveBeenCalled();
      expect(harness.approvalKey).not.toHaveBeenCalled();
      expect(JSON.stringify(harness.audit.events)).not.toContain(rejectedPayloadMarker);
    }
  } finally {
    for (const root of filePolicyRoots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
  }
});

describe("MCP-PR-226 real managed-policy revocation at late status issuance boundaries", () => {
  it.each([
    { boundary: "async_yield", action: "deny" },
    { boundary: "async_yield", action: "ask" },
    { boundary: "before_request_issued", action: "deny" },
    { boundary: "before_request_issued", action: "ask" },
  ] satisfies Array<{ boundary: LatePolicyBoundary; action: PolicyRevocation }>)("allow -> $action during $boundary cannot issue a status HTTP request", async ({ boundary, action }) => {
    const harness = await makeFilePolicyHarness(boundary, action);
    const result = await harness.run();
    expectUnverified(result, harness, "unavailable");
    expect(evidence(result)).toMatchObject({ verification_attempt_count: 2, verification_read_request_count: 0, catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: "catalog_generation_mismatch", catalog_metadata_converged: true });
    expect(harness.statusIssuedCount()).toBe(0);
    expect(harness.invocations.map((request) => request.command)).toEqual([enter]);
    expect(harness.bridge.invokeAttempts.map((request) => request.command)).toEqual(boundary === "async_yield" ? [enter] : [enter, editorStatus]);
    expect(harness.revocationCount()).toBe(1);
    const allowReloads = boundary === "async_yield" ? 2 : 3;
    expect(harness.reloadActions).toEqual([...Array.from({ length: allowReloads }, () => "allow"), action]);
    expect(harness.events).toEqual([
      "accepted_catalog",
      ...Array.from({ length: allowReloads }, (_, index) => `reload:${index + 1}:allow`),
      `revoke:${boundary}:${action}`,
      `reload:${allowReloads + 1}:${action}`,
    ]);
    expect(harness.manager.policy?.projects.game?.[action]).toContain(editorStatus);
    expect(harness.manager.effectivePolicyRevision).not.toBe(harness.initialPolicyRevision);
    expect(harness.manager.snapshot()).toMatchObject({ state: "active", configured: true, degraded: false });
    expect(JSON.parse(fs.readFileSync(harness.policyPath, "utf8"))).toEqual(allowingPolicy(enter, action));
    expect(statusDescribes(harness)).toHaveLength(2);
    expect(harness.bridge.errors).toHaveLength(1);
    expect(harness.registry.verificationCalls).toHaveLength(2);
    expect(harness.sleeps).toEqual([pollMs]);
    expect(harness.elapsedMs()).toBe(pollMs);
    expect(harness.approvalRequest).not.toHaveBeenCalled();
    expect(harness.approvalConsume).not.toHaveBeenCalled();
    expect(harness.approvalKey).not.toHaveBeenCalled();
    expect(JSON.stringify([result, harness.audit.events])).not.toContain(harness.root);
  });
});

async function makeFilePolicyHarness(boundary: LatePolicyBoundary, action: PolicyRevocation): Promise<FilePolicyHarness> {
  const preferredTemp = path.join(os.tmpdir(), "opencode");
  const tempParent = fs.existsSync(preferredTemp) ? preferredTemp : os.tmpdir();
  const root = fs.mkdtempSync(path.join(tempParent, "sgg-pr-226-late-policy-"));
  filePolicyRoots.push(root);
  const policyPath = path.join(root, "policy.json");
  fs.writeFileSync(policyPath, JSON.stringify(allowingPolicy(enter)));
  let clock = Date.now();
  let verificationStartedAt = clock;
  let writeIssued = false;
  let pendingHeartbeat: HeartbeatRecord | undefined;
  let postWriteCatalogCalls = 0;
  let armed = false;
  let revocations = 0;
  const events: string[] = [];
  const reloadActions: Array<"allow" | PolicyRevocation> = [];
  // No injected filesystem: the actual PolicyManager performs its ordinary
  // bounded stat/read/stat, JSON parsing, identity caching and revision hashing.
  const manager = new PolicyManager(policyPath, { now: () => new Date(clock) });
  const initialPolicyRevision = manager.effectivePolicyRevision;
  const reload = manager.reload.bind(manager);
  vi.spyOn(manager, "reload").mockImplementation((startup) => {
    const changed = reload(startup);
    if (armed) {
      const projectPolicy = manager.policy?.projects.game;
      const loadedAction = projectPolicy?.deny.includes(editorStatus) ? "deny" : projectPolicy?.ask.includes(editorStatus) ? "ask" : "allow";
      reloadActions.push(loadedAction);
      events.push(`reload:${reloadActions.length}:${loadedAction}`);
      if (boundary === "async_yield" && reloadActions.length === 2) {
        // The second armed reload is executePreparedInvocation's refresh. Both
        // it and the helper precheck saw allow. The queued mutation runs at the
        // subsequent await invokeLocal() yield, before the pre-dispatch guard.
        queueMicrotask(() => revoke("async_yield"));
      }
    }
    return changed;
  });
  const registry = new MemoryRegistry(heartbeat(), () => new Date(clock));
  const invocations: BridgeInvokeRequest[] = [];
  const handlerErrors: string[] = [];
  const sleeps: number[] = [];
  const audit = new InMemoryAuditSink();
  const nativeRead = vi.fn(() => { throw new Error("Late policy verification must not inspect native state"); });
  const nativeMutation = vi.fn(() => { throw new Error("Late policy verification must not mutate native state"); });
  const approvalStore = new ApprovalStore(path.join(root, "never-created-approvals"));
  const approvalRequest = vi.spyOn(approvalStore, "request").mockImplementation(() => { throw new Error("Late policy revocation must not request approval"); });
  const approvalConsume = vi.spyOn(approvalStore, "consume").mockImplementation(() => { throw new Error("Late policy revocation must not consume approval"); });
  const approvalKey = vi.fn(() => { throw new Error("Late policy revocation must not access an approval key"); });
  const sourceDescriptor = descriptor(enter, "write");
  const statusDescriptor = descriptor(editorStatus, "read");
  const server = createServer((request, response) => {
    void handle(request, response).catch((error: unknown) => {
      handlerErrors.push(String(error));
      response.writeHead(500, { "content-type": "application/json" }).end("{}");
    });
  });
  await new Promise<void>((resolve, reject) => { server.once("error", reject); server.listen(0, "127.0.0.1", resolve); });
  const address = server.address();
  if (!address || typeof address === "string") throw new Error("Expected a loopback late-policy test port");
  registry.current.bridge_port = address.port;
  const source = structuredClone(registry.current);
  const bridge = new LatePolicyHttpBridge(() => writeIssued, boundary, () => revoke("before_request_issued"));
  const context: DispatcherContext = {
    registry, bridge, audit, policyManager: manager,
    approvalStore, approvalBindingService: new ApprovalBindingService({ getKey: approvalKey }), approvalAutoAllowAsk: true,
    now: () => new Date(clock),
    sleepMs: async (duration) => {
      sleeps.push(duration);
      clock += duration;
      if (pendingHeartbeat) { registry.current = pendingHeartbeat; pendingHeartbeat = undefined; }
    },
    unityProcessDiagnostics: { status: nativeRead, terminate: nativeMutation },
  };
  const harness: FilePolicyHarness = {
    server, registry, bridge, invocations, handlerErrors, sleeps, audit, nativeRead, nativeMutation, approvalRequest, approvalConsume, approvalKey,
    command: enter, target: "playing", expectedPlayWriteCount: 1, root, policyPath, manager, initialPolicyRevision, reloadActions, events,
    revocationCount: () => revocations,
    statusIssuedCount: () => bridge.statusIssuedCount,
    elapsedMs: () => clock - verificationStartedAt,
    run: () => {
      clock = Date.now();
      return dispatchTool("sgg_unity_call", { project: "game-source", command: enter, request_id: "playmode-late-managed-policy", mode: "wait", args_json: "{}" }, context);
    },
  };
  filePolicyHarnesses.push(harness);
  return harness;

  function revoke(at: LatePolicyBoundary): void {
    if (revocations !== 0) throw new Error("Late policy revocation was repeated");
    expect(reloadActions).toEqual(Array.from({ length: at === "async_yield" ? 2 : 3 }, () => "allow"));
    const stagedPath = path.join(root, "policy-next.json");
    // Atomic replacement changes file identity; whitespace also distinguishes
    // size from the original file without changing or weakening policy schema.
    fs.writeFileSync(stagedPath, `${JSON.stringify(allowingPolicy(enter, action))}${"\n".repeat(32)}`);
    fs.renameSync(stagedPath, policyPath);
    revocations += 1;
    events.push(`revoke:${at}:${action}`);
  }

  async function handle(request: IncomingMessage, response: ServerResponse): Promise<void> {
    if (request.url === "/commands") {
      const record = registry.current;
      const catalog = { schema_version: 1, project: record.project, instance_id: record.instance_id, catalog_generation: record.catalog_generation, commands_hash: record.commands_hash, commands: [sourceDescriptor, statusDescriptor] };
      if (writeIssued) {
        postWriteCatalogCalls += 1;
        if (postWriteCatalogCalls === 1) {
          pendingHeartbeat = { ...record, catalog_generation: record.catalog_generation! + 1 };
          catalog.catalog_generation = pendingHeartbeat.catalog_generation;
          catalog.commands = [sourceDescriptor, { ...statusDescriptor, risk: "destructive", description: rejectedPayloadMarker }];
        } else if (!armed) {
          armed = true;
          events.push("accepted_catalog");
        }
      }
      response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify(catalog));
      return;
    }
    if (request.url !== "/invoke" || request.method !== "POST") throw new Error("Unexpected late-policy bridge endpoint");
    let body = "";
    for await (const chunk of request) body += String(chunk);
    const input = JSON.parse(body) as BridgeInvokeRequest;
    invocations.push(input);
    if (input.command === enter) {
      if (writeIssued) throw new Error("Late policy race replayed the Play Mode write");
      writeIssued = true;
      clock += sourceDescriptor.timeout_ms + playModeStabilityOverheadMs - timeoutMs;
      verificationStartedAt = clock;
      registry.current = { ...source, instance_id: "game-replacement", commands_hash: "hash-2", catalog_generation: 2 };
      response.writeHead(200, { "content-type": "application/json" }).end();
      return;
    }
    if (input.command !== editorStatus) throw new Error("Unexpected late-policy command");
    // A missing refresh would perform a real HTTP read and receive valid target
    // evidence. The fixture does not substitute a fabricated transport refusal.
    response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ schema_version: 1, request_id: input.request_id, status: "ok", data: {
      status: "playing", is_compiling: false, is_updating: false, is_playing_actual: true, is_playing_or_will_change_playmode: true,
      playmode_transition: verifiedTransition(String(input.args._playmode_transition_expectation_id), "playing"),
    }, diagnostics: [], duration_ms: 0 }));
  }
}

class LatePolicyHttpBridge extends RecordingHttpBridge {
  statusIssuedCount = 0;
  constructor(afterWrite: () => boolean, readonly boundary: LatePolicyBoundary, readonly revoke: () => void) { super(afterWrite); }
  override async invoke(record: HeartbeatRecord, request: BridgeInvokeRequest, transport?: BridgeTransportOptions) {
    if (request.command !== editorStatus) return super.invoke(record, request, transport);
    return super.invoke(record, request, {
      ...transport,
      beforeRequestIssued: () => {
        // Preserve the real HTTP client's last-before-fetch callback and the
        // Gateway's own guard; only replace the owned policy file just before it.
        if (this.boundary === "before_request_issued") this.revoke();
        transport?.beforeRequestIssued?.();
      },
      onRequestIssued: () => {
        this.statusIssuedCount += 1;
        transport?.onRequestIssued?.();
      },
    });
  }
}
