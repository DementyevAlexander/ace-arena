using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;
using PackageMarker = SGG.UnityMCP.Editor.Commands.SGGUnityMcpLongRunningMenuAttribute;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class EditorMenuOperationCommandsTests
    {
        [SetUp]
        public void SetUp() => EditorMenuOperationCommands.ResetForTests();

        [TearDown]
        public void TearDown() => EditorMenuOperationCommands.ResetForTests();

        [Test]
        public void CommandDescriptorMatchesPublicContract()
        {
            var method = typeof(EditorMenuOperationCommands).GetMethod("EditorMenuOperationStart", BindingFlags.Public | BindingFlags.Static);
            var descriptor = method.GetCustomAttribute<McpCommandAttribute>();

            Assert.That(descriptor.Id, Is.EqualTo("editor.menu.operation.start"));
            Assert.That(descriptor.Risk, Is.EqualTo("execute"));
            Assert.That(descriptor.Idempotency, Is.EqualTo("unsafe"));
            Assert.That(descriptor.TimeoutMs, Is.EqualTo(10000));
            Assert.That(descriptor.Capability, Is.EqualTo("editor_utilities"));
            Assert.That(descriptor.FeatureFlag, Is.EqualTo("editor_utilities"));
            Assert.That(descriptor.RequiresEditorStatus, Is.EqualTo(new[] { "editing" }));
            Assert.That(descriptor.SupportsJob, Is.False);
            Assert.That(descriptor.Tags, Does.Contain("editor_menu_operation_correlated"));
            Assert.That(descriptor.SchemaJson, Does.Contain("\"menu_path\""));
            Assert.That(descriptor.SchemaJson, Does.Contain("\"dry_run\""));
            Assert.That(descriptor.SchemaJson, Does.Not.Contain("_editor_menu_operation_id"));
        }

        [Test]
        public void TypeCacheRecognizesProjectOwnedMarkerAndNormalizesRequestedWhitespace()
        {
            var result = EditorMenuOperationCommands.ResolveEligibilityForTests("  " + MenuFixtures.ProjectOwnedPath + "  ");

            Assert.That(result.IsEligible, Is.True);
            Assert.That(result.RequestedMenuPath, Is.EqualTo(MenuFixtures.ProjectOwnedPath));
            Assert.That(result.MarkerType, Is.EqualTo(typeof(ProjectOwned.SGGUnityMcpLongRunningMenuAttribute)));
            Assert.That(result.Method, Is.EqualTo(typeof(MenuFixtures).GetMethod(nameof(MenuFixtures.ProjectOwned), BindingFlags.Public | BindingFlags.Static)));
        }

        [Test]
        public void PackageMarkerUsesTheSameExactSimpleNameRule()
        {
            var result = EditorMenuOperationCommands.ResolveEligibilityForTests(MenuFixtures.PackageOwnedPath);

            Assert.That(result.IsEligible, Is.True);
            Assert.That(result.MarkerType, Is.EqualTo(typeof(PackageMarker)));
            Assert.That(result.MarkerType.Name, Is.EqualTo("SGGUnityMcpLongRunningMenuAttribute"));
        }

        [Test]
        public void UnmarkedMenuItemIsRefused()
        {
            var result = EditorMenuOperationCommands.ResolveEligibilityForTests(MenuFixtures.UnmarkedPath);

            Assert.That(result.IsEligible, Is.False);
            Assert.That(result.FailureCode, Is.EqualTo("menu_operation_marker_missing"));
        }

        [Test]
        public void ValidatorMenuItemIsIgnored()
        {
            var result = EditorMenuOperationCommands.ResolveEligibilityForTests(MenuFixtures.ValidatorPath);

            Assert.That(result.IsEligible, Is.False);
            Assert.That(result.CandidateCount, Is.EqualTo(0));
            Assert.That(result.FailureCode, Is.EqualTo("menu_operation_menu_item_not_found"));
        }

        [TestCase("menu-20260806-123456-abcdef12", true)]
        [TestCase("menu-20260806-123456-ABCDEF12", false)]
        [TestCase("menu-20260806-12345-abcdef12", false)]
        [TestCase("menu-20260806-123456-abcdef123", false)]
        [TestCase("menu-../../-abcdef12", false)]
        public void OperationIdsRequireExactSafeShape(string operationId, bool valid)
        {
            Assert.That(EditorMenuOperationCommands.IsSafeMenuOperationId(operationId), Is.EqualTo(valid));
            Assert.That(string.IsNullOrEmpty(ArtifactPath(operationId)), Is.EqualTo(!valid));
        }

        [Test]
        public void PersistedReceiptIdentityIsCanonicalAndReadHelperRejectsMismatch()
        {
            const string operationId = "menu-20260806-123456-abcdef12";
            var artifactPath = ArtifactPath(operationId);
            var raw = ReceiptFixture(operationId, artifactPath);

            Assert.That(EditorMenuOperationCommands.PersistedMenuOperationArtifactIdentityMatches(raw, operationId, artifactPath), Is.True);
            Assert.That(EditorMenuOperationCommands.PersistedMenuOperationArtifactIdentityMatches(raw, operationId, artifactPath + ".other"), Is.False);
            Assert.That(EditorMenuOperationCommands.PersistedMenuOperationArtifactIdentityMatches(raw.Substring(0, raw.Length - 1), operationId, artifactPath), Is.False);
            Assert.That(EditorMenuOperationCommands.NormalizePersistedMenuOperationArtifactForRead(raw, operationId, artifactPath), Does.Contain("\"status_request\""));
            var impossible = raw.Replace("\"dispatch_count\":0", "\"dispatch_count\":1").Replace("\"menu_execute_invoked\":false", "\"menu_execute_invoked\":true");
            Assert.That(EditorMenuOperationCommands.NormalizePersistedMenuOperationArtifactForRead(impossible, operationId, artifactPath), Is.Empty);
            var failedWithTrueReturn = raw.Replace("\"status\":\"queued\",\"terminal\":false", "\"status\":\"failed\",\"terminal\":true")
                .Replace("\"dispatch_count\":0,\"execute_menu_item_returned\":null", "\"running_at_utc\":\"2026-08-06T12:34:57.0000000Z\",\"terminal_at_utc\":\"2026-08-06T12:34:58.0000000Z\",\"dispatch_count\":1,\"execute_menu_item_returned\":true")
                .Replace("\"menu_execute_invoked\":false", "\"menu_execute_invoked\":true");
            Assert.That(EditorMenuOperationCommands.NormalizePersistedMenuOperationArtifactForRead(failedWithTrueReturn, operationId, artifactPath), Is.Empty);
            var failedWithLoggedException = failedWithTrueReturn.Replace("\"failure_code\":\"\"", "\"failure_code\":\"unity_exception\"");
            Assert.That(EditorMenuOperationCommands.NormalizePersistedMenuOperationArtifactForRead(failedWithLoggedException, operationId, artifactPath), Does.Contain("\"failure_code\":\"unity_exception\""));
            var dispatchedWithoutRunningTimestamp = failedWithTrueReturn.Replace("\"execute_menu_item_returned\":true", "\"execute_menu_item_returned\":false")
                .Replace("\"running_at_utc\":\"2026-08-06T12:34:57.0000000Z\",", string.Empty);
            Assert.That(EditorMenuOperationCommands.NormalizePersistedMenuOperationArtifactForRead(dispatchedWithoutRunningTimestamp, operationId, artifactPath), Is.Empty);

            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Assert.That(EditorMenuOperationCommands.TryWriteArtifactAtomic(artifactPath, raw, out var writeError), Is.True, writeError);
                var read = EditorMenuOperationCommands.ReadPersistedMenuOperationArtifact(operationId, artifactPath, out var mismatch, out var readError);
                Assert.That(mismatch, Is.False);
                Assert.That(readError, Is.Empty);
                Assert.That(read, Does.Contain("\"operation_id\":\"" + operationId + "\""));
            }
            finally
            {
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
        }

        [Test]
        public void DryRunDoesNotCreateReceiptOrScheduleDispatch()
        {
            var operationId = "menu-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var artifactPath = ArtifactPath(operationId);
            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath)) File.Delete(fullPath);

            var args = "{\"menu_path\":\"" + MenuFixtures.ProjectOwnedPath + "\",\"dry_run\":true,\"_editor_menu_operation_id\":\"" + operationId + "\"}";
            var result = EditorMenuOperationCommands.EditorMenuOperationStart(new McpCommandContext(null, "test", "test", args, "{}"));

            Assert.That(result.Status, Is.EqualTo("ok"));
            Assert.That(result.DataJson, Does.Contain("\"would_start\":true"));
            Assert.That(result.DataJson, Does.Contain("\"dispatch_scheduled\":false"));
            Assert.That(result.DataJson, Does.Contain("\"operation_created\":false"));
            Assert.That(result.DataJson, Does.Contain("\"menu_execute_invoked\":false"));
            Assert.That(File.Exists(fullPath), Is.False);
        }

        [Test]
        public void RealStartPersistsQueuedReceiptAndUsesStatusCommand()
        {
            var operationId = "menu-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var artifactPath = ArtifactPath(operationId);
            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath)) File.Delete(fullPath);

            var args = "{\"menu_path\":\"" + MenuFixtures.ProjectOwnedPath + "\",\"_editor_menu_operation_id\":\"" + operationId + "\"}";
            try
            {
                var result = EditorMenuOperationCommands.EditorMenuOperationStart(new McpCommandContext(null, "test", "test", args, "{}"));

                Assert.That(result.Status, Is.EqualTo("ok"));
                Assert.That(result.DataJson, Does.Contain("\"status\":\"queued\""));
                Assert.That(result.DataJson, Does.Contain("\"dispatch_count\":0"));
                Assert.That(result.DataJson, Does.Contain("\"menu_execute_invoked\":false"));
                Assert.That(result.DataJson, Does.Contain("\"command\":\"editor.menu.operation.status\""));
                var parsed = JsonUtility.FromJson<ReceiptProbe>(result.DataJson);
                Assert.That(parsed.operation_id, Is.EqualTo(operationId));
                Assert.That(parsed.status_request.command, Is.EqualTo("editor.menu.operation.status"));
                Assert.That(File.Exists(fullPath), Is.True);
            }
            finally
            {
                EditorMenuOperationCommands.ResetForTests();
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
        }

        [Test]
        public void DirectMenuExecuteFailsWhenUnityLogsSynchronousException()
        {
            LogAssert.Expect(LogType.Exception, new Regex("menu execution regression"));

            var args = "{\"menu_path\":\"" + MenuFixtures.ThrowingPath + "\"}";
            var result = EditorUtilityCommands.EditorMenuExecute(new McpCommandContext(null, "test", "test", args, "{}"));

            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.ErrorCode, Is.EqualTo("unity_exception"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"execute_menu_item_returned\":true"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"exception_observed\":true"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("menu execution regression"));
        }

        [UnityTest]
        public IEnumerator DurableMenuReceiptFailsWhenUnityLogsSynchronousException()
        {
            var operationId = "menu-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var artifactPath = ArtifactPath(operationId);
            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath)) File.Delete(fullPath);

            var args = "{\"menu_path\":\"" + MenuFixtures.ThrowingPath + "\",\"_editor_menu_operation_id\":\"" + operationId + "\"}";
            try
            {
                var result = EditorMenuOperationCommands.EditorMenuOperationStart(new McpCommandContext(null, "test", "test", args, "{}"));
                Assert.That(result.Status, Is.EqualTo("ok"));
                LogAssert.Expect(LogType.Exception, new Regex("menu execution regression"));

                typeof(EditorMenuOperationCommands).GetMethod("Dispatch", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);

                yield return WaitForTerminalReceipt(fullPath, operationId);
                var receipt = ReadOwnedMenuReceipt(fullPath);
                var diagnostic = ReceiptFailureDiagnostic(receipt) + " " + MenuMemoryDiagnostic(operationId);
                Assert.That(receipt, Does.Contain("\"status\":\"failed\""), diagnostic);
                Assert.That(receipt, Does.Contain("\"success\":false"), diagnostic);
                Assert.That(receipt, Does.Contain("\"execute_menu_item_returned\":true"), diagnostic);
                Assert.That(receipt, Does.Contain("\"failure_code\":\"unity_exception\""), diagnostic);
            }
            finally
            {
                EditorMenuOperationCommands.ResetForTests();
                if (File.Exists(fullPath)) File.Delete(fullPath);
            }
        }

        [UnityTest]
        public IEnumerator TerminalPublicationRetryDoesNotExecuteTheOwnedThrowingMenuAgain()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("This synthetic sharing-denial fixture requires Windows file sharing semantics.");
            var operationId = "menu-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var artifactPath = ArtifactPath(operationId);
            var fullPath = Path.Combine(ProjectPaths.ProjectRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));
            MenuFixtures.LockedReceiptPath = fullPath;
            MenuFixtures.LockedInvocationCount = 0;
            try
            {
                var args = "{\"menu_path\":\"" + MenuFixtures.LockedThrowingPath + "\",\"_editor_menu_operation_id\":\"" + operationId + "\"}";
                var result = EditorMenuOperationCommands.EditorMenuOperationStart(new McpCommandContext(null, "test", "test", args, "{}"));
                Assert.That(result.Status, Is.EqualTo("ok"));
                LogAssert.Expect(LogType.Exception, new Regex("menu publication lock regression"));
                typeof(EditorMenuOperationCommands).GetMethod("Dispatch", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                var dispatchDeadline = EditorApplication.timeSinceStartup + 10d;
                while (MenuFixtures.LockedInvocationCount == 0 && EditorApplication.timeSinceStartup < dispatchDeadline)
                    yield return null;
                Assert.That(MenuFixtures.LockedInvocationCount, Is.EqualTo(1), MenuMemoryDiagnostic(operationId));
                var state = MenuSnapshot(operationId);
                Assert.That(state, Is.Not.Null);
                Assert.That(MenuStateField(state, "Status"), Is.EqualTo("failed"));
                Assert.That(MenuStateField(state, "Terminal"), Is.EqualTo(true));
                Assert.That(MenuStateField(state, "FailureCode"), Is.EqualTo("unity_exception"));
                Assert.That(MenuStateField(state, "DispatchCount"), Is.EqualTo(1));
                Assert.That(MenuStateField(state, "MenuExecuteInvoked"), Is.EqualTo(true));
                Assert.That(MenuStateField(state, "ExecuteMenuItemReturned"), Is.EqualTo(true));
                Assert.That(typeof(EditorMenuOperationCommands).GetField("TerminalPublicationRegistered", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.EqualTo(true));
                Assert.That(ReadOwnedMenuReceipt(fullPath), Does.Contain("\"status\":\"running\""));

                MenuFixtures.ReleaseReceiptHandle();
                yield return WaitForTerminalReceipt(fullPath, operationId);
                var receipt = ReadOwnedMenuReceipt(fullPath);
                var diagnostic = ReceiptFailureDiagnostic(receipt) + " " + MenuMemoryDiagnostic(operationId);
                Assert.That(receipt, Does.Contain("\"status\":\"failed\""), diagnostic);
                Assert.That(receipt, Does.Contain("\"success\":false"), diagnostic);
                Assert.That(receipt, Does.Contain("\"execute_menu_item_returned\":true"), diagnostic);
                Assert.That(receipt, Does.Contain("\"failure_code\":\"unity_exception\""), diagnostic);
                Assert.That(MenuFixtures.LockedInvocationCount, Is.EqualTo(1));
                Assert.That(MenuSnapshot(operationId), Is.Null);
                Assert.That(typeof(EditorMenuOperationCommands).GetField("TerminalPublicationRegistered", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null), Is.EqualTo(false));
            }
            finally
            {
                try { MenuFixtures.ReleaseReceiptHandle(); }
                finally
                {
                    MenuFixtures.LockedReceiptPath = null;
                    try { EditorMenuOperationCommands.ResetForTests(); }
                    finally { if (File.Exists(fullPath)) File.Delete(fullPath); }
                }
            }
        }

        static IEnumerator WaitForTerminalReceipt(string fullPath, string operationId)
        {
            // Observe real Editor updates and the production publication retry, never dispatch again.
            var deadline = EditorApplication.timeSinceStartup + 10d;
            while (EditorApplication.timeSinceStartup < deadline)
            {
                string receipt = null;
                try { receipt = ReadOwnedMenuReceipt(fullPath); }
                catch (IOException) { }
                if (receipt != null && receipt.Contains("\"terminal\":true")) yield break;
                yield return null;
            }
            Assert.Fail("Terminal receipt was not durably published within the fixed fixture deadline. " + MenuMemoryDiagnostic(operationId));
        }

        static string ReadOwnedMenuReceipt(string fullPath)
        {
            const int maxBytes = 1024 * 1024;
            var bytes = new byte[maxBytes + 1];
            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var total = 0;
                while (total < bytes.Length)
                {
                    var count = stream.Read(bytes, total, bytes.Length - total);
                    if (count <= 0) break;
                    total += count;
                }
                if (total > maxBytes) throw new InvalidOperationException("Owned menu receipt exceeds the fixture byte limit.");
                return new System.Text.UTF8Encoding(false, true).GetString(bytes, 0, total);
            }
        }

        static object MenuSnapshot(string operationId)
        {
            var state = typeof(EditorMenuOperationCommands).GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            return state != null && Equals(MenuStateField(state, "OperationId"), operationId) ? state : null;
        }

        static object MenuStateField(object state, string field)
        {
            return state.GetType().GetField(field, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).GetValue(state);
        }

        static string MenuMemoryDiagnostic(string operationId)
        {
            var state = MenuSnapshot(operationId);
            if (state == null) return "menu_memory=<unavailable_for_exact_id>";
            var builder = new System.Text.StringBuilder("menu_memory");
            foreach (var field in new[] { "Status", "Terminal", "FailureCode", "DispatchCount", "MenuExecuteInvoked", "ExecuteMenuItemReturned", "TerminalPublicationAttempts" })
                builder.Append(';').Append(field).Append('=').Append(MenuStateField(state, field));
            builder.Append(";publication_registered=").Append(typeof(EditorMenuOperationCommands).GetField("TerminalPublicationRegistered", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null));
            return builder.ToString();
        }

        [Test]
        public void InvalidHiddenCorrelationDeclaresDefiniteRefusal()
        {
            var args = "{\"menu_path\":\"" + MenuFixtures.ProjectOwnedPath + "\",\"_editor_menu_operation_id\":\"menu-invalid\"}";
            var result = EditorMenuOperationCommands.EditorMenuOperationStart(new McpCommandContext(null, "test", "test", args, "{}"));

            Assert.That(result.Status, Is.EqualTo("error"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"operation_created\":false"));
            Assert.That(result.ErrorDetailsJson, Does.Contain("\"menu_execute_invoked\":false"));
        }

        [Test]
        public void ReceiptDiagnosticPlacesFailureBeforeUnrelatedIdentityFields()
        {
            var receipt = "{\"operation_id\":\"not-disclosed\",\"args_hash\":\"not-disclosed\",\"artifact_path\":\"not-disclosed\","
                + "\"failure_code\":\"pre_execute_editor_not_ready\",\"message\":\"Not ready\",\"status\":\"failed\",\"dispatch_count\":0,\"menu_execute_invoked\":false}";
            var diagnostic = ReceiptFailureDiagnostic(receipt);
            Assert.That(diagnostic, Does.StartWith("menu_receipt failure_code=pre_execute_editor_not_ready;"));
            Assert.That(diagnostic, Does.Contain("message=Not ready"));
            Assert.That(diagnostic, Does.Not.Contain("not-disclosed"));
            Assert.That(diagnostic.Length, Is.LessThanOrEqualTo(768));
        }

        [Test]
        public void ReceiptDiagnosticBoundsTextBeforeFlatteningAndDoesNotSplitSurrogates()
        {
            var message = new string('x', 239) + "😀" + new string('y', 1000);
            var receipt = "{\"failure_code\":\"fixture_failure\",\"message\":\"" + message + "\",\"status\":\"failed\"}";
            var diagnostic = ReceiptFailureDiagnostic(receipt);
            Assert.That(diagnostic, Does.StartWith("menu_receipt failure_code=fixture_failure;"));
            Assert.That(diagnostic, Does.Contain("message=" + new string('x', 239) + ";"));
            Assert.That(diagnostic.IndexOf('\uD83D'), Is.EqualTo(-1));
            Assert.That(diagnostic.IndexOf('\uFFFD'), Is.EqualTo(-1));
            Assert.That(diagnostic.Length, Is.LessThanOrEqualTo(768));
        }

        [TestCase(null)]
        [TestCase("")]
        [TestCase("not json")]
        public void ReceiptDiagnosticFailureDoesNotMaskTheOriginalAssertion(string receipt)
        {
            string diagnostic = null;
            Assert.DoesNotThrow(() => diagnostic = ReceiptFailureDiagnostic(receipt));
            Assert.That(diagnostic, Is.EqualTo("menu_receipt diagnostic_unavailable"));
        }

        [Test]
        public void ReceiptDiagnosticSurvivesTheBoundedNUnitMessagePrefix()
        {
            var receipt = "{\"failure_code\":\"fixture_refused\",\"message\":\"Synthetic refusal\",\"status\":\"failed\",\"padding\":\"" + new string('p', 2000) + "\"}";
            var diagnostic = ReceiptFailureDiagnostic(receipt);
            var failure = Assert.Throws<AssertionException>(() => Assert.That(receipt, Does.Contain("\"execute_menu_item_returned\":true"), diagnostic));
            var boundedPrefix = failure.Message.Substring(0, Math.Min(512, failure.Message.Length));
            Assert.That(boundedPrefix.IndexOf("failure_code=fixture_refused", StringComparison.Ordinal), Is.GreaterThanOrEqualTo(0));
            Assert.That(failure.Message, Does.Contain("Expected:"));
        }

        [Test]
        public void ReceiptDiagnosticRejectsOversizedInputBeforeParsing()
        {
            Assert.That(ReceiptFailureDiagnostic(new string('x', 65537)), Is.EqualTo("menu_receipt diagnostic_unavailable"));
        }

        static string ReceiptFailureDiagnostic(string receipt)
        {
            if (string.IsNullOrEmpty(receipt) || receipt.Length > 65536) return "menu_receipt diagnostic_unavailable";
            try
            {
                var probe = JsonUtility.FromJson<ReceiptDiagnosticProbe>(receipt);
                if (probe == null) return "menu_receipt diagnostic_unavailable";
                // Keep failure evidence ahead of NUnit's automatic whole-receipt
                // rendering, which can exceed the bounded TestRunner message.
                // These are diagnostic fields only, never admission evidence.
                return "menu_receipt failure_code=" + DiagnosticText(probe.failure_code, 96)
                    + "; message=" + DiagnosticText(probe.message, 240)
                    + "; status=" + DiagnosticText(probe.status, 32) + ";";
            }
            catch { return "menu_receipt diagnostic_unavailable"; }
        }

        static string DiagnosticText(string value, int limit)
        {
            if (string.IsNullOrEmpty(value)) return "<empty_or_absent>";
            var end = Math.Min(value.Length, limit);
            if (end < value.Length && end > 0 && char.IsHighSurrogate(value[end - 1])) end--;
            return value.Substring(0, end).Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
        }

        [Serializable]
        sealed class ReceiptDiagnosticProbe
        {
            public string failure_code;
            public string message;
            public string status;
        }

        static string ArtifactPath(string operationId)
        {
            return EditorMenuOperationCommands.ArtifactPathForOperation(operationId);
        }

        static string ReceiptFixture(string operationId, string artifactPath)
        {
            const string hash = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
            return "{\"contract\":\"sgg.unity_mcp.editor_menu_operation.v1\",\"artifact_schema_version\":1,\"command\":\"editor.menu.operation.start\","
                + "\"operation_id\":\"" + operationId + "\",\"status\":\"queued\",\"terminal\":false,\"success\":false,"
                + "\"created_at_utc\":\"2026-08-06T12:34:56.0000000Z\",\"queued_at_utc\":\"2026-08-06T12:34:56.0000000Z\",\"queue_deadline_utc\":\"2026-08-06T12:35:06.0000000Z\","
                + "\"menu_path\":\"" + MenuFixtures.ProjectOwnedPath + "\",\"args_hash\":\"" + hash + "\",\"caller_args_hash\":\"" + hash + "\",\"dispatch_count\":0,\"execute_menu_item_returned\":null,"
                + "\"artifact_path\":\"" + artifactPath + "\",\"artifact_persisted\":true,\"operation_created\":true,\"menu_execute_invoked\":false,\"failure_code\":\"\",\"message\":\"\","
                + "\"eligibility\":{\"marker_type\":\"SGG.UnityMCP.Editor.Tests.ProjectOwned.SGGUnityMcpLongRunningMenuAttribute\",\"marker_assembly\":\"SGG.UnityMCP.Editor.Tests\",\"method_identity\":{\"assembly\":\"SGG.UnityMCP.Editor.Tests\",\"declaring_type\":\"MenuFixtures\",\"name\":\"ProjectOwned\",\"signature\":\"System.Void MenuFixtures.ProjectOwned()\"}},"
                + "\"status_request\":{\"command\":\"editor.menu.operation.status\",\"args_json\":\"{\\\"operation_id\\\":\\\"" + operationId + "\\\"}\"},\"next_valid_actions\":[\"editor.menu.operation.status\",\"job.artifact.read\"]}";
        }

        [Serializable]
        sealed class ReceiptProbe
        {
            public string operation_id;
            public StatusRequestProbe status_request;
        }

        [Serializable]
        sealed class StatusRequestProbe
        {
            public string command;
        }

        public static class MenuFixtures
        {
            public const string ProjectOwnedPath = "SGG/Unity MCP Tests/Project Owned Durable Menu";
            public const string PackageOwnedPath = "SGG/Unity MCP Tests/Package Owned Durable Menu";
            public const string UnmarkedPath = "SGG/Unity MCP Tests/Unmarked Durable Menu";
            public const string ValidatorPath = "SGG/Unity MCP Tests/Validator Durable Menu";
            public const string ThrowingPath = "SGG/Unity MCP Tests/Throwing Durable Menu";
            public const string LockedThrowingPath = "SGG/Unity MCP Tests/Owned Locked Throwing Durable Menu";
            public static string LockedReceiptPath;
            public static int LockedInvocationCount;
            static FileStream s_receiptHandle;

            public static void ReleaseReceiptHandle()
            {
                var handle = s_receiptHandle;
                s_receiptHandle = null;
                if (handle != null) handle.Dispose();
            }

            [MenuItem(ProjectOwnedPath)]
            [ProjectOwned.SGGUnityMcpLongRunningMenu]
            public static void ProjectOwned() { }

            [MenuItem(PackageOwnedPath + " _p")]
            [PackageMarker]
            public static void PackageOwned() { }

            [MenuItem(UnmarkedPath)]
            public static void Unmarked() { }

            [MenuItem(ThrowingPath)]
            [ProjectOwned.SGGUnityMcpLongRunningMenu]
            public static void Throwing() => throw new InvalidOperationException("menu execution regression");

            [MenuItem(LockedThrowingPath)]
            [ProjectOwned.SGGUnityMcpLongRunningMenu]
            public static void LockedThrowing()
            {
                LockedInvocationCount++;
                if (s_receiptHandle != null) throw new InvalidOperationException("menu publication lock regression: unexpected second invocation");
                s_receiptHandle = new FileStream(LockedReceiptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                throw new InvalidOperationException("menu publication lock regression");
            }

            [MenuItem(ValidatorPath, true)]
            [ProjectOwned.SGGUnityMcpLongRunningMenu]
            public static bool Validator() { return true; }
        }
    }

    namespace ProjectOwned
    {
        [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
        public sealed class SGGUnityMcpLongRunningMenuAttribute : Attribute
        {
        }
    }
}
