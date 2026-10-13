using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class UitkInspectionCommands
    {
        const string InspectionFeatureFlag = "uitk_inspection";
        const string ValidationFeatureFlag = "uitk_validation";
        const string SafeWritesFeatureFlag = "uitk_safe_writes";
        const string MigrationFeatureFlag = "uitk_migration";
        const string MigrationPreviewFeatureFlag = "uitk_migration_preview";
        const string MigrationPlanArtifactRoot = "Library/SGGUnityMcp/job-artifacts/uitk-migration-plans/";
        const string MigrationPreviewArtifactRoot = "Library/SGGUnityMcp/job-artifacts/uitk-previews/";
        const string MigrationScanSchemaJson = "{\"type\":\"object\",\"properties\":{\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"include_inactive\":{\"type\":\"boolean\"},\"include_prefab_variants\":{\"type\":\"boolean\"},\"max_depth\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":64},\"max_nodes\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000},\"include_components\":{\"type\":\"boolean\"},\"include_events\":{\"type\":\"boolean\"},\"prefab_layout_overrides\":{\"type\":\"array\",\"items\":{\"type\":\"object\",\"properties\":{\"prefab_path\":{\"type\":\"string\"},\"root_path\":{\"type\":\"string\"},\"size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"parent_size\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"anchored_position\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":2,\"maxItems\":2},\"scale\":{\"type\":\"array\",\"items\":{\"type\":\"number\"},\"minItems\":3,\"maxItems\":3},\"note\":{\"type\":\"string\"}},\"required\":[\"prefab_path\",\"root_path\"],\"additionalProperties\":false}}},\"additionalProperties\":false}";
        const string MigrationPlanSchemaJson = "{\"type\":\"object\",\"properties\":{\"scan_id\":{\"type\":\"string\"},\"strategy\":{\"type\":\"string\",\"enum\":[\"absolute_fidelity\",\"semantic_flex_with_absolute_fallback\",\"report_only\"]},\"output_root\":{\"type\":\"string\"},\"target_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"persist\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"scan_id\",\"strategy\",\"output_root\",\"target_name\"],\"additionalProperties\":false}";
        const string MigrationGenerateSchemaJson = "{\"type\":\"object\",\"properties\":{\"plan_id\":{\"type\":\"string\"},\"mode\":{\"type\":\"string\",\"enum\":[\"create_only\"]},\"dry_run\":{\"type\":\"boolean\",\"default\":true},\"overwrite\":{\"type\":\"boolean\",\"default\":false}},\"required\":[\"plan_id\"],\"additionalProperties\":false}";
        const string MigrationPreviewSchemaJson = "{\"type\":\"object\",\"properties\":{\"generation_id\":{\"type\":\"string\"},\"migration_map_path\":{\"type\":\"string\"},\"source_scan_id\":{\"type\":\"string\"},\"source_screenshot_path\":{\"type\":\"string\",\"maxLength\":240},\"generated_screenshot_path\":{\"type\":\"string\",\"maxLength\":240},\"capture_screenshots\":{\"type\":\"boolean\",\"default\":false},\"capture_role\":{\"type\":\"string\",\"enum\":[\"missing\",\"source\",\"generated\",\"both\"],\"default\":\"missing\"},\"capture_include_overlay_ui\":{\"type\":\"boolean\"},\"capture_backend\":{\"type\":\"string\",\"enum\":[\"camera_render\",\"end_of_frame\",\"screen_capture\"]},\"capture_width\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"capture_height\":{\"type\":\"integer\",\"minimum\":64,\"maximum\":4096},\"capture_settle_frames\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":120},\"capture_timeout_ms\":{\"type\":\"integer\",\"minimum\":1000,\"maximum\":30000},\"pixel_threshold\":{\"type\":\"integer\",\"minimum\":0,\"maximum\":255,\"default\":8},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"additionalProperties\":false}";
        const string DocumentInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"include_inactive\":{\"type\":\"boolean\"},\"include_panel_settings\":{\"type\":\"boolean\"},\"include_visual_tree_asset\":{\"type\":\"boolean\"},\"max_documents\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string UxmlInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"uxml_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"include_tree\":{\"type\":\"boolean\"},\"include_names\":{\"type\":\"boolean\"},\"include_classes\":{\"type\":\"boolean\"},\"max_elements\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"required\":[\"uxml_paths\"],\"additionalProperties\":false}";
        const string UssInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"uss_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"include_selectors\":{\"type\":\"boolean\"},\"include_asset_references\":{\"type\":\"boolean\"},\"max_rules\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":5000}},\"required\":[\"uss_paths\"],\"additionalProperties\":false}";
        const string PanelSettingsInspectSchemaJson = "{\"type\":\"object\",\"properties\":{\"panel_settings_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"include_references\":{\"type\":\"boolean\"}},\"required\":[\"panel_settings_paths\"],\"additionalProperties\":false}";
        const string ValidateSchemaJson = "{\"type\":\"object\",\"properties\":{\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"uxml_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"uss_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"binder_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"generation_id\":{\"type\":\"string\"},\"migration_map_path\":{\"type\":\"string\"},\"run_compile_check\":{\"type\":\"boolean\"},\"validate_binder_queries\":{\"type\":\"boolean\"},\"max_findings\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string BindingsValidateSchemaJson = "{\"type\":\"object\",\"properties\":{\"binder_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"uxml_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"query_patterns\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":16},\"max_findings\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"required\":[\"binder_paths\",\"uxml_paths\"],\"additionalProperties\":false}";
        const string LayoutAnalyzeSchemaJson = "{\"type\":\"object\",\"properties\":{\"uxml_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"uss_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"screen_sizes\":{\"type\":\"array\",\"items\":{\"type\":\"array\",\"items\":{\"type\":\"integer\"},\"minItems\":2,\"maxItems\":2},\"maxItems\":16},\"include_absolute_positioning\":{\"type\":\"boolean\"},\"max_findings\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string ReferencesReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"uxml_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"uss_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"include_images\":{\"type\":\"boolean\"},\"include_fonts\":{\"type\":\"boolean\"},\"include_templates\":{\"type\":\"boolean\"},\"include_style_sheets\":{\"type\":\"boolean\"},\"max_references\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500}},\"additionalProperties\":false}";
        const string ResourceReadSchemaJson = "{\"type\":\"object\",\"properties\":{\"uri\":{\"type\":\"string\"},\"format\":{\"type\":\"string\",\"enum\":[\"text\",\"json\",\"markdown\"]},\"max_chars\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":1000000}},\"required\":[\"uri\"],\"additionalProperties\":false}";
        const string DocumentCreateSchemaJson = "{\"type\":\"object\",\"properties\":{\"output_root\":{\"type\":\"string\"},\"name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"create_prefab\":{\"type\":\"boolean\",\"default\":true},\"create_panel_settings\":{\"type\":\"boolean\",\"default\":true},\"overwrite\":{\"type\":\"boolean\",\"default\":false},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"output_root\",\"name\"],\"additionalProperties\":false}";
        const string BinderGenerateSchemaJson = "{\"type\":\"object\",\"properties\":{\"uxml_path\":{\"type\":\"string\"},\"output_path\":{\"type\":\"string\"},\"class_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"namespace\":{\"type\":\"string\",\"maxLength\":160},\"include_unsubscribe_stubs\":{\"type\":\"boolean\",\"default\":true},\"overwrite\":{\"type\":\"boolean\",\"default\":false},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"uxml_path\",\"output_path\",\"class_name\"],\"additionalProperties\":false}";
        const string TemplateGenerateSchemaJson = "{\"type\":\"object\",\"properties\":{\"output_root\":{\"type\":\"string\"},\"control_name\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"namespace\":{\"type\":\"string\",\"maxLength\":160},\"create_uxml\":{\"type\":\"boolean\",\"default\":true},\"create_uss\":{\"type\":\"boolean\",\"default\":true},\"create_csharp\":{\"type\":\"boolean\",\"default\":true},\"overwrite\":{\"type\":\"boolean\",\"default\":false},\"dry_run\":{\"type\":\"boolean\",\"default\":true}},\"required\":[\"output_root\",\"control_name\"],\"additionalProperties\":false}";
        const string ReportSchemaJson = "{\"type\":\"object\",\"properties\":{\"inspection_id\":{\"type\":\"string\"},\"validation_id\":{\"type\":\"string\"},\"analysis_id\":{\"type\":\"string\"},\"reference_report_id\":{\"type\":\"string\"},\"targets\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"uxml_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"uss_paths\":{\"type\":\"array\",\"items\":{\"type\":\"string\"},\"maxItems\":100},\"format\":{\"type\":\"string\",\"enum\":[\"markdown\",\"json\",\"markdown_and_json\"]}},\"additionalProperties\":false}";
        const string DocumentInspectOutputLimitJson = "{\"max_documents\":200,\"max_targets\":100}";
        const string AssetInspectOutputLimitJson = "{\"max_paths\":100,\"max_items\":5000}";
        const string ValidateOutputLimitJson = "{\"max_findings\":200,\"max_paths\":300}";
        const string ReferencesOutputLimitJson = "{\"max_references\":500,\"max_paths\":200}";
        const string ResourceReadOutputLimitJson = "{\"max_chars\":1000000}";
        const string SafeWriteOutputLimitJson = "{\"max_files\":8,\"max_chars\":131072}";
        const string MigrationScanOutputLimitJson = "{\"max_depth\":64,\"max_nodes\":5000,\"max_targets\":100}";
        const string MigrationPlanOutputLimitJson = "{\"max_artifacts\":6,\"max_manual_tasks\":8,\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/uitk-migration-plans\"}";
        const string MigrationGenerateOutputLimitJson = "{\"max_files\":8,\"mode\":\"create_only\",\"dry_run_default\":true,\"resource_storage\":\"process_memory\"}";
        const string MigrationPreviewOutputLimitJson = "{\"artifact_root\":\"Library/SGGUnityMcp/job-artifacts/uitk-previews\",\"screenshot_roots\":[\"Library/SGGUnityMcp/screenshots\",\"CaptureArtifacts\"],\"screenshot_target_selection\":true,\"automatic_capture\":\"explicit_capture_screenshots_only\",\"max_pixel_compare_pixels\":16777216,\"dry_run_default\":true}";
        const string ReportOutputLimitJson = "{\"max_paths\":300,\"max_chars\":1000000}";
        const int MaxCachedReports = 32;
        const int MaxCachedResources = 64;
        const int MaxPreviewPixelComparePixels = 4096 * 4096;
        const int MaxTextBytes = 1000000;
        const int MaxGeneratedTextChars = 131072;
        const int MaxSerializedProperties = 120;

        static readonly object ReportCacheLock = new object();
        static readonly Dictionary<string, UitkReportSnapshot> ReportCache = new Dictionary<string, UitkReportSnapshot>();
        static readonly Queue<string> ReportCacheOrder = new Queue<string>();
        static readonly object ResourceCacheLock = new object();
        static readonly Dictionary<string, UitkResourceSnapshot> ResourceCache = new Dictionary<string, UitkResourceSnapshot>();
        static readonly Queue<string> ResourceCacheOrder = new Queue<string>();

        [McpCommand(
            "uitk.scan",
            Title = "UI Toolkit migration scan",
            Description = "Read a bounded uGUI Canvas snapshot as the first Canvas to UI Toolkit migration input without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = MigrationScanSchemaJson,
            Tags = new string[] { "uitk", "ugui", "canvas", "migration", "semantic-inspection" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = MigrationScanOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult MigrationScan(McpCommandContext context)
        {
            var sourceResult = UguiInspectionCommands.CanvasScan(context);
            if (sourceResult.Status != "ok")
            {
                return sourceResult;
            }

            var sourceScanId = JsonUtil.ExtractString(sourceResult.DataJson, "scan_id");
            var sourceResourceUri = "unity-ui://ugui/scans/" + sourceScanId + "/json";
            var resultJson = "{\"status\":\"ok\""
                + ",\"migration_mode\":\"ugui_to_uitk\""
                + ",\"scan_id\":" + JsonUtil.String(sourceScanId)
                + ",\"source_command\":\"ugui.canvas.scan\""
                + ",\"source_scan_id\":" + JsonUtil.String(sourceScanId)
                + ",\"source_scan\":" + sourceResult.DataJson
                + ",\"resource_refs\":" + ResourceRefsJson(sourceResourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"read_only\":true"
                + ",\"next_valid_actions\":[\"ugui.resource.read\",\"ugui.canvas.report\",\"ugui.events.extract\",\"ugui.layout.analyze\",\"ugui.navigation.analyze\",\"ugui.raycast.analyze\",\"ugui.references.report\",\"uitk.document.create\",\"uitk.template.generate\",\"uitk.binder.generate\",\"uitk.validate\",\"uitk.bindings.validate\"]}";
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.plan",
            Title = "UI Toolkit migration plan",
            Description = "Create a bounded Canvas to UI Toolkit conversion plan from an existing uGUI scan without generating UI assets or applying changes",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = MigrationPlanSchemaJson,
            Tags = new string[] { "uitk", "ugui", "canvas", "migration", "plan" },
            Capability = "uitk_migration",
            ResultShape = "status",
            OutputLimitJson = MigrationPlanOutputLimitJson,
            FeatureFlag = MigrationFeatureFlag)]
        public static CommandExecutionResult MigrationPlan(McpCommandContext context)
        {
            var scanId = JsonUtil.ExtractString(context.ArgsJson, "scan_id");
            if (string.IsNullOrEmpty(scanId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument scan_id is required");
            }

            var strategy = JsonUtil.ExtractString(context.ArgsJson, "strategy");
            if (!IsMigrationPlanStrategy(strategy))
            {
                return CommandExecutionResult.Error("invalid_field_value", "strategy must be absolute_fidelity, semantic_flex_with_absolute_fallback, or report_only");
            }

            var outputRootResult = RequireOutputRoot(context.ArgsJson, "output_root", out var outputRoot);
            if (outputRootResult != null)
            {
                return outputRootResult;
            }

            var targetNameResult = RequireIdentifierArgument(context.ArgsJson, "target_name", out var targetName);
            if (targetNameResult != null)
            {
                return targetNameResult;
            }

            var sourceReportResult = UguiInspectionCommands.CanvasReport(new McpCommandContext(
                context.State,
                context.RequestId,
                context.Project,
                "{\"scan_id\":" + JsonUtil.String(scanId) + "}",
                context.SettingsJson));
            if (sourceReportResult.Status != "ok")
            {
                return sourceReportResult;
            }

            if (JsonUtil.ExtractString(sourceReportResult.DataJson, "status") != "ok")
            {
                return CommandExecutionResult.Ok("{\"status\":\"not_found\""
                    + ",\"scan_id\":" + JsonUtil.String(scanId)
                    + ",\"source_command\":\"ugui.canvas.report\""
                    + ",\"source_report\":" + sourceReportResult.DataJson
                    + ",\"resource_refs\":[]"
                    + ",\"artifact_refs\":[]"
                    + ",\"next_valid_actions\":[\"uitk.scan\",\"ugui.canvas.scan\"]}");
            }

            var persist = JsonUtil.ExtractBool(context.ArgsJson, "persist", false);
            var planId = NewId("uitk_plan");
            var artifacts = MigrationPlanArtifacts(outputRoot, targetName);
            var manualTasks = MigrationPlanManualTasks(strategy);
            var planResourceUri = PlanResourceUri(planId);
            var markdownResourceUri = PlanMarkdownResourceUri(planId);
            var artifactPath = persist ? MigrationPlanArtifactRoot + planId + ".json" : string.Empty;
            var markdown = MigrationPlanMarkdown(planId, scanId, strategy, outputRoot, targetName, artifacts, manualTasks);
            var resultJson = MigrationPlanJson(planId, scanId, strategy, outputRoot, targetName, sourceReportResult.DataJson, artifacts, manualTasks, new string[] { planResourceUri, markdownResourceUri }, persist, artifactPath);

            StoreResource(planResourceUri, resultJson, "application/json");
            StoreResource(markdownResourceUri, markdown, "text/markdown");
            if (persist)
            {
                try
                {
                    WriteMigrationPlanArtifact(artifactPath, resultJson);
                }
                catch (Exception exception)
                {
                    return CommandExecutionResult.Error("unity_exception", "UITK migration plan artifact write failed: " + exception.Message);
                }
            }

            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.generate",
            Title = "UI Toolkit migration generate",
            Description = "Create side-by-side UI Toolkit scaffold artifacts from a bounded migration plan without modifying the legacy Canvas source",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = MigrationGenerateSchemaJson,
            Tags = new string[] { "uitk", "ugui", "canvas", "migration", "generate", "safe-write" },
            Capability = "uitk_migration",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            OutputLimitJson = MigrationGenerateOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = MigrationFeatureFlag)]
        public static CommandExecutionResult MigrationGenerate(McpCommandContext context)
        {
            var status = RequireEditing(context);
            if (status != null)
            {
                return status;
            }

            var planId = JsonUtil.ExtractString(context.ArgsJson, "plan_id");
            if (string.IsNullOrEmpty(planId))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument plan_id is required");
            }

            var mode = JsonUtil.ExtractString(context.ArgsJson, "mode");
            if (string.IsNullOrEmpty(mode))
            {
                mode = "create_only";
            }
            if (mode != "create_only")
            {
                return CommandExecutionResult.Error("invalid_field_value", "mode must be create_only in this migration slice");
            }

            var planResource = FindResource(PlanResourceUri(planId));
            if (planResource == null)
            {
                return CommandExecutionResult.Ok("{\"status\":\"not_found\""
                    + ",\"plan_id\":" + JsonUtil.String(planId)
                    + ",\"reason\":\"plan_id was not found in this Unity Bridge process resource cache\""
                    + ",\"cache\":{\"storage\":\"memory\",\"max_resources\":" + MaxCachedResources + "}"
                    + ",\"resource_refs\":[]"
                    + ",\"artifact_refs\":[]"
                    + ",\"next_valid_actions\":[\"uitk.plan\"]}");
            }

            var planJson = planResource.Content;
            var strategy = JsonUtil.ExtractString(planJson, "strategy");
            if (strategy == "report_only")
            {
                return CommandExecutionResult.Ok("{\"status\":\"blocked\""
                    + ",\"plan_id\":" + JsonUtil.String(planId)
                    + ",\"reason\":\"report_only plans do not permit asset generation\""
                    + ",\"resource_refs\":" + ResourceRefsJson(planResource.Uri)
                    + ",\"artifact_refs\":[]"
                    + ",\"next_valid_actions\":[\"uitk.plan\"]}");
            }

            var outputRoot = JsonUtil.ExtractString(planJson, "output_root");
            var targetName = JsonUtil.ExtractString(planJson, "target_name");
            var scanId = JsonUtil.ExtractString(planJson, "scan_id");
            if (string.IsNullOrEmpty(outputRoot) || string.IsNullOrEmpty(targetName) || string.IsNullOrEmpty(scanId) || !IsCSharpIdentifier(targetName) || IsCSharpKeyword(targetName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Cached plan is missing a valid output_root, target_name, or scan_id");
            }

            try
            {
                outputRoot = PathGuard.RequireUnityAssetPath(outputRoot).TrimEnd('/');
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Cached plan output_root is invalid: " + exception.Message);
            }
            if (!outputRoot.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Cached plan output_root must be a child path under Assets/");
            }

            var overwrite = JsonUtil.ExtractBool(context.ArgsJson, "overwrite", false);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var generationId = NewId("uitk_generation");
            var generationResourceUri = GenerationResourceUri(generationId);
            var artifacts = MigrationGenerateArtifacts(planId, generationId, scanId, strategy, outputRoot, targetName, planJson);
            var validationResult = ValidateGeneratedArtifacts(artifacts, overwrite);
            if (validationResult != null)
            {
                return validationResult;
            }

            var summary = "Prepared create-only UI Toolkit migration artifacts for " + targetName;
            if (dryRun)
            {
                var dryRunJson = MigrationGenerateResultJson(summary, true, outputRoot, targetName, planId, generationId, scanId, strategy, artifacts, 0, 0, planResource.Uri, generationResourceUri);
                StoreResource(generationResourceUri, dryRunJson, "application/json");
                return CommandExecutionResult.Ok(dryRunJson);
            }

            try
            {
                EnsureArtifactFolders(artifacts);
                WriteTextArtifacts(artifacts);
                CreateMigrationPanelSettings(targetName, outputRoot + "/" + targetName + "PanelSettings.asset");
                CreateDocumentPrefab(targetName, outputRoot + "/" + targetName + "UIDocument.prefab", outputRoot + "/" + targetName + ".uxml", outputRoot + "/" + targetName + "PanelSettings.asset");
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("unity_exception", "UITK migration artifact generation failed: " + exception.Message);
            }

            MarkArtifactsWritten(artifacts);
            var resultJson = MigrationGenerateResultJson(summary, false, outputRoot, targetName, planId, generationId, scanId, strategy, artifacts, CountCreated(artifacts), CountOverwritten(artifacts), planResource.Uri, generationResourceUri);
            StoreResource(generationResourceUri, resultJson, "application/json");
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.preview",
            Title = "UI Toolkit migration preview manifest",
            Description = "Create a bounded Canvas to UI Toolkit preview manifest, old/new screenshot target selection, and artifact refs from a generated migration bundle without applying or replacing legacy UI",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 15000,
            SchemaJson = MigrationPreviewSchemaJson,
            Tags = new string[] { "uitk", "ugui", "canvas", "migration", "preview" },
            Capability = "uitk_migration_preview",
            ResultShape = "status",
            OutputLimitJson = MigrationPreviewOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = MigrationPreviewFeatureFlag)]
        public static CommandExecutionResult MigrationPreview(McpCommandContext context)
        {
            var generationId = JsonUtil.ExtractString(context.ArgsJson, "generation_id");
            var migrationMapPath = JsonUtil.ExtractString(context.ArgsJson, "migration_map_path");
            if (string.IsNullOrEmpty(generationId) && string.IsNullOrEmpty(migrationMapPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "generation_id or migration_map_path is required");
            }

            var bundleResult = LoadMigrationValidationBundle(generationId, migrationMapPath, out var bundle);
            if (bundleResult != null)
            {
                return bundleResult;
            }

            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var pixelThreshold = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "pixel_threshold", 8), 0, 255);
            var previewId = NewId("uitk_preview");
            var warnings = new List<string>();
            var sourceScanId = JsonUtil.ExtractString(context.ArgsJson, "source_scan_id");
            if (string.IsNullOrEmpty(sourceScanId))
            {
                sourceScanId = bundle.ScanId;
            }
            if (string.IsNullOrEmpty(sourceScanId))
            {
                warnings.Add("source_scan_id is missing; bounds diff is limited to generated bundle artifact metadata");
            }

            var sourceScreenshotPath = JsonUtil.ExtractString(context.ArgsJson, "source_screenshot_path");
            var generatedScreenshotPath = JsonUtil.ExtractString(context.ArgsJson, "generated_screenshot_path");
            var sourceScreenshotResult = BuildPreviewScreenshotRef("source", sourceScreenshotPath);
            if (sourceScreenshotResult.ErrorResult != null)
            {
                return sourceScreenshotResult.ErrorResult;
            }
            var generatedScreenshotResult = BuildPreviewScreenshotRef("generated", generatedScreenshotPath);
            if (generatedScreenshotResult.ErrorResult != null)
            {
                return generatedScreenshotResult.ErrorResult;
            }

            var captureResult = PreviewCaptureResult.Disabled(dryRun);
            if (JsonUtil.ExtractBool(context.ArgsJson, "capture_screenshots", false))
            {
                captureResult = ExecutePreviewScreenshotCaptures(context, previewId, dryRun, sourceScreenshotResult, generatedScreenshotResult, ref sourceScreenshotPath, ref generatedScreenshotPath, warnings);
                if (captureResult.ErrorResult != null)
                {
                    return captureResult.ErrorResult;
                }

                sourceScreenshotResult = BuildPreviewScreenshotRef("source", sourceScreenshotPath);
                if (sourceScreenshotResult.ErrorResult != null)
                {
                    return sourceScreenshotResult.ErrorResult;
                }
                generatedScreenshotResult = BuildPreviewScreenshotRef("generated", generatedScreenshotPath);
                if (generatedScreenshotResult.ErrorResult != null)
                {
                    return generatedScreenshotResult.ErrorResult;
                }
            }

            AddAll(warnings, sourceScreenshotResult.Warnings);
            AddAll(warnings, generatedScreenshotResult.Warnings);

            var pixelDiffPath = MigrationPreviewArtifactRoot + previewId + ".pixel-diff.png";
            var pixelDiff = ComparePreviewScreenshots(sourceScreenshotResult, generatedScreenshotResult, pixelThreshold, pixelDiffPath, dryRun, warnings);
            var sourceReportJson = SourceScanPreviewReportJson(context, sourceScanId, warnings);
            var screenshotTargetsJson = PreviewScreenshotTargetsJson(sourceScanId, bundle, sourceScreenshotResult, generatedScreenshotResult, captureResult);
            var boundsDiffJson = PreviewBoundsDiffJson(previewId, sourceScanId, bundle, sourceScreenshotResult, generatedScreenshotResult, captureResult, pixelDiff, sourceReportJson, screenshotTargetsJson);
            var artifactPaths = new List<string>
            {
                MigrationPreviewArtifactRoot + previewId + ".json",
                MigrationPreviewArtifactRoot + previewId + ".bounds-diff.json",
                MigrationPreviewArtifactRoot + previewId + ".md"
            };
            if (!dryRun && pixelDiff.DiffImageWritten)
            {
                artifactPaths.Add(pixelDiffPath);
            }
            var resourceUri = PreviewResourceUri(previewId);
            var resultJson = MigrationPreviewResultJson(previewId, dryRun, bundle, sourceScanId, sourceScreenshotResult, generatedScreenshotResult, captureResult, boundsDiffJson, screenshotTargetsJson, artifactPaths, resourceUri, warnings);
            StoreResource(resourceUri, resultJson, "application/json");

            if (!dryRun)
            {
                try
                {
                    WritePreviewArtifact(artifactPaths[0], resultJson);
                    WritePreviewArtifact(artifactPaths[1], boundsDiffJson);
                    WritePreviewArtifact(artifactPaths[2], MigrationPreviewMarkdown(previewId, sourceScanId, bundle, sourceScreenshotResult, generatedScreenshotResult, captureResult, pixelDiff, warnings));
                }
                catch (Exception exception)
                {
                    return CommandExecutionResult.Error("unity_exception", "UITK migration preview artifact write failed: " + exception.Message);
                }
            }

            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.document.inspect",
            Title = "UI Toolkit document inspect",
            Description = "Read bounded UIDocument references and connected UI Toolkit assets from loaded scenes or prefab assets without mutating assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = DocumentInspectSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "document", "semantic-inspection" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = DocumentInspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult DocumentInspect(McpCommandContext context)
        {
            var targets = ExtractStringArray(context.ArgsJson, "targets", 100);
            var includeInactive = JsonUtil.ExtractBool(context.ArgsJson, "include_inactive", true);
            var includePanelSettings = JsonUtil.ExtractBool(context.ArgsJson, "include_panel_settings", true);
            var includeVisualTreeAsset = JsonUtil.ExtractBool(context.ArgsJson, "include_visual_tree_asset", true);
            var maxDocuments = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_documents", 200), 1, 200);
            var inspectionId = NewId("uitk_doc");
            var state = new DocumentInspectState(maxDocuments, includeInactive, includePanelSettings, includeVisualTreeAsset);
            var targetResults = new List<string>();

            if (targets.Count == 0)
            {
                for (var sceneIndex = 0; sceneIndex < SceneManager.sceneCount && !state.Truncated; sceneIndex++)
                {
                    var scene = SceneManager.GetSceneAt(sceneIndex);
                    if (!scene.IsValid() || !scene.isLoaded)
                    {
                        continue;
                    }

                    targetResults.Add(InspectSceneDocuments(scene.path, "loaded_scene", scene.GetRootGameObjects(), state));
                }
            }
            else
            {
                foreach (var rawTarget in targets)
                {
                    if (state.Truncated)
                    {
                        break;
                    }

                    targetResults.Add(InspectDocumentTarget(rawTarget, state));
                }
            }

            var reportId = NewId("uitk_report");
            var summary = "Inspected " + state.DocumentsFound + " UIDocument(s) across " + targetResults.Count + " target(s)";
            var resourceUri = DocumentResourceUri(inspectionId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"inspection_id\":" + JsonUtil.String(inspectionId)
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"targets_scanned\":" + targetResults.Count
                + ",\"documents_found\":" + state.DocumentsFound
                + ",\"missing_source_assets\":" + state.MissingSourceAssets
                + ",\"missing_panel_settings\":" + state.MissingPanelSettings
                + ",\"truncated\":" + JsonUtil.Bool(state.Truncated)
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"targets\":" + JsonArray(targetResults)
                + ",\"documents\":" + JsonArray(state.Documents)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(state.Warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.uxml.inspect\",\"uitk.uss.inspect\",\"uitk.panelsettings.inspect\",\"uitk.validate\",\"uitk.report\"]} ";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, inspectionId, "uitk.document.inspect", summary, resultJson, MarkdownReport("UITK document inspection", summary, state.Warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.uxml.inspect",
            Title = "UI Toolkit UXML inspect",
            Description = "Read bounded UXML structure, named elements, USS classes, style refs, and template refs without importing or modifying assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = UxmlInspectSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "uxml", "semantic-inspection" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = AssetInspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult UxmlInspect(McpCommandContext context)
        {
            var paths = ExtractStringArray(context.ArgsJson, "uxml_paths", 100);
            if (paths.Count == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument uxml_paths is required");
            }

            var includeTree = JsonUtil.ExtractBool(context.ArgsJson, "include_tree", false);
            var includeNames = JsonUtil.ExtractBool(context.ArgsJson, "include_names", true);
            var includeClasses = JsonUtil.ExtractBool(context.ArgsJson, "include_classes", true);
            var maxElements = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_elements", 1000), 1, 5000);
            var inspectionId = NewId("uitk_uxml");
            var fileResults = new List<string>();
            var warnings = new List<string>();
            var totalElements = 0;
            var totalDuplicateNames = 0;
            var truncated = false;

            foreach (var rawPath in paths)
            {
                var result = InspectUxmlPath(rawPath, includeTree, includeNames, includeClasses, maxElements, warnings, out var elements, out var duplicateNames, out var fileTruncated);
                fileResults.Add(result);
                totalElements += elements;
                totalDuplicateNames += duplicateNames;
                truncated = truncated || fileTruncated;
            }

            var reportId = NewId("uitk_report");
            var summary = "Inspected " + paths.Count + " UXML file(s), " + totalElements + " element(s)";
            var resourceUri = UxmlResourceUri(inspectionId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"inspection_id\":" + JsonUtil.String(inspectionId)
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"files_scanned\":" + paths.Count
                + ",\"elements_found\":" + totalElements
                + ",\"duplicate_names\":" + totalDuplicateNames
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"files\":" + JsonArray(fileResults)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.validate\",\"uitk.report\"]}";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, inspectionId, "uitk.uxml.inspect", summary, resultJson, MarkdownReport("UITK UXML inspection", summary, warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.uss.inspect",
            Title = "UI Toolkit USS inspect",
            Description = "Read bounded USS selectors, classes, and asset references without importing or modifying assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = UssInspectSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "uss", "semantic-inspection" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = AssetInspectOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult UssInspect(McpCommandContext context)
        {
            var paths = ExtractStringArray(context.ArgsJson, "uss_paths", 100);
            if (paths.Count == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument uss_paths is required");
            }

            var includeSelectors = JsonUtil.ExtractBool(context.ArgsJson, "include_selectors", true);
            var includeAssetReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_asset_references", true);
            var maxRules = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_rules", 1000), 1, 5000);
            var inspectionId = NewId("uitk_uss");
            var fileResults = new List<string>();
            var warnings = new List<string>();
            var totalRules = 0;
            var totalSelectors = 0;
            var truncated = false;

            foreach (var rawPath in paths)
            {
                var result = InspectUssPath(rawPath, includeSelectors, includeAssetReferences, maxRules, warnings, out var rules, out var selectors, out var fileTruncated);
                fileResults.Add(result);
                totalRules += rules;
                totalSelectors += selectors;
                truncated = truncated || fileTruncated;
            }

            var reportId = NewId("uitk_report");
            var summary = "Inspected " + paths.Count + " USS file(s), " + totalRules + " rule(s)";
            var resourceUri = UssResourceUri(inspectionId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"inspection_id\":" + JsonUtil.String(inspectionId)
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"files_scanned\":" + paths.Count
                + ",\"rules_found\":" + totalRules
                + ",\"selectors_found\":" + totalSelectors
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"files\":" + JsonArray(fileResults)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.validate\",\"uitk.report\"]}";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, inspectionId, "uitk.uss.inspect", summary, resultJson, MarkdownReport("UITK USS inspection", summary, warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.panelsettings.inspect",
            Title = "UI Toolkit PanelSettings inspect",
            Description = "Read bounded PanelSettings asset metadata and serialized references without modifying assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = PanelSettingsInspectSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "panelsettings", "semantic-inspection" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = AssetInspectOutputLimitJson,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult PanelSettingsInspect(McpCommandContext context)
        {
            var paths = ExtractStringArray(context.ArgsJson, "panel_settings_paths", 100);
            if (paths.Count == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument panel_settings_paths is required");
            }

            var includeReferences = JsonUtil.ExtractBool(context.ArgsJson, "include_references", true);
            var inspectionId = NewId("uitk_panelsettings");
            var results = new List<string>();
            var warnings = new List<string>();
            var found = 0;
            var referenceCount = 0;

            foreach (var rawPath in paths)
            {
                var result = InspectPanelSettingsPath(rawPath, includeReferences, warnings, out var assetFound, out var assetReferences);
                results.Add(result);
                if (assetFound)
                {
                    found++;
                }
                referenceCount += assetReferences;
            }

            var reportId = NewId("uitk_report");
            var summary = "Inspected " + found + " PanelSettings asset(s)";
            var resourceUri = PanelSettingsResourceUri(inspectionId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"inspection_id\":" + JsonUtil.String(inspectionId)
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"assets_requested\":" + paths.Count
                + ",\"assets_found\":" + found
                + ",\"references_found\":" + referenceCount
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"panel_settings\":" + JsonArray(results)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.document.inspect\",\"uitk.validate\",\"uitk.report\"]}";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, inspectionId, "uitk.panelsettings.inspect", summary, resultJson, MarkdownReport("UITK PanelSettings inspection", summary, warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.validate",
            Title = "UI Toolkit validate",
            Description = "Validate bounded UI Toolkit documents, UXML, USS, and binder queries without importing or modifying assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ValidateSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "validation" },
            Capability = "uitk_validation",
            ResultShape = "status",
            OutputLimitJson = ValidateOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = ValidationFeatureFlag)]
        public static CommandExecutionResult Validate(McpCommandContext context)
        {
            var targets = ExtractStringArray(context.ArgsJson, "targets", 100);
            var uxmlPaths = ExtractStringArray(context.ArgsJson, "uxml_paths", 100);
            var ussPaths = ExtractStringArray(context.ArgsJson, "uss_paths", 100);
            var binderPaths = ExtractStringArray(context.ArgsJson, "binder_paths", 100);
            var generationId = JsonUtil.ExtractString(context.ArgsJson, "generation_id");
            var migrationMapPath = JsonUtil.ExtractString(context.ArgsJson, "migration_map_path");
            if (targets.Count == 0
                && uxmlPaths.Count == 0
                && ussPaths.Count == 0
                && binderPaths.Count == 0
                && string.IsNullOrEmpty(generationId)
                && string.IsNullOrEmpty(migrationMapPath)
                && !JsonUtil.ExtractBool(context.ArgsJson, "run_compile_check", false))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "targets, uxml_paths, uss_paths, binder_paths, generation_id, migration_map_path, or run_compile_check is required");
            }

            var maxFindings = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_findings", 200), 1, 200);
            var validationId = NewId("uitk_validation");
            var findings = new FindingList(maxFindings);
            var warnings = new List<string>();
            var migrationBundleResults = new List<string>();
            MigrationValidationBundle migrationBundle = null;
            if (!string.IsNullOrEmpty(generationId) || !string.IsNullOrEmpty(migrationMapPath))
            {
                var bundleResult = LoadMigrationValidationBundle(generationId, migrationMapPath, out migrationBundle);
                if (bundleResult != null)
                {
                    return bundleResult;
                }

                AddUnique(targets, migrationBundle.PrefabPath);
                AddUnique(uxmlPaths, migrationBundle.UxmlPath);
                AddUnique(ussPaths, migrationBundle.UssPath);
                AddUnique(binderPaths, migrationBundle.BinderPath);
            }

            var runCompileCheck = JsonUtil.ExtractBool(context.ArgsJson, "run_compile_check", migrationBundle != null);
            var validateBinderQueries = JsonUtil.ExtractBool(context.ArgsJson, "validate_binder_queries", binderPaths.Count > 0);
            var uxmlInfo = new UxmlValidationInfo();
            var uxmlResults = new List<string>();
            var ussResults = new List<string>();
            var targetResults = new List<string>();
            var binderResults = new List<string>();

            if (migrationBundle != null)
            {
                migrationBundleResults.Add(ValidateMigrationBundle(migrationBundle, findings, warnings));
            }
            foreach (var target in targets)
            {
                targetResults.Add(ValidateDocumentTarget(target, findings));
            }
            foreach (var path in uxmlPaths)
            {
                uxmlResults.Add(ValidateUxmlPath(path, findings, uxmlInfo));
            }
            foreach (var path in ussPaths)
            {
                ussResults.Add(ValidateUssPath(path, findings));
            }
            if (validateBinderQueries)
            {
                ValidateBinderPaths(binderPaths, uxmlInfo, findings, binderResults, DefaultQueryMethods());
            }

            var compileStatusJson = "null";
            if (runCompileCheck)
            {
                compileStatusJson = CompileStatusJson(findings);
            }

            var reportId = NewId("uitk_report");
            var passed = findings.ErrorCount == 0;
            var summary = "Validated UITK inputs with " + findings.TotalCount + " finding(s)";
            var resourceUri = ValidationResourceUri(validationId);
            var migrationGate = migrationBundle == null ? "not_requested" : (passed ? "passed" : "failed");
            var resultJson = "{\"status\":\"ok\""
                + ",\"validation_id\":" + JsonUtil.String(validationId)
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"passed\":" + JsonUtil.Bool(passed)
                + ",\"read_only\":true"
                + ",\"migration_gate\":" + JsonUtil.String(migrationGate)
                + ",\"migration_bundle\":" + (migrationBundle == null ? "null" : migrationBundle.ToJson())
                + ",\"migration_artifacts\":" + JsonArray(migrationBundleResults)
                + ",\"targets_checked\":" + targets.Count
                + ",\"uxml_files_checked\":" + uxmlPaths.Count
                + ",\"uss_files_checked\":" + ussPaths.Count
                + ",\"binder_files_checked\":" + binderPaths.Count
                + ",\"compile_status\":" + compileStatusJson
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"findings_count\":" + findings.TotalCount
                + ",\"returned_findings_count\":" + findings.ReturnedCount
                + ",\"findings_truncated\":" + JsonUtil.Bool(findings.Truncated)
                + ",\"findings_by_severity\":" + findings.SeverityCountsJson()
                + ",\"targets\":" + JsonArray(targetResults)
                + ",\"uxml_files\":" + JsonArray(uxmlResults)
                + ",\"uss_files\":" + JsonArray(ussResults)
                + ",\"binder_files\":" + JsonArray(binderResults)
                + ",\"findings\":" + JsonArray(findings.ReturnedFindings)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":" + JsonArrayStrings(MigrationValidationNextActions(migrationBundle, passed))
                + "}";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, validationId, "uitk.validate", summary, resultJson, MarkdownReport("UITK validation", summary, warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.bindings.validate",
            Title = "UI Toolkit bindings validate",
            Description = "Validate bounded C# UI Toolkit root.Q/root.Query binder queries against UXML named elements without compiling or modifying assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = BindingsValidateSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "validation", "bindings" },
            Capability = "uitk_validation",
            ResultShape = "status",
            OutputLimitJson = ValidateOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = ValidationFeatureFlag)]
        public static CommandExecutionResult BindingsValidate(McpCommandContext context)
        {
            var binderPaths = ExtractStringArray(context.ArgsJson, "binder_paths", 100);
            var uxmlPaths = ExtractStringArray(context.ArgsJson, "uxml_paths", 100);
            if (binderPaths.Count == 0 || uxmlPaths.Count == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Arguments binder_paths and uxml_paths are required");
            }

            var maxFindings = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_findings", 200), 1, 200);
            var queryPatterns = ExtractStringArray(context.ArgsJson, "query_patterns", 16);
            var validationId = NewId("uitk_bindings");
            var findings = new FindingList(maxFindings);
            var warnings = new List<string>();
            var queryMethods = QueryMethodsFromPatterns(queryPatterns, findings);
            var uxmlInfo = new UxmlValidationInfo();
            var uxmlResults = new List<string>();
            var binderResults = new List<string>();

            foreach (var path in uxmlPaths)
            {
                uxmlResults.Add(ValidateUxmlPath(path, findings, uxmlInfo));
            }
            ValidateBinderPaths(binderPaths, uxmlInfo, findings, binderResults, queryMethods);

            var reportId = NewId("uitk_report");
            var summary = "Validated " + binderPaths.Count + " binder file(s) against " + uxmlPaths.Count + " UXML file(s)";
            var resourceUri = BindingsResourceUri(validationId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"validation_id\":" + JsonUtil.String(validationId)
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"passed\":" + JsonUtil.Bool(findings.ErrorCount == 0)
                + ",\"read_only\":true"
                + ",\"binder_files_checked\":" + binderPaths.Count
                + ",\"uxml_files_checked\":" + uxmlPaths.Count
                + ",\"query_patterns\":" + JsonArrayStrings(queryPatterns.Count == 0 ? new[] { "root.Q", "root.Query" } : queryPatterns.ToArray())
                + ",\"query_methods\":" + JsonArrayStrings(DedupeSorted(queryMethods))
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"findings_count\":" + findings.TotalCount
                + ",\"returned_findings_count\":" + findings.ReturnedCount
                + ",\"findings_truncated\":" + JsonUtil.Bool(findings.Truncated)
                + ",\"findings_by_severity\":" + findings.SeverityCountsJson()
                + ",\"uxml_files\":" + JsonArray(uxmlResults)
                + ",\"binder_files\":" + JsonArray(binderResults)
                + ",\"findings\":" + JsonArray(findings.ReturnedFindings)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.report\"]}";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, validationId, "uitk.bindings.validate", summary, resultJson, MarkdownReport("UITK bindings validation", summary, warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.layout.analyze",
            Title = "UI Toolkit layout analyze",
            Description = "Read bounded UXML and USS layout-risk diagnostics without rendering, importing, or modifying assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = LayoutAnalyzeSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "layout", "diagnostics" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = ValidateOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult LayoutAnalyze(McpCommandContext context)
        {
            var uxmlPaths = ExtractStringArray(context.ArgsJson, "uxml_paths", 100);
            var ussPaths = ExtractStringArray(context.ArgsJson, "uss_paths", 100);
            if (uxmlPaths.Count == 0 && ussPaths.Count == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "uxml_paths or uss_paths is required");
            }

            var includeAbsolutePositioning = JsonUtil.ExtractBool(context.ArgsJson, "include_absolute_positioning", true);
            var maxFindings = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_findings", 200), 1, 200);
            var screenSizes = ExtractScreenSizes(context.ArgsJson);
            var analysisId = NewId("uitk_layout");
            var findings = new FindingList(maxFindings);
            var warnings = new List<string>();
            var uxmlInfo = new UxmlLayoutInfo();
            var uxmlResults = new List<string>();
            var ussResults = new List<string>();
            var ussRules = new List<UssRuleInfo>();

            foreach (var path in uxmlPaths)
            {
                uxmlResults.Add(AnalyzeUxmlLayoutPath(path, uxmlInfo, findings, screenSizes));
            }
            foreach (var path in ussPaths)
            {
                ussResults.Add(AnalyzeUssLayoutPath(path, includeAbsolutePositioning, screenSizes, findings, ussRules));
            }
            AddCrossLayoutFindings(uxmlInfo, ussRules, findings);

            var reportId = NewId("uitk_report");
            var summary = "Analyzed " + uxmlPaths.Count + " UXML and " + ussPaths.Count + " USS file(s) with " + findings.TotalCount + " layout finding(s)";
            var resourceUri = LayoutResourceUri(analysisId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"analysis_id\":" + JsonUtil.String(analysisId)
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"read_only\":true"
                + ",\"uxml_files_checked\":" + uxmlPaths.Count
                + ",\"uss_files_checked\":" + ussPaths.Count
                + ",\"screen_sizes\":" + ScreenSizesJson(screenSizes)
                + ",\"include_absolute_positioning\":" + JsonUtil.Bool(includeAbsolutePositioning)
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"findings_count\":" + findings.TotalCount
                + ",\"returned_findings_count\":" + findings.ReturnedCount
                + ",\"findings_truncated\":" + JsonUtil.Bool(findings.Truncated)
                + ",\"findings_by_severity\":" + findings.SeverityCountsJson()
                + ",\"uxml_files\":" + JsonArray(uxmlResults)
                + ",\"uss_files\":" + JsonArray(ussResults)
                + ",\"findings\":" + JsonArray(findings.ReturnedFindings)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.references.report\",\"uitk.report\"]}";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, analysisId, "uitk.layout.analyze", summary, resultJson, MarkdownReport("UITK layout analysis", summary, warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.references.report",
            Title = "UI Toolkit references report",
            Description = "Read a bounded UI Toolkit-focused reference report for UXML style/template refs and USS asset refs without modifying assets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 30000,
            SchemaJson = ReferencesReportSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "references", "assets" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = ReferencesOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult ReferencesReport(McpCommandContext context)
        {
            var uxmlPaths = ExtractStringArray(context.ArgsJson, "uxml_paths", 100);
            var ussPaths = ExtractStringArray(context.ArgsJson, "uss_paths", 100);
            if (uxmlPaths.Count == 0 && ussPaths.Count == 0)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "uxml_paths or uss_paths is required");
            }

            var includeImages = JsonUtil.ExtractBool(context.ArgsJson, "include_images", true);
            var includeFonts = JsonUtil.ExtractBool(context.ArgsJson, "include_fonts", true);
            var includeTemplates = JsonUtil.ExtractBool(context.ArgsJson, "include_templates", true);
            var includeStyleSheets = JsonUtil.ExtractBool(context.ArgsJson, "include_style_sheets", true);
            var maxReferences = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_references", 500), 1, 500);
            var reportSourceId = NewId("uitk_refs");
            var references = new ReferenceList(maxReferences);
            var warnings = new List<string>();
            var uxmlResults = new List<string>();
            var ussResults = new List<string>();

            foreach (var path in uxmlPaths)
            {
                uxmlResults.Add(ReportUxmlReferencesPath(path, includeTemplates, includeStyleSheets, references, warnings));
            }
            foreach (var path in ussPaths)
            {
                ussResults.Add(ReportUssReferencesPath(path, includeImages, includeFonts, references, warnings));
            }

            var reportId = NewId("uitk_report");
            var summary = "Reported " + references.TotalCount + " UITK reference(s) from " + (uxmlPaths.Count + ussPaths.Count) + " file(s)";
            var resourceUri = ReferencesResourceUri(reportSourceId);
            var resultJson = "{\"status\":\"ok\""
                + ",\"report_id\":" + JsonUtil.String(reportId)
                + ",\"reference_report_id\":" + JsonUtil.String(reportSourceId)
                + ",\"read_only\":true"
                + ",\"uxml_files_checked\":" + uxmlPaths.Count
                + ",\"uss_files_checked\":" + ussPaths.Count
                + ",\"references_count\":" + references.TotalCount
                + ",\"returned_references_count\":" + references.ReturnedCount
                + ",\"references_truncated\":" + JsonUtil.Bool(references.Truncated)
                + ",\"resolved_count\":" + references.ResolvedCount
                + ",\"unresolved_count\":" + references.UnresolvedCount
                + ",\"references_by_kind\":" + references.KindCountsJson()
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"uxml_files\":" + JsonArray(uxmlResults)
                + ",\"uss_files\":" + JsonArray(ussResults)
                + ",\"references\":" + JsonArray(references.ReturnedReferences)
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.layout.analyze\",\"uitk.report\"]}";
            StoreResource(resourceUri, resultJson, "application/json");
            StoreReport(new UitkReportSnapshot(reportId, reportSourceId, "uitk.references.report", summary, resultJson, MarkdownReport("UITK references report", summary, warnings)));
            return CommandExecutionResult.Ok(resultJson);
        }

        [McpCommand(
            "uitk.resource.read",
            Title = "UI Toolkit resource read",
            Description = "Read a bounded process-local UI Toolkit resource emitted by previous UITK inspection, validation, diagnostics, or report commands",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ResourceReadSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "resource", "cache" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = ResourceReadOutputLimitJson,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult ResourceRead(McpCommandContext context)
        {
            var uri = JsonUtil.ExtractString(context.ArgsJson, "uri");
            var format = JsonUtil.ExtractString(context.ArgsJson, "format");
            var maxChars = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_chars", 200000), 1, 1000000);
            if (!IsUitkResourceUri(uri))
            {
                return CommandExecutionResult.Ok("{\"status\":\"invalid\",\"uri\":" + JsonUtil.String(uri)
                    + ",\"reason\":\"uri must be a unity-ui://uitk/... resource emitted by this Unity Bridge process\""
                    + ",\"read_only\":true}");
            }

            var resource = FindResource(uri);
            if (resource == null)
            {
                return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"uri\":" + JsonUtil.String(uri)
                    + ",\"reason\":\"resource was not found in this Unity Bridge process in-memory cache\""
                    + ",\"cache\":{\"storage\":\"memory\",\"max_resources\":" + MaxCachedResources + "}}");
            }

            var content = resource.Content;
            var returned = Math.Min(maxChars, content.Length);
            var truncated = returned < content.Length;
            var returnedContent = truncated ? content.Substring(0, returned) : content;
            return CommandExecutionResult.Ok("{\"status\":\"ok\""
                + ",\"uri\":" + JsonUtil.String(resource.Uri)
                + ",\"format\":" + JsonUtil.String(string.IsNullOrEmpty(format) ? "text" : format)
                + ",\"mime_type\":" + JsonUtil.String(resource.MimeType)
                + ",\"content\":" + JsonUtil.String(returnedContent)
                + ",\"chars\":" + returned
                + ",\"total_chars\":" + content.Length
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"warnings\":" + ResourceWarningsJson(truncated)
                + ",\"resource_refs\":[]"
                + ",\"artifact_refs\":[]"
                + ",\"read_only\":true"
                + ",\"risk\":\"low\""
                + ",\"requires_approval\":false}");
        }

        [McpCommand(
            "uitk.document.create",
            Title = "UI Toolkit document create",
            Description = "Create an isolated UI Toolkit UXML/USS skeleton, optional PanelSettings asset, and optional UIDocument prefab under Assets with dry-run support",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = DocumentCreateSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "document", "asset", "safe-write" },
            Capability = "uitk_safe_writes",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            OutputLimitJson = SafeWriteOutputLimitJson,
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult DocumentCreate(McpCommandContext context)
        {
            var status = RequireEditing(context);
            if (status != null)
            {
                return status;
            }

            var rootResult = RequireOutputRoot(context.ArgsJson, "output_root", out var outputRoot);
            if (rootResult != null)
            {
                return rootResult;
            }

            var nameResult = RequireIdentifierArgument(context.ArgsJson, "name", out var name);
            if (nameResult != null)
            {
                return nameResult;
            }

            var createPrefab = JsonUtil.ExtractBool(context.ArgsJson, "create_prefab", true);
            var createPanelSettings = JsonUtil.ExtractBool(context.ArgsJson, "create_panel_settings", true);
            var overwrite = JsonUtil.ExtractBool(context.ArgsJson, "overwrite", false);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var uxmlPath = outputRoot + "/" + name + ".uxml";
            var ussPath = outputRoot + "/" + name + ".uss";
            var panelSettingsPath = outputRoot + "/" + name + "PanelSettings.asset";
            var prefabPath = outputRoot + "/" + name + ".prefab";
            var artifacts = new List<GeneratedArtifact>
            {
                GeneratedArtifact.Text("uxml", uxmlPath, GenerateDocumentUxml(name, ussPath)),
                GeneratedArtifact.Text("uss", ussPath, GenerateDocumentUss(name))
            };

            if (createPanelSettings)
            {
                artifacts.Add(GeneratedArtifact.UnityObject("panel_settings", panelSettingsPath));
            }
            if (createPrefab)
            {
                artifacts.Add(GeneratedArtifact.UnityObject("prefab", prefabPath));
            }

            var validationResult = ValidateGeneratedArtifacts(artifacts, overwrite);
            if (validationResult != null)
            {
                return validationResult;
            }

            var summary = "Prepared UI Toolkit document artifacts for " + name;
            if (dryRun)
            {
                return CommandExecutionResult.Ok(SafeWritePlanJson("uitk.document.create", summary, true, outputRoot, artifacts, 0, 0, new string[] { "uitk.validate", "assets.refresh" }));
            }

            try
            {
                EnsureArtifactFolders(artifacts);
                WriteTextArtifacts(artifacts);
                if (createPanelSettings)
                {
                    var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
                    panelSettings.name = name + "PanelSettings";
                    AssetDatabase.CreateAsset(panelSettings, panelSettingsPath);
                    AssetDatabase.ImportAsset(panelSettingsPath, ImportAssetOptions.ForceUpdate);
                }
                if (createPrefab)
                {
                    CreateDocumentPrefab(name, prefabPath, uxmlPath, createPanelSettings ? panelSettingsPath : string.Empty);
                }
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("unity_exception", "UITK document artifact creation failed: " + exception.Message);
            }

            MarkArtifactsWritten(artifacts);
            return CommandExecutionResult.Ok(SafeWritePlanJson("uitk.document.create", summary, false, outputRoot, artifacts, CountCreated(artifacts), CountOverwritten(artifacts), new string[] { "uitk.document.inspect", "uitk.validate", "assets.refresh" }));
        }

        [McpCommand(
            "uitk.binder.generate",
            Title = "UI Toolkit binder generate",
            Description = "Generate a bounded C# UI Toolkit binder skeleton from named UXML elements under Assets with dry-run support",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = BinderGenerateSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "bindings", "script", "safe-write" },
            Capability = "uitk_safe_writes",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            OutputLimitJson = SafeWriteOutputLimitJson,
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult BinderGenerate(McpCommandContext context)
        {
            var status = RequireEditing(context);
            if (status != null)
            {
                return status;
            }

            var outputResult = RequireOutputAssetPath(context.ArgsJson, "output_path", ".cs", out var outputPath);
            if (outputResult != null)
            {
                return outputResult;
            }

            var classResult = RequireIdentifierArgument(context.ArgsJson, "class_name", out var className);
            if (classResult != null)
            {
                return classResult;
            }

            var namespaceResult = RequireOptionalNamespace(context.ArgsJson, out var namespaceName);
            if (namespaceResult != null)
            {
                return namespaceResult;
            }

            var pathResult = ReadTextAsset(JsonUtil.ExtractString(context.ArgsJson, "uxml_path"), ".uxml", MaxTextBytes, out var uxmlPath, out var content, out _, out _, out _);
            if (!pathResult)
            {
                return CommandExecutionResult.Error("invalid_field_value", "uxml_path must point to a readable .uxml asset");
            }

            List<UxmlNamedElement> elements;
            try
            {
                elements = ExtractNamedElementsForBinder(content, 200);
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "uxml_path could not be parsed as XML: " + exception.Message);
            }

            var includeUnsubscribeStubs = JsonUtil.ExtractBool(context.ArgsJson, "include_unsubscribe_stubs", true);
            var overwrite = JsonUtil.ExtractBool(context.ArgsJson, "overwrite", false);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var artifact = GeneratedArtifact.Text("csharp", outputPath, GenerateBinderCsharp(className, namespaceName, uxmlPath, elements, includeUnsubscribeStubs));
            var artifacts = new List<GeneratedArtifact> { artifact };
            var validationResult = ValidateGeneratedArtifacts(artifacts, overwrite);
            if (validationResult != null)
            {
                return validationResult;
            }

            var summary = "Prepared UI Toolkit binder " + className + " from " + uxmlPath + " with " + elements.Count + " named element(s)";
            if (dryRun)
            {
                return CommandExecutionResult.Ok(SafeWritePlanJson("uitk.binder.generate", summary, true, ParentAssetPath(outputPath), artifacts, 0, 0, new string[] { "uitk.bindings.validate", "assets.refresh" })
                    .TrimEnd('}') + ",\"source_uxml_path\":" + JsonUtil.String(uxmlPath) + ",\"named_elements\":" + elements.Count + "}");
            }

            try
            {
                EnsureArtifactFolders(artifacts);
                WriteTextArtifacts(artifacts);
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("unity_exception", "UITK binder generation failed: " + exception.Message);
            }

            MarkArtifactsWritten(artifacts);
            return CommandExecutionResult.Ok(SafeWritePlanJson("uitk.binder.generate", summary, false, ParentAssetPath(outputPath), artifacts, CountCreated(artifacts), CountOverwritten(artifacts), new string[] { "uitk.bindings.validate", "assets.refresh" })
                .TrimEnd('}') + ",\"source_uxml_path\":" + JsonUtil.String(uxmlPath) + ",\"named_elements\":" + elements.Count + "}");
        }

        [McpCommand(
            "uitk.template.generate",
            Title = "UI Toolkit template generate",
            Description = "Create an isolated reusable UI Toolkit control/template skeleton under Assets with dry-run support",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 30000,
            SchemaJson = TemplateGenerateSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "template", "control", "safe-write" },
            Capability = "uitk_safe_writes",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "mutation_result",
            OutputLimitJson = SafeWriteOutputLimitJson,
            FeatureFlag = SafeWritesFeatureFlag)]
        public static CommandExecutionResult TemplateGenerate(McpCommandContext context)
        {
            var status = RequireEditing(context);
            if (status != null)
            {
                return status;
            }

            var rootResult = RequireOutputRoot(context.ArgsJson, "output_root", out var outputRoot);
            if (rootResult != null)
            {
                return rootResult;
            }

            var controlResult = RequireIdentifierArgument(context.ArgsJson, "control_name", out var controlName);
            if (controlResult != null)
            {
                return controlResult;
            }

            var namespaceResult = RequireOptionalNamespace(context.ArgsJson, out var namespaceName);
            if (namespaceResult != null)
            {
                return namespaceResult;
            }

            var createUxml = JsonUtil.ExtractBool(context.ArgsJson, "create_uxml", true);
            var createUss = JsonUtil.ExtractBool(context.ArgsJson, "create_uss", true);
            var createCsharp = JsonUtil.ExtractBool(context.ArgsJson, "create_csharp", true);
            if (!createUxml && !createUss && !createCsharp)
            {
                return CommandExecutionResult.Error("schema_validation_failed", "At least one of create_uxml, create_uss, or create_csharp must be true");
            }

            var overwrite = JsonUtil.ExtractBool(context.ArgsJson, "overwrite", false);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true);
            var uxmlPath = outputRoot + "/" + controlName + ".uxml";
            var ussPath = outputRoot + "/" + controlName + ".uss";
            var csharpPath = outputRoot + "/" + controlName + ".cs";
            var artifacts = new List<GeneratedArtifact>();
            if (createUxml)
            {
                artifacts.Add(GeneratedArtifact.Text("uxml", uxmlPath, GenerateTemplateUxml(controlName, createUss ? ussPath : string.Empty)));
            }
            if (createUss)
            {
                artifacts.Add(GeneratedArtifact.Text("uss", ussPath, GenerateTemplateUss(controlName)));
            }
            if (createCsharp)
            {
                artifacts.Add(GeneratedArtifact.Text("csharp", csharpPath, GenerateTemplateCsharp(controlName, namespaceName)));
            }

            var validationResult = ValidateGeneratedArtifacts(artifacts, overwrite);
            if (validationResult != null)
            {
                return validationResult;
            }

            var summary = "Prepared UI Toolkit template artifacts for " + controlName;
            if (dryRun)
            {
                return CommandExecutionResult.Ok(SafeWritePlanJson("uitk.template.generate", summary, true, outputRoot, artifacts, 0, 0, new string[] { "uitk.validate", "assets.refresh" }));
            }

            try
            {
                EnsureArtifactFolders(artifacts);
                WriteTextArtifacts(artifacts);
            }
            catch (Exception exception)
            {
                return CommandExecutionResult.Error("unity_exception", "UITK template generation failed: " + exception.Message);
            }

            MarkArtifactsWritten(artifacts);
            return CommandExecutionResult.Ok(SafeWritePlanJson("uitk.template.generate", summary, false, outputRoot, artifacts, CountCreated(artifacts), CountOverwritten(artifacts), new string[] { "uitk.validate", "assets.refresh" }));
        }

        [McpCommand(
            "uitk.report",
            Title = "UI Toolkit report",
            Description = "Read a bounded markdown/json report for cached UI Toolkit inspection results or explicit UITK asset paths",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = ReportSchemaJson,
            Tags = new string[] { "uitk", "ui-toolkit", "report" },
            Capability = "uitk_inspection",
            ResultShape = "status",
            OutputLimitJson = ReportOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = InspectionFeatureFlag)]
        public static CommandExecutionResult Report(McpCommandContext context)
        {
            var inspectionId = JsonUtil.ExtractString(context.ArgsJson, "inspection_id");
            var validationId = JsonUtil.ExtractString(context.ArgsJson, "validation_id");
            var analysisId = JsonUtil.ExtractString(context.ArgsJson, "analysis_id");
            var referenceReportId = JsonUtil.ExtractString(context.ArgsJson, "reference_report_id");
            var format = JsonUtil.ExtractString(context.ArgsJson, "format");
            if (string.IsNullOrEmpty(format))
            {
                format = "markdown";
            }

            UitkReportSnapshot snapshot = null;
            if (!string.IsNullOrEmpty(inspectionId))
            {
                snapshot = FindReportBySourceId(inspectionId);
                if (snapshot == null)
                {
                    return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"inspection_id\":" + JsonUtil.String(inspectionId) + ",\"resource_refs\":[],\"artifact_refs\":[]}");
                }
            }
            else if (!string.IsNullOrEmpty(validationId))
            {
                snapshot = FindReportBySourceId(validationId);
                if (snapshot == null)
                {
                    return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"validation_id\":" + JsonUtil.String(validationId) + ",\"resource_refs\":[],\"artifact_refs\":[]}");
                }
            }
            else if (!string.IsNullOrEmpty(analysisId))
            {
                snapshot = FindReportBySourceId(analysisId);
                if (snapshot == null)
                {
                    return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"analysis_id\":" + JsonUtil.String(analysisId) + ",\"resource_refs\":[],\"artifact_refs\":[]}");
                }
            }
            else if (!string.IsNullOrEmpty(referenceReportId))
            {
                snapshot = FindReportBySourceId(referenceReportId);
                if (snapshot == null)
                {
                    return CommandExecutionResult.Ok("{\"status\":\"not_found\",\"reference_report_id\":" + JsonUtil.String(referenceReportId) + ",\"resource_refs\":[],\"artifact_refs\":[]}");
                }
            }
            else
            {
                var targets = ExtractStringArray(context.ArgsJson, "targets", 100);
                var uxmlPaths = ExtractStringArray(context.ArgsJson, "uxml_paths", 100);
                var ussPaths = ExtractStringArray(context.ArgsJson, "uss_paths", 100);
                if (targets.Count == 0 && uxmlPaths.Count == 0 && ussPaths.Count == 0)
                {
                    return CommandExecutionResult.Error("schema_validation_failed", "inspection_id, validation_id, analysis_id, reference_report_id, targets, uxml_paths, or uss_paths is required");
                }

                var reportId = NewId("uitk_report");
                var sourceId = NewId("uitk_explicit");
                var summary = "UI Toolkit explicit path report for " + (targets.Count + uxmlPaths.Count + ussPaths.Count) + " path(s)";
                var json = "{\"status\":\"ok\",\"source\":\"explicit_paths\",\"targets\":" + JsonArrayStrings(targets) + ",\"uxml_paths\":" + JsonArrayStrings(uxmlPaths) + ",\"uss_paths\":" + JsonArrayStrings(ussPaths) + ",\"summary\":" + JsonUtil.String(summary) + "}";
                snapshot = new UitkReportSnapshot(reportId, sourceId, "uitk.report", summary, json, MarkdownReport("UITK explicit path report", summary, new List<string>()));
                StoreReport(snapshot);
            }

            return CommandExecutionResult.Ok(ReportJson(snapshot, format));
        }

        static CommandExecutionResult RequireEditing(McpCommandContext context)
        {
            if (context.State.Status != "editing" && context.State.Status != "ready")
            {
                return CommandExecutionResult.Error("editor_not_ready", "Command requires editor status editing/ready");
            }

            return null;
        }

        static CommandExecutionResult RequireOutputRoot(string argsJson, string fieldName, out string outputRoot)
        {
            outputRoot = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, fieldName);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + fieldName + " is required");
            }

            try
            {
                outputRoot = PathGuard.RequireUnityAssetPath(rawPath).TrimEnd('/');
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " is invalid: " + exception.Message);
            }

            if (!outputRoot.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " must be a child path under Assets/");
            }
            if (outputRoot.EndsWith(".unity", StringComparison.OrdinalIgnoreCase) || outputRoot.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " must be a folder-like output root, not a scene or prefab path");
            }

            return null;
        }

        static CommandExecutionResult RequireOutputAssetPath(string argsJson, string fieldName, string requiredExtension, out string assetPath)
        {
            assetPath = string.Empty;
            var rawPath = JsonUtil.ExtractString(argsJson, fieldName);
            if (string.IsNullOrEmpty(rawPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + fieldName + " is required");
            }

            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " is invalid: " + exception.Message);
            }

            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " must be under Assets/");
            }
            if (!assetPath.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " must end with " + requiredExtension);
            }

            return null;
        }

        static CommandExecutionResult RequireIdentifierArgument(string argsJson, string fieldName, out string identifier)
        {
            identifier = JsonUtil.ExtractString(argsJson, fieldName);
            if (string.IsNullOrEmpty(identifier))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Argument " + fieldName + " is required");
            }
            if (!IsCSharpIdentifier(identifier) || IsCSharpKeyword(identifier))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " must be an ASCII C# identifier");
            }

            return null;
        }

        static CommandExecutionResult RequireOptionalNamespace(string argsJson, out string namespaceName)
        {
            namespaceName = JsonUtil.ExtractString(argsJson, "namespace");
            if (string.IsNullOrEmpty(namespaceName))
            {
                return null;
            }

            foreach (var segment in namespaceName.Split('.'))
            {
                if (!IsCSharpIdentifier(segment) || IsCSharpKeyword(segment))
                {
                    return CommandExecutionResult.Error("invalid_field_value", "namespace must contain dot-separated ASCII C# identifiers");
                }
            }

            return null;
        }

        static CommandExecutionResult ValidateGeneratedArtifacts(List<GeneratedArtifact> artifacts, bool overwrite)
        {
            foreach (var artifact in artifacts)
            {
                if (artifact.Content != null && (artifact.Content.Length > MaxGeneratedTextChars || artifact.Content.IndexOf('\0') >= 0))
                {
                    return CommandExecutionResult.Error("invalid_field_value", artifact.Kind + " generated content is too large or contains NUL bytes");
                }

                var fullPath = AbsoluteProjectAssetPath(artifact.AssetPath);
                if (!IsSameOrChildPath(fullPath, ProjectPaths.ProjectRoot))
                {
                    return CommandExecutionResult.Error("invalid_field_value", artifact.AssetPath + " resolves outside the project root");
                }

                artifact.ExistedBefore = File.Exists(fullPath) || AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(artifact.AssetPath) != null;
                if (!artifact.ExistedBefore)
                {
                    continue;
                }

                if (!overwrite)
                {
                    return CommandExecutionResult.Error("invalid_field_value", artifact.AssetPath + " already exists; set overwrite:true to replace text artifacts");
                }
                if (!artifact.AllowOverwrite)
                {
                    return CommandExecutionResult.Error("invalid_field_value", artifact.AssetPath + " already exists and this artifact kind cannot be overwritten");
                }
            }

            return null;
        }

        static void EnsureArtifactFolders(List<GeneratedArtifact> artifacts)
        {
            foreach (var artifact in artifacts)
            {
                var fullPath = AbsoluteProjectAssetPath(artifact.AssetPath);
                var parent = Path.GetDirectoryName(fullPath);
                if (string.IsNullOrEmpty(parent) || !IsSameOrChildPath(parent, ProjectPaths.ProjectRoot))
                {
                    throw new InvalidOperationException("Artifact parent resolves outside the project root: " + artifact.AssetPath);
                }

                Directory.CreateDirectory(parent);
            }
        }

        static void WriteTextArtifacts(List<GeneratedArtifact> artifacts)
        {
            foreach (var artifact in artifacts)
            {
                if (artifact.Content == null)
                {
                    continue;
                }

                File.WriteAllText(AbsoluteProjectAssetPath(artifact.AssetPath), artifact.Content, new UTF8Encoding(false));
                AssetDatabase.ImportAsset(artifact.AssetPath, ImportAssetOptions.ForceUpdate);
            }
        }

        static void CreateDocumentPrefab(string name, string prefabPath, string uxmlPath, string panelSettingsPath)
        {
            var root = new GameObject(name);
            try
            {
                var document = root.AddComponent<UIDocument>();
                document.visualTreeAsset = AssetDatabase.LoadAssetAtPath<VisualTreeAsset>(uxmlPath);
                if (!string.IsNullOrEmpty(panelSettingsPath))
                {
                    document.panelSettings = AssetDatabase.LoadAssetAtPath<PanelSettings>(panelSettingsPath);
                }

                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                if (prefab == null)
                {
                    throw new InvalidOperationException("PrefabUtility.SaveAsPrefabAsset returned null");
                }

                AssetDatabase.ImportAsset(prefabPath, ImportAssetOptions.ForceUpdate);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        static void CreateMigrationPanelSettings(string targetName, string panelSettingsPath)
        {
            var panelSettings = ScriptableObject.CreateInstance<PanelSettings>();
            panelSettings.name = targetName + "PanelSettings";
            AssetDatabase.CreateAsset(panelSettings, panelSettingsPath);
            AssetDatabase.ImportAsset(panelSettingsPath, ImportAssetOptions.ForceUpdate);
        }

        static void MarkArtifactsWritten(List<GeneratedArtifact> artifacts)
        {
            foreach (var artifact in artifacts)
            {
                artifact.Written = true;
            }
        }

        static int CountCreated(List<GeneratedArtifact> artifacts)
        {
            var count = 0;
            foreach (var artifact in artifacts)
            {
                if (!artifact.ExistedBefore)
                {
                    count++;
                }
            }
            return count;
        }

        static int CountOverwritten(List<GeneratedArtifact> artifacts)
        {
            var count = 0;
            foreach (var artifact in artifacts)
            {
                if (artifact.ExistedBefore)
                {
                    count++;
                }
            }
            return count;
        }

        static string SafeWritePlanJson(string command, string summary, bool dryRun, string outputRoot, List<GeneratedArtifact> artifacts, int createdCount, int overwrittenCount, string[] nextValidActions, string[] resourceRefs = null)
        {
            return "{\"status\":\"ok\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"would_write\":" + JsonUtil.Bool(dryRun)
                + ",\"written\":" + JsonUtil.Bool(!dryRun)
                + ",\"output_root\":" + JsonUtil.String(outputRoot)
                + ",\"summary\":" + JsonUtil.String(summary)
                + ",\"files_to_create\":" + ArtifactsJson(artifacts, false)
                + ",\"files_created\":" + (dryRun ? "[]" : ArtifactsJson(artifacts, true))
                + ",\"created_count\":" + createdCount
                + ",\"overwritten_count\":" + overwrittenCount
                + ",\"resource_refs\":" + ResourceRefsJson(resourceRefs ?? new string[0])
                + ",\"artifact_refs\":" + ArtifactRefsJson(artifacts, dryRun)
                + ",\"warnings\":[]"
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":" + JsonArrayStrings(nextValidActions)
                + "}";
        }

        static string ArtifactsJson(List<GeneratedArtifact> artifacts, bool includeGuid)
        {
            var values = new List<string>();
            foreach (var artifact in artifacts)
            {
                values.Add(artifact.ToJson(includeGuid));
            }
            return JsonArray(values);
        }

        static string ArtifactRefsJson(List<GeneratedArtifact> artifacts, bool dryRun)
        {
            if (dryRun)
            {
                return "[]";
            }

            var values = new List<string>();
            foreach (var artifact in artifacts)
            {
                values.Add("{\"kind\":" + JsonUtil.String(artifact.Kind)
                    + ",\"asset_path\":" + JsonUtil.String(artifact.AssetPath)
                    + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(artifact.AssetPath))
                    + "}");
            }
            return JsonArray(values);
        }

        static List<GeneratedArtifact> MigrationGenerateArtifacts(string planId, string generationId, string scanId, string strategy, string outputRoot, string targetName, string planJson)
        {
            var uxmlPath = outputRoot + "/" + targetName + ".uxml";
            var ussPath = outputRoot + "/" + targetName + ".uss";
            var binderPath = outputRoot + "/" + targetName + "Binder.cs";
            var panelSettingsPath = outputRoot + "/" + targetName + "PanelSettings.asset";
            var prefabPath = outputRoot + "/" + targetName + "UIDocument.prefab";
            var migrationMapPath = outputRoot + "/" + targetName + ".migration-map.json";
            var reportMarkdownPath = outputRoot + "/" + targetName + ".conversion-report.md";
            var reportJsonPath = outputRoot + "/" + targetName + ".conversion-report.json";
            var namedElements = new List<UxmlNamedElement>
            {
                new UxmlNamedElement(targetName + "Root", "VisualElement"),
                new UxmlNamedElement(targetName + "Title", "Label")
            };

            return new List<GeneratedArtifact>
            {
                GeneratedArtifact.Text("uxml", uxmlPath, GenerateDocumentUxml(targetName, ussPath)),
                GeneratedArtifact.Text("uss", ussPath, GenerateDocumentUss(targetName)),
                GeneratedArtifact.Text("csharp", binderPath, GenerateBinderCsharp(targetName + "Binder", string.Empty, uxmlPath, namedElements, true)),
                GeneratedArtifact.UnityObject("panel_settings", panelSettingsPath),
                GeneratedArtifact.UnityObject("prefab", prefabPath),
                GeneratedArtifact.Text("migration_map", migrationMapPath, MigrationMapJson(planId, generationId, scanId, strategy, targetName, outputRoot, uxmlPath, ussPath, binderPath, panelSettingsPath, prefabPath)),
                GeneratedArtifact.Text("conversion_report_markdown", reportMarkdownPath, MigrationConversionReportMarkdown(planId, generationId, scanId, strategy, targetName, outputRoot)),
                GeneratedArtifact.Text("conversion_report_json", reportJsonPath, MigrationConversionReportJson(planId, generationId, scanId, strategy, targetName, outputRoot, planJson))
            };
        }

        static string MigrationGenerateResultJson(string summary, bool dryRun, string outputRoot, string targetName, string planId, string generationId, string scanId, string strategy, List<GeneratedArtifact> artifacts, int createdCount, int overwrittenCount, string planResourceUri, string generationResourceUri)
        {
            return SafeWritePlanJson("uitk.generate", summary, dryRun, outputRoot, artifacts, createdCount, overwrittenCount, new string[] { "uitk.validate", "uitk.bindings.validate", "assets.refresh" }, new string[] { generationResourceUri })
                .TrimEnd('}')
                + ",\"mode\":\"create_only\""
                + ",\"plan_id\":" + JsonUtil.String(planId)
                + ",\"generation_id\":" + JsonUtil.String(generationId)
                + ",\"target_name\":" + JsonUtil.String(targetName)
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"strategy\":" + JsonUtil.String(strategy)
                + ",\"legacy_modified\":false"
                + ",\"files_to_modify\":[]"
                + ",\"source_plan_resource\":" + JsonUtil.String(planResourceUri)
                + ",\"generation_resource\":" + JsonUtil.String(generationResourceUri)
                + "}";
        }

        static string MigrationMapJson(string planId, string generationId, string scanId, string strategy, string targetName, string outputRoot, string uxmlPath, string ussPath, string binderPath, string panelSettingsPath, string prefabPath)
        {
            return "{\"schema_version\":1"
                + ",\"migration_mode\":\"ugui_to_uitk\""
                + ",\"plan_id\":" + JsonUtil.String(planId)
                + ",\"generation_id\":" + JsonUtil.String(generationId)
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"strategy\":" + JsonUtil.String(strategy)
                + ",\"target_name\":" + JsonUtil.String(targetName)
                + ",\"output_root\":" + JsonUtil.String(outputRoot)
                + ",\"legacy_modified\":false"
                + ",\"node_mappings\":[]"
                + ",\"artifacts\":{\"uxml\":" + JsonUtil.String(uxmlPath)
                + ",\"uss\":" + JsonUtil.String(ussPath)
                + ",\"binder\":" + JsonUtil.String(binderPath)
                + ",\"panel_settings\":" + JsonUtil.String(panelSettingsPath)
                + ",\"prefab\":" + JsonUtil.String(prefabPath)
                + "}"
                + ",\"manual_tasks\":" + JsonArrayStrings(MigrationPlanManualTasks(strategy))
                + "}\n";
        }

        static string MigrationConversionReportMarkdown(string planId, string generationId, string scanId, string strategy, string targetName, string outputRoot)
        {
            var builder = new StringBuilder();
            builder.Append("# Canvas/uGUI To UI Toolkit Conversion Report").Append('\n').Append('\n');
            builder.Append("- plan_id: ").Append(planId).Append('\n');
            builder.Append("- generation_id: ").Append(generationId).Append('\n');
            builder.Append("- scan_id: ").Append(scanId).Append('\n');
            builder.Append("- strategy: ").Append(strategy).Append('\n');
            builder.Append("- target_name: ").Append(targetName).Append('\n');
            builder.Append("- output_root: ").Append(outputRoot).Append('\n');
            builder.Append('\n').Append("## Status").Append('\n');
            builder.Append("Create-only side-by-side scaffold. Legacy Canvas scene/prefab was not modified.").Append('\n');
            builder.Append('\n').Append("## Manual Follow-up").Append('\n');
            foreach (var task in MigrationPlanManualTasks(strategy))
            {
                builder.Append("- ").Append(task).Append('\n');
            }
            return builder.ToString();
        }

        static string MigrationConversionReportJson(string planId, string generationId, string scanId, string strategy, string targetName, string outputRoot, string planJson)
        {
            return "{\"schema_version\":1"
                + ",\"migration_mode\":\"ugui_to_uitk\""
                + ",\"plan_id\":" + JsonUtil.String(planId)
                + ",\"generation_id\":" + JsonUtil.String(generationId)
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"strategy\":" + JsonUtil.String(strategy)
                + ",\"target_name\":" + JsonUtil.String(targetName)
                + ",\"output_root\":" + JsonUtil.String(outputRoot)
                + ",\"legacy_modified\":false"
                + ",\"source_plan\":" + planJson
                + ",\"manual_tasks\":" + JsonArrayStrings(MigrationPlanManualTasks(strategy))
                + "}\n";
        }

        static string GenerateDocumentUxml(string name, string ussPath)
        {
            var rootClass = CssClassName(name) + "-root";
            return "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\" editor-extension-mode=\"False\">\n"
                + "    <Style src=\"" + XmlEscape("project://database/" + ussPath) + "\" />\n"
                + "    <ui:VisualElement name=\"" + XmlEscape(name + "Root") + "\" class=\"" + XmlEscape(rootClass) + "\">\n"
                + "        <ui:Label name=\"" + XmlEscape(name + "Title") + "\" text=\"" + XmlEscape(name) + "\" class=\"" + XmlEscape(CssClassName(name) + "-title") + "\" />\n"
                + "    </ui:VisualElement>\n"
                + "</ui:UXML>\n";
        }

        static string GenerateDocumentUss(string name)
        {
            var block = CssClassName(name);
            return "." + block + "-root {\n"
                + "    flex-grow: 1;\n"
                + "    padding: 16px;\n"
                + "}\n\n"
                + "." + block + "-title {\n"
                + "    -unity-font-style: bold;\n"
                + "    font-size: 18px;\n"
                + "}\n";
        }

        static string GenerateTemplateUxml(string controlName, string ussPath)
        {
            var block = CssClassName(controlName);
            var styleLine = string.IsNullOrEmpty(ussPath) ? string.Empty : "    <Style src=\"" + XmlEscape("project://database/" + ussPath) + "\" />\n";
            return "<ui:UXML xmlns:ui=\"UnityEngine.UIElements\" xmlns:uie=\"UnityEditor.UIElements\" editor-extension-mode=\"False\">\n"
                + styleLine
                + "    <ui:VisualElement name=\"" + XmlEscape(controlName + "Root") + "\" class=\"" + XmlEscape(block) + "\">\n"
                + "        <ui:Label name=\"" + XmlEscape(controlName + "Label") + "\" text=\"" + XmlEscape(controlName) + "\" class=\"" + XmlEscape(block + "__label") + "\" />\n"
                + "    </ui:VisualElement>\n"
                + "</ui:UXML>\n";
        }

        static string GenerateTemplateUss(string controlName)
        {
            var block = CssClassName(controlName);
            return "." + block + " {\n"
                + "    flex-grow: 1;\n"
                + "    padding: 8px;\n"
                + "}\n\n"
                + "." + block + "__label {\n"
                + "    unity-text-align: middle-left;\n"
                + "}\n";
        }

        static string GenerateTemplateCsharp(string controlName, string namespaceName)
        {
            var indent = string.IsNullOrEmpty(namespaceName) ? string.Empty : "    ";
            var builder = new StringBuilder();
            builder.Append("using UnityEngine.UIElements;\n\n");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                builder.Append("namespace ").Append(namespaceName).Append("\n{\n");
            }
            builder.Append(indent).Append("public sealed class ").Append(controlName).Append(" : VisualElement\n");
            builder.Append(indent).Append("{\n");
            builder.Append(indent).Append("    public new class UxmlFactory : UxmlFactory<").Append(controlName).Append(", UxmlTraits> { }\n");
            builder.Append(indent).Append("    public new class UxmlTraits : VisualElement.UxmlTraits { }\n\n");
            builder.Append(indent).Append("    public ").Append(controlName).Append("()\n");
            builder.Append(indent).Append("    {\n");
            builder.Append(indent).Append("        AddToClassList(").Append(CSharpString(CssClassName(controlName))).Append(");\n");
            builder.Append(indent).Append("    }\n");
            builder.Append(indent).Append("}\n");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                builder.Append("}\n");
            }
            return builder.ToString();
        }

        static string GenerateBinderCsharp(string className, string namespaceName, string uxmlPath, List<UxmlNamedElement> elements, bool includeUnsubscribeStubs)
        {
            var indent = string.IsNullOrEmpty(namespaceName) ? string.Empty : "    ";
            var fields = BinderFields(elements);
            var builder = new StringBuilder();
            builder.Append("using UnityEngine.UIElements;\n\n");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                builder.Append("namespace ").Append(namespaceName).Append("\n{\n");
            }
            builder.Append(indent).Append("public sealed class ").Append(className).Append("\n");
            builder.Append(indent).Append("{\n");
            builder.Append(indent).Append("    readonly VisualElement _root;\n");
            foreach (var field in fields)
            {
                builder.Append(indent).Append("    VisualElement ").Append(field.FieldName).Append("; // ").Append(SingleLineCommentText(field.Element.TypeName)).Append(" ").Append(SingleLineCommentText(field.Element.Name)).Append("\n");
            }
            builder.Append("\n");
            builder.Append(indent).Append("    public ").Append(className).Append("(VisualElement root)\n");
            builder.Append(indent).Append("    {\n");
            builder.Append(indent).Append("        _root = root;\n");
            builder.Append(indent).Append("    }\n\n");
            builder.Append(indent).Append("    public void Bind()\n");
            builder.Append(indent).Append("    {\n");
            builder.Append(indent).Append("        // Generated from ").Append(uxmlPath).Append(". Keep names in sync with UXML.\n");
            foreach (var field in fields)
            {
                builder.Append(indent).Append("        ").Append(field.FieldName).Append(" = _root.Q<VisualElement>(").Append(CSharpString(field.Element.Name)).Append(");\n");
            }
            builder.Append(indent).Append("    }\n");
            if (includeUnsubscribeStubs)
            {
                builder.Append("\n");
                builder.Append(indent).Append("    public void Unbind()\n");
                builder.Append(indent).Append("    {\n");
                foreach (var field in fields)
                {
                    builder.Append(indent).Append("        ").Append(field.FieldName).Append(" = null;\n");
                }
                builder.Append(indent).Append("    }\n");
            }
            builder.Append(indent).Append("}\n");
            if (!string.IsNullOrEmpty(namespaceName))
            {
                builder.Append("}\n");
            }
            return builder.ToString();
        }

        static List<BinderField> BinderFields(List<UxmlNamedElement> elements)
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            var fields = new List<BinderField>();
            foreach (var element in elements)
            {
                var fieldName = UniqueFieldName(element.Name, used);
                fields.Add(new BinderField(element, fieldName));
            }
            return fields;
        }

        static List<UxmlNamedElement> ExtractNamedElementsForBinder(string content, int maxElements)
        {
            var document = XDocument.Parse(content, LoadOptions.None);
            var elements = new List<UxmlNamedElement>();
            var names = new HashSet<string>(StringComparer.Ordinal);
            TraverseNamedElements(document.Root, elements, names, maxElements);
            return elements;
        }

        static void TraverseNamedElements(XElement element, List<UxmlNamedElement> elements, HashSet<string> names, int maxElements)
        {
            if (element == null || elements.Count >= maxElements)
            {
                return;
            }

            var name = AttributeValue(element, "name");
            if (!string.IsNullOrEmpty(name) && names.Add(name))
            {
                elements.Add(new UxmlNamedElement(name, element.Name.LocalName));
            }

            foreach (var child in element.Elements())
            {
                TraverseNamedElements(child, elements, names, maxElements);
            }
        }

        static string UniqueFieldName(string sourceName, HashSet<string> used)
        {
            var identifier = ToIdentifier(sourceName);
            if (string.IsNullOrEmpty(identifier))
            {
                identifier = "element";
            }
            identifier = char.ToLowerInvariant(identifier[0]) + identifier.Substring(1);
            var baseName = "_" + identifier;
            var candidate = baseName;
            var suffix = 2;
            while (!used.Add(candidate))
            {
                candidate = baseName + suffix;
                suffix++;
            }
            return candidate;
        }

        static string ToIdentifier(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value ?? string.Empty)
            {
                if ((character >= 'A' && character <= 'Z') || (character >= 'a' && character <= 'z') || (character >= '0' && character <= '9') || character == '_')
                {
                    builder.Append(character);
                }
                else
                {
                    builder.Append('_');
                }
            }
            if (builder.Length == 0)
            {
                return string.Empty;
            }
            if (builder[0] >= '0' && builder[0] <= '9')
            {
                builder.Insert(0, '_');
            }
            return builder.ToString();
        }

        static bool IsCSharpIdentifier(string value)
        {
            return !string.IsNullOrEmpty(value) && Regex.IsMatch(value, @"^[A-Za-z_][A-Za-z0-9_]*$");
        }

        static bool IsCSharpKeyword(string value)
        {
            switch (value)
            {
                case "class":
                case "namespace":
                case "public":
                case "private":
                case "protected":
                case "internal":
                case "void":
                case "string":
                case "int":
                case "float":
                case "double":
                case "bool":
                case "new":
                case "return":
                case "using":
                    return true;
                default:
                    return false;
            }
        }

        static string CssClassName(string value)
        {
            var builder = new StringBuilder();
            var previousDash = false;
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                if (character >= 'A' && character <= 'Z')
                {
                    if (index > 0 && !previousDash)
                    {
                        builder.Append('-');
                    }
                    builder.Append(char.ToLowerInvariant(character));
                    previousDash = false;
                }
                else if ((character >= 'a' && character <= 'z') || (character >= '0' && character <= '9'))
                {
                    builder.Append(character);
                    previousDash = false;
                }
                else if (!previousDash)
                {
                    builder.Append('-');
                    previousDash = true;
                }
            }
            return builder.ToString().Trim('-');
        }

        static string XmlEscape(string value)
        {
            return (value ?? string.Empty)
                .Replace("&", "&amp;")
                .Replace("\"", "&quot;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
        }

        static string CSharpString(string value)
        {
            return "\"" + (value ?? string.Empty)
                .Replace("\\", "\\\\")
                .Replace("\"", "\\\"")
                .Replace("\r", "\\r")
                .Replace("\n", "\\n")
                .Replace("\t", "\\t") + "\"";
        }

        static string SingleLineCommentText(string value)
        {
            return (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
        }

        static string ParentAssetPath(string assetPath)
        {
            var index = assetPath.LastIndexOf('/');
            return index <= 0 ? "Assets" : assetPath.Substring(0, index);
        }

        static string AbsoluteProjectAssetPath(string assetPath)
        {
            return Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
        }

        static bool IsSameOrChildAssetPath(string assetPath, string rootPath)
        {
            return string.Equals(assetPath, rootPath, StringComparison.Ordinal)
                || assetPath.StartsWith(rootPath.TrimEnd('/') + "/", StringComparison.Ordinal);
        }

        static string AnalyzeUxmlLayoutPath(string rawPath, UxmlLayoutInfo aggregate, FindingList findings, List<ScreenSize> screenSizes)
        {
            var pathResult = ReadTextAsset(rawPath, ".uxml", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                findings.Add("error", "UITK_LAYOUT_UXML_READ_FAILED", "UXML could not be read for layout analysis", rawPath, string.Empty);
                return errorJson;
            }

            try
            {
                var document = XDocument.Parse(content, LoadOptions.None);
                var fileInfo = new UxmlLayoutInfo();
                var truncated = false;
                TraverseUxmlLayout(document.Root, 0, 5000, path, fileInfo, findings, screenSizes, ref truncated);
                aggregate.Merge(fileInfo);
                return "{\"path\":" + JsonUtil.String(path)
                    + ",\"status\":\"ok\""
                    + ",\"byte_length\":" + byteLength
                    + ",\"truncated\":" + JsonUtil.Bool(fileTruncated || truncated)
                    + ",\"elements_found\":" + fileInfo.ElementsFound
                    + ",\"classes_found\":" + fileInfo.Classes.Count
                    + ",\"root_classes\":" + JsonArrayStrings(DedupeSorted(fileInfo.RootClasses))
                    + ",\"scroll_containers\":" + fileInfo.ScrollContainers
                    + "}";
            }
            catch (Exception exception)
            {
                findings.Add("error", "UITK_LAYOUT_UXML_INVALID", "UXML failed XML parsing during layout analysis: " + exception.Message, path, string.Empty);
                return "{\"path\":" + JsonUtil.String(path) + ",\"status\":\"invalid_xml\",\"message\":" + JsonUtil.String(exception.Message) + ",\"byte_length\":" + byteLength + ",\"truncated\":" + JsonUtil.Bool(fileTruncated) + "}";
            }
        }

        static string AnalyzeUssLayoutPath(string rawPath, bool includeAbsolutePositioning, List<ScreenSize> screenSizes, FindingList findings, List<UssRuleInfo> aggregateRules)
        {
            var pathResult = ReadTextAsset(rawPath, ".uss", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                findings.Add("error", "UITK_LAYOUT_USS_READ_FAILED", "USS could not be read for layout analysis", rawPath, string.Empty);
                return errorJson;
            }

            var rules = ExtractUssRuleInfos(content, 5000, path, out var truncated);
            var absoluteRules = 0;
            var fixedPixelValues = 0;
            var selectorsFound = 0;
            var classes = new HashSet<string>();
            foreach (var rule in rules)
            {
                aggregateRules.Add(rule);
                selectorsFound += rule.Selectors.Count;
                foreach (var className in rule.Classes)
                {
                    classes.Add(className);
                }
                AddUssLayoutFindings(rule, includeAbsolutePositioning, screenSizes, findings, ref absoluteRules, ref fixedPixelValues);
            }

            return "{\"path\":" + JsonUtil.String(path)
                + ",\"status\":\"ok\""
                + ",\"byte_length\":" + byteLength
                + ",\"truncated\":" + JsonUtil.Bool(fileTruncated || truncated)
                + ",\"rules_found\":" + rules.Count
                + ",\"selectors_found\":" + selectorsFound
                + ",\"classes_found\":" + classes.Count
                + ",\"absolute_position_rules\":" + absoluteRules
                + ",\"fixed_pixel_values\":" + fixedPixelValues
                + "}";
        }

        static void TraverseUxmlLayout(XElement element, int depth, int maxElements, string sourcePath, UxmlLayoutInfo info, FindingList findings, List<ScreenSize> screenSizes, ref bool truncated)
        {
            if (element == null || truncated)
            {
                return;
            }
            if (info.ElementsFound >= maxElements)
            {
                truncated = true;
                return;
            }

            info.ElementsFound++;
            var typeName = element.Name.LocalName;
            info.Types.Add(typeName);
            var isVisualRoot = depth == 1 || (depth == 0 && !string.Equals(typeName, "UXML", StringComparison.OrdinalIgnoreCase));
            var classValue = AttributeValue(element, "class");
            foreach (var className in SplitWhitespace(classValue))
            {
                info.Classes.Add(className);
                if (isVisualRoot)
                {
                    info.RootClasses.Add(className);
                }
                if (LooksLikeScrollName(className))
                {
                    info.ScrollClasses.Add(className);
                }
            }

            if (typeName.IndexOf("ScrollView", StringComparison.OrdinalIgnoreCase) >= 0 || LooksLikeScrollName(classValue))
            {
                info.ScrollContainers++;
            }

            var style = AttributeValue(element, "style");
            if (!string.IsNullOrEmpty(style))
            {
                AddInlineStyleLayoutFindings(sourcePath, typeName, style, findings, screenSizes);
            }

            foreach (var child in element.Elements())
            {
                TraverseUxmlLayout(child, depth + 1, maxElements, sourcePath, info, findings, screenSizes, ref truncated);
            }
        }

        static void AddInlineStyleLayoutFindings(string sourcePath, string typeName, string style, FindingList findings, List<ScreenSize> screenSizes)
        {
            if (Regex.IsMatch(style, @"(?i)\bposition\s*:\s*absolute\b"))
            {
                findings.Add("warning", "UITK_LAYOUT_ABSOLUTE_INLINE", "Inline UXML style uses absolute positioning", sourcePath, typeName);
            }

            foreach (Match match in Regex.Matches(style, @"(?i)([A-Za-z-]+)\s*:\s*(-?\d+(?:\.\d+)?)px"))
            {
                if (TryParseDouble(match.Groups[2].Value, out var pixels) && IsLargeFixedPixel(pixels, screenSizes))
                {
                    findings.Add("warning", "UITK_LAYOUT_FIXED_PIXEL", "Inline UXML style uses a large fixed pixel value", sourcePath, match.Groups[1].Value + ":" + match.Groups[2].Value + "px");
                }
            }
        }

        static void AddUssLayoutFindings(UssRuleInfo rule, bool includeAbsolutePositioning, List<ScreenSize> screenSizes, FindingList findings, ref int absoluteRules, ref int fixedPixelValues)
        {
            if (Regex.IsMatch(rule.Body, @"(?i)\bposition\s*:\s*absolute\b"))
            {
                absoluteRules++;
                if (includeAbsolutePositioning)
                {
                    findings.Add("warning", "UITK_LAYOUT_ABSOLUTE_POSITION", "USS rule uses absolute positioning", rule.SourcePath, rule.SelectorGroup);
                }
            }

            foreach (Match match in Regex.Matches(rule.Body, @"(?i)([A-Za-z-]*(?:width|height|left|right|top|bottom|margin|padding|size)[A-Za-z-]*)\s*:\s*(-?\d+(?:\.\d+)?)px"))
            {
                if (TryParseDouble(match.Groups[2].Value, out var pixels) && IsLargeFixedPixel(pixels, screenSizes))
                {
                    fixedPixelValues++;
                    findings.Add("warning", "UITK_LAYOUT_FIXED_PIXEL", "USS rule uses a large fixed pixel value", rule.SourcePath, rule.SelectorGroup + " " + match.Groups[1].Value + ":" + match.Groups[2].Value + "px");
                }
            }
        }

        static void AddCrossLayoutFindings(UxmlLayoutInfo uxmlInfo, List<UssRuleInfo> ussRules, FindingList findings)
        {
            if (uxmlInfo.ElementsFound > 0 && uxmlInfo.RootClasses.Count == 0)
            {
                findings.Add("info", "UITK_LAYOUT_ROOT_CLASS_MISSING", "UXML visual root has no class for reusable responsive USS rules", string.Empty, string.Empty);
            }

            foreach (var rootClass in uxmlInfo.RootClasses)
            {
                if (!HasClassRuleWithFlexGrow(rootClass, ussRules))
                {
                    findings.Add("info", "UITK_LAYOUT_ROOT_FLEX_GROW", "UXML root class has no obvious USS flex-grow rule", string.Empty, rootClass);
                }
            }

            if (uxmlInfo.Classes.Count > 0)
            {
                var reported = new HashSet<string>();
                foreach (var rule in ussRules)
                {
                    foreach (var className in rule.Classes)
                    {
                        if (!uxmlInfo.Classes.Contains(className) && reported.Add(className))
                        {
                            findings.Add("info", "UITK_LAYOUT_SELECTOR_UNUSED", "USS class selector was not found in provided UXML classes", rule.SourcePath, className);
                        }
                    }
                }
            }

            foreach (var scrollClass in uxmlInfo.ScrollClasses)
            {
                if (!HasClassRuleWithBoundedSizeOrFlex(scrollClass, ussRules))
                {
                    findings.Add("info", "UITK_LAYOUT_SCROLL_UNBOUNDED", "Scroll-looking UXML class has no obvious bounded size or flex rule", string.Empty, scrollClass);
                }
            }
        }

        static string ReportUxmlReferencesPath(string rawPath, bool includeTemplates, bool includeStyleSheets, ReferenceList references, List<string> warnings)
        {
            var pathResult = ReadTextAsset(rawPath, ".uxml", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                warnings.Add("UXML reference read failed: " + rawPath);
                return errorJson;
            }

            try
            {
                var document = XDocument.Parse(content, LoadOptions.None);
                var styleRefs = new List<string>();
                var templateRefs = new List<string>();
                CollectUxmlReferences(document.Root, styleRefs, templateRefs);
                var includedStyleRefs = 0;
                var includedTemplateRefs = 0;
                if (includeStyleSheets)
                {
                    foreach (var reference in styleRefs)
                    {
                        references.Add(path, "style_sheet", reference);
                        includedStyleRefs++;
                    }
                }
                if (includeTemplates)
                {
                    foreach (var reference in templateRefs)
                    {
                        references.Add(path, "template", reference);
                        includedTemplateRefs++;
                    }
                }

                return "{\"path\":" + JsonUtil.String(path)
                    + ",\"status\":\"ok\""
                    + ",\"byte_length\":" + byteLength
                    + ",\"truncated\":" + JsonUtil.Bool(fileTruncated)
                    + ",\"style_references\":" + styleRefs.Count
                    + ",\"included_style_references\":" + includedStyleRefs
                    + ",\"template_references\":" + templateRefs.Count
                    + ",\"included_template_references\":" + includedTemplateRefs
                    + "}";
            }
            catch (Exception exception)
            {
                warnings.Add("invalid UXML references: " + path);
                return "{\"path\":" + JsonUtil.String(path) + ",\"status\":\"invalid_xml\",\"message\":" + JsonUtil.String(exception.Message) + ",\"byte_length\":" + byteLength + ",\"truncated\":" + JsonUtil.Bool(fileTruncated) + "}";
            }
        }

        static string ReportUssReferencesPath(string rawPath, bool includeImages, bool includeFonts, ReferenceList references, List<string> warnings)
        {
            var pathResult = ReadTextAsset(rawPath, ".uss", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                warnings.Add("USS reference read failed: " + rawPath);
                return errorJson;
            }

            var rawReferences = ExtractUssAssetReferences(content);
            var included = 0;
            foreach (var reference in rawReferences)
            {
                var kind = ReferenceKindFromAssetReference(reference);
                if ((kind == "image" && !includeImages) || (kind == "font" && !includeFonts))
                {
                    continue;
                }

                references.Add(path, kind, reference);
                included++;
            }

            return "{\"path\":" + JsonUtil.String(path)
                + ",\"status\":\"ok\""
                + ",\"byte_length\":" + byteLength
                + ",\"truncated\":" + JsonUtil.Bool(fileTruncated)
                + ",\"asset_references\":" + rawReferences.Count
                + ",\"included_asset_references\":" + included
                + "}";
        }

        static void CollectUxmlReferences(XElement element, List<string> styleRefs, List<string> templateRefs)
        {
            if (element == null)
            {
                return;
            }

            AddReferenceAttributes(element, styleRefs, templateRefs);
            foreach (var child in element.Elements())
            {
                CollectUxmlReferences(child, styleRefs, templateRefs);
            }
        }

        static List<UssRuleInfo> ExtractUssRuleInfos(string content, int maxRules, string sourcePath, out bool truncated)
        {
            truncated = false;
            var rules = new List<UssRuleInfo>();
            var stripped = Regex.Replace(content, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match match in Regex.Matches(stripped, @"([^{}]+)\{([^{}]*)\}", RegexOptions.Singleline))
            {
                if (rules.Count >= maxRules)
                {
                    truncated = true;
                    break;
                }

                var selectorGroup = match.Groups[1].Value.Trim();
                if (selectorGroup.StartsWith("@", StringComparison.Ordinal))
                {
                    continue;
                }

                rules.Add(new UssRuleInfo(sourcePath, selectorGroup, match.Groups[2].Value.Trim()));
            }

            return rules;
        }

        static List<string> ExtractUssAssetReferences(string content)
        {
            var assetRefs = new List<string>();
            foreach (Match match in Regex.Matches(content, @"(?:url|resource)\(([^)]+)\)", RegexOptions.IgnoreCase))
            {
                var value = match.Groups[1].Value.Trim().Trim('\'', '"');
                if (!string.IsNullOrEmpty(value))
                {
                    assetRefs.Add(value);
                }
            }

            return assetRefs;
        }

        static List<ScreenSize> ExtractScreenSizes(string json)
        {
            var sizes = new List<ScreenSize>();
            var slice = ExtractArraySlice(json, "screen_sizes");
            foreach (Match match in Regex.Matches(slice, @"\[\s*(\d{2,5})\s*,\s*(\d{2,5})\s*\]"))
            {
                var width = int.Parse(match.Groups[1].Value);
                var height = int.Parse(match.Groups[2].Value);
                if (width > 0 && height > 0 && sizes.Count < 16)
                {
                    sizes.Add(new ScreenSize(width, height));
                }
            }

            if (sizes.Count == 0)
            {
                sizes.Add(new ScreenSize(1920, 1080));
                sizes.Add(new ScreenSize(1680, 1050));
                sizes.Add(new ScreenSize(2560, 1080));
                sizes.Add(new ScreenSize(1080, 1920));
            }

            return sizes;
        }

        static string ExtractArraySlice(string json, string property)
        {
            var start = FindArrayStart(json, property);
            if (start < 0)
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
                if (inString)
                {
                    continue;
                }
                if (character == '[')
                {
                    depth++;
                }
                else if (character == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        return json.Substring(start, index - start + 1);
                    }
                }
            }

            return string.Empty;
        }

        static string ScreenSizesJson(List<ScreenSize> sizes)
        {
            var values = new List<string>();
            foreach (var size in sizes)
            {
                values.Add("{\"width\":" + size.Width + ",\"height\":" + size.Height + "}");
            }
            return JsonArray(values);
        }

        static bool LooksLikeScrollName(string value)
        {
            return !string.IsNullOrEmpty(value) && value.IndexOf("scroll", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static bool TryParseDouble(string value, out double result)
        {
            return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out result);
        }

        static bool IsLargeFixedPixel(double pixels, List<ScreenSize> screenSizes)
        {
            var absolute = Math.Abs(pixels);
            if (absolute >= 512)
            {
                return true;
            }

            var shortest = 0;
            foreach (var size in screenSizes)
            {
                var candidate = Math.Min(size.Width, size.Height);
                if (shortest == 0 || candidate < shortest)
                {
                    shortest = candidate;
                }
            }

            return shortest > 0 && absolute >= shortest * 0.5;
        }

        static bool HasClassRuleWithFlexGrow(string className, List<UssRuleInfo> rules)
        {
            foreach (var rule in rules)
            {
                if (rule.Classes.Contains(className) && RuleHasNonZeroProperty(rule.Body, "flex-grow"))
                {
                    return true;
                }
            }

            return false;
        }

        static bool HasClassRuleWithBoundedSizeOrFlex(string className, List<UssRuleInfo> rules)
        {
            foreach (var rule in rules)
            {
                if (!rule.Classes.Contains(className))
                {
                    continue;
                }
                if (RuleHasProperty(rule.Body, "height") || RuleHasProperty(rule.Body, "max-height") || RuleHasProperty(rule.Body, "flex-grow"))
                {
                    return true;
                }
            }

            return false;
        }

        static bool RuleHasProperty(string body, string propertyName)
        {
            return Regex.IsMatch(body, @"(?i)(^|;)\s*" + Regex.Escape(propertyName) + @"\s*:");
        }

        static bool RuleHasNonZeroProperty(string body, string propertyName)
        {
            var match = Regex.Match(body, @"(?i)(^|;)\s*" + Regex.Escape(propertyName) + @"\s*:\s*(-?\d+(?:\.\d+)?)");
            return match.Success && TryParseDouble(match.Groups[2].Value, out var value) && Math.Abs(value) > 0.0001;
        }

        static string ReferenceKindFromAssetReference(string rawReference)
        {
            var assetPath = AssetPathFromReference(rawReference);
            var extension = Path.GetExtension(string.IsNullOrEmpty(assetPath) ? rawReference : assetPath).ToLowerInvariant();
            if (extension == ".png" || extension == ".jpg" || extension == ".jpeg" || extension == ".tga" || extension == ".psd" || extension == ".gif" || extension == ".bmp" || extension == ".exr" || extension == ".hdr" || extension == ".svg")
            {
                return "image";
            }
            if (extension == ".ttf" || extension == ".otf" || extension == ".fontsettings")
            {
                return "font";
            }

            return "asset";
        }

        static string ValidateDocumentTarget(string rawTarget, FindingList findings)
        {
            var state = new DocumentInspectState(200, true, true, true);
            var targetJson = InspectDocumentTarget(rawTarget, state);
            foreach (var documentJson in state.Documents)
            {
                if (documentJson.Contains("\"source_asset\":{\"assigned\":false}", StringComparison.Ordinal))
                {
                    findings.Add("error", "UITK001", "UIDocument has no VisualTreeAsset source asset", rawTarget, string.Empty);
                }
                if (documentJson.Contains("\"panel_settings\":{\"assigned\":false}", StringComparison.Ordinal))
                {
                    findings.Add("warning", "UITK002", "UIDocument has no PanelSettings assigned", rawTarget, string.Empty);
                }
            }

            return targetJson;
        }

        static string ValidateUxmlPath(string rawPath, FindingList findings, UxmlValidationInfo info)
        {
            var pathResult = ReadTextAsset(rawPath, ".uxml", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                findings.Add("error", "UITK003", "UXML could not be read or parsed as text", rawPath, string.Empty);
                return errorJson;
            }

            try
            {
                var document = XDocument.Parse(content, LoadOptions.None);
                var typeCounts = new Dictionary<string, int>();
                var names = new List<string>();
                var localNameCounts = new Dictionary<string, int>();
                var classes = new HashSet<string>();
                var styleRefs = new List<string>();
                var templateRefs = new List<string>();
                var tree = new List<string>();
                var elementIndex = 0;
                var truncated = false;
                TraverseUxml(document.Root, 0, 5000, false, true, true, typeCounts, names, localNameCounts, classes, styleRefs, templateRefs, tree, ref elementIndex, ref truncated);
                foreach (var name in names)
                {
                    info.Names.Add(name);
                    Increment(info.NameCounts, name);
                }

                foreach (var entry in localNameCounts)
                {
                    if (entry.Value > 1)
                    {
                        findings.Add("warning", "UITK009", "Duplicate UXML element name", path, entry.Key);
                    }
                }

                var missingStyleRefs = 0;
                foreach (var reference in styleRefs)
                {
                    var assetPath = AssetPathFromReference(reference);
                    if (!string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
                    {
                        missingStyleRefs++;
                        findings.Add("error", "UITK005", "UXML style reference does not resolve", path, assetPath);
                    }
                }

                var missingTemplateRefs = 0;
                foreach (var reference in templateRefs)
                {
                    var assetPath = AssetPathFromReference(reference);
                    if (!string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
                    {
                        missingTemplateRefs++;
                        findings.Add("error", "UITK005", "UXML template reference does not resolve", path, assetPath);
                    }
                }

                return "{\"path\":" + JsonUtil.String(path)
                    + ",\"status\":\"ok\""
                    + ",\"byte_length\":" + byteLength
                    + ",\"truncated\":" + JsonUtil.Bool(fileTruncated || truncated)
                    + ",\"elements_found\":" + elementIndex
                    + ",\"named_elements\":" + names.Count
                    + ",\"duplicate_names\":" + CountDuplicates(localNameCounts)
                    + ",\"style_references\":" + styleRefs.Count
                    + ",\"missing_style_references\":" + missingStyleRefs
                    + ",\"template_references\":" + templateRefs.Count
                    + ",\"missing_template_references\":" + missingTemplateRefs
                    + "}";
            }
            catch (Exception exception)
            {
                findings.Add("error", "UITK003", "UXML failed XML parsing: " + exception.Message, path, string.Empty);
                return "{\"path\":" + JsonUtil.String(path) + ",\"status\":\"invalid_xml\",\"message\":" + JsonUtil.String(exception.Message) + ",\"byte_length\":" + byteLength + ",\"truncated\":" + JsonUtil.Bool(fileTruncated) + "}";
            }
        }

        static string ValidateUssPath(string rawPath, FindingList findings)
        {
            var pathResult = ReadTextAsset(rawPath, ".uss", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                findings.Add("error", "UITK004", "USS could not be read as text", rawPath, string.Empty);
                return errorJson;
            }

            var selectors = new List<string>();
            var classes = new HashSet<string>();
            var assetRefs = new List<string>();
            ExtractUssRules(content, 5000, selectors, classes, assetRefs, out var rulesFound, out var selectorsFound, out var truncated);
            var missingAssetRefs = 0;
            foreach (var reference in assetRefs)
            {
                var assetPath = AssetPathFromReference(reference);
                if (!string.IsNullOrEmpty(assetPath) && string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)))
                {
                    missingAssetRefs++;
                    findings.Add("error", "UITK006", "USS asset reference does not resolve", path, assetPath);
                }
            }

            return "{\"path\":" + JsonUtil.String(path)
                + ",\"status\":\"ok\""
                + ",\"byte_length\":" + byteLength
                + ",\"truncated\":" + JsonUtil.Bool(fileTruncated || truncated)
                + ",\"rules_found\":" + rulesFound
                + ",\"selectors_found\":" + selectorsFound
                + ",\"asset_references\":" + assetRefs.Count
                + ",\"missing_asset_references\":" + missingAssetRefs
                + "}";
        }

        static void ValidateBinderPaths(List<string> binderPaths, UxmlValidationInfo uxmlInfo, FindingList findings, List<string> binderResults, HashSet<string> queryMethods)
        {
            if (uxmlInfo.Names.Count == 0)
            {
                findings.Add("warning", "UITK008", "No UXML named elements are available for binder query validation", string.Empty, string.Empty);
            }

            var queriedNames = new HashSet<string>();
            foreach (var rawPath in binderPaths)
            {
                binderResults.Add(ValidateBinderPath(rawPath, uxmlInfo, findings, queriedNames, queryMethods));
            }

            foreach (var name in uxmlInfo.Names)
            {
                if (!queriedNames.Contains(name))
                {
                    findings.Add("info", "UITK_BINDING_UNUSED_NAME", "UXML named element is not referenced by scanned binder queries", string.Empty, name);
                }
            }
        }

        static string ValidateBinderPath(string rawPath, UxmlValidationInfo uxmlInfo, FindingList findings, HashSet<string> queriedNames, HashSet<string> queryMethods)
        {
            var pathResult = ReadTextAsset(rawPath, ".cs", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                findings.Add("error", "UITK_BINDER_READ_FAILED", "Binder file could not be read as text", rawPath, string.Empty);
                return errorJson;
            }

            var queries = ExtractBinderQueries(content, queryMethods);
            var missing = 0;
            foreach (var query in queries)
            {
                queriedNames.Add(query);
                if (!uxmlInfo.Names.Contains(query))
                {
                    missing++;
                    findings.Add("error", "UITK008", "Binder root.Q/root.Query name was not found in UXML", path, query);
                }
            }

            return "{\"path\":" + JsonUtil.String(path)
                + ",\"status\":\"ok\""
                + ",\"byte_length\":" + byteLength
                + ",\"truncated\":" + JsonUtil.Bool(fileTruncated)
                + ",\"queries_found\":" + queries.Count
                + ",\"missing_queries\":" + missing
                + ",\"query_names\":" + JsonArrayStrings(DedupeSorted(queries))
                + "}";
        }

        static List<string> ExtractBinderQueries(string content, HashSet<string> queryMethods)
        {
            var queries = new List<string>();
            foreach (Match match in Regex.Matches(content, "\\.(Q|Query)\\s*(?:<[^>]+>)?\\s*\\(\\s*\"((?:\\\\.|[^\"\\\\])*)\""))
            {
                if (queryMethods.Contains(match.Groups[1].Value))
                {
                    queries.Add(UnescapeSimpleString(match.Groups[2].Value));
                }
            }
            return queries;
        }

        static HashSet<string> DefaultQueryMethods()
        {
            return new HashSet<string>(StringComparer.Ordinal) { "Q", "Query" };
        }

        static HashSet<string> QueryMethodsFromPatterns(List<string> queryPatterns, FindingList findings)
        {
            var methods = new HashSet<string>(StringComparer.Ordinal);
            foreach (var pattern in queryPatterns)
            {
                var value = string.IsNullOrEmpty(pattern) ? string.Empty : pattern.Trim();
                if (value.EndsWith(".Q", StringComparison.Ordinal) || value == "Q")
                {
                    methods.Add("Q");
                }
                else if (value.EndsWith(".Query", StringComparison.Ordinal) || value == "Query")
                {
                    methods.Add("Query");
                }
                else if (!string.IsNullOrEmpty(value))
                {
                    findings.Add("warning", "UITK_BINDING_PATTERN_UNSUPPORTED", "Unsupported binder query pattern; scanner supports Q and Query methods", string.Empty, value);
                }
            }

            if (methods.Count == 0)
            {
                return DefaultQueryMethods();
            }

            return methods;
        }

        static string UnescapeSimpleString(string value)
        {
            return value.Replace("\\\"", "\"").Replace("\\\\", "\\");
        }

        static string CompileStatusJson(FindingList findings)
        {
            var isCompiling = EditorApplication.isCompiling;
            var isUpdating = EditorApplication.isUpdating;
            var isPlaying = EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode;
            if (isCompiling)
            {
                findings.Add("warning", "UITK007", "Unity is compiling during validation", string.Empty, string.Empty);
            }
            if (isUpdating)
            {
                findings.Add("warning", "UITK007", "Unity is updating assets during validation", string.Empty, string.Empty);
            }
            return "{\"is_compiling\":" + JsonUtil.Bool(isCompiling)
                + ",\"is_updating\":" + JsonUtil.Bool(isUpdating)
                + ",\"is_playing\":" + JsonUtil.Bool(isPlaying)
                + ",\"ready\":" + JsonUtil.Bool(!isCompiling && !isUpdating && !isPlaying)
                + "}";
        }

        static string InspectDocumentTarget(string rawTarget, DocumentInspectState state)
        {
            string target;
            try
            {
                target = PathGuard.RequireUnityAssetPath(rawTarget);
            }
            catch (ArgumentException exception)
            {
                state.Warnings.Add("invalid target: " + rawTarget);
                return TargetStatusJson(rawTarget, "unknown", "invalid", exception.Message, 0);
            }

            if (target.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
            {
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(target);
                if (prefab == null)
                {
                    state.Warnings.Add("prefab not found: " + target);
                    return TargetStatusJson(target, "prefab_asset", "not_found", "prefab asset was not found", 0);
                }

                return InspectSceneDocuments(target, "prefab_asset", new[] { prefab }, state);
            }

            if (target.EndsWith(".unity", StringComparison.OrdinalIgnoreCase))
            {
                var scene = LoadedSceneByPath(target);
                if (scene.IsValid() && scene.isLoaded)
                {
                    return InspectSceneDocuments(target, "loaded_scene", scene.GetRootGameObjects(), state);
                }

                state.Warnings.Add("scene target is not loaded: " + target);
                return TargetStatusJson(target, "loaded_scene", "skipped", "scene target is not loaded; this read-only iteration does not open scenes", 0);
            }

            state.Warnings.Add("unsupported document target: " + target);
            return TargetStatusJson(target, "unknown", "skipped", "target type is not supported by uitk.document.inspect", 0);
        }

        static string InspectSceneDocuments(string targetPath, string targetKind, GameObject[] roots, DocumentInspectState state)
        {
            var before = state.DocumentsFound;
            foreach (var root in roots)
            {
                if (root == null || state.Truncated)
                {
                    continue;
                }

                var documents = root.GetComponentsInChildren<UIDocument>(state.IncludeInactive);
                foreach (var document in documents)
                {
                    if (state.Truncated)
                    {
                        break;
                    }

                    state.AddDocument(DocumentJson(document, targetPath, targetKind, state));
                }
            }

            return TargetStatusJson(targetPath, targetKind, "ok", "ok", state.DocumentsFound - before);
        }

        static string DocumentJson(UIDocument document, string targetPath, string targetKind, DocumentInspectState state)
        {
            var sourceAsset = document == null ? null : document.visualTreeAsset;
            var panelSettings = document == null ? null : document.panelSettings;
            var componentIndex = ComponentIndex(document);
            if (sourceAsset == null)
            {
                state.MissingSourceAssets++;
            }
            if (state.IncludePanelSettings && panelSettings == null)
            {
                state.MissingPanelSettings++;
            }

            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"document_id\":").Append(JsonUtil.String("document_" + (state.DocumentsFound + 1).ToString("0000")));
            builder.Append(",\"target_path\":").Append(JsonUtil.String(targetPath));
            builder.Append(",\"target_kind\":").Append(JsonUtil.String(targetKind));
            builder.Append(",\"object_path\":").Append(JsonUtil.String(document == null ? string.Empty : TransformPath(document.transform)));
            builder.Append(",\"gameobject_name\":").Append(JsonUtil.String(document == null ? string.Empty : document.gameObject.name));
            builder.Append(",\"component_index\":").Append(componentIndex);
            builder.Append(",\"active_self\":").Append(JsonUtil.Bool(document != null && document.gameObject.activeSelf));
            builder.Append(",\"active_in_hierarchy\":").Append(JsonUtil.Bool(document != null && document.gameObject.activeInHierarchy));
            builder.Append(",\"sorting_order\":").Append(JsonUtil.String(PropertyValue(document, "sortingOrder")));
            if (state.IncludeVisualTreeAsset)
            {
                builder.Append(",\"source_asset\":").Append(ObjectRefJson(sourceAsset));
            }
            if (state.IncludePanelSettings)
            {
                builder.Append(",\"panel_settings\":").Append(ObjectRefJson(panelSettings));
            }
            builder.Append(",\"runtime_interaction_eligibility\":").Append(RuntimeInteractionEligibilityJson(document, targetPath, targetKind, componentIndex));
            builder.Append(",\"interaction_command_guidance\":").Append(InteractionCommandGuidanceJson(document, targetPath, componentIndex));
            builder.Append('}');
            return builder.ToString();
        }

        static int ComponentIndex(UIDocument document)
        {
            if (document == null || document.gameObject == null)
            {
                return -1;
            }

            var components = document.gameObject.GetComponents<Component>();
            for (var index = 0; index < components.Length; index++)
            {
                if (components[index] == document)
                {
                    return index;
                }
            }

            return -1;
        }

        static string RuntimeInteractionEligibilityJson(UIDocument document, string targetPath, string targetKind, int componentIndex)
        {
            var rootAvailable = false;
            if (document != null)
            {
                try
                {
                    rootAvailable = document.rootVisualElement != null && document.rootVisualElement.panel != null;
                }
                catch (Exception)
                {
                    rootAvailable = false;
                }
            }

            var loadedScene = document != null
                && document.gameObject != null
                && document.gameObject.scene.IsValid()
                && document.gameObject.scene.isLoaded
                && !string.IsNullOrEmpty(document.gameObject.scene.path)
                && targetKind == "loaded_scene";
            var eligible = loadedScene && rootAvailable && componentIndex >= 0 && componentIndex <= 64;
            var currentlyEligible = eligible && EditorApplication.isPlaying;
            var reason = eligible
                ? currentlyEligible
                    ? "Loaded scene UIDocument root is currently eligible for an exact Play Mode UITK smoke request."
                    : "UIDocument identity is eligible, but uitk.interaction.smoke can execute only after Play Mode is active."
                : "This UIDocument is not an eligible loaded-scene runtime target; use the exact loaded scene path, object path, component index, and a root attached to a live panel.";
            return "{\"eligible\":" + JsonUtil.Bool(eligible)
                + ",\"currently_eligible\":" + JsonUtil.Bool(currentlyEligible)
                + ",\"command\":\"uitk.interaction.smoke\""
                + ",\"requires_play_mode\":true"
                + ",\"requires_loaded_scene\":true"
                + ",\"requires_exact_scene_path\":true"
                + ",\"requires_exact_document_object_path\":true"
                + ",\"requires_component_index\":true"
                + ",\"requires_non_null_root\":true"
                + ",\"requires_live_panel\":true"
                + ",\"dry_run_default\":true"
                + ",\"execution_is_ask_gated\":true"
                + ",\"executes_in_this_command\":false"
                + ",\"reason\":" + JsonUtil.String(Bounded(reason, 240)) + "}";
        }

        static string InteractionCommandGuidanceJson(UIDocument document, string targetPath, int componentIndex)
        {
            var objectPath = document == null ? string.Empty : TransformPath(document.transform);
            var scenePath = document == null || document.gameObject == null ? targetPath : document.gameObject.scene.path;
            var argsTemplate = "{\"scene_path\":" + JsonUtil.String(scenePath)
                + ",\"document_object_path\":" + JsonUtil.String(objectPath)
                + ",\"component_index\":" + componentIndex
                + ",\"steps\":[{\"control_name\":\"<exact VisualElement.name>\",\"action\":\"focus\"}],\"dry_run\":true,\"settle_frames\":0}";
            return "{\"command\":\"uitk.interaction.smoke\",\"manual_call_required\":true,\"normal_policy_required\":true,\"dry_run_first\":true,\"args_json\":"
                + JsonUtil.String(argsTemplate)
                + ",\"note\":\"Resolve exact named controls from the live UIDocument root; execution is Play Mode only, ask-gated, and does not save scenes or files.\"}";
        }

        static string InspectUxmlPath(string rawPath, bool includeTree, bool includeNames, bool includeClasses, int maxElements, List<string> warnings, out int elementsFound, out int duplicateNames, out bool truncated)
        {
            elementsFound = 0;
            duplicateNames = 0;
            truncated = false;
            var pathResult = ReadTextAsset(rawPath, ".uxml", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                warnings.Add("UXML read failed: " + rawPath);
                return errorJson;
            }

            try
            {
                var document = XDocument.Parse(content, LoadOptions.None);
                var root = document.Root;
                var typeCounts = new Dictionary<string, int>();
                var names = new List<string>();
                var classes = new HashSet<string>();
                var styleRefs = new List<string>();
                var templateRefs = new List<string>();
                var tree = new List<string>();
                var nameCounts = new Dictionary<string, int>();
                var elementIndex = 0;
                TraverseUxml(root, 0, maxElements, includeTree, includeNames, includeClasses, typeCounts, names, nameCounts, classes, styleRefs, templateRefs, tree, ref elementIndex, ref truncated);
                elementsFound = elementIndex;
                duplicateNames = CountDuplicates(nameCounts);
                truncated = truncated || fileTruncated;

                return "{\"path\":" + JsonUtil.String(path)
                    + ",\"status\":\"ok\""
                    + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(path))
                    + ",\"byte_length\":" + byteLength
                    + ",\"truncated\":" + JsonUtil.Bool(truncated)
                    + ",\"root_element\":" + JsonUtil.String(root == null ? string.Empty : root.Name.LocalName)
                    + ",\"elements_found\":" + elementsFound
                    + ",\"type_counts\":" + CountsJson(typeCounts)
                    + ",\"named_elements\":" + (includeNames ? JsonArrayStrings(DedupeSorted(names)) : "[]")
                    + ",\"duplicate_names\":" + DuplicateNamesJson(nameCounts)
                    + ",\"classes\":" + (includeClasses ? JsonArrayStrings(DedupeSorted(classes)) : "[]")
                    + ",\"style_references\":" + ReferencesJson(styleRefs)
                    + ",\"template_references\":" + ReferencesJson(templateRefs)
                    + ",\"tree\":" + (includeTree ? JsonArrayStrings(tree) : "[]")
                    + "}";
            }
            catch (Exception exception)
            {
                warnings.Add("invalid UXML: " + path);
                return "{\"path\":" + JsonUtil.String(path) + ",\"status\":\"invalid_xml\",\"message\":" + JsonUtil.String(exception.Message) + ",\"byte_length\":" + byteLength + ",\"truncated\":" + JsonUtil.Bool(fileTruncated) + "}";
            }
        }

        static string InspectUssPath(string rawPath, bool includeSelectors, bool includeAssetReferences, int maxRules, List<string> warnings, out int rulesFound, out int selectorsFound, out bool truncated)
        {
            rulesFound = 0;
            selectorsFound = 0;
            truncated = false;
            var pathResult = ReadTextAsset(rawPath, ".uss", MaxTextBytes, out var path, out var content, out var byteLength, out var fileTruncated, out var errorJson);
            if (!pathResult)
            {
                warnings.Add("USS read failed: " + rawPath);
                return errorJson;
            }

            var selectors = new List<string>();
            var classes = new HashSet<string>();
            var assetRefs = new List<string>();
            var rules = ExtractUssRules(content, maxRules, selectors, classes, assetRefs, out rulesFound, out selectorsFound, out truncated);
            truncated = truncated || fileTruncated;
            return "{\"path\":" + JsonUtil.String(path)
                + ",\"status\":\"ok\""
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(path))
                + ",\"byte_length\":" + byteLength
                + ",\"truncated\":" + JsonUtil.Bool(truncated)
                + ",\"rules_found\":" + rulesFound
                + ",\"selectors_found\":" + selectorsFound
                + ",\"selectors\":" + (includeSelectors ? JsonArrayStrings(DedupeSorted(selectors)) : "[]")
                + ",\"classes\":" + JsonArrayStrings(DedupeSorted(classes))
                + ",\"asset_references\":" + (includeAssetReferences ? ReferencesJson(assetRefs) : "[]")
                + ",\"rules\":" + (includeSelectors ? JsonArrayStrings(rules) : "[]")
                + "}";
        }

        static string InspectPanelSettingsPath(string rawPath, bool includeReferences, List<string> warnings, out bool found, out int referencesFound)
        {
            found = false;
            referencesFound = 0;
            string path;
            try
            {
                path = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (ArgumentException exception)
            {
                warnings.Add("invalid PanelSettings path: " + rawPath);
                return "{\"path\":" + JsonUtil.String(rawPath) + ",\"status\":\"invalid\",\"message\":" + JsonUtil.String(exception.Message) + "}";
            }

            var asset = AssetDatabase.LoadAssetAtPath<PanelSettings>(path);
            if (asset == null)
            {
                warnings.Add("PanelSettings not found: " + path);
                return "{\"path\":" + JsonUtil.String(path) + ",\"status\":\"not_found\"}";
            }

            found = true;
            var properties = SerializedPropertiesJson(asset, includeReferences, out referencesFound, out var references);
            return "{\"path\":" + JsonUtil.String(path)
                + ",\"status\":\"ok\""
                + ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(path))
                + ",\"name\":" + JsonUtil.String(asset.name)
                + ",\"properties\":" + properties
                + ",\"references\":" + (includeReferences ? JsonArray(references) : "[]")
                + "}";
        }

        static void TraverseUxml(XElement element, int depth, int maxElements, bool includeTree, bool includeNames, bool includeClasses, Dictionary<string, int> typeCounts, List<string> names, Dictionary<string, int> nameCounts, HashSet<string> classes, List<string> styleRefs, List<string> templateRefs, List<string> tree, ref int elementIndex, ref bool truncated)
        {
            if (element == null || truncated)
            {
                return;
            }
            if (elementIndex >= maxElements)
            {
                truncated = true;
                return;
            }

            elementIndex++;
            var typeName = element.Name.LocalName;
            Increment(typeCounts, typeName);
            if (includeTree)
            {
                tree.Add(new string(' ', Math.Min(depth, 32) * 2) + typeName);
            }

            var name = AttributeValue(element, "name");
            if (includeNames && !string.IsNullOrEmpty(name))
            {
                names.Add(name);
                Increment(nameCounts, name);
            }

            var classValue = AttributeValue(element, "class");
            if (includeClasses && !string.IsNullOrEmpty(classValue))
            {
                foreach (var className in SplitWhitespace(classValue))
                {
                    classes.Add(className);
                }
            }

            AddReferenceAttributes(element, styleRefs, templateRefs);
            foreach (var child in element.Elements())
            {
                TraverseUxml(child, depth + 1, maxElements, includeTree, includeNames, includeClasses, typeCounts, names, nameCounts, classes, styleRefs, templateRefs, tree, ref elementIndex, ref truncated);
            }
        }

        static void AddReferenceAttributes(XElement element, List<string> styleRefs, List<string> templateRefs)
        {
            foreach (var attribute in element.Attributes())
            {
                var name = attribute.Name.LocalName.ToLowerInvariant();
                if (name == "stylesheet" || name == "stylesheets" || (element.Name.LocalName == "Style" && name == "src"))
                {
                    styleRefs.Add(attribute.Value);
                }
                else if (name == "template" || (element.Name.LocalName == "Template" && name == "src"))
                {
                    templateRefs.Add(attribute.Value);
                }
            }
        }

        static List<string> ExtractUssRules(string content, int maxRules, List<string> selectors, HashSet<string> classes, List<string> assetRefs, out int rulesFound, out int selectorsFound, out bool truncated)
        {
            rulesFound = 0;
            selectorsFound = 0;
            truncated = false;
            var rules = new List<string>();
            var stripped = Regex.Replace(content, @"/\*.*?\*/", string.Empty, RegexOptions.Singleline);
            foreach (Match match in Regex.Matches(stripped, @"([^{}]+)\{[^{}]*\}", RegexOptions.Singleline))
            {
                if (rulesFound >= maxRules)
                {
                    truncated = true;
                    break;
                }

                var selectorGroup = match.Groups[1].Value.Trim();
                if (selectorGroup.StartsWith("@", StringComparison.Ordinal))
                {
                    continue;
                }

                rulesFound++;
                rules.Add(selectorGroup);
                foreach (var selector in selectorGroup.Split(','))
                {
                    var trimmed = selector.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }
                    selectors.Add(trimmed);
                    selectorsFound++;
                    foreach (Match classMatch in Regex.Matches(trimmed, @"\.([A-Za-z_][A-Za-z0-9_-]*)"))
                    {
                        classes.Add(classMatch.Groups[1].Value);
                    }
                }
            }

            foreach (Match match in Regex.Matches(content, @"(?:url|resource)\(([^)]+)\)", RegexOptions.IgnoreCase))
            {
                var value = match.Groups[1].Value.Trim().Trim('\'', '"');
                if (!string.IsNullOrEmpty(value))
                {
                    assetRefs.Add(value);
                }
            }

            return rules;
        }

        static string SerializedPropertiesJson(UnityEngine.Object asset, bool includeReferences, out int referencesFound, out List<string> references)
        {
            referencesFound = 0;
            references = new List<string>();
            var properties = new List<string>();
            var serialized = new SerializedObject(asset);
            var property = serialized.GetIterator();
            var includeChildren = true;
            while (property.NextVisible(includeChildren) && properties.Count < MaxSerializedProperties)
            {
                includeChildren = false;
                if (property.propertyPath == "m_Script")
                {
                    continue;
                }

                if (property.propertyType == SerializedPropertyType.ObjectReference)
                {
                    if (includeReferences && property.objectReferenceValue != null)
                    {
                        referencesFound++;
                        references.Add(ObjectReferencePropertyJson(property));
                    }
                    continue;
                }

                var value = SerializedPropertyValue(property);
                if (value.Length > 0)
                {
                    properties.Add("{\"path\":" + JsonUtil.String(property.propertyPath) + ",\"type\":" + JsonUtil.String(property.propertyType.ToString()) + ",\"value\":" + JsonUtil.String(value) + "}");
                }
            }

            return JsonArray(properties);
        }

        static string SerializedPropertyValue(SerializedProperty property)
        {
            switch (property.propertyType)
            {
                case SerializedPropertyType.Boolean:
                    return JsonUtil.Bool(property.boolValue);
                case SerializedPropertyType.Integer:
                    return property.intValue.ToString();
                case SerializedPropertyType.Float:
                    return property.floatValue.ToString(System.Globalization.CultureInfo.InvariantCulture);
                case SerializedPropertyType.String:
                    return property.stringValue ?? string.Empty;
                case SerializedPropertyType.Enum:
                    return property.enumDisplayNames != null && property.enumValueIndex >= 0 && property.enumValueIndex < property.enumDisplayNames.Length ? property.enumDisplayNames[property.enumValueIndex] : property.enumValueIndex.ToString();
                case SerializedPropertyType.Color:
                    return property.colorValue.ToString();
                case SerializedPropertyType.Vector2:
                    return property.vector2Value.ToString();
                case SerializedPropertyType.Vector3:
                    return property.vector3Value.ToString();
                default:
                    return string.Empty;
            }
        }

        static string ObjectReferencePropertyJson(SerializedProperty property)
        {
            return "{\"property_path\":" + JsonUtil.String(property.propertyPath)
                + ",\"reference\":" + ObjectRefJson(property.objectReferenceValue)
                + "}";
        }

        static bool ReadTextAsset(string rawPath, string requiredExtension, int maxBytes, out string assetPath, out string content, out long byteLength, out bool truncated, out string errorJson)
        {
            content = string.Empty;
            byteLength = 0;
            truncated = false;
            errorJson = string.Empty;
            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (ArgumentException exception)
            {
                assetPath = rawPath;
                errorJson = "{\"path\":" + JsonUtil.String(rawPath) + ",\"status\":\"invalid\",\"message\":" + JsonUtil.String(exception.Message) + "}";
                return false;
            }

            if (!assetPath.EndsWith(requiredExtension, StringComparison.OrdinalIgnoreCase))
            {
                errorJson = "{\"path\":" + JsonUtil.String(assetPath) + ",\"status\":\"invalid\",\"message\":" + JsonUtil.String("path must end with " + requiredExtension) + "}";
                return false;
            }

            if (!TryResolveAssetFilePath(assetPath, out var fullPath))
            {
                errorJson = "{\"path\":" + JsonUtil.String(assetPath) + ",\"status\":\"not_found\"}";
                return false;
            }

            var bytes = ReadBoundedBytes(fullPath, maxBytes, out byteLength, out truncated);
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] == 0)
                {
                    errorJson = "{\"path\":" + JsonUtil.String(assetPath) + ",\"status\":\"invalid_text\",\"message\":\"asset content looks binary\"}";
                    return false;
                }
            }

            try
            {
                content = new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                errorJson = "{\"path\":" + JsonUtil.String(assetPath) + ",\"status\":\"invalid_text\",\"message\":\"asset content is not valid UTF-8\"}";
                return false;
            }
        }

        static bool TryResolveAssetFilePath(string assetPath, out string fullPath)
        {
            fullPath = string.Empty;
            if (assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
                return IsSameOrChildPath(fullPath, ProjectPaths.ProjectRoot) && File.Exists(fullPath);
            }

            if (!assetPath.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return false;
            }

            var relative = assetPath.Substring("Packages/".Length);
            var slash = relative.IndexOf('/');
            var packageName = slash < 0 ? relative : relative.Substring(0, slash);
            var packageRelative = slash < 0 ? string.Empty : relative.Substring(slash + 1).Replace('/', Path.DirectorySeparatorChar);
            foreach (var package in UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages())
            {
                if (!string.Equals(package.name, packageName, StringComparison.Ordinal))
                {
                    continue;
                }

                var packageRoot = Path.GetFullPath(package.resolvedPath);
                var candidate = Path.GetFullPath(Path.Combine(packageRoot, packageRelative));
                if (IsSameOrChildPath(candidate, packageRoot) && File.Exists(candidate))
                {
                    fullPath = candidate;
                    return true;
                }
            }

            var fallback = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, assetPath.Replace('/', Path.DirectorySeparatorChar)));
            if (IsSameOrChildPath(fallback, ProjectPaths.ProjectRoot) && File.Exists(fallback))
            {
                fullPath = fallback;
                return true;
            }

            return false;
        }

        static byte[] ReadBoundedBytes(string fullPath, int maxBytes, out long byteLength, out bool truncated)
        {
            var info = new FileInfo(fullPath);
            byteLength = info.Length;
            truncated = byteLength > maxBytes;
            var readLength = (int)Math.Min(byteLength, maxBytes);
            var bytes = new byte[readLength];
            var offset = 0;
            using (var stream = File.OpenRead(fullPath))
            {
                while (offset < readLength)
                {
                    var read = stream.Read(bytes, offset, readLength - offset);
                    if (read <= 0)
                    {
                        break;
                    }

                    offset += read;
                }
            }

            if (offset != bytes.Length)
            {
                Array.Resize(ref bytes, offset);
            }

            return bytes;
        }

        static bool IsSameOrChildPath(string candidatePath, string rootPath)
        {
            var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(candidate, root, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        static Scene LoadedSceneByPath(string scenePath)
        {
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (scene.IsValid() && scene.isLoaded && string.Equals(scene.path, scenePath, StringComparison.OrdinalIgnoreCase))
                {
                    return scene;
                }
            }
            return default(Scene);
        }

        static string AttributeValue(XElement element, string localName)
        {
            foreach (var attribute in element.Attributes())
            {
                if (string.Equals(attribute.Name.LocalName, localName, StringComparison.OrdinalIgnoreCase))
                {
                    return attribute.Value;
                }
            }

            return string.Empty;
        }

        static string ReferencesJson(List<string> references)
        {
            var values = new List<string>();
            foreach (var reference in references)
            {
                values.Add(ReferenceJson(reference));
            }
            return JsonArray(values);
        }

        static string ReferenceJson(string rawReference)
        {
            var assetPath = AssetPathFromReference(rawReference);
            var guid = string.IsNullOrEmpty(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
            return "{\"raw\":" + JsonUtil.String(rawReference)
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"resolved\":" + JsonUtil.Bool(!string.IsNullOrEmpty(guid))
                + "}";
        }

        static string AssetPathFromReference(string rawReference)
        {
            if (string.IsNullOrEmpty(rawReference))
            {
                return string.Empty;
            }

            var value = rawReference.Trim().Trim('\'', '"');
            const string projectPrefix = "project://database/";
            if (value.StartsWith(projectPrefix, StringComparison.OrdinalIgnoreCase))
            {
                value = value.Substring(projectPrefix.Length);
            }

            var queryIndex = value.IndexOf('?');
            if (queryIndex >= 0)
            {
                value = value.Substring(0, queryIndex);
            }

            value = Uri.UnescapeDataString(value).Replace('\\', '/');
            if (value.StartsWith("Assets/", StringComparison.Ordinal) || value.StartsWith("Packages/", StringComparison.Ordinal))
            {
                return value;
            }

            return string.Empty;
        }

        static string ObjectRefJson(UnityEngine.Object value)
        {
            if (value == null)
            {
                return "{\"assigned\":false}";
            }

            var path = AssetDatabase.GetAssetPath(value);
            var guid = string.IsNullOrEmpty(path) ? string.Empty : AssetDatabase.AssetPathToGUID(path);
            return "{\"assigned\":true"
                + ",\"name\":" + JsonUtil.String(value.name)
                + ",\"type\":" + JsonUtil.String(value.GetType().FullName)
                + ",\"asset_path\":" + JsonUtil.String(path)
                + ",\"guid\":" + JsonUtil.String(guid)
                + ",\"resolved\":" + JsonUtil.Bool(!string.IsNullOrEmpty(guid))
                + "}";
        }

        static string TargetStatusJson(string target, string targetKind, string status, string message, int documentsFound)
        {
            return "{\"target\":" + JsonUtil.String(target)
                + ",\"target_kind\":" + JsonUtil.String(targetKind)
                + ",\"status\":" + JsonUtil.String(status)
                + ",\"message\":" + JsonUtil.String(message)
                + ",\"documents_found\":" + documentsFound
                + "}";
        }

        static string PropertyValue(object instance, string propertyName)
        {
            if (instance == null)
            {
                return string.Empty;
            }

            var property = instance.GetType().GetProperty(propertyName);
            if (property == null)
            {
                return string.Empty;
            }

            var value = property.GetValue(instance, null);
            return value == null ? string.Empty : value.ToString();
        }

        static string TransformPath(Transform transform)
        {
            if (transform == null)
            {
                return string.Empty;
            }

            var segments = new List<string>();
            var current = transform;
            while (current != null)
            {
                segments.Add(current.name);
                current = current.parent;
            }
            segments.Reverse();
            return "/" + string.Join("/", segments.ToArray());
        }

        static CommandExecutionResult DescriptorOnly(string command, string featureFlag, string[] nextValidActions)
        {
            return CommandExecutionResult.Ok("{\"status\":\"not_implemented\""
                + ",\"command\":" + JsonUtil.String(command)
                + ",\"read_only\":true"
                + ",\"feature_flag\":" + JsonUtil.String(featureFlag)
                + ",\"reason\":\"implementation follows in the next UITK iteration\""
                + ",\"resource_refs\":[]"
                + ",\"artifact_refs\":[]"
                + ",\"warnings\":[\"descriptor_only\"]"
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":" + JsonArrayStrings(nextValidActions)
                + "}");
        }

        static string ReportJson(UitkReportSnapshot snapshot, string format)
        {
            if (format != "markdown" && format != "json" && format != "markdown_and_json")
            {
                format = "markdown";
            }

            var markdownUri = ReportMarkdownResourceUri(snapshot.ReportId);
            var jsonUri = ReportJsonResourceUri(snapshot.ReportId);
            StoreResource(markdownUri, snapshot.Markdown, "text/markdown");
            StoreResource(jsonUri, snapshot.Json, "application/json");
            var json = "{\"status\":\"ok\""
                + ",\"report_id\":" + JsonUtil.String(snapshot.ReportId)
                + ",\"source_id\":" + JsonUtil.String(snapshot.SourceId)
                + ",\"source_command\":" + JsonUtil.String(snapshot.SourceCommand)
                + ",\"format\":" + JsonUtil.String(format)
                + ",\"summary\":" + JsonUtil.String(snapshot.Summary)
                + ",\"resource_refs\":" + ResourceRefsJson(new string[] { markdownUri, jsonUri })
                + ",\"artifact_refs\":[]";
            if (format == "markdown" || format == "markdown_and_json")
            {
                json += ",\"markdown\":" + JsonUtil.String(snapshot.Markdown);
            }
            if (format == "json" || format == "markdown_and_json")
            {
                json += ",\"json\":" + snapshot.Json;
            }
            json += ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.document.inspect\",\"uitk.uxml.inspect\",\"uitk.uss.inspect\",\"uitk.panelsettings.inspect\",\"uitk.layout.analyze\",\"uitk.references.report\"]}";
            return json;
        }

        static bool IsMigrationPlanStrategy(string strategy)
        {
            return strategy == "absolute_fidelity"
                || strategy == "semantic_flex_with_absolute_fallback"
                || strategy == "report_only";
        }

        static List<string> MigrationPlanArtifacts(string outputRoot, string targetName)
        {
            return new List<string>
            {
                outputRoot + "/" + targetName + ".uxml",
                outputRoot + "/" + targetName + ".uss",
                outputRoot + "/" + targetName + "Binder.cs",
                outputRoot + "/" + targetName + "PanelSettings.asset",
                outputRoot + "/" + targetName + "UIDocument.prefab",
                outputRoot + "/" + targetName + ".migration-map.json"
            };
        }

        static List<string> MigrationPlanManualTasks(string strategy)
        {
            var tasks = new List<string>
            {
                "Review the source uGUI scan and report before creating side-by-side UI Toolkit files.",
                "Map UnityEvent callbacks, custom MonoBehaviours, animation/tween usage, and dynamic runtime UI manually before binder generation.",
                "Run uitk.validate and uitk.bindings.validate against generated or edited files before any preview/apply workflow."
            };
            if (strategy == "semantic_flex_with_absolute_fallback")
            {
                tasks.Add("Review every RectTransform/LayoutGroup conversion where flex layout cannot preserve exact pixel fidelity.");
            }
            if (strategy == "report_only")
            {
                tasks.Add("Do not generate assets from this plan; use it only as a migration review/report artifact.");
            }
            return tasks;
        }

        static string MigrationPlanJson(string planId, string scanId, string strategy, string outputRoot, string targetName, string sourceReportJson, List<string> artifacts, List<string> manualTasks, string[] resourceUris, bool persisted, string artifactPath)
        {
            return "{\"status\":\"ok\""
                + ",\"plan_id\":" + JsonUtil.String(planId)
                + ",\"migration_mode\":\"ugui_to_uitk\""
                + ",\"scan_id\":" + JsonUtil.String(scanId)
                + ",\"source_command\":\"ugui.canvas.report\""
                + ",\"strategy\":" + JsonUtil.String(strategy)
                + ",\"output_root\":" + JsonUtil.String(outputRoot)
                + ",\"target_name\":" + JsonUtil.String(targetName)
                + ",\"persisted\":" + JsonUtil.Bool(persisted)
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"artifacts_to_create\":" + JsonArrayStrings(artifacts)
                + ",\"manual_tasks\":" + JsonArrayStrings(manualTasks)
                + ",\"unsupported_nodes\":[]"
                + ",\"node_mappings\":[]"
                + ",\"confidence\":\"low\""
                + ",\"confidence_reason\":\"MVP plan uses the bounded uGUI report summary; node-level mappings require a later migration analyzer.\""
                + ",\"source_report\":" + sourceReportJson
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUris)
                + ",\"artifact_refs\":" + (persisted ? JsonArrayStrings(new string[] { artifactPath }) : "[]")
                + ",\"readable_with\":" + (persisted ? JsonUtil.String("job.artifact.read") : "null")
                + ",\"next_valid_actions\":[\"uitk.resource.read\",\"uitk.document.create\",\"uitk.template.generate\",\"uitk.binder.generate\",\"uitk.validate\",\"uitk.bindings.validate\"]}";
        }

        static string MigrationPlanMarkdown(string planId, string scanId, string strategy, string outputRoot, string targetName, List<string> artifacts, List<string> manualTasks)
        {
            var builder = new StringBuilder();
            builder.Append("# Canvas/uGUI To UI Toolkit Migration Plan").Append('\n').Append('\n');
            builder.Append("- plan_id: ").Append(planId).Append('\n');
            builder.Append("- scan_id: ").Append(scanId).Append('\n');
            builder.Append("- strategy: ").Append(strategy).Append('\n');
            builder.Append("- output_root: ").Append(outputRoot).Append('\n');
            builder.Append("- target_name: ").Append(targetName).Append('\n');
            builder.Append('\n').Append("## Artifacts To Create").Append('\n');
            foreach (var artifact in artifacts)
            {
                builder.Append("- ").Append(artifact).Append('\n');
            }
            builder.Append('\n').Append("## Manual Tasks").Append('\n');
            foreach (var task in manualTasks)
            {
                builder.Append("- ").Append(task).Append('\n');
            }
            builder.Append('\n').Append("## Safety").Append('\n');
            builder.Append("- This plan does not generate assets, replace scenes or prefabs, run previews, apply migration, or create rollback state.").Append('\n');
            builder.Append("- Use dry-run safe-write commands and validation before any future apply workflow.").Append('\n');
            return builder.ToString();
        }

        static void WriteMigrationPlanArtifact(string artifactPath, string content)
        {
            var projectRoot = ProjectPaths.ProjectRoot;
            var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, MigrationPlanArtifactRoot.Replace('/', Path.DirectorySeparatorChar)));
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, artifactRoot))
            {
                throw new InvalidOperationException("artifact path resolves outside UITK migration plan artifact root");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

        static CommandExecutionResult LoadMigrationValidationBundle(string generationId, string migrationMapPath, out MigrationValidationBundle bundle)
        {
            bundle = null;
            if (!string.IsNullOrEmpty(generationId) && !string.IsNullOrEmpty(migrationMapPath))
            {
                return CommandExecutionResult.Error("schema_validation_failed", "Use either generation_id or migration_map_path, not both");
            }

            if (!string.IsNullOrEmpty(generationId))
            {
                var resource = FindResource(GenerationResourceUri(generationId));
                if (resource == null)
                {
                    return CommandExecutionResult.Ok("{\"status\":\"not_found\""
                        + ",\"generation_id\":" + JsonUtil.String(generationId)
                        + ",\"reason\":\"generation_id was not found in this Unity Bridge process resource cache\""
                        + ",\"cache\":{\"storage\":\"memory\",\"max_resources\":" + MaxCachedResources + "}"
                        + ",\"resource_refs\":[]"
                        + ",\"artifact_refs\":[]"
                        + ",\"next_valid_actions\":[\"uitk.generate\",\"uitk.validate\"]}");
                }

                var generationJson = resource.Content;
                if (JsonUtil.ExtractBool(generationJson, "dry_run", true))
                {
                    return CommandExecutionResult.Ok("{\"status\":\"blocked\""
                        + ",\"generation_id\":" + JsonUtil.String(generationId)
                        + ",\"reason\":\"generation_id refers to a dry-run result; run uitk.generate with dry_run:false before validating generated assets\""
                        + ",\"resource_refs\":" + ResourceRefsJson(resource.Uri)
                        + ",\"artifact_refs\":[]"
                        + ",\"next_valid_actions\":[\"uitk.generate\"]}");
                }

                return BuildMigrationValidationBundle(
                    "generation_resource",
                    resource.Uri,
                    JsonUtil.ExtractString(generationJson, "plan_id"),
                    generationId,
                    JsonUtil.ExtractString(generationJson, "scan_id"),
                    JsonUtil.ExtractString(generationJson, "strategy"),
                    JsonUtil.ExtractString(generationJson, "output_root"),
                    JsonUtil.ExtractString(generationJson, "target_name"),
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    string.Empty,
                    JsonUtil.ExtractBool(generationJson, "legacy_modified", false),
                    out bundle);
            }

            if (!ReadTextAsset(migrationMapPath, ".json", MaxTextBytes, out var mapPath, out var mapJson, out _, out _, out var errorJson))
            {
                return CommandExecutionResult.Error("invalid_field_value", "migration_map_path could not be read: " + errorJson);
            }
            if (!mapPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "migration_map_path must be under Assets/");
            }

            var artifactsJson = JsonUtil.ExtractObject(mapJson, "artifacts");
            return BuildMigrationValidationBundle(
                "migration_map_path",
                string.Empty,
                JsonUtil.ExtractString(mapJson, "plan_id"),
                JsonUtil.ExtractString(mapJson, "generation_id"),
                JsonUtil.ExtractString(mapJson, "scan_id"),
                JsonUtil.ExtractString(mapJson, "strategy"),
                JsonUtil.ExtractString(mapJson, "output_root"),
                JsonUtil.ExtractString(mapJson, "target_name"),
                mapPath,
                JsonUtil.ExtractString(artifactsJson, "uxml"),
                JsonUtil.ExtractString(artifactsJson, "uss"),
                JsonUtil.ExtractString(artifactsJson, "binder"),
                JsonUtil.ExtractString(artifactsJson, "panel_settings"),
                JsonUtil.ExtractString(artifactsJson, "prefab"),
                JsonUtil.ExtractBool(mapJson, "legacy_modified", false),
                out bundle);
        }

        static CommandExecutionResult BuildMigrationValidationBundle(string source, string sourceResourceUri, string planId, string generationId, string scanId, string strategy, string outputRoot, string targetName, string migrationMapPath, string uxmlPath, string ussPath, string binderPath, string panelSettingsPath, string prefabPath, bool legacyModified, out MigrationValidationBundle bundle)
        {
            bundle = null;
            if (string.IsNullOrEmpty(generationId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Migration bundle is missing generation_id");
            }
            if (string.IsNullOrEmpty(outputRoot))
            {
                outputRoot = string.IsNullOrEmpty(migrationMapPath) ? string.Empty : ParentAssetPath(migrationMapPath);
            }
            if (string.IsNullOrEmpty(outputRoot) || string.IsNullOrEmpty(targetName) || !IsCSharpIdentifier(targetName) || IsCSharpKeyword(targetName))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Migration bundle is missing a valid output_root or target_name");
            }

            try
            {
                outputRoot = PathGuard.RequireUnityAssetPath(outputRoot).TrimEnd('/');
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", "Migration bundle output_root is invalid: " + exception.Message);
            }
            if (!outputRoot.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", "Migration bundle output_root must be under Assets/");
            }

            if (string.IsNullOrEmpty(migrationMapPath))
            {
                migrationMapPath = outputRoot + "/" + targetName + ".migration-map.json";
            }
            if (string.IsNullOrEmpty(uxmlPath))
            {
                uxmlPath = outputRoot + "/" + targetName + ".uxml";
            }
            if (string.IsNullOrEmpty(ussPath))
            {
                ussPath = outputRoot + "/" + targetName + ".uss";
            }
            if (string.IsNullOrEmpty(binderPath))
            {
                binderPath = outputRoot + "/" + targetName + "Binder.cs";
            }
            if (string.IsNullOrEmpty(panelSettingsPath))
            {
                panelSettingsPath = outputRoot + "/" + targetName + "PanelSettings.asset";
            }
            if (string.IsNullOrEmpty(prefabPath))
            {
                prefabPath = outputRoot + "/" + targetName + "UIDocument.prefab";
            }

            var pathResult = NormalizeMigrationBundlePath(outputRoot, ".migration-map.json", "migration_map_path", migrationMapPath, out migrationMapPath)
                ?? NormalizeMigrationBundlePath(outputRoot, ".uxml", "uxml", uxmlPath, out uxmlPath)
                ?? NormalizeMigrationBundlePath(outputRoot, ".uss", "uss", ussPath, out ussPath)
                ?? NormalizeMigrationBundlePath(outputRoot, ".cs", "binder", binderPath, out binderPath)
                ?? NormalizeMigrationBundlePath(outputRoot, ".asset", "panel_settings", panelSettingsPath, out panelSettingsPath)
                ?? NormalizeMigrationBundlePath(outputRoot, ".prefab", "prefab", prefabPath, out prefabPath);
            if (pathResult != null)
            {
                return pathResult;
            }

            bundle = new MigrationValidationBundle(
                source,
                sourceResourceUri,
                planId,
                generationId,
                scanId,
                strategy,
                outputRoot,
                targetName,
                migrationMapPath,
                uxmlPath,
                ussPath,
                binderPath,
                panelSettingsPath,
                prefabPath,
                legacyModified);
            return null;
        }

        static CommandExecutionResult NormalizeMigrationBundlePath(string outputRoot, string requiredSuffix, string fieldName, string rawPath, out string assetPath)
        {
            assetPath = string.Empty;
            try
            {
                assetPath = PathGuard.RequireUnityAssetPath(rawPath);
            }
            catch (ArgumentException exception)
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " path is invalid: " + exception.Message);
            }

            if (!assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " path must be under Assets/");
            }
            if (!IsSameOrChildAssetPath(assetPath, outputRoot))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " path must stay under the migration output_root");
            }
            if (!assetPath.EndsWith(requiredSuffix, StringComparison.OrdinalIgnoreCase))
            {
                return CommandExecutionResult.Error("invalid_field_value", fieldName + " path must end with " + requiredSuffix);
            }

            return null;
        }

        static string ValidateMigrationBundle(MigrationValidationBundle bundle, FindingList findings, List<string> warnings)
        {
            var missing = 0;
            var artifacts = new List<string>
            {
                ValidateMigrationTextArtifact("migration_map", bundle.MigrationMapPath, findings, ref missing),
                ValidateMigrationTextArtifact("uxml", bundle.UxmlPath, findings, ref missing),
                ValidateMigrationTextArtifact("uss", bundle.UssPath, findings, ref missing),
                ValidateMigrationTextArtifact("binder", bundle.BinderPath, findings, ref missing),
                ValidateMigrationObjectArtifact("panel_settings", bundle.PanelSettingsPath, typeof(PanelSettings), findings, ref missing),
                ValidateMigrationObjectArtifact("prefab", bundle.PrefabPath, typeof(GameObject), findings, ref missing)
            };

            if (bundle.LegacyModified)
            {
                findings.Add("error", "UITK_MIGRATION_LEGACY_MODIFIED", "Migration validation bundle reports legacy Canvas mutation; create-only validation requires legacy_modified:false", bundle.MigrationMapPath, bundle.GenerationId);
            }
            if (bundle.Source == "generation_resource" && string.IsNullOrEmpty(bundle.SourceResourceUri))
            {
                warnings.Add("generation resource uri is unavailable; validate again with migration_map_path after domain reload");
            }

            return "{\"status\":\"ok\""
                + ",\"source\":" + JsonUtil.String(bundle.Source)
                + ",\"generation_id\":" + JsonUtil.String(bundle.GenerationId)
                + ",\"output_root\":" + JsonUtil.String(bundle.OutputRoot)
                + ",\"artifacts_checked\":6"
                + ",\"missing_artifacts\":" + missing
                + ",\"legacy_modified\":" + JsonUtil.Bool(bundle.LegacyModified)
                + ",\"artifacts\":" + JsonArray(artifacts)
                + "}";
        }

        static string ValidateMigrationTextArtifact(string kind, string assetPath, FindingList findings, ref int missing)
        {
            var exists = TryResolveAssetFilePath(assetPath, out _);
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!exists)
            {
                missing++;
                findings.Add("error", "UITK_MIGRATION_ARTIFACT_MISSING", "Migration text artifact is missing", assetPath, kind);
            }
            else if (string.IsNullOrEmpty(guid))
            {
                findings.Add("warning", "UITK_MIGRATION_ARTIFACT_NOT_IMPORTED", "Migration text artifact exists on disk but has no AssetDatabase GUID yet", assetPath, kind);
            }

            return "{\"kind\":" + JsonUtil.String(kind)
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"exists\":" + JsonUtil.Bool(exists)
                + ",\"guid\":" + JsonUtil.String(guid)
                + "}";
        }

        static string ValidateMigrationObjectArtifact(string kind, string assetPath, Type expectedType, FindingList findings, ref int missing)
        {
            var asset = AssetDatabase.LoadAssetAtPath(assetPath, expectedType);
            var exists = asset != null;
            var guid = AssetDatabase.AssetPathToGUID(assetPath);
            if (!exists)
            {
                missing++;
                findings.Add("error", "UITK_MIGRATION_ARTIFACT_MISSING", "Migration Unity object artifact is missing or has an unexpected type", assetPath, kind);
            }

            return "{\"kind\":" + JsonUtil.String(kind)
                + ",\"asset_path\":" + JsonUtil.String(assetPath)
                + ",\"expected_type\":" + JsonUtil.String(expectedType.FullName)
                + ",\"exists\":" + JsonUtil.Bool(exists)
                + ",\"guid\":" + JsonUtil.String(guid)
                + "}";
        }

        static string[] MigrationValidationNextActions(MigrationValidationBundle bundle, bool passed)
        {
            if (bundle == null)
            {
                return new string[] { "uitk.resource.read", "uitk.report" };
            }
            if (passed)
            {
                return new string[] { "uitk.resource.read", "uitk.report", "uitk.bindings.validate", "uitk.preview" };
            }

            return new string[] { "uitk.resource.read", "uitk.report", "uitk.generate" };
        }

        static PreviewScreenshotRef BuildPreviewScreenshotRef(string role, string rawPath)
        {
            if (string.IsNullOrEmpty(rawPath))
            {
                return new PreviewScreenshotRef(role, false, false, string.Empty, string.Empty, 0, 0, 0, "missing", new string[] { role + "_screenshot_path was not provided; run screenshot commands first or pass an existing project-local PNG" }, null);
            }

            var normalized = rawPath.Replace('\\', '/').Trim();
            if (Path.IsPathRooted(normalized) || normalized.IndexOf("..", StringComparison.Ordinal) >= 0 || !normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return new PreviewScreenshotRef(role, true, false, normalized, string.Empty, 0, 0, 0, "invalid", new string[0], CommandExecutionResult.Error("invalid_field_value", role + "_screenshot_path must be a project-relative .png path without traversal"));
            }
            if (!normalized.StartsWith("Library/SGGUnityMcp/screenshots/", StringComparison.Ordinal) && !normalized.StartsWith("CaptureArtifacts/", StringComparison.Ordinal))
            {
                return new PreviewScreenshotRef(role, true, false, normalized, string.Empty, 0, 0, 0, "invalid", new string[0], CommandExecutionResult.Error("path_not_allowed", role + "_screenshot_path must be under Library/SGGUnityMcp/screenshots/ or CaptureArtifacts/"));
            }

            var fullPath = Path.GetFullPath(Path.Combine(ProjectPaths.ProjectRoot, normalized.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, ProjectPaths.ProjectRoot))
            {
                return new PreviewScreenshotRef(role, true, false, normalized, string.Empty, 0, 0, 0, "invalid", new string[0], CommandExecutionResult.Error("path_not_allowed", role + "_screenshot_path resolves outside the project root"));
            }
            if (!File.Exists(fullPath))
            {
                return new PreviewScreenshotRef(role, true, false, normalized, fullPath, 0, 0, 0, "not_found", new string[] { role + "_screenshot_path does not exist yet" }, null);
            }

            var width = 0;
            var height = 0;
            TryReadPngDimensions(fullPath, out width, out height);
            var byteLength = new FileInfo(fullPath).Length;
            return new PreviewScreenshotRef(role, true, true, normalized, fullPath, width, height, byteLength, "ok", new string[0], null);
        }

        static bool TryReadPngDimensions(string fullPath, out int width, out int height)
        {
            width = 0;
            height = 0;
            var bytes = new byte[24];
            using (var stream = File.OpenRead(fullPath))
            {
                if (stream.Read(bytes, 0, bytes.Length) < bytes.Length)
                {
                    return false;
                }
            }

            if (bytes[0] != 137 || bytes[1] != 80 || bytes[2] != 78 || bytes[3] != 71 || bytes[12] != 73 || bytes[13] != 72 || bytes[14] != 68 || bytes[15] != 82)
            {
                return false;
            }

            width = ReadBigEndianInt(bytes, 16);
            height = ReadBigEndianInt(bytes, 20);
            return width > 0 && height > 0;
        }

        static int ReadBigEndianInt(byte[] bytes, int offset)
        {
            return (bytes[offset] << 24)
                | (bytes[offset + 1] << 16)
                | (bytes[offset + 2] << 8)
                | bytes[offset + 3];
        }

        static string SourceScanPreviewReportJson(McpCommandContext context, string sourceScanId, List<string> warnings)
        {
            if (string.IsNullOrEmpty(sourceScanId))
            {
                return "null";
            }

            var reportResult = UguiInspectionCommands.CanvasReport(new McpCommandContext(
                context.State,
                context.RequestId,
                context.Project,
                "{\"scan_id\":" + JsonUtil.String(sourceScanId) + ",\"format\":\"json\"}",
                context.SettingsJson));
            if (reportResult.Status != "ok")
            {
                warnings.Add("ugui.canvas.report failed for source_scan_id: " + sourceScanId);
                return "{\"status\":\"error\",\"scan_id\":" + JsonUtil.String(sourceScanId) + "}";
            }

            return reportResult.DataJson;
        }

        static PixelComparisonResult ComparePreviewScreenshots(PreviewScreenshotRef sourceScreenshot, PreviewScreenshotRef generatedScreenshot, int threshold, string diffImagePath, bool dryRun, List<string> warnings)
        {
            if (!sourceScreenshot.Exists || !generatedScreenshot.Exists)
            {
                return PixelComparisonResult.Unavailable("screenshots_missing", threshold, diffImagePath, dryRun);
            }

            Texture2D sourceTexture = null;
            Texture2D generatedTexture = null;
            Texture2D diffTexture = null;
            try
            {
                sourceTexture = LoadPreviewTexture(sourceScreenshot.FullPath);
                generatedTexture = LoadPreviewTexture(generatedScreenshot.FullPath);
                var compareWidth = Math.Min(sourceTexture.width, generatedTexture.width);
                var compareHeight = Math.Min(sourceTexture.height, generatedTexture.height);
                if (compareWidth <= 0 || compareHeight <= 0)
                {
                    warnings.Add("pixel diff skipped because screenshot dimensions are empty");
                    return PixelComparisonResult.Unavailable("empty_dimensions", threshold, diffImagePath, dryRun);
                }

                var comparedPixels = compareWidth * compareHeight;
                if (comparedPixels > MaxPreviewPixelComparePixels)
                {
                    warnings.Add("pixel diff skipped because screenshots exceed max_pixel_compare_pixels");
                    return PixelComparisonResult.Unavailable("pixel_count_exceeded", threshold, diffImagePath, dryRun);
                }

                var sourcePixels = sourceTexture.GetPixels32();
                var generatedPixels = generatedTexture.GetPixels32();
                var diffPixels = new Color32[comparedPixels];
                long changedPixels = 0;
                long totalChannelDelta = 0;
                var maxChannelDelta = 0;
                for (var y = 0; y < compareHeight; y++)
                {
                    for (var x = 0; x < compareWidth; x++)
                    {
                        var sourcePixel = sourcePixels[(y * sourceTexture.width) + x];
                        var generatedPixel = generatedPixels[(y * generatedTexture.width) + x];
                        var redDelta = Math.Abs(sourcePixel.r - generatedPixel.r);
                        var greenDelta = Math.Abs(sourcePixel.g - generatedPixel.g);
                        var blueDelta = Math.Abs(sourcePixel.b - generatedPixel.b);
                        var alphaDelta = Math.Abs(sourcePixel.a - generatedPixel.a);
                        var maxDelta = Math.Max(Math.Max(redDelta, greenDelta), Math.Max(blueDelta, alphaDelta));
                        totalChannelDelta += redDelta + greenDelta + blueDelta + alphaDelta;
                        if (maxDelta > maxChannelDelta)
                        {
                            maxChannelDelta = maxDelta;
                        }
                        if (maxDelta > threshold)
                        {
                            changedPixels++;
                            diffPixels[(y * compareWidth) + x] = new Color32((byte)Math.Min(255, Math.Max(32, maxDelta)), 0, 0, 255);
                        }
                        else
                        {
                            diffPixels[(y * compareWidth) + x] = new Color32(0, 0, 0, 0);
                        }
                    }
                }

                var changedRatio = comparedPixels == 0 ? 0 : changedPixels / (double)comparedPixels;
                var meanChannelDelta = comparedPixels == 0 ? 0 : totalChannelDelta / (double)(comparedPixels * 4);
                var diffImageWritten = false;
                if (!dryRun)
                {
                    diffTexture = new Texture2D(compareWidth, compareHeight, TextureFormat.RGBA32, false);
                    diffTexture.SetPixels32(diffPixels);
                    diffTexture.Apply(false);
                    WritePreviewBinaryArtifact(diffImagePath, diffTexture.EncodeToPNG());
                    diffImageWritten = true;
                }

                return new PixelComparisonResult(
                    true,
                    string.Empty,
                    threshold,
                    sourceTexture.width,
                    sourceTexture.height,
                    generatedTexture.width,
                    generatedTexture.height,
                    compareWidth,
                    compareHeight,
                    comparedPixels,
                    changedPixels,
                    changedRatio,
                    meanChannelDelta,
                    maxChannelDelta,
                    sourceTexture.width == generatedTexture.width && sourceTexture.height == generatedTexture.height,
                    diffImagePath,
                    diffImageWritten,
                    dryRun);
            }
            catch (Exception exception)
            {
                warnings.Add("pixel diff skipped: " + exception.Message);
                return PixelComparisonResult.Unavailable("pixel_diff_failed", threshold, diffImagePath, dryRun);
            }
            finally
            {
                if (sourceTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(sourceTexture);
                }
                if (generatedTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(generatedTexture);
                }
                if (diffTexture != null)
                {
                    UnityEngine.Object.DestroyImmediate(diffTexture);
                }
            }
        }

        static Texture2D LoadPreviewTexture(string fullPath)
        {
            var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(fullPath)))
            {
                UnityEngine.Object.DestroyImmediate(texture);
                throw new InvalidOperationException("failed to load PNG " + Path.GetFileName(fullPath));
            }

            return texture;
        }

        static string PreviewSourceResourceRef(string sourceScanId)
        {
            return string.IsNullOrEmpty(sourceScanId) ? string.Empty : "unity-ui://ugui/scans/" + sourceScanId + "/json";
        }

        static PreviewCaptureResult ExecutePreviewScreenshotCaptures(McpCommandContext context, string previewId, bool dryRun, PreviewScreenshotRef sourceScreenshot, PreviewScreenshotRef generatedScreenshot, ref string sourceScreenshotPath, ref string generatedScreenshotPath, List<string> warnings)
        {
            var captureRole = JsonUtil.ExtractString(context.ArgsJson, "capture_role").Trim();
            if (string.IsNullOrEmpty(captureRole))
            {
                captureRole = "missing";
            }
            if (captureRole != "missing" && captureRole != "source" && captureRole != "generated" && captureRole != "both")
            {
                return PreviewCaptureResult.Invalid(CommandExecutionResult.Error("invalid_field_value", "capture_role must be missing, source, generated, or both"));
            }

            warnings.Add("automatic screenshot capture invokes game.view.screenshot for the current Game View state only; it does not load generated prefabs, switch scenes, apply migration output, or create rollback state");
            var requests = new List<string>();
            var started = 0;
            if (ShouldAttemptPreviewCapture("source", captureRole, sourceScreenshot, warnings))
            {
                if (string.IsNullOrEmpty(sourceScreenshotPath))
                {
                    sourceScreenshotPath = PreviewCapturePath(previewId, "source");
                }
                requests.Add(ExecutePreviewScreenshotCapture(context, "source", sourceScreenshotPath, dryRun, out var startedSource));
                if (startedSource)
                {
                    started++;
                }
            }
            else
            {
                requests.Add(PreviewCaptureSkippedJson("source", sourceScreenshot));
            }

            if (ShouldAttemptPreviewCapture("generated", captureRole, generatedScreenshot, warnings))
            {
                if (string.IsNullOrEmpty(generatedScreenshotPath))
                {
                    generatedScreenshotPath = PreviewCapturePath(previewId, "generated");
                }
                requests.Add(ExecutePreviewScreenshotCapture(context, "generated", generatedScreenshotPath, dryRun, out var startedGenerated));
                if (startedGenerated)
                {
                    started++;
                }
            }
            else
            {
                requests.Add(PreviewCaptureSkippedJson("generated", generatedScreenshot));
            }

            return new PreviewCaptureResult(true, dryRun, captureRole, requests.Count, started, "[" + string.Join(",", requests.ToArray()) + "]", null);
        }

        static bool ShouldAttemptPreviewCapture(string role, string captureRole, PreviewScreenshotRef screenshot, List<string> warnings)
        {
            var roleRequested = captureRole == "missing" || captureRole == "both" || captureRole == role;
            if (!roleRequested)
            {
                return false;
            }

            if (!screenshot.Exists)
            {
                return true;
            }

            if (captureRole == "both" || captureRole == role)
            {
                warnings.Add(role + " screenshot already exists; automatic capture skipped to avoid overwriting an existing artifact path");
            }
            return false;
        }

        static string ExecutePreviewScreenshotCapture(McpCommandContext context, string role, string screenshotPath, bool dryRun, out bool started)
        {
            started = false;
            var captureArgsJson = PreviewGameViewScreenshotArgsJson(context.ArgsJson, screenshotPath, dryRun);
            var result = EditorScreenshotCommands.GameViewScreenshot(new McpCommandContext(
                context.State,
                context.RequestId,
                context.Project,
                captureArgsJson,
                context.SettingsJson));

            if (result.Status != "ok")
            {
                return "{\"role\":" + JsonUtil.String(role)
                    + ",\"command\":\"game.view.screenshot\""
                    + ",\"status\":\"error\""
                    + ",\"capture_started\":false"
                    + ",\"artifact_path\":" + JsonUtil.String(screenshotPath)
                    + ",\"error\":{\"code\":" + JsonUtil.String(result.ErrorCode)
                    + ",\"message\":" + JsonUtil.String(result.ErrorMessage) + "}}";
            }

            var status = JsonUtil.ExtractString(result.DataJson, "status");
            var artifactPath = JsonUtil.ExtractString(result.DataJson, "artifact_path");
            started = !dryRun && (status == "ok" || status == "scheduled" || status == "settling" || status == "submitted");
            return "{\"role\":" + JsonUtil.String(role)
                + ",\"command\":\"game.view.screenshot\""
                + ",\"status\":" + JsonUtil.String(status)
                + ",\"capture_started\":" + JsonUtil.Bool(started)
                + ",\"artifact_path\":" + JsonUtil.String(string.IsNullOrEmpty(artifactPath) ? screenshotPath : artifactPath)
                + ",\"status_command\":\"game.view.screenshot.status\""
                + ",\"result\":" + result.DataJson
                + "}";
        }

        static string PreviewGameViewScreenshotArgsJson(string previewArgsJson, string outputPath, bool dryRun)
        {
            var includeOverlayUi = JsonUtil.ExtractBool(previewArgsJson, "capture_include_overlay_ui", false);
            var backend = JsonUtil.ExtractString(previewArgsJson, "capture_backend").Trim();
            var width = JsonUtil.ExtractInt(previewArgsJson, "capture_width", 0);
            var height = JsonUtil.ExtractInt(previewArgsJson, "capture_height", 0);
            var settleFrames = JsonUtil.ExtractInt(previewArgsJson, "capture_settle_frames", -1);
            var timeoutMs = JsonUtil.ExtractInt(previewArgsJson, "capture_timeout_ms", 0);
            var builder = new StringBuilder();
            builder.Append("{\"output_path\":").Append(JsonUtil.String(outputPath));
            builder.Append(",\"dry_run\":").Append(JsonUtil.Bool(dryRun));
            if (JsonUtil.HasProperty(previewArgsJson, "capture_include_overlay_ui"))
            {
                builder.Append(",\"include_overlay_ui\":").Append(JsonUtil.Bool(includeOverlayUi));
            }
            if (!string.IsNullOrEmpty(backend))
            {
                builder.Append(",\"backend\":").Append(JsonUtil.String(backend));
            }
            if (width >= 64)
            {
                builder.Append(",\"width\":").Append(Clamp(width, 64, 4096));
            }
            if (height >= 64)
            {
                builder.Append(",\"height\":").Append(Clamp(height, 64, 4096));
            }
            if (settleFrames >= 0)
            {
                builder.Append(",\"settle_frames\":").Append(Clamp(settleFrames, 0, 120));
            }
            if (timeoutMs >= 1000)
            {
                builder.Append(",\"timeout_ms\":").Append(Clamp(timeoutMs, 1000, 30000));
            }
            builder.Append('}');
            return builder.ToString();
        }

        static string PreviewCapturePath(string previewId, string role)
        {
            return "Library/SGGUnityMcp/screenshots/uitk-previews/" + previewId + "-" + role + ".png";
        }

        static string PreviewCaptureSkippedJson(string role, PreviewScreenshotRef screenshot)
        {
            var reason = screenshot.Exists ? "artifact_exists" : "role_not_requested";
            return "{\"role\":" + JsonUtil.String(role)
                + ",\"command\":\"game.view.screenshot\""
                + ",\"status\":\"skipped\""
                + ",\"capture_started\":false"
                + ",\"reason\":" + JsonUtil.String(reason)
                + ",\"artifact_path\":" + JsonUtil.String(screenshot.Path)
                + "}";
        }

        static string PreviewScreenshotTargetsJson(string sourceScanId, MigrationValidationBundle bundle, PreviewScreenshotRef sourceScreenshot, PreviewScreenshotRef generatedScreenshot, PreviewCaptureResult capture)
        {
            var sourceResourceRef = PreviewSourceResourceRef(sourceScanId);
            var sourceStatus = string.IsNullOrEmpty(sourceScanId) ? "needs_source_scan" : "ready";
            var generatedStatus = string.IsNullOrEmpty(bundle.PrefabPath) ? "missing_generated_prefab" : "ready";
            var mode = capture.Enabled ? "capture_execution" : "target_selection_only";
            return "{\"status\":\"ok\""
                + ",\"mode\":" + JsonUtil.String(mode)
                + ",\"capture_started\":" + JsonUtil.Bool(capture.CaptureStarted)
                + ",\"automatic_capture\":" + JsonUtil.Bool(capture.Enabled)
                + ",\"bounds_source\":\"ugui_canvas_scan_resource\""
                + ",\"source_scan_id\":" + JsonUtil.String(sourceScanId)
                + ",\"source_resource_ref\":" + JsonUtil.String(sourceResourceRef)
                + ",\"targets\":["
                + "{\"role\":\"old_ugui\",\"status\":" + JsonUtil.String(sourceStatus)
                + ",\"scan_id\":" + JsonUtil.String(sourceScanId)
                + ",\"resource_ref\":" + JsonUtil.String(sourceResourceRef)
                + ",\"recommended_commands\":[\"scene.view.screenshot\",\"game.view.screenshot\",\"camera.screenshot\"]"
                + ",\"bounds_contract\":\"use ugui.canvas.report(source_scan_id) embedded in bounds_diff.source_report\"}"
                + ",{\"role\":\"new_uitk\",\"status\":" + JsonUtil.String(generatedStatus)
                + ",\"prefab_path\":" + JsonUtil.String(bundle.PrefabPath)
                + ",\"panel_settings_path\":" + JsonUtil.String(bundle.PanelSettingsPath)
                + ",\"uxml_path\":" + JsonUtil.String(bundle.UxmlPath)
                + ",\"recommended_commands\":[\"game.view.screenshot\",\"camera.screenshot\"]"
                + ",\"bounds_contract\":\"capture the generated UIDocument prefab with the same viewport/camera and compare against generated_bundle metadata\"}"
                + "]"
                + ",\"visual_diff_inputs\":{\"required\":[\"source_screenshot_path\",\"generated_screenshot_path\"]"
                + ",\"source_screenshot_path\":" + JsonUtil.String(sourceScreenshot.Path)
                + ",\"generated_screenshot_path\":" + JsonUtil.String(generatedScreenshot.Path)
                + ",\"accepted_roots\":[\"Library/SGGUnityMcp/screenshots\",\"CaptureArtifacts\"]}"
                + ",\"capture_execution\":" + capture.ToJson()
                + ",\"orchestration_notes\":" + JsonArrayStrings(capture.Enabled
                    ? new string[] { "uitk.preview selected old/new visual targets and explicitly invoked game.view.screenshot for requested missing artifacts.", "Poll game.view.screenshot.status for scheduled overlay captures, then rerun uitk.preview with both screenshot artifact paths to compute pixel diff." }
                    : new string[] { "uitk.preview selects old/new visual targets and ties them to the uGUI scan resource; capture_screenshots must be true to start screenshot capture.", "Rerun uitk.preview with both screenshot artifact paths to compute pixel diff." })
                + "}";
        }

        static string PreviewBoundsDiffJson(string previewId, string sourceScanId, MigrationValidationBundle bundle, PreviewScreenshotRef sourceScreenshot, PreviewScreenshotRef generatedScreenshot, PreviewCaptureResult capture, PixelComparisonResult pixelDiff, string sourceReportJson, string screenshotTargetsJson)
        {
            var screenshotsReady = sourceScreenshot.Exists && generatedScreenshot.Exists;
            var widthDelta = screenshotsReady ? generatedScreenshot.Width - sourceScreenshot.Width : 0;
            var heightDelta = screenshotsReady ? generatedScreenshot.Height - sourceScreenshot.Height : 0;
            return "{\"status\":\"ok\""
                + ",\"preview_id\":" + JsonUtil.String(previewId)
                + ",\"diff_mode\":\"metadata_bounds_and_pixel_preflight\""
                + ",\"pixel_diff_available\":" + JsonUtil.Bool(pixelDiff.Computed)
                + ",\"pixel_diff\":" + pixelDiff.ToJson()
                + ",\"bounds_diff_available\":" + JsonUtil.Bool(!string.IsNullOrEmpty(sourceScanId))
                + ",\"source_scan_id\":" + JsonUtil.String(sourceScanId)
                + ",\"generation_id\":" + JsonUtil.String(bundle.GenerationId)
                + ",\"source_report\":" + sourceReportJson
                + ",\"screenshot_targets\":" + screenshotTargetsJson
                + ",\"screenshots_ready\":" + JsonUtil.Bool(screenshotsReady)
                + ",\"screenshot_dimension_delta\":{\"width\":" + widthDelta + ",\"height\":" + heightDelta + "}"
                + ",\"source_screenshot\":" + sourceScreenshot.ToJson()
                + ",\"generated_screenshot\":" + generatedScreenshot.ToJson()
                + ",\"capture_execution\":" + capture.ToJson()
                + ",\"generated_bundle\":" + bundle.ToJson()
                + ",\"notes\":" + JsonArrayStrings(capture.Enabled
                    ? new string[] { "Pixel diff compares existing screenshot PNGs after synchronous capture or after async capture status reports an artifact.", "Automatic capture never applies migration output, replaces legacy UI, or overwrites existing screenshot artifacts." }
                    : new string[] { "Pixel diff compares provided existing screenshot PNGs only; pass capture_screenshots:true to invoke the bounded screenshot command workflow." })
                + "}";
        }

        static string MigrationPreviewResultJson(string previewId, bool dryRun, MigrationValidationBundle bundle, string sourceScanId, PreviewScreenshotRef sourceScreenshot, PreviewScreenshotRef generatedScreenshot, PreviewCaptureResult capture, string boundsDiffJson, string screenshotTargetsJson, List<string> artifactPaths, string resourceUri, List<string> warnings)
        {
            var artifacts = dryRun ? "[]" : JsonArrayStrings(artifactPaths);
            var status = capture.CaptureStarted && (!sourceScreenshot.Exists || !generatedScreenshot.Exists) ? "capture_pending" : (!sourceScreenshot.Exists || !generatedScreenshot.Exists) ? "needs_screenshots" : "ready_for_visual_review";
            return "{\"status\":\"ok\""
                + ",\"preview_status\":" + JsonUtil.String(status)
                + ",\"preview_id\":" + JsonUtil.String(previewId)
                + ",\"dry_run\":" + JsonUtil.Bool(dryRun)
                + ",\"would_write\":" + JsonUtil.Bool(dryRun)
                + ",\"written\":" + JsonUtil.Bool(!dryRun)
                + ",\"source_scan_id\":" + JsonUtil.String(sourceScanId)
                + ",\"generation_id\":" + JsonUtil.String(bundle.GenerationId)
                + ",\"migration_bundle\":" + bundle.ToJson()
                + ",\"source_screenshot\":" + sourceScreenshot.ToJson()
                + ",\"generated_screenshot\":" + generatedScreenshot.ToJson()
                + ",\"capture_execution\":" + capture.ToJson()
                + ",\"bounds_diff\":" + boundsDiffJson
                + ",\"screenshot_targets\":" + screenshotTargetsJson
                + ",\"resource_refs\":" + ResourceRefsJson(resourceUri)
                + ",\"artifact_refs\":" + artifacts
                + ",\"screenshot_contract\":{\"accepted_roots\":[\"Library/SGGUnityMcp/screenshots\",\"CaptureArtifacts\"],\"commands\":[\"scene.view.screenshot\",\"game.view.screenshot\",\"game.view.screenshot.status\",\"camera.screenshot\"],\"automatic_capture_args\":[\"capture_screenshots\",\"capture_role\",\"capture_backend\",\"capture_include_overlay_ui\",\"capture_width\",\"capture_height\",\"capture_settle_frames\",\"capture_timeout_ms\"],\"required_for_visual_review\":[\"source_screenshot_path\",\"generated_screenshot_path\"]}"
                + ",\"legacy_modified\":false"
                + ",\"warnings\":" + JsonArrayStrings(warnings)
                + ",\"errors\":[]"
                + ",\"next_valid_actions\":" + JsonArrayStrings(PreviewNextActions(sourceScreenshot, generatedScreenshot, capture))
                + "}";
        }

        static string[] PreviewNextActions(PreviewScreenshotRef sourceScreenshot, PreviewScreenshotRef generatedScreenshot, PreviewCaptureResult capture)
        {
            if (capture.CaptureStarted && (!sourceScreenshot.Exists || !generatedScreenshot.Exists))
            {
                return new string[] { "game.view.screenshot.status", "uitk.preview" };
            }
            if (!sourceScreenshot.Exists || !generatedScreenshot.Exists)
            {
                return new string[] { "scene.view.screenshot", "game.view.screenshot", "camera.screenshot", "uitk.preview" };
            }

            return new string[] { "job.artifact.read", "artifact.metadata", "uitk.validate", "uitk.report" };
        }

        static string MigrationPreviewMarkdown(string previewId, string sourceScanId, MigrationValidationBundle bundle, PreviewScreenshotRef sourceScreenshot, PreviewScreenshotRef generatedScreenshot, PreviewCaptureResult capture, PixelComparisonResult pixelDiff, List<string> warnings)
        {
            var builder = new StringBuilder();
            builder.Append("# Canvas/uGUI To UI Toolkit Preview").Append('\n').Append('\n');
            builder.Append("- preview_id: ").Append(previewId).Append('\n');
            builder.Append("- generation_id: ").Append(bundle.GenerationId).Append('\n');
            builder.Append("- source_scan_id: ").Append(string.IsNullOrEmpty(sourceScanId) ? "(missing)" : sourceScanId).Append('\n');
            builder.Append("- source_resource_ref: ").Append(string.IsNullOrEmpty(sourceScanId) ? "(missing)" : PreviewSourceResourceRef(sourceScanId)).Append('\n');
            builder.Append("- output_root: ").Append(bundle.OutputRoot).Append('\n');
            builder.Append("- old_ugui_target: ugui.canvas.report bounds from source_scan_id").Append('\n');
            builder.Append("- new_uitk_target: ").Append(string.IsNullOrEmpty(bundle.PrefabPath) ? "(missing)" : bundle.PrefabPath).Append('\n');
            builder.Append("- screenshot_target_selection: ").Append(capture.Enabled ? "capture_execution" : "target_selection_only").Append(", capture_started=").Append(capture.CaptureStarted ? "true" : "false").Append('\n');
            builder.Append("- automatic_capture: ").Append(capture.Enabled ? "explicit" : "false").Append('\n');
            builder.Append("- source_screenshot: ").Append(string.IsNullOrEmpty(sourceScreenshot.Path) ? "(missing)" : sourceScreenshot.Path).Append('\n');
            builder.Append("- generated_screenshot: ").Append(string.IsNullOrEmpty(generatedScreenshot.Path) ? "(missing)" : generatedScreenshot.Path).Append('\n');
            builder.Append("- pixel_diff: ").Append(pixelDiff.Computed ? "computed" : pixelDiff.Reason).Append('\n');
            if (pixelDiff.Computed)
            {
                builder.Append("- changed_pixels: ").Append(pixelDiff.ChangedPixels).Append(" / ").Append(pixelDiff.ComparedPixels).Append('\n');
                builder.Append("- changed_ratio: ").Append(DoubleJson(pixelDiff.ChangedRatio)).Append('\n');
                builder.Append("- mean_channel_delta: ").Append(DoubleJson(pixelDiff.MeanChannelDelta)).Append('\n');
                builder.Append("- max_channel_delta: ").Append(pixelDiff.MaxChannelDelta).Append('\n');
                builder.Append("- diff_image: ").Append(pixelDiff.DiffImageWritten ? pixelDiff.DiffImagePath : "(dry-run)").Append('\n');
            }
            builder.Append('\n').Append("## Status").Append('\n');
            builder.Append("Preview compares provided screenshot artifacts when available. Legacy Canvas scene/prefab was not modified. Apply/rollback is not part of this command.").Append('\n');
            if (warnings.Count > 0)
            {
                builder.Append('\n').Append("## Warnings").Append('\n');
                foreach (var warning in warnings)
                {
                    builder.Append("- ").Append(warning).Append('\n');
                }
            }

            return builder.ToString();
        }

        static void WritePreviewArtifact(string artifactPath, string content)
        {
            var projectRoot = ProjectPaths.ProjectRoot;
            var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, MigrationPreviewArtifactRoot.Replace('/', Path.DirectorySeparatorChar)));
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, artifactRoot))
            {
                throw new InvalidOperationException("preview artifact path resolves outside UITK preview artifact root");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllText(fullPath, content, new UTF8Encoding(false));
        }

        static void WritePreviewBinaryArtifact(string artifactPath, byte[] content)
        {
            var projectRoot = ProjectPaths.ProjectRoot;
            var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, MigrationPreviewArtifactRoot.Replace('/', Path.DirectorySeparatorChar)));
            var fullPath = Path.GetFullPath(Path.Combine(projectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsSameOrChildPath(fullPath, artifactRoot))
            {
                throw new InvalidOperationException("preview artifact path resolves outside UITK preview artifact root");
            }

            var directory = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.WriteAllBytes(fullPath, content);
        }

        static string DoubleJson(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                return "0";
            }

            return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string MarkdownReport(string title, string summary, List<string> warnings)
        {
            var builder = new StringBuilder();
            builder.Append("# ").Append(title).Append('\n').Append('\n');
            builder.Append(summary).Append('\n');
            if (warnings.Count > 0)
            {
                builder.Append('\n').Append("## Warnings").Append('\n');
                foreach (var warning in warnings)
                {
                    builder.Append("- ").Append(warning).Append('\n');
                }
            }

            return builder.ToString();
        }

        static string ResourceRefsJson(string uri)
        {
            return JsonArrayStrings(new string[] { uri });
        }

        static string ResourceRefsJson(IEnumerable<string> uris)
        {
            return JsonArrayStrings(uris);
        }

        static string DocumentResourceUri(string inspectionId)
        {
            return "unity-ui://uitk/document/" + inspectionId + "/json";
        }

        static string UxmlResourceUri(string inspectionId)
        {
            return "unity-ui://uitk/uxml/" + inspectionId + "/json";
        }

        static string UssResourceUri(string inspectionId)
        {
            return "unity-ui://uitk/uss/" + inspectionId + "/json";
        }

        static string PanelSettingsResourceUri(string inspectionId)
        {
            return "unity-ui://uitk/panelsettings/" + inspectionId + "/json";
        }

        static string ValidationResourceUri(string validationId)
        {
            return "unity-ui://uitk/validation/" + validationId + "/json";
        }

        static string BindingsResourceUri(string validationId)
        {
            return "unity-ui://uitk/bindings/" + validationId + "/json";
        }

        static string LayoutResourceUri(string analysisId)
        {
            return "unity-ui://uitk/layout/" + analysisId + "/json";
        }

        static string ReferencesResourceUri(string referenceReportId)
        {
            return "unity-ui://uitk/references/" + referenceReportId + "/json";
        }

        static string ReportMarkdownResourceUri(string reportId)
        {
            return "unity-ui://uitk/report/" + reportId + "/markdown";
        }

        static string ReportJsonResourceUri(string reportId)
        {
            return "unity-ui://uitk/report/" + reportId + "/json";
        }

        static string PlanResourceUri(string planId)
        {
            return "unity-ui://uitk/plan/" + planId + "/json";
        }

        static string PlanMarkdownResourceUri(string planId)
        {
            return "unity-ui://uitk/plan/" + planId + "/markdown";
        }

        static string GenerationResourceUri(string generationId)
        {
            return "unity-ui://uitk/generation/" + generationId + "/json";
        }

        static string PreviewResourceUri(string previewId)
        {
            return "unity-ui://uitk/preview/" + previewId + "/json";
        }

        static string ResourceWarningsJson(bool truncated)
        {
            var warnings = new List<string>();
            if (truncated)
            {
                warnings.Add("resource content was truncated by max_chars; rerun uitk.resource.read with a higher max_chars within schema limits");
            }
            return JsonArrayStrings(warnings);
        }

        static void StoreResource(string uri, string content, string mimeType)
        {
            if (!IsUitkResourceUri(uri))
            {
                return;
            }

            lock (ResourceCacheLock)
            {
                if (!ResourceCache.ContainsKey(uri))
                {
                    ResourceCacheOrder.Enqueue(uri);
                }
                ResourceCache[uri] = new UitkResourceSnapshot(uri, content, mimeType);

                while (ResourceCacheOrder.Count > MaxCachedResources)
                {
                    var evicted = ResourceCacheOrder.Dequeue();
                    ResourceCache.Remove(evicted);
                }
            }
        }

        static UitkResourceSnapshot FindResource(string uri)
        {
            if (string.IsNullOrEmpty(uri))
            {
                return null;
            }

            lock (ResourceCacheLock)
            {
                return ResourceCache.TryGetValue(uri, out var resource) ? resource : null;
            }
        }

        static bool IsUitkResourceUri(string uri)
        {
            return !string.IsNullOrEmpty(uri)
                && uri.StartsWith("unity-ui://uitk/", StringComparison.Ordinal)
                && uri.IndexOf("..", StringComparison.Ordinal) < 0;
        }

        static void StoreReport(UitkReportSnapshot snapshot)
        {
            lock (ReportCacheLock)
            {
                ReportCache[snapshot.ReportId] = snapshot;
                ReportCacheOrder.Enqueue(snapshot.ReportId);
                while (ReportCacheOrder.Count > MaxCachedReports)
                {
                    ReportCache.Remove(ReportCacheOrder.Dequeue());
                }
            }
        }

        static UitkReportSnapshot FindReportBySourceId(string sourceId)
        {
            lock (ReportCacheLock)
            {
                foreach (var snapshot in ReportCache.Values)
                {
                    if (string.Equals(snapshot.SourceId, sourceId, StringComparison.Ordinal))
                    {
                        return snapshot;
                    }
                }
            }

            return null;
        }

        static List<string> ExtractStringArray(string json, string property, int maxItems)
        {
            var values = new List<string>();
            var start = FindArrayStart(json, property);
            if (start < 0)
            {
                return values;
            }

            var inString = false;
            var escaped = false;
            var current = new StringBuilder();
            for (var index = start + 1; index < json.Length && values.Count < maxItems; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    if (inString)
                    {
                        current.Append(character);
                    }
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
                    if (inString)
                    {
                        values.Add(current.ToString());
                        current.Length = 0;
                    }
                    inString = !inString;
                    continue;
                }
                if (inString)
                {
                    current.Append(character);
                    continue;
                }
                if (character == ']')
                {
                    break;
                }
            }

            return values;
        }

        static int FindArrayStart(string json, string property)
        {
            if (string.IsNullOrEmpty(json))
            {
                return -1;
            }
            var propertyIndex = json.IndexOf("\"" + property + "\"", StringComparison.Ordinal);
            if (propertyIndex < 0)
            {
                return -1;
            }
            var colon = json.IndexOf(':', propertyIndex);
            if (colon < 0)
            {
                return -1;
            }
            for (var index = colon + 1; index < json.Length; index++)
            {
                if (char.IsWhiteSpace(json[index]))
                {
                    continue;
                }
                return json[index] == '[' ? index : -1;
            }
            return -1;
        }

        static int Clamp(int value, int minimum, int maximum)
        {
            if (value < minimum)
            {
                return minimum;
            }
            return value > maximum ? maximum : value;
        }

        static void Increment(Dictionary<string, int> counts, string key)
        {
            if (string.IsNullOrEmpty(key))
            {
                key = "unknown";
            }
            counts.TryGetValue(key, out var value);
            counts[key] = value + 1;
        }

        static int CountDuplicates(Dictionary<string, int> counts)
        {
            var duplicates = 0;
            foreach (var entry in counts)
            {
                if (entry.Value > 1)
                {
                    duplicates++;
                }
            }
            return duplicates;
        }

        static string DuplicateNamesJson(Dictionary<string, int> counts)
        {
            var values = new List<string>();
            foreach (var entry in counts)
            {
                if (entry.Value > 1)
                {
                    values.Add("{\"name\":" + JsonUtil.String(entry.Key) + ",\"count\":" + entry.Value + "}");
                }
            }
            return JsonArray(values);
        }

        static string CountsJson(Dictionary<string, int> counts)
        {
            var keys = new List<string>(counts.Keys);
            keys.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            builder.Append('{');
            for (var index = 0; index < keys.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(JsonUtil.String(keys[index])).Append(':').Append(counts[keys[index]]);
            }
            builder.Append('}');
            return builder.ToString();
        }

        static List<string> SplitWhitespace(string value)
        {
            var results = new List<string>();
            foreach (var item in Regex.Split(value, @"\s+"))
            {
                if (!string.IsNullOrEmpty(item))
                {
                    results.Add(item);
                }
            }
            return results;
        }

        static List<string> DedupeSorted(IEnumerable<string> values)
        {
            var set = new HashSet<string>();
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    set.Add(value);
                }
            }
            var list = new List<string>(set);
            list.Sort(StringComparer.Ordinal);
            return list;
        }

        static void AddUnique(List<string> values, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }

        static void AddAll(List<string> target, IEnumerable<string> values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrEmpty(value))
                {
                    target.Add(value);
                }
            }
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
                builder.Append(values[index]);
            }
            builder.Append(']');
            return builder.ToString();
        }

        static string JsonArrayStrings(IEnumerable<string> values)
        {
            var builder = new StringBuilder();
            builder.Append('[');
            var index = 0;
            foreach (var value in values)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }
                builder.Append(JsonUtil.String(value));
                index++;
            }
            builder.Append(']');
            return builder.ToString();
        }

        static string NewId(string prefix)
        {
            return prefix + "_" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        }

        sealed class PreviewCaptureResult
        {
            public PreviewCaptureResult(bool enabled, bool dryRun, string captureRole, int requestCount, int startedCount, string requestsJson, CommandExecutionResult errorResult)
            {
                Enabled = enabled;
                DryRun = dryRun;
                CaptureRole = captureRole ?? string.Empty;
                RequestCount = requestCount;
                StartedCount = startedCount;
                RequestsJson = string.IsNullOrEmpty(requestsJson) ? "[]" : requestsJson;
                ErrorResult = errorResult;
            }

            public bool Enabled { get; }
            public bool DryRun { get; }
            public string CaptureRole { get; }
            public int RequestCount { get; }
            public int StartedCount { get; }
            public string RequestsJson { get; }
            public CommandExecutionResult ErrorResult { get; }
            public bool CaptureStarted
            {
                get { return StartedCount > 0; }
            }

            public static PreviewCaptureResult Disabled(bool dryRun)
            {
                return new PreviewCaptureResult(false, dryRun, "missing", 0, 0, "[]", null);
            }

            public static PreviewCaptureResult Invalid(CommandExecutionResult error)
            {
                return new PreviewCaptureResult(true, true, "invalid", 0, 0, "[]", error);
            }

            public string ToJson()
            {
                return "{\"enabled\":" + JsonUtil.Bool(Enabled)
                    + ",\"automatic_capture\":" + JsonUtil.Bool(Enabled)
                    + ",\"dry_run\":" + JsonUtil.Bool(DryRun)
                    + ",\"capture_role\":" + JsonUtil.String(CaptureRole)
                    + ",\"capture_started\":" + JsonUtil.Bool(CaptureStarted)
                    + ",\"request_count\":" + RequestCount
                    + ",\"started_count\":" + StartedCount
                    + ",\"command\":\"game.view.screenshot\""
                    + ",\"status_command\":\"game.view.screenshot.status\""
                    + ",\"requests\":" + RequestsJson
                    + "}";
            }
        }

        sealed class PreviewScreenshotRef
        {
            public PreviewScreenshotRef(string role, bool provided, bool exists, string path, string fullPath, int width, int height, long byteLength, string status, IEnumerable<string> warnings, CommandExecutionResult errorResult)
            {
                Role = role;
                Provided = provided;
                Exists = exists;
                Path = path;
                FullPath = fullPath;
                Width = width;
                Height = height;
                ByteLength = byteLength;
                Status = status;
                Warnings = new List<string>(warnings);
                ErrorResult = errorResult;
            }

            public string Role { get; }
            public bool Provided { get; }
            public bool Exists { get; }
            public string Path { get; }
            public string FullPath { get; }
            public int Width { get; }
            public int Height { get; }
            public long ByteLength { get; }
            public string Status { get; }
            public List<string> Warnings { get; }
            public CommandExecutionResult ErrorResult { get; }

            public string ToJson()
            {
                return "{\"role\":" + JsonUtil.String(Role)
                    + ",\"provided\":" + JsonUtil.Bool(Provided)
                    + ",\"exists\":" + JsonUtil.Bool(Exists)
                    + ",\"status\":" + JsonUtil.String(Status)
                    + ",\"artifact_path\":" + JsonUtil.String(Path)
                    + ",\"width\":" + Width
                    + ",\"height\":" + Height
                    + ",\"byte_length\":" + ByteLength
                    + ",\"warnings\":" + JsonArrayStrings(Warnings)
                    + "}";
            }
        }

        sealed class PixelComparisonResult
        {
            public PixelComparisonResult(bool computed, string reason, int threshold, int sourceWidth, int sourceHeight, int generatedWidth, int generatedHeight, int compareWidth, int compareHeight, int comparedPixels, long changedPixels, double changedRatio, double meanChannelDelta, int maxChannelDelta, bool dimensionsMatch, string diffImagePath, bool diffImageWritten, bool dryRun)
            {
                Computed = computed;
                Reason = reason;
                Threshold = threshold;
                SourceWidth = sourceWidth;
                SourceHeight = sourceHeight;
                GeneratedWidth = generatedWidth;
                GeneratedHeight = generatedHeight;
                CompareWidth = compareWidth;
                CompareHeight = compareHeight;
                ComparedPixels = comparedPixels;
                ChangedPixels = changedPixels;
                ChangedRatio = changedRatio;
                MeanChannelDelta = meanChannelDelta;
                MaxChannelDelta = maxChannelDelta;
                DimensionsMatch = dimensionsMatch;
                DiffImagePath = diffImagePath;
                DiffImageWritten = diffImageWritten;
                DryRun = dryRun;
            }

            public bool Computed { get; }
            public string Reason { get; }
            public int Threshold { get; }
            public int SourceWidth { get; }
            public int SourceHeight { get; }
            public int GeneratedWidth { get; }
            public int GeneratedHeight { get; }
            public int CompareWidth { get; }
            public int CompareHeight { get; }
            public int ComparedPixels { get; }
            public long ChangedPixels { get; }
            public double ChangedRatio { get; }
            public double MeanChannelDelta { get; }
            public int MaxChannelDelta { get; }
            public bool DimensionsMatch { get; }
            public string DiffImagePath { get; }
            public bool DiffImageWritten { get; }
            public bool DryRun { get; }

            public static PixelComparisonResult Unavailable(string reason, int threshold, string diffImagePath, bool dryRun)
            {
                return new PixelComparisonResult(false, reason, threshold, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false, diffImagePath, false, dryRun);
            }

            public string ToJson()
            {
                return "{\"computed\":" + JsonUtil.Bool(Computed)
                    + ",\"reason\":" + JsonUtil.String(Reason)
                    + ",\"threshold\":" + Threshold
                    + ",\"source_dimensions\":{\"width\":" + SourceWidth + ",\"height\":" + SourceHeight + "}"
                    + ",\"generated_dimensions\":{\"width\":" + GeneratedWidth + ",\"height\":" + GeneratedHeight + "}"
                    + ",\"dimensions_match\":" + JsonUtil.Bool(DimensionsMatch)
                    + ",\"compared_area\":{\"width\":" + CompareWidth + ",\"height\":" + CompareHeight + ",\"pixels\":" + ComparedPixels + "}"
                    + ",\"changed_pixels\":" + ChangedPixels
                    + ",\"changed_ratio\":" + DoubleJson(ChangedRatio)
                    + ",\"mean_channel_delta\":" + DoubleJson(MeanChannelDelta)
                    + ",\"max_channel_delta\":" + MaxChannelDelta
                    + ",\"diff_image_path\":" + JsonUtil.String(DiffImagePath)
                    + ",\"diff_image_written\":" + JsonUtil.Bool(DiffImageWritten)
                    + ",\"dry_run\":" + JsonUtil.Bool(DryRun)
                    + "}";
            }
        }

        sealed class MigrationValidationBundle
        {
            public MigrationValidationBundle(string source, string sourceResourceUri, string planId, string generationId, string scanId, string strategy, string outputRoot, string targetName, string migrationMapPath, string uxmlPath, string ussPath, string binderPath, string panelSettingsPath, string prefabPath, bool legacyModified)
            {
                Source = source;
                SourceResourceUri = sourceResourceUri;
                PlanId = planId;
                GenerationId = generationId;
                ScanId = scanId;
                Strategy = strategy;
                OutputRoot = outputRoot;
                TargetName = targetName;
                MigrationMapPath = migrationMapPath;
                UxmlPath = uxmlPath;
                UssPath = ussPath;
                BinderPath = binderPath;
                PanelSettingsPath = panelSettingsPath;
                PrefabPath = prefabPath;
                LegacyModified = legacyModified;
            }

            public string Source { get; }
            public string SourceResourceUri { get; }
            public string PlanId { get; }
            public string GenerationId { get; }
            public string ScanId { get; }
            public string Strategy { get; }
            public string OutputRoot { get; }
            public string TargetName { get; }
            public string MigrationMapPath { get; }
            public string UxmlPath { get; }
            public string UssPath { get; }
            public string BinderPath { get; }
            public string PanelSettingsPath { get; }
            public string PrefabPath { get; }
            public bool LegacyModified { get; }

            public string ToJson()
            {
                return "{\"source\":" + JsonUtil.String(Source)
                    + ",\"source_resource\":" + JsonUtil.String(SourceResourceUri)
                    + ",\"plan_id\":" + JsonUtil.String(PlanId)
                    + ",\"generation_id\":" + JsonUtil.String(GenerationId)
                    + ",\"scan_id\":" + JsonUtil.String(ScanId)
                    + ",\"strategy\":" + JsonUtil.String(Strategy)
                    + ",\"output_root\":" + JsonUtil.String(OutputRoot)
                    + ",\"target_name\":" + JsonUtil.String(TargetName)
                    + ",\"migration_map_path\":" + JsonUtil.String(MigrationMapPath)
                    + ",\"artifacts\":{\"uxml\":" + JsonUtil.String(UxmlPath)
                    + ",\"uss\":" + JsonUtil.String(UssPath)
                    + ",\"binder\":" + JsonUtil.String(BinderPath)
                    + ",\"panel_settings\":" + JsonUtil.String(PanelSettingsPath)
                    + ",\"prefab\":" + JsonUtil.String(PrefabPath)
                    + "}"
                    + ",\"legacy_modified\":" + JsonUtil.Bool(LegacyModified)
                    + "}";
            }
        }

        sealed class GeneratedArtifact
        {
            GeneratedArtifact(string kind, string assetPath, string content, bool allowOverwrite)
            {
                Kind = kind;
                AssetPath = assetPath;
                Content = content;
                AllowOverwrite = allowOverwrite;
            }

            public string Kind { get; }
            public string AssetPath { get; }
            public string Content { get; }
            public bool AllowOverwrite { get; }
            public bool ExistedBefore { get; set; }
            public bool Written { get; set; }

            public static GeneratedArtifact Text(string kind, string assetPath, string content)
            {
                return new GeneratedArtifact(kind, assetPath, content, true);
            }

            public static GeneratedArtifact UnityObject(string kind, string assetPath)
            {
                return new GeneratedArtifact(kind, assetPath, null, false);
            }

            public string ToJson(bool includeGuid)
            {
                return "{\"kind\":" + JsonUtil.String(Kind)
                    + ",\"asset_path\":" + JsonUtil.String(AssetPath)
                    + ",\"exists\":" + JsonUtil.Bool(ExistedBefore)
                    + ",\"will_overwrite\":" + JsonUtil.Bool(ExistedBefore && AllowOverwrite)
                    + ",\"byte_length\":" + (Content == null ? 0 : Encoding.UTF8.GetByteCount(Content))
                    + (includeGuid ? ",\"guid\":" + JsonUtil.String(AssetDatabase.AssetPathToGUID(AssetPath)) : string.Empty)
                    + "}";
            }
        }

        sealed class UxmlNamedElement
        {
            public UxmlNamedElement(string name, string typeName)
            {
                Name = name;
                TypeName = typeName;
            }

            public string Name { get; }
            public string TypeName { get; }
        }

        sealed class BinderField
        {
            public BinderField(UxmlNamedElement element, string fieldName)
            {
                Element = element;
                FieldName = fieldName;
            }

            public UxmlNamedElement Element { get; }
            public string FieldName { get; }
        }

        sealed class DocumentInspectState
        {
            readonly int m_maxDocuments;

            public DocumentInspectState(int maxDocuments, bool includeInactive, bool includePanelSettings, bool includeVisualTreeAsset)
            {
                m_maxDocuments = maxDocuments;
                IncludeInactive = includeInactive;
                IncludePanelSettings = includePanelSettings;
                IncludeVisualTreeAsset = includeVisualTreeAsset;
            }

            public bool IncludeInactive { get; }
            public bool IncludePanelSettings { get; }
            public bool IncludeVisualTreeAsset { get; }
            public int DocumentsFound { get; private set; }
            public int MissingSourceAssets { get; set; }
            public int MissingPanelSettings { get; set; }
            public bool Truncated { get; private set; }
            public List<string> Documents { get; } = new List<string>();
            public List<string> Warnings { get; } = new List<string>();

            public void AddDocument(string json)
            {
                if (DocumentsFound >= m_maxDocuments)
                {
                    Truncated = true;
                    return;
                }

                Documents.Add(json);
                DocumentsFound++;
                if (DocumentsFound >= m_maxDocuments)
                {
                    Truncated = true;
                }
            }
        }

        sealed class FindingList
        {
            readonly int m_maxFindings;
            readonly Dictionary<string, int> m_severityCounts = new Dictionary<string, int>();

            public FindingList(int maxFindings)
            {
                m_maxFindings = maxFindings;
            }

            public int TotalCount { get; private set; }
            public int ErrorCount { get; private set; }
            public int ReturnedCount { get { return ReturnedFindings.Count; } }
            public bool Truncated { get; private set; }
            public List<string> ReturnedFindings { get; } = new List<string>();

            public void Add(string severity, string code, string message, string path, string subject)
            {
                var normalizedSeverity = NormalizeSeverity(severity);
                TotalCount++;
                Increment(m_severityCounts, normalizedSeverity);
                if (normalizedSeverity == "error")
                {
                    ErrorCount++;
                }

                if (ReturnedFindings.Count >= m_maxFindings)
                {
                    Truncated = true;
                    return;
                }

                ReturnedFindings.Add("{\"severity\":" + JsonUtil.String(normalizedSeverity)
                    + ",\"code\":" + JsonUtil.String(string.IsNullOrEmpty(code) ? "UITK000" : code)
                    + ",\"path\":" + JsonUtil.String(path ?? string.Empty)
                    + ",\"subject\":" + JsonUtil.String(subject ?? string.Empty)
                    + ",\"message\":" + JsonUtil.String(message ?? string.Empty)
                    + "}");
            }

            public string SeverityCountsJson()
            {
                return CountsJson(m_severityCounts);
            }

            static string NormalizeSeverity(string severity)
            {
                if (severity == "error" || severity == "warning" || severity == "info")
                {
                    return severity;
                }

                return "info";
            }
        }

        sealed class UxmlValidationInfo
        {
            public HashSet<string> Names { get; } = new HashSet<string>(StringComparer.Ordinal);
            public Dictionary<string, int> NameCounts { get; } = new Dictionary<string, int>();
        }

        sealed class UxmlLayoutInfo
        {
            public int ElementsFound { get; set; }
            public int ScrollContainers { get; set; }
            public HashSet<string> Types { get; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> Classes { get; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> RootClasses { get; } = new HashSet<string>(StringComparer.Ordinal);
            public HashSet<string> ScrollClasses { get; } = new HashSet<string>(StringComparer.Ordinal);

            public void Merge(UxmlLayoutInfo other)
            {
                ElementsFound += other.ElementsFound;
                ScrollContainers += other.ScrollContainers;
                AddAll(Types, other.Types);
                AddAll(Classes, other.Classes);
                AddAll(RootClasses, other.RootClasses);
                AddAll(ScrollClasses, other.ScrollClasses);
            }

            static void AddAll(HashSet<string> target, HashSet<string> source)
            {
                foreach (var value in source)
                {
                    target.Add(value);
                }
            }
        }

        sealed class UssRuleInfo
        {
            public UssRuleInfo(string sourcePath, string selectorGroup, string body)
            {
                SourcePath = sourcePath;
                SelectorGroup = selectorGroup;
                Body = body;
                foreach (var selector in selectorGroup.Split(','))
                {
                    var trimmed = selector.Trim();
                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    Selectors.Add(trimmed);
                    foreach (Match classMatch in Regex.Matches(trimmed, @"\.([A-Za-z_][A-Za-z0-9_-]*)"))
                    {
                        Classes.Add(classMatch.Groups[1].Value);
                    }
                }
            }

            public string SourcePath { get; }
            public string SelectorGroup { get; }
            public string Body { get; }
            public List<string> Selectors { get; } = new List<string>();
            public HashSet<string> Classes { get; } = new HashSet<string>(StringComparer.Ordinal);
        }

        sealed class ScreenSize
        {
            public ScreenSize(int width, int height)
            {
                Width = width;
                Height = height;
            }

            public int Width { get; }
            public int Height { get; }
        }

        sealed class ReferenceList
        {
            readonly int m_maxReferences;
            readonly Dictionary<string, int> m_kindCounts = new Dictionary<string, int>();

            public ReferenceList(int maxReferences)
            {
                m_maxReferences = maxReferences;
            }

            public int TotalCount { get; private set; }
            public int ResolvedCount { get; private set; }
            public int UnresolvedCount { get; private set; }
            public int ReturnedCount { get { return ReturnedReferences.Count; } }
            public bool Truncated { get; private set; }
            public List<string> ReturnedReferences { get; } = new List<string>();

            public void Add(string sourcePath, string kind, string rawReference)
            {
                TotalCount++;
                Increment(m_kindCounts, kind);
                var assetPath = AssetPathFromReference(rawReference);
                var guid = string.IsNullOrEmpty(assetPath) ? string.Empty : AssetDatabase.AssetPathToGUID(assetPath);
                var resolved = !string.IsNullOrEmpty(guid);
                if (resolved)
                {
                    ResolvedCount++;
                }
                else
                {
                    UnresolvedCount++;
                }

                if (ReturnedReferences.Count >= m_maxReferences)
                {
                    Truncated = true;
                    return;
                }

                ReturnedReferences.Add("{\"source_path\":" + JsonUtil.String(sourcePath)
                    + ",\"kind\":" + JsonUtil.String(kind)
                    + ",\"raw\":" + JsonUtil.String(rawReference)
                    + ",\"asset_path\":" + JsonUtil.String(assetPath)
                    + ",\"guid\":" + JsonUtil.String(guid)
                    + ",\"resolved\":" + JsonUtil.Bool(resolved)
                    + "}");
            }

            public string KindCountsJson()
            {
                return CountsJson(m_kindCounts);
            }
        }

        sealed class UitkReportSnapshot
        {
            public UitkReportSnapshot(string reportId, string sourceId, string sourceCommand, string summary, string json, string markdown)
            {
                ReportId = reportId;
                SourceId = sourceId;
                SourceCommand = sourceCommand;
                Summary = summary;
                Json = json;
                Markdown = markdown;
            }

            public string ReportId { get; }
            public string SourceId { get; }
            public string SourceCommand { get; }
            public string Summary { get; }
            public string Json { get; }
            public string Markdown { get; }
        }

        sealed class UitkResourceSnapshot
        {
            public UitkResourceSnapshot(string uri, string content, string mimeType)
            {
                Uri = uri;
                Content = content ?? string.Empty;
                MimeType = mimeType;
            }

            public string Uri { get; }
            public string Content { get; }
            public string MimeType { get; }
        }

        static string Bounded(string value, int maximum)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            value = value.Replace('\n', ' ').Replace('\r', ' ');
            return value.Length <= maximum ? value : value.Substring(0, maximum);
        }
    }
}
