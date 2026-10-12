using System;
using System.Globalization;
using SGG.UnityMCP.Editor.UI.Localization;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class GatewayTestHistoryModel
    {
        internal const int DefaultMiB = 200;
        internal const int DefaultBindings = 192000;
        internal const long MiB = 1024L * 1024;
        internal const int MaxMiB = 1024;
        internal const int MaxBindings = 1000000;
        internal const string Scope = "gateway_test_run_owner_metadata";

        [Serializable]
        internal sealed class Counts
        {
            public long bytes;
            public int bindings;
            public int files;
        }

        [Serializable]
        internal sealed class Preserved
        {
            public int active;
            public int unknown;
            public int invalid;
            public int foreign;
            public int batch_limit;
        }

        [Serializable]
        internal sealed class Snapshot
        {
            public string scope;
            public string capacity_state;
            public bool configuration_saved;
            public bool restart_required;
            public string storage_error_code;
            public long bytes;
            public int bindings;
            public long max_bytes;
            public int max_bindings;
            public int retention_days;
            public string settings_source;
            public string config_fingerprint;
            public string preview_fingerprint;
            public string preview_expires_at;
            public string completed_cleanup_blocked;
            public Counts candidates;
            public Counts removed;
            public Preserved preserved;
        }

        internal static bool ValidLimits(int sizeMiB, int bindings)
        {
            return sizeMiB >= 1 && sizeMiB <= MaxMiB && bindings >= 1 && bindings <= MaxBindings;
        }

        internal static bool ValidSnapshot(Snapshot status)
        {
            return ValidSettingsSnapshot(status) && status.capacity_state != "unavailable" && status.bytes >= 0 && status.bindings >= 0;
        }

        internal static bool ValidSettingsSnapshot(Snapshot status)
        {
            return status != null && status.scope == Scope && status.settings_source == "saved_config_restart_required"
                && status.max_bytes >= MiB && status.max_bytes <= MaxMiB * MiB && status.max_bindings >= 1 && status.max_bindings <= MaxBindings
                && status.retention_days >= 1 && status.retention_days <= 3650;
        }

        internal static float Percent(long used, long limit)
        {
            return limit <= 0 ? 0 : (float)Math.Min(100d, Math.Max(0d, used * 100d / limit));
        }

        internal static string ByteUsage(Snapshot status)
        {
            return McpWindowLocalization.Format("Metadata: {0} / {1} MiB ({2} bytes)",
                (status.bytes / (double)MiB).ToString("0.00", CultureInfo.InvariantCulture),
                (status.max_bytes / (double)MiB).ToString("0.##", CultureInfo.InvariantCulture), status.bytes);
        }

        internal static string BindingUsage(Snapshot status)
        {
            return McpWindowLocalization.Format("Records: {0} / {1}", status.bindings, status.max_bindings);
        }

        internal static bool CanConfirmCleanup(Snapshot preview, DateTime utcNow)
        {
            return ValidSnapshot(preview) && preview.candidates != null && preview.candidates.bindings > 0
                && preview.candidates.files > 0 && preview.candidates.bytes > 0 && preview.preserved != null
                && IsFingerprint(preview.preview_fingerprint)
                && DateTime.TryParse(preview.preview_expires_at, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var expires) && expires > utcNow;
        }

        internal static bool IsFingerprint(string value)
        {
            if (value == null || value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal)) return false;
            for (var i = 7; i < value.Length; i++)
                if (!((value[i] >= '0' && value[i] <= '9') || (value[i] >= 'a' && value[i] <= 'f'))) return false;
            return true;
        }

        internal static string CleanupSummary(Snapshot preview)
        {
            if (preview == null || preview.candidates == null || preview.preserved == null)
                return McpWindowLocalization.Text("Cleanup preview is unavailable. Nothing was removed.");
            var text = McpWindowLocalization.Format("Remove {0} history records ({1} files, {2} bytes)?", preview.candidates.bindings, preview.candidates.files, preview.candidates.bytes)
                + "\n\n" + McpWindowLocalization.Text("Only validated terminal Gateway metadata is removed. Unity test results and reports are never deleted.")
                + "\n" + McpWindowLocalization.Format("Preserved: active {0}, unknown {1}, invalid {2}, foreign {3}, beyond batch {4}.",
                    preview.preserved.active, preview.preserved.unknown, preview.preserved.invalid, preview.preserved.foreign, preview.preserved.batch_limit);
            if (!string.IsNullOrEmpty(preview.completed_cleanup_blocked))
                text += "\n\n" + McpWindowLocalization.Text("Completed-test cleanup is unavailable; this preview includes only proven non-executed requests.");
            return text;
        }
    }
}
