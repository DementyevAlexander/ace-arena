import { describe, expect, it } from "vitest";

import { createUnityProcessGenerationInspector, inspectUnityProcesses, sameUnityProcessStart, terminateUnityProcess, type ExecFileSyncLike } from "../src/diagnostics/unity-processes.js";
import type { PublicProjectRecord, RegistryProcessGenerationIdentity } from "../src/registry/project-registry.js";

describe("Unity process diagnostics", () => {
  it("parses Unity process ownership from PowerShell process JSON", () => {
    const execFileSync: ExecFileSyncLike = (file, args) => {
      expect(file).toBe("powershell.exe");
      expect(args.join(" ")).toContain("Win32_Process");
      expect(args.join(" ")).toContain("(Get-Process -Id $_.ProcessId -ErrorAction Stop).StartTime");
      expect(args.join(" ")).not.toContain("$_.CreationDate.ToUniversalTime()");
      return processEnvelope([
        {
          ProcessId: 12345,
          ParentProcessId: 100,
          Name: "Unity.exe",
          ProcessStartUtc: "2026-05-12T08:00:00.000Z",
          ExecutablePath: "C:\\Program Files\\Unity\\Editor\\Unity.exe",
          CommandLine: '"Unity.exe" -batchmode -projectPath "C:\\u\\game" -token super-secret',
        },
        {
          ProcessId: 999,
          ParentProcessId: 1,
          Name: "Unity.Licensing.Client.exe",
          ExecutablePath: "C:\\Program Files\\Unity Hub\\UnityLicensingClient_V1\\Unity.Licensing.Client.exe",
          CommandLine: "Unity.Licensing.Client.exe",
        },
      ]);
    };

    const result = inspectUnityProcesses([project()], project(), { execFileSync, platform: "win32" });

    expect(result.supported).toBe(true);
    expect(result.process_count).toBe(2);
    expect(result.processes[0]).toMatchObject({
      pid: 12345,
      role: "unity_editor",
      process_start_utc: "2026-05-12T08:00:00.000Z",
      parsed_project_path: "C:\\u\\game",
      canonical_project_root: "c:/u/game",
      current_project_match: true,
      eligible_for_terminate: true,
      heartbeat_match_count: 1,
    });
    expect(result.processes[0]?.command_line_redacted).toContain("-token <redacted>");
    expect(result.processes[1]).toMatchObject({
      pid: 999,
      role: "unity_licensing_client",
      eligible_for_terminate: false,
      terminate_refusal_reason: "unsupported_process_role",
    });
  });

  it("classifies exact AssetImportWorker role markers and refuses project-owned termination", () => {
    let taskkillAttempted = false;
    const execFileSync: ExecFileSyncLike = (file) => {
      if (file === "taskkill.exe") {
        taskkillAttempted = true;
        throw new Error("taskkill should not run");
      }
      return processEnvelope([
        {
          ProcessId: 12345,
          ParentProcessId: 100,
          Name: "Unity.exe",
          ProcessStartUtc: "2026-05-12T08:00:00.000Z",
          ExecutablePath: "C:\\Program Files\\Unity\\Editor\\Unity.exe",
          CommandLine: '"Unity.exe" "-adb2" "-batchMode" "-name" "AssetImportWorkerHW12" "-projectPath" "C:\\u\\game" "-logFile" "Logs/AssetImportWorkerHW12.log" "-name" "AssetImport"',
        },
      ]);
    };

    const listed = inspectUnityProcesses([project()], project(), { execFileSync, platform: "win32" });
    const terminated = terminateUnityProcess({ pid: 12345, canonical_project_root: "c:/u/game", confirm_terminate: true, dry_run: false }, [project()], project(), { execFileSync, platform: "win32" });

    expect(listed.processes[0]).toMatchObject({
      role: "unity_asset_import_worker",
      canonical_project_root: "c:/u/game",
      current_project_match: true,
      eligible_for_terminate: false,
      terminate_refusal_reason: "unsupported_process_role",
    });
    expect(terminated).toMatchObject({ status: "refused", refusal_reason: "unsupported_process_role", terminated_count: 0 });
    expect(taskkillAttempted).toBe(false);
  });

  it("does not infer AssetImportWorker role from similar names or log paths", () => {
    const result = inspectUnityProcesses([], undefined, {
      execFileSync: () => processEnvelope([
        { ProcessId: 100, Name: "Unity.exe", CommandLine: '"Unity.exe" -name AssetImportWorkerHW12-copy -projectPath "C:\\u\\one"' },
        { ProcessId: 200, Name: "Unity.exe", CommandLine: '"Unity.exe" -name MainEditor -logFile "Logs/AssetImportWorkerHW12.log" -projectPath "C:\\u\\two"' },
        { ProcessId: 300, Name: "Unity.exe", CommandLine: '"Unity.exe" -NAME assetimportworker7 -projectPath "C:\\u\\three"' },
      ]),
      platform: "win32",
    });

    expect(result.processes.map(({ pid, role }) => ({ pid, role }))).toEqual([
      { pid: 100, role: "unity_editor" },
      { pid: 200, role: "unity_editor" },
      { pid: 300, role: "unity_asset_import_worker" },
    ]);
  });

  it("refuses terminate unless PID and canonical project ownership match", () => {
    const execFileSync: ExecFileSyncLike = (file, args) => {
      if (file === "taskkill.exe") {
        throw new Error("taskkill should not run");
      }
      expect(args.join(" ")).toContain("Win32_Process");
      return processEnvelope([{ ProcessId: 22222, ParentProcessId: 1, Name: "Unity.exe", ExecutablePath: "Unity.exe", CommandLine: '"Unity.exe" -projectPath "C:\\u\\other"' }]);
    };

    const result = terminateUnityProcess({ pid: 22222, canonical_project_root: "c:/u/game", confirm_terminate: true, dry_run: false }, [project()], project(), { execFileSync, platform: "win32" });

    expect(result).toMatchObject({
      status: "refused",
      refusal_reason: "project_mismatch",
      terminated_count: 0,
    });
  });

  it("runs taskkill only for approved exact project-owned Unity Editor PID", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync: ExecFileSyncLike = (file, args) => {
      calls.push({ file, args });
      if (file === "taskkill.exe") {
        expect(args).toEqual(["/PID", "12345"]);
        return "SUCCESS";
      }
      return processEnvelope([{ ProcessId: 12345, ParentProcessId: 1, Name: "Unity.exe", ExecutablePath: "Unity.exe", CommandLine: '"Unity.exe" -projectPath "C:\\u\\game"' }]);
    };

    const result = terminateUnityProcess({ pid: 12345, canonical_project_root: "c:/u/game", confirm_terminate: true, dry_run: false }, [project()], project(), { execFileSync, platform: "win32" });

    expect(result).toMatchObject({
      status: "terminated",
      terminated_count: 1,
      target_pid: 12345,
    });
    expect(calls.some((call) => call.file === "taskkill.exe")).toBe(true);
  });

  it("reports truncation instead of treating a capped process list as complete", () => {
    const rows = Array.from({ length: 51 }, (_, index) => ({
      ProcessId: 20_000 + index,
      ParentProcessId: 1,
      Name: "Unity.exe",
      ProcessStartUtc: `2026-05-12T08:00:${String(index).padStart(2, "0")}.000Z`,
      ExecutablePath: "Unity.exe",
      CommandLine: `"Unity.exe" -projectPath "C:\\u\\game-${index}"`,
    }));
    const result = inspectUnityProcesses([], undefined, { execFileSync: () => processEnvelope(rows), platform: "win32" });

    expect(result.process_count).toBe(50);
    expect(result.scan_truncated).toBe(true);
  });

  it.each(["", "not-json", JSON.stringify([]), processEnvelope([{ ProcessId: 12345, Name: "Unity.exe" }, { ProcessId: "bad", Name: "Unity.exe" }])])("fails closed for missing, malformed or partial process output", (output) => {
    const result = inspectUnityProcesses([], undefined, { execFileSync: () => output, platform: "win32" });

    expect(result).toMatchObject({ supported: false, process_count: 0, errors: ["powershell_invalid_json"] });
  });

  it("accepts an explicit successful empty process query", () => {
    const result = inspectUnityProcesses([], undefined, { execFileSync: () => processEnvelope([]), platform: "win32" });

    expect(result).toMatchObject({ supported: true, process_count: 0, errors: [] });
  });

  it("bounds process helper timeout and does not expose its PowerShell source", () => {
    let timeout = 0;
    const result = inspectUnityProcesses([], undefined, {
      platform: "win32",
      toolTimeoutMs: 375,
      execFileSync: (_file, _args, options) => {
        timeout = options.timeout;
        const error = new Error(`spawnSync powershell.exe ETIMEDOUT ${"secret-source".repeat(100)}`) as Error & { code: string };
        error.code = "ETIMEDOUT";
        throw error;
      },
    });

    expect(timeout).toBe(375);
    expect(result).toMatchObject({ supported: false, errors: ["powershell_failed: windows_tool_timeout"] });
    expect(JSON.stringify(result)).not.toContain("secret-source");
  });

  it("classifies exact live, absent, PID-reused, and unknown process generations from one complete scan", () => {
    let command = "";
    const inspector = createUnityProcessGenerationInspector({
      platform: "win32",
      now: () => new Date("2026-08-26T14:00:00.000Z"),
      pidState: () => "live",
      execFileSync: (_file, args) => {
        command = args.join(" ");
        return processEnvelope([
          { ProcessId: 100, ParentProcessId: 1, Name: "Unity.exe", ProcessStartUtc: "2026-08-26T13:00:00.1234000Z" },
          { ProcessId: 200, ParentProcessId: 1, Name: "Unity.exe", ProcessStartUtc: "2026-08-26T13:30:00.1235000Z" },
          { ProcessId: 300, ParentProcessId: 1, Name: "Unity.exe", ProcessStartUtc: "" },
        ]);
      },
    });

    const observations = inspector([
      processIdentity({ instance_id: "exact", unity_pid: 100 }),
      processIdentity({ instance_id: "absent", unity_pid: 999 }),
      processIdentity({ instance_id: "reused", unity_pid: 200 }),
      processIdentity({ instance_id: "unknown", unity_pid: 300 }),
    ]);

    expect(observations.map(({ instance_id, state }) => ({ instance_id, state }))).toEqual([
      { instance_id: "exact", state: "live" },
      { instance_id: "absent", state: "dead" },
      { instance_id: "reused", state: "dead" },
      { instance_id: "unknown", state: "uncertain" },
    ]);
    expect(command).toContain("$pids=@(100,200,300,999)");
    expect(command).toContain("[System.Diagnostics.Process]::GetProcessById($processId)");
    expect(command).not.toContain("Get-CimInstance");
    expect(command).not.toContain("Win32_Process");
    expect(command).not.toContain("CommandLine");
    expect(command).not.toContain("ExecutablePath");
  });

  it("preserves fractional process-start identity and caches by the exact OS process generation", () => {
    let current = new Date("2026-08-26T14:00:00.000Z");
    let calls = 0;
    const inspector = createUnityProcessGenerationInspector({
      platform: "win32",
      now: () => current,
      pidState: () => "live",
      execFileSync: () => {
        calls += 1;
        return processEnvelope([{ ProcessId: 100, ParentProcessId: 1, Name: "Unity.exe", ProcessStartUtc: "2026-08-26T13:00:00.1234000Z" }]);
      },
    });
    const identity = processIdentity({ instance_id: "exact", unity_pid: 100 });

    expect(inspector([identity])).toMatchObject([{ state: "live" }]);
    expect(inspector([identity])).toMatchObject([{ state: "live" }]);
    expect(calls).toBe(1);

    current = new Date("2026-08-26T14:00:00.500Z");
    expect(inspector([{ ...identity, heartbeat_generation: 8, last_heartbeat: current.toISOString() }])).toMatchObject([{ state: "live" }]);
    expect(calls).toBe(1);
    expect(inspector([{ ...identity, unity_process_start_utc: "2026-08-26T13:00:00.2234Z", heartbeat_generation: 9 }])).toMatchObject([{ state: "dead" }]);
    expect(calls).toBe(2);
    expect(sameUnityProcessStart("2026-08-26T13:00:00.1234Z", "2026-08-26T13:00:00.1234000Z")).toBe(true);
    expect(sameUnityProcessStart("2026-08-26T13:00:00.1234Z", "2026-08-26T13:00:00.1235Z")).toBe(false);
    expect(sameUnityProcessStart("2026-08-26T17:51:15.9803002Z", "2026-08-26T17:51:15.9803000Z")).toBe(false);
  });

  it("keeps unsupported and malformed process scans uncertain", () => {
    const identity = processIdentity();
    expect(createUnityProcessGenerationInspector({ platform: "linux" })([identity])).toMatchObject([{ state: "uncertain" }]);
    expect(createUnityProcessGenerationInspector({ platform: "win32", pidState: () => "live", execFileSync: () => "not-json" })([identity])).toMatchObject([{ state: "uncertain" }]);
  });

  it("proves PID absence immediately without launching the slower process scanner", () => {
    let scanCalls = 0;
    const inspector = createUnityProcessGenerationInspector({
      platform: "win32",
      pidState: () => "dead",
      execFileSync: () => { scanCalls += 1; return processEnvelope([]); },
    });

    expect(inspector([processIdentity()])).toMatchObject([{ state: "dead" }]);
    expect(scanCalls).toBe(0);
  });

  it("fails closed without launching PowerShell when exact generation PID capacity is exceeded", () => {
    let scanCalls = 0;
    const inspector = createUnityProcessGenerationInspector({
      platform: "win32",
      pidState: () => "live",
      execFileSync: () => { scanCalls += 1; return processEnvelope([]); },
    });
    const identities = Array.from({ length: 1025 }, (_, index) => processIdentity({ instance_id: `game-${index}`, unity_pid: index + 1 }));

    const observations = inspector(identities);

    expect(observations).toHaveLength(1025);
    expect(observations.every((observation) => observation.state === "uncertain")).toBe(true);
    expect(scanCalls).toBe(0);
  });
});

function processEnvelope(processes: unknown[]): string {
  return JSON.stringify({ schema_version: 1, query_succeeded: true, processes });
}

function project(): PublicProjectRecord {
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
    ],
    unity_version: "6000.0.0f1",
    unity_pid: 12345,
    bridge_host: "127.0.0.1",
    bridge_port: 18142,
    status: "ready",
    commands_hash: "hash",
    last_heartbeat: "2026-06-15T10:00:00.000Z",
    stale: false,
  };
}

function processIdentity(overrides: Partial<RegistryProcessGenerationIdentity> = {}): RegistryProcessGenerationIdentity {
  return {
    instance_id: "game-a",
    unity_pid: 100,
    unity_process_start_utc: "2026-08-26T13:00:00.1234Z",
    heartbeat_generation: 7,
    last_heartbeat: "2026-08-26T13:59:50.000Z",
    ...overrides,
  };
}
