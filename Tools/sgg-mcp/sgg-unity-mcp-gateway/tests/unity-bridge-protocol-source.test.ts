import { readFileSync } from "node:fs";

import { describe, expect, it } from "vitest";

const server = readFileSync("unity/Packages/com.sungeargames.unity-mcp/Editor/Bridge/BridgeServer.cs", "utf8");
const protocol = readFileSync("unity/Packages/com.sungeargames.unity-mcp/Editor/Bridge/BridgeProtocol.cs", "utf8");
const result = readFileSync("unity/Packages/com.sungeargames.unity-mcp/Editor/Commands/CommandExecutionResult.cs", "utf8");

describe("Unity bridge invoke protocol source-contract", () => {
  it("bounds fixed and streamed invoke request bodies before dispatch", () => {
    expect(protocol).toContain("MaxInvokeBytes = 4 * 1024 * 1024");
    expect(server).toContain("request.ContentLength64 > BridgeProtocol.MaxInvokeBytes");
    expect(server).toContain("buffer.Length + read > BridgeProtocol.MaxInvokeBytes");
    expect(server.indexOf("BridgeProtocol.TryParseInvoke")).toBeLessThan(server.indexOf("InvokeJsonSafely(request, catalog)"));
    expect(server.indexOf("TryValidateCatalogBinding(request, catalog")).toBeLessThan(server.indexOf("if (!TryClaimCommandDispatch())"));
    expect(server.indexOf("TryValidateCatalogBinding(request, catalog")).toBeLessThan(server.indexOf("catalog.ContainsCommand(request.Command)"));
    expect(server.indexOf("catalog.ContainsCommand(request.Command)")).toBeLessThan(server.indexOf("if (!TryClaimCommandDispatch())"));
    expect(server).toContain("var builtInCommand = catalog.IsBuiltInCommand(command)");
  });

  it("correlates projects ordinally before every built-in or registry dispatch", () => {
    expect(protocol).toContain("string.Equals(request.Project, expectedProject, StringComparison.Ordinal)");
    expect(protocol).toContain('ErrorResponse("protocol_validation_error", "Invoke request project does not match this Unity bridge"');
    const correlation = server.indexOf("BridgeProtocol.TryValidateProject(request, m_state.Project");
    expect(correlation).toBeGreaterThan(server.indexOf("BridgeProtocol.TryParseInvoke"));
    expect(correlation).toBeLessThan(server.indexOf("InvokeJsonSafely(request, catalog)"));
    expect(correlation).toBeLessThan(server.indexOf('if (builtInCommand && command == "compile.status")'));
    expect(correlation).toBeLessThan(server.indexOf("return m_registry.Invoke"));
  });

  it("emits complete v1 invoke success and error envelopes", () => {
    for (const field of ["schema_version", "status", "diagnostics", "duration_ms"]) expect(result).toContain(`\\\"${field}\\\"`);
    expect(protocol).toContain("protocol_version_mismatch");
    expect(protocol).toContain("protocol_validation_error");
    expect(protocol).toContain("IsSafeRequestId(requestId)");
  });
});
