import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandPath = path.join(packageRoot, "Editor", "Commands", "SceneAuthoringSpecCommands.cs");
const uguiAuthoringPath = path.join(packageRoot, "Editor", "Commands", "UguiSceneAuthoringCommands.cs");
const gatewayExamplePath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const cliPath = path.join(process.cwd(), "src", "cli.ts");
const dispatcherPath = path.join(process.cwd(), "src", "mcp", "dispatcher.ts");
const bridgeClientPath = path.join(process.cwd(), "src", "bridge", "bridge-client.ts");

describe("scene authoring composite command source-contract", () => {
  it("declares a separately feature-gated composite apply spec command", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const gatewayExample = fs.readFileSync(gatewayExamplePath, "utf8");
    const policyExample = fs.readFileSync(policyExamplePath, "utf8");
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");
    const cli = fs.readFileSync(cliPath, "utf8");

    expect(source).toContain('"scene.authoring.apply_spec"');
    expect(source).toContain('const string FeatureFlag = "scene_authoring_composite_safe_writes"');
    expect(source).toContain('Risk = "write"');
    expect(source).toContain('Idempotency = "unsafe"');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", true)');
    expect(source).toContain("confirm_apply must be true");
    expect(source).toContain('RequirePrecondition(context.ArgsJson, preconditionHash, "scene.authoring.apply_spec")');
    expect(source).toContain('\\"scene_saved\\":false');
    expect(source).toContain('\\"saved_prefab\\":false');
    expect(source).toContain('\\"opens_scenes\\":false');
    expect(source).toContain('\\"writes_prefab_assets\\":false');
    expect(gatewayExample).toContain('"scene_authoring_composite_safe_writes": false');
    expect(policyExample).toContain('"scene.authoring.apply_spec"');
    expect(policyProfiles).toContain('"scene.authoring.apply_spec"');
    expect(policyProfiles).toContain('"scene_authoring_composite_safe_writes"');
    expect(cli).toContain('"scene.authoring.": "scene_authoring_composite_safe_writes"');
  });

  it("composes only focused safe-write commands with nested feature checks", () => {
    const source = fs.readFileSync(commandPath, "utf8");
    const uguiSource = fs.readFileSync(uguiAuthoringPath, "utf8");
    const dispatcher = fs.readFileSync(dispatcherPath, "utf8");
    const bridgeClient = fs.readFileSync(bridgeClientPath, "utf8");

    expect(source).toContain("RequireCompositeFeatureFlags(context.SettingsJson, steps");
    expect(source).toContain('JsonUtil.ExtractObject(settingsJson, "feature_flags")');
    expect(source).toContain('return "ugui_safe_writes"');
    expect(source).toContain('return "mono_behaviour_safe_writes"');
    expect(source).toContain('return "serialized_safe_writes"');
    expect(source).toContain('return "unityevent_safe_writes"');
    expect(source).toContain("UguiInspectionCommands.UguiObjectCreate");
    expect(source).toContain("UguiSceneAuthoringCommands.UguiTextSet");
    expect(source).toContain("SceneWiringCommands.MonoBehaviourAdd");
    expect(source).toContain("SceneWiringCommands.SerializedPropertySet");
    expect(source).toContain("SceneWiringCommands.SerializedObjectReferenceSet");
    expect(source).toContain("SceneWiringCommands.SerializedArrayEdit");
    expect(source).toContain("SceneWiringCommands.UnityEventPersistentCallSet");
    expect(source).toContain("StepRequiresPrecondition");
    expect(source).toContain("partial_apply_scene_may_be_dirty");
    expect(uguiSource).toContain("ugui.screen.apply_spec supports only same-feature uGUI safe-write commands in this slice");
    expect(dispatcher).toContain("settings.feature_flags = context.features");
    expect(bridgeClient).toContain("feature_flags?: Record<string, boolean>");
  });
});
