import { describe, expect, it } from "vitest";

import { dispatchTool, type DispatcherContext } from "../src/mcp/dispatcher.js";
import { gatewayResultToMcpToolResult } from "../src/mcp/result-presentation.js";
import {
  COMPACT_SELECTOR_RECORD_CONTRACT,
  COMPACT_NO_LIVE_SELECTOR_ERROR_CONTRACT,
  MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES,
  MAX_SELECTOR_ERROR_BYTES,
  MAX_SELECTOR_RECORD_BYTES,
  MAX_SELECTOR_RECORDS,
  boundSelectorErrorResult,
  projectSelectorRecords,
} from "../src/mcp/selector-response-bounds.js";
import { legacyTextCompatibleToolResult } from "../src/mcp/stdio-shim.js";
import type { ProjectRegistry, ProjectResolution, PublicProjectRecord } from "../src/registry/project-registry.js";

describe("MCP-PR-153A selector response bounds", () => {
  it("projects 126 stale matches once with exact counts and executable continuation", async () => {
    const records = Array.from({ length: 126 }, (_, index) => projectRecord(index, true));
    const currentCandidates = Array.from({ length: 20 }, (_, index) => ({
      ...projectRecord(index, false),
      instance_id: `game-live-${String(index).padStart(3, "0")}`,
    }));
    const context = contextWithResolution({ status: "error", code: "project_stale", matches: records }, [...records, ...currentCandidates]);

    const first = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "asset.import",
      args_json: "{}",
      mode: "wait",
    }, context);
    const second = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "asset.import",
      args_json: "{}",
      mode: "wait",
    }, context);

    expect(first.status).toBe("error");
    expect(Buffer.byteLength(JSON.stringify(first), "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_ERROR_BYTES);
    expect(JSON.stringify(first)).toBe(JSON.stringify(second));
    const mcpResult = gatewayResultToMcpToolResult(first);
    const directErrorText = mcpResult.content[0]?.type === "text" ? mcpResult.content[0].text : "";
    const legacyResult = legacyTextCompatibleToolResult(mcpResult);
    const legacyText = legacyResult.content[0]?.type === "text" ? legacyResult.content[0].text : "";
    // V2 retains only text for isError: keep the exact bounded selector recovery, not a summary.
    expect(JSON.parse(directErrorText)).toEqual(first);
    expect(mcpResult.isError).toBe(true);
    expect(Buffer.byteLength(directErrorText, "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_ERROR_BYTES);
    expect(Buffer.byteLength(JSON.stringify(mcpResult.structuredContent), "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_ERROR_BYTES);
    expect(Buffer.byteLength(legacyText, "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_ERROR_BYTES);
    if (first.status === "error") {
      const matches = first.error.details?.matches as Array<Record<string, unknown>>;
      expect(matches).toHaveLength(MAX_SELECTOR_RECORDS);
      expect(first.error.details?.matches_summary).toMatchObject({
        retained_count: MAX_SELECTOR_RECORDS,
        total_count: 126,
        truncated: true,
        limits: { max_records: MAX_SELECTOR_RECORDS, max_bytes: MAX_SELECTOR_RECORD_BYTES },
        additional_records: { tool: "sgg_unity_stale_projects", args: { project: "game", limit: 16 } },
      });
      expect(matches[0]).toMatchObject({ selector_record_contract: COMPACT_SELECTOR_RECORD_CONTRACT, instance_id: "game-000", stale: true });
      expect(first.error.details?.current_candidate_instances).toHaveLength(MAX_SELECTOR_RECORDS);
      expect(first.error.details?.current_candidates_summary).toMatchObject({ retained_count: MAX_SELECTOR_RECORDS, total_count: 20, truncated: true });
      expect(Object.keys(matches[0]!)).toEqual([
        "selector_record_schema_version",
        "selector_record_contract",
        "project",
        "display_name",
        "instance_id",
        "canonical_project_root",
        "stale",
        "status",
        "last_heartbeat",
        "unity_pid",
        "unity_process_start_utc",
        "heartbeat_generation",
        "has_unsaved_scene_changes",
        "dirty_scene_count",
      ]);
      expect(selectorRecordArrayPaths(first)).toEqual(["error.details.matches", "error.details.current_candidate_instances"]);
      expect(JSON.stringify(first)).not.toMatch(/secret-token|dirty-scene-\d+\.unity|"aliases"|"project_root"|"package_version"/);
    }
  });

  it.each(["full", "compact"])("applies the same 59-match limit in %s response mode", async (responseMode) => {
    const records = Array.from({ length: 59 }, (_, index) => projectRecord(index, true));
    const result = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "asset.import",
      args_json: JSON.stringify({ response_mode: responseMode }),
      mode: "wait",
    }, contextWithResolution({ status: "error", code: "project_stale", matches: records }, records));

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("project_stale");
      expect(result.error.details?.matches).toHaveLength(MAX_SELECTOR_RECORDS);
      expect(result.error.details?.matches_summary).toMatchObject({ retained_count: MAX_SELECTOR_RECORDS, total_count: 59, truncated: true });
      expect(Buffer.byteLength(JSON.stringify(result), "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_ERROR_BYTES);
    }
  });

  it.each([0, 1, 16, 166])("omits bulk stale records for a compact no-live selector with %i exact matches", async (matchCount) => {
    const records = Array.from({ length: matchCount }, (_, index) => projectRecord(index, true));
    const context = contextWithResolution({ status: "error", code: "project_stale", matches: records }, records);
    const args = {
      project: "game",
      command: "compile.status",
      args_json: JSON.stringify({ response_mode: "compact" }),
      mode: "wait" as const,
    };

    const first = await dispatchTool("sgg_unity_call", args, context);
    const second = await dispatchTool("sgg_unity_call", args, context);

    expect(first.status).toBe("error");
    expect(JSON.stringify(first)).toBe(JSON.stringify(second));
    expect(Buffer.byteLength(JSON.stringify(first), "utf8")).toBeLessThanOrEqual(MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES);
    if (first.status === "error") {
      expect(first.error.details).toMatchObject({
        resolution_code: "project_stale",
        matches: [],
        matches_summary: {
          retained_count: 0,
          total_count: matchCount,
          truncated: matchCount > 0,
          bulk_records_omitted: matchCount > 0,
        },
        stale_matched_instances: { count: matchCount, evidence_ref: "error.details.matches_summary.total_count" },
        command_delivery_state: "not_dispatched",
        delivery_state: "not_dispatched",
        queue_admitted: false,
        command_bridge_request_issued: false,
        bridge_request_issued: false,
        command_replayed: false,
        compact_response_bounds: { contract: COMPACT_NO_LIVE_SELECTOR_ERROR_CONTRACT, max_bytes: MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES },
      });
      if (matchCount > 0) {
        expect(first.error.details?.matches_summary).toMatchObject({
          additional_records: { tool: "sgg_unity_stale_projects", args: { project: "game", limit: 16 } },
        });
        expect(first.truncated).toBe(true);
      } else {
        expect(first.error.details?.matches_summary).not.toHaveProperty("additional_records");
      }
      expect(JSON.stringify(first.error.details)).not.toContain("error.details.matches[");
      expect(JSON.stringify(first)).not.toMatch(/dirty-scene-\d+\.unity|"aliases"|"project_root"|"package_version"/);
      expect(JSON.stringify(first)).not.toMatch(/stale\.cleanup|unity\.project\.open|automatic_reroute/);
    }

    if (matchCount === 166) {
      const mcpResult = gatewayResultToMcpToolResult(first);
      const legacyResult = legacyTextCompatibleToolResult(mcpResult);
      const text = mcpResult.content[0]?.type === "text" ? mcpResult.content[0].text : "";
      const legacyText = legacyResult.content[0]?.type === "text" ? legacyResult.content[0].text : "";
      expect(mcpResult.structuredContent).toEqual(first);
      expect(JSON.parse(text)).toEqual(first);
      expect(mcpResult.isError).toBe(true);
      expect(Buffer.byteLength(text, "utf8")).toBeLessThanOrEqual(MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES);
      expect(Buffer.byteLength(legacyText, "utf8")).toBeLessThanOrEqual(MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES);
    }
  });

  it("keeps full stale history explicit while compact mode omits the same stale bodies", async () => {
    const records = Array.from({ length: 166 }, (_, index) => projectRecord(index, true));
    const full = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "compile.status",
      args_json: JSON.stringify({ response_mode: "full" }),
      mode: "wait",
    }, contextWithResolution({ status: "error", code: "project_stale", matches: records }, records));
    const compact = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "compile.status",
      args_json: JSON.stringify({ response_mode: "compact" }),
      mode: "wait",
    }, contextWithResolution({ status: "error", code: "project_stale", matches: records }, records));

    expect(full.status).toBe("error");
    expect(compact.status).toBe("error");
    if (full.status === "error" && compact.status === "error") {
      expect(full.error.details?.matches).toHaveLength(MAX_SELECTOR_RECORDS);
      expect(compact.error.details?.matches).toEqual([]);
      expect(Buffer.byteLength(JSON.stringify(compact), "utf8")).toBeLessThan(Buffer.byteLength(JSON.stringify(full), "utf8") / 2);
    }
  });

  it("keeps duplicate live resolution ambiguous and directs omitted records to live discovery", async () => {
    const records = Array.from({ length: 20 }, (_, index) => projectRecord(index, false));
    const context = contextWithResolution({ status: "error", code: "duplicate_project_id", matches: records }, records);
    const result = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "asset.import",
      args_json: "{}",
      mode: "wait",
    }, context);

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("duplicate_project_id");
      expect(result.error.details?.matches_summary).toMatchObject({
        retained_count: MAX_SELECTOR_RECORDS,
        total_count: 20,
        truncated: true,
        additional_records: { tool: "sgg_unity_projects", args: {} },
      });
      expect(result.error.details?.duplicate_project_recovery).toMatchObject({
        recovery_contract: "sgg.unity_mcp.duplicate_project_recovery.v2",
        matched_instances: { count: 20, evidence_ref: "error.details.matches" },
      });
    }
  });

  it("preserves selectable duplicate live candidates in compact mode", async () => {
    const records = Array.from({ length: 20 }, (_, index) => projectRecord(index, false));
    const result = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "compile.status",
      args_json: JSON.stringify({ response_mode: "compact" }),
      mode: "wait",
    }, contextWithResolution({ status: "error", code: "duplicate_project_id", matches: records }, records));

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("duplicate_project_id");
      expect(result.error.details?.matches).toHaveLength(MAX_SELECTOR_RECORDS);
      expect(result.error.details?.matches_summary).toMatchObject({ retained_count: MAX_SELECTOR_RECORDS, total_count: 20, truncated: true });
      expect(result.error.details).not.toHaveProperty("compact_response_bounds");
    }
  });

  it("keeps compact no-live output bounded when omitted stale records contain multibyte text", async () => {
    const records = Array.from({ length: 166 }, (_, index) => projectRecord(index, true, "プロジェクト".repeat(40)));
    const result = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "compile.status",
      args_json: JSON.stringify({ response_mode: "compact" }),
      mode: "wait",
    }, contextWithResolution({ status: "error", code: "project_stale", matches: records }, records));

    expect(result.status).toBe("error");
    expect(Buffer.byteLength(JSON.stringify(result), "utf8")).toBeLessThanOrEqual(MAX_COMPACT_NO_LIVE_SELECTOR_ERROR_BYTES);
    if (result.status === "error") {
      expect(result.error.details?.matches).toEqual([]);
      expect(result.error.details?.matches_summary).toMatchObject({ retained_count: 0, total_count: 166, truncated: true });
      expect(JSON.stringify(result)).not.toContain("プロジェクト");
    }
  });

  it("uses UTF-8 bytes rather than character counts for multibyte records", () => {
    const records = Array.from({ length: 126 }, (_, index) => projectRecord(index, true, "プロジェクト".repeat(40)));
    const projection = projectSelectorRecords(records, { tool: "sgg_unity_stale_projects", args: { project: "game", limit: 16 } });

    expect(projection.records.length).toBeGreaterThan(0);
    expect(projection.records.length).toBeLessThanOrEqual(MAX_SELECTOR_RECORDS);
    expect(Buffer.byteLength(JSON.stringify(projection.records), "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_RECORD_BYTES);
    expect(projection.summary).toMatchObject({ total_count: 126, retained_count: projection.records.length, truncated: true });
  });

  it("drops optional observations before exceeding the complete selector error ceiling", () => {
    const record = projectSelectorRecords([projectRecord(0, true)], { tool: "sgg_unity_stale_projects", args: { project: "game", limit: 16 } });
    const result = boundSelectorErrorResult({
      status: "error",
      diagnostics: [],
      project: "game",
      command: "test.run.status",
      error: {
        code: "project_stale",
        message: "Project is stale",
        details: {
          recovery_contract: "sgg.unity_mcp.stale_command_recovery.v2",
          project_selector: "game",
          matches: record.records,
          matches_summary: record.summary,
          bridge_reload_trace_tail: { lines: ["x".repeat(70_000)] },
          test_run_recovery: {
            recovery_contract: "sgg.unity_mcp.test_run_rescue.v2",
            operation_id: "test-bound-operation",
            test_run_replayed: false,
            write_command_repeated: false,
          },
          command_delivery_state: "not_dispatched",
          command_replayed: false,
        },
      },
      next_valid_actions: ["call_sgg_unity_stale_projects"],
    });

    expect(Buffer.byteLength(JSON.stringify(result), "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_ERROR_BYTES);
    expect(result).toMatchObject({
      status: "error",
      truncated: true,
      error: {
        details: {
          command_delivery_state: "not_dispatched",
          command_replayed: false,
          test_run_recovery: { operation_id: "test-bound-operation", test_run_replayed: false, write_command_repeated: false },
          response_bounds: { truncated: true, max_bytes: MAX_SELECTOR_ERROR_BYTES, omitted_sections: ["bridge_reload_trace_tail"] },
        },
      },
    });
    if (result.status === "error") {
      expect((result.error.details?.response_bounds as { measured_bytes?: number }).measured_bytes).toBe(Buffer.byteLength(JSON.stringify(result), "utf8"));
    }
  });

  it("drops replacement arrays and their nested references atomically in the essential fallback", () => {
    const matches = projectSelectorRecords([projectRecord(0, true)], { tool: "sgg_unity_stale_projects", args: { project: "game", limit: 16 } });
    const candidates = projectSelectorRecords(
      Array.from({ length: 20 }, (_, index) => ({ ...projectRecord(index, false), instance_id: `game-live-${index}` })),
      { tool: "sgg_unity_projects", args: {} },
    );
    const result = boundSelectorErrorResult({
      status: "error",
      diagnostics: [],
      project: "game",
      command: "test.run.status",
      error: {
        code: "project_stale",
        message: "Project is stale",
        details: {
          recovery_contract: "sgg.unity_mcp.stale_command_recovery.v2",
          project_selector: "game",
          matches: matches.records,
          matches_summary: matches.summary,
          current_candidate_instances: candidates.records,
          current_candidates_summary: candidates.summary,
          recent_instance_replacement: { diagnostic: "x".repeat(70_000) },
          test_run_recovery: {
            recovery_contract: "sgg.unity_mcp.test_run_rescue.v2",
            operation_id: "test-bound-operation",
            current_candidate_selectors: { count: 20, evidence_ref: "error.details.current_candidate_instances" },
            test_run_replayed: false,
            write_command_repeated: false,
          },
          command_delivery_state: "not_dispatched",
          command_replayed: false,
        },
      },
      next_valid_actions: ["call_sgg_unity_projects"],
    });

    expect(Buffer.byteLength(JSON.stringify(result), "utf8")).toBeLessThanOrEqual(MAX_SELECTOR_ERROR_BYTES);
    expect(result).toMatchObject({
      status: "error",
      error: {
        details: {
          current_candidates_summary: { retained_count: 0, total_count: 20, truncated: true },
          test_run_recovery: { operation_id: "test-bound-operation", test_run_replayed: false, write_command_repeated: false },
          response_bounds: {
            omitted_sections: expect.arrayContaining(["nonessential_error_details", "current_candidate_instances", "nonessential_test_run_recovery"]),
          },
        },
      },
    });
    if (result.status === "error") {
      expect(result.error.details).not.toHaveProperty("current_candidate_instances");
      expect(result.error.details?.test_run_recovery).not.toHaveProperty("current_candidate_selectors");
    }
  });
});

function projectRecord(index: number, stale: boolean, displayName = `Game ${index}`): PublicProjectRecord {
  const suffix = String(index).padStart(3, "0");
  return {
    schema_version: 1,
    instance_id: `game-${suffix}`,
    project: "game",
    display_name: displayName,
    package_version: "2026.8.26-2",
    project_root: `C:/Work/Games/game-${suffix}`,
    canonical_project_root: `c:/work/games/game-${suffix}`,
    aliases: [{ environment: "windows", host_path: `C:\\Work\\Games\\game-${suffix}`, canonical_path: `c:/work/games/game-${suffix}` }],
    unity_version: "6000.5.9f1",
    unity_pid: 10_000 + index,
    unity_process_start_utc: "2026-08-26T08:00:00.000Z",
    bridge_host: "127.0.0.1",
    bridge_port: 20_000 + index,
    status: "ready",
    has_unsaved_scene_changes: index % 2 === 0,
    dirty_scene_count: index % 2,
    dirty_scenes: [{ scene_path: `Assets/Scenes/dirty-scene-${index}.unity`, scene_name: `dirty-scene-${index}`, is_active: true }],
    commands_hash: "commands-hash",
    catalog_status: "ready",
    catalog_generation: 1,
    heartbeat_generation: index + 1,
    last_heartbeat: "2026-08-26T09:00:00.000Z",
    stale,
  };
}

function contextWithResolution(resolution: ProjectResolution, records: PublicProjectRecord[]): DispatcherContext {
  let nowMs = Date.parse("2026-08-26T09:00:00.000Z");
  return {
    registry: {
      resolveProject: () => resolution,
      listProjects: () => records,
      resolveRecentReplacementRecord: () => ({ status: "error", code: "project_not_found" }),
    } as unknown as ProjectRegistry,
    now: () => new Date(nowMs),
    sleepMs: async (ms) => { nowMs += ms; },
  };
}

function selectorRecordArrayPaths(value: unknown, path = "", output: string[] = []): string[] {
  if (Array.isArray(value)) {
    if (value.some((item) => isRecord(item) && item.selector_record_contract === COMPACT_SELECTOR_RECORD_CONTRACT)) output.push(path);
    value.forEach((item, index) => selectorRecordArrayPaths(item, `${path}[${index}]`, output));
    return output;
  }
  if (!isRecord(value)) return output;
  for (const [key, child] of Object.entries(value)) selectorRecordArrayPaths(child, path ? `${path}.${key}` : key, output);
  return output;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === "object" && value !== null && !Array.isArray(value);
}
