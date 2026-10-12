import type { MessagePort } from "node:worker_threads";

import { TestRunOperationStore, TestRunOperationStoreError, type TestRunRequestIssuanceMarker, type TestRunTerminalNoExecutionReceipt } from "./test-run-operation-store.js";
import {
  assertTestRunOperationWorkerClose, assertTestRunOperationWorkerData, assertTestRunOperationWorkerJob,
  TEST_RUN_OPERATION_WORKER_GUARD_TIMEOUT_MS, workerFailure, publicationAbort,
  type TestRunOperationWorkerJob, type TestRunOperationWorkerMessage,
} from "./test-run-operation-worker-protocol.js";

/** Internal storage service only: no CLI/configuration/registry/server construction. */
export function runTestRunOperationStoreWorker(port: MessagePort | null, input: unknown): void {
  if (!port) throw new TestRunOperationStoreError("storage_unavailable");
  assertTestRunOperationWorkerData(input);
  const data = Object.freeze({ ...structuredClone(input), limits: Object.freeze(structuredClone(input.limits)) });
  const store = new TestRunOperationStore(data.dir, data.limits);
  let lastJobId = 0;
  let closed = false;
  const reply = (message: TestRunOperationWorkerMessage) => port.postMessage(message);
  const base = { protocol: data.protocol, nonce: data.nonce };
  port.on("message", (inputJob: unknown) => {
    if (closed) return;
    try {
      if (typeof inputJob === "object" && inputJob !== null && "type" in inputJob) {
        assertTestRunOperationWorkerClose(inputJob, data.nonce);
        closed = true;
        port.close();
        return;
      }
      assertTestRunOperationWorkerJob(inputJob, data);
      // A monotonic per-context ID rejects replay without an ever-growing history.
      if (inputJob.job_id <= lastJobId) throw new TestRunOperationStoreError("storage_unavailable");
      lastJobId = inputJob.job_id;
      const job = inputJob;
      const resultBase = { ...base, type: "result" as const, job_id: job.job_id, owner_binding_hash: job.owner_binding_hash, phase: job.phase };
      try {
        let value: null | TestRunRequestIssuanceMarker | TestRunTerminalNoExecutionReceipt;
        if (job.phase === "prepare") {
          if (Date.now() >= job.deadline_unix_ms) throw publicationAbort("prepare_admission", "command_deadline_elapsed");
          store.publish(job.binding, { beforeCommit: () => beforeCommit(job) });
          value = null;
        } else if (job.phase === "issuance") {
          value = store.publishRequestIssuance(job.binding, new Date(job.issued_at));
        } else {
          value = store.publishTerminalNoExecution(job.binding, job.marker, job.evidence);
        }
        reply({ ...resultBase, outcome: "ok", value, status: store.snapshotStatus() });
      } catch (error) {
        reply({ ...resultBase, outcome: "error", error: workerFailure(error), status: store.snapshotStatus() });
      }
    } catch {
      // Invalid wire input is fatal. Never echo it or attempt a guessed job/operation.
      closed = true;
      port.close();
      process.exitCode = 1;
    }
  });
  port.on("messageerror", () => { closed = true; port.close(); process.exitCode = 1; });
  reply({ ...base, type: "ready", status: store.snapshotStatus() });

  function beforeCommit(job: Extract<TestRunOperationWorkerJob, { phase: "prepare" }>): boolean {
    const cell = new Int32Array(job.guard);
    const guardStarted = Date.now();
    const remaining = Math.min(job.deadline_unix_ms - guardStarted, TEST_RUN_OPERATION_WORKER_GUARD_TIMEOUT_MS);
    if (remaining <= 0) throw publicationAbort("worker_precommit", "command_deadline_elapsed");
    if (Atomics.load(cell, 0) !== 0) throw publicationAbort("worker_precommit", "guard_refused_or_failed");
    const guardDeadline = guardStarted + remaining;
    reply({ ...base, type: "before_commit", job_id: job.job_id, owner_binding_hash: job.owner_binding_hash, guard_deadline_unix_ms: guardDeadline });
    const waited = Atomics.wait(cell, 0, 0, remaining);
    if (waited === "timed-out" || Date.now() >= guardDeadline) {
      // Expiry is final. A delayed parent must never resurrect this permission cell.
      Atomics.compareExchange(cell, 0, 0, 2);
      throw publicationAbort("worker_precommit", Date.now() >= job.deadline_unix_ms ? "command_deadline_elapsed" : "guard_handshake_timeout", remaining);
    }
    if (Atomics.load(cell, 0) !== 1) throw publicationAbort("worker_precommit", "guard_refused_or_failed", remaining);
    return true;
  }
}
