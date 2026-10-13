import { createServer, type IncomingMessage, type ServerResponse, type Server } from "node:http";

import { afterEach, describe, expect, it, vi } from "vitest";

import { BridgeRequestError, HttpUnityBridgeClient, type BridgeRequestErrorDetails, type BridgeInvokeRequest, type BridgeTransportOptions, type CommandDescriptor } from "../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../src/config/config.js";
import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import type { GatewayResult } from "../src/mcp/results.js";
import { InMemoryAuditSink } from "../src/observability/audit.js";
import { ProjectRegistry, type HeartbeatRecord, type ProjectResolution, type PublicProjectRecord, type VerificationProjectResolution } from "../src/registry/project-registry.js";

const refresh = "compile.refresh_and_wait";
const waitReady = "compile.wait_ready";
const packageVersion = "2026.10.7-4";
const rejectedPayloadMarker = "failed-catalog-payload-must-not-be-retained";
const timeoutMs = 600;
const pollMs = 100;
type LagReason = "catalog_generation_mismatch" | "commands_hash_mismatch";
type ErrorDetailPatch = { [Key in keyof BridgeRequestErrorDetails]?: BridgeRequestErrorDetails[Key] | undefined };
type HarnessOptions = {
  timeoutMs?: number;
  lags?: LagReason[];
  persistent?: LagReason;
  catalogFault?: string;
  injectedError?: (record: HeartbeatRecord) => Error;
  replacementPatch?: Partial<HeartbeatRecord>;
  sourcePatch?: Partial<HeartbeatRecord>;
  sourceDescriptorPatch?: Partial<CommandDescriptor>;
  verificationDescriptorPatch?: Partial<CommandDescriptor>;
  barrierPatch?: Record<string, unknown>;
  omitBarrier?: boolean;
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
  run: () => Promise<GatewayResult>;
};
const harnesses: Harness[] = [];

afterEach(async () => {
  for (const harness of harnesses.splice(0)) {
    harness.server.closeAllConnections();
    await new Promise<void>((resolve, reject) => harness.server.close((error) => error ? reject(error) : resolve()));
    expect(harness.handlerErrors).toEqual([]);
    expect(harness.invocations.filter((request) => request.command === refresh)).toHaveLength(1);
    expect(harness.invocations.every((request) => request.command === refresh || request.command === waitReady)).toBe(true);
    expect(harness.bridge.logCalls).toBe(0);
    expect(harness.nativeRead).not.toHaveBeenCalled();
    expect(harness.nativeMutation).not.toHaveBeenCalled();
  }
});

describe("MCP-PR-220 strict compile catalog metadata convergence (synthetic, no host reproduction claim)", () => {
  it.each<LagReason>(["catalog_generation_mismatch", "commands_hash_mismatch"])("re-resolves fresh heartbeat metadata after %s before one verified read", async (reason) => {
    const harness = await makeHarness({ lags: [reason] });
    const result = await harness.run();
    expect(result).toMatchObject({ status: "ok", data: { ready: true, post_reload_stability_verified: true, write_command_repeated: false, write_replay_allowed: false } });
    expect(evidence(result)).toMatchObject({ verification_status: "ready_clean", catalog_metadata_read_retry_count: 1, catalog_metadata_last_retry_reason: reason, catalog_metadata_converged: true, read_request_count: 1, write_request_count: 1 });
    expect(harness.sleeps).toEqual([pollMs]);
    expect(harness.registry.verificationCalls).toHaveLength(2);
    const reads = harness.bridge.described.filter((entry) => entry.selector === `command:${waitReady}`);
    expect(reads).toHaveLength(2);
    expect(reads[1]!.record).toMatchObject(harness.registry.current);
    expect(reads[1]!.record.commands_hash === reads[0]!.record.commands_hash && reads[1]!.record.catalog_generation === reads[0]!.record.catalog_generation).toBe(false);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh, waitReady]);
    expect(harness.invocations[1]).toMatchObject({ risk: "read", expected_instance_id: "game-replacement", expected_catalog_generation: harness.registry.current.catalog_generation, expected_commands_hash: harness.registry.current.commands_hash });
    expect(harness.bridge.errors[0]).toMatchObject({ message: "bridge_protocol_error", details: { endpoint: "/commands", failure_kind: "response_shape", protocol_schema: "sgg.unity_mcp.bridge.command_catalog.v1", protocol_reason: reason } });
    expect(JSON.stringify([result, harness.audit.events])).not.toContain(rejectedPayloadMarker);
  });

  it("allows successive generation/hash lag only after separate fresh registry resolutions", async () => {
    const harness = await makeHarness({ lags: ["catalog_generation_mismatch", "commands_hash_mismatch"] });
    const result = await harness.run();
    expect(result.status).toBe("ok");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 2, catalog_metadata_last_retry_reason: "commands_hash_mismatch", catalog_metadata_converged: true, read_request_count: 1, attempt_count: 3 });
    expect(harness.registry.verificationCalls).toHaveLength(3);
    expect(harness.sleeps).toEqual([pollMs, pollMs]);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh, waitReady]);
    expect(JSON.stringify(result)).not.toContain(rejectedPayloadMarker);
  });

  it("does not label a normally converged catalog as metadata lag", async () => {
    const harness = await makeHarness();
    const result = await harness.run();
    expect(result.status).toBe("ok");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, read_request_count: 1 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_converged");
    expect(harness.sleeps).toEqual([]);
  });

  it.each<LagReason>(["catalog_generation_mismatch", "commands_hash_mismatch"])("persistent %s exhausts the existing virtual deadline as catalog_stale without a verifier invoke", async (reason) => {
    const harness = await makeHarness({ persistent: reason });
    const result = await harness.run();
    const details = evidence(result);
    expect(details).toMatchObject({ verification_status: "catalog_stale", catalog_metadata_last_retry_reason: reason, catalog_metadata_converged: false, read_request_count: 0, write_request_count: 1, original_outcome: { status: "error", error_code: "bridge_protocol_error" } });
    expect(details.catalog_metadata_read_retry_count).toBeGreaterThan(1);
    expect(details.catalog_metadata_read_retry_count).toBe(Number(details.attempt_count) - 1);
    expect(details.catalog_metadata_read_retry_count).toBe(harness.bridge.described.filter((entry) => entry.selector === `command:${waitReady}`).length - 1);
    expect(Number(details.attempt_count)).toBeLessThanOrEqual(Number(details.attempt_budget));
    expect(Number(details.verification_elapsed_ms)).toBeLessThanOrEqual(timeoutMs);
    expect(harness.sleeps.every((duration) => duration > 0 && duration <= pollMs)).toBe(true);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
    expect(JSON.stringify(result)).not.toContain(rejectedPayloadMarker);
    expect(result).toMatchObject({ status: "error", error: { code: "bridge_protocol_error", details: { original_command_completed: "unknown", post_reload_stability_verified: false, write_command_repeated: false, write_replay_allowed: false } } });
  });

  it.each<LagReason>(["catalog_generation_mismatch", "commands_hash_mismatch"])("first %s rejection followed by deadline expiry is not an actual read retry", async (reason) => {
    const harness = await makeHarness({ persistent: reason, timeoutMs: pollMs });
    const result = await harness.run();
    expect(evidence(result)).toMatchObject({ verification_status: "catalog_stale", attempt_count: 1, catalog_metadata_read_retry_count: 0, catalog_metadata_last_retry_reason: reason, catalog_metadata_converged: false, read_request_count: 0, write_request_count: 1, original_outcome: { status: "error", error_code: "bridge_protocol_error" } });
    expect(harness.bridge.described.filter((entry) => entry.selector === `command:${waitReady}`)).toHaveLength(1);
    expect(harness.registry.verificationCalls).toHaveLength(1);
    expect(harness.sleeps).toEqual([pollMs]);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
    expect(result).toMatchObject({ status: "error", error: { code: "bridge_protocol_error", details: { original_command_completed: "unknown", post_reload_stability_verified: false, write_command_repeated: false, write_replay_allowed: false } } });
  });

  it.each(["project", "instance", "schema_version", "payload_shape", "duplicate_commands", "invalid_json", "content_type", "http_401", "http_500"])("does not retry strict-parser failure %s as catalog metadata lag", async (catalogFault) => {
    const harness = await makeHarness({ catalogFault });
    const result = await harness.run();
    expect(result.status).toBe("error");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(harness.sleeps).toEqual([]);
    expect(harness.bridge.described.filter((entry) => entry.selector === `command:${waitReady}`)).toHaveLength(1);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
  });

  it.each([
    { name: "health_schema", patch: { protocol_schema: "sgg.unity_mcp.bridge.health.v1" } },
    { name: "single_command_schema", patch: { protocol_schema: "sgg.unity_mcp.bridge.single_command.v1" } },
    { name: "missing_schema", patch: { protocol_schema: undefined } },
    { name: "other_reason", patch: { protocol_reason: "instance_id_mismatch" } },
    { name: "missing_reason", patch: { protocol_reason: undefined } },
    { name: "invoke_endpoint", patch: { endpoint: "/invoke" } },
    { name: "endpoint_prefix", patch: { endpoint: "/commands/other" } },
    { name: "endpoint_query", patch: { endpoint: "/commands?selector=commands" } },
    { name: "parse_failure", patch: { failure_kind: "response_parse" as const } },
    { name: "wrong_port", patch: { bridge_port: 1 } },
    { name: "wrong_host", patch: { bridge_host: "127.0.0.2" } },
  ])("refuses forged metadata-lag classification $name", async ({ patch }) => {
    const harness = await makeHarness({ injectedError: (record) => metadataError(record, patch) });
    const result = await harness.run();
    expect(result.status).toBe("error");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, read_request_count: 0 });
    expect(harness.sleeps).toEqual([]);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
  });

  it("does not accept a non-BridgeRequestError with matching text and details", async () => {
    const harness = await makeHarness({ injectedError: (record) => Object.assign(new Error("bridge_protocol_error"), { details: metadataError(record).details }) });
    const result = await harness.run();
    expect(result.status).toBe("error");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, read_request_count: 0 });
    expect(harness.sleeps).toEqual([]);
  });

  it.each(["bridge_timeout", "bridge_unreachable"] as const)("preserves ordinary bounded %s retries without metadata-drift classification", async (code) => {
    const harness = await makeHarness({ injectedError: (record) => new BridgeRequestError(code, metadataError(record).details) });
    const result = await harness.run();
    expect(result.status).toBe("error");
    expect(evidence(result)).toMatchObject({ verification_status: "timeout", catalog_metadata_read_retry_count: 0, read_request_count: 0 });
    expect(evidence(result)).not.toHaveProperty("catalog_metadata_last_retry_reason");
    expect(harness.sleeps.length).toBeGreaterThan(0);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
  });

  it.each([
    { name: "project", patch: { project: "other" } },
    { name: "root", patch: { canonical_project_root: "c:/u/other", project_root: "C:/u/other" } },
    { name: "pid", patch: { unity_pid: 54321 } },
    { name: "start", patch: { unity_process_start_utc: "2026-10-07T01:00:01.000Z" } },
    { name: "missing_start", patch: { unity_process_start_utc: undefined } },
    { name: "package", patch: { package_version: "2026.10.7-2" } },
    { name: "missing_package", patch: { package_version: undefined } },
  ])("never retries catalog lag across replacement $name drift", async ({ patch }) => {
    const harness = await makeHarness({ persistent: "catalog_generation_mismatch", replacementPatch: patch });
    const result = await harness.run();
    expect(result.status).toBe("error");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, read_request_count: 0 });
    expect(harness.sleeps).toEqual([]);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
  });

  it.each([
    { name: "source_trust", sourceDescriptorPatch: { trust: "untrusted_package" as const } },
    { name: "source_kind", sourceDescriptorPatch: { source: "manifest" as const } },
    { name: "source_package_id", sourceDescriptorPatch: { package_id: "com.other.extension" } },
    { name: "descriptor_package_version", sourceDescriptorPatch: { package_version: "2026.10.7-2" } },
    { name: "missing_descriptor_package_version", sourceDescriptorPatch: { package_version: undefined } },
    { name: "source_heartbeat_package", sourcePatch: { package_version: "2026.10.7-2" } },
  ])("requires trusted exact source provenance before retrying: $name", async (options) => {
    const harness = await makeHarness({ ...options, persistent: "commands_hash_mismatch" });
    const result = await harness.run();
    expect(result.status).toBe("error");
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 0, read_request_count: 0 });
    expect(harness.sleeps).toEqual([]);
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
  });

  it.each([
    { name: "risk", patch: { risk: "write" as const } },
    { name: "trust", patch: { trust: "untrusted_package" as const } },
    { name: "source", patch: { source: "manifest" as const } },
    { name: "package_id", patch: { package_id: "com.other.extension" } },
    { name: "package_version", patch: { package_version: "2026.10.7-2" } },
    { name: "schema", patch: { schema: { type: "object", properties: { timeout_ms: { type: "string" }, poll_interval_ms: { type: "integer" } }, additionalProperties: false } } },
  ])("convergence does not weaken the fresh verifier descriptor $name gate", async ({ patch }) => {
    const harness = await makeHarness({ lags: ["commands_hash_mismatch"], verificationDescriptorPatch: patch });
    const result = await harness.run();
    expect(result.status).toBe("error");
    expect(evidence(result)).toMatchObject({ verification_status: "catalog_stale", catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true, read_request_count: 0 });
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh]);
  });

  it.each([
    { name: "operation", barrierPatch: { operation_id: "another-operation" } },
    { name: "source", barrierPatch: { source_instance_id: "other-source" } },
    { name: "reload_source", barrierPatch: { reload_source_instance_id: "other-source" } },
    { name: "replacement", barrierPatch: { replacement_instance_id: "other-replacement" } },
    { name: "required", barrierPatch: { required: false } },
    { name: "reload_edge", barrierPatch: { reload_started: false } },
    { name: "post_reload", barrierPatch: { post_reload_observed: false } },
    { name: "generation", barrierPatch: { finished_generation: 2 } },
    { name: "terminal_result", barrierPatch: { terminal_compile_result: "failed", terminal_compile_error_count: 1 } },
    { name: "missing", omitBarrier: true },
  ])("ready/clean flags cannot claim recovery through a wrong $name barrier", async (options) => {
    const harness = await makeHarness({ ...options, lags: ["catalog_generation_mismatch"] });
    const result = await harness.run();
    expect(result).toMatchObject({ status: "error", error: { details: { post_reload_stability_verified: false, write_command_repeated: false, write_replay_allowed: false } } });
    expect(evidence(result)).toMatchObject({ catalog_metadata_read_retry_count: 1, catalog_metadata_converged: true, read_request_count: 1 });
    expect(evidence(result).verification_status).not.toBe("ready_clean");
    expect(harness.invocations.map((request) => request.command)).toEqual([refresh, waitReady]);
  });
});

function evidence(result: GatewayResult): Record<string, unknown> {
  const data = (result.status === "ok" ? result.data : result.error.details) as Record<string, unknown> | undefined;
  const value = data?.compile_refresh_verification;
  expect(value, "post-reload verification evidence").toBeTypeOf("object");
  const details = value as Record<string, unknown>;
  if (details.catalog_metadata_last_retry_reason === undefined) expect(details).not.toHaveProperty("catalog_metadata_converged");
  return details;
}

async function makeHarness(options: HarnessOptions = {}): Promise<Harness> {
  let clock = Date.now();
  let writeIssued = false;
  let pendingHeartbeat: HeartbeatRecord | undefined;
  let postWriteCatalogCalls = 0;
  const invocations: BridgeInvokeRequest[] = [];
  const handlerErrors: string[] = [];
  const sleeps: number[] = [];
  const audit = new InMemoryAuditSink();
  const nativeRead = vi.fn(() => { throw new Error("Native inspection is outside this test"); });
  const nativeMutation = vi.fn(() => { throw new Error("Native mutation is forbidden"); });
  const registry = new MemoryRegistry(heartbeat(options.sourcePatch), () => new Date(clock));
  // The dispatcher admission deadline is the descriptor timeout, not the
  // command's Unity-side timeout_ms argument. Bound both in this fixture.
  const sourceDescriptor = descriptor(refresh, "write", { timeout_ms: options.timeoutMs ?? timeoutMs, ...options.sourceDescriptorPatch });
  const verificationDescriptor = descriptor(waitReady, "read", options.verificationDescriptorPatch);
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
  const bridge = new RecordingHttpBridge(() => writeIssued, options.injectedError);
  const context: DispatcherContext = {
    registry, bridge, audit, policy: allowingPolicy(),
    now: () => new Date(clock),
    sleepMs: async (duration) => {
      sleeps.push(duration);
      clock += duration;
      if (pendingHeartbeat) { registry.current = pendingHeartbeat; pendingHeartbeat = undefined; }
    },
    unityProcessDiagnostics: { status: nativeRead, terminate: nativeMutation },
  };
  const harness = {
    server, registry, bridge, invocations, handlerErrors, sleeps, audit, nativeRead, nativeMutation,
    run: () => {
      clock = Date.now();
      return dispatchTool("sgg_unity_call", { project: "game-source", command: refresh, request_id: "catalog-convergence", args_json: JSON.stringify({ timeout_ms: options.timeoutMs ?? timeoutMs, poll_interval_ms: pollMs }) }, context);
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
        }
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
    if (input.command === refresh) {
      if (writeIssued) throw new Error("Compile write was replayed");
      writeIssued = true;
      registry.current = { ...source, instance_id: "game-replacement", commands_hash: "hash-2", catalog_generation: 2, ...options.replacementPatch };
      response.writeHead(200, { "content-type": "application/json" }).end();
      return;
    }
    if (input.command !== waitReady) throw new Error("Unexpected verifier command");
    const barrier = verifiedBarrier(input, source.instance_id, registry.current.instance_id);
    const data = { status: "ready", ready: true, editor_ready: true, compilable: true, lifecycle_settled: true, timed_out: false, has_compile_errors: false, compile_error_count: 0, ...(options.omitBarrier ? {} : { compile_barrier: { ...barrier, ...options.barrierPatch } }) };
    response.writeHead(200, { "content-type": "application/json" }).end(JSON.stringify({ schema_version: 1, request_id: input.request_id, status: "ok", data, diagnostics: [], duration_ms: 0 }));
  }
}

// No registry directory is created, read, or modified. Deliberately malformed
// selected records exercise dispatcher admission independently of registry filtering.
class MemoryRegistry extends ProjectRegistry {
  readonly verificationCalls: Array<{ root: string; instance: string; record: HeartbeatRecord }> = [];
  constructor(public current: HeartbeatRecord, now: () => Date) {
    super({ instancesDir: "/virtual/never-created-compile-catalog-registry", staleAfterMs: 90_000, now });
  }
  override resolveProject(): ProjectResolution { return { status: "ok", record: structuredClone(this.current) }; }
  override resolveVerificationProject(root: string, instance: string): VerificationProjectResolution {
    this.verificationCalls.push({ root, instance, record: structuredClone(this.current) });
    return { status: "ok", role: this.current.instance_id === instance ? "current" : "replacement", record: structuredClone(this.current) };
  }
  override listProjects(): PublicProjectRecord[] { return this.listProjectRecords(); }
  override listProjectRecords(): PublicProjectRecord[] { return [{ ...this.current, stale: false } as PublicProjectRecord]; }
  override captureSnapshot(): never { throw new Error("Filesystem registry snapshot is forbidden in this fixture"); }
}

class RecordingHttpBridge extends HttpUnityBridgeClient {
  readonly described: Array<{ record: HeartbeatRecord; selector: string }> = [];
  readonly errors: Error[] = [];
  logCalls = 0;
  constructor(readonly afterWrite: () => boolean, readonly injectedError?: (record: HeartbeatRecord) => Error) { super(); }
  override async describe(record: HeartbeatRecord, selector: string, transport?: BridgeTransportOptions): Promise<unknown> {
    this.described.push({ record: structuredClone(record), selector });
    try {
      if (this.afterWrite() && this.injectedError) throw this.injectedError(record);
      return await super.describe(record, selector, transport);
    } catch (error) {
      if (error instanceof Error) this.errors.push(error);
      throw error;
    }
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
  return { id, title: id, description: id, package: "com.sungeargames.unity-mcp", package_id: "com.sungeargames.unity-mcp", package_version: packageVersion, source: "builtin", trust: "builtin", risk, idempotency: risk === "read" ? "safe" : "idempotent", timeout_ms: 295_000, schema: { type: "object", properties: { timeout_ms: { type: "integer", minimum: 1, maximum: 295_000 }, poll_interval_ms: { type: "integer", minimum: 50, maximum: 5_000 } }, additionalProperties: false }, ...patch };
}

function heartbeat(patch: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
  return { schema_version: 1, project: "game", display_name: "Game", instance_id: "game-source", project_root: "C:/u/game", canonical_project_root: "c:/u/game", aliases: [], unity_version: "6000.6.2f1", unity_pid: 12345, unity_process_start_utc: "2026-10-07T01:00:00.000Z", package_version: packageVersion, bridge_host: "127.0.0.1", bridge_port: 1, token: "synthetic-test-token", status: "ready", commands_hash: "hash-1", catalog_generation: 1, last_heartbeat: new Date().toISOString(), ...patch };
}

function allowingPolicy(): PolicyConfig {
  return { schema_version: 1, defaults: { read: "allow", write: "allow", execute: "deny", destructive: "deny", network: "deny" }, trusted_packages: [], untrusted_package_default: "ask", projects: { game: { allow: [refresh, waitReady], ask: [], deny: [] } } };
}

function verifiedBarrier(request: BridgeInvokeRequest, source: string, replacement: string): Record<string, unknown> {
  const at = (offset: number) => new Date(Date.now() - 100 + offset).toISOString();
  return { state: "verified", required: request.args._require_explicit_compile_barrier === true, operation_id: request.args._compile_barrier_expectation_id, requested: true, request_issued: true, started: true, finished: true, source_instance_id: source, reload_source_instance_id: source, replacement_instance_id: replacement, terminal_compile_result: "clean", terminal_compile_error_count: 0, terminal_compile_failure_without_reload: false, reload_started: true, post_reload_observed: true, requested_utc: at(0), request_issued_utc: at(10), started_utc: at(20), finished_utc: at(30), terminal_compile_observed_utc: at(30), reload_started_utc: at(35), post_reload_observed_utc: at(40), observed_generation: 1, request_generation: 1, started_generation: 1, finished_generation: 1 };
}
