import { inspect } from "node:util";

import { describe, expect, it, vi } from "vitest";

import { dispatchTool } from "../src/mcp/dispatcher.js";
import { AuditContext, writeInvocationAudit } from "../src/observability/audit-stage.js";
import { createAuditEvent, type AuditSink } from "../src/observability/audit.js";
import { ok } from "../src/mcp/results.js";

describe("dispatcher audit stage", () => {
  it("keeps audit event schema, redaction, and field order at parity", async () => {
    const now = () => new Date("2026-07-12T10:00:00.000Z");
    const rawArgs = { project: "game", command: "compile.status", args_json: '{"token":"secret"}' };
    const result = ok({ project: "game", command: "compile.status", data: { ready: true } });
    const metadata = { command: { risk: "read" as const, idempotency: "safe" as const, timeout_ms: 1_000 } };
    const expected = createAuditEvent("sgg_unity_call", rawArgs, result, now().getTime(), now, metadata, "run-1", "audit-1");
    const context = AuditContext.create({ tool: "sgg_unity_call", rawArgs, result, startedAtMs: now().getTime(), now, metadata, runId: "run-1", auditId: "audit-1" });
    const events: unknown[] = [];

    await writeInvocationAudit({ prepare: () => {}, write: (event) => { events.push(event); } }, context);

    expect(events).toEqual([expected]);
    expect(Object.keys(events[0] as object)).toEqual(Object.keys(expected));
    expect(JSON.stringify(events)).not.toContain("secret");
  });

  it("is an immutable safe snapshot without args, tokens, or roots in serialization or inspection", () => {
    const context = AuditContext.create({
      tool: "sgg_unity_call",
      rawArgs: { project: "C:/private/root", args_json: '{"token":"secret"}' },
      result: ok({ data: {} }),
      startedAtMs: 0,
      now: () => new Date(0),
    });

    expect(Object.isFrozen(context)).toBe(true);
    expect(Object.isFrozen(context.event)).toBe(true);
    expect(JSON.stringify(context)).not.toMatch(/secret|private|root|token|args/i);
    expect(inspect(context)).not.toMatch(/secret|private|root|token|args/i);
  });

  it("writes only after dispatch completes and leaves audit failures observable", async () => {
    const order: string[] = [];
    const sink: AuditSink = { prepare: () => {}, write: vi.fn(() => { order.push("audit"); throw new Error("audit failed"); }) };
    const registry = { captureSnapshot: vi.fn(() => { order.push("dispatch"); return { records: [], getStats: () => ({ registryOperationalState: "ok" }) }; }) };

    await expect(dispatchTool("sgg_unity_projects", {}, { registry: registry as never, audit: sink })).rejects.toThrow("audit failed");
    expect(order).toEqual(["dispatch", "audit"]);
    expect(sink.write).toHaveBeenCalledTimes(1);
  });

  it("does not dispatch when mandatory audit preflight fails", async () => {
    const registry = { captureSnapshot: vi.fn(() => ({ records: [], getStats: () => ({ registryOperationalState: "ok" }) })) };
    const sink: AuditSink = { prepare: () => { throw new Error("audit unavailable"); }, write: vi.fn() };

    await expect(dispatchTool("sgg_unity_projects", {}, { registry: registry as never, audit: sink })).rejects.toThrow("audit unavailable");
    expect(registry.captureSnapshot).not.toHaveBeenCalled();
    expect(sink.write).not.toHaveBeenCalled();
  });

  it("durably admits a bounded start identity before dispatch and correlates the terminal record", async () => {
    const order: string[] = [];
    let startEvent: Parameters<AuditSink["prepare"]>[0] | undefined;
    const sink: AuditSink = {
      prepare: (event) => { startEvent = event; order.push("start"); },
      write: (event) => { order.push("terminal"); expect(event.audit_id).toBe(startEvent?.audit_id); expect(event.phase).toBe("terminal"); },
    };
    const registry = { captureSnapshot: () => { order.push("dispatch"); return { records: [], getStats: () => ({ registryOperationalState: "ok" }) }; } };

    await dispatchTool("sgg_unity_projects", {}, { registry: registry as never, audit: sink, now: () => new Date(0) });

    expect(startEvent).toMatchObject({ schema_version: 1, phase: "start", status: "ok", output_chars: 0, audit_id: expect.stringMatching(/^[0-9a-f-]{36}$/) });
    expect(order).toEqual(["start", "dispatch", "terminal"]);
  });
});
