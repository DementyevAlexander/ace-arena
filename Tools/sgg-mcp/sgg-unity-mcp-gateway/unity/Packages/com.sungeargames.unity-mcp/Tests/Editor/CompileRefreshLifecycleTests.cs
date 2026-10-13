using System;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class CompileRefreshLifecycleTests
    {
        const string OperationId = "0123456789abcdef0123456789abcdef";

        [Test]
        public void IdleEditorAllowsRefreshPreflight()
        {
            Assert.That(BuiltInCommands.CompileRefreshBusyPreflight(false, false, false), Is.Null);
        }

        [TestCase(true, false, false, "compilation_active")]
        [TestCase(false, true, false, "asset_update_active")]
        [TestCase(false, false, true, "play_mode_active_or_transitioning")]
        public void BusyEditorRejectsRefreshBeforeSideEffects(bool isCompiling, bool isUpdating, bool isPlaying, string reason)
        {
            var result = BuiltInCommands.CompileRefreshBusyPreflight(isCompiling, isUpdating, isPlaying);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.ErrorCode, Is.EqualTo("unity_busy"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"" + reason + "\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"refresh_started\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"compile_barrier_created\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"script_compilation_requested\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"retryable\":true"));
        }

        [Test]
        public void BusyPriorityIsDeterministic()
        {
            var result = BuiltInCommands.CompileRefreshBusyPreflight(true, true, true);

            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"compilation_active\""));
        }

        [Test]
        public void CompilationFallbackRequiresMatchingArmedPendingBarrier()
        {
            var armed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var pending = new ExplicitCompilationBarrierSnapshot("pending", armed, string.Empty, true, false, 0);
            var failedState = armed.FailForCallback("callback_context_unavailable");
            var failed = new ExplicitCompilationBarrierSnapshot("failed", failedState, failedState.Failure, true, false, 0);

            Assert.That(BuiltInCommands.IsMatchingArmedCompileBarrier(pending, OperationId), Is.True);
            Assert.That(BuiltInCommands.ShouldRequestCompilationAfterRefresh(pending, OperationId, false), Is.True);
            Assert.That(BuiltInCommands.ShouldRequestCompilationAfterRefresh(pending, OperationId, true), Is.False);
            Assert.That(BuiltInCommands.IsMatchingArmedCompileBarrier(failed, OperationId), Is.False);
            Assert.That(BuiltInCommands.ShouldRequestCompilationAfterRefresh(failed, OperationId, false), Is.False);
            Assert.That(BuiltInCommands.ShouldRequestCompilationAfterRefresh(pending, "fedcba9876543210fedcba9876543210", false), Is.False);
        }

        [TestCase(false, false, true)]
        [TestCase(true, false, false)]
        [TestCase(false, true, false)]
        public void TestRunAdmissionRequiresSettledEditorAndPackageLifecycle(bool isUpdating, bool packageRefreshRequired, bool expected)
        {
            var barrier = new ExplicitCompilationBarrierSnapshot("not_required", null, string.Empty, false, false, 0);

            Assert.That(BuildTestAutomationCommands.IsTestRunCompilationSettled(barrier, isUpdating, packageRefreshRequired), Is.EqualTo(expected));
        }

        [Test]
        public void NewPackageFingerprintInvalidatesAnEarlierCleanTestAdmission()
        {
            var barrier = new ExplicitCompilationBarrierSnapshot("not_required", null, string.Empty, false, false, 0);

            Assert.That(BuildTestAutomationCommands.IsTestRunCompilationSettled(barrier, false, false), Is.True);
            Assert.That(BuildTestAutomationCommands.IsTestRunCompilationSettled(barrier, false, true), Is.False);
        }

        [Test]
        public void BackgroundCompilerProgressParticipatesInTestRunAdmission()
        {
            var barrier = new ExplicitCompilationBarrierSnapshot("not_required", null, string.Empty, false, false, 0);

            Assert.That(BuildTestAutomationCommands.IsTestRunCompilationSettled(barrier, false, false, false), Is.True);
            Assert.That(BuildTestAutomationCommands.IsTestRunCompilationSettled(barrier, false, false, true), Is.False);
        }

        [Test]
        public void ActiveBackgroundComputeCompilationKeepsLifecycleBusy()
        {
            var state = ReadyState();
            state.SetBackgroundCompilerProgressForTests(BackgroundCompilerProgress.ForTests(true, true, false, "active", "compute_variant_compile"));

            Assert.That(state.IsReady, Is.False);
            Assert.That(state.Status, Is.EqualTo("busy"));
            Assert.That(state.BackgroundCompilerProgressJson(), Does.Contain("\"state\":\"active\""));
            Assert.That(state.BackgroundCompilerProgressJson(), Does.Contain("\"blocks_readiness\":true"));
            Assert.That(state.BackgroundCompilerProgressJson(), Does.Contain("\"cancellation_attempted\":false"));
        }

        [TestCase(false, false, "background_compilation_unavailable")]
        [TestCase(true, true, "background_compilation_failed")]
        public void TestRunIsNotDispatchedWithoutAuthoritativeBackgroundCompletion(bool authoritative, bool failed, string reason)
        {
            var state = ReadyState();
            state.SetBackgroundCompilerProgressForTests(BackgroundCompilerProgress.ForTests(authoritative, false, failed, failed ? "failed" : "unavailable", failed ? "compute_variant_compile" : "unknown"));

            var result = BuildTestAutomationCommands.RequireSettledCompilationForTestRun(state);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"" + reason + "\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"command_dispatched\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"test_runner_execute_invoked\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"write_replay_allowed\":false"));
        }

        [Test]
        public void BackgroundCompilerProgressRequiresASettledAuthoritativeClearObservation()
        {
            BackgroundCompilerProgress.ResetForTests();
            try
            {
                var started = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
                var active = BackgroundCompilerProgress.ObserveForTests(
                    true,
                    new[] { BackgroundCompilerProgress.ProgressItemForTests(7, "Compiling compute shader variants", "Running", 250) },
                    started,
                    12,
                    8,
                    7,
                    "sha256:package-a");
                var settling = BackgroundCompilerProgress.ObserveForTests(true, new BackgroundCompilerProgress.ProgressItem[0], started.AddMilliseconds(100), 12, 8, 8, "sha256:package-a");
                var complete = BackgroundCompilerProgress.ObserveForTests(true, new BackgroundCompilerProgress.ProgressItem[0], started.AddMilliseconds(600), 12, 8, 8, "sha256:package-a");

                Assert.That(active.State, Is.EqualTo("active"));
                Assert.That(active.Category, Is.EqualTo("compute_variant_compile"));
                Assert.That(active.BlocksReadiness, Is.True);
                Assert.That(settling.State, Is.EqualTo("settling"));
                Assert.That(settling.BlocksReadiness, Is.True);
                Assert.That(complete.State, Is.EqualTo("complete"));
                Assert.That(complete.BlocksReadiness, Is.False);
                Assert.That(complete.CompilationGeneration, Is.EqualTo(8));
                Assert.That(complete.CompletedCompilationGeneration, Is.EqualTo(8));
                Assert.That(complete.PackageFingerprint, Is.EqualTo("sha256:package-a"));
            }
            finally
            {
                BackgroundCompilerProgress.ResetForTests();
            }
        }

        [Test]
        public void BackgroundCompilerProgressFailsClosedButIgnoresUnrelatedProgress()
        {
            BackgroundCompilerProgress.ResetForTests();
            try
            {
                var observedAt = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
                var unrelated = BackgroundCompilerProgress.ObserveForTests(
                    true,
                    new[] { BackgroundCompilerProgress.ProgressItemForTests(1, "Importing texture", "Running", 500) },
                    observedAt,
                    3,
                    2,
                    2,
                    "sha256:package");
                var settledUnrelated = BackgroundCompilerProgress.ObserveForTests(
                    true,
                    new[] { BackgroundCompilerProgress.ProgressItemForTests(1, "Importing texture", "Running", 500) },
                    observedAt.AddMilliseconds(600),
                    3,
                    2,
                    2,
                    "sha256:package");
                BackgroundCompilerProgress.ObserveForTests(
                    true,
                    new[] { BackgroundCompilerProgress.ProgressItemForTests(2, "Shader compiler", "Running", 800) },
                    observedAt.AddMilliseconds(601),
                    3,
                    2,
                    2,
                    "sha256:package");
                var failed = BackgroundCompilerProgress.ObserveForTests(
                    true,
                    new[] { BackgroundCompilerProgress.ProgressItemForTests(2, "Shader compiler", "Failed", 900) },
                    observedAt.AddMilliseconds(602),
                    3,
                    2,
                    2,
                    "sha256:package");
                var unavailable = BackgroundCompilerProgress.ObserveForTests(false, null, observedAt.AddMilliseconds(603), 3, 2, 2, "sha256:package");

                Assert.That(unrelated.State, Is.EqualTo("settling"));
                Assert.That(unrelated.RelevantItemCount, Is.Zero);
                Assert.That(unrelated.BlocksReadiness, Is.True);
                Assert.That(settledUnrelated.State, Is.EqualTo("complete"));
                Assert.That(settledUnrelated.RelevantItemCount, Is.Zero);
                Assert.That(settledUnrelated.BlocksReadiness, Is.False);
                Assert.That(failed.State, Is.EqualTo("failed"));
                Assert.That(failed.Failed, Is.True);
                Assert.That(failed.BlocksReadiness, Is.True);
                Assert.That(unavailable.State, Is.EqualTo("unavailable"));
                Assert.That(unavailable.Authoritative, Is.False);
                Assert.That(unavailable.BlocksReadiness, Is.True);
            }
            finally
            {
                BackgroundCompilerProgress.ResetForTests();
            }
        }

        [Test]
        public void CorrelatedNativeFailureRemainsVisibleWhileOtherComputeWorkContinues()
        {
            BackgroundCompilerProgress.ResetForTests();
            try
            {
                var started = new DateTime(2026, 9, 23, 9, 0, 0, DateTimeKind.Utc);
                BackgroundCompilerProgress.ObserveForTests(true, new[]
                {
                    BackgroundCompilerProgress.ProgressItemForTests(7, "Shader compiler", "Running", 300),
                    BackgroundCompilerProgress.ProgressItemForTests(8, "Compiling compute shader variants", "Running", 500),
                }, started, 12, 8, 8, "sha256:package-a");
                var partialFailure = BackgroundCompilerProgress.ObserveForTests(true, new[]
                {
                    BackgroundCompilerProgress.ProgressItemForTests(7, "Shader compiler", "Failed", 310),
                    BackgroundCompilerProgress.ProgressItemForTests(8, "Compiling compute shader variants", "Running", 800),
                }, started.AddMilliseconds(100), 12, 8, 8, "sha256:package-a");
                var drift = BackgroundCompilerProgress.ObserveForTests(true, new[]
                {
                    BackgroundCompilerProgress.ProgressItemForTests(8, "Compiling compute shader variants", "Running", 850),
                }, started.AddMilliseconds(200), 13, 8, 8, "sha256:package-a");

                Assert.That(partialFailure.State, Is.EqualTo("active"));
                Assert.That(partialFailure.Failed, Is.False);
                Assert.That(partialFailure.CorrelatedNativeFailureObserved, Is.True);
                Assert.That(partialFailure.NativeFailureCategory, Is.EqualTo("shader_variant_compile"));
                Assert.That(partialFailure.ToJson(), Does.Contain("\"native_failure_absence_authoritative\":false"));
                Assert.That(drift.CorrelatedNativeFailureObserved, Is.False);
            }
            finally
            {
                BackgroundCompilerProgress.ResetForTests();
            }
        }

        [Test]
        public void BackgroundCompilerProgressRebindsAcrossCatalogAndPackageGenerationDrift()
        {
            BackgroundCompilerProgress.ResetForTests();
            try
            {
                var observedAt = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Utc);
                BackgroundCompilerProgress.ObserveForTests(
                    true,
                    new[] { BackgroundCompilerProgress.ProgressItemForTests(9, "Native compiler", "Running", 100) },
                    observedAt,
                    4,
                    5,
                    4,
                    "sha256:old");
                var rebound = BackgroundCompilerProgress.ObserveForTests(
                    true,
                    new[] { BackgroundCompilerProgress.ProgressItemForTests(10, "Native compiler", "Running", 200) },
                    observedAt.AddMilliseconds(1),
                    5,
                    6,
                    5,
                    "sha256:new");

                Assert.That(rebound.State, Is.EqualTo("active"));
                Assert.That(rebound.Category, Is.EqualTo("native_compiler"));
                Assert.That(rebound.CatalogGeneration, Is.EqualTo(5));
                Assert.That(rebound.CompilationGeneration, Is.EqualTo(6));
                Assert.That(rebound.CompletedCompilationGeneration, Is.EqualTo(5));
                Assert.That(rebound.PackageFingerprint, Is.EqualTo("sha256:new"));
                Assert.That(rebound.ToJson(), Does.Contain("\"work_replayed\":false"));
            }
            finally
            {
                BackgroundCompilerProgress.ResetForTests();
            }
        }

        [Test]
        public void PackagePendingRefusalCarriesBoundedTransitionAndInventoryProvenance()
        {
            var state = new BridgeRuntimeState();
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Available(1, 2, 2, 0, 0, string.Empty, "source", "asset-database", "source-state"));
            state.SetPackageUpdateStatus("current", "Current.", false, "sha256:current", string.Empty, 6, "2026-09-01T10:00:00Z");
            state.SetPackageUpdateStatus("package_refresh_required", "Changed.", true, "sha256:current", "sha256:pending", 6, "2026-09-01T10:00:00Z");

            var result = BuildTestAutomationCommands.RequireSettledCompilationForTestRun(state);

            Assert.That(result, Is.Not.Null);
            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.ErrorCode, Is.EqualTo("package_refresh_required"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"package_refresh_required\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"command_dispatched\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"test_runner_execute_invoked\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"fingerprint\":\"sha256:current\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"pending_fingerprint\":\"sha256:pending\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"last_transition_utc\":"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"local_package_code_inventory\":{\"status\":\"current\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"refresh_wait_can_advance\":false"));
            Assert.That(result.ErrorDetailsJson, Does.EndWith("\"next_valid_actions\":[\"compile.status\",\"run_compile.refresh_and_wait_only_as_a_new_explicit_action\",\"do_not_auto_repeat_test.run\"]}"));
            Assert.That(result.ErrorDetailsJson, Does.Not.Contain("projectPath"));
        }

        [Test]
        public void TestRunDefersWhilePackageValidationCanStillAdvance()
        {
            var state = ReadyState();
            state.SetPackageUpdateStatus("package_refresh_required", "Scanning.", true, "sha256:current", string.Empty, 1, string.Empty, refreshWaitCanAdvance: true);

            var result = BuildTestAutomationCommands.RequireSettledCompilationForTestRun(state);

            Assert.That(result.ErrorCode, Is.EqualTo("unity_reloading"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"package_refresh_pending\""));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"package_refresh_wait_can_advance\":true"));
            Assert.That(result.ErrorDetailsJson, Does.EndWith("\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\",\"do_not_auto_repeat_test.run\"]}"));
        }

        [Test]
        public void TestRunTransientBlockersOutrankTerminalPackageRefreshGuidance()
        {
            var barrier = new ExplicitCompilationBarrierSnapshot("not_required", null, string.Empty, false, false, 0);
            var active = BackgroundCompilerProgress.ForTests(true, true, false, "active", "compute_variant_compile");
            var failed = BackgroundCompilerProgress.ForTests(true, false, true, "failed", "compute_variant_compile");
            var unavailable = BackgroundCompilerProgress.ForTests(false, false, false, "unavailable", "unknown");

            Assert.That(BuildTestAutomationCommands.TestRunCompilationBlockReason(barrier, false, true, false, active), Is.EqualTo("background_compilation_pending"));
            Assert.That(BuildTestAutomationCommands.TestRunCompilationBlockReason(barrier, true, true, false, BackgroundCompilerProgressSnapshot.Unobserved), Is.EqualTo("asset_update_active"));
            Assert.That(BuildTestAutomationCommands.TestRunCompilationBlockReason(barrier, false, true, false, BackgroundCompilerProgressSnapshot.Unobserved), Is.EqualTo("package_refresh_required"));
            Assert.That(BuildTestAutomationCommands.TestRunCompilationBlockReason(barrier, false, true, true, BackgroundCompilerProgressSnapshot.Unobserved), Is.EqualTo("package_refresh_pending"));
            Assert.That(BuildTestAutomationCommands.TestRunCompilationBlockReason(barrier, false, true, false, failed), Is.EqualTo("background_compilation_failed"));
            Assert.That(BuildTestAutomationCommands.TestRunCompilationBlockReason(barrier, false, true, false, unavailable), Is.EqualTo("background_compilation_unavailable"));
        }

        [Test]
        public void ActiveBackgroundCompilationDefersTerminalPackageRefreshActions()
        {
            var state = ReadyState();
            state.SetPackageUpdateStatus("package_refresh_required", "Changed.", true, "sha256:current", "sha256:pending", 1, string.Empty);
            state.SetBackgroundCompilerProgressForTests(BackgroundCompilerProgress.ForTests(true, true, false, "active", "compute_variant_compile"));

            var result = BuildTestAutomationCommands.RequireSettledCompilationForTestRun(state);

            Assert.That(result.ErrorCode, Is.EqualTo("unity_reloading"));
            Assert.That(result.ErrorMessage, Is.EqualTo("test.run was not admitted while Unity background shader or compute compilation is active or settling"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"background_compilation_pending\""));
            Assert.That(result.ErrorDetailsJson, Does.EndWith("\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\",\"do_not_auto_repeat_test.run\"]}"));
        }

        [Test]
        public void TestRunCombinedBlockResultsKeepCodeMessageReasonAndActionsConsistent()
        {
            var stable = new ExplicitCompilationBarrierSnapshot("not_required", null, string.Empty, false, false, 0);
            var unavailableBarrier = new ExplicitCompilationBarrierSnapshot("failed", null, "snapshot_unavailable", true, false, 0);
            var unavailableProgress = BackgroundCompilerProgress.ForTests(false, false, false, "unavailable", "unknown");

            AssertTestRunBlockResult(
                BuildTestAutomationCommands.TestRunCompilationBlockResult(stable, false, true, false, unavailableProgress, "{}"),
                "background_compilation_unavailable",
                "test.run was not admitted because authoritative Unity background compiler progress is unavailable",
                "background_compilation_unavailable",
                false);
            AssertTestRunBlockResult(
                BuildTestAutomationCommands.TestRunCompilationBlockResult(unavailableBarrier, false, true, false, BackgroundCompilerProgressSnapshot.Unobserved, "{}"),
                "compile_freshness_unavailable",
                "test.run was not admitted because compilation lifecycle evidence is unavailable",
                "compile_freshness_unavailable",
                false);
            AssertTestRunBlockResult(
                BuildTestAutomationCommands.TestRunCompilationBlockResult(stable, true, true, false, BackgroundCompilerProgressSnapshot.Unobserved, "{}"),
                "unity_reloading",
                "test.run was not admitted while Unity is updating assets or packages",
                "asset_update_active",
                false);
            AssertTestRunBlockResult(
                BuildTestAutomationCommands.TestRunCompilationBlockResult(stable, false, true, false, BackgroundCompilerProgressSnapshot.Unobserved, "{}"),
                "package_refresh_required",
                "test.run was not admitted because package changes require a new explicit refresh",
                "package_refresh_required",
                true);
            AssertTestRunBlockResult(
                BuildTestAutomationCommands.TestRunCompilationBlockResult(stable, false, true, true, BackgroundCompilerProgressSnapshot.Unobserved, "{}"),
                "unity_reloading",
                "test.run was not admitted while package validation is pending",
                "package_refresh_pending",
                false);
        }

        [TestCase(false, true, false, true)]
        [TestCase(false, true, true, false)]
        [TestCase(true, true, false, false)]
        [TestCase(false, false, false, false)]
        public void ReadOnlyWaitRequiresExplicitRefreshOnlyForTerminalPackageDelta(bool refreshed, bool refreshRequired, bool waitCanAdvance, bool expected)
        {
            Assert.That(BridgeServer.ShouldReturnPackageRefreshRequired(refreshed, refreshRequired, waitCanAdvance), Is.EqualTo(expected));
        }

        [Test]
        public void PackageLifecyclePublishesWhetherReadOnlyWaitCanAdvance()
        {
            var state = new BridgeRuntimeState();
            state.SetPackageUpdateStatus("package_refresh_required", "Scanning.", true, "sha256:current", string.Empty, 1, string.Empty, refreshWaitCanAdvance: true);

            var transient = state.CapturePackageLifecycleSnapshot();
            Assert.That(transient.PackageRefreshWaitCanAdvance, Is.True);
            Assert.That(transient.PackageUpdateJson, Does.Contain("\"refresh_wait_can_advance\":true"));

            state.SetPackageUpdateStatus("package_refresh_required", "Changed.", true, "sha256:current", "sha256:pending", 1, string.Empty);

            var terminal = state.CapturePackageLifecycleSnapshot();
            Assert.That(terminal.PackageRefreshWaitCanAdvance, Is.False);
            Assert.That(terminal.PackageUpdateJson, Does.Contain("\"refresh_wait_can_advance\":false"));
            Assert.That(BuiltInCommands.CompileStatusJson(state, false, 0, 120000), Does.Contain("\"package_refresh_wait_can_advance\":false"));
        }

        [Test]
        public void PackageTransitionTimestampTracksInventoryAndFailureEvidenceChanges()
        {
            var state = new BridgeRuntimeState();
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Available(1, 2, 2, 0, 0, string.Empty, "source", "asset-database", "source-state"));
            state.SetPackageUpdateStatus("current", "Current.", false, "sha256:current", string.Empty, 6, "2026-09-01T10:00:00Z");
            var currentTransition = state.PackageLastTransitionUtc;

            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Unavailable("scan_failed"));
            var inventoryTransition = state.PackageLastTransitionUtc;
            Assert.That(inventoryTransition, Is.Not.EqualTo(currentTransition));
            Assert.That(state.PackageUpdateJson(), Does.Contain("\"failure_reason\":\"scan_failed\""));
            var inventorySnapshot = state.CapturePackageLifecycleSnapshot();
            Assert.That(inventorySnapshot.LocalPackageCodeInventoryUnavailable, Is.True);
            Assert.That(inventorySnapshot.PackageUpdateJson, Does.Contain("\"last_transition_utc\":\"" + inventoryTransition + "\""));
            Assert.That(inventorySnapshot.PackageUpdateJson, Does.Contain("\"failure_reason\":\"scan_failed\""));

            state.SetPackageUpdateStatus("current", "Current.", false, "sha256:current", string.Empty, 6, "2026-09-01T10:00:00Z");

            state.SetPackageUpdateStatus("current", "Current.", false, "sha256:current", string.Empty, 6, "2026-09-01T10:00:00Z", false, "project_packages", "budget_exhausted", "packages_lock");

            Assert.That(state.PackageLastTransitionUtc, Is.Not.EqualTo(inventoryTransition));
        }

        static BridgeRuntimeState ReadyState()
        {
            var state = new BridgeRuntimeState();
            state.PublishCatalog("sha256:catalog", 1);
            state.SetBridgeListening(true);
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Available(1, 2, 2, 0, 0, string.Empty, "source", "asset-database", "source-state"));
            state.SetPackageUpdateStatus("current", "Current.", false, "sha256:current", string.Empty, 1, "2026-09-02T00:00:00Z");
            state.SetBackgroundCompilerProgressForTests(BackgroundCompilerProgress.ForTests(true, false, false));
            return state;
        }

        static void AssertTestRunBlockResult(CommandExecutionResult result, string code, string message, string reason, bool explicitRefreshAction)
        {
            Assert.That(result.ErrorCode, Is.EqualTo(code));
            Assert.That(result.ErrorMessage, Is.EqualTo(message));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"reason\":\"" + reason + "\""));
            var expectedActions = explicitRefreshAction
                ? "\"next_valid_actions\":[\"compile.status\",\"run_compile.refresh_and_wait_only_as_a_new_explicit_action\",\"do_not_auto_repeat_test.run\"]}"
                : "\"next_valid_actions\":[\"compile.wait_ready\",\"compile.status\",\"do_not_auto_repeat_test.run\"]}";
            Assert.That(result.ErrorDetailsJson, Does.EndWith(expectedActions));
        }

        [Test]
        public void TestRunAdmissionIgnoresOnlyAProvenSupersededBarrier()
        {
            var armed = ExplicitCompilationBarrierState.CreatePending(OperationId, "test-instance-a", 100).WithRequestIssued(110);
            var abandoned = armed.WithReplacementObserved("test-instance-b", 120);
            var independent = new ExplicitCompilationBarrierSnapshot("abandoned", abandoned, abandoned.Failure, false, false, 0);
            var exact = new ExplicitCompilationBarrierSnapshot("abandoned", abandoned, abandoned.Failure, true, false, 0);

            Assert.That(BuildTestAutomationCommands.IsTestRunCompilationSettled(independent, false, false), Is.True);
            Assert.That(BuildTestAutomationCommands.IsTestRunCompilationSettled(exact, false, false), Is.False);
        }

        [Test]
        public void CompilationLifecycleRequiresMatchingFinishEvidence()
        {
            var lifecycle = new CompilationLifecycleState();
            var unobserved = lifecycle.Snapshot();
            Assert.That(unobserved.Evidence, Is.EqualTo("unobserved"));

            var started = lifecycle.Start(new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc));
            Assert.That(started.Generation, Is.EqualTo(1));
            Assert.That(started.CompletedGeneration, Is.EqualTo(0));
            Assert.That(started.Evidence, Is.EqualTo("active"));

            var duplicateStartEvidence = lifecycle.EnsureStarted(new DateTime(2026, 7, 15, 10, 0, 1, DateTimeKind.Utc));
            Assert.That(duplicateStartEvidence.Generation, Is.EqualTo(1));

            var finished = lifecycle.Finish(new DateTime(2026, 7, 15, 10, 0, 2, DateTimeKind.Utc));
            Assert.That(finished.CompletedGeneration, Is.EqualTo(1));
            Assert.That(finished.Evidence, Is.EqualTo("finished"));
            Assert.That(finished.ToJson(), Does.Contain("\"generation\":1"));
            Assert.That(finished.ToJson(), Does.Contain("\"completed_generation\":1"));
        }

        [Test]
        public void FinishWithoutObservedStartCreatesBoundedFinishedGeneration()
        {
            var lifecycle = new CompilationLifecycleState();
            var finished = lifecycle.Finish(new DateTime(2026, 7, 15, 10, 0, 0, DateTimeKind.Utc));

            Assert.That(finished.Generation, Is.EqualTo(1));
            Assert.That(finished.CompletedGeneration, Is.EqualTo(1));
            Assert.That(finished.Evidence, Is.EqualTo("finished"));
        }

        [TestCase(false, true, true, false, true, false, true, false)]
        [TestCase(false, true, false, true, true, false, false, true)]
        public void ExplicitCompileFailureOutranksPackageInventoryFailure(
            bool isReady,
            bool requireExplicitBarrier,
            bool barrierStable,
            bool terminalCompileFailureWithoutReload,
            bool hasCompileErrors,
            bool packageMetadataUnavailable,
            bool localPackageCodeInventoryUnavailable,
            bool localPackageCodeInventoryStale)
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                isReady,
                requireExplicitBarrier,
                barrierStable,
                terminalCompileFailureWithoutReload,
                hasCompileErrors,
                packageMetadataUnavailable,
                localPackageCodeInventoryUnavailable,
                localPackageCodeInventoryStale,
                true);

            Assert.That(errorCode, Is.EqualTo("compilation_failed"));
        }

        [Test]
        public void ReadOnlyWaitStillReportsInventoryWhenCompileEvidenceIsNotAuthoritative()
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                false,
                false,
                true,
                false,
                true,
                false,
                true,
                false,
                false);

            Assert.That(errorCode, Is.EqualTo("local_package_inventory_unavailable"));
        }

        [Test]
        public void ReadOnlyWaitDefersTransientPackageInventoryUnavailability()
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                true,
                false,
                true,
                false,
                false,
                false,
                true,
                false,
                true);

            Assert.That(errorCode, Is.Empty);
        }

        [Test]
        public void StableExplicitBarrierDefersTransientPackageInventoryStaleness()
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                false,
                true,
                true,
                false,
                false,
                false,
                false,
                true,
                true);

            Assert.That(errorCode, Is.Empty);
        }

        [Test]
        public void StableExplicitBarrierReportsInventoryStalenessWithoutPendingRefresh()
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                false,
                true,
                true,
                false,
                false,
                false,
                false,
                true,
                false);

            Assert.That(errorCode, Is.EqualTo("local_package_inventory_stale"));
        }

        [Test]
        public void StableExplicitBarrierDefersTransientPackageInventoryUnavailability()
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                false,
                true,
                true,
                false,
                false,
                false,
                true,
                false,
                true);

            Assert.That(errorCode, Is.Empty);
        }

        [Test]
        public void StableExplicitBarrierReportsInventoryUnavailabilityWithoutPendingRefresh()
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                false,
                true,
                true,
                false,
                false,
                false,
                true,
                false,
                false);

            Assert.That(errorCode, Is.EqualTo("local_package_inventory_unavailable"));
        }

        [Test]
        public void MissingExplicitBarrierIsNotMaskedByPackageInventoryFailure()
        {
            var errorCode = BridgeServer.CompileWaitPriorityErrorCode(
                false,
                true,
                false,
                false,
                false,
                false,
                true,
                true,
                true);

            Assert.That(errorCode, Is.Empty);
        }
    }
}
