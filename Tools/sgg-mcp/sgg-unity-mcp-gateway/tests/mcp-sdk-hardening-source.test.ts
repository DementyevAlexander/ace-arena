import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();

describe("MCP SDK hardening source-contract", () => {
  it("pins the dual-era MCP SDK packages exactly in package metadata and lock data", () => {
    const packageJson = readJson("package.json");
    const lock = readJson("package-lock.json");

    expect(packageJson.dependencies["@modelcontextprotocol/sdk"]).toBe("1.29.0");
    expect(lock.packages[""].dependencies["@modelcontextprotocol/sdk"]).toBe("1.29.0");
    expect(lock.packages["node_modules/@modelcontextprotocol/sdk"].version).toBe("1.29.0");
    expect(packageJson.dependencies["@modelcontextprotocol/server"]).toBe("2.0.0");
    expect(packageJson.dependencies["@modelcontextprotocol/node"]).toBe("2.0.0");
    expect(packageJson.devDependencies["@modelcontextprotocol/client"]).toBe("2.0.0");
    expect(lock.packages["node_modules/@modelcontextprotocol/server"].version).toBe("2.0.0");
    expect(lock.packages["node_modules/@modelcontextprotocol/node"].version).toBe("2.0.0");
    expect(lock.packages["node_modules/@modelcontextprotocol/client"].version).toBe("2.0.0");
    expect(fs.readFileSync(path.join(root, "scripts/check-version.mjs"), "utf8")).toContain('REQUIRED_MCP_SDK_VERSION = "1.29.0"');
    expect(fs.readFileSync(path.join(root, "scripts/check-version.mjs"), "utf8")).toContain('REQUIRED_MODERN_MCP_SDK_VERSION = "2.0.0"');
  });

  it("does not access private MCP registration state or direct registration callbacks", () => {
    const sdkServer = fs.readFileSync(path.join(root, "src/mcp/sdk-server.ts"), "utf8");
    const stdioShim = fs.readFileSync(path.join(root, "src/mcp/stdio-shim.ts"), "utf8");
    const clientRoots = fs.readFileSync(path.join(root, "src/mcp/client-roots.ts"), "utf8");
    const production = ["src/mcp/http-server.ts", "src/mcp/sdk-server.ts", "src/mcp/sdk-v2-server.ts", "src/mcp/stdio-shim.ts", "src/mcp/client-roots.ts"]
      .map((relativePath) => fs.readFileSync(path.join(root, relativePath), "utf8"))
      .join("\n");

    expect(production).not.toMatch(/_(?:registeredTools|serverInfo)/);
    expect(production).not.toMatch(/\bRegisteredTool\b/);
    expect(production).not.toMatch(/\.handler\s*\(/);
    expect(production).not.toContain("normalizeObjectSchema");
    expect(production).not.toContain("toJsonSchemaCompat");
    expect(production).toContain("RequestHandlerExtra");
    expect(sdkServer).not.toContain("extra.sendRequest");
    expect(stdioShim).toContain('extra.sendRequest({ method: "roots/list" }');
    expect(clientRoots).toContain('extra.sendRequest({ method: "roots/list" }');
    expect(production.match(/extra\.sendRequest/g)).toHaveLength(2);
    expect(production).toContain("server.server.getClientCapabilities()");
    expect(production).toContain("server.server.elicitInput");
  });
});

function readJson(relativePath: string): any {
  return JSON.parse(fs.readFileSync(path.join(root, relativePath), "utf8"));
}
