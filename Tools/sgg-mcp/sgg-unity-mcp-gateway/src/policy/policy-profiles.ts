import type { PolicyConfig } from "../config/config.js";

export type PolicyAction = "allow" | "ask" | "deny";

export const POLICY_PROFILE_NAMES = ["readonly", "inspect", "api-reflection", "unity-docs", "profiler-readonly", "profiler-recording", "package-management", "test-validation", "safe-writes", "runtime-debug", "allow-all"] as const;

export type PolicyProfileName = (typeof POLICY_PROFILE_NAMES)[number];

export type PolicyProfile = {
  title: string;
  description: string;
  allow?: string[];
  ask?: string[];
  deny?: string[];
  features?: string[];
};

export const POLICY_PROFILES: Record<PolicyProfileName, PolicyProfile> = {
  readonly: {
    title: "Read-only status",
    description: "Allow low-risk status, logs, project settings, package list, loaded scenes, build settings/profiles, test discovery/status, and artifact metadata.",
    allow: [
      "logs.*",
      "compile.status",
      "compile.wait_ready",
      "compile.warnings.recent",
      "project.settings.summary",
      "project.settings.inspect",
      "package.list",
      "package.info",
      "package.operation.status",
      "scene.loaded",
      "build.settings",
      "build.profile.list",
      "build.run.status",
      "test.framework.status",
      "test.list",
      "test.run.status",
      "job.artifact.read",
      "editor.menu.operation.status",
      "artifact.list",
      "artifact.metadata",
    ],
  },
  inspect: {
    title: "Semantic inspection",
    description: "Allow read-only scene, prefab, asset index, script, visual screenshot, semantic diff, and knowledge inspection commands.",
    allow: [
      "component.types.search",
      "component.type.inspect",
      "gameobject.builtin_assets",
      "gameobject.bounds",
      "scene.visible_objects",
      "scene.view.screenshot",
      "scene.view.capture_multi_angle",
      "scene.view.overlay",
      "game.view.screenshot",
      "camera.list",
      "camera.screenshot",
      "editor.window.inspect",
      "editor.window.screenshot",
      "scene.hierarchy",
      "scene.search",
      "scene.inspect_object",
      "scene.inspect_component",
      "prefab.hierarchy",
      "prefab.inspect_object",
      "prefab.inspect_component",
      "asset.components.list",
      "project.assets.taxonomy",
      "asset.index.status",
      "asset.list",
      "asset.semantic_index.status",
      "asset.semantic_search",
      "asset.info",
      "asset.serialized.inspect",
      "asset.search",
      "asset.references",
      "asset.dependencies",
      "dependency.query",
      "asset.relocation.preflight",
      "asset.text.read",
      "asset.text.find",
      "asset.text.search",
      "shader.list",
      "shader.dependencies",
      "shader.references.report",
      "tmp.font_asset.list",
      "tmp.font_asset.inspect",
      "tmp.font_asset.unicode_set.extract",
      "tmp.unicode_set.collect",
      "tmp.unicode_set.normalize",
      "tmp.font_asset.coverage.check",
      "tmp.font_asset.static.plan",
      "tmp.font_asset.report",
      "tmp.font_asset.validate",
      "tmp.font_asset.resource.read",
      "ui.screen.inventory",
      "ui.screen.inspect",
      "ui.interaction.map",
      "ui.text.inventory",
      "ui.text.references.report",
      "ugui.canvas.scan",
      "ugui.canvas.report",
      "ugui.canvas.inspect_node",
      "ugui.resource.read",
      "ugui.events.extract",
      "ugui.layout.analyze",
      "ugui.navigation.analyze",
      "ugui.raycast.analyze",
      "ugui.references.report",
      "ugui.bounds.snapshot",
      "ugui.screen.diff",
      "serializereference.types.search",
      "serializereference.index.status",
      "serializereference.types.used",
      "serializereference.usages.find",
      "serializereference.graph",
      "script.read",
      "script.hash",
      "script.validate",
      "script.capabilities",
      "script.text_edits.spec",
      "asset.guid.resolve",
      "asset.risk_report",
      "addressables.status",
      "addressables.groups.list",
      "addressables.entries.search",
      "addressables.labels.report",
      "addressables.dependencies.report",
      "addressables.build.preflight",
      "scriptable_object.inspect",
      "scriptable_object.schema",
      "animation.controller.inspect",
      "animation.controller.graph",
      "animation.bindings.report",
      "animation.clip.inspect",
      "timeline.asset.inspect",
      "gameplay.data.references.report",
      "gameplay.data.validation.report",
      "diff.unity_yaml.semantic",
      "diff.unity_yaml.preview",
      "diff.unity_yaml.inspect_change",
      "unity.docs.index.status",
      "unity.docs.query",
      "unity.docs.open_reference",
      "knowledge.list",
      "knowledge.query",
      "knowledge.read",
    ],
    ask: ["asset.index.rebuild", "asset.index.clear", "serializereference.index.rebuild", "unity.docs.index.rebuild", "unity.docs.index.clear", "merge.unity_yaml.preview", "merge.unity_yaml.plan"],
    features: ["semantic_scene_inspection", "asset_index", "asset_semantic_index", "serialize_reference_index", "asset_readonly", "ui_readonly", "ui_text_readonly", "ugui_inspection", "ugui_visual_inspection", "addressables_readonly", "gameplay_data_readonly", "editor_screenshots", "semantic_unity_diff", "unity_docs_local", "knowledge_resources"],
  },
  "api-reflection": {
    title: "Unity API reflection",
    description: "Allow read-only local reflection over loaded Unity Editor assemblies and script assembly ownership for API verification.",
    allow: ["unity.api.loaded_assemblies", "unity.api.reflect.search", "unity.api.reflect.type", "unity.api.reflect.member", "script.assembly.resolve", "assembly.list", "assembly.sources.list", "assembly.references.graph", "csproj.generated.list", "csproj.generated.inspect", "csproj.generated.compare", "script.relocation.preflight", "assembly.inspect", "assembly.references.explain", "assembly.references.trace", "script.type.resolve"],
    features: ["unity_api_reflection"],
  },
  "unity-docs": {
    title: "Unity local docs",
    description: "Allow read-only bounded local Unity docs status/search/reference reads without network access while keeping cache writes confirmation-gated.",
    allow: ["unity.docs.index.status", "unity.docs.query", "unity.docs.open_reference"],
    ask: ["unity.docs.index.rebuild", "unity.docs.index.clear"],
    features: ["unity_docs_local"],
  },
  "profiler-readonly": {
    title: "Profiler read-only",
    description: "Allow bounded Unity Profiler availability, module, already-available frame marker, search, session, and process-memory/project-artifact report reads without recording captures.",
    allow: ["profiler.status", "profiler.modules", "profiler.frame.summary", "profiler.frame.markers", "profiler.marker.search", "profiler.report.create", "profiler.report.read", "profiler.memory.snapshot.compare", "profiler.memory.snapshot.list", "profiler.session.list", "profiler.session.summary", "frame_debugger.status", "frame_debugger.events"],
    features: ["profiler_readonly"],
  },
  "profiler-recording": {
    title: "Profiler recording",
    description: "Keep Unity Profiler recording controls and capture export confirmation-gated, explicit, and project-local.",
    ask: ["profiler.recording.start", "profiler.recording.stop", "profiler.capture.save"],
    features: ["profiler_recording"],
  },
  "package-management": {
    title: "Package management",
    description: "Keep package mutation and registry search commands confirmation-gated.",
    allow: ["package.list", "package.info", "package.operation.list", "package.operation.status"],
    ask: ["package.install", "package.install.git", "package.remove", "package.search", "package.update", "package.operation.prune"],
    features: ["package_registry_network"],
  },
  "test-validation": {
    title: "Test validation",
    description: "Allow focused compile/log/editor/build/test discovery and validation reads while keeping refresh, after-edit validation, and test execution confirmation-gated.",
    allow: ["compile.status", "compile.wait_ready", "compile.warnings.recent", "validation.preflight", "logs.*", "editor.status", "build.settings", "build.profile.list", "build.run.status", "test.framework.status", "test.list", "test.run.status", "editor.menu.operation.status", "job.artifact.read", "artifact.list", "artifact.metadata"],
    ask: ["assets.refresh", "compile.refresh_and_wait", "validation.after_edit", "test.run", "test.run.cancel", "editor.quit"],
    features: ["build_test_automation"],
  },
  "safe-writes": {
    title: "Safe editor writes",
    description: "Keep bounded asset, scene, GameObject, Component, and Material writes confirmation-gated.",
    allow: ["editor.status", "scene.loaded", "assets.refresh", "script.read", "script.hash", "script.validate", "script.capabilities", "script.text_edits.spec", "gameobject.write_risk_report", "prefab.write_risk_report", "prefab.edit.preview", "component.property_schema", "material.property_schema", "material.properties.inspect"],
    ask: [
      "asset.folder.create",
      "asset.move",
      "asset.delete",
      "tmp.font_asset.static.create",
      "tmp.font_asset.static.bake",
      "tmp.font_asset.references.swap",
      "tmp.font_asset.fallbacks.set",
      "tmp.font_asset.static.convert",
      "script.write",
      "asset.import",
      "scene.open",
      "scene.save",
      "scene.create",
      "scene.unload",
      "gameobject.*",
      "prefab.component.modify",
      "prefab.variant.create",
      "prefab.component.property.set",
      "prefab.component.add",
      "prefab.component.remove",
      "prefab.object.create",
      "prefab.ugui.object.create",
      "prefab.ugui.component.add",
      "prefab.ugui.text.set",
      "prefab.mono_behaviour.add",
      "prefab.serialized.property.set",
      "prefab.serialized.object_reference.set",
      "prefab.serialized.array.edit",
      "prefab.authoring.apply_spec",
      "prefab.object.delete",
      "prefab.apply_overrides",
      "component.*",
      "material.*",
      "ugui.canvas.modify",
      "ugui.object.create",
      "ugui.component.add",
      "ugui.text.set",
      "ugui.image.set",
      "ugui.canvas_group.set",
      "ugui.layout_group.configure",
      "ugui.button.configure",
      "ugui.selectable.navigation.set",
      "ugui.screen.apply_spec",
      "scene.authoring.apply_spec",
      "mono_behaviour.add",
      "serialized.property.set",
      "serialized.object_reference.set",
      "serialized.array.edit",
      "unityevent.persistent_call.set",
      "editor.selection.set",
      "editor.active_tool.set",
      "editor.window.position.set",
      "editor.tags.add",
      "editor.tags.remove",
      "editor.layers.add",
      "editor.layers.remove",
      "editor.menu.execute",
      "editor.menu.operation.start",
      "editor.quit",
      "merge.unity_yaml.apply",
    ],
    features: ["asset_readonly", "asset_safe_writes", "editor_safe_writes", "editor_utilities", "ugui_safe_writes", "scene_authoring_composite_safe_writes", "prefab_authoring_composite_safe_writes", "mono_behaviour_safe_writes", "serialized_safe_writes", "unityevent_safe_writes", "semantic_unity_diff"],
  },
  "runtime-debug": {
    title: "Runtime debugging",
    description: "Allow bounded runtime snapshots and keep frame/event sampling confirmation-gated.",
    allow: ["editor.status", "runtime.capture_state", "runtime.quality_settings.inspect", "runtime.state_machine.compile", "runtime.event_capture.status", "runtime.event_capture.read"],
    ask: ["editor.playmode.enter", "editor.playmode.set", "editor.playmode.exit", "editor.quit", "runtime.state_machine.run", "runtime.event_capture.start", "runtime.event_capture.stop", "runtime.event_capture.cancel", "ugui.interaction.smoke", "uitk.interaction.smoke"],
    features: ["editor_csharp_execution", "runtime_state_debugging", "ugui_interaction_tests", "uitk_interaction_tests"],
  },
  "allow-all": {
    title: "Allow all commands",
    description: "Dangerous trusted-local profile: allow every built-in/trusted Gateway-visible command for the project without ask gates.",
    allow: ["*"],
  },
};

export function createDefaultPolicy(): PolicyConfig {
  return {
    schema_version: 1,
    defaults: {
      read: "allow",
      write: "ask",
      execute: "deny",
      destructive: "ask",
      network: "deny",
    },
    projects: {},
  };
}

export function applyPolicyProfile(policy: PolicyConfig, project: string, profileName: PolicyProfileName): PolicyConfig {
  const profile = POLICY_PROFILES[profileName];
  let next = clonePolicy(policy);
  if (profileName === "allow-all") {
    next.projects[project] = { allow: ["*"], ask: [], deny: [] };
    return next;
  }

  for (const action of ["allow", "ask", "deny"] as const) {
    for (const pattern of profile[action] ?? []) {
      next = setProjectPolicyRule(next, project, action, pattern);
    }
  }
  return next;
}

export function setProjectPolicyRule(policy: PolicyConfig, project: string, action: PolicyAction, pattern: string): PolicyConfig {
  const next = clonePolicy(policy);
  const projectRules = next.projects[project] ?? { allow: [], ask: [], deny: [] };
  projectRules.allow = removePattern(projectRules.allow, pattern);
  projectRules.ask = removePattern(projectRules.ask, pattern);
  projectRules.deny = removePattern(projectRules.deny, pattern);
  projectRules[action] = addPattern(projectRules[action], pattern);
  next.projects[project] = projectRules;
  return next;
}

export function summarizeProjectPolicy(policy: PolicyConfig, project: string): Record<string, unknown> {
  return {
    project,
    defaults: policy.defaults,
    rules: policy.projects[project] ?? { allow: [], ask: [], deny: [] },
  };
}

export function isPolicyProfileName(value: string): value is PolicyProfileName {
  return POLICY_PROFILE_NAMES.includes(value as PolicyProfileName);
}

function clonePolicy(policy: PolicyConfig): PolicyConfig {
  return {
    schema_version: policy.schema_version,
    defaults: { ...policy.defaults },
    projects: Object.fromEntries(
      Object.entries(policy.projects).map(([project, rules]) => [project, {
        allow: [...rules.allow],
        ask: [...rules.ask],
        deny: [...rules.deny],
      }]),
    ),
  };
}

function addPattern(patterns: string[], pattern: string): string[] {
  return patterns.includes(pattern) ? patterns : [...patterns, pattern].sort();
}

function removePattern(patterns: string[], pattern: string): string[] {
  return patterns.filter((item) => item !== pattern);
}
