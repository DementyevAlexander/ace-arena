import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "FrameDebuggerReadonlyCommands.cs");
const metaPath = `${commandPath}.meta`;
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const commandsDocsPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const securityDocsPath = path.join(process.cwd(), "docs", "en", "reference", "security.md");

describe("Unity Frame Debugger read-only command source-contract", () => {
  it("declares status and event commands behind profiler_readonly", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const meta = fs.readFileSync(metaPath, "utf8");

    expect(source).toContain('"frame_debugger.status"');
    expect(source).toContain('"frame_debugger.events"');
    expect(source).toContain('const string FeatureFlag = "profiler_readonly"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(2);
    expect(source.match(/Idempotency = "safe"/g)).toHaveLength(2);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(2);
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("uses reflected read-only Frame Debugger APIs with bounded output", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("ResolveFrameDebuggerUtilityType");
    expect(source).toContain("UnityEditorInternal.FrameDebuggerUtility");
    expect(source).toContain("ResolveFrameDebuggerEventDataType");
    expect(source).toContain("UnityEditorInternal.FrameDebuggerEventData");
    expect(source).toContain("ResolveGetFrameEventDataMethod");
    expect(source).toContain("GetFrameEventData");
    expect(source).toContain("FrameEventJson");
    expect(source).toContain('\\"max_events\\"');
    expect(source).toContain("MaxEvents = 500");
    expect(source).toContain('\\"write_capabilities_exposed\\":false');
    expect(source).toContain('\\"state_changed\\":false');
  });

  it("does not enable, step, select, capture, or mutate Frame Debugger state", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).not.toContain("SetEnabled");
    expect(source).not.toContain("SetEventIndex");
    expect(source).not.toContain("EnableFrameDebugger");
    expect(source).not.toContain("DisableFrameDebugger");
    expect(source).not.toContain("SaveProfile");
    expect(source).not.toContain("StartRecording");
    expect(source).not.toContain("StopRecording");
    expect(source).not.toContain("File.Write");
    expect(source).not.toContain("Process.Start");
  });

  it("adds exact namespace fallbacks after legacy candidates without broadening signature or invocation gates", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    for (const suffix of ["FrameDebuggerUtility", "FrameDebuggerEventData"]) {
      const first = source.indexOf(`ResolveType("UnityEditorInternal.${suffix}", resolver)`);
      const second = source.indexOf(`ResolveType("UnityEditor.${suffix}", resolver)`);
      const fallback = source.indexOf(`ResolveType("UnityEditorInternal.FrameDebuggerInternal.${suffix}", resolver)`);
      expect(first).toBeGreaterThan(-1);
      expect(second).toBeGreaterThan(first);
      expect(fallback).toBeGreaterThan(second);
    }
    expect(source).toContain('string.Equals(method.Name, "GetFrameEventData", StringComparison.Ordinal)');
    expect(source).toContain("BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static");
    expect(source).toContain("parameters.Length != 2 || parameters[0].ParameterType != typeof(int)");
    expect(source).toContain("parameterType == eventDataType");
    expect(source).toContain("parameterType.IsByRef && parameterType.GetElementType() == eventDataType");
    expect(source).toContain("utilityType == null || eventDataType == null");
    const lookup = source.slice(source.indexOf("internal static Type ResolveFrameDebuggerUtilityType"), source.indexOf("static bool TryReadFrameEvent"));
    expect(lookup).not.toContain(".Invoke(");
    expect(lookup).not.toContain(".GetValue(");
    expect(lookup).not.toContain("Activator.CreateInstance");
  });

  it("documents and allows the read-only Frame Debugger slice intentionally", () => {
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");
    const commandsDocs = fs.readFileSync(commandsDocsPath, "utf8");
    const securityDocs = fs.readFileSync(securityDocsPath, "utf8");

    expect(policyExample).toContain('"frame_debugger.status"');
    expect(policyExample).toContain('"frame_debugger.events"');
    expect(policyProfiles).toContain('"frame_debugger.status"');
    expect(policyProfiles).toContain('"frame_debugger.events"');
    expect(commandsDocs).toContain("frame_debugger.status");
    expect(commandsDocs).toContain("frame_debugger.events");
    expect(securityDocs).toContain("frame_debugger.status");
    expect(securityDocs).toContain("frame_debugger.events");
  });
});
