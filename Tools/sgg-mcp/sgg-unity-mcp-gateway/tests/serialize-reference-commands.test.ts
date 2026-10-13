import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandsPath = path.join(packageRoot, "Editor", "Commands", "SerializeReferenceCommands.cs");
const invalidatorPath = path.join(packageRoot, "Editor", "Commands", "AssetIndexCacheInvalidator.cs");
const gatewayConfigPath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const fixtureRoot = path.join(process.cwd(), "tests", "fixtures", "serialize-reference-index", "Assets");

describe("Unity SerializeReference command source-contract", () => {
  it("declares SerializeReference commands through the catalog", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain('"serializereference.types.search"');
    expect(source).toContain('"serializereference.index.status"');
    expect(source).toContain('"serializereference.index.rebuild"');
    expect(source).toContain('"serializereference.types.used"');
    expect(source).toContain('"serializereference.usages.find"');
    expect(source).toContain('"serializereference.graph"');
  });

  it("keeps SerializeReference commands read-only and feature-gated", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "serialize_reference_index"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(6);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(6);
    expect(source).toContain("SupportsJob = true");
    expect(source).toContain('Capability = FeatureFlag');
  });

  it("uses TypeCache for assignable Serializable type search", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("TypeCache.GetTypesDerivedFrom(baseType)");
    expect(source).toContain("type.GetCustomAttribute<SerializableAttribute>()");
    expect(source).toContain("typeof(UnityEngine.Object).IsAssignableFrom(type)");
    expect(source).toContain("type.ContainsGenericParameters");
    expect(source).toContain("IsObsolete(type)");
    expect(source).toContain('\\"full_name\\"');
    expect(source).toContain('\\"display_name\\"');
  });

  it("scans Unity YAML references and prefab override managed references", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain('"references:"');
    expect(source).toContain("managedReferences[");
    expect(source).toContain("SerializeReferenceRegex");
    expect(source).toContain("PrefabOverrideSerializeReferenceTypeRegex");
    expect(source).toContain('path.EndsWith(".unity"');
    expect(source).toContain('path.EndsWith(".prefab"');
    expect(source).toContain('path.EndsWith(".asset"');
    expect(source).toContain("GetGenericArgumentTypes()");
    expect(source).toContain("IsSameTypeOrNestedInside");
    expect(source).toContain("ContainsGenericArgument");
    expect(source).toContain("SerializeReferenceGraphBuilder");
    expect(source).toContain("uses_serialize_reference");
    expect(source).toContain("has_generic_argument");
  });

  it("persists cache beside asset index cache and invalidates on YAML asset changes", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const invalidator = fs.readFileSync(invalidatorPath, "utf8");

    expect(source).toContain('CacheFileName = "serialize-reference-index.json"');
    expect(source).toContain('DefaultCacheRelativeDirectory = "Library/SGGUnityMcp"');
    expect(source).toContain('SettingsAssetIndexCacheDir = "asset_index_cache_dir"');
    expect(source).toContain("JsonUtility.ToJson(cache, true)");
    expect(source).toContain("JsonUtility.FromJson<SerializeReferenceIndexCache>");
    expect(invalidator).toContain("SerializeReferenceCommands.InvalidateCache()");
    expect(invalidator).toContain("HasSerializeReferenceAssetChanges");
  });

  it("documents and allows SerializeReference inspection", () => {
    const gatewayConfig = fs.readFileSync(gatewayConfigPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const docsRu = fs.readFileSync(commandsRuDocsPath, "utf8");

    expect(gatewayConfig).toContain("serialize_reference_index");

    expect(policyExample).toContain("serializereference.types.search");
    expect(policyExample).toContain("serializereference.usages.find");
    expect(policyExample).toContain("serializereference.graph");

    for (const text of [policyProfiles, docs, docsRu]) {
      expect(text).toContain("serialize_reference_index");
      expect(text).toContain("serializereference.types.search");
      expect(text).toContain("serializereference.usages.find");
      expect(text).toContain("serializereference.graph");
    }
  });

  it("keeps parser fixtures for references, multiline types, prefab overrides, generics and nested types", () => {
    const parsed = parseFixtureAssets();

    expect(parsed.map((item) => `${item.assetPath}:${item.line}:${item.source}:${item.type}`)).toEqual([
      "Dialogues/Intro.asset:7:references:Game.Dialogue.Nodes.SayTextNode, Game.Runtime",
      "Dialogues/Intro.asset:10:references:Game.Dialogue.Nodes.GenericNode`1[[Game.Dialogue.Payload, Game.Runtime]], Game.Runtime",
      "Dialogues/Intro.asset:13:references:Game.Dialogue.Nodes.Really.Long.Namespace.ReallyLongNode, Game.Runtime",
      "Prefabs/Npc.prefab:8:prefab_override:Game.Dialogue.Nodes.SayTextNode, Game.Runtime",
      "Prefabs/Npc.prefab:12:prefab_override:Game.Dialogue.Nodes.GenericNode`1[[System.Int32, mscorlib]], Game.Runtime",
      "Scenes/Main.unity:7:references:Game.Nested.Outer/Inner, Game.Runtime",
    ]);
  });
});

type ParsedReference = { assetPath: string; line: number; source: string; type: string };

const referenceRegex = /type:\s*\{class:\s*(?:'(?<quotedClass>(?:[^']|'')*)'|(?<class>[^,]+)),\s*ns:\s*(?<namespace>[^,]*),\s*asm:\s*(?<assembly>[^}]+)\}/;
const prefabOverrideRegex = /value:\s+'?(?<assembly>[^\s']+)\s+(?<fullType>.+?)'?\s*$/;

function parseFixtureAssets(): ParsedReference[] {
  return fs.readdirSync(fixtureRoot, { recursive: true })
    .map((entry) => String(entry).replaceAll(path.sep, "/"))
    .filter((entry) => /\.(asset|prefab|unity)$/.test(entry))
    .sort()
    .flatMap((assetPath) => parseAsset(assetPath, fs.readFileSync(path.join(fixtureRoot, assetPath), "utf8")));
}

function parseAsset(assetPath: string, content: string): ParsedReference[] {
  const result: ParsedReference[] = [];
  let insideReferences = false;
  let previous = "";
  let previousLine = 0;
  let pendingPrefabOverride: { text: string; line: number } | undefined;
  const lines = content.split(/\r?\n/);
  for (let index = 0; index < lines.length; index++) {
    const lineNumber = index + 1;
    let line = lines[index] ?? "";
    if (pendingPrefabOverride) {
      const combined = `${pendingPrefabOverride.text} ${line.trim()}`;
      const parsed = parsePrefabOverride(combined);
      if (parsed) {
        result.push({ assetPath, line: pendingPrefabOverride.line, source: "prefab_override", type: parsed });
        pendingPrefabOverride = undefined;
        previous = line.trimStart();
        previousLine = lineNumber;
        continue;
      }
      pendingPrefabOverride = { text: combined, line: pendingPrefabOverride.line };
    }

    if (previous.trimStart().startsWith("propertyPath: 'managedReferences[")) {
      const parsed = parsePrefabOverride(line);
      if (parsed) {
        result.push({ assetPath, line: lineNumber, source: "prefab_override", type: parsed });
      } else if (line.trimStart().startsWith("value:") && isUnclosedQuotedValue(line)) {
        pendingPrefabOverride = { text: line.trimStart(), line: lineNumber };
      }
    }

    line = line.trimStart();
    if (line.startsWith("---")) {
      insideReferences = false;
      previous = line;
      previousLine = lineNumber;
      continue;
    }
    if (!insideReferences && line.startsWith("references:")) {
      insideReferences = true;
      previous = line;
      previousLine = lineNumber;
      continue;
    }
    if (insideReferences) {
      const parsed = parseReference(line, previous);
      if (parsed) {
        result.push({ assetPath, line: parsed.multiLine ? previousLine : lineNumber, source: "references", type: parsed.type });
      }
    }
    previous = line;
    previousLine = lineNumber;
  }
  return result;
}

function parseReference(line: string, previous: string): { type: string; multiLine: boolean } | undefined {
  let match = referenceRegex.exec(line);
  let multiLine = false;
  if (!match && previous.includes("type:") && line.includes("asm:")) {
    match = referenceRegex.exec(previous + line);
    multiLine = true;
  }
  if (!match?.groups) {
    return undefined;
  }
  const namespace = match.groups.namespace;
  const assembly = match.groups.assembly;
  if (!namespace || !assembly) {
    return undefined;
  }
  const className = (match.groups.quotedClass ?? match.groups.class ?? "").replaceAll("''", "'").trim();
  return { type: `${namespace.trim()}.${className}, ${assembly.trim()}`, multiLine };
}

function parsePrefabOverride(value: string): string | undefined {
  if (isUnclosedQuotedValue(value)) {
    return undefined;
  }
  const match = prefabOverrideRegex.exec(value);
  if (!match?.groups) {
    return undefined;
  }
  const fullType = match.groups.fullType;
  const assembly = match.groups.assembly;
  if (!fullType || !assembly) {
    return undefined;
  }
  return `${fullType.trim().replaceAll("+", "/")}, ${assembly.trim()}`;
}

function isUnclosedQuotedValue(value: string): boolean {
  const first = value.indexOf("'");
  return first >= 0 && value.lastIndexOf("'") === first;
}
