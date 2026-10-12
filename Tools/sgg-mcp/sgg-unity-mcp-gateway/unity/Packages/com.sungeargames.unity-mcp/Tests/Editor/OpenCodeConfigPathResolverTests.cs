using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Setup;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class OpenCodeConfigPathResolverTests
    {
        string m_root;
        string m_unityRoot;

        [SetUp]
        public void SetUp()
        {
            m_root = Path.Combine(Path.GetTempPath(), "sgg-opencode-resolver-" + Guid.NewGuid().ToString("N"));
            m_unityRoot = Path.Combine(m_root, "game");
            Directory.CreateDirectory(m_unityRoot);
        }

        [TearDown]
        public void TearDown()
        {
            if (Directory.Exists(m_root)) Directory.Delete(m_root, true);
        }

        [Test]
        public void ExplicitRootWinsAndSelectsExistingJson()
        {
            var files = new HashSet<string>(PathComparer) { Path.Combine(m_root, "opencode.json") };

            var result = Resolve(m_root, m_unityRoot, Path.Combine(m_root, "ignored-git"), files);

            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.Explicit));
            Assert.That(result.WorkspaceRoot, Is.EqualTo(Path.GetFullPath(m_root)));
            Assert.That(result.ConfigPath, Is.EqualTo(Path.Combine(m_root, "opencode.json")));
            Assert.That(result.State, Is.EqualTo(OpenCodeConfigPathResolver.ConfigState.Json));
            Assert.That(result.IsNestedProject, Is.True);
            Assert.That(result.RequiresConfirmation, Is.False);
        }

        [Test]
        public void SavedRootWinsWhenExplicitRootIsMissing()
        {
            var result = Resolve(null, m_root, Path.Combine(m_root, "ignored-git"), new HashSet<string>(PathComparer));

            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.Saved));
            Assert.That(result.ConfigPath, Is.EqualTo(Path.Combine(m_root, "opencode.jsonc")));
            Assert.That(result.State, Is.EqualTo(OpenCodeConfigPathResolver.ConfigState.Missing));
        }

        [Test]
        public void GitRootWinsBeforeUnityFallback()
        {
            var result = Resolve(null, null, m_root, new HashSet<string>(PathComparer));

            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.GitWorktree));
            Assert.That(result.WorkspaceRoot, Is.EqualTo(Path.GetFullPath(m_root)));
            Assert.That(result.RequiresConfirmation, Is.False);
        }

        [Test]
        public void MissingOrUnrelatedGitRootRequiresUnityFallbackConfirmation()
        {
            var unrelated = Path.Combine(Path.GetTempPath(), "sgg-unrelated-" + Guid.NewGuid().ToString("N"));
            var result = Resolve(null, null, unrelated, new HashSet<string>(PathComparer));

            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.UnityProjectFallback));
            Assert.That(result.WorkspaceRoot, Is.EqualTo(Path.GetFullPath(m_unityRoot)));
            Assert.That(result.RequiresConfirmation, Is.True);
            Assert.That(result.IsNestedProject, Is.False);
        }

        [Test]
        public void BothConfigFilesReturnTypedConflictWithoutSelectingOne()
        {
            var files = new HashSet<string>(PathComparer)
            {
                Path.Combine(m_root, "opencode.jsonc"),
                Path.Combine(m_root, "opencode.json"),
            };

            var result = Resolve(m_root, null, null, files);

            Assert.That(result.State, Is.EqualTo(OpenCodeConfigPathResolver.ConfigState.Conflict));
            Assert.That(result.HasConflict, Is.True);
            Assert.That(result.ConfigPath, Is.Null);
            Assert.That(result.JsoncPath, Is.EqualTo(Path.Combine(m_root, "opencode.jsonc")));
            Assert.That(result.JsonPath, Is.EqualTo(Path.Combine(m_root, "opencode.json")));
        }

        [Test]
        public void ExistingJsoncIsSelectedWithoutCreatingJson()
        {
            var files = new HashSet<string>(PathComparer) { Path.Combine(m_root, "opencode.jsonc") };

            var result = Resolve(m_root, null, null, files);

            Assert.That(result.State, Is.EqualTo(OpenCodeConfigPathResolver.ConfigState.Jsonc));
            Assert.That(result.ConfigPath, Is.EqualTo(Path.Combine(m_root, "opencode.jsonc")));
        }

        [TestCase("opencode.jsonc")]
        [TestCase("opencode.json")]
        public void LocalHiddenConfigIsATypedBlockerEvenWhenRootConfigIsMissing(string hiddenFile)
        {
            var files = new HashSet<string>(PathComparer) { Path.Combine(m_root, ".opencode", hiddenFile) };
            var result = Resolve(m_root, null, null, files);

            Assert.That(result.State, Is.EqualTo(OpenCodeConfigPathResolver.ConfigState.HiddenLayerConflict));
            Assert.That(result.HasHiddenLayer, Is.True);
            Assert.That(result.HasConflict, Is.True);
            Assert.That(result.ConfigPath, Is.Null);
            Assert.That(result.JsoncPath, Is.EqualTo(Path.Combine(m_root, "opencode.jsonc")));
            Assert.That(result.JsonPath, Is.EqualTo(Path.Combine(m_root, "opencode.json")));
            Assert.That(result.HiddenJsoncPath, Is.EqualTo(Path.Combine(m_root, ".opencode", "opencode.jsonc")));
            Assert.That(result.HiddenJsonPath, Is.EqualTo(Path.Combine(m_root, ".opencode", "opencode.json")));
            Assert.That(result.WorkspaceRoot, Is.EqualTo(Path.GetFullPath(m_root)));
            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.Explicit));
        }

        [Test]
        public void HiddenSameNameDisabledOverrideBlocksAReadyRootWithoutMigratingEitherFile()
        {
            var rootPath = Path.Combine(m_root, "opencode.jsonc");
            var hiddenPath = Path.Combine(m_root, ".opencode", "opencode.json");
            var rootConfig = GatewayNetworkModeManager.OpenCodeConfigSnippet(new GatewayNetworkModeManager.NetworkStatus
            {
                SelectedMode = GatewayNetworkModeManager.ModeWindowsStdio,
                McpUrl = "http://127.0.0.1:27182/mcp",
                TokenEnv = "SGG_UNITY_MCP_TOKEN",
            });
            const string hiddenConfig = "{\"mcp\":{\"servers\":{\"sgg-unity-mcp\":{\"disabled\":true}}}}";
            Directory.CreateDirectory(Path.GetDirectoryName(hiddenPath));
            File.WriteAllText(rootPath, rootConfig);
            File.WriteAllText(hiddenPath, hiddenConfig);

            Assert.That(GatewayNetworkModeManager.IsOpenCodeConfigReady(rootPath, GatewayNetworkModeManager.ModeWindowsStdio, "http://127.0.0.1:27182/mcp"), Is.True);
            var result = OpenCodeConfigPathResolver.ResolveWithKnownGitRoot(m_unityRoot, m_root, null, null);
            Assert.That(result.HasConflict, Is.True);
            Assert.That(GatewayNetworkModeManager.IsOpenCodeTargetReady(result, GatewayNetworkModeManager.ModeWindowsStdio, "http://127.0.0.1:27182/mcp"), Is.False);
            foreach (var write in new[] { false, true })
            {
                Assert.That(GatewayNetworkModeManager.ValidateOpenCodeSetupTarget(result, write, rootPath, null),
                    Does.Contain(".opencode config layer").And.Contain("entire same-named MCP server").And.Contain(hiddenPath));
            }
            Assert.That(File.ReadAllText(rootPath), Is.EqualTo(rootConfig));
            Assert.That(File.ReadAllText(hiddenPath), Is.EqualTo(hiddenConfig));
        }

        [Test]
        public void BothHiddenFilesAndRootFilesNeverSelectOrMergeATarget()
        {
            var result = Resolve(m_root, null, null, new HashSet<string>(PathComparer)
            {
                Path.Combine(m_root, "opencode.jsonc"),
                Path.Combine(m_root, "opencode.json"),
                Path.Combine(m_root, ".opencode", "opencode.jsonc"),
                Path.Combine(m_root, ".opencode", "opencode.json"),
            });
            Assert.That(result.HasConflict, Is.True);
            Assert.That(result.HasHiddenLayer, Is.True);
            Assert.That(result.ConfigPath, Is.Null);
            Assert.That(GatewayNetworkModeManager.ValidateOpenCodeSetupTarget(result, true, null, null), Does.Contain("will not choose, merge, rename or delete"));
        }

        [Test]
        public void HiddenLayerDoesNotChangeFallbackConfirmationOrDirectoryIdentity()
        {
            var result = Resolve(null, null, null, new HashSet<string>(PathComparer)
            {
                Path.Combine(m_unityRoot, ".opencode", "opencode.jsonc"),
            });
            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.UnityProjectFallback));
            Assert.That(result.RequiresConfirmation, Is.True);
            Assert.That(result.WorkspaceRoot, Is.EqualTo(Path.GetFullPath(m_unityRoot)));
            Assert.That(result.CanonicalWorkspaceRoot, Is.EqualTo(NormalizeCanonical(m_unityRoot)));
            Assert.That(result.HasHiddenLayer, Is.True);
            Assert.That(result.ConfigPath, Is.Null);
        }

        [Test]
        public void InvalidSavedRootIsReportedBeforeGitTargetIsUsed()
        {
            var result = Resolve(null, m_root + "-other", m_root, new HashSet<string>(PathComparer));

            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.GitWorktree));
            Assert.That(result.SavedWorkspaceRejected, Is.True);
        }

        [Test]
        public void WslWorkspaceIsCanonicalizedBeforeItIsMappedToAHostPath()
        {
            const string windowsUnityRoot = "C:/workspace/game";
            const string wslWorkspaceRoot = "/mnt/c/workspace";
            string observedHostInput = null;

            var result = OpenCodeConfigPathResolver.Resolve(
                windowsUnityRoot,
                wslWorkspaceRoot,
                null,
                path => path.StartsWith("/mnt/c/", StringComparison.Ordinal) ? "c:/" + path.Substring(7) : path.ToLowerInvariant(),
                path =>
                {
                    observedHostInput = path;
                    return "C:/workspace";
                },
                _ => null,
                _ => false);

            Assert.That(observedHostInput, Is.EqualTo(wslWorkspaceRoot));
            Assert.That(result.CanonicalWorkspaceRoot, Is.EqualTo("c:/workspace"));
            Assert.That(result.CanonicalUnityProjectRoot, Is.EqualTo("c:/workspace/game"));
            Assert.That(result.WorkspaceRoot, Is.EqualTo("C:/workspace"));
        }

        [Test]
        public void CanonicalFilesystemRootContainsNestedProject()
        {
            var result = OpenCodeConfigPathResolver.Resolve(
                "/game",
                "/",
                null,
                path => path,
                path => path,
                _ => null,
                _ => false);

            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.Explicit));
            Assert.That(result.IsNestedProject, Is.True);
        }

        [TestCase("C:\\workspace\\game", '\\', "windows")]
        [TestCase("/mnt/c/workspace/game", '/', "wsl")]
        [TestCase("/home/user/game", '/', "posix")]
        public void HostEnvironmentComesFromUnityProjectNotCandidate(string unityRoot, char separator, string expected)
        {
            Assert.That(OpenCodeConfigPathResolver.HostEnvironmentFor(unityRoot, separator), Is.EqualTo(expected));
        }

        [Test]
        public void WriteTargetValidationBindsExactDisplayedFallbackPath()
        {
            var target = new OpenCodeConfigPathResolver.Result
            {
                ConfigPath = Path.Combine(m_unityRoot, "opencode.jsonc"),
                RequiresConfirmation = true,
            };

            Assert.That(GatewayNetworkModeManager.ValidateOpenCodeWriteTarget(target, target.ConfigPath, null), Does.Contain("requires confirmation"));
            Assert.That(GatewayNetworkModeManager.ValidateOpenCodeWriteTarget(target, target.ConfigPath, target.ConfigPath), Is.Null);
            Assert.That(GatewayNetworkModeManager.ValidateOpenCodeWriteTarget(target, Path.Combine(m_root, "opencode.jsonc"), target.ConfigPath), Does.Contain("target changed"));
        }

        [Test]
        public void WriteTargetValidationDoesNotRequireFallbackConfirmationForGitTarget()
        {
            var target = new OpenCodeConfigPathResolver.Result
            {
                ConfigPath = Path.Combine(m_root, "opencode.jsonc"),
                RequiresConfirmation = false,
            };

            Assert.That(GatewayNetworkModeManager.ValidateOpenCodeWriteTarget(target, target.ConfigPath, null), Is.Null);
        }

        [Test]
        public void SetupTargetGateRejectsConflictOrPathDriftBeforeWrite()
        {
            var expectedPath = Path.Combine(m_root, "opencode.jsonc");
            var conflict = Resolve(m_root, null, null, new HashSet<string>(PathComparer)
            {
                Path.Combine(m_root, "opencode.jsonc"),
                Path.Combine(m_root, "opencode.json"),
            });
            Assert.That(
                GatewayNetworkModeManager.ValidateOpenCodeSetupTarget(conflict, true, expectedPath, null),
                Does.StartWith("Both OpenCode config files exist"));

            var drifted = new OpenCodeConfigPathResolver.Result
            {
                ConfigPath = Path.Combine(m_root, "opencode.json"),
            };
            Assert.That(
                GatewayNetworkModeManager.ValidateOpenCodeSetupTarget(drifted, true, expectedPath, null),
                Does.Contain("target changed"));
        }

        [Test]
        public void ManualWorkspaceSelectionRejectsEmptyInsteadOfSavingFallback()
        {
            Assert.Throws<ArgumentException>(() => GatewayNetworkModeManager.ValidateOpenCodeWorkspaceSelection(m_unityRoot, string.Empty));
            Assert.Throws<ArgumentException>(() => GatewayNetworkModeManager.ValidateOpenCodeWorkspaceSelection(m_unityRoot, "   "));
        }

        [Test]
        public void ManualWorkspaceSelectionUsesExplicitContainingRoot()
        {
            var result = GatewayNetworkModeManager.ValidateOpenCodeWorkspaceSelection(m_unityRoot, m_root);

            Assert.That(result.Source, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.Explicit));
            Assert.That(result.WorkspaceRoot, Is.EqualTo(Path.GetFullPath(m_root)));
            Assert.That(result.IsNestedProject, Is.True);
            Assert.That(result.RequiresConfirmation, Is.False);
        }

        [Test]
        public void WorkspacePreferenceIsProjectScopedAndRoundTrips()
        {
            var firstKey = GatewayNetworkModeManager.OpenCodeWorkspacePrefsKey(m_unityRoot);
            var secondKey = GatewayNetworkModeManager.OpenCodeWorkspacePrefsKey(Path.Combine(m_root, "other-game"));
            try
            {
                Assert.That(firstKey, Is.Not.EqualTo(secondKey));
                EditorPrefs.SetString(firstKey, m_root);
                Assert.That(GatewayNetworkModeManager.SavedOpenCodeWorkspaceRoot(m_unityRoot), Is.EqualTo(m_root));
            }
            finally
            {
                EditorPrefs.DeleteKey(firstKey);
                EditorPrefs.DeleteKey(secondKey);
            }
        }

        [Test]
        public void PublicManualSaveStatusAndAutoResetRoundTripForCurrentProject()
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

                var status = GatewayNetworkModeManager.GetStatus(GatewayNetworkModeManager.ModeWindowsStdio);
                Assert.That(status.OpenCodeTargetAvailable, Is.True);
                Assert.That(status.OpenCodeTargetSource, Is.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.Saved.ToString()));
                Assert.That(status.OpenCodeWorkspaceRoot, Is.EqualTo(Path.GetFullPath(workspaceRoot)));
                Assert.That(status.OpenCodeNestedProject, Is.True);

                var reset = GatewayNetworkModeManager.ResetOpenCodeWorkspaceRoot();
                Assert.That(reset.Success, Is.True, reset.Message);
                Assert.That(GatewayNetworkModeManager.SavedOpenCodeWorkspaceRoot(projectRoot), Is.Empty);
            }
            finally
            {
                if (hadPrevious) EditorPrefs.SetString(key, previous);
                else EditorPrefs.DeleteKey(key);
                GatewayNetworkModeManager.InvalidateOpenCodeGitRootCache();
            }
        }

        [Test]
        public void GetStatusRejectsInvalidSavedWorkspaceBeforeAutomaticResolution()
        {
            var projectRoot = ProjectPaths.ProjectRoot;
            var key = GatewayNetworkModeManager.OpenCodeWorkspacePrefsKey(projectRoot);
            var hadPrevious = EditorPrefs.HasKey(key);
            var previous = EditorPrefs.GetString(key, string.Empty);
            try
            {
                EditorPrefs.SetString(key, projectRoot + "-not-containing-project");
                GatewayNetworkModeManager.InvalidateOpenCodeGitRootCache();

                var status = GatewayNetworkModeManager.GetStatus(GatewayNetworkModeManager.ModeWindowsStdio);
                Assert.That(status.OpenCodeTargetAvailable, Is.True);
                Assert.That(status.OpenCodeSavedWorkspaceRejected, Is.True);
                Assert.That(status.OpenCodeTargetSource, Is.Not.EqualTo(OpenCodeConfigPathResolver.WorkspaceSource.Saved.ToString()));
            }
            finally
            {
                if (hadPrevious) EditorPrefs.SetString(key, previous);
                else EditorPrefs.DeleteKey(key);
                GatewayNetworkModeManager.InvalidateOpenCodeGitRootCache();
            }
        }

        [Test]
        public void ExplicitSiblingPrefixIsRejected()
        {
            var siblingPrefix = m_root + "-other";

            Assert.Throws<InvalidOperationException>(() => Resolve(siblingPrefix, null, null, new HashSet<string>(PathComparer)));
        }

        [Test]
        public void WorkspaceIdentityHashMatchesGatewayDomainSeparatedHash()
        {
            Assert.That(
                GatewayNetworkModeManager.OpenCodeWorkspaceIdentityHash("c:/workspace"),
                Is.EqualTo("sha256:6e66e4b0b0671092f63edbf87ebc3400c55b2ede64ceca9d1737170cae46f844"));
        }

        OpenCodeConfigPathResolver.Result Resolve(
            string explicitRoot,
            string savedRoot,
            string gitRoot,
            HashSet<string> files)
        {
            return OpenCodeConfigPathResolver.Resolve(
                m_unityRoot,
                explicitRoot,
                savedRoot,
                NormalizeCanonical,
                NormalizeHost,
                _ => gitRoot,
                files.Contains);
        }

        static string NormalizeCanonical(string path)
        {
            return Path.GetFullPath(path).Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        }

        static string NormalizeHost(string path)
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static StringComparer PathComparer => Environment.OSVersion.Platform == PlatformID.Win32NT
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }
}
