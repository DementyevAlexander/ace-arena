using System;
using System.IO;
using System.Reflection;
using System.Text;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.UI;
using SGG.UnityMCP.Editor.UI.Localization;
using UnityEngine;
using UnityEngine.UIElements;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityMcpSetupPopulationTests
    {
        static readonly string[] LocalRows =
        {
            "setup-canonical-path-registry", "setup-canonical-path-dependency", "setup-manifest",
            "setup-node-version", "setup-node-path", "setup-npm-path",
        };

        [Test]
        public void SyntheticWindowDoesNotCancelOwnersSharedToolCoordinator()
        {
            var previous = UnityMcpSetupWindow.SkipAutomaticRefreshForTests;
            var field = typeof(UnityMcpSetupWindow).GetField("s_unityToolUpdateCheck", BindingFlags.Static | BindingFlags.NonPublic);
            var owner = field.GetValue(null);
            var cacheField = typeof(UnityCliUpdateCheck).GetField("s_cachedStatus", BindingFlags.Static | BindingFlags.NonPublic);
            var ownerCache = cacheField.GetValue(null);
            UnityMcpSetupWindow fixture = null;
            UnityCliUpdateCheckCoordinator sentinel = null;
            try
            {
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
                fixture = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
                Assert.That(fixture.IsIsolatedTestFixture, Is.True);
                var complete = UnityCliUpdateCheckSnapshot.NotChecked();
                complete.IsComplete = true; // A completed sentinel must never publish into the owner's cache.
                sentinel = new UnityCliUpdateCheckCoordinator(complete, null, () => DateTime.UtcNow, TimeSpan.FromSeconds(1));
                field.SetValue(null, sentinel);
                UnityEngine.Object.DestroyImmediate(fixture);
                Assert.That(field.GetValue(null), Is.SameAs(sentinel));
                Assert.That(cacheField.GetValue(null), Is.SameAs(ownerCache));
            }
            finally
            {
                // Same synchronous Editor turn: the actual owner's coordinator was never cancelled.
                field.SetValue(null, owner);
                cacheField.SetValue(null, ownerCache);
                if (fixture != null) UnityEngine.Object.DestroyImmediate(fixture);
                sentinel?.Dispose();
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = previous;
            }
        }

        [Test]
        public void InitialGuiPopulatesRealLocalRowsEvenWhenAnotherFixtureSuppressesRefresh()
        {
            var previous = UnityMcpSetupWindow.SkipAutomaticRefreshForTests;
            var language = McpWindowLocalization.CurrentLanguage;
            UnityMcpSetupWindow window = null;
            try
            {
                McpWindowLocalization.CurrentLanguage = "en";
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
                window = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
                window.EnableLocalRefreshForIsolatedTests();
                window.SkipToolFreshnessForTests = true; // No network freshness check in this regression.
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
                window.CreateGUI();

                foreach (var name in LocalRows)
                {
                    var label = window.rootVisualElement.Q<Label>(name);
                    Assert.That(label, Is.Not.Null, name);
                    Assert.That(label.text, Is.Not.Empty.And.Not.EqualTo("Not checked."), name);
                }
                Assert.That(window.rootVisualElement.Q<Label>("setup-manifest").text,
                    Is.EqualTo(Path.Combine(ProjectPaths.ProjectRoot, "Packages", "manifest.json")));
                Assert.That(window.LatestTimingSummaryForCommand(), Does.Contain("RefreshAll"));
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = previous;
                McpWindowLocalization.CurrentLanguage = language;
            }
        }

        [Test]
        public void UncheckedFixtureRowsAreExplicitNotBlank()
        {
            var previous = UnityMcpSetupWindow.SkipAutomaticRefreshForTests;
            var language = McpWindowLocalization.CurrentLanguage;
            UnityMcpSetupWindow window = null;
            try
            {
                McpWindowLocalization.CurrentLanguage = "en";
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
                window = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
                window.CreateGUI();
                foreach (var name in LocalRows)
                    Assert.That(window.rootVisualElement.Q<Label>(name).text, Is.EqualTo(McpWindowLocalization.Text("Not checked.")), name);
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = previous;
                McpWindowLocalization.CurrentLanguage = language;
            }
        }

        [Test]
        public void FailedOptionalReadIsVisibleAndDoesNotStopLaterLocalRefresh()
        {
            var previous = UnityMcpSetupWindow.SkipAutomaticRefreshForTests;
            UnityMcpSetupWindow window = null;
            try
            {
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = true;
                window = ScriptableObject.CreateInstance<UnityMcpSetupWindow>();
                window.CreateGUI();
                var tracker = typeof(UnityMcpSetupWindow).GetMethod("TrackMcpWindowStep", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, new[] { typeof(StringBuilder), typeof(string), typeof(Action) }, null);
                var details = new StringBuilder();
                Action fail = () => throw new InvalidOperationException("population regression");
                Assert.DoesNotThrow(() => tracker.Invoke(window, new object[] { details, "optional_fixture", fail }));
                Action next = () => typeof(UnityMcpSetupWindow).GetMethod("RefreshCanonicalPathRegistry", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(window, null);
                Assert.DoesNotThrow(() => tracker.Invoke(window, new object[] { details, "canonical_path_registry", next }));
                Assert.That(details.ToString(), Does.Contain("optional_fixture=failed").And.Contain("canonical_path_registry="));
                Assert.That(window.rootVisualElement.Q<Label>("setup-manifest").text, Is.Not.Empty.And.Not.EqualTo("Not checked."));
                Assert.That(window.rootVisualElement.Q<Label>("mcp-refresh-failures").text, Does.Contain("optional_fixture"));
                var lastAction = typeof(UnityMcpSetupWindow).GetField("m_lastActionLabel", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(window) as Label;
                lastAction.text = "Optional freshness started";
                Assert.That(window.rootVisualElement.Q<Label>("mcp-refresh-failures").text, Does.Contain("optional_fixture"));
            }
            finally
            {
                if (window != null) UnityEngine.Object.DestroyImmediate(window);
                UnityMcpSetupWindow.SkipAutomaticRefreshForTests = previous;
            }
        }
    }
}
