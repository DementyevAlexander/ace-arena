using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class ProfilerReadonlyCommands
    {
        const string FeatureFlag = "profiler_readonly";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string ModulesSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_modules\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"additionalProperties\":false}";
        const string FrameSummarySchemaJson = "{\"type\":\"object\",\"properties\":{\"max_threads\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16},\"max_markers\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string FrameMarkersSchemaJson = "{\"type\":\"object\",\"properties\":{\"frame_index\":{\"type\":\"integer\",\"minimum\":0},\"thread\":{\"type\":\"string\",\"maxLength\":160},\"max_threads\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16},\"max_markers\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"include_children\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string MarkerSearchSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":120},\"frame_index\":{\"type\":\"integer\",\"minimum\":0},\"case_sensitive\":{\"type\":\"boolean\"},\"max_threads\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"include_children\":{\"type\":\"boolean\"}},\"required\":[\"query\"],\"additionalProperties\":false}";
        const string ReportCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"name\":{\"type\":\"string\",\"maxLength\":80},\"frame_index\":{\"type\":\"integer\",\"minimum\":0},\"max_threads\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":16},\"max_markers\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_chars\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":65536},\"include_children\":{\"type\":\"boolean\"}},\"additionalProperties\":false}";
        const string ReportReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"report_id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"max_chars\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":65536}},\"required\":[\"report_id\"],\"additionalProperties\":false}";
        const string MemorySnapshotCompareSchemaJson = "{\"type\":\"object\",\"properties\":{\"left_artifact_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"right_artifact_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"max_items\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"required\":[\"left_artifact_path\",\"right_artifact_path\"],\"additionalProperties\":false}";
        const string MemorySnapshotListSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_snapshots\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"max_scan_files\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_metric_names\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100}},\"additionalProperties\":false}";
        const string SessionListSchemaJson = "{\"type\":\"object\",\"properties\":{\"include_process_memory\":{\"type\":\"boolean\"},\"include_project_artifacts\":{\"type\":\"boolean\"},\"max_sessions\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100},\"max_scan_files\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"additionalProperties\":false}";
        const string SessionSummarySchemaJson = "{\"type\":\"object\",\"properties\":{\"session_id\":{\"type\":\"string\",\"maxLength\":120},\"artifact_path\":{\"type\":\"string\",\"maxLength\":512},\"include_report_preview\":{\"type\":\"boolean\"},\"max_report_chars\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":8192}},\"additionalProperties\":false}";
        const string StatusOutputLimitJson = "{\"max_fields\":40}";
        const string ModulesOutputLimitJson = "{\"max_modules\":100}";
        const string FrameSummaryOutputLimitJson = "{\"max_threads\":16,\"max_markers\":200,\"max_marker_name_chars\":160}";
        const string FrameMarkersOutputLimitJson = "{\"max_threads\":16,\"max_markers\":1000,\"max_marker_name_chars\":160}";
        const string MarkerSearchOutputLimitJson = "{\"max_results\":200,\"max_threads\":16,\"max_marker_name_chars\":160}";
        const string ReportOutputLimitJson = "{\"max_chars\":65536,\"max_cached_reports\":16,\"storage\":\"process_memory_and_project_artifact\",\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/profiler-reports\"}";
        const string MemorySnapshotCompareOutputLimitJson = "{\"max_items\":100,\"max_input_bytes\":4194304,\"storage\":\"project_artifact\",\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/profiler-reports\"}";
        const string MemorySnapshotListOutputLimitJson = "{\"max_snapshots\":100,\"max_scan_files\":5000,\"max_metric_names\":100,\"max_input_bytes\":4194304,\"storage\":\"project_artifact\",\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts\"}";
        const string SessionOutputLimitJson = "{\"max_sessions\":100,\"max_scan_files\":5000,\"max_report_chars\":8192,\"storage\":\"process_memory_and_project_artifact\",\"artifact_roots\":[\"Library/SGGUnityMcp/job-artifacts/profiler-reports\",\"Library/SGGUnityMcp/job-artifacts/profiler-captures\"]}";
        const string JobArtifactRoot = "Library/SGGUnityMcp/job-artifacts/";
        const string ProfilerReportArtifactRoot = JobArtifactRoot + "profiler-reports/";
        const string ProfilerCaptureArtifactRoot = JobArtifactRoot + "profiler-captures/";
        const int MaxCachedProfilerReports = 16;
        const int ProfilerReportTtlMinutes = 30;
        const int MaxMemorySnapshotArtifactBytes = 4 * 1024 * 1024;
        const int MaxMemorySnapshotListItems = 100;
        const int MaxMemorySnapshotScanFiles = 5000;
        const int MaxProfilerSessions = 100;
        const int MaxProfilerSessionScanFiles = 5000;
        const int MaxProfilerSessionSummaryChars = 8192;

        static readonly object ReportCacheLock = new object();
        static readonly Dictionary<string, ProfilerReportSnapshot> ReportCache = new Dictionary<string, ProfilerReportSnapshot>();
        static readonly Queue<string> ReportCacheOrder = new Queue<string>();
        static readonly string[] MemorySnapshotNumericFields = new string[]
        {
            "total_size",
            "total_bytes",
            "used_bytes",
            "reserved_bytes",
            "gc_used_memory",
            "gc_reserved_memory",
            "mono_used_size",
            "mono_heap_size",
            "native_used_size",
            "native_reserved_size",
            "managed_heap_used_size",
            "managed_heap_reserved_size",
            "graphics_memory",
            "object_count",
            "native_object_count",
            "managed_object_count",
            "texture_count",
            "mesh_count",
            "material_count",
            "animation_clip_count",
            "audio_clip_count",
            "game_object_count",
            "component_count"
        };

        [McpCommand(
            "profiler.status",
            Title = "Profiler status",
            Description = "Read bounded Unity Profiler API availability and current safe status hints without recording captures",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "profiler", "performance" },
            Capability = "profiler_readonly",
            ResultShape = "status",
            OutputLimitJson = StatusOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Status()
        {
            var profilerDriver = ResolveType("UnityEditorInternal.ProfilerDriver");
            var profilerModule = ResolveType("UnityEditor.Profiling.ProfilerModule");
            var apiAvailable = profilerDriver != null || profilerModule != null;
            var enabled = TryReadStaticBool(profilerDriver, "enabled", out var enabledValue);
            var recording = TryReadAnyStaticBool(profilerDriver, new string[] { "isRecording", "recording", "record" }, out var recordingValue, out var recordingMember);
            var deepProfiling = TryReadAnyStaticBool(profilerDriver, new string[] { "deepProfiling", "deepProfilingEnabled" }, out var deepProfilingValue, out var deepProfilingMember);
            var profileEditor = TryReadAnyStaticBool(profilerDriver, new string[] { "profileEditor" }, out var profileEditorValue, out var profileEditorMember);

            var json = new StringBuilder();
            json.Append("{\"api_available\":").Append(JsonUtil.Bool(apiAvailable));
            json.Append(",\"unity_version\":").Append(JsonUtil.String(Application.unityVersion));
            json.Append(",\"editor\":{");
            json.Append("\"is_playing\":").Append(JsonUtil.Bool(EditorApplication.isPlaying));
            json.Append(",\"is_playing_or_will_change_playmode\":").Append(JsonUtil.Bool(EditorApplication.isPlayingOrWillChangePlaymode));
            json.Append(",\"is_paused\":").Append(JsonUtil.Bool(EditorApplication.isPaused));
            json.Append('}');
            json.Append(",\"types\":{");
            json.Append("\"profiler_driver\":").Append(JsonUtil.Bool(profilerDriver != null));
            json.Append(",\"profiler_module\":").Append(JsonUtil.Bool(profilerModule != null));
            json.Append('}');
            json.Append(",\"profiler\":{");
            AppendBoolStatus(json, "enabled", enabled, enabledValue, "enabled");
            json.Append(',');
            AppendBoolStatus(json, "recording", recording, recordingValue, recordingMember);
            json.Append(',');
            AppendBoolStatus(json, "deep_profiling", deepProfiling, deepProfilingValue, deepProfilingMember);
            json.Append(',');
            AppendBoolStatus(json, "profile_editor", profileEditor, profileEditorValue, profileEditorMember);
            json.Append('}');
            if (!apiAvailable)
            {
                json.Append(",\"reason\":\"Unity profiler editor APIs were not detected in loaded assemblies\"");
            }

            json.Append('}');
            return CommandExecutionResult.Ok(json.ToString());
        }

        [McpCommand(
            "profiler.modules",
            Title = "Profiler modules",
            Description = "Read a bounded list of Unity Profiler modules when a stable read-only API is discoverable",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ModulesSchemaJson,
            Tags = new string[] { "profiler", "performance" },
            Capability = "profiler_readonly",
            ResultShape = "bounded_list",
            OutputLimitJson = ModulesOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Modules(McpCommandContext context)
        {
            var maxModules = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_modules", 50), 1, 100);
            var profilerDriver = ResolveType("UnityEditorInternal.ProfilerDriver");
            var moduleNames = TryReadProfilerModuleNames(profilerDriver, out var sourceMember, out var reason);
            if (moduleNames == null)
            {
                return CommandExecutionResult.Ok("{\"api_available\":false,\"max_modules\":" + maxModules
                    + ",\"modules\":[],\"truncated\":false,\"reason\":" + JsonUtil.String(reason) + "}");
            }

            moduleNames.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            var count = Math.Min(maxModules, moduleNames.Count);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"name\":").Append(JsonUtil.String(moduleNames[index])).Append('}');
            }

            return CommandExecutionResult.Ok("{\"api_available\":true,\"source\":" + JsonUtil.String(sourceMember)
                + ",\"max_modules\":" + maxModules
                + ",\"matched\":" + moduleNames.Count
                + ",\"truncated\":" + JsonUtil.Bool(moduleNames.Count > count)
                + ",\"modules\":[" + builder + "]}");
        }

        [McpCommand(
            "profiler.frame.summary",
            Title = "Profiler frame summary",
            Description = "Read a bounded summary of already available Unity Profiler frame data without recording or captures",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = FrameSummarySchemaJson,
            Tags = new string[] { "profiler", "performance" },
            Capability = "profiler_readonly",
            ResultShape = "bounded_list",
            OutputLimitJson = FrameSummaryOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult FrameSummary(McpCommandContext context)
        {
            var maxThreads = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_threads", 8), 1, 16);
            var maxMarkers = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_markers", 50), 1, 200);
            var result = ReadProfilerFrameMarkers(-1, maxThreads, maxMarkers, string.Empty, true, string.Empty, false);
            return CommandExecutionResult.Ok(FrameMarkersJson(result, string.Empty, true));
        }

        [McpCommand(
            "profiler.frame.markers",
            Title = "Profiler frame markers",
            Description = "Read bounded marker timing rows from an already available Unity Profiler frame without recording",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = FrameMarkersSchemaJson,
            Tags = new string[] { "profiler", "performance", "markers" },
            Capability = "profiler_readonly",
            ResultShape = "bounded_list",
            OutputLimitJson = FrameMarkersOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult FrameMarkers(McpCommandContext context)
        {
            var requestedFrameIndex = JsonUtil.ExtractInt(context.ArgsJson, "frame_index", -1);
            var threadFilter = JsonUtil.ExtractString(context.ArgsJson, "thread");
            var maxThreads = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_threads", 8), 1, 16);
            var maxMarkers = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_markers", 200), 1, 1000);
            var includeChildren = JsonUtil.ExtractBool(context.ArgsJson, "include_children", true);
            var result = ReadProfilerFrameMarkers(requestedFrameIndex, maxThreads, maxMarkers, threadFilter, includeChildren, string.Empty, false);
            return CommandExecutionResult.Ok(FrameMarkersJson(result, threadFilter, includeChildren));
        }

        [McpCommand(
            "profiler.marker.search",
            Title = "Profiler marker search",
            Description = "Search bounded marker names in already available Unity Profiler frame data without recording",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = MarkerSearchSchemaJson,
            Tags = new string[] { "profiler", "performance", "markers", "search" },
            Capability = "profiler_readonly",
            ResultShape = "bounded_list",
            OutputLimitJson = MarkerSearchOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MarkerSearch(McpCommandContext context)
        {
            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            if (string.IsNullOrWhiteSpace(query))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument query is required");
            }

            var requestedFrameIndex = JsonUtil.ExtractInt(context.ArgsJson, "frame_index", -1);
            var caseSensitive = JsonUtil.ExtractBool(context.ArgsJson, "case_sensitive", false);
            var maxThreads = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_threads", 8), 1, 16);
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 200);
            var includeChildren = JsonUtil.ExtractBool(context.ArgsJson, "include_children", true);
            var result = ReadProfilerFrameMarkers(requestedFrameIndex, maxThreads, maxResults, string.Empty, includeChildren, query, caseSensitive);
            return CommandExecutionResult.Ok(SearchProfilerMarkersJson(result, query, caseSensitive, includeChildren));
        }

        [McpCommand(
            "profiler.report.create",
            Title = "Profiler report create",
            Description = "Create a bounded process-memory and project-local artifact report from already available Unity Profiler frame data without captures",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReportCreateSchemaJson,
            Tags = new string[] { "profiler", "performance", "report" },
            Capability = "profiler_readonly",
            ResultShape = "object_details",
            OutputLimitJson = ReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ReportCreate(McpCommandContext context)
        {
            var name = JsonUtil.ExtractString(context.ArgsJson, "name");
            if (string.IsNullOrWhiteSpace(name))
            {
                name = "Unity Profiler frame report";
            }

            name = Bounded(name.Trim(), 80);
            var requestedFrameIndex = JsonUtil.ExtractInt(context.ArgsJson, "frame_index", -1);
            var maxThreads = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_threads", 8), 1, 16);
            var maxMarkers = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_markers", 200), 1, 1000);
            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 32768), 1024, 65536);
            var includeChildren = JsonUtil.ExtractBool(context.ArgsJson, "include_children", true);
            var frame = ReadProfilerFrameMarkers(requestedFrameIndex, maxThreads, maxMarkers, string.Empty, includeChildren, string.Empty, false);
            var report = BuildProfilerReportMarkdown(name, frame, maxChars, out var reportTruncated);
            var now = DateTime.UtcNow;
            var expiresAt = now.AddMinutes(ProfilerReportTtlMinutes);
            var reportId = NewProfilerReportId();
            var summary = ProfilerReportSummary(frame);
            var artifactPath = ProfilerReportArtifactPath(reportId);
            var artifactPersisted = TryWriteProfilerReportArtifact(artifactPath, reportId, name, summary, report, now, expiresAt, reportTruncated, frame, out var artifactWarning);
            var resultState = ProfilerFrameReadPolicy.ResultState(frame.SelectionState, frame.Truncated, frame.MatchedMarkers);
            var snapshot = new ProfilerReportSnapshot(reportId, name, summary, report, artifactPath, artifactPersisted, now, expiresAt, reportTruncated, frame.ApiAvailable, frame.DataAvailable, frame.SelectionState, resultState, frame.FrameIndex);
            StoreProfilerReport(snapshot);
            var preview = Bounded(report, Math.Min(maxChars, 4096));
            return CommandExecutionResult.Ok("{\"status\":" + JsonUtil.String(frame.DataAvailable ? "ok" : "frame_unavailable")
                + ",\"api_available\":" + JsonUtil.Bool(frame.ApiAvailable)
                + ",\"result_state\":" + JsonUtil.String(resultState)
                + ",\"data_available\":" + JsonUtil.Bool(frame.DataAvailable)
                + ",\"range_available\":" + JsonUtil.Bool(frame.RangeAvailable)
                + ",\"first_frame_index\":" + (frame.RangeAvailable ? frame.FirstFrameIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"last_frame_index\":" + (frame.RangeAvailable ? frame.LastFrameIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"thread_count\":" + (frame.ThreadCountAvailable ? frame.ThreadCount.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"name\":" + JsonUtil.String(name)
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"frame_index\":" + frame.FrameIndex
                + ",\"created_utc\":" + JsonUtil.String(now.ToString("o"))
                + ",\"expires_at_utc\":" + JsonUtil.String(expiresAt.ToString("o"))
                + ",\"process_memory_expires_at_utc\":" + JsonUtil.String(expiresAt.ToString("o"))
                + ",\"expires_in_memory_only\":false"
                + ",\"storage\":\"process_memory_and_project_artifact\""
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"artifact_persisted\":" + JsonUtil.Bool(artifactPersisted)
                + ",\"artifact_refs\":" + (artifactPersisted ? "[" + JsonUtil.String(artifactPath) + "]" : "[]")
                + ",\"readable_with\":\"job.artifact.read\""
                + ",\"max_chars\":" + maxChars
                + ",\"truncated\":" + JsonUtil.Bool(reportTruncated || report.Length > preview.Length)
                + ",\"report_preview\":" + JsonUtil.String(preview)
                + (string.IsNullOrEmpty(artifactWarning) ? string.Empty : ",\"artifact_warning\":" + JsonUtil.String(artifactWarning))
                + ",\"next_valid_actions\":[\"profiler.report.read\",\"job.artifact.read\",\"artifact.metadata\",\"artifact.prune\",\"profiler.frame.markers\",\"profiler.marker.search\"]}");
        }

        [McpCommand(
            "profiler.report.read",
            Title = "Profiler report read",
            Description = "Read a bounded process-memory or project-local profiler report created by profiler.report.create",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReportReadSchemaJson,
            Tags = new string[] { "profiler", "performance", "report" },
            Capability = "profiler_readonly",
            ResultShape = "text_artifact",
            OutputLimitJson = ReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ReportRead(McpCommandContext context)
        {
            var reportId = JsonUtil.ExtractString(context.ArgsJson, "report_id");
            if (string.IsNullOrWhiteSpace(reportId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument report_id is required");
            }

            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 65536), 1024, 65536);
            var snapshot = FindProfilerReport(reportId, DateTime.UtcNow);
            if (snapshot == null)
            {
                if (TryReadProfilerReportArtifact(reportId, maxChars, out var artifactResponse))
                {
                    return CommandExecutionResult.Ok(artifactResponse);
                }

                var artifactPath = ProfilerReportArtifactPath(reportId);
                return CommandExecutionResult.Ok("{\"status\":\"not_found\""
                    + ",\"report_id\":" + JsonUtil.String(reportId)
                    + ",\"storage\":\"process_memory_and_project_artifact\""
                    + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                    + ",\"artifact_refs\":[]"
                    + ",\"reason\":\"Profiler report was not found in process memory or project-local profiler report artifacts\"}");
            }

            var report = Bounded(snapshot.Report, maxChars);
            return CommandExecutionResult.Ok("{\"status\":" + JsonUtil.String(snapshot.DataAvailable ? "ok" : "frame_unavailable")
                + ",\"report_id\":" + JsonUtil.String(snapshot.ReportId)
                + ",\"name\":" + JsonUtil.String(snapshot.Name)
                + ",\"summary\":" + JsonUtil.String(snapshot.Summary)
                + ",\"api_available\":" + JsonUtil.Bool(snapshot.ApiAvailable)
                + ",\"selection_state\":" + JsonUtil.String(snapshot.SelectionState)
                + ",\"result_state\":" + JsonUtil.String(snapshot.ResultState)
                + ",\"data_available\":" + JsonUtil.Bool(snapshot.DataAvailable)
                + ",\"frame_index\":" + snapshot.FrameIndex
                + ",\"created_utc\":" + JsonUtil.String(snapshot.CreatedUtc.ToString("o"))
                + ",\"expires_at_utc\":" + JsonUtil.String(snapshot.ExpiresAtUtc.ToString("o"))
                + ",\"process_memory_expires_at_utc\":" + JsonUtil.String(snapshot.ExpiresAtUtc.ToString("o"))
                + ",\"storage\":\"process_memory_and_project_artifact\""
                + ",\"artifact_path\":" + JsonUtil.String(snapshot.ArtifactPath)
                + ",\"artifact_persisted\":" + JsonUtil.Bool(snapshot.ArtifactPersisted)
                + ",\"artifact_refs\":" + (snapshot.ArtifactPersisted ? "[" + JsonUtil.String(snapshot.ArtifactPath) + "]" : "[]")
                + ",\"readable_with\":\"job.artifact.read\""
                + ",\"content_type\":\"text/markdown\""
                + ",\"max_chars\":" + maxChars
                + ",\"truncated\":" + JsonUtil.Bool(snapshot.Truncated || snapshot.Report.Length > report.Length)
                + ",\"report\":" + JsonUtil.String(report) + "}");
        }

        [McpCommand(
            "profiler.memory.snapshot.compare",
            Title = "Profiler memory snapshot compare",
            Description = "Compare bounded numeric summaries from two existing project-local profiler memory snapshot JSON artifacts without capturing new snapshots",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 15000,
            SchemaJson = MemorySnapshotCompareSchemaJson,
            Tags = new string[] { "profiler", "performance", "memory", "snapshot", "compare" },
            Capability = "profiler_readonly",
            ResultShape = "object_details",
            OutputLimitJson = MemorySnapshotCompareOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MemorySnapshotCompare(McpCommandContext context)
        {
            var leftPath = JsonUtil.ExtractString(context.ArgsJson, "left_artifact_path").Trim();
            var rightPath = JsonUtil.ExtractString(context.ArgsJson, "right_artifact_path").Trim();
            var maxItems = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_items", 50), 1, 100);

            CommandExecutionResult loadError;
            MemorySnapshotArtifact left;
            MemorySnapshotArtifact right;
            if (!TryLoadMemorySnapshotArtifact(leftPath, "left_artifact_path", out left, out loadError))
            {
                return loadError;
            }

            if (!TryLoadMemorySnapshotArtifact(rightPath, "right_artifact_path", out right, out loadError))
            {
                return loadError;
            }

            var metrics = CompareMemorySnapshotArtifacts(left, right);
            var compareId = NewMemoryCompareId();
            var createdUtc = DateTime.UtcNow;
            var inlineMetrics = MetricsJson(metrics, maxItems, out var includedMetrics, out var changedMetrics, out var truncated);
            var artifactPath = MemoryCompareArtifactPath(compareId);
            var artifactPersisted = TryWriteMemoryCompareArtifact(artifactPath, compareId, createdUtc, left, right, metrics, out var artifactWarning);

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"comparison_id\":" + JsonUtil.String(compareId)
                + ",\"created_utc\":" + JsonUtil.String(createdUtc.ToString("o"))
                + ",\"left\":" + MemorySnapshotArtifactSummaryJson(left)
                + ",\"right\":" + MemorySnapshotArtifactSummaryJson(right)
                + ",\"same_content\":" + JsonUtil.Bool(left.ContentHash == right.ContentHash)
                + ",\"fields_compared\":" + metrics.Count
                + ",\"changed_fields\":" + changedMetrics
                + ",\"included_fields\":" + includedMetrics
                + ",\"max_items\":" + maxItems
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"metrics\":[" + inlineMetrics + "]"
                + ",\"storage\":\"project_artifact\""
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"artifact_persisted\":" + JsonUtil.Bool(artifactPersisted)
                + ",\"artifact_refs\":" + (artifactPersisted ? "[" + JsonUtil.String(artifactPath) + "]" : "[]")
                + ",\"readable_with\":\"job.artifact.read\""
                + (string.IsNullOrEmpty(artifactWarning) ? string.Empty : ",\"artifact_warning\":" + JsonUtil.String(artifactWarning))
                + ",\"capture_created\":false"
                + ",\"next_valid_actions\":[\"job.artifact.read\",\"artifact.metadata\",\"artifact.list\",\"artifact.prune\"]}");
        }

        [McpCommand(
            "profiler.memory.snapshot.list",
            Title = "Profiler memory snapshot list",
            Description = "List bounded project-local JSON artifacts that expose known memory snapshot counters without capturing new snapshots",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = MemorySnapshotListSchemaJson,
            Tags = new string[] { "profiler", "performance", "memory", "snapshot", "artifact" },
            Capability = "profiler_readonly",
            ResultShape = "bounded_list",
            OutputLimitJson = MemorySnapshotListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult MemorySnapshotList(McpCommandContext context)
        {
            var maxSnapshots = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_snapshots", 50), 1, MaxMemorySnapshotListItems);
            var maxScanFiles = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_files", 1000), 1, MaxMemorySnapshotScanFiles);
            var maxMetricNames = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_metric_names", 32), 0, MemorySnapshotNumericFields.Length);
            var candidates = new List<MemorySnapshotCandidate>();
            var scannedFiles = 0;
            var skippedFiles = 0;
            var scanTruncated = false;
            AddMemorySnapshotCandidates(candidates, maxScanFiles, maxMetricNames, ref scannedFiles, ref skippedFiles, ref scanTruncated);
            candidates.Sort(CompareMemorySnapshotCandidates);
            var returned = Math.Min(maxSnapshots, candidates.Count);
            var snapshotsJson = new StringBuilder();
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    snapshotsJson.Append(',');
                }

                snapshotsJson.Append(candidates[index].ToJson());
            }

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"artifact_root\":" + JsonUtil.String(JobArtifactRoot)
                + ",\"max_snapshots\":" + maxSnapshots
                + ",\"max_scan_files\":" + maxScanFiles
                + ",\"max_metric_names\":" + maxMetricNames
                + ",\"scanned_file_count\":" + scannedFiles
                + ",\"skipped_file_count\":" + skippedFiles
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"matched_snapshot_count\":" + candidates.Count
                + ",\"returned_snapshot_count\":" + returned
                + ",\"snapshots_truncated\":" + JsonUtil.Bool(candidates.Count > returned)
                + ",\"snapshots\":[" + snapshotsJson + "]"
                + ",\"values_returned\":false"
                + ",\"capture_created\":false"
                + ",\"recording_started\":false"
                + ",\"next_valid_actions\":[\"profiler.memory.snapshot.compare\",\"job.artifact.read\",\"artifact.metadata\",\"artifact.list\",\"artifact.prune\"]}");
        }

        [McpCommand(
            "profiler.session.list",
            Title = "Profiler session list",
            Description = "List bounded process-memory and project-local profiler report/capture sessions without recording or reading arbitrary files",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SessionListSchemaJson,
            Tags = new string[] { "profiler", "performance", "session", "artifact" },
            Capability = "profiler_readonly",
            ResultShape = "bounded_list",
            OutputLimitJson = SessionOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SessionList(McpCommandContext context)
        {
            var includeProcessMemory = JsonUtil.ExtractBool(context.ArgsJson, "include_process_memory", true);
            var includeProjectArtifacts = JsonUtil.ExtractBool(context.ArgsJson, "include_project_artifacts", true);
            var maxSessions = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_sessions", 50), 1, MaxProfilerSessions);
            var maxScanFiles = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_files", 1000), 1, MaxProfilerSessionScanFiles);
            var sessions = new List<ProfilerSessionDescriptor>();
            if (includeProcessMemory)
            {
                AddProfilerProcessMemorySessions(sessions, DateTime.UtcNow);
            }

            var scannedFiles = 0;
            var skippedFiles = 0;
            var scanTruncated = false;
            if (includeProjectArtifacts)
            {
                AddProjectProfilerSessions(ProfilerReportArtifactRoot, "profiler_report", maxScanFiles, sessions, ref scannedFiles, ref skippedFiles, ref scanTruncated);
                AddProjectProfilerSessions(ProfilerCaptureArtifactRoot, "profiler_capture", maxScanFiles, sessions, ref scannedFiles, ref skippedFiles, ref scanTruncated);
            }

            sessions.Sort(CompareProfilerSessions);
            var returned = Math.Min(maxSessions, sessions.Count);
            var json = new StringBuilder();
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    json.Append(',');
                }

                json.Append(sessions[index].ToJson(false));
            }

            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"include_process_memory\":" + JsonUtil.Bool(includeProcessMemory)
                + ",\"include_project_artifacts\":" + JsonUtil.Bool(includeProjectArtifacts)
                + ",\"max_sessions\":" + maxSessions
                + ",\"max_scan_files\":" + maxScanFiles
                + ",\"matched_sessions\":" + sessions.Count
                + ",\"returned_sessions\":" + returned
                + ",\"sessions_truncated\":" + JsonUtil.Bool(sessions.Count > returned)
                + ",\"scanned_artifact_file_count\":" + scannedFiles
                + ",\"skipped_artifact_file_count\":" + skippedFiles
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"sessions\":[" + json + "]"
                + ",\"capture_data_inline\":false"
                + ",\"recording_started\":false"
                + ",\"capture_created\":false"
                + ",\"next_valid_actions\":[\"profiler.session.summary\",\"profiler.report.read\",\"job.artifact.read\",\"artifact.metadata\",\"artifact.list\",\"artifact.prune\"]}");
        }

        [McpCommand(
            "profiler.session.summary",
            Title = "Profiler session summary",
            Description = "Read bounded metadata for one profiler session from process memory or fixed project-local profiler artifacts without recording",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = SessionSummarySchemaJson,
            Tags = new string[] { "profiler", "performance", "session", "artifact" },
            Capability = "profiler_readonly",
            ResultShape = "object_details",
            OutputLimitJson = SessionOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SessionSummary(McpCommandContext context)
        {
            var sessionId = Bounded(JsonUtil.ExtractString(context.ArgsJson, "session_id"), 120).Trim();
            var artifactPath = JsonUtil.ExtractString(context.ArgsJson, "artifact_path").Trim().Replace('\\', '/');
            var includeReportPreview = JsonUtil.ExtractBool(context.ArgsJson, "include_report_preview", false);
            var maxReportChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_report_chars", 2048), 0, MaxProfilerSessionSummaryChars);
            if (string.IsNullOrEmpty(sessionId) && string.IsNullOrEmpty(artifactPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "session_id or artifact_path is required");
            }

            if (!string.IsNullOrEmpty(artifactPath))
            {
                return ProfilerSessionSummaryForArtifactPath(artifactPath, includeReportPreview, maxReportChars);
            }

            if (!SafeProfilerSessionId(sessionId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "session_id must be a bounded profiler id without path separators, dots, URI syntax or NUL bytes");
            }

            var snapshot = FindProfilerReport(sessionId, DateTime.UtcNow);
            if (snapshot != null)
            {
                var descriptor = ProfilerSessionFromSnapshot(snapshot);
                return CommandExecutionResult.Ok(ProfilerSessionSummaryJson(descriptor, snapshot.Report, includeReportPreview, maxReportChars, false));
            }

            var reportPath = ProfilerReportArtifactPath(sessionId);
            if (!string.IsNullOrEmpty(reportPath) && TryProfilerSessionSummaryForArtifactPath(reportPath, includeReportPreview, maxReportChars, out var reportResponse))
            {
                return CommandExecutionResult.Ok(reportResponse);
            }

            var capturePath = ProfilerCaptureArtifactPath(sessionId);
            if (!string.IsNullOrEmpty(capturePath) && TryProfilerSessionSummaryForArtifactPath(capturePath, includeReportPreview, maxReportChars, out var captureResponse))
            {
                return CommandExecutionResult.Ok(captureResponse);
            }

            return CommandExecutionResult.Ok("{\"status\":\"not_found\""
                + ",\"session_id\":" + JsonUtil.String(sessionId)
                + ",\"searched_roots\":[\"process_memory\",\"" + ProfilerReportArtifactRoot + "\",\"" + ProfilerCaptureArtifactRoot + "\"]"
                + ",\"capture_data_inline\":false"
                + ",\"recording_started\":false"
                + ",\"capture_created\":false"
                + ",\"next_valid_actions\":[\"profiler.session.list\",\"artifact.list\"]}");
        }

        static ProfilerFrameReadResult ReadProfilerFrameMarkers(
            int requestedFrameIndex,
            int maxThreads,
            int maxMarkers,
            string threadFilter,
            bool includeChildren,
            string markerQuery,
            bool caseSensitive)
        {
            var result = new ProfilerFrameReadResult
            {
                UnityVersion = Application.unityVersion,
                RequestedFrameIndex = requestedFrameIndex,
                FrameIndex = requestedFrameIndex,
                MaxThreads = maxThreads,
                MaxMarkers = maxMarkers,
            };

            if (!TryResolveProfilerFrame(requestedFrameIndex, result, out var iteratorType))
            {
                return result;
            }

            var iterator = SafeCreate(iteratorType);
            if (iterator == null)
            {
                result.ApiAvailable = false;
                result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                result.Reason = "Profiler frame iterator could not be created";
                return result;
            }

            try
            {
                if (!TryReadThreadCount(iteratorType, iterator, result.FrameIndex, out var threadCount))
                {
                    result.ApiAvailable = false;
                    result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                    result.Reason = "Profiler GetThreadCount(frame) was unavailable or failed";
                    return result;
                }

                result.ThreadCountAvailable = true;
                result.ThreadCount = threadCount;
                result.SelectionState = ProfilerFrameReadPolicy.ClassifyData(result.SelectionState, true, threadCount);
                result.DataAvailable = result.SelectionState == ProfilerFrameReadPolicy.Authoritative;
                if (!result.DataAvailable)
                {
                    result.Reason = "The selected profiler frame is in range but its thread data is not settled";
                    return result;
                }

                var threadLimit = Math.Min(maxThreads, Math.Max(0, threadCount));
                for (var threadIndex = 0; threadIndex < threadLimit; threadIndex++)
                {
                    result.ScannedThreads++;
                    if (!TrySetRoot(iteratorType, iterator, result.FrameIndex, threadIndex))
                    {
                        result.Truncated = true;
                        result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                        result.DataAvailable = false;
                        result.Reason = "Profiler iterator root selection failed before the frame could be read authoritatively";
                        var unavailableThread = new ProfilerThreadSnapshot(threadIndex, "thread-" + threadIndex)
                        {
                            Reason = "Profiler iterator root could not be set for this thread",
                            Truncated = true,
                        };
                        if (ThreadMatches(unavailableThread.Name, threadFilter)) result.Threads.Add(unavailableThread);
                        break;
                    }

                    if (!TryReadThreadName(iteratorType, iterator, threadIndex, out var rawThreadName))
                    {
                        result.Truncated = true;
                        result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                        result.DataAvailable = false;
                        result.Reason = "Profiler thread identity failed before the frame could be read authoritatively";
                        break;
                    }
                    var threadName = Bounded(rawThreadName, 160);
                    if (!ThreadMatches(threadName, threadFilter))
                    {
                        continue;
                    }

                    var thread = new ProfilerThreadSnapshot(threadIndex, threadName);
                    result.Threads.Add(thread);
                    var safety = 0;
                    var traversalCompleted = false;
                    while (safety < 5000)
                    {
                        safety++;
                        if (!TryNextMarker(iteratorType, iterator, includeChildren, out var hasNextMarker))
                        {
                            thread.Truncated = true;
                            result.Truncated = true;
                            result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                            result.DataAvailable = false;
                            result.Reason = "Profiler iterator traversal failed before the frame could be read authoritatively";
                            break;
                        }
                        if (!hasNextMarker)
                        {
                            traversalCompleted = true;
                            break;
                        }
                        if (!TryReadMarkerSnapshot(iteratorType, iterator, threadIndex, threadName, out var marker))
                        {
                            thread.Truncated = true;
                            result.Truncated = true;
                            result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                            result.DataAvailable = false;
                            result.Reason = "Profiler marker data failed before the frame could be read authoritatively";
                            break;
                        }
                        if (!string.IsNullOrEmpty(markerQuery) && !ContainsMarkerQuery(marker.Name, markerQuery, caseSensitive)) continue;
                        thread.MatchedMarkers++;
                        result.MatchedMarkers++;
                        if (result.IncludedMarkers < maxMarkers)
                        {
                            thread.Markers.Add(marker);
                            result.IncludedMarkers++;
                        }
                        else
                        {
                            thread.Truncated = true;
                            result.Truncated = true;
                        }
                    }

                    if (ProfilerFrameReadPolicy.TraversalLimitReached(safety, 5000, traversalCompleted, result.SelectionState == ProfilerFrameReadPolicy.Unavailable))
                    {
                        thread.Truncated = true;
                        result.Truncated = true;
                    }

                    if (result.SelectionState == ProfilerFrameReadPolicy.Unavailable) break;

                    if (result.IncludedMarkers >= maxMarkers)
                    {
                        if (thread.MatchedMarkers > thread.Markers.Count || threadIndex + 1 < threadLimit || threadCount > threadLimit)
                        {
                            result.Truncated = true;
                        }
                        break;
                    }
                }

                if (threadCount > result.ScannedThreads)
                {
                    result.Truncated = true;
                }

                RevalidateProfilerFrameRange(result);
                return result;
            }
            finally
            {
                SafeDispose(iterator);
            }
        }

        static bool TryResolveProfilerFrame(int requestedFrameIndex, ProfilerFrameReadResult result, out Type iteratorType)
        {
            iteratorType = ResolveType("UnityEditorInternal.ProfilerFrameDataIterator");
            var profilerDriver = ResolveType("UnityEditorInternal.ProfilerDriver");
            result.ApiAvailable = iteratorType != null && profilerDriver != null && HasProfilerIteratorMemberShape(iteratorType);
            result.Source = iteratorType == null ? string.Empty : iteratorType.FullName;
            if (!result.ApiAvailable)
            {
                result.FrameIndex = -1;
                result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                result.Reason = "The required Unity Profiler frame iterator member shape is unavailable";
                return false;
            }

            result.RangeAvailable = TryReadProfilerFrameRange(profilerDriver, out var firstFrameIndex, out var lastFrameIndex);
            result.FirstFrameIndex = firstFrameIndex;
            result.LastFrameIndex = lastFrameIndex;
            result.SelectionState = ProfilerFrameReadPolicy.SelectFrame(
                true,
                result.RangeAvailable,
                firstFrameIndex,
                lastFrameIndex,
                requestedFrameIndex,
                out var selectedFrameIndex);
            result.FrameIndex = selectedFrameIndex;
            if (result.SelectionState == ProfilerFrameReadPolicy.Selected) return true;
            if (result.SelectionState == ProfilerFrameReadPolicy.Invalid)
            {
                result.Reason = "Requested profiler frame_index is outside the current recorded frame range";
            }
            else if (result.SelectionState == ProfilerFrameReadPolicy.Unsettled)
            {
                result.Reason = "No settled profiler frame range is currently available";
            }
            else
            {
                result.Reason = "Profiler frame range evidence is unavailable";
            }
            return false;
        }

        static bool HasProfilerIteratorMemberShape(Type iteratorType)
        {
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            return iteratorType.GetMethod("GetThreadCount", flags, null, new Type[] { typeof(int) }, null) != null
                && iteratorType.GetMethod("GetThreadName", flags, null, Type.EmptyTypes, null) != null
                && iteratorType.GetMethod("SetRoot", flags, null, new Type[] { typeof(int), typeof(int) }, null) != null
                && iteratorType.GetMethod("Next", flags, null, new Type[] { typeof(bool) }, null) != null
                && iteratorType.GetProperty("name", flags) != null
                && iteratorType.GetProperty("durationMS", flags) != null
                && iteratorType.GetProperty("depth", flags) != null;
        }

        static bool TryReadProfilerFrameRange(Type profilerDriver, out int firstFrameIndex, out int lastFrameIndex)
        {
            var firstAvailable = TryReadStaticInt(profilerDriver, "firstFrameIndex", out firstFrameIndex);
            var lastAvailable = TryReadStaticInt(profilerDriver, "lastFrameIndex", out lastFrameIndex);
            return firstAvailable && lastAvailable;
        }

        static bool TryReadThreadCount(Type iteratorType, object iterator, int frameIndex, out int threadCount)
        {
            threadCount = -1;
            try
            {
                var method = iteratorType.GetMethod("GetThreadCount", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(int) }, null);
                return method != null && TryConvertInt(method.Invoke(iterator, new object[] { frameIndex }), out threadCount) && threadCount >= 0;
            }
            catch
            {
                threadCount = -1;
                return false;
            }
        }

        static void RevalidateProfilerFrameRange(ProfilerFrameReadResult result)
        {
            if (result.SelectionState != ProfilerFrameReadPolicy.Authoritative) return;
            var profilerDriver = ResolveType("UnityEditorInternal.ProfilerDriver");
            if (profilerDriver == null || !TryReadProfilerFrameRange(profilerDriver, out var firstFrameIndex, out var lastFrameIndex))
            {
                result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
                result.DataAvailable = false;
                result.RangeAvailable = false;
                result.Reason = "Profiler frame range could not be revalidated after marker traversal";
                return;
            }

            var state = ProfilerFrameReadPolicy.SelectFrame(true, true, firstFrameIndex, lastFrameIndex, result.FrameIndex, out _);
            result.FirstFrameIndex = firstFrameIndex;
            result.LastFrameIndex = lastFrameIndex;
            result.RangeAvailable = true;
            result.FrameRangeRevalidated = true;
            if (state == ProfilerFrameReadPolicy.Selected) return;
            result.SelectionState = ProfilerFrameReadPolicy.Unavailable;
            result.DataAvailable = false;
            result.Reason = "The selected profiler frame left the recorded range during marker traversal";
        }

        static string FrameMarkersJson(ProfilerFrameReadResult result, string threadFilter, bool includeChildren)
        {
            var builder = new StringBuilder();
            builder.Append("{\"api_available\":").Append(JsonUtil.Bool(result.ApiAvailable));
            builder.Append(",\"unity_version\":").Append(JsonUtil.String(result.UnityVersion));
            builder.Append(",\"source\":").Append(JsonUtil.String(result.Source));
            builder.Append(",\"frame_index\":").Append(result.FrameIndex);
            builder.Append(",\"result_state\":").Append(JsonUtil.String(ProfilerFrameReadPolicy.ResultState(result.SelectionState, result.Truncated, result.MatchedMarkers)));
            AppendFrameEvidenceJson(builder, result);
            builder.Append(",\"thread_filter\":").Append(JsonUtil.String(threadFilter ?? string.Empty));
            builder.Append(",\"include_children\":").Append(JsonUtil.Bool(includeChildren));
            builder.Append(",\"max_threads\":").Append(result.MaxThreads);
            builder.Append(",\"max_markers\":").Append(result.MaxMarkers);
            builder.Append(",\"thread_count\":").Append(result.ThreadCountAvailable ? result.ThreadCount.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"scanned_threads\":").Append(result.ScannedThreads);
            builder.Append(",\"matched_markers\":").Append(result.MatchedMarkers);
            builder.Append(",\"included_markers\":").Append(result.IncludedMarkers);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(result.Truncated));
            if (!string.IsNullOrEmpty(result.Reason))
            {
                builder.Append(",\"reason\":").Append(JsonUtil.String(result.Reason));
            }

            builder.Append(",\"threads\":[");
            for (var index = 0; index < result.Threads.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendThreadSnapshotJson(builder, result.Threads[index]);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static string SearchProfilerMarkersJson(ProfilerFrameReadResult result, string query, bool caseSensitive, bool includeChildren)
        {
            var results = new StringBuilder();
            var included = 0;
            for (var threadIndex = 0; threadIndex < result.Threads.Count; threadIndex++)
            {
                var thread = result.Threads[threadIndex];
                for (var markerIndex = 0; markerIndex < thread.Markers.Count; markerIndex++)
                {
                    if (included++ > 0) results.Append(',');
                    AppendMarkerSnapshotJson(results, thread.Markers[markerIndex], true);
                }
            }

            var builder = new StringBuilder();
            builder.Append("{\"api_available\":").Append(JsonUtil.Bool(result.ApiAvailable));
            builder.Append(",\"unity_version\":").Append(JsonUtil.String(result.UnityVersion));
            builder.Append(",\"source\":").Append(JsonUtil.String(result.Source));
            builder.Append(",\"query\":").Append(JsonUtil.String(query));
            builder.Append(",\"case_sensitive\":").Append(JsonUtil.Bool(caseSensitive));
            builder.Append(",\"include_children\":").Append(JsonUtil.Bool(includeChildren));
            builder.Append(",\"frame_index\":").Append(result.FrameIndex);
            builder.Append(",\"result_state\":").Append(JsonUtil.String(ProfilerFrameReadPolicy.ResultState(result.SelectionState, result.Truncated, result.MatchedMarkers)));
            AppendFrameEvidenceJson(builder, result);
            builder.Append(",\"max_threads\":").Append(result.MaxThreads);
            builder.Append(",\"max_results\":").Append(result.MaxMarkers);
            builder.Append(",\"thread_count\":").Append(result.ThreadCountAvailable ? result.ThreadCount.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"scanned_threads\":").Append(result.ScannedThreads);
            builder.Append(",\"matched_markers\":").Append(result.MatchedMarkers);
            builder.Append(",\"included_results\":").Append(result.IncludedMarkers);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(result.Truncated));
            if (!string.IsNullOrEmpty(result.Reason)) builder.Append(",\"reason\":").Append(JsonUtil.String(result.Reason));
            builder.Append(",\"results\":[").Append(results).Append("]}");
            return builder.ToString();
        }

        static void AppendFrameEvidenceJson(StringBuilder builder, ProfilerFrameReadResult result)
        {
            builder.Append(",\"frame_evidence\":{\"selection_state\":").Append(JsonUtil.String(result.SelectionState));
            builder.Append(",\"requested_frame_index\":").Append(result.RequestedFrameIndex >= 0 ? result.RequestedFrameIndex.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"selected_frame_index\":").Append(result.FrameIndex >= 0 ? result.FrameIndex.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"range_available\":").Append(JsonUtil.Bool(result.RangeAvailable));
            builder.Append(",\"first_frame_index\":").Append(result.RangeAvailable ? result.FirstFrameIndex.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"last_frame_index\":").Append(result.RangeAvailable ? result.LastFrameIndex.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"data_available\":").Append(JsonUtil.Bool(result.DataAvailable));
            builder.Append(",\"thread_count_available\":").Append(JsonUtil.Bool(result.ThreadCountAvailable));
            builder.Append(",\"range_revalidated\":").Append(JsonUtil.Bool(result.FrameRangeRevalidated));
            builder.Append('}');
        }

        static string BuildProfilerReportMarkdown(string name, ProfilerFrameReadResult frame, int maxChars, out bool truncated)
        {
            var builder = new StringBuilder();
            builder.Append("# ").Append(name).Append('\n').Append('\n');
            builder.Append("- Unity version: ").Append(frame.UnityVersion).Append('\n');
            builder.Append("- API available: ").Append(frame.ApiAvailable ? "true" : "false").Append('\n');
            builder.Append("- Selection state: ").Append(frame.SelectionState).Append('\n');
            builder.Append("- Result state: ").Append(ProfilerFrameReadPolicy.ResultState(frame.SelectionState, frame.Truncated, frame.MatchedMarkers)).Append('\n');
            builder.Append("- Frame range: ").Append(frame.RangeAvailable ? frame.FirstFrameIndex + ".." + frame.LastFrameIndex : "unavailable").Append('\n');
            builder.Append("- Data available: ").Append(frame.DataAvailable ? "true" : "false").Append('\n');
            builder.Append("- Frame index: ").Append(frame.FrameIndex).Append('\n');
            builder.Append("- Source: ").Append(frame.Source).Append('\n');
            builder.Append("- Threads scanned: ").Append(frame.ScannedThreads).Append(" / ").Append(frame.ThreadCountAvailable ? frame.ThreadCount.ToString(CultureInfo.InvariantCulture) : "unavailable").Append('\n');
            builder.Append("- Markers included: ").Append(frame.IncludedMarkers).Append(" / ").Append(frame.MatchedMarkers).Append('\n');
            builder.Append("- Truncated: ").Append(frame.Truncated ? "true" : "false").Append('\n');
            if (!string.IsNullOrEmpty(frame.Reason))
            {
                builder.Append("- Reason: ").Append(frame.Reason).Append('\n');
            }

            builder.Append('\n').Append("## Marker Timings").Append('\n');
            if (frame.Threads.Count == 0)
            {
                builder.Append('\n').Append("No profiler marker rows were available.").Append('\n');
            }

            for (var threadIndex = 0; threadIndex < frame.Threads.Count; threadIndex++)
            {
                var thread = frame.Threads[threadIndex];
                builder.Append('\n').Append("### ").Append(thread.Name).Append(" (thread ").Append(thread.ThreadIndex).Append(')').Append('\n');
                if (!string.IsNullOrEmpty(thread.Reason))
                {
                    builder.Append(thread.Reason).Append('\n');
                }

                for (var markerIndex = 0; markerIndex < thread.Markers.Count; markerIndex++)
                {
                    var marker = thread.Markers[markerIndex];
                    builder.Append("- ").Append(marker.Name)
                        .Append(" | calls: ").Append(marker.Calls.HasValue ? marker.Calls.Value.ToString(CultureInfo.InvariantCulture) : "unavailable")
                        .Append(" | total_ms: ").Append(marker.TotalMs ?? "unavailable")
                        .Append(" | self_ms: ").Append(marker.SelfMs ?? "unavailable")
                        .Append(" | depth: ").Append(marker.Depth.HasValue ? marker.Depth.Value.ToString(CultureInfo.InvariantCulture) : "unavailable")
                        .Append('\n');
                }
            }

            var report = builder.ToString();
            truncated = report.Length > maxChars || frame.Truncated;
            return Bounded(report, maxChars);
        }

        static string ProfilerReportSummary(ProfilerFrameReadResult frame)
        {
            if (!frame.DataAvailable)
            {
                return "Profiler frame report unavailable: " + frame.Reason;
            }

            return "Profiler frame " + frame.FrameIndex + ": " + frame.IncludedMarkers + " marker row(s) across " + frame.Threads.Count + " thread(s)";
        }

        static bool TryReadMarkerSnapshot(Type iteratorType, object iterator, int threadIndex, string threadName, out ProfilerMarkerSnapshot marker)
        {
            marker = null;
            var nameAvailable = TryReadInstanceStringValue(iteratorType, iterator, "name", out var markerName);
            var durationAvailable = TryReadInstanceFloatValue(iteratorType, iterator, "durationMS", out var durationMs);
            var depthAvailable = TryReadInstanceIntValue(iteratorType, iterator, "depth", out var depth);
            if (!nameAvailable || !durationAvailable || !depthAvailable) return false;
            marker = new ProfilerMarkerSnapshot(
                threadIndex,
                threadName ?? string.Empty,
                Bounded(markerName, 160),
                null,
                durationMs,
                null,
                depth);
            return true;
        }

        static void AppendThreadSnapshotJson(StringBuilder builder, ProfilerThreadSnapshot thread)
        {
            builder.Append("{\"thread_index\":").Append(thread.ThreadIndex);
            builder.Append(",\"name\":").Append(JsonUtil.String(thread.Name));
            builder.Append(",\"matched_markers\":").Append(thread.MatchedMarkers);
            builder.Append(",\"included_markers\":").Append(thread.Markers.Count);
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(thread.Truncated));
            if (!string.IsNullOrEmpty(thread.Reason))
            {
                builder.Append(",\"reason\":").Append(JsonUtil.String(thread.Reason));
            }

            builder.Append(",\"markers\":[");
            for (var index = 0; index < thread.Markers.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                AppendMarkerSnapshotJson(builder, thread.Markers[index], false);
            }

            builder.Append("]}");
        }

        static void AppendMarkerSnapshotJson(StringBuilder builder, ProfilerMarkerSnapshot marker, bool includeThread)
        {
            builder.Append("{\"name\":").Append(JsonUtil.String(marker.Name));
            if (includeThread)
            {
                builder.Append(",\"thread_index\":").Append(marker.ThreadIndex);
                builder.Append(",\"thread\":").Append(JsonUtil.String(marker.ThreadName));
            }

            builder.Append(",\"calls\":").Append(marker.Calls.HasValue ? marker.Calls.Value.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"total_ms\":").Append(marker.TotalMs ?? "null");
            builder.Append(",\"self_ms\":").Append(marker.SelfMs ?? "null");
            builder.Append(",\"depth\":").Append(marker.Depth.HasValue ? marker.Depth.Value.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append('}');
        }

        static bool ThreadMatches(string threadName, string threadFilter)
        {
            if (string.IsNullOrWhiteSpace(threadFilter))
            {
                return true;
            }

            return (threadName ?? string.Empty).IndexOf(threadFilter.Trim(), StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool ContainsMarkerQuery(string markerName, string query, bool caseSensitive)
        {
            return (markerName ?? string.Empty).IndexOf(query ?? string.Empty, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string NewProfilerReportId()
        {
            return "profiler_report_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static string ProfilerReportArtifactPath(string reportId)
        {
            if (string.IsNullOrEmpty(reportId) || reportId.IndexOfAny(new[] { '/', '\\', ':', '.', '\0' }) >= 0)
            {
                return string.Empty;
            }

            return ProfilerReportArtifactRoot + reportId + ".json";
        }

        static string MemoryCompareArtifactPath(string compareId)
        {
            if (string.IsNullOrEmpty(compareId) || compareId.IndexOfAny(new[] { '/', '\\', ':', '.', '\0' }) >= 0)
            {
                return string.Empty;
            }

            return ProfilerReportArtifactRoot + compareId + ".json";
        }

        static string ProfilerCaptureArtifactPath(string captureId)
        {
            if (string.IsNullOrEmpty(captureId) || captureId.IndexOfAny(new[] { '/', '\\', ':', '.', '\0' }) >= 0)
            {
                return string.Empty;
            }

            return ProfilerCaptureArtifactRoot + captureId + ".json";
        }

        static void AddProfilerProcessMemorySessions(List<ProfilerSessionDescriptor> sessions, DateTime now)
        {
            lock (ReportCacheLock)
            {
                foreach (var snapshot in ReportCache.Values)
                {
                    if (snapshot.ExpiresAtUtc < now)
                    {
                        continue;
                    }

                    sessions.Add(ProfilerSessionFromSnapshot(snapshot));
                }
            }
        }

        static ProfilerSessionDescriptor ProfilerSessionFromSnapshot(ProfilerReportSnapshot snapshot)
        {
            return new ProfilerSessionDescriptor(
                snapshot.ReportId,
                "profiler_report",
                "process_memory",
                "profiler.report.create",
                snapshot.DataAvailable ? "ok" : "frame_unavailable",
                snapshot.Name,
                snapshot.Summary,
                snapshot.CreatedUtc.ToString("o"),
                "text/markdown",
                snapshot.ArtifactPath,
                snapshot.ArtifactPersisted,
                snapshot.ArtifactPersisted ? snapshot.ArtifactPath : string.Empty,
                0L,
                snapshot.FrameIndex,
                snapshot.ApiAvailable,
                false);
        }

        static void AddProjectProfilerSessions(string artifactRoot, string defaultKind, int maxScanFiles, List<ProfilerSessionDescriptor> sessions, ref int scannedFiles, ref int skippedFiles, ref bool scanTruncated)
        {
            string fullRoot;
            try
            {
                fullRoot = RequireProjectLocalDirectoryPath(artifactRoot);
            }
            catch
            {
                skippedFiles++;
                return;
            }

            if (!Directory.Exists(fullRoot))
            {
                return;
            }

            FileInfo[] files;
            try
            {
                files = new DirectoryInfo(fullRoot).GetFiles("*.json", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                skippedFiles++;
                return;
            }

            Array.Sort(files, CompareFileInfoNewestFirst);
            for (var index = 0; index < files.Length; index++)
            {
                if (scannedFiles >= maxScanFiles)
                {
                    scanTruncated = true;
                    return;
                }

                scannedFiles++;
                var file = files[index];
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length <= 0 || file.Length > MaxMemorySnapshotArtifactBytes)
                {
                    skippedFiles++;
                    continue;
                }

                string content;
                try
                {
                    content = File.ReadAllText(file.FullName, Encoding.UTF8);
                }
                catch
                {
                    skippedFiles++;
                    continue;
                }

                sessions.Add(ProfilerSessionFromArtifact(artifactRoot + file.Name, content, file.Length, file.LastWriteTimeUtc, defaultKind));
            }
        }

        static int CompareFileInfoNewestFirst(FileInfo left, FileInfo right)
        {
            var timeCompare = right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc);
            return timeCompare != 0 ? timeCompare : string.Compare(left.Name, right.Name, StringComparison.Ordinal);
        }

        static int CompareProfilerSessions(ProfilerSessionDescriptor left, ProfilerSessionDescriptor right)
        {
            var createdCompare = string.Compare(right.CreatedUtc, left.CreatedUtc, StringComparison.Ordinal);
            if (createdCompare != 0)
            {
                return createdCompare;
            }

            return string.Compare(left.SessionId, right.SessionId, StringComparison.Ordinal);
        }

        static ProfilerSessionDescriptor ProfilerSessionFromArtifact(string artifactPath, string content, long artifactBytes, DateTime lastWriteUtc, string defaultKind)
        {
            var command = Bounded(JsonUtil.ExtractString(content, "command"), 120);
            var kind = ProfilerSessionKind(command, defaultKind);
            var sessionId = Bounded(FirstNonEmpty(
                JsonUtil.ExtractString(content, "report_id"),
                JsonUtil.ExtractString(content, "comparison_id"),
                JsonUtil.ExtractString(content, "capture_id"),
                Path.GetFileNameWithoutExtension(artifactPath)), 120);
            var createdUtc = Bounded(FirstNonEmpty(
                JsonUtil.ExtractString(content, "created_utc"),
                JsonUtil.ExtractString(content, "created_at"),
                lastWriteUtc.ToString("o")), 80);
            var name = Bounded(JsonUtil.ExtractString(content, "name"), 120);
            var summary = Bounded(FirstNonEmpty(
                JsonUtil.ExtractString(content, "summary"),
                JsonUtil.ExtractString(content, "warning"),
                JsonUtil.ExtractString(content, "status")), 240);
            var contentType = Bounded(JsonUtil.ExtractString(content, "content_type"), 120);
            var frameIndex = JsonUtil.ExtractInt(content, "frame_index", -1);
            var apiAvailable = JsonUtil.ExtractBool(content, "api_available", false);
            var captureExported = JsonUtil.ExtractBool(content, "capture_exported", false);
            var captureArtifactPath = Bounded(JsonUtil.ExtractString(content, "capture_artifact_path"), 512);
            var status = Bounded(JsonUtil.ExtractString(content, "status"), 80);
            if (string.Equals(command, "profiler.report.create", StringComparison.Ordinal))
            {
                var artifactSchemaVersion = JsonUtil.ExtractInt(content, "artifact_schema_version", 1);
                var selectionState = JsonUtil.ExtractString(content, "selection_state");
                var resultState = JsonUtil.ExtractString(content, "result_state");
                status = artifactSchemaVersion >= 2
                    ? (JsonUtil.ExtractBool(content, "data_available", false)
                        && ProfilerFrameReadPolicy.IsAuthoritativeResultEvidence(selectionState, resultState) ? "ok" : "frame_unavailable")
                    : "legacy_unverified";
            }
            return new ProfilerSessionDescriptor(
                sessionId,
                kind,
                "project_artifact",
                command,
                status,
                name,
                summary,
                createdUtc,
                contentType,
                artifactPath,
                true,
                captureArtifactPath,
                artifactBytes,
                frameIndex,
                apiAvailable,
                captureExported);
        }

        static string ProfilerSessionKind(string command, string defaultKind)
        {
            if (string.Equals(command, "profiler.report.create", StringComparison.Ordinal))
            {
                return "profiler_report";
            }

            if (string.Equals(command, "profiler.memory.snapshot.compare", StringComparison.Ordinal))
            {
                return "memory_snapshot_compare";
            }

            if (string.Equals(command, "profiler.capture.save", StringComparison.Ordinal))
            {
                return "profiler_capture";
            }

            return string.IsNullOrEmpty(defaultKind) ? "profiler_artifact" : defaultKind;
        }

        static CommandExecutionResult ProfilerSessionSummaryForArtifactPath(string artifactPath, bool includeReportPreview, int maxReportChars)
        {
            if (!artifactPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "artifact_path must point to a JSON profiler session artifact");
            }

            var requiredRoot = artifactPath.StartsWith(ProfilerReportArtifactRoot, StringComparison.Ordinal) ? ProfilerReportArtifactRoot :
                (artifactPath.StartsWith(ProfilerCaptureArtifactRoot, StringComparison.Ordinal) ? ProfilerCaptureArtifactRoot : string.Empty);
            if (string.IsNullOrEmpty(requiredRoot))
            {
                return CommandExecutionResult.Error("path_not_allowed", "artifact_path must stay under " + ProfilerReportArtifactRoot + " or " + ProfilerCaptureArtifactRoot);
            }

            string fullPath;
            try
            {
                fullPath = RequireProjectLocalFilePath(artifactPath, requiredRoot);
            }
            catch
            {
                return CommandExecutionResult.Error("path_not_allowed", "artifact_path must stay under " + requiredRoot);
            }

            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "profiler session artifact was not found");
            }

            var info = new FileInfo(fullPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length <= 0 || info.Length > MaxMemorySnapshotArtifactBytes)
            {
                return CommandExecutionResult.Error("invalid_field_value", "profiler session artifact must be a non-empty non-reparse JSON file no larger than " + MaxMemorySnapshotArtifactBytes + " bytes");
            }

            string content;
            try
            {
                content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("unity_exception", "Failed to read profiler session artifact: " + exception.GetType().Name);
            }

            var descriptor = ProfilerSessionFromArtifact(artifactPath, content, info.Length, info.LastWriteTimeUtc, requiredRoot == ProfilerCaptureArtifactRoot ? "profiler_capture" : "profiler_report");
            return CommandExecutionResult.Ok(ProfilerSessionSummaryJson(descriptor, content, includeReportPreview, maxReportChars, true));
        }

        static bool TryProfilerSessionSummaryForArtifactPath(string artifactPath, bool includeReportPreview, int maxReportChars, out string response)
        {
            response = string.Empty;
            if (!artifactPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var requiredRoot = artifactPath.StartsWith(ProfilerReportArtifactRoot, StringComparison.Ordinal) ? ProfilerReportArtifactRoot :
                (artifactPath.StartsWith(ProfilerCaptureArtifactRoot, StringComparison.Ordinal) ? ProfilerCaptureArtifactRoot : string.Empty);
            if (string.IsNullOrEmpty(requiredRoot))
            {
                return false;
            }

            string fullPath;
            try
            {
                fullPath = RequireProjectLocalFilePath(artifactPath, requiredRoot);
            }
            catch
            {
                return false;
            }

            if (!File.Exists(fullPath))
            {
                return false;
            }

            var info = new FileInfo(fullPath);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length <= 0 || info.Length > MaxMemorySnapshotArtifactBytes)
            {
                return false;
            }

            string content;
            try
            {
                content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch
            {
                return false;
            }

            var descriptor = ProfilerSessionFromArtifact(artifactPath, content, info.Length, info.LastWriteTimeUtc, requiredRoot == ProfilerCaptureArtifactRoot ? "profiler_capture" : "profiler_report");
            response = ProfilerSessionSummaryJson(descriptor, content, includeReportPreview, maxReportChars, true);
            return true;
        }

        static string ProfilerSessionSummaryJson(ProfilerSessionDescriptor descriptor, string content, bool includeReportPreview, int maxReportChars, bool fromArtifact)
        {
            var reportAvailable = string.Equals(descriptor.Command, "profiler.report.create", StringComparison.Ordinal);
            var fullReport = reportAvailable ? (fromArtifact ? JsonUtil.ExtractString(content, "report") : content) : string.Empty;
            var report = includeReportPreview && maxReportChars > 0 && reportAvailable
                ? Bounded(fullReport, maxReportChars)
                : string.Empty;
            var responseStatus = reportAvailable && !string.Equals(descriptor.Status, "ok", StringComparison.Ordinal) ? descriptor.Status : "ok";
            return "{\"status\":" + JsonUtil.String(responseStatus)
                + ",\"session\":" + descriptor.ToJson(true)
                + ",\"source\":" + JsonUtil.String(descriptor.Source)
                + ",\"from_project_artifact\":" + JsonUtil.Bool(fromArtifact)
                + ",\"report_available\":" + JsonUtil.Bool(reportAvailable)
                + ",\"include_report_preview\":" + JsonUtil.Bool(includeReportPreview)
                + ",\"max_report_chars\":" + maxReportChars
                + ",\"report_preview\":" + JsonUtil.String(report)
                + ",\"report_preview_truncated\":" + JsonUtil.Bool(reportAvailable && includeReportPreview && fullReport.Length > report.Length)
                + ",\"capture_data_inline\":false"
                + ",\"recording_started\":false"
                + ",\"capture_created\":false"
                + ",\"next_valid_actions\":[\"profiler.session.list\",\"profiler.report.read\",\"job.artifact.read\",\"artifact.metadata\",\"artifact.list\",\"artifact.prune\"]}";
        }

        static bool SafeProfilerSessionId(string sessionId)
        {
            return !string.IsNullOrEmpty(sessionId)
                && sessionId.Length <= 120
                && sessionId.IndexOf("://", StringComparison.Ordinal) < 0
                && sessionId.IndexOfAny(new[] { '/', '\\', ':', '.', '\0' }) < 0;
        }

        static string FirstNonEmpty(params string[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (!string.IsNullOrEmpty(values[index]))
                {
                    return values[index];
                }
            }

            return string.Empty;
        }

        static void AddMemorySnapshotCandidates(List<MemorySnapshotCandidate> candidates, int maxScanFiles, int maxMetricNames, ref int scannedFiles, ref int skippedFiles, ref bool scanTruncated)
        {
            string fullRoot;
            try
            {
                fullRoot = RequireProjectLocalDirectoryPath(JobArtifactRoot);
            }
            catch
            {
                skippedFiles++;
                return;
            }

            if (!Directory.Exists(fullRoot))
            {
                return;
            }

            ScanMemorySnapshotDirectory(fullRoot, fullRoot, candidates, maxScanFiles, maxMetricNames, ref scannedFiles, ref skippedFiles, ref scanTruncated);
        }

        static void ScanMemorySnapshotDirectory(string fullRoot, string directoryPath, List<MemorySnapshotCandidate> candidates, int maxScanFiles, int maxMetricNames, ref int scannedFiles, ref int skippedFiles, ref bool scanTruncated)
        {
            if (scanTruncated)
            {
                return;
            }

            DirectoryInfo directory;
            try
            {
                directory = new DirectoryInfo(directoryPath);
                if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedFiles++;
                    return;
                }
            }
            catch
            {
                skippedFiles++;
                return;
            }

            FileInfo[] files;
            try
            {
                files = directory.GetFiles("*.json", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                skippedFiles++;
                files = new FileInfo[0];
            }

            Array.Sort(files, CompareFileInfoNewestFirst);
            for (var index = 0; index < files.Length; index++)
            {
                if (scannedFiles >= maxScanFiles)
                {
                    scanTruncated = true;
                    return;
                }

                scannedFiles++;
                var file = files[index];
                if ((file.Attributes & FileAttributes.ReparsePoint) != 0 || file.Length <= 0 || file.Length > MaxMemorySnapshotArtifactBytes)
                {
                    skippedFiles++;
                    continue;
                }

                string content;
                try
                {
                    content = File.ReadAllText(file.FullName, Encoding.UTF8);
                }
                catch
                {
                    skippedFiles++;
                    continue;
                }

                var candidate = BuildMemorySnapshotCandidate(ArtifactPathFromFullPath(fullRoot, file.FullName, JobArtifactRoot), content, file.Length, file.LastWriteTimeUtc, maxMetricNames);
                if (candidate != null)
                {
                    candidates.Add(candidate);
                }
            }

            DirectoryInfo[] directories;
            try
            {
                directories = directory.GetDirectories("*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                skippedFiles++;
                return;
            }

            Array.Sort(directories, CompareDirectoryInfoOrdinal);
            for (var index = 0; index < directories.Length; index++)
            {
                if ((directories[index].Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    skippedFiles++;
                    continue;
                }

                ScanMemorySnapshotDirectory(fullRoot, directories[index].FullName, candidates, maxScanFiles, maxMetricNames, ref scannedFiles, ref skippedFiles, ref scanTruncated);
                if (scanTruncated)
                {
                    return;
                }
            }
        }

        static MemorySnapshotCandidate BuildMemorySnapshotCandidate(string artifactPath, string content, long bytes, DateTime lastWriteUtc, int maxMetricNames)
        {
            var metricNames = new List<string>();
            var metricCount = 0;
            for (var index = 0; index < MemorySnapshotNumericFields.Length; index++)
            {
                var field = MemorySnapshotNumericFields[index];
                if (!TryExtractJsonNumber(content, field, out _))
                {
                    continue;
                }

                metricCount++;
                if (metricNames.Count < maxMetricNames)
                {
                    metricNames.Add(field);
                }
            }

            if (metricCount == 0)
            {
                return null;
            }

            return new MemorySnapshotCandidate(
                artifactPath,
                bytes,
                StableContentHash(content),
                Bounded(JsonUtil.ExtractString(content, "command"), 120),
                Bounded(FirstNonEmpty(JsonUtil.ExtractString(content, "created_utc"), JsonUtil.ExtractString(content, "created_at"), lastWriteUtc.ToString("o")), 80),
                Bounded(JsonUtil.ExtractString(content, "unity_version"), 80),
                metricCount,
                metricNames,
                metricCount > metricNames.Count);
        }

        static string ArtifactPathFromFullPath(string fullRoot, string fullPath, string artifactRoot)
        {
            var root = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var relative = fullPath.Length > root.Length ? fullPath.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : Path.GetFileName(fullPath);
            return artifactRoot + relative.Replace(Path.DirectorySeparatorChar, '/').Replace(Path.AltDirectorySeparatorChar, '/');
        }

        static int CompareDirectoryInfoOrdinal(DirectoryInfo left, DirectoryInfo right)
        {
            return string.Compare(left.FullName, right.FullName, StringComparison.OrdinalIgnoreCase);
        }

        static int CompareMemorySnapshotCandidates(MemorySnapshotCandidate left, MemorySnapshotCandidate right)
        {
            var createdCompare = string.Compare(right.CreatedUtc, left.CreatedUtc, StringComparison.Ordinal);
            if (createdCompare != 0)
            {
                return createdCompare;
            }

            return string.Compare(left.ArtifactPath, right.ArtifactPath, StringComparison.Ordinal);
        }

        static string JsonArrayStrings(List<string> values)
        {
            var builder = new StringBuilder();
            for (var index = 0; index < values.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(values[index]));
            }

            return "[" + builder + "]";
        }

        static bool TryWriteProfilerReportArtifact(string artifactPath, string reportId, string name, string summary, string report, DateTime createdUtc, DateTime expiresAtUtc, bool truncated, ProfilerFrameReadResult frame, out string warning)
        {
            warning = string.Empty;
            if (string.IsNullOrEmpty(artifactPath))
            {
                warning = "Profiler report artifact path could not be created";
                return false;
            }

            try
            {
                var fullPath = RequireProjectLocalFilePath(artifactPath, ProfilerReportArtifactRoot);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, ProfilerReportArtifactJson(reportId, name, summary, report, createdUtc, expiresAtUtc, truncated, frame), new UTF8Encoding(false));
                return true;
            }
            catch (Exception exception)
            {
                warning = "Profiler report artifact write failed: " + exception.GetType().Name;
                return false;
            }
        }

        static string ProfilerReportArtifactJson(string reportId, string name, string summary, string report, DateTime createdUtc, DateTime expiresAtUtc, bool truncated, ProfilerFrameReadResult frame)
        {
            return "{\"artifact_schema_version\":2"
                + ",\"command\":\"profiler.report.create\""
                + ",\"status\":" + JsonUtil.String(frame.DataAvailable ? "ok" : "frame_unavailable")
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"name\":" + JsonUtil.String(name)
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"api_available\":" + JsonUtil.Bool(frame.ApiAvailable)
                + ",\"selection_state\":" + JsonUtil.String(frame.SelectionState)
                + ",\"result_state\":" + JsonUtil.String(ProfilerFrameReadPolicy.ResultState(frame.SelectionState, frame.Truncated, frame.MatchedMarkers))
                + ",\"data_available\":" + JsonUtil.Bool(frame.DataAvailable)
                + ",\"range_available\":" + JsonUtil.Bool(frame.RangeAvailable)
                + ",\"first_frame_index\":" + (frame.RangeAvailable ? frame.FirstFrameIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"last_frame_index\":" + (frame.RangeAvailable ? frame.LastFrameIndex.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"thread_count\":" + (frame.ThreadCountAvailable ? frame.ThreadCount.ToString(CultureInfo.InvariantCulture) : "null")
                + ",\"frame_index\":" + frame.FrameIndex
                + ",\"created_at\":" + JsonUtil.String(createdUtc.ToString("o"))
                + ",\"created_utc\":" + JsonUtil.String(createdUtc.ToString("o"))
                + ",\"process_memory_expires_at_utc\":" + JsonUtil.String(expiresAtUtc.ToString("o"))
                + ",\"unity_version\":" + JsonUtil.String(frame.UnityVersion)
                + ",\"package_version\":" + JsonUtil.String(UnityBridgeRuntime.PackageVersion)
                + ",\"storage\":\"project_artifact\""
                + ",\"content_type\":\"text/markdown\""
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"report\":" + JsonUtil.String(report) + "}";
        }

        static bool TryWriteMemoryCompareArtifact(string artifactPath, string compareId, DateTime createdUtc, MemorySnapshotArtifact left, MemorySnapshotArtifact right, List<MemorySnapshotMetricDelta> metrics, out string warning)
        {
            warning = string.Empty;
            if (string.IsNullOrEmpty(artifactPath))
            {
                warning = "Memory snapshot compare artifact path could not be created";
                return false;
            }

            try
            {
                var fullPath = RequireProjectLocalFilePath(artifactPath, ProfilerReportArtifactRoot);
                var directory = Path.GetDirectoryName(fullPath);
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(fullPath, MemoryCompareArtifactJson(compareId, createdUtc, left, right, metrics), new UTF8Encoding(false));
                return true;
            }
            catch (Exception exception)
            {
                warning = "Memory snapshot compare artifact write failed: " + exception.GetType().Name;
                return false;
            }
        }

        static string MemoryCompareArtifactJson(string compareId, DateTime createdUtc, MemorySnapshotArtifact left, MemorySnapshotArtifact right, List<MemorySnapshotMetricDelta> metrics)
        {
            var metricsJson = MetricsJson(metrics, Math.Max(1, metrics.Count), out _, out var changedMetrics, out _);
            return "{\"artifact_schema_version\":1"
                + ",\"command\":\"profiler.memory.snapshot.compare\""
                + ",\"status\":\"ok\""
                + ",\"comparison_id\":" + JsonUtil.String(compareId)
                + ",\"created_at\":" + JsonUtil.String(createdUtc.ToString("o"))
                + ",\"created_utc\":" + JsonUtil.String(createdUtc.ToString("o"))
                + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                + ",\"package_version\":" + JsonUtil.String(UnityBridgeRuntime.PackageVersion)
                + ",\"storage\":\"project_artifact\""
                + ",\"content_type\":\"application/json\""
                + ",\"left\":" + MemorySnapshotArtifactSummaryJson(left)
                + ",\"right\":" + MemorySnapshotArtifactSummaryJson(right)
                + ",\"same_content\":" + JsonUtil.Bool(left.ContentHash == right.ContentHash)
                + ",\"fields_compared\":" + metrics.Count
                + ",\"changed_fields\":" + changedMetrics
                + ",\"metrics\":[" + metricsJson + "]}";
        }

        static bool TryReadProfilerReportArtifact(string reportId, int maxChars, out string response)
        {
            response = string.Empty;
            var artifactPath = ProfilerReportArtifactPath(reportId);
            if (string.IsNullOrEmpty(artifactPath))
            {
                return false;
            }

            try
            {
                var fullPath = RequireProjectLocalFilePath(artifactPath, ProfilerReportArtifactRoot);
                if (!File.Exists(fullPath))
                {
                    return false;
                }

                var content = File.ReadAllText(fullPath);
                var report = JsonUtil.ExtractString(content, "report");
                var artifactSchemaVersion = JsonUtil.ExtractInt(content, "artifact_schema_version", 1);
                var currentEvidence = artifactSchemaVersion >= 2;
                var selectionState = currentEvidence ? FirstNonEmpty(JsonUtil.ExtractString(content, "selection_state"), ProfilerFrameReadPolicy.Unavailable) : "legacy_unverified";
                var resultState = currentEvidence ? FirstNonEmpty(JsonUtil.ExtractString(content, "result_state"), ProfilerFrameReadPolicy.Unavailable) : "legacy_unverified";
                var dataAvailable = currentEvidence
                    && JsonUtil.ExtractBool(content, "data_available", false)
                    && ProfilerFrameReadPolicy.IsAuthoritativeResultEvidence(selectionState, resultState);
                var truncated = report.Length > maxChars;
                if (truncated)
                {
                    report = report.Substring(0, maxChars);
                }

                response = "{\"status\":" + JsonUtil.String(currentEvidence ? dataAvailable ? "ok" : "frame_unavailable" : "legacy_unverified")
                    + ",\"report_id\":" + JsonUtil.String(reportId)
                    + ",\"name\":" + JsonUtil.String(JsonUtil.ExtractString(content, "name"))
                    + ",\"summary\":" + JsonUtil.String(JsonUtil.ExtractString(content, "summary"))
                    + ",\"api_available\":" + JsonUtil.Bool(JsonUtil.ExtractBool(content, "api_available", false))
                    + ",\"selection_state\":" + JsonUtil.String(selectionState)
                    + ",\"result_state\":" + JsonUtil.String(resultState)
                    + ",\"data_available\":" + JsonUtil.Bool(dataAvailable)
                    + ",\"frame_index\":" + JsonUtil.ExtractInt(content, "frame_index", -1)
                    + ",\"created_utc\":" + JsonUtil.String(JsonUtil.ExtractString(content, "created_utc"))
                    + ",\"process_memory_expired\":true"
                    + ",\"storage\":\"project_artifact\""
                    + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                    + ",\"artifact_persisted\":true"
                    + ",\"artifact_refs\":[" + JsonUtil.String(artifactPath) + "]"
                    + ",\"readable_with\":\"job.artifact.read\""
                    + ",\"content_type\":\"text/markdown\""
                    + ",\"max_chars\":" + maxChars
                    + ",\"truncated\":" + JsonUtil.Bool(truncated)
                    + ",\"report\":" + JsonUtil.String(report) + "}";
                return true;
            }
            catch
            {
                response = string.Empty;
                return false;
            }
        }

        static bool TryLoadMemorySnapshotArtifact(string artifactPath, string fieldName, out MemorySnapshotArtifact snapshot, out CommandExecutionResult error)
        {
            snapshot = null;
            error = null;
            if (string.IsNullOrWhiteSpace(artifactPath))
            {
                error = CommandExecutionResult.Error("schema_validation_failed", "Argument " + fieldName + " is required");
                return false;
            }

            artifactPath = artifactPath.Replace('\\', '/');
            if (!artifactPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                error = CommandExecutionResult.Error("invalid_field_value", fieldName + " must point to a JSON artifact under " + JobArtifactRoot);
                return false;
            }

            string fullPath;
            try
            {
                fullPath = RequireProjectLocalFilePath(artifactPath, JobArtifactRoot);
            }
            catch
            {
                error = CommandExecutionResult.Error("path_not_allowed", fieldName + " must stay under " + JobArtifactRoot);
                return false;
            }

            if (!File.Exists(fullPath))
            {
                error = CommandExecutionResult.Error("asset_not_found", fieldName + " artifact was not found");
                return false;
            }

            var info = new FileInfo(fullPath);
            if (info.Length <= 0 || info.Length > MaxMemorySnapshotArtifactBytes)
            {
                error = CommandExecutionResult.Error("invalid_field_value", fieldName + " artifact must be between 1 byte and " + MaxMemorySnapshotArtifactBytes + " bytes");
                return false;
            }

            string content;
            try
            {
                content = File.ReadAllText(fullPath, Encoding.UTF8);
            }
            catch (Exception exception)
            {
                error = CommandExecutionResult.Error("unity_exception", "Failed to read " + fieldName + ": " + exception.GetType().Name);
                return false;
            }

            snapshot = new MemorySnapshotArtifact(
                artifactPath,
                info.Length,
                StableContentHash(content),
                Bounded(JsonUtil.ExtractString(content, "command"), 120),
                Bounded(JsonUtil.ExtractString(content, "created_at"), 80),
                Bounded(JsonUtil.ExtractString(content, "created_utc"), 80),
                Bounded(JsonUtil.ExtractString(content, "unity_version"), 80),
                content);
            return true;
        }

        static List<MemorySnapshotMetricDelta> CompareMemorySnapshotArtifacts(MemorySnapshotArtifact left, MemorySnapshotArtifact right)
        {
            var metrics = new List<MemorySnapshotMetricDelta>();
            metrics.Add(new MemorySnapshotMetricDelta("artifact_bytes", true, left.Bytes, true, right.Bytes));
            foreach (var field in MemorySnapshotNumericFields)
            {
                var leftHas = TryExtractJsonNumber(left.Content, field, out var leftValue);
                var rightHas = TryExtractJsonNumber(right.Content, field, out var rightValue);
                if (leftHas || rightHas)
                {
                    metrics.Add(new MemorySnapshotMetricDelta(field, leftHas, leftValue, rightHas, rightValue));
                }
            }

            return metrics;
        }

        static string MetricsJson(List<MemorySnapshotMetricDelta> metrics, int maxItems, out int includedMetrics, out int changedMetrics, out bool truncated)
        {
            var builder = new StringBuilder();
            includedMetrics = 0;
            changedMetrics = 0;
            truncated = metrics.Count > maxItems;
            for (var index = 0; index < metrics.Count; index++)
            {
                var metric = metrics[index];
                if (metric.Changed)
                {
                    changedMetrics++;
                }

                if (includedMetrics >= maxItems)
                {
                    continue;
                }

                if (includedMetrics > 0)
                {
                    builder.Append(',');
                }

                AppendMetricDeltaJson(builder, metric);
                includedMetrics++;
            }

            return builder.ToString();
        }

        static void AppendMetricDeltaJson(StringBuilder builder, MemorySnapshotMetricDelta metric)
        {
            builder.Append("{\"field\":").Append(JsonUtil.String(metric.Field));
            builder.Append(",\"left\":").Append(OptionalNumberJson(metric.LeftHasValue, metric.LeftValue));
            builder.Append(",\"right\":").Append(OptionalNumberJson(metric.RightHasValue, metric.RightValue));
            builder.Append(",\"delta\":").Append(metric.LeftHasValue && metric.RightHasValue ? NumberJson(metric.Delta) : "null");
            builder.Append(",\"delta_ratio\":").Append(metric.HasDeltaRatio ? NumberJson(metric.DeltaRatio) : "null");
            builder.Append(",\"changed\":").Append(JsonUtil.Bool(metric.Changed));
            builder.Append('}');
        }

        static string MemorySnapshotArtifactSummaryJson(MemorySnapshotArtifact snapshot)
        {
            return "{\"artifact_path\":" + JsonUtil.String(snapshot.ArtifactPath)
                + ",\"bytes\":" + snapshot.Bytes
                + ",\"content_hash\":" + JsonUtil.String(snapshot.ContentHash)
                + ",\"command\":" + JsonUtil.String(snapshot.Command)
                + ",\"created_at\":" + JsonUtil.String(string.IsNullOrEmpty(snapshot.CreatedAt) ? snapshot.CreatedUtc : snapshot.CreatedAt)
                + ",\"unity_version\":" + JsonUtil.String(snapshot.UnityVersion) + "}";
        }

        static bool TryExtractJsonNumber(string json, string field, out double value)
        {
            value = 0d;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(field))
            {
                return false;
            }

            var pattern = "\\\"" + Regex.Escape(field) + "\\\"\\s*:\\s*(-?\\d+(?:\\.\\d+)?)";
            var match = Regex.Match(json, pattern, RegexOptions.CultureInvariant);
            return match.Success && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }

        static string OptionalNumberJson(bool hasValue, double value)
        {
            return hasValue ? NumberJson(value) : "null";
        }

        static string NumberJson(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "null";
            }

            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }

        static string StableContentHash(string content)
        {
            unchecked
            {
                ulong hash = 1469598103934665603UL;
                for (var index = 0; index < (content ?? string.Empty).Length; index++)
                {
                    hash ^= content[index];
                    hash *= 1099511628211UL;
                }

                return "fnv1a64:" + hash.ToString("x16", CultureInfo.InvariantCulture);
            }
        }

        static string NewMemoryCompareId()
        {
            return "profiler_memory_compare_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        static void StoreProfilerReport(ProfilerReportSnapshot snapshot)
        {
            lock (ReportCacheLock)
            {
                ReportCache[snapshot.ReportId] = snapshot;
                ReportCacheOrder.Enqueue(snapshot.ReportId);
                while (ReportCacheOrder.Count > MaxCachedProfilerReports)
                {
                    ReportCache.Remove(ReportCacheOrder.Dequeue());
                }
            }
        }

        static ProfilerReportSnapshot FindProfilerReport(string reportId, DateTime now)
        {
            lock (ReportCacheLock)
            {
                if (!ReportCache.TryGetValue(reportId, out var snapshot))
                {
                    return null;
                }

                if (snapshot.ExpiresAtUtc <= now)
                {
                    ReportCache.Remove(reportId);
                    return null;
                }

                return snapshot;
            }
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

        static List<string> TryReadProfilerModuleNames(Type profilerDriver, out string sourceMember, out string reason)
        {
            sourceMember = string.Empty;
            reason = string.Empty;
            if (profilerDriver == null)
            {
                reason = "UnityEditorInternal.ProfilerDriver was not detected in loaded assemblies";
                return null;
            }

            foreach (var methodName in new string[] { "GetAllProfilerModuleNames", "GetProfilerModuleNames", "GetAllModuleNames" })
            {
                var method = profilerDriver.GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (method == null)
                {
                    continue;
                }

                var names = CoerceStringList(SafeInvoke(method));
                if (names == null)
                {
                    continue;
                }

                sourceMember = profilerDriver.FullName + "." + methodName;
                return names;
            }

            reason = "No known read-only profiler module listing API was detected";
            return null;
        }

        static object SafeInvoke(MethodInfo method)
        {
            try
            {
                return method.Invoke(null, Array.Empty<object>());
            }
            catch
            {
                return null;
            }
        }

        static object SafeCreate(Type type)
        {
            try
            {
                return Activator.CreateInstance(type);
            }
            catch
            {
                return null;
            }
        }

        static void SafeDispose(object value)
        {
            var disposable = value as IDisposable;
            if (disposable != null)
            {
                disposable.Dispose();
            }
        }

        static bool TryReadFrameIndex(Type profilerDriver, out int frameIndex)
        {
            foreach (var name in new string[] { "lastFrameIndex", "currentFrame", "selectedFrameIndex" })
            {
                if (TryReadStaticInt(profilerDriver, name, out frameIndex) && frameIndex >= 0)
                {
                    return true;
                }
            }

            frameIndex = -1;
            return false;
        }

        static bool TryReadStaticInt(Type type, string name, out int value)
        {
            value = 0;
            if (type == null)
            {
                return false;
            }

            try
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return TryConvertInt(property.GetValue(null, null), out value);
                }

                var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field != null)
                {
                    return TryConvertInt(field.GetValue(null), out value);
                }
            }
            catch
            {
                value = 0;
            }

            return false;
        }

        static bool TryReadInstanceIntValue(Type type, object instance, string name, out int value)
        {
            value = 0;
            try
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return TryConvertInt(property.GetValue(instance, null), out value);
                }

                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    return TryConvertInt(method.Invoke(instance, Array.Empty<object>()), out value);
                }
            }
            catch
            {
                value = 0;
            }

            return false;
        }

        static bool TryConvertInt(object value, out int result)
        {
            if (value is int intValue)
            {
                result = intValue;
                return true;
            }

            if (value is long longValue && longValue >= int.MinValue && longValue <= int.MaxValue)
            {
                result = (int)longValue;
                return true;
            }

            result = 0;
            return false;
        }

        static bool TryReadThreadName(Type iteratorType, object iterator, int threadIndex, out string threadName)
        {
            threadName = "thread-" + threadIndex;
            var method = iteratorType.GetMethod("GetThreadName", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (method == null)
            {
                return false;
            }

            try
            {
                threadName = Convert.ToString(method.Invoke(iterator, Array.Empty<object>())) ?? threadName;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool TrySetRoot(Type iteratorType, object iterator, int frameIndex, int threadIndex)
        {
            foreach (var method in iteratorType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (method.Name != "SetRoot")
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (parameters.Length != 2 || parameters[0].ParameterType != typeof(int) || parameters[1].ParameterType != typeof(int))
                {
                    continue;
                }

                try
                {
                    method.Invoke(iterator, new object[] { frameIndex, threadIndex });
                    return true;
                }
                catch
                {
                    return false;
                }
            }

            return false;
        }

        static bool TryNextMarker(Type iteratorType, object iterator, bool enterChildren, out bool hasNextMarker)
        {
            hasNextMarker = false;
            var method = iteratorType.GetMethod("Next", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, new Type[] { typeof(bool) }, null);
            if (method == null)
            {
                return false;
            }

            try
            {
                if (!(method.Invoke(iterator, new object[] { enterChildren }) is bool result)) return false;
                hasNextMarker = result;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool TryReadInstanceStringValue(Type type, object instance, string name, out string value)
        {
            value = string.Empty;
            try
            {
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = Convert.ToString(property.GetValue(instance, null)) ?? string.Empty;
                    return true;
                }

                var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                if (method != null)
                {
                    value = Convert.ToString(method.Invoke(instance, Array.Empty<object>())) ?? string.Empty;
                    return true;
                }
            }
            catch
            {
                value = string.Empty;
            }
            return false;
        }

        static bool TryReadInstanceFloatValue(Type type, object instance, string name, out string valueText)
        {
            valueText = null;
            try
            {
                object value;
                var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    value = property.GetValue(instance, null);
                }
                else
                {
                    var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
                    if (method == null) return false;
                    value = method.Invoke(instance, Array.Empty<object>());
                }
                if (value is float floatValue)
                {
                    if (!ProfilerFrameReadPolicy.IsFinite(floatValue)) return false;
                    valueText = floatValue.ToString("0.###", CultureInfo.InvariantCulture);
                    return true;
                }

                if (value is double doubleValue)
                {
                    if (!ProfilerFrameReadPolicy.IsFinite(doubleValue)) return false;
                    valueText = doubleValue.ToString("0.###", CultureInfo.InvariantCulture);
                    return true;
                }
            }
            catch
            {
                valueText = null;
            }

            return false;
        }

        static List<string> CoerceStringList(object value)
        {
            if (value == null)
            {
                return null;
            }

            var names = new List<string>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var enumerable = value as IEnumerable;
            if (enumerable == null)
            {
                var single = Convert.ToString(value);
                AddName(names, seen, single);
                return names.Count == 0 ? null : names;
            }

            foreach (var item in enumerable)
            {
                AddName(names, seen, Convert.ToString(item));
                if (names.Count >= 1000)
                {
                    break;
                }
            }

            return names.Count == 0 ? null : names;
        }

        static void AddName(List<string> names, HashSet<string> seen, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            var bounded = Bounded(value.Trim(), 120);
            if (seen.Add(bounded))
            {
                names.Add(bounded);
            }
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

        static string Bounded(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength);
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            if (value > max)
            {
                return max;
            }

            return value;
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

        static string RequireProjectLocalDirectoryPath(string requiredRoot)
        {
            var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot);
            var fullRoot = Path.GetFullPath(Path.Combine(projectRoot, requiredRoot.Replace('/', Path.DirectorySeparatorChar)));
            if (HasReparsePointInExistingChain(fullRoot, fullRoot))
            {
                throw new ArgumentException("path_not_allowed");
            }

            return fullRoot;
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

        sealed class ProfilerFrameReadResult
        {
            public bool ApiAvailable;
            public string UnityVersion = string.Empty;
            public string Source = string.Empty;
            public string Reason = string.Empty;
            public string SelectionState = ProfilerFrameReadPolicy.Unavailable;
            public int RequestedFrameIndex = -1;
            public int FrameIndex = -1;
            public bool RangeAvailable;
            public int FirstFrameIndex = -1;
            public int LastFrameIndex = -1;
            public bool DataAvailable;
            public bool ThreadCountAvailable;
            public bool FrameRangeRevalidated;
            public int MaxThreads;
            public int MaxMarkers;
            public int ThreadCount = -1;
            public int ScannedThreads;
            public int MatchedMarkers;
            public int IncludedMarkers;
            public bool Truncated;
            public readonly List<ProfilerThreadSnapshot> Threads = new List<ProfilerThreadSnapshot>();
        }

        sealed class ProfilerThreadSnapshot
        {
            public ProfilerThreadSnapshot(int threadIndex, string name)
            {
                ThreadIndex = threadIndex;
                Name = name ?? string.Empty;
            }

            public int ThreadIndex { get; }
            public string Name { get; }
            public int MatchedMarkers;
            public bool Truncated;
            public string Reason = string.Empty;
            public readonly List<ProfilerMarkerSnapshot> Markers = new List<ProfilerMarkerSnapshot>();
        }

        sealed class ProfilerMarkerSnapshot
        {
            public ProfilerMarkerSnapshot(int threadIndex, string threadName, string name, int? calls, string totalMs, string selfMs, int? depth)
            {
                ThreadIndex = threadIndex;
                ThreadName = threadName ?? string.Empty;
                Name = name ?? string.Empty;
                Calls = calls;
                TotalMs = totalMs;
                SelfMs = selfMs;
                Depth = depth;
            }

            public int ThreadIndex { get; }
            public string ThreadName { get; }
            public string Name { get; }
            public int? Calls { get; }
            public string TotalMs { get; }
            public string SelfMs { get; }
            public int? Depth { get; }
        }

        sealed class ProfilerReportSnapshot
        {
            public ProfilerReportSnapshot(
                string reportId,
                string name,
                string summary,
                string report,
                string artifactPath,
                bool artifactPersisted,
                DateTime createdUtc,
                DateTime expiresAtUtc,
                bool truncated,
                bool apiAvailable,
                bool dataAvailable,
                string selectionState,
                string resultState,
                int frameIndex)
            {
                ReportId = reportId;
                Name = name;
                Summary = summary;
                Report = report;
                ArtifactPath = artifactPath;
                ArtifactPersisted = artifactPersisted;
                CreatedUtc = createdUtc;
                ExpiresAtUtc = expiresAtUtc;
                Truncated = truncated;
                ApiAvailable = apiAvailable;
                DataAvailable = dataAvailable;
                SelectionState = selectionState ?? ProfilerFrameReadPolicy.Unavailable;
                ResultState = resultState ?? ProfilerFrameReadPolicy.Unavailable;
                FrameIndex = frameIndex;
            }

            public string ReportId { get; }
            public string Name { get; }
            public string Summary { get; }
            public string Report { get; }
            public string ArtifactPath { get; }
            public bool ArtifactPersisted { get; }
            public DateTime CreatedUtc { get; }
            public DateTime ExpiresAtUtc { get; }
            public bool Truncated { get; }
            public bool ApiAvailable { get; }
            public bool DataAvailable { get; }
            public string SelectionState { get; }
            public string ResultState { get; }
            public int FrameIndex { get; }
        }

        sealed class ProfilerSessionDescriptor
        {
            public ProfilerSessionDescriptor(string sessionId, string kind, string source, string command, string status, string name, string summary, string createdUtc, string contentType, string artifactPath, bool artifactPersisted, string captureArtifactPath, long artifactBytes, int frameIndex, bool apiAvailable, bool captureExported)
            {
                SessionId = sessionId ?? string.Empty;
                Kind = kind ?? string.Empty;
                Source = source ?? string.Empty;
                Command = command ?? string.Empty;
                Status = status ?? string.Empty;
                Name = name ?? string.Empty;
                Summary = summary ?? string.Empty;
                CreatedUtc = createdUtc ?? string.Empty;
                ContentType = contentType ?? string.Empty;
                ArtifactPath = artifactPath ?? string.Empty;
                ArtifactPersisted = artifactPersisted;
                CaptureArtifactPath = captureArtifactPath ?? string.Empty;
                ArtifactBytes = artifactBytes;
                FrameIndex = frameIndex;
                ApiAvailable = apiAvailable;
                CaptureExported = captureExported;
            }

            public string SessionId { get; }
            public string Kind { get; }
            public string Source { get; }
            public string Command { get; }
            public string Status { get; }
            public string Name { get; }
            public string Summary { get; }
            public string CreatedUtc { get; }
            public string ContentType { get; }
            public string ArtifactPath { get; }
            public bool ArtifactPersisted { get; }
            public string CaptureArtifactPath { get; }
            public long ArtifactBytes { get; }
            public int FrameIndex { get; }
            public bool ApiAvailable { get; }
            public bool CaptureExported { get; }

            public string ToJson(bool includeDetails)
            {
                var builder = new StringBuilder();
                builder.Append("{\"session_id\":").Append(JsonUtil.String(SessionId))
                    .Append(",\"kind\":").Append(JsonUtil.String(Kind))
                    .Append(",\"source\":").Append(JsonUtil.String(Source))
                    .Append(",\"command\":").Append(JsonUtil.String(Command))
                    .Append(",\"status\":").Append(JsonUtil.String(Status))
                    .Append(",\"name\":").Append(JsonUtil.String(Name))
                    .Append(",\"summary\":").Append(JsonUtil.String(Summary))
                    .Append(",\"created_utc\":").Append(JsonUtil.String(CreatedUtc))
                    .Append(",\"artifact_path\":").Append(JsonUtil.String(ArtifactPath))
                    .Append(",\"artifact_persisted\":").Append(JsonUtil.Bool(ArtifactPersisted))
                    .Append(",\"capture_artifact_path\":").Append(JsonUtil.String(CaptureArtifactPath))
                    .Append(",\"capture_exported\":").Append(JsonUtil.Bool(CaptureExported));
                if (includeDetails)
                {
                    builder.Append(",\"content_type\":").Append(JsonUtil.String(ContentType))
                        .Append(",\"artifact_bytes\":").Append(Math.Max(0L, ArtifactBytes))
                        .Append(",\"frame_index\":").Append(FrameIndex)
                        .Append(",\"api_available\":").Append(JsonUtil.Bool(ApiAvailable));
                }

                builder.Append('}');
                return builder.ToString();
            }
        }

        sealed class MemorySnapshotCandidate
        {
            public MemorySnapshotCandidate(string artifactPath, long bytes, string contentHash, string command, string createdUtc, string unityVersion, int metricCount, List<string> metricNames, bool metricNamesTruncated)
            {
                ArtifactPath = artifactPath ?? string.Empty;
                Bytes = bytes;
                ContentHash = contentHash ?? string.Empty;
                Command = command ?? string.Empty;
                CreatedUtc = createdUtc ?? string.Empty;
                UnityVersion = unityVersion ?? string.Empty;
                MetricCount = metricCount;
                MetricNames = metricNames ?? new List<string>();
                MetricNamesTruncated = metricNamesTruncated;
            }

            public string ArtifactPath { get; }
            public long Bytes { get; }
            public string ContentHash { get; }
            public string Command { get; }
            public string CreatedUtc { get; }
            public string UnityVersion { get; }
            public int MetricCount { get; }
            public List<string> MetricNames { get; }
            public bool MetricNamesTruncated { get; }

            public string ToJson()
            {
                return "{\"artifact_path\":" + JsonUtil.String(ArtifactPath)
                    + ",\"bytes\":" + Math.Max(0L, Bytes)
                    + ",\"content_hash\":" + JsonUtil.String(ContentHash)
                    + ",\"command\":" + JsonUtil.String(Command)
                    + ",\"created_utc\":" + JsonUtil.String(CreatedUtc)
                    + ",\"unity_version\":" + JsonUtil.String(UnityVersion)
                    + ",\"metric_count\":" + MetricCount
                    + ",\"metric_names\":" + JsonArrayStrings(MetricNames)
                    + ",\"metric_names_truncated\":" + JsonUtil.Bool(MetricNamesTruncated) + "}";
            }
        }

        sealed class MemorySnapshotArtifact
        {
            public MemorySnapshotArtifact(string artifactPath, long bytes, string contentHash, string command, string createdAt, string createdUtc, string unityVersion, string content)
            {
                ArtifactPath = artifactPath ?? string.Empty;
                Bytes = bytes;
                ContentHash = contentHash ?? string.Empty;
                Command = command ?? string.Empty;
                CreatedAt = createdAt ?? string.Empty;
                CreatedUtc = createdUtc ?? string.Empty;
                UnityVersion = unityVersion ?? string.Empty;
                Content = content ?? string.Empty;
            }

            public string ArtifactPath { get; }
            public long Bytes { get; }
            public string ContentHash { get; }
            public string Command { get; }
            public string CreatedAt { get; }
            public string CreatedUtc { get; }
            public string UnityVersion { get; }
            public string Content { get; }
        }

        sealed class MemorySnapshotMetricDelta
        {
            public MemorySnapshotMetricDelta(string field, bool leftHasValue, double leftValue, bool rightHasValue, double rightValue)
            {
                Field = field ?? string.Empty;
                LeftHasValue = leftHasValue;
                LeftValue = leftValue;
                RightHasValue = rightHasValue;
                RightValue = rightValue;
            }

            public string Field { get; }
            public bool LeftHasValue { get; }
            public double LeftValue { get; }
            public bool RightHasValue { get; }
            public double RightValue { get; }
            public double Delta { get { return RightValue - LeftValue; } }
            public bool HasDeltaRatio { get { return LeftHasValue && RightHasValue && Math.Abs(LeftValue) > 0.000001d; } }
            public double DeltaRatio { get { return HasDeltaRatio ? Delta / Math.Abs(LeftValue) : 0d; } }
            public bool Changed { get { return LeftHasValue != RightHasValue || (LeftHasValue && RightHasValue && Math.Abs(Delta) > 0.000001d); } }
        }
    }
}
