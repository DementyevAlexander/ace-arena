using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class GatewayTestHistoryTests
    {
        static GatewayTestHistoryModel.Snapshot Snapshot()
        {
            return new GatewayTestHistoryModel.Snapshot
            {
                scope = GatewayTestHistoryModel.Scope, settings_source = "saved_config_restart_required",
                bytes = 100 * GatewayTestHistoryModel.MiB, bindings = 48000,
                max_bytes = 200 * GatewayTestHistoryModel.MiB, max_bindings = 192000, retention_days = 30,
                config_fingerprint = "sha256:" + new string('a', 64),
                preview_fingerprint = "sha256:" + new string('b', 64),
                preview_expires_at = DateTime.UtcNow.AddMinutes(2).ToString("O"),
                candidates = new GatewayTestHistoryModel.Counts { bindings = 3, files = 9, bytes = 7000 },
                preserved = new GatewayTestHistoryModel.Preserved { active = 2, unknown = 5, foreign = 1 },
            };
        }

        [Test]
        public void DefaultsAndIndependentCapacityIndicatorsMatchUserRequest()
        {
            Assert.That(GatewayTestHistoryModel.DefaultBindings, Is.EqualTo(192000));
            Assert.That(GatewayTestHistoryModel.DefaultMiB * GatewayTestHistoryModel.MiB, Is.EqualTo(209715200));
            WithWindow(window =>
            {
                window.RenderTestHistory(Snapshot());
                Assert.That(window.rootVisualElement.Q<ProgressBar>("setup-test-history-bytes").value, Is.EqualTo(50));
                Assert.That(window.rootVisualElement.Q<ProgressBar>("setup-test-history-bindings").value, Is.EqualTo(25));
                Assert.That(window.rootVisualElement.Q<IntegerField>("setup-test-history-size-mib").value, Is.EqualTo(200));
                Assert.That(window.rootVisualElement.Q<IntegerField>("setup-test-history-record-limit").value, Is.EqualTo(192000));
                Assert.That(window.rootVisualElement.Q<Label>("setup-test-history-status").text, Does.Contain("Running limits not verified"));
            });
        }

        [TestCase(1, 1, true)]
        [TestCase(200, 192000, true)]
        [TestCase(1024, 1000000, true)]
        [TestCase(0, 192000, false)]
        [TestCase(1025, 192000, false)]
        [TestCase(200, 1000001, false)]
        [TestCase(200, 0, false)]
        public void SettingsHaveIndependentFiniteBounds(int size, int records, bool valid)
        {
            Assert.That(GatewayTestHistoryModel.ValidLimits(size, records), Is.EqualTo(valid));
        }

        [Test]
        public void UnknownStatusIsNotPresentedAsEmptyHistory()
        {
            WithWindow(window =>
            {
                window.RenderTestHistory(null);
                var bytes = window.rootVisualElement.Q<ProgressBar>("setup-test-history-bytes");
                Assert.That(bytes.enabledSelf, Is.False);
                Assert.That(bytes.title, Does.Contain("unknown"));
                Assert.That(window.rootVisualElement.Q<Button>("setup-test-history-clear").enabledSelf, Is.False);
                Assert.That(window.rootVisualElement.Q<Button>("setup-test-history-save").enabledSelf, Is.False);
            });
        }

        [Test]
        public void OverQuotaKeepsTruthfulNumbersAndClampsOnlyBar()
        {
            var snapshot = Snapshot();
            snapshot.bytes = snapshot.max_bytes + 1;
            snapshot.bindings = 192001;
            WithWindow(window =>
            {
                window.RenderTestHistory(snapshot);
                var bytes = window.rootVisualElement.Q<ProgressBar>("setup-test-history-bytes");
                Assert.That(bytes.value, Is.EqualTo(100));
                Assert.That(bytes.title, Does.Contain(snapshot.bytes.ToString()));
                Assert.That(bytes.ClassListContains("mcp-history-full"), Is.True);
                Assert.That(window.rootVisualElement.Q<ProgressBar>("setup-test-history-bindings").title, Does.Contain("192001 / 192000"));
            });
        }

        [Test]
        public void CancelledCleanupNeverCallsMutation()
        {
            WithWindow(window =>
            {
                var calls = new List<GatewayTestHistoryManager.Operation>();
                var confirmations = new List<bool>();
                var dialogMessage = string.Empty;
                window.HistoryOperationForTests = (operation, binding, size, records, confirm, preserveBytes) =>
                {
                    calls.Add(operation);
                    confirmations.Add(confirm);
                    return Task.FromResult(new GatewayTestHistoryManager.Result { status = "ok", data = Snapshot() });
                };
                window.HistoryConfirmationForTests = (title, message, accept, cancel) =>
                {
                    dialogMessage = message;
                    return false;
                };
                window.ClearTestHistoryAsync().GetAwaiter().GetResult();
                Assert.That(calls, Is.EqualTo(new[] { GatewayTestHistoryManager.Operation.PreviewCleanup }));
                Assert.That(confirmations, Is.EqualTo(new[] { false }));
                Assert.That(dialogMessage, Does.Contain("unknown 5").And.Contain("never deleted"));
                Assert.That(window.rootVisualElement.Q<Label>("setup-test-history-status").text, Does.Contain("cancelled"));
            });
        }

        [Test]
        public void ConfirmedCleanupUsesExactPreviewAndNeverReplaysStaleFailure()
        {
            WithWindow(window =>
            {
                var preview = Snapshot();
                var calls = new List<GatewayTestHistoryManager.Operation>();
                GatewayTestHistoryModel.Snapshot cleanupBinding = null;
                var cleanupConfirmed = false;
                window.HistoryConfirmationForTests = (title, message, accept, cancel) => true;
                window.HistoryOperationForTests = (operation, binding, size, records, confirm, preserveBytes) =>
                {
                    calls.Add(operation);
                    if (operation == GatewayTestHistoryManager.Operation.PreviewCleanup)
                        return Task.FromResult(new GatewayTestHistoryManager.Result { status = "ok", data = preview });
                    cleanupBinding = binding;
                    cleanupConfirmed = confirm;
                    return Task.FromResult(GatewayTestHistoryManager.Result.Fail("preview_changed"));
                };
                window.ClearTestHistoryAsync().GetAwaiter().GetResult();
                Assert.That(cleanupBinding, Is.SameAs(preview));
                Assert.That(cleanupConfirmed, Is.True);
                Assert.That(calls, Is.EqualTo(new[] { GatewayTestHistoryManager.Operation.PreviewCleanup, GatewayTestHistoryManager.Operation.Cleanup }));
                Assert.That(window.rootVisualElement.Q<Label>("setup-test-history-status").text, Does.Contain("preview_changed"));
                Assert.That(window.rootVisualElement.Q<Button>("setup-test-history-clear").enabledSelf, Is.False);
            });
        }

        [Test]
        public void ExpiredPreviewCannotAuthorizeCleanup()
        {
            var preview = Snapshot();
            preview.preview_expires_at = DateTime.UtcNow.AddSeconds(-1).ToString("O");
            Assert.That(GatewayTestHistoryModel.CanConfirmCleanup(preview, DateTime.UtcNow), Is.False);
            Assert.Throws<ArgumentException>(() => GatewayTestHistoryManager.BuildArguments(Target(), GatewayTestHistoryManager.Operation.Cleanup, preview, 200, 192000, true));
        }

        [Test]
        public void ConfigureRequiresConfirmationAndExactConfigFingerprint()
        {
            Assert.Throws<ArgumentException>(() => GatewayTestHistoryManager.BuildArguments(Target(), GatewayTestHistoryManager.Operation.Configure, Snapshot(), 200, 192000, false));
            var args = GatewayTestHistoryManager.BuildArguments(Target(), GatewayTestHistoryManager.Operation.Configure, Snapshot(), 200, 192000, true);
            Assert.That(args, Does.Contain("--max-bytes 209715200").And.Contain("--max-bindings 192000").And.Contain("--confirm").And.Contain("--config-fingerprint sha256:"));
            Assert.That(args, Does.Not.Contain("--policy").And.Not.Contain("restart"));
        }

        [Test]
        public void CancelledSettingsDoNotWriteConfig()
        {
            WithWindow(window =>
            {
                window.RenderTestHistory(Snapshot());
                window.rootVisualElement.Q<IntegerField>("setup-test-history-size-mib").value = 201;
                window.HistoryConfirmationForTests = (title, message, accept, cancel) => false;
                var writes = 0;
                window.HistoryOperationForTests = (operation, binding, size, records, confirm, preserveBytes) => { writes++; return Task.FromResult(GatewayTestHistoryManager.Result.Fail("unexpected_write")); };
                window.SaveTestHistoryLimitsAsync().GetAwaiter().GetResult();
                Assert.That(writes, Is.Zero);
                Assert.That(window.rootVisualElement.Q<IntegerField>("setup-test-history-size-mib").value, Is.EqualTo(201));
            });
        }

        [Test]
        public void StatusRefreshDoesNotOverwriteUnsavedSettings()
        {
            WithWindow(window =>
            {
                window.RenderTestHistory(Snapshot());
                window.rootVisualElement.Q<IntegerField>("setup-test-history-size-mib").value = 300;
                window.RenderTestHistory(Snapshot());
                Assert.That(window.rootVisualElement.Q<IntegerField>("setup-test-history-size-mib").value, Is.EqualTo(300));
            });
        }

        [Test]
        public void MalformedOrMissingAdminDataFailsClosed()
        {
            foreach (var source in new[] { "", "{}", "{\"status\":\"ok\",\"data\":{}}", "{\"status\":\"error\"}", "not json" })
                Assert.That(GatewayTestHistoryManager.ParseResult(source).Success, Is.False, source);
        }

        [Test]
        public void MissingUsageAndMissingCleanupCountsNeverBecomeZero()
        {
            var status = AdminJson(Snapshot(), "status");
            Assert.That(GatewayTestHistoryManager.ParseResult(status).Success, Is.True);
            Assert.That(GatewayTestHistoryManager.ParseResult(status.Replace("\"bytes\":104857600,", "")).Success, Is.False);
            Assert.That(GatewayTestHistoryManager.ParseResult(status.Replace("\"bindings\":48000,", "")).Success, Is.False);
            // JsonUtility materializes a null serializable class as a zero-valued object;
            // use literal valid JSON with the entire removed object genuinely absent.
            const string missingRemoved = "{\"schema_version\":1,\"status\":\"ok\",\"action\":\"cleanup\",\"data\":{\"scope\":\"gateway_test_run_owner_metadata\",\"settings_source\":\"saved_config_restart_required\",\"bytes\":104857600,\"bindings\":48000,\"max_bytes\":209715200,\"max_bindings\":192000,\"retention_days\":30}}";
            Assert.That(GatewayTestHistoryManager.ParseResult(missingRemoved, GatewayTestHistoryManager.Operation.Cleanup).Success, Is.False);
            Assert.That(GatewayTestHistoryManager.ParseResult(status.Replace("\"bytes\":104857600", "\"bytes\":104857600,\"bytes\":0")).Success, Is.False);
        }

        [Test]
        public void SavedConfigurationWithUnavailableUsageIsReportedAsSavedNotEmpty()
        {
            var snapshot = Snapshot();
            snapshot.capacity_state = "unavailable";
            snapshot.configuration_saved = snapshot.restart_required = true;
            var json = AdminJson(snapshot, "configure").Replace("\"bytes\":104857600", "\"bytes\":null").Replace("\"bindings\":48000", "\"bindings\":null");
            var result = GatewayTestHistoryManager.ParseResult(json, GatewayTestHistoryManager.Operation.Configure);
            Assert.That(result.ConfigurationSaved, Is.True);
            Assert.That(result.Success, Is.False);
            WithWindow(window =>
            {
                window.RenderTestHistory(Snapshot());
                window.rootVisualElement.Q<IntegerField>("setup-test-history-record-limit").value = 192001;
                window.HistoryConfirmationForTests = (title, message, accept, cancel) => true;
                window.HistoryOperationForTests = (op, binding, size, records, confirm, preserve) => Task.FromResult(result);
                window.SaveTestHistoryLimitsAsync().GetAwaiter().GetResult();
                Assert.That(window.rootVisualElement.Q<Label>("setup-test-history-status").text, Does.Contain("saved").And.Contain("unavailable"));
                Assert.That(window.rootVisualElement.Q<ProgressBar>("setup-test-history-bytes").enabledSelf, Is.False);
            });
        }

        [Test]
        public void RecordOnlyEditPreservesExactFractionalMiBByteLimit()
        {
            WithWindow(window =>
            {
                var snapshot = Snapshot();
                snapshot.max_bytes += 17;
                window.RenderTestHistory(snapshot);
                window.rootVisualElement.Q<IntegerField>("setup-test-history-record-limit").value = 192001;
                window.HistoryConfirmationForTests = (title, message, accept, cancel) => true;
                GatewayTestHistoryModel.Snapshot requestedBinding = null;
                GatewayTestHistoryManager.Operation requestedOperation = default;
                var requestedSize = 0;
                var requestedRecords = 0;
                var requestedConfirm = false;
                var requestedPreserve = false;
                window.HistoryOperationForTests = (op, binding, size, records, confirm, preserve) =>
                {
                    requestedBinding = binding;
                    requestedOperation = op;
                    requestedSize = size;
                    requestedRecords = records;
                    requestedConfirm = confirm;
                    requestedPreserve = preserve;
                    return Task.FromResult(new GatewayTestHistoryManager.Result { status = "ok", data = snapshot });
                };
                window.SaveTestHistoryLimitsAsync().GetAwaiter().GetResult();
                Assert.That(requestedPreserve, Is.True);
                Assert.That(requestedBinding, Is.SameAs(snapshot));
                var args = GatewayTestHistoryManager.BuildArguments(Target(), requestedOperation, requestedBinding, requestedSize, requestedRecords, requestedConfirm, requestedPreserve);
                Assert.That(args, Does.Contain("--max-bytes 209715217").And.Contain("--max-bindings 192001"));
            });
        }

        [Test]
        public void RebuildPreservesPendingAdmissionAndDoesNotAuthorizeOldPreview()
        {
            WithWindow(window =>
            {
                var completion = new TaskCompletionSource<GatewayTestHistoryManager.Result>();
                var calls = 0;
                var confirmations = 0;
                window.HistoryConfirmationForTests = (title, message, accept, cancel) => { confirmations++; return false; };
                window.HistoryOperationForTests = (op, binding, size, records, confirm, preserve) => { calls++; return completion.Task; };
                var operation = window.ClearTestHistoryAsync();
                window.CreateGUI();
                Assert.That(window.rootVisualElement.Q<Button>("setup-test-history-refresh").enabledSelf, Is.False);
                window.ClearTestHistoryAsync().GetAwaiter().GetResult();
                Assert.That(calls, Is.EqualTo(1));
                completion.SetResult(new GatewayTestHistoryManager.Result { status = "ok", data = Snapshot() });
                Assert.That(operation.IsCompleted, Is.True);
                Assert.That(confirmations, Is.Zero);
                Assert.That(calls, Is.EqualTo(1));
                Assert.That(window.rootVisualElement.Q<Button>("setup-test-history-refresh").enabledSelf, Is.True);
                Assert.That(window.rootVisualElement.Q<Button>("setup-test-history-clear").enabledSelf, Is.False);
            });
        }

        [Test]
        public void DestroyedFixtureCannotConfirmAnOutstandingPreview()
        {
            WithWindow(window =>
            {
                var completion = new TaskCompletionSource<GatewayTestHistoryManager.Result>();
                var calls = 0;
                var confirmations = 0;
                window.HistoryOperationForTests = (op, binding, size, records, confirm, preserve) => { calls++; return completion.Task; };
                window.HistoryConfirmationForTests = (title, message, accept, cancel) => { confirmations++; return false; };
                var operation = window.ClearTestHistoryAsync();
                UnityEngine.Object.DestroyImmediate(window);
                completion.SetResult(new GatewayTestHistoryManager.Result { status = "ok", data = Snapshot() });
                Assert.That(operation.IsCompleted, Is.True);
                Assert.That(confirmations, Is.Zero);
                Assert.That(calls, Is.EqualTo(1));
            });
        }

        static string AdminJson(GatewayTestHistoryModel.Snapshot snapshot, string action)
            => "{\"schema_version\":1,\"status\":\"ok\",\"action\":\"" + action + "\",\"data\":" + JsonUtility.ToJson(snapshot) + "}";

        static GatewayTestHistoryManager.Target Target() => new GatewayTestHistoryManager.Target { CliPath = "C:/fixture/gateway-cli.mjs", ConfigPath = "C:/fixture with spaces/gateway.json" };

        static void WithWindow(Action<UnityMcpSetupWindow> action)
        {
            var previous = UnityMcpSetupWindow.SkipAutomaticRefreshForTests;
            var language = McpWindowLocalization.CurrentLanguage;
            UnityMcpSetupWindow window = null;
            try
            {
                McpWindowLocalization.CurrentLanguage = "en";
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
                window = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
                window.Show();
                window.CreateGUI();
                Assert.That(window.rootVisualElement.panel, Is.Not.Null, "Field change regressions require a real Editor panel, not a detached tree.");
                action(window);
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = previous;
                McpWindowLocalization.CurrentLanguage = language;
            }
        }
    }
}
