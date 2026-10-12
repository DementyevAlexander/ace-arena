using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class UnityCliUpdateCheck
    {
        internal const string CheckStateNotChecked = "not_checked";
        internal const string CheckStateChecking = "checking";
        internal const string CheckStateChecked = "checked";
        internal const string CheckStateExpired = "expired";
        internal const string CheckStateFailed = "check_failed";
        internal const string CheckStateCancelled = "cancelled";
        internal const string CheckStateSkippedNotInstalled = "skipped_not_installed";

        internal const string UpdateStateNotChecked = "not_checked";
        internal const string UpdateStateChecking = "checking";
        internal const string UpdateStateCurrent = "current";
        internal const string UpdateStateAvailable = "update_available";
        internal const string UpdateStateUpstreamReview = "upstream_change_requires_review";
        internal const string UpdateStateLocalModified = "local_modified";
        internal const string UpdateStateUnsupported = "unsupported";
        internal const string UpdateStateCheckFailed = "check_failed";
        internal const string UpdateStateInProgress = "update_in_progress";
        internal const string UpdateStateVerificationRequired = "verification_required";
        internal const string UpdateStateOutcomeUnknown = "outcome_unknown";
        internal const string UpdateStateNotInstalled = "not_installed";

        internal const string CliPlatform = "win32-x64";
        internal const string CliChannel = "beta";
        internal const string CliSourceKind = "official_unity_cli_upgrade_check";
        internal const string PipelineSourceKind = "official_unity_pipeline_list_versions";
        internal const string SkillRepositoryPath = "skills/unity-cli";
        internal const string SkillChannel = SkillRepositoryPath;
        internal const string SkillSourceKind = "official_github_commits_path_api";
        internal const string SkillLatestApiUrl = "https://api.github.com/repos/Unity-Technologies/skills/commits?path=skills%2Funity-cli&per_page=1";
        internal const int MaxMetadataResponseCharacters = 16384;
        internal const int MaxMetadataEntries = 64;
        internal const int MaxMetadataEntryCharacters = 512;
        internal const int MaxSemVerCharacters = 128;
        internal static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(15);
        internal static readonly TimeSpan AutomaticCheckInterval = TimeSpan.FromHours(24);
        internal static readonly TimeSpan AutomaticFailureBackoff = TimeSpan.FromHours(1);
        internal static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(30);
        internal static readonly TimeSpan LocalProcessTimeout = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan MaximumAllComponentsStageBudget = TimeSpan.FromTicks((ProcessTimeout.Ticks * 4) + (LocalProcessTimeout.Ticks * 3));
        internal static readonly TimeSpan TotalTimeout = MaximumAllComponentsStageBudget.Add(TimeSpan.FromSeconds(15));

        static UnityCliUpdateCheckSnapshot s_cachedStatus = UnityCliUpdateCheckSnapshot.NotChecked();
        static UnityCliUpdateCheckCoordinator s_activeCoordinator;

        public static UnityCliUpdateCheckSnapshot CachedStatus => GetCachedStatus(DateTime.UtcNow);

        internal static UnityCliUpdateCheckSnapshot GetCachedStatus(DateTime utcNow)
        {
            if (s_cachedStatus != null && s_cachedStatus.IsComplete)
            {
                MarkExpired(s_cachedStatus.WindowsCli == null ? null : s_cachedStatus.WindowsCli.Update, utcNow);
                MarkExpired(s_cachedStatus.Pipeline == null ? null : s_cachedStatus.Pipeline.Update, utcNow);
                MarkExpired(s_cachedStatus.Skill == null ? null : s_cachedStatus.Skill.Update, utcNow);
            }

            return s_cachedStatus;
        }

        public static UnityCliUpdateCheckCoordinator Start(string projectRoot)
        {
            return Start(projectRoot, UnityToolUpdateComponents.All);
        }

        public static UnityCliUpdateCheckCoordinator Start(string projectRoot, UnityToolUpdateComponents requestedComponents)
        {
            if (s_activeCoordinator != null && !s_activeCoordinator.IsComplete)
            {
                return s_activeCoordinator;
            }

            var installed = InspectInstalledSet(projectRoot);
            var components = installed.Components & requestedComponents;
            return StartCoordinator(
                projectRoot,
                CreateInitialSnapshot(projectRoot, installed, components, DateTime.UtcNow, UnityToolFreshnessStore.Load(projectRoot)),
                null,
                () => DateTime.UtcNow,
                TotalTimeout);
        }

        // Explicit button-only availability reads may include missing Pipeline/Skill.
        // Normal and automatic checks retain their installed-component selection.
        public static UnityCliUpdateCheckCoordinator StartAvailabilityCheck(string projectRoot, UnityToolUpdateComponents requestedComponents)
        {
            if (s_activeCoordinator != null && !s_activeCoordinator.IsComplete)
            {
                return s_activeCoordinator;
            }

            var installed = InspectInstalledSet(projectRoot);
            return StartCoordinator(
                projectRoot,
                CreateInitialAvailabilitySnapshot(
                    projectRoot, installed, requestedComponents, DateTime.UtcNow,
                    (requestedComponents & UnityToolUpdateComponents.Pipeline) != 0
                        && TryConfirmPipelineAbsent(projectRoot, installed.Pipeline),
                    UnityToolFreshnessStore.Load(projectRoot)),
                null,
                () => DateTime.UtcNow,
                TotalTimeout);
        }

        public static bool TryStartAutomatic(string projectRoot, DateTime utcNow, out UnityCliUpdateCheckCoordinator coordinator)
        {
            coordinator = null;
            if (s_activeCoordinator != null && !s_activeCoordinator.IsComplete)
            {
                coordinator = s_activeCoordinator;
                return true;
            }

            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            var installed = InspectInstalledSet(projectRoot);
            var records = UnityToolFreshnessStore.Load(projectRoot);
            var components = UnityToolFreshnessStore.ComponentsNeedingAutomaticCheck(installed, records, now);
            var snapshot = CreateInitialSnapshot(projectRoot, installed, components, now, records);
            if (components == UnityToolUpdateComponents.None)
            {
                snapshot.IsComplete = true;
                Publish(snapshot);
                return false;
            }

            coordinator = StartCoordinator(projectRoot, snapshot, null, () => DateTime.UtcNow, TotalTimeout);
            return true;
        }

        public static void RestorePersistedStatus(string projectRoot)
        {
            if (s_cachedStatus != null && (s_cachedStatus.IsComplete || (s_activeCoordinator != null && !s_activeCoordinator.IsComplete)))
            {
                return;
            }

            var installed = InspectInstalledSet(projectRoot);
            var snapshot = CreateInitialSnapshot(
                projectRoot,
                installed,
                UnityToolUpdateComponents.None,
                DateTime.UtcNow,
                UnityToolFreshnessStore.Load(projectRoot));
            snapshot.IsComplete = true;
            Publish(snapshot);
        }

        public static void RecordFreshness(string projectRoot, UnityCliUpdateCheckSnapshot snapshot, DateTime utcNow)
        {
            UnityToolFreshnessStore.Record(projectRoot, InspectInstalledSet(projectRoot), snapshot, utcNow);
        }

        internal static UnityCliUpdateCheckCoordinator StartForTests(
            string projectRoot,
            UnityCliUpdateCheckSnapshot snapshot,
            IUpdateCheckProcessStarter processStarter,
            Func<DateTime> nowUtc,
            TimeSpan totalTimeout)
        {
            return StartCoordinator(projectRoot, snapshot, processStarter, nowUtc, totalTimeout);
        }

        static UnityCliUpdateCheckCoordinator StartCoordinator(
            string projectRoot,
            UnityCliUpdateCheckSnapshot snapshot,
            IUpdateCheckProcessStarter processStarter,
            Func<DateTime> nowUtc,
            TimeSpan totalTimeout)
        {
            if (s_activeCoordinator != null && !s_activeCoordinator.IsComplete)
            {
                return s_activeCoordinator;
            }

            Publish(snapshot);
            var coordinator = new UnityCliUpdateCheckCoordinator(
                projectRoot,
                snapshot,
                processStarter,
                nowUtc,
                totalTimeout);
            s_activeCoordinator = coordinator;
            return coordinator;
        }

        internal static UnityCliUpdateCheckSnapshot CreateInitialSnapshot(string projectRoot)
        {
            var installed = InspectInstalledSet(projectRoot);
            return CreateInitialSnapshot(projectRoot, installed, installed.Components, DateTime.UtcNow);
        }

        internal static UnityCliUpdateCheckSnapshot CreateInitialSnapshotForTests(
            UnityCliPipelineSetup.WindowsUnityCliStatus windows,
            bool cliCandidateExists,
            UnityCliPipelineSetup.UnityPipelineStatus pipeline,
            UnityCliProjectSkillInstaller.UnityCliSkillStatus skill,
            UnityToolUpdateComponents components,
            DateTime utcNow)
        {
            var installed = InspectInstalledSet(string.Empty, windows, cliCandidateExists, pipeline, skill);
            return CreateInitialSnapshot(string.Empty, installed, components, utcNow);
        }

        internal static UnityCliUpdateCheckSnapshot CreateInitialAvailabilitySnapshotForTests(
            UnityCliPipelineSetup.WindowsUnityCliStatus windows,
            bool cliCandidateExists,
            UnityCliPipelineSetup.UnityPipelineStatus pipeline,
            UnityCliProjectSkillInstaller.UnityCliSkillStatus skill,
            UnityToolUpdateComponents requestedComponents,
            DateTime utcNow,
            bool pipelineAbsenceVerified = false,
            UnityToolFreshnessRecords records = null)
        {
            var installed = InspectInstalledSet(string.Empty, windows, cliCandidateExists, pipeline, skill);
            return CreateInitialAvailabilitySnapshot(string.Empty, installed, requestedComponents, utcNow, pipelineAbsenceVerified, records);
        }

        static UnityCliUpdateCheckSnapshot CreateInitialAvailabilitySnapshot(
            string projectRoot,
            UnityToolInstalledSet installed,
            UnityToolUpdateComponents requestedComponents,
            DateTime utcNow,
            bool pipelineAbsenceVerified,
            UnityToolFreshnessRecords records)
        {
            var snapshot = CreateInitialSnapshot(projectRoot, installed, requestedComponents, utcNow, records);
            if ((requestedComponents & UnityToolUpdateComponents.Pipeline) != 0)
            {
                snapshot.CheckPipeline = true;
                snapshot.PipelineAvailabilityDisplayOnly = !installed.PipelineInstalled;
                snapshot.PipelineAbsenceVerified = pipelineAbsenceVerified && IsPipelineAbsenceCandidate(snapshot.Pipeline);
                if (snapshot.PipelineAvailabilityDisplayOnly)
                {
                    snapshot.Pipeline.Update = ComponentUpdateStatus.Checking(
                        snapshot.Pipeline.Version, string.Empty, PipelineSourceKind,
                        SourceIdentity(UnityCliPipelineSetup.PipelinePackageId + "|pipeline list-versions"));
                }
            }
            if ((requestedComponents & UnityToolUpdateComponents.Skill) != 0)
            {
                snapshot.CheckSkill = true;
                snapshot.SkillAvailabilityDisplayOnly = !installed.SkillInstalled
                    || !snapshot.Skill.Valid || !snapshot.Skill.ProvenanceVerified
                    || snapshot.Skill.ModifiedOrUnknown || snapshot.Skill.ReparsePoint
                    || (!string.Equals(snapshot.Skill.LocalState, UnityCliProjectSkillInstaller.LocalStateCurrentReviewed, StringComparison.Ordinal)
                        && !string.Equals(snapshot.Skill.LocalState, UnityCliProjectSkillInstaller.LocalStatePreviousReviewed, StringComparison.Ordinal));
                snapshot.SkillAbsenceVerified = !snapshot.Skill.Exists
                    && !snapshot.Skill.ModifiedOrUnknown && !snapshot.Skill.ReparsePoint
                    && !snapshot.Skill.Valid && !snapshot.Skill.ProvenanceVerified
                    && string.IsNullOrEmpty(snapshot.Skill.InstalledReviewedCommit)
                    && string.IsNullOrEmpty(snapshot.Skill.InstalledReviewedHash)
                    && string.IsNullOrEmpty(snapshot.Skill.ContentHash)
                    && string.Equals(snapshot.Skill.LocalState, UnityCliProjectSkillInstaller.LocalStateAbsent, StringComparison.Ordinal)
                    && string.Equals(snapshot.Skill.SourceProvenance, "absent", StringComparison.Ordinal);
                if (snapshot.SkillAvailabilityDisplayOnly)
                {
                    snapshot.Skill.Update = ComponentUpdateStatus.Checking(
                        snapshot.Skill.InstalledReviewedCommit, SkillChannel, SkillSourceKind, SkillSourceBaseIdentity());
                }
            }
            return snapshot;
        }

        static bool IsPipelineAbsenceCandidate(UnityCliPipelineSetup.UnityPipelineStatus pipeline)
        {
            return pipeline != null && !pipeline.Registered
                && string.IsNullOrEmpty(pipeline.Version) && string.IsNullOrEmpty(pipeline.ManifestVersion)
                && string.IsNullOrEmpty(pipeline.Source)
                && string.IsNullOrEmpty(pipeline.ResolvedPath)
                && (string.Equals(pipeline.EditorCompatibilityState, "supported", StringComparison.Ordinal)
                    || string.Equals(pipeline.EditorCompatibilityState, "unsupported", StringComparison.Ordinal))
                && Regex.IsMatch(pipeline.ProjectPackagesIdentitySha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        }

        static bool TryConfirmPipelineAbsent(string projectRoot, UnityCliPipelineSetup.UnityPipelineStatus pipeline)
        {
            if (!IsPipelineAbsenceCandidate(pipeline) || string.IsNullOrEmpty(projectRoot)) return false;
            try
            {
                if (!PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, "Packages/manifest.json", out var manifestFingerprint)
                    || !PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, "Packages/packages-lock.json", out var lockFingerprint)
                    || !string.Equals(pipeline.ProjectPackagesIdentitySha256, SourceIdentity(manifestFingerprint + lockFingerprint), StringComparison.Ordinal)) return false;
                var manifestPath = Path.Combine(Path.GetFullPath(projectRoot), "Packages", "manifest.json");
                if (new FileInfo(manifestPath).Length > 1048576) return false;
                var json = File.ReadAllText(manifestPath);
                // An unreadable, malformed, duplicate-key or non-string declaration
                // is unknown local state, never proof that the package is absent.
                return IsPipelineAbsentManifest(json)
                    && PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, "Packages/manifest.json", out var afterFingerprint)
                    && string.Equals(manifestFingerprint, afterFingerprint, StringComparison.Ordinal);
            }
            catch
            {
                return false;
            }
        }

        internal static bool IsPipelineAbsentManifest(string json)
        {
            return !string.IsNullOrEmpty(json) && json.Length <= 1048576
                && BridgeProtocol.IsValidUniqueObjectJson(json)
                && PipelineJsonEvidenceParser.TryReadOptionalStringPath(json, new[] { "dependencies", UnityCliPipelineSetup.PipelinePackageId }, out _)
                && !PipelineJsonEvidenceParser.TryReadStringPath(json, new[] { "dependencies", UnityCliPipelineSetup.PipelinePackageId }, out _);
        }

        static UnityCliUpdateCheckSnapshot CreateInitialSnapshot(
            string projectRoot,
            UnityToolInstalledSet installed,
            UnityToolUpdateComponents components,
            DateTime utcNow,
            UnityToolFreshnessRecords records = null)
        {
            installed = installed ?? InspectInstalledSet(projectRoot);
            components &= installed.Components;
            var windows = CloneWindowsStatus(installed.WindowsCli);
            var pipeline = installed.Pipeline ?? CreatePipelineStatus(projectRoot);
            var skill = installed.Skill ?? CreateSkillStatus(projectRoot);

            windows.Update = InitialComponentStatus(
                installed.WindowsCliInstalled,
                (components & UnityToolUpdateComponents.WindowsCli) != 0,
                windows.Version,
                CliChannel,
                CliSourceKind,
                SourceIdentity("unity.exe|upgrade|--check|" + CliChannel + "|" + CliPlatform),
                records == null ? null : records.windowsCli,
                installed.WindowsCliIdentity,
                utcNow);
            pipeline.Update = InitialComponentStatus(
                installed.PipelineInstalled,
                (components & UnityToolUpdateComponents.Pipeline) != 0,
                pipeline.Version,
                string.Empty,
                PipelineSourceKind,
                SourceIdentity(UnityCliPipelineSetup.PipelinePackageId + "|pipeline list-versions"),
                records == null ? null : records.pipeline,
                installed.PipelineIdentity,
                utcNow);
            skill.Update = InitialComponentStatus(
                installed.SkillInstalled,
                (components & UnityToolUpdateComponents.Skill) != 0,
                skill.InstalledReviewedCommit,
                SkillChannel,
                SkillSourceKind,
                SkillSourceBaseIdentity(),
                records == null ? null : records.skill,
                installed.SkillIdentity,
                utcNow);

            return new UnityCliUpdateCheckSnapshot(windows, pipeline, skill)
            {
                CheckWindowsCli = (components & UnityToolUpdateComponents.WindowsCli) != 0,
                CheckPipeline = (components & UnityToolUpdateComponents.Pipeline) != 0,
                CheckSkill = (components & UnityToolUpdateComponents.Skill) != 0,
                CliTransportCandidate = installed.WindowsCliCandidateExists,
                WindowsCliInstalledIdentity = installed.WindowsCliIdentity,
                PipelineInstalledIdentity = installed.PipelineIdentity,
                SkillInstalledIdentity = installed.SkillIdentity,
            };
        }

        static ComponentUpdateStatus InitialComponentStatus(
            bool installed,
            bool selected,
            string installedVersion,
            string channel,
            string sourceKind,
            string sourceIdentity,
            UnityToolFreshnessRecord record,
            string installedIdentity,
            DateTime utcNow)
        {
            if (!installed)
            {
                if (!selected && record != null && record.availabilityDisplayOnly
                    && UnityToolFreshnessStore.TryRestore(record, installedIdentity, utcNow, out var availability)
                    && string.Equals(availability.Channel, channel, StringComparison.Ordinal)
                    && string.Equals(availability.SourceKind, sourceKind, StringComparison.Ordinal))
                {
                    return availability;
                }
                return ComponentUpdateStatus.NotInstalled(channel, sourceKind, sourceIdentity);
            }
            if (selected)
            {
                return ComponentUpdateStatus.Checking(installedVersion, channel, sourceKind, sourceIdentity);
            }
            if (UnityToolFreshnessStore.TryRestore(record, installedIdentity, utcNow, out var restored)
                && string.Equals(restored.Channel, channel, StringComparison.Ordinal)
                && string.Equals(restored.SourceKind, sourceKind, StringComparison.Ordinal))
            {
                return restored;
            }
            return ComponentUpdateStatus.NotChecked(installedVersion, channel, sourceKind, sourceIdentity);
        }

        internal static void Publish(UnityCliUpdateCheckSnapshot status)
        {
            s_cachedStatus = status ?? UnityCliUpdateCheckSnapshot.NotChecked();
        }

        internal static void ResetCacheForTests()
        {
            if (s_activeCoordinator != null && !s_activeCoordinator.IsComplete)
            {
                s_activeCoordinator.Dispose();
            }

            s_activeCoordinator = null;
            s_cachedStatus = UnityCliUpdateCheckSnapshot.NotChecked();
        }

        public static void InvalidateCache()
        {
            if (s_activeCoordinator == null || s_activeCoordinator.IsComplete)
            {
                s_cachedStatus = UnityCliUpdateCheckSnapshot.NotChecked();
            }
        }

        public static void InvalidateIfLocalStateChanged(string projectRoot)
        {
            if (s_cachedStatus == null || !s_cachedStatus.IsComplete || (s_activeCoordinator != null && !s_activeCoordinator.IsComplete))
            {
                return;
            }

            var windows = UnityCliPipelineSetup.WindowsStatus;
            var pipeline = CreatePipelineStatus(projectRoot);
            var skill = CreateSkillStatus(projectRoot);
            var checkedWindows = s_cachedStatus.WindowsCli.Update != null && s_cachedStatus.WindowsCli.Update.AuthoritativeForMutation;
            var checkedPipeline = s_cachedStatus.Pipeline.Update != null && s_cachedStatus.Pipeline.Update.AuthoritativeForMutation;
            var checkedSkill = s_cachedStatus.Skill.Update != null && s_cachedStatus.Skill.Update.AuthoritativeForMutation;
            if ((checkedWindows
                    && (windows.Installed != s_cachedStatus.WindowsCli.Installed
                        || !string.Equals(windows.Version, s_cachedStatus.WindowsCli.Version, StringComparison.Ordinal)
                        || !string.Equals(windows.ExecutablePath, s_cachedStatus.WindowsCli.ExecutablePath, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(windows.ExecutableSignatureState, s_cachedStatus.WindowsCli.ExecutableSignatureState, StringComparison.Ordinal)
                        || !string.Equals(windows.ExecutableSigner, s_cachedStatus.WindowsCli.ExecutableSigner, StringComparison.Ordinal)
                        || !string.Equals(windows.ExecutableSignatureThumbprint, s_cachedStatus.WindowsCli.ExecutableSignatureThumbprint, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(windows.ExecutableSha256, s_cachedStatus.WindowsCli.ExecutableSha256, StringComparison.OrdinalIgnoreCase)))
                || (checkedPipeline
                    && (pipeline.Registered != s_cachedStatus.Pipeline.Registered
                        || !string.Equals(pipeline.Version, s_cachedStatus.Pipeline.Version, StringComparison.Ordinal)
                        || !string.Equals(pipeline.ManifestVersion, s_cachedStatus.Pipeline.ManifestVersion, StringComparison.Ordinal)
                        || !string.Equals(pipeline.Source, s_cachedStatus.Pipeline.Source, StringComparison.Ordinal)
                        || !string.Equals(pipeline.ProjectPackagesIdentitySha256, s_cachedStatus.Pipeline.ProjectPackagesIdentitySha256, StringComparison.Ordinal)))
                || (checkedSkill
                    && (skill.Exists != s_cachedStatus.Skill.Exists
                        || skill.Valid != s_cachedStatus.Skill.Valid
                        || !string.Equals(skill.LocalState, s_cachedStatus.Skill.LocalState, StringComparison.Ordinal)
                        || !string.Equals(skill.InstalledReviewedCommit, s_cachedStatus.Skill.InstalledReviewedCommit, StringComparison.Ordinal)
                        || !string.Equals(skill.InstalledReviewedHash, s_cachedStatus.Skill.InstalledReviewedHash, StringComparison.Ordinal)
                        || !string.Equals(skill.ContentHash, s_cachedStatus.Skill.ContentHash, StringComparison.OrdinalIgnoreCase))))
            {
                s_cachedStatus = UnityCliUpdateCheckSnapshot.NotChecked();
            }
        }

        static void MarkExpired(ComponentUpdateStatus status, DateTime utcNow)
        {
            if (status == null || !status.AuthoritativeForMutation || status.CheckState == CheckStateExpired || !status.IsExpired(utcNow))
            {
                return;
            }

            status.CheckState = CheckStateExpired;
            status.UpdateState = UpdateStateOutcomeUnknown;
            status.LastErrorCode = "check_cache_expired";
            status.LastErrorSummary = "The manual update check cache expired; run the check again.";
        }

        static UnityToolInstalledSet InspectInstalledSet(string projectRoot)
        {
            return InspectInstalledSet(
                projectRoot,
                CloneWindowsStatus(UnityCliPipelineSetup.WindowsStatus),
                File.Exists(UnityCliPipelineSetup.OfficialWindowsInstallPath),
                CreatePipelineStatus(projectRoot),
                CreateSkillStatus(projectRoot));
        }

        internal static UnityToolInstalledSet InspectInstalledSet(
            string projectRoot,
            UnityCliPipelineSetup.WindowsUnityCliStatus windows,
            bool cliCandidateExists,
            UnityCliPipelineSetup.UnityPipelineStatus pipeline,
            UnityCliProjectSkillInstaller.UnityCliSkillStatus skill)
        {
            windows = windows ?? UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked();
            pipeline = pipeline ?? new UnityCliPipelineSetup.UnityPipelineStatus();
            skill = skill ?? new UnityCliProjectSkillInstaller.UnityCliSkillStatus();
            var pipelineInstalled = pipeline.Registered
                && string.Equals(pipeline.Source, "Registry", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(pipeline.Version)
                && string.Equals(pipeline.Version, pipeline.ManifestVersion, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(pipeline.ProjectPackagesIdentitySha256);
            var skillInstalled = skill.Exists
                && !skill.ReparsePoint
                && !string.Equals(skill.LocalState, UnityCliProjectSkillInstaller.LocalStateIncomplete, StringComparison.Ordinal);
            return new UnityToolInstalledSet
            {
                WindowsCli = windows,
                Pipeline = pipeline,
                Skill = skill,
                WindowsCliCandidateExists = cliCandidateExists,
                WindowsCliInstalled = cliCandidateExists,
                PipelineInstalled = pipelineInstalled,
                SkillInstalled = skillInstalled,
                WindowsCliIdentity = CliInstalledIdentity(cliCandidateExists),
                PipelineIdentity = pipelineInstalled
                    ? SourceIdentity(pipeline.Version + "|" + pipeline.ManifestVersion + "|" + pipeline.Source + "|" + pipeline.ProjectPackagesIdentitySha256)
                    : SourceIdentity("availability|" + projectRoot + "|" + pipeline.Registered + "|" + pipeline.Version + "|"
                        + pipeline.ManifestVersion + "|" + pipeline.Source + "|" + pipeline.ResolvedPath + "|"
                        + pipeline.EditorCompatibilityState + "|" + pipeline.ProjectPackagesIdentitySha256),
                SkillIdentity = skillInstalled
                    ? SourceIdentity(skill.LocalState + "|" + skill.InstalledReviewedCommit + "|" + skill.InstalledReviewedHash + "|" + skill.ContentHash)
                    : SourceIdentity("availability|" + projectRoot + "|" + skill.Exists + "|" + skill.Valid + "|"
                        + skill.ReparsePoint + "|" + skill.ModifiedOrUnknown + "|" + skill.LocalState + "|"
                        + skill.SourceProvenance + "|" + skill.InstalledReviewedCommit + "|" + skill.InstalledReviewedHash + "|" + skill.ContentHash),
            };
        }

        static string CliInstalledIdentity(bool candidateExists)
        {
            if (!candidateExists)
            {
                return string.Empty;
            }

            try
            {
                var info = new FileInfo(UnityCliPipelineSetup.OfficialWindowsInstallPath);
                return SourceIdentity(info.Length.ToString(CultureInfo.InvariantCulture) + "|" + info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture));
            }
            catch
            {
                return string.Empty;
            }
        }

        internal static bool TryParseCliCheckResponse(string output, out CliCheckMetadata metadata, out string errorCode)
        {
            metadata = null;
            errorCode = "cli_response_invalid";
            if (!IsBoundedResponse(output))
            {
                errorCode = "cli_response_oversized";
                return false;
            }

            CliCheckEnvelope envelope;
            try
            {
                envelope = new CliCheckResponseReader(output).Read();
            }
            catch
            {
                errorCode = "cli_response_malformed_json";
                return false;
            }

            if (envelope == null || !envelope.success)
            {
                errorCode = "cli_check_unsuccessful";
                return false;
            }

            // The verified beta.9 binary exposes upgrade as the self-update alias
            // and reports its canonical name in JSON. Keep the legacy exact name.
            if (!string.Equals(envelope.command, "upgrade", StringComparison.Ordinal)
                && !string.Equals(envelope.command, "self-update", StringComparison.Ordinal))
            {
                errorCode = "cli_command_mismatch";
                return false;
            }

            if (envelope.data == null || !string.Equals(envelope.data.platform, CliPlatform, StringComparison.Ordinal))
            {
                errorCode = "cli_platform_mismatch";
                return false;
            }

            if (!TryValidateMessages(envelope.errors, out errorCode)
                || !TryValidateMessages(envelope.warnings, out errorCode))
            {
                return false;
            }

            if (envelope.errors.Length != 0)
            {
                errorCode = "cli_response_errors";
                return false;
            }

            if (!TryParseSemVer(envelope.data.currentVersion, out _))
            {
                errorCode = "cli_current_version_invalid";
                return false;
            }

            if (!TryParseSemVer(envelope.data.latestVersion, out _))
            {
                errorCode = "cli_latest_version_invalid";
                return false;
            }

            if (!IsVersionInChannel(envelope.data.currentVersion, CliChannel)
                || !IsVersionInChannel(envelope.data.latestVersion, CliChannel))
            {
                errorCode = "cli_channel_mismatch";
                return false;
            }

            metadata = new CliCheckMetadata
            {
                CurrentVersion = envelope.data.currentVersion,
                LatestVersion = envelope.data.latestVersion,
                Platform = envelope.data.platform,
                Channel = CliChannel,
            };
            return true;
        }

        internal static bool TryParsePipelineListVersionsResponse(string output, out PipelineListVersionsMetadata metadata, out string errorCode)
        {
            metadata = null;
            errorCode = "pipeline_response_invalid";
            if (!IsBoundedResponse(output))
            {
                errorCode = "pipeline_response_oversized";
                return false;
            }

            PipelineListVersionsEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<PipelineListVersionsEnvelope>(output);
            }
            catch
            {
                errorCode = "pipeline_response_malformed_json";
                return false;
            }

            if (envelope == null || !envelope.success)
            {
                errorCode = "pipeline_check_unsuccessful";
                return false;
            }

            if (!string.Equals(envelope.command, "pipeline list-versions", StringComparison.Ordinal))
            {
                errorCode = "pipeline_command_mismatch";
                return false;
            }

            if (envelope.data == null || !string.Equals(envelope.data.packageId, UnityCliPipelineSetup.PipelinePackageId, StringComparison.Ordinal))
            {
                errorCode = "pipeline_package_mismatch";
                return false;
            }

            if (!TryValidateMessages(envelope.errors, out errorCode)
                || !TryValidateMessages(envelope.warnings, out errorCode))
            {
                return false;
            }

            if (envelope.errors.Length != 0)
            {
                errorCode = "pipeline_response_errors";
                return false;
            }

            if (envelope.data.versions == null
                || envelope.data.versions.Length == 0
                || envelope.data.versions.Length > MaxMetadataEntries)
            {
                errorCode = "pipeline_version_count_invalid";
                return false;
            }

            if (!TryParseSemVer(envelope.data.latest, out _))
            {
                errorCode = "pipeline_latest_version_invalid";
                return false;
            }

            var latestIsListed = false;
            for (var index = 0; index < envelope.data.versions.Length; index++)
            {
                var candidate = envelope.data.versions[index];
                if (!TryParseSemVer(candidate, out _))
                {
                    errorCode = "pipeline_version_invalid";
                    return false;
                }

                if (string.Equals(candidate, envelope.data.latest, StringComparison.Ordinal))
                {
                    latestIsListed = true;
                }

                if (TryCompareSemVer(candidate, envelope.data.latest, out var comparison) && comparison > 0)
                {
                    errorCode = "pipeline_latest_not_highest";
                    return false;
                }

                for (var prior = 0; prior < index; prior++)
                {
                    if (string.Equals(candidate, envelope.data.versions[prior], StringComparison.Ordinal))
                    {
                        errorCode = "pipeline_version_duplicate";
                        return false;
                    }
                }
            }

            if (!latestIsListed)
            {
                errorCode = "pipeline_latest_not_listed";
                return false;
            }

            metadata = new PipelineListVersionsMetadata
            {
                PackageId = envelope.data.packageId,
                LatestVersion = envelope.data.latest,
                Versions = (string[])envelope.data.versions.Clone(),
            };
            return true;
        }

        internal static bool TryParseSkillLatestCommitResponse(string output, out string commit, out string errorCode)
        {
            commit = string.Empty;
            errorCode = "skill_latest_commit_invalid";
            if (!IsBoundedResponse(output))
            {
                errorCode = "skill_latest_response_oversized";
                return false;
            }

            var lines = (output ?? string.Empty).Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            if (lines.Length > 0 && lines[lines.Length - 1].Length == 0)
            {
                Array.Resize(ref lines, lines.Length - 1);
            }

            if (lines.Length != 1 || string.IsNullOrEmpty(lines[0]))
            {
                errorCode = "skill_latest_response_count_invalid";
                return false;
            }

            var fields = lines[0].Split('\t');
            if (fields.Length != 2 || !string.Equals(fields[1], SkillRepositoryPath, StringComparison.Ordinal))
            {
                errorCode = "skill_latest_path_mismatch";
                return false;
            }

            if (!Regex.IsMatch(fields[0], "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant))
            {
                errorCode = "skill_latest_commit_invalid";
                return false;
            }

            commit = fields[0].ToLowerInvariant();
            return true;
        }

        internal static string[] BuildSkillLatestCheckArguments()
        {
            const string script = "$ErrorActionPreference='Stop';$ProgressPreference='SilentlyContinue';"
                + "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;"
                + "$headers=@{Accept='application/vnd.github+json';'User-Agent'='SGG-Unity-MCP-Gateway';'X-GitHub-Api-Version'='2022-11-28'};"
                + "$items=@(Invoke-RestMethod -Uri '" + SkillLatestApiUrl + "' -Headers $headers -TimeoutSec 20);"
                + "if($items.Count -ne 1 -or [string]::IsNullOrEmpty([string]$items[0].sha)){throw 'Invalid skill commit response'};"
                + "[Console]::Out.Write(([string]$items[0].sha).ToLowerInvariant()+[char]9+'" + SkillRepositoryPath + "')";
            return new[]
            {
                "-NoLogo",
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy",
                "Bypass",
                "-Command",
                script,
            };
        }

        internal static bool TryValidateCliUpgradeHelp(string output)
        {
            return TryValidateCommandHelp(
                output,
                "Usage: unity upgrade [options]",
                new[] { "--target <version>", "-y, --yes" },
                new[] { "--format <format>", "--non-interactive", "--no-banner" })
                || TryValidateCommandHelp(
                    output,
                    "Usage: unity self-update|upgrade [options]",
                    new[] { "--target <version>", "-y, --yes" },
                    new[] { "--format <format>", "--non-interactive", "--no-banner" });
        }

        internal static bool TryValidatePipelineInstallHelp(string output)
        {
            return TryValidateCommandHelp(
                output,
                "Usage: unity pipeline install [options]",
                new[] { "--project-path <path>", "--package-version <version>" },
                new[] { "--format <format>", "--non-interactive", "--no-banner" });
        }

        internal static bool TryParseSemVer(string value, out UnityCliSemVersion version)
        {
            version = default(UnityCliSemVersion);
            if (string.IsNullOrEmpty(value) || value.Length > MaxSemVerCharacters)
            {
                return false;
            }

            var match = Regex.Match(
                value,
                "\\A(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)\\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?(?:\\+([0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*))?\\z",
                RegexOptions.CultureInvariant);
            if (!match.Success)
            {
                return false;
            }

            var prerelease = match.Groups[4].Success ? match.Groups[4].Value.Split('.') : new string[0];
            for (var index = 0; index < prerelease.Length; index++)
            {
                if (IsNumericIdentifier(prerelease[index]) && prerelease[index].Length > 1 && prerelease[index][0] == '0')
                {
                    return false;
                }
            }

            version = new UnityCliSemVersion(
                match.Groups[1].Value,
                match.Groups[2].Value,
                match.Groups[3].Value,
                prerelease,
                value);
            return true;
        }

        internal static bool TryCompareSemVer(string localVersion, string availableVersion, out int comparison)
        {
            comparison = 0;
            if (!TryParseSemVer(localVersion, out var local) || !TryParseSemVer(availableVersion, out var available))
            {
                return false;
            }

            comparison = local.CompareTo(available);
            return true;
        }

        internal static bool IsVersionInChannel(string value, string channel)
        {
            if (!TryParseSemVer(value, out _) || string.IsNullOrEmpty(channel))
            {
                return false;
            }

            var buildIndex = value.IndexOf('+');
            var semanticPart = buildIndex < 0 ? value : value.Substring(0, buildIndex);
            var prereleaseIndex = semanticPart.IndexOf('-');
            if (string.Equals(channel, "stable", StringComparison.Ordinal))
            {
                return prereleaseIndex < 0;
            }
            if (prereleaseIndex < 0)
            {
                return false;
            }

            var prerelease = semanticPart.Substring(prereleaseIndex + 1);
            var separator = prerelease.IndexOf('.');
            var identifier = separator < 0 ? prerelease : prerelease.Substring(0, separator);
            return string.Equals(identifier, channel, StringComparison.Ordinal);
        }

        internal static ComponentUpdateStatus BuildVersionUpdateStatus(
            string installedVersion,
            string availableVersion,
            string channel,
            string sourceKind,
            string sourceIdentity,
            bool componentInstalled,
            bool supported,
            DateTime checkedAtUtc)
        {
            var status = ComponentUpdateStatus.Checked(
                installedVersion,
                availableVersion,
                channel,
                sourceKind,
                SourceIdentity(sourceIdentity),
                checkedAtUtc);

            if (!supported)
            {
                status.UpdateState = UpdateStateUnsupported;
                status.LastErrorCode = "component_unsupported";
                status.LastErrorSummary = "The component is not supported by the current Editor or CLI.";
                return status;
            }

            if (!componentInstalled || string.IsNullOrEmpty(installedVersion))
            {
                status.UpdateState = UpdateStateUnsupported;
                status.LastErrorCode = "component_not_installed";
                status.LastErrorSummary = "The component is not installed locally.";
                return status;
            }

            if (!TryParseSemVer(installedVersion, out _))
            {
                status.UpdateState = UpdateStateOutcomeUnknown;
                status.LastErrorCode = "local_version_invalid";
                status.LastErrorSummary = "The locally installed version could not be validated.";
                return status;
            }

            if (!TryParseSemVer(availableVersion, out _))
            {
                status.UpdateState = UpdateStateCheckFailed;
                status.LastErrorCode = "available_version_invalid";
                status.LastErrorSummary = "The official available version could not be validated.";
                return status;
            }

            if (!TryCompareSemVer(installedVersion, availableVersion, out var comparison))
            {
                status.UpdateState = UpdateStateOutcomeUnknown;
                status.LastErrorCode = "version_comparison_failed";
                status.LastErrorSummary = "Installed and available versions could not be compared.";
                return status;
            }

            if (comparison == 0)
            {
                status.UpdateState = UpdateStateCurrent;
                return status;
            }

            if (comparison > 0)
            {
                status.UpdateState = UpdateStateCurrent;
                status.LastErrorCode = "local_newer_no_downgrade";
                status.LastErrorSummary = "The locally installed version is newer; downgrade is disabled.";
                return status;
            }

            status.UpdateState = UpdateStateAvailable;
            return status;
        }

        internal static void ApplySkillGitSignal(UnityCliProjectSkillInstaller.UnityCliSkillStatus localStatus, string remoteCommit, DateTime checkedAtUtc)
        {
            if (localStatus == null || !Regex.IsMatch(remoteCommit ?? string.Empty, "^[0-9a-fA-F]{40}$", RegexOptions.CultureInvariant))
            {
                if (localStatus != null)
                {
                    localStatus.UpstreamChangeDetected = false;
                    localStatus.AvailableReviewedCommit = UnityCliProjectSkillInstaller.ReviewedSourceCommit;
                    localStatus.AvailableReviewedHash = UnityCliProjectSkillInstaller.ReviewedSkillsHash;
                    localStatus.Update = ComponentUpdateStatus.Failed(
                        localStatus.InstalledReviewedCommit,
                        SkillChannel,
                        SkillSourceKind,
                        SkillSourceBaseIdentity(),
                        "skill_latest_commit_invalid",
                        "The official skill path history returned an invalid commit.",
                        checkedAtUtc);
                    localStatus.Update.AvailableVersion = UnityCliProjectSkillInstaller.ReviewedSourceCommit;
                }

                return;
            }

            remoteCommit = remoteCommit.ToLowerInvariant();
            var sourceIdentity = SkillSourceIdentity(remoteCommit);
            var matchesReviewed = string.Equals(remoteCommit, UnityCliProjectSkillInstaller.ReviewedSourceCommit, StringComparison.OrdinalIgnoreCase);
            localStatus.UpstreamChangeDetected = !matchesReviewed;
            localStatus.AvailableReviewedCommit = UnityCliProjectSkillInstaller.ReviewedSourceCommit;
            localStatus.AvailableReviewedHash = UnityCliProjectSkillInstaller.ReviewedSkillsHash;

            var status = ComponentUpdateStatus.Checked(
                localStatus.InstalledReviewedCommit,
                remoteCommit,
                SkillChannel,
                SkillSourceKind,
                sourceIdentity,
                checkedAtUtc);

            if (!matchesReviewed)
            {
                status.UpdateState = UpdateStateUpstreamReview;
                status.AuthoritativeForMutation = false;
                status.LastErrorCode = "unreviewed_upstream_change";
                status.LastErrorSummary = "The official latest commit differs from the supported immutable source snapshot; refresh the acquisition target, not manual content approval.";
            }
            else if (!localStatus.Exists)
            {
                status.UpdateState = UpdateStateUnsupported;
                status.LastErrorCode = "skill_not_installed";
                status.LastErrorSummary = "The official skill is not installed in this project.";
            }
            else if (localStatus.Valid
                && localStatus.ProvenanceVerified
                && string.Equals(localStatus.LocalState, UnityCliProjectSkillInstaller.LocalStatePreviousReviewed, StringComparison.Ordinal))
            {
                status.UpdateState = UpdateStateAvailable;
            }
            else if (localStatus.Valid
                && localStatus.ProvenanceVerified
                && string.Equals(localStatus.LocalState, UnityCliProjectSkillInstaller.LocalStateCurrentReviewed, StringComparison.Ordinal))
            {
                status.UpdateState = UpdateStateCurrent;
            }
            else if (localStatus.ReparsePoint || localStatus.ModifiedOrUnknown)
            {
                status.UpdateState = UpdateStateLocalModified;
                status.LastErrorCode = "skill_local_state_ambiguous";
                status.LastErrorSummary = "The project skill target is modified, unknown, or unsafe to replace.";
            }
            else
            {
                status.UpdateState = UpdateStateOutcomeUnknown;
                status.LastErrorCode = "skill_local_state_unknown";
                status.LastErrorSummary = "The project skill local state could not be proven.";
            }

            localStatus.Update = status;
        }

        internal static string SourceIdentity(string value)
        {
            return Sha256(value ?? string.Empty);
        }

        internal static string CliSourceIdentity(string installedVersion, string availableVersion)
        {
            return SourceIdentity(
                "unity.exe|upgrade|--check|" + CliChannel + "|" + CliPlatform + "|"
                + (installedVersion ?? string.Empty) + "|" + (availableVersion ?? string.Empty));
        }

        internal static string PipelineVersionsIdentity(IReadOnlyList<string> versions)
        {
            var builder = new StringBuilder(UnityCliPipelineSetup.PipelinePackageId);
            if (versions != null)
            {
                for (var index = 0; index < versions.Count; index++)
                {
                    builder.Append('|').Append(versions[index] ?? string.Empty);
                }
            }
            return SourceIdentity(builder.ToString());
        }

        internal static string PipelineSourceIdentity(
            string installedVersion,
            string availableVersion,
            string versionsIdentitySha256,
            string cliVersion,
            string editorVersion)
        {
            return SourceIdentity(
                UnityCliPipelineSetup.PipelinePackageId + "|pipeline list-versions|"
                + (installedVersion ?? string.Empty) + "|" + (availableVersion ?? string.Empty) + "|"
                + (versionsIdentitySha256 ?? string.Empty) + "|" + (cliVersion ?? string.Empty) + "|"
                + (editorVersion ?? string.Empty));
        }

        internal static string SkillSourceIdentity(string remoteCommit)
        {
            return SourceIdentity(UnityCliProjectSkillInstaller.Repository + "|" + SkillRepositoryPath + "|" + (remoteCommit ?? string.Empty).ToLowerInvariant());
        }

        internal static string SkillSourceBaseIdentity()
        {
            return SourceIdentity(UnityCliProjectSkillInstaller.Repository + "|" + SkillRepositoryPath);
        }

        static UnityCliPipelineSetup.WindowsUnityCliStatus CloneWindowsStatus(UnityCliPipelineSetup.WindowsUnityCliStatus source)
        {
            source = source ?? UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked();
            return new UnityCliPipelineSetup.WindowsUnityCliStatus
            {
                Checked = source.Checked,
                Installed = source.Installed,
                ExecutablePath = source.ExecutablePath,
                Version = source.Version,
                Message = source.Message,
                ExecutableSignatureState = source.ExecutableSignatureState,
                ExecutableSigner = source.ExecutableSigner,
                ExecutableSignatureThumbprint = source.ExecutableSignatureThumbprint,
                ExecutableSha256 = source.ExecutableSha256,
                Update = source.Update == null ? ComponentUpdateStatus.NotChecked() : source.Update.Clone(),
            };
        }

        static UnityCliPipelineSetup.UnityPipelineStatus CreatePipelineStatus(string projectRoot)
        {
            try
            {
                return UnityCliPipelineSetup.GetPipelineStatus(projectRoot);
            }
            catch
            {
                return new UnityCliPipelineSetup.UnityPipelineStatus
                {
                    EditorSupported = false,
                    EditorCompatibilityState = "unknown",
                    CliCompatibilityState = "unknown",
                    Update = ComponentUpdateStatus.NotChecked(),
                };
            }
        }

        static UnityCliProjectSkillInstaller.UnityCliSkillStatus CreateSkillStatus(string projectRoot)
        {
            try
            {
                return UnityCliProjectSkillInstaller.GetStatus(projectRoot);
            }
            catch
            {
                return new UnityCliProjectSkillInstaller.UnityCliSkillStatus
                {
                    LocalState = UnityCliProjectSkillInstaller.LocalStateUnknown,
                    ModifiedOrUnknown = true,
                    Message = "The project skill local state could not be inspected.",
                    Update = ComponentUpdateStatus.NotChecked(),
                };
            }
        }

        static bool IsBoundedResponse(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= MaxMetadataResponseCharacters;
        }

        static bool TryValidateCommandHelp(
            string output,
            string usage,
            IReadOnlyList<string> commandOptions,
            IReadOnlyList<string> globalOptions)
        {
            if (!IsBoundedResponse(output) || output.IndexOf('\0') >= 0)
            {
                return false;
            }

            var lines = output.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            var usageIndex = FindUniqueTrimmedLine(lines, usage);
            var optionsIndex = FindUniqueTrimmedLine(lines, "Options:");
            var globalOptionsIndex = FindUniqueTrimmedLine(lines, "Global Options:");
            if (usageIndex < 0 || optionsIndex <= usageIndex || globalOptionsIndex <= optionsIndex
                || FindNextHelpSection(lines, optionsIndex + 1) != globalOptionsIndex)
            {
                return false;
            }
            for (var index = 0; index < lines.Length; index++)
            {
                if (index != usageIndex && lines[index].Trim().StartsWith("Usage:", StringComparison.Ordinal)) return false;
            }
            var globalOptionsEnd = FindNextHelpSection(lines, globalOptionsIndex + 1);
            if (globalOptionsEnd < 0) globalOptionsEnd = lines.Length;

            for (var optionIndex = 0; optionIndex < commandOptions.Count; optionIndex++)
            {
                if (!ContainsUniqueOptionDeclaration(lines, optionsIndex + 1, globalOptionsIndex, commandOptions[optionIndex]))
                {
                    return false;
                }
            }

            for (var optionIndex = 0; optionIndex < globalOptions.Count; optionIndex++)
            {
                if (!ContainsUniqueOptionDeclaration(lines, globalOptionsIndex + 1, globalOptionsEnd, globalOptions[optionIndex]))
                {
                    return false;
                }
            }

            return true;
        }

        static int FindUniqueTrimmedLine(string[] lines, string expected)
        {
            var foundIndex = -1;
            for (var index = 0; index < lines.Length; index++)
            {
                if (!string.Equals(lines[index].Trim(), expected, StringComparison.Ordinal)) continue;
                if (foundIndex >= 0)
                {
                    return -1;
                }
                foundIndex = index;
            }
            return foundIndex;
        }

        static int FindNextHelpSection(string[] lines, int startIndex)
        {
            for (var index = startIndex; index < lines.Length; index++)
            {
                var candidate = lines[index].Trim();
                if (Regex.IsMatch(candidate, "^[A-Za-z][A-Za-z ]*:$", RegexOptions.CultureInvariant)) return index;
            }
            return -1;
        }

        static bool ContainsUniqueOptionDeclaration(string[] lines, int startIndex, int endIndex, string declaration)
        {
            var matches = 0;
            for (var index = startIndex; index < endIndex; index++)
            {
                var line = lines[index].TrimStart();
                if (!line.StartsWith(declaration, StringComparison.Ordinal)) continue;
                var remainder = line.Substring(declaration.Length);
                if (remainder.Length == 0 || (remainder.Length >= 2 && remainder[0] == ' ' && remainder[1] == ' ')) matches++;
            }
            return matches == 1;
        }

        static bool TryValidateMessages(string[] messages, out string errorCode)
        {
            errorCode = string.Empty;
            if (messages == null || messages.Length > MaxMetadataEntries)
            {
                errorCode = "metadata_message_count_invalid";
                return false;
            }

            for (var index = 0; index < messages.Length; index++)
            {
                if (messages[index] == null || messages[index].Length > MaxMetadataEntryCharacters || messages[index].IndexOf('\0') >= 0)
                {
                    errorCode = "metadata_message_invalid";
                    return false;
                }
            }

            return true;
        }

        static string Sha256(string value)
        {
            using (var hash = SHA256.Create())
            {
                var bytes = hash.ComputeHash(Encoding.UTF8.GetBytes(value));
                var builder = new StringBuilder(bytes.Length * 2);
                for (var index = 0; index < bytes.Length; index++)
                {
                    builder.Append(bytes[index].ToString("x2", CultureInfo.InvariantCulture));
                }

                return builder.ToString();
            }
        }

        internal static bool IsNumericIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return false;
            }

            for (var index = 0; index < value.Length; index++)
            {
                if (value[index] < '0' || value[index] > '9')
                {
                    return false;
                }
            }

            return true;
        }

        // JsonUtility tolerates duplicate keys, coerced values and unknown members.
        // Update authorization accepts only the bounded, observed response schema.
        sealed class CliCheckResponseReader
        {
            readonly string m_json;
            int m_index;

            internal CliCheckResponseReader(string json) { m_json = json; }

            internal CliCheckEnvelope Read()
            {
                var envelope = new CliCheckEnvelope();
                ReadObject(name =>
                {
                    switch (name)
                    {
                        case "success": envelope.success = ReadBoolean(); break;
                        case "command": envelope.command = ReadString(); break;
                        case "data": envelope.data = ReadData(); break;
                        case "errors": envelope.errors = ReadStringArray(); break;
                        case "warnings": envelope.warnings = ReadStringArray(); break;
                        default: throw new FormatException();
                    }
                }, 5);
                SkipWhitespace();
                if (m_index != m_json.Length) throw new FormatException();
                return envelope;
            }

            CliCheckData ReadData()
            {
                var data = new CliCheckData();
                ReadObject(name =>
                {
                    switch (name)
                    {
                        case "currentVersion": data.currentVersion = ReadString(); break;
                        case "latestVersion": data.latestVersion = ReadString(); break;
                        case "platform": data.platform = ReadString(); break;
                        default: throw new FormatException();
                    }
                }, 3);
                return data;
            }

            void ReadObject(Action<string> readMember, int requiredMembers)
            {
                Require('{');
                var names = new HashSet<string>(StringComparer.Ordinal);
                if (Take('}')) throw new FormatException();
                while (true)
                {
                    var name = ReadString();
                    if (name.Length > 32 || !names.Add(name) || names.Count > requiredMembers) throw new FormatException();
                    Require(':');
                    readMember(name);
                    if (Take('}')) break;
                    Require(',');
                }
                if (names.Count != requiredMembers) throw new FormatException();
            }

            string[] ReadStringArray()
            {
                Require('[');
                var items = new List<string>();
                if (Take(']')) return items.ToArray();
                while (true)
                {
                    if (items.Count >= MaxMetadataEntries) throw new FormatException();
                    items.Add(ReadString());
                    if (Take(']')) return items.ToArray();
                    Require(',');
                }
            }

            bool ReadBoolean()
            {
                SkipWhitespace();
                if (TakeLiteral("true")) return true;
                if (TakeLiteral("false")) return false;
                throw new FormatException();
            }

            string ReadString()
            {
                Require('"');
                var builder = new StringBuilder();
                while (m_index < m_json.Length)
                {
                    var character = m_json[m_index++];
                    if (character == '"') return builder.ToString();
                    if (character < ' ') throw new FormatException();
                    if (character == '\\')
                    {
                        if (m_index >= m_json.Length) throw new FormatException();
                        switch (m_json[m_index++])
                        {
                            case '"': character = '"'; break;
                            case '\\': character = '\\'; break;
                            case '/': character = '/'; break;
                            case 'b': character = '\b'; break;
                            case 'f': character = '\f'; break;
                            case 'n': character = '\n'; break;
                            case 'r': character = '\r'; break;
                            case 't': character = '\t'; break;
                            case 'u':
                                if (m_index + 4 > m_json.Length
                                    || !ushort.TryParse(m_json.Substring(m_index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var code)) throw new FormatException();
                                character = (char)code;
                                m_index += 4;
                                break;
                            default: throw new FormatException();
                        }
                    }
                    if (character == '\0' || builder.Length >= MaxMetadataEntryCharacters) throw new FormatException();
                    builder.Append(character);
                }
                throw new FormatException();
            }

            bool TakeLiteral(string value)
            {
                if (m_index + value.Length > m_json.Length
                    || string.CompareOrdinal(m_json, m_index, value, 0, value.Length) != 0) return false;
                m_index += value.Length;
                return true;
            }

            bool Take(char value)
            {
                SkipWhitespace();
                if (m_index >= m_json.Length || m_json[m_index] != value) return false;
                m_index++;
                return true;
            }

            void Require(char value) { if (!Take(value)) throw new FormatException(); }

            void SkipWhitespace()
            {
                while (m_index < m_json.Length
                    && (m_json[m_index] == ' ' || m_json[m_index] == '\t' || m_json[m_index] == '\r' || m_json[m_index] == '\n')) m_index++;
            }
        }

        [Serializable]
        sealed class CliCheckEnvelope
        {
            public bool success;
            public string command;
            public CliCheckData data;
            public string[] errors;
            public string[] warnings;
        }

        [Serializable]
        sealed class CliCheckData
        {
            public string currentVersion;
            public string latestVersion;
            public string platform;
        }

        [Serializable]
        sealed class PipelineListVersionsEnvelope
        {
            public bool success;
            public string command;
            public PipelineListVersionsData data;
            public string[] errors;
            public string[] warnings;
        }

        [Serializable]
        sealed class PipelineListVersionsData
        {
            public string packageId;
            public string latest;
            public string[] versions;
        }

    }

    [Flags]
    internal enum UnityToolUpdateComponents
    {
        None = 0,
        WindowsCli = 1,
        Pipeline = 2,
        Skill = 4,
        All = WindowsCli | Pipeline | Skill,
    }

    internal sealed class UnityToolInstalledSet
    {
        public UnityCliPipelineSetup.WindowsUnityCliStatus WindowsCli;
        public UnityCliPipelineSetup.UnityPipelineStatus Pipeline;
        public UnityCliProjectSkillInstaller.UnityCliSkillStatus Skill;
        public bool WindowsCliCandidateExists;
        public bool WindowsCliInstalled;
        public bool PipelineInstalled;
        public bool SkillInstalled;
        public string WindowsCliIdentity = string.Empty;
        public string PipelineIdentity = string.Empty;
        public string SkillIdentity = string.Empty;

        public UnityToolUpdateComponents Components
        {
            get
            {
                var components = UnityToolUpdateComponents.None;
                if (WindowsCliInstalled) components |= UnityToolUpdateComponents.WindowsCli;
                if (PipelineInstalled) components |= UnityToolUpdateComponents.Pipeline;
                if (SkillInstalled) components |= UnityToolUpdateComponents.Skill;
                return components;
            }
        }
    }

    [Serializable]
    internal sealed class UnityToolFreshnessRecord
    {
        public bool availabilityDisplayOnly;
        public string installedIdentity = string.Empty;
        public string installedVersion = string.Empty;
        public string availableVersion = string.Empty;
        public string channel = string.Empty;
        public string sourceKind = string.Empty;
        public string sourceIdentitySha256 = string.Empty;
        public string updateState = string.Empty;
        public string checkedAtUtc = string.Empty;
        public string lastErrorCode = string.Empty;
        public string lastErrorSummary = string.Empty;
        public string lastFailureAtUtc = string.Empty;
    }

    [Serializable]
    internal sealed class UnityToolFreshnessRecords
    {
        public int schemaVersion = 1;
        public UnityToolFreshnessRecord windowsCli = new UnityToolFreshnessRecord();
        public UnityToolFreshnessRecord pipeline = new UnityToolFreshnessRecord();
        public UnityToolFreshnessRecord skill = new UnityToolFreshnessRecord();
    }

    internal static class UnityToolFreshnessStore
    {
        const int SchemaVersion = 1;
        const int MaxSerializedCharacters = 8192;
        const string KeyPrefix = "SGG.UnityMCP.UnityToolFreshness.v1.";

        internal static UnityToolFreshnessRecords Load(string projectRoot)
        {
            if (string.IsNullOrWhiteSpace(projectRoot))
            {
                return new UnityToolFreshnessRecords();
            }

            try
            {
                var serialized = EditorPrefs.GetString(Key(projectRoot), string.Empty);
                if (string.IsNullOrEmpty(serialized) || serialized.Length > MaxSerializedCharacters)
                {
                    return new UnityToolFreshnessRecords();
                }

                var records = JsonUtility.FromJson<UnityToolFreshnessRecords>(serialized);
                return records != null && records.schemaVersion == SchemaVersion
                    ? Normalize(records)
                    : new UnityToolFreshnessRecords();
            }
            catch
            {
                return new UnityToolFreshnessRecords();
            }
        }

        internal static UnityToolUpdateComponents ComponentsNeedingAutomaticCheck(
            UnityToolInstalledSet installed,
            UnityToolFreshnessRecords records,
            DateTime utcNow)
        {
            installed = installed ?? new UnityToolInstalledSet();
            records = Normalize(records ?? new UnityToolFreshnessRecords());
            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            var components = UnityToolUpdateComponents.None;
            if (installed.WindowsCliInstalled && NeedsAutomaticCheck(
                records.windowsCli,
                installed.WindowsCliIdentity,
                UnityCliUpdateCheck.CliChannel,
                UnityCliUpdateCheck.CliSourceKind,
                now))
            {
                components |= UnityToolUpdateComponents.WindowsCli;
            }
            if (installed.PipelineInstalled && NeedsAutomaticCheck(
                records.pipeline,
                installed.PipelineIdentity,
                string.Empty,
                UnityCliUpdateCheck.PipelineSourceKind,
                now))
            {
                components |= UnityToolUpdateComponents.Pipeline;
            }
            if (installed.SkillInstalled && NeedsAutomaticCheck(
                records.skill,
                installed.SkillIdentity,
                UnityCliUpdateCheck.SkillChannel,
                UnityCliUpdateCheck.SkillSourceKind,
                now))
            {
                components |= UnityToolUpdateComponents.Skill;
            }
            return components;
        }

        internal static bool NeedsAutomaticCheck(UnityToolFreshnessRecord record, string installedIdentity, DateTime utcNow)
        {
            return NeedsAutomaticCheck(record, installedIdentity, null, null, utcNow);
        }

        static bool NeedsAutomaticCheck(
            UnityToolFreshnessRecord record,
            string installedIdentity,
            string expectedChannel,
            string expectedSourceKind,
            DateTime utcNow)
        {
            if (string.IsNullOrEmpty(installedIdentity) || record == null
                || !string.Equals(record.installedIdentity, installedIdentity, StringComparison.Ordinal)
                || (expectedChannel != null && !string.Equals(record.channel, expectedChannel, StringComparison.Ordinal))
                || (expectedSourceKind != null && !string.Equals(record.sourceKind, expectedSourceKind, StringComparison.Ordinal)))
            {
                return true;
            }

            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            if (TryReadUtc(record.lastFailureAtUtc, out var failedAt)
                && failedAt <= now
                && now - failedAt < UnityCliUpdateCheck.AutomaticFailureBackoff)
            {
                return false;
            }
            return !TryReadUtc(record.checkedAtUtc, out var checkedAt)
                || checkedAt > now
                || now - checkedAt >= UnityCliUpdateCheck.AutomaticCheckInterval;
        }

        internal static bool TryRestore(
            UnityToolFreshnessRecord record,
            string installedIdentity,
            DateTime utcNow,
            out ComponentUpdateStatus status)
        {
            status = null;
            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            if (record == null
                || string.IsNullOrEmpty(installedIdentity)
                || !string.Equals(record.installedIdentity, installedIdentity, StringComparison.Ordinal)
                || !TryReadUtc(record.checkedAtUtc, out var checkedAt)
                || checkedAt > now
                || !ValidField(record.installedVersion, 128)
                || !ValidField(record.availableVersion, 128)
                || !ValidField(record.channel, 32)
                || !ValidField(record.sourceKind, 64)
                || !Regex.IsMatch(record.sourceIdentitySha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                || (!ValidUpdateState(record.updateState)
                    && !(record.availabilityDisplayOnly && record.updateState == UnityCliUpdateCheck.UpdateStateNotInstalled)))
            {
                return false;
            }

            status = new ComponentUpdateStatus
            {
                CheckState = UnityCliUpdateCheck.CheckStateChecked,
                InstalledVersion = record.installedVersion ?? string.Empty,
                AvailableVersion = record.availableVersion ?? string.Empty,
                Channel = record.channel ?? string.Empty,
                SourceKind = record.sourceKind ?? string.Empty,
                SourceIdentitySha256 = record.sourceIdentitySha256 ?? string.Empty,
                UpdateState = record.updateState ?? UnityCliUpdateCheck.UpdateStateOutcomeUnknown,
                CheckedAtUtc = checkedAt,
                ExpiresAtUtc = DateTime.MinValue,
                LastErrorCode = ValidField(record.lastErrorCode, 64) ? record.lastErrorCode ?? string.Empty : string.Empty,
                LastErrorSummary = ValidField(record.lastErrorSummary, 256) ? record.lastErrorSummary ?? string.Empty : string.Empty,
                AuthoritativeForMutation = false,
            };
            return true;
        }

        internal static void Record(string projectRoot, UnityToolInstalledSet installed, UnityCliUpdateCheckSnapshot snapshot, DateTime utcNow)
        {
            if (string.IsNullOrWhiteSpace(projectRoot) || installed == null || snapshot == null)
            {
                return;
            }

            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
            var records = Load(projectRoot);
            records.windowsCli = RecordComponent(records.windowsCli, installed.WindowsCliInstalled, installed.WindowsCliIdentity, snapshot.CheckWindowsCli, snapshot.WindowsCli == null ? null : snapshot.WindowsCli.Update, now, snapshot.WindowsCliInstalledIdentity);
            records.pipeline = RecordComponent(records.pipeline, installed.PipelineInstalled, installed.PipelineIdentity, snapshot.CheckPipeline, snapshot.Pipeline == null ? null : snapshot.Pipeline.Update, now, snapshot.PipelineInstalledIdentity, snapshot.PipelineAvailabilityDisplayOnly);
            records.skill = RecordComponent(records.skill, installed.SkillInstalled, installed.SkillIdentity, snapshot.CheckSkill, snapshot.Skill == null ? null : snapshot.Skill.Update, now, snapshot.SkillInstalledIdentity, snapshot.SkillAvailabilityDisplayOnly);
            records.schemaVersion = SchemaVersion;
            try
            {
                var serialized = JsonUtility.ToJson(records);
                if (!string.IsNullOrEmpty(serialized) && serialized.Length <= MaxSerializedCharacters)
                {
                    EditorPrefs.SetString(Key(projectRoot), serialized);
                }
            }
            catch
            {
                // Freshness persistence is advisory and never changes the completed check result.
            }
        }

        internal static UnityToolFreshnessRecord RecordComponent(
            UnityToolFreshnessRecord record,
            bool installed,
            string installedIdentity,
            bool selected,
            ComponentUpdateStatus status,
            DateTime utcNow,
            string checkedInstalledIdentity = null,
            bool availabilityDisplayOnly = false)
        {
            record = record ?? new UnityToolFreshnessRecord();
            if (!selected || status == null)
            {
                return record;
            }
            if (!installed && !(availabilityDisplayOnly && !status.AuthoritativeForMutation
                && status.CheckState == UnityCliUpdateCheck.CheckStateChecked))
            {
                return new UnityToolFreshnessRecord();
            }
            if (string.Equals(status.CheckState, UnityCliUpdateCheck.CheckStateChecked, StringComparison.Ordinal))
            {
                if (!string.Equals(installedIdentity, checkedInstalledIdentity ?? installedIdentity, StringComparison.Ordinal))
                {
                    return new UnityToolFreshnessRecord
                    {
                        installedIdentity = installedIdentity ?? string.Empty,
                        lastFailureAtUtc = utcNow.ToString("O", CultureInfo.InvariantCulture),
                    };
                }
                return new UnityToolFreshnessRecord
                {
                    availabilityDisplayOnly = availabilityDisplayOnly,
                    installedIdentity = installedIdentity ?? string.Empty,
                    installedVersion = Bound(status.InstalledVersion, 128),
                    availableVersion = Bound(status.AvailableVersion, 128),
                    channel = Bound(status.Channel, 32),
                    sourceKind = Bound(status.SourceKind, 64),
                    sourceIdentitySha256 = Regex.IsMatch(status.SourceIdentitySha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant) ? status.SourceIdentitySha256 : string.Empty,
                    updateState = ValidUpdateState(status.UpdateState)
                        || (availabilityDisplayOnly && status.UpdateState == UnityCliUpdateCheck.UpdateStateNotInstalled)
                        ? status.UpdateState : UnityCliUpdateCheck.UpdateStateOutcomeUnknown,
                    checkedAtUtc = status.CheckedAtUtc.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                    lastErrorCode = Bound(status.LastErrorCode, 64),
                    lastErrorSummary = Bound(status.LastErrorSummary, 256),
                    lastFailureAtUtc = string.Empty,
                };
            }
            if (string.Equals(status.CheckState, UnityCliUpdateCheck.CheckStateFailed, StringComparison.Ordinal)
                || string.Equals(status.CheckState, UnityCliUpdateCheck.CheckStateSkippedNotInstalled, StringComparison.Ordinal))
            {
                if (!string.Equals(record.installedIdentity, installedIdentity, StringComparison.Ordinal)
                    || !string.Equals(record.channel, status.Channel, StringComparison.Ordinal)
                    || !string.Equals(record.sourceKind, status.SourceKind, StringComparison.Ordinal))
                {
                    record = new UnityToolFreshnessRecord();
                }
                record.installedIdentity = installedIdentity ?? string.Empty;
                record.channel = Bound(status.Channel, 32);
                record.sourceKind = Bound(status.SourceKind, 64);
                record.lastFailureAtUtc = utcNow.ToString("O", CultureInfo.InvariantCulture);
            }
            return record;
        }

        static UnityToolFreshnessRecords Normalize(UnityToolFreshnessRecords records)
        {
            records = records ?? new UnityToolFreshnessRecords();
            records.windowsCli = records.windowsCli ?? new UnityToolFreshnessRecord();
            records.pipeline = records.pipeline ?? new UnityToolFreshnessRecord();
            records.skill = records.skill ?? new UnityToolFreshnessRecord();
            return records;
        }

        static bool ValidUpdateState(string value)
        {
            return string.Equals(value, UnityCliUpdateCheck.UpdateStateCurrent, StringComparison.Ordinal)
                || string.Equals(value, UnityCliUpdateCheck.UpdateStateAvailable, StringComparison.Ordinal)
                || string.Equals(value, UnityCliUpdateCheck.UpdateStateUpstreamReview, StringComparison.Ordinal)
                || string.Equals(value, UnityCliUpdateCheck.UpdateStateLocalModified, StringComparison.Ordinal)
                || string.Equals(value, UnityCliUpdateCheck.UpdateStateUnsupported, StringComparison.Ordinal)
                || string.Equals(value, UnityCliUpdateCheck.UpdateStateOutcomeUnknown, StringComparison.Ordinal);
        }

        static bool ValidField(string value, int maxCharacters)
        {
            return value != null && value.Length <= maxCharacters && value.IndexOf('\0') < 0;
        }

        static string Bound(string value, int maxCharacters)
        {
            var safe = BoundedProcessOperation.Sanitize(value ?? string.Empty);
            return safe.Length <= maxCharacters ? safe : safe.Substring(0, maxCharacters);
        }

        static bool TryReadUtc(string value, out DateTime utc)
        {
            return DateTime.TryParseExact(
                value ?? string.Empty,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out utc);
        }

        static string Key(string projectRoot)
        {
            string normalized;
            try
            {
                normalized = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
            }
            catch
            {
                normalized = projectRoot ?? string.Empty;
            }
            return KeyPrefix + UnityCliUpdateCheck.SourceIdentity(normalized);
        }
    }

    internal sealed class ComponentUpdateStatus
    {
        public string CheckState = UnityCliUpdateCheck.CheckStateNotChecked;
        public string InstalledVersion = string.Empty;
        public string AvailableVersion = string.Empty;
        public string Channel = string.Empty;
        public string SourceKind = string.Empty;
        public string SourceIdentitySha256 = string.Empty;
        public string UpdateState = UnityCliUpdateCheck.UpdateStateNotChecked;
        public DateTime CheckedAtUtc = DateTime.MinValue;
        public DateTime ExpiresAtUtc = DateTime.MinValue;
        public string LastErrorCode = string.Empty;
        public string LastErrorSummary = string.Empty;
        public bool AuthoritativeForMutation;

        public bool IsExpired(DateTime utcNow)
        {
            return ExpiresAtUtc != DateTime.MinValue && utcNow.ToUniversalTime() >= ExpiresAtUtc;
        }

        internal static ComponentUpdateStatus NotChecked()
        {
            return new ComponentUpdateStatus();
        }

        internal static ComponentUpdateStatus NotChecked(
            string installedVersion,
            string channel,
            string sourceKind,
            string sourceIdentitySha256)
        {
            return new ComponentUpdateStatus
            {
                InstalledVersion = installedVersion ?? string.Empty,
                Channel = channel ?? string.Empty,
                SourceKind = sourceKind ?? string.Empty,
                SourceIdentitySha256 = sourceIdentitySha256 ?? string.Empty,
            };
        }

        internal static ComponentUpdateStatus Checking(string installedVersion, string channel, string sourceKind, string sourceIdentitySha256)
        {
            return new ComponentUpdateStatus
            {
                CheckState = UnityCliUpdateCheck.CheckStateChecking,
                InstalledVersion = installedVersion ?? string.Empty,
                Channel = channel ?? string.Empty,
                SourceKind = sourceKind ?? string.Empty,
                SourceIdentitySha256 = sourceIdentitySha256 ?? string.Empty,
                UpdateState = UnityCliUpdateCheck.UpdateStateChecking,
            };
        }

        internal static ComponentUpdateStatus NotInstalled(string channel, string sourceKind, string sourceIdentitySha256)
        {
            return new ComponentUpdateStatus
            {
                CheckState = UnityCliUpdateCheck.CheckStateSkippedNotInstalled,
                Channel = channel ?? string.Empty,
                SourceKind = sourceKind ?? string.Empty,
                SourceIdentitySha256 = sourceIdentitySha256 ?? string.Empty,
                UpdateState = UnityCliUpdateCheck.UpdateStateNotInstalled,
            };
        }

        internal static ComponentUpdateStatus Checked(
            string installedVersion,
            string availableVersion,
            string channel,
            string sourceKind,
            string sourceIdentitySha256,
            DateTime checkedAtUtc)
        {
            var checkedAt = checkedAtUtc.ToUniversalTime();
            return new ComponentUpdateStatus
            {
                CheckState = UnityCliUpdateCheck.CheckStateChecked,
                InstalledVersion = installedVersion ?? string.Empty,
                AvailableVersion = availableVersion ?? string.Empty,
                Channel = channel ?? string.Empty,
                SourceKind = sourceKind ?? string.Empty,
                SourceIdentitySha256 = sourceIdentitySha256 ?? string.Empty,
                UpdateState = UnityCliUpdateCheck.UpdateStateOutcomeUnknown,
                CheckedAtUtc = checkedAt,
                ExpiresAtUtc = checkedAt.Add(UnityCliUpdateCheck.CacheTtl),
                AuthoritativeForMutation = true,
            };
        }

        internal static ComponentUpdateStatus Failed(
            string installedVersion,
            string channel,
            string sourceKind,
            string sourceIdentitySha256,
            string errorCode,
            string errorSummary,
            DateTime checkedAtUtc)
        {
            var status = Checked(installedVersion, string.Empty, channel, sourceKind, sourceIdentitySha256, checkedAtUtc);
            status.CheckState = UnityCliUpdateCheck.CheckStateFailed;
            status.UpdateState = UnityCliUpdateCheck.UpdateStateCheckFailed;
            status.LastErrorCode = BoundErrorCode(errorCode);
            status.LastErrorSummary = BoundSummary(errorSummary);
            return status;
        }

        internal ComponentUpdateStatus Clone()
        {
            return new ComponentUpdateStatus
            {
                CheckState = CheckState,
                InstalledVersion = InstalledVersion,
                AvailableVersion = AvailableVersion,
                Channel = Channel,
                SourceKind = SourceKind,
                SourceIdentitySha256 = SourceIdentitySha256,
                UpdateState = UpdateState,
                CheckedAtUtc = CheckedAtUtc,
                ExpiresAtUtc = ExpiresAtUtc,
                LastErrorCode = LastErrorCode,
                LastErrorSummary = LastErrorSummary,
                AuthoritativeForMutation = AuthoritativeForMutation,
            };
        }

        static string BoundErrorCode(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 64 || !Regex.IsMatch(value, "^[a-z0-9_]+$", RegexOptions.CultureInvariant))
            {
                return "unknown_error";
            }

            return value;
        }

        static string BoundSummary(string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }

            var safe = BoundedProcessOperation.Sanitize(value);
            return safe.Length <= 256 ? safe : safe.Substring(0, 256);
        }
    }

    internal sealed class UnityCliUpdateCheckSnapshot
    {
        public UnityCliPipelineSetup.WindowsUnityCliStatus WindowsCli;
        public UnityCliPipelineSetup.UnityPipelineStatus Pipeline;
        public UnityCliProjectSkillInstaller.UnityCliSkillStatus Skill;
        public bool IsComplete;
        public bool IsCancelled;
        public bool CheckWindowsCli;
        public bool CheckPipeline;
        public bool CheckSkill;
        public bool CliTransportCandidate;
        public bool PipelineAvailabilityDisplayOnly;
        public bool PipelineAbsenceVerified;
        public bool SkillAvailabilityDisplayOnly;
        public bool SkillAbsenceVerified;
        public string WindowsCliInstalledIdentity = string.Empty;
        public string PipelineInstalledIdentity = string.Empty;
        public string SkillInstalledIdentity = string.Empty;

        internal UnityCliUpdateCheckSnapshot(
            UnityCliPipelineSetup.WindowsUnityCliStatus windowsCli,
            UnityCliPipelineSetup.UnityPipelineStatus pipeline,
            UnityCliProjectSkillInstaller.UnityCliSkillStatus skill)
        {
            WindowsCli = windowsCli;
            Pipeline = pipeline;
            Skill = skill;
            CheckWindowsCli = windowsCli != null && windowsCli.Update != null
                && string.Equals(windowsCli.Update.CheckState, UnityCliUpdateCheck.CheckStateChecking, StringComparison.Ordinal);
            CheckPipeline = pipeline != null && pipeline.Update != null
                && string.Equals(pipeline.Update.CheckState, UnityCliUpdateCheck.CheckStateChecking, StringComparison.Ordinal);
            CheckSkill = skill != null && skill.Update != null
                && string.Equals(skill.Update.CheckState, UnityCliUpdateCheck.CheckStateChecking, StringComparison.Ordinal);
            CliTransportCandidate = CheckWindowsCli || CheckPipeline;
        }

        internal static UnityCliUpdateCheckSnapshot NotChecked()
        {
            return new UnityCliUpdateCheckSnapshot(
                UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked(),
                new UnityCliPipelineSetup.UnityPipelineStatus
                {
                    EditorCompatibilityState = "unknown",
                    CliCompatibilityState = "unknown",
                    Update = ComponentUpdateStatus.NotChecked(),
                },
                new UnityCliProjectSkillInstaller.UnityCliSkillStatus
                {
                    LocalState = UnityCliProjectSkillInstaller.LocalStateAbsent,
                    Update = ComponentUpdateStatus.NotChecked(),
                });
        }
    }

    internal interface IUpdateCheckProcess : IDisposable
    {
        int ExitCode { get; }
        string StandardOutput { get; }
        string StandardError { get; }
        bool TimedOut { get; }
        bool TerminationConfirmed { get; }
        int ProcessId { get; }
        bool Poll();
    }

    internal interface IUpdateCheckProcessStarter
    {
        bool TryStart(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, out IUpdateCheckProcess operation, out string error);
    }

    internal sealed class UnityCliUpdateCheckCoordinator : IDisposable
    {
        enum CheckStage
        {
            CliSignature,
            CliVersion,
            CliMutationHelp,
            Cli,
            PipelineMutationHelp,
            Pipeline,
            Skill,
            Complete,
        }

        readonly UnityCliUpdateCheckSnapshot m_status;
        readonly IUpdateCheckProcessStarter m_processStarter;
        readonly Func<DateTime> m_nowUtc;
        readonly DateTime m_startedAtUtc;
        readonly TimeSpan m_totalTimeout;
        readonly string m_projectRoot;
        CheckStage m_stage = CheckStage.CliSignature;
        IUpdateCheckProcess m_process;
        bool m_disposed;
        bool m_cliVersionValidated;
        bool m_cliSignatureTrusted;
        bool m_cliMutationSupported;
        bool m_pipelineMutationSupported;

        internal UnityCliUpdateCheckCoordinator(
            UnityCliUpdateCheckSnapshot status,
            IUpdateCheckProcessStarter processStarter,
            Func<DateTime> nowUtc,
            TimeSpan totalTimeout)
        {
            m_status = status ?? UnityCliUpdateCheckSnapshot.NotChecked();
            m_processStarter = processStarter ?? new DefaultProcessStarter();
            m_nowUtc = nowUtc ?? (() => DateTime.UtcNow);
            m_totalTimeout = totalTimeout;
            m_startedAtUtc = UtcNow();
            m_projectRoot = string.Empty;
            StartNextStage();
        }

        internal UnityCliUpdateCheckCoordinator(
            string projectRoot,
            UnityCliUpdateCheckSnapshot status,
            IUpdateCheckProcessStarter processStarter,
            Func<DateTime> nowUtc,
            TimeSpan totalTimeout)
        {
            m_status = status ?? UnityCliUpdateCheckSnapshot.NotChecked();
            m_processStarter = processStarter ?? new DefaultProcessStarter();
            m_nowUtc = nowUtc ?? (() => DateTime.UtcNow);
            m_totalTimeout = totalTimeout;
            m_startedAtUtc = UtcNow();
            m_projectRoot = projectRoot ?? string.Empty;
            StartNextStage();
        }

        public UnityCliUpdateCheckSnapshot Status => m_status;
        public bool IsComplete => m_stage == CheckStage.Complete;
        public bool IsCancelled => m_status.IsCancelled;

        public bool Poll()
        {
            if (IsComplete)
            {
                CompleteCoordinator();
                return true;
            }

            if (UtcNow() - m_startedAtUtc > m_totalTimeout)
            {
                CancelInternal("check_deadline_exceeded", "The update check exceeded its bounded deadline.");
                return true;
            }

            if (m_process == null)
            {
                StartNextStage();
                return IsComplete;
            }

            bool completed;
            var pollFailed = false;
            try
            {
                completed = m_process.Poll();
            }
            catch
            {
                completed = true;
                pollFailed = true;
            }

            if (!completed)
            {
                return false;
            }

            if (m_process != null)
            {
                var process = m_process;
                m_process = null;
                try
                {
                    if (!IsComplete)
                    {
                        if (pollFailed)
                        {
                            if (!process.TerminationConfirmed)
                            {
                                WindowsCliUpdateCoordinator.TrackUnconfirmedProcess(process.ProcessId);
                            }
                            CancelInternal("process_poll_failed", "The update check process could not be polled or its termination could not be confirmed.");
                        }
                        else if (!process.TerminationConfirmed)
                        {
                            WindowsCliUpdateCoordinator.TrackUnconfirmedProcess(process.ProcessId);
                            CancelInternal("process_termination_unconfirmed", "The update check process termination could not be confirmed.");
                        }
                        else
                        {
                            HandleCompletedProcess(process);
                        }

                        if (!IsComplete)
                        {
                            m_stage = NextStage(m_stage);
                        }
                    }
                }
                finally
                {
                    DisposeProcess(process);
                }
            }

            if (m_stage == CheckStage.Complete)
            {
                CompleteCoordinator();
            }
            else
            {
                StartNextStage();
            }

            return IsComplete;
        }

        static CheckStage NextStage(CheckStage stage)
        {
            switch (stage)
            {
                case CheckStage.CliSignature: return CheckStage.CliVersion;
                case CheckStage.CliVersion: return CheckStage.CliMutationHelp;
                case CheckStage.CliMutationHelp: return CheckStage.Cli;
                case CheckStage.Cli: return CheckStage.PipelineMutationHelp;
                case CheckStage.PipelineMutationHelp: return CheckStage.Pipeline;
                case CheckStage.Pipeline: return CheckStage.Skill;
                case CheckStage.Skill: return CheckStage.Complete;
                default: return CheckStage.Complete;
            }
        }

        public void Cancel()
        {
            if (IsComplete)
            {
                return;
            }

            CancelInternal("check_cancelled", "The update check was cancelled.");
        }

        public void Dispose()
        {
            if (!m_disposed)
            {
                m_disposed = true;
                if (!IsComplete)
                {
                    CancelInternal("check_disposed", "The update check was disposed before completion.");
                }
            }
        }

        void StartNextStage()
        {
            while (!IsComplete && m_process == null)
            {
                switch (m_stage)
                {
                    case CheckStage.CliSignature:
                        StartCliSignatureCheck();
                        if (m_process == null) m_stage = CheckStage.CliVersion;
                        break;
                    case CheckStage.CliVersion:
                        StartCliVersionCheck();
                        if (m_process == null) m_stage = CheckStage.CliMutationHelp;
                        break;
                    case CheckStage.CliMutationHelp:
                        StartCliMutationHelpCheck();
                        if (m_process == null) m_stage = CheckStage.Cli;
                        break;
                    case CheckStage.Cli:
                        StartCliCheck();
                        if (m_process == null) m_stage = CheckStage.PipelineMutationHelp;
                        break;
                    case CheckStage.PipelineMutationHelp:
                        StartPipelineMutationHelpCheck();
                        if (m_process == null) m_stage = CheckStage.Pipeline;
                        break;
                    case CheckStage.Pipeline:
                        StartPipelineCheck();
                        if (m_process == null) m_stage = CheckStage.Skill;
                        break;
                    case CheckStage.Skill:
                        StartSkillCheck();
                        if (m_process == null) m_stage = CheckStage.Complete;
                        break;
                    default:
                        CompleteCoordinator();
                        break;
                }
            }

            if (m_stage == CheckStage.Complete && !m_status.IsComplete)
            {
                CompleteCoordinator();
            }
        }

        void StartCliCheck()
        {
            if (!m_status.CheckWindowsCli)
            {
                return;
            }
            if (!m_cliVersionValidated || !m_cliSignatureTrusted)
            {
                return;
            }

            StartTrustedCliProcess(
                CheckStage.Cli,
                new[] { "upgrade", "--check", "--channel", UnityCliUpdateCheck.CliChannel, "--format", "json", "--non-interactive", "--no-banner" },
                null,
                UnityCliUpdateCheck.ProcessTimeout);
        }

        void StartPipelineCheck()
        {
            if (!m_status.CheckPipeline)
            {
                return;
            }
            if (!m_cliSignatureTrusted)
            {
                FailPipeline("check_blocked_update_transport_unavailable", "The installed Pipeline could not be checked because a validated official Windows Unity CLI transport is unavailable.");
                return;
            }

            StartTrustedCliProcess(
                CheckStage.Pipeline,
                new[] { "pipeline", "list-versions", "--format", "json", "--non-interactive", "--no-banner" },
                m_projectRoot,
                UnityCliUpdateCheck.ProcessTimeout);
        }

        void StartCliMutationHelpCheck()
        {
            if (!m_status.CheckWindowsCli || !m_cliSignatureTrusted)
            {
                return;
            }

            StartTrustedCliProcess(
                CheckStage.CliMutationHelp,
                new[] { "upgrade", "--help", "--no-banner" },
                null,
                UnityCliUpdateCheck.LocalProcessTimeout);
        }

        void StartPipelineMutationHelpCheck()
        {
            if (!m_status.CheckPipeline || !m_cliSignatureTrusted)
            {
                return;
            }

            StartTrustedCliProcess(
                CheckStage.PipelineMutationHelp,
                new[] { "pipeline", "install", "--help", "--no-banner" },
                m_projectRoot,
                UnityCliUpdateCheck.LocalProcessTimeout);
        }

        void StartCliSignatureCheck()
        {
            if (!RequiresCliTransport())
            {
                return;
            }
            if (!m_status.CliTransportCandidate)
            {
                MarkCliNotInstalled("The exact official Windows Unity CLI executable is not installed.");
                BlockPipelineWithoutTransport();
                return;
            }
            if (!Environment.Is64BitProcess)
            {
                FailCliSignature("cli_signature_requires_64_bit_process", "Authenticode verification requires a 64-bit Windows Editor process.");
                return;
            }

            var powerShell = WindowsCliAuthenticode.PowerShellPath;
            StartProcess(
                CheckStage.CliSignature,
                powerShell,
                WindowsCliAuthenticode.BuildProbeArguments(UnityCliPipelineSetup.OfficialWindowsInstallPath),
                null,
                UnityCliUpdateCheck.ProcessTimeout);
        }

        void StartSkillCheck()
        {
            if (!m_status.CheckSkill)
            {
                return;
            }
            StartProcess(
                CheckStage.Skill,
                WindowsCliAuthenticode.PowerShellPath,
                UnityCliUpdateCheck.BuildSkillLatestCheckArguments(),
                null,
                UnityCliUpdateCheck.ProcessTimeout);
        }

        void StartCliVersionCheck()
        {
            if (!RequiresCliTransport())
            {
                return;
            }
            if (!m_cliSignatureTrusted)
            {
                return;
            }

            StartTrustedCliProcess(
                CheckStage.CliVersion,
                new[] { "--version", "--no-banner" },
                null,
                UnityCliUpdateCheck.LocalProcessTimeout);
        }

        bool RequiresCliTransport()
        {
            return m_status.CheckWindowsCli || m_status.CheckPipeline;
        }

        void StartProcess(CheckStage stage, string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
        {
            if (m_processStarter.TryStart(fileName, arguments, workingDirectory, timeout, out m_process, out _))
            {
                return;
            }

            switch (stage)
            {
                case CheckStage.CliVersion:
                    MarkCliNotInstalled("The exact official Windows Unity CLI executable did not pass bounded local version validation.");
                    BlockPipelineWithoutTransport();
                    break;
                case CheckStage.Cli:
                    FailCli("cli_process_start_failed", "The official Unity CLI update check process could not start.");
                    break;
                case CheckStage.CliSignature:
                    FailCliSignature("cli_signature_process_start_failed", "The exact CLI Authenticode verification process could not start.");
                    break;
                case CheckStage.CliMutationHelp:
                    m_cliMutationSupported = false;
                    break;
                case CheckStage.PipelineMutationHelp:
                    m_pipelineMutationSupported = false;
                    break;
                case CheckStage.Pipeline:
                    FailPipeline("pipeline_process_start_failed", "The official Pipeline version check process could not start.");
                    break;
                case CheckStage.Skill:
                    FailSkill("skill_process_start_failed", "The official skill remote check process could not start.");
                    break;
            }
        }

        void StartTrustedCliProcess(CheckStage stage, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout)
        {
            StartProcess(
                stage,
                WindowsCliAuthenticode.PowerShellPath,
                WindowsCliAuthenticode.BuildBoundCommandArguments(
                    UnityCliPipelineSetup.OfficialWindowsInstallPath,
                    m_status.WindowsCli.ExecutableSignatureThumbprint,
                    m_status.WindowsCli.ExecutableSha256,
                    arguments),
                workingDirectory,
                timeout);
        }

        void HandleCompletedProcess(IUpdateCheckProcess process)
        {
            if (process.ExitCode != 0)
            {
                switch (m_stage)
                {
                    case CheckStage.CliVersion:
                        MarkCliNotInstalled("The exact official Windows Unity CLI executable did not pass bounded local version validation.");
                        BlockPipelineWithoutTransport();
                        break;
                    case CheckStage.Cli:
                        CompleteProcessWithFailure("cli_process_failed", "The official Unity CLI update check failed.");
                        break;
                    case CheckStage.CliSignature:
                        CompleteProcessWithFailure("cli_signature_process_failed", "The exact CLI Authenticode verification failed.");
                        break;
                    case CheckStage.CliMutationHelp:
                        m_cliMutationSupported = false;
                        break;
                    case CheckStage.PipelineMutationHelp:
                        m_pipelineMutationSupported = false;
                        break;
                    case CheckStage.Pipeline:
                        CompleteProcessWithFailure("pipeline_process_failed", "The official Pipeline version check failed.");
                        break;
                    default:
                        CompleteProcessWithFailure("skill_process_failed", "The official skill remote check failed.");
                        break;
                }
                return;
            }

            switch (m_stage)
            {
                case CheckStage.CliVersion:
                    if (UnityCliPipelineSetup.TryExtractCliVersion(process.StandardOutput, out var localVersion))
                    {
                        CompleteCliVersion(localVersion);
                    }
                    else
                    {
                        MarkCliNotInstalled("The exact official Windows Unity CLI executable returned invalid local version metadata.");
                        BlockPipelineWithoutTransport();
                    }
                    break;
                case CheckStage.Cli:
                    if (UnityCliUpdateCheck.TryParseCliCheckResponse(process.StandardOutput, out var cli, out var cliError))
                    {
                        CompleteCli(cli);
                    }
                    else
                    {
                        FailCli(cliError, "The official Unity CLI returned an invalid update-check response.");
                    }

                    break;
                case CheckStage.CliSignature:
                    if (WindowsCliAuthenticode.TryParseProbeResponse(process.StandardOutput, out var signature, out var signatureError))
                    {
                        CompleteCliSignature(signature);
                    }
                    else
                    {
                        FailCliSignature(signatureError, "The exact CLI Authenticode result is invalid or not trusted for Unity.");
                    }

                    break;
                case CheckStage.CliMutationHelp:
                    m_cliMutationSupported = UnityCliUpdateCheck.TryValidateCliUpgradeHelp(process.StandardOutput);
                    break;
                case CheckStage.PipelineMutationHelp:
                    m_pipelineMutationSupported = UnityCliUpdateCheck.TryValidatePipelineInstallHelp(process.StandardOutput);
                    break;
                case CheckStage.Pipeline:
                    if (UnityCliUpdateCheck.TryParsePipelineListVersionsResponse(process.StandardOutput, out var pipeline, out var pipelineError))
                    {
                        CompletePipeline(pipeline);
                    }
                    else
                    {
                        FailPipeline(pipelineError, "The official Pipeline CLI returned an invalid version response.");
                    }

                    break;
                case CheckStage.Skill:
                    if (UnityCliUpdateCheck.TryParseSkillLatestCommitResponse(process.StandardOutput, out var commit, out var skillError))
                    {
                        UnityCliUpdateCheck.ApplySkillGitSignal(m_status.Skill, commit, UtcNow());
                        if (m_status.SkillAvailabilityDisplayOnly)
                        {
                            var update = m_status.Skill.Update;
                            update.AuthoritativeForMutation = false;
                            if (!m_status.SkillAbsenceVerified)
                            {
                                update.UpdateState = UnityCliUpdateCheck.UpdateStateUnsupported;
                                update.LastErrorCode = "skill_local_state_unverified";
                                update.LastErrorSummary = "Official availability is display-only; the local skill target is unknown, modified, incomplete or unsafe, not confirmed absent.";
                            }
                            else if (!m_status.Skill.UpstreamChangeDetected)
                            {
                                update.UpdateState = UnityCliUpdateCheck.UpdateStateNotInstalled;
                                update.LastErrorCode = "skill_not_installed";
                                update.LastErrorSummary = "The official latest skill is available but is not installed locally; installation still requires explicit confirmation.";
                            }
                        }
                    }
                    else
                    {
                        FailSkill(skillError, "The official skill path history returned an invalid commit response.");
                    }

                    break;
            }
        }

        void CompleteProcessWithFailure(string errorCode, string summary)
        {
            switch (m_stage)
            {
                case CheckStage.CliVersion:
                    MarkCliNotInstalled(summary);
                    BlockPipelineWithoutTransport();
                    break;
                case CheckStage.Cli: FailCli(errorCode, summary); break;
                case CheckStage.CliSignature: FailCliSignature(errorCode, summary); break;
                case CheckStage.Pipeline: FailPipeline(errorCode, summary); break;
                case CheckStage.Skill: FailSkill(errorCode, summary); break;
            }
        }

        void CompleteCli(CliCheckMetadata metadata)
        {
            if (!m_cliVersionValidated
                || !string.Equals(m_status.WindowsCli.Version, metadata.CurrentVersion, StringComparison.Ordinal))
            {
                FailCli("cli_current_version_changed", "The exact local Unity CLI version changed between local validation and the official update check.");
                return;
            }
            m_status.WindowsCli.Checked = true;
            m_status.WindowsCli.Installed = true;
            m_status.WindowsCli.ExecutablePath = UnityCliPipelineSetup.OfficialWindowsInstallPath;
            m_status.WindowsCli.Version = metadata.CurrentVersion;
            m_status.WindowsCli.Message = "Installed " + metadata.CurrentVersion;
            m_status.WindowsCli.Update = UnityCliUpdateCheck.BuildVersionUpdateStatus(
                metadata.CurrentVersion,
                metadata.LatestVersion,
                metadata.Channel,
                "official_unity_cli_upgrade_check",
                "unity.exe|upgrade|--check|" + metadata.Channel + "|" + metadata.Platform + "|" + metadata.CurrentVersion + "|" + metadata.LatestVersion,
                true,
                m_cliMutationSupported,
                UtcNow());
            if (!m_cliMutationSupported)
            {
                m_status.WindowsCli.Update.AuthoritativeForMutation = false;
                m_status.WindowsCli.Update.LastErrorCode = "cli_update_surface_unavailable";
                m_status.WindowsCli.Update.LastErrorSummary = "The installed CLI does not expose the exact reviewed target-update command surface.";
            }
            UnityCliPipelineSetup.SetWindowsStatus(m_status.WindowsCli);
        }

        void CompleteCliVersion(string version)
        {
            m_cliVersionValidated = true;
            m_status.WindowsCli.Checked = true;
            m_status.WindowsCli.Installed = true;
            m_status.WindowsCli.ExecutablePath = UnityCliPipelineSetup.OfficialWindowsInstallPath;
            m_status.WindowsCli.Version = version;
            m_status.WindowsCli.Message = "Installed " + version;
            if (m_status.WindowsCli.Update != null)
            {
                m_status.WindowsCli.Update.InstalledVersion = version;
            }
            UnityCliPipelineSetup.SetWindowsStatus(m_status.WindowsCli);
        }

        void CompleteCliSignature(WindowsCliSignature signature)
        {
            m_cliSignatureTrusted = true;
            m_status.WindowsCli.ExecutableSignatureState = signature.State;
            m_status.WindowsCli.ExecutableSigner = signature.Signer;
            m_status.WindowsCli.ExecutableSignatureThumbprint = signature.Thumbprint;
            m_status.WindowsCli.ExecutableSha256 = signature.FileSha256;
            UnityCliPipelineSetup.SetWindowsStatus(m_status.WindowsCli);
        }

        void FailCliSignature(string errorCode, string summary)
        {
            m_cliSignatureTrusted = false;
            m_status.WindowsCli.Installed = false;
            m_status.WindowsCli.ExecutablePath = string.Empty;
            m_status.WindowsCli.Message = summary;
            m_status.WindowsCli.ExecutableSignatureState = WindowsCliAuthenticode.InvalidState;
            m_status.WindowsCli.ExecutableSigner = string.Empty;
            m_status.WindowsCli.ExecutableSignatureThumbprint = string.Empty;
            m_status.WindowsCli.ExecutableSha256 = string.Empty;
            if (m_status.CheckWindowsCli)
            {
                var update = ComponentUpdateStatus.NotInstalled(
                    UnityCliUpdateCheck.CliChannel,
                    "official_unity_cli_upgrade_check",
                    UnityCliUpdateCheck.SourceIdentity("unity.exe|upgrade|--check|" + UnityCliUpdateCheck.CliChannel + "|" + UnityCliUpdateCheck.CliPlatform));
                update.LastErrorCode = errorCode;
                update.LastErrorSummary = summary;
                m_status.WindowsCli.Update = update;
            }
            UnityCliPipelineSetup.SetWindowsStatus(m_status.WindowsCli);
        }

        void MarkCliNotInstalled(string summary)
        {
            m_cliVersionValidated = false;
            m_cliSignatureTrusted = false;
            m_status.WindowsCli.Checked = true;
            m_status.WindowsCli.Installed = false;
            m_status.WindowsCli.ExecutablePath = string.Empty;
            m_status.WindowsCli.Version = string.Empty;
            m_status.WindowsCli.Message = summary;
            m_status.WindowsCli.ExecutableSignatureState = "unknown";
            m_status.WindowsCli.ExecutableSigner = string.Empty;
            m_status.WindowsCli.ExecutableSignatureThumbprint = string.Empty;
            m_status.WindowsCli.ExecutableSha256 = string.Empty;
            if (m_status.CheckWindowsCli)
            {
                m_status.WindowsCli.Update = ComponentUpdateStatus.NotInstalled(
                    UnityCliUpdateCheck.CliChannel,
                    "official_unity_cli_upgrade_check",
                    UnityCliUpdateCheck.SourceIdentity("unity.exe|upgrade|--check|" + UnityCliUpdateCheck.CliChannel + "|" + UnityCliUpdateCheck.CliPlatform));
            }
            UnityCliPipelineSetup.SetWindowsStatus(m_status.WindowsCli);
        }

        void BlockPipelineWithoutTransport()
        {
            if (m_status.CheckPipeline)
            {
                FailPipeline(
                    "check_blocked_update_transport_unavailable",
                    "The installed Pipeline could not be checked because a validated official Windows Unity CLI transport is unavailable.");
            }
        }

        void CompletePipeline(PipelineListVersionsMetadata metadata)
        {
            var versionsIdentity = UnityCliUpdateCheck.PipelineVersionsIdentity(metadata.Versions);
            var cliVersion = m_status.WindowsCli == null ? string.Empty : m_status.WindowsCli.Version;
            var editorVersion = Application.unityVersion;
            m_status.Pipeline.TargetPackageVersion = metadata.LatestVersion;
            m_status.Pipeline.AvailableVersionsIdentitySha256 = versionsIdentity;
            m_status.Pipeline.CheckedCliVersion = cliVersion;
            m_status.Pipeline.CheckedEditorVersion = editorVersion;
            m_status.Pipeline.CliCompatibilityState = m_cliVersionValidated && m_cliSignatureTrusted
                && m_pipelineMutationSupported ? "supported"
                : "unknown";
            m_status.Pipeline.Update = UnityCliUpdateCheck.BuildVersionUpdateStatus(
                m_status.Pipeline.Version,
                metadata.LatestVersion,
                "",
                "official_unity_pipeline_list_versions",
                UnityCliPipelineSetup.PipelinePackageId + "|pipeline list-versions|"
                    + m_status.Pipeline.Version + "|" + metadata.LatestVersion + "|" + versionsIdentity + "|" + cliVersion + "|" + editorVersion,
                m_status.Pipeline.Registered,
                m_status.Pipeline.EditorSupported && m_pipelineMutationSupported,
                UtcNow());

            if (m_status.PipelineAvailabilityDisplayOnly)
            {
                m_status.Pipeline.Update.AuthoritativeForMutation = false;
                m_status.Pipeline.Update.UpdateState = m_status.PipelineAbsenceVerified
                    ? UnityCliUpdateCheck.UpdateStateNotInstalled : UnityCliUpdateCheck.UpdateStateUnsupported;
                m_status.Pipeline.Update.LastErrorCode = m_status.PipelineAbsenceVerified
                    ? "pipeline_not_installed" : "pipeline_local_state_unverified";
                m_status.Pipeline.Update.LastErrorSummary = m_status.PipelineAbsenceVerified
                    ? "The official package version is available but Pipeline is not installed locally; this check does not authorize installation."
                    : "Official package availability is display-only; the local package state is unknown or unresolved, not confirmed absent.";
                return;
            }

            if (!m_pipelineMutationSupported)
            {
                m_status.Pipeline.Update.AuthoritativeForMutation = false;
                m_status.Pipeline.Update.LastErrorCode = "pipeline_update_surface_unavailable";
                m_status.Pipeline.Update.LastErrorSummary = "The installed CLI does not expose the exact reviewed project-scoped Pipeline update surface.";
            }

            if (!m_status.Pipeline.Registered && !string.IsNullOrEmpty(m_status.Pipeline.ManifestVersion))
            {
                m_status.Pipeline.Update.UpdateState = UnityCliUpdateCheck.UpdateStateOutcomeUnknown;
                m_status.Pipeline.Update.LastErrorCode = "pipeline_not_registered";
                m_status.Pipeline.Update.LastErrorSummary = "The package is declared in the manifest but is not registered yet.";
            }
        }

        void FailCli(string errorCode, string summary)
        {
            var installed = m_status.WindowsCli.Installed ? m_status.WindowsCli.Version : string.Empty;
            m_status.WindowsCli.Update = ComponentUpdateStatus.Failed(
                installed,
                UnityCliUpdateCheck.CliChannel,
                "official_unity_cli_upgrade_check",
                UnityCliUpdateCheck.SourceIdentity("unity.exe|upgrade|--check|" + UnityCliUpdateCheck.CliChannel + "|" + UnityCliUpdateCheck.CliPlatform),
                errorCode,
                summary,
                UtcNow());
        }

        void FailPipeline(string errorCode, string summary)
        {
            m_status.Pipeline.Update = ComponentUpdateStatus.Failed(
                m_status.Pipeline.Version,
                "",
                "official_unity_pipeline_list_versions",
                UnityCliUpdateCheck.SourceIdentity(UnityCliPipelineSetup.PipelinePackageId + "|pipeline list-versions"),
                errorCode,
                summary,
                UtcNow());
            if (m_status.PipelineAvailabilityDisplayOnly) m_status.Pipeline.Update.AuthoritativeForMutation = false;
        }

        void FailSkill(string errorCode, string summary)
        {
            m_status.Skill.Update = ComponentUpdateStatus.Failed(
                m_status.Skill.InstalledReviewedCommit,
                UnityCliUpdateCheck.SkillChannel,
                UnityCliUpdateCheck.SkillSourceKind,
                UnityCliUpdateCheck.SkillSourceBaseIdentity(),
                errorCode,
                summary,
                UtcNow());
            if (m_status.SkillAvailabilityDisplayOnly) m_status.Skill.Update.AuthoritativeForMutation = false;
        }

        void CancelInternal(string errorCode, string summary)
        {
            if (m_process != null)
            {
                var process = m_process;
                m_process = null;
                var processId = process.ProcessId;
                DisposeProcess(process);
                if (!process.TerminationConfirmed)
                {
                    WindowsCliUpdateCoordinator.TrackUnconfirmedProcess(processId);
                }
            }

            var checkedAt = UtcNow();
            if (m_status.CheckWindowsCli) m_status.WindowsCli.Update = CancelledStatus(m_status.WindowsCli.Update, errorCode, summary, checkedAt);
            if (m_status.CheckPipeline) m_status.Pipeline.Update = CancelledStatus(m_status.Pipeline.Update, errorCode, summary, checkedAt);
            if (m_status.CheckSkill) m_status.Skill.Update = CancelledStatus(m_status.Skill.Update, errorCode, summary, checkedAt);
            m_status.IsCancelled = true;
            m_stage = CheckStage.Complete;
            CompleteCoordinator();
        }

        void CompleteCoordinator()
        {
            if (m_status.IsComplete)
            {
                return;
            }

            m_status.IsComplete = true;
            UnityCliUpdateCheck.Publish(m_status);
        }

        DateTime UtcNow()
        {
            var value = m_nowUtc();
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        static ComponentUpdateStatus CancelledStatus(ComponentUpdateStatus existing, string errorCode, string summary, DateTime checkedAt)
        {
            var status = existing == null ? ComponentUpdateStatus.NotChecked() : existing.Clone();
            status.CheckState = UnityCliUpdateCheck.CheckStateCancelled;
            status.UpdateState = UnityCliUpdateCheck.UpdateStateOutcomeUnknown;
            status.CheckedAtUtc = checkedAt.ToUniversalTime();
            status.ExpiresAtUtc = status.CheckedAtUtc.Add(UnityCliUpdateCheck.CacheTtl);
            status.LastErrorCode = errorCode;
            status.LastErrorSummary = summary;
            return status;
        }

        static void DisposeProcess(IUpdateCheckProcess process)
        {
            try
            {
                process?.Dispose();
            }
            catch
            {
                // Cancellation remains fail-closed; local state is rechecked on the next manual action.
            }
        }

        sealed class DefaultProcessStarter : IUpdateCheckProcessStarter
        {
            public bool TryStart(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, out IUpdateCheckProcess operation, out string error)
            {
                operation = null;
                if (!BoundedProcessOperation.TryStart(fileName, arguments, workingDirectory, timeout, out var bounded, out error))
                {
                    return false;
                }

                operation = new DefaultProcess(bounded);
                return true;
            }
        }

        sealed class DefaultProcess : IUpdateCheckProcess
        {
            readonly BoundedProcessOperation m_operation;

            public DefaultProcess(BoundedProcessOperation operation)
            {
                m_operation = operation;
            }

            public int ExitCode => m_operation.ExitCode;
            public string StandardOutput => m_operation.StandardOutput;
            public string StandardError => m_operation.StandardError;
            public bool TimedOut => m_operation.TimedOut;
            public bool TerminationConfirmed => m_operation.TerminationConfirmed;
            public int ProcessId => m_operation.ProcessId;
            public bool Poll() => m_operation.Poll();
            public void Dispose() => m_operation.Dispose();
        }
    }

    internal sealed class CliCheckMetadata
    {
        public string CurrentVersion = string.Empty;
        public string LatestVersion = string.Empty;
        public string Platform = string.Empty;
        public string Channel = string.Empty;
    }

    internal sealed class PipelineListVersionsMetadata
    {
        public string PackageId = string.Empty;
        public string LatestVersion = string.Empty;
        public string[] Versions = new string[0];
    }

    internal struct UnityCliSemVersion : IComparable<UnityCliSemVersion>
    {
        readonly string m_major;
        readonly string m_minor;
        readonly string m_patch;
        readonly string[] m_prerelease;

        internal UnityCliSemVersion(string major, string minor, string patch, string[] prerelease, string original)
        {
            m_major = major;
            m_minor = minor;
            m_patch = patch;
            m_prerelease = prerelease ?? new string[0];
            Original = original ?? string.Empty;
        }

        public string Original { get; }

        public int CompareTo(UnityCliSemVersion other)
        {
            var result = CompareNumeric(m_major, other.m_major);
            if (result != 0) return result;
            result = CompareNumeric(m_minor, other.m_minor);
            if (result != 0) return result;
            result = CompareNumeric(m_patch, other.m_patch);
            if (result != 0) return result;

            var thisIsRelease = m_prerelease == null || m_prerelease.Length == 0;
            var otherIsRelease = other.m_prerelease == null || other.m_prerelease.Length == 0;
            if (thisIsRelease && otherIsRelease) return 0;
            if (thisIsRelease) return 1;
            if (otherIsRelease) return -1;

            var count = Math.Min(m_prerelease.Length, other.m_prerelease.Length);
            for (var index = 0; index < count; index++)
            {
                var left = m_prerelease[index];
                var right = other.m_prerelease[index];
                if (UnityCliUpdateCheck.IsNumericIdentifier(left) && UnityCliUpdateCheck.IsNumericIdentifier(right))
                {
                    result = CompareNumeric(left, right);
                }
                else if (UnityCliUpdateCheck.IsNumericIdentifier(left))
                {
                    result = -1;
                }
                else if (UnityCliUpdateCheck.IsNumericIdentifier(right))
                {
                    result = 1;
                }
                else
                {
                    result = string.CompareOrdinal(left, right);
                }

                if (result != 0) return result;
            }

            return m_prerelease.Length.CompareTo(other.m_prerelease.Length);
        }

        static int CompareNumeric(string left, string right)
        {
            left = left.TrimStart('0');
            right = right.TrimStart('0');
            if (left.Length == 0) left = "0";
            if (right.Length == 0) right = "0";
            if (left.Length != right.Length) return left.Length.CompareTo(right.Length);
            return string.CompareOrdinal(left, right);
        }
    }

}
