import { describe, expect, it } from "vitest";

import { InMemoryJobManager } from "../src/jobs/job-manager.js";

describe("InMemoryJobManager", () => {
  it("tracks job lifecycle and stores completed results", async () => {
    const manager = new InMemoryJobManager(() => new Date("2026-05-12T10:00:00.000Z"));
    const started = manager.start({
      project: "game",
      command: "asset.index.rebuild",
      task: async () => ({ status: "ok", project: "game", command: "asset.index.rebuild", data: { rebuilt: true }, diagnostics: [] }),
    });

    expect(started.status).toBe("running");
    await new Promise((resolve) => setTimeout(resolve, 0));

    const completed = manager.get("game", started.job_id);
    expect(completed).toMatchObject({ status: "succeeded", result: { status: "ok", data: { rebuilt: true } } });
  });

  it("marks running jobs as cancel requested", () => {
    const manager = new InMemoryJobManager(() => new Date("2026-05-12T10:00:00.000Z"));
    const started = manager.start({
      project: "game",
      command: "asset.index.rebuild",
      task: () => new Promise(() => undefined),
    });

    const cancelled = manager.cancel("game", started.job_id);
    expect(cancelled).toMatchObject({ status: "cancel_requested", cancel_requested: true });
  });
});
