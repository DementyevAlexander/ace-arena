using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using SGG.UnityMCP.Runtime;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    [InitializeOnLoad]
    public static class EditorScreenshotCommands
    {
        const string FeatureFlag = "editor_screenshots";
        const string GameViewFocusCaptureFeatureFlag = "game_view_focus_capture";
        const string DesktopScreenshotFeatureFlag = "desktop_screenshots";
        const string ScreenshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096}},\"additionalProperties\":false}";
        const string GameViewScreenshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"backend\":{\"type\":\"string\",\"enum\":[\"camera_render\",\"end_of_frame\",\"screen_capture\"],\"default\":\"screen_capture\",\"description\":\"screen_capture is the authoritative final Game View; end_of_frame is a compatibility alias; camera_render is camera-only\"},\"include_overlay_ui\":{\"type\":\"boolean\",\"default\":true,\"description\":\"Must be true for screen_capture/end_of_frame and false for camera_render\"},\"settle_frames\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":120,\"default\":2},\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":30000,\"default\":15000},\"output_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":240,\"pattern\":\"^(Library/SGGUnityMcp/screenshots|CaptureArtifacts)/.+\\\\.png$\",\"description\":\"Project-relative PNG path under Library/SGGUnityMcp/screenshots/ or CaptureArtifacts/ without traversal or unsafe path segments\"},\"dry_run\":{\"type\":\"boolean\"}},\"dependentRequired\":{\"width\":[\"height\"],\"height\":[\"width\"]},\"additionalProperties\":false}";
        const string GameViewScreenshotWrapperWaitJson = "{\"mode\":\"wait\",\"wait_for_replacement_ms\":{\"min_ms\":1,\"max_ms\":3600000,\"deadline_scope\":\"pre_dispatch_only\"}}";
        const string GameViewScreenshotStatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"capture_id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":96}},\"required\":[\"capture_id\"],\"additionalProperties\":false}";
        const string GameViewScreenshotCancelSchemaJson = "{\"type\":\"object\",\"properties\":{\"capture_id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":96}},\"required\":[\"capture_id\"],\"additionalProperties\":false}";
        const string GameViewFocusSchemaJson = "{\"type\":\"object\",\"properties\":{\"window_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":99},\"windows_hash\":{\"type\":\"string\",\"pattern\":\"^fnv1a32:[0-9a-f]{8}$\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_focus\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"window_index\",\"windows_hash\"],\"additionalProperties\":false}";
        const string GameViewFocusCaptureSchemaJson = "{\"type\":\"object\",\"properties\":{\"window_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":99},\"windows_hash\":{\"type\":\"string\",\"pattern\":\"^fnv1a32:[0-9a-f]{8}$\"},\"backend\":{\"type\":\"string\",\"enum\":[\"screen_capture\",\"end_of_frame\"],\"default\":\"screen_capture\"},\"include_overlay_ui\":{\"type\":\"boolean\",\"default\":true},\"width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"settle_frames\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":120,\"default\":2},\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":30000,\"default\":15000},\"output_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":240,\"pattern\":\"^(Library/SGGUnityMcp/screenshots|CaptureArtifacts)/.+\\\\.png$\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_focus\":{\"type\":\"boolean\",\"default\":false},\"plan_id\":{\"type\":\"string\",\"pattern\":\"^game-focus-capture-[0-9a-f]{32}$\"},\"plan_hash\":{\"type\":\"string\",\"pattern\":\"^sha256:[0-9a-f]{64}$\"}},\"required\":[\"window_index\",\"windows_hash\",\"output_path\"],\"dependentRequired\":{\"width\":[\"height\"],\"height\":[\"width\"]},\"additionalProperties\":false}";
        const string MultiAngleScreenshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"panel_width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":2048},\"panel_height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":2048},\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"include_inactive\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string EditorWindowScreenshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"title\":{\"type\":\"string\"},\"type_name\":{\"type\":\"string\"},\"focused\":{\"type\":\"boolean\",\"enum\":[true]},\"window_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":99},\"windows_hash\":{\"type\":\"string\",\"pattern\":\"^fnv1a32:[0-9a-f]{8}$\"},\"max_width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"max_height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"include_editor_chrome\":{\"type\":\"boolean\",\"default\":false},\"purpose\":{\"type\":\"string\",\"maxLength\":160},\"output_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":240,\"pattern\":\"^(Library/SGGUnityMcp/screenshots|CaptureArtifacts)/.+\\\\.png$\",\"description\":\"Project-relative PNG path under Library/SGGUnityMcp/screenshots/ or CaptureArtifacts/ without traversal or unsafe path segments\"},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"windows_hash\"],\"additionalProperties\":false}";
        const string DesktopRegionScreenshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"x\":{\"type\":\"integer\",\"minimum\":-65535,\"maximum\":65535},\"y\":{\"type\":\"integer\",\"minimum\":-65535,\"maximum\":65535},\"width\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":4096},\"height\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":4096},\"purpose\":{\"type\":\"string\",\"maxLength\":160},\"output_path\":{\"type\":\"string\",\"maxLength\":240},\"dry_run\":{\"type\":\"boolean\"}},\"required\":[\"x\",\"y\",\"width\",\"height\"],\"additionalProperties\":false}";
#if UNITY_6000_4_OR_NEWER
        const string CameraScreenshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"camera_path\":{\"type\":\"string\"},\"camera_name\":{\"type\":\"string\"},\"camera_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":99},\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"instance_id\":{\"type\":\"integer\"},\"entity_id\":{\"type\":\"string\"},\"include_inactive\":{\"type\":\"boolean\"},\"include_stack\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
#else
        const string CameraScreenshotSchemaJson = "{\"type\":\"object\",\"properties\":{\"width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"camera_path\":{\"type\":\"string\"},\"camera_name\":{\"type\":\"string\"},\"camera_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":99},\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"instance_id\":{\"type\":\"integer\"},\"include_inactive\":{\"type\":\"boolean\"},\"include_stack\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
#endif
        const string CameraListSchemaJson = "{\"type\":\"object\",\"properties\":{\"include_inactive\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string OverlaySchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"max_objects\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"include_inactive\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string ScreenshotOutputLimitJson = "{\"max_width\":4096,\"max_height\":4096}";
        const string GameViewScreenshotOutputLimitJson = "{\"max_width\":4096,\"max_height\":4096,\"roots\":[\"Library/SGGUnityMcp/screenshots\",\"CaptureArtifacts\"],\"async_history\":32}";
        const string GameViewFocusOutputLimitJson = "{\"max_windows\":100,\"target_type\":\"UnityEditor.GameView\",\"native_focus_api_used\":false}";
        const string GameViewFocusCaptureOutputLimitJson = "{\"max_windows\":100,\"max_plans\":32,\"plan_ttl_seconds\":60,\"target_type\":\"UnityEditor.GameView\",\"native_focus_api_used\":false,\"roots\":[\"Library/SGGUnityMcp/screenshots\",\"CaptureArtifacts\"]}";
        const string MultiAngleOutputLimitJson = "{\"max_panel_width\":2048,\"max_panel_height\":2048,\"max_total_width\":4096,\"max_total_height\":4096,\"angles\":4}";
        const string EditorWindowScreenshotOutputLimitJson = "{\"max_width\":4096,\"max_height\":4096,\"roots\":[\"Library/SGGUnityMcp/screenshots\",\"CaptureArtifacts\"],\"artifact_produced\":true,\"backend\":\"unity_internal_capture_editor_window\",\"target_binding\":\"managed_editor_window_to_host_view\",\"include_editor_chrome\":false}";
        const string DesktopRegionScreenshotOutputLimitJson = "{\"max_width\":4096,\"max_height\":4096,\"roots\":[\"Library/SGGUnityMcp/screenshots/desktop-regions\",\"CaptureArtifacts\"]}";
        const string CameraListOutputLimitJson = "{\"max_cameras\":100}";
        const string OverlayOutputLimitJson = "{\"max_objects\":500}";
        const string ScreenshotRoot = "Library/SGGUnityMcp/screenshots";
        const string DesktopRegionScreenshotRoot = ScreenshotRoot + "/desktop-regions";
        const string CaptureArtifactsRoot = "CaptureArtifacts";
        const int MaxScreenshotOutputPathLength = 240;
        const int MaxCameras = 100;
        const int MaxGameViewCaptureHistory = 32;
        const int MaxGameViewFocusCapturePlans = 32;
        const int GameViewCaptureExpirySeconds = 600;
        const int GameViewFocusCapturePlanExpirySeconds = 60;
        const string GameViewCaptureSessionStateKey = "SGG.UnityMCP.GameViewCapture.current";
        const string ScreenshotPrivacyWarning = "Editor screenshots may include visible project data, open files, scene contents, inspector values, console text, and other local Editor information. Review before sharing.";
        static readonly object s_gameViewCaptureLock = new object();
        static readonly Dictionary<string, GameViewCaptureState> s_gameViewCaptures = new Dictionary<string, GameViewCaptureState>(StringComparer.Ordinal);
        static readonly object s_gameViewFocusCapturePlanLock = new object();
        static readonly Dictionary<string, GameViewFocusCapturePlan> s_gameViewFocusCapturePlans = new Dictionary<string, GameViewFocusCapturePlan>(StringComparer.Ordinal);
        static bool s_gameViewScreenCapturePollHooked;

        static EditorScreenshotCommands()
        {
            RecoverPersistedGameViewCapture();
            AssemblyReloadEvents.beforeAssemblyReload += InterruptGameViewCaptureForReload;
            EditorApplication.quitting += InterruptGameViewCaptureForReload;
        }

        [McpCommand(
            "scene.view.screenshot",
            Title = "Scene view screenshot",
            Description = "Capture the active Unity Scene view camera to a project-local PNG",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ScreenshotSchemaJson,
            Tags = new string[] { "scene", "screenshot" },
            Capability = "editor_screenshots",
            ResultShape = "object_details",
            OutputLimitJson = ScreenshotOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneViewScreenshot(McpCommandContext context)
        {
            var sceneView = ActiveSceneView();
            if (sceneView == null || sceneView.camera == null)
            {
                return MissingSceneViewCamera();
            }

            return CaptureCamera(sceneView.camera, "scene-view", context.ArgsJson);
        }

        [McpCommand(
            "scene.view.capture_multi_angle",
            Title = "Scene view multi-angle screenshot",
            Description = "Capture a 2x2 multi-angle Scene view layout screenshot to a project-local PNG",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 45000,
            SchemaJson = MultiAngleScreenshotSchemaJson,
            Tags = new string[] { "scene", "screenshot", "camera" },
            Capability = "editor_screenshots",
            ResultShape = "object_details",
            OutputLimitJson = MultiAngleOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneViewCaptureMultiAngle(McpCommandContext context)
        {
            var sceneView = ActiveSceneView();
            if (sceneView == null || sceneView.camera == null)
            {
                return MissingSceneViewCamera();
            }

            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", false);
            var scenePath = JsonUtil.ExtractString(context.ArgsJson, "scene_path");
            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            var hasScenePath = !string.IsNullOrEmpty(scenePath);
            var hasObjectPath = !string.IsNullOrEmpty(objectPath);
            if (hasScenePath != hasObjectPath)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "scene_path and object_path must be provided together");
            }

            CommandExecutionResult focusResult;
            Bounds focusBounds;
            string focusJson;
            if (hasScenePath)
            {
                focusResult = TryBuildObjectFocusBounds(context.ArgsJson, includeInactive, out focusBounds, out focusJson);
            }
            else
            {
                focusResult = TryBuildSceneViewFocusBounds(sceneView, includeInactive, out focusBounds, out focusJson);
            }

            if (focusResult != null)
            {
                return focusResult;
            }

            return CaptureMultiAngle(sceneView.camera, focusBounds, focusJson, context.ArgsJson);
        }

        [McpCommand(
            "scene.view.overlay",
            Title = "Scene view overlay metadata",
            Description = "Read bounded viewport overlay metadata for visible Scene view objects",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = OverlaySchemaJson,
            Tags = new string[] { "scene", "screenshot", "overlay" },
            Capability = "editor_screenshots",
            ResultShape = "bounded_list",
            OutputLimitJson = OverlayOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SceneViewOverlay(McpCommandContext context)
        {
            var sceneView = ActiveSceneView();
            if (sceneView == null || sceneView.camera == null)
            {
                return MissingSceneViewCameraForOverlay();
            }

            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            var maxObjects = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_objects", 100), 1, 500);
            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", false);
            var overlays = BuildOverlayJson(sceneView.camera, query, maxObjects, includeInactive, out var matched, out var truncated);
            return CommandExecutionResult.Ok("{\"camera\":" + JsonUtil.String(sceneView.camera.name)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"matched_objects\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"overlays\":[" + overlays + "]}");
        }

        [McpCommand(
            "editor.window.screenshot",
            Title = "Editor window screenshot",
            Description = "Capture the exact focused selected Unity Editor window content to a project-local PNG without Editor chrome or desktop fallback",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = EditorWindowScreenshotSchemaJson,
            Tags = new string[] { "editor", "windows", "screenshot" },
            Capability = "editor_screenshots",
            ResultShape = "screenshot_artifact",
            OutputLimitJson = EditorWindowScreenshotOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorWindowScreenshot(McpCommandContext context)
        {
            var selectResult = EditorWindowSelector.Select(context.ArgsJson, out var window, out var selectedIndex, out _, out var selectedWindowsHash);
            if (selectResult != null)
            {
                return selectResult;
            }

            var requestedWindowsHash = JsonUtil.ExtractString(context.ArgsJson, "windows_hash").Trim();
            if (!string.Equals(requestedWindowsHash, selectedWindowsHash, StringComparison.Ordinal))
            {
                return EditorWindowCaptureUnavailable("windows_hash_changed", "Editor window inventory changed after discovery; call editor.windows.list and select the target again", window, selectedIndex, requestedWindowsHash, selectedWindowsHash);
            }

            var maxWidth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_width", 4096), 64, 4096);
            var maxHeight = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_height", 4096), 64, 4096);
            var includeEditorChrome = JsonUtil.ExtractBool(context.ArgsJson, "include_editor_chrome", false);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            if (includeEditorChrome)
            {
                return EditorWindowCaptureUnavailable("editor_chrome_not_supported", "editor.window.screenshot captures EditorWindow content only; include_editor_chrome must be false", window, selectedIndex, requestedWindowsHash, selectedWindowsHash);
            }

            var width = Mathf.CeilToInt(window.position.width);
            var height = Mathf.CeilToInt(window.position.height);
            if (width <= 0 || height <= 0 || width > maxWidth || height > maxHeight || width > 4096 || height > 4096)
            {
                return EditorWindowCaptureUnavailable("dimensions_exceeded", "Selected EditorWindow content exceeds max_width or max_height", window, selectedIndex, requestedWindowsHash, selectedWindowsHash);
            }

            if (!EditorWindowCaptureBackend.TryValidateTarget(window, requestedWindowsHash, out var currentWindowsHash, out var targetFailure))
            {
                return EditorWindowCaptureUnavailable(targetFailure.Reason, targetFailure.Message, window, selectedIndex, requestedWindowsHash, currentWindowsHash);
            }

            if (!EditorWindowCaptureBackend.TryGetCaptureMethod(out _, out var backendFailure))
            {
                return EditorWindowCaptureUnavailable("capture_api_unavailable", backendFailure, window, selectedIndex, requestedWindowsHash, currentWindowsHash, true, false);
            }

            var defaultPath = EditorWindowScreenshotPath(window, selectedIndex, requestedWindowsHash);
            var pathResult = ResolveScreenshotOutputPath(context.ArgsJson, defaultPath, out var relativePath, out var fullPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            if (dryRun)
            {
                return EditorWindowScreenshotStatus("dry_run", window, selectedIndex, requestedWindowsHash, relativePath, width, height, true, null, 0, false, false, true);
            }

            if (!TryEncodeEditorWindowCapture(window, width, height, out var png, out var captureFailure))
            {
                if (!EditorWindowCaptureBackend.TryValidateTarget(window, requestedWindowsHash, out currentWindowsHash, out targetFailure))
                {
                    return EditorWindowCaptureUnavailable(targetFailure.Reason, targetFailure.Message, window, selectedIndex, requestedWindowsHash, currentWindowsHash);
                }

                return EditorWindowCaptureFailure("unity_exception", captureFailure, window, selectedIndex, requestedWindowsHash, requestedWindowsHash, true, "capture_failed");
            }

            var pngValid = IsValidEncodedPng(png, width, height);
            if (!pngValid)
            {
                return EditorWindowCaptureFailure("unity_exception", "Unity internal EditorWindow capture did not encode as the expected bounded PNG", window, selectedIndex, requestedWindowsHash, requestedWindowsHash, true, "capture_corrupt_artifact");
            }

            var sha256 = Sha256Hex(png);
            if (!EditorWindowCaptureBackend.TryValidateTarget(window, requestedWindowsHash, out currentWindowsHash, out targetFailure))
            {
                return EditorWindowCaptureUnavailable(targetFailure.Reason, targetFailure.Message, window, selectedIndex, requestedWindowsHash, currentWindowsHash);
            }

            var stagingPath = EditorWindowStagingPath(fullPath);
            if (!GameViewCapturePolicy.TryStage(
                ProjectPaths.ProjectRoot,
                stagingPath,
                fullPath,
                png,
                staged => staged.Length == png.Length && IsValidEncodedPng(staged, width, height),
                out var stageErrorCode,
                out var stageMessage))
            {
                var stagingCleanupVerified = CleanupEditorWindowStaging(stagingPath);
                return EditorWindowCaptureFailure(stageErrorCode, stageMessage, window, selectedIndex, requestedWindowsHash, currentWindowsHash, true, stageErrorCode, true, stagingCleanupVerified);
            }

            if (!EditorWindowCaptureBackend.TryValidateTarget(window, requestedWindowsHash, out currentWindowsHash, out targetFailure))
            {
                var stagingCleanupVerified = CleanupEditorWindowStaging(stagingPath);
                return EditorWindowCaptureUnavailable(targetFailure.Reason, targetFailure.Message, window, selectedIndex, requestedWindowsHash, currentWindowsHash, false, false, stagingCleanupVerified);
            }

            if (!GameViewCapturePolicy.TryPublishReserved(ProjectPaths.ProjectRoot, stagingPath, fullPath, out var publication, out var publishErrorCode, out var publishMessage))
            {
                var stagingCleanupVerified = CleanupEditorWindowStaging(stagingPath);
                return EditorWindowCaptureFailure(publishErrorCode, publishMessage, window, selectedIndex, requestedWindowsHash, currentWindowsHash, true, publishErrorCode, true, stagingCleanupVerified);
            }

            var integrity = GameViewCapturePolicy.CompletedArtifactIntegrity(ProjectPaths.ProjectRoot, fullPath, png.LongLength, sha256);
            if (!string.Equals(integrity, "verified", StringComparison.Ordinal))
            {
                var rollbackSucceeded = GameViewCapturePolicy.RollbackPublication(publication, out var rollbackErrorCode, out var rollbackMessage);
                if (!rollbackSucceeded)
                {
                    return EditorWindowPublicationStateUncertain(
                        rollbackErrorCode,
                        rollbackMessage,
                        "rollback_failed_after_integrity_failure",
                        window,
                        selectedIndex,
                        requestedWindowsHash,
                        currentWindowsHash,
                        relativePath);
                }

                return EditorWindowCaptureFailure(
                    "unity_exception",
                    "Published PNG integrity could not be verified",
                    window,
                    selectedIndex,
                    requestedWindowsHash,
                    currentWindowsHash,
                    true,
                    "capture_corrupt_artifact");
            }

            if (!GameViewCapturePolicy.CommitPublication(publication, out var commitErrorCode, out var commitMessage))
            {
                var rollbackSucceeded = GameViewCapturePolicy.RollbackPublication(publication, out var rollbackErrorCode, out var rollbackMessage);
                if (!rollbackSucceeded)
                {
                    return EditorWindowPublicationStateUncertain(
                        rollbackErrorCode,
                        rollbackMessage,
                        "rollback_failed_after_commit_failure",
                        window,
                        selectedIndex,
                        requestedWindowsHash,
                        currentWindowsHash,
                        relativePath);
                }

                return EditorWindowCaptureFailure(
                    commitErrorCode,
                    commitMessage,
                    window,
                    selectedIndex,
                    requestedWindowsHash,
                    currentWindowsHash,
                    true,
                    commitErrorCode);
            }

            return EditorWindowScreenshotStatus("ok", window, selectedIndex, requestedWindowsHash, relativePath, width, height, true, sha256, png.LongLength, true, true, false);
        }

        [McpCommand(
            "desktop.region.screenshot",
            Title = "Desktop region screenshot",
            Description = "Capture a bounded desktop screen region to a project-local PNG",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = DesktopRegionScreenshotSchemaJson,
            Tags = new string[] { "desktop", "screen", "screenshot" },
            Capability = "desktop_screenshots",
            ResultShape = "screenshot_artifact",
            OutputLimitJson = DesktopRegionScreenshotOutputLimitJson,
            FeatureFlag = DesktopScreenshotFeatureFlag)]
        public static CommandExecutionResult DesktopRegionScreenshot(McpCommandContext context)
        {
            var x = JsonUtil.ExtractInt(context.ArgsJson, "x", int.MinValue);
            var y = JsonUtil.ExtractInt(context.ArgsJson, "y", int.MinValue);
            var width = JsonUtil.ExtractInt(context.ArgsJson, "width", 0);
            var height = JsonUtil.ExtractInt(context.ArgsJson, "height", 0);
            if (x == int.MinValue || y == int.MinValue || width < 1 || height < 1)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "x, y, width, and height are required");
            }

            if (width > 4096 || height > 4096)
            {
                return CommandExecutionResult.Error("dimensions_exceeded", "Desktop region exceeds maximum width or height");
            }

            var pathResult = ResolveScreenshotOutputPath(context.ArgsJson, DesktopRegionScreenshotPath(), out var relativePath, out var fullPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var backend = DesktopRegionBackend();
            if (dryRun)
            {
                return DesktopRegionScreenshotStatus("dry_run", x, y, width, height, relativePath, backend, false, backend == "unsupported" ? "Desktop region screenshots are supported only on Windows" : null);
            }

            if (!TryCaptureScreenRegion(x, y, width, height, fullPath, out var message))
            {
                return DesktopRegionScreenshotStatus("unsupported_platform", x, y, width, height, null, backend, false, message);
            }

            return DesktopRegionScreenshotStatus("ok", x, y, width, height, relativePath, backend, true, null);
        }

        [McpCommand(
            "game.view.screenshot",
            Title = "Game view screenshot",
            Description = "Capture the Unity Game view to a project-local PNG, optionally including overlay UI asynchronously",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = GameViewScreenshotSchemaJson,
            Tags = new string[] { "game", "screenshot" },
            Capability = "editor_screenshots",
            ResultShape = "screenshot_artifact",
            OutputLimitJson = GameViewScreenshotOutputLimitJson,
            WrapperWaitJson = GameViewScreenshotWrapperWaitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameViewScreenshot(McpCommandContext context)
        {
            var backendProvided = JsonUtil.HasProperty(context.ArgsJson, "backend");
            var requestedBackend = JsonUtil.ExtractString(context.ArgsJson, "backend").Trim();
            if (!backendProvided || string.IsNullOrEmpty(requestedBackend)) requestedBackend = "screen_capture";
            if (requestedBackend != "camera_render" && requestedBackend != "end_of_frame" && requestedBackend != "screen_capture")
            {
                return CommandExecutionResult.Error("invalid_field_value", "backend must be one of camera_render, end_of_frame, or screen_capture",
                    "{\"invalid_path\":\"args.backend\",\"capture_state\":\"not_scheduled\",\"fallback_attempted\":false}");
            }

            var effectiveBackend = requestedBackend == "end_of_frame" ? "screen_capture" : requestedBackend;
            var includeOverlayUiProvided = JsonUtil.HasProperty(context.ArgsJson, "include_overlay_ui");
            var includeOverlayUi = JsonUtil.ExtractBool(context.ArgsJson, "include_overlay_ui", effectiveBackend != "camera_render");
            if (includeOverlayUiProvided && ((includeOverlayUi && effectiveBackend == "camera_render") || (!includeOverlayUi && effectiveBackend == "screen_capture")))
            {
                return IncompatibleGameViewCaptureOptions(requestedBackend, includeOverlayUi);
            }

            if (effectiveBackend == "screen_capture")
            {
                return ScheduleGameViewOverlayCapture(context.ArgsJson, requestedBackend, requestedBackend == "end_of_frame", includeOverlayUi, includeOverlayUiProvided);
            }

            var camera = Camera.main ?? FirstEnabledCamera();
            if (camera == null)
            {
                return CommandExecutionResult.Error("unity_busy", "No enabled game camera is available");
            }

            return CaptureCamera(camera, "game-view", context.ArgsJson, -1, requestedBackend);
        }

        [McpCommand(
            "game.view.focus",
            Title = "Focus exact Game view",
            Description = "Dry-run-first explicit focus of one exact already-open Unity Game view selected from editor.windows.list",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = GameViewFocusSchemaJson,
            Tags = new string[] { "game", "screenshot", "focus" },
            Capability = "editor_screenshots",
            ResultShape = "status",
            OutputLimitJson = GameViewFocusOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameViewFocus(McpCommandContext context)
        {
            var selectResult = EditorWindowSelector.Select(context.ArgsJson, out var window, out var selectedIndex, out _, out var selectedWindowsHash);
            if (selectResult != null) return selectResult;

            var requestedWindowsHash = JsonUtil.ExtractString(context.ArgsJson, "windows_hash").Trim();
            if (!string.Equals(requestedWindowsHash, selectedWindowsHash, StringComparison.Ordinal))
            {
                return GameViewFocusError("catalog_stale", "Editor window inventory changed after discovery", window, selectedIndex, requestedWindowsHash, selectedWindowsHash, false);
            }
            if (!string.Equals(window.GetType().FullName, "UnityEditor.GameView", StringComparison.Ordinal))
            {
                return GameViewFocusError("invalid_field_value", "window_index must select an exact UnityEditor.GameView from editor.windows.list", window, selectedIndex, requestedWindowsHash, selectedWindowsHash, false);
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var confirmFocus = JsonUtil.ExtractBool(context.ArgsJson, "confirm_focus", false);
            if (!dryRun && !confirmFocus)
            {
                return CommandExecutionResult.Error("confirmation_required", "game.view.focus requires confirm_focus:true when dry_run:false",
                    "{\"focus_attempted\":false,\"native_focus_api_used\":false,\"next_valid_actions\":[\"game.view.focus\"]}");
            }

            var applicationFocusedBefore = Application.isFocused;
            var gameViewFocusedBefore = ReferenceEquals(EditorWindow.focusedWindow, window);
            if (dryRun)
            {
                return GameViewFocusResult("dry_run", window, selectedIndex, requestedWindowsHash, selectedWindowsHash, applicationFocusedBefore, applicationFocusedBefore, gameViewFocusedBefore, gameViewFocusedBefore, false, false, true);
            }

            try
            {
                window.Focus();
                window.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception)
            {
                return GameViewFocusError("unity_exception", "Unity could not focus the selected exact Game view", window, selectedIndex, requestedWindowsHash, selectedWindowsHash, true);
            }

            var applicationFocusedAfter = Application.isFocused;
            var gameViewFocusedAfter = ReferenceEquals(EditorWindow.focusedWindow, window);
            var focusVerified = applicationFocusedAfter && gameViewFocusedAfter;
            return GameViewFocusResult(
                focusVerified ? "focused" : gameViewFocusedAfter ? "focus_requested" : "focus_not_observed",
                window,
                selectedIndex,
                requestedWindowsHash,
                EditorUtilityCommands.EditorWindowsHash(out _),
                applicationFocusedBefore,
                applicationFocusedAfter,
                gameViewFocusedBefore,
                gameViewFocusedAfter,
                true,
                focusVerified,
                false);
        }

        [McpCommand(
            "game.view.focus_capture",
            Title = "Focus exact Game view and capture",
            Description = "Use one short-lived exact plan to focus an already-open Unity Game view and schedule one authoritative capture; its feature also exposes exact window discovery and capture status/cancel",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = GameViewFocusCaptureSchemaJson,
            Tags = new string[] { "game", "screenshot", "focus", "atomic" },
            Capability = "game_view_focus_capture",
            ResultShape = "screenshot_artifact",
            OutputLimitJson = GameViewFocusCaptureOutputLimitJson,
            FeatureFlag = GameViewFocusCaptureFeatureFlag)]
        public static CommandExecutionResult GameViewFocusCapture(McpCommandContext context)
        {
            var captureArgsResult = BuildGameViewFocusCaptureArgs(context.ArgsJson, out var captureArgsJson, out var captureArgsHash, out var requestedBackend, out var aliasResolved);
            if (captureArgsResult != null) return captureArgsResult;

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (dryRun)
            {
                var selectResult = EditorWindowSelector.Select(context.ArgsJson, out var window, out var selectedIndex, out _, out var selectedWindowsHash);
                if (selectResult != null) return selectResult;
                var requestedWindowsHash = JsonUtil.ExtractString(context.ArgsJson, "windows_hash").Trim();
                if (!string.Equals(requestedWindowsHash, selectedWindowsHash, StringComparison.Ordinal))
                {
                    return GameViewFocusCaptureError("catalog_stale", "Editor window inventory changed after discovery", string.Empty, false, false, false);
                }
                if (!IsExactGameView(window))
                {
                    return GameViewFocusCaptureError("invalid_field_value", "window_index must select an exact UnityEditor.GameView from editor.windows.list", string.Empty, false, false, false);
                }
                if (!TryReadGameViewFocusCaptureBridgeBinding(context, out var bridgeInstanceId, out var commandsHash, out var catalogGeneration))
                {
                    return GameViewFocusCaptureError("catalog_stale", "A ready exact Bridge catalog is required to create a focus/capture plan", string.Empty, false, false, false);
                }

                var now = DateTime.UtcNow;
                var plan = new GameViewFocusCapturePlan
                {
                    PlanId = "game-focus-capture-" + Guid.NewGuid().ToString("N"),
                    BridgeInstanceId = bridgeInstanceId,
                    CommandsHash = commandsHash,
                    CatalogGeneration = catalogGeneration,
                    WindowIndex = selectedIndex,
                    WindowsHash = selectedWindowsHash,
                    WindowRuntimeIdentity = EditorWindowSelector.RuntimeIdentity(window),
                    CaptureArgsJson = captureArgsJson,
                    CaptureArgsHash = captureArgsHash,
                    CreatedUtc = now,
                    ExpiresUtc = now.AddSeconds(GameViewFocusCapturePlanExpirySeconds),
                };
                plan.PlanHash = GameViewFocusCapturePlanHash(plan);
                lock (s_gameViewFocusCapturePlanLock)
                {
                    PruneGameViewFocusCapturePlansLocked(now);
                    if (s_gameViewFocusCapturePlans.Count >= MaxGameViewFocusCapturePlans)
                    {
                        return GameViewFocusCaptureError("unity_busy", "The bounded focus/capture plan store is full", string.Empty, false, false, false);
                    }
                    s_gameViewFocusCapturePlans[plan.PlanId] = plan;
                }
                return CommandExecutionResult.Ok(GameViewFocusCapturePlanJson(plan, window));
            }

            if (!JsonUtil.ExtractBool(context.ArgsJson, "confirm_focus", false))
            {
                return GameViewFocusCaptureError("confirmation_required", "game.view.focus_capture requires confirm_focus:true when dry_run:false", JsonUtil.ExtractString(context.ArgsJson, "plan_id").Trim(), false, false, false);
            }

            var planId = JsonUtil.ExtractString(context.ArgsJson, "plan_id").Trim();
            var planHash = JsonUtil.ExtractString(context.ArgsJson, "plan_hash").Trim();
            if (string.IsNullOrEmpty(planId) || string.IsNullOrEmpty(planHash))
            {
                return GameViewFocusCaptureError("precondition_required", "A plan_id and plan_hash from a fresh dry-run are required", planId, false, false, false);
            }

            GameViewFocusCapturePlan consumedPlan;
            lock (s_gameViewFocusCapturePlanLock)
            {
                var now = DateTime.UtcNow;
                PruneGameViewFocusCapturePlansLocked(now);
                if (!s_gameViewFocusCapturePlans.TryGetValue(planId, out consumedPlan)
                    || !string.Equals(consumedPlan.PlanHash, planHash, StringComparison.Ordinal))
                {
                    return GameViewFocusCaptureError("precondition_failed", "The focus/capture plan was not found, expired, consumed, or did not match plan_hash", planId, false, false, false);
                }
                s_gameViewFocusCapturePlans.Remove(planId);
            }

            if (!TryReadGameViewFocusCaptureBridgeBinding(context, out var currentBridgeInstanceId, out var currentCommandsHash, out var currentCatalogGeneration)
                || !string.Equals(consumedPlan.BridgeInstanceId, currentBridgeInstanceId, StringComparison.Ordinal)
                || !string.Equals(consumedPlan.CommandsHash, currentCommandsHash, StringComparison.Ordinal)
                || consumedPlan.CatalogGeneration != currentCatalogGeneration
                || !string.Equals(consumedPlan.CaptureArgsHash, captureArgsHash, StringComparison.Ordinal)
                || consumedPlan.WindowIndex != JsonUtil.ExtractInt(context.ArgsJson, "window_index", -1)
                || !string.Equals(consumedPlan.WindowsHash, JsonUtil.ExtractString(context.ArgsJson, "windows_hash").Trim(), StringComparison.Ordinal))
            {
                return GameViewFocusCaptureError("precondition_failed", "The consumed plan no longer matches the exact Bridge, catalog, window selector, or capture arguments", planId, true, false, false);
            }

            var executionSelectResult = EditorWindowSelector.Select(context.ArgsJson, out var executionWindow, out var executionWindowIndex, out _, out var executionWindowsHash);
            if (executionSelectResult != null)
            {
                return GameViewFocusCaptureError(executionSelectResult.ErrorCode, executionSelectResult.ErrorMessage, planId, true, false, false, executionSelectResult.ErrorDetailsJson);
            }
            if (!IsExactGameView(executionWindow)
                || executionWindowIndex != consumedPlan.WindowIndex
                || !string.Equals(executionWindowsHash, consumedPlan.WindowsHash, StringComparison.Ordinal)
                || !string.Equals(EditorWindowSelector.RuntimeIdentity(executionWindow), consumedPlan.WindowRuntimeIdentity, StringComparison.Ordinal))
            {
                return GameViewFocusCaptureError("precondition_failed", "The exact already-open UnityEditor.GameView changed after the dry-run", planId, true, false, false);
            }

            try
            {
                executionWindow.Focus();
                executionWindow.Repaint();
                EditorApplication.QueuePlayerLoopUpdate();
            }
            catch (Exception)
            {
                return GameViewFocusCaptureError("unity_exception", "Unity could not focus the selected exact Game view", planId, true, true, false);
            }

            var focusVerified = Application.isFocused
                && ReferenceEquals(EditorWindow.focusedWindow, executionWindow)
                && IsExactGameView(executionWindow)
                && string.Equals(EditorWindowSelector.RuntimeIdentity(executionWindow), consumedPlan.WindowRuntimeIdentity, StringComparison.Ordinal);
            if (!focusVerified)
            {
                return GameViewFocusCaptureError("capture_precondition_failed", "Managed focus of the exact Game view was not synchronously verified; capture was not scheduled", planId, true, true, false);
            }

            var focusExecution = new GameViewFocusCaptureExecution
            {
                PlanId = planId,
                PlanHash = planHash,
                WindowRuntimeIdentity = consumedPlan.WindowRuntimeIdentity,
                WindowsHashBeforeFocus = consumedPlan.WindowsHash,
                WindowsHashAfterFocus = EditorUtilityCommands.EditorWindowsHash(out _),
            };
            var captureResult = ScheduleGameViewOverlayCapture(captureArgsJson, requestedBackend, aliasResolved, true, true, focusExecution);
            if (captureResult.Status != "ok")
            {
                return GameViewFocusCaptureError(captureResult.ErrorCode, captureResult.ErrorMessage, planId, true, true, true, captureResult.ErrorDetailsJson);
            }
            return captureResult;
        }

        static CommandExecutionResult BuildGameViewFocusCaptureArgs(
            string argsJson,
            out string captureArgsJson,
            out string captureArgsHash,
            out string requestedBackend,
            out bool aliasResolved)
        {
            captureArgsJson = string.Empty;
            captureArgsHash = string.Empty;
            aliasResolved = false;
            requestedBackend = JsonUtil.ExtractString(argsJson, "backend").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(requestedBackend)) requestedBackend = "screen_capture";
            if (requestedBackend != "screen_capture" && requestedBackend != "end_of_frame")
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "backend must be screen_capture or end_of_frame", "backend", "enum_value_not_allowed");
            }
            aliasResolved = requestedBackend == "end_of_frame";
            if (!JsonUtil.ExtractBool(argsJson, "include_overlay_ui", true))
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "include_overlay_ui must be true for authoritative Game View capture", "include_overlay_ui", "enum_value_not_allowed");
            }

            var widthProvided = JsonUtil.HasProperty(argsJson, "width");
            var heightProvided = JsonUtil.HasProperty(argsJson, "height");
            if (widthProvided != heightProvided)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "width and height must be both omitted or both provided", "{\"capture_state\":\"not_scheduled\",\"focus_attempted\":false,\"artifact_produced\":false}");
            }
            var width = widthProvided ? JsonUtil.ExtractInt(argsJson, "width", 0) : 0;
            var height = heightProvided ? JsonUtil.ExtractInt(argsJson, "height", 0) : 0;
            if (widthProvided && (width < 64 || width > 4096 || height < 64 || height > 4096))
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "width and height must be between 64 and 4096", "width", "out_of_range");
            }
            var settleFrames = JsonUtil.ExtractInt(argsJson, "settle_frames", 2);
            var timeoutMs = JsonUtil.ExtractInt(argsJson, "timeout_ms", 15000);
            if (settleFrames < 0 || settleFrames > 120)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "settle_frames must be between 0 and 120", "settle_frames", "out_of_range");
            }
            if (timeoutMs < 1000 || timeoutMs > 30000)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "timeout_ms must be between 1000 and 30000", "timeout_ms", "out_of_range");
            }
            if (!JsonUtil.HasProperty(argsJson, "output_path") || string.IsNullOrWhiteSpace(JsonUtil.ExtractString(argsJson, "output_path")))
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "An explicit output_path is required for a focus/capture plan", "output_path", "required_field_missing");
            }
            var pathResult = ResolveScreenshotOutputPath(argsJson, string.Empty, out var relativePath, out _);
            if (pathResult != null) return pathResult;

            var builder = new StringBuilder();
            builder.Append("{\"backend\":").Append(JsonUtil.String(requestedBackend));
            builder.Append(",\"include_overlay_ui\":true");
            if (widthProvided)
            {
                builder.Append(",\"width\":").Append(width).Append(",\"height\":").Append(height);
            }
            builder.Append(",\"settle_frames\":").Append(settleFrames);
            builder.Append(",\"timeout_ms\":").Append(timeoutMs);
            builder.Append(",\"output_path\":").Append(JsonUtil.String(relativePath));
            builder.Append(",\"dry_run\":false}");
            captureArgsJson = builder.ToString();
            captureArgsHash = "sha256:" + Sha256Hex(Encoding.UTF8.GetBytes(captureArgsJson));
            return null;
        }

        static bool TryReadGameViewFocusCaptureBridgeBinding(McpCommandContext context, out string instanceId, out string commandsHash, out long catalogGeneration)
        {
            instanceId = context != null && context.State != null ? context.State.InstanceId : string.Empty;
            commandsHash = context != null && context.State != null ? context.State.CommandsHash : string.Empty;
            catalogGeneration = context != null && context.State != null ? context.State.CatalogGeneration : 0;
            return !string.IsNullOrEmpty(instanceId)
                && !string.IsNullOrEmpty(commandsHash)
                && !string.Equals(commandsHash, "bootstrap-pending", StringComparison.Ordinal)
                && catalogGeneration > 0;
        }

        static bool IsExactGameView(EditorWindow window)
        {
            return window != null && string.Equals(window.GetType().FullName, "UnityEditor.GameView", StringComparison.Ordinal);
        }

        static string GameViewFocusCapturePlanHash(GameViewFocusCapturePlan plan)
        {
            var input = "game.view.focus_capture\n"
                + plan.PlanId + "\n"
                + plan.BridgeInstanceId + "\n"
                + plan.CommandsHash + "\n"
                + plan.CatalogGeneration + "\n"
                + plan.WindowIndex + "\n"
                + plan.WindowsHash + "\n"
                + plan.WindowRuntimeIdentity + "\n"
                + plan.CaptureArgsHash + "\n"
                + plan.ExpiresUtc.Ticks;
            return "sha256:" + Sha256Hex(Encoding.UTF8.GetBytes(input));
        }

        static string GameViewFocusCapturePlanJson(GameViewFocusCapturePlan plan, EditorWindow window)
        {
            return "{\"status\":\"dry_run\",\"plan_id\":" + JsonUtil.String(plan.PlanId)
                + ",\"plan_hash\":" + JsonUtil.String(plan.PlanHash)
                + ",\"single_use\":true,\"precondition_not_authorization\":true,\"gateway_approval_required\":true"
                + ",\"created_utc\":" + JsonUtil.String(plan.CreatedUtc.ToString("o"))
                + ",\"expires_utc\":" + JsonUtil.String(plan.ExpiresUtc.ToString("o"))
                + ",\"bridge_binding\":{\"instance_id\":" + JsonUtil.String(plan.BridgeInstanceId)
                + ",\"commands_hash\":" + JsonUtil.String(plan.CommandsHash)
                + ",\"catalog_generation\":" + plan.CatalogGeneration + "}"
                + ",\"window_runtime_identity\":" + JsonUtil.String(plan.WindowRuntimeIdentity)
                + ",\"windows_hash\":" + JsonUtil.String(plan.WindowsHash)
                + ",\"selected_window\":" + EditorWindowSelector.MetadataJson(window, plan.WindowIndex)
                + ",\"capture_args_hash\":" + JsonUtil.String(plan.CaptureArgsHash)
                + ",\"capture_args\":" + plan.CaptureArgsJson
                + ",\"focus_attempted\":false,\"focus_verified\":false,\"capture_scheduled\":false,\"artifact_produced\":false"
                + ",\"native_focus_api_used\":false,\"synthetic_input_used\":false,\"view_opened\":false,\"view_switched\":false,\"fallback_attempted\":false,\"replay_supported\":false"
                + ",\"next_valid_actions\":[\"game.view.focus_capture\"]}";
        }

        static CommandExecutionResult GameViewFocusCaptureError(
            string code,
            string message,
            string planId,
            bool planConsumed,
            bool focusAttempted,
            bool focusVerified,
            string captureErrorDetailsJson = "")
        {
            var details = new StringBuilder();
            details.Append("{\"plan_id\":").Append(string.IsNullOrEmpty(planId) ? "null" : JsonUtil.String(planId));
            details.Append(",\"plan_consumed\":").Append(JsonUtil.Bool(planConsumed));
            details.Append(",\"focus_attempted\":").Append(JsonUtil.Bool(focusAttempted));
            details.Append(",\"focus_verified\":").Append(JsonUtil.Bool(focusVerified));
            details.Append(",\"capture_scheduled\":false,\"artifact_produced\":false");
            details.Append(",\"native_focus_api_used\":false,\"synthetic_input_used\":false,\"view_opened\":false,\"view_switched\":false,\"fallback_attempted\":false,\"replay_supported\":false");
            if (!string.IsNullOrEmpty(captureErrorDetailsJson))
            {
                details.Append(",\"capture_error_details\":").Append(captureErrorDetailsJson);
            }
            details.Append('}');
            return CommandExecutionResult.Error(code, message, details.ToString());
        }

        static void PruneGameViewFocusCapturePlansLocked(DateTime now)
        {
            var expired = new List<string>();
            foreach (var pair in s_gameViewFocusCapturePlans)
            {
                if (pair.Value == null || pair.Value.ExpiresUtc <= now) expired.Add(pair.Key);
            }
            for (var index = 0; index < expired.Count; index++) s_gameViewFocusCapturePlans.Remove(expired[index]);
        }

        [McpCommand(
            "game.view.screenshot.status",
            Title = "Game view screenshot status",
            Description = "Read status for an asynchronous overlay-inclusive Game view screenshot capture",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = GameViewScreenshotStatusSchemaJson,
            Tags = new string[] { "game", "screenshot" },
            Capability = "editor_screenshots",
            ResultShape = "screenshot_artifact",
            OutputLimitJson = GameViewScreenshotOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameViewScreenshotStatus(McpCommandContext context)
        {
            var captureId = JsonUtil.ExtractString(context.ArgsJson, "capture_id").Trim();
            if (string.IsNullOrEmpty(captureId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument capture_id is required");
            }

            lock (s_gameViewCaptureLock)
            {
                var now = DateTime.UtcNow;
                PruneGameViewCapturesLocked(now);
                if (!s_gameViewCaptures.TryGetValue(captureId, out var capture))
                {
                    return CommandExecutionResult.Error("not_found", "Game view screenshot capture_id was not found or expired");
                }

                ExpireGameViewCaptureLocked(capture, now);
                return CommandExecutionResult.Ok(GameViewCaptureJson(capture));
            }
        }

        [McpCommand(
            "game.view.screenshot.cancel",
            Title = "Cancel Game view screenshot",
            Description = "Cooperatively cancel and clean up one asynchronous Game view screenshot capture",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = GameViewScreenshotCancelSchemaJson,
            Tags = new string[] { "game", "screenshot", "cancel", "recovery" },
            Capability = "editor_screenshots",
            ResultShape = "status",
            OutputLimitJson = GameViewScreenshotOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameViewScreenshotCancel(McpCommandContext context)
        {
            var captureId = JsonUtil.ExtractString(context.ArgsJson, "capture_id").Trim();
            if (string.IsNullOrEmpty(captureId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument capture_id is required");
            }

            lock (s_gameViewCaptureLock)
            {
                var now = DateTime.UtcNow;
                PruneGameViewCapturesLocked(now);
                if (!s_gameViewCaptures.TryGetValue(captureId, out var capture))
                {
                    return CommandExecutionResult.Ok("{\"capture_id\":" + JsonUtil.String(captureId)
                        + ",\"status\":\"not_found\",\"cancel_requested\":false,\"cleanup_performed\":false}");
                }

                ExpireGameViewCaptureLocked(capture, now);
                if (IsActiveGameViewCaptureStatus(capture.Status))
                {
                    capture.CancelRequested = true;
                    MarkGameViewCaptureTerminalLocked(capture, "cancelled", string.Empty, "Screenshot capture was cancelled; any late coroutine completion will be discarded.", now);
                }
                else if (capture.ResolutionRestoreRequired && !capture.ResolutionRestoreVerified)
                {
                    capture.CancelRequested = true;
                    if (TryRestoreGameViewResolutionLocked(capture) && capture.ErrorCode == "capture_resolution_restore_pending")
                    {
                        capture.ErrorCode = "capture_domain_reload_interrupted";
                        capture.Message = "The persisted capture was discarded without replay and its exact Game View rendering dimensions were restored.";
                    }
                    capture.UpdatedUtc = now;
                    PersistGameViewCaptureLocked(capture);
                }

                return CommandExecutionResult.Ok(GameViewCaptureJson(capture));
            }
        }

        [McpCommand(
            "camera.list",
            Title = "List cameras",
            Description = "Read a bounded list of scene cameras available for visual capture",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CameraListSchemaJson,
            Tags = new string[] { "camera", "screenshot" },
            Capability = "editor_screenshots",
            ResultShape = "bounded_list",
            OutputLimitJson = CameraListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CameraList(McpCommandContext context)
        {
            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", false);
            var cameras = DiscoverCameras(includeInactive);
            var builder = new StringBuilder();
            var count = Math.Min(cameras.Count, MaxCameras);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendCameraMetadata(builder, cameras[index], index);
            }

            return CommandExecutionResult.Ok("{\"cameras\":[" + builder + "],\"count\":" + count
                + ",\"truncated\":" + JsonUtil.Bool(cameras.Count > MaxCameras) + "}");
        }

        [McpCommand(
            "camera.screenshot",
            Title = "Camera screenshot",
            Description = "Capture a selected scene camera to a project-local PNG",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = CameraScreenshotSchemaJson,
            Tags = new string[] { "camera", "screenshot" },
            Capability = "editor_screenshots",
            ResultShape = "object_details",
            OutputLimitJson = ScreenshotOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CameraScreenshot(McpCommandContext context)
        {
            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", false);
            var scenePath = JsonUtil.ExtractString(context.ArgsJson, "scene_path");
            var objectPath = JsonUtil.ExtractString(context.ArgsJson, "object_path");
            var instanceId = JsonUtil.ExtractInt(context.ArgsJson, "instance_id", 0);
            var entityId = JsonUtil.ExtractString(context.ArgsJson, "entity_id").Trim();
            var hasScenePath = !string.IsNullOrEmpty(scenePath);
            var hasObjectPath = !string.IsNullOrEmpty(objectPath);
            var hasInstanceId = instanceId != 0;
            var hasEntityId = !string.IsNullOrEmpty(entityId);
            var requestedIndex = JsonUtil.ExtractInt(context.ArgsJson, "camera_index", -1);
            if (requestedIndex >= MaxCameras)
            {
                return CommandExecutionResult.Error("invalid_field_value", "camera_index must be between 0 and 99");
            }

            if (hasScenePath != hasObjectPath)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "scene_path and object_path must be provided together");
            }

            if (hasScenePath && HasLegacyCameraSelector(context.ArgsJson, requestedIndex))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "scene_path/object_path cannot be combined with camera_path, camera_name, or camera_index");
            }

            if (hasInstanceId && hasEntityId)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide only one of instance_id or entity_id");
            }

            if ((hasInstanceId || hasEntityId) && (hasScenePath || HasLegacyCameraSelector(context.ArgsJson, requestedIndex)))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "instance_id/entity_id cannot be combined with camera_path, camera_name, camera_index, scene_path, or object_path");
            }

            Camera camera;
            int selectedIndex;
            if (hasScenePath)
            {
                var selectResult = SelectCameraBySceneObject(context.ArgsJson, includeInactive, out camera, out selectedIndex);
                if (selectResult != null)
                {
                    return selectResult;
                }
            }
            else if (hasInstanceId || hasEntityId)
            {
                var selectResult = SelectCameraByRuntimeId(instanceId, entityId, includeInactive, out camera, out selectedIndex);
                if (selectResult != null)
                {
                    return selectResult;
                }
            }
            else
            {
                camera = SelectCamera(context.ArgsJson, includeInactive, out selectedIndex);
            }

            if (camera == null)
            {
                return CommandExecutionResult.Error("not_found", "No matching camera is available");
            }

            return CaptureCamera(camera, "camera", context.ArgsJson, selectedIndex);
        }

        static bool HasLegacyCameraSelector(string argsJson, int requestedIndex)
        {
            return requestedIndex >= 0
                || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "camera_path"))
                || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "camera_name"));
        }

        static SceneView ActiveSceneView()
        {
            var sceneView = SceneView.lastActiveSceneView;
            if (sceneView == null && SceneView.sceneViews.Count > 0)
            {
                sceneView = SceneView.sceneViews[0] as SceneView;
            }

            return sceneView;
        }

        static CommandExecutionResult MissingSceneViewCamera()
        {
            return CommandExecutionResult.Error(
                "unity_busy",
                "No Scene view camera is available. In Unity Editor, open Window > General > Scene or focus an existing Scene view, then explicitly retry; alternatively select a camera or Game view capture target.",
                "{\"capture_state\":\"not_scheduled\",\"automatic_window_action_attempted\":false,\"operator_action_required\":true"
                    + ",\"operator_instructions\":[\"Open Window > General > Scene in Unity Editor, or focus an existing Scene view.\",\"After its camera is available, explicitly retry the original Scene view command.\"]"
                    + ",\"recovery_commands\":[{\"command\":\"camera.list\",\"args_json\":\"{}\"},{\"command\":\"camera.screenshot\",\"guidance\":\"Choose one camera returned by camera.list and pass its bounded selector.\"},{\"command\":\"game.view.screenshot\",\"args_json\":\"{}\"}]"
                    + ",\"next_valid_actions\":[\"operator_open_or_focus_scene_view\",\"retry_scene_view_command_after_camera_available\",\"call_camera.list_then_camera.screenshot_with_explicit_target\",\"call_game.view.screenshot_as_explicit_alternative\"]}");
        }

        static CommandExecutionResult MissingSceneViewCameraForOverlay()
        {
            return CommandExecutionResult.Error(
                "unity_busy",
                "No Scene view camera is available. In Unity Editor, open Window > General > Scene or focus an existing Scene view, then explicitly retry scene.view.overlay.",
                "{\"operation_state\":\"not_started\",\"automatic_window_action_attempted\":false,\"operator_action_required\":true"
                    + ",\"operator_instructions\":[\"Open Window > General > Scene in Unity Editor, or focus an existing Scene view.\",\"After its camera is available, explicitly retry scene.view.overlay.\"]"
                    + ",\"next_valid_actions\":[\"operator_open_or_focus_scene_view\",\"retry_scene.view.overlay_after_camera_available\"]}");
        }

        static Camera FirstEnabledCamera()
        {
            var cameras = DiscoverCameras(false);
            for (var index = 0; index < cameras.Count; index++)
            {
                if (cameras[index] != null && cameras[index].enabled)
                {
                    return cameras[index];
                }
            }

            return null;
        }

        static Camera SelectCamera(string argsJson, bool includeInactive, out int selectedIndex)
        {
            selectedIndex = -1;
            var cameras = DiscoverCameras(includeInactive);
            var path = JsonUtil.ExtractString(argsJson, "camera_path");
            var name = JsonUtil.ExtractString(argsJson, "camera_name");
            var requestedIndex = JsonUtil.ExtractInt(argsJson, "camera_index", -1);
            for (var index = 0; index < cameras.Count; index++)
            {
                var camera = cameras[index];
                if ((!string.IsNullOrEmpty(path) && ObjectPath(camera.gameObject) == path)
                    || (!string.IsNullOrEmpty(name) && camera.name == name)
                    || (requestedIndex >= 0 && requestedIndex == index))
                {
                    selectedIndex = index;
                    return camera;
                }
            }

            if (!string.IsNullOrEmpty(path) || !string.IsNullOrEmpty(name) || requestedIndex >= 0)
            {
                return null;
            }

            var main = Camera.main;
            if (main != null && IsDiscoverableCamera(main, includeInactive))
            {
                selectedIndex = cameras.IndexOf(main);
                return main;
            }

            var fallback = FirstEnabledCamera();
            selectedIndex = fallback == null ? -1 : cameras.IndexOf(fallback);
            return fallback;
        }

        static CommandExecutionResult SelectCameraBySceneObject(string argsJson, bool includeInactive, out Camera camera, out int selectedIndex)
        {
            camera = null;
            selectedIndex = -1;
            var pathResult = RequireSceneAssetPath(argsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(argsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            var scene = FindLoadedScene(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Error("scene_not_loaded", "scene_path must identify an already loaded scene");
            }

            var target = FindByPath(scene.GetRootGameObjects(), objectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("not_found", "object_path does not resolve in scene_path");
            }

            camera = target.GetComponent<Camera>();
            if (camera == null)
            {
                return CommandExecutionResult.Error("not_found", "object_path does not reference a GameObject with a Camera component");
            }

            if (!IsDiscoverableCamera(camera, includeInactive))
            {
                return CommandExecutionResult.Error("camera_not_available", "Camera is disabled or inactive; set include_inactive:true to capture it");
            }

            selectedIndex = DiscoverCameras(includeInactive).IndexOf(camera);
            return null;
        }

        static CommandExecutionResult SelectCameraByRuntimeId(int instanceId, string entityId, bool includeInactive, out Camera camera, out int selectedIndex)
        {
            camera = null;
            selectedIndex = -1;
            var resolveResult = TryResolveObjectByRuntimeId(instanceId, entityId, out var selectedObject);
            if (resolveResult != null)
            {
                return resolveResult;
            }

            camera = selectedObject as Camera;
            if (camera == null)
            {
                var gameObject = selectedObject as GameObject;
                camera = gameObject == null ? null : gameObject.GetComponent<Camera>();
            }

            if (camera == null)
            {
                return CommandExecutionResult.Error("not_found", "object id does not reference a Camera or GameObject with a Camera component");
            }

            if (!IsDiscoverableCamera(camera, includeInactive))
            {
                return CommandExecutionResult.Error("camera_not_available", "Camera is disabled or inactive; set include_inactive:true to capture it");
            }

            selectedIndex = DiscoverCameras(includeInactive).IndexOf(camera);
            return null;
        }

        static CommandExecutionResult TryBuildObjectFocusBounds(string argsJson, bool includeInactive, out Bounds bounds, out string focusJson)
        {
            bounds = new Bounds(Vector3.zero, Vector3.one);
            focusJson = "{}";
            var pathResult = RequireSceneAssetPath(argsJson, "scene_path", out var scenePath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var objectPath = JsonUtil.ExtractString(argsJson, "object_path");
            if (string.IsNullOrEmpty(objectPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument object_path is required");
            }

            var scene = FindLoadedScene(scenePath);
            if (!scene.IsValid() || !scene.isLoaded)
            {
                return CommandExecutionResult.Error("scene_not_loaded", "scene_path must identify an already loaded scene");
            }

            var target = FindByPath(scene.GetRootGameObjects(), objectPath);
            if (target == null)
            {
                return CommandExecutionResult.Error("not_found", "object_path does not resolve in scene_path");
            }

            if (!includeInactive && !target.activeInHierarchy)
            {
                return CommandExecutionResult.Error("object_not_available", "object_path resolves to an inactive object; set include_inactive:true to focus it");
            }

            var rendererCount = EncapsulateRendererBounds(target.GetComponentsInChildren<Renderer>(includeInactive), includeInactive, out bounds);
            if (rendererCount == 0)
            {
                bounds = new Bounds(target.transform.position, Vector3.one);
            }

            focusJson = "{\"mode\":\"object\",\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"object_path\":" + JsonUtil.String(NormalizeObjectPath(objectPath))
                + ",\"active_in_hierarchy\":" + JsonUtil.Bool(target.activeInHierarchy)
                + ",\"renderer_count\":" + rendererCount + "}";
            return null;
        }

        static CommandExecutionResult TryBuildSceneViewFocusBounds(SceneView sceneView, bool includeInactive, out Bounds bounds, out string focusJson)
        {
            var fallbackSize = Math.Max(1f, sceneView.size);
            bounds = new Bounds(sceneView.pivot, Vector3.one * fallbackSize);
            focusJson = "{\"mode\":\"scene_view_pivot\",\"renderer_count\":0}";

            var renderers = Resources.FindObjectsOfTypeAll<Renderer>();
            var planes = GeometryUtility.CalculateFrustumPlanes(sceneView.camera);
            var visibleCount = 0;
            var hasVisibleBounds = false;
            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                if (!ShouldUseRendererForFocus(renderer, includeInactive) || !GeometryUtility.TestPlanesAABB(planes, renderer.bounds))
                {
                    continue;
                }

                if (!hasVisibleBounds)
                {
                    bounds = renderer.bounds;
                    hasVisibleBounds = true;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }

                visibleCount++;
            }

            if (visibleCount > 0)
            {
                focusJson = "{\"mode\":\"scene_view_visible\",\"renderer_count\":" + visibleCount + "}";
                return null;
            }

            var loadedRendererCount = EncapsulateRendererBounds(renderers, includeInactive, out var loadedBounds);
            if (loadedRendererCount > 0)
            {
                bounds = loadedBounds;
                focusJson = "{\"mode\":\"loaded_scene_renderers\",\"renderer_count\":" + loadedRendererCount + "}";
            }

            return null;
        }

        static int EncapsulateRendererBounds(Renderer[] renderers, bool includeInactive, out Bounds bounds)
        {
            bounds = new Bounds(Vector3.zero, Vector3.one);
            var count = 0;
            for (var index = 0; index < renderers.Length; index++)
            {
                var renderer = renderers[index];
                if (!ShouldUseRendererForFocus(renderer, includeInactive))
                {
                    continue;
                }

                if (count == 0)
                {
                    bounds = renderer.bounds;
                }
                else
                {
                    bounds.Encapsulate(renderer.bounds);
                }

                count++;
            }

            return count;
        }

        static bool ShouldUseRendererForFocus(Renderer renderer, bool includeInactive)
        {
            return renderer != null && renderer.enabled && IsSceneObject(renderer.gameObject) && (includeInactive || renderer.gameObject.activeInHierarchy);
        }

        static CommandExecutionResult TryResolveObjectByRuntimeId(int instanceId, string entityId, out UnityEngine.Object selectedObject)
        {
            selectedObject = null;
#if UNITY_6000_4_OR_NEWER
            ulong runtimeEntityId;
            if (!string.IsNullOrEmpty(entityId))
            {
                if (!ulong.TryParse(entityId, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out runtimeEntityId) || runtimeEntityId == 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "entity_id must be a positive unsigned integer string");
                }
            }
            else
            {
                if (instanceId <= 0)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "instance_id must be positive in Unity 6; use entity_id when available");
                }

                runtimeEntityId = (ulong)instanceId;
            }

            selectedObject = EditorUtility.EntityIdToObject(UnityEngine.EntityId.FromULong(runtimeEntityId));
#else
            if (!string.IsNullOrEmpty(entityId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "entity_id is available only in Unity 6 or newer");
            }

#pragma warning disable 0618
            selectedObject = EditorUtility.InstanceIDToObject(instanceId);
#pragma warning restore 0618
#endif
            if (selectedObject == null)
            {
                return CommandExecutionResult.Error("not_found", "object id does not resolve to an object");
            }

            return null;
        }

        static List<Camera> DiscoverCameras(bool includeInactive)
        {
            var cameras = new List<Camera>();
            var objects = Resources.FindObjectsOfTypeAll<Camera>();
            Array.Sort(objects, CompareCamerasByPath);
            for (var index = 0; index < objects.Length; index++)
            {
                if (IsDiscoverableCamera(objects[index], includeInactive))
                {
                    cameras.Add(objects[index]);
                }
            }

            return cameras;
        }

        static bool IsDiscoverableCamera(Camera camera, bool includeInactive)
        {
            return camera != null && IsSceneObject(camera.gameObject) && (includeInactive || (camera.enabled && camera.gameObject.activeInHierarchy));
        }

        static int CompareCamerasByPath(Camera left, Camera right)
        {
            return string.Compare(ObjectPath(left == null ? null : left.gameObject), ObjectPath(right == null ? null : right.gameObject), StringComparison.Ordinal);
        }

        static string BuildOverlayJson(Camera camera, string query, int maxObjects, bool includeInactive, out int matched, out bool truncated)
        {
            var objects = Resources.FindObjectsOfTypeAll<GameObject>();
            Array.Sort(objects, CompareGameObjectsByPath);
            var builder = new StringBuilder();
            var count = 0;
            matched = 0;
            truncated = false;
            for (var index = 0; index < objects.Length; index++)
            {
                var gameObject = objects[index];
                if (!IsSceneObject(gameObject) || (!includeInactive && !gameObject.activeInHierarchy))
                {
                    continue;
                }

                var path = ObjectPath(gameObject);
                if (!string.IsNullOrEmpty(query) && gameObject.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                var viewport = camera.WorldToViewportPoint(gameObject.transform.position);
                if (viewport.z <= 0 || viewport.x < 0 || viewport.x > 1 || viewport.y < 0 || viewport.y > 1)
                {
                    continue;
                }

                matched++;
                if (count >= maxObjects)
                {
                    truncated = true;
                    break;
                }

                AppendOverlay(builder, gameObject, path, viewport, count > 0);
                count++;
            }

            return builder.ToString();
        }

        static void AppendOverlay(StringBuilder builder, GameObject gameObject, string path, Vector3 viewport, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append("{\"path\":").Append(JsonUtil.String(path));
            builder.Append(",\"name\":").Append(JsonUtil.String(gameObject.name));
            builder.Append(",\"scene\":").Append(JsonUtil.String(gameObject.scene.name));
            builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(gameObject.activeInHierarchy));
            builder.Append(",\"viewport\":{");
            AppendFloat(builder, "x", viewport.x, false);
            AppendFloat(builder, "y", viewport.y, true);
            AppendFloat(builder, "z", viewport.z, true);
            builder.Append("}}");
        }

        static void AppendFloat(StringBuilder builder, string name, float value, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append(JsonUtil.String(name)).Append(':').Append(FloatJson(value));
        }

        static bool IsSceneObject(GameObject gameObject)
        {
            return gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded;
        }

        static CommandExecutionResult EditorWindowCaptureUnavailable(
            string reason,
            string message,
            EditorWindow window,
            int selectedIndex,
            string requestedWindowsHash,
            string currentWindowsHash,
            bool targetIdentityProved = false,
            bool backendAvailable = false,
            bool stagingCleanupVerified = true)
        {
            return EditorWindowCaptureFailure(
                "capture_target_unavailable",
                message,
                window,
                selectedIndex,
                requestedWindowsHash,
                currentWindowsHash,
                targetIdentityProved,
                reason,
                backendAvailable,
                stagingCleanupVerified);
        }

        static CommandExecutionResult EditorWindowCaptureFailure(
            string errorCode,
            string message,
            EditorWindow window,
            int selectedIndex,
            string requestedWindowsHash,
            string currentWindowsHash,
            bool targetIdentityProved,
            string reason = "capture_failed",
            bool backendAvailable = true,
            bool stagingCleanupVerified = true)
        {
            var normalizedErrorCode = NormalizeEditorWindowCaptureErrorCode(errorCode);
            return CommandExecutionResult.Error(
                normalizedErrorCode,
                string.IsNullOrEmpty(message) ? "EditorWindow screenshot was not published" : message,
                "{\"reason\":" + JsonUtil.String(reason)
                + ",\"capture_state\":\"not_published\""
                + ",\"artifact_produced\":false"
                + ",\"artifact_path\":null"
                + ",\"project_relative_path\":null"
                + ",\"artifact_ref\":null"
                + ",\"backend\":" + JsonUtil.String(EditorWindowCaptureBackend.BackendName)
                + ",\"backend_available\":" + JsonUtil.Bool(backendAvailable)
                + ",\"target_binding\":" + JsonUtil.String(targetIdentityProved ? EditorWindowCaptureBackend.TargetBinding : "fail_closed")
                + ",\"target_identity_proved\":" + JsonUtil.Bool(targetIdentityProved)
                + ",\"fallback_attempted\":false"
                + ",\"native_hwnd_capture_attempted\":false"
                + ",\"focus_change_attempted\":false"
                + ",\"staging_cleanup_verified\":" + JsonUtil.Bool(stagingCleanupVerified)
                + ",\"requested_windows_hash\":" + JsonUtil.String(requestedWindowsHash)
                + ",\"current_windows_hash\":" + JsonUtil.String(currentWindowsHash)
                + ",\"selected_window\":" + EditorWindowSelector.MetadataJson(window, selectedIndex)
                + ",\"privacy_warning\":" + JsonUtil.String(ScreenshotPrivacyWarning)
                + ",\"next_valid_actions\":[\"editor.windows.list\",\"editor.window.inspect\"]}");
        }

        static CommandExecutionResult EditorWindowPublicationStateUncertain(
            string errorCode,
            string message,
            string reason,
            EditorWindow window,
            int selectedIndex,
            string requestedWindowsHash,
            string currentWindowsHash,
            string relativePath)
        {
            return CommandExecutionResult.Error(
                NormalizeEditorWindowCaptureErrorCode(errorCode),
                string.IsNullOrEmpty(message) ? "EditorWindow screenshot publication state is uncertain after rollback failure" : message,
                "{\"reason\":" + JsonUtil.String(reason)
                + ",\"capture_state\":\"publication_state_uncertain\""
                + ",\"artifact_produced\":null"
                + ",\"artifact_path\":" + JsonUtil.String(relativePath)
                + ",\"project_relative_path\":" + JsonUtil.String(relativePath)
                + ",\"artifact_ref\":" + JsonUtil.String(relativePath)
                + ",\"backend\":" + JsonUtil.String(EditorWindowCaptureBackend.BackendName)
                + ",\"backend_available\":true"
                + ",\"target_binding\":" + JsonUtil.String(EditorWindowCaptureBackend.TargetBinding)
                + ",\"target_identity_proved\":true"
                + ",\"fallback_attempted\":false"
                + ",\"native_hwnd_capture_attempted\":false"
                + ",\"focus_change_attempted\":false"
                + ",\"rollback_verified\":false"
                + ",\"requested_windows_hash\":" + JsonUtil.String(requestedWindowsHash)
                + ",\"current_windows_hash\":" + JsonUtil.String(currentWindowsHash)
                + ",\"selected_window\":" + EditorWindowSelector.MetadataJson(window, selectedIndex)
                + ",\"privacy_warning\":" + JsonUtil.String(ScreenshotPrivacyWarning)
                + ",\"next_valid_actions\":[\"artifact.metadata\"]}");
        }

        internal static string NormalizeEditorWindowCaptureErrorCode(string errorCode)
        {
            if (string.Equals(errorCode, "capture_target_unavailable", StringComparison.Ordinal)
                || string.Equals(errorCode, "path_not_allowed", StringComparison.Ordinal)
                || string.Equals(errorCode, "path_traversal_rejected", StringComparison.Ordinal)
                || string.Equals(errorCode, "absolute_path_rejected", StringComparison.Ordinal)
                || string.Equals(errorCode, "filename_too_long", StringComparison.Ordinal)
                || string.Equals(errorCode, "unity_exception", StringComparison.Ordinal))
            {
                return errorCode;
            }

            if (string.Equals(errorCode, "path_reparse_point_rejected", StringComparison.Ordinal))
            {
                return "path_not_allowed";
            }

            return "unity_exception";
        }

        internal static CommandExecutionResult EditorWindowScreenshotStatus(
            string status,
            EditorWindow window,
            int selectedIndex,
            string windowsHash,
            string relativePath,
            int width,
            int height,
            bool targetIdentityProved,
            string sha256,
            long bytes,
            bool pngSignatureValid,
            bool pngDimensionsValid,
            bool dryRun)
        {
            var artifactProduced = string.Equals(status, "ok", StringComparison.Ordinal) && !dryRun;
            var builder = new StringBuilder();
            builder.Append("{\"status\":").Append(JsonUtil.String(status));
            builder.Append(",\"capture_state\":").Append(JsonUtil.String(dryRun ? "planned" : status));
            builder.Append(",\"artifact_produced\":").Append(JsonUtil.Bool(artifactProduced));
            builder.Append(",\"artifact_path\":").Append(artifactProduced ? JsonUtil.String(relativePath) : "null");
            builder.Append(",\"project_relative_path\":").Append(artifactProduced ? JsonUtil.String(relativePath) : "null");
            builder.Append(",\"artifact_ref\":").Append(artifactProduced ? JsonUtil.String(relativePath) : "null");
            builder.Append(",\"planned_artifact_path\":").Append(string.IsNullOrEmpty(relativePath) ? "null" : JsonUtil.String(relativePath));
            builder.Append(",\"dimensions\":{\"width\":").Append(width).Append(",\"height\":").Append(height).Append('}');
            builder.Append(",\"width\":").Append(width);
            builder.Append(",\"height\":").Append(height);
            builder.Append(",\"backend\":").Append(JsonUtil.String(EditorWindowCaptureBackend.BackendName));
            builder.Append(",\"backend_available\":true");
            builder.Append(",\"target_binding\":").Append(JsonUtil.String(EditorWindowCaptureBackend.TargetBinding));
            builder.Append(",\"include_editor_chrome\":false");
            builder.Append(",\"target_identity_proved\":").Append(JsonUtil.Bool(targetIdentityProved));
            builder.Append(",\"fallback_attempted\":false");
            builder.Append(",\"native_hwnd_capture_attempted\":false");
            builder.Append(",\"focus_change_attempted\":false");
            builder.Append(",\"selected_window\":").Append(EditorWindowSelector.MetadataJson(window, selectedIndex));
            builder.Append(",\"windows_hash\":").Append(JsonUtil.String(windowsHash));
            builder.Append(",\"composition\":\"editor_panel_content\",\"authoritative\":false");
            builder.Append(",\"format\":\"png\"");
            if (dryRun)
            {
                builder.Append(",\"png\":null");
            }
            else
            {
                builder.Append(",\"png\":{\"signature_valid\":").Append(JsonUtil.Bool(pngSignatureValid));
                builder.Append(",\"dimensions_valid\":").Append(JsonUtil.Bool(pngDimensionsValid));
                builder.Append(",\"width\":").Append(width).Append(",\"height\":").Append(height).Append('}');
            }
            builder.Append(",\"sha256\":").Append(string.IsNullOrEmpty(sha256) ? "null" : JsonUtil.String(sha256));
            builder.Append(",\"bytes\":").Append(bytes);
            builder.Append(",\"artifact_integrity\":{\"status\":").Append(JsonUtil.String(artifactProduced ? "verified" : "not_applicable"));
            builder.Append(",\"verified\":").Append(JsonUtil.Bool(artifactProduced)).Append('}');
            builder.Append(",\"privacy_warning\":").Append(JsonUtil.String(ScreenshotPrivacyWarning));
            builder.Append(",\"dry_run\":").Append(JsonUtil.Bool(dryRun));
            builder.Append(",\"next_actions\":").Append(artifactProduced ? "[\"artifact.metadata\"]" : "[\"editor.window.screenshot\"]");
            builder.Append('}');
            return CommandExecutionResult.Ok(builder.ToString());
        }

        static CommandExecutionResult DesktopRegionScreenshotStatus(string status, int x, int y, int width, int height, string relativePath, string backend, bool visiblePixelsOnly, string message)
        {
            var builder = new StringBuilder();
            builder.Append("{\"status\":").Append(JsonUtil.String(status));
            if (!string.IsNullOrEmpty(message))
            {
                builder.Append(",\"message\":").Append(JsonUtil.String(message));
            }

            builder.Append(",\"artifact_path\":").Append(string.IsNullOrEmpty(relativePath) ? "null" : JsonUtil.String(relativePath));
            builder.Append(",\"project_relative_path\":").Append(string.IsNullOrEmpty(relativePath) ? "null" : JsonUtil.String(relativePath));
            builder.Append(",\"artifact_ref\":").Append(string.IsNullOrEmpty(relativePath) ? "null" : JsonUtil.String(relativePath));
            builder.Append(",\"dimensions\":{\"width\":").Append(width).Append(",\"height\":").Append(height).Append('}');
            builder.Append(",\"region\":{\"x\":").Append(x).Append(",\"y\":").Append(y).Append(",\"width\":").Append(width).Append(",\"height\":").Append(height).Append('}');
            builder.Append(",\"privacy_warning\":").Append(JsonUtil.String(ScreenshotPrivacyWarning));
            builder.Append(",\"backend\":").Append(JsonUtil.String(string.IsNullOrEmpty(backend) ? "unsupported" : backend));
            builder.Append(",\"chrome_included\":false");
            builder.Append(",\"visible_pixels_only\":").Append(JsonUtil.Bool(visiblePixelsOnly));
            builder.Append(",\"format\":\"png\"");
            builder.Append(",\"next_actions\":[\"job.artifact.read\",\"artifact.metadata\"]");
            builder.Append('}');
            return CommandExecutionResult.Ok(builder.ToString());
        }

        static CommandExecutionResult ScheduleGameViewOverlayCapture(
            string argsJson,
            string requestedBackend,
            bool aliasResolved,
            bool includeOverlayUi,
            bool includeOverlayUiProvided,
            GameViewFocusCaptureExecution focusExecution = null)
        {
            var widthProvided = JsonUtil.HasProperty(argsJson, "width");
            var heightProvided = JsonUtil.HasProperty(argsJson, "height");
            if (widthProvided != heightProvided)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "width and height must be both omitted or both provided for authoritative Game View capture",
                    "{\"capture_state\":\"not_scheduled\",\"invalid_path\":\"args.width,args.height\",\"fallback_attempted\":false}");
            }

            var gameViewAvailable = GameViewCaptureEditorContext.TryInspect(out var gameViewEvidence);
            if (!gameViewAvailable || !gameViewEvidence.IsGameView)
            {
                return GameViewRequiredError("not_scheduled", gameViewEvidence);
            }

            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            if (screenWidth < 1 || screenHeight < 1 || screenWidth > 4096 || screenHeight > 4096)
            {
                return CommandExecutionResult.Error("dimensions_exceeded", "Current Game View Screen dimensions are unavailable or exceed 4096 pixels",
                    "{\"capture_state\":\"not_scheduled\",\"screen_width\":" + screenWidth + ",\"screen_height\":" + screenHeight + ",\"fallback_attempted\":false}");
            }

            var requestedWidth = widthProvided ? JsonUtil.ExtractInt(argsJson, "width", 0) : 0;
            var requestedHeight = heightProvided ? JsonUtil.ExtractInt(argsJson, "height", 0) : 0;
            var editorWidth = gameViewEvidence.Width;
            var editorHeight = gameViewEvidence.Height;
            if (editorWidth < 1 || editorHeight < 1 || editorWidth > 4096 || editorHeight > 4096)
            {
                return CommandExecutionResult.Error("dimensions_exceeded", "Selected Game View rendering resolution is unavailable or exceeds 4096 pixels",
                    "{\"capture_state\":\"not_scheduled\",\"editor_game_view_resolution\":{\"width\":" + editorWidth + ",\"height\":" + editorHeight + "},\"fallback_attempted\":false}");
            }
            var dimensionsMatch = GameViewCapturePolicy.ResolutionMatches(editorWidth, editorHeight, screenWidth, screenHeight, requestedWidth, requestedHeight);
            var resolutionChangeRequired = GameViewCapturePolicy.ResolutionChangeRequired(editorWidth, editorHeight, screenWidth, screenHeight, requestedWidth, requestedHeight);
            if (!widthProvided && !dimensionsMatch)
            {
                return CommandExecutionResult.Error("capture_dimensions_mismatch", "Current Game View rendering resolution and Screen dimensions must agree before capture",
                    "{\"capture_state\":\"not_scheduled\",\"requested_dimensions\":{\"width\":" + requestedWidth + ",\"height\":" + requestedHeight
                    + "},\"editor_game_view_resolution\":{\"width\":" + editorWidth + ",\"height\":" + editorHeight
                    + "},\"screen_dimensions\":{\"width\":" + screenWidth + ",\"height\":" + screenHeight + "},\"resolution_change_supported\":" + JsonUtil.Bool(GameViewCaptureEditorContext.SupportsCustomRenderingResolution)
                    + ",\"final_game_view_resized\":false,\"resize_attempted\":false,\"fallback_attempted\":false}");
            }
            if (resolutionChangeRequired && !GameViewCaptureEditorContext.SupportsCustomRenderingResolution)
            {
                return CommandExecutionResult.Error("capture_resolution_change_unsupported", "This Unity version cannot select and restore a custom authoritative Game View resolution",
                    "{\"capture_state\":\"not_scheduled\",\"requested_dimensions\":{\"width\":" + requestedWidth + ",\"height\":" + requestedHeight
                    + "},\"editor_game_view_resolution\":{\"width\":" + editorWidth + ",\"height\":" + editorHeight
                    + "},\"screen_dimensions\":{\"width\":" + screenWidth + ",\"height\":" + screenHeight + "},\"resolution_change_supported\":false,\"resize_attempted\":false,\"fallback_attempted\":false}");
            }

            var dryRun = JsonUtil.ExtractBool(argsJson, "dry_run", false);
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid())
            {
                return CommandExecutionResult.Error("capture_scene_required", "An active scene is required for authoritative Game View capture",
                    "{\"capture_state\":\"not_scheduled\",\"fallback_attempted\":false}");
            }
            if (!GameViewSceneIdentity.TryCaptureLoadedScenes(out var loadedSceneSet))
            {
                return CommandExecutionResult.Error("capture_scene_set_unavailable", "The bounded full loaded scene set could not be fingerprinted",
                    "{\"capture_state\":\"not_scheduled\",\"max_loaded_scenes\":" + GameViewSceneIdentity.MaxLoadedScenes + ",\"fallback_attempted\":false}");
            }
            var barrier = default(PlayModeTransitionTracker.PlayModeBarrierEvidence);
            if (!dryRun && !PlayModeTransitionTracker.TryGetCurrentPlayModeBarrier(out barrier))
            {
                return CommandExecutionResult.Error("capture_lifecycle_barrier_required", "A fresh EnteredPlayMode lifecycle barrier is required before authoritative Game View capture",
                    "{\"capture_state\":\"not_scheduled\",\"play_mode_barrier_observed\":false,\"fallback_attempted\":false,\"next_valid_actions\":[\"editor.playmode.enter\",\"editor.status\",\"game.view.screenshot\"]}");
            }
            if (dryRun)
            {
                barrier = default;
            }
            else if (AuthoritativeCaptureCannotAdvance(out var preflightDetails))
            {
                return CommandExecutionResult.Error("capture_precondition_failed", "Authoritative Game View capture requires current focused, unpaused, stable Play Mode",
                    preflightDetails);
            }

            var settleFrames = Clamp(JsonUtil.ExtractInt(argsJson, "settle_frames", 2), 0, 120);
            var timeoutMs = Clamp(JsonUtil.ExtractInt(argsJson, "timeout_ms", 15000), 1000, 30000);
            var pathResult = ResolveScreenshotOutputPath(argsJson, ScreenshotPath("game-view"), out var relativePath, out var fullPath);
            if (pathResult != null) return pathResult;

            var now = DateTime.UtcNow;
            var capture = new GameViewCaptureState
            {
                CaptureId = NewGameViewCaptureId(),
                Status = dryRun ? "dry_run" : resolutionChangeRequired ? "resizing" : "scheduled",
                RequestedBackend = requestedBackend,
                EffectiveBackend = "screen_capture",
                BackendAliasResolved = aliasResolved,
                Authoritative = true,
                Composition = "final_game_view",
                IncludeOverlayUi = includeOverlayUi,
                IncludeOverlayUiProvided = includeOverlayUiProvided,
                RelativePath = relativePath,
                FullPath = fullPath,
                RequestedWidth = requestedWidth,
                RequestedHeight = requestedHeight,
                ScheduleWidth = resolutionChangeRequired ? requestedWidth : editorWidth,
                ScheduleHeight = resolutionChangeRequired ? requestedHeight : editorHeight,
                ScheduleScreenWidth = screenWidth,
                ScheduleScreenHeight = screenHeight,
                Width = resolutionChangeRequired ? requestedWidth : editorWidth,
                Height = resolutionChangeRequired ? requestedHeight : editorHeight,
                DimensionsSource = resolutionChangeRequired ? "requested_custom_rendering_resolution" : "play_mode_window_rendering_resolution_at_schedule",
                DimensionsMatch = dimensionsMatch,
                ResolutionChangeRequired = resolutionChangeRequired,
                ResolutionChangeSupported = GameViewCaptureEditorContext.SupportsCustomRenderingResolution,
                ResolutionOriginalWidth = editorWidth,
                ResolutionOriginalHeight = editorHeight,
                ResolutionTargetWidth = resolutionChangeRequired ? requestedWidth : editorWidth,
                ResolutionTargetHeight = resolutionChangeRequired ? requestedHeight : editorHeight,
                ResolutionRestoreRequired = resolutionChangeRequired && !dryRun,
                SettleFramesRequested = settleFrames,
                TimeoutMs = timeoutMs,
                ScheduleFrame = resolutionChangeRequired ? 0 : Time.frameCount,
                CreatedUtc = now,
                UpdatedUtc = now,
                ExpiresUtc = now.AddSeconds(GameViewCaptureExpirySeconds),
                DeadlineUtc = now.AddMilliseconds(timeoutMs),
                ApplicationFocusedAtSchedule = Application.isFocused,
                EditorPausedAtSchedule = EditorApplication.isPaused,
                BridgeInstanceId = UnityBridgeRuntime.InstanceId,
                PlayModeGeneration = dryRun ? 0 : barrier.Generation,
                PlayModeBarrierId = dryRun ? string.Empty : barrier.BarrierId,
                PlayModeEnteredUtc = dryRun ? default : barrier.EnteredUtc,
                ScheduledSceneHandle = GameViewSceneIdentity.HandleIdentity(activeScene),
                ScheduledScenePath = activeScene.path,
                ScheduledSceneName = activeScene.name,
                ScheduledSceneSetCount = loadedSceneSet.Count,
                ScheduledSceneSetFingerprint = loadedSceneSet.Fingerprint,
                PlayModeWindowApiSupported = gameViewEvidence.ApiSupported,
                GameViewConfirmed = gameViewEvidence.IsGameView,
                SelectedPlayModeViewType = gameViewEvidence.ViewType,
                CurrentEditorWidth = editorWidth,
                CurrentEditorHeight = editorHeight,
                CurrentScreenWidth = screenWidth,
                CurrentScreenHeight = screenHeight,
                ReadbackBackend = "pending",
                FidelityStatus = "pending",
                AutomaticFocusAttempted = focusExecution != null,
                FocusVerified = focusExecution != null,
                FocusCapturePlanId = focusExecution != null ? focusExecution.PlanId : string.Empty,
                FocusCapturePlanHash = focusExecution != null ? focusExecution.PlanHash : string.Empty,
                FocusWindowRuntimeIdentity = focusExecution != null ? focusExecution.WindowRuntimeIdentity : string.Empty,
                FocusWindowsHashBefore = focusExecution != null ? focusExecution.WindowsHashBeforeFocus : string.Empty,
                FocusWindowsHashAfter = focusExecution != null ? focusExecution.WindowsHashAfterFocus : string.Empty,
            };
            capture.StagingPath = GameViewCapturePolicy.BuildStagingPath(fullPath, capture.CaptureId);

            if (dryRun)
            {
                capture.DryRun = true;
                return CommandExecutionResult.Ok(GameViewCaptureJson(capture));
            }

            lock (s_gameViewCaptureLock)
            {
                PruneGameViewCapturesLocked(now);
                foreach (var existing in s_gameViewCaptures.Values)
                {
                    if (!existing.Authoritative) continue;
                    if (existing.ResolutionRestoreRequired && !existing.ResolutionRestoreVerified)
                    {
                        TryRestoreGameViewResolutionLocked(existing);
                        PersistGameViewCaptureLocked(existing);
                    }
                    if (IsActiveGameViewCaptureStatus(existing.Status) || (existing.ResolutionRestoreRequired && !existing.ResolutionRestoreVerified))
                    {
                        return CommandExecutionResult.Error("unity_busy", "A prior authoritative Game View capture or resolution restoration is still active",
                            "{\"capture_state\":\"not_scheduled\",\"active_capture_id\":" + JsonUtil.String(existing.CaptureId) + ",\"fallback_attempted\":false}");
                    }
                }
                if (s_gameViewCaptures.Count >= MaxGameViewCaptureHistory && !TryRemoveOldestTerminalCaptureLocked())
                {
                    return CommandExecutionResult.Error("unity_busy", "The bounded Game view screenshot history is full of active captures",
                        "{\"reason\":\"capture_queue_full\",\"capture_state\":\"not_scheduled\",\"max_active_captures\":" + MaxGameViewCaptureHistory
                        + ",\"automatic_capture_attempted\":false,\"fallback_attempted\":false,\"next_valid_actions\":[\"game.view.screenshot.status\",\"game.view.screenshot.cancel\"]}");
                }
                s_gameViewCaptures[capture.CaptureId] = capture;
                PersistGameViewCaptureLocked(capture);
            }

            EnsureGameViewScreenCapturePollHook();
            if (resolutionChangeRequired)
            {
                ApplyRequestedGameViewResolution(capture);
            }
            else
            {
                BeginGameViewCapture(capture);
            }
            return CommandExecutionResult.Ok(GameViewCaptureJson(capture));
        }

        static void ApplyRequestedGameViewResolution(GameViewCaptureState capture)
        {
            lock (s_gameViewCaptureLock)
            {
                if (!IsActiveGameViewCaptureStatus(capture.Status) || capture.Status != "resizing") return;
                capture.ResolutionApplyAttempted = true;
                PersistGameViewCaptureLocked(capture);
            }

            if (!GameViewCaptureEditorContext.TrySetCustomRenderingResolution(capture.ResolutionTargetWidth, capture.ResolutionTargetHeight, out var failureReason))
            {
                lock (s_gameViewCaptureLock)
                {
                    capture.ResolutionApplyFailureReason = failureReason;
                    if (IsActiveGameViewCaptureStatus(capture.Status))
                    {
                        MarkGameViewCaptureTerminalLocked(capture, "interrupted", "capture_resolution_change_failed", "Unity could not apply the requested bounded Game View rendering resolution", DateTime.UtcNow);
                    }
                }
                return;
            }

            lock (s_gameViewCaptureLock)
            {
                capture.ResolutionApplySucceeded = true;
                PersistGameViewCaptureLocked(capture);
            }
            if (!TryStartGameViewCaptureAfterResolutionSettled(capture)) EditorApplication.QueuePlayerLoopUpdate();
        }

        static bool TryStartGameViewCaptureAfterResolutionSettled(GameViewCaptureState capture)
        {
            if (!GameViewCaptureEditorContext.TryInspect(out var evidence) || !evidence.IsGameView) return false;
            var screenWidth = Screen.width;
            var screenHeight = Screen.height;
            lock (s_gameViewCaptureLock)
            {
                if (!IsActiveGameViewCaptureStatus(capture.Status) || capture.Status != "resizing") return false;
                capture.PlayModeWindowApiSupported = evidence.ApiSupported;
                capture.GameViewConfirmed = evidence.IsGameView;
                capture.SelectedPlayModeViewType = evidence.ViewType;
                capture.PlayModeWindowFailureReason = evidence.FailureReason;
                capture.CurrentEditorWidth = evidence.Width;
                capture.CurrentEditorHeight = evidence.Height;
                capture.CurrentScreenWidth = screenWidth;
                capture.CurrentScreenHeight = screenHeight;
                if (!GameViewCapturePolicy.ResolutionMatches(evidence.Width, evidence.Height, screenWidth, screenHeight, capture.ResolutionTargetWidth, capture.ResolutionTargetHeight))
                {
                    PersistGameViewCaptureLocked(capture);
                    return false;
                }

                capture.ResolutionApplyVerified = true;
                capture.DimensionsMatch = true;
                capture.ScheduleWidth = evidence.Width;
                capture.ScheduleHeight = evidence.Height;
                capture.ScheduleScreenWidth = screenWidth;
                capture.ScheduleScreenHeight = screenHeight;
                capture.Width = evidence.Width;
                capture.Height = evidence.Height;
                capture.DimensionsSource = "custom_rendering_resolution_at_schedule";
                capture.ScheduleFrame = Time.frameCount;
                capture.Status = "scheduled";
                capture.UpdatedUtc = DateTime.UtcNow;
                PersistGameViewCaptureLocked(capture);
            }
            BeginGameViewCapture(capture);
            return true;
        }

        static void BeginGameViewCapture(GameViewCaptureState capture)
        {
            if (!GameViewCaptureBridge.Begin(capture.CaptureId, capture.SettleFramesRequested, capture.DeadlineUtc.Ticks, OnGameViewCaptureProgress, OnGameViewCaptureCompleted))
            {
                MarkGameViewCaptureTerminal(capture.CaptureId, "interrupted", "capture_bridge_unavailable", "The runtime Game View capture bridge could not be attached");
            }
            else
            {
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        static void OnGameViewCaptureProgress(GameViewCaptureBridge.CaptureProgress progress)
        {
            lock (s_gameViewCaptureLock)
            {
                if (!s_gameViewCaptures.TryGetValue(progress.RequestId, out var capture) || !IsActiveGameViewCaptureStatus(capture.Status)) return;
                var now = DateTime.UtcNow;
                if (now >= capture.DeadlineUtc)
                {
                    MarkGameViewCaptureTerminalLocked(capture, "timed_out", "capture_timeout", "The capture reached its exact deadline before submission completed", now);
                    return;
                }
                if (progress.SubmissionFrame > 0)
                {
                    capture.SubmissionScreenWidth = progress.Width;
                    capture.SubmissionScreenHeight = progress.Height;
                    capture.ReadbackBackend = progress.ReadbackBackend;
                }
                var validation = ValidateCurrentCaptureContext(capture, 0, 0);
                if (!validation.IsValid)
                {
                    ApplyContextFailureLocked(capture, validation, now);
                    return;
                }
                if (progress.SubmissionFrame > 0)
                {
                    capture.SubmissionWidth = capture.CurrentEditorWidth;
                    capture.SubmissionHeight = capture.CurrentEditorHeight;
                    capture.DimensionsSource = "play_mode_window_rendering_resolution_at_submission";
                }
                capture.Status = progress.Stage == "submitted" ? "submitted" : "settling";
                capture.SettleFramesObserved = progress.SettledFrames;
                capture.LastSettleFrame = progress.LastSettledFrame;
                capture.LastRenderedFrame = progress.LastRenderedFrame;
                if (progress.SubmissionFrame > 0)
                {
                    capture.SubmissionFrame = progress.SubmissionFrame;
                    capture.SubmissionUtc = now;
                }
                capture.UpdatedUtc = now;
                PersistGameViewCaptureLocked(capture);
            }
        }

        static void OnGameViewCaptureCompleted(GameViewCaptureBridge.CaptureResult result)
        {
            GameViewCaptureState capture;
            var now = DateTime.UtcNow;
            lock (s_gameViewCaptureLock)
            {
                if (!s_gameViewCaptures.TryGetValue(result.RequestId, out capture)) return;
                if (!IsActiveGameViewCaptureStatus(capture.Status))
                {
                    capture.LateCompletionDiscarded = true;
                    capture.CleanupPerformed |= CleanupGameViewCaptureStaging(capture);
                    PersistGameViewCaptureLocked(capture);
                    return;
                }
                capture.CompletionDeadlineChecked = true;
                capture.SubmissionFrame = result.SubmissionFrame;
                capture.CompletionFrame = result.CompletionFrame;
                capture.SubmissionUtc = TicksToUtc(result.SubmissionUtcTicks, now);
                capture.CompletionUtc = TicksToUtc(result.CompletionUtcTicks, now);
                capture.ReadbackBackend = string.IsNullOrEmpty(result.ReadbackBackend) ? capture.ReadbackBackend : result.ReadbackBackend;
                capture.ReadbackCompletionAccepted = result.Succeeded && GameViewCapturePolicy.ShouldAcceptReadbackCompletion(
                    capture.Status,
                    result.Abandoned,
                    capture.DeadlineUtc.Ticks,
                    result.CompletionUtcTicks);
                if (result.DeadlineExceeded || result.CompletionUtcTicks >= capture.DeadlineUtc.Ticks || now >= capture.DeadlineUtc)
                {
                    capture.LateCompletionDiscarded = true;
                    MarkGameViewCaptureTerminalLocked(capture, "timed_out", "capture_timeout", "The capture completed after its exact deadline and was discarded", now);
                    return;
                }
                if (result.Width > 0 && result.Height > 0)
                {
                    capture.SubmissionScreenWidth = result.Width;
                    capture.SubmissionScreenHeight = result.Height;
                }
                var immediateCompletionValidation = ValidateCurrentCaptureContext(capture, result.Width, result.Height);
                if (!immediateCompletionValidation.IsValid)
                {
                    ApplyContextFailureLocked(capture, immediateCompletionValidation, now);
                    return;
                }
                if (capture.SubmissionWidth <= 0)
                {
                    capture.SubmissionWidth = capture.CurrentEditorWidth;
                    capture.SubmissionHeight = capture.CurrentEditorHeight;
                    capture.DimensionsSource = "play_mode_window_rendering_resolution_at_submission";
                }
                if (result.Abandoned)
                {
                    capture.ReadbackAbandoned = true;
                    capture.LateCompletionDiscarded = true;
                    MarkGameViewCaptureTerminalLocked(capture, "interrupted", "capture_readback_abandoned", "GPU readback completion was abandoned and discarded.", now);
                    return;
                }
                if (!result.Succeeded)
                {
                    MarkGameViewCaptureTerminalLocked(capture, "interrupted", "capture_runtime_interrupted", "Runtime Game View capture did not complete.", now);
                    return;
                }

                capture.Width = result.Width;
                capture.Height = result.Height;
                capture.SampleCount = result.Metrics.Count;
                capture.ZeroSampleCount = result.Metrics.Zero;
                capture.NearBlackSampleCount = result.Metrics.NearBlack;
                capture.TransparentSampleCount = result.Metrics.Transparent;
                capture.UniformSampleCount = result.Metrics.Uniform;
                capture.UniqueSampleCount = result.Metrics.Unique;
                capture.SentinelSampleCount = result.Metrics.Sentinel;
                capture.DimensionsMatch = true;
                if (!IsValidEncodedPng(result.Png, result.Width, result.Height))
                {
                    MarkGameViewCaptureTerminalLocked(capture, "corrupt", "capture_corrupt_artifact", "The capture did not encode as the expected bounded PNG", now);
                    return;
                }
                if (HasStrongFidelityFailure(capture))
                {
                    capture.FidelityStatus = "strong_failure";
                    MarkGameViewCaptureTerminalLocked(capture, "fidelity_suspect", "capture_fidelity_suspect", "The bounded sample strongly indicates an uninitialized capture target.", now);
                    return;
                }
            }

            string sha256;
            try
            {
                sha256 = Sha256Hex(result.Png);
            }
            catch (Exception)
            {
                MarkGameViewCaptureTerminal(capture.CaptureId, "corrupt", GameViewCapturePolicy.PublishFailureCode, GameViewCapturePolicy.PublishFailureMessage);
                return;
            }
            if (!GameViewCapturePolicy.TryStage(
                ProjectPaths.ProjectRoot,
                capture.StagingPath,
                capture.FullPath,
                result.Png,
                staged => staged.Length == result.Png.Length && IsValidEncodedPng(staged, result.Width, result.Height),
                out var stageErrorCode,
                out var stageMessage))
            {
                MarkGameViewCaptureTerminal(capture.CaptureId, "corrupt", stageErrorCode, stageMessage);
                return;
            }

            lock (s_gameViewCaptureLock)
            {
                if (!s_gameViewCaptures.TryGetValue(capture.CaptureId, out var current) || !ReferenceEquals(current, capture) || !IsActiveGameViewCaptureStatus(capture.Status))
                {
                    capture.LateCompletionDiscarded = true;
                    capture.CleanupPerformed |= CleanupGameViewCaptureStaging(capture);
                    PersistGameViewCaptureLocked(capture);
                    return;
                }
                now = DateTime.UtcNow;
                if (now >= capture.DeadlineUtc)
                {
                    capture.LateCompletionDiscarded = true;
                    MarkGameViewCaptureTerminalLocked(capture, "timed_out", "capture_timeout", "The staged capture reached its deadline before publication and was discarded", now);
                    return;
                }
                var reservationValidation = ValidateCurrentCaptureContext(capture, result.Width, result.Height);
                if (!reservationValidation.IsValid)
                {
                    ApplyContextFailureLocked(capture, reservationValidation, now);
                    return;
                }
                capture.PublishReserved = true;
                PersistGameViewCaptureLocked(capture);
            }

            if (!GameViewCapturePolicy.TryPublishReserved(ProjectPaths.ProjectRoot, capture.StagingPath, capture.FullPath, out var publication, out var publishErrorCode, out var publishMessage))
            {
                lock (s_gameViewCaptureLock)
                {
                    capture.PublishReserved = false;
                    if (IsActiveGameViewCaptureStatus(capture.Status))
                    {
                        MarkGameViewCaptureTerminalLocked(capture, "corrupt", publishErrorCode, publishMessage, DateTime.UtcNow);
                    }
                }
                return;
            }

            PublicationFinalizeDecision decision;
            CaptureContextValidation finalValidation = default;
            lock (s_gameViewCaptureLock)
            {
                var sameOwner = s_gameViewCaptures.TryGetValue(capture.CaptureId, out var current) && ReferenceEquals(current, capture);
                var active = sameOwner && IsActiveGameViewCaptureStatus(capture.Status);
                var beforeDeadline = DateTime.UtcNow < capture.DeadlineUtc;
                if (sameOwner && active && capture.PublishReserved && beforeDeadline)
                {
                    finalValidation = ValidateCurrentCaptureContext(capture, result.Width, result.Height);
                }
                decision = GameViewCapturePolicy.FinalizePublication(sameOwner, active, capture.PublishReserved, beforeDeadline, finalValidation.IsValid);
            }

            if (decision.ReportCompleted)
            {
                bool resolutionRestored;
                lock (s_gameViewCaptureLock)
                {
                    resolutionRestored = TryRestoreGameViewResolutionLocked(capture);
                    PersistGameViewCaptureLocked(capture);
                }
                if (!resolutionRestored)
                {
                    var restoreRollbackSucceeded = GameViewCapturePolicy.RollbackPublication(publication, out var restoreRollbackErrorCode, out var restoreRollbackMessage);
                    lock (s_gameViewCaptureLock)
                    {
                        capture.PublishReserved = false;
                        if (IsActiveGameViewCaptureStatus(capture.Status))
                        {
                            MarkGameViewCaptureTerminalLocked(
                                capture,
                                restoreRollbackSucceeded ? "interrupted" : "corrupt",
                                restoreRollbackSucceeded ? "capture_resolution_restore_failed" : restoreRollbackErrorCode,
                                restoreRollbackSucceeded ? "The prior Game View rendering resolution could not be restored before artifact commit" : restoreRollbackMessage,
                                DateTime.UtcNow);
                        }
                    }
                    return;
                }

                // Filesystem commit and SessionState persistence cannot be atomic; keep them adjacent and fail closed on cleanup failure.
                if (!GameViewCapturePolicy.CommitPublication(publication, out var commitErrorCode, out var commitMessage))
                {
                    var commitRollbackSucceeded = GameViewCapturePolicy.RollbackPublication(publication, out var commitRollbackErrorCode, out var commitRollbackMessage);
                    lock (s_gameViewCaptureLock)
                    {
                        capture.PublishReserved = false;
                        if (IsActiveGameViewCaptureStatus(capture.Status))
                        {
                            MarkGameViewCaptureTerminalLocked(
                                capture,
                                commitRollbackSucceeded ? "interrupted" : "corrupt",
                                commitRollbackSucceeded ? commitErrorCode : commitRollbackErrorCode,
                                commitRollbackSucceeded ? commitMessage : commitRollbackMessage,
                                DateTime.UtcNow);
                        }
                    }
                    return;
                }

                lock (s_gameViewCaptureLock)
                {
                    capture.PublishReserved = false;
                    if (!IsActiveGameViewCaptureStatus(capture.Status))
                    {
                        capture.LateCompletionDiscarded = true;
                        PersistGameViewCaptureLocked(capture);
                        return;
                    }
                    capture.Bytes = result.Png.LongLength;
                    capture.Sha256 = sha256;
                    capture.Status = "completed";
                    capture.FidelityStatus = "review_required";
                    capture.ErrorCode = string.Empty;
                    capture.Message = string.Empty;
                    capture.UpdatedUtc = DateTime.UtcNow;
                    capture.ExpiresUtc = capture.UpdatedUtc.AddSeconds(GameViewCaptureExpirySeconds);
                    PersistGameViewCaptureLocked(capture);
                }
                return;
            }

            var rollbackSucceeded = GameViewCapturePolicy.RollbackPublication(publication, out var rollbackErrorCode, out var rollbackMessage);
            lock (s_gameViewCaptureLock)
            {
                capture.PublishReserved = false;
                if (!IsActiveGameViewCaptureStatus(capture.Status))
                {
                    capture.LateCompletionDiscarded = true;
                    PersistGameViewCaptureLocked(capture);
                    return;
                }
                if (!rollbackSucceeded)
                {
                    MarkGameViewCaptureTerminalLocked(capture, "corrupt", rollbackErrorCode, rollbackMessage, DateTime.UtcNow);
                }
                else if (decision.TerminalStatus == "timed_out")
                {
                    capture.LateCompletionDiscarded = true;
                    MarkGameViewCaptureTerminalLocked(capture, "timed_out", "capture_timeout", "Publication completed after the capture deadline and was rolled back", DateTime.UtcNow);
                }
                else if (!finalValidation.IsValid && !string.IsNullOrEmpty(finalValidation.ErrorCode))
                {
                    ApplyContextFailureLocked(capture, finalValidation, DateTime.UtcNow);
                }
                else
                {
                    MarkGameViewCaptureTerminalLocked(capture, "interrupted", decision.ErrorCode, "Publication ownership changed and the published output was rolled back.", DateTime.UtcNow);
                }
            }
        }

        static void EnsureGameViewScreenCapturePollHook()
        {
            if (s_gameViewScreenCapturePollHooked)
            {
                return;
            }

            s_gameViewScreenCapturePollHooked = true;
            EditorApplication.update += PollGameViewScreenCaptures;
        }

        static void PollGameViewScreenCaptures()
        {
            var now = DateTime.UtcNow;
            var pending = new List<GameViewCaptureState>();
            lock (s_gameViewCaptureLock)
            {
                foreach (var capture in s_gameViewCaptures.Values)
                {
                    if (IsActiveGameViewCaptureStatus(capture.Status))
                    {
                        capture.WatchdogPollCount++;
                        capture.LastWatchdogPollUtc = now;
                        pending.Add(capture);
                    }
                }
            }

            for (var index = 0; index < pending.Count; index++)
            {
                var capture = pending[index];
                try
                {
                    if (now >= capture.DeadlineUtc)
                    {
                        MarkGameViewCaptureTerminal(capture.CaptureId, "timed_out", "capture_timeout", "The requested screenshot capture reached its exact deadline");
                    }
                    else if (capture.Status == "resizing")
                    {
                        if (!ResolutionTransitionContextStillValid(capture, out var errorCode, out var message))
                        {
                            MarkGameViewCaptureTerminal(capture.CaptureId, "interrupted", errorCode, message);
                        }
                        else if (!TryStartGameViewCaptureAfterResolutionSettled(capture))
                        {
                            EditorApplication.QueuePlayerLoopUpdate();
                        }
                    }
                    else
                    {
                        var validation = ValidateCurrentCaptureContext(capture, 0, 0);
                        if (!validation.IsValid)
                        {
                            lock (s_gameViewCaptureLock)
                            {
                                if (IsActiveGameViewCaptureStatus(capture.Status)) ApplyContextFailureLocked(capture, validation, DateTime.UtcNow);
                            }
                        }
                        else
                        {
                            EditorApplication.QueuePlayerLoopUpdate();
                        }
                    }
                }
                catch (Exception)
                {
                    if (now >= capture.DeadlineUtc)
                    {
                        MarkGameViewCaptureTerminal(capture.CaptureId, "timed_out", "capture_timeout", "The requested screenshot capture reached its exact deadline");
                    }
                }
            }

            lock (s_gameViewCaptureLock)
            {
                foreach (var capture in s_gameViewCaptures.Values)
                {
                    if (IsActiveGameViewCaptureStatus(capture.Status))
                    {
                        return;
                    }
                }
            }

            EditorApplication.update -= PollGameViewScreenCaptures;
            s_gameViewScreenCapturePollHooked = false;
        }

        static bool ResolutionTransitionContextStillValid(GameViewCaptureState capture, out string errorCode, out string message)
        {
            errorCode = string.Empty;
            message = string.Empty;
            if (AuthoritativeCaptureCannotAdvance(out _))
            {
                errorCode = "capture_resolution_settle_interrupted";
                message = "Play Mode could not advance while the requested Game View resolution was settling";
                return false;
            }
            if (!PlayModeBarrierStillCurrent(capture))
            {
                errorCode = "capture_lifecycle_interrupted";
                message = "Play Mode lifecycle changed while the requested Game View resolution was settling";
                return false;
            }
            var activeScene = SceneManager.GetActiveScene();
            if (!activeScene.IsValid() || !GameViewCapturePolicy.SceneMatches(
                capture.ScheduledSceneHandle,
                capture.ScheduledScenePath,
                GameViewSceneIdentity.HandleIdentity(activeScene),
                activeScene.path))
            {
                errorCode = "capture_scene_changed";
                message = "The active scene changed while the requested Game View resolution was settling";
                return false;
            }
            if (!GameViewSceneIdentity.TryCaptureLoadedScenes(out var loadedSceneSet)
                || !GameViewSceneIdentity.Matches(new LoadedSceneSetIdentity(capture.ScheduledSceneSetCount, capture.ScheduledSceneSetFingerprint), loadedSceneSet))
            {
                errorCode = "capture_scene_set_changed";
                message = "The loaded scene set changed while the requested Game View resolution was settling";
                return false;
            }
            if (!GameViewCaptureEditorContext.TryInspect(out var evidence) || !evidence.IsGameView)
            {
                errorCode = "capture_game_view_required";
                message = "The selected main Play Mode view changed while the requested Game View resolution was settling";
                return false;
            }
            return true;
        }

        static bool IsActiveGameViewCaptureStatus(string status)
        {
            return status == "resizing" || status == "scheduled" || status == "settling" || status == "submitted";
        }

        static void MarkGameViewCaptureTerminal(string captureId, string status, string errorCode, string message)
        {
            lock (s_gameViewCaptureLock)
            {
                if (s_gameViewCaptures.TryGetValue(captureId, out var capture) && IsActiveGameViewCaptureStatus(capture.Status))
                {
                    MarkGameViewCaptureTerminalLocked(capture, status, errorCode, message, DateTime.UtcNow);
                }
            }
        }

        static void MarkGameViewCaptureTerminalLocked(GameViewCaptureState capture, string status, string errorCode, string message, DateTime now)
        {
            if (status != "completed" && capture.ReadbackBackend == "async_gpu_readback") capture.ReadbackAbandoned = true;
            if (GameViewCapturePolicy.TerminalRequiresBridgeDisposal(status)) GameViewCaptureBridge.Cancel(capture.CaptureId);
            var resolutionRestored = TryRestoreGameViewResolutionLocked(capture);
            capture.Status = status;
            capture.ErrorCode = errorCode ?? string.Empty;
            capture.Message = message ?? string.Empty;
            capture.UpdatedUtc = now;
            capture.ExpiresUtc = now.AddSeconds(GameViewCaptureExpirySeconds);
            capture.CleanupPerformed |= CleanupGameViewCaptureStaging(capture);
            PersistGameViewCaptureLocked(capture);
        }

        static bool TryRestoreGameViewResolutionLocked(GameViewCaptureState capture)
        {
            if (capture == null || !capture.ResolutionRestoreRequired) return true;
            if (capture.ResolutionRestoreVerified) return true;
            capture.ResolutionRestoreAttempted = true;
            capture.ResolutionRestoreSucceeded = false;
            capture.ResolutionRestoreScreenConverged = false;
            capture.ResolutionRestoreFailureReason = string.Empty;
            if (!GameViewCaptureEditorContext.TryInspect(out var selectedView) || !selectedView.IsGameView)
            {
                capture.ResolutionRestoreFailureReason = "restore_game_view_required";
                capture.FinalGameViewResized = true;
                return false;
            }
            if (!GameViewCaptureEditorContext.TrySetCustomRenderingResolution(capture.ResolutionOriginalWidth, capture.ResolutionOriginalHeight, out var failureReason))
            {
                capture.ResolutionRestoreFailureReason = failureReason;
                capture.FinalGameViewResized = true;
                return false;
            }

            capture.ResolutionRestoreSucceeded = true;
            var inspected = GameViewCaptureEditorContext.TryGetRenderingResolution(out var editorWidth, out var editorHeight);
            capture.CurrentEditorWidth = editorWidth;
            capture.CurrentEditorHeight = editorHeight;
            capture.CurrentScreenWidth = Screen.width;
            capture.CurrentScreenHeight = Screen.height;
            capture.ResolutionRestoreVerified = inspected
                && editorWidth == capture.ResolutionOriginalWidth
                && editorHeight == capture.ResolutionOriginalHeight;
            capture.ResolutionRestoreScreenConverged = inspected && GameViewCapturePolicy.ResolutionRestored(
                editorWidth, editorHeight, Screen.width, Screen.height, capture.ResolutionOriginalWidth, capture.ResolutionOriginalHeight);
            if (!capture.ResolutionRestoreVerified)
            {
                capture.ResolutionRestoreFailureReason = inspected ? "restored_resolution_not_observed" : "restored_game_view_unavailable";
                capture.FinalGameViewResized = true;
            }
            else
            {
                capture.ResolutionRestoreFailureReason = string.Empty;
                capture.FinalGameViewResized = false;
            }
            return capture.ResolutionRestoreVerified;
        }

        static void ApplyContextFailureLocked(GameViewCaptureState capture, CaptureContextValidation validation, DateTime now)
        {
            capture.DimensionsMatch = !validation.FinalGameViewResized;
            capture.FinalGameViewResized |= validation.FinalGameViewResized;
            MarkGameViewCaptureTerminalLocked(capture, "interrupted", validation.ErrorCode, validation.Message, now);
        }

        static CaptureContextValidation ValidateCurrentCaptureContext(GameViewCaptureState capture, int captureWidth, int captureHeight)
        {
            var cannotAdvance = AuthoritativeCaptureCannotAdvance(out _);
            var activeScene = SceneManager.GetActiveScene();
            var sceneSetAvailable = GameViewSceneIdentity.TryCaptureLoadedScenes(out var loadedSceneSet);
            var gameViewAvailable = GameViewCaptureEditorContext.TryInspect(out var gameViewEvidence);
            capture.PlayModeWindowApiSupported = gameViewEvidence.ApiSupported;
            capture.GameViewConfirmed = gameViewAvailable && gameViewEvidence.IsGameView;
            capture.SelectedPlayModeViewType = gameViewEvidence.ViewType;
            capture.PlayModeWindowFailureReason = gameViewEvidence.FailureReason;
            capture.CurrentEditorWidth = gameViewEvidence.Width;
            capture.CurrentEditorHeight = gameViewEvidence.Height;
            capture.CurrentScreenWidth = Screen.width;
            capture.CurrentScreenHeight = Screen.height;
            var resolutionAgreement = capture.GameViewConfirmed && GameViewCapturePolicy.ResolutionMatches(
                gameViewEvidence.Width,
                gameViewEvidence.Height,
                Screen.width,
                Screen.height,
                capture.RequestedWidth,
                capture.RequestedHeight);
            return GameViewCapturePolicy.ValidateContext(
                capture.GameViewConfirmed,
                resolutionAgreement,
                !cannotAdvance,
                PlayModeBarrierStillCurrent(capture),
                sceneSetAvailable && GameViewSceneIdentity.Matches(
                    new LoadedSceneSetIdentity(capture.ScheduledSceneSetCount, capture.ScheduledSceneSetFingerprint),
                    loadedSceneSet),
                capture.ScheduledSceneHandle,
                capture.ScheduledScenePath,
                activeScene.IsValid() ? GameViewSceneIdentity.HandleIdentity(activeScene) : 0UL,
                activeScene.IsValid() ? activeScene.path : string.Empty,
                capture.ScheduleWidth,
                capture.ScheduleHeight,
                capture.SubmissionWidth,
                capture.SubmissionHeight,
                gameViewEvidence.Width,
                gameViewEvidence.Height,
                captureWidth,
                captureHeight);
        }

        static CommandExecutionResult GameViewRequiredError(string captureState, GameViewEditorEvidence evidence)
        {
            return CommandExecutionResult.Error(
                "capture_game_view_required",
                "The selected main Play Mode view must be Game View for authoritative capture",
                "{\"capture_state\":" + JsonUtil.String(captureState)
                    + ",\"play_mode_window_api_supported\":" + JsonUtil.Bool(evidence.ApiSupported)
                    + ",\"selected_play_mode_view_type\":" + JsonUtil.String(evidence.ViewType)
                    + ",\"game_view_confirmed\":false,\"automatic_view_switch_attempted\":false,\"automatic_focus_attempted\":false"
                    + ",\"failure_reason\":" + JsonUtil.String(evidence.FailureReason)
                    + ",\"operator_guidance\":[\"Select the Game tab as the main Play Mode view without changing capture arguments.\",\"Use editor.windows.list followed by explicit game.view.focus if the Game view is open but not focused.\"]"
                    + ",\"next_valid_actions\":[\"editor.windows.list\",\"game.view.focus\",\"game.view.screenshot\"]"
                    + ",\"fallback_attempted\":false}");
        }

        static CommandExecutionResult GameViewFocusError(string errorCode, string message, EditorWindow window, int selectedIndex, string requestedWindowsHash, string currentWindowsHash, bool focusAttempted)
        {
            return CommandExecutionResult.Error(errorCode, message,
                "{\"focus_attempted\":" + JsonUtil.Bool(focusAttempted)
                    + ",\"focus_verified\":false"
                    + ",\"native_focus_api_used\":false"
                    + ",\"automatic_focus_attempted\":false"
                    + ",\"requested_windows_hash\":" + JsonUtil.String(requestedWindowsHash)
                    + ",\"windows_hash\":" + JsonUtil.String(currentWindowsHash)
                    + ",\"selected_window\":" + EditorWindowSelector.MetadataJson(window, selectedIndex)
                    + ",\"next_valid_actions\":[\"editor.windows.list\",\"game.view.focus\"]}");
        }

        static CommandExecutionResult GameViewFocusResult(
            string status,
            EditorWindow window,
            int selectedIndex,
            string requestedWindowsHash,
            string currentWindowsHash,
            bool applicationFocusedBefore,
            bool applicationFocusedAfter,
            bool gameViewFocusedBefore,
            bool gameViewFocusedAfter,
            bool focusAttempted,
            bool focusVerified,
            bool dryRun)
        {
            return CommandExecutionResult.Ok("{\"status\":" + JsonUtil.String(status)
                + ",\"success\":" + JsonUtil.Bool(focusVerified && !dryRun)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"focus_attempted\":" + JsonUtil.Bool(focusAttempted)
                + ",\"focus_verified\":" + JsonUtil.Bool(focusVerified)
                + ",\"operator_visible_effect\":" + JsonUtil.Bool(focusAttempted)
                + ",\"native_focus_api_used\":false"
                + ",\"automatic_focus_attempted\":false"
                + ",\"application_focused_before\":" + JsonUtil.Bool(applicationFocusedBefore)
                + ",\"application_focused_after\":" + JsonUtil.Bool(applicationFocusedAfter)
                + ",\"game_view_focused_before\":" + JsonUtil.Bool(gameViewFocusedBefore)
                + ",\"game_view_focused_after\":" + JsonUtil.Bool(gameViewFocusedAfter)
                + ",\"requested_windows_hash\":" + JsonUtil.String(requestedWindowsHash)
                + ",\"windows_hash\":" + JsonUtil.String(currentWindowsHash)
                + ",\"selected_window\":" + EditorWindowSelector.MetadataJson(window, selectedIndex)
                + ",\"next_valid_actions\":" + (focusVerified ? "[\"game.view.screenshot\"]" : "[\"editor.windows.list\",\"game.view.focus\"]")
                + "}");
        }

        static string GameViewCaptureJson(GameViewCaptureState capture)
        {
            var builder = new StringBuilder();
            builder.Append("{\"capture_id\":").Append(JsonUtil.String(capture.CaptureId));
            builder.Append(",\"status\":").Append(JsonUtil.String(capture.Status));
            builder.Append(",\"capture_state\":").Append(JsonUtil.String(capture.DryRun ? "planned" : capture.Status));
            if (!string.IsNullOrEmpty(capture.ErrorCode))
            {
                builder.Append(",\"error_code\":").Append(JsonUtil.String(capture.ErrorCode));
            }

            if (!string.IsNullOrEmpty(capture.Message))
            {
                builder.Append(",\"message\":").Append(JsonUtil.String(capture.Message));
            }

            builder.Append(",\"requested_backend\":").Append(JsonUtil.String(capture.RequestedBackend));
            builder.Append(",\"effective_backend\":").Append(JsonUtil.String(capture.EffectiveBackend));
            builder.Append(",\"backend\":").Append(JsonUtil.String(capture.EffectiveBackend));
            builder.Append(",\"backend_alias_resolved\":").Append(JsonUtil.Bool(capture.BackendAliasResolved));
            builder.Append(",\"authoritative\":").Append(JsonUtil.Bool(capture.Authoritative));
            builder.Append(",\"composition\":").Append(JsonUtil.String(capture.Composition));
            builder.Append(",\"fallback_attempted\":false");
            builder.Append(",\"include_overlay_ui\":").Append(JsonUtil.Bool(capture.IncludeOverlayUi));
            builder.Append(",\"requested_include_overlay_ui\":").Append(capture.IncludeOverlayUiProvided ? JsonUtil.Bool(capture.IncludeOverlayUi) : "null");
            builder.Append(",\"include_overlay_ui_explicit\":").Append(JsonUtil.Bool(capture.IncludeOverlayUiProvided));
            var artifactIntegrity = capture.Status == "completed"
                ? GameViewCapturePolicy.CompletedArtifactIntegrity(ProjectPaths.ProjectRoot, capture.FullPath, capture.Bytes, capture.Sha256)
                : "not_applicable";
            var artifactAvailable = artifactIntegrity == "verified";
            builder.Append(",\"artifact_path\":").Append(artifactAvailable ? JsonUtil.String(capture.RelativePath) : "null");
            builder.Append(",\"project_relative_path\":").Append(artifactAvailable ? JsonUtil.String(capture.RelativePath) : "null");
            builder.Append(",\"artifact_ref\":").Append(artifactAvailable ? JsonUtil.String(capture.RelativePath) : "null");
            builder.Append(",\"planned_artifact_path\":").Append(string.IsNullOrEmpty(capture.RelativePath) ? "null" : JsonUtil.String(capture.RelativePath));
            builder.Append(",\"dimensions\":{\"width\":").Append(capture.Width).Append(",\"height\":").Append(capture.Height).Append('}');
            builder.Append(",\"schedule_dimensions\":{\"width\":").Append(capture.ScheduleWidth).Append(",\"height\":").Append(capture.ScheduleHeight).Append('}');
            builder.Append(",\"schedule_screen_dimensions\":{\"width\":").Append(capture.ScheduleScreenWidth).Append(",\"height\":").Append(capture.ScheduleScreenHeight).Append('}');
            builder.Append(",\"submission_dimensions\":").Append(capture.SubmissionWidth > 0
                ? "{\"width\":" + capture.SubmissionWidth + ",\"height\":" + capture.SubmissionHeight + "}"
                : "null");
            builder.Append(",\"submission_screen_dimensions\":").Append(capture.SubmissionScreenWidth > 0
                ? "{\"width\":" + capture.SubmissionScreenWidth + ",\"height\":" + capture.SubmissionScreenHeight + "}"
                : "null");
            builder.Append(",\"requested_dimensions\":").Append(capture.RequestedWidth > 0
                ? "{\"width\":" + capture.RequestedWidth + ",\"height\":" + capture.RequestedHeight + "}"
                : "null");
            builder.Append(",\"dimensions_source\":").Append(JsonUtil.String(capture.DimensionsSource));
            builder.Append(",\"dimensions_match\":").Append(JsonUtil.Bool(capture.DimensionsMatch));
            builder.Append(",\"final_game_view_resized\":").Append(JsonUtil.Bool(capture.FinalGameViewResized));
            builder.Append(",\"resolution_change\":{\"supported\":").Append(JsonUtil.Bool(capture.ResolutionChangeSupported));
            builder.Append(",\"required\":").Append(JsonUtil.Bool(capture.ResolutionChangeRequired));
            builder.Append(",\"original\":{\"width\":").Append(capture.ResolutionOriginalWidth).Append(",\"height\":").Append(capture.ResolutionOriginalHeight).Append('}');
            builder.Append(",\"target\":{\"width\":").Append(capture.ResolutionTargetWidth).Append(",\"height\":").Append(capture.ResolutionTargetHeight).Append('}');
            builder.Append(",\"apply_attempted\":").Append(JsonUtil.Bool(capture.ResolutionApplyAttempted));
            builder.Append(",\"apply_succeeded\":").Append(JsonUtil.Bool(capture.ResolutionApplySucceeded));
            builder.Append(",\"apply_verified\":").Append(JsonUtil.Bool(capture.ResolutionApplyVerified));
            builder.Append(",\"apply_failure_reason\":").Append(string.IsNullOrEmpty(capture.ResolutionApplyFailureReason) ? "null" : JsonUtil.String(capture.ResolutionApplyFailureReason));
            builder.Append(",\"restore_required\":").Append(JsonUtil.Bool(capture.ResolutionRestoreRequired));
            builder.Append(",\"restore_attempted\":").Append(JsonUtil.Bool(capture.ResolutionRestoreAttempted));
            builder.Append(",\"restore_succeeded\":").Append(JsonUtil.Bool(capture.ResolutionRestoreSucceeded));
            builder.Append(",\"restore_verified\":").Append(JsonUtil.Bool(capture.ResolutionRestoreVerified));
            builder.Append(",\"restore_screen_converged\":").Append(JsonUtil.Bool(capture.ResolutionRestoreScreenConverged));
            builder.Append(",\"restore_failure_reason\":").Append(string.IsNullOrEmpty(capture.ResolutionRestoreFailureReason) ? "null" : JsonUtil.String(capture.ResolutionRestoreFailureReason)).Append('}');
            builder.Append(",\"editor_game_view_resolution\":{\"width\":").Append(capture.CurrentEditorWidth).Append(",\"height\":").Append(capture.CurrentEditorHeight).Append('}');
            builder.Append(",\"screen_resolution\":{\"width\":").Append(capture.CurrentScreenWidth).Append(",\"height\":").Append(capture.CurrentScreenHeight).Append('}');
            builder.Append(",\"play_mode_window_api_supported\":").Append(JsonUtil.Bool(capture.PlayModeWindowApiSupported));
            builder.Append(",\"selected_play_mode_view_type\":").Append(JsonUtil.String(capture.SelectedPlayModeViewType));
            builder.Append(",\"game_view_confirmed\":").Append(JsonUtil.Bool(capture.GameViewConfirmed));
            builder.Append(",\"automatic_view_switch_attempted\":false,\"automatic_focus_attempted\":").Append(JsonUtil.Bool(capture.AutomaticFocusAttempted));
            builder.Append(",\"focus_verified\":").Append(JsonUtil.Bool(capture.FocusVerified));
            builder.Append(",\"focus_capture_plan_id\":").Append(string.IsNullOrEmpty(capture.FocusCapturePlanId) ? "null" : JsonUtil.String(capture.FocusCapturePlanId));
            builder.Append(",\"focus_capture_plan_hash\":").Append(string.IsNullOrEmpty(capture.FocusCapturePlanHash) ? "null" : JsonUtil.String(capture.FocusCapturePlanHash));
            builder.Append(",\"focus_window_runtime_identity\":").Append(string.IsNullOrEmpty(capture.FocusWindowRuntimeIdentity) ? "null" : JsonUtil.String(capture.FocusWindowRuntimeIdentity));
            builder.Append(",\"focus_windows_hash_before\":").Append(string.IsNullOrEmpty(capture.FocusWindowsHashBefore) ? "null" : JsonUtil.String(capture.FocusWindowsHashBefore));
            builder.Append(",\"focus_windows_hash_after\":").Append(string.IsNullOrEmpty(capture.FocusWindowsHashAfter) ? "null" : JsonUtil.String(capture.FocusWindowsHashAfter));
            builder.Append(",\"format\":\"png\"");
            builder.Append(",\"created_utc\":").Append(JsonUtil.String(capture.CreatedUtc.ToString("o")));
            builder.Append(",\"updated_utc\":").Append(JsonUtil.String(capture.UpdatedUtc.ToString("o")));
            builder.Append(",\"expires_utc\":").Append(JsonUtil.String(capture.ExpiresUtc.ToString("o")));
            builder.Append(",\"deadline_utc\":").Append(JsonUtil.String(capture.DeadlineUtc.ToString("o")));
            builder.Append(",\"timeout_ms\":").Append(capture.TimeoutMs);
            builder.Append(",\"age_ms\":").Append(Math.Max(0, (long)(DateTime.UtcNow - capture.CreatedUtc).TotalMilliseconds));
            builder.Append(",\"application_focused\":").Append(JsonUtil.Bool(Application.isFocused));
            builder.Append(",\"editor_paused\":").Append(JsonUtil.Bool(EditorApplication.isPaused));
            builder.Append(",\"is_playing\":").Append(JsonUtil.Bool(EditorApplication.isPlaying));
            builder.Append(",\"scheduled_application_focused\":").Append(JsonUtil.Bool(capture.ApplicationFocusedAtSchedule));
            builder.Append(",\"scheduled_editor_paused\":").Append(JsonUtil.Bool(capture.EditorPausedAtSchedule));
            builder.Append(",\"settle_frames_requested\":").Append(capture.SettleFramesRequested);
            builder.Append(",\"settle_frames_observed\":").Append(capture.SettleFramesObserved);
            builder.Append(",\"schedule_frame\":").Append(capture.ScheduleFrame);
            builder.Append(",\"last_settle_frame\":").Append(capture.LastSettleFrame);
            builder.Append(",\"last_rendered_frame\":").Append(capture.LastRenderedFrame);
            builder.Append(",\"submission_frame\":").Append(capture.SubmissionFrame);
            builder.Append(",\"completion_frame\":").Append(capture.CompletionFrame);
            builder.Append(",\"submission_utc\":").Append(capture.SubmissionUtc == default ? "null" : JsonUtil.String(capture.SubmissionUtc.ToString("o")));
            builder.Append(",\"completion_utc\":").Append(capture.CompletionUtc == default ? "null" : JsonUtil.String(capture.CompletionUtc.ToString("o")));
            builder.Append(",\"bridge_instance_id\":").Append(JsonUtil.String(capture.BridgeInstanceId));
            builder.Append(",\"play_mode_generation\":").Append(capture.PlayModeGeneration);
            builder.Append(",\"play_mode_barrier_id\":").Append(string.IsNullOrEmpty(capture.PlayModeBarrierId) ? "null" : JsonUtil.String(capture.PlayModeBarrierId));
            builder.Append(",\"play_mode_entered_utc\":").Append(capture.PlayModeEnteredUtc == default ? "null" : JsonUtil.String(capture.PlayModeEnteredUtc.ToString("o")));
            builder.Append(",\"scheduled_scene\":{\"handle\":").Append(capture.ScheduledSceneHandle);
            builder.Append(",\"path\":").Append(JsonUtil.String(capture.ScheduledScenePath));
            builder.Append(",\"name\":").Append(JsonUtil.String(capture.ScheduledSceneName)).Append('}');
            builder.Append(",\"loaded_scene_set\":{\"count\":").Append(capture.ScheduledSceneSetCount);
            builder.Append(",\"fingerprint\":").Append(JsonUtil.String(capture.ScheduledSceneSetFingerprint));
            builder.Append(",\"max_scenes\":").Append(GameViewSceneIdentity.MaxLoadedScenes).Append('}');
            builder.Append(",\"watchdog_poll_count\":").Append(Math.Max(0, capture.WatchdogPollCount));
            builder.Append(",\"last_watchdog_poll_utc\":").Append(capture.WatchdogPollCount > 0 ? JsonUtil.String(capture.LastWatchdogPollUtc.ToString("o")) : "null");
            builder.Append(",\"completion_deadline_checked\":").Append(JsonUtil.Bool(capture.CompletionDeadlineChecked));
            builder.Append(",\"can_cancel\":").Append(JsonUtil.Bool(!capture.DryRun && (IsActiveGameViewCaptureStatus(capture.Status) || (capture.ResolutionRestoreRequired && !capture.ResolutionRestoreVerified))));
            builder.Append(",\"cancel_requested\":").Append(JsonUtil.Bool(capture.CancelRequested));
            builder.Append(",\"cleanup_performed\":").Append(JsonUtil.Bool(capture.CleanupPerformed));
            builder.Append(",\"late_completion_discarded\":").Append(JsonUtil.Bool(capture.LateCompletionDiscarded));
            builder.Append(",\"readback_backend\":").Append(JsonUtil.String(capture.ReadbackBackend));
            builder.Append(",\"readback_completion_accepted\":").Append(JsonUtil.Bool(capture.ReadbackCompletionAccepted));
            builder.Append(",\"readback_abandoned\":").Append(JsonUtil.Bool(capture.ReadbackAbandoned));
            builder.Append(",\"artifact_integrity\":{\"status\":").Append(JsonUtil.String(artifactIntegrity));
            builder.Append(",\"verified\":").Append(JsonUtil.Bool(artifactAvailable)).Append('}');
            builder.Append(",\"sha256\":").Append(string.IsNullOrEmpty(capture.Sha256) ? "null" : JsonUtil.String(capture.Sha256));
            builder.Append(",\"bytes\":").Append(capture.Bytes);
            builder.Append(",\"sample_metrics\":{\"sample_count\":").Append(capture.SampleCount);
            builder.Append(",\"grid_max\":\"64x64\"");
            builder.Append(",\"zero_count\":").Append(capture.ZeroSampleCount);
            builder.Append(",\"zero_ratio\":").Append(RatioJson(capture.ZeroSampleCount, capture.SampleCount));
            builder.Append(",\"near_black_count\":").Append(capture.NearBlackSampleCount);
            builder.Append(",\"near_black_ratio\":").Append(RatioJson(capture.NearBlackSampleCount, capture.SampleCount));
            builder.Append(",\"transparent_count\":").Append(capture.TransparentSampleCount);
            builder.Append(",\"transparent_ratio\":").Append(RatioJson(capture.TransparentSampleCount, capture.SampleCount));
            builder.Append(",\"alpha_zero_count\":").Append(capture.TransparentSampleCount);
            builder.Append(",\"alpha_zero_ratio\":").Append(RatioJson(capture.TransparentSampleCount, capture.SampleCount));
            builder.Append(",\"uniform_count\":").Append(capture.UniformSampleCount);
            builder.Append(",\"uniform_ratio\":").Append(RatioJson(capture.UniformSampleCount, capture.SampleCount));
            builder.Append(",\"unique_colors\":").Append(capture.UniqueSampleCount);
            builder.Append(",\"sentinel_count\":").Append(capture.SentinelSampleCount).Append('}');
            builder.Append(",\"fidelity_status\":").Append(JsonUtil.String(capture.FidelityStatus));
            builder.Append(",\"live_pixel_agreement_claimed\":false");
            builder.Append(",\"visual_review_required\":true");
            if (capture.ErrorCode == "capture_game_view_required")
            {
                builder.Append(",\"failure_reason\":").Append(JsonUtil.String(capture.PlayModeWindowFailureReason));
                builder.Append(",\"operator_guidance\":[\"Select the Game tab as the main Play Mode view.\",\"Use editor.windows.list followed by explicit game.view.focus if needed.\"]");
            }
            builder.Append(",\"dry_run\":").Append(JsonUtil.Bool(capture.DryRun));
            builder.Append(",\"privacy_warning\":").Append(JsonUtil.String(ScreenshotPrivacyWarning));
            builder.Append(",\"next_actions\":");
            if (IsActiveGameViewCaptureStatus(capture.Status) && !capture.DryRun)
            {
                builder.Append("[\"game.view.screenshot.status\",\"game.view.screenshot.cancel\"]");
            }
            else if (capture.ResolutionRestoreRequired && !capture.ResolutionRestoreVerified)
            {
                builder.Append("[\"editor.windows.list\",\"game.view.focus\",\"game.view.screenshot.cancel\"]");
            }
            else if (capture.Status == "completed")
            {
                builder.Append("[\"job.artifact.read\",\"artifact.metadata\"]");
            }
            else
            {
                builder.Append("[\"game.view.screenshot\"]");
            }
            builder.Append('}');
            return builder.ToString();
        }

        static bool AuthoritativeCaptureCannotAdvance(out string detailsJson)
        {
            var focused = Application.isFocused;
            var paused = EditorApplication.isPaused;
            var playing = EditorApplication.isPlaying;
            var batchMode = Application.isBatchMode;
            var compiling = EditorApplication.isCompiling;
            var updating = EditorApplication.isUpdating;
            var blocked = !playing || !focused || paused || batchMode || compiling || updating;
            detailsJson = "{\"application_focused\":" + JsonUtil.Bool(focused)
                + ",\"editor_paused\":" + JsonUtil.Bool(paused)
                + ",\"is_playing\":" + JsonUtil.Bool(playing)
                + ",\"batch_mode\":" + JsonUtil.Bool(batchMode)
                + ",\"is_compiling\":" + JsonUtil.Bool(compiling)
                + ",\"is_updating\":" + JsonUtil.Bool(updating)
                + ",\"retryable\":true"
                + ",\"automatic_focus_attempted\":false"
                + ",\"fallback_attempted\":false"
                + ",\"next_valid_actions\":[\"editor.windows.list\",\"game.view.focus\",\"game.view.screenshot\",\"runtime.capture_state\"]}";
            return blocked;
        }

        static void ExpireGameViewCaptureLocked(GameViewCaptureState capture, DateTime now)
        {
            if (IsActiveGameViewCaptureStatus(capture.Status) && now >= capture.DeadlineUtc)
            {
                MarkGameViewCaptureTerminalLocked(capture, "timed_out", "capture_timeout", "Game View capture reached its exact deadline", now);
            }
        }

        static bool CleanupGameViewCaptureStaging(GameViewCaptureState capture)
        {
            try
            {
                if (capture == null
                    || !GameViewCapturePolicy.IsExpectedStagingPath(capture.FullPath, capture.StagingPath, capture.CaptureId)
                    || !GameViewCapturePolicy.IsConfinedOutputPath(ProjectPaths.ProjectRoot, capture.FullPath)
                    || !GameViewCapturePolicy.IsConfinedOutputPath(ProjectPaths.ProjectRoot, capture.StagingPath)
                    || !File.Exists(capture.StagingPath))
                {
                    return false;
                }
                File.Delete(capture.StagingPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string Sha256Hex(byte[] bytes)
        {
            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(bytes);
                var builder = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++) builder.Append(digest[index].ToString("x2"));
                return builder.ToString();
            }
        }

        static bool HasStrongFidelityFailure(GameViewCaptureState capture)
        {
            return GameViewCapturePolicy.HasStrongFidelityFailure(
                capture.SampleCount,
                capture.ZeroSampleCount,
                capture.TransparentSampleCount,
                capture.UniformSampleCount,
                capture.UniqueSampleCount,
                capture.SentinelSampleCount);
        }

        static string RatioJson(int count, int total)
        {
            return total <= 0 ? "0" : FloatJson((float)count / total);
        }

        static DateTime TicksToUtc(long ticks, DateTime fallback)
        {
            return ticks > 0 && ticks <= DateTime.MaxValue.Ticks ? new DateTime(ticks, DateTimeKind.Utc) : fallback;
        }

        static bool PlayModeBarrierStillCurrent(GameViewCaptureState capture)
        {
            return PlayModeTransitionTracker.TryGetCurrentPlayModeBarrier(out var evidence)
                && evidence.Generation == capture.PlayModeGeneration
                && string.Equals(evidence.BarrierId, capture.PlayModeBarrierId, StringComparison.Ordinal);
        }

        static void PersistGameViewCaptureLocked(GameViewCaptureState capture)
        {
            if (capture == null) return;
            capture.CreatedTicks = capture.CreatedUtc.Ticks;
            capture.UpdatedTicks = capture.UpdatedUtc.Ticks;
            capture.ExpiresTicks = capture.ExpiresUtc.Ticks;
            capture.DeadlineTicks = capture.DeadlineUtc.Ticks;
            capture.SubmissionTicks = capture.SubmissionUtc == default ? 0 : capture.SubmissionUtc.Ticks;
            capture.CompletionTicks = capture.CompletionUtc == default ? 0 : capture.CompletionUtc.Ticks;
            capture.PlayModeEnteredTicks = capture.PlayModeEnteredUtc == default ? 0 : capture.PlayModeEnteredUtc.Ticks;
            var json = JsonUtility.ToJson(capture);
            if (json.Length <= 16384) SessionState.SetString(GameViewCaptureSessionStateKey, json);
        }

        static void RecoverPersistedGameViewCapture()
        {
            var json = SessionState.GetString(GameViewCaptureSessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(json) || json.Length > 16384) return;
            try
            {
                var capture = JsonUtility.FromJson<GameViewCaptureState>(json);
                if (capture == null || !GameViewCapturePolicy.IsValidCaptureId(capture.CaptureId))
                {
                    SessionState.SetString(GameViewCaptureSessionStateKey, string.Empty);
                    return;
                }
                var now = DateTime.UtcNow;
                capture.CreatedUtc = TicksToUtc(capture.CreatedTicks, now);
                capture.UpdatedUtc = TicksToUtc(capture.UpdatedTicks, capture.CreatedUtc);
                capture.ExpiresUtc = TicksToUtc(capture.ExpiresTicks, capture.UpdatedUtc.AddSeconds(GameViewCaptureExpirySeconds));
                capture.DeadlineUtc = TicksToUtc(capture.DeadlineTicks, capture.CreatedUtc);
                capture.SubmissionUtc = capture.SubmissionTicks == 0 ? default : TicksToUtc(capture.SubmissionTicks, capture.UpdatedUtc);
                capture.CompletionUtc = capture.CompletionTicks == 0 ? default : TicksToUtc(capture.CompletionTicks, capture.UpdatedUtc);
                capture.PlayModeEnteredUtc = capture.PlayModeEnteredTicks == 0 ? default : TicksToUtc(capture.PlayModeEnteredTicks, capture.CreatedUtc);
                if (TryNormalizeScreenshotOutputPath(capture.RelativePath, out var relativePath) != null)
                {
                    capture.RelativePath = string.Empty;
                    capture.FullPath = string.Empty;
                    capture.StagingPath = string.Empty;
                    RestoreOrRetainDiscardedPersistedCaptureLocked(capture, false, now);
                    return;
                }
                capture.RelativePath = relativePath;
                capture.FullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
                capture.StagingPath = GameViewCapturePolicy.BuildStagingPath(capture.FullPath, capture.CaptureId);
                if (capture.ExpiresUtc <= now)
                {
                    RestoreOrRetainDiscardedPersistedCaptureLocked(capture, true, now);
                    return;
                }
                if (!IsActiveGameViewCaptureStatus(capture.Status) && !GameViewCapturePolicy.TerminalRequiresBridgeDisposal(capture.Status) && capture.Status != "dry_run")
                {
                    RestoreOrRetainDiscardedPersistedCaptureLocked(capture, true, now);
                    return;
                }
                s_gameViewCaptures[capture.CaptureId] = capture;
                if (IsActiveGameViewCaptureStatus(capture.Status))
                {
                    MarkGameViewCaptureTerminalLocked(capture, "interrupted", "capture_domain_reload_interrupted", "The authoritative capture was interrupted by assembly/domain reload and was not replayed", DateTime.UtcNow);
                }
                else if (capture.ResolutionRestoreRequired && !capture.ResolutionRestoreVerified)
                {
                    TryRestoreGameViewResolutionLocked(capture);
                    PersistGameViewCaptureLocked(capture);
                }
            }
            catch (Exception)
            {
                SessionState.SetString(GameViewCaptureSessionStateKey, string.Empty);
            }
        }

        static void RestoreOrRetainDiscardedPersistedCaptureLocked(GameViewCaptureState capture, bool cleanupStaging, DateTime now)
        {
            if (cleanupStaging) capture.CleanupPerformed |= CleanupGameViewCaptureStaging(capture);
            if (TryRestoreGameViewResolutionLocked(capture))
            {
                SessionState.SetString(GameViewCaptureSessionStateKey, string.Empty);
                return;
            }
            capture.Status = "interrupted";
            capture.ErrorCode = "capture_resolution_restore_pending";
            capture.Message = "The persisted capture was discarded without replay, but exact Game View resolution restoration still requires the Game view to be selected.";
            capture.UpdatedUtc = now;
            capture.ExpiresUtc = now.AddSeconds(GameViewCaptureExpirySeconds);
            s_gameViewCaptures[capture.CaptureId] = capture;
            PersistGameViewCaptureLocked(capture);
        }

        static void InterruptGameViewCaptureForReload()
        {
            lock (s_gameViewCaptureLock)
            {
                foreach (var capture in s_gameViewCaptures.Values)
                {
                    if (!IsActiveGameViewCaptureStatus(capture.Status)) continue;
                    MarkGameViewCaptureTerminalLocked(capture, "interrupted", "capture_lifecycle_interrupted", "The authoritative capture was interrupted by Editor lifecycle shutdown", DateTime.UtcNow);
                }
            }
        }

        static bool IsValidEncodedPng(byte[] bytes, int expectedWidth, int expectedHeight)
        {
            if (bytes == null || bytes.Length < 45 || bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 || bytes[4] != 13 || bytes[5] != 10 || bytes[6] != 26 || bytes[7] != 10)
            {
                return false;
            }

            var end = bytes.Length - 12;
            return ReadPngInt32(bytes, 8) == 13
                && bytes[12] == 73 && bytes[13] == 72 && bytes[14] == 68 && bytes[15] == 82
                && ReadPngInt32(bytes, 16) == expectedWidth && ReadPngInt32(bytes, 20) == expectedHeight
                && ReadPngInt32(bytes, end) == 0
                && bytes[end + 4] == 73 && bytes[end + 5] == 69 && bytes[end + 6] == 78 && bytes[end + 7] == 68
                && bytes[end + 8] == 174 && bytes[end + 9] == 66 && bytes[end + 10] == 96 && bytes[end + 11] == 130;
        }

        static int ReadPngInt32(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }

        static bool TryEncodeEditorWindowCapture(EditorWindow window, int width, int height, out byte[] png, out string message)
        {
            png = null;
            message = string.Empty;
            RenderTexture renderTexture = null;
            Texture2D texture = null;
            var previousActive = RenderTexture.active;
            try
            {
                renderTexture = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
                if (renderTexture == null)
                {
                    message = "Unity could not allocate the bounded EditorWindow capture framebuffer";
                    return false;
                }

                texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
                if (!EditorWindowCaptureBackend.TryCapture(window, renderTexture, out message))
                {
                    return false;
                }

                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                png = texture.EncodeToPNG();
                if (png == null || png.Length == 0)
                {
                    message = "Unity could not encode the EditorWindow framebuffer as PNG";
                    png = null;
                    return false;
                }

                return true;
            }
            catch (Exception)
            {
                png = null;
                message = "Unity internal EditorWindow framebuffer capture failed";
                return false;
            }
            finally
            {
                RenderTexture.active = previousActive;
                if (renderTexture != null)
                {
                    RenderTexture.ReleaseTemporary(renderTexture);
                }

                if (texture != null)
                {
                    UnityEngine.Object.DestroyImmediate(texture);
                }
            }
        }

        static string EditorWindowScreenshotPath(EditorWindow window, int selectedIndex, string windowsHash)
        {
            var typeName = window == null ? "window" : (window.GetType().Name ?? "window");
            var stableHash = string.IsNullOrEmpty(windowsHash) ? "unknown" : windowsHash.Replace(':', '-');
            return ScreenshotRoot + "/editor-window-" + selectedIndex + "-" + SanitizeFilePart(typeName) + "-" + SanitizeFilePart(stableHash) + ".png";
        }

        static string EditorWindowStagingPath(string fullPath)
        {
            return fullPath + ".editor-window-" + Guid.NewGuid().ToString("N") + ".staging";
        }

        static bool CleanupEditorWindowStaging(string stagingPath)
        {
            try
            {
                if (string.IsNullOrEmpty(stagingPath)
                    || !GameViewCapturePolicy.IsConfinedOutputPath(ProjectPaths.ProjectRoot, stagingPath)
                    || !File.Exists(stagingPath))
                {
                    return string.IsNullOrEmpty(stagingPath) || !File.Exists(stagingPath);
                }

                File.Delete(stagingPath);
                return !File.Exists(stagingPath);
            }
            catch (Exception)
            {
                return false;
            }
        }

        static CommandExecutionResult IncompatibleGameViewCaptureOptions(string backend, bool includeOverlayUi)
        {
            return CommandExecutionResult.Error("schema_validation_failed", "include_overlay_ui must match the explicitly selected Game view screenshot backend",
                "{\"invalid_path\":\"args.include_overlay_ui\""
                + ",\"backend\":" + JsonUtil.String(backend)
                + ",\"requested_include_overlay_ui\":" + JsonUtil.Bool(includeOverlayUi)
                + ",\"capture_state\":\"not_scheduled\""
                + ",\"accepted_combinations\":[{\"backend\":\"camera_render\",\"include_overlay_ui\":false},{\"backend\":\"end_of_frame\",\"include_overlay_ui\":true},{\"backend\":\"screen_capture\",\"include_overlay_ui\":true}]"
                + ",\"automatic_capture_attempted\":false,\"fallback_attempted\":false}");
        }

        static string NewGameViewCaptureId()
        {
            return "gameview-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        static void PruneGameViewCapturesLocked(DateTime now)
        {
            var expired = new List<string>();
            foreach (var pair in s_gameViewCaptures)
            {
                if (pair.Value.ExpiresUtc <= now
                    && !IsActiveGameViewCaptureStatus(pair.Value.Status)
                    && (!pair.Value.ResolutionRestoreRequired || pair.Value.ResolutionRestoreVerified))
                {
                    expired.Add(pair.Key);
                }
            }

            for (var index = 0; index < expired.Count; index++)
            {
                s_gameViewCaptures.Remove(expired[index]);
            }

            while (s_gameViewCaptures.Count > MaxGameViewCaptureHistory)
            {
                if (!TryRemoveOldestTerminalCaptureLocked())
                {
                    break;
                }
            }
        }

        static bool TryRemoveOldestTerminalCaptureLocked()
        {
            string oldestKey = null;
            var oldest = DateTime.MaxValue;
            foreach (var pair in s_gameViewCaptures)
            {
                if (!IsActiveGameViewCaptureStatus(pair.Value.Status)
                    && (!pair.Value.ResolutionRestoreRequired || pair.Value.ResolutionRestoreVerified)
                    && pair.Value.CreatedUtc < oldest)
                {
                    oldest = pair.Value.CreatedUtc;
                    oldestKey = pair.Key;
                }
            }

            return !string.IsNullOrEmpty(oldestKey) && s_gameViewCaptures.Remove(oldestKey);
        }

        static CommandExecutionResult ResolveScreenshotOutputPath(string argsJson, string defaultRelativePath, out string relativePath, out string fullPath)
        {
            relativePath = string.Empty;
            fullPath = string.Empty;
            var requestedPath = JsonUtil.ExtractString(argsJson, "output_path");
            if (string.IsNullOrEmpty(requestedPath))
            {
                relativePath = defaultRelativePath;
            }
            else
            {
                var normalizeResult = TryNormalizeScreenshotOutputPath(requestedPath, out relativePath);
                if (normalizeResult != null)
                {
                    return normalizeResult;
                }
            }

            fullPath = Path.Combine(ProjectPaths.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var candidate = Path.GetFullPath(fullPath);
            if (!IsPathInside(projectRoot, candidate))
            {
                return CommandExecutionResult.Error("path_traversal_rejected", "output_path must stay inside the Unity project");
            }
            if (!GameViewCapturePolicy.IsConfinedOutputPath(projectRoot, candidate))
            {
                return CommandExecutionResult.Error(GameViewCapturePolicy.UnsafePathCode, "output_path must not traverse a symbolic link or reparse point");
            }

            fullPath = candidate;
            return null;
        }

        static CommandExecutionResult TryNormalizeScreenshotOutputPath(string input, out string relativePath)
        {
            relativePath = string.Empty;
            try
            {
                PathGuard.AssertNoNul(input);
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error(exception.Message, "output_path contains an unsafe character");
            }

            var normalized = input.Replace('\\', '/').Trim();
            if (normalized.Length == 0)
            {
                return CommandExecutionResult.Error("invalid_field_value", "output_path must not be empty");
            }

            if (normalized.Length > MaxScreenshotOutputPathLength)
            {
                return CommandExecutionResult.Error("filename_too_long", "output_path must be at most 240 characters");
            }

            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal) || IsDriveAbsolute(normalized))
            {
                return CommandExecutionResult.Error("absolute_path_rejected", "output_path must be project-relative");
            }

            if (PathGuard.HasTraversal(normalized))
            {
                return CommandExecutionResult.Error("path_traversal_rejected", "output_path must not contain traversal");
            }

            if (!normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "output_path must end with .png");
            }

            if (!IsApprovedScreenshotOutputPath(normalized))
            {
                return CommandExecutionResult.Error("path_not_allowed", "output_path must be under Library/SGGUnityMcp/screenshots/ or CaptureArtifacts/");
            }

            var parts = normalized.Split('/');
            for (var index = 0; index < parts.Length; index++)
            {
                if (!IsSafeScreenshotPathPart(parts[index]))
                {
                    return CommandExecutionResult.Error("path_not_allowed", "output_path contains an unsafe path segment");
                }
            }

            relativePath = normalized;
            return null;
        }

        static bool IsApprovedScreenshotOutputPath(string normalized)
        {
            return normalized.StartsWith(ScreenshotRoot + "/", StringComparison.Ordinal)
                || normalized.StartsWith(CaptureArtifactsRoot + "/", StringComparison.Ordinal);
        }

        static bool IsSafeScreenshotPathPart(string part)
        {
            return part.Length > 0
                && part.Length <= 255
                && part != "."
                && part != ".."
                && part.IndexOf(':') < 0
                && !part.EndsWith(".", StringComparison.Ordinal)
                && !part.EndsWith(" ", StringComparison.Ordinal)
                && !IsWindowsReservedName(part);
        }

        static bool IsWindowsReservedName(string component)
        {
            var dotIndex = component.IndexOf('.');
            var stem = (dotIndex >= 0 ? component.Substring(0, dotIndex) : component).ToUpperInvariant();
            return stem == "CON" || stem == "PRN" || stem == "AUX" || stem == "NUL" || stem == "COM1" || stem == "COM2" || stem == "COM3" || stem == "COM4" || stem == "COM5" || stem == "COM6" || stem == "COM7" || stem == "COM8" || stem == "COM9" || stem == "LPT1" || stem == "LPT2" || stem == "LPT3" || stem == "LPT4" || stem == "LPT5" || stem == "LPT6" || stem == "LPT7" || stem == "LPT8" || stem == "LPT9";
        }

        static bool IsDriveAbsolute(string input)
        {
            return input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':';
        }

        static bool IsPathInside(string root, string candidate)
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return candidate.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }

        static Scene FindLoadedScene(string scenePath)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && string.Equals(scene.path, scenePath, StringComparison.Ordinal))
                {
                    return scene;
                }
            }

            return default;
        }

        static GameObject FindByPath(GameObject[] roots, string objectPath)
        {
            var normalized = NormalizeObjectPath(objectPath);
            var parts = normalized.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                return null;
            }

            foreach (var root in roots)
            {
                if (root.name != parts[0])
                {
                    continue;
                }

                var current = root;
                for (var index = 1; index < parts.Length; index++)
                {
                    current = FindChild(current, parts[index]);
                    if (current == null)
                    {
                        break;
                    }
                }

                if (current != null)
                {
                    return current;
                }
            }

            return null;
        }

        static GameObject FindChild(GameObject parent, string name)
        {
            for (var index = 0; index < parent.transform.childCount; index++)
            {
                var child = parent.transform.GetChild(index).gameObject;
                if (child.name == name)
                {
                    return child;
                }
            }

            return null;
        }

        static string NormalizeObjectPath(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                return "/";
            }

            return objectPath.StartsWith("/", StringComparison.Ordinal) ? objectPath : "/" + objectPath;
        }

        static CommandExecutionResult RequireSceneAssetPath(string argsJson, string field, out string scenePath)
        {
            scenePath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            }

            scenePath = PathGuard.RequireUnityAssetPath(rawPath);
            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", field + " must end with .unity");
            }

            return null;
        }

        static int CompareGameObjectsByPath(GameObject left, GameObject right)
        {
            return string.Compare(ObjectPath(left), ObjectPath(right), StringComparison.Ordinal);
        }

        static string ObjectPath(GameObject gameObject)
        {
            if (gameObject == null)
            {
                return "/";
            }

            var names = new List<string>();
            var current = gameObject.transform;
            while (current != null)
            {
                names.Add(current.name);
                current = current.parent;
            }

            names.Reverse();
            var builder = new StringBuilder();
            for (var index = 0; index < names.Count; index++)
            {
                builder.Append('/').Append(names[index]);
            }

            return builder.Length == 0 ? "/" : builder.ToString();
        }

        static CommandExecutionResult CaptureMultiAngle(Camera sceneViewCamera, Bounds focusBounds, string focusJson, string argsJson)
        {
            var panelWidth = Clamp(JsonUtil.ExtractInt(argsJson, "panel_width", 768), 64, 2048);
            var panelHeight = Clamp(JsonUtil.ExtractInt(argsJson, "panel_height", 432), 64, 2048);
            var totalWidth = panelWidth * 2;
            var totalHeight = panelHeight * 2;
            var relativePath = ScreenshotPath("scene-view-multi-angle");
            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            var previousActive = RenderTexture.active;
            var composite = new Texture2D(totalWidth, totalHeight, TextureFormat.RGBA32, false);
            var cameraObject = new GameObject("SGGMcp Scene View Multi Angle Camera");
            cameraObject.hideFlags = HideFlags.HideAndDontSave;
            var camera = cameraObject.AddComponent<Camera>();
            camera.hideFlags = HideFlags.HideAndDontSave;
            CopySceneCameraSettings(sceneViewCamera, camera);
            var panels = MultiAnglePanels();
            try
            {
                for (var index = 0; index < panels.Length; index++)
                {
                    CaptureMultiAnglePanel(camera, focusBounds, panels[index], panelWidth, panelHeight, composite);
                }

                composite.Apply();
                File.WriteAllBytes(fullPath, composite.EncodeToPNG());
            }
            finally
            {
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(cameraObject);
                UnityEngine.Object.DestroyImmediate(composite);
            }

            return CommandExecutionResult.Ok("{\"project_relative_path\":" + JsonUtil.String(relativePath)
                + ",\"width\":" + totalWidth
                + ",\"height\":" + totalHeight
                + ",\"panel_width\":" + panelWidth
                + ",\"panel_height\":" + panelHeight
                + ",\"layout\":\"2x2\""
                + ",\"projection\":\"orthographic\""
                + ",\"focus\":" + focusJson
                + ",\"angles\":" + MultiAnglePanelsJson(panels, panelWidth, panelHeight)
                + ",\"format\":\"png\"}");
        }

        static void CopySceneCameraSettings(Camera source, Camera target)
        {
            target.clearFlags = source.clearFlags;
            target.backgroundColor = source.backgroundColor;
            target.cullingMask = source.cullingMask;
            target.allowHDR = source.allowHDR;
            target.allowMSAA = source.allowMSAA;
            target.useOcclusionCulling = source.useOcclusionCulling;
        }

        static void CaptureMultiAnglePanel(Camera camera, Bounds bounds, MultiAnglePanel panel, int panelWidth, int panelHeight, Texture2D composite)
        {
            var previousTarget = camera.targetTexture;
            var renderTexture = RenderTexture.GetTemporary(panelWidth, panelHeight, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(panelWidth, panelHeight, TextureFormat.RGBA32, false);
            try
            {
                ConfigureMultiAngleCamera(camera, bounds, panel, panelWidth, panelHeight);
                camera.targetTexture = renderTexture;
                camera.Render();
                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, panelWidth, panelHeight), 0, 0);
                texture.Apply();
                composite.SetPixels(panel.Column * panelWidth, PanelPixelY(panel, panelHeight), panelWidth, panelHeight, texture.GetPixels());
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        static void ConfigureMultiAngleCamera(Camera camera, Bounds bounds, MultiAnglePanel panel, int panelWidth, int panelHeight)
        {
            var forward = panel.Forward.normalized;
            var rotation = Quaternion.LookRotation(forward, panel.Up);
            var radius = Math.Max(0.5f, bounds.extents.magnitude);
            var distance = Math.Max(10f, radius * 4f);
            var aspect = panelWidth / (float)panelHeight;
            camera.transform.position = bounds.center - forward * distance;
            camera.transform.rotation = rotation;
            camera.orthographic = true;
            camera.aspect = aspect;
            camera.orthographicSize = ProjectedOrthographicSize(bounds, rotation, aspect) * 1.15f;
            camera.nearClipPlane = 0.01f;
            camera.farClipPlane = distance + radius * 6f;
        }

        static float ProjectedOrthographicSize(Bounds bounds, Quaternion rotation, float aspect)
        {
            var inverse = Quaternion.Inverse(rotation);
            var corners = BoundsCorners(bounds);
            var minX = 0f;
            var maxX = 0f;
            var minY = 0f;
            var maxY = 0f;
            for (var index = 0; index < corners.Length; index++)
            {
                var local = inverse * (corners[index] - bounds.center);
                if (index == 0)
                {
                    minX = local.x;
                    maxX = local.x;
                    minY = local.y;
                    maxY = local.y;
                }
                else
                {
                    minX = Math.Min(minX, local.x);
                    maxX = Math.Max(maxX, local.x);
                    minY = Math.Min(minY, local.y);
                    maxY = Math.Max(maxY, local.y);
                }
            }

            var halfWidth = (maxX - minX) * 0.5f;
            var halfHeight = (maxY - minY) * 0.5f;
            return Math.Max(0.5f, Math.Max(halfHeight, halfWidth / Math.Max(0.01f, aspect)));
        }

        static Vector3[] BoundsCorners(Bounds bounds)
        {
            var min = bounds.min;
            var max = bounds.max;
            return new[]
            {
                new Vector3(min.x, min.y, min.z),
                new Vector3(max.x, min.y, min.z),
                new Vector3(min.x, max.y, min.z),
                new Vector3(max.x, max.y, min.z),
                new Vector3(min.x, min.y, max.z),
                new Vector3(max.x, min.y, max.z),
                new Vector3(min.x, max.y, max.z),
                new Vector3(max.x, max.y, max.z)
            };
        }

        static MultiAnglePanel[] MultiAnglePanels()
        {
            return new[]
            {
                new MultiAnglePanel("isometric", new Vector3(1f, -0.75f, 1f), Vector3.up, 0, 0),
                new MultiAnglePanel("front", Vector3.forward, Vector3.up, 1, 0),
                new MultiAnglePanel("top", Vector3.down, Vector3.forward, 0, 1),
                new MultiAnglePanel("right", Vector3.left, Vector3.up, 1, 1)
            };
        }

        static string MultiAnglePanelsJson(MultiAnglePanel[] panels, int panelWidth, int panelHeight)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < panels.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var panel = panels[index];
                builder.Append("{\"name\":").Append(JsonUtil.String(panel.Name));
                builder.Append(",\"row\":").Append(panel.Row);
                builder.Append(",\"column\":").Append(panel.Column);
                builder.Append(",\"pixel_x\":").Append(panel.Column * panelWidth);
                builder.Append(",\"pixel_y\":").Append(PanelPixelY(panel, panelHeight));
                builder.Append(",\"width\":").Append(panelWidth);
                builder.Append(",\"height\":").Append(panelHeight);
                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        static int PanelPixelY(MultiAnglePanel panel, int panelHeight)
        {
            return panel.Row == 0 ? panelHeight : 0;
        }

        static CommandExecutionResult CaptureCamera(Camera camera, string prefix, string argsJson)
        {
            return CaptureCamera(camera, prefix, argsJson, -1);
        }

        static CommandExecutionResult CaptureCamera(Camera camera, string prefix, string argsJson, int selectedIndex)
        {
            return CaptureCamera(camera, prefix, argsJson, selectedIndex, "camera_render");
        }

        static CommandExecutionResult CaptureCamera(Camera camera, string prefix, string argsJson, int selectedIndex, string requestedBackend)
        {
            var width = Clamp(JsonUtil.ExtractInt(argsJson, "width", Math.Max(64, camera.pixelWidth)), 64, 4096);
            var height = Clamp(JsonUtil.ExtractInt(argsJson, "height", Math.Max(64, camera.pixelHeight)), 64, 4096);
            var includeStack = JsonUtil.ExtractBool(argsJson, "include_stack", false);
            var includeOverlayUiProvided = JsonUtil.HasProperty(argsJson, "include_overlay_ui");
            var stack = includeStack ? CameraStack(camera) : new List<Camera>();
            var pathResult = ResolveScreenshotOutputPath(argsJson, ScreenshotPath(prefix), out var relativePath, out var fullPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            var dryRun = JsonUtil.ExtractBool(argsJson, "dry_run", false);
            if (dryRun)
            {
                return CameraScreenshotStatus("dry_run", relativePath, width, height, camera, selectedIndex, includeStack, stack, requestedBackend, false, includeOverlayUiProvided, null, 0, default);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(fullPath));

            var previousTarget = camera.targetTexture;
            var previousActive = RenderTexture.active;
            var previousStackTargets = new RenderTexture[stack.Count];
            var renderTexture = RenderTexture.GetTemporary(width, height, 24, RenderTextureFormat.ARGB32);
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
            byte[] png = null;
            var metrics = default(GameViewCaptureBridge.SampleMetrics);
            try
            {
                camera.targetTexture = renderTexture;
                for (var index = 0; index < stack.Count; index++)
                {
                    var stackCamera = stack[index];
                    if (stackCamera != null && stackCamera.enabled)
                    {
                        previousStackTargets[index] = stackCamera.targetTexture;
                        stackCamera.targetTexture = renderTexture;
                    }
                }

                ClearCameraRenderTarget(renderTexture);
                RenderCameraPass(camera, stack);

                RenderTexture.active = renderTexture;
                texture.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                texture.Apply(false, false);
                metrics = GameViewCaptureBridge.SampleMetrics.FromPixels(texture.GetPixels32(), width, height);
                png = texture.EncodeToPNG();
                File.WriteAllBytes(fullPath, png);
            }
            finally
            {
                camera.targetTexture = previousTarget;
                for (var index = 0; index < stack.Count; index++)
                {
                    if (stack[index] != null)
                    {
                        stack[index].targetTexture = previousStackTargets[index];
                    }
                }

                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(renderTexture);
                UnityEngine.Object.DestroyImmediate(texture);
            }

            return CameraScreenshotStatus("ok", relativePath, width, height, camera, selectedIndex, includeStack, stack, requestedBackend, false, includeOverlayUiProvided, Sha256Hex(png), png.LongLength, metrics);
        }

        static void ClearCameraRenderTarget(RenderTexture renderTexture)
        {
            RenderTexture.active = renderTexture;
            GL.Clear(true, true, Color.clear);
        }

        static void RenderCameraPass(Camera camera, List<Camera> stack)
        {
            camera.Render();
            for (var index = 0; index < stack.Count; index++)
            {
                var stackCamera = stack[index];
                if (stackCamera == null || !stackCamera.enabled)
                {
                    continue;
                }

                try
                {
                    stackCamera.Render();
                }
                catch (Exception)
                {
                }
            }
        }

        static CommandExecutionResult CameraScreenshotStatus(string status, string relativePath, int width, int height, Camera camera, int selectedIndex, bool includeStack, List<Camera> stack, string requestedBackend, bool includeOverlayUi, bool includeOverlayUiProvided, string sha256, long bytes, GameViewCaptureBridge.SampleMetrics metrics)
        {
            var artifactAvailable = status == "ok";
            return CommandExecutionResult.Ok("{\"status\":" + JsonUtil.String(status)
                + ",\"artifact_path\":" + (artifactAvailable ? JsonUtil.String(relativePath) : "null")
                + ",\"project_relative_path\":" + (artifactAvailable ? JsonUtil.String(relativePath) : "null")
                + ",\"artifact_ref\":" + (artifactAvailable ? JsonUtil.String(relativePath) : "null")
                + ",\"planned_artifact_path\":" + JsonUtil.String(relativePath)
                + ",\"width\":" + width
                + ",\"height\":" + height
                + ",\"dimensions\":{\"width\":" + width + ",\"height\":" + height + "}"
                + ",\"camera\":" + JsonUtil.String(camera.name)
                + ",\"camera_metadata\":" + CameraMetadataJson(camera, selectedIndex)
                + ",\"stack\":" + StackMetadataJson(includeStack, stack)
                + ",\"requested_backend\":" + JsonUtil.String(requestedBackend)
                + ",\"effective_backend\":\"camera_render\""
                + ",\"backend\":\"camera_render\""
                + ",\"backend_alias_resolved\":false"
                + ",\"authoritative\":false"
                + ",\"composition\":\"camera_only\""
                + ",\"fallback_attempted\":false"
                + ",\"include_overlay_ui\":" + JsonUtil.Bool(includeOverlayUi)
                + ",\"requested_include_overlay_ui\":" + (includeOverlayUiProvided ? JsonUtil.Bool(includeOverlayUi) : "null")
                + ",\"include_overlay_ui_explicit\":" + JsonUtil.Bool(includeOverlayUiProvided)
                + ",\"render_target_initialized\":" + JsonUtil.Bool(status == "ok")
                + ",\"render_pass_count\":" + (status == "ok" ? 1 : 0)
                + ",\"readback_backend\":\"synchronous_readpixels_camera_render\""
                + ",\"sha256\":" + (string.IsNullOrEmpty(sha256) ? "null" : JsonUtil.String(sha256))
                + ",\"bytes\":" + bytes
                + ",\"sample_metrics\":{\"sample_count\":" + metrics.Count
                + ",\"grid_max\":\"64x64\",\"zero_count\":" + metrics.Zero
                + ",\"near_black_count\":" + metrics.NearBlack
                + ",\"transparent_count\":" + metrics.Transparent
                + ",\"uniform_count\":" + metrics.Uniform
                + ",\"unique_colors\":" + metrics.Unique + "}"
                + ",\"fidelity_status\":\"visual_review_required\""
                + ",\"live_pixel_agreement_claimed\":false"
                + ",\"visual_review_required\":true"
                + ",\"format\":\"png\"}");
        }

        static List<Camera> CameraStack(Camera camera)
        {
            var stack = new List<Camera>();
            var components = camera.GetComponents<Component>();
            for (var componentIndex = 0; componentIndex < components.Length; componentIndex++)
            {
                var component = components[componentIndex];
                if (component == null)
                {
                    continue;
                }

                var property = component.GetType().GetProperty("cameraStack", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                System.Collections.IEnumerable value = null;
                try
                {
                    value = property == null ? null : property.GetValue(component, null) as System.Collections.IEnumerable;
                }
                catch (Exception)
                {
                    value = null;
                }

                if (value == null)
                {
                    continue;
                }

                try
                {
                    foreach (var item in value)
                    {
                        var stackCamera = item as Camera;
                        if (stackCamera != null)
                        {
                            stack.Add(stackCamera);
                        }
                    }
                }
                catch (Exception)
                {
                    stack.Clear();
                }

                break;
            }

            return stack;
        }

        static string CameraMetadataJson(Camera camera, int index)
        {
            var builder = new StringBuilder();
            AppendCameraMetadata(builder, camera, index);
            return builder.ToString();
        }

        static void AppendCameraMetadata(StringBuilder builder, Camera camera, int index)
        {
            builder.Append("{\"index\":").Append(index);
            builder.Append(",\"name\":").Append(JsonUtil.String(camera.name));
            builder.Append(",\"path\":").Append(JsonUtil.String(ObjectPath(camera.gameObject)));
            builder.Append(",\"scene\":").Append(JsonUtil.String(camera.gameObject.scene.name));
            builder.Append(",\"scene_path\":").Append(JsonUtil.String(camera.gameObject.scene.path));
            AppendRuntimeObjectIds(builder, camera);
            builder.Append(",\"enabled\":").Append(JsonUtil.Bool(camera.enabled));
            builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(camera.gameObject.activeInHierarchy));
            builder.Append(",\"tag\":").Append(JsonUtil.String(camera.tag));
            builder.Append("}");
        }

        static void AppendRuntimeObjectIds(StringBuilder builder, UnityEngine.Object selectedObject)
        {
#if UNITY_6000_4_OR_NEWER
            var entityId = UnityEngine.EntityId.ToULong(selectedObject.GetEntityId());
            var legacyInstanceId = entityId <= int.MaxValue ? (int)entityId : 0;
            builder.Append(",\"instance_id\":").Append(legacyInstanceId);
            builder.Append(",\"entity_id\":").Append(JsonUtil.String(entityId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
#else
            builder.Append(",\"instance_id\":").Append(selectedObject.GetInstanceID());
#endif
        }

        static string StackMetadataJson(bool includeStack, List<Camera> stack)
        {
            var builder = new StringBuilder();
            builder.Append("{\"requested\":").Append(JsonUtil.Bool(includeStack));
            builder.Append(",\"detected\":").Append(JsonUtil.Bool(stack.Count > 0));
            builder.Append(",\"capture_mode\":").Append(JsonUtil.String(includeStack && stack.Count > 0 ? "best_effort_reflection" : "base_camera_only"));
            builder.Append(",\"screen_space_overlay_ui\":\"not_captured\"");
            builder.Append(",\"cameras\":[");
            for (var index = 0; index < stack.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendCameraMetadata(builder, stack[index], -1);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static string DesktopRegionBackend()
        {
#if UNITY_EDITOR_WIN
            return "screen_copy";
#else
            return "unsupported";
#endif
        }

        static bool TryCaptureScreenRegion(int x, int y, int width, int height, string fullPath, out string message)
        {
#if UNITY_EDITOR_WIN
            return TryCaptureScreenRegionWin32(x, y, width, height, fullPath, out message);
#else
            message = "Desktop region screenshots are supported only on Windows";
            return false;
#endif
        }

#if UNITY_EDITOR_WIN
        static bool TryCaptureScreenRegionWin32(int x, int y, int width, int height, string fullPath, out string message)
        {
            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                message = "GetDC failed";
                return false;
            }

            var memoryDc = IntPtr.Zero;
            var bitmap = IntPtr.Zero;
            var previous = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero)
                {
                    message = "CreateCompatibleDC failed";
                    return false;
                }

                bitmap = CreateCompatibleBitmap(screenDc, width, height);
                if (bitmap == IntPtr.Zero)
                {
                    message = "CreateCompatibleBitmap failed";
                    return false;
                }

                previous = SelectObject(memoryDc, bitmap);
                if (!BitBlt(memoryDc, 0, 0, width, height, screenDc, x, y, SRCCOPY | CAPTUREBLT))
                {
                    message = "BitBlt failed";
                    return false;
                }

                return TryWriteBitmapPng(screenDc, bitmap, width, height, 0, 0, width, height, fullPath, out message);
            }
            finally
            {
                if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero)
                {
                    SelectObject(memoryDc, previous);
                }

                if (bitmap != IntPtr.Zero)
                {
                    DeleteObject(bitmap);
                }

                if (memoryDc != IntPtr.Zero)
                {
                    DeleteDC(memoryDc);
                }

                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }

        static bool TryWriteBitmapPng(IntPtr hdc, IntPtr bitmap, int bitmapWidth, int bitmapHeight, int cropX, int cropY, int cropWidth, int cropHeight, string fullPath, out string message)
        {
            message = null;
            if (cropX < 0 || cropY < 0 || cropWidth <= 0 || cropHeight <= 0 || cropX + cropWidth > bitmapWidth || cropY + cropHeight > bitmapHeight)
            {
                message = "Capture crop is outside the native bitmap";
                return false;
            }

            var info = BitmapInfo.Create(bitmapWidth, bitmapHeight);
            var bytes = new byte[bitmapWidth * bitmapHeight * 4];
            if (GetDIBits(hdc, bitmap, 0, (uint)bitmapHeight, bytes, ref info, DIB_RGB_COLORS) == 0)
            {
                message = "GetDIBits failed";
                return false;
            }

            var colors = new Color32[cropWidth * cropHeight];
            for (var destY = 0; destY < cropHeight; destY++)
            {
                var sourceY = cropY + cropHeight - 1 - destY;
                for (var destX = 0; destX < cropWidth; destX++)
                {
                    var sourceX = cropX + destX;
                    var sourceIndex = ((sourceY * bitmapWidth) + sourceX) * 4;
                    colors[(destY * cropWidth) + destX] = new Color32(bytes[sourceIndex + 2], bytes[sourceIndex + 1], bytes[sourceIndex], 255);
                }
            }

            var texture = new Texture2D(cropWidth, cropHeight, TextureFormat.RGBA32, false);
            try
            {
                texture.SetPixels32(colors);
                texture.Apply(false);
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath));
                File.WriteAllBytes(fullPath, texture.EncodeToPNG());
                return File.Exists(fullPath);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }
#endif

        static string ScreenshotPath(string prefix)
        {
            return ScreenshotRoot + "/" + prefix + "-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png";
        }

        static string DesktopRegionScreenshotPath()
        {
            return DesktopRegionScreenshotRoot + "/region-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff") + ".png";
        }

        static string SanitizeFilePart(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "window";
            }

            var builder = new StringBuilder();
            for (var index = 0; index < value.Length && builder.Length < 64; index++)
            {
                var ch = value[index];
                builder.Append(char.IsLetterOrDigit(ch) || ch == '-' || ch == '_' ? ch : '-');
            }

            return builder.Length == 0 ? "window" : builder.ToString();
        }

        sealed class GameViewFocusCapturePlan
        {
            public string PlanId;
            public string PlanHash;
            public string BridgeInstanceId;
            public string CommandsHash;
            public long CatalogGeneration;
            public int WindowIndex;
            public string WindowsHash;
            public string WindowRuntimeIdentity;
            public string CaptureArgsJson;
            public string CaptureArgsHash;
            public DateTime CreatedUtc;
            public DateTime ExpiresUtc;
        }

        sealed class GameViewFocusCaptureExecution
        {
            public string PlanId;
            public string PlanHash;
            public string WindowRuntimeIdentity;
            public string WindowsHashBeforeFocus;
            public string WindowsHashAfterFocus;
        }

        [Serializable]
        sealed class GameViewCaptureState
        {
            public string CaptureId;
            public string Status;
            public string RequestedBackend;
            public string EffectiveBackend;
            public bool BackendAliasResolved;
            public bool Authoritative;
            public string Composition;
            public bool IncludeOverlayUi;
            public bool IncludeOverlayUiProvided;
            public string RelativePath;
            [NonSerialized]
            public string FullPath;
            [NonSerialized]
            public string StagingPath;
            public int RequestedWidth;
            public int RequestedHeight;
            public int ScheduleWidth;
            public int ScheduleHeight;
            public int ScheduleScreenWidth;
            public int ScheduleScreenHeight;
            public int SubmissionWidth;
            public int SubmissionHeight;
            public int SubmissionScreenWidth;
            public int SubmissionScreenHeight;
            public int Width;
            public int Height;
            public string DimensionsSource;
            public bool DimensionsMatch;
            public bool FinalGameViewResized;
            public bool ResolutionChangeSupported;
            public bool ResolutionChangeRequired;
            public int ResolutionOriginalWidth;
            public int ResolutionOriginalHeight;
            public int ResolutionTargetWidth;
            public int ResolutionTargetHeight;
            public bool ResolutionApplyAttempted;
            public bool ResolutionApplySucceeded;
            public bool ResolutionApplyVerified;
            public string ResolutionApplyFailureReason;
            public bool ResolutionRestoreRequired;
            public bool ResolutionRestoreAttempted;
            public bool ResolutionRestoreSucceeded;
            public bool ResolutionRestoreVerified;
            public bool ResolutionRestoreScreenConverged;
            public string ResolutionRestoreFailureReason;
            public int SettleFramesRequested;
            public int SettleFramesObserved;
            public int TimeoutMs;
            public int ScheduleFrame;
            public int LastSettleFrame;
            public int LastRenderedFrame;
            public int SubmissionFrame;
            public int CompletionFrame;
            public DateTime CreatedUtc;
            public DateTime UpdatedUtc;
            public DateTime ExpiresUtc;
            public DateTime DeadlineUtc;
            public DateTime SubmissionUtc;
            public DateTime CompletionUtc;
            public string BridgeInstanceId;
            public int PlayModeGeneration;
            public string PlayModeBarrierId;
            public DateTime PlayModeEnteredUtc;
            public ulong ScheduledSceneHandle;
            public string ScheduledScenePath;
            public string ScheduledSceneName;
            public int ScheduledSceneSetCount;
            public string ScheduledSceneSetFingerprint;
            public bool PlayModeWindowApiSupported;
            public bool GameViewConfirmed;
            public string SelectedPlayModeViewType;
            public string PlayModeWindowFailureReason;
            public int CurrentEditorWidth;
            public int CurrentEditorHeight;
            public int CurrentScreenWidth;
            public int CurrentScreenHeight;
            public string ReadbackBackend;
            public bool ReadbackCompletionAccepted;
            public bool ReadbackAbandoned;
            public bool PublishReserved;
            public string ErrorCode;
            public string Message;
            public bool ApplicationFocusedAtSchedule;
            public bool EditorPausedAtSchedule;
            public bool AutomaticFocusAttempted;
            public bool FocusVerified;
            public string FocusCapturePlanId;
            public string FocusCapturePlanHash;
            public string FocusWindowRuntimeIdentity;
            public string FocusWindowsHashBefore;
            public string FocusWindowsHashAfter;
            public int WatchdogPollCount;
            public DateTime LastWatchdogPollUtc;
            public bool CompletionDeadlineChecked;
            public bool CancelRequested;
            public bool CleanupPerformed;
            public bool LateCompletionDiscarded;
            public string Sha256;
            public long Bytes;
            public int SampleCount;
            public int ZeroSampleCount;
            public int NearBlackSampleCount;
            public int TransparentSampleCount;
            public int UniformSampleCount;
            public int UniqueSampleCount;
            public int SentinelSampleCount;
            public string FidelityStatus;
            public bool DryRun;
            public long CreatedTicks;
            public long UpdatedTicks;
            public long ExpiresTicks;
            public long DeadlineTicks;
            public long SubmissionTicks;
            public long CompletionTicks;
            public long PlayModeEnteredTicks;
        }

#if UNITY_EDITOR_WIN
        const int SRCCOPY = 0x00CC0020;
        const int CAPTUREBLT = 0x40000000;
        const uint BI_RGB = 0;
        const uint DIB_RGB_COLORS = 0;

        [DllImport("user32.dll")]
        static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll")]
        static extern int ReleaseDC(IntPtr hWnd, IntPtr hDc);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

        [DllImport("gdi32.dll")]
        static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll")]
        static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSource, int xSource, int ySource, int rasterOperation);

        [DllImport("gdi32.dll")]
        static extern int GetDIBits(IntPtr hdc, IntPtr bitmap, uint startScan, uint scanLines, byte[] bits, ref BitmapInfo bitmapInfo, uint usage);

        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfoHeader
        {
            public uint Size;
            public int Width;
            public int Height;
            public ushort Planes;
            public ushort BitCount;
            public uint Compression;
            public uint SizeImage;
            public int XPelsPerMeter;
            public int YPelsPerMeter;
            public uint ClrUsed;
            public uint ClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BitmapInfo
        {
            public BitmapInfoHeader Header;
            public uint Colors;

            public static BitmapInfo Create(int width, int height)
            {
                return new BitmapInfo
                {
                    Header = new BitmapInfoHeader
                    {
                        Size = (uint)Marshal.SizeOf(typeof(BitmapInfoHeader)),
                        Width = width,
                        Height = -height,
                        Planes = 1,
                        BitCount = 32,
                        Compression = BI_RGB,
                        SizeImage = (uint)(width * height * 4),
                    },
                    Colors = 0,
                };
            }
        }
#endif

        struct MultiAnglePanel
        {
            public readonly string Name;
            public readonly Vector3 Forward;
            public readonly Vector3 Up;
            public readonly int Column;
            public readonly int Row;

            public MultiAnglePanel(string name, Vector3 forward, Vector3 up, int column, int row)
            {
                Name = name;
                Forward = forward;
                Up = up;
                Column = column;
                Row = row;
            }
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? JsonUtil.String(value.ToString()) : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
    }
}
