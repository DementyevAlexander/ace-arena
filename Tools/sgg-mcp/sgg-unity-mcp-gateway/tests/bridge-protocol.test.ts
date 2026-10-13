import { describe, expect, it } from "vitest";

import {
  BRIDGE_COMMAND_SOURCES,
  BRIDGE_COMMAND_TRUST,
  BRIDGE_EDITOR_STATUSES,
  BRIDGE_IDEMPOTENCY,
  BRIDGE_PROTOCOL_BOUNDS,
  BRIDGE_RISKS,
  BridgeProtocolError,
  HEARTBEAT_STATUSES,
  assertBridgeResponseIdentity,
  commandCatalogRevision,
  commandDescriptorRevision,
  isBoundedJson,
  normalizeEffectiveCommandDescriptor,
  parseCommandCatalogV1,
  parseBridgeInvokeRequestV1,
  parseBridgeInvokeResponseV1,
  parseCommandDescriptorV1,
  parseHealthResponseV1,
  parseHeartbeatRecordV1,
  parseLogsResponseV1,
  parseSingleCommandResponseV1,
  serializeBridgeInvokeRequestV1,
  type CommandDescriptor,
} from "../src/bridge/bridge-protocol.js";

describe("Bridge v1 protocol", () => {
  it("publishes and enforces the v1 enums", () => {
    expect(BRIDGE_RISKS).toEqual(["read", "write", "execute", "destructive", "network"]);
    expect(BRIDGE_IDEMPOTENCY).toEqual(["safe", "idempotent", "unsafe"]);
    expect(BRIDGE_COMMAND_SOURCES).toContain("manifest");
    expect(BRIDGE_COMMAND_TRUST).toContain("untrusted_package");
    expect(BRIDGE_EDITOR_STATUSES).toEqual(["editing", "playing", "playing_paused"]);
    expect(HEARTBEAT_STATUSES).toContain("waiting_for_idle");
    for (const mutation of [
      { risk: "unknown" },
      { idempotency: "sometimes" },
      { source: "remote" },
      { trust: "unknown" },
      { requires_editor_status: ["ready"] },
    ]) {
      expect(() => parseCommandDescriptorV1({ ...descriptor(), ...mutation })).toThrow(protocolError("protocol_validation_error"));
    }
  });

  it("requires routable heartbeat state and binds current catalog identity", () => {
    expect(() => parseHeartbeatRecordV1({ ...heartbeatRecord(), bridge_port: 0 })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseHeartbeatRecordV1({ ...heartbeatRecord(), catalog_generation: 0 })).toThrow(protocolError("protocol_validation_error"));
    expect(parseHeartbeatRecordV1({
      ...heartbeatRecord(),
      status: "starting",
      bridge_port: 0,
      commands_hash: "bootstrap-pending",
      catalog_status: "pending",
      catalog_generation: 0,
    })).toMatchObject({ status: "starting", bridge_port: 0, catalog_status: "pending", catalog_generation: 0 });
    expect(() => parseHeartbeatRecordV1({ ...heartbeatRecord(), catalog_status: "ready" })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseHeartbeatRecordV1({ ...heartbeatRecord(), catalog_generation: 7 })).toThrow(protocolError("protocol_validation_error"));

    const record = parseHeartbeatRecordV1({ ...heartbeatRecord(), catalog_status: "ready", catalog_generation: 7 });
    const catalog = parseCommandCatalogV1({
      schema_version: 1,
      instance_id: "game-a",
      project: "game",
      commands_hash: "abc123",
      catalog_generation: 7,
      commands: [descriptor()],
    });
    expect(() => assertBridgeResponseIdentity(record, catalog)).not.toThrow();
    expect(() => assertBridgeResponseIdentity(record, { ...catalog, catalog_generation: 8 })).toThrow(expect.objectContaining({ diagnostic: expect.objectContaining({ reason: "catalog_generation_mismatch" }) }));
    expect(() => assertBridgeResponseIdentity(record, { ...catalog, commands_hash: "other" })).toThrow(expect.objectContaining({ diagnostic: expect.objectContaining({ reason: "commands_hash_mismatch" }) }));
    expect(() => parseCommandCatalogV1({ ...catalog, catalog_generation: undefined })).toThrow(protocolError("protocol_validation_error"));
    const health = parseHealthResponseV1({ ...healthResponse(), commands_hash: "abc123", catalog_status: "ready", catalog_generation: 7, catalog_ready: true, listener_ready: true });
    expect(() => assertBridgeResponseIdentity(record, health)).not.toThrow();
    expect(() => assertBridgeResponseIdentity(record, { ...health, catalog_generation: 8 })).toThrow(expect.objectContaining({ diagnostic: expect.objectContaining({ envelope: "health", reason: "catalog_generation_mismatch" }) }));
  });

  it("bounds timeout, identifiers, descriptions, tags, paths, and the catalog above the live catalog size", () => {
    expect(parseCommandDescriptorV1({ ...descriptor(), timeout_ms: BRIDGE_PROTOCOL_BOUNDS.timeoutMs }).timeout_ms).toBe(BRIDGE_PROTOCOL_BOUNDS.timeoutMs);
    expect(() => parseCommandDescriptorV1({ ...descriptor(), timeout_ms: 0 })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseCommandDescriptorV1({ ...descriptor(), timeout_ms: BRIDGE_PROTOCOL_BOUNDS.timeoutMs + 1 })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseCommandDescriptorV1({ ...descriptor(), id: "x".repeat(BRIDGE_PROTOCOL_BOUNDS.identifierChars + 1) })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseCommandDescriptorV1({ ...descriptor(), description: "x".repeat(BRIDGE_PROTOCOL_BOUNDS.descriptionChars + 1) })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseCommandDescriptorV1({ ...descriptor(), tags: Array.from({ length: BRIDGE_PROTOCOL_BOUNDS.tags + 1 }, (_, index) => `tag-${index}`) })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseCommandDescriptorV1({ ...descriptor(), manifest_path: "x".repeat(BRIDGE_PROTOCOL_BOUNDS.pathChars + 1) })).toThrow(protocolError("protocol_validation_error"));

    const commands = Array.from({ length: BRIDGE_PROTOCOL_BOUNDS.catalogCommands }, (_, index) => descriptor(`test.command.${index.toString().padStart(4, "0")}`));
    expect(parseCommandCatalogV1({ schema_version: 1, project: "game", commands_hash: "hash", commands }).commands).toHaveLength(1_024);
    expect(() => parseCommandCatalogV1({ schema_version: 1, project: "game", commands_hash: "hash", commands: Array.from({ length: BRIDGE_PROTOCOL_BOUNDS.catalogCommands + 1 }, (_, index) => descriptor(`test.${index}`)) })).toThrow(protocolError("protocol_validation_error"));
  });

  it("parses the exact current C# health shape and bounds nested package state and counters", () => {
    expect(parseHealthResponseV1(healthResponse())).toEqual(healthResponse());
    const inventory = {
      status: "current" as const,
      failure_reason: "" as const,
      mismatch_package: "",
      local_package_count: 1,
      source_file_count: 2,
      asset_database_file_count: 2,
      missing_asset_file_count: 0,
      unexpected_asset_file_count: 0,
      compiler_source_file_count: 2,
      missing_compiler_source_file_count: 0,
      unexpected_compiler_source_file_count: 0,
      source_fingerprint: "source",
      asset_database_fingerprint: "assets",
      source_state_fingerprint: "state",
      compiler_source_fingerprint: "compiler",
    };
    expect(parseHealthResponseV1({
      ...healthResponse(),
      package_update: { ...healthResponse().package_update, local_package_code_inventory: inventory },
    }).package_update.local_package_code_inventory).toEqual(inventory);
    expect(parseHealthResponseV1({
      ...healthResponse(),
      package_update: { ...healthResponse().package_update, local_package_code_inventory: { ...inventory, status: "unavailable", failure_reason: "compiler_source_read_exception" } },
    }).package_update.local_package_code_inventory).toMatchObject({ status: "unavailable", failure_reason: "compiler_source_read_exception" });
    const { compiler_source_fingerprint: _omittedCompilerFingerprint, ...partialInventory } = inventory;
    expect(() => parseHealthResponseV1({
      ...healthResponse(),
      package_update: { ...healthResponse().package_update, local_package_code_inventory: partialInventory },
    })).toThrow(protocolError("protocol_validation_error"));
    const legacyPackageUpdate: Partial<ReturnType<typeof healthResponse>["package_update"]> = { ...healthResponse().package_update };
    delete legacyPackageUpdate.last_observed_utc;
    delete legacyPackageUpdate.last_transition_utc;
    expect(parseHealthResponseV1({ ...healthResponse(), package_update: legacyPackageUpdate }).package_update).not.toHaveProperty("last_observed_utc");
    for (const mutation of [
      { ready: "yes" },
      { status: "unknown" },
      { queue_depth: BRIDGE_PROTOCOL_BOUNDS.counter + 1 },
      { package_update: { ...healthResponse().package_update, status: "future" } },
      { package_update: { ...healthResponse().package_update, last_refresh_utc: "not-a-time" } },
      { package_update: { ...healthResponse().package_update, last_observed_utc: "not-a-time" } },
      { package_update: { ...healthResponse().package_update, last_transition_utc: "not-a-time" } },
      { package_update: { ...healthResponse().package_update, fingerprint_failure: { evidence_class: "project_packages", reason: "future", file: "manifest" } } },
      { package_update: { ...healthResponse().package_update, fingerprint_failure: { evidence_class: "project_packages", reason: "read_exception", file: "/host/path" } } },
      { dirty_scenes: [{ scene_path: "Assets/A.unity", scene_name: "A" }] },
    ]) {
      expect(() => parseHealthResponseV1({ ...healthResponse(), ...mutation })).toThrow(protocolError("protocol_validation_error"));
    }
  });

  it("rejects a native failure category without an affirmative correlated observation", () => {
    const progress = {
      observed: true, authoritative: true, active: true, failed: false, blocks_readiness: true,
      state: "active", category: "compute_variant_compile", relevant_item_count: 2, active_item_count: 1,
      progress_permille: 928, process_id: 12345, catalog_generation: 1,
      compilation_generation: 0, completed_compilation_generation: 0, package_fingerprint: "a".repeat(64),
      observed_utc: "2026-09-23T09:00:00.100Z", failure_reason: "", cancellation_attempted: false, work_replayed: false,
      native_failure_category: "shader_variant_compile", native_failure_observed_utc: "2026-09-23T09:00:00.050Z",
      native_failure_absence_authoritative: false,
    };
    const baseline = healthResponse();
    expect(() => parseHealthResponseV1({ ...baseline, status: "busy", ready: false, background_compiler_progress: progress })).toThrow(protocolError("protocol_validation_error"));
    const valid = { ...progress, correlated_native_failure_observed: true };
    expect(parseHealthResponseV1({ ...baseline, status: "busy", ready: false, background_compiler_progress: valid }).background_compiler_progress).toMatchObject({ correlated_native_failure_observed: true, native_failure_category: "shader_variant_compile" });
  });

  it("parses synthetic single-command and C# log responses with exact bounds", () => {
    const single = parseSingleCommandResponseV1({ schema_version: 1, project: "game", commands_hash: "hash", command: descriptor() });
    expect(single.command?.id).toBe("asset.search");
    expect(parseSingleCommandResponseV1({ schema_version: 1, project: "game", commands_hash: "hash", command: null }).command).toBeNull();

    const entry = { timestamp: "2026-07-11T12:00:00.0000000Z", type: "Error", message: "m".repeat(BRIDGE_PROTOCOL_BOUNDS.logMessageChars), stack_trace: "s".repeat(BRIDGE_PROTOCOL_BOUNDS.logStackChars) };
    expect(parseLogsResponseV1({ schema_version: 1, entries: Array(1_000).fill(entry) }).entries).toHaveLength(1_000);
    const buffer = { epoch: "a".repeat(32), initialized_at_utc: "2026-08-27T12:34:56.0000000Z", reset_loss: true, history_complete: false };
    expect(parseLogsResponseV1({ schema_version: 1, entries: [], buffer })).toEqual({ schema_version: 1, entries: [], buffer });
    expect(() => parseLogsResponseV1({ schema_version: 1, entries: Array(1_001).fill(entry) })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseLogsResponseV1({ schema_version: 1, entries: [{ ...entry, message: `${entry.message}x` }] })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseLogsResponseV1({ schema_version: 1, entries: [{ ...entry, stack_trace: `${entry.stack_trace}x` }] })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseLogsResponseV1({ schema_version: 1, entries: [{ ...entry, type: "Debug" }] })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseLogsResponseV1({ schema_version: 1, entries: [], buffer: { ...buffer, epoch: "not-an-epoch" } })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseLogsResponseV1({ schema_version: 1, entries: [], buffer: { ...buffer, history_complete: true } })).toThrow(protocolError("protocol_validation_error"));
  });

  it("strips known-v1 unknown response fields and rejects duplicate command ids", () => {
    const health = parseHealthResponseV1({ ...healthResponse(), secret: "drop", package_update: { ...healthResponse().package_update, secret: "drop" } });
    expect(health).not.toHaveProperty("secret");
    expect(health.package_update).not.toHaveProperty("secret");
    const logs = parseLogsResponseV1({ schema_version: 1, secret: "drop", entries: [{ timestamp: "2026-07-11T12:00:00Z", type: "Log", message: "ok", secret: "drop" }], buffer: { epoch: "a".repeat(32), initialized_at_utc: "2026-08-27T12:34:56Z", reset_loss: false, history_complete: false, secret: "drop" } });
    expect(logs).not.toHaveProperty("secret");
    expect(logs.entries[0]).not.toHaveProperty("secret");
    expect(logs.buffer).not.toHaveProperty("secret");
    expect(() => parseCommandCatalogV1({ schema_version: 1, project: "game", commands_hash: "hash", commands: [descriptor(), descriptor()] })).toThrow(protocolError("protocol_validation_error"));
  });

  it("publishes only bounded primitive versions and issue path/code without messages or values", () => {
    const secret = "never-leak-protocol-secret";
    const mismatch = captureProtocolError(() => parseHealthResponseV1({ ...healthResponse(), schema_version: `${secret}${"x".repeat(100)}` }));
    expect(mismatch.diagnostic).toMatchObject({ reason: "unsupported_schema_version", expected_version: 1, received_version: expect.any(String) });
    expect(String(mismatch.diagnostic.received_version)).toHaveLength(BRIDGE_PROTOCOL_BOUNDS.receivedPrimitiveChars);
    const malformed = captureProtocolError(() => parseHealthResponseV1({ ...healthResponse(), package_update: { ...healthResponse().package_update, message: secret.repeat(2_000) } }));
    expect(malformed.diagnostic.issues).toContainEqual({ path: ["package_update", "message"], code: "too_big" });
    expect(JSON.stringify(malformed)).not.toContain(secret);
    expect(Object.keys(malformed.diagnostic.issues?.[0] ?? {})).toEqual(["path", "code"]);
    expect(JSON.stringify(malformed.diagnostic.issues)).not.toContain(secret);
  });

  it("iteratively bounds nested JSON while requiring object command schemas", () => {
    expect(parseCommandDescriptorV1(descriptor()).schema).toEqual({ type: "object", properties: {} });
    expect(() => parseCommandDescriptorV1({ ...descriptor(), schema: { type: "array" } })).toThrow(protocolError("protocol_validation_error"));
    expect(() => parseCommandDescriptorV1({ ...descriptor(), schema: [] })).toThrow(protocolError("protocol_validation_error"));
    expect(isBoundedJson({ value: [1, true, null, "ok"] }, true)).toBe(true);
    expect(isBoundedJson({ value: Number.NaN }, true)).toBe(false);
    expect(isBoundedJson({ value: Array(BRIDGE_PROTOCOL_BOUNDS.jsonArrayItems + 1).fill(null) }, true)).toBe(false);
    expect(isBoundedJson({ value: Array(5).fill("x".repeat(Math.floor(BRIDGE_PROTOCOL_BOUNDS.jsonTotalStringChars / 4))) }, true)).toBe(false);

    let nested: Record<string, unknown> = { leaf: true };
    for (let depth = 0; depth <= BRIDGE_PROTOCOL_BOUNDS.jsonDepth; depth += 1) nested = { nested };
    expect(isBoundedJson(nested, true)).toBe(false);
    expect(() => parseCommandDescriptorV1({ ...descriptor(), schema: { type: "object", nested } })).toThrow(protocolError("protocol_validation_error"));
  });

  it("accepts v1 envelopes and strips unknown fields instead of passing them through", () => {
    const heartbeat = parseHeartbeatRecordV1({
      ...heartbeatRecord(),
      canonical_project_root: "forwarded-wrong-value",
      aliases: [{ environment: "wrong", host_path: "wrong", canonical_path: "wrong", alias_secret: "strip" }],
      heartbeat_generation: 7,
      refresh_generation: 2,
      last_asset_refresh_utc: "2026-07-15T06:00:00.000Z",
      compilation_generation: 4,
      completed_compilation_generation: 4,
      last_compilation_started_utc: "2026-07-15T06:00:01.000Z",
      last_compilation_finished_utc: "2026-07-15T06:00:02.000Z",
      unity_process_start_utc: "2026-07-15T05:00:00.000Z",
      quit_operation_id: "quit-20260807-123456-deadbeef",
      quit_caller_args_hash: `sha256:${"a".repeat(64)}`,
      quit_request_state: "deferred",
      quit_requested_utc: "2026-08-07T12:34:56.000Z",
      quit_state_updated_utc: "2026-08-07T12:34:56.100Z",
      unknown_secret: "strip-me",
    });
    expect(heartbeat.canonical_project_root).toBe("c:/work/game");
    expect(heartbeat.aliases).toEqual([
      { environment: "wsl", host_path: "/mnt/c/work/game", canonical_path: "c:/work/game" },
      { environment: "unity", host_path: "C:/work/game", canonical_path: "c:/work/game" },
      { environment: "windows", host_path: "C:\\work\\game", canonical_path: "c:/work/game" },
    ]);
    expect(heartbeat).not.toHaveProperty("unknown_secret");
    expect(heartbeat).toMatchObject({
      heartbeat_generation: 7,
      refresh_generation: 2,
      compilation_generation: 4,
      completed_compilation_generation: 4,
      quit_operation_id: "quit-20260807-123456-deadbeef",
      quit_request_state: "deferred",
    });
    expect(parseHeartbeatRecordV1({
      ...heartbeatRecord(),
      heartbeat_generation: 8,
      compilation_generation: 2,
      completed_compilation_generation: 1,
      last_compilation_started_utc: "2026-07-15T06:01:00.000Z",
      last_compilation_finished_utc: "",
    })).toMatchObject({ compilation_generation: 2, completed_compilation_generation: 1 });

    for (const invalidLifecycle of [
      { heartbeat_generation: 0 },
      { compilation_generation: 1, completed_compilation_generation: 2, last_compilation_started_utc: "2026-07-15T06:00:01.000Z", last_compilation_finished_utc: "2026-07-15T06:00:02.000Z" },
      { compilation_generation: 1, completed_compilation_generation: 0 },
      { compilation_generation: 1, completed_compilation_generation: 1, last_compilation_started_utc: "2026-07-15T06:00:01.000Z" },
      { compilation_generation: 2, completed_compilation_generation: 1, last_compilation_started_utc: "2026-07-15T06:00:01.000Z", last_compilation_finished_utc: "2026-07-15T06:00:02.000Z" },
      { refresh_generation: 1 },
      { last_compilation_started_utc: "2026-07-15T06:00:01.000Z" },
      { last_compilation_finished_utc: "2026-07-15T06:00:02.000Z" },
      { last_asset_refresh_utc: "2026-07-15T06:00:00.000Z" },
      { quit_operation_id: "quit-20260807-123456-deadbeef", quit_request_state: "deferred" },
      { quit_request_state: "accepted" },
      { quit_caller_args_hash: `sha256:${"a".repeat(64)}` },
    ]) {
      expect(() => parseHeartbeatRecordV1({ ...heartbeatRecord(), ...invalidLifecycle })).toThrow(protocolError("protocol_validation_error"));
    }

    const catalog = parseCommandCatalogV1({
      schema_version: 1,
      project: "game",
      commands_hash: "hash",
      commands: [{ ...descriptor(), unknown_descriptor_field: "strip" }],
      manifest_diagnostics: [{ code: "manifest_invalid", message: "safe", unknown_diagnostic_field: "strip" }],
      unknown_catalog_field: "strip",
    });
    expect(catalog).not.toHaveProperty("unknown_catalog_field");
    expect(catalog.commands[0]).not.toHaveProperty("unknown_descriptor_field");
    expect(catalog.manifest_diagnostics?.[0]).not.toHaveProperty("unknown_diagnostic_field");
  });

  it("classifies unknown, missing, and malformed versions without retaining raw values or content", () => {
    for (const value of [
      { ...heartbeatRecord(), schema_version: 2, token: "never-leak-a" },
      { project: "private-project", token: "never-leak-b" },
      { ...heartbeatRecord(), schema_version: "1", token: "never-leak-c" },
      null,
    ]) {
      try {
        parseHeartbeatRecordV1(value);
        throw new Error("expected mismatch");
      } catch (error) {
        expect(error).toBeInstanceOf(BridgeProtocolError);
        expect(error).toMatchObject({ code: "protocol_version_mismatch", diagnostic: { code: "protocol_version_mismatch", envelope: "heartbeat" } });
        expect(JSON.stringify(error)).not.toContain("never-leak");
        expect(JSON.stringify(error)).not.toContain("private-project");
        expect(error).not.toHaveProperty("received");
      }
    }
  });

  it("normalizes optional fields and set-like fields but preserves semantic schema arrays", () => {
    const left = descriptor();
    const right = {
      ...descriptor(),
      tags: ["z", "a", "z"],
      requires_editor_status: ["playing", "editing", "playing"],
      schema: { required: ["b", "a"], type: "object", properties: {} },
    } satisfies CommandDescriptor;
    const equivalent = {
      ...right,
      tags: ["a", "z"],
      requires_editor_status: ["editing", "playing"],
    } satisfies CommandDescriptor;
    expect(normalizeEffectiveCommandDescriptor(left)).toMatchObject({ tags: null, requires_editor_status: null, wrapper_wait: null, source: null, trust: null });
    expect(normalizeEffectiveCommandDescriptor(right)).toMatchObject({ tags: ["a", "z"], requires_editor_status: ["editing", "playing"] });
    expect(commandDescriptorRevision(right)).toBe(commandDescriptorRevision(equivalent));
    expect(commandDescriptorRevision(right)).not.toBe(commandDescriptorRevision({ ...equivalent, schema: { ...equivalent.schema, required: ["a", "b"] } }));
    expect(commandDescriptorRevision({ ...left, ignored: "strip" } as CommandDescriptor)).toBe(commandDescriptorRevision(left));
  });

  it("changes revisions for effective mutations and makes catalog ordering canonical", () => {
    const base = descriptor("a.command");
    const revision = commandDescriptorRevision(base);
    for (const mutation of [
      { title: "Changed" }, { description: "Changed" }, { package: "changed" }, { risk: "write" as const },
      { idempotency: "unsafe" as const }, { timeout_ms: 20_000 }, { schema: { type: "object", required: ["x"] } },
      { output_limit: { max_items: 1 } }, { supports_job: true }, { manifest_path: "Packages/x.json" },
      { wrapper_wait: { mode: "wait" as const, wait_for_replacement_ms: { min_ms: 1, max_ms: 45_000, deadline_scope: "pre_dispatch_only" as const } } },
    ]) {
      expect(commandDescriptorRevision({ ...base, ...mutation })).not.toBe(revision);
    }
    const other = descriptor("z.command");
    expect(commandCatalogRevision([base, other])).toBe(commandCatalogRevision([other, base]));
    expect(commandCatalogRevision([base, other])).not.toBe(commandCatalogRevision([base, { ...other, description: "Changed" }]));
    expect(() => commandCatalogRevision([base, { ...base }])).toThrow(protocolError("protocol_validation_error"));
  });

  it("validates bounded descriptor-backed wrapper wait contracts", () => {
    const replacement = {
      mode: "wait",
      wait_for_replacement_ms: { min_ms: 1, max_ms: 3_600_000, deadline_scope: "pre_dispatch_only" },
    } as const;
    const terminal = {
      mode: "wait",
      wait_for_test_run_terminal_ms: { min_ms: 1, max_ms: 3_600_000, deadline_scope: "start_and_terminal_poll", response_mode: "compact", dry_run: false },
    } as const;

    expect(parseCommandDescriptorV1({ ...descriptor(), wrapper_wait: replacement }).wrapper_wait).toEqual(replacement);
    expect(parseCommandDescriptorV1({ ...descriptor(), wrapper_wait: terminal }).wrapper_wait).toEqual(terminal);
    const readiness = { ...terminal, wait_for_test_run_terminal_ms: { ...terminal.wait_for_test_run_terminal_ms, compile_readiness_contract: "sgg.unity_mcp.test_run_compile_readiness.v1" } };
    expect(parseCommandDescriptorV1({ ...descriptor(), wrapper_wait: readiness }).wrapper_wait).toEqual(readiness);
    expect(commandDescriptorRevision({ ...descriptor(), wrapper_wait: readiness })).not.toBe(commandDescriptorRevision({ ...descriptor(), wrapper_wait: terminal }));
    const futureReadiness = { ...readiness, wait_for_test_run_terminal_ms: { ...readiness.wait_for_test_run_terminal_ms, compile_readiness_contract: "sgg.unity_mcp.test_run_compile_readiness.v2" } };
    expect(parseCommandDescriptorV1({ ...descriptor(), wrapper_wait: futureReadiness }).wrapper_wait).toEqual(futureReadiness);
    for (const compile_readiness_contract of [null, true, 1, {}, "", "x".repeat(129)]) {
      expect(() => parseCommandDescriptorV1({ ...descriptor(), wrapper_wait: { ...readiness, wait_for_test_run_terminal_ms: { ...readiness.wait_for_test_run_terminal_ms, compile_readiness_contract } } })).toThrow(protocolError("protocol_validation_error"));
    }
    for (const wrapper_wait of [
      { mode: "wait" },
      { ...replacement, wait_for_replacement_ms: { ...replacement.wait_for_replacement_ms, min_ms: 2, max_ms: 1 } },
      { ...replacement, wait_for_replacement_ms: { ...replacement.wait_for_replacement_ms, max_ms: 3_600_001 } },
      { ...terminal, wait_for_test_run_terminal_ms: { ...terminal.wait_for_test_run_terminal_ms, response_mode: "full" } },
      { ...terminal, wait_for_test_run_terminal_ms: { ...terminal.wait_for_test_run_terminal_ms, dry_run: true } },
    ]) expect(() => parseCommandDescriptorV1({ ...descriptor(), wrapper_wait })).toThrow(protocolError("protocol_validation_error"));
  });

  it("strictly parses bounded v1 invoke requests and strips known-v1 unknown fields", () => {
    const request = parseBridgeInvokeRequestV1({
      schema_version: 1,
      request_id: "request-1",
      project: "game",
      command: "compile.status",
      args: { nested: [true, 1] },
      settings: {
        asset_index_cache_dir: "C:/cache/game",
        feature_flags: { safe_flag: true },
        mono_behaviour_allowlist: [{ assembly_name: "Game", full_type_name: "Game.Player", note: "safe" }],
        unknown: "drop",
      },
      risk: "read",
      deadline_unix_ms: 1,
      expected_instance_id: "game-a",
      expected_catalog_generation: 7,
      expected_commands_hash: "catalog-a",
      unknown: "drop",
    });
    expect(request).not.toHaveProperty("unknown");
    expect(request.settings).not.toHaveProperty("unknown");
    expect(JSON.parse(serializeBridgeInvokeRequestV1(request))).toEqual(request);

    for (const invalid of [
      { ...request, schema_version: 2 },
      { ...request, schema_version: undefined },
      { ...request, project: "" },
      { ...request, command: "x".repeat(BRIDGE_PROTOCOL_BOUNDS.identifierChars + 1) },
      { ...request, args: [] },
      { ...request, request_id: "unsafe id" },
      { ...request, request_id: "x".repeat(BRIDGE_PROTOCOL_BOUNDS.requestIdChars + 1) },
      { ...request, risk: "unknown" },
      { ...request, deadline_unix_ms: Number.NaN },
      { ...request, deadline_unix_ms: 0 },
      { ...request, deadline_unix_ms: Number.MAX_SAFE_INTEGER + 1 },
      { ...request, settings: { feature_flags: Object.fromEntries(Array.from({ length: 257 }, (_, index) => [`f${index}`, true])) } },
      { ...request, settings: { mono_behaviour_allowlist: Array(1_001).fill({ assembly_name: "Game", full_type_name: "Game.Player" }) } },
      { ...request, settings: { asset_index_cache_dir: "x".repeat(BRIDGE_PROTOCOL_BOUNDS.pathChars + 1) } },
      { ...request, expected_catalog_generation: undefined },
      { ...request, expected_catalog_generation: 0 },
    ]) expect(() => parseBridgeInvokeRequestV1(invalid)).toThrow(BridgeProtocolError);
  });

  it("strictly parses correlated bounded invoke ok/error responses", () => {
    const ok = parseBridgeInvokeResponseV1({ schema_version: 1, request_id: "request-1", status: "ok", data: { ready: true }, diagnostics: [{ severity: "info", source: "unity", message: "ready", details: { count: 1 }, unknown: "drop" }], duration_ms: 1, unknown: "drop" }, "request-1");
    expect(ok).not.toHaveProperty("unknown");
    expect(ok.diagnostics[0]).not.toHaveProperty("unknown");
    for (const source of ["gateway", "bridge", "unity"] as const) {
      expect(parseBridgeInvokeResponseV1({ schema_version: 1, status: "ok", data: {}, diagnostics: [{ severity: "info", source, message: "ok" }], duration_ms: 0 }).diagnostics[0]?.source).toBe(source);
    }
    expect(parseBridgeInvokeResponseV1({ schema_version: 1, status: "error", error: { code: "command_failed", message: "failed", details: { phase: "compile" }, unknown: "drop" }, diagnostics: [], duration_ms: 0 })).toMatchObject({ status: "error", error: { code: "command_failed" } });

    for (const invalid of [
      { schema_version: 2, status: "ok", data: {}, diagnostics: [], duration_ms: 0 },
      { schema_version: 1, status: "ok", diagnostics: [], duration_ms: 0 },
      { schema_version: 1, status: "error", error: { code: "NOT-SAFE", message: "failed" }, diagnostics: [], duration_ms: 0 },
      { schema_version: 1, status: "error", error: { code: "failed", message: "" }, diagnostics: [], duration_ms: 0 },
      { schema_version: 1, status: "ok", data: {}, diagnostics: Array(101).fill({ severity: "info", source: "bridge", message: "ok" }), duration_ms: 0 },
      { schema_version: 1, status: "ok", data: {}, diagnostics: [{ severity: "debug", source: "bridge", message: "ok" }], duration_ms: 0 },
      { schema_version: 1, status: "ok", data: {}, diagnostics: [{ severity: "info", source: "compile", message: "ok" }], duration_ms: 0 },
      { schema_version: 1, status: "ok", data: {}, diagnostics: [{ severity: "info", source: "bridge", message: "ok", details: [] }], duration_ms: 0 },
      { schema_version: 1, status: "error", error: { code: "failed", message: "failed", details: [] }, diagnostics: [], duration_ms: 0 },
      { schema_version: 1, status: "ok", data: {}, diagnostics: [], duration_ms: Number.NaN },
      { schema_version: 1, status: "ok", data: {}, diagnostics: [], duration_ms: BRIDGE_PROTOCOL_BOUNDS.durationMs + 1 },
    ]) expect(() => parseBridgeInvokeResponseV1(invalid)).toThrow(BridgeProtocolError);

    const mismatch = captureProtocolError(() => parseBridgeInvokeResponseV1({ schema_version: 1, request_id: "other", status: "ok", data: {}, diagnostics: [], duration_ms: 0 }, "request-1"));
    expect(mismatch.diagnostic.reason).toBe("request_id_mismatch");
  });
});

function protocolError(code: "protocol_version_mismatch" | "protocol_validation_error"): string {
  return code;
}

function descriptor(id = "asset.search"): CommandDescriptor {
  return {
    id,
    title: "Asset search",
    description: "Search project assets",
    package: "com.sungeargames.unity-mcp",
    risk: "read",
    idempotency: "safe",
    timeout_ms: 10_000,
    schema: { type: "object", properties: {} },
  };
}

function heartbeatRecord(): Record<string, unknown> {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    display_name: "Game",
    project_root: "/mnt/c/work/game",
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    token: "private-token",
    status: "ready",
    commands_hash: "abc123",
    last_heartbeat: "2026-07-11T12:00:00.000Z",
  };
}

function healthResponse() {
  return {
    schema_version: 1 as const, instance_id: "game-a", project: "game", status: "ready" as const, unity_version: "6000.0.0f1", commands_hash: "hash",
    is_compiling: false, is_updating: false, is_playing: false, ready: true,
    package_update: { status: "current" as const, auto_refresh_enabled: false, refresh_pending: false, package_refresh_required: false, stale_package_resolution: false, fingerprint: "abc", pending_fingerprint: "", refresh_count: 0, last_refresh_utc: "", last_observed_utc: "2026-09-01T12:00:00.000Z", last_transition_utc: "2026-09-01T11:59:59.000Z", fingerprint_failure: null, message: "Package/runtime files are current." },
    has_unsaved_scene_changes: false, dirty_scene_count: 0, dirty_scenes: [], queue_depth: 0,
  };
}

function captureProtocolError(action: () => unknown): BridgeProtocolError {
  try {
    action();
    throw new Error("expected protocol error");
  } catch (error) {
    expect(error).toBeInstanceOf(BridgeProtocolError);
    return error as BridgeProtocolError;
  }
}
