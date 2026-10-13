import fs from "node:fs";
import path from "node:path";

import { describe, expect, it } from "vitest";

const packageRoot = path.join(process.cwd(), "unity", "Packages", "com.sungeargames.unity-mcp");
const commandsPath = path.join(packageRoot, "Editor", "Commands", "BuildTestAutomationCommands.cs");
const builtInCommandsPath = path.join(packageRoot, "Editor", "Commands", "BuiltInCommands.cs");
const bridgeServerPath = path.join(packageRoot, "Editor", "Bridge", "BridgeServer.cs");
const projectPathsPath = path.join(packageRoot, "Editor", "Bridge", "ProjectPaths.cs");

describe("Unity build/test automation command source-contract", () => {
  it("keeps timeout and tracking-cleanup guidance read-first without changing cancel admission", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const actions = source.slice(source.indexOf("static List<string> TestRunNextValidActions"), source.indexOf("static string DirtyScenePreflightJson"));
    const timeout = actions.slice(actions.indexOf('else if (state.Status == "timeout")'), actions.indexOf('else if (state.ArtifactPublicationState'));
    expect(actions.indexOf('state.Status == "timeout"')).toBeGreaterThan(actions.indexOf('state.Status == "callback_lost_after_playmode_exit"'));
    expect(timeout).toContain('actions.Add("test.run.status")');
    expect(timeout).toContain('if (state.ArtifactPersisted) actions.Add("job.artifact.read")');
    expect(timeout).toContain('actions.Add("diagnostics.report_problem")');
    expect(timeout).toContain('actions.Add("do_not_repeat_test.run_until_execution_state_is_resolved")');
    expect(timeout).not.toContain('actions.Add("test.run.cancel")');
    expect(timeout).not.toContain('actions.Add("test.run")');
    const formatter = source.slice(source.indexOf("static string TestRunCancelResultJson"), source.indexOf("static bool TryReadTestRunArtifactIdentityForV3Transition"));
    expect(formatter).toContain("tracking cleanup does not prove that Unity Test Runner stopped");
    expect(formatter).toContain("Do not start another run until execution state is resolved.");
    expect(formatter).not.toContain('{ "test.run",');
    expect(formatter).not.toContain('"job.artifact.read", "test.run",');
    const descriptor = source.slice(source.indexOf('"test.run.cancel",'), source.indexOf("public static CommandExecutionResult TestRunCancel"));
    expect(descriptor).toContain('Risk = "write"');
    expect(descriptor).toContain('Idempotency = "idempotent"');
    const schemaLiteral = /const string TestRunCancelSchemaJson = ("(?:\\.|[^"\\])*");/.exec(source);
    expect(schemaLiteral).not.toBeNull();
    const schema = JSON.parse(JSON.parse(schemaLiteral![1]!));
    expect(schema.additionalProperties).toBe(false);
    expect(Object.keys(schema.properties)).toEqual(["operation_id", "run_id", "reason", "forget_artifact"]);
  });

  it("collects additive non-passing leaf evidence without changing execution or count semantics", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    expect(source).toContain("m_state.NonPassingEvidence.Capture(result);");
    expect(source).toContain("m_state.NonPassingEvidence.CaptureTree(result);");
    expect(source).toContain("state.NonPassingEvidence.AppendJson(builder, MaxTestRunArtifactChars);");
    expect(source).toContain("state.NonPassingEvidence.Restore(persistedContent);");
    const collector = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "TestRunNonPassingEvidence.cs"), "utf8");
    for (const forbidden of [".Execute(", "FinalizeTestRun(", "PersistTestRunSnapshot(", "TerminalCountsAuthoritative", "state.Success", "File.Write"]) {
      expect(collector).not.toContain(forbidden);
    }
    expect(source).toContain("const int CurrentTestRunArtifactSchemaVersion = 3;");
  });

  it("bounds non-passing collection, traversal, and escaped UTF-8 publication independently", () => {
    const collector = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "TestRunNonPassingEvidence.cs"), "utf8");
    for (const bound of ["MaxEntries = 64", "MaxNodes = 20000", "MaxDepth = 64", "MaxEvidenceBytes = 48000", "new UTF8Encoding(false, true)"]) {
      expect(collector).toContain(bound);
    }
    expect(collector.indexOf("if (m_keys.Contains(key)) return;")).toBeLessThan(collector.indexOf("if (m_entries.Count >= MaxEntries)"));
    expect(collector).toContain("maxArtifactBytes - Utf8.GetByteCount(builder.ToString()) - 1");
    expect(collector).toContain("if (Utf8.GetByteCount(suffix) <= remaining) builder.Append(suffix);");
    expect(collector).toContain("Replace(terminal);");
    expect(collector).toContain("fields.ContainsKey(key)");
  });

  it("declares bounded build and test automation command IDs", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain('"build.settings"');
    expect(source).toContain('"build.profile.list"');
    expect(source).toContain('"build.run"');
    expect(source).toContain('"build.run.status"');
    expect(source).toContain('"test.framework.status"');
    expect(source).toContain('"test.list"');
    expect(source).toContain('"test.run"');
    expect(source).toContain('"test.run.status"');
    expect(source).toContain('"test.run.batchmode_fallback"');
    expect(source).toContain('"test.run.cancel"');
    expect(source).toContain('const string FeatureFlag = "build_test_automation"');
    expect(source.match(/Risk = "read"/g)).toHaveLength(7);
    expect(source).toContain('Risk = "write"');
    expect(source).toContain('Risk = "execute"');
    expect(source.match(/Idempotency = "safe"/g)).toHaveLength(7);
    expect(source.match(/Idempotency = "unsafe"/g)).toHaveLength(2);
    expect(source.match(/FeatureFlag = FeatureFlag/g)).toHaveLength(10);
  });

  it("descriptor-backs explicit compact test and compile responses", () => {
    const tests = fs.readFileSync(commandsPath, "utf8");
    const builtIns = fs.readFileSync(builtInCommandsPath, "utf8");

    for (const schema of ["TestListSchemaJson", "TestRunSchemaJson", "TestRunStatusSchemaJson"]) {
      expect(tests.slice(tests.indexOf(`const string ${schema}`), tests.indexOf(";", tests.indexOf(`const string ${schema}`)) + 1)).toContain('\\"response_mode\\"');
    }
    for (const schema of ["CompileStatusSchemaJson", "CompileWaitReadySchemaJson", "CompileRefreshAndWaitSchemaJson"]) {
      expect(builtIns.slice(builtIns.indexOf(`const string ${schema}`), builtIns.indexOf(";", builtIns.indexOf(`const string ${schema}`)) + 1)).toContain('\\"response_mode\\"');
    }
    expect(tests).toContain('\\"enum\\":[\\"full\\",\\"compact\\"]');
    expect(builtIns).toContain('\\"enum\\":[\\"full\\",\\"compact\\"]');
  });

  it("bounds build settings schema and output", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("BuildSettingsSchemaJson");
    expect(source).toContain('\\"maximum\\":200');
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 200)');
    expect(source).toContain('\\"total_scene_count\\"');
    expect(source).toContain('\\"enabled_scene_count\\"');
    expect(source).toContain('\\"truncated\\"');
    expect(source).toContain("OutputLimitJson = BuildSettingsOutputLimitJson");
  });

  it("runs builds without switching targets", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("BuildPipeline.BuildPlayer");
    expect(source).toContain("BuildPlayerOptions");
    expect(source).not.toContain("SwitchActiveBuildTarget");
  });

  it("lists Unity Build Profiles by reflection without switching targets or building", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("BuildProfileListSchemaJson");
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 100)');
    expect(source).toContain('"UnityEditor.Build.Profile.BuildProfile"');
    expect(source).toContain('AssetDatabase.FindAssets("t:" + profileType.Name)');
    expect(source).toContain("AssetDatabase.LoadMainAssetAtPath(assetPath)");
    expect(source).toContain("ReflectedValue(asset, \"buildTarget\", \"target\")");
    expect(source).toContain('builder.Append("\\\"api_available\\\":")');
    expect(source).toContain('builder.Append("\\\"profiles\\\":[")');
  });

  it("adds build.run as a job-capable confirmed-output build command with bounded artifacts", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("BuildRunSchemaJson");
    expect(source).toContain('SupportsJob = true');
    expect(source).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(source).toContain('TimeoutMs = 10000');
    expect(source).toContain('BuildOutputRoot = "BuildArtifacts/SGGUnityMcp"');
    expect(source).toContain('\\"output_root\\"');
    expect(source).toContain('\\"pattern\\":\\".*\\\\\\\\S.*\\"');
    expect(source).toContain('TryNormalizeExternalBuildOutputRoot(requestedOutputRoot');
    expect(source).toContain('outputSelection = "caller_absolute_root"');
    expect(source).toContain('Caller-selected output_root must be outside and must not contain the Unity project');
    expect(source).toContain('UNC and device paths are not allowed for output_root');
    expect(source).toContain('output_root or an existing parent contains a symlink or reparse point');
    expect(source).toContain('Caller-selected output_root is currently supported only by the Windows Editor');
    expect(source).toContain('Caller-selected output_root is unavailable when the Unity project uses a SUBST, mapped, network or 8.3 alias path');
    expect(source).toContain('IsSafePersistedDefaultBuildOutputPath');
    expect(source).toContain('.sgg-unity-mcp-build-reservation');
    expect(source).toContain('FileShare.Read');
    expect(source).toContain('BuildOutputPath(operationId, activeTarget, buildName, outputRoot');
    expect(source).toContain('BuildArtifactRoot = "Library/SGGUnityMcp/job-artifacts/build-runs"');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false)');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "allow_unsaved_scene_changes", false)');
    expect(source).toContain('BuiltInCommands.RequireNoUnsavedSceneChanges(context.State, "build.run", "build.run")');
    expect(source).toContain('CommandExecutionResult.Error("compilation_failed"');
    expect(source).toContain('fix compile.errors.recent before starting build.run');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "development", false)');
    expect(source).toContain('BuildOptions.Development');
    expect(source).toContain('TryWriteArtifactAtomic(artifactPath, runningJson');
    expect(source).toContain('MainThreadDispatcher.Enqueue(() => ExecuteBuildRun(state))');
    expect(source).not.toContain('EditorApplication.delayCall += () => ExecuteBuildRun(state)');
    expect(source).toContain('BuildPipeline.BuildPlayer');
    expect(source).toContain('"build_run_operation_correlated"');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "_build_run_operation_id")');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "_build_run_caller_args_hash")');
    expect(source).toContain('BuildRunStatusSchemaJson');
    expect(source).toContain('ReadPersistedBuildRunArtifact(operationId, artifactPath, out var identityMismatch, out var readError)');
    expect(source).toContain('TryResolveBuildArtifactPath(artifactPath');
    expect(source).toContain('stream.Flush(true)');
    expect(source).toContain('HasExistingReparsePointBelowRoot');
    expect(source).toContain('Reserved build output destination already exists');
    expect(source).toContain('\\"build_output_path\\"');
    expect(source).toContain('\\"build_output_root\\"');
    expect(source).toContain('\\"output_selection\\"');
    expect(source).toContain('\\"artifact_path\\"');
    expect(source).toContain('\\"artifact_ref\\"');
    expect(source).toContain('\\"artifact_refs\\"');
    expect(source).toContain('\\"readable_with\\":\\"job.artifact.read\\"');
    expect(source).toContain('BuildFailureSummaryJson');
    expect(source).toContain('BuildRunNextValidActions');
    expect(source).toContain('Sha256Prefixed(context.ArgsJson)');
    expect(source).toContain('\\"command\\":\\"build.run\\"');
    expect(source).toContain('\\"package_version\\"');
    expect(source).toContain('\\"unity_version\\"');
    expect(source).toContain('\\"created_at\\"');
    expect(source).toContain('\\"build_target\\"');
    expect(source).toContain('\\"scenes_truncated\\"');
  });

  it("normalizes fresh and persisted build receipts with one authoritative consistency classifier", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const executeBuild = source.slice(source.indexOf("static void ExecuteBuildRun"), source.indexOf("static void CompleteBuildRunFailure"));
    const persisted = source.slice(source.indexOf("internal static string NormalizePersistedBuildRunArtifactForRead"), source.indexOf("static bool IsSafePersistedDefaultBuildOutputPath"));

    expect(source).toContain("internal static BuildRunReceiptClassification ClassifyBuildRunReceipt");
    expect(executeBuild).toContain("ApplyBuildRunReceiptClassification(state)");
    expect(executeBuild).toContain("state.TotalErrors = summary.totalErrors");
    expect(executeBuild).toContain("state.TerminalEvidenceAuthoritative = true");
    expect(executeBuild).not.toContain('state.Result = "output_missing"');
    expect((executeBuild.match(/BuildPipeline\.BuildPlayer/g) ?? [])).toHaveLength(1);
    expect(persisted).toContain("ClassifyBuildRunReceipt(status, terminalEvidenceAuthoritative, result, totalErrors, outputExists)");
    expect(persisted).toContain('normalizedSuccess = status == "completed" && persistedSuccessClaim && classification.Success');
    expect(persisted).toContain('builder.Append("\\"persisted_success_claim\\":")');
    expect(persisted).toContain("BuildFailureSummaryJson(normalizedSuccess");
    expect(persisted).toContain("durationMs, false, null, false, classification.ReceiptConsistency");
    expect(persisted).not.toContain("ConsoleLogBuffer.HasCompileErrors()");
    expect(persisted).toContain("BuildRunNextValidActions(false, normalizedSuccess");
    expect(source).toContain('builder.Append("\\"receipt_consistency\\":")');
    expect(source).toContain('builder.Append("\\"receipt_consistency_conflict\\":")');
    expect(source).toContain('builder.Append("\\"receipt_conflicts\\":")');
    expect(source).not.toContain("Directory.Delete(state.OutputPath");
    expect(source).not.toContain("File.Delete(state.OutputPath");
  });

  it("uses the authoritative reflection-only TestRunner catalog without running tests", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const catalog = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "UnityTestDiscoveryCatalog.cs"), "utf8");

    expect(source).toContain("TestListSchemaJson");
    expect(source).toContain('\\\"mode\\\":{\\\"type\\\":\\\"string\\\",\\\"enum\\\":[\\\"editmode\\\",\\\"playmode\\\"]}');
    expect(source).toContain('\\\"assembly\\\":{\\\"type\\\":\\\"string\\\"');
    expect(source).toContain('\\\"cursor\\\":{\\\"type\\\":\\\"integer\\\",\\\"minimum\\\":0}');
    expect(source).toContain('Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 500)');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "mode")');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "assembly")');
    expect(source).toContain('JsonUtil.ExtractInt(context.ArgsJson, "cursor", 0)');
    expect(source).toContain("UnityTestDiscoveryCatalog.Query");
    expect(source).toContain("resolution_status");
    expect(source).toContain('builder.Append(",\\"discovery_generation\\":")');
    expect(source).toContain("next_cursor");
    expect(source).toContain("CatalogDiagnosticHints");
    expect(source).toContain("DiscoverTestsLegacyBestEffort");
    expect(source).toContain("legacy_reflection_preview");
    expect(source).not.toContain("DiscoverTestsForRunPreview");
    expect(source).not.toContain("UseRawTestFilter");
    expect(catalog).toContain('FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi")');
    expect(catalog).toContain('FindType("UnityEditor.TestTools.TestRunner.Api.TestMode")');
    expect(catalog).toContain('FindType("UnityEditor.TestTools.TestRunner.Api.ITestAdaptor")');
    expect(catalog).toContain("MakeGenericMethod(adaptorType)");
    expect(catalog).toContain("RetrieveTestList");
    expect(catalog).toContain("MaxNodes = 100000");
    expect(catalog).toContain("MaxDepth = 64");
    expect(catalog).toContain("StatusPending");
    expect(catalog).toContain("IsTestAssembly");
    expect(catalog).toContain("UniqueName");
    expect(catalog).toContain("IsRunnable");
    expect(catalog).toContain("Candidates.Sort(TestCandidate.Compare)");
    expect(catalog).toContain("DiscoveryGeneration");
    expect(source).not.toContain("TestRunnerApi.Execute");
  });

  it("adds test.run as a non-blocking reflected test runner operation with bounded artifacts", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const testRunDescriptor = source.slice(source.indexOf('[McpCommand(\n            "test.run"'), source.indexOf("public static CommandExecutionResult TestRun"));

    expect(source).toContain("TestRunSchemaJson");
    expect(testRunDescriptor).toContain("TimeoutMs = 60000");
    expect(testRunDescriptor).toContain("60 seconds for admission/start including durable owner publication");
    expect(source).toContain('const string TestRunWrapperWaitJson = "{\\"mode\\":\\"wait\\",\\"wait_for_test_run_terminal_ms\\":{\\"min_ms\\":1,\\"max_ms\\":295000,\\"compile_readiness_contract\\":\\"sgg.unity_mcp.test_run_compile_readiness.v1\\",\\"deadline_scope\\":\\"start_and_terminal_poll\\",\\"response_mode\\":\\"compact\\",\\"dry_run\\":false}}"');
    expect(source).toContain("WrapperWaitJson = TestRunWrapperWaitJson");
    expect(source).toContain("TestRunStatusSchemaJson");
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "_test_run_operation_id")');
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "_test_run_caller_args_hash")');
    expect(source).toContain(String.raw`\"anyOf\":[{\"required\":[\"operation_id\"]},{\"required\":[\"run_id\"]}]`);
    expect(source).toContain('"test_run_operation_correlated"');
    expect(source.match(/RequireSettledCompilationForTestRun\(context\.State\)/g)).toHaveLength(2);
    expect(source).toContain('"compile_lifecycle_settling"');
    expect(source).toContain(String.raw`\"test_runner_execute_invoked\":false`);
    expect(source).toContain(String.raw`\"write_replay_allowed\":false`);
    expect(source).toContain("TestRunCancelSchemaJson");
    expect(source).toContain('Risk = "execute"');
    expect(source.match(/SupportsJob = true/g)).toHaveLength(1);
    expect(source).toContain('RequiresEditorStatus = new string[] { "editing" }');
    expect(source).toContain('ResultShape = "operation_status"');
    expect(source).toContain('TestRunArtifactRoot = "Library/SGGUnityMcp/job-artifacts/test-runs"');
    expect(source).toContain('TryWriteFixedRootArtifactAtomic(artifactPath, content, TestRunArtifactRoot, "Test run artifact"');
    expect(source).not.toContain("File.WriteAllText(fullPath, content)");
    expect(source).toContain('MaxTestRunMatchedTests = 32');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false)');
    expect(source).toContain('JsonUtil.ExtractBool(context.ArgsJson, "allow_unsaved_scene_changes", false)');
    expect(source).toContain("AllowUnsavedSceneChanges = allowUnsavedSceneChanges");
    expect(source).toContain('RequireCleanScenesForTestRun(context.State, allowUnsavedSceneChanges, "admission")');
    expect(source).toContain('RequireCleanScenesForTestRun(context.State, allowUnsavedSceneChanges, "pre_execute")');
    expect(source).toContain('static readonly Dictionary<string, TestRunState> s_testRuns');
    expect(source).toContain('FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi")');
    expect(source).toContain('FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings")');
    expect(source).toContain('SetRequiredMember(filterInstance, "testNames", testNames.ToArray())');
    expect(source).toContain('SetRequiredMember(filterInstance, "assemblyNames", new string[] { testAssembly })');
    expect(source).toContain("MemberValuesEqual");
    expect(source).toContain("throw new MissingMemberException(type.FullName, name)");
    expect(source).toContain('JsonUtil.ExtractString(context.ArgsJson, "assembly")');
    expect(source).toContain('\\"test_assembly\\"');
    expect(source).toContain("List<UnityTestDiscoveryCatalog.TestCandidate> candidates, int discoveryGeneration = 0");
    expect(source).toContain("resolution == null ? 0 : resolution.DiscoveryGeneration");
    expect(source).toContain('TestRunResolvedFilterMatchingMode = "unity_test_runner_filter.authoritative_catalog_exact_test_names"');
    expect(source).toContain('TestRunUnityModeOnlyMatchingMode = "unity_test_runner_filter.testMode_only"');
    expect(source).toContain('TestRunReflectionMatchingMode = "reflection_case_insensitive_substring_assembly_type_method_with_best_effort_mode"');
    expect(source).toContain('TestRunReflectionOmittedMatchingMode = "reflection_preview_omitted_test_mode_only"');
    expect(source).toContain('UnityTestDiscoveryCatalog.Resolve(mode, filter, testAssembly)');
    expect(source).toContain('JsonUtil.ExtractInt(context.ArgsJson, "_test_discovery_generation", 0)');
    expect(source).toContain('"catalog_stale"');
    expect(source).toContain('authoritative TestRunner discovery generation changed before Execute');
    expect(source).toContain('CaptureAuthoritativeMatchedTests(dryRunState, filterResolution)');
    expect(source).toContain('CaptureAuthoritativeMatchedTests(state, filterResolution)');
    expect(source).toContain('TestFilterResolutionError(filterResolution)');
    expect(source).toContain('"test_filter_resolution_pending"');
    expect(source).toContain('"test_filter_resolution_unavailable"');
    expect(source).toContain('"test_filter_unresolved"');
    expect(source).toContain('"test_filter_ambiguous"');
    expect(source).not.toContain('UseRawTestFilter');
    expect(source).not.toContain('DiscoverTestsForRunPreview');
    expect(source).toContain('state.UnityTestNameFilters');
    expect(source).toContain('PersistTestRunSnapshot(state, "created")');
    expect(source).toContain('var reservedRunId = JsonUtil.ExtractString(context.ArgsJson, "_test_run_operation_id").Trim();');
    expect(source).toContain("TestRunOperationExists(runId, artifactPath)");
    expect(source).toContain("ArgsHash = effectiveArgsHash");
    expect(source).toContain("CallerArgsHash = resolvedCallerArgsHash");
    expect(source).toContain('builder.Append("\\\"caller_args_hash\\\":")');
    expect(source).toContain("TestRunIdsMatch(operationId, runIdAlias)");
    expect(source).toContain("PersistedTestRunArtifactIdentityMatches(content, runId)");
    expect(source).toContain("artifactIdentityMismatch");
    expect(source).toContain('s_testRuns.ContainsKey(runId)');
    expect(source).toContain('File.Exists(AbsoluteProjectPath(artifactPath))');
    expect(source).toContain('InvokeReflected(api, "RegisterCallbacks", callbacks)');
    expect(source).toContain('PersistTestRunSnapshot(state, "callbacks_registered")');
    expect(source).toContain('PersistTestRunSnapshot(state, "execute_dispatching")');
    expect(source).toContain('InvokeReflected(api, "Execute", settings)');
    expect(source).toContain('PersistTestRunSnapshot(state, "execute_invoked")');
    expect(source).toContain('EditorApplication.update += UpdateTestRuns');
    expect(source).toContain('WriteArtifact(state.ArtifactPath, TestRunStateJson(state))');
    expect(source).toContain('builder.Append("\\\"max_seconds\\\":")');
    expect(source).toContain('CaptureUnsavedSceneWarning(context.State, state)');
    expect(source).toContain('\\"unsaved_scene_changes\\"');
    expect(source).toContain('\\"has_unsaved_scene_changes\\"');
    expect(source).toContain('\\"dirty_scene_count\\"');
    expect(source).toContain('BuiltInCommands.DirtyScenesJson(state.DirtyScenesAtStart)');
    expect(source).toContain('MaxFailedTestDetails = 16');
    expect(source).toContain('\\\"filter_matching_mode\\\"');
    expect(source).toContain('\\\"matched_tests_matching_mode\\\"');
    expect(source).toContain('\\\"matched_count\\\"');
    expect(source).toContain('\\\"matched_count_reliable\\\"');
    expect(source).toContain('\\\"matched_count_status\\\"');
    expect(source).toContain('\\\"matched_tests_coverage\\\"');
    expect(source).toContain('\\\"matched_tests_truncated\\\"');
    expect(source).toContain('\\\"matched_tests_scan\\\"');
    expect(source).toContain('\\\"omitted\\\"');
    expect(source).toContain('\\\"omitted_reason\\\"');
    expect(source).toContain('\\\"matched_tests\\\"');
    expect(source).toContain('\\\"runnable_filters\\\"');
    expect(source).toContain('\\\"filter_contract\\\"');
    expect(source).toContain('\\\"unity_test_names\\\"');
    expect(source).toContain('filter_contract_warning');
    expect(source).toContain('AppendTestRunEvidenceSplit(builder, state)');
    expect(source).toContain('\\"filter_preview\\"');
    expect(source).toContain('\\"execution_result\\"');
    expect(source).toContain('\\"supersedes_preview_absence\\"');
    expect(source).toContain('executed_results_are_authoritative_for_this_completed_run');
    expect(source).toContain('Executed TestRunner results show total > 0; do not treat reflection preview matched_count=0 as zero coverage');
    expect(source).toContain('\\\"filter_diagnostic_hints\\\"');
    expect(source).toContain('CaptureFailedTestResult(m_state, result)');
    expect(source).toContain('CaptureFailedTestResultTree(m_state, result)');
    expect(source).toContain('ReflectedLong(result, "PassCount")');
    expect(source).toContain('ReflectedLong(result, "FailCount")');
    expect(source).toContain('ReflectedLong(result, "SkipCount")');
    expect(source).toContain('ReflectedLong(result, "InconclusiveCount")');
    expect(source).toContain('ReflectedLong(result, "AssertCount")');
    expect(source).toContain('ReflectedLong(result, "TestCaseCount")');
    expect(source).toContain('TryApplyTerminalTestCounts(m_state, passCount, failCount, skipCount, inconclusiveCount, assertCount, testCaseCount)');
    expect(source).toContain('\\"pass_count\\"');
    expect(source).toContain('\\"fail_count\\"');
    expect(source).toContain('\\"skip_count\\"');
    expect(source).toContain('\\"inconclusive_count\\"');
    expect(source).toContain('\\"authoritative_leaf_total\\"');
    expect(source).toContain('\\"assert_count\\"');
    expect(source).toContain('not_a_test_count');
    expect(source).toContain('\\"test_case_count\\"');
    expect(source).toContain('unity_test_runner_root.TestCaseCount');
    expect(source).toContain('\\"count_consistency\\"');
    expect(source).toContain('state.TerminalCountsAuthoritative');
    expect(source).toContain('"terminal_counts_unavailable"');
    expect(source).toContain('\\"failed_tests\\"');
    expect(source).toContain('\\"failed_tests_truncated\\"');
    expect(source).toContain('\\"artifact_ref\\"');
    expect(source).toContain('\\"artifact_refs\\"');
    expect(source).toContain('TestFailureSummaryJson');
    expect(source).toContain('TestRunNextValidActions');
    expect(source).toContain('DirtyScenePreflightJson');
    expect(source).toContain('state != null && state.AllowUnsavedSceneChanges');
    expect(source).toContain('\\"allow_unsaved_scene_changes\\"');
    expect(source).toContain('\\"command\\":\\"test.run\\"');
    expect(source).toContain('\\"args_hash\\"');
    expect(source).toContain('\\"test_mode\\"');
    expect(source).toContain('\\"test_filter\\"');
    expect(source).toContain('"unity_test_framework_unavailable"');
    expect(source).toContain('"unity_test_run_busy"');
    expect(source).toContain('\\"operation_created\\":false');
    expect(source).toContain('\\"active_operation_id\\":');
    expect(source).toContain('"timeout"');
    expect(source).toContain('\\"artifact_path\\"');
    expect(source).toContain('\\"artifact_schema_version\\"');
    expect(source).toContain('const int MinimumAuthoritativeTestRunArtifactSchemaVersion = 2');
    expect(source).toContain('const int CurrentTestRunArtifactSchemaVersion = 3');
    expect(source).toContain('const int OwnerBindingFallbackTestRunArtifactSchemaVersion = 3');
    expect(source).toContain('\\"terminal_counts_authoritative\\"');
    expect(source).toContain('\\"terminal_count_source\\"');
    expect(source).toContain('\\"artifact_persisted\\"');
    expect(source).toContain('\\"cancel_requested\\"');
    expect(source).toContain('\\"cleanup_performed\\"');
    expect(source).toContain('\\"forget_artifact\\"');
    expect(source).toContain('\\"started_at\\"');
    expect(source).toContain('\\"last_update_at\\"');
    expect(source).toContain('\\"lifecycle\\"');
    expect(source).toContain("TestRunCancel(McpCommandContext context)");
    expect(source).toContain("CancelOrCleanupTestRunState(state, reason)");
    expect(source).toContain("if (invalidatePlayModeDiscovery || activeBeforeCancel) UnityTestDiscoveryCatalog.InvalidateAfterPlayModeRun()");
    expect(source).not.toContain("CancelledUnverifiedTestRunSessionStateKey");
    expect(source).toContain("TryReadTestRunArtifactIdentityForV3Transition(runId, artifactPath");
    expect(source).toContain("TestRunCancelledAfterReloadJson(runId, artifactPath, reason, argsHash, callerArgsHash)");
    expect(source).toContain("TryDeleteTestRunArtifact(artifactPath)");
    expect(source).toContain("System.Threading.Thread.Sleep(WindowsArtifactCommitRetryDelayMs)");
    expect(source).not.toContain("using System.Threading");
    expect(source).not.toContain("while (!state.Finished");
    const callbackSource = source.slice(source.indexOf("public class TestRunCallbacksProxy"));
    expect(callbackSource).toContain('ReflectedLong(result, "AssertCount")');
    expect(callbackSource).toContain('ReflectedLong(result, "TestCaseCount")');
    const authorityValidator = source.slice(source.indexOf("internal static bool TryValidateTerminalTestCounts"), source.indexOf("internal static bool TryApplyTerminalTestCounts"));
    expect(authorityValidator).not.toContain("AssertCount");
    expect(authorityValidator).not.toContain("TestCaseCount");
  });

  it("publishes TestRunner lifecycle receipts with Windows-safe atomic replacement and bounded post-dispatch retries", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const testRun = source.slice(source.indexOf("public static CommandExecutionResult TestRun"), source.indexOf('static CommandExecutionResult TestRunBusy'));
    const dispatch = source.slice(source.indexOf("internal static CommandExecutionResult DispatchPreparedTestRun"), source.indexOf("internal static CommandExecutionResult RetainPreExecuteRefusal"));
    const finalizer = source.slice(source.indexOf("internal static void FinalizeTestRun"), source.indexOf("internal static void ClassifyResolvedTestRunOutcome"));

    expect(source).toContain('EntryPoint = "MoveFileExW"');
    expect(source).toContain("MoveFileReplaceExisting | MoveFileWriteThrough");
    expect(source).toContain("MaxWindowsArtifactCommitAttempts = 8");
    expect(source).toContain("WindowsArtifactCommitRetryDelayMs = 10");
    expect(source).toContain("IsTransientWindowsArtifactCommitError(errorCode)");
    expect(source).toContain("ArtifactPublicationDisposition.TransientContention");
    expect(source).toContain("publication.TransientContention");
    expect(source).toContain("MaxTestRunPublicationAttempts = 8");
    expect(source).toContain("TestRunPublicationRetryWindowSeconds = 8d");
    expect(source).toContain('state.ArtifactPublicationState = retry ? "pending" : "failed"');
    expect(source).toContain("RetryTestRunPublication(state)");
    expect(source).toContain("s_retainedTestRunPublications[state.RunId] = state");
    expect(source).toContain("StoreRetainedTestRunPublicationSession(state)");
    expect(source).toContain("RestoreRetainedTestRunPublicationsOnDomainLoad()");
    expect(source).toContain('PersistTestRunSnapshot(state, "artifact_publication_failed_pre_execute")');
    expect(testRun).toContain("var preparedResult = DispatchPreparedTestRun(state,");
    expect(testRun).toContain('() => InvokeReflected(api, "Execute", settings), out executeEntered)');
    expect(dispatch.indexOf("state.ExecuteDispatchCount = 1")).toBeGreaterThan(-1);
    expect(dispatch.indexOf("state.ExecuteDispatchCount = 1")).toBeLessThan(dispatch.indexOf('PersistTestRunSnapshot(state, "execute_dispatching")'));
    expect(dispatch.indexOf('PersistTestRunSnapshot(state, "execute_dispatching")')).toBeLessThan(dispatch.indexOf("executeEntered = true;"));
    expect(dispatch.indexOf("executeEntered = true;")).toBeLessThan(dispatch.indexOf("execute();"));
    expect(dispatch).toContain("state.ExecuteDispatchCount = 0");
    expect(testRun).toContain('state.Status = "execution_may_have_started"');
    expect(testRun).toContain('PersistTestRunSnapshot(state, "execute_invocation_uncertain")');
    expect(testRun).toContain('CommandExecutionResult.Error("unity_test_run_delivery_uncertain"');
    expect(finalizer).toContain("PersistTestRunSnapshot(state, state.Lifecycle)");
    expect(finalizer).not.toContain("WriteArtifact(");
  });

  it("retains only the published prepared operation at the final compilation refusal without deleting or executing it", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const dispatch = source.slice(source.indexOf("internal static CommandExecutionResult DispatchPreparedTestRun"), source.indexOf("internal static CommandExecutionResult RetainPreExecuteRefusal"));
    const finalGate = dispatch.slice(dispatch.indexOf("var refusal = finalLifecycleGate();"), dispatch.indexOf("state.ExecuteDispatchCount = 1;"));
    const retention = source.slice(source.indexOf("internal static CommandExecutionResult RetainPreExecuteRefusal"), source.indexOf("static string TestRunPreExecuteRefusalErrorDetailsJson"));
    expect(dispatch).toContain('state.LastPublishedLifecycle != "callbacks_registered" || !state.ArtifactPersisted');
    expect(dispatch).toContain("state.ExecuteDispatchCount != 0 || state.ExecuteCallReturned");
    expect(finalGate).toContain("return RetainPreExecuteRefusal(state, refusal);");
    for (const forbidden of ["TryDeleteTestRunArtifact", "s_testRuns.Remove", "ClearActiveTestRunSession", "execute();", "InvokeReflected("])
      expect(finalGate).not.toContain(forbidden);
    expect(retention).toContain("state.Finished = true;");
    expect(retention).toContain("state.Success = false;");
    expect(retention).toContain("state.TerminalCountsAuthoritative = false;");
    expect(retention.indexOf("state.Finished = true;")).toBeLessThan(retention.indexOf("FinalizeTestRun(state)"));
    expect(retention).toContain("CommandExecutionResult.Error(refusal.ErrorCode, refusal.ErrorMessage,");
    expect(retention).not.toContain("TryDeleteTestRunArtifact");
    expect(retention).not.toContain("s_testRuns.Remove");
    expect(retention).not.toContain("execute();");
    expect(retention).not.toContain("state.ExecuteDispatchCount = 0");
    expect(source).toContain('PreExecuteRefusedStatus = "pre_execute_refused"');
  });

  it("blocks every non-dry test.run on dirty scenes without honoring the legacy override", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const testRun = source.slice(source.indexOf("public static CommandExecutionResult TestRun"), source.indexOf('[McpCommand(\n            "test.run.status"'));
    const guard = source.slice(source.indexOf("static CommandExecutionResult RequireCleanScenesForTestRun"), source.indexOf("static long ArtifactSizeBytes"));

    expect(testRun).toContain("if (!dryRun)");
    expect(testRun).not.toContain("if (!dryRun && !allowUnsavedSceneChanges)");
    expect(testRun.match(/RequireCleanScenesForTestRun\(/g)).toHaveLength(2);
    const preExecuteGuardIndex = testRun.lastIndexOf("RequireCleanScenesForTestRun(");
    expect(preExecuteGuardIndex).toBeGreaterThan(testRun.indexOf("CaptureAuthoritativeMatchedTests(state, filterResolution)"));
    expect(preExecuteGuardIndex).toBeGreaterThan(testRun.indexOf("CaptureMatchedTests(state, ModeOnlyDiscovery(mode))"));
    expect(preExecuteGuardIndex).toBeLessThan(testRun.indexOf('InvokeReflected(api, "Execute", settings)'));
    expect(guard).toContain('"test_run_scene_state_unavailable"');
    expect(guard).toContain("runtimeState.UpdateFromEditor()");
    expect(guard).toContain('"test_run_dirty_scene_blocked"');
    expect(guard).toContain('allow_unsaved_scene_changes_honored\\\":false');
    expect(guard).toContain('test_runner_execute_invoked\\\":false');
    expect(guard).toContain('native_save_prompt_answered\\\":false');
    expect(guard).toContain('scene_state_mutated\\\":false');
    expect(guard).not.toContain("SaveCurrentModifiedScenesIfUserWantsTo");
    expect(guard).not.toContain("MarkSceneClean");
  });

  it("keeps pending and incomplete authoritative catalog results fail-closed", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const catalog = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "UnityTestDiscoveryCatalog.cs"), "utf8");
    expect(source).toContain("ModeOnlyDiscovery");
    expect(source).toContain("CaptureAuthoritativeMatchedTests");
    expect(source).toContain("TestFilterResolutionError");
    expect(catalog).toContain("CatalogQueryResult.Pending");
    expect(catalog).toContain("StatusIncomplete");
    expect(catalog).toContain("PendingTimeoutSeconds = 15");
    expect(catalog).toContain("retrieve_test_list_timeout");
    expect(catalog).toContain('Fail("max_nodes")');
    expect(catalog).toContain('Fail("children_missing")');
    expect(catalog).toContain('Fail("string_too_long")');
    expect(catalog).toContain('Fail("max_depth")');
    expect(source).toContain('builder.Append(",\\"resolution_status\\":")');
    expect(source).toContain('builder.Append(",\\"coverage_status\\":")');
    expect(source).toContain('builder.Append(",\\"next_valid_actions\\":")');
    expect(source).toContain('test_runner_execute_invoked\\":false');
  });

  it("uses catalog exact names for focused resolution instead of global type/method caps", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const catalog = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "UnityTestDiscoveryCatalog.cs"), "utf8");
    expect(source).toContain("UnityTestDiscoveryCatalog.Resolve");
    expect(source).toContain("filterResolution.ResolvedAssembly");
    expect(source).toContain("CreateExecutionSettings(settingsType, filterType, modeType, mode, state.UnityTestNameFilters, state.TestAssembly)");
    expect(source).not.toContain("reflection_preview_partial");
    expect(source).not.toContain("no_reflection_match");
    expect(catalog).toContain("ResolveExactCandidate");
    expect(catalog).toContain("multiple_fixtures_or_suites");
    expect(catalog).toContain("RecommendedFilter");
  });

  it("refuses exact suites without structural same-assembly selectable named leaves before TestRunner admission", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const catalog = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "UnityTestDiscoveryCatalog.cs"), "utf8");
    const exact = catalog.slice(catalog.indexOf("static FilterResolution ResolveExactCandidate"), catalog.indexOf("static Snapshot EnsureSnapshot"));
    expect(exact).toContain("candidate.IsSuite && candidate.ExactlySelectableLeafDescendantCount == 0");
    expect(exact).toContain('"exact_suite_no_selectable_leaves"');
    expect(exact.indexOf('"exact_suite_no_selectable_leaves"')).toBeLessThan(exact.indexOf("FilterResolution.Resolved("));
    expect(catalog).toContain("result.ExactlySelectableLeaves(assembly) - selectableLeavesBeforeChildren");
    expect(catalog).toContain("!candidate.IsSuite && IsExactlySelectable(candidate) && !string.IsNullOrEmpty(candidate.FullName)");
    expect(catalog).toContain("if (!isSuite && hasChildren)");
    expect(catalog).toContain('result.Fail("leaf_has_children")');
    const admissionStart = source.indexOf("UnityTestDiscoveryCatalog.FilterResolution filterResolution = null;");
    expect(admissionStart).toBeGreaterThan(0);
    const admission = source.slice(admissionStart, source.indexOf("var resolvedAssembly =", admissionStart));
    expect(admission).toContain("if (!filterResolution.IsResolved)");
    expect(admission).toContain("return TestFilterResolutionError(filterResolution);");
  });

  it("treats missing test operation status as unresolved instead of suggesting a new run or cancellation", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const start = source.indexOf("internal static string TestRunMissingDetailsJson");
    expect(start).toBeGreaterThan(0);
    const missing = source.slice(start, source.indexOf("static string TestRunArtifactIdentityMismatchDetailsJson", start));
    expect(missing).toContain("Missing status is not proof of non-execution");
    expect(missing).toContain("Do not repeat test.run or cancel the unresolved operation");
    expect(missing).toContain("do_not_repeat_test.run");
    expect(missing).not.toContain("start a new test.run");
    expect(missing).not.toContain('\\"test.run.cancel\\"');
  });

  it("unwraps reflected test runner failures and writes diagnostic artifacts", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const resultSource = fs.readFileSync(path.join(packageRoot, "Editor", "Commands", "CommandExecutionResult.cs"), "utf8");

    expect(source).toContain("UnwrapTargetInvocation(Exception exception)");
    expect(source).toContain("exception is TargetInvocationException");
    expect(source).toContain("public class TestRunCallbacksProxy : System.Reflection.DispatchProxy");
    expect(source).not.toContain("sealed class TestRunCallbacksProxy");
    expect(source).toContain("public TestRunCallbacksProxy()");
    expect(source).toContain("internal sealed class TestRunState");
    expect(source).toContain("BuildTestAutomationCommands.FinalizeTestRun(m_state)");
    expect(source).toContain("FindInstanceMethod(instance.GetType(), methodName, argument)");
    expect(source).toContain("ConstructGenericMethod(methods[index], argument)");
    expect(source).toContain("method.MakeGenericMethod(argument.GetType())");
    expect(source).toContain("BuildMethodArguments(method, argument)");
    expect(source).toContain("CanSupplyExtraParameters(parameters)");
    expect(source).toContain('MarkTestRunFailed(state, "startup_failed", unwrapped)');
    expect(source).toContain("IsCurrentTestRunCallback(m_state, m_callbackRegistrationEpoch)");
    expect(source).toContain('MarkTestRunFailed(m_state, "callback_failed", exception)');
    expect(source).toContain('Diagnostic artifact: " + artifactPath');
    expect(source).toContain('diagnostic_artifact');
    expect(source).toContain('exception_type');
    expect(source).toContain('stack_trace');
    expect(source).toContain("Bounded(unwrapped.StackTrace, 12000)");
    expect(resultSource).toContain("ErrorDetailsJson");
    expect(resultSource).toContain('json += ",\\\"details\\\":" + ErrorDetailsJson');
  });

  it("recovers test.run.status from persisted artifacts after domain reload", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("ReadPersistedTestRunArtifact(runId, artifactPath, out var artifactIdentityMismatch)");
    expect(source).toContain("NormalizePersistedTestRunArtifactForRead(content, runId, artifactPath)");
    expect(source).toContain('legacy_terminal_counts_untrusted');
    expect(source).toContain('legacy_artifact_v1');
    expect(source).toContain('ExtractTopLevelJsonInt(content, "artifact_schema_version", 1)');
    const legacyNormalizer = source.slice(source.indexOf("internal static string NormalizePersistedTestRunArtifactForRead"), source.indexOf("static string TryRecoverTestRunCallbackAfterReload"));
    expect(legacyNormalizer).not.toContain("WriteArtifact(");
    expect(source).toContain("MaxTestRunArtifactChars = 200000");
    expect(source).toContain("TestRunReloadRecoveryTimeoutMs = 5000");
    expect(source).toContain("TestRunRecoveredPlayModeExitGraceSeconds = 10");
    expect(source).toContain("new FileInfo(fullPath)");
    expect(source).toContain("artifact_too_large");
    expect(source).toContain("File.ReadAllText(fullPath)");
    expect(source).toContain("IsPersistedRunningTestRunArtifact(content)");
    expect(source).toContain("TryRecoverTestRunFromBatchmodeXmlAfterReload(runId, artifactPath, content)");
    expect(source).toContain("TryRecoverTestRunCallbackAfterReload(runId, artifactPath, content)");
    expect(source).toContain("MainThreadDispatcher.Run(() => ReRegisterTestRunCallbacksAfterReload");
    expect(source).toContain("TestRunStateFromPersistedArtifact(runId, artifactPath, persistedContent)");
    expect(source).toContain('InvokeReflected(api, "RegisterCallbacks", callbacks)');
    expect(source).toContain('PersistTestRunSnapshot(state, "callbacks_re_registered_after_reload")');
    expect(source).toContain("CallbackReRegisteredAfterReload");
    expect(source).toContain("callback_re_registered_after_reload");
    expect(source).toContain("callback_re_registered_after_domain_reload");
    expect(source).toContain("[InitializeOnLoadMethod]");
    expect(source).toContain("RestoreActiveTestRunCallbacksOnDomainLoad()");
    expect(source).toContain("ActiveTestRunSessionStateKey");
    expect(source).toContain("SessionState.SetString(ActiveTestRunSessionStateKey, runId)");
    expect(source).toContain("ClearActiveTestRunSession(state.RunId)");
    expect(source).toContain("SetActiveTestRunSession(runId)");
    expect(source).toContain("s_activeTestRunId");
    expect(source).toContain("active or awaiting callback restoration");
    expect(source).toContain("CallbackRegistrationEpoch");
    expect(source).toContain('ExtractTopLevelJsonInt(persistedContent, "execute_dispatch_count", 0)');
    expect(source).toContain('ExtractTopLevelJsonString(persistedContent, "filter_resolution_identity")');
    expect(source).toContain('ExtractTopLevelJsonString(persistedContent, "resolved_filter_name")');
    expect(source).toContain('ExtractTopLevelJsonBool(persistedContent, "resolved_filter_is_suite", false)');
    expect(source).toContain('"test_operation_replaced_after_reload"');
    expect(source).toContain('"test_execution_receipt_unavailable_after_reload"');
    expect(source).toContain('"test_filter_drift_after_reload"');
    expect(source).toContain('"test_operation_result_identity_mismatch"');
    expect(source).toContain('"test_operation_result_identity_unavailable"');
    expect(source).toContain('"resolved_filter_zero_execution"');
    expect(source).toContain('state.FilterResolutionIdentityStatus == "revalidation_unavailable"');
    expect(source).toContain('state.FilterResolutionIdentityStatus == "revalidation_timeout_after_reload"');
    const terminalIdentityFallback = "CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(state)";
    const outcomeClassifier = source.slice(source.indexOf("internal static void ClassifyResolvedTestRunOutcome"), source.indexOf("static void SetActiveTestRunSession"));
    const evidenceSplit = source.slice(source.indexOf("static void AppendTestRunEvidenceSplit"), source.indexOf("static void AppendTerminalCountEvidence"));
    expect(outcomeClassifier).toContain(terminalIdentityFallback);
    expect(evidenceSplit).toContain(terminalIdentityFallback);
    expect(source).toContain('CallbackReRegisteredAfterReload = ExtractTopLevelJsonBool(persistedContent, "callback_re_registered_after_reload", false)');
    expect(source).toContain("state.Success = IsSuccessfulTestRunOutcome(state)");
    expect(source).toContain("&& !IsFailedResultState(state.ResultState)");
    expect(source).toContain("state.TerminalCountsAuthoritative && !HasCoherentAuthoritativeTerminalCounts(state)");
    expect(source).toContain('ExtractTopLevelJsonInt(persistedContent, "assert_count", -1)');
    expect(source).toContain('ExtractTopLevelJsonInt(persistedContent, "test_case_count", -1)');
    expect(source).toContain('"awaiting_filter_revalidation_after_reload"');
    expect(source).toContain("TryDeferRecoveredFilterRevalidation(m_state)");
    expect(source).toContain("TryFinalizeRecoveredFilterRevalidation(state, now)");
    expect(source).toContain("IsPersistedAwaitingFilterRevalidationArtifact(content)");
    expect(source).toContain("return content;");
    expect(source).toContain('ReflectedMember(result, "Test") ?? result');
    expect(source).toContain('TryReflectedBool(testMetadata, "IsSuite"');
    expect(source).toContain("traversal.ExactSuiteIdentityCount != 1");
    expect(source).toContain("identity[suiteName.Length] == '.'");
    expect(source).toContain("traversal.Outcomes.TryGetValue(identity");
    expect(source).toContain('ExtractTopLevelJsonStringArray(persistedContent, "terminal_result_identities", MaxTerminalResultIdentities)');
    expect(source).toContain('\\"terminal_result_identity_status\\"');
    expect(source).toContain('\\"filter_resolution_identity_status\\"');
    expect(source).toContain("IsCurrentTestRunCallback(m_state, m_callbackRegistrationEpoch)");
    expect(source).toContain("ReferenceEquals(current, state)");
    expect(source).toContain("MainThreadDispatcher.IsMainThread");
    expect(source).toContain('startup_callback_recovery_skips_reflection_preview');
    const startupRecovery = source.slice(source.indexOf("static void RestoreActiveTestRunCallbacksOnDomainLoad"), source.indexOf("[McpCommand(", source.indexOf("static void RestoreActiveTestRunCallbacksOnDomainLoad")));
    expect(startupRecovery).not.toContain("Application.isBatchMode");
    expect(startupRecovery).not.toContain("delayCall");
    expect(startupRecovery).not.toContain("MainThreadDispatcher.Run");
    expect(startupRecovery.indexOf("RestoreRetainedTestRunPublicationsOnDomainLoad()")).toBeLessThan(startupRecovery.indexOf("SessionState.GetString(ActiveTestRunSessionStateKey"));
    expect(startupRecovery.indexOf("TryValidateTestRunArtifactIdentityForV3Transition")).toBeLessThan(startupRecovery.indexOf("ReRegisterTestRunCallbacksAfterReload"));
    expect(source).toContain("TryClassifyRecoveredPlayModeRunAfterExit(state, context.State)");
    expect(source).toContain("EditorReadyAndNotPlaying(runtimeState)");
    expect(source).toContain("callback_lost_after_playmode_exit");
    expect(source).toContain("Unity Editor is ready and not in Play Mode after test.run callback re-registration");
    expect(source).toContain('ExtractTopLevelJsonDateTime(persistedContent, "started_at", DateTime.UtcNow)');
    expect(source).toContain('ExtractTopLevelJsonInt(persistedContent, "max_seconds", 600)');
    expect(source).toContain("WriteArtifact(artifactPath, recovered)");
    expect(source).toContain("TestRunRecoveredFromBatchmodeXmlJson(state, summary)");
    expect(source).toContain("TestRunLostCallbackAfterDomainReloadJson(runId, artifactPath, content)");
    expect(source).toContain("TestRunFallbackStatusJson(runId, mode, filter, testAssembly)");
    expect(source).toContain('\\"fallback_status\\":');
    expect(source).toContain("unknown_after_reload");
    expect(source).toContain("lost_callback_after_domain_reload");
    expect(source).toContain("Do not treat unknown_after_reload as pass or fail");
    expect(source).toContain("unity.editor.installations.list");
    expect(source).toContain("TestRunBatchmodeFallbackRoot = \"Library/SGGUnityMcp/job-artifacts/test-batchmode-results\"");
    expect(source).toContain("TestRunBatchmodeFallbackContract = \"sgg.unity_mcp.test_run_batchmode_fallback.v1\"");
    expect(source).toContain("TestRunBatchmodeFallback(McpCommandContext context)");
    expect(source).toContain('"test.run.batchmode_fallback"');
    expect(source).toContain("TestRunBatchmodeFallbackResultJson(runId, artifactPath, artifactExists, artifactTooLarge, mode, filter, testAssembly)");
    expect(source).toContain("TestRunBatchmodeFallbackJson(runId, mode, filter, testAssembly, true)");
    expect(source).toContain("TestRunBatchmodeUnityArgs(mode, filter, testAssembly, xmlPath, logPath)");
    expect(source).toContain('"-assemblyNames"');
    expect(source).toContain('PersistedTestRunAssembly(persistedContent)');
    expect(source).toContain('"assembly_unknown"');
    expect(source).toContain('\\"fallback_available\\"');
    expect(source).toContain('\\"terminal_counts_authoritative\\":false');
    expect(source).toContain('\\"test_assembly\\"');
    expect(source).toContain("\"-runTests\"");
    expect(source).toContain("\"-testResults\"");
    expect(source).not.toContain('" -quit"');
    expect(source).not.toContain('"-quit"');
    expect(source).toContain('\\"manual_only\\":true');
    expect(source).toContain('\\"copy_safe\\":false');
    expect(source).toContain('\\"same_project_open_editor_unsafe\\":true');
    expect(source).toContain('\\"requires_separate_project_or_closed_editor\\":true');
    expect(source).toContain("TestRunBatchmodeNoXmlDiagnosticsJson(xmlPath, logPath)");
    expect(source).toContain('ExtractTopLevelJsonString(content, "mode")');
    expect(source).toContain('ExtractTopLevelJsonString(content, "test_mode")');
    expect(source).toContain('ExtractTopLevelJsonString(content, "filter")');
    expect(source).toContain('ExtractTopLevelJsonString(content, "test_filter")');
    expect(source).toContain("FindTopLevelJsonPropertyColon");
    expect(source).toContain("ClassifyBatchmodeNoXmlLog");
    expect(source).toContain("compile_errors_before_test_runner_xml");
    expect(source).toContain("licensing_or_upm_startup_before_test_runner_xml");
    expect(source).toContain("unity_startup_stalled_before_test_runner_xml");
    expect(source).toContain("test_runner_not_started_before_xml");
    expect(source).toContain("TestRunFallbackEvidenceStatus");
    expect(source).toContain('"no_xml"');
    expect(source).toContain('"xml_too_large"');
    expect(source).toContain('"xml_unreadable"');
    expect(source).toContain('"xml_zero_tests"');
    expect(source).toContain('\\"xml_result_exists\\":');
    expect(source).toContain('\\"xml_result_too_large\\":');
    expect(source).toContain('\\"xml_size_bytes\\":');
    expect(source).toContain('\\"log_exists\\":');
    expect(source).toContain('\\"log_size_bytes\\":');
    expect(source).toContain('\\"no_xml_diagnostics\\":');
    expect(source).toContain("Do not blindly repeat test.run after unknown_after_reload");
    expect(source).toContain("Use unity.editor.installations.list to select an installed Unity Editor executable");
    expect(source).toContain("MaxTestRunBatchmodeLogDiagnosticChars = 12000");
    expect(source).toContain("Batchmode XML exists but reports zero tests");
    expect(source).toContain('\\"zero_tests\\"');
    expect(source).not.toContain("run_unity_batchmode_test_fallback");
    expect(source).toContain("recovered_from_batchmode_xml_after_reload");
    expect(source).toContain("\"test.run.cancel\"");
    expect(source).toContain("Unity test run operation was not found in memory or persisted artifacts");
    expect(source).toContain("TestRunMissingDetailsJson(runId, artifactPath)");
    expect(source.match(/InvokeReflected\(api, "Execute", settings\)/g)).toHaveLength(1);
    const callbackRecovery = source.slice(source.indexOf("static string ReRegisterTestRunCallbacksAfterReload"), source.indexOf("static TestRunState TestRunStateFromPersistedArtifact"));
    expect(callbackRecovery).toContain('InvokeReflected(api, "RegisterCallbacks", callbacks)');
    expect(callbackRecovery).not.toContain('InvokeReflected(api, "Execute", settings)');
    expect(source).toContain('builder.Append("\\\"write_command_repeated\\\":false,")');
    expect(source).toContain('builder.Append("\\\"write_replay_allowed\\\":false,")');
    const persistedRecovery = source.slice(source.indexOf("if (IsPersistedRunningTestRunArtifact(content))"), source.indexOf("internal static bool PersistedTestRunArtifactIdentityMatches"));
    expect(persistedRecovery.indexOf("TryValidateTestRunArtifactIdentityForV3Transition")).toBeLessThan(persistedRecovery.indexOf("TryRecoverTestRunFromBatchmodeXmlAfterReload"));
    expect(persistedRecovery.indexOf("TryRecoverTestRunFromBatchmodeXmlAfterReload")).toBeLessThan(persistedRecovery.indexOf("TryRecoverTestRunCallbackAfterReload"));
    const xmlRecovery = source.slice(source.indexOf("static string TryRecoverTestRunFromBatchmodeXmlAfterReload"), source.indexOf("static bool TryReadBatchmodeXmlSummary"));
    expect(xmlRecovery.indexOf("FinalizeInvalidRecoveredTestRun(state)")).toBeLessThan(xmlRecovery.indexOf("RecoverTestRunFromBatchmodeXml(state)"));
    const xmlResult = source.slice(source.indexOf("static string TestRunRecoveredFromBatchmodeXmlJson"), source.indexOf("static string TestRunBatchmodeFallbackResultJson"));
    expect(xmlResult).toContain('builder.Append("\\\"success\\\":false,")');
    expect(xmlResult).toContain('terminal_count_source\\\":\\\"batchmode_xml_non_authoritative');
    expect(xmlResult).toContain('callback_re_registered_after_reload\\\":false');
    const retainedRecovery = source.slice(source.indexOf("static TestRunState TestRunStateFromRetainedPublicationSession"), source.indexOf("static bool HasPendingRetainedTestRunPublication"));
    expect(retainedRecovery).toContain("TryValidateTestRunArtifactIdentityForV3Transition(content, runId, artifactPath");
    expect(retainedRecovery).toContain('state.FilterResolutionIdentityStatus = ExtractTopLevelJsonString(content, "filter_resolution_identity_status")');
    expect(retainedRecovery).toContain("ClassifyResolvedTestRunOutcome(state)");
    expect(retainedRecovery).toContain("state.Success = IsSuccessfulTestRunOutcome(state)");
  });

  it("writes owner-bound schema-v3 identity through every test.run transition", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    expect(source).toContain('"test_run_artifact_v3"');
    const builders: Array<[string, string]> = [
      ["static string TestRunStateJson", "static void AppendArtifactContract"],
      ["static string TestRunCancelledAfterReloadJson", "static bool TryDeleteTestRunArtifact"],
      ["static string TestRunLostCallbackAfterDomainReloadJson", "static string TestRunFallbackStatusJson"],
      ["static string TestRunRecoveredFromBatchmodeXmlJson", "static string TestRunBatchmodeFallbackResultJson"],
    ];
    for (const [start, end] of builders) {
      const builder = source.slice(source.indexOf(start), source.indexOf(end, source.indexOf(start)));
      expect(builder).toContain('\\"args_hash\\"');
      expect(builder).toContain('\\"caller_args_hash\\"');
      expect(builder).toContain("CurrentTestRunArtifactSchemaVersion");
    }
    const cancel = source.slice(source.indexOf("if (!TryReadTestRunArtifactIdentityForV3Transition"), source.indexOf("static string BuildSettingsJson"));
    expect(cancel.indexOf("TryReadTestRunArtifactIdentityForV3Transition")).toBeLessThan(cancel.indexOf("WriteArtifact(artifactPath, stateJson)"));
    expect(cancel).toContain('"artifact_overwritten\\\":false');
  });

  it("serves test.run status and cancel recovery from the bridge thread without Unity main-thread dispatch", () => {
    const bridgeSource = fs.readFileSync(bridgeServerPath, "utf8");
    const commandSource = fs.readFileSync(commandsPath, "utf8");
    const projectPathsSource = fs.readFileSync(projectPathsPath, "utf8");

    expect(bridgeSource).toContain('if (builtInCommand && command == "test.run.status")');
    expect(bridgeSource).toContain("BuildTestAutomationCommands.TestRunStatus(new McpCommandContext");
    expect(bridgeSource).toContain('if (builtInCommand && command == "test.run.batchmode_fallback")');
    expect(bridgeSource).toContain("BuildTestAutomationCommands.TestRunBatchmodeFallback(new McpCommandContext");
    expect(bridgeSource).toContain('if (builtInCommand && command == "test.run.cancel")');
    expect(bridgeSource).toContain("BuildTestAutomationCommands.TestRunCancel(new McpCommandContext");
    expect(bridgeSource).toContain("EmptyInvokeResponseErrorJson(request)");
    expect(bridgeSource).toContain('CommandExecutionResult.Error("bridge_protocol_error"');
    expect(bridgeSource.indexOf('if (builtInCommand && command == "test.run.status")')).toBeLessThan(bridgeSource.indexOf("return m_registry.Invoke"));
    expect(bridgeSource).toContain('if (builtInCommand && command == "build.run.status")');
    expect(bridgeSource).toContain("BuildTestAutomationCommands.BuildRunStatus(new McpCommandContext");
    expect(bridgeSource.indexOf('if (builtInCommand && command == "build.run.status")')).toBeLessThan(bridgeSource.indexOf("return m_registry.Invoke"));
    expect(bridgeSource.indexOf('if (builtInCommand && command == "test.run.batchmode_fallback")')).toBeLessThan(bridgeSource.indexOf("return m_registry.Invoke"));
    expect(bridgeSource.indexOf('if (builtInCommand && command == "test.run.cancel")')).toBeLessThan(bridgeSource.indexOf("return m_registry.Invoke"));
    expect(commandSource).toContain("static readonly object s_testRunsGate");
    expect(commandSource).toContain("lock (s_testRunsGate)");
    expect(projectPathsSource).toContain("[InitializeOnLoad]");
    expect(projectPathsSource).toContain("static readonly string s_projectRoot = ResolveProjectRoot()");
    expect(projectPathsSource).toContain("get { return s_projectRoot; }");
  });

  it("blocks test.run when compiler errors are present", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("ConsoleLogBuffer.HasCompileErrors()");
    expect(source).toContain('CommandExecutionResult.Error("compilation_failed"');
    expect(source).toContain("fix compile.errors.recent before starting test.run");
    expect(source.indexOf("ConsoleLogBuffer.HasCompileErrors()")).toBeLessThan(source.indexOf("InvokeReflected(api, \"Execute\", settings)"));
  });

  it("prevents unsafe test.run.status artifact path traversal", () => {
    const source = fs.readFileSync(commandsPath, "utf8");

    expect(source).toContain("IsSafeTestRunId(runId)");
    expect(source).toContain('runId.StartsWith("test-", StringComparison.Ordinal)');
    expect(source).toContain("runId.Length != 29");
    expect(source).toContain("character != '-'");
    expect(source).toContain("!char.IsDigit(character)");
    expect(source).toContain("character >= 'a' && character <= 'f'");
    expect(source).not.toContain('TestRunArtifactRoot + "/" + JsonUtil.ExtractString');
  });

  it("detects test framework availability without asmdef reference", () => {
    const source = fs.readFileSync(commandsPath, "utf8");
    const asmdef = fs.readFileSync(path.join(packageRoot, "Editor", "SGG.UnityMCP.Editor.asmdef"), "utf8");

    expect(source).toContain("EditorAssemblyDiscovery.LoadedAssemblies()");
    expect(source).not.toContain("AppDomain.CurrentDomain.GetAssemblies()");
    expect(source).toContain('"UnityEditor.TestTools.TestRunner.Api.TestRunnerApi"');
    expect(source).toContain('"UnityEngine.TestTools.UnityTestAttribute"');
    expect(source).toContain('\\"available\\"');
    expect(source).toContain('\\"type_hints\\"');
    expect(source).toContain('\\"package_hints\\"');
    expect(source).not.toContain("using UnityEditor.TestTools");
    expect(source).not.toContain("using UnityEngine.TestTools");
    expect(asmdef).not.toContain("com.unity.test-framework");
  });

  it("documents commands and feature flag in examples", () => {
    const docs = fs.readFileSync(path.join(process.cwd(), "docs", "en", "reference", "commands.md"), "utf8");
    const ruDocs = fs.readFileSync(path.join(process.cwd(), "docs", "ru", "reference", "commands.md"), "utf8");
    const gatewayExample = fs.readFileSync(path.join(process.cwd(), "config", "gateway.example.json"), "utf8");
    const policyExample = fs.readFileSync(path.join(process.cwd(), "config", "policy.example.json"), "utf8");

    for (const text of [docs, ruDocs]) {
      expect(text).toContain("`build.settings`");
      expect(text).toContain("`build.profile.list`");
      expect(text).toContain("`build.run`");
      expect(text).toContain("`build.run.status`");
      expect(text).toContain("`test.framework.status`");
      expect(text).toContain("`test.list`");
      expect(text).toContain("`test.run`");
      expect(text).toContain("`test.run.status`");
      expect(text).toContain("`test.run.cancel`");
      expect(text.toLowerCase()).toContain("poll");
      expect(text).toContain("build_test_automation");
      expect(text).toContain("max_results");
      expect(text).toContain("cursor");
      expect(text).toContain("parameterized identities");
      expect(text).toContain("exact runnable full names");
      expect(text).toContain("ambiguous");
      expect(text).toContain("test.run.status");
    }

    expect(gatewayExample).toContain('"build_test_automation": false');
    expect(policyExample).toContain('"build.settings"');
    expect(policyExample).toContain('"build.profile.list"');
    expect(policyExample).toContain('"build.run"');
    expect(policyExample).toContain('"build.run.status"');
    expect(policyExample).toContain('"test.framework.status"');
    expect(policyExample).toContain('"test.run.status"');
    expect(policyExample).toContain('"test.run"');
    expect(policyExample).toContain('"test.run.cancel"');
  });
});
