using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class GameplayDataInspectionCommands
    {
        const string FeatureFlag = "gameplay_data_readonly";
        const string ScriptableObjectInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"guid\":{\"type\":\"string\",\"maxLength\":64},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"include_references\":{\"type\":\"boolean\"},\"include_managed_references\":{\"type\":\"boolean\"},\"include_asset_dependencies\":{\"type\":\"boolean\"},\"max_properties\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_array_elements\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500},\"max_string_chars\":{\"type\":\"integer\",\"minimum\":16,\"maximum\":4096},\"max_references\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500},\"max_dependencies\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string ScriptableObjectInspectOutputLimitJson = "{\"max_properties\":1000,\"max_depth\":16,\"max_array_elements\":500,\"max_string_chars\":4096,\"max_references\":500,\"max_dependencies\":500,\"readonly\":true}";
        const string ScriptableObjectSchemaSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"guid\":{\"type\":\"string\",\"maxLength\":64},\"property_path\":{\"type\":\"string\",\"maxLength\":512},\"include_enum_names\":{\"type\":\"boolean\"},\"include_managed_references\":{\"type\":\"boolean\"},\"max_properties\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":2000},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_array_elements\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500},\"max_enum_names\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string ScriptableObjectSchemaOutputLimitJson = "{\"max_properties\":2000,\"max_depth\":16,\"max_array_elements\":500,\"max_enum_names\":500,\"readonly\":true}";
        const string AnimationClipInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"guid\":{\"type\":\"string\",\"maxLength\":64},\"clip_name\":{\"type\":\"string\",\"maxLength\":256},\"include_curves\":{\"type\":\"boolean\"},\"include_object_reference_curves\":{\"type\":\"boolean\"},\"include_events\":{\"type\":\"boolean\"},\"include_asset_dependencies\":{\"type\":\"boolean\"},\"max_curve_bindings\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"max_keyframes_per_curve\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":250},\"max_object_reference_bindings\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"max_object_reference_keyframes_per_binding\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":250},\"max_events\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500},\"max_dependencies\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500},\"max_string_chars\":{\"type\":\"integer\",\"minimum\":16,\"maximum\":4096}},\"additionalProperties\":false}";
        const string AnimationClipInspectOutputLimitJson = "{\"max_curve_bindings\":1000,\"max_keyframes_per_curve\":250,\"max_object_reference_bindings\":1000,\"max_object_reference_keyframes_per_binding\":250,\"max_events\":500,\"max_dependencies\":500,\"max_string_chars\":4096,\"readonly\":true}";
        const string AnimationControllerInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"guid\":{\"type\":\"string\",\"maxLength\":64},\"include_parameters\":{\"type\":\"boolean\"},\"include_layers\":{\"type\":\"boolean\"},\"include_states\":{\"type\":\"boolean\"},\"include_transitions\":{\"type\":\"boolean\"},\"include_motions\":{\"type\":\"boolean\"},\"include_asset_dependencies\":{\"type\":\"boolean\"},\"max_parameters\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500},\"max_layers\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"max_states\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2000},\"max_transitions\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2000},\"max_conditions\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2000},\"max_state_behaviours\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_dependencies\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string AnimationControllerInspectOutputLimitJson = "{\"max_parameters\":500,\"max_layers\":100,\"max_states\":2000,\"max_transitions\":2000,\"max_conditions\":2000,\"max_state_behaviours\":1000,\"max_depth\":16,\"max_dependencies\":500,\"readonly\":true}";
        const string AnimationControllerGraphSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"guid\":{\"type\":\"string\",\"maxLength\":64},\"include_motion_nodes\":{\"type\":\"boolean\"},\"include_parameter_nodes\":{\"type\":\"boolean\"},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_edges\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":10000},\"max_layers\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_conditions_per_transition\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":100}},\"additionalProperties\":false}";
        const string AnimationControllerGraphOutputLimitJson = "{\"max_nodes\":5000,\"max_edges\":10000,\"max_layers\":100,\"max_depth\":16,\"max_conditions_per_transition\":100,\"readonly\":true}";
        const string AnimationBindingsReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_clips\":{\"type\":\"boolean\"},\"include_controllers\":{\"type\":\"boolean\"},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_clip_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"max_binding_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":5000},\"max_controller_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"max_motion_reference_samples\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":5000},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16}},\"additionalProperties\":false}";
        const string AnimationBindingsReportOutputLimitJson = "{\"max_scan_assets\":100000,\"max_clip_samples\":1000,\"max_binding_samples\":5000,\"max_controller_samples\":1000,\"max_motion_reference_samples\":5000,\"max_depth\":16,\"readonly\":true}";
        const string TimelineAssetInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"asset_path\":{\"type\":\"string\",\"maxLength\":512},\"guid\":{\"type\":\"string\",\"maxLength\":64},\"include_tracks\":{\"type\":\"boolean\"},\"include_clips\":{\"type\":\"boolean\"},\"include_asset_dependencies\":{\"type\":\"boolean\"},\"max_tracks\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":2000},\"max_clips\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":5000},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_string_chars\":{\"type\":\"integer\",\"minimum\":16,\"maximum\":4096},\"max_dependencies\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string TimelineAssetInspectOutputLimitJson = "{\"max_tracks\":2000,\"max_clips\":5000,\"max_depth\":16,\"max_string_chars\":4096,\"max_dependencies\":500,\"readonly\":true}";
        const string GameplayDataReferencesReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_managed_references\":{\"type\":\"boolean\"},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_sources\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_references_per_source\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_array_elements\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string GameplayDataReferencesReportOutputLimitJson = "{\"max_scan_assets\":100000,\"max_sources\":5000,\"max_references_per_source\":1000,\"max_depth\":16,\"max_array_elements\":500,\"readonly\":true}";
        const string GameplayDataValidationReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"path_prefix\":{\"type\":\"string\",\"maxLength\":512},\"include_packages\":{\"type\":\"boolean\"},\"include_managed_references\":{\"type\":\"boolean\"},\"max_scan_assets\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100000},\"max_sources\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"max_issues_per_source\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":1000},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":16},\"max_array_elements\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":500}},\"additionalProperties\":false}";
        const string GameplayDataValidationReportOutputLimitJson = "{\"max_scan_assets\":100000,\"max_sources\":5000,\"max_issues_per_source\":1000,\"max_depth\":16,\"max_array_elements\":500,\"readonly\":true}";
        const int MaxProperties = 1000;
        const int MaxSchemaProperties = 2000;
        const int MaxDepth = 16;
        const int MaxArrayElements = 500;
        const int MaxStringChars = 4096;
        const int MaxReferences = 500;
        const int MaxDependencies = 500;
        const int MaxCurveBindings = 1000;
        const int MaxKeyframesPerCurve = 250;
        const int MaxObjectReferenceBindings = 1000;
        const int MaxObjectReferenceKeyframesPerBinding = 250;
        const int MaxAnimationEvents = 500;
        const int MaxControllerParameters = 500;
        const int MaxControllerLayers = 100;
        const int MaxControllerStates = 2000;
        const int MaxControllerTransitions = 2000;
        const int MaxControllerConditions = 2000;
        const int MaxControllerStateBehaviours = 1000;
        const int MaxControllerGraphNodes = 5000;
        const int MaxControllerGraphEdges = 10000;
        const int MaxConditionsPerTransition = 100;
        const int MaxScanAssets = 100000;
        const int MaxClipSamples = 1000;
        const int MaxBindingSamples = 5000;
        const int MaxControllerSamples = 1000;
        const int MaxMotionReferenceSamples = 5000;
        const int MaxTimelineTracks = 2000;
        const int MaxTimelineClips = 5000;
        const int MaxReferenceSources = 5000;
        const int MaxReferencesPerSource = 1000;
        const int MaxValidationSources = 5000;
        const int MaxIssuesPerSource = 1000;

        [McpCommand(
            "scriptable_object.inspect",
            Title = "Inspect ScriptableObject",
            Description = "Read serialized ScriptableObject properties with stable property selectors, object references, and SerializeReference status without mutating gameplay data",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ScriptableObjectInspectSchemaJson,
            Tags = new string[] { "gameplay-data", "scriptable-object", "serialized-property", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = ScriptableObjectInspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ScriptableObjectInspect(McpCommandContext context)
        {
            var targetResult = ResolveTargetAsset(context.ArgsJson, out var assetPath, out var guid, out var asset);
            if (targetResult != null)
            {
                return targetResult;
            }

            if (!(asset is ScriptableObject scriptableObject))
            {
                return CommandExecutionResult.Error("invalid_target_type", "asset_path or guid must reference a ScriptableObject asset");
            }

            var propertyPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "property_path"), 512).Trim();
            if (PropertyPathLooksUnsafe(propertyPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "property_path must be a SerializedProperty path without path separators or URI syntax");
            }

            var includeReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_references", true);
            var includeManagedReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_managed_references", true);
            var includeAssetDependencies = JsonUtil.ExtractBool(context.ArgsJson, "include_asset_dependencies", false);
            var maxProperties = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_properties", 200), 1, MaxProperties);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 6), 0, MaxDepth);
            var maxArrayElements = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_array_elements", 50), 0, MaxArrayElements);
            var maxStringChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_string_chars", 512), 16, MaxStringChars);
            var maxReferences = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_references", 100), 0, MaxReferences);
            var maxDependencies = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_dependencies", 100), 0, MaxDependencies);

            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(scriptableObject);
                serializedObject.Update();
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("serialization_unavailable", "ScriptableObject could not be inspected through SerializedObject: " + exception.GetType().Name);
            }

            var references = new ReferenceCollector(maxReferences);
            var managedReferenceFindings = new ManagedReferenceFindings();
            var propertiesJson = SerializedPropertiesJson(serializedObject, propertyPath, includeReferences, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxStringChars, references, managedReferenceFindings, out var propertyCount, out var propertiesTruncated, out var propertyError);
            if (!string.IsNullOrEmpty(propertyError))
            {
                return CommandExecutionResult.Error("property_not_found", propertyError);
            }

            var scriptAsset = MonoScript.FromScriptableObject(scriptableObject);
            var dependencyCount = 0;
            var dependenciesTruncated = false;
            var dependenciesJson = "[]";
            if (includeAssetDependencies)
            {
                dependenciesJson = AssetDependenciesJson(assetPath, maxDependencies, out dependencyCount, out dependenciesTruncated);
            }

            var response = "{\"asset\":" + AssetJson(assetPath, guid, scriptableObject)
                + ",\"script_asset\":" + ObjectReferenceJson(scriptAsset)
                + ",\"property_path\":" + JsonUtil.String(propertyPath)
                + ",\"include_references\":" + JsonUtil.Bool(includeReferences)
                + ",\"include_managed_references\":" + JsonUtil.Bool(includeManagedReferences)
                + ",\"include_asset_dependencies\":" + JsonUtil.Bool(includeAssetDependencies)
                + ",\"max_properties\":" + maxProperties
                + ",\"max_depth\":" + maxDepth
                + ",\"max_array_elements\":" + maxArrayElements
                + ",\"max_string_chars\":" + maxStringChars
                + ",\"property_count\":" + propertyCount
                + ",\"properties_truncated\":" + JsonUtil.Bool(propertiesTruncated)
                + ",\"reference_count\":" + references.TotalCount
                + ",\"references_truncated\":" + JsonUtil.Bool(references.Truncated)
                + ",\"references\":[" + references.Json + "]"
                + ",\"managed_reference_count\":" + managedReferenceFindings.TotalCount
                + ",\"managed_reference_missing_or_unresolved_count\":" + managedReferenceFindings.MissingOrUnresolvedCount
                + ",\"managed_reference_findings\":[" + managedReferenceFindings.Json + "]"
                + ",\"dependency_count\":" + dependencyCount
                + ",\"dependencies_truncated\":" + JsonUtil.Bool(dependenciesTruncated)
                + ",\"asset_dependencies\":[" + dependenciesJson + "]"
                + ",\"selectors_stable\":\"SerializedProperty.propertyPath\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This command is read-only and does not call ApplyModifiedProperties, SetDirty, import, refresh, save, or arbitrary reflection setters.\",\"Use returned property_path selectors as stable read selectors; custom gameplay data writes remain deferred until schemas, checkpoints, preconditions and allowlists are proven.\",\"ManagedReference missing-type detection is limited to what Unity exposes through SerializedProperty metadata.\"]"
                + ",\"next_valid_actions\":[\"asset.dependencies\",\"asset.references\",\"asset.risk_report\",\"dependency.query\",\"serializereference.usages.find\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "scriptable_object.schema",
            Title = "Inspect ScriptableObject Schema",
            Description = "Read ScriptableObject serialized property schema selectors and value shapes without returning gameplay data values or mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ScriptableObjectSchemaSchemaJson,
            Tags = new string[] { "gameplay-data", "scriptable-object", "schema", "serialized-property", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "schema",
            OutputLimitJson = ScriptableObjectSchemaOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult ScriptableObjectSchema(McpCommandContext context)
        {
            var targetResult = ResolveTargetAsset(context.ArgsJson, out var assetPath, out var guid, out var asset);
            if (targetResult != null)
            {
                return targetResult;
            }

            if (!(asset is ScriptableObject scriptableObject))
            {
                return CommandExecutionResult.Error("invalid_target_type", "asset_path or guid must reference a ScriptableObject asset");
            }

            var propertyPath = Bounded(JsonUtil.ExtractString(context.ArgsJson, "property_path"), 512).Trim();
            if (PropertyPathLooksUnsafe(propertyPath))
            {
                return CommandExecutionResult.Error("invalid_field_value", "property_path must be a SerializedProperty path without path separators or URI syntax");
            }

            var includeEnumNames = JsonUtil.ExtractBool(context.ArgsJson, "include_enum_names", true);
            var includeManagedReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_managed_references", true);
            var maxProperties = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_properties", 500), 1, MaxSchemaProperties);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 8), 0, MaxDepth);
            var maxArrayElements = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_array_elements", 50), 0, MaxArrayElements);
            var maxEnumNames = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_enum_names", 100), 0, 500);

            SerializedObject serializedObject;
            try
            {
                serializedObject = new SerializedObject(scriptableObject);
                serializedObject.Update();
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("serialization_unavailable", "ScriptableObject schema could not be inspected through SerializedObject: " + exception.GetType().Name);
            }

            var propertiesJson = SerializedSchemaJson(serializedObject, propertyPath, includeEnumNames, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxEnumNames, out var propertyCount, out var propertiesTruncated, out var propertyError);
            if (!string.IsNullOrEmpty(propertyError))
            {
                return CommandExecutionResult.Error("property_not_found", propertyError);
            }

            var response = "{\"asset\":" + AssetJson(assetPath, guid, scriptableObject)
                + ",\"property_path\":" + JsonUtil.String(propertyPath)
                + ",\"include_enum_names\":" + JsonUtil.Bool(includeEnumNames)
                + ",\"include_managed_references\":" + JsonUtil.Bool(includeManagedReferences)
                + ",\"property_count\":" + propertyCount
                + ",\"properties_truncated\":" + JsonUtil.Bool(propertiesTruncated)
                + ",\"properties\":[" + propertiesJson + "]"
                + ",\"selectors_stable\":\"SerializedProperty.propertyPath\""
                + ",\"values_returned\":false"
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This command is read-only and returns property schema/value-shape metadata, not gameplay data values.\",\"No ApplyModifiedProperties, SetDirty, import, refresh, save, file writes, or arbitrary reflection setters are exposed.\"]"
                + ",\"next_valid_actions\":[\"scriptable_object.inspect\",\"serializereference.types.used\",\"serializereference.usages.find\",\"asset.dependencies\",\"asset.references\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "animation.clip.inspect",
            Title = "Inspect Animation Clip",
            Description = "Read AnimationClip curve bindings, object-reference curves, and events with stable selectors without mutating animation assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = AnimationClipInspectSchemaJson,
            Tags = new string[] { "gameplay-data", "animation", "animation-clip", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = AnimationClipInspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AnimationClipInspect(McpCommandContext context)
        {
            var targetResult = ResolveAnimationClip(context.ArgsJson, out var assetPath, out var guid, out var clip, out var clipCount, out var selectionMode);
            if (targetResult != null)
            {
                return targetResult;
            }

            var includeCurves = JsonUtil.ExtractBool(context.ArgsJson, "include_curves", true);
            var includeObjectReferenceCurves = JsonUtil.ExtractBool(context.ArgsJson, "include_object_reference_curves", true);
            var includeEvents = JsonUtil.ExtractBool(context.ArgsJson, "include_events", true);
            var includeAssetDependencies = JsonUtil.ExtractBool(context.ArgsJson, "include_asset_dependencies", false);
            var maxCurveBindings = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_curve_bindings", 200), 0, MaxCurveBindings);
            var maxKeyframesPerCurve = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_keyframes_per_curve", 40), 0, MaxKeyframesPerCurve);
            var maxObjectReferenceBindings = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_object_reference_bindings", 200), 0, MaxObjectReferenceBindings);
            var maxObjectReferenceKeyframesPerBinding = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_object_reference_keyframes_per_binding", 40), 0, MaxObjectReferenceKeyframesPerBinding);
            var maxEvents = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_events", 100), 0, MaxAnimationEvents);
            var maxDependencies = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_dependencies", 100), 0, MaxDependencies);
            var maxStringChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_string_chars", 512), 16, MaxStringChars);

            var curveBindingCount = 0;
            var curvesTruncated = false;
            var curvesJson = string.Empty;
            if (includeCurves)
            {
                curvesJson = CurveBindingsJson(clip, maxCurveBindings, maxKeyframesPerCurve, out curveBindingCount, out curvesTruncated);
            }
            else
            {
                curveBindingCount = 0;
                curvesTruncated = false;
            }

            var objectReferenceBindingCount = 0;
            var objectReferenceCurvesTruncated = false;
            var nullObjectReferenceKeyframeCount = 0;
            var objectReferenceCurvesJson = string.Empty;
            if (includeObjectReferenceCurves)
            {
                objectReferenceCurvesJson = ObjectReferenceCurveBindingsJson(clip, maxObjectReferenceBindings, maxObjectReferenceKeyframesPerBinding, out objectReferenceBindingCount, out objectReferenceCurvesTruncated, out nullObjectReferenceKeyframeCount);
            }
            else
            {
                objectReferenceBindingCount = 0;
                objectReferenceCurvesTruncated = false;
                nullObjectReferenceKeyframeCount = 0;
            }

            var eventCount = 0;
            var eventsTruncated = false;
            var eventsJson = string.Empty;
            if (includeEvents)
            {
                eventsJson = AnimationEventsJson(clip, maxEvents, maxStringChars, out eventCount, out eventsTruncated);
            }
            else
            {
                eventCount = 0;
                eventsTruncated = false;
            }

            var dependencyCount = 0;
            var dependenciesTruncated = false;
            var dependenciesJson = string.Empty;
            if (includeAssetDependencies)
            {
                dependenciesJson = AssetDependenciesJson(assetPath, maxDependencies, out dependencyCount, out dependenciesTruncated);
            }
            else
            {
                dependencyCount = 0;
                dependenciesTruncated = false;
            }

            var response = "{\"asset\":" + AssetJson(assetPath, guid, clip)
                + ",\"selection\":{\"clip_count\":" + clipCount
                + ",\"selection_mode\":" + JsonUtil.String(selectionMode)
                + ",\"clip_name\":" + JsonUtil.String(clip.name) + "}"
                + ",\"clip\":{\"name\":" + JsonUtil.String(clip.name)
                + ",\"length\":" + FloatJson(clip.length)
                + ",\"frame_rate\":" + FloatJson(clip.frameRate)
                + ",\"legacy\":" + JsonUtil.Bool(clip.legacy)
                + ",\"empty\":" + JsonUtil.Bool(clip.empty) + "}"
                + ",\"include_curves\":" + JsonUtil.Bool(includeCurves)
                + ",\"include_object_reference_curves\":" + JsonUtil.Bool(includeObjectReferenceCurves)
                + ",\"include_events\":" + JsonUtil.Bool(includeEvents)
                + ",\"include_asset_dependencies\":" + JsonUtil.Bool(includeAssetDependencies)
                + ",\"max_curve_bindings\":" + maxCurveBindings
                + ",\"max_keyframes_per_curve\":" + maxKeyframesPerCurve
                + ",\"curve_binding_count\":" + curveBindingCount
                + ",\"curves_truncated\":" + JsonUtil.Bool(curvesTruncated)
                + ",\"curves\":[" + curvesJson + "]"
                + ",\"max_object_reference_bindings\":" + maxObjectReferenceBindings
                + ",\"max_object_reference_keyframes_per_binding\":" + maxObjectReferenceKeyframesPerBinding
                + ",\"object_reference_binding_count\":" + objectReferenceBindingCount
                + ",\"object_reference_curves_truncated\":" + JsonUtil.Bool(objectReferenceCurvesTruncated)
                + ",\"null_object_reference_keyframe_count\":" + nullObjectReferenceKeyframeCount
                + ",\"object_reference_curves\":[" + objectReferenceCurvesJson + "]"
                + ",\"max_events\":" + maxEvents
                + ",\"event_count\":" + eventCount
                + ",\"events_truncated\":" + JsonUtil.Bool(eventsTruncated)
                + ",\"events\":[" + eventsJson + "]"
                + ",\"dependency_count\":" + dependencyCount
                + ",\"dependencies_truncated\":" + JsonUtil.Bool(dependenciesTruncated)
                + ",\"asset_dependencies\":[" + dependenciesJson + "]"
                + ",\"selectors_stable\":\"AnimationUtility.EditorCurveBinding(path,type,propertyName); AnimationEvent index/time/functionName\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This command is read-only and does not import, refresh, save, write, reserialize, or mutate AnimationClip assets.\",\"Use returned binding selectors as stable read selectors; animation writes remain deferred until schemas, checkpoints, preconditions and allowlists are proven.\",\"Null object-reference curve keyframes are reported as null references; Unity's AnimationUtility API may not expose missing-object instance ids for those keyframes.\"]"
                + ",\"next_valid_actions\":[\"asset.dependencies\",\"asset.references\",\"asset.risk_report\",\"dependency.query\",\"scriptable_object.inspect\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "animation.controller.inspect",
            Title = "Inspect Animator Controller",
            Description = "Read AnimatorController parameters, layers, states, motion references, and transition selectors without mutating controller assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = AnimationControllerInspectSchemaJson,
            Tags = new string[] { "gameplay-data", "animation", "animator-controller", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = AnimationControllerInspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AnimationControllerInspect(McpCommandContext context)
        {
            var targetResult = ResolveAnimatorController(context.ArgsJson, out var assetPath, out var guid, out var controller);
            if (targetResult != null)
            {
                return targetResult;
            }

            var includeParameters = JsonUtil.ExtractBool(context.ArgsJson, "include_parameters", true);
            var includeLayers = JsonUtil.ExtractBool(context.ArgsJson, "include_layers", true);
            var includeStates = JsonUtil.ExtractBool(context.ArgsJson, "include_states", true);
            var includeTransitions = JsonUtil.ExtractBool(context.ArgsJson, "include_transitions", true);
            var includeMotions = JsonUtil.ExtractBool(context.ArgsJson, "include_motions", true);
            var includeAssetDependencies = JsonUtil.ExtractBool(context.ArgsJson, "include_asset_dependencies", false);
            var maxParameters = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_parameters", 200), 0, MaxControllerParameters);
            var maxLayers = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_layers", 50), 0, MaxControllerLayers);
            var maxStates = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_states", 500), 0, MaxControllerStates);
            var maxTransitions = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_transitions", 500), 0, MaxControllerTransitions);
            var maxConditions = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_conditions", 500), 0, MaxControllerConditions);
            var maxStateBehaviours = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_state_behaviours", 200), 0, MaxControllerStateBehaviours);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 8), 0, MaxDepth);
            var maxDependencies = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_dependencies", 100), 0, MaxDependencies);

            var parameterCount = 0;
            var parametersTruncated = false;
            var parametersJson = string.Empty;
            if (includeParameters)
            {
                parametersJson = AnimatorParametersJson(controller, maxParameters, out parameterCount, out parametersTruncated);
            }
            else
            {
                parameterCount = 0;
                parametersTruncated = false;
            }

            var inspector = new AnimatorControllerInspector(maxLayers, maxStates, maxTransitions, maxConditions, maxStateBehaviours, maxDepth, includeLayers, includeStates, includeTransitions, includeMotions);
            inspector.Inspect(controller);

            var dependenciesJson = string.Empty;
            var dependencyCount = 0;
            var dependenciesTruncated = false;
            if (includeAssetDependencies)
            {
                dependenciesJson = AssetDependenciesJson(assetPath, maxDependencies, out dependencyCount, out dependenciesTruncated);
            }

            var response = "{\"asset\":" + AssetJson(assetPath, guid, controller)
                + ",\"controller\":{\"name\":" + JsonUtil.String(controller.name)
                + ",\"parameter_count\":" + (controller.parameters == null ? 0 : controller.parameters.Length)
                + ",\"layer_count\":" + (controller.layers == null ? 0 : controller.layers.Length) + "}"
                + ",\"include_parameters\":" + JsonUtil.Bool(includeParameters)
                + ",\"include_layers\":" + JsonUtil.Bool(includeLayers)
                + ",\"include_states\":" + JsonUtil.Bool(includeStates)
                + ",\"include_transitions\":" + JsonUtil.Bool(includeTransitions)
                + ",\"include_motions\":" + JsonUtil.Bool(includeMotions)
                + ",\"include_asset_dependencies\":" + JsonUtil.Bool(includeAssetDependencies)
                + ",\"parameter_count\":" + parameterCount
                + ",\"parameters_truncated\":" + JsonUtil.Bool(parametersTruncated)
                + ",\"parameters\":[" + parametersJson + "]"
                + ",\"layer_count\":" + inspector.LayerCount
                + ",\"layers_truncated\":" + JsonUtil.Bool(inspector.LayersTruncated)
                + ",\"layers\":[" + inspector.LayersJson + "]"
                + ",\"state_count\":" + inspector.StateCount
                + ",\"states_truncated\":" + JsonUtil.Bool(inspector.StatesTruncated)
                + ",\"states\":[" + inspector.StatesJson + "]"
                + ",\"transition_count\":" + inspector.TransitionCount
                + ",\"transitions_truncated\":" + JsonUtil.Bool(inspector.TransitionsTruncated)
                + ",\"transitions\":[" + inspector.TransitionsJson + "]"
                + ",\"condition_count\":" + inspector.ConditionCount
                + ",\"conditions_truncated\":" + JsonUtil.Bool(inspector.ConditionsTruncated)
                + ",\"state_behaviour_count\":" + inspector.StateBehaviourCount
                + ",\"state_behaviours_truncated\":" + JsonUtil.Bool(inspector.StateBehavioursTruncated)
                + ",\"missing_state_behaviour_count\":" + inspector.MissingStateBehaviourCount
                + ",\"dependency_count\":" + dependencyCount
                + ",\"dependencies_truncated\":" + JsonUtil.Bool(dependenciesTruncated)
                + ",\"asset_dependencies\":[" + dependenciesJson + "]"
                + ",\"selectors_stable\":\"AnimatorController layer_index/layer_name plus state_path, transition_index, parameter_name\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This command is read-only and does not import, refresh, save, write, reserialize, or mutate AnimatorController assets.\",\"Use returned layer/state/transition/parameter selectors as stable read selectors; controller writes remain deferred until schemas, checkpoints, preconditions and allowlists are proven.\",\"Missing StateMachineBehaviour scripts are reported when Unity exposes null behaviour entries.\"]"
                + ",\"next_valid_actions\":[\"animation.clip.inspect\",\"asset.dependencies\",\"asset.references\",\"asset.risk_report\",\"dependency.query\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "animation.controller.graph",
            Title = "Graph Animator Controller",
            Description = "Read AnimatorController state machine graph nodes and transition edges with stable selectors without mutating controller assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = AnimationControllerGraphSchemaJson,
            Tags = new string[] { "gameplay-data", "animation", "animator-controller", "graph", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "graph",
            OutputLimitJson = AnimationControllerGraphOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AnimationControllerGraph(McpCommandContext context)
        {
            var targetResult = ResolveAnimatorController(context.ArgsJson, out var assetPath, out var guid, out var controller);
            if (targetResult != null)
            {
                return targetResult;
            }

            var includeMotionNodes = JsonUtil.ExtractBool(context.ArgsJson, "include_motion_nodes", true);
            var includeParameterNodes = JsonUtil.ExtractBool(context.ArgsJson, "include_parameter_nodes", true);
            var maxNodes = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_nodes", 1500), 1, MaxControllerGraphNodes);
            var maxEdges = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_edges", 3000), 1, MaxControllerGraphEdges);
            var maxLayers = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_layers", 50), 0, MaxControllerLayers);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 8), 0, MaxDepth);
            var maxConditionsPerTransition = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_conditions_per_transition", 20), 0, MaxConditionsPerTransition);

            var graph = new AnimatorControllerGraphBuilder(maxNodes, maxEdges, maxLayers, maxDepth, maxConditionsPerTransition, includeMotionNodes, includeParameterNodes);
            graph.Build(controller);

            var response = "{\"asset\":" + AssetJson(assetPath, guid, controller)
                + ",\"graph_mode\":\"animator_controller_state_machine\""
                + ",\"node_count\":" + graph.NodeCount
                + ",\"nodes_truncated\":" + JsonUtil.Bool(graph.NodesTruncated)
                + ",\"nodes\":[" + graph.NodesJson + "]"
                + ",\"edge_count\":" + graph.EdgeCount
                + ",\"edges_truncated\":" + JsonUtil.Bool(graph.EdgesTruncated)
                + ",\"edges\":[" + graph.EdgesJson + "]"
                + ",\"layer_count\":" + graph.LayerCount
                + ",\"layers_truncated\":" + JsonUtil.Bool(graph.LayersTruncated)
                + ",\"transition_count\":" + graph.TransitionCount
                + ",\"conditions_truncated\":" + JsonUtil.Bool(graph.ConditionsTruncated)
                + ",\"selectors_stable\":\"graph node ids are derived from layer_index, state_machine_path, state_path, parameter_name and motion asset reference\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This command is read-only and does not import, refresh, save, write, reserialize, create controllers, or mutate AnimatorController assets.\",\"Use graph node ids and edge selectors as stable read selectors; controller writes remain deferred until schemas, checkpoints, preconditions and allowlists are proven.\"]"
                + ",\"next_valid_actions\":[\"animation.controller.inspect\",\"animation.clip.inspect\",\"asset.dependencies\",\"asset.references\",\"asset.risk_report\",\"dependency.query\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "animation.bindings.report",
            Title = "Report Animation Bindings",
            Description = "Aggregate bounded AnimationClip bindings and AnimatorController motion references without mutating animation assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = AnimationBindingsReportSchemaJson,
            Tags = new string[] { "gameplay-data", "animation", "bindings", "report", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "report",
            OutputLimitJson = AnimationBindingsReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult AnimationBindingsReport(McpCommandContext context)
        {
            var prefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (prefixResult != null)
            {
                return prefixResult;
            }

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", false);
            var includeClips = JsonUtil.ExtractBool(context.ArgsJson, "include_clips", true);
            var includeControllers = JsonUtil.ExtractBool(context.ArgsJson, "include_controllers", true);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxScanAssets);
            var maxClipSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_clip_samples", 100), 0, MaxClipSamples);
            var maxBindingSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_binding_samples", 500), 0, MaxBindingSamples);
            var maxControllerSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_controller_samples", 100), 0, MaxControllerSamples);
            var maxMotionReferenceSamples = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_motion_reference_samples", 500), 0, MaxMotionReferenceSamples);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 8), 0, MaxDepth);

            var report = new AnimationBindingsReportState(maxClipSamples, maxBindingSamples, maxControllerSamples, maxMotionReferenceSamples, maxDepth);
            var searchRoots = SearchRoots(pathPrefix, includePackages);
            var scannedAssets = 0;
            var scanTruncated = false;

            if (includeClips)
            {
                var clipPaths = CandidateAssetsByType("AnimationClip", searchRoots, includePackages, pathPrefix, maxScanAssets, ref scannedAssets, ref scanTruncated);
                for (var index = 0; index < clipPaths.Count; index++)
                {
                    report.InspectClipAsset(clipPaths[index]);
                }
            }

            if (includeControllers && !scanTruncated)
            {
                var controllerPaths = CandidateAssetsByType("AnimatorController", searchRoots, includePackages, pathPrefix, maxScanAssets, ref scannedAssets, ref scanTruncated);
                for (var index = 0; index < controllerPaths.Count; index++)
                {
                    report.InspectControllerAsset(controllerPaths[index]);
                }
            }

            var response = "{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_clips\":" + JsonUtil.Bool(includeClips)
                + ",\"include_controllers\":" + JsonUtil.Bool(includeControllers)
                + ",\"scanned_asset_count\":" + scannedAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"clip_asset_count\":" + report.ClipAssetCount
                + ",\"clip_count\":" + report.ClipCount
                + ",\"curve_binding_count\":" + report.CurveBindingCount
                + ",\"object_reference_binding_count\":" + report.ObjectReferenceBindingCount
                + ",\"animation_event_count\":" + report.AnimationEventCount
                + ",\"clip_samples_truncated\":" + JsonUtil.Bool(report.ClipSamplesTruncated)
                + ",\"clip_samples\":[" + report.ClipSamplesJson + "]"
                + ",\"binding_samples_truncated\":" + JsonUtil.Bool(report.BindingSamplesTruncated)
                + ",\"binding_samples\":[" + report.BindingSamplesJson + "]"
                + ",\"controller_asset_count\":" + report.ControllerAssetCount
                + ",\"controller_state_count\":" + report.ControllerStateCount
                + ",\"controller_transition_count\":" + report.ControllerTransitionCount
                + ",\"motion_reference_count\":" + report.MotionReferenceCount
                + ",\"controller_samples_truncated\":" + JsonUtil.Bool(report.ControllerSamplesTruncated)
                + ",\"controller_samples\":[" + report.ControllerSamplesJson + "]"
                + ",\"motion_reference_samples_truncated\":" + JsonUtil.Bool(report.MotionReferenceSamplesTruncated)
                + ",\"motion_reference_samples\":[" + report.MotionReferenceSamplesJson + "]"
                + ",\"selectors_stable\":\"clip binding selectors use asset_path, clip_name, curve_kind, path, type, property_name; controller selectors use asset_path and state_path\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This report is read-only and does not open scenes, import, refresh, save, write, reserialize, create controllers, or mutate animation assets.\",\"Use focused animation.clip.inspect or animation.controller.inspect for full bounded details before any future write workflow.\"]"
                + ",\"next_valid_actions\":[\"animation.clip.inspect\",\"animation.controller.inspect\",\"animation.controller.graph\",\"asset.dependencies\",\"asset.references\",\"asset.risk_report\",\"dependency.query\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "timeline.asset.inspect",
            Title = "Inspect Timeline Asset",
            Description = "Read TimelineAsset tracks, clips, timing, and asset references without a compile-time Timeline dependency or asset mutation",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = TimelineAssetInspectSchemaJson,
            Tags = new string[] { "gameplay-data", "timeline", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "object_details",
            OutputLimitJson = TimelineAssetInspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TimelineAssetInspect(McpCommandContext context)
        {
            var targetResult = ResolveTargetAsset(context.ArgsJson, out var assetPath, out var guid, out var asset);
            if (targetResult != null)
            {
                return targetResult;
            }

            var assetType = asset.GetType();
            if (!string.Equals(assetType.FullName, "UnityEngine.Timeline.TimelineAsset", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_target_type", "asset_path or guid must reference a UnityEngine.Timeline.TimelineAsset asset");
            }

            var includeTracks = JsonUtil.ExtractBool(context.ArgsJson, "include_tracks", true);
            var includeClips = JsonUtil.ExtractBool(context.ArgsJson, "include_clips", true);
            var includeAssetDependencies = JsonUtil.ExtractBool(context.ArgsJson, "include_asset_dependencies", false);
            var maxTracks = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_tracks", 500), 0, MaxTimelineTracks);
            var maxClips = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_clips", 1000), 0, MaxTimelineClips);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 8), 0, MaxDepth);
            var maxStringChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_string_chars", 512), 16, MaxStringChars);
            var maxDependencies = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_dependencies", 100), 0, MaxDependencies);

            var timeline = new TimelineInspectionState(maxTracks, maxClips, maxDepth, maxStringChars, includeTracks, includeClips);
            timeline.Inspect(asset);

            var dependenciesJson = string.Empty;
            var dependencyCount = 0;
            var dependenciesTruncated = false;
            if (includeAssetDependencies)
            {
                dependenciesJson = AssetDependenciesJson(assetPath, maxDependencies, out dependencyCount, out dependenciesTruncated);
            }

            var response = "{\"asset\":" + AssetJson(assetPath, guid, asset)
                + ",\"timeline\":{\"name\":" + JsonUtil.String(asset.name)
                + ",\"duration\":" + ReflectionDoubleJson(asset, "duration")
                + ",\"fixed_duration\":" + ReflectionDoubleJson(asset, "fixedDuration")
                + ",\"track_count\":" + timeline.TrackCount
                + ",\"clip_count\":" + timeline.ClipCount + "}"
                + ",\"include_tracks\":" + JsonUtil.Bool(includeTracks)
                + ",\"include_clips\":" + JsonUtil.Bool(includeClips)
                + ",\"include_asset_dependencies\":" + JsonUtil.Bool(includeAssetDependencies)
                + ",\"track_count\":" + timeline.TrackCount
                + ",\"tracks_truncated\":" + JsonUtil.Bool(timeline.TracksTruncated)
                + ",\"tracks\":[" + timeline.TracksJson + "]"
                + ",\"clip_count\":" + timeline.ClipCount
                + ",\"clips_truncated\":" + JsonUtil.Bool(timeline.ClipsTruncated)
                + ",\"clips\":[" + timeline.ClipsJson + "]"
                + ",\"clip_asset_reference_count\":" + timeline.ClipAssetReferenceCount
                + ",\"dependency_count\":" + dependencyCount
                + ",\"dependencies_truncated\":" + JsonUtil.Bool(dependenciesTruncated)
                + ",\"asset_dependencies\":[" + dependenciesJson + "]"
                + ",\"selectors_stable\":\"timeline selectors use track_path, track_index and clip_index with reflected Timeline public metadata\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This command is read-only and does not import, refresh, save, write, reserialize, create Timeline assets, or mutate tracks/clips.\",\"Timeline package APIs are read at runtime without a compile-time dependency; unavailable optional public members are returned as null.\"]"
                + ",\"next_valid_actions\":[\"animation.clip.inspect\",\"animation.bindings.report\",\"asset.dependencies\",\"asset.references\",\"asset.risk_report\",\"dependency.query\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "gameplay.data.references.report",
            Title = "Report Gameplay Data References",
            Description = "Aggregate bounded ScriptableObject object references and SerializeReference metadata without mutating gameplay data",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = GameplayDataReferencesReportSchemaJson,
            Tags = new string[] { "gameplay-data", "scriptable-object", "references", "report", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "report",
            OutputLimitJson = GameplayDataReferencesReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameplayDataReferencesReport(McpCommandContext context)
        {
            var prefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (prefixResult != null)
            {
                return prefixResult;
            }

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", false);
            var includeManagedReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_managed_references", true);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxScanAssets);
            var maxSources = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_sources", 500), 1, MaxReferenceSources);
            var maxReferencesPerSource = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_references_per_source", 100), 0, MaxReferencesPerSource);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 8), 0, MaxDepth);
            var maxArrayElements = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_array_elements", 50), 0, MaxArrayElements);
            var scannedAssets = 0;
            var scanTruncated = false;
            var sourcePaths = CandidateAssetsByType("ScriptableObject", SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, maxScanAssets, ref scannedAssets, ref scanTruncated);
            var report = new GameplayDataReferencesReportState(maxSources, maxReferencesPerSource, maxDepth, maxArrayElements, includeManagedReferences);
            for (var index = 0; index < sourcePaths.Count; index++)
            {
                report.InspectSource(sourcePaths[index]);
            }

            var response = "{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_managed_references\":" + JsonUtil.Bool(includeManagedReferences)
                + ",\"scanned_asset_count\":" + scannedAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"source_count\":" + report.SourceCount
                + ",\"sources_truncated\":" + JsonUtil.Bool(report.SourcesTruncated)
                + ",\"object_reference_count\":" + report.ObjectReferenceCount
                + ",\"missing_object_reference_count\":" + report.MissingObjectReferenceCount
                + ",\"managed_reference_count\":" + report.ManagedReferenceCount
                + ",\"managed_reference_missing_or_unresolved_count\":" + report.ManagedReferenceMissingOrUnresolvedCount
                + ",\"sources\":[" + report.SourcesJson + "]"
                + ",\"selectors_stable\":\"source selectors use asset_path/guid; reference selectors use SerializedProperty.propertyPath\""
                + ",\"write_capabilities_exposed\":false"
                + ",\"warnings\":[\"This report is read-only and does not import, refresh, save, write, reserialize, or mutate gameplay data assets.\",\"Use scriptable_object.inspect for focused values and scriptable_object.schema for value-shape metadata before any future write workflow.\"]"
                + ",\"next_valid_actions\":[\"scriptable_object.inspect\",\"scriptable_object.schema\",\"serializereference.graph\",\"serializereference.usages.find\",\"asset.references\",\"asset.dependencies\"]}";
            return CommandExecutionResult.Ok(response);
        }

        [McpCommand(
            "gameplay.data.validation.report",
            Title = "Report Gameplay Data Validation Risks",
            Description = "Report bounded ScriptableObject validation risks without returning arbitrary gameplay data values or mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = GameplayDataValidationReportSchemaJson,
            Tags = new string[] { "gameplay-data", "scriptable-object", "validation", "report", "readonly" },
            Capability = FeatureFlag,
            ResultShape = "report",
            OutputLimitJson = GameplayDataValidationReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult GameplayDataValidationReport(McpCommandContext context)
        {
            var prefixResult = RequireAssetPrefix(context.ArgsJson, out var pathPrefix);
            if (prefixResult != null)
            {
                return prefixResult;
            }

            var includePackages = JsonUtil.ExtractBool(context.ArgsJson, "include_packages", false);
            var includeManagedReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_managed_references", true);
            var maxScanAssets = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_scan_assets", 10000), 1, MaxScanAssets);
            var maxSources = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_sources", 500), 1, MaxValidationSources);
            var maxIssuesPerSource = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_issues_per_source", 100), 0, MaxIssuesPerSource);
            var maxDepth = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_depth", 8), 0, MaxDepth);
            var maxArrayElements = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_array_elements", 50), 0, MaxArrayElements);
            var scannedAssets = 0;
            var scanTruncated = false;
            var sourcePaths = CandidateAssetsByType("ScriptableObject", SearchRoots(pathPrefix, includePackages), includePackages, pathPrefix, maxScanAssets, ref scannedAssets, ref scanTruncated);
            var report = new GameplayDataValidationReportState(maxSources, maxIssuesPerSource, maxDepth, maxArrayElements, includeManagedReferences);
            for (var index = 0; index < sourcePaths.Count; index++)
            {
                report.InspectSource(sourcePaths[index]);
            }

            var response = "{\"path_prefix\":" + JsonUtil.String(pathPrefix)
                + ",\"include_packages\":" + JsonUtil.Bool(includePackages)
                + ",\"include_managed_references\":" + JsonUtil.Bool(includeManagedReferences)
                + ",\"scanned_asset_count\":" + scannedAssets
                + ",\"scan_truncated\":" + JsonUtil.Bool(scanTruncated)
                + ",\"source_count\":" + report.SourceCount
                + ",\"sources_truncated\":" + JsonUtil.Bool(report.SourcesTruncated)
                + ",\"skipped_non_scriptable_object_count\":" + report.SkippedNonScriptableObjectCount
                + ",\"source_with_issues_count\":" + report.SourceWithIssuesCount
                + ",\"issue_count\":" + report.IssueCount
                + ",\"serialization_unavailable_count\":" + report.SerializationUnavailableCount
                + ",\"missing_script_asset_count\":" + report.MissingScriptAssetCount
                + ",\"object_reference_count\":" + report.ObjectReferenceCount
                + ",\"missing_object_reference_count\":" + report.MissingObjectReferenceCount
                + ",\"managed_reference_count\":" + report.ManagedReferenceCount
                + ",\"managed_reference_null_count\":" + report.ManagedReferenceNullCount
                + ",\"managed_reference_missing_or_unresolved_count\":" + report.ManagedReferenceMissingOrUnresolvedCount
                + ",\"traversal_truncated_source_count\":" + report.TraversalTruncatedSourceCount
                + ",\"sources\":[" + report.SourcesJson + "]"
                + ",\"selectors_stable\":\"source selectors use asset_path/guid; issue selectors use SerializedProperty.propertyPath\""
                + ",\"values_returned\":false"
                + ",\"write_capabilities_exposed\":false"
                + ",\"validation_started\":false"
                + ",\"warnings\":[\"This validation report is read-only and does not import, refresh, save, write, reserialize, or mutate gameplay data assets.\",\"Findings expose selectors and metadata only; arbitrary gameplay data values are not returned.\",\"ManagedReference missing-type detection is limited to what Unity exposes through SerializedProperty metadata.\"]"
                + ",\"next_valid_actions\":[\"scriptable_object.inspect\",\"scriptable_object.schema\",\"gameplay.data.references.report\",\"serializereference.graph\",\"serializereference.usages.find\",\"asset.references\",\"asset.dependencies\"]}";
            return CommandExecutionResult.Ok(response);
        }

        static CommandExecutionResult ResolveTargetAsset(string argsJson, out string assetPath, out string guid, out UnityEngine.Object asset)
        {
            asset = null;
            guid = Bounded(JsonUtil.ExtractString(argsJson, "guid"), 64).Trim();
            assetPath = Bounded(JsonUtil.ExtractString(argsJson, "asset_path"), 512).Trim();
            if (!string.IsNullOrEmpty(guid))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return CommandExecutionResult.Error("not_found", "guid does not resolve to an asset path");
                }
            }

            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandExecutionResult.Error("missing_required_field", "asset_path or guid is required");
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(assetPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must be a Unity-relative path under Assets/ or Packages/");
            }

            asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                return CommandExecutionResult.Error("not_found", "asset_path does not reference an existing asset");
            }

            guid = AssetDatabase.AssetPathToGUID(assetPath);
            return null;
        }

        static CommandExecutionResult RequireAssetPrefix(string argsJson, out string pathPrefix)
        {
            pathPrefix = JsonUtil.ExtractString(argsJson, "path_prefix");
            if (string.IsNullOrEmpty(pathPrefix))
            {
                pathPrefix = "Assets/";
                return null;
            }

            try
            {
                pathPrefix = NormalizeAssetPrefix(pathPrefix);
                return null;
            }
            catch (ArgumentException)
            {
                pathPrefix = string.Empty;
                return CommandExecutionResult.Error("invalid_field_value", "path_prefix must be a safe Unity-relative prefix under Assets/ or Packages/");
            }
        }

        static string NormalizeAssetPrefix(string rawPrefix)
        {
            PathGuard.AssertNoNul(rawPrefix);
            var normalized = rawPrefix.Replace('\\', '/');
            if (normalized == "Assets" || normalized == "Assets/") return "Assets/";
            if (normalized == "Packages" || normalized == "Packages/") return "Packages/";
            var hadTrailingSlash = normalized.EndsWith("/", StringComparison.Ordinal);
            var candidate = hadTrailingSlash ? normalized.TrimEnd('/') : normalized;
            candidate = PathGuard.RequireUnityAssetPath(candidate);
            return hadTrailingSlash ? candidate + "/" : candidate;
        }

        static string[] SearchRoots(string pathPrefix, bool includePackages)
        {
            if (pathPrefix.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return new[] { "Packages" };
            }

            return includePackages ? new[] { "Assets", "Packages" } : new[] { "Assets" };
        }

        static List<string> CandidateAssetsByType(string typeName, string[] searchRoots, bool includePackages, string pathPrefix, int maxScanAssets, ref int scannedAssets, ref bool scanTruncated)
        {
            var guids = AssetDatabase.FindAssets("t:" + typeName, searchRoots);
            var paths = new List<string>(guids.Length);
            for (var index = 0; index < guids.Length; index++)
            {
                if (scannedAssets >= maxScanAssets)
                {
                    scanTruncated = true;
                    break;
                }

                scannedAssets++;
                var assetPath = AssetDatabase.GUIDToAssetPath(guids[index]);
                if (string.IsNullOrEmpty(assetPath) || AssetDatabase.IsValidFolder(assetPath) || (!includePackages && assetPath.StartsWith("Packages/", StringComparison.Ordinal)))
                {
                    continue;
                }

                if (!assetPath.StartsWith(pathPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    assetPath = PathGuard.RequireUnityAssetPath(assetPath);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (!paths.Contains(assetPath))
                {
                    paths.Add(assetPath);
                }
            }

            paths.Sort(StringComparer.Ordinal);
            return paths;
        }

        static CommandExecutionResult ResolveAnimatorController(string argsJson, out string assetPath, out string guid, out AnimatorController controller)
        {
            controller = null;
            guid = Bounded(JsonUtil.ExtractString(argsJson, "guid"), 64).Trim();
            assetPath = Bounded(JsonUtil.ExtractString(argsJson, "asset_path"), 512).Trim();
            if (!string.IsNullOrEmpty(guid))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return CommandExecutionResult.Error("not_found", "guid does not resolve to an asset path");
                }
            }

            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandExecutionResult.Error("missing_required_field", "asset_path or guid is required");
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(assetPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must be a Unity-relative path under Assets/ or Packages/");
            }

            var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (asset == null)
            {
                return CommandExecutionResult.Error("not_found", "asset_path does not reference an existing asset");
            }

            controller = asset as AnimatorController;
            if (controller == null)
            {
                return CommandExecutionResult.Error("invalid_target_type", "asset_path or guid must reference an AnimatorController asset");
            }

            guid = AssetDatabase.AssetPathToGUID(assetPath);
            return null;
        }

        static CommandExecutionResult ResolveAnimationClip(string argsJson, out string assetPath, out string guid, out AnimationClip clip, out int clipCount, out string selectionMode)
        {
            clip = null;
            clipCount = 0;
            selectionMode = string.Empty;
            guid = Bounded(JsonUtil.ExtractString(argsJson, "guid"), 64).Trim();
            assetPath = Bounded(JsonUtil.ExtractString(argsJson, "asset_path"), 512).Trim();
            if (!string.IsNullOrEmpty(guid))
            {
                assetPath = AssetDatabase.GUIDToAssetPath(guid);
                if (string.IsNullOrEmpty(assetPath))
                {
                    return CommandExecutionResult.Error("not_found", "guid does not resolve to an asset path");
                }
            }

            if (string.IsNullOrEmpty(assetPath))
            {
                return CommandExecutionResult.Error("missing_required_field", "asset_path or guid is required");
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(assetPath);
            }
            catch (ArgumentException)
            {
                return CommandExecutionResult.Error("invalid_field_value", "asset_path must be a Unity-relative path under Assets/ or Packages/");
            }

            var clipName = Bounded(JsonUtil.ExtractString(argsJson, "clip_name"), 256).Trim();
            var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath) ?? new UnityEngine.Object[0];
            var clips = new List<AnimationClip>();
            for (var index = 0; index < assets.Length; index++)
            {
                if (assets[index] is AnimationClip animationClip)
                {
                    clips.Add(animationClip);
                }
            }

            clipCount = clips.Count;
            if (clips.Count == 0)
            {
                return CommandExecutionResult.Error("invalid_target_type", "asset_path or guid must reference an AnimationClip asset or an asset containing AnimationClip sub-assets");
            }

            if (!string.IsNullOrEmpty(clipName))
            {
                for (var index = 0; index < clips.Count; index++)
                {
                    if (string.Equals(clips[index].name, clipName, StringComparison.Ordinal))
                    {
                        clip = clips[index];
                        guid = AssetDatabase.AssetPathToGUID(assetPath);
                        selectionMode = "clip_name";
                        return null;
                    }
                }

                return CommandExecutionResult.Error("not_found", "clip_name did not match any AnimationClip sub-asset at asset_path");
            }

            var mainAsset = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (mainAsset is AnimationClip mainClip)
            {
                clip = mainClip;
                guid = AssetDatabase.AssetPathToGUID(assetPath);
                selectionMode = "main_asset";
                return null;
            }

            if (clips.Count == 1)
            {
                clip = clips[0];
                guid = AssetDatabase.AssetPathToGUID(assetPath);
                selectionMode = "single_sub_asset";
                return null;
            }

            return CommandExecutionResult.Error("ambiguous_target", "asset_path contains multiple AnimationClip sub-assets; pass clip_name");
        }

        static string CurveBindingsJson(AnimationClip clip, int maxBindings, int maxKeyframesPerCurve, out int bindingCount, out bool truncated)
        {
            var bindings = AnimationUtility.GetCurveBindings(clip) ?? new EditorCurveBinding[0];
            Array.Sort(bindings, CompareEditorCurveBinding);
            bindingCount = bindings.Length;
            truncated = bindings.Length > maxBindings;
            var builder = new StringBuilder();
            var returned = Math.Min(bindings.Length, maxBindings);
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var binding = bindings[index];
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                builder.Append("{\"selector\":").Append(EditorCurveBindingJson(binding, "float_curve"))
                    .Append(",\"curve\":").Append(AnimationCurveJson(curve, maxKeyframesPerCurve)).Append('}');
            }

            return builder.ToString();
        }

        static string AnimatorParametersJson(AnimatorController controller, int maxParameters, out int parameterCount, out bool truncated)
        {
            var parameters = controller.parameters ?? new AnimatorControllerParameter[0];
            parameterCount = parameters.Length;
            truncated = parameters.Length > maxParameters;
            var builder = new StringBuilder();
            var returned = Math.Min(parameters.Length, maxParameters);
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var parameter = parameters[index];
                builder.Append("{\"selector\":{\"parameter_name\":").Append(JsonUtil.String(parameter.name ?? string.Empty))
                    .Append(",\"parameter_index\":").Append(index).Append('}')
                    .Append(",\"name\":").Append(JsonUtil.String(parameter.name ?? string.Empty))
                    .Append(",\"type\":").Append(JsonUtil.String(parameter.type.ToString()))
                    .Append(",\"default_bool\":").Append(JsonUtil.Bool(parameter.defaultBool))
                    .Append(",\"default_float\":").Append(FloatJson(parameter.defaultFloat))
                    .Append(",\"default_int\":").Append(parameter.defaultInt.ToString(CultureInfo.InvariantCulture))
                    .Append('}');
            }

            return builder.ToString();
        }

        static string ObjectReferenceCurveBindingsJson(AnimationClip clip, int maxBindings, int maxKeyframesPerBinding, out int bindingCount, out bool truncated, out int nullReferenceKeyframeCount)
        {
            var bindings = AnimationUtility.GetObjectReferenceCurveBindings(clip) ?? new EditorCurveBinding[0];
            Array.Sort(bindings, CompareEditorCurveBinding);
            bindingCount = bindings.Length;
            truncated = bindings.Length > maxBindings;
            nullReferenceKeyframeCount = 0;
            var builder = new StringBuilder();
            var returned = Math.Min(bindings.Length, maxBindings);
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var binding = bindings[index];
                var keyframes = AnimationUtility.GetObjectReferenceCurve(clip, binding) ?? new ObjectReferenceKeyframe[0];
                builder.Append("{\"selector\":").Append(EditorCurveBindingJson(binding, "object_reference_curve"))
                    .Append(",\"keyframe_count\":").Append(keyframes.Length)
                    .Append(",\"keyframes_truncated\":").Append(JsonUtil.Bool(keyframes.Length > maxKeyframesPerBinding))
                    .Append(",\"keyframes\":[");
                var returnedKeyframes = Math.Min(keyframes.Length, maxKeyframesPerBinding);
                for (var keyIndex = 0; keyIndex < returnedKeyframes; keyIndex++)
                {
                    if (keyIndex > 0)
                    {
                        builder.Append(',');
                    }

                    var keyframe = keyframes[keyIndex];
                    if (keyframe.value == null)
                    {
                        nullReferenceKeyframeCount++;
                    }

                    builder.Append("{\"index\":").Append(keyIndex)
                        .Append(",\"time\":").Append(FloatJson(keyframe.time))
                        .Append(",\"reference\":").Append(ObjectReferenceJson(keyframe.value))
                        .Append(",\"null_or_missing_reference\":").Append(JsonUtil.Bool(keyframe.value == null))
                        .Append('}');
                }

                for (var keyIndex = returnedKeyframes; keyIndex < keyframes.Length; keyIndex++)
                {
                    if (keyframes[keyIndex].value == null)
                    {
                        nullReferenceKeyframeCount++;
                    }
                }

                builder.Append("]}");
            }

            return builder.ToString();
        }

        static string AnimationEventsJson(AnimationClip clip, int maxEvents, int maxStringChars, out int eventCount, out bool truncated)
        {
            var events = AnimationUtility.GetAnimationEvents(clip) ?? new AnimationEvent[0];
            eventCount = events.Length;
            truncated = events.Length > maxEvents;
            var builder = new StringBuilder();
            var returned = Math.Min(events.Length, maxEvents);
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var animationEvent = events[index];
                builder.Append("{\"selector\":{\"event_index\":").Append(index)
                    .Append(",\"time\":").Append(FloatJson(animationEvent.time))
                    .Append(",\"function_name\":").Append(JsonUtil.String(animationEvent.functionName ?? string.Empty)).Append('}')
                    .Append(",\"time\":").Append(FloatJson(animationEvent.time))
                    .Append(",\"function_name\":").Append(JsonUtil.String(animationEvent.functionName ?? string.Empty))
                    .Append(",\"string_parameter\":").Append(JsonUtil.String(Bounded(animationEvent.stringParameter ?? string.Empty, maxStringChars)))
                    .Append(",\"float_parameter\":").Append(FloatJson(animationEvent.floatParameter))
                    .Append(",\"int_parameter\":").Append(animationEvent.intParameter.ToString(CultureInfo.InvariantCulture))
                    .Append(",\"object_reference_parameter\":").Append(ObjectReferenceJson(animationEvent.objectReferenceParameter))
                    .Append('}');
            }

            return builder.ToString();
        }

        static string EditorCurveBindingJson(EditorCurveBinding binding, string curveKind)
        {
            return "{\"path\":" + JsonUtil.String(binding.path ?? string.Empty)
                + ",\"type\":" + JsonUtil.String(binding.type == null ? string.Empty : (binding.type.FullName ?? binding.type.Name))
                + ",\"property_name\":" + JsonUtil.String(binding.propertyName ?? string.Empty)
                + ",\"curve_kind\":" + JsonUtil.String(curveKind) + "}";
        }

        static string AnimationCurveJson(AnimationCurve curve, int maxKeyframes)
        {
            var keyframes = curve == null ? new Keyframe[0] : (curve.keys ?? new Keyframe[0]);
            var builder = new StringBuilder();
            builder.Append("{\"keyframe_count\":").Append(keyframes.Length)
                .Append(",\"keyframes_truncated\":").Append(JsonUtil.Bool(keyframes.Length > maxKeyframes))
                .Append(",\"pre_wrap_mode\":").Append(JsonUtil.String(curve == null ? string.Empty : curve.preWrapMode.ToString()))
                .Append(",\"post_wrap_mode\":").Append(JsonUtil.String(curve == null ? string.Empty : curve.postWrapMode.ToString()))
                .Append(",\"keyframes\":[");
            var returned = Math.Min(keyframes.Length, maxKeyframes);
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                var keyframe = keyframes[index];
                builder.Append("{\"index\":").Append(index)
                    .Append(",\"time\":").Append(FloatJson(keyframe.time))
                    .Append(",\"value\":").Append(FloatJson(keyframe.value))
                    .Append(",\"in_tangent\":").Append(FloatJson(keyframe.inTangent))
                    .Append(",\"out_tangent\":").Append(FloatJson(keyframe.outTangent))
                    .Append('}');
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static int CompareEditorCurveBinding(EditorCurveBinding left, EditorCurveBinding right)
        {
            var result = string.Compare(left.path ?? string.Empty, right.path ?? string.Empty, StringComparison.Ordinal);
            if (result != 0)
            {
                return result;
            }

            result = string.Compare(left.type == null ? string.Empty : (left.type.FullName ?? left.type.Name), right.type == null ? string.Empty : (right.type.FullName ?? right.type.Name), StringComparison.Ordinal);
            return result != 0 ? result : string.Compare(left.propertyName ?? string.Empty, right.propertyName ?? string.Empty, StringComparison.Ordinal);
        }

        static IEnumerable ReflectionInvokeEnumerable(object target, string methodName)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                var method = target.GetType().GetMethod(methodName, Type.EmptyTypes);
                return method == null ? null : method.Invoke(target, null) as IEnumerable;
            }
            catch (Exception)
            {
                return null;
            }
        }

        static object ReflectionPropertyValue(object target, string propertyName)
        {
            if (target == null)
            {
                return null;
            }

            try
            {
                var property = target.GetType().GetProperty(propertyName);
                return property == null ? null : property.GetValue(target, null);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string ReflectionString(object target, string propertyName, int maxStringChars)
        {
            return JsonUtil.String(Bounded(Convert.ToString(ReflectionPropertyValue(target, propertyName), CultureInfo.InvariantCulture) ?? string.Empty, maxStringChars));
        }

        static string ReflectionBoolJson(object target, string propertyName)
        {
            var value = ReflectionPropertyValue(target, propertyName);
            return value is bool boolValue ? JsonUtil.Bool(boolValue) : "null";
        }

        static string ReflectionDoubleJson(object target, string propertyName)
        {
            var value = ReflectionPropertyValue(target, propertyName);
            if (value == null)
            {
                return "null";
            }

            try
            {
                return DoubleJson(Convert.ToDouble(value, CultureInfo.InvariantCulture));
            }
            catch (Exception)
            {
                return "null";
            }
        }

        static string SerializedPropertiesJson(SerializedObject serializedObject, string propertyPath, bool includeReferences, bool includeManagedReferences, int maxProperties, int maxDepth, int maxArrayElements, int maxStringChars, ReferenceCollector references, ManagedReferenceFindings managedReferenceFindings, out int propertyCount, out bool truncated, out string error)
        {
            propertyCount = 0;
            truncated = false;
            error = string.Empty;
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(propertyPath))
            {
                var property = FindSerializedProperty(serializedObject, propertyPath);
                if (property == null)
                {
                    error = "property_path was not found on the ScriptableObject";
                    return string.Empty;
                }

                AppendPropertyTree(builder, property, property.depth, includeReferences, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxStringChars, references, managedReferenceFindings, ref propertyCount, ref truncated);
                return builder.ToString();
            }

            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = iterator.depth < maxDepth;
                if (!AppendProperty(builder, iterator, 0, includeReferences, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxStringChars, references, managedReferenceFindings, ref propertyCount, ref truncated))
                {
                    break;
                }
            }

            return builder.ToString();
        }

        static string SerializedSchemaJson(SerializedObject serializedObject, string propertyPath, bool includeEnumNames, bool includeManagedReferences, int maxProperties, int maxDepth, int maxArrayElements, int maxEnumNames, out int propertyCount, out bool truncated, out string error)
        {
            propertyCount = 0;
            truncated = false;
            error = string.Empty;
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(propertyPath))
            {
                var property = FindSerializedProperty(serializedObject, propertyPath);
                if (property == null)
                {
                    error = "property_path was not found on the ScriptableObject";
                    return string.Empty;
                }

                AppendSchemaPropertyTree(builder, property, property.depth, includeEnumNames, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxEnumNames, ref propertyCount, ref truncated);
                return builder.ToString();
            }

            var iterator = serializedObject.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = iterator.depth < maxDepth;
                if (!AppendSchemaProperty(builder, iterator, 0, includeEnumNames, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxEnumNames, ref propertyCount, ref truncated))
                {
                    break;
                }
            }

            return builder.ToString();
        }

        static void AppendSchemaPropertyTree(StringBuilder builder, SerializedProperty rootProperty, int rootDepth, bool includeEnumNames, bool includeManagedReferences, int maxProperties, int maxDepth, int maxArrayElements, int maxEnumNames, ref int propertyCount, ref bool truncated)
        {
            var property = rootProperty.Copy();
            var end = property.GetEndProperty();
            var enterChildren = true;
            do
            {
                enterChildren = property.depth - rootDepth < maxDepth;
                if (!AppendSchemaProperty(builder, property, rootDepth, includeEnumNames, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxEnumNames, ref propertyCount, ref truncated))
                {
                    break;
                }
            }
            while (property.NextVisible(enterChildren) && !SerializedProperty.EqualContents(property, end));
        }

        static bool AppendSchemaProperty(StringBuilder builder, SerializedProperty property, int rootDepth, bool includeEnumNames, bool includeManagedReferences, int maxProperties, int maxDepth, int maxArrayElements, int maxEnumNames, ref int propertyCount, ref bool truncated)
        {
            var relativeDepth = property.depth - rootDepth;
            if (relativeDepth > maxDepth || IsArrayElementPastLimit(property.propertyPath, maxArrayElements))
            {
                truncated = true;
                return true;
            }

            if (propertyCount >= maxProperties)
            {
                truncated = true;
                return false;
            }

            if (propertyCount > 0)
            {
                builder.Append(',');
            }

            builder.Append(SchemaPropertyJson(property, relativeDepth, includeEnumNames, includeManagedReferences, maxEnumNames));
            propertyCount++;
            return true;
        }

        static string SchemaPropertyJson(SerializedProperty property, int relativeDepth, bool includeEnumNames, bool includeManagedReferences, int maxEnumNames)
        {
            return "{\"selector\":{\"property_path\":" + JsonUtil.String(property.propertyPath)
                + ",\"display_name\":" + JsonUtil.String(property.displayName) + "}"
                + ",\"depth\":" + relativeDepth
                + ",\"property_type\":" + JsonUtil.String(property.propertyType.ToString())
                + ",\"value_shape\":" + JsonUtil.String(PropertyValueShape(property))
                + ",\"is_array\":" + JsonUtil.Bool(property.isArray && property.propertyType != SerializedPropertyType.String)
                + ",\"array_size\":" + ArraySizeJson(property)
                + ",\"enum_names\":" + (includeEnumNames && property.propertyType == SerializedPropertyType.Enum ? EnumNamesJson(property, maxEnumNames) : "[]")
                + ",\"object_reference_type\":" + (property.propertyType == SerializedPropertyType.ObjectReference ? JsonUtil.String(property.objectReferenceValue == null ? string.Empty : (property.objectReferenceValue.GetType().FullName ?? property.objectReferenceValue.GetType().Name)) : "null")
                + ",\"managed_reference_schema\":" + (includeManagedReferences && property.propertyType == SerializedPropertyType.ManagedReference ? ManagedReferenceJson(property, out _) : "null")
                + "}";
        }

        static string PropertyValueShape(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                case SerializedPropertyType.LayerMask:
                case SerializedPropertyType.ArraySize:
                    return "integer";
                case SerializedPropertyType.Boolean:
                    return "boolean";
                case SerializedPropertyType.Float:
                    return "number";
                case SerializedPropertyType.String:
                case SerializedPropertyType.Character:
                case SerializedPropertyType.Enum:
                    return "string";
                case SerializedPropertyType.Color:
                    return "color";
                case SerializedPropertyType.ObjectReference:
                    return "object_reference";
                case SerializedPropertyType.Vector2:
                    return "vector2";
                case SerializedPropertyType.Vector3:
                    return "vector3";
                case SerializedPropertyType.Vector4:
                    return "vector4";
                case SerializedPropertyType.Rect:
                    return "rect";
                case SerializedPropertyType.Bounds:
                    return "bounds";
                case SerializedPropertyType.Quaternion:
                    return "quaternion";
                case SerializedPropertyType.ManagedReference:
                    return "managed_reference";
                default:
                    return "object_or_unsupported";
            }
        }

        static string EnumNamesJson(SerializedProperty property, int maxEnumNames)
        {
            var names = property.enumDisplayNames ?? new string[0];
            var returned = Math.Min(names.Length, maxEnumNames);
            var builder = new StringBuilder();
            builder.Append('[');
            for (var index = 0; index < returned; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(names[index] ?? string.Empty));
            }

            builder.Append(']');
            return builder.ToString();
        }

        static void AppendPropertyTree(StringBuilder builder, SerializedProperty rootProperty, int rootDepth, bool includeReferences, bool includeManagedReferences, int maxProperties, int maxDepth, int maxArrayElements, int maxStringChars, ReferenceCollector references, ManagedReferenceFindings managedReferenceFindings, ref int propertyCount, ref bool truncated)
        {
            var property = rootProperty.Copy();
            var end = property.GetEndProperty();
            var enterChildren = true;
            do
            {
                enterChildren = property.depth - rootDepth < maxDepth;
                if (!AppendProperty(builder, property, rootDepth, includeReferences, includeManagedReferences, maxProperties, maxDepth, maxArrayElements, maxStringChars, references, managedReferenceFindings, ref propertyCount, ref truncated))
                {
                    break;
                }
            }
            while (property.NextVisible(enterChildren) && !SerializedProperty.EqualContents(property, end));
        }

        static bool AppendProperty(StringBuilder builder, SerializedProperty property, int rootDepth, bool includeReferences, bool includeManagedReferences, int maxProperties, int maxDepth, int maxArrayElements, int maxStringChars, ReferenceCollector references, ManagedReferenceFindings managedReferenceFindings, ref int propertyCount, ref bool truncated)
        {
            var relativeDepth = property.depth - rootDepth;
            if (relativeDepth > maxDepth || IsArrayElementPastLimit(property.propertyPath, maxArrayElements))
            {
                truncated = true;
                return true;
            }

            if (propertyCount >= maxProperties)
            {
                truncated = true;
                return false;
            }

            if (propertyCount > 0)
            {
                builder.Append(',');
            }

            builder.Append(PropertyJson(property, relativeDepth, includeReferences, includeManagedReferences, maxStringChars, references, managedReferenceFindings));
            propertyCount++;
            return true;
        }

        static string PropertyJson(SerializedProperty property, int relativeDepth, bool includeReferences, bool includeManagedReferences, int maxStringChars, ReferenceCollector references, ManagedReferenceFindings managedReferenceFindings)
        {
            var objectReferenceJson = "null";
            if (includeReferences && property.propertyType == SerializedPropertyType.ObjectReference)
            {
                objectReferenceJson = ObjectReferencePropertyJson(property);
                references.Add(property.propertyPath, property);
            }

            var managedReferenceJson = "null";
            if (includeManagedReferences && property.propertyType == SerializedPropertyType.ManagedReference)
            {
                managedReferenceJson = ManagedReferenceJson(property, out var status);
                managedReferenceFindings.Add(property.propertyPath, managedReferenceJson, status);
            }

            return "{\"selector\":{\"property_path\":" + JsonUtil.String(property.propertyPath)
                + ",\"display_name\":" + JsonUtil.String(property.displayName) + "}"
                + ",\"depth\":" + relativeDepth
                + ",\"property_type\":" + JsonUtil.String(property.propertyType.ToString())
                + ",\"is_array\":" + JsonUtil.Bool(property.isArray && property.propertyType != SerializedPropertyType.String)
                + ",\"array_size\":" + ArraySizeJson(property)
                + ",\"value\":" + PropertyValueJson(property, maxStringChars)
                + ",\"object_reference\":" + objectReferenceJson
                + ",\"managed_reference\":" + managedReferenceJson
                + "}";
        }

        static string PropertyValueJson(SerializedProperty property, int maxStringChars)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Boolean:
                    return JsonUtil.Bool(property.boolValue);
                case SerializedPropertyType.Float:
                    return FloatJson(property.floatValue);
                case SerializedPropertyType.String:
                    return JsonUtil.String(Bounded(property.stringValue ?? string.Empty, maxStringChars));
                case SerializedPropertyType.Color:
                    return ColorJson(property.colorValue);
                case SerializedPropertyType.ObjectReference:
                    return ObjectReferencePropertyJson(property);
                case SerializedPropertyType.LayerMask:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Enum:
                    return JsonUtil.String(property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString(CultureInfo.InvariantCulture));
                case SerializedPropertyType.Vector2:
                    return Vector2Json(property.vector2Value);
                case SerializedPropertyType.Vector3:
                    return Vector3Json(property.vector3Value);
                case SerializedPropertyType.Vector4:
                    return Vector4Json(property.vector4Value);
                case SerializedPropertyType.Rect:
                    return RectJson(property.rectValue);
                case SerializedPropertyType.ArraySize:
                    return property.intValue.ToString(CultureInfo.InvariantCulture);
                case SerializedPropertyType.Character:
                    return JsonUtil.String(((char)property.intValue).ToString());
                case SerializedPropertyType.Bounds:
                    return BoundsJson(property.boundsValue);
                case SerializedPropertyType.Quaternion:
                    return QuaternionJson(property.quaternionValue);
                case SerializedPropertyType.ManagedReference:
                    return "null";
                default:
                    return "null";
            }
        }

        static string ManagedReferenceJson(SerializedProperty property, out string status)
        {
            ManagedReferenceMetadata(property, out status, out var referenceId, out var fullTypename, out var fieldTypename);
            return "{\"status\":" + JsonUtil.String(status)
                + ",\"managed_reference_id\":" + referenceId.ToString(CultureInfo.InvariantCulture)
                + ",\"full_typename\":" + JsonUtil.String(fullTypename)
                + ",\"field_typename\":" + JsonUtil.String(fieldTypename) + "}";
        }

        static void ManagedReferenceMetadata(SerializedProperty property, out string status, out long referenceId, out string fullTypename, out string fieldTypename)
        {
            try
            {
                fullTypename = property.managedReferenceFullTypename ?? string.Empty;
                fieldTypename = property.managedReferenceFieldTypename ?? string.Empty;
                referenceId = property.managedReferenceId;
            }
            catch (Exception)
            {
                fullTypename = string.Empty;
                fieldTypename = string.Empty;
                referenceId = 0;
            }

            status = !string.IsNullOrEmpty(fullTypename) ? "ok" : (referenceId == 0 ? "null" : "missing_type_or_unresolved");
        }

        static SerializedProperty FindSerializedProperty(SerializedObject serializedObject, string propertyPath)
        {
            var property = serializedObject.FindProperty(propertyPath);
            return property != null || propertyPath.IndexOf('/') < 0 ? property : serializedObject.FindProperty(propertyPath.Replace('/', '.'));
        }

        static string AssetDependenciesJson(string assetPath, int maxDependencies, out int dependencyCount, out bool truncated)
        {
            dependencyCount = 0;
            truncated = false;
            if (maxDependencies <= 0)
            {
                return string.Empty;
            }

            var dependencies = AssetDatabase.GetDependencies(assetPath, true) ?? new string[0];
            Array.Sort(dependencies, StringComparer.Ordinal);
            var builder = new StringBuilder();
            var returned = 0;
            for (var index = 0; index < dependencies.Length; index++)
            {
                var dependencyPath = dependencies[index];
                if (string.Equals(dependencyPath, assetPath, StringComparison.Ordinal))
                {
                    continue;
                }

                dependencyCount++;
                if (returned >= maxDependencies)
                {
                    truncated = true;
                    continue;
                }

                if (returned > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"asset_path\":").Append(JsonUtil.String(dependencyPath))
                    .Append(",\"guid\":").Append(JsonUtil.String(AssetDatabase.AssetPathToGUID(dependencyPath))).Append('}');
                returned++;
            }

            return builder.ToString();
        }

        static string AssetJson(string assetPath, string guid, UnityEngine.Object asset)
        {
            var type = asset.GetType();
            return "{\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"name\":" + JsonUtil.String(asset.name)
                + ",\"type\":" + JsonUtil.String(type.FullName ?? type.Name) + "}";
        }

        static string ObjectReferencePropertyJson(SerializedProperty property)
        {
            var value = property.objectReferenceValue;
            if (value == null)
            {
                return "{\"path\":\"\",\"guid\":\"\",\"name\":\"\",\"type\":\"\","
                    + ObjectReferenceIdentityJson(property)
                    + ",\"missing\":" + JsonUtil.Bool(ObjectReferenceIsMissing(property)) + "}";
            }

            return ObjectReferenceJson(value);
        }

        static string ObjectReferenceJson(UnityEngine.Object value)
        {
            if (value == null)
            {
                return "null";
            }

            var path = AssetDatabase.GetAssetPath(value);
            var type = value.GetType();
            return "{\"path\":" + JsonUtil.String(path)
                + ",\"guid\":" + JsonUtil.String(string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path))
                + ",\"name\":" + JsonUtil.String(value.name)
                + ",\"type\":" + JsonUtil.String(type.FullName ?? type.Name)
                + "," + ObjectIdentityJson(value)
                + ",\"missing\":false}";
        }

#if UNITY_6000_4_OR_NEWER
        static string ObjectReferenceIdentityJson(SerializedProperty property)
        {
            return "\"entity_id\":" + EntityId.ToULong(property.objectReferenceEntityIdValue).ToString(CultureInfo.InvariantCulture);
        }

        static string ObjectReferenceIssueIdentityJson(SerializedProperty property)
        {
            return "\"object_reference_entity_id\":" + EntityId.ToULong(property.objectReferenceEntityIdValue).ToString(CultureInfo.InvariantCulture);
        }

        static string ObjectReferenceIdentityKey(SerializedProperty property)
        {
            return EntityId.ToULong(property.objectReferenceEntityIdValue).ToString(CultureInfo.InvariantCulture);
        }

        static bool ObjectReferenceIsMissing(SerializedProperty property)
        {
            return property.objectReferenceValue == null && property.objectReferenceEntityIdValue != EntityId.None;
        }

        static string ObjectIdentityJson(UnityEngine.Object value)
        {
            return "\"entity_id\":" + EntityId.ToULong(value.GetEntityId()).ToString(CultureInfo.InvariantCulture);
        }

        static string ObjectIdentityKey(UnityEngine.Object value)
        {
            return EntityId.ToULong(value.GetEntityId()).ToString(CultureInfo.InvariantCulture);
        }
#else
        static string ObjectReferenceIdentityJson(SerializedProperty property)
        {
            return "\"instance_id\":" + property.objectReferenceInstanceIDValue.ToString(CultureInfo.InvariantCulture);
        }

        static string ObjectReferenceIssueIdentityJson(SerializedProperty property)
        {
            return "\"object_reference_instance_id\":" + property.objectReferenceInstanceIDValue.ToString(CultureInfo.InvariantCulture);
        }

        static string ObjectReferenceIdentityKey(SerializedProperty property)
        {
            return property.objectReferenceInstanceIDValue.ToString(CultureInfo.InvariantCulture);
        }

        static bool ObjectReferenceIsMissing(SerializedProperty property)
        {
            return property.objectReferenceValue == null && property.objectReferenceInstanceIDValue != 0;
        }

        static string ObjectIdentityJson(UnityEngine.Object value)
        {
            return "\"instance_id\":" + value.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }

        static string ObjectIdentityKey(UnityEngine.Object value)
        {
            return value.GetInstanceID().ToString(CultureInfo.InvariantCulture);
        }
#endif

        static string ArraySizeJson(SerializedProperty property)
        {
            return property.isArray && property.propertyType != SerializedPropertyType.String ? property.arraySize.ToString(CultureInfo.InvariantCulture) : "null";
        }

        static string FloatJson(float value)
        {
            return float.IsNaN(value) || float.IsInfinity(value) ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        static string DoubleJson(double value)
        {
            return double.IsNaN(value) || double.IsInfinity(value) ? "null" : value.ToString("R", CultureInfo.InvariantCulture);
        }

        static string ColorJson(Color value)
        {
            return "{\"r\":" + FloatJson(value.r) + ",\"g\":" + FloatJson(value.g) + ",\"b\":" + FloatJson(value.b) + ",\"a\":" + FloatJson(value.a) + "}";
        }

        static string Vector2Json(Vector2 value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + "}";
        }

        static string Vector3Json(Vector3 value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + ",\"z\":" + FloatJson(value.z) + "}";
        }

        static string Vector4Json(Vector4 value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + ",\"z\":" + FloatJson(value.z) + ",\"w\":" + FloatJson(value.w) + "}";
        }

        static string RectJson(Rect value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + ",\"width\":" + FloatJson(value.width) + ",\"height\":" + FloatJson(value.height) + "}";
        }

        static string BoundsJson(Bounds value)
        {
            return "{\"center\":" + Vector3Json(value.center) + ",\"size\":" + Vector3Json(value.size) + "}";
        }

        static string QuaternionJson(Quaternion value)
        {
            return "{\"x\":" + FloatJson(value.x) + ",\"y\":" + FloatJson(value.y) + ",\"z\":" + FloatJson(value.z) + ",\"w\":" + FloatJson(value.w) + "}";
        }

        static bool IsArrayElementPastLimit(string propertyPath, int maxArrayElements)
        {
            const string Marker = ".Array.data[";
            var searchFrom = 0;
            while (searchFrom < propertyPath.Length)
            {
                var marker = propertyPath.IndexOf(Marker, searchFrom, StringComparison.Ordinal);
                if (marker < 0)
                {
                    return false;
                }

                var start = marker + Marker.Length;
                var end = propertyPath.IndexOf(']', start);
                if (end < 0)
                {
                    return false;
                }

                if (int.TryParse(propertyPath.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out var arrayIndex) && arrayIndex >= maxArrayElements)
                {
                    return true;
                }

                searchFrom = end + 1;
            }

            return false;
        }

        static bool PropertyPathLooksUnsafe(string propertyPath)
        {
            if (string.IsNullOrEmpty(propertyPath))
            {
                return false;
            }

            if (propertyPath.IndexOf("://", StringComparison.Ordinal) >= 0 || propertyPath.IndexOf('\\') >= 0)
            {
                return true;
            }

            for (var index = 0; index < propertyPath.Length; index++)
            {
                var character = propertyPath[index];
                if (!(char.IsLetterOrDigit(character) || character == '_' || character == '.' || character == '[' || character == ']'))
                {
                    return true;
                }
            }

            return false;
        }

        static string Bounded(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
            {
                return value ?? string.Empty;
            }

            return value.Substring(0, maxLength);
        }

        static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }

            return value > maximum ? maximum : value;
        }

        sealed class ReferenceCollector
        {
            readonly int MaxReferences;
            readonly HashSet<string> Seen = new HashSet<string>(StringComparer.Ordinal);

            public ReferenceCollector(int maxReferences)
            {
                MaxReferences = maxReferences;
            }

            public int TotalCount { get; private set; }
            public bool Truncated { get; private set; }
            public StringBuilder Json { get; } = new StringBuilder();

            public void Add(string propertyPath, SerializedProperty property)
            {
                TotalCount++;
                var key = propertyPath + "|" + ObjectReferenceIdentityKey(property);
                if (!Seen.Add(key))
                {
                    return;
                }

                if (Seen.Count > MaxReferences)
                {
                    Truncated = true;
                    return;
                }

                if (Json.Length > 0)
                {
                    Json.Append(',');
                }

                Json.Append("{\"property_path\":").Append(JsonUtil.String(propertyPath))
                    .Append(",\"reference\":");
                var value = property.objectReferenceValue;
                if (value == null)
                {
                    Json.Append("{\"path\":\"\",\"guid\":\"\",\"name\":\"\",\"type\":\"\",")
                        .Append(ObjectReferenceIdentityJson(property))
                        .Append(",\"missing\":").Append(JsonUtil.Bool(ObjectReferenceIsMissing(property))).Append('}');
                }
                else
                {
                    Json.Append(ObjectReferenceJson(value));
                }

                Json.Append('}');
            }
        }

        sealed class ManagedReferenceFindings
        {
            public int TotalCount { get; private set; }
            public int MissingOrUnresolvedCount { get; private set; }
            public StringBuilder Json { get; } = new StringBuilder();

            public void Add(string propertyPath, string managedReferenceJson, string status)
            {
                TotalCount++;
                if (!string.Equals(status, "ok", StringComparison.Ordinal) && !string.Equals(status, "null", StringComparison.Ordinal))
                {
                    MissingOrUnresolvedCount++;
                }

                if (Json.Length > 0)
                {
                    Json.Append(',');
                }

                Json.Append("{\"property_path\":").Append(JsonUtil.String(propertyPath))
                    .Append(",\"managed_reference\":").Append(managedReferenceJson).Append('}');
            }
        }

        sealed class AnimationBindingsReportState
        {
            readonly int MaxClipSamples;
            readonly int MaxBindingSamples;
            readonly int MaxControllerSamples;
            readonly int MaxMotionReferenceSamples;
            readonly int MaxDepth;

            public AnimationBindingsReportState(int maxClipSamples, int maxBindingSamples, int maxControllerSamples, int maxMotionReferenceSamples, int maxDepth)
            {
                MaxClipSamples = maxClipSamples;
                MaxBindingSamples = maxBindingSamples;
                MaxControllerSamples = maxControllerSamples;
                MaxMotionReferenceSamples = maxMotionReferenceSamples;
                MaxDepth = maxDepth;
            }

            public int ClipAssetCount { get; private set; }
            public int ClipCount { get; private set; }
            public int CurveBindingCount { get; private set; }
            public int ObjectReferenceBindingCount { get; private set; }
            public int AnimationEventCount { get; private set; }
            public bool ClipSamplesTruncated { get; private set; }
            public StringBuilder ClipSamplesJson { get; } = new StringBuilder();
            public bool BindingSamplesTruncated { get; private set; }
            public StringBuilder BindingSamplesJson { get; } = new StringBuilder();
            int BindingSampleCount;
            public int ControllerAssetCount { get; private set; }
            public int ControllerStateCount { get; private set; }
            public int ControllerTransitionCount { get; private set; }
            public int MotionReferenceCount { get; private set; }
            public bool ControllerSamplesTruncated { get; private set; }
            public StringBuilder ControllerSamplesJson { get; } = new StringBuilder();
            public bool MotionReferenceSamplesTruncated { get; private set; }
            public StringBuilder MotionReferenceSamplesJson { get; } = new StringBuilder();

            public void InspectClipAsset(string assetPath)
            {
                ClipAssetCount++;
                var assets = AssetDatabase.LoadAllAssetsAtPath(assetPath) ?? new UnityEngine.Object[0];
                for (var index = 0; index < assets.Length; index++)
                {
                    if (!(assets[index] is AnimationClip clip))
                    {
                        continue;
                    }

                    ClipCount++;
                    var curveBindings = AnimationUtility.GetCurveBindings(clip) ?? new EditorCurveBinding[0];
                    var objectReferenceBindings = AnimationUtility.GetObjectReferenceCurveBindings(clip) ?? new EditorCurveBinding[0];
                    var events = AnimationUtility.GetAnimationEvents(clip) ?? new AnimationEvent[0];
                    CurveBindingCount += curveBindings.Length;
                    ObjectReferenceBindingCount += objectReferenceBindings.Length;
                    AnimationEventCount += events.Length;
                    AppendClipSample(assetPath, clip, curveBindings.Length, objectReferenceBindings.Length, events.Length);
                    AppendBindingSamples(assetPath, clip, curveBindings, "float_curve");
                    AppendBindingSamples(assetPath, clip, objectReferenceBindings, "object_reference_curve");
                }
            }

            public void InspectControllerAsset(string assetPath)
            {
                var controller = AssetDatabase.LoadMainAssetAtPath(assetPath) as AnimatorController;
                if (controller == null)
                {
                    return;
                }

                ControllerAssetCount++;
                var beforeStates = ControllerStateCount;
                var beforeTransitions = ControllerTransitionCount;
                var beforeMotions = MotionReferenceCount;
                var layers = controller.layers ?? new AnimatorControllerLayer[0];
                for (var index = 0; index < layers.Length; index++)
                {
                    var layerName = string.IsNullOrEmpty(layers[index].name) ? "Layer" + index.ToString(CultureInfo.InvariantCulture) : layers[index].name;
                    InspectStateMachine(assetPath, index, layerName, layers[index].stateMachine, layerName, 0);
                }

                AppendControllerSample(assetPath, controller, ControllerStateCount - beforeStates, ControllerTransitionCount - beforeTransitions, MotionReferenceCount - beforeMotions);
            }

            void InspectStateMachine(string assetPath, int layerIndex, string layerName, AnimatorStateMachine stateMachine, string stateMachinePath, int depth)
            {
                if (stateMachine == null || depth > MaxDepth)
                {
                    return;
                }

                CountTransitions(stateMachine.anyStateTransitions);
                CountTransitions(stateMachine.entryTransitions);
                var childStates = stateMachine.states ?? new ChildAnimatorState[0];
                Array.Sort(childStates, CompareChildAnimatorState);
                for (var index = 0; index < childStates.Length; index++)
                {
                    var state = childStates[index].state;
                    if (state == null)
                    {
                        continue;
                    }

                    ControllerStateCount++;
                    var statePath = stateMachinePath + "/" + state.name;
                    if (state.motion != null)
                    {
                        MotionReferenceCount++;
                        AppendMotionReferenceSample(assetPath, layerIndex, layerName, statePath, state.motion);
                    }

                    CountTransitions(state.transitions);
                }

                var childStateMachines = stateMachine.stateMachines ?? new ChildAnimatorStateMachine[0];
                Array.Sort(childStateMachines, CompareChildAnimatorStateMachine);
                for (var index = 0; index < childStateMachines.Length; index++)
                {
                    var childStateMachine = childStateMachines[index].stateMachine;
                    if (childStateMachine != null)
                    {
                        InspectStateMachine(assetPath, layerIndex, layerName, childStateMachine, stateMachinePath + "/" + childStateMachine.name, depth + 1);
                    }
                }
            }

            void CountTransitions(AnimatorTransitionBase[] transitions)
            {
                if (transitions != null)
                {
                    ControllerTransitionCount += transitions.Length;
                }
            }

            void AppendClipSample(string assetPath, AnimationClip clip, int curveBindingCount, int objectReferenceBindingCount, int eventCount)
            {
                if (ClipCount > MaxClipSamples)
                {
                    ClipSamplesTruncated = true;
                    return;
                }

                if (ClipSamplesJson.Length > 0)
                {
                    ClipSamplesJson.Append(',');
                }

                ClipSamplesJson.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"clip_name\":").Append(JsonUtil.String(clip.name ?? string.Empty))
                    .Append(",\"length\":").Append(FloatJson(clip.length))
                    .Append(",\"frame_rate\":").Append(FloatJson(clip.frameRate))
                    .Append(",\"curve_binding_count\":").Append(curveBindingCount)
                    .Append(",\"object_reference_binding_count\":").Append(objectReferenceBindingCount)
                    .Append(",\"animation_event_count\":").Append(eventCount)
                    .Append('}');
            }

            void AppendBindingSamples(string assetPath, AnimationClip clip, EditorCurveBinding[] bindings, string curveKind)
            {
                Array.Sort(bindings, CompareEditorCurveBinding);
                for (var index = 0; index < bindings.Length; index++)
                {
                    if (BindingSampleCount >= MaxBindingSamples)
                    {
                        BindingSamplesTruncated = true;
                        return;
                    }

                    if (BindingSamplesJson.Length > 0)
                    {
                        BindingSamplesJson.Append(',');
                    }

                    var binding = bindings[index];
                    BindingSamplesJson.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                        .Append(",\"clip_name\":").Append(JsonUtil.String(clip.name ?? string.Empty))
                        .Append(",\"selector\":").Append(EditorCurveBindingJson(binding, curveKind))
                        .Append('}');
                    BindingSampleCount++;
                }
            }

            void AppendControllerSample(string assetPath, AnimatorController controller, int stateCount, int transitionCount, int motionReferenceCount)
            {
                if (ControllerAssetCount > MaxControllerSamples)
                {
                    ControllerSamplesTruncated = true;
                    return;
                }

                if (ControllerSamplesJson.Length > 0)
                {
                    ControllerSamplesJson.Append(',');
                }

                ControllerSamplesJson.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"controller_name\":").Append(JsonUtil.String(controller.name ?? string.Empty))
                    .Append(",\"parameter_count\":").Append(controller.parameters == null ? 0 : controller.parameters.Length)
                    .Append(",\"layer_count\":").Append(controller.layers == null ? 0 : controller.layers.Length)
                    .Append(",\"state_count\":").Append(stateCount)
                    .Append(",\"transition_count\":").Append(transitionCount)
                    .Append(",\"motion_reference_count\":").Append(motionReferenceCount)
                    .Append('}');
            }

            void AppendMotionReferenceSample(string assetPath, int layerIndex, string layerName, string statePath, Motion motion)
            {
                if (MotionReferenceCount > MaxMotionReferenceSamples)
                {
                    MotionReferenceSamplesTruncated = true;
                    return;
                }

                if (MotionReferenceSamplesJson.Length > 0)
                {
                    MotionReferenceSamplesJson.Append(',');
                }

                MotionReferenceSamplesJson.Append("{\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"selector\":{\"layer_index\":").Append(layerIndex)
                    .Append(",\"layer_name\":").Append(JsonUtil.String(layerName))
                    .Append(",\"state_path\":").Append(JsonUtil.String(statePath)).Append('}')
                    .Append(",\"motion\":").Append(MotionJson(motion))
                    .Append('}');
            }

            static string MotionJson(Motion motion)
            {
                if (motion == null)
                {
                    return "null";
                }

                return "{\"reference\":" + ObjectReferenceJson(motion)
                    + ",\"motion_kind\":" + JsonUtil.String(motion is AnimationClip ? "animation_clip" : (motion is BlendTree ? "blend_tree" : "motion"))
                    + "}";
            }

            static int CompareChildAnimatorState(ChildAnimatorState left, ChildAnimatorState right)
            {
                return string.Compare(left.state == null ? string.Empty : left.state.name, right.state == null ? string.Empty : right.state.name, StringComparison.Ordinal);
            }

            static int CompareChildAnimatorStateMachine(ChildAnimatorStateMachine left, ChildAnimatorStateMachine right)
            {
                return string.Compare(left.stateMachine == null ? string.Empty : left.stateMachine.name, right.stateMachine == null ? string.Empty : right.stateMachine.name, StringComparison.Ordinal);
            }
        }

        sealed class TimelineInspectionState
        {
            readonly int MaxTracks;
            readonly int MaxClips;
            readonly int MaxDepth;
            readonly int MaxStringChars;
            readonly bool IncludeTracks;
            readonly bool IncludeClips;

            public TimelineInspectionState(int maxTracks, int maxClips, int maxDepth, int maxStringChars, bool includeTracks, bool includeClips)
            {
                MaxTracks = maxTracks;
                MaxClips = maxClips;
                MaxDepth = maxDepth;
                MaxStringChars = maxStringChars;
                IncludeTracks = includeTracks;
                IncludeClips = includeClips;
            }

            public int TrackCount { get; private set; }
            public bool TracksTruncated { get; private set; }
            public StringBuilder TracksJson { get; } = new StringBuilder();
            public int ClipCount { get; private set; }
            public bool ClipsTruncated { get; private set; }
            public StringBuilder ClipsJson { get; } = new StringBuilder();
            public int ClipAssetReferenceCount { get; private set; }

            public void Inspect(UnityEngine.Object timelineAsset)
            {
                var rootTracks = ReflectionInvokeEnumerable(timelineAsset, "GetRootTracks");
                if (rootTracks == null)
                {
                    return;
                }

                var index = 0;
                foreach (var track in rootTracks)
                {
                    var trackName = ObjectName(track);
                    var trackPath = string.IsNullOrEmpty(trackName) ? "Track" + index.ToString(CultureInfo.InvariantCulture) : trackName;
                    InspectTrack(track, trackPath, index, 0);
                    index++;
                }
            }

            void InspectTrack(object track, string trackPath, int trackIndex, int depth)
            {
                if (track == null)
                {
                    return;
                }

                if (depth > MaxDepth)
                {
                    TracksTruncated = true;
                    ClipsTruncated = true;
                    return;
                }

                TrackCount++;
                if (IncludeTracks)
                {
                    AppendTrack(track, trackPath, trackIndex, depth);
                }

                if (IncludeClips)
                {
                    var clips = ReflectionInvokeEnumerable(track, "GetClips");
                    if (clips != null)
                    {
                        var clipIndex = 0;
                        foreach (var clip in clips)
                        {
                            AppendClip(trackPath, clipIndex, clip);
                            clipIndex++;
                        }
                    }
                }

                var childTracks = ReflectionInvokeEnumerable(track, "GetChildTracks");
                if (childTracks == null)
                {
                    return;
                }

                var childIndex = 0;
                foreach (var childTrack in childTracks)
                {
                    var childName = ObjectName(childTrack);
                    var childPath = trackPath + "/" + (string.IsNullOrEmpty(childName) ? "Track" + childIndex.ToString(CultureInfo.InvariantCulture) : childName);
                    InspectTrack(childTrack, childPath, childIndex, depth + 1);
                    childIndex++;
                }
            }

            void AppendTrack(object track, string trackPath, int trackIndex, int depth)
            {
                if (TrackCount > MaxTracks)
                {
                    TracksTruncated = true;
                    return;
                }

                if (TracksJson.Length > 0)
                {
                    TracksJson.Append(',');
                }

                var trackObject = track as UnityEngine.Object;
                TracksJson.Append("{\"selector\":{\"track_path\":").Append(JsonUtil.String(trackPath))
                    .Append(",\"track_index\":").Append(trackIndex).Append('}')
                    .Append(",\"name\":").Append(JsonUtil.String(ObjectName(track)))
                    .Append(",\"type\":").Append(JsonUtil.String(track.GetType().FullName ?? track.GetType().Name))
                    .Append(",\"depth\":").Append(depth)
                    .Append(",\"muted\":").Append(ReflectionBoolJson(track, "muted"))
                    .Append(",\"muted_in_hierarchy\":").Append(ReflectionBoolJson(track, "mutedInHierarchy"))
                    .Append(",\"locked\":").Append(ReflectionBoolJson(track, "locked"))
                    .Append(",\"track_asset\":").Append(ObjectReferenceJson(trackObject))
                    .Append('}');
            }

            void AppendClip(string trackPath, int clipIndex, object clip)
            {
                if (clip == null)
                {
                    return;
                }

                ClipCount++;
                if (ClipCount > MaxClips)
                {
                    ClipsTruncated = true;
                    return;
                }

                var clipAsset = ReflectionPropertyValue(clip, "asset") as UnityEngine.Object;
                if (clipAsset != null)
                {
                    ClipAssetReferenceCount++;
                }

                if (ClipsJson.Length > 0)
                {
                    ClipsJson.Append(',');
                }

                ClipsJson.Append("{\"selector\":{\"track_path\":").Append(JsonUtil.String(trackPath))
                    .Append(",\"clip_index\":").Append(clipIndex).Append('}')
                    .Append(",\"display_name\":").Append(ReflectionString(clip, "displayName", MaxStringChars))
                    .Append(",\"type\":").Append(JsonUtil.String(clip.GetType().FullName ?? clip.GetType().Name))
                    .Append(",\"start\":").Append(ReflectionDoubleJson(clip, "start"))
                    .Append(",\"duration\":").Append(ReflectionDoubleJson(clip, "duration"))
                    .Append(",\"end\":").Append(ReflectionDoubleJson(clip, "end"))
                    .Append(",\"clip_in\":").Append(ReflectionDoubleJson(clip, "clipIn"))
                    .Append(",\"time_scale\":").Append(ReflectionDoubleJson(clip, "timeScale"))
                    .Append(",\"asset\":").Append(ObjectReferenceJson(clipAsset))
                    .Append('}');
            }

            static string ObjectName(object value)
            {
                if (value is UnityEngine.Object unityObject)
                {
                    return unityObject.name ?? string.Empty;
                }

                return Convert.ToString(ReflectionPropertyValue(value, "name"), CultureInfo.InvariantCulture) ?? string.Empty;
            }
        }

        sealed class GameplayDataReferencesReportState
        {
            readonly int MaxSources;
            readonly int MaxReferencesPerSource;
            readonly int MaxDepth;
            readonly int MaxArrayElements;
            readonly bool IncludeManagedReferences;

            public GameplayDataReferencesReportState(int maxSources, int maxReferencesPerSource, int maxDepth, int maxArrayElements, bool includeManagedReferences)
            {
                MaxSources = maxSources;
                MaxReferencesPerSource = maxReferencesPerSource;
                MaxDepth = maxDepth;
                MaxArrayElements = maxArrayElements;
                IncludeManagedReferences = includeManagedReferences;
            }

            public int SourceCount { get; private set; }
            public bool SourcesTruncated { get; private set; }
            public StringBuilder SourcesJson { get; } = new StringBuilder();
            public int ObjectReferenceCount { get; private set; }
            public int MissingObjectReferenceCount { get; private set; }
            public int ManagedReferenceCount { get; private set; }
            public int ManagedReferenceMissingOrUnresolvedCount { get; private set; }

            public void InspectSource(string assetPath)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath) as ScriptableObject;
                if (asset == null)
                {
                    return;
                }

                SourceCount++;
                if (SourceCount > MaxSources)
                {
                    SourcesTruncated = true;
                    return;
                }

                SerializedObject serializedObject;
                try
                {
                    serializedObject = new SerializedObject(asset);
                    serializedObject.Update();
                }
                catch (Exception)
                {
                    AppendSource(assetPath, asset, "serialization_unavailable", 0, 0, 0, 0, string.Empty, false);
                    return;
                }

                var referencesJson = new StringBuilder();
                var referenceSamples = 0;
                var sourceObjectReferences = 0;
                var sourceMissingObjectReferences = 0;
                var sourceManagedReferences = 0;
                var sourceMissingManagedReferences = 0;
                var referencesTruncated = false;
                var iterator = serializedObject.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = iterator.depth < MaxDepth;
                    if (iterator.depth > MaxDepth || IsArrayElementPastLimit(iterator.propertyPath, MaxArrayElements))
                    {
                        referencesTruncated = true;
                        continue;
                    }

                    if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        sourceObjectReferences++;
                        ObjectReferenceCount++;
                        var missing = ObjectReferenceIsMissing(iterator);
                        if (missing)
                        {
                            sourceMissingObjectReferences++;
                            MissingObjectReferenceCount++;
                        }

                        AppendReferenceSample(referencesJson, ref referenceSamples, ref referencesTruncated, iterator.propertyPath, "object_reference", ObjectReferencePropertyJson(iterator));
                    }
                    else if (IncludeManagedReferences && iterator.propertyType == SerializedPropertyType.ManagedReference)
                    {
                        sourceManagedReferences++;
                        ManagedReferenceCount++;
                        var managedReferenceJson = ManagedReferenceJson(iterator, out var status);
                        if (!string.Equals(status, "ok", StringComparison.Ordinal) && !string.Equals(status, "null", StringComparison.Ordinal))
                        {
                            sourceMissingManagedReferences++;
                            ManagedReferenceMissingOrUnresolvedCount++;
                        }

                        AppendReferenceSample(referencesJson, ref referenceSamples, ref referencesTruncated, iterator.propertyPath, "managed_reference", managedReferenceJson);
                    }
                }

                AppendSource(assetPath, asset, "ok", sourceObjectReferences, sourceMissingObjectReferences, sourceManagedReferences, sourceMissingManagedReferences, referencesJson.ToString(), referencesTruncated);
            }

            void AppendReferenceSample(StringBuilder builder, ref int referenceSamples, ref bool referencesTruncated, string propertyPath, string referenceKind, string referenceJson)
            {
                if (referenceSamples >= MaxReferencesPerSource)
                {
                    referencesTruncated = true;
                    return;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"property_path\":").Append(JsonUtil.String(propertyPath))
                    .Append(",\"reference_kind\":").Append(JsonUtil.String(referenceKind))
                    .Append(",\"reference\":").Append(referenceJson)
                    .Append('}');
                referenceSamples++;
            }

            void AppendSource(string assetPath, ScriptableObject asset, string status, int objectReferenceCount, int missingObjectReferenceCount, int managedReferenceCount, int missingManagedReferenceCount, string referencesJson, bool referencesTruncated)
            {
                if (SourcesJson.Length > 0)
                {
                    SourcesJson.Append(',');
                }

                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                SourcesJson.Append("{\"selector\":{\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"guid\":").Append(JsonUtil.String(guid)).Append('}')
                    .Append(",\"status\":").Append(JsonUtil.String(status))
                    .Append(",\"asset\":").Append(AssetJson(assetPath, guid, asset))
                    .Append(",\"object_reference_count\":").Append(objectReferenceCount)
                    .Append(",\"missing_object_reference_count\":").Append(missingObjectReferenceCount)
                    .Append(",\"managed_reference_count\":").Append(managedReferenceCount)
                    .Append(",\"managed_reference_missing_or_unresolved_count\":").Append(missingManagedReferenceCount)
                    .Append(",\"references_truncated\":").Append(JsonUtil.Bool(referencesTruncated))
                    .Append(",\"references\":[").Append(referencesJson).Append("]}");
            }
        }

        sealed class GameplayDataValidationReportState
        {
            readonly int MaxSources;
            readonly int MaxIssuesPerSource;
            readonly int MaxDepth;
            readonly int MaxArrayElements;
            readonly bool IncludeManagedReferences;

            public GameplayDataValidationReportState(int maxSources, int maxIssuesPerSource, int maxDepth, int maxArrayElements, bool includeManagedReferences)
            {
                MaxSources = maxSources;
                MaxIssuesPerSource = maxIssuesPerSource;
                MaxDepth = maxDepth;
                MaxArrayElements = maxArrayElements;
                IncludeManagedReferences = includeManagedReferences;
            }

            public int SourceCount { get; private set; }
            public bool SourcesTruncated { get; private set; }
            public int SkippedNonScriptableObjectCount { get; private set; }
            public int SourceWithIssuesCount { get; private set; }
            public int IssueCount { get; private set; }
            public int SerializationUnavailableCount { get; private set; }
            public int MissingScriptAssetCount { get; private set; }
            public int ObjectReferenceCount { get; private set; }
            public int MissingObjectReferenceCount { get; private set; }
            public int ManagedReferenceCount { get; private set; }
            public int ManagedReferenceNullCount { get; private set; }
            public int ManagedReferenceMissingOrUnresolvedCount { get; private set; }
            public int TraversalTruncatedSourceCount { get; private set; }
            public StringBuilder SourcesJson { get; } = new StringBuilder();

            public void InspectSource(string assetPath)
            {
                var asset = AssetDatabase.LoadMainAssetAtPath(assetPath) as ScriptableObject;
                if (asset == null)
                {
                    SkippedNonScriptableObjectCount++;
                    return;
                }

                SourceCount++;
                if (SourceCount > MaxSources)
                {
                    SourcesTruncated = true;
                    return;
                }

                var issuesJson = new StringBuilder();
                var issueSamples = 0;
                var sourceIssueCount = 0;
                var issuesTruncated = false;
                var sourceObjectReferences = 0;
                var sourceMissingObjectReferences = 0;
                var sourceManagedReferences = 0;
                var sourceManagedReferenceNulls = 0;
                var sourceMissingManagedReferences = 0;
                var traversalTruncated = false;
                var scriptAssetPresent = MonoScript.FromScriptableObject(asset) != null;
                if (!scriptAssetPresent)
                {
                    MissingScriptAssetCount++;
                    AppendIssue(issuesJson, ref issueSamples, ref issuesTruncated, ref sourceIssueCount, "missing_script_asset", "error", string.Empty, string.Empty);
                }

                SerializedObject serializedObject;
                try
                {
                    serializedObject = new SerializedObject(asset);
                    serializedObject.Update();
                }
                catch (Exception exception)
                {
                    SerializationUnavailableCount++;
                    AppendIssue(issuesJson, ref issueSamples, ref issuesTruncated, ref sourceIssueCount, "serialization_unavailable", "error", string.Empty, "\"exception_type\":" + JsonUtil.String(exception.GetType().Name));
                    AppendSource(assetPath, asset, "serialization_unavailable", scriptAssetPresent, sourceObjectReferences, sourceMissingObjectReferences, sourceManagedReferences, sourceManagedReferenceNulls, sourceMissingManagedReferences, sourceIssueCount, issuesTruncated, false, issuesJson.ToString());
                    return;
                }

                var iterator = serializedObject.GetIterator();
                var enterChildren = true;
                while (iterator.NextVisible(enterChildren))
                {
                    enterChildren = iterator.depth < MaxDepth;
                    if (iterator.depth > MaxDepth || IsArrayElementPastLimit(iterator.propertyPath, MaxArrayElements))
                    {
                        traversalTruncated = true;
                        continue;
                    }

                    if (iterator.propertyType == SerializedPropertyType.ObjectReference)
                    {
                        sourceObjectReferences++;
                        ObjectReferenceCount++;
                        var missing = ObjectReferenceIsMissing(iterator);
                        if (missing)
                        {
                            sourceMissingObjectReferences++;
                            MissingObjectReferenceCount++;
                            AppendIssue(issuesJson, ref issueSamples, ref issuesTruncated, ref sourceIssueCount, "missing_object_reference", "error", iterator.propertyPath, ObjectReferenceIssueIdentityJson(iterator));
                        }
                    }
                    else if (IncludeManagedReferences && iterator.propertyType == SerializedPropertyType.ManagedReference)
                    {
                        sourceManagedReferences++;
                        ManagedReferenceCount++;
                        ManagedReferenceMetadata(iterator, out var status, out var referenceId, out var fullTypename, out var fieldTypename);
                        if (string.Equals(status, "null", StringComparison.Ordinal))
                        {
                            sourceManagedReferenceNulls++;
                            ManagedReferenceNullCount++;
                        }
                        else if (!string.Equals(status, "ok", StringComparison.Ordinal))
                        {
                            sourceMissingManagedReferences++;
                            ManagedReferenceMissingOrUnresolvedCount++;
                            AppendIssue(issuesJson, ref issueSamples, ref issuesTruncated, ref sourceIssueCount, "managed_reference_missing_or_unresolved", "error", iterator.propertyPath, "\"managed_reference_status\":" + JsonUtil.String(status)
                                + ",\"managed_reference_id\":" + referenceId.ToString(CultureInfo.InvariantCulture)
                                + ",\"managed_reference_full_typename\":" + JsonUtil.String(fullTypename)
                                + ",\"managed_reference_field_typename\":" + JsonUtil.String(fieldTypename));
                        }
                    }
                }

                if (traversalTruncated)
                {
                    TraversalTruncatedSourceCount++;
                    AppendIssue(issuesJson, ref issueSamples, ref issuesTruncated, ref sourceIssueCount, "validation_traversal_truncated", "warning", string.Empty, "\"max_depth\":" + MaxDepth + ",\"max_array_elements\":" + MaxArrayElements);
                }

                AppendSource(assetPath, asset, sourceIssueCount > 0 ? "issues_found" : "ok", scriptAssetPresent, sourceObjectReferences, sourceMissingObjectReferences, sourceManagedReferences, sourceManagedReferenceNulls, sourceMissingManagedReferences, sourceIssueCount, issuesTruncated, traversalTruncated, issuesJson.ToString());
            }

            void AppendIssue(StringBuilder builder, ref int issueSamples, ref bool issuesTruncated, ref int sourceIssueCount, string kind, string severity, string propertyPath, string detailsJson)
            {
                sourceIssueCount++;
                IssueCount++;
                if (issueSamples >= MaxIssuesPerSource)
                {
                    issuesTruncated = true;
                    return;
                }

                if (builder.Length > 0)
                {
                    builder.Append(',');
                }

                builder.Append("{\"kind\":").Append(JsonUtil.String(kind))
                    .Append(",\"severity\":").Append(JsonUtil.String(severity))
                    .Append(",\"property_path\":").Append(JsonUtil.String(propertyPath));
                if (!string.IsNullOrEmpty(detailsJson))
                {
                    builder.Append(',').Append(detailsJson);
                }

                builder.Append('}');
                issueSamples++;
            }

            void AppendSource(string assetPath, ScriptableObject asset, string status, bool scriptAssetPresent, int objectReferenceCount, int missingObjectReferenceCount, int managedReferenceCount, int managedReferenceNullCount, int missingManagedReferenceCount, int issueCount, bool issuesTruncated, bool traversalTruncated, string issuesJson)
            {
                if (issueCount > 0)
                {
                    SourceWithIssuesCount++;
                }

                if (SourcesJson.Length > 0)
                {
                    SourcesJson.Append(',');
                }

                var guid = AssetDatabase.AssetPathToGUID(assetPath);
                SourcesJson.Append("{\"selector\":{\"asset_path\":").Append(JsonUtil.String(assetPath))
                    .Append(",\"guid\":").Append(JsonUtil.String(guid)).Append('}')
                    .Append(",\"status\":").Append(JsonUtil.String(status))
                    .Append(",\"asset\":").Append(AssetJson(assetPath, guid, asset))
                    .Append(",\"script_asset_present\":").Append(JsonUtil.Bool(scriptAssetPresent))
                    .Append(",\"object_reference_count\":").Append(objectReferenceCount)
                    .Append(",\"missing_object_reference_count\":").Append(missingObjectReferenceCount)
                    .Append(",\"managed_reference_count\":").Append(managedReferenceCount)
                    .Append(",\"managed_reference_null_count\":").Append(managedReferenceNullCount)
                    .Append(",\"managed_reference_missing_or_unresolved_count\":").Append(missingManagedReferenceCount)
                    .Append(",\"issue_count\":").Append(issueCount)
                    .Append(",\"issues_truncated\":").Append(JsonUtil.Bool(issuesTruncated))
                    .Append(",\"traversal_truncated\":").Append(JsonUtil.Bool(traversalTruncated))
                    .Append(",\"issues\":[").Append(issuesJson).Append("]}");
            }
        }

        sealed class AnimatorControllerInspector
        {
            readonly int MaxLayers;
            readonly int MaxStates;
            readonly int MaxTransitions;
            readonly int MaxConditions;
            readonly int MaxStateBehaviours;
            readonly int MaxDepth;
            readonly bool IncludeLayers;
            readonly bool IncludeStates;
            readonly bool IncludeTransitions;
            readonly bool IncludeMotions;

            public AnimatorControllerInspector(int maxLayers, int maxStates, int maxTransitions, int maxConditions, int maxStateBehaviours, int maxDepth, bool includeLayers, bool includeStates, bool includeTransitions, bool includeMotions)
            {
                MaxLayers = maxLayers;
                MaxStates = maxStates;
                MaxTransitions = maxTransitions;
                MaxConditions = maxConditions;
                MaxStateBehaviours = maxStateBehaviours;
                MaxDepth = maxDepth;
                IncludeLayers = includeLayers;
                IncludeStates = includeStates;
                IncludeTransitions = includeTransitions;
                IncludeMotions = includeMotions;
            }

            public int LayerCount { get; private set; }
            public bool LayersTruncated { get; private set; }
            public StringBuilder LayersJson { get; } = new StringBuilder();
            public int StateCount { get; private set; }
            public bool StatesTruncated { get; private set; }
            public StringBuilder StatesJson { get; } = new StringBuilder();
            public int TransitionCount { get; private set; }
            public bool TransitionsTruncated { get; private set; }
            public StringBuilder TransitionsJson { get; } = new StringBuilder();
            public int ConditionCount { get; private set; }
            public bool ConditionsTruncated { get; private set; }
            public int StateBehaviourCount { get; private set; }
            public int MissingStateBehaviourCount { get; private set; }
            public bool StateBehavioursTruncated { get; private set; }

            public void Inspect(AnimatorController controller)
            {
                var layers = controller.layers ?? new AnimatorControllerLayer[0];
                if (IncludeLayers)
                {
                    LayerCount = layers.Length;
                    LayersTruncated = layers.Length > MaxLayers;
                }

                var returnedLayers = Math.Min(layers.Length, MaxLayers);
                if (layers.Length > MaxLayers)
                {
                    if (IncludeStates)
                    {
                        StatesTruncated = true;
                    }

                    if (IncludeTransitions)
                    {
                        TransitionsTruncated = true;
                    }
                }

                for (var index = 0; index < returnedLayers; index++)
                {
                    var layer = layers[index];
                    var layerName = string.IsNullOrEmpty(layer.name) ? "Layer" + index.ToString(CultureInfo.InvariantCulture) : layer.name;
                    if (IncludeLayers)
                    {
                        AppendLayer(index, layerName, layer);
                    }

                    if (IncludeStates || IncludeTransitions)
                    {
                        AppendStateMachine(index, layerName, layer.stateMachine, layerName, 0);
                    }
                }
            }

            void AppendLayer(int layerIndex, string layerName, AnimatorControllerLayer layer)
            {
                if (LayersJson.Length > 0)
                {
                    LayersJson.Append(',');
                }

                LayersJson.Append("{\"selector\":{\"layer_index\":").Append(layerIndex)
                    .Append(",\"layer_name\":").Append(JsonUtil.String(layerName)).Append('}')
                    .Append(",\"name\":").Append(JsonUtil.String(layerName))
                    .Append(",\"default_weight\":").Append(FloatJson(layer.defaultWeight))
                    .Append(",\"blending_mode\":").Append(JsonUtil.String(layer.blendingMode.ToString()))
                    .Append(",\"synced_layer_index\":").Append(layer.syncedLayerIndex)
                    .Append(",\"ik_pass\":").Append(JsonUtil.Bool(layer.iKPass))
                    .Append(",\"avatar_mask\":").Append(ObjectReferenceJson(layer.avatarMask))
                    .Append(",\"default_state_path\":").Append(JsonUtil.String(DefaultStatePath(layerName, layer.stateMachine)))
                    .Append('}');
            }

            void AppendStateMachine(int layerIndex, string layerName, AnimatorStateMachine stateMachine, string stateMachinePath, int depth)
            {
                if (stateMachine == null)
                {
                    return;
                }

                if (depth > MaxDepth)
                {
                    if (IncludeStates)
                    {
                        StatesTruncated = true;
                    }

                    if (IncludeTransitions)
                    {
                        TransitionsTruncated = true;
                    }

                    return;
                }

                if (IncludeTransitions)
                {
                    AppendTransitions(stateMachine.anyStateTransitions, "any_state", stateMachinePath + "/AnyState");
                    AppendTransitions(stateMachine.entryTransitions, "entry", stateMachinePath + "/Entry");
                }

                var childStates = stateMachine.states ?? new ChildAnimatorState[0];
                Array.Sort(childStates, CompareChildAnimatorState);
                for (var index = 0; index < childStates.Length; index++)
                {
                    var state = childStates[index].state;
                    if (state == null)
                    {
                        continue;
                    }

                    var statePath = stateMachinePath + "/" + state.name;
                    if (IncludeStates)
                    {
                        AppendState(layerIndex, layerName, stateMachinePath, statePath, state);
                    }

                    if (IncludeTransitions)
                    {
                        AppendTransitions(state.transitions, "state", statePath);
                    }
                }

                var childStateMachines = stateMachine.stateMachines ?? new ChildAnimatorStateMachine[0];
                Array.Sort(childStateMachines, CompareChildAnimatorStateMachine);
                for (var index = 0; index < childStateMachines.Length; index++)
                {
                    var childStateMachine = childStateMachines[index].stateMachine;
                    if (childStateMachine == null)
                    {
                        continue;
                    }

                    AppendStateMachine(layerIndex, layerName, childStateMachine, stateMachinePath + "/" + childStateMachine.name, depth + 1);
                }
            }

            void AppendState(int layerIndex, string layerName, string stateMachinePath, string statePath, AnimatorState state)
            {
                StateCount++;
                if (StateCount > MaxStates)
                {
                    StatesTruncated = true;
                    return;
                }

                if (StatesJson.Length > 0)
                {
                    StatesJson.Append(',');
                }

                StatesJson.Append("{\"selector\":{\"layer_index\":").Append(layerIndex)
                    .Append(",\"layer_name\":").Append(JsonUtil.String(layerName))
                    .Append(",\"state_path\":").Append(JsonUtil.String(statePath)).Append('}')
                    .Append(",\"state_machine_path\":").Append(JsonUtil.String(stateMachinePath))
                    .Append(",\"name\":").Append(JsonUtil.String(state.name ?? string.Empty))
                    .Append(",\"tag\":").Append(JsonUtil.String(state.tag ?? string.Empty))
                    .Append(",\"speed\":").Append(FloatJson(state.speed))
                    .Append(",\"cycle_offset\":").Append(FloatJson(state.cycleOffset))
                    .Append(",\"mirror\":").Append(JsonUtil.Bool(state.mirror))
                    .Append(",\"ik_on_feet\":").Append(JsonUtil.Bool(state.iKOnFeet))
                    .Append(",\"write_default_values\":").Append(JsonUtil.Bool(state.writeDefaultValues))
                    .Append(",\"motion\":").Append(IncludeMotions ? MotionJson(state.motion) : "null")
                    .Append(",\"behaviours\":[").Append(StateBehavioursJson(statePath, state.behaviours)).Append("]}");
            }

            void AppendTransitions(AnimatorTransitionBase[] transitions, string sourceKind, string sourcePath)
            {
                if (transitions == null)
                {
                    return;
                }

                for (var index = 0; index < transitions.Length; index++)
                {
                    var transition = transitions[index];
                    if (transition == null)
                    {
                        continue;
                    }

                    TransitionCount++;
                    if (TransitionCount > MaxTransitions)
                    {
                        TransitionsTruncated = true;
                        continue;
                    }

                    if (TransitionsJson.Length > 0)
                    {
                        TransitionsJson.Append(',');
                    }

                    TransitionsJson.Append("{\"selector\":{\"source_kind\":").Append(JsonUtil.String(sourceKind))
                        .Append(",\"source_path\":").Append(JsonUtil.String(sourcePath))
                        .Append(",\"transition_index\":").Append(index).Append('}')
                        .Append(",\"source_kind\":").Append(JsonUtil.String(sourceKind))
                        .Append(",\"source_path\":").Append(JsonUtil.String(sourcePath))
                        .Append(",\"destination\":").Append(TransitionDestinationJson(transition))
                        .Append(",\"is_exit\":").Append(JsonUtil.Bool(transition.isExit))
                        .Append(",\"mute\":").Append(JsonUtil.Bool(transition.mute))
                        .Append(",\"solo\":").Append(JsonUtil.Bool(transition.solo));

                    var stateTransition = transition as AnimatorStateTransition;
                    if (stateTransition != null)
                    {
                        TransitionsJson.Append(",\"state_transition\":{\"has_exit_time\":").Append(JsonUtil.Bool(stateTransition.hasExitTime))
                            .Append(",\"exit_time\":").Append(FloatJson(stateTransition.exitTime))
                            .Append(",\"duration\":").Append(FloatJson(stateTransition.duration))
                            .Append(",\"offset\":").Append(FloatJson(stateTransition.offset))
                            .Append(",\"has_fixed_duration\":").Append(JsonUtil.Bool(stateTransition.hasFixedDuration))
                            .Append(",\"can_transition_to_self\":").Append(JsonUtil.Bool(stateTransition.canTransitionToSelf))
                            .Append('}');
                    }
                    else
                    {
                        TransitionsJson.Append(",\"state_transition\":null");
                    }

                    TransitionsJson.Append(",\"conditions\":[").Append(ConditionsJson(transition.conditions)).Append("]}");
                }
            }

            string ConditionsJson(AnimatorCondition[] conditions)
            {
                if (conditions == null || conditions.Length == 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                for (var index = 0; index < conditions.Length; index++)
                {
                    ConditionCount++;
                    if (ConditionCount > MaxConditions)
                    {
                        ConditionsTruncated = true;
                        continue;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append(',');
                    }

                    var condition = conditions[index];
                    builder.Append("{\"condition_index\":").Append(index)
                        .Append(",\"parameter\":").Append(JsonUtil.String(condition.parameter ?? string.Empty))
                        .Append(",\"mode\":").Append(JsonUtil.String(condition.mode.ToString()))
                        .Append(",\"threshold\":").Append(FloatJson(condition.threshold))
                        .Append('}');
                }

                return builder.ToString();
            }

            string StateBehavioursJson(string statePath, StateMachineBehaviour[] behaviours)
            {
                if (behaviours == null || behaviours.Length == 0)
                {
                    return string.Empty;
                }

                var builder = new StringBuilder();
                for (var index = 0; index < behaviours.Length; index++)
                {
                    StateBehaviourCount++;
                    if (StateBehaviourCount > MaxStateBehaviours)
                    {
                        StateBehavioursTruncated = true;
                        continue;
                    }

                    if (builder.Length > 0)
                    {
                        builder.Append(',');
                    }

                    var behaviour = behaviours[index];
                    if (behaviour == null)
                    {
                        MissingStateBehaviourCount++;
                        builder.Append("{\"selector\":{\"state_path\":").Append(JsonUtil.String(statePath))
                            .Append(",\"behaviour_index\":").Append(index).Append('}')
                            .Append(",\"missing\":true,\"type\":\"\",\"name\":\"\"}");
                        continue;
                    }

                    var type = behaviour.GetType();
                    builder.Append("{\"selector\":{\"state_path\":").Append(JsonUtil.String(statePath))
                        .Append(",\"behaviour_index\":").Append(index).Append('}')
                        .Append(",\"missing\":false")
                        .Append(",\"type\":").Append(JsonUtil.String(type.FullName ?? type.Name))
                        .Append(",\"name\":").Append(JsonUtil.String(behaviour.name ?? string.Empty))
                        .Append('}');
                }

                return builder.ToString();
            }

            static string DefaultStatePath(string layerName, AnimatorStateMachine stateMachine)
            {
                if (stateMachine == null || stateMachine.defaultState == null)
                {
                    return string.Empty;
                }

                return layerName + "/" + stateMachine.defaultState.name;
            }

            static string MotionJson(Motion motion)
            {
                if (motion == null)
                {
                    return "null";
                }

                return "{\"reference\":" + ObjectReferenceJson(motion)
                    + ",\"motion_kind\":" + JsonUtil.String(motion is AnimationClip ? "animation_clip" : (motion is BlendTree ? "blend_tree" : "motion"))
                    + "}";
            }

            static string TransitionDestinationJson(AnimatorTransitionBase transition)
            {
                if (transition.destinationState != null)
                {
                    return "{\"kind\":\"state\",\"state_name\":" + JsonUtil.String(transition.destinationState.name ?? string.Empty) + "}";
                }

                if (transition.destinationStateMachine != null)
                {
                    return "{\"kind\":\"state_machine\",\"state_machine_name\":" + JsonUtil.String(transition.destinationStateMachine.name ?? string.Empty) + "}";
                }

                return transition.isExit ? "{\"kind\":\"exit\"}" : "{\"kind\":\"none\"}";
            }

            static int CompareChildAnimatorState(ChildAnimatorState left, ChildAnimatorState right)
            {
                return string.Compare(left.state == null ? string.Empty : left.state.name, right.state == null ? string.Empty : right.state.name, StringComparison.Ordinal);
            }

            static int CompareChildAnimatorStateMachine(ChildAnimatorStateMachine left, ChildAnimatorStateMachine right)
            {
                return string.Compare(left.stateMachine == null ? string.Empty : left.stateMachine.name, right.stateMachine == null ? string.Empty : right.stateMachine.name, StringComparison.Ordinal);
            }
        }

        sealed class AnimatorControllerGraphBuilder
        {
            readonly int MaxNodes;
            readonly int MaxEdges;
            readonly int MaxLayers;
            readonly int MaxDepth;
            readonly int MaxConditionsPerTransition;
            readonly bool IncludeMotionNodes;
            readonly bool IncludeParameterNodes;
            readonly HashSet<string> SeenNodes = new HashSet<string>(StringComparer.Ordinal);
            readonly Dictionary<AnimatorState, string> StateIds = new Dictionary<AnimatorState, string>();
            readonly Dictionary<AnimatorStateMachine, string> StateMachineIds = new Dictionary<AnimatorStateMachine, string>();
            readonly List<TransitionRecord> Transitions = new List<TransitionRecord>();

            public AnimatorControllerGraphBuilder(int maxNodes, int maxEdges, int maxLayers, int maxDepth, int maxConditionsPerTransition, bool includeMotionNodes, bool includeParameterNodes)
            {
                MaxNodes = maxNodes;
                MaxEdges = maxEdges;
                MaxLayers = maxLayers;
                MaxDepth = maxDepth;
                MaxConditionsPerTransition = maxConditionsPerTransition;
                IncludeMotionNodes = includeMotionNodes;
                IncludeParameterNodes = includeParameterNodes;
            }

            public int NodeCount { get; private set; }
            public bool NodesTruncated { get; private set; }
            public StringBuilder NodesJson { get; } = new StringBuilder();
            public int EdgeCount { get; private set; }
            public bool EdgesTruncated { get; private set; }
            public StringBuilder EdgesJson { get; } = new StringBuilder();
            public int LayerCount { get; private set; }
            public bool LayersTruncated { get; private set; }
            public int TransitionCount { get; private set; }
            public bool ConditionsTruncated { get; private set; }

            public void Build(AnimatorController controller)
            {
                AddNode("controller", "controller", controller.name, "{\"asset_name\":" + JsonUtil.String(controller.name) + "}");
                if (IncludeParameterNodes)
                {
                    var parameters = controller.parameters ?? new AnimatorControllerParameter[0];
                    for (var index = 0; index < parameters.Length; index++)
                    {
                        var parameter = parameters[index];
                        var parameterId = ParameterNodeId(parameter.name);
                        AddNode(parameterId, "parameter", parameter.name ?? string.Empty, "{\"parameter_name\":" + JsonUtil.String(parameter.name ?? string.Empty) + ",\"parameter_index\":" + index + ",\"type\":" + JsonUtil.String(parameter.type.ToString()) + "}");
                        AddEdge("controller", parameterId, "declares_parameter", "{\"parameter_name\":" + JsonUtil.String(parameter.name ?? string.Empty) + "}");
                    }
                }

                var layers = controller.layers ?? new AnimatorControllerLayer[0];
                LayerCount = layers.Length;
                LayersTruncated = layers.Length > MaxLayers;
                var returnedLayers = Math.Min(layers.Length, MaxLayers);
                for (var index = 0; index < returnedLayers; index++)
                {
                    var layer = layers[index];
                    var layerName = string.IsNullOrEmpty(layer.name) ? "Layer" + index.ToString(CultureInfo.InvariantCulture) : layer.name;
                    var layerId = LayerNodeId(index);
                    AddNode(layerId, "layer", layerName, "{\"layer_index\":" + index + ",\"layer_name\":" + JsonUtil.String(layerName) + "}");
                    AddEdge("controller", layerId, "has_layer", "{\"layer_index\":" + index + "}");
                    AppendStateMachine(index, layerName, layer.stateMachine, layerName, layerId, 0);
                    if (layer.stateMachine != null && layer.stateMachine.defaultState != null && StateIds.TryGetValue(layer.stateMachine.defaultState, out var defaultStateId))
                    {
                        AddEdge(layerId, defaultStateId, "default_state", "{\"layer_index\":" + index + "}");
                    }
                }

                for (var index = 0; index < Transitions.Count; index++)
                {
                    AppendTransitionEdge(index, Transitions[index]);
                }
            }

            void AppendStateMachine(int layerIndex, string layerName, AnimatorStateMachine stateMachine, string stateMachinePath, string parentId, int depth)
            {
                if (stateMachine == null)
                {
                    return;
                }

                if (depth > MaxDepth)
                {
                    NodesTruncated = true;
                    EdgesTruncated = true;
                    return;
                }

                var stateMachineId = StateMachineNodeId(layerIndex, stateMachinePath);
                StateMachineIds[stateMachine] = stateMachineId;
                AddNode(stateMachineId, "state_machine", stateMachine.name ?? stateMachinePath, "{\"layer_index\":" + layerIndex + ",\"layer_name\":" + JsonUtil.String(layerName) + ",\"state_machine_path\":" + JsonUtil.String(stateMachinePath) + "}");
                AddEdge(parentId, stateMachineId, parentId.StartsWith("layer:", StringComparison.Ordinal) ? "has_root_state_machine" : "has_child_state_machine", "{\"state_machine_path\":" + JsonUtil.String(stateMachinePath) + "}");

                var anyStateId = stateMachineId + ":AnyState";
                AddNode(anyStateId, "virtual_any_state", "AnyState", "{\"layer_index\":" + layerIndex + ",\"state_machine_path\":" + JsonUtil.String(stateMachinePath) + "}");
                var entryId = stateMachineId + ":Entry";
                AddNode(entryId, "virtual_entry", "Entry", "{\"layer_index\":" + layerIndex + ",\"state_machine_path\":" + JsonUtil.String(stateMachinePath) + "}");
                var exitId = stateMachineId + ":Exit";
                AddNode(exitId, "virtual_exit", "Exit", "{\"layer_index\":" + layerIndex + ",\"state_machine_path\":" + JsonUtil.String(stateMachinePath) + "}");

                AddTransitionRecords(stateMachine.anyStateTransitions, anyStateId, "any_state", stateMachinePath);
                AddTransitionRecords(stateMachine.entryTransitions, entryId, "entry", stateMachinePath);

                var childStates = stateMachine.states ?? new ChildAnimatorState[0];
                Array.Sort(childStates, CompareChildAnimatorState);
                for (var index = 0; index < childStates.Length; index++)
                {
                    var state = childStates[index].state;
                    if (state == null)
                    {
                        continue;
                    }

                    var statePath = stateMachinePath + "/" + state.name;
                    var stateId = StateNodeId(layerIndex, statePath);
                    StateIds[state] = stateId;
                    AddNode(stateId, "state", state.name ?? string.Empty, "{\"layer_index\":" + layerIndex + ",\"layer_name\":" + JsonUtil.String(layerName) + ",\"state_path\":" + JsonUtil.String(statePath) + "}");
                    AddEdge(stateMachineId, stateId, "has_state", "{\"state_path\":" + JsonUtil.String(statePath) + "}");
                    if (IncludeMotionNodes && state.motion != null)
                    {
                        var motionId = MotionNodeId(state.motion);
                        AddNode(motionId, state.motion is BlendTree ? "blend_tree" : "animation_clip", state.motion.name ?? string.Empty, "{\"motion\":" + ObjectReferenceJson(state.motion) + "}");
                        AddEdge(stateId, motionId, "uses_motion", "{\"state_path\":" + JsonUtil.String(statePath) + "}");
                    }

                    AddTransitionRecords(state.transitions, stateId, "state", statePath);
                }

                var childStateMachines = stateMachine.stateMachines ?? new ChildAnimatorStateMachine[0];
                Array.Sort(childStateMachines, CompareChildAnimatorStateMachine);
                for (var index = 0; index < childStateMachines.Length; index++)
                {
                    var childStateMachine = childStateMachines[index].stateMachine;
                    if (childStateMachine == null)
                    {
                        continue;
                    }

                    AppendStateMachine(layerIndex, layerName, childStateMachine, stateMachinePath + "/" + childStateMachine.name, stateMachineId, depth + 1);
                }
            }

            void AddTransitionRecords(AnimatorTransitionBase[] transitions, string sourceId, string sourceKind, string sourcePath)
            {
                if (transitions == null)
                {
                    return;
                }

                for (var index = 0; index < transitions.Length; index++)
                {
                    if (transitions[index] != null)
                    {
                        Transitions.Add(new TransitionRecord(sourceId, sourceKind, sourcePath, index, transitions[index]));
                    }
                }
            }

            void AppendTransitionEdge(int graphTransitionIndex, TransitionRecord record)
            {
                TransitionCount++;
                var targetId = TransitionTargetId(record.Transition, record.SourceId);
                var conditionsJson = ConditionsJson(record.Transition.conditions);
                AddEdge(record.SourceId, targetId, "transition", "{\"transition_index\":" + record.TransitionIndex
                    + ",\"graph_transition_index\":" + graphTransitionIndex
                    + ",\"source_kind\":" + JsonUtil.String(record.SourceKind)
                    + ",\"source_path\":" + JsonUtil.String(record.SourcePath)
                    + ",\"is_exit\":" + JsonUtil.Bool(record.Transition.isExit)
                    + ",\"conditions\":[" + conditionsJson + "]}");
            }

            string ConditionsJson(AnimatorCondition[] conditions)
            {
                if (conditions == null || conditions.Length == 0 || MaxConditionsPerTransition <= 0)
                {
                    if (conditions != null && conditions.Length > 0 && MaxConditionsPerTransition <= 0)
                    {
                        ConditionsTruncated = true;
                    }

                    return string.Empty;
                }

                var builder = new StringBuilder();
                var returned = Math.Min(conditions.Length, MaxConditionsPerTransition);
                if (conditions.Length > MaxConditionsPerTransition)
                {
                    ConditionsTruncated = true;
                }

                for (var index = 0; index < returned; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    var condition = conditions[index];
                    builder.Append("{\"condition_index\":").Append(index)
                        .Append(",\"parameter\":").Append(JsonUtil.String(condition.parameter ?? string.Empty))
                        .Append(",\"mode\":").Append(JsonUtil.String(condition.mode.ToString()))
                        .Append(",\"threshold\":").Append(FloatJson(condition.threshold))
                        .Append('}');
                    if (IncludeParameterNodes)
                    {
                        var parameterId = ParameterNodeId(condition.parameter);
                        AddNode(parameterId, "parameter", condition.parameter ?? string.Empty, "{\"parameter_name\":" + JsonUtil.String(condition.parameter ?? string.Empty) + "}");
                    }
                }

                return builder.ToString();
            }

            string TransitionTargetId(AnimatorTransitionBase transition, string sourceId)
            {
                if (transition.destinationState != null && StateIds.TryGetValue(transition.destinationState, out var stateId))
                {
                    return stateId;
                }

                if (transition.destinationStateMachine != null && StateMachineIds.TryGetValue(transition.destinationStateMachine, out var stateMachineId))
                {
                    return stateMachineId;
                }

                if (transition.isExit)
                {
                    var exitId = sourceId + ":Exit";
                    AddNode(exitId, "virtual_exit", "Exit", "{\"source_id\":" + JsonUtil.String(sourceId) + "}");
                    return exitId;
                }

                var missingId = "missing_destination:" + sourceId + ":" + TransitionCount.ToString(CultureInfo.InvariantCulture);
                AddNode(missingId, "missing_destination", "Missing destination", "{\"source_id\":" + JsonUtil.String(sourceId) + "}");
                return missingId;
            }

            bool AddNode(string id, string kind, string label, string selectorJson)
            {
                if (!SeenNodes.Add(id))
                {
                    return true;
                }

                NodeCount++;
                if (NodeCount > MaxNodes)
                {
                    NodesTruncated = true;
                    return false;
                }

                if (NodesJson.Length > 0)
                {
                    NodesJson.Append(',');
                }

                NodesJson.Append("{\"id\":").Append(JsonUtil.String(id))
                    .Append(",\"kind\":").Append(JsonUtil.String(kind))
                    .Append(",\"label\":").Append(JsonUtil.String(label ?? string.Empty))
                    .Append(",\"selector\":").Append(selectorJson)
                    .Append('}');
                return true;
            }

            void AddEdge(string sourceId, string targetId, string kind, string detailsJson)
            {
                EdgeCount++;
                if (EdgeCount > MaxEdges)
                {
                    EdgesTruncated = true;
                    return;
                }

                if (EdgesJson.Length > 0)
                {
                    EdgesJson.Append(',');
                }

                EdgesJson.Append("{\"selector\":{\"edge_index\":").Append(EdgeCount - 1)
                    .Append(",\"source_id\":").Append(JsonUtil.String(sourceId))
                    .Append(",\"target_id\":").Append(JsonUtil.String(targetId))
                    .Append(",\"kind\":").Append(JsonUtil.String(kind)).Append('}')
                    .Append(",\"source_id\":").Append(JsonUtil.String(sourceId))
                    .Append(",\"target_id\":").Append(JsonUtil.String(targetId))
                    .Append(",\"kind\":").Append(JsonUtil.String(kind))
                    .Append(",\"details\":").Append(detailsJson)
                    .Append('}');
            }

            static string LayerNodeId(int layerIndex)
            {
                return "layer:" + layerIndex.ToString(CultureInfo.InvariantCulture);
            }

            static string StateMachineNodeId(int layerIndex, string stateMachinePath)
            {
                return "state_machine:" + layerIndex.ToString(CultureInfo.InvariantCulture) + ":" + stateMachinePath;
            }

            static string StateNodeId(int layerIndex, string statePath)
            {
                return "state:" + layerIndex.ToString(CultureInfo.InvariantCulture) + ":" + statePath;
            }

            static string ParameterNodeId(string parameterName)
            {
                return "parameter:" + (parameterName ?? string.Empty);
            }

            static string MotionNodeId(Motion motion)
            {
                var path = AssetDatabase.GetAssetPath(motion);
                return "motion:" + (string.IsNullOrEmpty(path) ? ObjectIdentityKey(motion) : path + "#" + motion.name);
            }

            static int CompareChildAnimatorState(ChildAnimatorState left, ChildAnimatorState right)
            {
                return string.Compare(left.state == null ? string.Empty : left.state.name, right.state == null ? string.Empty : right.state.name, StringComparison.Ordinal);
            }

            static int CompareChildAnimatorStateMachine(ChildAnimatorStateMachine left, ChildAnimatorStateMachine right)
            {
                return string.Compare(left.stateMachine == null ? string.Empty : left.stateMachine.name, right.stateMachine == null ? string.Empty : right.stateMachine.name, StringComparison.Ordinal);
            }

            sealed class TransitionRecord
            {
                public TransitionRecord(string sourceId, string sourceKind, string sourcePath, int transitionIndex, AnimatorTransitionBase transition)
                {
                    SourceId = sourceId;
                    SourceKind = sourceKind;
                    SourcePath = sourcePath;
                    TransitionIndex = transitionIndex;
                    Transition = transition;
                }

                public string SourceId { get; }
                public string SourceKind { get; }
                public string SourcePath { get; }
                public int TransitionIndex { get; }
                public AnimatorTransitionBase Transition { get; }
            }
        }
    }
}
