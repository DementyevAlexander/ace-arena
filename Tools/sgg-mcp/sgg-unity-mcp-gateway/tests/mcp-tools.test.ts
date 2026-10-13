import { describe, expect, it } from "vitest";

import {
  BATCH_MODES,
  TOOL_ARG_SCHEMAS,
  TOOL_NAMES,
  commandSelectorSchema,
  parseArgsJson,
  parseItemsJson,
} from "../src/mcp/contracts.js";

describe("MCP tool contracts", () => {
  it("exposes only the stable public tools", () => {
    expect(TOOL_NAMES).toEqual([
      "sgg_unity_projects",
      "sgg_unity_stale_projects",
      "sgg_unity_stale_registry_cleanup",
      "sgg_unity_describe",
      "sgg_unity_call",
      "sgg_unity_batch",
      "sgg_unity_job",
      "sgg_unity_logs",
    ]);

    expect(TOOL_NAMES).not.toContain("set_active");
    expect(TOOL_NAMES).not.toContain("set_active_instance");
    expect(TOOL_NAMES).not.toContain("active_project");
  });

  it("bounds stale project queries and defaults stale cleanup to dry-run", () => {
    expect(TOOL_ARG_SCHEMAS.sgg_unity_stale_projects.parse({})).toMatchObject({ limit: 8 });
    expect(TOOL_ARG_SCHEMAS.sgg_unity_stale_projects.parse({ status: "stopping", limit: 16 })).toMatchObject({ status: "stopping", limit: 16 });
    expect(TOOL_ARG_SCHEMAS.sgg_unity_stale_projects.parse({ last_heartbeat_from: "2026-08-14T14:00:00+02:00", last_heartbeat_to: "2026-08-14T12:30:00Z" })).toMatchObject({ limit: 8 });
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_stale_projects.parse({ limit: 17 })).toThrow();
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_stale_projects.parse({ last_heartbeat_from: "2026-08-14T12:00:00.000Z", last_heartbeat_to: "2026-08-14T11:00:00.000Z" })).toThrow();
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_stale_projects.parse({ cursor: "not+a+base64url+cursor" })).toThrow();

    expect(TOOL_ARG_SCHEMAS.sgg_unity_stale_registry_cleanup.parse({})).toMatchObject({ dry_run: true, confirm_cleanup: false, limit: 25 });
    expect(TOOL_ARG_SCHEMAS.sgg_unity_stale_registry_cleanup.parse({
      dry_run: false,
      confirm_cleanup: true,
      record_ids: [`sha256:${"a".repeat(64)}`],
      precondition_hash: `sha256:${"b".repeat(64)}`,
    })).toMatchObject({ dry_run: false, confirm_cleanup: true });
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_stale_registry_cleanup.parse({ record_ids: [`sha256:${"a".repeat(64)}`, `sha256:${"a".repeat(64)}`] })).toThrow();
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_stale_registry_cleanup.parse({ limit: 101 })).toThrow();
    expect(TOOL_ARG_SCHEMAS.sgg_unity_stale_registry_cleanup.parse({ last_heartbeat_before: "2026-08-14T14:00:00+02:00" })).toMatchObject({ dry_run: true });
  });

  it("requires explicit project for Unity-facing tools", () => {
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_describe.parse({ selector: "commands" })).toThrow();
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ command: "compile.status" })).toThrow();
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_job.parse({ job_id: "job-1" })).toThrow();
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_logs.parse({ kind: "console" })).toThrow();
  });

  it("supports documented describe selectors and rejects unsupported selectors", () => {
    for (const selector of ["status", "commands", "catalog", "commands:scene", "commands:asset", "commands:project", "commands:gameobject", "commands:component", "commands:material", "commands:package", "commands:build", "commands:test", "commands:unity-api", "commands:unity-docs", "command:compile.status", "packages", "scenes", "selection", "logs"]) {
      expect(commandSelectorSchema.parse(selector)).toBe(selector);
    }

    expect(commandSelectorSchema.parse("command:compile.status")).toBe("command:compile.status");
    expect(commandSelectorSchema.parse("compile.status")).toBe("compile.status");
    expect(commandSelectorSchema.parse("asset")).toBe("asset");
    expect(() => commandSelectorSchema.parse("commands:unknown")).toThrow();
  });

  it("uses the documented batch modes", () => {
    expect(BATCH_MODES).toEqual(["sequential", "parallel_by_project"]);
    expect(TOOL_ARG_SCHEMAS.sgg_unity_batch.parse({ items_json: "[]" }).mode).toBe("sequential");
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_batch.parse({ mode: "parallel", items_json: "[]" })).toThrow();
  });

  it("validates optional call and batch correlation ids as bounded single names", () => {
    expect(TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ project: "game", command: "compile.status", checkpoint_id: "cp.2026-05-19_1", plan_id: "plan-a" })).toMatchObject({
      checkpoint_id: "cp.2026-05-19_1",
      plan_id: "plan-a",
    });
    expect(TOOL_ARG_SCHEMAS.sgg_unity_batch.parse({ items_json: "[]", checkpoint_id: "checkpoint_1", plan_id: "plan.1" })).toMatchObject({
      checkpoint_id: "checkpoint_1",
      plan_id: "plan.1",
    });

    for (const invalid of ["", "../checkpoint", "foo/bar", "foo\\bar", "a".repeat(129), "bad\0id"]) {
      expect(() => TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ project: "game", command: "compile.status", checkpoint_id: invalid })).toThrow();
      expect(() => TOOL_ARG_SCHEMAS.sgg_unity_batch.parse({ items_json: "[]", plan_id: invalid })).toThrow();
    }
  });

  it("accepts bounded wrapper waits long enough for replacement plus command execution", () => {
    const base = { project: "game", command: "game.view.screenshot" };
    expect(TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ ...base, wait_for_replacement_ms: 45_000 })).toMatchObject({ wait_for_replacement_ms: 45_000 });
    expect(TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ ...base, wait_for_replacement_ms: 3_600_000 })).toMatchObject({ wait_for_replacement_ms: 3_600_000 });
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ ...base, wait_for_replacement_ms: 3_600_001 })).toThrow();
  });

  it("reserves delivery time inside the 300 second caller boundary for terminal test waits", () => {
    const base = { project: "game", command: "test.run" };
    expect(TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ ...base, wait_for_test_run_terminal_ms: 294_999 })).toMatchObject({ wait_for_test_run_terminal_ms: 294_999 });
    expect(TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ ...base, wait_for_test_run_terminal_ms: 295_000 })).toMatchObject({ wait_for_test_run_terminal_ms: 295_000 });
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ ...base, wait_for_test_run_terminal_ms: 295_001 })).toThrow();
    expect(() => TOOL_ARG_SCHEMAS.sgg_unity_call.parse({ ...base, wait_for_test_run_terminal_ms: 300_000 })).toThrow();
  });

  it("validates args_json as a JSON object", () => {
    expect(parseArgsJson("{\"scene_path\":\"Assets/Scenes/Main.unity\"}")).toEqual({
      scene_path: "Assets/Scenes/Main.unity",
    });

    expect(() => parseArgsJson("[]")).toThrow("args_json_must_be_object");
    expect(() => parseArgsJson("not json")).toThrow("args_json_invalid_json");
  });

  it("validates items_json as an array of explicit project commands", () => {
    expect(
      parseItemsJson(
        JSON.stringify([
          {
            project: "game",
            command: "compile.status",
            args: {},
          },
        ]),
      ),
    ).toEqual([{ project: "game", command: "compile.status", args: {}, mode: "wait" }]);

    expect(
      parseItemsJson(
        JSON.stringify([
          {
            project: "game",
            command: "compile.status",
            args_json: "{}",
            mode: "job",
            request_id: "req-1",
          },
        ]),
      ),
    ).toEqual([{ project: "game", command: "compile.status", args_json: "{}", mode: "job", request_id: "req-1" }]);

    expect(() => parseItemsJson("{}")).toThrow("items_json_must_be_array");
    expect(() => parseItemsJson(JSON.stringify([{ command: "compile.status" }]))).toThrow();
    expect(() => parseItemsJson(JSON.stringify([{ project: "game", command: "compile.status", args_json: "[]" }]))).toThrow("args_json_must_be_object");
    expect(() => parseItemsJson(JSON.stringify([{ project: "game", command: "compile.status", args: {}, args_json: "{}" }]))).toThrow();
    expect(() => parseItemsJson(JSON.stringify([{ project: "game", command: "compile.status", unexpected: true }]))).toThrow();
  });
});
