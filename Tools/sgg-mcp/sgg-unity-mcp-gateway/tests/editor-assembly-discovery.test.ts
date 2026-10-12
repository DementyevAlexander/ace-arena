import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const commandsRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp", "Editor", "Commands");
const discoveryPath = path.join(commandsRoot, "EditorAssemblyDiscovery.cs");
const runtimeTestPath = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp", "Tests", "Editor", "EditorAssemblyDiscoveryTests.cs");

describe("MCP-PR-196 Editor assembly discovery", () => {
  it("routes package-owned assembly discovery through one compatibility seam with shared catalog lookups", () => {
    const files = fs.readdirSync(commandsRoot, { recursive: true, encoding: "utf8" })
      .filter((file) => file.endsWith(".cs"))
      .map((file) => path.join(commandsRoot, file));
    const consumers = files.filter((file) => file !== discoveryPath).map((file) => fs.readFileSync(file, "utf8")).join("\n");
    const discovery = fs.readFileSync(discoveryPath, "utf8");

    // The former 26 sites now use 25 calls: one catalog-build snapshot serves
    // both attributed discovery and all manifest handler resolutions.
    expect(consumers.match(/EditorAssemblyDiscovery\.LoadedAssemblies\(\)/g)).toHaveLength(25);
    expect(consumers).not.toContain("AppDomain.CurrentDomain.GetAssemblies()");
    expect(discovery.match(/AppDomain\.CurrentDomain\.GetAssemblies\(\)/g)).toHaveLength(1);
    expect(discovery.indexOf("#if UNITY_6000_6_OR_NEWER")).toBeLessThan(discovery.indexOf("AppDomain.CurrentDomain.GetAssemblies()"));
    expect(discovery).not.toContain("UAC0005");
    expect(discovery).not.toContain("#pragma warning");
  });

  it("shares a single catalog-build assembly snapshot instead of rediscovering every manifest handler", () => {
    const registry = fs.readFileSync(path.join(commandsRoot, "CommandRegistry.cs"), "utf8");
    const resolver = fs.readFileSync(path.join(commandsRoot, "ManifestHandlerResolver.cs"), "utf8");

    expect(registry.match(/EditorAssemblyDiscovery\.LoadedAssemblies\(\)/g)).toHaveLength(1);
    expect(registry).toContain("new ManifestHandlerResolver(assemblies)");
    expect(registry).toContain("RegisterAttributedCommands(assemblies)");
    expect(resolver).not.toContain("EditorAssemblyDiscovery.LoadedAssemblies()");
  });

  it("uses bounded deterministic Unity-supported discovery on Unity 6000.6 and newer", () => {
    const source = fs.readFileSync(discoveryPath, "utf8");

    expect(source).toContain("TypeCache.GetTypesDerivedFrom<object>()");
    expect(source).toContain("TypeCache.GetTypesDerivedFrom<ValueType>()");
    expect(source).toContain("UnityEditor.Compilation.CompilationPipeline.GetAssemblies()");
    expect(source).toContain("const int MaxAssemblies = 2048");
    expect(source).toContain("const int MaxTypeCacheTypes = 200000");
    expect(source).toContain("Array.Sort(result, CompareAssemblies)");
    expect(source).toContain("catch");
  });

  it("has an EditMode contract for bounds, ordering, uniqueness, and known assemblies", () => {
    const source = fs.readFileSync(runtimeTestPath, "utf8");

    expect(source).toContain("first.Length, Is.LessThanOrEqualTo(2048)");
    expect(source).toContain("typeof(object).Assembly");
    expect(source).toContain("typeof(EditorAssemblyDiscovery).Assembly");
    expect(source).toContain("typeof(EditorAssemblyDiscoveryTests).Assembly");
    expect(source).toContain("unique.Add(first[index])");
    expect(source).toContain("CompareAssemblies(first[index - 1], first[index])");
  });
});
