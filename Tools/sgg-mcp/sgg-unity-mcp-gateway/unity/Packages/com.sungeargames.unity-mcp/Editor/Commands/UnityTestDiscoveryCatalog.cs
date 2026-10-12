using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Commands
{
    /// <summary>
    /// Reflection-only access to the Unity Test Runner discovery API.
    ///
    /// SGG.UnityMCP.Editor intentionally does not reference UnityEditor.TestRunner.  The
    /// catalog therefore keeps the Test Runner objects behind object/Type/Delegate
    /// boundaries and keeps the API and callback alive until Unity invokes the callback.
    /// </summary>
    [InitializeOnLoad]
    public static class UnityTestDiscoveryCatalog
    {
        public const int MaxNodes = 100000;
        public const int MaxDepth = 64;
        public const int MaxChildrenPerNode = 20000;
        public const int MaxStringChars = 1024;
        public const int MaxResolutionCandidates = 32;
        public const int MaxResolutionNames = 256;
        public const int MaxPageSize = 500;
        public const int PendingTimeoutSeconds = 15;
        const int EmptyPlayModeRecheckSeconds = 2;

        const string EditMode = "editmode";
        const string PlayMode = "playmode";
        const string StatusPending = "pending";
        const string StatusReady = "ready";
        const string StatusUnavailable = "unavailable";
        const string StatusIncomplete = "incomplete";

        static readonly object s_gate = new object();
        static readonly object s_unreadableMember = new object();
        static readonly Dictionary<string, SnapshotState> s_states = new Dictionary<string, SnapshotState>(StringComparer.Ordinal);
        static int s_nextDiscoveryGeneration;

        static UnityTestDiscoveryCatalog()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        internal static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode || change == PlayModeStateChange.EnteredEditMode)
            {
                InvalidateAfterPlayModeRun();
            }
        }

        // A PlayMode execution can change the executable TestRunner tree even if the
        // command catalog and the cached package snapshot have not changed.
        internal static void InvalidateAfterPlayModeRun()
        {
            lock (s_gate)
            {
                s_states.Remove(PlayMode);
            }
        }

        internal static void SetSnapshotForTests(string mode, object root, DateTime? completedAtUtc = null)
        {
            var state = new SnapshotState(NormalizeMode(mode));
            lock (s_gate) s_states[state.Mode] = state;
            state.Complete(root);
            if (completedAtUtc.HasValue) state.SetCompletedAtUtcForTests(completedAtUtc.Value);
        }

        internal static void ClearSnapshotsForTests()
        {
            lock (s_gate) s_states.Clear();
        }

        public static CatalogQueryResult Query(string mode, string query, string assemblyName, int cursor, int maxResults)
        {
            var normalizedMode = NormalizeMode(mode);
            var normalizedQuery = (query ?? string.Empty).Trim();
            var normalizedAssembly = (assemblyName ?? string.Empty).Trim();
            var boundedCursor = Math.Max(0, cursor);
            var boundedMaxResults = Clamp(maxResults, 1, MaxPageSize);
            if (!string.IsNullOrEmpty(normalizedMode) && !IsKnownMode(normalizedMode))
            {
                return CatalogQueryResult.Unavailable(normalizedMode, "invalid_mode", StatusUnavailable, normalizedQuery, normalizedAssembly, boundedCursor, boundedMaxResults);
            }

            var snapshots = new List<Snapshot>();

            if (string.IsNullOrEmpty(normalizedMode))
            {
                snapshots.Add(EnsureSnapshot(EditMode));
                snapshots.Add(EnsureSnapshot(PlayMode, boundedCursor == 0, boundedCursor == 0 && string.IsNullOrEmpty(normalizedQuery)));
            }
            else
            {
                snapshots.Add(EnsureSnapshot(normalizedMode, normalizedMode == PlayMode && boundedCursor == 0,
                    normalizedMode == PlayMode && boundedCursor == 0 && string.IsNullOrEmpty(normalizedQuery)));
            }

            for (var index = 0; index < snapshots.Count; index++)
            {
                if (snapshots[index].Status == StatusPending)
                {
                    return CatalogQueryResult.Pending(normalizedMode, normalizedQuery, normalizedAssembly, boundedCursor, boundedMaxResults, snapshots[index].FailureReason, snapshots[index].DiscoveryGeneration);
                }

                if (snapshots[index].Status != StatusReady)
                {
                    return CatalogQueryResult.Unavailable(normalizedMode, snapshots[index].FailureReason, snapshots[index].Status, normalizedQuery, normalizedAssembly, boundedCursor, boundedMaxResults, snapshots[index].DiscoveryGeneration);
                }
            }

            var candidates = new List<TestCandidate>();
            for (var snapshotIndex = 0; snapshotIndex < snapshots.Count; snapshotIndex++)
            {
                var entries = snapshots[snapshotIndex].Candidates;
                for (var entryIndex = 0; entryIndex < entries.Count; entryIndex++)
                {
                    var candidate = entries[entryIndex];
                    if (!string.IsNullOrEmpty(normalizedAssembly)
                        && !string.Equals(candidate.AssemblyName, normalizedAssembly, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (!candidate.MatchesQuery(normalizedQuery))
                    {
                        continue;
                    }

                    candidates.Add(candidate);
                }
            }

            candidates.Sort(TestCandidate.Compare);
            var total = candidates.Count;
            var runnableLeafCount = CountRunnableLeaves(candidates);
            var start = Math.Min(boundedCursor, total);
            var returned = Math.Min(boundedMaxResults, total - start);
            var page = new List<TestCandidate>(returned);
            for (var index = 0; index < returned; index++)
            {
                page.Add(candidates[start + index]);
            }

            var nextCursor = start + returned < total ? start + returned : (int?)null;
            var discoveryGeneration = snapshots.Count == 1 ? snapshots[0].DiscoveryGeneration : 0;
            return CatalogQueryResult.Ready(normalizedMode, normalizedQuery, normalizedAssembly, boundedCursor, boundedMaxResults, total, page, nextCursor, discoveryGeneration, runnableLeafCount);
        }

        public static FilterResolution Resolve(string mode, string filter, string assemblyName)
        {
            var normalizedMode = NormalizeMode(mode);
            if (!IsKnownMode(normalizedMode))
            {
                return FilterResolution.Unresolved(normalizedMode, filter, assemblyName, "invalid_mode", null);
            }

            return ResolveSnapshot(EnsureSnapshot(normalizedMode), normalizedMode, filter, assemblyName);
        }

        /// <summary>
        /// Builds a complete snapshot from an adaptor-shaped object.  The production path
        /// reaches the same traversal from TestRunnerApi.  This entry point is deliberately
        /// bounded and is used by focused Editor tests without a compile-time Test Runner
        /// assembly reference.
        /// </summary>
        public static Snapshot BuildSnapshotForTests(string mode, object root)
        {
            return BuildSnapshot(NormalizeMode(mode), root);
        }

        public static Snapshot BuildSnapshotForTests(string mode, object root, int discoveryGeneration)
        {
            var snapshot = BuildSnapshot(NormalizeMode(mode), root);
            return new Snapshot(snapshot.Mode, snapshot.Status, snapshot.FailureReason, new List<TestCandidate>(snapshot.Candidates), Math.Max(0, discoveryGeneration));
        }

        public static Snapshot PendingSnapshotForTests(string mode, string reason)
        {
            return new Snapshot(NormalizeMode(mode), StatusPending, Bounded(reason, MaxStringChars), new List<TestCandidate>(), 0);
        }

        public static CatalogQueryResult QuerySnapshotForTests(Snapshot snapshot, string query, string assemblyName, int cursor, int maxResults)
        {
            if (snapshot == null)
            {
                return CatalogQueryResult.Unavailable(string.Empty, "snapshot_missing", StatusUnavailable, query, assemblyName, Math.Max(0, cursor), Clamp(maxResults, 1, MaxPageSize));
            }

            if (snapshot.Status != StatusReady)
            {
                return snapshot.Status == StatusPending
                    ? CatalogQueryResult.Pending(snapshot.Mode, query, assemblyName, Math.Max(0, cursor), Clamp(maxResults, 1, MaxPageSize), snapshot.FailureReason, snapshot.DiscoveryGeneration)
                    : CatalogQueryResult.Unavailable(snapshot.Mode, snapshot.FailureReason, snapshot.Status, query, assemblyName, Math.Max(0, cursor), Clamp(maxResults, 1, MaxPageSize), snapshot.DiscoveryGeneration);
            }

            var normalizedQuery = (query ?? string.Empty).Trim();
            var normalizedAssembly = (assemblyName ?? string.Empty).Trim();
            var boundedCursor = Math.Max(0, cursor);
            var boundedMaxResults = Clamp(maxResults, 1, MaxPageSize);
            var candidates = new List<TestCandidate>();
            for (var index = 0; index < snapshot.Candidates.Count; index++)
            {
                var candidate = snapshot.Candidates[index];
                if ((!string.IsNullOrEmpty(normalizedAssembly) && !string.Equals(candidate.AssemblyName, normalizedAssembly, StringComparison.Ordinal))
                    || !candidate.MatchesQuery(normalizedQuery))
                {
                    continue;
                }

                candidates.Add(candidate);
            }

            candidates.Sort(TestCandidate.Compare);
            var total = candidates.Count;
            var runnableLeafCount = CountRunnableLeaves(candidates);
            var start = Math.Min(boundedCursor, total);
            var returned = Math.Min(boundedMaxResults, total - start);
            var page = new List<TestCandidate>(returned);
            for (var index = 0; index < returned; index++)
            {
                page.Add(candidates[start + index]);
            }

            return CatalogQueryResult.Ready(snapshot.Mode, normalizedQuery, normalizedAssembly, boundedCursor, boundedMaxResults, total, page, start + returned < total ? start + returned : (int?)null, snapshot.DiscoveryGeneration, runnableLeafCount);
        }

        static int CountRunnableLeaves(IList<TestCandidate> candidates)
        {
            var count = 0;
            for (var index = 0; candidates != null && index < candidates.Count; index++)
            {
                if (!candidates[index].IsSuite && candidates[index].Runnable) count++;
            }
            return count;
        }

        public static FilterResolution ResolveSnapshot(Snapshot snapshot, string mode, string filter, string assemblyName)
        {
            var normalizedMode = NormalizeMode(mode);
            var normalizedFilter = (filter ?? string.Empty).Trim();
            var normalizedAssembly = (assemblyName ?? string.Empty).Trim();
            if (snapshot == null)
            {
                return FilterResolution.Unresolved(normalizedMode, normalizedFilter, normalizedAssembly, "snapshot_missing", null);
            }

            if (!string.Equals(snapshot.Mode, normalizedMode, StringComparison.Ordinal))
            {
                return FilterResolution.Unresolved(normalizedMode, normalizedFilter, normalizedAssembly, "mode_mismatch", null, snapshot.DiscoveryGeneration);
            }

            if (snapshot.Status == StatusPending)
            {
                return FilterResolution.Pending(normalizedMode, normalizedFilter, normalizedAssembly, snapshot.FailureReason, snapshot.DiscoveryGeneration);
            }

            if (snapshot.Status != StatusReady)
            {
                return FilterResolution.Unavailable(normalizedMode, normalizedFilter, normalizedAssembly, snapshot.Status, snapshot.FailureReason, snapshot.DiscoveryGeneration);
            }

            if (string.IsNullOrEmpty(normalizedFilter))
            {
                return FilterResolution.Unresolved(normalizedMode, normalizedFilter, normalizedAssembly, "filter_required", null, snapshot.DiscoveryGeneration);
            }

            var exactMatches = new List<TestCandidate>();
            var allNameMatches = new List<TestCandidate>();
            for (var index = 0; index < snapshot.Candidates.Count; index++)
            {
                var candidate = snapshot.Candidates[index];
                if (!candidate.HasExactName(normalizedFilter))
                {
                    continue;
                }

                allNameMatches.Add(candidate);
                if (string.IsNullOrEmpty(normalizedAssembly) || string.Equals(candidate.AssemblyName, normalizedAssembly, StringComparison.Ordinal))
                {
                    exactMatches.Add(candidate);
                }
            }

            exactMatches.Sort(TestCandidate.Compare);
            allNameMatches.Sort(TestCandidate.Compare);
            if (exactMatches.Count == 0 && allNameMatches.Count > 0 && !string.IsNullOrEmpty(normalizedAssembly))
            {
                return FilterResolution.Unresolved(normalizedMode, normalizedFilter, normalizedAssembly, "assembly_mismatch", allNameMatches, snapshot.DiscoveryGeneration);
            }

            if (exactMatches.Count == 1)
            {
                var exactMatch = exactMatches[0];
                if (!string.Equals(exactMatch.FullName, normalizedFilter, StringComparison.Ordinal)
                    && string.Equals(exactMatch.UniqueName, normalizedFilter, StringComparison.Ordinal))
                {
                    var executableNameMatches = new List<TestCandidate>();
                    for (var index = 0; index < snapshot.Candidates.Count; index++)
                    {
                        var candidate = snapshot.Candidates[index];
                        if (IsExactlySelectable(candidate)
                            && string.Equals(candidate.AssemblyName, exactMatch.AssemblyName, StringComparison.Ordinal)
                            && string.Equals(candidate.FullName, exactMatch.FullName, StringComparison.Ordinal))
                        {
                            executableNameMatches.Add(candidate);
                        }
                    }

                    if (executableNameMatches.Count > 1)
                    {
                        executableNameMatches.Sort(TestCandidate.Compare);
                        return FilterResolution.Ambiguous(normalizedMode, normalizedFilter, normalizedAssembly, "unique_identity_has_non_unique_full_name", executableNameMatches, snapshot.DiscoveryGeneration);
                    }
                }

                return ResolveExactCandidate(normalizedMode, normalizedFilter, normalizedAssembly, exactMatches[0], snapshot.DiscoveryGeneration);
            }

            if (exactMatches.Count > 1)
            {
                return FilterResolution.Ambiguous(normalizedMode, normalizedFilter, normalizedAssembly, "multiple_exact_candidates", exactMatches, snapshot.DiscoveryGeneration);
            }

            var runnableLeaves = new List<TestCandidate>();
            var allRunnableLeaves = new List<TestCandidate>();
            for (var index = 0; index < snapshot.Candidates.Count; index++)
            {
                var candidate = snapshot.Candidates[index];
                if (candidate.IsSuite || !candidate.Runnable || !candidate.MatchesQuery(normalizedFilter))
                {
                    continue;
                }

                allRunnableLeaves.Add(candidate);

                if (!string.IsNullOrEmpty(normalizedAssembly) && !string.Equals(candidate.AssemblyName, normalizedAssembly, StringComparison.Ordinal))
                {
                    continue;
                }

                runnableLeaves.Add(candidate);
            }

            runnableLeaves.Sort(TestCandidate.Compare);
            if (runnableLeaves.Count == 0)
            {
                allRunnableLeaves.Sort(TestCandidate.Compare);
                if (allRunnableLeaves.Count > 0 && !string.IsNullOrEmpty(normalizedAssembly))
                {
                    return FilterResolution.Unresolved(normalizedMode, normalizedFilter, normalizedAssembly, "assembly_mismatch", allRunnableLeaves, snapshot.DiscoveryGeneration);
                }

                var nonRunnableMatches = new List<TestCandidate>();
                for (var index = 0; index < snapshot.Candidates.Count; index++)
                {
                    var candidate = snapshot.Candidates[index];
                    if (!candidate.IsSuite && candidate.MatchesQuery(normalizedFilter)
                        && (string.IsNullOrEmpty(normalizedAssembly) || string.Equals(candidate.AssemblyName, normalizedAssembly, StringComparison.Ordinal)))
                    {
                        nonRunnableMatches.Add(candidate);
                    }
                }

                nonRunnableMatches.Sort(TestCandidate.Compare);
                return FilterResolution.Unresolved(normalizedMode, normalizedFilter, normalizedAssembly, "no_runnable_match", nonRunnableMatches, snapshot.DiscoveryGeneration);
            }

            var resolvedAssemblies = new List<string>();
            var fixtureKeys = new List<string>();
            for (var index = 0; index < runnableLeaves.Count; index++)
            {
                var resolvedAssembly = runnableLeaves[index].AssemblyName;
                if (!resolvedAssemblies.Contains(resolvedAssembly))
                {
                    resolvedAssemblies.Add(resolvedAssembly);
                }

                var fixtureKey = runnableLeaves[index].FixtureKey;
                if (!fixtureKeys.Contains(fixtureKey))
                {
                    fixtureKeys.Add(fixtureKey);
                }
            }

            if (resolvedAssemblies.Count != 1)
            {
                return FilterResolution.Ambiguous(normalizedMode, normalizedFilter, normalizedAssembly, "multiple_assemblies", runnableLeaves, snapshot.DiscoveryGeneration);
            }

            if (fixtureKeys.Count != 1)
            {
                return FilterResolution.Ambiguous(normalizedMode, normalizedFilter, normalizedAssembly, "multiple_fixtures_or_suites", runnableLeaves, snapshot.DiscoveryGeneration);
            }

            if (runnableLeaves.Count > MaxResolutionNames)
            {
                return FilterResolution.Ambiguous(normalizedMode, normalizedFilter, normalizedAssembly, "too_many_exact_leaf_names", runnableLeaves, snapshot.DiscoveryGeneration);
            }

            var names = new List<string>();
            for (var index = 0; index < runnableLeaves.Count; index++)
            {
                AddUnique(names, runnableLeaves[index].FullName, MaxResolutionNames);
            }

            if (names.Count == 0)
            {
                return FilterResolution.Unresolved(normalizedMode, normalizedFilter, normalizedAssembly, "runnable_name_missing", runnableLeaves, snapshot.DiscoveryGeneration);
            }

            if (names.Count != runnableLeaves.Count)
            {
                return FilterResolution.Ambiguous(normalizedMode, normalizedFilter, normalizedAssembly, "non_unique_executable_full_names", runnableLeaves, snapshot.DiscoveryGeneration);
            }

            return FilterResolution.Resolved(normalizedMode, normalizedFilter, normalizedAssembly, names, runnableLeaves, resolvedAssemblies[0], "substring_single_fixture", snapshot.DiscoveryGeneration);
        }

        static FilterResolution ResolveExactCandidate(string mode, string filter, string assemblyName, TestCandidate candidate, int discoveryGeneration)
        {
            if (!IsExactlySelectable(candidate))
            {
                return FilterResolution.Unresolved(mode, filter, assemblyName, "candidate_not_runnable", candidate == null ? null : new List<TestCandidate> { candidate }, discoveryGeneration);
            }

            if (candidate.IsSuite && candidate.ExactlySelectableLeafDescendantCount == 0)
            {
                return FilterResolution.Unresolved(mode, filter, assemblyName, "exact_suite_no_selectable_leaves", new List<TestCandidate> { candidate }, discoveryGeneration);
            }

            var names = new List<string>();
            AddUnique(names, candidate.FullName, MaxResolutionNames);
            if (names.Count == 0)
            {
                return FilterResolution.Unresolved(mode, filter, assemblyName, "runnable_name_missing", new List<TestCandidate> { candidate }, discoveryGeneration);
            }

            return FilterResolution.Resolved(mode, filter, assemblyName, names, new List<TestCandidate> { candidate }, candidate.AssemblyName, candidate.IsSuite ? "exact_suite" : "exact_leaf", discoveryGeneration);
        }

        static Snapshot EnsureSnapshot(string mode, bool recheckEmptyPlayMode = false, bool recheckNoRunnableModeOnly = false)
        {
            var normalizedMode = NormalizeMode(mode);
            SnapshotState state;
            var shouldStart = false;
            lock (s_gate)
            {
                if (!s_states.TryGetValue(normalizedMode, out state))
                {
                    state = new SnapshotState(normalizedMode);
                    s_states.Add(normalizedMode, state);
                    shouldStart = true;
                }
                else if (state.IsPendingExpired(DateTime.UtcNow))
                {
                    state.Fail("retrieve_test_list_timeout");
                }
                else if (state.CanRetryTimedOutRequest)
                {
                    state = new SnapshotState(normalizedMode);
                    s_states[normalizedMode] = state;
                    shouldStart = true;
                }
                else if (normalizedMode == PlayMode && state.Status == StatusReady
                    && ((recheckEmptyPlayMode && state.Candidates.Count == 0)
                        || (recheckNoRunnableModeOnly && state.RunnableLeafCount == 0))
                    && DateTime.UtcNow - state.CompletedAtUtc >= TimeSpan.FromSeconds(EmptyPlayModeRecheckSeconds))
                {
                    // One new bounded read per explicit caller after the empty snapshot ages;
                    // a transient post-Play zero must not remain an indefinitely ready catalog.
                    state = new SnapshotState(normalizedMode);
                    s_states[normalizedMode] = state;
                    shouldStart = true;
                }
            }

            if (shouldStart)
            {
                StartRequest(state);
            }

            return state.CurrentSnapshot();
        }

        static void StartRequest(SnapshotState state)
        {
            try
            {
                var apiType = FindType("UnityEditor.TestTools.TestRunner.Api.TestRunnerApi");
                var adaptorType = FindType("UnityEditor.TestTools.TestRunner.Api.ITestAdaptor");
                var modeType = FindType("UnityEditor.TestTools.TestRunner.Api.TestMode");
                if (apiType == null || adaptorType == null || modeType == null)
                {
                    state.Fail("test_runner_api_unavailable");
                    return;
                }

                var api = CreateApi(apiType);
                var receiver = new DiscoveryCallback(state);
                var callback = CreateCallback(receiver, adaptorType);
                state.RetainedApi = api;
                state.RetainedCallback = callback;
                state.RetainedReceiver = receiver;

                var retrieveMethod = FindRetrieveMethod(api.GetType(), callback, adaptorType);
                if (retrieveMethod == null)
                {
                    state.Fail("retrieve_test_list_unavailable");
                    return;
                }

                var testMode = Enum.Parse(modeType, state.Mode == PlayMode ? "PlayMode" : "EditMode");
                retrieveMethod.Invoke(api, BuildRetrieveArguments(retrieveMethod, callback, testMode));
            }
            catch (Exception exception)
            {
                state.Fail(Bounded(Unwrap(exception).GetType().Name + ": " + Unwrap(exception).Message, MaxStringChars));
            }
        }

        static object[] BuildRetrieveArguments(MethodInfo method, object callback, object testMode)
        {
            var parameters = method.GetParameters();
            var values = new object[parameters.Length];
            for (var index = 0; index < parameters.Length; index++)
            {
                var parameter = parameters[index];
                if (parameter.ParameterType.IsInstanceOfType(callback))
                {
                    values[index] = callback;
                }
                else if (testMode != null && parameter.ParameterType.IsInstanceOfType(testMode))
                {
                    values[index] = testMode;
                }
                else if (parameter.IsOptional)
                {
                    values[index] = parameter.DefaultValue;
                }
                else if (parameter.ParameterType == typeof(int))
                {
                    values[index] = 0;
                }
                else
                {
                    values[index] = null;
                }
            }

            return values;
        }

        static MethodInfo FindRetrieveMethod(Type apiType, object callback, Type adaptorType)
        {
            var methods = apiType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (var index = 0; index < methods.Length; index++)
            {
                var method = methods[index];
                if (!string.Equals(method.Name, "RetrieveTestList", StringComparison.Ordinal))
                {
                    continue;
                }

                if (method.IsGenericMethodDefinition)
                {
                    var genericArguments = method.GetGenericArguments();
                    if (genericArguments.Length != 1)
                    {
                        continue;
                    }

                    try
                    {
                        method = method.MakeGenericMethod(adaptorType);
                    }
                    catch
                    {
                        continue;
                    }
                }

                var parameters = method.GetParameters();
                var callbackIndex = -1;
                for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (parameters[parameterIndex].ParameterType.IsInstanceOfType(callback))
                    {
                        callbackIndex = parameterIndex;
                        break;
                    }
                }

                if (callbackIndex < 0)
                {
                    continue;
                }

                var canSupply = true;
                for (var parameterIndex = 0; parameterIndex < parameters.Length; parameterIndex++)
                {
                    if (parameterIndex == callbackIndex || parameters[parameterIndex].ParameterType.IsEnum)
                    {
                        continue;
                    }

                    if (!parameters[parameterIndex].IsOptional && parameters[parameterIndex].ParameterType != typeof(int))
                    {
                        canSupply = false;
                        break;
                    }
                }

                if (canSupply)
                {
                    return method;
                }
            }

            return null;
        }

        static object CreateCallback(DiscoveryCallback receiver, Type adaptorType)
        {
            var callbackType = typeof(Action<>).MakeGenericType(adaptorType);
            var method = typeof(DiscoveryCallback).GetMethod("Receive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (method == null)
            {
                throw new MissingMethodException(typeof(DiscoveryCallback).FullName, "Receive");
            }

            var generic = method.MakeGenericMethod(adaptorType);
            return Delegate.CreateDelegate(callbackType, receiver, generic);
        }

        static object CreateApi(Type apiType)
        {
            return typeof(ScriptableObject).IsAssignableFrom(apiType)
                ? ScriptableObject.CreateInstance(apiType)
                : Activator.CreateInstance(apiType);
        }

        static Snapshot BuildSnapshot(string mode, object root)
        {
            var result = new TraversalResult(mode);
            if (root == null)
            {
                result.Fail("null_root");
                return result.ToSnapshot();
            }

            VisitNode(result, root, string.Empty, string.Empty, false, false, 0, true);
            if (!result.Complete)
            {
                return result.ToSnapshot();
            }

            result.Candidates.Sort(TestCandidate.Compare);
            return result.ToSnapshot();
        }

        static void VisitNode(TraversalResult result, object adaptor, string inheritedAssembly, string inheritedFixture, bool inheritedExplicit, bool inheritedBlocked, int depth, bool root)
        {
            if (!result.Complete)
            {
                return;
            }

            if (adaptor == null)
            {
                result.Fail("null_adaptor");
                return;
            }

            if (depth > MaxDepth)
            {
                result.Fail("max_depth");
                return;
            }

            if (!result.Visit())
            {
                return;
            }

            if (!result.Seen.Add(adaptor))
            {
                result.Fail("adaptor_cycle_or_duplicate");
                return;
            }

            var isTestAssembly = ReadBoolean(adaptor, result, "IsTestAssembly", false, out _);
            var assembly = inheritedAssembly;
            if (isTestAssembly)
            {
                assembly = ReadAssemblyName(adaptor, result);
                if (!result.Complete)
                {
                    return;
                }
            }
            else if (string.IsNullOrEmpty(assembly))
            {
                assembly = ReadString(adaptor, result, "AssemblyName", "assemblyName", "Assembly");
                if (!result.Complete)
                {
                    return;
                }
            }

            var children = ReadChildren(adaptor, result);
            if (!result.Complete)
            {
                return;
            }

            var hasChildren = children.Count > 0;
            var hasChildrenValue = ReadBoolean(adaptor, result, "HasChildren", hasChildren, out var hasChildrenMember);
            if (hasChildrenMember && hasChildrenValue != hasChildren)
            {
                result.Fail("children_shape_incomplete");
                return;
            }

            var isSuite = ReadBoolean(adaptor, result, "IsSuite", hasChildren, out var isSuiteMember);
            if (!isSuiteMember)
            {
                isSuite = hasChildren;
            }
            if (!isSuite && hasChildren)
            {
                result.Fail("leaf_has_children");
                return;
            }

            var fullName = ReadString(adaptor, result, "FullName", "fullName");
            var uniqueName = ReadString(adaptor, result, "UniqueName", "uniqueName");
            var name = ReadString(adaptor, result, "Name", "name");
            var runState = ReadString(adaptor, result, "RunState", "runState");
            var explicitRunnable = ReadBoolean(adaptor, result, "IsRunnable", false, out var hasRunnableMember);
            var stateExplicit = IsExplicitState(runState);
            var stateRunnable = IsRunnableState(runState);
            var stateBlocked = !string.IsNullOrEmpty(runState) && !stateExplicit && !stateRunnable;
            var effectivelyBlocked = inheritedBlocked || stateBlocked;
            var effectivelyExplicit = !effectivelyBlocked && (inheritedExplicit || stateExplicit);
            var runnable = !effectivelyBlocked && !effectivelyExplicit
                && (hasRunnableMember ? explicitRunnable && (string.IsNullOrEmpty(runState) || stateRunnable) : stateRunnable);
            var exactlySelectable = runnable || effectivelyExplicit;
            var typeName = ReadNestedName(adaptor, result, "TypeInfo", "typeInfo");
            var methodName = ReadNestedName(adaptor, result, "Method", "method");
            var identity = FirstNonEmpty(uniqueName, fullName);

            var fixtureKey = inheritedFixture;
            var includeCandidate = !root && !isTestAssembly;
            var isFixture = includeCandidate
                && isSuite
                && !string.IsNullOrEmpty(typeName)
                && string.IsNullOrEmpty(methodName);
            if (isFixture)
            {
                fixtureKey = identity;
            }

            TestCandidate nodeCandidate = null;
            if (includeCandidate)
            {
                if (string.IsNullOrEmpty(identity) && (isSuite || exactlySelectable))
                {
                    result.Fail("test_name_missing");
                    return;
                }

                if (exactlySelectable && string.IsNullOrEmpty(assembly))
                {
                    result.Fail("test_assembly_missing");
                    return;
                }

                var candidateFixtureKey = string.IsNullOrEmpty(fixtureKey) && !isSuite ? identity : fixtureKey;
                var suiteName = isSuite ? identity : candidateFixtureKey;
                nodeCandidate = new TestCandidate(
                    assembly,
                    result.Mode,
                    fullName,
                    uniqueName,
                    suiteName,
                    typeName,
                    methodName,
                    runState,
                    runnable,
                    effectivelyExplicit,
                    isSuite,
                    candidateFixtureKey,
                    isFixture,
                    name);
                result.Add(nodeCandidate);
            }

            // Count only this structural subtree and the selected assembly, not
            // similarly named siblings or leaves under another assembly filter.
            // Cumulative per-assembly counters keep the bounded walk single-pass.
            var selectableLeavesBeforeChildren = result.ExactlySelectableLeaves(assembly);
            for (var index = 0; index < children.Count; index++)
            {
                VisitNode(result, children[index], assembly, fixtureKey, effectivelyExplicit, effectivelyBlocked, depth + 1, false);
                if (!result.Complete)
                {
                    return;
                }
            }
            if (nodeCandidate != null && nodeCandidate.IsSuite)
            {
                nodeCandidate.ExactlySelectableLeafDescendantCount = result.ExactlySelectableLeaves(assembly) - selectableLeavesBeforeChildren;
            }
        }

        static List<object> ReadChildren(object adaptor, TraversalResult result)
        {
            var children = new List<object>();
            if (!TryReadMember(adaptor, "Children", out var value, out var memberFound))
            {
                if (!memberFound)
                {
                    result.Fail("children_missing");
                    return children;
                }

                result.Fail("children_unreadable");
                return children;
            }

            if (value == null || value is string)
            {
                return children;
            }

            if (!(value is IEnumerable enumerable))
            {
                result.Fail("children_not_enumerable");
                return children;
            }

            try
            {
                foreach (var child in enumerable)
                {
                    if (children.Count >= MaxChildrenPerNode)
                    {
                        result.Fail("max_children");
                        return children;
                    }

                    children.Add(child);
                }
            }
            catch (Exception exception)
            {
                result.Fail("children_enumeration_failed:" + Bounded(Unwrap(exception).GetType().Name, 120));
            }

            return children;
        }

        static string ReadAssemblyName(object adaptor, TraversalResult result)
        {
            var assembly = ReadString(adaptor, result, "AssemblyName", "assemblyName", "Assembly");
            if (!string.IsNullOrEmpty(assembly))
            {
                return NormalizeAssemblyName(assembly);
            }

            return NormalizeAssemblyName(ReadString(adaptor, result, "Name", "FullName", "fullName"));
        }

        static string NormalizeAssemblyName(string value)
        {
            return !string.IsNullOrEmpty(value) && value.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
                ? value.Substring(0, value.Length - 4)
                : value;
        }

        static string ReadNestedName(object adaptor, TraversalResult result, params string[] memberNames)
        {
            if (!TryReadAnyMember(adaptor, memberNames, out var value, out var memberFound))
            {
                if (memberFound)
                {
                    result.Fail("member_unreadable");
                }

                return string.Empty;
            }

            if (value == null)
            {
                return string.Empty;
            }

            if (value is string stringValue)
            {
                return ValidateString(stringValue, result);
            }

            return ReadString(value, result, "FullName", "Name", "fullName", "name", "MethodName", "TypeName");
        }

        static string ReadString(object instance, TraversalResult result, params string[] names)
        {
            if (!TryReadAnyMember(instance, names, out var value, out var memberFound))
            {
                if (memberFound)
                {
                    result.Fail("member_unreadable");
                }

                return string.Empty;
            }

            if (value == null)
            {
                return string.Empty;
            }

            return ValidateString(value.ToString(), result);
        }

        static string ValidateString(string value, TraversalResult result)
        {
            if (value == null)
            {
                return string.Empty;
            }

            if (value.Length > MaxStringChars)
            {
                result.Fail("string_too_long");
                return string.Empty;
            }

            return value;
        }

        static bool ReadBoolean(object instance, TraversalResult result, string name, bool fallback, out bool memberFound)
        {
            memberFound = false;
            if (!TryReadMember(instance, name, out var value, out memberFound) || value == null)
            {
                if (ReferenceEquals(value, s_unreadableMember))
                {
                    result.Fail("member_unreadable");
                }

                return fallback;
            }

            if (value is bool boolean)
            {
                return boolean;
            }

            return bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
        }

        static bool TryReadAnyMember(object instance, string[] names, out object value, out bool memberFound)
        {
            value = null;
            memberFound = false;
            for (var index = 0; index < names.Length; index++)
            {
                if (TryReadMember(instance, names[index], out value, out memberFound))
                {
                    return true;
                }

                if (memberFound)
                {
                    return false;
                }
            }

            return false;
        }

        static bool TryReadMember(object instance, string name, out object value, out bool memberFound)
        {
            value = null;
            memberFound = false;
            if (instance == null || string.IsNullOrEmpty(name))
            {
                return false;
            }

            var type = instance.GetType();
            try
            {
                var property = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (property != null && property.GetIndexParameters().Length == 0)
                {
                    memberFound = true;
                    value = property.GetValue(instance, null);
                    return true;
                }

                var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.IgnoreCase);
                if (field != null)
                {
                    memberFound = true;
                    value = field.GetValue(instance);
                    return true;
                }
            }
            catch
            {
                memberFound = true;
                value = s_unreadableMember;
                return false;
            }

            return false;
        }

        static bool IsRunnableState(string state)
        {
            return string.Equals(state, "Runnable", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "run", StringComparison.OrdinalIgnoreCase)
                || string.Equals(state, "runnable", StringComparison.OrdinalIgnoreCase);
        }

        static bool IsExactlySelectable(TestCandidate candidate)
        {
            return candidate != null && (candidate.Runnable || candidate.ExplicitlySelectable);
        }

        static bool IsExplicitState(string state)
        {
            return string.Equals(state, "Explicit", StringComparison.OrdinalIgnoreCase);
        }

        static string NormalizeMode(string mode)
        {
            return (mode ?? string.Empty).Trim().ToLowerInvariant();
        }

        static bool IsKnownMode(string mode)
        {
            return mode == EditMode || mode == PlayMode;
        }

        static string FirstNonEmpty(params string[] values)
        {
            for (var index = 0; index < values.Length; index++)
            {
                if (!string.IsNullOrEmpty(values[index]))
                {
                    return values[index];
                }
            }

            return string.Empty;
        }

        static void AddUnique(List<string> values, string value, int max)
        {
            if (string.IsNullOrEmpty(value) || values.Count >= max || values.Contains(value))
            {
                return;
            }

            values.Add(value);
        }

        static int Clamp(int value, int min, int max)
        {
            if (value < min)
            {
                return min;
            }

            return value > max ? max : value;
        }

        static Type FindType(string fullName)
        {
            var assemblies = EditorAssemblyDiscovery.LoadedAssemblies();
            for (var index = 0; index < assemblies.Length; index++)
            {
                try
                {
                    var type = assemblies[index].GetType(fullName, false);
                    if (type != null)
                    {
                        return type;
                    }
                }
                catch
                {
                    // A stale or partially loaded assembly is not authoritative evidence.
                }
            }

            return null;
        }

        static Exception Unwrap(Exception exception)
        {
            while (exception is TargetInvocationException invocation && invocation.InnerException != null)
            {
                exception = invocation.InnerException;
            }

            return exception;
        }

        static string Bounded(string value, int maxChars)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            return value.Length <= maxChars ? value : value.Substring(0, maxChars);
        }

        sealed class SnapshotState
        {
            public SnapshotState(string mode)
            {
                Mode = mode;
                StartedAtUtc = DateTime.UtcNow;
                s_nextDiscoveryGeneration = s_nextDiscoveryGeneration == int.MaxValue ? 1 : s_nextDiscoveryGeneration + 1;
                DiscoveryGeneration = s_nextDiscoveryGeneration;
            }

            public string Mode { get; }
            public DateTime StartedAtUtc { get; }
            public DateTime CompletedAtUtc { get; private set; } = DateTime.MinValue;

            public void SetCompletedAtUtcForTests(DateTime completedAtUtc)
            {
                lock (s_gate)
                {
                    if (Status == StatusReady) CompletedAtUtc = completedAtUtc;
                }
            }
            public int DiscoveryGeneration { get; }
            public string Status { get; private set; } = StatusPending;
            public string FailureReason { get; private set; } = "retrieve_test_list_pending";
            public object RetainedApi { get; set; }
            public object RetainedCallback { get; set; }
            public object RetainedReceiver { get; set; }
            public bool CompletionClaimed { get; set; }
            public List<TestCandidate> Candidates { get; private set; } = new List<TestCandidate>();
            public int RunnableLeafCount { get; private set; }

            public bool CanRetryTimedOutRequest
            {
                get { return Status == StatusUnavailable && FailureReason == "retrieve_test_list_timeout"; }
            }

            public bool IsPendingExpired(DateTime nowUtc)
            {
                return Status == StatusPending && nowUtc - StartedAtUtc >= TimeSpan.FromSeconds(PendingTimeoutSeconds);
            }

            public void Complete(object root)
            {
                lock (s_gate)
                {
                    if (CompletionClaimed || Status != StatusPending)
                    {
                        return;
                    }

                    CompletionClaimed = true;
                }

                Snapshot snapshot;
                try
                {
                    snapshot = BuildSnapshot(Mode, root);
                }
                catch (Exception exception)
                {
                    lock (s_gate)
                    {
                        if (Status == StatusPending)
                        {
                            Status = StatusIncomplete;
                            FailureReason = Bounded("snapshot_build_failed:" + Unwrap(exception).GetType().Name, MaxStringChars);
                        }

                        RetainedApi = null;
                        RetainedCallback = null;
                        RetainedReceiver = null;
                    }

                    return;
                }

                lock (s_gate)
                {
                    if (Status != StatusPending)
                    {
                        RetainedApi = null;
                        RetainedCallback = null;
                        RetainedReceiver = null;
                        return;
                    }

                    if (snapshot.Status == StatusReady)
                    {
                        Candidates = new List<TestCandidate>(snapshot.Candidates);
                        RunnableLeafCount = CountRunnableLeaves(Candidates);
                        Status = StatusReady;
                        CompletedAtUtc = DateTime.UtcNow;
                        FailureReason = string.Empty;
                    }
                    else
                    {
                        Status = StatusIncomplete;
                        FailureReason = snapshot.FailureReason;
                    }

                    RetainedApi = null;
                    RetainedCallback = null;
                    RetainedReceiver = null;
                }
            }

            public void Fail(string reason)
            {
                lock (s_gate)
                {
                    if (Status != StatusPending)
                    {
                        return;
                    }

                    CompletionClaimed = true;
                    Status = StatusUnavailable;
                    FailureReason = Bounded(string.IsNullOrEmpty(reason) ? "test_runner_discovery_unavailable" : reason, MaxStringChars);
                    RetainedApi = null;
                    RetainedCallback = null;
                    RetainedReceiver = null;
                }
            }

            public Snapshot CurrentSnapshot()
            {
                lock (s_gate)
                {
                    return new Snapshot(Mode, Status, FailureReason, new List<TestCandidate>(Candidates), DiscoveryGeneration);
                }
            }
        }

        sealed class DiscoveryCallback
        {
            readonly SnapshotState m_state;

            public DiscoveryCallback(SnapshotState state)
            {
                m_state = state;
            }

            void Receive<TAdaptor>(TAdaptor root)
            {
                m_state.Complete(root);
            }
        }

        sealed class TraversalResult
        {
            readonly Dictionary<string, int> m_exactlySelectableLeaves = new Dictionary<string, int>(StringComparer.Ordinal);

            public TraversalResult(string mode)
            {
                Mode = mode;
            }

            public string Mode { get; }
            public bool Complete { get; private set; } = true;
            public string FailureReason { get; private set; } = string.Empty;
            public List<TestCandidate> Candidates { get; } = new List<TestCandidate>();
            public HashSet<object> Seen { get; } = new HashSet<object>(ReferenceComparer.Instance);
            public int VisitedNodes { get; private set; }

            public bool Visit()
            {
                if (VisitedNodes >= MaxNodes)
                {
                    Fail("max_nodes");
                    return false;
                }

                VisitedNodes++;
                return true;
            }

            public void Add(TestCandidate candidate)
            {
                if (!Complete)
                {
                    return;
                }

                if (Candidates.Count >= MaxNodes)
                {
                    Fail("max_nodes");
                    return;
                }

                Candidates.Add(candidate);
                if (!candidate.IsSuite && IsExactlySelectable(candidate) && !string.IsNullOrEmpty(candidate.FullName))
                {
                    m_exactlySelectableLeaves[candidate.AssemblyName] = ExactlySelectableLeaves(candidate.AssemblyName) + 1;
                }
            }

            public int ExactlySelectableLeaves(string assemblyName)
            {
                return m_exactlySelectableLeaves.TryGetValue(assemblyName, out var count) ? count : 0;
            }

            public void Fail(string reason)
            {
                if (!Complete)
                {
                    return;
                }

                Complete = false;
                FailureReason = Bounded(reason, MaxStringChars);
                Candidates.Clear();
                m_exactlySelectableLeaves.Clear();
            }

            public Snapshot ToSnapshot()
            {
                return new Snapshot(Mode, Complete ? StatusReady : StatusIncomplete, FailureReason, new List<TestCandidate>(Candidates), 0);
            }
        }

        sealed class ReferenceComparer : IEqualityComparer<object>
        {
            public static readonly ReferenceComparer Instance = new ReferenceComparer();

            public new bool Equals(object left, object right)
            {
                return ReferenceEquals(left, right);
            }

            public int GetHashCode(object value)
            {
                return value == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
            }
        }

        public sealed class Snapshot
        {
            internal Snapshot(string mode, string status, string failureReason, List<TestCandidate> candidates, int discoveryGeneration)
            {
                Mode = mode;
                Status = status;
                FailureReason = failureReason ?? string.Empty;
                Candidates = candidates ?? new List<TestCandidate>();
                DiscoveryGeneration = Math.Max(0, discoveryGeneration);
            }

            public string Mode { get; }
            public string Status { get; }
            public string FailureReason { get; }
            public int DiscoveryGeneration { get; }
            public bool Authoritative { get { return Status == StatusReady; } }
            public IReadOnlyList<TestCandidate> Candidates { get; }
        }

        public sealed class CatalogQueryResult
        {
            CatalogQueryResult(string mode, string query, string assemblyName, int cursor, int maxResults, string status, string coverageStatus, int totalAvailable, List<TestCandidate> candidates, int? nextCursor, string reason, int discoveryGeneration, int runnableLeafCount = 0)
            {
                Mode = mode ?? string.Empty;
                Query = query ?? string.Empty;
                AssemblyName = assemblyName ?? string.Empty;
                Cursor = Math.Max(0, cursor);
                MaxResults = Clamp(maxResults, 1, MaxPageSize);
                Status = status;
                ResolutionStatus = status == StatusReady ? "resolved" : status;
                CoverageStatus = coverageStatus;
                TotalAvailable = Math.Max(0, totalAvailable);
                RunnableLeafCount = Math.Max(0, runnableLeafCount);
                Candidates = candidates ?? new List<TestCandidate>();
                Returned = Candidates.Count;
                NextCursor = nextCursor;
                FailureReason = reason ?? string.Empty;
                DiscoveryGeneration = Math.Max(0, discoveryGeneration);
            }

            public string Mode { get; }
            public string Query { get; }
            public string AssemblyName { get; }
            public int Cursor { get; }
            public int MaxResults { get; }
            public string Status { get; }
            public string ResolutionStatus { get; }
            public string CoverageStatus { get; }
            public int TotalAvailable { get; }
            public int RunnableLeafCount { get; }
            public int Returned { get; }
            public int? NextCursor { get; }
            public bool Truncated { get { return NextCursor.HasValue; } }
            public string FailureReason { get; }
            public int DiscoveryGeneration { get; }
            public List<TestCandidate> Candidates { get; }

            internal static CatalogQueryResult Ready(string mode, string query, string assemblyName, int cursor, int maxResults, int totalAvailable, List<TestCandidate> candidates, int? nextCursor, int discoveryGeneration = 0, int runnableLeafCount = 0)
            {
                return new CatalogQueryResult(mode, query, assemblyName, cursor, maxResults, StatusReady, "complete", totalAvailable, candidates, nextCursor, string.Empty, discoveryGeneration, runnableLeafCount);
            }

            internal static CatalogQueryResult Pending(string mode, string query, string assemblyName, int cursor, int maxResults, string reason, int discoveryGeneration = 0)
            {
                return new CatalogQueryResult(mode, query, assemblyName, cursor, maxResults, StatusPending, "pending", 0, new List<TestCandidate>(), null, reason, discoveryGeneration);
            }

            internal static CatalogQueryResult Unavailable(string mode, string reason, string status = StatusUnavailable, string query = "", string assemblyName = "", int cursor = 0, int maxResults = 1, int discoveryGeneration = 0)
            {
                var normalizedStatus = status == StatusIncomplete ? StatusIncomplete : StatusUnavailable;
                return new CatalogQueryResult(mode, query, assemblyName, cursor, maxResults, normalizedStatus, normalizedStatus, 0, new List<TestCandidate>(), null, reason, discoveryGeneration);
            }
        }

        public sealed class FilterResolution
        {
            FilterResolution(string mode, string filter, string assemblyName, string status, string reason, List<TestCandidate> candidates, List<string> testNames, string resolvedAssembly, int discoveryGeneration)
            {
                Mode = mode ?? string.Empty;
                Filter = filter ?? string.Empty;
                AssemblyName = assemblyName ?? string.Empty;
                Status = status;
                Reason = reason ?? string.Empty;
                Candidates = candidates ?? new List<TestCandidate>();
                TestNames = testNames ?? new List<string>();
                ResolvedAssembly = resolvedAssembly ?? string.Empty;
                DiscoveryGeneration = Math.Max(0, discoveryGeneration);
            }

            public string Mode { get; }
            public string Filter { get; }
            public string AssemblyName { get; }
            public string Status { get; }
            public string Reason { get; }
            public bool IsResolved { get { return Status == "resolved"; } }
            public List<TestCandidate> Candidates { get; }
            public List<string> TestNames { get; }
            public string ResolvedAssembly { get; }
            public int DiscoveryGeneration { get; }

            internal static FilterResolution Pending(string mode, string filter, string assemblyName, string reason, int discoveryGeneration = 0)
            {
                return new FilterResolution(mode, filter, assemblyName, StatusPending, reason, null, null, string.Empty, discoveryGeneration);
            }

            internal static FilterResolution Resolved(string mode, string filter, string assemblyName, List<string> testNames, List<TestCandidate> candidates, string resolvedAssembly, string reason, int discoveryGeneration = 0)
            {
                return new FilterResolution(mode, filter, assemblyName, "resolved", reason, candidates, testNames, resolvedAssembly, discoveryGeneration);
            }

            internal static FilterResolution Unresolved(string mode, string filter, string assemblyName, string reason, List<TestCandidate> candidates, int discoveryGeneration = 0)
            {
                return new FilterResolution(mode, filter, assemblyName, "unresolved", reason, candidates, null, string.Empty, discoveryGeneration);
            }

            internal static FilterResolution Unavailable(string mode, string filter, string assemblyName, string status, string reason, int discoveryGeneration = 0)
            {
                var normalizedStatus = status == StatusIncomplete ? StatusIncomplete : StatusUnavailable;
                return new FilterResolution(mode, filter, assemblyName, normalizedStatus, reason, null, null, string.Empty, discoveryGeneration);
            }

            internal static FilterResolution Ambiguous(string mode, string filter, string assemblyName, string reason, List<TestCandidate> candidates, int discoveryGeneration = 0)
            {
                return new FilterResolution(mode, filter, assemblyName, "ambiguous", reason, candidates, null, string.Empty, discoveryGeneration);
            }
        }

        public sealed class TestCandidate
        {
            internal TestCandidate(string assemblyName, string mode, string fullName, string uniqueName, string suiteName, string typeName, string methodName, string runState, bool runnable, bool explicitlySelectable, bool isSuite, string fixtureKey, bool isFixture, string nodeName)
            {
                AssemblyName = assemblyName ?? string.Empty;
                Mode = mode ?? string.Empty;
                FullName = fullName ?? string.Empty;
                UniqueName = uniqueName ?? string.Empty;
                SuiteName = suiteName ?? string.Empty;
                TypeName = typeName ?? string.Empty;
                MethodName = methodName ?? string.Empty;
                RunState = runState ?? string.Empty;
                Runnable = runnable;
                ExplicitlySelectable = explicitlySelectable;
                IsSuite = isSuite;
                FixtureKey = fixtureKey ?? string.Empty;
                IsFixture = isFixture;
                NodeName = nodeName ?? string.Empty;
            }

            public string AssemblyName { get; }
            public string Mode { get; }
            public string FullName { get; }
            public string UniqueName { get; }
            public string SuiteName { get; }
            public string TypeName { get; }
            public string MethodName { get; }
            public string RunState { get; }
            public bool Runnable { get; }
            internal bool ExplicitlySelectable { get; }
            public bool IsSuite { get; }
            internal int ExactlySelectableLeafDescendantCount { get; set; }
            public string FixtureKey { get; }
            public bool IsFixture { get; }
            public string NodeName { get; }
            public string RecommendedFilter { get { return string.IsNullOrEmpty(FullName) ? UniqueName : FullName; } }

            public void AppendJson(StringBuilder builder)
            {
                builder.Append('{');
                builder.Append("\"assembly\":").Append(JsonUtil.String(AssemblyName)).Append(',');
                builder.Append("\"suite\":").Append(JsonUtil.String(SuiteName)).Append(',');
                builder.Append("\"type\":").Append(JsonUtil.String(TypeName)).Append(',');
                builder.Append("\"method\":").Append(JsonUtil.String(MethodName)).Append(',');
                builder.Append("\"full_name\":").Append(JsonUtil.String(FullName)).Append(',');
                builder.Append("\"unique_name\":").Append(JsonUtil.String(UniqueName)).Append(',');
                builder.Append("\"mode\":").Append(JsonUtil.String(Mode)).Append(',');
                builder.Append("\"kind\":").Append(JsonUtil.String(IsSuite ? "suite" : "leaf")).Append(',');
                builder.Append("\"is_suite\":").Append(JsonUtil.Bool(IsSuite)).Append(',');
                builder.Append("\"run_state\":").Append(JsonUtil.String(RunState)).Append(',');
                builder.Append("\"runnable\":").Append(JsonUtil.Bool(Runnable)).Append(',');
                builder.Append("\"runnable_filters\":{");
                builder.Append("\"recommended\":").Append(JsonUtil.String(RecommendedFilter)).Append(',');
                builder.Append("\"unity_test_names\":[");
                var filterValues = new List<string>();
                AddUnique(filterValues, RecommendedFilter, 3);
                AddUnique(filterValues, FullName, 3);
                AddUnique(filterValues, UniqueName, 3);
                for (var index = 0; index < filterValues.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    builder.Append(JsonUtil.String(filterValues[index]));
                }
                builder.Append(']');
                builder.Append(",\"full_name\":").Append(JsonUtil.String(FullName));
                builder.Append(",\"unique_name\":").Append(JsonUtil.String(UniqueName));
                builder.Append(",\"fixture\":").Append(JsonUtil.String(FixtureKey));
                builder.Append(",\"method\":").Append(JsonUtil.String(MethodName));
                builder.Append(",\"assembly\":").Append(JsonUtil.String(AssemblyName));
                builder.Append('}');
                builder.Append('}');
            }

            public bool HasExactName(string value)
            {
                return string.Equals(FullName, value, StringComparison.Ordinal)
                    || string.Equals(UniqueName, value, StringComparison.Ordinal)
                    || (IsSuite && string.Equals(ShortName(TypeName), value, StringComparison.Ordinal));
            }

            public bool MatchesQuery(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return true;
                }

                var lower = value.ToLowerInvariant();
                return Contains(AssemblyName, lower)
                    || Contains(SuiteName, lower)
                    || Contains(TypeName, lower)
                    || Contains(MethodName, lower)
                    || Contains(FullName, lower)
                    || Contains(UniqueName, lower)
                    || Contains(NodeName, lower);
            }

            public static int Compare(TestCandidate left, TestCandidate right)
            {
                var compare = string.Compare(left == null ? string.Empty : left.Mode, right == null ? string.Empty : right.Mode, StringComparison.Ordinal);
                if (compare != 0) return compare;
                compare = string.Compare(left == null ? string.Empty : left.AssemblyName, right == null ? string.Empty : right.AssemblyName, StringComparison.Ordinal);
                if (compare != 0) return compare;
                compare = string.Compare(left == null ? string.Empty : left.FullName, right == null ? string.Empty : right.FullName, StringComparison.Ordinal);
                if (compare != 0) return compare;
                compare = string.Compare(left == null ? string.Empty : left.UniqueName, right == null ? string.Empty : right.UniqueName, StringComparison.Ordinal);
                if (compare != 0) return compare;
                compare = (left != null && left.IsSuite ? 0 : 1).CompareTo(right != null && right.IsSuite ? 0 : 1);
                if (compare != 0) return compare;
                compare = string.Compare(left == null ? string.Empty : left.FixtureKey, right == null ? string.Empty : right.FixtureKey, StringComparison.Ordinal);
                if (compare != 0) return compare;
                compare = string.Compare(left == null ? string.Empty : left.TypeName, right == null ? string.Empty : right.TypeName, StringComparison.Ordinal);
                if (compare != 0) return compare;
                compare = string.Compare(left == null ? string.Empty : left.MethodName, right == null ? string.Empty : right.MethodName, StringComparison.Ordinal);
                if (compare != 0) return compare;
                compare = string.Compare(left == null ? string.Empty : left.NodeName, right == null ? string.Empty : right.NodeName, StringComparison.Ordinal);
                if (compare != 0) return compare;
                return string.Compare(left == null ? string.Empty : left.RunState, right == null ? string.Empty : right.RunState, StringComparison.Ordinal);
            }

            static bool Contains(string value, string lowerNeedle)
            {
                return !string.IsNullOrEmpty(value)
                    && !string.IsNullOrEmpty(lowerNeedle)
                    && value.ToLowerInvariant().IndexOf(lowerNeedle, StringComparison.Ordinal) >= 0;
            }

            static string ShortName(string value)
            {
                if (string.IsNullOrEmpty(value))
                {
                    return string.Empty;
                }

                var separator = value.LastIndexOf('.');
                return separator < 0 || separator == value.Length - 1 ? value : value.Substring(separator + 1);
            }
        }
    }
}
