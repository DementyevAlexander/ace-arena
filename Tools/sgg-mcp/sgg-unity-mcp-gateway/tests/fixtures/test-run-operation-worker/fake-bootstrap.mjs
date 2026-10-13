import fs from "node:fs";
import path from "node:path";
import { parentPort, workerData } from "node:worker_threads";

// A typed-protocol fixture, not evidence of real filesystem publication.
fs.mkdirSync(workerData.dir, { recursive: true, mode: 0o700 });
fs.appendFileSync(path.join(workerData.dir, "starts.signal"), "start\n", { mode: 0o600 });
const base = { protocol: 1, nonce: workerData.nonce };
let bindings = 0;
parentPort.on("message", job => {
  if (job.type === "close") { parentPort.close(); return; }
  fs.appendFileSync(path.join(workerData.dir, "requests.signal"), `${job.phase}\n`, { mode: 0o600 });
  if (job.phase !== "prepare") throw new Error("Fixture only prepares");
  parentPort.postMessage({ ...base, type: "before_commit", job_id: job.job_id, owner_binding_hash: job.owner_binding_hash,
    guard_deadline_unix_ms: Math.min(job.deadline_unix_ms, Date.now() + 5000) });
  const cell = new Int32Array(job.guard);
  Atomics.wait(cell, 0, 0, 5000);
  const allowed = Atomics.load(cell, 0) === 1;
  parentPort.postMessage({ ...base, type: "result", job_id: job.job_id, owner_binding_hash: job.owner_binding_hash, phase: "prepare",
    status: { schema_version: 1, state: "ok", bindings: allowed ? ++bindings : bindings, bytes: bindings * 512 },
    ...(allowed ? { outcome: "ok", value: null } : { outcome: "error", error: { reason: "publication_aborted" } }) });
});
parentPort.postMessage({ ...base, type: "ready", status: { schema_version: 1, state: "ok", bindings: 0, bytes: 0 } });
