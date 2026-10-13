using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class OpenCodePresentationSetupManagerTests
    {
        [Test]
        public void BuildArgumentsQuotesExactWorkspaceAndAction()
        {
            var arguments = OpenCodePresentationSetupManager.BuildArguments(
                @"C:\Program Files\SGG\gateway-cli.mjs",
                @"C:\Workspaces\Game With Spaces",
                OpenCodePresentationSetupManager.SetupAction.DryRun);

            Assert.That(arguments, Does.StartWith("\"C:\\Program Files\\SGG\\gateway-cli.mjs\" setup opencode-presentation"));
            Assert.That(arguments, Does.Contain("--workspace \"C:\\Workspaces\\Game With Spaces\""));
            Assert.That(arguments, Does.EndWith("--dry-run --json"));
            Assert.That(arguments, Does.Not.Contain("--write"));
        }

        [Test]
        public void ParseResultProjectsBoundedLifecycleStateWithoutRuntimePath()
        {
            const string json = "{\n"
                + "  \"schema_version\": 1,\n"
                + "  \"setup\": \"opencode-presentation\",\n"
                + "  \"action\": \"write\",\n"
                + "  \"workspace\": \"C:/Workspace/Game\",\n"
                + "  \"artifact\": { \"state\": \"current\", \"bundled_version\": \"1.0.0\", \"installed_version\": \"1.0.0\", \"bundled_hash\": \"sha256:123456789abc\" },\n"
                + "  \"config\": { \"file\": \"opencode.jsonc\", \"state\": \"configured\", \"owned_entries\": 1 },\n"
                + "  \"changes\": [\"install_bundled_artifact\"],\n"
                + "  \"blocked_reasons\": [],\n"
                + "  \"recovery\": \"none\",\n"
                + "  \"restart_required\": true,\n"
                + "  \"applied\": true\n"
                + "}";

            var status = OpenCodePresentationSetupManager.ParseResult(json, true, string.Empty);

            Assert.That(status.Parsed, Is.True);
            Assert.That(status.CommandSucceeded, Is.True);
            Assert.That(status.ArtifactState, Is.EqualTo("current"));
            Assert.That(status.ConfigState, Is.EqualTo("configured"));
            Assert.That(status.RestartRequired, Is.True);
            Assert.That(status.Summary(), Does.Contain("restart required"));
            Assert.That(status.RedactedDiagnostics(), Does.Not.Contain("C:/Workspace/Game"));
            Assert.That(status.RedactedDiagnostics(), Does.Not.Contain("gateway-cli"));
            var expectedWorkspaceHash = GatewayNetworkModeManager.OpenCodeWorkspaceIdentityHash("c:/Workspace/Game");
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json, true, string.Empty, OpenCodePresentationSetupManager.SetupAction.Write, expectedWorkspaceHash).Parsed, Is.True);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json, true, string.Empty, OpenCodePresentationSetupManager.SetupAction.Status).Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json, true, string.Empty, OpenCodePresentationSetupManager.SetupAction.Write, "sha256:not-the-requested-workspace").Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json.Replace("  \"restart_required\": true,\n", string.Empty), true, string.Empty).Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json.Replace(",\n  \"applied\": true\n", "\n"), true, string.Empty).Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json.Replace("\"applied\": true", "\"applied\": \"true\""), true, string.Empty).Parsed, Is.False);
            var nestedBooleans = json
                .Replace("\"artifact\": {", "\"artifact\": { \"applied\": true, \"restart_required\": false,")
                .Replace("  \"restart_required\": true,\n", string.Empty)
                .Replace(",\n  \"applied\": true\n", "\n");
            Assert.That(OpenCodePresentationSetupManager.ParseResult(nestedBooleans, true, string.Empty).Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json.Replace("  \"applied\": true\n", "  \"applied\": true,\n  \"applied\": false\n"), true, string.Empty).Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json.Replace("  \"applied\": true\n", "  \"applied\": true,\n  \"applied\": null\n"), true, string.Empty).Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json.Replace("  \"applied\": true\n", "  \"applied\": true,\n  \"appl\\u0069ed\": false\n"), true, string.Empty).Parsed, Is.False);
            Assert.That(OpenCodePresentationSetupManager.ParseResult(json.Replace("  \"action\": \"write\",\n", string.Empty), true, string.Empty).Parsed, Is.False);
        }

        [Test]
        public void ParseResultKeepsBlockedStatusInspectible()
        {
            const string json = "{\"schema_version\":1,\"setup\":\"opencode-presentation\",\"action\":\"remove\",\"workspace\":\"C:/Workspace\","
                + "\"artifact\":{\"state\":\"modified\",\"bundled_version\":\"1.0.0\",\"installed_version\":\"1.0.0\",\"bundled_hash\":\"sha256:123456789abc\"},"
                + "\"config\":{\"file\":\"opencode.jsonc\",\"state\":\"configured\",\"owned_entries\":1},"
                + "\"changes\":[\"remove_owned_artifact\"],\"blocked_reasons\":[\"installed_artifact_modified\"],"
                + "\"recovery\":\"none\",\"restart_required\":false,\"applied\":false}";

            var status = OpenCodePresentationSetupManager.ParseResult(json, false, string.Empty);

            Assert.That(status.Parsed, Is.True);
            Assert.That(status.CommandSucceeded, Is.False);
            Assert.That(status.Blocked, Is.True);
            Assert.That(status.Summary(), Does.Contain("installed_artifact_modified"));
        }

        [TestCase("1.18.21", true, "outside bundled")]
        [TestCase("opencode 1.19.4", true, "outside bundled")]
        [TestCase("2.0.0", true, "outside bundled")]
        [TestCase("opencode v2.0.25", true, "range supported")]
        [TestCase("2.1.0", true, "active loading/rendering not verified")]
        [TestCase("3.0.0", true, "outside bundled")]
        [TestCase("OpenChamber 1.20.0", false, "not yet live-validated")]
        [TestCase("OpenChamber v2.0.0", false, "frontend labels/forms require separate evidence")]
        [TestCase("OpenChamber 1.21.0", false, "not yet live-validated")]
        [TestCase("not installed", false, "Not detected")]
        public void FormatClientVersionReportsExactCompatibility(string output, bool openCode, string expected)
        {
            Assert.That(OpenCodePresentationSetupManager.FormatClientVersion(output, openCode), Does.Contain(expected));
        }

        [Test]
        public void ReadOnlyStatusDoesNotClearPendingRestartReminder()
        {
            var applied = new OpenCodePresentationSetupManager.PresentationStatus { RestartRequired = true };
            var readOnly = new OpenCodePresentationSetupManager.PresentationStatus { RestartRequired = false };
            var uncertain = new OpenCodePresentationSetupManager.PresentationStatus { OutcomeUncertain = true };

            Assert.That(UnityMcpSetupWindow.PreservePresentationRestartPending(false, applied), Is.True);
            Assert.That(UnityMcpSetupWindow.PreservePresentationRestartPending(true, readOnly), Is.True);
            Assert.That(UnityMcpSetupWindow.PreservePresentationRestartPending(false, uncertain), Is.True);
            Assert.That(UnityMcpSetupWindow.PreservePresentationRestartPending(false, readOnly), Is.False);
        }

        [Test]
        public void UncertainOutcomeBlocksMutationUntilStatusRefresh()
        {
            Assert.That(UnityMcpSetupWindow.PresentationMutationAllowed(true, false), Is.True);
            Assert.That(UnityMcpSetupWindow.PresentationMutationAllowed(true, true), Is.False);
            Assert.That(UnityMcpSetupWindow.PresentationMutationAllowed(false, false), Is.False);
        }

        [Test]
        public void UncertainOutcomeAndRestartReminderRemainScopedAcrossWorkspaceSwitches()
        {
            const string workspaceA = "C:/Workspace/A";
            const string workspaceB = "C:/Workspace/B";
            UnityMcpSetupWindow.ClearOpenCodePresentationSessionStateForCommand(workspaceA);
            UnityMcpSetupWindow.ClearOpenCodePresentationSessionStateForCommand(workspaceB);
            var firstWindow = UnityEngine.ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
            UnityMcpSetupWindow secondWindow = null;
            try
            {
                firstWindow.CacheOpenCodePresentationStatusForCommand(new OpenCodePresentationSetupManager.PresentationStatus { OutcomeUncertain = true }, workspaceA);
                firstWindow.CacheOpenCodePresentationStatusForCommand(new OpenCodePresentationSetupManager.PresentationStatus { Parsed = true }, workspaceB, true);
                UnityEngine.Object.DestroyImmediate(firstWindow);
                firstWindow = null;
                secondWindow = UnityEngine.ScriptableObject.CreateInstance<UnityMcpSetupWindow>();

                Assert.That(secondWindow.OpenCodePresentationOutcomeUncertainForCommand(workspaceA), Is.True);
                Assert.That(secondWindow.OpenCodePresentationOutcomeUncertainForCommand(workspaceB), Is.False);
                Assert.That(secondWindow.OpenCodePresentationRestartPendingForCommand(workspaceA), Is.True);

                secondWindow.CacheOpenCodePresentationStatusForCommand(new OpenCodePresentationSetupManager.PresentationStatus { Parsed = true }, workspaceA, true);
                Assert.That(secondWindow.OpenCodePresentationOutcomeUncertainForCommand(workspaceA), Is.False);
                Assert.That(secondWindow.OpenCodePresentationRestartPendingForCommand(workspaceA), Is.True);
            }
            finally
            {
                if (firstWindow != null) UnityEngine.Object.DestroyImmediate(firstWindow);
                if (secondWindow != null) UnityEngine.Object.DestroyImmediate(secondWindow);
                UnityMcpSetupWindow.ClearOpenCodePresentationSessionStateForCommand(workspaceA);
                UnityMcpSetupWindow.ClearOpenCodePresentationSessionStateForCommand(workspaceB);
            }
        }

        [Test]
        public void SetupWindowExposesExplicitPresentationActionsWithoutAutomaticProbe()
        {
            UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
            var existed = UnityMcpSetupWindow.TryGetOpenWindow(out var window, out var count) && count > 0;
            if (!existed)
            {
                window = UnityMcpSetupWindow.OpenForCommand("Setup");
            }
            try
            {
                Assert.That(window, Is.Not.Null);
                window.CreateGUI();
                var root = window.rootVisualElement;
                Assert.That(root.Q<Label>("setup-opencode-presentation-artifact"), Is.Not.Null);
                Assert.That(root.Q<Label>("setup-opencode-presentation-artifact").text, Does.Contain("Not checked"));
                Assert.That(root.Q<Button>("setup-opencode-presentation-refresh"), Is.Not.Null);
                Assert.That(root.Q<Button>("setup-opencode-presentation-preview"), Is.Not.Null);
                Assert.That(root.Q<Button>("setup-opencode-presentation-install"), Is.Not.Null);
                Assert.That(root.Q<Button>("setup-opencode-presentation-remove"), Is.Not.Null);
                Assert.That(root.Q<Button>("setup-opencode-presentation-open-config"), Is.Not.Null);
                Assert.That(root.Q<Button>("setup-opencode-presentation-copy-diagnostics"), Is.Not.Null);
            }
            finally
            {
                if (!existed && window != null) window.Close();
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = false;
            }
        }
    }
}
