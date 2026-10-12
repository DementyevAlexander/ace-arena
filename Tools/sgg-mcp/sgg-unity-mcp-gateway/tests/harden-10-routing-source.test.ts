import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

describe("HARDEN-10 routing architecture", () => {
  it("keeps routing typed while security, approval, invocation, recovery, and audit stages remain central", () => {
    const dispatcher = fs.readFileSync(path.join(process.cwd(), "src/mcp/dispatcher.ts"), "utf8");
    const routing = fs.readFileSync(path.join(process.cwd(), "src/mcp/tool-routing.ts"), "utf8");

    for (const route of ["InvocationRoute", "BatchRoute", "JobRoute", "LogsRoute"]) {
      expect(dispatcher).toContain(`new ${route}(`);
      expect(routing).toContain(`class ${route}`);
    }
    expect(dispatcher).not.toContain('args.mode === "job"');
    expect(dispatcher).not.toContain('args.mode === "sequential"');
    expect(dispatcher).not.toContain('args.action === "cancel"');
    expect(dispatcher.match(/buildInvocationPlan\(/g)).toHaveLength(1);
    // Declaration, initial admission, fallback admission and the exact-popup
    // pre-issuance guard all share the same central approval implementation.
    expect(dispatcher.match(/checkInvocationPermission\(/g)).toHaveLength(4);
    expect(dispatcher).toContain("checkInvocationPermission(context, fallbackInvocation, args.mode, args.request_id, commandArgs)");
    const guardStart = dispatcher.indexOf("function revalidateKnownPopupAutoApproval(");
    const guardEnd = dispatcher.indexOf("\nasync function invokePreparedCommand(", guardStart);
    expect(guardStart).toBeGreaterThan(0);
    expect(guardEnd).toBeGreaterThan(guardStart);
    const guard = dispatcher.slice(guardStart, guardEnd);
    expect(guard.match(/checkInvocationPermission\(/g)).toHaveLength(1);
    expect(guard).toContain("if (!isKnownUnityPopupResponse(invocation.descriptor)) return undefined;");
    expect(guard).toContain("refreshManagedPolicy(context)");
    expect(guard).toContain("applyInvocationPolicy(context,");
    expect(guard).toContain('checkInvocationPermission(context, invocation, context.approvedKnownPopupMode ?? "wait", requestId, commandArgs)');
    expect(dispatcher.match(/executePreparedInvocation\(/g)).toHaveLength(1);
    expect(dispatcher.match(/writeInvocationAudit\(/g)).toHaveLength(1);
    expect(dispatcher).toContain("selectRecoveryStrategy(recoveryContext)");
  });
});
