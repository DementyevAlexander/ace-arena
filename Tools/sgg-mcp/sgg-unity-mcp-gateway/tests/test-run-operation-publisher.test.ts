import { spawn } from "node:child_process";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { afterEach, describe, expect, it, vi } from "vitest";

import { CanonicalPathService } from "../src/paths/canonical-path.js";
import { TestRunOperationStorePublisher, TEST_RUN_OPERATION_PUBLISHER_MAX_LEASES } from "../src/operations/test-run-operation-publisher.js";
import {
  resolveTestRunOperationStoreLimits, TEST_RUN_ARTIFACT_CAPABILITY, TEST_RUN_ARTIFACT_ROOT, TEST_RUN_OPERATION_OWNER_CONTRACT,
  testRunCanonicalProjectRootHash, testRunOperationBindingHash, TestRunOperationStore,
  type TestRunOperationBinding, type TestRunOperationStoreLimits, type TestRunTerminalNoExecutionEvidence,
} from "../src/operations/test-run-operation-store.js";
import {
  assertTerminalEvidence, assertTestRunOperationWorkerData, assertTestRunOperationWorkerJob, assertTestRunOperationWorkerMessage, assertTestRunOperationWorkerLimits,
  assertTestRunPublicationAbortDiagnostics,
} from "../src/operations/test-run-operation-worker-protocol.js";

const roots: string[] = [];
const publishers: TestRunOperationStorePublisher[] = [];
const fixture = new URL("./fixtures/test-run-operation-worker/controlled-bootstrap.mjs", import.meta.url);
const fakeFixture = new URL("./fixtures/test-run-operation-worker/fake-bootstrap.mjs", import.meta.url);
let sequence = 0;

afterEach(async () => {
  vi.restoreAllMocks();
  vi.unstubAllEnvs();
  for (const root of roots) if (fs.existsSync(path.join(root, "operations", "blocked.signal"))) fs.writeFileSync(path.join(root, "operations", "continue.signal"), "continue", { mode: 0o600 });
  await Promise.all(publishers.splice(0).map(publisher => publisher.close()));
  for (const root of roots.splice(0)) fs.rmSync(root, { recursive: true, force: true });
});

describe("TestRunOperationStorePublisher", () => {
  it("validates the increased default and million-record ceiling without raising the 64-lease bound", () => {
    expect(resolveTestRunOperationStoreLimits()).toEqual({ maxBindingBytes: 16384, maxBindings: 192000, maxBytes: 209715200, retentionMs: 30 * 86400000 });
    const limits = resolveTestRunOperationStoreLimits({ maxBytes: 1048576, maxBindings: 1000000 });
    expect(() => assertTestRunOperationWorkerLimits(limits)).not.toThrow();
    expect(limits.maxBindings).toBe(1000000);
    expect(() => resolveTestRunOperationStoreLimits({ maxBindings: 1000001 })).toThrow();
    expect(() => assertTestRunOperationWorkerLimits({ ...limits, maxBindings: 1000001 })).toThrow();
    expect(TEST_RUN_OPERATION_PUBLISHER_MAX_LEASES).toBe(64);
  });

  it("uses the real fixed source bootstrap and preserves binding, reservation, captured issuance and terminal formats", async () => {
    const { root, dir, publisher } = setup();
    const binding = sampleBinding(root);
    let guardCalls = 0;
    const lease = await publisher.prepare(binding, { deadlineUnixMs: Date.now() + 10_000, beforeCommit: () => {
      guardCalls++;
      expect(fs.existsSync(path.join(dir, `${binding.operation_id}.reserve`))).toBe(true);
      expect(fs.existsSync(path.join(dir, `${binding.operation_id}.json`))).toBe(false);
      expect(fs.readdirSync(dir).some(name => name.startsWith(".publish-v1-"))).toBe(true);
      return true;
    } });
    const store = new TestRunOperationStore(dir);
    expect(guardCalls).toBe(1);
    expect(store.lookup(binding.operation_id)).toEqual({ status: "found", binding });
    expect(store.lookupRequestIssuance(binding)).toEqual({ status: "reserved" });
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.issued`))).toBe(false);
    await expect(lease.publishTerminalNoExecution({} as never, evidence())).rejects.toMatchObject({ reason: "terminal_outcome_invalid" });
    const captured = new Date(Date.parse(binding.created_at) + 17);
    const marker = await lease.publishRequestIssuance(captured);
    expect(marker.issued_at).toBe(captured.toISOString());
    expect(marker.owner_binding_hash).toBe(testRunOperationBindingHash(binding));
    expect(store.lookupRequestIssuance(binding)).toEqual({ status: "found", marker });
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.reserve`))).toBe(false);
    const receipt = await lease.publishTerminalNoExecution(marker, evidence());
    expect(store.lookupTerminalNoExecution(binding, marker)).toEqual({ status: "found", receipt });
    expect(receipt).toMatchObject({ state: "not_executed", operation_created: false, artifact_persisted: false, request_issuance_hash: expect.stringMatching(/^sha256:/) });
    expect(publisher.snapshotStatus()).toMatchObject({ state: "ok", bindings: 1 });
    lease.release();
  }, 15_000);

  it("preserves the legacy clock clamp but rejects invalid or contrary explicit captured timestamps", () => {
    const { root, dir } = setup();
    const binding = sampleBinding(root);
    const created = Date.parse(binding.created_at);
    const store = new TestRunOperationStore(dir, { now: () => new Date(created - 1000) });
    store.publish(binding);
    for (const date of [new Date(NaN), new Date(created - 1)]) expect(() => store.publishRequestIssuance(binding, date)).toThrowError(expect.objectContaining({ reason: "issuance_invalid" }));
    expect(store.lookupRequestIssuance(binding)).toEqual({ status: "reserved" });
    expect(store.publishRequestIssuance(binding).issued_at).toBe(binding.created_at);
    expect(Object.isFrozen(store.limits)).toBe(true);
  });

  it("captures cloned binding/date/evidence and prevents duplicate publication or retargeting", async () => {
    const { root, dir, publisher } = setup();
    const binding = sampleBinding(root);
    const original = structuredClone(binding);
    const prepared = publisher.prepare(binding, ready());
    binding.origin_unity_pid++;
    const lease = await prepared;
    const captured = new Date(Date.parse(original.created_at) + 5);
    const expected = captured.toISOString();
    const pending = lease.publishRequestIssuance(captured);
    captured.setTime(0);
    await expect(lease.publishRequestIssuance(new Date())).rejects.toMatchObject({ reason: "issuance_invalid" });
    const marker = await pending;
    expect(marker.issued_at).toBe(expected);
    expect(marker.origin_unity_pid).toBe(original.origin_unity_pid);
    await expect(lease.publishTerminalNoExecution({ ...marker, origin_unity_pid: 42 }, evidence())).rejects.toMatchObject({ reason: "issuance_invalid" });
    const refusal = evidence();
    const terminal = lease.publishTerminalNoExecution(marker, refusal);
    refusal.package_update.fingerprint = "changed after capture";
    const receipt = await terminal;
    expect(receipt.package_update.fingerprint).toBe("original");
    await expect(lease.publishTerminalNoExecution(marker, evidence())).rejects.toMatchObject({ reason: "terminal_outcome_invalid" });
    lease.release();
    await expect(lease.publishRequestIssuance(new Date())).rejects.toMatchObject({ reason: "publication_aborted" });
    expect(new TestRunOperationStore(dir).lookup(original.operation_id)).toEqual({ status: "found", binding: original });
  }, 15_000);

  it.each(["deny", "throw", "cross_deadline"])("honors the after-temp-fsync precommit guard: %s", async mode => {
    const { root, dir, publisher } = setup();
    const binding = sampleBinding(root);
    const deadline = Date.now() + 10_000;
    let called = false;
    const result = publisher.prepare(binding, { deadlineUnixMs: deadline, beforeCommit: () => {
      called = true;
      expect(fs.readdirSync(dir).some(name => name.startsWith(".publish-v1-"))).toBe(true);
      if (mode === "throw") throw new Error("private_token=fixture-secret");
      if (mode === "cross_deadline") vi.spyOn(Date, "now").mockReturnValue(deadline);
      return mode === "cross_deadline";
    } });
    await expect(result).rejects.toMatchObject({ reason: expect.stringMatching(/publication_aborted|storage_unavailable/) });
    vi.restoreAllMocks();
    expect(called).toBe(true);
    await waitUntil(() => !fs.existsSync(path.join(dir, ".store.lock")));
    expect(new TestRunOperationStore(dir).lookup(binding.operation_id)).toEqual({ status: "not_found" });
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.issued`))).toBe(false);
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.terminal`))).toBe(false);
  }, 15_000);

  it("rejects an expired preparation before boot/publication and never relaxes numeric limits", async () => {
    const { root, dir, publisher } = setup();
    await expect(publisher.prepare(sampleBinding(root), { deadlineUnixMs: Date.now() - 1, beforeCommit: () => true })).rejects.toMatchObject({ reason: "publication_aborted",
      publication: { stage: "prepare_admission", reason: "command_deadline_elapsed", guard_budget_ms: 0 } });
    expect(fs.existsSync(dir)).toBe(false);
    for (const value of [0, -1, NaN, 1.5, "2"]) expect(() => new TestRunOperationStorePublisher({ storeDir: dir, limits: { ...resolveTestRunOperationStoreLimits(), maxBindings: value } as TestRunOperationStoreLimits })).toThrow();
    expect(() => new TestRunOperationStorePublisher({ storeDir: dir, workerEntry: new URL("https://example.invalid/worker.mjs") })).toThrow();
  });

  it("bounds logical leases before dispatch, uses one worker, and release frees only unused logical capacity", async () => {
    const { root, dir, publisher } = setup(fakeFixture);
    expect(fs.existsSync(dir)).toBe(false);
    const leases = await Promise.all(Array.from({ length: TEST_RUN_OPERATION_PUBLISHER_MAX_LEASES }, () => publisher.prepare(sampleBinding(root), ready())));
    await expect(publisher.prepare(sampleBinding(root), ready())).rejects.toMatchObject({ reason: "capacity_exhausted" });
    leases[0]!.release();
    const extra = await publisher.prepare(sampleBinding(root), ready());
    extra.release();
    for (const lease of leases) lease.release();
    expect(lines(dir, "starts.signal")).toHaveLength(1);
    expect(lines(dir, "requests.signal")).toHaveLength(TEST_RUN_OPERATION_PUBLISHER_MAX_LEASES + 1);
    await publisher.close();
    await publisher.close();
  }, 15_000);

  it("bounds queued preparations by actual limits while one mutation is in flight", async () => {
    const limits = resolveTestRunOperationStoreLimits({ maxBindings: 2 });
    const { root, dir, publisher } = setup(fixture, limits);
    const first = publisher.prepare(sampleBinding(root, "b10c0001"), ready());
    const second = publisher.prepare(sampleBinding(root), ready());
    await expect(publisher.prepare(sampleBinding(root), ready())).rejects.toMatchObject({ reason: "capacity_exhausted" });
    await waitUntil(() => fs.existsSync(path.join(dir, "blocked.signal")));
    expect(lines(dir, "requests.signal")).toEqual(["prepare"]);
    fs.writeFileSync(path.join(dir, "continue.signal"), "continue", { mode: 0o600 });
    const leases = await Promise.all([first, second]);
    for (const lease of leases) lease.release();
    expect(lines(dir, "requests.signal")).toEqual(["prepare", "prepare"]);
  }, 15_000);

  it("refuses logical byte exhaustion before worker boot", async () => {
    const { root, dir } = setup();
    const binding = sampleBinding(root);
    const bytes = Buffer.byteLength(`${JSON.stringify(binding, null, 2)}\n`);
    const publisher = new TestRunOperationStorePublisher({ storeDir: dir, limits: resolveTestRunOperationStoreLimits({ maxBytes: bytes - 1 }) });
    publishers.push(publisher);
    await expect(publisher.prepare(binding, ready())).rejects.toMatchObject({ reason: "capacity_exhausted" });
    expect(fs.existsSync(dir)).toBe(false);
  });

  it("expires queued preparation without later publishing the expired binding", async () => {
    const { root, dir, publisher } = setup(fixture);
    const first = publisher.prepare(sampleBinding(root, "b10c0001"), ready());
    await waitUntil(() => fs.existsSync(path.join(dir, "blocked.signal")));
    const expiredBinding = sampleBinding(root);
    await expect(publisher.prepare(expiredBinding, { deadlineUnixMs: Date.now() + 20, beforeCommit: () => true })).rejects.toMatchObject({ reason: "publication_aborted",
      publication: { stage: "prepare_queue", reason: "command_deadline_elapsed", guard_budget_ms: 0 } });
    expect(lines(dir, "requests.signal")).toEqual(["prepare"]);
    fs.writeFileSync(path.join(dir, "continue.signal"), "continue", { mode: 0o600 });
    const lease = await first;
    lease.release();
    expect(new TestRunOperationStore(dir).lookup(expiredBinding.operation_id)).toEqual({ status: "not_found" });
  }, 15_000);

  it("detaches active preparation at its absolute deadline without claiming the binding was never published", async () => {
    const { root, dir, publisher } = setup(fixture);
    (await publisher.prepare(sampleBinding(root), ready())).release();
    const binding = sampleBinding(root, "b10c0001");
    let guardCalls = 0;
    const result = publisher.prepare(binding, { deadlineUnixMs: Date.now() + 100, beforeCommit: () => { guardCalls++; return true; } }).catch(error => error);
    await waitUntil(() => fs.existsSync(path.join(dir, "blocked.signal")));
    expect(await result).toMatchObject({ reason: "storage_unavailable" });
    fs.writeFileSync(path.join(dir, "continue.signal"), "continue", { mode: 0o600 });
    await publisher.close();
    expect(guardCalls).toBe(0);
    expect(new TestRunOperationStore(dir).lookup(binding.operation_id)).toEqual({ status: "not_found" });
  }, 15_000);

  it.each(["deny", "throw"])("distinguishes observed precommit refusal from storage failure: %s", async mode => {
    const { root, dir, publisher } = setup();
    const binding = sampleBinding(root);
    const failure = await publisher.prepare(binding, { deadlineUnixMs: Date.now() + 10_000, beforeCommit: () => {
      if (mode === "throw") throw new Error("private_token=fixture-secret");
      return false;
    } }).catch(error => error);
    expect(failure).toMatchObject({ reason: "publication_aborted", publication: {
      contract: "sgg.unity_mcp.test_run_publication_abort.v1", stage: "worker_precommit", reason: "guard_refused_or_failed", guard_budget_ms: 5000,
    } });
    expect(JSON.stringify(failure)).not.toContain("fixture-secret");
    expect(JSON.stringify(failure)).not.toContain(root);
    expect(new TestRunOperationStore(dir).lookup(binding.operation_id)).toEqual({ status: "not_found" });
  }, 15_000);

  it("never grants an expired five-second handshake after a slow parent guard, even with command budget remaining", async () => {
    const { root, dir, publisher } = setup();
    const binding = sampleBinding(root);
    const failure = await publisher.prepare(binding, { deadlineUnixMs: Date.now() + 15_000, beforeCommit: () => {
      // Independent real worker deadline advances while the Gateway event loop is blocked.
      Atomics.wait(new Int32Array(new SharedArrayBuffer(4)), 0, 0, 5100);
      return true;
    } }).catch(error => error);
    expect(failure).toMatchObject({ reason: "publication_aborted", publication: {
      stage: "worker_precommit", reason: "guard_handshake_timeout", guard_budget_ms: 5000,
    } });
    expect(new TestRunOperationStore(dir).lookup(binding.operation_id)).toEqual({ status: "not_found" });
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.issued`))).toBe(false);
    expect(publisher.snapshotStatus().state).toBe("ok");
  }, 20_000);

  it("retains a committed binding when preparation ACK arrives after the original deadline", async () => {
    const { root, dir, publisher } = setup(fixture);
    (await publisher.prepare(sampleBinding(root), ready())).release();
    const binding = sampleBinding(root, "b10c0003");
    const deadline = Date.now() + 10_000;
    const preparation = publisher.prepare(binding, { deadlineUnixMs: deadline, beforeCommit: () => true }).catch(error => error);
    await waitUntil(() => fs.existsSync(path.join(dir, "blocked.signal")));
    const store = new TestRunOperationStore(dir);
    expect(store.lookup(binding.operation_id)).toEqual({ status: "found", binding });
    expect(store.lookupRequestIssuance(binding)).toEqual({ status: "reserved" });
    // Advance only the parent's absolute clock after the real commit; no storage-speed assumption.
    vi.spyOn(Date, "now").mockReturnValue(deadline);
    fs.writeFileSync(path.join(dir, "continue.signal"), "continue", { mode: 0o600 });
    expect(await preparation).toMatchObject({ reason: "storage_unavailable" });
    vi.restoreAllMocks();
    await publisher.close();
    expect(store.lookup(binding.operation_id)).toEqual({ status: "found", binding });
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.issued`))).toBe(false);
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.terminal`))).toBe(false);
    expect(lines(dir, "requests.signal")).toEqual(["prepare", "prepare"]);
  }, 15_000);

  it("does not transfer private environment or inherited Node loader arguments", async () => {
    vi.stubEnv("SGG_TEST_PRIVATE_TOKEN", "fixture-secret");
    vi.stubEnv("NODE_OPTIONS", "--trace-warnings");
    const { root, dir, publisher } = setup(fixture);
    (await publisher.prepare(sampleBinding(root), ready())).release();
    expect(JSON.parse(fs.readFileSync(path.join(dir, "environment.signal"), "utf8"))).toEqual({ hasPrivateToken: false, hasNodeOptions: false, execArgvCount: 0 });
  }, 15_000);

  it("does not block parent timers/microtasks, and release never cancels already queued issuance", async () => {
    const { root, dir, publisher } = setup(fixture);
    const binding = sampleBinding(root, "b10c0002");
    const lease = await publisher.prepare(binding, ready());
    const issuedAt = new Date();
    const issuance = lease.publishRequestIssuance(issuedAt);
    await waitUntil(() => fs.existsSync(path.join(dir, "blocked.signal")));
    await expect(lease.publishTerminalNoExecution({} as never, evidence())).rejects.toMatchObject({ reason: "terminal_outcome_invalid" });
    let microtask = false;
    queueMicrotask(() => { microtask = true; });
    await new Promise<void>(resolve => setTimeout(() => {
      expect(microtask).toBe(true);
      expect(new TestRunOperationStore(dir).lookupRequestIssuance(binding)).toEqual({ status: "reserved" });
      lease.release();
      fs.writeFileSync(path.join(dir, "continue.signal"), "continue", { mode: 0o600 });
      resolve();
    }, 0));
    const marker = await issuance;
    expect(marker.issued_at).toBe(issuedAt.toISOString());
    expect(new TestRunOperationStore(dir).lookupRequestIssuance(binding)).toEqual({ status: "found", marker });
    expect(lines(dir, "requests.signal")).toEqual(["prepare", "issuance"]);
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.terminal`))).toBe(false);
  }, 15_000);

  it.each(["bad00001", "bad00002", "bad00003", "bad00004", "bad00005", "bad00006", "bad00007"])("fuses on invalid correlated result %s without echo, retry or replacement", async suffix => {
    const { root, dir, publisher } = setup(fixture);
    const error = await publisher.prepare(sampleBinding(root, suffix), ready()).catch(error => error);
    expect(error).toMatchObject({ reason: "storage_unavailable" });
    expect(error.message).not.toContain("fixture-secret");
    expect(error.message).not.toContain(dir);
    await expect(publisher.prepare(sampleBinding(root), ready())).rejects.toMatchObject({ reason: "storage_unavailable" });
    expect(publisher.snapshotStatus().state).toBe("unavailable");
    await publisher.close();
    expect(lines(dir, "starts.signal")).toHaveLength(1);
    expect(lines(dir, "requests.signal")).toEqual(["prepare"]);
  }, 15_000);

  it("rejects every pending promise after worker exit and never respawns", async () => {
    const { root, dir, publisher } = setup(fixture);
    const results = await Promise.allSettled([
      publisher.prepare(sampleBinding(root, "c2a50000"), ready()),
      publisher.prepare(sampleBinding(root), ready()),
      publisher.prepare(sampleBinding(root), ready()),
    ]);
    expect(results.every(result => result.status === "rejected" && result.reason.reason === "storage_unavailable")).toBe(true);
    await expect(publisher.prepare(sampleBinding(root), ready())).rejects.toMatchObject({ reason: "storage_unavailable" });
    await publisher.close();
    expect(lines(dir, "starts.signal")).toHaveLength(1);
    expect(lines(dir, "requests.signal")).toEqual(["prepare"]);
  }, 15_000);

  it("preserves validated storage diagnostics, sanitizes errors and fuses pending work after storage I/O failure", async () => {
    const { root, dir, publisher } = setup(fixture);
    const results = await Promise.allSettled([
      publisher.prepare(sampleBinding(root, "51020000"), ready()),
      publisher.prepare(sampleBinding(root), ready()),
    ]);
    const first = results[0]!;
    expect(first.status).toBe("rejected");
    if (first.status === "rejected") {
      expect(first.reason).toMatchObject({ reason: "storage_unavailable", diagnostics: { stage: "binding_publication", category: "storage_io", automatic_recovery: "not_attempted" } });
      expect(first.reason.message).not.toContain("fixture-secret");
      expect(JSON.stringify(first.reason)).not.toContain(dir);
    }
    expect(results[1]).toMatchObject({ status: "rejected", reason: { reason: "storage_unavailable" } });
    expect(publisher.snapshotStatus()).toMatchObject({ state: "unavailable", last_failure: { stage: "binding_publication", category: "storage_io" } });
    await publisher.close();
    expect(lines(dir, "requests.signal")).toEqual(["prepare"]);
  }, 15_000);

  it.each([
    ["c2a50001", "reserved"],
    ["c2a50002", "found"],
    ["c2a50003", "reserved"],
  ] as const)("retains truthful issuance state after crash %s, without terminal synthesis or same-PID restart", async (suffix, observed) => {
    const { root, dir, publisher } = setup(fixture);
    const binding = sampleBinding(root, suffix);
    const lease = await publisher.prepare(binding, ready());
    await expect(lease.publishRequestIssuance(new Date())).rejects.toMatchObject({ reason: "storage_unavailable" });
    expect(new TestRunOperationStore(dir).lookupRequestIssuance(binding).status).toBe(observed);
    await expect(lease.publishTerminalNoExecution({} as never, evidence())).rejects.toMatchObject({ reason: "storage_unavailable" });
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.terminal`))).toBe(false);
    const replacement = new TestRunOperationStorePublisher({ storeDir: dir, workerEntry: fixture });
    publishers.push(replacement);
    await expect(replacement.prepare(sampleBinding(root), ready())).rejects.toMatchObject({ reason: "storage_unavailable" });
    expect(lines(dir, "starts.signal")).toHaveLength(1);
    expect(lines(dir, "requests.signal")).toEqual(["prepare", "issuance"]);
  }, 15_000);

  it("rejects stale duplicated results even when the first preparation completed", async () => {
    const { root, publisher } = setup(fixture);
    const lease = await publisher.prepare(sampleBinding(root, "57a1e000"), ready());
    await waitUntil(() => publisher.snapshotStatus().state === "unavailable");
    await expect(lease.publishRequestIssuance(new Date())).rejects.toMatchObject({ reason: "storage_unavailable" });
    lease.release();
  }, 15_000);

  it("bounded close detaches an unknown commit, blocks same-directory replacement and leaves cleanup to the original worker", async () => {
    const { root, dir, publisher } = setup(fixture);
    const binding = sampleBinding(root, "b10c0002");
    const lease = await publisher.prepare(binding, ready());
    const issuance = lease.publishRequestIssuance(new Date()).catch(error => error);
    await waitUntil(() => fs.existsSync(path.join(dir, "blocked.signal")));
    await publisher.close();
    expect(await issuance).toMatchObject({ reason: "storage_unavailable" });
    expect(publisher.snapshotStatus().state).toBe("unavailable");
    const replacement = new TestRunOperationStorePublisher({ storeDir: dir, workerEntry: fixture });
    publishers.push(replacement);
    await expect(replacement.prepare(sampleBinding(root), ready())).rejects.toMatchObject({ reason: "storage_unavailable" });
    expect(lines(dir, "starts.signal")).toHaveLength(1);
    expect(new TestRunOperationStore(dir).lookupRequestIssuance(binding)).toEqual({ status: "reserved" });
    fs.writeFileSync(path.join(dir, "continue.signal"), "continue", { mode: 0o600 });
    await waitUntil(() => new TestRunOperationStore(dir).lookupRequestIssuance(binding).status === "found" && !fs.existsSync(path.join(dir, ".store.lock")));
    expect(fs.existsSync(path.join(dir, `${binding.operation_id}.terminal`))).toBe(false);
  }, 15_000);

  it("an idle real worker does not keep its parent process alive", async () => {
    const { root, dir } = setup();
    const bindingFile = path.join(root, "binding.input");
    fs.writeFileSync(bindingFile, JSON.stringify(sampleBinding(root)), { mode: 0o600 });
    const child = spawn(process.execPath, [fileURLToPath(new URL("./fixtures/test-run-operation-worker/idle-client.mjs", import.meta.url)), dir, bindingFile], { stdio: ["ignore", "ignore", "pipe"] });
    const errorChunks: Buffer[] = [];
    child.stderr.on("data", chunk => errorChunks.push(chunk));
    const result = await new Promise<number | null>((resolve, reject) => {
      const timer = setTimeout(() => { child.kill(); reject(new Error("Idle worker kept its parent alive")); }, 7000);
      child.once("error", error => { clearTimeout(timer); reject(error); });
      child.once("exit", code => { clearTimeout(timer); resolve(code); });
    });
    expect(Buffer.concat(errorChunks).toString()).toBe("");
    expect(result).toBe(0);
    expect(fs.existsSync(path.join(dir, "idle.signal"))).toBe(true);
  }, 15_000);

  it("an in-flight real worker keeps its parent alive until issuance commits, then becomes idle", async () => {
    const { root, dir } = setup();
    const binding = sampleBinding(root, "b10c0002");
    const bindingFile = path.join(root, "binding.input");
    fs.writeFileSync(bindingFile, JSON.stringify(binding), { mode: 0o600 });
    const child = spawn(process.execPath, [fileURLToPath(new URL("./fixtures/test-run-operation-worker/active-client.mjs", import.meta.url)), dir, bindingFile], { stdio: ["ignore", "ignore", "pipe"] });
    const errorChunks: Buffer[] = [];
    child.stderr.on("data", chunk => errorChunks.push(chunk));
    const completion = new Promise<number | null>((resolve, reject) => {
      const timer = setTimeout(() => { child.kill(); reject(new Error("Active worker fixture did not complete")); }, 12_000);
      child.once("error", error => { clearTimeout(timer); reject(error); });
      child.once("exit", code => { clearTimeout(timer); resolve(code); });
    });
    // Attach failure handling before the barrier wait; a fixture crash is never unhandled.
    void completion.catch(() => {});
    try {
      await waitUntil(() => fs.existsSync(path.join(dir, "blocked.signal")));
      await new Promise(resolve => setTimeout(resolve, 50));
      expect(child.exitCode).toBe(null);
      expect(fs.existsSync(path.join(dir, "committed.signal"))).toBe(false);
      fs.writeFileSync(path.join(dir, "continue.signal"), "continue", { mode: 0o600 });
      expect(await completion).toBe(0);
      expect(Buffer.concat(errorChunks).toString()).toBe("");
      expect(fs.existsSync(path.join(dir, "committed.signal"))).toBe(true);
      expect(new TestRunOperationStore(dir).lookupRequestIssuance(binding).status).toBe("found");
    } finally {
      if (child.exitCode === null) { child.kill(); await completion.catch(() => {}); }
    }
  }, 15_000);
});

describe("test-run worker wire validation", () => {
  it("rejects unbounded, private and impossible publication diagnostics", () => {
    const valid = { schema_version: 1, contract: "sgg.unity_mcp.test_run_publication_abort.v1", stage: "worker_precommit", reason: "guard_handshake_timeout", guard_budget_ms: 5000 };
    expect(() => assertTestRunPublicationAbortDiagnostics(valid)).not.toThrow();
    for (const value of [{ ...valid, raw_error: "secret" }, { ...valid, reason: "secret" }, { ...valid, guard_budget_ms: 5001 },
      { ...valid, guard_budget_ms: "5000" }, { ...valid, stage: "prepare_queue" }, { ...valid, guard_budget_ms: 0 },
      { ...valid, reason: "publisher_closed" }, { ...valid, stage: "prepare_admission", reason: "publisher_closed", guard_budget_ms: 0 }]) expect(() => assertTestRunPublicationAbortDiagnostics(value)).toThrow();
    const nonce = "11111111-1111-4111-8111-111111111111";
    const result = { protocol: 1, nonce, type: "result", job_id: 1, owner_binding_hash: `sha256:${"a".repeat(64)}`,
      phase: "prepare", status: { schema_version: 1, state: "ok", bindings: 0, bytes: 0 }, outcome: "error",
      error: { reason: "publication_aborted", publication: valid } };
    expect(() => assertTestRunOperationWorkerMessage(result, nonce)).not.toThrow();
    for (const phase of ["issuance", "terminal"]) expect(() => assertTestRunOperationWorkerMessage({ ...result, phase }, nonce)).toThrow();
  });
  it("accepts only exact boot keys and bounded numeric limits", () => {
    const boot = { role: "sgg.test_run_operation_store", protocol: 1, nonce: "11111111-1111-4111-8111-111111111111", dir: path.resolve("owned-fixture"), limits: resolveTestRunOperationStoreLimits() };
    expect(() => assertTestRunOperationWorkerData(boot)).not.toThrow();
    for (const changed of [{ ...boot, token: "secret" }, { ...boot, protocol: "1" }, { ...boot, dir: "relative" },
      { ...boot, dir: `${boot.dir}${path.sep}` }, { ...boot, dir: `${boot.dir}${path.sep}.${path.sep}child` },
      { ...boot, limits: { ...boot.limits, maxBytes: "1" } }]) expect(() => assertTestRunOperationWorkerData(changed)).toThrow();
    expect(() => assertTestRunOperationWorkerData({ ...boot, dir: path.parse(boot.dir).root })).not.toThrow();
    if (process.platform === "win32") expect(() => assertTestRunOperationWorkerData({ ...boot, dir: "\\owned-fixture" })).toThrow();
  });
  it("validates exact jobs, owner hash, captured timestamp, guard and no coercible evidence", () => {
    const { root, dir } = setup();
    const binding = sampleBinding(root);
    const data = { role: "sgg.test_run_operation_store" as const, protocol: 1 as const, nonce: "11111111-1111-4111-8111-111111111111", dir, limits: resolveTestRunOperationStoreLimits() };
    const job = { protocol: 1, nonce: data.nonce, job_id: 1, owner_binding_hash: testRunOperationBindingHash(binding), binding,
      phase: "prepare", deadline_unix_ms: Date.now() + 1000, guard: new SharedArrayBuffer(4) };
    expect(() => assertTestRunOperationWorkerJob(job, data)).not.toThrow();
    for (const changed of [
      { ...job, caller_args: {} }, { ...job, job_id: "1" }, { ...job, owner_binding_hash: `sha256:${"0".repeat(64)}` },
      { ...job, deadline_unix_ms: Infinity }, { ...job, guard: new ArrayBuffer(4) }, { ...job, guard: new SharedArrayBuffer(8) },
    ]) expect(() => assertTestRunOperationWorkerJob(changed, data)).toThrow();
    const { deadline_unix_ms: _deadline, guard: _guard, ...issuedJob } = job;
    expect(() => assertTestRunOperationWorkerJob({ ...issuedJob, phase: "issuance", issued_at: binding.created_at }, data)).not.toThrow();
    for (const issuedAt of [new Date(Date.parse(binding.created_at) - 1).toISOString(), "2026-10-10", 1000]) expect(() => assertTestRunOperationWorkerJob({ ...issuedJob, phase: "issuance", issued_at: issuedAt }, data)).toThrow();
    const coercion = vi.fn(() => "current");
    expect(() => assertTerminalEvidence({ ...evidence(), package_update: { ...evidence().package_update, status: { toString: coercion } } })).toThrow();
    expect(coercion).not.toHaveBeenCalled();
  });
  it.each([
    { schema_version: 1, state: "ok", bindings: -1, bytes: 0 },
    { schema_version: 1, state: "ok", bindings: 0, bytes: 0, arbitrary_path: "secret" },
    { schema_version: 1, state: "ok", bindings: 0, bytes: 0, lock_recovery: { schema_version: 1, owner_generation_state: "invalid", automatic_recovery: "not_needed" } },
    { schema_version: 1, state: "ok", bindings: 0, bytes: 0, last_failure: { raw_error: "secret" } },
  ])("rejects invalid status/diagnostic payload %j", status => {
    const nonce = "11111111-1111-4111-8111-111111111111";
    expect(() => assertTestRunOperationWorkerMessage({ protocol: 1, nonce, type: "ready", status }, nonce)).toThrow();
  });
});

function setup(entry?: URL, limits?: TestRunOperationStoreLimits) {
  const tempParent = process.platform !== "win32" && fs.existsSync("/tmp/opencode") ? "/tmp/opencode" : os.tmpdir();
  const root = fs.mkdtempSync(path.join(tempParent, "test-run-publisher-"));
  fs.chmodSync(root, 0o700);
  roots.push(root);
  const dir = path.join(root, "operations");
  const publisher = new TestRunOperationStorePublisher({ storeDir: dir, ...(entry ? { workerEntry: entry } : {}), ...(limits ? { limits } : {}) });
  publishers.push(publisher);
  return { root, dir, publisher };
}
function sampleBinding(root: string, suffix = (++sequence).toString(16).padStart(8, "0")): TestRunOperationBinding {
  const created = new Date();
  const stamp = created.toISOString().replace(/[-:]/g, "");
  const operationId = `test-${stamp.slice(0, 8)}-${stamp.slice(9, 15)}-${suffix}`;
  const canonical = new CanonicalPathService().normalizeProjectRoot(root);
  return { schema_version: 1, contract: TEST_RUN_OPERATION_OWNER_CONTRACT, command: "test.run", operation_id: operationId,
    project: "fixture", original_project_selector: "fixture", origin_project_root: root, canonical_project_root: canonical,
    canonical_project_root_hash: testRunCanonicalProjectRootHash(canonical), origin_instance_id: "fixture-instance", origin_unity_pid: 1234,
    origin_unity_process_start_utc: new Date(created.getTime() - 60_000).toISOString(), caller_args_hash: `sha256:${"a".repeat(64)}`,
    artifact_path: `${TEST_RUN_ARTIFACT_ROOT}/${operationId}.json`, artifact_schema_version: 3, package_id: "com.sungeargames.unity-mcp",
    package_version: "2026.10.10-4", commands_hash: `sha256:${"b".repeat(64)}`, catalog_generation: 1,
    artifact_schema_capability: TEST_RUN_ARTIFACT_CAPABILITY, created_at: created.toISOString() };
}
function ready() { return { deadlineUnixMs: Date.now() + 10_000, beforeCommit: () => true }; }
function lines(dir: string, name: string): string[] { return fs.readFileSync(path.join(dir, name), "utf8").trim().split("\n"); }
async function waitUntil(predicate: () => boolean): Promise<void> {
  const deadline = Date.now() + 10_000;
  while (!predicate()) {
    if (Date.now() >= deadline) throw new Error("Owned worker fixture did not reach its barrier");
    await new Promise(resolve => setTimeout(resolve, 10));
  }
}
function evidence(): TestRunTerminalNoExecutionEvidence {
  return { bridge_error_code: "package_refresh_required", reason: "package_refresh_required", package_update: {
    status: "package_refresh_required", refresh_pending: true, package_refresh_required: true, package_refresh_wait_can_advance: false,
    stale_package_resolution: false, fingerprint: "original", pending_fingerprint: "pending", last_transition_utc: "",
    local_package_code_inventory: { status: "current", failure_reason: "", mismatch_package: "", local_package_count: 0, source_file_count: 0,
      asset_database_file_count: 0, missing_asset_file_count: 0, unexpected_asset_file_count: 0, source_fingerprint: "", asset_database_fingerprint: "", source_state_fingerprint: "" },
  } };
}
