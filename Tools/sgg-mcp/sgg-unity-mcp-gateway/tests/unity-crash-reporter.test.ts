import fs from "node:fs";
import os from "node:os";
import path from "node:path";

import { afterEach, describe, expect, it } from "vitest";

import { closeUnityCrashReporters, inspectUnityCrashReporter, type ExecFileSyncLike } from "../src/diagnostics/unity-crash-reporter.js";

const tempDirs: string[] = [];
const now = new Date("2026-05-27T01:00:00.000Z");

afterEach(() => {
  for (const dir of tempDirs.splice(0)) {
    fs.rmSync(dir, { recursive: true, force: true });
  }
});

describe("Unity crash reporter diagnostics", () => {
  it("detects active Bug Reporter windows and recent crash artifacts", () => {
    const tempRoot = makeTempRoot();
    writeCrashArtifacts(tempRoot);
    const execFileSync = fakeExecFileSync([
      '"UnityBugReporter.exe","29136","Console","1","200 K","Running","user","0:01:00","Unity Bug Reporter"',
      '"UnityBugReporter.exe","50320","Console","1","200 K","Running","user","0:01:00","N/A"',
    ].join("\n"));

    const status = inspectUnityCrashReporter({ execFileSync, tempRoots: [tempRoot], env: {}, tmpdir: () => "/tmp/ignored", now: () => now });

    expect(status).toMatchObject({
      supported: true,
      unity_crashed: true,
      blocking_agent_work: true,
      active_reporters_count: 1,
      active_reporters: [{ pid: 29136, image_name: "UnityBugReporter.exe", window_title: "Unity Bug Reporter" }],
    });
    expect(status.recent_crash_artifacts.crash_directories[0]).toMatchObject({ name: "Crash_2026-05-27_003619388", files: expect.arrayContaining([expect.objectContaining({ name: "crash.dmp", role: "crash_dump" })]) });
    expect(status.recent_crash_artifacts.bug_report_json[0]).toMatchObject({ name: "CrashReport_32af8ca7adf3ac646bc287610a2f6be0.json" });
    expect(status.recent_crash_artifacts.bug_report_temp_directories[0]).toMatchObject({ name: "UnityBugReporter_1779842204272", files: [expect.objectContaining({ name: "system_info.txt", role: "system_info" })] });
  });

  it("closes only verified active Bug Reporter PIDs without force by default", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync = fakeExecFileSync('"UnityBugReporter.exe","29136","Console","1","200 K","Running","user","0:01:00","Unity Bug Reporter"', calls);

    const result = closeUnityCrashReporters({ pids: [29136, 12345] }, { execFileSync, tempRoots: [], env: {}, tmpdir: () => "/tmp/ignored", now: () => now });

    expect(result).toMatchObject({
      dry_run: false,
      force: false,
      submit_attempted: false,
      close_method: "taskkill_window_close",
      target_pids: [29136],
      closed_count: 1,
      skipped: [{ pid: 12345, reason: "not_active_unity_bug_reporter" }],
    });
    expect(calls.some((call) => call.file.endsWith("taskkill.exe") && call.args.join(" ") === "/PID 29136")).toBe(true);
    expect(calls.some((call) => call.args.includes("/F"))).toBe(false);
  });

  it("supports dry-run close previews without invoking taskkill", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync = fakeExecFileSync('"UnityBugReporter.exe","29136","Console","1","200 K","Running","user","0:01:00","Unity Bug Reporter"', calls);

    const result = closeUnityCrashReporters({ dry_run: true }, { execFileSync, tempRoots: [], env: {}, tmpdir: () => "/tmp/ignored", now: () => now });

    expect(result.results).toEqual([{ pid: 29136, status: "would_close" }]);
    expect(calls.some((call) => call.file.endsWith("taskkill.exe"))).toBe(false);
  });
});

function makeTempRoot(): string {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-crash-reporter-"));
  tempDirs.push(dir);
  return dir;
}

function writeCrashArtifacts(tempRoot: string): void {
  const crashDir = path.join(tempRoot, "Unity", "Editor", "Crashes", "Crash_2026-05-27_003619388");
  fs.mkdirSync(crashDir, { recursive: true });
  const dumpPath = path.join(crashDir, "crash.dmp");
  const editorLogPath = path.join(crashDir, "Editor.log");
  fs.writeFileSync(dumpPath, "dump", "utf8");
  fs.writeFileSync(editorLogPath, "editor log", "utf8");
  touch(dumpPath);
  touch(editorLogPath);
  touch(crashDir);

  const reportJsonDir = path.join(tempRoot, "BugReporterCrashReportJson");
  fs.mkdirSync(reportJsonDir, { recursive: true });
  const reportJsonPath = path.join(reportJsonDir, "CrashReport_32af8ca7adf3ac646bc287610a2f6be0.json");
  fs.writeFileSync(reportJsonPath, "{}", "utf8");
  touch(reportJsonPath);

  const bugReporterDir = path.join(tempRoot, "UnityBugReporter_1779842204272");
  fs.mkdirSync(bugReporterDir, { recursive: true });
  const systemInfoPath = path.join(bugReporterDir, "system_info.txt");
  fs.writeFileSync(systemInfoPath, "system", "utf8");
  touch(systemInfoPath);
  touch(bugReporterDir);
}

function touch(filePath: string): void {
  fs.utimesSync(filePath, now, now);
}

function fakeExecFileSync(tasklistOutput: string, calls: Array<{ file: string; args: string[] }> = []): ExecFileSyncLike {
  return (file, args) => {
    calls.push({ file, args });
    if (file.endsWith("tasklist.exe")) {
      return tasklistOutput;
    }
    if (file.endsWith("taskkill.exe")) {
      return "SUCCESS";
    }
    const error = new Error("ENOENT");
    (error as { code?: string }).code = "ENOENT";
    throw error;
  };
}
