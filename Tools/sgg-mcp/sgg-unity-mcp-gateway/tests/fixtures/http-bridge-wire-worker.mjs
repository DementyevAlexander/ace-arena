import http from "node:http";
import { parentPort, workerData } from "node:worker_threads";

if (!parentPort || workerData?.role !== "owned-http-bridge-fixture" || !(workerData.counters instanceof SharedArrayBuffer))
  throw new Error("Invalid owned HTTP fixture");
const counters = new Int32Array(workerData.counters);
const server = http.createServer((request, response) => {
  const chunks = [];
  let bytes = 0;
  request.on("data", (chunk) => {
    bytes += chunk.byteLength;
    if (bytes > 65536) request.destroy();
    else chunks.push(chunk);
  });
  request.on("end", () => {
    Atomics.add(counters, 0, 1);
    Atomics.add(counters, 1, bytes);
    let body;
    try { body = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
    catch { response.writeHead(400); response.end(); return; }
    response.writeHead(200, { "Content-Type": "application/json" });
    response.end(JSON.stringify({ schema_version: 1, ...(typeof body.request_id === "string" ? { request_id: body.request_id } : {}),
      status: "ok", data: { acknowledged: true }, diagnostics: [], duration_ms: 0 }));
    Atomics.add(counters, 2, 1);
  });
});
server.listen(0, "127.0.0.1", () => {
  const address = server.address();
  parentPort.postMessage({ type: "listening", port: address.port });
});
