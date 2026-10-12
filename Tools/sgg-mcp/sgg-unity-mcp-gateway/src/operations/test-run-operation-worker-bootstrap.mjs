import { parentPort, workerData } from "node:worker_threads";
import { tsImport } from "tsx/esm/api";

// Fixed source-only entry. Production invokes the statically bundled service from the CLI.
const { runTestRunOperationStoreWorker } = await tsImport("./test-run-operation-worker.ts", { parentURL: import.meta.url, tsconfig: false });
runTestRunOperationStoreWorker(parentPort, workerData);
