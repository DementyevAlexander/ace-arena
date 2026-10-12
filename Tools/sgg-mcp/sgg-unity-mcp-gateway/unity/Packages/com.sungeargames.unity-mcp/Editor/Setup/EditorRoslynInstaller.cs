using System;
using System.IO;
using System.IO.Compression;
using SGG.UnityMCP.Editor.Commands;
using UnityEditor;
using UnityEngine;
using UnityEngine.Networking;

namespace SGG.UnityMCP.Editor.Setup
{
    public static class EditorRoslynInstaller
    {
        const string PluginsRelativePath = "Plugins/SGGUnityMcpRoslyn";

        static readonly NuGetDll[] Packages =
        {
            new NuGetDll("microsoft.codeanalysis.common", "4.12.0", "lib/netstandard2.0/Microsoft.CodeAnalysis.dll", "Microsoft.CodeAnalysis.dll"),
            new NuGetDll("microsoft.codeanalysis.csharp", "4.12.0", "lib/netstandard2.0/Microsoft.CodeAnalysis.CSharp.dll", "Microsoft.CodeAnalysis.CSharp.dll"),
            new NuGetDll("system.collections.immutable", "8.0.0", "lib/netstandard2.0/System.Collections.Immutable.dll", "System.Collections.Immutable.dll"),
            new NuGetDll("system.reflection.metadata", "8.0.0", "lib/netstandard2.0/System.Reflection.Metadata.dll", "System.Reflection.Metadata.dll"),
            new NuGetDll("system.runtime.compilerservices.unsafe", "6.0.0", "lib/netstandard2.0/System.Runtime.CompilerServices.Unsafe.dll", "System.Runtime.CompilerServices.Unsafe.dll"),
        };

        public static string PluginsFolder => Path.Combine(Application.dataPath, PluginsRelativePath);
        public static string ProjectRelativeFolder => "Assets/" + PluginsRelativePath;

        public static bool IsInstalled()
        {
            for (var index = 0; index < Packages.Length; index++)
            {
                if (!File.Exists(Path.Combine(PluginsFolder, Packages[index].DllName)))
                {
                    return false;
                }
            }

            return true;
        }

        public static bool IsCompilerLoaded()
        {
            return Type.GetType("Microsoft.CodeAnalysis.CSharp.CSharpCompilation, Microsoft.CodeAnalysis.CSharp") != null;
        }

        [MenuItem("SGG/Unity MCP/Install Roslyn Compiler")]
        public static void InstallFromMenu()
        {
            var result = Install(true);
            if (!result.Success)
            {
                EditorUtility.DisplayDialog("Roslyn Install Failed", result.Message, "OK");
            }
        }

        [MenuItem("SGG/Unity MCP/Uninstall Roslyn Compiler")]
        public static void UninstallFromMenu()
        {
            if (!EditorUtility.DisplayDialog("Uninstall Roslyn Compiler", "Remove Roslyn DLLs from " + ProjectRelativeFolder + "?", "Uninstall", "Cancel"))
            {
                return;
            }

            var result = Uninstall();
            EditorUtility.DisplayDialog(result.Success ? "Roslyn Removed" : "Roslyn Remove Failed", result.Message, "OK");
        }

        public static InstallResult Install(bool interactive)
        {
            if (interactive && IsInstalled())
            {
                if (!EditorUtility.DisplayDialog("Roslyn Already Installed", "Roslyn DLLs are already present in " + ProjectRelativeFolder + ". Reinstall?", "Reinstall", "Cancel"))
                {
                    return InstallResult.Ok("Roslyn install cancelled");
                }
            }

            try
            {
                Directory.CreateDirectory(PluginsFolder);
                for (var index = 0; index < Packages.Length; index++)
                {
                    var package = Packages[index];
                    if (interactive)
                    {
                        EditorUtility.DisplayProgressBar("Installing Roslyn", "Downloading " + package.Id + " " + package.Version, (float)index / Packages.Length);
                    }

                    var bytes = DownloadPackage(package);
                    var dllBytes = ExtractFile(bytes, package.PathInPackage);
                    if (dllBytes == null || dllBytes.Length == 0)
                    {
                        return InstallResult.Fail("Package " + package.Id + " did not contain " + package.PathInPackage);
                    }

                    File.WriteAllBytes(Path.Combine(PluginsFolder, package.DllName), dllBytes);
                }

                if (interactive)
                {
                    EditorUtility.DisplayProgressBar("Installing Roslyn", "Refreshing AssetDatabase", 0.95f);
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EditorCSharpExecutionCommands.ResetCompilerCache();

                if (interactive)
                {
                    EditorUtility.ClearProgressBar();
                    EditorUtility.DisplayDialog("Roslyn Installed", "Roslyn DLLs were installed to " + ProjectRelativeFolder + ". Unity may need a domain reload before dynamic C# commands can use them.", "OK");
                }

                return InstallResult.Ok("Roslyn DLLs installed to " + ProjectRelativeFolder);
            }
            catch (Exception exception)
            {
                if (interactive)
                {
                    EditorUtility.ClearProgressBar();
                }

                return InstallResult.Fail(exception.Message);
            }
        }

        public static InstallResult Uninstall()
        {
            try
            {
                if (Directory.Exists(PluginsFolder))
                {
                    Directory.Delete(PluginsFolder, true);
                    var metaPath = PluginsFolder + ".meta";
                    if (File.Exists(metaPath))
                    {
                        File.Delete(metaPath);
                    }
                }

                AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
                EditorCSharpExecutionCommands.ResetCompilerCache();
                return InstallResult.Ok("Roslyn DLLs removed from " + ProjectRelativeFolder);
            }
            catch (Exception exception)
            {
                return InstallResult.Fail(exception.Message);
            }
        }

        static byte[] DownloadPackage(NuGetDll package)
        {
            var url = "https://api.nuget.org/v3-flatcontainer/" + package.Id + "/" + package.Version + "/" + package.Id + "." + package.Version + ".nupkg";
            using var request = UnityWebRequest.Get(url);
            request.timeout = 30;
            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                System.Threading.Thread.Sleep(50);
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new InvalidOperationException("Download failed for " + package.Id + ": " + request.error);
            }

            return request.downloadHandler.data;
        }

        static byte[] ExtractFile(byte[] zipBytes, string entryPath)
        {
            var normalizedEntryPath = entryPath.Replace('\\', '/');
            using var stream = new MemoryStream(zipBytes);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var entry in archive.Entries)
            {
                if (!string.Equals(entry.FullName.Replace('\\', '/'), normalizedEntryPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var input = entry.Open();
                using var output = new MemoryStream();
                input.CopyTo(output);
                return output.ToArray();
            }

            return null;
        }

        sealed class NuGetDll
        {
            public readonly string Id;
            public readonly string Version;
            public readonly string PathInPackage;
            public readonly string DllName;

            public NuGetDll(string id, string version, string pathInPackage, string dllName)
            {
                Id = id;
                Version = version;
                PathInPackage = pathInPackage;
                DllName = dllName;
            }
        }

        public sealed class InstallResult
        {
            public bool Success;
            public string Message;

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
