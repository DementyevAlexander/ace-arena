using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using UnityEditor;

namespace SGG.UnityMCP.Editor.Commands
{
    internal sealed class BackgroundCompilerProgressSnapshot
    {
        public static readonly BackgroundCompilerProgressSnapshot Unobserved = new BackgroundCompilerProgressSnapshot(
            false, false, false, false, "unobserved", "none", 0, 0, -1, 0, 0, 0, string.Empty, string.Empty);

        public BackgroundCompilerProgressSnapshot(
            bool observed,
            bool authoritative,
            bool active,
            bool failed,
            string state,
            string category,
            int relevantItemCount,
            int activeItemCount,
            int progressPermille,
            long catalogGeneration,
            long compilationGeneration,
            long completedCompilationGeneration,
            string packageFingerprint,
            string observedUtc,
            string failureReason = "",
            bool correlatedNativeFailureObserved = false,
            string nativeFailureCategory = "none",
            string nativeFailureObservedUtc = "")
        {
            Observed = observed;
            Authoritative = authoritative;
            Active = active;
            Failed = failed;
            State = state ?? "unavailable";
            Category = category ?? "none";
            RelevantItemCount = Math.Max(0, relevantItemCount);
            ActiveItemCount = Math.Max(0, activeItemCount);
            ProgressPermille = progressPermille < 0 ? -1 : Math.Min(1000, progressPermille);
            CatalogGeneration = Math.Max(0, catalogGeneration);
            CompilationGeneration = Math.Max(0, compilationGeneration);
            CompletedCompilationGeneration = Math.Max(0, completedCompilationGeneration);
            PackageFingerprint = packageFingerprint ?? string.Empty;
            ObservedUtc = observedUtc ?? string.Empty;
            FailureReason = failureReason ?? string.Empty;
            CorrelatedNativeFailureObserved = correlatedNativeFailureObserved;
            NativeFailureCategory = nativeFailureCategory ?? "none";
            NativeFailureObservedUtc = nativeFailureObservedUtc ?? string.Empty;
        }

        public bool Observed { get; }
        public bool Authoritative { get; }
        public bool Active { get; }
        public bool Failed { get; }
        public string State { get; }
        public string Category { get; }
        public int RelevantItemCount { get; }
        public int ActiveItemCount { get; }
        public int ProgressPermille { get; }
        public long CatalogGeneration { get; }
        public long CompilationGeneration { get; }
        public long CompletedCompilationGeneration { get; }
        public string PackageFingerprint { get; }
        public string ObservedUtc { get; }
        public string FailureReason { get; }
        public bool CorrelatedNativeFailureObserved { get; }
        public string NativeFailureCategory { get; }
        public string NativeFailureObservedUtc { get; }
        public bool BlocksReadiness => Observed && (!Authoritative || Active || Failed || !string.Equals(State, "complete", StringComparison.Ordinal));

        public string ToJson()
        {
            return "{\"observed\":" + JsonUtil.Bool(Observed)
                + ",\"authoritative\":" + JsonUtil.Bool(Authoritative)
                + ",\"active\":" + JsonUtil.Bool(Active)
                + ",\"failed\":" + JsonUtil.Bool(Failed)
                + ",\"blocks_readiness\":" + JsonUtil.Bool(BlocksReadiness)
                + ",\"state\":" + JsonUtil.String(State)
                + ",\"category\":" + JsonUtil.String(Category)
                + ",\"relevant_item_count\":" + RelevantItemCount
                + ",\"active_item_count\":" + ActiveItemCount
                + ",\"progress_permille\":" + (ProgressPermille < 0 ? "null" : ProgressPermille.ToString())
                + ",\"process_id\":" + Process.GetCurrentProcess().Id
                + ",\"catalog_generation\":" + CatalogGeneration
                + ",\"compilation_generation\":" + CompilationGeneration
                + ",\"completed_compilation_generation\":" + CompletedCompilationGeneration
                + ",\"package_fingerprint\":" + JsonUtil.String(PackageFingerprint)
                + ",\"observed_utc\":" + JsonUtil.String(ObservedUtc)
                + ",\"failure_reason\":" + JsonUtil.String(FailureReason)
                + ",\"correlated_native_failure_observed\":" + JsonUtil.Bool(CorrelatedNativeFailureObserved)
                + ",\"native_failure_category\":" + JsonUtil.String(NativeFailureCategory)
                + ",\"native_failure_observed_utc\":" + JsonUtil.String(NativeFailureObservedUtc)
                + ",\"native_failure_absence_authoritative\":false"
                + ",\"cancellation_attempted\":false,\"work_replayed\":false}";
        }
    }

    internal static class BackgroundCompilerProgress
    {
        const int MaxProgressItems = 256;
        const int ClearSettleMilliseconds = 500;
        static readonly object Gate = new object();
        static readonly HashSet<long> RunningItemIds = new HashSet<long>();
        static string s_generationKey = string.Empty;
        static DateTime s_clearSinceUtc = DateTime.MinValue;
        static bool s_failureLatched;
        static bool s_nativeFailureObserved;
        static string s_nativeFailureCategory = "none";
        static string s_nativeFailureObservedUtc = string.Empty;

        public static BackgroundCompilerProgressSnapshot Capture(long catalogGeneration, long compilationGeneration, long completedCompilationGeneration, string packageFingerprint)
        {
            var now = DateTime.UtcNow;
            var generationKey = Process.GetCurrentProcess().Id + "\u001f" + Math.Max(0, catalogGeneration) + "\u001f"
                + Math.Max(0, compilationGeneration) + "\u001f" + (packageFingerprint ?? string.Empty);
            try
            {
                var progressType = typeof(EditorApplication).Assembly.GetType("UnityEditor.Progress", false);
                var enumerateItems = progressType == null ? null : progressType.GetMethod("EnumerateItems", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, null, Type.EmptyTypes, null);
                if (enumerateItems == null)
                {
                    return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, "progress_api_unavailable");
                }

                var enumerable = enumerateItems.Invoke(null, null) as IEnumerable;
                if (enumerable == null)
                {
                    return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, "progress_items_unavailable");
                }

                var items = new List<ProgressObservation>();
                var scannedItems = 0;
                foreach (var item in enumerable)
                {
                    scannedItems++;
                    if (scannedItems > MaxProgressItems)
                    {
                        return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, "progress_item_limit_exceeded");
                    }
                    var observation = Observe(item, out var observationFailure);
                    if (!string.IsNullOrEmpty(observationFailure))
                    {
                        return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, observationFailure);
                    }
                    if (observation != null && observation.Category != "none")
                    {
                        items.Add(observation);
                    }
                }
                return Evaluate(generationKey, items, catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now);
            }
            catch (Exception exception)
            {
                return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, "progress_scan_failed:" + exception.GetType().Name);
            }
        }

        internal static BackgroundCompilerProgressSnapshot ForTests(bool authoritative, bool active, bool failed, string state = "complete", string category = "none")
        {
            return new BackgroundCompilerProgressSnapshot(true, authoritative, active, failed, state, category, active ? 1 : 0, active ? 1 : 0, active ? 500 : -1, 1, 1, 1, "sha256:test", "2026-09-02T00:00:00.0000000Z", authoritative ? string.Empty : "fixture_unavailable");
        }

        internal static ProgressItem ProgressItemForTests(long id, string name, string status, int progressPermille)
        {
            return new ProgressItem(id, name, string.Empty, status, progressPermille < 0 ? -1d : progressPermille / 1000d);
        }

        internal static BackgroundCompilerProgressSnapshot ObserveForTests(bool apiAvailable, IReadOnlyList<ProgressItem> sourceItems, DateTime now, long catalogGeneration, long compilationGeneration, long completedCompilationGeneration, string packageFingerprint)
        {
            if (!apiAvailable || sourceItems == null)
            {
                return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, "progress_api_unavailable");
            }
            if (sourceItems.Count > MaxProgressItems)
            {
                return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, "progress_item_limit_exceeded");
            }
            var observations = new List<ProgressObservation>();
            for (var index = 0; index < sourceItems.Count; index++)
            {
                var observation = Observe(sourceItems[index], out var failureReason);
                if (!string.IsNullOrEmpty(failureReason))
                {
                    return Unavailable(catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, failureReason);
                }
                if (observation != null && observation.Category != "none") observations.Add(observation);
            }
            var generationKey = "test-process\u001f" + Math.Max(0, catalogGeneration) + "\u001f" + Math.Max(0, compilationGeneration)
                + "\u001f" + (packageFingerprint ?? string.Empty);
            return Evaluate(generationKey, observations, catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now);
        }

        internal static void ResetForTests()
        {
            lock (Gate)
            {
                RunningItemIds.Clear();
                s_generationKey = string.Empty;
                s_clearSinceUtc = DateTime.MinValue;
                s_failureLatched = false;
                s_nativeFailureObserved = false;
                s_nativeFailureCategory = "none";
                s_nativeFailureObservedUtc = string.Empty;
            }
        }

        static BackgroundCompilerProgressSnapshot Evaluate(string generationKey, List<ProgressObservation> items, long catalogGeneration, long compilationGeneration, long completedCompilationGeneration, string packageFingerprint, DateTime now)
        {
            lock (Gate)
            {
                if (!string.Equals(s_generationKey, generationKey, StringComparison.Ordinal))
                {
                    s_generationKey = generationKey;
                    RunningItemIds.Clear();
                    s_clearSinceUtc = DateTime.MinValue;
                    s_failureLatched = false;
                    s_nativeFailureObserved = false;
                    s_nativeFailureCategory = "none";
                    s_nativeFailureObservedUtc = string.Empty;
                }

                var activeCount = 0;
                var progressTotal = 0;
                var progressCount = 0;
                var failed = false;
                var category = "none";
                for (var index = 0; index < items.Count; index++)
                {
                    var item = items[index];
                    category = HigherPriorityCategory(category, item.Category);
                    if (!IsSuccessfulStatus(item.Status) && !IsFailedStatus(item.Status))
                    {
                        activeCount++;
                        RunningItemIds.Add(item.Id);
                        if (item.ProgressPermille >= 0)
                        {
                            progressTotal += item.ProgressPermille;
                            progressCount++;
                        }
                    }
                    else if (IsFailedStatus(item.Status) && RunningItemIds.Contains(item.Id))
                    {
                        failed = true;
                        s_failureLatched = true;
                        if (item.Status == "failed" || item.Status == "error")
                        {
                            s_nativeFailureObserved = true;
                            s_nativeFailureCategory = item.Category;
                            s_nativeFailureObservedUtc = now.ToString("O");
                        }
                    }
                    else if (IsSuccessfulStatus(item.Status))
                    {
                        RunningItemIds.Remove(item.Id);
                    }
                }

                if (activeCount > 0)
                {
                    s_clearSinceUtc = DateTime.MinValue;
                    return Snapshot(true, true, false, "active", category, items.Count, activeCount, progressCount == 0 ? -1 : progressTotal / progressCount, catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, string.Empty);
                }
                if (failed || s_failureLatched)
                {
                    s_clearSinceUtc = DateTime.MinValue;
                    return Snapshot(true, false, true, "failed", category, items.Count, 0, -1, catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, "correlated_background_compiler_failure");
                }
                if (s_clearSinceUtc == DateTime.MinValue)
                {
                    s_clearSinceUtc = now;
                }
                var settled = (now - s_clearSinceUtc).TotalMilliseconds >= ClearSettleMilliseconds;
                if (settled) RunningItemIds.Clear();
                return Snapshot(true, false, false, settled ? "complete" : "settling", category, items.Count, 0, -1, catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now, string.Empty);
            }
        }

        static BackgroundCompilerProgressSnapshot Unavailable(long catalogGeneration, long compilationGeneration, long completedCompilationGeneration, string packageFingerprint, DateTime now, string reason)
        {
            lock (Gate)
            {
                s_clearSinceUtc = DateTime.MinValue;
            }
            return new BackgroundCompilerProgressSnapshot(true, false, false, false, "unavailable", "unknown", 0, 0, -1, catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now.ToString("O"), reason);
        }

        static BackgroundCompilerProgressSnapshot Snapshot(bool authoritative, bool active, bool failed, string state, string category, int relevantCount, int activeCount, int progressPermille, long catalogGeneration, long compilationGeneration, long completedCompilationGeneration, string packageFingerprint, DateTime now, string reason)
        {
            return new BackgroundCompilerProgressSnapshot(true, authoritative, active, failed, state, category, relevantCount, activeCount, progressPermille, catalogGeneration, compilationGeneration, completedCompilationGeneration, packageFingerprint, now.ToString("O"), reason, s_nativeFailureObserved, s_nativeFailureCategory, s_nativeFailureObservedUtc);
        }

        static ProgressObservation Observe(object item, out string failureReason)
        {
            failureReason = string.Empty;
            if (item == null)
            {
                failureReason = "progress_item_shape_unavailable";
                return null;
            }
            if (!TryReadMember(item, "id", out var idValue)
                || !TryReadMember(item, "status", out var statusValue))
            {
                failureReason = "progress_item_shape_unavailable";
                return null;
            }
            var hasName = TryReadMember(item, "name", out var nameValue);
            var hasDescription = TryReadMember(item, "description", out var descriptionValue);
            var hasTitle = TryReadMember(item, "title", out var titleValue);
            if (!hasName && !hasDescription && !hasTitle)
            {
                failureReason = "progress_item_shape_unavailable";
                return null;
            }
            var name = Convert.ToString(nameValue) ?? string.Empty;
            var description = Convert.ToString(descriptionValue) ?? string.Empty;
            var title = Convert.ToString(titleValue) ?? string.Empty;
            var category = Category(name + " " + description + " " + title);
            if (category == "none")
            {
                return null;
            }
            var status = (Convert.ToString(statusValue) ?? string.Empty).Trim().ToLowerInvariant();
            long id;
            try { id = Convert.ToInt64(idValue); }
            catch
            {
                failureReason = "progress_item_shape_unavailable";
                return null;
            }
            var progressPermille = -1;
            try
            {
                if (!TryReadMember(item, "progress", out var progressValue)) progressValue = null;
                var progress = Convert.ToDouble(progressValue);
                if (!double.IsNaN(progress) && !double.IsInfinity(progress) && progress >= 0)
                {
                    progressPermille = Math.Min(1000, Math.Max(0, (int)Math.Round(progress * 1000)));
                }
            }
            catch { }
            return new ProgressObservation(id, status, category, progressPermille);
        }

        static bool TryReadMember(object target, string name, out object value)
        {
            value = null;
            var type = target.GetType();
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (property != null)
            {
                value = property.GetValue(target, null);
                return true;
            }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (field == null) return false;
            value = field.GetValue(target);
            return true;
        }

        static string Category(string value)
        {
            var key = Normalize(value);
            if (key.Contains("compilingcomputevariants") || key.Contains("computevariant") || key.Contains("computeshadervariant") || key.Contains("computeshadercompiler"))
            {
                return "compute_variant_compile";
            }
            if (key.Contains("shadervariant") || key.Contains("compilingshader") || key.Contains("shadercompiler"))
            {
                return "shader_variant_compile";
            }
            if (key.Contains("runningbackend") && (key.Contains("shader") || key.Contains("compute")))
            {
                return "native_compiler";
            }
            if (key.Contains("nativecompiler")) return "native_compiler";
            return "none";
        }

        static bool IsSuccessfulStatus(string status)
        {
            return string.Equals(status, "succeeded", StringComparison.Ordinal)
                || string.Equals(status, "completed", StringComparison.Ordinal)
                || string.Equals(status, "finished", StringComparison.Ordinal);
        }

        static bool IsFailedStatus(string status)
        {
            return string.Equals(status, "failed", StringComparison.Ordinal)
                || string.Equals(status, "error", StringComparison.Ordinal)
                || string.Equals(status, "canceled", StringComparison.Ordinal)
                || string.Equals(status, "cancelled", StringComparison.Ordinal);
        }

        static string HigherPriorityCategory(string left, string right)
        {
            if (left == "compute_variant_compile" || right == "compute_variant_compile") return "compute_variant_compile";
            if (left == "shader_variant_compile" || right == "shader_variant_compile") return "shader_variant_compile";
            if (left == "native_compiler" || right == "native_compiler") return "native_compiler";
            return "none";
        }

        static string Normalize(string value)
        {
            var builder = new StringBuilder((value ?? string.Empty).Length);
            foreach (var character in value ?? string.Empty)
            {
                if (char.IsLetterOrDigit(character)) builder.Append(char.ToLowerInvariant(character));
            }
            return builder.ToString();
        }

        sealed class ProgressObservation
        {
            public ProgressObservation(long id, string status, string category, int progressPermille)
            {
                Id = id;
                Status = status ?? string.Empty;
                Category = category ?? "none";
                ProgressPermille = progressPermille;
            }

            public long Id { get; }
            public string Status { get; }
            public string Category { get; }
            public int ProgressPermille { get; }
        }

        internal sealed class ProgressItem
        {
            public ProgressItem(long id, string name, string description, string status, double progress)
            {
                this.id = id;
                this.name = name ?? string.Empty;
                this.description = description ?? string.Empty;
                this.status = status ?? string.Empty;
                this.progress = progress;
            }

            public readonly long id;
            public readonly string name;
            public readonly string description;
            public readonly string status;
            public readonly double progress;
        }
    }
}
