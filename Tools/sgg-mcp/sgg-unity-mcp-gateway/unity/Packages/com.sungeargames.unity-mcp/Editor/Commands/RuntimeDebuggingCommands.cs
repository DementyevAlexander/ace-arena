using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class RuntimeDebuggingCommands
    {
        const string FeatureFlag = "runtime_state_debugging";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string CaptureSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"max_objects\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"include_inactive\":{\"type\":\"boolean\"},\"include_components\":{\"type\":\"boolean\"},\"include_transform\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string StateMachineSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"max_frames\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":120},\"sample_every_frames\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":60},\"max_objects\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"include_inactive\":{\"type\":\"boolean\"},\"include_components\":{\"type\":\"boolean\"},\"include_transform\":{\"type\":\"boolean\"},\"stop_when_found\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string EventCaptureStartSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"max_objects\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"include_inactive\":{\"type\":\"boolean\"},\"include_components\":{\"type\":\"boolean\"},\"include_transform\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string EventCaptureIdSchemaJson = "{\"type\":\"object\",\"properties\":{\"capture_id\":{\"type\":\"string\"}},\"required\":[\"capture_id\"],\"additionalProperties\":false}";
        const string EventCaptureCancelSchemaJson = "{\"type\":\"object\",\"properties\":{\"capture_id\":{\"type\":\"string\"},\"reason\":{\"type\":\"string\",\"maxLength\":240}},\"required\":[\"capture_id\"],\"additionalProperties\":false}";
        const string EventCaptureReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"capture_id\":{\"type\":\"string\"},\"artifact_path\":{\"type\":\"string\"},\"max_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200000}},\"additionalProperties\":false}";
        const string OutputLimitJson = "{\"max_objects\":500}";
        const string StateMachineOutputLimitJson = "{\"max_frames\":120,\"max_objects\":200}";
        const string EventCaptureOutputLimitJson = "{\"max_objects\":200,\"max_chars\":200000}";
        const int QualityNamesMaxChars = 2048;
        const int QualityNameMaxChars = 256;
        const string JobArtifactRoot = "Library/SGGUnityMcp/job-artifacts/";
        const string EventCaptureArtifactRoot = JobArtifactRoot + "runtime-event-captures/";

        static readonly object s_captureLock = new object();
        static readonly Dictionary<string, EventCapture> s_eventCaptures = new Dictionary<string, EventCapture>(StringComparer.Ordinal);

        [McpCommand(
            "runtime.capture_state",
            Title = "Capture runtime state",
            Description = "Capture a bounded Play Mode GameObject state snapshot",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = CaptureSchemaJson,
            Tags = new string[] { "runtime", "state" },
            Capability = "runtime_state_debugging",
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "bounded_list",
            OutputLimitJson = OutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CaptureState(McpCommandContext context)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "runtime.capture_state requires Play Mode");
            }

            var objectsJson = BuildObjectsJson(context.ArgsJson, 100, 500, out var query, out var objectPath, out var scanned, out var truncated);

            return CommandExecutionResult.Ok("{\"is_playing\":" + JsonUtil.Bool(EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
                + ",\"is_paused\":" + JsonUtil.Bool(EditorApplication.isPaused)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"matched_objects\":" + scanned
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"objects\":[" + objectsJson + "]}");
        }

        [McpCommand(
            "runtime.quality_settings.inspect",
            Title = "Inspect effective runtime quality settings",
            Description = "Read a fixed bounded snapshot of effective UnityEngine.QualitySettings values in Play Mode",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "runtime", "quality-settings", "diagnostics" },
            Capability = "runtime_state_debugging",
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "object_details",
            OutputLimitJson = "{\"max_quality_levels\":64,\"max_chars\":4096}",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult QualitySettingsInspect(McpCommandContext context)
        {
            if (!EditorApplication.isPlaying)
            {
                return CommandExecutionResult.Error("unity_busy", "runtime.quality_settings.inspect requires Play Mode");
            }

            var qualityNames = QualitySettings.names ?? Array.Empty<string>();
            var qualityLevel = QualitySettings.GetQualityLevel();
            var qualityName = qualityLevel >= 0 && qualityLevel < qualityNames.Length ? qualityNames[qualityLevel] : string.Empty;
            var qualityNameJson = BoundedJsonString(qualityName, QualityNameMaxChars, out var qualityNameTruncated);
            var qualityNamesJson = StringArrayJson(qualityNames, 64, QualityNamesMaxChars, out var qualityNamesReturned, out var qualityNamesTruncated);
            return CommandExecutionResult.Ok("{\"is_playing\":" + JsonUtil.Bool(EditorApplication.isPlaying)
                + ",\"is_paused\":" + JsonUtil.Bool(EditorApplication.isPaused)
                + ",\"active_quality_level\":{\"index\":" + qualityLevel + ",\"name\":" + qualityNameJson + ",\"name_truncated\":" + JsonUtil.Bool(qualityNameTruncated) + "}"
                + ",\"quality_level_count\":" + qualityNames.Length
                + ",\"quality_names_returned\":" + qualityNamesReturned
                + ",\"quality_names_truncated\":" + JsonUtil.Bool(qualityNamesTruncated)
                + ",\"quality_names\":" + qualityNamesJson
                + ",\"v_sync_count\":" + QualitySettings.vSyncCount
                + ",\"anti_aliasing\":" + QualitySettings.antiAliasing
                + ",\"anisotropic_filtering\":" + JsonUtil.String(QualitySettings.anisotropicFiltering.ToString())
                + ",\"pixel_light_count\":" + QualitySettings.pixelLightCount
                + ",\"shadows\":" + JsonUtil.String(QualitySettings.shadows.ToString())
                + ",\"shadow_resolution\":" + JsonUtil.String(QualitySettings.shadowResolution.ToString())
                + ",\"shadow_projection\":" + JsonUtil.String(QualitySettings.shadowProjection.ToString())
                + ",\"shadow_distance\":" + FloatJson(QualitySettings.shadowDistance)
                + ",\"shadow_cascades\":" + QualitySettings.shadowCascades
                + ",\"lod_bias\":" + FloatJson(QualitySettings.lodBias)
                + ",\"maximum_lod_level\":" + QualitySettings.maximumLODLevel
                + ",\"soft_particles\":" + JsonUtil.Bool(QualitySettings.softParticles)
                + ",\"soft_vegetation\":" + JsonUtil.Bool(QualitySettings.softVegetation)
                + ",\"realtime_reflection_probes\":" + JsonUtil.Bool(QualitySettings.realtimeReflectionProbes)
                + ",\"source\":\"UnityEngine.QualitySettings\"}");
        }

        [McpCommand(
            "runtime.state_machine.compile",
            Title = "Compile runtime state machine",
            Description = "Validate and normalize a bounded runtime sampling plan",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = StateMachineSchemaJson,
            Tags = new string[] { "runtime", "state-machine" },
            Capability = "runtime_state_debugging",
            ResultShape = "object_details",
            OutputLimitJson = StateMachineOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CompileStateMachine(McpCommandContext context)
        {
            return CommandExecutionResult.Ok("{\"compiled\":true,\"plan\":" + PlanJson(context.ArgsJson) + "}");
        }

        [McpCommand(
            "runtime.state_machine.run",
            Title = "Run runtime state machine",
            Description = "Run a bounded Play Mode runtime sampling plan",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 60000,
            SchemaJson = StateMachineSchemaJson,
            Tags = new string[] { "runtime", "state-machine" },
            Capability = "runtime_state_debugging",
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "bounded_list",
            OutputLimitJson = StateMachineOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult RunStateMachine(McpCommandContext context)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "runtime.state_machine.run requires Play Mode");
            }

            var maxFrames = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_frames", 1), 1, 120);
            var sampleEveryFrames = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "sample_every_frames", 1), 1, 60);
            var stopWhenFound = JsonUtil.ExtractBool(context.ArgsJson, "stop_when_found", false);
            if (maxFrames > 1 && !EditorApplication.isPaused)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "Multi-frame runtime.state_machine.run requires paused Play Mode in this slice", "max_frames", "state_constraint_failed");
            }

            var samples = new StringBuilder();
            var totalMatched = 0;
            var truncated = false;
            var capturedSamples = 0;

            for (var frame = 0; frame < maxFrames; frame += sampleEveryFrames)
            {
                var objectsJson = BuildObjectsJson(context.ArgsJson, 50, 200, out _, out _, out var matched, out var sampleTruncated);
                totalMatched += matched;
                truncated = truncated || sampleTruncated;
                if (capturedSamples > 0)
                {
                    samples.Append(',');
                }

                samples.Append("{\"index\":").Append(capturedSamples);
                samples.Append(",\"frame_count\":").Append(Time.frameCount);
                samples.Append(",\"matched_objects\":").Append(matched);
                samples.Append(",\"truncated\":").Append(JsonUtil.Bool(sampleTruncated));
                samples.Append(",\"objects\":").Append('[').Append(objectsJson).Append("]}");
                capturedSamples++;

                if (stopWhenFound && matched > 0)
                {
                    break;
                }

                if (frame + sampleEveryFrames < maxFrames)
                {
                    StepPausedFrames(sampleEveryFrames);
                }
            }

            return CommandExecutionResult.Ok("{\"plan\":" + PlanJson(context.ArgsJson)
                + ",\"is_paused\":" + JsonUtil.Bool(EditorApplication.isPaused)
                + ",\"captured_samples\":" + capturedSamples
                + ",\"total_matched_objects\":" + totalMatched
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"samples\":[" + samples + "]}");
        }

        [McpCommand(
            "runtime.event_capture.start",
            Title = "Start runtime event capture",
            Description = "Start a bounded Play Mode event capture session",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = EventCaptureStartSchemaJson,
            Tags = new string[] { "runtime", "event-capture" },
            Capability = "runtime_state_debugging",
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "object_details",
            OutputLimitJson = EventCaptureOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EventCaptureStart(McpCommandContext context)
        {
            if (!EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "runtime.event_capture.start requires Play Mode");
            }

            var captureId = Guid.NewGuid().ToString("N");
            var capture = new EventCapture
            {
                Id = captureId,
                ArgsJson = context.ArgsJson,
                StartedFrame = Time.frameCount,
                StartedAtUtc = DateTime.UtcNow,
            };

            lock (s_captureLock)
            {
                s_eventCaptures[captureId] = capture;
            }

            return CommandExecutionResult.Ok("{\"capture_id\":" + JsonUtil.String(captureId)
                + ",\"status\":\"running\""
                + ",\"started_frame\":" + capture.StartedFrame
                + ",\"started_at_utc\":" + JsonUtil.String(capture.StartedAtUtc.ToString("o"))
                + ",\"plan\":" + EventCapturePlanJson(context.ArgsJson) + "}");
        }

        [McpCommand(
            "runtime.event_capture.status",
            Title = "Runtime event capture status",
            Description = "Read compact status for a runtime event capture session",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EventCaptureIdSchemaJson,
            Tags = new string[] { "runtime", "event-capture" },
            Capability = "runtime_state_debugging",
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "status",
            OutputLimitJson = EventCaptureOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EventCaptureStatus(McpCommandContext context)
        {
            var capture = RequireEventCapture(context.ArgsJson);
            if (capture == null)
            {
                return CommandExecutionResult.Error("asset_not_found", "Capture id was not found");
            }

            BuildObjectsJson(capture.ArgsJson, 50, 200, out _, out _, out var matched, out var truncated);
            return CommandExecutionResult.Ok("{\"capture_id\":" + JsonUtil.String(capture.Id)
                + ",\"status\":\"running\""
                + ",\"started_frame\":" + capture.StartedFrame
                + ",\"current_frame\":" + Time.frameCount
                + ",\"elapsed_frames\":" + Math.Max(0, Time.frameCount - capture.StartedFrame)
                + ",\"matched_objects\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"plan\":" + EventCapturePlanJson(capture.ArgsJson) + "}");
        }

        [McpCommand(
            "runtime.event_capture.stop",
            Title = "Stop runtime event capture",
            Description = "Stop a runtime event capture session and return a bounded final snapshot",
            Risk = "read",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = EventCaptureIdSchemaJson,
            Tags = new string[] { "runtime", "event-capture" },
            Capability = "runtime_state_debugging",
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "bounded_list",
            OutputLimitJson = EventCaptureOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EventCaptureStop(McpCommandContext context)
        {
            var captureId = JsonUtil.ExtractString(context.ArgsJson, "capture_id");
            if (string.IsNullOrEmpty(captureId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument capture_id is required");
            }

            EventCapture capture;
            lock (s_captureLock)
            {
                if (!s_eventCaptures.TryGetValue(captureId, out capture))
                {
                    return CommandExecutionResult.Error("asset_not_found", "Capture id was not found");
                }

                s_eventCaptures.Remove(captureId);
            }

            var objectsJson = BuildObjectsJson(capture.ArgsJson, 50, 200, out var query, out var objectPath, out var matched, out var truncated);
            var artifactPath = EventCaptureArtifactRoot + capture.Id + ".json";
            var resultJson = "{\"capture_id\":" + JsonUtil.String(capture.Id)
                + ",\"status\":\"stopped\""
                + ",\"started_frame\":" + capture.StartedFrame
                + ",\"stopped_frame\":" + Time.frameCount
                + ",\"elapsed_frames\":" + Math.Max(0, Time.frameCount - capture.StartedFrame)
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"query\":" + JsonUtil.String(query)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"matched_objects\":" + matched
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"objects\":[" + objectsJson + "]}";
            WriteEventCaptureArtifact(artifactPath, resultJson);
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "runtime.event_capture.cancel",
            Title = "Cancel runtime event capture",
            Description = "Cooperatively cancel and clean up one runtime event capture session",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = EventCaptureCancelSchemaJson,
            Tags = new string[] { "runtime", "event-capture", "cancel", "recovery" },
            Capability = "runtime_state_debugging",
            ResultShape = "status",
            OutputLimitJson = EventCaptureOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EventCaptureCancel(McpCommandContext context)
        {
            var captureId = JsonUtil.ExtractString(context.ArgsJson, "capture_id");
            if (string.IsNullOrEmpty(captureId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument capture_id is required");
            }

            var reason = JsonUtil.ExtractString(context.ArgsJson, "reason");
            if (reason.Length > 240)
            {
                return CommandExecutionResult.Error("invalid_field_value", "reason must be at most 240 characters");
            }

            EventCapture capture;
            lock (s_captureLock)
            {
                if (!s_eventCaptures.TryGetValue(captureId, out capture))
                {
                    return CommandExecutionResult.Ok("{\"capture_id\":" + JsonUtil.String(captureId)
                        + ",\"status\":\"not_found\""
                        + ",\"cancel_requested\":false"
                        + ",\"cleanup_performed\":false"
                        + ",\"cancellation_pending\":false"
                        + ",\"cancellation_unsupported\":false"
                        + ",\"warning\":\"Capture id was not found; it may have already stopped, been cancelled, or been lost after domain reload.\""
                        + ",\"next_valid_actions\":[\"runtime.event_capture.start\",\"runtime.event_capture.read\",\"artifact.list\"]}");
                }

                s_eventCaptures.Remove(captureId);
            }

            var cancelledAtUtc = DateTime.UtcNow;
            var artifactPath = EventCaptureArtifactRoot + capture.Id + ".json";
            var resultJson = "{\"capture_id\":" + JsonUtil.String(capture.Id)
                + ",\"status\":\"cancelled\""
                + ",\"result\":\"cancelled\""
                + ",\"cancel_requested\":true"
                + ",\"cleanup_performed\":true"
                + ",\"cancellation_pending\":false"
                + ",\"cancellation_unsupported\":false"
                + ",\"unity_main_thread_interrupted\":false"
                + ",\"cancellation_mode\":\"cooperative_session_cleanup\""
                + ",\"started_frame\":" + capture.StartedFrame
                + ",\"cancelled_frame\":" + Time.frameCount
                + ",\"elapsed_frames\":" + Math.Max(0, Time.frameCount - capture.StartedFrame)
                + ",\"started_at_utc\":" + JsonUtil.String(capture.StartedAtUtc.ToString("o"))
                + ",\"cancelled_at_utc\":" + JsonUtil.String(cancelledAtUtc.ToString("o"))
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"artifact_persisted\":true"
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"plan\":" + EventCapturePlanJson(capture.ArgsJson)
                + ",\"warning\":\"runtime.event_capture.cancel cleans up MCP capture tracking only; it does not interrupt Unity main-thread work.\""
                + ",\"next_valid_actions\":[\"runtime.event_capture.start\",\"runtime.event_capture.read\",\"artifact.list\",\"artifact.prune\"]}";
            WriteEventCaptureArtifact(artifactPath, resultJson);
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "runtime.event_capture.read",
            Title = "Read runtime event capture artifact",
            Description = "Read a bounded persisted runtime event capture artifact",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EventCaptureReadSchemaJson,
            Tags = new string[] { "runtime", "event-capture", "artifact" },
            Capability = "runtime_state_debugging",
            ResultShape = "object_details",
            OutputLimitJson = EventCaptureOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EventCaptureRead(McpCommandContext context)
        {
            var hasArtifactPath = !string.IsNullOrEmpty(JsonUtil.ExtractString(context.ArgsJson, "artifact_path"));
            var hasCaptureId = !string.IsNullOrEmpty(JsonUtil.ExtractString(context.ArgsJson, "capture_id"));
            if (!hasArtifactPath && !hasCaptureId)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument capture_id or artifact_path is required");
            }

            var relativePath = ResolveEventCaptureArtifactPath(context.ArgsJson);
            if (string.IsNullOrEmpty(relativePath))
            {
                return CommandExecutionResult.Error("path_not_allowed", "Event capture artifact path must stay under Library/SGGUnityMcp/job-artifacts/runtime-event-captures");
            }

            string fullPath;
            try
            {
                fullPath = RequireProjectLocalFilePath(relativePath, EventCaptureArtifactRoot);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("path_not_allowed", "Event capture artifact path must stay under Library/SGGUnityMcp/job-artifacts/runtime-event-captures");
            }

            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "Event capture artifact was not found");
            }

            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 20000), 1, 200000);
            var content = File.ReadAllText(fullPath);
            var truncated = content.Length > maxChars;
            if (truncated)
            {
                content = content.Substring(0, maxChars);
            }

            return CommandExecutionResult.Ok("{\"artifact_path\":" + JsonUtil.String(relativePath)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"content\":" + JsonUtil.String(content) + "}");
        }

        static EventCapture RequireEventCapture(string argsJson)
        {
            var captureId = JsonUtil.ExtractString(argsJson, "capture_id");
            if (string.IsNullOrEmpty(captureId))
            {
                return null;
            }

            lock (s_captureLock)
            {
                s_eventCaptures.TryGetValue(captureId, out var capture);
                return capture;
            }
        }

        static void WriteEventCaptureArtifact(string relativePath, string content)
        {
            var fullPath = RequireProjectLocalFilePath(relativePath, EventCaptureArtifactRoot);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content, Encoding.UTF8);
        }

        static string ResolveEventCaptureArtifactPath(string argsJson)
        {
            var artifactPath = JsonUtil.ExtractString(argsJson, "artifact_path");
            if (!string.IsNullOrEmpty(artifactPath))
            {
                return NormalizeEventCaptureArtifactPath(artifactPath);
            }

            var captureId = JsonUtil.ExtractString(argsJson, "capture_id");
            if (string.IsNullOrEmpty(captureId) || captureId.IndexOfAny(new[] { '/', '\\', ':', '.' }) >= 0)
            {
                return string.Empty;
            }

            return EventCaptureArtifactRoot + captureId + ".json";
        }

        static string NormalizeEventCaptureArtifactPath(string artifactPath)
        {
            if (artifactPath.IndexOf('\0') >= 0)
            {
                return string.Empty;
            }

            var normalized = artifactPath.Replace('\\', '/');
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal) || IsDriveAbsolute(normalized))
            {
                return string.Empty;
            }

            if (normalized.IndexOf(':') >= 0 || normalized.Contains("../") || normalized == ".." || normalized.Contains("/.."))
            {
                return string.Empty;
            }

            if (normalized.StartsWith(EventCaptureArtifactRoot, StringComparison.Ordinal))
            {
                return normalized;
            }

            if (normalized.StartsWith(JobArtifactRoot, StringComparison.Ordinal))
            {
                return string.Empty;
            }

            if (normalized.StartsWith("runtime-event-captures/", StringComparison.Ordinal))
            {
                return JobArtifactRoot + normalized;
            }

            return EventCaptureArtifactRoot + normalized;
        }

        static string AbsoluteProjectPath(string relativePath)
        {
            return Path.Combine(ProjectPaths.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        static string RequireProjectLocalFilePath(string relativePath, string requiredRoot)
        {
            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var fullRoot = Path.GetFullPath(Path.Combine(projectRoot, requiredRoot.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, fullRoot) || HasReparsePointInExistingChain(fullPath, fullRoot))
            {
                throw new ArgumentException("path_not_allowed");
            }

            return fullPath;
        }

        static bool IsSameOrChildPath(string path, string root)
        {
            var trimmedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), trimmedRoot, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || path.StartsWith(trimmedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static bool HasReparsePointInExistingChain(string path, string root)
        {
            var current = path;
            while (IsSameOrChildPath(current, root))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                var next = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(next) || string.Equals(next, current, StringComparison.Ordinal))
                {
                    return false;
                }

                current = next;
            }

            return false;
        }

        static bool IsDriveAbsolute(string input)
        {
            return input.Length >= 2 && char.IsLetter(input[0]) && input[1] == ':';
        }

        static string EventCapturePlanJson(string argsJson)
        {
            var query = JsonUtil.ExtractString(argsJson, "query");
            var objectPath = NormalizeObjectPath(JsonUtil.ExtractString(argsJson, "object_path"));
            return "{\"query\":" + JsonUtil.String(query)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"max_objects\":" + Clamp(JsonUtil.ExtractInt(argsJson, "max_objects", 50), 1, 200)
                + ",\"include_inactive\":" + JsonUtil.Bool(JsonUtil.ExtractBool(argsJson, "include_inactive", false))
                + ",\"include_components\":" + JsonUtil.Bool(JsonUtil.ExtractBool(argsJson, "include_components", true))
                + ",\"include_transform\":" + JsonUtil.Bool(JsonUtil.ExtractBool(argsJson, "include_transform", true)) + "}";
        }

        static string BuildObjectsJson(string argsJson, int defaultMaxObjects, int maxObjectLimit, out string query, out string objectPath, out int scanned, out bool truncated)
        {
            query = JsonUtil.ExtractString(argsJson, "query");
            objectPath = NormalizeObjectPath(JsonUtil.ExtractString(argsJson, "object_path"));
            var maxObjects = Clamp(JsonUtil.ExtractInt(argsJson, "max_objects", defaultMaxObjects), 1, maxObjectLimit);
            var includeInactive = JsonUtil.ExtractBool(argsJson, "include_inactive", false);
            var includeComponents = JsonUtil.ExtractBool(argsJson, "include_components", true);
            var includeTransform = JsonUtil.ExtractBool(argsJson, "include_transform", true);
            var objects = Resources.FindObjectsOfTypeAll<GameObject>();
            Array.Sort(objects, CompareGameObjectsByPath);

            var builder = new StringBuilder();
            var count = 0;
            scanned = 0;
            truncated = false;
            foreach (var gameObject in objects)
            {
                if (!IsSceneObject(gameObject))
                {
                    continue;
                }

                if (!includeInactive && !gameObject.activeInHierarchy)
                {
                    continue;
                }

                var path = ObjectPath(gameObject);
                if (!string.IsNullOrEmpty(objectPath) && !string.Equals(path, objectPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!string.IsNullOrEmpty(query) && gameObject.name.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0 && path.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    continue;
                }

                scanned++;
                if (count >= maxObjects)
                {
                    truncated = true;
                    break;
                }

                AppendRuntimeObject(builder, gameObject, path, includeComponents, includeTransform, count > 0);
                count++;
            }

            return builder.ToString();
        }

        static string PlanJson(string argsJson)
        {
            var query = JsonUtil.ExtractString(argsJson, "query");
            var objectPath = NormalizeObjectPath(JsonUtil.ExtractString(argsJson, "object_path"));
            return "{\"query\":" + JsonUtil.String(query)
                + ",\"object_path\":" + JsonUtil.String(objectPath)
                + ",\"max_frames\":" + Clamp(JsonUtil.ExtractInt(argsJson, "max_frames", 1), 1, 120)
                + ",\"sample_every_frames\":" + Clamp(JsonUtil.ExtractInt(argsJson, "sample_every_frames", 1), 1, 60)
                + ",\"max_objects\":" + Clamp(JsonUtil.ExtractInt(argsJson, "max_objects", 50), 1, 200)
                + ",\"include_inactive\":" + JsonUtil.Bool(JsonUtil.ExtractBool(argsJson, "include_inactive", false))
                + ",\"include_components\":" + JsonUtil.Bool(JsonUtil.ExtractBool(argsJson, "include_components", true))
                + ",\"include_transform\":" + JsonUtil.Bool(JsonUtil.ExtractBool(argsJson, "include_transform", true))
                + ",\"stop_when_found\":" + JsonUtil.Bool(JsonUtil.ExtractBool(argsJson, "stop_when_found", false)) + "}";
        }

        static void StepPausedFrames(int frames)
        {
            for (var index = 0; index < frames; index++)
            {
                EditorApplication.Step();
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        static void AppendRuntimeObject(StringBuilder builder, GameObject gameObject, string path, bool includeComponents, bool includeTransform, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append('{');
            builder.Append("\"path\":").Append(JsonUtil.String(path));
            builder.Append(",\"name\":").Append(JsonUtil.String(gameObject.name));
            builder.Append(",\"scene\":").Append(JsonUtil.String(gameObject.scene.name));
            builder.Append(",\"tag\":").Append(JsonUtil.String(gameObject.tag));
            builder.Append(",\"layer\":").Append(gameObject.layer);
            builder.Append(",\"active_self\":").Append(JsonUtil.Bool(gameObject.activeSelf));
            builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(gameObject.activeInHierarchy));
            builder.Append(",\"child_count\":").Append(gameObject.transform.childCount);
            if (includeTransform)
            {
                AppendTransform(builder, gameObject.transform);
            }

            if (includeComponents)
            {
                AppendComponents(builder, gameObject);
            }

            builder.Append('}');
        }

        static void AppendTransform(StringBuilder builder, Transform transform)
        {
            builder.Append(",\"transform\":{");
            AppendVector3(builder, "position", transform.position, false);
            AppendVector3(builder, "rotation_euler", transform.rotation.eulerAngles, true);
            AppendVector3(builder, "local_scale", transform.localScale, true);
            builder.Append('}');
        }

        static void AppendVector3(StringBuilder builder, string name, Vector3 value, bool prependComma)
        {
            if (prependComma)
            {
                builder.Append(',');
            }

            builder.Append(JsonUtil.String(name)).Append(':');
            builder.Append("{\"x\":").Append(FloatJson(value.x));
            builder.Append(",\"y\":").Append(FloatJson(value.y));
            builder.Append(",\"z\":").Append(FloatJson(value.z));
            builder.Append('}');
        }

        static void AppendComponents(StringBuilder builder, GameObject gameObject)
        {
            var components = gameObject.GetComponents<Component>();
            builder.Append(",\"components\":[");
            for (var index = 0; index < components.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var component = components[index];
                builder.Append("{\"type\":").Append(JsonUtil.String(component == null ? "MissingScript" : component.GetType().Name)).Append('}');
            }

            builder.Append(']');
        }

        static bool IsSceneObject(GameObject gameObject)
        {
            return gameObject != null && gameObject.scene.IsValid() && gameObject.scene.isLoaded;
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

        static string NormalizeObjectPath(string objectPath)
        {
            if (string.IsNullOrEmpty(objectPath))
            {
                return string.Empty;
            }

            return objectPath.StartsWith("/", StringComparison.Ordinal) ? objectPath : "/" + objectPath;
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? JsonUtil.String(value.ToString()) : value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        static string StringArrayJson(string[] values, int maxItems, int maxChars, out int returned, out bool truncated)
        {
            var items = values ?? Array.Empty<string>();
            var count = Math.Min(items.Length, Math.Max(0, maxItems));
            var builder = new StringBuilder();
            builder.Append('[');
            returned = 0;
            truncated = items.Length > count;
            for (var index = 0; index < count; index++)
            {
                var itemJson = BoundedJsonString(items[index], QualityNameMaxChars, out var itemTruncated);
                var separatorLength = returned > 0 ? 1 : 0;
                if (builder.Length + separatorLength + itemJson.Length + 1 > maxChars)
                {
                    truncated = true;
                    break;
                }
                if (returned > 0) builder.Append(',');
                builder.Append(itemJson);
                returned++;
                truncated = truncated || itemTruncated;
            }
            builder.Append(']');
            return builder.ToString();
        }

        static string BoundedJsonString(string value, int maxChars, out bool truncated)
        {
            var source = value ?? string.Empty;
            var encoded = JsonUtil.String(source);
            if (encoded.Length <= maxChars)
            {
                truncated = false;
                return encoded;
            }

            var length = Math.Min(source.Length, Math.Max(0, maxChars - 2));
            do
            {
                encoded = JsonUtil.String(source.Substring(0, length));
                length--;
            }
            while (encoded.Length > maxChars && length >= 0);
            truncated = true;
            return encoded;
        }

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        sealed class EventCapture
        {
            public string Id = string.Empty;
            public string ArgsJson = "{}";
            public int StartedFrame;
            public DateTime StartedAtUtc;
        }
    }
}
