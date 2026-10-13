import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const extensionRoot = path.join(process.cwd(), "examples", "extensions", "com.example.sgg-mcp-demo");

describe("example extension package sample", () => {
  it("declares dependency-free manifest commands with governance metadata", () => {
    const packageJson = JSON.parse(fs.readFileSync(path.join(extensionRoot, "package.json"), "utf8")) as Record<string, unknown>;
    const manifest = JSON.parse(fs.readFileSync(path.join(extensionRoot, "Editor", "Mcp", "mcp.commands.json"), "utf8")) as { package?: string; commands?: Array<Record<string, unknown>> };
    const asmdef = JSON.parse(fs.readFileSync(path.join(extensionRoot, "Editor", "Example.SggMcpDemo.Editor.asmdef"), "utf8")) as Record<string, unknown>;
    const handler = fs.readFileSync(path.join(extensionRoot, "Editor", "ExampleCommands.cs"), "utf8");

    expect(packageJson.name).toBe("com.example.sgg-mcp-demo");
    expect(packageJson.dependencies).toBeUndefined();
    expect(manifest.package).toBe(packageJson.name);
    expect(asmdef.references).toEqual([]);
    expect(asmdef.includePlatforms).toEqual(["Editor"]);
    expect(handler).not.toContain("SGG.UnityMCP.Editor");

    const commands = manifest.commands ?? [];
    expect(commands.map((command) => command.id)).toEqual(["demo.status", "demo.report.generate"]);

    for (const command of commands) {
      expect(command.handler).toMatch(/^Example\.SggMcpDemo\.Editor\.ExampleCommands\./);
      expect(command.risk).toMatch(/^(read|execute)$/);
      expect(command.idempotency).toMatch(/^(safe|unsafe)$/);
      expect(command.timeout_ms).toEqual(expect.any(Number));
      expect(command.schema).toEqual(expect.objectContaining({ type: "object" }));
      expect(command.output_limit).toEqual(expect.objectContaining({ max_chars: expect.any(Number) }));
      expect((command.output_limit as { max_chars?: number }).max_chars).toBeGreaterThan(0);
    }

    const report = commands.find((command) => command.id === "demo.report.generate");
    expect(report).toEqual(expect.objectContaining({
      capability: "demo_extension_reports",
      feature_flag: "demo_reports",
      result_shape: "object_details",
      risk: "execute",
      supports_cancel: false,
      supports_job: true,
    }));
  });

  it("keeps the extension sample discoverable from public docs", () => {
    const docsIndex = fs.readFileSync(path.join(process.cwd(), "docs", "en", "README.md"), "utf8");
    const ruDocsIndex = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "README.md"), "utf8");
    const examplesReadme = fs.readFileSync(path.join(process.cwd(), "examples", "README.md"), "utf8");
    const moduleDocs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "extensions", "quick-start.md"), "utf8");
    const ruModuleDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "extensions", "quick-start.md"), "utf8");
    const testingDocs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "extensions", "testing.md"), "utf8");
    const ruTestingDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "extensions", "testing.md"), "utf8");
    const governanceDocs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "extensions", "governance.md"), "utf8");
    const ruGovernanceDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "extensions", "governance.md"), "utf8");
    const sampleReadme = fs.readFileSync(path.join(extensionRoot, "README.md"), "utf8");

    expect(docsIndex).toContain("Add MCP Support To Your Unity Asset");
    expect(docsIndex).toContain("./extensions/quick-start.md");
    expect(ruDocsIndex).toContain("./extensions/quick-start.md");
    expect(moduleDocs).toContain("without running another MCP server");
    expect(moduleDocs).toContain("top-level MCP tool");
    expect(moduleDocs).toContain("[McpCommand]");
    expect(moduleDocs).toContain("Editor/Mcp/mcp.commands.json");
    expect(ruModuleDocs).toContain("без второго MCP server");
    expect(ruModuleDocs).toContain("[McpCommand]");
    expect(ruModuleDocs).toContain("Editor/Mcp/mcp.commands.json");
    expect(examplesReadme).toContain("extensions/com.example.sgg-mcp-demo/");

    for (const text of [testingDocs, ruTestingDocs, governanceDocs, ruGovernanceDocs]) {
      expect(text).toContain("examples/extensions/com.example.sgg-mcp-demo/");
    }

    expect(testingDocs).toContain("Asset authors do not run a second MCP server");
    expect(ruTestingDocs).toContain("Asset authors не запускают второй MCP server");
    expect(governanceDocs).toContain("Trust Boundary");
    expect(ruGovernanceDocs).toContain("Trust boundary");
    expect(examplesReadme).toContain("untrusted by default");
    expect(sampleReadme).toContain("local Unity package");
    expect(sampleReadme).toMatch(/copy this folder into `?Packages\/com\.example\.sgg-mcp-demo`?/);
    expect(sampleReadme).toContain("untrusted by default");
    expect(sampleReadme).toContain("trusted_packages");
    expect(governanceDocs).toContain("not listed in default trusted policy");
    expect(ruGovernanceDocs).toContain("не добавлен в default trusted policy");
  });

  it("keeps generic negative manifest examples documented but not loadable", () => {
    const examplesReadme = fs.readFileSync(path.join(process.cwd(), "examples", "README.md"), "utf8");
    const governanceDocs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "extensions", "testing.md"), "utf8");
    const ruGovernanceDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "extensions", "testing.md"), "utf8");
    const examplesRoot = path.join(process.cwd(), "examples", "extensions");
    const cases = ["good-manifest", "bad-missing-risk", "bad-builtin-impersonation", "bad-unbounded-output"];

    for (const directory of cases) {
      expect(fs.existsSync(path.join(examplesRoot, directory, "README.md"))).toBe(true);
      expect(examplesReadme).toContain(`extensions/${directory}/`);
      expect(governanceDocs).toContain(`${directory}/`);
      expect(ruGovernanceDocs).toContain(`${directory}/`);
    }

    const good = JSON.parse(fs.readFileSync(path.join(examplesRoot, "good-manifest", "mcp.commands.example.json"), "utf8"));
    expect(good.package).toBe("com.example.good-manifest");
    expect(good.commands[0]).toEqual(expect.objectContaining({ risk: "read", idempotency: "safe", output_limit: { max_chars: 2048 } }));

    const missingRisk = JSON.parse(fs.readFileSync(path.join(examplesRoot, "bad-missing-risk", "mcp.commands.bad.json"), "utf8"));
    expect(missingRisk.package).toBe("com.example.bad-missing-risk");
    expect(missingRisk.commands[0].risk).toBeUndefined();

    const impersonation = JSON.parse(fs.readFileSync(path.join(examplesRoot, "bad-builtin-impersonation", "mcp.commands.bad.json"), "utf8"));
    expect(impersonation.package).toBe("com.sungeargames.unity-mcp");

    const unbounded = JSON.parse(fs.readFileSync(path.join(examplesRoot, "bad-unbounded-output", "mcp.commands.bad.json"), "utf8"));
    expect(unbounded.commands[0]).toEqual(expect.objectContaining({ result_shape: "text_artifact" }));
    expect(unbounded.commands[0].output_limit).toBeUndefined();
  });
});
