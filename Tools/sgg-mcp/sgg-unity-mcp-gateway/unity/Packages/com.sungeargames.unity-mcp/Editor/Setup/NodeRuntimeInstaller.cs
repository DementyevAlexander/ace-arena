using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace SGG.UnityMCP.Editor.Setup
{
    public static class NodeRuntimeInstaller
    {
        const string NodeVersion = "22.16.0";
        const string NodeFolderName = "node-v" + NodeVersion + "-win-x64";
        const string ArchiveName = NodeFolderName + ".zip";
        const string DownloadBaseUrl = "https://nodejs.org/dist/v" + NodeVersion + "/";

        public static string PortableNodeFolder => Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "runtime", NodeFolderName);
        public static string PortableNodePath => Path.Combine(PortableNodeFolder, "node.exe");
        public static string PortableNpmPath => Path.Combine(PortableNodeFolder, "npm.cmd");

        public static NodeRuntimeStatus GetStatus()
        {
            return GetStatus(Application.platform == RuntimePlatform.WindowsEditor);
        }

        internal static NodeRuntimeStatus GetStatus(bool windowsEditor)
        {
            var status = new NodeRuntimeStatus
            {
                RequiredVersion = NodeVersion,
                PortablePath = PortableNodePath,
                PortableNpmPath = PortableNpmPath,
            };

            if (!windowsEditor)
            {
                status.Message = "Portable Node installer is currently implemented for Windows Editor.";
                return status;
            }

            if (ValidateNode(PortableNodePath, out var portableVersion, out var portableMessage))
            {
                status.IsAvailable = true;
                status.IsPortable = true;
                status.NodePath = PortableNodePath;
                status.NpmPath = File.Exists(PortableNpmPath) ? PortableNpmPath : string.Empty;
                status.Version = portableVersion;
                status.Message = "Portable Node is installed.";
                return status;
            }

            status.PortableError = portableMessage;

            if (ValidateNode("node.exe", out var globalVersion, out var globalMessage))
            {
                status.IsAvailable = true;
                status.IsPortable = false;
                status.NodePath = "node.exe";
                status.Version = globalVersion;
                status.Message = "Global Node is available. Portable Node is still recommended.";
                return status;
            }

            status.Message = string.IsNullOrEmpty(globalMessage) ? "Node 22+ is not available." : globalMessage;
            return status;
        }

        public static InstallResult InstallPortableNode(bool interactive)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
            {
                return InstallResult.Fail("Portable Node installer currently supports Windows Editor only.");
            }

            try
            {
                if (File.Exists(PortableNodePath) && ValidateNode(PortableNodePath, out _, out _))
                {
                    return InstallResult.Ok("Portable Node is already installed at " + PortableNodePath);
                }

                var runtimeRoot = Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "runtime");
                var downloadRoot = Path.Combine(runtimeRoot, "downloads");
                Directory.CreateDirectory(downloadRoot);

                var archivePath = Path.Combine(downloadRoot, ArchiveName);
                var archiveUrl = DownloadBaseUrl + ArchiveName;
                var sumsUrl = DownloadBaseUrl + "SHASUMS256.txt";

                DownloadFile(archiveUrl, archivePath, interactive, "Downloading Portable Node");
                var expectedSha256 = DownloadExpectedSha256(sumsUrl, ArchiveName, interactive);
                var actualSha256 = ComputeSha256(archivePath);
                if (!string.Equals(expectedSha256, actualSha256, StringComparison.OrdinalIgnoreCase))
                {
                    return InstallResult.Fail("Node archive checksum mismatch. Expected " + expectedSha256 + ", got " + actualSha256 + ".");
                }

                if (Directory.Exists(PortableNodeFolder))
                {
                    Directory.Delete(PortableNodeFolder, true);
                }

                ExtractZip(archivePath, runtimeRoot, interactive);
                if (!ValidateNode(PortableNodePath, out var version, out var validationMessage))
                {
                    return InstallResult.Fail("Portable Node was extracted but validation failed: " + validationMessage);
                }

                return InstallResult.Ok("Portable Node " + version + " installed at " + PortableNodePath);
            }
            catch (Exception exception)
            {
                return InstallResult.Fail(exception.Message);
            }
            finally
            {
                if (interactive)
                {
                    EditorUtility.ClearProgressBar();
                }
            }
        }

        public static bool ValidateNode(string nodePath, out string version, out string message)
        {
            version = string.Empty;
            message = string.Empty;

            if (string.IsNullOrEmpty(nodePath))
            {
                message = "Node path is empty.";
                return false;
            }

            if (Path.IsPathRooted(nodePath) && !File.Exists(nodePath))
            {
                message = "Node executable not found: " + nodePath;
                return false;
            }

            if (!ProcessUtil.TryRun(nodePath, "--version", null, out var stdout, out var stderr, 8000))
            {
                message = string.IsNullOrWhiteSpace(stderr) ? "Could not execute node --version." : stderr.Trim();
                return false;
            }

            var output = string.IsNullOrWhiteSpace(stdout) ? stderr.Trim() : stdout.Trim();
            if (output.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            {
                output = output.Substring(1);
            }

            version = output;
            if (!Version.TryParse(version.Split('-')[0], out var parsed))
            {
                message = "Could not parse Node version: " + output;
                return false;
            }

            if (parsed.Major < 22)
            {
                message = "Node " + version + " is too old. Node 22+ is required.";
                return false;
            }

            return true;
        }

        static void DownloadFile(string url, string outputPath, bool interactive, string title)
        {
            using (var request = UnityWebRequest.Get(url))
            {
                request.timeout = 120;
                var operation = request.SendWebRequest();
                while (!operation.isDone)
                {
                    if (interactive)
                    {
                        EditorUtility.DisplayProgressBar(title, url, Mathf.Clamp01(operation.progress));
                    }

                    System.Threading.Thread.Sleep(50);
                }

                if (request.result != UnityWebRequest.Result.Success)
                {
                    throw new InvalidOperationException("Download failed: " + request.error);
                }

                File.WriteAllBytes(outputPath, request.downloadHandler.data);
            }
        }

        static string DownloadExpectedSha256(string url, string archiveName, bool interactive)
        {
            var tempPath = Path.Combine(UnityMcpPackagePaths.LocalAppDataRoot, "runtime", "downloads", "SHASUMS256.txt");
            DownloadFile(url, tempPath, interactive, "Downloading Node checksums");
            foreach (var line in File.ReadAllLines(tempPath))
            {
                var trimmed = line.Trim();
                if (!trimmed.EndsWith(" " + archiveName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var parts = trimmed.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > 0)
                {
                    return parts[0];
                }
            }

            throw new InvalidOperationException("Could not find checksum for " + archiveName);
        }

        static string ComputeSha256(string filePath)
        {
            using (var sha256 = SHA256.Create())
            using (var stream = File.OpenRead(filePath))
            {
                var hash = sha256.ComputeHash(stream);
                return BitConverter.ToString(hash).Replace("-", string.Empty).ToLowerInvariant();
            }
        }

        static void ExtractZip(string archivePath, string destinationRoot, bool interactive)
        {
            using (var archiveStream = File.OpenRead(archivePath))
            using (var archive = new ZipArchive(archiveStream, ZipArchiveMode.Read))
            {
                for (var index = 0; index < archive.Entries.Count; index++)
                {
                    var entry = archive.Entries[index];
                    if (interactive)
                    {
                        EditorUtility.DisplayProgressBar("Extracting Portable Node", entry.FullName, (float)index / Math.Max(1, archive.Entries.Count));
                    }

                    var destinationPath = Path.GetFullPath(Path.Combine(destinationRoot, entry.FullName));
                    var rootPath = Path.GetFullPath(destinationRoot);
                    if (!destinationPath.StartsWith(rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException("Archive entry escapes destination: " + entry.FullName);
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(destinationPath);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(destinationPath));
                    using (var input = entry.Open())
                    using (var output = File.Open(destinationPath, FileMode.Create, FileAccess.Write))
                    {
                        input.CopyTo(output);
                    }
                }
            }
        }

        public sealed class NodeRuntimeStatus
        {
            public bool IsAvailable;
            public bool IsPortable;
            public string Version = string.Empty;
            public string RequiredVersion = string.Empty;
            public string NodePath = string.Empty;
            public string NpmPath = string.Empty;
            public string PortablePath = string.Empty;
            public string PortableNpmPath = string.Empty;
            public string PortableError = string.Empty;
            public string Message = string.Empty;
        }

        public sealed class InstallResult
        {
            public bool Success;
            public string Message = string.Empty;

            public static InstallResult Ok(string message)
            {
                return new InstallResult { Success = true, Message = message };
            }

            public static InstallResult Fail(string message)
            {
                return new InstallResult { Success = false, Message = message };
            }
        }
    }
}
