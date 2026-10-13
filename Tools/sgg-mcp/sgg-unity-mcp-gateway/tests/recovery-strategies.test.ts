import { inspect } from "node:util";

import { describe, expect, it } from "vitest";

import {
  RecoveryContext,
  RecoveryStrategyResult,
  selectRecoveryStrategy,
  type RecoveryHandlers,
  type RecoveryStrategyKind,
} from "../src/mcp/recovery-strategies.js";
import type { GatewayResult } from "../src/mcp/results.js";

describe("dispatcher recovery strategies", () => {
  it.each([
    ["compile.status", "wait", false, "read"],
    ["compile.wait_ready", "wait", false, "read"],
    ["test.run.status", "wait", false, "read"],
    ["compile.refresh_and_wait", "wait", false, "compile_refresh"],
    ["validation.after_edit", "wait", false, "validation_after_edit"],
    ["test.run", "wait", false, "risk_aware"],
    ["asset.import", "wait", false, "risk_aware"],
    ["compile.status", "job", false, "no_automatic_recovery"],
    ["compile.status", "wait", true, "no_automatic_recovery"],
    ["unknown.command", "wait", false, "risk_aware"],
  ] as const)("selects existing recovery for %s/%s", (command, mode, acceptedPending, expected) => {
    const context = RecoveryContext.call({ command, mode, acceptedPending });

    expect(selectRecoveryStrategy(context).kind).toBe(expected);
  });

  it("selects bounded read recovery for describe", () => {
    expect(selectRecoveryStrategy(RecoveryContext.describe()).kind).toBe("read");
  });

  it("executes exactly one selected handler", async () => {
    const calls: RecoveryStrategyKind[] = [];
    const result: GatewayResult = { status: "error", error: { code: "bridge_timeout", message: "bridge_timeout" }, diagnostics: [] };
    const handler = (kind: RecoveryStrategyKind) => async (): Promise<GatewayResult> => {
      calls.push(kind);
      return result;
    };
    const handlers: RecoveryHandlers = {
      read: handler("read"),
      risk_aware: handler("risk_aware"),
      compile_refresh: handler("compile_refresh"),
      validation_after_edit: handler("validation_after_edit"),
      no_automatic_recovery: handler("no_automatic_recovery"),
    };
    const context = RecoveryContext.call({ command: "test.run", mode: "wait", acceptedPending: false });
    const strategy = selectRecoveryStrategy(context);

    const outcome = await strategy.execute(context, handlers);

    expect(calls).toEqual(["risk_aware"]);
    expect(outcome.result).toBe(result);
  });

  it("keeps context and result inspection free of invocation data", () => {
    const context = RecoveryContext.call({ command: "asset.import", mode: "wait", acceptedPending: false });
    const result = new RecoveryStrategyResult("no_automatic_recovery", context, {
      status: "ok",
      data: { args: { token: "secret", project_root: "C:/secret/project" } },
      diagnostics: [],
    });
    const inspected = `${JSON.stringify(context)} ${inspect(context)} ${JSON.stringify(result)} ${inspect(result)}`;

    expect(Object.isFrozen(context)).toBe(true);
    expect(Object.isFrozen(result)).toBe(true);
    expect(inspected).toContain("asset.import");
    expect(inspected).not.toContain("secret");
    expect(inspected).not.toContain("project_root");
    expect(inspected).not.toContain("args");
  });
});
