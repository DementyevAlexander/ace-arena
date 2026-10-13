import { describe, expect, it } from "vitest";

import { ProjectQueueManager } from "../src/routing/project-queue.js";

describe("ProjectQueueManager", () => {
  it("serializes work inside one Unity project", async () => {
    const queues = new ProjectQueueManager();
    const events: string[] = [];
    let releaseFirst!: () => void;
    let firstStarted!: () => void;
    const firstStartedPromise = new Promise<void>((resolve) => {
      firstStarted = resolve;
    });
    const firstReleasePromise = new Promise<void>((resolve) => {
      releaseFirst = resolve;
    });

    const first = queues.run("game", async () => {
      events.push("first-start");
      firstStarted();
      await firstReleasePromise;
      events.push("first-end");
      return "first";
    });

    const second = queues.run("game", async () => {
      events.push("second-start");
      return "second";
    });

    await firstStartedPromise;
    await flushPromises();
    expect(events).toEqual(["first-start"]);

    releaseFirst();
    await expect(Promise.all([first, second])).resolves.toEqual(["first", "second"]);
    expect(events).toEqual(["first-start", "first-end", "second-start"]);
  });

  it("allows different project queues to run concurrently", async () => {
    const queues = new ProjectQueueManager();
    const events: string[] = [];
    let release!: () => void;
    const releasePromise = new Promise<void>((resolve) => {
      release = resolve;
    });

    const game = queues.run("game", async () => {
      events.push("game-start");
      await releasePromise;
      events.push("game-end");
    });

    const tools = queues.run("tools", async () => {
      events.push("tools-start");
      await releasePromise;
      events.push("tools-end");
    });

    await flushPromises();
    expect(events).toEqual(["game-start", "tools-start"]);

    release();
    await Promise.all([game, tools]);
    expect(events).toEqual(["game-start", "tools-start", "game-end", "tools-end"]);
  });

  it("exposes only the active owner metadata and clears it after success or failure", async () => {
    let now = 1_000;
    const queues = new ProjectQueueManager(() => now);
    let release!: () => void;
    const blocker = new Promise<void>((resolve) => { release = resolve; });
    const first = queues.run("game", async () => blocker, {
      command: "compile.refresh_and_wait",
      risk: "write",
      idempotency: "idempotent",
      deadline_unix_ms: 10_000,
      args_json: "{\"token\":\"queue-owner-secret\"}",
      project_path: "C:/private/game",
      prior_owner: "test.run",
    } as never);

    await flushPromises();
    expect(queues.activeOwner("game")).toEqual({
      command: "compile.refresh_and_wait",
      risk: "write",
      idempotency: "idempotent",
      deadline_unix_ms: 10_000,
      enqueued_at_unix_ms: 1_000,
      admitted_at_unix_ms: 1_000,
    });
    expect(queues.activeOwner("tools")).toBeUndefined();
    expect(JSON.stringify(queues.activeOwner("game"))).not.toMatch(/queue-owner-secret|private|prior_owner|args_json/);

    now = 2_000;
    release();
    await first;
    expect(queues.activeOwner("game")).toBeUndefined();

    const failed = queues.run("game", async () => { throw new Error("expected"); }, {
      command: "test.run",
      risk: "execute",
      idempotency: "unsafe",
    });
    await expect(failed).rejects.toThrow("expected");
    expect(queues.activeOwner("game")).toBeUndefined();
  });
});

function flushPromises(): Promise<void> {
  return new Promise((resolve) => {
    setImmediate(resolve);
  });
}
