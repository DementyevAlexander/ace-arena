using System;
using System.Collections.Generic;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class BuiltInCommands
    {
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string CompileStatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"response_mode\":{\"type\":\"string\",\"enum\":[\"full\",\"compact\"],\"default\":\"full\"}},\"additionalProperties\":false}";
        const string CompileWaitReadySchemaJson = "{\"type\":\"object\",\"properties\":{\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":300000},\"poll_interval_ms\":{\"type\":\"integer\",\"minimum\":50,\"maximum\":5000},\"response_mode\":{\"type\":\"string\",\"enum\":[\"full\",\"compact\"],\"default\":\"full\"}},\"additionalProperties\":false}";
        const string CompileRefreshAndWaitSchemaJson = "{\"type\":\"object\",\"properties\":{\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":295000},\"poll_interval_ms\":{\"type\":\"integer\",\"minimum\":50,\"maximum\":5000},\"allow_unsaved_scene_changes\":{\"type\":\"boolean\",\"default\":false},\"response_mode\":{\"type\":\"string\",\"enum\":[\"full\",\"compact\"],\"default\":\"full\"},\"expected_script_paths\":{\"type\":\"array\",\"maxItems\":64,\"uniqueItems\":true,\"description\":\"Exact Unity-relative C# script paths under Assets/. Local or Embedded package scripts use package.local_script.import.\",\"items\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512,\"pattern\":\"^Assets/.+[.][cC][sS]$\"}}},\"additionalProperties\":false}";
        const string ValidationAfterEditSchemaJson = "{\"type\":\"object\",\"properties\":{\"timeout_ms\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":300000},\"poll_interval_ms\":{\"type\":\"integer\",\"minimum\":50,\"maximum\":5000},\"allow_unsaved_scene_changes\":{\"type\":\"boolean\",\"default\":false}},\"additionalProperties\":false}";
        const string CompileErrorsRecentSchemaJson = "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"default\":20},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100,\"description\":\"Compatibility alias for limit; do not send both.\"},\"max_message_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":8000},\"max_stack_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":12000}},\"additionalProperties\":false}";
        const string CompileWarningsRecentSchemaJson = "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"default\":50},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200,\"description\":\"Compatibility alias for limit; do not send both.\"},\"max_message_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":8000},\"max_stack_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":12000}},\"additionalProperties\":false}";
        const string AssetsRefreshSchemaJson = "{\"type\":\"object\",\"properties\":{\"allow_unsaved_scene_changes\":{\"type\":\"boolean\",\"default\":false}},\"additionalProperties\":false}";
        const string AssetImportSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"minLength\":1,\"pattern\":\"^Assets[\\\\\\\\/].+\",\"description\":\"One safe Unity-relative path under Assets/. Any asset file extension is accepted. Eligible Local or Embedded package C# files use package.local_script.import.\"},\"dry_run\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"asset_path\"],\"additionalProperties\":false}";
        const string ScenePathSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"}},\"required\":[\"scene_path\"],\"additionalProperties\":false}";
        const string SceneOpenSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"allow_unsaved_scene_changes\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"scene_path\"],\"additionalProperties\":false}";
        const string ConsoleLogReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"types\":{\"type\":\"string\",\"description\":\"Comma-separated all,error,warning,log,exception,assert\"},\"filter_text\":{\"type\":\"string\",\"maxLength\":256},\"include_stack_trace\":{\"type\":\"boolean\"},\"max_message_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":8000},\"max_stack_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":12000}},\"additionalProperties\":false}";
        const string ConsoleLogQuerySchemaJson = "{\"type\":\"object\",\"properties\":{\"severity\":{\"type\":\"string\",\"description\":\"Comma-separated all,error,warning,log,exception,assert\",\"maxLength\":128},\"types\":{\"type\":\"string\",\"description\":\"Compatibility alias for severity\",\"maxLength\":128},\"contains\":{\"type\":\"string\",\"maxLength\":256},\"filter_text\":{\"type\":\"string\",\"description\":\"Compatibility alias for contains\",\"maxLength\":256},\"since\":{\"type\":\"string\",\"description\":\"Optional ISO-8601 UTC timestamp lower bound\",\"maxLength\":64},\"since_utc\":{\"type\":\"string\",\"description\":\"Compatibility alias for since\",\"maxLength\":64},\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500,\"default\":50},\"include_stack_trace\":{\"type\":\"boolean\",\"default\":false},\"max_chars\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":100000}},\"additionalProperties\":false}";
        const string BridgeReloadTraceSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_lines\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"max_chars\":{\"type\":\"integer\",\"minimum\":1024,\"maximum\":262144}},\"additionalProperties\":false}";
        const string ConsoleLogOutputLimitJson = "{\"max_results\":1000}";
        const string ConsoleLogReadOutputLimitJson = "{\"max_results\":1000,\"max_message_chars\":8000,\"max_stack_chars\":12000}";
        const string ConsoleLogQueryOutputLimitJson = "{\"max_results\":500,\"max_chars\":100000}";
        const string BridgeReloadTraceOutputLimitJson = "{\"max_lines\":500,\"max_chars\":262144}";

        [McpCommand(
            "compile.status",
            Title = "Compile status",
            Description = "Read Unity compilation status",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CompileStatusSchemaJson,
            ResultShape = "status")]
        public static CommandExecutionResult CompileStatus(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(CompileStatusJson(context.State, false, 0, 0));
        }

        [McpCommand(
            "compile.wait_ready",
            Title = "Wait for Unity ready",
            Description = "Poll until Unity is not compiling, updating, or playing, with a bounded timeout, and surface compiler errors.",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 300000,
            SchemaJson = CompileWaitReadySchemaJson,
            Tags = new string[] { "compile", "status" },
            ResultShape = "status")]
        public static CommandExecutionResult CompileWaitReady(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(CompileStatusJson(context.State, false, 0, 0));
        }

        [McpCommand(
            "compile.refresh_and_wait",
            Title = "Refresh assets and wait for compile",
            Description = "Refresh Unity AssetDatabase after dirty-scene and idle-Editor preflight, request incremental script compilation, then wait for strict compile readiness.",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 295000,
            SchemaJson = CompileRefreshAndWaitSchemaJson,
            Tags = new string[] { "compile", "status", "asset" },
            ResultShape = "status")]
        public static CommandExecutionResult CompileRefreshAndWait(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(CompileStatusJson(context.State, false, 0, 0));
        }

        [McpCommand(
            "compile.errors.recent",
            Title = "Recent compiler errors",
            Description = "Read bounded recent compiler errors captured from CompilationPipeline and console error logs.",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CompileErrorsRecentSchemaJson,
            Tags = new string[] { "compile", "status", "logs" },
            ResultShape = "bounded_list",
            OutputLimitJson = "{\"max_results\":100,\"max_message_chars\":8000,\"max_stack_chars\":12000}")]
        public static CommandExecutionResult CompileErrorsRecent(McpCommandContext context)
        {
            if (JsonUtil.HasProperty(context.ArgsJson, "limit") && JsonUtil.HasProperty(context.ArgsJson, "max_results"))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide only one of limit or max_results");
            }

            var limit = JsonUtil.ExtractInt(context.ArgsJson, JsonUtil.HasProperty(context.ArgsJson, "max_results") ? "max_results" : "limit", 20);
            var maxMessageChars = JsonUtil.ExtractInt(context.ArgsJson, "max_message_chars", 4000);
            var maxStackChars = JsonUtil.ExtractInt(context.ArgsJson, "max_stack_chars", 8000);
            return CommandExecutionResult.Ok(ConsoleLogBuffer.CompileDiagnosticsJson(limit, maxMessageChars, maxStackChars));
        }

        [McpCommand(
            "compile.warnings.recent",
            Title = "Recent compiler warnings",
            Description = "Read bounded recent compiler warnings captured from CompilationPipeline and console warning logs.",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = CompileWarningsRecentSchemaJson,
            Tags = new string[] { "compile", "status", "logs", "warnings" },
            ResultShape = "bounded_list",
            OutputLimitJson = "{\"max_results\":200,\"max_message_chars\":8000,\"max_stack_chars\":12000}")]
        public static CommandExecutionResult CompileWarningsRecent(McpCommandContext context)
        {
            if (JsonUtil.HasProperty(context.ArgsJson, "limit") && JsonUtil.HasProperty(context.ArgsJson, "max_results"))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Provide only one of limit or max_results");
            }

            var limit = JsonUtil.ExtractInt(context.ArgsJson, JsonUtil.HasProperty(context.ArgsJson, "max_results") ? "max_results" : "limit", 50);
            var maxMessageChars = JsonUtil.ExtractInt(context.ArgsJson, "max_message_chars", 4000);
            var maxStackChars = JsonUtil.ExtractInt(context.ArgsJson, "max_stack_chars", 8000);
            return CommandExecutionResult.Ok(ConsoleLogBuffer.CompileWarningsJson(limit, maxMessageChars, maxStackChars));
        }

        [McpCommand(
            "diagnostics.bridge_reload_trace",
            Title = "Bridge reload trace",
            Description = "Read bounded project-local bridge reload trace events for diagnosing Unity domain reload and Package Manager singleton timing",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = BridgeReloadTraceSchemaJson,
            Tags = new string[] { "diagnostics", "reload", "logs" },
            ResultShape = "bounded_list",
            OutputLimitJson = BridgeReloadTraceOutputLimitJson)]
        public static CommandExecutionResult DiagnosticsBridgeReloadTrace(McpCommandContext context)
        {
            var maxLines = JsonUtil.ExtractInt(context.ArgsJson, "max_lines", 100);
            var maxChars = JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 65536);
            return CommandExecutionResult.Ok(BridgeReloadTrace.ReadRecentJson(maxLines, maxChars));
        }

        [McpCommand(
            "validation.preflight",
            Title = "Validation preflight",
            Description = "Read strict editor, compile, and dirty-scene readiness before edits or tests.",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "validation", "compile", "status" },
            ResultShape = "status")]
        public static CommandExecutionResult ValidationPreflight(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(ValidationStatusJson(context.State, "preflight", false, false, 0, 0));
        }

        [McpCommand(
            "validation.after_edit",
            Title = "Validation after edit",
            Description = "Refresh AssetDatabase after external edits, then wait for strict compile readiness.",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 305000,
            SchemaJson = ValidationAfterEditSchemaJson,
            Tags = new string[] { "validation", "compile", "asset" },
            ResultShape = "status")]
        public static CommandExecutionResult ValidationAfterEdit(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(ValidationStatusJson(context.State, "after_edit", false, false, 0, 0));
        }

        public static string CompileStatusJson(BridgeRuntimeState state, bool timedOut, int waitedMs, int timeoutMs, string expectedBarrierOperationId = "", bool requireExplicitBarrier = false)
        {
            return CompileStatusJson(state, timedOut, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, state == null ? null : state.CapturePackageLifecycleSnapshot());
        }

        internal static string CompileStatusJson(BridgeRuntimeState state, bool timedOut, int waitedMs, int timeoutMs, string expectedBarrierOperationId, bool requireExplicitBarrier, PackageLifecycleSnapshot lifecycle, bool additionalReadinessVerified = true)
        {
            var hasCompileErrors = ConsoleLogBuffer.HasCompileErrors();
            var editorReady = lifecycle != null && lifecycle.IsReady;
            var compileBarrier = ExplicitCompilationBarrier.Snapshot(expectedBarrierOperationId, requireExplicitBarrier);
            var terminalCompileFailureWithoutReload = compileBarrier.TerminalCompileFailureWithoutReload;
            var lifecycleSettled = compileBarrier.Stable && !compileBarrier.Unavailable;
            var packageRefreshRequired = lifecycle != null && lifecycle.PackageRefreshRequired;
            var packageRefreshWaitCanAdvance = lifecycle != null && lifecycle.PackageRefreshWaitCanAdvance;
            var stalePackageResolution = lifecycle != null && lifecycle.StalePackageResolution;
            var packageMetadataUnavailable = lifecycle != null && lifecycle.PackageMetadataUnavailable;
            var backgroundCompilerProgress = lifecycle == null || lifecycle.BackgroundCompilerProgress == null
                ? BackgroundCompilerProgressSnapshot.Unobserved
                : lifecycle.BackgroundCompilerProgress;
            var backgroundCompilerBlocked = backgroundCompilerProgress.BlocksReadiness;
            var editorStatus = lifecycle == null ? "unknown" : lifecycle.Status;
            var status = !additionalReadinessVerified ? "expected_script_not_imported"
                : packageRefreshRequired ? (packageMetadataUnavailable ? "package_metadata_unavailable" : stalePackageResolution ? "stale_package_resolution" : "package_refresh_required")
                : backgroundCompilerProgress.Failed ? "background_compilation_failed"
                : backgroundCompilerProgress.Observed && !backgroundCompilerProgress.Authoritative ? "background_compilation_unavailable"
                : backgroundCompilerBlocked ? "background_compilation_pending"
                : compileBarrier.Unavailable ? "compile_freshness_unavailable"
                : terminalCompileFailureWithoutReload && editorReady ? "compilation_failed"
                : compileBarrier.Settling ? "settling"
                : (hasCompileErrors && editorReady ? "compilation_failed" : editorStatus);
            return "{\"is_compiling\":" + JsonUtil.Bool(lifecycle != null && lifecycle.IsCompiling)
                + ",\"is_updating\":" + JsonUtil.Bool(lifecycle != null && lifecycle.IsUpdating)
                + ",\"is_playing\":" + JsonUtil.Bool(lifecycle != null && lifecycle.IsPlaying)
                + ",\"ready\":" + JsonUtil.Bool(editorReady && lifecycleSettled && !hasCompileErrors && !packageRefreshRequired && !backgroundCompilerBlocked && additionalReadinessVerified)
                + ",\"editor_ready\":" + JsonUtil.Bool(editorReady)
                + ",\"lifecycle_settled\":" + JsonUtil.Bool(lifecycleSettled)
                + ",\"terminal_compile_failure_without_reload\":" + JsonUtil.Bool(terminalCompileFailureWithoutReload)
                + ",\"compilable\":" + JsonUtil.Bool(!hasCompileErrors && !packageRefreshRequired && !backgroundCompilerProgress.Failed)
                + ",\"package_refresh_required\":" + JsonUtil.Bool(packageRefreshRequired)
                + ",\"package_refresh_wait_can_advance\":" + JsonUtil.Bool(packageRefreshWaitCanAdvance)
                + ",\"stale_package_resolution\":" + JsonUtil.Bool(stalePackageResolution)
                + ",\"package_metadata_unavailable\":" + JsonUtil.Bool(packageMetadataUnavailable)
                + ",\"timed_out\":" + JsonUtil.Bool(timedOut)
                + ",\"waited_ms\":" + Math.Max(0, waitedMs)
                + ",\"timeout_ms\":" + Math.Max(0, timeoutMs)
                + ",\"last_editor_update_age_ms\":" + (lifecycle == null ? 0 : lifecycle.LastEditorUpdateAgeMs)
                + ",\"status_duration_ms\":" + (lifecycle == null ? 0 : lifecycle.StatusDurationMs)
                + ",\"status\":" + JsonUtil.String(status)
                + ",\"editor_status\":" + JsonUtil.String(editorStatus)
                + ",\"has_compile_errors\":" + JsonUtil.Bool(hasCompileErrors)
                + ",\"compile_error_count\":" + ConsoleLogBuffer.CompileErrorCount()
                + ",\"readiness_qualification\":" + CompileReadinessQualificationJson()
                + ",\"compilation_lifecycle\":" + ConsoleLogBuffer.CompilationLifecycleJson()
                + ",\"compile_diagnostics\":" + ConsoleLogBuffer.CompileDiagnosticsJson(20, 4000, 8000)
                + ",\"compile_progress_window\":" + CompileProgressWindowDetector.StatusJson()
                + ",\"background_compiler_progress\":" + backgroundCompilerProgress.ToJson()
                + ",\"compile_barrier\":" + compileBarrier.ToJson()
                + ",\"package_update\":" + (lifecycle == null ? "{}" : lifecycle.PackageUpdateJson)
                + ",\"local_package_inventory_diagnostics\":" + (lifecycle == null ? "null" : lifecycle.LocalPackageCodeInventoryDiagnosticsJson)
                + ",\"has_unsaved_scene_changes\":" + JsonUtil.Bool(state != null && state.HasUnsavedSceneChanges)
                + ",\"dirty_scene_count\":" + (state == null ? 0 : Math.Max(0, state.DirtySceneCount))
                + ",\"dirty_scenes\":" + DirtyScenesJson(state)
                + "}";
        }

        public static string DirtyScenesJson(BridgeRuntimeState state)
        {
            return DirtyScenesJson(state == null ? null : state.DirtyScenes);
        }

        // Qualification is descriptive only. It never contributes to ready,
        // counts, barrier state or package/background admission decisions.
        internal static string CompileReadinessQualificationJson()
        {
            return "{\"schema_version\":1,\"contract\":\"sgg.unity_mcp.compile_readiness_qualification.v1\""
                + ",\"readiness_scope\":\"editor_package_lifecycle_script_evidence_and_background_progress\""
                + ",\"compile_diagnostics_scope\":\"script_compiler_and_console_cs_error_marker\""
                + ",\"shader_asset_readiness\":\"unknown\""
                + ",\"console_history_complete\":false,\"console_history_durable\":false"
                + ",\"explicit_compilation_evidence\":\"inspect_compile_barrier_not_inferred_from_ready_clean\"}";
        }

        public static string DirtyScenesJson(DirtySceneInfo[] dirtyScenes)
        {
            var scenes = dirtyScenes ?? Array.Empty<DirtySceneInfo>();
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < scenes.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var scene = scenes[index];
                builder.Append("{\"scene_path\":").Append(JsonUtil.String(scene == null ? string.Empty : scene.scene_path));
                builder.Append(",\"scene_name\":").Append(JsonUtil.String(scene == null ? string.Empty : scene.scene_name));
                builder.Append(",\"is_active\":").Append(JsonUtil.Bool(scene != null && scene.is_active));
                builder.Append('}');
            }

            builder.Append(']');
            return builder.ToString();
        }

        public static string ValidationStatusJson(BridgeRuntimeState state, string stage, bool refreshed, bool timedOut, int waitedMs, int timeoutMs, string expectedBarrierOperationId = "", bool requireExplicitBarrier = false)
        {
            return ValidationStatusJson(state, stage, refreshed, timedOut, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, state == null ? null : state.CapturePackageLifecycleSnapshot());
        }

        internal static string ValidationStatusJson(BridgeRuntimeState state, string stage, bool refreshed, bool timedOut, int waitedMs, int timeoutMs, string expectedBarrierOperationId, bool requireExplicitBarrier, PackageLifecycleSnapshot lifecycle)
        {
            var hasCompileErrors = ConsoleLogBuffer.HasCompileErrors();
            var compileBarrier = ExplicitCompilationBarrier.Snapshot(expectedBarrierOperationId, requireExplicitBarrier);
            var editorReady = lifecycle != null && lifecycle.IsReady && compileBarrier.Stable && !compileBarrier.Unavailable;
            var hasUnsavedSceneChanges = state != null && state.HasUnsavedSceneChanges;
            var compilable = !hasCompileErrors && (lifecycle == null || !lifecycle.PackageRefreshRequired);
            var validationReady = editorReady && compilable && !hasUnsavedSceneChanges && !timedOut;
            var localPackageInventoryBlocked = lifecycle != null && (lifecycle.LocalPackageCodeInventoryUnavailable || lifecycle.LocalPackageCodeInventoryStale);
            return "{\"stage\":" + JsonUtil.String(stage)
                + ",\"validation_ready\":" + JsonUtil.Bool(validationReady)
                + ",\"refreshed\":" + JsonUtil.Bool(refreshed)
                + ",\"checks\":{\"editor_ready\":" + JsonUtil.Bool(editorReady)
                + ",\"compilable\":" + JsonUtil.Bool(compilable)
                + ",\"no_unsaved_scene_changes\":" + JsonUtil.Bool(!hasUnsavedSceneChanges)
                + ",\"timed_out\":" + JsonUtil.Bool(timedOut)
                + "},\"compile_status\":" + CompileStatusJson(state, timedOut, waitedMs, timeoutMs, expectedBarrierOperationId, requireExplicitBarrier, lifecycle)
                + ",\"next_valid_actions\":" + ValidationActionsJson(hasCompileErrors, hasUnsavedSceneChanges, timedOut, refreshed, lifecycle != null && lifecycle.PackageMetadataUnavailable, localPackageInventoryBlocked)
                + "}";
        }

        static string ValidationActionsJson(bool hasCompileErrors, bool hasUnsavedSceneChanges, bool timedOut, bool refreshed, bool packageMetadataUnavailable, bool localPackageInventoryBlocked)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var count = 0;
            void Add(string action)
            {
                if (count > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(action));
                count++;
            }

            if (packageMetadataUnavailable)
            {
                Add("package.info");
                Add("package.list");
                Add("inspect_or_fix_local_package_manifest_or_upm_resolution");
                Add("call_gateway_sgg_unity_call_diagnostics.report_problem_if_available");
                Add("use_cli_report-problem_with_project_summary_and_include-doctor_if_gateway_mcp_unavailable");
            }
            else if (localPackageInventoryBlocked)
            {
                Add("compile.status");
                Add("package.info");
                Add("inspect_local_package_code_inventory");
                Add("run_compile.refresh_and_wait_only_as_a_new_explicit_action");
            }
            else if (hasUnsavedSceneChanges)
            {
                Add("inspect_or_save_dirty_scenes_before_write_commands");
                Add("editor.save_prompt.get_if_unity_blocks");
            }

            if (!packageMetadataUnavailable && !localPackageInventoryBlocked && hasCompileErrors)
            {
                Add("compile.errors.recent");
                Add("fix_compile_errors_then_retry_validation.after_edit");
            }

            if (!packageMetadataUnavailable && !localPackageInventoryBlocked && timedOut)
            {
                Add("compile.status");
                Add("compile.wait_ready");
                Add("check_scene_save_prompt_or_modal_dialog");
                Add("call_gateway_sgg_unity_call_diagnostics.report_problem_if_available");
                Add("use_cli_report-problem_with_project_summary_and_include-doctor_if_gateway_mcp_unavailable");
            }

            if (!packageMetadataUnavailable && !localPackageInventoryBlocked && !hasCompileErrors && !hasUnsavedSceneChanges && !timedOut)
            {
                Add(refreshed ? "run_tests_or_target_validation" : "perform_edit_then_validation.after_edit");
            }

            builder.Append(']');
            return builder.ToString();
        }

        [McpCommand(
            "logs.console.tail",
            Title = "Console log tail",
            Description = "Read recent Unity console logs",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = "{\"type\":\"object\",\"properties\":{\"limit\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000}},\"additionalProperties\":false}",
            ResultShape = "bounded_list",
            OutputLimitJson = ConsoleLogOutputLimitJson)]
        public static CommandExecutionResult ConsoleLogTail(McpCommandContext context)
        {
            var limit = JsonUtil.ExtractInt(context.ArgsJson, "limit", 200);
            return CommandExecutionResult.Ok(ConsoleLogBuffer.EntriesJson(limit));
        }

        [McpCommand(
            "logs.console.read",
            Title = "Read console logs",
            Description = "Read bounded Unity console logs with type and text filters without clearing the console",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ConsoleLogReadSchemaJson,
            Tags = new string[] { "logs", "console", "unity-ai-assistant" },
            ResultShape = "bounded_list",
            OutputLimitJson = ConsoleLogReadOutputLimitJson)]
        public static CommandExecutionResult ConsoleLogRead(McpCommandContext context)
        {
            var limit = JsonUtil.ExtractInt(context.ArgsJson, "limit", 200);
            var types = JsonUtil.ExtractString(context.ArgsJson, "types");
            var filterText = JsonUtil.ExtractString(context.ArgsJson, "filter_text");
            var includeStackTrace = JsonUtil.ExtractBool(context.ArgsJson, "include_stack_trace", false);
            var maxMessageChars = JsonUtil.ExtractInt(context.ArgsJson, "max_message_chars", 4000);
            var maxStackChars = JsonUtil.ExtractInt(context.ArgsJson, "max_stack_chars", 8000);
            return CommandExecutionResult.Ok(ConsoleLogBuffer.ReadJson(limit, types, filterText, includeStackTrace, maxMessageChars, maxStackChars));
        }

        [McpCommand(
            "logs.console.query",
            Title = "Query console logs",
            Description = "Query bounded Unity console logs with severity and text filters without clearing the console",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ConsoleLogQuerySchemaJson,
            Tags = new string[] { "logs", "console", "unity-ai-assistant" },
            ResultShape = "bounded_list",
            OutputLimitJson = ConsoleLogQueryOutputLimitJson)]
        public static CommandExecutionResult ConsoleLogQuery(McpCommandContext context)
        {
            var severity = JsonUtil.ExtractString(context.ArgsJson, "severity");
            if (string.IsNullOrWhiteSpace(severity))
            {
                severity = JsonUtil.ExtractString(context.ArgsJson, "types");
            }

            var contains = JsonUtil.ExtractString(context.ArgsJson, "contains");
            if (string.IsNullOrEmpty(contains))
            {
                contains = JsonUtil.ExtractString(context.ArgsJson, "filter_text");
            }

            var since = JsonUtil.ExtractString(context.ArgsJson, "since");
            if (string.IsNullOrWhiteSpace(since))
            {
                since = JsonUtil.ExtractString(context.ArgsJson, "since_utc");
            }

            var limit = JsonUtil.ExtractInt(context.ArgsJson, "limit", 50);
            var includeStackTrace = JsonUtil.ExtractBool(context.ArgsJson, "include_stack_trace", false);
            var maxChars = JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 20000);
            return CommandExecutionResult.Ok(ConsoleLogBuffer.QueryJson(limit, severity, contains, since, includeStackTrace, maxChars));
        }

        [McpCommand(
            "package.list",
            Title = "Package list",
            Description = "Read installed Unity packages",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "package" },
            Capability = "package_management",
            ResultShape = "package_list")]
        public static CommandExecutionResult PackageList(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(PackageListJson());
        }

        [McpCommand(
            "assets.refresh",
            Title = "Refresh AssetDatabase",
            Description = "Refresh Unity AssetDatabase after refusing dirty-scene save-prompt risk by default",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = AssetsRefreshSchemaJson,
            ResultShape = "mutation_result")]
        public static CommandExecutionResult AssetsRefresh(McpCommandContext context)
        {
            var allowUnsavedSceneChanges = JsonUtil.ExtractBool(context.ArgsJson, "allow_unsaved_scene_changes", false);
            if (!allowUnsavedSceneChanges)
            {
                var unsavedResult = RequireNoUnsavedSceneChanges(context.State, "assets.refresh", "assets.refresh");
                if (unsavedResult != null)
                {
                    return unsavedResult;
                }
            }

            var refreshObservation = UnityBridgeRuntime.BeginAssetRefresh();
            AssetDatabase.Refresh();
            var refreshEvidence = UnityBridgeRuntime.CompleteAssetRefresh(refreshObservation);
            return CommandExecutionResult.Ok("{\"refreshed\":true,\"allow_unsaved_scene_changes\":" + JsonUtil.Bool(allowUnsavedSceneChanges)
                + ",\"refresh_evidence\":" + refreshEvidence
                + ",\"next_valid_actions\":[\"compile.status\",\"compile.wait_ready\"]}");
        }

        internal static CommandExecutionResult CompileRefreshAndRequestBarrier(McpCommandContext context, out string operationId, out string[] expectedScriptPaths, string command = "compile.refresh_and_wait")
        {
            operationId = string.Empty;
            var expectedPathProperty = string.Equals(command, "compile.refresh_and_wait", StringComparison.Ordinal) ? "expected_script_paths" : string.Empty;
            var expectedPathsResult = ReadExpectedScriptPaths(context.ArgsJson, expectedPathProperty, out expectedScriptPaths);
            if (expectedPathsResult != null)
            {
                return expectedPathsResult;
            }
            var allowUnsavedSceneChanges = JsonUtil.ExtractBool(context.ArgsJson, "allow_unsaved_scene_changes", false);
            if (!allowUnsavedSceneChanges)
            {
                var unsavedResult = RequireNoUnsavedSceneChanges(context.State, command, command);
                if (unsavedResult != null)
                {
                    return unsavedResult;
                }
            }

            var busyResult = CompileRefreshBusyPreflight(
                EditorApplication.isCompiling,
                EditorApplication.isUpdating,
                EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode,
                command);
            if (busyResult != null)
            {
                return busyResult;
            }

            var expectedOperationId = JsonUtil.ExtractString(context.ArgsJson, "_compile_barrier_expectation_id");
            var begin = ExplicitCompilationBarrier.Begin(expectedOperationId);
            if (!begin.Success)
            {
                return CommandExecutionResult.Error(begin.ErrorCode, "Could not create an explicit compilation barrier", ExplicitCompilationBarrier.StatusJson(expectedOperationId, true));
            }

            operationId = begin.State.OperationId;
            var armed = ExplicitCompilationBarrier.MarkRequestIssued(operationId);
            if (!armed.Success)
            {
                return CommandExecutionResult.Error(armed.ErrorCode, "Could not arm the explicit compilation barrier", ExplicitCompilationBarrier.StatusJson(operationId, true));
            }

            ConsoleLogBuffer.BeginPrefabImportObservation(operationId);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var barrierAfterRefresh = ExplicitCompilationBarrier.Snapshot(operationId, true);
            if (!IsMatchingArmedCompileBarrier(barrierAfterRefresh, operationId))
            {
                var code = barrierAfterRefresh.Unavailable ? "compile_freshness_unavailable" : "compile_freshness_unverified";
                var details = "{\"reason\":\"compile_barrier_invalid_after_refresh\""
                    + ",\"command\":" + JsonUtil.String(command)
                    + ",\"refresh_started\":true"
                    + ",\"command_dispatched\":true"
                    + ",\"write_command_repeated\":false"
                    + ",\"write_replay_allowed\":false"
                    + ",\"script_compilation_requested\":false"
                    + ",\"compile_barrier\":" + barrierAfterRefresh.ToJson()
                    + ",\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\",\"do_not_auto_repeat_" + command + "\"]}";
                return CommandExecutionResult.Error(code, "The explicit compilation barrier became invalid during AssetDatabase.Refresh", details);
            }
            var scriptCompilationRequested = ShouldRequestCompilationAfterRefresh(barrierAfterRefresh, operationId, EditorApplication.isCompiling);
            if (scriptCompilationRequested)
            {
                CompilationPipeline.RequestScriptCompilation();
            }
            return CommandExecutionResult.Ok("{\"refreshed\":true,\"script_compilation_requested\":" + JsonUtil.Bool(scriptCompilationRequested) + ",\"refresh_started_compilation\":" + JsonUtil.Bool(!scriptCompilationRequested) + ",\"compile_barrier\":" + ExplicitCompilationBarrier.StatusJson(operationId, true) + "}");
        }

        internal static CommandExecutionResult ReadExpectedScriptPaths(string argsJson, string propertyName, out string[] expectedScriptPaths)
        {
            expectedScriptPaths = Array.Empty<string>();
            if (string.IsNullOrEmpty(propertyName) || !JsonUtil.HasProperty(argsJson, propertyName))
            {
                return null;
            }

            if (!BridgeProtocol.TryReadStringArray(argsJson, propertyName, 64, out var requested, out var arrayFailureReason))
            {
                var message = arrayFailureReason == "max_items_exceeded"
                    ? propertyName + " supports at most 64 paths"
                    : propertyName + " must be an array of strings";
                return CommandExecutionResult.ValidationError("invalid_field_value", message, propertyName, arrayFailureReason == "array_required" ? "invalid_type" : "relational_constraint_failed");
            }

            var normalized = new List<string>(requested.Length);
            var unique = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < requested.Length; index++)
            {
                var rawPath = requested[index];
                if (string.IsNullOrEmpty(rawPath) || rawPath.Length > 512)
                {
                    return CommandExecutionResult.ValidationError("invalid_field_value", propertyName + " entries must be 1..512 characters", propertyName, index, string.IsNullOrEmpty(rawPath) ? "string_too_short" : "string_too_long");
                }

                string assetPath;
                try
                {
                    assetPath = PathGuard.RequireUnityAssetPath(rawPath);
                }
                catch (Exception)
                {
                    return CommandExecutionResult.ValidationError("invalid_field_value", propertyName + " entries must be safe Unity-relative asset paths", propertyName, index, "invalid_path");
                }

                if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal) || !assetPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                {
                    return CommandExecutionResult.ValidationError("invalid_field_value", propertyName + " entries must be C# scripts under Assets/", propertyName, index, "pattern_mismatch");
                }
                if (!unique.Add(assetPath))
                {
                    return CommandExecutionResult.ValidationError("invalid_field_value", propertyName + " entries must be unique", propertyName, index, "relational_constraint_failed");
                }
                normalized.Add(assetPath);
            }

            expectedScriptPaths = normalized.ToArray();
            return null;
        }

        internal static ExpectedScriptImportInspection InspectExpectedScriptImports(string[] expectedScriptPaths)
        {
            var activeCompilerSources = CaptureExpectedCompilerSources(expectedScriptPaths);
            return InspectExpectedScriptImportsForTests(
                expectedScriptPaths,
                path => AssetDatabase.AssetPathToGUID(path),
                path => AssetDatabase.LoadMainAssetAtPath(path) is MonoScript,
                path => activeCompilerSources.Contains(path));
        }

        internal static ExpectedScriptImportInspection InspectExpectedScriptImportsForTests(string[] expectedScriptPaths, Func<string, string> guidLookup, Func<string, bool> monoScriptMainAssetLookup, Func<string, bool> compilerSourceLookup)
        {
            var paths = expectedScriptPaths ?? Array.Empty<string>();
            var items = new List<ExpectedScriptImportEvidence>(paths.Length);
            for (var index = 0; index < paths.Length; index++)
            {
                var guid = guidLookup == null ? string.Empty : guidLookup(paths[index]);
                var monoScriptMainAsset = monoScriptMainAssetLookup != null && monoScriptMainAssetLookup(paths[index]);
                var compilerSourcePresent = compilerSourceLookup != null && compilerSourceLookup(paths[index]);
                items.Add(new ExpectedScriptImportEvidence(paths[index], !string.IsNullOrEmpty(guid), IsExactUnityGuid(guid), monoScriptMainAsset, compilerSourcePresent));
            }
            return new ExpectedScriptImportInspection(items);
        }

        static HashSet<string> CaptureExpectedCompilerSources(string[] expectedScriptPaths)
        {
            var expected = new HashSet<string>(expectedScriptPaths ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            var matched = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var assemblies = CompilationPipeline.GetAssemblies();
            for (var assemblyIndex = 0; assemblyIndex < assemblies.Length && matched.Count < expected.Count; assemblyIndex++)
            {
                var sources = assemblies[assemblyIndex] == null ? null : assemblies[assemblyIndex].sourceFiles;
                for (var sourceIndex = 0; sources != null && sourceIndex < sources.Length; sourceIndex++)
                {
                    var sourcePath = NormalizeCompilerSourcePath(sources[sourceIndex]);
                    if (expected.Contains(sourcePath))
                    {
                        matched.Add(sourcePath);
                    }
                }
            }
            return matched;
        }

        static string NormalizeCompilerSourcePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return string.Empty;
            }
            var normalized = path.Replace('\\', '/');
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return normalized;
            }
            var projectRelative = FileUtil.GetProjectRelativePath(path);
            return string.IsNullOrEmpty(projectRelative) ? string.Empty : projectRelative.Replace('\\', '/');
        }

        internal static bool IsExactUnityGuid(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 32)
            {
                return false;
            }
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9')
                    || (character >= 'a' && character <= 'f')
                    || (character >= 'A' && character <= 'F')))
                {
                    return false;
                }
            }
            return true;
        }

        internal static bool IsMatchingArmedCompileBarrier(ExplicitCompilationBarrierSnapshot barrier, string operationId)
        {
            var operation = barrier == null ? null : barrier.Operation;
            return operation != null
                && string.Equals(operation.OperationId, operationId, StringComparison.Ordinal)
                && operation.RequestIssued
                && (barrier.State == "pending" || barrier.Verified);
        }

        internal static bool ShouldRequestCompilationAfterRefresh(ExplicitCompilationBarrierSnapshot barrier, string operationId, bool compilationActive)
        {
            return IsMatchingArmedCompileBarrier(barrier, operationId)
                && barrier.State == "pending"
                && !barrier.Operation.Started
                && !compilationActive;
        }

        internal static CommandExecutionResult CompileRefreshBusyPreflight(bool isCompiling, bool isUpdating, bool isPlaying, string command = "compile.refresh_and_wait")
        {
            var reason = isCompiling ? "compilation_active" : isUpdating ? "asset_update_active" : isPlaying ? "play_mode_active_or_transitioning" : string.Empty;
            if (string.IsNullOrEmpty(reason))
            {
                return null;
            }

            var activity = isCompiling ? "compiling" : isUpdating ? "updating assets" : "in or entering Play Mode";
            var details = "{\"reason\":" + JsonUtil.String(reason)
                + ",\"is_compiling\":" + JsonUtil.Bool(isCompiling)
                + ",\"is_updating\":" + JsonUtil.Bool(isUpdating)
                + ",\"is_playing\":" + JsonUtil.Bool(isPlaying)
                + ",\"refresh_started\":false"
                + ",\"compile_barrier_created\":false"
                + ",\"script_compilation_requested\":false"
                + ",\"retryable\":true"
                + ",\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\",\"retry_compile.refresh_and_wait_after_editor_is_ready\"]}";
            return CommandExecutionResult.Error("unity_busy", command + " requires an idle Editor; Unity is " + activity, details);
        }

        [McpCommand(
            "asset.import",
            Title = "Import asset",
            Description = "Import one safe Unity-relative asset path under Assets/. Eligible Local or Embedded package C# files use package.local_script.import.",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = AssetImportSchemaJson,
            ResultShape = "mutation_result")]
        public static CommandExecutionResult AssetImport(McpCommandContext context)
        {
            var assetPath = JsonUtil.ExtractString(context.ArgsJson, "asset_path");
            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument asset_path is required");
            }

            var validatedPath = PathGuard.RequireUnityAssetPath(assetPath);
            var pathScopeError = AssetImportPathScopeError(validatedPath);
            if (pathScopeError != null) return pathScopeError;

            var guidBefore = AssetDatabase.AssetPathToGUID(validatedPath);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            if (dryRun)
            {
                return CommandExecutionResult.Ok("{\"dry_run\":true,\"would_import\":true,\"asset_path\":" + JsonUtil.String(validatedPath)
                    + ",\"guid_before\":" + JsonUtil.String(guidBefore)
                    + ",\"changed_assets\":[]"
                    + ",\"imported_assets\":[]"
                    + ",\"warnings\":[\"Dry-run only; rerun without dry_run after external file writes are complete.\"]"
                    + ",\"next_valid_actions\":[\"asset.import\",\"validation.after_edit\"]}");
            }

            AssetDatabase.ImportAsset(validatedPath, ImportAssetOptions.ForceUpdate);
            var guidAfter = AssetDatabase.AssetPathToGUID(validatedPath);
            return CommandExecutionResult.Ok("{\"imported\":true,\"asset_path\":" + JsonUtil.String(validatedPath)
                + ",\"guid\":" + JsonUtil.String(guidAfter)
                + ",\"guid_before\":" + JsonUtil.String(guidBefore)
                + ",\"guid_after\":" + JsonUtil.String(guidAfter)
                + ",\"changed_assets\":[" + JsonUtil.String(validatedPath) + "]"
                + ",\"imported_assets\":[" + JsonUtil.String(validatedPath) + "]"
                + ",\"warnings\":[]"
                + ",\"next_valid_actions\":[\"validation.after_edit\",\"compile.status\",\"asset.info\"]}");
        }

        internal static CommandExecutionResult AssetImportPathScopeError(string validatedPath)
        {
            if (!string.IsNullOrEmpty(validatedPath) && validatedPath.StartsWith("Assets/", StringComparison.Ordinal)) return null;
            var packageScriptCandidate = !string.IsNullOrEmpty(validatedPath)
                && validatedPath.StartsWith("Packages/", StringComparison.Ordinal)
                && validatedPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase);
            var message = packageScriptCandidate
                ? "asset_path must be under Assets/. Eligible Local or Embedded package C# files use package.local_script.import."
                : "asset_path must be under Assets/";
            return CommandExecutionResult.ValidationError("invalid_field_value", message, "asset_path", "unsupported_for_target");
        }

        internal sealed class ExpectedScriptImportInspection
        {
            readonly List<ExpectedScriptImportEvidence> m_items;
            readonly List<ExpectedScriptImportEvidence> m_failures;

            internal ExpectedScriptImportInspection(List<ExpectedScriptImportEvidence> items)
            {
                m_items = items ?? new List<ExpectedScriptImportEvidence>();
                m_failures = m_items.FindAll(item => !item.Verified);
            }

            internal bool Verified { get { return m_failures.Count == 0; } }

            internal string ToJson()
            {
                var builder = new StringBuilder();
                builder.Append("{\"required\":true");
                builder.Append(",\"verified\":").Append(JsonUtil.Bool(Verified));
                builder.Append(",\"checked_count\":").Append(m_items.Count);
                builder.Append(",\"verified_count\":").Append(m_items.Count - m_failures.Count);
                builder.Append(",\"missing_count\":").Append(m_failures.Count);
                builder.Append(",\"items\":[");
                for (var index = 0; index < m_items.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    builder.Append(m_items[index].ToJson());
                }
                builder.Append(']');
                builder.Append(",\"failures\":[");
                for (var index = 0; index < m_failures.Count; index++)
                {
                    if (index > 0) builder.Append(',');
                    builder.Append(m_failures[index].ToJson());
                }
                builder.Append("]}");
                return builder.ToString();
            }
        }

        internal sealed class ExpectedScriptImportEvidence
        {
            readonly string m_assetPath;
            readonly bool m_guidPresent;
            readonly bool m_guidValid;
            readonly bool m_monoScriptMainAsset;
            readonly bool m_compilerSourcePresent;

            internal ExpectedScriptImportEvidence(string assetPath, bool guidPresent, bool guidValid, bool monoScriptMainAsset, bool compilerSourcePresent)
            {
                m_assetPath = assetPath ?? string.Empty;
                m_guidPresent = guidPresent;
                m_guidValid = guidValid;
                m_monoScriptMainAsset = monoScriptMainAsset;
                m_compilerSourcePresent = compilerSourcePresent;
            }

            internal bool Verified { get { return m_guidValid && m_monoScriptMainAsset && m_compilerSourcePresent; } }

            internal string ToJson()
            {
                var causes = new List<string>(3);
                if (!m_guidValid) causes.Add("invalid_guid");
                if (!m_monoScriptMainAsset) causes.Add("mono_script_missing");
                if (!m_compilerSourcePresent) causes.Add("compiler_source_missing");
                return "{\"asset_path\":" + JsonUtil.String(m_assetPath)
                    + ",\"verified\":" + JsonUtil.Bool(Verified)
                    + ",\"guid_present\":" + JsonUtil.Bool(m_guidPresent)
                    + ",\"guid_valid\":" + JsonUtil.Bool(m_guidValid)
                    + ",\"mono_script_loaded\":" + JsonUtil.Bool(m_monoScriptMainAsset)
                    + ",\"mono_script_main_asset\":" + JsonUtil.Bool(m_monoScriptMainAsset)
                    + ",\"compiler_source_present\":" + JsonUtil.Bool(m_compilerSourcePresent)
                    + ",\"failure_causes\":[" + string.Join(",", causes.ConvertAll(JsonUtil.String).ToArray()) + "]}";
            }
        }

        [McpCommand(
            "scene.open",
            Title = "Open scene",
            Description = "Open a Unity scene by Unity-relative path after refusing dirty-scene save-prompt risk by default",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = SceneOpenSchemaJson,
            ResultShape = "mutation_result")]
        public static CommandExecutionResult SceneOpen(McpCommandContext context)
        {
            var allowUnsavedSceneChanges = JsonUtil.ExtractBool(context.ArgsJson, "allow_unsaved_scene_changes", false);
            if (!allowUnsavedSceneChanges)
            {
                var unsavedResult = RequireNoUnsavedSceneChanges(context.State, "scene.open", "scene.open");
                if (unsavedResult != null)
                {
                    return unsavedResult;
                }
            }

            var pathValidation = RequireScenePath(context, out var validatedPath);
            if (pathValidation != null)
            {
                return pathValidation;
            }

            var scene = EditorSceneManager.OpenScene(validatedPath, OpenSceneMode.Single);
            return CommandExecutionResult.Ok("{\"opened\":true,\"scene_path\":" + JsonUtil.String(validatedPath) + ",\"scene_name\":" + JsonUtil.String(scene.name) + "}");
        }

        [McpCommand(
            "scene.save",
            Title = "Save scene",
            Description = "Save the active scene to a Unity-relative path",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 30000,
            SchemaJson = ScenePathSchemaJson,
            ResultShape = "mutation_result")]
        public static CommandExecutionResult SceneSave(McpCommandContext context)
        {
            var pathValidation = RequireScenePath(context, out var validatedPath);
            if (pathValidation != null)
            {
                return pathValidation;
            }

            var scene = SceneManager.GetActiveScene();
            if (!EditorSceneManager.SaveScene(scene, validatedPath))
            {
                return CommandExecutionResult.Error("unity_exception", "Scene save failed");
            }

            return CommandExecutionResult.Ok("{\"saved\":true,\"scene_path\":" + JsonUtil.String(validatedPath) + "}");
        }

        public static CommandExecutionResult RequireNoUnsavedSceneChanges(BridgeRuntimeState state, string commandId, string operationName)
        {
            if (state == null || !state.HasUnsavedSceneChanges)
            {
                return null;
            }

            return CommandExecutionResult.Error(
                "unity_busy",
                operationName + " refused while loaded scenes have unsaved changes because Unity may open a blocking save prompt",
                UnsavedSceneChangesDetailsJson(state, commandId));
        }

        public static string UnsavedSceneChangesDetailsJson(BridgeRuntimeState state, string commandId)
        {
            return "{\"reason\":\"unsaved_scene_changes\""
                + ",\"command\":" + JsonUtil.String(commandId)
                + ",\"has_unsaved_scene_changes\":" + JsonUtil.Bool(state != null && state.HasUnsavedSceneChanges)
                + ",\"dirty_scene_count\":" + (state == null ? 0 : Math.Max(0, state.DirtySceneCount))
                + ",\"dirty_scenes\":" + DirtyScenesJson(state)
                + ",\"safe_recovery_commands\":[\"editor.save_prompt.get\",\"editor.save_prompt.respond:{\\\"action\\\":\\\"cancel\\\"}\",\"compile.status\"]"
                + ",\"hint\":\"Resolve dirty scenes before retrying. If a save prompt is already open, use editor.save_prompt.respond with action=cancel to close it without saving or discarding scene changes. Do not use dont_save unless discarding changes is intended.\"}";
        }

        public static string ConsoleLogsJson(int limit)
        {
            return ConsoleLogBuffer.EntriesJson(limit);
        }

        public static string ConsoleLogEntriesArrayJson(int limit)
        {
            return ConsoleLogBuffer.EntriesArrayJson(limit);
        }

        public static string ConsoleLogBufferMetadataJson()
        {
            return ConsoleLogBuffer.BufferMetadataJson();
        }

        static string PackageListJson()
        {
            var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
            Array.Sort(packages, (left, right) => string.Compare(left.name, right.name, StringComparison.Ordinal));
            var metadataBatch = LocalPackageMetadataInspector.InspectPackages(packages, false);

            var builder = new StringBuilder();
            builder.Append("{\"installed_only\":true");
            LocalPackageMetadataInspector.AppendSummaryJsonFields(builder, metadataBatch.Summary);
            builder.Append(",\"packages\":[");
            for (var index = 0; index < packages.Length; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var package = packages[index];
                builder.Append("{\"name\":").Append(JsonUtil.String(package.name));
                builder.Append(",\"version\":").Append(JsonUtil.String(package.version));
                builder.Append(",\"display_name\":").Append(JsonUtil.String(package.displayName));
                builder.Append(",\"source\":").Append(JsonUtil.String(package.source.ToString()));
                LocalPackageMetadataInspector.AppendPackageJsonFields(builder, metadataBatch.Evidence[index]);
                builder.Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static CommandExecutionResult RequireScenePath(McpCommandContext context, out string validatedPath)
        {
            validatedPath = string.Empty;
            var scenePath = JsonUtil.ExtractString(context.ArgsJson, "scene_path");
            if (string.IsNullOrEmpty(scenePath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument scene_path is required");
            }

            validatedPath = PathGuard.RequireUnityAssetPath(scenePath);
            if (!validatedPath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", "scene_path must end with .unity");
            }

            return null;
        }
    }
}
