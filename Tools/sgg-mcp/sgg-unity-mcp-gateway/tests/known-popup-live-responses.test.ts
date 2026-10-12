import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it, vi } from "vitest";

import { ApprovalBindingService } from "../src/approvals/approval-binding.js";
import { ApprovalStore, type ApprovalPendingGeneration } from "../src/approvals/approval-store.js";
import { KnownUnityPopupSettingsManager } from "../src/approvals/known-unity-popup-settings.js";
import { KnownPopupPermissionChanged } from "../src/approvals/known-unity-popups.js";
import { FakeUnityBridgeClient, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import {
  createUnityWindowDiagnosticsRuntime,
  type ExecFileSyncLike,
  type UnityExternalSceneReloadRespondArgs,
  type UnityWindowDiagnosticsRuntimeOptions,
  type UnityProjectDowngradeContinueArgs,
  type UnityProjectDowngradeEvidence,
  type UnitySafeModeEnterArgs,
  type UnityScopedRegistryImportCloseArgs,
} from "../src/diagnostics/unity-window-diagnostics.js";
import { InMemoryJobManager } from "../src/jobs/job-manager.js";
import { dispatchAcceptedPendingCall, dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { fail, type GatewayResult } from "../src/mcp/results.js";
import { InMemoryAuditSink } from "../src/observability/audit.js";
import { ProjectRegistry, type HeartbeatRecord } from "../src/registry/project-registry.js";

const nativeKinds = ["external_scene", "downgrade", "safe_mode", "scoped_registry"] as const;
type NativeKind = typeof nativeKinds[number];
type Kind = NativeKind | "save_prompt";
const kinds: readonly Kind[] = [...nativeKinds, "save_prompt"];
const root = "c:/u/game";
const start = "2026-08-24T05:10:19.007Z";
const initialNow = new Date("2026-10-10T08:00:00.000Z");
const scopedFixture = JSON.parse(fs.readFileSync(new URL("./fixtures/mcp-pr-151-scoped-registry-modal.json", import.meta.url), "utf8")) as { row: Record<string, unknown> };
const scannerFixture = JSON.parse(fs.readFileSync(new URL("./fixtures/mcp-pr-150-native-scanner.json", import.meta.url), "utf8")) as { tasklist: { english_no_match: { stdout_utf8: string } } };
const directories: string[] = [];

afterEach(() => {
  vi.restoreAllMocks();
  for (const directory of directories.splice(0)) fs.rmSync(directory, { recursive: true, force: true });
});

describe("live consent for the five other exact built-in popup responses (isolated executors)", () => {
  it.each(kinds)("saved OFF to ON admits one explicit %s response without replaying the blocked call", async (kind) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    expect(await call(h, args)).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(h.issued).not.toHaveBeenCalled();
    h.save(true);
    expect(h.issued).not.toHaveBeenCalled();
    expect(await call(h, args)).toMatchObject({ status: "ok", data: { interaction_attempted: true } });
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(h.audit.events.at(-1)).toMatchObject({ approval_status: "auto_allowed", auto_allow_known_unity_popups: true });
    expect(h.audit.events.at(-1)).not.toHaveProperty("auto_allow_ask");
    if (kind !== "save_prompt") {
      expect(await call(h, args)).toMatchObject({ status: "ok", data: { status: "refused", interaction_attempted: false } });
      expect(h.issued).toHaveBeenCalledTimes(1);
      expect(h.invoke).not.toHaveBeenCalled();
    }
    expect(h.terminate).not.toHaveBeenCalled();
  });

  it.each(kinds)("%s revocation at the real final scan/transport boundary allows only unchanged human continuation", async (kind) => {
    const h = makeHarness(kind);
    h.save(true);
    const args = await prepareArgs(h);
    const revoke = () => { h.save(false); delete h.on.scan; delete h.on.beforeBridge; };
    if (kind === "save_prompt") h.on.beforeBridge = revoke;
    else h.on.scan = revoke;
    const blocked = await call(h, args);
    expect(blocked, JSON.stringify(blocked)).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { interaction_attempted: false, native_dispatch_count: 0, command_bridge_request_issued: false } } });
    expect(h.issued).not.toHaveBeenCalled();
    expect(h.context.knownUnityPopupSettings!.snapshot()).toMatchObject({ saved_enabled: false });
    approve(h, blocked);
    expect(h.issued).not.toHaveBeenCalled();
    expect(await call(h, args)).toMatchObject({ status: "ok", data: { interaction_attempted: true } });
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(h.audit.events.at(-1)).toMatchObject({ approval_status: "consumed" });
    expect(h.audit.events.at(-1)).not.toHaveProperty("auto_allow_known_unity_popups");
    expect(h.terminate).not.toHaveBeenCalled();
  });

  it.each(nativeKinds)("%s real engine invokes the shared authorization guard after validation and preserves its unattempted evidence", async (kind) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    h.events.length = 0;
    const denied = new KnownPopupPermissionChanged(fail({ error: { code: "confirmation_required", message: "Isolated pre-send revocation" } }));
    const guard = vi.fn(() => {
      expect(h.events.at(-1)).toBe("scan");
      h.events.push("guard");
      throw denied;
    });
    let caught: unknown;
    try { applyNative(h, args, guard); } catch (error) { caught = error; }
    expect(caught).toBe(denied);
    expect(guard).toHaveBeenCalledTimes(1);
    expect(h.issued).not.toHaveBeenCalled();
    expect(h.events.at(-1)).toBe("guard");
    expect(applyNative(h, args)).toMatchObject({ interaction_attempted: true });
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(applyNative(h, args)).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(h.issued).toHaveBeenCalledTimes(1);
  });

  it.each(nativeKinds)("%s preserved evidence keeps its original expiry rather than gaining another minute", async (kind) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    h.advance(59_000);
    const denied = new KnownPopupPermissionChanged(fail({ error: { code: "confirmation_required", message: "Isolated pre-send revocation" } }));
    expect(() => applyNative(h, args, () => { throw denied; })).toThrow(denied);
    h.advance(1_001);
    const guard = vi.fn();
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(guard).not.toHaveBeenCalled();
    expect(h.issued).not.toHaveBeenCalled();
  });

  it.each(nativeKinds)("%s refuses evidence that expires inside the actual validation rescan before authorization or issuance", async (kind) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    h.advance(59_000);
    h.events.length = 0;
    h.on.scan = () => { h.advance(1_001); delete h.on.scan; };
    const guard = vi.fn();
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(h.clock().getTime()).toBe(initialNow.getTime() + 60_001);
    expect(h.events).toContain("scan");
    expect(guard).not.toHaveBeenCalled();
    expect(h.issued).not.toHaveBeenCalled();
    h.advance(-60_001);
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(guard).not.toHaveBeenCalled();
    expect(h.issued).not.toHaveBeenCalled();
  });

  it.each(nativeKinds)("%s refuses evidence that expires inside a successful authorization callback before issuance", async (kind) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    h.advance(59_000);
    const guard = vi.fn(() => { h.advance(1_001); });
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(guard).toHaveBeenCalledTimes(1);
    expect(h.clock().getTime()).toBe(initialNow.getTime() + 60_001);
    expect(h.issued).not.toHaveBeenCalled();
    h.advance(-60_001);
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(guard).toHaveBeenCalledTimes(1);
    expect(h.issued).not.toHaveBeenCalled();
  });

  it.each(nativeKinds.flatMap((kind) => [1_000, 1_001].map((elapsedMs) => ({ kind, elapsedMs }))))("$kind does not restore a typed authorization failure at/after expiry (+$elapsedMs ms), even if the clock rewinds", async ({ kind, elapsedMs }) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    h.advance(59_000);
    const denied = new KnownPopupPermissionChanged(fail({ error: { code: "confirmation_required", message: "Revocation crossing original expiry" } }));
    const guard = vi.fn(() => { h.advance(elapsedMs); throw denied; });
    expect(thrownBy(() => applyNative(h, args, guard))).toBe(denied);
    expect(guard).toHaveBeenCalledTimes(1);
    expect(h.clock().getTime()).toBe(initialNow.getTime() + 59_000 + elapsedMs);
    expect(h.issued).not.toHaveBeenCalled();
    // Rewinding distinguishes a consumed token from an incorrectly restored
    // token that merely fails the next call's current-time expiry check.
    h.advance(-59_000 - elapsedMs);
    expect(h.clock().getTime()).toBe(initialNow.getTime());
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(guard).toHaveBeenCalledTimes(1);
    expect(h.issued).not.toHaveBeenCalled();
  });

  it.each(["external_scene", "downgrade"] as const)("%s never restores evidence when a responder issues once and then throws the exact shared permission error", async (kind) => {
    const denied = new KnownPopupPermissionChanged(fail({ error: { code: "confirmation_required", message: "Responder error after isolated issuance, not authorization" } }));
    let h!: Harness;
    const respond = vi.fn(() => { h.events.push("injected_issue"); h.issued(); throw denied; });
    h = makeHarness(kind, {}, kind === "external_scene" ? { respondExternalSceneReload: respond } : { continueProjectDowngrade: respond });
    const args = await prepareArgs(h);
    const guard = vi.fn();
    expect(thrownBy(() => applyNative(h, args, guard))).toBe(denied);
    expect(guard).toHaveBeenCalledTimes(1);
    expect(respond).toHaveBeenCalledTimes(1);
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(h.events.at(-1)).toBe("injected_issue");
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(guard).toHaveBeenCalledTimes(1);
    expect(respond).toHaveBeenCalledTimes(1);
    expect(h.issued).toHaveBeenCalledTimes(1);
  });

  it.each(["external_scene", "downgrade"] as const)("%s rejects same-evidence reentrancy inside a guard before typed revocation, then permits only one unchanged continuation", async (kind) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    const denied = new KnownPopupPermissionChanged(fail({ error: { code: "confirmation_required", message: "Pre-send revocation after reentrant refusal" } }));
    const reentrantGuard = vi.fn();
    const guard = vi.fn(() => {
      expect(applyNative(h, args, reentrantGuard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
      expect(reentrantGuard).not.toHaveBeenCalled();
      expect(h.issued).not.toHaveBeenCalled();
      throw denied;
    });
    expect(thrownBy(() => applyNative(h, args, guard))).toBe(denied);
    expect(guard).toHaveBeenCalledTimes(1);
    expect(h.issued).not.toHaveBeenCalled();
    expect(applyNative(h, args)).toMatchObject({ interaction_attempted: true });
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(applyNative(h, args)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(h.issued).toHaveBeenCalledTimes(1);
  });

  it.each(["external_scene", "downgrade"] as const)("%s rejects same-evidence reentrancy within an issued responder and cannot restore its subsequent typed error", async (kind) => {
    const denied = new KnownPopupPermissionChanged(fail({ error: { code: "confirmation_required", message: "Typed responder error after issuance and reentrant refusal" } }));
    let h!: Harness;
    let args!: Record<string, unknown>;
    const reentrantGuard = vi.fn();
    const respond = vi.fn(() => {
      h.events.push("injected_issue");
      h.issued();
      expect(applyNative(h, args, reentrantGuard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
      expect(reentrantGuard).not.toHaveBeenCalled();
      expect(h.issued).toHaveBeenCalledTimes(1);
      throw denied;
    });
    h = makeHarness(kind, {}, kind === "external_scene" ? { respondExternalSceneReload: respond } : { continueProjectDowngrade: respond });
    args = await prepareArgs(h);
    const guard = vi.fn();
    expect(thrownBy(() => applyNative(h, args, guard))).toBe(denied);
    expect(guard).toHaveBeenCalledTimes(1);
    expect(respond).toHaveBeenCalledTimes(1);
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(applyNative(h, args, guard)).toMatchObject({ status: "refused", error: expiryErrors[kind], interaction_attempted: false });
    expect(guard).toHaveBeenCalledTimes(1);
    expect(respond).toHaveBeenCalledTimes(1);
    expect(reentrantGuard).not.toHaveBeenCalled();
    expect(h.issued).toHaveBeenCalledTimes(1);
  });

  it.each(nativeKinds)("%s does not preserve evidence for an error-name/result lookalike", async (kind) => {
    const h = makeHarness(kind);
    const args = await prepareArgs(h);
    const lookalike = Object.assign(new Error("Known popup permission changed before interaction"), {
      name: "KnownPopupPermissionChanged", result: fail({ error: { code: "confirmation_required", message: "Not the shared guard class" } }),
    });
    expect(() => applyNative(h, args, () => { throw lookalike; })).toThrow(lookalike);
    expect(applyNative(h, args)).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(h.issued).not.toHaveBeenCalled();
  });

  it("saved consent cannot bypass an expired/replaced accepted pending generation or consume its replacement", async () => {
    const h = makeHarness("save_prompt");
    const args = await prepareArgs(h);
    h.context.approvalDefaultTtlMs = 1;
    const original = await call(h, args);
    const originalGeneration = pendingGeneration(h, original);
    h.advance(2);
    h.context.approvalDefaultTtlMs = 60_000;
    const replacement = await call(h, args);
    const replacementGeneration = pendingGeneration(h, replacement);
    expect(replacementGeneration.created_at).not.toBe(originalGeneration.created_at);
    h.save(true);
    const before = h.context.approvalStore!.listPending({ now: h.clock() });
    expect(await dispatchAcceptedPendingCall(callArgs(h, args), h.context, { generation: originalGeneration, original_confirmation: original })).toEqual(original);
    expect(h.context.approvalStore!.listPending({ now: h.clock() })).toEqual(before);
    expect(h.issued).not.toHaveBeenCalled();
    expect(await dispatchAcceptedPendingCall(callArgs(h, args), h.context, { generation: replacementGeneration, original_confirmation: replacement })).toMatchObject({ status: "ok" });
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(h.audit.events.at(-1)).toMatchObject({ approval_status: "consumed" });
    expect(h.audit.events.at(-1)).not.toHaveProperty("auto_allow_known_unity_popups");
    expect(await dispatchAcceptedPendingCall(callArgs(h, args), h.context, { generation: replacementGeneration, original_confirmation: replacement })).toEqual(replacement);
    expect(h.issued).toHaveBeenCalledTimes(1);
  });

  it.each([
    { id: "editor.save_prompt.respond", source: "manifest" as const, trust: "trusted_package" as const },
    { id: "editor.save_prompt.respond.extra", source: "builtin" as const, trust: "builtin" as const },
    { id: "diagnostics.unity_unknown.respond", source: "builtin" as const, trust: "builtin" as const },
  ])("saved ON does not authorize trusted-package or prefix lookalike $id/$source", async (override) => {
    const h = makeHarness("save_prompt", override);
    h.save(true);
    expect(await call(h, { action: "cancel" })).toMatchObject({ status: "error", error: { code: "confirmation_required" } });
    expect(h.issued).not.toHaveBeenCalled();
    expect(h.invoke).not.toHaveBeenCalled();
    expect(h.context.approvalStore!.listPending({ now: h.clock() })).toHaveLength(1);
  });

  it("a job revoked before exact bridge issuance cannot consume an existing wait-mode approval", async () => {
    const h = makeHarness("save_prompt", { supports_job: true });
    const args = { action: "cancel" };
    approve(h, await call(h, args));
    const before = h.context.approvalStore!.list({ now: h.clock() });
    expect(before).toMatchObject([{ mode: "wait" }]);
    const jobs = new InMemoryJobManager(h.clock);
    const startJob = jobs.start.bind(jobs);
    let completion: Promise<GatewayResult> | undefined;
    vi.spyOn(jobs, "start").mockImplementation((input) => startJob({ ...input, task: () => { completion = input.task(); return completion; } }));
    h.context.jobs = jobs;
    h.save(true);
    h.on.beforeBridge = () => { h.save(false); delete h.on.beforeBridge; };
    const started = await call(h, args, "job");
    expect(started).toMatchObject({ status: "ok", data: { job_id: expect.any(String) } });
    expect(completion).toBeDefined();
    expect(await completion!).toMatchObject({ status: "error", error: { code: "confirmation_required", details: { command_bridge_request_issued: false } } });
    expect(h.issued).not.toHaveBeenCalled();
    expect(h.context.approvalStore!.list({ now: h.clock() })).toEqual(before);
    expect(h.context.approvalStore!.listPending({ now: h.clock() })).toMatchObject([{ mode: "job" }]);
    expect(await call(h, args)).toMatchObject({ status: "ok" });
    expect(h.issued).toHaveBeenCalledTimes(1);
    expect(h.context.approvalStore!.list({ now: h.clock() })).toEqual([]);
    expect(h.context.approvalStore!.listPending({ now: h.clock() })).toMatchObject([{ mode: "job" }]);
  });
});

const commandIds: Record<Kind, string> = {
  save_prompt: "editor.save_prompt.respond",
  external_scene: "diagnostics.unity_external_scene_reload.respond",
  downgrade: "diagnostics.unity_project_downgrade.continue",
  safe_mode: "diagnostics.unity_safe_mode.enter",
  scoped_registry: "diagnostics.unity_scoped_registry_import.close",
};
const nativeTypes: Record<NativeKind, string> = {
  external_scene: "SGGUnityExternalSceneReloadUser32",
  downgrade: "SGGUnityProjectDowngradeUser32",
  safe_mode: "SGGUnitySafeModeEnterUser32",
  scoped_registry: "SGGUnityScopedRegistryCloseUser32",
};
const expiryErrors: Record<NativeKind, string> = {
  external_scene: "external_scene_reload_evidence_not_issued_expired_or_consumed",
  downgrade: "project_downgrade_evidence_not_issued_expired_or_consumed",
  safe_mode: "safe_mode_enter_plan_not_issued_expired_or_consumed",
  scoped_registry: "scoped_registry_close_plan_not_issued_expired_or_consumed",
};

function makeHarness(kind: Kind, descriptorOverride: Partial<CommandDescriptor> = {}, responders: Pick<UnityWindowDiagnosticsRuntimeOptions, "respondExternalSceneReload" | "continueProjectDowngrade"> = {}) {
  const tempRoot = path.join(os.tmpdir(), "opencode");
  fs.mkdirSync(tempRoot, { recursive: true });
  const directory = fs.mkdtempSync(path.join(tempRoot, "known-popup-live-responses-"));
  directories.push(directory);
  const instancesDir = path.join(directory, "instances");
  fs.mkdirSync(instancesDir, { mode: 0o700 });
  let currentNow = initialNow;
  const clock = () => currentNow;
  const advance = (ms: number) => { currentNow = new Date(currentNow.getTime() + ms); };
  const record: HeartbeatRecord = {
    schema_version: 1, instance_id: "game-editor", project: "game", display_name: "Game",
    project_root: "C:/u/game", canonical_project_root: root, aliases: [],
    unity_version: "6000.0.20f1", unity_pid: 80872, unity_process_start_utc: start,
    bridge_host: "127.0.0.1", bridge_port: 18142, token: "isolated-fixture-token", status: "ready",
    commands_hash: "known-popup-fixture", last_heartbeat: initialNow.toISOString(),
  };
  fs.writeFileSync(path.join(instancesDir, "game-editor.json"), JSON.stringify(record));
  const descriptor: CommandDescriptor = {
    id: commandIds.save_prompt, title: "Isolated save prompt response", description: "No Editor or native execution",
    package: "com.sungeargames.unity-mcp", package_id: "com.sungeargames.unity-mcp", source: "builtin", trust: "builtin",
    risk: "destructive", idempotency: "unsafe", timeout_ms: 10_000,
    schema: { type: "object", properties: { action: { type: "string", enum: ["save", "dont_save", "cancel"] } }, required: ["action"], additionalProperties: false },
    ...descriptorOverride,
  };
  const command = kind === "save_prompt" ? descriptor.id : commandIds[kind];
  const bridge = new FakeUnityBridgeClient([descriptor]);
  const issued = vi.fn<() => void>();
  const events: string[] = [];
  const on: { scan?: () => void; beforeBridge?: () => void } = {};
  const invoke = vi.spyOn(bridge, "invoke").mockImplementation(async (_record, _request, transport) => {
    if (kind !== "save_prompt") throw new Error("Gateway-local responses must not issue Bridge work");
    on.beforeBridge?.();
    transport?.beforeRequestIssued?.();
    events.push("bridge_issue");
    issued();
    transport?.onRequestIssued?.();
    return { status: "ok", data: { status: "message_submitted", interaction_attempted: true } };
  });
  const row = kind === "save_prompt" ? undefined : modalRow(kind);
  const rows = row ? [row] : [];
  const exec: ExecFileSyncLike = (file, args, options) => {
    expect(options.timeout).toBeGreaterThan(0);
    expect(options.maxBuffer).toBeGreaterThan(0);
    if (file.endsWith("tasklist.exe")) return scannerFixture.tasklist.english_no_match.stdout_utf8;
    if (!file.endsWith("powershell.exe")) throw new Error("Unexpected injected executable");
    const script = args.at(-1) ?? "";
    if (script.includes("SGGUnityNativeModalUser32")) {
      events.push("scan");
      on.scan?.();
      return JSON.stringify({ rows, user32_scan_succeeded: true, user32_scan_complete: true, user32_candidate_count: rows.length, uia_scan_succeeded: false, uia_scan_status: "not_required" });
    }
    if (kind === "save_prompt" || !script.includes(nativeTypes[kind])) throw new Error("Unexpected injected native response");
    events.push("native_issue");
    issued();
    return JSON.stringify({ status: kind === "external_scene" || kind === "scoped_registry" ? "message_sent" : "message_submitted", button_text: kind === "external_scene" ? "Ignore" : kind === "downgrade" ? "Continue" : kind === "safe_mode" ? "Enter Safe Mode" : "Close", reload_count: 1, ignore_count: 1 });
  };
  // By default these are the real runtime engines and response helpers. Only
  // explicit responder-error cases replace a helper, never the core guard.
  // Every process boundary is injected; no Windows tool is run.
  const runtime = createUnityWindowDiagnosticsRuntime({ now: clock, execFileSync: exec, ...responders });
  const terminate = vi.fn(() => { throw new Error("Popup tests must never terminate a process"); });
  const audit = new InMemoryAuditSink();
  const policy: PolicyConfig = {
    schema_version: 1, defaults: { read: "allow", write: "ask", execute: "ask", destructive: "ask", network: "deny" },
    trusted_packages: ["com.sungeargames.unity-mcp"], untrusted_package_default: "ask",
    projects: { game: { allow: [], ask: [], deny: [] } },
  };
  const config = JSON.parse(fs.readFileSync(new URL("../config/gateway.example.json", import.meta.url), "utf8"));
  const configPath = path.join(directory, "gateway.json");
  const save = (enabled: boolean) => { config.approvals.auto_allow_known_unity_popups = enabled; fs.writeFileSync(configPath, JSON.stringify(config)); };
  save(false);
  const context: DispatcherContext = {
    registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000, now: clock }), instancesDir,
    bridge, audit, policy, now: clock, features: { sgg_mcp_diagnostics: true },
    approvalAutoAllowAsk: false, approvalAutoAllowKnownUnityPopups: false,
    knownUnityPopupSettings: new KnownUnityPopupSettingsManager(configPath),
    approvalStore: new ApprovalStore(path.join(directory, "approvals")),
    approvalBindingService: new ApprovalBindingService({ getKey: () => Buffer.alloc(32, 0x41) }),
    unityWindowDiagnostics: runtime,
    unityProcessDiagnostics: {
      status: () => ({ schema_version: 1, supported: true, scan_truncated: false, process_count: 1, errors: [], next_valid_actions: [], processes: [{
        pid: record.unity_pid, parent_pid: null, process_name: "Unity.exe", role: "unity_editor",
        process_start_utc: start, executable_path: null, command_line_redacted: "Unity.exe -projectPath <redacted>",
        parsed_project_path: "C:/u/game", canonical_project_root: root, heartbeat_match_count: 0, heartbeat_matches: [],
        current_project_match: false, eligible_for_terminate: false, terminate_refusal_reason: "current_project_mismatch",
      }] }),
      terminate,
    },
  };
  return { kind, command, context, runtime, save, clock, advance, issued, invoke, events, on, audit, terminate, record };
}

type Harness = ReturnType<typeof makeHarness>;

async function prepareArgs(h: Harness): Promise<Record<string, unknown>> {
  if (h.kind === "save_prompt") return { action: "cancel" };
  if (h.kind === "external_scene" || h.kind === "downgrade") {
    h.runtime.status();
    if (h.kind === "external_scene") {
      const evidence = h.runtime.externalSceneReloadEvidence!(h.record.unity_pid);
      expect(evidence, "Expected a real issued external-scene evidence token").toBeDefined();
      return { action: "ignore", pid: evidence!.pid, hwnd: evidence!.hwnd, evidence_id: evidence!.evidence_id };
    }
    const evidence = h.runtime.projectDowngradeEvidence!(h.record.unity_pid, root);
    expect(evidence, "Expected a real issued downgrade evidence token").toBeDefined();
    return downgradeArgs(evidence!);
  }
  const base = { pid: h.record.unity_pid, process_start_utc: start, canonical_project_root: root, dry_run: true };
  const result = await call(h, base);
  expect(result, JSON.stringify(result)).toMatchObject({ status: "ok", data: { status: "ready", dry_run: true, interaction_attempted: false, native_dispatch_count: 0, precondition_hash: expect.stringMatching(/^sha256:[a-f0-9]{64}$/) } });
  const preconditionHash = (result as { data: { precondition_hash: string } }).data.precondition_hash;
  return { ...base, dry_run: false, [h.kind === "safe_mode" ? "confirm_enter_safe_mode" : "confirm_close"]: true, precondition_hash: preconditionHash };
}

function callArgs(h: Harness, args: Record<string, unknown>, mode: "wait" | "job" = "wait") {
  return { project: "game", command: h.command, args_json: JSON.stringify(args), mode, request_id: "known-popup-live-fixture" };
}

function call(h: Harness, args: Record<string, unknown>, mode: "wait" | "job" = "wait") {
  return dispatchTool("sgg_unity_call", callArgs(h, args, mode), h.context);
}

function approve(h: Harness, result: GatewayResult): void {
  const generation = pendingGeneration(h, result);
  h.context.approvalStore!.approvePending({ pending_id: generation.id, ttl_ms: 60_000, now: h.clock() });
}

function pendingGeneration(h: Harness, result: GatewayResult): ApprovalPendingGeneration {
  if (result.status !== "error" || result.error.code !== "confirmation_required") throw new Error("Expected an exact pending human approval");
  const request = result.error.details?.approval_request as { pending_id: string };
  const pending = h.context.approvalStore!.listPending({ now: h.clock() }).find((entry) => entry.id === request.pending_id);
  if (!pending) throw new Error("Exact pending fixture generation missing");
  return { id: pending.id, created_at: pending.created_at, updated_at: pending.updated_at, expires_at: pending.expires_at };
}

function applyNative(h: Harness, args: Record<string, unknown>, guard?: () => void) {
  const engineArgs = { ...args, project_identity_sha256: `sha256:${createHash("sha256").update(root).digest("hex")}` };
  switch (h.kind) {
    case "external_scene": return h.runtime.respondExternalSceneReload!(args as UnityExternalSceneReloadRespondArgs, guard);
    case "downgrade": return h.runtime.continueProjectDowngrade!(args as UnityProjectDowngradeContinueArgs, guard);
    case "safe_mode": return h.runtime.enterSafeMode!(engineArgs as UnitySafeModeEnterArgs, guard);
    case "scoped_registry": return h.runtime.closeScopedRegistryImport!(engineArgs as UnityScopedRegistryImportCloseArgs, guard);
    default: throw new Error("A Bridge response is not a Gateway-local native engine");
  }
}

function thrownBy(callback: () => unknown): unknown {
  try { callback(); } catch (error) { return error; }
  return undefined;
}

function downgradeArgs(evidence: UnityProjectDowngradeEvidence): Record<string, unknown> {
  return {
    evidence_id: evidence.evidence_id, canonical_project_root: root, confirm_continue: true,
    pid: evidence.pid, process_name: evidence.process_name, process_start_utc: evidence.process_start_utc,
    hwnd: evidence.hwnd, window_thread_id: evidence.window_thread_id, class_name: evidence.class_name, window_title: evidence.window_title,
    source_version: evidence.source_version, target_version: evidence.target_version, body_sha256: evidence.body_sha256,
    button_count: evidence.buttons.count, continue_button_hwnd: evidence.buttons.continue_hwnd, quit_button_hwnd: evidence.buttons.quit_hwnd,
  };
}

function modalRow(kind: NativeKind): Record<string, unknown> {
  const base = structuredClone(scopedFixture.row);
  if (kind === "scoped_registry") return base;
  const title = kind === "external_scene" ? "The open scene(s) have been modified externally" : kind === "downgrade" ? "Project Downgrade Required" : "Enter Safe Mode?";
  const body = kind === "external_scene" ? ["The following open scene(s) have been changed on disk:\r\nAssets/Scenes/Main.unity\r\n\r\nDo you want to reload the scene(s)?"]
    : kind === "downgrade" ? ["The source project version 6000.0.30f1 is newer.", "The target editor version 6000.0.20f1 is older.", "Downgrading this project is not supported.", "Continuing with Continue will downgrade the project.", "All assets will reimport.", "Packages and settings may also be downgraded."] : [];
  const buttons = kind === "external_scene" ? ["Reload", "Ignore"] : kind === "downgrade" ? ["Continue", "Quit"] : ["Enter Safe Mode", "Ignore", "Quit"];
  const controls = [...body.map((text) => ({ text, class_name: kind === "external_scene" ? "Edit" : "Static" })), ...buttons.map((text) => ({ text, class_name: "Button" }))];
  return {
    ...base, window_title: title, child_texts: [...body, ...buttons],
    child_controls: controls.map((control, index) => ({ ...control, hwnd: `0x6000${index.toString(16)}`, parent_hwnd: base.hwnd, pid: base.pid, window_thread_id: base.window_thread_id, visible: true, enabled: true, control_id: 100 + index })),
  };
}
