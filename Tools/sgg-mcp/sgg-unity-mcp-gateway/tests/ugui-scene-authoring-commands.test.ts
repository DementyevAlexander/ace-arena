import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const uguiAuthoringCommandsPath = path.join(packageRoot, "Editor", "Commands", "UguiSceneAuthoringCommands.cs");
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");

describe("uGUI scene authoring command source-contract", () => {
  it("declares uGUI authoring command IDs", () => {
    const source = fs.readFileSync(uguiAuthoringCommandsPath, "utf8");

    expect(source).toContain('"ugui.text.set"');
    expect(source).toContain('"ugui.image.set"');
    expect(source).toContain('"ugui.canvas_group.set"');
    expect(source).toContain('"ugui.layout_group.configure"');
    expect(source).toContain('"ugui.button.configure"');
    expect(source).toContain('"ugui.selectable.navigation.set"');
    expect(source).toContain('"ugui.screen.apply_spec"');
    expect(source).toContain('const string FeatureFlag = "ugui_safe_writes"');
    expect(source.match(/Risk = "write"/g)).toHaveLength(7);
    expect(source).toContain('Idempotency = "idempotent"');
    expect(source).toContain('Idempotency = "unsafe"');
  });

  it("implements dry-run-first loaded-scene uGUI text setting", () => {
    const source = fs.readFileSync(uguiAuthoringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult UguiTextSet");
    expect(source).toContain("UnityEngine.UI.Text");
    expect(source).toContain("TMPro.TMP_Text");
    expect(source).toContain("TMPro.TextMeshProUGUI");
    expect(source).toContain("At least one allowlisted text field is required");
    expect(source).toContain("text must be bounded text without NUL bytes");
    expect(source).toContain("fontSize");
    expect(source).toContain("raycastTarget");
    expect(source).toContain("horizontal_alignment");
    expect(source).toContain("vertical_alignment");
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(source).toContain('RequirePrecondition(context.ArgsJson, hash, "ugui.text.set")');
    expect(source).toContain('Undo.RecordObject(component, "Set uGUI Text")');
    expect(source).toContain("EditorSceneManager.MarkSceneDirty(scene)");
    expect(source).toContain("Canvas.ForceUpdateCanvases()");
    expect(source).toContain('\\"scene_saved\\":false');
    expect(source).toContain('\\"saved_prefab\\":false');
    expect(source).toContain('\\"opens_scenes\\":false');
    expect(source).toContain('\\"writes_prefab_assets\\":false');
    expect(source).toContain('\\"loaded_scenes_only\\":true');
  });

  it("implements P1 wrapper commands without UnityEvent or array mutation", () => {
    const source = fs.readFileSync(uguiAuthoringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult UguiImageSet");
    expect(source).toContain("UnityEngine.UI.Image");
    expect(source).toContain("UnityEngine.UI.RawImage");
    expect(source).toContain("sprite_asset_path");
    expect(source).toContain("texture_asset_path");
    expect(source).toContain("material_asset_path");
    expect(source).toContain("public static CommandExecutionResult UguiCanvasGroupSet");
    expect(source).toContain("UnityEngine.CanvasGroup");
    expect(source).toContain("blocksRaycasts");
    expect(source).toContain("ignoreParentGroups");
    expect(source).toContain("public static CommandExecutionResult UguiLayoutGroupConfigure");
    expect(source).toContain("UnityEngine.UI.HorizontalLayoutGroup");
    expect(source).toContain("UnityEngine.UI.GridLayoutGroup");
    expect(source).toContain("UnityEngine.UI.ContentSizeFitter");
    expect(source).toContain("UnityEngine.UI.LayoutElement");
    expect(source).toContain("public static CommandExecutionResult UguiButtonConfigure");
    expect(source).toContain("targetGraphic");
    expect(source).toContain("public static CommandExecutionResult UguiSelectableNavigationSet");
    expect(source).toContain("selectOnUp");
    expect(source).toContain("selectOnDown");
    expect(source).toContain("ConfigureComponent(context");
    expect(source).toContain('Undo.RecordObject(component, command)');
    expect(source).toContain('RequirePrecondition(context.ArgsJson, hash, command)');
    expect(source).not.toContain("onClick");
    expect(source).not.toContain("PersistentCall");
    expect(source).not.toContain("serialized.array.edit");
  });

  it("implements bounded screen specs without bypassing non-uGUI feature buckets", () => {
    const source = fs.readFileSync(uguiAuthoringCommandsPath, "utf8");

    expect(source).toContain("public static CommandExecutionResult UguiScreenApplySpec");
    expect(source).toContain("const int MaxApplySpecSteps = 32");
    expect(source).toContain('confirm_apply must be true when ugui.screen.apply_spec runs with dry_run:false');
    expect(source).toContain('RequirePrecondition(context.ArgsJson, preconditionHash, "ugui.screen.apply_spec")');
    expect(source).toContain("IsApplySpecCommand(command)");
    expect(source).toContain("supports only same-feature uGUI safe-write commands");
    expect(source).toContain("UguiInspectionCommands.UguiObjectCreate(nested)");
    expect(source).toContain("UguiInspectionCommands.UguiComponentAdd(nested)");
    expect(source).toContain("return UguiTextSet(nested)");
    expect(source).toContain("return UguiImageSet(nested)");
    expect(source).toContain("return UguiCanvasGroupSet(nested)");
    expect(source).toContain("return UguiLayoutGroupConfigure(nested)");
    expect(source).toContain("return UguiButtonConfigure(nested)");
    expect(source).toContain("return UguiSelectableNavigationSet(nested)");
    expect(source).toContain("partial_failure");
    expect(source).not.toContain('SceneWiringCommands.MonoBehaviourAdd');
    expect(source).not.toContain('SceneWiringCommands.SerializedPropertySet');
    expect(source).not.toContain('SceneWiringCommands.SerializedObjectReferenceSet');
  });

  it("wires uGUI authoring commands to CLI feature hints and safe-write policy", () => {
    const cli = fs.readFileSync(cliPath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");

    expect(cli).toContain('"ugui.text.": "ugui_safe_writes"');
    expect(cli).toContain('"ugui.image.": "ugui_safe_writes"');
    expect(cli).toContain('"ugui.canvas_group.": "ugui_safe_writes"');
    expect(cli).toContain('"ugui.layout_group.": "ugui_safe_writes"');
    expect(cli).toContain('"ugui.button.": "ugui_safe_writes"');
    expect(cli).toContain('"ugui.selectable.": "ugui_safe_writes"');
    expect(cli).toContain('"ugui.screen.": "ugui_safe_writes"');
    expect(policyExample).toContain('"ugui.text.set"');
    expect(policyExample).toContain('"ugui.image.set"');
    expect(policyExample).toContain('"ugui.canvas_group.set"');
    expect(policyExample).toContain('"ugui.layout_group.configure"');
    expect(policyExample).toContain('"ugui.button.configure"');
    expect(policyExample).toContain('"ugui.selectable.navigation.set"');
    expect(policyExample).toContain('"ugui.screen.apply_spec"');
    expect(policyProfiles).toContain('"ugui.text.set"');
    expect(policyProfiles).toContain('"ugui.image.set"');
    expect(policyProfiles).toContain('"ugui.canvas_group.set"');
    expect(policyProfiles).toContain('"ugui.layout_group.configure"');
    expect(policyProfiles).toContain('"ugui.button.configure"');
    expect(policyProfiles).toContain('"ugui.selectable.navigation.set"');
    expect(policyProfiles).toContain('"ugui.screen.apply_spec"');
  });
});
