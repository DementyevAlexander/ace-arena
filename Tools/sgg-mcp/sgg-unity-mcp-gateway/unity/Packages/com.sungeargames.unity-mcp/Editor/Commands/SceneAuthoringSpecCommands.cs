using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class SceneAuthoringSpecCommands
    {
        const string FeatureFlag = "scene_authoring_composite_safe_writes";
        const int MaxSteps = 32;
        const string ApplySpecSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"steps\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"enum\":[\"ugui.object.create\",\"ugui.component.add\",\"ugui.text.set\",\"ugui.image.set\",\"ugui.canvas_group.set\",\"ugui.layout_group.configure\",\"ugui.button.configure\",\"ugui.selectable.navigation.set\",\"mono_behaviour.add\",\"serialized.property.set\",\"serialized.object_reference.set\",\"serialized.array.edit\",\"unityevent.persistent_call.set\"]},\"args\":{\"type\":\"object\"}},\"required\":[\"command\",\"args\"],\"additionalProperties\":false}},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_apply\":{\"type\":\"boolean\",\"default\":false},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"steps\"],\"additionalProperties\":false}";
        const string ApplySpecOutputLimitJson = "{\"max_steps\":32,\"dry_run_default\":true,\"requires_confirm_apply\":true,\"requires_precondition_hash\":true,\"loaded_scenes_only\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":false}";

        [McpCommand("scene.authoring.apply_spec", Title = "Scene authoring apply spec", Description = "Dry-run-first loaded-scene authoring spec composed from separately feature-gated safe-write commands", Risk = "write", Idempotency = "unsafe", TimeoutMs = 30000, SchemaJson = ApplySpecSchemaJson, Tags = new[] { "scene", "authoring", "composite", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "status", OutputLimitJson = ApplySpecOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ApplySpec(McpCommandContext context)
        {
            var sceneResult = RequireLoadedScene(context.ArgsJson, out var scene, out var scenePath);
            if (sceneResult != null) return sceneResult;
            var stepsResult = ExtractSteps(context.ArgsJson, out var steps);
            if (stepsResult != null) return stepsResult;
            var featureResult = RequireCompositeFeatureFlags(context.SettingsJson, steps, out var requiredFeaturesJson);
            if (featureResult != null) return featureResult;
            var planResult = Plan(context, scenePath, steps, requiredFeaturesJson, out var planJson, out var preconditionHash);
            if (planResult != null) return planResult;
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (dryRun) return CommandExecutionResult.Ok(ResultJson(scene, scenePath, true, false, false, preconditionHash, requiredFeaturesJson, planJson, "[]", -1, string.Empty));
            if (!JsonUtil.ExtractBool(context.ArgsJson, "confirm_apply", false)) return CommandExecutionResult.Error("confirmation_required", "confirm_apply must be true when scene.authoring.apply_spec runs with dry_run:false", "{\"precondition_hash\":" + JsonUtil.String(preconditionHash) + "}");
            var precondition = RequirePrecondition(context.ArgsJson, preconditionHash, "scene.authoring.apply_spec");
            if (precondition != null) return precondition;
            var editResult = RequireEditing("scene.authoring.apply_spec");
            if (editResult != null) return editResult;
            var applyResult = ApplySteps(context, scenePath, steps, out var appliedJson, out var failedIndex, out var failureJson);
            if (applyResult != null) return CommandExecutionResult.Ok(ResultJson(scene, scenePath, false, false, true, preconditionHash, requiredFeaturesJson, planJson, appliedJson, failedIndex, failureJson));
            return CommandExecutionResult.Ok(ResultJson(scene, scenePath, false, true, false, preconditionHash, requiredFeaturesJson, planJson, appliedJson, -1, string.Empty));
        }

        static CommandExecutionResult Plan(McpCommandContext context, string scenePath, List<SpecStep> steps, string requiredFeaturesJson, out string planJson, out string preconditionHash)
        {
            var plannedObjects = new HashSet<string>(StringComparer.Ordinal);
            var builder = new StringBuilder();
            for (var index = 0; index < steps.Count; index++)
            {
                if (index > 0) builder.Append(',');
                var step = steps[index];
                var targetObjectPath = JsonUtil.ExtractString(step.ArgsJson, "object_path");
                if (!string.IsNullOrEmpty(targetObjectPath) && plannedObjects.Contains(NormalizeObjectPath(targetObjectPath)))
                {
                    builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"feature_flag\":").Append(JsonUtil.String(FeatureForCommand(step.Command))).Append(",\"status\":\"planned_after_create\",\"object_path\":").Append(JsonUtil.String(NormalizeObjectPath(targetObjectPath))).Append('}');
                    continue;
                }
                var nested = InvokeStep(context, step.Command, BuildNestedArgs(scenePath, step.ArgsJson, true, string.Empty));
                if (nested.Status != "ok")
                {
                    planJson = "[]"; preconditionHash = string.Empty;
                    return CommandExecutionResult.Error(nested.ErrorCode, "scene.authoring.apply_spec dry-run failed at step " + index + ": " + nested.ErrorMessage, "{\"step_index\":" + index + ",\"command\":" + JsonUtil.String(step.Command) + "}");
                }
                builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"feature_flag\":").Append(JsonUtil.String(FeatureForCommand(step.Command))).Append(",\"result\":").Append(nested.DataJson).Append('}');
                if (step.Command == "ugui.object.create")
                {
                    var planned = JsonUtil.ExtractString(nested.DataJson, "planned_object_path");
                    if (!string.IsNullOrEmpty(planned)) plannedObjects.Add(NormalizeObjectPath(planned));
                }
            }
            planJson = "[" + builder + "]";
            preconditionHash = Sha256Prefixed("scene.authoring.apply_spec\n" + scenePath + "\n" + StepsStableJson(steps) + "\n" + requiredFeaturesJson + "\n" + planJson);
            return null;
        }

        static CommandExecutionResult ApplySteps(McpCommandContext context, string scenePath, List<SpecStep> steps, out string appliedJson, out int failedIndex, out string failureJson)
        {
            var builder = new StringBuilder();
            failedIndex = -1; failureJson = string.Empty;
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var perStepHash = string.Empty;
                if (StepRequiresPrecondition(step.Command))
                {
                    var dryRun = InvokeStep(context, step.Command, BuildNestedArgs(scenePath, step.ArgsJson, true, string.Empty));
                    if (dryRun.Status != "ok") { failedIndex = index; failureJson = ErrorJson(dryRun); appliedJson = "[" + builder + "]"; return dryRun; }
                    perStepHash = JsonUtil.ExtractString(dryRun.DataJson, "precondition_hash");
                }
                var result = InvokeStep(context, step.Command, BuildNestedArgs(scenePath, step.ArgsJson, false, perStepHash));
                if (result.Status != "ok") { failedIndex = index; failureJson = ErrorJson(result); appliedJson = "[" + builder + "]"; return result; }
                if (builder.Length > 0) builder.Append(',');
                builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"feature_flag\":").Append(JsonUtil.String(FeatureForCommand(step.Command))).Append(",\"result\":").Append(result.DataJson).Append('}');
            }
            appliedJson = "[" + builder + "]";
            return null;
        }

        static CommandExecutionResult InvokeStep(McpCommandContext context, string command, string argsJson)
        {
            var nested = new McpCommandContext(context.State, context.RequestId, context.Project, argsJson, context.SettingsJson);
            if (command == "ugui.object.create") return UguiInspectionCommands.UguiObjectCreate(nested);
            if (command == "ugui.component.add") return UguiInspectionCommands.UguiComponentAdd(nested);
            if (command == "ugui.text.set") return UguiSceneAuthoringCommands.UguiTextSet(nested);
            if (command == "ugui.image.set") return UguiSceneAuthoringCommands.UguiImageSet(nested);
            if (command == "ugui.canvas_group.set") return UguiSceneAuthoringCommands.UguiCanvasGroupSet(nested);
            if (command == "ugui.layout_group.configure") return UguiSceneAuthoringCommands.UguiLayoutGroupConfigure(nested);
            if (command == "ugui.button.configure") return UguiSceneAuthoringCommands.UguiButtonConfigure(nested);
            if (command == "ugui.selectable.navigation.set") return UguiSceneAuthoringCommands.UguiSelectableNavigationSet(nested);
            if (command == "mono_behaviour.add") return SceneWiringCommands.MonoBehaviourAdd(nested);
            if (command == "serialized.property.set") return SceneWiringCommands.SerializedPropertySet(nested);
            if (command == "serialized.object_reference.set") return SceneWiringCommands.SerializedObjectReferenceSet(nested);
            if (command == "serialized.array.edit") return SceneWiringCommands.SerializedArrayEdit(nested);
            if (command == "unityevent.persistent_call.set") return SceneWiringCommands.UnityEventPersistentCallSet(nested);
            return CommandExecutionResult.Error("invalid_field_value", "step command is not supported by scene.authoring.apply_spec");
        }

        static CommandExecutionResult RequireCompositeFeatureFlags(string settingsJson, List<SpecStep> steps, out string requiredFeaturesJson)
        {
            var featuresJson = JsonUtil.ExtractObject(settingsJson, "feature_flags");
            var required = new List<string>();
            for (var index = 0; index < steps.Count; index++)
            {
                var feature = FeatureForCommand(steps[index].Command);
                if (!required.Contains(feature)) required.Add(feature);
                if (!FeatureEnabled(featuresJson, feature)) { requiredFeaturesJson = StringArrayJson(required); return CommandExecutionResult.Error("feature_disabled", "scene.authoring.apply_spec requires nested feature flag " + feature + " for step " + index, "{\"step_index\":" + index + ",\"command\":" + JsonUtil.String(steps[index].Command) + ",\"required_feature\":" + JsonUtil.String(feature) + "}"); }
            }
            requiredFeaturesJson = StringArrayJson(required);
            return null;
        }

        static bool FeatureEnabled(string featuresJson, string feature)
        {
            return !string.IsNullOrEmpty(featuresJson) && JsonUtil.ExtractBool(featuresJson, feature, false);
        }

        static string FeatureForCommand(string command)
        {
            if (command.StartsWith("ugui.", StringComparison.Ordinal)) return "ugui_safe_writes";
            if (command.StartsWith("mono_behaviour.", StringComparison.Ordinal)) return "mono_behaviour_safe_writes";
            if (command.StartsWith("serialized.", StringComparison.Ordinal)) return "serialized_safe_writes";
            if (command.StartsWith("unityevent.", StringComparison.Ordinal)) return "unityevent_safe_writes";
            return string.Empty;
        }

        static bool StepRequiresPrecondition(string command)
        {
            return command != "ugui.object.create" && command != "ugui.component.add" && command != "mono_behaviour.add";
        }

        static CommandExecutionResult ExtractSteps(string json, out List<SpecStep> steps)
        {
            steps = new List<SpecStep>();
            var arrayJson = ExtractArray(json, "steps");
            if (string.IsNullOrEmpty(arrayJson)) return CommandExecutionResult.Error("schema_validation_failed", "steps must be an array");
            var objects = new List<string>();
            if (!TryExtractObjectArrayItems(arrayJson, objects)) return CommandExecutionResult.Error("schema_validation_failed", "steps must contain objects");
            if (objects.Count == 0 || objects.Count > MaxSteps) return CommandExecutionResult.Error("invalid_field_value", "steps must contain 1 to 32 items");
            for (var index = 0; index < objects.Count; index++)
            {
                var command = JsonUtil.ExtractString(objects[index], "command");
                if (string.IsNullOrEmpty(FeatureForCommand(command))) return CommandExecutionResult.Error("invalid_field_value", "step command is not supported by scene.authoring.apply_spec");
                var args = JsonUtil.ExtractObject(objects[index], "args");
                if (string.IsNullOrEmpty(args) || args == "{}") return CommandExecutionResult.Error("schema_validation_failed", "each step requires args object");
                steps.Add(new SpecStep(command, args));
            }
            return null;
        }

        static CommandExecutionResult RequireLoadedScene(string argsJson, out Scene scene, out string scenePath)
        {
            scene = default; scenePath = string.Empty;
            var raw = JsonUtil.ExtractString(argsJson, "scene_path");
            if (string.IsNullOrEmpty(raw)) return CommandExecutionResult.Error("schema_validation_failed", "Argument scene_path is required");
            try { scenePath = PathGuard.RequireUnityAssetPath(raw); } catch (Exception ex) { return CommandExecutionResult.Error("invalid_field_value", "scene_path must be Unity-relative", JsonUtil.String(ex.Message)); }
            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return CommandExecutionResult.Error("invalid_field_value", "scene_path must end with .unity");
            for (var index = 0; index < SceneManager.sceneCount; index++) { var candidate = SceneManager.GetSceneAt(index); if (string.Equals(candidate.path, scenePath, StringComparison.OrdinalIgnoreCase)) { scene = candidate; break; } }
            return !scene.IsValid() || !scene.isLoaded ? CommandExecutionResult.Error("asset_not_found", "scene_path must reference a loaded scene") : null;
        }

        static CommandExecutionResult RequireEditing(string command)
        {
            if (EditorApplication.isCompiling) return CommandExecutionResult.Error("unity_busy", command + " requires editing status; Unity is compiling");
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return CommandExecutionResult.Error("unity_busy", command + " requires Edit Mode; Unity is in or entering Play Mode");
            return null;
        }

        static CommandExecutionResult RequirePrecondition(string argsJson, string hash, string command)
        {
            var actual = JsonUtil.ExtractString(argsJson, "precondition_hash").Trim();
            if (string.IsNullOrEmpty(actual)) return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when " + command + " runs with dry_run:false", "{\"precondition_hash\":" + JsonUtil.String(hash) + "}");
            return string.Equals(actual, hash, StringComparison.Ordinal) ? null : CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match current scene state or requested operation", "{\"precondition_hash\":" + JsonUtil.String(hash) + "}");
        }

        static string ResultJson(Scene scene, string scenePath, bool dryRun, bool applied, bool partialFailure, string preconditionHash, string requiredFeaturesJson, string planJson, string appliedJson, int failedIndex, string failureJson)
        {
            return "{\"status\":\"ok\",\"command\":\"scene.authoring.apply_spec\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"applied\":" + JsonUtil.Bool(applied) + ",\"partial_failure\":" + JsonUtil.Bool(partialFailure) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"required_feature_flags\":" + requiredFeaturesJson + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash) + ",\"plan_steps\":" + planJson + ",\"applied_steps\":" + appliedJson + ",\"failed_step_index\":" + failedIndex + ",\"failure\":" + (string.IsNullOrEmpty(failureJson) ? "null" : failureJson) + ",\"scene_dirty\":" + JsonUtil.Bool(scene.IsValid() && scene.isDirty) + ",\"scene_saved\":false,\"saved_prefab\":false,\"opens_scenes\":false,\"writes_prefab_assets\":false,\"loaded_scenes_only\":true,\"warnings\":[\"" + (dryRun ? "dry_run_no_scene_mutation" : applied ? "scene_dirty_not_saved" : partialFailure ? "partial_apply_scene_may_be_dirty" : "no_scene_change_applied") + "\"],\"next_valid_actions\":[\"scene.inspect_object\",\"scene.inspect_component\",\"scene.save\"]}";
        }

        static string BuildNestedArgs(string scenePath, string argsJson, bool dryRun, string preconditionHash)
        {
            var body = TrimObjectBody(argsJson);
            var prefix = "\"scene_path\":" + JsonUtil.String(scenePath) + ",\"dry_run\":" + JsonUtil.Bool(dryRun);
            if (!string.IsNullOrEmpty(preconditionHash)) prefix += ",\"precondition_hash\":" + JsonUtil.String(preconditionHash);
            return "{" + prefix + (string.IsNullOrEmpty(body) ? string.Empty : "," + body) + "}";
        }

        static string StepsStableJson(List<SpecStep> steps)
        {
            var builder = new StringBuilder("[");
            for (var index = 0; index < steps.Count; index++) { if (index > 0) builder.Append(','); builder.Append("{\"command\":").Append(JsonUtil.String(steps[index].Command)).Append(",\"args\":").Append(steps[index].ArgsJson).Append('}'); }
            return builder.Append(']').ToString();
        }

        static string ErrorJson(CommandExecutionResult result)
        {
            return "{\"status\":\"error\",\"code\":" + JsonUtil.String(result.ErrorCode) + ",\"message\":" + JsonUtil.String(result.ErrorMessage) + ",\"details\":" + (string.IsNullOrEmpty(result.ErrorDetailsJson) ? "null" : result.ErrorDetailsJson) + "}";
        }

        static string StringArrayJson(List<string> values)
        {
            var builder = new StringBuilder("[");
            for (var index = 0; index < values.Count; index++) { if (index > 0) builder.Append(','); builder.Append(JsonUtil.String(values[index])); }
            return builder.Append(']').ToString();
        }

        static string NormalizeObjectPath(string path) { return string.IsNullOrEmpty(path) ? "/" : path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path; }
        static string TrimObjectBody(string json) { var value = string.IsNullOrEmpty(json) ? "{}" : json.Trim(); return value.Length >= 2 && value[0] == '{' && value[value.Length - 1] == '}' ? value.Substring(1, value.Length - 2).Trim() : string.Empty; }
        static string Sha256Prefixed(string value) { using (var sha = SHA256.Create()) return "sha256:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant(); }

        static string ExtractArray(string json, string property)
        {
            var colon = FindPropertyColon(json, property); if (colon < 0) return string.Empty;
            var start = IndexOfNextNonWhitespace(json, colon + 1); if (start < 0 || json[start] != '[') return string.Empty;
            var depth = 0; var inString = false; var escaped = false;
            for (var index = start; index < json.Length; index++) { var ch = json[index]; if (escaped) { escaped = false; continue; } if (ch == '\\') { escaped = true; continue; } if (ch == '"') { inString = !inString; continue; } if (inString) continue; if (ch == '[') depth++; else if (ch == ']') { depth--; if (depth == 0) return json.Substring(start, index - start + 1); } }
            return string.Empty;
        }

        static bool TryExtractObjectArrayItems(string arrayJson, List<string> items)
        {
            var start = IndexOfNextNonWhitespace(arrayJson, 0); if (start < 0 || arrayJson[start] != '[') return false;
            var depth = 0; var objectStart = -1; var inString = false; var escaped = false;
            for (var index = start + 1; index < arrayJson.Length; index++) { var ch = arrayJson[index]; if (escaped) { escaped = false; continue; } if (ch == '\\') { escaped = true; continue; } if (ch == '"') { inString = !inString; continue; } if (inString) continue; if (ch == '{') { if (depth == 0) objectStart = index; depth++; } else if (ch == '}') { depth--; if (depth < 0) return false; if (depth == 0 && objectStart >= 0) { items.Add(arrayJson.Substring(objectStart, index - objectStart + 1)); objectStart = -1; } } else if (ch == ']' && depth == 0) return true; }
            return false;
        }

        static int FindPropertyColon(string json, string property) { if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property)) return -1; var pattern = "\"" + property + "\""; var at = json.IndexOf(pattern, StringComparison.Ordinal); return at < 0 ? -1 : json.IndexOf(':', at + pattern.Length); }
        static int IndexOfNextNonWhitespace(string value, int start) { for (var index = start; index < value.Length; index++) if (!char.IsWhiteSpace(value[index])) return index; return -1; }

        sealed class SpecStep
        {
            public SpecStep(string command, string argsJson) { Command = command; ArgsJson = argsJson; }
            public string Command { get; private set; }
            public string ArgsJson { get; private set; }
        }
    }
}
