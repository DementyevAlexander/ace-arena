import fs from "node:fs";
import path from "node:path";
import { tsImport } from "tsx/esm/api";

const { TestRunOperationStorePublisher } = await tsImport("../../../src/operations/test-run-operation-publisher.ts", { parentURL: import.meta.url, tsconfig: false });
const [dir, bindingFile] = process.argv.slice(2);
const publisher = new TestRunOperationStorePublisher({ storeDir: dir, workerEntry: new URL("./controlled-bootstrap.mjs", import.meta.url) });
const lease = await publisher.prepare(JSON.parse(fs.readFileSync(bindingFile, "utf8")), { deadlineUnixMs: Date.now() + 10_000, beforeCommit: () => true });
const issuance = lease.publishRequestIssuance(new Date());
lease.release();
issuance.then(() => fs.writeFileSync(path.join(dir, "committed.signal"), "committed", { mode: 0o600 }));
// No timer, await or close(): only the active worker can keep this process alive until commit.
