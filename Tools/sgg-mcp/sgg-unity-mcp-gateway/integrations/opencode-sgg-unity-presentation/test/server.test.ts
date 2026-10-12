import fs from "node:fs";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";

import type { Plugin } from "@opencode/plugin";
import { Error as ToolError, type Info, type ToolContext, type ToolDomain, type ToolEditor } from "@opencode/plugin/promise/tool";
import { describe, expect, test, vi } from "vitest";

import plugin from "../src/server.js";

type AfterEvent = Extract<Parameters<Parameters<ToolDomain["hook"]>[1]>[0], { status: "completed" | "error" }>;
const callTool = "sgg-unity-mcp_sgg_unity_call";
const label = "Checking Unity compile status";

function createHost(options: Plugin.Context["options"] = {}, base: (Info & { id: string })[] = []) {
  let after: ((event: AfterEvent) => Promise<void> | void) | undefined;
  let transform: ((editor: ToolEditor) => void) | undefined;
  let active: (Info & { id: string })[] = [];
  const dispose = vi.fn(async () => {});
  const tool: ToolDomain = {
    hook: vi.fn(async (name, callback) => {
      expect(name).toBe("execute.after");
      after = callback as typeof after;
      return { dispose };
    }),
    transform: vi.fn(async (callback) => {
      transform = callback;
      replay();
      return { dispose };
    }),
    reload: vi.fn(async () => replay()),
    list: async () => active,
  };
  function replay() {
    active = base.map((entry) => ({ ...entry }));
    const editor: ToolEditor = {
      list: () => active,
      get: (id) => active.find((entry) => entry.id === id),
      update: (id, update) => { const entry = active.find((value) => value.id === id); if (entry) update(entry); },
      add: () => { throw new Error("must not add tools"); },
      remove: () => { throw new Error("must not remove tools"); },
      namespace: () => { throw new Error("must not change namespaces"); },
    };
    transform?.(editor);
  }
  return {
    // The test host exposes only the public domains the plugin is allowed to use.
    context: { options, tool } as Plugin.Context,
    after: (event: AfterEvent) => after!(event),
    active: () => active,
    dispose,
  };
}

function event(target: object, status: "completed" | "error" = "completed", tool = callTool, input: unknown = { command: "compile.status" }): AfterEvent {
  const value = { tool, input, status, [status === "completed" ? "result" : "error"]: target };
  for (const key of ["sessionID", "id", "agent", "messageID"]) {
    Object.defineProperty(value, key, { get() { throw new Error(`lifecycle ID read: ${key}`); } });
  }
  return value as AfterEvent;
}

describe("native V2 presentation plugin", () => {
  test("uses a stable Plugin.define/setup entrypoint and the native main export", async () => {
    expect(plugin.id).toBe("sgg-unity.presentation");
    expect(plugin.setup).toBeTypeOf("function");
    expect(plugin).not.toHaveProperty("server");
    expect(plugin).not.toHaveProperty("tui");
    const packageRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
    const packageJson = JSON.parse(fs.readFileSync(path.join(packageRoot, "package.json"), "utf8"));
    expect(packageJson.exports).toEqual({ ".": "./src/server.ts" });
    expect(packageJson.dependencies).toEqual({ "@opencode/plugin": "2.0.25" });
    expect(packageJson.engines.opencode).toBe(">=2.0.25 <3");
    const loaded = await import(pathToFileURL(path.resolve(packageRoot, packageJson.exports["."])).href);
    expect(loaded.default).toBe(plugin);
    const host = createHost();
    await plugin.setup(host.context);
    expect(host.context.tool.hook).toHaveBeenCalledOnce();
    expect(host.context.tool.transform).toHaveBeenCalledOnce();
    // Registrations are disposed by the V2 plugin runtime; no timers or subscriptions are owned here.
    expect(host.dispose).not.toHaveBeenCalled();
  });

  test("adds only namespaced metadata without reading input secrets or canonical payloads", async () => {
    const host = createHost();
    await plugin.setup(host.context);
    const input = { command: "compile.status" };
    for (const key of ["args_json", "project", "request_id", "checkpoint_id", "plan_id"]) {
      Object.defineProperty(input, key, { get() { throw new Error(`secret input read: ${key}`); } });
    }
    const metadata = { retained: { value: true }, description: "existing description" };
    const result = { metadata };
    for (const key of ["content", "output", "structuredContent", "error", "isError"]) {
      Object.defineProperty(result, key, { get() { throw new Error(`canonical payload read: ${key}`); } });
    }
    await host.after(event(result, "completed", callTool, Object.freeze(input)));
    expect(result.metadata).toBe(metadata);
    expect(metadata).toEqual({
      retained: { value: true }, description: "existing description",
      sggPresentation: { schemaVersion: 1, label, outcome: "completed" },
    });
  });

  test("annotates typed V2 failures without reading or changing their message/error", async () => {
    const host = createHost();
    await plugin.setup(host.context);
    const error = new ToolError({ message: "canonical failure", metadata: { retained: true } });
    await host.after(event(error, "error"));
    expect(error.message).toBe("canonical failure");
    expect(error.metadata).toEqual({ retained: true, sggPresentation: { schemaVersion: 1, label, outcome: "error" } });
    const hostileError = {};
    for (const key of ["message", "error", "content", "output"]) {
      Object.defineProperty(hostileError, key, { get() { throw new Error(`failure payload read: ${key}`); } });
    }
    await host.after(event(hostileError, "error"));
    expect(Object.getOwnPropertyDescriptor(hostileError, "metadata")?.value).toEqual({ sggPresentation: { schemaVersion: 1, label, outcome: "error" } });
  });

  test("fails open for immutable, malformed, accessor, and colliding metadata", async () => {
    const host = createHost();
    await plugin.setup(host.context);
    const collision = { sggPresentation: { owner: "another registration" } };
    const targets = [Object.freeze({}), { metadata: Object.freeze({}) }, { metadata: [] }, { metadata: null }, { metadata: collision }];
    const accessor = Object.defineProperty({}, "metadata", { get() { throw new Error("metadata getter read"); } });
    for (const target of [...targets, accessor]) await expect(Promise.resolve(host.after(event(target)))).resolves.toBeUndefined();
    expect(collision).toEqual({ sggPresentation: { owner: "another registration" } });
  });

  test("never claims the execute/Code Mode outer tool or other server suffix matches", async () => {
    const host = createHost();
    await plugin.setup(host.context);
    for (const tool of ["execute", "other_sgg_unity_call", `prefix_${callTool}`, `${callTool}-near`, "sgg-unity-mcp.sgg_unity_call"]) {
      const result = {};
      await host.after(event(result, "completed", tool));
      expect(result).toEqual({});
    }
    const row = { tool: "sgg-unity-mcp.sgg_unity_call", status: "completed", input: { command: "compile.status", args_json: "{secret}" } };
    const metadata = { toolCalls: [row] };
    const outer = { metadata, content: "canonical Code Mode result" };
    await host.after(event(outer, "completed", "execute", { code: "private source" }));
    expect(outer).toEqual({ metadata: { toolCalls: [row] }, content: "canonical Code Mode result" });
    expect(row).not.toHaveProperty("metadata");
  });

  test("uses only configured exact server aliases and rejects invalid configuration", async () => {
    const host = createHost({ serverPrefixes: ["unity-dev"] });
    await plugin.setup(host.context);
    const result = {};
    await host.after(event(result, "completed", "unity-dev_sgg_unity_call"));
    expect(result).toHaveProperty("metadata.sggPresentation.label", label);
    const defaultResult = {};
    await host.after(event(defaultResult));
    expect(defaultResult).toEqual({});
    for (const serverPrefixes of [[], ["*"], ["unity.dev"], ["a", "a"], ["a/secret"], "sgg-unity-mcp"]) {
      const invalid = createHost({ serverPrefixes });
      await expect(plugin.setup(invalid.context)).rejects.toThrow("exact MCP server prefixes");
      expect(invalid.context.tool.hook).not.toHaveBeenCalled();
    }
  });

  test("replays wrappers onto fresh catalogs and preserves schema/options/input/context/result", async () => {
    const input = Object.freeze({ command: "compile.status", args_json: "{secret}" });
    const result = Object.freeze({ content: "canonical", output: { canonical: true } });
    const original = vi.fn(async () => result);
    const schema = { type: "object" as const, properties: {} };
    const options = Object.freeze({ namespace: "sgg-unity-mcp", codemode: true as const, permission: "mcp" });
    const base = [
      { id: callTool, name: "sgg_unity_call", description: "original", input: schema, options, execute: original },
      { id: "other_sgg_unity_call", name: "other", description: "other", input: schema, execute: original },
    ];
    const host = createHost({}, base);
    await plugin.setup(host.context);
    const progress = vi.fn(async () => {});
    const context = { progress, signal: new AbortController().signal } as unknown as ToolContext;
    for (let replay = 0; replay < 3; replay += 1) {
      if (replay) await host.context.tool.reload();
      const wrapped = host.active()[0]!;
      expect(wrapped.input).toBe(schema);
      expect(wrapped.options).toBe(options);
      expect(wrapped.description).toBe("original");
      expect(host.active()[1]!.execute).toBe(original);
      expect(await wrapped.execute(input, context)).toBe(result);
    }
    expect(original).toHaveBeenCalledTimes(3);
    expect(original).toHaveBeenLastCalledWith(input, context);
    expect(progress).toHaveBeenCalledTimes(3);
    expect(progress).toHaveBeenLastCalledWith({ sggPresentation: { schemaVersion: 1, label, outcome: "running" } });
  });

  test("awaits progress and preserves executor errors even when progress fails", async () => {
    const failure = new ToolError({ message: "unchanged" });
    const order: string[] = [];
    const original = vi.fn(async () => { order.push("execute"); throw failure; });
    const base = [{ id: callTool, name: "sgg_unity_call", description: "original", input: {}, execute: original }];
    const host = createHost({}, base);
    await plugin.setup(host.context);
    const progress = vi.fn(async () => { await Promise.resolve(); order.push("progress"); throw new Error("UI failure"); });
    const context = { progress, signal: new AbortController().signal } as unknown as ToolContext;
    await expect(host.active()[0]!.execute({ command: "compile.status" }, context)).rejects.toBe(failure);
    expect(order).toEqual(["progress", "execute"]);
    expect(original).toHaveBeenCalledOnce();
  });

  test("wraps exact SGG tools arriving only after MCP catalog refresh", async () => {
    const base: (Info & { id: string })[] = [];
    const host = createHost({}, base);
    await plugin.setup(host.context);
    const original = vi.fn(async () => ({ content: "unchanged" }));
    base.push({ id: callTool, name: "sgg_unity_call", description: "late tool", input: {}, execute: original });
    await host.context.tool.reload();
    expect(host.active()[0]!.execute).not.toBe(original);
  });

  test("keeps default Code Mode eligibility and never injects input/description/pinned options", async () => {
    const original = vi.fn(async () => ({ content: "canonical" }));
    const base = [{ id: callTool, name: "sgg_unity_call", description: "original", input: {}, execute: original }];
    const host = createHost({}, base);
    await plugin.setup(host.context);
    const wrapped = host.active()[0]!;
    expect(wrapped).not.toHaveProperty("options");
    expect(wrapped.description).toBe("original");
    expect(wrapped.input).toBe(base[0]!.input);
  });
});
