import { describe, expect, test } from "vitest";

import { createPresentationSnapshot, presentationToolAllowlist, SGG_UNITY_PRESENTATION_TOOL_IDS } from "../src/format.js";

const EXPECTED_LABELS = new Map([
  ["sgg-unity-mcp_sgg_unity_projects", "Finding live Unity projects"],
  ["sgg-unity-mcp_sgg_unity_describe", "Reading Unity command metadata"],
  ["sgg-unity-mcp_sgg_unity_call", "Running a Unity command"],
  ["sgg-unity-mcp_sgg_unity_batch", "Running a Unity command batch"],
  ["sgg-unity-mcp_sgg_unity_job", "Checking a Unity job"],
  ["sgg-unity-mcp_sgg_unity_logs", "Reading Unity logs"],
  ["sgg-unity-mcp_sgg_unity_stale_projects", "Inspecting stale Unity project records"],
  ["sgg-unity-mcp_sgg_unity_stale_registry_cleanup", "Previewing or cleaning stale Unity records"],
]);

describe("createPresentationSnapshot", () => {
  test("matches only the exact eight SGG Unity tool IDs", () => {
    expect(SGG_UNITY_PRESENTATION_TOOL_IDS).toHaveLength(8);
    expect(new Set(SGG_UNITY_PRESENTATION_TOOL_IDS)).toEqual(new Set(EXPECTED_LABELS.keys()));
    for (const tool of SGG_UNITY_PRESENTATION_TOOL_IDS) {
      expect(createPresentationSnapshot(tool, {})?.label).toBe(EXPECTED_LABELS.get(tool));
      expect(createPresentationSnapshot(`${tool}-near-miss`, {})).toBeUndefined();
    }
    expect(createPresentationSnapshot("SGG-UNITY-MCP_sgg_unity_projects", {})).toBeUndefined();
    expect(createPresentationSnapshot(undefined, {})).toBeUndefined();
  });

  test("uses only exact allowlisted Unity command labels", () => {
    const tool = "sgg-unity-mcp_sgg_unity_call";

    expect(createPresentationSnapshot(tool, { command: "compile.refresh_and_wait" })?.label)
      .toBe("Refreshing and compiling Unity");
    expect(createPresentationSnapshot(tool, { command: "test.run.status" })?.label)
      .toBe("Checking Unity test status");
    expect(createPresentationSnapshot(tool, { command: "__proto__" })?.label)
      .toBe("Running a Unity command");
    expect(createPresentationSnapshot(tool, { command: "compile.refresh_and_wait\nsecret" })?.label)
      .toBe("Running a Unity command");
  });

  test("reads batch length without reading any item", () => {
    const first = Object.defineProperty({}, "project", {
      get() {
        throw new Error("batch item was read");
      },
    });
    const items = [first, {}, {}];

    expect(createPresentationSnapshot("sgg-unity-mcp_sgg_unity_batch", { items })?.label)
      .toBe("Running a Unity command batch (3 commands)");
    expect(createPresentationSnapshot("sgg-unity-mcp_sgg_unity_batch", { items: new Array(1_000) })?.label)
      .toBe("Running a Unity command batch (999+ commands)");
  });

  test("does not access forbidden argument properties", () => {
    const args: Record<string, unknown> = { command: "compile.status" };
    for (const key of ["args_json", "project", "request_id", "checkpoint_id", "plan_id", "job_id", "output", "error"]) {
      Object.defineProperty(args, key, {
        get() {
          throw new Error(`forbidden getter read: ${key}`);
        },
      });
    }

    expect(createPresentationSnapshot("sgg-unity-mcp_sgg_unity_call", args)?.label)
      .toBe("Checking Unity compile status");
  });

  test("uses only exact job action", () => {
    const tool = "sgg-unity-mcp_sgg_unity_job";
    expect(createPresentationSnapshot(tool, { action: "cancel" })?.label)
      .toBe("Requesting Unity job cancellation");
    expect(createPresentationSnapshot(tool, { action: "cancel-now" })?.label)
      .toBe("Checking a Unity job");
  });

  test("builds a constant exact allowlist from validated aliases without suffix matching", () => {
    const allowlist = presentationToolAllowlist(["unity-dev", "sgg-unity-mcp"]);
    expect(allowlist.size).toBe(16);
    expect(createPresentationSnapshot("unity-dev_sgg_unity_call", { command: "compile.status" }, allowlist)?.label).toBe("Checking Unity compile status");
    expect(createPresentationSnapshot("another_unity-dev_sgg_unity_call", {}, allowlist)).toBeUndefined();
    expect(createPresentationSnapshot("unconfigured_sgg_unity_call", {}, allowlist)).toBeUndefined();
  });
});
