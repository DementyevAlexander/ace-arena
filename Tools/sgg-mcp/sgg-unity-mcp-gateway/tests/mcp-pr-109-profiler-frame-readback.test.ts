import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandsPath = path.join(packageRoot, "Editor", "Commands", "ProfilerReadonlyCommands.cs");
const policyPath = path.join(packageRoot, "Editor", "Commands", "ProfilerFrameReadPolicy.cs");

describe("MCP-PR-109 authoritative profiler frame readback", () => {
  it("covers the indexed empty-frame report with one shared reader", () => {
    const reportId = "problem_20260718T145547_748daaf458ca";
    const source = read(commandsPath);

    expect(reportId).toMatch(/^problem_[0-9]{8}T[0-9]{6}_[0-9a-f]{12}$/);
    expect(source.match(/ReadProfilerFrameMarkers\(/g)?.length).toBeGreaterThanOrEqual(5);
    expect(source).toContain("SearchProfilerMarkersJson(result, query, caseSensitive, includeChildren)");
  });

  it("distinguishes unavailable, unsettled, invalid, authoritative empty and bounded empty", () => {
    const commands = read(commandsPath);
    const policy = read(policyPath);

    expect(policy).toContain('internal const string Invalid = "invalid"');
    expect(policy).toContain('internal const string Unsettled = "unsettled"');
    expect(policy).toContain('internal const string Unavailable = "unavailable"');
    expect(policy).toContain('return truncated ? "authoritative_bounded_empty" : "authoritative_empty"');
    expect(policy).toContain('"authoritative_empty"');
    expect(commands).toContain("firstFrameIndex");
    expect(commands).toContain("lastFrameIndex");
    expect(commands).toContain("Requested profiler frame_index is outside the current recorded frame range");
    expect(commands).toContain("The selected profiler frame is in range but its thread data is not settled");
    expect(commands).toContain("Profiler iterator traversal failed before the frame could be read authoritatively");
    expect(commands).toContain("Profiler frame range could not be revalidated after marker traversal");
    expect(policy).toContain("TraversalLimitReached");
    expect(policy).toContain("IsFinite");
  });

  it("uses current Unity iterator members and reports unavailable timing fields as null", () => {
    const source = read(commandsPath);

    expect(source).toContain('GetMethod("GetThreadCount"');
    expect(source).toContain('GetMethod("GetThreadName"');
    expect(source).toContain('GetProperty("name"');
    expect(source).toContain('GetProperty("durationMS"');
    expect(source).toContain('GetProperty("depth"');
    expect(source).toContain('marker.Calls.HasValue ? marker.Calls.Value.ToString(CultureInfo.InvariantCulture) : "null"');
    expect(source).toContain('marker.SelfMs ?? "null"');
    expect(source).not.toContain('"threadCount", 0');
    expect(source).not.toContain('"GetCallsCount", 0');
    expect(source).not.toContain('"GetSelfTimeMS"');
  });

  it("preserves unavailable frame state through report memory and artifact reads", () => {
    const source = read(commandsPath);

    expect(source).toContain('"artifact_schema_version\\":2');
    expect(source).toContain('snapshot.DataAvailable ? "ok" : "frame_unavailable"');
    expect(source).toContain('snapshot.SelectionState');
    expect(source).toContain('snapshot.ResultState');
    expect(source).toContain('"legacy_unverified"');
    expect(source).toContain('JsonUtil.ExtractBool(content, "data_available", false)');
    expect(source).toContain('ProfilerFrameReadPolicy.IsAuthoritativeResultEvidence(selectionState, resultState)');
    expect(source).toContain('status = artifactSchemaVersion >= 2');
    expect(source).toContain(': "legacy_unverified"');
    expect(source).toContain('reportAvailable && !string.Equals(descriptor.Status, "ok", StringComparison.Ordinal)');
  });
});

function read(filePath: string): string {
  return fs.readFileSync(filePath, "utf8");
}
