import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const uguiCommandsPath = path.join(packageRoot, "Editor", "Commands", "UguiInspectionCommands.cs");
const contractsPath = path.join(process.cwd(), "src", "mcp", "contracts.ts");
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");

describe("Unity uGUI inspection command source-contract", () => {
  it("declares the uGUI command IDs", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain('"ugui.canvas.scan"');
    expect(source).toContain('"ugui.canvas.report"');
    expect(source).toContain('"ugui.canvas.inspect_node"');
    expect(source).toContain('"ugui.canvas.modify"');
    expect(source).toContain('"ugui.object.create"');
    expect(source).toContain('"ugui.component.add"');
    expect(source).toContain('"ugui.resource.read"');
    expect(source).toContain('"ugui.events.extract"');
    expect(source).toContain('"ugui.layout.analyze"');
    expect(source).toContain('"ugui.navigation.analyze"');
    expect(source).toContain('"ugui.raycast.analyze"');
    expect(source).toContain('"ugui.references.report"');
    expect(source).toContain('"ugui.bounds.snapshot"');
    expect(source).toContain('"ugui.screen.diff"');
    expect(source).toContain('"ugui.interaction.smoke"');
  });

  it("keeps uGUI read commands read-only and gates interaction smoke separately", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "ugui_inspection"');
    expect(source).toContain('const string VisualInspectionFeatureFlag = "ugui_visual_inspection"');
    expect(source).toContain('const string InteractionFeatureFlag = "ugui_interaction_tests"');
    expect(source).toContain('const string SafeWriteFeatureFlag = "ugui_safe_writes"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(11);
    expect(source.match(/Idempotency = "safe"/g)).toHaveLength(11);
    expect(source.match(/Risk = "execute"/g)).toHaveLength(1);
    expect(source.match(/Idempotency = "unsafe"/g)).toHaveLength(3);
    expect(source.match(/Risk = "write"/g)).toHaveLength(3);
    expect(source.match(/Idempotency = "idempotent"/g)).toHaveLength(1);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(10);
    expect(source.match(/FeatureFlag = VisualInspectionFeatureFlag/g)).toHaveLength(1);
    expect(source.match(/FeatureFlag = InteractionFeatureFlag/g)).toHaveLength(1);
    expect(source.match(/FeatureFlag = SafeWriteFeatureFlag/g)).toHaveLength(3);
  });

  it("adds feature flag and commands:ugui selector support", () => {
    const contracts = fs.readFileSync(contractsPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");

    expect(contracts).toContain('"ugui"');
    expect(gatewayExample).toContain('"ugui_inspection": false');
    expect(gatewayExample).toContain('"ugui_visual_inspection": false');
    expect(gatewayExample).toContain('"ugui_interaction_tests": false');
    expect(gatewayExample).toContain('"ugui_safe_writes": false');
  });

  it("routes uGUI scan and report geometry follow-up to bounds snapshots", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");
    const scanSource = source.slice(source.indexOf("public static CommandExecutionResult CanvasScan"), source.indexOf('[McpCommand(\n            "ugui.canvas.report"'));
    const reportSource = source.slice(source.indexOf("public static CommandExecutionResult CanvasReport"), source.indexOf('[McpCommand(\n            "ugui.canvas.inspect_node"'));

    expect(scanSource).toContain("RectTransformGeometryEvidenceJson(snapshot, scanResourceUri, string.Empty)");
    expect(scanSource).toContain('\\"geometry_evidence\\"');
    expect(scanSource).toContain('\\"ugui.bounds.snapshot\\"');
    expect(reportSource).toContain("RectTransformGeometryEvidenceJson(snapshot, ScanResourceUri(snapshot.ScanId), string.Empty)");
    expect(reportSource).toContain('\\"geometry_evidence\\"');
    expect(reportSource).toContain('\\"ugui.bounds.snapshot\\"');
  });

  it("implements dry-run-first ask-gated uGUI canvas modify", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const commandSource = source.slice(source.indexOf('"ugui.canvas.modify"'), source.indexOf('[McpCommand(\n            "ugui.object.create"'));

    expect(source).toContain("public static CommandExecutionResult CanvasModify");
    expect(source).toContain("CanvasModifySchemaJson");
    expect(source).toContain("CanvasModifyOutputLimitJson");
    expect(commandSource).toContain('Capability = "ugui_safe_writes"');
    expect(commandSource).toContain('Risk = "write"');
    expect(commandSource).toContain('Idempotency = "idempotent"');
    expect(commandSource).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(commandSource).toContain("RequireCanvasModifyPrecondition");
    expect(source).toContain("precondition_hash is required when ugui.canvas.modify runs with dry_run:false");
    expect(commandSource).toContain("ValidateCanvasModifyTarget(snapshot, node, request");
    expect(source).toContain("first slice only supports cached nodes from loaded_scene targets");
    expect(source).toContain("RectTransform fields require a cached node with has_rect_transform:true");
    expect(source).toContain("ApplyCanvasModify(gameObject, rectTransform");
    expect(source).toContain("Undo.RecordObject(gameObject, \"uGUI Canvas Modify Active State\")");
    expect(source).toContain("Undo.RecordObject(rectTransform, \"uGUI Canvas Modify RectTransform\")");
    expect(source).toContain("EditorSceneManager.MarkSceneDirty(scene)");
    expect(source).toContain("Canvas.ForceUpdateCanvases()");
    expect(source).toContain('\\"event_rewiring\\":false');
    expect(source).toContain('\\"scene_saved\\":false');
    expect(source).toContain('\\"saved_prefab\\":false');
    expect(source).toContain('\\"opens_scenes\\":false');
    expect(source).toContain("only_active_self_and_rect_transform_fields_are_allowlisted");
    expect(source).toContain("Sha256Prefixed(\"ugui.canvas.modify\\n\"");
    expect(source).toContain("active_self");
    expect(source).toContain("anchor_min");
    expect(source).toContain("anchor_max");
    expect(source).toContain("anchored_position");
    expect(source).toContain("size_delta");
    expect(source).toContain("pivot");
    expect(source).toContain("local_scale");
    expect(source).toContain("local_euler_angles");
    expect(cli).toContain('"ugui.canvas.modify": "ugui_safe_writes"');
    expect(gatewayExample).toContain('"ugui_safe_writes": false');
    expect(policyExample).toContain('"ugui.canvas.modify"');
  });

  it("implements dry-run-first loaded-scene uGUI object create", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const commandSource = source.slice(source.indexOf('"ugui.object.create"'), source.indexOf('[McpCommand(\n            "ugui.component.add"'));

    expect(source).toContain("public static CommandExecutionResult UguiObjectCreate");
    expect(source).toContain("UguiObjectCreateSchemaJson");
    expect(source).toContain("UguiSceneAuthoringOutputLimitJson");
    expect(commandSource).toContain('Capability = "ugui_safe_writes"');
    expect(commandSource).toContain('Risk = "write"');
    expect(commandSource).toContain('Idempotency = "unsafe"');
    expect(commandSource).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(source).toContain('request.DryRun = JsonUtil.ExtractBool(json, "dry_run", true)');
    expect(source).toContain('PathGuard.RequireUnityAssetPath(rawPath)');
    expect(source).toContain('scene_path must reference a loaded scene');
    expect(source).toContain('RequireUguiParent(context.ArgsJson, scene');
    expect(source).toContain('GetComponent<RectTransform>() == null && gameObject.GetComponent<Canvas>() == null');
    expect(source).toContain('new GameObject(request.Name, typeof(RectTransform))');
    expect(source).toContain('Undo.RegisterCreatedObjectUndo(gameObject, "Create uGUI Object")');
    expect(source).toContain('Undo.SetTransformParent(gameObject.transform, parent.transform, "Parent uGUI Object")');
    expect(source).toContain('ApplyRectTransformCreateState(rectTransform, request)');
    expect(source).toContain('rectTransform.SetSiblingIndex');
    expect(source).toContain('EditorSceneManager.MarkSceneDirty(scene)');
    expect(source).toContain('Canvas.ForceUpdateCanvases()');
    expect(source).toContain('sibling_collision');
    expect(source).toContain('planned_object_path');
    expect(source).toContain('\\"scene_saved\\":false');
    expect(source).toContain('\\"saved_prefab\\":false');
    expect(source).toContain('\\"writes_prefab_assets\\":false');
    expect(source).toContain('\\"loaded_scenes_only\\":true');
    expect(cli).toContain('"ugui.object.": "ugui_safe_writes"');
    expect(policyExample).toContain('"ugui.object.create"');
  });

  it("implements allowlisted uGUI component add without arbitrary MonoBehaviour writes", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const commandSource = source.slice(source.indexOf('"ugui.component.add"'), source.indexOf('[McpCommand(\n            "ugui.resource.read"'));

    expect(source).toContain("public static CommandExecutionResult UguiComponentAdd");
    expect(source).toContain("UguiComponentAddSchemaJson");
    expect(commandSource).toContain('Capability = "ugui_safe_writes"');
    expect(commandSource).toContain('Risk = "write"');
    expect(commandSource).toContain('Idempotency = "unsafe"');
    expect(source).toContain("UguiAuthoringComponentTypeNames");
    expect(source).toContain('"UnityEngine.UI.Image"');
    expect(source).toContain('"UnityEngine.UI.Button"');
    expect(source).toContain('"UnityEngine.UI.VerticalLayoutGroup"');
    expect(source).toContain('"UnityEngine.CanvasGroup"');
    expect(source).toContain('"TMPro.TextMeshProUGUI"');
    expect(source).toContain("ResolveAllowedUguiAuthoringComponentType");
    expect(source).toContain("component_type is ambiguous; use full_type_name and assembly_name");
    expect(source).toContain("arbitrary MonoBehaviour components are not allowlisted by ugui.component.add");
    expect(source).toContain("Undo.AddComponent(target, componentType)");
    expect(source).toContain("EditorUtility.SetDirty(component)");
    expect(source).toContain('\\"unity_event_mutation\\":false');
    expect(source).toContain('\\"serialized_fields_configured\\":false');
    expect(source).toContain('component_index');
    expect(source).toContain('full_type_name');
    expect(source).toContain('assembly_name');
    expect(cli).toContain('"ugui.component.": "ugui_safe_writes"');
    expect(policyExample).toContain('"ugui.component.add"');
  });

  it("implements read-only uGUI bounds snapshots tied to scan resources", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult BoundsSnapshot");
    expect(source).toContain("BoundsSnapshotSchemaJson");
    expect(source).toContain("BoundsSnapshotOutputLimitJson");
    expect(source).toContain('Capability = "ugui_visual_inspection"');
    expect(source).toContain("BoundsSnapshotJson");
    expect(source).toContain("BoundsNodeJson");
    expect(source).toContain("RectTransformGeometryEvidenceJson");
    expect(source).toContain("RectTransformNodeCount");
    expect(source).toContain("BoundsSnapshotResourceUri");
    expect(source).toContain("unity-ui://ugui/bounds/");
    expect(source).toContain('\\"scan_resource_ref\\"');
    expect(source).toContain('\\"geometry_evidence\\"');
    expect(source).toContain('\\"bounds_command\\":\\"ugui.bounds.snapshot\\"');
    expect(source).toContain('\\"bounds_follow_up\\"');
    expect(source).toContain('\\"inline_in_scan_default\\":false');
    expect(source).toContain('\\"cached_at_scan_time\\":true');
    expect(source).toContain('\\"bounds_resource_ref\\"');
    expect(source).toContain('\\"world_bounds_source\\":\\"RectTransform.GetWorldCorners\\"');
    expect(source).toContain('\\"coordinate_space\\":\\"cached_rect_transform_world_aabb\\"');
    expect(source).toContain('\\"source_contract\\":{\\"requires_scan_id\\":true');
    expect(source).toContain('\\"uses_generic_scene_screenshot\\":false');
    expect(source).toContain('\\"captures_screenshot\\":false');
    expect(source).toContain('\\"world_bounds\\"');
    expect(source).toContain('\\"rect_nodes_found\\"');
    expect(source).toContain('\\"bounds_returned\\"');
    expect(source).toContain("StoreResource(resourceUri, resultJson, \"application/json\")");
  });

  it("wires uGUI visual inspection feature flag to config, policy, and CLI hints", () => {
    const cli = fs.readFileSync(cliPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    expect(cli).toContain('"ugui.bounds.": "ugui_visual_inspection"');
    expect(cli).toContain('"ugui.canvas.": "ugui_inspection"');
    expect(gatewayExample).toContain('"ugui_visual_inspection": false');
    expect(policyExample).toContain('"ugui.bounds.snapshot"');
    expect(policyExample).toContain('"ugui.screen.diff"');
    expect(cli).toContain('"ugui.screen.diff": "ugui_inspection"');
  });

  it("implements read-only cached uGUI screen diffs", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult ScreenDiff");
    expect(source).toContain("ScreenDiffSchemaJson");
    expect(source).toContain("ScreenDiffOutputLimitJson");
    expect(source).toContain('Capability = "ugui_inspection"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('Idempotency = "safe"');
    expect(source).toContain("FindScan(leftScanId)");
    expect(source).toContain("FindScan(rightScanId)");
    expect(source).toContain("ScreenDiffNodeMap");
    expect(source).toContain("ScreenDiffChangedFields");
    expect(source).toContain("ScreenDiffComponentSignature");
    expect(source).toContain('\\"read_only\\":true');
    expect(source).toContain('\\"diff_counts\\"');
    expect(source).toContain('\\"added\\"');
    expect(source).toContain('\\"removed\\"');
    expect(source).toContain('\\"changed\\"');
  });

  it("implements dry-run-first ask-gated uGUI interaction smoke", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");

    expect(source).toContain("public static CommandExecutionResult InteractionSmoke");
    expect(source).toContain("InteractionSmokeSchemaJson");
    expect(source).toContain("InteractionSmokeOutputLimitJson");
    expect(source).toContain('Capability = "ugui_interaction_tests"');
    expect(source).toContain('\\"dry_run\\":{\\"type\\":\\"boolean\\",\\"default\\":true');
    expect(source).toContain('\\"confirm_execute\\":{\\"type\\":\\"boolean\\",\\"default\\":false');
    expect(source).toContain('\\"allow_edit_mode\\":{\\"type\\":\\"boolean\\",\\"default\\":false');
    expect(source).toContain("confirm_execute must be true when ugui.interaction.smoke runs with dry_run:false");
    expect(source).toContain("EditorApplication.isPlaying");
    expect(source).toContain("EditorApplication.isPlayingOrWillChangePlaymode");
    expect(source).toContain("FindCurrentEventSystem");
    expect(source).toContain("SetEventSystemSelectedGameObject");
    expect(source).toContain("InvokeUnityEventProperty");
    expect(source).toContain("SetPropertyValue");
    expect(source).toContain("FindComponentByTypeName");
    expect(source).toContain("UnityEngine.UI.Button");
    expect(source).toContain("UnityEngine.UI.Toggle");
    expect(source).toContain("UnityEngine.UI.InputField");
    expect(source).toContain("TMPro.TMP_InputField");
    expect(source).toContain("node.GameObject");
    expect(source).toContain("unity-ui://ugui/interaction/");
    expect(source).toContain('\\"callbacks_may_mutate_game_state\\":true');
    expect(source).toContain('\\"writes_files\\":false');
    expect(source).toContain('\\"saves_scenes\\":false');
    expect(source).toContain('\\"opens_scenes\\":false');
    expect(source).toContain("StoreResource(resourceUri, resultJson, \"application/json\")");
    expect(cli).toContain('"ugui.interaction.": "ugui_interaction_tests"');
    expect(gatewayExample).toContain('"ugui_interaction_tests": false');
    expect(policyExample).toContain('"ugui.interaction.smoke"');
  });

  it("provides external prefab layout override schema without applying mutations", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("PrefabLayoutOverrideSchemaJson");
    expect(source).toContain('\\"prefab_layout_overrides\\"');
    expect(source).toContain('\\"prefab_path\\"');
    expect(source).toContain('\\"root_path\\"');
    expect(source).toContain('\\"size\\"');
    expect(source).toContain('\\"parent_size\\"');
    expect(source).toContain('\\"anchored_position\\"');
    expect(source).toContain('\\"scale\\"');
    expect(source).toContain("TryExtractVector2");
    expect(source).toContain("TryExtractVector3");
    expect(source).toContain("ApplyPrefabLayoutOverridesToPreviewInstance");
    expect(source).toContain("FindTransformByPath");
    expect(source).toContain("SetSizeWithCurrentAnchors");
    expect(source).toContain("anchoredPosition");
    expect(source).toContain("localScale");
    expect(source).toContain("applied to temporary preview instance only");
    expect(source).toContain('\\"applied\\"');
    expect(source).toContain('\\"ignored\\"');
    expect(source).toContain('\\"invalid\\"');
  });

  it("implements minimal bounded scan with reflection-style uGUI type names", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult CanvasScan");
    expect(source).toContain("StoreScan(snapshot)");
    expect(source).toContain("const int MaxCachedScans = 16");
    expect(source).toContain("SceneManager.sceneCount");
    expect(source).toContain("AssetDatabase.LoadAssetAtPath<GameObject>");
    expect(source).toContain("EditorSceneManager.NewPreviewScene");
    expect(source).toContain("PrefabUtility.InstantiatePrefab");
    expect(source).toContain("EditorSceneManager.ClosePreviewScene");
    expect(source).toContain("Canvas.ForceUpdateCanvases");
    expect(source).toContain("GetComponent<Canvas>()");
    expect(source).toContain('"UnityEngine.UI.Button"');
    expect(source).toContain('"TMPro.TMP_Text"');
    expect(source).toContain("IsKnownUguiType");
    expect(source).toContain('\\"max_nodes\\"');
    expect(source).toContain('\\"maximum\\":5000');
    expect(source).toContain('\\"maximum\\":64');
  });

  it("implements bounded in-memory scan report and node inspection", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("static readonly Dictionary<string, ScanSnapshot> ScanCache");
    expect(source).toContain("static readonly Queue<string> ScanCacheOrder");
    expect(source).toContain("public static CommandExecutionResult CanvasReport");
    expect(source).toContain("public static CommandExecutionResult InspectNode");
    expect(source).toContain('\\"source_targets\\"');
    expect(source).toContain('\\"canvas_roots\\"');
    expect(source).toContain('\\"nodes_included\\"');
    expect(source).toContain('\\"component_counts\\"');
    expect(source).toContain('\\"type_counts\\"');
    expect(source).toContain('\\"missing_scripts\\"');
    expect(source).toContain('\\"override_summary\\"');
    expect(source).toContain("NodeInspectJson");
    expect(source).toContain('\\"rect_transform\\"');
    expect(source).toContain('\\"active_self\\"');
    expect(source).toContain('\\"active_in_hierarchy\\"');
    expect(source).toContain('\\"sibling_index\\"');
    expect(source).toContain('\\"has_rect_transform\\"');
    expect(source).toContain('\\"render_mode\\"');
    expect(source).toContain("MissingScan(scanId)");
    expect(source).toContain('\\"status\\":\\"not_found\\"');
  });

  it("implements bounded process-local uGUI resource reads", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult ResourceRead");
    expect(source).toContain("ResourceReadSchemaJson");
    expect(source).toContain("ResourceReadOutputLimitJson");
    expect(source).toContain("MaxCachedResources = 64");
    expect(source).toContain("static readonly Dictionary<string, UguiResourceSnapshot> ResourceCache");
    expect(source).toContain("StoreResource(scanResourceUri");
    expect(source).toContain("StoreResource(reportResourceUri");
    expect(source).toContain("StoreResource(layoutResourceUri");
    expect(source).toContain("StoreResource(eventsResourceUri");
    expect(source).toContain("StoreResource(navigationResourceUri");
    expect(source).toContain("StoreResource(raycastResourceUri");
    expect(source).toContain("StoreResource(referencesResourceUri");
    expect(source).toContain("FindResource(uri)");
    expect(source).toContain("IsUguiResourceUri");
    expect(source).toContain("ScanResourceUri");
    expect(source).toContain("ReportResourceUri");
    expect(source).toContain("LayoutResourceUri");
    expect(source).toContain("EventsResourceUri");
    expect(source).toContain("NavigationResourceUri");
    expect(source).toContain("RaycastResourceUri");
    expect(source).toContain("ReferencesResourceUri");
    expect(source).toContain("unity-ui://ugui/scans/");
    expect(source).toContain("unity-ui://ugui/reports/");
    expect(source).toContain("unity-ui://ugui/layout/");
    expect(source).toContain("unity-ui://ugui/events/");
    expect(source).toContain("unity-ui://ugui/navigation/");
    expect(source).toContain("unity-ui://ugui/raycast/");
    expect(source).toContain("unity-ui://ugui/references/");
    expect(source).toContain('\\"max_chars\\"');
    expect(source).toContain('\\"content\\"');
    expect(source).toContain('\\"total_chars\\"');
    expect(source).toContain('\\"truncated\\"');
  });

  it("implements cached layout analysis with bounded findings", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult LayoutAnalyze");
    expect(source).toContain("FindScan(scanId)");
    expect(source).toContain("MissingScan(scanId)");
    expect(source).toContain("LayoutAnalysisJson");
    expect(source).toContain("AnalyzeLayoutNode");
    expect(source).toContain("ExtractScreenSizes");
    expect(source).toContain("LayoutFindingsJson");
    expect(source).toContain("LayoutWarningsJson");
    expect(source).toContain("SortLayoutFindingsBySeverity");
    expect(source).toContain("SeverityRank");
    expect(source).toContain("FindingRank");
    expect(source).toContain("FindLowestSeverityFindingIndex");
    expect(source).toContain("const int MaxLayoutFindings = 250");
    expect(source).toContain("AnchorMin");
    expect(source).toContain("AnchorMax");
    expect(source).toContain("SizeDelta");
    expect(source).toContain("AnchoredPosition");
    expect(source).toContain("LocalScale");
    expect(source).toContain("LocalEulerAngles");
    expect(source).toContain("WorldMin");
    expect(source).toContain("WorldMax");
    expect(source).toContain('\\"nodes_analyzed\\"');
    expect(source).toContain('\\"screen_sizes\\"');
    expect(source).toContain('\\"include_absolute_positions\\"');
    expect(source).toContain('\\"findings\\"');
    expect(source).toContain('\\"returned_findings_count\\"');
    expect(source).toContain('\\"findings_by_severity\\"');
    expect(source).toContain('\\"findings_by_code\\"');
    expect(source).toContain('"fixed_size_anchors"');
    expect(source).toContain('"stretch_with_size_delta"');
    expect(source).toContain('"content_size_fitter_layout_group"');
    expect(source).toContain('"scrollrect_missing_viewport_or_content"');
    expect(source).not.toContain("layout analysis from persisted scans is not implemented");
  });

  it("prioritizes higher severity layout findings before truncating", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("SortLayoutFindingsBySeverity(findings)");
    expect(source).toContain('case "warning"');
    expect(source).toContain('case "info"');
    expect(source).toContain("FindLowestSeverityFindingIndex");
    expect(source).toContain("FindingRank(finding)");
    expect(source).toContain("FindingRank(findings[lowestIndex])");
    expect(source).toContain("finding.OverrideDependent ? 10 : 0");
    expect(source).toContain("MaxLayoutFindings + 1");
    expect(source).not.toContain("!node.HasRectTransform || findings.Count >= MaxLayoutFindings + 1");
  });

  it("implements cached read-only persistent UnityEvent extraction", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult EventsExtract");
    expect(source).toContain("FindScan(scanId)");
    expect(source).toContain("MissingScan(scanId)");
    expect(source).toContain('ExtractStringArray(context.ArgsJson, "event_types"');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_target_paths"');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "include_method_signatures"');
    expect(source).toContain('JsonUtil.ExtractInt(context.ArgsJson, "max_events"');
    expect(source).toContain("EventsExtractJson");
    expect(source).toContain("EventsExtractOutputLimitJson");
    expect(source).toContain("UnityEventBase");
    expect(source).toContain("GetPersistentEventCount");
    expect(source).toContain("GetPersistentTarget");
    expect(source).toContain("GetPersistentMethodName");
    expect(source).toContain("GetPersistentListenerState");
    expect(source).toContain("BindingFlags.Instance");
    expect(source).toContain("EventBindingSnapshot");
    expect(source).toContain("EventBindings");
    expect(source).toContain("ObjectPathsByInstanceId");
    expect(source).toContain("EventBindingsJson");
    expect(source).toContain("EventBindingMatchesFilters");
    expect(source).toContain("TryMethodSignature");
    expect(source).toContain('\\"target_path\\"');
    expect(source).toContain('\\"method_signature\\"');
    expect(source).toContain('\\"listener_state\\"');
    expect(source).toContain('\\"bindings_found\\"');
    expect(source).toContain('\\"events_truncated\\"');
    expect(source).toContain('\\"read_only\\":true');
    expect(source).not.toContain("UnityEvent extraction from persisted scans is not implemented");
  });

  it("implements cached read-only Selectable navigation analysis", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult NavigationAnalyze");
    expect(source).toContain("NavigationAnalyzeSchemaJson");
    expect(source).toContain("NavigationAnalyzeOutputLimitJson");
    expect(source).toContain("MaxNavigationFindings = 250");
    expect(source).toContain("UnityEngine.UI.Selectable");
    expect(source).toContain("UnityEngine.EventSystems.EventSystem");
    expect(source).toContain("AddNavigationNodeSnapshot");
    expect(source).toContain("AddEventSystemSnapshot");
    expect(source).toContain("GetPropertyValue(component, \"navigation\")");
    expect(source).toContain("selectOnUp");
    expect(source).toContain("selectOnDown");
    expect(source).toContain("selectOnLeft");
    expect(source).toContain("selectOnRight");
    expect(source).toContain("NavigationAnalysisJson");
    expect(source).toContain("DetectNavigationCycles");
    expect(source).toContain("NavigationSelectablesJson");
    expect(source).toContain("NavigationEdgesJson");
    expect(source).toContain("NavigationFindingsJson");
    expect(source).toContain("navigation_target_not_in_scan");
    expect(source).toContain("navigation_cycle");
    expect(source).toContain("missing_default_selection_candidates");
    expect(source).toContain("automatic_navigation");
    expect(source).toContain("explicit_navigation_without_targets");
    expect(source).toContain('\\"selectables_found\\"');
    expect(source).toContain('\\"explicit_edges_count\\"');
    expect(source).toContain('\\"event_systems_found\\"');
    expect(source).toContain('\\"read_only\\":true');
  });

  it("implements cached read-only Graphic raycast analysis", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult RaycastAnalyze");
    expect(source).toContain("RaycastAnalyzeSchemaJson");
    expect(source).toContain("RaycastAnalyzeOutputLimitJson");
    expect(source).toContain("MaxRaycastFindings = 250");
    expect(source).toContain("MaxRaycastOverlapChecks = 20000");
    expect(source).toContain("UnityEngine.UI.Graphic");
    expect(source).toContain("UnityEngine.UI.GraphicRaycaster");
    expect(source).toContain("UnityEngine.CanvasGroup");
    expect(source).toContain("AddRaycastGraphicSnapshot");
    expect(source).toContain("AddCanvasGroupSnapshot");
    expect(source).toContain("AddGraphicRaycasterSnapshot");
    expect(source).toContain("GetPropertyValue(component, \"color\")");
    expect(source).toContain("GetBoolProperty(component, \"raycastTarget\"");
    expect(source).toContain("GetBoolProperty(component, \"blocksRaycasts\"");
    expect(source).toContain("RaycastAnalysisJson");
    expect(source).toContain("AnalyzeRaycastOverlaps");
    expect(source).toContain("RaycastGraphicsJson");
    expect(source).toContain("CanvasGroupsJson");
    expect(source).toContain("GraphicRaycastersJson");
    expect(source).toContain("RaycastFindingsJson");
    expect(source).toContain("invisible_graphic_blocks_raycast");
    expect(source).toContain("overlapping_raycast_targets");
    expect(source).toContain("graphic_raycaster_without_event_system");
    expect(source).toContain("canvas_group_blocks_not_interactable");
    expect(source).toContain('\\"raycast_targets\\"');
    expect(source).toContain('\\"graphic_raycasters_found\\"');
    expect(source).toContain('\\"event_systems_found\\"');
    expect(source).toContain('\\"read_only\\":true');
  });

  it("implements cached read-only uGUI reference reports", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult ReferencesReport");
    expect(source).toContain("ReferencesReportSchemaJson");
    expect(source).toContain("ReferencesReportOutputLimitJson");
    expect(source).toContain("MaxAssetReferencesPerScan = 10000");
    expect(source).toContain("ExtractAssetReferences");
    expect(source).toContain("SerializedObject(component)");
    expect(source).toContain("SerializedPropertyType.ObjectReference");
    expect(source).toContain("AssetDatabase.GetAssetPath(value)");
    expect(source).toContain("AssetDatabase.AssetPathToGUID(assetPath)");
    expect(source).toContain("MonoScript.FromMonoBehaviour");
    expect(source).toContain("ReferenceType");
    expect(source).toContain("AssetReferencesJson");
    expect(source).toContain("AssetReferenceSummariesJson");
    expect(source).toContain("include_sprites");
    expect(source).toContain("include_textures");
    expect(source).toContain("include_fonts");
    expect(source).toContain("include_materials");
    expect(source).toContain("include_scripts");
    expect(source).toContain('\\"references_found\\"');
    expect(source).toContain('\\"distinct_assets_count\\"');
    expect(source).toContain('\\"counts_by_type\\"');
    expect(source).toContain('\\"read_only\\":true');
  });

  it("keeps uGUI safe writes narrow without scene opening, prefab saving, files, network, or process APIs", () => {
    const source = fs.readFileSync(uguiCommandsPath, "utf8");

    expect(source).not.toContain("EditorSceneManager.OpenScene");
    expect(source).not.toContain("EditorSceneManager.SaveScene");
    expect(source).not.toContain("SaveAsPrefabAsset");
    expect(source).not.toContain("PrefabUtility.Save");
    expect(source).not.toContain("ApplyPrefabInstance");
    expect(source).not.toContain("ApplyPropertyOverride");
    expect(source).not.toContain("AssetDatabase.SaveAssets");
    expect(source).not.toContain("AssetDatabase.ImportAsset");
    expect(source).not.toContain("AssetDatabase.CreateAsset");
    expect(source).not.toContain("AssetDatabase.DeleteAsset");
    expect(source).not.toContain("AssetDatabase.MoveAsset");
    expect(source).not.toContain("WriteAll");
    expect(source).not.toContain("CreateDirectory");
    expect(source).not.toContain("File.");
    expect(source).not.toContain("Directory.");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
    expect(source).not.toContain("Process.Start");
    expect(source).toContain("EditorSceneManager.MarkSceneDirty(scene)");
    expect(source).toContain("Undo.RecordObject");
  });
});
