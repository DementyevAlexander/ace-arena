using System;
using System.Globalization;
using System.IO;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.UI;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class McpWindowPerfCommands
    {
        const string FeatureFlag = "sgg_mcp_diagnostics";
        const string ArtifactRoot = "Library/SGGUnityMcp/job-artifacts/mcp-window-perf/";
        const string StatusSchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string CaptureSchemaJson = "{\"type\":\"object\",\"properties\":{\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_capture\":{\"type\":\"boolean\",\"default\":false},\"open_if_missing\":{\"type\":\"boolean\",\"default\":false},\"tab\":{\"type\":\"string\",\"maxLength\":40},\"refresh_count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10},\"probe_health\":{\"type\":\"boolean\",\"default\":false},\"health_only\":{\"type\":\"boolean\",\"default\":false},\"write_artifact\":{\"type\":\"boolean\",\"default\":true}},\"additionalProperties\":false}";
        const string ScrollBenchmarkSchemaJson = "{\"type\":\"object\",\"properties\":{\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_scroll\":{\"type\":\"boolean\",\"default\":false},\"open_if_missing\":{\"type\":\"boolean\",\"default\":false},\"tab\":{\"type\":\"string\",\"maxLength\":40},\"steps\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200},\"delta_pixels\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000},\"restore_scroll\":{\"type\":\"boolean\",\"default\":true},\"refresh_diagnostics_each_step\":{\"type\":\"boolean\",\"default\":false},\"write_artifact\":{\"type\":\"boolean\",\"default\":true}},\"additionalProperties\":false}";
        const string ReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"artifact_path\":{\"type\":\"string\",\"maxLength\":512},\"max_chars\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":65536}},\"additionalProperties\":false}";
        const string StatusOutputLimitJson = "{\"max_fields\":40,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/mcp-window-perf\"}";
        const string CaptureOutputLimitJson = "{\"dry_run_default\":true,\"requires_confirm_capture\":true,\"max_refresh_count\":10,\"storage\":\"project_artifact\",\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/mcp-window-perf\"}";
        const string ScrollOutputLimitJson = "{\"dry_run_default\":true,\"requires_confirm_scroll\":true,\"max_steps\":200,\"max_delta_pixels\":2000,\"storage\":\"project_artifact\",\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/mcp-window-perf\"}";
        const string ReportOutputLimitJson = "{\"max_chars\":65536,\"storage\":\"project_artifact\",\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/mcp-window-perf\"}";
        const int MaxRefreshCount = 10;
        const int MaxScrollSteps = 200;
        const int MaxDeltaPixels = 2000;
        const int MaxReportChars = 65536;

        [McpCommand(
            "mcp_window.perf.status",
            Title = "MCP Window perf status",
            Description = "Read bounded MCP Window open state, current tab and latest internal timing summary",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = StatusSchemaJson,
            Tags = new string[] { "mcp_window", "performance", "diagnostics" },
            Capability = "mcp_window_perf",
            ResultShape = "status",
            OutputLimitJson = StatusOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Status()
        {
            var hasWindow = UnityMcpSetupWindow.TryGetOpenWindow(out var window, out var openWindowCount);
            var json = new StringBuilder();
            json.Append("{\"open\":").Append(JsonUtil.Bool(hasWindow));
            json.Append(",\"open_window_count\":").Append(openWindowCount.ToString(CultureInfo.InvariantCulture));
            json.Append(",\"unity_version\":").Append(JsonUtil.String(Application.unityVersion));
            json.Append(",\"editor\":").Append(EditorStatusJson());
            json.Append(",\"measurement_source\":\"mcp_window_internal_stopwatch\"");
            json.Append(",\"artifact_root\":").Append(JsonUtil.String(ArtifactRoot.TrimEnd('/')));
            if (hasWindow && window != null)
            {
                var healthProbe = window.LastHealthProbeForCommand();
                json.Append(",\"current_tab\":").Append(JsonUtil.String(window.CurrentTabForCommand()));
                json.Append(",\"last_gateway_health_probed\":").Append(JsonUtil.Bool(healthProbe?.HealthProbed == true));
                json.Append(",\"last_gateway_health_ok\":").Append(JsonUtil.Bool(healthProbe?.HealthOk == true));
                json.Append(",\"last_gateway_health_message\":").Append(JsonUtil.String(window.LastHealthProbeMessageForCommand()));
                json.Append(",\"gui_ready\":").Append(JsonUtil.Bool(window.IsGuiReadyForCommand()));
                json.Append(",\"main_scroll_available\":").Append(JsonUtil.Bool(window.HasMainScrollViewForCommand()));
                json.Append(",\"header_shortcut_bar_visible\":").Append(JsonUtil.Bool(window.HeaderShortcutBarVisibleForCommand()));
                json.Append(",\"health_refresh_shortcut_visible\":").Append(JsonUtil.Bool(window.HealthRefreshShortcutVisibleForCommand()));
                json.Append(",\"ftue_tab_visible\":").Append(JsonUtil.Bool(window.FtueTabVisibleForCommand()));
                json.Append(",\"ftue_complete\":").Append(JsonUtil.Bool(window.FtueCompleteForCommand()));
                json.Append(",\"ftue_smoke_test_ready\":").Append(JsonUtil.Bool(window.FtueSmokeTestReadyForCommand()));
                json.Append(",\"ftue_client_config_ready\":").Append(JsonUtil.Bool(window.FtueClientConfigReadyForCommand()));
                json.Append(",\"ftue_permission_profile_ready\":").Append(JsonUtil.Bool(window.FtuePermissionProfileReadyForCommand()));
                json.Append(",\"scroll_offset_y\":").Append(Number(window.MainScrollOffsetForCommand().y));
                json.Append(",\"latest_timing_summary\":").Append(JsonUtil.String(window.LatestTimingSummaryForCommand()));
            }
            else
            {
                json.Append(",\"current_tab\":\"\"");
                json.Append(",\"last_gateway_health_probed\":false");
                json.Append(",\"last_gateway_health_ok\":false");
                json.Append(",\"last_gateway_health_message\":\"\"");
                json.Append(",\"gui_ready\":false");
                json.Append(",\"main_scroll_available\":false");
                json.Append(",\"header_shortcut_bar_visible\":false");
                json.Append(",\"health_refresh_shortcut_visible\":false");
                json.Append(",\"ftue_tab_visible\":false");
                json.Append(",\"ftue_complete\":false");
                json.Append(",\"ftue_smoke_test_ready\":false");
                json.Append(",\"ftue_client_config_ready\":false");
                json.Append(",\"ftue_permission_profile_ready\":false");
                json.Append(",\"scroll_offset_y\":0");
                json.Append(",\"latest_timing_summary\":\"\"");
                json.Append(",\"hint\":\"Open SGG/Unity MCP/MCP Window or call an approved dry_run:false command with open_if_missing:true.\"");
            }

            json.Append('}');
            return CommandExecutionResult.Ok(json.ToString());
        }

        [McpCommand(
            "mcp_window.perf.capture",
            Title = "Capture MCP Window timing",
            Description = "Dry-run-first bounded MCP Window refresh timing capture using internal Stopwatch summaries",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = CaptureSchemaJson,
            Tags = new string[] { "mcp_window", "performance", "diagnostics" },
            Capability = "mcp_window_perf",
            ResultShape = "artifact",
            OutputLimitJson = CaptureOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Capture(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var confirmCapture = JsonUtil.ExtractBool(context.ArgsJson, "confirm_capture", false);
            var openIfMissing = JsonUtil.ExtractBool(context.ArgsJson, "open_if_missing", false);
            var tab = NormalizeTab(JsonUtil.ExtractString(context.ArgsJson, "tab"), "Diagnostics");
            var refreshCount = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "refresh_count", 1), 1, MaxRefreshCount);
            var probeHealth = JsonUtil.ExtractBool(context.ArgsJson, "probe_health", false);
            var healthOnly = JsonUtil.ExtractBool(context.ArgsJson, "health_only", false);
            var writeArtifact = JsonUtil.ExtractBool(context.ArgsJson, "write_artifact", true);

            if (dryRun)
            {
                return CommandExecutionResult.Ok(DryRunJson("mcp_window.perf.capture", "confirm_capture", openIfMissing, tab, refreshCount, probeHealth, healthOnly));
            }

            if (!confirmCapture)
            {
                return CommandExecutionResult.Error("confirmation_required", "mcp_window.perf.capture requires confirm_capture:true when dry_run:false", "{\"retry_args\":{\"dry_run\":false,\"confirm_capture\":true}}" );
            }

            var window = ResolveWindow(openIfMissing, tab, out var openedWindow, out var openWindowCount);
            if (window == null)
            {
                return CommandExecutionResult.Error("mcp_window_not_open", "MCP Window is not open and open_if_missing was false", "{\"open_window_count\":" + openWindowCount.ToString(CultureInfo.InvariantCulture) + ",\"retry_args\":{\"open_if_missing\":true,\"dry_run\":false,\"confirm_capture\":true}}" );
            }

            window.SelectTabForCommand(tab);
            var samples = new StringBuilder();
            long totalOuterMs = 0;
            for (var index = 0; index < refreshCount; index++)
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var summary = healthOnly ? window.RefreshHealthForCommand() : window.RefreshAllForCommand(probeHealth);
                watch.Stop();
                totalOuterMs += watch.ElapsedMilliseconds;
                if (samples.Length > 0)
                {
                    samples.Append(',');
                }

                samples.Append("{\"iteration\":").Append((index + 1).ToString(CultureInfo.InvariantCulture));
                samples.Append(",\"outer_duration_ms\":").Append(watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
                samples.Append(",\"timing_summary\":").Append(JsonUtil.String(summary));
                samples.Append('}');
            }

            var artifactPath = writeArtifact ? NextArtifactPath("capture") : string.Empty;
            var json = "{\"command\":\"mcp_window.perf.capture\""
                + ",\"dry_run\":false"
                + ",\"opened_window\":" + JsonUtil.Bool(openedWindow)
                + ",\"open_window_count_before\":" + openWindowCount.ToString(CultureInfo.InvariantCulture)
                + ",\"tab\":" + JsonUtil.String(tab)
                + ",\"refresh_count\":" + refreshCount.ToString(CultureInfo.InvariantCulture)
                + ",\"probe_health\":" + JsonUtil.Bool(probeHealth)
                + ",\"health_only\":" + JsonUtil.Bool(healthOnly)
                + ",\"measurement_source\":\"mcp_window_internal_stopwatch\""
                + ",\"outer_total_ms\":" + totalOuterMs.ToString(CultureInfo.InvariantCulture)
                + ",\"latest_timing_summary\":" + JsonUtil.String(window.LatestTimingSummaryForCommand())
                + ",\"samples\":[" + samples + "]"
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + "}";
            if (writeArtifact)
            {
                WriteArtifact(artifactPath, json);
            }

            return CommandExecutionResult.Ok(json);
        }

        [McpCommand(
            "mcp_window.scroll.benchmark",
            Title = "Benchmark MCP Window scroll",
            Description = "Dry-run-first bounded synthetic MCP Window ScrollView offset benchmark with optional artifact output",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = ScrollBenchmarkSchemaJson,
            Tags = new string[] { "mcp_window", "performance", "diagnostics", "scroll" },
            Capability = "mcp_window_perf",
            ResultShape = "artifact",
            OutputLimitJson = ScrollOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ScrollBenchmark(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var confirmScroll = JsonUtil.ExtractBool(context.ArgsJson, "confirm_scroll", false);
            var openIfMissing = JsonUtil.ExtractBool(context.ArgsJson, "open_if_missing", false);
            var tab = NormalizeTab(JsonUtil.ExtractString(context.ArgsJson, "tab"), "Diagnostics");
            var steps = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "steps", 40), 1, MaxScrollSteps);
            var deltaPixels = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "delta_pixels", 320), 1, MaxDeltaPixels);
            var restoreScroll = JsonUtil.ExtractBool(context.ArgsJson, "restore_scroll", true);
            var refreshDiagnosticsEachStep = JsonUtil.ExtractBool(context.ArgsJson, "refresh_diagnostics_each_step", false);
            var writeArtifact = JsonUtil.ExtractBool(context.ArgsJson, "write_artifact", true);

            if (dryRun)
            {
                return CommandExecutionResult.Ok(DryRunJson("mcp_window.scroll.benchmark", "confirm_scroll", openIfMissing, tab, steps, false, false));
            }

            if (!confirmScroll)
            {
                return CommandExecutionResult.Error("confirmation_required", "mcp_window.scroll.benchmark requires confirm_scroll:true when dry_run:false", "{\"retry_args\":{\"dry_run\":false,\"confirm_scroll\":true}}" );
            }

            var window = ResolveWindow(openIfMissing, tab, out var openedWindow, out var openWindowCount);
            if (window == null)
            {
                return CommandExecutionResult.Error("mcp_window_not_open", "MCP Window is not open and open_if_missing was false", "{\"open_window_count\":" + openWindowCount.ToString(CultureInfo.InvariantCulture) + ",\"retry_args\":{\"open_if_missing\":true,\"dry_run\":false,\"confirm_scroll\":true}}" );
            }

            window.SelectTabForCommand(tab);
            var result = window.RunScrollBenchmarkForCommand(steps, deltaPixels, restoreScroll, refreshDiagnosticsEachStep);
            var artifactPath = writeArtifact ? NextArtifactPath("scroll") : string.Empty;
            var json = ScrollResultJson(result, openedWindow, openWindowCount, tab, artifactPath);
            if (writeArtifact)
            {
                WriteArtifact(artifactPath, json);
            }

            return CommandExecutionResult.Ok(json);
        }

        [McpCommand(
            "mcp_window.perf.report",
            Title = "Read MCP Window perf report",
            Description = "Read the latest or selected project-local MCP Window perf artifact",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReportSchemaJson,
            Tags = new string[] { "mcp_window", "performance", "diagnostics", "artifact" },
            Capability = "mcp_window_perf",
            ResultShape = "object_details",
            OutputLimitJson = ReportOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult Report(McpCommandContext context)
        {
            var requestedPath = JsonUtil.ExtractString(context.ArgsJson, "artifact_path");
            var artifactPath = string.IsNullOrWhiteSpace(requestedPath) ? LatestArtifactPath() : NormalizeArtifactPath(requestedPath);
            if (string.IsNullOrEmpty(artifactPath))
            {
                return CommandExecutionResult.Ok("{\"available\":false,\"artifact_path\":\"\",\"artifact_root\":" + JsonUtil.String(ArtifactRoot.TrimEnd('/')) + ",\"reason\":\"no MCP Window perf artifact found\"}");
            }

            string fullPath;
            try
            {
                fullPath = RequireProjectLocalFilePath(artifactPath, ArtifactRoot);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("path_not_allowed", "artifact_path must stay under Library/SGGUnityMcp/job-artifacts/mcp-window-perf");
            }

            if (!File.Exists(fullPath))
            {
                return CommandExecutionResult.Error("asset_not_found", "MCP Window perf artifact was not found");
            }

            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 20000), 1024, MaxReportChars);
            var content = File.ReadAllText(fullPath);
            var truncated = content.Length > maxChars;
            if (truncated)
            {
                content = content.Substring(0, maxChars);
            }

            return CommandExecutionResult.Ok("{\"available\":true,\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"max_chars\":" + maxChars.ToString(CultureInfo.InvariantCulture)
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"content\":" + JsonUtil.String(content) + "}");
        }

        static UnityMcpSetupWindow ResolveWindow(bool openIfMissing, string tab, out bool openedWindow, out int openWindowCount)
        {
            openedWindow = false;
            if (UnityMcpSetupWindow.TryGetOpenWindow(out var window, out openWindowCount) && window != null)
            {
                return window;
            }

            if (!openIfMissing)
            {
                return null;
            }

            openedWindow = true;
            window = UnityMcpSetupWindow.OpenForCommand(tab);
            return window;
        }

        static string DryRunJson(string command, string confirmationFlag, bool openIfMissing, string tab, int count, bool probeHealth, bool healthOnly)
        {
            return "{\"command\":" + JsonUtil.String(command)
                + ",\"dry_run\":true"
                + ",\"would_execute\":true"
                + ",\"open_if_missing\":" + JsonUtil.Bool(openIfMissing)
                + ",\"tab\":" + JsonUtil.String(tab)
                + ",\"bounded\":true"
                + ",\"count\":" + count.ToString(CultureInfo.InvariantCulture)
                + ",\"probe_health\":" + JsonUtil.Bool(probeHealth)
                + ",\"health_only\":" + JsonUtil.Bool(healthOnly)
                + ",\"artifact_root\":" + JsonUtil.String(ArtifactRoot.TrimEnd('/'))
                + ",\"next_valid_actions\":[\"repeat with dry_run:false and " + confirmationFlag + ":true after operator approval\"]"
                + ",\"retry_args\":{\"dry_run\":false," + JsonUtil.String(confirmationFlag) + ":true}"
                + "}";
        }

        static string ScrollResultJson(UnityMcpSetupWindow.McpWindowScrollBenchmarkResult result, bool openedWindow, int openWindowCount, string tab, string artifactPath)
        {
            return "{\"command\":\"mcp_window.scroll.benchmark\""
                + ",\"dry_run\":false"
                + ",\"opened_window\":" + JsonUtil.Bool(openedWindow)
                + ",\"open_window_count_before\":" + openWindowCount.ToString(CultureInfo.InvariantCulture)
                + ",\"tab\":" + JsonUtil.String(tab)
                + ",\"available\":" + JsonUtil.Bool(result.Available)
                + ",\"reason\":" + JsonUtil.String(result.Reason)
                + ",\"measurement_source\":\"synthetic_scroll_offset_stopwatch\""
                + ",\"steps_requested\":" + result.StepsRequested.ToString(CultureInfo.InvariantCulture)
                + ",\"steps_completed\":" + result.StepsCompleted.ToString(CultureInfo.InvariantCulture)
                + ",\"delta_pixels\":" + Number(result.DeltaPixels)
                + ",\"restore_scroll\":" + JsonUtil.Bool(result.Restored)
                + ",\"refresh_diagnostics_each_step\":" + JsonUtil.Bool(result.RefreshDiagnosticsEachStep)
                + ",\"repaint_requested\":" + JsonUtil.Bool(result.RepaintRequested)
                + ",\"total_ms\":" + result.TotalMs.ToString(CultureInfo.InvariantCulture)
                + ",\"min_step_ms\":" + result.MinStepMs.ToString(CultureInfo.InvariantCulture)
                + ",\"max_step_ms\":" + result.MaxStepMs.ToString(CultureInfo.InvariantCulture)
                + ",\"average_step_ms\":" + Number(result.AverageStepMs)
                + ",\"original_scroll_y\":" + Number(result.OriginalScrollY)
                + ",\"final_scroll_y\":" + Number(result.FinalScrollY)
                + ",\"timing_summary\":" + JsonUtil.String(result.TimingSummary)
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + "}";
        }

        static string EditorStatusJson()
        {
            return "{\"is_playing\":" + JsonUtil.Bool(EditorApplication.isPlaying)
                + ",\"is_playing_or_will_change_playmode\":" + JsonUtil.Bool(EditorApplication.isPlayingOrWillChangePlaymode)
                + ",\"is_paused\":" + JsonUtil.Bool(EditorApplication.isPaused)
                + "}";
        }

        static string NormalizeTab(string tab, string defaultTab)
        {
            if (string.IsNullOrWhiteSpace(tab))
            {
                return defaultTab;
            }

            var normalized = tab.Trim();
            if (string.Equals(normalized, "Setup", StringComparison.OrdinalIgnoreCase)) return "Setup";
            if (string.Equals(normalized, "Status", StringComparison.OrdinalIgnoreCase)) return "Status";
            if (string.Equals(normalized, "Permissions", StringComparison.OrdinalIgnoreCase)) return "Permissions";
            if (string.Equals(normalized, "Commands", StringComparison.OrdinalIgnoreCase)) return "Commands";
            if (string.Equals(normalized, "Plans", StringComparison.OrdinalIgnoreCase)) return "Plans";
            if (string.Equals(normalized, "Skills", StringComparison.OrdinalIgnoreCase)) return "Skills";
            if (string.Equals(normalized, "Unity AI", StringComparison.OrdinalIgnoreCase) || string.Equals(normalized, "Unity AI Assistant", StringComparison.OrdinalIgnoreCase)) return "Unity AI Assistant";
            if (string.Equals(normalized, "Diagnostics", StringComparison.OrdinalIgnoreCase)) return "Diagnostics";
            return defaultTab;
        }

        static string NextArtifactPath(string prefix)
        {
            return ArtifactRoot + prefix + "-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssfffZ", CultureInfo.InvariantCulture) + ".json";
        }

        static void WriteArtifact(string artifactPath, string content)
        {
            var fullPath = RequireProjectLocalFilePath(artifactPath, ArtifactRoot);
            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(fullPath, content, Encoding.UTF8);
        }

        static string LatestArtifactPath()
        {
            var root = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, ArtifactRoot.Replace('/', Path.DirectorySeparatorChar)));
            if (!Directory.Exists(root))
            {
                return string.Empty;
            }

            var files = Directory.GetFiles(root, "*.json", SearchOption.TopDirectoryOnly);
            string latest = string.Empty;
            var latestTime = DateTime.MinValue;
            for (var index = 0; index < files.Length; index++)
            {
                var writeTime = File.GetLastWriteTimeUtc(files[index]);
                if (writeTime > latestTime)
                {
                    latestTime = writeTime;
                    latest = files[index];
                }
            }

            if (string.IsNullOrEmpty(latest))
            {
                return string.Empty;
            }

            return ArtifactRoot + Path.GetFileName(latest);
        }

        static string NormalizeArtifactPath(string artifactPath)
        {
            if (string.IsNullOrWhiteSpace(artifactPath) || artifactPath.IndexOf('\0') >= 0)
            {
                return string.Empty;
            }

            var normalized = artifactPath.Replace('\\', '/').Trim();
            if (normalized.StartsWith("/", StringComparison.Ordinal) || normalized.StartsWith("//", StringComparison.Ordinal) || IsDriveAbsolute(normalized))
            {
                return string.Empty;
            }

            if (normalized.IndexOf(':') >= 0 || normalized.Contains("../") || normalized == ".." || normalized.Contains("/.."))
            {
                return string.Empty;
            }

            if (!normalized.StartsWith(ArtifactRoot, StringComparison.Ordinal))
            {
                normalized = ArtifactRoot + normalized.TrimStart('/');
            }

            return normalized.StartsWith(ArtifactRoot, StringComparison.Ordinal) ? normalized : string.Empty;
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

        static int Clamp(int value, int min, int max)
        {
            return Math.Max(min, Math.Min(max, value));
        }

        static string Number(double value)
        {
            return value.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
