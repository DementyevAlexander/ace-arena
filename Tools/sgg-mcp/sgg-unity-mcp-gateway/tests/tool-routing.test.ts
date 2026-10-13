import { inspect } from "node:util";

import { describe, expect, it, vi } from "vitest";

import { BatchRoute, InvocationRoute, JobRoute, LogsRoute } from "../src/mcp/tool-routing.js";

describe("typed tool routing", () => {
  it.each([
    ["wait", "wait"],
    ["job", "job"],
  ] as const)("routes invocation mode %s exactly once", (mode, expected) => {
    const wait = vi.fn(() => "wait");
    const job = vi.fn(() => "job");

    expect(new InvocationRoute(mode).execute({ wait, job })).toBe(expected);
    expect(wait.mock.calls.length + job.mock.calls.length).toBe(1);
  });

  it("preserves sequential execution, failures, and partial results", async () => {
    const events: string[] = [];
    const items = [
      async () => { events.push("first"); return { status: "ok" }; },
      async () => { events.push("second"); return { status: "error" }; },
      async () => { events.push("third"); return { status: "ok" }; },
    ];

    const results = await new BatchRoute("sequential", items.length).execute({
      sequential: async () => {
        const output = [];
        for (const item of items) output.push(await item());
        return output;
      },
      parallelByProject: () => Promise.reject(new Error("wrong route")),
    });

    expect(events).toEqual(["first", "second", "third"]);
    expect(results).toEqual([{ status: "ok" }, { status: "error" }, { status: "ok" }]);
  });

  it("preserves parallel result ordering while different project work overlaps", async () => {
    const events: string[] = [];
    let releaseFirst!: () => void;
    const firstBlocked = new Promise<void>((resolve) => { releaseFirst = resolve; });
    const tasks = [
      async () => { events.push("game-start"); await firstBlocked; return "game"; },
      async () => { events.push("tools-start"); releaseFirst(); return "tools"; },
    ];

    const results = await new BatchRoute("parallel_by_project", tasks.length).execute({
      sequential: () => Promise.reject(new Error("wrong route")),
      parallelByProject: () => Promise.all(tasks.map((task) => task())),
    });

    expect(events).toEqual(["game-start", "tools-start"]);
    expect(results).toEqual(["game", "tools"]);
  });

  it.each(["status", "cancel"] as const)("routes job action %s exactly once", (action) => {
    const status = vi.fn(() => "status");
    const cancel = vi.fn(() => "cancel");

    expect(new JobRoute(action).execute({ status, cancel })).toBe(action);
    expect(status.mock.calls.length + cancel.mock.calls.length).toBe(1);
  });

  it("forwards the validated log kind and bound exactly once", () => {
    const read = vi.fn((kind, limit) => ({ kind, limit }));

    expect(new LogsRoute("jobs", 1000).execute(read)).toEqual({ kind: "jobs", limit: 1000 });
    expect(read).toHaveBeenCalledOnce();
  });

  it("keeps route inspection immutable and free of arguments, tokens, roots, and outputs", () => {
    const routes = [
      new InvocationRoute("job"),
      new BatchRoute("parallel_by_project", 2),
      new JobRoute("cancel"),
      new LogsRoute("bridge", 200),
    ];
    const serialized = `${JSON.stringify(routes)} ${routes.map((route) => inspect(route)).join(" ")}`;

    expect(routes.every(Object.isFrozen)).toBe(true);
    expect(serialized).not.toMatch(/args|token|root|output|result|projectSelector|project:/i);
    expect(serialized).toContain("parallel_by_project");
  });
});
