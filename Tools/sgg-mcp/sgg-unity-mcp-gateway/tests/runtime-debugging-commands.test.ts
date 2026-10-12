import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const runtimeCommandsPath = path.join(packageRoot, "Editor", "Commands", "RuntimeDebuggingCommands.cs");
const commandsDocPath = path.join(process.cwd(), "docs", "en", "reference", "commands.md");
const generatedCommandsDocPath = path.join(process.cwd(), "docs", "commands.generated.md");
const policyExamplePath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");

describe("Unity runtime debugging command source-contract", () => {
  it("declares runtime capture and state-machine commands through the catalog", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");

    expect(source).toContain('"runtime.capture_state"');
    expect(source).toContain('"runtime.quality_settings.inspect"');
    expect(source).toContain('"runtime.state_machine.compile"');
    expect(source).toContain('"runtime.state_machine.run"');
    expect(source).toContain('"runtime.event_capture.start"');
    expect(source).toContain('"runtime.event_capture.status"');
    expect(source).toContain('"runtime.event_capture.stop"');
    expect(source).toContain('"runtime.event_capture.cancel"');
    expect(source).toContain('"runtime.event_capture.read"');
    expect(source).toContain('Risk = "read"');
    expect(source).toContain('SupportsJob = true');
  });

  it("exposes fixed read-only effective QualitySettings in Play Mode", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"runtime.quality_settings.inspect"'), source.indexOf('[McpCommand(\n            "runtime.state_machine.compile"'));

    expect(commandSource).toContain('Risk = "read"');
    expect(commandSource).toContain('Idempotency = "safe"');
    expect(commandSource).toContain('RequiresEditorStatus = new string[] { "playing", "playing_paused" }');
    expect(commandSource).toContain("QualitySettings.GetQualityLevel()");
    expect(commandSource).toContain("QualitySettings.names");
    expect(commandSource).toContain("QualitySettings.vSyncCount");
    expect(commandSource).toContain("QualitySettings.antiAliasing");
    expect(commandSource).toContain("QualitySettings.shadowDistance");
    expect(commandSource).toContain("QualitySettings.lodBias");
    expect(commandSource).toContain("if (!EditorApplication.isPlaying)");
    expect(commandSource).toContain("QualityNamesMaxChars");
    expect(commandSource).toContain('\\"quality_names_truncated\\"');
    expect(commandSource).toContain('\\"name_truncated\\"');
    expect(commandSource).toContain('\\"source\\":\\"UnityEngine.QualitySettings\\"');
    expect(commandSource).not.toContain("QualitySettings.SetQualityLevel");
    expect(commandSource).not.toContain("editor.execute_csharp");
    expect(commandSource).not.toContain("File.");
  });

  it("keeps runtime capture feature-gated and Play Mode scoped", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "runtime_state_debugging"');
    expect(source).toContain('RequiresEditorStatus = new string[] { "playing", "playing_paused" }');
    expect(source).toContain("runtime.capture_state requires Play Mode");
    expect(source).toContain("runtime.state_machine.run requires Play Mode");
  });

  it("bounds snapshot output and filters scene objects", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");

    expect(source).toContain('\\"maximum\\":500');
    expect(source).toContain("BuildObjectsJson(context.ArgsJson, 100, 500");
    expect(source).toContain("Resources.FindObjectsOfTypeAll<GameObject>()");
    expect(source).toContain("gameObject.scene.IsValid() && gameObject.scene.isLoaded");
  });

  it("captures compact transform and component state only", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");

    expect(source).toContain("AppendTransform");
    expect(source).toContain("AppendComponents");
    expect(source).toContain("Multi-frame runtime.state_machine.run requires paused Play Mode");
    expect(source).toContain("max_frames");
    expect(source).toContain("sample_every_frames");
    expect(source).toContain("StepPausedFrames");
    expect(source).not.toContain("editor.execute_csharp");
  });

  it("tracks bounded runtime event capture sessions", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");

    expect(source).toContain("s_eventCaptures");
    expect(source).toContain("Guid.NewGuid().ToString");
    expect(source).toContain("EventCapturePlanJson");
    expect(source).toContain("runtime.event_capture.start requires Play Mode");
    expect(source).toContain('EventCaptureArtifactRoot = JobArtifactRoot + "runtime-event-captures/"');
    expect(source).toContain("WriteEventCaptureArtifact");
    expect(source).toContain("NormalizeEventCaptureArtifactPath");
  });

  it("adds cooperative event capture cancellation without promising main-thread interruption", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");
    const commandSource = source.slice(source.indexOf('"runtime.event_capture.cancel"'), source.indexOf('[McpCommand(\n            "runtime.event_capture.read"'));

    expect(source).toContain("EventCaptureCancelSchemaJson");
    expect(commandSource).toContain('Risk = "write"');
    expect(commandSource).toContain('Idempotency = "idempotent"');
    expect(commandSource).toContain('Tags = new string[] { "runtime", "event-capture", "cancel", "recovery" }');
    expect(commandSource).toContain('ResultShape = "status"');
    expect(commandSource).toContain('reason must be at most 240 characters');
    expect(commandSource).toContain("s_eventCaptures.Remove(captureId)");
    expect(commandSource).toContain('\\"status\\":\\"not_found\\"');
    expect(commandSource).toContain('\\"status\\":\\"cancelled\\"');
    expect(commandSource).toContain('\\"cancellation_mode\\":\\"cooperative_session_cleanup\\"');
    expect(commandSource).toContain('\\"unity_main_thread_interrupted\\":false');
    expect(commandSource).toContain("WriteEventCaptureArtifact(artifactPath, resultJson)");
    expect(commandSource).toContain('\\"next_valid_actions\\":[\\"runtime.event_capture.start\\",\\"runtime.event_capture.read\\",\\"artifact.list\\",\\"artifact.prune\\"]');
    expect(commandSource).not.toContain("EditorApplication.ExitPlaymode");
    expect(commandSource).not.toContain("EditorApplication.isPaused =");
    expect(commandSource).not.toContain("Thread.Abort");
  });

  it("uses filesystem-bound containment and reparse-point checks for event capture artifacts", () => {
    const source = fs.readFileSync(runtimeCommandsPath, "utf8");

    expect(source).toContain("RequireProjectLocalFilePath(relativePath, EventCaptureArtifactRoot)");
    expect(source).toContain("Path.GetFullPath(Path.Combine(projectRoot");
    expect(source).toContain("IsSameOrChildPath(fullPath, fullRoot)");
    expect(source).toContain("FileAttributes.ReparsePoint");
    expect(source).toContain('normalized.StartsWith("/", StringComparison.Ordinal)');
    expect(source).toContain("IsDriveAbsolute(normalized)");
    expect(source).not.toContain("TrimStart('/')");
  });

  it("documents and policy-gates runtime event capture cancellation", () => {
    const commandsDoc = fs.readFileSync(commandsDocPath, "utf8");
    const generatedDoc = fs.readFileSync(generatedCommandsDocPath, "utf8");
    const policy = JSON.parse(fs.readFileSync(policyExamplePath, "utf8"));
    const profiles = fs.readFileSync(policyProfilesPath, "utf8");

    expect(commandsDoc).toContain("runtime.event_capture.cancel");
    expect(commandsDoc).toContain("cooperative session cleanup");
    expect(generatedDoc).toContain("`runtime.event_capture.cancel`");
    expect(policy.projects.game.ask).toContain("runtime.event_capture.cancel");
    expect(policy.projects.game.allow).toContain("runtime.quality_settings.inspect");
    expect(profiles).toContain('"runtime.event_capture.cancel"');
    expect(profiles).toContain('"runtime.quality_settings.inspect"');
  });
});
