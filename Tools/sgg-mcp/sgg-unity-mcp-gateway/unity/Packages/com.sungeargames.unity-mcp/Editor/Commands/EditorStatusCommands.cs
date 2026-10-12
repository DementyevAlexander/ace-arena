using System;
using System.Globalization;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    internal sealed class EditorEditModeEligibility
    {
        EditorEditModeEligibility(bool isCompiling, bool isUpdating, bool isPlaying, bool isPlayingOrWillChangePlaymode, bool isPaused)
        {
            IsCompiling = isCompiling;
            IsUpdating = isUpdating;
            IsPlayingActual = isPlaying;
            IsPlayingOrWillChangePlaymode = isPlayingOrWillChangePlaymode;
            IsPaused = isPaused;
            Status = EditorStatusCommands.Status(isCompiling, isUpdating, isPlaying, isPlayingOrWillChangePlaymode, isPaused);
        }

        internal bool IsCompiling { get; }
        internal bool IsUpdating { get; }
        internal bool IsPlayingActual { get; }
        internal bool IsPlayingOrWillChangePlaymode { get; }
        internal bool IsPaused { get; }
        internal bool IsPlaying => IsPlayingActual || IsPlayingOrWillChangePlaymode;
        internal bool IsEligible => !IsCompiling && !IsUpdating && !IsPlaying;
        internal string Status { get; }

        internal static EditorEditModeEligibility From(bool isCompiling, bool isUpdating, bool isPlaying, bool isPlayingOrWillChangePlaymode, bool isPaused)
        {
            return new EditorEditModeEligibility(isCompiling, isUpdating, isPlaying, isPlayingOrWillChangePlaymode, isPaused);
        }

        internal string NormalizedStateJson()
        {
            return "{\"status\":" + JsonUtil.String(Status)
                + ",\"is_compiling\":" + JsonUtil.Bool(IsCompiling)
                + ",\"is_updating\":" + JsonUtil.Bool(IsUpdating)
                + ",\"is_playing\":" + JsonUtil.Bool(IsPlaying)
                + ",\"is_playing_actual\":" + JsonUtil.Bool(IsPlayingActual)
                + ",\"is_playing_or_will_change_playmode\":" + JsonUtil.Bool(IsPlayingOrWillChangePlaymode)
                + ",\"is_paused\":" + JsonUtil.Bool(IsPaused)
                + ",\"edit_mode_eligible\":" + JsonUtil.Bool(IsEligible)
                + "}";
        }
    }

    public static class EditorStatusCommands
    {
        const string FeatureFlag = "editor_csharp_execution";
        const string EditorUtilitiesFeatureFlag = "editor_utilities";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string PlayModeStatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"_playmode_transition_expectation_id\":{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{32}$\"}},\"additionalProperties\":false}";
        const string SetPlayModeSchemaJson = "{\"type\":\"object\",\"properties\":{\"state\":{\"type\":\"string\",\"enum\":[\"playing\",\"editing\"]},\"_playmode_transition_expectation_id\":{\"type\":\"string\",\"pattern\":\"^[0-9a-f]{32}$\"}},\"required\":[\"state\"],\"additionalProperties\":false}";
        const string EditorQuitSchemaJson = "{\"type\":\"object\",\"properties\":{\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_close\":{\"type\":\"boolean\",\"default\":false},\"wait_timeout_ms\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":60000,\"default\":35000},\"_editor_quit_operation_id\":{\"type\":\"string\",\"pattern\":\"^quit-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$\"},\"_editor_quit_caller_args_hash\":{\"type\":\"string\",\"pattern\":\"^sha256:[0-9a-f]{64}$\"}},\"additionalProperties\":false}";

        [McpCommand(
            "editor.state.get",
            Title = "Get editor state",
            Description = "Read Unity Editor state for Unity AI Assistant compatibility",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "editor", "status", "unity-ai-assistant" },
            Capability = EditorUtilitiesFeatureFlag,
            ResultShape = "status",
            FeatureFlag = EditorUtilitiesFeatureFlag)]
        public static CommandExecutionResult EditorStateGet(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(StatusJson(context.State, "Unity.ManageEditor GetState"));
        }

        [McpCommand(
            "editor.status",
            Title = "Editor status",
            Description = "Read Unity Editor state before advanced editor operations",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = PlayModeStatusSchemaJson,
            Tags = new string[] { "editor", "status" },
            Capability = "editor_csharp_execution",
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EditorStatus(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(StatusJson(context.State, null, null, null, JsonUtil.ExtractString(context.ArgsJson, "_playmode_transition_expectation_id")));
        }

        [McpCommand(
            "editor.playmode.enter",
            Title = "Enter Play Mode",
            Description = "Request Unity Editor Play Mode",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = PlayModeStatusSchemaJson,
            Tags = new string[] { "editor", "playmode" },
            Capability = "editor_csharp_execution",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult EnterPlayMode(McpCommandContext context)
        {
            return RequestPlayMode(context, true);
        }

        [McpCommand(
            "editor.playmode.set",
            Title = "Set Play Mode",
            Description = "Request Unity Editor Play Mode state",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = SetPlayModeSchemaJson,
            Tags = new string[] { "editor", "playmode" },
            Capability = "editor_csharp_execution",
            RequiresEditorStatus = new string[] { "editing", "playing", "playing_paused" },
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult SetPlayMode(McpCommandContext context)
        {
            var state = JsonUtil.ExtractString(context.ArgsJson, "state").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(state))
            {
                return CommandExecutionResult.ValidationError("schema_validation_failed", "Argument state is required", "state", JsonUtil.HasProperty(context.ArgsJson, "state") ? "enum_value_not_allowed" : "missing_required_field");
            }

            if (state == "playing")
            {
                return RequestPlayMode(context, true);
            }

            if (state == "editing")
            {
                return RequestPlayMode(context, false);
            }

            return CommandExecutionResult.ValidationError("invalid_field_value", "state must be playing or editing", "state", "enum_value_not_allowed");
        }

        [McpCommand(
            "editor.playmode.exit",
            Title = "Exit Play Mode",
            Description = "Request Unity Editor exit Play Mode",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = PlayModeStatusSchemaJson,
            Tags = new string[] { "editor", "playmode" },
            Capability = "editor_csharp_execution",
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "status",
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ExitPlayMode(McpCommandContext context)
        {
            return RequestPlayMode(context, false);
        }

        [McpCommand(
            "editor.quit",
            Title = "Quit Unity Editor safely",
            Description = "Safely request Unity Editor shutdown after dry-run, explicit confirmation and dirty-scene/playmode/compile preflight",
            Risk = "destructive",
            Idempotency = "unsafe",
            TimeoutMs = 70000,
            SchemaJson = EditorQuitSchemaJson,
            Tags = new string[] { "editor", "lifecycle", "quit" },
            ResultShape = "status")]
        public static CommandExecutionResult EditorQuit(McpCommandContext context)
        {
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var confirmClose = JsonUtil.ExtractBool(context.ArgsJson, "confirm_close", false);
            context.State?.UpdateFromEditor();

            var blocked = EditorQuitBlockedResult(context, dryRun, confirmClose);
            if (blocked != null)
            {
                return blocked;
            }

            if (dryRun)
            {
                return CommandExecutionResult.Ok(EditorQuitJson(context.State, dryRun, confirmClose, false, "dry_run", string.Empty, "none"));
            }

            var operationId = JsonUtil.ExtractString(context.ArgsJson, "_editor_quit_operation_id");
            var callerArgsHash = JsonUtil.ExtractString(context.ArgsJson, "_editor_quit_caller_args_hash");
            var waitTimeoutMs = JsonUtil.ExtractInt(context.ArgsJson, "wait_timeout_ms", 35000);
            if (string.IsNullOrEmpty(operationId) || string.IsNullOrEmpty(callerArgsHash))
            {
                return CommandExecutionResult.Error("unsafe_binding_refused", "editor.quit requires Gateway-reserved lifecycle correlation", "{\"command\":\"editor.quit\",\"close_requested\":false,\"quit_operation_reserved\":false,\"automatic_retry_allowed\":false}");
            }

            EditorQuitLifecycle.Begin(operationId, callerArgsHash, waitTimeoutMs);
            BridgeReloadTrace.Log("editor.quit.requested", "confirm_close=true");
            EditorQuitLifecycle.ScheduleEditorExit();
            return CommandExecutionResult.Ok(EditorQuitJson(context.State, dryRun, confirmClose, true, "scheduled", operationId, "deferred"));
        }

        static CommandExecutionResult RequestPlayMode(McpCommandContext context, bool playing)
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_compiling", "Unity is compiling");
            }

            var transitionRequested = playing
                ? !EditorApplication.isPlayingOrWillChangePlaymode
                : EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            var target = playing ? "playing" : "editing";
            var targetAlreadyReached = playing
                ? EditorApplication.isPlaying && EditorApplication.isPlayingOrWillChangePlaymode
                : !EditorApplication.isPlaying && !EditorApplication.isPlayingOrWillChangePlaymode;
            var reloadExpected = PlayModeTransitionTracker.ReloadExpectedForTransition(
                transitionRequested,
                playing,
                EditorSettings.enterPlayModeOptionsEnabled,
                EditorSettings.enterPlayModeOptions);
            var operationId = PlayModeTransitionTracker.Begin(
                JsonUtil.ExtractString(context.ArgsJson, "_playmode_transition_expectation_id"),
                target,
                transitionRequested,
                targetAlreadyReached,
                reloadExpected);
            if (playing)
            {
                if (transitionRequested)
                {
                    EditorApplication.isPlaying = true;
                }
            }
            else if (transitionRequested)
            {
                EditorApplication.isPlaying = false;
            }

            return CommandExecutionResult.Ok(StatusJson(context.State, null, target, transitionRequested, operationId));
        }

        static CommandExecutionResult EditorQuitBlockedResult(McpCommandContext context, bool dryRun, bool confirmClose)
        {
            if (Application.isBatchMode)
            {
                return CommandExecutionResult.Error("unsupported_context", "editor.quit is only supported for an interactive Unity Editor", EditorQuitBlockedDetailsJson(context, dryRun, confirmClose, "batchmode", "Use the owning supervisor process to stop batchmode Unity."));
            }

            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_busy", "editor.quit refused while Unity is compiling", EditorQuitBlockedDetailsJson(context, dryRun, confirmClose, "compiling", "Wait for compile.status to report ready before closing the Editor."));
            }

            if (EditorApplication.isUpdating)
            {
                return CommandExecutionResult.Error("unity_busy", "editor.quit refused while Unity is updating assets", EditorQuitBlockedDetailsJson(context, dryRun, confirmClose, "updating", "Wait for compile.status to report ready before closing the Editor."));
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", "editor.quit refused while Unity is in or entering Play Mode", EditorQuitBlockedDetailsJson(context, dryRun, confirmClose, "playmode", "Closing from Play Mode is unsupported. This command does not observe retained native custody; consult the owning package or operator before any lifecycle transition. No automatic Play exit or resource release is performed."));
            }

            var unsavedResult = BuiltInCommands.RequireNoUnsavedSceneChanges(context.State, "editor.quit", "editor.quit");
            if (unsavedResult != null)
            {
                return CommandExecutionResult.Error(unsavedResult.ErrorCode, unsavedResult.ErrorMessage, EditorQuitPreflightRefusalDetailsJson(context, unsavedResult.ErrorDetailsJson));
            }

            if (!dryRun && !confirmClose)
            {
                return CommandExecutionResult.Error("confirmation_required", "editor.quit requires confirm_close:true when dry_run:false", EditorQuitBlockedDetailsJson(context, dryRun, confirmClose, "confirm_close_required", "Repeat with dry_run:false and confirm_close:true only after operator approval."));
            }

            return null;
        }

        static string EditorQuitJson(BridgeRuntimeState state, bool dryRun, bool confirmClose, bool closeRequested, string lifecycle, string operationId, string quitRequestState)
        {
            return "{\"command\":\"editor.quit\""
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"confirm_close\":" + JsonUtil.Bool(confirmClose)
                + ",\"close_requested\":" + JsonUtil.Bool(closeRequested)
                + ",\"lifecycle\":" + JsonUtil.String(lifecycle)
                + ",\"quit_operation_id\":" + JsonUtil.String(operationId)
                + ",\"quit_request_state\":" + JsonUtil.String(quitRequestState)
                + ",\"automatic_second_quit_attempted\":false"
                + ",\"exit_code\":0"
                + ",\"has_unsaved_scene_changes\":" + JsonUtil.Bool(state != null && state.HasUnsavedSceneChanges)
                + ",\"dirty_scene_count\":" + (state == null ? 0 : state.DirtySceneCount)
                + ",\"dirty_scenes\":" + BuiltInCommands.DirtyScenesJson(state)
                + ",\"next_valid_actions\":" + (dryRun ? "[\"retry_editor.quit_with_dry_run_false_confirm_close_true_after_operator_approval\",\"editor.status\",\"compile.status\"]" : "[\"call_sgg_unity_projects_after_editor_exits\",\"wait_for_project_offline_or_stale\"]")
                + "}";
        }

        static string EditorQuitBlockedDetailsJson(McpCommandContext context, bool dryRun, bool confirmClose, string reason, string hint)
        {
            var state = context.State;
            var details = "{\"command\":\"editor.quit\""
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"confirm_close\":" + JsonUtil.Bool(confirmClose)
                + ",\"has_unsaved_scene_changes\":" + JsonUtil.Bool(state != null && state.HasUnsavedSceneChanges)
                + ",\"dirty_scene_count\":" + (state == null ? 0 : state.DirtySceneCount)
                + ",\"dirty_scenes\":" + BuiltInCommands.DirtyScenesJson(state)
                + ",\"retry_args\":{\"dry_run\":false,\"confirm_close\":true}"
                + ",\"safe_recovery_commands\":[\"editor.status\",\"compile.status\",\"editor.save_prompt.get\"]"
                + ",\"next_valid_actions\":[\"consult_owning_package_or_operator_before_lifecycle_changes\",\"do_not_exit_playmode_automatically\",\"do_not_kill_unity_automatically\",\"do_not_replay_editor.quit_automatically\"]"
                + ",\"hint\":" + JsonUtil.String(hint)
                + "}";
            return EditorQuitPreflightRefusalDetailsJson(context, details);
        }

        // Called only on this handler's refusal branches, before Begin or ScheduleEditorExit.
        // This says nothing about another invocation or retained package/GPU custody.
        static string EditorQuitPreflightRefusalDetailsJson(McpCommandContext context, string detailsJson)
        {
            var processId = 0;
            var processStartUtc = string.Empty;
            var canonicalProjectRoot = string.Empty;
            try
            {
                using (var process = System.Diagnostics.Process.GetCurrentProcess())
                {
                    processId = process.Id;
                    processStartUtc = process.StartTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                // No process-generation proof is inferred when passive identity is unavailable.
            }
            try
            {
                canonicalProjectRoot = new CanonicalPathService().NormalizeProjectRoot(ProjectPaths.ProjectRoot).Value;
            }
            catch
            {
                // The original refusal remains valid; the Gateway must not accept an incomplete binding.
            }

            return EditorQuitPreflightRefusalDetailsJson(
                detailsJson, context.ArgsJson, context.State == null ? string.Empty : context.State.Project,
                context.State == null ? string.Empty : context.State.InstanceId,
                canonicalProjectRoot, processId, processStartUtc);
        }

        internal static string EditorQuitPreflightRefusalDetailsJson(string detailsJson, string argsJson, string project, string instanceId, string canonicalProjectRoot, int processId, string processStartUtc)
        {
            var reason = JsonUtil.ExtractString(detailsJson, "reason");
            if (string.IsNullOrEmpty(detailsJson) || detailsJson[detailsJson.Length - 1] != '}'
                || JsonUtil.ExtractString(detailsJson, "command") != "editor.quit"
                || (reason != "batchmode" && reason != "compiling" && reason != "updating" && reason != "playmode"
                    && reason != "unsaved_scene_changes" && reason != "confirm_close_required")
                || JsonUtil.HasProperty(detailsJson, "editor_quit_preflight_refusal") || JsonUtil.HasProperty(detailsJson, "close_requested"))
            {
                return detailsJson;
            }

            var operationId = JsonUtil.ExtractString(argsJson, "_editor_quit_operation_id");
            var callerArgsHash = JsonUtil.ExtractString(argsJson, "_editor_quit_caller_args_hash");
            if (operationId.Length != 29 || !Regex.IsMatch(operationId, @"\Aquit-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}\z")) operationId = string.Empty;
            if (callerArgsHash.Length != 71 || !Regex.IsMatch(callerArgsHash, @"\Asha256:[0-9a-f]{64}\z")) callerArgsHash = string.Empty;
            project = BoundedQuitOwnerIdentity(project, 128);
            instanceId = BoundedQuitOwnerIdentity(instanceId, 256);
            canonicalProjectRoot = BoundedQuitOwnerIdentity(canonicalProjectRoot, 4096);
            DateTime startedUtc;
            if (string.IsNullOrEmpty(processStartUtc) || processStartUtc.Length > 64
                || !DateTime.TryParseExact(processStartUtc, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out startedUtc)
                || startedUtc.Kind != DateTimeKind.Utc) processStartUtc = string.Empty;
            var bindingComplete = operationId.Length > 0 && callerArgsHash.Length > 0 && project.Length > 0
                && instanceId.Length > 0 && canonicalProjectRoot.Length > 0 && processId > 0 && processStartUtc.Length > 0;
            var refusal = "{\"schema_version\":1,\"contract\":\"sgg.unity_mcp.editor_quit_preflight_refusal.v1\""
                + ",\"command\":\"editor.quit\",\"operation_id\":" + JsonUtil.String(operationId)
                + ",\"caller_args_hash\":" + JsonUtil.String(callerArgsHash)
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"phase\":\"preflight\",\"scope\":\"this_invocation\",\"binding_complete\":" + JsonUtil.Bool(bindingComplete)
                + ",\"owner\":{\"project\":" + JsonUtil.String(project)
                + ",\"instance_id\":" + JsonUtil.String(instanceId)
                + ",\"canonical_project_root\":" + JsonUtil.String(canonicalProjectRoot)
                + ",\"unity_pid\":" + Math.Max(0, processId)
                + ",\"unity_process_start_utc\":" + JsonUtil.String(processStartUtc) + "}"
                + ",\"close_requested\":false,\"quit_lifecycle_started\":false,\"exit_scheduled\":false,\"editor_exit_invoked\":false"
                + ",\"automatic_replay_attempted\":false,\"automatic_process_termination_attempted\":false"
                + ",\"retained_native_custody_observed\":false,\"retained_native_close_supported\":false}";
            return detailsJson.Substring(0, detailsJson.Length - 1)
                + ",\"close_requested\":false,\"editor_quit_preflight_refusal\":" + refusal + "}";
        }

        static string BoundedQuitOwnerIdentity(string value, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > maxChars) return string.Empty;
            for (var index = 0; index < value.Length; index++)
            {
                if (char.IsControl(value[index])) return string.Empty;
            }
            return value;
        }

        static string StatusJson(BridgeRuntimeState state, string compatibleWith = null, string playModeTarget = null, bool? transitionRequested = null, string playModeOperationId = null)
        {
            var editorState = CaptureEditModeEligibility();

            var json = editorState.NormalizedStateJson();
            json = json.Substring(0, json.Length - 1)
                + ",\"is_batch_mode\":" + JsonUtil.Bool(Application.isBatchMode)
                + ",\"play_mode_lifecycle_barrier\":" + PlayModeTransitionTracker.CurrentPlayModeBarrierJson()
                + ",\"playmode_transition_current\":" + PlayModeTransitionTracker.StatusJson()
                + ",\"unity_version\":" + JsonUtil.String(Application.unityVersion)
                + ",\"package_update\":" + (state == null ? "{}" : state.PackageUpdateJson())
                + ",\"has_unsaved_scene_changes\":" + JsonUtil.Bool(state != null && state.HasUnsavedSceneChanges)
                + ",\"dirty_scene_count\":" + (state == null ? 0 : state.DirtySceneCount)
                + ",\"dirty_scenes\":" + BuiltInCommands.DirtyScenesJson(state);
            if (!string.IsNullOrEmpty(compatibleWith))
            {
                json += ",\"compatible_with\":" + JsonUtil.String(compatibleWith);
            }
            if (!string.IsNullOrEmpty(playModeTarget))
            {
                json += ",\"playmode_target\":" + JsonUtil.String(playModeTarget)
                    + ",\"playmode_transition_requested\":" + JsonUtil.Bool(transitionRequested == true);
            }
            if (!string.IsNullOrEmpty(playModeOperationId))
            {
                json += ",\"playmode_transition\":" + PlayModeTransitionTracker.StatusJson(playModeOperationId);
            }

            return json + "}";
        }

        internal static EditorEditModeEligibility CaptureEditModeEligibility()
        {
            return EditorEditModeEligibility.From(
                EditorApplication.isCompiling,
                EditorApplication.isUpdating,
                EditorApplication.isPlaying,
                EditorApplication.isPlayingOrWillChangePlaymode,
                EditorApplication.isPaused);
        }

        internal static string Status(bool isCompiling, bool isUpdating, bool isPlaying, bool isPlayingOrWillChangePlaymode, bool isPaused)
        {
            if (isCompiling)
            {
                return "compiling";
            }

            if (isUpdating)
            {
                return "updating";
            }

            if ((isPlaying || isPlayingOrWillChangePlaymode) && isPaused)
            {
                return "playing_paused";
            }

            if (isPlaying || isPlayingOrWillChangePlaymode)
            {
                return "playing";
            }

            return "editing";
        }
    }
}
