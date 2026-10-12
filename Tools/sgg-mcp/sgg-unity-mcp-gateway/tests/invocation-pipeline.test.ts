import { inspect } from "node:util";

import { describe, expect, it, vi } from "vitest";

import type { CommandDescriptor } from "../src/bridge/bridge-client.js";
import { InvocationContextSnapshot } from "../src/mcp/invocation-context.js";
import { buildInvocationPlan, ReadyInvocationPlan, type InvocationPipelineStages } from "../src/mcp/invocation-pipeline.js";
import { fail } from "../src/mcp/results.js";

const descriptor: CommandDescriptor = {
  id: "asset.import",
  title: "Import",
  description: "Import an asset",
  package: "builtin",
  risk: "write",
  idempotency: "unsafe",
  timeout_ms: 1_000,
  schema: { type: "object", properties: { token: { type: "string" } } },
};

const invocation = InvocationContextSnapshot.create({
  projectSelector: "C:/private/project",
  project: "game",
  command: descriptor.id,
  descriptor,
  commandsHash: null,
});

describe("typed invocation decision pipeline", () => {
  it("runs resolution, policy, validation, and approval in the established order", async () => {
    const order: string[] = [];
    const resolved = { descriptor, secret: "not-serializable" };
    const plan = await buildInvocationPlan({
      resolve: async () => { order.push("resolve"); return { state: "resolved", value: resolved }; },
      applyPolicy: (value) => { order.push("policy"); return { state: "permitted", value }; },
      validate: (value) => { order.push("validate"); return new ReadyInvocationPlan(invocation, value); },
      approve: () => { order.push("approval"); return { state: "approved" }; },
    });

    expect(order).toEqual(["resolve", "policy", "validate", "approval"]);
    expect(plan.state).toBe("ready");
    if (plan.state === "ready") {
      expect(plan.resolved).toBe(resolved);
      expect(JSON.stringify(plan)).not.toMatch(/private|secret|schema|token|Import/);
      expect(inspect(plan)).not.toMatch(/private|secret|schema|token|Import/);
    }
  });

  it.each(["resolve", "policy", "validate", "approval"] as const)("stops after a blocked %s decision", async (blockedStage) => {
    const blocked = { state: "blocked" as const, result: fail({ error: { code: "permission_denied", message: "blocked" } }) };
    const stages: InvocationPipelineStages<{ descriptor: CommandDescriptor }> = {
      resolve: vi.fn(async () => blockedStage === "resolve" ? blocked : { state: "resolved" as const, value: { descriptor } }),
      applyPolicy: vi.fn((value) => blockedStage === "policy" ? blocked : { state: "permitted" as const, value }),
      validate: vi.fn((value) => blockedStage === "validate" ? blocked : new ReadyInvocationPlan(invocation, value)),
      approve: vi.fn(() => blockedStage === "approval" ? blocked : { state: "approved" as const }),
    };

    await expect(buildInvocationPlan(stages)).resolves.toEqual(blocked);
    expect(stages.resolve).toHaveBeenCalledTimes(1);
    expect(stages.applyPolicy).toHaveBeenCalledTimes(blockedStage === "resolve" ? 0 : 1);
    expect(stages.validate).toHaveBeenCalledTimes(["resolve", "policy"].includes(blockedStage) ? 0 : 1);
    expect(stages.approve).toHaveBeenCalledTimes(blockedStage === "approval" ? 1 : 0);
  });
});
