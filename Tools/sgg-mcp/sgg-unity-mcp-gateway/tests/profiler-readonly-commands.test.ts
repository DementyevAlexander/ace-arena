import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const profilerCommandsPath = path.join(packageRoot, "Editor", "Commands", "ProfilerReadonlyCommands.cs");

describe("Unity profiler read-only command source-contract", () => {
  it("declares profiler read-only commands through the catalog", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain('"profiler.status"');
    expect(source).toContain('"profiler.modules"');
    expect(source).toContain('"profiler.frame.summary"');
    expect(source).toContain('"profiler.frame.markers"');
    expect(source).toContain('"profiler.marker.search"');
    expect(source).toContain('"profiler.report.create"');
    expect(source).toContain('"profiler.report.read"');
    expect(source).toContain('"profiler.memory.snapshot.compare"');
    expect(source).toContain('"profiler.memory.snapshot.list"');
    expect(source).toContain('"profiler.session.list"');
    expect(source).toContain('"profiler.session.summary"');
  });

  it("keeps profiler commands read-only, safe, and feature-gated", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain('const string FeatureFlag = "profiler_readonly"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(11);
    expect(source.match(/Idempotency = "safe"/g)).toHaveLength(11);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(11);
  });

  it("uses reflection with graceful availability output", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain('ResolveType("UnityEditorInternal.ProfilerDriver")');
    expect(source).toContain('ResolveType("UnityEditorInternal.ProfilerFrameDataIterator")');
    expect(source).toContain('\\"api_available\\"');
    expect(source).toContain('"No known read-only profiler module listing API was detected"');
    expect(source).toContain('"The required Unity Profiler frame iterator member shape is unavailable"');
    expect(source).toContain('"maximum\\":100');
    expect(source).toContain('"max_threads"');
    expect(source).toContain('"max_markers"');
    expect(source).toContain('"maximum\\":16');
    expect(source).toContain('"maximum\\":200');
    expect(source).toContain('ProfilerFrameReadPolicy.ResultState');
    expect(source).toContain('TryResolveProfilerFrame');
    expect(source).toContain('ReadProfilerFrameMarkers');
    expect(source).toContain('"maximum\\":1000');
    expect(source).toContain('"max_results"');
    expect(source).toContain('"required\\":[\\"query\\"]');
  });

  it("binds the current iterator API and never fabricates empty frame evidence", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain('GetMethod("GetThreadCount"');
    expect(source).toContain('new Type[] { typeof(int) }');
    expect(source).toContain('GetMethod("GetThreadName"');
    expect(source).toContain('Type.EmptyTypes');
    expect(source).toContain('GetProperty("name"');
    expect(source).toContain('GetProperty("durationMS"');
    expect(source).toContain('GetProperty("depth"');
    expect(source).toContain('\\"frame_evidence\\"');
    expect(source).toContain('\\"result_state\\"');
    expect(source).toContain('\\"data_available\\"');
    expect(source).toContain('\\"range_available\\"');
    expect(source).toContain('\\"thread_count_available\\"');
    expect(source).not.toContain('ReadInstanceInt(iteratorType, iterator, "threadCount", 0)');
    expect(source).not.toContain('ReadInstanceString(iteratorType, iterator, "GetName")');
    expect(source).not.toContain('ReadInstanceFloatString(iteratorType, iterator, "GetTotalTimeMS")');
  });

  it("adds bounded marker search and artifact-backed reports", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain('const int MaxCachedProfilerReports = 16');
    expect(source).toContain('const int ProfilerReportTtlMinutes = 30');
    expect(source).toContain('ProfilerReportArtifactRoot = JobArtifactRoot + "profiler-reports/"');
    expect(source).toContain('"storage\\":\\"process_memory_and_project_artifact\\"');
    expect(source).toContain('"artifact_root\\":\\"Library/SGGUnityMcp/job-artifacts/profiler-reports\\"');
    expect(source).toContain('\\"artifact_path\\"');
    expect(source).toContain('\\"artifact_refs\\"');
    expect(source).toContain('\\"readable_with\\":\\"job.artifact.read\\"');
    expect(source).toContain('"profiler.report.read"');
    expect(source).toContain('SearchProfilerMarkersJson');
    expect(source).toContain('BuildProfilerReportMarkdown');
    expect(source).toContain('TryWriteProfilerReportArtifact');
    expect(source).toContain('TryReadProfilerReportArtifact');
    expect(source).toContain('RequireProjectLocalFilePath(artifactPath, ProfilerReportArtifactRoot)');
    expect(source).toContain('FindProfilerReport');
  });

  it("adds bounded compare for existing memory snapshot artifacts without capture APIs", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain('MemorySnapshotCompareSchemaJson');
    expect(source).toContain('left_artifact_path');
    expect(source).toContain('right_artifact_path');
    expect(source).toContain('MaxMemorySnapshotArtifactBytes = 4 * 1024 * 1024');
    expect(source).toContain('TryLoadMemorySnapshotArtifact');
    expect(source).toContain('CompareMemorySnapshotArtifacts');
    expect(source).toContain('TryWriteMemoryCompareArtifact');
    expect(source).toContain('ProfilerReportArtifactRoot + compareId + ".json"');
    expect(source).toContain('RequireProjectLocalFilePath(artifactPath, JobArtifactRoot)');
    expect(source).toContain('capture_created\\":false');
  });

  it("adds bounded memory snapshot candidate inventory without returning counter values", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain("MemorySnapshotListSchemaJson");
    expect(source).toContain("MemorySnapshotListOutputLimitJson");
    expect(source).toContain("MaxMemorySnapshotScanFiles = 5000");
    expect(source).toContain("AddMemorySnapshotCandidates");
    expect(source).toContain("ScanMemorySnapshotDirectory");
    expect(source).toContain("MemorySnapshotCandidate");
    expect(source).toContain("metric_names");
    expect(source).toContain("values_returned\\\":false");
    expect(source).toContain("TryExtractJsonNumber(content, field, out _)");
  });

  it("adds bounded profiler session list and summary over fixed artifact roots", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).toContain("SessionListSchemaJson");
    expect(source).toContain("SessionSummarySchemaJson");
    expect(source).toContain("SessionOutputLimitJson");
    expect(source).toContain('ProfilerCaptureArtifactRoot = JobArtifactRoot + "profiler-captures/"');
    expect(source).toContain("AddProfilerProcessMemorySessions");
    expect(source).toContain("AddProjectProfilerSessions");
    expect(source).toContain("ProfilerSessionDescriptor");
    expect(source).toContain("RequireProjectLocalDirectoryPath");
    expect(source).toContain("SafeProfilerSessionId");
    expect(source).toContain("capture_data_inline\\\":false");
    expect(source).toContain("recording_started\\\":false");
    expect(source).toContain("capture_created\\\":false");
  });

  it("does not start captures, create snapshots, accept arbitrary output paths, or use network/process APIs", () => {
    const source = fs.readFileSync(profilerCommandsPath, "utf8");

    expect(source).not.toContain("StartRecording");
    expect(source).not.toContain("StopRecording");
    expect(source).not.toContain("BeginSample");
    expect(source).not.toContain("SaveProfile");
    expect(source).not.toContain("LoadProfile");
    expect(source).not.toContain("MemoryProfiler");
    expect(source).not.toContain("FrameDebugger");
    expect(source).not.toContain("output_path");
    expect(source).not.toContain("HttpClient");
    expect(source).not.toContain("WebRequest");
    expect(source).not.toContain("Process.Start");
  });
});
