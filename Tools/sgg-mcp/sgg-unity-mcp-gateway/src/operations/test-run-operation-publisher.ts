import { randomUUID } from "node:crypto";
import { Worker } from "node:worker_threads";

import { absoluteStoragePath } from "../security/safe-storage.js";
import {
  assertTestRunOperationBinding, assertTestRunRequestIssuanceMarker, assertTestRunTerminalNoExecutionReceipt,
  resolveTestRunOperationStoreLimits, testRunOperationBindingHash, testRunRequestIssuanceMarkerHash,
  TestRunOperationStoreError, type TestRunOperationBinding, type TestRunOperationStoreLimits,
  type TestRunOperationStoreStatus, type TestRunRequestIssuanceMarker, type TestRunTerminalNoExecutionEvidence,
  type TestRunTerminalNoExecutionReceipt,
} from "./test-run-operation-store.js";
import {
  assertTerminalEvidence, assertTestRunOperationWorkerData, assertTestRunOperationWorkerJob,
  assertTestRunOperationWorkerLimits, assertTestRunOperationWorkerMessage,
  TEST_RUN_OPERATION_WORKER_PROTOCOL, TEST_RUN_OPERATION_WORKER_ROLE,
  TEST_RUN_OPERATION_WORKER_GUARD_TIMEOUT_MS, publicationAbort,
  type TestRunOperationWorkerData, type TestRunOperationWorkerJob, type TestRunOperationWorkerResult,
} from "./test-run-operation-worker-protocol.js";

export interface TestRunOperationPublicationLease {
  publishRequestIssuance(issuedAt: Date): Promise<TestRunRequestIssuanceMarker>;
  publishTerminalNoExecution(marker: TestRunRequestIssuanceMarker, evidence: TestRunTerminalNoExecutionEvidence): Promise<TestRunTerminalNoExecutionReceipt>;
  release(): void;
}
export interface TestRunOperationPublisher {
  readonly dir: string;
  readonly limits?: TestRunOperationStoreLimits;
  prepare(binding: TestRunOperationBinding, options: { deadlineUnixMs: number; beforeCommit: () => boolean }): Promise<TestRunOperationPublicationLease>;
  snapshotStatus(): TestRunOperationStoreStatus;
  close(): Promise<void>;
}
export type TestRunOperationStorePublisherOptions = {
  storeDir: string;
  limits?: TestRunOperationStoreLimits;
  /** Trusted internal entry only. The production CLI supplies its own file URL. */
  workerEntry?: URL;
};
export const TEST_RUN_OPERATION_PUBLISHER_MAX_LEASES = 64;
export const TEST_RUN_OPERATION_PUBLISHER_SHUTDOWN_TIMEOUT_MS = 1_000;
const BOOT_TIMEOUT_MS = 5_000;
// Binding + a matching marker + the bounded terminal-evidence/envelope fields. Reserving the
// largest wire job now ensures bookkeeping never competes for new queue capacity after fetch.
const LEASE_WIRE_OVERHEAD_BYTES = 16 * 1024;
// A crashed thread can leave a lock owned by our still-live PID. Failed roots stay quarantined
// for this process lifetime; never infer that thread exit proves the existing lock was released.
const ACTIVE_WORKER_ROOTS = new Set<string>();

type LeaseState = {
  binding: TestRunOperationBinding;
  hash: string;
  bytes: number;
  released: boolean;
  pending: number;
  issuanceStarted: boolean;
  terminalStarted: boolean;
  marker?: TestRunRequestIssuanceMarker;
};
type Job = {
  request: TestRunOperationWorkerJob;
  lease: LeaseState;
  resolve: (value: unknown) => void;
  reject: (error: TestRunOperationStoreError) => void;
  settled: boolean;
  guardSeen: boolean;
  beforeCommit?: () => boolean;
  timer: ReturnType<typeof setTimeout> | undefined;
};

/** All lock-taking publication work runs off the Gateway event loop. Never retries a job. */
export class TestRunOperationStorePublisher implements TestRunOperationPublisher {
  readonly dir: string;
  readonly #data: TestRunOperationWorkerData;
  readonly #entry: URL;
  readonly #rootKey: string;
  readonly #leases = new Map<string, LeaseState>();
  readonly #jobs = new Map<number, Job>();
  readonly #queue: Job[] = [];
  #bytes = 0;
  #nextJobId = 0;
  #worker: Worker | undefined;
  #active: Job | undefined;
  #ready = false;
  #failed = false;
  #closed = false;
  #rootClaimed = false;
  #everPosted = false;
  #closeSent = false;
  #bootTimer: ReturnType<typeof setTimeout> | undefined;
  #closeTimer: ReturnType<typeof setTimeout> | undefined;
  #closePromise: Promise<void> | undefined;
  #resolveClose: (() => void) | undefined;
  #status: TestRunOperationStoreStatus = { schema_version: 1, state: "ok", bindings: 0, bytes: 0 };

  constructor(options: TestRunOperationStorePublisherOptions) {
    this.dir = absoluteStoragePath(options.storeDir);
    if (options.limits !== undefined) assertTestRunOperationWorkerLimits(options.limits);
    const limits = resolveTestRunOperationStoreLimits(options.limits);
    this.#data = Object.freeze({ role: TEST_RUN_OPERATION_WORKER_ROLE, protocol: TEST_RUN_OPERATION_WORKER_PROTOCOL, nonce: randomUUID(), dir: this.dir, limits });
    assertTestRunOperationWorkerData(this.#data);
    this.#entry = new URL((options.workerEntry ?? new URL("./test-run-operation-worker-bootstrap.mjs", import.meta.url)).href);
    if (this.#entry.protocol !== "file:" || this.#entry.search || this.#entry.hash) throw new TypeError("Invalid test run operation worker entry");
    this.#rootKey = process.platform === "win32" ? this.dir.toLowerCase() : this.dir;
  }

  async prepare(binding: TestRunOperationBinding, options: { deadlineUnixMs: number; beforeCommit: () => boolean }): Promise<TestRunOperationPublicationLease> {
    this.available();
    if (!Number.isSafeInteger(options.deadlineUnixMs) || options.deadlineUnixMs > 8_640_000_000_000_000
      || typeof options.beforeCommit !== "function") throw new TestRunOperationStoreError("publication_aborted");
    if (options.deadlineUnixMs <= Date.now()) throw publicationAbort("prepare_admission", "command_deadline_elapsed");
    assertTestRunOperationBinding(binding);
    const captured = Object.freeze(structuredClone(binding));
    const bindingBytes = Buffer.byteLength(`${JSON.stringify(captured, null, 2)}\n`);
    if (bindingBytes > this.#data.limits.maxBindingBytes) throw new TestRunOperationStoreError("binding_invalid");
    const bytes = 2 * bindingBytes + LEASE_WIRE_OVERHEAD_BYTES;
    if (this.#leases.has(captured.operation_id)) throw new TestRunOperationStoreError("binding_exists");
    if (this.#leases.size >= Math.min(TEST_RUN_OPERATION_PUBLISHER_MAX_LEASES, this.#data.limits.maxBindings)
      || this.#bytes + bytes > this.#data.limits.maxBytes) throw new TestRunOperationStoreError("capacity_exhausted");
    const lease: LeaseState = { binding: captured, hash: testRunOperationBindingHash(captured), bytes, released: false, pending: 0, issuanceStarted: false, terminalStarted: false };
    this.#leases.set(captured.operation_id, lease);
    this.#bytes += bytes;
    try {
      await this.enqueue(lease, { phase: "prepare", deadline_unix_ms: options.deadlineUnixMs, guard: new SharedArrayBuffer(4) }, options.beforeCommit);
      this.available();
      return Object.freeze({
        publishRequestIssuance: (issuedAt: Date) => this.issue(lease, issuedAt),
        publishTerminalNoExecution: (marker: TestRunRequestIssuanceMarker, evidence: TestRunTerminalNoExecutionEvidence) => this.terminal(lease, marker, evidence),
        release: () => this.release(lease),
      });
    } catch (error) {
      this.release(lease);
      throw error;
    }
  }

  snapshotStatus(): TestRunOperationStoreStatus { return structuredClone(this.#status); }

  get limits(): TestRunOperationStoreLimits { return this.#data.limits; }

  close(): Promise<void> {
    if (this.#closePromise) return this.#closePromise;
    this.#closed = true;
    this.#closePromise = new Promise(resolve => { this.#resolveClose = resolve; });
    for (const lease of this.#leases.values()) this.release(lease);
    // Preparation has not authorized a fetch. Refuse queued work and deny ungranted precommits.
    for (const job of [...this.#jobs.values()]) if (job.request.phase === "prepare") this.expirePrepare(job);
    this.#closeTimer = setTimeout(() => {
      // Do not terminate a worker inside an atomic commit. The result is unknown, not cancelled.
      this.fuse();
      this.#worker?.unref();
      this.finishClose();
    }, TEST_RUN_OPERATION_PUBLISHER_SHUTDOWN_TIMEOUT_MS);
    this.maybeCloseWorker();
    return this.#closePromise;
  }

  private async issue(lease: LeaseState, issuedAt: Date): Promise<TestRunRequestIssuanceMarker> {
    this.liveLease(lease);
    if (lease.issuanceStarted) throw new TestRunOperationStoreError("issuance_invalid");
    let timestamp: number;
    try { timestamp = Date.prototype.getTime.call(issuedAt); } catch { throw new TestRunOperationStoreError("issuance_invalid"); }
    if (!Number.isFinite(timestamp) || timestamp < Date.parse(lease.binding.created_at)) throw new TestRunOperationStoreError("issuance_invalid");
    lease.issuanceStarted = true;
    const marker = await this.enqueue(lease, { phase: "issuance", issued_at: new Date(timestamp).toISOString() }) as TestRunRequestIssuanceMarker;
    lease.marker = Object.freeze(structuredClone(marker));
    return structuredClone(marker);
  }

  private async terminal(lease: LeaseState, marker: TestRunRequestIssuanceMarker, evidence: TestRunTerminalNoExecutionEvidence): Promise<TestRunTerminalNoExecutionReceipt> {
    this.liveLease(lease);
    if (lease.terminalStarted || !lease.marker) throw new TestRunOperationStoreError("terminal_outcome_invalid");
    assertTestRunRequestIssuanceMarker(marker, lease.binding);
    if (testRunRequestIssuanceMarkerHash(marker) !== testRunRequestIssuanceMarkerHash(lease.marker)) throw new TestRunOperationStoreError("terminal_outcome_invalid");
    assertTerminalEvidence(evidence);
    lease.terminalStarted = true;
    return await this.enqueue(lease, { phase: "terminal", marker: structuredClone(lease.marker), evidence: structuredClone(evidence) }) as TestRunTerminalNoExecutionReceipt;
  }

  private enqueue(lease: LeaseState, phase: { phase: "prepare"; deadline_unix_ms: number; guard: SharedArrayBuffer } | { phase: "issuance"; issued_at: string } | { phase: "terminal"; marker: TestRunRequestIssuanceMarker; evidence: TestRunTerminalNoExecutionEvidence }, beforeCommit?: () => boolean): Promise<unknown> {
    const request: TestRunOperationWorkerJob = { protocol: 1, nonce: this.#data.nonce, job_id: ++this.#nextJobId, owner_binding_hash: lease.hash, binding: lease.binding, ...phase };
    assertTestRunOperationWorkerJob(request, this.#data);
    if (Buffer.byteLength(JSON.stringify(request)) > lease.bytes || lease.pending !== 0) throw new TestRunOperationStoreError("storage_unavailable");
    return new Promise((resolve, reject) => {
      const job: Job = { request, lease, resolve, reject, settled: false, guardSeen: false, timer: undefined, ...(beforeCommit ? { beforeCommit } : {}) };
      lease.pending++;
      this.#jobs.set(request.job_id, job);
      this.#queue.push(job);
      if (request.phase === "prepare") job.timer = setTimeout(() => this.expirePrepare(job), Math.min(request.deadline_unix_ms - Date.now(), 2_147_483_647));
      this.boot();
      this.drain();
    });
  }

  private boot(): void {
    if (this.#worker || this.#failed) return;
    if (ACTIVE_WORKER_ROOTS.has(this.#rootKey) || ACTIVE_WORKER_ROOTS.size >= TEST_RUN_OPERATION_PUBLISHER_MAX_LEASES) { this.fuse(); return; }
    ACTIVE_WORKER_ROOTS.add(this.#rootKey);
    this.#rootClaimed = true;
    try {
      const worker = new Worker(this.#entry, { workerData: this.#data, execArgv: [], env: workerEnvironment(), stdout: true, stderr: true });
      this.#worker = worker;
      // Capture, but do not forward or continuously read worker stdio: a pending stdio read
      // references Node's internal MessagePort even after worker.unref(). The service logs nothing.
      worker.on("message", this.onMessage);
      worker.on("messageerror", this.onFailure);
      worker.on("error", this.onFailure);
      worker.on("exit", this.onExit);
      this.#bootTimer = setTimeout(() => this.fuse(), BOOT_TIMEOUT_MS);
    } catch {
      this.releaseRoot();
      this.fuse();
    }
  }

  private readonly onFailure = (): void => { this.fuse(); };
  private readonly onExit = (code: number): void => {
    if (this.#bootTimer) clearTimeout(this.#bootTimer);
    this.#bootTimer = undefined;
    this.#worker?.off("message", this.onMessage);
    this.#worker?.off("messageerror", this.onFailure);
    this.#worker?.off("error", this.onFailure);
    this.#worker?.off("exit", this.onExit);
    this.#worker?.stdout.destroy();
    this.#worker?.stderr.destroy();
    if (code !== 0 || !this.#closed || this.#jobs.size > 0) this.fuse();
    if (!this.#failed || !this.#everPosted) this.releaseRoot();
    this.#worker = undefined;
    this.finishClose();
  };
  private readonly onMessage = (input: unknown): void => {
    if (this.#failed) return;
    try {
      assertTestRunOperationWorkerMessage(input, this.#data.nonce);
      if (input.type === "ready") {
        if (this.#ready) throw new TestRunOperationStoreError("storage_unavailable");
        this.#ready = true;
        if (this.#bootTimer) clearTimeout(this.#bootTimer);
        this.#bootTimer = undefined;
        this.#status = structuredClone(input.status);
        this.drain();
        return;
      }
      const job = this.#active;
      if (!this.#ready || !job || input.job_id !== job.request.job_id || input.owner_binding_hash !== job.lease.hash) throw new TestRunOperationStoreError("storage_unavailable");
      if (input.type === "before_commit") {
        if (job.request.phase !== "prepare" || job.guardSeen) throw new TestRunOperationStoreError("storage_unavailable");
        if (input.guard_deadline_unix_ms > job.request.deadline_unix_ms
          || input.guard_deadline_unix_ms > Date.now() + TEST_RUN_OPERATION_WORKER_GUARD_TIMEOUT_MS) throw new TestRunOperationStoreError("storage_unavailable");
        job.guardSeen = true;
        let allowed = false;
        try { allowed = !this.#closed && !job.settled && Date.now() < input.guard_deadline_unix_ms
          && job.beforeCommit?.() === true && Date.now() < input.guard_deadline_unix_ms; } catch { }
        const cell = new Int32Array(job.request.guard);
        Atomics.compareExchange(cell, 0, 0, allowed ? 1 : 2);
        Atomics.notify(cell, 0);
        return;
      }
      this.complete(job, input);
    } catch { this.fuse(); }
  };

  private complete(job: Job, result: TestRunOperationWorkerResult): void {
    if (result.phase !== job.request.phase) throw new TestRunOperationStoreError("storage_unavailable");
    if (result.outcome === "ok") {
      if (job.request.phase === "prepare") {
        if (!job.guardSeen || Atomics.load(new Int32Array(job.request.guard), 0) !== 1) throw new TestRunOperationStoreError("storage_unavailable");
      } else if (job.request.phase === "issuance") {
        assertTestRunRequestIssuanceMarker(result.value, job.lease.binding);
        if (result.value.issued_at !== job.request.issued_at) throw new TestRunOperationStoreError("storage_unavailable");
      } else {
        assertTestRunTerminalNoExecutionReceipt(result.value, job.lease.binding, job.request.marker);
        if (result.value.bridge_error_code !== job.request.evidence.bridge_error_code || result.value.reason !== job.request.evidence.reason
          || JSON.stringify(result.value.package_update) !== JSON.stringify(job.request.evidence.package_update)) throw new TestRunOperationStoreError("storage_unavailable");
      }
    }
    this.#status = structuredClone(result.status);
    this.#active = undefined;
    this.removeJob(job);
    if (result.outcome === "error") this.settle(job, undefined, new TestRunOperationStoreError(result.error.reason, result.error.diagnostics, result.error.publication));
    else if (job.request.phase === "prepare" && (this.#closed || Date.now() >= job.request.deadline_unix_ms)) this.settle(job, undefined, new TestRunOperationStoreError("storage_unavailable"));
    else this.settle(job, result.value);
    if (result.outcome === "error" && result.error.reason === "storage_unavailable") { this.fuse(); return; }
    this.drain();
  }

  private drain(): void {
    if (this.#failed || !this.#ready || this.#active) return;
    while (this.#queue.length) {
      const job = this.#queue.shift()!;
      if (job.request.phase === "prepare" && (this.#closed || Date.now() >= job.request.deadline_unix_ms)) {
        this.removeJob(job);
        this.settle(job, undefined, publicationAbort("prepare_queue", this.#closed ? "publisher_closed" : "command_deadline_elapsed"));
        continue;
      }
      this.#active = job;
      this.#everPosted = true;
      this.#worker?.ref();
      try { this.#worker!.postMessage(job.request); } catch { this.fuse(); }
      return;
    }
    this.#worker?.unref();
    this.maybeCloseWorker();
  }

  private expirePrepare(job: Job): void {
    if (!this.#jobs.has(job.request.job_id) || job.request.phase !== "prepare") return;
    const cell = new Int32Array(job.request.guard);
    Atomics.compareExchange(cell, 0, 0, 2);
    Atomics.notify(cell, 0);
    if (this.#active === job) {
      // ACK may be lost after commit. Caller timeout is not proof of non-publication.
      this.settle(job, undefined, new TestRunOperationStoreError("storage_unavailable"));
    } else {
      const index = this.#queue.indexOf(job);
      if (index >= 0) this.#queue.splice(index, 1);
      this.removeJob(job);
      this.settle(job, undefined, publicationAbort("prepare_queue", this.#closed ? "publisher_closed" : "command_deadline_elapsed"));
      this.maybeCloseWorker();
    }
  }

  private settle(job: Job, value: unknown, error?: TestRunOperationStoreError): void {
    if (job.settled) return;
    job.settled = true;
    if (job.timer) clearTimeout(job.timer);
    job.timer = undefined;
    if (error) job.reject(error); else job.resolve(value);
  }
  private removeJob(job: Job): void {
    this.#jobs.delete(job.request.job_id);
    if (job.timer) clearTimeout(job.timer);
    job.lease.pending--;
    this.freeReleasedLease(job.lease);
  }
  private release(lease: LeaseState): void { lease.released = true; this.freeReleasedLease(lease); }
  private freeReleasedLease(lease: LeaseState): void {
    if (lease.released && lease.pending === 0 && this.#leases.get(lease.binding.operation_id) === lease) {
      this.#leases.delete(lease.binding.operation_id);
      this.#bytes -= lease.bytes;
    }
  }
  private liveLease(lease: LeaseState): void { this.available(); if (lease.released) throw new TestRunOperationStoreError("publication_aborted"); }
  private available(): void { if (this.#closed || this.#failed) throw new TestRunOperationStoreError("storage_unavailable"); }

  private fuse(): void {
    if (this.#failed) return;
    this.#failed = true;
    this.#status = { ...this.#status, state: "unavailable" };
    if (this.#bootTimer) clearTimeout(this.#bootTimer);
    this.#bootTimer = undefined;
    for (const job of this.#jobs.values()) {
      if (job.request.phase === "prepare") {
        const cell = new Int32Array(job.request.guard);
        Atomics.compareExchange(cell, 0, 0, 2);
        Atomics.notify(cell, 0);
      }
      this.settle(job, undefined, new TestRunOperationStoreError("storage_unavailable"));
      job.lease.pending--;
    }
    this.#jobs.clear();
    this.#queue.length = 0;
    this.#active = undefined;
    for (const lease of this.#leases.values()) this.release(lease);
    this.sendClose();
    this.#worker?.off("message", this.onMessage);
    // The worker can finish its original cleanup; no terminate(), replay or replacement worker.
    this.#worker?.unref();
    if (!this.#worker) this.finishClose();
  }
  private maybeCloseWorker(): void {
    if (!this.#closed || this.#jobs.size > 0) return;
    if (!this.#worker) this.finishClose();
    else { if (!this.#failed) this.#worker.ref(); this.sendClose(); }
  }
  private sendClose(): void {
    if (!this.#worker || this.#closeSent) return;
    this.#closeSent = true;
    try { this.#worker.postMessage({ protocol: 1, nonce: this.#data.nonce, type: "close" }); } catch { }
  }
  private finishClose(): void {
    if (this.#closeTimer) clearTimeout(this.#closeTimer);
    this.#closeTimer = undefined;
    this.#resolveClose?.();
    this.#resolveClose = undefined;
  }
  private releaseRoot(): void { if (this.#rootClaimed) ACTIVE_WORKER_ROOTS.delete(this.#rootKey); this.#rootClaimed = false; }
}

function workerEnvironment(): NodeJS.ProcessEnv {
  const env: NodeJS.ProcessEnv = {};
  for (const key of ["SystemRoot", "windir", "Path", "PATH", "PATHEXT", "ComSpec", "TEMP", "TMP"]) if (process.env[key] !== undefined) env[key] = process.env[key];
  return env;
}
