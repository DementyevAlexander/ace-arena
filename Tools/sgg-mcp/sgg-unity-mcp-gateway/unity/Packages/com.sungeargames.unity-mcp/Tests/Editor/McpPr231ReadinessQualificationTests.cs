using System;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class McpPr231ReadinessQualificationTests
    {
        [Test]
        public void QualificationNamesScopeWithoutClaimingShaderOrExplicitCompilationSuccess()
        {
            var json = BuiltInCommands.CompileReadinessQualificationJson();
            Assert.That(JsonUtil.ExtractInt(json, "schema_version", 0), Is.EqualTo(1));
            Assert.That(JsonUtil.ExtractString(json, "contract"), Is.EqualTo("sgg.unity_mcp.compile_readiness_qualification.v1"));
            Assert.That(JsonUtil.ExtractString(json, "readiness_scope"), Is.EqualTo("editor_package_lifecycle_script_evidence_and_background_progress"));
            Assert.That(JsonUtil.ExtractString(json, "compile_diagnostics_scope"), Is.EqualTo("script_compiler_and_console_cs_error_marker"));
            Assert.That(JsonUtil.ExtractString(json, "explicit_compilation_evidence"), Is.EqualTo("inspect_compile_barrier_not_inferred_from_ready_clean"));
            AssertUnknownAssetsAndIncompleteHistory(json);
            Assert.That(json.Length, Is.LessThan(700));
            Assert.That(json, Does.Not.Contain("\"ready\":true"));
        }

        [TestCase("Shader error in 'Owned/Test': syntax error at line 3", LogType.Error, false)]
        [TestCase("Compute shader error: undeclared identifier", LogType.Exception, false)]
        [TestCase("Shader compiler failure", LogType.Warning, false)]
        [TestCase("Assets/Owned/Test.cs(1,2): error CS1002: ; expected", LogType.Error, true)]
        [TestCase("Shader-shaped message containing error CS1002", LogType.Error, true)]
        [TestCase("error CS1002", LogType.Log, false)]
        public void ConsoleCompilerEvidenceIsTheExistingTextualCsClassifierNotAShaderInventory(string message, LogType type, bool expected)
        {
            var method = typeof(ConsoleLogBuffer).GetMethod("IsCompilerErrorLog", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.That(method, Is.Not.Null);
            var arguments = new object[] { message, type, string.Empty, 0, 0 };
            var count = ConsoleLogBuffer.CompileErrorCount();
            Assert.That(method.Invoke(null, arguments), Is.EqualTo(expected));
            Assert.That(ConsoleLogBuffer.CompileErrorCount(), Is.EqualTo(count));
            AssertUnknownAssetsAndIncompleteHistory(BuiltInCommands.CompileReadinessQualificationJson());
        }

        [TestCase(false)]
        [TestCase(true)]
        public void ResetLossOrAnEmptyEpochNeverBecomesCompleteDurableHistory(bool previousEpoch)
        {
            var marker = previousEpoch ? ConsoleLogBuffer.BufferMetadataMarkerForTests("0123456789abcdef0123456789abcdef") : string.Empty;
            var json = ConsoleLogBuffer.BufferMetadataJsonForTests(marker, "fedcba9876543210fedcba9876543210", "2026-10-08T07:00:00Z");
            Assert.That(JsonUtil.ExtractBool(json, "reset_loss", !previousEpoch), Is.EqualTo(previousEpoch));
            Assert.That(JsonUtil.ExtractBool(json, "history_complete", true), Is.False);
            AssertUnknownAssetsAndIncompleteHistory(BuiltInCommands.CompileReadinessQualificationJson());
        }

        [TestCase(false, false)]
        [TestCase(true, false)]
        [TestCase(false, true)]
        [TestCase(true, true)]
        public void StatusAndNestedValidationKeepExistingReadinessCountsAndBackgroundGates(bool timedOut, bool blocked)
        {
            var state = new BridgeRuntimeState();
            state.PublishCatalog("sha256:catalog", 1);
            state.SetBridgeListening(true);
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Available(1, 2, 2, 0, 0, string.Empty, "source", "asset-database", "source-state"));
            state.SetPackageUpdateStatus("current", "Current.", false, "sha256:current", string.Empty, 1, "2026-10-08T07:00:00Z");
            state.SetBackgroundCompilerProgressForTests(BackgroundCompilerProgress.ForTests(true, blocked, false, blocked ? "active" : "complete", "compute_variant_compile"));
            var lifecycle = state.CapturePackageLifecycleSnapshot();
            var barrier = ExplicitCompilationBarrier.Snapshot(string.Empty, false);
            var count = ConsoleLogBuffer.CompileErrorCount();
            var errors = ConsoleLogBuffer.HasCompileErrors();
            var expectedReady = lifecycle.IsReady && barrier.Stable && !barrier.Unavailable && !errors && !lifecycle.PackageRefreshRequired && !lifecycle.BackgroundCompilerProgress.BlocksReadiness;
            var status = BuiltInCommands.CompileStatusJson(state, timedOut, 25, 100, string.Empty, false, lifecycle);
            var validation = BuiltInCommands.ValidationStatusJson(state, "preflight", false, timedOut, 25, 100, string.Empty, false, lifecycle);
            Assert.That(JsonUtil.ExtractBool(status, "ready", !expectedReady), Is.EqualTo(expectedReady));
            Assert.That(JsonUtil.ExtractBool(status, "timed_out", !timedOut), Is.EqualTo(timedOut));
            Assert.That(JsonUtil.ExtractInt(status, "compile_error_count", -1), Is.EqualTo(count));
            Assert.That(JsonUtil.ExtractObject(status, "compile_barrier"), Is.EqualTo(barrier.ToJson()));
            Assert.That(JsonUtil.ExtractObject(validation, "compile_status"), Is.EqualTo(status));
            Assert.That(JsonUtil.ExtractObject(status, "readiness_qualification"), Is.EqualTo(BuiltInCommands.CompileReadinessQualificationJson()));
            Assert.That(ConsoleLogBuffer.CompileErrorCount(), Is.EqualTo(count));
            AssertUnknownAssetsAndIncompleteHistory(JsonUtil.ExtractObject(status, "readiness_qualification"));
        }

        [Test]
        public void NoRequiredBarrierAndPendingOperationRemainDistinctWithoutQualificationSupersession()
        {
            var idle = new ExplicitCompilationBarrierSnapshot("not_required", null, string.Empty, false, false, 0);
            var pending = ExplicitCompilationBarrierState.CreatePending("0123456789abcdef0123456789abcdef", "test-instance", 100);
            var required = new ExplicitCompilationBarrierSnapshot("pending", pending, string.Empty, true, false, 0);
            Assert.That(idle.Stable, Is.True);
            Assert.That(idle.Verified, Is.False);
            Assert.That(required.Stable, Is.False);
            Assert.That(required.Verified, Is.False);
            var before = required.ToJson();
            AssertUnknownAssetsAndIncompleteHistory(BuiltInCommands.CompileReadinessQualificationJson());
            Assert.That(required.ToJson(), Is.EqualTo(before));
            Assert.That(JsonUtil.ExtractBool(idle.ToJson(), "required", true), Is.False);
            Assert.That(JsonUtil.ExtractBool(required.ToJson(), "required", false), Is.True);
        }

        static void AssertUnknownAssetsAndIncompleteHistory(string json)
        {
            Assert.That(JsonUtil.ExtractString(json, "shader_asset_readiness"), Is.EqualTo("unknown"));
            Assert.That(JsonUtil.ExtractBool(json, "console_history_complete", true), Is.False);
            Assert.That(JsonUtil.ExtractBool(json, "console_history_durable", true), Is.False);
        }
    }
}
