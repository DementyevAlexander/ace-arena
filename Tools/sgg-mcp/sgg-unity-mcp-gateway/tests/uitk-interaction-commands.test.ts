import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const root = process.cwd();
const packageRoot = path.join(root, "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "UitkInteractionCommands.cs");
const commandMetaPath = `${commandPath}.meta`;
const unityTestPath = path.join(packageRoot, "Tests", "Editor", "McpPr106UitkInteractionSmokeTests.cs");
const unityTestMetaPath = `${unityTestPath}.meta`;
const configPath = path.join(root, "config", "gateway.example.json");
const policyPath = path.join(root, "config", "policy.example.json");
const cliPath = path.join(root, "src", "cli.ts");
const profilesPath = path.join(root, "src", "policy", "policy-profiles.ts");
const policyManagerPath = path.join(packageRoot, "Editor", "Setup", "GatewayPolicyManager.cs");

describe("UITK interaction smoke source-contract", () => {
  it("registers one execute/unsafe Play Mode command with the bounded identity schema", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const meta = fs.readFileSync(commandMetaPath, "utf8");
    const schemaLiteral = source.match(/const string InteractionSmokeSchemaJson = "((?:\\.|[^"])*)";/)?.[1];
    const schema = JSON.parse(JSON.parse(`"${schemaLiteral}"`));

    expect(source).toContain('"uitk.interaction.smoke"');
    expect(source).toContain('Risk = "execute"');
    expect(source).toContain('Idempotency = "unsafe"');
    expect(source).toContain('Capability = InteractionFeatureFlag');
    expect(source).toContain('const string InteractionFeatureFlag = "uitk_interaction_tests"');
    expect(source).toContain('RequiresEditorStatus = new string[] { "playing", "playing_paused" }');
    expect(source).toContain('SupportsJob = true');
    expect(source).toContain('"scene_path"');
    expect(source).toContain('"document_object_path"');
    expect(source).toContain('"component_index"');
    expect(source).toContain('\\"minItems\\":1,\\"maxItems\\":16');
    expect(source).toContain('\\"maximum\\":64');
    expect(source).toContain('\\"maximum\\":120');
    expect(source).toContain('\\"maximum\\":1024');
    expect(source).toContain('\\"maxLength\\":4096');
    expect(source).toContain('\\"focus\\"');
    expect(source).toContain('\\"click\\"');
    expect(source).toContain('\\"set_toggle\\"');
    expect(source).toContain('\\"set_text\\"');
    expect(source).toContain('\\"set_dropdown_index\\"');
    expect(source).toContain('JsonUtil.ExtractBool(argsJson, "dry_run", true)');
    expect(source).toContain('JsonUtil.ExtractBool(argsJson, "confirm_execute", false)');
    expect(schema.properties.steps.items.oneOf).toHaveLength(4);
    expect(schema.properties.steps.items.oneOf.every((branch: { type: string; additionalProperties: boolean }) => branch.type === "object" && branch.additionalProperties === false)).toBe(true);
    expect(schema.oneOf).toHaveLength(2);
    expect(schema.oneOf[1].required).toEqual(["dry_run", "confirm_execute", "precondition_hash"]);
    expect(schema.oneOf[1].properties.confirm_execute).toEqual({ enum: [true] });
    expect(schema.oneOf[1].properties.precondition_hash).toEqual({ type: "string", minLength: 1 });
    expect(schema.oneOf.every((branch: { type: string; additionalProperties: boolean }) => branch.type === "object" && branch.additionalProperties === false)).toBe(true);
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("resolves exact loaded scene/document/component identity and bounded unique named controls", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("SceneManager.sceneCount");
    expect(source).toContain("scene.isLoaded");
    expect(source).toContain("StringComparison.Ordinal");
    expect(source).toContain("GetComponents<Component>()");
    expect(source).toContain("as UIDocument");
    expect(source).toContain("rootVisualElement");
    expect(source).toContain("MaxControlNodes = 5000");
    expect(source).toContain("TryCollectNamedControls");
    expect(source).toContain("duplicate exact names");
    expect(source).toContain("ConditionalWeakTable<VisualElement, ControlIdentity>");
    expect(source).toContain("ControlRuntimeIdentity");
    expect(source).toContain("VisualHierarchyPath");
    expect(source).toContain("ControlTypeName");
    expect(source).toContain("Button");
    expect(source).toContain("Toggle");
    expect(source).toContain("TextField");
    expect(source).toContain("DropdownField");
  });

  it("uses typed APIs, preflights before mutation, and reports bounded drift/callback safety", () => {
    const source = fs.readFileSync(commandPath, "utf8");

    expect(source).toContain("BuildPlans");
    expect(source).toContain("CanExecuteAll");
    expect(source).toContain("TryResolveStepWithoutDrift");
    expect(source).toContain("precondition_failed");
    expect(source).toContain("SHA256.Create");
    expect(source).toContain("NavigationSubmitEvent.GetPooled");
    expect(source).toContain("submitEvent.target = button");
    expect(source).toContain("button.SendEvent");
    expect(source).toContain("toggle.value = plan.Request.ToggleValue");
    expect(source).toContain("textField.value = plan.Request.Text");
    expect(source).toContain("dropdown.index = plan.Request.DropdownIndex");
    expect(source).toContain("partial callback side effects");
    expect(source).toContain("RestorationStepsJson");
    expect(source).toContain("reverse_order");
    expect(source).toContain("files_saved\\\":false");
    expect(source).toContain("scenes_saved\\\":false");
    expect(source).toContain("EditorApplication.Step()");
    expect(source).toContain("EditorApplication.QueuePlayerLoopUpdate()");
    expect(source).toContain("actual_frame_count_delta");
    expect(source).toContain("settle_completion_verified");
    expect(source).toContain("playmode_state_drift");
    expect(source).toContain("document_panel_unavailable");
    expect(source).toContain("focus_not_acquired");
    expect(source).toContain("control primitive value drifted after preflight");
    expect(source).toContain("control action precondition drifted");
    expect(source).toContain("requested control value drifted after preflight");
    expect(source).toContain('AppendHashField(builder, "settle_frames"');
    expect(source).toContain('AppendHashField(builder, "enabled_in_hierarchy"');
    expect(source).toContain("restoration_requires_new_dry_run");
    expect(source).toContain('builder.Append(",\\\"dropdown_index\\\":"')
    expect(source).not.toContain("System.Reflection");
    expect(source).not.toContain("Activator.CreateInstance");
    expect(source).not.toContain("editor.execute_csharp");
    expect(source).not.toContain("Undo.");
    expect(source).not.toContain("EditorSceneManager.OpenScene");
    expect(source).not.toContain("AssetDatabase.SaveAssets");
  });

  it("contains focused typed behavior tests without scene or file persistence", () => {
    const source = fs.readFileSync(unityTestPath, "utf8");
    const meta = fs.readFileSync(unityTestMetaPath, "utf8");

    expect(source).toContain("ExactNameResolutionRejectsDuplicatesAndAcceptsUniqueNames");
    expect(source).toContain("ActionValidationAllowsOnlyTypedCompatibleControls");
    expect(source).toContain("ButtonSubmitUsesTypedNavigationEventAndInvokesClickedCallback");
    expect(source).toContain("ToggleTextAndDropdownUseTypedMutationAndExposeRestorationValues");
    expect(source).toContain("SettlePolicyDistinguishesRunningAndPausedPlayMode");
    expect(source).toContain("PreconditionFingerprintChangesForCurrentRequestAndRuntimeIdentityDrift");
    expect(source).not.toContain("File.Write");
    expect(source).not.toContain("SceneManager.LoadScene");
    expect(meta).toContain("fileFormatVersion: 2");
    expect(meta).toContain("guid:");
  });

  it("exposes the feature in examples, ask policy, profile flags, and specific CLI routing", () => {
    const config = JSON.parse(fs.readFileSync(configPath, "utf8")) as { features: Record<string, boolean> };
    const policy = JSON.parse(fs.readFileSync(policyPath, "utf8")) as { projects: { game: { ask: string[] } } };
    const cli = fs.readFileSync(cliPath, "utf8");
    const profiles = fs.readFileSync(profilesPath, "utf8");
    const policyManager = fs.readFileSync(policyManagerPath, "utf8");

    expect(config.features.uitk_interaction_tests).toBe(false);
    expect(policy.projects.game.ask).toContain("uitk.interaction.smoke");
    expect(profiles).toContain('"uitk.interaction.smoke"');
    expect(profiles).toContain('"uitk_interaction_tests"');
    expect(policyManager).toContain('"uitk_interaction_tests"');
    const specific = cli.indexOf('"uitk.interaction.": "uitk_interaction_tests"');
    const generic = cli.indexOf('"uitk.": "uitk_inspection"');
    expect(specific).toBeGreaterThanOrEqual(0);
    expect(generic).toBeGreaterThan(specific);
  });
});
