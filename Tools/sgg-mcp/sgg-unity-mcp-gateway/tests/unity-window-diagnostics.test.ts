import { createHash } from "node:crypto";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { describe, expect, it } from "vitest";

import { createUnityWindowDiagnosticsRuntime, externalSceneReloadEvidence, inspectUnityWindowDiagnostics, projectDowngradeEvidence, respondToUnityRecoveryPrompt, type ExecFileSyncLike, type UnityProjectDowngradeEvidence, type UnitySceneBackupKeepArgs, type UnitySceneBackupKeepEvidence, type UnityWindowDiagnosticsStatus } from "../src/diagnostics/unity-window-diagnostics.js";

const scannerFixtures = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "mcp-pr-150-native-scanner.json"), "utf8")) as {
  tasklist: Record<string, { stdout_utf8?: string; stdout_base64?: string; stdout_sha256?: string; stdout_bytes?: number }>;
  native_powershell: Record<string, { stdout_utf8: string }>;
};
const scopedRegistryFixture = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "mcp-pr-151-scoped-registry-modal.json"), "utf8")) as {
  row: {
    hwnd: string;
    pid: number;
    process_name: string;
    process_start_utc: string;
    window_thread_id: number;
    window_visible: boolean;
    window_enabled: boolean;
    class_name: string;
    window_title: string;
    child_texts: string[];
    child_controls: Array<{ hwnd: string; parent_hwnd: string; pid: number; window_thread_id: number; visible: boolean; enabled: boolean; class_name: string; text: string }>;
  };
};
const sceneBackupRecoveryFixture = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "mcp-pr-159-scene-backup-recovery-modal.json"), "utf8")) as {
  row: typeof scopedRegistryFixture.row;
};
const lowDiskSpaceFixture = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "mcp-pr-161-low-disk-space-modal.json"), "utf8")) as {
  row: typeof scopedRegistryFixture.row & {
    child_controls: Array<(typeof scopedRegistryFixture.row.child_controls[number]) & { control_id: number; read_only: boolean; multiline: boolean }>;
  };
};
const sceneBackupDetectedFixture = JSON.parse(fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "fixtures", "mcp-pr-219-scene-backup-detected-modal.json"), "utf8")) as {
  fixture_kind: string;
  evidence: { native_topology_source: string };
  row: Omit<typeof scopedRegistryFixture.row, "child_controls"> & {
    modal_like_reason: string;
    inspection_complete: boolean;
    child_controls: Array<(typeof scopedRegistryFixture.row.child_controls[number]) & { control_id: number; read_only: boolean; multiline: boolean }>;
  };
};

const responseReaderCases = [
  { kind: "reload", nativeType: "SGGUnityExternalSceneReloadUser32", finalRead: "$currentButtonText = Get-WindowTextValue $button", clickHandle: "$button" },
  { kind: "ignore", nativeType: "SGGUnityExternalSceneReloadUser32", finalRead: "$currentButtonText = Get-WindowTextValue $button", clickHandle: "$button" },
  { kind: "scoped_registry_close", nativeType: "SGGUnityScopedRegistryCloseUser32", finalRead: "$buttonText=Get-Text $close[0].handle", clickHandle: "$close[0].handle" },
  { kind: "project_downgrade_continue", nativeType: "SGGUnityProjectDowngradeUser32", finalRead: "$buttonBoundaryValid=(Test-BoundButton", clickHandle: "$continue[0].handle" },
  { kind: "safe_mode_enter", nativeType: "SGGUnitySafeModeEnterUser32", finalRead: "$buttonText=Get-Text $enter[0].handle", clickHandle: "$enter[0].handle" },
] as const;
type ResponseReaderKind = typeof responseReaderCases[number]["kind"];

describe("Unity window diagnostics", () => {
  it("fails closed for empty or malformed tasklist output", () => {
    for (const output of ["", "not,csv", '"Unity.exe","123","Console","1","200 K","Running","user","0:01:00","']) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe") ? output : "[]" });
      expect(status.errors).toContain("tasklist_invalid_output:Unity.exe");
    }
  });

  it("uses one authoritative Unity.exe tasklist probe", () => {
    const calls: string[][] = [];
    const output = '"Unity.exe","41001","Console","1","200 K","Running","user","0:01:00","Game - Unity"';
    const status = inspectUnityWindowDiagnostics({ execFileSync: (file, args) => {
      if (file.endsWith("tasklist.exe")) calls.push(args);
      return file.endsWith("tasklist.exe") ? output : "[]";
    } });

    expect(status.top_level_window_scan_authoritative).toBe(true);
    expect(calls).toHaveLength(1);
    expect(calls[0]).toContain("IMAGENAME eq Unity.exe");
    expect(calls[0]).not.toContain("IMAGENAME eq Unity");
  });

  it("accepts the exact captured Russian OEM-866 no-match bytes", () => {
    const fixture = scannerFixtures.tasklist.russian_oem866_no_match!;
    const output = Buffer.from(fixture.stdout_base64!, "base64");
    const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe") ? output : "[]" });

    expect(output).toHaveLength(fixture.stdout_bytes!);
    expect(createHash("sha256").update(output).digest("hex")).toBe(fixture.stdout_sha256);
    expect(status).toMatchObject({
      top_level_window_scan_authoritative: true,
      active_unity_windows_count: 0,
      helper_observations: {
        tasklist: {
          authoritative: true,
          stdout_bytes: 65,
          stdout_encoding: "ibm866",
        },
      },
    });
  });

  it("keeps native authority separate from failed top-level inventory", () => {
    const native = scannerFixtures.native_powershell.success!.stdout_utf8.replace('"user32_scan_succeeded":true', '"user32_scan_succeeded":true,"user32_scan_complete":true,"user32_candidate_count":0');
    const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe") ? "malformed" : native });

    expect(status).toMatchObject({
      supported: true,
      top_level_window_scan_authoritative: false,
      native_modal_scan_authoritative: true,
      aggregate_scan_authoritative: false,
      semantic_channels: {
        top_level_inventory: { authoritative: false, status: "failed" },
        native_user32: { authoritative: true, status: "authoritative" },
      },
    });
  });

  it("reports scoped UIA authority without claiming broad native or aggregate authority", () => {
    const tasklist = scannerFixtures.tasklist.english_no_match!.stdout_utf8!;
    const native = scannerFixtures.native_powershell.partial!.stdout_utf8;
    const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe") ? tasklist : native });

    expect(status).toMatchObject({
      top_level_window_scan_authoritative: true,
      native_modal_scan_authoritative: false,
      aggregate_scan_authoritative: false,
      semantic_channels: {
        native_user32: { authoritative: false, status: "partial", partial_reason: "user32_scan_failed" },
        native_uia_scoped_fallback: { authoritative: true, status: "authoritative" },
      },
    });
  });

  it("does not start another helper after the shared monotonic budget reaches cleanup reserve", () => {
    let now = 0;
    const calls: Array<{ file: string; timeout: number }> = [];
    const status = inspectUnityWindowDiagnostics({
      scanBudgetMs: 5000,
      nowMonotonic: () => now,
      execFileSync: (file, _args, options) => {
        calls.push({ file, timeout: options.timeout });
        now = 4800;
        return scannerFixtures.tasklist.english_no_match!.stdout_utf8!;
      },
    });

    expect(calls).toHaveLength(1);
    expect(calls[0]).toMatchObject({ timeout: 4750 });
    expect(status.scan_elapsed_ms).toBe(4800);
    expect(status.helper_observations.native_powershell).toMatchObject({ status: "skipped", normalized_error_code: "scan_budget_exhausted" });
    expect(status.aggregate_scan_authoritative).toBe(false);
  });

  it("returns bounded typed timeout telemetry with the last native scanner stage", () => {
    const timeoutFixture = scannerFixtures.native_powershell.timeout_partial!;
    const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => {
      if (file.endsWith("tasklist.exe")) return scannerFixtures.tasklist.english_no_match!.stdout_utf8!;
      const error = new Error(`spawnSync powershell.exe ETIMEDOUT Add-Type ${"x".repeat(5000)}`) as Error & { code: string; stdout: Buffer; stderr: Buffer };
      error.code = "ETIMEDOUT";
      error.stdout = Buffer.from(timeoutFixture.stdout_utf8, "utf8");
      error.stderr = Buffer.alloc(0);
      throw error;
    } });

    expect(status.helper_observations.native_powershell).toMatchObject({
      status: "timeout",
      timed_out: true,
      normalized_error_code: "helper_timeout",
      last_stage: "user32_children:entered",
    });
    expect(status.errors).toEqual(["native_modal_scan_failed:helper_timeout"]);
    expect(JSON.stringify(status)).not.toContain("Add-Type");
    expect(JSON.stringify(status).length).toBeLessThan(10_000);
  });

  it.each(["user32_process", "user32_title", "user32_children", "user32_child_text", "user32_top_level"] as const)("localizes a native timeout to %s without resetting the shared scanner budget", (stage) => {
    let now = 0;
    const calls: Array<{ file: string; timeout: number }> = [];
    const status = inspectUnityWindowDiagnostics({
      scanBudgetMs: 5000,
      nowMonotonic: () => now,
      execFileSync: (file, _args, options) => {
        calls.push({ file, timeout: options.timeout });
        if (file.endsWith("tasklist.exe")) {
          now = 200;
          return '"Unity.exe","21901","Console","1","200 K","Running","user","0:00:01","Scene Backup Detected"';
        }
        now = 4750;
        const error = new Error("private native helper details") as Error & { code: string; stdout: Buffer; stderr: Buffer };
        error.code = "ETIMEDOUT";
        error.stdout = Buffer.from(`__SGG_SCAN_STAGE__:user32_children:entered\n__SGG_SCAN_STAGE__:${stage}:entered\n`, "utf8");
        error.stderr = Buffer.from("C:\\private\\project native body", "utf8");
        throw error;
      },
    });

    expect(calls).toHaveLength(2);
    expect(calls.map((call) => call.timeout)).toEqual([4750, 4550]);
    expect(status).toMatchObject({
      scan_budget_ms: 5000,
      scan_elapsed_ms: 4750,
      top_level_window_scan_authoritative: true,
      native_modal_scan_authoritative: false,
      aggregate_scan_authoritative: false,
      native_modal_candidates_count: 0,
      helper_observations: { native_powershell: { status: "timeout", normalized_error_code: "helper_timeout", last_stage: `${stage}:entered` } },
    });
    expect(JSON.stringify(status)).not.toMatch(/private|native body/);
  });

  it("never exposes an arbitrary helper-supplied stage name or promotes its malformed payload", () => {
    const unknownMarker = "__SGG_SCAN_STAGE__:private_project_or_control_identity:entered";
    for (const timedOut of [false, true]) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => {
        if (file.endsWith("tasklist.exe")) return scannerFixtures.tasklist.english_no_match!.stdout_utf8!;
        const output = `__SGG_SCAN_STAGE__:user32_process:entered\n${unknownMarker}\n`;
        if (!timedOut) return `${output}{"rows":[],"user32_scan_succeeded":true,"user32_scan_complete":true,"user32_candidate_count":0,"uia_scan_succeeded":false,"uia_scan_status":"not_required"}`;
        const error = new Error("timeout") as Error & { code: string; stdout: Buffer };
        error.code = "ETIMEDOUT";
        error.stdout = Buffer.from(output, "utf8");
        throw error;
      } });

      expect(status.helper_observations.native_powershell.last_stage).toBe("user32_process:entered");
      expect(status.native_modal_scan_authoritative).toBe(false);
      expect(status.aggregate_scan_authoritative).toBe(false);
      expect(JSON.stringify(status)).not.toContain("private_project_or_control_identity");
    }
  });

  it("places fixed process/title/children/text stages around each native scanner operation without the old sticky children marker", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", calls, "[]") });
    const script = calls.find((call) => call.file.endsWith("powershell.exe"))!.args.at(-1)!;
    const ordered = [
      "Write-ScanStage 'user32_process' 'entered'",
      "$process = Get-Process",
      "$process.StartTime.ToUniversalTime()",
      "Write-ScanStage 'user32_process' 'completed'",
      "Write-ScanStage 'user32_title' 'entered'",
      "$titleRead = Read-WindowTextValue $handle",
      "Write-ScanStage 'user32_title' 'completed'",
      "Write-ScanStage 'user32_children' 'entered'",
      "::EnumChildWindows($handle",
      "Write-ScanStage 'user32_child_text' 'entered'",
      "$textRead = Read-WindowTextValue $childHandle",
      "Write-ScanStage 'user32_child_text' 'completed'",
      "[uint32]$childPid = 0",
      "Write-ScanStage 'user32_children' 'completed'",
      "$dialogText =",
    ];
    const indices = ordered.map((part) => script.indexOf(part));
    expect(indices.every((index) => index >= 0)).toBe(true);
    expect(indices).toEqual([...indices].sort((left, right) => left - right));
    expect(script).toContain("Write-ScanStage 'user32_child_text' 'completed'\n    Write-ScanStage 'user32_children' 'entered'");
    expect(script).toContain("Write-ScanStage 'user32_children' 'completed'\n  Write-ScanStage 'user32_top_level' 'entered'");
    expect(script).not.toContain("user32ChildrenStageEntered");
    expect(script).toContain("if ($script:scanStageCount -gt 256) { return }");
    expect(script).toContain("__SGG_SCAN_STAGE__:trace_limit:skipped");
    expect(script).toContain("0x0002, 100, [ref]$messageResult");
    expect(script).not.toMatch(/0x00F5|SetForeground|SetFocus|SendKeys|PostMessage|InvokePattern/);
    for (const marker of script.matchAll(/Write-ScanStage '([^']+)' '(entered|completed|skipped)'/g)) {
      expect(marker[1]).toMatch(/^(add_type|user32_top_level|user32_process|user32_title|user32_children|user32_child_text|uia_enumeration|serialization)$/);
    }
  });

  it("does not claim a stale native stage after the bounded tracing limit is reached", () => {
    const status = inspectUnityWindowDiagnostics({ execFileSync: file => {
      if (file.endsWith("tasklist.exe")) return "";
      const error = new Error("timeout") as Error & { code: string; stdout: Buffer };
      error.code = "ETIMEDOUT";
      error.stdout = Buffer.from("__SGG_SCAN_STAGE__:user32_child_text:entered\n__SGG_SCAN_STAGE__:trace_limit:skipped\n");
      throw error;
    } });
    expect(status.helper_observations.native_powershell.last_stage).toBe("trace_limit:skipped");
    expect(status.native_modal_scan_authoritative).toBe(false);
  });

  it("fails closed when helper stdout exceeds the byte limit", () => {
    const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe")
      ? Buffer.alloc(128 * 1024 + 1, 65)
      : scannerFixtures.native_powershell.success!.stdout_utf8 });

    expect(status.helper_observations.tasklist).toMatchObject({
      status: "error",
      stdout_bytes: 128 * 1024,
      stdout_truncated: true,
      normalized_error_code: "helper_stdout_limit_exceeded",
      authoritative: false,
    });
    expect(status.top_level_window_scan_authoritative).toBe(false);
    expect(status.aggregate_scan_authoritative).toBe(false);
  });

  it("reports non-zero helper exit metadata without returning raw stderr", () => {
    const status = inspectUnityWindowDiagnostics({ execFileSync: (file) => {
      if (!file.endsWith("tasklist.exe")) return scannerFixtures.native_powershell.success!.stdout_utf8;
      const error = new Error("sensitive raw command and stderr") as Error & { status: number; stdout: Buffer; stderr: Buffer };
      error.status = 7;
      error.stdout = Buffer.from("partial", "utf8");
      error.stderr = Buffer.from("bounded tasklist failure", "utf8");
      throw error;
    } });

    expect(status.helper_observations.tasklist).toMatchObject({
      status: "error",
      exit_code: 7,
      timed_out: false,
      stdout_bytes: 7,
      stderr_bytes: 24,
      normalized_error_code: "helper_failed",
    });
    expect(JSON.stringify(status)).not.toContain("sensitive raw command");
    expect(JSON.stringify(status)).not.toContain("bounded tasklist failure");
  });

  it("marks failed or malformed native modal scans as non-authoritative", () => {
    const failed = inspectUnityWindowDiagnostics({
      execFileSync: (file) => {
        if (file.endsWith("tasklist.exe")) return "INFO: No tasks are running which match the specified criteria.";
        const error = new Error("EnumWindows invocation failed");
        (error as { code?: string }).code = "EIO";
        throw error;
      },
    });
    const baseRow = { hwnd: "0x1", pid: 1, process_name: "Unity", class_name: "#32770", window_title: "Enter Safe Mode?", child_texts: [] };
    const malformedOutputs = [
      "", "not-json", "{}", "null", "\"unexpected\"", "[\"unexpected\"]", "[{}]",
      JSON.stringify({ rows: [null], user32_scan_succeeded: true, uia_scan_succeeded: true }),
      JSON.stringify([{ ...baseRow, process_start_utc: "not-a-date" }]),
      JSON.stringify([{ ...baseRow, window_thread_id: 1.5 }]),
      JSON.stringify([{ ...baseRow, child_controls: [{}] }]),
      JSON.stringify([{ ...baseRow, window_title: "", child_texts: [] }]),
    ];

    expect(failed).toMatchObject({
      native_modal_scan_authoritative: false,
      native_modal_candidates_count: 0,
      blocking_automation_possible: false,
    });
    expect(failed.errors).toContain("native_modal_scan_failed:helper_failed");
    for (const output of malformedOutputs) {
      const malformed = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe")
        ? "INFO: No tasks are running which match the specified criteria."
        : output });
      expect(malformed.native_modal_scan_authoritative, output).toBe(false);
      expect(malformed.errors, output).toContain("native_modal_scan_invalid_output");
      expect(malformed.next_valid_actions, output).toEqual(expect.arrayContaining(["retry_native_unity_modal_scan", "inspect_visible_unity_windows_manually"]));
    }
  });

  it("retains unknown Unity modal-like evidence as redacted partial coverage", () => {
    const windowTitle = "Private Tool Confirmation";
    const childText = "Confidential project-specific question";
    const native = JSON.stringify({
      rows: [{
        hwnd: "0x610500",
        pid: 24683,
        process_name: "Unity",
        process_start_utc: "2026-09-07T15:50:00.000Z",
        window_thread_id: 1401,
        window_visible: true,
        window_enabled: true,
        class_name: "#32770",
        window_title: windowTitle,
        child_texts: [childText],
        child_controls: [{ hwnd: "0x610501", parent_hwnd: "0x610500", pid: 24683, window_thread_id: 1401, visible: true, enabled: true, control_id: 1, class_name: "Static", text: childText }],
        modal_like_reason: "dialog_class",
        inspection_complete: true,
      }],
      user32_scan_succeeded: true,
      user32_scan_complete: true,
      user32_candidate_count: 1,
      uia_scan_succeeded: false,
      uia_scan_status: "not_required",
    });
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], native) });

    expect(status).toMatchObject({
      native_modal_scan_authoritative: false,
      aggregate_scan_authoritative: false,
      native_modal_candidates_count: 1,
      blocking_automation_possible: false,
      semantic_channels: { native_user32: { status: "partial", authoritative: false, partial_reason: "unclassified_unity_modal_candidate" } },
      native_modal_candidates: [{
        window_title: "<redacted>",
        child_texts: [],
        unclassified_modal: {
          evidence_redacted: true,
          modal_like_reason: "dialog_class",
          window_title_sha256: `sha256:${createHash("sha256").update(windowTitle).digest("hex")}`,
          visible_child_text_sha256: `sha256:${createHash("sha256").update(childText).digest("hex")}`,
          visible_child_control_count: 1,
          inspection_complete: true,
        },
      }],
      errors: ["native_modal_scan_partial:unclassified_unity_modal_candidate"],
      next_valid_actions: expect.arrayContaining(["operator_review_unclassified_native_unity_modal", "do_not_treat_zero_candidates_as_authoritative"]),
    });
    expect(JSON.stringify(status)).not.toContain(windowTitle);
    expect(JSON.stringify(status)).not.toContain(childText);
  });

  it("makes omitted native candidates and incomplete inspection non-authoritative", () => {
    const legacy = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe")
      ? "INFO: No tasks are running which match the specified criteria."
      : "[]" });
    const missingMetadata = inspectUnityWindowDiagnostics({ execFileSync: (file) => file.endsWith("tasklist.exe")
      ? "INFO: No tasks are running which match the specified criteria."
      : JSON.stringify({ rows: [], user32_scan_succeeded: true, uia_scan_succeeded: false, uia_scan_status: "not_required" }) });
    const omitted = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify({
      rows: [],
      user32_scan_succeeded: true,
      user32_scan_complete: true,
      user32_candidate_count: 1,
      uia_scan_succeeded: false,
      uia_scan_status: "not_required",
    })) });
    const incomplete = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify({
      rows: [{
        hwnd: "0x620500", pid: 24683, process_name: "Unity", process_start_utc: "2026-09-07T15:50:00.000Z", window_thread_id: 1401,
        window_visible: true, window_enabled: true, class_name: "#32770", window_title: "", child_texts: [], child_controls: [],
        modal_like_reason: "dialog_class", inspection_complete: false,
      }],
      user32_scan_succeeded: true,
      user32_scan_complete: false,
      user32_candidate_count: 1,
      uia_scan_succeeded: false,
      uia_scan_status: "not_required",
    })) });

    expect(legacy).toMatchObject({ native_modal_scan_authoritative: false, aggregate_scan_authoritative: false, errors: ["native_modal_scan_partial:coverage_metadata_missing"] });
    expect(missingMetadata).toMatchObject({ native_modal_scan_authoritative: false, aggregate_scan_authoritative: false, errors: ["native_modal_scan_partial:coverage_metadata_missing"] });
    expect(omitted).toMatchObject({ native_modal_scan_authoritative: false, aggregate_scan_authoritative: false, native_modal_candidates_count: 0, errors: ["native_modal_scan_partial:candidate_projection_incomplete"] });
    expect(incomplete).toMatchObject({ native_modal_scan_authoritative: false, aggregate_scan_authoritative: false, native_modal_candidates_count: 1, errors: ["native_modal_scan_partial:candidate_inspection_incomplete"] });
  });

  it("keeps ordinary unowned Unity main windows outside modal projection", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const native = JSON.stringify({ rows: [], user32_scan_succeeded: true, user32_scan_complete: true, user32_candidate_count: 0, uia_scan_succeeded: false, uia_scan_status: "not_required" });
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync('"Unity.exe","24683","Console","1","200 K","Running","user","0:01:00","Game - Unity"', calls, native) });

    expect(status).toMatchObject({ native_modal_scan_authoritative: true, aggregate_scan_authoritative: true, native_modal_candidates_count: 0 });
    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("$isUnityProcess = $processName -ceq 'Unity'");
    expect(scannerScript).toContain("$className -ceq '#32770'");
    expect(scannerScript).toContain("GetWindow($handle, 4)");
    expect(scannerScript).toContain("$extendedStyle -band 0x00000001");
    expect(scannerScript).toContain("if (-not $knownTextMatch -and -not $modalLikeReason) { return $true }");
  });

  it("caches explicit scans without rescanning on cached reads", () => {
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync('"Unity.exe","41001","Console","1","200 K","Running","user","0:01:00","MyGame - Unity"', []) });
    let scans = 0;
    const runtime = createUnityWindowDiagnosticsRuntime({
      inspect: () => {
        scans += 1;
        return status;
      },
      now: () => new Date("2026-07-14T18:20:00.000Z"),
    });

    expect(runtime.cachedStatus?.()).toBeUndefined();
    expect(runtime.status()).toBe(status);
    expect(runtime.cachedStatus?.()).toEqual({ status, captured_at: "2026-07-14T18:20:00.000Z" });
    expect(runtime.cachedStatus?.()).toEqual({ status, captured_at: "2026-07-14T18:20:00.000Z" });
    expect(scans).toBe(1);
  });

  it("deduplicates overlapping async scans before updating cached evidence", async () => {
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], "[]") });
    const newerStatus = { ...status, errors: ["newer_sync_scan"] };
    let scans = 0;
    let finish!: (value: typeof status) => void;
    const runtime = createUnityWindowDiagnosticsRuntime({
      inspect: () => newerStatus,
      inspectAsync: () => {
        scans += 1;
        return new Promise((resolve) => { finish = resolve; });
      },
    });

    const first = runtime.statusAsync!();
    const second = runtime.statusAsync!();
    expect(first).toBe(second);
    expect(scans).toBe(1);
    expect(runtime.status()).toBe(newerStatus);
    finish(status);
    await expect(first).resolves.toBe(newerStatus);
    expect(runtime.cachedStatus?.()?.status).toBe(newerStatus);
  });

  it("detects likely Unity recovery modal windows from tasklist titles", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync = fakeExecFileSync([
      '"Unity.exe","41000","Console","1","200 K","Running","user","0:01:00","Unity Project Recovery"',
      '"Unity.exe","41001","Console","1","200 K","Running","user","0:01:00","MyGame - SampleScene - Windows, Mac, Linux - Unity 2022.3"',
      '"Unity.exe","41002","Console","1","200 K","Running","user","0:01:00","N/A"',
    ].join("\n"), calls);

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status).toMatchObject({
      supported: true,
      active_unity_windows_count: 2,
      recovery_prompt_candidates_count: 1,
      blocking_startup_possible: true,
      recovery_prompt_candidates: [{ pid: 41000, window_title: "Unity Project Recovery", responding: true, likely_recovery_prompt: true }],
    });
    expect(status.recovery_prompt_candidates[0]?.matched_keywords).toContain("recovery");
    expect(status.next_valid_actions).toContain("operator_close_recovery_prompt_discard_if_safe");
    expect(status.next_valid_actions).toContain("dry_run_diagnostics.unity_recovery_prompt.respond");
    expect(calls.some((call) => call.file.endsWith("tasklist.exe") && call.args.includes("IMAGENAME eq Unity.exe"))).toBe(true);
  });

  it("detects Unity Licensing Client native application error dialogs owned by csrss", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([{
        hwnd: "0x110E52",
        pid: 1672,
        process_name: "csrss",
        class_name: "#32770",
        window_title: "Unity.Licensing.Client.exe - Ошибка приложения",
        child_texts: [
          "ОК",
          "Отмена",
          "Исключение неизвестное программное исключение (0xe0434352) в приложении по адресу 0x00007FFE6BA41B6A.",
        ],
      },
    ]);
    const execFileSync = fakeExecFileSync(
      '"Unity.exe","41050","Console","1","200 K","Running","user","0:01:00","MyGame - SampleScene - Unity 6000.0"',
      calls,
      nativeModalOutput,
    );

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status).toMatchObject({
      supported: true,
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "unity_licensing_client_crash_modal",
      native_modal_candidates_count: 1,
      licensing_client_crash_modal_candidates_count: 1,
      licensing_client_crash_modal_candidates: [
        {
          hwnd: "0x110E52",
          pid: 1672,
          process_name: "csrss",
          class_name: "#32770",
          window_title: "Unity.Licensing.Client.exe - Ошибка приложения",
          exception_code: "0xe0434352",
          block_code: "unity_licensing_client_crash_modal",
        },
      ],
    });
    expect(status.next_valid_actions).toContain("operator_close_unity_licensing_client_error_dialog");
  });

  it("detects the fully evidenced English Unity Licensing Client application error", () => {
    const nativeWindow = {
      hwnd: "0x110E53",
      pid: 1673,
      process_name: "csrss",
      class_name: "#32770",
      window_title: "Unity.Licensing.Client.exe - Application Error",
      child_texts: [
        "OK",
        "Cancel",
        "The exception unknown software exception (0xe0434352) occurred in the application at location 0x00007FFFC6431C0A.",
      ],
    };

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([nativeWindow])) });

    expect(status.licensing_client_crash_modal_candidates_count).toBe(1);
    expect(status.blocking_modal_code).toBe("unity_licensing_client_crash_modal");
  });

  it("rejects unrelated and incomplete Unity Licensing Client application errors", () => {
    const complete = {
      hwnd: "0x110E54",
      pid: 1674,
      process_name: "csrss",
      class_name: "#32770",
      window_title: "Unity.Licensing.Client.exe - Application Error",
      child_texts: [
        "OK",
        "Cancel",
        "The exception unknown software exception (0xe0434352) occurred in the application at location 0x00007FFFC6431C0A.",
      ],
    };
    const message = complete.child_texts[2] ?? "";
    const variants = [
      { ...complete, class_name: "UnityContainerWndClass" },
      { ...complete, window_title: "Other.exe - Application Error" },
      { ...complete, window_title: "Unity.Licensing.Client.exe - Unexpected failure" },
      { ...complete, child_texts: ["OK", "Cancel", message.replace("unknown software exception", "access violation")] },
      { ...complete, child_texts: ["OK", "Cancel", message.replace("0xe0434352", "0xc0000005")] },
      { ...complete, child_texts: ["OK", "Cancel", message.replace("0x00007FFFC6431C0A", "0xe0434352")] },
      { ...complete, child_texts: complete.child_texts.filter((text) => text !== "OK") },
      { ...complete, child_texts: complete.child_texts.filter((text) => text !== "Cancel") },
      { ...complete, window_title: "Other.exe - Application Error", child_texts: ["OK", "Cancel", message] },
    ];

    for (const [index, variant] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([{ ...variant, hwnd: `0x${1200 + index}` }])) });
      expect(status.licensing_client_crash_modal_candidates_count).toBe(0);
      expect(status.blocking_modal_code).not.toBe("unity_licensing_client_crash_modal");
      expect(status.next_valid_actions).not.toContain("operator_close_unity_licensing_client_error_dialog");
    }
  });

  it("detects Corrupted Library native modal dialogs", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([{
        hwnd: "0x200204",
        pid: 24680,
        process_name: "Unity",
        class_name: "#32770",
        window_title: "Corrupted Library Detected",
        child_texts: [
          "Corrupted files in the Library prevented Unity to load your project. Do you want to rebuild the Library?",
          "Rebuild Library",
          "Don't rebuild Library",
        ],
      },
    ]);
    const execFileSync = fakeExecFileSync("", calls, nativeModalOutput);

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status).toMatchObject({
      supported: true,
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_corrupted_library",
      native_modal_candidates_count: 1,
      corrupted_library_modal_candidates_count: 1,
      corrupted_library_modal_candidates: [
        {
          hwnd: "0x200204",
          pid: 24680,
          process_name: "Unity",
          class_name: "#32770",
          window_title: "Corrupted Library Detected",
          block_code: "blocked_corrupted_library",
        },
      ],
    });
    expect(status.next_valid_actions).toContain("operator_choose_dont_rebuild_library_or_close_dialog");
  });

  it("detects the exact duplicate-project modal without exposing its project path or OK handle", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeWindow = duplicateProjectModalWindow();
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", calls, JSON.stringify([nativeWindow])) });

    expect(status).toMatchObject({
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_duplicate_project_instance",
      duplicate_project_instance_modal_candidates_count: 1,
      duplicate_project_instance_modal_candidates: [{
        hwnd: "0x500500",
        pid: 6636,
        process_name: "Unity",
        process_start_utc: "2026-08-18T13:00:00.000Z",
        window_thread_id: 1700,
        class_name: "#32770",
        window_title: "Error!",
        child_texts: ["OK"],
        likely_duplicate_project_instance: true,
        duplicate_project_instance: {
          body_sha256: expect.stringMatching(/^sha256:[0-9a-f]{64}$/),
          project_identity_sha256: expect.stringMatching(/^sha256:[0-9a-f]{64}$/),
          body_evidence: { another_instance_running: true, multiple_instances_forbidden: true, project_path_present: true },
          button_count: 1,
          ok_button_count: 1,
          diagnostic_only: true,
          interaction_supported: false,
        },
        block_code: "blocked_duplicate_project_instance",
      }],
    });
    expect(status.next_valid_actions).toEqual(expect.arrayContaining(["inspect_unity_processes_and_project_roots", "do_not_auto_click_duplicate_project_dialog", "do_not_launch_another_unity_instance"]));
    const serialized = JSON.stringify(status);
    expect(serialized).not.toContain("C:\\\\Work\\\\Unity\\\\road-to-ithaka");
    expect(serialized).not.toContain("0x500510");
    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("^Error!");
    expect(scannerScript).toContain("SendMessageTimeoutText($handle, 0x000D");
    expect(scannerScript).not.toContain("0x00F5");
  });

  it("rejects incomplete, unrelated, ambiguous, or non-bound duplicate-project dialogs", () => {
    const complete = duplicateProjectModalWindow();
    const body = complete.child_texts[0]!;
    const button = complete.child_controls[1]!;
    const variants = [
      { ...complete, process_name: "Other" },
      { ...complete, process_start_utc: "" },
      { ...complete, window_thread_id: 0 },
      { ...complete, class_name: "OtherWindowClass" },
      { ...complete, window_title: "Error" },
      { ...complete, child_texts: [body.replace("another Unity instance", "a Unity process"), "OK"] },
      { ...complete, child_texts: [body.replace("Multiple Unity instances cannot open the same project.", "Only one Editor is recommended."), "OK"] },
      { ...complete, child_texts: [body.replace("C:\\Work\\Unity\\road-to-ithaka", "road-to-ithaka"), "OK"] },
      { ...complete, child_texts: [`${body}\nProject: D:\\Other`, "OK"] },
      { ...complete, child_controls: complete.child_controls.filter((control) => control.class_name !== "Button") },
      { ...complete, child_controls: [{ ...button, enabled: false }] },
      { ...complete, child_controls: [{ ...button, pid: 9999 }] },
      { ...complete, child_controls: [{ ...button, window_thread_id: 1701 }] },
      { ...complete, child_controls: [{ ...complete.child_controls[0]!, pid: 9999 }, button] },
      { ...complete, child_controls: [button, { ...button, hwnd: "0x500511", text: "Cancel" }] },
      { ...complete, child_controls: [{ ...button, class_name: "ButtonHost" }] },
    ];

    for (const [index, variant] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([{ ...variant, hwnd: `0x${6000 + index}` }])) });
      expect(status.duplicate_project_instance_modal_candidates_count, `variant ${index}`).toBe(0);
      expect(status.blocking_modal_code, `variant ${index}`).not.toBe("blocked_duplicate_project_instance");
    }
  });

  it("detects the exact read-only scoped-registry import dialog with bounded hash evidence", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const row = scopedRegistryModalWindow();
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", calls, JSON.stringify([row])) });

    expect(status).toMatchObject({
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_scoped_registry_import",
      scoped_registry_import_modal_candidates_count: 1,
      scoped_registry_import_modal_candidates: [{
        hwnd: "0x510500",
        pid: 80872,
        process_name: "Unity",
        process_start_utc: "2026-08-24T05:10:19.007Z",
        window_thread_id: 1700,
        window_visible: true,
        window_enabled: true,
        class_name: "#32770",
        window_title: "Importing a scoped registry",
        child_texts: ["Learn More", "Close"],
        likely_scoped_registry_import_prompt: true,
        scoped_registry_import: {
          title_sha256: `sha256:${createHash("sha256").update("Importing a scoped registry").digest("hex")}`,
          body_sha256: `sha256:${createHash("sha256").update("A new scoped registry is now available in the Package Manager.").digest("hex")}`,
          body_evidence: { exact_single_registry_message: true, package_manager_named: true },
          visible_control_count: 3,
          text_control_count: 3,
          button_count: 2,
          learn_more_button_count: 1,
          close_button_count: 1,
          diagnostic_only: true,
          interaction_supported: false,
        },
        block_code: "blocked_scoped_registry_import",
      }],
    });
    expect(status.next_valid_actions).toEqual(expect.arrayContaining([
      "operator_review_scoped_registry_import_dialog",
      "do_not_click_learn_more_or_close_from_gateway",
      "do_not_repeat_blocked_test_compile_or_package_operation",
      "require_fresh_heartbeat_catalog_and_operation_status_after_operator_resolution",
    ]));
    expect(status.next_valid_actions).not.toContain("retry_after_modal_closed");
    const serialized = JSON.stringify(status);
    expect(serialized).not.toContain("A new scoped registry is now available");
    expect(serialized).not.toContain("0x510510");
    expect(serialized).not.toContain("0x510511");

    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("^Importing a scoped registry");
    expect(scannerScript).toContain("$dialogName -cne 'Importing a scoped registry'");
    expect(scannerScript).toContain("window_visible = $windowVisible");
    expect(scannerScript).toContain("window_enabled = $windowEnabled");
    expect(scannerScript).toContain("SendMessageTimeoutText($handle, 0x000D");
    expect(scannerScript).not.toContain("0x00F5");
    expect(scannerScript).not.toContain("InvokePattern");
  });

  it("rejects scoped-registry title-only, wrong-boundary, and incomplete control lookalikes", () => {
    const complete = scopedRegistryModalWindow();
    const body = complete.child_texts[0]!;
    const learnMore = complete.child_controls[1]!;
    const close = complete.child_controls[2]!;
    const variants = [
      { ...complete, process_name: "Other" },
      { ...complete, process_start_utc: "" },
      { ...complete, window_thread_id: 0 },
      { ...complete, window_visible: false },
      { ...complete, window_enabled: false },
      { ...complete, class_name: "UnityContainerWndClass" },
      { ...complete, window_title: "Importing scoped registry" },
      { ...complete, child_texts: ["Learn More", "Close"], child_controls: [learnMore, close] },
      { ...complete, child_texts: [body.replace("Package Manager", "registry settings"), "Learn More", "Close"] },
      { ...complete, child_texts: [body, "Close"], child_controls: [complete.child_controls[0]!, close] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...learnMore, enabled: false }, close] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...learnMore, pid: 80873 }, close] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...learnMore, window_thread_id: 1701 }, close] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...learnMore, parent_hwnd: "0x999999" }, close] },
      { ...complete, child_texts: [body, "Learn More", "Close", "Cancel"], child_controls: [...complete.child_controls, { ...close, hwnd: "0x510512", text: "Cancel" }] },
      { ...complete, child_controls: [...complete.child_controls, { ...close, hwnd: "0x510512", text: "Help" }], child_texts: [body, "Learn More", "Close", "Help"] },
    ];

    for (const [index, variant] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([{ ...variant, hwnd: `0x52${index.toString(16)}` }])) });
      expect(status.scoped_registry_import_modal_candidates_count, `variant ${index}`).toBe(0);
      expect(status.blocking_modal_code, `variant ${index}`).not.toBe("blocked_scoped_registry_import");
    }
  });

  it("retains read-only scoped-registry evidence for scoped UIA fallback and duplicate exact windows", () => {
    const first = scopedRegistryModalWindow();
    const second = {
      ...scopedRegistryModalWindow(),
      hwnd: "0x520500",
      child_controls: scopedRegistryModalWindow().child_controls.map((control, index) => ({ ...control, hwnd: `0x52051${index}`, parent_hwnd: "0x520500" })),
    };
    const output = JSON.stringify({ rows: [first, second], user32_scan_succeeded: false, uia_scan_succeeded: true, uia_scan_status: "succeeded" });
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], output) });

    expect(status.native_modal_scan_authoritative).toBe(false);
    expect(status.semantic_channels.native_uia_scoped_fallback.authoritative).toBe(true);
    expect(status.errors).toContain("native_modal_scan_partial:user32_failed_uia_fallback");
    expect(status.scoped_registry_import_modal_candidates_count).toBe(2);
    expect(status.blocking_modal_code).toBe("blocked_scoped_registry_import");
    expect(status.scoped_registry_import_modal_candidates).toEqual([
      expect.objectContaining({ hwnd: "0x510500", scoped_registry_import: expect.objectContaining({ interaction_supported: false }) }),
      expect.objectContaining({ hwnd: "0x520500", scoped_registry_import: expect.objectContaining({ interaction_supported: false }) }),
    ]);
    expect(status.next_valid_actions).toEqual(expect.arrayContaining(["do_not_click_learn_more_or_close_from_gateway", "do_not_repeat_blocked_test_compile_or_package_operation"]));
    expect(status.next_valid_actions).not.toContain("retry_after_modal_closed");
  });

  it("issues one short-lived exact scoped-registry Close plan and consumes it before one dispatch", () => {
    const exact = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([scopedRegistryModalWindow()])) });
    const clean = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], "[]") });
    const scans = [exact, exact, clean];
    let dispatches = 0;
    const now = new Date("2026-09-02T10:00:00.000Z");
    const runtime = createUnityWindowDiagnosticsRuntime({
      now: () => now,
      inspect: () => scans.shift() ?? clean,
      closeScopedRegistryImport: () => {
        dispatches += 1;
        return { status: "message_sent", interaction_attempted: true, delivery_state: "message_sent" };
      },
    });
    const projectIdentity = `sha256:${createHash("sha256").update("c:/work/unity/sgg-weather-test").digest("hex")}`;
    const baseArgs = { pid: 80872, process_start_utc: "2026-08-24T05:10:19.007Z", project_identity_sha256: projectIdentity };

    const plan = runtime.planScopedRegistryImportClose!(baseArgs);
    expect(plan).toMatchObject({
      dry_run: true,
      status: "ready",
      interaction_attempted: false,
      native_dispatch_count: 0,
      binding: {
        source: "user32",
        process_generation_verified: true,
        button_count: 2,
        learn_more_button_count: 1,
        close_button_count: 1,
        button_identities_bound_internally: true,
      },
    });
    expect(plan.precondition_hash).toMatch(/^sha256:[a-f0-9]{64}$/);
    const serializedPlan = JSON.stringify(plan);
    expect(serializedPlan).not.toContain("0x510510");
    expect(serializedPlan).not.toContain("0x510511");
    expect(serializedPlan).not.toContain("A new scoped registry is now available");

    if (!plan.precondition_hash) throw new Error("Expected scoped-registry close precondition hash");
    const applyArgs = { ...baseArgs, dry_run: false, confirm_close: true, precondition_hash: plan.precondition_hash };
    const applied = runtime.closeScopedRegistryImport!(applyArgs);
    const repeated = runtime.closeScopedRegistryImport!(applyArgs);

    expect(applied).toMatchObject({ status: "message_sent", native_dispatch_count: 1, delivery_state: "message_sent", popup_state: "modal_absent", popup_disappearance_verified: true, blocked_operation_replayed: false });
    expect(repeated).toMatchObject({ status: "refused", native_dispatch_count: 0, error: "scoped_registry_close_plan_not_issued_expired_or_consumed" });
    expect(dispatches).toBe(1);
  });

  it("refuses scoped-registry Close planning without authoritative User32 evidence", () => {
    const exact = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify({ rows: [scopedRegistryModalWindow()], user32_scan_succeeded: false, uia_scan_succeeded: true, uia_scan_status: "succeeded" })) });
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => exact });
    const result = runtime.planScopedRegistryImportClose!({
      pid: 80872,
      process_start_utc: "2026-08-24T05:10:19.007Z",
      project_identity_sha256: `sha256:${"1".repeat(64)}`,
    });
    expect(result).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "authoritative_user32_scan_required" });
  });

  it("detects the exact read-only scene-backup recovery dialog with bounded hash evidence", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const row = sceneBackupRecoveryModalWindow();
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", calls, JSON.stringify([row])) });
    const body = row.child_texts[0]!;

    expect(status).toMatchObject({
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_scene_backup_recovery",
      scene_backup_recovery_modal_candidates_count: 1,
      scene_backup_recovery_modal_candidates: [{
        hwnd: "0x590500",
        pid: 31856,
        process_name: "Unity",
        process_start_utc: "2026-08-28T15:36:34.3395231Z",
        window_thread_id: 1900,
        window_visible: true,
        window_enabled: true,
        class_name: "#32770",
        window_title: "Recovering Scene Backups",
        child_texts: ["Yes", "No"],
        likely_scene_backup_recovery_prompt: true,
        scene_backup_recovery: {
          title_sha256: `sha256:${createHash("sha256").update("Recovering Scene Backups").digest("hex")}`,
          body_sha256: `sha256:${createHash("sha256").update(body).digest("hex")}`,
          body_evidence: { previous_editor_session_detected: true, editor_may_not_have_closed_correctly: true, recovery_copy_destination_named: true },
          visible_control_count: 3,
          text_control_count: 3,
          button_count: 2,
          yes_button_count: 1,
          no_button_count: 1,
          diagnostic_only: true,
          interaction_supported: false,
        },
        block_code: "blocked_scene_backup_recovery",
      }],
    });
    expect(status.next_valid_actions).toEqual(expect.arrayContaining([
      "operator_review_scene_backup_recovery_dialog",
      "do_not_click_yes_or_no_from_gateway",
      "do_not_focus_terminate_or_restart_unity",
      "do_not_repeat_blocked_test_compile_or_package_operation",
    ]));
    expect(status.next_valid_actions).not.toContain("retry_after_modal_closed");
    expect(status.next_valid_actions.filter((action) => action.includes("unity_recovery_prompt.respond"))).toEqual([]);
    const serialized = JSON.stringify(status);
    expect(serialized).not.toContain("Scene backups from a previous Editor session");
    expect(serialized).not.toContain("0x590510");
    expect(serialized).not.toContain("0x590511");

    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("^Recovering Scene Backups");
    expect(scannerScript).toContain("$dialogName -cne 'Recovering Scene Backups'");
    expect(scannerScript).toContain("SendMessageTimeoutText($handle, 0x000D");
    expect(scannerScript).not.toContain("0x00F5");
    expect(scannerScript).not.toContain("InvokePattern");
  });

  it("rejects scene-backup title-only, wrong-boundary, and incomplete control lookalikes", () => {
    const complete = sceneBackupRecoveryModalWindow();
    const body = complete.child_texts[0]!;
    const yes = complete.child_controls[1]!;
    const no = complete.child_controls[2]!;
    const variants = [
      { ...complete, process_name: "Other" },
      { ...complete, process_start_utc: "" },
      { ...complete, window_thread_id: 0 },
      { ...complete, window_visible: false },
      { ...complete, window_enabled: false },
      { ...complete, class_name: "UnityContainerWndClass" },
      { ...complete, window_title: "Recover Scene Backups" },
      { ...complete, child_texts: ["Yes", "No"], child_controls: [yes, no] },
      { ...complete, child_texts: [body.replace("previous Editor session", "earlier run"), "Yes", "No"] },
      { ...complete, child_texts: [body, "No"], child_controls: [complete.child_controls[0]!, no] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...yes, enabled: false }, no] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...yes, pid: 31857 }, no] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...yes, window_thread_id: 1901 }, no] },
      { ...complete, child_controls: [complete.child_controls[0]!, { ...yes, parent_hwnd: "0x999999" }, no] },
      { ...complete, child_texts: [body, "Yes", "No", "Cancel"], child_controls: [...complete.child_controls, { ...no, hwnd: "0x590512", text: "Cancel" }] },
    ];

    for (const [index, variant] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([{ ...variant, hwnd: `0x59${index.toString(16)}` }])) });
      expect(status.scene_backup_recovery_modal_candidates_count, `variant ${index}`).toBe(0);
      expect(status.blocking_modal_code, `variant ${index}`).not.toBe("blocked_scene_backup_recovery");
    }
  });

  it("retains duplicate exact scene-backup dialogs as ambiguous read-only blockers", () => {
    const first = sceneBackupRecoveryModalWindow();
    const second = sceneBackupRecoveryModalWindow();
    second.hwnd = "0x590600";
    second.child_controls = second.child_controls.map((control, index) => ({ ...control, hwnd: `0x59061${index}`, parent_hwnd: second.hwnd }));

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([first, second])) });

    expect(status.scene_backup_recovery_modal_candidates_count).toBe(2);
    expect(status.blocking_modal_code).toBe("blocked_scene_backup_recovery");
    expect(status.scene_backup_recovery_modal_candidates).toEqual([
      expect.objectContaining({ hwnd: "0x590500", scene_backup_recovery: expect.objectContaining({ interaction_supported: false }) }),
      expect.objectContaining({ hwnd: "0x590600", scene_backup_recovery: expect.objectContaining({ interaction_supported: false }) }),
    ]);
  });

  it("excludes exact scene-backup dialogs from the generic recovery responder", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const tasklist = '"Unity.exe","31856","Console","1","200 K","Running","user","0:00:01","Recovering Scene Backups"';
    const native = JSON.stringify([sceneBackupRecoveryModalWindow()]);

    const result = respondToUnityRecoveryPrompt({ action: "do_not_restore", pids: [31856], dry_run: false }, {
      execFileSync: fakeExecFileSync(tasklist, calls, native),
    });

    expect(result).toMatchObject({
      dry_run: false,
      target_pids: [],
      clicked_count: 0,
      skipped: [{ pid: 31856, reason: "not_active_unity_recovery_prompt" }],
      errors: ["no_recovery_prompt"],
    });
    expect(calls.some((call) => call.file.toLowerCase().endsWith("powershell.exe") && call.args.join(" ").includes("$targetPids"))).toBe(false);
  });

  it("classifies the distinct Scene Backup Detected full body and standard native controls without promoting visual evidence", () => {
    const row = sceneBackupDetectedModalWindow();
    const status = sceneBackupDetectedStatus([row]);
    expect(sceneBackupDetectedFixture.fixture_kind).toContain("synthetic");
    expect(sceneBackupDetectedFixture.evidence.native_topology_source).toContain("not an authoritative live native control trace");
    expect(status).toMatchObject({
      blocking_modal_code: "blocked_scene_backup_recovery",
      native_modal_scan_authoritative: true,
      scene_backup_recovery_modal_candidates_count: 1,
      scene_backup_recovery_modal_candidates: [{
        scene_backup_recovery_variant: "scene_backup_detected",
        child_texts: ["Keep Backups", "Delete"],
        scene_backup_detected: {
          evidence_scope: "complete_body",
          body_sha256: `sha256:${createHash("sha256").update(row.child_texts[0]!).digest("hex")}`,
          visible_control_count: 4,
          button_count: 2,
          keep_backups_button_count: 1,
          delete_button_count: 1,
          diagnostic_only: true,
          interaction_supported: false,
        },
      }],
    });
    expect(status.scene_backup_recovery_modal_candidates![0]).not.toHaveProperty("scene_backup_recovery");
    expect(status.next_valid_actions).toContain("obtain_exact_keep_backups_dry_run_plan");
    expect(status.next_valid_actions).not.toContain("do_not_click_yes_or_no_from_gateway");
    const serialized = JSON.stringify(status);
    expect(serialized).not.toContain(row.child_texts[0]);
    expect(serialized).not.toContain("0x219511");
    expect(serialized).not.toContain("0x219512");
  });

  it("accepts exact Scene Backup Detected words with layout-only line wrapping and the optional native warning icon", () => {
    const row = sceneBackupDetectedModalWindow();
    const body = row.child_texts[0]!.replaceAll(" ", "\r\n");
    row.child_texts[0] = body;
    row.child_controls[1]!.text = body;
    row.child_controls.shift();
    row.child_controls.reverse();
    row.child_texts.reverse();
    expect(sceneBackupDetectedStatus([row]).scene_backup_recovery_modal_candidates).toEqual([
      expect.objectContaining({ scene_backup_recovery_variant: "scene_backup_detected", scene_backup_detected: expect.objectContaining({ visible_control_count: 3 }) }),
    ]);
  });

  it("rejects incomplete, unknown-class, body, topology, and process lookalikes for Scene Backup Detected", () => {
    const row = sceneBackupDetectedModalWindow();
    const [icon, body, keep, remove] = row.child_controls;
    const { visible: _visible, ...unobservedVisible } = keep!;
    const { enabled: _enabled, ...unobservedEnabled } = keep!;
    const { inspection_complete: _inspection, ...unobservedInspection } = row;
    const driftBody = row.child_texts[0]!.replace("keep these scene backups", "delete these scene backups");
    const variants = [
      { ...row, process_name: "Other" }, { ...row, process_start_utc: "" }, { ...row, window_thread_id: 0 },
      { ...row, window_visible: false }, { ...row, window_enabled: false },
      { ...row, class_name: "UnityContainerWndClass" }, { ...row, window_title: "scene backup detected" },
      { ...row, inspection_complete: false }, unobservedInspection,
      { ...row, hwnd: "0x0", child_controls: row.child_controls.map((control) => ({ ...control, parent_hwnd: "0x0" })) },
      { ...row, child_controls: [icon, body, { ...keep, hwnd: "0x0" }, remove] },
      { ...row, child_texts: ["Keep Backups", "Delete"], child_controls: [keep, remove] },
      { ...row, child_texts: [driftBody, "Keep Backups", "Delete"], child_controls: [icon, { ...body, text: driftBody }, keep, remove] },
      { ...row, child_controls: [icon, { ...body, class_name: "Static" }, keep, remove] },
      { ...row, child_controls: [icon, { ...body, class_name: "CustomMessage" }, keep, remove] },
      { ...row, child_controls: [icon, { ...body, read_only: false }, keep, remove] },
      { ...row, child_controls: [icon, { ...body, multiline: false }, keep, remove] },
      { ...row, child_controls: [icon, { ...body, control_id: 1018 }, keep, remove] },
      { ...row, child_controls: [icon, body, { ...keep, control_id: 2 }, { ...remove, control_id: 1 }] },
      { ...row, child_controls: [icon, body, { ...keep, class_name: "CustomButton" }, remove] },
      { ...row, child_controls: [icon, body, { ...keep, enabled: false }, remove] },
      { ...row, child_controls: [icon, body, { ...keep, visible: false }, remove] },
      { ...row, child_controls: [icon, body, unobservedVisible, remove] },
      { ...row, child_controls: [icon, body, unobservedEnabled, remove] },
      { ...row, child_controls: [icon, body, { ...keep, pid: row.pid + 1 }, remove] },
      { ...row, child_controls: [icon, body, { ...keep, window_thread_id: row.window_thread_id + 1 }, remove] },
      { ...row, child_controls: [icon, body, { ...keep, parent_hwnd: "0x999999" }, remove] },
      { ...row, child_controls: [icon, body, { ...keep, hwnd: remove!.hwnd }, remove] },
      { ...row, child_controls: [icon, body, keep, remove, { ...keep, hwnd: "0x219513", visible: false }] },
      { ...row, child_texts: [...row.child_texts, "Cancel"], child_controls: [...row.child_controls, { ...keep, hwnd: "0x219513", text: "Cancel" }] },
      { ...row, child_controls: [{ ...icon, text: "Unexpected" }, body, keep, remove] },
    ];
    for (const [index, variant] of variants.entries()) {
      const status = sceneBackupDetectedStatus([variant]);
      expect(status.scene_backup_recovery_modal_candidates_count, `variant ${index}`).toBe(0);
      if ((variant as { process_name: string }).process_name === "Unity") expect(status.aggregate_scan_authoritative, `variant ${index}`).toBe(false);
    }
  });

  it("does not offer legacy Yes/No or generic recovery responses for Scene Backup Detected", () => {
    const row = sceneBackupDetectedModalWindow();
    const legacy = sceneBackupRecoveryModalWindow();
    const legacyStatus = sceneBackupDetectedStatus([legacy]);
    expect(legacyStatus.scene_backup_recovery_modal_candidates![0]).toMatchObject({ scene_backup_recovery_variant: "recovering_scene_backups", scene_backup_recovery: { yes_button_count: 1, no_button_count: 1 } });
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => legacyStatus });
    expect(runtime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "refused", native_dispatch_count: 0 });
    const calls: Array<{ file: string; args: string[] }> = [];
    const result = respondToUnityRecoveryPrompt({ action: "do_not_restore", pids: [row.pid], dry_run: false }, { execFileSync: fakeExecFileSync('"Unity.exe","21901","Console","1","200 K","Running","user","0:00:01","Scene Backup Detected"', calls, JSON.stringify([row])) });
    expect(result).toMatchObject({ clicked_count: 0, target_pids: [] });
    expect(calls.some((call) => call.args.join(" ").includes("$targetPids"))).toBe(false);
  });

  it("issues a default-dry-run random single-use process/root-bound Keep Backups plan and submits exactly once", () => {
    const status = sceneBackupDetectedStatus();
    let dispatches = 0;
    let bound: UnitySceneBackupKeepEvidence | undefined;
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => status, now: () => new Date("2026-10-07T06:00:00.000Z"), keepSceneBackups: (evidence) => {
      dispatches += 1; bound = evidence;
      return { status: "message_submitted", interaction_attempted: true, delivery_state: "message_submitted" };
    } });
    const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
    const secondPlan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
    expect(plan).toMatchObject({ action: "keep_scene_backups", dry_run: true, status: "ready", interaction_attempted: false, message_submitted: false, native_dispatch_count: 0, expires_at: "2026-10-07T06:01:00.000Z" });
    expect(plan.precondition_hash).toMatch(/^sha256:[a-f0-9]{64}$/);
    expect(secondPlan.precondition_hash).not.toBe(plan.precondition_hash);
    expect(JSON.stringify(plan)).not.toMatch(/0x2195|c:\/test|Unity attempts/);
    const args = { ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! };
    const result = runtime.keepSceneBackups!(args);
    expect(result).toMatchObject({ action: "keep_scene_backups", status: "message_submitted", native_dispatch_count: 1, interaction_attempted: true, message_submitted: true, popup_closed_verified: false, backups_kept_verified: false });
    expect(bound?.buttons).toEqual({ count: 2, keep_backups_count: 1, delete_count: 1, keep_backups_hwnd: "0x219511", delete_hwnd: "0x219512" });
    expect(runtime.keepSceneBackups!(args)).toMatchObject({ status: "refused", native_dispatch_count: 0 });
    expect(runtime.keepSceneBackups!({ ...args, precondition_hash: secondPlan.precondition_hash! })).toMatchObject({ status: "refused", native_dispatch_count: 0 });
    expect(runtime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "refused", error: "scene_backup_keep_delivery_already_attempted_for_popup" });
    expect(dispatches).toBe(1);
  });

  it("uses authoritative exact-target User32 evidence while unrelated unknown dialogs remain non-authoritative", () => {
    const foreign = sceneBackupDetectedModalWindow();
    foreign.pid += 1; foreign.hwnd = "0x999999"; foreign.window_title = "Unclassified foreign prompt";
    const status = sceneBackupDetectedStatus([sceneBackupDetectedModalWindow(), foreign]);
    expect(status.aggregate_scan_authoritative).toBe(false);
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => status });
    expect(runtime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "ready", native_dispatch_count: 0 });
    for (const envelope of [
      { user32_scan_succeeded: false, uia_scan_succeeded: true, uia_scan_status: "succeeded" },
      { user32_candidate_count: 2 },
      { user32_scan_complete: undefined },
    ]) {
      const incomplete = sceneBackupDetectedStatus(undefined, envelope);
      const refusedRuntime = createUnityWindowDiagnosticsRuntime({ inspect: () => incomplete });
      expect(refusedRuntime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "refused", native_dispatch_count: 0 });
    }
  });

  it("refuses a complete Scene Backup target row when reported User32 coverage is explicitly false", () => {
    const status = sceneBackupDetectedStatus(undefined, { user32_scan_complete: false });
    let interactions = 0;
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => status, keepSceneBackups: () => {
      interactions += 1;
      return { status: "message_submitted", interaction_attempted: true, delivery_state: "message_submitted" };
    } });

    expect(status).toMatchObject({ native_modal_candidates_count: 1, native_modal_scan_authoritative: false, aggregate_scan_authoritative: false });
    const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
    expect(plan).toMatchObject({ status: "refused", error: "exact_scene_backup_detected_modal_not_found_ambiguous_or_non_authoritative", interaction_attempted: false, native_dispatch_count: 0 });
    expect(plan.precondition_hash).toBeUndefined();
    expect(interactions).toBe(0);
  });

  it.each(["unprojected_window", "projected_dialog"] as const)("consumes a Keep Backups plan without interaction after incomplete same-owner %s coverage", (kind) => {
    let status = sceneBackupDetectedStatus();
    let interactions = 0;
    let guards = 0;
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => status, keepSceneBackups: () => {
      interactions += 1;
      return { status: "message_submitted", interaction_attempted: true, delivery_state: "message_submitted" };
    } });
    const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
    expect(plan.status).toBe("ready");
    const complete = sceneBackupDetectedModalWindow();
    const incomplete = { ...complete, hwnd: "0x219600", window_title: "Unclassified same-owner dialog", child_texts: [], child_controls: [], inspection_complete: false };
    // A failed read on an unprojected same-owner main window appears only in
    // the helper's global completeness flag; no owner-scoped proof exists.
    status = sceneBackupDetectedStatus(kind === "unprojected_window" ? [complete] : [complete, incomplete], { user32_scan_complete: false });
    expect(status.native_modal_scan_authoritative).toBe(false);
    const args = { ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! };
    expect(runtime.keepSceneBackups!(args, () => { guards += 1; })).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0 });
    status = sceneBackupDetectedStatus();
    expect(runtime.keepSceneBackups!(args)).toMatchObject({ status: "refused", error: "scene_backup_keep_plan_not_issued_expired_or_consumed", native_dispatch_count: 0 });
    expect(interactions).toBe(0);
    expect(guards).toBe(0);
  });

  it("refuses ambiguous same-generation dialogs, foreign PID, and stale process generation for Keep Backups", () => {
    const duplicate = sceneBackupDetectedModalWindow();
    duplicate.hwnd = "0x219600";
    duplicate.child_controls = duplicate.child_controls.map((control, index) => ({ ...control, hwnd: `0x21961${index}`, parent_hwnd: duplicate.hwnd }));
    for (const [status, args] of [
      [sceneBackupDetectedStatus([sceneBackupDetectedModalWindow(), duplicate]), sceneBackupKeepArgs()],
      [sceneBackupDetectedStatus([sceneBackupDetectedModalWindow(), { ...duplicate, process_start_utc: "2026-10-07T05:01:00.0000000Z" }]), sceneBackupKeepArgs()],
      [sceneBackupDetectedStatus(), { ...sceneBackupKeepArgs(), pid: 21902 }],
      [sceneBackupDetectedStatus(), { ...sceneBackupKeepArgs(), process_start_utc: "2026-10-06T05:00:00.0000000Z" }],
    ] as const) {
      const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => status });
      expect(runtime.keepSceneBackups!(args)).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0 });
    }
  });

  it("expires Keep Backups plans at the 60-second boundary and consumes changed root/PID/start bindings", () => {
    for (const change of ["expiry", "root", "pid", "start", "confirmation"] as const) {
      let now = new Date("2026-10-07T06:00:00.000Z");
      let dispatches = 0;
      const runtime = createUnityWindowDiagnosticsRuntime({ now: () => now, inspect: () => sceneBackupDetectedStatus(), keepSceneBackups: () => { dispatches += 1; return { status: "message_submitted", interaction_attempted: true, delivery_state: "message_submitted" }; } });
      const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
      const args: UnitySceneBackupKeepArgs = { ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! };
      if (change === "expiry") now = new Date("2026-10-07T06:01:00.000Z");
      if (change === "root") args.canonical_project_root = "c:/test/foreign";
      if (change === "pid") args.pid = 21902;
      if (change === "start") args.process_start_utc = "2026-10-06T05:00:00.0000000Z";
      if (change === "confirmation") args.confirm_keep_backups = false;
      expect(runtime.keepSceneBackups!(args), change).toMatchObject({ status: "refused", native_dispatch_count: 0 });
      expect(runtime.keepSceneBackups!({ ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! }), change).toMatchObject({ status: "refused", native_dispatch_count: 0 });
      expect(dispatches, change).toBe(0);
    }
  });

  it("revalidates the full body and native identities before Keep Backups submission", () => {
    for (const drift of ["body_layout", "button", "generation", "thread", "icon", "incomplete"] as const) {
      let row = sceneBackupDetectedModalWindow();
      let dispatches = 0;
      const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => sceneBackupDetectedStatus([row]), keepSceneBackups: () => { dispatches += 1; return { status: "message_submitted", interaction_attempted: true, delivery_state: "message_submitted" }; } });
      const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
      if (drift === "body_layout") { row.child_texts[0] += "\n"; row.child_controls[1]!.text += "\n"; }
      if (drift === "button") row.child_controls[2]!.hwnd = "0x219599";
      if (drift === "generation") row.process_start_utc = "2026-10-07T05:01:00.0000000Z";
      if (drift === "thread") { row.window_thread_id += 1; row.child_controls.forEach((control) => control.window_thread_id += 1); }
      if (drift === "icon") row.child_controls.shift();
      if (drift === "incomplete") row.inspection_complete = false;
      expect(runtime.keepSceneBackups!({ ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! }), drift).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0 });
      expect(dispatches, drift).toBe(0);
    }
  });

  it("suppresses fresh Keep Backups plans after uncertain helper attempts even if body layout changes", () => {
    for (const uncertain of ["timeout", "exception"] as const) {
      const row = sceneBackupDetectedModalWindow();
      let dispatches = 0;
      const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => sceneBackupDetectedStatus([row]), keepSceneBackups: () => {
        dispatches += 1;
        if (uncertain === "exception") throw new Error("unknown after native send");
        return { status: "error", interaction_attempted: true, delivery_state: "unknown_after_send_attempt", error: "bm_click_failed_or_timed_out" };
      } });
      const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
      const result = runtime.keepSceneBackups!({ ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! });
      expect(result).toMatchObject({ status: "error", native_dispatch_count: 1, message_submitted: false, popup_closed_verified: false, backups_kept_verified: false });
      row.child_texts[0] += "\n"; row.child_controls[1]!.text += "\n";
      expect(runtime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "refused", error: "scene_backup_keep_delivery_already_attempted_for_popup" });
      expect(dispatches).toBe(1);
    }
  });

  it("refuses Keep Backups invalid input and fabricated preconditions before any native scan", () => {
    let inspections = 0;
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => { inspections += 1; return sceneBackupDetectedStatus(); } });
    for (const args of [
      {}, { ...sceneBackupKeepArgs(), pid: 0 }, { ...sceneBackupKeepArgs(), pid: 1.5 },
      { ...sceneBackupKeepArgs(), process_start_utc: "unknown" }, { ...sceneBackupKeepArgs(), canonical_project_root: "" },
      { ...sceneBackupKeepArgs(), canonical_project_root: " c:/test/scene-backup" },
      { ...sceneBackupKeepArgs(), canonical_project_root: "c:/test/\0backup" },
      { ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: `sha256:${"1".repeat(64)}` },
    ]) expect(runtime.keepSceneBackups!(args)).toMatchObject({ status: "refused", native_dispatch_count: 0, interaction_attempted: false });
    expect(inspections).toBe(0);
  });

  it("rechecks Keep Backups plan expiry after a delayed fresh rescan", () => {
    let now = new Date("2026-10-07T06:00:00.000Z");
    let delayed = false;
    const runtime = createUnityWindowDiagnosticsRuntime({ now: () => now, inspect: () => { if (delayed) now = new Date("2026-10-07T06:01:00.000Z"); return sceneBackupDetectedStatus(); } });
    const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
    delayed = true;
    expect(runtime.keepSceneBackups!({ ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! })).toMatchObject({ status: "refused", native_dispatch_count: 0, error: "scene_backup_keep_plan_not_issued_expired_or_consumed" });
  });

  it("bounds Keep Backups plan storage and never clears uncertain attempts from a partial scan", () => {
    let status = sceneBackupDetectedStatus();
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => status, keepSceneBackups: () => ({ status: "error", interaction_attempted: "unknown", delivery_state: "unknown" }) });
    const first = runtime.keepSceneBackups!(sceneBackupKeepArgs());
    for (let index = 1; index < 64; index += 1) expect(runtime.keepSceneBackups!(sceneBackupKeepArgs()).status).toBe("ready");
    expect(runtime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "refused", error: "scene_backup_keep_plan_capacity_reached" });
    expect(runtime.keepSceneBackups!({ ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: first.precondition_hash! })).toMatchObject({ status: "error", native_dispatch_count: 1 });
    status = sceneBackupDetectedStatus([], { user32_scan_complete: false });
    runtime.status();
    status = sceneBackupDetectedStatus();
    expect(runtime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "refused", error: "scene_backup_keep_delivery_already_attempted_for_popup" });
  });

  it("generates only a bounded Unicode body reader and one exact Keep Backups BM_CLICK with no Delete/focus/fallback", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => sceneBackupDetectedStatus(), execFileSync: fakeExecFileSync("", calls, '{"status":"message_submitted"}') });
    const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
    expect(runtime.keepSceneBackups!({ ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! })).toMatchObject({ status: "message_submitted", native_dispatch_count: 1 });
    expect(calls).toHaveLength(1);
    const script = calls[0]!.args.at(-1)!;
    expect(script).toContain('EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode');
    expect(script).toContain("SendMessageTimeoutText($handle,0x000D");
    expect(script).toContain("0x0002,100,[ref]$r");
    expect(script).toContain("$script:textReadsComplete=$false");
    expect(script).toContain("control_id -eq 1019");
    expect(script).toContain("GetAncestor($child,2)");
    expect(script).toContain("SendMessageTimeout($keep[0].handle,0x00F5");
    expect(script.match(/0x00F5/g)).toHaveLength(1);
    expect(script).not.toMatch(/GetWindowText|SetForeground|SetFocus|InvokePattern|SendKeys|PostMessage|0x0010|SendMessageTimeout\(\$delete/);
  });

  it("records actual Keep Backups native timeout or malformed helper output as unknown and never retries it", () => {
    for (const response of ['{"status":"error","error":"bm_click_failed_or_timed_out"}', "malformed", "helper_timeout"] as const) {
      let calls = 0;
      const runtime = createUnityWindowDiagnosticsRuntime({ inspect: () => sceneBackupDetectedStatus(), execFileSync: () => {
        calls += 1;
        if (response === "helper_timeout") { const error = new Error("timeout"); Object.assign(error, { code: "ETIMEDOUT", killed: true }); throw error; }
        return response;
      } });
      const plan = runtime.keepSceneBackups!(sceneBackupKeepArgs());
      expect(runtime.keepSceneBackups!({ ...sceneBackupKeepArgs(), dry_run: false, confirm_keep_backups: true, precondition_hash: plan.precondition_hash! })).toMatchObject({ status: "error", native_dispatch_count: 1, message_submitted: false });
      expect(runtime.keepSceneBackups!(sceneBackupKeepArgs())).toMatchObject({ status: "refused", error: "scene_backup_keep_delivery_already_attempted_for_popup" });
      expect(calls).toBe(1);
    }
  });

  it("detects the exact read-only low-disk dialog with bounded hash evidence", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const row = lowDiskSpaceModalWindow();
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", calls, JSON.stringify([row])) });

    expect(status).toMatchObject({
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_low_disk_space",
      low_disk_space_modal_candidates_count: 1,
      low_disk_space_modal_candidates: [{
        hwnd: "0x5A0500",
        pid: 6388,
        process_name: "Unity",
        process_start_utc: "2026-08-28T20:15:25.1396244Z",
        window_thread_id: 1920,
        window_visible: true,
        window_enabled: true,
        class_name: "#32770",
        window_title: "Low disk space",
        child_texts: ["OK"],
        likely_low_disk_space_prompt: true,
        low_disk_space: {
          title_sha256: "sha256:5b1f5bf00209595444f2fd408616c7d1baeb5b6303705720c2a491332e2a828a",
          body_sha256: "sha256:8b277692d1bf9efb880dc4524c1a4dc4fdfbba5e4500f7bb06607320c58021c3",
          body_evidence: { disk_space_below_one_gb: true, free_space_requested: true, unity_work_impacted: true },
          visible_control_count: 3,
          static_control_count: 1,
          edit_control_count: 1,
          read_only_multiline_edit_count: 1,
          button_count: 1,
          ok_button_count: 1,
          diagnostic_only: true,
          interaction_supported: false,
          disk_cleanup_supported: false,
        },
        block_code: "blocked_low_disk_space",
      }],
    });
    expect(status.next_valid_actions).toEqual(expect.arrayContaining([
      "operator_review_low_disk_space_dialog",
      "do_not_click_focus_or_close_low_disk_dialog_from_gateway",
      "do_not_free_disk_or_delete_library_or_cache_from_gateway",
      "do_not_terminate_or_restart_unity",
      "do_not_repeat_blocked_test_compile_or_package_operation",
    ]));
    expect(status.next_valid_actions).not.toContain("retry_after_modal_closed");
    const serialized = JSON.stringify(status);
    expect(serialized).not.toContain("The project is on a drive with diskspace");
    expect(serialized).not.toContain("0x5A0510");
    expect(serialized).not.toContain("0x5A0511");

    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("^Low disk space");
    expect(scannerScript).toContain("GetDlgCtrlID");
    expect(scannerScript).toContain("read_only =");
    expect(scannerScript).toContain("multiline =");
    expect(scannerScript).not.toContain("$dialogName -cne 'Low disk space'");
    expect(scannerScript).toContain("SendMessageTimeoutText($handle, 0x000D");
    expect(scannerScript).not.toContain("0x00F5");
    expect(scannerScript).not.toContain("InvokePattern");
  });

  it("detects the exact sparse low-disk dialog without inventing body evidence", () => {
    const complete = lowDiskSpaceModalWindow();
    const [staticControl, , okButton] = complete.child_controls;
    const sparse = { ...complete, child_texts: ["OK"], child_controls: [staticControl!, okButton!] };

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([sparse])) });

    expect(status).toMatchObject({
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_low_disk_space",
      low_disk_space_modal_candidates_count: 1,
      low_disk_space_modal_candidates: [{
        hwnd: "0x5A0500",
        pid: 6388,
        process_name: "Unity",
        process_start_utc: "2026-08-28T20:15:25.1396244Z",
        window_thread_id: 1920,
        window_visible: true,
        window_enabled: true,
        class_name: "#32770",
        window_title: "Low disk space",
        child_texts: ["OK"],
        likely_low_disk_space_prompt: true,
        block_code: "blocked_low_disk_space",
        matched_keywords: ["low_disk_space", "diagnostic_only", "no_gateway_cleanup"],
      }],
    });
    expect(status.low_disk_space_modal_candidates?.[0]).not.toHaveProperty("low_disk_space");
    expect(status.next_valid_actions).toEqual(expect.arrayContaining([
      "operator_review_low_disk_space_dialog",
      "do_not_click_focus_or_close_low_disk_dialog_from_gateway",
      "do_not_free_disk_or_delete_library_or_cache_from_gateway",
      "do_not_terminate_or_restart_unity",
      "do_not_repeat_blocked_test_compile_or_package_operation",
    ]));
    expect(status.next_valid_actions).not.toContain("retry_after_modal_closed");
    const serialized = JSON.stringify(status);
    expect(serialized).not.toContain("body_sha256");
    expect(serialized).not.toContain("body_evidence");
    expect(serialized).not.toContain("0x5A0510");
    expect(serialized).not.toContain("0x5A0512");
  });

  it("rejects low-disk process, topology, body, order, and control drift", () => {
    const complete = lowDiskSpaceModalWindow();
    const [staticControl, editControl, okButton] = complete.child_controls;
    const body = complete.child_texts[0]!;
    const sparse = { ...complete, child_texts: ["OK"], child_controls: [staticControl!, okButton!] };
    const { visible: _staticVisible, ...staticWithoutVisible } = staticControl!;
    const { enabled: _okEnabled, ...okWithoutEnabled } = okButton!;
    const variants = [
      { ...complete, process_name: "Other" },
      { ...complete, process_start_utc: "" },
      { ...complete, window_thread_id: 0 },
      { ...complete, window_visible: false },
      { ...complete, window_enabled: false },
      { ...complete, class_name: "UnityContainerWndClass" },
      { ...complete, window_title: "Low Disk Space" },
      { ...complete, child_texts: [body.replace("1.00 GB", "2.00 GB"), "OK"] },
      { ...complete, child_controls: [{ ...staticControl!, control_id: 1041 }, editControl!, okButton!] },
      { ...complete, child_controls: [staticControl!, { ...editControl!, read_only: false }, okButton!] },
      { ...complete, child_controls: [staticControl!, { ...editControl!, multiline: false }, okButton!] },
      { ...complete, child_controls: [staticControl!, editControl!, { ...okButton!, enabled: false }] },
      { ...complete, child_controls: [staticControl!, { ...editControl!, pid: 6389 }, okButton!] },
      { ...complete, child_controls: [staticControl!, { ...editControl!, window_thread_id: 1921 }, okButton!] },
      { ...complete, child_controls: [staticControl!, { ...editControl!, parent_hwnd: "0x999999" }, okButton!] },
      { ...complete, child_controls: [editControl!, staticControl!, okButton!] },
      { ...complete, child_controls: [staticControl!, editControl!, okButton!, { ...okButton!, hwnd: "0x5A0512" }] },
      { ...sparse, process_name: "Other" },
      { ...sparse, process_start_utc: "" },
      { ...sparse, window_thread_id: 0 },
      { ...sparse, window_visible: false },
      { ...sparse, window_enabled: false },
      { ...sparse, class_name: "UnityContainerWndClass" },
      { ...sparse, window_title: "Low Disk Space" },
      { ...sparse, child_texts: [" OK "] },
      { ...sparse, child_texts: ["Low disk space", "OK"] },
      { ...sparse, child_controls: [{ ...staticControl!, control_id: 1041 }, okButton!] },
      { ...sparse, child_controls: [{ ...staticControl!, class_name: "Edit" }, okButton!] },
      { ...sparse, child_controls: [{ ...staticControl!, text: "Low disk space" }, okButton!] },
      { ...sparse, child_controls: [{ ...staticControl!, visible: false }, okButton!] },
      { ...sparse, child_controls: [staticWithoutVisible, okButton!] },
      { ...sparse, child_controls: [{ ...staticControl!, parent_hwnd: "0x999999" }, okButton!] },
      { ...sparse, child_controls: [{ ...staticControl!, window_thread_id: 1921 }, okButton!] },
      { ...sparse, child_controls: [okButton!, staticControl!] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, class_name: "Static" }] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, control_id: 2 }] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, text: " OK " }] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, enabled: false }] },
      { ...sparse, child_controls: [staticControl!, okWithoutEnabled] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, visible: false }] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, pid: 6389 }] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, window_thread_id: 1921 }] },
      { ...sparse, child_controls: [staticControl!, { ...okButton!, parent_hwnd: "0x999999" }] },
      { ...sparse, child_controls: [staticControl!, okButton!, { ...okButton!, hwnd: "0x5A0512" }] },
      { ...sparse, child_controls: [staticControl!, okButton!, { ...okButton!, hwnd: "0x5A0512", visible: false }] },
    ];

    for (const [index, variant] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([variant])) });
      expect(status.low_disk_space_modal_candidates_count, `variant ${index}`).toBe(0);
      expect(status.blocking_modal_code, `variant ${index}`).not.toBe("blocked_low_disk_space");
    }
  });

  it("detects Enter Safe Mode native modal dialogs without treating them as recovery prompts", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const body = "The project you are opening contains compilation errors. Entering Safe Mode allows you to fix these errors without waiting for all of your project's assets to be imported. If you choose Ignore, your project will continue to import, but it may be in a broken or unusable state.";
    const nativeModalOutput = JSON.stringify({
      rows: [{
        hwnd: "0x300304",
        pid: 24681,
        process_name: "Unity",
        process_start_utc: "2026-08-23T19:35:00.000Z",
        window_thread_id: 1400,
        class_name: "#32770",
        window_title: "Enter Safe Mode?",
        child_texts: [body, "Enter Safe Mode", "Ignore", "Quit"],
        child_controls: [
          { hwnd: "0x300310", pid: 24681, window_thread_id: 1400, visible: true, enabled: true, class_name: "Static", text: body },
          { hwnd: "0x300311", pid: 24681, window_thread_id: 1400, visible: true, enabled: true, class_name: "Button", text: "Enter Safe Mode" },
          { hwnd: "0x300312", pid: 24681, window_thread_id: 1400, visible: true, enabled: true, class_name: "Button", text: "Ignore" },
          { hwnd: "0x300313", pid: 24681, window_thread_id: 1400, visible: true, enabled: true, class_name: "Button", text: "Quit" },
        ],
      }],
      user32_scan_succeeded: false,
      uia_scan_succeeded: true,
    });
    const execFileSync = fakeExecFileSync('"Unity.exe","24681","Console","1","200 K","Running","user","0:01:00","Enter Safe Mode?"', calls, nativeModalOutput);

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status).toMatchObject({
      supported: true,
      native_modal_scan_authoritative: false,
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_safe_mode_prompt",
      safe_mode_modal_candidates_count: 1,
      recovery_prompt_candidates_count: 0,
      safe_mode_modal_candidates: [
        {
          hwnd: "0x300304",
          pid: 24681,
          process_start_utc: "2026-08-23T19:35:00.000Z",
          window_thread_id: 1400,
          window_title: "Enter Safe Mode?",
          block_code: "blocked_safe_mode_prompt",
          likely_safe_mode_prompt: true,
        },
      ],
    });
    expect(status.next_valid_actions).toContain("dry_run_diagnostics.unity_safe_mode.enter");
    expect(status.next_valid_actions).toContain("do_not_choose_ignore_or_quit_from_gateway");
    expect(status.next_valid_actions).toContain("do_not_auto_click_safe_mode_prompt");
    expect(status.errors).toContain("native_modal_scan_partial:user32_failed_uia_fallback");
    expect(status.recovery_prompt_candidates).toEqual([]);
    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("Get-Process -Name Unity");
    expect(scannerScript).toContain("if (-not $user32ScanSucceeded)");
    expect(scannerScript).toContain("$results.Clear()");
    expect(scannerScript).toContain("uia_scan_status = $uiAutomationScanStatus");
    expect(scannerScript).toContain("$dialogName -cne 'Enter Safe Mode?'");
    expect(scannerScript).toContain("user32_scan_succeeded");
    expect(scannerScript).toContain("rows = $results.ToArray()");
    expect(scannerScript).toContain("child_texts = $childTexts.ToArray()");
    expect(scannerScript).toContain("child_controls = $childControls.ToArray()");
    expect(scannerScript).toContain("if (-not $enumWindowsSucceeded) { throw 'enum_windows_failed' }");
    expect(scannerScript).toContain("[void][SGGUnityNativeModalUser32]::EnumChildWindows");
    expect(scannerScript).not.toContain("$childrenSucceeded");
    expect(scannerScript).not.toContain("childEnumerationSucceeded");
    expect(scannerScript.indexOf("$replacement = [pscustomobject]")).toBeLessThan(scannerScript.indexOf("$results.Remove($existing)"));
    expect(scannerScript).not.toContain("InvokePattern");
    expect(scannerScript).toContain("SendMessageTimeoutText($handle, 0x000D");
    expect(scannerScript).not.toContain("0x00F5");
  });

  it("detects the captured sparse Safe Mode control topology", () => {
    const hwnd = "0x130E50";
    const sparse = {
      hwnd,
      pid: 8300,
      process_name: "Unity",
      process_start_utc: "2026-09-08T11:07:48.3912806Z",
      window_thread_id: 49076,
      window_visible: true,
      window_enabled: true,
      class_name: "#32770",
      window_title: "Enter Safe Mode?",
      child_texts: ["Enter Safe Mode", "Ignore", "Quit"],
      child_controls: ["Enter Safe Mode", "Ignore", "Quit"].map((text, index) => ({
        hwnd: `0x11D20AC${index}`,
        parent_hwnd: hwnd,
        pid: 8300,
        window_thread_id: 49076,
        visible: true,
        enabled: true,
        class_name: "Button",
        text,
      })),
    };

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([sparse])) });

    expect(status).toMatchObject({
      safe_mode_modal_candidates_count: 1,
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_safe_mode_prompt",
      safe_mode_modal_candidates: [{
        pid: 8300,
        process_start_utc: "2026-09-08T11:07:48.3912806Z",
        window_thread_id: 49076,
        window_visible: true,
        window_enabled: true,
        child_texts: ["Enter Safe Mode", "Ignore", "Quit"],
        likely_safe_mode_prompt: true,
        block_code: "blocked_safe_mode_prompt",
        safe_mode: {
          evidence_scope: "sparse_control_topology",
          visible_control_count: 3,
          button_count: 3,
          enter_safe_mode_button_count: 1,
          ignore_button_count: 1,
          quit_button_count: 1,
        },
      }],
    });
  });

  it("issues and consumes one exact Safe Mode Enter precondition", () => {
    const hwnd = "0x130E50";
    const processStartUtc = "2026-09-08T11:07:48.3912806Z";
    const row = {
      hwnd,
      pid: 8300,
      process_name: "Unity",
      process_start_utc: processStartUtc,
      window_thread_id: 49076,
      window_visible: true,
      window_enabled: true,
      class_name: "#32770",
      window_title: "Enter Safe Mode?",
      child_texts: ["Enter Safe Mode", "Ignore", "Quit"],
      child_controls: ["Enter Safe Mode", "Ignore", "Quit"].map((text, index) => ({
        hwnd: `0x11D20AC${index}`,
        parent_hwnd: hwnd,
        pid: 8300,
        window_thread_id: 49076,
        visible: true,
        enabled: true,
        control_id: 100 + index,
        class_name: "Button",
        text,
      })),
    };
    let status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([row])) });
    let dispatchCount = 0;
    const now = new Date("2026-09-10T10:00:00.000Z");
    const runtime = createUnityWindowDiagnosticsRuntime({
      inspect: () => status,
      now: () => now,
      enterSafeMode: () => {
        dispatchCount += 1;
        return { status: "message_submitted", interaction_attempted: true, delivery_state: "message_submitted" };
      },
    });
    const projectHash = `sha256:${createHash("sha256").update("c:/u/game").digest("hex")}`;
    const baseArgs = { pid: 8300, process_start_utc: processStartUtc, project_identity_sha256: projectHash };
    const plan = runtime.planSafeModeEnter!(baseArgs);

    expect(plan).toMatchObject({
      action: "enter_safe_mode",
      dry_run: true,
      status: "ready",
      interaction_attempted: false,
      native_dispatch_count: 0,
      safe_mode_entered_verified: false,
      binding: {
        process_generation_verified: true,
        evidence_scope: "sparse_control_topology",
        button_count: 3,
        enter_safe_mode_button_count: 1,
        ignore_button_count: 1,
        quit_button_count: 1,
      },
    });
    const executeArgs = { ...baseArgs, dry_run: false, confirm_enter_safe_mode: true, precondition_hash: plan.precondition_hash! };
    const executed = runtime.enterSafeMode!(executeArgs);
    const replay = runtime.enterSafeMode!(executeArgs);

    expect(executed).toMatchObject({
      status: "message_submitted",
      interaction_attempted: true,
      native_dispatch_count: 1,
      delivery_state: "message_submitted",
      safe_mode_entered_verified: false,
      popup_closed_verified: false,
      binding: { process_generation_verified: true, button_count: 3, enter_safe_mode_button_count: 1, ignore_button_count: 1, quit_button_count: 1 },
    });
    expect(replay).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "safe_mode_enter_plan_not_issued_expired_or_consumed" });
    expect(runtime.planSafeModeEnter!(baseArgs)).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "safe_mode_enter_delivery_already_attempted_for_popup" });
    expect(dispatchCount).toBe(1);

    let uncertainDispatchCount = 0;
    const uncertainRuntime = createUnityWindowDiagnosticsRuntime({
      inspect: () => status,
      now: () => now,
      enterSafeMode: () => {
        uncertainDispatchCount += 1;
        return { status: "error", interaction_attempted: true, delivery_state: "unknown_after_send_attempt", error: "bm_click_failed_or_timed_out" };
      },
    });
    const uncertainPlan = uncertainRuntime.planSafeModeEnter!(baseArgs);
    const secondUncertainPlan = uncertainRuntime.planSafeModeEnter!(baseArgs);
    const uncertainArgs = { ...executeArgs, precondition_hash: uncertainPlan.precondition_hash! };
    const uncertain = uncertainRuntime.enterSafeMode!(uncertainArgs);
    const uncertainReplay = uncertainRuntime.enterSafeMode!({ ...uncertainArgs, precondition_hash: secondUncertainPlan.precondition_hash! });
    const uncertainFreshPlan = uncertainRuntime.planSafeModeEnter!(baseArgs);
    expect(uncertain).toMatchObject({ status: "error", interaction_attempted: true, native_dispatch_count: 1, delivery_state: "unknown_after_send_attempt", error: "bm_click_failed_or_timed_out" });
    expect(uncertainReplay).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "safe_mode_enter_plan_not_issued_expired_or_consumed" });
    expect(uncertainFreshPlan).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "safe_mode_enter_delivery_already_attempted_for_popup" });
    expect(uncertainDispatchCount).toBe(1);

    const originalSafeModeCandidate = status.safe_mode_modal_candidates[0]!;
    const secondSafeModeCandidate = structuredClone(originalSafeModeCandidate);
    secondSafeModeCandidate.hwnd = "0x130E51";
    secondSafeModeCandidate.safe_mode!.control_topology_sha256 = `sha256:${"2".repeat(64)}`;
    secondSafeModeCandidate.safe_mode!.enter_safe_mode_button_hwnd = "0x21D20AC0";
    secondSafeModeCandidate.safe_mode!.ignore_button_hwnd = "0x21D20AC1";
    secondSafeModeCandidate.safe_mode!.quit_button_hwnd = "0x21D20AC2";
    status = { ...status, safe_mode_modal_candidates: [originalSafeModeCandidate, secondSafeModeCandidate], safe_mode_modal_candidates_count: 2 };
    expect(uncertainRuntime.planSafeModeEnter!(baseArgs)).toMatchObject({ status: "refused", error: "exact_safe_mode_prompt_not_found_or_ambiguous" });
    status = { ...status, safe_mode_modal_candidates: [originalSafeModeCandidate], safe_mode_modal_candidates_count: 1 };
    expect(uncertainRuntime.planSafeModeEnter!(baseArgs)).toMatchObject({ status: "refused", error: "safe_mode_enter_delivery_already_attempted_for_popup" });

    const expiryRuntime = createUnityWindowDiagnosticsRuntime({ inspect: () => status, now: () => now });
    const expiredPlan = expiryRuntime.planSafeModeEnter!(baseArgs);
    now.setTime(now.getTime() + 60_001);
    const expired = expiryRuntime.enterSafeMode!({ ...executeArgs, precondition_hash: expiredPlan.precondition_hash! });
    expect(expired).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "safe_mode_enter_plan_not_issued_expired_or_consumed" });
    expect(dispatchCount).toBe(1);

    const driftRuntime = createUnityWindowDiagnosticsRuntime({ inspect: () => status, now: () => now });
    const driftPlan = driftRuntime.planSafeModeEnter!(baseArgs);
    status = { ...status, safe_mode_modal_candidates: [], safe_mode_modal_candidates_count: 0 };
    const drifted = driftRuntime.enterSafeMode!({ ...executeArgs, precondition_hash: driftPlan.precondition_hash! });
    expect(drifted).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "safe_mode_prompt_or_button_changed" });
    expect(dispatchCount).toBe(1);
  });

  it.each(responseReaderCases)("emits a bounded Unicode reader and sticky pre-click completeness guards for $kind", (testCase) => {
    const harness = createResponseReaderHarness(testCase.kind);
    const result = harness.apply();
    expect(result).toMatchObject({ interaction_attempted: true, delivery_state: testCase.kind === "project_downgrade_continue" || testCase.kind === "safe_mode_enter" ? "message_submitted" : "message_sent" });
    expect(harness.responseCalls).toHaveLength(1);
    const script = harness.responseCalls[0]!.args.at(-1)!;
    const compact = script.replace(/\s+/g, "");
    expect(script).toContain('EntryPoint="SendMessageTimeoutW", CharSet=CharSet.Unicode, SetLastError=true');
    expect(script).toContain("System.Text.StringBuilder 1024");
    expect(compact).toMatch(new RegExp(`\\[${testCase.nativeType}\\]::SendMessageTimeoutText\\(\\$handle,0x000D,\\[IntPtr\\]\\$(?:b|builder)\\.Capacity,\\$(?:b|builder),0x0002,100,\\[ref\\]\\$(?:r|messageResult)\\)`));
    expect(compact).toMatch(/if\(\$sent-eq\[IntPtr\]::Zero-or\$(?:r|messageResult)\.ToInt64\(\)-ge\(\$(?:b|builder)\.Capacity-1\)\)\{\$script:textReadsComplete=\$false;return''\}/);
    // Empty successful WM_GETTEXT (message result 0) must remain distinct from transport failure.
    expect(compact).not.toMatch(/\$(?:r|messageResult)\.ToInt64\(\)-eq0/);
    expect(compact.match(/\$script:textReadsComplete=\$true/g)).toHaveLength(1);
    expect(compact.match(/if\(-not\$script:textReadsComplete\)/g)).toHaveLength(2);
    expect(script).not.toContain("GetWindowText");
    expect(script).not.toContain("StringBuilder 2048");
    expect(script.match(/0x00F5/g)).toHaveLength(1);
    expect(compact).toContain(`SendMessageTimeout(${testCase.clickHandle},0x00F5,[IntPtr]::Zero,[IntPtr]::Zero,0x0002,1000,[ref]$sendResult)`);
    const initialGuard = script.indexOf("if (-not $script:textReadsComplete)");
    const finalRead = script.indexOf(testCase.finalRead);
    const finalGuard = script.indexOf("if (-not $script:textReadsComplete)", finalRead);
    const click = script.indexOf("0x00F5");
    expect(initialGuard).toBeGreaterThan(script.indexOf("::EnumChildWindows($targetHwnd"));
    expect(finalRead).toBeGreaterThan(initialGuard);
    expect(finalGuard).toBeGreaterThan(finalRead);
    expect(click).toBeGreaterThan(finalGuard);
    expect(script).not.toMatch(/SetForegroundWindow|SetFocus|SendKeys|InvokePattern|PostMessage|GetWindowText|0x0010/);
    expect(script).toContain("$processStartUtc -ceq");
    expect(script).toContain("$targetPid");
    if (testCase.kind === "project_downgrade_continue") {
      expect(script).toContain("(Get-Text $handle) -cne $expectedText");
      expect(script).toContain("Test-BoundButton $quit[0].handle");
      expect(script).toContain("$bodyHash -ceq ([string]$expected.body_sha256)");
      expect(result).toMatchObject({ status: "message_submitted", success: false, message_submitted: true, popup_closed_verified: false });
    } else if (testCase.kind === "safe_mode_enter") {
      expect(script).toContain("$buttonText -cne 'Enter Safe Mode'");
      expect(script).toContain("(Get-Sha256 $bodyText) -ceq ([string]$expected.body_sha256)");
      expect(result).toMatchObject({ status: "message_submitted", native_dispatch_count: 1, safe_mode_entered_verified: false, popup_closed_verified: false });
    } else if (testCase.kind === "scoped_registry_close") {
      expect(script).toContain("$buttonText.Trim() -cne 'Close'");
      expect(script).toContain("$bodyHash -ceq ([string]$expected.body_sha256)");
      expect(result).toMatchObject({ status: "message_sent", native_dispatch_count: 1, popup_state: "modal_still_present", popup_disappearance_verified: false, blocked_operation_replayed: false });
    } else {
      expect(script).toContain("$currentButtonText -cne $buttonText");
      expect(script).toContain("$sceneDifference.Count -ne 0");
      expect(initialGuard).toBeLessThan(script.indexOf("$sceneDifference = @(Compare-Object"));
      expect(script.slice(initialGuard, script.indexOf("$dialogText ="))).toContain("return");
      expect(result).toMatchObject({ action: testCase.kind, status: "message_sent", message_sent: true, popup_closed_verified: false, compile_state_verified: false });
    }
  });

  it.each(responseReaderCases)("preserves the not-attempted refusal shape and consumes the token after an incomplete $kind native reread", ({ kind }) => {
    // Mocked emitted-helper outcome: control-flow/reader assertions above cover failed or truncated reads.
    const harness = createResponseReaderHarness(kind, { status: "refused", error: "popup_text_read_incomplete", reload_count: 1, ignore_count: 1 });
    expect(harness.apply()).toMatchObject({ status: "refused", interaction_attempted: false, delivery_state: "not_attempted", error: "popup_text_read_incomplete" });
    expect(harness.apply()).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(harness.responseCalls).toHaveLength(1);
  });

  it.each(responseReaderCases)("preserves unknown native BM_CLICK timeout delivery and does not replay the same $kind token", ({ kind }) => {
    const harness = createResponseReaderHarness(kind, { status: "error", error: "bm_click_failed_or_timed_out" });
    expect(harness.apply()).toMatchObject({ status: "error", interaction_attempted: true, delivery_state: "unknown_after_send_attempt", error: "bm_click_failed_or_timed_out" });
    expect(harness.apply()).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(harness.responseCalls).toHaveLength(1);
  });

  it.each(responseReaderCases)("preserves stale process-generation refusal before the $kind helper is emitted", ({ kind }) => {
    const harness = createResponseReaderHarness(kind);
    harness.row.process_start_utc = "2026-09-11T10:00:00.0000000Z";
    expect(harness.apply()).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(harness.apply()).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(harness.responseCalls).toHaveLength(0);
  });

  it.each(["reload", "ignore"] as const)("preserves Unicode scene paths and exact approved %s binding in the emitted UTF-8 evidence", (action) => {
    const harness = createResponseReaderHarness(action);
    const scenePath = "Assets/Scenes/主菜单_Ёжик_Δοκιμή_🚀.unity";
    expect(harness.evidence).toMatchObject({ evidence_scope: "complete_body_and_scene_paths", scene_paths: [scenePath], scene_paths_count: 1 });
    expect(harness.apply()).toMatchObject({ action, status: "message_sent", message_sent: true, button_text: action === "reload" ? "Reload" : "Ignore" });
    const script = harness.responseCalls[0]!.args.at(-1)!;
    const encodedPaths = script.match(/\$expectedScenePaths = @\(ConvertFrom-Json .*FromBase64String\('([^']+)'\)/)?.[1];
    expect(encodedPaths).toBeDefined();
    expect(JSON.parse(Buffer.from(encodedPaths!, "base64").toString("utf8"))).toEqual([scenePath]);
    expect(script).toContain("[Text.Encoding]::UTF8.GetString");
    expect(script).toContain("Compare-Object -ReferenceObject $expectedSorted -DifferenceObject $currentScenePaths");
    expect(script).toContain(".Replace('\\', '/')");
  });

  it("keeps sparse Safe Mode's empty-body hash but refuses incomplete native text reads independently of that hash", () => {
    const harness = createResponseReaderHarness("safe_mode_enter", { status: "refused", error: "popup_text_read_incomplete" });
    expect(harness.evidence).toMatchObject({ binding: { evidence_scope: "sparse_control_topology", body_sha256: `sha256:${createHash("sha256").update("").digest("hex")}`, visible_control_count: 4 } });
    expect(harness.row.child_controls.some((control) => control.class_name === "Edit" && control.text === "")).toBe(true);
    expect(harness.apply()).toMatchObject({ status: "refused", interaction_attempted: false, native_dispatch_count: 0, error: "popup_text_read_incomplete" });
    const script = harness.responseCalls[0]!.args.at(-1)!;
    expect(script).toContain("$bodyText=@($controls | Where-Object { $_.class_name -cne 'Button' -and $_.text }");
    expect(script).toContain("(Get-Sha256 $bodyText) -ceq ([string]$expected.body_sha256)");
    expect(script.indexOf("if (-not $script:textReadsComplete)")).toBeLessThan(script.indexOf("elseif (-not $topologyValid)"));
    expect(script).not.toContain("$expected.evidence_scope -ceq");
    expect(harness.apply()).toMatchObject({ status: "refused", interaction_attempted: false });
    expect(harness.responseCalls).toHaveLength(1);
  });

  it("Safe Mode interaction source can submit only the exact Enter button once", () => {
    const source = fs.readFileSync(path.join(path.dirname(fileURLToPath(import.meta.url)), "..", "src", "diagnostics", "unity-window-diagnostics.ts"), "utf8");
    const start = source.indexOf("function safeModeEnterPowerShellScript");
    const end = source.indexOf("function parseSafeModeEnterInteraction", start);
    const script = source.slice(start, end);

    expect(script).toContain("$buttons.Count -eq 3");
    expect(script).toContain("$enter.Count -eq 1");
    expect(script).toContain("$ignore.Count -eq 1");
    expect(script).toContain("$quit.Count -eq 1");
    expect(script).toContain("SendMessageTimeoutText($handle,0x000D");
    expect(script).toContain("SendMessageTimeout($enter[0].handle,0x00F5");
    expect(script).not.toContain("SendMessageTimeout($ignore");
    expect(script).not.toContain("SendMessageTimeout($quit");
    expect(script).not.toContain("$targetAction");
  });

  it("rejects sparse Safe Mode topology and process-binding drift", () => {
    const hwnd = "0x130E50";
    const complete = {
      hwnd,
      pid: 8300,
      process_name: "Unity",
      process_start_utc: "2026-09-08T11:07:48.3912806Z",
      window_thread_id: 49076,
      window_visible: true,
      window_enabled: true,
      class_name: "#32770",
      window_title: "Enter Safe Mode?",
      child_texts: ["Enter Safe Mode", "Ignore", "Quit"],
      child_controls: ["Enter Safe Mode", "Ignore", "Quit"].map((text, index) => ({
        hwnd: `0x11D20AC${index}`,
        parent_hwnd: hwnd,
        pid: 8300,
        window_thread_id: 49076,
        visible: true,
        enabled: true,
        class_name: "Button",
        text,
      })),
    };
    const variants = [
      { ...complete, window_visible: false },
      { ...complete, window_enabled: false },
      { ...complete, child_texts: ["Ignore", "Enter Safe Mode", "Quit"] },
      { ...complete, child_texts: [...complete.child_texts, "Details"] },
      { ...complete, child_controls: complete.child_controls.slice(0, 2) },
      { ...complete, child_controls: complete.child_controls.map((control, index) => index === 0 ? { ...control, visible: false } : control) },
      { ...complete, child_controls: complete.child_controls.map((control, index) => index === 1 ? { ...control, enabled: false } : control) },
      { ...complete, child_controls: complete.child_controls.map((control, index) => index === 1 ? { ...control, parent_hwnd: "0x999999" } : control) },
      { ...complete, child_controls: complete.child_controls.map((control, index) => index === 1 ? { ...control, pid: 8301 } : control) },
      { ...complete, child_controls: complete.child_controls.map((control, index) => index === 1 ? { ...control, window_thread_id: 49077 } : control) },
      { ...complete, child_controls: complete.child_controls.map((control, index) => index === 2 ? { ...control, class_name: "Static" } : control) },
    ];

    for (const [index, variant] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([variant])) });
      expect(status.safe_mode_modal_candidates_count, `variant ${index}`).toBe(0);
      expect(status.blocking_modal_code, `variant ${index}`).not.toBe("blocked_safe_mode_prompt");
    }
  });

  it("captures Project Upgrade Required without inventing an exact classifier", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([{
      hwnd: "0x400500",
      pid: 24683,
      process_name: "Unity",
      process_start_utc: "2026-09-07T15:50:00.000Z",
      window_thread_id: 1401,
      window_visible: true,
      window_enabled: true,
      class_name: "#32770",
      window_title: "Project Upgrade Required",
      child_texts: ["Continue"],
      child_controls: [{ hwnd: "0x400501", parent_hwnd: "0x400500", pid: 24683, window_thread_id: 1401, visible: true, enabled: true, class_name: "Button", text: "Continue" }],
    }]);
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync('"Unity.exe","24683","Console","1","200 K","Running","user","0:01:00","Opening project..."', calls, nativeModalOutput) });

    expect(status).toMatchObject({
      native_modal_scan_authoritative: false,
      aggregate_scan_authoritative: false,
      native_modal_candidates_count: 1,
      blocking_startup_possible: false,
      blocking_automation_possible: false,
      native_modal_candidates: [{
        window_title: "<redacted>",
        child_texts: [],
        likely_modal: true,
        matched_keywords: ["project_upgrade"],
        unclassified_modal: { evidence_redacted: true, modal_like_reason: "dialog_class", inspection_complete: true },
      }],
    });
    expect(status.errors).toContain("native_modal_scan_partial:unclassified_unity_modal_candidate");
    expect(status.native_modal_candidates[0]).not.toHaveProperty("block_code");
    expect(JSON.stringify(status)).not.toContain("Project Upgrade Required");
    expect(JSON.stringify(status)).not.toContain("Continue");
    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("^Project Upgrade Required\\r?$");
    expect(scannerScript).toContain("$dialogName -cne 'Project Upgrade Required'");
  });

  it("rejects incomplete or non-bound Safe Mode lookalikes", () => {
    const body = "The project you are opening contains compilation errors. Entering Safe Mode allows errors to be fixed. If you choose Ignore, import continues.";
    const complete = {
      hwnd: "0x300304",
      pid: 24681,
      process_name: "Unity",
      process_start_utc: "2026-08-23T19:35:00.000Z",
      window_thread_id: 1400,
      class_name: "#32770",
      window_title: "Enter Safe Mode?",
      child_texts: [body, "Enter Safe Mode", "Ignore", "Quit"],
    };
    const variants = [
      complete,
      { ...complete, process_name: "Other" },
      { ...complete, process_start_utc: "" },
      { ...complete, window_thread_id: 0 },
      { ...complete, class_name: "UnityContainerWndClass" },
      { ...complete, window_title: "Enter Safe Mode" },
      { ...complete, child_texts: complete.child_texts.filter((text) => text !== "Quit") },
      { ...complete, child_texts: complete.child_texts.map((text) => text.replace("The project you are opening contains compilation errors", "Compilation warning")) },
      { ...complete, child_texts: complete.child_texts.map((text) => text.replace("If you choose Ignore", "Ignore")) },
    ];

    for (const [index, variant] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([{ ...variant, hwnd: `0x31${index}` }])) });
      expect(status.safe_mode_modal_candidates_count, `variant ${index}`).toBe(0);
      expect(status.blocking_modal_code, `variant ${index}`).not.toBe("blocked_safe_mode_prompt");
    }
  });

  it("detects Missing Signature package security prompts", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([
      {
        hwnd: "0x400404",
        pid: 24682,
        process_name: "Unity",
        class_name: "UnityContainerWndClass",
        window_title: "Missing Signature",
        child_texts: [
          "UnityEditor.PackageManager.UI.Internal.ModalWindowContainer",
        ],
      },
    ]);
    const execFileSync = fakeExecFileSync('"Unity.exe","24682","Console","1","200 K","Running","user","0:01:00","Missing Signature"', calls, nativeModalOutput);

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status).toMatchObject({
      supported: true,
      blocking_startup_possible: true,
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_missing_signature_packages",
      missing_signature_modal_candidates_count: 1,
      missing_signature_modal_candidates: [
        {
          hwnd: "0x400404",
          pid: 24682,
          window_title: "Missing Signature",
          block_code: "blocked_missing_signature_packages",
          likely_missing_signature_prompt: true,
        },
      ],
    });
    expect(status.next_valid_actions).toContain("dry_run_diagnostics.unity_recovery_prompt.respond_close_missing_signature");
    expect(status.next_valid_actions).toContain("call_compile.status_after_heartbeat_recovers");
  });

  it("detects external scene reload modals only with complete evidence", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([{
      hwnd: "0x450450",
      pid: 24683,
      process_name: "Unity",
      process_start_utc: "2026-07-15T07:00:00.000Z",
      window_thread_id: 1200,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: [
        "The following open scene(s) have been changed on disk:\r\n Assets/Scenes/Main Menu.unity\r\n\r\nDo you want to reload the scene(s)?",
        "Reload",
        "Ignore",
      ],
      child_controls: [
        { hwnd: "0x450451", parent_hwnd: "0x450450", pid: 24683, window_thread_id: 1200, visible: true, enabled: true, class_name: "Edit", text: "The following open scene(s) have been changed on disk:\r\n Assets/Scenes/Main Menu.unity\r\n\r\nDo you want to reload the scene(s)?" },
        { hwnd: "0x450460", parent_hwnd: "0x450450", pid: 24683, window_thread_id: 1200, visible: true, enabled: true, class_name: "Button", text: "Reload" },
        { hwnd: "0x450461", parent_hwnd: "0x450450", pid: 24683, window_thread_id: 1200, visible: true, enabled: true, class_name: "Button", text: "Ignore" },
      ],
    }]);

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", calls, nativeModalOutput) });

    expect(status).toMatchObject({
      blocking_automation_possible: true,
      blocking_modal_code: "blocked_external_scene_reload",
      external_scene_reload_modal_candidates_count: 1,
      external_scene_reload_modal_candidates: [{
        hwnd: "0x450450",
        pid: 24683,
        window_title: "The open scene(s) have been modified externally",
        likely_external_scene_reload_prompt: true,
        scene_paths: ["Assets/Scenes/Main Menu.unity"],
        block_code: "blocked_external_scene_reload",
      }],
    });
    expect(status.next_valid_actions).toContain("do_not_auto_respond_external_scene_reload_prompt");
    expect(status.next_valid_actions).toContain("do_not_retry_write_command");
    const nativeScannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    const emittedPattern = /\$windowPattern = '([^']+)'/.exec(nativeScannerScript)?.[1];
    expect(emittedPattern).toBeDefined();
    const windowPattern = new RegExp(emittedPattern!.replace(/^\(\?im\)/, ""), "im");
    const exactTitle = "The open scene(s) have been modified externally";
    expect(windowPattern.test([exactTitle, "Reload", "Ignore"].join("\r\n"))).toBe(true);
    expect(windowPattern.test(["Project - Unity", exactTitle, "Reload", "Ignore"].join("\r\n"))).toBe(true);
    expect(windowPattern.test(["Project - Unity", exactTitle, "Reload", "Ignore"].join("\n"))).toBe(true);
    expect(windowPattern.test(["Project - Unity", "Reload files?", "Reload", "Ignore"].join("\r\n"))).toBe(false);
  });

  it("detects the report-backed sparse external-scene topology without fabricating body or scene paths", () => {
    const nativeModal = {
      hwnd: "0xB80EC8",
      pid: 37524,
      process_name: "Unity",
      process_start_utc: "2026-09-07T06:16:45.5796787Z",
      window_thread_id: 44028,
      window_visible: true,
      window_enabled: true,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: ["Reload", "Ignore"],
      child_controls: [
        { hwnd: "0xC270B9E", parent_hwnd: "0xB80EC8", pid: 37524, window_thread_id: 44028, visible: true, enabled: true, class_name: "Button", text: "Reload" },
        { hwnd: "0x16C0B70", parent_hwnd: "0xB80EC8", pid: 37524, window_thread_id: 44028, visible: true, enabled: true, class_name: "Button", text: "Ignore" },
      ],
    };
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([nativeModal])) });
    const evidence = externalSceneReloadEvidence(status, 37524, "2026-09-07T08:00:02.696Z");

    expect(status).toMatchObject({
      blocking_modal_code: "blocked_external_scene_reload",
      external_scene_reload_modal_candidates_count: 1,
      external_scene_reload_modal_candidates: [{
        external_scene_reload_evidence_scope: "sparse_control_topology",
        likely_external_scene_reload_prompt: true,
        reload_button_count: 1,
        ignore_button_count: 1,
      }],
    });
    expect(evidence).toMatchObject({
      evidence_scope: "sparse_control_topology",
      scene_paths: [],
      scene_paths_count: 0,
      buttons: { reload_hwnd: "0xC270B9E", ignore_hwnd: "0x16C0B70" },
    });
    expect(evidence).not.toHaveProperty("body_evidence");
  });

  it("rejects incomplete or broadened sparse external-scene topologies", () => {
    const base = {
      hwnd: "0xB80EC8",
      pid: 37524,
      process_name: "Unity",
      process_start_utc: "2026-09-07T06:16:45.5796787Z",
      window_thread_id: 44028,
      window_visible: true,
      window_enabled: true,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: ["Reload", "Ignore"],
      child_controls: [
        { hwnd: "0xC270B9E", parent_hwnd: "0xB80EC8", pid: 37524, window_thread_id: 44028, visible: true, enabled: true, class_name: "Button", text: "Reload" },
        { hwnd: "0x16C0B70", parent_hwnd: "0xB80EC8", pid: 37524, window_thread_id: 44028, visible: true, enabled: true, class_name: "Button", text: "Ignore" },
      ],
    };
    const variants = [
      { ...base, window_visible: undefined },
      { ...base, window_enabled: false },
      { ...base, process_start_utc: "" },
      { ...base, class_name: "UnityContainerWndClass" },
      { ...base, child_texts: ["Ignore", "Reload"] },
      { ...base, child_controls: base.child_controls.slice(0, 1) },
      { ...base, child_controls: [...base.child_controls, { ...base.child_controls[0], hwnd: "0xDEADBEEF", text: "" }] },
      { ...base, child_controls: [{ ...base.child_controls[0], visible: false }, base.child_controls[1]] },
      { ...base, child_controls: [{ ...base.child_controls[0], enabled: undefined }, base.child_controls[1]] },
      { ...base, child_controls: [{ ...base.child_controls[0], parent_hwnd: "0xBAD" }, base.child_controls[1]] },
      { ...base, child_controls: [{ ...base.child_controls[0], pid: 1 }, base.child_controls[1]] },
      { ...base, child_controls: [{ ...base.child_controls[0], window_thread_id: 1 }, base.child_controls[1]] },
    ];

    for (const variant of variants) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([variant])) });
      expect(status.external_scene_reload_modal_candidates_count).toBe(0);
      expect(status.blocking_modal_code).not.toBe("blocked_external_scene_reload");
    }
  });

  it("uses the exact embedded dialog state for sparse external-scene evidence", () => {
    const embeddedDialog = {
      hwnd: "0xB80EC8",
      parent_hwnd: "0xB80E00",
      pid: 37524,
      window_thread_id: 44028,
      visible: true,
      enabled: true,
      class_name: "#32770",
      text: "The open scene(s) have been modified externally",
    };
    const row = {
      hwnd: "0xB80E00",
      pid: 37524,
      process_name: "Unity",
      process_start_utc: "2026-09-07T06:16:45.5796787Z",
      window_thread_id: 44027,
      window_visible: true,
      window_enabled: false,
      class_name: "UnityContainerWndClass",
      window_title: "Project - Unity",
      child_texts: ["The open scene(s) have been modified externally", "Reload", "Ignore"],
      child_controls: [
        embeddedDialog,
        { hwnd: "0xC270B9E", parent_hwnd: "0xB80EC8", pid: 37524, window_thread_id: 44028, visible: true, enabled: true, class_name: "Button", text: "Reload" },
        { hwnd: "0x16C0B70", parent_hwnd: "0xB80EC8", pid: 37524, window_thread_id: 44028, visible: true, enabled: true, class_name: "Button", text: "Ignore" },
      ],
    };

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([row])) });
    expect(status.external_scene_reload_modal_candidates).toEqual([expect.objectContaining({
      hwnd: "0xB80EC8",
      window_thread_id: 44028,
      window_visible: true,
      window_enabled: true,
      external_scene_reload_evidence_scope: "sparse_control_topology",
    })]);
    expect(externalSceneReloadEvidence(status, 37524, "2026-09-07T08:00:02.696Z")).toMatchObject({
      hwnd: "0xB80EC8",
      evidence_scope: "sparse_control_topology",
      scene_paths: [],
    });

    const unobservedState = {
      ...row,
      child_controls: [{ ...embeddedDialog, enabled: undefined }, ...row.child_controls.slice(1)],
    };
    const rejected = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([unobservedState])) });
    expect(rejected.external_scene_reload_modal_candidates_count).toBe(0);
  });

  it("falls back to read-only UIAutomation discovery for exact external-scene dialogs", () => {
    const calls: Array<{ file: string; args: string[] }> = [];

    inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", calls, "[]") });

    const scannerScript = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"))?.args.join(" ") ?? "";
    expect(scannerScript).toContain("Add-Type -AssemblyName UIAutomationClient");
    expect(scannerScript).toContain("AutomationElement]::NameProperty");
    expect(scannerScript).toContain("The open scene(s) have been modified externally");
    expect(scannerScript).toContain("AutomationElement]::ProcessIdProperty");
    expect(scannerScript).toContain("AutomationElement]::NativeWindowHandleProperty");
    expect(scannerScript).toContain("TreeScope]::Children");
    expect(scannerScript).toContain("$results.Remove($existing)");
    expect(scannerScript).not.toContain("InvokePattern");
    expect(scannerScript).toContain("SendMessageTimeoutText($handle, 0x000D");
    expect(scannerScript).not.toContain("0x00F5");
  });

  it.each(["#32770", "UnityContainerWndClass"])("promotes one exact embedded %s external-scene dialog", (dialogClass) => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([{
      hwnd: "0x450500",
      pid: 24683,
      process_name: "Unity",
      process_start_utc: "2026-07-15T07:00:00.000Z",
      window_thread_id: 1200,
      class_name: "UnityContainerWndClass",
      window_title: "Project - Unity",
      child_texts: [
        "The open scene(s) have been modified externally",
        "The following open scene(s) have been changed on disk:",
        "Assets/Scenes/Main Menu.unity",
        "Do you want to reload the scene(s)?",
        "Reload",
        "Ignore",
      ],
      child_controls: [
        { hwnd: "0x450550", parent_hwnd: "0x450500", pid: 24683, window_thread_id: 1201, visible: true, class_name: dialogClass, text: "The open scene(s) have been modified externally" },
        { hwnd: "0x450551", parent_hwnd: "0x450550", pid: 24683, window_thread_id: 1201, visible: true, class_name: "Static", text: "The following open scene(s) have been changed on disk:" },
        { hwnd: "0x450552", parent_hwnd: "0x450550", pid: 24683, window_thread_id: 1201, visible: true, class_name: "Static", text: "Assets/Scenes/Main Menu.unity" },
        { hwnd: "0x450553", parent_hwnd: "0x450550", pid: 24683, window_thread_id: 1201, visible: true, class_name: "Static", text: "Do you want to reload the scene(s)?" },
        { hwnd: "0x450560", parent_hwnd: "0x450550", pid: 24683, window_thread_id: 1201, visible: true, class_name: "Button", text: "Reload" },
        { hwnd: "0x450561", parent_hwnd: "0x450550", pid: 24683, window_thread_id: 1201, visible: true, class_name: "Button", text: "Ignore" },
        { hwnd: "0x450570", parent_hwnd: "0x450500", pid: 24683, window_thread_id: 1200, visible: true, class_name: "Button", text: "Reload" },
        { hwnd: "0x450571", parent_hwnd: "0x450550", pid: 24683, window_thread_id: 1201, visible: false, enabled: true, class_name: "Button", text: "Ignore" },
        { hwnd: "0x450572", parent_hwnd: "0x450550", pid: 24683, window_thread_id: 1201, visible: true, enabled: true, class_name: "ButtonHost", text: "Reload" },
      ],
    }]);
    const runtime = createUnityWindowDiagnosticsRuntime({
      execFileSync: fakeExecFileSync("", calls, [nativeModalOutput, nativeModalOutput, JSON.stringify({ status: "message_sent", button_text: "Reload", reload_count: 1, ignore_count: 1 })]),
      now: () => new Date("2026-07-15T08:27:00.000Z"),
    });

    const status = runtime.status();
    const evidence = runtime.externalSceneReloadEvidence?.(24683);

    expect(status.external_scene_reload_modal_candidates).toEqual([expect.objectContaining({
      hwnd: "0x450550",
      window_thread_id: 1201,
      pid: 24683,
      class_name: dialogClass,
      window_title: "The open scene(s) have been modified externally",
      reload_button_hwnd: "0x450560",
      ignore_button_hwnd: "0x450561",
      scene_paths: ["Assets/Scenes/Main Menu.unity"],
    })]);
    expect(evidence).toMatchObject({
      hwnd: "0x450550",
      window_thread_id: 1201,
      buttons: { reload_hwnd: "0x450560", ignore_hwnd: "0x450561", reload_count: 1, ignore_count: 1 },
      scene_paths: ["Assets/Scenes/Main Menu.unity"],
    });
    const response = runtime.respondExternalSceneReload?.({ action: "reload", pid: 24683, hwnd: "0x450550", evidence_id: evidence!.evidence_id });
    expect(response).toMatchObject({ status: "message_sent", interaction_attempted: true, button_text: "Reload" });
    const responseScript = calls.filter((call) => call.file.toLowerCase().endsWith("powershell.exe"))[2]?.args.join(" ") ?? "";
    expect(responseScript).toContain("ToInt64('450550', 16)");
    expect(responseScript).toContain("IsChild($targetHwnd, $button)");
    expect(responseScript).toContain("IsWindowEnabled($button)");
    expect(responseScript).toContain("$visible -and $enabled");
    expect(responseScript).toContain("$className -ceq 'Button'");
    expect(responseScript).toContain("exact_button_process_thread_or_ancestry_changed");
  });

  it("rejects ambiguous embedded external-scene dialog containers", () => {
    const output = JSON.stringify([{
      hwnd: "0x450500",
      pid: 24683,
      process_name: "Unity",
      class_name: "UnityContainerWndClass",
      window_title: "Project - Unity",
      child_texts: [
        "The open scene(s) have been modified externally",
        "The following open scene(s) have been changed on disk:",
        "Assets/Scenes/Main.unity",
        "Do you want to reload the scene(s)?",
        "Reload",
        "Ignore",
      ],
      child_controls: [
        { hwnd: "0x450550", class_name: "#32770", text: "The open scene(s) have been modified externally" },
        { hwnd: "0x450551", class_name: "#32770", text: "The open scene(s) have been modified externally" },
        { hwnd: "0x450560", class_name: "Button", text: "Reload" },
        { hwnd: "0x450561", class_name: "Button", text: "Ignore" },
      ],
    }]);

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], output) });

    expect(status.external_scene_reload_modal_candidates_count).toBe(0);
    expect(status.blocking_modal_code).not.toBe("blocked_external_scene_reload");
  });

  it("rejects generic or incomplete Reload/Ignore dialogs", () => {
    const completeChildren = [
      "The following open scene(s) have been changed on disk:",
      "Assets/Scenes/Main.unity",
      "Do you want to reload the scene(s)?",
      "Reload",
      "Ignore",
    ];
    const variants = [
      { window_title: "Reload files?", child_texts: ["Assets/Scenes/Main.unity", "Reload", "Ignore"] },
      { window_title: "The open scene(s) have been modified externally", child_texts: completeChildren.filter((text) => !text.startsWith("The following")) },
      { window_title: "The open scene(s) have been modified externally", child_texts: completeChildren.filter((text) => !text.startsWith("Do you want")) },
      { window_title: "The open scene(s) have been modified externally", child_texts: completeChildren.filter((text) => !text.endsWith(".unity")) },
      { window_title: "The open scene(s) have been modified externally", child_texts: completeChildren.filter((text) => text !== "Reload") },
      { window_title: "The open scene(s) have been modified externally", child_texts: completeChildren.filter((text) => text !== "Ignore") },
    ];

    for (const [index, variant] of variants.entries()) {
      const output = JSON.stringify([{ hwnd: `0x${index + 1}`, pid: 25000 + index, process_name: "Unity", class_name: "#32770", ...variant }]);
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], output) });
      expect(status.external_scene_reload_modal_candidates_count).toBe(0);
      expect(status.blocking_modal_code).not.toBe("blocked_external_scene_reload");
    }
  });

  it.each(["reload", "ignore"] as const)("builds bounded exact external-scene evidence and sends only the approved %s button message", (action) => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([{
      hwnd: "0x450450",
      pid: 24683,
      process_name: "Unity",
      process_start_utc: "2026-07-15T07:00:00.000Z",
      window_thread_id: 1200,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: [
        "The following open scene(s) have been changed on disk:",
        "Assets/Scenes/Main Menu.unity",
        "Do you want to reload the scene(s)?",
        "Reload",
        "Ignore",
      ],
      child_controls: [
        { hwnd: "0x450460", class_name: "Button", text: "Reload" },
        { hwnd: "0x450461", class_name: "Button", text: "Ignore" },
      ],
    }]);
    const runtime = createUnityWindowDiagnosticsRuntime({
      execFileSync: fakeExecFileSync("", calls, [nativeModalOutput, nativeModalOutput, JSON.stringify({ status: "message_sent", button_text: action === "reload" ? "Reload" : "Ignore", reload_count: 1, ignore_count: 1 })]),
      now: () => new Date("2026-07-15T08:27:00.000Z"),
    });
    runtime.status();
    const evidence = runtime.externalSceneReloadEvidence?.(24683);
    expect(evidence).toMatchObject({
      evidence_id: expect.stringMatching(/^sha256:[a-f0-9]{64}$/),
      pid: 24683,
      hwnd: "0x450450",
      scene_paths: ["Assets/Scenes/Main Menu.unity"],
      buttons: { reload_count: 1, ignore_count: 1 },
    });

    const result = runtime.respondExternalSceneReload?.({
      action,
      pid: 24683,
      hwnd: "0x450450",
      evidence_id: evidence!.evidence_id,
    });

    expect(result).toMatchObject({
      action,
      status: "message_sent",
      interaction_attempted: true,
      message_sent: true,
      popup_closed_verified: false,
      compile_state_verified: false,
      button_text: action === "reload" ? "Reload" : "Ignore",
    });
    const responseScript = calls.filter((call) => call.file.toLowerCase().endsWith("powershell.exe"))[2]?.args.join(" ") ?? "";
    expect(responseScript).toContain(`$targetAction = '${action}'`);
    expect(responseScript).toContain("$targetHwnd");
    expect(responseScript).toContain("$windowClass -ceq ([string]$expectedIdentity.class_name)");
    expect(responseScript).toContain("SendMessageTimeout");
    expect(responseScript).toContain(".Replace('\\', '/')");
    expect(responseScript).not.toContain("SetForegroundWindow");
    const replay = runtime.respondExternalSceneReload?.({ action, pid: 24683, hwnd: "0x450450", evidence_id: evidence!.evidence_id });
    expect(replay).toMatchObject({ status: "refused", interaction_attempted: false, error: "external_scene_reload_evidence_not_issued_expired_or_consumed" });
  });

  it("refuses stale evidence and duplicate exact action buttons without clicking", () => {
    const nativeModalOutput = JSON.stringify([{
      hwnd: "0x450451",
      pid: 24684,
      process_name: "Unity",
      process_start_utc: "2026-07-15T07:00:00.000Z",
      window_thread_id: 1201,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: [
        "The following open scene(s) have been changed on disk:",
        "Assets/Scenes/Main.unity",
        "Do you want to reload the scene(s)?",
        "Reload",
        "Reload",
        "Ignore",
      ],
      child_controls: [
        { hwnd: "0x450462", class_name: "Button", text: "Reload" },
        { hwnd: "0x450463", class_name: "Button", text: "Reload" },
        { hwnd: "0x450464", class_name: "Button", text: "Ignore" },
      ],
    }]);
    const calls: Array<{ file: string; args: string[] }> = [];
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], nativeModalOutput) });
    expect(externalSceneReloadEvidence(status, 24684, "2026-07-15T08:27:00.000Z")).toBeUndefined();

    const runtime = createUnityWindowDiagnosticsRuntime({ execFileSync: fakeExecFileSync("", calls, nativeModalOutput) });
    runtime.status();
    const result = runtime.respondExternalSceneReload?.({ action: "ignore", pid: 24684, hwnd: "0x450451", evidence_id: `sha256:${"0".repeat(64)}` });
    expect(result).toMatchObject({ status: "refused", interaction_attempted: false, message_sent: false, error: "external_scene_reload_evidence_not_issued_expired_or_consumed" });
    expect(calls.filter((call) => call.file.toLowerCase().endsWith("powershell.exe"))).toHaveLength(1);
  });

  it("marks a timed-out button message as attempted with uncertain delivery", () => {
    const nativeModalOutput = JSON.stringify([{
      hwnd: "0x450452",
      pid: 24685,
      process_name: "Unity",
      process_start_utc: "2026-07-15T07:00:00.000Z",
      window_thread_id: 1202,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: ["The following open scene(s) have been changed on disk:", "Assets/Scenes/Main.unity", "Do you want to reload the scene(s)?", "Reload", "Ignore"],
      child_controls: [{ hwnd: "0x450465", class_name: "Button", text: "Reload" }, { hwnd: "0x450466", class_name: "Button", text: "Ignore" }],
    }]);
    const runtime = createUnityWindowDiagnosticsRuntime({
      execFileSync: fakeExecFileSync("", [], [nativeModalOutput, nativeModalOutput, JSON.stringify({ status: "error", error: "bm_click_failed_or_timed_out", button_text: "Ignore", reload_count: 1, ignore_count: 1 })]),
    });
    runtime.status();
    const evidence = runtime.externalSceneReloadEvidence?.(24685);
    const result = runtime.respondExternalSceneReload?.({ action: "ignore", pid: 24685, hwnd: "0x450452", evidence_id: evidence!.evidence_id });
    expect(result).toMatchObject({
      status: "error",
      interaction_attempted: true,
      message_sent: false,
      delivery_state: "unknown_after_send_attempt",
      popup_closed_verified: false,
      error: "bm_click_failed_or_timed_out",
    });
  });

  it("expires an issued popup generation before any interaction", () => {
    const nativeModalOutput = JSON.stringify([{
      hwnd: "0x450453",
      pid: 24686,
      process_name: "Unity",
      process_start_utc: "2026-07-15T07:00:00.000Z",
      window_thread_id: 1203,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: ["The following open scene(s) have been changed on disk:", "Assets/Scenes/Main.unity", "Do you want to reload the scene(s)?", "Reload", "Ignore"],
      child_controls: [{ hwnd: "0x450467", class_name: "Button", text: "Reload" }, { hwnd: "0x450468", class_name: "Button", text: "Ignore" }],
    }]);
    let now = new Date("2026-07-15T08:27:00.000Z");
    const runtime = createUnityWindowDiagnosticsRuntime({ execFileSync: fakeExecFileSync("", [], nativeModalOutput), now: () => now });
    runtime.status();
    const evidence = runtime.externalSceneReloadEvidence?.(24686);
    now = new Date("2026-07-15T08:28:00.001Z");
    const result = runtime.respondExternalSceneReload?.({ action: "reload", pid: 24686, hwnd: "0x450453", evidence_id: evidence!.evidence_id });
    expect(result).toMatchObject({ status: "refused", interaction_attempted: false, delivery_state: "not_attempted", error: "external_scene_reload_evidence_not_issued_expired_or_consumed" });
  });

  it("preserves a popup token across an unrelated project scan", () => {
    const row = (pid: number, hwnd: string, suffix: string) => ({
      hwnd,
      pid,
      process_name: "Unity",
      process_start_utc: `2026-07-15T07:00:0${suffix}.000Z`,
      window_thread_id: 1300 + pid,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: ["The following open scene(s) have been changed on disk:", `Assets/Scenes/Main${suffix}.unity`, "Do you want to reload the scene(s)?", "Reload", "Ignore"],
      child_controls: [{ hwnd: `0x${suffix}10`, class_name: "Button", text: "Reload" }, { hwnd: `0x${suffix}11`, class_name: "Button", text: "Ignore" }],
    });
    const nativeOutput = JSON.stringify([row(24687, "0x450454", "1"), row(24688, "0x450455", "2")]);
    const runtime = createUnityWindowDiagnosticsRuntime({
      execFileSync: fakeExecFileSync("", [], [nativeOutput, nativeOutput, nativeOutput, JSON.stringify({ status: "message_sent", button_text: "Reload", reload_count: 1, ignore_count: 1 })]),
    });
    runtime.status();
    const evidence = runtime.externalSceneReloadEvidence?.(24687);
    runtime.status();
    const result = runtime.respondExternalSceneReload?.({ action: "reload", pid: 24687, hwnd: "0x450454", evidence_id: evidence!.evidence_id });
    expect(result).toMatchObject({ status: "message_sent", interaction_attempted: true, delivery_state: "message_sent" });
  });

  it("invalidates a token when process or exact button identity changes", () => {
    const row = (processStart: string, reloadHwnd: string) => ({
      hwnd: "0x450456",
      pid: 24689,
      process_name: "Unity",
      process_start_utc: processStart,
      window_thread_id: 1400,
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: ["The following open scene(s) have been changed on disk:", "Assets/Scenes/Main.unity", "Do you want to reload the scene(s)?", "Reload", "Ignore"],
      child_controls: [{ hwnd: reloadHwnd, class_name: "Button", text: "Reload" }, { hwnd: "0x450471", class_name: "Button", text: "Ignore" }],
    });
    const first = JSON.stringify([row("2026-07-15T07:00:00.000Z", "0x450470")]);
    const replacement = JSON.stringify([row("2026-07-15T07:01:00.000Z", "0x450472")]);
    const runtime = createUnityWindowDiagnosticsRuntime({ execFileSync: fakeExecFileSync("", [], [first, replacement]) });
    runtime.status();
    const evidence = runtime.externalSceneReloadEvidence?.(24689);
    runtime.status();
    const result = runtime.respondExternalSceneReload?.({ action: "reload", pid: 24689, hwnd: "0x450456", evidence_id: evidence!.evidence_id });
    expect(result).toMatchObject({ status: "refused", interaction_attempted: false, error: "external_scene_reload_evidence_not_issued_expired_or_consumed" });
  });

  it("detects only the exact fully evidenced project downgrade prompt without exposing its raw body", () => {
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([projectDowngradeWindow()])) });
    expect(status).toMatchObject({
      blocking_modal_code: "blocked_project_downgrade_prompt",
      project_downgrade_modal_candidates_count: 1,
      project_downgrade_modal_candidates: [{
        pid: 26000,
        process_name: "Unity",
        window_title: "Project Downgrade Required",
        child_texts: ["Continue", "Quit"],
        project_downgrade: {
          source_version: "6000.0.30f1",
          target_version: "6000.0.20f1",
          body_sha256: expect.stringMatching(/^sha256:[a-f0-9]{64}$/),
          button_count: 2,
          continue_button_hwnd: "0x700710",
          quit_button_hwnd: "0x700711",
        },
      }],
    });
    expect(JSON.stringify(status.project_downgrade_modal_candidates)).not.toContain("Packages and settings");
    expect(status.next_valid_actions).toContain("do_not_auto_continue_or_quit_project_downgrade");
  });

  it("fails closed when any independent project downgrade body proof is absent", () => {
    const body = projectDowngradeBody();
    for (let index = 0; index < body.length; index += 1) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([projectDowngradeWindow({ body: body.filter((_, bodyIndex) => bodyIndex !== index) })])) });
      expect(status.project_downgrade_modal_candidates_count, `body criterion ${index}`).toBe(0);
      expect(status.blocking_modal_code).not.toBe("blocked_project_downgrade_prompt");
    }
  });

  it("rejects wrong title case, class, owner, malformed/equal/reversed versions, and non-exact buttons", () => {
    const variants = [
      projectDowngradeWindow({ window_title: "project downgrade required" }),
      projectDowngradeWindow({ window_title: "Project Downgrade Required!" }),
      projectDowngradeWindow({ class_name: "OtherDialog" }),
      projectDowngradeWindow({ process_name: "Unity Hub" }),
      projectDowngradeWindow({ body: projectDowngradeBody().map((text) => text.replace("6000.0.30f1", "6000.x")) }),
      projectDowngradeWindow({ body: projectDowngradeBody().map((text) => text.replace("6000.0.30f1", "6000.0.20f1")) }),
      projectDowngradeWindow({ body: projectDowngradeBody().map((text) => text.replace("6000.0.30f1", "6000.0.10f1")) }),
      projectDowngradeWindow({ body: [...projectDowngradeBody().map((text) => text.replace(" is newer", "").replace(" is older", "")), "Unrelated newer and older wording."] }),
      projectDowngradeWindow({ buttons: [{ hwnd: "0x700710", class_name: "Button", text: "Continue" }] }),
      projectDowngradeWindow({ buttons: [{ hwnd: "0x700710", class_name: "Button", text: "Continue" }, { hwnd: "0x700712", class_name: "Button", text: "Continue" }, { hwnd: "0x700711", class_name: "Button", text: "Quit" }] }),
      projectDowngradeWindow({ buttons: [{ hwnd: "0x700710", class_name: "Button", text: "Continue" }, { hwnd: "0x700711", class_name: "Button", text: "Quit" }, { hwnd: "0x700712", class_name: "Button", text: "Help" }] }),
      projectDowngradeWindow({ buttons: [{ hwnd: "0x700710", class_name: "button", text: "Continue" }, { hwnd: "0x700711", class_name: "Button", text: "Quit" }] }),
    ];
    for (const [index, window] of variants.entries()) {
      const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([{ ...window, hwnd: `0x71${index}` }])) });
      expect(status.project_downgrade_modal_candidates_count, `variant ${index}`).toBe(0);
    }
  });

  it.each([
    ["6000.5.3f1", "6000.4.10f1"],
    ["2022.3.10p2", "2022.3.10f1"],
    ["2022.3.10f1", "2022.3.10rc4"],
    ["2022.3.10rc2", "2022.3.10b9"],
    ["2022.3.10b2", "2022.3.10a9"],
    ["2022.3.11", "2022.3.10f99"],
  ])("orders supported Unity version forms %s above %s", (source, target) => {
    const body = projectDowngradeBody().map((text) => text.replace("6000.0.30f1", source).replace("6000.0.20f1", target));
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([projectDowngradeWindow({ body })])) });
    expect(status.project_downgrade_modal_candidates).toMatchObject([{ project_downgrade: { source_version: source, target_version: target } }]);
  });

  it("uses 60-second single-use project-bound evidence and submits only BM_CLICK to Continue without a success claim", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const popup = JSON.stringify([projectDowngradeWindow()]);
    const runtime = createUnityWindowDiagnosticsRuntime({
      execFileSync: fakeExecFileSync("", calls, [popup, popup, JSON.stringify({ status: "message_submitted", button_text: "Continue" })]),
      now: () => new Date("2026-07-15T10:00:00.000Z"),
    });
    runtime.status();
    const evidence = runtime.projectDowngradeEvidence?.(26000, "c:/u/game");
    expect(evidence).toMatchObject({ project_identity_sha256: expect.stringMatching(/^sha256:/), source_version: "6000.0.30f1", target_version: "6000.0.20f1", buttons: { count: 2 } });
    expect(evidence).not.toHaveProperty("canonical_project_root");
    const result = runtime.continueProjectDowngrade?.(projectDowngradeArgs(evidence!));
    expect(result).toMatchObject({ status: "message_submitted", success: false, message_submitted: true, popup_closed_verified: false, fresh_project_state_verified: false, button_text: "Continue" });
    const script = calls.filter((call) => call.file.toLowerCase().endsWith("powershell.exe"))[2]?.args.join(" ") ?? "";
    expect(script).toContain("SendMessageTimeout($continue[0].handle,0x00F5");
    expect(script).toContain("Test-BoundButton $continue[0].handle");
    expect(script).toContain("Test-BoundButton $quit[0].handle");
    expect(script).toContain("IsWindowEnabled($handle)");
    expect(script).toContain("GetParent($handle)");
    expect(script).toContain("GetAncestor($handle,2)");
    expect(script).toContain("GetWindowThreadProcessId($handle,[ref]$childPid)");
    expect(script).not.toContain("SendMessageTimeout($quit");
    expect(script).not.toContain("targetAction");
    expect(result?.next_valid_actions).toEqual(expect.arrayContaining([
      "call_diagnostics.unity_modal.status_for_fresh_popup_scan",
      "call_unity.process.list_for_project_process_rediscovery",
      "call_sgg_unity_projects_for_fresh_heartbeat",
      "call_package.info_and_package.operation.status_for_resolution_after_fresh_heartbeat",
      "call_compile.status_after_fresh_heartbeat",
      "do_not_claim_popup_closed_or_downgrade_complete_from_message_submission",
    ]));
    expect(runtime.continueProjectDowngrade?.(projectDowngradeArgs(evidence!))).toMatchObject({ status: "refused", interaction_attempted: false, error: "project_downgrade_evidence_not_issued_expired_or_consumed" });
  });

  it("returns one active popup-scoped token and consumes the whole popup generation on an attempted submission", () => {
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([projectDowngradeWindow()])) });
    let attempts = 0;
    const runtime = createUnityWindowDiagnosticsRuntime({
      inspect: () => status,
      continueProjectDowngrade: (args) => {
        attempts += 1;
        return { schema_version: 1, status: "error", success: false, interaction_attempted: true, message_submitted: false, delivery_state: "unknown_after_send_attempt", popup_closed_verified: false, fresh_project_state_verified: false, target_pid: args.pid ?? null, target_hwnd: args.hwnd ?? null, button_text: "Continue", error: "bm_click_failed_or_timed_out", next_valid_actions: [] };
      },
    });
    runtime.status();
    const first = runtime.projectDowngradeEvidence?.(26000, "c:/u/game");
    const sibling = runtime.projectDowngradeEvidence?.(26000, "c:/u/game");
    const differentlyBoundSibling = runtime.projectDowngradeEvidence?.(26000, "c:/u/other");
    expect(sibling?.evidence_id).toBe(first?.evidence_id);
    expect(differentlyBoundSibling?.evidence_id).toBe(first?.evidence_id);
    expect(differentlyBoundSibling?.project_identity_sha256).toBe(first?.project_identity_sha256);
    expect(runtime.continueProjectDowngrade?.(projectDowngradeArgs(first!))).toMatchObject({ interaction_attempted: true, delivery_state: "unknown_after_send_attempt" });
    expect(runtime.continueProjectDowngrade?.(projectDowngradeArgs(differentlyBoundSibling!))).toMatchObject({ status: "refused", interaction_attempted: false, error: "project_downgrade_evidence_not_issued_expired_or_consumed" });
    expect(attempts).toBe(1);
  });

  it("fails closed on duplicate exact project downgrade dialogs for one Unity process", () => {
    const first = projectDowngradeWindow();
    const second = projectDowngradeWindow({ hwnd: "0x700701", buttons: [{ hwnd: "0x700712", class_name: "Button", text: "Continue" }, { hwnd: "0x700713", class_name: "Button", text: "Quit" }] });
    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify([first, second])) });
    expect(status.project_downgrade_modal_candidates_count).toBe(2);
    expect(projectDowngradeEvidence(status, 26000, "2026-07-15T10:00:00.000Z", `sha256:${"1".repeat(64)}`)).toBeUndefined();
  });

  it.each([
    [{ status: "refused", error: "popup_button_boundary_changed" }, { status: "refused", interaction_attempted: false, delivery_state: "not_attempted", error: "popup_button_boundary_changed" }],
    [{ status: "error", error: "bm_click_failed_or_timed_out", button_text: "Continue" }, { status: "error", interaction_attempted: true, delivery_state: "unknown_after_send_attempt", error: "bm_click_failed_or_timed_out" }],
  ])("reports final button-boundary refusal or timeout without a success claim", (interaction, expected) => {
    const popup = JSON.stringify([projectDowngradeWindow()]);
    const runtime = createUnityWindowDiagnosticsRuntime({ execFileSync: fakeExecFileSync("", [], [popup, popup, JSON.stringify(interaction)]) });
    runtime.status();
    const evidence = runtime.projectDowngradeEvidence?.(26000, "c:/u/game");
    expect(runtime.continueProjectDowngrade?.(projectDowngradeArgs(evidence!))).toMatchObject({ ...expected, success: false, popup_closed_verified: false, fresh_project_state_verified: false });
  });

  it("expires project downgrade evidence before interaction", () => {
    let now = new Date("2026-07-15T10:00:00.000Z");
    const runtime = createUnityWindowDiagnosticsRuntime({ execFileSync: fakeExecFileSync("", [], JSON.stringify([projectDowngradeWindow()])), now: () => now });
    runtime.status();
    const evidence = runtime.projectDowngradeEvidence?.(26000, "c:/u/game");
    now = new Date("2026-07-15T10:01:00.001Z");
    expect(runtime.continueProjectDowngrade?.(projectDowngradeArgs(evidence!))).toMatchObject({ status: "refused", interaction_attempted: false, error: "project_downgrade_evidence_not_issued_expired_or_consumed" });
  });

  it("consumes and refuses evidence when its canonical project binding changes", () => {
    const popup = JSON.stringify([projectDowngradeWindow()]);
    const runtime = createUnityWindowDiagnosticsRuntime({ execFileSync: fakeExecFileSync("", [], popup) });
    runtime.status();
    const evidence = runtime.projectDowngradeEvidence?.(26000, "c:/u/game");
    expect(runtime.continueProjectDowngrade?.({ ...projectDowngradeArgs(evidence!), canonical_project_root: "c:/u/other" })).toMatchObject({ status: "refused", interaction_attempted: false, error: "invalid_or_changed_project_downgrade_binding" });
    expect(runtime.continueProjectDowngrade?.(projectDowngradeArgs(evidence!))).toMatchObject({ status: "refused", error: "project_downgrade_evidence_not_issued_expired_or_consumed" });
  });

  it("invalidates project downgrade evidence for every popup identity mutation", () => {
    const mutations = [
      { pid: 26001 },
      { process_name: "Unity.exe" },
      { process_start_utc: "2026-07-15T09:01:00.000Z" },
      { hwnd: "0x700701" },
      { window_thread_id: 1701 },
      { class_name: "UnityContainerWndClass" },
      { window_title: "project downgrade required" },
      { body: projectDowngradeBody().map((text) => text.replace("6000.0.30f1", "6000.0.31f1")) },
      { buttons: [{ hwnd: "0x700712", class_name: "Button", text: "Continue" }, { hwnd: "0x700711", class_name: "Button", text: "Quit" }] },
      { buttons: [{ hwnd: "0x700710", class_name: "Button", text: "Continue" }, { hwnd: "0x700713", class_name: "Button", text: "Quit" }] },
      { buttons: [{ hwnd: "0x700710", class_name: "Button", text: "Continue" }, { hwnd: "0x700711", class_name: "Button", text: "Quit" }, { hwnd: "0x700714", class_name: "Button", text: "Help" }] },
    ];
    for (const [index, mutation] of mutations.entries()) {
      const first = JSON.stringify([projectDowngradeWindow()]);
      const changed = JSON.stringify([projectDowngradeWindow(mutation)]);
      const runtime = createUnityWindowDiagnosticsRuntime({ execFileSync: fakeExecFileSync("", [], [first, changed]) });
      runtime.status();
      const evidence = runtime.projectDowngradeEvidence?.(26000, "c:/u/game");
      runtime.status();
      expect(runtime.continueProjectDowngrade?.(projectDowngradeArgs(evidence!)), `mutation ${index}`).toMatchObject({ status: "refused", interaction_attempted: false, error: "project_downgrade_evidence_not_issued_expired_or_consumed" });
    }
  });

  it("detects stalled Reloading Domain progress windows", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([
      {
        hwnd: "0x500504",
        pid: 41082,
        process_name: "Unity",
        class_name: "UnityContainerWndClass",
        window_title: "Reloading Domain (busy for 08:57)...",
        child_texts: ["Reloading Domain", "busy for 08:57", "Cancel"],
      },
    ]);
    const execFileSync = fakeExecFileSync('"Unity.exe","41082","Console","1","200 K","Running","user","0:08:57","Reloading Domain (busy for 08:57)..."', calls, nativeModalOutput);

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status).toMatchObject({
      supported: true,
      blocking_startup_possible: true,
      blocking_progress_possible: true,
      blocking_reload_possible: true,
      blocking_progress_code: "blocked_domain_reload_progress",
      native_progress_windows_count: 1,
      domain_reload_progress_windows_count: 1,
      domain_reload_progress_stall_count: 1,
      domain_reload_progress_windows: [
        {
          pid: 41082,
          window_title: "Reloading Domain (busy for 08:57)...",
          progress_kind: "domain_reload",
          busy_text: "08:57",
          busy_seconds: 537,
          stall_threshold_seconds: 120,
          is_stalled: true,
          diagnostic_only: true,
          automation_attempted: false,
          block_code: "blocked_domain_reload_progress",
        },
      ],
    });
    expect(status.next_valid_actions).toContain("operator_inspect_visible_unity_progress_window");
    expect(status.next_valid_actions).toContain("do_not_click_cancel_from_gateway");
    const powershellCall = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe"));
    expect(powershellCall?.args.join(" ")).toContain("Reloading\\s+Domain");
  });

  it("detects body-only Reloading Domain progress below stall threshold", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([
      {
        hwnd: "0x500505",
        pid: 41083,
        process_name: "Unity",
        class_name: "UnityContainerWndClass",
        window_title: "Unity",
        child_texts: ["Reloading Domain", "busy for 01:00", "Cancel"],
      },
    ]);
    const execFileSync = fakeExecFileSync('"Unity.exe","41083","Console","1","200 K","Running","user","0:01:00","Unity"', calls, nativeModalOutput);

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status).toMatchObject({
      blocking_progress_possible: false,
      blocking_reload_possible: true,
      native_progress_windows_count: 1,
      domain_reload_progress_windows_count: 1,
      domain_reload_progress_stall_count: 0,
      domain_reload_progress_windows: [
        {
          pid: 41083,
          window_title: "Unity",
          progress_kind: "domain_reload",
          busy_text: "01:00",
          busy_seconds: 60,
          is_stalled: false,
        },
      ],
    });
    expect(status).not.toHaveProperty("blocking_progress_code");
  });

  it("classifies native asset import progress and tasklist responsiveness without treating it as a stalled reload", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([
      {
        hwnd: "0x500506",
        pid: 41084,
        process_name: "Unity",
        class_name: "UnityContainerWndClass",
        window_title: "Importing Assets",
        child_texts: ["Importing asset", "Texture3D", "busy for 02:15"],
      },
    ]);
    const tasklist = [
      '"Unity.exe","41084","Console","1","200 K","Running","user","0:02:15","MyGame - Unity"',
      '"Unity.exe","41085","Console","1","200 K","Not Responding","user","0:02:15","OtherGame - Unity"',
    ].join("\n");

    const status = inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync(tasklist, calls, nativeModalOutput) });

    expect(status.active_unity_windows).toEqual(expect.arrayContaining([
      expect.objectContaining({ pid: 41084, responding: true }),
      expect.objectContaining({ pid: 41085, responding: false }),
    ]));
    expect(status.native_progress_windows).toEqual([
      expect.objectContaining({
        pid: 41084,
        progress_kind: "asset_import",
        busy_seconds: 135,
        is_stalled: false,
        matched_keywords: expect.arrayContaining(["asset_import_progress", "busy_for"]),
      }),
    ]);
    expect(status.blocking_progress_possible).toBe(false);
    expect(status.blocking_reload_possible).toBe(false);
    expect(calls.some((call) => call.file.toLowerCase().endsWith("powershell.exe") && call.args.join(" ").includes("Importing\\s+(?:Asset|Assets|Package|Packages)"))).toBe(true);
  });

  it("ignores non-Unity busy progress text", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([
      {
        hwnd: "0x600606",
        pid: 1234,
        process_name: "OtherApp",
        class_name: "#32770",
        window_title: "busy for 08:57",
        child_texts: ["busy for 08:57", "Cancel"],
      },
    ]);
    const execFileSync = fakeExecFileSync("", calls, nativeModalOutput);

    const status = inspectUnityWindowDiagnostics({ execFileSync });

    expect(status.native_progress_windows_count).toBe(0);
    expect(status.domain_reload_progress_windows_count).toBe(0);
    expect(status.blocking_progress_possible).toBe(false);
  });

  it("dry-runs Missing Signature close only after matching the exact dialog and Close button", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const nativeModalOutput = JSON.stringify([
      {
        hwnd: "0x400404",
        pid: 24682,
        process_name: "Unity",
        class_name: "UnityContainerWndClass",
        window_title: "Missing Signature",
        child_texts: ["UnityEditor.PackageManager.UI.Internal.ModalWindowContainer"],
      },
    ]);
    const clickOutput = JSON.stringify([{ pid: 24682, status: "would_click", window_title: "Missing Signature", button_text: "Close", close_method: "window_close", matched_button_count: 0 }]);
    const execFileSync = fakeExecFileSync('"Unity.exe","24682","Console","1","200 K","Running","user","0:01:00","Missing Signature"', calls, [nativeModalOutput, clickOutput]);

    const result = respondToUnityRecoveryPrompt({ action: "close_missing_signature", pids: [24682], dry_run: true }, { execFileSync });

    expect(result).toMatchObject({
      action: "close_missing_signature",
      dry_run: true,
      button_click_method: "powershell_user32_exact_window_close",
      target_pids: [24682],
      clicked_count: 0,
      results: [{ pid: 24682, status: "would_click", window_title: "Missing Signature", button_text: "Close", close_method: "window_close", matched_button_count: 0 }],
    });
    expect(result.next_valid_actions).toContain("call_compile.status_after_heartbeat_recovers");
    const powershellCall = calls.filter((call) => call.file.toLowerCase().endsWith("powershell.exe"))[1];
    const script = powershellCall?.args.join(" ") ?? "";
    expect(script).toContain("$titlePattern = '(?i)^Missing Signature$'");
    expect(script).toContain('$buttonPattern = "(?i)^Close$"');
    expect(script).toContain("$requiredClassPattern = '(?i)^(#32770|UnityContainerWndClass)$'");
    expect(script).toContain("$closeMethod = 'window_close'");
    expect(script).toContain("UnityEditor\\.PackageManager\\.UI\\.Internal\\.ModalWindowContainer");
  });

  it("does not close normal recovery prompts when Missing Signature close is requested", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync = fakeExecFileSync('"Unity.exe","41000","Console","1","200 K","Running","user","0:01:00","Unity Project Recovery"', calls);

    const result = respondToUnityRecoveryPrompt({ action: "close_missing_signature", pids: [41000], dry_run: false }, { execFileSync });

    expect(result).toMatchObject({
      action: "close_missing_signature",
      target_pids: [],
      clicked_count: 0,
      skipped: [{ pid: 41000, reason: "not_active_missing_signature_prompt" }],
      errors: ["no_missing_signature_prompt"],
    });
    expect(calls.some((call) => call.file.toLowerCase().endsWith("powershell.exe") && call.args.join(" ").includes("$targetPids"))).toBe(false);
  });

  it("dry-runs the recovery prompt response only after matching the recovery candidate", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync = fakeExecFileSync([
      '"Unity.exe","41000","Console","1","200 K","Running","user","0:01:00","Unity Project Recovery"',
      '"Unity.exe","41050","Console","1","200 K","Running","user","0:01:00","MyGame - SampleScene - Unity 6000.0"',
    ].join("\n"), calls, JSON.stringify([{ pid: 41000, status: "would_click", window_title: "Unity Project Recovery", button_text: "Don't Restore", matched_button_count: 1 }]));

    const result = respondToUnityRecoveryPrompt({ action: "do_not_restore", dry_run: true }, { execFileSync });

    expect(result).toMatchObject({
      action: "do_not_restore",
      dry_run: true,
      target_pids: [41000],
      clicked_count: 0,
      results: [{ pid: 41000, status: "would_click", window_title: "Unity Project Recovery", button_text: "Don't Restore" }],
    });
    const powershellCall = calls.find((call) => call.file.toLowerCase().endsWith("powershell.exe") && call.args.join(" ").includes("$targetPids"));
    expect(powershellCall?.args.join(" ")).toContain("41000");
    expect(powershellCall?.args.join(" ")).toContain("$dryRun = $true");
  });

  it("requires one explicit recovery prompt target for non-dry-run when multiple candidates exist", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync = fakeExecFileSync([
      '"Unity.exe","41000","Console","1","200 K","Running","user","0:01:00","Unity Project Recovery"',
      '"Unity.exe","41001","Console","1","200 K","Running","user","0:01:00","Unity Crash Recovery"',
    ].join("\n"), calls);

    const result = respondToUnityRecoveryPrompt({ action: "discard_recovery", dry_run: false }, { execFileSync });

    expect(result).toMatchObject({
      action: "do_not_restore",
      action_alias: "discard_recovery",
      dry_run: false,
      target_pids: [41000, 41001],
      clicked_count: 0,
      errors: ["explicit_single_pid_required"],
    });
    expect(result.skipped).toEqual([
      { pid: 41000, reason: "explicit_single_pid_required" },
      { pid: 41001, reason: "explicit_single_pid_required" },
    ]);
    expect(calls.some((call) => call.file.toLowerCase().endsWith("powershell.exe") && call.args.join(" ").includes("$targetPids"))).toBe(false);
  });

  it("does not target normal Unity windows requested by pid", () => {
    const calls: Array<{ file: string; args: string[] }> = [];
    const execFileSync = fakeExecFileSync('"Unity.exe","41050","Console","1","200 K","Running","user","0:01:00","MyGame - SampleScene - Unity 6000.0"', calls);

    const result = respondToUnityRecoveryPrompt({ action: "do_not_restore", pids: [41050], dry_run: false }, { execFileSync });

    expect(result).toMatchObject({
      dry_run: false,
      target_pids: [],
      clicked_count: 0,
      skipped: [{ pid: 41050, reason: "not_active_unity_recovery_prompt" }],
      errors: ["no_recovery_prompt"],
    });
    expect(calls.some((call) => call.file.toLowerCase().endsWith("powershell.exe") && call.args.join(" ").includes("$targetPids"))).toBe(false);
  });
});

function fakeExecFileSync(tasklistOutput: string, calls: Array<{ file: string; args: string[] }>, powershellOutput: string | string[] = ""): ExecFileSyncLike {
  const powershellOutputs = Array.isArray(powershellOutput) ? [...powershellOutput] : undefined;
  return (file, args) => {
    calls.push({ file, args });
    if (file.endsWith("tasklist.exe")) {
      if (args.includes("IMAGENAME eq Unity")) return "INFO: No tasks are running which match the specified criteria.";
      return tasklistOutput || "INFO: No tasks are running which match the specified criteria.";
    }
    if (file.toLowerCase().endsWith("powershell.exe") && powershellOutputs && powershellOutputs.length > 0 && powershellOutputs[0]) {
      const output = powershellOutputs.shift() ?? "";
      return args.join(" ").includes("SGGUnityNativeModalUser32") ? withAuthoritativeNativeCoverage(output) : output;
    }
    if (file.toLowerCase().endsWith("powershell.exe") && typeof powershellOutput === "string" && powershellOutput) {
      return args.join(" ").includes("SGGUnityNativeModalUser32") ? withAuthoritativeNativeCoverage(powershellOutput) : powershellOutput;
    }
    const error = new Error("ENOENT");
    (error as { code?: string }).code = "ENOENT";
    throw error;
  };
}

function withAuthoritativeNativeCoverage(output: string): string {
  try {
    const rows = JSON.parse(output) as unknown;
    if (!Array.isArray(rows)) return output;
    return JSON.stringify({
      rows,
      user32_scan_succeeded: true,
      user32_scan_complete: true,
      user32_candidate_count: rows.length,
      uia_scan_succeeded: false,
      uia_scan_status: "not_required",
    });
  } catch {
    return output;
  }
}

function duplicateProjectModalWindow() {
  const body = "It looks like another Unity instance is running with this project open! Multiple Unity instances cannot open the same project. Project: C:\\Work\\Unity\\road-to-ithaka";
  return {
    hwnd: "0x500500",
    pid: 6636,
    process_name: "Unity",
    process_start_utc: "2026-08-18T13:00:00.000Z",
    window_thread_id: 1700,
    class_name: "#32770",
    window_title: "Error!",
    child_texts: [body, "OK"],
    child_controls: [
      { hwnd: "0x500509", pid: 6636, window_thread_id: 1700, visible: true, enabled: true, class_name: "Static", text: body },
      { hwnd: "0x500510", pid: 6636, window_thread_id: 1700, visible: true, enabled: true, class_name: "Button", text: "OK" },
    ],
  };
}

function scopedRegistryModalWindow(): typeof scopedRegistryFixture.row {
  return structuredClone(scopedRegistryFixture.row);
}

function sceneBackupRecoveryModalWindow(): typeof sceneBackupRecoveryFixture.row {
  return structuredClone(sceneBackupRecoveryFixture.row);
}

function sceneBackupDetectedModalWindow(): typeof sceneBackupDetectedFixture.row {
  return structuredClone(sceneBackupDetectedFixture.row);
}

function sceneBackupDetectedStatus(rows: unknown[] = [sceneBackupDetectedModalWindow()], envelope: Record<string, unknown> = {}): UnityWindowDiagnosticsStatus {
  return inspectUnityWindowDiagnostics({ execFileSync: fakeExecFileSync("", [], JSON.stringify({
    rows,
    user32_scan_succeeded: true,
    user32_scan_complete: true,
    user32_candidate_count: rows.length,
    uia_scan_succeeded: false,
    uia_scan_status: "not_required",
    ...envelope,
  })) });
}

function sceneBackupKeepArgs(): UnitySceneBackupKeepArgs {
  return { pid: 21901, process_start_utc: "2026-10-07T05:00:00.0000000Z", canonical_project_root: "c:/test/scene-backup" };
}

function lowDiskSpaceModalWindow(): typeof lowDiskSpaceFixture.row {
  return structuredClone(lowDiskSpaceFixture.row);
}

function projectDowngradeBody(): string[] {
  return [
    "The source project version 6000.0.30f1 is newer.",
    "The target editor version 6000.0.20f1 is older.",
    "Downgrading this project is not supported.",
    "Continuing with Continue will downgrade the project.",
    "All assets will reimport.",
    "Packages and settings may also be downgraded.",
  ];
}

function projectDowngradeWindow(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  const body = Array.isArray(overrides.body) ? overrides.body as string[] : projectDowngradeBody();
  const buttons = Array.isArray(overrides.buttons) ? overrides.buttons : [
    { hwnd: "0x700710", class_name: "Button", text: "Continue" },
    { hwnd: "0x700711", class_name: "Button", text: "Quit" },
  ];
  const base = {
    hwnd: "0x700700",
    pid: 26000,
    process_name: "Unity",
    process_start_utc: "2026-07-15T09:00:00.000Z",
    window_thread_id: 1700,
    class_name: "#32770",
    window_title: "Project Downgrade Required",
    child_texts: [...body, ...buttons.map((button) => (button as { text?: string }).text ?? "")],
    child_controls: [
      ...body.map((text, index) => ({ hwnd: `0x7008${index}`, class_name: "Static", text })),
      ...buttons,
    ],
  };
  const { body: _body, buttons: _buttons, ...rest } = overrides;
  return { ...base, ...rest };
}

function projectDowngradeArgs(evidence: UnityProjectDowngradeEvidence): Record<string, unknown> {
  return {
    evidence_id: evidence.evidence_id,
    canonical_project_root: "c:/u/game",
    confirm_continue: true,
    pid: evidence.pid,
    process_name: evidence.process_name,
    process_start_utc: evidence.process_start_utc,
    hwnd: evidence.hwnd,
    window_thread_id: evidence.window_thread_id,
    class_name: evidence.class_name,
    window_title: evidence.window_title,
    source_version: evidence.source_version,
    target_version: evidence.target_version,
    body_sha256: evidence.body_sha256,
    button_count: evidence.buttons.count,
    continue_button_hwnd: evidence.buttons.continue_hwnd,
    quit_button_hwnd: evidence.buttons.quit_hwnd,
  };
}

function responseReaderModalWindow(kind: ResponseReaderKind): typeof scopedRegistryFixture.row {
  if (kind === "scoped_registry_close") return scopedRegistryModalWindow();
  const safeMode = kind === "safe_mode_enter";
  const downgrade = kind === "project_downgrade_continue";
  const row = {
    ...scopedRegistryModalWindow(),
    hwnd: safeMode ? "0x130E50" : downgrade ? "0x700700" : "0x450450",
    pid: safeMode ? 8300 : downgrade ? 26000 : 24683,
    process_start_utc: "2026-09-08T11:07:48.3912806Z",
    window_thread_id: 1700,
    window_title: safeMode ? "Enter Safe Mode?" : downgrade ? "Project Downgrade Required" : "The open scene(s) have been modified externally",
  };
  const body = safeMode ? [""] : downgrade ? projectDowngradeBody() : ["The following open scene(s) have been changed on disk:\r\nAssets/Scenes/主菜单_Ёжик_Δοκιμή_🚀.unity\r\n\r\nDo you want to reload the scene(s)?"];
  const labels = safeMode ? ["Enter Safe Mode", "Ignore", "Quit"] : downgrade ? ["Continue", "Quit"] : ["Reload", "Ignore"];
  const controls = [...body.map((text) => ({ class_name: downgrade ? "Static" : "Edit", text })), ...labels.map((text) => ({ class_name: "Button", text }))];
  return {
    ...row,
    child_texts: [...body.filter(Boolean), ...labels],
    child_controls: controls.map((control, index) => ({
      ...control,
      hwnd: `0x8000${index}`,
      parent_hwnd: row.hwnd,
      pid: row.pid,
      window_thread_id: row.window_thread_id,
      visible: true,
      enabled: true,
      control_id: 100 + index,
    })),
  };
}

// Captures the real emitted response via an injected executor; it never invokes Windows or Unity.
function createResponseReaderHarness(kind: ResponseReaderKind, helperOutput?: Record<string, unknown>) {
  const row = responseReaderModalWindow(kind);
  const responseCalls: Array<{ file: string; args: string[] }> = [];
  const messageSubmitted = kind === "safe_mode_enter" || kind === "project_downgrade_continue";
  const output = JSON.stringify(helperOutput ?? { status: messageSubmitted ? "message_submitted" : "message_sent", button_text: kind === "reload" ? "Reload" : kind === "ignore" ? "Ignore" : "Continue", reload_count: 1, ignore_count: 1 });
  const runtime = createUnityWindowDiagnosticsRuntime({
    now: () => new Date("2026-09-10T10:00:00.000Z"),
    execFileSync: (file, args) => {
      if (file.endsWith("tasklist.exe")) return "INFO: No tasks are running which match the specified criteria.";
      if (args.at(-1)?.includes("SGGUnityNativeModalUser32")) return withAuthoritativeNativeCoverage(JSON.stringify([row]));
      responseCalls.push({ file, args });
      return output;
    },
  });
  if (kind === "reload" || kind === "ignore") {
    runtime.status();
    const evidence = runtime.externalSceneReloadEvidence!(row.pid);
    if (!evidence) throw new Error(`Expected ${kind} reader fixture evidence`);
    const args = { action: kind, pid: row.pid, hwnd: row.hwnd, evidence_id: evidence.evidence_id };
    return { row, responseCalls, evidence, apply: () => runtime.respondExternalSceneReload!(args) };
  }
  if (kind === "project_downgrade_continue") {
    runtime.status();
    const evidence = runtime.projectDowngradeEvidence!(row.pid, "c:/u/game");
    if (!evidence) throw new Error("Expected downgrade reader fixture evidence");
    const args = projectDowngradeArgs(evidence);
    return { row, responseCalls, evidence, apply: () => runtime.continueProjectDowngrade!(args) };
  }
  const baseArgs = { pid: row.pid, process_start_utc: row.process_start_utc, project_identity_sha256: `sha256:${createHash("sha256").update("c:/u/game").digest("hex")}` };
  if (kind === "safe_mode_enter") {
    const evidence = runtime.planSafeModeEnter!(baseArgs);
    if (!evidence.precondition_hash) throw new Error("Expected Safe Mode reader fixture precondition");
    const args = { ...baseArgs, dry_run: false, confirm_enter_safe_mode: true, precondition_hash: evidence.precondition_hash };
    return { row, responseCalls, evidence, apply: () => runtime.enterSafeMode!(args) };
  }
  const evidence = runtime.planScopedRegistryImportClose!(baseArgs);
  if (!evidence.precondition_hash) throw new Error("Expected scoped registry reader fixture precondition");
  const args = { ...baseArgs, dry_run: false, confirm_close: true, precondition_hash: evidence.precondition_hash };
  return { row, responseCalls, evidence, apply: () => runtime.closeScopedRegistryImport!(args) };
}
