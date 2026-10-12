using System;
using System.IO;
using SGG.UnityMCP.Editor.Bridge;
using UnityEditor.PackageManager;
using UnityEngine;

namespace SGG.UnityMCP.Editor.Setup
{
    public static class UnityMcpPackagePaths
    {
        public static string PackageAssetPath
        {
            get
            {
                var packageInfo = PackageInfo.FindForAssembly(typeof(UnityBridgeRuntime).Assembly);
                return string.IsNullOrEmpty(packageInfo?.assetPath) ? "Packages/com.sungeargames.unity-mcp" : packageInfo.assetPath.Replace('\\', '/');
            }
        }

        public static string PackageResolvedPath
        {
            get
            {
                var packageInfo = PackageInfo.FindForAssembly(typeof(UnityBridgeRuntime).Assembly);
                return string.IsNullOrEmpty(packageInfo?.resolvedPath) ? string.Empty : packageInfo.resolvedPath;
            }
        }

        public static string LocalAppDataRoot
        {
            get
            {
                var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                if (string.IsNullOrEmpty(localAppData))
                {
                    localAppData = Application.temporaryCachePath;
                }

                return Path.Combine(localAppData, "SGG", "UnityMcpGateway");
            }
        }

        public static string UserConfigDirectory => Path.Combine(LocalAppDataRoot, "config");

        public static string UserGatewayConfigPath => Path.Combine(UserConfigDirectory, "gateway.json");

        public static string UserPolicyConfigPath => Path.Combine(UserConfigDirectory, "policy.json");

        public static string FindGatewayRepoRoot()
        {
            var packagePath = PackageResolvedPath;
            if (string.IsNullOrEmpty(packagePath))
            {
                return string.Empty;
            }

            var directory = new DirectoryInfo(packagePath);
            for (var depth = 0; depth < 8 && directory != null; depth++)
            {
                if (File.Exists(Path.Combine(directory.FullName, "src", "cli.ts")) && File.Exists(Path.Combine(directory.FullName, "package.json")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            return string.Empty;
        }
    }
}
