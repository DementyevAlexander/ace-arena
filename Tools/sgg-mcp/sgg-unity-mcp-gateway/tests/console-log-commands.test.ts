import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandsRoot = path.join(packageRoot, "Editor", "Commands");
const builtInPath = path.join(commandsRoot, "BuiltInCommands.cs");
const bufferPath = path.join(commandsRoot, "ConsoleLogBuffer.cs");
const policyExampleConfigPath = path.join(process.cwd(), "config", "policy.example.json");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const commandsRuDocsPath = path.join(process.cwd(), "docs", "ru", "reference", "commands.md");
const compatibilityDocsPath = path.join(process.cwd(), "docs", "en", "connect", "unity-ai-assistant.md");

describe("Unity console log commands", () => {
  it("declares read-only console tail and filtered read commands", () => {
    const source = fs.readFileSync(builtInPath, "utf8");

    expect(source).toContain('"logs.console.tail"');
    expect(source).toContain('"logs.console.read"');
    expect(source).toContain('"logs.console.query"');
    expect(source).toContain("ConsoleLogReadSchemaJson");
    expect(source).toContain("ConsoleLogQuerySchemaJson");
    expect(source).toContain("ConsoleLogReadOutputLimitJson");
    expect(source).toContain("ConsoleLogQueryOutputLimitJson");
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain('ResultShape = "bounded_list"');
    expect(source).toContain('"limit"');
    expect(source).toContain('"severity"');
    expect(source).toContain('"types"');
    expect(source).toContain('"contains"');
    expect(source).toContain('"filter_text"');
    expect(source).toContain('"since"');
    expect(source).toContain('"since_utc"');
    expect(source).toContain('"include_stack_trace"');
    expect(source).toContain('"max_chars"');
    expect(source).toContain('"max_message_chars"');
    expect(source).toContain('"max_stack_chars"');
  });

  it("filters the existing console ring buffer without clearing or reflection", () => {
    const source = fs.readFileSync(bufferPath, "utf8");
    const readSource = source.slice(source.indexOf("public static string ReadJson"), source.indexOf("static void OnLogMessageReceived"));

    expect(readSource).toContain("MatchesTypeFilter(entry.Type, types)");
    expect(readSource).toContain("IndexOf(filterText, StringComparison.OrdinalIgnoreCase)");
    expect(readSource).toContain("includeStackTrace");
    expect(readSource).toContain("Truncate(entry.Message, maxMessageChars)");
    expect(readSource).toContain("Truncate(entry.StackTrace, maxStackChars)");
    expect(readSource).toContain('\\"cleared\\":false');
    expect(readSource).toContain('\\"buffer\\"');
    expect(source).toContain("BufferMetadataKey");
    expect(source).toContain("SessionState.GetString(BufferMetadataKey");
    expect(source).toContain("Guid.NewGuid().ToString(\"N\")");
    expect(source).toContain('\\"reset_loss\\"');
    expect(source).toContain('\\"history_complete\\":false');
    expect(readSource).toContain("matched_count");
    expect(readSource).toContain("ContainsFilterToken(filter, \"error\")");
    expect(readSource).not.toContain("Clear");
    expect(readSource).not.toContain("UnityEditor.LogEntries");
    expect(readSource).not.toContain("BindingFlags");
  });

  it("queries the console ring buffer with severity aliases and bounded output", () => {
    const source = fs.readFileSync(bufferPath, "utf8");
    const querySource = source.slice(source.indexOf("public static string QueryJson"), source.indexOf("static bool MatchesTypeFilter"));
    const builtInSource = fs.readFileSync(builtInPath, "utf8");

    expect(builtInSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "severity")');
    expect(builtInSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "types")');
    expect(builtInSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "contains")');
    expect(builtInSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "filter_text")');
    expect(builtInSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "since")');
    expect(builtInSource).toContain('JsonUtil.ExtractString(context.ArgsJson, "since_utc")');
    expect(querySource).toContain("Math.Min(500, limit)");
    expect(querySource).toContain("Math.Min(100000, maxChars)");
    expect(querySource).toContain("MatchesTypeFilter(entry.Type, severity)");
    expect(querySource).toContain("IndexOf(contains, StringComparison.OrdinalIgnoreCase)");
    expect(querySource).toContain("TryParseSinceUtc(since, out var sinceUtc)");
    expect(querySource).toContain("entry.TimestampUtc < sinceUtc");
    expect(querySource).toContain('\\"status\\":\\"ok\\"');
    expect(querySource).toContain('\\"logs\\"');
    expect(querySource).toContain('\\"returned_count\\"');
    expect(querySource).toContain('\\"filters\\"');
    expect(querySource).toContain('\\"since_valid\\"');
    expect(querySource).toContain('\\"source_available\\":false');
    expect(querySource).toContain('\\"source_note\\"');
    expect(querySource).toContain('\\"next_actions\\"');
    expect(querySource).toContain('\\"cleared\\":false');
    expect(querySource).toContain('\\"buffer\\"');
    expect(source).toContain("TimestampUtc = timestampUtc");
    expect(querySource).not.toContain("Clear");
    expect(querySource).not.toContain("UnityEditor.LogEntries");
  });

  it("keeps the executable compile-recovery query payload in the built-in descriptor", () => {
    const source = fs.readFileSync(builtInPath, "utf8");
    const encodedSchema = source.match(/const string ConsoleLogQuerySchemaJson = ("(?:\\.|[^"\\])*");/)?.[1];

    expect(encodedSchema).toBeDefined();
    const schema = JSON.parse(JSON.parse(encodedSchema as string)) as {
      properties: Record<string, { type?: string; minimum?: number; maximum?: number }>;
      additionalProperties: boolean;
    };
    expect(schema.properties.severity).toMatchObject({ type: "string" });
    expect(schema.properties.limit).toMatchObject({ type: "integer", minimum: 1, maximum: 500 });
    expect(schema.properties.include_stack_trace).toMatchObject({ type: "boolean" });
    expect(schema.additionalProperties).toBe(false);
    expect(source).not.toContain("logs.console.query_errors");
  });

  it("documents policy and Unity AI Assistant console parity", () => {
    const policy = JSON.parse(fs.readFileSync(policyExampleConfigPath, "utf8"));
    const docs = fs.readFileSync(commandsDocsPath, "utf8");
    const ruDocs = fs.readFileSync(commandsRuDocsPath, "utf8");
    const compatibilityDocs = fs.readFileSync(compatibilityDocsPath, "utf8");

    expect(policy.projects.game.allow).toContain("logs.*");
    expect(docs).toContain("logs.console.read");
    expect(docs).toContain("logs.console.query");
    expect(docs).toContain("severity");
    expect(docs).toContain("contains");
    expect(docs).toContain("since");
    expect(docs).toContain("source_available:false");
    expect(docs).toContain("filter_text");
    expect(docs).toContain("include_stack_trace");
    expect(docs).toContain("does not clear the Unity Console");
    expect(ruDocs).toContain("logs.console.read");
    expect(ruDocs).toContain("logs.console.query");
    expect(ruDocs).toContain("severity");
    expect(ruDocs).toContain("contains");
    expect(ruDocs).toContain("since");
    expect(ruDocs).toContain("source_available:false");
    expect(ruDocs).toContain("filter_text");
    expect(ruDocs).toContain("include_stack_trace");
    expect(ruDocs).toContain("не очищает Unity Console");
    expect(compatibilityDocs).toContain("logs.console.read");
  });
});
