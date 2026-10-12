using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    public static class GatewayInstaller
    {
        const int MaxCheckoutArtifactBytes = 32 * 1024 * 1024;
        static string GatewayRoot => Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "gateway");
        static string LegacyRuntimeGatewayRoot => Path.Combine(GatewayRoot, "source-dev");
        static string ActiveRuntimePointerPath => Path.Combine(GatewayRoot, "active-runtime.json");
        public static string RuntimeGatewayRoot => ResolveActiveRuntimeRoot();

        public static GatewayInstallStatus GetStatus()
        {
            var repoRoot = UnityMcpPackagePaths.FindGatewayRepoRoot();
            var packageRuntimeRoot = Path.Combine(UnityMcpPackagePaths.PackageResolvedPath, "Editor", "GatewayRuntime");
            var status = new GatewayInstallStatus
            {
                RepoRoot = repoRoot,
                LocalRepoFound = !string.IsNullOrEmpty(repoRoot),
                PackageRuntimeRoot = packageRuntimeRoot,
                PackageRuntimeCliPath = Path.Combine(packageRuntimeRoot, "gateway-cli.mjs"),
                PackageRuntimeMetadataPath = Path.Combine(packageRuntimeRoot, "gateway-runtime.json"),
                RuntimeRoot = RuntimeGatewayRoot,
            };

            if (!File.Exists(status.PackageRuntimeCliPath))
            {
                status.Message = "Bundled gateway runtime is missing from the Unity package.";
                return status;
            }

            var packageMetadata = ReadRuntimeMetadata(status.PackageRuntimeMetadataPath, out var packageMetadataShapeValid);
            if (!ValidateRuntimeMetadata(packageRuntimeRoot, packageMetadata, packageMetadataShapeValid, true, out var packageMetadataMessage)
                || !string.Equals(packageMetadata.version, UnityBridgeRuntime.PackageVersion, StringComparison.Ordinal))
            {
                status.Message = "Bundled gateway runtime compliance metadata is invalid: " + (string.IsNullOrEmpty(packageMetadataMessage) ? "package version mismatch." : packageMetadataMessage);
                return status;
            }
            status.PackageMetadataValid = true;
            status.PackageRuntimeVersion = packageMetadata.version ?? string.Empty;
            status.PackageGatewayVirtualCommandCount = Math.Max(0, packageMetadata.gateway_virtual_commands_count);
            status.PackageCliSha256 = packageMetadata.artifacts.gateway_cli.sha256;
            status.PackageLegalSha256 = packageMetadata.artifacts.legal_comments.sha256;
            status.PackageNoticesSha256 = packageMetadata.artifacts.third_party_notices.sha256;
            status.PackageSbomSha256 = packageMetadata.artifacts.sbom.sha256;
            status.PackageOpenCodePresentationManifestSha256 = packageMetadata.artifacts.opencode_presentation_manifest.sha256;
            status.PackageOpenCodePresentationPackageSha256 = packageMetadata.artifacts.opencode_presentation_package.sha256;
            status.PackageOpenCodePresentationServerSha256 = packageMetadata.artifacts.opencode_presentation_server.sha256;

            PopulateInstalledRuntimeStatus(status, status.RuntimeRoot);
            if (!status.RuntimeFresh && !status.HasBundledCli)
            {
                var candidateRuntimeRoot = RuntimeGatewayRootForHash(status.PackageCliSha256);
                if (!string.Equals(Path.GetFullPath(candidateRuntimeRoot), Path.GetFullPath(status.RuntimeRoot), StringComparison.OrdinalIgnoreCase)
                    && File.Exists(Path.Combine(candidateRuntimeRoot, "gateway-cli.mjs")))
                {
                    PopulateInstalledRuntimeStatus(status, candidateRuntimeRoot);
                }
            }

            status.CanStart = status.HasBundledCli && status.RuntimeFresh;

            if (status.CanStart)
            {
                status.Message = "Bundled gateway runtime is installed in LocalAppData.";
            }
            else if (status.HasBundledCli)
            {
                status.Message = "Installed gateway runtime is stale. Install Runtime to update LocalAppData from the Unity package.";
            }
            else
            {
                status.Message = "Bundled gateway runtime is available. Install runtime to copy it to LocalAppData.";
            }

            return status;
        }

        public static NodeRuntimeInstaller.InstallResult InstallSourceDependencies()
        {
            return InstallSourceDependencies(null);
        }

        internal static NodeRuntimeInstaller.InstallResult InstallSourceDependencies(GatewayRuntimeInstallConfirmation confirmation)
        {
            var gatewayStatus = GetStatus();
            if (!gatewayStatus.PackageMetadataValid)
            {
                return NodeRuntimeInstaller.InstallResult.Fail(gatewayStatus.Message);
            }

            if (!ValidateRuntimeInstallAdmission(gatewayStatus, confirmation, out var admissionMessage))
            {
                return NodeRuntimeInstaller.InstallResult.Fail(admissionMessage);
            }

            var nodeStatus = NodeRuntimeInstaller.GetStatus();
            if (!nodeStatus.IsPortable || string.IsNullOrEmpty(nodeStatus.NodePath) || !File.Exists(nodeStatus.NodePath))
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Install portable Node first. Gateway runtime validation uses portable node.exe.");
            }

            if (!File.Exists(gatewayStatus.PackageRuntimeCliPath))
            {
                return NodeRuntimeInstaller.InstallResult.Fail(gatewayStatus.Message);
            }

            var targetRoot = RuntimeGatewayRootForHash(gatewayStatus.PackageCliSha256);
            var installingRoot = RuntimeGatewayInstallingRootForHash(gatewayStatus.PackageCliSha256);
            var targetStatus = BuildRuntimeStatus(gatewayStatus, targetRoot);
            PopulateInstalledRuntimeStatus(targetStatus, targetRoot);
            if (targetStatus.RuntimeFresh && ValidateRuntime(targetStatus, nodeStatus, out _))
            {
                if (!ValidateConfirmedDowngradeStillCurrent(confirmation, out var staleConfirmationMessage))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail(staleConfirmationMessage);
                }
                WriteActiveRuntimePointer(targetRoot, gatewayStatus);
                TryCleanupOldRuntimeRoots(targetRoot);
                return NodeRuntimeInstaller.InstallResult.Ok("Gateway runtime is already installed and active at " + targetRoot + ". npm was not used.");
            }

            if (TryRefreshSameCliRuntime(gatewayStatus, targetStatus, nodeStatus, confirmation, out var refreshResult))
            {
                return refreshResult;
            }

            try
            {
                EditorUtility.DisplayProgressBar("Installing Gateway Runtime", "Preparing LocalAppData runtime workspace", 0.1f);
                DeleteDirectory(installingRoot);
                CopyBundledRuntime(gatewayStatus, installingRoot);

                EditorUtility.DisplayProgressBar("Installing Gateway Runtime", "Validating bundled gateway runtime", 0.75f);
                var installingStatus = BuildRuntimeStatus(gatewayStatus, installingRoot);
                PopulateInstalledRuntimeStatus(installingStatus, installingRoot);
                if (!ValidateRuntime(installingStatus, nodeStatus, out var validationMessage))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail("Gateway runtime validation failed: " + validationMessage);
                }

                EditorUtility.DisplayProgressBar("Installing Gateway Runtime", "Activating runtime workspace", 0.98f);
                if (!ValidateConfirmedDowngradeStillCurrent(confirmation, out var staleConfirmationMessage))
                {
                    return NodeRuntimeInstaller.InstallResult.Fail(staleConfirmationMessage);
                }
                DeleteDirectory(targetRoot);
                Directory.Move(installingRoot, targetRoot);
                WriteActiveRuntimePointer(targetRoot, gatewayStatus);
                TryCleanupOldRuntimeRoots(targetRoot);

                return NodeRuntimeInstaller.InstallResult.Ok("Gateway runtime installed and validated in " + targetRoot + ". npm was not used. Restart the gateway to use this runtime if it is already running.");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        internal static bool TryCompareInstalledAndPackageVersions(GatewayInstallStatus status, out int comparison)
        {
            comparison = 0;
            return status != null
                && UnityCliUpdateCheck.TryCompareSemVer(status.InstalledRuntimeVersion, status.PackageRuntimeVersion, out comparison);
        }

        internal static GatewayRuntimeInstallConfirmation CreateRuntimeDowngradeConfirmation(GatewayInstallStatus status)
        {
            return new GatewayRuntimeInstallConfirmation(status);
        }

        internal static bool ValidateRuntimeInstallAdmission(GatewayInstallStatus status, GatewayRuntimeInstallConfirmation confirmation, out string message)
        {
            message = string.Empty;
            if (status == null)
            {
                message = "Gateway runtime status is unavailable. No files were changed.";
                return false;
            }

            if (!status.HasBundledCli || status.RuntimeFresh)
            {
                return true;
            }

            if (!TryCompareInstalledAndPackageVersions(status, out var comparison))
            {
                message = "Gateway runtime version direction could not be determined. No files were changed; repair or reinstall the runtime metadata before updating.";
                return false;
            }

            if (comparison <= 0)
            {
                return true;
            }

            if (confirmation == null)
            {
                message = "The installed Gateway runtime is newer than this Unity package. No files were changed; explicitly confirm the downgrade in the MCP Window.";
                return false;
            }

            if (!confirmation.Matches(status))
            {
                message = "Gateway runtime versions or content changed after downgrade confirmation. No files were changed; refresh status and confirm the current transition.";
                return false;
            }

            return true;
        }

        static bool ValidateConfirmedDowngradeStillCurrent(GatewayRuntimeInstallConfirmation confirmation, out string message)
        {
            message = string.Empty;
            return confirmation == null || ValidateRuntimeInstallAdmission(GetStatus(), confirmation, out message);
        }

        public static NodeRuntimeInstaller.InstallResult EnsureUserConfigFiles(GatewayInstallStatus status = null)
        {
            status = status ?? GetStatus();
            try
            {
                Directory.CreateDirectory(UnityMcpPackagePaths.UserConfigDirectory);
                CopyExampleIfMissing(status.ExampleConfigPath, status.ConfigPath);
                CopyExampleIfMissing(status.ExamplePolicyPath, status.PolicyPath);
                return NodeRuntimeInstaller.InstallResult.Ok("User Gateway config is ready at " + UnityMcpPackagePaths.UserConfigDirectory + ". Policy-only changes reload live; restart the Gateway after config, feature, approval, token or network changes.");
            }
            catch (Exception exception)
            {
                return NodeRuntimeInstaller.InstallResult.Fail("Could not create user Gateway config: " + exception.Message);
            }
        }

        static GatewayInstallStatus BuildRuntimeStatus(string repoRoot, string packageRuntimeRoot, string runtimeRoot)
        {
            return new GatewayInstallStatus
            {
                RepoRoot = repoRoot,
                PackageRuntimeRoot = packageRuntimeRoot,
                PackageRuntimeCliPath = Path.Combine(packageRuntimeRoot, "gateway-cli.mjs"),
                PackageRuntimeMetadataPath = Path.Combine(packageRuntimeRoot, "gateway-runtime.json"),
                RuntimeRoot = runtimeRoot,
                LocalRepoFound = !string.IsNullOrEmpty(repoRoot),
                BundledCliPath = Path.Combine(runtimeRoot, "gateway-cli.mjs"),
                RuntimeMetadataPath = Path.Combine(runtimeRoot, "gateway-runtime.json"),
                ExampleConfigPath = Path.Combine(runtimeRoot, "config", "gateway.example.json"),
                ExamplePolicyPath = Path.Combine(runtimeRoot, "config", "policy.example.json"),
                ConfigPath = UnityMcpPackagePaths.UserGatewayConfigPath,
                PolicyPath = UnityMcpPackagePaths.UserPolicyConfigPath,
                UserConfigReady = File.Exists(UnityMcpPackagePaths.UserGatewayConfigPath) && File.Exists(UnityMcpPackagePaths.UserPolicyConfigPath),
            };
        }

        static GatewayInstallStatus BuildRuntimeStatus(GatewayInstallStatus packageStatus, string runtimeRoot)
        {
            var status = BuildRuntimeStatus(packageStatus.RepoRoot, packageStatus.PackageRuntimeRoot, runtimeRoot);
            status.PackageRuntimeVersion = packageStatus.PackageRuntimeVersion;
            status.PackageCliSha256 = packageStatus.PackageCliSha256;
            status.PackageGatewayVirtualCommandCount = packageStatus.PackageGatewayVirtualCommandCount;
            status.PackageMetadataValid = packageStatus.PackageMetadataValid;
            status.PackageLegalSha256 = packageStatus.PackageLegalSha256;
            status.PackageNoticesSha256 = packageStatus.PackageNoticesSha256;
            status.PackageSbomSha256 = packageStatus.PackageSbomSha256;
            status.PackageOpenCodePresentationManifestSha256 = packageStatus.PackageOpenCodePresentationManifestSha256;
            status.PackageOpenCodePresentationPackageSha256 = packageStatus.PackageOpenCodePresentationPackageSha256;
            status.PackageOpenCodePresentationServerSha256 = packageStatus.PackageOpenCodePresentationServerSha256;
            return status;
        }

        static bool TryRefreshSameCliRuntime(GatewayInstallStatus packageStatus, GatewayInstallStatus targetStatus, NodeRuntimeInstaller.NodeRuntimeStatus nodeStatus, GatewayRuntimeInstallConfirmation confirmation, out NodeRuntimeInstaller.InstallResult result)
        {
            result = null;
            if (!RuntimeCliMatchesPackage(targetStatus))
            {
                return false;
            }

            try
            {
                if (!ValidateConfirmedDowngradeStillCurrent(confirmation, out var staleConfirmationMessage))
                {
                    result = NodeRuntimeInstaller.InstallResult.Fail(staleConfirmationMessage);
                    return true;
                }
                CopyBundledRuntimeSidecarFiles(packageStatus, targetStatus.RuntimeRoot);
                PopulateInstalledRuntimeStatus(targetStatus, targetStatus.RuntimeRoot);
                if (!targetStatus.RuntimeFresh)
                {
                    result = NodeRuntimeInstaller.InstallResult.Fail("Gateway runtime sidecar refresh did not make LocalAppData runtime current. Stop the gateway and install runtime again.");
                    return true;
                }

                if (!ValidateRuntime(targetStatus, nodeStatus, out var validationMessage))
                {
                    result = NodeRuntimeInstaller.InstallResult.Fail("Gateway runtime validation failed after sidecar refresh: " + validationMessage);
                    return true;
                }

                WriteActiveRuntimePointer(targetStatus.RuntimeRoot, packageStatus);
                TryCleanupOldRuntimeRoots(targetStatus.RuntimeRoot);
                result = NodeRuntimeInstaller.InstallResult.Ok("Gateway runtime metadata and config refreshed in " + targetStatus.RuntimeRoot + ". npm was not used. Restart the gateway to use the updated runtime version if it is already running.");
                return true;
            }
            catch (Exception exception)
            {
                result = NodeRuntimeInstaller.InstallResult.Fail("Could not refresh gateway runtime metadata/config in " + targetStatus.RuntimeRoot + ": " + exception.Message);
                return true;
            }
        }

        static void PopulateInstalledRuntimeStatus(GatewayInstallStatus status, string runtimeRoot)
        {
            status.RuntimeRoot = runtimeRoot;
            status.BundledCliPath = Path.Combine(runtimeRoot, "gateway-cli.mjs");
            status.RuntimeMetadataPath = Path.Combine(runtimeRoot, "gateway-runtime.json");
            status.ExampleConfigPath = Path.Combine(runtimeRoot, "config", "gateway.example.json");
            status.ExamplePolicyPath = Path.Combine(runtimeRoot, "config", "policy.example.json");
            status.ConfigPath = UnityMcpPackagePaths.UserGatewayConfigPath;
            status.PolicyPath = UnityMcpPackagePaths.UserPolicyConfigPath;
            status.UserConfigReady = File.Exists(status.ConfigPath) && File.Exists(status.PolicyPath);
            status.HasBundledCli = File.Exists(status.BundledCliPath);

            var installedMetadata = ReadRuntimeMetadata(status.RuntimeMetadataPath, out var installedMetadataShapeValid);
            status.InstalledRuntimeVersion = installedMetadata.version ?? string.Empty;
            status.InstalledGatewayVirtualCommandCount = Math.Max(0, installedMetadata.gateway_virtual_commands_count);
            status.InstalledCliSha256 = ComputeSha256(status.BundledCliPath);
            status.InstalledMetadataValid = ValidateRuntimeMetadata(runtimeRoot, installedMetadata, installedMetadataShapeValid, false, out _);
            if (status.InstalledMetadataValid)
            {
                status.InstalledLegalSha256 = installedMetadata.artifacts.legal_comments.sha256;
                status.InstalledNoticesSha256 = installedMetadata.artifacts.third_party_notices.sha256;
                status.InstalledSbomSha256 = installedMetadata.artifacts.sbom.sha256;
                status.InstalledOpenCodePresentationManifestSha256 = installedMetadata.artifacts.opencode_presentation_manifest.sha256;
                status.InstalledOpenCodePresentationPackageSha256 = installedMetadata.artifacts.opencode_presentation_package.sha256;
                status.InstalledOpenCodePresentationServerSha256 = installedMetadata.artifacts.opencode_presentation_server.sha256;
            }
            status.RuntimeFresh = RuntimeMatchesPackage(status);
        }

        public static bool ValidateRuntime(GatewayInstallStatus status, NodeRuntimeInstaller.NodeRuntimeStatus nodeStatus, out string message)
        {
            message = string.Empty;
            if (!nodeStatus.IsAvailable || string.IsNullOrEmpty(nodeStatus.NodePath))
            {
                message = "Node 22+ is required to validate gateway runtime.";
                return false;
            }

            if (!File.Exists(status.BundledCliPath))
            {
                message = "Bundled gateway entrypoint is missing: " + status.BundledCliPath;
                return false;
            }

            if (!status.RuntimeFresh)
            {
                message = "Gateway runtime compliance artifacts are missing, stale, or tampered.";
                return false;
            }

            var arguments = ProcessUtil.Quote(status.BundledCliPath) + " --help";
            var pathPrepend = nodeStatus.IsPortable ? Path.GetDirectoryName(nodeStatus.NodePath) : null;
            var result = ProcessUtil.TryRun(nodeStatus.NodePath, arguments, status.RuntimeRoot, out var stdout, out var stderr, 20000, pathPrepend);
            message = result
                ? "Portable Node can execute the bundled gateway CLI."
                : TrimForDialog(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
            return result;
        }

        static void CopyBundledRuntime(GatewayInstallStatus packageStatus, string destinationRoot)
        {
            CopyDirectory(packageStatus.PackageRuntimeRoot, destinationRoot);
            CanonicalizeCopiedRuntimeArtifacts(packageStatus, destinationRoot, false);
        }

        static void CopyBundledRuntimeSidecarFiles(GatewayInstallStatus packageStatus, string destinationRoot)
        {
            CopyDirectory(packageStatus.PackageRuntimeRoot, destinationRoot, "gateway-cli.mjs");
            CanonicalizeCopiedRuntimeArtifacts(packageStatus, destinationRoot, true);
        }

        static void CanonicalizeCopiedRuntimeArtifacts(GatewayInstallStatus packageStatus, string destinationRoot, bool skipCli)
        {
            if (!skipCli)
            {
                CanonicalizeCopiedArtifact(packageStatus.PackageRuntimeRoot, destinationRoot, "gateway-cli.mjs", packageStatus.PackageCliSha256);
            }
            CanonicalizeCopiedArtifact(packageStatus.PackageRuntimeRoot, destinationRoot, "gateway-cli.LEGAL.txt", packageStatus.PackageLegalSha256);
            CanonicalizeCopiedArtifact(packageStatus.PackageRuntimeRoot, destinationRoot, "THIRD_PARTY_NOTICES.md", packageStatus.PackageNoticesSha256);
            CanonicalizeCopiedArtifact(packageStatus.PackageRuntimeRoot, destinationRoot, "sbom.cdx.json", packageStatus.PackageSbomSha256);
            CanonicalizeCopiedArtifact(packageStatus.PackageRuntimeRoot, destinationRoot, "opencode-presentation/sgg-plugin-manifest.json", packageStatus.PackageOpenCodePresentationManifestSha256);
            CanonicalizeCopiedArtifact(packageStatus.PackageRuntimeRoot, destinationRoot, "opencode-presentation/package.json", packageStatus.PackageOpenCodePresentationPackageSha256);
            CanonicalizeCopiedArtifact(packageStatus.PackageRuntimeRoot, destinationRoot, "opencode-presentation/server.js", packageStatus.PackageOpenCodePresentationServerSha256);
        }

        internal static void CanonicalizeCopiedArtifact(string packageRuntimeRoot, string destinationRoot, string relativePath, string expectedSha256)
        {
            var sourcePath = Path.Combine(packageRuntimeRoot, relativePath);
            if (!TryReadCanonicalCheckoutArtifact(sourcePath, expectedSha256, out var canonicalBytes))
            {
                throw new InvalidDataException("Bundled runtime artifact changed or failed SHA-256 validation before installation: " + relativePath);
            }
            if (canonicalBytes != null)
            {
                var destinationPath = Path.Combine(destinationRoot, relativePath);
                var attributes = File.GetAttributes(destinationPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(destinationPath, attributes & ~FileAttributes.ReadOnly);
                }
                File.WriteAllBytes(destinationPath, canonicalBytes);
            }
        }

        static void CopyExampleIfMissing(string sourcePath, string destinationPath)
        {
            if (File.Exists(destinationPath))
            {
                return;
            }

            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
            {
                throw new FileNotFoundException("Bundled config example is missing", sourcePath);
            }

            var directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }
            File.Copy(sourcePath, destinationPath, false);
        }

        static void CopyDirectory(string sourceRoot, string destinationRoot)
        {
            CopyDirectory(sourceRoot, destinationRoot, string.Empty);
        }

        static void CopyDirectory(string sourceRoot, string destinationRoot, string skipRelativePath)
        {
            Directory.CreateDirectory(destinationRoot);
            foreach (var directory in Directory.GetDirectories(sourceRoot, "*", SearchOption.AllDirectories))
            {
                Directory.CreateDirectory(Path.Combine(destinationRoot, directory.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }

            foreach (var file in Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories))
            {
                var relativePath = file.Substring(sourceRoot.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (RelativePathsEqual(relativePath, skipRelativePath))
                {
                    continue;
                }

                var destinationPath = Path.Combine(destinationRoot, relativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                File.Copy(file, destinationPath, true);
            }
        }

        static bool RelativePathsEqual(string left, string right)
        {
            if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
            {
                return false;
            }

            return string.Equals(left.Replace('\\', '/'), right.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        static void DeleteDirectory(string directoryPath)
        {
            if (string.IsNullOrEmpty(directoryPath) || !Directory.Exists(directoryPath))
            {
                return;
            }

            const int attempts = 3;
            Exception lastException = null;
            for (var attempt = 1; attempt <= attempts; attempt++)
            {
                try
                {
                    ClearReadOnlyAttributes(directoryPath);
                    Directory.Delete(directoryPath, true);
                    return;
                }
                catch (Exception exception)
                {
                    lastException = exception;
                    System.Threading.Thread.Sleep(250 * attempt);
                }
            }

            throw new InvalidOperationException("Could not remove runtime directory at " + directoryPath + ": " + lastException?.Message + ". Stop the gateway if it is running and try again.");
        }

        static void ClearReadOnlyAttributes(string root)
        {
            foreach (var file in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            {
                try
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }
                catch
                {
                    // Best effort: Directory.Delete will report the actual failure if this matters.
                }
            }
        }

        static string TrimForDialog(string value)
        {
            var text = string.IsNullOrWhiteSpace(value) ? "No output" : value.Trim();
            var errorIndex = text.IndexOf("\nError:", StringComparison.Ordinal);
            if (errorIndex >= 0)
            {
                text = text.Substring(errorIndex + 1).Trim();
            }

            const int maxLength = 1800;
            return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "\n...";
        }

        static string ResolveActiveRuntimeRoot()
        {
            var pointer = ReadActiveRuntimePointer();
            if (!string.IsNullOrEmpty(pointer.runtime_root) && File.Exists(Path.Combine(pointer.runtime_root, "gateway-cli.mjs")))
            {
                return pointer.runtime_root;
            }

            return LegacyRuntimeGatewayRoot;
        }

        static string RuntimeGatewayRootForHash(string sha256)
        {
            return Path.Combine(GatewayRoot, "source-" + ShortHash(sha256));
        }

        static string RuntimeGatewayInstallingRootForHash(string sha256)
        {
            return Path.Combine(GatewayRoot, "source-" + ShortHash(sha256) + "-installing");
        }

        static string ShortHash(string sha256)
        {
            return string.IsNullOrEmpty(sha256) ? "unknown" : sha256.Substring(0, Math.Min(12, sha256.Length)).ToLowerInvariant();
        }

        static void WriteActiveRuntimePointer(string runtimeRoot, GatewayInstallStatus packageStatus)
        {
            Directory.CreateDirectory(GatewayRoot);
            var pointer = new GatewayRuntimePointer
            {
                runtime_root = runtimeRoot,
                version = packageStatus.PackageRuntimeVersion,
                gateway_cli_sha256 = packageStatus.PackageCliSha256,
            };
            File.WriteAllText(ActiveRuntimePointerPath, JsonUtility.ToJson(pointer, true));
        }

        static GatewayRuntimePointer ReadActiveRuntimePointer()
        {
            if (!File.Exists(ActiveRuntimePointerPath))
            {
                return new GatewayRuntimePointer();
            }

            try
            {
                return JsonUtility.FromJson<GatewayRuntimePointer>(File.ReadAllText(ActiveRuntimePointerPath)) ?? new GatewayRuntimePointer();
            }
            catch
            {
                return new GatewayRuntimePointer();
            }
        }

        static void TryCleanupOldRuntimeRoots(string activeRuntimeRoot)
        {
            if (!Directory.Exists(GatewayRoot))
            {
                return;
            }

            var activeFullPath = Path.GetFullPath(activeRuntimeRoot);
            foreach (var directory in Directory.GetDirectories(GatewayRoot, "source-*", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(Path.GetFullPath(directory), activeFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                TryDeleteDirectory(directory);
            }
        }

        static bool TryDeleteDirectory(string directoryPath)
        {
            try
            {
                DeleteDirectory(directoryPath);
                return true;
            }
            catch
            {
                return false;
            }
        }

        static bool RuntimeMatchesPackage(GatewayInstallStatus status)
        {
            if (status == null || !status.HasBundledCli || !status.PackageMetadataValid || !status.InstalledMetadataValid)
            {
                return false;
            }
            return RuntimeStateMatchesPackage(status, FilesEqual(status.PackageRuntimeMetadataPath, status.RuntimeMetadataPath));
        }

        internal static bool RuntimeStateMatchesPackage(GatewayInstallStatus status, bool metadataFilesEqual)
        {
            return status != null
                && status.HasBundledCli
                && status.PackageMetadataValid
                && status.InstalledMetadataValid
                && string.Equals(status.PackageRuntimeVersion, status.InstalledRuntimeVersion, StringComparison.Ordinal)
                && status.PackageGatewayVirtualCommandCount == status.InstalledGatewayVirtualCommandCount
                && metadataFilesEqual
                && HashesEqual(status.PackageCliSha256, status.InstalledCliSha256)
                && HashesEqual(status.PackageLegalSha256, status.InstalledLegalSha256)
                && HashesEqual(status.PackageNoticesSha256, status.InstalledNoticesSha256)
                && HashesEqual(status.PackageSbomSha256, status.InstalledSbomSha256)
                && HashesEqual(status.PackageOpenCodePresentationManifestSha256, status.InstalledOpenCodePresentationManifestSha256)
                && HashesEqual(status.PackageOpenCodePresentationPackageSha256, status.InstalledOpenCodePresentationPackageSha256)
                && HashesEqual(status.PackageOpenCodePresentationServerSha256, status.InstalledOpenCodePresentationServerSha256);
        }

        static bool RuntimeCliMatchesPackage(GatewayInstallStatus status)
        {
            return status.HasBundledCli
                && !string.IsNullOrEmpty(status.PackageCliSha256)
                && HashesEqual(status.PackageCliSha256, status.InstalledCliSha256);
        }

        static bool ValidateRuntimeMetadata(string runtimeRoot, GatewayRuntimeMetadata metadata, bool shapeValid, bool allowCheckoutLineEndings, out string message)
        {
            message = string.Empty;
            if (!shapeValid || metadata == null || metadata.schema_version != 3 || string.IsNullOrEmpty(metadata.version)
                || !string.Equals(metadata.entrypoint, "gateway-cli.mjs", StringComparison.Ordinal)
                || !string.Equals(metadata.config, "config/gateway.example.json", StringComparison.Ordinal)
                || !string.Equals(metadata.policy, "config/policy.example.json", StringComparison.Ordinal)
                || metadata.gateway_virtual_commands_count <= 0 || metadata.artifacts == null)
            {
                message = "gateway-runtime.json is not strict schema v3 metadata.";
                return false;
            }

            return ValidateArtifact(runtimeRoot, metadata.artifacts.gateway_cli, "gateway-cli.mjs", allowCheckoutLineEndings, out message)
                && ValidateArtifact(runtimeRoot, metadata.artifacts.legal_comments, "gateway-cli.LEGAL.txt", allowCheckoutLineEndings, out message)
                && ValidateArtifact(runtimeRoot, metadata.artifacts.third_party_notices, "THIRD_PARTY_NOTICES.md", allowCheckoutLineEndings, out message)
                && ValidateArtifact(runtimeRoot, metadata.artifacts.sbom, "sbom.cdx.json", allowCheckoutLineEndings, out message)
                && ValidateArtifact(runtimeRoot, metadata.artifacts.opencode_presentation_manifest, "opencode-presentation/sgg-plugin-manifest.json", allowCheckoutLineEndings, out message)
                && ValidateArtifact(runtimeRoot, metadata.artifacts.opencode_presentation_package, "opencode-presentation/package.json", allowCheckoutLineEndings, out message)
                && ValidateArtifact(runtimeRoot, metadata.artifacts.opencode_presentation_server, "opencode-presentation/server.js", allowCheckoutLineEndings, out message);
        }

        internal static bool ValidateRuntimeMetadataAtRoot(string runtimeRoot, bool allowCheckoutLineEndings, out string message)
        {
            var metadata = ReadRuntimeMetadata(Path.Combine(runtimeRoot, "gateway-runtime.json"), out var shapeValid);
            return ValidateRuntimeMetadata(runtimeRoot, metadata, shapeValid, allowCheckoutLineEndings, out message);
        }

        static bool ValidateArtifact(string runtimeRoot, GatewayRuntimeArtifact artifact, string expectedPath, bool allowCheckoutLineEndings, out string message)
        {
            message = string.Empty;
            if (artifact == null || !string.Equals(artifact.path, expectedPath, StringComparison.Ordinal)
                || !IsSha256(artifact.sha256))
            {
                message = expectedPath + " metadata is missing or malformed.";
                return false;
            }

            var artifactPath = Path.Combine(runtimeRoot, expectedPath);
            if (!File.Exists(artifactPath)
                || !(allowCheckoutLineEndings
                    ? TryReadCanonicalCheckoutArtifact(artifactPath, artifact.sha256, out _)
                    : HashesEqual(artifact.sha256, ComputeSha256(artifactPath))))
            {
                message = expectedPath + " is missing or does not match gateway-runtime.json.";
                return false;
            }
            return true;
        }

        internal static bool TryReadCanonicalCheckoutArtifact(string filePath, string expectedSha256, out byte[] canonicalBytes)
        {
            canonicalBytes = null;
            if (!IsSha256(expectedSha256) || string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return false;
            }
            if (HashesEqual(expectedSha256, ComputeSha256(filePath)))
            {
                return true;
            }

            // Git on Windows can leave CRLF in an existing local checkout even after .gitattributes
            // begins requiring LF. Accept only a *complete* CRLF conversion of the exact manifest-pinned bytes.
            if (new FileInfo(filePath).Length > MaxCheckoutArtifactBytes)
            {
                return false;
            }
            var bytes = File.ReadAllBytes(filePath);
            var normalized = new byte[bytes.Length];
            var length = 0;
            var converted = false;
            for (var index = 0; index < bytes.Length; index++)
            {
                if (bytes[index] == '\r')
                {
                    if (++index >= bytes.Length || bytes[index] != '\n')
                    {
                        return false;
                    }
                    normalized[length++] = (byte)'\n';
                    converted = true;
                }
                else if (bytes[index] == '\n')
                {
                    return false;
                }
                else
                {
                    normalized[length++] = bytes[index];
                }
            }
            if (!converted)
            {
                return false;
            }
            using (var sha256 = SHA256.Create())
            {
                var actual = BitConverter.ToString(sha256.ComputeHash(normalized, 0, length)).Replace("-", string.Empty).ToLowerInvariant();
                if (!HashesEqual(expectedSha256, actual))
                {
                    return false;
                }
            }
            canonicalBytes = new byte[length];
            Buffer.BlockCopy(normalized, 0, canonicalBytes, 0, length);
            return true;
        }

        static bool HashesEqual(string left, string right)
        {
            return !string.IsNullOrEmpty(left) && !string.IsNullOrEmpty(right)
                && string.Equals(left, right, StringComparison.Ordinal);
        }

        static bool IsSha256(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length != 64)
            {
                return false;
            }
            foreach (var character in value)
            {
                if (!((character >= '0' && character <= '9') || (character >= 'a' && character <= 'f')))
                {
                    return false;
                }
            }
            return true;
        }

        static bool FilesEqual(string leftPath, string rightPath)
        {
            if (string.IsNullOrEmpty(leftPath) || string.IsNullOrEmpty(rightPath)
                || !File.Exists(leftPath) || !File.Exists(rightPath))
            {
                return false;
            }

            var left = File.ReadAllBytes(leftPath);
            var right = File.ReadAllBytes(rightPath);
            if (left.Length != right.Length)
            {
                return false;
            }
            for (var index = 0; index < left.Length; index++)
            {
                if (left[index] != right[index])
                {
                    return false;
                }
            }
            return true;
        }

        static GatewayRuntimeMetadata ReadRuntimeMetadata(string metadataPath, out bool shapeValid)
        {
            shapeValid = false;
            if (string.IsNullOrEmpty(metadataPath) || !File.Exists(metadataPath))
            {
                return new GatewayRuntimeMetadata();
            }

            try
            {
                var json = File.ReadAllText(metadataPath);
                shapeValid = RuntimeManifestJsonShape.IsExactSchemaV3(json);
                return JsonUtility.FromJson<GatewayRuntimeMetadata>(json) ?? new GatewayRuntimeMetadata();
            }
            catch
            {
                return new GatewayRuntimeMetadata();
            }
        }

        sealed class RuntimeManifestJsonShape
        {
            static readonly string[] RootKeys = { "schema_version", "version", "entrypoint", "gateway_virtual_commands_count", "config", "policy", "artifacts" };
            static readonly string[] ArtifactKeys =
            {
                "gateway_cli", "legal_comments", "third_party_notices", "sbom",
                "opencode_presentation_manifest", "opencode_presentation_package", "opencode_presentation_server"
            };
            static readonly string[] FileKeys = { "path", "sha256" };

            readonly string m_json;
            int m_index;

            RuntimeManifestJsonShape(string json)
            {
                m_json = json ?? string.Empty;
            }

            public static bool IsExactSchemaV3(string json)
            {
                try
                {
                    var reader = new RuntimeManifestJsonShape(json);
                    var root = reader.ReadObject();
                    reader.SkipWhitespace();
                    return reader.m_index == reader.m_json.Length
                        && HasExactKeys(root, RootKeys)
                        && root.Objects.TryGetValue("artifacts", out var artifacts)
                        && HasExactKeys(artifacts, ArtifactKeys)
                        && HasExactFileObject(artifacts, "gateway_cli")
                        && HasExactFileObject(artifacts, "legal_comments")
                        && HasExactFileObject(artifacts, "third_party_notices")
                        && HasExactFileObject(artifacts, "sbom")
                        && HasExactFileObject(artifacts, "opencode_presentation_manifest")
                        && HasExactFileObject(artifacts, "opencode_presentation_package")
                        && HasExactFileObject(artifacts, "opencode_presentation_server");
                }
                catch
                {
                    return false;
                }
            }

            static bool HasExactFileObject(JsonObjectShape parent, string key)
            {
                return parent.Objects.TryGetValue(key, out var value) && HasExactKeys(value, FileKeys);
            }

            static bool HasExactKeys(JsonObjectShape value, string[] expected)
            {
                if (value.Keys.Count != expected.Length)
                {
                    return false;
                }
                var actual = new HashSet<string>(value.Keys, StringComparer.Ordinal);
                return actual.Count == expected.Length && Array.TrueForAll(expected, actual.Contains);
            }

            JsonObjectShape ReadObject()
            {
                SkipWhitespace();
                Require('{');
                var result = new JsonObjectShape();
                SkipWhitespace();
                if (TryConsume('}'))
                {
                    return result;
                }
                while (true)
                {
                    SkipWhitespace();
                    var key = ReadString();
                    result.Keys.Add(key);
                    SkipWhitespace();
                    Require(':');
                    SkipWhitespace();
                    if (Peek() == '{')
                    {
                        var child = ReadObject();
                        if (!result.Objects.ContainsKey(key))
                        {
                            result.Objects.Add(key, child);
                        }
                    }
                    else
                    {
                        ReadValue();
                    }
                    SkipWhitespace();
                    if (TryConsume('}'))
                    {
                        return result;
                    }
                    Require(',');
                }
            }

            void ReadValue()
            {
                var character = Peek();
                if (character == '"')
                {
                    ReadString();
                    return;
                }
                if (character == '[')
                {
                    ReadArray();
                    return;
                }
                if (character == '{')
                {
                    ReadObject();
                    return;
                }
                var start = m_index;
                while (m_index < m_json.Length && !char.IsWhiteSpace(m_json[m_index]) && m_json[m_index] != ',' && m_json[m_index] != '}' && m_json[m_index] != ']')
                {
                    m_index++;
                }
                if (m_index == start)
                {
                    throw new FormatException("Invalid JSON value.");
                }
            }

            void ReadArray()
            {
                Require('[');
                SkipWhitespace();
                if (TryConsume(']'))
                {
                    return;
                }
                while (true)
                {
                    SkipWhitespace();
                    ReadValue();
                    SkipWhitespace();
                    if (TryConsume(']'))
                    {
                        return;
                    }
                    Require(',');
                }
            }

            string ReadString()
            {
                Require('"');
                var value = new StringBuilder();
                while (m_index < m_json.Length)
                {
                    var character = m_json[m_index++];
                    if (character == '"')
                    {
                        return value.ToString();
                    }
                    if (character < 0x20)
                    {
                        throw new FormatException("Invalid JSON string.");
                    }
                    if (character != '\\')
                    {
                        value.Append(character);
                        continue;
                    }
                    if (m_index >= m_json.Length)
                    {
                        throw new FormatException("Invalid JSON escape.");
                    }
                    var escaped = m_json[m_index++];
                    switch (escaped)
                    {
                        case '"': value.Append('"'); break;
                        case '\\': value.Append('\\'); break;
                        case '/': value.Append('/'); break;
                        case 'b': value.Append('\b'); break;
                        case 'f': value.Append('\f'); break;
                        case 'n': value.Append('\n'); break;
                        case 'r': value.Append('\r'); break;
                        case 't': value.Append('\t'); break;
                        case 'u': value.Append(ReadUnicodeEscape()); break;
                        default: throw new FormatException("Invalid JSON escape.");
                    }
                }
                throw new FormatException("Unterminated JSON string.");
            }

            char ReadUnicodeEscape()
            {
                if (m_index + 4 > m_json.Length)
                {
                    throw new FormatException("Invalid JSON unicode escape.");
                }
                var value = 0;
                for (var offset = 0; offset < 4; offset++)
                {
                    var character = m_json[m_index++];
                    value <<= 4;
                    if (character >= '0' && character <= '9') value += character - '0';
                    else if (character >= 'a' && character <= 'f') value += character - 'a' + 10;
                    else if (character >= 'A' && character <= 'F') value += character - 'A' + 10;
                    else throw new FormatException("Invalid JSON unicode escape.");
                }
                return (char)value;
            }

            void SkipWhitespace()
            {
                while (m_index < m_json.Length && char.IsWhiteSpace(m_json[m_index])) m_index++;
            }

            char Peek()
            {
                if (m_index >= m_json.Length) throw new FormatException("Unexpected end of JSON.");
                return m_json[m_index];
            }

            bool TryConsume(char expected)
            {
                if (m_index < m_json.Length && m_json[m_index] == expected)
                {
                    m_index++;
                    return true;
                }
                return false;
            }

            void Require(char expected)
            {
                if (!TryConsume(expected)) throw new FormatException("Invalid JSON structure.");
            }

            sealed class JsonObjectShape
            {
                public readonly List<string> Keys = new List<string>();
                public readonly Dictionary<string, JsonObjectShape> Objects = new Dictionary<string, JsonObjectShape>(StringComparer.Ordinal);
            }
        }

        static string ComputeSha256(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                return string.Empty;
            }

            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        [Serializable]
        sealed class GatewayRuntimeMetadata
        {
            public int schema_version;
            public string version = string.Empty;
            public string entrypoint = string.Empty;
            public int gateway_virtual_commands_count;
            public string config = string.Empty;
            public string policy = string.Empty;
            public GatewayRuntimeArtifacts artifacts = new GatewayRuntimeArtifacts();
        }

        [Serializable]
        sealed class GatewayRuntimeArtifacts
        {
            public GatewayRuntimeArtifact gateway_cli = new GatewayRuntimeArtifact();
            public GatewayRuntimeArtifact legal_comments = new GatewayRuntimeArtifact();
            public GatewayRuntimeArtifact third_party_notices = new GatewayRuntimeArtifact();
            public GatewayRuntimeArtifact sbom = new GatewayRuntimeArtifact();
            public GatewayRuntimeArtifact opencode_presentation_manifest = new GatewayRuntimeArtifact();
            public GatewayRuntimeArtifact opencode_presentation_package = new GatewayRuntimeArtifact();
            public GatewayRuntimeArtifact opencode_presentation_server = new GatewayRuntimeArtifact();
        }

        [Serializable]
        sealed class GatewayRuntimeArtifact
        {
            public string path = string.Empty;
            public string sha256 = string.Empty;
        }

        [Serializable]
        sealed class GatewayRuntimePointer
        {
            public string runtime_root = string.Empty;
            public string version = string.Empty;
            public string gateway_cli_sha256 = string.Empty;
        }

        internal sealed class GatewayRuntimeInstallConfirmation
        {
            readonly string m_runtimeRoot;
            readonly string m_packageRuntimeRoot;
            readonly string m_installedVersion;
            readonly string m_packageVersion;
            readonly int m_installedCommandCount;
            readonly int m_packageCommandCount;
            readonly string m_installedCliSha256;
            readonly string m_packageCliSha256;
            readonly string m_installedLegalSha256;
            readonly string m_packageLegalSha256;
            readonly string m_installedNoticesSha256;
            readonly string m_packageNoticesSha256;
            readonly string m_installedSbomSha256;
            readonly string m_packageSbomSha256;
            readonly string m_installedOpenCodePresentationManifestSha256;
            readonly string m_packageOpenCodePresentationManifestSha256;
            readonly string m_installedOpenCodePresentationPackageSha256;
            readonly string m_packageOpenCodePresentationPackageSha256;
            readonly string m_installedOpenCodePresentationServerSha256;
            readonly string m_packageOpenCodePresentationServerSha256;

            internal GatewayRuntimeInstallConfirmation(GatewayInstallStatus status)
            {
                if (status == null) throw new ArgumentNullException(nameof(status));
                m_runtimeRoot = status.RuntimeRoot;
                m_packageRuntimeRoot = status.PackageRuntimeRoot;
                m_installedVersion = status.InstalledRuntimeVersion;
                m_packageVersion = status.PackageRuntimeVersion;
                m_installedCommandCount = status.InstalledGatewayVirtualCommandCount;
                m_packageCommandCount = status.PackageGatewayVirtualCommandCount;
                m_installedCliSha256 = status.InstalledCliSha256;
                m_packageCliSha256 = status.PackageCliSha256;
                m_installedLegalSha256 = status.InstalledLegalSha256;
                m_packageLegalSha256 = status.PackageLegalSha256;
                m_installedNoticesSha256 = status.InstalledNoticesSha256;
                m_packageNoticesSha256 = status.PackageNoticesSha256;
                m_installedSbomSha256 = status.InstalledSbomSha256;
                m_packageSbomSha256 = status.PackageSbomSha256;
                m_installedOpenCodePresentationManifestSha256 = status.InstalledOpenCodePresentationManifestSha256;
                m_packageOpenCodePresentationManifestSha256 = status.PackageOpenCodePresentationManifestSha256;
                m_installedOpenCodePresentationPackageSha256 = status.InstalledOpenCodePresentationPackageSha256;
                m_packageOpenCodePresentationPackageSha256 = status.PackageOpenCodePresentationPackageSha256;
                m_installedOpenCodePresentationServerSha256 = status.InstalledOpenCodePresentationServerSha256;
                m_packageOpenCodePresentationServerSha256 = status.PackageOpenCodePresentationServerSha256;
            }

            internal bool Matches(GatewayInstallStatus status)
            {
                return status != null
                    && status.HasBundledCli
                    && status.PackageMetadataValid
                    && status.InstalledMetadataValid
                    && string.Equals(status.RuntimeRoot, m_runtimeRoot, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(status.PackageRuntimeRoot, m_packageRuntimeRoot, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(status.InstalledRuntimeVersion, m_installedVersion, StringComparison.Ordinal)
                    && string.Equals(status.PackageRuntimeVersion, m_packageVersion, StringComparison.Ordinal)
                    && status.InstalledGatewayVirtualCommandCount == m_installedCommandCount
                    && status.PackageGatewayVirtualCommandCount == m_packageCommandCount
                    && HashesEqual(status.InstalledCliSha256, m_installedCliSha256)
                    && HashesEqual(status.PackageCliSha256, m_packageCliSha256)
                    && HashesEqual(status.InstalledLegalSha256, m_installedLegalSha256)
                    && HashesEqual(status.PackageLegalSha256, m_packageLegalSha256)
                    && HashesEqual(status.InstalledNoticesSha256, m_installedNoticesSha256)
                    && HashesEqual(status.PackageNoticesSha256, m_packageNoticesSha256)
                    && HashesEqual(status.InstalledSbomSha256, m_installedSbomSha256)
                    && HashesEqual(status.PackageSbomSha256, m_packageSbomSha256)
                    && HashesEqual(status.InstalledOpenCodePresentationManifestSha256, m_installedOpenCodePresentationManifestSha256)
                    && HashesEqual(status.PackageOpenCodePresentationManifestSha256, m_packageOpenCodePresentationManifestSha256)
                    && HashesEqual(status.InstalledOpenCodePresentationPackageSha256, m_installedOpenCodePresentationPackageSha256)
                    && HashesEqual(status.PackageOpenCodePresentationPackageSha256, m_packageOpenCodePresentationPackageSha256)
                    && HashesEqual(status.InstalledOpenCodePresentationServerSha256, m_installedOpenCodePresentationServerSha256)
                    && HashesEqual(status.PackageOpenCodePresentationServerSha256, m_packageOpenCodePresentationServerSha256);
            }
        }

        public sealed class GatewayInstallStatus
        {
            public bool LocalRepoFound;
            public bool HasBundledCli;
            public bool CanStart;
            public string RepoRoot = string.Empty;
            public string PackageRuntimeRoot = string.Empty;
            public string PackageRuntimeCliPath = string.Empty;
            public string PackageRuntimeMetadataPath = string.Empty;
            public string PackageRuntimeVersion = string.Empty;
            public string PackageCliSha256 = string.Empty;
            public string PackageLegalSha256 = string.Empty;
            public string PackageNoticesSha256 = string.Empty;
            public string PackageSbomSha256 = string.Empty;
            public string PackageOpenCodePresentationManifestSha256 = string.Empty;
            public string PackageOpenCodePresentationPackageSha256 = string.Empty;
            public string PackageOpenCodePresentationServerSha256 = string.Empty;
            public bool PackageMetadataValid;
            public int PackageGatewayVirtualCommandCount;
            public string RuntimeRoot = string.Empty;
            public string BundledCliPath = string.Empty;
            public string RuntimeMetadataPath = string.Empty;
            public string InstalledRuntimeVersion = string.Empty;
            public string InstalledCliSha256 = string.Empty;
            public string InstalledLegalSha256 = string.Empty;
            public string InstalledNoticesSha256 = string.Empty;
            public string InstalledSbomSha256 = string.Empty;
            public string InstalledOpenCodePresentationManifestSha256 = string.Empty;
            public string InstalledOpenCodePresentationPackageSha256 = string.Empty;
            public string InstalledOpenCodePresentationServerSha256 = string.Empty;
            public bool InstalledMetadataValid;
            public int InstalledGatewayVirtualCommandCount;
            public string ExampleConfigPath = string.Empty;
            public string ExamplePolicyPath = string.Empty;
            public string ConfigPath = string.Empty;
            public string PolicyPath = string.Empty;
            public bool UserConfigReady;
            public bool RuntimeFresh;
            public string Message = string.Empty;
        }
    }
}
