using System.Collections.Generic;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI.State;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class Harden09A1WindowStateTests
    {
        [TestCase(null)]
        [TestCase("")]
        [TestCase("Secrets")]
        public void WindowStateNormalizesUnsupportedSelectionToSetup(string tabName)
        {
            var state = new McpWindowState();

            Assert.That(state.SelectTab(tabName), Is.EqualTo(McpWindowState.DefaultTab));
            Assert.That(state.SelectedTab, Is.EqualTo(McpWindowState.DefaultTab));
        }

        [TestCase("Setup")]
        [TestCase("FTUE")]
        [TestCase("Status")]
        [TestCase("Permissions")]
        [TestCase("Commands")]
        [TestCase("Plans")]
        [TestCase("Skills")]
        [TestCase("Unity AI Assistant")]
        [TestCase("Diagnostics")]
        public void WindowStateSelectsEverySupportedTab(string tabName)
        {
            var state = new McpWindowState();

            Assert.That(McpWindowState.IsSupportedTab(tabName), Is.True);
            Assert.That(state.SelectTab(tabName), Is.EqualTo(tabName));
            Assert.That(state.SelectedTab, Is.EqualTo(tabName));
        }

        [Test]
        public void WindowStateUnityJsonRoundtripPreservesNormalizedSelection()
        {
            var source = new McpWindowState();
            source.SelectTab("Diagnostics");

            var json = JsonUtility.ToJson(source);
            var restored = JsonUtility.FromJson<McpWindowState>(json);

            Assert.That(json, Does.Contain("\"m_selectedTab\":\"Diagnostics\""));
            Assert.That(restored.SelectedTab, Is.EqualTo("Diagnostics"));

            restored = JsonUtility.FromJson<McpWindowState>("{\"m_selectedTab\":null}");
            Assert.That(restored.SelectedTab, Is.EqualTo(McpWindowState.DefaultTab));
        }

        [Test]
        public void FtueCompletionRequiresEveryRequiredStateIncludingOneProfileAndSmokeEvidence()
        {
            var state = CompleteFtueState();

            Assert.That(state.IsComplete, Is.True);

            state.PermissionProfileReady = false;
            Assert.That(state.IsComplete, Is.False);
            state.PermissionProfileReady = true;

            state.SmokeTestReady = false;
            Assert.That(state.IsComplete, Is.False);
        }

        [TestCase("127.0.0.1", false, false, "", true)]
        [TestCase("localhost", false, false, "", true)]
        [TestCase("0.0.0.0", false, true, "http://trusted", false)]
        [TestCase("0.0.0.0", true, false, "http://trusted", false)]
        [TestCase("0.0.0.0", true, true, "", false)]
        [TestCase("0.0.0.0", true, true, "http://trusted", true)]
        public void FtueBindSecurityUsesActualGatewayBind(string host, bool allowNonLoopback, bool tokenPresent, string allowedOrigins, bool expected)
        {
            Assert.That(GatewayNetworkModeManager.IsBindSecurityReady(host, allowNonLoopback, tokenPresent, allowedOrigins), Is.EqualTo(expected));
        }

        [Test]
        public void FtueProfileFingerprintIsScopedToCurrentRulesAndIgnoresOrdering()
        {
            var first = McpFtueProfileTracker.RulesFingerprint(
                new List<string> { "compile.status", "logs.*" },
                new List<string> { "asset.*" },
                new List<string>());
            var reordered = McpFtueProfileTracker.RulesFingerprint(
                new List<string> { "logs.*", "compile.status" },
                new List<string> { "asset.*" },
                new List<string>());
            var changed = McpFtueProfileTracker.RulesFingerprint(
                new List<string> { "compile.status", "logs.*" },
                new List<string>(),
                new List<string> { "asset.*" });

            Assert.That(reordered, Is.EqualTo(first));
            Assert.That(changed, Is.Not.EqualTo(first));
        }

        static McpFtueState CompleteFtueState()
        {
            return new McpFtueState
            {
                UnityBridgeReady = true,
                PortableNodeReady = true,
                GatewayRuntimeReady = true,
                GatewayProcessReady = true,
                TokenContractReady = true,
                ClientConfigReady = true,
                PermissionsReady = true,
                PermissionProfileReady = true,
                SmokeTestReady = true,
            };
        }

        [Test]
        public void RuntimeHealthCurrentRuntimeNeedsNoAction()
        {
            var state = McpRuntimeHealthViewState.Evaluate(HealthyStatus(), InstallStatus(), false);

            Assert.That(state.VersionMismatch, Is.False);
            Assert.That(state.ActionableStaleProjects, Is.False);
            Assert.That(state.ProcessNeedsRestart, Is.False);
            Assert.That(state.StartOrRestartActionNeeded, Is.False);
            Assert.That(state.HealthRefreshActionNeeded, Is.False);
            Assert.That(state.CurrentHealthyRuntime, Is.True);
        }

        [Test]
        public void RuntimeHealthVersionMismatchRequiresRestartAndInvalidRecordsDoNot()
        {
            var process = HealthyStatus();
            process.HealthVersion = "old";
            var mismatch = McpRuntimeHealthViewState.Evaluate(process, InstallStatus());
            Assert.That(mismatch.VersionMismatch, Is.True);
            Assert.That(mismatch.ProcessNeedsRestart, Is.True);
            Assert.That(mismatch.StartOrRestartActionNeeded, Is.True);
            Assert.That(mismatch.CurrentHealthyRuntime, Is.False);

            process = HealthyStatus();
            process.InvalidProjectRecordsCount = 1;
            var invalid = McpRuntimeHealthViewState.Evaluate(process, InstallStatus());
            Assert.That(invalid.VersionMismatch, Is.False);
            Assert.That(invalid.ProcessNeedsRestart, Is.False);
            Assert.That(invalid.StartOrRestartActionNeeded, Is.False);
            Assert.That(invalid.CurrentHealthyRuntime, Is.True);
        }

        [Test]
        public void RuntimeHealthStaleRecordsRespectLiveCoverage()
        {
            var process = HealthyStatus();
            process.StaleProjectRecordsCount = 1;
            var withoutCoverage = McpRuntimeHealthViewState.Evaluate(process, InstallStatus());
            Assert.That(withoutCoverage.ActionableStaleProjects, Is.True);
            Assert.That(withoutCoverage.HealthRefreshActionNeeded, Is.True);
            Assert.That(withoutCoverage.CurrentHealthyRuntime, Is.False);

            process.LiveProjectRecordsCount = 1;
            var covered = McpRuntimeHealthViewState.Evaluate(process, InstallStatus());
            Assert.That(covered.ActionableStaleProjects, Is.False);
            Assert.That(covered.HealthRefreshActionNeeded, Is.False);
            Assert.That(covered.CurrentHealthyRuntime, Is.True);

            process.StaleProjectsCount = 1;
            var historical = McpRuntimeHealthViewState.Evaluate(process, InstallStatus());
            Assert.That(historical.ActionableStaleProjects, Is.False);
            Assert.That(historical.HealthRefreshActionNeeded, Is.False);
            Assert.That(historical.CurrentHealthyRuntime, Is.True);
        }

        [Test]
        public void RuntimeHealthPolicyHeartbeatAndUnavailableHealthRequireRefreshOnly()
        {
            var process = HealthyStatus();
            process.PolicyDegraded = true;
            AssertRefreshOnly(process, false);

            process = HealthyStatus();
            process.PolicyMetadataMalformed = true;
            AssertRefreshOnly(process, false);

            AssertRefreshOnly(HealthyStatus(), true, true);

            process = new GatewayProcessManager.GatewayRuntimeStatus { HealthProbed = true, HealthOk = false };
            var noHealth = McpRuntimeHealthViewState.Evaluate(process, InstallStatus());
            Assert.That(noHealth.HealthRefreshActionNeeded, Is.True);
            Assert.That(noHealth.StartOrRestartActionNeeded, Is.True);
            Assert.That(noHealth.ProcessNeedsRestart, Is.False);
            Assert.That(noHealth.CurrentHealthyRuntime, Is.False);

            var missing = McpRuntimeHealthViewState.Evaluate(null, InstallStatus());
            Assert.That(missing.HealthRefreshActionNeeded, Is.True);
            Assert.That(missing.StartOrRestartActionNeeded, Is.False);
            Assert.That(missing.ProcessNeedsRestart, Is.False);
            Assert.That(missing.CurrentHealthyRuntime, Is.False);
        }

        static void AssertRefreshOnly(GatewayProcessManager.GatewayRuntimeStatus process, bool localHeartbeatStale, bool currentHealthyRuntime = false)
        {
            var state = McpRuntimeHealthViewState.Evaluate(process, InstallStatus(), localHeartbeatStale);
            Assert.That(state.HealthRefreshActionNeeded, Is.True);
            Assert.That(state.ProcessNeedsRestart, Is.False);
            Assert.That(state.StartOrRestartActionNeeded, Is.False);
            Assert.That(state.CurrentHealthyRuntime, Is.EqualTo(currentHealthyRuntime));
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

        static GatewayInstaller.GatewayInstallStatus InstallStatus()
        {
            return new GatewayInstaller.GatewayInstallStatus
            {
                CanStart = true,
                InstalledRuntimeVersion = "current",
            };
        }
    }
}
