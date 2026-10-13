using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.State;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityMcpPureHelperTests
    {
        const string Revision = "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Test]
        public void TestRunAdvertisesBoundedDurableAdmissionWithoutRelaxingExecutionGates()
        {
            var method = typeof(BuildTestAutomationCommands).GetMethod("TestRun", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var attributes = method.GetCustomAttributes(typeof(McpCommandAttribute), false);
            Assert.That(attributes.Length, Is.EqualTo(1));
            var descriptor = (McpCommandAttribute)attributes[0];
            Assert.That(descriptor.Id, Is.EqualTo("test.run"));
            Assert.That(descriptor.TimeoutMs, Is.EqualTo(60000));
            Assert.That(descriptor.Description, Does.Contain("60 seconds for admission/start including durable owner publication"));
            Assert.That(descriptor.Risk, Is.EqualTo("execute"));
            Assert.That(descriptor.Idempotency, Is.EqualTo("unsafe"));
            Assert.That(descriptor.SupportsJob, Is.False);
            Assert.That(descriptor.RequiresEditorStatus, Is.EqualTo(new[] { "editing" }));
            Assert.That(descriptor.Tags, Does.Contain("test_run_operation_correlated"));
            Assert.That(descriptor.Tags, Does.Contain("test_run_artifact_v3"));
            Assert.That(descriptor.SchemaJson, Does.Contain("\"max_seconds\""));
            Assert.That(descriptor.SchemaJson, Does.Not.Contain("admission_timeout"));
            Assert.That(descriptor.FeatureFlag, Is.EqualTo("build_test_automation"));
        }

        [Test]
        public void TestRunTerminalWaitAdvertisesExactCompileReadinessContractOnlyInWrapperMetadata()
        {
            var wrapper = typeof(BuildTestAutomationCommands).GetField("TestRunWrapperWaitJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            var schema = typeof(BuildTestAutomationCommands).GetField("TestRunSchemaJson", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
            Assert.That(wrapper, Is.Not.Null);
            Assert.That(schema, Is.Not.Null);
            Assert.That(wrapper.GetRawConstantValue(), Is.EqualTo("{\"mode\":\"wait\",\"wait_for_test_run_terminal_ms\":{\"min_ms\":1,\"max_ms\":295000,\"compile_readiness_contract\":\"sgg.unity_mcp.test_run_compile_readiness.v1\",\"deadline_scope\":\"start_and_terminal_poll\",\"response_mode\":\"compact\",\"dry_run\":false}}"));
            Assert.That((string)schema.GetRawConstantValue(), Does.Not.Contain("compile_readiness_contract"));
        }

        [TestCase(false, false, false, false, false, "editing", true)]
        [TestCase(true, false, false, false, false, "compiling", false)]
        [TestCase(false, true, false, false, false, "updating", false)]
        [TestCase(false, false, false, true, false, "playing", false)]
        [TestCase(false, false, true, true, false, "playing", false)]
        [TestCase(false, false, true, true, true, "playing_paused", false)]
        public void EditorEditModeEligibilityNormalizesFreshState(bool isCompiling, bool isUpdating, bool isPlaying, bool isPlayingOrWillChangePlaymode, bool isPaused, string expectedStatus, bool expectedEligible)
        {
            var state = EditorEditModeEligibility.From(isCompiling, isUpdating, isPlaying, isPlayingOrWillChangePlaymode, isPaused);

            Assert.That(state.Status, Is.EqualTo(expectedStatus));
            Assert.That(state.IsEligible, Is.EqualTo(expectedEligible));
            Assert.That(state.NormalizedStateJson(), Does.Contain("\"status\":\"" + expectedStatus + "\""));
            Assert.That(state.NormalizedStateJson(), Does.Contain("\"edit_mode_eligible\":" + (expectedEligible ? "true" : "false")));
        }

        [TestCase(true, false, false, false, "editor_not_ready", "compiling")]
        [TestCase(false, true, false, false, "editor_not_ready", "updating")]
        [TestCase(false, false, false, true, "unsupported_in_play_mode", "playing")]
        [TestCase(false, false, true, false, "unsupported_in_play_mode", "playing")]
        public void AssetWriteEligibilityRefusesWithoutMutation(bool isCompiling, bool isUpdating, bool isPlaying, bool isPlayingOrWillChangePlaymode, string expectedCode, string expectedStatus)
        {
            var state = EditorEditModeEligibility.From(isCompiling, isUpdating, isPlaying, isPlayingOrWillChangePlaymode, false);
            var result = AssetSafeWriteCommands.RequireEditing("asset.delete", true, state);

            Assert.That(result.ErrorCode, Is.EqualTo(expectedCode));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"mutation_state\":\"not_applied\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"status\":\"" + expectedStatus + "\""));
        }

        [Test]
        public void AssetDeleteGuidReplacementFailsBeforeMutation()
        {
            var originalGuid = new string('a', 32);
            var replacementGuid = new string('b', 32);
            var result = AssetSafeWriteCommands.RequirePreconditionGuid(
                "{\"precondition_guid\":\"" + originalGuid + "\"}",
                "asset.delete",
                "Assets/McpPr210/Owned.asset",
                replacementGuid);

            Assert.That(result.ErrorCode, Is.EqualTo("precondition_failed"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"current_guid\":\"" + replacementGuid + "\""));
        }

        [Test]
        public void AssetDeleteDryRunIgnoresStaleBridgeSnapshotWithoutTouchingAssets()
        {
            var liveState = EditorStatusCommands.CaptureEditModeEligibility();
            if (!liveState.IsEligible)
            {
                Assert.Ignore("Edit Mode eligibility is required for the stale snapshot regression.");
            }

            var staleState = new BridgeRuntimeState();
            var context = new McpCommandContext(staleState, "mcp-pr-210", "test", "{\"asset_path\":\"Assets/__SGG_MCP_PR_210_MISSING__.asset\",\"dry_run\":true}", "{}");
            var result = AssetSafeWriteCommands.AssetDelete(context);

            Assert.That(staleState.Status, Is.EqualTo("starting"));
            Assert.That(result.ErrorCode, Is.EqualTo("asset_not_found"));
        }

        [Test]
        public void AssetDeletePreservesReplacementAssetWhenDryRunGuidIsStale()
        {
            if (!EditorStatusCommands.CaptureEditModeEligibility().IsEligible)
            {
                Assert.Ignore("Edit Mode eligibility is required for the replacement GUID regression.");
            }

            var folderPath = "Assets/__SGG_UnityMcp_McpPr210_" + Guid.NewGuid().ToString("N");
            var assetPath = folderPath + "/Owned.anim";
            var replacementPath = folderPath + "/Replacement.anim";
            try
            {
                Assert.That(UnityEditor.AssetDatabase.CreateFolder("Assets", folderPath.Substring("Assets/".Length)), Is.Not.Empty);
                UnityEditor.AssetDatabase.CreateAsset(new UnityEngine.AnimationClip(), assetPath);
                var originalGuid = UnityEditor.AssetDatabase.AssetPathToGUID(assetPath);
                UnityEditor.AssetDatabase.CreateAsset(new UnityEngine.AnimationClip(), replacementPath);
                var replacementGuid = UnityEditor.AssetDatabase.AssetPathToGUID(replacementPath);
                Assert.That(replacementGuid, Is.Not.EqualTo(originalGuid));
                var staleState = new BridgeRuntimeState();
                var dryRun = AssetSafeWriteCommands.AssetDelete(new McpCommandContext(
                    staleState,
                    "mcp-pr-210-dry-run",
                    "test",
                    "{\"asset_path\":\"" + assetPath + "\",\"dry_run\":true}",
                    "{}"));

                Assert.That(dryRun.Status, Is.EqualTo("ok"));
                Assert.That(JsonUtil.ExtractString(dryRun.DataJson, "precondition_guid"), Is.EqualTo(originalGuid));
                Assert.That(dryRun.DataJson, Does.Contain("\"checkpoint.create\""));
                Assert.That(dryRun.DataJson, Does.Not.Contain("checkpoint.create_before_write"));
                Assert.That(UnityEditor.AssetDatabase.DeleteAsset(assetPath), Is.True);
                Assert.That(UnityEditor.AssetDatabase.MoveAsset(replacementPath, assetPath), Is.Empty);
                Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(assetPath), Is.EqualTo(replacementGuid));

                var apply = AssetSafeWriteCommands.AssetDelete(new McpCommandContext(
                    staleState,
                    "mcp-pr-210-apply",
                    "test",
                    "{\"asset_path\":\"" + assetPath + "\",\"dry_run\":false,\"precondition_guid\":\"" + originalGuid + "\"}",
                    "{}"));

                Assert.That(apply.ErrorCode, Is.EqualTo("precondition_failed"));
                Assert.That(UnityEditor.AssetDatabase.AssetPathToGUID(assetPath), Is.EqualTo(replacementGuid));
                Assert.That(UnityEditor.AssetDatabase.LoadMainAssetAtPath(assetPath), Is.Not.Null);
            }
            finally
            {
                UnityEditor.AssetDatabase.DeleteAsset(folderPath);
            }
        }

        [Test]
        public void BootstrapLifecycleFailureKindsAreBounded()
        {
            Assert.That(BridgeBootstrapLifecycleObserver.FailureKind(new IOException("host path")), Is.EqualTo("io"));
            Assert.That(BridgeBootstrapLifecycleObserver.FailureKind(new UnauthorizedAccessException("host path")), Is.EqualTo("unauthorized"));
            Assert.That(BridgeBootstrapLifecycleObserver.FailureKind(new InvalidOperationException("host path")), Is.EqualTo("unexpected"));
        }

        [Test]
        public void TransformSerializedValuesUseTypedInvariantJson()
        {
            var previousCulture = CultureInfo.CurrentCulture;
            var gameObject = new UnityEngine.GameObject("MCP-PR-089 Transform");
            gameObject.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("fr-FR");
                var serialized = new UnityEditor.SerializedObject(gameObject.transform);
                var position = serialized.FindProperty("m_LocalPosition");
                var rotation = serialized.FindProperty("m_LocalRotation");
                var scale = serialized.FindProperty("m_LocalScale");
                var constrained = serialized.FindProperty("m_ConstrainProportionsScale");
                position.vector3Value = new UnityEngine.Vector3(1.25f, -2.5f, 3.75f);
                rotation.quaternionValue = new UnityEngine.Quaternion(0.125f, -0.25f, 0.5f, 0.75f);
                scale.vector3Value = new UnityEngine.Vector3(0.5f, 1.5f, 2.5f);
                constrained.boolValue = true;

                Assert.That(SemanticInspectionCommands.SerializedPropertyValueJson(position), Is.EqualTo("{\"x\":1.25,\"y\":-2.5,\"z\":3.75}"));
                Assert.That(SemanticInspectionCommands.SerializedPropertyValueJson(rotation), Is.EqualTo("{\"x\":0.125,\"y\":-0.25,\"z\":0.5,\"w\":0.75}"));
                Assert.That(SemanticInspectionCommands.SerializedPropertyValueJson(scale), Is.EqualTo("{\"x\":0.5,\"y\":1.5,\"z\":2.5}"));
                Assert.That(SemanticInspectionCommands.SerializedPropertyValueJson(constrained), Is.EqualTo("\"true\""));
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
                UnityEngine.Object.DestroyImmediate(gameObject);
            }
        }

        [Test]
        public void SerializedVectorsKeepValidJsonForNonFiniteComponents()
        {
            var fixture = UnityEngine.ScriptableObject.CreateInstance<SerializedVectorFixture>();
            try
            {
                fixture.hideFlags = UnityEngine.HideFlags.HideAndDontSave;
                var serialized = new UnityEditor.SerializedObject(fixture);
                var vector2 = serialized.FindProperty("vector2");
                var vector4 = serialized.FindProperty("vector4");
                vector2.vector2Value = new UnityEngine.Vector2(float.NaN, float.PositiveInfinity);
                vector4.vector4Value = new UnityEngine.Vector4(1.25f, float.NegativeInfinity, -2.5f, 3.75f);

                Assert.That(SemanticInspectionCommands.SerializedPropertyValueJson(vector2), Is.EqualTo("{\"x\":null,\"y\":null}"));
                Assert.That(SemanticInspectionCommands.SerializedPropertyValueJson(vector4), Is.EqualTo("{\"x\":1.25,\"y\":null,\"z\":-2.5,\"w\":3.75}"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(fixture);
            }
        }

        [Test]
        public void RuntimeValidationErrorUsesBoundedVersionedFieldContract()
        {
            var result = CommandExecutionResult.ValidationError("invalid_field_value", "Target constraint failed", "enabled", "unsupported_for_target");
            var indexed = CommandExecutionResult.ValidationError("invalid_field_value", "Indexed constraint failed", "items", 2, "pattern_mismatch");

            Assert.That(result.ErrorCode, Is.EqualTo("invalid_field_value"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("sgg.unity_mcp.runtime_validation.v1"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"path\":[\"enabled\"]"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"issue_code\":\"unsupported_for_target\""));
            Assert.That(result.ErrorDetailsJson, Does.Not.Contain("Target constraint failed"));
            Assert.That(indexed.ErrorDetailsJson, Does.Contain("\"path\":[\"items\",2]"));
            Assert.That(indexed.ErrorDetailsJson, Does.Contain("\"issue_code\":\"pattern_mismatch\""));
            Assert.That(indexed.ErrorDetailsJson, Does.Not.Contain("Indexed constraint failed"));
        }

        [Test]
        public void AssetImportScopeRejectsPackageScriptsWithoutReflectingTheirPath()
        {
            var valid = BuiltInCommands.AssetImportPathScopeError("Assets/Foo.prefab");
            var rejected = BuiltInCommands.AssetImportPathScopeError("Packages/com.private/Foo.cs");

            Assert.That(valid, Is.Null);
            Assert.That(rejected.ErrorCode, Is.EqualTo("invalid_field_value"));
            Assert.That(rejected.ErrorMessage, Does.Contain("package.local_script.import"));
            Assert.That(rejected.ErrorMessage, Does.Contain("Local or Embedded"));
            Assert.That(rejected.ErrorDetailsJson, Does.Contain("sgg.unity_mcp.runtime_validation.v1"));
            Assert.That(rejected.ErrorDetailsJson, Does.Contain("\"path\":[\"asset_path\"]"));
            Assert.That(rejected.ErrorDetailsJson, Does.Contain("\"issue_code\":\"unsupported_for_target\""));
            Assert.That(rejected.ToJson("mcp-pr-215"), Does.Not.Contain("com.private"));
            Assert.That(rejected.ToJson("mcp-pr-215"), Does.Not.Contain("Foo.cs"));
        }

        [Test]
        public void SerializedSceneWritePreconditionBindsRuntimeComponentPropertyAndValueHashes()
        {
            var baseline = SceneWiringCommands.SerializedSceneWritePreconditionHash("serialized.property.set", "game-a1", "global-a|101", "Assets/Test.unity", "/Root", 2, "Game.Settings", "Game", "enabled", "false", "true");

            Assert.That(SceneWiringCommands.SerializedSceneWritePreconditionHash("serialized.property.set", "game-b2", "global-a|101", "Assets/Test.unity", "/Root", 2, "Game.Settings", "Game", "enabled", "false", "true"), Is.Not.EqualTo(baseline));
            Assert.That(SceneWiringCommands.SerializedSceneWritePreconditionHash("serialized.property.set", "game-a1", "global-b|102", "Assets/Test.unity", "/Root", 2, "Game.Settings", "Game", "enabled", "false", "true"), Is.Not.EqualTo(baseline));
            Assert.That(SceneWiringCommands.SerializedSceneWritePreconditionHash("serialized.property.set", "game-a1", "global-a|101", "Assets/Test.unity", "/Root", 2, "Game.Settings", "Game", "other", "false", "true"), Is.Not.EqualTo(baseline));
            Assert.That(SceneWiringCommands.SerializedSceneWritePreconditionHash("serialized.property.set", "game-a1", "global-a|101", "Assets/Test.unity", "/Root", 2, "Game.Settings", "Game", "enabled", "true", "true"), Is.Not.EqualTo(baseline));
            Assert.That(SceneWiringCommands.SerializedSceneWritePreconditionHash("serialized.property.set", "game-a1", "global-a|101", "Assets/Test.unity", "/Root", 2, "Game.Settings", "Game", "enabled", "false", "false"), Is.Not.EqualTo(baseline));
        }

        [Test]
        public void SerializedSceneWriteFailuresAreTypedAndDoNotReflectValues()
        {
            const string hash = "sha256:0123456789abcdef";
            var missing = SceneWiringCommands.RequirePrecondition("{}", hash, "serialized.property.set");
            var stale = SceneWiringCommands.RequirePrecondition("{\"precondition_hash\":\"sha256:stale\"}", hash, "serialized.property.set");
            var failure = SceneWiringCommands.SerializedWriteFailureDetailsJson("serialized.property.set", "apply_modified_properties", "exception", "System.InvalidOperationException", "application_uncertain", hash);

            Assert.That(missing.ErrorCode, Is.EqualTo("precondition_required"));
            Assert.That(stale.ErrorCode, Is.EqualTo("precondition_failed"));
            Assert.That(stale.ErrorDetailsJson, Does.Contain("\"value_data_included\":false"));
            Assert.That(failure, Does.Contain("sgg.unity_mcp.serialized_write_failure.v1"));
            Assert.That(failure, Does.Contain("\"mutation_state\":\"application_uncertain\""));
            Assert.That(failure, Does.Contain("\"automatic_retry_safe\":false"));
        }

        [Test]
        public void JsonPropertyLookupIgnoresStringValuesAndNestedProperties()
        {
            Assert.That(JsonUtil.HasProperty("{\"title\":\"focused\"}", "focused"), Is.False);
            Assert.That(JsonUtil.HasProperty("{\"nested\":{\"focused\":true}}", "focused"), Is.False);
            Assert.That(JsonUtil.HasProperty("{\"nested\":{\"text\":\"}\"},\"focused\":true}", "focused"), Is.True);
            Assert.That(JsonUtil.HasProperty("{\"focused\":true,\"title\":\"Game\"}", "focused"), Is.True);
            Assert.That(JsonUtil.HasProperty("{\"dry\\u005frun\":true}", "dry_run"), Is.True);
            Assert.That(JsonUtil.ExtractString("{\"value\":\"A\\u005fB\"}", "value"), Is.EqualTo("A_B"));
            Assert.That(
                BuildTestAutomationCommands.ExtractTopLevelJsonStringArray(
                    "{\"nested\":{\"terminal_result_identities\":[\"wrong\"]},\"terminal_result_identities\":[\"Suite.CaseA\",\"Suite.CaseB\"]}",
                    "terminal_result_identities",
                    2),
                Is.EqualTo(new[] { "Suite.CaseA", "Suite.CaseB" }));
        }

        [Test]
        public void TerminalTestAggregatesUseLeafCountsInsteadOfAssertCount()
        {
            const int assertCount = 16;

            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(59, 0, 17, 0, out var counts), Is.True);
            Assert.That(counts.Total, Is.EqualTo(76));
            Assert.That(counts.Passed, Is.EqualTo(59));
            Assert.That(counts.Failed, Is.Zero);
            Assert.That(counts.Skipped, Is.EqualTo(17));
            Assert.That(counts.Total, Is.Not.EqualTo(assertCount));
        }

        [Test]
        public void TerminalTestAggregatesIncludeInconclusiveAsSkipped()
        {
            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(2, 1, 3, 4, out var counts), Is.True);
            Assert.That(counts.Total, Is.EqualTo(10));
            Assert.That(counts.Skipped, Is.EqualTo(7));
        }

        [Test]
        public void TerminalTestAggregatesAcceptAuthoritativeZero()
        {
            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(0, 0, 0, 0, out var counts), Is.True);
            Assert.That(counts.Total, Is.Zero);
        }

        [Test]
        public void FilteredAuthoritativeZeroIsTypedFailureInsteadOfSuccessfulEmptyRun()
        {
            var state = FilteredTestRunState();
            state.Status = "completed";
            state.TerminalCountsAuthoritative = true;

            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(state);

            Assert.That(state.Status, Is.EqualTo("resolved_filter_zero_execution"));
            Assert.That(state.ResultState, Is.EqualTo("resolved_filter_zero_execution"));
            Assert.That(state.TerminalResultIdentityStatus, Is.EqualTo("zero_execution"));
            Assert.That(state.Success, Is.False);
        }

        [Test]
        public void RecoveredFilterIdentityDistinguishesVerifiedDriftAndMissingBindings()
        {
            var resolved = Resolution("Identity.Fixture.Case");
            var changed = Resolution("Identity.Fixture.Other");
            var state = FilteredTestRunState();
            state.RecoveredAfterReload = true;
            state.CallbackReRegisteredAfterReload = true;
            state.FilterResolutionIdentity = BuildTestAutomationCommands.TestFilterResolutionIdentity(resolved);

            BuildTestAutomationCommands.RefreshRecoveredFilterIdentity(state, resolved);
            Assert.That(state.FilterResolutionIdentityStatus, Is.EqualTo("verified_after_reload"));

            BuildTestAutomationCommands.RefreshRecoveredFilterIdentity(state, changed);
            Assert.That(state.FilterResolutionIdentityStatus, Is.EqualTo("drift_after_reload"));

            state.FilterResolutionIdentity = string.Empty;
            BuildTestAutomationCommands.RefreshRecoveredFilterIdentity(state, resolved);
            Assert.That(state.FilterResolutionIdentityStatus, Is.EqualTo("missing_after_reload"));
        }

        [Test]
        public void RecoveredFilterRevalidationWaitsOnlyForPendingExactCatalogIdentity()
        {
            var state = FilteredTestRunState();
            state.RecoveredAfterReload = true;
            state.FilterResolutionIdentityStatus = "revalidation_pending";

            Assert.That(BuildTestAutomationCommands.RequiresRecoveredFilterRevalidationWait(state), Is.True);

            state.FilterResolutionIdentityStatus = "verified_after_reload";
            Assert.That(BuildTestAutomationCommands.RequiresRecoveredFilterRevalidationWait(state), Is.False);

            state.RecoveredAfterReload = false;
            state.FilterResolutionIdentityStatus = "revalidation_pending";
            Assert.That(BuildTestAutomationCommands.RequiresRecoveredFilterRevalidationWait(state), Is.False);
        }

        [TestCase("revalidation_unavailable", "Passed")]
        [TestCase("revalidation_unavailable", "Failed")]
        [TestCase("revalidation_timeout_after_reload", "Passed")]
        [TestCase("revalidation_timeout_after_reload", "Failed")]
        public void MatchedTerminalIdentityPreservesAuthoritativeResultWhenRevalidationCannotFinish(string revalidationStatus, string resultState)
        {
            var state = RecoveredMatchedTestRunState(revalidationStatus);
            state.ResultState = resultState;
            if (resultState == "Failed")
            {
                state.Passed = 0;
                state.Failed = 1;
                state.PassCount = 0;
                state.FailCount = 1;
            }

            Assert.That(BuildTestAutomationCommands.CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(state), Is.True);

            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(state);
            state.Success = BuildTestAutomationCommands.IsSuccessfulTestRunOutcome(state);

            Assert.That(state.Status, Is.EqualTo("completed"));
            Assert.That(state.ResultState, Is.EqualTo(resultState));
            Assert.That(state.Failed, Is.EqualTo(resultState == "Failed" ? 1 : 0));
            Assert.That(state.Success, Is.EqualTo(resultState == "Passed"));
        }

        [Test]
        public void VerifiedRecoveredFilterRemainsTheNormalAuthorityPath()
        {
            var state = RecoveredMatchedTestRunState("verified_after_reload");

            Assert.That(BuildTestAutomationCommands.CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(state), Is.False);

            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(state);

            Assert.That(state.Status, Is.EqualTo("completed"));
            Assert.That(state.ResultState, Is.EqualTo("Passed"));
        }

        [TestCase("drift_after_reload", "test_filter_drift_after_reload")]
        [TestCase("missing_after_reload", "test_filter_identity_unavailable_after_reload")]
        [TestCase("revalidation_pending", "test_filter_identity_unavailable_after_reload")]
        [TestCase("revalidation_incomplete", "test_filter_identity_unavailable_after_reload")]
        public void MatchedTerminalIdentityDoesNotOverrideOtherRevalidationStates(string revalidationStatus, string expectedStatus)
        {
            var state = RecoveredMatchedTestRunState(revalidationStatus);

            Assert.That(BuildTestAutomationCommands.CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(state), Is.False);

            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(state);

            Assert.That(state.Status, Is.EqualTo(expectedStatus));
            Assert.That(state.ResultState, Is.EqualTo(expectedStatus));
        }

        [Test]
        public void UnavailableRevalidationRequiresEveryTerminalAuthorityInvariant()
        {
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.Status = "running");
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.Finished = false);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.CallbackReRegisteredAfterReload = false);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.ExecuteDispatchCount = 0);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.ExecuteDispatchCount = 2);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.FilterResolutionStatus = "ambiguous");
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.FilterResolutionIdentity = string.Empty);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.ResolvedFilterCandidateCount = 2);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.ResolvedFilterName = string.Empty);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.TerminalCountsAuthoritative = false);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.Total = 0);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.TerminalResultIdentityStatus = "mismatch");
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.TerminalResultIdentityStatus = "unavailable");
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.TerminalResultLeafCount = 0);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.TerminalResultMatchingLeafCount = 0);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.TerminalResultUnexpectedLeafCount = 1);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.TerminalResultTraversalTruncated = true);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.Total = 2);
            AssertRejected(RecoveredMatchedTestRunState("revalidation_unavailable"), state => state.PassCount = 2);
        }

        [Test]
        public void RecoveredMatchedTerminalAuthoritySurvivesSchemaV3RoundTrip()
        {
            var awaiting = RecoveredMatchedTestRunState("revalidation_unavailable");
            awaiting.Status = "awaiting_filter_revalidation_after_reload";
            awaiting.Finished = false;
            var persisted = BuildTestAutomationCommands.TestRunStateJson(awaiting);
            Assert.That(
                BuildTestAutomationCommands.TryValidateTestRunArtifactIdentityForV3Transition(
                    persisted,
                    awaiting.RunId,
                    awaiting.ArtifactPath,
                    out _,
                    out _,
                    out _),
                Is.True);

            var restored = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(awaiting.RunId, awaiting.ArtifactPath, persisted);
            restored.Status = "completed";
            restored.Finished = true;
            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(restored);
            restored.Success = BuildTestAutomationCommands.IsSuccessfulTestRunOutcome(restored);
            restored.ArtifactPublicationState = "committed";
            var completed = BuildTestAutomationCommands.TestRunStateJson(restored);

            Assert.That(restored.CallbackReRegisteredAfterReload, Is.True);
            Assert.That(restored.AssertCountAvailable, Is.True);
            Assert.That(restored.AssertCount, Is.EqualTo(1));
            Assert.That(restored.TestCaseCountAvailable, Is.True);
            Assert.That(restored.TestCaseCount, Is.EqualTo(1));
            Assert.That(completed, Does.Contain("\"status\":\"completed\""));
            Assert.That(completed, Does.Contain("\"success\":true"));
            Assert.That(completed, Does.Contain("\"test_result_success\":true"));
            Assert.That(completed, Does.Contain("\"filter_resolution_identity_status\":\"revalidation_unavailable\""));
            Assert.That(completed, Does.Contain("\"proves_filter_matched\":true"));
            Assert.That(completed, Does.Contain("\"assert_count\":1"));
            Assert.That(completed, Does.Contain("\"test_case_count\":1"));
            Assert.That(completed, Does.Contain("\"write_command_repeated\":false"));
            Assert.That(completed, Does.Contain("\"write_replay_allowed\":false"));
        }

        [Test]
        public void RetainedPublicationReclassifiesRecoveredTerminalAuthority()
        {
            var state = RecoveredMatchedTestRunState("revalidation_timeout_after_reload");
            state.Success = BuildTestAutomationCommands.IsSuccessfulTestRunOutcome(state);
            state.ArtifactPublicationState = "failed";
            var retainedContent = BuildTestAutomationCommands.TestRunStateJson(state);

            var restored = BuildTestAutomationCommands.TestRunStateFromRetainedPublicationSession(state.RunId, retainedContent);
            Assert.That(restored, Is.Not.Null);
            var serialized = BuildTestAutomationCommands.TestRunStateJson(restored);

            Assert.That(restored.FilterResolutionIdentityStatus, Is.EqualTo("revalidation_timeout_after_reload"));
            Assert.That(restored.Status, Is.EqualTo("completed"));
            Assert.That(restored.Success, Is.True);
            Assert.That(serialized, Does.Contain("\"test_result_success\":true"));
            Assert.That(serialized, Does.Contain("\"proves_filter_matched\":true"));
        }

        [Test]
        public void RootFailureCannotBecomeSuccessWhenMatchedLeavesPassed()
        {
            var state = RecoveredMatchedTestRunState("revalidation_unavailable");
            state.ResultState = "Failed";

            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(state);
            state.Success = BuildTestAutomationCommands.IsSuccessfulTestRunOutcome(state);
            var completed = BuildTestAutomationCommands.TestRunStateJson(state);

            Assert.That(state.Status, Is.EqualTo("completed"));
            Assert.That(state.Failed, Is.Zero);
            Assert.That(completed, Does.Contain("\"success\":false"));
            Assert.That(completed, Does.Contain("\"test_result_success\":false"));
            Assert.That(completed, Does.Contain("\"proves_filter_matched\":true"));
        }

        [Test]
        public void IncoherentPersistedTerminalCountsLoseAuthorityOnSchemaV3Restore()
        {
            var awaiting = RecoveredMatchedTestRunState("revalidation_timeout_after_reload");
            awaiting.Status = "awaiting_filter_revalidation_after_reload";
            awaiting.Finished = false;
            awaiting.Total = 2;
            var persisted = BuildTestAutomationCommands.TestRunStateJson(awaiting);

            var restored = BuildTestAutomationCommands.TestRunStateFromPersistedArtifact(awaiting.RunId, awaiting.ArtifactPath, persisted);
            restored.Status = "completed";
            restored.Finished = true;

            Assert.That(restored.TerminalCountsAuthoritative, Is.False);
            Assert.That(BuildTestAutomationCommands.CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(restored), Is.False);

            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(restored);
            restored.Success = BuildTestAutomationCommands.IsSuccessfulTestRunOutcome(restored);

            Assert.That(restored.Status, Is.EqualTo("test_filter_identity_unavailable_after_reload"));
            Assert.That(restored.Success, Is.False);
            Assert.That(BuildTestAutomationCommands.TestRunStateJson(restored), Does.Contain("\"authoritative_counts_available\":false"));
        }

        [Test]
        public void TerminalResultLeavesMustMatchBoundExactLeafOrSuite()
        {
            var leafState = FilteredTestRunState();
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(
                leafState,
                Result("Root", Result("Identity.Fixture.Case")));
            Assert.That(leafState.TerminalResultIdentityStatus, Is.EqualTo("matched"));
            Assert.That(leafState.TerminalResultIdentities, Is.EqualTo(new[] { "Identity.Fixture.Case" }));

            var mismatchState = FilteredTestRunState();
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(
                mismatchState,
                Result("Root", Result("Identity.Fixture.Other")));
            Assert.That(mismatchState.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));
            mismatchState.Status = "completed";
            mismatchState.TerminalCountsAuthoritative = true;
            mismatchState.Total = 1;
            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(mismatchState);
            Assert.That(mismatchState.Status, Is.EqualTo("test_operation_result_identity_mismatch"));

            var suiteState = FilteredTestRunState();
            suiteState.ResolvedFilterName = "Identity.Fixture";
            suiteState.ResolvedFilterIsSuite = true;
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(
                suiteState,
                Result("Root", Result("Identity.Fixture", Result("Identity.Fixture.Case"))));
            Assert.That(suiteState.TerminalResultIdentityStatus, Is.EqualTo("matched"));

            var extraLeafState = FilteredTestRunState();
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(
                extraLeafState,
                Result("Root", Result("Identity.Fixture.Case"), Result("Identity.Fixture.Other")));
            Assert.That(extraLeafState.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));
            Assert.That(extraLeafState.TerminalResultUnexpectedLeafCount, Is.EqualTo(1));

            var duplicateLeafState = FilteredTestRunState();
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(
                duplicateLeafState,
                Result("Root", Result("Identity.Fixture.Case"), Result("Identity.Fixture.Case")));
            Assert.That(duplicateLeafState.TerminalResultIdentityStatus, Is.EqualTo("matched"));
            Assert.That(duplicateLeafState.TerminalResultLeafCount, Is.EqualTo(1));

            var broadSuiteState = FilteredTestRunState();
            broadSuiteState.ResolvedFilterName = "Identity.Fixture";
            broadSuiteState.ResolvedFilterIsSuite = true;
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(
                broadSuiteState,
                Result("Root", Result("Identity.Fixture", Result("Identity.Fixture.Case")), Result("Other.Fixture.Case")));
            Assert.That(broadSuiteState.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));

            var unnamedLeafState = FilteredTestRunState();
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(
                unnamedLeafState,
                Result("Root", Result("Identity.Fixture.Case"), new FakeTestResult { Children = new FakeTestResult[0] }));
            Assert.That(unnamedLeafState.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));
            Assert.That(unnamedLeafState.TerminalResultLeafCount, Is.EqualTo(2));

            var exactSuiteLeafState = FilteredTestRunState();
            exactSuiteLeafState.ResolvedFilterName = "Identity.Fixture";
            exactSuiteLeafState.ResolvedFilterIsSuite = true;
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(exactSuiteLeafState, Result("Identity.Fixture"));
            Assert.That(exactSuiteLeafState.TerminalResultIdentityStatus, Is.EqualTo("matched"));

            var emptySuiteContainerState = FilteredTestRunState();
            emptySuiteContainerState.ResolvedFilterName = "Identity.Fixture";
            emptySuiteContainerState.ResolvedFilterIsSuite = true;
            var emptySuiteContainer = Result("Identity.Fixture");
            emptySuiteContainer.IsSuite = true;
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(emptySuiteContainerState, emptySuiteContainer);
            Assert.That(emptySuiteContainerState.TerminalResultIdentityStatus, Is.EqualTo("unavailable"));

            var wrongContextState = FilteredTestRunState();
            var wrongContextLeaf = Result("Identity.Fixture.Case");
            wrongContextLeaf.AssemblyName = "Other.Editor";
            wrongContextLeaf.TestMode = "EditMode";
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(wrongContextState, Result("Root", wrongContextLeaf));
            Assert.That(wrongContextState.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));

            var normalizedContextState = FilteredTestRunState();
            var normalizedContextLeaf = Result("Identity.Fixture.Case");
            normalizedContextLeaf.AssemblyName = "Library/ScriptAssemblies/Identity.Editor.dll";
            normalizedContextLeaf.TestMode = "Play Mode";
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(normalizedContextState, Result("Root", normalizedContextLeaf));
            Assert.That(normalizedContextState.TerminalResultIdentityStatus, Is.EqualTo("matched"));

            var unavailableModeState = FilteredTestRunState();
            var unavailableModeLeaf = Result("Identity.Fixture.Case");
            unavailableModeLeaf.TestMode = "0";
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(unavailableModeState, Result("Root", unavailableModeLeaf));
            Assert.That(unavailableModeState.TerminalResultIdentityStatus, Is.EqualTo("matched"));

            var wrongModeState = FilteredTestRunState();
            var wrongModeLeaf = Result("Identity.Fixture.Case");
            wrongModeLeaf.TestMode = "EditMode";
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(wrongModeState, Result("Root", wrongModeLeaf));
            Assert.That(wrongModeState.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));
        }

        [Test]
        public void FlatParameterizedSuiteResultsUseExactSemanticIdentityBoundaries()
        {
            var state = FilteredTestRunState();
            state.ResolvedFilterName = "Identity.Fixture";
            state.ResolvedFilterIsSuite = true;
            var suite = Result("Identity.Fixture");
            suite.IsSuite = true;
            var parameterizedContainer = Result("Identity.Fixture.Cases");
            parameterizedContainer.IsSuite = true;

            var projectAggregate = Result("ValidationProject", suite, parameterizedContainer, Result("Identity.Fixture.Case(1)"), Result("Identity.Fixture.Case(2)"));
            projectAggregate.AssemblyName = "ValidationProject";
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(state, projectAggregate);

            Assert.That(state.TerminalResultIdentityStatus, Is.EqualTo("matched"));
            Assert.That(state.TerminalResultLeafCount, Is.EqualTo(2));
            Assert.That(state.TerminalResultMatchingLeafCount, Is.EqualTo(2));
            Assert.That(state.TerminalResultIdentities, Is.EqualTo(new[] { "Identity.Fixture.Case(1)", "Identity.Fixture.Case(2)" }));
        }

        [Test]
        public void FlatSuiteResultsRejectMissingOrDuplicateRootsAndPrefixCollisions()
        {
            var missingRoot = FilteredTestRunState();
            missingRoot.ResolvedFilterName = "Identity.Fixture";
            missingRoot.ResolvedFilterIsSuite = true;
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(missingRoot, Result("Root", Result("Identity.Fixture.Case")));
            Assert.That(missingRoot.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));

            var duplicateRoot = FilteredTestRunState();
            duplicateRoot.ResolvedFilterName = "Identity.Fixture";
            duplicateRoot.ResolvedFilterIsSuite = true;
            var firstRoot = Result("Identity.Fixture");
            firstRoot.IsSuite = true;
            var secondRoot = Result("Identity.Fixture");
            secondRoot.IsSuite = true;
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(duplicateRoot, Result("Root", firstRoot, secondRoot, Result("Identity.Fixture.Case")));
            Assert.That(duplicateRoot.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));

            var prefixCollision = FilteredTestRunState();
            prefixCollision.ResolvedFilterName = "Identity.Fixture";
            prefixCollision.ResolvedFilterIsSuite = true;
            var exactRoot = Result("Identity.Fixture");
            exactRoot.IsSuite = true;
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(prefixCollision, Result("Root", exactRoot, Result("Identity.FixtureCollision.Case")));
            Assert.That(prefixCollision.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));
            Assert.That(prefixCollision.TerminalResultUnexpectedLeafCount, Is.EqualTo(1));
        }

        [Test]
        public void DuplicateTerminalCasesRequireConsistentOutcomes()
        {
            var consistent = FilteredTestRunState();
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(consistent, Result("Root", Result("Identity.Fixture.Case"), Result("Identity.Fixture.Case")));
            Assert.That(consistent.TerminalResultIdentityStatus, Is.EqualTo("matched"));
            Assert.That(consistent.TerminalResultLeafCount, Is.EqualTo(1));

            var inconsistent = FilteredTestRunState();
            var failedDuplicate = Result("Identity.Fixture.Case");
            failedDuplicate.ResultState = "Failed";
            BuildTestAutomationCommands.CaptureTerminalResultIdentities(inconsistent, Result("Root", Result("Identity.Fixture.Case"), failedDuplicate));
            Assert.That(inconsistent.TerminalResultIdentityStatus, Is.EqualTo("mismatch"));
            Assert.That(inconsistent.TerminalResultUnexpectedLeafCount, Is.EqualTo(1));
        }

        [Test]
        public void TerminalResultTraversalFailsClosedAtTheNodeLimit()
        {
            var children = new List<FakeTestResult>();
            for (var index = 0; index < 4100; index++)
            {
                children.Add(Result(index == 0 ? "Identity.Fixture.Case" : "Identity.Fixture.Other" + index));
            }
            var state = FilteredTestRunState();

            BuildTestAutomationCommands.CaptureTerminalResultIdentities(state, Result("Root", children.ToArray()));

            Assert.That(state.TerminalResultTraversalTruncated, Is.True);
            Assert.That(state.TerminalResultIdentityStatus, Is.EqualTo("unavailable"));
            Assert.That(state.TerminalResultLeafCount, Is.LessThan(4100));
        }

        [Test]
        public void MultiCandidateFocusedFilterKeepsAggregateAuthorityWithoutSingleLeafBinding()
        {
            var state = FilteredTestRunState();
            state.ResolvedFilterCandidateCount = 2;
            state.ResolvedFilterName = string.Empty;
            state.Status = "completed";
            state.TerminalCountsAuthoritative = true;
            state.Total = 2;

            BuildTestAutomationCommands.CaptureTerminalResultIdentities(state, Result("Root", Result("Identity.Fixture.CaseA"), Result("Identity.Fixture.CaseB")));
            BuildTestAutomationCommands.ClassifyResolvedTestRunOutcome(state);

            Assert.That(state.TerminalResultIdentityStatus, Is.EqualTo("not_required_multi_candidate_filter"));
            Assert.That(state.Status, Is.EqualTo("completed"));
        }

        [TestCase(-1L, 0L, 0L, 0L)]
        [TestCase(0L, -1L, 0L, 0L)]
        [TestCase(0L, 0L, -1L, 0L)]
        [TestCase(0L, 0L, 0L, -1L)]
        [TestCase((long)int.MaxValue + 1L, 0L, 0L, 0L)]
        [TestCase(int.MaxValue, 1L, 0L, 0L)]
        [TestCase(0L, 0L, int.MaxValue, 1L)]
        public void TerminalTestAggregatesRejectNegativeOrOverflowingValues(long passed, long failed, long skipped, long inconclusive)
        {
            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(passed, failed, skipped, inconclusive, out _), Is.False);
        }

        [Test]
        public void TerminalTestAggregatesRejectUnavailableValues()
        {
            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(null, 0, 0, 0, out _), Is.False);
            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(0, null, 0, 0, out _), Is.False);
            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(0, 0, null, 0, out _), Is.False);
            Assert.That(BuildTestAutomationCommands.TryValidateTerminalTestCounts(0, 0, 0, null, out _), Is.False);
        }

        [Test]
        public void RecursiveRootAggregatesOverwriteSyntheticSuiteCallbacksAndIgnoreAssertCountForTotal()
        {
            const int provisionalSuiteCallbackCount = 16;
            const int assertCount = 16;
            var state = new BuildTestAutomationCommands.TestRunState("test-20260713-000000-00000000", "editmode", string.Empty, "artifact.json", 60, false)
            {
                Total = provisionalSuiteCallbackCount,
                Passed = provisionalSuiteCallbackCount,
            };

            Assert.That(BuildTestAutomationCommands.TryApplyTerminalTestCounts(state, 59, 0, 10, 7, assertCount, 76), Is.True);
            Assert.That(state.TerminalCountsAuthoritative, Is.True);
            Assert.That(state.Total, Is.EqualTo(76));
            Assert.That(state.Passed, Is.EqualTo(59));
            Assert.That(state.Skipped, Is.EqualTo(17));
            Assert.That(state.PassCount, Is.EqualTo(59));
            Assert.That(state.FailCount, Is.Zero);
            Assert.That(state.SkipCount, Is.EqualTo(10));
            Assert.That(state.InconclusiveCount, Is.EqualTo(7));
            Assert.That(state.AssertCountAvailable, Is.True);
            Assert.That(state.AssertCount, Is.EqualTo(assertCount));
            Assert.That(state.Total, Is.Not.EqualTo(provisionalSuiteCallbackCount));
            Assert.That(state.Total, Is.Not.EqualTo(assertCount));
            Assert.That(state.TestCaseCountAvailable, Is.True);
            Assert.That(state.TestCaseCount, Is.EqualTo(76));
        }

        [Test]
        public void OptionalTestCaseCountMismatchDoesNotChangeAuthoritativeLeafTotal()
        {
            var state = new BuildTestAutomationCommands.TestRunState("test-20260713-000000-00000000", "editmode", string.Empty, "artifact.json", 60, false);

            Assert.That(BuildTestAutomationCommands.TryApplyTerminalTestCounts(state, 3, 0, 1, 1, 99, 1), Is.True);
            Assert.That(state.Total, Is.EqualTo(5));
            Assert.That(state.TestCaseCount, Is.EqualTo(1));
            Assert.That(state.AssertCount, Is.EqualTo(99));
        }

        [Test]
        public void UnavailableTerminalTestAggregatesClearProvisionalCallbacks()
        {
            var state = new BuildTestAutomationCommands.TestRunState("test-20260713-000000-00000000", "editmode", string.Empty, "artifact.json", 60, false)
            {
                Total = 16,
                Passed = 16,
            };

            Assert.That(BuildTestAutomationCommands.TryApplyTerminalTestCounts(state, null, 0, 0, 0), Is.False);
            Assert.That(state.TerminalCountsAuthoritative, Is.False);
            Assert.That(state.Total, Is.Zero);
            Assert.That(state.Passed, Is.Zero);
            Assert.That(state.AssertCountAvailable, Is.False);
            Assert.That(state.TestCaseCountAvailable, Is.False);
        }

        static BuildTestAutomationCommands.TestRunState FilteredTestRunState()
        {
            const string runId = "test-20260807-120000-deadbeef";
            return new BuildTestAutomationCommands.TestRunState(runId, "playmode", "Identity.Fixture.Case", "Library/SGGUnityMcp/job-artifacts/test-runs/" + runId + ".json", 60, false)
            {
                TestAssembly = "Identity.Editor",
                ArgsHash = Revision,
                CallerArgsHash = Revision,
                FilterResolutionIdentity = Revision,
                FilterResolutionIdentityStatus = "bound_before_execute",
                ResolvedFilterName = "Identity.Fixture.Case",
                ResolvedFilterCandidateCount = 1,
            };
        }

        static BuildTestAutomationCommands.TestRunState RecoveredMatchedTestRunState(string revalidationStatus)
        {
            var state = FilteredTestRunState();
            state.RecoveredAfterReload = true;
            state.CallbackReRegisteredAfterReload = true;
            state.Finished = true;
            state.ExecuteDispatchCount = 1;
            state.FilterResolutionStatus = "resolved";
            state.FilterResolutionIdentityStatus = revalidationStatus;
            state.TerminalCountsAuthoritative = true;
            state.Total = 1;
            state.Passed = 1;
            state.PassCount = 1;
            state.AssertCountAvailable = true;
            state.AssertCount = 1;
            state.TestCaseCountAvailable = true;
            state.TestCaseCount = 1;
            state.Status = "completed";
            state.ResultState = "Passed";
            state.TerminalResultIdentityStatus = "matched";
            state.TerminalResultLeafCount = 1;
            state.TerminalResultMatchingLeafCount = 1;
            state.ArtifactPublicationState = "committed";
            return state;
        }

        static void AssertRejected(BuildTestAutomationCommands.TestRunState state, Action<BuildTestAutomationCommands.TestRunState> mutate)
        {
            mutate(state);
            Assert.That(BuildTestAutomationCommands.CanUseMatchedTerminalIdentityAfterUnavailableRevalidation(state), Is.False);
        }

        static UnityTestDiscoveryCatalog.FilterResolution Resolution(string fullName)
        {
            var candidate = new UnityTestDiscoveryCatalog.TestCandidate(
                "Identity.Editor",
                "playmode",
                fullName,
                fullName,
                "Identity.Fixture",
                "Identity.Fixture",
                fullName.Substring(fullName.LastIndexOf('.') + 1),
                "Runnable",
                true,
                false,
                false,
                "Identity.Fixture",
                false,
                fullName);
            return UnityTestDiscoveryCatalog.FilterResolution.Resolved(
                "playmode",
                fullName,
                "Identity.Editor",
                new List<string> { fullName },
                new List<UnityTestDiscoveryCatalog.TestCandidate> { candidate },
                "Identity.Editor",
                "exact_leaf");
        }

        static FakeTestResult Result(string fullName, params FakeTestResult[] children)
        {
            return new FakeTestResult { FullName = fullName, UniqueName = fullName, IsSuite = children != null && children.Length > 0, ResultState = "Passed", Children = children };
        }

        sealed class FakeTestResult
        {
            public string FullName { get; set; }
            public string UniqueName { get; set; }
            public string AssemblyName { get; set; }
            public string TestMode { get; set; }
            public bool IsSuite { get; set; }
            public bool IsTestAssembly { get; set; }
            public string ResultState { get; set; }
            public IEnumerable<FakeTestResult> Children { get; set; }
        }

        [Test]
        public void CompletedSchemaV1TestArtifactIsDowngradedWithoutTrustingHistoricalCounts()
        {
            const string legacy = "{\"command\":\"test.run\",\"artifact_schema_version\":1,\"package_version\":\"2026.7.13-2\",\"operation_id\":\"test-20260713-000000-00000000\",\"status\":\"completed\",\"success\":true,\"result\":\"Passed\",\"mode\":\"editmode\",\"filter\":\"Fixture.Test\",\"total\":16,\"passed\":16,\"failed\":0,\"skipped\":0}";

            var normalized = BuildTestAutomationCommands.NormalizePersistedTestRunArtifactForRead(legacy, "test-20260713-000000-00000000", "Library/test.json");

            Assert.That(normalized, Does.Contain("\"artifact_schema_version\":1"));
            Assert.That(normalized, Does.Contain("\"package_version\":\"2026.7.13-2\""));
            Assert.That(normalized, Does.Contain("\"status\":\"legacy_terminal_counts_untrusted\""));
            Assert.That(normalized, Does.Contain("\"success\":false"));
            Assert.That(normalized, Does.Contain("\"terminal_counts_authoritative\":false"));
            Assert.That(normalized, Does.Contain("\"test_assembly\":\"\""));
            Assert.That(normalized, Does.Contain("\"source\":\"legacy_artifact_v1\",\"available\":false"));
            Assert.That(normalized, Does.Contain("\"total\":16"));
            Assert.That(normalized, Does.Contain("\"next_valid_actions\":[\"test.run\""));
        }

        [Test]
        public void SchemaV2AndRunningSchemaV1TestArtifactsRemainUnchangedOnRead()
        {
            const string current = "{\"command\":\"test.run\",\"artifact_schema_version\":2,\"status\":\"completed\",\"terminal_counts_authoritative\":true}";
            const string activeLegacy = "{\"command\":\"test.run\",\"artifact_schema_version\":1,\"status\":\"running\"}";

            Assert.That(BuildTestAutomationCommands.NormalizePersistedTestRunArtifactForRead(current, "run", "artifact"), Is.EqualTo(current));
            Assert.That(BuildTestAutomationCommands.NormalizePersistedTestRunArtifactForRead(activeLegacy, "run", "artifact"), Is.EqualTo(activeLegacy));
        }

        [Test]
        public void TestRunStatusAliasesMustIdentifyTheSameOperation()
        {
            Assert.That(BuildTestAutomationCommands.TestRunIdsMatch("test-20260716-120000-deadbeef", "test-20260716-120000-deadbeef"), Is.True);
            Assert.That(BuildTestAutomationCommands.TestRunIdsMatch("test-20260716-120000-deadbeef", string.Empty), Is.True);
            Assert.That(BuildTestAutomationCommands.TestRunIdsMatch("test-20260716-120000-deadbeef", "test-20260716-120000-bad0cafe"), Is.False);
        }

        [TestCase("test-20260716-120000-deadbeef")]
        [TestCase("run\"with\\escaped\nidentity")]
        public void MissingTestRunStatusKeepsExactIdentityAndUnresolvedNoRepeatGuidance(string operationId)
        {
            const string artifactPath = "Library/SGGUnityMcp/job-artifacts/test-runs/exact.json";
            var details = BuildTestAutomationCommands.TestRunMissingDetailsJson(operationId, artifactPath);

            Assert.That(JsonUtil.ExtractString(details, "operation_id"), Is.EqualTo(operationId));
            Assert.That(JsonUtil.ExtractString(details, "artifact_path"), Is.EqualTo(artifactPath));
            Assert.That(details, Does.Contain("\"test_run_delivery_state\":\"unknown\""));
            Assert.That(details, Does.Contain("\"repeat_blocked_until_delivery_state_checked\":true"));
            Assert.That(details, Does.Contain("\"write_replay_allowed\":false"));
            Assert.That(details, Does.Contain("Missing status is not proof of non-execution"));
            Assert.That(details, Does.Contain("do_not_repeat_test.run"));
            Assert.That(details, Does.Not.Contain("start a new test.run"));
            Assert.That(details, Does.Not.Contain("\"test.run.cancel\""));
        }

        [Test]
        public void CurrentTestRunArtifactsRequireBothMatchingOperationAliases()
        {
            const string runId = "test-20260716-120000-deadbeef";
            const string matching = "{\"artifact_schema_version\":2,\"operation_id\":\"test-20260716-120000-deadbeef\",\"run_id\":\"test-20260716-120000-deadbeef\"}";
            const string conflicting = "{\"artifact_schema_version\":2,\"operation_id\":\"test-20260716-120000-deadbeef\",\"run_id\":\"test-20260716-120000-bad0cafe\"}";
            const string missingAlias = "{\"artifact_schema_version\":2,\"operation_id\":\"test-20260716-120000-deadbeef\"}";

            Assert.That(BuildTestAutomationCommands.PersistedTestRunArtifactIdentityMatches(matching, runId), Is.True);
            Assert.That(BuildTestAutomationCommands.PersistedTestRunArtifactIdentityMatches(conflicting, runId), Is.False);
            Assert.That(BuildTestAutomationCommands.PersistedTestRunArtifactIdentityMatches(missingAlias, runId), Is.False);
        }

        [Test]
        public void SchemaV3CancelTransitionPreservesOnlyValidatedOperationHashes()
        {
            const string runId = "test-20260716-120000-deadbeef";
            const string artifactPath = "Library/SGGUnityMcp/job-artifacts/test-runs/test-20260716-120000-deadbeef.json";
            const string valid = "{\"command\":\"test.run\",\"artifact_schema_version\":3,\"args_hash\":\"" + Revision + "\",\"caller_args_hash\":\"" + Revision + "\",\"operation_id\":\"" + runId + "\",\"run_id\":\"" + runId + "\",\"artifact_path\":\"" + artifactPath + "\"}";

            Assert.That(BuildTestAutomationCommands.TryValidateTestRunArtifactIdentityForV3Transition(valid, runId, artifactPath, out var argsHash, out var callerArgsHash, out var failureReason), Is.True);
            Assert.That(argsHash, Is.EqualTo(Revision));
            Assert.That(callerArgsHash, Is.EqualTo(Revision));
            Assert.That(failureReason, Is.Empty);

            var schemaV2 = valid.Replace("\"artifact_schema_version\":3", "\"artifact_schema_version\":2");
            Assert.That(BuildTestAutomationCommands.TryValidateTestRunArtifactIdentityForV3Transition(schemaV2, runId, artifactPath, out argsHash, out callerArgsHash, out failureReason), Is.False);
            Assert.That(argsHash, Is.Empty);
            Assert.That(callerArgsHash, Is.Empty);
            Assert.That(failureReason, Is.EqualTo("artifact_identity_mismatch"));

            var missingCallerHash = valid.Replace("\"caller_args_hash\":\"" + Revision + "\",", string.Empty);
            Assert.That(BuildTestAutomationCommands.TryValidateTestRunArtifactIdentityForV3Transition(missingCallerHash, runId, artifactPath, out _, out _, out failureReason), Is.False);
            Assert.That(failureReason, Is.EqualTo("artifact_identity_mismatch"));

            var conflictingRunAlias = valid.Replace("\"run_id\":\"" + runId + "\"", "\"run_id\":\"test-20260716-120000-bad0cafe\"");
            Assert.That(BuildTestAutomationCommands.TryValidateTestRunArtifactIdentityForV3Transition(conflictingRunAlias, runId, artifactPath, out _, out _, out failureReason), Is.False);
            Assert.That(failureReason, Is.EqualTo("artifact_identity_mismatch"));

            Assert.That(BuildTestAutomationCommands.TryValidateTestRunArtifactIdentityForV3Transition(valid, runId, artifactPath + ".other", out _, out _, out failureReason), Is.False);
            Assert.That(failureReason, Is.EqualTo("artifact_identity_mismatch"));
        }

        [Test]
        public void TestRunArtifactPublicationRetriesOnlyKnownWindowsContention()
        {
            Assert.That(BuildTestAutomationCommands.IsTransientWindowsArtifactCommitError(5), Is.True);
            Assert.That(BuildTestAutomationCommands.IsTransientWindowsArtifactCommitError(32), Is.True);
            Assert.That(BuildTestAutomationCommands.IsTransientWindowsArtifactCommitError(33), Is.True);
            Assert.That(BuildTestAutomationCommands.IsTransientWindowsArtifactCommitError(1175), Is.True);
            Assert.That(BuildTestAutomationCommands.IsTransientWindowsArtifactCommitError(2), Is.False);
            Assert.That(BuildTestAutomationCommands.IsTransientWindowsArtifactCommitError(87), Is.False);
            Assert.That(BuildTestAutomationCommands.ClassifyWindowsArtifactCommitError(32), Is.EqualTo(BuildTestAutomationCommands.ArtifactPublicationDisposition.TransientContention));
            Assert.That(BuildTestAutomationCommands.ClassifyWindowsArtifactCommitError(87), Is.EqualTo(BuildTestAutomationCommands.ArtifactPublicationDisposition.PermanentFailure));
        }

        [Test]
        public void BuildRunReceiptClassifierRequiresConsistentAuthoritativeSucceededEvidence()
        {
            var succeeded = BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Succeeded", 0, true);
            Assert.That(succeeded.Success, Is.True);
            Assert.That(succeeded.ReceiptConsistency, Is.EqualTo("consistent"));
            Assert.That(succeeded.ReceiptConflicts, Is.Empty);

            var oneError = BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Succeeded", 1, true);
            Assert.That(oneError.Success, Is.False);
            Assert.That(oneError.ReceiptConsistencyConflict, Is.EqualTo("nonzero_total_errors"));
            CollectionAssert.AreEqual(new[] { "nonzero_total_errors" }, oneError.ReceiptConflicts);

            var manyErrors = BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Succeeded", 14, true);
            Assert.That(manyErrors.Success, Is.False);
            Assert.That(manyErrors.ReceiptConsistencyConflict, Is.EqualTo("nonzero_total_errors"));

            var outputMissing = BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Succeeded", 0, false);
            Assert.That(outputMissing.Success, Is.False);
            CollectionAssert.AreEqual(new[] { "output_missing" }, outputMissing.ReceiptConflicts);

            var both = BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Succeeded", 2, false);
            Assert.That(both.ReceiptConsistencyConflict, Is.EqualTo("nonzero_total_errors_and_output_missing"));
            CollectionAssert.AreEqual(new[] { "nonzero_total_errors", "output_missing" }, both.ReceiptConflicts);

            Assert.That(BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Failed", 1, true).Success, Is.False);
            Assert.That(BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Cancelled", 0, true).Success, Is.False);
            Assert.That(BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", true, "Unknown", 0, true).Success, Is.False);

            var unavailable = BuildTestAutomationCommands.ClassifyBuildRunReceipt("completed", false, "Unknown", 0, false);
            Assert.That(unavailable.Success, Is.False);
            Assert.That(unavailable.ReceiptConsistencyConflict, Is.EqualTo("terminal_evidence_unavailable"));
        }

        [Test]
        public void BuildRunArtifactsRequireAnExactSafeOperationId()
        {
            const string operationId = "build-20260727-120000-deadbeef";
            const string matching = "{\"artifact_schema_version\":2,\"operation_id\":\"build-20260727-120000-deadbeef\",\"status\":\"running\"}";
            const string conflicting = "{\"artifact_schema_version\":2,\"operation_id\":\"build-20260727-120000-bad0cafe\",\"status\":\"completed\"}";

            Assert.That(BuildTestAutomationCommands.IsSafeBuildRunId(operationId), Is.True);
            Assert.That(BuildTestAutomationCommands.IsSafeBuildRunId("../outside"), Is.False);
            Assert.That(BuildTestAutomationCommands.PersistedBuildRunArtifactIdentityMatches(matching, operationId), Is.True);
            Assert.That(BuildTestAutomationCommands.PersistedBuildRunArtifactIdentityMatches(conflicting, operationId), Is.False);
            var normalized = BuildTestAutomationCommands.NormalizePersistedBuildRunArtifactForRead(
                "{\"artifact_schema_version\":2,\"operation_id\":\"build-20260727-120000-deadbeef\",\"status\":\"running\",\"result\":\"running\",\"artifact_path\":\"Library/SGGUnityMcp/job-artifacts/build-runs/build-20260727-120000-deadbeef.json\",\"build_output_path\":\"BuildArtifacts/SGGUnityMcp/build-20260727-120000-deadbeef/Game.exe\"}",
                operationId,
                "Library/SGGUnityMcp/job-artifacts/build-runs/build-20260727-120000-deadbeef.json");
            Assert.That(normalized, Does.Contain("\"operation_id\":\"" + operationId + "\""));
            Assert.That(normalized, Does.Contain("\"status\":\"running\""));
            Assert.That(BuildTestAutomationCommands.NormalizePersistedBuildRunArtifactForRead(matching, operationId, "artifact.json"), Is.Empty);
            Assert.That(BuildTestAutomationCommands.NormalizePersistedBuildRunArtifactForRead(
                "{\"artifact_schema_version\":2,\"operation_id\":\"" + operationId + "\",\"status\":\"running\",\"result\":\"running\",\"artifact_path\":\"artifact.json\",\"build_output_path\":\"BuildArtifacts/SGGUnityMcp/" + operationId + "/../escape.exe\"}",
                operationId,
                "artifact.json"), Is.Empty);
        }

        [Test]
        public void BatchmodeFallbackArgsPreserveExactAssemblyFilterAndModeTuple()
        {
            var args = BuildTestAutomationCommands.TestRunBatchmodeUnityArgs("editmode", "Fixture.Parameterized", "SGG.UnityMCP.Editor.Tests", "Library/result.xml", "Library/result.log");

            Assert.That(args[args.IndexOf("-testPlatform") + 1], Is.EqualTo("EditMode"));
            Assert.That(args[args.IndexOf("-assemblyNames") + 1], Is.EqualTo("SGG.UnityMCP.Editor.Tests"));
            Assert.That(args[args.IndexOf("-testFilter") + 1], Is.EqualTo("Fixture.Parameterized"));
        }

        [Test]
        public void LegacyFallbackWithoutKnownAssemblyIsUnavailableInsteadOfBroadening()
        {
            Assert.That(BuildTestAutomationCommands.PersistedTestRunAssembly("{\"artifact_schema_version\":1,\"status\":\"running\"}"), Is.Empty);
            Assert.That(BuildTestAutomationCommands.TestRunBatchmodeUnityArgs("editmode", "Fixture", string.Empty, "Library/result.xml", "Library/result.log"), Is.Empty);
            Assert.That(BuildTestAutomationCommands.PersistedTestRunAssembly("{\"test_assembly\":\"unsafe assembly\"}"), Is.Empty);
        }

        [TestCase("error", "CS1234")]
        [TestCase("warning", "CS5678")]
        public void CompileDiagnosticSerializerEmitsSafeSeverityAndCompilerCode(string severity, string code)
        {
            var json = ConsoleLogBuffer.CompileDiagnosticEntryJsonForTests(severity, code, "quoted \" message", "stack\ntrace", 8000, 12000);

            Assert.That(json, Does.Contain("\"severity\":\"" + severity + "\""));
            Assert.That(json, Does.Contain("\"code\":\"" + code + "\""));
            Assert.That(json, Does.Contain("\"source\":\"compilation_pipeline\""));
            Assert.That(json, Does.Contain("\"assembly_path\":\"Library/ScriptAssemblies/Game.dll\""));
            Assert.That(json, Does.Contain("\"file\":\"Assets/Test.cs\""));
            Assert.That(json, Does.Contain("\"line\":12"));
            Assert.That(json, Does.Contain("\"column\":3"));
            Assert.That(json, Does.Contain("\"message\":\"quoted \\\" message\""));
            Assert.That(json, Does.Contain("\"stack_trace\":\"stack\\ntrace\""));
        }

        [Test]
        public void CompileDiagnosticSerializerOmitsMalformedCodeAndBoundsEscapedText()
        {
            const string malformedCode = "CS1234\"AUTHORIZATION_SENTINEL";
            var json = ConsoleLogBuffer.CompileDiagnosticEntryJsonForTests("error", malformedCode, "A\"B\nMESSAGE_SENTINEL", "S\nSTACK_SENTINEL", 4, 3);

            Assert.That(json, Does.Contain("\"severity\":\"error\""));
            Assert.That(json, Does.Not.Contain("\"code\":"));
            Assert.That(json, Does.Not.Contain("AUTHORIZATION_SENTINEL"));
            Assert.That(json, Does.Contain("\"message\":\"A\\\"B\\n\""));
            Assert.That(json, Does.Contain("\"stack_trace\":\"S\\nS\""));
            Assert.That(json, Does.Not.Contain("MESSAGE_SENTINEL"));
            Assert.That(json, Does.Not.Contain("STACK_SENTINEL"));
        }

        [Test]
        public void ConsoleBufferMetadataDistinguishesFirstObservationFromDomainReloadLoss()
        {
            var firstEpoch = new string('a', 32);
            var nextEpoch = new string('b', 32);
            const string initializedAtUtc = "2026-08-27T12:34:56.0000000Z";
            var marker = ConsoleLogBuffer.BufferMetadataMarkerForTests(firstEpoch);

            var first = ConsoleLogBuffer.BufferMetadataJsonForTests(string.Empty, firstEpoch, initializedAtUtc);
            var reloaded = ConsoleLogBuffer.BufferMetadataJsonForTests(marker, nextEpoch, initializedAtUtc);
            var malformed = ConsoleLogBuffer.BufferMetadataJsonForTests("v1|broken", nextEpoch, initializedAtUtc);

            Assert.That(first, Does.Contain("\"epoch\":\"" + firstEpoch + "\""));
            Assert.That(first, Does.Contain("\"initialized_at_utc\":\"" + initializedAtUtc + "\""));
            Assert.That(first, Does.Contain("\"reset_loss\":false"));
            Assert.That(first, Does.Contain("\"history_complete\":false"));
            Assert.That(reloaded, Does.Contain("\"epoch\":\"" + nextEpoch + "\""));
            Assert.That(reloaded, Does.Contain("\"reset_loss\":true"));
            Assert.That(malformed, Does.Contain("\"reset_loss\":false"));
        }

        [Test]
        public void RedactDiagnosticsTextRemovesHeaderAndTextSecrets()
        {
            const string authorizationSecret = "AUTH_SENTINEL_8B";
            const string nonBearerAuthorizationSecret = "AUTH_RAW_SENTINEL_8B";
            const string tokenSecret = "TOKEN_SENTINEL_8B";
            const string apiKeySecret = "APIKEY_SENTINEL_8B";
            const string passwordSecret = "PASSWORD_SENTINEL_8B";
            const string secretSecret = "SECRET_SENTINEL_8B";
            const string cookieSessionSecret = "COOKIE_SESSION_SENTINEL_8B";
            const string cookieRefreshSecret = "COOKIE_REFRESH_SENTINEL_8B";
            var input = "healthy gateway mode=local\n"
                + "WARN Authorization: Bearer " + authorizationSecret + "\n"
                + "INFO authorization=\"" + nonBearerAuthorizationSecret + "\" request=healthy\n"
                + "token=Bearer " + tokenSecret + "\n"
                + "api-key: Bearer " + apiKeySecret + "\n"
                + "password=" + passwordSecret + "\n"
                + "secret: " + secretSecret + "\n"
                + "WARN Cookie: session=" + cookieSessionSecret + "; refresh=" + cookieRefreshSecret + "\n"
                + "unrelated=value remains visible";

            var result = McpDiagnosticsCopyViewModel.Redact(input);

            Assert.That(result, Does.Contain("healthy gateway mode=local"));
            Assert.That(result, Does.Contain("request=healthy"));
            Assert.That(result, Does.Contain("unrelated=value remains visible"));
            Assert.That(result, Does.Not.Contain("token=Bearer"));
            Assert.That(result, Does.Not.Contain(authorizationSecret));
            Assert.That(result, Does.Not.Contain(nonBearerAuthorizationSecret));
            Assert.That(result, Does.Not.Contain(tokenSecret));
            Assert.That(result, Does.Not.Contain(apiKeySecret));
            Assert.That(result, Does.Not.Contain(passwordSecret));
            Assert.That(result, Does.Not.Contain(secretSecret));
            Assert.That(result, Does.Not.Contain(cookieSessionSecret));
            Assert.That(result, Does.Not.Contain(cookieRefreshSecret));
        }

        [Test]
        public void RedactDiagnosticsTextRemovesJsonSecretsAndPreservesBenignFields()
        {
            const string authorizationSecret = "JSON_AUTH_SENTINEL_8B";
            const string tokenSecret = "JSON_TOKEN_SENTINEL_8B";
            const string apiKeySecret = "JSON_APIKEY_SENTINEL_8B";
            const string passwordSecret = "JSON_PASSWORD_SENTINEL_8B";
            const string secretSecret = "JSON_SECRET_SENTINEL_8B";
            const string cookieSecret = "JSON_COOKIE_SENTINEL_8B";
            var input = "{\"status\":\"healthy\",\"project\":\"sample\",\"authorization\":\"Bearer " + authorizationSecret
                + "\",\"token\":\"Bearer " + tokenSecret + "\",\"api_key\":\"" + apiKeySecret
                + "\",\"password\":\"" + passwordSecret + "\",\"secret\":\"" + secretSecret
                + "\",\"cookie\":\"session=" + cookieSecret + "\"}";

            var result = McpDiagnosticsCopyViewModel.Redact(input);

            Assert.That(result, Does.Not.Contain(authorizationSecret));
            Assert.That(result, Does.Not.Contain(tokenSecret));
            Assert.That(result, Does.Not.Contain(apiKeySecret));
            Assert.That(result, Does.Not.Contain(passwordSecret));
            Assert.That(result, Does.Not.Contain(secretSecret));
            Assert.That(result, Does.Not.Contain(cookieSecret));
            Assert.That(result, Does.Contain("\"status\":\"healthy\""));
            Assert.That(result, Does.Contain("\"project\":\"sample\""));
        }

        [TestCase(null, "Not configured")]
        [TestCase("", "Not configured")]
        [TestCase("TOKEN_STATUS_SENTINEL_8B", "Present")]
        public void FormatTokenPresenceReturnsOnlyPresenceState(string token, string expected)
        {
            var result = UnityMcpSetupWindow.FormatTokenPresence(token);

            Assert.That(result, Is.EqualTo(expected));
            Assert.That(result, Does.Not.Contain("TOKEN_STATUS_SENTINEL_8B"));
        }

        [Test]
        public void RuntimePolicySummaryReportsUnavailableHealthWithoutPrivateStatusData()
        {
            var status = StatusWithPrivateData();

            var result = McpRuntimePolicyViewModel.BuildSummary(status);

            Assert.That(result, Is.EqualTo("Unavailable until Refresh Health succeeds."));
            AssertNoPrivateStatusData(result);
        }

        [Test]
        public void RuntimePolicySummaryRejectsMalformedMetadataSafely()
        {
            var status = StatusWithPrivateData();
            status.HealthProbed = true;
            status.HealthOk = true;
            status.PolicyMetadataMalformed = true;

            var result = McpRuntimePolicyViewModel.BuildSummary(status);

            Assert.That(result, Does.Contain("malformed policy metadata was rejected safely"));
            Assert.That(result, Does.Contain("Refresh Health"));
            AssertNoPrivateStatusData(result);
        }

        [Test]
        public void RuntimePolicySummarySupportsOlderHealthWithoutMetadata()
        {
            var status = StatusWithPrivateData();
            status.HealthProbed = true;
            status.HealthOk = true;

            var result = McpRuntimePolicyViewModel.BuildSummary(status);

            Assert.That(result, Is.EqualTo("Unavailable from this older Gateway health contract."));
            AssertNoPrivateStatusData(result);
        }

        [Test]
        public void RuntimePolicySummaryReportsActiveConfiguredPolicyAndPublicRevision()
        {
            var status = ActiveStatus();

            var result = McpRuntimePolicyViewModel.BuildSummary(status);

            Assert.That(result, Does.Contain("state=active, configured=yes, degraded=no"));
            Assert.That(result, Does.Contain("revision=" + Revision));
            AssertNoPrivateStatusData(result);
        }

        [Test]
        public void RuntimePolicySummaryReportsDegradedLastKnownGoodGuidance()
        {
            var status = ActiveStatus();
            status.PolicyState = "degraded";
            status.PolicyDegraded = true;
            status.PolicyLastErrorCode = "policy_invalid";
            status.PolicyLastErrorKind = "invalid_schema";

            var result = McpRuntimePolicyViewModel.BuildSummary(status);

            Assert.That(result, Does.Contain("Risky commands fail closed"));
            Assert.That(result, Does.Contain("last-known-good policy"));
            Assert.That(result, Does.Contain("No blocked command is replayed automatically"));
            Assert.That(result, Does.Contain("Do not restart for a policy-only recovery"));
            AssertNoPrivateStatusData(result);
        }

        [TestCase("SCENE", "all")]
        [TestCase("Hierarchy", "all")]
        [TestCase("OBJECTS", "all")]
        [TestCase("", "read")]
        public void MatchesCommandSearchAndRiskFiltersAcceptsIdTitleDescriptionAndExactRisk(string search, string risk)
        {
            Assert.That(UnityMcpSetupWindow.MatchesCommandSearchAndRiskFilters(Descriptor(), risk, search), Is.True);
        }

        [TestCase("missing", "all")]
        [TestCase("", "write")]
        public void MatchesCommandSearchAndRiskFiltersRejectsNonMatches(string search, string risk)
        {
            Assert.That(UnityMcpSetupWindow.MatchesCommandSearchAndRiskFilters(Descriptor(), risk, search), Is.False);
        }

        [TestCase("all")]
        [TestCase("scene")]
        [TestCase("semantic")]
        [TestCase("semantic_inspection")]
        [TestCase("inspection")]
        public void MatchesCommandGroupFilterAcceptsAllIdCapabilityPrefixExactAndTag(string group)
        {
            Assert.That(UnityMcpSetupWindow.MatchesCommandGroupFilter(Descriptor(), group), Is.True);
        }

        [Test]
        public void MatchesCommandGroupFilterRejectsNonMatch()
        {
            Assert.That(UnityMcpSetupWindow.MatchesCommandGroupFilter(Descriptor(), "asset"), Is.False);
        }

        [TestCase("allow: exact", "allow")]
        [TestCase("default ask for read", "ask")]
        [TestCase("deny: wildcard", "deny")]
        [TestCase("unavailable", "unknown")]
        public void PolicyActionBucketClassifiesPureDecisionText(string decision, string expected)
        {
            Assert.That(UnityMcpSetupWindow.PolicyActionBucket(decision), Is.EqualTo(expected));
        }

        static GatewayProcessManager.GatewayRuntimeStatus StatusWithPrivateData()
        {
            return new GatewayProcessManager.GatewayRuntimeStatus
            {
                HealthBody = "PRIVATE_CONTENT_SENTINEL_8B",
                LogPath = "C:/PRIVATE_PATH_SENTINEL_8B/policy.json",
                LogTail = "PRIVATE_HASH_SENTINEL_8B"
            };
        }

        static GatewayProcessManager.GatewayRuntimeStatus ActiveStatus()
        {
            var status = StatusWithPrivateData();
            status.HealthProbed = true;
            status.HealthOk = true;
            status.PolicyMetadataAvailable = true;
            status.PolicyConfigured = true;
            status.PolicyState = "active";
            status.PolicyRevision = Revision;
            return status;
        }

        static void AssertNoPrivateStatusData(string result)
        {
            Assert.That(result, Does.Not.Contain("PRIVATE_CONTENT_SENTINEL_8B"));
            Assert.That(result, Does.Not.Contain("PRIVATE_PATH_SENTINEL_8B"));
            Assert.That(result, Does.Not.Contain("PRIVATE_HASH_SENTINEL_8B"));
        }

        static CommandDescriptor Descriptor()
        {
            return new CommandDescriptor
            {
                Id = "scene.hierarchy",
                Title = "Hierarchy Reader",
                Description = "Lists scene objects",
                Risk = "read",
                Capability = "semantic_inspection",
                Tags = new[] { "inspection" }
            };
        }

        sealed class SerializedVectorFixture : UnityEngine.ScriptableObject
        {
            public UnityEngine.Vector2 vector2 = new UnityEngine.Vector2();
            public UnityEngine.Vector4 vector4 = new UnityEngine.Vector4();
        }
    }
}
