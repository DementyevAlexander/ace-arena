import { EventEmitter } from "node:events";

import { describe, expect, it, vi } from "vitest";

import { OperationalLogger, type OperationalLogWriter } from "../src/observability/operational-logger.js";

const now = () => new Date("2026-07-12T10:20:30.456Z");

class FakeWriter extends EventEmitter implements OperationalLogWriter {
  readonly lines: string[] = [];
  results: boolean[] = [];
  error: Error | undefined;

  write(line: string): boolean {
    if (this.error) throw this.error;
    this.lines.push(line);
    return this.results.shift() ?? true;
  }
}

describe("OperationalLogger", () => {
  it("filters by configured level and reports aggregate counters", () => {
    const writer = new FakeWriter();
    const logger = new OperationalLogger({ level: "warn", now, writer });

    logger.log("SGG-OP-STARTUP-001", "stdio", "warn", true, false, false, "2026.7.11-44");
    logger.log("SGG-OP-SESSION-CAPACITY-001", 2, 2);

    expect(writer.lines).toHaveLength(1);
    expect(logger.status()).toEqual({ schema_version: 1, written: 1, dropped: 1, errors: 0 });
  });

  it("emits a deterministic null-prototype schema with only generated run correlation", () => {
    const writer = new FakeWriter();
    const logger = new OperationalLogger({ level: "debug", runId: "11111111-1111-4111-8111-111111111111", now, writer });

    logger.log("SGG-OP-BRIDGE-FAILURE-001", "invoke", "bridge_timeout", "timeout");

    expect(writer.lines[0]).toBe('{"schema_version":1,"timestamp":"2026-07-12T10:20:30.456Z","level":"warn","event_code":"SGG-OP-BRIDGE-FAILURE-001","run_id":"11111111-1111-4111-8111-111111111111","operation":"invoke","error_code":"bridge_timeout","failure_kind":"timeout"}\n');
    expect(writer.lines[0]).not.toContain("request_id");
    expect(writer.lines[0]).not.toContain("correlation_id");
  });

  it("rejects hostile values without invoking getters, proxy traps, or own toJSON", () => {
    const writer = new FakeWriter();
    const logger = new OperationalLogger({ level: "debug", now, writer });
    let getterCalls = 0;
    let proxyTraps = 0;
    let toJsonCalls = 0;
    const accessor = Object.defineProperty({}, "value", { get: () => { getterCalls += 1; return "secret"; } });
    const proxied = new Proxy({}, {
      get: () => { proxyTraps += 1; return "secret"; },
      getOwnPropertyDescriptor: () => { proxyTraps += 1; return undefined; },
      getPrototypeOf: () => { proxyTraps += 1; return Object.prototype; },
      ownKeys: () => { proxyTraps += 1; return []; },
    });
    const ownToJson = { toJSON: () => { toJsonCalls += 1; return "secret"; } };

    logger.log("SGG-OP-HTTP-LISTEN-001", accessor);
    logger.log("SGG-OP-HTTP-LISTEN-001", proxied);
    logger.log("SGG-OP-HTTP-LISTEN-001", ownToJson);

    expect({ getterCalls, proxyTraps, toJsonCalls }).toEqual({ getterCalls: 0, proxyTraps: 0, toJsonCalls: 0 });
    expect(writer.lines.every((line) => line.includes('"event_code":"SGG-OP-LOGGER-001"') && !line.includes("secret"))).toBe(true);
  });

  it("does not invoke polluted Object.prototype.toJSON when serializing valid events", () => {
    const writer = new FakeWriter();
    const logger = new OperationalLogger({ level: "debug", now, writer });
    let calls = 0;
    Object.defineProperty(Object.prototype, "toJSON", { configurable: true, value: () => { calls += 1; return { secret: "polluted" }; } });
    try {
      logger.log("SGG-OP-HTTP-LISTEN-001", 27182);
    } finally {
      delete (Object.prototype as { toJSON?: unknown }).toJSON;
    }

    expect(calls).toBe(0);
    expect(writer.lines[0]).toContain('"port":27182');
    expect(writer.lines[0]).not.toContain("polluted");
  });

  it("rejects oversized, nested, non-scalar, and malformed field counts", () => {
    const writer = new FakeWriter();
    const logger = new OperationalLogger({ level: "debug", runId: "/private/run", now, writer });

    logger.log("SGG-OP-HTTP-LISTEN-001", 70_000);
    logger.log("SGG-OP-SESSION-CAPACITY-001", { value: 1 }, 2);
    logger.log("SGG-OP-HTTP-LISTEN-001", 27182, "extra");

    expect(writer.lines.every((line) => !line.includes("private") && line.includes('"redacted":true'))).toBe(true);
  });

  it("uses the fixed redaction record for unknown codes and serialization failures", () => {
    const writer = new FakeWriter();
    const logger = new OperationalLogger({ level: "debug", now, writer });

    logger.log("unknown" as never);
    const stringify = vi.spyOn(JSON, "stringify").mockImplementationOnce(() => { throw new Error("serialization secret"); });
    logger.log("SGG-OP-HTTP-LISTEN-001", 27182);
    stringify.mockRestore();

    expect(writer.lines).toHaveLength(2);
    expect(writer.lines.every((line) => line.includes('"event_code":"SGG-OP-LOGGER-001"') && !line.includes("secret"))).toBe(true);
    expect(logger.status()).toEqual({ schema_version: 1, written: 2, dropped: 0, errors: 2 });
  });

  it("drops while stderr is blocked and attaches only one bounded drain listener", () => {
    const writer = new FakeWriter();
    writer.results.push(false, true);
    const logger = new OperationalLogger({ level: "debug", now, writer });

    logger.log("SGG-OP-HTTP-LISTEN-001", 27182);
    logger.log("SGG-OP-HTTP-LISTEN-001", 27183);
    logger.log("SGG-OP-HTTP-LISTEN-001", 27184);
    expect(writer.listenerCount("drain")).toBe(1);
    expect(writer.lines).toHaveLength(1);
    writer.emit("drain");
    logger.log("SGG-OP-HTTP-LISTEN-001", 27185);

    expect(writer.lines).toHaveLength(2);
    expect(writer.listenerCount("drain")).toBe(0);
    expect(logger.status()).toEqual({ schema_version: 1, written: 2, dropped: 2, errors: 0 });
  });

  it("contains synchronous and asynchronous writer failures", () => {
    const throwing = new FakeWriter();
    throwing.error = new Error("sink secret");
    const syncLogger = new OperationalLogger({ level: "debug", now, writer: throwing });
    expect(() => syncLogger.log("SGG-OP-AUDIT-STATE-001", "unavailable")).not.toThrow();
    expect(syncLogger.status()).toEqual({ schema_version: 1, written: 0, dropped: 1, errors: 1 });
    syncLogger.log("SGG-OP-AUDIT-STATE-001", "unavailable");
    expect(syncLogger.status()).toEqual({ schema_version: 1, written: 0, dropped: 2, errors: 1 });

    const asyncWriter = new FakeWriter();
    const asyncLogger = new OperationalLogger({ level: "debug", now, writer: asyncWriter });
    asyncLogger.log("SGG-OP-HTTP-LISTEN-001", 27182);
    expect(() => asyncWriter.emit("error", new Error("async secret"))).not.toThrow();
    expect(asyncLogger.status()).toEqual({ schema_version: 1, written: 1, dropped: 0, errors: 1 });
    asyncLogger.log("SGG-OP-HTTP-LISTEN-001", 27183);
    expect(asyncLogger.status()).toEqual({ schema_version: 1, written: 1, dropped: 1, errors: 1 });
    expect(() => asyncWriter.emit("error", new Error("repeated async secret"))).not.toThrow();
    expect(asyncLogger.status()).toEqual({ schema_version: 1, written: 1, dropped: 1, errors: 2 });
    asyncLogger.log("SGG-OP-HTTP-LISTEN-001", 27184);
    expect(asyncLogger.status()).toEqual({ schema_version: 1, written: 1, dropped: 2, errors: 2 });
  });

  it("uses stderr only by default", () => {
    const stderr = vi.spyOn(process.stderr, "write").mockImplementation(() => true);
    const stdout = vi.spyOn(process.stdout, "write").mockImplementation(() => true);
    const logger = new OperationalLogger({ level: "info", now });

    logger.log("SGG-OP-HTTP-LISTEN-001", 27182);

    expect(stderr).toHaveBeenCalledOnce();
    expect(stdout).not.toHaveBeenCalled();
  });
});
