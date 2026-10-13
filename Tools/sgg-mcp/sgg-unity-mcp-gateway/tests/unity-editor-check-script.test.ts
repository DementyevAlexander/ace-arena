import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { spawnSync } from "node:child_process";
import { EventEmitter } from "node:events";
import { pathToFileURL } from "node:url";

import { afterEach, describe, expect, it, vi } from "vitest";

const scriptPath = path.join(process.cwd(), "scripts", "check-unity-editor.mjs");
const matrixScriptPath = path.join(process.cwd(), "scripts", "check-unity-editor-matrix.mjs");

describe("Unity editor compile gate script", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    vi.useRealTimers();
  });

  it("skips when Unity environment variables are absent", () => {
    const env = { ...process.env };
    delete env.UNITY_EXE;
    delete env.SGG_UNITY_TEST_PROJECT;
    env.SGG_UNITY_EDITOR_ENV_FILE = "off";

    const result = spawnSync(process.execPath, [scriptPath], { env, encoding: "utf8" });

    expect(result.status).toBe(0);
    expect(result.stdout).toContain("skipped: set UNITY_EXE or SGG_UNITY_EDITOR_VERSION");
  }, 15000);

  it("fails in required mode when Unity environment variables are absent", () => {
    const env = { ...process.env };
    delete env.UNITY_EXE;
    delete env.SGG_UNITY_TEST_PROJECT;
    env.SGG_UNITY_EDITOR_ENV_FILE = "off";

    const result = spawnSync(process.execPath, [scriptPath, "--required"], { env, encoding: "utf8" });

    expect(result.status).toBe(1);
    expect(result.stderr).toContain("required validation unavailable");
    expect(result.stderr).toContain("SGG_UNITY_TEST_PROJECT");
  }, 15000);

  it("flags compile errors and package obsolete API warnings", async () => {
    const { analyzeUnityEditorLog } = await import(pathToFileURL(scriptPath).href);

    expect(analyzeUnityEditorLog("Assets/Foo.cs(1,1): error CS0246: missing type", { scriptAssemblyExists: true })).toMatchObject({
      ok: false,
      message: "Unity log contains C# compiler errors",
    });
    expect(analyzeUnityEditorLog("Packages/com.sungeargames.unity-mcp/Editor/Foo.cs(1,1): warning CS0618: obsolete", { scriptAssemblyExists: true })).toMatchObject({
      ok: false,
      message: "Unity package compile produced obsolete API warning CS0618",
    });
    expect(analyzeUnityEditorLog("Bee finished SGG.UnityMCP.Editor.dll", { scriptAssemblyExists: false })).toMatchObject({ ok: true });
  }, 15000);

  it("can require a local file package reference for the smoke project", async () => {
    const { validateLocalPackageReference } = await import(pathToFileURL(scriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-check-"));
    const projectPath = path.join(tempRoot, "Project");
    const expectedPackage = path.join(tempRoot, "repo", "unity", "Packages", "com.sungeargames.unity-mcp");
    fs.mkdirSync(path.join(projectPath, "Packages"), { recursive: true });
    fs.mkdirSync(expectedPackage, { recursive: true });
    fs.writeFileSync(
      path.join(projectPath, "Packages", "manifest.json"),
      JSON.stringify({ dependencies: { "com.sungeargames.unity-mcp": `file:${path.relative(path.join(projectPath, "Packages"), expectedPackage)}` } }),
    );

    expect(validateLocalPackageReference(projectPath, expectedPackage, true)).toMatchObject({ ok: true });
    expect(validateLocalPackageReference(projectPath, path.join(tempRoot, "other"), true)).toMatchObject({
      ok: false,
    });
    expect(validateLocalPackageReference(projectPath, expectedPackage, false)).toMatchObject({ ok: true });
  }, 15000);

  it("loads Unity editor validation variables from a local env file", async () => {
    const { loadLocalUnityEditorEnv } = await import(pathToFileURL(scriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-env-"));
    fs.writeFileSync(
      path.join(tempRoot, ".env.local"),
      [
        "UNITY_EXE=/Unity/Editor/Unity.exe",
        "SGG_UNITY_EDITOR_VERSION=6000.6",
        "SGG_UNITY_EDITOR_ROOTS=/Unity/Hub/Editor",
        "SGG_UNITY_EDITOR_TIMEOUT_MS=12345",
        "SGG_UNITY_TEST_PROJECT=/tmp/TestProject",
        "SGG_UNITY_REQUIRE_LOCAL_PACKAGE=true",
        "IGNORED_SECRET=value",
      ].join("\n"),
    );

    expect(loadLocalUnityEditorEnv({}, tempRoot)).toMatchObject({
      UNITY_EXE: "/Unity/Editor/Unity.exe",
      SGG_UNITY_EDITOR_VERSION: "6000.6",
      SGG_UNITY_EDITOR_ROOTS: "/Unity/Hub/Editor",
      SGG_UNITY_EDITOR_TIMEOUT_MS: "12345",
      SGG_UNITY_TEST_PROJECT: "/tmp/TestProject",
      SGG_UNITY_REQUIRE_LOCAL_PACKAGE: "true",
    });
    expect(loadLocalUnityEditorEnv({}, tempRoot)).not.toHaveProperty("IGNORED_SECRET");
    expect(loadLocalUnityEditorEnv({ UNITY_EXE: "/override/Unity.exe" }, tempRoot)).toMatchObject({ UNITY_EXE: "/override/Unity.exe" });
  });

  it("converts WSL mount paths for Windows Unity Editor arguments", async () => {
    const { toLocalFsPath, toUnityEditorFsPath } = await import(pathToFileURL(scriptPath).href);

    expect(toUnityEditorFsPath("/mnt/c/Work/Unity/Project")).toBe("C:\\Work\\Unity\\Project");
    expect(toUnityEditorFsPath("C:\\Work\\Unity\\Project")).toBe("C:\\Work\\Unity\\Project");
    expect(toLocalFsPath("C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.10f1\\Editor\\Unity.exe", { hostPlatform: "win32" })).toBe("C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.10f1\\Editor\\Unity.exe");
    expect(toLocalFsPath("C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.10f1\\Editor\\Unity.exe", { hostPlatform: "linux" })).toBe("/mnt/c/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe");
    expect(toLocalFsPath("C:/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe", { hostPlatform: "linux" })).toBe("/mnt/c/Program Files/Unity/Hub/Editor/6000.4.10f1/Editor/Unity.exe");
    expect(toLocalFsPath("C:\\Program Files\\Unity\\Editor\\Unity.exe", { hostPlatform: "linux", wslMountRoot: "/host" })).toBe("/host/c/Program Files/Unity/Editor/Unity.exe");
    expect(toUnityEditorFsPath("/host/c/Work/Unity/Project", { wslMountRoot: "/host" })).toBe("C:\\Work\\Unity\\Project");
  });

  it("parses Unity editor timeout configuration", async () => {
    const { defaultUnityEditorTimeoutMs, defaultUnityStartupStallTimeoutMs, parseUnityEditorTimeoutMs, parseUnityStartupStallTimeoutMs } = await import(pathToFileURL(scriptPath).href);

    expect(parseUnityEditorTimeoutMs({})).toMatchObject({ ok: true, timeoutMs: defaultUnityEditorTimeoutMs });
    expect(parseUnityEditorTimeoutMs({ SGG_UNITY_EDITOR_TIMEOUT_MS: "1000" })).toMatchObject({ ok: true, timeoutMs: 1000 });
    expect(parseUnityEditorTimeoutMs({ SGG_UNITY_EDITOR_TIMEOUT_MS: "0" })).toMatchObject({ ok: false });
    expect(parseUnityEditorTimeoutMs({ SGG_UNITY_EDITOR_TIMEOUT_MS: "1.5" })).toMatchObject({ ok: false });
    expect(parseUnityEditorTimeoutMs({ SGG_UNITY_EDITOR_TIMEOUT_MS: "nope" })).toMatchObject({ ok: false });
    expect(parseUnityStartupStallTimeoutMs({})).toMatchObject({ ok: true, timeoutMs: defaultUnityStartupStallTimeoutMs });
    expect(parseUnityStartupStallTimeoutMs({ SGG_UNITY_STARTUP_STALL_TIMEOUT_MS: "5000" })).toMatchObject({ ok: true, timeoutMs: 5000 });
    expect(parseUnityStartupStallTimeoutMs({ SGG_UNITY_STARTUP_STALL_TIMEOUT_MS: "0" })).toMatchObject({ ok: false });
  });

  it("classifies Unity startup stall log tails", async () => {
    const { classifyUnityStartupLogTail } = await import(pathToFileURL(scriptPath).href);

    expect(classifyUnityStartupLogTail("Licensing is initialized\nLibrary Redirect Path: Library/")).toMatchObject({
      phase: "library_redirect",
      evidence: "Library Redirect Path: Library/",
    });
    expect(classifyUnityStartupLogTail("LibraryInitializing\nArtifactDB lock is still present")).toMatchObject({
      phase: "library_initializing",
    });
    expect(classifyUnityStartupLogTail("normal compile output")).toBeNull();
  });

  it("detects Unity Licensing Client native application error window snapshots", async () => {
    const { inspectUnityNativeBlockingModals, isUnityLicensingClientCrashModal, parseNativeUnityModalWindows } = await import(pathToFileURL(scriptPath).href);
    const nativeWindow = {
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
    };

    expect(isUnityLicensingClientCrashModal(nativeWindow)).toBe(true);
    expect(parseNativeUnityModalWindows(JSON.stringify([nativeWindow]))).toEqual([nativeWindow]);
    expect(inspectUnityNativeBlockingModals({ execFileSync: () => JSON.stringify([nativeWindow]) })).toMatchObject({
      supported: true,
      blocking: true,
      code: "unity_licensing_client_crash_modal",
      candidates: [nativeWindow],
    });
  });

  it("requires every licensing crash criterion for English and localized snapshots", async () => {
    const { isUnityLicensingClientCrashModal } = await import(pathToFileURL(scriptPath).href);
    const englishWindow = {
      hwnd: "0x110E53",
      pid: 1673,
      process_name: "csrss",
      class_name: "#32770",
      window_title: "Unity.Licensing.Client.exe - Application Error",
      child_texts: ["OK", "Cancel", "The exception unknown software exception (0xe0434352) occurred in the application at location 0x00007FFFC6431C0A."],
    };
    const localizedWindow = {
      ...englishWindow,
      window_title: "Unity.Licensing.Client.exe - Ошибка приложения",
      child_texts: ["ОК", "Отмена", "Исключение неизвестное программное исключение (0xe0434352) в приложении по адресу 0x00007FFE6BA41B6A."],
    };

    expect(isUnityLicensingClientCrashModal(englishWindow)).toBe(true);
    expect(isUnityLicensingClientCrashModal(localizedWindow)).toBe(true);

    const message = englishWindow.child_texts[2] ?? "";
    const incomplete = [
      { ...englishWindow, class_name: "UnityContainerWndClass" },
      { ...englishWindow, window_title: "Other.exe - Application Error" },
      { ...englishWindow, window_title: "Unity.Licensing.Client.exe - Unexpected failure" },
      { ...englishWindow, child_texts: ["OK", "Cancel", message.replace("unknown software exception", "access violation")] },
      { ...englishWindow, child_texts: ["OK", "Cancel", message.replace("0xe0434352", "0xc0000005")] },
      { ...englishWindow, child_texts: ["OK", "Cancel", message.replace("0x00007FFFC6431C0A", "0xe0434352")] },
      { ...englishWindow, child_texts: englishWindow.child_texts.filter((text) => text !== "OK") },
      { ...englishWindow, child_texts: englishWindow.child_texts.filter((text) => text !== "Cancel") },
    ];
    for (const window of incomplete) {
      expect(isUnityLicensingClientCrashModal(window)).toBe(false);
    }
  });

  it("detects Corrupted Library native modal snapshots", async () => {
    const { inspectUnityNativeBlockingModals, isCorruptedLibraryModal, parseNativeUnityModalWindows } = await import(pathToFileURL(scriptPath).href);
    const nativeWindow = {
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
    };

    expect(isCorruptedLibraryModal(nativeWindow)).toBe(true);
    expect(parseNativeUnityModalWindows(JSON.stringify([nativeWindow]))).toEqual([nativeWindow]);
    expect(inspectUnityNativeBlockingModals({ execFileSync: () => JSON.stringify([nativeWindow]) })).toMatchObject({
      supported: true,
      blocking: true,
      code: "blocked_corrupted_library",
      candidates: [nativeWindow],
    });
  });

  it("detects only the fully bound duplicate-project Error dialog", async () => {
    const { duplicateProjectInstanceModalCode, inspectUnityNativeBlockingModals, isDuplicateProjectInstanceModal } = await import(pathToFileURL(scriptPath).href);
    const body = "It looks like another Unity instance is running with this project open! Multiple Unity instances cannot open the same project. Project: C:\\Work\\Unity\\road-to-ithaka";
    const nativeWindow = {
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

    expect(duplicateProjectInstanceModalCode).toBe("blocked_duplicate_project_instance");
    expect(isDuplicateProjectInstanceModal(nativeWindow)).toBe(true);
    expect(inspectUnityNativeBlockingModals({ execFileSync: () => JSON.stringify([nativeWindow]) })).toMatchObject({
      supported: true,
      blocking: true,
      code: "blocked_duplicate_project_instance",
      candidates: [nativeWindow],
    });

    const button = nativeWindow.child_controls[1]!;
    for (const incomplete of [
      { ...nativeWindow, process_name: "Other" },
      { ...nativeWindow, process_start_utc: "" },
      { ...nativeWindow, window_thread_id: 0 },
      { ...nativeWindow, class_name: "OtherWindowClass" },
      { ...nativeWindow, window_title: "Error" },
      { ...nativeWindow, child_texts: [body.replace("another Unity instance", "a Unity process"), "OK"] },
      { ...nativeWindow, child_texts: [body.replace("Multiple Unity instances cannot open the same project.", "Only one Editor is recommended."), "OK"] },
      { ...nativeWindow, child_texts: [body.replace("C:\\Work\\Unity\\road-to-ithaka", "road-to-ithaka"), "OK"] },
      { ...nativeWindow, child_controls: [{ ...button, enabled: false }] },
      { ...nativeWindow, child_controls: [{ ...button, pid: 9999 }] },
      { ...nativeWindow, child_controls: [{ ...button, window_thread_id: 1701 }] },
      { ...nativeWindow, child_controls: [{ ...nativeWindow.child_controls[0]!, pid: 9999 }, button] },
      { ...nativeWindow, child_controls: [button, { ...button, hwnd: "0x500511", text: "Cancel" }] },
    ]) {
      expect(isDuplicateProjectInstanceModal(incomplete)).toBe(false);
    }
  });

  it("detects Missing Signature package native modal snapshots", async () => {
    const { inspectUnityNativeBlockingModals, isMissingSignatureModal, parseNativeUnityModalWindows } = await import(pathToFileURL(scriptPath).href);
    const nativeWindow = {
      hwnd: "0x400404",
      pid: 24682,
      process_name: "Unity",
      class_name: "UnityContainerWndClass",
      window_title: "Missing Signature",
      child_texts: [
        "UnityEditor.PackageManager.UI.Internal.ModalWindowContainer",
      ],
    };

    expect(isMissingSignatureModal(nativeWindow)).toBe(true);
    expect(parseNativeUnityModalWindows(JSON.stringify([nativeWindow]))).toEqual([nativeWindow]);
    expect(inspectUnityNativeBlockingModals({ execFileSync: () => JSON.stringify([nativeWindow]) })).toMatchObject({
      supported: true,
      blocking: true,
      code: "blocked_missing_signature_packages",
      candidates: [nativeWindow],
    });
  });

  it("detects only fully evidenced external scene reload modal snapshots", async () => {
    const { externalSceneReloadModalCode, inspectUnityNativeBlockingModals, isExternalSceneReloadModal, runUnityEditorValidation } = await import(pathToFileURL(scriptPath).href);
    const nativeWindow = {
      hwnd: "0x450450",
      pid: 24683,
      process_name: "Unity",
      class_name: "#32770",
      window_title: "The open scene(s) have been modified externally",
      child_texts: ["The following open scene(s) have been changed on disk:", "Assets/Scenes/Main.unity", "Do you want to reload the scene(s)?", "Reload", "Ignore"],
    };

    expect(externalSceneReloadModalCode).toBe("blocked_external_scene_reload");
    expect(isExternalSceneReloadModal(nativeWindow)).toBe(true);
    expect(inspectUnityNativeBlockingModals({ execFileSync: () => JSON.stringify([nativeWindow]) })).toMatchObject({
      blocking: true,
      code: "blocked_external_scene_reload",
      candidates: [nativeWindow],
    });

    const omissions = [
      { ...nativeWindow, window_title: "Reload changed files?" },
      ...nativeWindow.child_texts.map((_text, index) => ({ ...nativeWindow, child_texts: nativeWindow.child_texts.filter((_, childIndex) => childIndex !== index) })),
    ];
    for (const incomplete of omissions) {
      expect(isExternalSceneReloadModal(incomplete)).toBe(false);
    }
    expect(isExternalSceneReloadModal({ ...nativeWindow, child_texts: ["Reload", "Ignore"] })).toBe(false);

    const spawnMock = vi.fn();
    const stderrMock = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const status = await runUnityEditorValidation(
      { UNITY_EXE: "/Unity/Editor/Unity.exe", SGG_UNITY_TEST_PROJECT: os.tmpdir(), SGG_UNITY_EDITOR_ENV_FILE: "off" },
      process.cwd(),
      {
        spawn: spawnMock,
        sameProjectEditorOpenInspector: () => ({ blocking: false }),
        nativeModalScanner: () => ({ supported: true, blocking: true, code: externalSceneReloadModalCode, candidates: [nativeWindow], windows: [nativeWindow], errors: [] }),
      },
    );
    const stderr = stderrMock.mock.calls.map((call) => call.join(" ")).join("\n");
    expect(status).toBe(1);
    expect(spawnMock).not.toHaveBeenCalled();
    expect(stderr).toContain("blocked by blocked_external_scene_reload during preflight");
    expect(stderr).toContain("hwnd=0x450450 pid=24683");
    expect(stderr).toContain("does not click either button and does not retry write commands");
  });

  it("detects only the exact report-backed sparse external-scene topology", async () => {
    const { externalSceneReloadModalCode, inspectUnityNativeBlockingModals, isExternalSceneReloadModal, parseNativeUnityModalWindows } = await import(pathToFileURL(scriptPath).href);
    const sparseWindow = {
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

    expect(isExternalSceneReloadModal(sparseWindow)).toBe(true);
    const parsed = parseNativeUnityModalWindows(JSON.stringify([sparseWindow]));
    expect(parsed).toEqual([sparseWindow]);
    expect(inspectUnityNativeBlockingModals({ execFileSync: () => JSON.stringify([sparseWindow]) })).toMatchObject({
      supported: true,
      blocking: true,
      code: externalSceneReloadModalCode,
      candidates: [sparseWindow],
    });
    expect(isExternalSceneReloadModal({ ...sparseWindow, window_enabled: false })).toBe(false);
    expect(isExternalSceneReloadModal({ ...sparseWindow, child_controls: sparseWindow.child_controls.slice(0, 1) })).toBe(false);
    expect(isExternalSceneReloadModal({ ...sparseWindow, child_controls: [{ ...sparseWindow.child_controls[0], parent_hwnd: "0xBAD" }, sparseWindow.child_controls[1]] })).toBe(false);
    expect(isExternalSceneReloadModal({ ...sparseWindow, child_controls: [...sparseWindow.child_controls, { ...sparseWindow.child_controls[0], hwnd: "0xDEADBEEF" }] })).toBe(false);
  });

  it("detects only the exact fully evidenced project downgrade startup prompt without responding", async () => {
    const { inspectUnityNativeBlockingModals, isProjectDowngradeModal, parseNativeUnityModalWindows, projectDowngradeModalCode, runUnityEditorValidation } = await import(pathToFileURL(scriptPath).href);
    const body = [
      "The source project version 6000.5.3f1 is newer.",
      "The target editor version 6000.4.10f1 is older.",
      "Downgrading this project is not supported.",
      "Continuing with Continue will downgrade the project.",
      "All assets will reimport.",
      "Packages and settings may also be downgraded.",
    ];
    const sensitiveText = "Project path C:/secret/private-game";
    const buttons = [{ hwnd: "0x700710", class_name: "Button", text: "Continue" }, { hwnd: "0x700711", class_name: "Button", text: "Quit" }];
    const nativeWindow = {
      hwnd: "0x700700",
      pid: 26000,
      process_name: "Unity",
      class_name: "#32770",
      window_title: "Project Downgrade Required",
      child_texts: [...body, sensitiveText, "Continue", "Quit"],
      child_controls: [...body.map((text, index) => ({ hwnd: `0x7008${index}`, class_name: "Static", text })), { hwnd: "0x700899", class_name: "Static", text: sensitiveText }, ...buttons],
    };

    expect(projectDowngradeModalCode).toBe("blocked_project_downgrade_prompt");
    expect(isProjectDowngradeModal(nativeWindow)).toBe(true);
    expect(inspectUnityNativeBlockingModals({ execFileSync: () => JSON.stringify([nativeWindow]) })).toMatchObject({ blocking: true, code: "blocked_project_downgrade_prompt" });
    const variants = [
      { ...nativeWindow, window_title: "project downgrade required" },
      { ...nativeWindow, class_name: "OtherDialog" },
      { ...nativeWindow, process_name: "Unity Hub" },
      ...body.map((_text, index) => ({ ...nativeWindow, child_controls: [...body.filter((_, bodyIndex) => bodyIndex !== index).map((text, bodyIndex) => ({ hwnd: `0x7018${bodyIndex}`, class_name: "Static", text })), ...buttons] })),
      { ...nativeWindow, child_controls: [...body.map((text, index) => ({ hwnd: `0x7028${index}`, class_name: "Static", text: text.replace("6000.5.3f1", "6000.4.10f1") })), ...buttons] },
      { ...nativeWindow, child_controls: [...body.map((text, index) => ({ hwnd: `0x7038${index}`, class_name: "Static", text })), ...buttons, { hwnd: "0x700712", class_name: "Button", text: "Help" }] },
      { ...nativeWindow, child_controls: [...body.map((text, index) => ({ hwnd: `0x7048${index}`, class_name: "Static", text: text.replace(" is newer", "").replace(" is older", "") })), { hwnd: "0x704899", class_name: "Static", text: "Unrelated newer and older wording." }, ...buttons] },
      { ...nativeWindow, child_controls: [...body.filter((text) => !text.startsWith("Packages")).map((text, index) => ({ hwnd: `0x7058${index}`, class_name: "Static", text })), { hwnd: "0x705890", class_name: "Static", text: "Packages are present." }, { hwnd: "0x705891", class_name: "Static", text: "Settings may also change." }, ...buttons] },
      { ...nativeWindow, child_controls: [...body.map((text, index) => ({ hwnd: `0x7068${index}`, class_name: "Static", text })), ...Array.from({ length: 57 }, (_, index) => ({ hwnd: `0x71${index.toString(16).padStart(2, "0")}`, class_name: "Static", text: "" })), ...buttons] },
    ];
    for (const variant of variants) expect(isProjectDowngradeModal(variant)).toBe(false);
    expect(parseNativeUnityModalWindows(JSON.stringify([variants.at(-1)]))[0].child_controls.length).toBeGreaterThan(64);

    const spawnMock = vi.fn();
    const stderrMock = vi.spyOn(console, "error").mockImplementation(() => undefined);
    const status = await runUnityEditorValidation(
      { UNITY_EXE: "/Unity/Editor/Unity.exe", SGG_UNITY_TEST_PROJECT: os.tmpdir(), SGG_UNITY_EDITOR_ENV_FILE: "off" },
      process.cwd(),
      { spawn: spawnMock, sameProjectEditorOpenInspector: () => ({ blocking: false }), nativeModalScanner: () => ({ supported: true, blocking: true, code: projectDowngradeModalCode, candidates: [nativeWindow], windows: [nativeWindow], errors: [] }) },
    );
    const stderr = stderrMock.mock.calls.map((call) => call.join(" ")).join("\n");
    expect(status).toBe(1);
    expect(spawnMock).not.toHaveBeenCalled();
    expect(stderr).toContain("blocked by blocked_project_downgrade_prompt during preflight");
    expect(stderr).toContain("source_version=6000.5.3f1 target_version=6000.4.10f1 button_count=2");
    expect(stderr).toContain("never clicks Continue or Quit and never infers downgrade completion");
    expect(stderr).not.toContain("C:/secret/private-game");
    expect(stderr).not.toContain("Packages and settings");
  });

  it("fails Unity validation before launch when a native licensing crash modal is already open", async () => {
    const { runUnityEditorValidation } = await import(pathToFileURL(scriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-native-modal-"));
    const spawnMock = vi.fn();
    const stderrMock = vi.spyOn(console, "error").mockImplementation(() => undefined);

    const status = await runUnityEditorValidation(
      {
        UNITY_EXE: "/Unity/Editor/Unity.exe",
        SGG_UNITY_TEST_PROJECT: tempRoot,
        SGG_UNITY_EDITOR_ENV_FILE: "off",
      },
      process.cwd(),
      {
        spawn: spawnMock,
        nativeModalScanner: () => ({
          supported: true,
          blocking: true,
          code: "unity_licensing_client_crash_modal",
          candidates: [{
            hwnd: "0x110E52",
            pid: 1672,
            process_name: "csrss",
            class_name: "#32770",
            window_title: "Unity.Licensing.Client.exe - Ошибка приложения",
            child_texts: ["Исключение неизвестное программное исключение (0xe0434352)"],
          }],
          windows: [],
          errors: [],
        }),
      },
    );

    expect(status).toBe(1);
    expect(spawnMock).not.toHaveBeenCalled();
    expect(stderrMock.mock.calls.map((call) => call.join(" ")).join("\n")).toContain("blocked by unity_licensing_client_crash_modal during preflight");
  });

  it("detects an already-open same-project Unity editor from gateway heartbeat records", async () => {
    const { inspectSameProjectEditorOpen } = await import(pathToFileURL(scriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-same-project-"));
    const instancesDir = path.join(tempRoot, "instances");
    fs.mkdirSync(instancesDir, { recursive: true });
    fs.writeFileSync(path.join(instancesDir, "game.json"), JSON.stringify({
      instance_id: "game-live",
      project: "game",
      display_name: "game",
      project_root: "C:\\Work\\Unity\\Game",
      canonical_project_root: "C:/WORK/UNITY/GAME",
      unity_pid: 24680,
      status: "ready",
      last_heartbeat: "2026-06-15T08:00:00.000Z",
    }));

    const result = inspectSameProjectEditorOpen("C:\\Work\\Unity\\Game", {
      env: { SGG_UNITY_MCP_INSTANCES_DIR: instancesDir },
      now: () => new Date("2026-06-15T08:00:10.000Z"),
      processExists: () => false,
      unityProcessScanner: () => ({ supported: true, processes: [], errors: [] }),
    });

    expect(result).toMatchObject({
      supported: true,
      blocking: true,
      code: "same_project_editor_open",
      checked_records_count: 1,
      matches: [{ instance_id: "game-live", block_reason: "live_heartbeat" }],
    });
  });

  it("parses Unity project paths from process command lines", async () => {
    const { parseUnityEditorProcessRows, parseUnityProjectPathFromCommandLine } = await import(pathToFileURL(scriptPath).href);

    expect(parseUnityProjectPathFromCommandLine('"C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.10f1\\Editor\\Unity.exe" -projectpath C:\\Work\\Unity\\sgg-perfmeter-hdrp-test -acceptSoftwareTermsForThisRunOnly')).toBe("C:\\Work\\Unity\\sgg-perfmeter-hdrp-test");
    expect(parseUnityProjectPathFromCommandLine('"C:\\Program Files\\Unity\\Editor\\Unity.exe" -projectPath="C:\\Work\\Unity\\Game With Space" -batchmode')).toBe("C:\\Work\\Unity\\Game With Space");
    expect(parseUnityEditorProcessRows(JSON.stringify({
      process_id: 19740,
      process_name: "Unity.exe",
      command_line: '"C:\\Program Files\\Unity\\Editor\\Unity.exe" -projectpath C:\\Work\\Unity\\Game -useHub',
    }))).toEqual([expect.objectContaining({
      process_id: 19740,
      project_path: "C:\\Work\\Unity\\Game",
    })]);
  });

  it("detects an already-open same-project Unity editor from a live process command line", async () => {
    const { inspectSameProjectEditorOpen } = await import(pathToFileURL(scriptPath).href);

    const result = inspectSameProjectEditorOpen("C:\\Work\\Unity\\Game", {
      env: {},
      unityProcessScanner: () => ({
        supported: true,
        processes: [{
          process_id: 19740,
          process_name: "Unity.exe",
          command_line: '"C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.10f1\\Editor\\Unity.exe" -projectpath C:\\Work\\Unity\\Game -acceptSoftwareTermsForThisRunOnly',
        }],
        errors: [],
      }),
    });

    expect(result).toMatchObject({
      supported: true,
      blocking: true,
      code: "same_project_editor_open",
      checked_records_count: 0,
      checked_processes_count: 1,
      process_matches: [{ process_id: 19740, project_path: "C:\\Work\\Unity\\Game", block_reason: "unity_process_project_path_match" }],
    });
  });

  it("matches Windows and WSL aliases but does not block a different clone", async () => {
    const { inspectSameProjectEditorOpen } = await import(pathToFileURL(scriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-alias-project-"));
    const instancesDir = path.join(tempRoot, "instances");
    fs.mkdirSync(instancesDir, { recursive: true });
    fs.writeFileSync(path.join(instancesDir, "game.json"), JSON.stringify({
      instance_id: "game-live",
      project: "game",
      project_root: "C:\\Work\\Unity\\Game",
      aliases: [
        { environment: "windows", host_path: "C:\\Work\\Unity\\Game", canonical_path: "c:/Work/Unity/Game" },
        { environment: "wsl", host_path: "/mnt/c/Work/Unity/Game", canonical_path: "c:/Work/Unity/Game" },
      ],
      unity_pid: 24680,
      status: "ready",
      last_heartbeat: "2026-06-15T08:00:00.000Z",
    }));
    const options = {
      env: { SGG_UNITY_MCP_INSTANCES_DIR: instancesDir },
      now: () => new Date("2026-06-15T08:00:10.000Z"),
      processExists: () => false,
      unityProcessScanner: () => ({ supported: true, processes: [], errors: [] }),
    };

    expect(inspectSameProjectEditorOpen("/mnt/c/Work/Unity/Game", options)).toMatchObject({
      blocking: true,
      matches: [{ instance_id: "game-live" }],
    });
    expect(inspectSameProjectEditorOpen("C:\\Work\\Unity\\GameClone", options)).toMatchObject({
      blocking: false,
      matches: [],
    });
  });

  it("blocks stale records only while their Unity process is still alive", async () => {
    const { inspectSameProjectEditorOpen } = await import(pathToFileURL(scriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-stale-project-"));
    const instancesDir = path.join(tempRoot, "instances");
    const configDir = path.join(tempRoot, "config");
    fs.mkdirSync(instancesDir, { recursive: true });
    fs.mkdirSync(configDir, { recursive: true });
    fs.writeFileSync(path.join(configDir, "gateway.json"), JSON.stringify({
      registry: { instances_dir: instancesDir, stale_after_ms: 1000 },
    }));
    fs.writeFileSync(path.join(instancesDir, "game.json"), JSON.stringify({
      instance_id: "game-stale",
      project: "game",
      project_root: "C:\\Work\\Unity\\Game",
      unity_pid: 24680,
      status: "ready",
      last_heartbeat: "2026-06-15T07:50:00.000Z",
    }));
    const baseOptions = {
      env: { SGG_UNITY_MCP_CONFIG: path.join(configDir, "gateway.json") },
      now: () => new Date("2026-06-15T08:00:00.000Z"),
      unityProcessScanner: () => ({ supported: true, processes: [], errors: [] }),
    };

    expect(inspectSameProjectEditorOpen("C:\\Work\\Unity\\Game", { ...baseOptions, processExists: () => true })).toMatchObject({
      blocking: true,
      registry_stale_after_ms: 1000,
      matches: [{ instance_id: "game-stale", stale: true, process_alive: true, block_reason: "stale_record_process_alive" }],
    });
    expect(inspectSameProjectEditorOpen("C:\\Work\\Unity\\Game", { ...baseOptions, processExists: () => false })).toMatchObject({
      blocking: false,
      matches: [{ instance_id: "game-stale", stale: true, process_alive: false, block_reason: "stale_record_process_dead_or_missing" }],
    });
  });

  it("fails Unity validation before spawning batchmode when the same project is open", async () => {
    const { runUnityEditorValidation } = await import(pathToFileURL(scriptPath).href);
    const spawnMock = vi.fn();
    const stderrMock = vi.spyOn(console, "error").mockImplementation(() => undefined);

    const status = await runUnityEditorValidation(
      {
        UNITY_EXE: "/Unity/Editor/Unity.exe",
        SGG_UNITY_TEST_PROJECT: "C:\\Work\\Unity\\Game",
        SGG_UNITY_EDITOR_ENV_FILE: "off",
      },
      process.cwd(),
      {
        spawn: spawnMock,
        sameProjectEditorOpenInspector: () => ({
          blocking: true,
          code: "same_project_editor_open",
          requested_project_path: "C:\\Work\\Unity\\Game",
          canonical_project_root: "c:/Work/Unity/Game",
          matches: [{ instance_id: "game-live", project: "game", unity_pid: 24680, status: "ready", stale: false, process_alive: true }],
        }),
      },
    );

    expect(status).toBe(1);
    expect(spawnMock).not.toHaveBeenCalled();
    expect(stderrMock.mock.calls.map((call) => call.join(" ")).join("\n")).toContain("blocked by same_project_editor_open");
  });

  it("fails Unity validation before spawning batchmode when a live Unity process already uses the same project", async () => {
    const { runUnityEditorValidation } = await import(pathToFileURL(scriptPath).href);
    const spawnMock = vi.fn();
    const stderrMock = vi.spyOn(console, "error").mockImplementation(() => undefined);

    const status = await runUnityEditorValidation(
      {
        UNITY_EXE: "/Unity/Editor/Unity.exe",
        SGG_UNITY_TEST_PROJECT: "C:\\Work\\Unity\\Game",
        SGG_UNITY_EDITOR_ENV_FILE: "off",
      },
      process.cwd(),
      {
        spawn: spawnMock,
        unityProcessScanner: () => ({
          supported: true,
          processes: [{
            process_id: 19740,
            process_name: "Unity.exe",
            command_line: '"C:\\Program Files\\Unity\\Hub\\Editor\\6000.4.10f1\\Editor\\Unity.exe" -projectpath C:\\Work\\Unity\\Game -useHub',
          }],
          errors: [],
        }),
      },
    );

    expect(status).toBe(1);
    expect(spawnMock).not.toHaveBeenCalled();
    expect(stderrMock.mock.calls.map((call) => call.join(" ")).join("\n")).toContain("unity process: pid=19740");
  });

  it("stops the spawned Unity validation process when a native licensing crash modal appears during wait", async () => {
    const { runUnityEditorProcess } = await import(pathToFileURL(scriptPath).href);
    const child = new EventEmitter() as EventEmitter & { pid: number; kill: ReturnType<typeof vi.fn>; unref: ReturnType<typeof vi.fn> };
    child.pid = 24680;
    child.kill = vi.fn(() => true);
    child.unref = vi.fn();
    const spawnMock = vi.fn(() => child);
    let scans = 0;
    vi.useFakeTimers();

    const resultPromise = runUnityEditorProcess(spawnMock, "/Unity/Editor/Unity.exe", ["-batchmode"], {
      timeoutMs: 10000,
      killGraceMs: 1,
      nativeModalPollIntervalMs: 100,
      nativeModalScanner: () => {
        scans += 1;
        return scans >= 2
          ? {
              supported: true,
              blocking: true,
              code: "unity_licensing_client_crash_modal",
              candidates: [{ hwnd: "0x110E52", pid: 1672, process_name: "csrss", class_name: "#32770", window_title: "Unity.Licensing.Client.exe - Ошибка приложения", child_texts: ["0xe0434352"] }],
              windows: [],
              errors: [],
            }
          : { supported: true, blocking: false, code: null, candidates: [], windows: [], errors: [] };
      },
    });
    await vi.advanceTimersByTimeAsync(200);
    const result = await resultPromise;

    expect(result).toMatchObject({
      blockedByNativeModal: true,
      signal: "native_modal_block",
      pid: 24680,
      nativeModal: { code: "unity_licensing_client_crash_modal" },
    });
    expect(child.kill).toHaveBeenCalledWith("SIGTERM");
    expect(child.unref).toHaveBeenCalled();
  });

  it("stops the spawned Unity validation process when a Corrupted Library modal appears during wait", async () => {
    const { runUnityEditorProcess } = await import(pathToFileURL(scriptPath).href);
    const child = new EventEmitter() as EventEmitter & { pid: number; kill: ReturnType<typeof vi.fn>; unref: ReturnType<typeof vi.fn> };
    child.pid = 24681;
    child.kill = vi.fn(() => true);
    child.unref = vi.fn();
    const spawnMock = vi.fn(() => child);
    vi.useFakeTimers();

    const resultPromise = runUnityEditorProcess(spawnMock, "/Unity/Editor/Unity.exe", ["-batchmode"], {
      timeoutMs: 10000,
      killGraceMs: 1,
      nativeModalPollIntervalMs: 100,
      nativeModalScanner: () => ({
        supported: true,
        blocking: true,
        code: "blocked_corrupted_library",
        candidates: [{
          hwnd: "0x200204",
          pid: 24680,
          process_name: "Unity",
          class_name: "#32770",
          window_title: "Corrupted Library Detected",
          child_texts: ["Corrupted files in the Library prevented Unity to load your project.", "Don't rebuild Library"],
        }],
        windows: [],
        errors: [],
      }),
    });
    await vi.advanceTimersByTimeAsync(100);
    const result = await resultPromise;

    expect(result).toMatchObject({
      blockedByNativeModal: true,
      signal: "native_modal_block",
      pid: 24681,
      nativeModal: { code: "blocked_corrupted_library" },
    });
    expect(child.kill).toHaveBeenCalledWith("SIGTERM");
    expect(child.unref).toHaveBeenCalled();
  });

  it("stops the spawned Unity validation process when startup log stalls before bridge", async () => {
    const { runUnityEditorProcess } = await import(pathToFileURL(scriptPath).href);
    const child = new EventEmitter() as EventEmitter & { pid: number; kill: ReturnType<typeof vi.fn>; unref: ReturnType<typeof vi.fn> };
    child.pid = 24682;
    child.kill = vi.fn(() => true);
    child.unref = vi.fn();
    const spawnMock = vi.fn(() => child);
    let scans = 0;
    vi.useFakeTimers();

    const resultPromise = runUnityEditorProcess(spawnMock, "/Unity/Editor/Unity.exe", ["-batchmode"], {
      timeoutMs: 10000,
      killGraceMs: 1,
      startupStallPollIntervalMs: 100,
      startupStallDetector: () => {
        scans += 1;
        return scans >= 2
          ? { blocking: true, code: "unity_startup_stalled_before_bridge", phase: "library_redirect", evidence: "Library Redirect Path: Library/", idle_ms: 5000, timeout_ms: 5000 }
          : { blocking: false, code: null };
      },
    });
    await vi.advanceTimersByTimeAsync(200);
    const result = await resultPromise;

    expect(result).toMatchObject({
      blockedByStartupStall: true,
      signal: "startup_stall",
      pid: 24682,
      startupStall: { code: "unity_startup_stalled_before_bridge", phase: "library_redirect" },
    });
    expect(child.kill).toHaveBeenCalledWith("SIGTERM");
    expect(child.unref).toHaveBeenCalled();
  });

  it("parses Unity editor matrix versions", async () => {
    const { defaultUnityEditorMatrixVersions, parseUnityEditorMatrixVersions } = await import(pathToFileURL(matrixScriptPath).href);

    expect(parseUnityEditorMatrixVersions({})).toEqual({ ok: true, versions: defaultUnityEditorMatrixVersions });
    expect(parseUnityEditorMatrixVersions({ SGG_UNITY_EDITOR_MATRIX: "2022.3.62f3, 6000.4.10f1;6000.5" })).toEqual({
      ok: true,
      versions: ["2022.3.62f3", "6000.4.10f1", "6000.5"],
    });
    expect(parseUnityEditorMatrixVersions({ SGG_UNITY_EDITOR_MATRIX: "   " })).toMatchObject({ ok: false });
  });

  it("skips optional missing Unity editor matrix entries and can require all entries", async () => {
    const { runUnityEditorMatrix } = await import(pathToFileURL(matrixScriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-matrix-"));
    const stdoutMock = vi.spyOn(console, "log").mockImplementation(() => undefined);
    const stderrMock = vi.spyOn(console, "error").mockImplementation(() => undefined);

    const optionalStatus = await runUnityEditorMatrix(
      {
        SGG_UNITY_TEST_PROJECT: tempRoot,
        SGG_UNITY_EDITOR_MATRIX: "6000.4.10f1",
        SGG_UNITY_EDITOR_ENV_FILE: "off",
      },
      process.cwd(),
      { discovery: { editors: [] } },
    );
    expect(optionalStatus).toBe(0);
    expect(stdoutMock.mock.calls.map((call) => call.join(" ")).join("\n")).toContain("skipped: no requested Unity Editors were discovered");

    const requiredStatus = await runUnityEditorMatrix(
      {
        SGG_UNITY_TEST_PROJECT: tempRoot,
        SGG_UNITY_EDITOR_MATRIX: "6000.4.10f1",
        SGG_UNITY_EDITOR_MATRIX_REQUIRED: "true",
        SGG_UNITY_EDITOR_ENV_FILE: "off",
      },
      process.cwd(),
      { discovery: { editors: [] } },
    );
    expect(requiredStatus).toBe(1);
    expect(stderrMock.mock.calls.map((call) => call.join(" ")).join("\n")).toContain("matching Unity Editor was not discovered");
  });

  it("runs discovered Unity editor matrix entries with version-specific logs", async () => {
    const { runUnityEditorMatrix } = await import(pathToFileURL(matrixScriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-matrix-"));
    const capturedEnv: Record<string, string | undefined>[] = [];
    const runValidation = vi.fn(async (env: Record<string, string | undefined>) => {
      capturedEnv.push(env);
      return 0;
    });

    const status = await runUnityEditorMatrix(
      {
        UNITY_EXE: "/ignored/Unity.exe",
        SGG_UNITY_TEST_PROJECT: tempRoot,
        SGG_UNITY_EDITOR_MATRIX: "6000.4",
        SGG_UNITY_EDITOR_ENV_FILE: "off",
      },
      process.cwd(),
      {
        discovery: { editors: [{ version: "6000.4.10f1", executable_path: "/Unity/6000.4.10f1/Editor/Unity.exe", source: "test", exists: true }] },
        runValidation,
      },
    );

    expect(status).toBe(0);
    expect(runValidation).toHaveBeenCalledTimes(1);
    const firstEnv = capturedEnv[0] as Record<string, string | undefined>;
    expect(firstEnv).toMatchObject({
      SGG_UNITY_EDITOR_ENV_FILE: "off",
      SGG_UNITY_EDITOR_VERSION: "6000.4",
    });
    expect(firstEnv).not.toHaveProperty("UNITY_EXE");
    expect(firstEnv.SGG_UNITY_TEST_LOG?.replaceAll("\\", "/")).toContain("Logs/sgg-unity-mcp-editor-matrix/sgg-unity-mcp-editor-check-6000.4.log");
  });

  it("uses an async watchdog for Unity timeouts and reports log tails", async () => {
    const { runUnityEditorValidation } = await import(pathToFileURL(scriptPath).href);
    const tempRoot = fs.mkdtempSync(path.join(os.tmpdir(), "sgg-unity-editor-timeout-"));
    const logDir = path.join(tempRoot, "Logs");
    fs.mkdirSync(logDir, { recursive: true });
    fs.writeFileSync(path.join(logDir, "sgg-unity-mcp-editor-check.log"), "Licensing Client hang\nStill starting Unity\n", "utf8");
    const child = new EventEmitter() as EventEmitter & { pid: number; kill: ReturnType<typeof vi.fn>; unref: ReturnType<typeof vi.fn> };
    child.pid = 12345;
    child.kill = vi.fn(() => true);
    child.unref = vi.fn();
    const spawnMock = vi.fn(() => child);
    const stderrMock = vi.spyOn(console, "error").mockImplementation(() => undefined);
    vi.useFakeTimers();

    const statusPromise = runUnityEditorValidation(
      {
        UNITY_EXE: "/Unity/Editor/Unity.exe",
        SGG_UNITY_TEST_PROJECT: tempRoot,
        SGG_UNITY_EDITOR_TIMEOUT_MS: "123",
        SGG_UNITY_EDITOR_ENV_FILE: "off",
      },
      process.cwd(),
      { spawn: spawnMock, killGraceMs: 1, nativeModalScanner: () => ({ supported: true, blocking: false, code: null, candidates: [], windows: [], errors: [] }) },
    );
    await vi.advanceTimersByTimeAsync(124);
    const status = await statusPromise;

    expect(status).toBe(1);
    expect(spawnMock).toHaveBeenCalledWith(
      "/Unity/Editor/Unity.exe",
      ["-batchmode", "-quit", "-projectPath", tempRoot, "-logFile", path.join(tempRoot, "Logs", "sgg-unity-mcp-editor-check.log")],
      expect.objectContaining({ stdio: "inherit", windowsHide: true }),
    );
    expect(child.kill).toHaveBeenCalledWith("SIGTERM");
    expect(child.unref).toHaveBeenCalled();
    expect(stderrMock.mock.calls.map((call) => call.join(" ")).join("\n")).toContain("Unity Editor validation timed out after 123 ms");
    expect(stderrMock.mock.calls.map((call) => call.join(" ")).join("\n")).toContain("Licensing Client hang");
  });
});
