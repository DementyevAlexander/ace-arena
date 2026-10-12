import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const reflectionCommandsPath = path.join(packageRoot, "Editor", "Commands", "UnityApiReflectionCommands.cs");

describe("Unity API reflection command source-contract", () => {
  it("declares Unity API reflection commands through the catalog", () => {
    const source = fs.readFileSync(reflectionCommandsPath, "utf8");

    expect(source).toContain('"unity.api.loaded_assemblies"');
    expect(source).toContain('"unity.api.reflect.search"');
    expect(source).toContain('"unity.api.reflect.type"');
    expect(source).toContain('"unity.api.reflect.member"');
  });

  it("keeps API reflection read-only and feature-gated", () => {
    const source = fs.readFileSync(reflectionCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "unity_api_reflection"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(4);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(4);
    expect(source).toContain('Idempotency = "safe"');
  });

  it("uses local loaded reflection only", () => {
    const source = fs.readFileSync(reflectionCommandsPath, "utf8");

    expect(source).toContain("EditorAssemblyDiscovery.LoadedAssemblies()");
    expect(source).not.toContain("AppDomain.CurrentDomain.GetAssemblies()");
    expect(source).toContain("assembly.GetTypes()");
    expect(source).toContain("BindingFlags.Public");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
    expect(source).not.toContain("File.Read");
    expect(source).not.toContain("Process.Start");
  });

  it("bounds queries, result counts, and signatures", () => {
    const source = fs.readFileSync(reflectionCommandsPath, "utf8");

    expect(source).toContain('"minLength\\":2');
    expect(source).toContain('"maxLength\\":128');
    expect(source).toContain('"maximum\\":200');
    expect(source).toContain('"maximum\\":100');
    expect(source).toContain("query.Trim().Length < 2");
    expect(source).toContain("Bounded(Signature(member), 360)");
    expect(source).not.toContain("GetCustomAttributes");
  });

  it("returns clear not-found errors", () => {
    const source = fs.readFileSync(reflectionCommandsPath, "utf8");

    expect(source).toContain('CommandExecutionResult.Error("type_not_found"');
    expect(source).toContain('CommandExecutionResult.Error("member_not_found"');
  });
});
