import fs from "node:fs";
import { createHash } from "node:crypto";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { ApprovalStore } from "../src/approvals/approval-store.js";
import { isKnownUnityPopupResponse, KnownPopupPermissionChanged } from "../src/approvals/known-unity-popups.js";
import { KnownUnityPopupSettingsManager } from "../src/approvals/known-unity-popup-settings.js";
import { FakeUnityBridgeClient, compileStatusCommand } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import type { UnityProcessListResult, UnityProcessSnapshot } from "../src/diagnostics/unity-processes.js";
import { createUnityWindowDiagnosticsRuntime, inspectUnityWindowDiagnostics, type UnitySceneBackupKeepArgs } from "../src/diagnostics/unity-window-diagnostics.js";
import { DIAGNOSTICS_COMMANDS, runDiagnosticsCommand, type UnitySceneBackupBindingWitness } from "../src/mcp/diagnostics-commands.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { fail } from "../src/mcp/results.js";
import { InMemoryAuditSink } from "../src/observability/audit.js";
import { PolicyManager } from "../src/policy/policy-manager.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const command = "diagnostics.unity_scene_backup.keep_backups";
const root = "c:/test/scene-backup";
const now = new Date("2026-10-07T06:00:00.000Z");
const currentPid = 21901;
const currentStart = "2026-10-07T05:00:00.0000000Z";
const oldPid = 21000;
const oldStart = "2026-10-06T05:00:00.0000000Z";
const unissuedHash = `sha256:${"0".repeat(64)}`;
const args: UnitySceneBackupKeepArgs = { pid: currentPid, process_start_utc: currentStart, canonical_project_root: root };
const fixture = JSON.parse(fs.readFileSync(new URL("./fixtures/mcp-pr-219-scene-backup-detected-modal.json", import.meta.url), "utf8")) as { row: Record<string, unknown> };
const legacyFixture = JSON.parse(fs.readFileSync(new URL("./fixtures/mcp-pr-159-scene-backup-recovery-modal.json", import.meta.url), "utf8")) as { row: Record<string, unknown> };
const tempDirs: string[] = [];
const safetyChecks: Array<() => void> = [];

afterEach(() => {
  try {
    for (const check of safetyChecks) check();
  } finally {
    safetyChecks.length = 0;
    for (const directory of tempDirs.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
  }
});

describe("Scene Backup Keep Backups wrapper and dispatcher security", () => {
  it("advertises only the exact built-in destructive, unsafe, default-dry-run response", () => {
    const descriptor = DIAGNOSTICS_COMMANDS.find((entry) => entry.id === command)!;
    expect(descriptor).toMatchObject({
      risk: "destructive", idempotency: "unsafe", feature_flag: "sgg_mcp_diagnostics",
      source: "builtin", trust: "builtin", package_id: "com.sungeargames.unity-mcp",
      schema: { required: ["pid", "process_start_utc", "canonical_project_root"], additionalProperties: false, properties: { dry_run: { default: true }, confirm_keep_backups: { const: true } } },
    });
    expect(isKnownUnityPopupResponse(descriptor)).toBe(true);
    expect(isKnownUnityPopupResponse({ ...descriptor, id: `${command}.extra` })).toBe(false);
    expect(isKnownUnityPopupResponse({ ...descriptor, source: "manifest", trust: "trusted_package" })).toBe(false);
    expect(JSON.stringify(descriptor.schema)).not.toMatch(/delete|button_text|hwnd|"action"/i);
    expect(DIAGNOSTICS_COMMANDS.some((entry) => /scene_backup.*(?:delete|yes|no|generic)/i.test(entry.id))).toBe(false);
  });

  it.each(["allow", "ask"] as const)("defaults to a non-interacting plan under %s policy without requesting human approval", async (decision) => {
    const harness = makeHarness({ decision });
    for (const dryRunArgs of [args, { ...args, dry_run: true }]) {
      const result = await call(harness.context, dryRunArgs);
      expect(result, JSON.stringify(result)).toMatchObject({ status: "ok", project: "game", data: { status: "ready", dry_run: true, interaction_attempted: false, native_dispatch_count: 0, backups_kept_verified: false, popup_closed_verified: false, precondition_hash: expect.stringMatching(/^sha256:[a-f0-9]{64}$/) } });
    }
    expect(harness.nativeSend).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it.each(["game", root, "old-editor"])("binds a reopened current Editor before its new heartbeat using %s, not the old PID/start", async (selector) => {
    const harness = makeHarness();
    const result = await call(harness.context, args, selector);
    expect(result).toMatchObject({ status: "ok", project: "game", data: { status: "ready", target_pid: currentPid, process_start_utc: currentStart } });
    expect(harness.context.registry!.listProjectRecords()[0]).toMatchObject({ stale: true, unity_pid: oldPid, unity_process_start_utc: oldStart });
    expect(harness.processStatus).toHaveBeenCalledTimes(1);
    expect(harness.processStatus.mock.calls[0]![1]).toMatchObject({ unity_pid: oldPid, unity_process_start_utc: oldStart });
    expect(harness.keepWrapper).toHaveBeenCalledWith({ ...args, dry_run: true });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("does not treat even a recent old heartbeat as the owner when a fresh process scan proves a new generation", async () => {
    const harness = makeHarness({ records: [heartbeat({ last_heartbeat: "2026-10-07T05:59:59.000Z" })] });
    expect(await call(harness.context, args)).toMatchObject({ status: "ok", data: { status: "ready", target_pid: currentPid, process_start_utc: currentStart } });
  });

  it("binds the new generation when the operating system reused the historical PID", async () => {
    const harness = makeHarness({ records: [heartbeat({ unity_pid: currentPid })] });
    expect(await call(harness.context, args)).toMatchObject({ status: "ok", data: { status: "ready", target_pid: currentPid, process_start_utc: currentStart } });
    expect(harness.keepWrapper).toHaveBeenCalledWith({ ...args, dry_run: true });
  });

  it.each(["game", root])("routes %s through several historical heartbeat generations with one known root/project identity", async (selector) => {
    const harness = makeHarness({ records: [heartbeat(), heartbeat({ instance_id: "older-editor", unity_pid: 20000, unity_process_start_utc: "2026-10-05T05:00:00.0000000Z" })] });
    const result = await call(harness.context, args, selector);
    expect(result, JSON.stringify(result)).toMatchObject({ status: "ok", project: "game", data: { status: "ready", target_pid: currentPid } });
    expect(harness.processStatus).toHaveBeenCalledTimes(1);
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it.each([root, "c:/test/other"])("never collapses a stable project ID spanning different canonical roots into %s", async (requestedRoot) => {
    const harness = makeHarness({ records: [heartbeat(), heartbeat({ instance_id: "same-id-other-root", project_root: "C:/test/other", canonical_project_root: "c:/test/other", aliases: [] })], optIn: true });
    for (const input of [args, realArgs(unissuedHash)]) {
      expect(await call(harness.context, { ...input, canonical_project_root: requestedRoot }, "game")).toMatchObject({ status: "error", error: { code: "unsafe_binding_refused", details: { project_identity_mapping: "project_identity_ambiguous", approval_requested: false, command_bridge_request_issued: false } } });
    }
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.nativeSend).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it.each(["other", "c:/test/other", "Game"])("refuses no-heartbeat cross-project/root or non-exact stable selectors: %s", async (selector) => {
    const other = heartbeat({ instance_id: "other-old", project: "other", display_name: "Other", project_root: "C:/test/other", canonical_project_root: "c:/test/other", aliases: [] });
    const harness = makeHarness({ records: [heartbeat(), heartbeat({ instance_id: "older-editor" }), other, { ...other, instance_id: "other-older" }] });
    expect(await call(harness.context, realArgs(unissuedHash), selector)).toMatchObject({ status: "error", error: { code: "unsafe_binding_refused", details: { project_identity_mapping: "canonical_root_mismatch", approval_requested: false, command_bridge_request_issued: false } } });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it("refuses an unknown requested root through an otherwise valid no-heartbeat stable selector", async () => {
    const harness = makeHarness({ records: [heartbeat(), heartbeat({ instance_id: "older-editor" })] });
    expect(await call(harness.context, { ...realArgs(unissuedHash), canonical_project_root: "c:/test/unknown" })).toMatchObject({ status: "error", error: { code: "unsafe_binding_refused", details: { project_identity_mapping: "project_identity_unavailable", approval_requested: false } } });
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it.each([
    "diagnostics.unity_project_downgrade.continue",
    "diagnostics.unity_scoped_registry_import.close",
    "diagnostics.unity_safe_mode.enter",
  ])("does not extend stable-selector no-heartbeat fallback to another popup command: %s", async (otherCommand) => {
    const harness = makeHarness({ records: [heartbeat(), heartbeat({ instance_id: "older-editor" })], optIn: true });
    expect(await dispatchTool("sgg_unity_call", { project: "game", command: otherCommand, args_json: JSON.stringify(args) }, harness.context)).toMatchObject({ status: "error", error: { code: "unsafe_binding_refused", details: { project_identity_mapping: "canonical_root_mismatch", approval_requested: false } } });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it.each(["allow", "ask"] as const)("opt-in OFF still requires human approval under %s even with generic auto-allow-ask ON", async (decision) => {
    const harness = makeHarness({ decision });
    harness.context.approvalAutoAllowAsk = true;
    const hash = await plan(harness.context);
    const result = await call(harness.context, realArgs(hash));
    expect(result).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(harness.keepWrapper).toHaveBeenCalledTimes(1);
    expect(harness.nativeSend).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(1);
  });

  it("fails closed without approval infrastructure when owner opt-in is OFF", async () => {
    const harness = makeHarness({ decision: "allow" });
    const hash = await plan(harness.context);
    delete harness.context.approvalStore;
    delete harness.context.approvalBindingService;
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: "approval_unavailable" } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each(["allow", "ask"] as const)("opt-in ON permits one exact single-use response under %s without new human approval", async (decision) => {
    const harness = makeHarness({ decision, optIn: true });
    const hash = await plan(harness.context);
    const result = await call(harness.context, realArgs(hash));
    const replay = await call(harness.context, realArgs(hash));
    expect(result).toMatchObject({ status: "ok", data: { status: "message_submitted", native_dispatch_count: 1, message_submitted: true, backups_kept_verified: false, popup_closed_verified: false } });
    expect(replay).toMatchObject({ status: "ok", data: { status: "refused", native_dispatch_count: 0, interaction_attempted: false } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
    expect(harness.audit.events[1]).toMatchObject({ command, policy_action: decision, approval_status: "auto_allowed", auto_allow_known_unity_popups: true });
    expect(harness.audit.events[1]).not.toHaveProperty("auto_allow_ask");
    const output = JSON.stringify(result);
    const audit = JSON.stringify([...harness.audit.starts, ...harness.audit.events]);
    expect(output).not.toContain(root);
    expect(output).not.toContain("0x2195");
    expect(audit).not.toContain(root);
    expect(audit).not.toContain(hash);
  });

  it("owner opt-in ON does not require approval infrastructure for this built-in response", async () => {
    const harness = makeHarness({ optIn: true });
    const hash = await plan(harness.context);
    delete harness.context.approvalStore;
    delete harness.context.approvalBindingService;
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { status: "message_submitted", native_dispatch_count: 1 } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
  });

  it.each(["allow", "ask"] as const)("saved OFF to ON applies without replacing the context under %s and never replays a blocked call", async (decision) => {
    const harness = makeHarness({ decision });
    const save = attachLiveSetting(harness, false);
    const hash = await plan(harness.context);
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    save(true);
    expect(harness.nativeSend).not.toHaveBeenCalled();
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { native_dispatch_count: 1, status: "message_submitted" } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
    expect(harness.audit.events.at(-1)).toMatchObject({ approval_status: "auto_allowed", auto_allow_known_unity_popups: true });
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { status: "refused", native_dispatch_count: 0 } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
  });

  it.each([false, true])("saved ON to OFF revokes before submission despite generic auto-allow-ask %s", async (genericAsk) => {
    const harness = makeHarness({ optIn: true });
    const save = attachLiveSetting(harness, true);
    harness.context.approvalAutoAllowAsk = genericAsk;
    const hash = await plan(harness.context);
    save(false);
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
    expect(harness.audit.events.at(-1)).not.toHaveProperty("auto_allow_known_unity_popups");
  });

  it.each(["process_scan", "modal_scan"] as const)("rechecks owner consent after %s and blocks native issuance if revoked", async (stage) => {
    const harness = makeHarness();
    const save = attachLiveSetting(harness, true);
    const hash = await plan(harness.context);
    if (stage === "process_scan") harness.processStatus.mockImplementationOnce(() => { save(false); return processResult(); });
    else harness.inspect.mockImplementationOnce(() => { save(false); return sceneBackupStatus(); });
    const result = await call(harness.context, realArgs(hash));
    expect(result, JSON.stringify(result)).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { interaction_attempted: false, native_dispatch_count: 0, command_bridge_request_issued: false } } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
    expect(harness.audit.events.at(-1)).toMatchObject({ statistics_dispatched: false });
    approve(harness.context, result);
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { status: "message_submitted", native_dispatch_count: 1 } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
  });

  it.each(["invalid", "missing"] as const)("fails closed when consent becomes %s during the final modal rescan", async (failure) => {
    const harness = makeHarness();
    attachLiveSetting(harness, true);
    const hash = await plan(harness.context);
    harness.inspect.mockImplementationOnce(() => {
      const file = harness.context.knownUnityPopupSettings!.sourcePath;
      if (failure === "missing") fs.unlinkSync(file); else fs.writeFileSync(file, '{"approvals":{"auto_allow_known_unity_popups":true}}');
      return sceneBackupStatus();
    });
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { native_dispatch_count: 0 } } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("rechecks managed deny policy at final native issuance, not only saved consent", async () => {
    const harness = makeHarness();
    attachLiveSetting(harness, true);
    const file = path.join(harness.directory, "policy.json");
    fs.writeFileSync(file, JSON.stringify(policy("ask")));
    harness.context.policyManager = new PolicyManager(file);
    const hash = await plan(harness.context);
    harness.inspect.mockImplementationOnce(() => { fs.writeFileSync(file, JSON.stringify(policy("deny"))); return sceneBackupStatus(); });
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: "permission_denied", details: { native_dispatch_count: 0 } } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each(["rename", "remove", "conflicting_root"] as const)("refuses final registry identity drift (%s) without rerouting or native issuance", async (drift) => {
    const harness = makeHarness();
    attachLiveSetting(harness, true);
    const hash = await plan(harness.context);
    harness.inspect.mockImplementationOnce(() => {
      const file = path.join(harness.context.instancesDir!, "old-editor.json");
      if (drift === "remove") fs.unlinkSync(file);
      if (drift === "rename") fs.writeFileSync(file, JSON.stringify(heartbeat({ project: "renamed-project" })));
      if (drift === "conflicting_root") fs.writeFileSync(path.join(harness.context.instancesDir!, "conflict.json"), JSON.stringify(heartbeat({ instance_id: "conflict", canonical_project_root: "c:/OtherProject", project_root: "C:/OtherProject" })));
      return sceneBackupStatus();
    });
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: "unsafe_binding_refused", details: { native_dispatch_count: 0, command_bridge_request_issued: false } } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each(["feature", "degraded"] as const)("refuses final %s gate drift before issuing the popup response", async (gate) => {
    const harness = makeHarness();
    attachLiveSetting(harness, true);
    const file = path.join(harness.directory, "policy.json");
    fs.writeFileSync(file, JSON.stringify(policy("ask")));
    harness.context.policyManager = new PolicyManager(file);
    const hash = await plan(harness.context);
    harness.inspect.mockImplementationOnce(() => {
      if (gate === "feature") harness.context.features!.sgg_mcp_diagnostics = false; else fs.writeFileSync(file, "{");
      return sceneBackupStatus();
    });
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "error", error: { code: gate === "feature" ? "feature_disabled" : "policy_degraded", details: { native_dispatch_count: 0, command_bridge_request_issued: false } } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each(["modal_scan", "authorization"] as const)("does not issue Keep Backups when its original plan expires inside final %s", async (stage) => {
    const harness = makeHarness();
    const hash = await plan(harness.context);
    harness.advance(59_000);
    const guard = vi.fn(() => { if (stage === "authorization") harness.advance(1_000); });
    if (stage === "modal_scan") harness.inspect.mockImplementationOnce(() => { harness.advance(1_000); return sceneBackupStatus(); });
    expect(harness.context.unityWindowDiagnostics!.keepSceneBackups!(realArgs(hash), guard)).toMatchObject({ status: "refused", native_dispatch_count: 0, interaction_attempted: false });
    expect(guard).toHaveBeenCalledTimes(stage === "authorization" ? 1 : 0);
    expect(harness.nativeSend).not.toHaveBeenCalled();
    harness.advance(-1_000);
    expect(harness.context.unityWindowDiagnostics!.keepSceneBackups!(realArgs(hash))).toMatchObject({ status: "refused", native_dispatch_count: 0 });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("does not restore a Keep Backups plan if it expires inside a typed permission refusal", async () => {
    const harness = makeHarness();
    const hash = await plan(harness.context);
    harness.advance(59_000);
    const denied = new KnownPopupPermissionChanged(fail({ error: { code: "confirmation_required", message: "Isolated final revocation" } }));
    expect(() => harness.context.unityWindowDiagnostics!.keepSceneBackups!(realArgs(hash), () => { harness.advance(1_000); throw denied; })).toThrow(denied);
    harness.advance(-1_000);
    expect(harness.context.unityWindowDiagnostics!.keepSceneBackups!(realArgs(hash))).toMatchObject({ status: "refused", native_dispatch_count: 0 });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each([false, true])("modal discovery reflects owner opt-in %s but preserves plan, unsupported-button and no-replay guidance", async (optIn) => {
    const harness = makeHarness({ optIn });
    const result = await dispatchTool("sgg_unity_call", { project: root, command: "diagnostics.unity_modal.status", args_json: "{}" }, harness.context);
    expect(result).toMatchObject({ status: "ok", data: { blocking_modal_code: "blocked_scene_backup_recovery", popup_response_approval: { enabled: optIn, default_enabled: false, supported_command_ids: expect.arrayContaining([command]), automatic_replay_allowed: false }, next_valid_actions: expect.arrayContaining(["dry_run_diagnostics.unity_scene_backup.keep_backups", "do_not_select_delete_or_legacy_yes_no", "do_not_repeat_blocked_test_compile_or_package_operation", "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution"]) } });
    expect(result).toMatchObject({ data: { scene_backup_recovery_modal_candidates_count: 1, scene_backup_recovery_modal_candidates: [{ window_title: "Scene Backup Detected", scene_backup_recovery_variant: "scene_backup_detected", child_texts: ["Keep Backups", "Delete"], scene_backup_detected: { evidence_scope: "complete_body", button_count: 2, keep_backups_button_count: 1, delete_button_count: 1 } }] } });
    const data = (result as { data: { recovery_hint: string; next_valid_actions: string[] } }).data;
    if (optIn) {
      expect(data.recovery_hint).toContain("without another human question");
      expect(data.next_valid_actions).toContain("use_owner_preapproved_dedicated_popup_response");
      expect(data.next_valid_actions).not.toContain("ask_gate_exact_keep_backups_only");
      expect(data.next_valid_actions).not.toContain("operator_review_scene_backup_recovery_dialog");
      expect(data.recovery_hint).not.toMatch(/after human approval|operator.*(?:choose|review)|human.*(?:choose|review)/i);
    } else {
      expect(data.recovery_hint).toContain(command);
      expect(data.next_valid_actions).toContain("ask_gate_exact_keep_backups_only");
    }
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each([false, true])("legacy Recovering Scene Backups remains diagnostic-only/manual with owner opt-in %s", async (optIn) => {
    const harness = makeHarness({ optIn, modalRows: [legacyFixture.row] });
    const result = await dispatchTool("sgg_unity_call", { project: root, command: "diagnostics.unity_modal.status", args_json: "{}" }, harness.context);
    expect(result).toMatchObject({ status: "ok", data: { blocking_modal_code: "blocked_scene_backup_recovery", scene_backup_recovery_modal_candidates_count: 1, scene_backup_recovery_modal_candidates: [{ window_title: "Recovering Scene Backups", scene_backup_recovery_variant: "recovering_scene_backups", child_texts: ["Yes", "No"], scene_backup_recovery: { diagnostic_only: true, interaction_supported: false, button_count: 2, yes_button_count: 1, no_button_count: 1 } }], next_valid_actions: expect.arrayContaining(["operator_review_scene_backup_recovery_dialog", "do_not_click_yes_or_no_from_gateway", "do_not_repeat_blocked_test_compile_or_package_operation"]) } });
    const data = (result as { data: { recovery_hint: string; next_valid_actions: string[] } }).data;
    expect(data.recovery_hint).toContain("diagnostic-only");
    expect(data.recovery_hint).toContain("must not click Yes or No");
    expect(data.recovery_hint).not.toContain("without another human question");
    expect(data.next_valid_actions).not.toContain("use_owner_preapproved_dedicated_popup_response");
    expect(data.next_valid_actions).not.toContain(`dry_run_${command}`);
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("requires exact human approval, rejects changed args, consumes approval once, and cannot reuse an approved plan", async () => {
    const harness = makeHarness({ decision: "allow" });
    const hash = await plan(harness.context);
    const blocked = await call(harness.context, realArgs(hash));
    approve(harness.context, blocked);
    const changed = await call(harness.context, realArgs(unissuedHash));
    expect(changed).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
    const approved = await call(harness.context, realArgs(hash));
    expect(approved).toMatchObject({ status: "ok", data: { status: "message_submitted", native_dispatch_count: 1 } });
    const replay = await call(harness.context, realArgs(hash));
    expect(replay).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    approve(harness.context, replay);
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { status: "refused", native_dispatch_count: 0 } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
  }, 30_000);

  it.each([
    { name: "renamed_ID_with_stable_selector", selector: "game", change: "rename" },
    { name: "renamed_ID_with_root_selector", selector: root, change: "rename" },
    { name: "root_mapping_removed", selector: root, change: "move" },
    { name: "root_mapping_became_ambiguous", selector: root, change: "conflict" },
  ])("revalidates $name after human permission and before local/native dispatch", async ({ selector, change }) => {
    const records = [heartbeat(), heartbeat({ instance_id: "older-editor" })];
    const harness = makeHarness({ records });
    const hash = await plan(harness.context, selector);
    const blocked = await call(harness.context, realArgs(hash), selector);
    approve(harness.context, blocked);
    const store = harness.context.approvalStore!;
    const consume = store.consume.bind(store);
    const permission = vi.spyOn(store, "consume").mockImplementation((...input) => {
      const result = consume(...input);
      expect(result.status).toBe("consumed");
      // Simulate an external registry change at the precise permission-to-local
      // boundary. Only isolated fixture records are changed, never live state.
      for (const [index, record] of records.entries()) {
        const changed = change === "rename" ? { ...record, project: "renamed" }
          : change === "move" ? { ...record, project_root: "C:/test/moved", canonical_project_root: "c:/test/moved", aliases: [] }
            : { ...record, project: index === 0 ? "game" : "conflicting" };
        fs.writeFileSync(path.join(harness.context.instancesDir!, `${record.instance_id}.json`), JSON.stringify(changed));
      }
      return result;
    });
    const result = await call(harness.context, realArgs(hash), selector);
    expect(result).toMatchObject({ status: "error", project: "game", error: { code: "unsafe_binding_refused", message: "Scene-backup project identity changed before local dispatch", details: { command_bridge_request_issued: false, command_replayed: false, delivery_state: "not_dispatched" } } });
    expect(permission).toHaveBeenCalledTimes(1);
    expect(harness.audit.events.at(-1)).toMatchObject({ approval_status: "consumed" });
    expect(harness.processStatus).toHaveBeenCalledTimes(1);
    expect(harness.keepWrapper).toHaveBeenCalledTimes(1);
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each(["default_deny", "project_deny", "feature_disabled", "policy_degraded"] as const)("owner opt-in preserves %s for both plans and real responses", async (variant) => {
    const harness = makeHarness({ optIn: true, decision: variant === "default_deny" ? "deny" : "allow" });
    if (variant === "project_deny") harness.context.policy!.projects.game = { allow: ["*"], ask: [], deny: [command] };
    if (variant === "feature_disabled") harness.context.features = { sgg_mcp_diagnostics: false };
    if (variant === "policy_degraded") {
      const policyFile = path.join(harness.directory, "policy.json");
      fs.writeFileSync(policyFile, JSON.stringify(harness.context.policy));
      harness.context.policyManager = new PolicyManager(policyFile, { now: () => now });
      fs.writeFileSync(policyFile, "not JSON");
    }
    const expected = variant === "default_deny" || variant === "project_deny" ? "permission_denied" : variant;
    for (const input of [args, realArgs(unissuedHash)]) expect(await call(harness.context, input)).toMatchObject({ status: "error", project: "game", error: { code: expected } });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.nativeSend).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it("uses the stable project policy for root-only no-heartbeat fallback and denies even an already approved call", async () => {
    const harness = makeHarness({ records: [heartbeat(), heartbeat({ instance_id: "older-editor" })] });
    const hash = await plan(harness.context, root);
    const blocked = await call(harness.context, realArgs(hash), root);
    approve(harness.context, blocked);
    harness.context.policy!.projects.game = { allow: ["*"], ask: [], deny: [command] };
    expect(await call(harness.context, realArgs(hash), root)).toMatchObject({ status: "error", project: "game", error: { code: "permission_denied", details: { policy_reason: "project_deny", policy_pattern: command } } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each([
    { name: "unknown_root", records: [] as HeartbeatRecord[], reason: "project_identity_unavailable" },
    { name: "ambiguous_project_ids", records: [heartbeat(), heartbeat({ instance_id: "different-project", project: "other" })], reason: "project_identity_ambiguous" },
  ])("refuses no-heartbeat $name before approval or native/process work", async ({ records, reason }) => {
    const harness = makeHarness({ records, optIn: true });
    expect(await call(harness.context, args, root)).toMatchObject({ status: "error", error: { code: "unsafe_binding_refused", details: { project_identity_mapping: reason, approval_requested: false, command_bridge_request_issued: false } } });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it("refuses conflicting project IDs for the root even when an exact old instance is selected", async () => {
    const harness = makeHarness({ records: [heartbeat(), heartbeat({ instance_id: "different-project", project: "other" })], optIn: true });
    const result = await call(harness.context, args, "old-editor");
    expect(result).toMatchObject({ status: "ok", data: { status: "refused", error: "scene_backup_known_project_identity_mismatch_or_ambiguous", native_dispatch_count: 0 } });
    expect(bindingWitness(result)).toMatchObject({ stage: "project_identity", selected_root_equal: true, process_scan: { authoritative: "unavailable", matching_editor_count: "unavailable" }, pid_equal: "unavailable", process_start_utc_equal: "unavailable" });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
  });

  it.each(["other", "c:/test/other"])("refuses response binding through another routable project: %s", async (selector) => {
    const other = heartbeat({ instance_id: "other-editor", project: "other", project_root: "C:/test/other", canonical_project_root: "c:/test/other", aliases: [], last_heartbeat: "2026-10-07T05:59:59.000Z" });
    const harness = makeHarness({ records: [heartbeat(), other], optIn: true });
    const result = await call(harness.context, args, selector);
    expect(result).toMatchObject({ status: "ok", data: { status: "refused", error: "scene_backup_selected_project_root_mismatch", native_dispatch_count: 0 } });
    expect(bindingWitness(result)).toMatchObject({ stage: "project_identity", selected_root_equal: false, process_scan: { authoritative: "unavailable", matching_editor_count: "unavailable" }, pid_equal: "unavailable", process_start_utc_equal: "unavailable" });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
  });

  it.each([
    { name: "missing_process", processes: [], count: 0, pidEqual: "unavailable", startEqual: "unavailable" },
    { name: "wrong_root", processes: [currentProcess({ canonical_project_root: "c:/test/other" })], count: 0, pidEqual: "unavailable", startEqual: "unavailable" },
    { name: "missing_root", processes: [currentProcess({ canonical_project_root: null })], count: 0, pidEqual: "unavailable", startEqual: "unavailable" },
    { name: "wrong_pid", processes: [currentProcess({ pid: currentPid + 1 })], count: 1, pidEqual: false, startEqual: "unavailable" },
    { name: "wrong_start", processes: [currentProcess({ process_start_utc: oldStart })], count: 1, pidEqual: true, startEqual: false },
    { name: "old_heartbeat_owner", processes: [currentProcess({ pid: oldPid, process_start_utc: oldStart })], count: 1, pidEqual: false, startEqual: "unavailable" },
    { name: "missing_start", processes: [currentProcess({ process_start_utc: null })], count: 1, pidEqual: true, startEqual: "unavailable" },
    { name: "ambiguous_editors_for_root", processes: [currentProcess(), currentProcess({ pid: currentPid + 1 })], count: 2, pidEqual: "unavailable", startEqual: "unavailable" },
    { name: "asset_import_worker", processes: [currentProcess({ role: "unity_asset_import_worker" })], count: 0, pidEqual: "unavailable", startEqual: "unavailable" },
  ])("refuses $name from the fresh scan before either planning or real native dispatch", async ({ processes, count, pidEqual, startEqual }) => {
    const harness = makeHarness({ optIn: true });
    harness.processStatus.mockReturnValue(processResult(processes));
    for (const input of [args, realArgs(unissuedHash)]) {
      const result = await call(harness.context, input);
      expect(result).toMatchObject({ status: "ok", data: { status: "refused", error: "scene_backup_current_process_identity_mismatch_or_ambiguous", interaction_attempted: false, native_dispatch_count: 0 } });
      expect(bindingWitness(result)).toMatchObject({ stage: "current_process", selected_root_equal: true, process_scan: { authoritative: true, matching_editor_count: count }, pid_equal: pidEqual, process_start_utc_equal: startEqual });
    }
    expect(harness.processStatus).toHaveBeenCalledTimes(2);
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each([
    { name: "unsupported", change: { supported: false } },
    { name: "truncated", change: { scan_truncated: true } },
    { name: "helper_error", change: { errors: ["powershell_scan_timeout"] } },
  ])("refuses a non-authoritative $name process scan even when the exact owner is present", async ({ change }) => {
    const harness = makeHarness({ optIn: true });
    harness.processStatus.mockReturnValue({ ...processResult(), ...change });
    for (const input of [args, realArgs(unissuedHash)]) {
      const result = await call(harness.context, input);
      expect(result).toMatchObject({ status: "ok", data: { status: "refused", error: "authoritative_scene_backup_process_scan_required", native_dispatch_count: 0 } });
      expect(bindingWitness(result)).toMatchObject({ stage: "process_scan", selected_root_equal: true, process_scan: { authoritative: false, matching_editor_count: "unavailable" }, pid_equal: "unavailable", process_start_utc_equal: "unavailable" });
    }
    expect(harness.processStatus).toHaveBeenCalledTimes(2);
    expect(harness.keepWrapper).not.toHaveBeenCalled();
  });

  it.each([
    { name: "noncanonical_root", change: { canonical_project_root: "C:\\test\\scene-backup" } },
    { name: "wrong_requested_root", change: { canonical_project_root: "c:/test/other" } },
    { name: "wrong_requested_pid", change: { pid: oldPid } },
    { name: "wrong_requested_start", change: { process_start_utc: oldStart } },
  ])("refuses $name arguments rather than binding the historical heartbeat owner", async ({ change }) => {
    const harness = makeHarness({ optIn: true });
    expect(await call(harness.context, { ...realArgs(unissuedHash), ...change })).toMatchObject({ status: "ok", data: { status: "refused", native_dispatch_count: 0 } });
    expect(harness.keepWrapper).not.toHaveBeenCalled();
  });

  it("reports redacted domain-separated root witnesses without accepting host-shaped input", async () => {
    const harness = makeHarness({ optIn: true });
    const hostRoot = "C:\\test\\scene-backup";
    const result = await call(harness.context, { ...args, canonical_project_root: hostRoot });
    const witness = bindingWitness(result);
    const fingerprint = (value: string) => `sha256:${createHash("sha256").update("sgg-unity-mcp:pr219:scene-backup-root:v1\0").update(value).digest("hex")}`;
    expect(witness).toEqual({
      schema_version: 1, stage: "input_validation",
      input_validation: { canonical_root_exact: false, pid_positive: true, process_start_utc_parseable: true },
      root_fingerprints: { input: fingerprint(hostRoot), normalized: fingerprint(root), selected: fingerprint(root) },
      selected_root_equal: "unavailable",
      process_scan: { authoritative: "unavailable", matching_editor_count: "unavailable" },
      pid_equal: "unavailable", process_start_utc_equal: "unavailable",
    });
    expect(witness.root_fingerprints.input).not.toBe(witness.root_fingerprints.normalized);
    expect(witness.root_fingerprints.normalized).not.toBe(`sha256:${createHash("sha256").update(root).digest("hex")}`);
    for (const sensitive of [root, hostRoot, "fixture-local-token"]) expect(JSON.stringify(result)).not.toContain(JSON.stringify(sensitive).slice(1, -1));
    expect(JSON.stringify(witness).length).toBeLessThan(1024);
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each([
    { name: "missing_root", change: { canonical_project_root: null }, rootExact: "unavailable", pidPositive: true, startParseable: true },
    { name: "empty_root", change: { canonical_project_root: "" }, rootExact: "unavailable", pidPositive: true, startParseable: true },
    { name: "invalid_pid", change: { pid: 0 }, rootExact: true, pidPositive: false, startParseable: true },
    { name: "invalid_start", change: { process_start_utc: "not-a-process-start-timestamp" }, rootExact: true, pidPositive: true, startParseable: false },
  ])("keeps unchecked ownership comparisons unavailable for handler input $name", ({ change, rootExact, pidPositive, startParseable }) => {
    const harness = makeHarness({ optIn: true });
    const data = runDiagnosticsCommand(command, { ...args, ...change }, harness.context, heartbeat(), { project_resolved: true, original_project_selector: "game", resolved_project_id: "game" });
    expect(data).toMatchObject({ status: "refused", error: "invalid_scene_backup_project_or_process_binding", binding_witness: {
      stage: "input_validation",
      input_validation: { canonical_root_exact: rootExact, pid_positive: pidPositive, process_start_utc_parseable: startParseable },
      selected_root_equal: "unavailable", process_scan: { authoritative: "unavailable", matching_editor_count: "unavailable" },
      pid_equal: "unavailable", process_start_utc_equal: "unavailable",
    } });
    if (rootExact === "unavailable") expect(data).toMatchObject({ binding_witness: { root_fingerprints: { input: "unavailable", normalized: "unavailable" } } });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("keeps exact process-start comparison despite a last-tick difference hidden by Date parsing", async () => {
    const harness = makeHarness({ optIn: true });
    const differentTick = "2026-10-07T05:00:00.0000001Z";
    expect(Date.parse(differentTick)).toBe(Date.parse(currentStart));
    const result = await call(harness.context, { ...args, process_start_utc: differentTick });
    expect(bindingWitness(result)).toMatchObject({ stage: "current_process", process_scan: { authoritative: true, matching_editor_count: 1 }, pid_equal: true, process_start_utc_equal: false });
    expect(harness.processStatus).toHaveBeenCalledTimes(1);
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("keeps accepted canonical process/root binding separate from incomplete native admission", async () => {
    const harness = makeHarness({ optIn: true, modalRows: [{ ...fixture.row, inspection_complete: false }] });
    const result = await call(harness.context, args);
    expect(result).toMatchObject({ status: "ok", data: { status: "refused", error: "exact_scene_backup_detected_modal_not_found_ambiguous_or_non_authoritative", native_dispatch_count: 0 } });
    const witness = bindingWitness(result);
    expect(witness).toMatchObject({ stage: "runtime_admission", input_validation: { canonical_root_exact: true }, selected_root_equal: true, process_scan: { authoritative: true, matching_editor_count: 1 }, pid_equal: true, process_start_utc_equal: true });
    expect(witness.root_fingerprints.input).toBe(witness.root_fingerprints.normalized);
    expect(witness.root_fingerprints.selected).toBe(witness.root_fingerprints.normalized);
    expect(harness.processStatus).toHaveBeenCalledTimes(1);
    expect(harness.inspect).toHaveBeenCalledTimes(1);
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it.each([
    { name: "both_missing", input: { ...args, dry_run: false } },
    { name: "confirm_missing", input: { ...args, dry_run: false, precondition_hash: unissuedHash } },
    { name: "plan_missing", input: { ...args, dry_run: false, confirm_keep_backups: true } },
    { name: "false_confirm", input: { ...realArgs(unissuedHash), confirm_keep_backups: false } },
  ])("owner opt-in does not bypass required real-response confirmation/precondition: $name", async ({ input }) => {
    const harness = makeHarness({ optIn: true });
    const result = await call(harness.context, input);
    expect(result).toMatchObject({ status: "ok", data: { status: "refused", error: "scene_backup_keep_requires_exact_plan_and_confirmation", native_dispatch_count: 0 } });
    expect(bindingWitness(result)).toMatchObject({ stage: "runtime_admission", process_scan: { authoritative: true, matching_editor_count: 1 }, pid_equal: true, process_start_utc_equal: true });
    expect(harness.processStatus).toHaveBeenCalledTimes(1);
    expect(harness.keepWrapper).not.toHaveBeenCalled();
  });

  it.each([
    { name: "invalid_hash", input: { ...realArgs(unissuedHash), precondition_hash: "not-a-plan" } },
    { name: "delete_action", input: { ...realArgs(unissuedHash), action: "delete" } },
  ])("rejects schema-invalid $name without requesting approval or executing native code", async ({ input }) => {
    const harness = makeHarness({ optIn: true });
    expect(await call(harness.context, input)).toMatchObject({ status: "error", error: { code: "schema_validation_failed" } });
    expect(harness.processStatus).not.toHaveBeenCalled();
    expect(harness.keepWrapper).not.toHaveBeenCalled();
    expect(harness.context.approvalStore!.listPending({ now })).toHaveLength(0);
  });

  it("does not accept a syntactically valid but unissued native plan", async () => {
    const harness = makeHarness({ optIn: true });
    expect(await call(harness.context, realArgs(unissuedHash))).toMatchObject({ status: "ok", data: { status: "refused", error: "scene_backup_keep_plan_not_issued_expired_or_consumed", native_dispatch_count: 0 } });
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("freshly scans again at apply time and refuses process-generation drift after planning", async () => {
    const harness = makeHarness({ optIn: true });
    const hash = await plan(harness.context);
    harness.processStatus.mockReturnValue(processResult([currentProcess({ process_start_utc: "2026-10-07T05:30:00.0000000Z" })]));
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { status: "refused", error: "scene_backup_current_process_identity_mismatch_or_ambiguous", native_dispatch_count: 0 } });
    expect(harness.processStatus).toHaveBeenCalledTimes(2);
    expect(harness.keepWrapper).toHaveBeenCalledTimes(1);
    expect(harness.nativeSend).not.toHaveBeenCalled();
  });

  it("refuses when the dedicated native runtime is unavailable", async () => {
    const harness = makeHarness({ optIn: true });
    delete harness.context.unityWindowDiagnostics!.keepSceneBackups;
    const result = await call(harness.context, args);
    expect(result).toMatchObject({ status: "ok", data: { status: "refused", error: "scene_backup_keep_runtime_unavailable", native_dispatch_count: 0 } });
    expect(bindingWitness(result)).toMatchObject({ stage: "runtime_admission", process_scan: { authoritative: true, matching_editor_count: 1 }, pid_equal: true, process_start_utc_equal: true });
    expect(harness.processStatus).toHaveBeenCalledTimes(1);
  });

  it("does not automatically retry uncertain native delivery or replay any blocked bridge operation", async () => {
    const harness = makeHarness({ optIn: true });
    const hash = await plan(harness.context);
    harness.nativeSend.mockImplementation(() => { throw new Error("simulated native helper timeout after possible send"); });
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { status: "error", native_dispatch_count: 1, interaction_attempted: "unknown", delivery_state: "unknown", popup_closed_verified: false, backups_kept_verified: false } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
    expect(await call(harness.context, realArgs(hash))).toMatchObject({ status: "ok", data: { status: "refused", native_dispatch_count: 0 } });
    expect(await call(harness.context, args)).toMatchObject({ status: "ok", data: { status: "refused", native_dispatch_count: 0 } });
    expect(harness.nativeSend).toHaveBeenCalledTimes(1);
  });
});

function makeHarness(options: { records?: HeartbeatRecord[]; decision?: "allow" | "ask" | "deny"; optIn?: boolean; modalRows?: Record<string, unknown>[] } = {}) {
  const tempRoot = path.join(os.tmpdir(), "opencode");
  fs.mkdirSync(tempRoot, { recursive: true });
  const directory = fs.mkdtempSync(path.join(tempRoot, "sgg-scene-backup-dispatcher-"));
  tempDirs.push(directory);
  const instancesDir = path.join(directory, "instances");
  fs.mkdirSync(instancesDir, { mode: 0o700 });
  for (const record of options.records ?? [heartbeat()]) fs.writeFileSync(path.join(instancesDir, `${record.instance_id}.json`), JSON.stringify(record));
  const bridge = new FakeUnityBridgeClient([compileStatusCommand]);
  const bridgeDescribe = vi.spyOn(bridge, "describe").mockRejectedValue(new Error("scene-backup command must not use Bridge discovery"));
  const bridgeInvoke = vi.spyOn(bridge, "invoke").mockRejectedValue(new Error("scene-backup command must not invoke or replay Bridge work"));
  const bridgeLogs = vi.spyOn(bridge, "logs").mockRejectedValue(new Error("scene-backup command must not use Bridge logs"));
  const processStatus = vi.fn<NonNullable<DispatcherContext["unityProcessDiagnostics"]>["status"]>(() => processResult());
  const terminate = vi.fn<NonNullable<DispatcherContext["unityProcessDiagnostics"]>["terminate"]>(() => { throw new Error("scene-backup command must never terminate Unity"); });
  const nativeSend = vi.fn<NonNullable<NonNullable<Parameters<typeof createUnityWindowDiagnosticsRuntime>[0]>["keepSceneBackups"]>>(() => ({ status: "message_submitted", interaction_attempted: true, delivery_state: "message_submitted" }));
  const inspect = vi.fn(() => sceneBackupStatus(options.modalRows));
  let currentNow = now;
  const clock = () => currentNow;
  const advance = (ms: number) => { currentNow = new Date(currentNow.getTime() + ms); };
  const runtime = createUnityWindowDiagnosticsRuntime({ now: clock, inspect, keepSceneBackups: nativeSend });
  const keepWrapper = vi.spyOn(runtime, "keepSceneBackups");
  const audit = new InMemoryAuditSink();
  const context: DispatcherContext = {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: clock }),
    bridge, audit, now: clock, instancesDir,
    policy: policy(options.decision ?? "ask"), features: { sgg_mcp_diagnostics: true },
    approvalStore: new ApprovalStore(path.join(directory, "approvals")),
    approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }),
    approvalAutoAllowKnownUnityPopups: options.optIn ?? false,
    unityProcessDiagnostics: { status: processStatus, terminate }, unityWindowDiagnostics: runtime,
  };
  const snapshot = context.registry!.captureSnapshot();
  expect(snapshot.records, JSON.stringify(snapshot.getStats())).toHaveLength((options.records ?? [heartbeat()]).length);
  const harness = { context, directory, audit, processStatus, inspect, terminate, nativeSend, keepWrapper, bridgeDescribe, bridgeInvoke, bridgeLogs, advance };
  safetyChecks.push(() => {
    // Approval failures and uncertain helper delivery must stay Gateway-local.
    expect(bridgeDescribe).not.toHaveBeenCalled();
    expect(bridgeInvoke).not.toHaveBeenCalled();
    expect(bridgeLogs).not.toHaveBeenCalled();
    expect(terminate).not.toHaveBeenCalled();
  });
  return harness;
}

function heartbeat(overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return {
    schema_version: 1, instance_id: "old-editor", project: "game", display_name: "Game",
    project_root: "C:/test/scene-backup", canonical_project_root: root,
    aliases: [{ environment: "windows", host_path: "C:\\test\\scene-backup", canonical_path: root }],
    unity_version: "6000.6.2f1", unity_pid: oldPid, unity_process_start_utc: oldStart,
    bridge_host: "127.0.0.1", bridge_port: 18142, token: "fixture-local-token", status: "ready",
    commands_hash: "scene-backup-fixture", last_heartbeat: "2026-10-06T05:00:00.000Z", ...overrides,
  };
}

function attachLiveSetting(harness: ReturnType<typeof makeHarness>, enabled: boolean) {
  const file = path.join(harness.directory, "gateway.json");
  const config = JSON.parse(fs.readFileSync(new URL("../config/gateway.example.json", import.meta.url), "utf8"));
  const save = (value: boolean) => { config.approvals.auto_allow_known_unity_popups = value; fs.writeFileSync(file, JSON.stringify(config)); };
  save(enabled);
  harness.context.knownUnityPopupSettings = new KnownUnityPopupSettingsManager(file);
  return save;
}

function policy(decision: "allow" | "ask" | "deny"): PolicyConfig {
  return { schema_version: 1, defaults: { read: "allow", write: "ask", execute: "deny", destructive: decision, network: "deny" }, trusted_packages: [], untrusted_package_default: "ask", projects: { game: { allow: [], ask: [], deny: [] } } };
}

function currentProcess(overrides: Partial<UnityProcessSnapshot> = {}): UnityProcessSnapshot {
  return { pid: currentPid, parent_pid: null, process_name: "Unity.exe", role: "unity_editor", process_start_utc: currentStart, executable_path: null, command_line_redacted: "Unity.exe -projectPath <redacted>", parsed_project_path: "C:/test/scene-backup", canonical_project_root: root, heartbeat_match_count: 0, heartbeat_matches: [], current_project_match: false, eligible_for_terminate: false, terminate_refusal_reason: "current_project_mismatch", ...overrides };
}

function processResult(processes = [currentProcess()]): UnityProcessListResult {
  return { schema_version: 1, supported: true, process_count: processes.length, scan_truncated: false, processes, errors: [], next_valid_actions: [] };
}

function sceneBackupStatus(rows = [fixture.row]) {
  return inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe")
    ? "INFO: No tasks are running which match the specified criteria."
    : JSON.stringify({ rows, user32_scan_succeeded: true, user32_scan_complete: true, user32_candidate_count: rows.length, uia_scan_succeeded: false, uia_scan_status: "not_required" }) });
}

function call(context: DispatcherContext, input: object, project = "game") {
  return dispatchTool("sgg_unity_call", { project, command, args_json: JSON.stringify(input), request_id: "scene-backup-fixture" }, context);
}

function bindingWitness(result: unknown): UnitySceneBackupBindingWitness {
  expect(result).toMatchObject({ status: "ok", data: { status: "refused", binding_witness: { schema_version: 1 } } });
  return (result as { data: { binding_witness: UnitySceneBackupBindingWitness } }).data.binding_witness;
}

async function plan(context: DispatcherContext, project = "game"): Promise<string> {
  const result = await call(context, args, project);
  expect(result).toMatchObject({ status: "ok", data: { status: "ready", native_dispatch_count: 0 } });
  return (result as { data: { precondition_hash: string } }).data.precondition_hash;
}

function realArgs(hash: string) {
  return { ...args, dry_run: false, confirm_keep_backups: true, precondition_hash: hash };
}

function approve(context: DispatcherContext, result: Awaited<ReturnType<typeof call>>) {
  if (result.status !== "error" || result.error.code !== "confirmation_required") throw new Error("Expected exact pending human approval");
  const request = result.error.details?.approval_request as { pending_id: string };
  context.approvalStore!.approvePending({ pending_id: request.pending_id, ttl_ms: 60_000, now });
}
