import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");

describe("MCP-PR-216 local package inventory diagnostics", () => {
  it("keeps mismatch evidence bounded, deterministic, read-only, and package-relative", () => {
    const inventory = readPackageFile("Editor/Bridge/LocalPackageCodeInventoryInspector.cs");
    const state = readPackageFile("Editor/Bridge/BridgeRuntimeState.cs");
    const commands = readPackageFile("Editor/Commands/BuiltInCommands.cs");

    expect(inventory).toContain("MaxMismatchDetails = 16");
    expect(inventory).toContain("MaxDiagnosticPathChars = 512");
    expect(inventory).toContain('AppendMismatchDetails(mismatchDetails, "missing_asset"');
    expect(inventory).toContain('AppendMismatchDetails(mismatchDetails, "unexpected_asset"');
    expect(inventory).toContain('AppendMismatchDetails(mismatchDetails, "missing_compiler_source"');
    expect(inventory).toContain('AppendMismatchDetails(mismatchDetails, "unexpected_compiler_source"');
    expect(inventory).toContain("sortedExpectedCompilerPaths.Sort(StringComparer.Ordinal)");
    expect(inventory).toContain('new LocalPackageSourceMetadataEvidence("meta_missing", 0)');
    expect(inventory).toContain('"guid_invalid_length"');
    expect(inventory).toContain('"guid_invalid_format"');
    expect(inventory).not.toContain("AssetDatabase.Refresh");
    expect(inventory).not.toContain("AssetDatabase.ImportAsset");
    expect(inventory).not.toContain("File.WriteAllText");
    expect(state).toContain("sgg.unity_mcp.local_package_inventory_diagnostics.v1");
    expect(state).toContain('Append(",\\\"automatic_repair_attempted\\\":false")');
    expect(commands).toContain("local_package_inventory_diagnostics");
  });
});

function readPackageFile(relativePath: string): string {
  return fs.readFileSync(path.join(packageRoot, relativePath), "utf8");
}
