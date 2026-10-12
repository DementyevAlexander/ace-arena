import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const profilerRecordingCommandsPath = path.join(packageRoot, "Editor", "Commands", "ProfilerRecordingCommands.cs");
const gatewayExampleConfigPath = path.join(process.cwd(), "config", "gateway.example.json");
const policyExampleConfigPath = path.join(process.cwd(), "config", "policy.example.json");
const policyProfilesPath = path.join(process.cwd(), "src", "policy", "policy-profiles.ts");
const artifactCommandsPath = path.join(process.cwd(), "src", "mcp", "artifact-commands.ts");

describe("Unity profiler recording command source-contract", () => {
  it("declares explicit ask-gated recording and capture commands through the catalog", () => {
    const source = fs.readFileSync(profilerRecordingCommandsPath, "utf8");
    const config = JSON.parse(fs.readFileSync(gatewayExampleConfigPath, "utf8"));
    const policy = JSON.parse(fs.readFileSync(policyExampleConfigPath, "utf8"));
    const policyProfiles = fs.readFileSync(policyProfilesPath, "utf8");

    expect(source).toContain('"profiler.recording.start"');
    expect(source).toContain('"profiler.recording.stop"');
    expect(source).toContain('"profiler.capture.save"');
    expect(source).toContain('const string FeatureFlag = "profiler_recording"');
    expect(source.match(/Risk = "write"/g)).toHaveLength(3);
    expect(source.match(/Idempotency = "unsafe"/g)).toHaveLength(3);
    expect(source.match(/SupportsJob = true/g)).toHaveLength(3);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(3);
    expect(config.features.profiler_recording).toBe(false);
    expect(policy.projects.game.ask).toEqual(expect.arrayContaining(["profiler.recording.start", "profiler.recording.stop", "profiler.capture.save"]));
    expect(policyProfiles).toContain('"profiler-recording"');
    expect(policyProfiles).toContain('features: ["profiler_recording"]');
  });

  it("requires explicit confirmations and avoids implicit recording or arbitrary output paths", () => {
    const source = fs.readFileSync(profilerRecordingCommandsPath, "utf8");

    expect(source).toContain('confirm_recording');
    expect(source).toContain('confirm_capture_export');
    expect(source).toContain('var recordingStarted = false;');
    expect(source).toContain('recordingStarted = true;');
    expect(source).toContain('"required\\":[\\"confirm_recording\\"]');
    expect(source).toContain('"required\\":[\\"confirm_capture_export\\"]');
    expect(source).toContain('"implicit_recording\\":false');
    expect(source).toContain('"recording_started\\":false');
    expect(source).toContain('"recording_stopped\\":false');
    expect(source).toContain('"output_path_accepted\\":false');
    expect(source).not.toContain('\\"output_path\\":{\\"type\\"');
    expect(source).not.toContain("MemoryProfiler");
    expect(source).not.toContain("FrameDebugger");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
    expect(source).not.toContain("Process.Start");
  });

  it("writes capture exports only under generated project-local artifacts", () => {
    const source = fs.readFileSync(profilerRecordingCommandsPath, "utf8");
    const artifactCommands = fs.readFileSync(artifactCommandsPath, "utf8");

    expect(source).toContain('ProfilerCaptureArtifactRoot = JobArtifactRoot + "profiler-captures/"');
    expect(source).toContain('NewCaptureId()');
    expect(source).toContain('ProfilerCaptureArtifactRoot + captureId + ".data"');
    expect(source).toContain('ProfilerCaptureArtifactRoot + captureId + ".json"');
    expect(source).toContain('RequireProjectLocalFilePath(capturePath, ProfilerCaptureArtifactRoot)');
    expect(source).toContain('RequireProjectLocalFilePath(metadataPath, ProfilerCaptureArtifactRoot)');
    expect(source).toContain('\\"artifact_refs\\"');
    expect(source).toContain('\\"readable_with\\":\\"artifact.metadata\\"');
    expect(source).toContain('\\"metadata_readable_with\\":\\"job.artifact.read\\"');
    expect(artifactCommands).toContain('"profiler_capture"');
    expect(artifactCommands).toContain('Library/SGGUnityMcp/job-artifacts/profiler-captures/');
  });
});
