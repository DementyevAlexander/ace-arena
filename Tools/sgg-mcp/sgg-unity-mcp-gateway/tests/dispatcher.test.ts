import { describe, expect, it } from "vitest";

import { dispatchTool } from "../src/mcp/dispatcher.js";
import { ERROR_CODES, isErrorCode } from "../src/mcp/results.js";

describe("gateway result contracts", () => {
  it("keeps documented error codes stable", () => {
    expect(ERROR_CODES).toContain("project_required");
    expect(ERROR_CODES).toContain("selector_not_supported");
    expect(ERROR_CODES).toContain("protocol_version_mismatch");
    expect(ERROR_CODES).toContain("batch_mode_not_supported");
    expect(ERROR_CODES).toContain("project_required_for_log_kind");
    expect(ERROR_CODES).toContain("args_json_must_be_object");
    expect(ERROR_CODES).toContain("items_json_must_be_array");
    expect(ERROR_CODES).toContain("feature_disabled");
    expect(ERROR_CODES).toContain("type_not_found");
    expect(ERROR_CODES).toContain("member_not_found");
    expect(ERROR_CODES).toContain("package_operation_not_found");
    expect(ERROR_CODES).toContain("test_run_dirty_scene_blocked");
    expect(ERROR_CODES).toContain("test_run_scene_state_unavailable");
    expect(ERROR_CODES).toContain("test_run_status_unavailable");
    expect(ERROR_CODES).toContain("capture_target_unavailable");
    expect(ERROR_CODES).toContain("precondition_required");
    expect(ERROR_CODES).toContain("precondition_failed");
    expect(ERROR_CODES).toContain("serialized_write_failed");
    expect(ERROR_CODES).toContain("mcp_session_not_found");
    expect(isErrorCode("project_not_found")).toBe(true);
    expect(isErrorCode("set_active_required")).toBe(false);
  });

  it("returns an empty project list before registry implementation", async () => {
    await expect(dispatchTool("sgg_unity_projects", {})).resolves.toEqual({
      status: "ok",
      data: {
        projects: [],
        counts: { total: 0, live: 0, stale: 0, unique_projects: 0 },
        registry_operational_state: "unavailable",
        startup_blockers: {
          schema_version: 1,
          scan_status: "unsupported",
          diagnostic_only: true,
          read_only: true,
          interaction_attempted: false,
          process_action_attempted: false,
          candidate_count: 0,
          candidates: [],
          candidates_truncated: false,
          zero_candidates_authoritative: false,
          errors: ["window_diagnostics_unavailable"],
          errors_truncated: false,
          next_valid_actions: ["operator_inspect_unity_editor_startup", "do_not_interact_with_unity_prompts_from_gateway", "call_diagnostics.report_problem"],
        },
      },
      diagnostics: [],
    });
  });

  it("normalizes missing project into schema_validation_failed", async () => {
    const result = await dispatchTool("sgg_unity_call", { command: "compile.status" });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("schema_validation_failed");
    }
  });

  it("normalizes args_json object failures", async () => {
    const result = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "compile.status",
      args_json: "[]",
    });

    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.error.code).toBe("args_json_must_be_object");
      expect(result.next_valid_actions).toEqual(["sgg_unity_projects"]);
      expect(result.error.details).toMatchObject({
        schema_lookup: {
          tool: "sgg_unity_projects",
          arguments: {},
          metadata_lookup: {
            action_kind: "mcp_tool",
            tool: "sgg_unity_projects",
            arguments: {},
          },
        },
      });
    }
  });

  it("returns stable project_not_found until registry is implemented", async () => {
    let sleepCalls = 0;
    const result = await dispatchTool("sgg_unity_call", {
      project: "game",
      command: "compile.status",
      args_json: "{}",
    }, {
      sleepMs: async () => {
        sleepCalls += 1;
      },
    });

    expect(sleepCalls).toBe(0);
    expect(result.status).toBe("error");
    if (result.status === "error") {
      expect(result.project).toBe("game");
      expect(result.command).toBe("compile.status");
      expect(result.error.code).toBe("project_not_found");
      expect(result.next_valid_actions).toEqual(["call_sgg_unity_projects", "inspect_project_status"]);
    }
  });
});
