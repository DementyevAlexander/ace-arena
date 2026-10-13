import { HttpUnityBridgeClient } from "../../../src/bridge/bridge-client.js";
import type { PolicyConfig } from "../../../src/config/config.js";
import type { DispatcherContext } from "../../../src/mcp/dispatcher.js";
import { startHttpMcpServer } from "../../../src/mcp/http-server.js";
import { createUnityMcpServer } from "../../../src/mcp/sdk-server.js";
import { createUnityMcpV2ServerFactory } from "../../../src/mcp/sdk-v2-server.js";
import type { AuditEvent, AuditSink } from "../../../src/observability/audit.js";
import { InMemoryAuditSink } from "../../../src/observability/audit.js";
import { ProjectRegistry } from "../../../src/registry/project-registry.js";

const [instancesDir, runId, rawPort, mode = "normal"] = process.argv.slice(2);
if (!instancesDir || !runId || !rawPort) throw new Error("Expected instancesDir, runId and port");
const port = Number(rawPort);
if (!Number.isSafeInteger(port) || port <= 0 || port > 65535 || port === 27182) throw new Error("Invalid child Gateway port");

class BlockingAuditSink implements AuditSink {
  prepare(event: AuditEvent): Promise<void> {
    writeMessage({ stage: "audit_prepare", run_id: runId, audit_id: event.audit_id });
    return new Promise<void>(() => {});
  }

  write(): void {}
}

const audit: AuditSink = mode === "block-audit" ? new BlockingAuditSink() : new InMemoryAuditSink();
const context: DispatcherContext = {
  registry: new ProjectRegistry({ instancesDir, staleAfterMs: 90_000 }),
  bridge: new HttpUnityBridgeClient(),
  policy: allowUnsafePolicy(),
  audit,
  runId,
};
const modernFactory = createUnityMcpV2ServerFactory(context, { version: "0.0.0" });
const running = await startHttpMcpServer(
  () => createUnityMcpServer(context, { version: "0.0.0" }),
  { host: "127.0.0.1", port, modernServerFactory: modernFactory, shutdownTimeoutMs: 250 },
);

writeMessage({
  stage: "ready",
  run_id: runId,
  pid: process.pid,
  process_start_utc: new Date(Date.now() - process.uptime() * 1000).toISOString(),
  url: running.url,
});

let stopping = false;
const stop = async () => {
  if (stopping) return;
  stopping = true;
  await running.close();
  process.exitCode = 0;
};
process.once("SIGTERM", () => { void stop(); });
process.once("SIGINT", () => { void stop(); });

function allowUnsafePolicy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: {
      read: "allow",
      write: "allow",
      execute: "allow",
      destructive: "deny",
      network: "deny",
    },
    projects: {},
  };
}

function writeMessage(message: Record<string, unknown>): void {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}
