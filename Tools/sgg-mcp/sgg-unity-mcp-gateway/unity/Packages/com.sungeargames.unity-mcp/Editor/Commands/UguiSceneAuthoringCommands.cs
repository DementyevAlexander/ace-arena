using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class UguiSceneAuthoringCommands
    {
        const string FeatureFlag = "ugui_safe_writes";
        const int MaxApplySpecSteps = 32;
        const string UguiTextSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"text\":{\"type\":\"string\",\"maxLength\":4096},\"font_size\":{\"type\":\"number\",\"minimum\":0,\"maximum\":4096},\"color\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"raycast_target\":{\"type\":\"boolean\"},\"horizontal_alignment\":{\"type\":\"string\",\"enum\":[\"left\",\"center\",\"right\",\"justified\"]},\"vertical_alignment\":{\"type\":\"string\",\"enum\":[\"top\",\"middle\",\"bottom\",\"baseline\"]},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string UguiImageSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"color\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":4,\"maxItems\":4},\"raycast_target\":{\"type\":\"boolean\"},\"preserve_aspect\":{\"type\":\"boolean\"},\"sprite_asset_path\":{\"type\":\"string\"},\"clear_sprite\":{\"type\":\"boolean\"},\"texture_asset_path\":{\"type\":\"string\"},\"clear_texture\":{\"type\":\"boolean\"},\"material_asset_path\":{\"type\":\"string\"},\"clear_material\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string UguiCanvasGroupSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"alpha\":{\"type\":\"number\",\"minimum\":0,\"maximum\":1},\"interactable\":{\"type\":\"boolean\"},\"blocks_raycasts\":{\"type\":\"boolean\"},\"ignore_parent_groups\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string UguiLayoutGroupConfigureSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"component_type\":{\"type\":\"string\"},\"full_type_name\":{\"type\":\"string\"},\"assembly_name\":{\"type\":\"string\"},\"spacing\":{\"type\":\"number\"},\"spacing_xy\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"cell_size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"child_alignment\":{\"type\":\"string\"},\"child_control_width\":{\"type\":\"boolean\"},\"child_control_height\":{\"type\":\"boolean\"},\"child_force_expand_width\":{\"type\":\"boolean\"},\"child_force_expand_height\":{\"type\":\"boolean\"},\"child_scale_width\":{\"type\":\"boolean\"},\"child_scale_height\":{\"type\":\"boolean\"},\"reverse_arrangement\":{\"type\":\"boolean\"},\"start_corner\":{\"type\":\"string\"},\"start_axis\":{\"type\":\"string\"},\"constraint\":{\"type\":\"string\"},\"constraint_count\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000},\"horizontal_fit\":{\"type\":\"string\"},\"vertical_fit\":{\"type\":\"string\"},\"ignore_layout\":{\"type\":\"boolean\"},\"min_width\":{\"type\":\"number\"},\"min_height\":{\"type\":\"number\"},\"preferred_width\":{\"type\":\"number\"},\"preferred_height\":{\"type\":\"number\"},\"flexible_width\":{\"type\":\"number\"},\"flexible_height\":{\"type\":\"number\"},\"layout_priority\":{\"type\":\"integer\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string UguiButtonConfigureSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"interactable\":{\"type\":\"boolean\"},\"transition\":{\"type\":\"string\"},\"target_graphic_object_path\":{\"type\":\"string\"},\"target_graphic_component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"target_graphic_component_type\":{\"type\":\"string\"},\"target_graphic_full_type_name\":{\"type\":\"string\"},\"target_graphic_assembly_name\":{\"type\":\"string\"},\"clear_target_graphic\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string UguiSelectableNavigationSetSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"object_path\":{\"type\":\"string\"},\"component_index\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":256},\"mode\":{\"type\":\"string\"},\"select_on_up_path\":{\"type\":\"string\"},\"select_on_down_path\":{\"type\":\"string\"},\"select_on_left_path\":{\"type\":\"string\"},\"select_on_right_path\":{\"type\":\"string\"},\"clear_explicit\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"object_path\"],\"additionalProperties\":false}";
        const string UguiScreenApplySpecSchemaJson = "{\"type\":\"object\",\"properties\":{\"scene_path\":{\"type\":\"string\"},\"steps\":{\"type\":\"array\",\"minItems\":1,\"maxItems\":32,\"items\":{\"type\":\"object\",\"properties\":{\"command\":{\"type\":\"string\",\"enum\":[\"ugui.object.create\",\"ugui.component.add\",\"ugui.text.set\",\"ugui.image.set\",\"ugui.canvas_group.set\",\"ugui.layout_group.configure\",\"ugui.button.configure\",\"ugui.selectable.navigation.set\"]},\"args\":{\"type\":\"object\"}},\"required\":[\"command\",\"args\"],\"additionalProperties\":false}},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"confirm_apply\":{\"type\":\"boolean\",\"default\":false},\"precondition_hash\":{\"type\":\"string\",\"maxLength\":128}},\"required\":[\"scene_path\",\"steps\"],\"additionalProperties\":false}";
        const string UguiTextSetOutputLimitJson = "{\"max_components\":1,\"dry_run_default\":true,\"requires_precondition_hash\":true,\"loaded_scenes_only\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":false}";
        const string UguiComponentConfigureOutputLimitJson = "{\"max_components\":1,\"dry_run_default\":true,\"requires_precondition_hash\":true,\"loaded_scenes_only\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":false}";
        const string UguiScreenApplySpecOutputLimitJson = "{\"max_steps\":32,\"dry_run_default\":true,\"requires_confirm_apply\":true,\"requires_precondition_hash\":true,\"loaded_scenes_only\":true,\"opens_scenes\":false,\"saves_scenes\":false,\"saves_prefabs\":false}";

        [McpCommand("ugui.text.set", Title = "uGUI text set", Description = "Dry-run-first bounded text, color, font-size and alignment writes on one loaded-scene uGUI/TMP text component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = UguiTextSetSchemaJson, Tags = new[] { "ugui", "text", "scene", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = UguiTextSetOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UguiTextSet(McpCommandContext context)
        {
            var targetResult = RequireTextTarget(context.ArgsJson, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex);
            if (targetResult != null) return targetResult;
            var requestResult = ExtractTextRequest(context.ArgsJson, component, out var request);
            if (requestResult != null) return requestResult;
            var current = CaptureTextState(component);
            var requested = RequestedTextState(component, current, request);
            var changedFields = TextChangedFields(current, requested, request);
            var changed = changedFields.Count > 0;
            var hash = UguiTextPreconditionHash(scenePath, objectPath, component, componentIndex, current, requested);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (!dryRun && changed)
            {
                var editResult = RequireEditing("ugui.text.set");
                if (editResult != null) return editResult;
                var precondition = RequirePrecondition(context.ArgsJson, hash, "ugui.text.set");
                if (precondition != null) return precondition;
                var applyResult = ApplyTextState(component, current, requested, changedFields);
                if (applyResult != null) return applyResult;
                EditorUtility.SetDirty(component);
                EditorSceneManager.MarkSceneDirty(scene);
                Canvas.ForceUpdateCanvases();
            }
            return CommandExecutionResult.Ok(UguiTextSetJson(scene, scenePath, objectPath, target, component, componentIndex, current, requested, changedFields, hash, dryRun, changed));
        }

        [McpCommand("ugui.image.set", Title = "uGUI image set", Description = "Dry-run-first bounded Image/RawImage sprite, texture, material, color and raycast writes on one loaded-scene component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = UguiImageSetSchemaJson, Tags = new[] { "ugui", "image", "scene", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = UguiComponentConfigureOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UguiImageSet(McpCommandContext context)
        {
            return ConfigureComponent(context, "ugui.image.set", new[] { "UnityEngine.UI.Image", "UnityEngine.UI.RawImage" }, BuildImageMutations);
        }

        [McpCommand("ugui.canvas_group.set", Title = "uGUI CanvasGroup set", Description = "Dry-run-first bounded CanvasGroup alpha/interactable/raycast writes on one loaded-scene component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = UguiCanvasGroupSetSchemaJson, Tags = new[] { "ugui", "canvas-group", "scene", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = UguiComponentConfigureOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UguiCanvasGroupSet(McpCommandContext context)
        {
            return ConfigureComponent(context, "ugui.canvas_group.set", new[] { "UnityEngine.CanvasGroup" }, BuildCanvasGroupMutations);
        }

        [McpCommand("ugui.layout_group.configure", Title = "uGUI layout configure", Description = "Dry-run-first bounded layout group, ContentSizeFitter and LayoutElement configuration on one loaded-scene component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = UguiLayoutGroupConfigureSchemaJson, Tags = new[] { "ugui", "layout", "scene", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = UguiComponentConfigureOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UguiLayoutGroupConfigure(McpCommandContext context)
        {
            return ConfigureComponent(context, "ugui.layout_group.configure", new[] { "UnityEngine.UI.HorizontalLayoutGroup", "UnityEngine.UI.VerticalLayoutGroup", "UnityEngine.UI.GridLayoutGroup", "UnityEngine.UI.ContentSizeFitter", "UnityEngine.UI.LayoutElement" }, BuildLayoutMutations);
        }

        [McpCommand("ugui.button.configure", Title = "uGUI Button configure", Description = "Dry-run-first bounded non-event Button configuration on one loaded-scene Button component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = UguiButtonConfigureSchemaJson, Tags = new[] { "ugui", "button", "selectable", "scene", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = UguiComponentConfigureOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UguiButtonConfigure(McpCommandContext context)
        {
            return ConfigureComponent(context, "ugui.button.configure", new[] { "UnityEngine.UI.Button" }, BuildButtonMutations);
        }

        [McpCommand("ugui.selectable.navigation.set", Title = "uGUI Selectable navigation set", Description = "Dry-run-first bounded Selectable Navigation struct configuration on one loaded-scene Selectable component", Risk = "write", Idempotency = "idempotent", TimeoutMs = 10000, SchemaJson = UguiSelectableNavigationSetSchemaJson, Tags = new[] { "ugui", "selectable", "navigation", "scene", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "object_details", OutputLimitJson = UguiComponentConfigureOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UguiSelectableNavigationSet(McpCommandContext context)
        {
            return ConfigureComponent(context, "ugui.selectable.navigation.set", new[] { "UnityEngine.UI.Selectable" }, BuildNavigationMutations);
        }

        [McpCommand("ugui.screen.apply_spec", Title = "uGUI screen apply spec", Description = "Dry-run-first bounded loaded-scene uGUI screen spec application composed from focused uGUI safe-write commands", Risk = "write", Idempotency = "unsafe", TimeoutMs = 30000, SchemaJson = UguiScreenApplySpecSchemaJson, Tags = new[] { "ugui", "screen", "scene", "safe-write" }, Capability = FeatureFlag, RequiresEditorStatus = new[] { "editing" }, ResultShape = "status", OutputLimitJson = UguiScreenApplySpecOutputLimitJson, FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult UguiScreenApplySpec(McpCommandContext context)
        {
            var sceneResult = RequireLoadedScene(context.ArgsJson, out var scene, out var scenePath);
            if (sceneResult != null) return sceneResult;
            var stepsResult = ExtractApplySpecSteps(context.ArgsJson, out var steps);
            if (stepsResult != null) return stepsResult;
            var planResult = PlanApplySpec(context, scenePath, steps, out var planJson, out var preconditionHash);
            if (planResult != null) return planResult;
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (dryRun)
            {
                return CommandExecutionResult.Ok(UguiScreenApplySpecJson(scene, scenePath, true, false, false, preconditionHash, planJson, "[]", -1, string.Empty));
            }
            if (!JsonUtil.ExtractBool(context.ArgsJson, "confirm_apply", false))
            {
                return CommandExecutionResult.Error("confirmation_required", "confirm_apply must be true when ugui.screen.apply_spec runs with dry_run:false", "{\"precondition_hash\":" + JsonUtil.String(preconditionHash) + "}");
            }
            var precondition = RequirePrecondition(context.ArgsJson, preconditionHash, "ugui.screen.apply_spec");
            if (precondition != null) return precondition;
            var editResult = RequireEditing("ugui.screen.apply_spec");
            if (editResult != null) return editResult;
            var applyResult = ApplySpecSteps(context, scenePath, steps, out var appliedJson, out var failedIndex, out var failureJson);
            if (applyResult != null)
            {
                return CommandExecutionResult.Ok(UguiScreenApplySpecJson(scene, scenePath, false, false, true, preconditionHash, planJson, appliedJson, failedIndex, failureJson));
            }
            Canvas.ForceUpdateCanvases();
            return CommandExecutionResult.Ok(UguiScreenApplySpecJson(scene, scenePath, false, true, false, preconditionHash, planJson, appliedJson, -1, string.Empty));
        }

        static CommandExecutionResult RequireTextTarget(string argsJson, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var sceneResult = RequireLoadedScene(argsJson, out scene, out scenePath);
            if (sceneResult != null) { target = null; objectPath = string.Empty; return sceneResult; }
            var objectResult = RequireSceneObject(argsJson, scene, "object_path", out target, out objectPath);
            if (objectResult != null) return objectResult;
            if (target.GetComponent<RectTransform>() == null && target.GetComponent<Canvas>() == null) return CommandExecutionResult.Error("invalid_field_value", "object_path must reference a uGUI object with RectTransform or Canvas");
            return RequireTextComponent(argsJson, target, out component, out componentIndex);
        }

        static CommandExecutionResult RequireTextComponent(string argsJson, GameObject target, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var hasSelector = TryReadInt(argsJson, "component_index", out var requestedIndex)
                || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "component_type"))
                || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "full_type_name"));
            var components = target.GetComponents<Component>();
            if (hasSelector)
            {
                var selected = RequireComponent(argsJson, target, string.Empty, out component, out componentIndex);
                if (selected != null) return selected;
                return IsTextComponent(component) ? null : CommandExecutionResult.Error("invalid_field_value", "component selector must reference UnityEngine.UI.Text, TMPro.TMP_Text, or TMPro.TextMeshProUGUI");
            }
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !IsTextComponent(candidate)) continue;
                if (component != null) return CommandExecutionResult.Error("invalid_field_value", "text component selector is ambiguous on this object; pass component_index");
                component = candidate;
                componentIndex = index;
            }
            return component == null ? CommandExecutionResult.Error("asset_not_found", "Text component was not found") : null;
        }

        static CommandExecutionResult ExtractTextRequest(string json, Component component, out TextSetRequest request)
        {
            request = new TextSetRequest();
            if (HasJsonProperty(json, "text"))
            {
                request.HasText = true;
                request.Text = JsonUtil.ExtractString(json, "text");
                if (request.Text.Length > 4096 || request.Text.IndexOf('\0') >= 0) return CommandExecutionResult.Error("invalid_field_value", "text must be bounded text without NUL bytes");
            }
            if (HasJsonProperty(json, "font_size"))
            {
                request.HasFontSize = true;
                if (!TryExtractFloatField(json, "font_size", out request.FontSize) || request.FontSize < 0f || request.FontSize > 4096f) return CommandExecutionResult.Error("invalid_field_value", "font_size must be a number between 0 and 4096");
                if (!HasWritableProperty(component, "fontSize")) return CommandExecutionResult.Error("invalid_field_value", "selected text component does not expose writable fontSize");
            }
            if (HasJsonProperty(json, "color"))
            {
                request.HasColor = true;
                if (!TryExtractVector4(json, "color", out request.Color)) return CommandExecutionResult.Error("invalid_field_value", "color must be an array of four numbers");
                if (!HasWritableProperty(component, "color")) return CommandExecutionResult.Error("invalid_field_value", "selected text component does not expose writable color");
            }
            if (HasJsonProperty(json, "raycast_target"))
            {
                request.HasRaycastTarget = true;
                request.RaycastTarget = JsonUtil.ExtractBool(json, "raycast_target", false);
                if (!HasWritableProperty(component, "raycastTarget")) return CommandExecutionResult.Error("invalid_field_value", "selected text component does not expose writable raycastTarget");
            }
            if (HasJsonProperty(json, "horizontal_alignment"))
            {
                request.HasHorizontalAlignment = true;
                request.HorizontalAlignment = NormalizeHorizontalAlignment(JsonUtil.ExtractString(json, "horizontal_alignment"));
                if (string.IsNullOrEmpty(request.HorizontalAlignment)) return CommandExecutionResult.Error("invalid_field_value", "horizontal_alignment must be left, center, right, or justified");
            }
            if (HasJsonProperty(json, "vertical_alignment"))
            {
                request.HasVerticalAlignment = true;
                request.VerticalAlignment = NormalizeVerticalAlignment(JsonUtil.ExtractString(json, "vertical_alignment"));
                if (string.IsNullOrEmpty(request.VerticalAlignment)) return CommandExecutionResult.Error("invalid_field_value", "vertical_alignment must be top, middle, bottom, or baseline");
            }
            if (!request.HasAnyField) return CommandExecutionResult.Error("schema_validation_failed", "At least one allowlisted text field is required");
            if ((request.HasHorizontalAlignment || request.HasVerticalAlignment) && !HasWritableProperty(component, "alignment")) return CommandExecutionResult.Error("invalid_field_value", "selected text component does not expose writable alignment");
            if (request.HasText && !HasWritableProperty(component, "text")) return CommandExecutionResult.Error("invalid_field_value", "selected text component does not expose writable text");
            return null;
        }

        static TextState CaptureTextState(Component component)
        {
            var state = new TextState();
            state.Text = GetPropertyValue(component, "text") as string ?? string.Empty;
            state.FontSize = ConvertToFloat(GetPropertyValue(component, "fontSize"));
            var color = GetPropertyValue(component, "color");
            state.Color = color is Color ? (Color)color : Color.white;
            state.RaycastTarget = ConvertToBool(GetPropertyValue(component, "raycastTarget"));
            var alignment = GetPropertyValue(component, "alignment");
            state.Alignment = alignment == null ? string.Empty : alignment.ToString();
            InferAlignment(state.Alignment, out state.HorizontalAlignment, out state.VerticalAlignment);
            return state;
        }

        static TextState RequestedTextState(Component component, TextState current, TextSetRequest request)
        {
            var requested = current.Clone();
            if (request.HasText) requested.Text = request.Text;
            if (request.HasFontSize) requested.FontSize = request.FontSize;
            if (request.HasColor) requested.Color = new Color(request.Color.x, request.Color.y, request.Color.z, request.Color.w);
            if (request.HasRaycastTarget) requested.RaycastTarget = request.RaycastTarget;
            if (request.HasHorizontalAlignment || request.HasVerticalAlignment)
            {
                requested.HorizontalAlignment = request.HasHorizontalAlignment ? request.HorizontalAlignment : current.HorizontalAlignment;
                requested.VerticalAlignment = request.HasVerticalAlignment ? request.VerticalAlignment : current.VerticalAlignment;
                requested.Alignment = ResolveAlignmentName(component, requested.HorizontalAlignment, requested.VerticalAlignment);
            }
            return requested;
        }

        static List<string> TextChangedFields(TextState current, TextState requested, TextSetRequest request)
        {
            var fields = new List<string>();
            if (request.HasText && current.Text != requested.Text) fields.Add("text");
            if (request.HasFontSize && Math.Abs(current.FontSize - requested.FontSize) > 0.0001f) fields.Add("font_size");
            if (request.HasColor && ColorDistance(current.Color, requested.Color) > 0.0001f) fields.Add("color");
            if (request.HasRaycastTarget && current.RaycastTarget != requested.RaycastTarget) fields.Add("raycast_target");
            if ((request.HasHorizontalAlignment || request.HasVerticalAlignment) && current.Alignment != requested.Alignment) fields.Add("alignment");
            return fields;
        }

        static CommandExecutionResult ApplyTextState(Component component, TextState current, TextState requested, List<string> changedFields)
        {
            Undo.RecordObject(component, "Set uGUI Text");
            for (var index = 0; index < changedFields.Count; index++)
            {
                var field = changedFields[index];
                if (field == "text") SetPropertyValue(component, "text", requested.Text);
                else if (field == "font_size") SetNumericPropertyValue(component, "fontSize", requested.FontSize);
                else if (field == "color") SetPropertyValue(component, "color", requested.Color);
                else if (field == "raycast_target") SetPropertyValue(component, "raycastTarget", requested.RaycastTarget);
                else if (field == "alignment")
                {
                    var property = component.GetType().GetProperty("alignment", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (property == null || !property.PropertyType.IsEnum) return CommandExecutionResult.Error("invalid_field_value", "alignment property is not a writable enum");
                    SetPropertyValue(component, "alignment", Enum.Parse(property.PropertyType, requested.Alignment));
                }
            }
            return null;
        }

        static string ResolveAlignmentName(Component component, string horizontal, string vertical)
        {
            var property = component.GetType().GetProperty("alignment", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property == null || !property.PropertyType.IsEnum) return string.Empty;
            var names = new HashSet<string>(Enum.GetNames(property.PropertyType));
            var candidates = AlignmentCandidateNames(property.PropertyType.FullName ?? property.PropertyType.Name, horizontal, vertical);
            for (var index = 0; index < candidates.Length; index++) if (names.Contains(candidates[index])) return candidates[index];
            return string.Empty;
        }

        static string[] AlignmentCandidateNames(string enumTypeName, string horizontal, string vertical)
        {
            var hTitle = Title(horizontal == "justified" ? "justified" : horizontal);
            if (enumTypeName == "UnityEngine.TextAnchor")
            {
                var v = vertical == "top" ? "Upper" : vertical == "bottom" ? "Lower" : "Middle";
                var h = horizontal == "right" ? "Right" : horizontal == "left" ? "Left" : "Center";
                return new[] { v + h };
            }
            if (vertical == "top") return new[] { "Top" + (horizontal == "center" ? string.Empty : hTitle), horizontal == "center" ? "Top" : string.Empty };
            if (vertical == "bottom") return new[] { "Bottom" + (horizontal == "center" ? string.Empty : hTitle), horizontal == "center" ? "Bottom" : string.Empty };
            if (vertical == "baseline") return new[] { "Baseline" + (horizontal == "center" ? string.Empty : hTitle), horizontal == "center" ? "Baseline" : string.Empty };
            return new[] { horizontal == "left" ? "Left" : horizontal == "right" ? "Right" : horizontal == "justified" ? "Justified" : "Center", "Midline" + (horizontal == "center" ? string.Empty : hTitle) };
        }

        static CommandExecutionResult ConfigureComponent(McpCommandContext context, string command, string[] acceptedTypes, ComponentMutationBuilder builder)
        {
            var targetResult = RequireTypedTarget(context.ArgsJson, acceptedTypes, out var scene, out var scenePath, out var target, out var objectPath, out var component, out var componentIndex);
            if (targetResult != null) return targetResult;
            var mutations = new List<ComponentMutation>();
            var mutationResult = builder(context.ArgsJson, scene, component, mutations);
            if (mutationResult != null) return mutationResult;
            if (mutations.Count == 0) return CommandExecutionResult.Error("schema_validation_failed", command + " requires at least one supported field");
            var changedFields = ChangedMutationFields(mutations);
            var changed = changedFields.Count > 0;
            var hash = ComponentConfigurePreconditionHash(command, scenePath, objectPath, component, componentIndex, mutations);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            if (!dryRun && changed)
            {
                var editResult = RequireEditing(command);
                if (editResult != null) return editResult;
                var precondition = RequirePrecondition(context.ArgsJson, hash, command);
                if (precondition != null) return precondition;
                Undo.RecordObject(component, command);
                for (var index = 0; index < mutations.Count; index++) mutations[index].Apply();
                EditorUtility.SetDirty(component);
                EditorSceneManager.MarkSceneDirty(scene);
                Canvas.ForceUpdateCanvases();
            }
            return CommandExecutionResult.Ok(ComponentConfigureJson(command, scene, scenePath, objectPath, component, componentIndex, mutations, changedFields, hash, dryRun, changed));
        }

        static CommandExecutionResult BuildImageMutations(string argsJson, Scene scene, Component component, List<ComponentMutation> mutations)
        {
            var result = AddColorMutation(argsJson, "color", component, "color", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "raycast_target", component, "raycastTarget", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "preserve_aspect", component, "preserveAspect", mutations); if (result != null) return result;
            result = AddAssetReferenceMutation(argsJson, "sprite", "sprite_asset_path", "clear_sprite", component, "sprite", mutations); if (result != null) return result;
            result = AddAssetReferenceMutation(argsJson, "texture", "texture_asset_path", "clear_texture", component, "texture", mutations); if (result != null) return result;
            return AddAssetReferenceMutation(argsJson, "material", "material_asset_path", "clear_material", component, "material", mutations);
        }

        static CommandExecutionResult BuildCanvasGroupMutations(string argsJson, Scene scene, Component component, List<ComponentMutation> mutations)
        {
            var result = AddFloatMutation(argsJson, "alpha", component, "alpha", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "interactable", component, "interactable", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "blocks_raycasts", component, "blocksRaycasts", mutations); if (result != null) return result;
            return AddBoolMutation(argsJson, "ignore_parent_groups", component, "ignoreParentGroups", mutations);
        }

        static CommandExecutionResult BuildLayoutMutations(string argsJson, Scene scene, Component component, List<ComponentMutation> mutations)
        {
            CommandExecutionResult result;
            result = AddFloatMutation(argsJson, "spacing", component, "spacing", mutations); if (result != null) return result;
            result = AddVector2Mutation(argsJson, "spacing_xy", component, "spacing", mutations); if (result != null) return result;
            result = AddVector2Mutation(argsJson, "cell_size", component, "cellSize", mutations); if (result != null) return result;
            result = AddEnumMutation(argsJson, "child_alignment", component, "childAlignment", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "child_control_width", component, "childControlWidth", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "child_control_height", component, "childControlHeight", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "child_force_expand_width", component, "childForceExpandWidth", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "child_force_expand_height", component, "childForceExpandHeight", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "child_scale_width", component, "childScaleWidth", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "child_scale_height", component, "childScaleHeight", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "reverse_arrangement", component, "reverseArrangement", mutations); if (result != null) return result;
            result = AddEnumMutation(argsJson, "start_corner", component, "startCorner", mutations); if (result != null) return result;
            result = AddEnumMutation(argsJson, "start_axis", component, "startAxis", mutations); if (result != null) return result;
            result = AddEnumMutation(argsJson, "constraint", component, "constraint", mutations); if (result != null) return result;
            result = AddIntMutation(argsJson, "constraint_count", component, "constraintCount", mutations); if (result != null) return result;
            result = AddEnumMutation(argsJson, "horizontal_fit", component, "horizontalFit", mutations); if (result != null) return result;
            result = AddEnumMutation(argsJson, "vertical_fit", component, "verticalFit", mutations); if (result != null) return result;
            result = AddBoolMutation(argsJson, "ignore_layout", component, "ignoreLayout", mutations); if (result != null) return result;
            result = AddFloatMutation(argsJson, "min_width", component, "minWidth", mutations); if (result != null) return result;
            result = AddFloatMutation(argsJson, "min_height", component, "minHeight", mutations); if (result != null) return result;
            result = AddFloatMutation(argsJson, "preferred_width", component, "preferredWidth", mutations); if (result != null) return result;
            result = AddFloatMutation(argsJson, "preferred_height", component, "preferredHeight", mutations); if (result != null) return result;
            result = AddFloatMutation(argsJson, "flexible_width", component, "flexibleWidth", mutations); if (result != null) return result;
            result = AddFloatMutation(argsJson, "flexible_height", component, "flexibleHeight", mutations); if (result != null) return result;
            return AddIntMutation(argsJson, "layout_priority", component, "layoutPriority", mutations);
        }

        static CommandExecutionResult BuildButtonMutations(string argsJson, Scene scene, Component component, List<ComponentMutation> mutations)
        {
            var result = AddBoolMutation(argsJson, "interactable", component, "interactable", mutations); if (result != null) return result;
            result = AddEnumMutation(argsJson, "transition", component, "transition", mutations); if (result != null) return result;
            return AddSceneComponentReferenceMutation(argsJson, scene, "target_graphic", "target_graphic_object_path", "clear_target_graphic", component, "targetGraphic", mutations);
        }

        static CommandExecutionResult BuildNavigationMutations(string argsJson, Scene scene, Component component, List<ComponentMutation> mutations)
        {
            if (!HasJsonProperty(argsJson, "mode") && !HasJsonProperty(argsJson, "select_on_up_path") && !HasJsonProperty(argsJson, "select_on_down_path") && !HasJsonProperty(argsJson, "select_on_left_path") && !HasJsonProperty(argsJson, "select_on_right_path") && !HasJsonProperty(argsJson, "clear_explicit")) return null;
            if (!TryGetWritableMember(component, "navigation", out var member)) return CommandExecutionResult.Error("invalid_field_value", "selected component does not expose writable navigation");
            var current = GetMemberValue(component, member);
            if (current == null) return CommandExecutionResult.Error("invalid_field_value", "navigation value is not readable");
            var requested = current;
            if (HasJsonProperty(argsJson, "mode"))
            {
                var modeMember = FindMember(requested.GetType(), "mode");
                if (modeMember == null || !MemberType(modeMember).IsEnum) return CommandExecutionResult.Error("invalid_field_value", "navigation mode is not writable");
                if (!TryParseEnumValue(MemberType(modeMember), JsonUtil.ExtractString(argsJson, "mode"), out var mode)) return CommandExecutionResult.Error("invalid_field_value", "mode is not valid for this Selectable navigation enum");
                SetMemberValue(requested, modeMember, mode);
            }
            if (JsonUtil.ExtractBool(argsJson, "clear_explicit", false))
            {
                SetNavigationReference(requested, "selectOnUp", null);
                SetNavigationReference(requested, "selectOnDown", null);
                SetNavigationReference(requested, "selectOnLeft", null);
                SetNavigationReference(requested, "selectOnRight", null);
            }
            var result = SetNavigationReferenceFromPath(argsJson, scene, requested, "select_on_up_path", "selectOnUp"); if (result != null) return result;
            result = SetNavigationReferenceFromPath(argsJson, scene, requested, "select_on_down_path", "selectOnDown"); if (result != null) return result;
            result = SetNavigationReferenceFromPath(argsJson, scene, requested, "select_on_left_path", "selectOnLeft"); if (result != null) return result;
            result = SetNavigationReferenceFromPath(argsJson, scene, requested, "select_on_right_path", "selectOnRight"); if (result != null) return result;
            mutations.Add(new ComponentMutation("navigation", NavigationJson(current), NavigationJson(requested), delegate { SetMemberValue(component, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddBoolMutation(string json, string jsonField, object target, string memberName, List<ComponentMutation> mutations)
        {
            if (!HasJsonProperty(json, jsonField)) return null;
            if (!TryGetWritableMember(target, memberName, out var member) || MemberType(member) != typeof(bool)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " is not supported by the selected component");
            var current = ConvertToBool(GetMemberValue(target, member));
            var requested = JsonUtil.ExtractBool(json, jsonField, false);
            mutations.Add(new ComponentMutation(jsonField, JsonUtil.Bool(current), JsonUtil.Bool(requested), delegate { SetMemberValue(target, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddFloatMutation(string json, string jsonField, object target, string memberName, List<ComponentMutation> mutations)
        {
            if (!HasJsonProperty(json, jsonField)) return null;
            if (!TryGetWritableMember(target, memberName, out var member) || !IsNumericMember(member)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " is not supported by the selected component");
            if (!TryExtractFloatField(json, jsonField, out var requested)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " must be a number");
            var current = ConvertToFloat(GetMemberValue(target, member));
            mutations.Add(new ComponentMutation(jsonField, Number(current), Number(requested), delegate { SetNumericMemberValue(target, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddIntMutation(string json, string jsonField, object target, string memberName, List<ComponentMutation> mutations)
        {
            if (!HasJsonProperty(json, jsonField)) return null;
            if (!TryGetWritableMember(target, memberName, out var member) || MemberType(member) != typeof(int)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " is not supported by the selected component");
            var current = JsonInt(GetMemberValue(target, member));
            var requested = JsonUtil.ExtractInt(json, jsonField, 0);
            mutations.Add(new ComponentMutation(jsonField, current.ToString(CultureInfo.InvariantCulture), requested.ToString(CultureInfo.InvariantCulture), delegate { SetMemberValue(target, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddColorMutation(string json, string jsonField, object target, string memberName, List<ComponentMutation> mutations)
        {
            if (!HasJsonProperty(json, jsonField)) return null;
            if (!TryGetWritableMember(target, memberName, out var member) || MemberType(member) != typeof(Color)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " is not supported by the selected component");
            if (!TryExtractVector4(json, jsonField, out var value)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " must be an array of four numbers");
            var current = GetMemberValue(target, member) is Color ? (Color)GetMemberValue(target, member) : Color.white;
            var requested = new Color(value.x, value.y, value.z, value.w);
            mutations.Add(new ComponentMutation(jsonField, ColorJson(current), ColorJson(requested), delegate { SetMemberValue(target, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddVector2Mutation(string json, string jsonField, object target, string memberName, List<ComponentMutation> mutations)
        {
            if (!HasJsonProperty(json, jsonField)) return null;
            if (!TryGetWritableMember(target, memberName, out var member) || MemberType(member) != typeof(Vector2)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " is not supported by the selected component");
            if (!TryExtractVector2(json, jsonField, out var requested)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " must be an array of two numbers");
            var current = GetMemberValue(target, member) is Vector2 ? (Vector2)GetMemberValue(target, member) : Vector2.zero;
            mutations.Add(new ComponentMutation(jsonField, Vector2Json(current), Vector2Json(requested), delegate { SetMemberValue(target, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddEnumMutation(string json, string jsonField, object target, string memberName, List<ComponentMutation> mutations)
        {
            if (!HasJsonProperty(json, jsonField)) return null;
            if (!TryGetWritableMember(target, memberName, out var member) || !MemberType(member).IsEnum) return CommandExecutionResult.Error("invalid_field_value", jsonField + " is not supported by the selected component");
            if (!TryParseEnumValue(MemberType(member), JsonUtil.ExtractString(json, jsonField), out var requested)) return CommandExecutionResult.Error("invalid_field_value", jsonField + " is not valid for this component enum");
            var current = GetMemberValue(target, member);
            mutations.Add(new ComponentMutation(jsonField, JsonUtil.String(current == null ? string.Empty : current.ToString()), JsonUtil.String(requested.ToString()), delegate { SetMemberValue(target, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddAssetReferenceMutation(string json, string fieldName, string assetPathField, string clearField, object target, string memberName, List<ComponentMutation> mutations)
        {
            var hasPath = !string.IsNullOrEmpty(JsonUtil.ExtractString(json, assetPathField));
            var clear = JsonUtil.ExtractBool(json, clearField, false);
            if (!hasPath && !clear) return null;
            if (hasPath && clear) return CommandExecutionResult.Error("schema_validation_failed", "Provide only one of " + assetPathField + " or " + clearField);
            if (!TryGetWritableMember(target, memberName, out var member) || !typeof(UnityEngine.Object).IsAssignableFrom(MemberType(member))) return CommandExecutionResult.Error("invalid_field_value", fieldName + " is not supported by the selected component");
            UnityEngine.Object requested = null;
            if (hasPath)
            {
                string path;
                try { path = PathGuard.RequireUnityAssetPath(JsonUtil.ExtractString(json, assetPathField)); } catch (Exception ex) { return CommandExecutionResult.Error("invalid_field_value", assetPathField + " must be Unity-relative", JsonUtil.String(ex.Message)); }
                requested = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(path);
                if (requested == null) return CommandExecutionResult.Error("asset_not_found", assetPathField + " was not found");
                if (!MemberType(member).IsAssignableFrom(requested.GetType())) return CommandExecutionResult.Error("invalid_field_value", assetPathField + " is not assignable to " + MemberType(member).FullName);
            }
            var current = GetMemberValue(target, member) as UnityEngine.Object;
            mutations.Add(new ComponentMutation(fieldName, ObjectReferenceJson(current), ObjectReferenceJson(requested), delegate { SetMemberValue(target, member, requested); }));
            return null;
        }

        static CommandExecutionResult AddSceneComponentReferenceMutation(string json, Scene scene, string fieldName, string objectPathField, string clearField, object target, string memberName, List<ComponentMutation> mutations)
        {
            var hasPath = !string.IsNullOrEmpty(JsonUtil.ExtractString(json, objectPathField));
            var clear = JsonUtil.ExtractBool(json, clearField, false);
            if (!hasPath && !clear) return null;
            if (hasPath && clear) return CommandExecutionResult.Error("schema_validation_failed", "Provide only one of " + objectPathField + " or " + clearField);
            if (!TryGetWritableMember(target, memberName, out var member) || !typeof(Component).IsAssignableFrom(MemberType(member))) return CommandExecutionResult.Error("invalid_field_value", fieldName + " is not supported by the selected component");
            Component requested = null;
            if (hasPath)
            {
                var objectResult = RequireSceneObject(json, scene, objectPathField, out var sourceObject, out var sourcePath);
                if (objectResult != null) return objectResult;
                var componentResult = ResolveAssignableComponent(json, sourceObject, fieldName + "_", MemberType(member), out requested);
                if (componentResult != null) return componentResult;
            }
            var current = GetMemberValue(target, member) as UnityEngine.Object;
            mutations.Add(new ComponentMutation(fieldName, ObjectReferenceJson(current), ObjectReferenceJson(requested), delegate { SetMemberValue(target, member, requested); }));
            return null;
        }

        static List<string> ChangedMutationFields(List<ComponentMutation> mutations)
        {
            var fields = new List<string>();
            for (var index = 0; index < mutations.Count; index++) if (mutations[index].CurrentJson != mutations[index].RequestedJson) fields.Add(mutations[index].Name);
            return fields;
        }

        static string ComponentConfigureJson(string command, Scene scene, string scenePath, string objectPath, Component component, int componentIndex, List<ComponentMutation> mutations, List<string> changedFields, string hash, bool dryRun, bool changed)
        {
            return "{\"status\":\"ok\",\"command\":" + JsonUtil.String(command) + ",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed) + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"component\":" + ComponentJson(component, componentIndex) + ",\"changed_fields\":" + StringArrayJson(changedFields) + ",\"fields\":" + MutationsJson(mutations) + ",\"precondition_hash\":" + JsonUtil.String(hash) + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]") + SceneContractJson(scene, dryRun, !dryRun && changed) + "}";
        }

        static string ComponentConfigurePreconditionHash(string command, string scenePath, string objectPath, Component component, int componentIndex, List<ComponentMutation> mutations)
        {
            return Sha256Prefixed(command + "\n" + scenePath + "\n" + objectPath + "\n" + componentIndex + "\n" + (component.GetType().FullName ?? component.GetType().Name) + "\n" + MutationsJson(mutations));
        }

        static string MutationsJson(List<ComponentMutation> mutations)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < mutations.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append("{\"field\":").Append(JsonUtil.String(mutations[index].Name)).Append(",\"current\":").Append(mutations[index].CurrentJson).Append(",\"requested\":").Append(mutations[index].RequestedJson).Append('}');
            }
            builder.Append(']');
            return builder.ToString();
        }

        static string NavigationJson(object navigation)
        {
            return "{\"mode\":" + JsonUtil.String(MemberValueLabel(navigation, "mode")) + ",\"select_on_up\":" + ObjectReferenceJson(GetMemberValue(navigation, "selectOnUp") as UnityEngine.Object) + ",\"select_on_down\":" + ObjectReferenceJson(GetMemberValue(navigation, "selectOnDown") as UnityEngine.Object) + ",\"select_on_left\":" + ObjectReferenceJson(GetMemberValue(navigation, "selectOnLeft") as UnityEngine.Object) + ",\"select_on_right\":" + ObjectReferenceJson(GetMemberValue(navigation, "selectOnRight") as UnityEngine.Object) + "}";
        }

        static CommandExecutionResult SetNavigationReferenceFromPath(string argsJson, Scene scene, object navigation, string pathField, string memberName)
        {
            if (!HasJsonProperty(argsJson, pathField)) return null;
            var member = FindMember(navigation.GetType(), memberName);
            if (member == null || !typeof(Component).IsAssignableFrom(MemberType(member))) return CommandExecutionResult.Error("invalid_field_value", memberName + " is not supported by this Navigation type");
            var objectResult = RequireSceneObject(argsJson, scene, pathField, out var sourceObject, out var sourcePath);
            if (objectResult != null) return objectResult;
            var componentResult = ResolveAssignableComponent(argsJson, sourceObject, string.Empty, MemberType(member), out var requested);
            if (componentResult != null) return componentResult;
            SetMemberValue(navigation, member, requested);
            return null;
        }

        static void SetNavigationReference(object navigation, string memberName, Component value)
        {
            var member = FindMember(navigation.GetType(), memberName);
            if (member != null) SetMemberValue(navigation, member, value);
        }

        static CommandExecutionResult RequireTypedTarget(string argsJson, string[] acceptedTypes, out Scene scene, out string scenePath, out GameObject target, out string objectPath, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var sceneResult = RequireLoadedScene(argsJson, out scene, out scenePath);
            if (sceneResult != null) { target = null; objectPath = string.Empty; return sceneResult; }
            var objectResult = RequireSceneObject(argsJson, scene, "object_path", out target, out objectPath);
            if (objectResult != null) return objectResult;
            if (target.GetComponent<RectTransform>() == null && target.GetComponent<Canvas>() == null) return CommandExecutionResult.Error("invalid_field_value", "object_path must reference a uGUI object with RectTransform or Canvas");
            return RequireAcceptedComponent(argsJson, target, acceptedTypes, out component, out componentIndex);
        }

        static CommandExecutionResult RequireAcceptedComponent(string argsJson, GameObject target, string[] acceptedTypes, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var hasSelector = TryReadInt(argsJson, "component_index", out var requestedIndex) || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "component_type")) || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, "full_type_name"));
            if (hasSelector)
            {
                var result = RequireComponent(argsJson, target, string.Empty, out component, out componentIndex);
                if (result != null) return result;
                return IsAcceptedComponent(component, acceptedTypes) ? null : CommandExecutionResult.Error("invalid_field_value", "component selector does not match this command's allowed uGUI component types");
            }
            var components = target.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !IsAcceptedComponent(candidate, acceptedTypes)) continue;
                if (component != null) return CommandExecutionResult.Error("invalid_field_value", "component selector is ambiguous on this object; pass component_index");
                component = candidate;
                componentIndex = index;
            }
            return component == null ? CommandExecutionResult.Error("asset_not_found", "Allowed uGUI component was not found") : null;
        }

        static bool IsAcceptedComponent(Component component, string[] acceptedTypes)
        {
            if (component == null) return false;
            for (var index = 0; index < acceptedTypes.Length; index++) if (IsTypeNamed(component.GetType(), acceptedTypes[index])) return true;
            return false;
        }

        static bool IsTypeNamed(Type type, string fullName)
        {
            while (type != null)
            {
                if (string.Equals(type.FullName ?? type.Name, fullName, StringComparison.Ordinal) || string.Equals(type.Name, fullName, StringComparison.Ordinal)) return true;
                type = type.BaseType;
            }
            return false;
        }

        static CommandExecutionResult ResolveAssignableComponent(string argsJson, GameObject sourceObject, string selectorPrefix, Type expectedType, out Component component)
        {
            component = null;
            var hasSelector = TryReadInt(argsJson, selectorPrefix + "component_index", out var requestedIndex) || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, selectorPrefix + "component_type")) || !string.IsNullOrEmpty(JsonUtil.ExtractString(argsJson, selectorPrefix + "full_type_name"));
            if (hasSelector)
            {
                var selected = RequireComponent(argsJson, sourceObject, selectorPrefix, out component, out var componentIndex);
                if (selected != null) return selected;
                return expectedType.IsAssignableFrom(component.GetType()) ? null : CommandExecutionResult.Error("invalid_field_value", "selected source component is not assignable to " + expectedType.FullName);
            }
            var components = sourceObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !expectedType.IsAssignableFrom(candidate.GetType())) continue;
                if (component != null) return CommandExecutionResult.Error("invalid_field_value", "source component selector is ambiguous; pass component_index");
                component = candidate;
            }
            return component == null ? CommandExecutionResult.Error("asset_not_found", "Assignable source component was not found") : null;
        }

        static CommandExecutionResult PlanApplySpec(McpCommandContext context, string scenePath, List<ApplySpecStep> steps, out string planJson, out string preconditionHash)
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
                    builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"status\":\"planned_after_create\",\"object_path\":").Append(JsonUtil.String(NormalizeObjectPath(targetObjectPath))).Append("}");
                    continue;
                }
                var nestedArgs = BuildNestedArgs(scenePath, step.ArgsJson, true, string.Empty);
                var nested = InvokeApplySpecStep(context, step.Command, nestedArgs);
                if (nested.Status != "ok")
                {
                    planJson = "[]";
                    preconditionHash = string.Empty;
                    return CommandExecutionResult.Error(nested.ErrorCode, "ugui.screen.apply_spec dry-run failed at step " + index + ": " + nested.ErrorMessage, "{\"step_index\":" + index + ",\"command\":" + JsonUtil.String(step.Command) + "}");
                }
                builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"result\":").Append(nested.DataJson).Append('}');
                if (step.Command == "ugui.object.create")
                {
                    var planned = JsonUtil.ExtractString(nested.DataJson, "planned_object_path");
                    if (!string.IsNullOrEmpty(planned)) plannedObjects.Add(NormalizeObjectPath(planned));
                }
            }
            planJson = "[" + builder + "]";
            preconditionHash = Sha256Prefixed("ugui.screen.apply_spec\n" + scenePath + "\n" + StepsStableJson(steps) + "\n" + planJson);
            return null;
        }

        static CommandExecutionResult ApplySpecSteps(McpCommandContext context, string scenePath, List<ApplySpecStep> steps, out string appliedJson, out int failedIndex, out string failureJson)
        {
            var builder = new StringBuilder();
            failedIndex = -1;
            failureJson = string.Empty;
            for (var index = 0; index < steps.Count; index++)
            {
                var step = steps[index];
                var perStepHash = string.Empty;
                if (StepRequiresPrecondition(step.Command))
                {
                    var dryRunArgs = BuildNestedArgs(scenePath, step.ArgsJson, true, string.Empty);
                    var dryRun = InvokeApplySpecStep(context, step.Command, dryRunArgs);
                    if (dryRun.Status != "ok")
                    {
                        failedIndex = index;
                        failureJson = ErrorJson(dryRun);
                        appliedJson = "[" + builder + "]";
                        return dryRun;
                    }
                    perStepHash = JsonUtil.ExtractString(dryRun.DataJson, "precondition_hash");
                }
                var nestedArgs = BuildNestedArgs(scenePath, step.ArgsJson, false, perStepHash);
                var result = InvokeApplySpecStep(context, step.Command, nestedArgs);
                if (result.Status != "ok")
                {
                    failedIndex = index;
                    failureJson = ErrorJson(result);
                    appliedJson = "[" + builder + "]";
                    return result;
                }
                if (builder.Length > 0) builder.Append(',');
                builder.Append("{\"step_index\":").Append(index).Append(",\"command\":").Append(JsonUtil.String(step.Command)).Append(",\"result\":").Append(result.DataJson).Append('}');
            }
            appliedJson = "[" + builder + "]";
            return null;
        }

        static CommandExecutionResult InvokeApplySpecStep(McpCommandContext context, string command, string argsJson)
        {
            var nested = new McpCommandContext(context.State, context.RequestId, context.Project, argsJson, context.SettingsJson);
            if (command == "ugui.object.create") return UguiInspectionCommands.UguiObjectCreate(nested);
            if (command == "ugui.component.add") return UguiInspectionCommands.UguiComponentAdd(nested);
            if (command == "ugui.text.set") return UguiTextSet(nested);
            if (command == "ugui.image.set") return UguiImageSet(nested);
            if (command == "ugui.canvas_group.set") return UguiCanvasGroupSet(nested);
            if (command == "ugui.layout_group.configure") return UguiLayoutGroupConfigure(nested);
            if (command == "ugui.button.configure") return UguiButtonConfigure(nested);
            if (command == "ugui.selectable.navigation.set") return UguiSelectableNavigationSet(nested);
            return CommandExecutionResult.Error("invalid_field_value", "ugui.screen.apply_spec supports only same-feature uGUI safe-write commands in this slice");
        }

        static CommandExecutionResult ExtractApplySpecSteps(string json, out List<ApplySpecStep> steps)
        {
            steps = new List<ApplySpecStep>();
            var arrayJson = ExtractArray(json, "steps");
            if (string.IsNullOrEmpty(arrayJson)) return CommandExecutionResult.Error("schema_validation_failed", "steps must be an array");
            var objects = new List<string>();
            if (!TryExtractObjectArrayItems(arrayJson, objects)) return CommandExecutionResult.Error("schema_validation_failed", "steps must contain objects");
            if (objects.Count == 0 || objects.Count > MaxApplySpecSteps) return CommandExecutionResult.Error("invalid_field_value", "steps must contain 1 to 32 items");
            for (var index = 0; index < objects.Count; index++)
            {
                var stepJson = objects[index];
                var command = JsonUtil.ExtractString(stepJson, "command");
                if (!IsApplySpecCommand(command)) return CommandExecutionResult.Error("invalid_field_value", "step command is not supported by ugui.screen.apply_spec first slice");
                var args = JsonUtil.ExtractObject(stepJson, "args");
                if (string.IsNullOrEmpty(args) || args == "{}") return CommandExecutionResult.Error("schema_validation_failed", "each step requires args object");
                steps.Add(new ApplySpecStep(command, args));
            }
            return null;
        }

        static string UguiTextSetJson(Scene scene, string scenePath, string objectPath, GameObject target, Component component, int componentIndex, TextState current, TextState requested, List<string> changedFields, string hash, bool dryRun, bool changed)
        {
            return "{\"status\":\"ok\",\"command\":\"ugui.text.set\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"changed\":" + JsonUtil.Bool(!dryRun && changed) + ",\"would_change\":" + JsonUtil.Bool(dryRun && changed) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"object_path\":" + JsonUtil.String(objectPath) + ",\"component\":" + ComponentJson(component, componentIndex) + ",\"changed_fields\":" + StringArrayJson(changedFields) + ",\"current\":" + TextStateJson(current) + ",\"requested\":" + TextStateJson(requested) + ",\"precondition_hash\":" + JsonUtil.String(hash) + ",\"changed_components\":" + (!dryRun && changed ? "[" + ComponentJson(component, componentIndex) + "]" : "[]") + SceneContractJson(scene, dryRun, !dryRun && changed) + "}";
        }

        static string UguiScreenApplySpecJson(Scene scene, string scenePath, bool dryRun, bool applied, bool partialFailure, string preconditionHash, string planJson, string appliedJson, int failedIndex, string failureJson)
        {
            return "{\"status\":\"ok\",\"command\":\"ugui.screen.apply_spec\",\"dry_run\":" + JsonUtil.Bool(dryRun) + ",\"applied\":" + JsonUtil.Bool(applied) + ",\"partial_failure\":" + JsonUtil.Bool(partialFailure) + ",\"scene_path\":" + JsonUtil.String(scenePath) + ",\"precondition_hash\":" + JsonUtil.String(preconditionHash) + ",\"plan_steps\":" + planJson + ",\"applied_steps\":" + appliedJson + ",\"failed_step_index\":" + failedIndex + ",\"failure\":" + (string.IsNullOrEmpty(failureJson) ? "null" : failureJson) + SceneContractJson(scene, dryRun, applied || partialFailure) + "}";
        }

        static string SceneContractJson(Scene scene, bool dryRun, bool changed)
        {
            return ",\"scene_dirty\":" + JsonUtil.Bool(scene.IsValid() && scene.isDirty) + ",\"scene_saved\":false,\"saved_prefab\":false,\"opens_scenes\":false,\"writes_prefab_assets\":false,\"loaded_scenes_only\":true,\"warnings\":[\"" + (dryRun ? "dry_run_no_scene_mutation" : changed ? "scene_dirty_not_saved" : "no_scene_change_applied") + "\"],\"next_valid_actions\":[\"scene.inspect_object\",\"scene.inspect_component\",\"scene.save\"]";
        }

        static string TextStateJson(TextState state)
        {
            return "{\"text\":" + JsonUtil.String(state.Text) + ",\"font_size\":" + Number(state.FontSize) + ",\"color\":" + ColorJson(state.Color) + ",\"raycast_target\":" + JsonUtil.Bool(state.RaycastTarget) + ",\"alignment\":" + JsonUtil.String(state.Alignment) + ",\"horizontal_alignment\":" + JsonUtil.String(state.HorizontalAlignment) + ",\"vertical_alignment\":" + JsonUtil.String(state.VerticalAlignment) + "}";
        }

        static string ComponentJson(Component component, int index)
        {
            if (component == null) return "{\"component_index\":" + index + ",\"missing\":true}";
            var type = component.GetType();
            return "{\"component_index\":" + index + ",\"type\":" + JsonUtil.String(type.Name) + ",\"full_type_name\":" + JsonUtil.String(type.FullName ?? type.Name) + ",\"assembly_name\":" + JsonUtil.String(type.Assembly.GetName().Name) + "}";
        }

        static string UguiTextPreconditionHash(string scenePath, string objectPath, Component component, int componentIndex, TextState current, TextState requested)
        {
            return Sha256Prefixed("ugui.text.set\n" + scenePath + "\n" + objectPath + "\n" + componentIndex + "\n" + (component.GetType().FullName ?? component.GetType().Name) + "\n" + TextStateJson(current) + "\n" + TextStateJson(requested));
        }

        static string BuildNestedArgs(string scenePath, string argsJson, bool dryRun, string preconditionHash)
        {
            var body = TrimObjectBody(argsJson);
            var prefix = "\"scene_path\":" + JsonUtil.String(scenePath) + ",\"dry_run\":" + JsonUtil.Bool(dryRun);
            if (!string.IsNullOrEmpty(preconditionHash)) prefix += ",\"precondition_hash\":" + JsonUtil.String(preconditionHash);
            return "{" + prefix + (string.IsNullOrEmpty(body) ? string.Empty : "," + body) + "}";
        }

        static string StepsStableJson(List<ApplySpecStep> steps)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < steps.Count; index++)
            {
                if (index > 0) builder.Append(',');
                builder.Append("{\"command\":").Append(JsonUtil.String(steps[index].Command)).Append(",\"args\":").Append(steps[index].ArgsJson).Append('}');
            }
            builder.Append(']');
            return builder.ToString();
        }

        static CommandExecutionResult RequirePrecondition(string argsJson, string hash, string command)
        {
            var actual = JsonUtil.ExtractString(argsJson, "precondition_hash").Trim();
            if (string.IsNullOrEmpty(actual)) return CommandExecutionResult.Error("precondition_required", "precondition_hash is required when " + command + " runs with dry_run:false and would change the scene", "{\"precondition_hash\":" + JsonUtil.String(hash) + "}");
            return string.Equals(actual, hash, StringComparison.Ordinal) ? null : CommandExecutionResult.Error("precondition_failed", "precondition_hash does not match current scene state or requested operation", "{\"precondition_hash\":" + JsonUtil.String(hash) + "}");
        }

        static CommandExecutionResult RequireLoadedScene(string argsJson, out Scene scene, out string scenePath)
        {
            scene = default(Scene);
            scenePath = string.Empty;
            var raw = JsonUtil.ExtractString(argsJson, "scene_path");
            if (string.IsNullOrEmpty(raw)) return CommandExecutionResult.Error("schema_validation_failed", "Argument scene_path is required");
            try { scenePath = PathGuard.RequireUnityAssetPath(raw); } catch (Exception ex) { return CommandExecutionResult.Error("invalid_field_value", "scene_path must be Unity-relative", JsonUtil.String(ex.Message)); }
            if (!scenePath.EndsWith(".unity", StringComparison.OrdinalIgnoreCase)) return CommandExecutionResult.Error("invalid_field_value", "scene_path must end with .unity");
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var candidate = SceneManager.GetSceneAt(index);
                if (string.Equals(candidate.path, scenePath, StringComparison.OrdinalIgnoreCase)) { scene = candidate; break; }
            }
            if (!scene.IsValid() || !scene.isLoaded) return CommandExecutionResult.Error("asset_not_found", "scene_path must reference a loaded scene");
            return null;
        }

        static CommandExecutionResult RequireSceneObject(string argsJson, Scene scene, string field, out GameObject target, out string objectPath)
        {
            target = null;
            objectPath = JsonUtil.ExtractString(argsJson, field);
            if (string.IsNullOrEmpty(objectPath)) return CommandExecutionResult.Error("schema_validation_failed", "Argument " + field + " is required");
            if (objectPath.Length > 512 || objectPath.IndexOf('\0') >= 0 || objectPath.IndexOf("..", StringComparison.Ordinal) >= 0) return CommandExecutionResult.Error("invalid_field_value", field + " must be a safe scene hierarchy path");
            objectPath = NormalizeObjectPath(objectPath);
            target = FindByPath(scene.GetRootGameObjects(), objectPath);
            return target == null ? CommandExecutionResult.Error("asset_not_found", field + " was not found in the loaded scene") : null;
        }

        static CommandExecutionResult RequireComponent(string argsJson, GameObject target, string prefix, out Component component, out int componentIndex)
        {
            component = null;
            componentIndex = -1;
            var hasIndex = TryReadInt(argsJson, prefix + "component_index", out var requestedIndex);
            var requestedType = JsonUtil.ExtractString(argsJson, prefix + "component_type");
            var fullType = JsonUtil.ExtractString(argsJson, prefix + "full_type_name");
            var assembly = JsonUtil.ExtractString(argsJson, prefix + "assembly_name");
            if (!hasIndex && string.IsNullOrEmpty(requestedType) && string.IsNullOrEmpty(fullType)) return CommandExecutionResult.Error("schema_validation_failed", "Argument " + prefix + "component_type, " + prefix + "full_type_name, or " + prefix + "component_index is required");
            var components = target.GetComponents<Component>();
            if (hasIndex)
            {
                if (requestedIndex < 0 || requestedIndex >= components.Length) return CommandExecutionResult.Error("asset_not_found", "Component was not found");
                component = components[requestedIndex];
                componentIndex = requestedIndex;
                if (component == null) return CommandExecutionResult.Error("invalid_field_value", "Missing script components are not supported");
                if ((!string.IsNullOrEmpty(requestedType) || !string.IsNullOrEmpty(fullType) || !string.IsNullOrEmpty(assembly)) && !MatchesComponentSelector(component.GetType(), requestedType, fullType, assembly)) return CommandExecutionResult.Error("invalid_field_value", "component_index does not match component selector");
                return null;
            }
            for (var index = 0; index < components.Length; index++)
            {
                var candidate = components[index];
                if (candidate == null || !MatchesComponentSelector(candidate.GetType(), requestedType, fullType, assembly)) continue;
                if (component != null) return CommandExecutionResult.Error("invalid_field_value", "component selector is ambiguous on this object; pass component_index");
                component = candidate;
                componentIndex = index;
            }
            return component == null ? CommandExecutionResult.Error("asset_not_found", "Component was not found") : null;
        }

        static CommandExecutionResult RequireEditing(string command)
        {
            if (EditorApplication.isCompiling) return CommandExecutionResult.Error("unity_busy", command + " requires editing status; Unity is compiling");
            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode) return CommandExecutionResult.Error("unity_busy", command + " requires Edit Mode; Unity is in or entering Play Mode");
            return null;
        }

        static bool IsTextComponent(Component component)
        {
            if (component == null) return false;
            var type = component.GetType();
            while (type != null)
            {
                var fullName = type.FullName ?? type.Name;
                if (fullName == "UnityEngine.UI.Text" || fullName == "TMPro.TMP_Text" || fullName == "TMPro.TextMeshProUGUI") return true;
                type = type.BaseType;
            }
            return false;
        }

        static bool MatchesComponentSelector(Type type, string componentType, string fullTypeName, string assemblyName)
        {
            if (!string.IsNullOrEmpty(assemblyName) && !string.Equals(type.Assembly.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(fullTypeName)) return string.Equals(type.FullName ?? type.Name, fullTypeName, StringComparison.OrdinalIgnoreCase);
            return !string.IsNullOrEmpty(componentType) && (string.Equals(type.Name, componentType, StringComparison.OrdinalIgnoreCase) || string.Equals(type.FullName ?? type.Name, componentType, StringComparison.OrdinalIgnoreCase));
        }

        static bool IsApplySpecCommand(string command)
        {
            return command == "ugui.object.create" || command == "ugui.component.add" || command == "ugui.text.set" || command == "ugui.image.set" || command == "ugui.canvas_group.set" || command == "ugui.layout_group.configure" || command == "ugui.button.configure" || command == "ugui.selectable.navigation.set";
        }

        static bool StepRequiresPrecondition(string command)
        {
            return command != "ugui.object.create" && command != "ugui.component.add";
        }

        static bool TryGetWritableMember(object target, string memberName, out MemberInfo member)
        {
            member = target == null ? null : FindMember(target.GetType(), memberName);
            if (member == null) return false;
            var property = member as PropertyInfo;
            if (property != null) return property.CanRead && property.CanWrite && property.GetIndexParameters().Length == 0;
            var field = member as FieldInfo;
            return field != null && !field.IsInitOnly;
        }

        static MemberInfo FindMember(Type type, string memberName)
        {
            while (type != null)
            {
                var property = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (property != null && property.GetIndexParameters().Length == 0) return property;
                var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null) return field;
                type = type.BaseType;
            }
            return null;
        }

        static Type MemberType(MemberInfo member)
        {
            var property = member as PropertyInfo;
            if (property != null) return property.PropertyType;
            return ((FieldInfo)member).FieldType;
        }

        static object GetMemberValue(object target, string memberName)
        {
            return target == null ? null : GetMemberValue(target, FindMember(target.GetType(), memberName));
        }

        static object GetMemberValue(object target, MemberInfo member)
        {
            if (target == null || member == null) return null;
            var property = member as PropertyInfo;
            return property != null ? property.GetValue(target, null) : ((FieldInfo)member).GetValue(target);
        }

        static void SetMemberValue(object target, MemberInfo member, object value)
        {
            var property = member as PropertyInfo;
            if (property != null) property.SetValue(target, value, null);
            else ((FieldInfo)member).SetValue(target, value);
        }

        static bool IsNumericMember(MemberInfo member)
        {
            var type = MemberType(member);
            return type == typeof(float) || type == typeof(double) || type == typeof(int);
        }

        static void SetNumericMemberValue(object target, MemberInfo member, float value)
        {
            var type = MemberType(member);
            if (type == typeof(int)) SetMemberValue(target, member, Mathf.RoundToInt(value));
            else if (type == typeof(double)) SetMemberValue(target, member, (double)value);
            else SetMemberValue(target, member, value);
        }

        static bool TryParseEnumValue(Type enumType, string raw, out object value)
        {
            value = null;
            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numeric))
            {
                value = Enum.ToObject(enumType, numeric);
                return true;
            }
            var normalized = NormalizeToken(raw);
            var names = Enum.GetNames(enumType);
            for (var index = 0; index < names.Length; index++)
            {
                if (NormalizeToken(names[index]) == normalized)
                {
                    value = Enum.Parse(enumType, names[index]);
                    return true;
                }
            }
            return false;
        }

        static string NormalizeToken(string value)
        {
            var builder = new StringBuilder();
            value = value ?? string.Empty;
            for (var index = 0; index < value.Length; index++)
            {
                var ch = value[index];
                if (char.IsLetterOrDigit(ch)) builder.Append(char.ToLowerInvariant(ch));
            }
            return builder.ToString();
        }

        static string MemberValueLabel(object target, string memberName)
        {
            var value = GetMemberValue(target, memberName);
            return value == null ? string.Empty : value.ToString();
        }

        static int JsonInt(object value)
        {
            try { return value == null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture); } catch { return 0; }
        }

        static object GetPropertyValue(object target, string propertyName)
        {
            if (target == null) return null;
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property == null || !property.CanRead || property.GetIndexParameters().Length != 0 ? null : property.GetValue(target, null);
        }

        static bool HasWritableProperty(object target, string propertyName)
        {
            if (target == null) return false;
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return property != null && property.CanWrite && property.GetIndexParameters().Length == 0;
        }

        static void SetPropertyValue(object target, string propertyName, object value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            property.SetValue(target, value, null);
        }

        static void SetNumericPropertyValue(object target, string propertyName, float value)
        {
            var property = target.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (property.PropertyType == typeof(int)) property.SetValue(target, Mathf.RoundToInt(value), null);
            else if (property.PropertyType == typeof(float)) property.SetValue(target, value, null);
            else if (property.PropertyType == typeof(double)) property.SetValue(target, (double)value, null);
        }

        static void InferAlignment(string alignment, out string horizontal, out string vertical)
        {
            var value = (alignment ?? string.Empty).ToLowerInvariant();
            horizontal = value.IndexOf("right", StringComparison.Ordinal) >= 0 ? "right" : value.IndexOf("left", StringComparison.Ordinal) >= 0 ? "left" : value.IndexOf("justified", StringComparison.Ordinal) >= 0 ? "justified" : "center";
            vertical = value.IndexOf("top", StringComparison.Ordinal) >= 0 || value.IndexOf("upper", StringComparison.Ordinal) >= 0 ? "top" : value.IndexOf("bottom", StringComparison.Ordinal) >= 0 || value.IndexOf("lower", StringComparison.Ordinal) >= 0 ? "bottom" : value.IndexOf("baseline", StringComparison.Ordinal) >= 0 ? "baseline" : "middle";
        }

        static string NormalizeHorizontalAlignment(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == "left" || value == "center" || value == "right" || value == "justified" ? value : string.Empty;
        }

        static string NormalizeVerticalAlignment(string value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();
            return value == "top" || value == "middle" || value == "bottom" || value == "baseline" ? value : string.Empty;
        }

        static string Title(string value) { return string.IsNullOrEmpty(value) ? string.Empty : char.ToUpperInvariant(value[0]) + value.Substring(1).ToLowerInvariant(); }
        static bool TryReadInt(string json, string property, out int value) { value = JsonUtil.ExtractInt(json, property, int.MinValue); return HasJsonProperty(json, property); }
        static bool HasJsonProperty(string json, string property) { return !string.IsNullOrEmpty(json) && json.IndexOf("\"" + property + "\"", StringComparison.Ordinal) >= 0; }
        static float ConvertToFloat(object value) { try { return value == null ? 0f : Convert.ToSingle(value, CultureInfo.InvariantCulture); } catch { return 0f; } }
        static bool ConvertToBool(object value) { return value is bool && (bool)value; }
        static float ColorDistance(Color a, Color b) { return Math.Abs(a.r - b.r) + Math.Abs(a.g - b.g) + Math.Abs(a.b - b.b) + Math.Abs(a.a - b.a); }
        static string NormalizeObjectPath(string path) { return string.IsNullOrEmpty(path) ? "/" : path.StartsWith("/", StringComparison.Ordinal) ? path : "/" + path; }
        static GameObject FindByPath(GameObject[] roots, string path) { var parts = NormalizeObjectPath(path).Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries); foreach (var root in roots) { if (parts.Length == 0 || root.name != parts[0]) continue; var current = root; for (var i = 1; i < parts.Length && current != null; i++) current = FindChild(current, parts[i]); if (current != null) return current; } return null; }
        static GameObject FindChild(GameObject parent, string name) { for (var i = 0; i < parent.transform.childCount; i++) { var child = parent.transform.GetChild(i).gameObject; if (child.name == name) return child; } return null; }
        static bool TryExtractFloatField(string json, string field, out float value) { value = 0f; var marker = "\"" + field + "\""; var at = json.IndexOf(marker, StringComparison.Ordinal); if (at < 0) return false; var colon = json.IndexOf(':', at + marker.Length); if (colon < 0) return false; var end = colon + 1; while (end < json.Length && char.IsWhiteSpace(json[end])) end++; var start = end; while (end < json.Length && "-+.0123456789eE".IndexOf(json[end]) >= 0) end++; return float.TryParse(json.Substring(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out value); }
        static bool TryExtractVector2(string json, string field, out Vector2 v) { v = Vector2.zero; var n = ExtractFloatArray(json, field, 2); if (n.Count != 2) return false; v = new Vector2(n[0], n[1]); return true; }
        static bool TryExtractVector4(string json, string field, out Vector4 v) { v = Vector4.zero; var n = ExtractFloatArray(json, field, 4); if (n.Count != 4) return false; v = new Vector4(n[0], n[1], n[2], n[3]); return true; }
        static List<float> ExtractFloatArray(string json, string field, int max) { var values = new List<float>(); var at = json.IndexOf("\"" + field + "\"", StringComparison.Ordinal); if (at < 0) return values; var start = json.IndexOf('[', at); var end = start < 0 ? -1 : json.IndexOf(']', start); if (end < 0) return values; var token = new StringBuilder(); for (var i = start + 1; i < end && values.Count < max; i++) { var ch = json[i]; if ("-+.0123456789eE".IndexOf(ch) >= 0) token.Append(ch); else FlushFloat(token, values, max); } FlushFloat(token, values, max); return values; }
        static void FlushFloat(StringBuilder token, List<float> values, int max) { if (token.Length == 0 || values.Count >= max) { token.Length = 0; return; } if (float.TryParse(token.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) values.Add(f); token.Length = 0; }
        static string Vector2Json(Vector2 v) { return "[" + Number(v.x) + "," + Number(v.y) + "]"; }
        static string ColorJson(Color c) { return "[" + Number(c.r) + "," + Number(c.g) + "," + Number(c.b) + "," + Number(c.a) + "]"; }
        static string ObjectReferenceJson(UnityEngine.Object value) { if (value == null) return "null"; var path = AssetDatabase.GetAssetPath(value); return "{\"name\":" + JsonUtil.String(value.name) + ",\"type\":" + JsonUtil.String(value.GetType().FullName ?? value.GetType().Name) + ",\"asset_path\":" + JsonUtil.String(IsUnityPath(path) ? path : string.Empty) + "}"; }
        static bool IsUnityPath(string path) { return !string.IsNullOrEmpty(path) && (path.StartsWith("Assets/", StringComparison.Ordinal) || path.StartsWith("Packages/", StringComparison.Ordinal)); }
        static string Number(float value) { return value.ToString("G9", CultureInfo.InvariantCulture); }
        static string StringArrayJson(List<string> values) { var builder = new StringBuilder(); builder.Append('['); for (var index = 0; index < values.Count; index++) { if (index > 0) builder.Append(','); builder.Append(JsonUtil.String(values[index])); } builder.Append(']'); return builder.ToString(); }
        static string ErrorJson(CommandExecutionResult result) { return "{\"code\":" + JsonUtil.String(result.ErrorCode) + ",\"message\":" + JsonUtil.String(result.ErrorMessage) + (string.IsNullOrEmpty(result.ErrorDetailsJson) ? string.Empty : ",\"details\":" + result.ErrorDetailsJson) + "}"; }
        static string Sha256Prefixed(string value) { using (var sha = SHA256.Create()) return "sha256:" + BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-", string.Empty).ToLowerInvariant(); }

        static string ExtractArray(string json, string property)
        {
            var colon = FindPropertyColon(json, property);
            if (colon < 0) return string.Empty;
            var start = IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0 || json[start] != '[') return string.Empty;
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = start; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped) { escaped = false; continue; }
                if (character == '\\') { escaped = true; continue; }
                if (character == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (character == '[') depth++;
                else if (character == ']') { depth--; if (depth == 0) return json.Substring(start, index - start + 1); }
            }
            return string.Empty;
        }

        static bool TryExtractObjectArrayItems(string arrayJson, List<string> items)
        {
            var start = IndexOfNextNonWhitespace(arrayJson, 0);
            if (start < 0 || arrayJson[start] != '[') return false;
            var depth = 0;
            var objectStart = -1;
            var inString = false;
            var escaped = false;
            for (var index = start + 1; index < arrayJson.Length; index++)
            {
                var character = arrayJson[index];
                if (escaped) { escaped = false; continue; }
                if (character == '\\') { escaped = true; continue; }
                if (character == '"') { inString = !inString; continue; }
                if (inString) continue;
                if (character == '{') { if (depth == 0) objectStart = index; depth++; }
                else if (character == '}') { depth--; if (depth < 0) return false; if (depth == 0 && objectStart >= 0) { items.Add(arrayJson.Substring(objectStart, index - objectStart + 1)); objectStart = -1; } }
                else if (character == ']' && depth == 0) return true;
            }
            return false;
        }

        static string TrimObjectBody(string objectJson)
        {
            var value = (objectJson ?? string.Empty).Trim();
            if (value.Length < 2 || value[0] != '{' || value[value.Length - 1] != '}') return string.Empty;
            return value.Substring(1, value.Length - 2).Trim();
        }

        static int FindPropertyColon(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property)) return -1;
            var pattern = "\"" + property + "\"";
            var propertyIndex = json.IndexOf(pattern, StringComparison.Ordinal);
            return propertyIndex < 0 ? -1 : json.IndexOf(':', propertyIndex + pattern.Length);
        }

        static int IndexOfNextNonWhitespace(string value, int start)
        {
            for (var index = start; index < value.Length; index++) if (!char.IsWhiteSpace(value[index])) return index;
            return -1;
        }

        sealed class TextSetRequest
        {
            public bool HasText;
            public string Text = string.Empty;
            public bool HasFontSize;
            public float FontSize;
            public bool HasColor;
            public Vector4 Color;
            public bool HasRaycastTarget;
            public bool RaycastTarget;
            public bool HasHorizontalAlignment;
            public string HorizontalAlignment = string.Empty;
            public bool HasVerticalAlignment;
            public string VerticalAlignment = string.Empty;
            public bool HasAnyField { get { return HasText || HasFontSize || HasColor || HasRaycastTarget || HasHorizontalAlignment || HasVerticalAlignment; } }
        }

        sealed class TextState
        {
            public string Text = string.Empty;
            public float FontSize;
            public Color Color = Color.white;
            public bool RaycastTarget;
            public string Alignment = string.Empty;
            public string HorizontalAlignment = "center";
            public string VerticalAlignment = "middle";

            public TextState Clone()
            {
                return new TextState { Text = Text, FontSize = FontSize, Color = Color, RaycastTarget = RaycastTarget, Alignment = Alignment, HorizontalAlignment = HorizontalAlignment, VerticalAlignment = VerticalAlignment };
            }
        }

        sealed class ApplySpecStep
        {
            public ApplySpecStep(string command, string argsJson)
            {
                Command = command;
                ArgsJson = argsJson;
            }

            public string Command { get; private set; }
            public string ArgsJson { get; private set; }
        }

        delegate CommandExecutionResult ComponentMutationBuilder(string argsJson, Scene scene, Component component, List<ComponentMutation> mutations);

        sealed class ComponentMutation
        {
            public ComponentMutation(string name, string currentJson, string requestedJson, Action apply)
            {
                Name = name;
                CurrentJson = string.IsNullOrEmpty(currentJson) ? "null" : currentJson;
                RequestedJson = string.IsNullOrEmpty(requestedJson) ? "null" : requestedJson;
                Apply = apply;
            }

            public string Name { get; private set; }
            public string CurrentJson { get; private set; }
            public string RequestedJson { get; private set; }
            public Action Apply { get; private set; }
        }
    }
}
