using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using SGG.UnityMCP.Editor.Commands;
using SGG.UnityMCP.Editor.Paths;
using UnityEditor;
using UnityEditor.Compilation;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    internal sealed class UnityPipelineUpdateBinding
    {
        public string InstalledVersion = string.Empty;
        public string TargetVersion = string.Empty;
        public string SourceIdentitySha256 = string.Empty;
        public string VersionsIdentitySha256 = string.Empty;
        public string BindingIdentitySha256 = string.Empty;
        public string CanonicalProjectRoot = string.Empty;
        public string WindowsProjectRoot = string.Empty;
        public string CliVersion = string.Empty;
        public string CliExecutablePath = string.Empty;
        public string CliSignatureThumbprint = string.Empty;
        public string CliExecutableSha256 = string.Empty;
        public string EditorVersion = string.Empty;
        public string PreManifestSha256 = string.Empty;
        public long PreManifestLength;
        public bool PreLockExists;
        public string PreLockSha256 = string.Empty;
        public long PreLockLength;
        public string PreLockVersion = string.Empty;
        public string PreRegisteredSource = string.Empty;
        public DateTime CheckedAtUtc = DateTime.MinValue;
        public DateTime ExpiresAtUtc = DateTime.MinValue;
    }

    internal sealed class UnityPipelineLocalEvidence
    {
        public string ManifestVersion = string.Empty;
        public string RegisteredVersion = string.Empty;
        public string RegisteredSource = string.Empty;
        public string ManifestSha256 = string.Empty;
        public long ManifestLength;
        public bool LockExists;
        public string LockSha256 = string.Empty;
        public long LockLength;
        public string LockVersion = string.Empty;
    }

    internal sealed class UnityPipelineUpdateResult
    {
        public string UpdateState = UnityCliUpdateCheck.UpdateStateOutcomeUnknown;
        public string ErrorCode = string.Empty;
        public string Summary = string.Empty;
        public bool MutationStarted;
        public bool MutationExitSucceeded;
        public bool Success;
    }

    [Serializable]
    internal sealed class UnityPipelinePersistedOperation
    {
        public int schemaVersion = 1;
        public string operationId = string.Empty;
        public string phase = string.Empty;
        public string updateState = UnityCliUpdateCheck.UpdateStateNotChecked;
        public string errorCode = string.Empty;
        public string summary = string.Empty;
        public string installedVersion = string.Empty;
        public string targetVersion = string.Empty;
        public string sourceIdentitySha256 = string.Empty;
        public string versionsIdentitySha256 = string.Empty;
        public string bindingIdentitySha256 = string.Empty;
        public string canonicalProjectRoot = string.Empty;
        public string windowsProjectRoot = string.Empty;
        public string cliVersion = string.Empty;
        public string cliExecutablePath = string.Empty;
        public string cliSignatureThumbprint = string.Empty;
        public string cliExecutableSha256 = string.Empty;
        public string editorVersion = string.Empty;
        public string preManifestSha256 = string.Empty;
        public long preManifestLength;
        public bool preLockExists;
        public string preLockSha256 = string.Empty;
        public long preLockLength;
        public string preLockVersion = string.Empty;
        public string preRegisteredSource = string.Empty;
        public long checkedAtUtcTicks;
        public long expiresAtUtcTicks;
        public long startedAtUtcTicks;
        public long updatedAtUtcTicks;
        public int dispatchCount;
        public bool mutationStarted;
        public bool mutationExitSucceeded;
        public bool terminationConfirmed;
        public bool noReplay = true;
        public bool localRefreshCompleted;
        public string deliveryCertainty = "not_dispatched";
        public int processId;
        public string compileBarrierOperationId = string.Empty;
        public string actualManifestVersion = string.Empty;
        public string actualRegisteredVersion = string.Empty;
        public string actualRegisteredSource = string.Empty;
        public string actualLockVersion = string.Empty;

        internal string Serialize()
        {
            return JsonUtility.ToJson(this);
        }

        internal static bool TryParse(string value, out UnityPipelinePersistedOperation state)
        {
            state = null;
            if (string.IsNullOrEmpty(value) || value.Length > UnityPipelineUpdateOperation.MaxPersistedStateCharacters)
            {
                return false;
            }
            try
            {
                state = JsonUtility.FromJson<UnityPipelinePersistedOperation>(value);
            }
            catch
            {
                return false;
            }
            return state != null && state.IsValid();
        }

        internal bool IsValid()
        {
            return schemaVersion == 1
                && Regex.IsMatch(operationId ?? string.Empty, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant)
                && UnityPipelineUpdateOperation.IsKnownPhase(phase)
                && UnityPipelineUpdateOperation.IsKnownUpdateState(updateState)
                && UnityCliUpdateCheck.TryParseSemVer(installedVersion, out _)
                && UnityCliUpdateCheck.TryParseSemVer(targetVersion, out _)
                && IsSha256(sourceIdentitySha256)
                && IsSha256(versionsIdentitySha256)
                && IsSha256(bindingIdentitySha256)
                && string.Equals(bindingIdentitySha256, UnityPipelineUpdateOperation.ComputeBindingIdentity(this), StringComparison.Ordinal)
                && IsBoundedPath(canonicalProjectRoot)
                && IsBoundedPath(windowsProjectRoot)
                && UnityCliUpdateCheck.TryParseSemVer(cliVersion, out _)
                && IsBoundedPath(cliExecutablePath)
                && Regex.IsMatch(cliSignatureThumbprint ?? string.Empty, "^[0-9A-F]{40}$", RegexOptions.CultureInvariant)
                && IsSha256(cliExecutableSha256)
                && !string.IsNullOrEmpty(editorVersion) && editorVersion.Length <= 64
                && IsSha256(preManifestSha256)
                && preManifestLength >= 0 && preManifestLength <= 4L * 1024L * 1024L
                && (!preLockExists || IsSha256(preLockSha256))
                && preLockLength >= 0 && preLockLength <= 4L * 1024L * 1024L
                && (!preLockExists || UnityCliUpdateCheck.TryParseSemVer(preLockVersion, out _))
                && string.Equals(preRegisteredSource, "Registry", StringComparison.Ordinal)
                && IsValidTicks(checkedAtUtcTicks)
                && IsValidTicks(expiresAtUtcTicks)
                && expiresAtUtcTicks > checkedAtUtcTicks
                && IsValidTicks(startedAtUtcTicks)
                && IsValidTicks(updatedAtUtcTicks)
                && dispatchCount >= 0 && dispatchCount <= 1
                && processId >= 0
                && noReplay
                && IsKnownDelivery(deliveryCertainty)
                && IsOptionalVersion(actualManifestVersion)
                && IsOptionalVersion(actualRegisteredVersion)
                && (string.IsNullOrEmpty(actualRegisteredSource) || actualRegisteredSource.Length <= 64)
                && IsOptionalVersion(actualLockVersion)
                && (string.IsNullOrEmpty(compileBarrierOperationId)
                    || Regex.IsMatch(compileBarrierOperationId, "^[0-9a-f]{32}$", RegexOptions.CultureInvariant))
                && IsBoundedError(errorCode, 64)
                && IsBoundedError(summary, 512)
                && HasConsistentPhase();
        }

        static bool IsSha256(string value)
        {
            return Regex.IsMatch(value ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        }

        static bool IsBoundedPath(string value)
        {
            return !string.IsNullOrEmpty(value) && value.Length <= 1024 && value.IndexOf('\0') < 0;
        }

        static bool IsValidTicks(long value)
        {
            return value > DateTime.MinValue.Ticks && value <= DateTime.MaxValue.Ticks;
        }

        static bool IsBoundedError(string value, int maxCharacters)
        {
            return value != null && value.Length <= maxCharacters && value.IndexOf('\0') < 0;
        }

        static bool IsKnownDelivery(string value)
        {
            return string.Equals(value, "not_dispatched", StringComparison.Ordinal)
                || string.Equals(value, "dispatch_pending", StringComparison.Ordinal)
                || string.Equals(value, "dispatched_once", StringComparison.Ordinal)
                || string.Equals(value, "delivered_response", StringComparison.Ordinal)
                || string.Equals(value, "uncertain", StringComparison.Ordinal);
        }

        static bool IsOptionalVersion(string value)
        {
            return string.IsNullOrEmpty(value) || UnityCliUpdateCheck.TryParseSemVer(value, out _);
        }

        bool HasConsistentPhase()
        {
            if (string.Equals(phase, UnityPipelineUpdateOperation.PhasePrepared, StringComparison.Ordinal))
            {
                return dispatchCount == 0 && !mutationStarted && !mutationExitSucceeded && processId == 0
                    && string.Equals(deliveryCertainty, "not_dispatched", StringComparison.Ordinal)
                    && string.Equals(updateState, UnityCliUpdateCheck.UpdateStateInProgress, StringComparison.Ordinal);
            }
            if (string.Equals(phase, UnityPipelineUpdateOperation.PhaseDispatching, StringComparison.Ordinal))
            {
                return dispatchCount == 1 && !mutationStarted && !mutationExitSucceeded && processId == 0
                    && string.Equals(deliveryCertainty, "dispatch_pending", StringComparison.Ordinal)
                    && string.Equals(updateState, UnityCliUpdateCheck.UpdateStateInProgress, StringComparison.Ordinal);
            }
            if (string.Equals(phase, UnityPipelineUpdateOperation.PhaseMutationRunning, StringComparison.Ordinal))
            {
                return dispatchCount == 1 && mutationStarted && !mutationExitSucceeded && !terminationConfirmed && processId > 0
                    && string.Equals(deliveryCertainty, "dispatched_once", StringComparison.Ordinal)
                    && string.Equals(updateState, UnityCliUpdateCheck.UpdateStateInProgress, StringComparison.Ordinal);
            }
            if (string.Equals(phase, UnityPipelineUpdateOperation.PhaseAwaitingPackage, StringComparison.Ordinal)
                || string.Equals(phase, UnityPipelineUpdateOperation.PhaseCompilationArming, StringComparison.Ordinal)
                || string.Equals(phase, UnityPipelineUpdateOperation.PhaseCompilationRequested, StringComparison.Ordinal))
            {
                return dispatchCount == 1 && mutationStarted && mutationExitSucceeded && terminationConfirmed && processId == 0
                    && string.Equals(deliveryCertainty, "delivered_response", StringComparison.Ordinal)
                    && string.Equals(updateState, UnityCliUpdateCheck.UpdateStateInProgress, StringComparison.Ordinal);
            }
            return string.Equals(phase, UnityPipelineUpdateOperation.PhaseComplete, StringComparison.Ordinal)
                && !string.Equals(updateState, UnityCliUpdateCheck.UpdateStateInProgress, StringComparison.Ordinal)
                && (!mutationStarted || dispatchCount == 1);
        }
    }

    [InitializeOnLoad]
    internal static class UnityPipelineUpdateOperation
    {
        internal const int MaxPersistedStateCharacters = 12288;
        internal const string PhasePrepared = "prepared";
        internal const string PhaseDispatching = "dispatching";
        internal const string PhaseMutationRunning = "mutation_running";
        internal const string PhaseAwaitingPackage = "awaiting_package";
        internal const string PhaseCompilationArming = "compilation_arming";
        internal const string PhaseCompilationRequested = "compilation_requested";
        internal const string PhaseComplete = "complete";
        const string SessionStateKey = "SGG.UnityMCP.PipelineUpdate.Operation";
        const string OfficialSourceKind = "official_unity_pipeline_list_versions";
        const string RegistrySource = "Registry";
        static readonly TimeSpan RecoveryTimeout = TimeSpan.FromMinutes(10);

        static UnityPipelineUpdateOperation()
        {
            EditorApplication.delayCall += ResumePersistedVerification;
        }

        internal static bool HasActiveOperation
        {
            get
            {
                if (!TryLoadState(out var state, out _)) return false;
                return IsActivePhase(state.phase);
            }
        }

        internal static bool RequiresLocalRefresh
        {
            get
            {
                var raw = SessionState.GetString(SessionStateKey, string.Empty);
                if (string.IsNullOrEmpty(raw)) return false;
                if (!UnityPipelinePersistedOperation.TryParse(raw, out var state)) return true;
                return !state.localRefreshCompleted
                    && (string.Equals(state.updateState, UnityCliUpdateCheck.UpdateStateOutcomeUnknown, StringComparison.Ordinal)
                        || string.Equals(state.updateState, UnityCliUpdateCheck.UpdateStateVerificationRequired, StringComparison.Ordinal));
            }
        }

        internal static bool TryCreateBinding(
            UnityCliUpdateCheckSnapshot snapshot,
            UnityCliPipelineSetup.UnityPipelineStatus localPipeline,
            UnityCliPipelineSetup.WindowsUnityCliStatus localCli,
            string projectRoot,
            string expectedProjectRoot,
            DateTime utcNow,
            out UnityPipelineUpdateBinding binding,
            out string error)
        {
            binding = null;
            error = "Run a fresh manual update check before updating Pipeline.";
            if (HasActiveOperation || RequiresLocalRefresh)
            {
                error = "Refresh Local State before another Pipeline update after an incomplete or unverified operation.";
                return false;
            }
            if (Application.platform != RuntimePlatform.WindowsEditor || !Environment.Is64BitProcess)
            {
                error = "Pipeline update requires a 64-bit Windows Editor process.";
                return false;
            }
            if (!EditorReadyForMutation())
            {
                error = "Pipeline update is blocked while Unity is compiling, updating packages/assets, or in Play Mode.";
                return false;
            }
            if (snapshot == null || !snapshot.IsComplete || snapshot.IsCancelled || snapshot.Pipeline == null || snapshot.WindowsCli == null)
            {
                return false;
            }

            var checkedPipeline = snapshot.Pipeline;
            var update = checkedPipeline.Update;
            var now = AsUtc(utcNow);
            if (update == null
                || !string.Equals(update.CheckState, UnityCliUpdateCheck.CheckStateChecked, StringComparison.Ordinal)
                || !update.AuthoritativeForMutation
                || !string.Equals(update.UpdateState, UnityCliUpdateCheck.UpdateStateAvailable, StringComparison.Ordinal)
                || update.CheckedAtUtc == DateTime.MinValue
                || update.ExpiresAtUtc == DateTime.MinValue
                || update.CheckedAtUtc > now
                || update.ExpiresAtUtc <= update.CheckedAtUtc
                || update.ExpiresAtUtc - update.CheckedAtUtc > UnityCliUpdateCheck.CacheTtl
                || update.IsExpired(now))
            {
                error = "The manual Pipeline check is missing, expired, or does not prove an available update.";
                return false;
            }

            if (!checkedPipeline.EditorSupported
                || !string.Equals(checkedPipeline.EditorCompatibilityState, "supported", StringComparison.Ordinal)
                || !string.Equals(checkedPipeline.CliCompatibilityState, "supported", StringComparison.Ordinal)
                || !checkedPipeline.Registered
                || !string.Equals(update.SourceKind, OfficialSourceKind, StringComparison.Ordinal)
                || !string.Equals(update.InstalledVersion, checkedPipeline.Version, StringComparison.Ordinal)
                || !string.Equals(update.AvailableVersion, checkedPipeline.TargetPackageVersion, StringComparison.Ordinal)
                || !UnityCliUpdateCheck.TryCompareSemVer(update.InstalledVersion, update.AvailableVersion, out var comparison)
                || comparison >= 0
                || !Regex.IsMatch(checkedPipeline.AvailableVersionsIdentitySha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant)
                || !string.Equals(checkedPipeline.CheckedEditorVersion, Application.unityVersion, StringComparison.Ordinal)
                || !string.Equals(checkedPipeline.CheckedCliVersion, snapshot.WindowsCli.Version, StringComparison.Ordinal)
                || !string.Equals(update.SourceIdentitySha256, UnityCliUpdateCheck.PipelineSourceIdentity(
                    update.InstalledVersion,
                    update.AvailableVersion,
                    checkedPipeline.AvailableVersionsIdentitySha256,
                    checkedPipeline.CheckedCliVersion,
                    checkedPipeline.CheckedEditorVersion), StringComparison.Ordinal))
            {
                error = "The checked Pipeline target is not an exact newer compatible official version.";
                return false;
            }

            localPipeline = localPipeline ?? new UnityCliPipelineSetup.UnityPipelineStatus();
            localCli = localCli ?? UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked();
            if (!localPipeline.EditorSupported
                || !localPipeline.Registered
                || !string.Equals(localPipeline.Version, update.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(localPipeline.ManifestVersion, update.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(localPipeline.Source, RegistrySource, StringComparison.Ordinal)
                || !string.Equals(checkedPipeline.ManifestVersion, update.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(checkedPipeline.Source, RegistrySource, StringComparison.Ordinal))
            {
                error = "The registered Pipeline package, manifest declaration, or official Registry source changed after the manual check.";
                return false;
            }
            if (!WindowsCliUpdateCoordinator.HasTrustedSignature(snapshot.WindowsCli)
                || !WindowsCliUpdateCoordinator.HasTrustedSignature(localCli)
                || !string.Equals(localCli.Version, checkedPipeline.CheckedCliVersion, StringComparison.Ordinal)
                || !SamePath(localCli.ExecutablePath, UnityCliPipelineSetup.OfficialWindowsInstallPath)
                || !string.Equals(snapshot.WindowsCli.ExecutableSignatureThumbprint, localCli.ExecutableSignatureThumbprint, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(snapshot.WindowsCli.ExecutableSha256, localCli.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "The exact official Windows CLI identity changed after the manual Pipeline check.";
                return false;
            }
            if (!TryResolveProjectRoots(projectRoot, expectedProjectRoot, out var canonicalRoot, out var windowsRoot, out error)
                || !TryCaptureLocalEvidence(windowsRoot, localPipeline, out var evidence, out error))
            {
                return false;
            }

            binding = new UnityPipelineUpdateBinding
            {
                InstalledVersion = update.InstalledVersion,
                TargetVersion = update.AvailableVersion,
                SourceIdentitySha256 = update.SourceIdentitySha256,
                VersionsIdentitySha256 = checkedPipeline.AvailableVersionsIdentitySha256,
                CanonicalProjectRoot = canonicalRoot,
                WindowsProjectRoot = windowsRoot,
                CliVersion = checkedPipeline.CheckedCliVersion,
                CliExecutablePath = UnityCliPipelineSetup.OfficialWindowsInstallPath,
                CliSignatureThumbprint = localCli.ExecutableSignatureThumbprint,
                CliExecutableSha256 = localCli.ExecutableSha256,
                EditorVersion = checkedPipeline.CheckedEditorVersion,
                PreManifestSha256 = evidence.ManifestSha256,
                PreManifestLength = evidence.ManifestLength,
                PreLockExists = evidence.LockExists,
                PreLockSha256 = evidence.LockSha256,
                PreLockLength = evidence.LockLength,
                PreLockVersion = evidence.LockVersion,
                PreRegisteredSource = evidence.RegisteredSource,
                CheckedAtUtc = update.CheckedAtUtc,
                ExpiresAtUtc = update.ExpiresAtUtc,
            };
            binding.BindingIdentitySha256 = ComputeBindingIdentity(binding);
            error = string.Empty;
            return true;
        }

        internal static IReadOnlyList<string> BuildMutationArguments(UnityPipelineUpdateBinding binding)
        {
            binding = binding ?? new UnityPipelineUpdateBinding();
            return new[]
            {
                "pipeline", "install", "--project-path", binding.WindowsProjectRoot,
                "--package-version", binding.TargetVersion,
                "--format", "json", "--non-interactive", "--no-banner",
            };
        }

        internal static string ComputeBindingIdentity(UnityPipelineUpdateBinding binding)
        {
            binding = binding ?? new UnityPipelineUpdateBinding();
            return UnityCliUpdateCheck.SourceIdentity(
                binding.InstalledVersion + "|" + binding.TargetVersion + "|" + binding.SourceIdentitySha256 + "|"
                + binding.VersionsIdentitySha256 + "|" + binding.CanonicalProjectRoot + "|" + binding.WindowsProjectRoot + "|"
                + binding.CliVersion + "|" + binding.CliExecutablePath + "|" + binding.CliSignatureThumbprint + "|" + binding.CliExecutableSha256 + "|"
                + binding.EditorVersion + "|" + binding.PreManifestSha256 + "|" + binding.PreManifestLength + "|"
                + binding.PreLockExists + "|" + binding.PreLockSha256 + "|" + binding.PreLockLength + "|"
                + binding.PreLockVersion + "|" + binding.CheckedAtUtc.Ticks);
        }

        internal static string ComputeBindingIdentity(UnityPipelinePersistedOperation state)
        {
            if (state == null) return string.Empty;
            return ComputeBindingIdentity(new UnityPipelineUpdateBinding
            {
                InstalledVersion = state.installedVersion,
                TargetVersion = state.targetVersion,
                SourceIdentitySha256 = state.sourceIdentitySha256,
                VersionsIdentitySha256 = state.versionsIdentitySha256,
                CanonicalProjectRoot = state.canonicalProjectRoot,
                WindowsProjectRoot = state.windowsProjectRoot,
                CliVersion = state.cliVersion,
                CliExecutablePath = state.cliExecutablePath,
                CliSignatureThumbprint = state.cliSignatureThumbprint,
                CliExecutableSha256 = state.cliExecutableSha256,
                EditorVersion = state.editorVersion,
                PreManifestSha256 = state.preManifestSha256,
                PreManifestLength = state.preManifestLength,
                PreLockExists = state.preLockExists,
                PreLockSha256 = state.preLockSha256,
                PreLockLength = state.preLockLength,
                PreLockVersion = state.preLockVersion,
                CheckedAtUtc = new DateTime(state.checkedAtUtcTicks, DateTimeKind.Utc),
            });
        }

        internal static IReadOnlyList<string> BuildTrackedMutationArguments(UnityPipelineUpdateBinding binding)
        {
            binding = binding ?? new UnityPipelineUpdateBinding();
            var module = PowerShellLiteral(WindowsCliAuthenticode.SecurityModulePath);
            var cli = PowerShellLiteral(binding.CliExecutablePath);
            var project = PowerShellLiteral(binding.WindowsProjectRoot);
            var target = PowerShellLiteral(binding.TargetVersion);
            var thumbprint = PowerShellLiteral(binding.CliSignatureThumbprint);
            var cliSha = PowerShellLiteral(binding.CliExecutableSha256);
            var manifestSha = PowerShellLiteral(binding.PreManifestSha256);
            var lockSha = PowerShellLiteral(binding.PreLockSha256);
            var commandLine = PowerShellLiteral(BoundedProcessOperation.BuildArguments(BuildMutationArguments(binding)));
            return new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$ErrorActionPreference='Stop';"
                    + "$module='" + module + "';$cli='" + cli + "';$project='" + project + "';$target='" + target + "';"
                    + "$expectedThumbprint='" + thumbprint + "';$expectedCliSha='" + cliSha + "';"
                    + "$expectedManifestSha='" + manifestSha + "';$expectedManifestLength=" + binding.PreManifestLength.ToString(CultureInfo.InvariantCulture) + ";"
                    + "$expectedLockExists=$" + (binding.PreLockExists ? "true" : "false") + ";$expectedLockSha='" + lockSha + "';"
                    + "$expectedLockLength=" + binding.PreLockLength.ToString(CultureInfo.InvariantCulture) + ";"
                    + "function Assert-BoundDirectory([string]$path){$item=Get-Item -LiteralPath $path -Force;"
                    + "if(-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'bound project directory is unsafe'}};"
                    + "function Assert-NoReparsePath([string]$candidate){$current=[IO.Path]::GetFullPath($candidate);while(-not [string]::IsNullOrEmpty($current)){$item=Get-Item -LiteralPath $current -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'bound path contains a reparse point'};$parent=[IO.Path]::GetDirectoryName($current);if([string]::IsNullOrEmpty($parent) -or $parent -ceq $current){break};$current=$parent}};"
                    + "Import-Module -Name $module -Force;Assert-NoReparsePath $project;Assert-BoundDirectory $project;$packages=Join-Path $project 'Packages';Assert-NoReparsePath $packages;Assert-BoundDirectory $packages;"
                    + "Assert-NoReparsePath $cli;"
                    + "$cliStream=[IO.File]::Open($cli,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);"
                    + "$manifestStream=$null;$lockStream=$null;try{$signature=Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath $cli;if($null -eq $signature.SignerCertificate){throw 'signature unavailable'};"
                    + "$cliStream.Position=0;$actualCliSha=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($cliStream)).Replace('-','');"
                    + "if([string]$signature.Status -cne 'Valid' -or [string]$signature.SignerCertificate.Thumbprint -cne $expectedThumbprint -or $actualCliSha -cne $expectedCliSha.ToUpperInvariant()){throw 'bound executable identity changed'};"
                    + "$manifest=Join-Path $packages 'manifest.json';$manifestItem=Get-Item -LiteralPath $manifest -Force;if($manifestItem.PSIsContainer -or ($manifestItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'bound manifest is unsafe'};"
                    + "$manifestStream=[IO.File]::Open($manifest,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);if($manifestStream.Length -ne $expectedManifestLength){throw 'bound manifest length changed'};"
                    + "$actualManifestSha=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($manifestStream)).Replace('-','');if($actualManifestSha -cne $expectedManifestSha.ToUpperInvariant()){throw 'bound manifest hash changed'};"
                    + "$lock=Join-Path $packages 'packages-lock.json';if($expectedLockExists){$lockItem=Get-Item -LiteralPath $lock -Force;if($lockItem.PSIsContainer -or ($lockItem.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'bound package lock is unsafe'};"
                    + "$lockStream=[IO.File]::Open($lock,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);if($lockStream.Length -ne $expectedLockLength){throw 'bound package lock length changed'};"
                    + "$actualLockSha=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($lockStream)).Replace('-','');if($actualLockSha -cne $expectedLockSha.ToUpperInvariant()){throw 'bound package lock hash changed'}}elseif(Test-Path -LiteralPath $lock){throw 'bound package lock appeared'};"
                    + "$arguments='" + commandLine + "';"
                    + "$process=Start-Process -FilePath $cli -ArgumentList $arguments -WorkingDirectory $project -NoNewWindow -PassThru}finally{if($null -ne $lockStream){$lockStream.Dispose()};if($null -ne $manifestStream){$manifestStream.Dispose()};$cliStream.Dispose()};"
                    + "$process.WaitForExit();exit $process.ExitCode",
            };
        }

        internal static bool TryCaptureLocalEvidence(
            string projectRoot,
            UnityCliPipelineSetup.UnityPipelineStatus status,
            out UnityPipelineLocalEvidence evidence,
            out string error)
        {
            evidence = null;
            error = "Pipeline project package evidence could not be captured safely.";
            if (status == null || !status.Registered
                || !UnityCliUpdateCheck.TryParseSemVer(status.Version, out _)
                || !UnityCliUpdateCheck.TryParseSemVer(status.ManifestVersion, out _)
                || !string.Equals(status.Source, RegistrySource, StringComparison.Ordinal))
            {
                return false;
            }
            if (!TryReadBoundProjectFile(projectRoot, "Packages/manifest.json", true, out var manifestContent, out var manifestExists, out var manifestLength, out var manifestSha)
                || !manifestExists
                || !PipelineJsonEvidenceParser.TryReadStringPath(manifestContent, new[] { "dependencies", UnityCliPipelineSetup.PipelinePackageId }, out var manifestVersion)
                || !string.Equals(manifestVersion, status.ManifestVersion, StringComparison.Ordinal)
                || !TryReadBoundProjectFile(projectRoot, "Packages/packages-lock.json", false, out var lockContent, out var lockExists, out var lockLength, out var lockSha))
            {
                return false;
            }
            var lockVersion = string.Empty;
            if (lockExists
                && (!PipelineJsonEvidenceParser.TryReadStringPath(lockContent, new[] { "dependencies", UnityCliPipelineSetup.PipelinePackageId, "version" }, out lockVersion)
                    || !string.Equals(lockVersion, status.Version, StringComparison.Ordinal)))
            {
                return false;
            }

            evidence = new UnityPipelineLocalEvidence
            {
                ManifestVersion = manifestVersion,
                RegisteredVersion = status.Version,
                RegisteredSource = status.Source,
                ManifestSha256 = manifestSha,
                ManifestLength = manifestLength,
                LockExists = lockExists,
                LockSha256 = lockSha,
                LockLength = lockLength,
                LockVersion = lockVersion,
            };
            error = string.Empty;
            return true;
        }

        internal static string EvaluateVerification(
            UnityCliPipelineSetup.UnityPipelineStatus status,
            string targetVersion,
            bool barrierVerified,
            bool postReloadObserved,
            bool terminalCompileFailureWithoutReload,
            bool editorReady,
            bool hasCompileErrors)
        {
            if (terminalCompileFailureWithoutReload)
            {
                return "compilation_failed";
            }
            if (status == null || !status.Registered)
            {
                return "wait";
            }
            if (!string.Equals(status.ManifestVersion, targetVersion, StringComparison.Ordinal)
                || !string.Equals(status.Version, targetVersion, StringComparison.Ordinal)
                || !string.Equals(status.Source, RegistrySource, StringComparison.Ordinal))
            {
                return "verification_required";
            }
            if (!barrierVerified || !postReloadObserved || !editorReady)
            {
                return "wait";
            }
            return hasCompileErrors ? "verification_required" : "current";
        }

        internal static ComponentUpdateStatus EffectiveStatus(ComponentUpdateStatus fallback, string projectRoot)
        {
            var raw = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return fallback ?? ComponentUpdateStatus.NotChecked();
            if (!UnityPipelinePersistedOperation.TryParse(raw, out var state))
            {
                var malformed = (fallback ?? ComponentUpdateStatus.NotChecked()).Clone();
                malformed.UpdateState = UnityCliUpdateCheck.UpdateStateOutcomeUnknown;
                malformed.LastErrorCode = "pipeline_update_state_invalid";
                malformed.LastErrorSummary = "Persisted Pipeline update state is invalid. Restart the Editor before another update action.";
                return malformed;
            }
            if (string.Equals(state.phase, PhaseComplete, StringComparison.Ordinal)
                && fallback != null
                && string.Equals(fallback.CheckState, UnityCliUpdateCheck.CheckStateChecked, StringComparison.Ordinal)
                && fallback.CheckedAtUtc != DateTime.MinValue
                && fallback.CheckedAtUtc.Ticks > state.updatedAtUtcTicks)
            {
                return fallback;
            }
            if (string.Equals(state.updateState, UnityCliUpdateCheck.UpdateStateCurrent, StringComparison.Ordinal))
            {
                if (!TryRebindPersistedProject(state, projectRoot, out _))
                {
                    state.localRefreshCompleted = false;
                    Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_project_binding_changed", "The persisted Pipeline result no longer belongs to the exact current project.", DateTime.UtcNow);
                }
                else
                {
                    var local = UnityCliPipelineSetup.GetPipelineStatus(state.windowsProjectRoot);
                    var verifiedLockVersion = state.actualLockVersion;
                    RecordActualState(state, local);
                    if (!string.Equals(local.ManifestVersion, state.targetVersion, StringComparison.Ordinal)
                        || !string.Equals(local.Version, state.targetVersion, StringComparison.Ordinal)
                        || !string.Equals(local.Source, RegistrySource, StringComparison.Ordinal)
                        || !TryCaptureLocalEvidence(state.windowsProjectRoot, local, out var evidence, out _)
                        || !string.Equals(evidence.LockVersion, verifiedLockVersion, StringComparison.Ordinal))
                    {
                        state.localRefreshCompleted = false;
                        Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_terminal_state_drifted", "The local Pipeline package drifted after the previously verified update.", DateTime.UtcNow);
                    }
                }
            }

            var status = (fallback ?? ComponentUpdateStatus.NotChecked()).Clone();
            status.CheckState = UnityCliUpdateCheck.CheckStateChecked;
            status.InstalledVersion = string.Equals(state.updateState, UnityCliUpdateCheck.UpdateStateCurrent, StringComparison.Ordinal)
                ? state.targetVersion
                : state.installedVersion;
            status.AvailableVersion = state.targetVersion;
            status.SourceKind = OfficialSourceKind;
            status.SourceIdentitySha256 = state.sourceIdentitySha256;
            status.UpdateState = state.updateState;
            status.CheckedAtUtc = new DateTime(state.checkedAtUtcTicks, DateTimeKind.Utc);
            status.ExpiresAtUtc = new DateTime(state.expiresAtUtcTicks, DateTimeKind.Utc);
            status.LastErrorCode = state.errorCode;
            status.LastErrorSummary = state.summary;
            return status;
        }

        internal static bool CanRefreshLocalState(out string error)
        {
            error = string.Empty;
            var raw = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return true;
            if (!UnityPipelinePersistedOperation.TryParse(raw, out var state))
            {
                error = "Persisted Pipeline update state is invalid. Restart the Editor before another update action.";
                return false;
            }
            if (IsActivePhase(state.phase))
            {
                error = "Pipeline update verification is still active. Wait for it to complete before refreshing local state.";
                return false;
            }
            if (state.processId > 0 && IsProcessAlive(state.processId))
            {
                error = "The tracked Pipeline updater process is still alive. Wait for it to exit before refreshing local state.";
                return false;
            }
            return true;
        }

        internal static bool TryRefreshLocalState(string projectRoot, out string summary)
        {
            summary = "No persisted Pipeline update recovery was present.";
            var raw = SessionState.GetString(SessionStateKey, string.Empty);
            if (string.IsNullOrEmpty(raw)) return true;
            if (!CanRefreshLocalState(out summary)) return false;
            if (!TryLoadState(out var state, out _) || !TryRebindPersistedProject(state, projectRoot, out summary)) return false;

            var local = UnityCliPipelineSetup.GetPipelineStatus(state.windowsProjectRoot);
            RecordActualState(state, local);
            state.localRefreshCompleted = true;
            state.processId = 0;
            state.terminationConfirmed = true;
            var barrierVerified = false;
            var postReloadObserved = false;
            var terminalCompileFailureWithoutReload = false;
            if (!string.IsNullOrEmpty(state.compileBarrierOperationId))
            {
                var barrier = ExplicitCompilationBarrier.Snapshot(state.compileBarrierOperationId, true);
                barrierVerified = barrier.Verified;
                postReloadObserved = barrier.PostReloadObserved;
                terminalCompileFailureWithoutReload = barrier.TerminalCompileFailureWithoutReload;
            }
            var editorReady = !EditorApplication.isCompiling && !EditorApplication.isUpdating
                && !EditorApplication.isPlayingOrWillChangePlaymode
                && string.Equals(UnityBridgeRuntime.Status, "ready", StringComparison.Ordinal);
            var decision = EvaluateVerification(local, state.targetVersion, barrierVerified, postReloadObserved, terminalCompileFailureWithoutReload, editorReady, ConsoleLogBuffer.HasCompileErrors());
            if (string.Equals(decision, "current", StringComparison.Ordinal)
                && TryCaptureLocalEvidence(state.windowsProjectRoot, local, out _, out _))
            {
                summary = "Pipeline local refresh confirmed the exact target and retained clean post-reload compile proof.";
                Finish(state, UnityCliUpdateCheck.UpdateStateCurrent, string.Empty, summary, DateTime.UtcNow);
            }
            else
            {
                summary = "Pipeline local refresh recorded actual manifest=" + SafeVersion(local.ManifestVersion)
                    + ", registered=" + SafeVersion(local.Version) + ", source=" + Bound(local.Source, 64)
                    + ". A fresh manual update check is required before any new mutation.";
                Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_local_state_refreshed", summary, DateTime.UtcNow);
            }
            return true;
        }

        internal static void ResetForTests()
        {
            EditorApplication.update -= PollPersistedVerification;
            SessionState.EraseString(SessionStateKey);
        }

        internal static string PersistedStateForTests()
        {
            return SessionState.GetString(SessionStateKey, string.Empty);
        }

        internal static bool TryParseStateForTests(string value, out UnityPipelinePersistedOperation state)
        {
            return UnityPipelinePersistedOperation.TryParse(value, out state);
        }

        internal static void SchedulePersistedVerification()
        {
            EditorApplication.update -= PollPersistedVerification;
            EditorApplication.update += PollPersistedVerification;
        }

        internal static bool FinishInterruptedPreparedStateForTests(UnityPipelinePersistedOperation state, DateTime utcNow)
        {
            return TryFinishInterruptedPreparedState(state, utcNow);
        }

        internal static void Persist(UnityPipelinePersistedOperation state)
        {
            if (state == null || !state.IsValid())
            {
                SessionState.SetString(SessionStateKey, "invalid_pipeline_update_state");
                return;
            }
            SessionState.SetString(SessionStateKey, state.Serialize());
        }

        internal static UnityPipelinePersistedOperation CreateState(UnityPipelineUpdateBinding binding, DateTime startedAtUtc)
        {
            var started = AsUtc(startedAtUtc);
            return new UnityPipelinePersistedOperation
            {
                operationId = Guid.NewGuid().ToString("N"),
                phase = PhasePrepared,
                updateState = UnityCliUpdateCheck.UpdateStateInProgress,
                installedVersion = binding.InstalledVersion,
                targetVersion = binding.TargetVersion,
                sourceIdentitySha256 = binding.SourceIdentitySha256,
                versionsIdentitySha256 = binding.VersionsIdentitySha256,
                bindingIdentitySha256 = binding.BindingIdentitySha256,
                canonicalProjectRoot = binding.CanonicalProjectRoot,
                windowsProjectRoot = binding.WindowsProjectRoot,
                cliVersion = binding.CliVersion,
                cliExecutablePath = binding.CliExecutablePath,
                cliSignatureThumbprint = binding.CliSignatureThumbprint,
                cliExecutableSha256 = binding.CliExecutableSha256,
                editorVersion = binding.EditorVersion,
                preManifestSha256 = binding.PreManifestSha256,
                preManifestLength = binding.PreManifestLength,
                preLockExists = binding.PreLockExists,
                preLockSha256 = binding.PreLockSha256,
                preLockLength = binding.PreLockLength,
                preLockVersion = binding.PreLockVersion,
                preRegisteredSource = binding.PreRegisteredSource,
                checkedAtUtcTicks = binding.CheckedAtUtc.Ticks,
                expiresAtUtcTicks = binding.ExpiresAtUtc.Ticks,
                startedAtUtcTicks = started.Ticks,
                updatedAtUtcTicks = started.Ticks,
            };
        }

        internal static void Finish(UnityPipelinePersistedOperation state, string updateState, string errorCode, string summary, DateTime utcNow)
        {
            if (state == null) return;
            state.phase = PhaseComplete;
            state.updateState = updateState;
            state.errorCode = Bound(errorCode, 64);
            state.summary = Bound(summary, 512);
            state.updatedAtUtcTicks = AsUtc(utcNow).Ticks;
            if (string.Equals(updateState, UnityCliUpdateCheck.UpdateStateCurrent, StringComparison.Ordinal))
            {
                state.processId = 0;
            }
            Persist(state);
        }

        internal static bool EditorReadyForMutation()
        {
            return !EditorApplication.isCompiling
                && !EditorApplication.isUpdating
                && !EditorApplication.isPlaying
                && !EditorApplication.isPlayingOrWillChangePlaymode;
        }

        internal static bool ValidateProjectBindingForDispatch(UnityPipelineUpdateBinding binding, out string error)
        {
            error = "The exact current project path or reparse-point state changed before Pipeline dispatch.";
            if (binding == null
                || !TryResolveProjectRoots(ProjectPaths.ProjectRoot, ProjectPaths.ProjectRoot, out var canonicalRoot, out var windowsRoot, out _)
                || !string.Equals(canonicalRoot, binding.CanonicalProjectRoot, StringComparison.Ordinal)
                || !SamePath(windowsRoot, binding.WindowsProjectRoot)
                || !string.Equals(binding.BindingIdentitySha256, ComputeBindingIdentity(binding), StringComparison.Ordinal))
            {
                return false;
            }
            error = string.Empty;
            return true;
        }

        internal static bool IsKnownPhase(string phase)
        {
            return string.Equals(phase, PhasePrepared, StringComparison.Ordinal)
                || string.Equals(phase, PhaseDispatching, StringComparison.Ordinal)
                || string.Equals(phase, PhaseMutationRunning, StringComparison.Ordinal)
                || string.Equals(phase, PhaseAwaitingPackage, StringComparison.Ordinal)
                || string.Equals(phase, PhaseCompilationArming, StringComparison.Ordinal)
                || string.Equals(phase, PhaseCompilationRequested, StringComparison.Ordinal)
                || string.Equals(phase, PhaseComplete, StringComparison.Ordinal);
        }

        internal static bool IsKnownUpdateState(string state)
        {
            return string.Equals(state, UnityCliUpdateCheck.UpdateStateNotChecked, StringComparison.Ordinal)
                || string.Equals(state, UnityCliUpdateCheck.UpdateStateInProgress, StringComparison.Ordinal)
                || string.Equals(state, UnityCliUpdateCheck.UpdateStateCurrent, StringComparison.Ordinal)
                || string.Equals(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, StringComparison.Ordinal)
                || string.Equals(state, UnityCliUpdateCheck.UpdateStateOutcomeUnknown, StringComparison.Ordinal);
        }

        static bool IsActivePhase(string phase)
        {
            return string.Equals(phase, PhasePrepared, StringComparison.Ordinal)
                || string.Equals(phase, PhaseDispatching, StringComparison.Ordinal)
                || string.Equals(phase, PhaseMutationRunning, StringComparison.Ordinal)
                || string.Equals(phase, PhaseAwaitingPackage, StringComparison.Ordinal)
                || string.Equals(phase, PhaseCompilationArming, StringComparison.Ordinal)
                || string.Equals(phase, PhaseCompilationRequested, StringComparison.Ordinal);
        }

        static void ResumePersistedVerification()
        {
            if (!TryLoadState(out var state, out var malformed))
            {
                if (malformed) EditorApplication.update -= PollPersistedVerification;
                return;
            }
            if (!TryRebindPersistedProject(state, ProjectPaths.ProjectRoot, out _))
            {
                state.localRefreshCompleted = false;
                Finish(state, UnityCliUpdateCheck.UpdateStateOutcomeUnknown, "pipeline_update_project_binding_changed", "Persisted Pipeline operation does not bind the exact current project. Restart or inspect local state manually; no replay is allowed.", DateTime.UtcNow);
                return;
            }
            if (TryFinishInterruptedPreparedState(state, DateTime.UtcNow)) return;
            if (string.Equals(state.phase, PhaseMutationRunning, StringComparison.Ordinal)
                || string.Equals(state.phase, PhaseDispatching, StringComparison.Ordinal))
            {
                state.deliveryCertainty = "uncertain";
                state.localRefreshCompleted = false;
                RecordActualState(state, UnityCliPipelineSetup.GetPipelineStatus(state.windowsProjectRoot));
                Finish(state, UnityCliUpdateCheck.UpdateStateOutcomeUnknown, "pipeline_update_reload_during_dispatch", "Assembly reload interrupted tracked Pipeline mutation delivery. Refresh local state; do not repeat automatically.", DateTime.UtcNow);
                return;
            }
            if (IsActivePhase(state.phase)) SchedulePersistedVerification();
        }

        static bool TryFinishInterruptedPreparedState(UnityPipelinePersistedOperation state, DateTime utcNow)
        {
            if (state == null || !string.Equals(state.phase, PhasePrepared, StringComparison.Ordinal)) return false;
            state.localRefreshCompleted = true;
            Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_preflight_interrupted", "Assembly reload interrupted the read-only Pipeline preflight before mutation dispatch; no replay is needed.", utcNow);
            return true;
        }

        static void PollPersistedVerification()
        {
            if (!TryLoadState(out var state, out _))
            {
                EditorApplication.update -= PollPersistedVerification;
                return;
            }
            if (!IsActivePhase(state.phase))
            {
                EditorApplication.update -= PollPersistedVerification;
                return;
            }
            if (!TryRebindPersistedProject(state, ProjectPaths.ProjectRoot, out _))
            {
                state.localRefreshCompleted = false;
                Finish(state, UnityCliUpdateCheck.UpdateStateOutcomeUnknown, "pipeline_update_project_binding_changed", "Persisted Pipeline operation does not bind the exact current project. No verification or replay was performed.", DateTime.UtcNow);
                EditorApplication.update -= PollPersistedVerification;
                return;
            }
            if (DateTime.UtcNow - new DateTime(state.startedAtUtcTicks, DateTimeKind.Utc) > RecoveryTimeout)
            {
                RecordActualState(state, UnityCliPipelineSetup.GetPipelineStatus(state.windowsProjectRoot));
                Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_verification_deadline_exceeded", "Pipeline update verification exceeded its bounded deadline. Refresh local state; no automatic repeat is allowed.", DateTime.UtcNow);
                EditorApplication.update -= PollPersistedVerification;
                return;
            }
            if (EditorApplication.isCompiling || EditorApplication.isUpdating || EditorApplication.isPlayingOrWillChangePlaymode)
            {
                return;
            }

            if (string.Equals(state.phase, PhaseAwaitingPackage, StringComparison.Ordinal))
            {
                var status = UnityCliPipelineSetup.GetPipelineStatus(state.windowsProjectRoot);
                if (!status.Registered
                    || (!string.Equals(status.Version, state.targetVersion, StringComparison.Ordinal)
                        && string.Equals(status.Version, state.installedVersion, StringComparison.Ordinal)))
                {
                    return;
                }
                if (!string.Equals(status.ManifestVersion, state.targetVersion, StringComparison.Ordinal)
                    || !string.Equals(status.Version, state.targetVersion, StringComparison.Ordinal)
                    || !string.Equals(status.Source, RegistrySource, StringComparison.Ordinal))
                {
                    RecordActualState(state, status);
                    Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_package_postcondition_failed", "Pipeline manifest, registered package version, or Registry source did not match the exact target.", DateTime.UtcNow);
                    EditorApplication.update -= PollPersistedVerification;
                    return;
                }
                BeginCompilationVerification(state);
                return;
            }

            if (string.Equals(state.phase, PhaseCompilationArming, StringComparison.Ordinal))
            {
                Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compile_arming_interrupted", "Pipeline compile verification was interrupted before one request was proven. Refresh local state; no automatic request replay is allowed.", DateTime.UtcNow);
                EditorApplication.update -= PollPersistedVerification;
                return;
            }

            if (!string.Equals(state.phase, PhaseCompilationRequested, StringComparison.Ordinal)) return;
            var barrier = ExplicitCompilationBarrier.Snapshot(state.compileBarrierOperationId, true);
            if (barrier.Unavailable)
            {
                RecordActualState(state, UnityCliPipelineSetup.GetPipelineStatus(state.windowsProjectRoot));
                Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compile_barrier_failed", "The explicit Pipeline compile barrier failed or became unavailable.", DateTime.UtcNow);
                EditorApplication.update -= PollPersistedVerification;
                return;
            }
            var local = UnityCliPipelineSetup.GetPipelineStatus(state.windowsProjectRoot);
            RecordActualState(state, local);
            var editorReady = !EditorApplication.isCompiling && !EditorApplication.isUpdating
                && string.Equals(UnityBridgeRuntime.Status, "ready", StringComparison.Ordinal);
            var decision = EvaluateVerification(local, state.targetVersion, barrier.Verified, barrier.PostReloadObserved, barrier.TerminalCompileFailureWithoutReload, editorReady, ConsoleLogBuffer.HasCompileErrors());
            if (string.Equals(decision, "wait", StringComparison.Ordinal)) return;
            if (string.Equals(decision, "compilation_failed", StringComparison.Ordinal))
            {
                Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compilation_failed", "Pipeline compilation finished with exact correlated compiler errors; no update or compile operation was repeated.", DateTime.UtcNow);
            }
            else if (string.Equals(decision, "current", StringComparison.Ordinal)
                && TryCaptureLocalEvidence(state.windowsProjectRoot, local, out _, out _))
            {
                Finish(state, UnityCliUpdateCheck.UpdateStateCurrent, string.Empty, "Pipeline updated to the exact target with manifest, registered Registry package, package resolution, and clean post-reload compile proof.", DateTime.UtcNow);
            }
            else
            {
                Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compile_postcondition_failed", "Pipeline did not prove the exact registered target and a clean post-reload compilation.", DateTime.UtcNow);
            }
            EditorApplication.update -= PollPersistedVerification;
        }

        static void BeginCompilationVerification(UnityPipelinePersistedOperation state)
        {
            try
            {
                ConsoleLogBuffer.Initialize();
                var begin = ExplicitCompilationBarrier.Begin(state.operationId);
                if (!begin.Success)
                {
                    Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compile_barrier_begin_failed", "Could not create the exact Pipeline compile barrier.", DateTime.UtcNow);
                    EditorApplication.update -= PollPersistedVerification;
                    return;
                }
                state.compileBarrierOperationId = begin.State.OperationId;
                state.phase = PhaseCompilationArming;
                state.updatedAtUtcTicks = DateTime.UtcNow.Ticks;
                Persist(state);
                var armed = ExplicitCompilationBarrier.MarkRequestIssued(state.compileBarrierOperationId);
                if (!armed.Success)
                {
                    Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compile_barrier_arm_failed", "Could not arm the exact Pipeline compile barrier.", DateTime.UtcNow);
                    EditorApplication.update -= PollPersistedVerification;
                    return;
                }
                state.phase = PhaseCompilationRequested;
                state.updatedAtUtcTicks = DateTime.UtcNow.Ticks;
                Persist(state);
                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                var barrierAfterRefresh = ExplicitCompilationBarrier.Snapshot(state.compileBarrierOperationId, true);
                if (!BuiltInCommands.IsMatchingArmedCompileBarrier(barrierAfterRefresh, state.compileBarrierOperationId))
                {
                    Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compile_barrier_invalid_after_refresh", "The exact Pipeline compile barrier became invalid during AssetDatabase.Refresh.", DateTime.UtcNow);
                    EditorApplication.update -= PollPersistedVerification;
                    return;
                }
                if (BuiltInCommands.ShouldRequestCompilationAfterRefresh(barrierAfterRefresh, state.compileBarrierOperationId, EditorApplication.isCompiling))
                {
                    CompilationPipeline.RequestScriptCompilation();
                }
            }
            catch
            {
                Finish(state, UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_compile_request_failed", "The exact Pipeline compile verification request could not be issued.", DateTime.UtcNow);
                EditorApplication.update -= PollPersistedVerification;
            }
        }

        static bool TryResolveProjectRoots(string projectRoot, string expectedProjectRoot, out string canonicalRoot, out string windowsRoot, out string error)
        {
            canonicalRoot = string.Empty;
            windowsRoot = string.Empty;
            error = "The exact current Unity project root could not be validated.";
            try
            {
                var paths = new CanonicalPathService();
                canonicalRoot = paths.NormalizeProjectRoot(projectRoot).Value;
                var expected = paths.NormalizeProjectRoot(expectedProjectRoot).Value;
                if (!string.Equals(canonicalRoot, expected, StringComparison.Ordinal) || !Directory.Exists(projectRoot)) return false;
                var aliases = paths.ProjectRootAliases(projectRoot);
                for (var index = 0; index < aliases.Length; index++)
                {
                    if (string.Equals(aliases[index].environment, "windows", StringComparison.Ordinal))
                    {
                        windowsRoot = aliases[index].host_path;
                        break;
                    }
                }
                if (string.IsNullOrEmpty(windowsRoot)) windowsRoot = Path.GetFullPath(projectRoot);
                if (!Directory.Exists(windowsRoot)
                    || !File.Exists(Path.Combine(windowsRoot, "Packages", "manifest.json"))
                    || (File.GetAttributes(windowsRoot) & FileAttributes.ReparsePoint) != 0
                    || (File.GetAttributes(Path.Combine(windowsRoot, "Packages")) & FileAttributes.ReparsePoint) != 0) return false;
                error = string.Empty;
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool TryRebindPersistedProject(UnityPipelinePersistedOperation state, string currentProjectRoot, out string error)
        {
            error = "The persisted Pipeline operation does not bind the exact current project.";
            if (state == null
                || !string.Equals(state.editorVersion, Application.unityVersion, StringComparison.Ordinal)
                || !TryResolveProjectRoots(currentProjectRoot, currentProjectRoot, out var canonicalRoot, out var windowsRoot, out _)
                || !string.Equals(canonicalRoot, state.canonicalProjectRoot, StringComparison.Ordinal)
                || !SamePath(windowsRoot, state.windowsProjectRoot)
                || !SamePath(state.cliExecutablePath, UnityCliPipelineSetup.OfficialWindowsInstallPath)
                || !string.Equals(state.bindingIdentitySha256, ComputeBindingIdentity(state), StringComparison.Ordinal))
            {
                return false;
            }
            error = string.Empty;
            return true;
        }

        internal static void RecordActualState(UnityPipelinePersistedOperation state, UnityCliPipelineSetup.UnityPipelineStatus local)
        {
            if (state == null) return;
            local = local ?? new UnityCliPipelineSetup.UnityPipelineStatus();
            state.actualManifestVersion = ValidOrEmptyVersion(local.ManifestVersion);
            state.actualRegisteredVersion = ValidOrEmptyVersion(local.Version);
            state.actualRegisteredSource = Bound(local.Source, 64);
            state.actualLockVersion = string.Empty;
            if (local.Registered && TryCaptureLocalEvidence(state.windowsProjectRoot, local, out var evidence, out _))
            {
                state.actualLockVersion = evidence.LockVersion;
            }
        }

        static string ValidOrEmptyVersion(string value)
        {
            return UnityCliUpdateCheck.TryParseSemVer(value, out _) ? value : string.Empty;
        }

        static string SafeVersion(string value)
        {
            return UnityCliUpdateCheck.TryParseSemVer(value, out _) ? value : "unavailable";
        }

        static bool TryParseFingerprint(string fingerprint, string relativePath, bool required, out bool exists, out long length, out string sha256)
        {
            exists = false;
            length = 0;
            sha256 = string.Empty;
            var missing = relativePath + "|missing|0|\n";
            if (!required && string.Equals(fingerprint, missing, StringComparison.Ordinal)) return true;
            var match = Regex.Match(fingerprint ?? string.Empty, "\\A" + Regex.Escape(relativePath) + "\\|file\\|([0-9]+)\\|([0-9a-f]{64})\\n\\z", RegexOptions.CultureInvariant);
            if (!match.Success || !long.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out length)) return false;
            exists = true;
            sha256 = match.Groups[2].Value;
            return length >= 0 && length <= 4L * 1024L * 1024L;
        }

        static bool TryReadBoundProjectFile(
            string projectRoot,
            string relativePath,
            bool required,
            out string content,
            out bool exists,
            out long length,
            out string sha256)
        {
            content = string.Empty;
            exists = false;
            length = 0;
            sha256 = string.Empty;
            if (!PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, relativePath, out var before)
                || !TryParseFingerprint(before, relativePath, required, out exists, out length, out sha256))
            {
                return false;
            }
            if (!exists) return true;

            try
            {
                var path = Path.Combine(projectRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) return false;
                byte[] bytes;
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length != length || stream.Length > 4L * 1024L * 1024L) return false;
                    bytes = new byte[(int)stream.Length];
                    var offset = 0;
                    while (offset < bytes.Length)
                    {
                        var read = stream.Read(bytes, offset, bytes.Length - offset);
                        if (read <= 0) return false;
                        offset += read;
                    }
                }
                if (!string.Equals(Sha256(bytes), sha256, StringComparison.Ordinal)
                    || !PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, relativePath, out var after)
                    || !string.Equals(before, after, StringComparison.Ordinal))
                {
                    return false;
                }
                content = new UTF8Encoding(false, true).GetString(bytes);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static string Sha256(byte[] value)
        {
            using (var hash = SHA256.Create())
            {
                var digest = hash.ComputeHash(value ?? new byte[0]);
                var builder = new StringBuilder(digest.Length * 2);
                for (var index = 0; index < digest.Length; index++) builder.Append(digest[index].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
        }

        static bool TryLoadState(out UnityPipelinePersistedOperation state, out bool malformed)
        {
            var raw = SessionState.GetString(SessionStateKey, string.Empty);
            state = null;
            if (string.IsNullOrEmpty(raw))
            {
                malformed = false;
                return false;
            }
            malformed = !UnityPipelinePersistedOperation.TryParse(raw, out state);
            return !malformed;
        }

        static bool IsProcessAlive(int processId)
        {
            try
            {
                using (var process = Process.GetProcessById(processId)) return !process.HasExited;
            }
            catch (ArgumentException)
            {
                return false;
            }
            catch
            {
                return true;
            }
        }

        static bool SamePath(string left, string right)
        {
            try
            {
                return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right)
                    && string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        static string PowerShellLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        static string Bound(string value, int maxCharacters)
        {
            var safe = BoundedProcessOperation.Sanitize(value ?? string.Empty);
            return safe.Length <= maxCharacters ? safe : safe.Substring(0, maxCharacters);
        }

        static DateTime AsUtc(DateTime value)
        {
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }
    }

    internal static class PipelineJsonEvidenceParser
    {
        internal static bool TryReadBoolPath(string json, IReadOnlyList<string> path, out bool value)
        {
            value = false;
            if (string.IsNullOrEmpty(json) || path == null || path.Count == 0 || json.Length > 4 * 1024 * 1024
                || !new Parser(json, path, false, true).TryRead(out var text)) return false;
            value = text == "true";
            return true;
        }

        internal static bool TryReadInt64Path(string json, IReadOnlyList<string> path, out long value)
        {
            value = 0;
            return !string.IsNullOrEmpty(json) && path != null && path.Count > 0 && json.Length <= 4 * 1024 * 1024
                && new Parser(json, path, true).TryRead(out var text)
                && long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);
        }

        internal static bool TryReadStringPath(string json, IReadOnlyList<string> path, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrEmpty(json) || path == null || path.Count == 0 || json.Length > 4 * 1024 * 1024) return false;
            var parser = new Parser(json, path);
            return parser.TryRead(out value);
        }

        internal static bool TryReadOptionalStringPath(string json, IReadOnlyList<string> path, out string value)
        {
            value = string.Empty;
            if (string.IsNullOrEmpty(json) || path == null || path.Count == 0 || json.Length > 4 * 1024 * 1024) return false;
            return new Parser(json, path).TryRead(out value, false);
        }

        internal static bool TryReadOptionalStringPath(string json, IReadOnlyList<string> path, out string value, out bool found)
        {
            value = string.Empty;
            found = false;
            if (string.IsNullOrEmpty(json) || path == null || path.Count == 0 || json.Length > 4 * 1024 * 1024) return false;
            var parser = new Parser(json, path);
            if (!parser.TryRead(out value, false)) return false;
            found = parser.Found;
            return true;
        }

        sealed class Parser
        {
            readonly string m_json;
            readonly IReadOnlyList<string> m_path;
            readonly bool m_readNumber;
            readonly bool m_readBoolean;
            int m_index;
            internal bool Found { get; private set; }

            public Parser(string json, IReadOnlyList<string> path, bool readNumber = false, bool readBoolean = false)
            {
                m_json = json;
                m_path = path;
                m_readNumber = readNumber;
                m_readBoolean = readBoolean;
            }

            public bool TryRead(out string value, bool requireFound = true)
            {
                value = string.Empty;
                SkipWhitespace();
                if (!ReadObjectAtPath(0, 0, out var found, out value) || (requireFound && !found)) return false;
                Found = found;
                SkipWhitespace();
                return m_index == m_json.Length;
            }

            bool ReadObjectAtPath(int pathIndex, int depth, out bool found, out string value)
            {
                found = false;
                value = string.Empty;
                if (depth > 32 || !Consume('{')) return false;
                SkipWhitespace();
                if (Consume('}')) return true;
                var matches = 0;
                while (true)
                {
                    if (!ReadString(out var key)) return false;
                    SkipWhitespace();
                    if (!Consume(':')) return false;
                    SkipWhitespace();
                    if (string.Equals(key, m_path[pathIndex], StringComparison.Ordinal))
                    {
                        matches++;
                        if (matches > 1) return false;
                        if (pathIndex == m_path.Count - 1)
                        {
                            if (m_readNumber)
                            {
                                var start = m_index;
                                if (!SkipNumber()) return false;
                                value = m_json.Substring(start, m_index - start);
                            }
                            else if (m_readBoolean)
                            {
                                if (StartsWith("true")) { value = "true"; m_index += 4; }
                                else if (StartsWith("false")) { value = "false"; m_index += 5; }
                                else return false;
                            }
                            else if (!ReadString(out value)) return false;
                            found = true;
                        }
                        else
                        {
                            if (!ReadObjectAtPath(pathIndex + 1, depth + 1, out found, out value)) return false;
                        }
                    }
                    else if (!SkipValue(depth + 1))
                    {
                        return false;
                    }
                    SkipWhitespace();
                    if (Consume('}')) return true;
                    if (!Consume(',')) return false;
                    SkipWhitespace();
                }
            }

            bool SkipValue(int depth)
            {
                if (depth > 32 || m_index >= m_json.Length) return false;
                SkipWhitespace();
                if (m_index >= m_json.Length) return false;
                if (m_json[m_index] == '"') return ReadString(out _);
                if (m_json[m_index] == '{')
                {
                    m_index++;
                    SkipWhitespace();
                    if (Consume('}')) return true;
                    while (true)
                    {
                        if (!ReadString(out _)) return false;
                        SkipWhitespace();
                        if (!Consume(':') || !SkipValue(depth + 1)) return false;
                        SkipWhitespace();
                        if (Consume('}')) return true;
                        if (!Consume(',')) return false;
                        SkipWhitespace();
                    }
                }
                if (m_json[m_index] == '[')
                {
                    m_index++;
                    SkipWhitespace();
                    if (Consume(']')) return true;
                    while (true)
                    {
                        if (!SkipValue(depth + 1)) return false;
                        SkipWhitespace();
                        if (Consume(']')) return true;
                        if (!Consume(',')) return false;
                        SkipWhitespace();
                    }
                }
                if (StartsWith("true")) { m_index += 4; return true; }
                if (StartsWith("false")) { m_index += 5; return true; }
                if (StartsWith("null")) { m_index += 4; return true; }
                return SkipNumber();
            }

            bool ReadString(out string value)
            {
                value = string.Empty;
                if (!Consume('"')) return false;
                var builder = new StringBuilder();
                while (m_index < m_json.Length)
                {
                    var character = m_json[m_index++];
                    if (character == '"')
                    {
                        value = builder.ToString();
                        return true;
                    }
                    if (character < ' ') return false;
                    if (character != '\\')
                    {
                        builder.Append(character);
                        continue;
                    }
                    if (m_index >= m_json.Length) return false;
                    var escaped = m_json[m_index++];
                    switch (escaped)
                    {
                        case '"': builder.Append('"'); break;
                        case '\\': builder.Append('\\'); break;
                        case '/': builder.Append('/'); break;
                        case 'b': builder.Append('\b'); break;
                        case 'f': builder.Append('\f'); break;
                        case 'n': builder.Append('\n'); break;
                        case 'r': builder.Append('\r'); break;
                        case 't': builder.Append('\t'); break;
                        case 'u':
                            if (m_index + 4 > m_json.Length
                                || !int.TryParse(m_json.Substring(m_index, 4), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var codePoint)) return false;
                            builder.Append((char)codePoint);
                            m_index += 4;
                            break;
                        default: return false;
                    }
                }
                return false;
            }

            bool SkipNumber()
            {
                var start = m_index;
                if (m_index < m_json.Length && m_json[m_index] == '-') m_index++;
                if (m_index >= m_json.Length) return false;
                if (m_json[m_index] == '0')
                {
                    m_index++;
                }
                else
                {
                    if (m_json[m_index] < '1' || m_json[m_index] > '9') return false;
                    while (m_index < m_json.Length && m_json[m_index] >= '0' && m_json[m_index] <= '9') m_index++;
                }
                if (m_index < m_json.Length && m_json[m_index] == '.')
                {
                    m_index++;
                    var fraction = m_index;
                    while (m_index < m_json.Length && m_json[m_index] >= '0' && m_json[m_index] <= '9') m_index++;
                    if (fraction == m_index) return false;
                }
                if (m_index < m_json.Length && (m_json[m_index] == 'e' || m_json[m_index] == 'E'))
                {
                    m_index++;
                    if (m_index < m_json.Length && (m_json[m_index] == '+' || m_json[m_index] == '-')) m_index++;
                    var exponent = m_index;
                    while (m_index < m_json.Length && m_json[m_index] >= '0' && m_json[m_index] <= '9') m_index++;
                    if (exponent == m_index) return false;
                }
                return m_index > start;
            }

            bool Consume(char value)
            {
                if (m_index >= m_json.Length || m_json[m_index] != value) return false;
                m_index++;
                return true;
            }

            bool StartsWith(string value)
            {
                return m_index + value.Length <= m_json.Length
                    && string.CompareOrdinal(m_json, m_index, value, 0, value.Length) == 0;
            }

            void SkipWhitespace()
            {
                while (m_index < m_json.Length)
                {
                    var value = m_json[m_index];
                    if (value != ' ' && value != '\t' && value != '\r' && value != '\n') break;
                    m_index++;
                }
            }
        }
    }

    internal sealed class UnityPipelineUpdateCoordinator : IDisposable
    {
        enum CoordinatorStage
        {
            PreflightCommandSurface,
            Mutation,
        }

        internal static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan CommandSurfaceTimeout = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(6);
        readonly UnityCliUpdateCheckSnapshot m_snapshot;
        readonly UnityPipelineUpdateBinding m_binding;
        readonly IUpdateCheckProcessStarter m_processStarter;
        readonly Func<UnityCliPipelineSetup.UnityPipelineStatus> m_statusReader;
        readonly Func<UnityPipelineUpdateBinding, bool> m_projectBindingValidator;
        readonly Func<DateTime> m_nowUtc;
        readonly DateTime m_startedAtUtc;
        readonly TimeSpan m_totalTimeout;
        IUpdateCheckProcess m_process;
        UnityPipelinePersistedOperation m_state;
        CoordinatorStage m_stage = CoordinatorStage.PreflightCommandSurface;
        bool m_disposed;
        bool m_complete;

        internal UnityPipelineUpdateCoordinator(
            UnityCliUpdateCheckSnapshot snapshot,
            UnityPipelineUpdateBinding binding,
            IUpdateCheckProcessStarter processStarter = null,
            Func<UnityCliPipelineSetup.UnityPipelineStatus> statusReader = null,
            Func<UnityPipelineUpdateBinding, bool> projectBindingValidator = null,
            Func<DateTime> nowUtc = null,
            TimeSpan? totalTimeout = null)
        {
            m_snapshot = snapshot ?? UnityCliUpdateCheckSnapshot.NotChecked();
            m_binding = binding ?? new UnityPipelineUpdateBinding();
            m_processStarter = processStarter ?? new DefaultProcessStarter();
            m_statusReader = statusReader ?? (() => UnityCliPipelineSetup.GetPipelineStatus(m_binding.WindowsProjectRoot));
            m_projectBindingValidator = projectBindingValidator ?? (candidate => UnityPipelineUpdateOperation.ValidateProjectBindingForDispatch(candidate, out _));
            m_nowUtc = nowUtc ?? (() => DateTime.UtcNow);
            m_startedAtUtc = UtcNow();
            m_totalTimeout = totalTimeout ?? TotalTimeout;
            Result = new UnityPipelineUpdateResult();
            Start();
        }

        public UnityPipelineUpdateResult Result { get; private set; }
        public bool IsComplete => m_complete;

        public bool Poll()
        {
            if (m_complete) return true;
            if (UtcNow() - m_startedAtUtc > m_totalTimeout)
            {
                DisposeProcess();
                Complete(
                    Result.MutationStarted ? UnityCliUpdateCheck.UpdateStateOutcomeUnknown : UnityCliUpdateCheck.UpdateStateVerificationRequired,
                    Result.MutationStarted ? "pipeline_update_deadline_exceeded" : "pipeline_update_preflight_deadline_exceeded",
                    Result.MutationStarted
                        ? "Pipeline update exceeded its bounded deadline after dispatch. Refresh local state; do not repeat automatically."
                        : "Pipeline command-surface preflight exceeded its bounded deadline; no mutation started.");
                return true;
            }
            if (m_process == null)
            {
                Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_process_missing", "The Pipeline preflight or one allowed update process is unavailable; no automatic repeat is allowed.");
                return true;
            }

            bool completed;
            try
            {
                completed = m_process.Poll();
            }
            catch
            {
                DisposeProcess();
                Complete(
                    Result.MutationStarted ? UnityCliUpdateCheck.UpdateStateOutcomeUnknown : UnityCliUpdateCheck.UpdateStateVerificationRequired,
                    "pipeline_update_process_poll_failed",
                    Result.MutationStarted
                        ? "The tracked Pipeline update process could not be polled. Refresh local state; no automatic repeat is allowed."
                        : "The read-only Pipeline command-surface preflight could not be polled; no mutation started.");
                return true;
            }
            if (!completed) return false;

            var process = m_process;
            m_process = null;
            var startMutation = false;
            try
            {
                if (m_stage == CoordinatorStage.PreflightCommandSurface)
                {
                    if (!process.TerminationConfirmed)
                    {
                        WindowsCliUpdateCoordinator.TrackUnconfirmedProcess(process.ProcessId);
                        Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_preflight_termination_unconfirmed", "The read-only Pipeline command-surface probe termination is unconfirmed; no mutation started.");
                    }
                    else if (process.TimedOut || process.ExitCode != 0
                        || !UnityCliUpdateCheck.TryValidatePipelineInstallHelp(process.StandardOutput))
                    {
                        Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_preflight_surface_changed", "The exact project-scoped Pipeline update command surface is unavailable; no mutation started.");
                    }
                    else
                    {
                        startMutation = true;
                    }
                }
                else
                {
                    m_state.terminationConfirmed = process.TerminationConfirmed;
                    if (!process.TerminationConfirmed)
                    {
                        m_state.deliveryCertainty = "uncertain";
                        m_state.processId = process.ProcessId;
                        Complete(UnityCliUpdateCheck.UpdateStateOutcomeUnknown, "pipeline_update_process_termination_unconfirmed", "Pipeline updater termination is unconfirmed. Wait for the tracked PID to exit, then refresh local state.");
                    }
                    else if (process.TimedOut)
                    {
                        m_state.deliveryCertainty = "uncertain";
                        m_state.processId = 0;
                        Complete(UnityCliUpdateCheck.UpdateStateOutcomeUnknown, "pipeline_update_timed_out", "Pipeline update timed out. Refresh local state; do not repeat automatically.");
                    }
                    else if (process.ExitCode != 0)
                    {
                        m_state.deliveryCertainty = "delivered_response";
                        m_state.processId = 0;
                        Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_command_failed", "The one exact Pipeline update command failed. Refresh local state before another decision.");
                    }
                    else
                    {
                        Result.MutationExitSucceeded = true;
                        m_state.mutationExitSucceeded = true;
                        m_state.terminationConfirmed = true;
                        m_state.deliveryCertainty = "delivered_response";
                        m_state.processId = 0;
                        m_state.phase = UnityPipelineUpdateOperation.PhaseAwaitingPackage;
                        m_state.updateState = UnityCliUpdateCheck.UpdateStateInProgress;
                        m_state.summary = "Pipeline mutation completed; waiting for exact package resolution and post-reload compile proof.";
                        m_state.updatedAtUtcTicks = UtcNow().Ticks;
                        UnityPipelineUpdateOperation.Persist(m_state);
                        PublishStatus(UnityCliUpdateCheck.UpdateStateInProgress, string.Empty, m_state.summary);
                        Result.UpdateState = UnityCliUpdateCheck.UpdateStateInProgress;
                        Result.Summary = m_state.summary;
                        m_complete = true;
                        UnityPipelineUpdateOperation.SchedulePersistedVerification();
                    }
                }
            }
            finally
            {
                try { process.Dispose(); } catch { }
            }
            if (startMutation && !m_complete)
            {
                StartMutation();
            }
            return m_complete;
        }

        public void Cancel()
        {
            if (m_complete) return;
            DisposeProcess();
            Complete(
                Result.MutationStarted ? UnityCliUpdateCheck.UpdateStateOutcomeUnknown : UnityCliUpdateCheck.UpdateStateVerificationRequired,
                Result.MutationStarted ? "pipeline_update_cancelled_after_dispatch" : "pipeline_update_cancelled_before_dispatch",
                Result.MutationStarted
                    ? "Pipeline update was cancelled after dispatch. Refresh local state; do not repeat automatically."
                    : "Pipeline update was cancelled before mutation dispatch.");
        }

        public void Dispose()
        {
            if (m_disposed) return;
            m_disposed = true;
            if (!m_complete) Cancel();
        }

        void Start()
        {
            m_state = UnityPipelineUpdateOperation.CreateState(m_binding, m_startedAtUtc);
            UnityPipelineUpdateOperation.Persist(m_state);
            PublishStatus(UnityCliUpdateCheck.UpdateStateInProgress, string.Empty, "Pipeline exact-target preflight is validating bound local evidence.");

            if (!TryValidateBoundLocalEvidence(out var errorCode, out var summary))
            {
                Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, errorCode, summary);
                return;
            }

            if (!m_processStarter.TryStart(
                WindowsCliAuthenticode.PowerShellPath,
                WindowsCliAuthenticode.BuildBoundCommandArguments(
                    m_binding.CliExecutablePath,
                    m_binding.CliSignatureThumbprint,
                    m_binding.CliExecutableSha256,
                    new[] { "pipeline", "install", "--help", "--no-banner" }),
                m_binding.WindowsProjectRoot,
                CommandSurfaceTimeout,
                out m_process,
                out _))
            {
                Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_surface_probe_start_failed", "The read-only Pipeline command-surface preflight could not start; no mutation started.");
            }
        }

        void StartMutation()
        {
            if (!TryValidateBoundLocalEvidence(out var errorCode, out var summary))
            {
                Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, errorCode, summary);
                return;
            }

            m_stage = CoordinatorStage.Mutation;

            m_state.phase = UnityPipelineUpdateOperation.PhaseDispatching;
            m_state.dispatchCount = 1;
            m_state.deliveryCertainty = "dispatch_pending";
            m_state.updatedAtUtcTicks = UtcNow().Ticks;
            UnityPipelineUpdateOperation.Persist(m_state);
            if (!m_processStarter.TryStart(
                WindowsCliAuthenticode.PowerShellPath,
                UnityPipelineUpdateOperation.BuildTrackedMutationArguments(m_binding),
                m_binding.WindowsProjectRoot,
                MutationTimeout,
                out m_process,
                out _))
            {
                m_state.deliveryCertainty = "not_dispatched";
                m_state.terminationConfirmed = true;
                Complete(UnityCliUpdateCheck.UpdateStateVerificationRequired, "pipeline_update_process_start_failed", "The one exact Pipeline update process did not start; run a fresh manual check before retrying.");
                return;
            }

            Result.MutationStarted = true;
            m_state.mutationStarted = true;
            m_state.deliveryCertainty = "dispatched_once";
            m_state.terminationConfirmed = false;
            m_state.processId = m_process.ProcessId;
            m_state.phase = UnityPipelineUpdateOperation.PhaseMutationRunning;
            m_state.summary = "One exact Pipeline update command is running for the confirmed project and target.";
            m_state.updatedAtUtcTicks = UtcNow().Ticks;
            UnityPipelineUpdateOperation.Persist(m_state);
            PublishStatus(UnityCliUpdateCheck.UpdateStateInProgress, string.Empty, m_state.summary);
        }

        bool TryValidateBoundLocalEvidence(out string errorCode, out string summary)
        {
            if (!UnityPipelineUpdateOperation.EditorReadyForMutation())
            {
                errorCode = "pipeline_update_editor_busy";
                summary = "Unity became busy compiling, updating packages/assets, or entering Play Mode before dispatch; no mutation started.";
                return false;
            }

            UnityCliPipelineSetup.UnityPipelineStatus local;
            try
            {
                local = m_statusReader();
            }
            catch
            {
                errorCode = "pipeline_update_preflight_unavailable";
                summary = "Pipeline local state could not be re-read before dispatch; no mutation started.";
                return false;
            }
            if (!UnityPipelineUpdateOperation.TryCaptureLocalEvidence(m_binding.WindowsProjectRoot, local, out var evidence, out _)
                || !string.Equals(evidence.ManifestVersion, m_binding.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(evidence.RegisteredVersion, m_binding.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(evidence.RegisteredSource, m_binding.PreRegisteredSource, StringComparison.Ordinal)
                || !string.Equals(evidence.ManifestSha256, m_binding.PreManifestSha256, StringComparison.Ordinal)
                || evidence.ManifestLength != m_binding.PreManifestLength
                || evidence.LockExists != m_binding.PreLockExists
                || !string.Equals(evidence.LockSha256, m_binding.PreLockSha256, StringComparison.Ordinal)
                || evidence.LockLength != m_binding.PreLockLength
                || !string.Equals(evidence.LockVersion, m_binding.PreLockVersion, StringComparison.Ordinal))
            {
                errorCode = "pipeline_update_preflight_changed";
                summary = "Pipeline manifest, lock, or registered package evidence changed before dispatch; no mutation started.";
                return false;
            }
            if (!m_projectBindingValidator(m_binding))
            {
                errorCode = "pipeline_update_project_binding_changed";
                summary = "The exact current project path or reparse-point state changed before dispatch; no mutation started.";
                return false;
            }

            errorCode = string.Empty;
            summary = string.Empty;
            return true;
        }

        void Complete(string updateState, string errorCode, string summary)
        {
            if (m_complete) return;
            m_complete = true;
            Result.UpdateState = updateState;
            Result.ErrorCode = errorCode;
            Result.Summary = summary;
            Result.Success = string.Equals(updateState, UnityCliUpdateCheck.UpdateStateCurrent, StringComparison.Ordinal);
            if (m_state != null)
            {
                try { UnityPipelineUpdateOperation.RecordActualState(m_state, m_statusReader()); } catch { }
                UnityPipelineUpdateOperation.Finish(m_state, updateState, errorCode, summary, UtcNow());
            }
            PublishStatus(updateState, errorCode, summary);
        }

        void PublishStatus(string updateState, string errorCode, string summary)
        {
            if (m_snapshot.Pipeline == null) return;
            var status = m_snapshot.Pipeline.Update == null ? ComponentUpdateStatus.NotChecked() : m_snapshot.Pipeline.Update.Clone();
            status.UpdateState = updateState;
            status.LastErrorCode = errorCode ?? string.Empty;
            status.LastErrorSummary = summary ?? string.Empty;
            m_snapshot.Pipeline.Update = status;
            UnityCliUpdateCheck.Publish(m_snapshot);
        }

        void DisposeProcess()
        {
            var process = m_process;
            m_process = null;
            if (process == null) return;
            var processId = process.ProcessId;
            try { process.Dispose(); } catch { }
            if (m_state == null) return;
            if (m_stage == CoordinatorStage.PreflightCommandSurface)
            {
                if (!process.TerminationConfirmed)
                {
                    WindowsCliUpdateCoordinator.TrackUnconfirmedProcess(processId);
                }
                return;
            }
            m_state.terminationConfirmed = process.TerminationConfirmed;
            if (!process.TerminationConfirmed)
            {
                m_state.processId = processId;
                m_state.deliveryCertainty = "uncertain";
            }
            else
            {
                m_state.processId = 0;
                if (m_state.mutationStarted) m_state.deliveryCertainty = "uncertain";
            }
        }

        DateTime UtcNow()
        {
            var value = m_nowUtc();
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        sealed class DefaultProcessStarter : IUpdateCheckProcessStarter
        {
            public bool TryStart(string fileName, IReadOnlyList<string> arguments, string workingDirectory, TimeSpan timeout, out IUpdateCheckProcess operation, out string error)
            {
                operation = null;
                if (!BoundedProcessOperation.TryStart(fileName, arguments, workingDirectory, timeout, out var bounded, out error)) return false;
                operation = new DefaultProcess(bounded);
                return true;
            }
        }

        sealed class DefaultProcess : IUpdateCheckProcess
        {
            readonly BoundedProcessOperation m_operation;
            public DefaultProcess(BoundedProcessOperation operation) { m_operation = operation; }
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
}
