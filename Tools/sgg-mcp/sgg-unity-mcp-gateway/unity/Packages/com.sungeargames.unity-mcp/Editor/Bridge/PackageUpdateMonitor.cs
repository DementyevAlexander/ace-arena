using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SGG.UnityMCP.Editor.Setup;
using UnityEditor;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

namespace SGG.UnityMCP.Editor.Bridge
{
    public sealed class PackageUpdateMonitor : IDisposable
    {
        const double FallbackScanIntervalSeconds = 120.0;
        const double FailureRetrySeconds = 30.0;
        const double ExplicitRefreshConvergenceRetrySeconds = 1.0;
        const int MaxExplicitRefreshConvergenceRetries = 120;
        const long EventDebounceTicks = TimeSpan.TicksPerMillisecond * 250L;
        const string SessionSnapshotKey = "SGG.UnityMCP.PackageUpdateMonitor.current";
        const double UnchangedTraceSampleIntervalSeconds = 60.0;
        const long MaxProjectPackageFileBytes = 4L * 1024L * 1024L;
        const long MaxProjectPackageFingerprintMilliseconds = 500L;

        static readonly HashSet<string> WatchedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".asmdef",
            ".cs",
            ".json",
            ".meta",
            ".mjs",
            ".uss",
            ".uxml",
            ".xlf",
        };

        readonly string m_packageRoot;
        string m_currentFingerprint = string.Empty;
        string m_currentProjectPackagesFingerprint = string.Empty;
        string m_pendingFingerprint = string.Empty;
        string m_status = "current";
        string m_message = "Package/runtime files are current.";
        string m_fingerprintFailureClass = string.Empty;
        string m_fingerprintFailureReason = string.Empty;
        string m_fingerprintFailureFile = string.Empty;
        int m_refreshCount;
        string m_lastRefreshUtc = string.Empty;
        double m_nextFallbackScanTime;
        double m_retryNotBeforeTime;
        double m_nextUnchangedTraceSampleTime;
        DateTime m_nextWarningUtc = DateTime.MinValue;
        readonly object m_watcherGate = new object();
        readonly List<FileSystemWatcher> m_watchers = new List<FileSystemWatcher>();
        string m_watcherSignature = string.Empty;
        Task<FingerprintSnapshot> m_scanTask;
        int m_scanGeneration;
        bool m_scanWasFallback;
        bool m_refreshPending;
        int m_explicitRefreshConvergenceRetries;
        int m_dirtyGeneration = 1;
        int m_scannedGeneration;
        int m_publishedDirtyGeneration;
        int m_projectPackagesDirty;
        long m_scanNotBeforeUtcTicks;
        int m_disposed;
        int m_clearPersistedSnapshot;
        PersistedSnapshot m_restoredSnapshot;
        LocalPackageCodeInventorySnapshot m_lastCurrentInventory;
        LocalPackageCodeInventoryInput[] m_cachedLocalInputs;
        string[] m_cachedAssetPaths;
        string[] m_cachedCompilationSourcePaths;
        string[] m_cachedExpectedCompilationSourcePaths;
        LocalPackageMetadataInput[] m_cachedMetadataInputs;
        string m_cachedInputFailure = string.Empty;

        public PackageUpdateMonitor(string packageRoot)
        {
            m_packageRoot = string.IsNullOrEmpty(packageRoot) ? UnityMcpPackagePaths.PackageResolvedPath : packageRoot;
            m_status = "unavailable";
            m_message = "Package/runtime monitor is waiting for its initial background validation.";
            RestoreForPlayMode();
            UnityEditor.PackageManager.Events.registeredPackages += OnRegisteredPackages;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            InitializeWatcherRoots();
        }

        public void Update(BridgeRuntimeState state, double now)
        {
            if (state == null || Volatile.Read(ref m_disposed) != 0)
            {
                return;
            }

            if (Interlocked.Exchange(ref m_clearPersistedSnapshot, 0) != 0) SessionState.EraseString(SessionSnapshotKey);
            ApplyRestoredSnapshot(state);
            ApplyCompletedScan(state, now);
            var generation = Volatile.Read(ref m_dirtyGeneration);
            var dirty = generation != m_scannedGeneration;
            if (dirty && m_publishedDirtyGeneration != generation)
            {
                PublishPendingState(state, generation);
            }

            var isPlaying = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying;
            if (ShouldStartScan(dirty, m_scanTask != null, isPlaying, EditorApplication.isCompiling, EditorApplication.isUpdating, now, m_retryNotBeforeTime))
            {
                if (DateTime.UtcNow.Ticks < Interlocked.Read(ref m_scanNotBeforeUtcTicks)) return;
                StartBackgroundScan(state, generation, false, now);
                return;
            }

            if (ShouldStartFallbackScan(dirty, m_scanTask != null, isPlaying, EditorApplication.isCompiling, EditorApplication.isUpdating, now, m_nextFallbackScanTime))
            {
                StartBackgroundScan(state, generation, true, now);
            }
        }

        internal static bool ShouldStartScan(bool dirty, bool scanRunning, bool isPlaying, bool isCompiling, bool isUpdating, double now, double retryNotBeforeTime)
        {
            return dirty && !scanRunning && !isPlaying && !isCompiling && !isUpdating && now >= retryNotBeforeTime;
        }

        internal static bool ShouldStartFallbackScan(bool dirty, bool scanRunning, bool isPlaying, bool isCompiling, bool isUpdating, double now, double nextFallbackScanTime)
        {
            return !dirty && !scanRunning && !isPlaying && !isCompiling && !isUpdating && now >= nextFallbackScanTime;
        }

        public void MarkRefreshObserved(BridgeRuntimeState state, string reason)
        {
            if (state == null)
            {
                return;
            }

            BridgeReloadTrace.Log("package_update_monitor.manual_refresh.observed", reason ?? "explicit_refresh");
            m_refreshPending = true;
            m_explicitRefreshConvergenceRetries = 0;
            InvalidateFromMainThread(reason ?? "explicit_refresh", true, false);
            Interlocked.Exchange(ref m_scanNotBeforeUtcTicks, 0L);
            PublishPendingState(state, Volatile.Read(ref m_dirtyGeneration));
            Update(state, EditorApplication.timeSinceStartup);
        }

        public void Invalidate(string reason, bool packageResolutionChanged = false, bool debounce = true)
        {
            if (Volatile.Read(ref m_disposed) != 0) return;
            Interlocked.Exchange(ref m_clearPersistedSnapshot, 1);
            if (packageResolutionChanged) Interlocked.Exchange(ref m_projectPackagesDirty, 1);
            if (debounce) Interlocked.Exchange(ref m_scanNotBeforeUtcTicks, DateTime.UtcNow.Ticks + EventDebounceTicks);
            Interlocked.Increment(ref m_dirtyGeneration);
        }

        public void InvalidateFromMainThread(string reason, bool packageResolutionChanged = false, bool debounce = true)
        {
            SessionState.EraseString(SessionSnapshotKey);
            Interlocked.Exchange(ref m_clearPersistedSnapshot, 0);
            Invalidate(reason, packageResolutionChanged, debounce);
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref m_disposed, 1) != 0) return;
            UnityEditor.PackageManager.Events.registeredPackages -= OnRegisteredPackages;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            lock (m_watcherGate)
            {
                DisposeWatchers();
            }
        }

        void OnRegisteredPackages(UnityEditor.PackageManager.PackageRegistrationEventArgs args)
        {
            InvalidateFromMainThread("registered_packages_changed", true);
        }

        void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingEditMode
                && (Volatile.Read(ref m_clearPersistedSnapshot) != 0 || Volatile.Read(ref m_dirtyGeneration) != m_scannedGeneration || m_scanTask != null))
            {
                SessionState.EraseString(SessionSnapshotKey);
            }
        }

        void InitializeWatcherRoots()
        {
            try
            {
                var packages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                LocalPackageCodeInventoryInspector.TryCaptureRegisteredPackages(packages, out m_cachedLocalInputs, out m_cachedInputFailure);
                m_cachedMetadataInputs = LocalPackageMetadataInspector.CapturePackages(packages);
                RebuildWatchers(m_cachedLocalInputs);
            }
            catch
            {
                RebuildWatchers(new LocalPackageCodeInventoryInput[0]);
            }
        }

        void PublishPendingState(BridgeRuntimeState state, int generation)
        {
            m_publishedDirtyGeneration = generation;
            SessionState.EraseString(SessionSnapshotKey);
            var packageResolutionChanged = Volatile.Read(ref m_projectPackagesDirty) != 0;
            var status = packageResolutionChanged ? "stale_package_resolution" : "package_refresh_required";
            var message = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying
                ? "Package/runtime change was observed during Play Mode; background validation is deferred until Edit Mode."
                : "Package/runtime change was observed; background validation is pending.";
            SetStatus(state, status, message, true, m_currentFingerprint, m_pendingFingerprint, false, true);
        }

        void StartBackgroundScan(BridgeRuntimeState state, int generation, bool fallback, double now)
        {
            if (string.IsNullOrEmpty(m_packageRoot) || !Directory.Exists(m_packageRoot))
            {
                SetFingerprintFailure("package_runtime", "missing_file", "package_root");
                state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Unavailable("snapshot_unavailable"));
                SetStatus(state, "unavailable", FingerprintUnavailableMessage(m_refreshPending), true, m_currentFingerprint, m_pendingFingerprint, false, true);
                m_retryNotBeforeTime = now + FailureRetrySeconds;
                LogWarning();
                return;
            }

            try
            {
                if (!fallback || m_cachedLocalInputs == null || m_cachedAssetPaths == null || m_cachedCompilationSourcePaths == null || m_cachedExpectedCompilationSourcePaths == null || m_cachedMetadataInputs == null)
                {
                    var registeredPackages = UnityEditor.PackageManager.PackageInfo.GetAllRegisteredPackages();
                    LocalPackageCodeInventoryInspector.TryCaptureRegisteredPackages(registeredPackages, out m_cachedLocalInputs, out m_cachedInputFailure);
                    m_cachedAssetPaths = AssetDatabase.GetAllAssetPaths();
                    if (!LocalPackageCodeInventoryInspector.TryCaptureCompilationSourcePaths(m_cachedLocalInputs, m_cachedAssetPaths, out m_cachedCompilationSourcePaths, out m_cachedExpectedCompilationSourcePaths, out var compilationFailure))
                    {
                        m_cachedCompilationSourcePaths = new string[0];
                        m_cachedExpectedCompilationSourcePaths = new string[0];
                        m_cachedInputFailure = compilationFailure;
                    }
                    m_cachedMetadataInputs = LocalPackageMetadataInspector.CapturePackages(registeredPackages);
                    RebuildWatchers(m_cachedLocalInputs);
                }
                var request = new FingerprintRequest(m_packageRoot, ProjectPaths.ProjectRoot, m_cachedLocalInputs, m_cachedAssetPaths, m_cachedCompilationSourcePaths, m_cachedExpectedCompilationSourcePaths, m_cachedMetadataInputs, m_cachedInputFailure);
                m_scanGeneration = generation;
                m_scanWasFallback = fallback;
                m_nextFallbackScanTime = now + FallbackScanIntervalSeconds;
                m_scanTask = Task.Run(() => ComputeFingerprintSafely(request));
            }
            catch (Exception exception)
            {
                SetFingerprintFailure("package_runtime", "read_exception", "unknown");
                state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Unavailable("snapshot_unavailable"));
                SetStatus(state, "unavailable", FingerprintUnavailableMessage(m_refreshPending), true, m_currentFingerprint, m_pendingFingerprint, false, true);
                m_retryNotBeforeTime = now + FailureRetrySeconds;
                BridgeReloadTrace.LogError("package_update_monitor.update.error", exception);
                LogWarning();
            }
        }

        void ApplyCompletedScan(BridgeRuntimeState state, double now)
        {
            var task = m_scanTask;
            if (task == null || !task.IsCompleted) return;
            m_scanTask = null;
            FingerprintSnapshot snapshot;
            try
            {
                snapshot = task.GetAwaiter().GetResult();
            }
            catch (Exception exception)
            {
                snapshot = FingerprintSnapshot.Failure("package_runtime", "read_exception", "unknown");
                BridgeReloadTrace.LogError("package_update_monitor.update.error", exception);
            }

            var currentGeneration = Volatile.Read(ref m_dirtyGeneration);
            if (m_scanGeneration != currentGeneration)
            {
                m_retryNotBeforeTime = 0;
                return;
            }

            if (snapshot == null || string.IsNullOrEmpty(snapshot.CombinedFingerprint))
            {
                SetFingerprintFailure(snapshot == null ? "package_runtime" : snapshot.FailureClass, snapshot == null ? "read_exception" : snapshot.FailureReason, snapshot == null ? "unknown" : snapshot.FailureFile);
                state.SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot.Unavailable("snapshot_unavailable"));
                SetStatus(state, "unavailable", FingerprintUnavailableMessage(m_refreshPending), true, m_currentFingerprint, m_pendingFingerprint, false, true);
                m_retryNotBeforeTime = now + FailureRetrySeconds;
                LogWarning();
                return;
            }

            ClearFingerprintFailure();
            m_scannedGeneration = currentGeneration;
            m_publishedDirtyGeneration = currentGeneration;
            m_retryNotBeforeTime = 0;
            var total = snapshot.Duration;
            var pendingFingerprintChanged = !string.Equals(m_pendingFingerprint, snapshot.CombinedFingerprint, StringComparison.Ordinal);
            var fingerprintChanged = !string.IsNullOrEmpty(m_currentFingerprint)
                && !string.Equals(snapshot.CombinedFingerprint, m_currentFingerprint, StringComparison.Ordinal);
            if (pendingFingerprintChanged)
            {
                if (fingerprintChanged) BridgeReloadTrace.LogFingerprints("package_update_monitor.fingerprint.changed", m_currentFingerprint, snapshot.CombinedFingerprint);
                m_pendingFingerprint = snapshot.CombinedFingerprint;
            }

            if (!ApplyLocalInventoryStatus(state, snapshot, "package_update_monitor.update.end", total, true, now, pendingFingerprintChanged)
                || !ApplyLocalMetadataStatus(state, snapshot, "package_update_monitor.update.end", total, true, now, pendingFingerprintChanged))
            {
                var explicitRefreshInventoryConverging = ShouldRetryExplicitRefreshInventory(
                    m_refreshPending,
                    snapshot.LocalCodeInventory != null && snapshot.LocalCodeInventory.IsStale,
                    snapshot.LocalCodeInventory == null || snapshot.LocalCodeInventory.IsUnavailable,
                    m_explicitRefreshConvergenceRetries);
                var retryable = snapshot.LocalCodeInventory == null || snapshot.LocalCodeInventory.IsUnavailable
                    || snapshot.LocalMetadata == null || string.Equals(snapshot.LocalMetadata.Status, "unavailable", StringComparison.Ordinal);
                if (retryable || explicitRefreshInventoryConverging)
                {
                    if (explicitRefreshInventoryConverging) m_explicitRefreshConvergenceRetries++;
                    m_retryNotBeforeTime = now + (explicitRefreshInventoryConverging ? ExplicitRefreshConvergenceRetrySeconds : FailureRetrySeconds);
                    InvalidateFromMainThread(explicitRefreshInventoryConverging ? "explicit_refresh_inventory_converging" : "background_scan_unavailable", false, false);
                }
                return;
            }

            if (m_refreshPending)
            {
                ApplyCurrentSnapshot(snapshot);
                m_pendingFingerprint = string.Empty;
                m_refreshPending = false;
                m_explicitRefreshConvergenceRetries = 0;
                m_refreshCount++;
                m_lastRefreshUtc = DateTime.UtcNow.ToString("o");
                Interlocked.Exchange(ref m_projectPackagesDirty, 0);
                SetStatus(state, "current", "Package/runtime files and project package manifest/lock were validated after an explicit refresh.", false, m_currentFingerprint, string.Empty);
                PersistCurrentSnapshot();
                BridgeReloadTrace.Log("package_update_monitor.manual_refresh.end", total.ElapsedMilliseconds);
                return;
            }

            if (string.IsNullOrEmpty(m_currentFingerprint)) ApplyCurrentSnapshot(snapshot);
            if (string.Equals(snapshot.CombinedFingerprint, m_currentFingerprint, StringComparison.Ordinal))
            {
                m_pendingFingerprint = string.Empty;
                Interlocked.Exchange(ref m_projectPackagesDirty, 0);
                var traceUpdate = BeginNormalUpdateTrace(now, "current", string.Empty);
                SetStatus(state, "current", "Package/runtime files and project package manifest/lock are current.", false, m_currentFingerprint, string.Empty);
                PersistCurrentSnapshot();
                EndNormalUpdateTrace(traceUpdate, "current", total);
                return;
            }

            var projectPackagesChanged = !string.Equals(snapshot.ProjectPackagesFingerprint, m_currentProjectPackagesFingerprint, StringComparison.Ordinal);
            var status = projectPackagesChanged ? "stale_package_resolution" : "package_refresh_required";
            var message = projectPackagesChanged
                ? "Project Packages/manifest.json or Packages/packages-lock.json changed; package resolution must be refreshed before compile readiness can be trusted. Run compile.refresh_and_wait or validation.after_edit."
                : "Package/runtime files changed; run an explicit refresh command such as compile.refresh_and_wait or validation.after_edit.";
            var traceStaleUpdate = BeginNormalUpdateTrace(now, status, m_pendingFingerprint, pendingFingerprintChanged || m_scanWasFallback);
            SetStatus(state, status, message, true, m_currentFingerprint, m_pendingFingerprint);
            EndNormalUpdateTrace(traceStaleUpdate, status, total);
        }

        static FingerprintSnapshot ComputeFingerprintSafely(FingerprintRequest request)
        {
            var total = Stopwatch.StartNew();
            if (request == null || string.IsNullOrEmpty(request.PackageRoot) || !Directory.Exists(request.PackageRoot))
            {
                return FingerprintSnapshot.Failure("package_runtime", "missing_file", "package_root", total);
            }

            try
            {
                var packageRuntimeInput = new StringBuilder();
                var files = new List<string>();
                foreach (var file in Directory.GetFiles(request.PackageRoot, "*", SearchOption.AllDirectories))
                {
                    if (ShouldWatch(file)) files.Add(file);
                }
                files.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (var file in files)
                {
                    var info = new FileInfo(file);
                    var relative = file.Substring(request.PackageRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Replace('\\', '/');
                    packageRuntimeInput.Append(relative).Append('|').Append(info.Length).Append('|').Append(info.LastWriteTimeUtc.Ticks).Append('\n');
                }

                var projectPackagesInput = new StringBuilder();
                var budget = Stopwatch.StartNew();
                if (!TryAppendProjectPackageFile(request.ProjectRoot, projectPackagesInput, "Packages/manifest.json", budget, out var failureReason, out var failureFile)
                    || !TryAppendProjectPackageFile(request.ProjectRoot, projectPackagesInput, "Packages/packages-lock.json", budget, out failureReason, out failureFile))
                {
                    return FingerprintSnapshot.Failure("project_packages", failureReason, failureFile, total);
                }

                var inventory = string.IsNullOrEmpty(request.InputFailure)
                    ? LocalPackageCodeInventoryInspector.InspectCapturedInputs(request.LocalInputs, request.AssetPaths, request.CompilationSourcePaths, request.ExpectedCompilationSourcePaths)
                    : LocalPackageCodeInventorySnapshot.Unavailable(request.InputFailure);
                var metadata = LocalPackageMetadataInspector.InspectCapturedPackages(request.MetadataInputs);
                var packageRuntimeFingerprint = Sha256(packageRuntimeInput.ToString());
                var projectPackagesFingerprint = Sha256(projectPackagesInput.ToString());
                var combined = "package_runtime:" + packageRuntimeFingerprint
                    + "\nproject_packages:" + projectPackagesFingerprint
                    + "\nlocal_package_source_state:" + inventory.SourceStateFingerprint
                    + "\nlocal_package_asset_database:" + inventory.AssetDatabaseFingerprint
                    + "\nlocal_package_compiler_sources:" + inventory.CompilerSourceFingerprint + "\n";
                return FingerprintSnapshot.Success(Sha256(combined), packageRuntimeFingerprint, projectPackagesFingerprint, metadata, inventory, total);
            }
            catch
            {
                return FingerprintSnapshot.Failure("package_runtime", "read_exception", "unknown", total);
            }
        }

        internal static bool ShouldRetryExplicitRefreshInventory(bool refreshPending, bool localInventoryStale, bool localInventoryUnavailable, int retryCount)
        {
            return refreshPending && (localInventoryStale || localInventoryUnavailable)
                && retryCount >= 0 && retryCount < MaxExplicitRefreshConvergenceRetries;
        }

        static bool TryAppendProjectPackageFile(string projectRoot, StringBuilder input, string relativePath, Stopwatch budget, out string failureReason, out string failureFile)
        {
            failureFile = ProjectPackageFileKind(relativePath);
            if (!TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, relativePath, budget, MaxProjectPackageFingerprintMilliseconds, null, out var fingerprint, out failureReason)) return false;
            input.Append(fingerprint);
            return true;
        }

        void RebuildWatchers(LocalPackageCodeInventoryInput[] localInputs)
        {
            var roots = new List<WatcherRoot>();
            AddWatcherRoot(roots, m_packageRoot, true, false);
            AddWatcherRoot(roots, Path.Combine(ProjectPaths.ProjectRoot, "Packages"), false, true);
            if (localInputs != null)
            {
                for (var index = 0; index < localInputs.Length; index++)
                {
                    var input = localInputs[index];
                    if (input != null) AddWatcherRoot(roots, input.ResolvedPath, true, false);
                }
            }
            roots.Sort((left, right) => string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase));
            var signature = string.Join("\n", roots.ConvertAll(root => root.Path + "|" + root.IncludeSubdirectories + "|" + root.ProjectPackages).ToArray());
            if (string.Equals(signature, m_watcherSignature, StringComparison.Ordinal)) return;

            lock (m_watcherGate)
            {
                if (Volatile.Read(ref m_disposed) != 0) return;
                DisposeWatchers();
                for (var index = 0; index < roots.Count; index++)
                {
                    var root = roots[index];
                    try
                    {
                        var watcher = new FileSystemWatcher(root.Path)
                        {
                            IncludeSubdirectories = root.IncludeSubdirectories,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
                        };
                        watcher.Changed += (_, args) => OnWatcherPath(args.FullPath, root.ProjectPackages);
                        watcher.Created += (_, args) => OnWatcherPath(args.FullPath, root.ProjectPackages);
                        watcher.Deleted += (_, args) => OnWatcherPath(args.FullPath, root.ProjectPackages);
                        watcher.Renamed += (_, args) => OnWatcherRename(args.OldFullPath, args.FullPath, root.ProjectPackages);
                        watcher.Error += (_, __) => Invalidate("filesystem_watcher_error", root.ProjectPackages);
                        watcher.EnableRaisingEvents = true;
                        m_watchers.Add(watcher);
                    }
                    catch
                    {
                        Invalidate("filesystem_watcher_unavailable", root.ProjectPackages, false);
                    }
                }
                m_watcherSignature = signature;
            }
        }

        static void AddWatcherRoot(List<WatcherRoot> roots, string path, bool includeSubdirectories, bool projectPackages)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) return;
            string fullPath;
            try { fullPath = Path.GetFullPath(path); }
            catch { return; }
            for (var index = 0; index < roots.Count; index++)
            {
                if (string.Equals(roots[index].Path, fullPath, StringComparison.OrdinalIgnoreCase)) return;
            }
            roots.Add(new WatcherRoot(fullPath, includeSubdirectories, projectPackages));
        }

        void OnWatcherPath(string path, bool projectPackages)
        {
            if (projectPackages)
            {
                var name = Path.GetFileName(path);
                if (!string.Equals(name, "manifest.json", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(name, "packages-lock.json", StringComparison.OrdinalIgnoreCase)) return;
            }
            else if (!ShouldWatch(path) && !string.IsNullOrEmpty(Path.GetExtension(path)))
            {
                return;
            }
            Invalidate(projectPackages ? "project_packages_changed" : "package_files_changed", projectPackages);
        }

        void OnWatcherRename(string oldPath, string newPath, bool projectPackages)
        {
            OnWatcherPath(oldPath, projectPackages);
            OnWatcherPath(newPath, projectPackages);
        }

        void DisposeWatchers()
        {
            for (var index = 0; index < m_watchers.Count; index++)
            {
                try { m_watchers[index].Dispose(); } catch { }
            }
            m_watchers.Clear();
        }

        internal static bool TryBuildProjectPackageFileFingerprint(string fullPath, string relativePath, out string fingerprint)
        {
            return TryBuildProjectPackageFileFingerprint(fullPath, relativePath, null, Stopwatch.StartNew(), MaxProjectPackageFingerprintMilliseconds, null, out fingerprint, out _);
        }

        internal static bool TryBuildProjectPackageFileFingerprint(string fullPath, string relativePath, out string fingerprint, out string failureReason)
        {
            return TryBuildProjectPackageFileFingerprint(fullPath, relativePath, null, Stopwatch.StartNew(), MaxProjectPackageFingerprintMilliseconds, null, out fingerprint, out failureReason);
        }

        internal static bool TryBuildProjectPackageFileFingerprintWithinRoot(string trustedRoot, string relativePath, out string fingerprint)
        {
            return TryBuildProjectPackageFileFingerprintWithinRoot(trustedRoot, relativePath, Stopwatch.StartNew(), MaxProjectPackageFingerprintMilliseconds, null, out fingerprint, out _);
        }

        internal static bool TryBuildProjectPackageFileFingerprintWithinRoot(string trustedRoot, string relativePath, Stopwatch budget, long maxElapsedMilliseconds, Action afterFirstRead, out string fingerprint, out string failureReason)
        {
            fingerprint = string.Empty;
            failureReason = "path_security_rejected";
            if (string.IsNullOrEmpty(trustedRoot) || string.IsNullOrEmpty(relativePath))
            {
                return false;
            }
            if (BudgetExceeded(budget, maxElapsedMilliseconds))
            {
                failureReason = "budget_exhausted";
                return false;
            }

            string canonicalRoot;
            string fullPath;
            try
            {
                canonicalRoot = NormalizeNativePath(trustedRoot);
                fullPath = NormalizeNativePath(Path.Combine(canonicalRoot, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch
            {
                return false;
            }

            if (!IsSameOrChildPath(fullPath, canonicalRoot))
            {
                return false;
            }
            try
            {
                if (HasReparsePointBelowRoot(fullPath, canonicalRoot))
                {
                    return false;
                }
            }
            catch
            {
                failureReason = "read_exception";
                return false;
            }
            return TryBuildProjectPackageFileFingerprint(fullPath, relativePath, canonicalRoot, budget, maxElapsedMilliseconds, afterFirstRead, out fingerprint, out failureReason);
        }

        static bool TryBuildProjectPackageFileFingerprint(string fullPath, string relativePath, string trustedRoot, Stopwatch budget, long maxElapsedMilliseconds, Action afterFirstRead, out string fingerprint, out string failureReason)
        {
            fingerprint = string.Empty;
            failureReason = "path_security_rejected";
            if (string.IsNullOrEmpty(fullPath) || string.IsNullOrEmpty(relativePath))
            {
                return false;
            }
            if (BudgetExceeded(budget, maxElapsedMilliseconds))
            {
                failureReason = "budget_exhausted";
                return false;
            }
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(fullPath);
            }
            catch (FileNotFoundException)
            {
                if (string.Equals(relativePath, "Packages/manifest.json", StringComparison.Ordinal))
                {
                    failureReason = "missing_file";
                    return false;
                }
                if (BudgetExceeded(budget, maxElapsedMilliseconds))
                {
                    failureReason = "budget_exhausted";
                    return false;
                }
                try
                {
                    if (!string.IsNullOrEmpty(trustedRoot) && HasReparsePointBelowRoot(fullPath, trustedRoot))
                    {
                        failureReason = "path_security_rejected";
                        return false;
                    }
                }
                catch
                {
                    failureReason = "read_exception";
                    return false;
                }
                fingerprint = relativePath + "|missing|0|\n";
                failureReason = string.Empty;
                return true;
            }
            catch (DirectoryNotFoundException)
            {
                if (string.Equals(relativePath, "Packages/manifest.json", StringComparison.Ordinal))
                {
                    failureReason = "missing_file";
                    return false;
                }
                if (BudgetExceeded(budget, maxElapsedMilliseconds))
                {
                    failureReason = "budget_exhausted";
                    return false;
                }
                try
                {
                    if (!string.IsNullOrEmpty(trustedRoot) && HasReparsePointBelowRoot(fullPath, trustedRoot))
                    {
                        failureReason = "path_security_rejected";
                        return false;
                    }
                }
                catch
                {
                    failureReason = "read_exception";
                    return false;
                }
                fingerprint = relativePath + "|missing|0|\n";
                failureReason = string.Empty;
                return true;
            }
            catch
            {
                failureReason = "read_exception";
                return false;
            }
            if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
            {
                return false;
            }

            try
            {
                if (!TryReadStableProjectPackageFile(fullPath, trustedRoot, budget, maxElapsedMilliseconds, out var first, out failureReason))
                {
                    return false;
                }
                afterFirstRead?.Invoke();
                if (!TryReadStableProjectPackageFile(fullPath, trustedRoot, budget, maxElapsedMilliseconds, out var second, out failureReason))
                {
                    return false;
                }
                if (first.CreationTicks != second.CreationTicks)
                {
                    failureReason = "replacement_race";
                    return false;
                }
                if (first.Length != second.Length
                    || first.WriteTicks != second.WriteTicks
                    || !string.Equals(first.Digest, second.Digest, StringComparison.Ordinal))
                {
                    failureReason = "unstable_metadata";
                    return false;
                }
                if (!string.IsNullOrEmpty(trustedRoot) && HasReparsePointBelowRoot(fullPath, trustedRoot))
                {
                    failureReason = "path_security_rejected";
                    return false;
                }
                fingerprint = relativePath + "|file|" + first.Length + "|" + first.Digest + "\n";
                failureReason = string.Empty;
                return true;
            }
            catch (FileNotFoundException)
            {
                failureReason = "replacement_race";
                return false;
            }
            catch (DirectoryNotFoundException)
            {
                failureReason = "replacement_race";
                return false;
            }
            catch
            {
                failureReason = "read_exception";
                return false;
            }
        }

        static bool TryReadStableProjectPackageFile(string fullPath, string trustedRoot, Stopwatch total, long maxElapsedMilliseconds, out ProjectPackageFileRead observation, out string failureReason)
        {
            observation = null;
            failureReason = "read_exception";
            if (BudgetExceeded(total, maxElapsedMilliseconds))
            {
                failureReason = "budget_exhausted";
                return false;
            }
            if (!string.IsNullOrEmpty(trustedRoot) && HasReparsePointBelowRoot(fullPath, trustedRoot))
            {
                failureReason = "path_security_rejected";
                return false;
            }

            var before = new FileInfo(fullPath);
            if (!before.Exists)
            {
                failureReason = "replacement_race";
                return false;
            }
            if ((before.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                failureReason = "path_security_rejected";
                return false;
            }
            if (before.Length < 0 || before.Length > MaxProjectPackageFileBytes)
            {
                failureReason = "size_limit_exceeded";
                return false;
            }
            var beforeLength = before.Length;
            var beforeWriteTicks = before.LastWriteTimeUtc.Ticks;
            var beforeCreationTicks = before.CreationTimeUtc.Ticks;
            var readBytes = 0L;
            var digest = string.Empty;

            using (var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var sha256 = SHA256.Create())
            {
                if (stream.Length != beforeLength)
                {
                    failureReason = "replacement_race";
                    return false;
                }
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                {
                    readBytes += read;
                    if (readBytes > MaxProjectPackageFileBytes)
                    {
                        failureReason = "size_limit_exceeded";
                        return false;
                    }
                    if (BudgetExceeded(total, maxElapsedMilliseconds))
                    {
                        failureReason = "budget_exhausted";
                        return false;
                    }
                    sha256.TransformBlock(buffer, 0, read, buffer, 0);
                }
                sha256.TransformFinalBlock(new byte[0], 0, 0);
                digest = ToHex(sha256.Hash);
            }
            if (BudgetExceeded(total, maxElapsedMilliseconds))
            {
                failureReason = "budget_exhausted";
                return false;
            }

            var after = new FileInfo(fullPath);
            if (!after.Exists || after.CreationTimeUtc.Ticks != beforeCreationTicks)
            {
                failureReason = "replacement_race";
                return false;
            }
            if ((after.Attributes & FileAttributes.ReparsePoint) != 0
                || (!string.IsNullOrEmpty(trustedRoot) && HasReparsePointBelowRoot(fullPath, trustedRoot)))
            {
                failureReason = "path_security_rejected";
                return false;
            }
            if (after.Length != readBytes || after.Length != beforeLength || after.LastWriteTimeUtc.Ticks != beforeWriteTicks)
            {
                failureReason = "unstable_metadata";
                return false;
            }
            observation = new ProjectPackageFileRead(after.Length, after.LastWriteTimeUtc.Ticks, after.CreationTimeUtc.Ticks, digest);
            failureReason = string.Empty;
            return true;
        }

        static bool BudgetExceeded(Stopwatch budget, long maxElapsedMilliseconds)
        {
            return budget == null || budget.ElapsedMilliseconds > maxElapsedMilliseconds;
        }

        static string ProjectPackageFileKind(string relativePath)
        {
            if (string.Equals(relativePath, "Packages/manifest.json", StringComparison.Ordinal))
            {
                return "manifest";
            }
            if (string.Equals(relativePath, "Packages/packages-lock.json", StringComparison.Ordinal))
            {
                return "packages_lock";
            }
            return "unknown";
        }

        static bool HasReparsePointBelowRoot(string fullPath, string root)
        {
            var canonicalRoot = TrimTrailingDirectorySeparators(NormalizeNativePath(root));
            var canonicalPath = NormalizeNativePath(fullPath);
            if (!IsSameOrChildPath(canonicalPath, canonicalRoot))
            {
                return true;
            }

            var current = Directory.GetParent(canonicalPath);
            while (current != null && !string.Equals(TrimTrailingDirectorySeparators(NormalizeNativePath(current.FullName)), canonicalRoot, NativePathComparison))
            {
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }
                current = current.Parent;
            }
            return current == null;
        }

        internal static bool IsSameOrChildPath(string path, string root)
        {
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root))
            {
                return false;
            }
            try
            {
                var candidate = TrimTrailingDirectorySeparators(NormalizeNativePath(path));
                var canonicalRoot = TrimTrailingDirectorySeparators(NormalizeNativePath(root));
                var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), NativePathComparison)
                    ? canonicalRoot
                    : canonicalRoot + Path.DirectorySeparatorChar;
                return string.Equals(candidate, canonicalRoot, NativePathComparison)
                    || candidate.StartsWith(rootPrefix, NativePathComparison);
            }
            catch
            {
                return false;
            }
        }

        static string NormalizeNativePath(string path)
        {
            return Path.GetFullPath(path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        }

        static string TrimTrailingDirectorySeparators(string path)
        {
            var pathRoot = Path.GetPathRoot(path);
            if (!string.IsNullOrEmpty(pathRoot) && string.Equals(path, pathRoot, NativePathComparison))
            {
                return pathRoot;
            }
            return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        static StringComparison NativePathComparison => Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        void ApplyCurrentSnapshot(FingerprintSnapshot snapshot)
        {
            m_currentFingerprint = snapshot == null ? string.Empty : snapshot.CombinedFingerprint;
            m_currentProjectPackagesFingerprint = snapshot == null ? string.Empty : snapshot.ProjectPackagesFingerprint;
        }

        bool ApplyLocalMetadataStatus(BridgeRuntimeState state, FingerprintSnapshot snapshot, string traceEvent, Stopwatch total, bool normalUpdateTrace = false, double traceNow = 0.0, bool pendingFingerprintChanged = false)
        {
            var metadata = snapshot == null ? null : snapshot.LocalMetadata;
            if (metadata == null || string.Equals(metadata.Status, "unavailable", StringComparison.Ordinal))
            {
                m_pendingFingerprint = snapshot == null ? string.Empty : snapshot.CombinedFingerprint;
                var traceUpdate = normalUpdateTrace ? BeginNormalUpdateTrace(traceNow, "unavailable", m_pendingFingerprint, pendingFingerprintChanged) : false;
                SetStatus(state, "unavailable", "Registered local package metadata could not be verified against Unity Package Manager resolution. Inspect package.info; no package-manager restart or resolve was attempted automatically.", true, m_currentFingerprint, m_pendingFingerprint, true, true);
                if (normalUpdateTrace) EndNormalUpdateTrace(traceUpdate, "unavailable", total);
                else BridgeReloadTrace.Log(traceEvent, "local_metadata_unavailable", total.ElapsedMilliseconds);
                return false;
            }
            if (string.Equals(metadata.Status, "stale_package_resolution", StringComparison.Ordinal))
            {
                m_pendingFingerprint = snapshot.CombinedFingerprint;
                var traceUpdate = normalUpdateTrace ? BeginNormalUpdateTrace(traceNow, "stale_package_resolution", m_pendingFingerprint, pendingFingerprintChanged) : false;
                SetStatus(state, "stale_package_resolution", "Registered local package package.json metadata differs from Unity Package Manager resolution. Inspect package.info; an AssetDatabase refresh or compile barrier does not prove UPM metadata agreement.", true, m_currentFingerprint, m_pendingFingerprint);
                if (normalUpdateTrace) EndNormalUpdateTrace(traceUpdate, "stale_package_resolution", total);
                else BridgeReloadTrace.Log(traceEvent, "stale_local_package_metadata", total.ElapsedMilliseconds);
                return false;
            }
            return true;
        }

        bool ApplyLocalInventoryStatus(BridgeRuntimeState state, FingerprintSnapshot snapshot, string traceEvent, Stopwatch total, bool normalUpdateTrace = false, double traceNow = 0.0, bool pendingFingerprintChanged = false)
        {
            var inventory = snapshot == null ? null : snapshot.LocalCodeInventory;
            state.SetLocalPackageCodeInventory(inventory);
            if (inventory == null || inventory.IsUnavailable)
            {
                m_pendingFingerprint = snapshot == null ? string.Empty : snapshot.CombinedFingerprint;
                var reason = inventory == null ? "snapshot_unavailable" : inventory.FailureReason;
                var traceUpdate = normalUpdateTrace ? BeginNormalUpdateTrace(traceNow, "unavailable", m_pendingFingerprint, pendingFingerprintChanged) : false;
                SetStatus(state, "unavailable", "Local package code inventory could not be compared with Unity AssetDatabase; reason=" + reason + ". The background monitor will retry the bounded read-only scan.", true, m_currentFingerprint, m_pendingFingerprint, false, true);
                if (normalUpdateTrace) EndNormalUpdateTrace(traceUpdate, "unavailable", total);
                else BridgeReloadTrace.Log(traceEvent, "local_package_inventory_unavailable", total.ElapsedMilliseconds);
                return false;
            }
            if (inventory.IsStale)
            {
                m_pendingFingerprint = snapshot.CombinedFingerprint;
                var traceUpdate = normalUpdateTrace ? BeginNormalUpdateTrace(traceNow, "package_refresh_required", m_pendingFingerprint, pendingFingerprintChanged) : false;
                SetStatus(state, "package_refresh_required", "Local package code inventory differs from Unity AssetDatabase for package " + inventory.MismatchPackage + "; compile readiness is not authoritative until an explicit refresh imports the same bounded inventory.", true, m_currentFingerprint, m_pendingFingerprint, false, m_refreshPending);
                if (normalUpdateTrace) EndNormalUpdateTrace(traceUpdate, "package_refresh_required", total);
                else BridgeReloadTrace.Log(traceEvent, "stale_local_package_code_inventory", total.ElapsedMilliseconds);
                return false;
            }
            m_lastCurrentInventory = inventory;
            return true;
        }

        void RestoreForPlayMode()
        {
            if (!EditorApplication.isPlayingOrWillChangePlaymode && !EditorApplication.isPlaying) return;
            try
            {
                var json = SessionState.GetString(SessionSnapshotKey, string.Empty);
                var snapshot = string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<PersistedSnapshot>(json);
                if (snapshot == null || snapshot.schema_version != 2
                    || !string.Equals(snapshot.package_version, UnityBridgeRuntime.PackageVersion, StringComparison.Ordinal)
                    || string.IsNullOrEmpty(snapshot.current_fingerprint)
                    || string.IsNullOrEmpty(snapshot.project_packages_fingerprint)) return;
                m_restoredSnapshot = snapshot;
                m_currentFingerprint = snapshot.current_fingerprint;
                m_currentProjectPackagesFingerprint = snapshot.project_packages_fingerprint;
                m_dirtyGeneration = 0;
                m_scannedGeneration = 0;
                m_publishedDirtyGeneration = 0;
                m_nextFallbackScanTime = EditorApplication.timeSinceStartup + FallbackScanIntervalSeconds;
            }
            catch
            {
                m_restoredSnapshot = null;
                SessionState.EraseString(SessionSnapshotKey);
            }
        }

        void ApplyRestoredSnapshot(BridgeRuntimeState state)
        {
            var snapshot = m_restoredSnapshot;
            if (snapshot == null) return;
            m_restoredSnapshot = null;
            m_lastCurrentInventory = LocalPackageCodeInventorySnapshot.Available(
                snapshot.local_package_count,
                snapshot.source_file_count,
                snapshot.asset_database_file_count,
                0,
                0,
                string.Empty,
                snapshot.source_fingerprint,
                snapshot.asset_database_fingerprint,
                snapshot.source_state_fingerprint,
                snapshot.compiler_source_file_count,
                0,
                0,
                snapshot.compiler_source_fingerprint);
            state.SetLocalPackageCodeInventory(m_lastCurrentInventory);
            SetStatus(state, "current", "Package/runtime freshness was restored across the Play Mode domain reload.", false, m_currentFingerprint, string.Empty);
        }

        void PersistCurrentSnapshot()
        {
            var inventory = m_lastCurrentInventory;
            if (inventory == null || !inventory.IsCurrent || string.IsNullOrEmpty(m_currentFingerprint) || string.IsNullOrEmpty(m_currentProjectPackagesFingerprint))
            {
                SessionState.EraseString(SessionSnapshotKey);
                return;
            }
            var snapshot = new PersistedSnapshot
            {
                schema_version = 2,
                package_version = UnityBridgeRuntime.PackageVersion,
                current_fingerprint = m_currentFingerprint,
                project_packages_fingerprint = m_currentProjectPackagesFingerprint,
                local_package_count = inventory.LocalPackageCount,
                source_file_count = inventory.SourceFileCount,
                asset_database_file_count = inventory.AssetDatabaseFileCount,
                source_fingerprint = inventory.SourceFingerprint,
                asset_database_fingerprint = inventory.AssetDatabaseFingerprint,
                source_state_fingerprint = inventory.SourceStateFingerprint,
                compiler_source_file_count = inventory.CompilerSourceFileCount,
                compiler_source_fingerprint = inventory.CompilerSourceFingerprint,
            };
            SessionState.SetString(SessionSnapshotKey, JsonUtility.ToJson(snapshot));
        }

        bool BeginNormalUpdateTrace(double now, string nextStatus, string nextPendingFingerprint, bool pendingFingerprintChanged = false)
        {
            var retain = ShouldRetainUpdateTrace(m_status, nextStatus, m_pendingFingerprint, nextPendingFingerprint, now, m_nextUnchangedTraceSampleTime, pendingFingerprintChanged);
            if (!retain)
            {
                return false;
            }

            BridgeReloadTrace.Log("package_update_monitor.update.start");
            m_nextUnchangedTraceSampleTime = now + UnchangedTraceSampleIntervalSeconds;
            return true;
        }

        void EndNormalUpdateTrace(bool traceUpdate, string status, Stopwatch total)
        {
            if (traceUpdate)
            {
                BridgeReloadTrace.Log("package_update_monitor.update.end", status, total.ElapsedMilliseconds);
            }
        }

        internal static bool ShouldRetainUpdateTrace(string currentStatus, string nextStatus, string currentPendingFingerprint, string nextPendingFingerprint, double now, double nextUnchangedTraceSampleTime, bool pendingFingerprintChanged = false)
        {
            return !string.Equals(currentStatus, nextStatus, StringComparison.Ordinal)
                || !string.Equals(currentPendingFingerprint, nextPendingFingerprint, StringComparison.Ordinal)
                || now >= nextUnchangedTraceSampleTime
                || pendingFingerprintChanged;
        }

        static string Sha256(string input)
        {
            using (var sha256 = SHA256.Create())
            {
                return ToHex(sha256.ComputeHash(Encoding.UTF8.GetBytes(input ?? string.Empty)));
            }
        }

        static bool ShouldWatch(string filePath)
        {
            return WatchedExtensions.Contains(Path.GetExtension(filePath));
        }

        static string ToHex(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                return string.Empty;
            }

            var builder = new StringBuilder(bytes.Length * 2);
            for (var index = 0; index < bytes.Length; index++)
            {
                builder.Append(bytes[index].ToString("x2"));
            }

            return builder.ToString();
        }

        void SetStatus(BridgeRuntimeState state, string status, string message, bool refreshPending, string currentFingerprint, string pendingFingerprint, bool localMetadataUnavailable = false, bool refreshWaitCanAdvance = false)
        {
            m_status = status;
            m_message = message;
            state.SetPackageUpdateStatus(m_status, m_message, refreshPending, currentFingerprint, pendingFingerprint, m_refreshCount, m_lastRefreshUtc, localMetadataUnavailable, m_fingerprintFailureClass, m_fingerprintFailureReason, m_fingerprintFailureFile, refreshWaitCanAdvance);
        }

        string FingerprintUnavailableMessage(bool afterExplicitRefresh)
        {
            var suffix = afterExplicitRefresh ? " after explicit refresh" : string.Empty;
            if (string.Equals(m_fingerprintFailureClass, "project_packages", StringComparison.Ordinal))
            {
                return "Project package metadata fingerprint is unavailable" + suffix + "; reason=" + m_fingerprintFailureReason + ", file=" + m_fingerprintFailureFile + ". The background monitor will retry the bounded read-only scan.";
            }
            return "Package/runtime fingerprint is unavailable" + suffix + "; reason=" + m_fingerprintFailureReason + ". The background monitor will retry the bounded read-only scan.";
        }

        void ClearFingerprintFailure()
        {
            m_fingerprintFailureClass = string.Empty;
            m_fingerprintFailureReason = string.Empty;
            m_fingerprintFailureFile = string.Empty;
        }

        void SetFingerprintFailure(string evidenceClass, string reason, string file)
        {
            m_fingerprintFailureClass = evidenceClass ?? string.Empty;
            m_fingerprintFailureReason = reason ?? "read_exception";
            m_fingerprintFailureFile = file ?? "unknown";
        }

        void LogWarning()
        {
            var now = DateTime.UtcNow;
            if (now < m_nextWarningUtc)
            {
                return;
            }

            m_nextWarningUtc = now.AddSeconds(30);
            var detail = m_fingerprintFailureClass + ":" + m_fingerprintFailureReason + ":" + m_fingerprintFailureFile;
            BridgeReloadTrace.Log("package_update_monitor.fingerprint.unavailable", detail);
            UnityEngine.Debug.LogWarning("SGG Unity MCP " + m_fingerprintFailureClass + " fingerprint unavailable (reason=" + m_fingerprintFailureReason + ", file=" + m_fingerprintFailureFile + "). The background monitor will retry the bounded read-only scan.");
        }

        sealed class ProjectPackageFileRead
        {
            public ProjectPackageFileRead(long length, long writeTicks, long creationTicks, string digest)
            {
                Length = length;
                WriteTicks = writeTicks;
                CreationTicks = creationTicks;
                Digest = digest;
            }

            public long Length { get; private set; }
            public long WriteTicks { get; private set; }
            public long CreationTicks { get; private set; }
            public string Digest { get; private set; }
        }

        sealed class FingerprintSnapshot
        {
            FingerprintSnapshot() { }

            public static FingerprintSnapshot Success(string combinedFingerprint, string packageRuntimeFingerprint, string projectPackagesFingerprint, LocalPackageMetadataSummary localMetadata, LocalPackageCodeInventorySnapshot localCodeInventory, Stopwatch duration)
            {
                return new FingerprintSnapshot
                {
                    CombinedFingerprint = combinedFingerprint ?? string.Empty,
                    PackageRuntimeFingerprint = packageRuntimeFingerprint ?? string.Empty,
                    ProjectPackagesFingerprint = projectPackagesFingerprint ?? string.Empty,
                    LocalMetadata = localMetadata,
                    LocalCodeInventory = localCodeInventory,
                    Duration = duration ?? Stopwatch.StartNew(),
                };
            }

            public static FingerprintSnapshot Failure(string failureClass, string failureReason, string failureFile, Stopwatch duration = null)
            {
                return new FingerprintSnapshot
                {
                    FailureClass = failureClass ?? "package_runtime",
                    FailureReason = failureReason ?? "read_exception",
                    FailureFile = failureFile ?? "unknown",
                    Duration = duration ?? Stopwatch.StartNew(),
                };
            }

            public string CombinedFingerprint { get; private set; }
            public string PackageRuntimeFingerprint { get; private set; }
            public string ProjectPackagesFingerprint { get; private set; }
            public LocalPackageMetadataSummary LocalMetadata { get; private set; }
            public LocalPackageCodeInventorySnapshot LocalCodeInventory { get; private set; }
            public string FailureClass { get; private set; }
            public string FailureReason { get; private set; }
            public string FailureFile { get; private set; }
            public Stopwatch Duration { get; private set; }
        }

        sealed class FingerprintRequest
        {
            public FingerprintRequest(string packageRoot, string projectRoot, LocalPackageCodeInventoryInput[] localInputs, string[] assetPaths, string[] compilationSourcePaths, string[] expectedCompilationSourcePaths, LocalPackageMetadataInput[] metadataInputs, string inputFailure)
            {
                PackageRoot = packageRoot ?? string.Empty;
                ProjectRoot = projectRoot ?? string.Empty;
                LocalInputs = localInputs ?? new LocalPackageCodeInventoryInput[0];
                AssetPaths = assetPaths ?? new string[0];
                CompilationSourcePaths = compilationSourcePaths ?? new string[0];
                ExpectedCompilationSourcePaths = expectedCompilationSourcePaths ?? new string[0];
                MetadataInputs = metadataInputs ?? new LocalPackageMetadataInput[0];
                InputFailure = inputFailure ?? string.Empty;
            }

            public string PackageRoot { get; private set; }
            public string ProjectRoot { get; private set; }
            public LocalPackageCodeInventoryInput[] LocalInputs { get; private set; }
            public string[] AssetPaths { get; private set; }
            public string[] CompilationSourcePaths { get; private set; }
            public string[] ExpectedCompilationSourcePaths { get; private set; }
            public LocalPackageMetadataInput[] MetadataInputs { get; private set; }
            public string InputFailure { get; private set; }
        }

        sealed class WatcherRoot
        {
            public WatcherRoot(string path, bool includeSubdirectories, bool projectPackages)
            {
                Path = path;
                IncludeSubdirectories = includeSubdirectories;
                ProjectPackages = projectPackages;
            }

            public string Path { get; private set; }
            public bool IncludeSubdirectories { get; private set; }
            public bool ProjectPackages { get; private set; }
        }

        [Serializable]
        sealed class PersistedSnapshot
        {
            public int schema_version;
            public string package_version;
            public string current_fingerprint;
            public string project_packages_fingerprint;
            public int local_package_count;
            public int source_file_count;
            public int asset_database_file_count;
            public int compiler_source_file_count;
            public string source_fingerprint;
            public string asset_database_fingerprint;
            public string source_state_fingerprint;
            public string compiler_source_fingerprint;
        }
    }
}
