using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class UitkInteractionCommands
    {
        const string InteractionFeatureFlag = "uitk_interaction_tests";
        const int MaxSteps = 16;
        const int MaxControlNodes = 5000;
        const int MaxDocumentNodes = 5000;
        const int MaxComponentIndex = 64;
        const int MaxTextChars = 4096;
        const int MaxSettleFrames = 120;
        const int MaxPreconditionHashChars = 128;
        const int MaxIdentityPathChars = 512;
        const int MaxControlNameChars = 128;
        const int MaxExpectedTypeChars = 64;
        const int MaxControlPathChars = 4096;
        const int MaxFailureTextChars = 240;

        const string InteractionSmokeSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"document_object_path\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":512},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"steps\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":16,\"items\":{\"type\":\"object\",\"properties\":{\"control_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":128},\"action\":{\"type\":\"string\",\"enum\":[\"focus\",\"click\",\"set_toggle\",\"set_text\",\"set_dropdown_index\"]},\"expected_control_type\":{\"type\":\"string\",\"enum\":[\"Button\",\"Toggle\",\"TextField\",\"DropdownField\"]},\"toggle_value\":{\"type\":\"boolean\"},\"text\":{\"type\":\"string\",\"maxLength\":4096},\"dropdown_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1024}},\"required\":[\"control_name\",\"action\"],\"oneOf\":[{\"type\":\"object\",\"properties\":{\"control_name\":{},\"action\":{\"enum\":[\"focus\",\"click\"]},\"expected_control_type\":{}},\"required\":[\"action\"],\"additionalProperties\":false},{\"type\":\"object\",\"properties\":{\"control_name\":{},\"action\":{\"enum\":[\"set_toggle\"]},\"expected_control_type\":{},\"toggle_value\":{}},\"required\":[\"action\",\"toggle_value\"],\"additionalProperties\":false},{\"type\":\"object\",\"properties\":{\"control_name\":{},\"action\":{\"enum\":[\"set_text\"]},\"expected_control_type\":{},\"text\":{}},\"required\":[\"action\",\"text\"],\"additionalProperties\":false},{\"type\":\"object\",\"properties\":{\"control_name\":{},\"action\":{\"enum\":[\"set_dropdown_index\"]},\"expected_control_type\":{},\"dropdown_index\":{}},\"required\":[\"action\",\"dropdown_index\"],\"additionalProperties\":false}],\"additionalProperties\":false}},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_execute\":{\"type\":\"boolean\",\"default\":false},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128},\"settle_frames\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":120,\"default\":0}},\"required\":[\"scene_path\",\"document_object_path\",\"component_index\",\"steps\"],\"oneOf\":[{\"type\":\"object\",\"properties\":{\"scene_path\":{},\"document_object_path\":{},\"component_index\":{},\"steps\":{},\"dry_run\":{\"enum\":[true]},\"settle_frames\":{}},\"additionalProperties\":false},{\"type\":\"object\",\"properties\":{\"scene_path\":{},\"document_object_path\":{},\"component_index\":{},\"steps\":{},\"dry_run\":{\"enum\":[false]},\"confirm_execute\":{\"enum\":[true]},\"precondition_hash\":{\"type\":\"string\",\"minLength\":1},\"settle_frames\":{}},\"required\":[\"dry_run\",\"confirm_execute\",\"precondition_hash\"],\"additionalProperties\":false}],\"additionalProperties\":false}";
        const string InteractionSmokeOutputLimitJson = "{\"max_steps\":16,\"max_control_nodes\":5000,\"max_control_path_chars\":4096,\"max_text_chars\":4096,\"max_failure_text_chars\":240,\"files_saved\":false,\"scenes_saved\":false,\"resource_storage\":\"none\"}";
        const string CallbackWarning = "UI Toolkit callbacks can mutate game state or throw; no callback side effects are rolled back and this command never saves files or scenes.";

        static readonly ConditionalWeakTable<VisualElement, ControlIdentity> s_controlIdentities = new ConditionalWeakTable<VisualElement, ControlIdentity>();
        static long s_nextControlIdentity;

        [McpCommand(
            "uitk.interaction.smoke",
            Title = "UI Toolkit interaction smoke",
            Description = "Dry-run-first Play Mode UI Toolkit interaction smoke for one exact loaded scene UIDocument and bounded named-control steps",
            Risk = "execute",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = InteractionSmokeSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "interaction", "smoke", "playmode" },
            Capability = InteractionFeatureFlag,
            RequiresEditorStatus = new string[] { "playing", "playing_paused" },
            ResultShape = "status",
            OutputLimitJson = InteractionSmokeOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InteractionFeatureFlag)]
        public static CommandExecutionResult InteractionSmoke(McpCommandContext context)
        {
            var parseResult = ParseRequest(context.ArgsJson, out var request);
            if (parseResult != null)
            {
                return parseResult;
            }

            if (!EditorApplication.isPlaying)
            {
                return CommandExecutionResult.Error("unity_busy", "uitk.interaction.smoke requires Play Mode");
            }

            if (!request.DryRun && !request.ConfirmExecute)
            {
                return CommandExecutionResult.Error(
                    "confirmation_required",
                    "confirm_execute must be true when uitk.interaction.smoke runs with dry_run:false",
                    "{\"command\":\"uitk.interaction.smoke\",\"required_arg\":\"confirm_execute\",\"dry_run_default\":true,\"callbacks_may_mutate_game_state\":true}");
            }

            if (request.SettleFrames > 0 && !EditorApplication.isPaused)
            {
                return CommandExecutionResult.Error(
                    "invalid_field_value",
                    "settle_frames greater than zero is allowed only while Play Mode is paused",
                    "{\"field\":\"settle_frames\",\"issue_code\":\"paused_playmode_required\",\"mutation_started\":false}");
            }

            var requestedFrameDelta = request.SettleFrames;
            var actualFrameDelta = 0;
            var settleApplied = false;

            var documentResult = TryResolveDocument(request, out var document);
            if (documentResult != null)
            {
                return documentResult;
            }

            var plansResult = BuildPlans(document, request, out var plans);
            if (plansResult != null)
            {
                return plansResult;
            }

            var preconditionHash = PreconditionHash(document, plans, request.SettleFrames);
            var canExecute = CanExecuteAll(plans);
            if (!request.DryRun && !string.Equals(request.PreconditionHash, preconditionHash, StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error(
                    "precondition_failed",
                    "precondition_hash does not match the exact current loaded scene, UIDocument identity, control tree, values, and request",
                    "{\"command\":\"uitk.interaction.smoke\",\"mutation_started\":false,\"expected_precondition_hash\":"
                    + JsonUtil.String(request.PreconditionHash)
                    + ",\"current_precondition_hash\":" + JsonUtil.String(preconditionHash) + "}");
            }

            if (!canExecute)
            {
                return CommandExecutionResult.Ok(ResultJson(
                    request,
                    document,
                    plans,
                    preconditionHash,
                    request.DryRun ? "dry_run" : "preflight_failed",
                    false,
                    false,
                    0,
                    requestedFrameDelta,
                    actualFrameDelta,
                    settleApplied,
                    false,
                    string.Empty));
            }

            if (request.DryRun)
            {
                return CommandExecutionResult.Ok(ResultJson(
                    request,
                    document,
                    plans,
                    preconditionHash,
                    "dry_run",
                    false,
                    false,
                    0,
                    requestedFrameDelta,
                    actualFrameDelta,
                    settleApplied,
                    false,
                    string.Empty));
            }

            var executedSteps = 0;
            var partialCallbackSideEffects = false;
            var failureJson = string.Empty;
            var terminalErrorCode = string.Empty;
            for (var index = 0; index < plans.Count; index++)
            {
                var plan = plans[index];
                var stateReason = CurrentExecutionStateConstraint(request.SettleFrames);
                if (!string.IsNullOrEmpty(stateReason))
                {
                    terminalErrorCode = "playmode_state_drift";
                    failureJson = FailureJson(terminalErrorCode, index, stateReason, executedSteps > 0, string.Empty, string.Empty);
                    break;
                }
                if (!TryResolveStepWithoutDrift(request, document, plan, out var driftReason))
                {
                    terminalErrorCode = "runtime_drift";
                    failureJson = FailureJson(terminalErrorCode, index, driftReason, executedSteps > 0, string.Empty, string.Empty);
                    break;
                }

                var execution = ExecuteTypedStep(plan);
                if (!execution.Success)
                {
                    terminalErrorCode = execution.ErrorCode;
                    partialCallbackSideEffects = execution.PartialCallbackSideEffects;
                    failureJson = FailureJson(
                        execution.ErrorCode,
                        index,
                        execution.Message,
                        execution.PartialCallbackSideEffects,
                        execution.ExceptionType,
                        execution.ExceptionMessage);
                    break;
                }

                plan.Executed = true;
                executedSteps++;
            }

            if (executedSteps == plans.Count && request.SettleFrames > 0)
            {
                var stateReason = CurrentExecutionStateConstraint(request.SettleFrames);
                if (!string.IsNullOrEmpty(stateReason))
                {
                    terminalErrorCode = "playmode_state_drift";
                    failureJson = FailureJson(terminalErrorCode, plans.Count, stateReason, true, string.Empty, string.Empty);
                }
                else
                {
                    var beforeFrame = Time.frameCount;
                    try
                    {
                        StepPausedFrames(request.SettleFrames);
                        settleApplied = true;
                        actualFrameDelta = Math.Max(0, Time.frameCount - beforeFrame);
                        if (actualFrameDelta < request.SettleFrames)
                        {
                            terminalErrorCode = "settle_frames_unverified";
                            failureJson = FailureJson(
                                terminalErrorCode,
                                plans.Count,
                                "Paused frame steps were requested, but the synchronous command observed fewer completed frames; use a separate status or screenshot settle operation before visual conclusions.",
                                false,
                                string.Empty,
                                string.Empty);
                        }
                    }
                    catch (Exception exception)
                    {
                        settleApplied = true;
                        actualFrameDelta = Math.Max(0, Time.frameCount - beforeFrame);
                        terminalErrorCode = "settle_frames_failed";
                        failureJson = FailureJson(
                            terminalErrorCode,
                            plans.Count,
                            "Paused Play Mode settling failed after interaction steps executed.",
                            false,
                            Bounded(exception.GetType().FullName, MaxFailureTextChars),
                            Bounded(exception.Message, MaxFailureTextChars));
                    }
                }
            }

            var status = string.IsNullOrEmpty(terminalErrorCode)
                ? "ok"
                : terminalErrorCode == "settle_frames_unverified" || terminalErrorCode == "settle_frames_failed"
                    ? "settle_unverified"
                    : "partial";
            var hadPartialSideEffects = partialCallbackSideEffects || executedSteps > 0 && executedSteps < plans.Count;
            var resultJson = ResultJson(
                request,
                document,
                plans,
                preconditionHash,
                status,
                true,
                executedSteps > 0 || partialCallbackSideEffects,
                executedSteps,
                requestedFrameDelta,
                actualFrameDelta,
                settleApplied,
                hadPartialSideEffects,
                failureJson);
            return string.IsNullOrEmpty(terminalErrorCode)
                ? CommandExecutionResult.Ok(resultJson)
                : CommandExecutionResult.Error(terminalErrorCode, "UI Toolkit interaction smoke did not complete the requested bounded operation", resultJson);
        }

        static CommandExecutionResult ParseRequest(string argsJson, out SmokeRequest request)
        {
            request = null;
            var scenePath = JsonUtil.ExtractString(argsJson, "scene_path");
            if (string.IsNullOrEmpty(scenePath) || scenePath.Length > MaxIdentityPathChars)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "scene_path is required and must be at most 512 characters");
            }

            var documentObjectPath = JsonUtil.ExtractString(argsJson, "document_object_path");
            if (string.IsNullOrEmpty(documentObjectPath) || documentObjectPath.Length > MaxIdentityPathChars || !documentObjectPath.StartsWith("/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "document_object_path is required, bounded, and must be an exact slash-prefixed hierarchy path");
            }

            if (!JsonUtil.HasProperty(argsJson, "component_index"))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "component_index is required");
            }

            var componentIndex = JsonUtil.ExtractInt(argsJson, "component_index", -1);
            if (componentIndex < 0 || componentIndex > MaxComponentIndex)
            {
                return CommandExecutionResult.Error("invalid_field_value", "component_index must be between 0 and 64");
            }

            var stepsJson = ExtractArray(argsJson, "steps");
            if (string.IsNullOrEmpty(stepsJson))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "steps is required and must be an array");
            }

            var stepObjects = new List<string>();
            if (!TryExtractObjectArrayItems(stepsJson, stepObjects) || stepObjects.Count < 1 || stepObjects.Count > MaxSteps)
            {
                return CommandExecutionResult.Error("invalid_field_value", "steps must contain between 1 and 16 object items");
            }

            var steps = new List<SmokeStepRequest>(stepObjects.Count);
            var names = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < stepObjects.Count; index++)
            {
                var stepJson = stepObjects[index];
                var controlName = JsonUtil.ExtractString(stepJson, "control_name");
                if (string.IsNullOrEmpty(controlName) || controlName.Length > MaxControlNameChars)
                {
                    return CommandExecutionResult.Error("schema_validation_failed", "each step requires a unique bounded control_name");
                }

                if (!names.Add(controlName))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "control_name must be unique across steps");
                }

                var action = JsonUtil.ExtractString(stepJson, "action");
                if (!IsSupportedAction(action))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "action must be focus, click, set_toggle, set_text, or set_dropdown_index");
                }

                var expectedControlType = JsonUtil.ExtractString(stepJson, "expected_control_type");
                if (!string.IsNullOrEmpty(expectedControlType)
                    && (!IsSupportedControlType(expectedControlType) || expectedControlType.Length > MaxExpectedTypeChars))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "expected_control_type must be Button, Toggle, TextField, or DropdownField");
                }

                var hasToggleValue = JsonUtil.HasProperty(stepJson, "toggle_value");
                var hasText = JsonUtil.HasProperty(stepJson, "text");
                var hasDropdownIndex = JsonUtil.HasProperty(stepJson, "dropdown_index");
                if (action == "set_toggle" && !hasToggleValue)
                {
                    return CommandExecutionResult.Error("schema_validation_failed", "set_toggle requires toggle_value");
                }
                if (action == "set_text" && !hasText)
                {
                    return CommandExecutionResult.Error("schema_validation_failed", "set_text requires text");
                }
                if (action == "set_dropdown_index" && !hasDropdownIndex)
                {
                    return CommandExecutionResult.Error("schema_validation_failed", "set_dropdown_index requires dropdown_index");
                }
                if (action != "set_toggle" && hasToggleValue
                    || action != "set_text" && hasText
                    || action != "set_dropdown_index" && hasDropdownIndex)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "action-specific fields may only be used with their matching action");
                }

                var text = JsonUtil.ExtractString(stepJson, "text");
                if (text.Length > MaxTextChars)
                {
                    return CommandExecutionResult.Error("invalid_field_value", "text must be at most 4096 characters");
                }

                var dropdownIndex = JsonUtil.ExtractInt(stepJson, "dropdown_index", -1);
                if (hasDropdownIndex && (dropdownIndex < 0 || dropdownIndex > 1024))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "dropdown_index must be between 0 and 1024");
                }

                steps.Add(new SmokeStepRequest(
                    index,
                    controlName,
                    action,
                    expectedControlType,
                    JsonUtil.ExtractBool(stepJson, "toggle_value", false),
                    text,
                    dropdownIndex));
            }

            var preconditionHash = JsonUtil.ExtractString(argsJson, "precondition_hash");
            if (preconditionHash.Length > MaxPreconditionHashChars)
            {
                return CommandExecutionResult.Error("invalid_field_value", "precondition_hash must be at most 128 characters");
            }

            request = new SmokeRequest(
                scenePath,
                documentObjectPath,
                componentIndex,
                steps,
                JsonUtil.ExtractBool(argsJson, "dry_run", true),
                JsonUtil.ExtractBool(argsJson, "confirm_execute", false),
                preconditionHash,
                JsonUtil.ExtractInt(argsJson, "settle_frames", 0));
            if (request.SettleFrames < 0 || request.SettleFrames > MaxSettleFrames)
            {
                return CommandExecutionResult.Error("invalid_field_value", "settle_frames must be between 0 and 120");
            }
            if (!request.DryRun && string.IsNullOrEmpty(request.PreconditionHash))
            {
                return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when dry_run:false");
            }

            return null;
        }

        static CommandExecutionResult TryResolveDocument(SmokeRequest request, out DocumentIdentity document)
        {
            document = null;
            Scene loadedScene = default(Scene);
            var sceneMatches = 0;
            for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount; sceneIndex++)
            {
                var scene = SceneManager.GetSceneAt(sceneIndex);
                if (scene.IsValid() && scene.isLoaded && string.Equals(scene.path, request.ScenePath, StringComparison.Ordinal))
                {
                    loadedScene = scene;
                    sceneMatches++;
                }
            }

            if (sceneMatches == 0)
            {
                return CommandExecutionResult.Error("scene_not_loaded", "scene_path must identify exactly one currently loaded scene");
            }
            if (sceneMatches > 1)
            {
                return CommandExecutionResult.Error("ambiguous_scene_path", "scene_path matched more than one loaded scene");
            }

            GameObject targetObject = null;
            var targetMatches = 0;
            var visited = 0;
            var truncated = false;
            var roots = loadedScene.GetRootGameObjects();
            var pending = new Stack<Transform>();
            for (var rootIndex = roots.Length - 1; rootIndex >= 0; rootIndex--)
            {
                if (roots[rootIndex] != null)
                {
                    pending.Push(roots[rootIndex].transform);
                }
            }

            while (pending.Count > 0)
            {
                if (visited >= MaxDocumentNodes)
                {
                    truncated = true;
                    break;
                }

                var current = pending.Pop();
                visited++;
                if (current != null && string.Equals(SceneObjectPath(current), request.DocumentObjectPath, StringComparison.Ordinal))
                {
                    targetObject = current.gameObject;
                    targetMatches++;
                }

                if (current == null)
                {
                    continue;
                }
                for (var childIndex = current.childCount - 1; childIndex >= 0; childIndex--)
                {
                    var child = current.GetChild(childIndex);
                    if (child != null)
                    {
                        pending.Push(child);
                    }
                }
            }

            if (truncated)
            {
                return CommandExecutionResult.Error("document_hierarchy_truncated", "document object resolution exceeded the bounded 5000-node traversal");
            }
            if (targetMatches == 0 || targetObject == null)
            {
                return CommandExecutionResult.Error("document_object_not_found", "document_object_path did not resolve exactly in the loaded scene");
            }
            if (targetMatches > 1)
            {
                return CommandExecutionResult.Error("ambiguous_document_object_path", "document_object_path matched more than one loaded object");
            }

            var components = targetObject.GetComponents<Component>();
            if (request.ComponentIndex < 0 || request.ComponentIndex >= components.Length)
            {
                return CommandExecutionResult.Error("component_not_found", "component_index does not identify a component on the exact document object");
            }

            var documentComponent = components[request.ComponentIndex] as UIDocument;
            if (documentComponent == null)
            {
                return CommandExecutionResult.Error("component_type_mismatch", "component_index must identify a UIDocument component");
            }

            VisualElement root;
            try
            {
                root = documentComponent.rootVisualElement;
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("document_root_unavailable", "UIDocument rootVisualElement could not be resolved: " + Bounded(exception.Message, MaxFailureTextChars));
            }

            if (root == null)
            {
                return CommandExecutionResult.Error("document_root_unavailable", "UIDocument rootVisualElement is null");
            }
            if (root.panel == null)
            {
                return CommandExecutionResult.Error("document_panel_unavailable", "UIDocument rootVisualElement is not attached to a live UI Toolkit panel");
            }

            document = new DocumentIdentity(
                loadedScene,
                request.ScenePath,
                request.DocumentObjectPath,
                request.ComponentIndex,
                documentComponent,
                targetObject,
                root);
            return null;
        }

        static CommandExecutionResult BuildPlans(DocumentIdentity document, SmokeRequest request, out List<SmokeStepPlan> plans)
        {
            plans = new List<SmokeStepPlan>(request.Steps.Count);
            if (!TryCollectNamedControls(document.Root, out var namedControls, out var traversalReason))
            {
                return CommandExecutionResult.Error("control_tree_truncated", traversalReason);
            }

            for (var index = 0; index < request.Steps.Count; index++)
            {
                var step = request.Steps[index];
                var plan = new SmokeStepPlan(step);
                if (!namedControls.TryGetValue(step.ControlName, out var candidates) || candidates.Count == 0)
                {
                    plan.CanExecute = false;
                    plan.Reason = "control_name was not found by exact name in the bounded visual tree";
                    plans.Add(plan);
                    continue;
                }
                if (candidates.Count > 1)
                {
                    plan.CanExecute = false;
                    plan.Reason = "control_name is ambiguous because the visual tree contains duplicate exact names";
                    plans.Add(plan);
                    continue;
                }

                plan.Control = candidates[0];
                plan.ControlType = ControlTypeName(plan.Control);
                plan.ControlPath = VisualHierarchyPath(document.Root, plan.Control);
                plan.ControlIdentity = ControlRuntimeIdentity(plan.Control);
                plan.EnabledInHierarchy = plan.Control.enabledInHierarchy;
                plan.Focusable = plan.Control.focusable;
                if (plan.Control.panel == null || !object.ReferenceEquals(plan.Control.panel, document.Root.panel))
                {
                    plan.CanExecute = false;
                    plan.Reason = "control is not attached to the exact UIDocument live panel";
                    plans.Add(plan);
                    continue;
                }
                if (string.IsNullOrEmpty(plan.ControlPath) || plan.ControlPath.Length > MaxControlPathChars)
                {
                    plan.CanExecute = false;
                    plan.Reason = "control hierarchy path is unavailable or exceeds the bounded 4096-character limit";
                    plans.Add(plan);
                    continue;
                }
                try
                {
                    plan.CurrentValueJson = PrimitiveValueJson(plan.Control);
                }
                catch (Exception exception)
                {
                    plan.CanExecute = false;
                    plan.Reason = "control primitive value could not be read: " + Bounded(exception.Message, MaxFailureTextChars);
                    plans.Add(plan);
                    continue;
                }

                plan.RestorationValueJson = plan.CurrentValueJson;
                var validationReason = ValidateActionCompatibility(plan.Control, step.Action, step.ExpectedControlType, step.DropdownIndex);
                if (string.IsNullOrEmpty(validationReason))
                {
                    try
                    {
                        plan.RequestedValueJson = RequestedValueJson(step, plan.Control);
                        plan.CanExecute = true;
                    }
                    catch (Exception exception)
                    {
                        plan.CanExecute = false;
                        plan.Reason = "requested control value could not be bounded: " + Bounded(exception.Message, MaxFailureTextChars);
                    }
                }
                else
                {
                    plan.CanExecute = false;
                    plan.Reason = validationReason;
                }

                plans.Add(plan);
            }

            return null;
        }

        static bool TryCollectNamedControls(VisualElement root, out Dictionary<string, List<VisualElement>> namedControls, out string reason)
        {
            namedControls = new Dictionary<string, List<VisualElement>>(StringComparer.Ordinal);
            reason = string.Empty;
            if (root == null)
            {
                reason = "UIDocument rootVisualElement is null";
                return false;
            }

            var pending = new Stack<VisualElement>();
            pending.Push(root);
            var visited = 0;
            try
            {
                while (pending.Count > 0)
                {
                    if (visited >= MaxControlNodes)
                    {
                        reason = "visual tree traversal exceeded the bounded 5000-node limit";
                        return false;
                    }

                    var current = pending.Pop();
                    visited++;
                    if (current != null && !string.IsNullOrEmpty(current.name))
                    {
                        if (!namedControls.TryGetValue(current.name, out var values))
                        {
                            values = new List<VisualElement>();
                            namedControls.Add(current.name, values);
                        }
                        values.Add(current);
                    }

                    if (current == null)
                    {
                        continue;
                    }

                    for (var childIndex = current.hierarchy.childCount - 1; childIndex >= 0; childIndex--)
                    {
                        var child = current.hierarchy.ElementAt(childIndex);
                        if (child != null)
                        {
                            pending.Push(child);
                        }
                    }
                }
            }
            catch (Exception exception)
            {
                reason = "visual tree traversal failed: " + Bounded(exception.Message, MaxFailureTextChars);
                return false;
            }

            return true;
        }

        static bool TryResolveNamedControl(VisualElement root, string controlName, out VisualElement control, out string reason)
        {
            control = null;
            reason = string.Empty;
            if (!TryCollectNamedControls(root, out var namedControls, out reason))
            {
                return false;
            }
            if (!namedControls.TryGetValue(controlName, out var candidates) || candidates.Count == 0)
            {
                reason = "control_name was not found by exact name in the bounded visual tree";
                return false;
            }
            if (candidates.Count > 1)
            {
                reason = "control_name is ambiguous because the visual tree contains duplicate exact names";
                return false;
            }

            control = candidates[0];
            return true;
        }

        static string ValidateActionCompatibility(VisualElement control, string action, string expectedControlType, int dropdownIndex)
        {
            if (control == null)
            {
                return "control is null";
            }

            var controlType = ControlTypeName(control);
            if (!IsSupportedControlType(controlType))
            {
                return "control type is not allowlisted; only Button, Toggle, TextField, and DropdownField are supported";
            }
            if (!string.IsNullOrEmpty(expectedControlType) && !string.Equals(expectedControlType, controlType, StringComparison.Ordinal))
            {
                return "expected_control_type does not match the exact resolved control type";
            }
            if (!control.enabledInHierarchy)
            {
                return "control is disabled in the current visual hierarchy";
            }

            if (action == "focus")
            {
                return control.focusable ? string.Empty : "focus requires a focusable control";
            }
            if (action == "click")
            {
                if (!(control is Button)) return "click is compatible only with Button";
                return control.focusable ? string.Empty : "click requires a focusable Button";
            }
            if (action == "set_toggle")
            {
                return control is Toggle ? string.Empty : "set_toggle is compatible only with Toggle";
            }
            if (action == "set_text")
            {
                return control is TextField ? string.Empty : "set_text is compatible only with TextField";
            }
            if (action == "set_dropdown_index")
            {
                var dropdown = control as DropdownField;
                if (dropdown == null)
                {
                    return "set_dropdown_index is compatible only with DropdownField";
                }
                if (dropdownIndex < 0 || dropdownIndex >= dropdown.choices.Count)
                {
                    return "dropdown_index must identify an existing DropdownField choice";
                }
                var requestedChoice = dropdown.choices[dropdownIndex];
                if (requestedChoice != null && requestedChoice.Length > MaxTextChars)
                {
                    return "requested DropdownField choice exceeds the bounded 4096-character limit";
                }
                return string.Empty;
            }

            return "action is not supported";
        }

        static ExecutionOutcome ExecuteTypedStep(SmokeStepPlan plan)
        {
            try
            {
                switch (plan.Request.Action)
                {
                    case "focus":
                        plan.Control.Focus();
                        return ExecutionOutcome.Succeeded();
                    case "click":
                        var button = plan.Control as Button;
                        if (button == null)
                        {
                            return ExecutionOutcome.Failed("action_incompatible", "click is compatible only with Button");
                        }
                        button.Focus();
                        if (button.panel == null || button.panel.focusController == null || !object.ReferenceEquals(button.panel.focusController.focusedElement, button))
                        {
                            return ExecutionOutcome.Failed("focus_not_acquired", "Button did not acquire focus on its attached panel before navigation submit");
                        }
                        using (var submitEvent = NavigationSubmitEvent.GetPooled())
                        {
                            submitEvent.target = button;
                            button.SendEvent(submitEvent);
                        }
                        return ExecutionOutcome.Succeeded();
                    case "set_toggle":
                        var toggle = plan.Control as Toggle;
                        if (toggle == null)
                        {
                            return ExecutionOutcome.Failed("action_incompatible", "set_toggle is compatible only with Toggle");
                        }
                        toggle.value = plan.Request.ToggleValue;
                        return ExecutionOutcome.Succeeded();
                    case "set_text":
                        var textField = plan.Control as TextField;
                        if (textField == null)
                        {
                            return ExecutionOutcome.Failed("action_incompatible", "set_text is compatible only with TextField");
                        }
                        textField.value = plan.Request.Text;
                        return ExecutionOutcome.Succeeded();
                    case "set_dropdown_index":
                        var dropdown = plan.Control as DropdownField;
                        if (dropdown == null)
                        {
                            return ExecutionOutcome.Failed("action_incompatible", "set_dropdown_index is compatible only with DropdownField");
                        }
                        dropdown.index = plan.Request.DropdownIndex;
                        return ExecutionOutcome.Succeeded();
                    default:
                        return ExecutionOutcome.Failed("action_incompatible", "action is not supported");
                }
            }
            catch (Exception exception)
            {
                return ExecutionOutcome.CallbackFailure(
                    "callback_exception",
                    "A typed UI Toolkit API or callback threw; partial callback side effects are possible",
                    Bounded(exception.GetType().FullName, MaxFailureTextChars),
                    Bounded(exception.Message, MaxFailureTextChars));
            }
        }

        static bool TryResolveStepWithoutDrift(SmokeRequest request, DocumentIdentity expectedDocument, SmokeStepPlan plan, out string reason)
        {
            reason = string.Empty;
            var result = TryResolveDocument(request, out var currentDocument);
            if (result != null)
            {
                reason = "exact scene or UIDocument identity no longer resolves";
                return false;
            }
            if (GameViewSceneIdentity.HandleIdentity(currentDocument.Scene) != GameViewSceneIdentity.HandleIdentity(expectedDocument.Scene)
                || !string.Equals(RuntimeObjectIdentity(currentDocument.Document), RuntimeObjectIdentity(expectedDocument.Document), StringComparison.Ordinal)
                || !string.Equals(RuntimeObjectIdentity(currentDocument.GameObject), RuntimeObjectIdentity(expectedDocument.GameObject), StringComparison.Ordinal)
                || currentDocument.ComponentIndex != expectedDocument.ComponentIndex)
            {
                reason = "exact loaded scene, document object, or UIDocument component identity drifted";
                return false;
            }
            if (!TryResolveNamedControl(currentDocument.Root, plan.Request.ControlName, out var currentControl, out reason))
            {
                return false;
            }
            if (!object.ReferenceEquals(currentControl, plan.Control) || ControlRuntimeIdentity(currentControl) != plan.ControlIdentity)
            {
                reason = "control runtime identity drifted";
                return false;
            }
            if (currentControl.panel == null || !object.ReferenceEquals(currentControl.panel, currentDocument.Root.panel))
            {
                reason = "control live panel identity drifted";
                return false;
            }
            if (!string.Equals(VisualHierarchyPath(currentDocument.Root, currentControl), plan.ControlPath, StringComparison.Ordinal))
            {
                reason = "control hierarchy path drifted";
                return false;
            }
            if (!string.Equals(ControlTypeName(currentControl), plan.ControlType, StringComparison.Ordinal))
            {
                reason = "control type drifted";
                return false;
            }
            var compatibilityReason = ValidateActionCompatibility(currentControl, plan.Request.Action, plan.Request.ExpectedControlType, plan.Request.DropdownIndex);
            if (!string.IsNullOrEmpty(compatibilityReason))
            {
                reason = "control action precondition drifted: " + compatibilityReason;
                return false;
            }
            if (!TryRevalidateRequestedValue(plan.Request, currentControl, plan.RequestedValueJson, out reason))
            {
                return false;
            }
            if (currentControl.enabledInHierarchy != plan.EnabledInHierarchy || currentControl.focusable != plan.Focusable)
            {
                reason = "control enabled or focusable state drifted";
                return false;
            }
            string currentValueJson;
            try
            {
                currentValueJson = PrimitiveValueJson(currentControl);
            }
            catch (Exception exception)
            {
                reason = "control primitive value could not be revalidated: " + Bounded(exception.Message, MaxFailureTextChars);
                return false;
            }
            if (!string.Equals(currentValueJson, plan.CurrentValueJson, StringComparison.Ordinal))
            {
                reason = "control primitive value drifted after preflight";
                return false;
            }

            plan.Control = currentControl;
            return true;
        }

        static void StepPausedFrames(int frames)
        {
            for (var index = 0; index < frames; index++)
            {
                EditorApplication.Step();
                EditorApplication.QueuePlayerLoopUpdate();
            }
        }

        static bool CanExecuteAll(List<SmokeStepPlan> plans)
        {
            for (var index = 0; index < plans.Count; index++)
            {
                if (!plans[index].CanExecute)
                {
                    return false;
                }
            }
            return plans.Count > 0;
        }

        static string ResultJson(
            SmokeRequest request,
            DocumentIdentity document,
            List<SmokeStepPlan> plans,
            string preconditionHash,
            string status,
            bool executionStarted,
            bool mutationStarted,
            int executedSteps,
            int requestedFrameDelta,
            int actualFrameDelta,
            bool settleApplied,
            bool partialCallbackSideEffects,
            string failureJson)
        {
            var canExecute = CanExecuteAll(plans);
            var builder = new StringBuilder();
            builder.Append("{\"status\":").Append(JsonUtil.String(status));
            builder.Append(",\"command\":\"uitk.interaction.smoke\"");
            builder.Append(",\"dry_run\":").Append(JsonUtil.Bool(request.DryRun));
            builder.Append(",\"executed\":").Append(JsonUtil.Bool(executionStarted));
            builder.Append(",\"mutation_started\":").Append(JsonUtil.Bool(mutationStarted));
            builder.Append(",\"executed_steps\":").Append(executedSteps);
            builder.Append(",\"can_execute\":").Append(JsonUtil.Bool(canExecute));
            builder.Append(",\"scene_path\":").Append(JsonUtil.String(request.ScenePath));
            builder.Append(",\"document_object_path\":").Append(JsonUtil.String(request.DocumentObjectPath));
            builder.Append(",\"component_index\":").Append(request.ComponentIndex);
            builder.Append(",\"runtime_identity\":{\"scene_handle\":").Append(GameViewSceneIdentity.HandleIdentity(document.Scene));
            builder.Append(",\"document_runtime_id\":").Append(JsonUtil.String(RuntimeObjectIdentity(document.Document)));
            builder.Append(",\"document_game_object_runtime_id\":").Append(JsonUtil.String(RuntimeObjectIdentity(document.GameObject)));
            builder.Append("}");
            builder.Append(",\"precondition_hash\":").Append(JsonUtil.String(preconditionHash));
            builder.Append(",\"steps\":[");
            for (var index = 0; index < plans.Count; index++)
            {
                if (index > 0) builder.Append(',');
                AppendStepJson(builder, plans[index]);
            }
            builder.Append(']');
            builder.Append(",\"restoration_steps\":").Append(RestorationStepsJson(plans, request.DryRun));
            builder.Append(",\"restoration_requires_new_dry_run\":true");
            builder.Append(",\"automatic_rollback\":false");
            builder.Append(",\"execution_constraints\":{");
            builder.Append("\"requires_play_mode\":true");
            builder.Append(",\"allowed_editor_states\":[\"playing\",\"playing_paused\"]");
            builder.Append(",\"running_play_mode_requires_settle_frames_zero\":true");
            builder.Append(",\"settle_frames_requested\":").Append(requestedFrameDelta);
            builder.Append(",\"settle_frames_applied\":").Append(JsonUtil.Bool(settleApplied));
            builder.Append(",\"actual_frame_count_delta\":").Append(actualFrameDelta);
            builder.Append(",\"actual_frame_delta_observed\":").Append(JsonUtil.Bool(settleApplied));
            builder.Append(",\"settle_completion_verified\":").Append(JsonUtil.Bool(requestedFrameDelta == 0 || settleApplied && actualFrameDelta >= requestedFrameDelta));
            builder.Append(",\"callbacks_may_mutate_game_state\":true");
            builder.Append(",\"typed_ui_toolkit_apis_only\":true");
            builder.Append(",\"button_activation\":\"Button.Focus_then_NavigationSubmitEvent.GetPooled_with_exact_target_then_Button.SendEvent\"");
            builder.Append(",\"general_reflection\":false,\"dynamic_csharp\":false");
            builder.Append(",\"opens_scenes\":false,\"saves_scenes\":false,\"saves_files\":false");
            builder.Append("}");
            builder.Append(",\"files_saved\":false,\"scenes_saved\":false");
            builder.Append(",\"partial_callback_side_effects\":").Append(JsonUtil.Bool(partialCallbackSideEffects));
            builder.Append(",\"callbacks_warning\":").Append(JsonUtil.String(CallbackWarning));
            builder.Append(",\"failure\":").Append(string.IsNullOrEmpty(failureJson) ? "null" : failureJson);
            builder.Append(",\"next_valid_actions\":[\"uitk.document.inspect\",\"uitk.interaction.smoke\",\"game.view.screenshot\",\"runtime.capture_state\"]");
            builder.Append('}');
            return builder.ToString();
        }

        static void AppendStepJson(StringBuilder builder, SmokeStepPlan plan)
        {
            builder.Append('{');
            builder.Append("\"index\":").Append(plan.Request.Index);
            builder.Append(",\"control_name\":").Append(JsonUtil.String(plan.Request.ControlName));
            builder.Append(",\"action\":").Append(JsonUtil.String(plan.Request.Action));
            builder.Append(",\"expected_control_type\":").Append(JsonUtil.String(plan.Request.ExpectedControlType));
            builder.Append(",\"control_type\":").Append(JsonUtil.String(string.IsNullOrEmpty(plan.ControlType) ? "unknown" : plan.ControlType));
            builder.Append(",\"control_hierarchy_path\":").Append(JsonUtil.String(plan.ControlPath));
            builder.Append(",\"current_value\":").Append(string.IsNullOrEmpty(plan.CurrentValueJson) ? "null" : plan.CurrentValueJson);
            builder.Append(",\"requested_value\":").Append(string.IsNullOrEmpty(plan.RequestedValueJson) ? "null" : plan.RequestedValueJson);
            builder.Append(",\"enabled_in_hierarchy\":").Append(JsonUtil.Bool(plan.EnabledInHierarchy));
            builder.Append(",\"focusable\":").Append(JsonUtil.Bool(plan.Focusable));
            builder.Append(",\"can_execute\":").Append(JsonUtil.Bool(plan.CanExecute));
            builder.Append(",\"executed\":").Append(JsonUtil.Bool(plan.Executed));
            builder.Append(",\"irreversible\":").Append(JsonUtil.Bool(plan.Request.Action == "focus" || plan.Request.Action == "click"));
            builder.Append(",\"reason\":").Append(string.IsNullOrEmpty(plan.Reason) ? "null" : JsonUtil.String(Bounded(plan.Reason, MaxFailureTextChars)));
            builder.Append('}');
        }

        static string RestorationStepsJson(List<SmokeStepPlan> plans, bool includeUnexecuted)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var first = true;
            for (var index = plans.Count - 1; index >= 0; index--)
            {
                var plan = plans[index];
                if (!IsSetAction(plan.Request.Action) || !plan.CanExecute || !includeUnexecuted && !plan.Executed)
                {
                    continue;
                }
                if (!first) builder.Append(',');
                first = false;
                builder.Append('{');
                builder.Append("\"step_index\":").Append(plan.Request.Index);
                builder.Append(",\"control_name\":").Append(JsonUtil.String(plan.Request.ControlName));
                builder.Append(",\"action\":").Append(JsonUtil.String(plan.Request.Action));
                builder.Append(",\"expected_control_type\":").Append(JsonUtil.String(plan.ControlType));
                builder.Append(",\"control_hierarchy_path\":").Append(JsonUtil.String(plan.ControlPath));
                builder.Append(",\"restore_value\":").Append(string.IsNullOrEmpty(plan.RestorationValueJson) ? "null" : plan.RestorationValueJson);
                AppendRestorationRequestField(builder, plan.Request.Action, plan.RestorationValueJson);
                builder.Append(",\"reverse_order\":true,\"best_effort\":true,\"automatic_rollback\":false");
                builder.Append('}');
            }
            builder.Append(']');
            return builder.ToString();
        }

        static void AppendRestorationRequestField(StringBuilder builder, string action, string restorationValueJson)
        {
            if (action == "set_toggle")
            {
                builder.Append(",\"toggle_value\":").Append(restorationValueJson);
            }
            else if (action == "set_text")
            {
                builder.Append(",\"text\":").Append(restorationValueJson);
            }
            else if (action == "set_dropdown_index")
            {
                builder.Append(",\"dropdown_index\":").Append(restorationValueJson);
            }
        }

        static string FailureJson(string code, int stepIndex, string message, bool partialCallbackSideEffects, string exceptionType, string exceptionMessage)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"code\":").Append(JsonUtil.String(code));
            builder.Append(",\"step_index\":").Append(stepIndex);
            builder.Append(",\"message\":").Append(JsonUtil.String(Bounded(message, MaxFailureTextChars)));
            builder.Append(",\"partial_callback_side_effects\":").Append(JsonUtil.Bool(partialCallbackSideEffects));
            builder.Append(",\"exception_type\":").Append(string.IsNullOrEmpty(exceptionType) ? "null" : JsonUtil.String(Bounded(exceptionType, MaxFailureTextChars)));
            builder.Append(",\"exception_message\":").Append(string.IsNullOrEmpty(exceptionMessage) ? "null" : JsonUtil.String(Bounded(exceptionMessage, MaxFailureTextChars)));
            builder.Append('}');
            return builder.ToString();
        }

        static string PreconditionHash(DocumentIdentity document, List<SmokeStepPlan> plans, int settleFrames)
        {
            var builder = new StringBuilder();
            builder.Append("uitk.interaction.smoke/v1\n");
            AppendHashField(builder, "scene_path", document.ScenePath);
            AppendHashField(builder, "scene_handle", GameViewSceneIdentity.HandleIdentity(document.Scene).ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "document_object_path", document.DocumentObjectPath);
            AppendHashField(builder, "document_runtime_id", RuntimeObjectIdentity(document.Document));
            AppendHashField(builder, "document_game_object_runtime_id", RuntimeObjectIdentity(document.GameObject));
            AppendHashField(builder, "component_index", document.ComponentIndex.ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "settle_frames", settleFrames.ToString(CultureInfo.InvariantCulture));
            for (var index = 0; index < plans.Count; index++)
            {
                var plan = plans[index];
                AppendHashField(builder, "step_index", plan.Request.Index.ToString(CultureInfo.InvariantCulture));
                AppendHashField(builder, "control_name", plan.Request.ControlName);
                AppendHashField(builder, "action", plan.Request.Action);
                AppendHashField(builder, "expected_control_type", plan.Request.ExpectedControlType);
                AppendHashField(builder, "control_hierarchy_path", plan.ControlPath);
                AppendHashField(builder, "control_type", plan.ControlType);
                AppendHashField(builder, "control_runtime_identity", plan.ControlIdentity.ToString(CultureInfo.InvariantCulture));
                AppendHashField(builder, "enabled_in_hierarchy", JsonUtil.Bool(plan.EnabledInHierarchy));
                AppendHashField(builder, "focusable", JsonUtil.Bool(plan.Focusable));
                AppendHashField(builder, "current_primitive_value", plan.CurrentValueJson);
                AppendHashField(builder, "requested_primitive_value", plan.RequestedValueJson);
            }

            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var hex = new StringBuilder(digest.Length * 2 + 7);
                hex.Append("sha256:");
                for (var index = 0; index < digest.Length; index++)
                {
                    hex.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                }
                return hex.ToString();
            }
        }

        static void AppendHashField(StringBuilder builder, string key, string value)
        {
            value = value ?? string.Empty;
            builder.Append(key).Append('=').Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value).Append('\n');
        }

        static string PrimitiveValueJson(VisualElement control)
        {
            var toggle = control as Toggle;
            if (toggle != null)
            {
                return JsonUtil.Bool(toggle.value);
            }
            var textField = control as TextField;
            if (textField != null)
            {
                var value = textField.value ?? string.Empty;
                if (value.Length > MaxTextChars)
                {
                    throw new InvalidOperationException("current TextField value exceeds the bounded 4096-character limit");
                }
                return JsonUtil.String(value);
            }
            var dropdown = control as DropdownField;
            if (dropdown != null)
            {
                return dropdown.index.ToString(CultureInfo.InvariantCulture);
            }
            return "null";
        }

        static string RequestedValueJson(SmokeStepRequest step, VisualElement control)
        {
            if (step.Action == "set_toggle")
            {
                return JsonUtil.Bool(step.ToggleValue);
            }
            if (step.Action == "set_text")
            {
                return JsonUtil.String(step.Text);
            }
            if (step.Action == "set_dropdown_index")
            {
                var dropdown = control as DropdownField;
                if (dropdown == null || step.DropdownIndex < 0 || step.DropdownIndex >= dropdown.choices.Count)
                {
                    throw new InvalidOperationException("dropdown_index no longer identifies an existing DropdownField choice");
                }
                var choice = dropdown.choices[step.DropdownIndex];
                if (choice != null && choice.Length > MaxTextChars)
                {
                    throw new InvalidOperationException("requested DropdownField choice exceeds the bounded 4096-character limit");
                }
                return "{\"dropdown_index\":" + step.DropdownIndex.ToString(CultureInfo.InvariantCulture)
                    + ",\"choice\":" + (choice == null ? "null" : JsonUtil.String(choice)) + "}";
            }
            return "null";
        }

        static bool TryRevalidateRequestedValue(SmokeStepRequest step, VisualElement control, string expectedValueJson, out string reason)
        {
            reason = string.Empty;
            string requestedValueJson;
            try
            {
                requestedValueJson = RequestedValueJson(step, control);
            }
            catch (Exception exception)
            {
                reason = "requested control value could not be revalidated: " + Bounded(exception.Message, MaxFailureTextChars);
                return false;
            }
            if (!string.Equals(requestedValueJson, expectedValueJson, StringComparison.Ordinal))
            {
                reason = "requested control value drifted after preflight";
                return false;
            }
            return true;
        }

        static string ControlTypeName(VisualElement control)
        {
            if (control is Button) return "Button";
            if (control is Toggle) return "Toggle";
            if (control is TextField) return "TextField";
            if (control is DropdownField) return "DropdownField";
            return "Unsupported";
        }

        static bool IsSupportedControlType(string controlType)
        {
            return controlType == "Button"
                || controlType == "Toggle"
                || controlType == "TextField"
                || controlType == "DropdownField";
        }

        static bool IsSupportedAction(string action)
        {
            return action == "focus"
                || action == "click"
                || action == "set_toggle"
                || action == "set_text"
                || action == "set_dropdown_index";
        }

        static bool IsSetAction(string action)
        {
            return action == "set_toggle" || action == "set_text" || action == "set_dropdown_index";
        }

        static string CurrentExecutionStateConstraint(int settleFrames)
        {
            if (!EditorApplication.isPlaying)
            {
                return "Play Mode ended or entered a transition after preflight";
            }
            if (settleFrames > 0 && !EditorApplication.isPaused)
            {
                return "Play Mode is no longer paused for the requested bounded frame settling";
            }
            return string.Empty;
        }

        static long ControlRuntimeIdentity(VisualElement control)
        {
            return control == null
                ? 0
                : s_controlIdentities.GetValue(control, _ => new ControlIdentity(Interlocked.Increment(ref s_nextControlIdentity))).Value;
        }

        static string SceneObjectPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }
            var names = new List<string>();
            var current = transform;
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
            return builder.ToString();
        }

        static string RuntimeObjectIdentity(UnityEngine.Object unityObject)
        {
            if (unityObject == null)
            {
                return string.Empty;
            }
#if UNITY_6000_4_OR_NEWER
            return "entity:" + UnityEngine.EntityId.ToULong(unityObject.GetEntityId()).ToString(CultureInfo.InvariantCulture);
#else
            return "instance:" + unityObject.GetInstanceID().ToString(CultureInfo.InvariantCulture);
#endif
        }

        static string VisualHierarchyPath(VisualElement root, VisualElement control)
        {
            if (root == null || control == null)
            {
                return string.Empty;
            }

            var segments = new List<string>();
            var current = control;
            while (current != null)
            {
                if (object.ReferenceEquals(current, root))
                {
                    segments.Add(VisualSegment(current, -1));
                    segments.Reverse();
                    return "/" + string.Join("/", segments.ToArray());
                }

                var parent = current.parent;
                if (parent == null)
                {
                    return string.Empty;
                }
                var childIndex = -1;
                for (var index = 0; index < parent.hierarchy.childCount; index++)
                {
                    if (object.ReferenceEquals(parent.hierarchy.ElementAt(index), current))
                    {
                        childIndex = index;
                        break;
                    }
                }
                if (childIndex < 0)
                {
                    return string.Empty;
                }
                segments.Add(VisualSegment(current, childIndex));
                current = parent;
            }
            return string.Empty;
        }

        static string VisualSegment(VisualElement element, int childIndex)
        {
            var type = ControlTypeName(element);
            if (type == "Unsupported")
            {
                type = "VisualElement";
            }
            var suffix = childIndex < 0 ? "" : "[" + childIndex.ToString(CultureInfo.InvariantCulture) + "]";
            return type + suffix + (string.IsNullOrEmpty(element.name) ? string.Empty : ":" + element.name);
        }

        static string ExtractArray(string json, string property)
        {
            var propertyIndex = FindProperty(json, property);
            if (propertyIndex < 0)
            {
                return string.Empty;
            }
            var start = propertyIndex;
            while (start < json.Length && char.IsWhiteSpace(json[start])) start++;
            if (start >= json.Length || json[start] != '[')
            {
                return string.Empty;
            }

            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;
                if (character == '[') depth++;
                if (character == ']')
                {
                    depth--;
                    if (depth == 0) return json.Substring(start, index - start + 1);
                }
            }
            return string.Empty;
        }

        static bool TryExtractObjectArrayItems(string arrayJson, List<string> items)
        {
            var start = 0;
            while (start < arrayJson.Length && char.IsWhiteSpace(arrayJson[start])) start++;
            if (start >= arrayJson.Length || arrayJson[start] != '[') return false;

            var depth = 0;
            var objectStart = -1;
            var inString = false;
            var escaped = false;
            for (var index = start + 1; index < arrayJson.Length; index++)
            {
                var character = arrayJson[index];
                if (escaped)
                {
                    escaped = false;
                    continue;
                }
                if (character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                {
                    inString = !inString;
                    continue;
                }
                if (inString) continue;
                if (character == '{')
                {
                    if (depth == 0) objectStart = index;
                    depth++;
                }
                else if (character == '}')
                {
                    depth--;
                    if (depth < 0) return false;
                    if (depth == 0 && objectStart >= 0)
                    {
                        items.Add(arrayJson.Substring(objectStart, index - objectStart + 1));
                        objectStart = -1;
                    }
                }
                else if (character == ']' && depth == 0)
                {
                    return objectStart < 0;
                }
            }
            return false;
        }

        static int FindProperty(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property)) return -1;
            var marker = "\"" + property + "\"";
            var markerIndex = json.IndexOf(marker, StringComparison.Ordinal);
            if (markerIndex < 0) return -1;
            var colon = json.IndexOf(':', markerIndex + marker.Length);
            if (colon < 0) return -1;
            return colon + 1;
        }

        static string Bounded(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            value = value.Replace('\n', ' ').Replace('\r', ' ');
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }

        // Focused editor tests use these typed, non-persisting helpers rather than opening scenes or writing files.
        internal static bool TryResolveNamedControlForTests(VisualElement root, string controlName, out VisualElement control, out string reason)
        {
            return TryResolveNamedControl(root, controlName, out control, out reason);
        }

        internal static string ValidateActionCompatibilityForTests(VisualElement control, string action, string expectedControlType, int dropdownIndex)
        {
            return ValidateActionCompatibility(control, action, expectedControlType, dropdownIndex);
        }

        internal static string PrimitiveValueJsonForTests(VisualElement control)
        {
            return PrimitiveValueJson(control);
        }

        internal static string RequestedDropdownValueJsonForTests(DropdownField dropdown, int dropdownIndex)
        {
            return RequestedValueJson(new SmokeStepRequest(0, dropdown == null ? string.Empty : dropdown.name, "set_dropdown_index", "DropdownField", false, string.Empty, dropdownIndex), dropdown);
        }

        internal static bool RevalidateRequestedDropdownValueForTests(DropdownField dropdown, int dropdownIndex, string expectedValueJson, out string reason)
        {
            return TryRevalidateRequestedValue(new SmokeStepRequest(0, dropdown == null ? string.Empty : dropdown.name, "set_dropdown_index", "DropdownField", false, string.Empty, dropdownIndex), dropdown, expectedValueJson, out reason);
        }

        internal static string VisualHierarchyPathForTests(VisualElement root, VisualElement control)
        {
            return VisualHierarchyPath(root, control);
        }

        internal static bool ExecuteTypedValueForTests(VisualElement control, string action, bool toggleValue, string text, int dropdownIndex, out string exceptionType, out string exceptionMessage)
        {
            exceptionType = string.Empty;
            exceptionMessage = string.Empty;
            var plan = new SmokeStepPlan(new SmokeStepRequest(0, control == null ? string.Empty : control.name, action, string.Empty, toggleValue, text, dropdownIndex))
            {
                Control = control,
                ControlType = ControlTypeName(control),
                RequestedValueJson = action == "set_toggle" ? JsonUtil.Bool(toggleValue) : action == "set_text" ? JsonUtil.String(text) : dropdownIndex.ToString(CultureInfo.InvariantCulture),
            };
            var outcome = ExecuteTypedStep(plan);
            exceptionType = outcome.ExceptionType;
            exceptionMessage = outcome.ExceptionMessage;
            return outcome.Success;
        }

        internal static string ComputePreconditionHashForTests(
            string scenePath,
            int sceneHandle,
            string documentObjectPath,
            int documentInstanceId,
            int documentGameObjectInstanceId,
            int componentIndex,
            string controlName,
            string action,
            string controlHierarchyPath,
            string controlType,
            int controlInstanceId,
            string currentPrimitiveValue,
            string requestedPrimitiveValue,
            int settleFrames = 0,
            bool enabledInHierarchy = true,
            bool focusable = true)
        {
            var builder = new StringBuilder();
            builder.Append("uitk.interaction.smoke/v1\n");
            AppendHashField(builder, "scene_path", scenePath);
            AppendHashField(builder, "scene_handle", sceneHandle.ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "document_object_path", documentObjectPath);
            AppendHashField(builder, "document_runtime_id", "entity:" + documentInstanceId.ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "document_game_object_runtime_id", "entity:" + documentGameObjectInstanceId.ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "component_index", componentIndex.ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "settle_frames", settleFrames.ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "step_index", "0");
            AppendHashField(builder, "control_name", controlName);
            AppendHashField(builder, "action", action);
            AppendHashField(builder, "expected_control_type", string.Empty);
            AppendHashField(builder, "control_hierarchy_path", controlHierarchyPath);
            AppendHashField(builder, "control_type", controlType);
            AppendHashField(builder, "control_runtime_identity", controlInstanceId.ToString(CultureInfo.InvariantCulture));
            AppendHashField(builder, "enabled_in_hierarchy", JsonUtil.Bool(enabledInHierarchy));
            AppendHashField(builder, "focusable", JsonUtil.Bool(focusable));
            AppendHashField(builder, "current_primitive_value", currentPrimitiveValue);
            AppendHashField(builder, "requested_primitive_value", requestedPrimitiveValue);
            using (var sha256 = SHA256.Create())
            {
                var digest = sha256.ComputeHash(Encoding.UTF8.GetBytes(builder.ToString()));
                var result = new StringBuilder("sha256:");
                for (var index = 0; index < digest.Length; index++) result.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                return result.ToString();
            }
        }

        internal static string RestorationStepJsonForTests(int stepIndex, string controlName, string action, string controlHierarchyPath, string restoreValueJson)
        {
            var builder = new StringBuilder();
            builder.Append("{\"step_index\":").Append(stepIndex);
            builder.Append(",\"control_name\":").Append(JsonUtil.String(controlName));
            builder.Append(",\"action\":").Append(JsonUtil.String(action));
            builder.Append(",\"control_hierarchy_path\":").Append(JsonUtil.String(controlHierarchyPath));
            builder.Append(",\"restore_value\":").Append(string.IsNullOrEmpty(restoreValueJson) ? "null" : restoreValueJson);
            AppendRestorationRequestField(builder, action, restoreValueJson);
            builder.Append(",\"reverse_order\":true,\"best_effort\":true,\"automatic_rollback\":false}");
            return builder.ToString();
        }

        internal static string SettlePolicyForTests(bool isPlaying, bool isPaused, int settleFrames)
        {
            if (!isPlaying) return "play_mode_required";
            if (settleFrames < 0 || settleFrames > MaxSettleFrames) return "settle_frames_out_of_range";
            if (settleFrames > 0 && !isPaused) return "paused_playmode_required";
            return "allowed";
        }

        sealed class SmokeRequest
        {
            public readonly string ScenePath;
            public readonly string DocumentObjectPath;
            public readonly int ComponentIndex;
            public readonly List<SmokeStepRequest> Steps;
            public readonly bool DryRun;
            public readonly bool ConfirmExecute;
            public readonly string PreconditionHash;
            public readonly int SettleFrames;

            public SmokeRequest(string scenePath, string documentObjectPath, int componentIndex, List<SmokeStepRequest> steps, bool dryRun, bool confirmExecute, string preconditionHash, int settleFrames)
            {
                ScenePath = scenePath;
                DocumentObjectPath = documentObjectPath;
                ComponentIndex = componentIndex;
                Steps = steps;
                DryRun = dryRun;
                ConfirmExecute = confirmExecute;
                PreconditionHash = preconditionHash;
                SettleFrames = settleFrames;
            }
        }

        sealed class SmokeStepRequest
        {
            public readonly int Index;
            public readonly string ControlName;
            public readonly string Action;
            public readonly string ExpectedControlType;
            public readonly bool ToggleValue;
            public readonly string Text;
            public readonly int DropdownIndex;

            public SmokeStepRequest(int index, string controlName, string action, string expectedControlType, bool toggleValue, string text, int dropdownIndex)
            {
                Index = index;
                ControlName = controlName;
                Action = action;
                ExpectedControlType = expectedControlType;
                ToggleValue = toggleValue;
                Text = text ?? string.Empty;
                DropdownIndex = dropdownIndex;
            }
        }

        sealed class SmokeStepPlan
        {
            public readonly SmokeStepRequest Request;
            public VisualElement Control;
            public string ControlType = string.Empty;
            public string ControlPath = string.Empty;
            public string CurrentValueJson = string.Empty;
            public string RequestedValueJson = string.Empty;
            public string RestorationValueJson = string.Empty;
            public string Reason = string.Empty;
            public long ControlIdentity;
            public bool EnabledInHierarchy;
            public bool Focusable;
            public bool CanExecute;
            public bool Executed;

            public SmokeStepPlan(SmokeStepRequest request)
            {
                Request = request;
            }
        }

        sealed class DocumentIdentity
        {
            public readonly Scene Scene;
            public readonly string ScenePath;
            public readonly string DocumentObjectPath;
            public readonly int ComponentIndex;
            public readonly UIDocument Document;
            public readonly GameObject GameObject;
            public readonly VisualElement Root;

            public DocumentIdentity(Scene scene, string scenePath, string documentObjectPath, int componentIndex, UIDocument document, GameObject gameObject, VisualElement root)
            {
                Scene = scene;
                ScenePath = scenePath;
                DocumentObjectPath = documentObjectPath;
                ComponentIndex = componentIndex;
                Document = document;
                GameObject = gameObject;
                Root = root;
            }
        }

        sealed class ExecutionOutcome
        {
            public bool Success;
            public string ErrorCode = string.Empty;
            public string Message = string.Empty;
            public bool PartialCallbackSideEffects;
            public string ExceptionType = string.Empty;
            public string ExceptionMessage = string.Empty;

            public static ExecutionOutcome Succeeded()
            {
                return new ExecutionOutcome { Success = true };
            }

            public static ExecutionOutcome Failed(string code, string message)
            {
                return new ExecutionOutcome { ErrorCode = code, Message = message };
            }

            public static ExecutionOutcome CallbackFailure(string code, string message, string exceptionType, string exceptionMessage)
            {
                return new ExecutionOutcome
                {
                    ErrorCode = code,
                    Message = message,
                    PartialCallbackSideEffects = true,
                    ExceptionType = exceptionType,
                    ExceptionMessage = exceptionMessage,
                };
            }
        }

        sealed class ControlIdentity
        {
            public readonly long Value;

            public ControlIdentity(long value)
            {
                Value = value;
            }
        }
    }
}
