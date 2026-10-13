using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class WindowsCliAuthenticode
    {
        internal const string ValidState = "valid_unity";
        internal const string InvalidState = "invalid_or_untrusted";
        internal const string UnitySigner = "Unity Technologies SF";
        const int MaxSignatureResponseCharacters = 4096;

        internal static string PowerShellPath
        {
            get
            {
                var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
                return string.IsNullOrEmpty(systemDirectory)
                    ? string.Empty
                    : Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            }
        }

        internal static string SecurityModulePath
        {
            get
            {
                var windowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                return string.IsNullOrEmpty(windowsDirectory)
                    ? string.Empty
                    : Path.Combine(windowsDirectory, "System32", "WindowsPowerShell", "v1.0", "Modules", "Microsoft.PowerShell.Security", "Microsoft.PowerShell.Security.psd1");
            }
        }

        internal static IReadOnlyList<string> BuildProbeArguments(string executablePath)
        {
            var modulePath = PowerShellLiteral(SecurityModulePath);
            var cliPath = PowerShellLiteral(executablePath);
            return new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$ErrorActionPreference='Stop';"
                    + "$module='" + modulePath + "';"
                    + "$path='" + cliPath + "';"
                    + "Import-Module -Name $module -Force;"
                    + "$stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);"
                    + "try{$signature=Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath $path;"
                    + "if ($null -eq $signature.SignerCertificate) { throw 'signature unavailable' };"
                    + "$stream.Position=0;$sha=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-','')}finally{$stream.Dispose()};"
                    + "[ordered]@{status=[string]$signature.Status;subject=[string]$signature.SignerCertificate.Subject;thumbprint=[string]$signature.SignerCertificate.Thumbprint;sha256=$sha}|ConvertTo-Json -Compress",
            };
        }

        internal static IReadOnlyList<string> BuildTrackedUpdateArguments(WindowsCliUpdateBinding binding)
        {
            binding = binding ?? new WindowsCliUpdateBinding();
            var modulePath = PowerShellLiteral(SecurityModulePath);
            var cliPath = PowerShellLiteral(binding.ExecutablePath);
            var target = PowerShellLiteral(binding.TargetVersion);
            var expectedThumbprint = PowerShellLiteral(binding.SignatureThumbprint);
            var expectedSha256 = PowerShellLiteral(binding.ExecutableSha256);
            return new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$ErrorActionPreference='Stop';"
                    + "$module='" + modulePath + "';"
                    + "$path='" + cliPath + "';"
                    + "$target='" + target + "';"
                    + "$expectedThumbprint='" + expectedThumbprint + "';"
                    + "$expectedSha256='" + expectedSha256 + "';"
                    + "function Assert-NoReparsePath([string]$candidate){$current=[IO.Path]::GetFullPath($candidate);while(-not [string]::IsNullOrEmpty($current)){$item=Get-Item -LiteralPath $current -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'bound executable path contains a reparse point'};$parent=[IO.Path]::GetDirectoryName($current);if([string]::IsNullOrEmpty($parent) -or $parent -ceq $current){break};$current=$parent}};"
                    + "Import-Module -Name $module -Force;"
                    + "Assert-NoReparsePath $path;"
                    + "$stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);"
                    + "try{$signature=Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath $path;"
                    + "if($null -eq $signature.SignerCertificate){throw 'signature unavailable'};"
                    + "$stream.Position=0;$sha=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-','');"
                    + "if([string]$signature.Status -cne 'Valid' -or [string]$signature.SignerCertificate.Thumbprint -cne $expectedThumbprint -or $sha -cne $expectedSha256.ToUpperInvariant()){throw 'bound executable identity changed'};"
                    + "$arguments=@('upgrade','--target',$target,'--yes','--format','json','--non-interactive','--no-banner');"
                    + "$process=Start-Process -FilePath $path -ArgumentList $arguments -NoNewWindow -PassThru}finally{$stream.Dispose()};"
                    + "$process.WaitForExit();exit $process.ExitCode",
            };
        }

        internal static IReadOnlyList<string> BuildBoundCommandArguments(
            string executablePath,
            string expectedThumbprint,
            string expectedSha256,
            IReadOnlyList<string> commandArguments)
        {
            var argumentArray = new StringBuilder("@(");
            for (var index = 0; index < (commandArguments == null ? 0 : commandArguments.Count); index++)
            {
                if (index > 0) argumentArray.Append(',');
                argumentArray.Append('\'').Append(PowerShellLiteral(commandArguments[index])).Append('\'');
            }
            argumentArray.Append(')');

            var modulePath = PowerShellLiteral(SecurityModulePath);
            var cliPath = PowerShellLiteral(executablePath);
            var thumbprint = PowerShellLiteral(expectedThumbprint);
            var sha256 = PowerShellLiteral(expectedSha256);
            return new[]
            {
                "-NoProfile",
                "-NonInteractive",
                "-Command",
                "$ErrorActionPreference='Stop';"
                    + "$module='" + modulePath + "';"
                    + "$path='" + cliPath + "';"
                    + "$expectedThumbprint='" + thumbprint + "';"
                    + "$expectedSha256='" + sha256 + "';"
                    + "function Assert-NoReparsePath([string]$candidate){$current=[IO.Path]::GetFullPath($candidate);while(-not [string]::IsNullOrEmpty($current)){$item=Get-Item -LiteralPath $current -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'bound executable path contains a reparse point'};$parent=[IO.Path]::GetDirectoryName($current);if([string]::IsNullOrEmpty($parent) -or $parent -ceq $current){break};$current=$parent}};"
                    + "Import-Module -Name $module -Force;"
                    + "Assert-NoReparsePath $path;"
                    + "$stream=[IO.File]::Open($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);"
                    + "try{$signature=Microsoft.PowerShell.Security\\Get-AuthenticodeSignature -LiteralPath $path;"
                    + "if($null -eq $signature.SignerCertificate){throw 'signature unavailable'};"
                    + "$stream.Position=0;$sha=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($stream)).Replace('-','');"
                    + "if([string]$signature.Status -cne 'Valid' -or [string]$signature.SignerCertificate.Thumbprint -cne $expectedThumbprint -or $sha -cne $expectedSha256.ToUpperInvariant()){throw 'bound executable identity changed'};"
                    + "$arguments=" + argumentArray + ";"
                    + "& $path @arguments;$childExit=$LASTEXITCODE}finally{$stream.Dispose()};"
                    + "if($null -eq $childExit){exit 1};exit $childExit",
            };
        }

        internal static bool TryParseProbeResponse(string output, out WindowsCliSignature signature, out string errorCode)
        {
            signature = null;
            errorCode = "cli_signature_invalid";
            if (string.IsNullOrEmpty(output) || output.Length > MaxSignatureResponseCharacters)
            {
                errorCode = "cli_signature_response_invalid";
                return false;
            }

            SignatureEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<SignatureEnvelope>(output);
            }
            catch
            {
                errorCode = "cli_signature_response_malformed";
                return false;
            }

            if (envelope == null
                || !string.Equals(envelope.status, "Valid", StringComparison.Ordinal)
                || string.IsNullOrEmpty(envelope.subject)
                || envelope.subject.Length > 512
                || !Regex.IsMatch(envelope.thumbprint ?? string.Empty, "^[0-9A-Fa-f]{40}$", RegexOptions.CultureInvariant)
                || !Regex.IsMatch(envelope.sha256 ?? string.Empty, "^[0-9A-Fa-f]{64}$", RegexOptions.CultureInvariant)
                || !HasDistinguishedNameField(envelope.subject, "CN", UnitySigner)
                || !HasDistinguishedNameField(envelope.subject, "O", UnitySigner))
            {
                errorCode = "cli_signature_untrusted";
                return false;
            }

            signature = new WindowsCliSignature
            {
                State = ValidState,
                Signer = UnitySigner,
                Thumbprint = envelope.thumbprint.ToUpperInvariant(),
                FileSha256 = envelope.sha256.ToLowerInvariant(),
            };
            errorCode = string.Empty;
            return true;
        }

        static bool HasDistinguishedNameField(string subject, string field, string expected)
        {
            var pattern = "(?:^|,\\s*)" + Regex.Escape(field) + "=" + Regex.Escape(expected) + "(?:,|$)";
            return Regex.IsMatch(subject, pattern, RegexOptions.CultureInvariant);
        }

        static string PowerShellLiteral(string value)
        {
            return (value ?? string.Empty).Replace("'", "''");
        }

        [Serializable]
        sealed class SignatureEnvelope
        {
            public string status;
            public string subject;
            public string thumbprint;
            public string sha256;
        }
    }

    internal sealed class WindowsCliSignature
    {
        public string State = WindowsCliAuthenticode.InvalidState;
        public string Signer = string.Empty;
        public string Thumbprint = string.Empty;
        public string FileSha256 = string.Empty;
    }

    internal sealed class WindowsCliUpdateBinding
    {
        public string InstalledVersion = string.Empty;
        public string TargetVersion = string.Empty;
        public string Channel = string.Empty;
        public string SourceIdentitySha256 = string.Empty;
        public string BindingIdentitySha256 = string.Empty;
        public string ExecutablePath = string.Empty;
        public string SignatureState = string.Empty;
        public string Signer = string.Empty;
        public string SignatureThumbprint = string.Empty;
        public string ExecutableSha256 = string.Empty;
        public DateTime CheckedAtUtc = DateTime.MinValue;
        public DateTime ExpiresAtUtc = DateTime.MinValue;
    }

    internal sealed class WindowsCliUpdateResult
    {
        public string UpdateState = UnityCliUpdateCheck.UpdateStateOutcomeUnknown;
        public string ErrorCode = string.Empty;
        public string Summary = string.Empty;
        public string ObservedVersion = string.Empty;
        public string SignatureState = WindowsCliAuthenticode.InvalidState;
        public bool MutationStarted;
        public bool MutationExitSucceeded;
        public bool VersionVerified;
        public bool SignatureVerified;
        public bool Success;
    }

    internal sealed class WindowsCliUpdateCoordinator : IDisposable
    {
        const string LocalRefreshRequiredSessionKey = "SGG.UnityMCP.WindowsCliUpdate.LocalRefreshRequired";
        const string UnconfirmedProcessIdSessionKey = "SGG.UnityMCP.WindowsCliUpdate.UnconfirmedProcessId";
        const string TerminationUnconfirmedSessionKey = "SGG.UnityMCP.WindowsCliUpdate.TerminationUnconfirmed";

        enum UpdateStage
        {
            PreflightSignature,
            PreflightVersion,
            PreflightCommandSurface,
            Mutation,
            VerifySignature,
            VerifyVersion,
            Complete,
        }

        internal static readonly TimeSpan VersionProbeTimeout = TimeSpan.FromSeconds(5);
        internal static readonly TimeSpan SignatureProbeTimeout = TimeSpan.FromSeconds(15);
        internal static readonly TimeSpan MutationTimeout = TimeSpan.FromMinutes(5);
        internal static readonly TimeSpan TotalTimeout = TimeSpan.FromMinutes(6);

        readonly UnityCliUpdateCheckSnapshot m_snapshot;
        readonly WindowsCliUpdateBinding m_binding;
        readonly IUpdateCheckProcessStarter m_processStarter;
        readonly Func<DateTime> m_nowUtc;
        readonly DateTime m_startedAtUtc;
        readonly TimeSpan m_totalTimeout;
        IUpdateCheckProcess m_process;
        UpdateStage m_stage = UpdateStage.PreflightSignature;
        bool m_disposed;
        bool m_finished;
        bool m_mutationTimedOut;
        string m_observedVersion = string.Empty;
        WindowsCliSignature m_observedSignature;

        internal WindowsCliUpdateCoordinator(
            UnityCliUpdateCheckSnapshot snapshot,
            WindowsCliUpdateBinding binding,
            IUpdateCheckProcessStarter processStarter = null,
            Func<DateTime> nowUtc = null,
            TimeSpan? totalTimeout = null)
        {
            m_snapshot = snapshot ?? UnityCliUpdateCheckSnapshot.NotChecked();
            m_binding = binding ?? new WindowsCliUpdateBinding();
            m_processStarter = processStarter ?? new DefaultProcessStarter();
            m_nowUtc = nowUtc ?? (() => DateTime.UtcNow);
            m_totalTimeout = totalTimeout ?? TotalTimeout;
            m_startedAtUtc = UtcNow();
            Result = new WindowsCliUpdateResult();
            MarkInProgress();
            StartCurrentStage();
        }

        public WindowsCliUpdateResult Result { get; private set; }
        public bool IsComplete => m_finished;
        internal static bool RequiresLocalRefresh => SessionState.GetBool(LocalRefreshRequiredSessionKey, false);

        internal static void ClearLocalRefreshRequirement()
        {
            SessionState.EraseBool(LocalRefreshRequiredSessionKey);
            SessionState.EraseBool(TerminationUnconfirmedSessionKey);
            SessionState.EraseInt(UnconfirmedProcessIdSessionKey);
        }

        internal static bool CanRefreshLocalState(out string error)
        {
            error = string.Empty;
            var processId = SessionState.GetInt(UnconfirmedProcessIdSessionKey, 0);
            if (processId <= 0)
            {
                if (SessionState.GetBool(TerminationUnconfirmedSessionKey, false))
                {
                    error = "A previous CLI-related process termination is unconfirmed. Restart the Editor or verify the process externally before continuing.";
                    return false;
                }
                return true;
            }

            try
            {
                using (var process = Process.GetProcessById(processId))
                {
                    if (!process.HasExited)
                    {
                        error = "The previous CLI updater process is still alive. Wait for it to exit before refreshing local state.";
                        return false;
                    }
                }
            }
            catch (ArgumentException)
            {
                // The exact tracked process no longer exists.
            }
            catch
            {
                error = "The previous CLI updater termination cannot be confirmed yet.";
                return false;
            }

            SessionState.EraseInt(UnconfirmedProcessIdSessionKey);
            SessionState.EraseBool(TerminationUnconfirmedSessionKey);
            return true;
        }

        internal static bool TryCreateBinding(
            UnityCliUpdateCheckSnapshot snapshot,
            UnityCliPipelineSetup.WindowsUnityCliStatus localStatus,
            DateTime utcNow,
            out WindowsCliUpdateBinding binding,
            out string error)
        {
            binding = null;
            error = "Run a fresh manual update check before updating Windows Unity CLI.";
            if (RequiresLocalRefresh)
            {
                error = "Refresh Local State before another CLI, Pipeline, skill, or update-check action.";
                return false;
            }
            if (!Environment.Is64BitProcess)
            {
                error = "Windows Unity CLI update requires a 64-bit Windows Editor process.";
                return false;
            }
            if (snapshot == null || !snapshot.IsComplete || snapshot.IsCancelled || snapshot.WindowsCli == null)
            {
                return false;
            }

            localStatus = localStatus ?? UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked();
            var checkedStatus = snapshot.WindowsCli;
            var update = checkedStatus.Update;
            var now = utcNow.Kind == DateTimeKind.Utc ? utcNow : utcNow.ToUniversalTime();
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
                error = "The manual CLI update check is missing, expired, or does not prove an available update.";
                return false;
            }

            if (!string.Equals(update.Channel, UnityCliUpdateCheck.CliChannel, StringComparison.Ordinal)
                || !string.Equals(update.SourceKind, "official_unity_cli_upgrade_check", StringComparison.Ordinal)
                || !UnityCliUpdateCheck.IsVersionInChannel(update.InstalledVersion, UnityCliUpdateCheck.CliChannel)
                || !UnityCliUpdateCheck.IsVersionInChannel(update.AvailableVersion, UnityCliUpdateCheck.CliChannel)
                || !UnityCliUpdateCheck.TryCompareSemVer(update.InstalledVersion, update.AvailableVersion, out var comparison)
                || comparison >= 0
                || !string.Equals(
                    update.SourceIdentitySha256,
                    UnityCliUpdateCheck.CliSourceIdentity(update.InstalledVersion, update.AvailableVersion),
                    StringComparison.Ordinal))
            {
                error = "The checked CLI target is not an exact newer official beta version.";
                return false;
            }

            if (!checkedStatus.Checked
                || !checkedStatus.Installed
                || !localStatus.Checked
                || !localStatus.Installed
                || !SamePath(checkedStatus.ExecutablePath, UnityCliPipelineSetup.OfficialWindowsInstallPath)
                || !SamePath(localStatus.ExecutablePath, UnityCliPipelineSetup.OfficialWindowsInstallPath)
                || !string.Equals(checkedStatus.Version, update.InstalledVersion, StringComparison.Ordinal)
                || !string.Equals(localStatus.Version, update.InstalledVersion, StringComparison.Ordinal))
            {
                error = "The exact official CLI path or installed version changed after the manual check.";
                return false;
            }

            if (!HasTrustedSignature(checkedStatus) || !HasTrustedSignature(localStatus)
                || !string.Equals(checkedStatus.ExecutableSignatureThumbprint, localStatus.ExecutableSignatureThumbprint, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(checkedStatus.ExecutableSha256, localStatus.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
            {
                error = "The exact official CLI does not have a current trusted Unity Authenticode proof.";
                return false;
            }

            binding = new WindowsCliUpdateBinding
            {
                InstalledVersion = update.InstalledVersion,
                TargetVersion = update.AvailableVersion,
                Channel = update.Channel,
                SourceIdentitySha256 = update.SourceIdentitySha256,
                ExecutablePath = UnityCliPipelineSetup.OfficialWindowsInstallPath,
                SignatureState = localStatus.ExecutableSignatureState,
                Signer = localStatus.ExecutableSigner,
                SignatureThumbprint = localStatus.ExecutableSignatureThumbprint,
                ExecutableSha256 = localStatus.ExecutableSha256,
                CheckedAtUtc = update.CheckedAtUtc,
                ExpiresAtUtc = update.ExpiresAtUtc,
            };
            binding.BindingIdentitySha256 = UnityCliUpdateCheck.SourceIdentity(
                binding.InstalledVersion + "|" + binding.TargetVersion + "|" + binding.Channel + "|"
                + binding.SourceIdentitySha256 + "|" + binding.ExecutablePath + "|" + binding.SignatureThumbprint + "|"
                + binding.ExecutableSha256 + "|" + binding.CheckedAtUtc.Ticks);
            error = string.Empty;
            return true;
        }

        public bool Poll()
        {
            if (IsComplete)
            {
                return true;
            }

            if (UtcNow() - m_startedAtUtc > m_totalTimeout)
            {
                DisposeCurrentProcessAndTrackTermination();
                Finish(
                    Result.MutationStarted ? UnityCliUpdateCheck.UpdateStateOutcomeUnknown : UnityCliUpdateCheck.UpdateStateVerificationRequired,
                    Result.MutationStarted ? "cli_update_deadline_exceeded" : "cli_update_preflight_deadline_exceeded",
                    Result.MutationStarted
                        ? "The CLI update exceeded its bounded deadline after mutation started. Refresh local state; do not repeat automatically."
                        : "The CLI update preflight exceeded its bounded deadline before mutation started.");
                return true;
            }

            if (m_process == null)
            {
                StartCurrentStage();
                return IsComplete;
            }

            bool completed;
            try
            {
                completed = m_process.Poll();
            }
            catch
            {
                DisposeCurrentProcessAndTrackTermination();
                Finish(
                    Result.MutationStarted ? UnityCliUpdateCheck.UpdateStateOutcomeUnknown : UnityCliUpdateCheck.UpdateStateVerificationRequired,
                    "cli_update_process_poll_failed",
                    "The tracked CLI update process could not be polled. Refresh local state; no automatic repeat is allowed.");
                return true;
            }

            if (!completed)
            {
                return false;
            }

            var process = m_process;
            m_process = null;
            try
            {
                if (!process.TerminationConfirmed)
                {
                    TrackUnconfirmedProcess(process.ProcessId);
                    Finish(
                        Result.MutationStarted ? UnityCliUpdateCheck.UpdateStateOutcomeUnknown : UnityCliUpdateCheck.UpdateStateVerificationRequired,
                        "cli_related_process_termination_unconfirmed",
                        "A tracked CLI-related process termination could not be confirmed. Do not start another process until the exact PID exits.");
                }
                else
                {
                    HandleCompletedProcess(process);
                }
            }
            finally
            {
                DisposeProcess(process);
            }

            if (!IsComplete)
            {
                m_stage = NextStage(m_stage);
                if (m_stage == UpdateStage.Complete)
                {
                    CompleteFromVerification();
                }
                else
                {
                    StartCurrentStage();
                }
            }
            return IsComplete;
        }

        public void Cancel()
        {
            if (IsComplete)
            {
                return;
            }

            DisposeCurrentProcessAndTrackTermination();
            Finish(
                Result.MutationStarted ? UnityCliUpdateCheck.UpdateStateOutcomeUnknown : UnityCliUpdateCheck.UpdateStateVerificationRequired,
                Result.MutationStarted ? "cli_update_cancelled_after_start" : "cli_update_cancelled_before_start",
                Result.MutationStarted
                    ? "The tracked CLI update was cancelled after mutation started. Refresh local state; do not repeat automatically."
                    : "The CLI update was cancelled during read-only preflight; no mutation started.");
        }

        public void Dispose()
        {
            if (m_disposed)
            {
                return;
            }

            m_disposed = true;
            if (!IsComplete)
            {
                Cancel();
            }
        }

        void MarkInProgress()
        {
            var status = m_snapshot.WindowsCli.Update == null
                ? ComponentUpdateStatus.NotChecked()
                : m_snapshot.WindowsCli.Update.Clone();
            status.UpdateState = UnityCliUpdateCheck.UpdateStateInProgress;
            status.LastErrorCode = string.Empty;
            status.LastErrorSummary = string.Empty;
            m_snapshot.WindowsCli.Update = status;
            UnityCliUpdateCheck.Publish(m_snapshot);
        }

        void StartCurrentStage()
        {
            if (IsComplete || m_process != null)
            {
                return;
            }

            switch (m_stage)
            {
                case UpdateStage.PreflightSignature:
                case UpdateStage.VerifySignature:
                    if (!Environment.Is64BitProcess)
                    {
                        HandleStartFailure("cli_signature_requires_64_bit_process");
                        return;
                    }
                    StartProcess(
                        WindowsCliAuthenticode.PowerShellPath,
                        WindowsCliAuthenticode.BuildProbeArguments(m_binding.ExecutablePath),
                        SignatureProbeTimeout,
                        "cli_signature_probe_start_failed");
                    break;
                case UpdateStage.PreflightVersion:
                    StartBoundCliProcess(
                        m_binding.SignatureThumbprint,
                        m_binding.ExecutableSha256,
                        new[] { "--version", "--no-banner" },
                        VersionProbeTimeout,
                        "cli_update_version_probe_start_failed");
                    break;
                case UpdateStage.PreflightCommandSurface:
                    StartBoundCliProcess(
                        m_binding.SignatureThumbprint,
                        m_binding.ExecutableSha256,
                        new[] { "upgrade", "--help", "--no-banner" },
                        VersionProbeTimeout,
                        "cli_update_surface_probe_start_failed");
                    break;
                case UpdateStage.Mutation:
                    if (m_processStarter.TryStart(
                        WindowsCliAuthenticode.PowerShellPath,
                        WindowsCliAuthenticode.BuildTrackedUpdateArguments(m_binding),
                        null,
                        MutationTimeout,
                        out m_process,
                        out _))
                    {
                        Result.MutationStarted = true;
                        return;
                    }
                    HandleStartFailure("cli_update_process_start_failed");
                    break;
                case UpdateStage.VerifyVersion:
                    if (m_observedSignature == null)
                    {
                        HandleStartFailure("cli_update_signature_verification_missing");
                        return;
                    }
                    StartBoundCliProcess(
                        m_observedSignature.Thumbprint,
                        m_observedSignature.FileSha256,
                        new[] { "--version", "--no-banner" },
                        VersionProbeTimeout,
                        "cli_update_version_probe_start_failed");
                    break;
                default:
                    CompleteFromVerification();
                    break;
            }
        }

        void StartProcess(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, string errorCode)
        {
            if (!m_processStarter.TryStart(fileName, arguments, null, timeout, out m_process, out _))
            {
                HandleStartFailure(errorCode);
            }
        }

        void StartBoundCliProcess(
            string expectedThumbprint,
            string expectedSha256,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            string errorCode)
        {
            StartProcess(
                WindowsCliAuthenticode.PowerShellPath,
                WindowsCliAuthenticode.BuildBoundCommandArguments(m_binding.ExecutablePath, expectedThumbprint, expectedSha256, arguments),
                timeout,
                errorCode);
        }

        void HandleStartFailure(string errorCode)
        {
            if (m_stage == UpdateStage.PreflightVersion
                || m_stage == UpdateStage.PreflightSignature
                || m_stage == UpdateStage.PreflightCommandSurface)
            {
                Finish(UnityCliUpdateCheck.UpdateStateVerificationRequired, errorCode, "The exact CLI update preflight could not be completed; no mutation started.");
                return;
            }

            if (m_stage == UpdateStage.Mutation)
            {
                Finish(UnityCliUpdateCheck.UpdateStateVerificationRequired, errorCode, "The exact CLI update process did not start; run a fresh manual check before retrying.");
                return;
            }

            m_stage = NextStage(m_stage);
            StartCurrentStage();
        }

        void HandleCompletedProcess(IUpdateCheckProcess process)
        {
            switch (m_stage)
            {
                case UpdateStage.PreflightVersion:
                    if (process.TimedOut || process.ExitCode != 0
                        || !UnityCliPipelineSetup.TryExtractCliVersion(process.StandardOutput, out var preflightVersion)
                        || !string.Equals(preflightVersion, m_binding.InstalledVersion, StringComparison.Ordinal))
                    {
                        Finish(UnityCliUpdateCheck.UpdateStateVerificationRequired, "cli_update_preflight_version_changed", "The exact CLI version changed before mutation; no update was started.");
                    }
                    break;
                case UpdateStage.PreflightSignature:
                    if (process.TimedOut || process.ExitCode != 0
                        || !WindowsCliAuthenticode.TryParseProbeResponse(process.StandardOutput, out var preflightSignature, out _)
                        || !string.Equals(preflightSignature.Thumbprint, m_binding.SignatureThumbprint, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(preflightSignature.FileSha256, m_binding.ExecutableSha256, StringComparison.OrdinalIgnoreCase))
                    {
                        Finish(UnityCliUpdateCheck.UpdateStateVerificationRequired, "cli_update_preflight_signature_changed", "The exact CLI signature changed or became unavailable before mutation; no update was started.");
                    }
                    break;
                case UpdateStage.PreflightCommandSurface:
                    if (process.TimedOut || process.ExitCode != 0
                        || !UnityCliUpdateCheck.TryValidateCliUpgradeHelp(process.StandardOutput))
                    {
                        Finish(UnityCliUpdateCheck.UpdateStateVerificationRequired, "cli_update_preflight_surface_changed", "The exact CLI target-update command surface is unavailable; no update was started.");
                    }
                    break;
                case UpdateStage.Mutation:
                    m_mutationTimedOut = process.TimedOut;
                    Result.MutationExitSucceeded = process.TerminationConfirmed && !process.TimedOut && process.ExitCode == 0;
                    if (m_mutationTimedOut)
                    {
                        Finish(UnityCliUpdateCheck.UpdateStateOutcomeUnknown, "cli_update_timed_out", "The CLI update timed out. Refresh local state; do not repeat automatically.");
                    }
                    break;
                case UpdateStage.VerifyVersion:
                    if (!process.TimedOut && process.ExitCode == 0
                        && UnityCliPipelineSetup.TryExtractCliVersion(process.StandardOutput, out var observedVersion))
                    {
                        m_observedVersion = observedVersion;
                        Result.VersionVerified = string.Equals(observedVersion, m_binding.TargetVersion, StringComparison.Ordinal);
                    }
                    break;
                case UpdateStage.VerifySignature:
                    if (!process.TimedOut && process.ExitCode == 0
                        && WindowsCliAuthenticode.TryParseProbeResponse(process.StandardOutput, out var signature, out _))
                    {
                        m_observedSignature = signature;
                        Result.SignatureVerified = true;
                    }
                    break;
            }
        }

        void CompleteFromVerification()
        {
            if (Result.MutationExitSucceeded && Result.VersionVerified && Result.SignatureVerified)
            {
                Finish(UnityCliUpdateCheck.UpdateStateCurrent, string.Empty, "Windows Unity CLI updated to the exact target and verified at the official path.");
                return;
            }

            if (m_mutationTimedOut)
            {
                Finish(UnityCliUpdateCheck.UpdateStateOutcomeUnknown, "cli_update_timed_out", "The CLI update timed out. Refresh local state; do not repeat automatically.");
                return;
            }

            Finish(UnityCliUpdateCheck.UpdateStateVerificationRequired, "cli_update_postcondition_failed", "The CLI update did not prove exit success, exact target version, and a valid Unity signature.");
        }

        void Finish(string updateState, string errorCode, string summary)
        {
            if (m_finished)
            {
                return;
            }

            m_stage = UpdateStage.Complete;
            m_finished = true;
            Result.UpdateState = updateState;
            Result.ErrorCode = errorCode ?? string.Empty;
            Result.Summary = summary ?? string.Empty;
            Result.ObservedVersion = m_observedVersion;
            Result.SignatureState = m_observedSignature == null ? WindowsCliAuthenticode.InvalidState : m_observedSignature.State;
            Result.Success = string.Equals(updateState, UnityCliUpdateCheck.UpdateStateCurrent, StringComparison.Ordinal);

            var windows = m_snapshot.WindowsCli ?? UnityCliPipelineSetup.WindowsUnityCliStatus.NotChecked();
            if (!string.IsNullOrEmpty(m_observedVersion))
            {
                windows.Checked = true;
                windows.Installed = true;
                windows.ExecutablePath = m_binding.ExecutablePath;
                windows.Version = m_observedVersion;
                windows.Message = "Installed " + m_observedVersion;
            }
            if (m_observedSignature != null)
            {
                windows.ExecutableSignatureState = m_observedSignature.State;
                windows.ExecutableSigner = m_observedSignature.Signer;
                windows.ExecutableSignatureThumbprint = m_observedSignature.Thumbprint;
                windows.ExecutableSha256 = m_observedSignature.FileSha256;
            }

            if (!Result.Success)
            {
                SessionState.SetBool(LocalRefreshRequiredSessionKey, true);
                if (string.IsNullOrEmpty(m_observedVersion))
                {
                    windows.Checked = false;
                    windows.Installed = false;
                    windows.ExecutablePath = string.Empty;
                    windows.Version = string.Empty;
                    windows.Message = "Local Windows CLI state requires an explicit refresh.";
                }
                if (m_observedSignature == null)
                {
                    windows.ExecutableSignatureState = WindowsCliAuthenticode.InvalidState;
                    windows.ExecutableSigner = string.Empty;
                    windows.ExecutableSignatureThumbprint = string.Empty;
                    windows.ExecutableSha256 = string.Empty;
                }
            }
            else
            {
                ClearLocalRefreshRequirement();
            }

            var update = windows.Update == null ? ComponentUpdateStatus.NotChecked() : windows.Update.Clone();
            update.UpdateState = updateState;
            update.LastErrorCode = errorCode ?? string.Empty;
            update.LastErrorSummary = summary ?? string.Empty;
            if (Result.Success)
            {
                var completedAt = UtcNow();
                update.CheckState = UnityCliUpdateCheck.CheckStateChecked;
                update.InstalledVersion = m_binding.TargetVersion;
                update.AvailableVersion = m_binding.TargetVersion;
                update.CheckedAtUtc = completedAt;
                update.ExpiresAtUtc = completedAt.Add(UnityCliUpdateCheck.CacheTtl);
            }
            windows.Update = update;
            m_snapshot.WindowsCli = windows;

            if (Result.MutationStarted && m_snapshot.Pipeline != null)
            {
                m_snapshot.Pipeline.Update = ComponentUpdateStatus.NotChecked(
                    m_snapshot.Pipeline.Version,
                    string.Empty,
                    "official_unity_pipeline_list_versions",
                    UnityCliUpdateCheck.SourceIdentity(UnityCliPipelineSetup.PipelinePackageId + "|pipeline list-versions"));
                m_snapshot.Pipeline.TargetPackageVersion = string.Empty;
                m_snapshot.Pipeline.CliCompatibilityState = "unknown";
            }

            m_snapshot.IsComplete = true;
            UnityCliPipelineSetup.SetWindowsStatus(windows);
            UnityCliUpdateCheck.Publish(m_snapshot);
        }

        DateTime UtcNow()
        {
            var value = m_nowUtc();
            return value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
        }

        void DisposeCurrentProcessAndTrackTermination()
        {
            var process = m_process;
            var processId = process == null ? 0 : process.ProcessId;
            m_process = null;
            DisposeProcess(process);
            if (process != null && !process.TerminationConfirmed)
            {
                TrackUnconfirmedProcess(processId);
            }
        }

        internal static void TrackUnconfirmedProcess(int processId)
        {
            SessionState.SetBool(LocalRefreshRequiredSessionKey, true);
            SessionState.SetBool(TerminationUnconfirmedSessionKey, true);
            if (processId > 0)
            {
                SessionState.SetInt(UnconfirmedProcessIdSessionKey, processId);
            }
        }

        static void DisposeProcess(IUpdateCheckProcess process)
        {
            try
            {
                process?.Dispose();
            }
            catch
            {
                // The next explicit local-state refresh remains authoritative.
            }
        }

        internal static bool HasTrustedSignature(UnityCliPipelineSetup.WindowsUnityCliStatus status)
        {
            return status != null
                && string.Equals(status.ExecutableSignatureState, WindowsCliAuthenticode.ValidState, StringComparison.Ordinal)
                && string.Equals(status.ExecutableSigner, WindowsCliAuthenticode.UnitySigner, StringComparison.Ordinal)
                && Regex.IsMatch(status.ExecutableSignatureThumbprint ?? string.Empty, "^[0-9A-F]{40}$", RegexOptions.CultureInvariant)
                && Regex.IsMatch(status.ExecutableSha256 ?? string.Empty, "^[0-9a-f]{64}$", RegexOptions.CultureInvariant);
        }

        static bool SamePath(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            try
            {
                return string.Equals(Path.GetFullPath(left).TrimEnd('\\', '/'), Path.GetFullPath(right).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        internal static IReadOnlyList<string> BuildMutationArguments(string targetVersion)
        {
            return new[] { "upgrade", "--target", targetVersion, "--yes", "--format", "json", "--non-interactive", "--no-banner" };
        }

        static UpdateStage NextStage(UpdateStage stage)
        {
            switch (stage)
            {
                case UpdateStage.PreflightSignature: return UpdateStage.PreflightVersion;
                case UpdateStage.PreflightVersion: return UpdateStage.PreflightCommandSurface;
                case UpdateStage.PreflightCommandSurface: return UpdateStage.Mutation;
                case UpdateStage.Mutation: return UpdateStage.VerifySignature;
                case UpdateStage.VerifySignature: return UpdateStage.VerifyVersion;
                default: return UpdateStage.Complete;
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
}
