import fs from "node:fs";
import path from "node:path";
import { tsImport } from "tsx/esm/api";

const { TestRunOperationStorePublisher } = await tsImport("../../../src/operations/test-run-operation-publisher.ts", { parentURL: import.meta.url, tsconfig: false });
const [dir, bindingFile] = process.argv.slice(2);
const publisher = new TestRunOperationStorePublisher({ storeDir: dir });
const lease = await publisher.prepare(JSON.parse(fs.readFileSync(bindingFile, "utf8")), { deadlineUnixMs: Date.now() + 5000, beforeCommit: () => true });
lease.release();
fs.writeFileSync(path.join(dir, "idle.signal"), "idle", { mode: 0o600 });
// No close(): the idle worker must not keep this process alive.
