import { describe, expect, it } from "vitest";

import { createUnityProjectOpenRuntime } from "../src/diagnostics/unity-project-open.js";
import type { UnityProcessListResult, UnityProcessSnapshot } from "../src/diagnostics/unity-processes.js";
import type { PublicProjectRecord } from "../src/registry/project-registry.js";

const now = new Date("2026-05-12T10:00:00.000Z");

describe("Unity project open diagnostics", () => {
  it("reports an already open project with live MCP heartbeat", () => {
    const runtime = createUnityProjectOpenRuntime();

    const result = runtime.status({}, {
      projectSelector: "game",
      projects: [projectRecord()],
      processStatus: processStatus([unityProcess({ heartbeat_match_count: 1, heartbeat_matches: [{ project: "game", instance_id: "game-a", status: "ready", stale: false, unity_pid: 12345, canonical_project_root: "c:/u/game", match_reason: "pid" }] })]),
      now: () => now,
    });

    expect(result).toMatchObject({
      gateway_local: true,
      read_only: true,
      launch_unity_editor_attempted: false,
      requested_project_selector: "game",
      requested_canonical_project_root: null,
      status: "open_with_mcp_heartbeat",
      mcp_heartbeat_match_count: 1,
      mcp_heartbeat_matches: [{ instance_id: "game-a", stale: false, last_heartbeat_age_ms: 30000 }],
      unity_editor_process_match_count: 1,
    });
    expect(result.next_valid_actions).toContain("use_existing_mcp_project");
  });

  it("reports an open editor without an MCP heartbeat from process evidence", () => {
    const runtime = createUnityProjectOpenRuntime();

    const result = runtime.status({ canonical_project_root: "C:/u/game" }, {
      projectSelector: "game",
      projects: [],
      processStatus: processStatus([unityProcess()]),
      now: () => now,
    });

    expect(result).toMatchObject({
      requested_canonical_project_root: "c:/u/game",
      status: "open_without_mcp_heartbeat",
      mcp_heartbeat_match_count: 0,
      unity_editor_process_match_count: 1,
      unity_editor_process_matches: [{ pid: 12345, canonical_project_root: "c:/u/game" }],
    });
    expect(result.next_valid_actions).toContain("wait_for_heartbeat");
  });

  it("does not treat an AssetImportWorker as an open Unity Editor", () => {
    const runtime = createUnityProjectOpenRuntime();

    const result = runtime.status({ canonical_project_root: "C:/u/game" }, {
      projectSelector: "game",
      projects: [],
      processStatus: processStatus([unityProcess({ role: "unity_asset_import_worker" })]),
      now: () => now,
    });

    expect(result).toMatchObject({
      status: "not_detected",
      mcp_heartbeat_match_count: 0,
      unity_editor_process_match_count: 0,
      unity_editor_process_matches: [],
    });
  });

  it("collapses historical heartbeat generations of one exact canonical root", () => {
    const runtime = createUnityProjectOpenRuntime();
    const projects = Array.from({ length: 247 }, (_, index) => projectRecord({
      instance_id: `game-stale-${index}`,
      unity_pid: 20_000 + index,
      stale: true,
      last_heartbeat: `2026-05-11T${String(index % 24).padStart(2, "0")}:00:00.000Z`,
    }));

    const result = runtime.status({ canonical_project_root: "C:/u/game" }, {
      projectSelector: "game",
      projects,
      processStatus: processStatus([]),
      now: () => now,
    });

    expect(result).toMatchObject({
      status: "stale_or_offline",
      requested_canonical_project_root: "c:/u/game",
      mcp_heartbeat_match_count: 247,
      unity_editor_process_match_count: 0,
    });
    expect(result.mcp_heartbeat_matches).toHaveLength(10);
    expect(result.next_valid_actions).toContain("dry_run_unity.project.open");
  });

  it("collapses historical generations selected by stable project display name", () => {
    const runtime = createUnityProjectOpenRuntime();
    const projects = Array.from({ length: 12 }, (_, index) => projectRecord({
      instance_id: `game-stale-${index}`,
      unity_pid: 20_000 + index,
      stale: true,
    }));

    const result = runtime.status({}, {
      projectSelector: "Game",
      projects,
      processStatus: processStatus([]),
      now: () => now,
    });

    expect(result).toMatchObject({
      status: "stale_or_offline",
      requested_project_selector: "Game",
      requested_canonical_project_root: null,
      mcp_heartbeat_match_count: 12,
      unity_editor_process_match_count: 0,
    });
  });

  it("makes an explicit canonical root authoritative over a conflicting selector", () => {
    const runtime = createUnityProjectOpenRuntime();

    const result = runtime.status({ project_selector: "game", canonical_project_root: "C:/u/target" }, {
      projects: [projectRecord({ canonical_project_root: "c:/u/other", project_root: "C:/u/other", aliases: [] })],
      processStatus: processStatus([]),
      now: () => now,
    });

    expect(result).toMatchObject({ status: "not_detected", mcp_heartbeat_match_count: 0, unity_editor_process_match_count: 0 });
  });

  it("does not let PID heartbeat correlation override a known conflicting process root", () => {
    const runtime = createUnityProjectOpenRuntime();
    const stale = projectRecord({ stale: true });
    const process = unityProcess({
      canonical_project_root: "c:/u/other",
      parsed_project_path: "C:/u/other",
      heartbeat_match_count: 1,
      heartbeat_matches: [{ project: "game", instance_id: stale.instance_id, status: "ready", stale: true, unity_pid: stale.unity_pid, canonical_project_root: "c:/u/game", match_reason: "pid" }],
    });

    const result = runtime.status({ project_selector: "game" }, {
      projects: [stale],
      processStatus: processStatus([process]),
      now: () => now,
    });

    expect(result).toMatchObject({ status: "stale_or_offline", mcp_heartbeat_match_count: 1, unity_editor_process_match_count: 0 });
  });

  it("previews opening a detected Unity project without launching", () => {
    const spawnCalls: Array<{ file: string; args: string[] }> = [];
    const runtime = createUnityProjectOpenRuntime({
      platform: "linux",
      fs: fakeProjectFs(new Set(["/mnt/c/u/game", "/mnt/c/u/game/ProjectSettings"])),
      spawn: (file, args) => {
        spawnCalls.push({ file, args });
        return { pid: 9001, unref: () => undefined };
      },
    });

    const result = runtime.open({ canonical_project_root: "C:/u/game" }, {
      projectSelector: "game",
      projects: [],
      processStatus: processStatus([]),
      editorDiscovery: editorDiscovery("6000.4.10f1", "C:/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe"),
      now: () => now,
    });

    expect(result).toMatchObject({
      status: "would_launch",
      dry_run: true,
      confirm_open: false,
      launch_unity_editor_attempted: false,
      requested_canonical_project_root: "c:/u/game",
      host_project_path_checked: "/mnt/c/u/game",
      project_path_for_unity: "C:\\u\\game",
      selected_editor: { version: "6000.4.10f1", executable_path: "C:/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe" },
      arbitrary_editor_executable_accepted: false,
      closes_existing_editors: false,
      kills_unity_processes: false,
      deletes_library_or_cache: false,
    });
    expect(spawnCalls).toEqual([]);
  });

  it("launches the discovered editor only after explicit real-open confirmation", () => {
    const spawnCalls: Array<{ file: string; args: string[]; cwd?: string }> = [];
    const runtime = createUnityProjectOpenRuntime({
      platform: "linux",
      fs: fakeProjectFs(new Set(["/mnt/c/u/game", "/mnt/c/u/game/ProjectSettings"])),
      spawn: (file, args, options) => {
        spawnCalls.push({ file, args, ...(options.cwd ? { cwd: options.cwd } : {}) });
        return { pid: 9001, unref: () => undefined };
      },
    });

    const result = runtime.open({ canonical_project_root: "C:/u/game", dry_run: false, confirm_open: true }, {
      projectSelector: "game",
      projects: [],
      processStatus: processStatus([]),
      editorDiscovery: editorDiscovery("6000.4.10f1", "C:/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe"),
      now: () => now,
    });

    expect(result).toMatchObject({
      status: "launched",
      dry_run: false,
      confirm_open: true,
      launch_unity_editor_attempted: true,
      launched_pid: 9001,
    });
    expect(spawnCalls).toEqual([{ file: "/mnt/c/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe", args: ["-projectPath", "C:\\u\\game"], cwd: "/mnt/c/u/game" }]);
  });

  it("refuses to open a second editor when process evidence shows the project is already open", () => {
    const runtime = createUnityProjectOpenRuntime({ platform: "linux", fs: fakeProjectFs(new Set(["/mnt/c/u/game", "/mnt/c/u/game/ProjectSettings"])) });

    const result = runtime.open({ canonical_project_root: "C:/u/game", dry_run: false, confirm_open: true }, {
      projectSelector: "game",
      projects: [],
      processStatus: processStatus([unityProcess()]),
      editorDiscovery: editorDiscovery("6000.4.10f1", "C:/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe"),
      now: () => now,
    });

    expect(result).toMatchObject({
      status: "refused",
      refusal_reason: "already_open_without_mcp_heartbeat",
      launch_unity_editor_attempted: false,
    });
    expect(result.next_valid_actions).toContain("wait_for_heartbeat");
  });
});

function projectRecord(overrides: Partial<PublicProjectRecord> = {}): PublicProjectRecord {
  return {
    schema_version: 1,
    instance_id: "game-a",
    project: "game",
    display_name: "Game",
    project_root: "C:/u/game",
    canonical_project_root: "c:/u/game",
    aliases: [
      { environment: "unity", host_path: "C:/u/game", canonical_path: "c:/u/game" },
      { environment: "windows", host_path: "C:\\u\\game", canonical_path: "c:/u/game" },
      { environment: "wsl", host_path: "/mnt/c/u/game", canonical_path: "c:/u/game" },
    ],
    unity_version: "6000.4.10f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    status: "ready",
    commands_hash: "hash",
    last_heartbeat: "2026-05-12T09:59:30.000Z",
    stale: false,
    ...overrides,
  };
}

function unityProcess(overrides: Partial<UnityProcessSnapshot> = {}): UnityProcessSnapshot {
  return {
    pid: 12345,
    parent_pid: null,
    process_name: "Unity.exe",
    role: "unity_editor",
    process_start_utc: "2026-05-12T08:00:00.000Z",
    executable_path: "C:/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe",
    command_line_redacted: 'Unity.exe -projectPath "C:/u/game"',
    parsed_project_path: "C:/u/game",
    canonical_project_root: "c:/u/game",
    heartbeat_match_count: 0,
    heartbeat_matches: [],
    current_project_match: false,
    eligible_for_terminate: false,
    terminate_refusal_reason: "current_project_mismatch",
    ...overrides,
  };
}

function processStatus(processes: UnityProcessSnapshot[]): UnityProcessListResult {
  return {
    schema_version: 1,
    supported: true,
    process_count: processes.length,
    scan_truncated: false,
    processes,
    errors: [],
    next_valid_actions: [],
  };
}

function editorDiscovery(version: string, executablePath: string) {
  return {
    status: () => ({
      status: "ok",
      editors: [{ version, executable_path: executablePath, source: "test", exists: true }],
      probes: [],
    }),
  };
}

function fakeProjectFs(existingPaths: Set<string>) {
  return {
    existsSync: (value: unknown) => existingPaths.has(String(value).replaceAll("\\", "/")),
    statSync: (value: unknown) => {
      const normalized = String(value).replaceAll("\\", "/");
      if (!existingPaths.has(normalized)) {
        throw new Error("ENOENT");
      }
      return { isDirectory: () => true };
    },
  } as unknown as Pick<typeof import("node:fs"), "existsSync" | "statSync">;
}
