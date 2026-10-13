import fs from "node:fs";
import path from "node:path";
import { parentPort, workerData, type MessagePort } from "node:worker_threads";

import { runTestRunOperationStoreWorker } from "../../../src/operations/test-run-operation-worker.js";
import { assertTestRunOperationWorkerData, type TestRunOperationWorkerJob, type TestRunOperationWorkerMessage } from "../../../src/operations/test-run-operation-worker-protocol.js";

/** Authored test worker: controlled faults/progress barriers around the real service. */
export function run(): void {
  assertTestRunOperationWorkerData(workerData);
  if (!parentPort) throw new Error("Fixture requires a worker");
  const port = parentPort;
  fs.mkdirSync(workerData.dir, { recursive: true, mode: 0o700 });
  fs.appendFileSync(path.join(workerData.dir, "starts.signal"), "start\n", { mode: 0o600 });
  fs.writeFileSync(path.join(workerData.dir, "environment.signal"), JSON.stringify({ hasPrivateToken: Object.hasOwn(process.env, "SGG_TEST_PRIVATE_TOKEN"), hasNodeOptions: Object.hasOwn(process.env, "NODE_OPTIONS"), execArgvCount: process.execArgv.length }), { mode: 0o600 });
  const nativePost = port.postMessage.bind(port);
  let mode = "";
  const proxy = new Proxy(port, {
    get(target, key) {
      if (key === "postMessage") return (message: TestRunOperationWorkerMessage) => {
        if (message.type === "result" && message.phase === "prepare" && mode === "b10c0003") block();
        if (message.type === "result" && message.phase === "issuance" && mode === "c2a50002") process.exit(18);
        if (message.type === "result" && mode.startsWith("bad")) {
          const changed = structuredClone(message) as unknown as Record<string, unknown>;
          if (mode === "bad00001") changed.nonce = "11111111-1111-4111-8111-111111111111";
          if (mode === "bad00002") changed.owner_binding_hash = `sha256:${"f".repeat(64)}`;
          if (mode === "bad00003") changed.status = { schema_version: 1, state: "ok", bindings: "1", bytes: 1 };
          if (mode === "bad00004") { changed.outcome = "error"; delete changed.value; changed.error = { reason: "private_token=fixture-secret", message: workerData.dir }; }
          if (mode === "bad00005") changed.job_id = Number(changed.job_id) + 1;
          if (mode === "bad00006") changed.protocol = 2;
          if (mode === "bad00007") changed.phase = "issuance";
          nativePost(changed);
          return;
        }
        nativePost(message);
        if (message.type === "result" && mode === "57a1e000") nativePost(message);
      };
      if (key === "on") return (event: string, listener: (...args: unknown[]) => void) => {
        if (event === "message") target.on(event, (input: unknown) => {
          if (typeof input === "object" && input !== null && "phase" in input) {
            const job = input as TestRunOperationWorkerJob;
            mode = job.binding.operation_id.slice(-8);
            fs.appendFileSync(path.join(workerData.dir, "requests.signal"), `${job.phase}\n`, { mode: 0o600 });
            if (mode === "c2a50000") process.exit(17);
            if (mode === "c2a50001" && job.phase === "issuance") process.exit(17);
            if (mode === "c2a50003" && job.phase === "issuance") {
              const rename = fs.renameSync;
              fs.renameSync = (source, destination) => {
                if (String(source) === path.join(workerData.dir, `${job.binding.operation_id}.reserve`)) process.exit(19);
                rename(source, destination);
              };
            }
            if (mode === "51020000") {
              const link = fs.linkSync;
              fs.linkSync = (source, destination) => {
                if (String(destination) === path.join(workerData.dir, `${job.binding.operation_id}.json`)) throw Object.assign(new Error("private_token=fixture-secret"), { code: "EIO" });
                link(source, destination);
              };
            }
            if ((mode === "b10c0001" && job.phase === "prepare") || (mode === "b10c0002" && job.phase === "issuance")) {
              block();
            }
          }
          listener(input);
        });
        else target.on(event, listener);
        return proxy;
      };
      const value: unknown = Reflect.get(target, key, target);
      return typeof value === "function" ? value.bind(target) : value;
    },
  }) as MessagePort;
  runTestRunOperationStoreWorker(proxy, workerData);

  function block(): void {
    fs.writeFileSync(path.join(workerData.dir, "blocked.signal"), "blocked", { mode: 0o600 });
    const cell = new Int32Array(new SharedArrayBuffer(4));
    const deadline = Date.now() + 10_000;
    while (!fs.existsSync(path.join(workerData.dir, "continue.signal")) && Date.now() < deadline) Atomics.wait(cell, 0, 0, 10);
  }
}
