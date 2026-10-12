import { spawn } from "node:child_process";
import { fileURLToPath } from "node:url";

import { StreamableHTTPError } from "@modelcontextprotocol/sdk/client/streamableHttp.js";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { ListRootsResultSchema, RootsListChangedNotificationSchema, type CallToolResult, type Root } from "@modelcontextprotocol/sdk/types.js";
import { describe, expect, it, vi } from "vitest";

import { startHttpMcpServer } from "../src/mcp/http-server.js";
import {
  ReconnectableStdioShimClient,
  STDIO_SHIM_LEGACY_TEXT_MAX_BYTES,
  createHttpStdioShimGeneration,
  legacyTextCompatibleToolResult,
  staleSessionToolResult,
  type StdioShimRemoteGeneration,
} from "../src/mcp/stdio-shim.js";

describe("stdio shim compatibility", () => {
  it("projects the full canonical payload into one JSON text item and preserves error metadata", () => {
    const canonical = {
      status: "error",
      request_id: "req-compatible",
      error: { code: "project_not_found", message: "Missing", details: { request_executed: false } },
      diagnostics: [],
      next_valid_actions: ["call_sgg_unity_projects"],
      artifact_refs: ["artifact://report"],
      data: { cursor: "cursor-2" },
      truncated: true,
    };
    const result = legacyTextCompatibleToolResult(toolResult(canonical, true));

    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(canonical);
    expect(result.content).toHaveLength(1);
    expect(JSON.parse(textContent(result))).toEqual(canonical);
  });

  it("keeps a large compact discovery payload bounded while retaining the canonical representation", () => {
    const canonical = {
      status: "ok",
      data: { projects: Array.from({ length: 200 }, (_, index) => ({ id: `project-${index}`, state: "ready", detail: "x".repeat(1_600) })) },
      diagnostics: [],
      next_valid_actions: ["select_project"],
    };
    const result = legacyTextCompatibleToolResult(toolResult(canonical));
    const visible = textContent(result);

    expect(JSON.parse(visible)).toEqual(canonical);
    expect(Buffer.byteLength(visible, "utf8")).toBeGreaterThan(256 * 1024);
    expect(Buffer.byteLength(visible, "utf8")).toBeLessThanOrEqual(STDIO_SHIM_LEGACY_TEXT_MAX_BYTES);
    expect(Buffer.byteLength(JSON.stringify(result), "utf8")).toBeGreaterThan(Buffer.byteLength(visible, "utf8"));
  });

  it("returns explicit truncation evidence instead of a valid-looking partial success on projection overflow", () => {
    const canonical = {
      status: "ok",
      request_id: "req-overflow",
      data: { cursor: "cursor-next", payload: "x".repeat(4_000) },
      diagnostics: [],
      artifact_refs: ["artifact://full-result"],
      next_valid_actions: ["read_artifact"],
      truncated: false,
    };
    const result = legacyTextCompatibleToolResult(toolResult(canonical), 1_024);
    const visible = textContent(result);
    const projection = JSON.parse(visible);

    expect(Buffer.byteLength(visible, "utf8")).toBeLessThanOrEqual(1_024);
    expect(projection).toMatchObject({
      compatibility_projection: "truncated",
      reason: "canonical_payload_exceeds_legacy_text_limit",
      structured_content_preserved: true,
      original_status: "ok",
      request_id: "req-overflow",
      artifact_refs: ["artifact://full-result"],
      next_valid_actions: ["read_artifact"],
      cursor: "cursor-next",
      truncated: true,
    });
    expect(result.structuredContent).toEqual(canonical);
  });

  it("leaves results without structured content unchanged", () => {
    const result: CallToolResult = { content: [{ type: "text", text: "validation error" }], isError: true };
    expect(legacyTextCompatibleToolResult(result)).toBe(result);
  });
});

describe("stdio shim lifecycle", () => {
  it("drains an accepted one-shot call and exits after stdin EOF", async () => {
    let calls = 0;
    const running = await startHttpMcpServer(() => {
      const server = new McpServer({ name: "stdio-shim-one-shot-probe", version: "0.0.0" });
      server.registerTool("sgg_unity_projects", {}, async () => {
        calls++;
        await new Promise<void>((resolve) => { setTimeout(resolve, 75); });
        return toolResult({ status: "ok", data: { projects: [] }, diagnostics: [] });
      });
      return server;
    }, { host: "127.0.0.1", port: 0 });

    try {
      const cliPath = fileURLToPath(new URL("../src/cli.ts", import.meta.url));
      const child = spawn(process.execPath, ["--import", "tsx", cliPath, "stdio-shim", "--url", running.url], {
        cwd: fileURLToPath(new URL("..", import.meta.url)),
        stdio: ["pipe", "pipe", "pipe"],
      });
      let stdout = "";
      let stderr = "";
      child.stdout.on("data", (chunk) => { stdout += String(chunk); });
      child.stderr.on("data", (chunk) => { stderr += String(chunk); });
      child.stdin.end([
        JSON.stringify({
          jsonrpc: "2.0",
          id: 1,
          method: "initialize",
          params: {
            protocolVersion: "2025-06-18",
            capabilities: { roots: { listChanged: true } },
            clientInfo: { name: "one-shot-test", version: "0.0.0" },
          },
        }),
        JSON.stringify({ jsonrpc: "2.0", method: "notifications/initialized" }),
        JSON.stringify({ jsonrpc: "2.0", id: 2, method: "tools/call", params: { name: "sgg_unity_projects", arguments: {} } }),
      ].join("\n") + "\n");

      const exit = await waitForChildExit(child, 30_000);
      expect(exit).toEqual({ code: 0, signal: null });
      expect(stderr).toBe("");
      expect(calls).toBe(1);
      const messages = stdout.trim().split(/\r?\n/u).map((line) => JSON.parse(line) as Record<string, unknown>);
      expect(messages.find((message) => message.id === 2)).toMatchObject({
        jsonrpc: "2.0",
        id: 2,
        result: { structuredContent: { status: "ok", data: { projects: [] } } },
      });
    } finally {
      await running.close();
    }
  }, 35_000);
});

describe("stdio shim stale-session recovery", () => {
  it("returns the exact stale call without replay and reconnects before the next explicit call", async () => {
    const first = fakeGeneration(vi.fn().mockRejectedValue(staleSessionError()));
    const secondResult = toolResult({ status: "ok", data: { generation: 2 }, diagnostics: [] });
    const second = fakeGeneration(vi.fn().mockResolvedValue(secondResult));
    const factory = vi.fn()
      .mockResolvedValueOnce(first.generation)
      .mockResolvedValueOnce(second.generation);
    const client = new ReconnectableStdioShimClient(factory);
    await client.connect();

    const stale = await client.callTool({ name: "sgg_unity_call", arguments: { request_id: "req-stale" } });
    expect(stale.isError).toBe(true);
    expect(stale.structuredContent).toMatchObject({
      status: "error",
      request_id: "req-stale",
      error: { code: "mcp_session_not_found", details: { code: "mcp_session_not_found", request_executed: false, automatic_replay_attempted: false } },
    });
    expect(first.callTool).toHaveBeenCalledTimes(1);
    expect(factory).toHaveBeenCalledTimes(1);

    expect(await client.callTool({ name: "sgg_unity_projects", arguments: {} })).toBe(secondResult);
    expect(first.close).toHaveBeenCalledWith(false);
    expect(second.callTool).toHaveBeenCalledTimes(1);
    expect(factory).toHaveBeenCalledTimes(2);
    await client.close();
    expect(second.close).toHaveBeenCalledWith(true);
  });

  it("serializes one replacement after two concurrent stale observations", async () => {
    const first = fakeGeneration(vi.fn().mockRejectedValue(staleSessionError()));
    const second = fakeGeneration(vi.fn().mockResolvedValue(toolResult({ status: "ok", data: {}, diagnostics: [] })));
    const factory = vi.fn()
      .mockResolvedValueOnce(first.generation)
      .mockResolvedValueOnce(second.generation);
    const client = new ReconnectableStdioShimClient(factory);
    await client.connect();

    const staleResults = await Promise.all([
      client.callTool({ name: "sgg_unity_projects", arguments: {} }),
      client.callTool({ name: "sgg_unity_projects", arguments: {} }),
    ]);
    expect(staleResults.every((result) => result.isError === true)).toBe(true);
    expect(first.callTool).toHaveBeenCalledTimes(2);

    await Promise.all([
      client.callTool({ name: "sgg_unity_projects", arguments: {} }),
      client.callTool({ name: "sgg_unity_projects", arguments: {} }),
    ]);
    expect(factory).toHaveBeenCalledTimes(2);
    expect(first.close).toHaveBeenCalledTimes(1);
    expect(second.callTool).toHaveBeenCalledTimes(2);
    await client.close();
  });

  it("does not let a late stale completion invalidate the replacement generation", async () => {
    let rejectLate!: (error: unknown) => void;
    const late = new Promise<CallToolResult>((_resolve, reject) => { rejectLate = reject; });
    const firstCall = vi.fn()
      .mockReturnValueOnce(late)
      .mockRejectedValueOnce(staleSessionError());
    const first = fakeGeneration(firstCall);
    const second = fakeGeneration(vi.fn().mockResolvedValue(toolResult({ status: "ok", data: { generation: 2 }, diagnostics: [] })));
    const factory = vi.fn()
      .mockResolvedValueOnce(first.generation)
      .mockResolvedValueOnce(second.generation);
    const client = new ReconnectableStdioShimClient(factory);
    await client.connect();

    const lateCall = client.callTool({ name: "sgg_unity_projects", arguments: {} });
    await client.callTool({ name: "sgg_unity_projects", arguments: {} });
    await client.callTool({ name: "sgg_unity_projects", arguments: {} });
    rejectLate(staleSessionError());
    expect((await lateCall).isError).toBe(true);

    await client.callTool({ name: "sgg_unity_projects", arguments: {} });
    expect(factory).toHaveBeenCalledTimes(2);
    expect(second.callTool).toHaveBeenCalledTimes(2);
    await client.close();
  });

  it("continues roots notifications on the replacement generation", async () => {
    const first = fakeGeneration(vi.fn().mockRejectedValue(staleSessionError()));
    const second = fakeGeneration(vi.fn().mockResolvedValue(toolResult({ status: "ok", data: {}, diagnostics: [] })));
    const factory = vi.fn()
      .mockResolvedValueOnce(first.generation)
      .mockResolvedValueOnce(second.generation);
    const client = new ReconnectableStdioShimClient(factory);
    await client.connect();
    await client.notifyRootsChanged();
    expect(first.notifyRootsChanged).toHaveBeenCalledTimes(2);

    await client.callTool({ name: "sgg_unity_projects", arguments: {} });
    await client.callTool({ name: "sgg_unity_projects", arguments: {} });
    await client.notifyRootsChanged();
    expect(second.notifyRootsChanged).toHaveBeenCalledTimes(2);
    await client.close();
  });

  it("recovers a real SDK session after an HTTP server restart without dispatching the stale call", async () => {
    const firstCalls: Root[][] = [];
    const secondCalls: Root[][] = [];
    const firstNotifications = { count: 0 };
    const secondNotifications = { count: 0 };
    let firstServer: Awaited<ReturnType<typeof startHttpMcpServer>> | undefined = await startHttpMcpServer(
      () => rootsProbeServer(firstCalls, firstNotifications),
      { host: "127.0.0.1", port: 0 },
    );
    let secondServer: Awaited<ReturnType<typeof startHttpMcpServer>> | undefined;
    const url = new URL(firstServer.url);
    const roots: { current: Root[] } = { current: [{ uri: "file:///C:/Work/first" }] };
    const factory = vi.fn(async () => await createHttpStdioShimGeneration({ url: url.href, version: "0.0.0" }, roots));
    const client = new ReconnectableStdioShimClient(factory);
    const originalFetch = globalThis.fetch;
    // This fixture tests a real stale-session HTTP 404, not reuse of an idle
    // TCP socket concurrently destroyed by the old server. Do not retry a call.
    const fetchSpy = vi.spyOn(globalThis, "fetch").mockImplementation((input, init) => {
      const headers = new Headers(input instanceof Request ? input.headers : undefined);
      new Headers(init?.headers).forEach((value, name) => headers.set(name, value));
      headers.set("Connection", "close");
      return originalFetch(input, { ...init, headers });
    });

    try {
      await client.connect();
      await client.callTool({ name: "probe", arguments: {} });
      expect(firstCalls).toEqual([[{ uri: "file:///C:/Work/first" }]]);
      expect(firstNotifications.count).toBeGreaterThanOrEqual(1);

      await firstServer.close();
      firstServer = undefined;
      secondServer = await startHttpMcpServer(
        () => rootsProbeServer(secondCalls, secondNotifications),
        { host: "127.0.0.1", port: Number(url.port) },
      );
      roots.current = [{ uri: "file:///C:/Work/second" }];

      const stale = await client.callTool({ name: "probe", arguments: {} });
      expect(stale).toMatchObject({
        isError: true,
        structuredContent: { error: { code: "mcp_session_not_found", details: { request_executed: false } } },
      });
      expect(firstCalls).toHaveLength(1);
      expect(secondCalls).toHaveLength(0);

      await client.callTool({ name: "probe", arguments: {} });
      expect(factory).toHaveBeenCalledTimes(2);
      expect(secondCalls).toEqual([[{ uri: "file:///C:/Work/second" }]]);
      expect(secondNotifications.count).toBeGreaterThanOrEqual(1);
    } finally {
      await client.close().catch(() => {});
      await firstServer?.close().catch(() => {});
      await secondServer?.close().catch(() => {});
      fetchSpy.mockRestore();
    }
  });

  it.each([
    new StreamableHTTPError(401, "Unauthorized"),
    new StreamableHTTPError(403, "Forbidden"),
    new StreamableHTTPError(500, "Server failure"),
    new Error("request timed out"),
    new DOMException("cancelled", "AbortError"),
    new StreamableHTTPError(404, "Error POSTing to endpoint: malformed"),
    new StreamableHTTPError(404, `Error POSTing to endpoint: ${JSON.stringify({ error: { code: -32001, data: { code: "mcp_session_not_found", request_executed: true } } })}`),
  ])("does not reconnect or replay a non-matching failure %#", async (failure) => {
    const success = toolResult({ status: "ok", data: {}, diagnostics: [] });
    const generation = fakeGeneration(vi.fn().mockRejectedValueOnce(failure).mockResolvedValueOnce(success));
    const factory = vi.fn().mockResolvedValue(generation.generation);
    const client = new ReconnectableStdioShimClient(factory);
    await client.connect();

    await expect(client.callTool({ name: "sgg_unity_projects", arguments: {} })).rejects.toBe(failure);
    expect(await client.callTool({ name: "sgg_unity_projects", arguments: {} })).toBe(success);
    expect(factory).toHaveBeenCalledTimes(1);
    expect(generation.callTool).toHaveBeenCalledTimes(2);
    await client.close();
  });

  it("returns a bounded fixed stale-session error without reflecting the remote body", () => {
    const result = staleSessionToolResult(staleSessionError("secret-body-marker"));
    const serialized = JSON.stringify(result);

    expect(result?.isError).toBe(true);
    expect(result?.structuredContent).toMatchObject({ error: { details: { request_executed: false } } });
    expect(Buffer.byteLength(serialized, "utf8")).toBeLessThan(2_048);
    expect(serialized).not.toContain("secret-body-marker");
  });
});

function staleSessionError(extra = ""): StreamableHTTPError {
  return new StreamableHTTPError(404, `Error POSTing to endpoint: ${JSON.stringify({
    jsonrpc: "2.0",
    id: null,
    error: {
      code: -32001,
      message: `MCP session not found ${extra}`,
      data: { code: "mcp_session_not_found", request_executed: false },
    },
  })}`);
}

function rootsProbeServer(calls: Root[][], notifications: { count: number }): McpServer {
  const server = new McpServer({ name: "stdio-shim-remote-probe", version: "0.0.0" });
  server.server.setNotificationHandler(RootsListChangedNotificationSchema, async () => { notifications.count += 1; });
  server.registerTool("probe", {}, async (extra) => {
    const listed = await extra.sendRequest({ method: "roots/list" }, ListRootsResultSchema);
    calls.push(listed.roots);
    return { content: [{ type: "text", text: JSON.stringify(listed) }] };
  });
  return server;
}

function fakeGeneration(callTool: ReturnType<typeof vi.fn>): {
  generation: StdioShimRemoteGeneration;
  callTool: ReturnType<typeof vi.fn>;
  notifyRootsChanged: ReturnType<typeof vi.fn>;
  close: ReturnType<typeof vi.fn>;
} {
  const notifyRootsChanged = vi.fn().mockResolvedValue(undefined);
  const close = vi.fn().mockResolvedValue(undefined);
  return {
    generation: {
      client: { callTool: callTool as StdioShimRemoteGeneration["client"]["callTool"] },
      notifyRootsChanged,
      close,
    },
    callTool,
    notifyRootsChanged,
    close,
  };
}

function toolResult(structuredContent: Record<string, unknown>, isError = false): CallToolResult {
  return {
    content: [{ type: "text", text: "summary" }],
    structuredContent,
    ...(isError ? { isError: true } : {}),
  };
}

function textContent(result: CallToolResult): string {
  const content = result.content[0];
  if (!content || content.type !== "text") throw new Error("Expected text content");
  return content.text;
}

function waitForChildExit(child: ReturnType<typeof spawn>, timeoutMs: number): Promise<{ code: number | null; signal: NodeJS.Signals | null }> {
  return new Promise((resolve, reject) => {
    const timeout = setTimeout(() => {
      child.kill();
      reject(new Error(`stdio-shim child did not exit within ${timeoutMs}ms`));
    }, timeoutMs);
    child.once("error", (error) => {
      clearTimeout(timeout);
      reject(error);
    });
    child.once("exit", (code, signal) => {
      clearTimeout(timeout);
      resolve({ code, signal });
    });
  });
}
