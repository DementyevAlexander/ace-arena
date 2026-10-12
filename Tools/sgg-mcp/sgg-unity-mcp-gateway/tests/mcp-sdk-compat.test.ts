import { describe, expect, it } from "vitest";

import { assertMcpSdkCompatible, McpSdkIncompatibleError } from "../src/mcp/sdk-compat.js";

describe("MCP SDK runtime compatibility", () => {
  it("accepts the installed public SDK surface", () => {
    expect(() => assertMcpSdkCompatible()).not.toThrow();
  });

  it("reports only bounded typed evidence for missing public members", () => {
    let caught: unknown;
    try {
      assertMcpSdkCompatible({
        createMcpServer: () => ({ server: {} }),
        createStreamableHttpTransport: () => ({}),
      });
    } catch (error) {
      caught = error;
    }

    expect(caught).toBeInstanceOf(McpSdkIncompatibleError);
    const incompatible = caught as McpSdkIncompatibleError;
    expect(incompatible.code).toBe("mcp_sdk_incompatible");
    expect(incompatible.message).toBe("MCP SDK public surface is incompatible");
    expect(incompatible.evidence).toHaveLength(6);
    expect(incompatible.evidence).toContainEqual({ component: "McpServer", member: "connect", expected: "function" });
    expect(Object.keys(incompatible.evidence[0] ?? {}).sort()).toEqual(["component", "expected", "member"]);
    expect(JSON.stringify(incompatible)).not.toContain("stack");
  });

  it("does not expose injected constructor failures", () => {
    expect(() => assertMcpSdkCompatible({
      createMcpServer: () => { throw new Error("private-constructor-detail"); },
      createStreamableHttpTransport: () => { throw { secret: "private-transport-detail" }; },
    })).toThrowError(McpSdkIncompatibleError);

    try {
      assertMcpSdkCompatible({
        createMcpServer: () => { throw new Error("private-constructor-detail"); },
        createStreamableHttpTransport: () => { throw { secret: "private-transport-detail" }; },
      });
    } catch (error) {
      expect(JSON.stringify(error)).not.toMatch(/private-(?:constructor|transport)-detail/);
    }
  });
});
