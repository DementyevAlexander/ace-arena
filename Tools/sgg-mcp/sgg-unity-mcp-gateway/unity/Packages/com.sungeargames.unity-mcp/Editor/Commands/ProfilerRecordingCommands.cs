using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ProfilerRecordingCommands
    {
        const string FeatureFlag = "profiler_recording";
        const string StartSchemaJson = "{\"type\":\"object\",\"properties\":{\"confirm_recording\":{\"type\":\"boolean\"},\"profile_editor\":{\"type\":\"boolean\"}},\"required\":[\"confirm_recording\"],\"additionalProperties\":false}";
        const string StopSchemaJson = "{\"type\":\"object\",\"properties\":{\"reason\":{\"type\":\"string\",\"maxLength\":240}},\"additionalProperties\":false}";
        const string CaptureSaveSchemaJson = "{\"type\":\"object\",\"properties\":{\"confirm_capture_export\":{\"type\":\"boolean\"},\"name\":{\"type\":\"string\",\"maxLength\":80}},\"required\":[\"confirm_capture_export\"],\"additionalProperties\":false}";
        const string RecordingOutputLimitJson = "{\"max_fields\":80,\"implicit_recording\":false}";
        const string CaptureSaveOutputLimitJson = "{\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/profiler-captures\",\"output_path_accepted\":false,\"implicit_recording\":false}";
        const string JobArtifactRoot = "Library/SGGUnityMcp/job-artifacts/";
        const string ProfilerCaptureArtifactRoot = JobArtifactRoot + "profiler-captures/";

        [McpCommand(
            "profiler.recording.start",
            Title = "Start profiler recording",
            Description = "Explicitly start Unity Profiler recording after policy approval and confirm_recording:true",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = StartSchemaJson,
            Tags = new string[] { "profiler", "performance", "recording" },
            Capability = FeatureFlag,
            ResultShape = "status",
            OutputLimitJson = RecordingOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Start(McpCommandContext context)
        {
            if (!JsonUtil.ExtractBool(context.ArgsJson, "confirm_recording", false))
            {
                return CommandExecutionResult.Error("invalid_field_value", "confirm_recording must be true to start Unity Profiler recording");
            }

            var readiness = RequireEditorNotBusy();
            if (readiness != null)
            {
                return readiness;
            }

            var profilerDriver = ResolveType("UnityEditorInternal.ProfilerDriver");
            if (profilerDriver == null)
            {
                return ProfilerDriverUnavailable("profiler.recording.start", "recording_started", false);
            }

            var before = ProfilerStateJson(profilerDriver);
            var changedMembers = new List<string>();
            var warnings = new List<string>();
            var recordingStarted = false;
            if (TryExtractBool(context.ArgsJson, "profile_editor", out var profileEditor))
            {
                if (TrySetStaticBool(profilerDriver, "profileEditor", profileEditor, out var profileEditorMember, out var profileEditorWarning))
                {
                    changedMembers.Add(profileEditorMember);
                }
                else
                {
                    warnings.Add(profileEditorWarning);
                }
            }

            if (TrySetAnyStaticBool(profilerDriver, new string[] { "enabled", "isRecording", "recording", "record" }, true, out var recordingMember, out var recordingWarning))
            {
                changedMembers.Add(recordingMember);
                recordingStarted = true;
            }
            else
            {
                warnings.Add(recordingWarning);
            }

            var after = ProfilerStateJson(profilerDriver);
            return CommandExecutionResult.Ok("{\"status\":\"recording_start_requested\""
                + ",\"api_available\":true"
                + ",\"explicit_request\":true"
                + ",\"confirm_recording\":true"
                + ",\"implicit_recording\":false"
                + ",\"auto_stop\":false"
                + ",\"capture_created\":false"
                + ",\"recording_started\":" + JsonUtil.Bool(recordingStarted)
                + ",\"mutated_members\":" + JsonArray(changedMembers)
                + ",\"warnings\":" + JsonArray(warnings)
                + ",\"before\":" + before
                + ",\"after\":" + after
                + ",\"artifact_refs\":[]"
                + ",\"next_valid_actions\":[\"profiler.status\",\"profiler.recording.stop\",\"profiler.capture.save\"]}");
        }

        [McpCommand(
            "profiler.recording.stop",
            Title = "Stop profiler recording",
            Description = "Explicitly stop Unity Profiler recording after policy approval",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = StopSchemaJson,
            Tags = new string[] { "profiler", "performance", "recording" },
            Capability = FeatureFlag,
            ResultShape = "status",
            OutputLimitJson = RecordingOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Stop(McpCommandContext context)
        {
            var readiness = RequireEditorNotBusy();
            if (readiness != null)
            {
                return readiness;
            }

            var profilerDriver = ResolveType("UnityEditorInternal.ProfilerDriver");
            if (profilerDriver == null)
            {
                return ProfilerDriverUnavailable("profiler.recording.stop", "recording_stopped", false);
            }

            var before = ProfilerStateJson(profilerDriver);
            var changedMembers = new List<string>();
            var warnings = new List<string>();
            if (TrySetAnyStaticBool(profilerDriver, new string[] { "isRecording", "recording", "record", "enabled" }, false, out var recordingMember, out var recordingWarning))
            {
                changedMembers.Add(recordingMember);
            }
            else
            {
                warnings.Add(recordingWarning);
            }

            var after = ProfilerStateJson(profilerDriver);
            return CommandExecutionResult.Ok("{\"status\":\"recording_stop_requested\""
                + ",\"api_available\":true"
                + ",\"explicit_request\":true"
                + ",\"implicit_recording\":false"
                + ",\"capture_created\":false"
                + ",\"recording_stopped\":" + JsonUtil.Bool(changedMembers.Count > 0)
                + ",\"reason\":" + JsonUtil.String(Bounded(JsonUtil.ExtractString(context.ArgsJson, "reason"), 240))
                + ",\"mutated_members\":" + JsonArray(changedMembers)
                + ",\"warnings\":" + JsonArray(warnings)
                + ",\"before\":" + before
                + ",\"after\":" + after
                + ",\"artifact_refs\":[]"
                + ",\"next_valid_actions\":[\"profiler.status\",\"profiler.capture.save\",\"artifact.list\"]}");
        }

        [McpCommand(
            "profiler.capture.save",
            Title = "Save profiler capture",
            Description = "Explicitly export already collected Unity Profiler data to generated project-local artifacts without starting recording",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = CaptureSaveSchemaJson,
            Tags = new string[] { "profiler", "performance", "capture", "artifact" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = CaptureSaveOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult CaptureSave(McpCommandContext context)
        {
            if (!JsonUtil.ExtractBool(context.ArgsJson, "confirm_capture_export", false))
            {
                return CommandExecutionResult.Error("invalid_field_value", "confirm_capture_export must be true to export Unity Profiler data");
            }

            var readiness = RequireEditorNotBusy();
            if (readiness != null)
            {
                return readiness;
            }

            var profilerDriver = ResolveType("UnityEditorInternal.ProfilerDriver");
            if (profilerDriver == null)
            {
                return ProfilerDriverUnavailable("profiler.capture.save", "capture_exported", false);
            }

            var captureId = NewCaptureId();
            var name = Bounded(JsonUtil.ExtractString(context.ArgsJson, "name"), 80);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = captureId;
            }

            var capturePath = ProfilerCaptureArtifactRoot + captureId + ".data";
            var metadataPath = ProfilerCaptureArtifactRoot + captureId + ".json";
            string fullCapturePath;
            try
            {
                fullCapturePath = RequireProjectLocalFilePath(capturePath, ProfilerCaptureArtifactRoot);
                var directory = Path.GetDirectoryName(fullCapturePath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
            }
            catch
            {
                return CommandExecutionResult.Error("path_not_allowed", "Profiler capture artifacts must stay under " + ProfilerCaptureArtifactRoot);
            }

            if (!TryInvokeProfilerSave(profilerDriver, fullCapturePath, out var sourceMember, out var saveError, out var methodFound))
            {
                if (!methodFound)
                {
                    return CommandExecutionResult.Ok("{\"status\":\"capture_export_unavailable\""
                        + ",\"api_available\":false"
                        + ",\"explicit_request\":true"
                        + ",\"confirm_capture_export\":true"
                        + ",\"implicit_recording\":false"
                        + ",\"recording_started\":false"
                        + ",\"recording_stopped\":false"
                        + ",\"capture_exported\":false"
                        + ",\"output_path_accepted\":false"
                        + ",\"reason\":\"No known Unity Profiler save API was detected\""
                        + ",\"artifact_refs\":[]"
                        + ",\"next_valid_actions\":[\"profiler.status\",\"profiler.recording.start\"]}");
                }

                return CommandExecutionResult.Error("unity_exception", saveError);
            }

            var captureExists = File.Exists(fullCapturePath);
            var captureBytes = captureExists ? new FileInfo(fullCapturePath).Length : 0L;
            var exported = captureExists && captureBytes > 0;
            var warning = exported ? string.Empty : "Profiler save API returned but no non-empty capture artifact was created";
            if (!TryWriteCaptureMetadata(metadataPath, captureId, name, capturePath, captureBytes, exported, sourceMember, warning, out var metadataWarning))
            {
                return CommandExecutionResult.Error("unity_exception", metadataWarning);
            }

            var artifactRefs = exported
                ? "[" + JsonUtil.String(capturePath) + "," + JsonUtil.String(metadataPath) + "]"
                : "[" + JsonUtil.String(metadataPath) + "]";
            return CommandExecutionResult.Ok("{\"status\":" + JsonUtil.String(exported ? "capture_exported" : "capture_export_incomplete")
                + ",\"api_available\":true"
                + ",\"explicit_request\":true"
                + ",\"confirm_capture_export\":true"
                + ",\"implicit_recording\":false"
                + ",\"recording_started\":false"
                + ",\"recording_stopped\":false"
                + ",\"capture_exported\":" + JsonUtil.Bool(exported)
                + ",\"capture_id\":" + JsonUtil.String(captureId)
                + ",\"name\":" + JsonUtil.String(name)
                + ",\"source\":" + JsonUtil.String(sourceMember)
                + ",\"output_path_accepted\":false"
                + ",\"capture_artifact_path\":" + JsonUtil.String(exported ? capturePath : string.Empty)
                + ",\"metadata_artifact_path\":" + JsonUtil.String(metadataPath)
                + ",\"bytes\":" + captureBytes
                + ",\"artifact_refs\":" + artifactRefs
                + ",\"readable_with\":\"artifact.metadata\""
                + ",\"metadata_readable_with\":\"job.artifact.read\""
                + ",\"warnings\":" + (string.IsNullOrEmpty(warning) ? "[]" : "[" + JsonUtil.String(warning) + "]")
                + ",\"next_valid_actions\":[\"artifact.metadata\",\"artifact.list\",\"artifact.prune\"]}");
        }

        static CommandExecutionResult RequireEditorNotBusy()
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_compiling", "Profiler recording commands require Unity not to be compiling");
            }

            if (EditorApplication.isUpdating)
            {
                return CommandExecutionResult.Error("unity_busy", "Profiler recording commands require Unity not to be updating assets");
            }

            return null;
        }

        static CommandExecutionResult ProfilerDriverUnavailable(string command, string outcomeField, bool outcomeValue)
        {
            return CommandExecutionResult.Ok("{\"status\":\"profiler_api_unavailable\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"api_available\":false"
                + ",\"explicit_request\":true"
                + ",\"implicit_recording\":false"
                + ",\"" + outcomeField + "\":" + JsonUtil.Bool(outcomeValue)
                + ",\"reason\":\"UnityEditorInternal.ProfilerDriver was not detected in loaded assemblies\""
                + ",\"artifact_refs\":[]"
                + ",\"next_valid_actions\":[\"profiler.status\"]}");
        }

        static string ProfilerStateJson(Type profilerDriver)
        {
            var enabled = TryReadStaticBool(profilerDriver, "enabled", out var enabledValue);
            var recording = TryReadAnyStaticBool(profilerDriver, new string[] { "isRecording", "recording", "record" }, out var recordingValue, out var recordingMember);
            var profileEditor = TryReadStaticBool(profilerDriver, "profileEditor", out var profileEditorValue);
            var json = new StringBuilder();
            json.Append("{\"unity_version\":").Append(JsonUtil.String(Application.unityVersion));
            json.Append(",\"is_playing\":").Append(JsonUtil.Bool(EditorApplication.isPlaying));
            json.Append(",\"is_paused\":").Append(JsonUtil.Bool(EditorApplication.isPaused));
            json.Append(",\"profiler\":{");
            AppendBoolStatus(json, "enabled", enabled, enabledValue, "enabled");
            json.Append(',');
            AppendBoolStatus(json, "recording", recording, recordingValue, recordingMember);
            json.Append(',');
            AppendBoolStatus(json, "profile_editor", profileEditor, profileEditorValue, "profileEditor");
            json.Append("}}");
            return json.ToString();
        }

        static bool TryInvokeProfilerSave(Type profilerDriver, string fullCapturePath, out string sourceMember, out string error, out bool methodFound)
        {
            sourceMember = string.Empty;
            error = string.Empty;
            methodFound = false;
            foreach (var methodName in new string[] { "SaveProfile", "SaveProfilerData", "SaveRawFrameData" })
            {
                var method = profilerDriver.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, new Type[] { typeof(string) }, null);
                if (method == null)
                {
                    continue;
                }

                methodFound = true;
                sourceMember = profilerDriver.FullName + "." + methodName;
                try
                {
                    method.Invoke(null, new object[] { fullCapturePath });
                    return true;
                }
                catch (TargetInvocationException exception)
                {
                    error = "Profiler capture export failed: " + (exception.InnerException == null ? exception.GetType().Name : exception.InnerException.GetType().Name);
                    return false;
                }
                catch (Exception exception)
                {
                    error = "Profiler capture export failed: " + exception.GetType().Name;
                    return false;
                }
            }

            error = "No known Unity Profiler save API was detected";
            return false;
        }

        static bool TryWriteCaptureMetadata(string metadataPath, string captureId, string name, string capturePath, long captureBytes, bool exported, string sourceMember, string warning, out string error)
        {
            error = string.Empty;
            try
            {
                var fullMetadataPath = RequireProjectLocalFilePath(metadataPath, ProfilerCaptureArtifactRoot);
                var directory = Path.GetDirectoryName(fullMetadataPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullMetadataPath, CaptureMetadataJson(captureId, name, capturePath, captureBytes, exported, sourceMember, warning), new UTF8Encoding(false));
                return true;
            }
            catch (Exception exception)
            {
                error = "Profiler capture metadata write failed: " + exception.GetType().Name;
                return false;
            }
        }

        static string CaptureMetadataJson(string captureId, string name, string capturePath, long captureBytes, bool exported, string sourceMember, string warning)
        {
            var createdUtc = DateTime.UtcNow;
            return "{\"artifact_schema_version\":1"
                + ",\"command\":\"profiler.capture.save\""
                + ",\"status\":" + JsonUtil.String(exported ? "capture_exported" : "capture_export_incomplete")
                + ",\"capture_id\":" + JsonUtil.String(captureId)
                + ",\"name\":" + JsonUtil.String(name)
                + ",\"created_at\":" + JsonUtil.String(createdUtc.ToString("o"))
                + ",\"created_utc\":" + JsonUtil.String(createdUtc.ToString("o"))
                + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                + ",\"package_version\":" + JsonUtil.String(UnityBridgeRuntime.PackageVersion)
                + ",\"storage\":\"project_artifact\""
                + ",\"content_type\":\"application/vnd.unity.profiler\""
                + ",\"source\":" + JsonUtil.String(sourceMember)
                + ",\"implicit_recording\":false"
                + ",\"recording_started\":false"
                + ",\"recording_stopped\":false"
                + ",\"capture_exported\":" + JsonUtil.Bool(exported)
                + ",\"capture_artifact_path\":" + JsonUtil.String(exported ? capturePath : string.Empty)
                + ",\"bytes\":" + Math.Max(0L, captureBytes)
                + ",\"warning\":" + JsonUtil.String(warning) + "}";
        }

        static string NewCaptureId()
        {
            return "profiler_capture_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static Type ResolveType(string fullName)
        {
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                Type type = null;
                try
                {
                    type = assembly.GetType(fullName, false);
                }
                catch
                {
                    type = null;
                }

                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        static bool TryExtractBool(string json, string property, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property))
            {
                return false;
            }

            var pattern = "\"" + property + "\"";
            var propertyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return false;
            }

            var colon = json.IndexOf(':', propertyIndex + pattern.Length);
            if (colon < 0)
            {
                return false;
            }

            var index = colon + 1;
            while (index < json.Length && char.IsWhiteSpace(json[index]))
            {
                index++;
            }

            if (json.IndexOf("true", index, StringComparison.OrdinalIgnoreCase) == index)
            {
                value = true;
                return true;
            }

            if (json.IndexOf("false", index, StringComparison.OrdinalIgnoreCase) == index)
            {
                value = false;
                return true;
            }

            return false;
        }

        static bool TrySetAnyStaticBool(Type type, string[] names, bool newValue, out string memberName, out string warning)
        {
            foreach (var name in names)
            {
                if (TrySetStaticBool(type, name, newValue, out memberName, out warning))
                {
                    return true;
                }
            }

            memberName = string.Empty;
            warning = "No known settable profiler recording boolean was detected";
            return false;
        }

        static bool TrySetStaticBool(Type type, string name, bool newValue, out string memberName, out string warning)
        {
            memberName = string.Empty;
            warning = string.Empty;
            if (type == null)
            {
                warning = "ProfilerDriver API was not detected";
                return false;
            }

            try
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0 && property.CanWrite)
                {
                    property.SetValue(null, newValue, null);
                    memberName = type.FullName + "." + name;
                    return true;
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null && field.FieldType == typeof(bool) && !field.IsInitOnly)
                {
                    field.SetValue(null, newValue);
                    memberName = type.FullName + "." + name;
                    return true;
                }
            }
            catch (Exception exception)
            {
                warning = "Failed to set " + name + ": " + exception.GetType().Name;
                return false;
            }

            warning = "ProfilerDriver." + name + " is not settable";
            return false;
        }

        static bool TryReadAnyStaticBool(Type type, string[] names, out bool value, out string memberName)
        {
            foreach (var name in names)
            {
                if (TryReadStaticBool(type, name, out value))
                {
                    memberName = name;
                    return true;
                }
            }

            value = false;
            memberName = string.Empty;
            return false;
        }

        static bool TryReadStaticBool(Type type, string name, out bool value)
        {
            value = false;
            if (type == null)
            {
                return false;
            }

            try
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null && property.PropertyType == typeof(bool) && property.GetIndexParameters().Length == 0)
                {
                    value = (bool)property.GetValue(null, null);
                    return true;
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null && field.FieldType == typeof(bool))
                {
                    value = (bool)field.GetValue(null);
                    return true;
                }
            }
            catch
            {
                value = false;
            }

            return false;
        }

        static void AppendBoolStatus(StringBuilder json, string name, bool readable, bool value, string sourceMember)
        {
            json.Append(JsonUtil.String(name)).Append(":{");
            json.Append("\"readable\":").Append(JsonUtil.Bool(readable));
            json.Append(",\"value\":").Append(readable ? JsonUtil.Bool(value) : "null");
            json.Append(",\"source\":").Append(JsonUtil.String(readable ? sourceMember : string.Empty));
            json.Append('}');
        }

        static string JsonArray(List<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(values[index]));
            }

            builder.Append(']');
            return builder.ToString();
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

        static string Bounded(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxLength ? value : value.Substring(0, maxLength);
        }
    }
}
