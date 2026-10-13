import http from "node:http";
import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { Worker } from "node:worker_threads";

import { afterEach, describe, expect, it, vi } from "vitest";

import { BridgeRequestError, HttpUnityBridgeClient } from "../src/bridge/bridge-client.js";
import type { HeartbeatRecord } from "../src/registry/project-registry.js";
import { OperationalLogger } from "../src/observability/operational-logger.js";
import { TestRunOperationStorePublisher } from "../src/operations/test-run-operation-publisher.js";
import { TestRunOperationStore } from "../src/operations/test-run-operation-store.js";
import { acquireStorageLock } from "../src/security/safe-storage.js";
import { ownedTestRunOperationBinding } from "./helpers/test-run-operation-publisher.js";

const servers: http.Server[] = [];
const wireWorkers: Worker[] = [];
const wireRoots: string[] = [];

afterEach(async () => {
  vi.restoreAllMocks();
  await Promise.all(
    servers.splice(0).map(
      (server) =>
        new Promise<void>((resolve, reject) => {
          server.close((error) => (error ? reject(error) : resolve()));
        }),
    ),
  );
  await Promise.all(wireWorkers.splice(0).map((worker) => worker.terminate()));
  for (const root of wireRoots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe("HttpUnityBridgeClient", () => {
  it.each(["cold", "warm"])("keeps %s real-wire transmission and response handling live while the actual issuance worker waits for a storage lock", async (temperature) => {
    const counters = new Int32Array(new SharedArrayBuffer(12));
    const receiver = new Worker(new URL("./fixtures/http-bridge-wire-worker.mjs", import.meta.url), {
      workerData: { role: "owned-http-bridge-fixture", counters: counters.buffer }, execArgv: [], env: {},
    });
    wireWorkers.push(receiver);
    const port = await new Promise<number>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("Owned HTTP fixture did not start")), 2000);
      receiver.once("error", (error) => { clearTimeout(timer); reject(error); });
      receiver.once("message", (message: unknown) => {
        clearTimeout(timer);
        if (!message || typeof message !== "object" || !("port" in message) || typeof message.port !== "number") reject(new Error("Invalid owned HTTP fixture port"));
        else resolve(message.port);
      });
    });
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-wire-publication-"));
    wireRoots.push(root);
    const store = new TestRunOperationStore(path.join(root, "operations"));
    const publisher = new TestRunOperationStorePublisher({ storeDir: store.dir });
    const binding = ownedTestRunOperationBinding(root);
    const lease = await publisher.prepare(binding, { deadlineUnixMs: Date.now() + 5000, beforeCommit: () => true });
    const client = new HttpUnityBridgeClient();
    let issuance: Promise<unknown> | undefined;
    let publicationSettled = false;
    let lock: ReturnType<typeof acquireStorageLock> | undefined;
    try {
      if (temperature === "warm") await client.invoke(heartbeat(port), { project: "game", command: "compile.status", args: {} });
      // Hold only this freshly owned store lock before fetch. The real publisher
      // worker must wait; the independent receiver and caller must remain live.
      lock = acquireStorageLock(store.dir, ".store.lock");
      const result = await client.invoke(heartbeat(port), { project: "game", command: "test.run", args: { _test_run_operation_id: binding.operation_id } }, {
        deadlineUnixMs: Date.now() + 2000,
        onRequestIssued: () => {
          issuance = lease.publishRequestIssuance(new Date());
          void issuance.then(() => { publicationSettled = true; }, () => { publicationSettled = true; });
        },
      });
      expect(result).toMatchObject({ status: "ok", data: { acknowledged: true } });
      expect(Atomics.load(counters, 0)).toBe(temperature === "warm" ? 2 : 1);
      expect(Atomics.load(counters, 1)).toBeGreaterThan(0);
      expect(Atomics.load(counters, 2)).toBe(temperature === "warm" ? 2 : 1);
      expect(publicationSettled).toBe(false);
      expect(store.lookupRequestIssuance(binding)).toEqual({ status: "reserved" });
      lock.release();
      lock = undefined;
      await issuance;
      expect(store.lookupRequestIssuance(binding)).toMatchObject({ status: "found", marker: { operation_id: binding.operation_id } });
      expect(fs.readdirSync(store.dir).filter((name) => name.endsWith(".terminal"))).toEqual([]);
    } finally {
      lock?.release();
      if (issuance) await issuance.catch(() => undefined);
      lease.release();
      await publisher.close();
    }
  });

  it("rechecks the absolute budget after a synchronous pre-fetch guard without waiting for its timer", async () => {
    let now = 1000;
    vi.spyOn(Date, "now").mockImplementation(() => now);
    const fetchSpy = vi.spyOn(globalThis, "fetch");
    const issued = vi.fn();
    await expect(new HttpUnityBridgeClient().invoke(heartbeat(18142), { project: "game", command: "compile.status", args: {} }, {
      timeoutMs: 100, deadlineUnixMs: 1100,
      beforeRequestIssued: () => { now = 1101; }, onRequestIssued: issued,
    })).rejects.toMatchObject({ message: "bridge_timeout", details: { bridge_request_issued: false, failure_kind: "timeout" } });
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(issued).not.toHaveBeenCalled();
  });

  it("does not fetch when the caller cancels during the pre-fetch guard", async () => {
    const controller = new AbortController();
    const fetchSpy = vi.spyOn(globalThis, "fetch");
    const issued = vi.fn();
    await expect(new HttpUnityBridgeClient().invoke(heartbeat(18142), { project: "game", command: "compile.status", args: {} }, {
      signal: controller.signal, beforeRequestIssued: () => controller.abort(), onRequestIssued: issued,
    })).rejects.toMatchObject({ message: "bridge_timeout", details: { bridge_request_issued: false, abort_source: "caller" } });
    expect(fetchSpy).not.toHaveBeenCalled();
    expect(issued).not.toHaveBeenCalled();
  });

  it("does not accept a received response past the original absolute budget merely because its timer has not run", async () => {
    let now = 1000;
    vi.spyOn(Date, "now").mockImplementation(() => now);
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockResolvedValue(new Response(JSON.stringify({ schema_version: 1, status: "ok", data: {} }), { headers: { "content-type": "application/json" } }));
    const issued = vi.fn(() => { now = 1101; });
    await expect(new HttpUnityBridgeClient().invoke(heartbeat(18142), { project: "game", command: "compile.status", args: {} }, {
      timeoutMs: 100, deadlineUnixMs: 1100, onRequestIssued: issued,
    })).rejects.toMatchObject({ message: "bridge_timeout", details: { bridge_request_issued: true, failure_kind: "timeout" } });
    expect(fetchSpy).toHaveBeenCalledTimes(1);
    expect(issued).toHaveBeenCalledTimes(1);
  });

  it("never logs an identifier-safe client protocol request_id on bridge failure", async () => {
    const secretRequestId = "clientSecretRequestId123";
    const stderr = vi.spyOn(process.stderr, "write").mockImplementation(() => true);
    const client = new HttpUnityBridgeClient(new OperationalLogger({ level: "debug" }));

    await expect(client.invoke(heartbeat(0), {
      project: "game",
      command: "compile.status",
      args: {},
      request_id: secretRequestId,
    }, { requestId: secretRequestId })).rejects.toMatchObject({ message: "bridge_unreachable" });

    const output = stderr.mock.calls.map(([line]) => String(line)).join("");
    expect(output).toContain('"event_code":"SGG-OP-BRIDGE-FAILURE-001"');
    expect(output).not.toContain(secretRequestId);
    expect(output).not.toContain("request_id");
    expect(output).not.toContain("correlation_id");
  });

  it("sends bridge token and reads command catalog", async () => {
    const { record, seenAccepts, seenTokens } = await startBridgeServer();
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands")).resolves.toMatchObject({
      project: "game",
      commands: [{ id: "compile.status" }],
    });
    expect(seenTokens).toEqual(["local-token"]);
    expect(seenAccepts).toEqual(["application/json"]);
  });

  it("supports command:<id> selector through catalog lookup", async () => {
    const { record } = await startBridgeServer();
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "command:compile.status")).resolves.toMatchObject({
      project: "game",
      command: { id: "compile.status" },
    });
    await expect(client.describe(record, "command:missing.command")).resolves.toEqual({ schema_version: 1, project: "game", commands_hash: "hash", descriptor_revision: null, command: null });
  });

  it("reads the command catalog for grouped command selectors", async () => {
    const { record } = await startBridgeServer();
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands:scene")).resolves.toMatchObject({
      project: "game",
      commands: [{ id: "compile.status" }],
    });
  });

  it.each(["commands", "command:compile.status"])("rejects malformed descriptors for %s", async (selector) => {
    const { record } = await startBridgeServer({ commandsResponse: commandCatalog({ commands: [{ ...commandDescriptor(), risk: "secret-risk" }] }) });
    const error = await new HttpUnityBridgeClient().describe(record, selector).catch((cause: unknown) => cause) as BridgeRequestError;
    expect(error).toMatchObject({ message: "bridge_protocol_error", details: { protocol_reason: "schema_validation_failed", protocol_issues: [{ path: ["commands", 0, "risk"], code: "invalid_value" }] } });
    expect(JSON.stringify(error)).not.toContain("secret-risk");
  });

  it("maps protocol version mismatch separately without retrying or exposing bodies", async () => {
    let requests = 0;
    const { record } = await startRawServer((_request, response) => {
      requests += 1;
      sendJson(response, { schema_version: "private-unsupported-version", token: "never-leak" });
    });
    const error = await new HttpUnityBridgeClient().describe(record, "status").catch((cause: unknown) => cause) as BridgeRequestError;
    expect(error).toMatchObject({ message: "protocol_version_mismatch", details: { protocol_schema: "sgg.unity_mcp.bridge.health.v1", protocol_reason: "unsupported_schema_version", expected_protocol_version: 1, received_protocol_version: "private-unsupported-version" } });
    expect(requests).toBe(1);
    expect(JSON.stringify(error)).not.toContain("never-leak");
    expect(error.details).not.toHaveProperty("response_body_sha256");
  });

  it.each([
    ["health project", { healthResponse: { ...healthResponse(), project: "other" } }, "project_mismatch"],
    ["health instance", { healthResponse: { ...healthResponse(), instance_id: "other" } }, "instance_id_mismatch"],
    ["catalog project", { commandsResponse: commandCatalog({ project: "other" }) }, "project_mismatch"],
  ] as const)("rejects %s identity mismatch", async (_label, options, reason) => {
    const { record } = await startBridgeServer(options);
    const selector = "commandsResponse" in options ? "commands" : "status";
    await expect(new HttpUnityBridgeClient().describe(record, selector)).rejects.toMatchObject({ message: "bridge_protocol_error", details: { protocol_reason: reason } });
  });

  it("allows heartbeat commands_hash lag while exposing stable Gateway revisions", async () => {
    const { record } = await startBridgeServer();
    record.commands_hash = "lagging-heartbeat-hash";
    const client = new HttpUnityBridgeClient();
    const first = await client.describe(record, "commands") as { commands_hash: string; catalog_revision: string; commands: Array<{ descriptor_revision: string }> };
    const second = await client.describe(record, "commands") as typeof first;
    expect(first.commands_hash).toBe("hash");
    expect(first.catalog_revision).toMatch(/^sha256:/);
    expect(first.commands[0]?.descriptor_revision).toMatch(/^sha256:/);
    expect(second.catalog_revision).toBe(first.catalog_revision);
  });

  it("requires a generation-bound catalog when the heartbeat publishes one", async () => {
    const { record } = await startBridgeServer({ commandsResponse: commandCatalog({ instance_id: "game-a", catalog_generation: 7 }) });
    record.catalog_status = "ready";
    record.catalog_generation = 7;
    await expect(new HttpUnityBridgeClient().describe(record, "commands")).resolves.toMatchObject({ instance_id: "game-a", catalog_generation: 7, commands_hash: "hash" });

    const wrongGeneration = await startBridgeServer({ commandsResponse: commandCatalog({ instance_id: "game-a", catalog_generation: 8 }) });
    wrongGeneration.record.catalog_status = "ready";
    wrongGeneration.record.catalog_generation = 7;
    await expect(new HttpUnityBridgeClient().describe(wrongGeneration.record, "commands")).rejects.toMatchObject({ message: "bridge_protocol_error", details: { protocol_reason: "catalog_generation_mismatch" } });

    const wrongHash = await startBridgeServer({ commandsResponse: commandCatalog({ instance_id: "game-a", catalog_generation: 7, commands_hash: "other" }) });
    wrongHash.record.catalog_status = "ready";
    wrongHash.record.catalog_generation = 7;
    await expect(new HttpUnityBridgeClient().describe(wrongHash.record, "commands")).rejects.toMatchObject({ message: "bridge_protocol_error", details: { protocol_reason: "commands_hash_mismatch" } });
  });

  it("parses versioned C# log entries and strips unknown fields", async () => {
    const buffer = { epoch: "a".repeat(32), initialized_at_utc: "2026-08-27T12:34:56.0000000Z", reset_loss: true, history_complete: false, secret: "drop" };
    const { record } = await startBridgeServer({ logsResponse: { schema_version: 1, entries: [{ timestamp: "2026-07-11T12:00:00.0000000Z", type: "Warning", message: "warning", stack_trace: "trace", secret: "drop" }], buffer, secret: "drop" } });
    await expect(new HttpUnityBridgeClient().logs(record, "console", 10)).resolves.toEqual({ schema_version: 1, entries: [{ timestamp: "2026-07-11T12:00:00.0000000Z", type: "Warning", message: "warning", stack_trace: "trace" }], buffer: { epoch: "a".repeat(32), initialized_at_utc: "2026-08-27T12:34:56.0000000Z", reset_loss: true, history_complete: false } });
  });

  it("posts invoke requests", async () => {
    const { record, invokeBodies } = await startBridgeServer();
    const client = new HttpUnityBridgeClient();

    await expect(
      client.invoke(record, {
        request_id: "req-1",
        project: "game",
        command: "compile.status",
        args: {},
        settings: { asset_index_cache_dir: "C:/cache/game" },
        expected_instance_id: "game-a",
        expected_catalog_generation: 7,
        expected_commands_hash: "hash",
      }),
    ).resolves.toEqual({ schema_version: 1, request_id: "req-1", status: "ok", data: { refreshed: true }, diagnostics: [], duration_ms: 0 });

    expect(invokeBodies).toMatchObject([{
      request_id: "req-1",
      project: "game",
      command: "compile.status",
      args: {},
      settings: { asset_index_cache_dir: "C:/cache/game" },
      expected_instance_id: "game-a",
      expected_catalog_generation: 7,
      expected_commands_hash: "hash",
    }]);
  });

  it("fires request-issued only after fetch has been initiated", async () => {
    const order: string[] = [];
    const fetch = vi.spyOn(globalThis, "fetch").mockImplementation(async () => {
      order.push("fetch");
      return new Response(JSON.stringify(completeInvoke({ status: "ok", data: {} })), { status: 200, headers: { "Content-Type": "application/json" } });
    });

    await new HttpUnityBridgeClient().invoke(heartbeat(18142), { project: "game", command: "compile.status", args: {} }, {
      beforeRequestIssued: () => { order.push("before"); },
      onRequestIssued: () => { order.push("issued"); },
    });

    expect(fetch).toHaveBeenCalledOnce();
    expect(order).toEqual(["before", "fetch", "issued"]);
  });

  it("fires request-issued after fetch initiation even when fetch rejects", async () => {
    const order: string[] = [];
    vi.spyOn(globalThis, "fetch").mockImplementation(() => {
      order.push("fetch");
      return Promise.reject(new TypeError("connection failed"));
    });

    await expect(new HttpUnityBridgeClient().invoke(heartbeat(18142), { project: "game", command: "compile.status", args: {} }, {
      beforeRequestIssued: () => { order.push("before"); },
      onRequestIssued: () => { order.push("issued"); },
    })).rejects.toMatchObject({ details: { bridge_request_issued: true } });

    expect(order).toEqual(["before", "fetch", "issued"]);
  });

  it("handles a rejected fetch promise when request-issued publication throws", async () => {
    const order: string[] = [];
    vi.spyOn(globalThis, "fetch").mockImplementation(() => {
      order.push("fetch");
      return Promise.reject(new TypeError("connection failed"));
    });

    await expect(new HttpUnityBridgeClient().invoke(heartbeat(18142), { project: "game", command: "compile.status", args: {} }, {
      beforeRequestIssued: () => { order.push("before"); },
      onRequestIssued: () => { order.push("issued"); throw new Error("issuance publication failed"); },
    })).rejects.toMatchObject({ details: { bridge_request_issued: true } });
    await new Promise<void>((resolve) => setImmediate(resolve));

    expect(order).toEqual(["before", "fetch", "issued"]);
  });

  it.each([
    ["request id", { request_id: "unsafe id", project: "game", command: "compile.status", args: {} }],
    ["project", { project: "", command: "compile.status", args: {} }],
    ["command", { project: "game", command: "", args: {} }],
    ["args", { project: "game", command: "compile.status", args: [] }],
    ["risk", { project: "game", command: "compile.status", args: {}, risk: "unknown" }],
    ["deadline", { project: "game", command: "compile.status", args: {}, deadline_unix_ms: Number.POSITIVE_INFINITY }],
    ["settings", { project: "game", command: "compile.status", args: {}, settings: { feature_flags: Object.fromEntries(Array.from({ length: 257 }, (_, index) => [`f${index}`, true])) } }],
  ])("rejects invalid outgoing invoke %s before making a POST", async (_label, request) => {
    let posts = 0;
    const { record } = await startRawServer((incoming, response) => {
      posts += incoming.method === "POST" ? 1 : 0;
      sendJson(response, completeInvoke({ status: "ok", data: {} }));
    });

    await expect(new HttpUnityBridgeClient().invoke(record, request as never)).rejects.toBeInstanceOf(BridgeRequestError);
    expect(posts).toBe(0);
  });

  it("rejects invoke response version and request-id mismatches as typed protocol errors", async () => {
    const version = await startBridgeServer({ invokeResponse: { schema_version: 2, status: "ok", data: {} } });
    await expect(new HttpUnityBridgeClient().invoke(version.record, { project: "game", command: "compile.status", args: {} })).rejects.toMatchObject({ message: "protocol_version_mismatch" });

    const correlation = await startBridgeServer({ invokeResponse: { request_id: "other", status: "ok", data: {} } });
    await expect(new HttpUnityBridgeClient().invoke(correlation.record, { request_id: "expected", project: "game", command: "compile.status", args: {} })).rejects.toMatchObject({ message: "bridge_protocol_error", details: { protocol_reason: "request_id_mismatch" } });
  });

  it("accepts stable bridge error responses from invoke", async () => {
    const { record } = await startBridgeServer({ invokeResponse: { status: "error", error: { code: "absolute_path_rejected", message: "absolute_path_rejected" } } });
    const client = new HttpUnityBridgeClient();

    await expect(
      client.invoke(record, {
        project: "game",
        command: "asset.import",
        args: { asset_path: "C:/outside.txt" },
      }),
    ).resolves.toEqual({ schema_version: 1, status: "error", error: { code: "absolute_path_rejected", message: "absolute_path_rejected" }, diagnostics: [], duration_ms: 0 });
  });

  it("normalizes truncated bridge JSON responses with bounded body diagnostics", async () => {
    const { record } = await startBridgeServer({ commandsRawResponse: '{"schema_version":1,"project":"game","commands":[' });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands")).rejects.toThrow("bridge_protocol_error");
    try {
      await client.describe(record, "commands");
      throw new Error("expected describe to fail");
    } catch (error) {
      expect(error).toBeInstanceOf(BridgeRequestError);
      expect(error).toMatchObject({
        message: "bridge_protocol_error",
        details: {
          endpoint: "/commands",
          bridge_host: "127.0.0.1",
          bridge_port: record.bridge_port,
          http_status: 200,
          response_content_type: "application/json",
          response_body_bytes: 49,
          response_body_empty: false,
          response_body_truncated: false,
          response_body_sha256: sha256('{"schema_version":1,"project":"game","commands":['),
          response_body_max_bytes: 4 * 1024 * 1024,
          failure_kind: "response_parse",
          parse_error_kind: "truncated_json",
          cause_class: "SyntaxError",
        },
      });
    }
  });

  it("normalizes empty invoke responses as bridge protocol errors", async () => {
    const { record } = await startBridgeServer({ invokeRawResponse: "" });
    const lines: string[] = [];
    const client = new HttpUnityBridgeClient(new OperationalLogger({
      level: "debug",
      writer: { write: (line) => { lines.push(line); return true; }, once: () => undefined },
    }));

    await expect(
      client.invoke(record, {
        project: "game",
        command: "validation.after_edit",
        args: {},
      }),
    ).rejects.toThrow("bridge_protocol_error");
    try {
      await client.invoke(record, {
        project: "game",
        command: "validation.after_edit",
        args: {},
      });
      throw new Error("expected invoke to fail");
    } catch (error) {
      expect(error).toBeInstanceOf(BridgeRequestError);
      expect(error).toMatchObject({
        message: "bridge_protocol_error",
        details: {
          endpoint: "/invoke",
          bridge_host: "127.0.0.1",
          bridge_port: record.bridge_port,
          http_status: 200,
          response_body_bytes: 0,
          response_body_empty: true,
          response_body_truncated: false,
          response_body_sha256: sha256(""),
          parse_error_kind: "empty_body",
        },
      });
    }
    const events = lines.map((line) => JSON.parse(line)).filter((event) => event.event_code === "SGG-OP-BRIDGE-RESPONSE-001");
    expect(events).toEqual([
      expect.objectContaining({ operation: "invoke", state: "empty", http_status: 200, response_body_bytes: 0 }),
      expect.objectContaining({ operation: "invoke", state: "empty", http_status: 200, response_body_bytes: 0 }),
    ]);
    expect(JSON.stringify(events)).not.toContain("validation.after_edit");
  });

  it("normalizes zero-byte invoke responses without Content-Type as empty bodies", async () => {
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(200);
      response.end();
    });

    const error = await new HttpUnityBridgeClient().invoke(record, {
      project: "game",
      command: "compile.refresh_and_wait",
      args: {},
    }).catch((cause: unknown) => cause);

    expect(error).toMatchObject({
      message: "bridge_protocol_error",
      details: {
        endpoint: "/invoke",
        http_status: 200,
        failure_kind: "response_parse",
        parse_error_kind: "empty_body",
        response_body_bytes: 0,
        response_body_empty: true,
        response_body_truncated: false,
      },
    });
    expect((error as BridgeRequestError).details).not.toHaveProperty("response_content_type");
  });

  it("normalizes bridge response shape mismatches", async () => {
    const { record } = await startBridgeServer({ invokeResponse: { status: "ok" } });
    const client = new HttpUnityBridgeClient();

    await expect(
      client.invoke(record, {
        project: "game",
        command: "compile.status",
        args: {},
      }),
    ).rejects.toThrow("bridge_protocol_error");
    try {
      await client.invoke(record, {
        project: "game",
        command: "compile.status",
        args: {},
      });
      throw new Error("expected invoke to fail");
    } catch (error) {
      expect(error).toBeInstanceOf(BridgeRequestError);
      expect(error).toMatchObject({
        message: "bridge_protocol_error",
        details: {
          endpoint: "/invoke",
          bridge_host: "127.0.0.1",
          bridge_port: record.bridge_port,
          parse_error_kind: "response_shape_mismatch",
          failure_kind: "response_shape",
          cause_class: "ResponseShapeMismatch",
        },
      });
    }
  });

  it("normalizes unauthorized bridge responses", async () => {
    const { record } = await startBridgeServer({ expectedToken: "other-token" });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands")).rejects.toThrow("invalid_token");
    try {
      await client.describe(record, "commands");
      throw new Error("expected describe to fail");
    } catch (error) {
      expect(error).toBeInstanceOf(BridgeRequestError);
      expect(error).toMatchObject({
        message: "invalid_token",
        details: {
          endpoint: "/commands",
          bridge_host: "127.0.0.1",
          bridge_port: record.bridge_port,
          http_status: 401,
          failure_kind: "authentication",
          http_error_kind: "authentication",
        },
      });
      expect(JSON.stringify(error)).not.toContain("local-token");
    }
  });

  it("normalizes bridge network failures", async () => {
    const client = new HttpUnityBridgeClient();

    const record = heartbeat(1);
    await expect(client.describe(record, "commands")).rejects.toThrow("bridge_unreachable");
    try {
      await client.describe(record, "commands");
      throw new Error("expected describe to fail");
    } catch (error) {
      expect(error).toBeInstanceOf(BridgeRequestError);
      expect(error).toMatchObject({
        message: "bridge_unreachable",
        details: {
          endpoint: "/commands",
          bridge_host: "127.0.0.1",
          bridge_port: 1,
          failure_kind: "connection",
          cause_class: expect.any(String),
        },
      });
    }
  });

  it("rejects transitional heartbeats without a listener port before network fetch", async () => {
    const client = new HttpUnityBridgeClient();
    const record = heartbeat(0, { status: "waiting_for_idle" });

    await expect(client.describe(record, "commands")).rejects.toThrow("bridge_unreachable");
    try {
      await client.describe(record, "commands");
      throw new Error("expected describe to fail");
    } catch (error) {
      expect(error).toBeInstanceOf(BridgeRequestError);
      expect(error).toMatchObject({
        message: "bridge_unreachable",
        details: {
          endpoint: "/commands",
          bridge_host: "127.0.0.1",
          bridge_port: 0,
          bridge_request_issued: false,
          bridge_endpoint_available: false,
          lifecycle_status: "waiting_for_idle",
          cause_class: "BridgeLifecycleUnavailable",
        },
      });
    }
  });

  it("times out while waiting for response headers", async () => {
    const { record } = await startRawServer(() => undefined);
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands", { timeoutMs: 30 })).rejects.toMatchObject({
      message: "bridge_timeout",
      details: { abort_source: "transport_timeout", transport_timeout_ms: 30, bridge_request_issued: true },
    });
  });

  it("times out while reading a stalled response body", async () => {
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.write('{"commands":[');
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands", { timeoutMs: 30 })).rejects.toMatchObject({
      message: "bridge_timeout",
      details: { abort_source: "transport_timeout" },
    });
  });

  it("rejects an expired invoke deadline without making a network request", async () => {
    let requests = 0;
    const { record } = await startRawServer(() => {
      requests += 1;
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.invoke(record, { project: "game", command: "compile.status", args: {}, deadline_unix_ms: Date.now() - 1 })).rejects.toMatchObject({
      message: "bridge_timeout",
      details: { abort_source: "deadline", bridge_request_issued: false },
    });
    expect(requests).toBe(0);
  });

  it.each([
    { elapsedMs: 40_000, expired: false },
    { elapsedMs: 60_000, expired: true },
    { elapsedMs: 60_001, expired: true },
  ])("enforces the later invoke deadline after $elapsedMs ms without a compressed network timer", async ({ elapsedMs, expired }) => {
    const started = Date.now();
    let clock = started;
    let receivedDeadline: unknown;
    const { record } = await startRawServer(async (request, response) => {
      receivedDeadline = JSON.parse(await readBody(request)).deadline_unix_ms;
      // Advance the logical clock only after the real HTTP request arrives. The
      // original 30s bound would reject the 40s response; the later 60s bound must
      // accept it, then reject equality and expiry without a 20ms scheduler race.
      clock = started + elapsedMs;
      sendJson(response, completeInvoke({ status: "ok", data: { completed: true } }));
    });
    const client = new HttpUnityBridgeClient();
    const date = vi.spyOn(Date, "now").mockImplementation(() => clock);
    // Observe, but do not replace, the actual native timeout or cancellation path.
    const timer = vi.spyOn(globalThis, "setTimeout");

    try {
      const result = client.invoke(record, {
        project: "game",
        command: "compile.status",
        args: {},
        deadline_unix_ms: started + 60_000,
      });
      if (expired) await expect(result).rejects.toMatchObject({ message: "bridge_timeout", details: { bridge_request_issued: true } });
      else await expect(result).resolves.toEqual({ schema_version: 1, status: "ok", data: { completed: true }, diagnostics: [], duration_ms: 0 });
      expect(timer.mock.calls[0]?.[1]).toBe(60_000);
      expect(receivedDeadline).toBe(started + 60_000);
    } finally {
      timer.mockRestore();
      date.mockRestore();
    }
  });

  it("keeps the 30 second invoke default and caps deadline-derived endpoint timeouts at one hour", async () => {
    const client = new HttpUnityBridgeClient();
    const noDeadlineAbort = new AbortController();
    noDeadlineAbort.abort();
    await expect(client.invoke(heartbeat(1), { project: "game", command: "compile.status", args: {} }, { signal: noDeadlineAbort.signal })).rejects.toMatchObject({
      details: { abort_source: "caller", endpoint_timeout_ms: 30_000 },
    });

    const longDeadline = Date.now() + 2 * 60 * 60 * 1_000;
    const cappedAbort = new AbortController();
    cappedAbort.abort();
    await expect(client.invoke(heartbeat(1), { project: "game", command: "compile.status", args: {}, deadline_unix_ms: longDeadline }, { signal: cappedAbort.signal })).rejects.toMatchObject({
      details: { abort_source: "caller", endpoint_timeout_ms: 60 * 60 * 1_000, deadline_unix_ms: longDeadline },
    });
  });

  it("lets an earlier explicit invoke transport timeout beat a later request deadline", async () => {
    const { record } = await startRawServer(() => undefined);
    const client = new HttpUnityBridgeClient();

    await expect(client.invoke(record, {
      request_id: "transport-timeout-1",
      project: "game",
      command: "compile.status",
      args: {},
      deadline_unix_ms: Date.now() + 2_000,
    }, { timeoutMs: 25 })).rejects.toMatchObject({
      details: { abort_source: "transport_timeout", transport_timeout_ms: 25 },
    });
  });

  it("sends the same earlier deadline in the invoke payload and transport", async () => {
    let body: Record<string, unknown> | undefined;
    const { record } = await startRawServer(async (request, response) => {
      body = JSON.parse(await readBody(request));
      sendJson(response, completeInvoke({ status: "ok", data: {} }));
    });
    const requestDeadline = Date.now() + 10_000;
    const transportDeadline = requestDeadline - 5_000;

    await new HttpUnityBridgeClient().invoke(record, {
      project: "game",
      command: "compile.status",
      args: {},
      deadline_unix_ms: requestDeadline,
    }, { deadlineUnixMs: transportDeadline });

    expect(body?.deadline_unix_ms).toBe(transportDeadline);
  });

  it("includes only bounded safe request-id metadata on transport timeouts", async () => {
    const { record } = await startRawServer(() => undefined);
    const client = new HttpUnityBridgeClient();

    await expect(client.invoke(record, { project: "game", command: "asset.import", args: { secret: "never-log-me" } }, {
      timeoutMs: 20,
      requestId: "safe-request-1",
    })).rejects.toMatchObject({ details: { request_id: "safe-request-1" } });
    const unsafe = await client.invoke(record, { project: "game", command: "asset.import", args: { secret: "never-log-me" } }, {
      timeoutMs: 20,
      requestId: `unsafe ${"x".repeat(200)}`,
    }).catch((error: unknown) => error) as BridgeRequestError;
    expect(unsafe.details).not.toHaveProperty("request_id");
    expect(JSON.stringify(unsafe)).not.toContain("never-log-me");
  });

  it.each(["timeout", "oversize", "redirect", "wrong_type", "malformed", "server_error"] as const)("sends exactly one invoke POST for %s failures", async (failure) => {
    let posts = 0;
    const { record } = await startRawServer(async (request, response) => {
      posts += request.method === "POST" && request.url === "/invoke" ? 1 : 0;
      await readBody(request);
      if (failure === "timeout") return;
      if (failure === "oversize") {
        response.writeHead(200, { "Content-Type": "application/json", "Content-Length": 4 * 1024 * 1024 + 1 });
        response.flushHeaders();
        return;
      }
      if (failure === "redirect") {
        response.writeHead(302, { Location: "/other" });
        response.end();
        return;
      }
      if (failure === "wrong_type") {
        sendRaw(response, "not json", "text/plain");
        return;
      }
      if (failure === "malformed") {
        sendRaw(response, '{"status":"ok"', "application/json");
        return;
      }
      response.writeHead(500, { "Content-Type": "application/json" });
      response.end('{"error":{"code":"bridge_failed"}}');
    });

    const error = await new HttpUnityBridgeClient().invoke(record, {
      project: "game",
      command: "asset.import",
      args: { secret: "single-post-secret" },
    }, { ...(failure === "timeout" ? { timeoutMs: 500 } : {}), requestId: "single-post-1" }).catch((cause: unknown) => cause);

    expect(error).toBeInstanceOf(BridgeRequestError);
    expect(error).toMatchObject({ details: { request_id: "single-post-1" } });
    expect(posts).toBe(1);
    expect(JSON.stringify(error)).not.toContain("single-post-secret");
  });

  it("uses the earlier transport timeout or deadline", async () => {
    const { record } = await startRawServer(() => undefined);
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands", { timeoutMs: 25, deadlineUnixMs: Date.now() + 2_000 })).rejects.toMatchObject({
      details: { abort_source: "transport_timeout" },
    });
    await expect(client.describe(record, "commands", { timeoutMs: 2_000, deadlineUnixMs: Date.now() + 25 })).rejects.toMatchObject({
      details: { abort_source: "deadline" },
    });
  });

  it("honors an external caller abort without exposing its reason", async () => {
    const { record } = await startRawServer(() => undefined);
    const client = new HttpUnityBridgeClient();
    const controller = new AbortController();
    const pending = client.describe(record, "commands", { signal: controller.signal });
    controller.abort("secret caller reason");

    await expect(pending).rejects.toMatchObject({ message: "bridge_timeout", details: { abort_source: "caller" } });
    await expect(pending).rejects.not.toSatisfy((error: unknown) => JSON.stringify(error).includes("secret caller reason"));
  });

  it("removes the caller abort listener after a completed response", async () => {
    const { record } = await startBridgeServer();
    const client = new HttpUnityBridgeClient();
    const controller = new AbortController();
    const removeEventListener = vi.spyOn(controller.signal, "removeEventListener");

    await client.describe(record, "commands", { timeoutMs: 1_000, signal: controller.signal });

    expect(removeEventListener).toHaveBeenCalledWith("abort", expect.any(Function));
  });

  it("rejects oversized fixed Content-Length before reading the body", async () => {
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json", "Content-Length": 4 * 1024 * 1024 + 1 });
      response.flushHeaders();
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands")).rejects.toMatchObject({
      message: "bridge_protocol_error",
      details: {
        failure_kind: "response_too_large",
        response_too_large: true,
        response_body_max_bytes: 4 * 1024 * 1024,
        response_content_length: 4 * 1024 * 1024 + 1,
        response_body_bytes: 0,
        response_body_truncated: true,
      },
    });
  });

  it("cancels a chunked response as soon as it exceeds the limit", async () => {
    let responseClosed = false;
    const { record } = await startRawServer((_request, response) => {
      response.on("close", () => {
        responseClosed = true;
      });
      response.writeHead(200, { "Content-Type": "application/json" });
      response.write(Buffer.alloc(4 * 1024 * 1024, 0x20));
      response.write(Buffer.from("x"));
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "commands")).rejects.toMatchObject({
      details: { response_too_large: true, response_body_max_bytes: 4 * 1024 * 1024, response_body_truncated: true },
    });
    await expect.poll(() => responseClosed).toBe(true);
  });

  it("accepts a valid response at the exact byte limit", async () => {
    const limit = 256 * 1024;
    const base = JSON.stringify({ ...healthResponse(), pad: "" });
    const payload = JSON.stringify({ ...healthResponse(), pad: "a".repeat(limit - Buffer.byteLength(base)) });
    expect(Buffer.byteLength(payload)).toBe(limit);
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json", "Content-Length": limit });
      response.end(payload);
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "status")).resolves.toEqual(healthResponse());
  });

  it("decodes UTF-8 safely when a character is split across chunks", async () => {
    const payload = Buffer.from(JSON.stringify({ ...healthResponse(), package_update: { ...healthResponse().package_update, message: "€" } }), "utf8");
    const splitAt = payload.indexOf(Buffer.from("€", "utf8")) + 1;
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(200, { "Content-Type": "application/json" });
      response.write(payload.subarray(0, splitAt));
      setImmediate(() => response.end(payload.subarray(splitAt)));
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "status")).resolves.toMatchObject({ package_update: { message: "€" } });
  });

  it("applies the health and four-megabyte limits to every bridge route", async () => {
    const seenPaths: string[] = [];
    const { record } = await startRawServer((request, response) => {
      seenPaths.push(request.url ?? "");
      const length = request.url === "/health" ? 256 * 1024 + 1 : 4 * 1024 * 1024 + 1;
      response.writeHead(200, { "Content-Type": "application/json", "Content-Length": length });
      response.flushHeaders();
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(record, "status")).rejects.toMatchObject({ details: { response_body_max_bytes: 256 * 1024 } });
    await expect(client.describe(record, "commands")).rejects.toMatchObject({ details: { response_body_max_bytes: 4 * 1024 * 1024 } });
    await expect(client.invoke(record, { project: "game", command: "compile.status", args: {} })).rejects.toMatchObject({ details: { response_body_max_bytes: 4 * 1024 * 1024 } });
    await expect(client.logs(record, "console", 10)).rejects.toMatchObject({ details: { response_body_max_bytes: 4 * 1024 * 1024 } });
    await expect(client.describe(record, "legacy-selector")).rejects.toMatchObject({ message: "selector_not_supported", details: { failure_kind: "selector_not_supported" } });
    expect(seenPaths).toEqual(["/health", "/commands", "/invoke", "/logs?kind=console&limit=10"]);
  });

  it("does not follow redirects or forward the bridge token", async () => {
    const destinationTokens: Array<string | undefined> = [];
    const destination = await startRawServer((request, response) => {
      destinationTokens.push(typeof request.headers["x-sgg-unity-mcp-bridge-token"] === "string" ? request.headers["x-sgg-unity-mcp-bridge-token"] : undefined);
      sendJson(response, { commands: [] });
    });
    const source = await startRawServer((_request, response) => {
      response.writeHead(302, { Location: `http://127.0.0.1:${destination.record.bridge_port}/commands` });
      response.end();
    });
    const client = new HttpUnityBridgeClient();

    await expect(client.describe(source.record, "commands")).rejects.toMatchObject({
      message: "bridge_protocol_error",
      details: { failure_kind: "redirect", http_error_kind: "redirect", http_status: 302 },
    });
    expect(destinationTokens).toEqual([]);
  });

  it("accepts JSON media types case-insensitively while ignoring parameters", async () => {
    for (const contentType of ["Application/JSON; Charset=UTF-8", "application/problem+json; charset=utf-8"]) {
      const { record } = await startRawServer((_request, response) => {
        response.writeHead(200, { "Content-Type": contentType });
        response.end(JSON.stringify({ ...healthResponse(), accepted: true }));
      });
      await expect(new HttpUnityBridgeClient().describe(record, "status")).resolves.toEqual(healthResponse());
    }
  });

  it.each([
    ["missing", undefined, "missing_content_type"],
    ["HTML", "text/html", "invalid_content_type"],
    ["wrong application type", "application/xml", "invalid_content_type"],
  ])("rejects %s response media types", async (_label, contentType, parseErrorKind) => {
    const payload = '{"secret":"password-value"}';
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(200, contentType ? { "Content-Type": contentType } : {});
      response.end(payload);
    });
    const error = await new HttpUnityBridgeClient().describe(record, "status").catch((cause: unknown) => cause);
    expect(error).toMatchObject({
      message: "bridge_protocol_error",
      details: {
        failure_kind: "invalid_content_type",
        parse_error_kind: parseErrorKind,
        response_body_bytes: Buffer.byteLength(payload),
        response_body_sha256: sha256(payload),
      },
    });
    expect(JSON.stringify(error)).not.toContain("password-value");
  });

  it("records only byte and full-body hash evidence for malformed secret-bearing JSON", async () => {
    const payload = '{"token":"local-token","password":"hunter2"';
    const { record } = await startBridgeServer({ commandsRawResponse: payload });
    const error = await new HttpUnityBridgeClient().describe(record, "commands").catch((cause: unknown) => cause);
    expect(error).toMatchObject({
      details: {
        response_body_bytes: Buffer.byteLength(payload),
        response_body_sha256: sha256(payload),
        response_body_truncated: false,
      },
    });
    const serialized = JSON.stringify(error);
    expect(serialized).not.toContain("local-token");
    expect(serialized).not.toContain("hunter2");
    expect(serialized).not.toContain("response_body_prefix");
    expect(serialized).not.toContain("response_body_suffix");
    expect(serialized).not.toContain("cause_message");
  });

  it.each([401, 403])("classifies HTTP %i as invalid-token authentication", async (status) => {
    const payload = JSON.stringify({ error: { code: "invalid_token", message: "secret server message" } });
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(status, { "Content-Type": "application/json" });
      response.end(payload);
    });
    const error = await new HttpUnityBridgeClient().describe(record, "commands").catch((cause: unknown) => cause);
    expect(error).toMatchObject({
      message: "invalid_token",
      details: {
        failure_kind: "authentication",
        http_error_kind: "authentication",
        http_status: status,
        response_error_code: "invalid_token",
        response_body_sha256: sha256(payload),
      },
    });
    expect(JSON.stringify(error)).not.toContain("secret server message");
  });

  it("distinguishes client and server HTTP failures and exposes only validated machine codes", async () => {
    for (const [status, kind, code] of [[404, "client", "route_not_found"], [500, "server", "bridge_failed"]] as const) {
      const payload = JSON.stringify({ error: { code, message: "password=server-secret", details: { token: "reflected-token" } } });
      const { record } = await startRawServer((_request, response) => {
        response.writeHead(status, { "Content-Type": "application/json" });
        response.end(payload);
      });
      const error = await new HttpUnityBridgeClient().describe(record, "commands").catch((cause: unknown) => cause);
      expect(error).toMatchObject({
        message: "bridge_protocol_error",
        details: { failure_kind: "http_error", http_error_kind: kind, http_status: status, response_error_code: code, response_body_sha256: sha256(payload) },
      });
      expect(JSON.stringify(error)).not.toContain("server-secret");
      expect(JSON.stringify(error)).not.toContain("reflected-token");
    }
  });

  it("strips request query arguments from error endpoints", async () => {
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(404, { "Content-Type": "application/json" });
      response.end('{"error":{"code":"route_not_found"}}');
    });
    const error = await new HttpUnityBridgeClient().logs(record, "console", 37).catch((cause: unknown) => cause);
    expect(error).toMatchObject({ details: { endpoint: "/logs" } });
    expect(JSON.stringify(error)).not.toContain("kind=console");
    expect(JSON.stringify(error)).not.toContain("limit=37");
  });

  it("rejects unsafe or long JSON error codes", async () => {
    for (const [code, token] of [["NOT-SAFE", "local-token"], [`a${"b".repeat(64)}`, "local-token"], ["localtoken", "localtoken"]]) {
      const { record } = await startRawServer((_request, response) => {
        response.writeHead(500, { "Content-Type": "application/json" });
        response.end(JSON.stringify({ error: { code } }));
      });
      record.token = token;
      const error = await new HttpUnityBridgeClient().describe(record, "commands").catch((cause: unknown) => cause) as BridgeRequestError;
      expect(error.details).not.toHaveProperty("response_error_code");
    }
  });

  it("bounds oversized server-error bodies without publishing a prefix hash", async () => {
    const { record } = await startRawServer((_request, response) => {
      response.writeHead(500, { "Content-Type": "application/json", "Content-Length": 4 * 1024 * 1024 + 1 });
      response.flushHeaders();
    });
    const error = await new HttpUnityBridgeClient().describe(record, "commands").catch((cause: unknown) => cause) as BridgeRequestError;
    expect(error).toMatchObject({
      message: "bridge_protocol_error",
      details: { failure_kind: "response_too_large", http_error_kind: "server", response_too_large: true, response_body_truncated: true, response_body_max_bytes: 4 * 1024 * 1024 },
    });
    expect(error.details).not.toHaveProperty("response_body_sha256");
  });

  it("does not expose raw connection or abort messages", async () => {
    const connectionError = await new HttpUnityBridgeClient().describe(heartbeat(1), "commands").catch((cause: unknown) => cause) as BridgeRequestError;
    expect(connectionError.details).toMatchObject({ failure_kind: "connection", cause_class: expect.any(String) });
    expect(connectionError.details).not.toHaveProperty("cause_message");

    const controller = new AbortController();
    controller.abort("raw abort secret");
    const abortError = await new HttpUnityBridgeClient().describe(heartbeat(1), "commands", { signal: controller.signal }).catch((cause: unknown) => cause) as BridgeRequestError;
    expect(abortError).toMatchObject({ message: "bridge_timeout", details: { failure_kind: "timeout", abort_source: "caller" } });
    expect(JSON.stringify(abortError)).not.toContain("raw abort secret");
  });

  it("does not expose a reflected token or invoke request body in errors", async () => {
    const secretArgument = "secret-invoke-argument";
    const { record } = await startRawServer(async (request, response) => {
      if (request.url === "/commands") {
        sendRaw(response, `invalid local-token response`, "application/json");
        return;
      }
      sendRaw(response, await readBody(request), "application/json");
    });
    const client = new HttpUnityBridgeClient();

    const commandError = await client.describe(record, "commands").catch((error: unknown) => error);
    const invokeError = await client.invoke(record, { project: "game", command: "compile.status", args: { value: secretArgument } }).catch((error: unknown) => error);
    expect(JSON.stringify(commandError)).not.toContain("local-token");
    expect(JSON.stringify(invokeError)).not.toContain(secretArgument);
  });
});

async function startBridgeServer(
  options: {
    expectedToken?: string;
    commandsRawResponse?: string;
    commandsContentType?: string;
    commandsResponse?: unknown;
    healthResponse?: unknown;
    logsResponse?: unknown;
    invokeResponse?: unknown;
    invokeRawResponse?: string;
  } = {},
) {
  const expectedToken = options.expectedToken ?? "local-token";
  const seenTokens: string[] = [];
  const seenAccepts: Array<string | undefined> = [];
  const invokeBodies: unknown[] = [];
  const server = http.createServer(async (request, response) => {
    const token = request.headers["x-sgg-unity-mcp-bridge-token"];
    seenAccepts.push(typeof request.headers.accept === "string" ? request.headers.accept : undefined);
    if (typeof token === "string") {
      seenTokens.push(token);
    }

    if (token !== expectedToken) {
      response.writeHead(401, { "Content-Type": "application/json" });
      response.end(JSON.stringify({ status: "error" }));
      return;
    }

    if (request.url === "/health") {
      sendJson(response, options.healthResponse ?? healthResponse());
      return;
    }

    if (request.url === "/commands") {
      if (options.commandsRawResponse !== undefined) {
        sendRaw(response, options.commandsRawResponse, options.commandsContentType ?? "application/json");
        return;
      }

      sendJson(response, options.commandsResponse ?? commandCatalog());
      return;
    }

    if (request.url === "/invoke" && request.method === "POST") {
      const invokeBody = JSON.parse(await readBody(request));
      invokeBodies.push(invokeBody);
      if (options.invokeRawResponse !== undefined) {
        sendRaw(response, options.invokeRawResponse, "application/json");
        return;
      }

      sendJson(response, completeInvoke(options.invokeResponse ?? { status: "ok", data: { refreshed: true } }, invokeBody.request_id));
      return;
    }

    sendJson(response, options.logsResponse ?? { schema_version: 1, entries: [] });
  });

  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", () => resolve()));
  servers.push(server);
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("server did not bind to a TCP port");
  }

  return {
    record: heartbeat(address.port),
    seenTokens,
    seenAccepts,
    invokeBodies,
  };
}

async function startRawServer(listener: http.RequestListener): Promise<{ record: HeartbeatRecord }> {
  const server = http.createServer(listener);
  await new Promise<void>((resolve) => server.listen(0, "127.0.0.1", () => resolve()));
  servers.push(server);
  const address = server.address();
  if (!address || typeof address === "string") {
    throw new Error("server did not bind to a TCP port");
  }
  return { record: heartbeat(address.port) };
}

function sendJson(response: http.ServerResponse, payload: unknown): void {
  response.writeHead(200, { "Content-Type": "application/json" });
  response.end(JSON.stringify(payload));
}

function sendRaw(response: http.ServerResponse, payload: string, contentType: string): void {
  response.writeHead(200, { "Content-Type": contentType });
  response.end(payload);
}

function completeInvoke(payload: unknown, requestId?: unknown): unknown {
  return {
    schema_version: 1,
    ...(typeof requestId === "string" ? { request_id: requestId } : {}),
    ...(typeof payload === "object" && payload !== null ? payload : {}),
    diagnostics: [],
    duration_ms: 0,
  };
}

function sha256(value: string): string {
  return createHash("sha256").update(value).digest("hex");
}

function readBody(request: http.IncomingMessage): Promise<string> {
  return new Promise((resolve, reject) => {
    let body = "";
    request.setEncoding("utf8");
    request.on("data", (chunk) => {
      body += chunk;
    });
    request.on("end", () => resolve(body));
    request.on("error", reject);
  });
}

function heartbeat(port: number, overrides: Partial<HeartbeatRecord> = {}): HeartbeatRecord {
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
    bridge_port: port,
    token: "local-token",
    status: "ready",
    commands_hash: "hash",
    last_heartbeat: "2026-05-12T09:59:30.000Z",
    ...overrides,
  };
}

function healthResponse() {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    status: "ready",
    unity_version: "6000.0.0f1",
    commands_hash: "hash",
    is_compiling: false,
    is_updating: false,
    is_playing: false,
    ready: true,
    package_update: {
      status: "current",
      auto_refresh_enabled: false,
      refresh_pending: false,
      package_refresh_required: false,
      stale_package_resolution: false,
      fingerprint: "abc123",
      pending_fingerprint: "",
      refresh_count: 0,
      last_refresh_utc: "",
      message: "Package/runtime files are current.",
    },
    has_unsaved_scene_changes: false,
    dirty_scene_count: 0,
    dirty_scenes: [],
    queue_depth: 0,
  };
}

function commandDescriptor() {
  return { id: "compile.status", title: "Compile status", description: "Read compile status", package: "com.sungeargames.unity-mcp", risk: "read", idempotency: "safe", timeout_ms: 10_000, schema: { type: "object" } };
}

function commandCatalog(overrides: Record<string, unknown> = {}) {
  return { schema_version: 1, project: "game", commands_hash: "hash", commands: [commandDescriptor()], ...overrides };
}
