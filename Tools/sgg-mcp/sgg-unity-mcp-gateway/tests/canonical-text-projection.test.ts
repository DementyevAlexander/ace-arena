import type { CallToolResult } from "@modelcontextprotocol/sdk/types.js";
import { Client, StreamableHTTPClientTransport } from "@modelcontextprotocol/client";
import { describe, expect, it } from "vitest";

import { CANONICAL_TEXT_MAX_BYTES, canonicalTextProjection } from "../src/mcp/canonical-text-projection.js";
import { gatewayResultToMcpToolResult } from "../src/mcp/result-presentation.js";
import type { GatewayFailure } from "../src/mcp/results.js";
import { legacyTextCompatibleToolResult } from "../src/mcp/stdio-shim.js";
import { createUnityMcpServer } from "../src/mcp/sdk-server.js";
import { createUnityMcpV2ServerFactory } from "../src/mcp/sdk-v2-server.js";
import { startHttpMcpServer } from "../src/mcp/http-server.js";

describe("Gateway error text-only client contract", () => {
  // Contract simulation of OpenCode 2.0.25's isError -> text-only Error path;
  // not evidence of acceptance by an installed OpenCode/OpenChamber client.
  function textOnlyFailure(result: CallToolResult): Error {
    if (!result.isError) throw new Error("Expected MCP tool failure");
    return new Error(result.content.filter((item) => item.type === "text").map((item) => item.text).join("\n"));
  }

  it.each([
    { code: "confirmation_required" as const, details: { approval_request: { pending_id: "pending-exact", expires_at: "2026-10-10T12:00:00Z" }, approval_workflow: { fallback: { pending_id: "pending-exact", retry_original_call_required: true } } } },
    { code: "schema_validation_failed" as const, details: { schema_lookup: { metadata_lookup: { tool: "sgg_unity_describe", project: "game", selector: "command:asset.synthetic_write" } } } },
    { code: "command_timeout" as const, details: { operation_id: "test-20261010-120000-1234abcd", automatic_replay_attempted: false, write_command_repeated: false, status_lookup: { tool: "sgg_unity_call", command: "test.run.status", args_json: '{"operation_id":"test-20261010-120000-1234abcd"}' } } },
  ])("round-trips canonical $code recovery through text-only conversion", ({ code, details }) => {
    const canonical: GatewayFailure = { status: "error", request_id: "req-exact", project: "game", command: "asset.synthetic_write", error: { code, message: "Human or recovery action required", details }, diagnostics: [], next_valid_actions: ["inspect_exact_pending_or_operation_before_new_call"] };
    const result = gatewayResultToMcpToolResult(canonical);
    expect(result.isError).toBe(true);
    expect(result.structuredContent).toEqual(canonical);
    expect(JSON.parse(textOnlyFailure(result).message)).toEqual(canonical);
    expect(legacyTextCompatibleToolResult(result)).toEqual(result);
  });

  it("bounds overflowing error text, retaining exact recovery fields and full structured content", () => {
    const canonical: GatewayFailure = {
      status: "error", request_id: "req-overflow", error: {
        code: "command_timeout", message: "Uncertain write",
        details: {
          operation_id: "menu-20261010-120000-1234abcd", automatic_replay_attempted: false,
          schema_lookup: { metadata_lookup: { tool: "sgg_unity_describe", selector: "command:editor.menu.invoke" } },
          approval_request: { pending_id: "pending-exact", expires_at: "2026-10-10T12:00:00Z" },
          status_request: { command: "editor.menu.operation_status", args_json: '{"operation_id":"menu-20261010-120000-1234abcd"}' },
          verbose: "💥".repeat(CANONICAL_TEXT_MAX_BYTES),
        },
      }, diagnostics: [], next_valid_actions: ["poll_exact_operation_without_replay"],
    };
    const result = gatewayResultToMcpToolResult(canonical);
    const text = textOnlyFailure(result).message;
    expect(Buffer.byteLength(text, "utf8")).toBeLessThanOrEqual(CANONICAL_TEXT_MAX_BYTES);
    expect(JSON.parse(text)).toMatchObject({
      compatibility_projection: "truncated", truncated: true, structured_content_preserved: true,
      request_id: "req-overflow", error: { code: "command_timeout", details: {
        operation_id: "menu-20261010-120000-1234abcd", automatic_replay_attempted: false,
        schema_lookup: canonical.error.details!.schema_lookup, approval_request: canonical.error.details!.approval_request,
        status_request: canonical.error.details!.status_request,
      } }, next_valid_actions: canonical.next_valid_actions,
    });
    expect(result.structuredContent).toEqual(canonical);
    expect(result.isError).toBe(true);
  });

  it("keeps large direct success text short instead of duplicating JSON", () => {
    const canonical = { status: "ok" as const, data: { payload: "x".repeat(CANONICAL_TEXT_MAX_BYTES * 2) }, diagnostics: [] };
    const result = gatewayResultToMcpToolResult(canonical);
    expect(result.isError).toBeUndefined();
    expect(result.structuredContent).toEqual(canonical);
    expect(JSON.stringify(result.content).length).toBeLessThan(200);
  });

  it("preserves canonical error text on an actual public SDK v2 HTTP fixture", async () => {
    const running = await startHttpMcpServer(() => createUnityMcpServer({}, { version: "0.0.0" }), { host: "127.0.0.1", port: 0, modernServerFactory: createUnityMcpV2ServerFactory({}, { version: "0.0.0" }) });
    const client = new Client({ name: "text-projection-fixture", version: "0.0.0" }, { versionNegotiation: { mode: "auto" } });
    try {
      await client.connect(new StreamableHTTPClientTransport(new URL(running.url)));
      const result = await client.callTool({ name: "sgg_unity_call", arguments: { project: "missing", command: "compile.status", args_json: "{}" } }) as CallToolResult;
      expect(result.isError).toBe(true);
      expect(JSON.parse(textOnlyFailure(result).message)).toEqual(result.structuredContent);
    } finally {
      await client.close(); await running.close();
    }
  });

  it("never exceeds even a tight budget when adding error detail scaffolding", () => {
    const payload = { status: "error", request_id: "r".repeat(250), command: "c".repeat(250), error: { code: "command_timeout", message: "m".repeat(512), details: { operation_id: "exact-operation" } }, huge: "x".repeat(5_000) };
    const text = canonicalTextProjection(payload, 1_024);
    expect(Buffer.byteLength(text, "utf8")).toBeLessThanOrEqual(1_024);
    expect(JSON.parse(text)).toMatchObject({ truncated: true });
  });

  it.each([1_024, 0, -1, NaN, Infinity, CANONICAL_TEXT_MAX_BYTES * 2])("keeps overflow valid and byte-bounded at requested limit %s", (requested) => {
    const payload = { status: "error", request_id: "exact-id", error: { code: "command_timeout", details: { operation_id: "exact-operation", arbitrary: "x".repeat(CANONICAL_TEXT_MAX_BYTES * 2) } }, next_valid_actions: ["inspect"] };
    const text = canonicalTextProjection(payload, requested);
    const limit = Number.isSafeInteger(requested) ? Math.max(1_024, Math.min(requested, CANONICAL_TEXT_MAX_BYTES)) : CANONICAL_TEXT_MAX_BYTES;
    expect(Buffer.byteLength(text, "utf8")).toBeLessThanOrEqual(limit);
    expect(JSON.parse(text)).toMatchObject({ truncated: true, request_id: "exact-id", error: { details: { operation_id: "exact-operation" } } });
  });

  it("fails explicitly on invalid getters/cycles rather than pretending to serialize a result", () => {
    const getters = { get error() { throw new Error("private getter error"); } };
    const cycle: Record<string, unknown> = {};
    cycle.self = cycle;
    for (const payload of [getters, cycle]) {
      const text = canonicalTextProjection(payload);
      expect(JSON.parse(text)).toMatchObject({ truncated: true, reason: "canonical_payload_not_serializable", structured_content_preserved: true });
      expect(text).not.toContain("private getter error");
    }
  });
});
