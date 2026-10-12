using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.State;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityMcpSetupWindowTests
    {
        readonly List<UnityMcpSetupWindow> m_testWindows = new List<UnityMcpSetupWindow>();

        [SetUp]
        public void SetUp()
        {
            m_testWindows.Clear();
            UnityMcpSetupWindow.SkipAutomaticRefreshForTests = false;
            if (UnityMcpSetupWindow.TryGetOpenWindow(out _, out var count) && count > 0)
            {
                Assert.Ignore("Close the existing SGG Unity MCP window before running dedicated UnityMcpSetupWindowTests.");
            }

            UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
        }

        [TearDown]
        public void TearDown()
        {
            try
            {
                for (var index = 0; index < m_testWindows.Count; index++)
                {
                    if (m_testWindows[index] != null)
                    {
                        m_testWindows[index].Close();
                    }
                }
            }
            finally
            {
                m_testWindows.Clear();
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = false;
            }
        }

        [Test]
        public void OpenForCommandCreatesGuiReadyWindow()
        {
            var window = OpenTestWindow("Setup");

            Assert.That(window, Is.Not.Null);
            Assert.That(window.IsGuiReadyForCommand(), Is.True);
            Assert.That(window.rootVisualElement.childCount, Is.GreaterThan(0));
        }

        [Test]
        public void RepeatedOpenForCommandPreservesSingleWindowInstance()
        {
            var first = OpenTestWindow("Setup");
            var second = OpenTestWindow("Status");

            Assert.That(second, Is.SameAs(first));
            Assert.That(UnityMcpSetupWindow.TryGetOpenWindow(out var openWindow, out var count), Is.True);
            Assert.That(count, Is.EqualTo(1));
            Assert.That(openWindow, Is.SameAs(first));
        }

        [TestCase("2026.7.15-5", "SGG Unity MCP - 2026.7.15-5")]
        [TestCase(" 2026.7.22-1 ", "SGG Unity MCP - 2026.7.22-1")]
        [TestCase("", "SGG Unity MCP - package version unavailable")]
        [TestCase(null, "SGG Unity MCP - package version unavailable")]
        public void WindowTitleFormatterUsesPackageVersionOrExplicitUnavailableState(string version, string expected)
        {
            Assert.That(UnityMcpSetupWindow.BuildWindowTitle(version), Is.EqualTo(expected));
        }

        [Test]
        public void CreateGuiRestoresPackageVersionTitle()
        {
            var window = OpenTestWindow("Permissions");
            window.titleContent.text = "stale title";

            window.CreateGUI();

            Assert.That(window.titleContent.text, Does.StartWith("SGG Unity MCP - "));
            Assert.That(window.titleContent.text, Is.Not.EqualTo("stale title"));
            Assert.That(window.rootVisualElement.Q<VisualElement>(className: "mcp-policy-editor-catalog-list"), Is.Null);
        }

        [TestCase("Setup", "Setup")]
        [TestCase("FTUE", "FTUE")]
        [TestCase("Status", "Status")]
        [TestCase("Permissions", "Permissions")]
        [TestCase("Commands", "Commands")]
        [TestCase("Plans", "Plans")]
        [TestCase("Skills", "Skills")]
        [TestCase("Unity AI Assistant", "Unity AI")]
        [TestCase("Diagnostics", "Diagnostics")]
        public void SelectTabForCommandChangesCurrentTabAndSelectsExactlyOneToggle(string tabName, string toggleText)
        {
            var window = OpenTestWindow("Setup");

            window.SelectTabForCommand(tabName);

            Assert.That(window.CurrentTabForCommand(), Is.EqualTo(tabName));
            var selected = SelectedTabs(window.rootVisualElement);
            Assert.That(selected, Has.Count.EqualTo(1));
            Assert.That(selected[0].text, Is.EqualTo(toggleText));
        }

        [Test]
        public void EmptyTabSelectionPreservesCurrentTabAndSelectedToggle()
        {
            var window = OpenTestWindow("Status");

            window.SelectTabForCommand(string.Empty);

            Assert.That(window.CurrentTabForCommand(), Is.EqualTo("Status"));
            Assert.That(SelectedTabs(window.rootVisualElement), Has.Count.EqualTo(1));
        }

        [Test]
        public void SetupTabContainsChecklistAndSetupStateControls()
        {
            var window = OpenTestWindow("Setup");
            var root = window.rootVisualElement;

            Assert.That(root.Q<VisualElement>("setup-panel"), Is.Not.Null);
            Assert.That(root.Q<Label>("setup-checklist-unity-bridge"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-add-canonical-path-registry"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-install-portable-node"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-install-runtime"), Is.Not.Null);
            Assert.That(root.Q<PopupField<string>>("setup-connection-mode"), Is.Not.Null);
            var workspaceRoot = root.Q<TextField>("setup-opencode-workspace-root");
            Assert.That(workspaceRoot, Is.Not.Null);
            Assert.That(workspaceRoot.isDelayed, Is.True);
            Assert.That(root.Q<Button>("setup-opencode-workspace-auto"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-opencode-workspace-browse"), Is.Not.Null);
            Assert.That(root.Q<Label>("setup-opencode-reported-workspace"), Is.Not.Null);
            Assert.That(root.Q<Label>("setup-opencode-presentation-artifact"), Is.Not.Null);
            Assert.That(root.Q<Label>("setup-opencode-presentation-artifact").text, Does.Contain("Not checked"));
            Assert.That(root.Q<Button>("setup-opencode-presentation-refresh"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-opencode-presentation-preview"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-opencode-presentation-install"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-opencode-presentation-remove"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-opencode-presentation-open-config"), Is.Not.Null);
            Assert.That(root.Q<Button>("setup-opencode-presentation-copy-diagnostics"), Is.Not.Null);
            Assert.That(SelectedTabs(root), Has.Count.EqualTo(1));
        }

        [Test]
        public void SetupTabRefreshShowsSavedParentWorkspaceForNestedCurrentProject()
        {
            var projectRoot = ProjectPaths.ProjectRoot;
            var workspaceRoot = Directory.GetParent(projectRoot).FullName;
            var key = GatewayNetworkModeManager.OpenCodeWorkspacePrefsKey(projectRoot);
            var hadPrevious = EditorPrefs.HasKey(key);
            var previous = EditorPrefs.GetString(key, string.Empty);
            try
            {
                var saved = GatewayNetworkModeManager.SetOpenCodeWorkspaceRoot(workspaceRoot);
                Assert.That(saved.Success, Is.True, saved.Message);
                var window = OpenTestWindow("Setup");

                window.RefreshAllForCommand(false);

                Assert.That(window.rootVisualElement.Q<TextField>("setup-opencode-workspace-root").value, Is.EqualTo(Path.GetFullPath(workspaceRoot)));
                var targetStatus = window.rootVisualElement.Q<Label>("setup-opencode-config-status").text;
                Assert.That(targetStatus, Does.Contain("Saved"));
                Assert.That(targetStatus, Does.Contain("nested Unity project"));
            }
            finally
            {
                if (hadPrevious) EditorPrefs.SetString(key, previous);
                else EditorPrefs.DeleteKey(key);
                GatewayNetworkModeManager.InvalidateOpenCodeGitRootCache();
            }
        }

        [Test]
        public void FtueTabIsFirstAndReappearsSelectedAfterRequiredStateRegression()
        {
            var window = OpenTestWindow("Setup");
            var tabs = new List<ToolbarToggle>();
            window.rootVisualElement.Query<ToolbarToggle>().ForEach(tabs.Add);

            Assert.That(tabs[0].text, Is.EqualTo("FTUE"));
            Assert.That(window.rootVisualElement.Q<VisualElement>("ftue-panel"), Is.Not.Null);
            Assert.That(window.rootVisualElement.Q<Button>("ftue-install-portable-node"), Is.Not.Null);
            Assert.That(window.rootVisualElement.Q<Button>("ftue-apply-readonly"), Is.Not.Null);
            Assert.That(window.rootVisualElement.Q<Button>("ftue-apply-allow-all"), Is.Not.Null);

            var state = new McpFtueState
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
            window.ApplyFtueStateForCommand(state);
            Assert.That(window.FtueTabVisibleForCommand(), Is.False);
            Assert.That(window.CurrentTabForCommand(), Is.EqualTo("Setup"));

            state.GatewayProcessReady = false;
            window.ApplyFtueStateForCommand(state);
            Assert.That(window.FtueTabVisibleForCommand(), Is.True);
            Assert.That(window.CurrentTabForCommand(), Is.EqualTo("FTUE"));
        }

        static List<ToolbarToggle> SelectedTabs(VisualElement root)
        {
            var selected = new List<ToolbarToggle>();
            root.Query<ToolbarToggle>().ForEach(toggle =>
            {
                if (toggle.value)
                {
                    selected.Add(toggle);
                }
            });
            return selected;
        }

        UnityMcpSetupWindow OpenTestWindow(string tabName)
        {
            var window = UnityMcpSetupWindow.OpenForCommand(tabName);
            if (!m_testWindows.Contains(window))
            {
                m_testWindows.Add(window);
            }

            return window;
        }
    }
}
