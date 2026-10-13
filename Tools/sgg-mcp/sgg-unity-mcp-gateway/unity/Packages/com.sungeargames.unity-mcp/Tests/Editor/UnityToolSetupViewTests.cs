using System;
using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityToolSetupViewTests
    {
        [TestCase(true, "checked", "current", "optional")]
        [TestCase(false, "checked", "current", "ok")]
        [TestCase(false, "checked", "update_available", "error")]
        [TestCase(false, "expired", "current", "info")]
        [TestCase(false, "check_failed", "current", "info")]
        [TestCase(false, "checking", "update_available", "info")]
        [TestCase(false, "not_checked", "not_installed", "info")]
        [TestCase(false, "checked", "upstream_change_requires_review", "info")]
        [TestCase(false, "checked", "outcome_unknown", "info")]
        public void FreshnessColorsRequireKnownAbsenceOrCheckedLatest(bool absent, string check, string update, string expected)
        {
            var status = ComponentUpdateStatus.NotChecked();
            status.CheckState = check;
            status.UpdateState = update;
            Assert.That(UnityMcpSetupWindow.UnityToolFreshnessState(absent, status), Is.EqualTo(expected));
        }

        [Test]
        public void MissingAndUnmanagedSkillsDoNotClaimLatest()
        {
            var skill = new UnityCliProjectSkillInstaller.UnityCliSkillStatus();
            Assert.That(UnityMcpSetupWindow.UnityCliSkillDisplayState(skill, null), Is.EqualTo("optional"));
            skill.Exists = true;
            Assert.That(UnityMcpSetupWindow.UnityCliSkillDisplayState(skill, null), Is.EqualTo("info"));
            skill.Valid = true;
            skill.LocalState = UnityCliProjectSkillInstaller.LocalStatePreviousReviewed;
            Assert.That(UnityMcpSetupWindow.UnityCliSkillDisplayState(skill, null), Is.EqualTo("error"));
            skill.LocalState = UnityCliProjectSkillInstaller.LocalStateCurrentReviewed;
            Assert.That(UnityMcpSetupWindow.UnityCliSkillDisplayState(skill, null), Is.EqualTo("info"));
        }

        [Test]
        public void CliPresenceDistinguishesMissingRegularFileAndUnknownDirectory()
        {
            WithFixture(root =>
            {
                var path = Path.Combine(root, "unity.exe");
                Assert.That(UnityCliPipelineSetup.TryGetCliPresence(path, out var present), Is.True);
                Assert.That(present, Is.False);
                File.WriteAllText(path, "not executed");
                Assert.That(UnityCliPipelineSetup.TryGetCliPresence(path, out present), Is.True);
                Assert.That(present, Is.True);
                Assert.That(UnityCliPipelineSetup.TryGetCliPresence(root, out present), Is.False);
                Assert.That(present, Is.True);
                Assert.That(UnityCliPipelineSetup.TryGetCliPresence("\0", out _), Is.False);
            });
        }

        [Test]
        public void ExistingFileAtSkillTargetIsNotAbsentAndCannotBeInstalledOver()
        {
            WithFixture(root =>
            {
                var parent = Path.Combine(root, ".agents", "skills");
                Directory.CreateDirectory(parent);
                var target = Path.Combine(parent, "unity-cli");
                File.WriteAllText(target, "owned fixture");
                var status = UnityCliProjectSkillInstaller.GetStatus(root);
                Assert.That(status.Exists, Is.True);
                Assert.That(status.Valid, Is.False);
                Assert.That(status.LocalState, Is.EqualTo(UnityCliProjectSkillInstaller.LocalStateUnknown));
                Assert.That(UnityCliProjectSkillInstaller.TryStartInstall(root, out var operation, out _), Is.False);
                Assert.That(operation, Is.Null);
                Assert.That(File.ReadAllText(target), Is.EqualTo("owned fixture"));
            });
        }

        [Test]
        public void UnreadablePipelineManifestDoesNotOfferMissingPackageInstall()
        {
            WithFixture(root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "Packages", "manifest.json"));
                Assert.That(UnityCliPipelineSetup.ReadManifestVersion(root, UnityCliPipelineSetup.PipelinePackageId), Is.EqualTo("unknown"));
            });
        }

        [TestCase("", "invalid")]
        [TestCase("{", "invalid")]
        [TestCase("{\"dependencies\":{\"com.unity.pipeline\":\"\"}}", "invalid")]
        [TestCase("{\"dependencies\":{\"com.unity.pipeline\":false}}", "invalid")]
        [TestCase("{\"dependencies\":{\"com.unity.pipeline\":null}}", "invalid")]
        [TestCase("{\"dependencies\":{\"com.unity.pipeline\":\"0.4.0\",\"com.unity.pipeline\":\"0.5.0\"}}", "invalid")]
        [TestCase("{\"dependencies\":{\"com.unity.pip\\u0065line\":\"0.4.0\"}}", "0.4.0")]
        [TestCase("{\"dependencies\":{}} trailing", "invalid")]
        [TestCase("{\"dependencies\":[]}", "invalid")]
        [TestCase("{\"dependencies\":{},\"x\":1٢}", "invalid")]
        [TestCase("{\"dependencies\":{},\"x\":1.٢}", "invalid")]
        [TestCase("{\"dependencies\":{},\"x\":1e٢}", "invalid")]
        [TestCase("{\"dependencies\":{},\"x\":\"\\u 061\"}", "invalid")]
        [TestCase("{\"dependencies\":{},\"x\":\"\\u061 \"}", "invalid")]
        [TestCase("{\"dependencies\":{\"other\":\"1.0.0\"}}", "")]
        public void ExistingManifestProvesAbsenceOnlyAfterStrictDependencyParsing(string json, string expected)
        {
            WithFixture(root =>
            {
                Directory.CreateDirectory(Path.Combine(root, "Packages"));
                File.WriteAllText(Path.Combine(root, "Packages", "manifest.json"), json);
                Assert.That(UnityCliPipelineSetup.ReadManifestVersion(root, UnityCliPipelineSetup.PipelinePackageId), Is.EqualTo(expected));
            });
        }

        [Test]
        public void IneligibleSnapshotCannotOverrideUnknownPresenceWithMissingText()
        {
            var absent = ComponentUpdateStatus.NotInstalled("", "", "");
            Assert.That(UnityMcpSetupWindow.FormatObservedUpdateStatus(absent, false), Is.EqualTo(McpWindowLocalization.Text("Installation unresolved or latest version unknown. Use this block's Check Updates button.")));
            Assert.That(UnityMcpSetupWindow.FormatObservedUpdateStatus(absent, true), Is.EqualTo(McpWindowLocalization.Text("Not installed. Optional; latest check skipped. Use Install when prerequisites are ready.")));
            Assert.That(UnityMcpSetupWindow.AvailableToolVersionText(absent), Is.EqualTo(McpWindowLocalization.Text("Latest version unknown. Use this block's Check Updates button.")));
            var checkedVersion = ComponentUpdateStatus.Checked("0.3.0", "0.5.0-exp.1", "", "official_unity_pipeline_list_versions", "", DateTime.UtcNow);
            Assert.That(UnityMcpSetupWindow.AvailableToolVersionText(checkedVersion), Does.StartWith("0.5.0-exp.1"));
            checkedVersion.CheckState = UnityCliUpdateCheck.CheckStateFailed;
            Assert.That(UnityMcpSetupWindow.AvailableToolVersionText(checkedVersion), Is.EqualTo(McpWindowLocalization.Text("Latest version unknown. Use this block's Check Updates button.")));
        }

        [Test]
        public void InstallScriptUsesConfirmedDestinationAndChecksEveryExistingAncestor()
        {
            var script = UnityCliPipelineSetup.BuildWindowsCliInstallScript();
            var home = Path.GetDirectoryName(Path.GetDirectoryName(UnityCliPipelineSetup.OfficialWindowsInstallPath));
            Assert.That(script, Does.Contain("$expectedHome='" + home.Replace("'", "''") + "'"));
            Assert.That(script, Does.Not.Contain("$env:LOCALAPPDATA"));
            Assert.That(script, Does.Contain("$env:UNITY_CLI_HOME=$expectedHome"));
            Assert.That(script, Does.Contain("FileAttributes]::ReparsePoint"));
            Assert.That(script, Does.Contain("Assert-SafeDestination;"));
        }

        [Test]
        public void OfficialSkillInstallRequiresFreshExactLatestMetadataButNotManualContentApproval()
        {
            var now = DateTime.UtcNow;
            var status = ComponentUpdateStatus.Checked("", UnityCliProjectSkillInstaller.ReviewedSourceCommit,
                UnityCliUpdateCheck.SkillChannel, UnityCliUpdateCheck.SkillSourceKind,
                UnityCliUpdateCheck.SkillSourceIdentity(UnityCliProjectSkillInstaller.ReviewedSourceCommit), now);
            status.AuthoritativeForMutation = false; // Missing availability is not update authority; human Install confirmation is separate.
            status.UpdateState = UnityCliUpdateCheck.UpdateStateNotInstalled;
            Assert.That(UnityMcpSetupWindow.HasFreshOfficialSkillTarget(status, now), Is.True);
            Assert.That(UnityMcpSetupWindow.HasFreshOfficialSkillTarget(status, now.Add(UnityCliUpdateCheck.CacheTtl)), Is.False);
            status.AvailableVersion = new string('a', 40);
            Assert.That(UnityMcpSetupWindow.HasFreshOfficialSkillTarget(status, now), Is.False);
            status.AvailableVersion = UnityCliProjectSkillInstaller.ReviewedSourceCommit;
            status.ExpiresAtUtc = DateTime.MinValue; // Persisted display metadata cannot enable installation.
            Assert.That(UnityMcpSetupWindow.HasFreshOfficialSkillTarget(status, now), Is.False);
        }

        [Test]
        public void BoundCommandRefusesChangedOwnedExecutableBeforeAnyInvocation()
        {
            if (Application.platform != RuntimePlatform.WindowsEditor) Assert.Ignore("Windows Authenticode fixture requires Windows Editor.");
            WithFixture(root =>
            {
                var exe = Path.Combine(root, "changed.exe");
                var marker = Path.Combine(root, "must-not-exist.txt");
                File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), exe);
                var arguments = WindowsCliAuthenticode.BuildBoundCommandArguments(exe, new string('0', 40), new string('0', 64), new[] { "/c", "echo unsafe > \"" + marker + "\"" });
                Assert.That(BoundedProcessOperation.TryStart(WindowsCliAuthenticode.PowerShellPath, arguments, root, TimeSpan.FromSeconds(15), out var operation, out var error), Is.True, error);
                using (operation)
                {
                    var deadline = DateTime.UtcNow.AddSeconds(20);
                    while (!operation.Poll() && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(25);
                    Assert.That(operation.Poll(), Is.True);
                    Assert.That(operation.TimedOut, Is.False);
                    Assert.That(operation.ExitCode, Is.Not.EqualTo(0));
                    Assert.That(operation.StandardError, Does.Contain("bound executable identity changed").Or.Contain("signature unavailable"));
                    Assert.That(File.Exists(marker), Is.False, "Changed identity must never reach command invocation.");
                }
            });
        }

        [Test]
        public void ComponentBlocksOwnTheirButtonsAndExplainWslWithoutRunningActions()
        {
            var previous = UnityMcpSetupWindow.SkipAutomaticRefreshForTests;
            UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
            UnityMcpSetupWindow window = null;
            try
            {
                window = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
                var root = new VisualElement();
                typeof(UnityMcpSetupWindow).GetMethod("BuildUnityCliPipelineSection", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, new object[] { root });
                AssertButtons(root, "unity-tool-cli", "Install Windows CLI", "Check CLI Updates", "Update Windows CLI");
                AssertButtons(root, "unity-tool-pipeline", "Install Pipeline", "Check Pipeline Updates", "Update Pipeline", "Verify Pipeline Connection");
                AssertButtons(root, "unity-tool-skill", "Install Unity CLI Skill", "Check Skill Updates", "Update Unity CLI Skill");
                Assert.That(root.Q<VisualElement>("unity-tool-pipeline").Query<Label>().ToList().Any(label => label.text.Contains("Available Pipeline version")), Is.True);
                Assert.That(root.Q<VisualElement>("unity-tool-skill").Query<Label>().ToList().Any(label => label.text.Contains("skill version (commit)")), Is.True);
                var wsl = root.Q<VisualElement>("unity-tool-wsl");
                Assert.That(wsl, Is.Not.Null);
                Assert.That(wsl.Query<Label>().ToList().Any(label => label.text.Contains("Windows Subsystem for Linux")), Is.True);
                Assert.That(root.Query<Label>(className: "mcp-checklist-icon").ToList(), Is.Not.Empty);
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = previous;
            }
        }

        static void AssertButtons(VisualElement root, string name, params string[] expected)
        {
            var block = root.Q<VisualElement>(name);
            Assert.That(block, Is.Not.Null);
            var buttons = block.Query<Button>().ToList();
            foreach (var text in expected)
            {
                var button = buttons.SingleOrDefault(candidate => candidate.text == text);
                Assert.That(button, Is.Not.Null, name + ": " + text);
                Assert.That(button.parent.ClassListContains("mcp-actions"), Is.True);
                Assert.That(block.IndexOf(button.parent), Is.GreaterThan(block.Children().ToList().FindLastIndex(child => child.ClassListContains("mcp-row"))));
            }
        }

        [Test]
        public void RenderPathDoesNotTurnIneligibleSnapshotIntoFalseMissingSummary()
        {
            var previous = UnityMcpSetupWindow.SkipAutomaticRefreshForTests;
            UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
            UnityMcpSetupWindow window = null;
            try
            {
                window = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
                var root = new VisualElement();
                typeof(UnityMcpSetupWindow).GetMethod("BuildUnityCliPipelineSection", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(window, new object[] { root });
                var checkedCli = UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked();
                checkedCli.Update = ComponentUpdateStatus.NotInstalled("", "", "");
                var checkedPipeline = new UnityCliPipelineSetup.UnityPipelineStatus { Update = ComponentUpdateStatus.NotInstalled("", "", "") };
                var skill = new UnityCliProjectSkillInstaller.UnityCliSkillStatus { Exists = true };
                var snapshot = new UnityCliUpdateCheckSnapshot(checkedCli, checkedPipeline, skill) { IsComplete = true };
                window.RenderUnityToolUpdateStatus(snapshot, checkedCli, new UnityCliPipelineSetup.UnityPipelineStatus { ManifestVersion = "unknown" }, skill, false, false);
                var pipelineRow = root.Q<Label>("unity-tool-pipeline-update");
                Assert.That(pipelineRow.text, Is.EqualTo(McpWindowLocalization.Text("Installation unresolved or latest version unknown. Use this block's Check Updates button.")));
                Assert.That(pipelineRow.parent.ClassListContains("mcp-checklist--info"), Is.True);
                Assert.That(root.Q<Label>("unity-tool-cli-update").parent.ClassListContains("mcp-checklist--info"), Is.True);
                Assert.That(root.Q<Label>("unity-tool-summary").text, Does.Not.Contain("No optional Unity tools are installed"));
                checkedPipeline.Update = ComponentUpdateStatus.Checked("", "0.8.0-exp.1", "", "official_unity_pipeline_list_versions", "", DateTime.UtcNow);
                checkedPipeline.Update.UpdateState = UnityCliUpdateCheck.UpdateStateUnsupported;
                checkedPipeline.Update.AuthoritativeForMutation = false;
                window.RenderUnityToolUpdateStatus(snapshot, checkedCli, new UnityCliPipelineSetup.UnityPipelineStatus { ManifestVersion = "unknown" }, skill, false, false);
                Assert.That(pipelineRow.text, Does.Contain("installed=unknown/unverified"));
                Assert.That(pipelineRow.text, Does.Not.Contain("installed=not installed"));
                Assert.That(pipelineRow.parent.ClassListContains("mcp-checklist--info"), Is.True);
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = previous;
            }
        }

        static void WithFixture(Action<string> action)
        {
            var root = Path.Combine(Path.GetTempPath(), "sgg-unity-tool-view-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try { action(root); }
            finally { Directory.Delete(root, true); }
        }
    }
}
