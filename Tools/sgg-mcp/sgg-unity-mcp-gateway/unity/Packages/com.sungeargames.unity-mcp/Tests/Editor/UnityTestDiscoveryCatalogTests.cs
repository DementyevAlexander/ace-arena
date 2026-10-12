using System.Collections.Generic;
using NUnit.Framework;
using SGG.UnityMCP.Editor.Commands;

namespace SGG.UnityMCP.Editor.Tests
{
    public sealed class UnityTestDiscoveryCatalogTests
    {
        [Test]
        public void PreservesProjectAndLocalPackageOwnershipAndRequestedMode()
        {
            var editRoot = Root(
                Assembly("Assembly-CSharp-Editor.dll", Suite("Project.EditorFixture", Leaf("Project.EditorFixture.Parameterized(42)", "Project.EditorFixture.Parameterized(42)|case=42"))),
                Assembly("Local.Package.Editor", Suite("Package.EditorFixture", Leaf("Package.EditorFixture.Test", "Package.EditorFixture.Test(7)"))));
            var playRoot = Root(Assembly("Game.PlayModeTests", Suite("PlayFixture", Leaf("PlayFixture.Runs", "PlayFixture.Runs(1)"))));

            var edit = UnityTestDiscoveryCatalog.BuildSnapshotForTests("editmode", editRoot);
            var play = UnityTestDiscoveryCatalog.BuildSnapshotForTests("playmode", playRoot);

            Assert.That(edit.Status, Is.EqualTo("ready"));
            Assert.That(play.Status, Is.EqualTo("ready"));
            Assert.That(Contains(edit, "Assembly-CSharp-Editor", "editmode"), Is.True);
            Assert.That(Contains(edit, "Local.Package.Editor", "editmode"), Is.True);
            Assert.That(Contains(play, "Game.PlayModeTests", "playmode"), Is.True);

            var parameterized = Find(edit, "Project.EditorFixture.Parameterized(42)");
            Assert.That(parameterized.UniqueName, Is.EqualTo("Project.EditorFixture.Parameterized(42)|case=42"));
            Assert.That(parameterized.RecommendedFilter, Is.EqualTo(parameterized.FullName));
            var resolved = UnityTestDiscoveryCatalog.ResolveSnapshot(edit, "editmode", parameterized.UniqueName, "Assembly-CSharp-Editor");
            Assert.That(resolved.Status, Is.EqualTo("resolved"));
            Assert.That(resolved.TestNames, Is.EqualTo(new[] { parameterized.FullName }));

            var shortFixture = UnityTestDiscoveryCatalog.ResolveSnapshot(edit, "editmode", "EditorFixture", "Assembly-CSharp-Editor");
            Assert.That(shortFixture.Status, Is.EqualTo("resolved"));
            Assert.That(shortFixture.ResolvedAssembly, Is.EqualTo("Assembly-CSharp-Editor"));
        }

        [Test]
        public void PendingCatalogDoesNotBecomeAuthoritativeZero()
        {
            var pending = UnityTestDiscoveryCatalog.PendingSnapshotForTests("editmode", "retrieve_test_list_pending");
            var page = UnityTestDiscoveryCatalog.QuerySnapshotForTests(pending, "missing", string.Empty, 0, 20);

            Assert.That(page.Status, Is.EqualTo("pending"));
            Assert.That(page.ResolutionStatus, Is.EqualTo("pending"));
            Assert.That(page.CoverageStatus, Is.EqualTo("pending"));
            Assert.That(page.TotalAvailable, Is.EqualTo(0));
            Assert.That(page.Candidates, Is.Empty);
        }

        [Test]
        public void CarriesOneDiscoveryGenerationFromCatalogReadThroughFilterResolution()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(Assembly("Generation.Editor", Suite("Generation.Fixture", Leaf("Generation.Fixture.Test", "Generation.Fixture.Test")))),
                23);

            var page = UnityTestDiscoveryCatalog.QuerySnapshotForTests(snapshot, "Generation.Fixture.Test", "Generation.Editor", 0, 1);
            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Generation.Fixture.Test", "Generation.Editor");

            Assert.That(page.Status, Is.EqualTo("ready"));
            Assert.That(page.DiscoveryGeneration, Is.EqualTo(23));
            Assert.That(resolution.Status, Is.EqualTo("resolved"));
            Assert.That(resolution.DiscoveryGeneration, Is.EqualTo(23));
        }

        [Test]
        public void ModeOnlyAdmissionRequiresAuthoritativeNonemptyPlayModeCatalogAndExactGeneration()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests("playmode",
                Root(Assembly("Game.PlayModeTests", Suite("Game.Fixture", Leaf("Game.Fixture.Runs", "Game.Fixture.Runs")))), 23);
            var ready = UnityTestDiscoveryCatalog.QuerySnapshotForTests(snapshot, string.Empty, "", 0, 1);
            var missing = UnityTestDiscoveryCatalog.QuerySnapshotForTests(snapshot, string.Empty, "Missing.Assembly", 0, 1);
            var pending = UnityTestDiscoveryCatalog.QuerySnapshotForTests(UnityTestDiscoveryCatalog.PendingSnapshotForTests("playmode", "retrieve_test_list_pending"), string.Empty, "", 0, 1);

            Assert.That(BuildTestAutomationCommands.RequireModeOnlyTestCatalog(ready, "playmode", "", 23), Is.Null);
            var drift = BuildTestAutomationCommands.RequireModeOnlyTestCatalog(ready, "playmode", "", 24);
            Assert.That(drift.ErrorCode, Is.EqualTo("catalog_stale"));
            Assert.That(drift.ErrorDetailsJson, Does.Contain("\"test_runner_execute_invoked\":false"));
            var zero = BuildTestAutomationCommands.RequireModeOnlyTestCatalog(missing, "playmode", "Missing.Assembly", 23);
            Assert.That(zero.ErrorCode, Is.EqualTo("test_filter_unresolved"));
            Assert.That(zero.ErrorDetailsJson, Does.Contain("mode_only_no_runnable_leaves"));
            Assert.That(zero.ErrorDetailsJson, Does.Contain("\"test_runner_execute_invoked\":false"));
            var stillPending = BuildTestAutomationCommands.RequireModeOnlyTestCatalog(pending, "playmode", "", 0);
            Assert.That(stillPending.ErrorCode, Is.EqualTo("test_filter_resolution_pending"));
        }

        [Test]
        public void SuitesIgnoredAndExplicitLeavesAreNotModeOnlyRunnableEvidence()
        {
            var trees = new[]
            {
                Root(Assembly("Game.PlayModeTests", Suite("Game.Fixture"))),
                Root(Assembly("Game.PlayModeTests", SuiteWithState("Game.Fixture", "Ignored", false,
                    LeafWithState("Game.Fixture.Ignored", "Game.Fixture.Ignored", "Ignored", false)))),
                Root(Assembly("Game.PlayModeTests", SuiteWithState("Game.Fixture", "Explicit", false,
                    LeafWithState("Game.Fixture.Explicit", "Game.Fixture.Explicit", "Explicit", false)))),
            };
            foreach (var tree in trees)
            {
                var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests("playmode", tree, 23);
                var catalog = UnityTestDiscoveryCatalog.QuerySnapshotForTests(snapshot, string.Empty, "", 0, 1);
                Assert.That(catalog.Status, Is.EqualTo("ready"));
                Assert.That(catalog.TotalAvailable, Is.GreaterThan(0));
                Assert.That(catalog.RunnableLeafCount, Is.Zero);
                var refusal = BuildTestAutomationCommands.RequireModeOnlyTestCatalog(catalog, "playmode", "", 23);
                Assert.That(refusal.ErrorCode, Is.EqualTo("test_filter_unresolved"));
                Assert.That(refusal.ErrorDetailsJson, Does.Contain("\"test_runner_execute_invoked\":false"));
            }
        }

        [Test]
        public void CompletedPlayModeRunInvalidatesOnlyThePlayModeDiscoveryGeneration()
        {
            UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            try
            {
                UnityTestDiscoveryCatalog.SetSnapshotForTests("playmode", Root(Assembly("Game.PlayModeTests", Suite("Game.Fixture", Leaf("Game.Fixture.Runs", "Game.Fixture.Runs")))));
                UnityTestDiscoveryCatalog.SetSnapshotForTests("editmode", Root(Assembly("Game.EditorTests", Suite("Game.EditorFixture", Leaf("Game.EditorFixture.Runs", "Game.EditorFixture.Runs")))));
                var oldPlay = UnityTestDiscoveryCatalog.Query("playmode", string.Empty, string.Empty, 0, 1);
                var oldEdit = UnityTestDiscoveryCatalog.Query("editmode", string.Empty, string.Empty, 0, 1);

                UnityTestDiscoveryCatalog.OnPlayModeStateChanged(UnityEditor.PlayModeStateChange.EnteredEditMode);

                var freshPlay = UnityTestDiscoveryCatalog.Query("playmode", string.Empty, string.Empty, 0, 1);
                var sameEdit = UnityTestDiscoveryCatalog.Query("editmode", string.Empty, string.Empty, 0, 1);
                Assert.That(oldPlay.Status, Is.EqualTo("ready"));
                Assert.That(freshPlay.DiscoveryGeneration, Is.Not.EqualTo(oldPlay.DiscoveryGeneration));
                Assert.That(sameEdit.DiscoveryGeneration, Is.EqualTo(oldEdit.DiscoveryGeneration));
                Assert.That(sameEdit.Status, Is.EqualTo("ready"));
            }
            finally
            {
                UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            }
        }

        [Test]
        public void AgedEmptyPlayModeSnapshotStartsOneFreshReadInsteadOfStayingAuthoritativeForever()
        {
            UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            try
            {
                UnityTestDiscoveryCatalog.SetSnapshotForTests("playmode", EmptyRoot(), System.DateTime.UtcNow.AddSeconds(-5));
                var refreshed = UnityTestDiscoveryCatalog.Query("playmode", string.Empty, string.Empty, 0, 1);

                Assert.That(refreshed.DiscoveryGeneration, Is.GreaterThan(1));
            }
            finally
            {
                UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            }
        }

        [Test]
        public void ExplicitOnlySelectionKeepsItsBoundGenerationAfterEmptyModeOnlyRecheckInterval()
        {
            UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            try
            {
                UnityTestDiscoveryCatalog.SetSnapshotForTests("playmode", Root(Assembly("Game.PlayModeTests",
                    SuiteWithState("Game.Fixture", "Explicit", false,
                        LeafWithState("Game.Fixture.Explicit", "Game.Fixture.Explicit", "Explicit", false)))),
                    System.DateTime.UtcNow.AddSeconds(-5));
                var before = UnityTestDiscoveryCatalog.Query("playmode", "Explicit", string.Empty, 0, 1);
                var exact = UnityTestDiscoveryCatalog.Resolve("playmode", "Game.Fixture.Explicit", "Game.PlayModeTests");

                Assert.That(before.Status, Is.EqualTo("ready"));
                Assert.That(before.RunnableLeafCount, Is.Zero);
                Assert.That(exact.IsResolved, Is.True);
                Assert.That(exact.DiscoveryGeneration, Is.EqualTo(before.DiscoveryGeneration));
            }
            finally
            {
                UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            }
        }

        [Test]
        public void CompletionAgeRatherThanSlowDiscoveryStartControlsEmptyRecheck()
        {
            UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            try
            {
                UnityTestDiscoveryCatalog.SetSnapshotForTests("playmode", EmptyRoot(), System.DateTime.UtcNow);
                var before = UnityTestDiscoveryCatalog.Query("playmode", "Explicit", string.Empty, 0, 1);
                var immediate = UnityTestDiscoveryCatalog.Query("playmode", string.Empty, string.Empty, 0, 1);

                Assert.That(immediate.DiscoveryGeneration, Is.EqualTo(before.DiscoveryGeneration));
                Assert.That(immediate.Status, Is.EqualTo("ready"));
            }
            finally
            {
                UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            }
        }

        [Test]
        public void AgedTrulyEmptyPlayModeSnapshotCanBeRefetchedByFilteredRead()
        {
            UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            try
            {
                UnityTestDiscoveryCatalog.SetSnapshotForTests("playmode", EmptyRoot(), System.DateTime.UtcNow.AddSeconds(-5));
                var current = UnityTestDiscoveryCatalog.Query("playmode", "Game.Fixture.Runs", string.Empty, 0, 1);
                Assert.That(current.DiscoveryGeneration, Is.GreaterThan(1));
            }
            finally
            {
                UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            }
        }

        [Test]
        public void LaterPageDoesNotReplaceAnAgedNoRunnablePlayModeSnapshot()
        {
            UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            try
            {
                UnityTestDiscoveryCatalog.SetSnapshotForTests("playmode", Root(Assembly("Game.PlayModeTests", Suite("Game.Fixture"))),
                    System.DateTime.UtcNow.AddSeconds(-5));
                var page = UnityTestDiscoveryCatalog.Query("playmode", string.Empty, string.Empty, 1, 1);
                Assert.That(page.Status, Is.EqualTo("ready"));
                Assert.That(page.RunnableLeafCount, Is.Zero);
                var laterPage = UnityTestDiscoveryCatalog.Query("playmode", string.Empty, string.Empty, 2, 1);
                Assert.That(laterPage.DiscoveryGeneration, Is.EqualTo(page.DiscoveryGeneration));
                var initialPage = UnityTestDiscoveryCatalog.Query("playmode", string.Empty, string.Empty, 0, 1);
                Assert.That(initialPage.DiscoveryGeneration, Is.Not.EqualTo(page.DiscoveryGeneration));
            }
            finally
            {
                UnityTestDiscoveryCatalog.ClearSnapshotsForTests();
            }
        }

        [Test]
        public void ZeroAndAmbiguousFiltersFailClosed()
        {
            var root = Root(
                Assembly("A.Editor", Suite("Alpha.Fixture", Leaf("Alpha.Fixture.Shared", "Alpha.Fixture.Shared(1)"))),
                Assembly("B.Editor", Suite("Beta.Fixture", Leaf("Beta.Fixture.Shared", "Beta.Fixture.Shared(1)"))));
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests("editmode", root);

            var zero = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "does-not-exist", string.Empty);
            var ambiguous = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Shared", string.Empty);
            var assemblyMismatch = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Alpha.Fixture.Shared", "Other.Editor");
            var modeMismatch = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "playmode", "Alpha.Fixture.Shared", string.Empty);

            Assert.That(zero.Status, Is.EqualTo("unresolved"));
            Assert.That(zero.Reason, Is.EqualTo("no_runnable_match"));
            Assert.That(ambiguous.Status, Is.EqualTo("ambiguous"));
            Assert.That(ambiguous.Candidates.Count, Is.LessThanOrEqualTo(UnityTestDiscoveryCatalog.MaxResolutionCandidates));
            Assert.That(assemblyMismatch.Status, Is.EqualTo("unresolved"));
            Assert.That(assemblyMismatch.Reason, Is.EqualTo("assembly_mismatch"));
            Assert.That(modeMismatch.Status, Is.EqualTo("unresolved"));
            Assert.That(modeMismatch.Reason, Is.EqualTo("mode_mismatch"));
        }

        // Synthetic exact-suite admission regressions, not an originating-host
        // reproduction or root-cause claim for MCP-PR-223.
        [TestCase("editmode")]
        [TestCase("playmode")]
        public void ExactEmptySuiteRefusalPreservesCandidateAndGeneration(string mode)
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Empty"))), 23);

            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Empty", "Selection.Tests");
        }

        [TestCase("editmode", "Ignored")]
        [TestCase("playmode", "Ignored")]
        [TestCase("editmode", "Skipped")]
        [TestCase("playmode", "Skipped")]
        [TestCase("editmode", "NotRunnable")]
        [TestCase("playmode", "NotRunnable")]
        public void ExactSuiteWithOnlyBlockedLeavesCannotUseTheirRunnableFlags(string mode, string runState)
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent",
                    LeafWithState("Selection.Parent.Blocked", "Selection.Parent.Blocked", runState, true)))), 23);

            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Parent", "Selection.Tests");
            var leaf = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, "Selection.Parent.Blocked", "Selection.Tests");
            Assert.That(leaf.Status, Is.EqualTo("unresolved"));
            Assert.That(leaf.Reason, Is.EqualTo("candidate_not_runnable"));
            Assert.That(leaf.DiscoveryGeneration, Is.EqualTo(23));
        }

        [TestCase("editmode", false)]
        [TestCase("playmode", false)]
        [TestCase("editmode", true)]
        [TestCase("playmode", true)]
        public void RunnablePrefixMatchingSiblingsCannotRescueAnEmptyStructuralSubtree(string mode, bool siblingFirst)
        {
            var target = Suite("Selection.Parent");
            var sibling = Suite("Outside.Fixture",
                Leaf("Selection.Parent.Case", "Selection.Parent.Case"),
                Leaf("Selection.ParentExtra.Case", "Selection.ParentExtra.Case"));
            var children = siblingFirst ? new[] { sibling, target } : new[] { target, sibling };
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", HierarchySuite("Selection.Catalog", children))), 23);

            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Parent", "Selection.Tests");
            AssertExactSuiteResolved(snapshot, mode, "Outside.Fixture", "Selection.Tests");
            var catalog = UnityTestDiscoveryCatalog.QuerySnapshotForTests(snapshot, string.Empty, "Selection.Tests", 0, 20);
            Assert.That(catalog.RunnableLeafCount, Is.EqualTo(2));
        }

        [TestCase("editmode")]
        [TestCase("playmode")]
        public void ExactSuiteCountsStructurallyNestedLeavesWithoutNamePrefixInference(string mode)
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent",
                    HierarchySuite("Different.Hierarchy", Suite("Unrelated.Fixture",
                        Leaf("Other.Namespace.Case", "Other.Namespace.Case|unique")))))), 23);

            AssertExactSuiteResolved(snapshot, mode, "Selection.Parent", "Selection.Tests");
            AssertExactSuiteResolved(snapshot, mode, "Different.Hierarchy", "Selection.Tests");
            AssertExactSuiteResolved(snapshot, mode, "Unrelated.Fixture", "Selection.Tests");
            var leaf = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, "Other.Namespace.Case|unique", "Selection.Tests");
            Assert.That(leaf.Status, Is.EqualTo("resolved"));
            Assert.That(leaf.Reason, Is.EqualTo("exact_leaf"));
            Assert.That(leaf.TestNames, Is.EqualTo(new[] { "Other.Namespace.Case" }));
            Assert.That(leaf.DiscoveryGeneration, Is.EqualTo(23));
        }

        [TestCase("editmode")]
        [TestCase("playmode")]
        public void ExactSuiteContainingOnlyEmptyNestedSuitesHasNoSelectableLeaves(string mode)
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent",
                    Suite("Selection.Parent.Empty"),
                    HierarchySuite("Unrelated.Hierarchy", Suite("Unrelated.Nested.Empty"))))), 23);

            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Parent", "Selection.Tests");
            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Parent.Empty", "Selection.Tests");
            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Unrelated.Hierarchy", "Selection.Tests");
            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Unrelated.Nested.Empty", "Selection.Tests");
        }

        [TestCase("editmode", "Explicit", false)]
        [TestCase("playmode", "Explicit", false)]
        [TestCase("editmode", "Runnable", true)]
        [TestCase("playmode", "Runnable", true)]
        public void ExactSuiteCountsExplicitDescendantsWithoutBroadeningSubstringSelection(string mode, string leafRunState, bool leafRunnable)
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", SuiteWithState("Selection.Explicit", "Explicit", false,
                    LeafWithState("Selection.Explicit.Child", "Selection.Explicit.Child|unique", leafRunState, leafRunnable)))), 23);

            AssertExactSuiteResolved(snapshot, mode, "Selection.Explicit", "Selection.Tests");
            var leaf = Find(snapshot, "Selection.Explicit.Child");
            Assert.That(leaf.Runnable, Is.False);
            var exactLeaf = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, leaf.UniqueName, "Selection.Tests");
            Assert.That(exactLeaf.Status, Is.EqualTo("resolved"));
            Assert.That(exactLeaf.Reason, Is.EqualTo("exact_leaf"));
            Assert.That(exactLeaf.TestNames, Is.EqualTo(new[] { leaf.FullName }));
            Assert.That(exactLeaf.DiscoveryGeneration, Is.EqualTo(23));
            var substring = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, "Child", "Selection.Tests");
            Assert.That(substring.Status, Is.EqualTo("unresolved"));
            Assert.That(substring.Reason, Is.EqualTo("no_runnable_match"));
            Assert.That(substring.DiscoveryGeneration, Is.EqualTo(23));
            var catalog = UnityTestDiscoveryCatalog.QuerySnapshotForTests(snapshot, string.Empty, "Selection.Tests", 0, 20);
            Assert.That(catalog.RunnableLeafCount, Is.Zero);
        }

        [TestCase("editmode")]
        [TestCase("playmode")]
        public void ForeignAssemblyDescendantsCountOnlyForTheirOwnStructuralSuites(string mode)
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent",
                    Assembly("Foreign.Tests.dll", Suite("Foreign.Fixture",
                        Leaf("Selection.Parent.ForeignCase", "Selection.Parent.ForeignCase")))))), 23);

            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Parent", "Selection.Tests");
            AssertExactSuiteResolved(snapshot, mode, "Foreign.Fixture", "Foreign.Tests");
            var mismatch = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, "Foreign.Fixture", "Selection.Tests");
            Assert.That(mismatch.Status, Is.EqualTo("unresolved"));
            Assert.That(mismatch.Reason, Is.EqualTo("assembly_mismatch"));
            Assert.That(mismatch.DiscoveryGeneration, Is.EqualTo(23));
            Assert.That(Find(snapshot, "Selection.Parent.ForeignCase").AssemblyName, Is.EqualTo("Foreign.Tests"));
        }

        [TestCase("editmode", null)]
        [TestCase("playmode", null)]
        [TestCase("editmode", "")]
        [TestCase("playmode", "")]
        public void DisplayAndUniqueNamesCannotRescueALeafWithoutExecutableFullName(string mode, string fullName)
        {
            // Keep an authoritative identity and display name so this is a ready
            // catalog with a missing executable name, not an incomplete scan.
            var child = Leaf(fullName, "Selection.Parent.UniqueOnly");
            child.Name = "Selection.Parent.DisplayOnly";
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent", child))), 23);

            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Parent", "Selection.Tests");
            var leaf = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, child.UniqueName, "Selection.Tests");
            Assert.That(leaf.Status, Is.EqualTo("unresolved"));
            Assert.That(leaf.Reason, Is.EqualTo("runnable_name_missing"));
            Assert.That(leaf.DiscoveryGeneration, Is.EqualTo(23));
            Assert.That(leaf.Candidates, Has.Count.EqualTo(1));
            Assert.That(leaf.Candidates[0].FullName, Is.Empty);
        }

        [TestCase("editmode", "Ignored")]
        [TestCase("playmode", "Ignored")]
        [TestCase("editmode", "Skipped")]
        [TestCase("playmode", "Skipped")]
        [TestCase("editmode", "NotRunnable")]
        [TestCase("playmode", "NotRunnable")]
        public void InheritedBlockedStateExcludesRunnableAndExplicitLeafDescendants(string mode, string blockedState)
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent",
                    SuiteWithState("Selection.Parent.Blocked", blockedState, true,
                        Leaf("Selection.Parent.Blocked.Runnable", "Selection.Parent.Blocked.Runnable"),
                        SuiteWithState("Selection.Parent.Blocked.Explicit", "Explicit", true,
                            LeafWithState("Selection.Parent.Blocked.Explicit.Child", "Selection.Parent.Blocked.Explicit.Child", "Explicit", true)))))), 23);

            AssertExactSuiteHasNoSelectableLeaves(snapshot, mode, "Selection.Parent", "Selection.Tests");
            var blocked = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, "Selection.Parent.Blocked.Explicit", "Selection.Tests");
            Assert.That(blocked.Status, Is.EqualTo("unresolved"));
            Assert.That(blocked.Reason, Is.EqualTo("candidate_not_runnable"));
            Assert.That(blocked.DiscoveryGeneration, Is.EqualTo(23));
            Assert.That(Find(snapshot, "Selection.Parent.Blocked.Runnable").Runnable, Is.False);
            Assert.That(Find(snapshot, "Selection.Parent.Blocked.Explicit.Child").Runnable, Is.False);
        }

        [TestCase("editmode")]
        [TestCase("playmode")]
        public void RunnableNonSuiteBranchWithChildrenInvalidatesSuiteAndExactLeafResolution(string mode)
        {
            var branch = Leaf("Selection.Parent.Malformed", "Selection.Parent.Malformed|unique");
            branch.IsSuite = false;
            branch.HasChildren = true;
            branch.Children = new[]
            {
                Suite("Selection.Parent.Malformed.Empty"),
                HierarchySuite("Unrelated.Empty"),
            };
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent", branch))), 23);

            Assert.That(snapshot.Status, Is.EqualTo("incomplete"));
            Assert.That(snapshot.FailureReason, Is.EqualTo("leaf_has_children"));
            Assert.That(snapshot.Authoritative, Is.False);
            Assert.That(snapshot.Candidates, Is.Empty);
            Assert.That(snapshot.DiscoveryGeneration, Is.EqualTo(23));
            foreach (var filter in new[] { "Selection.Parent", branch.FullName, branch.UniqueName })
            {
                var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, filter, "Selection.Tests");
                Assert.That(resolution.Status, Is.EqualTo("incomplete"));
                Assert.That(resolution.Reason, Is.EqualTo("leaf_has_children"));
                Assert.That(resolution.IsResolved, Is.False);
                Assert.That(resolution.Candidates, Is.Empty);
                Assert.That(resolution.TestNames, Is.Empty);
                Assert.That(resolution.DiscoveryGeneration, Is.EqualTo(23));
            }
        }

        [TestCase("editmode")]
        [TestCase("playmode")]
        public void PartialBoundedTraversalCannotAdmitASuiteAfterSeeingASelectableLeaf(string mode)
        {
            var deep = Suite("Bounds.Empty");
            for (var index = 0; index <= UnityTestDiscoveryCatalog.MaxDepth; index++)
            {
                deep = HierarchySuite("Bounds.Level" + index, deep);
            }
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(mode,
                Root(Assembly("Selection.Tests", Suite("Selection.Parent",
                    Leaf("Selection.Parent.First", "Selection.Parent.First"), deep))), 23);

            Assert.That(snapshot.Status, Is.EqualTo("incomplete"));
            Assert.That(snapshot.FailureReason, Is.EqualTo("max_depth"));
            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, "Selection.Parent", "Selection.Tests");
            Assert.That(resolution.Status, Is.EqualTo("incomplete"));
            Assert.That(resolution.Reason, Is.EqualTo("max_depth"));
            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.TestNames, Is.Empty);
            Assert.That(resolution.DiscoveryGeneration, Is.EqualTo(23));
        }

        [Test]
        public void FocusedSubstringBindsOnlyMatchingLeaves()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(Assembly(
                    "Focused.Editor",
                    Suite(
                        "Focused.Fixture",
                        Leaf("Focused.Fixture.TargetCase", "Focused.Fixture.TargetCase"),
                        Leaf("Focused.Fixture.UnrelatedCase", "Focused.Fixture.UnrelatedCase")))));

            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Target", "Focused.Editor");

            Assert.That(resolution.Status, Is.EqualTo("resolved"));
            Assert.That(resolution.Reason, Is.EqualTo("substring_single_fixture"));
            Assert.That(resolution.TestNames, Is.EqualTo(new[] { "Focused.Fixture.TargetCase" }));
        }

        [Test]
        public void IdenticalFixtureNamesAcrossAssembliesRemainAmbiguous()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(
                    Assembly("Project.Editor", Suite("Shared.Fixture", Leaf("Shared.Fixture.Target", "Project.Target"))),
                    Assembly("Package.Editor", Suite("Shared.Fixture", Leaf("Shared.Fixture.Target", "Package.Target")))));

            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Targ", string.Empty);

            Assert.That(resolution.Status, Is.EqualTo("ambiguous"));
            Assert.That(resolution.Reason, Is.EqualTo("multiple_assemblies"));
        }

        [Test]
        public void UniqueIdentityWithSharedExecutableNameFailsClosed()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(Assembly(
                    "Parameterized.Editor",
                    Suite(
                        "Parameterized.Fixture",
                        Leaf("Parameterized.Fixture.Case", "Parameterized.Fixture.Case|one"),
                        Leaf("Parameterized.Fixture.Case", "Parameterized.Fixture.Case|two")))));

            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Parameterized.Fixture.Case|one", "Parameterized.Editor");

            Assert.That(resolution.Status, Is.EqualTo("ambiguous"));
            Assert.That(resolution.Reason, Is.EqualTo("unique_identity_has_non_unique_full_name"));
        }

        [Test]
        public void ExactSelectionOptsIntoExplicitLeafAndSuiteWithoutBroadeningSubstringRuns()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "playmode",
                Root(Assembly(
                    "Explicit.PlayMode",
                    SuiteWithState(
                        "Explicit.Fixture",
                        "Explicit",
                        false,
                        LeafWithState("Explicit.Fixture.Replay", "Explicit.Fixture.Replay|signed-zero", "Explicit", false),
                        LeafWithState("Explicit.Fixture.Ignored", "Explicit.Fixture.Ignored", "Ignored", false)))));

            var explicitCandidate = Find(snapshot, "Explicit.Fixture.Replay");
            var exactLeaf = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "playmode", explicitCandidate.UniqueName, "Explicit.PlayMode");
            var exactSuite = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "playmode", "Explicit.Fixture", "Explicit.PlayMode");
            var substring = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "playmode", "Replay", "Explicit.PlayMode");
            var ignored = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "playmode", "Explicit.Fixture.Ignored", "Explicit.PlayMode");

            Assert.That(explicitCandidate.RunState, Is.EqualTo("Explicit"));
            Assert.That(explicitCandidate.Runnable, Is.False);
            Assert.That(exactLeaf.Status, Is.EqualTo("resolved"));
            Assert.That(exactLeaf.Reason, Is.EqualTo("exact_leaf"));
            Assert.That(exactLeaf.TestNames, Is.EqualTo(new[] { explicitCandidate.FullName }));
            Assert.That(exactSuite.Status, Is.EqualTo("resolved"));
            Assert.That(exactSuite.Reason, Is.EqualTo("exact_suite"));
            Assert.That(substring.Status, Is.EqualTo("unresolved"));
            Assert.That(substring.Reason, Is.EqualTo("no_runnable_match"));
            Assert.That(ignored.Status, Is.EqualTo("unresolved"));
            Assert.That(ignored.Reason, Is.EqualTo("candidate_not_runnable"));
        }

        [Test]
        public void ExplicitUniqueIdentityWithSharedExecutableNameFailsClosed()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(Assembly(
                    "Explicit.Editor",
                    Suite(
                        "Explicit.ParameterizedFixture",
                        LeafWithState("Explicit.ParameterizedFixture.Case", "Explicit.ParameterizedFixture.Case|one", "Explicit", false),
                        LeafWithState("Explicit.ParameterizedFixture.Case", "Explicit.ParameterizedFixture.Case|two", "Explicit", false)))));

            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Explicit.ParameterizedFixture.Case|one", "Explicit.Editor");

            Assert.That(resolution.Status, Is.EqualTo("ambiguous"));
            Assert.That(resolution.Reason, Is.EqualTo("unique_identity_has_non_unique_full_name"));
        }

        [Test]
        public void ExplicitRunStateOverridesRunnableFlagAndPropagatesToRunnableDescendants()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "playmode",
                Root(Assembly(
                    "Explicit.PlayMode",
                    SuiteWithState(
                        "Explicit.Parent",
                        "Explicit",
                        true,
                        Leaf("Explicit.Parent.Child", "Explicit.Parent.Child")))));

            var suite = Find(snapshot, "Explicit.Parent");
            var child = Find(snapshot, "Explicit.Parent.Child");
            var exactChild = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "playmode", child.FullName, "Explicit.PlayMode");
            var substring = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "playmode", "Child", "Explicit.PlayMode");

            Assert.That(suite.Runnable, Is.False);
            Assert.That(child.RunState, Is.EqualTo("Runnable"));
            Assert.That(child.Runnable, Is.False);
            Assert.That(exactChild.Status, Is.EqualTo("resolved"));
            Assert.That(exactChild.Reason, Is.EqualTo("exact_leaf"));
            Assert.That(substring.Status, Is.EqualTo("unresolved"));
            Assert.That(substring.Reason, Is.EqualTo("no_runnable_match"));
        }

        [Test]
        public void NonRunnableRunStateOverridesRunnableFlagAndBlocksDescendants()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(Assembly(
                    "Ignored.Editor",
                    SuiteWithState(
                        "Ignored.Parent",
                        "Ignored",
                        true,
                        Leaf("Ignored.Parent.Child", "Ignored.Parent.Child")))));

            var suite = Find(snapshot, "Ignored.Parent");
            var child = Find(snapshot, "Ignored.Parent.Child");
            var exactSuite = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", suite.FullName, "Ignored.Editor");
            var exactChild = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", child.FullName, "Ignored.Editor");
            var substring = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Child", "Ignored.Editor");

            Assert.That(suite.Runnable, Is.False);
            Assert.That(child.Runnable, Is.False);
            Assert.That(exactSuite.Status, Is.EqualTo("unresolved"));
            Assert.That(exactChild.Status, Is.EqualTo("unresolved"));
            Assert.That(substring.Status, Is.EqualTo("unresolved"));
        }

        [Test]
        public void MissingChildrenMemberCannotProduceAuthoritativeZero()
        {
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests("editmode", new MissingChildrenAdaptor());

            Assert.That(snapshot.Status, Is.EqualTo("incomplete"));
            Assert.That(snapshot.FailureReason, Is.EqualTo("children_missing"));
            Assert.That(snapshot.Candidates, Is.Empty);
        }

        [Test]
        public void DisplayNameAliasDoesNotOverrideAuthoritativeTestIdentity()
        {
            var root = Root(Assembly(
                "Alias.Editor",
                HierarchySuite(
                    "Alias.Tests",
                    SuiteWithName("Alias.Tests.Bootstrap", "Tests", Leaf("Alias.Tests.Bootstrap.Runs", "Alias.Tests.Bootstrap.Runs")),
                    Suite("Alias.Tests.Other", Leaf("Alias.Tests.Other.Runs", "Alias.Tests.Other.Runs")))));
            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests("editmode", root);

            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, "editmode", "Tests", "Alias.Editor");

            Assert.That(resolution.Status, Is.EqualTo("ambiguous"));
            Assert.That(resolution.Reason, Is.EqualTo("multiple_fixtures_or_suites"));
        }

        [Test]
        public void ExactFilterResolutionIdentityIsDeterministicAndBindsLeafOrSuiteShape()
        {
            var first = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(Assembly("Identity.Editor", Suite("Identity.Fixture", Leaf("Identity.Fixture.CaseB", "Identity.Fixture.CaseB"), Leaf("Identity.Fixture.CaseA", "Identity.Fixture.CaseA")))));
            var reordered = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(Assembly("Identity.Editor", Suite("Identity.Fixture", Leaf("Identity.Fixture.CaseA", "Identity.Fixture.CaseA"), Leaf("Identity.Fixture.CaseB", "Identity.Fixture.CaseB")))));

            var firstCases = UnityTestDiscoveryCatalog.ResolveSnapshot(first, "editmode", "Case", "Identity.Editor");
            var reorderedCases = UnityTestDiscoveryCatalog.ResolveSnapshot(reordered, "editmode", "Case", "Identity.Editor");
            var leaf = UnityTestDiscoveryCatalog.ResolveSnapshot(first, "editmode", "Identity.Fixture.CaseA", "Identity.Editor");
            var suite = UnityTestDiscoveryCatalog.ResolveSnapshot(first, "editmode", "Identity.Fixture", "Identity.Editor");

            Assert.That(leaf.Status, Is.EqualTo("resolved"));
            Assert.That(leaf.Reason, Is.EqualTo("exact_leaf"));
            Assert.That(leaf.Candidates[0].IsSuite, Is.False);
            Assert.That(suite.Status, Is.EqualTo("resolved"));
            Assert.That(suite.Reason, Is.EqualTo("exact_suite"));
            Assert.That(suite.Candidates[0].IsSuite, Is.True);
            Assert.That(BuildTestAutomationCommands.TestFilterResolutionIdentity(firstCases), Is.EqualTo(BuildTestAutomationCommands.TestFilterResolutionIdentity(reorderedCases)));
            Assert.That(BuildTestAutomationCommands.TestFilterResolutionIdentity(leaf), Is.Not.EqualTo(BuildTestAutomationCommands.TestFilterResolutionIdentity(suite)));
        }

        [Test]
        public void DeterministicPaginationReachesBeyondLegacyReflectionPosition()
        {
            var firstLeaves = new List<FakeAdaptor>();
            var secondLeaves = new List<FakeAdaptor>();
            for (var index = 0; index < 10001; index++)
            {
                firstLeaves.Add(Leaf("Large.FirstFixture.Test" + index, "Large.FirstFixture.Test" + index + "(case)"));
                secondLeaves.Add(Leaf("Large.SecondFixture.Test" + index, "Large.SecondFixture.Test" + index + "(case)"));
            }

            var snapshot = UnityTestDiscoveryCatalog.BuildSnapshotForTests(
                "editmode",
                Root(
                    Assembly("Large.First.Editor", Suite("Large.FirstFixture", firstLeaves.ToArray())),
                    Assembly("Large.Second.Editor", Suite("Large.SecondFixture", secondLeaves.ToArray()))));
            var page = UnityTestDiscoveryCatalog.QuerySnapshotForTests(snapshot, string.Empty, string.Empty, 20000, 2);

            Assert.That(snapshot.Status, Is.EqualTo("ready"));
            Assert.That(page.TotalAvailable, Is.GreaterThan(20000));
            Assert.That(page.Cursor, Is.EqualTo(20000));
            Assert.That(page.Returned, Is.EqualTo(2));
            Assert.That(page.Candidates, Has.Count.EqualTo(2));
        }

        static void AssertExactSuiteHasNoSelectableLeaves(UnityTestDiscoveryCatalog.Snapshot snapshot, string mode, string fullName, string assemblyName)
        {
            var resolution = ResolveExactSuiteForAssertion(snapshot, mode, fullName, assemblyName);
            Assert.That(resolution.Status, Is.EqualTo("unresolved"));
            Assert.That(resolution.Reason, Is.EqualTo("exact_suite_no_selectable_leaves"));
            Assert.That(resolution.IsResolved, Is.False);
            Assert.That(resolution.TestNames, Is.Empty);
            Assert.That(resolution.ResolvedAssembly, Is.Empty);
        }

        static void AssertExactSuiteResolved(UnityTestDiscoveryCatalog.Snapshot snapshot, string mode, string fullName, string assemblyName)
        {
            var resolution = ResolveExactSuiteForAssertion(snapshot, mode, fullName, assemblyName);
            Assert.That(resolution.Status, Is.EqualTo("resolved"));
            Assert.That(resolution.Reason, Is.EqualTo("exact_suite"));
            Assert.That(resolution.IsResolved, Is.True);
            Assert.That(resolution.TestNames, Is.EqualTo(new[] { fullName }));
            Assert.That(resolution.ResolvedAssembly, Is.EqualTo(assemblyName));
        }

        static UnityTestDiscoveryCatalog.FilterResolution ResolveExactSuiteForAssertion(UnityTestDiscoveryCatalog.Snapshot snapshot, string mode, string fullName, string assemblyName)
        {
            Assert.That(snapshot.Status, Is.EqualTo("ready"));
            Assert.That(snapshot.DiscoveryGeneration, Is.EqualTo(23));
            var candidate = Find(snapshot, fullName);
            Assert.That(candidate.IsSuite, Is.True);
            Assert.That(candidate.AssemblyName, Is.EqualTo(assemblyName));
            var resolution = UnityTestDiscoveryCatalog.ResolveSnapshot(snapshot, mode, fullName, assemblyName);
            Assert.That(resolution.Mode, Is.EqualTo(mode));
            Assert.That(resolution.Filter, Is.EqualTo(fullName));
            Assert.That(resolution.AssemblyName, Is.EqualTo(assemblyName));
            Assert.That(resolution.DiscoveryGeneration, Is.EqualTo(23));
            Assert.That(resolution.Candidates, Has.Count.EqualTo(1));
            Assert.That(resolution.Candidates[0], Is.SameAs(candidate));
            return resolution;
        }

        static FakeAdaptor Root(params FakeAdaptor[] children)
        {
            return new FakeAdaptor { IsSuite = true, HasChildren = true, Children = children };
        }

        static FakeAdaptor EmptyRoot()
        {
            return new FakeAdaptor { IsSuite = true, HasChildren = false, Children = new FakeAdaptor[0] };
        }

        static FakeAdaptor Assembly(string assemblyName, FakeAdaptor child)
        {
            return new FakeAdaptor { IsTestAssembly = true, IsSuite = true, HasChildren = true, AssemblyName = assemblyName, Name = assemblyName, Children = new[] { child } };
        }

        static FakeAdaptor Suite(string fullName, params FakeAdaptor[] children)
        {
            return SuiteWithName(fullName, fullName, children);
        }

        static FakeAdaptor HierarchySuite(string fullName, params FakeAdaptor[] children)
        {
            return new FakeAdaptor { IsSuite = true, HasChildren = children.Length > 0, FullName = fullName, UniqueName = fullName, Name = fullName, RunState = "Runnable", IsRunnable = true, Children = children };
        }

        static FakeAdaptor SuiteWithName(string fullName, string name, params FakeAdaptor[] children)
        {
            return new FakeAdaptor { IsSuite = true, HasChildren = children.Length > 0, FullName = fullName, UniqueName = fullName, Name = name, TypeInfo = fullName, RunState = "Runnable", IsRunnable = true, Children = children };
        }

        static FakeAdaptor SuiteWithState(string fullName, string runState, bool isRunnable, params FakeAdaptor[] children)
        {
            return new FakeAdaptor { IsSuite = true, HasChildren = children.Length > 0, FullName = fullName, UniqueName = fullName, Name = fullName, TypeInfo = fullName, RunState = runState, IsRunnable = isRunnable, Children = children };
        }

        static FakeAdaptor Leaf(string fullName, string uniqueName)
        {
            return new FakeAdaptor { FullName = fullName, UniqueName = uniqueName, Name = fullName, RunState = "Runnable", IsRunnable = true, Children = new FakeAdaptor[0] };
        }

        static FakeAdaptor LeafWithState(string fullName, string uniqueName, string runState, bool isRunnable)
        {
            return new FakeAdaptor { FullName = fullName, UniqueName = uniqueName, Name = fullName, RunState = runState, IsRunnable = isRunnable, Children = new FakeAdaptor[0] };
        }

        static UnityTestDiscoveryCatalog.TestCandidate Find(UnityTestDiscoveryCatalog.Snapshot snapshot, string fullName)
        {
            foreach (var candidate in snapshot.Candidates)
            {
                if (candidate.FullName == fullName)
                {
                    return candidate;
                }
            }

            Assert.Fail("Missing catalog candidate: " + fullName);
            return null;
        }

        static bool Contains(UnityTestDiscoveryCatalog.Snapshot snapshot, string assemblyName, string mode)
        {
            foreach (var candidate in snapshot.Candidates)
            {
                if (candidate.AssemblyName == assemblyName && candidate.Mode == mode)
                {
                    return true;
                }
            }

            return false;
        }

        sealed class FakeAdaptor
        {
            public bool IsTestAssembly { get; set; }
            public bool IsSuite { get; set; }
            public bool HasChildren { get; set; }
            public bool IsRunnable { get; set; }
            public string AssemblyName { get; set; }
            public string Name { get; set; }
            public string FullName { get; set; }
            public string UniqueName { get; set; }
            public string TypeInfo { get; set; }
            public string RunState { get; set; }
            public IEnumerable<FakeAdaptor> Children { get; set; } = new FakeAdaptor[0];
        }

        sealed class MissingChildrenAdaptor
        {
            public bool IsSuite { get { return true; } }
            public bool HasChildren { get { return true; } }
        }
    }
}
