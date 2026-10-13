import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");

describe("MCP-PR-088 schema and invalid-field diagnostics", () => {
  it("uses one bounded descriptor-derived diagnostic contract without rejected-value reflection or retry", () => {
    const source = fs.readFileSync(path.join(root, "src", "mcp", "dispatcher.ts"), "utf8");

    expect(source).toContain('origin: "descriptor"');
    expect(source).toContain('origin: "unity_runtime"');
    expect(source).toContain("rejected_values_reflected: false");
    expect(source).toContain("descriptorExpectation");
    expect(source).toContain("safeValidationPath");
    expect(source).toContain("isTrustedBuiltInDescriptor");
    expect(source).toContain('contract.contract !== "sgg.unity_mcp.runtime_validation.v1"');
    expect(source).not.toContain("runtime_validation.rejected_value");
  });

  it("covers every reported command family at its current descriptor or semantic validation boundary", () => {
    const sources = {
      component: readCommand("EditorSafeWriteCommands.cs"),
      scene: readCommand("SemanticInspectionCommands.cs"),
      assets: readCommand("BuiltInCommands.cs"),
      tests: readCommand("BuildTestAutomationCommands.cs"),
      logs: readCommand("BuiltInCommands.cs"),
      playmode: readCommand("EditorStatusCommands.cs"),
      csharp: readCommand("EditorCSharpExecutionCommands.cs"),
      runtime: readCommand("RuntimeDebuggingCommands.cs"),
      screenshot: readCommand("EditorScreenshotCommands.cs") + readCommand("EditorWindowSelector.cs"),
      diagnostics: fs.readFileSync(path.join(root, "src", "mcp", "diagnostics-commands.ts"), "utf8"),
    };

    expect(sources.component).toContain('"component.modify"');
    expect(sources.component).toContain('"enabled", "unsupported_for_target"');
    expect(sources.scene).toContain('"scene.inspect_object"');
    expect(sources.scene).toContain('JsonUtil.HasProperty(context.ArgsJson, "object_path") ? "string_too_short" : "missing_required_field"');
    expect(sources.assets).toContain('"assets.refresh"');
    expect(sources.tests).toContain('"test.run"');
    expect(sources.tests).toContain('"mode", "enum_value_not_allowed"');
    expect(sources.logs).toContain('"logs.console.query"');
    expect(sources.playmode).toContain('"editor.playmode.enter"');
    expect(sources.playmode).toContain('"state", "enum_value_not_allowed"');
    expect(sources.csharp).toContain('"editor.execute_csharp"');
    expect(sources.csharp).toContain('JsonUtil.HasProperty(context.ArgsJson, "code") ? "string_too_short" : "missing_required_field"');
    expect(sources.diagnostics).toContain('id: "diagnostics.report_problem"');
    expect(sources.runtime).toContain('"runtime.state_machine.run"');
    expect(sources.runtime).toContain('"max_frames", "state_constraint_failed"');
    expect(sources.screenshot).toContain('"editor.window.screenshot"');
    expect(sources.screenshot).toContain('"window_index", "out_of_range"');
  });
});

function readCommand(name: string): string {
  return fs.readFileSync(path.join(packageRoot, "Editor", "Commands", name), "utf8");
}
