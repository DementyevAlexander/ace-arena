using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    public static class BuildTestAutomationCommands
    {
        const string FeatureFlag = "build_test_automation";
        const string BuildSettingsSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":200}},\"additionalProperties\":false}";
        const string BuildProfileListSchemaJson = "{\"type\":\"object\",\"properties\":{\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":100}},\"additionalProperties\":false}";
        const string BuildRunSchemaJson = "{\"type\":\"object\",\"properties\":{\"build_name\":{\"type\":\"string\",\"maxLength\":64},\"output_root\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":1024,\"pattern\":\".*\\\\S.*\",\"description\":\"Optional absolute local directory outside the Unity project. The Gateway appends its reserved operation id and target-specific build leaf. Real external builds always require exact human confirmation.\"},\"development\":{\"type\":\"boolean\"},\"dry_run\":{\"type\":\"boolean\"},\"allow_unsaved_scene_changes\":{\"type\":\"boolean\",\"default\":false}},\"additionalProperties\":false}";
        const string BuildRunStatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"operation_id\":{\"type\":\"string\",\"pattern\":\"^build-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$\"}},\"required\":[\"operation_id\"],\"additionalProperties\":false}";
        const string TestListSchemaJson = "{\"type\":\"object\",\"properties\":{\"query\":{\"type\":\"string\",\"maxLength\":128},\"mode\":{\"type\":\"string\",\"enum\":[\"editmode\",\"playmode\"]},\"assembly\":{\"type\":\"string\",\"maxLength\":160,\"pattern\":\"^[A-Za-z0-9._-]*$\"},\"cursor\":{\"type\":\"integer\",\"minimum\":0},\"max_results\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":500},\"response_mode\":{\"type\":\"string\",\"enum\":[\"full\",\"compact\"],\"default\":\"full\"}},\"additionalProperties\":false}";
        const string TestRunSchemaJson = "{\"type\":\"object\",\"properties\":{\"mode\":{\"type\":\"string\",\"enum\":[\"editmode\",\"playmode\"]},\"filter\":{\"type\":\"string\",\"maxLength\":160},\"assembly\":{\"type\":\"string\",\"maxLength\":160,\"pattern\":\"^[A-Za-z0-9._-]*$\"},\"max_seconds\":{\"type\":\"integer\",\"minimum\":1,\"maximum\":3600},\"dry_run\":{\"type\":\"boolean\"},\"allow_unsaved_scene_changes\":{\"type\":\"boolean\",\"default\":false},\"response_mode\":{\"type\":\"string\",\"enum\":[\"full\",\"compact\"],\"default\":\"full\"}},\"additionalProperties\":false}";
        const string TestRunWrapperWaitJson = "{\"mode\":\"wait\",\"wait_for_test_run_terminal_ms\":{\"min_ms\":1,\"max_ms\":295000,\"compile_readiness_contract\":\"sgg.unity_mcp.test_run_compile_readiness.v1\",\"deadline_scope\":\"start_and_terminal_poll\",\"response_mode\":\"compact\",\"dry_run\":false}}";
        const string TestRunStatusSchemaJson = "{\"type\":\"object\",\"properties\":{\"operation_id\":{\"type\":\"string\",\"pattern\":\"^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$\"},\"run_id\":{\"type\":\"string\",\"pattern\":\"^test-[0-9]{8}-[0-9]{6}-[0-9a-f]{8}$\"},\"response_mode\":{\"type\":\"string\",\"enum\":[\"full\",\"compact\"],\"default\":\"full\"}},\"anyOf\":[{\"required\":[\"operation_id\"]},{\"required\":[\"run_id\"]}],\"additionalProperties\":false}";
        const string TestRunBatchmodeFallbackSchemaJson = "{\"type\":\"object\",\"properties\":{\"operation_id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"run_id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80}},\"additionalProperties\":false}";
        const string TestRunCancelSchemaJson = "{\"type\":\"object\",\"properties\":{\"operation_id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"run_id\":{\"type\":\"string\",\"minLength\":1,\"maxLength\":80},\"reason\":{\"type\":\"string\",\"maxLength\":240},\"forget_artifact\":{\"type\":\"boolean\",\"default\":false}},\"additionalProperties\":false}";
        const string EmptySchemaJson = "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}";
        const string BuildSettingsOutputLimitJson = "{\"max_results\":200}";
        const string BuildProfileListOutputLimitJson = "{\"max_results\":100}";
        const string BuildRunOutputLimitJson = "{\"max_scenes\":200,\"max_artifact_chars\":200000}";
        const string TestFrameworkStatusOutputLimitJson = "{\"max_types\":8,\"max_hints\":8}";
        const string TestListOutputLimitJson = "{\"max_results\":500}";
        const string TestRunOutputLimitJson = "{\"max_seconds\":3600,\"max_artifact_chars\":200000,\"max_matched_tests\":32}";
        const string BuildOutputRoot = "BuildArtifacts/SGGUnityMcp";
        const string BuildArtifactRoot = "Library/SGGUnityMcp/job-artifacts/build-runs";
        const string TestRunArtifactRoot = "Library/SGGUnityMcp/job-artifacts/test-runs";
        const string TestRunBatchmodeFallbackRoot = "Library/SGGUnityMcp/job-artifacts/test-batchmode-results";
        const int MinimumAuthoritativeTestRunArtifactSchemaVersion = 2;
        const int CurrentTestRunArtifactSchemaVersion = 3;
        const int OwnerBindingFallbackTestRunArtifactSchemaVersion = 3;
        const int MaxBuildRunScenes = 200;
        const int BuildRunArtifactSchemaVersion = 2;
        const int MaxBuildRunArtifactChars = 200000;
        const int MaxBuildOutputRootChars = 1024;
        const int LegacyReflectionMaxAssembliesScanned = 250;
        const int LegacyReflectionMaxTypesScanned = 20000;
        const int LegacyReflectionMaxMethodsScanned = 200000;
        const int LegacyReflectionMaxScanMilliseconds = 7500;
        const int MaxFailedTestDetails = 16;
        const int MaxTestRunMatchedTests = 32;
        const int MaxTerminalResultIdentities = 64;
        const int MaxTerminalResultNodes = 4096;
        const string AwaitingFilterRevalidationStatus = "awaiting_filter_revalidation_after_reload";
        const string PreExecuteRefusedStatus = "pre_execute_refused";
        const int MaxFailedTestMessageChars = 1000;
        const int MaxFailedTestStackTraceChars = 2000;
        const int MaxTestRunArtifactChars = 200000;
        const int TestRunReloadRecoveryTimeoutMs = 5000;
        const int TestRunRecoveredPlayModeExitGraceSeconds = 10;
        const int MaxTestRunPublicationAttempts = 8;
        const double TestRunPublicationRetrySeconds = 1d;
        const double TestRunPublicationRetryWindowSeconds = 8d;
        const int MaxRetainedTestRunPublications = 16;
        const long MaxTestRunBatchmodeXmlBytes = 5L * 1024L * 1024L;
        const int MaxTestRunBatchmodeLogDiagnosticChars = 12000;
        const string TestRunBatchmodeFallbackContract = "sgg.unity_mcp.test_run_batchmode_fallback.v1";
        const string ActiveTestRunSessionStateKey = "SGG.UnityMCP.BuildTestAutomation.activeTestRun";
        const string RetainedTestRunPublicationIndexSessionStateKey = "SGG.UnityMCP.BuildTestAutomation.retainedTestRunPublications";
        const string RetainedTestRunPublicationSessionStateKeyPrefix = "SGG.UnityMCP.BuildTestAutomation.retainedTestRunPublication.";
        const string TestRunResolvedFilterMatchingMode = "unity_test_runner_filter.authoritative_catalog_exact_test_names";
        const string TestRunUnityModeOnlyMatchingMode = "unity_test_runner_filter.testMode_only";
        const string TestRunReflectionMatchingMode = "reflection_case_insensitive_substring_assembly_type_method_with_best_effort_mode";
        const string TestRunReflectionOmittedMatchingMode = "reflection_preview_omitted_test_mode_only";
        static readonly string[] s_testRunRefusalMarkerFields = { "status", "result", "lifecycle" };
        static readonly object s_testRunsGate = new object();
        static readonly object s_buildRunsGate = new object();
        static readonly Dictionary<string, TestRunState> s_testRuns = new Dictionary<string, TestRunState>(StringComparer.Ordinal);
        static readonly Dictionary<string, TestRunState> s_retainedTestRunPublications = new Dictionary<string, TestRunState>(StringComparer.Ordinal);
        static string s_activeBuildRunId = string.Empty;
        static bool s_testRunUpdateRegistered;
        static string s_activeTestRunId = string.Empty;

        static bool IsTestRunInProgressStatus(string status)
        {
            return status == "running" || status == "execution_may_have_started" || status == AwaitingFilterRevalidationStatus;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maxChars);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "MoveFileExW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        static extern bool MoveFileEx(string existingFileName, string newFileName, uint flags);

        const uint MoveFileReplaceExisting = 0x1;
        const uint MoveFileWriteThrough = 0x8;
        const int MaxWindowsArtifactCommitAttempts = 8;
        const int WindowsArtifactCommitRetryDelayMs = 10;

        [InitializeOnLoadMethod]
        static void RestoreActiveTestRunCallbacksOnDomainLoad()
        {
            RestoreRetainedTestRunPublicationsOnDomainLoad();

            var runId = SessionState.GetString(ActiveTestRunSessionStateKey, string.Empty);
            if (!IsSafeTestRunId(runId))
            {
                SessionState.EraseString(ActiveTestRunSessionStateKey);
                return;
            }

            lock (s_testRunsGate)
            {
                s_activeTestRunId = runId;
            }

            var artifactPath = TestRunArtifactRoot + "/" + runId + ".json";
            try
            {
                var fullPath = AbsoluteProjectPath(artifactPath);
                if (!File.Exists(fullPath))
                {
                    ClearActiveTestRunSession(runId);
                    return;
                }

                var fileInfo = new FileInfo(fullPath);
                if (fileInfo.Length <= 0 || fileInfo.Length > MaxTestRunArtifactChars)
                {
                    ClearActiveTestRunSession(runId);
                    return;
                }

                var content = File.ReadAllText(fullPath);
                if (RequiresStrictTestRunRefusalValidation(content))
                {
                    // Malformed new refusal evidence stays unresolved; do not clear its marker or repair it.
                    if (!TryValidateTestRunArtifactIdentityForV3Transition(content, runId, artifactPath, out _, out _, out _)
                        || !TryReadPreExecuteRefusal(content, out _)) return;
                    ClearActiveTestRunSession(runId);
                    return;
                }
                if (!TryValidateTestRunArtifactIdentityForV3Transition(content, runId, artifactPath, out _, out _, out _)
                    || !IsPersistedRunningTestRunArtifact(content))
                {
                    ClearActiveTestRunSession(runId);
                    return;
                }

                ReRegisterTestRunCallbacksAfterReload(runId, artifactPath, content);
            }
            catch
            {
                // Status polling retains the existing bounded fallback recovery path.
            }
        }

        [McpCommand(
            "build.settings",
            Title = "Build settings",
            Description = "Read active build target and enabled Build Settings scenes without building or switching targets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = BuildSettingsSchemaJson,
            Tags = new string[] { "build", "test", "status" },
            Capability = "build_test_automation",
            ResultShape = "bounded_list",
            OutputLimitJson = BuildSettingsOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult BuildSettings(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(BuildSettingsJson(context));
        }

        [McpCommand(
            "build.profile.list",
            Title = "Build profile list",
            Description = "Read bounded Unity Build Profile assets by reflection without building or switching targets",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = BuildProfileListSchemaJson,
            Tags = new string[] { "build", "profile", "status" },
            Capability = "build_test_automation",
            ResultShape = "bounded_list",
            OutputLimitJson = BuildProfileListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult BuildProfileList(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(BuildProfileListJson(context));
        }

        [McpCommand(
            "build.run",
            Title = "Run Unity build",
            Description = "Start a durable Unity build operation for the active target under the bounded project-local root or an explicitly confirmed absolute external output_root; poll build.run.status",
            Risk = "write",
            Idempotency = "unsafe",
            TimeoutMs = 10000,
            SchemaJson = BuildRunSchemaJson,
            Tags = new string[] { "build", "automation", "build_run_operation_correlated" },
            Capability = "build_test_automation",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "build_result",
            OutputLimitJson = BuildRunOutputLimitJson,
            SupportsJob = true,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult BuildRun(McpCommandContext context)
        {
            var statusResult = RequireEditingStatus("Build runs require editing status");
            if (statusResult != null)
            {
                return statusResult;
            }

            var scenes = EnabledScenePaths();
            if (scenes.Count == 0)
            {
                return CommandExecutionResult.Error("no_build_scenes", "Build Settings has no enabled scenes");
            }

            var rawBuildName = JsonUtil.ExtractString(context.ArgsJson, "build_name").Trim();
            if (rawBuildName.Length > 64)
            {
                return CommandExecutionResult.Error("invalid_field_value", "build_name must be at most 64 characters");
            }

            var buildName = SafeBuildName(string.IsNullOrEmpty(rawBuildName) ? ProjectPaths.ProjectId : rawBuildName);
            var development = JsonUtil.ExtractBool(context.ArgsJson, "development", false);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var reservedOperationId = JsonUtil.ExtractString(context.ArgsJson, "_build_run_operation_id").Trim();
            if (!string.IsNullOrEmpty(reservedOperationId) && !IsSafeBuildRunId(reservedOperationId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "_build_run_operation_id has an invalid format");
            }
            var operationId = string.IsNullOrEmpty(reservedOperationId) ? CreateBuildRunId() : reservedOperationId;
            var effectiveArgsHash = Sha256Prefixed(context.ArgsJson);
            var callerArgsHash = JsonUtil.ExtractString(context.ArgsJson, "_build_run_caller_args_hash").Trim();
            if (!string.IsNullOrEmpty(callerArgsHash) && !IsSafeSha256Hash(callerArgsHash))
            {
                return CommandExecutionResult.Error("invalid_field_value", "_build_run_caller_args_hash has an invalid format");
            }
            callerArgsHash = string.IsNullOrEmpty(callerArgsHash) ? effectiveArgsHash : callerArgsHash;
            var createdAt = DateTime.UtcNow.ToString("o");
            var activeTarget = EditorUserBuildSettings.activeBuildTarget;
            var activeGroup = BuildPipeline.GetBuildTargetGroup(activeTarget);
            var hasRequestedOutputRoot = JsonUtil.HasProperty(context.ArgsJson, "output_root");
            var requestedOutputRoot = JsonUtil.ExtractString(context.ArgsJson, "output_root").Trim();
            var outputSelection = "project_default";
            var outputRoot = BuildOutputRoot;
            if (hasRequestedOutputRoot && string.IsNullOrEmpty(requestedOutputRoot))
            {
                return CommandExecutionResult.Error("unsafe_build_output", "output_root must contain a non-whitespace absolute path");
            }
            if (!string.IsNullOrEmpty(requestedOutputRoot))
            {
                if (!TryNormalizeExternalBuildOutputRoot(requestedOutputRoot, out outputRoot, out var outputRootError))
                {
                    return CommandExecutionResult.Error("unsafe_build_output", outputRootError);
                }
                outputSelection = "caller_absolute_root";
            }
            var outputPath = BuildOutputPath(operationId, activeTarget, buildName, outputRoot, outputSelection == "caller_absolute_root");
            var artifactPath = BuildArtifactRoot + "/" + operationId + ".json";
            var options = development ? BuildOptions.Development : BuildOptions.None;
            var allowUnsavedSceneChanges = JsonUtil.ExtractBool(context.ArgsJson, "allow_unsaved_scene_changes", false);
            if (!TryValidateBuildOutputPath(outputPath, outputRoot, outputSelection, operationId, buildName, activeTarget, true, out var outputError))
            {
                return CommandExecutionResult.Error("unsafe_build_output", outputError);
            }
            var state = new BuildRunState
            {
                OperationId = operationId,
                ArgsHash = effectiveArgsHash,
                CallerArgsHash = callerArgsHash,
                CreatedAt = createdAt,
                Status = dryRun ? "planned" : "running",
                Result = dryRun ? "dry_run" : "running",
                Success = dryRun,
                Target = activeTarget,
                TargetGroup = activeGroup,
                Development = development,
                BuildName = buildName,
                OutputPath = outputPath,
                OutputRoot = outputRoot,
                OutputSelection = outputSelection,
                ArtifactPath = artifactPath,
                Scenes = scenes,
                Options = options,
                RuntimeState = context.State,
                AllowUnsavedSceneChanges = allowUnsavedSceneChanges,
                ReceiptConsistency = dryRun ? "not_applicable" : "pending",
            };

            if (dryRun)
            {
                return CommandExecutionResult.Ok(BuildRunStateJson(state, true));
            }

            if (!allowUnsavedSceneChanges)
            {
                var unsavedResult = BuiltInCommands.RequireNoUnsavedSceneChanges(context.State, "build.run", "build.run");
                if (unsavedResult != null)
                {
                    return unsavedResult;
                }
            }

            if (ConsoleLogBuffer.HasCompileErrors())
            {
                return CommandExecutionResult.Error("compilation_failed", "Unity has compiler errors; fix compile.errors.recent before starting build.run", BuiltInCommands.CompileStatusJson(context.State, false, 0, 0));
            }

            lock (s_buildRunsGate)
            {
                if (!string.IsNullOrEmpty(s_activeBuildRunId))
                {
                    return CommandExecutionResult.Error("unity_busy", "Another build.run operation is already active");
                }
                if (File.Exists(AbsoluteProjectPath(artifactPath)))
                {
                    return CommandExecutionResult.Error("operation_exists", "The build.run operation id already has a persisted receipt");
                }
                s_activeBuildRunId = operationId;
            }

            var runningJson = BuildRunStateJson(state, false);
            if (!TryWriteArtifactAtomic(artifactPath, runningJson, out var artifactError))
            {
                ClearActiveBuildRun(operationId);
                return CommandExecutionResult.Error("artifact_write_failed", "Could not persist the build.run startup receipt: " + artifactError);
            }

            MainThreadDispatcher.Enqueue(() => ExecuteBuildRun(state));
            return CommandExecutionResult.Ok(runningJson);
        }

        [McpCommand(
            "build.run.status",
            Title = "Unity build run status",
            Description = "Read the bounded persisted receipt for an exact build.run operation without replaying the build",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = BuildRunStatusSchemaJson,
            Tags = new string[] { "build", "status" },
            Capability = "build_test_automation",
            ResultShape = "operation_status",
            OutputLimitJson = BuildRunOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult BuildRunStatus(McpCommandContext context)
        {
            var operationId = JsonUtil.ExtractString(context.ArgsJson, "operation_id").Trim();
            if (!IsSafeBuildRunId(operationId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id has an invalid format");
            }

            var artifactPath = BuildArtifactRoot + "/" + operationId + ".json";
            var persisted = ReadPersistedBuildRunArtifact(operationId, artifactPath, out var identityMismatch, out var readError);
            if (identityMismatch)
            {
                return CommandExecutionResult.Error("bridge_protocol_error", "Persisted build.run artifact identity does not match the requested operation");
            }
            if (!string.IsNullOrEmpty(persisted))
            {
                return CommandExecutionResult.Ok(persisted);
            }
            if (!string.IsNullOrEmpty(readError))
            {
                return CommandExecutionResult.Error("internal_error", "The persisted build.run receipt could not be read safely", "{\"operation_id\":" + JsonUtil.String(operationId) + ",\"artifact_path\":" + JsonUtil.String(artifactPath) + ",\"receipt_unavailable\":true,\"build_run_replayed\":false,\"message\":" + JsonUtil.String(readError) + "}");
            }

            return CommandExecutionResult.Error("build_run_not_found", "Unity build.run operation was not found in persisted artifacts");
        }

        [McpCommand(
            "test.framework.status",
            Title = "Unity Test Framework status",
            Description = "Detect Unity Test Framework API availability without starting tests",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = EmptySchemaJson,
            Tags = new string[] { "test", "status" },
            Capability = "build_test_automation",
            ResultShape = "status",
            OutputLimitJson = TestFrameworkStatusOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TestFrameworkStatus(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(TestFrameworkStatusJson());
        }

        [McpCommand(
            "test.list",
            Title = "List Unity tests",
            Description = "List the complete bounded Unity TestRunner catalog without starting a test run; discovery remains pending until both requested mode snapshots are complete",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TestListSchemaJson,
            Tags = new string[] { "test", "discovery" },
            Capability = "build_test_automation",
            ResultShape = "bounded_list",
            OutputLimitJson = TestListOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TestList(McpCommandContext context)
        {
            return CommandExecutionResult.Ok(TestListJson(context));
        }

        [McpCommand(
            "test.run",
            Title = "Run Unity tests",
            Description = "Start a Unity-side Test Runner operation only when loaded scenes are clean; allow up to 60 seconds for admission/start including durable owner publication; poll it with test.run.status",
            Risk = "execute",
            Idempotency = "unsafe",
            TimeoutMs = 60000,
            SchemaJson = TestRunSchemaJson,
            Tags = new string[] { "test", "automation", "test_run_operation_correlated", "test_run_artifact_v3" },
            Capability = "build_test_automation",
            RequiresEditorStatus = new string[] { "editing" },
            ResultShape = "operation_status",
            OutputLimitJson = TestRunOutputLimitJson,
            WrapperWaitJson = TestRunWrapperWaitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TestRun(McpCommandContext context)
        {
            var statusResult = RequireEditingStatus("Test runs require editing status");
            if (statusResult != null)
            {
                return statusResult;
            }
            var lifecycleResult = RequireSettledCompilationForTestRun(context.State);
            if (lifecycleResult != null)
            {
                return lifecycleResult;
            }

            var mode = JsonUtil.ExtractString(context.ArgsJson, "mode").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(mode))
            {
                mode = "editmode";
            }

            if (mode != "editmode" && mode != "playmode")
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "mode must be editmode or playmode", "mode", "enum_value_not_allowed");
            }

            var filter = JsonUtil.ExtractString(context.ArgsJson, "filter").Trim();
            if (filter.Length > 160)
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "filter must be at most 160 characters", "filter", "string_too_long");
            }

            var testAssembly = JsonUtil.ExtractString(context.ArgsJson, "assembly").Trim();
            if (testAssembly.Length > 160 || !IsSafeTestAssemblyName(testAssembly))
            {
                return CommandExecutionResult.ValidationError("invalid_field_value", "assembly must be at most 160 characters and contain only letters, digits, dot, underscore, or hyphen", "assembly", testAssembly.Length > 160 ? "string_too_long" : "pattern_mismatch");
            }

            var maxSeconds = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_seconds", 600), 1, 3600);
            var dryRun = JsonUtil.ExtractBool(context.ArgsJson, "dry_run", false);
            var allowUnsavedSceneChanges = JsonUtil.ExtractBool(context.ArgsJson, "allow_unsaved_scene_changes", false);
            if (!dryRun)
            {
                var dirtySceneResult = RequireCleanScenesForTestRun(context.State, allowUnsavedSceneChanges, "admission");
                if (dirtySceneResult != null)
                {
                    return dirtySceneResult;
                }
            }

            if (!dryRun && ConsoleLogBuffer.HasCompileErrors())
            {
                return CommandExecutionResult.Error("compilation_failed", "Unity has compiler errors; fix compile.errors.recent before starting test.run", BuiltInCommands.CompileStatusJson(context.State, false, 0, 0));
            }

            var reservedRunId = JsonUtil.ExtractString(context.ArgsJson, "_test_run_operation_id").Trim();
            if (!string.IsNullOrEmpty(reservedRunId) && !IsSafeTestRunId(reservedRunId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "_test_run_operation_id has an invalid format");
            }
            var callerArgsHash = JsonUtil.ExtractString(context.ArgsJson, "_test_run_caller_args_hash").Trim();
            if (!string.IsNullOrEmpty(callerArgsHash) && !IsSafeSha256Hash(callerArgsHash))
            {
                return CommandExecutionResult.Error("invalid_field_value", "_test_run_caller_args_hash has an invalid format");
            }
            var effectiveArgsHash = Sha256Prefixed(context.ArgsJson);
            var resolvedCallerArgsHash = string.IsNullOrEmpty(callerArgsHash) ? effectiveArgsHash : callerArgsHash;

            var runId = string.IsNullOrEmpty(reservedRunId)
                ? "test-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8)
                : reservedRunId;
            var artifactPath = TestRunArtifactRoot + "/" + runId + ".json";
            if (!string.IsNullOrEmpty(reservedRunId) && TestRunOperationExists(runId, artifactPath))
            {
                return TestRunBusy("The reserved test.run operation id is already active or persisted", runId, "reserved_operation_exists", runId);
            }
            var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
            var filterType = FindType("UnityEditor.TestTools.TestRunner.Api.Filter");
            var settingsType = FindType("UnityEditor.TestTools.TestRunner.Api.ExecutionSettings");
            var callbackType = FindType("UnityEditor.TestTools.TestRunner.Api.ICallbacks");
            var modeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
            var available = apiType != null && filterType != null && settingsType != null && callbackType != null && modeType != null;

            if (!available)
            {
                if (!string.IsNullOrEmpty(filter))
                {
                    return CommandExecutionResult.Error(
                        "test_filter_resolution_unavailable",
                        "test.run refused because the authoritative Unity TestRunner discovery API is unavailable; a filtered run cannot use reflection fallback",
                        TestFilterResolutionDetailsJson(mode, filter, testAssembly, "test_runner_api_unavailable", "unavailable", null));
                }

                return CommandExecutionResult.Error("unity_test_framework_unavailable", "Unity Test Framework TestRunnerApi is not available in loaded editor assemblies");
            }

            UnityTestDiscoveryCatalog.FilterResolution filterResolution = null;
            var expectedDiscoveryGeneration = JsonUtil.ExtractInt(context.ArgsJson, "_test_discovery_generation", 0);
            if (!string.IsNullOrEmpty(filter))
            {
                filterResolution = UnityTestDiscoveryCatalog.Resolve(mode, filter, testAssembly);
                if (expectedDiscoveryGeneration > 0 && filterResolution.DiscoveryGeneration != expectedDiscoveryGeneration)
                {
                    return CommandExecutionResult.Error(
                        "catalog_stale",
                        "test.run refused because the authoritative TestRunner discovery generation changed before Execute",
                        "{\"expected_discovery_generation\":" + expectedDiscoveryGeneration
                            + ",\"observed_discovery_generation\":" + filterResolution.DiscoveryGeneration
                            + ",\"resolution_status\":" + JsonUtil.String(filterResolution.Status)
                            + ",\"test_runner_execute_invoked\":false"
                            + ",\"write_command_repeated\":false"
                            + ",\"write_replay_allowed\":false}");
                }
                if (!filterResolution.IsResolved)
                {
                    return TestFilterResolutionError(filterResolution);
                }
            }
            else if (!dryRun)
            {
                var modeCatalog = UnityTestDiscoveryCatalog.Query(mode, string.Empty, testAssembly, 0, 1);
                var modeCatalogRefusal = RequireModeOnlyTestCatalog(modeCatalog, mode, testAssembly, expectedDiscoveryGeneration);
                if (modeCatalogRefusal != null) return modeCatalogRefusal;
            }

            var resolvedAssembly = filterResolution == null || string.IsNullOrEmpty(filterResolution.ResolvedAssembly)
                ? testAssembly
                : filterResolution.ResolvedAssembly;

            if (dryRun)
            {
                var dryRunState = new TestRunState(runId, mode, filter, artifactPath, maxSeconds, true)
                {
                    AllowUnsavedSceneChanges = allowUnsavedSceneChanges,
                    ArgsHash = effectiveArgsHash,
                    CallerArgsHash = resolvedCallerArgsHash,
                    CreatedAt = DateTime.UtcNow.ToString("o"),
                    TestAssembly = resolvedAssembly,
                    Status = "dry_run",
                    ResultState = "dry_run",
                    Finished = true,
                    FinishedAt = DateTime.UtcNow,
                    Success = true,
                };
                CaptureUnsavedSceneWarning(context.State, dryRunState);
                if (filterResolution == null)
                {
                    CaptureMatchedTests(dryRunState, ModeOnlyDiscovery(mode));
                }
                else
                {
                    CaptureAuthoritativeMatchedTests(dryRunState, filterResolution);
                }
                return CommandExecutionResult.Ok(TestRunStateJson(dryRunState));
            }

            lock (s_testRunsGate)
            {
                if (!string.IsNullOrEmpty(s_activeTestRunId))
                {
                    return TestRunBusy("A Unity test run is already active or awaiting callback restoration", s_activeTestRunId, "active_or_callback_restoration", runId);
                }

                foreach (var active in s_testRuns.Values)
                {
                    if (IsTestRunInProgressStatus(active.Status))
                    {
                        return TestRunBusy("A Unity test run is already running", active.RunId, active.Status, runId);
                    }
                }
            }

            var state = new TestRunState(runId, mode, filter, artifactPath, maxSeconds, false)
            {
                AllowUnsavedSceneChanges = allowUnsavedSceneChanges,
                ArgsHash = effectiveArgsHash,
                CallerArgsHash = resolvedCallerArgsHash,
                CreatedAt = DateTime.UtcNow.ToString("o"),
                TestAssembly = resolvedAssembly,
                CallbackRegistrationEpoch = 1,
            };
            CaptureUnsavedSceneWarning(context.State, state);
            if (filterResolution == null)
            {
                CaptureMatchedTests(state, ModeOnlyDiscovery(mode));
            }
            else
            {
                CaptureAuthoritativeMatchedTests(state, filterResolution);
            }
            lock (s_testRunsGate)
            {
                s_testRuns[runId] = state;
            }
            RegisterTestRunUpdate();
            var executeEntered = false;
            var preExecuteDirtySceneResult = RequireCleanScenesForTestRun(context.State, allowUnsavedSceneChanges, "pre_execute");
            if (preExecuteDirtySceneResult != null)
            {
                lock (s_testRunsGate)
                {
                    s_testRuns.Remove(runId);
                }
                return preExecuteDirtySceneResult;
            }
            try
            {
                if (!PersistTestRunSnapshot(state, "created"))
                {
                    return TestRunPreExecutePublicationFailure(state, null, null, "created");
                }
                var api = CreateTestRunnerApi(apiType);
                var callbacks = TestRunCallbacksProxy.Create(callbackType, state);
                state.Api = api;
                state.Callbacks = callbacks;
                InvokeReflected(api, "RegisterCallbacks", callbacks);
                var settings = CreateExecutionSettings(settingsType, filterType, modeType, mode, state.UnityTestNameFilters, state.TestAssembly);
                if (!PersistTestRunSnapshot(state, "callbacks_registered"))
                {
                    return TestRunPreExecutePublicationFailure(state, api, callbacks, "callbacks_registered");
                }
                SetActiveTestRunSession(runId);
                var preparedResult = DispatchPreparedTestRun(state,
                    () => RequireSettledCompilationForTestRun(context.State),
                    () => InvokeReflected(api, "Execute", settings), out executeEntered);
                if (preparedResult != null)
                {
                    return preparedResult;
                }
            }
            catch (Exception exception)
            {
                var unwrapped = UnwrapTargetInvocation(exception);
                if (executeEntered || state.ExecuteDispatchCount == 1)
                {
                    state.Status = "execution_may_have_started";
                    state.ResultState = "execution_may_have_started";
                    state.Message = "Unity TestRunner Execute was entered but did not return conclusively; the exact operation remains active and must not be replayed";
                    state.ExceptionType = unwrapped.GetType().FullName ?? unwrapped.GetType().Name;
                    state.StackTrace = Bounded(unwrapped.StackTrace, MaxFailedTestStackTraceChars);
                    PersistTestRunSnapshot(state, "execute_invocation_uncertain");
                    return CommandExecutionResult.Error("unity_test_run_delivery_uncertain", state.Message + ". Diagnostic artifact: " + artifactPath, TestRunErrorDetailsJson(state));
                }
                MarkTestRunFailed(state, "startup_failed", unwrapped);
                ClearActiveTestRunSession(runId);
                return CommandExecutionResult.Error("unity_test_run_failed", "Unity test run failed during startup: " + state.Message + ". Diagnostic artifact: " + artifactPath, TestRunErrorDetailsJson(state));
            }

            return CommandExecutionResult.Ok(TestRunStateJson(state));
        }

        // Called only after the created/callbacks_registered receipts and active marker exist.
        // The delegates let fixtures exercise this exact last gate without nested TestRunner execution.
        internal static CommandExecutionResult DispatchPreparedTestRun(TestRunState state, Func<CommandExecutionResult> finalLifecycleGate, Action execute, out bool executeEntered)
        {
            executeEntered = false;
            if (state == null || state.Finished || state.Status != "running"
                || state.Lifecycle != "callbacks_registered" || state.LastPublishedLifecycle != "callbacks_registered" || !state.ArtifactPersisted
                || state.ExecuteDispatchCount != 0 || state.ExecuteCallReturned)
            {
                throw new InvalidOperationException("The test operation has no eligible prepared receipt or already has execution evidence; it cannot be dispatched or classified as pre-Execute refused.");
            }
            var refusal = finalLifecycleGate();
            if (refusal != null)
            {
                return RetainPreExecuteRefusal(state, refusal);
            }
            state.ExecuteDispatchCount = 1;
            if (!PersistTestRunSnapshot(state, "execute_dispatching"))
            {
                state.ExecuteDispatchCount = 0;
                return TestRunPreExecutePublicationFailure(state, state.Api, state.Callbacks, "execute_dispatching");
            }
            executeEntered = true;
            execute();
            state.ExecuteCallReturned = true;
            PersistTestRunSnapshot(state, "execute_invoked");
            return null;
        }

        internal static CommandExecutionResult RetainPreExecuteRefusal(TestRunState state, CommandExecutionResult refusal)
        {
            if (state == null || state.Finished || state.Status != "running"
                || state.Lifecycle != "callbacks_registered" || state.LastPublishedLifecycle != "callbacks_registered" || !state.ArtifactPersisted
                || state.ExecuteDispatchCount != 0 || state.ExecuteCallReturned
                || refusal == null || refusal.Status != "error"
                || ExtractTopLevelJsonString(refusal.ErrorDetailsJson, "command") != "test.run"
                || ExtractTopLevelJsonBool(refusal.ErrorDetailsJson, "test_runner_execute_invoked", true))
            {
                throw new InvalidOperationException("Pre-Execute refusal requires an original error and no TestRunner execution evidence.");
            }
            state.PreExecuteRefusal = new TestRunPreExecuteRefusal(refusal.ErrorCode, ExtractTopLevelJsonString(refusal.ErrorDetailsJson, "reason"));
            state.Status = PreExecuteRefusedStatus;
            state.ResultState = PreExecuteRefusedStatus;
            state.Finished = true;
            state.FinishedAt = DateTime.UtcNow;
            state.Success = false;
            state.TerminalCountsAuthoritative = false;
            state.ClearTerminalCountEvidence();
            state.Message = Bounded(refusal.ErrorMessage, MaxFailedTestMessageChars);
            // Terminalize before unregistering: a late callback must not overwrite the refusal.
            FinalizeTestRun(state);
            return CommandExecutionResult.Error(refusal.ErrorCode, refusal.ErrorMessage, TestRunPreExecuteRefusalErrorDetailsJson(state, refusal.ErrorDetailsJson));
        }

        static string TestRunPreExecuteRefusalErrorDetailsJson(TestRunState state, string originalDetails)
        {
            // Keep the original compilation details verbatim; only append operation evidence.
            var details = string.IsNullOrWhiteSpace(originalDetails) ? "{}" : originalDetails;
            var start = details.IndexOf('{');
            var end = details.LastIndexOf('}');
            if (start < 0 || end <= start)
            {
                throw new InvalidOperationException("The compilation refusal details must be a JSON object.");
            }
            var builder = new StringBuilder(details.Substring(0, end));
            if (details.Substring(start + 1, end - start - 1).Trim().Length > 0) builder.Append(',');
            builder.Append("\"operation_created\":true,\"terminal\":true,");
            builder.Append("\"operation_id\":").Append(JsonUtil.String(state.RunId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(state.RunId)).Append(',');
            builder.Append("\"artifact_schema_version\":").Append(CurrentTestRunArtifactSchemaVersion).Append(',');
            builder.Append("\"args_hash\":").Append(JsonUtil.String(state.ArgsHash)).Append(',');
            builder.Append("\"caller_args_hash\":").Append(JsonUtil.String(state.CallerArgsHash)).Append(',');
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(state.ArtifactPath)).Append(',');
            AppendArtifactContract(builder, state.ArtifactPath, TestRunArtifactReady(state), state.ArtifactPersisted);
            builder.Append(",\"status\":").Append(JsonUtil.String(state.Status));
            builder.Append(",\"result\":").Append(JsonUtil.String(state.ResultState));
            builder.Append(",\"lifecycle\":").Append(JsonUtil.String(state.Lifecycle));
            builder.Append(",\"artifact_publication_state\":").Append(JsonUtil.String(state.ArtifactPublicationState));
            builder.Append(",\"artifact_publication_attempts\":").Append(state.ArtifactPublicationAttempts);
            builder.Append(",\"artifact_publication_deadline_utc\":").Append(JsonUtil.String(state.ArtifactPublicationDeadlineUtc.ToString("o")));
            builder.Append(",\"last_published_lifecycle\":").Append(JsonUtil.String(state.LastPublishedLifecycle));
            builder.Append(",\"artifact_publication_error_code\":").Append(JsonUtil.String(state.ArtifactPublicationErrorCode));
            builder.Append(",\"execute_dispatch_count\":0,\"execute_dispatch_committed\":false,\"test_runner_execute_invocation_attempted\":false");
            if (FindTopLevelJsonPropertyColon(details, "test_runner_execute_invoked") < 0) builder.Append(",\"test_runner_execute_invoked\":false");
            builder.Append(",\"success\":false,\"terminal_counts_authoritative\":false,\"pre_execute_refusal\":").Append(state.PreExecuteRefusal.ToJson());
            builder.Append('}');
            return builder.ToString();
        }

        static CommandExecutionResult TestRunPreExecutePublicationFailure(TestRunState state, object api, object callbacks, string lifecycle)
        {
            InvokeReflectedIfExists(api, "UnregisterCallbacks", callbacks);
            state.Status = "failed";
            state.ResultState = "artifact_publication_failed_pre_execute";
            state.Message = "Test run artifact publication failed before Execute at lifecycle " + lifecycle + "; Unity TestRunner was not invoked";
            state.Finished = true;
            state.FinishedAt = DateTime.UtcNow;
            state.Success = false;
            ClearActiveTestRunSession(state.RunId);
            PersistTestRunSnapshot(state, "artifact_publication_failed_pre_execute");
            return CommandExecutionResult.Error("test_run_artifact_publication_failed", state.Message, TestRunErrorDetailsJson(state));
        }

        static CommandExecutionResult TestRunBusy(string message, string activeOperationId, string activeOperationState, string requestedOperationId)
        {
            return CommandExecutionResult.Error(
                "unity_test_run_busy",
                message,
                "{\"operation_created\":false"
                    + ",\"test_runner_execute_invoked\":false"
                    + ",\"write_command_repeated\":false"
                    + ",\"write_replay_allowed\":false"
                    + ",\"active_operation_id\":" + JsonUtil.String(activeOperationId)
                    + ",\"active_operation_state\":" + JsonUtil.String(activeOperationState)
                    + ",\"requested_operation_id\":" + JsonUtil.String(requestedOperationId)
                    + ",\"next_valid_actions\":[\"poll_active_test_run_status\",\"inspect_the_active_test_run_artifact\",\"do_not_repeat_test.run\"]}");
        }

        [McpCommand(
            "test.run.status",
            Title = "Unity test run status",
            Description = "Poll a Unity Test Framework run started by test.run and return its bounded summary and artifact path",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TestRunStatusSchemaJson,
            Tags = new string[] { "test", "status" },
            Capability = "build_test_automation",
            ResultShape = "operation_status",
            OutputLimitJson = TestRunOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TestRunStatus(McpCommandContext context)
        {
            var operationId = JsonUtil.ExtractString(context.ArgsJson, "operation_id").Trim();
            var runIdAlias = JsonUtil.ExtractString(context.ArgsJson, "run_id").Trim();
            if (!TestRunIdsMatch(operationId, runIdAlias))
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id and run_id must identify the same test run");
            }
            var runId = string.IsNullOrEmpty(operationId) ? runIdAlias : operationId;

            if (string.IsNullOrEmpty(runId) || runId.Length > 80)
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id or run_id is required");
            }

            if (!IsSafeTestRunId(runId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id or run_id has an invalid format");
            }

            lock (s_testRunsGate)
            {
                if (s_testRuns.TryGetValue(runId, out var state))
                {
                    TryClassifyRecoveredPlayModeRunAfterExit(state, context.State);
                    return CommandExecutionResult.Ok(TestRunStateJson(state));
                }
                if (s_retainedTestRunPublications.TryGetValue(runId, out var retained))
                {
                    return CommandExecutionResult.Ok(TestRunStateJson(retained));
                }
            }

            var artifactPath = TestRunArtifactRoot + "/" + runId + ".json";
            var persisted = ReadPersistedTestRunArtifact(runId, artifactPath, out var artifactIdentityMismatch);
            if (artifactIdentityMismatch)
            {
                return CommandExecutionResult.Error("bridge_protocol_error", "Persisted test run artifact identity does not match the requested operation", TestRunArtifactIdentityMismatchDetailsJson(runId, artifactPath));
            }
            if (!string.IsNullOrEmpty(persisted))
            {
                return CommandExecutionResult.Ok(persisted);
            }

            return CommandExecutionResult.Error("test_run_not_found", "Unity test run operation was not found in memory or persisted artifacts", TestRunMissingDetailsJson(runId, artifactPath));
        }

        [McpCommand(
            "test.run.batchmode_fallback",
            Title = "Unity test run batchmode fallback",
            Description = "Return the project-local Unity batchmode test fallback contract for a test.run operation after callback loss",
            Risk = "read",
            Idempotency = "safe",
            TimeoutMs = 10000,
            SchemaJson = TestRunBatchmodeFallbackSchemaJson,
            Tags = new string[] { "test", "recovery", "batchmode" },
            Capability = "build_test_automation",
            ResultShape = "operation_status",
            OutputLimitJson = TestRunOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TestRunBatchmodeFallback(McpCommandContext context)
        {
            var runId = ExtractTestRunId(context.ArgsJson);
            if (string.IsNullOrEmpty(runId) || runId.Length > 80)
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id or run_id is required");
            }

            if (!IsSafeTestRunId(runId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id or run_id has an invalid format");
            }

            var artifactPath = TestRunArtifactRoot + "/" + runId + ".json";
            var artifactExists = TryReadRawTestRunArtifact(artifactPath, out var artifactContent, out var artifactTooLarge);
            if (!artifactExists)
            {
                return CommandExecutionResult.Error("test_run_not_found", "Unity test run operation was not found in persisted artifacts", TestRunMissingDetailsJson(runId, artifactPath));
            }

            if (artifactTooLarge)
            {
                return CommandExecutionResult.Error("test_run_artifact_too_large", "Persisted test.run artifact is too large to derive batchmode fallback details", TestRunArtifactTooLargeDetailsJson(runId, artifactPath));
            }

            var mode = PersistedTestRunMode(artifactContent);
            var filter = PersistedTestRunFilter(artifactContent);
            var testAssembly = PersistedTestRunAssembly(artifactContent);
            return CommandExecutionResult.Ok(TestRunBatchmodeFallbackResultJson(runId, artifactPath, artifactExists, artifactTooLarge, mode, filter, testAssembly));
        }

        [McpCommand(
            "test.run.cancel",
            Title = "Unity test run cancel",
            Description = "Cancel or clean up MCP tracking for a Unity Test Framework run started by test.run",
            Risk = "write",
            Idempotency = "idempotent",
            TimeoutMs = 10000,
            SchemaJson = TestRunCancelSchemaJson,
            Tags = new string[] { "test", "cancel", "recovery" },
            Capability = "build_test_automation",
            ResultShape = "operation_status",
            OutputLimitJson = TestRunOutputLimitJson,
            FeatureFlag = FeatureFlag)]
        public static CommandExecutionResult TestRunCancel(McpCommandContext context)
        {
            var runId = ExtractTestRunId(context.ArgsJson);
            if (string.IsNullOrEmpty(runId) || runId.Length > 80)
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id or run_id is required");
            }

            if (!IsSafeTestRunId(runId))
            {
                return CommandExecutionResult.Error("invalid_field_value", "operation_id or run_id has an invalid format");
            }

            var reason = Bounded(JsonUtil.ExtractString(context.ArgsJson, "reason").Trim(), 240);
            if (string.IsNullOrEmpty(reason))
            {
                reason = "MCP test.run.cancel requested";
            }

            var forgetArtifact = JsonUtil.ExtractBool(context.ArgsJson, "forget_artifact", false);
            var artifactPath = TestRunArtifactRoot + "/" + runId + ".json";
            var previousStatus = string.Empty;
            var stateJson = string.Empty;
            var inMemoryStateFound = false;
            var activeBeforeCancel = false;
            var invalidatePlayModeDiscovery = false;
            lock (s_testRunsGate)
            {
                activeBeforeCancel = string.Equals(s_activeTestRunId, runId, StringComparison.Ordinal);
                if (s_testRuns.TryGetValue(runId, out var state))
                {
                    inMemoryStateFound = true;
                    previousStatus = state.Status;
                    invalidatePlayModeDiscovery = state.Mode == "playmode";
                    CancelOrCleanupTestRunState(state, reason);
                    stateJson = TestRunStateJson(state);
                    s_testRuns.Remove(runId);
                }
            }

            ClearActiveTestRunSession(runId);
            if (invalidatePlayModeDiscovery || activeBeforeCancel) UnityTestDiscoveryCatalog.InvalidateAfterPlayModeRun();

            if (inMemoryStateFound)
            {
                var artifactDeleted = forgetArtifact && TryDeleteTestRunArtifact(artifactPath);
                return CommandExecutionResult.Ok(TestRunCancelResultJson(runId, artifactPath, previousStatus, true, true, true, artifactDeleted, reason, stateJson));
            }

            var artifactFound = File.Exists(AbsoluteProjectPath(artifactPath));
            if (!artifactFound)
            {
                return CommandExecutionResult.Error("test_run_not_found", "Unity test run operation was not found in memory or persisted artifacts", TestRunMissingDetailsJson(runId, artifactPath));
            }

            if (forgetArtifact)
            {
                var artifactDeleted = TryDeleteTestRunArtifact(artifactPath);
                return CommandExecutionResult.Ok(TestRunCancelResultJson(runId, artifactPath, "artifact_only", false, true, true, artifactDeleted, reason, string.Empty));
            }

            if (!TryReadTestRunArtifactIdentityForV3Transition(runId, artifactPath, out var argsHash, out var callerArgsHash, out var identityFailureReason))
            {
                return CommandExecutionResult.Error(
                    "test_run_status_unavailable",
                    "The persisted test.run identity is unavailable; the artifact was not overwritten",
                    "{\"operation_id\":" + JsonUtil.String(runId)
                        + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                        + ",\"reason\":" + JsonUtil.String(identityFailureReason)
                        + ",\"artifact_overwritten\":false}"
                );
            }

            stateJson = TestRunCancelledAfterReloadJson(runId, artifactPath, reason, argsHash, callerArgsHash);
            WriteArtifact(artifactPath, stateJson);
            return CommandExecutionResult.Ok(TestRunCancelResultJson(runId, artifactPath, "artifact_only", false, true, true, false, reason, stateJson));
        }

        static string BuildSettingsJson(McpCommandContext context)
        {
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 200);
            var scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
            var enabledScenes = EnabledScenePaths();

            var count = Math.Min(enabledScenes.Count, maxResults);
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"active_build_target\":").Append(JsonUtil.String(EditorUserBuildSettings.activeBuildTarget.ToString())).Append(',');
            builder.Append("\"active_build_target_group\":").Append(JsonUtil.String(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString())).Append(',');
            builder.Append("\"total_scene_count\":").Append(scenes.Length).Append(',');
            builder.Append("\"enabled_scene_count\":").Append(enabledScenes.Count).Append(',');
            builder.Append("\"truncated\":").Append(JsonUtil.Bool(enabledScenes.Count > count)).Append(',');
            builder.Append("\"scenes\":[");
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append('{');
                builder.Append("\"index\":").Append(index).Append(',');
                builder.Append("\"scene_path\":").Append(JsonUtil.String(enabledScenes[index]));
                builder.Append('}');
            }
            builder.Append(']');
            builder.Append('}');
            return builder.ToString();
        }

        static CommandExecutionResult RequireEditingStatus(string message)
        {
            if (EditorApplication.isCompiling)
            {
                return CommandExecutionResult.Error("unity_compiling", "Unity is compiling");
            }

            if (EditorApplication.isPlaying || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return CommandExecutionResult.Error("unity_busy", message);
            }

            return null;
        }

        internal static CommandExecutionResult RequireSettledCompilationForTestRun(BridgeRuntimeState state)
        {
            var barrier = ExplicitCompilationBarrier.Snapshot(string.Empty, false);
            var isUpdating = EditorApplication.isUpdating;
            var lifecycle = state == null ? null : state.CapturePackageLifecycleSnapshot();
            var packageRefreshRequired = lifecycle != null && lifecycle.PackageRefreshRequired;
            var packageRefreshWaitCanAdvance = lifecycle != null && lifecycle.PackageRefreshWaitCanAdvance;
            var backgroundCompilerProgress = lifecycle == null
                ? BackgroundCompilerProgressSnapshot.Unobserved
                : lifecycle.BackgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved;
            if (IsTestRunCompilationSettled(barrier, isUpdating, packageRefreshRequired, backgroundCompilerProgress.BlocksReadiness))
            {
                return null;
            }

            return TestRunCompilationBlockResult(
                barrier,
                isUpdating,
                packageRefreshRequired,
                packageRefreshWaitCanAdvance,
                backgroundCompilerProgress,
                lifecycle == null ? "{}" : lifecycle.PackageUpdateJson);
        }

        internal static CommandExecutionResult TestRunCompilationBlockResult(ExplicitCompilationBarrierSnapshot barrier, bool isUpdating, bool packageRefreshRequired, bool packageRefreshWaitCanAdvance, BackgroundCompilerProgressSnapshot backgroundCompilerProgress, string packageUpdateJson)
        {
            backgroundCompilerProgress = backgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved;
            var reason = TestRunCompilationBlockReason(barrier, isUpdating, packageRefreshRequired, packageRefreshWaitCanAdvance, backgroundCompilerProgress);
            var code = reason == "background_compilation_failed" ? "background_compilation_failed"
                : reason == "background_compilation_unavailable" ? "background_compilation_unavailable"
                : reason == "compile_freshness_unavailable" ? "compile_freshness_unavailable"
                : reason == "package_refresh_required" ? "package_refresh_required"
                : "unity_reloading";
            var message = reason == "background_compilation_failed"
                ? "test.run was not admitted because correlated Unity background shader or compute compilation failed"
                : reason == "background_compilation_unavailable"
                    ? "test.run was not admitted because authoritative Unity background compiler progress is unavailable"
                : reason == "background_compilation_pending"
                    ? "test.run was not admitted while Unity background shader or compute compilation is active or settling"
                : reason == "compile_freshness_unavailable"
                ? "test.run was not admitted because compilation lifecycle evidence is unavailable"
                : reason == "asset_update_active" ? "test.run was not admitted while Unity is updating assets or packages"
                : reason == "package_refresh_required" ? "test.run was not admitted because package changes require a new explicit refresh"
                : reason == "package_refresh_pending" ? "test.run was not admitted while package validation is pending"
                : "test.run was not admitted while compilation or its correlated domain reload is settling";
            var details = "{\"reason\":" + JsonUtil.String(reason)
                + ",\"command\":\"test.run\""
                + ",\"command_dispatched\":false"
                + ",\"test_runner_execute_invoked\":false"
                + ",\"write_command_repeated\":false"
                + ",\"write_replay_allowed\":false"
                + ",\"is_updating\":" + JsonUtil.Bool(isUpdating)
                + ",\"package_refresh_required\":" + JsonUtil.Bool(packageRefreshRequired)
                + ",\"package_refresh_wait_can_advance\":" + JsonUtil.Bool(packageRefreshWaitCanAdvance)
                + ",\"package_update\":" + (string.IsNullOrEmpty(packageUpdateJson) ? "{}" : packageUpdateJson)
                + ",\"background_compiler_progress\":" + backgroundCompilerProgress.ToJson()
                + ",\"compile_barrier\":" + (barrier == null ? "{}" : barrier.ToJson())
                + (reason == "package_refresh_required"
                    ? ",\"next_valid_actions\":[\"compile.status\",\"run_compile.refresh_and_wait_only_as_a_new_explicit_action\",\"do_not_auto_repeat_test.run\"]}"
                    : ",\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\",\"do_not_auto_repeat_test.run\"]}");
            return CommandExecutionResult.Error(code, message, details);
        }

        internal static string TestRunCompilationBlockReason(ExplicitCompilationBarrierSnapshot barrier, bool isUpdating, bool packageRefreshRequired, bool packageRefreshWaitCanAdvance, BackgroundCompilerProgressSnapshot backgroundCompilerProgress)
        {
            backgroundCompilerProgress = backgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved;
            if (backgroundCompilerProgress.Failed) return "background_compilation_failed";
            if (backgroundCompilerProgress.Observed && !backgroundCompilerProgress.Authoritative) return "background_compilation_unavailable";
            if (backgroundCompilerProgress.BlocksReadiness) return "background_compilation_pending";
            if (barrier == null || barrier.Unavailable) return "compile_freshness_unavailable";
            if (isUpdating) return "asset_update_active";
            if (packageRefreshRequired && !packageRefreshWaitCanAdvance) return "package_refresh_required";
            return packageRefreshRequired ? "package_refresh_pending" : "compile_lifecycle_settling";
        }

        internal static bool IsTestRunCompilationSettled(ExplicitCompilationBarrierSnapshot barrier, bool isUpdating, bool packageRefreshRequired)
        {
            return IsTestRunCompilationSettled(barrier, isUpdating, packageRefreshRequired, false);
        }

        internal static bool IsTestRunCompilationSettled(ExplicitCompilationBarrierSnapshot barrier, bool isUpdating, bool packageRefreshRequired, bool backgroundCompilerBlocked)
        {
            return barrier != null && !barrier.Settling && !barrier.Unavailable && !isUpdating && !packageRefreshRequired && !backgroundCompilerBlocked;
        }

        static List<string> EnabledScenePaths()
        {
            var enabledScenes = new List<string>();
            var scenes = EditorBuildSettings.scenes ?? new EditorBuildSettingsScene[0];
            foreach (var scene in scenes)
            {
                if (scene != null && scene.enabled && !string.IsNullOrEmpty(scene.path))
                {
                    enabledScenes.Add(scene.path);
                }
            }

            return enabledScenes;
        }

        static void ExecuteBuildRun(BuildRunState state)
        {
            FileStream outputReservation = null;
            var outputReservationPath = string.Empty;
            try
            {
                var statusResult = RequireEditingStatus("Build runs require editing status");
                if (statusResult != null)
                {
                    CompleteBuildRunFailure(state, "pre_execute_editor_not_ready", "Unity left editing status before build execution");
                    return;
                }
                if (!state.AllowUnsavedSceneChanges)
                {
                    var unsavedResult = BuiltInCommands.RequireNoUnsavedSceneChanges(state.RuntimeState, "build.run", "build.run");
                    if (unsavedResult != null)
                    {
                        CompleteBuildRunFailure(state, "pre_execute_dirty_scenes", "Loaded scenes became dirty before build execution");
                        return;
                    }
                }
                if (ConsoleLogBuffer.HasCompileErrors())
                {
                    CompleteBuildRunFailure(state, "pre_execute_compilation_failed", "Unity has compiler errors before build execution");
                    return;
                }
                if (!TryPrepareBuildOutput(state.OutputPath, state.OutputRoot, state.OutputSelection, state.OperationId, state.BuildName, state.Target, out var fullOutputPath, out outputReservation, out outputReservationPath, out var outputError))
                {
                    CompleteBuildRunFailure(state, "unsafe_build_output", outputError);
                    return;
                }
                if (!TryValidateBuildOutputPath(state.OutputPath, state.OutputRoot, state.OutputSelection, state.OperationId, state.BuildName, state.Target, false, out outputError))
                {
                    CompleteBuildRunFailure(state, "unsafe_build_output", outputError);
                    return;
                }

                var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
                {
                    scenes = state.Scenes.ToArray(),
                    locationPathName = fullOutputPath,
                    target = state.Target,
                    targetGroup = state.TargetGroup,
                    options = state.Options,
                });
                ReleaseBuildOutputReservation(ref outputReservation, ref outputReservationPath);
                state.Result = "Unknown";
                if (report != null)
                {
                    var summary = report.summary;
                    state.Result = summary.result.ToString();
                    state.DurationMs = (long)Math.Max(0, summary.totalTime.TotalMilliseconds);
                    state.TotalSize = summary.totalSize > (ulong)long.MaxValue ? long.MaxValue : (long)summary.totalSize;
                    state.TotalErrors = summary.totalErrors;
                    state.TotalWarnings = summary.totalWarnings;
                    state.TerminalEvidenceAuthoritative = true;
                }
                state.OutputExists = File.Exists(fullOutputPath) || Directory.Exists(fullOutputPath);
                state.OutputKind = Directory.Exists(fullOutputPath) ? "directory" : File.Exists(fullOutputPath) ? "file" : "missing";
                state.Status = "completed";
                ApplyBuildRunReceiptClassification(state);
                state.CompletedAt = DateTime.UtcNow.ToString("o");
                PersistTerminalBuildRun(state);
            }
            catch (Exception exception)
            {
                CompleteBuildRunFailure(state, "unity_build_failed", exception.Message);
            }
            finally
            {
                ReleaseBuildOutputReservation(ref outputReservation, ref outputReservationPath);
                ClearActiveBuildRun(state.OperationId);
            }
        }

        static void CompleteBuildRunFailure(BuildRunState state, string result, string message)
        {
            state.Status = "completed";
            state.Success = false;
            state.Result = result;
            state.Message = Bounded(message, 2000);
            ApplyBuildRunReceiptClassification(state);
            state.CompletedAt = DateTime.UtcNow.ToString("o");
            PersistTerminalBuildRun(state);
        }

        static void PersistTerminalBuildRun(BuildRunState state)
        {
            var json = BuildRunStateJson(state, false);
            if (!TryWriteArtifactAtomic(state.ArtifactPath, json, out var error))
            {
                Debug.LogError("SGG Unity MCP could not publish terminal build.run receipt: " + error);
            }
        }

        static string BuildRunStateJson(BuildRunState state, bool dryRun)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"build.run\",");
            builder.Append("\"artifact_schema_version\":").Append(BuildRunArtifactSchemaVersion).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(state.OperationId)).Append(',');
            builder.Append("\"args_hash\":").Append(JsonUtil.String(state.ArgsHash)).Append(',');
            builder.Append("\"caller_args_hash\":").Append(JsonUtil.String(state.CallerArgsHash)).Append(',');
            builder.Append("\"package_version\":").Append(JsonUtil.String(UnityBridgeRuntime.PackageVersion)).Append(',');
            builder.Append("\"unity_version\":").Append(JsonUtil.String(Application.unityVersion)).Append(',');
            builder.Append("\"created_at\":").Append(JsonUtil.String(state.CreatedAt)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(state.Status)).Append(',');
            builder.Append("\"dry_run\":").Append(JsonUtil.Bool(dryRun)).Append(',');
            if (dryRun)
            {
                builder.Append("\"would_build\":true,");
            }

            if (state.Status != "running")
            {
                builder.Append("\"success\":").Append(JsonUtil.Bool(state.Success)).Append(',');
            }
            builder.Append("\"result\":").Append(JsonUtil.String(state.Result)).Append(',');
            builder.Append("\"active_build_target\":").Append(JsonUtil.String(state.Target.ToString())).Append(',');
            builder.Append("\"active_build_target_group\":").Append(JsonUtil.String(state.TargetGroup.ToString())).Append(',');
            builder.Append("\"build_target\":").Append(JsonUtil.String(state.Target.ToString())).Append(',');
            builder.Append("\"build_target_group\":").Append(JsonUtil.String(state.TargetGroup.ToString())).Append(',');
            builder.Append("\"development\":").Append(JsonUtil.Bool(state.Development)).Append(',');
            builder.Append("\"build_name\":").Append(JsonUtil.String(state.BuildName)).Append(',');
            builder.Append("\"build_output_path\":").Append(JsonUtil.String(state.OutputPath)).Append(',');
            builder.Append("\"build_output_root\":").Append(JsonUtil.String(state.OutputRoot)).Append(',');
            builder.Append("\"output_selection\":").Append(JsonUtil.String(state.OutputSelection)).Append(',');
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(state.ArtifactPath)).Append(',');
            AppendArtifactContract(builder, state.ArtifactPath, !dryRun);
            builder.Append(',');
            builder.Append("\"scene_count\":").Append(state.Scenes.Count).Append(',');
            builder.Append("\"scenes_truncated\":").Append(JsonUtil.Bool(state.Scenes.Count > MaxBuildRunScenes)).Append(',');
            builder.Append("\"total_errors\":").Append(state.TotalErrors).Append(',');
            builder.Append("\"total_warnings\":").Append(state.TotalWarnings).Append(',');
            builder.Append("\"total_size_bytes\":").Append(state.TotalSize).Append(',');
            builder.Append("\"duration_ms\":").Append(state.DurationMs).Append(',');
            builder.Append("\"output_exists\":").Append(JsonUtil.Bool(state.OutputExists)).Append(',');
            builder.Append("\"output_kind\":").Append(JsonUtil.String(state.OutputKind)).Append(',');
            builder.Append("\"terminal_evidence_authoritative\":").Append(JsonUtil.Bool(state.TerminalEvidenceAuthoritative)).Append(',');
            builder.Append("\"receipt_consistency\":").Append(JsonUtil.String(state.ReceiptConsistency)).Append(',');
            builder.Append("\"receipt_consistency_conflict\":").Append(JsonUtil.String(state.ReceiptConsistencyConflict)).Append(',');
            builder.Append("\"receipt_conflicts\":");
            AppendStringArray(builder, state.ReceiptConflicts, 2);
            builder.Append(',');
            if (!string.IsNullOrEmpty(state.CompletedAt)) builder.Append("\"completed_at\":").Append(JsonUtil.String(state.CompletedAt)).Append(',');
            if (!string.IsNullOrEmpty(state.Message)) builder.Append("\"message\":").Append(JsonUtil.String(state.Message)).Append(',');
            builder.Append("\"failure_summary\":");
            builder.Append(state.Status == "running" ? "null" : BuildFailureSummaryJson(state.Success, state.TotalErrors, state.TotalWarnings, state.TotalSize, state.DurationMs, ConsoleLogBuffer.HasCompileErrors(), state.RuntimeState, state.AllowUnsavedSceneChanges, state.ReceiptConsistency, state.ReceiptConsistencyConflict, state.ReceiptConflicts));
            builder.Append(',');
            builder.Append("\"next_valid_actions\":");
            AppendStringArray(builder, state.Status == "running" ? new List<string> { "build.run.status", "job.artifact.read" } : BuildRunNextValidActions(dryRun, state.Success, ConsoleLogBuffer.HasCompileErrors(), state.RuntimeState != null && state.RuntimeState.HasUnsavedSceneChanges, state.OutputSelection == "caller_absolute_root"));
            builder.Append(',');
            builder.Append("\"scenes\":");
            AppendStringArray(builder, state.Scenes, MaxBuildRunScenes);
            builder.Append('}');
            return builder.ToString();
        }

        static string BuildFailureSummaryJson(bool success, int totalErrors, int totalWarnings, long totalSize, long durationMs, bool hasCompileErrors, BridgeRuntimeState runtimeState, bool allowUnsavedSceneChanges, string receiptConsistency, string receiptConsistencyConflict, List<string> receiptConflicts)
        {
            return "{\"failed\":" + JsonUtil.Bool(!success)
                + ",\"compiler_errors\":" + JsonUtil.Bool(hasCompileErrors)
                + ",\"build_errors\":" + Math.Max(0, totalErrors)
                + ",\"build_warnings\":" + Math.Max(0, totalWarnings)
                + ",\"failed_tests\":0"
                + ",\"duration_ms\":" + Math.Max(0, durationMs)
                + ",\"output_size_bytes\":" + Math.Max(0, totalSize)
                + ",\"receipt_consistency\":" + JsonUtil.String(receiptConsistency)
                + ",\"receipt_consistency_conflict\":" + JsonUtil.String(receiptConsistencyConflict)
                + ",\"receipt_conflicts\":" + BuildRunReceiptConflictsJson(receiptConflicts)
                + ",\"dirty_scene_preflight\":" + DirtyScenePreflightJson(runtimeState, allowUnsavedSceneChanges)
                + "}";
        }

        static void ApplyBuildRunReceiptClassification(BuildRunState state)
        {
            var classification = ClassifyBuildRunReceipt(state.Status, state.TerminalEvidenceAuthoritative, state.Result, state.TotalErrors, state.OutputExists);
            state.Success = classification.Success;
            state.ReceiptConsistency = classification.ReceiptConsistency;
            state.ReceiptConsistencyConflict = classification.ReceiptConsistencyConflict;
            state.ReceiptConflicts.Clear();
            state.ReceiptConflicts.AddRange(classification.ReceiptConflicts);
            if (!state.Success && string.IsNullOrEmpty(state.Message))
            {
                state.Message = BuildRunReceiptClassificationMessage(classification);
            }
        }

        internal static BuildRunReceiptClassification ClassifyBuildRunReceipt(string status, bool terminalEvidenceAuthoritative, string result, int totalErrors, bool outputExists)
        {
            if (!string.Equals(status, "completed", StringComparison.Ordinal))
            {
                return new BuildRunReceiptClassification(false, status == "running" ? "pending" : "not_applicable", string.Empty, Array.Empty<string>());
            }
            var conflicts = new List<string>(2);
            if (string.Equals(result, "Succeeded", StringComparison.Ordinal) && totalErrors > 0) conflicts.Add("nonzero_total_errors");
            if (string.Equals(result, "Succeeded", StringComparison.Ordinal) && !outputExists) conflicts.Add("output_missing");
            var conflictCode = !terminalEvidenceAuthoritative
                ? "terminal_evidence_unavailable"
                : conflicts.Count == 2
                    ? "nonzero_total_errors_and_output_missing"
                    : conflicts.Count == 1 ? conflicts[0] : string.Empty;
            var success = terminalEvidenceAuthoritative
                && string.Equals(result, "Succeeded", StringComparison.Ordinal)
                && totalErrors == 0
                && outputExists;
            return new BuildRunReceiptClassification(success, string.IsNullOrEmpty(conflictCode) ? "consistent" : "conflict", conflictCode, conflicts.ToArray());
        }

        static string BuildRunReceiptClassificationMessage(BuildRunReceiptClassification classification)
        {
            if (classification.ReceiptConsistencyConflict == "nonzero_total_errors_and_output_missing")
            {
                return "Unity reported Succeeded with nonzero BuildSummary.totalErrors and without the reserved output; the receipt was normalized to non-success";
            }
            if (classification.ReceiptConsistencyConflict == "nonzero_total_errors")
            {
                return "Unity reported Succeeded with nonzero BuildSummary.totalErrors; the receipt was normalized to non-success";
            }
            if (classification.ReceiptConsistencyConflict == "output_missing")
            {
                return "Unity reported Succeeded but the reserved output does not exist; the receipt was normalized to non-success";
            }
            if (classification.ReceiptConsistencyConflict == "terminal_evidence_unavailable")
            {
                return "Unity did not provide authoritative terminal BuildReport evidence; the receipt was normalized to non-success";
            }
            return string.Empty;
        }

        static string BuildRunReceiptConflictsJson(List<string> receiptConflicts)
        {
            var builder = new StringBuilder();
            AppendStringArray(builder, receiptConflicts, 2);
            return builder.ToString();
        }

        static List<string> BuildRunNextValidActions(bool dryRun, bool success, bool hasCompileErrors, bool hasUnsavedSceneChanges, bool externalOutput)
        {
            var actions = new List<string>();
            if (dryRun)
            {
                actions.Add("build.run");
                actions.Add("build.settings");
            }
            else if (success)
            {
                if (!externalOutput) actions.Add("artifact.metadata");
                actions.Add("job.artifact.read");
                actions.Add("build.settings");
            }
            else
            {
                actions.Add("job.artifact.read");
                actions.Add("compile.status");
                actions.Add("compile.errors.recent");
                actions.Add("logs.console.query");
            }

            if (hasCompileErrors && !actions.Contains("compile.errors.recent"))
            {
                actions.Add("compile.errors.recent");
            }

            if (hasUnsavedSceneChanges)
            {
                actions.Add("editor.save_prompt.get");
            }

            return actions;
        }

        static string BuildOutputPath(string buildId, BuildTarget target, string buildName, string outputRoot, bool externalOutput)
        {
            if (externalOutput)
            {
                var operationRoot = Path.Combine(outputRoot, buildId);
                return UsesDirectoryOutput(target) ? operationRoot : Path.Combine(operationRoot, BuildOutputFileName(target, buildName));
            }

            var defaultOperationRoot = BuildOutputRoot + "/" + buildId;
            return UsesDirectoryOutput(target) ? defaultOperationRoot : defaultOperationRoot + "/" + BuildOutputFileName(target, buildName);
        }

        static bool UsesDirectoryOutput(BuildTarget target)
        {
            return UsesDirectoryOutput(target.ToString());
        }

        static bool UsesDirectoryOutput(string name)
        {
            return name.IndexOf("iOS", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("WebGL", StringComparison.OrdinalIgnoreCase) >= 0
                || name.IndexOf("tvOS", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string BuildOutputFileName(BuildTarget target, string buildName)
        {
            var targetName = target.ToString();
            if (targetName.IndexOf("Windows", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return buildName + ".exe";
            }

            if (targetName.IndexOf("OSX", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return buildName + ".app";
            }

            if (targetName.IndexOf("Android", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return buildName + ".apk";
            }

            return buildName;
        }

        static string OutputDirectory(string fullOutputPath, BuildTarget target)
        {
            if (UsesDirectoryOutput(target))
            {
                return fullOutputPath;
            }

            return Path.GetDirectoryName(fullOutputPath) ?? fullOutputPath;
        }

        static bool TryValidateBuildOutputPath(string outputPath, string outputRoot, string outputSelection, string operationId, string buildName, BuildTarget target, bool requireMissingDestination, out string error)
        {
            error = string.Empty;
            try
            {
                if (outputSelection == "caller_absolute_root")
                {
                    if (!TryNormalizeExternalBuildOutputRoot(outputRoot, out var normalizedRoot, out error))
                    {
                        return false;
                    }

                    var expectedOutputPath = Path.GetFullPath(BuildOutputPath(operationId, target, buildName, normalizedRoot, true));
                    var fullExternalOutputPath = Path.GetFullPath(outputPath);
                    var externalComparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                    if (!string.Equals(expectedOutputPath, fullExternalOutputPath, externalComparison))
                    {
                        error = "Caller-selected build output no longer matches its confirmed root and reserved operation id";
                        return false;
                    }

                    var externalOperationDirectory = Path.Combine(normalizedRoot, operationId);
                    if (HasExistingReparsePointBelowRoot(fullExternalOutputPath, normalizedRoot))
                    {
                        error = "Caller-selected build output path contains a symlink or reparse point";
                        return false;
                    }
                    if (requireMissingDestination && (File.Exists(externalOperationDirectory) || Directory.Exists(externalOperationDirectory)))
                    {
                        error = "Reserved build output destination already exists";
                        return false;
                    }
                    if (requireMissingDestination && !UsesDirectoryOutput(target) && (File.Exists(fullExternalOutputPath) || Directory.Exists(fullExternalOutputPath)))
                    {
                        error = "Reserved build output leaf already exists";
                        return false;
                    }
                    if (UsesDirectoryOutput(target) && !string.Equals(externalOperationDirectory, fullExternalOutputPath, externalComparison))
                    {
                        error = "Directory build target resolved an unexpected output shape";
                        return false;
                    }
                    return true;
                }

                if (outputSelection != "project_default" || !string.Equals(outputRoot, BuildOutputRoot, StringComparison.Ordinal))
                {
                    error = "Build output selection is invalid";
                    return false;
                }

                var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var defaultOutputRoot = Path.GetFullPath(Path.Combine(projectRoot, BuildOutputRoot.Replace('/', Path.DirectorySeparatorChar))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var fullOutputPath = Path.GetFullPath(Path.Combine(projectRoot, outputPath.Replace('/', Path.DirectorySeparatorChar)));
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!fullOutputPath.StartsWith(defaultOutputRoot + Path.DirectorySeparatorChar, comparison))
                {
                    error = "Build output must remain under " + BuildOutputRoot;
                    return false;
                }

                var relativeBelowRoot = fullOutputPath.Substring(defaultOutputRoot.Length + 1);
                var separatorIndex = relativeBelowRoot.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
                var operationDirectoryName = separatorIndex < 0 ? relativeBelowRoot : relativeBelowRoot.Substring(0, separatorIndex);
                if (!IsSafeBuildRunId(operationDirectoryName))
                {
                    error = "Build output does not contain a valid reserved operation directory";
                    return false;
                }
                var operationDirectory = Path.Combine(defaultOutputRoot, operationDirectoryName);
                if (HasExistingReparsePointBelowRoot(defaultOutputRoot, projectRoot) || HasExistingReparsePointBelowRoot(fullOutputPath, defaultOutputRoot))
                {
                    error = "Build output path contains a symlink or reparse point";
                    return false;
                }
                if (requireMissingDestination && (File.Exists(operationDirectory) || Directory.Exists(operationDirectory)))
                {
                    error = "Reserved build output destination already exists";
                    return false;
                }
                if (UsesDirectoryOutput(target) && !string.Equals(operationDirectory, fullOutputPath, comparison))
                {
                    error = "Directory build target resolved an unexpected output shape";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "Build output path could not be validated safely: " + Bounded(exception.Message, 500);
                return false;
            }
        }

        static bool TryPrepareBuildOutput(string outputPath, string outputRoot, string outputSelection, string operationId, string buildName, BuildTarget target, out string fullOutputPath, out FileStream reservation, out string reservationPath, out string error)
        {
            fullOutputPath = string.Empty;
            reservation = null;
            reservationPath = string.Empty;
            if (!TryValidateBuildOutputPath(outputPath, outputRoot, outputSelection, operationId, buildName, target, true, out error))
            {
                return false;
            }
            try
            {
                fullOutputPath = outputSelection == "caller_absolute_root"
                    ? Path.GetFullPath(outputPath)
                    : Path.GetFullPath(AbsoluteProjectPath(outputPath));
                var directoryToCreate = OutputDirectory(fullOutputPath, target);
                if (string.IsNullOrEmpty(directoryToCreate))
                {
                    error = "Build output parent directory is unavailable";
                    return false;
                }
                Directory.CreateDirectory(directoryToCreate);
                if (!TryValidateBuildOutputPath(outputPath, outputRoot, outputSelection, operationId, buildName, target, false, out error))
                {
                    if (string.IsNullOrEmpty(error)) error = "Build output path became redirected while reserving the destination";
                    return false;
                }
                if (outputSelection == "caller_absolute_root")
                {
                    var candidateReservationPath = Path.Combine(OutputDirectory(fullOutputPath, target), ".sgg-unity-mcp-build-reservation");
                    reservation = new FileStream(candidateReservationPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
                    reservationPath = candidateReservationPath;
                    var reservationBytes = Encoding.UTF8.GetBytes(operationId);
                    reservation.Write(reservationBytes, 0, reservationBytes.Length);
                    reservation.Flush(true);
                    if (!TryValidateBuildOutputPath(outputPath, outputRoot, outputSelection, operationId, buildName, target, false, out error))
                    {
                        ReleaseBuildOutputReservation(ref reservation, ref reservationPath);
                        return false;
                    }
                }
                return true;
            }
            catch (Exception exception)
            {
                ReleaseBuildOutputReservation(ref reservation, ref reservationPath);
                error = "Build output destination could not be reserved safely: " + Bounded(exception.Message, 500);
                return false;
            }
        }

        static void ReleaseBuildOutputReservation(ref FileStream reservation, ref string reservationPath)
        {
            if (reservation != null)
            {
                try { reservation.Dispose(); } catch { }
                reservation = null;
            }
            if (!string.IsNullOrEmpty(reservationPath))
            {
                try { if (File.Exists(reservationPath)) File.Delete(reservationPath); } catch { }
                reservationPath = string.Empty;
            }
        }

        internal static bool TryNormalizeExternalBuildOutputRoot(string requestedRoot, out string normalizedRoot, out string error)
        {
            normalizedRoot = string.Empty;
            error = string.Empty;
            try
            {
                if (string.IsNullOrWhiteSpace(requestedRoot))
                {
                    error = "output_root must not be empty";
                    return false;
                }
                if (requestedRoot.Length > MaxBuildOutputRootChars)
                {
                    error = "output_root must be at most " + MaxBuildOutputRootChars + " characters";
                    return false;
                }
                if (requestedRoot.IndexOf('\0') >= 0 || !Path.IsPathRooted(requestedRoot))
                {
                    error = "output_root must be an absolute local path";
                    return false;
                }
                if (Path.DirectorySeparatorChar != '\\')
                {
                    error = "Caller-selected output_root is currently supported only by the Windows Editor";
                    return false;
                }
                if (requestedRoot.StartsWith("\\\\", StringComparison.Ordinal) || requestedRoot.StartsWith("//", StringComparison.Ordinal))
                {
                    error = "UNC and device paths are not allowed for output_root";
                    return false;
                }
                if (Path.DirectorySeparatorChar == '\\'
                    && (requestedRoot.Length < 3
                        || !char.IsLetter(requestedRoot[0])
                        || requestedRoot[1] != ':'
                        || (requestedRoot[2] != '\\' && requestedRoot[2] != '/')))
                {
                    error = "output_root must use a local absolute drive path";
                    return false;
                }

                var fullRoot = Path.GetFullPath(requestedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var rawVolumeRoot = Path.GetPathRoot(fullRoot) ?? string.Empty;
                var comparableVolumeRoot = rawVolumeRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (string.IsNullOrEmpty(comparableVolumeRoot)) comparableVolumeRoot = rawVolumeRoot;
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (string.IsNullOrEmpty(rawVolumeRoot) || string.Equals(fullRoot, comparableVolumeRoot, comparison) || string.Equals(fullRoot + Path.DirectorySeparatorChar, rawVolumeRoot, comparison))
                {
                    error = "output_root must not be a filesystem root";
                    return false;
                }
                if (Path.DirectorySeparatorChar == '\\' && HasUnsafeWindowsPathSegment(fullRoot.Substring((Path.GetPathRoot(fullRoot) ?? string.Empty).Length)))
                {
                    error = "output_root contains an unsafe Windows path segment";
                    return false;
                }
                if (File.Exists(fullRoot))
                {
                    error = "output_root must be a directory path, not an existing file";
                    return false;
                }
                if (!Directory.Exists(fullRoot))
                {
                    error = "output_root must already exist as a normal local directory";
                    return false;
                }
                if (!IsSupportedWindowsLocalDrive(fullRoot, out error))
                {
                    return false;
                }

                var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var projectVolumeRoot = Path.GetPathRoot(projectRoot) ?? string.Empty;
                if (!IsSupportedWindowsLocalDrive(projectRoot, out _)
                    || HasUnsafeWindowsPathSegment(projectRoot.Substring(projectVolumeRoot.Length)))
                {
                    error = "Caller-selected output_root is unavailable when the Unity project uses a SUBST, mapped, network or 8.3 alias path";
                    return false;
                }
                if (HasExistingReparsePointBelowRoot(projectRoot, projectVolumeRoot))
                {
                    error = "Caller-selected output_root is unavailable when the Unity project path contains a symlink or reparse point";
                    return false;
                }
                if (IsSameOrBelow(fullRoot, projectRoot, comparison) || IsSameOrBelow(projectRoot, fullRoot, comparison))
                {
                    error = "Caller-selected output_root must be outside and must not contain the Unity project";
                    return false;
                }

                var rootedVolume = Path.GetPathRoot(fullRoot) ?? string.Empty;
                if (HasExistingReparsePointBelowRoot(fullRoot, rootedVolume))
                {
                    error = "output_root or an existing parent contains a symlink or reparse point";
                    return false;
                }

                normalizedRoot = fullRoot;
                return true;
            }
            catch (Exception exception)
            {
                error = "output_root could not be validated safely: " + Bounded(exception.Message, 500);
                return false;
            }
        }

        static bool IsSameOrBelow(string candidate, string root, StringComparison comparison)
        {
            return string.Equals(candidate, root, comparison)
                || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
                || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
        }

        static bool IsSupportedWindowsLocalDrive(string fullRoot, out string error)
        {
            error = string.Empty;
            try
            {
                var driveRoot = Path.GetPathRoot(fullRoot) ?? string.Empty;
                if (driveRoot.Length < 2 || new DriveInfo(driveRoot).DriveType != DriveType.Fixed)
                {
                    error = "output_root must use a fixed local drive";
                    return false;
                }
                var target = new StringBuilder(1024);
                if (QueryDosDevice(driveRoot.Substring(0, 2), target, target.Capacity) == 0)
                {
                    error = "output_root drive identity could not be verified";
                    return false;
                }
                var mapping = target.ToString();
                if (mapping.StartsWith("\\??\\", StringComparison.OrdinalIgnoreCase)
                    || mapping.IndexOf("Mup", StringComparison.OrdinalIgnoreCase) >= 0
                    || mapping.IndexOf("LanmanRedirector", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    error = "SUBST, mapped and network drives are not allowed for output_root";
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = "output_root drive identity could not be verified: " + Bounded(exception.Message, 500);
                return false;
            }
        }

        static bool HasUnsafeWindowsPathSegment(string relativePath)
        {
            var segments = relativePath.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var segment in segments)
            {
                if (segment == "." || segment == ".." || segment.EndsWith(".", StringComparison.Ordinal) || segment.EndsWith(" ", StringComparison.Ordinal)) return true;
                foreach (var character in segment)
                {
                    if (character < 32 || character == '<' || character == '>' || character == ':' || character == '"' || character == '|' || character == '?' || character == '*' || character == '~') return true;
                }
                var basename = segment.Split('.')[0];
                if (basename.Equals("CON", StringComparison.OrdinalIgnoreCase)
                    || basename.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                    || basename.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                    || basename.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                    || (basename.Length == 4 && (basename.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || basename.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && basename[3] >= '1' && basename[3] <= '9'))
                {
                    return true;
                }
            }
            return false;
        }

        static bool HasExistingReparsePointBelowRoot(string path, string root)
        {
            var canonicalRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var current = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            while (current.Length > canonicalRoot.Length)
            {
                if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, comparison))
                {
                    return true;
                }
                current = parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
            return !string.Equals(current, canonicalRoot, comparison);
        }

        static string AbsoluteProjectPath(string relativePath)
        {
            return Path.Combine(ProjectPaths.ProjectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        }

        static string SafeBuildName(string value)
        {
            var builder = new StringBuilder();
            foreach (var character in value)
            {
                if (char.IsLetterOrDigit(character) || character == '-' || character == '_' || character == '.')
                {
                    builder.Append(character);
                }
                else
                {
                    builder.Append('-');
                }
            }

            var result = builder.ToString().Trim('-', '_', '.');
            if (result.Length > 64)
            {
                result = result.Substring(0, 64).Trim('-', '_', '.');
            }

            return string.IsNullOrEmpty(result) ? "build" : result;
        }

        static void WriteArtifact(string artifactPath, string content)
        {
            var result = TryWriteFixedRootArtifactAtomic(artifactPath, content, TestRunArtifactRoot, "Test run artifact");
            if (!result.Committed)
            {
                throw new IOException(result.ErrorMessage);
            }
        }

        static bool TryWriteArtifactAtomic(string artifactPath, string content, out string error)
        {
            var result = TryWriteFixedRootArtifactAtomic(artifactPath, content, BuildArtifactRoot, "Build receipt");
            error = result.ErrorMessage;
            return result.Committed;
        }

        static FixedArtifactPublicationResult TryWriteFixedRootArtifactAtomic(string artifactPath, string content, string artifactRootPath, string artifactLabel)
        {
            string temporaryPath = null;
            try
            {
                if (!TryResolveFixedRootArtifactPath(artifactPath, artifactRootPath, artifactLabel, out var fullPath, out var error))
                {
                    return FixedArtifactPublicationResult.Permanent("artifact_path_validation_failed", error);
                }
                var directory = Path.GetDirectoryName(fullPath) ?? ProjectPaths.ProjectRoot;
                Directory.CreateDirectory(directory);
                if (!TryResolveFixedRootArtifactPath(artifactPath, artifactRootPath, artifactLabel, out fullPath, out error))
                {
                    return FixedArtifactPublicationResult.Permanent("artifact_path_validation_failed", error);
                }
                temporaryPath = fullPath + ".tmp-" + Guid.NewGuid().ToString("N");
                using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true))
                {
                    writer.Write(content);
                    writer.Flush();
                    stream.Flush(true);
                }
                if (!TryResolveFixedRootArtifactPath(artifactPath, artifactRootPath, artifactLabel, out var publicationPath, out error)
                    || !string.Equals(publicationPath, fullPath, Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    error = string.IsNullOrEmpty(error) ? artifactLabel + " path identity changed before publication" : error;
                    return FixedArtifactPublicationResult.Permanent("artifact_path_validation_failed", error);
                }
                if (Path.DirectorySeparatorChar == '\\')
                {
                    var commitResult = TryMoveFileExAtomic(temporaryPath, fullPath);
                    if (!commitResult.Committed)
                    {
                        return commitResult;
                    }
                }
                else if (File.Exists(fullPath))
                {
                    File.Replace(temporaryPath, fullPath, null);
                }
                else
                {
                    File.Move(temporaryPath, fullPath);
                }
                return FixedArtifactPublicationResult.Success();
            }
            catch (Exception exception)
            {
                return FixedArtifactPublicationResult.Permanent("artifact_write_failed", Bounded(exception.Message, 500));
            }
            finally
            {
                if (!string.IsNullOrEmpty(temporaryPath) && File.Exists(temporaryPath))
                {
                    try { File.Delete(temporaryPath); } catch { }
                }
            }
        }

        static FixedArtifactPublicationResult TryMoveFileExAtomic(string temporaryPath, string fullPath)
        {
            var errorCode = 0;
            for (var attempt = 1; attempt <= MaxWindowsArtifactCommitAttempts; attempt++)
            {
                if (MoveFileEx(temporaryPath, fullPath, MoveFileReplaceExisting | MoveFileWriteThrough))
                {
                    return FixedArtifactPublicationResult.Success();
                }
                errorCode = Marshal.GetLastWin32Error();
                if (!IsTransientWindowsArtifactCommitError(errorCode) || attempt == MaxWindowsArtifactCommitAttempts)
                {
                    var error = "Atomic publication failed with Win32 error " + errorCode;
                    return ClassifyWindowsArtifactCommitError(errorCode) == ArtifactPublicationDisposition.TransientContention
                        ? FixedArtifactPublicationResult.Transient("win32_" + errorCode, error)
                        : FixedArtifactPublicationResult.Permanent("win32_" + errorCode, error);
                }
                System.Threading.Thread.Sleep(WindowsArtifactCommitRetryDelayMs);
            }
            return FixedArtifactPublicationResult.Permanent("artifact_commit_failed", "Atomic publication failed");
        }

        internal static bool IsTransientWindowsArtifactCommitError(int errorCode)
        {
            return errorCode == 5 || errorCode == 32 || errorCode == 33 || errorCode == 1175;
        }

        internal static ArtifactPublicationDisposition ClassifyWindowsArtifactCommitError(int errorCode)
        {
            return IsTransientWindowsArtifactCommitError(errorCode)
                ? ArtifactPublicationDisposition.TransientContention
                : ArtifactPublicationDisposition.PermanentFailure;
        }

        internal enum ArtifactPublicationDisposition
        {
            Committed,
            TransientContention,
            PermanentFailure
        }

        internal readonly struct FixedArtifactPublicationResult
        {
            FixedArtifactPublicationResult(ArtifactPublicationDisposition disposition, string errorCode, string errorMessage)
            {
                Disposition = disposition;
                ErrorCode = errorCode ?? string.Empty;
                ErrorMessage = errorMessage ?? string.Empty;
            }

            public ArtifactPublicationDisposition Disposition { get; }
            public string ErrorCode { get; }
            public string ErrorMessage { get; }
            public bool Committed { get { return Disposition == ArtifactPublicationDisposition.Committed; } }
            public bool TransientContention { get { return Disposition == ArtifactPublicationDisposition.TransientContention; } }

            public static FixedArtifactPublicationResult Success()
            {
                return new FixedArtifactPublicationResult(ArtifactPublicationDisposition.Committed, string.Empty, string.Empty);
            }

            public static FixedArtifactPublicationResult Transient(string errorCode, string errorMessage)
            {
                return new FixedArtifactPublicationResult(ArtifactPublicationDisposition.TransientContention, errorCode, errorMessage);
            }

            public static FixedArtifactPublicationResult Permanent(string errorCode, string errorMessage)
            {
                return new FixedArtifactPublicationResult(ArtifactPublicationDisposition.PermanentFailure, errorCode, errorMessage);
            }
        }

        static bool TryResolveBuildArtifactPath(string artifactPath, out string fullPath, out string error)
        {
            return TryResolveFixedRootArtifactPath(artifactPath, BuildArtifactRoot, "Build receipt", out fullPath, out error);
        }

        static bool TryResolveFixedRootArtifactPath(string artifactPath, string artifactRootPath, string artifactLabel, out string fullPath, out string error)
        {
            fullPath = string.Empty;
            error = string.Empty;
            try
            {
                var projectRoot = Path.GetFullPath(ProjectPaths.ProjectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var artifactRoot = Path.GetFullPath(Path.Combine(projectRoot, artifactRootPath.Replace('/', Path.DirectorySeparatorChar))).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                fullPath = Path.GetFullPath(Path.Combine(projectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar)));
                var comparison = Path.DirectorySeparatorChar == '\\' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                if (!fullPath.StartsWith(artifactRoot + Path.DirectorySeparatorChar, comparison)
                    || HasExistingReparsePointBelowRoot(artifactRoot, projectRoot)
                    || HasExistingReparsePointBelowRoot(fullPath, artifactRoot))
                {
                    error = artifactLabel + " path is outside its fixed root or contains a symlink/reparse point";
                    fullPath = string.Empty;
                    return false;
                }
                return true;
            }
            catch (Exception exception)
            {
                error = artifactLabel + " path could not be validated safely: " + Bounded(exception.Message, 500);
                fullPath = string.Empty;
                return false;
            }
        }

        static string ReadPersistedBuildRunArtifact(string operationId, string artifactPath, out bool identityMismatch, out string readError)
        {
            identityMismatch = false;
            readError = string.Empty;
            try
            {
                if (!TryResolveBuildArtifactPath(artifactPath, out var fullPath, out readError)) return string.Empty;
                if (!File.Exists(fullPath)) return string.Empty;
                var info = new FileInfo(fullPath);
                if (info.Length <= 0 || info.Length > MaxBuildRunArtifactChars)
                {
                    return "{\"operation_id\":" + JsonUtil.String(operationId) + ",\"status\":\"artifact_too_large\",\"success\":false,\"artifact_path\":" + JsonUtil.String(artifactPath) + "}";
                }
                var content = File.ReadAllText(fullPath);
                if (!PersistedBuildRunArtifactIdentityMatches(content, operationId))
                {
                    identityMismatch = true;
                    return string.Empty;
                }
                var normalized = NormalizePersistedBuildRunArtifactForRead(content, operationId, artifactPath);
                if (string.IsNullOrEmpty(normalized))
                {
                    readError = "Persisted build.run receipt is malformed or incomplete";
                    return string.Empty;
                }
                if (string.Equals(ExtractTopLevelJsonString(content, "status"), "running", StringComparison.Ordinal) && !IsActiveBuildRun(operationId))
                {
                    return InterruptedBuildRunJson(operationId, artifactPath, normalized);
                }
                return normalized;
            }
            catch (Exception exception)
            {
                readError = Bounded(exception.Message, 500);
                return string.Empty;
            }
        }

        static string InterruptedBuildRunJson(string operationId, string artifactPath, string content)
        {
            var outputSelection = ExtractTopLevelJsonString(content, "output_selection");
            var nextActions = outputSelection == "caller_absolute_root"
                ? "[\"job.artifact.read\",\"build.settings\",\"diagnostics.report_problem\"]"
                : "[\"artifact.metadata\",\"job.artifact.read\",\"build.settings\",\"diagnostics.report_problem\"]";
            return "{\"command\":\"build.run\""
                + ",\"artifact_schema_version\":" + BuildRunArtifactSchemaVersion
                + ",\"operation_id\":" + JsonUtil.String(operationId)
                + ",\"status\":\"unknown_after_interruption\""
                + ",\"success\":false"
                + ",\"result\":\"unknown_after_interruption\""
                + ",\"build_output_path\":" + JsonUtil.String(ExtractTopLevelJsonString(content, "build_output_path"))
                + ",\"build_output_root\":" + JsonUtil.String(ExtractTopLevelJsonString(content, "build_output_root"))
                + ",\"output_selection\":" + JsonUtil.String(outputSelection)
                + ",\"build_target\":" + JsonUtil.String(ExtractTopLevelJsonString(content, "build_target"))
                + ",\"build_name\":" + JsonUtil.String(ExtractTopLevelJsonString(content, "build_name"))
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"artifact_ref\":" + JsonUtil.String(artifactPath)
                + ",\"artifact_ready\":true"
                + ",\"artifact_refs\":[" + JsonUtil.String(artifactPath) + "]"
                + ",\"readable_with\":\"job.artifact.read\""
                + ",\"terminal_evidence_authoritative\":false"
                + ",\"message\":\"The persisted running receipt survived but the originating Unity build operation is no longer active\""
                + ",\"next_valid_actions\":" + nextActions + "}";
        }

        static bool IsActiveBuildRun(string operationId)
        {
            lock (s_buildRunsGate)
            {
                return string.Equals(s_activeBuildRunId, operationId, StringComparison.Ordinal);
            }
        }

        static void ClearActiveBuildRun(string operationId)
        {
            lock (s_buildRunsGate)
            {
                if (string.Equals(s_activeBuildRunId, operationId, StringComparison.Ordinal)) s_activeBuildRunId = string.Empty;
            }
        }

        static string CreateBuildRunId()
        {
            return "build-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
        }

        internal static bool PersistedBuildRunArtifactIdentityMatches(string content, string operationId)
        {
            return IsSafeBuildRunId(operationId)
                && ExtractTopLevelJsonInt(content, "artifact_schema_version", 0) == BuildRunArtifactSchemaVersion
                && string.Equals(ExtractTopLevelJsonString(content, "operation_id"), operationId, StringComparison.Ordinal);
        }

        internal static string NormalizePersistedBuildRunArtifactForRead(string content, string operationId, string artifactPath)
        {
            var status = ExtractTopLevelJsonString(content, "status");
            var result = ExtractTopLevelJsonString(content, "result");
            var persistedArtifactPath = ExtractTopLevelJsonString(content, "artifact_path");
            var outputPath = ExtractTopLevelJsonString(content, "build_output_path");
            var outputRoot = ExtractTopLevelJsonString(content, "build_output_root");
            var outputSelection = ExtractTopLevelJsonString(content, "output_selection");
            var buildTarget = ExtractTopLevelJsonString(content, "build_target");
            var buildName = ExtractTopLevelJsonString(content, "build_name");
            if (string.IsNullOrEmpty(outputSelection)) outputSelection = "project_default";
            if (string.IsNullOrEmpty(outputRoot) && outputSelection == "project_default") outputRoot = BuildOutputRoot;
            var validOutputPath = outputSelection == "project_default"
                ? string.Equals(outputRoot, BuildOutputRoot, StringComparison.Ordinal)
                    && IsSafePersistedDefaultBuildOutputPath(outputPath, operationId)
                : outputSelection == "caller_absolute_root"
                    && !string.IsNullOrEmpty(buildName)
                    && string.Equals(buildName, SafeBuildName(buildName), StringComparison.Ordinal)
                    && Enum.TryParse(buildTarget, out BuildTarget parsedTarget)
                    && Enum.IsDefined(typeof(BuildTarget), parsedTarget)
                    && TryValidateBuildOutputPath(outputPath, outputRoot, outputSelection, operationId, buildName, parsedTarget, false, out _);
            if ((status != "running" && status != "completed")
                || string.IsNullOrEmpty(result)
                || !string.Equals(persistedArtifactPath, artifactPath, StringComparison.Ordinal)
                || !validOutputPath)
            {
                return string.Empty;
            }

            var totalErrors = Math.Max(0, ExtractTopLevelJsonInt(content, "total_errors", 0));
            var totalWarnings = Math.Max(0, ExtractTopLevelJsonInt(content, "total_warnings", 0));
            var totalSize = Math.Max(0, ExtractTopLevelJsonLong(content, "total_size_bytes", 0));
            var durationMs = Math.Max(0, ExtractTopLevelJsonInt(content, "duration_ms", 0));
            var outputExists = ExtractTopLevelJsonBool(content, "output_exists", false);
            var terminalEvidenceAuthoritative = ExtractTopLevelJsonBool(content, "terminal_evidence_authoritative", false);
            var persistedSuccessClaim = ExtractTopLevelJsonBool(content, "success", false);
            var classification = ClassifyBuildRunReceipt(status, terminalEvidenceAuthoritative, result, totalErrors, outputExists);
            var normalizedSuccess = status == "completed" && persistedSuccessClaim && classification.Success;

            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"build.run\",");
            builder.Append("\"artifact_schema_version\":").Append(BuildRunArtifactSchemaVersion).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(operationId)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(status)).Append(',');
            if (status == "completed")
            {
                builder.Append("\"success\":").Append(JsonUtil.Bool(normalizedSuccess)).Append(',');
                if (persistedSuccessClaim != normalizedSuccess)
                {
                    builder.Append("\"persisted_success_claim\":").Append(JsonUtil.Bool(persistedSuccessClaim)).Append(',');
                }
            }
            builder.Append("\"result\":").Append(JsonUtil.String(result)).Append(',');
            builder.Append("\"build_output_path\":").Append(JsonUtil.String(outputPath)).Append(',');
            builder.Append("\"build_output_root\":").Append(JsonUtil.String(outputRoot)).Append(',');
            builder.Append("\"output_selection\":").Append(JsonUtil.String(outputSelection)).Append(',');
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(artifactPath)).Append(',');
            AppendArtifactContract(builder, artifactPath, true);
            builder.Append(",\"build_target\":").Append(JsonUtil.String(buildTarget));
            builder.Append(",\"build_name\":").Append(JsonUtil.String(buildName));
            builder.Append(",\"development\":").Append(JsonUtil.Bool(ExtractTopLevelJsonBool(content, "development", false)));
            builder.Append(",\"total_errors\":").Append(totalErrors);
            builder.Append(",\"total_warnings\":").Append(totalWarnings);
            builder.Append(",\"total_size_bytes\":").Append(totalSize);
            builder.Append(",\"duration_ms\":").Append(durationMs);
            builder.Append(",\"output_exists\":").Append(JsonUtil.Bool(outputExists));
            builder.Append(",\"output_kind\":").Append(JsonUtil.String(ExtractTopLevelJsonString(content, "output_kind")));
            builder.Append(",\"terminal_evidence_authoritative\":").Append(JsonUtil.Bool(terminalEvidenceAuthoritative));
            builder.Append(",\"receipt_consistency\":").Append(JsonUtil.String(classification.ReceiptConsistency));
            builder.Append(",\"receipt_consistency_conflict\":").Append(JsonUtil.String(classification.ReceiptConsistencyConflict));
            builder.Append(",\"receipt_conflicts\":").Append(BuildRunReceiptConflictsJson(new List<string>(classification.ReceiptConflicts)));
            var message = ExtractTopLevelJsonString(content, "message");
            if (string.IsNullOrEmpty(message) && status == "completed" && !normalizedSuccess)
            {
                message = BuildRunReceiptClassificationMessage(classification);
            }
            if (!string.IsNullOrEmpty(message)) builder.Append(",\"message\":").Append(JsonUtil.String(Bounded(message, 2000)));
            builder.Append(",\"failure_summary\":");
            builder.Append(status == "running" ? "null" : BuildFailureSummaryJson(normalizedSuccess, totalErrors, totalWarnings, totalSize, durationMs, false, null, false, classification.ReceiptConsistency, classification.ReceiptConsistencyConflict, new List<string>(classification.ReceiptConflicts)));
            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, status == "running"
                ? new List<string> { "build.run.status", "job.artifact.read" }
                : BuildRunNextValidActions(false, normalizedSuccess, false, false, outputSelection == "caller_absolute_root"));
            builder.Append('}');
            return builder.ToString();
        }

        static bool IsSafePersistedDefaultBuildOutputPath(string outputPath, string operationId)
        {
            if (string.IsNullOrEmpty(outputPath) || Path.IsPathRooted(outputPath) || outputPath.IndexOf('\\') >= 0) return false;
            var prefix = BuildOutputRoot + "/" + operationId;
            if (string.Equals(outputPath, prefix, StringComparison.Ordinal)) return true;
            if (!outputPath.StartsWith(prefix + "/", StringComparison.Ordinal)) return false;
            var leaf = outputPath.Substring(prefix.Length + 1);
            return leaf.Length > 0
                && leaf.IndexOf('/') < 0
                && leaf != "."
                && leaf != "..";
        }

        internal static bool IsSafeBuildRunId(string operationId)
        {
            if (string.IsNullOrEmpty(operationId) || operationId.Length != 30 || !operationId.StartsWith("build-", StringComparison.Ordinal)) return false;
            for (var index = 6; index < operationId.Length; index++)
            {
                var character = operationId[index];
                if (index == 14 || index == 21)
                {
                    if (character != '-') return false;
                }
                else if (index < 21)
                {
                    if (!char.IsDigit(character)) return false;
                }
                else if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        static string TestFrameworkStatusJson()
        {
            var typeHints = new List<string>();
            var assemblyHints = new List<string>();
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                var assemblyName = assembly.GetName().Name;
                if (ContainsTestFrameworkHint(assemblyName))
                {
                    AddUnique(assemblyHints, assemblyName, 8);
                }

                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null)
                    {
                        continue;
                    }

                    var fullName = type.FullName ?? string.Empty;
                    if (fullName == "UnityEditor.TestTools.TestRunner.Api.TestRunnerApi" || fullName == "UnityEngine.TestTools.UnityTestAttribute")
                    {
                        AddUnique(typeHints, fullName, 8);
                        AddUnique(assemblyHints, assemblyName, 8);
                    }
                }
            }

            var available = typeHints.Count > 0 || assemblyHints.Count > 0;
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"available\":").Append(JsonUtil.Bool(available)).Append(',');
            builder.Append("\"detection\":\"reflection\",");
            builder.Append("\"type_hints\":");
            AppendStringArray(builder, typeHints);
            builder.Append(',');
            builder.Append("\"package_hints\":");
            AppendStringArray(builder, assemblyHints);
            builder.Append('}');
            return builder.ToString();
        }

        static string BuildProfileListJson(McpCommandContext context)
        {
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 50), 1, 100);
            var profileType = FindBuildProfileType();
            var profiles = new List<BuildProfileInfo>();
            if (profileType != null)
            {
                var guids = AssetDatabase.FindAssets("t:" + profileType.Name);
                Array.Sort(guids, StringComparer.Ordinal);
                foreach (var guid in guids)
                {
                    var assetPath = AssetDatabase.GUIDToAssetPath(guid);
                    if (string.IsNullOrEmpty(assetPath))
                    {
                        continue;
                    }

                    var asset = AssetDatabase.LoadMainAssetAtPath(assetPath);
                    if (asset == null || !profileType.IsInstanceOfType(asset))
                    {
                        continue;
                    }

                    profiles.Add(new BuildProfileInfo(asset.name, assetPath, guid, ReflectedValue(asset, "buildTarget", "target"), ReflectedValue(asset, "subtarget", "buildSubtarget")));
                }
            }

            var count = Math.Min(profiles.Count, maxResults);
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"active_build_target\":").Append(JsonUtil.String(EditorUserBuildSettings.activeBuildTarget.ToString())).Append(',');
            builder.Append("\"active_build_target_group\":").Append(JsonUtil.String(BuildPipeline.GetBuildTargetGroup(EditorUserBuildSettings.activeBuildTarget).ToString())).Append(',');
            builder.Append("\"api_available\":").Append(JsonUtil.Bool(profileType != null)).Append(',');
            builder.Append("\"type_name\":").Append(JsonUtil.String(profileType == null ? string.Empty : profileType.FullName)).Append(',');
            builder.Append("\"max_results\":").Append(maxResults).Append(',');
            builder.Append("\"total_available\":").Append(profiles.Count).Append(',');
            builder.Append("\"truncated\":").Append(JsonUtil.Bool(profiles.Count > count)).Append(',');
            builder.Append("\"profiles\":[");
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                profiles[index].AppendJson(builder);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static string TestListJson(McpCommandContext context)
        {
            var maxResults = Clamp(JsonUtil.ExtractInt(context.ArgsJson, "max_results", 100), 1, 500);
            var query = JsonUtil.ExtractString(context.ArgsJson, "query");
            if (query == null)
            {
                query = string.Empty;
            }

            query = query.Trim();
            if (query.Length > 128)
            {
                return "{\"error\":\"query_too_long\"}";
            }

            var mode = JsonUtil.ExtractString(context.ArgsJson, "mode").Trim().ToLowerInvariant();
            var assembly = JsonUtil.ExtractString(context.ArgsJson, "assembly").Trim();
            var cursor = Math.Max(0, JsonUtil.ExtractInt(context.ArgsJson, "cursor", 0));
            var discovery = UnityTestDiscoveryCatalog.Query(mode, query, assembly, cursor, maxResults);
            TestDiscoveryResult legacyPreview = null;
            if (discovery.Status == "unavailable")
            {
                legacyPreview = DiscoverTestsLegacyBestEffort(query, mode, Math.Min(maxResults, UnityTestDiscoveryCatalog.MaxResolutionCandidates), false);
            }

            var builder = new StringBuilder();
            builder.Append("{\"status\":").Append(JsonUtil.String(discovery.Status));
            builder.Append(",\"mode\":").Append(JsonUtil.String(mode));
            builder.Append(",\"assembly\":").Append(JsonUtil.String(assembly));
            builder.Append(",\"query\":").Append(JsonUtil.String(query));
            builder.Append(",\"cursor\":").Append(discovery.Cursor);
            builder.Append(",\"returned\":").Append(discovery.Returned);
            builder.Append(",\"next_cursor\":").Append(discovery.NextCursor.HasValue ? discovery.NextCursor.Value.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"max_results\":").Append(discovery.MaxResults);
            builder.Append(",\"total_available\":");
            builder.Append(discovery.Status == "ready" ? discovery.TotalAvailable.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"runnable_leaf_count\":");
            builder.Append(discovery.Status == "ready" ? discovery.RunnableLeafCount.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"total_available_status\":").Append(JsonUtil.String(discovery.Status == "ready" ? "exact" : discovery.Status));
            builder.Append(",\"resolution_status\":").Append(JsonUtil.String(discovery.ResolutionStatus));
            builder.Append(",\"coverage_status\":").Append(JsonUtil.String(discovery.CoverageStatus));
            builder.Append(",\"discovery_generation\":").Append(discovery.DiscoveryGeneration > 0 ? discovery.DiscoveryGeneration.ToString(CultureInfo.InvariantCulture) : "null");
            builder.Append(",\"truncated\":").Append(JsonUtil.Bool(discovery.Truncated));
            builder.Append(",\"scan_incomplete\":").Append(JsonUtil.Bool(discovery.Status == "incomplete"));
            builder.Append(",\"scan_reason\":").Append(JsonUtil.String(discovery.FailureReason));
            builder.Append(",\"diagnostics\":{");
            builder.Append("\"authoritative_catalog\":").Append(JsonUtil.Bool(discovery.Status == "ready"));
            builder.Append(",\"status\":").Append(JsonUtil.String(discovery.Status));
            builder.Append(",\"reason\":").Append(JsonUtil.String(discovery.FailureReason));
            builder.Append(",\"max_nodes\":").Append(UnityTestDiscoveryCatalog.MaxNodes);
            builder.Append(",\"max_depth\":").Append(UnityTestDiscoveryCatalog.MaxDepth);
            builder.Append(",\"max_string_chars\":").Append(UnityTestDiscoveryCatalog.MaxStringChars);
            if (legacyPreview != null)
            {
                builder.Append(",\"legacy_reflection_preview\":{");
                builder.Append("\"best_effort\":true");
                builder.Append(",\"matched_count\":").Append(legacyPreview.MatchedCount);
                builder.Append(",\"coverage_status\":").Append(JsonUtil.String(legacyPreview.CoverageStatus));
                builder.Append(",\"scan_incomplete\":").Append(JsonUtil.Bool(legacyPreview.ScanIncomplete));
                builder.Append(",\"scan_reason\":").Append(JsonUtil.String(legacyPreview.ScanReason));
                builder.Append("}");
            }
            builder.Append('}');
            builder.Append(",\"diagnostic_hints\":");
            AppendStringArray(builder, CatalogDiagnosticHints(discovery));
            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, CatalogNextValidActions(discovery));
            builder.Append(",\"tests\":[");
            for (var index = 0; index < discovery.Candidates.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                discovery.Candidates[index].AppendJson(builder);
            }

            builder.Append("]}");
            return builder.ToString();
        }

        static List<string> CatalogDiagnosticHints(UnityTestDiscoveryCatalog.CatalogQueryResult discovery)
        {
            var hints = new List<string>();
            if (discovery == null)
            {
                return hints;
            }

            if (discovery.Status == "pending")
            {
                hints.Add("Authoritative Unity TestRunner discovery is pending; no zero-test or absence conclusion is available yet.");
            }
            else if (discovery.Status == "incomplete")
            {
                hints.Add("Authoritative Unity TestRunner discovery was incomplete; partial nodes are not reported as an empty catalog.");
            }
            else if (discovery.Status == "unavailable")
            {
                hints.Add("Authoritative Unity TestRunner discovery is unavailable; legacy reflection is diagnostic-only and cannot authorize test.run filters.");
            }

            if (discovery.NextCursor.HasValue)
            {
                hints.Add("Use test.list with cursor=" + discovery.NextCursor.Value.ToString(CultureInfo.InvariantCulture) + " to continue the deterministic catalog page.");
            }

            return hints;
        }

        static List<string> CatalogNextValidActions(UnityTestDiscoveryCatalog.CatalogQueryResult discovery)
        {
            if (discovery == null)
            {
                return new List<string>();
            }

            if (discovery.Status == "pending")
            {
                return new List<string> { "test.list" };
            }

            if (discovery.Status == "unavailable" && discovery.FailureReason == "retrieve_test_list_timeout")
            {
                return new List<string> { "test.framework.status", "compile.refresh_and_wait", "test.list" };
            }

            if (discovery.Status == "unavailable" || discovery.Status == "incomplete")
            {
                return new List<string> { "test.framework.status", "compile.refresh_and_wait" };
            }

            return discovery.NextCursor.HasValue ? new List<string> { "test.list" } : new List<string>();
        }

        // This scanner is retained only as a bounded diagnostic when the authoritative
        // TestRunner catalog cannot be loaded.  Its output never authorizes test.run.
        static TestDiscoveryResult DiscoverTestsLegacyBestEffort(string query, string mode, int maxResults, bool countBeyondMaxResults)
        {
            var result = new TestDiscoveryResult(query, mode, maxResults);
            var assemblies = LegacyReflectionAssemblies();
            assemblies.Sort((left, right) => string.Compare(left.GetName().Name, right.GetName().Name, StringComparison.Ordinal));
            var typesToScan = new List<TestTypeScanInfo>();
            var deadlineUtc = DateTime.UtcNow.AddMilliseconds(LegacyReflectionMaxScanMilliseconds);
            var exactQueryTypes = LegacyReflectionScanExactQueryTypes(assemblies, result, deadlineUtc, !countBeyondMaxResults);
            if (result.ScanIncomplete)
            {
                return result;
            }

            foreach (var assembly in assemblies)
            {
                if (result.ScannedAssemblies >= LegacyReflectionMaxAssembliesScanned)
                {
                    result.MarkScanIncomplete("max_assemblies");
                    break;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    result.MarkScanIncomplete("deadline");
                    break;
                }

                result.ScannedAssemblies++;
                var types = new List<Type>(SafeGetTypes(assembly));
                types.Sort((left, right) => string.Compare(left == null ? string.Empty : left.FullName, right == null ? string.Empty : right.FullName, StringComparison.Ordinal));
                foreach (var type in types)
                {
                    if (type == null)
                    {
                        continue;
                    }

                    if (exactQueryTypes.Contains(type))
                    {
                        continue;
                    }

                    if (result.ScannedTypes >= LegacyReflectionMaxTypesScanned)
                    {
                        result.MarkScanIncomplete("max_types");
                        break;
                    }

                    if (DateTime.UtcNow >= deadlineUtc)
                    {
                        result.MarkScanIncomplete("deadline");
                        break;
                    }

                    result.ScannedTypes++;
                    var typeInfo = new TestTypeScanInfo(assembly, type, TestMode(assembly), QueryRank(assembly, type, result.LowerQuery));
                    if (!string.IsNullOrEmpty(result.LowerQuery) && typeInfo.QueryRank == 0)
                    {
                        LegacyReflectionScanTestType(typeInfo, result, deadlineUtc, !countBeyondMaxResults);
                        if (result.ScanIncomplete)
                        {
                            break;
                        }
                    }
                    else
                    {
                        typesToScan.Add(typeInfo);
                    }
                }

                if (result.ScanIncomplete)
                {
                    break;
                }
            }

            if (!result.ScanIncomplete && (string.IsNullOrEmpty(result.LowerQuery) || result.Tests.Count < result.MaxResults || countBeyondMaxResults))
            {
                typesToScan.Sort(TestTypeScanInfo.Compare);
                foreach (var typeInfo in typesToScan)
                {
                    LegacyReflectionScanTestType(typeInfo, result, deadlineUtc, !countBeyondMaxResults && !string.IsNullOrEmpty(result.LowerQuery));
                    if (result.ScanIncomplete)
                    {
                        break;
                    }
                }
            }

            return result;
        }

        static List<Assembly> LegacyReflectionAssemblies()
        {
            var assemblies = new List<Assembly>(EditorAssemblyDiscovery.LoadedAssemblies());
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var assembly in assemblies)
            {
                seen.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty);
            }

            try
            {
                var editorAssembliesType = FindType("UnityEditor.Scripting.ScriptCompilation.EditorAssemblies");
                var property = editorAssembliesType?.GetProperty("loadedAssemblies", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var field = editorAssembliesType?.GetField("loadedAssemblies", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                var loaded = property != null ? property.GetValue(null, null) : field?.GetValue(null);
                if (loaded is IEnumerable editorAssemblies)
                {
                    foreach (var value in editorAssemblies)
                    {
                        if (value is Assembly assembly && seen.Add(assembly.FullName ?? assembly.GetName().Name ?? string.Empty))
                        {
                            assemblies.Add(assembly);
                        }
                    }
                }
            }
            catch
            {
                // The Editor assembly snapshot remains a bounded best-effort fallback.
            }

            return assemblies;
        }

        static HashSet<Type> LegacyReflectionScanExactQueryTypes(List<Assembly> assemblies, TestDiscoveryResult result, DateTime deadlineUtc, bool stopAtMaxResults)
        {
            var scanned = new HashSet<Type>();
            if (string.IsNullOrEmpty(result.LowerQuery))
            {
                return scanned;
            }

            var inspectedAssemblies = 0;
            foreach (var assembly in assemblies)
            {
                if (inspectedAssemblies >= LegacyReflectionMaxAssembliesScanned)
                {
                    break;
                }

                inspectedAssemblies++;
                if (DateTime.UtcNow >= deadlineUtc)
                {
                    result.MarkScanIncomplete("deadline");
                    break;
                }

                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null || !LegacyReflectionTypeNameMatchesExactQuery(type, result.LowerQuery))
                    {
                        continue;
                    }

                    scanned.Add(type);
                    result.ScannedTypes++;
                    LegacyReflectionScanTestType(new TestTypeScanInfo(assembly, type, TestMode(assembly), 0), result, deadlineUtc, stopAtMaxResults);
                    if (result.ScanIncomplete)
                    {
                        return scanned;
                    }
                }
            }

            return scanned;
        }

        static bool LegacyReflectionTypeNameMatchesExactQuery(Type type, string lowerQuery)
        {
            var fullName = (type.FullName ?? type.Name).ToLowerInvariant();
            var shortName = type.Name.ToLowerInvariant();
            return fullName.Contains(lowerQuery)
                || lowerQuery.Contains(fullName)
                || shortName.Contains(lowerQuery)
                || lowerQuery.Contains(shortName);
        }

        static void LegacyReflectionScanTestType(TestTypeScanInfo typeInfo, TestDiscoveryResult result, DateTime deadlineUtc, bool stopAtMaxResults)
        {
            if (DateTime.UtcNow >= deadlineUtc)
            {
                result.MarkScanIncomplete("deadline");
                return;
            }

            var methods = typeInfo.Type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            Array.Sort(methods, (left, right) => string.Compare(left.Name, right.Name, StringComparison.Ordinal));
            var containerMatchesQuery = string.IsNullOrEmpty(result.LowerQuery) || typeInfo.QueryRank == 0;
            foreach (var method in methods)
            {
                if (result.ScannedMethods >= LegacyReflectionMaxMethodsScanned)
                {
                    result.MarkScanIncomplete("max_methods");
                    break;
                }

                if (DateTime.UtcNow >= deadlineUtc)
                {
                    result.MarkScanIncomplete("deadline");
                    break;
                }

                result.ScannedMethods++;
                if (!containerMatchesQuery && method.Name.ToLowerInvariant().IndexOf(result.LowerQuery, StringComparison.Ordinal) < 0)
                {
                    continue;
                }

                var attribute = TestAttributeName(method);
                if (string.IsNullOrEmpty(attribute))
                {
                    continue;
                }

                var test = new TestMethodInfo(typeInfo.Assembly.GetName().Name, typeInfo.Type.FullName ?? typeInfo.Type.Name, method.Name, attribute, typeInfo.Mode);
                if (!MatchesQuery(test, result.LowerQuery) || !MatchesMode(test, result.LowerMode))
                {
                    continue;
                }

                result.MatchedCount++;
                if (result.Tests.Count < result.MaxResults)
                {
                    result.Tests.Add(test);
                }

                if (stopAtMaxResults && result.Tests.Count >= result.MaxResults)
                {
                    result.MarkScanIncomplete("max_results");
                    break;
                }
            }
        }

        internal static string TestRunStateJson(TestRunState state)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"test.run\",");
            builder.Append("\"artifact_schema_version\":").Append(CurrentTestRunArtifactSchemaVersion).Append(',');
            builder.Append("\"args_hash\":").Append(JsonUtil.String(state.ArgsHash)).Append(',');
            builder.Append("\"caller_args_hash\":").Append(JsonUtil.String(state.CallerArgsHash)).Append(',');
            builder.Append("\"max_seconds\":").Append(Math.Max(1, state.MaxSeconds)).Append(',');
            builder.Append("\"package_version\":").Append(JsonUtil.String(UnityBridgeRuntime.PackageVersion)).Append(',');
            builder.Append("\"unity_version\":").Append(JsonUtil.String(Application.unityVersion)).Append(',');
            builder.Append("\"created_at\":").Append(JsonUtil.String(state.CreatedAt)).Append(',');
            builder.Append("\"started_at\":").Append(JsonUtil.String(state.StartedAt.ToString("o"))).Append(',');
            builder.Append("\"last_update_at\":").Append(JsonUtil.String(state.LastUpdatedAt.ToString("o"))).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(state.RunId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(state.RunId)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(state.Status)).Append(',');
            builder.Append("\"lifecycle\":").Append(JsonUtil.String(state.Lifecycle)).Append(',');
            builder.Append("\"artifact_publication_state\":").Append(JsonUtil.String(state.ArtifactPublicationState)).Append(',');
            builder.Append("\"artifact_publication_attempts\":").Append(Math.Max(0, state.ArtifactPublicationAttempts)).Append(',');
            if (state.ArtifactPublicationDeadlineUtc != default(DateTime))
            {
                builder.Append("\"artifact_publication_deadline_utc\":").Append(JsonUtil.String(state.ArtifactPublicationDeadlineUtc.ToString("o"))).Append(',');
            }
            builder.Append("\"last_published_lifecycle\":").Append(JsonUtil.String(state.LastPublishedLifecycle)).Append(',');
            if (!string.IsNullOrEmpty(state.ArtifactPublicationErrorCode))
            {
                builder.Append("\"artifact_publication_error_code\":").Append(JsonUtil.String(state.ArtifactPublicationErrorCode)).Append(',');
            }
            builder.Append("\"dry_run\":").Append(JsonUtil.Bool(state.DryRun)).Append(',');
            builder.Append("\"cancel_requested\":").Append(JsonUtil.Bool(state.CancelRequested)).Append(',');
            builder.Append("\"cleanup_performed\":").Append(JsonUtil.Bool(state.CleanupPerformed)).Append(',');
            if (state.RecoveredAfterReload)
            {
                builder.Append("\"recovered_after_reload\":true,");
                builder.Append("\"callback_re_registered_after_reload\":").Append(JsonUtil.Bool(state.CallbackReRegisteredAfterReload)).Append(',');
                builder.Append("\"recovery_reason\":").Append(JsonUtil.String(state.RecoveryReason)).Append(',');
            }

            builder.Append("\"callback_registration_epoch\":").Append(Math.Max(0, state.CallbackRegistrationEpoch)).Append(',');
            builder.Append("\"execute_dispatch_count\":").Append(Math.Max(0, state.ExecuteDispatchCount)).Append(',');
            builder.Append("\"execute_dispatch_committed\":").Append(JsonUtil.Bool(state.ExecuteDispatchCount == 1)).Append(',');
            builder.Append("\"test_runner_execute_invocation_attempted\":").Append(JsonUtil.Bool(state.ExecuteDispatchCount == 1)).Append(',');
            builder.Append("\"test_runner_execute_invoked\":").Append(JsonUtil.Bool(state.ExecuteCallReturned)).Append(',');
            if (state.PreExecuteRefusal != null)
            {
                builder.Append("\"operation_created\":true,\"terminal\":true,\"pre_execute_refusal\":").Append(state.PreExecuteRefusal.ToJson()).Append(',');
            }
            builder.Append("\"write_command_repeated\":false,");
            builder.Append("\"write_replay_allowed\":false,");

            if (state.DryRun)
            {
                builder.Append("\"would_run\":true,");
            }

            var publicationCommitted = state.DryRun || state.ArtifactPublicationState == "committed";
            builder.Append("\"success\":").Append(JsonUtil.Bool(state.Success && publicationCommitted)).Append(',');
            builder.Append("\"test_result_success\":").Append(JsonUtil.Bool(state.Success)).Append(',');
            builder.Append("\"terminal_counts_authoritative\":").Append(JsonUtil.Bool(state.TerminalCountsAuthoritative)).Append(',');
            builder.Append("\"terminal_count_source\":").Append(JsonUtil.String(state.TerminalCountsAuthoritative ? "unity_test_runner" : "unavailable")).Append(',');
            AppendTerminalCountEvidence(builder, state);
            builder.Append("\"result\":").Append(JsonUtil.String(state.ResultState)).Append(',');
            builder.Append("\"mode\":").Append(JsonUtil.String(state.Mode)).Append(',');
            builder.Append("\"test_mode\":").Append(JsonUtil.String(state.Mode)).Append(',');
            builder.Append("\"filter\":").Append(JsonUtil.String(state.Filter)).Append(',');
            builder.Append("\"test_filter\":").Append(JsonUtil.String(state.Filter)).Append(',');
            builder.Append("\"assembly\":").Append(JsonUtil.String(state.TestAssembly)).Append(',');
            builder.Append("\"test_assembly\":").Append(JsonUtil.String(state.TestAssembly)).Append(',');
            builder.Append("\"filter_resolution_identity\":").Append(JsonUtil.String(state.FilterResolutionIdentity)).Append(',');
            builder.Append("\"filter_resolution_identity_status\":").Append(JsonUtil.String(state.FilterResolutionIdentityStatus)).Append(',');
            builder.Append("\"resolved_filter_name\":").Append(JsonUtil.String(state.ResolvedFilterName)).Append(',');
            builder.Append("\"resolved_filter_is_suite\":").Append(JsonUtil.Bool(state.ResolvedFilterIsSuite)).Append(',');
            builder.Append("\"resolved_filter_candidate_count\":").Append(Math.Max(0, state.ResolvedFilterCandidateCount)).Append(',');
            builder.Append("\"terminal_result_identity_status\":").Append(JsonUtil.String(state.TerminalResultIdentityStatus)).Append(',');
            builder.Append("\"terminal_result_leaf_count\":").Append(Math.Max(0, state.TerminalResultLeafCount)).Append(',');
            builder.Append("\"terminal_result_matching_leaf_count\":").Append(Math.Max(0, state.TerminalResultMatchingLeafCount)).Append(',');
            builder.Append("\"terminal_result_unexpected_leaf_count\":").Append(Math.Max(0, state.TerminalResultUnexpectedLeafCount)).Append(',');
            builder.Append("\"terminal_result_traversal_truncated\":").Append(JsonUtil.Bool(state.TerminalResultTraversalTruncated)).Append(',');
            builder.Append("\"terminal_result_identities_truncated\":").Append(JsonUtil.Bool(state.TerminalResultIdentitiesTruncated)).Append(',');
            builder.Append("\"terminal_result_identities\":");
            AppendStringArray(builder, state.TerminalResultIdentities, MaxTerminalResultIdentities);
            builder.Append(',');
            builder.Append("\"allow_unsaved_scene_changes\":").Append(JsonUtil.Bool(state.AllowUnsavedSceneChanges)).Append(',');
            AppendTestRunMatchDiagnostics(builder, state);
            AppendTestRunEvidenceSplit(builder, state);
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(state.ArtifactPath)).Append(',');
            AppendArtifactContract(builder, state.ArtifactPath, TestRunArtifactReady(state), state.ArtifactPersisted);
            builder.Append(",\"batchmode_fallback\":").Append(TestRunBatchmodeFallbackJson(state.RunId, state.Mode, state.Filter, state.TestAssembly, false));
            builder.Append(',');
            builder.Append("\"total\":").Append(state.Total).Append(',');
            builder.Append("\"passed\":").Append(state.Passed).Append(',');
            builder.Append("\"failed\":").Append(state.Failed).Append(',');
            builder.Append("\"skipped\":").Append(state.Skipped).Append(',');
            builder.Append("\"duration_ms\":").Append(Math.Max(0, state.DurationMs));
            builder.Append(",\"failure_summary\":").Append(TestFailureSummaryJson(state));
            builder.Append(",\"has_unsaved_scene_changes\":").Append(JsonUtil.Bool(state.HasUnsavedSceneChangesAtStart));
            builder.Append(",\"dirty_scene_count\":").Append(Math.Max(0, state.DirtySceneCountAtStart));
            builder.Append(",\"dirty_scenes\":").Append(BuiltInCommands.DirtyScenesJson(state.DirtyScenesAtStart));
            if (state.HasUnsavedSceneChangesAtStart)
            {
                builder.Append(",\"warnings\":[{\"code\":\"unsaved_scene_changes\""
                    + ",\"message\":\"One or more loaded scenes had unsaved changes when test.run started; Unity may show save/revert prompts during test execution.\""
                    + ",\"dirty_scene_count\":").Append(Math.Max(0, state.DirtySceneCountAtStart)).Append("}]");
            }

            if (!string.IsNullOrEmpty(state.ResultName))
            {
                builder.Append(",\"result_name\":").Append(JsonUtil.String(state.ResultName));
            }

            if (!string.IsNullOrEmpty(state.Message))
            {
                builder.Append(",\"message\":").Append(JsonUtil.String(state.Message));
            }

            if (!string.IsNullOrEmpty(state.CancellationReason))
            {
                builder.Append(",\"cancellation_reason\":").Append(JsonUtil.String(state.CancellationReason));
            }

            if (!string.IsNullOrEmpty(state.ExceptionType))
            {
                builder.Append(",\"exception_type\":").Append(JsonUtil.String(state.ExceptionType));
            }

            if (!string.IsNullOrEmpty(state.StackTrace))
            {
                builder.Append(",\"stack_trace\":").Append(JsonUtil.String(state.StackTrace));
            }

            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, TestRunNextValidActions(state));

            if (state.FailedTests.Count > 0)
            {
                builder.Append(",\"failed_tests_truncated\":").Append(JsonUtil.Bool(state.FailedTestsTruncated));
                builder.Append(",\"failed_tests\":[");
                for (var index = 0; index < state.FailedTests.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    state.FailedTests[index].AppendJson(builder);
                }

                builder.Append(']');
            }

            state.NonPassingEvidence.AppendJson(builder, MaxTestRunArtifactChars);
            builder.Append('}');
            return builder.ToString();
        }

        static void AppendArtifactContract(StringBuilder builder, string artifactPath, bool artifactReady, bool artifactPersisted = false)
        {
            var hasArtifactRef = artifactReady || artifactPersisted;
            builder.Append("\"artifact_ref\":").Append(JsonUtil.String(artifactPath)).Append(',');
            builder.Append("\"artifact_ready\":").Append(JsonUtil.Bool(artifactReady)).Append(',');
            builder.Append("\"artifact_persisted\":").Append(JsonUtil.Bool(hasArtifactRef)).Append(',');
            builder.Append("\"artifact_refs\":");
            if (hasArtifactRef)
            {
                AppendStringArray(builder, new List<string> { artifactPath });
            }
            else
            {
                builder.Append("[]");
            }

            builder.Append(",\"readable_with\":\"job.artifact.read\"");
        }

        static bool TestRunArtifactReady(TestRunState state)
        {
            return state != null && !state.DryRun && state.Finished && state.ArtifactWritten;
        }

        static string TestFailureSummaryJson(TestRunState state)
        {
            var publicationFailed = state != null && state.Finished && state.ArtifactPublicationState != "committed";
            return "{\"failed\":" + JsonUtil.Bool(state != null && !state.DryRun && state.Finished && (!state.Success || publicationFailed))
                + ",\"compiler_errors\":false"
                + ",\"build_errors\":0"
                + ",\"failed_tests\":" + (state == null ? 0 : Math.Max(0, state.Failed))
                + ",\"artifact_publication_failed\":" + JsonUtil.Bool(publicationFailed)
                + ",\"duration_ms\":" + (state == null ? 0 : Math.Max(0, state.DurationMs))
                + ",\"output_size_bytes\":" + ArtifactSizeBytes(state == null ? string.Empty : state.ArtifactPath)
                + ",\"dirty_scene_preflight\":" + DirtyScenePreflightJson(state == null ? false : state.HasUnsavedSceneChangesAtStart, state == null ? 0 : state.DirtySceneCountAtStart, state == null ? Array.Empty<DirtySceneInfo>() : state.DirtyScenesAtStart, state != null && state.AllowUnsavedSceneChanges)
                + "}";
        }

        static List<string> TestRunNextValidActions(TestRunState state)
        {
            var actions = new List<string>();
            if (state == null)
            {
                actions.Add("test.run.status");
                return actions;
            }

            if (state.DryRun)
            {
                actions.Add("test.run");
                actions.Add("test.list");
                actions.Add("compile.status");
            }
            else if (IsTestRunInProgressStatus(state.Status))
            {
                actions.Add("test.run.status");
                actions.Add("test.run.cancel");
            }
            else if (state.Status == "callback_lost_after_playmode_exit")
            {
                actions.Add("job.artifact.read");
                actions.Add("test.run.batchmode_fallback");
                actions.Add("compile.status");
                actions.Add("logs.console.query");
                actions.Add("test.run.cancel");
                actions.Add("diagnostics.report_problem");
            }
            else if (state.Status == "timeout")
            {
                actions.Add("test.run.status");
                if (state.ArtifactPersisted) actions.Add("job.artifact.read");
                actions.Add("diagnostics.report_problem");
                actions.Add("do_not_repeat_test.run_until_execution_state_is_resolved");
            }
            else if (state.ArtifactPublicationState == "pending" || state.ArtifactPublicationState == "failed")
            {
                actions.Add("test.run.status");
                if (state.ArtifactPersisted) actions.Add("job.artifact.read");
                actions.Add("diagnostics.report_problem");
            }
            else if (state.Success)
            {
                actions.Add("job.artifact.read");
                actions.Add("artifact.metadata");
                actions.Add("test.list");
            }
            else
            {
                actions.Add("job.artifact.read");
                actions.Add("compile.status");
                actions.Add("logs.console.query");
                actions.Add("test.list");
            }

            if (state.HasUnsavedSceneChangesAtStart)
            {
                actions.Add("editor.save_prompt.get");
            }

            return actions;
        }

        static string DirtyScenePreflightJson(BridgeRuntimeState runtimeState, bool allowUnsavedSceneChanges)
        {
            return DirtyScenePreflightJson(runtimeState != null && runtimeState.HasUnsavedSceneChanges, runtimeState == null ? 0 : runtimeState.DirtySceneCount, runtimeState == null ? Array.Empty<DirtySceneInfo>() : runtimeState.DirtyScenes, allowUnsavedSceneChanges);
        }

        static string DirtyScenePreflightJson(bool hasUnsavedSceneChanges, int dirtySceneCount, DirtySceneInfo[] dirtyScenes, bool allowUnsavedSceneChanges)
        {
            return "{\"checked\":true"
                + ",\"allow_unsaved_scene_changes\":" + JsonUtil.Bool(allowUnsavedSceneChanges)
                + ",\"has_unsaved_scene_changes\":" + JsonUtil.Bool(hasUnsavedSceneChanges)
                + ",\"dirty_scene_count\":" + Math.Max(0, dirtySceneCount)
                + ",\"dirty_scenes\":" + BuiltInCommands.DirtyScenesJson(dirtyScenes)
                + "}";
        }

        static CommandExecutionResult RequireCleanScenesForTestRun(BridgeRuntimeState runtimeState, bool allowUnsavedSceneChanges, string phase)
        {
            if (runtimeState == null)
            {
                return CommandExecutionResult.Error(
                    "test_run_scene_state_unavailable",
                    "test.run refused because loaded scene state could not be verified",
                    "{\"reason\":\"test_run_scene_state_unavailable\",\"phase\":" + JsonUtil.String(phase)
                        + ",\"allow_unsaved_scene_changes_requested\":" + JsonUtil.Bool(allowUnsavedSceneChanges)
                        + ",\"test_runner_execute_invoked\":false,\"native_save_prompt_answered\":false,\"scene_state_mutated\":false}");
            }

            runtimeState.UpdateFromEditor();
            if (!runtimeState.HasUnsavedSceneChanges)
            {
                return null;
            }

            var details = "{\"reason\":\"test_run_dirty_scene_blocked\""
                + ",\"phase\":" + JsonUtil.String(phase)
                + ",\"allow_unsaved_scene_changes_requested\":" + JsonUtil.Bool(allowUnsavedSceneChanges)
                + ",\"allow_unsaved_scene_changes_honored\":false"
                + ",\"test_runner_execute_invoked\":false"
                + ",\"native_save_prompt_answered\":false"
                + ",\"scene_state_mutated\":false"
                + ",\"has_unsaved_scene_changes\":true"
                + ",\"dirty_scene_count\":" + Math.Max(0, runtimeState.DirtySceneCount)
                + ",\"dirty_scenes\":" + BuiltInCommands.DirtyScenesJson(runtimeState)
                + ",\"safe_recovery_commands\":[\"editor.save_prompt.get\",\"compile.status\"]"
                + ",\"hint\":\"test.run cannot safely execute while a loaded scene is dirty because Unity Test Framework may open a save/discard prompt. Save the intended changes explicitly or restore a clean scene state, then submit a new test.run request.\"}";
            return CommandExecutionResult.Error(
                "test_run_dirty_scene_blocked",
                "test.run refused while loaded scenes have unsaved changes; allow_unsaved_scene_changes does not authorize saving, discarding, clearing, or prompting for scene changes",
                details);
        }

        static long ArtifactSizeBytes(string artifactPath)
        {
            if (string.IsNullOrEmpty(artifactPath))
            {
                return 0;
            }

            try
            {
                var fullPath = AbsoluteProjectPath(artifactPath);
                return File.Exists(fullPath) ? Math.Max(0, new FileInfo(fullPath).Length) : 0;
            }
            catch
            {
                return 0;
            }
        }

        static string Sha256Prefixed(string value)
        {
            using (var sha256 = SHA256.Create())
            {
                return "sha256:" + HexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
            }
        }

        static string HexString(byte[] bytes)
        {
            var builder = new StringBuilder(bytes == null ? 0 : bytes.Length * 2);
            if (bytes != null)
            {
                foreach (var value in bytes)
                {
                    builder.Append(value.ToString("x2"));
                }
            }

            return builder.ToString();
        }

        static void AppendTestRunMatchDiagnostics(StringBuilder builder, TestRunState state)
        {
            builder.Append("\"filter_matching_mode\":").Append(JsonUtil.String(state.FilterMatchingMode)).Append(',');
            builder.Append("\"filter_resolution_status\":").Append(JsonUtil.String(state.FilterResolutionStatus)).Append(',');
            builder.Append("\"filter_resolution_reason\":").Append(JsonUtil.String(state.FilterResolutionReason)).Append(',');
            builder.Append("\"matched_tests_matching_mode\":").Append(JsonUtil.String(state.MatchedTestsMatchingMode)).Append(',');
            builder.Append("\"matched_count\":").Append(state.MatchedCount).Append(',');
            builder.Append("\"matched_count_reliable\":").Append(JsonUtil.Bool(state.MatchedCountReliable)).Append(',');
            builder.Append("\"matched_count_status\":").Append(JsonUtil.String(state.MatchedCountStatus)).Append(',');
            builder.Append("\"matched_tests_coverage\":").Append(JsonUtil.String(state.MatchedTestsCoverageStatus)).Append(',');
            builder.Append("\"matched_tests_truncated\":").Append(JsonUtil.Bool(state.MatchedTestsTruncated)).Append(',');
            builder.Append("\"matched_tests_scan\":{");
            builder.Append("\"incomplete\":").Append(JsonUtil.Bool(state.MatchedTestsScanIncomplete)).Append(',');
            builder.Append("\"reason\":").Append(JsonUtil.String(state.MatchedTestsScanReason)).Append(',');
            builder.Append("\"omitted\":").Append(JsonUtil.Bool(state.MatchedTestsScanOmitted)).Append(',');
            builder.Append("\"omitted_reason\":").Append(JsonUtil.String(state.MatchedTestsScanOmittedReason)).Append(',');
            builder.Append("\"scanned_assemblies\":").Append(state.MatchedTestsScannedAssemblies).Append(',');
            builder.Append("\"scanned_types\":").Append(state.MatchedTestsScannedTypes).Append(',');
            builder.Append("\"scanned_methods\":").Append(state.MatchedTestsScannedMethods).Append("},");
            builder.Append("\"matched_tests\":[");
            for (var index = 0; index < state.MatchedTests.Count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                state.MatchedTests[index].AppendJson(builder);
            }

            builder.Append("],\"filter_contract\":{");
            builder.Append("\"input\":").Append(JsonUtil.String(state.Filter)).Append(',');
            builder.Append("\"unity_test_names\":");
            AppendStringArray(builder, state.UnityTestNameFilters, MaxTestRunMatchedTests);
            builder.Append(",\"normalized\":").Append(JsonUtil.Bool(state.FilterNormalized));
            builder.Append(",\"warning\":").Append(JsonUtil.Bool(state.FilterContractWarning));
            if (state.FilterContractWarning)
            {
                builder.Append(",\"warning_code\":\"filter_contract_warning\"");
                builder.Append(",\"warning_reason\":").Append(JsonUtil.String(state.FilterContractWarningReason));
            }

            builder.Append(",\"suggested_filters\":");
            AppendStringArray(builder, state.FilterContractSuggestedFilters);
            builder.Append("},\"filter_diagnostic_hints\":");
            AppendStringArray(builder, TestFilterDiagnosticHints(state));
            builder.Append(',');
        }

        static void AppendTestRunEvidenceSplit(StringBuilder builder, TestRunState state)
        {
            var executedCount = Math.Max(0, state.Total);
            var terminalCountsAvailable = !state.DryRun && state.TerminalCountsAuthoritative;
            var filterIdentityVerified = string.IsNullOrEmpty(state.Filter)
                || (state.TerminalResultIdentityStatus == "matched"
                    && (!state.RecoveredAfterReload
                        || state.FilterResolutionIdentityStatus == "verified_after_reload"
                        || CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(state)));
            var executedResultsProveMatch = terminalCountsAvailable && executedCount > 0 && filterIdentityVerified;
            var previewZeroIsNotAbsence = state.MatchedCount == 0 && !state.MatchedCountReliable;
            var authoritativeCatalog = string.Equals(state.FilterResolutionStatus, "resolved", StringComparison.Ordinal);
            builder.Append("\"filter_preview\":{");
            builder.Append("\"source\":").Append(JsonUtil.String(authoritativeCatalog ? "unity_test_runner_catalog" : "reflection_preview")).Append(',');
            builder.Append("\"matched_count\":").Append(state.MatchedCount).Append(',');
            builder.Append("\"matched_count_reliable\":").Append(JsonUtil.Bool(state.MatchedCountReliable)).Append(',');
            builder.Append("\"matched_count_status\":").Append(JsonUtil.String(state.MatchedCountStatus)).Append(',');
            builder.Append("\"coverage\":").Append(JsonUtil.String(state.MatchedTestsCoverageStatus)).Append(',');
            builder.Append("\"absence_provable\":").Append(JsonUtil.Bool(state.MatchedCount == 0 && state.MatchedCountReliable && !state.MatchedTestsScanIncomplete && !state.MatchedTestsScanOmitted)).Append(',');
            builder.Append("\"zero_match_is_lower_bound\":").Append(JsonUtil.Bool(previewZeroIsNotAbsence)).Append(',');
            builder.Append("\"warning\":").Append(JsonUtil.Bool(state.FilterContractWarning)).Append(',');
            builder.Append("\"warning_reason\":").Append(JsonUtil.String(state.FilterContractWarningReason)).Append("},");

            builder.Append("\"execution_result\":{");
            builder.Append("\"source\":\"unity_test_runner\",");
            builder.Append("\"available\":").Append(JsonUtil.Bool(terminalCountsAvailable)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(state.Status)).Append(',');
            builder.Append("\"result\":").Append(JsonUtil.String(state.ResultState)).Append(',');
            builder.Append("\"total\":").Append(executedCount).Append(',');
            builder.Append("\"passed\":").Append(Math.Max(0, state.Passed)).Append(',');
            builder.Append("\"failed\":").Append(Math.Max(0, state.Failed)).Append(',');
            builder.Append("\"skipped\":").Append(Math.Max(0, state.Skipped)).Append(',');
            AppendTerminalCountEvidence(builder, state);
            builder.Append("\"proves_filter_matched\":").Append(JsonUtil.Bool(executedResultsProveMatch)).Append(',');
            builder.Append("\"supersedes_preview_absence\":").Append(JsonUtil.Bool(executedResultsProveMatch && state.MatchedCount == 0)).Append(',');
            builder.Append("\"interpretation\":").Append(JsonUtil.String(executedResultsProveMatch
                ? "executed_results_are_authoritative_for_this_completed_run"
                : previewZeroIsNotAbsence
                    ? "reflection_preview_zero_is_not_absence_evidence"
                    : "no_executed_test_count_available"));
            builder.Append("},");
        }

        static void AppendTerminalCountEvidence(StringBuilder builder, TestRunState state)
        {
            if (state.TerminalCountsAuthoritative)
            {
                builder.Append("\"pass_count\":").Append(state.PassCount).Append(',');
                builder.Append("\"fail_count\":").Append(state.FailCount).Append(',');
                builder.Append("\"skip_count\":").Append(state.SkipCount).Append(',');
                builder.Append("\"inconclusive_count\":").Append(state.InconclusiveCount).Append(',');
                builder.Append("\"authoritative_leaf_total\":").Append(state.Total).Append(',');
            }

            if (state.AssertCountAvailable)
            {
                builder.Append("\"assert_count\":").Append(state.AssertCount).Append(',');
            }

            if (state.TestCaseCountAvailable)
            {
                builder.Append("\"test_case_count\":").Append(state.TestCaseCount).Append(',');
                builder.Append("\"test_case_count_source\":\"unity_test_runner_root.TestCaseCount\",");
            }

            builder.Append("\"count_consistency\":{");
            builder.Append("\"authoritative_counts_available\":").Append(JsonUtil.Bool(state.TerminalCountsAuthoritative)).Append(',');
            builder.Append("\"leaf_sum_matches_total\":").Append(JsonUtil.Bool(state.TerminalCountsAuthoritative)).Append(',');
            builder.Append("\"assert_count_semantics\":\"not_a_test_count\",");
            builder.Append("\"test_case_count_available\":").Append(JsonUtil.Bool(state.TestCaseCountAvailable));
            if (state.TestCaseCountAvailable && state.TerminalCountsAuthoritative)
            {
                builder.Append(",\"test_case_count_matches_leaf_total\":").Append(JsonUtil.Bool(state.TestCaseCount == state.Total));
            }

            builder.Append("},");
        }

        static List<string> TestFilterDiagnosticHints(TestRunState state)
        {
            var hints = new List<string>();
            if (state.TerminalCountsAuthoritative && state.Total > 0 && state.MatchedCount == 0)
            {
                hints.Add("Executed TestRunner results show total > 0; do not treat reflection preview matched_count=0 as zero coverage for this completed run.");
            }

            if (state.MatchedTestsScanOmitted)
            {
                hints.Add(string.IsNullOrEmpty(state.Filter)
                    ? "matched_tests preview omitted for mode-only test.run; Unity Test Runner will run all tests in the selected mode unless a filter is supplied."
                    : "matched_tests preview was omitted after domain reload; use filter_resolution_identity_status and terminal_result_identity_status as the recovered exact-filter evidence.");
            }
            else if (state.MatchedCount == 0 && !state.MatchedCountReliable)
            {
                hints.Add("Reflection preview is incomplete; matched_count is a lower bound, not proof that no tests match. Narrow test.list by query or run with a known exact full_name.");
            }
            else if (state.MatchedCount == 0)
            {
                hints.Add("No tests matched the reflection preview; run compile.refresh_and_wait then test.list with the same query, or use an exact full_name from test.list.");
            }
            else if (state.MatchedCount == 1)
            {
                hints.Add("Only one test matched the reflection preview; if broad coverage was intended, inspect matched_tests before trusting aggregate counts and rerun with an exact full_name or adjusted filter.");
            }

            if (state.MatchedTestsTruncated || state.MatchedTestsScanIncomplete)
            {
                hints.Add("matched_tests is partial; run test.list with a narrower query or a larger max_results value for a fuller preview.");
            }

            if (state.FilterContractWarning)
            {
                hints.Add("filter_contract_warning: copy tests[].runnable_filters.recommended from test.list or matched_tests[].runnable_filters.recommended from this response into test.run filter.");
            }

            return hints;
        }

        static List<string> TestDiscoveryDiagnosticHints(TestDiscoveryResult discovery)
        {
            var hints = new List<string>();
            if (discovery == null)
            {
                return hints;
            }

            if (discovery.ScanIncomplete && discovery.MatchedCount == 0)
            {
                hints.Add("Scan stopped before full project coverage; total_available is a lower bound, not proof that no tests exist.");
                hints.Add("Retry test.list with a narrower query, or use test.run with an exact known filter.");
            }
            else if (discovery.ScanIncomplete)
            {
                hints.Add("Scan stopped before full project coverage; total_available and tests are partial.");
            }

            return hints;
        }

        static List<string> TestDiscoveryNextValidActions(TestDiscoveryResult discovery)
        {
            var actions = new List<string>();
            if (discovery != null && discovery.ScanIncomplete)
            {
                actions.Add("test.list");
                actions.Add("test.run");
                actions.Add("compile.refresh_and_wait");
            }

            return actions;
        }

        static string TestRunErrorDetailsJson(TestRunState state)
        {
            return "{\"diagnostic_artifact\":" + JsonUtil.String(state.ArtifactPath)
                + ",\"result\":" + JsonUtil.String(state.ResultState)
                + ",\"exception_type\":" + JsonUtil.String(state.ExceptionType)
                + "}";
        }

        static bool TryClassifyRecoveredPlayModeRunAfterExit(TestRunState state, BridgeRuntimeState runtimeState)
        {
            if (state == null
                || state.Status != "running"
                || !state.CallbackReRegisteredAfterReload
                || !string.Equals(state.Mode, "playmode", StringComparison.OrdinalIgnoreCase)
                || !EditorReadyAndNotPlaying(runtimeState))
            {
                return false;
            }

            var now = DateTime.UtcNow;
            if ((now - state.LastUpdatedAt).TotalSeconds < TestRunRecoveredPlayModeExitGraceSeconds)
            {
                return false;
            }

            state.Status = "callback_lost_after_playmode_exit";
            state.ResultState = "callback_lost_after_playmode_exit";
            state.Success = false;
            state.Message = "Unity Editor is ready and not in Play Mode after test.run callback re-registration, but Unity TestRunner did not deliver a terminal RunFinished callback. MCP marked this tracking operation stale/terminal; do not treat it as pass or fail without fallback evidence.";
            state.Finished = true;
            state.FinishedAt = now;
            FinalizeTestRun(state);
            return true;
        }

        static bool EditorReadyAndNotPlaying(BridgeRuntimeState runtimeState)
        {
            if (runtimeState != null)
            {
                return runtimeState.IsReady && !runtimeState.IsCompiling && !runtimeState.IsUpdating && !runtimeState.IsPlaying;
            }

            return !EditorApplication.isCompiling
                && !EditorApplication.isUpdating
                && !EditorApplication.isPlayingOrWillChangePlaymode
                && !EditorApplication.isPlaying;
        }

        internal static string TestRunMissingDetailsJson(string runId, string artifactPath)
        {
            return "{\"operation_id\":" + JsonUtil.String(runId)
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"test_run_delivery_state\":\"unknown\",\"status_or_artifact_check_required_before_repeat\":true,\"repeat_blocked_until_delivery_state_checked\":true"
                + ",\"test_run_replayed\":false,\"write_command_repeated\":false,\"write_replay_allowed\":false"
                + ",\"next_valid_actions\":[\"test.run.status\",\"sgg_unity_projects\",\"diagnostics.report_problem\",\"do_not_repeat_test.run\"]"
                + ",\"hint\":\"The run may still be active in a previous Unity domain, or its artifact may have been removed. Missing status is not proof of non-execution. Do not repeat test.run or cancel the unresolved operation. After fresh owner readiness, poll this exact operation's status or report the unresolved evidence.\"}";
        }

        static string TestRunArtifactIdentityMismatchDetailsJson(string runId, string artifactPath)
        {
            return "{\"operation_id\":" + JsonUtil.String(runId)
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"artifact_identity_verified\":false"
                + ",\"next_valid_actions\":[\"diagnostics.report_problem\",\"job.artifact.read\"]"
                + ",\"hint\":\"Do not use this artifact as test.run status evidence and do not repeat the test run automatically.\"}";
        }

        static string TestRunArtifactTooLargeDetailsJson(string runId, string artifactPath)
        {
            return "{\"operation_id\":" + JsonUtil.String(runId)
                + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                + ",\"max_artifact_chars\":" + MaxTestRunArtifactChars
                + ",\"next_valid_actions\":[\"job.artifact.read\",\"test.run.cancel\",\"diagnostics.report_problem\"]"
                + ",\"hint\":\"The persisted artifact is too large for recovery metadata parsing; inspect the artifact directly or cancel MCP tracking before a replacement run.\"}";
        }

        static string ExtractTestRunId(string argsJson)
        {
            var runId = JsonUtil.ExtractString(argsJson, "operation_id").Trim();
            if (string.IsNullOrEmpty(runId))
            {
                runId = JsonUtil.ExtractString(argsJson, "run_id").Trim();
            }

            return runId;
        }

        static void CancelOrCleanupTestRunState(TestRunState state, string reason)
        {
            if (state == null)
            {
                return;
            }

            state.CancelRequested = true;
            state.CleanupPerformed = true;
            state.CancellationReason = reason;
            if (IsTestRunInProgressStatus(state.Status) || state.Status == "cancel_requested")
            {
                state.Status = "cancelled";
                state.ResultState = "cancelled";
                state.Success = false;
                state.Finished = true;
                state.FinishedAt = DateTime.UtcNow;
                state.Message = reason;
                MarkTestRunActivity(state, "cancelled_by_mcp");
            }
            else
            {
                if (string.IsNullOrEmpty(state.Message))
                {
                    state.Message = reason;
                }

                MarkTestRunActivity(state, "cleanup_by_mcp");
            }

            try
            {
                state.ArtifactPersisted = true;
                WriteArtifact(state.ArtifactPath, TestRunStateJson(state));
                state.ArtifactWritten = true;
            }
            finally
            {
                ClearActiveTestRunSession(state.RunId);
            }
        }

        static string TestRunCancelResultJson(string runId, string artifactPath, string previousStatus, bool inMemoryStateFound, bool artifactFound, bool cleanupPerformed, bool artifactDeleted, string reason, string stateJson)
        {
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"test.run.cancel\",");
            builder.Append("\"operation_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(artifactDeleted ? "forgotten" : "cancelled")).Append(',');
            builder.Append("\"result\":").Append(JsonUtil.String(artifactDeleted ? "forgotten" : "cancelled")).Append(',');
            builder.Append("\"cancel_requested\":true,");
            builder.Append("\"cleanup_performed\":").Append(JsonUtil.Bool(cleanupPerformed)).Append(',');
            builder.Append("\"in_memory_state_found\":").Append(JsonUtil.Bool(inMemoryStateFound)).Append(',');
            builder.Append("\"artifact_found\":").Append(JsonUtil.Bool(artifactFound)).Append(',');
            builder.Append("\"artifact_deleted\":").Append(JsonUtil.Bool(artifactDeleted)).Append(',');
            builder.Append("\"previous_status\":").Append(JsonUtil.String(previousStatus)).Append(',');
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(artifactPath)).Append(',');
            AppendArtifactContract(builder, artifactPath, !artifactDeleted && cleanupPerformed, !artifactDeleted && artifactFound);
            builder.Append(",\"reason\":").Append(JsonUtil.String(reason));
            builder.Append(",\"warning\":\"test.run.cancel cancels MCP tracking and callback handling; tracking cleanup does not prove that Unity Test Runner stopped, including after a modal prompt or main-thread stall. Do not start another run until execution state is resolved.\"");
            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, artifactDeleted
                ? new List<string> { "diagnostics.report_problem", "do_not_repeat_test.run_until_execution_state_is_resolved" }
                : new List<string> { "test.run.status", "job.artifact.read", "diagnostics.report_problem", "do_not_repeat_test.run_until_execution_state_is_resolved" });
            if (!string.IsNullOrEmpty(stateJson))
            {
                builder.Append(",\"state\":").Append(stateJson);
            }

            builder.Append('}');
            return builder.ToString();
        }

        static bool TryReadTestRunArtifactIdentityForV3Transition(string runId, string artifactPath, out string argsHash, out string callerArgsHash, out string failureReason)
        {
            argsHash = string.Empty;
            callerArgsHash = string.Empty;
            failureReason = "artifact_invalid";
            try
            {
                var fullPath = AbsoluteProjectPath(artifactPath);
                if (!File.Exists(fullPath))
                {
                    failureReason = "artifact_not_found";
                    return false;
                }

                var before = new FileInfo(fullPath);
                if (before.Length <= 0 || before.Length > MaxTestRunArtifactChars)
                {
                    failureReason = before.Length > MaxTestRunArtifactChars ? "artifact_too_large" : "artifact_invalid";
                    return false;
                }

                var beforeLength = before.Length;
                var beforeWriteTicks = before.LastWriteTimeUtc.Ticks;
                var content = File.ReadAllText(fullPath);
                var after = new FileInfo(fullPath);
                if (after.Length != beforeLength || after.LastWriteTimeUtc.Ticks != beforeWriteTicks)
                {
                    failureReason = "artifact_changed";
                    return false;
                }

                return TryValidateTestRunArtifactIdentityForV3Transition(content, runId, artifactPath, out argsHash, out callerArgsHash, out failureReason);
            }
            catch
            {
                argsHash = string.Empty;
                callerArgsHash = string.Empty;
                failureReason = "artifact_changed";
                return false;
            }
        }

        internal static bool TryValidateTestRunArtifactIdentityForV3Transition(string content, string runId, string artifactPath, out string argsHash, out string callerArgsHash, out string failureReason)
        {
            argsHash = ExtractTopLevelJsonString(content, "args_hash");
            callerArgsHash = ExtractTopLevelJsonString(content, "caller_args_hash");
            if (ExtractTopLevelJsonInt(content, "artifact_schema_version", 0) != OwnerBindingFallbackTestRunArtifactSchemaVersion
                || !string.Equals(ExtractTopLevelJsonString(content, "command"), "test.run", StringComparison.Ordinal)
                || !PersistedTestRunArtifactIdentityMatches(content, runId)
                || !string.Equals(ExtractTopLevelJsonString(content, "artifact_path"), artifactPath, StringComparison.Ordinal)
                || !IsSafeSha256Hash(argsHash)
                || !IsSafeSha256Hash(callerArgsHash))
            {
                argsHash = string.Empty;
                callerArgsHash = string.Empty;
                failureReason = "artifact_identity_mismatch";
                return false;
            }

            failureReason = string.Empty;
            return true;
        }

        static string TestRunCancelledAfterReloadJson(string runId, string artifactPath, string reason, string argsHash, string callerArgsHash)
        {
            var now = DateTime.UtcNow.ToString("o");
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"test.run\",");
            builder.Append("\"artifact_schema_version\":").Append(CurrentTestRunArtifactSchemaVersion).Append(',');
            builder.Append("\"args_hash\":").Append(JsonUtil.String(argsHash)).Append(',');
            builder.Append("\"caller_args_hash\":").Append(JsonUtil.String(callerArgsHash)).Append(',');
            builder.Append("\"package_version\":").Append(JsonUtil.String(UnityBridgeRuntime.PackageVersion)).Append(',');
            builder.Append("\"unity_version\":").Append(JsonUtil.String(Application.unityVersion)).Append(',');
            builder.Append("\"created_at\":").Append(JsonUtil.String(now)).Append(',');
            builder.Append("\"started_at\":").Append(JsonUtil.String(now)).Append(',');
            builder.Append("\"last_update_at\":").Append(JsonUtil.String(now)).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"status\":\"cancelled\",");
            builder.Append("\"lifecycle\":\"cancelled_by_mcp_after_reload\",");
            builder.Append("\"dry_run\":false,");
            builder.Append("\"cancel_requested\":true,");
            builder.Append("\"cleanup_performed\":true,");
            builder.Append("\"success\":false,");
            builder.Append("\"terminal_counts_authoritative\":false,");
            builder.Append("\"terminal_count_source\":\"unavailable\",");
            builder.Append("\"result\":\"cancelled_after_reload\",");
            builder.Append("\"recovery_reason\":\"mcp_cancel_cleanup_without_in_memory_state\",");
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(artifactPath)).Append(',');
            AppendArtifactContract(builder, artifactPath, true, true);
            builder.Append(",\"message\":").Append(JsonUtil.String(reason));
            builder.Append(",\"cancellation_reason\":").Append(JsonUtil.String(reason));
            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, new List<string> { "test.run.status", "job.artifact.read", "test.run", "diagnostics.report_problem" });
            builder.Append('}');
            return builder.ToString();
        }

        static bool TryDeleteTestRunArtifact(string artifactPath)
        {
            var fullPath = AbsoluteProjectPath(artifactPath);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            File.Delete(fullPath);
            return true;
        }

        static string ReadPersistedTestRunArtifact(string runId, string artifactPath, out bool artifactIdentityMismatch)
        {
            artifactIdentityMismatch = false;
            var fullPath = AbsoluteProjectPath(artifactPath);
            if (!File.Exists(fullPath))
            {
                return string.Empty;
            }

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxTestRunArtifactChars)
            {
                return "{\"operation_id\":" + JsonUtil.String(runId)
                    + ",\"run_id\":" + JsonUtil.String(runId)
                    + ",\"status\":\"artifact_too_large\""
                    + ",\"success\":false"
                    + ",\"result\":\"artifact_too_large\""
                    + ",\"artifact_path\":" + JsonUtil.String(artifactPath)
                    + ",\"message\":\"Persisted test run artifact exceeds max_artifact_chars\"}";
            }

            var content = File.ReadAllText(fullPath);
            if (RequiresStrictTestRunRefusalValidation(content) && !TryReadPreExecuteRefusal(content, out _))
            {
                artifactIdentityMismatch = true;
                return string.Empty;
            }
            if (!PersistedTestRunArtifactIdentityMatches(content, runId))
            {
                artifactIdentityMismatch = true;
                return string.Empty;
            }
            if (RequiresStrictTestRunRefusalValidation(content))
            {
                if (!TryValidateTestRunArtifactIdentityForV3Transition(content, runId, artifactPath, out _, out _, out _))
                {
                    artifactIdentityMismatch = true;
                    return string.Empty;
                }
                // A valid refusal is terminal even if unrelated nested metadata contains a running marker.
                return content;
            }
            if (IsPersistedRunningTestRunArtifact(content))
            {
                if (!TryValidateTestRunArtifactIdentityForV3Transition(content, runId, artifactPath, out _, out _, out _))
                {
                    artifactIdentityMismatch = true;
                    return string.Empty;
                }
                if (!IsPersistedAwaitingFilterRevalidationArtifact(content))
                {
                    var recovered = TryRecoverTestRunFromBatchmodeXmlAfterReload(runId, artifactPath, content);
                    if (!string.IsNullOrEmpty(recovered))
                    {
                        return recovered;
                    }
                }

                var callbackRecovered = TryRecoverTestRunCallbackAfterReload(runId, artifactPath, content);
                if (!string.IsNullOrEmpty(callbackRecovered))
                {
                    return callbackRecovered;
                }
                if (IsPersistedAwaitingFilterRevalidationArtifact(content))
                {
                    return content;
                }

                return TestRunLostCallbackAfterDomainReloadJson(runId, artifactPath, content);
            }

            return NormalizePersistedTestRunArtifactForRead(content, runId, artifactPath);
        }

        internal static bool PersistedTestRunArtifactIdentityMatches(string content, string runId)
        {
            if (!IsSafeTestRunId(runId) || string.IsNullOrEmpty(content))
            {
                return false;
            }

            var operationId = ExtractTopLevelJsonString(content, "operation_id");
            var runIdAlias = ExtractTopLevelJsonString(content, "run_id");
            if (ExtractTopLevelJsonInt(content, "artifact_schema_version", 1) >= MinimumAuthoritativeTestRunArtifactSchemaVersion)
            {
                return string.Equals(operationId, runId, StringComparison.Ordinal)
                    && string.Equals(runIdAlias, runId, StringComparison.Ordinal);
            }

            return (string.IsNullOrEmpty(operationId) || string.Equals(operationId, runId, StringComparison.Ordinal))
                && (string.IsNullOrEmpty(runIdAlias) || string.Equals(runIdAlias, runId, StringComparison.Ordinal))
                && (!string.IsNullOrEmpty(operationId) || !string.IsNullOrEmpty(runIdAlias));
        }

        internal static string NormalizePersistedTestRunArtifactForRead(string content, string runId, string artifactPath)
        {
            if (RequiresStrictTestRunRefusalValidation(content) && !TryReadPreExecuteRefusal(content, out _)) return string.Empty;
            if (string.IsNullOrEmpty(content)
                || IsPersistedRunningTestRunArtifact(content)
                || ExtractTopLevelJsonInt(content, "artifact_schema_version", 1) >= MinimumAuthoritativeTestRunArtifactSchemaVersion
                || !string.Equals(ExtractTopLevelJsonString(content, "status"), "completed", StringComparison.Ordinal))
            {
                return content;
            }

            return LegacyCompletedTestRunArtifactJson(content, runId, artifactPath);
        }

        static string LegacyCompletedTestRunArtifactJson(string content, string runId, string artifactPath)
        {
            var mode = PersistedTestRunMode(content);
            var filter = PersistedTestRunFilter(content);
            var legacyStatus = ExtractTopLevelJsonString(content, "status");
            var legacyResult = ExtractTopLevelJsonString(content, "result");
            var legacyPackageVersion = ExtractTopLevelJsonString(content, "package_version");
            var total = Math.Max(0, ExtractTopLevelJsonInt(content, "total", 0));
            var passed = Math.Max(0, ExtractTopLevelJsonInt(content, "passed", 0));
            var failed = Math.Max(0, ExtractTopLevelJsonInt(content, "failed", 0));
            var skipped = Math.Max(0, ExtractTopLevelJsonInt(content, "skipped", 0));
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"test.run\",");
            builder.Append("\"artifact_schema_version\":1,");
            builder.Append("\"package_version\":").Append(JsonUtil.String(legacyPackageVersion)).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"status\":\"legacy_terminal_counts_untrusted\",");
            builder.Append("\"success\":false,");
            builder.Append("\"terminal_counts_authoritative\":false,");
            builder.Append("\"terminal_count_source\":\"legacy_artifact_v1\",");
            builder.Append("\"result\":").Append(JsonUtil.String(legacyResult)).Append(',');
            builder.Append("\"legacy_reported_status\":").Append(JsonUtil.String(legacyStatus)).Append(',');
            builder.Append("\"mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"test_mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"test_filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"assembly\":\"\",");
            builder.Append("\"test_assembly\":\"\",");
            builder.Append("\"execution_result\":{\"source\":\"legacy_artifact_v1\",\"available\":false");
            builder.Append(",\"status\":\"legacy_terminal_counts_untrusted\"");
            builder.Append(",\"result\":").Append(JsonUtil.String(legacyResult));
            builder.Append(",\"total\":").Append(total);
            builder.Append(",\"passed\":").Append(passed);
            builder.Append(",\"failed\":").Append(failed);
            builder.Append(",\"skipped\":").Append(skipped).Append("},");
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(artifactPath)).Append(',');
            AppendArtifactContract(builder, artifactPath, true, true);
            builder.Append(",\"total\":").Append(total);
            builder.Append(",\"passed\":").Append(passed);
            builder.Append(",\"failed\":").Append(failed);
            builder.Append(",\"skipped\":").Append(skipped);
            builder.Append(",\"message\":\"Schema-v1 terminal counts are historical and were not captured from authoritative Unity Test Framework root aggregates. Rerun test.run with the current package.\"");
            builder.Append(",\"next_valid_actions\":[\"test.run\",\"test.list\",\"job.artifact.read\"]");
            builder.Append('}');
            return builder.ToString();
        }

        static string TryRecoverTestRunCallbackAfterReload(string runId, string artifactPath, string persistedContent)
        {
            try
            {
                return MainThreadDispatcher.Run(() => ReRegisterTestRunCallbacksAfterReload(runId, artifactPath, persistedContent), TestRunReloadRecoveryTimeoutMs);
            }
            catch
            {
                return string.Empty;
            }
        }

        static string ReRegisterTestRunCallbacksAfterReload(string runId, string artifactPath, string persistedContent)
        {
            lock (s_testRunsGate)
            {
                if (s_testRuns.TryGetValue(runId, out var existing))
                {
                    return TestRunStateJson(existing);
                }
            }

            var state = TestRunStateFromPersistedArtifact(runId, artifactPath, persistedContent);
            if (state == null || state.Finished) return string.Empty;
            var invalidRecovery = FinalizeInvalidRecoveredTestRun(state);
            if (!string.IsNullOrEmpty(invalidRecovery))
            {
                return invalidRecovery;
            }

            RefreshRecoveredFilterIdentity(state);
            if (state.Status == AwaitingFilterRevalidationStatus)
            {
                lock (s_testRunsGate)
                {
                    if (s_testRuns.TryGetValue(runId, out var existing))
                    {
                        return TestRunStateJson(existing);
                    }
                    s_testRuns[runId] = state;
                }
                state.RecoveryReason = "filter_revalidation_resumed_after_domain_reload";
                RegisterTestRunUpdate();
                PersistTestRunSnapshot(state, "filter_revalidation_resumed_after_domain_reload");
                return TestRunStateJson(state);
            }
            var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
            var callbackType = FindType("UnityEditor.TestTools.TestRunner.Api.ICallbacks");
            if (apiType == null || callbackType == null)
            {
                return string.Empty;
            }

            try
            {
                var api = CreateTestRunnerApi(apiType);
                var callbacks = TestRunCallbacksProxy.Create(callbackType, state);
                state.Api = api;
                state.Callbacks = callbacks;
                InvokeReflected(api, "RegisterCallbacks", callbacks);
                state.CallbackReRegisteredAfterReload = true;
                state.RecoveryReason = "callback_re_registered_after_domain_reload";
                lock (s_testRunsGate)
                {
                    if (s_testRuns.TryGetValue(runId, out var existing))
                    {
                        InvokeReflectedIfExists(api, "UnregisterCallbacks", callbacks);
                        return TestRunStateJson(existing);
                    }

                    s_testRuns[runId] = state;
                }

                RegisterTestRunUpdate();
                if (!state.ArtifactWritten)
                {
                    PersistTestRunSnapshot(state, "callbacks_re_registered_after_reload");
                }

                return TestRunStateJson(state);
            }
            catch
            {
                InvokeReflectedIfExists(state.Api, "UnregisterCallbacks", state.Callbacks);
                lock (s_testRunsGate)
                {
                    s_testRuns.Remove(runId);
                }

                return string.Empty;
            }
        }

        static string FinalizeInvalidRecoveredTestRun(TestRunState state)
        {
            if (state == null) return string.Empty;
            var activeRunId = SessionState.GetString(ActiveTestRunSessionStateKey, string.Empty);
            if (!string.Equals(activeRunId, state.RunId, StringComparison.Ordinal))
            {
                state.Status = "test_operation_replaced_after_reload";
                state.ResultState = "test_operation_replaced_after_reload";
                state.Message = "The persisted active test operation no longer matches this running artifact after domain reload; callbacks were not attached and test.run was not repeated.";
                state.Finished = true;
                state.FinishedAt = DateTime.UtcNow;
                FinalizeTestRun(state);
                return TestRunStateJson(state);
            }
            if (state.ExecuteDispatchCount != 1)
            {
                state.Status = "test_execution_receipt_unavailable_after_reload";
                state.ResultState = "test_execution_receipt_unavailable_after_reload";
                state.Message = "The persisted running test operation does not prove exactly one pre-Execute dispatch commitment; callbacks were not attached and test.run was not repeated.";
                state.Finished = true;
                state.FinishedAt = DateTime.UtcNow;
                FinalizeTestRun(state);
                return TestRunStateJson(state);
            }

            return string.Empty;
        }

        internal static TestRunState TestRunStateFromPersistedArtifact(string runId, string artifactPath, string persistedContent)
        {
            TestRunPreExecuteRefusal persistedRefusal = null;
            if (RequiresStrictTestRunRefusalValidation(persistedContent)
                && (!TryValidateTestRunArtifactIdentityForV3Transition(persistedContent, runId, artifactPath, out _, out _, out _)
                    || !TryReadPreExecuteRefusal(persistedContent, out persistedRefusal)))
            {
                return null;
            }
            var mode = PersistedTestRunMode(persistedContent);
            var filter = PersistedTestRunFilter(persistedContent);
            var maxSeconds = Clamp(ExtractTopLevelJsonInt(persistedContent, "max_seconds", 600), 1, 3600);
            var startedAt = ExtractTopLevelJsonDateTime(persistedContent, "started_at", DateTime.UtcNow);
            var lastUpdatedAt = ExtractTopLevelJsonDateTime(persistedContent, "last_update_at", startedAt);
            var resultState = ExtractTopLevelJsonString(persistedContent, "result");
            var persistedStatus = ExtractTopLevelJsonString(persistedContent, "status");
            var state = new TestRunState(runId, mode, filter, artifactPath, maxSeconds, false)
            {
                AllowUnsavedSceneChanges = ExtractTopLevelJsonBool(persistedContent, "allow_unsaved_scene_changes", false),
                ArgsHash = ExtractTopLevelJsonString(persistedContent, "args_hash"),
                CallerArgsHash = ExtractTopLevelJsonString(persistedContent, "caller_args_hash"),
                CreatedAt = ExtractTopLevelJsonString(persistedContent, "created_at"),
                StartedAt = startedAt,
                LastUpdatedAt = lastUpdatedAt,
                ArtifactPersisted = true,
                RecoveredAfterReload = true,
                RecoveryReason = "domain_reload_recovery",
                Lifecycle = "recovered_state_loaded",
                Status = IsTestRunInProgressStatus(persistedStatus) ? persistedStatus : "running",
                ResultState = string.IsNullOrEmpty(resultState) ? "unknown" : resultState,
                TestAssembly = PersistedTestRunAssembly(persistedContent),
                FilterMatchingMode = ExtractTopLevelJsonString(persistedContent, "filter_matching_mode"),
                CallbackRegistrationEpoch = Math.Max(1, ExtractTopLevelJsonInt(persistedContent, "callback_registration_epoch", 0) + 1),
                CallbackReRegisteredAfterReload = ExtractTopLevelJsonBool(persistedContent, "callback_re_registered_after_reload", false),
                FilterResolutionStatus = ExtractTopLevelJsonString(persistedContent, "filter_resolution_status"),
                FilterResolutionReason = ExtractTopLevelJsonString(persistedContent, "filter_resolution_reason"),
                FilterResolutionIdentity = ExtractTopLevelJsonString(persistedContent, "filter_resolution_identity"),
                FilterResolutionIdentityStatus = persistedStatus == AwaitingFilterRevalidationStatus
                    ? ExtractTopLevelJsonString(persistedContent, "filter_resolution_identity_status")
                    : "restored_after_reload",
                ResolvedFilterName = ExtractTopLevelJsonString(persistedContent, "resolved_filter_name"),
                ResolvedFilterIsSuite = ExtractTopLevelJsonBool(persistedContent, "resolved_filter_is_suite", false),
                ResolvedFilterCandidateCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "resolved_filter_candidate_count", 0)),
                ExecuteDispatchCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "execute_dispatch_count", 0)),
                ExecuteCallReturned = ExtractTopLevelJsonBool(persistedContent, "test_runner_execute_invoked", false),
            };
            if (persistedRefusal != null)
            {
                state.PreExecuteRefusal = persistedRefusal;
                state.Status = PreExecuteRefusedStatus;
                state.ResultState = PreExecuteRefusedStatus;
                state.Lifecycle = PreExecuteRefusedStatus;
                state.Finished = true;
                state.FinishedAt = lastUpdatedAt;
                state.Message = Bounded(ExtractTopLevelJsonString(persistedContent, "message"), MaxFailedTestMessageChars);
                state.FilterResolutionIdentityStatus = ExtractTopLevelJsonString(persistedContent, "filter_resolution_identity_status");
            }
            if (string.IsNullOrEmpty(state.CreatedAt))
            {
                state.CreatedAt = startedAt.ToString("o");
            }
            if (string.IsNullOrEmpty(state.FilterMatchingMode))
            {
                state.FilterMatchingMode = string.IsNullOrEmpty(filter) ? TestRunUnityModeOnlyMatchingMode : TestRunResolvedFilterMatchingMode;
            }

            state.Total = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "total", 0));
            state.Passed = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "passed", 0));
            state.Failed = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "failed", 0));
            state.Skipped = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "skipped", 0));
            state.TerminalCountsAuthoritative = ExtractTopLevelJsonBool(persistedContent, "terminal_counts_authoritative", false);
            if (state.PreExecuteRefusal != null) state.TerminalCountsAuthoritative = false;
            state.PassCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "pass_count", 0));
            state.FailCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "fail_count", 0));
            state.SkipCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "skip_count", 0));
            state.InconclusiveCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "inconclusive_count", 0));
            var assertCount = ExtractTopLevelJsonInt(persistedContent, "assert_count", -1);
            state.AssertCountAvailable = assertCount >= 0;
            state.AssertCount = Math.Max(0, assertCount);
            var testCaseCount = ExtractTopLevelJsonInt(persistedContent, "test_case_count", -1);
            state.TestCaseCountAvailable = testCaseCount >= 0;
            state.TestCaseCount = Math.Max(0, testCaseCount);
            state.TerminalResultIdentityStatus = ExtractTopLevelJsonString(persistedContent, "terminal_result_identity_status");
            state.TerminalResultLeafCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "terminal_result_leaf_count", 0));
            state.TerminalResultMatchingLeafCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "terminal_result_matching_leaf_count", 0));
            state.TerminalResultUnexpectedLeafCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "terminal_result_unexpected_leaf_count", 0));
            state.TerminalResultTraversalTruncated = ExtractTopLevelJsonBool(persistedContent, "terminal_result_traversal_truncated", false);
            state.TerminalResultIdentitiesTruncated = ExtractTopLevelJsonBool(persistedContent, "terminal_result_identities_truncated", false);
            state.TerminalResultIdentities.AddRange(ExtractTopLevelJsonStringArray(persistedContent, "terminal_result_identities", MaxTerminalResultIdentities));
            state.NonPassingEvidence.Restore(persistedContent);
            if (state.TerminalCountsAuthoritative && !HasCoherentAuthoritativeTerminalCounts(state))
            {
                state.TerminalCountsAuthoritative = false;
                state.ClearTerminalCountEvidence();
            }
            state.MatchedCountReliable = false;
            state.MatchedCountStatus = "unavailable_after_reload";
            state.MatchedTestsCoverageStatus = "omitted";
            state.MatchedTestsScanOmitted = true;
            state.MatchedTestsScanOmittedReason = string.IsNullOrEmpty(filter)
                ? "startup_callback_recovery_skips_reflection_preview"
                : "startup_callback_recovery_restores_filter_identity_without_preview";
            state.MatchedTestsMatchingMode = string.IsNullOrEmpty(filter)
                ? TestRunReflectionOmittedMatchingMode
                : "unity_test_runner_authoritative_catalog_recovered_identity_only";
            return state;
        }

        static string TryRecoverTestRunFromBatchmodeXmlAfterReload(string runId, string artifactPath, string persistedContent)
        {
            try
            {
                return MainThreadDispatcher.Run(() =>
                {
                    var state = TestRunStateFromPersistedArtifact(runId, artifactPath, persistedContent);
                    if (state == null || state.Finished) return string.Empty;
                    var invalidRecovery = FinalizeInvalidRecoveredTestRun(state);
                    if (!string.IsNullOrEmpty(invalidRecovery))
                    {
                        return invalidRecovery;
                    }

                    var recovered = RecoverTestRunFromBatchmodeXml(state);
                    if (!string.IsNullOrEmpty(recovered))
                    {
                        WriteArtifact(artifactPath, recovered);
                        ClearActiveTestRunSession(runId);
                    }
                    return recovered;
                }, TestRunReloadRecoveryTimeoutMs);
            }
            catch
            {
                return string.Empty;
            }
        }

        static string RecoverTestRunFromBatchmodeXml(TestRunState state)
        {
            var xmlPath = TestRunBatchmodeXmlPath(state.RunId);
            var fullXmlPath = AbsoluteProjectPath(xmlPath);
            if (!File.Exists(fullXmlPath))
            {
                return string.Empty;
            }

            var fileInfo = new FileInfo(fullXmlPath);
            if (fileInfo.Length <= 0 || fileInfo.Length > MaxTestRunBatchmodeXmlBytes)
            {
                return string.Empty;
            }

            if (!TryReadBatchmodeXmlSummary(fullXmlPath, out var summary))
            {
                return string.Empty;
            }

            if (string.IsNullOrEmpty(state.TestAssembly))
            {
                return string.Empty;
            }

            return TestRunRecoveredFromBatchmodeXmlJson(state, summary);
        }

        static bool TryReadBatchmodeXmlSummary(string fullXmlPath, out TestRunBatchmodeXmlSummary summary)
        {
            summary = null;
            try
            {
                var settings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null
                };
                var document = new XmlDocument
                {
                    XmlResolver = null
                };
                using (var reader = XmlReader.Create(fullXmlPath, settings))
                {
                    document.Load(reader);
                }

                var root = document.DocumentElement;
                var element = FindBatchmodeResultElement(document, root);
                if (element == null)
                {
                    return false;
                }

                var result = XmlAttribute(element, "result", "outcome", "status");
                var total = XmlIntAttribute(element, "total", "testcasecount", "test-case-count", "testcase-count", "testCaseCount");
                var passed = XmlIntAttribute(element, "passed", "pass");
                var failed = XmlIntAttribute(element, "failed", "failures", "failure", "errors");
                var skipped = XmlIntAttribute(element, "skipped", "ignored", "inconclusive");
                var durationMs = XmlDurationMsAttribute(element, "duration", "time");
                if (total <= 0)
                {
                    total = Math.Max(0, passed + failed + skipped);
                }

                var resultLower = (result ?? string.Empty).ToLowerInvariant();
                var zeroTests = total <= 0;
                var success = (resultLower.IndexOf("passed", StringComparison.Ordinal) >= 0 || resultLower.IndexOf("success", StringComparison.Ordinal) >= 0)
                    && failed == 0
                    && !zeroTests;
                if (string.IsNullOrEmpty(result))
                {
                    result = failed == 0 && !zeroTests ? "Passed" : zeroTests ? "NoTests" : "Failed";
                    success = failed == 0 && !zeroTests;
                }

                summary = new TestRunBatchmodeXmlSummary(result, success, total, passed, failed, skipped, durationMs, zeroTests);
                return true;
            }
            catch
            {
                summary = null;
                return false;
            }
        }

        static XmlElement FindBatchmodeResultElement(XmlDocument document, XmlElement root)
        {
            if (root == null)
            {
                return null;
            }

            if (string.Equals(root.Name, "test-run", StringComparison.OrdinalIgnoreCase)
                || string.Equals(root.Name, "test-results", StringComparison.OrdinalIgnoreCase)
                || string.Equals(root.Name, "test-suite", StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }

            var testRuns = document.GetElementsByTagName("test-run");
            if (testRuns.Count > 0)
            {
                return testRuns[0] as XmlElement;
            }

            var testResults = document.GetElementsByTagName("test-results");
            if (testResults.Count > 0)
            {
                return testResults[0] as XmlElement;
            }

            var testSuites = document.GetElementsByTagName("test-suite");
            return testSuites.Count > 0 ? testSuites[0] as XmlElement : null;
        }

        static string XmlAttribute(XmlElement element, params string[] names)
        {
            if (element == null)
            {
                return string.Empty;
            }

            for (var index = 0; index < names.Length; index++)
            {
                var value = element.GetAttribute(names[index]);
                if (!string.IsNullOrEmpty(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        static int XmlIntAttribute(XmlElement element, params string[] names)
        {
            var value = XmlAttribute(element, names);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? Math.Max(0, result) : 0;
        }

        static long XmlDurationMsAttribute(XmlElement element, params string[] names)
        {
            var value = XmlAttribute(element, names);
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            {
                return 0;
            }

            return (long)Math.Max(0, seconds * 1000.0);
        }

        static bool IsPersistedRunningTestRunArtifact(string content)
        {
            return !string.IsNullOrEmpty(content)
                && content.IndexOf("\"command\":\"test.run\"", StringComparison.Ordinal) >= 0
                && (content.IndexOf("\"status\":\"running\"", StringComparison.Ordinal) >= 0
                    || content.IndexOf("\"status\":\"execution_may_have_started\"", StringComparison.Ordinal) >= 0
                    || IsPersistedAwaitingFilterRevalidationArtifact(content));
        }

        static bool IsPersistedAwaitingFilterRevalidationArtifact(string content)
        {
            return !string.IsNullOrEmpty(content)
                && content.IndexOf("\"status\":\"" + AwaitingFilterRevalidationStatus + "\"", StringComparison.Ordinal) >= 0;
        }

        static string TestRunLostCallbackAfterDomainReloadJson(string runId, string artifactPath, string persistedContent)
        {
            var mode = PersistedTestRunMode(persistedContent);
            var filter = PersistedTestRunFilter(persistedContent);
            var testAssembly = PersistedTestRunAssembly(persistedContent);
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"test.run\",");
            builder.Append("\"artifact_schema_version\":").Append(CurrentTestRunArtifactSchemaVersion).Append(',');
            builder.Append("\"args_hash\":").Append(JsonUtil.String(ExtractTopLevelJsonString(persistedContent, "args_hash"))).Append(',');
            builder.Append("\"caller_args_hash\":").Append(JsonUtil.String(ExtractTopLevelJsonString(persistedContent, "caller_args_hash"))).Append(',');
            builder.Append("\"package_version\":").Append(JsonUtil.String(UnityBridgeRuntime.PackageVersion)).Append(',');
            builder.Append("\"unity_version\":").Append(JsonUtil.String(Application.unityVersion)).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"status\":\"unknown_after_reload\",");
            builder.Append("\"recovered_after_reload\":true,");
            builder.Append("\"callback_re_registered_after_reload\":false,");
            builder.Append("\"success\":false,");
            builder.Append("\"terminal_counts_authoritative\":false,");
            builder.Append("\"terminal_count_source\":\"unavailable\",");
            builder.Append("\"result\":\"unknown_after_reload\",");
            builder.Append("\"mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"test_mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"test_filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"assembly\":").Append(JsonUtil.String(testAssembly)).Append(',');
            builder.Append("\"test_assembly\":").Append(JsonUtil.String(testAssembly)).Append(',');
            var executeDispatchCount = Math.Max(0, ExtractTopLevelJsonInt(persistedContent, "execute_dispatch_count", 0));
            var executeCallReturned = ExtractTopLevelJsonBool(persistedContent, "test_runner_execute_invoked", false);
            builder.Append("\"execute_dispatch_count\":").Append(executeDispatchCount).Append(',');
            builder.Append("\"execute_dispatch_committed\":").Append(JsonUtil.Bool(executeDispatchCount == 1)).Append(',');
            builder.Append("\"test_runner_execute_invocation_attempted\":").Append(JsonUtil.Bool(executeDispatchCount == 1)).Append(',');
            builder.Append("\"test_runner_execute_invoked\":").Append(JsonUtil.Bool(executeCallReturned)).Append(',');
            builder.Append("\"write_command_repeated\":false,");
            builder.Append("\"write_replay_allowed\":false,");
            builder.Append("\"filter_resolution_identity\":").Append(JsonUtil.String(ExtractTopLevelJsonString(persistedContent, "filter_resolution_identity"))).Append(',');
            builder.Append("\"filter_resolution_identity_status\":\"callback_unavailable_after_reload\",");
            builder.Append("\"terminal_result_identity_status\":\"callback_unavailable_after_reload\",");
            builder.Append("\"persisted_status\":\"running\",");
            builder.Append("\"recovery_reason\":\"lost_callback_after_domain_reload\",");
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(artifactPath)).Append(',');
            AppendArtifactContract(builder, artifactPath, false, true);
            builder.Append(',');
            builder.Append("\"fallback_status\":").Append(TestRunFallbackStatusJson(runId, mode, filter, testAssembly)).Append(',');
            builder.Append("\"batchmode_fallback\":").Append(TestRunBatchmodeFallbackJson(runId, mode, filter, testAssembly, true)).Append(',');
            builder.Append("\"message\":\"Persisted running test.run artifact exists, but no in-memory Unity TestRunner callback state survived the current domain reload.\",");
            builder.Append("\"recovery_hint\":\"Do not treat unknown_after_reload as pass or fail. Read fallback_status first; if XML exists, poll test.run.status for recovery. If XML is missing, use test.run.batchmode_fallback and unity.editor.installations.list to prepare an operator-supervised run outside the open Editor, or cancel MCP tracking if the run is no longer intended.\",");
            builder.Append("\"next_valid_actions\":");
            AppendStringArray(builder, new List<string>
            {
                "job.artifact.read",
                "test.run.status",
                "test.run.batchmode_fallback",
                "unity.editor.installations.list",
                "test.run.cancel",
                "editor.save_prompt.get",
                "sgg_unity_projects",
                "diagnostics.report_problem"
            });
            builder.Append('}');
            return builder.ToString();
        }

        static string TestRunFallbackStatusJson(string runId, string mode, string filter, string testAssembly)
        {
            var xmlPath = TestRunBatchmodeXmlPath(runId);
            var logPath = TestRunBatchmodeLogPath(runId);
            var fullXmlPath = AbsoluteProjectPath(xmlPath);
            var fullLogPath = AbsoluteProjectPath(logPath);
            var xmlExists = File.Exists(fullXmlPath);
            var logExists = File.Exists(fullLogPath);
            var xmlSizeBytes = xmlExists ? Math.Max(0, new FileInfo(fullXmlPath).Length) : 0;
            var logSizeBytes = logExists ? Math.Max(0, new FileInfo(fullLogPath).Length) : 0;
            var xmlTooLarge = xmlExists && xmlSizeBytes > MaxTestRunBatchmodeXmlBytes;
            TestRunBatchmodeXmlSummary summary = null;
            var xmlReadable = xmlExists && !xmlTooLarge && TryReadBatchmodeXmlSummary(fullXmlPath, out summary);
            var assemblyKnown = !string.IsNullOrEmpty(testAssembly);
            var status = assemblyKnown ? TestRunFallbackEvidenceStatus(xmlExists, xmlTooLarge, xmlReadable, summary) : "assembly_unknown";

            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"status\":").Append(JsonUtil.String(status)).Append(',');
            builder.Append("\"mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"test_mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"test_filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"assembly\":").Append(JsonUtil.String(testAssembly)).Append(',');
            builder.Append("\"test_assembly\":").Append(JsonUtil.String(testAssembly)).Append(',');
            builder.Append("\"fallback_available\":").Append(JsonUtil.Bool(assemblyKnown)).Append(',');
            builder.Append("\"terminal_counts_authoritative\":false,");
            builder.Append("\"terminal_count_source\":\"batchmode_xml_non_authoritative\",");
            builder.Append("\"xml_result_path\":").Append(JsonUtil.String(xmlPath)).Append(',');
            builder.Append("\"xml_result_exists\":").Append(JsonUtil.Bool(xmlExists)).Append(',');
            builder.Append("\"xml_result_too_large\":").Append(JsonUtil.Bool(xmlTooLarge)).Append(',');
            builder.Append("\"xml_size_bytes\":").Append(xmlSizeBytes).Append(',');
            builder.Append("\"xml_readable\":").Append(JsonUtil.Bool(xmlReadable)).Append(',');
            builder.Append("\"log_path\":").Append(JsonUtil.String(logPath)).Append(',');
            builder.Append("\"log_exists\":").Append(JsonUtil.Bool(logExists)).Append(',');
            builder.Append("\"log_size_bytes\":").Append(logSizeBytes).Append(',');
            if (summary != null)
            {
                builder.Append("\"xml_summary\":{\"result\":").Append(JsonUtil.String(summary.Result));
                builder.Append(",\"success\":").Append(JsonUtil.Bool(summary.Success));
                builder.Append(",\"total\":").Append(summary.Total);
                builder.Append(",\"passed\":").Append(summary.Passed);
                builder.Append(",\"failed\":").Append(summary.Failed);
                builder.Append(",\"skipped\":").Append(summary.Skipped);
                builder.Append(",\"duration_ms\":").Append(summary.DurationMs);
                builder.Append(",\"zero_tests\":").Append(JsonUtil.Bool(summary.ZeroTests)).Append("},");
            }

            builder.Append("\"no_xml_diagnostics\":").Append(TestRunBatchmodeNoXmlDiagnosticsJson(xmlPath, logPath)).Append(',');
            if (!assemblyKnown)
            {
                builder.Append("\"unavailable_reason\":\"assembly_unknown\",");
            }
            builder.Append("\"recovery_hint\":\"Use xml_result_exists/xml_readable before deciding next steps. Do not blindly repeat test.run after unknown_after_reload; prefer test.run.batchmode_fallback plus unity.editor.installations.list for supervised external evidence recovery, or test.run.cancel if the previous run is no longer needed.\",");
            builder.Append("\"next_valid_actions\":");
            AppendStringArray(builder, new List<string> { "test.run.batchmode_fallback", "unity.editor.installations.list", "test.run.status", "job.artifact.read", "test.run.cancel", "diagnostics.report_problem" });
            builder.Append('}');
            return builder.ToString();
        }

        static string TestRunFallbackEvidenceStatus(bool xmlExists, bool xmlTooLarge, bool xmlReadable, TestRunBatchmodeXmlSummary summary)
        {
            if (!xmlExists)
            {
                return "no_xml";
            }

            if (xmlTooLarge)
            {
                return "xml_too_large";
            }

            if (!xmlReadable)
            {
                return "xml_unreadable";
            }

            return summary != null && summary.ZeroTests ? "xml_zero_tests" : "xml_available";
        }

        static string TestRunRecoveredFromBatchmodeXmlJson(TestRunState state, TestRunBatchmodeXmlSummary summary)
        {
            var status = summary.ZeroTests
                ? "batchmode_xml_zero_tests"
                : summary.Success ? "batchmode_xml_non_authoritative" : "batchmode_xml_failed";
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"test.run\",");
            builder.Append("\"artifact_schema_version\":").Append(CurrentTestRunArtifactSchemaVersion).Append(',');
            builder.Append("\"args_hash\":").Append(JsonUtil.String(state.ArgsHash)).Append(',');
            builder.Append("\"caller_args_hash\":").Append(JsonUtil.String(state.CallerArgsHash)).Append(',');
            builder.Append("\"package_version\":").Append(JsonUtil.String(UnityBridgeRuntime.PackageVersion)).Append(',');
            builder.Append("\"unity_version\":").Append(JsonUtil.String(Application.unityVersion)).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(state.RunId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(state.RunId)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(status)).Append(',');
            builder.Append("\"lifecycle\":\"recovered_from_batchmode_xml_after_reload\",");
            builder.Append("\"recovered_after_reload\":true,");
            builder.Append("\"callback_re_registered_after_reload\":false,");
            builder.Append("\"recovery_reason\":\"batchmode_xml_after_domain_reload\",");
            builder.Append("\"success\":false,");
            builder.Append("\"terminal_counts_authoritative\":false,");
            builder.Append("\"terminal_count_source\":\"batchmode_xml_non_authoritative\",");
            builder.Append("\"result\":").Append(JsonUtil.String(summary.Result)).Append(',');
            builder.Append("\"mode\":").Append(JsonUtil.String(state.Mode)).Append(',');
            builder.Append("\"test_mode\":").Append(JsonUtil.String(state.Mode)).Append(',');
            builder.Append("\"filter\":").Append(JsonUtil.String(state.Filter)).Append(',');
            builder.Append("\"test_filter\":").Append(JsonUtil.String(state.Filter)).Append(',');
            builder.Append("\"assembly\":").Append(JsonUtil.String(state.TestAssembly)).Append(',');
            builder.Append("\"test_assembly\":").Append(JsonUtil.String(state.TestAssembly)).Append(',');
            builder.Append("\"execute_dispatch_count\":").Append(Math.Max(0, state.ExecuteDispatchCount)).Append(',');
            builder.Append("\"execute_dispatch_committed\":").Append(JsonUtil.Bool(state.ExecuteDispatchCount == 1)).Append(',');
            builder.Append("\"test_runner_execute_invocation_attempted\":").Append(JsonUtil.Bool(state.ExecuteDispatchCount == 1)).Append(',');
            builder.Append("\"test_runner_execute_invoked\":").Append(JsonUtil.Bool(state.ExecuteCallReturned)).Append(',');
            builder.Append("\"write_command_repeated\":false,");
            builder.Append("\"write_replay_allowed\":false,");
            builder.Append("\"filter_resolution_identity\":").Append(JsonUtil.String(state.FilterResolutionIdentity)).Append(',');
            builder.Append("\"filter_resolution_identity_status\":\"batchmode_xml_non_authoritative\",");
            builder.Append("\"terminal_result_identity_status\":\"batchmode_xml_non_authoritative\",");
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(state.ArtifactPath)).Append(',');
            AppendArtifactContract(builder, state.ArtifactPath, true, true);
            builder.Append(",\"batchmode_fallback\":").Append(TestRunBatchmodeFallbackJson(state.RunId, state.Mode, state.Filter, state.TestAssembly, true));
            builder.Append(",\"total\":").Append(summary.Total);
            builder.Append(",\"passed\":").Append(summary.Passed);
            builder.Append(",\"failed\":").Append(summary.Failed);
            builder.Append(",\"skipped\":").Append(summary.Skipped);
            builder.Append(",\"duration_ms\":").Append(summary.DurationMs);
            builder.Append(",\"zero_tests\":").Append(JsonUtil.Bool(summary.ZeroTests));
            if (summary.ZeroTests)
            {
                builder.Append(",\"diagnostic\":{\"code\":\"batchmode_xml_zero_tests\",\"message\":\"Batchmode XML exists but reports zero tests; check testPlatform, test filter, compile state and test.list discovery before treating the run as passed.\"}");
            }
            else if (summary.Success)
            {
                builder.Append(",\"diagnostic\":{\"code\":\"batchmode_xml_non_authoritative\",\"message\":\"Batchmode XML reports success but is not authoritative in-editor callback evidence for the reserved operation; test.run was not repeated.\"}");
            }
            builder.Append(",\"failure_summary\":{\"failed\":true");
            builder.Append(",\"compiler_errors\":false");
            builder.Append(",\"build_errors\":0");
            builder.Append(",\"failed_tests\":").Append(summary.Failed);
            builder.Append(",\"zero_tests\":").Append(JsonUtil.Bool(summary.ZeroTests));
            builder.Append(",\"duration_ms\":").Append(summary.DurationMs);
            builder.Append(",\"output_size_bytes\":").Append(ArtifactSizeBytes(TestRunBatchmodeXmlPath(state.RunId)));
            builder.Append('}');
            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, new List<string> { "job.artifact.read", "compile.status", "logs.console.query", "test.list", "diagnostics.report_problem" });
            builder.Append('}');
            return builder.ToString();
        }

        static string TestRunBatchmodeFallbackResultJson(string runId, string artifactPath, bool artifactExists, bool artifactTooLarge, string mode, string filter, string testAssembly)
        {
            var assemblyKnown = !string.IsNullOrEmpty(testAssembly);
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"command\":\"test.run.batchmode_fallback\",");
            builder.Append("\"package_version\":").Append(JsonUtil.String(UnityBridgeRuntime.PackageVersion)).Append(',');
            builder.Append("\"unity_version\":").Append(JsonUtil.String(Application.unityVersion)).Append(',');
            builder.Append("\"operation_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"run_id\":").Append(JsonUtil.String(runId)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(assemblyKnown ? "fallback_plan" : "assembly_unknown")).Append(',');
            builder.Append("\"success\":").Append(JsonUtil.Bool(assemblyKnown)).Append(',');
            builder.Append("\"terminal_counts_authoritative\":false,");
            builder.Append("\"terminal_count_source\":\"fallback_plan_non_authoritative\",");
            builder.Append("\"artifact_path\":").Append(JsonUtil.String(artifactPath)).Append(',');
            AppendArtifactContract(builder, artifactPath, false, artifactExists);
            builder.Append(",\"artifact_too_large\":").Append(JsonUtil.Bool(artifactTooLarge));
            builder.Append(",\"mode\":").Append(JsonUtil.String(mode));
            builder.Append(",\"test_mode\":").Append(JsonUtil.String(mode));
            builder.Append(",\"filter\":").Append(JsonUtil.String(filter));
            builder.Append(",\"test_filter\":").Append(JsonUtil.String(filter));
            builder.Append(",\"assembly\":").Append(JsonUtil.String(testAssembly));
            builder.Append(",\"test_assembly\":").Append(JsonUtil.String(testAssembly));
            builder.Append(",\"batchmode_fallback\":").Append(TestRunBatchmodeFallbackJson(runId, mode, filter, testAssembly, true));
            builder.Append(",\"message\":\"Run the provided Unity CLI args outside the open editor if in-editor TestRunner callback state was lost, then poll test.run.status for the same operation_id after the XML result exists.\"");
            builder.Append(",\"recovery_hint\":\"Use unity.editor.installations.list to select an installed Unity Editor executable when UNITY_EXE or SGG_UNITY_EDITOR_VERSION is not already configured; do not run batchmode against the same project while an interactive Editor owns its Library.\"");
            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, new List<string> { "unity.editor.installations.list", "test.run.status", "job.artifact.read", "test.run.cancel", "diagnostics.report_problem" });
            builder.Append('}');
            return builder.ToString();
        }

        static string TestRunBatchmodeFallbackJson(string runId, string mode, string filter, string testAssembly, bool includeUnityArgs)
        {
            var xmlPath = TestRunBatchmodeXmlPath(runId);
            var logPath = TestRunBatchmodeLogPath(runId);
            var fullXmlPath = AbsoluteProjectPath(xmlPath);
            var xmlExists = File.Exists(fullXmlPath);
            var xmlTooLarge = xmlExists && new FileInfo(fullXmlPath).Length > MaxTestRunBatchmodeXmlBytes;
            var assemblyKnown = !string.IsNullOrEmpty(testAssembly);
            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"contract\":").Append(JsonUtil.String(TestRunBatchmodeFallbackContract)).Append(',');
            builder.Append("\"supported\":").Append(JsonUtil.Bool(assemblyKnown)).Append(',');
            builder.Append("\"available\":").Append(JsonUtil.Bool(assemblyKnown)).Append(',');
            builder.Append("\"status\":").Append(JsonUtil.String(assemblyKnown ? "fallback_plan" : "assembly_unknown")).Append(',');
            builder.Append("\"terminal_counts_authoritative\":false,");
            builder.Append("\"terminal_count_source\":\"fallback_plan_non_authoritative\",");
            builder.Append("\"manual_only\":true,");
            builder.Append("\"copy_safe\":false,");
            builder.Append("\"same_project_open_editor_unsafe\":true,");
            builder.Append("\"requires_separate_project_or_closed_editor\":true,");
            builder.Append("\"purpose\":\"Recover terminal test evidence after PlayMode/domain reload loses in-editor TestRunner callbacks\",");
            builder.Append("\"quit_policy\":\"-quit is intentionally omitted from unity_cli_args because reported batchmode -runTests -quit runs can exit 0 before writing TestRunner XML; supervise the external process and stop it only after XML/log evidence is available.\",");
            builder.Append("\"test_platform\":").Append(JsonUtil.String(TestRunBatchmodePlatform(mode))).Append(',');
            builder.Append("\"mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"test_mode\":").Append(JsonUtil.String(mode)).Append(',');
            builder.Append("\"filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"test_filter\":").Append(JsonUtil.String(filter)).Append(',');
            builder.Append("\"assembly\":").Append(JsonUtil.String(testAssembly)).Append(',');
            builder.Append("\"test_assembly\":").Append(JsonUtil.String(testAssembly)).Append(',');
            builder.Append("\"xml_result_path\":").Append(JsonUtil.String(xmlPath)).Append(',');
            builder.Append("\"log_path\":").Append(JsonUtil.String(logPath)).Append(',');
            builder.Append("\"xml_result_exists\":").Append(JsonUtil.Bool(xmlExists)).Append(',');
            builder.Append("\"xml_result_too_large\":").Append(JsonUtil.Bool(xmlTooLarge)).Append(',');
            builder.Append("\"no_xml_diagnostics\":").Append(TestRunBatchmodeNoXmlDiagnosticsJson(xmlPath, logPath)).Append(',');
            builder.Append("\"readable_with\":\"job.artifact.read\",");
            builder.Append("\"poll_after_xml_written\":{\"tool\":\"sgg_unity_call\",\"command\":\"test.run.status\",\"args_json\":");
            builder.Append(JsonUtil.String("{\"operation_id\":\"" + runId + "\"}")).Append('}');
            if (!assemblyKnown)
            {
                builder.Append(",\"unavailable_reason\":\"assembly_unknown\"");
                builder.Append(",\"supervised_next_action\":\"Use test.list and a reviewed current-schema test.run to recover the exact assembly before preparing any external fallback.\"");
            }
            else if (includeUnityArgs)
            {
                builder.Append(",\"unity_cli_args\":");
                AppendStringArray(builder, TestRunBatchmodeUnityArgs(mode, filter, testAssembly, xmlPath, logPath));
            }
            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, new List<string> { "test.run.batchmode_fallback", "unity.editor.installations.list", "test.run.status", "job.artifact.read", "test.run.cancel", "diagnostics.report_problem" });
            builder.Append('}');
            return builder.ToString();
        }

        static string TestRunBatchmodeNoXmlDiagnosticsJson(string xmlPath, string logPath)
        {
            var fullXmlPath = AbsoluteProjectPath(xmlPath);
            var fullLogPath = AbsoluteProjectPath(logPath);
            var xmlExists = File.Exists(fullXmlPath);
            var logExists = File.Exists(fullLogPath);
            var logSizeBytes = logExists ? Math.Max(0, new FileInfo(fullLogPath).Length) : 0;
            var tail = logExists ? ReadFileTail(fullLogPath, MaxTestRunBatchmodeLogDiagnosticChars) : string.Empty;
            var evidence = string.Empty;
            var message = string.Empty;
            var classification = xmlExists
                ? "xml_available"
                : ClassifyBatchmodeNoXmlLog(tail, out evidence, out message);
            if (string.IsNullOrEmpty(message))
            {
                message = xmlExists
                    ? "Batchmode XML exists; poll test.run.status for recovery."
                    : logExists
                        ? "Batchmode XML is missing; inspect the bounded log evidence before retrying."
                        : "Batchmode XML and log are both missing; the external runner likely did not start or did not reach Unity logging.";
            }

            var builder = new StringBuilder();
            builder.Append('{');
            builder.Append("\"xml_result_path\":").Append(JsonUtil.String(xmlPath)).Append(',');
            builder.Append("\"xml_result_exists\":").Append(JsonUtil.Bool(xmlExists)).Append(',');
            builder.Append("\"log_path\":").Append(JsonUtil.String(logPath)).Append(',');
            builder.Append("\"log_exists\":").Append(JsonUtil.Bool(logExists)).Append(',');
            builder.Append("\"log_size_bytes\":").Append(logSizeBytes).Append(',');
            builder.Append("\"classification\":").Append(JsonUtil.String(classification)).Append(',');
            builder.Append("\"message\":").Append(JsonUtil.String(message));
            if (!string.IsNullOrEmpty(evidence))
            {
                builder.Append(",\"evidence\":").Append(JsonUtil.String(Bounded(evidence.Replace("\r", " ").Replace("\n", " "), 500)));
            }

            builder.Append(",\"next_valid_actions\":");
            AppendStringArray(builder, BatchmodeNoXmlNextActions(classification));
            builder.Append('}');
            return builder.ToString();
        }

        static string ClassifyBatchmodeNoXmlLog(string tail, out string evidence, out string message)
        {
            evidence = string.Empty;
            message = string.Empty;
            if (string.IsNullOrEmpty(tail))
            {
                message = "Batchmode XML is missing and no fallback log is available.";
                return "missing_xml_and_log";
            }

            if (TryFindLogLine(tail, out evidence, "error CS", "Scripts have compiler errors", "Compilation failed"))
            {
                message = "Batchmode log already contains compile errors; fix compile.errors.recent instead of retrying missing XML.";
                return "compile_errors_before_test_runner_xml";
            }

            if (TryFindLogLine(tail, out evidence, "Unity.Licensing.Client", "global mutex", "LicensingClient", "UPM", "Package Manager", "IPC"))
            {
                message = "Batchmode log points to licensing, Package Manager or IPC startup before TestRunner XML was written.";
                return "licensing_or_upm_startup_before_test_runner_xml";
            }

            if (TryFindLogLine(tail, out evidence, "Library Redirect Path: Library/", "LibraryInitializing", "ArtifactDB", "SourceAssetDB"))
            {
                message = "Batchmode log stalled during Unity startup or Library initialization before TestRunner XML was written.";
                return "unity_startup_stalled_before_test_runner_xml";
            }

            if (!ContainsOrdinalIgnoreCase(tail, "TestRunner") && !ContainsOrdinalIgnoreCase(tail, "runTests") && !ContainsOrdinalIgnoreCase(tail, "test-run"))
            {
                message = "Batchmode log does not show TestRunner startup evidence before XML went missing.";
                return "test_runner_not_started_before_xml";
            }

            message = "Batchmode log exists but XML is missing; inspect log tail and retry only after the startup/compile state is understood.";
            return "xml_missing_after_test_runner_attempt";
        }

        static List<string> BatchmodeNoXmlNextActions(string classification)
        {
            if (classification == "compile_errors_before_test_runner_xml")
            {
                return new List<string> { "compile.errors.recent", "compile.status", "logs.console.query", "test.list", "diagnostics.report_problem" };
            }

            if (classification == "licensing_or_upm_startup_before_test_runner_xml"
                || classification == "unity_startup_stalled_before_test_runner_xml")
            {
                return new List<string> { "diagnostics.unity_modal.status", "diagnostics.recovery.suggest", "logs.console.query", "diagnostics.report_problem" };
            }

            return new List<string> { "logs.console.query", "test.run.batchmode_fallback", "test.run.status", "diagnostics.report_problem" };
        }

        static bool TryReadRawTestRunArtifact(string artifactPath, out string content, out bool artifactTooLarge)
        {
            content = string.Empty;
            artifactTooLarge = false;
            var fullPath = AbsoluteProjectPath(artifactPath);
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var fileInfo = new FileInfo(fullPath);
            if (fileInfo.Length > MaxTestRunArtifactChars)
            {
                artifactTooLarge = true;
                return true;
            }

            content = File.ReadAllText(fullPath);
            return true;
        }

        internal static List<string> TestRunBatchmodeUnityArgs(string mode, string filter, string testAssembly, string xmlPath, string logPath)
        {
            if (string.IsNullOrEmpty(testAssembly)
                || testAssembly.Length > 160
                || !IsSafeTestAssemblyName(testAssembly)
                || filter == null
                || filter.Length > 160
                || filter.IndexOfAny(new char[] { '\r', '\n', '\0' }) >= 0)
            {
                return new List<string>();
            }

            var args = new List<string>
            {
                "-batchmode",
                "-projectPath",
                ProjectPaths.ProjectRoot,
                "-runTests",
                "-testPlatform",
                TestRunBatchmodePlatform(mode),
                "-assemblyNames",
                testAssembly,
                "-testResults",
                AbsoluteProjectPath(xmlPath),
                "-logFile",
                AbsoluteProjectPath(logPath)
            };
            if (!string.IsNullOrEmpty(filter))
            {
                args.Add("-testFilter");
                args.Add(filter);
            }

            return args;
        }

        static string TestRunBatchmodePlatform(string mode)
        {
            return string.Equals(mode, "playmode", StringComparison.OrdinalIgnoreCase) ? "PlayMode" : "EditMode";
        }

        static string TestRunBatchmodeXmlPath(string runId)
        {
            return TestRunBatchmodeFallbackRoot + "/" + runId + ".xml";
        }

        static string TestRunBatchmodeLogPath(string runId)
        {
            return TestRunBatchmodeFallbackRoot + "/" + runId + ".log";
        }

        static string PersistedTestRunMode(string content)
        {
            var mode = ExtractTopLevelJsonString(content, "mode").Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(mode))
            {
                mode = ExtractTopLevelJsonString(content, "test_mode").Trim().ToLowerInvariant();
            }

            return mode == "playmode" || mode == "editmode" ? mode : "editmode";
        }

        static string PersistedTestRunFilter(string content)
        {
            var filter = ExtractTopLevelJsonString(content, "filter").Trim();
            if (string.IsNullOrEmpty(filter))
            {
                filter = ExtractTopLevelJsonString(content, "test_filter").Trim();
            }

            return filter.Length <= 160 && filter.IndexOfAny(new char[] { '\r', '\n', '\0' }) < 0 ? filter : string.Empty;
        }

        internal static string PersistedTestRunAssembly(string content)
        {
            var testAssembly = ExtractTopLevelJsonString(content, "test_assembly").Trim();
            if (string.IsNullOrEmpty(testAssembly))
            {
                testAssembly = ExtractTopLevelJsonString(content, "assembly").Trim();
            }

            return testAssembly.Length <= 160 && IsSafeTestAssemblyName(testAssembly) ? testAssembly : string.Empty;
        }

        static bool ExtractTopLevelJsonBool(string json, string property, bool fallback)
        {
            var colon = FindTopLevelJsonPropertyColon(json, property);
            if (colon < 0)
            {
                return fallback;
            }

            var value = IndexOfNextNonWhitespace(json, colon + 1);
            if (value < 0)
            {
                return fallback;
            }

            if (value + 4 <= json.Length && string.CompareOrdinal(json, value, "true", 0, 4) == 0)
            {
                return true;
            }

            if (value + 5 <= json.Length && string.CompareOrdinal(json, value, "false", 0, 5) == 0)
            {
                return false;
            }

            return fallback;
        }

        static int ExtractTopLevelJsonInt(string json, string property, int fallback)
        {
            var colon = FindTopLevelJsonPropertyColon(json, property);
            if (colon < 0)
            {
                return fallback;
            }

            var start = IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0)
            {
                return fallback;
            }

            var end = start;
            if (end < json.Length && json[end] == '-')
            {
                end++;
            }

            while (end < json.Length && char.IsDigit(json[end]))
            {
                end++;
            }

            return end > start && int.TryParse(json.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
        }

        static long ExtractTopLevelJsonLong(string json, string property, long fallback)
        {
            var colon = FindTopLevelJsonPropertyColon(json, property);
            if (colon < 0) return fallback;
            var start = IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0) return fallback;
            var end = start;
            if (end < json.Length && json[end] == '-') end++;
            while (end < json.Length && char.IsDigit(json[end])) end++;
            return end > start && long.TryParse(json.Substring(start, end - start), NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : fallback;
        }

        static DateTime ExtractTopLevelJsonDateTime(string json, string property, DateTime fallback)
        {
            var value = ExtractTopLevelJsonString(json, property);
            return DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed.ToUniversalTime()
                : fallback;
        }

        static string ExtractTopLevelJsonString(string json, string property)
        {
            var colon = FindTopLevelJsonPropertyColon(json, property);
            if (colon < 0)
            {
                return string.Empty;
            }

            var quote = IndexOfNextNonWhitespace(json, colon + 1);
            if (quote < 0 || json[quote] != '"')
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            var escaped = false;
            for (var index = quote + 1; index < json.Length; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    builder.Append(UnescapeJsonCharacter(character));
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
                    return builder.ToString();
                }

                builder.Append(character);
            }

            return string.Empty;
        }

        internal static List<string> ExtractTopLevelJsonStringArray(string json, string property, int maxItems)
        {
            var values = new List<string>();
            var colon = FindTopLevelJsonPropertyColon(json, property);
            var start = colon < 0 ? -1 : IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0 || start >= json.Length || json[start] != '[' || maxItems <= 0)
            {
                return values;
            }

            var builder = new StringBuilder();
            var inString = false;
            var escaped = false;
            for (var index = start + 1; index < json.Length && values.Count < maxItems; index++)
            {
                var character = json[index];
                if (escaped)
                {
                    builder.Append(UnescapeJsonCharacter(character));
                    escaped = false;
                    continue;
                }
                if (inString && character == '\\')
                {
                    escaped = true;
                    continue;
                }
                if (character == '"')
                {
                    if (inString)
                    {
                        values.Add(builder.ToString());
                        builder.Length = 0;
                    }
                    inString = !inString;
                    continue;
                }
                if (inString)
                {
                    builder.Append(character);
                }
                else if (character == ']')
                {
                    break;
                }
            }

            return values;
        }

        static int FindTopLevelJsonPropertyColon(string json, string property)
        {
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(property))
            {
                return -1;
            }

            var pattern = "\"" + property + "\"";
            var depth = 0;
            var inString = false;
            var escaped = false;
            for (var index = 0; index < json.Length; index++)
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

                if (inString)
                {
                    if (character == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (depth == 1 && index + pattern.Length <= json.Length && string.CompareOrdinal(json, index, pattern, 0, pattern.Length) == 0)
                {
                    var colon = IndexOfNextNonWhitespace(json, index + pattern.Length);
                    if (colon >= 0 && json[colon] == ':')
                    {
                        return colon;
                    }
                }

                if (character == '"')
                {
                    inString = true;
                    continue;
                }

                if (character == '{')
                {
                    depth++;
                }
                else if (character == '}')
                {
                    depth = Math.Max(0, depth - 1);
                }
            }

            return -1;
        }

        static bool IsSafeTestRunId(string runId)
        {
            if (string.IsNullOrEmpty(runId) || runId.Length != 29 || !runId.StartsWith("test-", StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 5; index < runId.Length; index++)
            {
                var character = runId[index];
                if (index == 13 || index == 20)
                {
                    if (character != '-')
                    {
                        return false;
                    }

                    continue;
                }

                if (index < 20)
                {
                    if (!char.IsDigit(character))
                    {
                        return false;
                    }

                    continue;
                }

                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        internal static bool TestRunIdsMatch(string operationId, string runId)
        {
            return string.IsNullOrEmpty(operationId)
                || string.IsNullOrEmpty(runId)
                || string.Equals(operationId, runId, StringComparison.Ordinal);
        }

        static bool IsSafeSha256Hash(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal))
            {
                return false;
            }

            for (var index = 7; index < value.Length; index++)
            {
                var character = value[index];
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }

            return true;
        }

        static bool TestRunOperationExists(string runId, string artifactPath)
        {
            lock (s_testRunsGate)
            {
                if (s_testRuns.ContainsKey(runId) || s_retainedTestRunPublications.ContainsKey(runId) || string.Equals(s_activeTestRunId, runId, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            try
            {
                return File.Exists(AbsoluteProjectPath(artifactPath));
            }
            catch
            {
                return true;
            }
        }

        static bool IsSafeTestAssemblyName(string value)
        {
            for (var index = 0; index < value.Length; index++)
            {
                var character = value[index];
                var asciiLetterOrDigit = character >= 'A' && character <= 'Z'
                    || character >= 'a' && character <= 'z'
                    || character >= '0' && character <= '9';
                if (!asciiLetterOrDigit && character != '.' && character != '_' && character != '-')
                {
                    return false;
                }
            }

            return true;
        }

        static void RegisterTestRunUpdate()
        {
            if (s_testRunUpdateRegistered)
            {
                return;
            }

            EditorApplication.update += UpdateTestRuns;
            s_testRunUpdateRegistered = true;
        }

        static void UpdateTestRuns()
        {
            var now = DateTime.UtcNow;
            var remove = new List<string>();
            List<TestRunState> activeStates;
            List<TestRunState> retainedStates;
            lock (s_testRunsGate)
            {
                activeStates = new List<TestRunState>(s_testRuns.Values);
                retainedStates = new List<TestRunState>(s_retainedTestRunPublications.Values);
            }
            foreach (var state in activeStates)
            {
                RetryTestRunPublication(state);
                if (TryFinalizeRecoveredFilterRevalidation(state, now))
                {
                    continue;
                }
                if (TryClassifyRecoveredPlayModeRunAfterExit(state, null))
                {
                    continue;
                }

                if (IsTestRunInProgressStatus(state.Status) && (now - state.StartedAt).TotalSeconds > state.MaxSeconds)
                {
                    state.Status = "timeout";
                    state.ResultState = "timeout";
                    state.Message = "Test run did not finish before max_seconds";
                    state.FinishedAt = now;
                    FinalizeTestRun(state);
                }

                if (!IsTestRunInProgressStatus(state.Status) && (now - state.FinishedAt).TotalMinutes > 10)
                {
                    remove.Add(state.RunId);
                }
            }
            foreach (var state in retainedStates)
            {
                RetryTestRunPublication(state);
            }
            lock (s_testRunsGate)
            {
                foreach (var runId in remove)
                {
                    s_testRuns.Remove(runId);
                }

                if (s_testRuns.Count == 0 && !HasPendingRetainedTestRunPublication())
                {
                    EditorApplication.update -= UpdateTestRuns;
                    s_testRunUpdateRegistered = false;
                }
            }
        }

        static bool TryFinalizeRecoveredFilterRevalidation(TestRunState state, DateTime now)
        {
            if (state == null || state.Status != AwaitingFilterRevalidationStatus)
            {
                return false;
            }

            RefreshRecoveredFilterIdentity(state);
            if (state.FilterResolutionIdentityStatus == "revalidation_pending"
                && (now - state.StartedAt).TotalSeconds <= state.MaxSeconds)
            {
                return true;
            }
            if (state.FilterResolutionIdentityStatus == "revalidation_pending")
            {
                state.FilterResolutionIdentityStatus = "revalidation_timeout_after_reload";
            }

            state.Status = "completed";
            state.Finished = true;
            state.FinishedAt = now;
            FinalizeTestRun(state);
            return true;
        }

        internal static bool RequiresRecoveredFilterRevalidationWait(TestRunState state)
        {
            return state != null
                && state.RecoveredAfterReload
                && !string.IsNullOrEmpty(state.Filter)
                && state.FilterResolutionIdentityStatus == "revalidation_pending";
        }

        internal static bool TryDeferRecoveredFilterRevalidation(TestRunState state)
        {
            if (!RequiresRecoveredFilterRevalidationWait(state))
            {
                return false;
            }

            state.Status = AwaitingFilterRevalidationStatus;
            state.Finished = false;
            PersistTestRunSnapshot(state, AwaitingFilterRevalidationStatus);
            return true;
        }

        internal static void FinalizeTestRun(TestRunState state)
        {
            if (state.ArtifactWritten)
            {
                ClearActiveTestRunSession(state.RunId);
                return;
            }

            MarkTestRunActivity(state, string.IsNullOrEmpty(state.Status) ? "finished" : state.Status);
            state.Finished = true;
            if (state.FinishedAt == default(DateTime))
            {
                state.FinishedAt = DateTime.UtcNow;
            }

            ClassifyResolvedTestRunOutcome(state);

            state.Success = IsSuccessfulTestRunOutcome(state);
            InvokeReflectedIfExists(state.Api, "UnregisterCallbacks", state.Callbacks);
            ClearActiveTestRunSession(state.RunId);
            PersistTestRunSnapshot(state, state.Lifecycle);
            if (state.Mode == "playmode" && state.ExecuteDispatchCount == 1)
            {
                UnityTestDiscoveryCatalog.InvalidateAfterPlayModeRun();
            }
        }

        internal static bool IsSuccessfulTestRunOutcome(TestRunState state)
        {
            return state != null
                && HasCoherentAuthoritativeTerminalCounts(state)
                && state.Total > 0
                && state.Failed == 0
                && state.Status == "completed"
                && !IsFailedResultState(state.ResultState);
        }

        internal static void ClassifyResolvedTestRunOutcome(TestRunState state)
        {
            if (state == null || state.Status != "completed")
            {
                return;
            }
            if (state.TerminalCountsAuthoritative && state.Total == 0)
            {
                state.TerminalResultIdentityStatus = "zero_execution";
                state.Status = string.IsNullOrEmpty(state.Filter) ? "zero_tests_executed" : "resolved_filter_zero_execution";
                state.ResultState = state.Status;
                state.Message = string.IsNullOrEmpty(state.Filter)
                    ? "Unity TestRunner completed without executing any authoritative test leaves; this run is not acceptance evidence."
                    : "Unity TestRunner completed the bound filtered operation without executing any authoritative test leaves; this is a typed failure and test.run was not repeated.";
                return;
            }
            if (string.IsNullOrEmpty(state.Filter))
            {
                return;
            }

            if (state.RecoveredAfterReload)
            {
                if (state.FilterResolutionIdentityStatus == "drift_after_reload")
                {
                    state.Status = "test_filter_drift_after_reload";
                    state.ResultState = state.Status;
                    state.Message = "The authoritative filter resolution changed after domain reload, so the terminal result cannot prove the reserved test operation.";
                    return;
                }
                if (state.FilterResolutionIdentityStatus != "verified_after_reload"
                    && !CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(state))
                {
                    state.Status = "test_filter_identity_unavailable_after_reload";
                    state.ResultState = state.Status;
                    state.Message = "The exact preflight filter identity could not be verified after domain reload; test.run was not repeated.";
                    return;
                }
            }
            else if (!IsSafeSha256Hash(state.FilterResolutionIdentity))
            {
                state.Status = "test_operation_result_identity_unavailable";
                state.ResultState = state.Status;
                state.Message = "The completed filtered test operation has no valid preflight filter identity.";
                return;
            }
            if (state.ResolvedFilterCandidateCount > 1)
            {
                return;
            }

            if (state.TerminalResultIdentityStatus == "mismatch")
            {
                state.Status = "test_operation_result_identity_mismatch";
                state.ResultState = state.Status;
                state.Message = "Authoritative terminal result leaves do not match the exact filter identity reserved before TestRunner execution.";
            }
            else if (state.TerminalResultIdentityStatus != "matched")
            {
                state.Status = "test_operation_result_identity_unavailable";
                state.ResultState = state.Status;
                state.Message = "Authoritative terminal result leaves could not be bound to the exact filter identity reserved before TestRunner execution.";
            }
        }

        internal static bool CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(TestRunState state)
        {
            if (state == null
                || !state.RecoveredAfterReload
                || string.IsNullOrEmpty(state.Filter)
                || state.Status != "completed"
                || !state.Finished
                || !state.CallbackReRegisteredAfterReload
                || state.ExecuteDispatchCount != 1
                || !string.Equals(state.FilterResolutionStatus, "resolved", StringComparison.Ordinal)
                || !IsSafeSha256Hash(state.FilterResolutionIdentity)
                || state.ResolvedFilterCandidateCount != 1
                || string.IsNullOrEmpty(state.ResolvedFilterName)
                || !HasCoherentAuthoritativeTerminalCounts(state)
                || state.Total <= 0
                || !string.Equals(state.TerminalResultIdentityStatus, "matched", StringComparison.Ordinal)
                || state.TerminalResultLeafCount <= 0
                || state.TerminalResultMatchingLeafCount != state.TerminalResultLeafCount
                || state.TerminalResultUnexpectedLeafCount != 0
                || state.TerminalResultTraversalTruncated)
            {
                return false;
            }

            return state.FilterResolutionIdentityStatus == "revalidation_unavailable"
                || state.FilterResolutionIdentityStatus == "revalidation_timeout_after_reload";
        }

        static void SetActiveTestRunSession(string runId)
        {
            lock (s_testRunsGate)
            {
                s_activeTestRunId = runId;
            }

            SessionState.SetString(ActiveTestRunSessionStateKey, runId);
        }

        static void ClearActiveTestRunSession(string runId)
        {
            lock (s_testRunsGate)
            {
                if (string.Equals(s_activeTestRunId, runId, StringComparison.Ordinal))
                {
                    s_activeTestRunId = string.Empty;
                }
            }

            Action clearPersistedMarker = () =>
            {
                if (string.Equals(SessionState.GetString(ActiveTestRunSessionStateKey, string.Empty), runId, StringComparison.Ordinal))
                {
                    SessionState.EraseString(ActiveTestRunSessionStateKey);
                }
            };
            if (MainThreadDispatcher.IsMainThread)
            {
                clearPersistedMarker();
            }
            else
            {
                MainThreadDispatcher.Enqueue(clearPersistedMarker);
            }
        }

        internal static bool IsCurrentTestRunCallback(TestRunState state, int callbackRegistrationEpoch)
        {
            if (state == null)
            {
                return false;
            }

            lock (s_testRunsGate)
            {
                return s_testRuns.TryGetValue(state.RunId, out var current)
                    && ReferenceEquals(current, state)
                    && IsTestRunInProgressStatus(current.Status)
                    && current.CallbackRegistrationEpoch == callbackRegistrationEpoch;
            }
        }

        static bool PersistTestRunSnapshot(TestRunState state, string lifecycle)
        {
            MarkTestRunActivity(state, lifecycle);
            if (!string.Equals(state.PendingPublicationLifecycle, lifecycle, StringComparison.Ordinal))
            {
                ResetTestRunPublicationBudget(state, lifecycle);
            }
            return PublishTestRunState(state);
        }

        static void ResetTestRunPublicationBudget(TestRunState state, string lifecycle)
        {
            state.PendingPublicationLifecycle = lifecycle ?? string.Empty;
            state.ArtifactPublicationAttempts = 0;
            state.ArtifactPublicationDeadlineUtc = TestRunPublicationUtcNow(state).AddSeconds(TestRunPublicationRetryWindowSeconds);
            state.NextArtifactPublicationAttempt = 0d;
        }

        static bool PublishTestRunState(TestRunState state)
        {
            if (state == null)
            {
                return false;
            }
            if (string.IsNullOrEmpty(state.PendingPublicationLifecycle))
            {
                ResetTestRunPublicationBudget(state, state.Lifecycle);
            }

            state.ArtifactPublicationAttempts++;
            state.ArtifactPublicationState = "committed";
            state.ArtifactPublicationErrorCode = string.Empty;
            var previousPublishedLifecycle = state.LastPublishedLifecycle;
            var previousArtifactPersisted = state.ArtifactPersisted;
            var previousArtifactWritten = state.ArtifactWritten;
            state.LastPublishedLifecycle = state.Lifecycle;
            state.ArtifactPersisted = true;
            if (state.Finished) state.ArtifactWritten = true;
            var content = TestRunStateJson(state);
            var publication = state.PublicationHooks == null
                ? TryWriteFixedRootArtifactAtomic(state.ArtifactPath, content, TestRunArtifactRoot, "Test run artifact")
                : state.PublicationHooks.PublishArtifact(state.ArtifactPath, content);
            if (publication.Committed)
            {
                state.ArtifactPersisted = true;
                state.PendingArtifactPublication = false;
                if (state.Finished)
                {
                    state.ArtifactWritten = true;
                }
                ReleaseRetainedTestRunPublication(state.RunId);
                return true;
            }

            state.LastPublishedLifecycle = previousPublishedLifecycle;
            state.ArtifactPersisted = previousArtifactPersisted;
            state.ArtifactWritten = previousArtifactWritten;
            state.ArtifactPublicationErrorCode = string.IsNullOrEmpty(publication.ErrorCode) ? "artifact_publication_failed" : publication.ErrorCode;
            var retry = publication.TransientContention
                && state.ArtifactPublicationAttempts < MaxTestRunPublicationAttempts
                && TestRunPublicationUtcNow(state) < state.ArtifactPublicationDeadlineUtc;
            state.PendingArtifactPublication = retry;
            state.ArtifactPublicationState = retry ? "pending" : "failed";
            state.NextArtifactPublicationAttempt = TestRunPublicationEditorTime(state) + TestRunPublicationRetrySeconds;
            if (retry || state.Finished)
            {
                RetainTestRunPublication(state);
            }
            return false;
        }

        static void RetainTestRunPublication(TestRunState state)
        {
            if (state == null)
            {
                return;
            }
            var retained = false;
            lock (s_testRunsGate)
            {
                if (state.Finished)
                {
                    if (s_testRuns.TryGetValue(state.RunId, out var current) && ReferenceEquals(current, state))
                    {
                        s_testRuns.Remove(state.RunId);
                    }
                    s_retainedTestRunPublications[state.RunId] = state;
                    retained = true;
                }
                else if (!s_testRuns.TryGetValue(state.RunId, out var current) || !ReferenceEquals(current, state))
                {
                    s_testRuns[state.RunId] = state;
                }
            }
            if (retained)
            {
                ClearActiveTestRunSession(state.RunId);
                StoreRetainedTestRunPublicationSession(state);
            }
            RegisterTestRunUpdate();
        }

        static void StoreRetainedTestRunPublicationSession(TestRunState state)
        {
            if (state == null || !state.Finished)
            {
                return;
            }
            var content = TestRunStateJson(state);
            if (content.Length <= 0 || content.Length > MaxTestRunArtifactChars)
            {
                return;
            }
            try
            {
                var ids = RetainedTestRunPublicationSessionIds();
                if (!ids.Contains(state.RunId))
                {
                    if (ids.Count >= MaxRetainedTestRunPublications)
                    {
                        return;
                    }
                    ids.Add(state.RunId);
                }
                SessionState.SetString(RetainedTestRunPublicationSessionStateKeyPrefix + state.RunId, content);
                SessionState.SetString(RetainedTestRunPublicationIndexSessionStateKey, string.Join(",", ids.ToArray()));
            }
            catch
            {
                // The in-memory retained entry remains authoritative for this Editor generation.
            }
        }

        static List<string> RetainedTestRunPublicationSessionIds()
        {
            var result = new List<string>();
            var raw = SessionState.GetString(RetainedTestRunPublicationIndexSessionStateKey, string.Empty);
            foreach (var value in raw.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (IsSafeTestRunId(value) && !result.Contains(value) && result.Count < MaxRetainedTestRunPublications)
                {
                    result.Add(value);
                }
            }
            return result;
        }

        static void ReleaseRetainedTestRunPublication(string runId)
        {
            var removed = false;
            lock (s_testRunsGate)
            {
                removed = s_retainedTestRunPublications.Remove(runId);
            }
            if (!removed)
            {
                return;
            }
            try
            {
                SessionState.EraseString(RetainedTestRunPublicationSessionStateKeyPrefix + runId);
                var ids = RetainedTestRunPublicationSessionIds();
                ids.Remove(runId);
                if (ids.Count == 0) SessionState.EraseString(RetainedTestRunPublicationIndexSessionStateKey);
                else SessionState.SetString(RetainedTestRunPublicationIndexSessionStateKey, string.Join(",", ids.ToArray()));
            }
            catch
            {
                // A stale SessionState entry is revalidated against its embedded operation identity on reload.
            }
        }

        static void RestoreRetainedTestRunPublicationsOnDomainLoad()
        {
            foreach (var runId in RetainedTestRunPublicationSessionIds())
            {
                try
                {
                    var content = SessionState.GetString(RetainedTestRunPublicationSessionStateKeyPrefix + runId, string.Empty);
                    var state = TestRunStateFromRetainedPublicationSession(runId, content);
                    if (state == null)
                    {
                        if (!RequiresStrictTestRunRefusalValidation(content)) SessionState.EraseString(RetainedTestRunPublicationSessionStateKeyPrefix + runId);
                        continue;
                    }
                    lock (s_testRunsGate)
                    {
                        s_retainedTestRunPublications[runId] = state;
                    }
                }
                catch
                {
                    SessionState.EraseString(RetainedTestRunPublicationSessionStateKeyPrefix + runId);
                }
            }
            lock (s_testRunsGate)
            {
                if (HasPendingRetainedTestRunPublication()) RegisterTestRunUpdate();
            }
        }

        internal static TestRunState TestRunStateFromRetainedPublicationSession(string runId, string content, TestRunPublicationHooks publicationHooks = null)
        {
            var artifactPath = TestRunArtifactRoot + "/" + runId + ".json";
            if (string.IsNullOrEmpty(content) || content.Length > MaxTestRunArtifactChars
                || !TryValidateTestRunArtifactIdentityForV3Transition(content, runId, artifactPath, out _, out _, out _))
            {
                return null;
            }
            var publicationState = ExtractTopLevelJsonString(content, "artifact_publication_state");
            if (publicationState != "pending" && publicationState != "failed")
            {
                return null;
            }
            var state = TestRunStateFromPersistedArtifact(runId, artifactPath, content);
            if (state == null)
            {
                return null;
            }
            state.PublicationHooks = publicationHooks;
            if (state.PreExecuteRefusal == null)
            {
                state.Status = ExtractTopLevelJsonString(content, "status");
                state.Lifecycle = ExtractTopLevelJsonString(content, "lifecycle");
                state.ResultState = ExtractTopLevelJsonString(content, "result");
            }
            state.FilterResolutionIdentityStatus = ExtractTopLevelJsonString(content, "filter_resolution_identity_status");
            state.Message = ExtractTopLevelJsonString(content, "message");
            state.ExceptionType = ExtractTopLevelJsonString(content, "exception_type");
            state.StackTrace = Bounded(ExtractTopLevelJsonString(content, "stack_trace"), MaxFailedTestStackTraceChars);
            state.Finished = true;
            state.FinishedAt = DateTime.UtcNow;
            ClassifyResolvedTestRunOutcome(state);
            state.Success = IsSuccessfulTestRunOutcome(state);
            state.ArtifactPersisted = ExtractTopLevelJsonBool(content, "artifact_persisted", false);
            state.ArtifactWritten = false;
            state.ArtifactPublicationAttempts = Math.Max(0, ExtractTopLevelJsonInt(content, "artifact_publication_attempts", 0));
            state.ArtifactPublicationState = publicationState;
            state.ArtifactPublicationErrorCode = ExtractTopLevelJsonString(content, "artifact_publication_error_code");
            state.LastPublishedLifecycle = ExtractTopLevelJsonString(content, "last_published_lifecycle");
            state.PendingPublicationLifecycle = state.Lifecycle;
            state.ArtifactPublicationDeadlineUtc = ExtractTopLevelJsonDateTime(content, "artifact_publication_deadline_utc", TestRunPublicationUtcNow(state));
            state.PendingArtifactPublication = publicationState == "pending"
                && state.ArtifactPublicationAttempts < MaxTestRunPublicationAttempts
                && TestRunPublicationUtcNow(state) < state.ArtifactPublicationDeadlineUtc;
            if (!state.PendingArtifactPublication && publicationState == "pending")
            {
                state.ArtifactPublicationState = "failed";
                state.ArtifactPublicationErrorCode = "publication_retry_window_exhausted";
                StoreRetainedTestRunPublicationSession(state);
            }
            state.NextArtifactPublicationAttempt = TestRunPublicationEditorTime(state) + TestRunPublicationRetrySeconds;
            return state;
        }

        static bool HasPendingRetainedTestRunPublication()
        {
            foreach (var state in s_retainedTestRunPublications.Values)
            {
                if (state.PendingArtifactPublication) return true;
            }
            return false;
        }

        static void RetryTestRunPublication(TestRunState state)
        {
            if (state == null || !state.PendingArtifactPublication || TestRunPublicationEditorTime(state) < state.NextArtifactPublicationAttempt)
            {
                return;
            }
            PublishTestRunState(state);
        }

        static DateTime TestRunPublicationUtcNow(TestRunState state)
        {
            return state.PublicationHooks == null ? DateTime.UtcNow : state.PublicationHooks.UtcNow();
        }

        static double TestRunPublicationEditorTime(TestRunState state)
        {
            return state.PublicationHooks == null ? EditorApplication.timeSinceStartup : state.PublicationHooks.EditorTime();
        }

        static bool RequiresStrictTestRunRefusalValidation(string content)
        {
            // Only valid, unique object documents without decoded refusal markers retain legacy parsing.
            // Malformed/duplicate documents also route to rejection, never to legacy repair or marker clearing.
            return !BridgeProtocol.TryHasObjectMarker(content, PreExecuteRefusedStatus, s_testRunRefusalMarkerFields, "pre_execute_refusal", out var hasMarker)
                || hasMarker;
        }

        static bool TryReadPreExecuteRefusal(string content, out TestRunPreExecuteRefusal refusal)
        {
            refusal = null;
            // Valid legacy receipts bypass these refusal-specific fields; parser-invalid documents do not.
            if (!BridgeProtocol.IsValidUniqueObjectJson(content)) return false;
            TestRunRefusalMarkerProbe markers;
            try { markers = JsonUtility.FromJson<TestRunRefusalMarkerProbe>(content); }
            catch { return false; }
            if (markers == null || markers.status != PreExecuteRefusedStatus
                || markers.result != PreExecuteRefusedStatus || markers.lifecycle != PreExecuteRefusedStatus
                || !HasExactTopLevelJsonLiteral(content, "artifact_schema_version", "3")
                || !HasExactTopLevelJsonLiteral(content, "operation_created", "true")
                || !HasExactTopLevelJsonLiteral(content, "terminal", "true")
                || !HasExactTopLevelJsonLiteral(content, "execute_dispatch_count", "0")
                || !HasExactTopLevelJsonLiteral(content, "execute_dispatch_committed", "false")
                || !HasExactTopLevelJsonLiteral(content, "test_runner_execute_invocation_attempted", "false")
                || !HasExactTopLevelJsonLiteral(content, "test_runner_execute_invoked", "false")
                || !HasExactTopLevelJsonLiteral(content, "terminal_counts_authoritative", "false")
                || !HasExactTopLevelJsonLiteral(content, "success", "false")
                || !HasExactTopLevelJsonLiteral(content, "test_result_success", "false"))
            {
                return false;
            }
            var colon = FindTopLevelJsonPropertyColon(content, "pre_execute_refusal");
            var start = colon < 0 ? -1 : IndexOfNextNonWhitespace(content, colon + 1);
            if (start < 0 || content[start] != '{') return false;
            // Start at the located top-level object, rather than selecting a nested namesake.
            var fieldsJson = JsonUtil.ExtractObject("{\"value\":" + content.Substring(start), "value");
            if (!HasTopLevelJsonStringValue(fieldsJson, "code") || !HasTopLevelJsonStringValue(fieldsJson, "reason")) return false;
            TestRunPreExecuteRefusalFields fields;
            try { fields = JsonUtility.FromJson<TestRunPreExecuteRefusalFields>(fieldsJson); }
            catch { return false; }
            if (fields == null || string.IsNullOrWhiteSpace(fields.code) || fields.code.Length > 96
                || string.IsNullOrWhiteSpace(fields.reason) || fields.reason.Length > 160)
            {
                return false;
            }
            refusal = new TestRunPreExecuteRefusal(fields.code, fields.reason);
            return true;
        }

        static bool HasExactTopLevelJsonLiteral(string json, string property, string literal)
        {
            var colon = FindTopLevelJsonPropertyColon(json, property);
            var start = colon < 0 ? -1 : IndexOfNextNonWhitespace(json, colon + 1);
            if (start < 0 || start + literal.Length > json.Length
                || string.CompareOrdinal(json, start, literal, 0, literal.Length) != 0) return false;
            var end = IndexOfNextNonWhitespace(json, start + literal.Length);
            return end >= 0 && (json[end] == ',' || json[end] == '}');
        }

        static bool HasTopLevelJsonStringValue(string json, string property)
        {
            var colon = FindTopLevelJsonPropertyColon(json, property);
            var start = colon < 0 ? -1 : IndexOfNextNonWhitespace(json, colon + 1);
            return start >= 0 && json[start] == '"';
        }

        [Serializable]
        sealed class TestRunRefusalMarkerProbe
        {
            public string status = string.Empty;
            public string result = string.Empty;
            public string lifecycle = string.Empty;
        }

        [Serializable]
        sealed class TestRunPreExecuteRefusalFields
        {
            public string code = string.Empty;
            public string reason = string.Empty;
        }

        internal static void MarkTestRunActivity(TestRunState state, string lifecycle)
        {
            if (state == null)
            {
                return;
            }

            if (!string.IsNullOrEmpty(lifecycle))
            {
                state.Lifecycle = lifecycle;
            }

            state.LastUpdatedAt = DateTime.UtcNow;
        }

        internal static bool TryValidateTerminalTestCounts(
            long? passCount,
            long? failCount,
            long? skipCount,
            long? inconclusiveCount,
            out TestRunTerminalCounts counts)
        {
            counts = default(TestRunTerminalCounts);
            if (!passCount.HasValue || !failCount.HasValue || !skipCount.HasValue || !inconclusiveCount.HasValue
                || passCount.Value < 0 || failCount.Value < 0 || skipCount.Value < 0 || inconclusiveCount.Value < 0
                || passCount.Value > int.MaxValue || failCount.Value > int.MaxValue
                || skipCount.Value > int.MaxValue || inconclusiveCount.Value > int.MaxValue)
            {
                return false;
            }

            try
            {
                var passed = checked((int)passCount.Value);
                var failed = checked((int)failCount.Value);
                var skipped = checked((int)skipCount.Value + (int)inconclusiveCount.Value);
                var total = checked(passed + failed + skipped);
                counts = new TestRunTerminalCounts(
                    total,
                    passed,
                    failed,
                    skipped,
                    passed,
                    failed,
                    (int)skipCount.Value,
                    (int)inconclusiveCount.Value);
                return true;
            }
            catch (OverflowException)
            {
                return false;
            }
        }

        internal static bool TryApplyTerminalTestCounts(
            TestRunState state,
            long? passCount,
            long? failCount,
            long? skipCount,
            long? inconclusiveCount,
            long? assertCount = null,
            long? testCaseCount = null)
        {
            if (state == null || !TryValidateTerminalTestCounts(passCount, failCount, skipCount, inconclusiveCount, out var counts))
            {
                if (state != null)
                {
                    state.Total = 0;
                    state.Passed = 0;
                    state.Failed = 0;
                    state.Skipped = 0;
                    state.TerminalCountsAuthoritative = false;
                    state.ClearTerminalCountEvidence();
                }

                return false;
            }

            state.Total = counts.Total;
            state.Passed = counts.Passed;
            state.Failed = counts.Failed;
            state.Skipped = counts.Skipped;
            state.PassCount = counts.PassCount;
            state.FailCount = counts.FailCount;
            state.SkipCount = counts.SkipCount;
            state.InconclusiveCount = counts.InconclusiveCount;
            state.AssertCountAvailable = TryBoundOptionalCount(assertCount, out var boundedAssertCount);
            state.AssertCount = boundedAssertCount;
            state.TestCaseCountAvailable = TryBoundOptionalCount(testCaseCount, out var boundedTestCaseCount);
            state.TestCaseCount = boundedTestCaseCount;
            state.TerminalCountsAuthoritative = true;
            return true;
        }

        internal static bool HasCoherentAuthoritativeTerminalCounts(TestRunState state)
        {
            if (state == null
                || !state.TerminalCountsAuthoritative
                || !TryValidateTerminalTestCounts(state.PassCount, state.FailCount, state.SkipCount, state.InconclusiveCount, out var counts))
            {
                return false;
            }

            return state.Total == counts.Total
                && state.Passed == counts.Passed
                && state.Failed == counts.Failed
                && state.Skipped == counts.Skipped;
        }

        static bool TryBoundOptionalCount(long? value, out int bounded)
        {
            bounded = 0;
            if (!value.HasValue || value.Value < 0 || value.Value > int.MaxValue)
            {
                return false;
            }

            bounded = (int)value.Value;
            return true;
        }

        static void CaptureUnsavedSceneWarning(BridgeRuntimeState runtimeState, TestRunState testRunState)
        {
            if (runtimeState == null || testRunState == null)
            {
                return;
            }

            testRunState.HasUnsavedSceneChangesAtStart = runtimeState.HasUnsavedSceneChanges;
            testRunState.DirtySceneCountAtStart = runtimeState.DirtySceneCount;
            testRunState.DirtyScenesAtStart = runtimeState.DirtyScenes ?? Array.Empty<DirtySceneInfo>();
        }

        static TestDiscoveryResult ModeOnlyDiscovery(string mode)
        {
            var omitted = new TestDiscoveryResult(string.Empty, mode, MaxTestRunMatchedTests);
            omitted.MarkScanOmitted("test_mode_only_filter_runs_all_tests_without_reflection_preview");
            return omitted;
        }

        internal static CommandExecutionResult RequireModeOnlyTestCatalog(UnityTestDiscoveryCatalog.CatalogQueryResult catalog, string mode, string assembly, int expectedGeneration)
        {
            var status = catalog == null ? "unavailable" : catalog.Status;
            var generation = catalog == null ? 0 : catalog.DiscoveryGeneration;
            if (expectedGeneration > 0 && generation != expectedGeneration)
            {
                return CommandExecutionResult.Error("catalog_stale", "test.run refused because the mode-only TestRunner discovery generation changed before Execute",
                    "{\"expected_discovery_generation\":" + expectedGeneration
                    + ",\"observed_discovery_generation\":" + generation
                    + ",\"test_runner_execute_invoked\":false,\"write_command_repeated\":false,\"write_replay_allowed\":false}");
            }
            if (status == "ready" && catalog.RunnableLeafCount > 0) return null;
            var code = status == "pending" ? "test_filter_resolution_pending"
                : status != "ready" ? "test_filter_resolution_unavailable" : "test_filter_unresolved";
            var reason = status == "ready" ? "mode_only_no_runnable_leaves" : catalog == null ? "snapshot_missing" : catalog.FailureReason;
            return CommandExecutionResult.Error(code, "test.run refused because the requested mode has no current authoritative executable TestRunner catalog",
                TestFilterResolutionDetailsJson(mode, string.Empty, assembly, reason, status, null, generation));
        }

        static CommandExecutionResult TestFilterResolutionError(UnityTestDiscoveryCatalog.FilterResolution resolution)
        {
            var status = resolution == null ? "unresolved" : resolution.Status;
            var code = status == "pending"
                ? "test_filter_resolution_pending"
                : status == "unavailable" || status == "incomplete"
                    ? "test_filter_resolution_unavailable"
                : status == "ambiguous"
                    ? "test_filter_ambiguous"
                    : "test_filter_unresolved";
            var message = code == "test_filter_resolution_pending"
                ? "test.run refused because authoritative Unity TestRunner discovery is still pending; Execute was not invoked"
                : code == "test_filter_resolution_unavailable"
                    ? "test.run refused because authoritative Unity TestRunner discovery is unavailable or incomplete; Execute was not invoked"
                : code == "test_filter_ambiguous"
                    ? "test.run refused because the filter has multiple non-equivalent authoritative Unity TestRunner candidates"
                    : "test.run refused because the filter did not resolve to one authoritative runnable Unity TestRunner candidate";
            var mode = resolution == null ? string.Empty : resolution.Mode;
            var filter = resolution == null ? string.Empty : resolution.Filter;
            var assembly = resolution == null ? string.Empty : resolution.AssemblyName;
            var reason = resolution == null ? "resolution_missing" : resolution.Reason;
            return CommandExecutionResult.Error(code, message, TestFilterResolutionDetailsJson(mode, filter, assembly, reason, status, resolution == null ? null : resolution.Candidates, resolution == null ? 0 : resolution.DiscoveryGeneration));
        }

        static string TestFilterResolutionDetailsJson(string mode, string filter, string assembly, string reason, string resolutionStatus, List<UnityTestDiscoveryCatalog.TestCandidate> candidates, int discoveryGeneration = 0)
        {
            var builder = new StringBuilder();
            var coverageStatus = resolutionStatus == "pending"
                ? "pending"
                : resolutionStatus == "unavailable" || reason == "test_runner_api_unavailable" || reason.StartsWith("retrieve_test_list_", StringComparison.Ordinal)
                    ? "unavailable"
                    : resolutionStatus == "incomplete"
                        ? "incomplete"
                    : reason == "member_unreadable" || reason == "children_missing" || reason == "children_unreadable" || reason == "children_not_enumerable" || reason == "children_shape_incomplete" || reason.StartsWith("children_enumeration_failed", StringComparison.Ordinal) || reason.StartsWith("max_", StringComparison.Ordinal) || reason == "string_too_long" || reason == "test_name_missing" || reason == "test_assembly_missing" || reason == "adaptor_cycle_or_duplicate" || reason.StartsWith("snapshot_build_failed", StringComparison.Ordinal)
                        ? "incomplete"
                        : "complete";
            builder.Append('{');
            builder.Append("\"resolution_status\":").Append(JsonUtil.String(resolutionStatus));
            builder.Append(",\"coverage_status\":").Append(JsonUtil.String(coverageStatus));
            builder.Append(",\"discovery_generation\":").Append(discoveryGeneration <= 0 ? "null" : discoveryGeneration.ToString(CultureInfo.InvariantCulture));
            builder.Append(",\"mode\":").Append(JsonUtil.String(mode));
            builder.Append(",\"filter\":").Append(JsonUtil.String(filter));
            builder.Append(",\"assembly\":").Append(JsonUtil.String(assembly));
            builder.Append(",\"reason\":").Append(JsonUtil.String(reason));
            builder.Append(",\"test_runner_execute_invoked\":false");
            builder.Append(",\"candidates\":[");
            var count = candidates == null ? 0 : Math.Min(candidates.Count, UnityTestDiscoveryCatalog.MaxResolutionCandidates);
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                candidates[index].AppendJson(builder);
            }

            builder.Append("],\"next_valid_actions\":");
            List<string> actions;
            if (resolutionStatus == "pending")
            {
                actions = new List<string> { "test.list" };
            }
            else if (resolutionStatus == "unavailable" || resolutionStatus == "incomplete")
            {
                actions = reason == "retrieve_test_list_timeout"
                    ? new List<string> { "test.framework.status", "compile.refresh_and_wait", "test.list" }
                    : new List<string> { "test.framework.status", "compile.refresh_and_wait" };
            }
            else
            {
                actions = new List<string> { "test.list", "compile.refresh_and_wait" };
            }

            AppendStringArray(builder, actions);
            builder.Append('}');
            return builder.ToString();
        }

        static void CaptureMatchedTests(TestRunState testRunState, TestDiscoveryResult discovery)
        {
            if (testRunState == null || discovery == null)
            {
                return;
            }

            testRunState.FilterMatchingMode = TestRunUnityModeOnlyMatchingMode;
            testRunState.MatchedTestsMatchingMode = discovery.ScanOmitted ? TestRunReflectionOmittedMatchingMode : TestRunReflectionMatchingMode;
            testRunState.MatchedCount = discovery.MatchedCount;
            testRunState.MatchedCountReliable = discovery.MatchedCountReliable;
            testRunState.MatchedCountStatus = discovery.CountReliabilityStatus;
            testRunState.MatchedTestsCoverageStatus = discovery.CoverageStatus;
            testRunState.MatchedTestsTruncated = discovery.MatchedTestsTruncated;
            testRunState.MatchedTestsScanIncomplete = discovery.ScanIncomplete;
            testRunState.MatchedTestsScanReason = discovery.ScanReason;
            testRunState.MatchedTestsScanOmitted = discovery.ScanOmitted;
            testRunState.MatchedTestsScanOmittedReason = discovery.ScanOmittedReason;
            testRunState.MatchedTestsScannedAssemblies = discovery.ScannedAssemblies;
            testRunState.MatchedTestsScannedTypes = discovery.ScannedTypes;
            testRunState.MatchedTestsScannedMethods = discovery.ScannedMethods;
            testRunState.MatchedTests.Clear();
            testRunState.MatchedTests.AddRange(discovery.Tests);
            ResolveTestRunFilterContract(testRunState);
        }

        static void CaptureAuthoritativeMatchedTests(TestRunState testRunState, UnityTestDiscoveryCatalog.FilterResolution resolution)
        {
            if (testRunState == null || resolution == null)
            {
                return;
            }

            testRunState.FilterMatchingMode = TestRunResolvedFilterMatchingMode;
            testRunState.MatchedTestsMatchingMode = "unity_test_runner_authoritative_catalog";
            testRunState.FilterResolutionStatus = resolution.Status;
            testRunState.FilterResolutionReason = resolution.Reason;
            testRunState.MatchedCount = resolution.Candidates.Count;
            testRunState.MatchedCountReliable = true;
            testRunState.MatchedCountStatus = "exact";
            testRunState.MatchedTestsCoverageStatus = "complete";
            testRunState.MatchedTestsTruncated = resolution.Candidates.Count > MaxTestRunMatchedTests;
            testRunState.MatchedTestsScanIncomplete = false;
            testRunState.MatchedTestsScanReason = string.Empty;
            testRunState.MatchedTestsScanOmitted = false;
            testRunState.MatchedTestsScanOmittedReason = string.Empty;
            testRunState.MatchedTestsScannedAssemblies = 0;
            testRunState.MatchedTestsScannedTypes = 0;
            testRunState.MatchedTestsScannedMethods = 0;
            testRunState.MatchedTests.Clear();
            for (var index = 0; index < resolution.Candidates.Count && index < MaxTestRunMatchedTests; index++)
            {
                testRunState.MatchedTests.Add(TestMethodInfo.FromCatalog(resolution.Candidates[index]));
            }

            testRunState.UnityTestNameFilters.Clear();
            for (var index = 0; index < resolution.TestNames.Count; index++)
            {
                AddUnique(testRunState.UnityTestNameFilters, resolution.TestNames[index], UnityTestDiscoveryCatalog.MaxResolutionNames);
            }

            testRunState.FilterContractSuggestedFilters.Clear();
            for (var index = 0; index < testRunState.MatchedTests.Count; index++)
            {
                AddUnique(testRunState.FilterContractSuggestedFilters, testRunState.MatchedTests[index].RecommendedFilter, MaxTestRunMatchedTests);
            }

            testRunState.FilterNormalized = !UnityTestNamesEqualInput(testRunState);
            testRunState.FilterContractWarning = false;
            testRunState.FilterContractWarningReason = string.Empty;
            BindResolvedFilterIdentity(testRunState, resolution);
        }

        static void BindResolvedFilterIdentity(TestRunState state, UnityTestDiscoveryCatalog.FilterResolution resolution)
        {
            state.FilterResolutionIdentity = TestFilterResolutionIdentity(resolution);
            state.FilterResolutionIdentityStatus = string.IsNullOrEmpty(state.FilterResolutionIdentity) ? "binding_unavailable" : "bound_before_execute";
            state.ResolvedFilterCandidateCount = resolution.Candidates.Count;
            state.ResolvedFilterName = resolution.TestNames.Count == 1 ? resolution.TestNames[0] : string.Empty;
            state.ResolvedFilterIsSuite = resolution.Candidates.Count == 1 && resolution.Candidates[0].IsSuite;
        }

        internal static string TestFilterResolutionIdentity(UnityTestDiscoveryCatalog.FilterResolution resolution)
        {
            if (resolution == null || !resolution.IsResolved || resolution.Candidates.Count == 0 || resolution.TestNames.Count == 0)
            {
                return string.Empty;
            }

            var candidates = new List<UnityTestDiscoveryCatalog.TestCandidate>(resolution.Candidates);
            candidates.Sort(UnityTestDiscoveryCatalog.TestCandidate.Compare);
            var names = new List<string>(resolution.TestNames);
            names.Sort(StringComparer.Ordinal);
            var builder = new StringBuilder();
            AppendIdentityValue(builder, resolution.Mode);
            AppendIdentityValue(builder, resolution.ResolvedAssembly);
            AppendIdentityValue(builder, resolution.Reason);
            builder.Append(candidates.Count).Append('|');
            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                AppendIdentityValue(builder, candidate.AssemblyName);
                AppendIdentityValue(builder, candidate.Mode);
                AppendIdentityValue(builder, candidate.FullName);
                AppendIdentityValue(builder, candidate.UniqueName);
                AppendIdentityValue(builder, candidate.FixtureKey);
                builder.Append(candidate.Runnable ? '1' : '0').Append(candidate.IsSuite ? '1' : '0').Append('|');
            }
            builder.Append(names.Count).Append('|');
            for (var index = 0; index < names.Count; index++)
            {
                AppendIdentityValue(builder, names[index]);
            }
            return Sha256Prefixed(builder.ToString());
        }

        static void AppendIdentityValue(StringBuilder builder, string value)
        {
            value = value ?? string.Empty;
            builder.Append(value.Length).Append(':').Append(value).Append('|');
        }

        internal static void RefreshRecoveredFilterIdentity(TestRunState state)
        {
            if (state == null || !state.RecoveredAfterReload || string.IsNullOrEmpty(state.Filter))
            {
                return;
            }

            RefreshRecoveredFilterIdentity(state, UnityTestDiscoveryCatalog.Resolve(state.Mode, state.Filter, state.TestAssembly));
        }

        internal static void RefreshRecoveredFilterIdentity(TestRunState state, UnityTestDiscoveryCatalog.FilterResolution resolution)
        {
            if (state == null || !state.RecoveredAfterReload || string.IsNullOrEmpty(state.Filter))
            {
                return;
            }
            if (!IsSafeSha256Hash(state.FilterResolutionIdentity))
            {
                state.FilterResolutionIdentityStatus = "missing_after_reload";
                return;
            }

            if (resolution == null || !resolution.IsResolved)
            {
                var status = resolution == null ? "unavailable" : resolution.Status;
                state.FilterResolutionIdentityStatus = status == "unresolved" || status == "ambiguous"
                    ? "drift_after_reload"
                    : "revalidation_" + status;
                return;
            }

            var currentIdentity = TestFilterResolutionIdentity(resolution);
            state.FilterResolutionIdentityStatus = string.Equals(currentIdentity, state.FilterResolutionIdentity, StringComparison.Ordinal)
                ? "verified_after_reload"
                : "drift_after_reload";
        }

        static void ResolveTestRunFilterContract(TestRunState testRunState)
        {
            testRunState.UnityTestNameFilters.Clear();
            testRunState.FilterContractSuggestedFilters.Clear();
            testRunState.FilterNormalized = false;
            testRunState.FilterContractWarning = false;
            testRunState.FilterContractWarningReason = string.Empty;

            if (string.IsNullOrEmpty(testRunState.Filter))
            {
                testRunState.FilterMatchingMode = TestRunUnityModeOnlyMatchingMode;
                return;
            }

            foreach (var test in testRunState.MatchedTests)
            {
                AddUnique(testRunState.FilterContractSuggestedFilters, test.RecommendedFilter, MaxTestRunMatchedTests);
            }
            testRunState.FilterResolutionStatus = "unresolved";
            testRunState.FilterResolutionReason = "authoritative_catalog_required";
        }

        static bool UnityTestNamesEqualInput(TestRunState testRunState)
        {
            return testRunState.UnityTestNameFilters.Count == 1
                && string.Equals(testRunState.UnityTestNameFilters[0], testRunState.Filter, StringComparison.Ordinal);
        }

        internal static void MarkTestRunFailed(TestRunState state, string resultState, Exception exception)
        {
            var unwrapped = UnwrapTargetInvocation(exception);
            state.Status = "failed";
            state.ResultState = resultState;
            state.Message = Bounded(unwrapped.Message, 4000);
            state.ExceptionType = unwrapped.GetType().FullName ?? unwrapped.GetType().Name;
            state.StackTrace = Bounded(unwrapped.StackTrace, 12000);
            state.Finished = true;
            state.FinishedAt = DateTime.UtcNow;
            state.Success = false;
            FinalizeTestRun(state);
        }

        internal static void CaptureFailedTestResult(TestRunState state, object result)
        {
            if (state == null || result == null)
            {
                return;
            }

            var resultState = ReflectedValue(result, "ResultState", "TestStatus", "Outcome");
            if (IsFailedResultState(resultState))
            {
                AddFailedTestInfo(state, result, resultState);
            }
        }

        internal static void CaptureFailedTestResultTree(TestRunState state, object result)
        {
            if (state == null || result == null)
            {
                return;
            }

            CaptureFailedTestResultTree(state, result, 0);
        }

        internal static void CaptureTerminalResultIdentities(TestRunState state, object result)
        {
            if (state == null)
            {
                return;
            }

            state.TerminalResultIdentities.Clear();
            state.TerminalResultIdentitiesTruncated = false;
            state.TerminalResultTraversalTruncated = false;
            state.TerminalResultLeafCount = 0;
            state.TerminalResultMatchingLeafCount = 0;
            state.TerminalResultUnexpectedLeafCount = 0;
            if (string.IsNullOrEmpty(state.Filter))
            {
                state.TerminalResultIdentityStatus = "not_required";
                return;
            }
            if (state.ResolvedFilterCandidateCount > 1)
            {
                state.TerminalResultIdentityStatus = "not_required_multi_candidate_filter";
                return;
            }
            if (result == null || string.IsNullOrEmpty(state.ResolvedFilterName))
            {
                state.TerminalResultIdentityStatus = "unavailable";
                return;
            }

            var traversal = new TerminalResultIdentityTraversal();
            CaptureTerminalResultIdentities(state, result, 0, false, false, traversal);
            state.TerminalResultIdentityStatus = state.TerminalResultTraversalTruncated || traversal.MetadataUnavailable || state.TerminalResultLeafCount == 0
                ? "unavailable"
                : state.TerminalResultUnexpectedLeafCount > 0
                    || (!state.ResolvedFilterIsSuite && (state.TerminalResultLeafCount != 1 || state.TerminalResultMatchingLeafCount != 1))
                    || (state.ResolvedFilterIsSuite && (traversal.ExactSuiteIdentityCount != 1 || state.TerminalResultMatchingLeafCount != state.TerminalResultLeafCount))
                        ? "mismatch"
                        : "matched";
        }

        static void CaptureTerminalResultIdentities(
            TestRunState state,
            object result,
            int depth,
            bool insideResolvedSuite,
            bool terminalContextMismatch,
            TerminalResultIdentityTraversal traversal)
        {
            if (result == null || depth > 32 || traversal.ScannedNodes >= MaxTerminalResultNodes)
            {
                state.TerminalResultTraversalTruncated = true;
                return;
            }

            traversal.ScannedNodes++;
            var testMetadata = ReflectedMember(result, "Test") ?? result;
            var fullName = Bounded(ReflectedValue(testMetadata, "FullName", "FullTestName"), 300).Trim();
            var uniqueName = Bounded(ReflectedValue(testMetadata, "UniqueName"), 300).Trim();
            var nodeMatches = string.Equals(fullName, state.ResolvedFilterName, StringComparison.Ordinal)
                || string.Equals(uniqueName, state.ResolvedFilterName, StringComparison.Ordinal);
            var hasSuiteMetadata = TryReflectedBool(testMetadata, "IsSuite", out var isSuite);
            var isTestAssembly = TryReflectedBool(testMetadata, "IsTestAssembly", out var assemblyNode) && assemblyNode;
            var semanticSuiteDescendant = state.ResolvedFilterIsSuite
                && (IsSemanticSuiteDescendant(state.ResolvedFilterName, fullName)
                    || IsSemanticSuiteDescendant(state.ResolvedFilterName, uniqueName));
            var relevantContextNode = isTestAssembly || insideResolvedSuite || nodeMatches || semanticSuiteDescendant;
            var contextMismatch = terminalContextMismatch
                || (relevantContextNode
                    && (TerminalResultContextMismatches(state, result)
                        || (!ReferenceEquals(testMetadata, result) && TerminalResultContextMismatches(state, testMetadata))));
            if (state.ResolvedFilterIsSuite && nodeMatches && hasSuiteMetadata && !isTestAssembly && !contextMismatch)
            {
                traversal.ExactSuiteIdentityCount++;
            }
            var childInsideResolvedSuite = insideResolvedSuite || (state.ResolvedFilterIsSuite && nodeMatches && hasSuiteMetadata && isSuite && !contextMismatch);
            foreach (var child in ReflectedEnumerable(result, "Children", "Results", "TestResults"))
            {
                if (traversal.ScannedNodes >= MaxTerminalResultNodes)
                {
                    state.TerminalResultTraversalTruncated = true;
                    break;
                }
                CaptureTerminalResultIdentities(state, child, depth + 1, childInsideResolvedSuite, contextMismatch, traversal);
            }

            if (!hasSuiteMetadata)
            {
                if (!string.IsNullOrEmpty(fullName) || !string.IsNullOrEmpty(uniqueName))
                {
                    traversal.MetadataUnavailable = true;
                }
                return;
            }
            if (isSuite || isTestAssembly)
            {
                return;
            }

            var identity = string.IsNullOrEmpty(fullName) ? uniqueName : fullName;
            var outcome = Bounded(ReflectedValue(result, "ResultState", "TestStatus", "Outcome"), 120).Trim();
            if (!string.IsNullOrEmpty(identity) && traversal.Outcomes.TryGetValue(identity, out var previousOutcome))
            {
                if (!string.Equals(previousOutcome, outcome, StringComparison.Ordinal))
                {
                    state.TerminalResultUnexpectedLeafCount++;
                }
                return;
            }
            if (!string.IsNullOrEmpty(identity))
            {
                traversal.Outcomes.Add(identity, outcome);
            }

            state.TerminalResultLeafCount++;
            var leafMatches = !string.IsNullOrEmpty(identity)
                && !contextMismatch
                && (state.ResolvedFilterIsSuite
                    ? nodeMatches || childInsideResolvedSuite || semanticSuiteDescendant
                    : nodeMatches);
            if (leafMatches)
            {
                state.TerminalResultMatchingLeafCount++;
            }
            else
            {
                state.TerminalResultUnexpectedLeafCount++;
            }
            if (string.IsNullOrEmpty(identity))
            {
                return;
            }
            if (state.TerminalResultIdentities.Count < MaxTerminalResultIdentities)
            {
                state.TerminalResultIdentities.Add(identity);
            }
            else
            {
                state.TerminalResultIdentitiesTruncated = true;
            }
        }

        static bool IsSemanticSuiteDescendant(string suiteName, string identity)
        {
            return !string.IsNullOrEmpty(suiteName)
                && !string.IsNullOrEmpty(identity)
                && identity.Length > suiteName.Length
                && identity.StartsWith(suiteName, StringComparison.Ordinal)
                && identity[suiteName.Length] == '.';
        }

        sealed class TerminalResultIdentityTraversal
        {
            public int ScannedNodes;
            public int ExactSuiteIdentityCount;
            public bool MetadataUnavailable;
            public readonly Dictionary<string, string> Outcomes = new Dictionary<string, string>(StringComparer.Ordinal);
        }

        static bool TerminalResultContextMismatches(TestRunState state, object result)
        {
            var assemblyName = NormalizeTerminalResultAssemblyName(ReflectedValue(result, "AssemblyName"));
            if (!string.IsNullOrEmpty(assemblyName)
                && !string.IsNullOrEmpty(state.TestAssembly)
                && !string.Equals(assemblyName, state.TestAssembly, StringComparison.Ordinal))
            {
                return true;
            }

            var testMode = ReflectedValue(result, "TestMode").Replace("_", string.Empty).Replace(" ", string.Empty).ToLowerInvariant();
            if (string.Equals(testMode, "0", StringComparison.Ordinal)) testMode = string.Empty;
            return !string.IsNullOrEmpty(testMode)
                && !string.Equals(testMode, state.Mode, StringComparison.Ordinal);
        }

        static string NormalizeTerminalResultAssemblyName(string value)
        {
            var assemblyName = (value ?? string.Empty).Trim().Replace('\\', '/');
            var separator = assemblyName.LastIndexOf('/');
            if (separator >= 0) assemblyName = assemblyName.Substring(separator + 1);
            var displayNameSeparator = assemblyName.IndexOf(',');
            if (displayNameSeparator >= 0) assemblyName = assemblyName.Substring(0, displayNameSeparator).Trim();
            if (assemblyName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) assemblyName = assemblyName.Substring(0, assemblyName.Length - 4);
            return assemblyName;
        }

        static bool CaptureFailedTestResultTree(TestRunState state, object result, int depth)
        {
            if (result == null || depth > 8)
            {
                return false;
            }

            var failedDescendant = false;
            foreach (var child in ReflectedEnumerable(result, "Children", "Results", "TestResults"))
            {
                failedDescendant = CaptureFailedTestResultTree(state, child, depth + 1) || failedDescendant;
            }

            var resultState = ReflectedValue(result, "ResultState", "TestStatus", "Outcome");
            var failed = IsFailedResultState(resultState);
            if (failed && !failedDescendant)
            {
                AddFailedTestInfo(state, result, resultState);
            }

            return failed || failedDescendant;
        }

        static bool IsFailedResultState(string value)
        {
            return !string.IsNullOrEmpty(value)
                && (value.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("Failure", StringComparison.OrdinalIgnoreCase) >= 0
                    || value.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        static void AddFailedTestInfo(TestRunState state, object result, string resultState)
        {
            if (state.FailedTests.Count >= MaxFailedTestDetails)
            {
                state.FailedTestsTruncated = true;
                return;
            }

            var name = Bounded(ReflectedValue(result, "Name"), 200);
            var fullName = Bounded(ReflectedValue(result, "FullName", "FullTestName"), 300);
            var key = string.IsNullOrEmpty(fullName) ? name : fullName;
            if (string.IsNullOrEmpty(key))
            {
                key = Bounded(result.ToString(), 200);
            }

            if (string.IsNullOrEmpty(name))
            {
                name = key;
            }

            for (var index = 0; index < state.FailedTests.Count; index++)
            {
                if (state.FailedTests[index].Key == key)
                {
                    return;
                }
            }

            state.FailedTests.Add(new FailedTestInfo(
                key,
                name,
                fullName,
                Bounded(resultState, 120),
                Bounded(ReflectedValue(result, "Message", "Output"), MaxFailedTestMessageChars),
                Bounded(ReflectedValue(result, "StackTrace"), MaxFailedTestStackTraceChars)));
        }

        static IEnumerable ReflectedEnumerable(object instance, params string[] names)
        {
            if (instance == null)
            {
                yield break;
            }

            var type = instance.GetType();
            for (var index = 0; index < names.Length; index++)
            {
                object value = null;
                try
                {
                    var property = type.GetProperty(names[index], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        value = property.GetValue(instance, null);
                    }
                    else
                    {
                        var field = type.GetField(names[index], BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                        if (field != null)
                        {
                            value = field.GetValue(instance);
                        }
                    }
                }
                catch
                {
                    value = null;
                }

                var enumerable = value as IEnumerable;
                if (enumerable == null || value is string)
                {
                    continue;
                }

                foreach (var item in enumerable)
                {
                    yield return item;
                }
                yield break;
            }
        }

        static Exception UnwrapTargetInvocation(Exception exception)
        {
            while (exception is TargetInvocationException targetInvocationException && targetInvocationException.InnerException != null)
            {
                exception = targetInvocationException.InnerException;
            }

            return exception;
        }

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        static string ReadFileTail(string fullPath, int maxChars)
        {
            try
            {
                if (string.IsNullOrEmpty(fullPath) || !File.Exists(fullPath))
                {
                    return string.Empty;
                }

                var content = File.ReadAllText(fullPath);
                if (content.Length <= maxChars)
                {
                    return content;
                }

                return content.Substring(content.Length - maxChars);
            }
            catch
            {
                return string.Empty;
            }
        }

        static bool TryFindLogLine(string text, out string evidence, params string[] needles)
        {
            evidence = string.Empty;
            if (string.IsNullOrEmpty(text))
            {
                return false;
            }

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                var line = lines[lineIndex];
                for (var needleIndex = 0; needleIndex < needles.Length; needleIndex++)
                {
                    if (ContainsOrdinalIgnoreCase(line, needles[needleIndex]))
                    {
                        evidence = line.Trim();
                        return true;
                    }
                }
            }

            return false;
        }

        static bool ContainsOrdinalIgnoreCase(string value, string needle)
        {
            return !string.IsNullOrEmpty(value)
                && !string.IsNullOrEmpty(needle)
                && value.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static int IndexOfNextNonWhitespace(string value, int start)
        {
            if (string.IsNullOrEmpty(value))
            {
                return -1;
            }

            for (var index = Math.Max(0, start); index < value.Length; index++)
            {
                if (!char.IsWhiteSpace(value[index]))
                {
                    return index;
                }
            }

            return -1;
        }

        static char UnescapeJsonCharacter(char character)
        {
            switch (character)
            {
                case 'n':
                    return '\n';
                case 'r':
                    return '\r';
                case 't':
                    return '\t';
                default:
                    return character;
            }
        }

        static Type FindType(string fullName)
        {
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                var type = assembly.GetType(fullName, false);
                if (type != null)
                {
                    return type;
                }
            }

            return null;
        }

        static object CreateTestRunnerApi(Type apiType)
        {
            return typeof(ScriptableObject).IsAssignableFrom(apiType)
                ? ScriptableObject.CreateInstance(apiType)
                : Activator.CreateInstance(apiType);
        }

        static object CreateExecutionSettings(Type settingsType, Type filterType, Type modeType, string mode, List<string> testNames, string testAssembly)
        {
            var filterInstance = Activator.CreateInstance(filterType);
            var testMode = Enum.Parse(modeType, mode == "playmode" ? "PlayMode" : "EditMode");
            SetRequiredMember(filterInstance, "testMode", testMode);
            if (testNames != null && testNames.Count > 0)
            {
                SetRequiredMember(filterInstance, "testNames", testNames.ToArray());
            }

            if (!string.IsNullOrEmpty(testAssembly))
            {
                SetRequiredMember(filterInstance, "assemblyNames", new string[] { testAssembly });
            }

            return Activator.CreateInstance(settingsType, filterInstance);
        }

        static void InvokeReflected(object instance, string methodName, object argument)
        {
            var method = FindInstanceMethod(instance.GetType(), methodName, argument);
            if (method == null)
            {
                throw new InvalidOperationException("Unity TestRunnerApi missing method: " + methodName);
            }

            method.Invoke(instance, BuildMethodArguments(method, argument));
        }

        static void InvokeReflectedIfExists(object instance, string methodName, object argument)
        {
            if (instance == null)
            {
                return;
            }

            var method = FindInstanceMethod(instance.GetType(), methodName, argument);
            if (method == null)
            {
                return;
            }

            try
            {
                method.Invoke(instance, BuildMethodArguments(method, argument));
            }
            catch
            {
                // Callback cleanup is best-effort across Unity Test Framework versions.
            }
        }

        static MethodInfo FindInstanceMethod(Type type, string methodName, object argument)
        {
            var methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var index = 0; index < methods.Length; index++)
            {
                var method = ConstructGenericMethod(methods[index], argument);
                if (method == null)
                {
                    continue;
                }

                var parameters = method.GetParameters();
                if (method.Name == methodName && parameters.Length > 0 && ParameterAccepts(parameters[0], argument) && CanSupplyExtraParameters(parameters))
                {
                    return method;
                }
            }

            return null;
        }

        static MethodInfo ConstructGenericMethod(MethodInfo method, object argument)
        {
            if (!method.IsGenericMethodDefinition)
            {
                return method;
            }

            var genericArguments = method.GetGenericArguments();
            if (genericArguments.Length != 1 || argument == null)
            {
                return null;
            }

            try
            {
                return method.MakeGenericMethod(argument.GetType());
            }
            catch
            {
                return null;
            }
        }

        static object[] BuildMethodArguments(MethodInfo method, object argument)
        {
            var parameters = method.GetParameters();
            var values = new object[parameters.Length];
            values[0] = argument;
            for (var index = 1; index < parameters.Length; index++)
            {
                values[index] = DefaultParameterValue(parameters[index]);
            }

            return values;
        }

        static bool ParameterAccepts(ParameterInfo parameter, object argument)
        {
            return argument == null || parameter.ParameterType.IsInstanceOfType(argument);
        }

        static bool CanSupplyExtraParameters(ParameterInfo[] parameters)
        {
            for (var index = 1; index < parameters.Length; index++)
            {
                if (!parameters[index].IsOptional && parameters[index].ParameterType != typeof(int))
                {
                    return false;
                }
            }

            return true;
        }

        static object DefaultParameterValue(ParameterInfo parameter)
        {
            if (parameter.IsOptional)
            {
                return parameter.DefaultValue;
            }

            return parameter.ParameterType == typeof(int) ? 0 : null;
        }

        static void SetRequiredMember(object instance, string name, object value)
        {
            var type = instance.GetType();
            var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (property != null && property.CanWrite && property.CanRead)
            {
                property.SetValue(instance, value, null);
                if (MemberValuesEqual(property.GetValue(instance, null), value))
                {
                    return;
                }

                throw new InvalidOperationException("Unity TestRunner Filter member did not retain the required value: " + name);
            }

            var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
            if (field != null)
            {
                field.SetValue(instance, value);
                if (MemberValuesEqual(field.GetValue(instance), value))
                {
                    return;
                }

                throw new InvalidOperationException("Unity TestRunner Filter member did not retain the required value: " + name);
            }

            throw new MissingMemberException(type.FullName, name);
        }

        static bool MemberValuesEqual(object actual, object expected)
        {
            if (ReferenceEquals(actual, expected))
            {
                return true;
            }

            if (actual is Array actualArray && expected is Array expectedArray)
            {
                if (actualArray.Length != expectedArray.Length)
                {
                    return false;
                }

                for (var index = 0; index < actualArray.Length; index++)
                {
                    if (!object.Equals(actualArray.GetValue(index), expectedArray.GetValue(index)))
                    {
                        return false;
                    }
                }

                return true;
            }

            return object.Equals(actual, expected);
        }

        static string TestAttributeName(MethodInfo method)
        {
            foreach (var attribute in method.GetCustomAttributesData())
            {
                var name = attribute.AttributeType.FullName ?? attribute.AttributeType.Name;
                if (name == "NUnit.Framework.TestAttribute" || name == "NUnit.Framework.TestCaseAttribute" || name == "UnityEngine.TestTools.UnityTestAttribute")
                {
                    return name;
                }
            }

            return string.Empty;
        }

        static string TestMode(Assembly assembly)
        {
            var name = assembly.GetName().Name ?? string.Empty;
            return name.IndexOf("Editor", StringComparison.OrdinalIgnoreCase) >= 0 ? "editmode" : "playmode";
        }

        static bool MatchesQuery(TestMethodInfo test, string lowerQuery)
        {
            if (string.IsNullOrEmpty(lowerQuery))
            {
                return true;
            }

            return test.AssemblyName.ToLowerInvariant().Contains(lowerQuery)
                || test.TypeName.ToLowerInvariant().Contains(lowerQuery)
                || test.MethodName.ToLowerInvariant().Contains(lowerQuery)
                || test.FullName.ToLowerInvariant().Contains(lowerQuery);
        }

        static bool MatchesMode(TestMethodInfo test, string lowerMode)
        {
            return string.IsNullOrEmpty(lowerMode) || test.Mode == lowerMode;
        }

        static int QueryRank(Assembly assembly, Type type, string lowerQuery)
        {
            if (string.IsNullOrEmpty(lowerQuery))
            {
                return 1;
            }

            var typeName = type.FullName ?? type.Name;
            if (typeName.ToLowerInvariant().Contains(lowerQuery))
            {
                return 0;
            }

            var assemblyName = assembly.GetName().Name ?? string.Empty;
            return assemblyName.ToLowerInvariant().Contains(lowerQuery) ? 0 : 1;
        }

        static bool ContainsTestFrameworkHint(string value)
        {
            return !string.IsNullOrEmpty(value)
                && value.IndexOf("Test", StringComparison.OrdinalIgnoreCase) >= 0
                && value.IndexOf("Framework", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static Type FindBuildProfileType()
        {
            foreach (var assembly in EditorAssemblyDiscovery.LoadedAssemblies())
            {
                foreach (var type in SafeGetTypes(assembly))
                {
                    if (type == null)
                    {
                        continue;
                    }

                    var fullName = type.FullName ?? string.Empty;
                    if (fullName == "UnityEditor.Build.Profile.BuildProfile" || (type.Name == "BuildProfile" && fullName.IndexOf("Build.Profile", StringComparison.Ordinal) >= 0))
                    {
                        return type;
                    }
                }
            }

            return null;
        }

        static object ReflectedMember(object instance, string name)
        {
            if (instance == null)
            {
                return null;
            }

            try
            {
                var type = instance.GetType();
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    return property.GetValue(instance, null);
                }
                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                return field == null ? null : field.GetValue(instance);
            }
            catch
            {
                return null;
            }
        }

        static bool TryReflectedBool(object instance, string name, out bool value)
        {
            value = false;
            var reflected = ReflectedMember(instance, name);
            if (reflected is bool boolean)
            {
                value = boolean;
                return true;
            }
            return reflected != null && bool.TryParse(reflected.ToString(), out value);
        }

        internal static string ReflectedValue(object instance, params string[] names)
        {
            if (instance == null)
            {
                return string.Empty;
            }

            var type = instance.GetType();
            for (var index = 0; index < names.Length; index++)
            {
                var name = names[index];
                try
                {
                    var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                    if (property != null && property.GetIndexParameters().Length == 0)
                    {
                        var value = property.GetValue(instance, null);
                        if (value != null)
                        {
                            return value.ToString();
                        }
                    }

                    var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                    if (field != null)
                    {
                        var value = field.GetValue(instance);
                        if (value != null)
                        {
                            return value.ToString();
                        }
                    }
                }
                catch
                {
                    // Some Unity properties are editor-version dependent; omit unreadable fields.
                }
            }

            return string.Empty;
        }

        static IEnumerable<Type> SafeGetTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types;
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        static void AddUnique(List<string> values, string value, int max)
        {
            if (string.IsNullOrEmpty(value) || values.Count >= max || values.Contains(value))
            {
                return;
            }

            values.Add(value);
        }

        static void AppendStringArray(StringBuilder builder, List<string> values)
        {
            AppendStringArray(builder, values, values.Count);
        }

        static void AppendStringArray(StringBuilder builder, List<string> values, int max)
        {
            builder.Append('[');
            var count = Math.Min(values.Count, Math.Max(0, max));
            for (var index = 0; index < count; index++)
            {
                if (index > 0)
                {
                    builder.Append(',');
                }

                builder.Append(JsonUtil.String(values[index]));
            }
            builder.Append(']');
        }

        sealed class TestDiscoveryResult
        {
            public TestDiscoveryResult(string query, string mode, int maxResults)
            {
                Query = (query ?? string.Empty).Trim();
                LowerQuery = Query.ToLowerInvariant();
                Mode = (mode ?? string.Empty).Trim().ToLowerInvariant();
                LowerMode = Mode;
                MaxResults = Math.Max(1, maxResults);
            }

            public string Query { get; }
            public string LowerQuery { get; }
            public string Mode { get; }
            public string LowerMode { get; }
            public int MaxResults { get; }
            public List<TestMethodInfo> Tests { get; } = new List<TestMethodInfo>();
            public int MatchedCount { get; set; }
            public bool ScanIncomplete { get; private set; }
            public string ScanReason { get; private set; } = string.Empty;
            public bool ScanOmitted { get; private set; }
            public string ScanOmittedReason { get; private set; } = string.Empty;
            public int ScannedAssemblies { get; set; }
            public int ScannedTypes { get; set; }
            public int ScannedMethods { get; set; }

            public bool MatchedTestsTruncated
            {
                get { return MatchedCount > Tests.Count || (ScanIncomplete && ScanReason == "max_results"); }
            }

            public bool MatchedCountReliable
            {
                get { return !ScanOmitted && !ScanIncomplete; }
            }

            public string CountReliabilityStatus
            {
                get
                {
                    if (ScanOmitted)
                    {
                        return "not_applicable";
                    }

                    return ScanIncomplete ? "lower_bound" : "complete";
                }
            }

            public string CoverageStatus
            {
                get
                {
                    if (ScanOmitted)
                    {
                        return "omitted";
                    }

                    return ScanIncomplete ? "partial" : "complete";
                }
            }

            public void MarkScanIncomplete(string reason)
            {
                ScanIncomplete = true;
                ScanReason = reason;
            }

            public void MarkScanOmitted(string reason)
            {
                ScanOmitted = true;
                ScanOmittedReason = reason;
            }
        }

        internal sealed class TestMethodInfo
        {
            readonly string m_fullName;
            readonly string m_uniqueName;
            readonly string m_suiteName;
            readonly bool m_isSuite;
            readonly bool m_runnable;
            readonly string m_runState;
            readonly bool m_catalogAuthoritative;

            public TestMethodInfo(string assemblyName, string typeName, string methodName, string attributeName, string mode)
            {
                AssemblyName = assemblyName;
                TypeName = typeName;
                MethodName = methodName;
                AttributeName = attributeName;
                Mode = mode;
                m_fullName = string.IsNullOrEmpty(TypeName) ? MethodName : TypeName + "." + MethodName;
                m_uniqueName = m_fullName;
                m_suiteName = TypeName;
                m_runnable = true;
                m_runState = "Runnable";
                m_catalogAuthoritative = false;
            }

            TestMethodInfo(UnityTestDiscoveryCatalog.TestCandidate candidate)
            {
                AssemblyName = candidate.AssemblyName;
                TypeName = candidate.TypeName;
                MethodName = candidate.MethodName;
                AttributeName = "unity_test_runner";
                Mode = candidate.Mode;
                m_fullName = candidate.FullName;
                m_uniqueName = candidate.UniqueName;
                m_suiteName = candidate.SuiteName;
                m_isSuite = candidate.IsSuite;
                m_runnable = candidate.Runnable;
                m_runState = candidate.RunState;
                m_catalogAuthoritative = true;
            }

            public static TestMethodInfo FromCatalog(UnityTestDiscoveryCatalog.TestCandidate candidate)
            {
                return candidate == null ? null : new TestMethodInfo(candidate);
            }

            public string AssemblyName { get; }
            public string TypeName { get; }
            public string MethodName { get; }
            public string AttributeName { get; }
            public string Mode { get; }
            public string SuiteName { get { return m_suiteName; } }
            public string FullName { get { return m_fullName; } }
            public string UniqueName { get { return m_uniqueName; } }
            public bool IsSuite { get { return m_isSuite; } }
            public bool Runnable { get { return m_runnable; } }
            public string RunState { get { return m_runState; } }
            public string RecommendedFilter { get { return string.IsNullOrEmpty(FullName) ? m_uniqueName : FullName; } }

            public void AppendJson(StringBuilder builder)
            {
                builder.Append('{');
                builder.Append("\"assembly\":").Append(JsonUtil.String(AssemblyName)).Append(',');
                builder.Append("\"suite\":").Append(JsonUtil.String(SuiteName)).Append(',');
                builder.Append("\"type\":").Append(JsonUtil.String(TypeName)).Append(',');
                builder.Append("\"method\":").Append(JsonUtil.String(MethodName)).Append(',');
                builder.Append("\"full_name\":").Append(JsonUtil.String(FullName)).Append(',');
                builder.Append("\"unique_name\":").Append(JsonUtil.String(UniqueName)).Append(',');
                builder.Append("\"attribute\":").Append(JsonUtil.String(AttributeName)).Append(',');
                builder.Append("\"mode\":").Append(JsonUtil.String(Mode)).Append(',');
                builder.Append("\"is_suite\":").Append(JsonUtil.Bool(IsSuite)).Append(',');
                builder.Append("\"runnable\":").Append(JsonUtil.Bool(Runnable)).Append(',');
                builder.Append("\"run_state\":").Append(JsonUtil.String(RunState)).Append(',');
                builder.Append("\"runnable_filters\":{");
                builder.Append("\"recommended\":").Append(JsonUtil.String(RecommendedFilter)).Append(',');
                builder.Append("\"unity_test_names\":");
                AppendStringArray(builder, RunnableFilterValues());
                builder.Append(",\"reflected_full_name\":").Append(JsonUtil.String(FullName));
                builder.Append(",\"fixture\":").Append(JsonUtil.String(TypeName));
                builder.Append(",\"method\":").Append(JsonUtil.String(MethodName));
                builder.Append(",\"assembly\":").Append(JsonUtil.String(AssemblyName));
                builder.Append('}');
                builder.Append('}');
            }

            List<string> RunnableFilterValues()
            {
                var values = new List<string>();
                AddUnique(values, RecommendedFilter, 8);
                if (!m_catalogAuthoritative)
                {
                    AddUnique(values, MethodName, 8);
                    AddUnique(values, TypeName, 8);
                }
                return values;
            }
        }

        sealed class TestTypeScanInfo
        {
            public TestTypeScanInfo(Assembly assembly, Type type, string mode, int queryRank)
            {
                Assembly = assembly;
                Type = type;
                Mode = mode;
                QueryRank = queryRank;
            }

            public Assembly Assembly { get; }
            public Type Type { get; }
            public string Mode { get; }
            public int QueryRank { get; }

            public static int Compare(TestTypeScanInfo left, TestTypeScanInfo right)
            {
                var compare = left.QueryRank.CompareTo(right.QueryRank);
                if (compare != 0)
                {
                    return compare;
                }

                compare = string.Compare(left.Assembly.GetName().Name, right.Assembly.GetName().Name, StringComparison.Ordinal);
                if (compare != 0)
                {
                    return compare;
                }

                return string.Compare(left.Type.FullName ?? left.Type.Name, right.Type.FullName ?? right.Type.Name, StringComparison.Ordinal);
            }
        }

        sealed class BuildProfileInfo
        {
            public BuildProfileInfo(string name, string assetPath, string guid, string buildTarget, string subtarget)
            {
                Name = name;
                AssetPath = assetPath;
                Guid = guid;
                BuildTarget = buildTarget;
                Subtarget = subtarget;
            }

            public string Name { get; }
            public string AssetPath { get; }
            public string Guid { get; }
            public string BuildTarget { get; }
            public string Subtarget { get; }

            public void AppendJson(StringBuilder builder)
            {
                builder.Append('{');
                builder.Append("\"name\":").Append(JsonUtil.String(Name)).Append(',');
                builder.Append("\"asset_path\":").Append(JsonUtil.String(AssetPath)).Append(',');
                builder.Append("\"guid\":").Append(JsonUtil.String(Guid));
                if (!string.IsNullOrEmpty(BuildTarget))
                {
                    builder.Append(",\"build_target\":").Append(JsonUtil.String(BuildTarget));
                }

                if (!string.IsNullOrEmpty(Subtarget))
                {
                    builder.Append(",\"subtarget\":").Append(JsonUtil.String(Subtarget));
                }

                builder.Append('}');
            }
        }

        internal sealed class BuildRunReceiptClassification
        {
            public BuildRunReceiptClassification(bool success, string receiptConsistency, string receiptConsistencyConflict, string[] receiptConflicts)
            {
                Success = success;
                ReceiptConsistency = receiptConsistency ?? string.Empty;
                ReceiptConsistencyConflict = receiptConsistencyConflict ?? string.Empty;
                ReceiptConflicts = receiptConflicts ?? Array.Empty<string>();
            }

            public bool Success { get; }
            public string ReceiptConsistency { get; }
            public string ReceiptConsistencyConflict { get; }
            public string[] ReceiptConflicts { get; }
        }

        sealed class BuildRunState
        {
            public string OperationId { get; set; }
            public string ArgsHash { get; set; }
            public string CallerArgsHash { get; set; }
            public string CreatedAt { get; set; }
            public string CompletedAt { get; set; }
            public string Status { get; set; }
            public string Result { get; set; }
            public string Message { get; set; }
            public bool Success { get; set; }
            public BuildTarget Target { get; set; }
            public BuildTargetGroup TargetGroup { get; set; }
            public bool Development { get; set; }
            public string BuildName { get; set; }
            public string OutputPath { get; set; }
            public string OutputRoot { get; set; }
            public string OutputSelection { get; set; }
            public string ArtifactPath { get; set; }
            public List<string> Scenes { get; set; }
            public BuildOptions Options { get; set; }
            public BridgeRuntimeState RuntimeState { get; set; }
            public bool AllowUnsavedSceneChanges { get; set; }
            public int TotalErrors { get; set; }
            public int TotalWarnings { get; set; }
            public long TotalSize { get; set; }
            public long DurationMs { get; set; }
            public bool OutputExists { get; set; }
            public string OutputKind { get; set; } = "pending";
            public bool TerminalEvidenceAuthoritative { get; set; }
            public string ReceiptConsistency { get; set; } = "pending";
            public string ReceiptConsistencyConflict { get; set; } = string.Empty;
            public List<string> ReceiptConflicts { get; } = new List<string>();
        }

        internal sealed class FailedTestInfo
        {
            public FailedTestInfo(string key, string name, string fullName, string result, string message, string stackTrace)
            {
                Key = key;
                Name = name;
                FullName = fullName;
                Result = result;
                Message = message;
                StackTrace = stackTrace;
            }

            public string Key { get; }
            public string Name { get; }
            public string FullName { get; }
            public string Result { get; }
            public string Message { get; }
            public string StackTrace { get; }

            public void AppendJson(StringBuilder builder)
            {
                builder.Append('{');
                builder.Append("\"name\":").Append(JsonUtil.String(Name));
                if (!string.IsNullOrEmpty(FullName))
                {
                    builder.Append(",\"full_name\":").Append(JsonUtil.String(FullName));
                }

                if (!string.IsNullOrEmpty(Result))
                {
                    builder.Append(",\"result\":").Append(JsonUtil.String(Result));
                }

                if (!string.IsNullOrEmpty(Message))
                {
                    builder.Append(",\"message\":").Append(JsonUtil.String(Message));
                }

                if (!string.IsNullOrEmpty(StackTrace))
                {
                    builder.Append(",\"stack_trace\":").Append(JsonUtil.String(StackTrace));
                }

                builder.Append('}');
            }
        }

        sealed class TestRunBatchmodeXmlSummary
        {
            public TestRunBatchmodeXmlSummary(string result, bool success, int total, int passed, int failed, int skipped, long durationMs, bool zeroTests)
            {
                Result = result;
                Success = success;
                Total = total;
                Passed = passed;
                Failed = failed;
                Skipped = skipped;
                DurationMs = durationMs;
                ZeroTests = zeroTests;
            }

            public string Result { get; }
            public bool Success { get; }
            public int Total { get; }
            public int Passed { get; }
            public int Failed { get; }
            public int Skipped { get; }
            public long DurationMs { get; }
            public bool ZeroTests { get; }
        }

        internal sealed class TestRunPreExecuteRefusal
        {
            public TestRunPreExecuteRefusal(string code, string reason)
            {
                Code = Bounded(code, 96);
                Reason = Bounded(reason, 160);
            }

            public string Code { get; }
            public string Reason { get; }

            public string ToJson()
            {
                return "{\"code\":" + JsonUtil.String(Code) + ",\"reason\":" + JsonUtil.String(Reason) + "}";
            }
        }

        // Per-fixture publication/clock injection; never configured or persisted by production commands.
        internal sealed class TestRunPublicationHooks
        {
            public Func<string, string, FixedArtifactPublicationResult> PublishArtifact { get; set; }
            public Func<DateTime> UtcNow { get; set; }
            public Func<double> EditorTime { get; set; }
        }

        internal sealed class TestRunState
        {
            public TestRunState(string runId, string mode, string filter, string artifactPath, int maxSeconds, bool dryRun)
            {
                RunId = runId;
                Mode = mode;
                Filter = filter;
                ArtifactPath = artifactPath;
                MaxSeconds = maxSeconds;
                DryRun = dryRun;
            }

            public string RunId { get; }
            public string Mode { get; }
            public string Filter { get; }
            public string TestAssembly { get; set; } = string.Empty;
            public string ArtifactPath { get; }
            public string BatchmodeXmlPath => BuildTestAutomationCommands.TestRunBatchmodeXmlPath(RunId);
            public string BatchmodeLogPath => BuildTestAutomationCommands.TestRunBatchmodeLogPath(RunId);
            public int MaxSeconds { get; }
            public bool DryRun { get; }
            public bool AllowUnsavedSceneChanges { get; set; }
            public string ArgsHash { get; set; } = string.Empty;
            public string CallerArgsHash { get; set; } = string.Empty;
            public string CreatedAt { get; set; } = DateTime.UtcNow.ToString("o");
            public object Api { get; set; }
            public object Callbacks { get; set; }
            public DateTime StartedAt { get; set; } = DateTime.UtcNow;
            public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
            public DateTime FinishedAt { get; set; }
            public bool Finished { get; set; }
            public bool ArtifactWritten { get; set; }
            public bool ArtifactPersisted { get; set; }
            public bool PendingArtifactPublication { get; set; }
            public int ArtifactPublicationAttempts { get; set; }
            public DateTime ArtifactPublicationDeadlineUtc { get; set; }
            public double NextArtifactPublicationAttempt { get; set; }
            public string PendingPublicationLifecycle { get; set; } = string.Empty;
            public string ArtifactPublicationState { get; set; } = "not_started";
            public string ArtifactPublicationErrorCode { get; set; } = string.Empty;
            public string LastPublishedLifecycle { get; set; } = string.Empty;
            public TestRunPreExecuteRefusal PreExecuteRefusal { get; set; }
            public TestRunPublicationHooks PublicationHooks { get; set; }
            public bool CancelRequested { get; set; }
            public bool CleanupPerformed { get; set; }
            public bool Success { get; set; }
            public int Total { get; set; }
            public int Passed { get; set; }
            public int Failed { get; set; }
            public int Skipped { get; set; }
            public bool TerminalCountsAuthoritative { get; set; }
            public int PassCount { get; set; }
            public int FailCount { get; set; }
            public int SkipCount { get; set; }
            public int InconclusiveCount { get; set; }
            public bool AssertCountAvailable { get; set; }
            public int AssertCount { get; set; }
            public bool TestCaseCountAvailable { get; set; }
            public int TestCaseCount { get; set; }
            public string Status { get; set; } = "running";
            public string Lifecycle { get; set; } = "created";
            public string ResultState { get; set; } = "unknown";
            public string ResultName { get; set; } = string.Empty;
            public string Message { get; set; } = string.Empty;
            public string CancellationReason { get; set; } = string.Empty;
            public string ExceptionType { get; set; } = string.Empty;
            public string StackTrace { get; set; } = string.Empty;
            public bool CallbackReRegisteredAfterReload { get; set; }
            public bool RecoveredAfterReload { get; set; }
            public int CallbackRegistrationEpoch { get; set; }
            public int ExecuteDispatchCount { get; set; }
            public bool ExecuteCallReturned { get; set; }
            public string RecoveryReason { get; set; } = string.Empty;
            public string FilterMatchingMode { get; set; } = TestRunUnityModeOnlyMatchingMode;
            public string FilterResolutionStatus { get; set; } = "not_requested";
            public string FilterResolutionReason { get; set; } = string.Empty;
            public string FilterResolutionIdentity { get; set; } = string.Empty;
            public string FilterResolutionIdentityStatus { get; set; } = "not_bound";
            public string ResolvedFilterName { get; set; } = string.Empty;
            public bool ResolvedFilterIsSuite { get; set; }
            public int ResolvedFilterCandidateCount { get; set; }
            public string TerminalResultIdentityStatus { get; set; } = "not_observed";
            public int TerminalResultLeafCount { get; set; }
            public int TerminalResultMatchingLeafCount { get; set; }
            public int TerminalResultUnexpectedLeafCount { get; set; }
            public bool TerminalResultTraversalTruncated { get; set; }
            public bool TerminalResultIdentitiesTruncated { get; set; }
            public List<string> TerminalResultIdentities { get; } = new List<string>();
            public string MatchedTestsMatchingMode { get; set; } = TestRunReflectionMatchingMode;
            public bool FilterNormalized { get; set; }
            public bool FilterContractWarning { get; set; }
            public string FilterContractWarningReason { get; set; } = string.Empty;
            public List<string> UnityTestNameFilters { get; } = new List<string>();
            public List<string> FilterContractSuggestedFilters { get; } = new List<string>();
            public int MatchedCount { get; set; }
            public bool MatchedCountReliable { get; set; } = true;
            public string MatchedCountStatus { get; set; } = "complete";
            public string MatchedTestsCoverageStatus { get; set; } = "complete";
            public bool MatchedTestsTruncated { get; set; }
            public bool MatchedTestsScanIncomplete { get; set; }
            public string MatchedTestsScanReason { get; set; } = string.Empty;
            public bool MatchedTestsScanOmitted { get; set; }
            public string MatchedTestsScanOmittedReason { get; set; } = string.Empty;
            public int MatchedTestsScannedAssemblies { get; set; }
            public int MatchedTestsScannedTypes { get; set; }
            public int MatchedTestsScannedMethods { get; set; }
            public List<TestMethodInfo> MatchedTests { get; } = new List<TestMethodInfo>();
            public List<FailedTestInfo> FailedTests { get; } = new List<FailedTestInfo>();
            public bool FailedTestsTruncated { get; set; }
            public TestRunNonPassingEvidence NonPassingEvidence { get; } = new TestRunNonPassingEvidence();
            public bool HasUnsavedSceneChangesAtStart { get; set; }
            public int DirtySceneCountAtStart { get; set; }
            public DirtySceneInfo[] DirtyScenesAtStart { get; set; } = Array.Empty<DirtySceneInfo>();

            public long DurationMs
            {
                get
                {
                    var end = FinishedAt == default(DateTime) ? DateTime.UtcNow : FinishedAt;
                    return (long)Math.Max(0, (end - StartedAt).TotalMilliseconds);
                }
            }

            public void ClearTerminalCountEvidence()
            {
                PassCount = 0;
                FailCount = 0;
                SkipCount = 0;
                InconclusiveCount = 0;
                AssertCountAvailable = false;
                AssertCount = 0;
                TestCaseCountAvailable = false;
                TestCaseCount = 0;
            }
        }

        internal readonly struct TestRunTerminalCounts
        {
            public TestRunTerminalCounts(int total, int passed, int failed, int skipped, int passCount, int failCount, int skipCount, int inconclusiveCount)
            {
                Total = total;
                Passed = passed;
                Failed = failed;
                Skipped = skipped;
                PassCount = passCount;
                FailCount = failCount;
                SkipCount = skipCount;
                InconclusiveCount = inconclusiveCount;
            }

            public int Total { get; }
            public int Passed { get; }
            public int Failed { get; }
            public int Skipped { get; }
            public int PassCount { get; }
            public int FailCount { get; }
            public int SkipCount { get; }
            public int InconclusiveCount { get; }
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }
    }

    public class TestRunCallbacksProxy : System.Reflection.DispatchProxy
    {
        BuildTestAutomationCommands.TestRunState m_state;
        int m_callbackRegistrationEpoch;

        public TestRunCallbacksProxy()
        {
        }

        internal static object Create(Type callbackType, BuildTestAutomationCommands.TestRunState state)
        {
            var method = typeof(System.Reflection.DispatchProxy).GetMethod("Create").MakeGenericMethod(callbackType, typeof(TestRunCallbacksProxy));
            var proxy = method.Invoke(null, null);
            ((TestRunCallbacksProxy)proxy).m_state = state;
            ((TestRunCallbacksProxy)proxy).m_callbackRegistrationEpoch = state.CallbackRegistrationEpoch;
            return proxy;
        }

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod == null || !BuildTestAutomationCommands.IsCurrentTestRunCallback(m_state, m_callbackRegistrationEpoch))
            {
                return null;
            }

            try
            {
                if (targetMethod.Name == "TestFinished" && args != null && args.Length > 0)
                {
                    BuildTestAutomationCommands.MarkTestRunActivity(m_state, "test_finished_callback");
                    CaptureTestResult(args[0]);
                }
                else if (targetMethod.Name == "RunFinished" && args != null && args.Length > 0)
                {
                    BuildTestAutomationCommands.MarkTestRunActivity(m_state, "run_finished_callback");
                    var terminalCountsAvailable = CaptureRunResult(args[0]);
                    m_state.Status = terminalCountsAvailable ? "completed" : "terminal_counts_unavailable";
                    if (terminalCountsAvailable && BuildTestAutomationCommands.TryDeferRecoveredFilterRevalidation(m_state))
                    {
                        return null;
                    }
                    m_state.Finished = true;
                    m_state.FinishedAt = DateTime.UtcNow;
                    BuildTestAutomationCommands.FinalizeTestRun(m_state);
                }
            }
            catch (Exception exception)
            {
                BuildTestAutomationCommands.MarkTestRunFailed(m_state, "callback_failed", exception);
            }

            return null;
        }

        void CaptureTestResult(object result)
        {
            m_state.NonPassingEvidence.Capture(result);
            var state = BuildTestAutomationCommands.ReflectedValue(result, "TestStatus", "ResultState", "Outcome");
            if (state.IndexOf("Passed", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                m_state.Passed++;
            }
            else if (state.IndexOf("Failed", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("Error", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                m_state.Failed++;
                BuildTestAutomationCommands.CaptureFailedTestResult(m_state, result);
            }
            else if (state.IndexOf("Skipped", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("Ignored", StringComparison.OrdinalIgnoreCase) >= 0 || state.IndexOf("Inconclusive", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                m_state.Skipped++;
            }

            m_state.Total = Math.Max(m_state.Total, m_state.Passed + m_state.Failed + m_state.Skipped);
        }

        bool CaptureRunResult(object result)
        {
            m_state.NonPassingEvidence.CaptureTree(result);
            m_state.ResultName = BuildTestAutomationCommands.ReflectedValue(result, "Name", "FullName");
            m_state.ResultState = BuildTestAutomationCommands.ReflectedValue(result, "ResultState", "TestStatus", "Outcome");
            m_state.Message = BuildTestAutomationCommands.ReflectedValue(result, "Message");
            BuildTestAutomationCommands.CaptureFailedTestResultTree(m_state, result);
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(m_state, result);
            BuildTestAutomationCommands.RefreshRecoveredFilterIdentity(m_state);
            var passCount = ReflectedLong(result, "PassCount");
            var failCount = ReflectedLong(result, "FailCount");
            var skipCount = ReflectedLong(result, "SkipCount");
            var inconclusiveCount = ReflectedLong(result, "InconclusiveCount");
            var assertCount = ReflectedLong(result, "AssertCount");
            var testCaseCount = ReflectedLong(result, "TestCaseCount");
            var countsAvailable = BuildTestAutomationCommands.TryApplyTerminalTestCounts(m_state, passCount, failCount, skipCount, inconclusiveCount, assertCount, testCaseCount);
            if (countsAvailable) m_state.NonPassingEvidence.CheckTerminalCoverage(m_state.Total, m_state.Failed + m_state.Skipped);
            return countsAvailable;
        }

        static long? ReflectedLong(object instance, string name)
        {
            var value = BuildTestAutomationCommands.ReflectedValue(instance, name);
            return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result) ? result : (long?)null;
        }
    }
}
