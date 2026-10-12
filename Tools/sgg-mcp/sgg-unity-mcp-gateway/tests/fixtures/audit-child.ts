import { FileAuditSink, createAuditEvent } from "../../src/observability/audit.js";

const [root, timestamp, runId] = process.argv.slice(2);
if (!root || !timestamp || !runId) throw new Error("root, timestamp, and run id are required");
const now = new Date(timestamp);
const sink = new FileAuditSink(root, { maxFileBytes: 128 * 1024, maxFiles: 4, maxAgeMs: 86_400_000, maxTotalBytes: 512 * 1024, now: () => now });
const event = createAuditEvent("sgg_unity_projects", {}, { status: "ok", data: {}, diagnostics: [], artifact_refs: Array.from({ length: 16 }, (_, index) => `Library/${runId}-${index}-${"x".repeat(950)}`) }, now.getTime(), () => now, {}, runId, runId);
for (let attempt = 0; attempt < 3; attempt += 1) {
  try {
    sink.write(event);
    break;
  } catch (error) {
    if (attempt === 2 || !(error instanceof Error) || !error.message.startsWith("Storage lock unavailable.")) throw error;
  }
}
