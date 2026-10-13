import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const screenshotCommandsPath = path.join(packageRoot, "Editor", "Commands", "EditorScreenshotCommands.cs");
const editorWindowCaptureBackendPath = path.join(packageRoot, "Editor", "Commands", "EditorWindowCaptureBackend.cs");
const gameViewCaptureBridgePath = path.join(packageRoot, "Runtime", "GameViewCaptureBridge.cs");
const editorWindowSelectorPath = path.join(packageRoot, "Editor", "Commands", "EditorWindowSelector.cs");
const commandsDocPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const generatedCommandsDocPath = path.join(process.cwd(), "docs", "commands.generated.md");
const securityDocPath = path.join(process.cwd(), "docs", "en", "reference", "security.md");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");

describe("Unity editor screenshot command source-contract", () => {
  it("declares scene visual commands behind a feature flag", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "editor_screenshots"');
    expect(source).toContain('const string DesktopScreenshotFeatureFlag = "desktop_screenshots"');
    expect(source).toContain('"scene.view.screenshot"');
    expect(source).toContain('"scene.view.capture_multi_angle"');
    expect(source).toContain('"scene.view.overlay"');
    expect(source).toContain('"game.view.screenshot"');
    expect(source).toContain('"camera.list"');
    expect(source).toContain('"camera.screenshot"');
    expect(source).toContain('"editor.window.screenshot"');
    expect(source).toContain('"desktop.region.screenshot"');
    expect(source).toContain('"game.view.screenshot.status"');
    expect(source).toContain('"game.view.screenshot.cancel"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(9);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(10);
    expect(source).toContain('FeatureFlag = DesktopScreenshotFeatureFlag');
  });

  it("writes bounded PNG files under the project Library", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");

    expect(source).toContain('ScreenshotRoot = "Library/SGGUnityMcp/screenshots"');
    expect(source).toContain('DesktopRegionScreenshotRoot = ScreenshotRoot + "/desktop-regions"');
    expect(source).toContain('CaptureArtifactsRoot = "CaptureArtifacts"');
    expect(source).toContain("RenderTexture.GetTemporary(width, height");
    expect(source).toContain("texture.EncodeToPNG()");
    expect(source).toContain("output_path");
    expect(source).toContain("TryNormalizeScreenshotOutputPath");
    expect(source).toContain("GameViewScreenshotOutputLimitJson");
    expect(source).toContain('\\"maximum\\":4096');
  });

  it("schedules overlay-inclusive Game view screenshots with status polling", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");
    const bridgeSource = fs.readFileSync(gameViewCaptureBridgePath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult GameViewScreenshot"), source.indexOf('[McpCommand(\n            "camera.list"'));

    expect(source).toContain("GameViewScreenshotSchemaJson");
    expect(source).toContain('const string GameViewScreenshotWrapperWaitJson = "{\\"mode\\":\\"wait\\",\\"wait_for_replacement_ms\\":{\\"min_ms\\":1,\\"max_ms\\":3600000,\\"deadline_scope\\":\\"pre_dispatch_only\\"}}"');
    expect(source).toContain("WrapperWaitJson = GameViewScreenshotWrapperWaitJson");
    expect(source).toContain('\\"include_overlay_ui\\"');
    expect(source).toContain('\\"backend\\"');
    expect(source).toContain('\\"output_path\\"');
    expect(source).toContain('\\"minLength\\":1');
    expect(source).toContain('Library/SGGUnityMcp/screenshots|CaptureArtifacts');
    expect(source).toContain('"game.view.screenshot.status"');
    expect(commandSource).toContain("ScheduleGameViewOverlayCapture(context.ArgsJson, requestedBackend");
    expect(commandSource).toContain('requestedBackend = "screen_capture"');
    expect(commandSource).toContain('requestedBackend == "end_of_frame" ? "screen_capture" : requestedBackend');
    expect(commandSource).toContain("IncompatibleGameViewCaptureOptions(requestedBackend, includeOverlayUi)");
    expect(source).not.toContain("Awaitable.EndOfFrameAsync()");
    expect(bridgeSource).toContain("ScreenCapture.CaptureScreenshotIntoRenderTexture(target)");
    expect(source).toContain("s_gameViewCaptures");
    expect(source).toContain('"scheduled"');
    expect(source).toContain('"game.view.screenshot.status"');
    expect(commandSource).toContain('CommandExecutionResult.Error("invalid_field_value"');
    expect(commandSource).not.toContain('Set include_overlay_ui:true');
  });

  it("bounds and cooperatively cancels unfocused overlay captures", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");

    expect(source).toContain("AuthoritativeCaptureCannotAdvance(out var preflightDetails)");
    expect(source).toContain('"capture_precondition_failed"');
    expect(source).toContain("ExpireGameViewCaptureLocked(capture, now)");
    expect(source).toContain('MarkGameViewCaptureTerminalLocked(capture, "cancelled"');
    expect(source).toContain("CleanupGameViewCaptureStaging(capture)");
    expect(source).toContain("GameViewCaptureBridge.Begin(capture.CaptureId");
    expect(source).toContain("EnsureGameViewScreenCapturePollHook();");
    expect(source).toContain("var blocked = !playing || !focused || paused || batchMode || compiling || updating;");
    expect(source).toContain("capture.WatchdogPollCount++");
    expect(source).toContain("capture.CompletionDeadlineChecked = true");
    expect(source).toContain('"timed_out", "capture_timeout"');
    expect(source).toContain('CommandExecutionResult.Error("unity_busy", "The bounded Game view screenshot history is full of active captures"');
    expect(source).toContain("TryRemoveOldestTerminalCaptureLocked()");
    expect(source).toContain("!IsActiveGameViewCaptureStatus(pair.Value.Status)");
    expect(source).toContain('\\"can_cancel\\"');
    expect(source).toContain('\\"deadline_utc\\"');
    expect(source).toContain('\\"game.view.screenshot.cancel\\"');
    expect(source).toContain('\\"automatic_focus_attempted\\":false');
  });

  it("preserves overlay intent and exposes only completed screenshot artifacts", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");

    expect(source).toContain("IncludeOverlayUi = includeOverlayUi");
    expect(source).toContain("IncompatibleGameViewCaptureOptions");
    expect(source).toContain('var artifactAvailable = artifactIntegrity == "verified"');
    expect(source).toContain("GameViewCapturePolicy.CompletedArtifactIntegrity(ProjectPaths.ProjectRoot, capture.FullPath, capture.Bytes, capture.Sha256)");
    expect(source).toContain("artifact_integrity");
    expect(source).toContain("planned_artifact_path");
    expect(fs.readFileSync(gameViewCaptureBridgePath, "utf8")).toContain("ScreenCapture.CaptureScreenshotIntoRenderTexture(target)");
    expect(source).toContain("IsValidEncodedPng(result.Png, result.Width, result.Height)");
    expect(source).not.toContain("ScreenCapture.CaptureScreenshot(capture.FullPath)");
    expect(source).toContain('"capture_corrupt_artifact"');
    expect(source).toContain("capture.CleanupPerformed |= CleanupGameViewCaptureStaging(capture)");
    expect(source).toContain("requested_include_overlay_ui");
    expect(source).toContain("include_overlay_ui_explicit");
    expect(source).toContain("completion_deadline_checked");
  });

  it("initializes camera_render deterministically before one requested artifact render", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");
    const captureSource = source.slice(source.indexOf("static CommandExecutionResult CaptureCamera"), source.indexOf("static List<Camera> CameraStack"));

    expect(captureSource.match(/ClearCameraRenderTarget\(renderTexture\);/g)).toHaveLength(1);
    expect(captureSource.match(/RenderCameraPass\(camera, stack\);/g)).toHaveLength(1);
    expect(captureSource).toContain("GL.Clear(true, true, Color.clear)");
    expect(captureSource).toContain("render_target_initialized");
    expect(captureSource).toContain("render_pass_count");
    expect(captureSource).toContain('var artifactAvailable = status == "ok"');
  });

  it("selects cameras by path, name, bounded index, or loaded scene object path", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult CameraScreenshot"), source.indexOf("static SceneView ActiveSceneView"));
    const sceneSelectorSource = source.slice(source.indexOf("static CommandExecutionResult SelectCameraBySceneObject"), source.indexOf("static List<Camera> DiscoverCameras"));

    expect(source).toContain("SelectCamera");
    expect(source).toContain('JsonUtil.ExtractString(argsJson, "camera_path")');
    expect(source).toContain('JsonUtil.ExtractString(argsJson, "camera_name")');
    expect(source).toContain('JsonUtil.ExtractInt(argsJson, "camera_index", -1)');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "scene_path")');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "object_path")');
    expect(source).toContain('JsonUtil.ExtractInt(context.ArgsJson, "instance_id", 0)');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "entity_id").Trim()');
    expect(commandSource).toContain('scene_path and object_path must be provided together');
    expect(commandSource).toContain('scene_path/object_path cannot be combined with camera_path, camera_name, or camera_index');
    expect(commandSource).toContain('Provide only one of instance_id or entity_id');
    expect(commandSource).toContain('instance_id/entity_id cannot be combined with camera_path, camera_name, camera_index, scene_path, or object_path');
    expect(commandSource).toContain('SelectCameraByRuntimeId(instanceId, entityId, includeInactive');
    expect(source).toContain('PathGuard.RequireUnityAssetPath(rawPath)');
    expect(sceneSelectorSource).toContain('scene_path must identify an already loaded scene');
    expect(sceneSelectorSource).toContain('FindByPath(scene.GetRootGameObjects(), objectPath)');
    expect(sceneSelectorSource).toContain('target.GetComponent<Camera>()');
    expect(sceneSelectorSource).toContain('set include_inactive:true');
    expect(sceneSelectorSource).not.toContain('OpenScene');
    expect(source).toContain('EditorUtility.InstanceIDToObject(instanceId)');
    expect(source).toContain('EditorUtility.EntityIdToObject(UnityEngine.EntityId.FromULong(runtimeEntityId))');
    expect(source).toContain('UnityEngine.EntityId.ToULong(selectedObject.GetEntityId())');
    expect(source).toContain('AppendRuntimeObjectIds(builder, camera)');
    expect(source).toContain("const int MaxCameras = 100");
  });

  it("captures camera stack metadata without a compile-time URP dependency", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");

    expect(source).toContain('JsonUtil.ExtractBool(argsJson, "include_stack", false)');
    expect(source).toContain('GetProperty("cameraStack", BindingFlags.Instance');
    expect(source).toContain('"best_effort_reflection"');
    expect(source).toContain('screen_space_overlay_ui');
  });

  it("returns bounded Scene view overlay metadata", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");

    expect(source).toContain("BuildOverlayJson");
    expect(source).toContain("WorldToViewportPoint");
    expect(source).toContain("active_in_hierarchy");
    expect(source).toContain('\\"max_objects\\":500');
  });

  it("returns operator-only recovery when no Scene view camera is available", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");
    const recoverySource = source.slice(source.indexOf("static CommandExecutionResult MissingSceneViewCamera"), source.indexOf("static Camera FirstEnabledCamera"));

    expect(source.match(/return MissingSceneViewCamera\(\);/g)).toHaveLength(2);
    expect(recoverySource).toContain('\\"capture_state\\":\\"not_scheduled\\"');
    expect(recoverySource).toContain('\\"automatic_window_action_attempted\\":false');
    expect(recoverySource).toContain("Open Window > General > Scene");
    expect(recoverySource).toContain('\\"command\\":\\"camera.list\\"');
    expect(recoverySource).toContain('\\"command\\":\\"camera.screenshot\\"');
    expect(recoverySource).toContain('\\"command\\":\\"game.view.screenshot\\"');
    expect(recoverySource).not.toContain("GetWindow<SceneView>");
    expect(recoverySource).not.toContain("Focus()");
    expect(recoverySource).not.toContain("focusedWindow");
    expect(source).toContain("return MissingSceneViewCameraForOverlay();");
    expect(source).toContain('\\"operation_state\\":\\"not_started\\"');
  });

  it("captures a bounded 2x2 Scene view multi-angle screenshot", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult SceneViewCaptureMultiAngle"), source.indexOf("public static CommandExecutionResult SceneViewOverlay"));

    expect(source).toContain("MultiAngleScreenshotSchemaJson");
    expect(source).toContain('\\"panel_width\\":{\\"type\\":\\"integer\\",\\"minimum\\":64,\\"maximum\\":2048}');
    expect(source).toContain('\\"max_total_width\\":4096');
    expect(commandSource).toContain('scene_path and object_path must be provided together');
    expect(commandSource).toContain("TryBuildObjectFocusBounds(context.ArgsJson, includeInactive");
    expect(commandSource).toContain("TryBuildSceneViewFocusBounds(sceneView, includeInactive");
    expect(source).toContain('ScreenshotPath("scene-view-multi-angle")');
    expect(source).toContain('new MultiAnglePanel("isometric"');
    expect(source).toContain('new MultiAnglePanel("front"');
    expect(source).toContain('new MultiAnglePanel("top"');
    expect(source).toContain('new MultiAnglePanel("right"');
    expect(source).toContain("HideFlags.HideAndDontSave");
    expect(source).toContain("UnityEngine.Object.DestroyImmediate(cameraObject)");
    expect(source).not.toContain("multi_angle_output_path");
  });

  it("captures exact focused Editor window content through the bounded internal framebuffer backend", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");
    const backend = fs.readFileSync(editorWindowCaptureBackendPath, "utf8");
    const selector = fs.readFileSync(editorWindowSelectorPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult EditorWindowScreenshot"), source.indexOf('[McpCommand(\n            "desktop.region.screenshot"'));

    expect(source).toContain("EditorWindowScreenshotSchemaJson");
    expect(source).toContain('Capability = "editor_screenshots"');
    expect(source).toContain('ResultShape = "screenshot_artifact"');
    expect(source).toContain('Idempotency = "unsafe"');
    expect(selector).toContain('JsonUtil.ExtractString(argsJson, "title")');
    expect(selector).toContain('JsonUtil.ExtractString(argsJson, "type_name")');
    expect(selector).toContain('JsonUtil.ExtractBool(argsJson, "focused", false)');
    expect(selector).toContain('JsonUtil.ExtractInt(argsJson, "window_index", -1)');
    expect(source).toContain('JsonUtil.ExtractInt(context.ArgsJson, "max_width", 4096)');
    expect(source).toContain('JsonUtil.ExtractInt(context.ArgsJson, "max_height", 4096)');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "windows_hash")');
    expect(source).toContain('\\"required\\":[\\"windows_hash\\"]');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_editor_chrome", false)');
    expect(source).not.toContain("allow_focus_change");
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false)');
    expect(source).toContain('JsonUtil.ExtractString(argsJson, "output_path")');
    expect(source).toContain('"capture_target_unavailable"');
    expect(commandSource).toContain('"windows_hash_changed"');
    expect(commandSource).toContain('"editor_chrome_not_supported"');
    expect(commandSource).toContain("EditorWindowCaptureBackend.TryValidateTarget");
    expect(commandSource).toContain("EditorWindowCaptureBackend.TryGetCaptureMethod");
    expect(source).toContain("EditorWindowCaptureBackend.TryCapture");
    expect(commandSource).toContain("GameViewCapturePolicy.TryStage");
    expect(commandSource).toContain("GameViewCapturePolicy.TryPublishReserved");
    expect(commandSource).toContain("GameViewCapturePolicy.CommitPublication");
    expect(commandSource).toContain("EditorWindowPublicationStateUncertain");
    expect(source).toContain("NormalizeEditorWindowCaptureErrorCode");
    expect(source).toContain('artifactProduced ? "[\\"artifact.metadata\\"]"');
    expect(source).not.toContain('artifactProduced ? "[\\"job.artifact.read\\"');
    expect(commandSource).toContain('"dimensions_exceeded"');
    expect(source).toContain('"focus_change_attempted\\":false');
    expect(source).toContain('"selected_window\\":');
    expect(source).toContain('"fallback_attempted\\":false');
    expect(source).toContain('"native_hwnd_capture_attempted\\":false');
    expect(source).toContain('"artifact_path\\":null');
    expect(commandSource).not.toContain("TryPlanEditorWindowPlatformCapture");
    expect(commandSource).not.toContain("TryCaptureEditorWindowPlatform");
    expect(commandSource).not.toContain("TryCaptureWindowWithPrintWindow");
    expect(commandSource).not.toContain("TryCaptureScreenRegionWin32");
    expect(commandSource).not.toContain("PrintWindow");
    expect(commandSource).not.toContain("BitBlt");
    expect(backend).toContain('GetField("m_Parent"');
    expect(backend).toContain('GetProperty("actualView"');
    expect(backend).toContain("ReferenceEquals(actualView, window)");
    expect(backend).toContain('"CaptureEditorWindow"');
    expect(backend).toContain('new[] { typeof(EditorWindow), typeof(RenderTexture) }');
    expect(backend).toContain("method.ReturnType != typeof(bool)");
    expect(backend).toContain("method.Invoke(null");
    expect(backend).not.toContain("GrabPixels");
    expect(backend).not.toContain("RepaintImmediately");
    expect(backend).not.toContain(".Focus(");
    expect(source).toContain("EditorWindowSelector.Select(context.ArgsJson");
    expect(commandSource).toContain("out var selectedWindowsHash");
    expect(selector).toContain("UnityEngine.Resources.FindObjectsOfTypeAll<EditorWindow>()");
    expect(selector).toContain("EditorWindow.focusedWindow");
    expect(selector).toContain("No matching Unity Editor window");
    expect(selector).toContain("ambiguous_selector");
  });

  it("allows only bounded project-local screenshot output paths", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult EditorWindowScreenshot"), source.indexOf('[McpCommand(\n            "desktop.region.screenshot"'));

    expect(commandSource).toContain("EditorWindowScreenshotPath(window, selectedIndex, requestedWindowsHash)");
    expect(commandSource).toContain("ResolveScreenshotOutputPath");
    expect(source).toContain("ProjectPaths.ProjectRoot");
    expect(source).toContain("output_path");
    expect(source).toContain("absolute_path_rejected");
    expect(source).toContain("path_traversal_rejected");
    expect(source).toContain("output_path must end with .png");
    expect(source).toContain("output_path must be under Library/SGGUnityMcp/screenshots/ or CaptureArtifacts/");
    expect(commandSource).not.toContain("System.Drawing");
    expect(commandSource).not.toContain("Graphics.CopyFromScreen");
    expect(commandSource).not.toContain("Process.GetProcesses");
  });

  it("keeps native window and screen capture confined to desktop-region support", () => {
    const source = fs.readFileSync(screenshotCommandsPath, "utf8");

    const commandSource = source.slice(source.indexOf("public static CommandExecutionResult EditorWindowScreenshot"), source.indexOf('[McpCommand(\n            "desktop.region.screenshot"'));

    expect(commandSource).not.toContain("TryCaptureEditorWindowPlatform");
    expect(commandSource).not.toContain("TryCaptureWindowWithPrintWindow");
    expect(commandSource).not.toContain("PrintWindow");
    expect(commandSource).not.toContain("EnumWindows");
    expect(commandSource).not.toContain("BitBlt");
    expect(source).toContain("TryCaptureScreenRegionWin32");
    expect(source).toContain("BitBlt(memoryDc");
    expect(source).toContain('"desktop.region.screenshot"');
    expect(source).toContain('Capability = "desktop_screenshots"');
    expect(source).toContain('DesktopRegionScreenshotRoot + "/region-"');
  });

  it("documents and policy-allows Editor window screenshots consistently", () => {
    const commandsDoc = fs.readFileSync(commandsDocPath, "utf8");
    const generatedDoc = fs.readFileSync(generatedCommandsDocPath, "utf8");
    const securityDoc = fs.readFileSync(securityDocPath, "utf8");
    const policy = JSON.parse(fs.readFileSync(policyExamplePath, "utf8"));
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");

    expect(commandsDoc).toContain("editor.window.screenshot");
    expect(commandsDoc).toContain("desktop.region.screenshot");
    expect(commandsDoc).toContain("capture_target_unavailable");
    expect(commandsDoc).toContain("never substitutes Unity's main window or desktop pixels");
    expect(generatedDoc).toContain("`editor.window.screenshot`");
    expect(generatedDoc).toContain("`desktop.region.screenshot`");
    expect(securityDoc).toContain("Editor screenshots");
    expect(securityDoc).toContain("Desktop region screenshots");
    expect(securityDoc).toContain("privacy-sensitive read output");
    expect(policy.projects.game.allow).toContain("editor.window.screenshot");
    expect(policy.projects.game.allow).toContain("desktop.region.screenshot");
    expect(profiles).toContain('"editor.window.screenshot"');
    expect(profiles).toContain('"editor_screenshots"');
  });
});
