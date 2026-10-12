using System.Collections.Generic;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.State;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class Harden08CStateRestorationTests
    {
        const string Hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        readonly List<UnityMcpSetupWindow> m_windows = new List<UnityMcpSetupWindow>();

        [SetUp]
        public void SetUp()
        {
            UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
        }

        [TearDown]
        public void TearDown()
        {
            for (var index = 0; index < m_windows.Count; index++)
            {
                if (m_windows[index] != null)
                {
                    Object.DestroyImmediate(m_windows[index]);
                }
            }
            m_windows.Clear();
            UnityMcpSetupWindow.SkipAutomaticRefreshForTests = false;
        }

        [TestCase("Diagnostics", "Diagnostics")]
        [TestCase("Commands", "Commands")]
        public void EditorSerializationRestoresSelectedTabIntoRecreatedGui(string tabName, string toggleText)
        {
            var source = CreateTestWindow();
            source.CreateGUI();
            source.SelectTabForCommand(tabName);

            var json = EditorJsonUtility.ToJson(source);
            Assert.That(json, Does.Contain("\"m_windowState\":{\"m_selectedTab\":\"" + tabName + "\""));
            Assert.That(json, Does.Contain("\"m_currentTab\":\"\""));
            Assert.That(json, Does.Not.Contain("m_lastMcpWindowTimingSummary"));

            var restored = CreateTestWindow();
            EditorJsonUtility.FromJsonOverwrite(json, restored);
            restored.CreateGUI();

            Assert.That(restored.CurrentTabForCommand(), Is.EqualTo(tabName));
            var selected = new List<ToolbarToggle>();
            restored.rootVisualElement.Query<ToolbarToggle>().ForEach(toggle =>
            {
                if (toggle.value)
                {
                    selected.Add(toggle);
                }
            });
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0].text, Is.EqualTo(toggleText));
        }

        [Test]
        public void UnsupportedSerializedTabFallsBackToSetup()
        {
            var restored = CreateTestWindow();
            EditorJsonUtility.FromJsonOverwrite("{\"m_windowState\":{\"m_selectedTab\":\"Secrets\"}}", restored);

            restored.CreateGUI();

            Assert.That(restored.CurrentTabForCommand(), Is.EqualTo("Setup"));
            var selectedCount = 0;
            restored.rootVisualElement.Query<ToolbarToggle>().ForEach(toggle => selectedCount += toggle.value ? 1 : 0);
            Assert.That(selectedCount, Is.EqualTo(1));
        }

        [Test]
        public void NullSerializedWindowStateFallsBackToSetup()
        {
            var restored = CreateTestWindow();
            EditorJsonUtility.FromJsonOverwrite("{\"m_windowState\":null}", restored);

            Assert.That(restored.CurrentTabForCommand(), Is.EqualTo("Setup"));
            Assert.DoesNotThrow(restored.CreateGUI);
        }

        [Test]
        public void LegacyRootTabMigratesToNestedStateAndRecreatedGui()
        {
            var restored = CreateTestWindow();
            typeof(UnityMcpSetupWindow)
                .GetField("m_windowState", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(restored, null);
            typeof(UnityMcpSetupWindow)
                .GetField("m_currentTab", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
                .SetValue(restored, "Diagnostics");

            restored.CreateGUI();

            Assert.That(restored.CurrentTabForCommand(), Is.EqualTo("Diagnostics"));
            AssertSingleSelectedTab(restored, "Diagnostics");
            var migratedJson = EditorJsonUtility.ToJson(restored);
            Assert.That(migratedJson, Does.Contain("\"m_windowState\":{\"m_selectedTab\":\"Diagnostics\""));
            Assert.That(migratedJson, Does.Contain("\"m_currentTab\":\"\""));
        }

        [Test]
        public void InvalidLegacyRootTabFallsBackToSetupAndClearsLegacyState()
        {
            var restored = CreateTestWindow();
            EditorJsonUtility.FromJsonOverwrite("{\"m_currentTab\":\"Secrets\"}", restored);

            restored.CreateGUI();

            Assert.That(restored.CurrentTabForCommand(), Is.EqualTo("Setup"));
            AssertSingleSelectedTab(restored, "Setup");
            Assert.That(EditorJsonUtility.ToJson(restored), Does.Contain("\"m_currentTab\":\"\""));
        }

        [Test]
        public void ExplicitNestedSelectionWinsOverMixedLegacyStateAndClearsLegacy()
        {
            var restored = CreateTestWindow();
            EditorJsonUtility.FromJsonOverwrite("{\"m_currentTab\":\"Diagnostics\",\"m_windowState\":{\"m_selectedTab\":\"Setup\"}}", restored);

            restored.CreateGUI();

            Assert.That(restored.CurrentTabForCommand(), Is.EqualTo("Setup"));
            AssertSingleSelectedTab(restored, "Setup");
            var migratedJson = EditorJsonUtility.ToJson(restored);
            Assert.That(migratedJson, Does.Contain("\"m_windowState\":{\"m_selectedTab\":\"Setup\"}"));
            Assert.That(migratedJson, Does.Contain("\"m_currentTab\":\"\""));
        }

        static void AssertSingleSelectedTab(UnityMcpSetupWindow window, string expectedText)
        {
            var selected = new List<ToolbarToggle>();
            window.rootVisualElement.Query<ToolbarToggle>().ForEach(toggle =>
            {
                if (toggle.value)
                {
                    selected.Add(toggle);
                }
            });
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0].text, Is.EqualTo(expectedText));
        }

        [Test]
        public void BridgeLifecycleTransitionsWithoutHeartbeatOrListener()
        {
            var state = new BridgeRuntimeState();
            Assert.That(state.Status, Is.EqualTo("starting"));

            state.MarkReloading();
            Assert.That(state.Status, Is.EqualTo("reloading"));
            state.MarkWaitingForIdle();
            Assert.That(state.Status, Is.EqualTo("waiting_for_idle"));
            state.MarkStopping();
            Assert.That(state.Status, Is.EqualTo("stopping"));
            Assert.That(state.IsReady, Is.False);
        }

        [Test]
        public void PackageStalenessIsUpdatingAndJsonContainsBoundedCurrentAndPendingState()
        {
            var state = new BridgeRuntimeState();
            state.PublishCatalog("catalog-a", 1);
            state.SetBridgeListening(true);
            state.SetPackageUpdateStatus("stale_package_resolution", "Refresh required.", true, "current-fingerprint", "pending-fingerprint", 3, "2026-07-12T00:00:00Z");

            Assert.That(state.Status, Is.EqualTo("updating"));
            Assert.That(state.IsReady, Is.False);
            Assert.That(state.StalePackageResolution, Is.True);
            var json = state.PackageUpdateJson();
            Assert.That(json.Length, Is.LessThan(1024));
            Assert.That(json, Does.Contain("\"fingerprint\":\"current-fingerprint\""));
            Assert.That(json, Does.Contain("\"pending_fingerprint\":\"pending-fingerprint\""));
            Assert.That(json, Does.Contain("\"package_refresh_required\":true"));
            Assert.That(state.PackageLastObservedUtc, Is.Not.Empty);
            Assert.That(state.PackageLastTransitionUtc, Is.EqualTo(state.PackageLastObservedUtc));
            Assert.That(json, Does.Contain("\"last_observed_utc\":"));
            Assert.That(json, Does.Contain("\"last_transition_utc\":"));

            state.SetPackageUpdateStatus("current", "Current.", false, "pending-fingerprint", string.Empty, 4, "2026-07-12T00:01:00Z");
            Assert.That(state.Status, Is.EqualTo("ready"));
            Assert.That(state.PackageRefreshPending, Is.False);
            Assert.That(state.PackageUpdateFingerprint, Is.EqualTo("pending-fingerprint"));
            Assert.That(state.PackageUpdatePendingFingerprint, Is.Empty);

            state.SetPackageUpdateStatus("current", new string('x', 5000), false, "current", string.Empty, 4, string.Empty);
            Assert.That(state.PackageUpdateJson().Length, Is.LessThan(1024));

            state.SetPackageUpdateStatus("unavailable", "Project package metadata unavailable.", true, "current", "pending", 4, string.Empty, false, "project_packages", "budget_exhausted", "packages_lock");
            Assert.That(state.PackageMetadataUnavailable, Is.False);
            Assert.That(state.PackageUpdateJson(), Does.Contain("\"fingerprint_failure\":{\"evidence_class\":\"project_packages\",\"reason\":\"budget_exhausted\",\"file\":\"packages_lock\"}"));

            state.SetPackageUpdateStatus("unavailable", "Local metadata unavailable.", true, "current", "pending", 4, string.Empty, true);
            Assert.That(state.PackageMetadataUnavailable, Is.True);
            Assert.That(state.PackageUpdateJson(), Does.Contain("\"fingerprint_failure\":null"));
            Assert.That(state.StalePackageResolution, Is.False);
            Assert.That(state.PackageRefreshRequired, Is.True);
        }

        [Test]
        public void PackageUpdateJsonBoundsEncodedAdversarialFieldsAndRemainsValid()
        {
            var adversarial = new string('\u0001', 300) + new string('\\', 300) + new string('"', 300) + "\n\r\t";
            var currentFingerprint = new string('a', 64);
            var pendingFingerprint = new string('b', 64);
            var sourceFingerprint = new string('c', 64);
            var assetDatabaseFingerprint = new string('d', 64);
            var sourceStateFingerprint = new string('e', 64);
            var state = new BridgeRuntimeState();
            state.SetPackageUpdateStatus("stale_package_resolution", adversarial, true, currentFingerprint, pendingFingerprint, int.MaxValue, "2026-08-09T10:58:14.7057536Z", false, "project_packages", "budget_exhausted", "packages_lock");
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Available(int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, adversarial, sourceFingerprint, assetDatabaseFingerprint, sourceStateFingerprint));

            var json = state.PackageUpdateJson();

            Assert.That(json.Length, Is.LessThan(1024));
            Assert.That(json, Does.Contain("\"status\":"));
            Assert.That(json, Does.Contain("\"fingerprint\":"));
            Assert.That(json, Does.Contain("\"pending_fingerprint\":"));
            Assert.That(json, Does.Contain("\"last_refresh_utc\":"));
            Assert.That(json, Does.Contain("\"last_observed_utc\":"));
            Assert.That(json, Does.Contain("\"last_transition_utc\":"));
            Assert.That(json, Does.Contain("\"message\":"));
            var parsed = JsonUtility.FromJson<PackageUpdatePayload>(json);
            Assert.That(parsed, Is.Not.Null);
            Assert.That(parsed.status, Is.EqualTo("stale_package_resolution"));
            Assert.That(parsed.fingerprint, Is.Not.Empty.And.Not.EqualTo(adversarial));
            Assert.That(parsed.pending_fingerprint, Is.Not.Empty.And.Not.EqualTo(adversarial));
            Assert.That(parsed.last_refresh_utc, Is.Empty);
            Assert.That(parsed.message, Is.Not.Empty.And.Not.EqualTo(adversarial));
            Assert.That(parsed.local_package_code_inventory, Is.Not.Null);
            Assert.That(parsed.local_package_code_inventory.local_package_count, Is.EqualTo(64));
            Assert.That(parsed.local_package_code_inventory.source_file_count, Is.EqualTo(16384));
            Assert.That(parsed.local_package_code_inventory.asset_database_file_count, Is.EqualTo(16384));
            Assert.That(parsed.local_package_code_inventory.missing_asset_file_count, Is.EqualTo(16384));
            Assert.That(parsed.local_package_code_inventory.unexpected_asset_file_count, Is.EqualTo(16384));
            Assert.That(json, Does.Contain("\"fingerprint_failure\":null"));
            Assert.That(json, Does.Contain("\"failure_reason\":\"\""));
            Assert.That(json, Does.Contain("\"mismatch_package\":\"\""));
            Assert.That(json, Does.Contain("\"fingerprint\":\"" + currentFingerprint + "\""));
            Assert.That(json, Does.Contain("\"pending_fingerprint\":\"" + pendingFingerprint + "\""));
            Assert.That(json, Does.Contain("\"source_fingerprint\":\"" + sourceFingerprint + "\""));
            Assert.That(json, Does.Contain("\"asset_database_fingerprint\":\"" + assetDatabaseFingerprint + "\""));
            Assert.That(json, Does.Contain("\"source_state_fingerprint\":\"" + sourceStateFingerprint + "\""));
            Assert.That(parsed.refresh_count, Is.EqualTo(int.MaxValue));
            Assert.That(parsed.refresh_pending, Is.True);
        }

        [Test]
        public void PackageUpdateJsonIncludesCompleteCompilerSourceEvidenceWhenItFits()
        {
            var state = new BridgeRuntimeState();
            state.SetPackageUpdateStatus("current", "Current.", false, "current", string.Empty, 1, string.Empty);
            state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Available(
                1, 2, 2, 0, 0, string.Empty, "source", "assets", "state", 2, 0, 0, "compiler"));

            var json = state.PackageUpdateJson();
            var parsed = JsonUtility.FromJson<PackageUpdatePayload>(json);

            Assert.That(json.Length, Is.LessThan(1024));
            Assert.That(parsed.local_package_code_inventory.compiler_source_file_count, Is.EqualTo(2));
            Assert.That(parsed.local_package_code_inventory.missing_compiler_source_file_count, Is.Zero);
            Assert.That(parsed.local_package_code_inventory.unexpected_compiler_source_file_count, Is.Zero);
            Assert.That(json, Does.Contain("\"compiler_source_fingerprint\":\"compiler\""));
        }

        [TestCase(false, false, false, false, "ready")]
        [TestCase(true, false, false, false, "compiling")]
        [TestCase(false, true, false, false, "updating")]
        [TestCase(false, false, true, false, "playing")]
        [TestCase(false, false, false, true, "updating")]
        [TestCase(true, true, true, true, "compiling")]
        public void BridgeLifecycleResolverPreservesProductionPriority(bool compiling, bool updating, bool playing, bool refreshPending, string expected)
        {
            Assert.That(BridgeRuntimeState.ResolveLifecycleStatus(compiling, updating, playing, refreshPending), Is.EqualTo(expected));
        }

        [TestCase("package_metadata")]
        [TestCase("installed_metadata")]
        [TestCase("version")]
        [TestCase("manifest")]
        [TestCase("count")]
        [TestCase("cli_hash")]
        [TestCase("legal_hash")]
        [TestCase("notices_hash")]
        [TestCase("sbom_hash")]
        [TestCase("presentation_manifest_hash")]
        [TestCase("presentation_package_hash")]
        [TestCase("presentation_server_hash")]
        [TestCase("missing_cli")]
        public void RuntimeFreshnessRejectsEachStaleContractField(string mismatch)
        {
            var status = CurrentRuntimeStatus();
            var metadataEqual = true;
            switch (mismatch)
            {
                case "package_metadata": status.PackageMetadataValid = false; break;
                case "installed_metadata": status.InstalledMetadataValid = false; break;
                case "version": status.InstalledRuntimeVersion = "stale"; break;
                case "manifest": metadataEqual = false; break;
                case "count": status.InstalledGatewayVirtualCommandCount--; break;
                case "cli_hash": status.InstalledCliSha256 = "stale"; break;
                case "legal_hash": status.InstalledLegalSha256 = "stale"; break;
                case "notices_hash": status.InstalledNoticesSha256 = "stale"; break;
                case "sbom_hash": status.InstalledSbomSha256 = "stale"; break;
                case "presentation_manifest_hash": status.InstalledOpenCodePresentationManifestSha256 = "stale"; break;
                case "presentation_package_hash": status.InstalledOpenCodePresentationPackageSha256 = "stale"; break;
                case "presentation_server_hash": status.InstalledOpenCodePresentationServerSha256 = "stale"; break;
                case "missing_cli": status.HasBundledCli = false; break;
            }

            Assert.That(GatewayInstaller.RuntimeStateMatchesPackage(status, metadataEqual), Is.False);
        }

        [Test]
        public void RuntimeFreshnessAcceptsCurrentContract()
        {
            Assert.That(GatewayInstaller.RuntimeStateMatchesPackage(CurrentRuntimeStatus(), true), Is.True);
        }

        [Test]
        public void RuntimeDowngradeAdmissionRequiresExactVersionAndContentConfirmation()
        {
            var status = CurrentRuntimeStatus();
            status.RuntimeFresh = false;
            status.InstalledRuntimeVersion = "2026.8.9-2";
            status.PackageRuntimeVersion = "2026.8.8-4";
            status.InstalledCliSha256 = new string('a', 64);
            status.PackageCliSha256 = new string('b', 64);

            Assert.That(GatewayInstaller.ValidateRuntimeInstallAdmission(status, null, out var missingConfirmation), Is.False);
            StringAssert.Contains("explicitly confirm", missingConfirmation);
            var confirmation = GatewayInstaller.CreateRuntimeDowngradeConfirmation(status);
            Assert.That(GatewayInstaller.ValidateRuntimeInstallAdmission(status, confirmation, out var accepted), Is.True, accepted);
            status.InstalledCliSha256 = new string('c', 64);
            Assert.That(GatewayInstaller.ValidateRuntimeInstallAdmission(status, confirmation, out var changed), Is.False);
            StringAssert.Contains("content changed", changed);

            status.InstalledCliSha256 = new string('a', 64);
            status.PackageNoticesSha256 = new string('d', 64);
            Assert.That(GatewayInstaller.ValidateRuntimeInstallAdmission(status, confirmation, out var sidecarChanged), Is.False);
            StringAssert.Contains("content changed", sidecarChanged);
        }

        [TestCase("installed_manifest")]
        [TestCase("package_manifest")]
        [TestCase("installed_package")]
        [TestCase("package_package")]
        [TestCase("installed_server")]
        [TestCase("package_server")]
        public void RuntimeDowngradeConfirmationBindsPresentationArtifacts(string mismatch)
        {
            var status = CurrentRuntimeStatus();
            status.RuntimeFresh = false;
            status.InstalledRuntimeVersion = "2026.8.9-2";
            status.PackageRuntimeVersion = "2026.8.8-4";
            var confirmation = GatewayInstaller.CreateRuntimeDowngradeConfirmation(status);

            switch (mismatch)
            {
                case "installed_manifest": status.InstalledOpenCodePresentationManifestSha256 = "stale"; break;
                case "package_manifest": status.PackageOpenCodePresentationManifestSha256 = "stale"; break;
                case "installed_package": status.InstalledOpenCodePresentationPackageSha256 = "stale"; break;
                case "package_package": status.PackageOpenCodePresentationPackageSha256 = "stale"; break;
                case "installed_server": status.InstalledOpenCodePresentationServerSha256 = "stale"; break;
                case "package_server": status.PackageOpenCodePresentationServerSha256 = "stale"; break;
            }

            Assert.That(GatewayInstaller.ValidateRuntimeInstallAdmission(status, confirmation, out var message), Is.False);
            StringAssert.Contains("content changed", message);
        }

        [Test]
        public void RuntimeInstallAdmissionFailsClosedWhenVersionDirectionIsInvalid()
        {
            var status = CurrentRuntimeStatus();
            status.RuntimeFresh = false;
            status.InstalledRuntimeVersion = "invalid";
            status.PackageRuntimeVersion = "2026.8.9-2";

            Assert.That(GatewayInstaller.ValidateRuntimeInstallAdmission(status, null, out var message), Is.False);
            StringAssert.Contains("could not be determined", message);

            Assert.That(GatewayInstaller.ValidateRuntimeInstallAdmission(null, null, out var unavailable), Is.False);
            StringAssert.Contains("status is unavailable", unavailable);
        }

        [Test]
        public void GatewayActionDecisionsCoverSafeHealthTransitions()
        {
            var install = new GatewayInstaller.GatewayInstallStatus { CanStart = true, InstalledRuntimeVersion = "current" };
            Assert.That(McpRuntimeHealthViewState.Evaluate(null, install).HealthRefreshActionNeeded, Is.True);
            Assert.That(McpRuntimeHealthViewState.Evaluate(new GatewayProcessManager.GatewayRuntimeStatus(), install).HealthRefreshActionNeeded, Is.True);

            var healthy = HealthyStatus();
            var health = McpRuntimeHealthViewState.Evaluate(healthy, install);
            Assert.That(health.HealthRefreshActionNeeded, Is.False);
            Assert.That(health.ProcessNeedsRestart, Is.False);
            Assert.That(health.StartOrRestartActionNeeded, Is.False);

            healthy.HealthVersion = "stale";
            health = McpRuntimeHealthViewState.Evaluate(healthy, install);
            Assert.That(health.VersionMismatch, Is.True);
            Assert.That(health.ProcessNeedsRestart, Is.True);
            Assert.That(health.StartOrRestartActionNeeded, Is.True);

            healthy = HealthyStatus();
            healthy.InvalidProjectRecordsCount = 1;
            Assert.That(McpRuntimeHealthViewState.Evaluate(healthy, install).ProcessNeedsRestart, Is.False);

            healthy = HealthyStatus();
            healthy.StaleProjectRecordsCount = 1;
            healthy.StaleProjectsCount = 1;
            Assert.That(McpRuntimeHealthViewState.Evaluate(healthy, install).HealthRefreshActionNeeded, Is.True);

            healthy = HealthyStatus();
            healthy.PolicyDegraded = true;
            Assert.That(McpRuntimeHealthViewState.Evaluate(healthy, install).HealthRefreshActionNeeded, Is.True);
            healthy.PolicyDegraded = false;
            healthy.PolicyMetadataMalformed = true;
            Assert.That(McpRuntimeHealthViewState.Evaluate(healthy, install).HealthRefreshActionNeeded, Is.True);
            healthy.PolicyMetadataMalformed = false;
            Assert.That(McpRuntimeHealthViewState.Evaluate(healthy, install, true).HealthRefreshActionNeeded, Is.True);
        }

        [Test]
        public void SetActionNeededOnlyChangesClassAndTooltipAndIsNullSafe()
        {
            var button = new Button();
            button.SetEnabled(false);

            Assert.DoesNotThrow(() => UnityMcpSetupWindow.SetActionNeeded(null, true, "ignored"));
            UnityMcpSetupWindow.SetActionNeeded(button, true, "Refresh required");
            Assert.That(button.ClassListContains("mcp-action-needed"), Is.True);
            Assert.That(button.tooltip, Is.EqualTo("Refresh required"));
            Assert.That(button.enabledSelf, Is.False);

            UnityMcpSetupWindow.SetActionNeeded(button, false, "ignored");
            Assert.That(button.ClassListContains("mcp-action-needed"), Is.False);
            Assert.That(button.tooltip, Is.Empty);
            Assert.That(button.enabledSelf, Is.False);
        }

        UnityMcpSetupWindow CreateTestWindow()
        {
            var window = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
            m_windows.Add(window);
            return window;
        }

        static GatewayInstaller.GatewayInstallStatus CurrentRuntimeStatus()
        {
            return new GatewayInstaller.GatewayInstallStatus
            {
                HasBundledCli = true,
                PackageMetadataValid = true,
                InstalledMetadataValid = true,
                PackageRuntimeVersion = "current",
                InstalledRuntimeVersion = "current",
                PackageGatewayVirtualCommandCount = 54,
                InstalledGatewayVirtualCommandCount = 54,
                PackageCliSha256 = Hash,
                InstalledCliSha256 = Hash,
                PackageLegalSha256 = Hash,
                InstalledLegalSha256 = Hash,
                PackageNoticesSha256 = Hash,
                InstalledNoticesSha256 = Hash,
                PackageSbomSha256 = Hash,
                InstalledSbomSha256 = Hash,
                PackageOpenCodePresentationManifestSha256 = Hash,
                InstalledOpenCodePresentationManifestSha256 = Hash,
                PackageOpenCodePresentationPackageSha256 = Hash,
                InstalledOpenCodePresentationPackageSha256 = Hash,
                PackageOpenCodePresentationServerSha256 = Hash,
                InstalledOpenCodePresentationServerSha256 = Hash,
            };
        }

        static GatewayProcessManager.GatewayRuntimeStatus HealthyStatus()
        {
            return new GatewayProcessManager.GatewayRuntimeStatus
            {
                HealthProbed = true,
                HealthOk = true,
                HealthVersion = "current",
            };
        }

        [System.Serializable]
        sealed class PackageUpdatePayload
        {
            public string status;
            public bool refresh_pending;
            public string fingerprint;
            public string pending_fingerprint;
            public int refresh_count;
            public string last_refresh_utc;
            public PackageFingerprintFailurePayload fingerprint_failure;
            public LocalPackageCodeInventoryPayload local_package_code_inventory;
            public string message;
        }

        [System.Serializable]
        sealed class LocalPackageCodeInventoryPayload
        {
            public int local_package_count;
            public int source_file_count;
            public int asset_database_file_count;
            public int missing_asset_file_count;
            public int unexpected_asset_file_count;
            public int compiler_source_file_count;
            public int missing_compiler_source_file_count;
            public int unexpected_compiler_source_file_count;
        }

        [System.Serializable]
        sealed class PackageFingerprintFailurePayload
        {
            public string evidence_class;
            public string reason;
            public string file;
        }
    }
}
