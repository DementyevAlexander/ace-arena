using System;
using System.Collections.Generic;
using System.Text;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SGG.UnityMCP.Editor.Bridge
{
    internal sealed class PackageLifecycleSnapshot
    {
        public string Status;
        public string CatalogStatus;
        public bool IsCompiling;
        public bool IsUpdating;
        public bool IsPlaying;
        public bool IsReady;
        public bool PackageRefreshPending;
        public bool PackageRefreshRequired;
        public bool PackageRefreshWaitCanAdvance;
        public bool StalePackageResolution;
        public bool PackageMetadataUnavailable;
        public bool LocalPackageCodeInventoryUnavailable;
        public bool LocalPackageCodeInventoryStale;
        public BackgroundCompilerProgressSnapshot BackgroundCompilerProgress;
        public int LastEditorUpdateAgeMs;
        public int StatusDurationMs;
        public string PackageUpdateJson;
        public string LocalPackageCodeInventoryDiagnosticsJson;
    }

    public sealed class BridgeRuntimeState
    {
        const int MaxDirtyScenes = 20;
        const int MaxPackageUpdateJsonChars = 1024;
        const int MaxLocalPackageCount = 64;
        const int MaxLocalPackageFileCount = 16384;
        const int MaxLocalPackageMismatchDetails = 16;

        public string InstanceId { get; set; } = string.Empty;
        public string Project { get; set; } = string.Empty;
        public string UnityVersion { get; set; } = string.Empty;
        public string CommandsHash { get; set; } = string.Empty;
        public long CatalogGeneration { get; private set; }
        public bool CatalogReady => CatalogGeneration > 0 && !string.IsNullOrEmpty(CommandsHash) && !string.Equals(CommandsHash, "bootstrap-pending", StringComparison.Ordinal);
        public string CatalogStatus { get; private set; } = "pending";
        public bool BridgeListening { get; private set; }
        public string Status { get; private set; } = "starting";
        public bool IsCompiling { get; private set; }
        public bool IsUpdating { get; private set; }
        public bool IsPlaying { get; private set; }
        public bool IsReady => string.Equals(Status, "ready", StringComparison.Ordinal);
        public int QueueDepth { get; private set; }
        public int LastEditorUpdateAgeMs => MillisecondsSince(m_lastEditorUpdateUtc);
        public int StatusDurationMs => MillisecondsSince(m_statusChangedUtc);
        public bool HasUnsavedSceneChanges { get; private set; }
        public int DirtySceneCount { get; private set; }
        public DirtySceneInfo[] DirtyScenes { get; private set; } = new DirtySceneInfo[0];
        public bool PackageRefreshPending { get; private set; } = true;
        public bool PackageRefreshWaitCanAdvance { get; private set; } = true;
        public string PackageUpdateStatus { get; private set; } = "unavailable";
        public string PackageUpdateMessage { get; private set; } = "Package/runtime and local package inventory monitor has not completed its initial bounded scan.";
        public string PackageUpdateFingerprint { get; private set; } = string.Empty;
        public string PackageUpdatePendingFingerprint { get; private set; } = string.Empty;
        public int PackageRefreshCount { get; private set; }
        public string PackageLastRefreshUtc { get; private set; } = string.Empty;
        public string PackageLastObservedUtc { get; private set; } = string.Empty;
        public string PackageLastTransitionUtc { get; private set; } = string.Empty;
        public bool PackageRefreshRequired => PackageRefreshPending;
        public bool StalePackageResolution => PackageRefreshPending && string.Equals(PackageUpdateStatus, "stale_package_resolution", StringComparison.Ordinal);
        public bool PackageMetadataUnavailable { get; private set; }
        public string PackageFingerprintFailureClass { get; private set; } = string.Empty;
        public string PackageFingerprintFailureReason { get; private set; } = string.Empty;
        public string PackageFingerprintFailureFile { get; private set; } = string.Empty;
        public string LocalPackageCodeInventoryStatus { get; private set; } = "unavailable";
        public string LocalPackageCodeInventoryFailureReason { get; private set; } = "not_scanned";
        public string LocalPackageCodeInventoryMismatchPackage { get; private set; } = string.Empty;
        public int LocalPackageCodeInventoryPackageCount { get; private set; }
        public int LocalPackageCodeInventorySourceFileCount { get; private set; }
        public int LocalPackageCodeInventoryAssetDatabaseFileCount { get; private set; }
        public int LocalPackageCodeInventoryMissingAssetFileCount { get; private set; }
        public int LocalPackageCodeInventoryUnexpectedAssetFileCount { get; private set; }
        public int LocalPackageCodeInventoryCompilerSourceFileCount { get; private set; }
        public int LocalPackageCodeInventoryMissingCompilerSourceFileCount { get; private set; }
        public int LocalPackageCodeInventoryUnexpectedCompilerSourceFileCount { get; private set; }
        public string LocalPackageCodeInventorySourceFingerprint { get; private set; } = string.Empty;
        public string LocalPackageCodeInventoryAssetDatabaseFingerprint { get; private set; } = string.Empty;
        public string LocalPackageCodeInventorySourceStateFingerprint { get; private set; } = string.Empty;
        public string LocalPackageCodeInventoryCompilerSourceFingerprint { get; private set; } = string.Empty;
        internal LocalPackageInventoryMismatchDetail[] LocalPackageCodeInventoryMismatchDetails { get; private set; } = new LocalPackageInventoryMismatchDetail[0];
        public int LocalPackageCodeInventoryMismatchDetailTotalCount { get; private set; }
        public bool LocalPackageCodeInventoryMismatchDetailsTruncated { get; private set; }
        public bool LocalPackageCodeInventoryStale => string.Equals(LocalPackageCodeInventoryStatus, "stale", StringComparison.Ordinal);
        public bool LocalPackageCodeInventoryUnavailable => string.Equals(LocalPackageCodeInventoryStatus, "unavailable", StringComparison.Ordinal);
        internal BackgroundCompilerProgressSnapshot BackgroundCompilerProgress { get; private set; } = BackgroundCompilerProgressSnapshot.Unobserved;
        DateTime m_lastEditorUpdateUtc = DateTime.UtcNow;
        DateTime m_statusChangedUtc = DateTime.UtcNow;
        readonly object m_packageStateGate = new object();
        DateTime m_lastPackageObservationUtc = DateTime.MinValue;
        string m_packageObservationSignature = string.Empty;

        public void UpdateFromEditor()
        {
            UpdateDirtySceneSnapshot();
            var compilation = ConsoleLogBuffer.CompilationLifecycleSnapshot();
            var backgroundCompilerProgress = SGG.UnityMCP.Editor.Commands.BackgroundCompilerProgress.Capture(
                CatalogGeneration,
                compilation.Generation,
                compilation.CompletedGeneration,
                PackageUpdateFingerprint);

            lock (m_packageStateGate)
            {
                IsCompiling = EditorApplication.isCompiling;
                IsUpdating = EditorApplication.isUpdating;
                IsPlaying = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isPlaying;
                QueueDepth = MainThreadDispatcher.QueueDepth;
                var now = DateTime.UtcNow;
                m_lastEditorUpdateUtc = now;
                UnityVersion = Application.unityVersion;
                BackgroundCompilerProgress = backgroundCompilerProgress;
                UpdateStatusLocked(now);
            }
        }

        public void SetPackageUpdateStatus(string status, string message, bool refreshPending, string currentFingerprint, string pendingFingerprint, int refreshCount, string lastRefreshUtc, bool localMetadataUnavailable = false, string fingerprintFailureClass = "", string fingerprintFailureReason = "", string fingerprintFailureFile = "", bool refreshWaitCanAdvance = false)
        {
            lock (m_packageStateGate)
            {
                var now = DateTime.UtcNow;
                PackageUpdateStatus = string.IsNullOrEmpty(status) ? "current" : status;
                PackageUpdateMessage = message ?? string.Empty;
                PackageRefreshPending = refreshPending;
                PackageRefreshWaitCanAdvance = refreshPending && refreshWaitCanAdvance;
                PackageUpdateFingerprint = currentFingerprint ?? string.Empty;
                PackageUpdatePendingFingerprint = pendingFingerprint ?? string.Empty;
                PackageRefreshCount = Math.Max(0, refreshCount);
                PackageLastRefreshUtc = lastRefreshUtc ?? string.Empty;
                PackageMetadataUnavailable = refreshPending
                    && localMetadataUnavailable
                    && string.Equals(PackageUpdateStatus, "unavailable", StringComparison.Ordinal);
                PackageFingerprintFailureClass = fingerprintFailureClass ?? string.Empty;
                PackageFingerprintFailureReason = fingerprintFailureReason ?? string.Empty;
                PackageFingerprintFailureFile = fingerprintFailureFile ?? string.Empty;
                UpdatePackageObservationTimestamps(now);
                UpdateStatusLocked(now);
            }
        }

        public void PublishCatalog(string commandsHash, long catalogGeneration)
        {
            lock (m_packageStateGate)
            {
                CommandsHash = commandsHash ?? string.Empty;
                CatalogGeneration = Math.Max(0, catalogGeneration);
                UpdateStatusLocked(DateTime.UtcNow);
            }
        }

        public void SetBridgeListening(bool listening)
        {
            lock (m_packageStateGate)
            {
                BridgeListening = listening;
                UpdateStatusLocked(DateTime.UtcNow);
            }
        }

        internal void SetLocalPackageCodeInventory(LocalPackageCodeInventorySnapshot inventory)
        {
            lock (m_packageStateGate)
            {
                inventory = inventory ?? LocalPackageCodeInventorySnapshot.Unavailable("snapshot_unavailable");
                LocalPackageCodeInventoryStatus = inventory.Status ?? "unavailable";
                LocalPackageCodeInventoryFailureReason = inventory.FailureReason ?? string.Empty;
                LocalPackageCodeInventoryMismatchPackage = inventory.MismatchPackage ?? string.Empty;
                LocalPackageCodeInventoryPackageCount = Math.Max(0, inventory.LocalPackageCount);
                LocalPackageCodeInventorySourceFileCount = Math.Max(0, inventory.SourceFileCount);
                LocalPackageCodeInventoryAssetDatabaseFileCount = Math.Max(0, inventory.AssetDatabaseFileCount);
                LocalPackageCodeInventoryMissingAssetFileCount = Math.Max(0, inventory.MissingAssetFileCount);
                LocalPackageCodeInventoryUnexpectedAssetFileCount = Math.Max(0, inventory.UnexpectedAssetFileCount);
                LocalPackageCodeInventoryCompilerSourceFileCount = Math.Max(0, inventory.CompilerSourceFileCount);
                LocalPackageCodeInventoryMissingCompilerSourceFileCount = Math.Max(0, inventory.MissingCompilerSourceFileCount);
                LocalPackageCodeInventoryUnexpectedCompilerSourceFileCount = Math.Max(0, inventory.UnexpectedCompilerSourceFileCount);
                LocalPackageCodeInventorySourceFingerprint = inventory.SourceFingerprint ?? string.Empty;
                LocalPackageCodeInventoryAssetDatabaseFingerprint = inventory.AssetDatabaseFingerprint ?? string.Empty;
                LocalPackageCodeInventorySourceStateFingerprint = inventory.SourceStateFingerprint ?? string.Empty;
                LocalPackageCodeInventoryCompilerSourceFingerprint = inventory.CompilerSourceFingerprint ?? string.Empty;
                var mismatchDetails = inventory.MismatchDetails ?? new LocalPackageInventoryMismatchDetail[0];
                var retainedDetailCount = Math.Min(MaxLocalPackageMismatchDetails, mismatchDetails.Length);
                LocalPackageCodeInventoryMismatchDetails = new LocalPackageInventoryMismatchDetail[retainedDetailCount];
                Array.Copy(mismatchDetails, LocalPackageCodeInventoryMismatchDetails, retainedDetailCount);
                LocalPackageCodeInventoryMismatchDetailTotalCount = Math.Max(retainedDetailCount, inventory.MismatchDetailTotalCount);
                LocalPackageCodeInventoryMismatchDetailsTruncated = inventory.MismatchDetailsTruncated || retainedDetailCount < mismatchDetails.Length;
                UpdatePackageObservationTimestamps(DateTime.UtcNow);
            }
        }

        void UpdatePackageObservationTimestamps(DateTime now)
        {
            var observedUtc = now > m_lastPackageObservationUtc ? now : m_lastPackageObservationUtc.AddTicks(1);
            m_lastPackageObservationUtc = observedUtc;
            PackageLastObservedUtc = observedUtc.ToString("o");
            var observationSignature = PackageObservationSignature();
            if (!string.Equals(m_packageObservationSignature, observationSignature, StringComparison.Ordinal))
            {
                m_packageObservationSignature = observationSignature;
                PackageLastTransitionUtc = PackageLastObservedUtc;
            }
        }

        string PackageObservationSignature()
        {
            return string.Join("\u001f", new[]
            {
                PackageUpdateStatus,
                PackageUpdateMessage,
                PackageRefreshPending.ToString(),
                PackageRefreshWaitCanAdvance.ToString(),
                PackageUpdateFingerprint,
                PackageUpdatePendingFingerprint,
                PackageRefreshCount.ToString(),
                PackageLastRefreshUtc,
                PackageMetadataUnavailable.ToString(),
                PackageFingerprintFailureClass,
                PackageFingerprintFailureReason,
                PackageFingerprintFailureFile,
                LocalPackageCodeInventoryStatus,
                LocalPackageCodeInventoryFailureReason,
                LocalPackageCodeInventoryMismatchPackage,
                LocalPackageCodeInventoryPackageCount.ToString(),
                LocalPackageCodeInventorySourceFileCount.ToString(),
                LocalPackageCodeInventoryAssetDatabaseFileCount.ToString(),
                LocalPackageCodeInventoryMissingAssetFileCount.ToString(),
                LocalPackageCodeInventoryUnexpectedAssetFileCount.ToString(),
                LocalPackageCodeInventoryCompilerSourceFileCount.ToString(),
                LocalPackageCodeInventoryMissingCompilerSourceFileCount.ToString(),
                LocalPackageCodeInventoryUnexpectedCompilerSourceFileCount.ToString(),
                LocalPackageCodeInventorySourceFingerprint,
                LocalPackageCodeInventoryAssetDatabaseFingerprint,
                LocalPackageCodeInventorySourceStateFingerprint,
                LocalPackageCodeInventoryCompilerSourceFingerprint,
                LocalPackageCodeInventoryDiagnosticsJsonLocked(),
            });
        }

        public void MarkReloading()
        {
            SetLifecycleStatus("reloading");
        }

        public void MarkWaitingForIdle()
        {
            SetLifecycleStatus("waiting_for_idle");
        }

        public void MarkStopping()
        {
            SetLifecycleStatus("stopping");
        }

        public string PackageUpdateJson()
        {
            lock (m_packageStateGate)
            {
                return PackageUpdateJsonLocked();
            }
        }

        internal PackageLifecycleSnapshot CapturePackageLifecycleSnapshot()
        {
            lock (m_packageStateGate)
            {
                return new PackageLifecycleSnapshot
                {
                    Status = Status,
                    CatalogStatus = CatalogStatus,
                    IsCompiling = IsCompiling,
                    IsUpdating = IsUpdating,
                    IsPlaying = IsPlaying,
                    IsReady = IsReady,
                    PackageRefreshPending = PackageRefreshPending,
                    PackageRefreshRequired = PackageRefreshRequired,
                    PackageRefreshWaitCanAdvance = PackageRefreshWaitCanAdvance,
                    StalePackageResolution = StalePackageResolution,
                    PackageMetadataUnavailable = PackageMetadataUnavailable,
                    LocalPackageCodeInventoryUnavailable = LocalPackageCodeInventoryUnavailable,
                    LocalPackageCodeInventoryStale = LocalPackageCodeInventoryStale,
                    BackgroundCompilerProgress = BackgroundCompilerProgress,
                    LastEditorUpdateAgeMs = MillisecondsSince(m_lastEditorUpdateUtc),
                    StatusDurationMs = MillisecondsSince(m_statusChangedUtc),
                    PackageUpdateJson = PackageUpdateJsonLocked(),
                    LocalPackageCodeInventoryDiagnosticsJson = LocalPackageCodeInventoryDiagnosticsJsonLocked(),
                };
            }
        }

        internal void SetBackgroundCompilerProgressForTests(BackgroundCompilerProgressSnapshot progress)
        {
            lock (m_packageStateGate)
            {
                BackgroundCompilerProgress = progress ?? BackgroundCompilerProgressSnapshot.Unobserved;
                UpdateStatusLocked(DateTime.UtcNow);
            }
        }

        internal string BackgroundCompilerProgressJson()
        {
            lock (m_packageStateGate)
            {
                return (BackgroundCompilerProgress ?? BackgroundCompilerProgressSnapshot.Unobserved).ToJson();
            }
        }

        internal string LocalPackageCodeInventoryDiagnosticsJson()
        {
            lock (m_packageStateGate)
            {
                return LocalPackageCodeInventoryDiagnosticsJsonLocked();
            }
        }

        string LocalPackageCodeInventoryDiagnosticsJsonLocked()
        {
            if (!LocalPackageCodeInventoryStale && LocalPackageCodeInventoryMismatchDetailTotalCount == 0) return "null";
            var details = LocalPackageCodeInventoryMismatchDetails ?? new LocalPackageInventoryMismatchDetail[0];
            var builder = new StringBuilder();
            builder.Append("{\"contract\":\"sgg.unity_mcp.local_package_inventory_diagnostics.v1\"")
                .Append(",\"compiler_source_authority\":\"active_compilation_assemblies\"")
                .Append(",\"missing_asset_file_count\":").Append(Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryMissingAssetFileCount)))
                .Append(",\"unexpected_asset_file_count\":").Append(Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryUnexpectedAssetFileCount)))
                .Append(",\"missing_compiler_source_file_count\":").Append(Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryMissingCompilerSourceFileCount)))
                .Append(",\"unexpected_compiler_source_file_count\":").Append(Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryUnexpectedCompilerSourceFileCount)))
                .Append(",\"details_total_count\":").Append(Math.Max(0, LocalPackageCodeInventoryMismatchDetailTotalCount))
                .Append(",\"details_returned_count\":").Append(details.Length)
                .Append(",\"details_truncated\":").Append(JsonUtil.Bool(LocalPackageCodeInventoryMismatchDetailsTruncated || details.Length < LocalPackageCodeInventoryMismatchDetailTotalCount))
                .Append(",\"automatic_repair_attempted\":false")
                .Append(",\"details\":[");
            for (var index = 0; index < details.Length; index++)
            {
                var detail = details[index];
                if (index > 0) builder.Append(',');
                builder.Append("{\"kind\":").Append(JsonUtil.String(detail == null ? string.Empty : detail.Kind))
                    .Append(",\"path\":").Append(JsonUtil.String(detail == null ? string.Empty : detail.Path));
                if (detail != null && !string.IsNullOrEmpty(detail.MetadataReason))
                {
                    builder.Append(",\"metadata_issue\":{\"reason\":").Append(JsonUtil.String(detail.MetadataReason));
                    if (detail.ObservedGuidLength > 0 && detail.ObservedGuidLength <= 4096) builder.Append(",\"observed_guid_length\":").Append(detail.ObservedGuidLength);
                    builder.Append('}');
                }
                builder.Append('}');
            }
            return builder.Append("]}").ToString();
        }

        string PackageUpdateJsonLocked()
        {
            var json = BuildPackageUpdateJson(false);
            if (json.Length < MaxPackageUpdateJsonChars)
            {
                return json;
            }

            json = BuildPackageUpdateJson(true);
            if (json.Length < MaxPackageUpdateJsonChars)
            {
                return json;
            }

            json = BuildPackageUpdateJson(true, true);
            return json.Length < MaxPackageUpdateJsonChars ? json : BuildMinimalPackageUpdateJson();
        }

        string BuildPackageUpdateJson(bool compact, bool bounded = false)
        {
            return "{\"status\":" + BoundedJsonString(PackageUpdateStatus, compact ? 26 : 32)
                + ",\"auto_refresh_enabled\":\"unknown\""
                + ",\"refresh_pending\":" + JsonUtil.Bool(PackageRefreshPending)
                + ",\"package_refresh_required\":" + JsonUtil.Bool(PackageRefreshRequired)
                + ",\"refresh_wait_can_advance\":" + JsonUtil.Bool(PackageRefreshWaitCanAdvance)
                + ",\"stale_package_resolution\":" + JsonUtil.Bool(StalePackageResolution)
                + ",\"fingerprint\":" + BoundedJsonString(PackageUpdateFingerprint, compact ? 66 : 96)
                + ",\"pending_fingerprint\":" + BoundedJsonString(PackageUpdatePendingFingerprint, compact ? 66 : 96)
                + ",\"refresh_count\":" + Math.Max(0, PackageRefreshCount)
                + ",\"last_refresh_utc\":" + (compact ? "\"\"" : BoundedJsonString(PackageLastRefreshUtc, 48))
                + ",\"last_observed_utc\":" + (compact ? "\"\"" : BoundedJsonString(PackageLastObservedUtc, 48))
                + ",\"last_transition_utc\":" + (bounded ? "\"\"" : BoundedJsonString(PackageLastTransitionUtc, 48))
                + ",\"fingerprint_failure\":" + (compact ? "null" : PackageFingerprintFailureJson())
                + ",\"local_package_code_inventory\":" + LocalPackageCodeInventoryJson(compact, !bounded)
                + ",\"message\":" + (bounded ? (string.IsNullOrEmpty(PackageUpdateMessage) ? "\"\"" : "\"~\"") : BoundedJsonString(PackageUpdateMessage, compact ? 8 : 128))
                + "}";
        }

        string BuildMinimalPackageUpdateJson()
        {
            return "{\"status\":" + BoundedJsonString(PackageUpdateStatus, 26)
                + ",\"auto_refresh_enabled\":\"unknown\""
                + ",\"refresh_pending\":" + JsonUtil.Bool(PackageRefreshPending)
                + ",\"package_refresh_required\":" + JsonUtil.Bool(PackageRefreshRequired)
                + ",\"refresh_wait_can_advance\":" + JsonUtil.Bool(PackageRefreshWaitCanAdvance)
                + ",\"stale_package_resolution\":" + JsonUtil.Bool(StalePackageResolution)
                + ",\"fingerprint\":\"\""
                + ",\"pending_fingerprint\":\"\""
                + ",\"refresh_count\":" + Math.Max(0, PackageRefreshCount)
                + ",\"last_refresh_utc\":\"\""
                + ",\"last_observed_utc\":\"\""
                + ",\"last_transition_utc\":\"\""
                + ",\"fingerprint_failure\":null"
                + ",\"local_package_code_inventory\":" + LocalPackageCodeInventoryJson(true, false)
                + ",\"message\":\"Package update diagnostics exceeded bounds.\"}";
        }

        string LocalPackageCodeInventoryJson(bool compact, bool includeCompilerSourceEvidence = true)
        {
            var compilerSourceEvidence = includeCompilerSourceEvidence
                ? ",\"compiler_source_file_count\":" + Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryCompilerSourceFileCount))
                    + ",\"missing_compiler_source_file_count\":" + Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryMissingCompilerSourceFileCount))
                    + ",\"unexpected_compiler_source_file_count\":" + Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryUnexpectedCompilerSourceFileCount))
                    + ",\"compiler_source_fingerprint\":" + BoundedJsonString(LocalPackageCodeInventoryCompilerSourceFingerprint, compact ? 66 : 128)
                : string.Empty;
            return "{\"status\":" + BoundedJsonString(LocalPackageCodeInventoryStatus, compact ? 16 : 32)
                + ",\"failure_reason\":" + (compact ? "\"\"" : BoundedJsonString(LocalPackageCodeInventoryFailureReason, 64))
                + ",\"mismatch_package\":" + (compact ? "\"\"" : BoundedJsonString(LocalPackageCodeInventoryMismatchPackage, 128))
                + ",\"local_package_count\":" + Math.Min(MaxLocalPackageCount, Math.Max(0, LocalPackageCodeInventoryPackageCount))
                + ",\"source_file_count\":" + Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventorySourceFileCount))
                + ",\"asset_database_file_count\":" + Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryAssetDatabaseFileCount))
                + ",\"missing_asset_file_count\":" + Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryMissingAssetFileCount))
                + ",\"unexpected_asset_file_count\":" + Math.Min(MaxLocalPackageFileCount, Math.Max(0, LocalPackageCodeInventoryUnexpectedAssetFileCount))
                + compilerSourceEvidence
                + ",\"source_fingerprint\":" + BoundedJsonString(LocalPackageCodeInventorySourceFingerprint, compact ? 66 : 128)
                + ",\"asset_database_fingerprint\":" + BoundedJsonString(LocalPackageCodeInventoryAssetDatabaseFingerprint, compact ? 66 : 128)
                + ",\"source_state_fingerprint\":" + BoundedJsonString(LocalPackageCodeInventorySourceStateFingerprint, compact ? 66 : 128)
                + "}";
        }

        string PackageFingerprintFailureJson()
        {
            if (string.IsNullOrEmpty(PackageFingerprintFailureClass)
                || string.IsNullOrEmpty(PackageFingerprintFailureReason)
                || string.IsNullOrEmpty(PackageFingerprintFailureFile))
            {
                return "null";
            }
            return "{\"evidence_class\":" + BoundedJsonString(PackageFingerprintFailureClass, 64)
                + ",\"reason\":" + BoundedJsonString(PackageFingerprintFailureReason, 64)
                + ",\"file\":" + BoundedJsonString(PackageFingerprintFailureFile, 32)
                + "}";
        }

        static string BoundedJsonString(string value, int maxEncodedChars)
        {
            var text = value ?? string.Empty;
            var encoded = new StringBuilder(Math.Min(maxEncodedChars, text.Length + 2));
            encoded.Append('"');
            for (var index = 0; index < text.Length; index++)
            {
                var character = text[index];
                var pairLength = char.IsHighSurrogate(character) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
                var encodedLength = pairLength == 2 ? 2 : JsonCharacterLength(character);
                if (encoded.Length + encodedLength + 1 > maxEncodedChars)
                {
                    break;
                }

                if (pairLength == 2)
                {
                    encoded.Append(character).Append(text[++index]);
                }
                else
                {
                    AppendJsonCharacter(encoded, character);
                }
            }
            return encoded.Append('"').ToString();
        }

        static int JsonCharacterLength(char character)
        {
            if (character == '\\' || character == '"' || character == '\n' || character == '\r' || character == '\t')
            {
                return 2;
            }
            return char.IsControl(character) || char.IsSurrogate(character) ? 6 : 1;
        }

        static void AppendJsonCharacter(StringBuilder encoded, char character)
        {
            switch (character)
            {
                case '\\': encoded.Append("\\\\"); break;
                case '"': encoded.Append("\\\""); break;
                case '\n': encoded.Append("\\n"); break;
                case '\r': encoded.Append("\\r"); break;
                case '\t': encoded.Append("\\t"); break;
                default:
                    if (char.IsControl(character) || char.IsSurrogate(character))
                    {
                        encoded.Append("\\u").Append(((int)character).ToString("x4"));
                    }
                    else
                    {
                        encoded.Append(character);
                    }
                    break;
            }
        }

        void UpdateStatusLocked(DateTime now)
        {
            var previousStatus = Status;
            Status = ResolveLifecycleStatus(IsCompiling, IsUpdating, IsPlaying, PackageRefreshPending);
            if (Status == "ready" && BackgroundCompilerProgress != null && BackgroundCompilerProgress.BlocksReadiness)
            {
                Status = "busy";
            }
            if (Status == "ready" || Status == "playing")
            {
                if (!CatalogReady)
                {
                    CatalogStatus = "pending";
                    Status = "starting";
                }
                else if (!BridgeListening)
                {
                    CatalogStatus = "unavailable";
                    Status = "error";
                }
                else
                {
                    CatalogStatus = "ready";
                }
            }
            else if (!CatalogReady)
            {
                CatalogStatus = "pending";
            }
            else if (!BridgeListening)
            {
                CatalogStatus = "unavailable";
            }
            else
            {
                CatalogStatus = "ready";
            }

            if (!string.Equals(previousStatus, Status, StringComparison.Ordinal))
            {
                m_statusChangedUtc = now;
            }
        }

        internal static string ResolveLifecycleStatus(bool isCompiling, bool isUpdating, bool isPlaying, bool packageRefreshPending)
        {
            if (isCompiling)
            {
                return "compiling";
            }
            if (isUpdating || packageRefreshPending)
            {
                return "updating";
            }
            return isPlaying ? "playing" : "ready";
        }

        void SetLifecycleStatus(string status)
        {
            lock (m_packageStateGate)
            {
                var nextStatus = string.IsNullOrEmpty(status) ? "starting" : status;
                if (!string.Equals(Status, nextStatus, StringComparison.Ordinal))
                {
                    m_statusChangedUtc = DateTime.UtcNow;
                }

                Status = nextStatus;
            }
        }

        static int MillisecondsSince(DateTime startedUtc)
        {
            var elapsedMs = (DateTime.UtcNow - startedUtc).TotalMilliseconds;
            if (elapsedMs <= 0)
            {
                return 0;
            }

            return (int)Math.Min(int.MaxValue, elapsedMs);
        }

        void UpdateDirtySceneSnapshot()
        {
            var dirtyScenes = new List<DirtySceneInfo>();
            var dirtyCount = 0;
            var activeScene = SceneManager.GetActiveScene();
            for (var index = 0; index < SceneManager.sceneCount; index++)
            {
                var scene = SceneManager.GetSceneAt(index);
                if (!scene.IsValid() || !scene.isLoaded || !scene.isDirty)
                {
                    continue;
                }

                dirtyCount++;
                if (dirtyScenes.Count >= MaxDirtyScenes)
                {
                    continue;
                }

                dirtyScenes.Add(new DirtySceneInfo
                {
                    scene_path = scene.path ?? string.Empty,
                    scene_name = scene.name ?? string.Empty,
                    is_active = scene.handle == activeScene.handle,
                });
            }

            HasUnsavedSceneChanges = dirtyCount > 0;
            DirtySceneCount = dirtyCount;
            DirtyScenes = dirtyScenes.ToArray();
        }
    }

    [Serializable]
    public sealed class DirtySceneInfo
    {
        public string scene_path = string.Empty;
        public string scene_name = string.Empty;
        public bool is_active;
    }
}
