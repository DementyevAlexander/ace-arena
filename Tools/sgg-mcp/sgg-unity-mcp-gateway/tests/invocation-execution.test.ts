import { inspect } from "node:util";

import { describe, expect, it, vi } from "vitest";

import { executePreparedInvocation, InvocationOutcome, type InvocationExecutionStages } from "../src/mcp/invocation-execution.js";
import { fail, ok } from "../src/mcp/results.js";

describe("prepared invocation execution", () => {
  it.each([
    ["blocked", ["refresh", "gate"]],
    ["local", ["refresh", "gate", "local"]],
    ["official", ["refresh", "gate", "local", "official"]],
    ["bridge", ["refresh", "gate", "local", "bridge"]],
  ] as const)("preserves %s route order and invokes one terminal route", async (route, expectedOrder) => {
    const order: string[] = [];
    const stages: InvocationExecutionStages = {
      refreshPolicy: vi.fn(() => { order.push("refresh"); }),
      degradedGate: vi.fn(() => { order.push("gate"); return route === "blocked" ? fail({ error: { code: "policy_degraded", message: "blocked" } }) : undefined; }),
      invokeLocal: vi.fn(() => { order.push("local"); return route === "local" ? ok({ data: { source: "local" } }) : undefined; }),
      official: route === "official",
      invokeOfficial: vi.fn(async () => { order.push("official"); return new InvocationOutcome("official", ok({ data: { source: "official" } }), 2); }),
      invokeBridge: vi.fn(async () => { order.push("bridge"); return new InvocationOutcome("bridge", ok({ data: { source: "bridge" } }), 3); }),
    };

    const outcome = await executePreparedInvocation(stages);

    expect(outcome.route).toBe(route);
    expect(order).toEqual(expectedOrder);
    expect(stages.invokeOfficial).toHaveBeenCalledTimes(route === "official" ? 1 : 0);
    expect(stages.invokeBridge).toHaveBeenCalledTimes(route === "bridge" ? 1 : 0);
  });

  it("propagates a terminal failure without replay", async () => {
    const invokeBridge = vi.fn(async () => { throw new Error("transport failed"); });

    await expect(executePreparedInvocation({
      refreshPolicy: () => undefined,
      degradedGate: () => undefined,
      invokeLocal: () => undefined,
      official: false,
      invokeOfficial: async () => new InvocationOutcome("official", ok({ data: {} })),
      invokeBridge,
    })).rejects.toThrow("transport failed");
    expect(invokeBridge).toHaveBeenCalledTimes(1);
  });

  it("does not serialize or inspect results, args, tokens, or roots", () => {
    const outcome = new InvocationOutcome("bridge", ok({ data: { args: { token: "secret" }, root: "C:/private/project" } }), 5);

    expect(JSON.stringify(outcome)).toBe('{"route":"bridge","status":"ok","bridgeDurationPresent":true}');
    expect(inspect(outcome)).not.toMatch(/secret|private|project|token|args|root/i);
    expect(Object.isFrozen(outcome)).toBe(true);
  });
});
