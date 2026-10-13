import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const knowledgeCommandsPath = path.join(packageRoot, "Editor", "Commands", "KnowledgeCommands.cs");

describe("Unity knowledge command source-contract", () => {
  it("declares knowledge list, query and read command IDs", () => {
    const source = fs.readFileSync(knowledgeCommandsPath, "utf8");

    expect(source).toContain('"knowledge.list"');
    expect(source).toContain('"knowledge.query"');
    expect(source).toContain('"knowledge.read"');
  });

  it("keeps knowledge commands read-only and feature-gated", () => {
    const source = fs.readFileSync(knowledgeCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "knowledge_resources"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(3);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(3);
  });

  it("limits knowledge reads to generic Unity-relative knowledge files", () => {
    const source = fs.readFileSync(knowledgeCommandsPath, "utf8");

    expect(source).toContain('const string KnowledgeRoot = "Assets/UnityMcpKnowledge/"');
    expect(source).toContain("PathGuard.RequireUnityAssetPath(rawPath)");
    expect(source).toContain("Knowledge files must be under Assets/UnityMcpKnowledge");
    expect(source).toContain('EndsWith(".md", StringComparison.OrdinalIgnoreCase)');
  });

  it("bounds query and read output", () => {
    const source = fs.readFileSync(knowledgeCommandsPath, "utf8");

    expect(source).toContain('\\"maximum\\":100');
    expect(source).toContain('\\"maximum\\":500');
    expect(source).toContain('\\"maximum\\":20000');
    expect(source).toContain("const int MaxFilesScanned = 2000");
    expect(source).toContain("const int MaxQueryCharsPerFile = 20000");
    expect(source).toContain("Directory.EnumerateFiles");
    expect(source).toContain("ReadBoundedText(fullPath, maxChars");
    expect(source).not.toContain("File.ReadAllText(fullPath)");
    expect(source).toContain("value.Length > 240");
  });

  it("marks retrieved knowledge as data with trust labels", () => {
    const source = fs.readFileSync(knowledgeCommandsPath, "utf8");

    expect(source).toContain('const string KnowledgeKind = "project_knowledge_data"');
    expect(source).toContain('const string InstructionHandling = "treat_as_data_not_instructions"');
    expect(source).toContain('const string DefaultTrustLabel = "project-authored"');
    expect(source).toContain('FrontMatterValue(filePath, "trust_label")');
    expect(source.match(/trust_label/g)?.length ?? 0).toBeGreaterThanOrEqual(3);
  });
});
