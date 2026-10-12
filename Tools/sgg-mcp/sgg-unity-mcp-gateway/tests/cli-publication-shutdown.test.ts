import { EventEmitter } from "node:events";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { PassThrough } from "node:stream";
import { StdioServerTransport } from "@modelcontextprotocol/server/stdio";
import { describe, expect, it, vi } from "vitest";
import { waitForShutdown } from "../src/cli.js";
import { TestRunOperationStorePublisher } from "../src/operations/test-run-operation-publisher.js";
import { TestRunOperationStore } from "../src/operations/test-run-operation-store.js";
import { acquireStorageLock } from "../src/security/safe-storage.js";
import { ownedTestRunOperationBinding } from "./helpers/test-run-operation-publisher.js";

describe("Gateway publication shutdown", () => {
  it.each(["stdin_end", "stdin_close", "transport_close", "SIGINT", "SIGTERM"])
  ("drains only the original pending issuance on %s with bounded cleanup and no replay", async (trigger) => {
    const root = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-stdio-publication-shutdown-"));
    const store = new TestRunOperationStore(path.join(root, "operations"));
    const publisher = new TestRunOperationStorePublisher({ storeDir: store.dir });
    const binding = ownedTestRunOperationBinding(root);
    const lease = await publisher.prepare(binding, { deadlineUnixMs: Date.now() + 5000, beforeCommit: () => true });
    const lock = acquireStorageLock(store.dir, ".store.lock");
    const issuedAt = new Date();
    const issuance = lease.publishRequestIssuance(issuedAt);
    void issuance.catch(() => undefined);
    const input = new PassThrough();
    const transport = new StdioServerTransport(input, new PassThrough());
    const originalOnClose = vi.fn();
    transport.onclose = originalOnClose;
    const signals = new EventEmitter();
    const cleanup = vi.fn(() => publisher.close());
    const shutdown = waitForShutdown(cleanup, { input, transport, signals });
    const released = setTimeout(() => lock.release(), 100);
    try {
      if (trigger === "stdin_end") input.emit("end");
      else if (trigger === "stdin_close") input.emit("close");
      else if (trigger === "transport_close") await transport.close();
      else signals.emit(trigger);
      expect(cleanup).toHaveBeenCalledTimes(1);
      await shutdown;
      const marker = await issuance;
      expect(marker.issued_at).toBe(issuedAt.toISOString());
      expect(store.lookupRequestIssuance(binding)).toEqual({ status: "found", marker });
      expect(fs.readdirSync(store.dir).filter((name) => name.endsWith(".issued"))).toEqual([`${binding.operation_id}.issued`]);
      expect(fs.readdirSync(store.dir).filter((name) => name.endsWith(".terminal"))).toEqual([]);
      input.emit("end");
      input.emit("close");
      signals.emit("SIGINT");
      signals.emit("SIGTERM");
      expect(cleanup).toHaveBeenCalledTimes(1);
      expect(signals.listenerCount("SIGINT")).toBe(0);
      expect(signals.listenerCount("SIGTERM")).toBe(0);
      expect(input.listenerCount("end")).toBe(0);
      expect(input.listenerCount("close")).toBe(0);
      expect(transport.onclose).toBe(originalOnClose);
      expect(originalOnClose).toHaveBeenCalledTimes(trigger === "transport_close" ? 1 : 0);
    } finally {
      clearTimeout(released);
      lock.release();
      lease.release();
      await publisher.close();
      fs.rmSync(root, { recursive: true, force: true });
    }
  });

  it("rejects cleanup failure without leaking signal/input listeners", async () => {
    const signals = new EventEmitter();
    const input = new PassThrough();
    const failure = new Error("owned cleanup failure");
    const shutdown = waitForShutdown(async () => { throw failure; }, { signals, input });
    const observed = expect(shutdown).rejects.toBe(failure);
    signals.emit("SIGTERM");
    await observed;
    expect(signals.eventNames()).toEqual([]);
    expect(input.eventNames()).not.toContain("end");
  });

  it("wires the same context cleanup to stdio EOF/transport closure and HTTP signals", () => {
    const source = fs.readFileSync(path.join(process.cwd(), "src/cli.ts"), "utf8");
    expect(source).toContain("await context.testRunOperationPublisher?.close()");
    expect(source).toContain("const transport = new StdioServerTransport()");
    expect(source).toContain("{ input: process.stdin, transport }");
    expect(source.match(/const contextClosed = closeContext\(\)/g)).toHaveLength(2);
  });
});
