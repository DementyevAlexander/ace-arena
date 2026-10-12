using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor.PackageManager;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    internal static class UnityCliPipelineSetup
    {
        public const string PipelinePackageId = "com.unity.pipeline";
        public const string WindowsCliInstallerUrl = "https://public-cdn.cloud.unity3d.com/hub/prod/cli/install.ps1";
        public const string WindowsCliInstallerChannel = "beta";

        const string WslProbeScript = "p=$(command -v unity 2>/dev/null || true); on_path=true; if [ -z \"$p\" ] && [ -x \"$HOME/.local/bin/unity\" ]; then p=\"$HOME/.local/bin/unity\"; on_path=false; fi; if [ -z \"$p\" ]; then printf 'kind=missing\\npath=\\ntarget=\\non_path=false\\n'; exit 0; fi; target=; case \"$p\" in *.exe) k=windows_executable; target=\"$p\";; *) target=$(sed -n '2s/^exec \"\\([^\"]*\\)\" \"\\$@\"$/\\1/p' \"$p\" 2>/dev/null); if [ -n \"$target\" ]; then k=windows_wrapper; else k=linux_binary; fi;; esac; printf 'kind=%s\\npath=%s\\ntarget=%s\\non_path=%s\\n' \"$k\" \"$p\" \"$target\" \"$on_path\"; \"$p\" --version 2>&1";
        const string WslWrapperInstallScript = "set -eu; target=\"$HOME/.local/bin/unity\"; windows_cli=\"$1\"; case \"$windows_cli\" in /mnt/?/*/unity.exe) ;; *) printf 'invalid Windows CLI path\\n' >&2; exit 24;; esac; desired=$(printf '%s\\n' '#!/usr/bin/env bash' \"exec \\\"$windows_cli\\\" \\\"\\$@\\\"\"); if [ -L \"$target\" ]; then printf 'existing wrapper target is a symlink\\n' >&2; exit 23; fi; if [ -e \"$target\" ]; then current=$(cat \"$target\"); if [ \"$current\" = \"$desired\" ]; then exit 0; fi; printf 'existing ~/.local/bin/unity was not overwritten\\n' >&2; exit 23; fi; mkdir -p \"$HOME/.local/bin\"; umask 077; tmp=\"$target.sgg-tmp-$$\"; printf '%s\\n' \"$desired\" > \"$tmp\"; chmod 700 \"$tmp\"; mv \"$tmp\" \"$target\"";
        static WindowsUnityCliStatus s_windowsStatus = WindowsUnityCliStatus.NotChecked();
        static WslUnityCliStatus s_wslStatus = WslUnityCliStatus.NotChecked();

        public static WindowsUnityCliStatus WindowsStatus => s_windowsStatus;
        public static WslUnityCliStatus WslStatus => s_wslStatus;
        public static string OfficialWindowsInstallPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Unity", "bin", "unity.exe");

        internal static void SetWindowsStatus(WindowsUnityCliStatus status)
        {
            s_windowsStatus = status ?? WindowsUnityCliStatus.NotChecked();
        }

        public static string FindUnvalidatedWindowsCandidate()
        {
            return File.Exists(OfficialWindowsInstallPath) ? OfficialWindowsInstallPath : string.Empty;
        }

        internal static bool TryGetOfficialWindowsCliPresence(out bool present)
        {
            return TryGetCliPresence(OfficialWindowsInstallPath, out present);
        }

        internal static bool TryGetCliPresence(string path, out bool present)
        {
            present = false;
            if (!IsPathWithoutReparsePoints(path)) return false;
            try
            {
                var attributes = File.GetAttributes(path);
                present = true;
                return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
            }
            catch (FileNotFoundException) { return true; }
            catch (DirectoryNotFoundException) { return true; }
            catch { return false; }
        }

        internal static bool IsPathWithoutReparsePoints(string path)
        {
            try
            {
                if (!Path.IsPathRooted(path)) return false;
                var fullPath = Path.GetFullPath(path);
                var current = fullPath;
                while (!string.IsNullOrEmpty(current))
                {
                    try
                    {
                        var attributes = File.GetAttributes(current);
                        if ((attributes & FileAttributes.ReparsePoint) != 0
                            || (current != fullPath && (attributes & FileAttributes.Directory) == 0)) return false;
                    }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                    var parent = Path.GetDirectoryName(current);
                    if (parent == current) break;
                    current = parent;
                }
                return true;
            }
            catch { return false; }
        }

        public static WindowsUnityCliStatus ProbeWindowsCli()
        {
            var candidates = new List<string>();
            if (File.Exists(OfficialWindowsInstallPath))
            {
                candidates.Add(OfficialWindowsInstallPath);
            }

            if (ProcessUtil.TryRun("where.exe", "unity.exe", null, out var stdout, out _, 5000))
            {
                var pathCandidates = stdout.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                for (var index = 0; index < pathCandidates.Length && candidates.Count < 3; index++)
                {
                    var candidate = pathCandidates[index].Trim();
                    if (!ContainsPath(candidates, candidate)) candidates.Add(candidate);
                }
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                var candidate = candidates[index];
                if (!Path.IsPathRooted(candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                var arguments = BoundedProcessOperation.BuildArguments(new[] { "--version", "--no-banner" });
                if (!ProcessUtil.TryRun(candidate, arguments, null, out var versionOutput, out _, 3000))
                {
                    continue;
                }

                if (TryExtractCliVersion(versionOutput, out var version))
                {
                    return s_windowsStatus = WindowsUnityCliStatus.Ready(candidate, version);
                }
            }

            return s_windowsStatus = WindowsUnityCliStatus.Failed(candidates.Count == 0
                ? "Unity CLI was not found in the official install location or the Editor process PATH."
                : "Unity CLI candidates were found but did not pass version validation.");
        }

        public static WindowsUnityCliStatus ProbeOfficialWindowsCli()
        {
            if (!File.Exists(OfficialWindowsInstallPath))
            {
                return s_windowsStatus = WindowsUnityCliStatus.Failed("The official Windows Unity CLI executable was not created at the expected install location.");
            }

            var arguments = BoundedProcessOperation.BuildArguments(new[] { "--version", "--no-banner" });
            if (!ProcessUtil.TryRun(OfficialWindowsInstallPath, arguments, null, out var versionOutput, out _, 3000)
                || !TryExtractCliVersion(versionOutput, out var version))
            {
                return s_windowsStatus = WindowsUnityCliStatus.Failed("The official Windows Unity CLI executable did not pass version validation.");
            }

            return s_windowsStatus = WindowsUnityCliStatus.Ready(OfficialWindowsInstallPath, version);
        }

        public static bool TryStartOfficialWindowsCliProbe(out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            error = string.Empty;
            if (!File.Exists(OfficialWindowsInstallPath))
            {
                s_windowsStatus = WindowsUnityCliStatus.Failed("The official Windows Unity CLI executable was not found.");
                error = s_windowsStatus.Message;
                return false;
            }

            return BoundedProcessOperation.TryStart(
                OfficialWindowsInstallPath,
                new[] { "--version", "--no-banner" },
                null,
                TimeSpan.FromSeconds(5),
                out operation,
                out error);
        }

        public static WindowsUnityCliStatus CompleteOfficialWindowsCliProbe(BoundedProcessOperation operation)
        {
            if (operation == null || operation.TimedOut || operation.ExitCode != 0
                || !TryExtractCliVersion(operation.StandardOutput, out var version))
            {
                return s_windowsStatus = WindowsUnityCliStatus.Failed("The official Windows Unity CLI executable did not pass version validation.");
            }

            return s_windowsStatus = WindowsUnityCliStatus.Ready(OfficialWindowsInstallPath, version);
        }

        public static bool TryStartWindowsCliInstall(out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            error = string.Empty;
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = "Windows Unity CLI installation is available in Windows Editor only.";
                return false;
            }
            if (!Environment.Is64BitProcess)
            {
                error = "Windows Unity CLI installation requires a 64-bit Windows Editor process.";
                return false;
            }

            if (!TryGetOfficialWindowsCliPresence(out var present) || present)
            {
                error = "Install requires a known absent official Windows CLI. Detect/validate an existing target and use Update instead; it will not be overwritten.";
                return false;
            }

            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var powerShell = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            if (string.IsNullOrEmpty(systemDirectory) || !File.Exists(powerShell))
            {
                error = "64-bit Windows PowerShell was not found in the system directory.";
                return false;
            }

            return BoundedProcessOperation.TryStart(
                powerShell,
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy",
                    "Bypass",
                    "-Command",
                    BuildWindowsCliInstallScript(),
                },
                null,
                TimeSpan.FromMinutes(5),
                out operation,
                out error);
        }

        internal static string BuildWindowsCliInstallScript()
        {
            var expectedHome = Path.GetDirectoryName(Path.GetDirectoryName(OfficialWindowsInstallPath)).Replace("'", "''");
            return "$ErrorActionPreference='Stop';"
                + "$ProgressPreference='SilentlyContinue';"
                + "$expectedHome='" + expectedHome + "';"
                + "$env:UNITY_CLI_HOME=$expectedHome;"
                + "$destination=Join-Path $expectedHome 'bin\\unity.exe';"
                + "function Assert-SafeDestination{$current=$destination;while(-not [string]::IsNullOrEmpty($current)){try{$item=Get-Item -LiteralPath $current -Force -ErrorAction Stop;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Install destination contains a reparse point'}}catch{if($_.Exception -isnot [System.Management.Automation.ItemNotFoundException]){throw}};$parent=[IO.Path]::GetDirectoryName($current);if($parent -ceq $current){break};$current=$parent};if(Test-Path -LiteralPath $destination){throw 'Existing Unity CLI target will not be overwritten by Install'}};"
                + "Assert-SafeDestination;"
                + "[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12;"
                + "$installer=Join-Path ([IO.Path]::GetTempPath()) ('unity-cli-install-'+[guid]::NewGuid().ToString('N')+'.ps1');"
                + "try {"
                + "Invoke-WebRequest -UseBasicParsing -Uri '" + WindowsCliInstallerUrl + "' -OutFile $installer;"
                + "Assert-SafeDestination;"
                + "& $installer -Channel '" + WindowsCliInstallerChannel + "';"
                + "if ($null -ne $LASTEXITCODE -and $LASTEXITCODE -ne 0) { exit $LASTEXITCODE }"
                + "} finally {"
                + "Remove-Item -LiteralPath $installer -Force -ErrorAction SilentlyContinue"
                + "}";
        }

        public static WslUnityCliStatus ProbeDefaultWslCli()
        {
            var arguments = BoundedProcessOperation.BuildArguments(new[] { "-e", "sh", "-lc", WslProbeScript });
            if (!ProcessUtil.TryRun("wsl.exe", arguments, null, out var stdout, out var stderr, 5000))
            {
                return s_wslStatus = WslUnityCliStatus.Failed(string.IsNullOrWhiteSpace(stderr) ? "Default WSL distribution probe failed." : BoundedProcessOperation.Sanitize(stderr));
            }

            return s_wslStatus = ParseWslProbe(stdout);
        }

        public static UnityPipelineStatus GetPipelineStatus(string projectRoot)
        {
            var status = new UnityPipelineStatus
            {
                EditorSupported = IsPipelineEditorSupported(Application.unityVersion),
                EditorCompatibilityState = IsPipelineEditorSupported(Application.unityVersion) ? "supported" : "unsupported",
                CliCompatibilityState = "unknown",
                ManifestVersion = ReadManifestVersion(projectRoot, PipelinePackageId),
            };
            var packages = PackageInfo.GetAllRegisteredPackages();
            for (var index = 0; index < packages.Length; index++)
            {
                if (!string.Equals(packages[index].name, PipelinePackageId, StringComparison.Ordinal))
                {
                    continue;
                }

                status.Registered = true;
                status.Version = BoundedLocalVersion(packages[index].version);
                status.Source = packages[index].source.ToString();
                status.ResolvedPath = packages[index].resolvedPath ?? string.Empty;
                break;
            }

            status.Update = ComponentUpdateStatus.NotChecked(
                status.Version,
                "",
                "official_unity_pipeline_list_versions",
                UnityCliUpdateCheck.SourceIdentity(PipelinePackageId + "|pipeline list-versions"));
            if (PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, "Packages/manifest.json", out var manifestFingerprint)
                && PackageUpdateMonitor.TryBuildProjectPackageFileFingerprintWithinRoot(projectRoot, "Packages/packages-lock.json", out var lockFingerprint))
            {
                status.ProjectPackagesIdentitySha256 = UnityCliUpdateCheck.SourceIdentity(manifestFingerprint + lockFingerprint);
            }

            return status;
        }

        public static bool TryStartPipelineInstall(string projectRoot, out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            error = string.Empty;
            var status = GetPipelineStatus(projectRoot);
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                error = "Pipeline setup currently supports Windows Editor only.";
                return false;
            }

            if (!status.EditorSupported)
            {
                error = "com.unity.pipeline requires Unity 6.0 or newer.";
                return false;
            }

            if (status.Registered || !string.IsNullOrEmpty(status.ManifestVersion))
            {
                error = status.Registered ? PipelinePackageId + " is already installed." : PipelinePackageId + " is already declared and may still be resolving/importing.";
                return false;
            }

            if (!TrustedOfficialWindowsCliReady(out error))
            {
                return false;
            }

            var root = Path.GetFullPath(projectRoot);
            if (!Directory.Exists(root))
            {
                error = "Unity project root does not exist.";
                return false;
            }

            return BoundedProcessOperation.TryStart(
                WindowsCliAuthenticode.PowerShellPath,
                WindowsCliAuthenticode.BuildBoundCommandArguments(s_windowsStatus.ExecutablePath,
                    s_windowsStatus.ExecutableSignatureThumbprint, s_windowsStatus.ExecutableSha256,
                    new[] { "pipeline", "install", "--project-path", root }),
                root,
                TimeSpan.FromMinutes(5),
                out operation,
                out error);
        }

        public static bool TryStartPipelineVerification(string projectRoot, out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            error = string.Empty;
            if (!TrustedOfficialWindowsCliReady(out error))
            {
                return false;
            }

            var root = Path.GetFullPath(projectRoot);
            return BoundedProcessOperation.TryStart(
                WindowsCliAuthenticode.PowerShellPath,
                WindowsCliAuthenticode.BuildBoundCommandArguments(s_windowsStatus.ExecutablePath,
                    s_windowsStatus.ExecutableSignatureThumbprint, s_windowsStatus.ExecutableSha256,
                    new[] { "pipeline", "list", "--format", "json", "--non-interactive", "--no-banner" }),
                root,
                TimeSpan.FromSeconds(30),
                out operation,
                out error);
        }

        public static bool TryStartWslWrapperInstall(out BoundedProcessOperation operation, out string error)
        {
            operation = null;
            error = string.Empty;
            if (!TrustedOfficialWindowsCliReady(out error))
            {
                return false;
            }

            var wslPath = WindowsPathToWsl(s_windowsStatus.ExecutablePath);
            if (!IsSafeWslWrapperTarget(wslPath))
            {
                error = "The validated Windows Unity CLI path cannot be represented safely in WSL.";
                return false;
            }

            return BoundedProcessOperation.TryStart(
                "wsl.exe",
                new[] { "-e", "sh", "-lc", WslWrapperInstallScript, "--", wslPath },
                null,
                TimeSpan.FromSeconds(30),
                out operation,
                out error);
        }

        static bool TrustedOfficialWindowsCliReady(out string error)
        {
            error = string.Empty;
            if (WindowsCliUpdateCoordinator.RequiresLocalRefresh)
            {
                error = "Refresh exact local Windows CLI state before another CLI-dependent action.";
                return false;
            }
            if (!s_windowsStatus.Installed
                || !File.Exists(s_windowsStatus.ExecutablePath)
                || !string.Equals(NormalizeComparablePath(s_windowsStatus.ExecutablePath), NormalizeComparablePath(OfficialWindowsInstallPath), StringComparison.OrdinalIgnoreCase)
                || !WindowsCliUpdateCoordinator.HasTrustedSignature(s_windowsStatus))
            {
                error = "Run a fresh manual update check to validate the exact official Windows Unity CLI path and Authenticode signature.";
                return false;
            }
            return true;
        }

        internal static bool IsPipelineEditorSupported(string unityVersion)
        {
            if (string.IsNullOrWhiteSpace(unityVersion))
            {
                return false;
            }

            var match = Regex.Match(unityVersion, "^(\\d+)");
            return match.Success && int.TryParse(match.Groups[1].Value, out var major) && major >= 6000;
        }

        internal static bool TryExtractCliVersion(string output, out string version)
        {
            version = string.Empty;
            if (string.IsNullOrEmpty(output) || output.Length > 4096)
            {
                return false;
            }

            var matches = Regex.Matches(
                output,
                "(?<![0-9A-Za-z.+-])(?:v)?(\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?(?:\\+[0-9A-Za-z-]+(?:\\.[0-9A-Za-z-]+)*)?)(?![0-9A-Za-z.+-])");
            if (matches.Count != 1)
            {
                return false;
            }

            var candidate = matches[0].Groups[1].Value;
            if (!UnityCliUpdateCheck.TryParseSemVer(candidate, out _))
            {
                return false;
            }

            version = candidate;
            return true;
        }

        internal static WslUnityCliStatus ParseWslProbe(string output)
        {
            var kind = MatchValue(output, "kind");
            var path = MatchValue(output, "path");
            TryExtractCliVersion(output, out var version);
            switch (kind)
            {
                case "windows_executable": return WslUnityCliStatus.Found("Windows CLI", path, MatchValue(output, "target"), version, true, MatchValue(output, "on_path") == "true");
                case "windows_wrapper": return WslUnityCliStatus.Found("Wrapper to Windows CLI", path, MatchValue(output, "target"), version, true, MatchValue(output, "on_path") == "true");
                case "linux_binary": return WslUnityCliStatus.Found("Separate Linux CLI", path, string.Empty, version, false, MatchValue(output, "on_path") == "true");
                case "missing": return WslUnityCliStatus.Found("Not found", string.Empty, string.Empty, string.Empty, false, false);
                default: return WslUnityCliStatus.Failed("WSL returned an unknown Unity CLI probe result.");
            }
        }

        internal static bool PipelineListContainsProject(string output, string projectRoot)
        {
            var expected = NormalizeComparablePath(projectRoot);
            if (string.IsNullOrEmpty(expected))
            {
                return false;
            }

            PipelineListEnvelope envelope;
            try
            {
                envelope = JsonUtility.FromJson<PipelineListEnvelope>(output ?? string.Empty);
            }
            catch
            {
                return false;
            }

            if (envelope == null || !envelope.success)
            {
                return false;
            }

            var instances = envelope.data?.instances;
            if (instances == null)
            {
                return false;
            }

            for (var index = 0; index < instances.Length; index++)
            {
                var instance = instances[index];
                if (instance != null
                    && instance.isRunning
                    && instance.hasPipelinePackage
                    && !string.IsNullOrEmpty(instance.pipelineVersion)
                    && instance.pipelineServer != null
                    && instance.pipelineServer.isReachable
                    && string.Equals(NormalizeComparablePath(instance.projectPath), expected, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static bool WslDirectExecutableMatchesWindows(string wslPath, string windowsPath)
        {
            if (string.IsNullOrWhiteSpace(wslPath) || string.IsNullOrWhiteSpace(windowsPath))
            {
                return false;
            }

            var match = Regex.Match(wslPath.Trim(), "^/mnt/([a-zA-Z])/(.+)$");
            if (!match.Success)
            {
                return false;
            }

            var converted = char.ToUpperInvariant(match.Groups[1].Value[0]) + ":\\" + match.Groups[2].Value.Replace('/', '\\');
            return string.Equals(NormalizeComparablePath(converted), NormalizeComparablePath(windowsPath), StringComparison.OrdinalIgnoreCase);
        }

        internal static string WindowsPathToWsl(string windowsPath)
        {
            if (string.IsNullOrWhiteSpace(windowsPath)) return string.Empty;
            var match = Regex.Match(windowsPath.Trim(), "^([a-zA-Z]):[\\\\/](.+)$");
            if (!match.Success) return string.Empty;
            return "/mnt/" + char.ToLowerInvariant(match.Groups[1].Value[0]) + "/" + match.Groups[2].Value.Replace('\\', '/');
        }

        internal static bool IsSafeWslWrapperTarget(string wslPath)
        {
            return !string.IsNullOrEmpty(wslPath) && Regex.IsMatch(wslPath, "^/mnt/[a-zA-Z]/[A-Za-z0-9_./ -]+/unity\\.exe$");
        }

        static string MatchValue(string text, string key)
        {
            var match = Regex.Match(text ?? string.Empty, "(?:^|\\n)" + Regex.Escape(key) + "=([^\\r\\n]*)");
            return match.Success ? match.Groups[1].Value.Trim() : string.Empty;
        }

        static bool ContainsPath(List<string> paths, string candidate)
        {
            for (var index = 0; index < paths.Count; index++)
            {
                if (string.Equals(paths[index], candidate, StringComparison.OrdinalIgnoreCase)) return true;
            }

            return false;
        }

        static string NormalizeComparablePath(string value)
        {
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim().Replace('/', '\\').TrimEnd('\\');
        }

        internal static string ReadManifestVersion(string projectRoot, string packageId)
        {
            try
            {
                var path = Path.Combine(Path.GetFullPath(projectRoot), "Packages", "manifest.json");
                if ((File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    return "unknown";
                }

                if (new FileInfo(path).Length > 1048576)
                {
                    return "invalid";
                }

                if (!PipelineJsonEvidenceParser.TryReadOptionalStringPath(File.ReadAllText(path, new UTF8Encoding(false, true)), new[] { "dependencies", packageId }, out var version, out var found))
                {
                    return "invalid";
                }
                return found ? BoundedLocalVersion(version) : string.Empty;
            }
            catch (FileNotFoundException) { return string.Empty; }
            catch (DirectoryNotFoundException) { return string.Empty; }
            catch
            {
                return "unknown";
            }
        }

        static string BoundedLocalVersion(string value)
        {
            return UnityCliUpdateCheck.TryParseSemVer(value, out _) ? value : "invalid";
        }

        internal sealed class WindowsUnityCliStatus
        {
            public bool Checked;
            public bool Installed;
            public string ExecutablePath = string.Empty;
            public string Version = string.Empty;
            public string Message = string.Empty;
            public ComponentUpdateStatus Update = ComponentUpdateStatus.NotChecked();
            public string ExecutableSignatureState = "unknown";
            public string ExecutableSigner = string.Empty;
            public string ExecutableSignatureThumbprint = string.Empty;
            public string ExecutableSha256 = string.Empty;

            public static WindowsUnityCliStatus NotChecked() => new WindowsUnityCliStatus { Message = "Not checked. Use Probe Windows/WSL CLI." };
            public static WindowsUnityCliStatus Ready(string path, string version) => new WindowsUnityCliStatus
            {
                Checked = true,
                Installed = true,
                ExecutablePath = path,
                Version = version,
                Message = "Installed " + version,
                Update = ComponentUpdateStatus.NotChecked(version, WindowsCliInstallerChannel, "official_unity_cli_upgrade_check", UnityCliUpdateCheck.SourceIdentity("unity.exe|upgrade|--check|" + WindowsCliInstallerChannel + "|" + UnityCliUpdateCheck.CliPlatform)),
            };
            public static WindowsUnityCliStatus Failed(string message) => new WindowsUnityCliStatus { Checked = true, Message = message };
        }

        internal sealed class WslUnityCliStatus
        {
            public bool Checked;
            public string Kind = "Unknown";
            public string CommandPath = string.Empty;
            public string TargetPath = string.Empty;
            public string Version = string.Empty;
            public bool ResolvesToWindowsCli;
            public bool OnPath;
            public string Message = string.Empty;

            public static WslUnityCliStatus NotChecked() => new WslUnityCliStatus { Message = "Not checked. The default WSL distribution is probed only on explicit request." };
            public static WslUnityCliStatus Found(string kind, string path, string targetPath, string version, bool resolvesToWindows, bool onPath) => new WslUnityCliStatus { Checked = true, Kind = kind, CommandPath = path, TargetPath = targetPath, Version = version, ResolvesToWindowsCli = resolvesToWindows, OnPath = onPath, Message = kind + (string.IsNullOrEmpty(version) ? string.Empty : " " + version) + (onPath || kind == "Not found" ? string.Empty : "; installed wrapper is not on this WSL shell PATH yet") };
            public static WslUnityCliStatus Failed(string message) => new WslUnityCliStatus { Checked = true, Message = message };
        }

        internal sealed class UnityPipelineStatus
        {
            public bool EditorSupported;
            public bool Registered;
            public string ManifestVersion = string.Empty;
            public string Version = string.Empty;
            public string Source = string.Empty;
            public string ResolvedPath = string.Empty;
            public ComponentUpdateStatus Update = ComponentUpdateStatus.NotChecked();
            public string TargetPackageVersion = string.Empty;
            public string EditorCompatibilityState = "unknown";
            public string CliCompatibilityState = "unknown";
            public string AvailableVersionsIdentitySha256 = string.Empty;
            public string CheckedCliVersion = string.Empty;
            public string CheckedEditorVersion = string.Empty;
            public string ProjectPackagesIdentitySha256 = string.Empty;
        }

        [Serializable]
        sealed class PipelineListEnvelope
        {
            public bool success;
            public PipelineListData data;
        }

        [Serializable]
        sealed class PipelineListData
        {
            public PipelineListInstance[] instances;
        }

        [Serializable]
        sealed class PipelineListInstance
        {
            public string projectPath;
            public bool isRunning;
            public bool hasPipelinePackage;
            public string pipelineVersion;
            public PipelineServerStatus pipelineServer;
        }

        [Serializable]
        sealed class PipelineServerStatus
        {
            public bool isReachable;
        }
    }
}
